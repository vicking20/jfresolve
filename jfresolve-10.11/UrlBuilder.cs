using System;

namespace Jfresolve;

/// <summary>
/// Utility class for building and normalizing URLs used throughout Jfresolve.
/// </summary>
public static class UrlBuilder
{
    // Constants for API endpoints
    private const string ResolveEndpointBase = "/Plugins/Jfresolve/resolve";

    /// <summary>
    /// Normalizes a Jellyfin base URL by trimming trailing slashes.
    /// </summary>
    public static string NormalizeJellyfinBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "http://127.0.0.1:8096";

        return url.TrimEnd('/');
    }

    /// <summary>
    /// Normalizes a Stremio addon manifest URL.
    /// Handles various input formats and returns a clean https:// base URL.
    /// </summary>
    public static string NormalizeManifestUrl(string? manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return string.Empty;

        var normalized = manifestUrl
            .Replace("manifest.json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        normalized = normalized
            .Replace("stremio://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim('/');

        return $"https://{normalized}";
    }

    /// <summary>
    /// Builds a resolver URL for a movie.
    /// Format: "{baseUrl}/Plugins/Jfresolve/resolve/movie/{imdbId}"
    /// </summary>
    public static string BuildMovieResolverUrl(string baseUrl, string imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
            throw new ArgumentException("IMDB ID cannot be empty", nameof(imdbId));

        var normalizedBase = NormalizeJellyfinBaseUrl(baseUrl);
        return $"{normalizedBase}{ResolveEndpointBase}/movie/{imdbId}";
    }

    /// <summary>
    /// Builds a resolver URL for a TV series episode.
    /// Format: "{baseUrl}/Plugins/Jfresolve/resolve/series/{imdbId}?season={season}&episode={episode}"
    /// </summary>
    public static string BuildSeriesResolverUrl(string baseUrl, string imdbId, int season, int episode)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
            throw new ArgumentException("IMDB ID cannot be empty", nameof(imdbId));

        var normalizedBase = NormalizeJellyfinBaseUrl(baseUrl);
        return $"{normalizedBase}{ResolveEndpointBase}/series/{imdbId}?season={season}&episode={episode}";
    }

    /// <summary>
    /// Builds a resolver URL for a piece of external metadata (movie or series).
    /// Returns the appropriate resolver URL based on the metadata type and season/episode info.
    /// </summary>
    public static string BuildResolverUrlForStrm(string baseUrl, string imdbId, string metadataType, int? season = null, int? episode = null)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
            throw new ArgumentException("IMDB ID cannot be empty", nameof(imdbId));

        if (metadataType.Equals("Series", StringComparison.OrdinalIgnoreCase) && season.HasValue && episode.HasValue)
        {
            return BuildSeriesResolverUrl(baseUrl, imdbId, season.Value, episode.Value);
        }

        // Treat everything else as movie
        return BuildMovieResolverUrl(baseUrl, imdbId);
    }

    /// <summary>
    /// Builds a complete library item path for storing in Jellyfin.
    /// Combines library path with folder name.
    /// </summary>
    public static string BuildLibraryItemPath(string libraryPath, string folderName)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
            throw new ArgumentException("Library path cannot be empty", nameof(libraryPath));

        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be empty", nameof(folderName));

        return System.IO.Path.Combine(libraryPath, folderName);
    }
}
