using System;
using MediaBrowser.Model.Plugins;

namespace Jfresolve.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableSearch { get; set; } = true;
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Jellyfin server URL (e.g., http://localhost:8096)
    /// Used to construct API controller URLs for stream resolution
    /// </summary>
    public string JellyfinServerUrl { get; set; } = "http://localhost:8096";

    /// <summary>
    /// Preferred stream quality when multiple options are available
    /// Options: Auto, 4K, 1440p, 1080p, 720p, 480p
    /// Auto will select the highest quality available
    /// </summary>
    public string PreferredQuality { get; set; } = "Auto";

    /// <summary>
    /// Stremio addon manifest URL (e.g., stremio://11111112222222333333344444445555/manifest.json)
    /// Will be normalized to https:// format automatically
    /// </summary>
    public string AddonManifestUrl { get; set; } = string.Empty;

    // Library Folder Paths (like Gelato)
    /// <summary>
    /// Path to the movie library folder (e.g., /data/movies)
    /// This should be an existing Jellyfin library folder
    /// </summary>
    public string MoviePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the TV series library folder (e.g., /data/tvseries)
    /// This should be an existing Jellyfin library folder
    /// </summary>
    public string SeriesPath { get; set; } = string.Empty;

    /// <summary>
    /// Enable separate anime folder
    /// When enabled, anime shows (TMDB genre ID 16) will be added to the anime folder instead of the main series folder
    /// </summary>
    public bool EnableAnimeFolder { get; set; } = false;

    /// <summary>
    /// Path to the anime library folder (e.g., /data/anime)
    /// Only used when EnableAnimeFolder is true
    /// </summary>
    public string AnimePath { get; set; } = string.Empty;

    // TMDB Settings
    public bool IncludeAdult { get; set; } = false;
    public bool FilterUnreleased { get; set; } = true;
    public int UnreleasedBufferDays { get; set; } = 7;
    public int SearchResultLimit { get; set; } = 15;

    // Auto Library Population Settings
    /// <summary>
    /// Enable automatic library population with trending/popular content
    /// </summary>
    public bool EnableAutoPopulation { get; set; } = false;

    /// <summary>
    /// Source for library population content
    /// </summary>
    public PopulationSource PopulationSource { get; set; } = PopulationSource.TMDB;

    /// <summary>
    /// Maximum number of items to add per population run
    /// </summary>
    public int PopulationResultLimit { get; set; } = 20;

    /// <summary>
    /// Last time library population was run
    /// </summary>
    public DateTime? LastPopulationRun { get; set; } = null;

    // FFmpeg Settings (Gelato pattern)
    /// <summary>
    /// Enable custom FFmpeg settings
    /// When disabled, Jellyfin's default FFmpeg settings will be used
    /// </summary>
    public bool EnableCustomFFmpegSettings { get; set; } = false;

    /// <summary>
    /// FFmpeg analyzeduration parameter (e.g., "5M")
    /// Determines how much data is analyzed to find stream information
    /// Only used when EnableCustomFFmpegSettings is true
    /// </summary>
    public string FFmpegAnalyzeDuration { get; set; } = "5M";

    /// <summary>
    /// FFmpeg probesize parameter (e.g., "40M")
    /// Determines how much data is probed before determining stream characteristics
    /// Only used when EnableCustomFFmpegSettings is true
    /// </summary>
    public string FFmpegProbeSize { get; set; } = "40M";
}

/// <summary>
/// Content source for library population
/// </summary>
public enum PopulationSource
{
    /// <summary>
    /// Use TMDB trending content (default, same as previous `TMDB` value)
    /// </summary>
    TMDB = 0,

    /// <summary>
    /// Use TMDB popular content (movie/tv popular endpoints)
    /// </summary>
    TMDBPopular = 1,

    /// <summary>
    /// Use TMDB top rated content (movie/tv top_rated endpoints)
    /// </summary>
    TMDBTopRated = 2
}
