using System;
using MediaBrowser.Model.Plugins;

namespace Jfresolve.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableSearch { get; set; } = true;
    public string TmdbApiKey { get; set; } = string.Empty;

    // Base of the resolve URLs stored on items; must be reachable by clients
    public string JellyfinServerUrl { get; set; } = "http://localhost:8096";

    // Auto, 4K, 1440p, 1080p, 720p, 480p
    public string PreferredQuality { get; set; } = "Auto";

    // stremio:// or https:// manifest URL
    public string AddonManifestUrl { get; set; } = string.Empty;

    // Write .strm files instead of inserting items into the database
    public bool WriteStrmFiles { get; set; } = false;

    public PathConfigMode PathMode { get; set; } = PathConfigMode.Simple;

    // Simple mode: same folders for search results and auto-population
    public string MoviePath { get; set; } = string.Empty;
    public string SeriesPath { get; set; } = string.Empty;
    public bool EnableAnimeFolder { get; set; } = false;
    public string AnimePath { get; set; } = string.Empty;

    // Advanced mode: separate folders for search results and auto-population
    public string MovieSearchPath { get; set; } = string.Empty;
    public string SeriesSearchPath { get; set; } = string.Empty;
    public string AnimeSearchPath { get; set; } = string.Empty;
    public string MovieAutoPopulatePath { get; set; } = string.Empty;
    public string SeriesAutoPopulatePath { get; set; } = string.Empty;
    public string AnimeAutoPopulatePath { get; set; } = string.Empty;
    public bool EnableAnimeFolderAdvanced { get; set; } = false;

    public bool IncludeAdult { get; set; } = false;
    public bool FilterUnreleased { get; set; } = true;
    public int UnreleasedBufferDays { get; set; } = 7;
    public int SearchResultLimit { get; set; } = 15;

    public bool EnableAutoPopulation { get; set; } = false;

    // Deprecated: replaced by the Use*Source flags, kept so old configs still load
    public PopulationSource PopulationSource { get; set; } = PopulationSource.TMDB;

    public bool UseTrendingSource { get; set; } = true;
    public bool UsePopularSource { get; set; } = false;
    public bool UseTopRatedSource { get; set; } = false;
    public int PopulationResultLimit { get; set; } = 20;

    // Quality versions created per movie
    public bool Enable4KVersion { get; set; } = false;
    public bool Enable1080pVersion { get; set; } = false;
    public bool Enable720pVersion { get; set; } = false;
    public bool EnableUnknownVersion { get; set; } = false;

    // 1-10
    public int MaxItemsPerQuality { get; set; } = 1;

    public DateTime? LastPopulationRun { get; set; } = null;

    // Comma-separated TMDB IDs
    public string ExclusionList { get; set; } = string.Empty;

    public bool EnableCustomFFmpegSettings { get; set; } = false;
    public string FFmpegAnalyzeDuration { get; set; } = "5M";
    public string FFmpegProbeSize { get; set; } = "40M";

    // On a failed stream, try the next link for the same quality
    public bool EnableMovieFailover { get; set; } = false;
    public bool EnableShowFailover { get; set; } = false;

    // Keep serving the same link for this long while it buffers
    public int FailoverGracePeriodSeconds { get; set; } = 45;

    // After this long, assume playback worked and reset failover state
    public int FailoverWindowSeconds { get; set; } = 120;
}

public enum PopulationSource
{
    TMDB = 0, // Trending
    TMDBPopular = 1,
    TMDBTopRated = 2
}

public enum PathConfigMode
{
    Simple = 0,
    Advanced = 1
}
