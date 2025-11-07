using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jfresolve.Configuration;
using Jfresolve.Provider;
using Jfresolve.Utilities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jfresolve.Filters
{
    /// <summary>
    /// Intercepts item detail requests to handle insertion of external search results.
    /// When a user clicks on a TMDB search result, this filter:
    /// 1. Retrieves the cached external metadata
    /// 2. Checks if the item already exists in the library
    /// 3. If not found, creates STRM files for the item
    /// 4. If found, redirects to the library item's Guid
    /// </summary>
    public class InsertActionFilter : IAsyncActionFilter, IOrderedFilter
    {
        private readonly ILogger<InsertActionFilter> _logger;
        private readonly JfresolveProvider _provider;
        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;
        private readonly StrmFileGenerator _strmGenerator;
        private readonly IFileSystem _fileSystem;

        public int Order => 1;

        public InsertActionFilter(
            ILogger<InsertActionFilter> logger,
            JfresolveProvider provider,
            ILibraryManager libraryManager,
            IDtoService dtoService,
            StrmFileGenerator strmGenerator,
            IFileSystem fileSystem)
        {
            _logger = logger;
            _provider = provider;
            _libraryManager = libraryManager;
            _dtoService = dtoService;
            _strmGenerator = strmGenerator;
            _fileSystem = fileSystem;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
        {
            // Only handle GetItem/GetItems/GetPlaybackInfo actions
            if (!IsItemsAction(ctx))
            {
                await next();
                return;
            }

            // Try to extract the item Guid from the request
            if (!RouteParameterExtractor.TryGetItemIdWithQueryFallback(ctx, out var guid))
            {
                await next();
                return;
            }

            // Try to get cached external metadata
            if (!_provider.MetaCache.TryGetValue(guid, out var cachedEntry) || cachedEntry == null)
            {
                // Not a TMDB search result, proceed normally
                await next();
                return;
            }

            // Check if cache entry has expired (older than 5 minutes)
            // This prevents stale cache entries from being used after deletion
            if (cachedEntry.IsExpired())
            {
                _logger.LogInformation("[InsertActionFilter] Cache entry for item expired (older than 5 minutes); clearing and proceeding normally");
                _provider.MetaCache.Remove(guid);
                await next();
                return;
            }

            var externalMeta = cachedEntry.Meta;
            _logger.LogInformation("[InsertActionFilter] Found cached metadata for {Name}", externalMeta.Name);

            // Convert to BaseItem to get provider IDs
            var baseItem = _provider.IntoBaseItem(externalMeta);
            if (baseItem == null)
            {
                _logger.LogWarning("[InsertActionFilter] Failed to convert metadata to BaseItem");
                await next();
                return;
            }

            // Check if item already exists in library by provider IDs
            var providerIds = baseItem.ProviderIds;
            if (providerIds == null || providerIds.Count == 0)
            {
                _logger.LogWarning("[InsertActionFilter] No provider IDs available, cannot check for duplicates");
                await next();
                return;
            }

            var itemKind = baseItem.GetBaseItemKind();

            // Check for duplicate by querying library
            var existingItem = GetByProviderIds(providerIds, itemKind);

            if (existingItem != null)
            {
                // Item already in library, redirect to it
                _logger.LogInformation(
                    "[InsertActionFilter] Item already exists in library; redirecting to {Id}",
                    existingItem.Id);

                // For Series items, check if there are new episodes to add
                if (externalMeta.Type == "Series" && existingItem is Series)
                {
                    try
                    {
                        var updateResult = await _strmGenerator.UpdateSeriesStrmAsync(externalMeta);
                        if (!string.IsNullOrEmpty(updateResult))
                        {
                            _logger.LogInformation("[InsertActionFilter] Series {Name} was updated with new episodes, triggering library refresh", externalMeta.Name);
                            // Trigger library refresh to discover new episodes
                            await TriggerLibraryRefreshAsync(updateResult);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[InsertActionFilter] Error updating series {Name}", externalMeta.Name);
                        // Continue with normal flow even if update fails
                    }
                }

                ReplaceGuid(ctx, existingItem.Id);
                _provider.MetaCache.Remove(guid);
                await next();
                return;
            }

            // Item not in library - create STRM files and redirect to library item
            _logger.LogInformation("[InsertActionFilter] Item not found in library, creating STRM files for: {Name}", externalMeta.Name);

            try
            {
                // Create STRM files based on item type
                string? libraryFolder = externalMeta.Type switch
                {
                    "Movie" => await _strmGenerator.CreateMovieStrmAsync(externalMeta),
                    "Series" => await _strmGenerator.CreateSeriesStrmAsync(externalMeta),
                    _ => null
                };

                if (libraryFolder == null)
                {
                    _logger.LogWarning("[InsertActionFilter] Failed to create STRM files for {Name}", externalMeta.Name);
                    // Fallback: serve cached item as DTO
                    var options = new DtoOptions
                    {
                        Fields = new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.Overview },
                        EnableImages = true,
                    };
                    var dto = _dtoService.GetBaseItemDto(baseItem, options);
                    ctx.Result = new OkObjectResult(dto);
                    return;
                }

                // STRM files created successfully - now we need to make the item REAL
                _logger.LogInformation("[InsertActionFilter] STRM files created for {Name}, now making item real in database", externalMeta.Name);

                // Create a real database item with the same GUID as the cached item
                // This ensures continuity between cached search result and database item
                try
                {
                    var realItem = CreateDatabaseItem(baseItem, guid, externalMeta);
                    if (realItem != null)
                    {
                        _logger.LogInformation("[InsertActionFilter] Created real database item for {Name} with ID {Id}", externalMeta.Name, realItem.Id);

                        // Trigger library refresh immediately so Jellyfin discovers the new STRM files
                        // This prevents playback issues where Jellyfin can't find the newly created item
                        try
                        {
                            await TriggerLibraryRefreshAsync(libraryFolder);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[InsertActionFilter] Library refresh failed, but continuing with item creation");
                        }

                        ReplaceGuid(ctx, realItem.Id);
                        _provider.MetaCache.Remove(guid);
                        await next();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[InsertActionFilter] Error creating real database item for {Name}", externalMeta.Name);
                }

                // Fallback: serve cached item as DTO if database creation fails
                _logger.LogWarning("[InsertActionFilter] Fallback: serving cached item DTO for {Name}", externalMeta.Name);
                var dtoOptions = new DtoOptions
                {
                    Fields = new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.Overview },
                    EnableImages = true,
                };
                var resultDto = _dtoService.GetBaseItemDto(baseItem, dtoOptions);
                ctx.Result = new OkObjectResult(resultDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InsertActionFilter] Error creating STRM files for item: {Name}", externalMeta.Name);
                await next();
            }
        }

        /// <summary>
        /// Checks if the action is one we should intercept.
        /// </summary>
        private bool IsItemsAction(ActionExecutingContext ctx)
        {
            if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
                return false;

            return string.Equals(cad.ActionName, "GetItems", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cad.ActionName, "GetItem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cad.ActionName, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cad.ActionName, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cad.ActionName, "GetPlaybackInfo", StringComparison.OrdinalIgnoreCase);
        }



        /// <summary>
        /// Replaces the item Guid in the route with a new value.
        /// This causes Jellyfin to serve the real library item instead of the TMDB result.
        /// </summary>
        private void ReplaceGuid(ActionExecutingContext ctx, Guid value)
        {
            var rd = ctx.RouteData.Values;

            // Replace route values
            foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
            {
                if (rd.TryGetValue(key, out var raw) && raw is not null)
                {
                    _logger.LogDebug("[InsertActionFilter] Replacing route {Key} {Old} → {New}", key, raw, value);
                    rd[key] = value.ToString();

                    // Also update action arguments if present
                    if (ctx.ActionArguments.ContainsKey(key))
                    {
                        ctx.ActionArguments[key] = value;
                    }
                }
            }

            // Expose resolved Guid for downstream consumers
            ctx.HttpContext.Items["GuidResolved"] = value;
        }

        /// <summary>
        /// Polls the library for an item matching the given provider IDs.
        /// This is called after STRM file creation to wait for Jellyfin's library scanner to discover it.
        /// </summary>
        private async Task<BaseItem?> PollForLibraryItemAsync(Dictionary<string, string> providerIds, BaseItemKind kind, int timeoutMs = 5000)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            const int pollIntervalMs = 200; // Check every 200ms

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                var item = GetByProviderIds(providerIds, kind);
                if (item != null)
                {
                    _logger.LogInformation("[InsertActionFilter] Found library item after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                    return item;
                }

                // Wait before next poll
                await Task.Delay(pollIntervalMs).ConfigureAwait(false);
            }

            _logger.LogDebug("[InsertActionFilter] Library item not found after {TimeoutMs}ms polling", timeoutMs);
            return null;
        }

        /// <summary>
        /// Creates a real database item from a cached virtual item.
        /// This is called when user clicks on a search result to add it to the library.
        /// The item's Path is set to the STRM file location so Jellyfin can find and stream it.
        /// </summary>
        private BaseItem? CreateDatabaseItem(BaseItem baseItem, Guid preferredId, ExternalMeta externalMeta)
        {
            try
            {
                // Try to use the preferred ID (from search result) for continuity
                // If that fails, Jellyfin will generate a new one
                baseItem.Id = preferredId;
                baseItem.IsVirtualItem = false; // Make it real

                // CRITICAL: Set the Path to point to the STRM file on disk
                // Jellyfin will read this file and use its content as the stream URL
                var config = JfresolvePlugin.Instance?.Configuration;
                if (config != null)
                {
                    var strmPath = GetStrmFilePath(baseItem, config, externalMeta);
                    if (!string.IsNullOrEmpty(strmPath))
                    {
                        baseItem.Path = strmPath;
                        _logger.LogInformation("[InsertActionFilter] Setting item path to STRM file: {Path}", strmPath);
                    }
                }

                // Add to library - this persists it to the database
                _libraryManager.CreateItem(baseItem, null);

                _logger.LogInformation("[InsertActionFilter] Successfully created database item: {Name} (ID: {Id})", baseItem.Name, baseItem.Id);
                return baseItem;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InsertActionFilter] Failed to create database item for {Name}", baseItem.Name);
                return null;
            }
        }

        /// <summary>
        /// Gets the path for an item in the library.
        /// For movies and episodes, returns the resolver URL.
        /// For series, returns the folder path where STRM files are stored so Jellyfin can scan for episodes.
        /// Respects anime configuration - anime items use the anime library path if configured.
        /// </summary>
        private string? GetStrmFilePath(BaseItem item, PluginConfiguration config, ExternalMeta externalMeta)
        {
            if (item == null || config == null) return null;

            // Handle different item types
            if (item is Series series)
            {
                // Series root item - set Path to the folder where STRM files are stored
                // This allows Jellyfin to scan the folder and discover all episode STRM files
                var folderName = FileNameUtility.BuildSeriesFolderName(series.Name, series.ProductionYear);

                // Determine which library path to use based on anime classification
                string libraryPath = config.ShowsLibraryPath;
                if (externalMeta.IsAnime && !string.IsNullOrWhiteSpace(config.AnimeLibraryPath))
                {
                    libraryPath = config.AnimeLibraryPath;
                    _logger.LogInformation("[InsertActionFilter] Using anime library path for series: {Title}", series.Name);
                }

                var seriesPath = Path.Combine(libraryPath, folderName);

                _logger.LogInformation("[InsertActionFilter] Using series folder path: {Path}", seriesPath);
                return seriesPath;
            }
            else if (item is Episode episode)
            {
                // Episode item - include season and episode parameters for resolver
                var imdbId = item.GetProviderId("Imdb");
                if (string.IsNullOrEmpty(imdbId))
                {
                    _logger.LogWarning("[InsertActionFilter] Episode {Name} has no IMDB ID, cannot create resolver URL", item.Name);
                    return null;
                }

                var seasonNumber = episode.ParentIndexNumber ?? 0;
                var episodeNumber = episode.IndexNumber ?? 0;
                var resolverUrl = UrlBuilder.BuildSeriesResolverUrl(config.JellyfinBaseUrl, imdbId, seasonNumber, episodeNumber);

                _logger.LogInformation("[InsertActionFilter] Using resolver URL for episode: {Url}", resolverUrl);
                return resolverUrl;
            }
            else
            {
                // Movie or other type - use resolver URL
                var imdbId = item.GetProviderId("Imdb");
                if (string.IsNullOrEmpty(imdbId))
                {
                    _logger.LogWarning("[InsertActionFilter] Item {Name} has no IMDB ID, cannot create resolver URL", item.Name);
                    return null;
                }

                var resolverUrl = UrlBuilder.BuildMovieResolverUrl(config.JellyfinBaseUrl, imdbId);

                _logger.LogInformation("[InsertActionFilter] Using resolver URL for movie: {Url}", resolverUrl);
                return resolverUrl;
            }
        }

        /// <summary>
        /// Triggers a targeted library refresh for a specific folder containing STRM files.
        /// This is a partial/lightweight refresh that only scans the specified folder,
        /// not the entire library. Much better for slow devices than ValidateMediaLibrary.
        /// </summary>
        private async Task TriggerLibraryRefreshAsync(string folderPath)
        {
            try
            {
                _logger.LogInformation("[InsertActionFilter] Triggering targeted folder refresh for: {Path}", folderPath);

                // Try to find the specific folder by path
                var targetFolder = _libraryManager.FindByPath(folderPath, isFolder: true);

                if (targetFolder == null)
                {
                    _logger.LogWarning("[InsertActionFilter] Could not find folder by path: {Path}", folderPath);
                    return;
                }

                // Cast to Folder to access ValidateChildren method
                if (targetFolder is not Folder folder)
                {
                    _logger.LogWarning("[InsertActionFilter] Found item is not a folder: {Path}", folderPath);
                    return;
                }

                _logger.LogInformation("[InsertActionFilter] Found folder, starting validation: {Name} ({Path})", folder.Name, folder.Path);

                // Create metadata refresh options for non-intrusive validation
                var metadataOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    ReplaceAllImages = false,
                    ReplaceAllMetadata = false,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly
                };

                // Validate children of the folder (this only scans that specific folder)
                // recursive=true will scan all subfolders, but only within this folder, not the entire library
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var progress = new Progress<double>(percent =>
                {
                    if (percent % 25 == 0) // Log every 25%
                    {
                        _logger.LogDebug("[InsertActionFilter] Folder validation progress: {Percent}%", (int)(percent * 100));
                    }
                });

                await folder.ValidateChildren(progress, metadataOptions, recursive: true, allowRemoveRoot: false, cancellationToken: cts.Token)
                    .ConfigureAwait(false);

                _logger.LogInformation("[InsertActionFilter] Targeted folder refresh completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InsertActionFilter] Error triggering folder refresh for: {Path}", folderPath);
                // Don't throw - this is a non-critical operation
            }
        }

        /// <summary>
        /// Finds an existing item in the library by provider IDs.
        /// Also validates that the item's path still exists on disk to prevent orphaned references.
        /// </summary>
        private BaseItem? GetByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind)
        {
            try
            {
                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { kind },
                    HasAnyProviderId = providerIds,
                    Recursive = true
                };

                var items = _libraryManager.GetItemList(q);

                // Validate that the item's path still exists
                // This prevents returning orphaned database references for deleted items
                foreach (var item in items)
                {
                    if (!string.IsNullOrEmpty(item.Path))
                    {
                        // Check if the path (file or folder) exists
                        if (File.Exists(item.Path) || Directory.Exists(item.Path))
                        {
                            _logger.LogDebug("[InsertActionFilter] Found library item with valid path: {Path}", item.Path);
                            return item;
                        }
                        else
                        {
                            _logger.LogDebug("[InsertActionFilter] Found library item but path no longer exists: {Path} - skipping", item.Path);
                        }
                    }
                }

                _logger.LogDebug("[InsertActionFilter] No items found with valid paths for provider IDs");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InsertActionFilter] Error querying by provider IDs");
                return null;
            }
        }

        /// <summary>
        /// Creates a BaseItem from external metadata.
        /// </summary>
        private BaseItem CreateItemFromMeta(ExternalMeta meta, BaseItemKind kind)
        {
            BaseItem item = kind switch
            {
                BaseItemKind.Movie => new Movie
                {
                    Id = Guid.NewGuid(),
                    Name = meta.Name,
                    ProductionYear = meta.Year,
                    Overview = meta.Description,
                    IsVirtualItem = true
                },
                BaseItemKind.Series => new Series
                {
                    Id = Guid.NewGuid(),
                    Name = meta.Name,
                    ProductionYear = meta.Year,
                    Overview = meta.Description,
                    IsVirtualItem = true
                },
                _ => throw new NotSupportedException($"Item kind {kind} not supported")
            };

            // Set provider IDs
            var providerIds = meta.GetProviderIds();
            foreach (var kvp in providerIds)
            {
                item.SetProviderId(kvp.Key, kvp.Value);
            }

            // Set poster
            if (!string.IsNullOrEmpty(meta.Poster))
            {
                item.ImageInfos = new List<ItemImageInfo>
                {
                    new ItemImageInfo { Path = meta.Poster, Type = ImageType.Primary }
                }.ToArray();
            }

            return item;
        }
    }
}
