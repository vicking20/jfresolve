// JfResolveManager.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jfresolve.Configuration;
using Jfresolve.Provider;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jfresolve
{
    /// <summary>
    /// Manages plugin operations including scheduled library population and item insertion.
    /// </summary>
    public class JfResolveManager : IDisposable
    {
        private readonly PluginConfiguration _config;
        private readonly ILogger _logger;
        private readonly ILibraryManager? _libraryManager;
        private readonly IProviderManager? _providerManager;
        private readonly IFileSystem? _fileSystem;
        private System.Timers.Timer? _populationTimer;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="JfResolveManager"/> class.
        /// </summary>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager for querying items (optional).</param>
        /// <param name="providerManager">Provider manager for refreshing metadata (optional).</param>
        /// <param name="fileSystem">File system for directory operations (optional).</param>
        public JfResolveManager(
            PluginConfiguration config,
            ILogger logger,
            ILibraryManager? libraryManager = null,
            IProviderManager? providerManager = null,
            IFileSystem? fileSystem = null)
        {
            _config = config;
            _logger = logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            InitializeScheduler();
        }

        /// <summary>
        /// Initializes the library population scheduler.
        /// </summary>
        private void InitializeScheduler()
        {
            if (!_config.EnableLibraryPopulation)
            {
                _logger.LogInformation("[MANAGER] Library population is disabled");
                return;
            }

            _logger.LogInformation("[MANAGER] Initializing library population scheduler (3 AM UTC daily)");

            // Check every 10 minutes if we should populate
            _populationTimer = new System.Timers.Timer(TimeSpan.FromMinutes(10).TotalMilliseconds);
            _populationTimer.Elapsed += async (s, e) => await CheckAndPopulateAsync().ConfigureAwait(false);
            _populationTimer.AutoReset = true;
            _populationTimer.Start();
        }

        /// <summary>
        /// Checks if it's 3 AM UTC and populates library if needed.
        /// </summary>
        private async Task CheckAndPopulateAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                var targetHour = 3;

                // Check if it's 3 AM and hasn't run today yet
                if (now.Hour == targetHour && (_config.LastPopulationUtc?.Date != now.Date))
                {
                    _logger.LogInformation("[MANAGER] Starting scheduled library population at {Time:HH:mm} UTC", now);
                    await PopulateLibraryAsync().ConfigureAwait(false);
                    _config.LastPopulationUtc = now;
                    JfresolvePlugin.Instance?.SaveConfiguration();
                    _logger.LogInformation("[MANAGER] Scheduled library population completed at {Time:HH:mm} UTC", now);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MANAGER] Error checking for scheduled population");
            }
        }

        /// <summary>
        /// Populates the library immediately.
        /// If EnableAutoScanAfterPopulation is true, triggers a library scan after population.
        /// </summary>
        public async Task PopulateLibraryAsync()
        {
            if (!_config.EnableLibraryPopulation)
            {
                _logger.LogWarning("[MANAGER] Library population is disabled in configuration");
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.TmdbApiKey))
            {
                _logger.LogWarning("[MANAGER] TMDB API key not configured - skipping population");
                return;
            }

            try
            {
                using var populator = new JfResolvePopulator(_config, _logger);
                await populator.PopulateLibrariesAsync().ConfigureAwait(false);

                // Only trigger library refresh if explicitly enabled in configuration
                // This allows users on slower devices to disable automatic scanning
                if (_config.EnableAutoScanAfterPopulation)
                {
                    try
                    {
                        _logger.LogInformation("[MANAGER] EnableAutoScanAfterPopulation is true, triggering library refresh");
                        await TriggerLibraryRefreshAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[MANAGER] Library refresh after population failed, but continuing");
                    }
                }
                else
                {
                    _logger.LogInformation("[MANAGER] EnableAutoScanAfterPopulation is false, skipping library refresh (STRM files will be discovered on next scheduled scan)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MANAGER] Error during library population");
            }
        }

        /// <summary>
        /// Gets external metadata from cache by Guid.
        /// </summary>
        public ExternalMeta? GetExternalMeta(Guid guid, JfresolveProvider provider)
        {
            return provider.MetaCache.TryGetValue(guid, out var cachedEntry) ? cachedEntry.Meta : null;
        }

        /// <summary>
        /// Saves external metadata to cache by Guid.
        /// </summary>
        public void SaveExternalMeta(Guid guid, ExternalMeta meta, JfresolveProvider provider)
        {
            provider.MetaCache[guid] = new CachedMetaEntry(meta);
        }

        /// <summary>
        /// Removes external metadata from cache.
        /// </summary>
        public void RemoveExternalMeta(Guid guid, JfresolveProvider provider)
        {
            provider.MetaCache.Remove(guid);
        }

        /// <summary>
        /// Finds an existing item in the library by provider IDs.
        /// Returns null if not found.
        /// </summary>
        public BaseItem? GetByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind)
        {
            if (_libraryManager == null)
            {
                _logger.LogWarning("[MANAGER] Library manager not available for duplicate detection");
                return null;
            }

            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { kind },
                    HasAnyProviderId = providerIds,
                    Recursive = true
                };

                return _libraryManager.GetItemList(q).FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MANAGER] Error querying by provider IDs");
                return null;
            }
        }

        /// <summary>
        /// Inserts an external item into the library.
        /// Returns the created/found item and whether it was newly created.
        /// </summary>
        public Task<(BaseItem?, bool)> InsertExternalMeta(
            ExternalMeta meta,
            BaseItemKind kind,
            CancellationToken ct)
        {
            try
            {
                // Validate metadata
                if (meta == null || meta.TmdbId <= 0 || string.IsNullOrWhiteSpace(meta.Name))
                {
                    _logger.LogError("[MANAGER] Invalid metadata, cannot insert");
                    return Task.FromResult<(BaseItem?, bool)>((null, false));
                }

                // Create the base item
                BaseItem? baseItem = null;
                var isVirtualItem = true;

                switch (kind)
                {
                    case BaseItemKind.Movie:
                        baseItem = new Movie
                        {
                            Name = meta.Name,
                            ProductionYear = meta.Year,
                            Overview = meta.Description,
                            IsVirtualItem = isVirtualItem
                        };
                        break;

                    case BaseItemKind.Series:
                        baseItem = new Series
                        {
                            Name = meta.Name,
                            ProductionYear = meta.Year,
                            Overview = meta.Description,
                            IsVirtualItem = isVirtualItem
                        };
                        break;

                    default:
                        _logger.LogWarning("[MANAGER] Unsupported item kind: {Kind}", kind);
                        return Task.FromResult<(BaseItem?, bool)>((null, false));
                }

                if (baseItem == null)
                    return Task.FromResult<(BaseItem?, bool)>((null, false));

                // Set provider IDs
                var providerIds = meta.GetProviderIds();
                if (providerIds.Count > 0)
                {
                    foreach (var kvp in providerIds)
                    {
                        baseItem.SetProviderId(kvp.Key, kvp.Value);
                    }
                }

                // Set poster image
                if (!string.IsNullOrEmpty(meta.Poster))
                {
                    baseItem.ImageInfos = new List<ItemImageInfo>
                    {
                        new ItemImageInfo { Path = meta.Poster, Type = ImageType.Primary }
                    }.ToArray();
                }

                // Generate a unique ID
                baseItem.Id = Guid.NewGuid();

                _logger.LogInformation("[MANAGER] Inserted new {Kind}: {Name} (ID: {Id})", kind, meta.Name, baseItem.Id);

                return Task.FromResult<(BaseItem?, bool)>((baseItem, true));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MANAGER] Error inserting external meta for {Name}", meta?.Name);
                return Task.FromResult<(BaseItem?, bool)>((null, false));
            }
        }

        /// <summary>
        /// Triggers a library refresh to discover newly created STRM files.
        /// This ensures Jellyfin immediately detects files added during population or item insertion.
        /// </summary>
        private async Task TriggerLibraryRefreshAsync()
        {
            try
            {
                if (_libraryManager == null)
                {
                    _logger.LogDebug("[MANAGER] Library manager not available for refresh");
                    return;
                }

                _logger.LogInformation("[MANAGER] Triggering library refresh to discover new STRM files");

                // Use the library manager to validate/refresh the library
                // This will cause Jellyfin to scan for new STRM files
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                var progress = new Progress<double>(percent =>
                {
                    if (percent % 25 == 0) // Log every 25%
                    {
                        _logger.LogDebug("[MANAGER] Library refresh progress: {Percent}%", (int)(percent * 100));
                    }
                });

                await Task.Run(() => _libraryManager.ValidateMediaLibrary(progress, cts.Token)).ConfigureAwait(false);

                _logger.LogInformation("[MANAGER] Library refresh completed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[MANAGER] Library refresh timed out after 60 seconds");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MANAGER] Error triggering library refresh");
                // Don't throw - library refresh is non-critical
            }
        }

        /// <summary>
        /// Releases resources used by this manager.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                if (_populationTimer != null)
                {
                    _populationTimer.Stop();
                    _populationTimer.Dispose();
                    _populationTimer = null;
                }
            }

            _disposed = true;
        }
    }
}
