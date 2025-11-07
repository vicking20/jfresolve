// pluginconfiguration.cs
using System;
using MediaBrowser.Model.Plugins;

namespace Jfresolve.Configuration
{
    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
        /// </summary>
        public PluginConfiguration()
        {
            TmdbApiKey = string.Empty;
            MoviesLibraryPath = "/data/movies";
            ShowsLibraryPath = "/data/tvshows";
            AnimeLibraryPath = string.Empty;
            JellyfinBaseUrl = "http://127.0.0.1:8096";
            AddonManifestUrl = string.Empty;
            SearchNumber = 3;
            IncludeAdult = false;
            IncludeUnreleased = false;
            IncludeSpecials = false;
            // New defaults
            EnableExternalResults = true;
            EnableMixed = false;
            UnreleasedBufferDays = 30;
            EnableLibraryPopulation = true;
            FFmpegProbeSize = "40M";
            FFmpegAnalyzeDuration = "5M";
            ItemsPerRequest = 100;
            LibraryPopulationHour = 3;
        }

        /// <summary>
        /// Gets or Sets TMDb API key for fetching metadata.
        /// </summary>
        public string TmdbApiKey { get; set; }

        /// <summary>
        /// Gets or Sets Movies library path for plugin-populated items.
        /// </summary>
        public string MoviesLibraryPath { get; set; }

        /// <summary>
        /// Gets or Sets number of search results for each movies and shows.
        /// </summary>
        public int SearchNumber { get; set; }

        /// <summary>
        /// Gets or Sets TV Shows library path for plugin-populated items.
        /// </summary>
        public string ShowsLibraryPath { get; set; }

        /// <summary>
        /// Gets or Sets Optional Anime library path.
        /// </summary>
        public string AnimeLibraryPath { get; set; }

        /// <summary>
        /// Gets or Sets addon manifest JSON URL.
        /// </summary>
        public string AddonManifestUrl { get; set; }

        /// <summary>
        /// Gets or sets the Jellyfin base URL (including protocol and port).
        /// Example: http://127.0.0.1:8096.
        /// </summary>
        public string JellyfinBaseUrl { get; set; } = "http://127.0.0.1:8096";

        /// <summary>
        /// Gets or sets a value indicating whether adult content is included.
        /// </summary>
        public bool IncludeAdult { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether unreleased titles are included.
        /// </summary>
        public bool IncludeUnreleased { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether specials (Season 0) are included.
        /// </summary>
        public bool IncludeSpecials { get; set; }

        /// <summary>
        /// Gets or Sets last population date and time.
        /// </summary>
        public DateTime? LastPopulationUtc { get; set; }

        // -----------------
        // New configuration
        // -----------------

        /// <summary>
        /// If true, search can include external results (e.g., addon sources).
        /// </summary>
        public bool EnableExternalResults { get; set; }

        /// <summary>
        /// If true, merge local files with external alternates (mixed mode).
        /// </summary>
        public bool EnableMixed { get; set; }

        /// <summary>
        /// Additional buffer days when deciding if unreleased movies should be shown.
        /// </summary>
        public int UnreleasedBufferDays { get; set; }

        /// <summary>
        /// FFmpeg probe size (e.g., 40M) for better remote stream detection.
        /// </summary>
        public string FFmpegProbeSize { get; set; }

        /// <summary>
        /// FFmpeg analyze duration (e.g., 5M) for better remote stream detection.
        /// </summary>
        public string FFmpegAnalyzeDuration { get; set; }

        /// <summary>
        /// Enable custom FFmpeg configuration. If false, uses Jellyfin defaults.
        /// </summary>
        public bool EnableFFmpegCustomization { get; set; } = true;

        /// <summary>
        /// Master switch to enable/disable any automated library population logic.
        /// </summary>
        public bool EnableLibraryPopulation { get; set; }

        /// <summary>
        /// Max number of items to fetch per request/batch when populating.
        /// </summary>
        public int ItemsPerRequest { get; set; }

        /// <summary>
        /// Gets or sets the hour (0-23 UTC) when daily library population should run.
        /// Default: 3 (3 AM UTC).
        /// </summary>
        public int LibraryPopulationHour { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to trigger a library scan after manual population.
        /// When enabled, Jellyfin will scan the library folders to discover newly created STRM files.
        /// When disabled, items are added directly to the database without scanning.
        /// Default: false (disabled for better performance on slower devices).
        /// </summary>
        public bool EnableAutoScanAfterPopulation { get; set; } = false;
    }
}
