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

    // Library Folder Path Configuration Mode
    /// <summary>
    /// Path configuration mode: Simple (same paths for search and auto-populate) or Advanced (separate paths)
    /// </summary>
    public PathConfigMode PathMode { get; set; } = PathConfigMode.Simple;

    // Simple Mode Paths (backward compatible)
    /// <summary>
    /// Path to the movie library folder in Simple mode (e.g., /data/movies)
    /// Used for both search results and auto-populated content
    /// This should be an existing Jellyfin library folder
    /// </summary>
    public string MoviePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the TV series library folder in Simple mode (e.g., /data/tvseries)
    /// Used for both search results and auto-populated content
    /// This should be an existing Jellyfin library folder
    /// </summary>
    public string SeriesPath { get; set; } = string.Empty;

    /// <summary>
    /// Enable separate anime folder in Simple mode
    /// When enabled, anime shows (TMDB genre ID 16) will be added to the anime folder instead of the main series folder
    /// </summary>
    public bool EnableAnimeFolder { get; set; } = false;

    /// <summary>
    /// Path to the anime library folder in Simple mode (e.g., /data/anime)
    /// Only used when EnableAnimeFolder is true
    /// </summary>
    public string AnimePath { get; set; } = string.Empty;

    // Advanced Mode Paths - Search
    /// <summary>
    /// Path to the movie search results folder in Advanced mode (e.g., /data/movies-search)
    /// Only used when PathMode = Advanced
    /// </summary>
    public string MovieSearchPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the series search results folder in Advanced mode (e.g., /data/series-search)
    /// Only used when PathMode = Advanced
    /// </summary>
    public string SeriesSearchPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the anime search results folder in Advanced mode (e.g., /data/anime-search)
    /// Only used when PathMode = Advanced and EnableAnimeFolderAdvanced = true
    /// </summary>
    public string AnimeSearchPath { get; set; } = string.Empty;

    // Advanced Mode Paths - Auto-Populate
    /// <summary>
    /// Path to the movie auto-populate folder in Advanced mode (e.g., /data/movies-auto)
    /// Only used when PathMode = Advanced
    /// </summary>
    public string MovieAutoPopulatePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the series auto-populate folder in Advanced mode (e.g., /data/series-auto)
    /// Only used when PathMode = Advanced
    /// </summary>
    public string SeriesAutoPopulatePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the anime auto-populate folder in Advanced mode (e.g., /data/anime-auto)
    /// Only used when PathMode = Advanced and EnableAnimeFolderAdvanced = true
    /// </summary>
    public string AnimeAutoPopulatePath { get; set; } = string.Empty;

    /// <summary>
    /// Enable separate anime folders in Advanced mode
    /// When enabled, anime content will use separate search and auto-populate paths
    /// </summary>
    public bool EnableAnimeFolderAdvanced { get; set; } = false;

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
    /// Source for library population content (deprecated - use boolean flags below)
    /// Kept for backward compatibility
    /// </summary>
    public PopulationSource PopulationSource { get; set; } = PopulationSource.TMDB;

    /// <summary>
    /// Enable TMDB Trending content source for auto-population
    /// </summary>
    public bool UseTrendingSource { get; set; } = true;

    /// <summary>
    /// Enable TMDB Popular content source for auto-population
    /// </summary>
    public bool UsePopularSource { get; set; } = false;

    /// <summary>
    /// Enable TMDB Top Rated content source for auto-population
    /// </summary>
    public bool UseTopRatedSource { get; set; } = false;

    /// <summary>
    /// Maximum number of items to add per population run
    /// </summary>
    public int PopulationResultLimit { get; set; } = 20;

    // Virtual Versioning Settings
    /// <summary>
    /// Enable 4K/2160p version of virtual items
    /// </summary>
    public bool Enable4KVersion { get; set; } = false;

    /// <summary>
    /// Enable 1080p version of virtual items
    /// </summary>
    public bool Enable1080pVersion { get; set; } = false;

    /// <summary>
    /// Enable 720p version of virtual items
    /// </summary>
    public bool Enable720pVersion { get; set; } = false;

    /// <summary>
    /// Enable "Unknown" (first available) version of virtual items
    /// </summary>
    public bool EnableUnknownVersion { get; set; } = false;

    /// <summary>
    /// Maximum number of items to add for each enabled quality
    /// Allowed range: 1-10
    /// </summary>
    public int MaxItemsPerQuality { get; set; } = 1;

    /// <summary>
    /// Last time library population was run
    /// </summary>
    public DateTime? LastPopulationRun { get; set; } = null;

    /// <summary>
    /// Comma-separated list of TMDB IDs to exclude from auto-population
    /// </summary>
    public string ExclusionList { get; set; } = string.Empty;

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

    // Failover Settings
    /// <summary>
    /// Enable automatic failover for movies
    /// When enabled, if a stream fails, the plugin will try the next available quality/index
    /// </summary>
    public bool EnableMovieFailover { get; set; } = false;

    /// <summary>
    /// Enable automatic failover for TV shows
    /// When enabled, if a stream fails, the plugin will try the next available quality/index
    /// </summary>
    public bool EnableShowFailover { get; set; } = false;

    /// <summary>
    /// Grace period in seconds before considering a stream as failed
    /// During this time, the same link will be returned even if multiple requests come in
    /// This allows time for buffering/loading. Default: 45 seconds
    /// </summary>
    public int FailoverGracePeriodSeconds { get; set; } = 45;

    /// <summary>
    /// Failover window in seconds
    /// After this time, the failover state is reset and assumes previous playback was successful
    /// Default: 120 seconds (2 minutes)
    /// </summary>
    public int FailoverWindowSeconds { get; set; } = 120;
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

/// <summary>
/// Path configuration mode
/// </summary>
public enum PathConfigMode
{
    /// <summary>
    /// Simple mode: Same paths for search results and auto-populated content (backward compatible)
    /// </summary>
    Simple = 0,

    /// <summary>
    /// Advanced mode: Separate paths for search results vs auto-populated content
    /// </summary>
    Advanced = 1
}
