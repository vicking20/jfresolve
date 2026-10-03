using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jfresolve;

/// <summary>STRM mode: writes .strm files with the resolve URL instead of inserting items into the database.</summary>
public partial class JfresolveManager
{
    /// <summary>Marks plugin-owned folders for the update and purge tasks. Content: "movie|tv:{tmdbId}:{imdbId}".</summary>
    public const string StrmMarkerFileName = ".jfresolve";

    public bool IsStrmMode => JfresolvePlugin.Instance?.Configuration.WriteStrmFiles == true;

    public string BuildMovieResolveUrl(string imdbId, string quality = "", int index = 0)
    {
        var url = $"{GetServerUrl()}/Plugins/Jfresolve/resolve/movie/{imdbId}";
        return AppendVersionQuery(url, quality, index);
    }

    public string BuildEpisodeResolveUrl(string imdbId, int season, int episode, string quality = "", int index = 0)
    {
        var url = $"{GetServerUrl()}/Plugins/Jfresolve/resolve/series/{imdbId}?season={season}&episode={episode}";
        return AppendVersionQuery(url, quality, index);
    }

    private static string GetServerUrl()
    {
        var serverUrl = JfresolvePlugin.Instance?.Configuration.JellyfinServerUrl ?? "http://localhost:8096";
        return serverUrl.TrimEnd('/');
    }

    private static string AppendVersionQuery(string url, string quality, int index)
    {
        if (!string.IsNullOrEmpty(quality))
        {
            url += (url.Contains('?') ? "&" : "?") + $"quality={Uri.EscapeDataString(quality)}";
        }
        if (index > 0)
        {
            url += (url.Contains('?') ? "&" : "?") + $"index={index}";
        }
        return url;
    }

