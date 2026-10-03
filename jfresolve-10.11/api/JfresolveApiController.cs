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

/// <summary>
/// API controller for Jfresolve plugin endpoints
/// Provides stream resolution for virtual items with automatic failover for dead links
/// </summary>
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

    /// <summary>
    /// Tracks failover state with time windows
    /// </summary>
    private class FailoverState
    {
        public int CurrentIndex { get; set; }
        public DateTime FirstAttempt { get; set; }
        public DateTime LastAttempt { get; set; }
        public int AttemptCount { get; set; }
    }

    /// <summary>
    /// Resolves a stream URL for a given movie or series
    /// Contacts the Stremio addon to get the real stream URL
    /// </summary>
    /// <param name="type">The content type (movie, series)</param>
    /// <param name="id">The IMDb or TMDB ID</param>
    /// <param name="season">Optional season number (for series)</param>
    /// <param name="episode">Optional episode number (for series)</param>
    /// <returns>Proxied stream or error</returns>
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

        // Check if addon manifest URL is configured
        if (string.IsNullOrWhiteSpace(config.AddonManifestUrl))
        {
            _logger.LogError("Jfresolve: Addon manifest URL not configured - cannot resolve stream");
            return NotFound("Addon manifest URL not configured. Please configure it in plugin settings.");
        }

        try
        {
            // Normalize the manifest URL (remove stremio://, convert to https://)
            var manifestBase = UrlBuilder.NormalizeManifestUrl(config.AddonManifestUrl);

            // Build the stream endpoint URL
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

            // Call the Stremio addon to get the stream
            var addonHttpClient = _httpClientFactory.CreateClient();
            addonHttpClient.Timeout = TimeSpan.FromSeconds(30); // Set timeout to prevent hanging
            addonHttpClient.DefaultRequestHeaders.Add("User-Agent", "Jfresolve/1.0");
            using var addonResponse = await addonHttpClient.GetAsync(streamUrl);
            if (!addonResponse.IsSuccessStatusCode)
            {
                // Log the status and a snippet of the body - 403s here usually come from the addon or
                // Cloudflare in front of it (blocked server/VPS IP), not from the debrid provider
                var body = await addonResponse.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Jfresolve: Addon returned {Status} for {StreamUrl}. Response: {Body}",
                    (int)addonResponse.StatusCode, streamUrl, body.Length > 300 ? body[..300] : body);
                return StatusCode(502, $"Addon returned {(int)addonResponse.StatusCode}");
            }
            var response = await addonResponse.Content.ReadAsStringAsync();

            // Parse the JSON response
            using var json = JsonDocument.Parse(response);
            if (!json.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            {
                _logger.LogWarning("Jfresolve: No streams found for {Type}/{Id}", type, id);
                return NotFound($"No streams found for {id}");
            }

            // FAILOVER LOGIC: Determine effective index with time-window based retry for dead links
            var cacheKey = BuildFailoverCacheKey(type, id, season, episode, quality);
            int effectiveIndex = DetermineFailoverIndex(cacheKey, index, quality, streams, config.PreferredQuality, type);

            // Select the stream using failover-adjusted index
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

            // Ensure the redirect URL is absolute
            if (!Uri.IsWellFormedUriString(redirectUrl, UriKind.Absolute))
            {
                _logger.LogWarning("Jfresolve: Redirect URL is not absolute: {RedirectUrl}", redirectUrl);
                return BadRequest("Invalid redirect URL format");
            }

            // Jellyfin 10.11.6 compatibility: Proxy the stream instead of redirecting
            // FFmpeg in 10.11.6 doesn't properly follow HTTP redirects from plugin endpoints
            // By proxying, FFmpeg gets the stream directly without needing to follow redirects
            // IMPORTANT: Must support HTTP Range requests (206 Partial Content) for FFmpeg seeking
            try
            {
                _logger.LogInformation("Jfresolve: Proxying stream from {RedirectUrl}", redirectUrl);
                await ProxyStreamAsync(redirectUrl, HttpContext.RequestAborted);
                return new EmptyResult();
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Client (FFmpeg/player) went away - normal when seeking or stopping
                _logger.LogDebug("Jfresolve: Client closed stream for {Type}/{Id}", type, id);
                return new EmptyResult();
            }
            catch (Exception ex) when (Response.HasStarted)
            {
                // Bytes already sent - can't change the status code anymore, just end the response
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

    /// <summary>
    /// Proxies the upstream stream to the client, honouring Range requests.
    /// Debrid hosts often reset long-running connections on high-bitrate files (4K remuxes),
    /// so when the upstream drops mid-stream we reconnect with a Range header from the
    /// last byte sent and keep going instead of failing playback.
    /// </summary>
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
                // 403 here comes from the debrid host / addon resolve link (e.g. IP not allowed, VPN/datacenter IP, expired link)
                _logger.LogError(
                    "Jfresolve: Stream host returned {Status} for {Url} (final URL: {FinalUrl})",
                    (int)upstream.StatusCode, url, upstream.RequestMessage?.RequestUri);
            }
            upstream.EnsureSuccessStatusCode();

            // Reuse the final (post-redirect) debrid URL on resume so the addon isn't asked to re-resolve
            url = upstream.RequestMessage?.RequestUri?.ToString() ?? url;

            if (attempt == 0)
            {
                CopyResponseHeaders(upstream);
            }
            else if (upstream.StatusCode != HttpStatusCode.PartialContent)
            {
                // Host ignored our Range header - resuming would send duplicate bytes
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
        // Copy status code (206 for partial content if range was requested)
        Response.StatusCode = (int)upstream.StatusCode;

        if (upstream.Content.Headers.ContentType != null)
        {
            Response.ContentType = upstream.Content.Headers.ContentType.ToString();
        }

        // Content-Range can be in response headers or content headers depending on the server
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

    /// <summary>
    /// Parses a single "bytes=start-end" range. Returns (0, null) when absent or unsupported.
    /// </summary>
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

    /// <summary>
    /// Serves the plugin image (jfresolve.png)
    /// Jellyfin requests this from /Plugins/{guid}/{version}/Image
    /// </summary>
    [HttpGet("Image")]
    [HttpGet("{version}/Image")] // Handle versioned requests: /Plugins/{guid}/{version}/Image
    [AllowAnonymous]
    public IActionResult GetPluginImage(string? version = null)
    {
        try
        {
            _logger.LogDebug("Jfresolve: Plugin image requested (version: {Version})", version ?? "none");
            
            var assembly = Assembly.GetExecutingAssembly();
            
            // Try different possible resource names
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
            
            // If not found, list all resources for debugging
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

    /// <summary>
    /// Test endpoint to verify API controller is working
    /// </summary>
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

    /// <summary>
    /// Selects the best stream from the available streams based on preferred quality
    /// </summary>
    private JsonElement? SelectStreamByQuality(JsonElement streams, string preferredQuality, string? requestedQuality = null, int? requestedIndex = null)
    {
        var streamArray = streams.EnumerateArray().ToList();
        if (streamArray.Count == 0)
            return null;

        // If a specific quality is requested (Virtual Versioning), filter and pick by index
        if (!string.IsNullOrEmpty(requestedQuality))
        {
            var filteredStreams = FilterStreamsByQuality(streamArray, requestedQuality);
            if (filteredStreams.Count > 0)
            {
                var idx = requestedIndex ?? 0;
                // Fallback to last available if index is too high
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

        // Discovery logic (Discovery mode or fallback)
        if (preferredQuality.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return SelectHighestQualityStream(streamArray);
        }

        // Try to find exact match for preferred quality
        var matchedStream = FindStreamByQuality(streamArray, preferredQuality);
        if (matchedStream != null)
        {
            _logger.LogInformation("Jfresolve: Selected {Quality} stream (discovery match)", preferredQuality);
            return matchedStream;
        }

        // Fallback: treat the preferred quality as a ceiling - take the next best quality below it,
        // and only go above it if nothing at or below is available
        return SelectClosestQualityStream(streamArray, preferredQuality);
    }

    private static readonly string[] QualityPriority = { "4K", "1440p", "1080p", "720p", "480p" };

    /// <summary>
    /// Picks the best stream at or below the preferred quality; if none exist, the lowest one above it,
    /// and finally the first stream if no quality could be detected.
    /// </summary>
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

    /// <summary>
    /// Filters streams list to only those containing the specified quality indicators
    /// </summary>
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

    /// <summary>
    /// Finds a stream matching the specified quality preference
    /// </summary>
    private JsonElement? FindStreamByQuality(System.Collections.Generic.List<JsonElement> streams, string quality)
    {
        var qualityIndicators = GetQualityIndicators(quality);

        foreach (var stream in streams)
        {
            var streamText = GetStreamText(stream);

            // Check if any quality indicator is present in the stream text
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

    /// <summary>
    /// Selects the highest quality stream from the available streams
    /// Priority order: 4K/2160p > 1440p > 1080p > 720p > 480p > first available
    /// </summary>
    private JsonElement SelectHighestQualityStream(System.Collections.Generic.List<JsonElement> streams)
    {
        // Try to find streams in order of quality preference
        foreach (var quality in QualityPriority)
        {
            var stream = FindStreamByQuality(streams, quality);
            if (stream != null)
            {
                _logger.LogInformation("Jfresolve: Auto-selected {Quality} stream (highest available)", quality);
                return stream.Value;
            }
        }

        // Fallback to first stream if no quality indicators found
        _logger.LogInformation("Jfresolve: No quality indicators found, using first stream");
        return streams[0];
    }

    /// <summary>
    /// Gets quality indicators for a given quality preference
    /// Maps user-friendly names to various formats used by different addons
    /// </summary>
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

    /// <summary>
    /// Extracts searchable text from a stream object (name + title fields)
    /// </summary>
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

    /// <summary>
    /// Builds a cache key for failover tracking
    /// Format: type:id[:season:episode]:quality
    /// </summary>
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

    /// <summary>
    /// Determines the effective stream index using time-window failover logic
    /// - Grace period (0-45s): Keep serving same link to allow buffering
    /// - Failover window (45s-2min): Try next link on new request
    /// - Reset (>2min): Assume success, reset to original index
    /// </summary>
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

        // Check if failover is enabled for this content type
        bool failoverEnabled = type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? config.EnableMovieFailover
            : config.EnableShowFailover;

        if (!failoverEnabled)
        {
            _logger.LogDebug("Jfresolve FAILOVER: Disabled for {Type}, using requested index {Index}", type, requestedIndex ?? 0);
            return requestedIndex ?? 0;
        }

        int effectiveIndex = requestedIndex ?? 0;

        // Get total available streams for this quality
        var streamArray = streams.EnumerateArray().ToList();
        var totalStreams = streamArray.Count;

        // If quality is specified, count only matching streams
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

        // If only one stream available, no need for failover
        if (totalStreams <= 1)
        {
            _logger.LogDebug("Jfresolve FAILOVER: Only {Count} stream(s) available, no failover needed", totalStreams);
            return effectiveIndex;
        }

        var now = DateTime.UtcNow;
        var gracePeriod = TimeSpan.FromSeconds(config.FailoverGracePeriodSeconds);
        var resetWindow = TimeSpan.FromSeconds(config.FailoverWindowSeconds);

        // Check failover state
        if (_failoverCache.TryGetValue(cacheKey, out var state))
        {
            var timeSinceFirstAttempt = now - state.FirstAttempt;
            var timeSinceLastAttempt = now - state.LastAttempt;

            // Reset window: assume success, clear state
            if (timeSinceLastAttempt > resetWindow)
            {
                _logger.LogInformation(
                    "Jfresolve FAILOVER: Reset for {Key} - {Time:F1}s since last attempt (success assumed)",
                    cacheKey, timeSinceLastAttempt.TotalSeconds
                );
                _failoverCache.TryRemove(cacheKey, out _);
                effectiveIndex = requestedIndex ?? 0;

                // Create new state
                _failoverCache[cacheKey] = new FailoverState
                {
                    CurrentIndex = effectiveIndex,
                    FirstAttempt = now,
                    LastAttempt = now,
                    AttemptCount = 1
                };

                return effectiveIndex;
            }

            // Grace period: keep serving same link to allow buffering
            if (timeSinceFirstAttempt < gracePeriod)
            {
                _logger.LogDebug(
                    "Jfresolve FAILOVER: Grace period for {Key} - {Time:F1}s/{Grace}s elapsed, serving index {Index} (attempt #{Attempt})",
                    cacheKey, timeSinceFirstAttempt.TotalSeconds, gracePeriod.TotalSeconds, state.CurrentIndex, state.AttemptCount + 1
                );

                // Update last attempt time and count
                state.LastAttempt = now;
                state.AttemptCount++;

                return state.CurrentIndex;
            }

            // Failover window: try next link
            effectiveIndex = state.CurrentIndex + 1;

            // Wrap around if exhausted
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

            // Update state - new first attempt for this index
            state.CurrentIndex = effectiveIndex;
            state.FirstAttempt = now;  // Reset first attempt for new link
            state.LastAttempt = now;
            state.AttemptCount++;

            return effectiveIndex;
        }
        else
        {
            // First attempt for this content/quality
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
