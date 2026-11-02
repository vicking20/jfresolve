using System;

namespace Jfresolve;

public static class UrlBuilder
{
    /// <summary>Converts a stremio:// or manifest.json URL to the addon's https base URL.</summary>
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
}
