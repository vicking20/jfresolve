using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Api;

/// <summary>Resolves streams from the Stremio addon and proxies them to Jellyfin.</summary>
[ApiController]
[Route("Plugins/Jfresolve")]
[Route("Plugins/506f18b85dad4cd3b9a0f7ed933e9939")] // Alternative route using plugin GUID for image requests
public class JfresolveApiController : ControllerBase
{
    private readonly ILogger<JfresolveApiController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // Failover cache: tracks recent playback attempts with time windows
    private static readonly ConcurrentDictionary<string, FailoverState> _failoverCache = new();

    public JfresolveApiController(
        ILogger<JfresolveApiController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    private class FailoverState
    {
        public int CurrentIndex { get; set; }
        public DateTime FirstAttempt { get; set; }
        public DateTime LastAttempt { get; set; }
        public int AttemptCount { get; set; }
    }

    /// <summary>Looks up streams for the item on the addon, picks one and proxies it.</summary>
    [HttpGet("resolve/{type}/{id}")]
    [AllowAnonymous] // FFmpeg needs to access this endpoint without authentication
    public async Task<IActionResult> ResolveStream(
        string type,
        string id,
        [FromQuery] string? season = null,
        [FromQuery] string? episode = null,
        [FromQuery] string? quality = null,
        [FromQuery] int? index = null)
    {
        _logger.LogInformation(
            "Jfresolve: ResolveStream called - Type: {Type}, Id: {Id}, Season: {Season}, Episode: {Episode}, Quality: {Quality}, Index: {Index}, RequestPath: {Path}, Range: {Range}",
            type, id, season ?? "N/A", episode ?? "N/A", quality ?? "N/A", index?.ToString() ?? "N/A",
            Request.Path, Request.Headers["Range"].ToString()
        );

        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogError("Jfresolve: Plugin configuration is null");
            return BadRequest("Plugin not initialized");
        }

        _logger.LogInformation(
            "Jfresolve: Resolving stream for {Type}/{Id} (Season: {Season}, Episode: {Episode})",
            type, id, season ?? "N/A", episode ?? "N/A"
        );

        if (string.IsNullOrWhiteSpace(config.AddonManifestUrl))
        {
            _logger.LogError("Jfresolve: Addon manifest URL not configured - cannot resolve stream");
            return NotFound("Addon manifest URL not configured. Please configure it in plugin settings.");
        }

        try
        {
            var manifestBase = UrlBuilder.NormalizeManifestUrl(config.AddonManifestUrl);

            string streamUrl;
            if (type.Equals("movie", StringComparison.OrdinalIgnoreCase))
            {
                streamUrl = $"{manifestBase}/stream/movie/{id}.json";
            }
            else if (type.Equals("series", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(season) || string.IsNullOrWhiteSpace(episode))
                {
                    return BadRequest("Season and episode parameters are required for series type");
                }
                streamUrl = $"{manifestBase}/stream/series/{id}:{season}:{episode}.json";
            }
            else
            {
                streamUrl = $"{manifestBase}/stream/{type}/{id}.json";
            }

            _logger.LogInformation("Jfresolve: Requesting stream from addon: {StreamUrl}", streamUrl);

            var addonHttpClient = _httpClientFactory.CreateClient();
            addonHttpClient.Timeout = TimeSpan.FromSeconds(30); // Set timeout to prevent hanging
            addonHttpClient.DefaultRequestHeaders.Add("User-Agent", "Jfresolve/1.0");
            using var addonResponse = await addonHttpClient.GetAsync(streamUrl);
            if (!addonResponse.IsSuccessStatusCode)
            {
                // Usually Cloudflare in front of the addon blocking the server's IP
                var body = await addonResponse.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Jfresolve: Addon returned {Status} for {StreamUrl}. Response: {Body}",
                    (int)addonResponse.StatusCode, streamUrl, body.Length > 300 ? body[..300] : body);
                return StatusCode(502, $"Addon returned {(int)addonResponse.StatusCode}");
            }
            var response = await addonResponse.Content.ReadAsStringAsync();

            using var json = JsonDocument.Parse(response);
            if (!json.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            {
                _logger.LogWarning("Jfresolve: No streams found for {Type}/{Id}", type, id);
                return NotFound($"No streams found for {id}");
            }

            var cacheKey = BuildFailoverCacheKey(type, id, season, episode, quality);
            int effectiveIndex = DetermineFailoverIndex(cacheKey, index, quality, streams, config.PreferredQuality, type);

            var selectedStream = SelectStreamByQuality(streams, config.PreferredQuality, quality, effectiveIndex);
            if (selectedStream == null)
            {
                _logger.LogWarning("Jfresolve: Could not select a stream for {Type}/{Id}", type, id);
                return NotFound("No suitable stream found");
            }

            if (!selectedStream.Value.TryGetProperty("url", out var urlProperty))
            {
                _logger.LogWarning("Jfresolve: No URL property in stream response");
                return NotFound("No stream URL available in response");
            }

            var redirectUrl = urlProperty.GetString();
            if (string.IsNullOrWhiteSpace(redirectUrl))
            {
                _logger.LogWarning("Jfresolve: Empty stream URL received");
                return NotFound("Empty stream URL received");
            }

            _logger.LogInformation("Jfresolve: Resolved {Type}/{Id} to {RedirectUrl}", type, id, redirectUrl);

            if (!Uri.IsWellFormedUriString(redirectUrl, UriKind.Absolute))
            {
                _logger.LogWarning("Jfresolve: Redirect URL is not absolute: {RedirectUrl}", redirectUrl);
                return BadRequest("Invalid redirect URL format");
            }

            // Proxied rather than redirected: FFmpeg in 10.11.6 doesn't follow redirects from plugin endpoints
            // FFmpeg needs Range (206) support for seeking
            try
            {
                _logger.LogInformation("Jfresolve: Proxying stream from {RedirectUrl}", redirectUrl);
                await ProxyStreamAsync(redirectUrl, HttpContext.RequestAborted);
                return new EmptyResult();
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Client went away (seek or stop)
                _logger.LogDebug("Jfresolve: Client closed stream for {Type}/{Id}", type, id);
                return new EmptyResult();
            }
            catch (Exception ex) when (Response.HasStarted)
            {
                // Status code can't change once bytes are sent
                _logger.LogWarning(ex, "Jfresolve: Stream for {Type}/{Id} ended early after retries", type, id);
                HttpContext.Abort();
                return new EmptyResult();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Jfresolve: Error proxying stream from {RedirectUrl}", redirectUrl);
                return StatusCode(502, $"Error proxying stream: {ex.Message}");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Jfresolve: HTTP error resolving stream for {Type}/{Id}", type, id);
            return StatusCode(500, $"Error contacting addon: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Jfresolve: JSON parse error for {Type}/{Id}", type, id);
            return StatusCode(500, $"Invalid response from addon: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jfresolve: Error resolving stream for {Type}/{Id}", type, id);
            return StatusCode(500, $"Error resolving stream: {ex.Message}");
        }
    }

    private const int MaxStreamResumeAttempts = 5;

    /// <summary>Proxies the upstream stream, honouring Range. Debrid hosts often reset long connections on high-bitrate files, so on a drop it reconnects from the last byte sent.</summary>
    private async Task ProxyStreamAsync(string url, CancellationToken clientAborted)
    {
        var client = _httpClientFactory.CreateClient();
        // No overall timeout: a full movie can take hours to stream. Stalls surface as IO errors.
        client.Timeout = Timeout.InfiniteTimeSpan;

        var (rangeStart, rangeEnd) = ParseRange(Request.Headers["Range"].ToString());
        var forwardRange = !string.IsNullOrEmpty(Request.Headers["Range"].ToString());
        long sent = 0;
        var attempt = 0;
        var buffer = new byte[256 * 1024];

        while (true)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (attempt > 0)
            {
                request.Headers.Range = new RangeHeaderValue(rangeStart + sent, rangeEnd);
            }
            else if (forwardRange)
            {
                request.Headers.TryAddWithoutValidation("Range", Request.Headers["Range"].ToString());
            }

            using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, clientAborted);
            if (!upstream.IsSuccessStatusCode)
            {
                // Debrid host refused the link (IP restriction, VPN/datacenter IP, expired link)
                _logger.LogError(
                    "Jfresolve: Stream host returned {Status} for {Url} (final URL: {FinalUrl})",
                    (int)upstream.StatusCode, url, upstream.RequestMessage?.RequestUri);
            }
            upstream.EnsureSuccessStatusCode();

            // Resume against the final debrid URL so the addon doesn't re-resolve
            url = upstream.RequestMessage?.RequestUri?.ToString() ?? url;

            if (attempt == 0)
            {
                CopyResponseHeaders(upstream);
            }
            else if (upstream.StatusCode != HttpStatusCode.PartialContent)
            {
                // Host ignored Range; resuming would duplicate bytes
                throw new IOException("Upstream does not support range requests, cannot resume stream");
            }

            try
            {
                await using var stream = await upstream.Content.ReadAsStreamAsync(clientAborted);
                int read;
                while ((read = await stream.ReadAsync(buffer, clientAborted)) > 0)
                {
                    await Response.Body.WriteAsync(buffer.AsMemory(0, read), clientAborted);
                    sent += read;
                }
                return; // finished cleanly
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !clientAborted.IsCancellationRequested)
            {
                if (++attempt > MaxStreamResumeAttempts)
                {
                    throw;
                }

                _logger.LogWarning(
                    "Jfresolve: Upstream dropped after {Sent} bytes ({Message}), resuming (attempt {Attempt}/{Max})",
                    sent, ex.Message, attempt, MaxStreamResumeAttempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt), clientAborted);
            }
        }
    }

    private void CopyResponseHeaders(HttpResponseMessage upstream)
    {
        Response.StatusCode = (int)upstream.StatusCode;

        if (upstream.Content.Headers.ContentType != null)
        {
            Response.ContentType = upstream.Content.Headers.ContentType.ToString();
        }

        // Content-Range can be in response or content headers depending on the server
        string? contentRange = null;
        if (upstream.Headers.TryGetValues("Content-Range", out var responseContentRange))
        {
            contentRange = responseContentRange.FirstOrDefault();
        }
        else if (upstream.Content.Headers.TryGetValues("Content-Range", out var contentContentRange))
        {
            contentRange = contentContentRange.FirstOrDefault();
        }

        if (!string.IsNullOrEmpty(contentRange))
        {
            Response.Headers["Content-Range"] = contentRange;
        }

        Response.Headers["Accept-Ranges"] = "bytes";

        if (upstream.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = upstream.Content.Headers.ContentLength.Value;
        }
    }

    /// <summary>Parses a single "bytes=start-end" range. Returns (0, null) when absent or unsupported.</summary>
    private static (long Start, long? End) ParseRange(string header)
    {
        if (RangeHeaderValue.TryParse(header, out var parsed) && parsed.Ranges.Count == 1)
        {
            var r = parsed.Ranges.First();
            if (r.From.HasValue)
            {
                return (r.From.Value, r.To);
            }
        }
        return (0, null);
    }

    /// <summary>Serves jfresolve.png. Jellyfin requests it from /Plugins/{guid}/{version}/Image.</summary>
    [HttpGet("Image")]
    [HttpGet("{version}/Image")] // Handle versioned requests: /Plugins/{guid}/{version}/Image
    [AllowAnonymous]
    public IActionResult GetPluginImage(string? version = null)
    {
        try
        {
            _logger.LogDebug("Jfresolve: Plugin image requested (version: {Version})", version ?? "none");
            
            var assembly = Assembly.GetExecutingAssembly();
            
            var possibleNames = new[]
            {
                "Jfresolve.jfresolve.png",
                "jfresolve.jfresolve.png",
                "Jfresolve.jfresolve-10.11.jfresolve.png"
            };
            
            Stream? imageStream = null;
            string? foundResourceName = null;
            
            foreach (var resourceName in possibleNames)
            {
                imageStream = assembly.GetManifestResourceStream(resourceName);
                if (imageStream != null)
                {
                    foundResourceName = resourceName;
                    _logger.LogDebug("Jfresolve: Found plugin image resource: {ResourceName}", resourceName);
                    break;
                }
            }
            
            if (imageStream == null)
            {
                var allResources = assembly.GetManifestResourceNames();
                _logger.LogWarning("Jfresolve: Plugin image resource not found. Available resources: {Resources}", 
                    string.Join(", ", allResources));
                return NotFound("Plugin image not found");
            }

            return File(imageStream, "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jfresolve: Error serving plugin image");
            return StatusCode(500, "Error serving plugin image");
        }
    }

    [HttpGet("test")]
    [AllowAnonymous]
    public IActionResult Test()
    {
        return Ok(new
        {
            plugin = "Jfresolve",
            version = JfresolvePlugin.Instance?.Version?.ToString() ?? "Unknown",
            message = "API controller is working!",
            manifestConfigured = !string.IsNullOrWhiteSpace(JfresolvePlugin.Instance?.Configuration?.AddonManifestUrl)
        });
    }

    private JsonElement? SelectStreamByQuality(JsonElement streams, string preferredQuality, string? requestedQuality = null, int? requestedIndex = null)
    {
        var streamArray = streams.EnumerateArray().ToList();
        if (streamArray.Count == 0)
            return null;

        if (!string.IsNullOrEmpty(requestedQuality))
        {
            var filteredStreams = FilterStreamsByQuality(streamArray, requestedQuality);
            if (filteredStreams.Count > 0)
            {
                var idx = requestedIndex ?? 0;
                // Index too high: use the last one
                if (idx >= filteredStreams.Count)
                {
                    _logger.LogWarning("Jfresolve: Requested index {Index} out of range for quality {Quality}. Falling back to index {FallbackIndex}.",
                        idx, requestedQuality, filteredStreams.Count - 1);
                    idx = filteredStreams.Count - 1;
                }
                _logger.LogInformation("Jfresolve: Selected quality {Quality} stream at index {Index}", requestedQuality, idx);
                return filteredStreams[idx];
            }

            _logger.LogWarning("Jfresolve: Specifically requested quality {Quality} not found, falling back to discovery logic", requestedQuality);
        }

        if (preferredQuality.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return SelectHighestQualityStream(streamArray);
        }

        var matchedStream = FindStreamByQuality(streamArray, preferredQuality);
        if (matchedStream != null)
        {
            _logger.LogInformation("Jfresolve: Selected {Quality} stream (discovery match)", preferredQuality);
            return matchedStream;
        }

        // Preferred quality is a ceiling
        return SelectClosestQualityStream(streamArray, preferredQuality);
    }

    private static readonly string[] QualityPriority = { "4K", "1440p", "1080p", "720p", "480p" };

    /// <summary>Best stream at or below the preferred quality, else the lowest above it, else the first stream.</summary>
    private JsonElement SelectClosestQualityStream(System.Collections.Generic.List<JsonElement> streams, string preferredQuality)
    {
        var preferredIndex = Array.FindIndex(QualityPriority, q => q.Equals(preferredQuality, StringComparison.OrdinalIgnoreCase));
        if (preferredIndex < 0)
        {
            return SelectHighestQualityStream(streams);
        }

        var below = QualityPriority.Skip(preferredIndex + 1);
        var above = QualityPriority.Take(preferredIndex).Reverse();
        foreach (var quality in below.Concat(above))
        {
            var stream = FindStreamByQuality(streams, quality);
            if (stream != null)
            {
                _logger.LogInformation("Jfresolve: Preferred quality {Preferred} not found, selected closest available {Quality}", preferredQuality, quality);
                return stream.Value;
            }
        }

        _logger.LogInformation("Jfresolve: Preferred quality {Preferred} not found and no quality indicators detected, using first stream", preferredQuality);
        return streams[0];
    }

    private System.Collections.Generic.List<JsonElement> FilterStreamsByQuality(System.Collections.Generic.List<JsonElement> streams, string quality)
    {
        var indicators = GetQualityIndicators(quality);
        var results = new System.Collections.Generic.List<JsonElement>();

        foreach (var stream in streams)
        {
            var text = GetStreamText(stream);
            if (indicators.Any(ind => text.Contains(ind, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(stream);
            }
        }

        return results;
    }

    private JsonElement? FindStreamByQuality(System.Collections.Generic.List<JsonElement> streams, string quality)
    {
        var qualityIndicators = GetQualityIndicators(quality);

        foreach (var stream in streams)
        {
            var streamText = GetStreamText(stream);

            foreach (var indicator in qualityIndicators)
            {
                if (streamText.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                {
                    return stream;
                }
            }
        }

        return null;
    }

    private JsonElement SelectHighestQualityStream(System.Collections.Generic.List<JsonElement> streams)
    {
        foreach (var quality in QualityPriority)
        {
            var stream = FindStreamByQuality(streams, quality);
            if (stream != null)
            {
                _logger.LogInformation("Jfresolve: Auto-selected {Quality} stream (highest available)", quality);
                return stream.Value;
            }
        }

        _logger.LogInformation("Jfresolve: No quality indicators found, using first stream");
        return streams[0];
    }

    private string[] GetQualityIndicators(string quality)
    {
        return quality.ToLowerInvariant() switch
        {
            "4k" => new[] { "4k", "2160p", "2160" },
            "1440p" => new[] { "1440p", "1440" },
            "1080p" => new[] { "1080p", "1080" },
            "720p" => new[] { "720p", "720" },
            "480p" => new[] { "480p", "480" },
            _ => new[] { quality.ToLowerInvariant() }
        };
    }

    private string GetStreamText(JsonElement stream)
    {
        var text = string.Empty;

        if (stream.TryGetProperty("name", out var name))
        {
            text += name.GetString() + " ";
        }

        if (stream.TryGetProperty("title", out var title))
        {
            text += title.GetString();
        }

        return text;
    }

    private string BuildFailoverCacheKey(string type, string id, string? season, string? episode, string? quality)
    {
        var key = $"{type}:{id}";

        if (!string.IsNullOrEmpty(season) && !string.IsNullOrEmpty(episode))
        {
            key += $":{season}:{episode}";
        }

        key += $":{quality ?? "default"}";

        return key;
    }

    /// <summary>Failover by time window: within the grace period serve the same link (buffering), within the failover window move to the next link, after that reset.</summary>
    private int DetermineFailoverIndex(
        string cacheKey,
        int? requestedIndex,
        string? quality,
        JsonElement streams,
        string preferredQuality,
        string type)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null)
        {
            return requestedIndex ?? 0;
        }

        bool failoverEnabled = type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? config.EnableMovieFailover
            : config.EnableShowFailover;

        if (!failoverEnabled)
        {
            _logger.LogDebug("Jfresolve FAILOVER: Disabled for {Type}, using requested index {Index}", type, requestedIndex ?? 0);
            return requestedIndex ?? 0;
        }

        int effectiveIndex = requestedIndex ?? 0;

        var streamArray = streams.EnumerateArray().ToList();
        var totalStreams = streamArray.Count;

        // With a quality set, count only matching streams
        if (!string.IsNullOrEmpty(quality))
        {
            var filteredStreams = FilterStreamsByQuality(streamArray, quality);
            totalStreams = filteredStreams.Count;

            if (totalStreams == 0)
            {
                _logger.LogWarning(
                    "Jfresolve FAILOVER: No streams found for quality {Quality}, falling back to discovery",
                    quality
                );
                totalStreams = streamArray.Count;
            }
        }

        if (totalStreams <= 1)
        {
            _logger.LogDebug("Jfresolve FAILOVER: Only {Count} stream(s) available, no failover needed", totalStreams);
            return effectiveIndex;
        }

        var now = DateTime.UtcNow;
        var gracePeriod = TimeSpan.FromSeconds(config.FailoverGracePeriodSeconds);
        var resetWindow = TimeSpan.FromSeconds(config.FailoverWindowSeconds);

        if (_failoverCache.TryGetValue(cacheKey, out var state))
        {
            var timeSinceFirstAttempt = now - state.FirstAttempt;
            var timeSinceLastAttempt = now - state.LastAttempt;

            if (timeSinceLastAttempt > resetWindow)
            {
                _logger.LogInformation(
                    "Jfresolve FAILOVER: Reset for {Key} - {Time:F1}s since last attempt (success assumed)",
                    cacheKey, timeSinceLastAttempt.TotalSeconds
                );
                _failoverCache.TryRemove(cacheKey, out _);
                effectiveIndex = requestedIndex ?? 0;

                _failoverCache[cacheKey] = new FailoverState
                {
                    CurrentIndex = effectiveIndex,
                    FirstAttempt = now,
                    LastAttempt = now,
                    AttemptCount = 1
                };

                return effectiveIndex;
            }

            // Grace period: same link while it buffers
            if (timeSinceFirstAttempt < gracePeriod)
            {
                _logger.LogDebug(
                    "Jfresolve FAILOVER: Grace period for {Key} - {Time:F1}s/{Grace}s elapsed, serving index {Index} (attempt #{Attempt})",
                    cacheKey, timeSinceFirstAttempt.TotalSeconds, gracePeriod.TotalSeconds, state.CurrentIndex, state.AttemptCount + 1
                );

                state.LastAttempt = now;
                state.AttemptCount++;

                return state.CurrentIndex;
            }

            // Failover window: next link
            effectiveIndex = state.CurrentIndex + 1;

            // Wrap around when exhausted
            if (effectiveIndex >= totalStreams)
            {
                effectiveIndex = 0;
                _logger.LogWarning(
                    "Jfresolve FAILOVER: Exhausted all {Count} streams for {Key}, wrapping to index 0 (attempt #{Attempt})",
                    totalStreams, cacheKey, state.AttemptCount + 1
                );
            }
            else
            {
                _logger.LogWarning(
                    "Jfresolve FAILOVER: Grace period expired for {Key}. " +
                    "Switching from index {OldIndex} to {NewIndex}/{Total} (attempt #{Attempt})",
                    cacheKey, state.CurrentIndex, effectiveIndex, totalStreams, state.AttemptCount + 1
                );
            }

            state.CurrentIndex = effectiveIndex;
            state.FirstAttempt = now;  // Reset first attempt for new link
            state.LastAttempt = now;
            state.AttemptCount++;

            return effectiveIndex;
        }
        else
        {
            _logger.LogInformation(
                "Jfresolve FAILOVER: First attempt for {Key}, serving index {Index}",
                cacheKey, effectiveIndex
            );

            _failoverCache[cacheKey] = new FailoverState
            {
                CurrentIndex = effectiveIndex,
                FirstAttempt = now,
                LastAttempt = now,
                AttemptCount = 1
            };

            return effectiveIndex;
        }
    }
}