    public async Task<bool> WriteStrmFilesAsync(Folder parent, object metadata, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parent.Path))
        {
            _log.LogWarning("Jfresolve: Cannot write .strm files - library folder has no path");
            return false;
        }

        try
        {
            return metadata switch
            {
                TmdbMovie movie => await WriteMovieStrmAsync(parent.Path, movie, ct),
                TmdbTvShow show => await WriteSeriesStrmAsync(parent.Path, show.Id, show.ImdbId, show.Name, show.GetYear(), ct) > 0,
                _ => false,
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Failed to write .strm files into '{Path}'", parent.Path);
            return false;
        }
    }

    private async Task<bool> WriteMovieStrmAsync(string libraryPath, TmdbMovie movie, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(movie.ImdbId))
        {
            _log.LogDebug("Jfresolve: Skipping .strm for movie '{Title}' - no IMDB ID", movie.Title);
            return false;
        }

        var baseName = BuildBaseName(movie.Title, movie.GetYear());
        var dir = Path.Combine(libraryPath, $"{baseName} [tmdbid-{movie.Id}]");
        var fileBase = Path.GetFileName(dir);
        var written = false;

        // First file is the clean "auto" version; Jellyfin groups "<folder> - <label>.strm" as versions
        var tasks = GetEnabledVersioningTasks();
        for (var i = 0; i < tasks.Count; i++)
        {
            var (quality, index) = i == 0 ? ("", 0) : tasks[i];
            var fileName = i == 0
                ? $"{fileBase}.strm"
                : $"{fileBase} - {GetQualityDisplayTag(quality)}{(index > 0 ? $" #{index + 1}" : "")}.strm";

            written |= await WriteIfChangedAsync(Path.Combine(dir, fileName), BuildMovieResolveUrl(movie.ImdbId, quality, index), ct);
        }

        await WriteIfChangedAsync(Path.Combine(dir, StrmMarkerFileName), $"movie:{movie.Id}:{movie.ImdbId}", ct);

        if (written)
        {
            _log.LogInformation("Jfresolve: Wrote .strm for movie '{Title}' to {Dir}", movie.Title, dir);
        }
        return written;
    }

    /// <summary>Writes one .strm per aired episode. Returns the number of new files.</summary>
    public async Task<int> WriteSeriesStrmAsync(string libraryPath, int tmdbId, string? imdbId, string name, int? year, CancellationToken ct)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(imdbId))
        {
            _log.LogDebug("Jfresolve: Skipping .strm for series '{Name}' - no IMDB ID", name);
            return 0;
        }

        var baseName = BuildBaseName(name, year);
        var dir = Path.Combine(libraryPath, $"{baseName} [tmdbid-{tmdbId}]");

        var details = await _tmdbService.GetTvDetailsAsync(tmdbId, config.TmdbApiKey);
        if (details?.Seasons == null)
        {
            _log.LogWarning("Jfresolve: No seasons found for series '{Name}'", name);
            return 0;
        }

        var now = DateTime.UtcNow;
        var written = 0;

        foreach (var seasonInfo in details.Seasons)
        {
            ct.ThrowIfCancellationRequested();

            var season = await _tmdbService.GetSeasonDetailsAsync(tmdbId, seasonInfo.SeasonNumber, config.TmdbApiKey);
            if (season?.Episodes == null)
            {
                continue;
            }

            var seasonDir = Path.Combine(dir, $"Season {seasonInfo.SeasonNumber:00}");
            foreach (var episode in season.Episodes)
            {
                // Unaired episodes would just fail at playback
                var airDate = episode.GetAirDateTime();
                if (airDate == null || airDate > now)
                {
                    continue;
                }

                var file = Path.Combine(seasonDir, $"{baseName} S{seasonInfo.SeasonNumber:00}E{episode.EpisodeNumber:00}.strm");
                if (await WriteIfChangedAsync(file, BuildEpisodeResolveUrl(imdbId, seasonInfo.SeasonNumber, episode.EpisodeNumber), ct))
                {
                    written++;
                }
            }
        }

        await WriteIfChangedAsync(Path.Combine(dir, StrmMarkerFileName), $"tv:{tmdbId}:{imdbId}", ct);

        if (written > 0)
        {
            _log.LogInformation("Jfresolve: Wrote {Count} episode .strm files for series '{Name}' to {Dir}", written, name, dir);
        }
        return written;
    }

    /// <summary>Adds newly aired episodes and rewrites URLs if the server URL changed. Returns the number of files written.</summary>
    public async Task<int> UpdateStrmSeriesAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var dir in EnumerateStrmFolders())
        {
            ct.ThrowIfCancellationRequested();

            var marker = File.ReadAllText(Path.Combine(dir, StrmMarkerFileName)).Trim().Split(':');
            var libraryPath = Path.GetDirectoryName(dir);

            try
            {
                if (marker.Length == 3 && marker[0] == "tv" && int.TryParse(marker[1], out var tmdbId) && libraryPath != null)
                {
                    // Recover "Name (Year)" from the folder name "Name (Year) [tmdbid-X]"
                    var folderName = Path.GetFileName(dir);
                    var tagStart = folderName.LastIndexOf(" [tmdbid-", StringComparison.Ordinal);
                    var baseName = tagStart > 0 ? folderName[..tagStart] : folderName;
                    total += await WriteSeriesStrmAsync(libraryPath, tmdbId, marker[2], baseName, null, ct);
                }
                else if (marker.Length == 3 && marker[0] == "movie")
                {
                    // Keep movie URLs in sync with the current server URL
                    foreach (var file in Directory.EnumerateFiles(dir, "*.strm"))
                    {
                        var content = File.ReadAllText(file).Trim();
                        var pathStart = content.IndexOf("/Plugins/Jfresolve/", StringComparison.Ordinal);
                        if (pathStart > 0 && await WriteIfChangedAsync(file, GetServerUrl() + content[pathStart..], ct))
                        {
                            total++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Jfresolve: Failed to update .strm folder '{Dir}'", dir);
            }
        }
        return total;
    }

    /// <summary>Deletes every folder that has the marker file.</summary>
    public int PurgeStrmFolders()
    {
        var deleted = 0;
        foreach (var dir in EnumerateStrmFolders())
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve: Failed to delete .strm folder '{Dir}'", dir);
            }
        }
        return deleted;
    }

    public void QueueStrmScan()
    {
        _libraryManager.QueueLibraryScan();
    }

    private IEnumerable<string> EnumerateStrmFolders()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null)
        {
            return Enumerable.Empty<string>();
        }

        var roots = new[]
        {
            config.MoviePath, config.SeriesPath, config.AnimePath,
            config.MovieSearchPath, config.SeriesSearchPath, config.AnimeSearchPath,
            config.MovieAutoPopulatePath, config.SeriesAutoPopulatePath, config.AnimeAutoPopulatePath,
        };

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(r => Directory.EnumerateDirectories(r))
            .Where(d => File.Exists(Path.Combine(d, StrmMarkerFileName)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<bool> WriteIfChangedAsync(string path, string content, CancellationToken ct)
    {
        if (File.Exists(path) && (await File.ReadAllTextAsync(path, ct)).Trim() == content)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        return true;
    }

    private static string BuildBaseName(string title, int? year)
    {
        var name = year.HasValue ? $"{title} ({year})" : title;
        return SanitizeFileName(name);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '/', '\\', '?', '*', '"', '<', '>', '|' }).ToHashSet();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? ' ' : c);
        }

        var result = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(result) ? "Unknown" : result;
    }
}
