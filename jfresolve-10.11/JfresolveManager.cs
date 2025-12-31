using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jfresolve;

/// <summary>
/// Manages virtual items and search functionality for the Jfresolve plugin
/// Based on Gelato's pattern
/// </summary>
public class JfresolveManager
{
    private readonly ILogger<JfresolveManager> _log;
    private readonly TmdbService _tmdbService;
    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _repo;
    private readonly IProviderManager _provider;
    private readonly IFileSystem _fileSystem;

    public IProviderManager Provider => _provider;
    public IFileSystem FileSystem => _fileSystem;
    private readonly ConcurrentDictionary<Guid, (object Metadata, DateTime Added)> _metadataCache;
    private readonly SemaphoreSlim _insertLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DateTime> _syncCache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(2);
    private const int MAX_CACHE_SIZE = 500; // Limit cache to 500 items (~5 MB max)

    public JfresolveManager(
        ILogger<JfresolveManager> log,
        TmdbService tmdbService,
        ILibraryManager libraryManager,
        IItemRepository repo,
        IProviderManager provider,
        IFileSystem fileSystem)
    {
        _log = log;
        _tmdbService = tmdbService;
        _libraryManager = libraryManager;
        _repo = repo;
        _provider = provider;
        _fileSystem = fileSystem;
        _metadataCache = new ConcurrentDictionary<Guid, (object, DateTime)>();
    }

    // ============ CACHE MANAGEMENT (Gelato pattern) ============

    public void SaveTmdbMetadata(Guid guid, object meta)
    {
        // If cache is full, remove oldest entries (10% of max size)
        if (_metadataCache.Count >= MAX_CACHE_SIZE)
        {
            var entriesToRemove = (int)(MAX_CACHE_SIZE * 0.1); // Remove 10% to avoid frequent evictions
            var oldestEntries = _metadataCache
                .OrderBy(x => x.Value.Added)
                .Take(entriesToRemove)
                .Select(x => x.Key)
                .ToList();

            foreach (var key in oldestEntries)
            {
                _metadataCache.TryRemove(key, out _);
            }

            _log.LogDebug("Jfresolve: Cache full, evicted {Count} oldest entries (cache size: {Size}/{Max})",
                entriesToRemove, _metadataCache.Count, MAX_CACHE_SIZE);
        }

        _metadataCache.TryAdd(guid, (meta, DateTime.UtcNow));
        _log.LogDebug("Jfresolve: Cached metadata for {Guid} (cache size: {Size}/{Max})",
            guid, _metadataCache.Count, MAX_CACHE_SIZE);
    }

    public T? GetTmdbMetadata<T>(Guid guid) where T : class
    {
        return _metadataCache.TryGetValue(guid, out var value) ? value.Metadata as T : null;
    }

    public void RemoveTmdbMetadata(Guid guid)
    {
        _metadataCache.TryRemove(guid, out _);
        _log.LogDebug("Jfresolve: Removed cached metadata for {Guid} (cache size: {Size})",
            guid, _metadataCache.Count);
    }

    // ============ GUID REPLACEMENT (Gelato pattern) ============

    public void ReplaceGuid(ActionContext ctx, Guid value)
    {
        // Replace route values
        var rd = ctx.RouteData.Values;
        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                _log.LogDebug("Jfresolve: Replacing route {Key} {Old} → {New}", key, raw, value);
                ctx.RouteData.Values[key] = value.ToString();
            }
        }

        // Replace action arguments (critical for the current request)
        if (ctx is Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext execCtx)
        {
            var args = execCtx.ActionArguments;
            foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
            {
                if (args.TryGetValue(key, out var raw) && raw is not null)
                {
                    _log.LogDebug("Jfresolve: Replacing action argument {Key} {Old} → {New}", key, raw, value);
                    args[key] = value;
                }
            }
        }

        // Replace query string "ids"
        var request = ctx.HttpContext.Request;
        var parsed = QueryHelpers.ParseQuery(request.QueryString.Value ?? "");

        if (parsed.TryGetValue("ids", out var existing) && existing.Count == 1)
        {
            _log.LogDebug("Jfresolve: Replacing query ids {Old} → {New}", existing[0], value);

            var dict = new Dictionary<string, StringValues>(parsed)
            {
                ["ids"] = new StringValues(value.ToString()),
            };

            ctx.HttpContext.Request.QueryString = QueryString.Create(dict);
        }
    }

    // ============ FOLDER MANAGEMENT (Gelato pattern) ============

    /// <summary>
    /// Seeds a folder with a marker file to trigger library scans (Gelato pattern)
    /// </summary>
    public static void SeedFolder(string path)
    {
        Directory.CreateDirectory(path);
        var seed = Path.Combine(path, ".ignore");
        File.WriteAllText(
            seed,
            "This is a seed file created by Jfresolve so that library scans are triggered. Do not remove."
        );
    }

    public Folder? TryGetMovieFolder()
    {
        return TryGetFolder(JfresolvePlugin.Instance!.Configuration.MoviePath);
    }

    public Folder? TryGetSeriesFolder()
    {
        return TryGetFolder(JfresolvePlugin.Instance!.Configuration.SeriesPath);
    }

    public Folder? TryGetFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _log.LogDebug("Jfresolve: TryGetFolder called with empty path");
            return null;
        }

        _log.LogWarning("Jfresolve: TryGetFolder looking for path: {Path}", path);

        // Seed the folder to ensure it exists and triggers library scans
        SeedFolder(path);

        try
        {
            // Query using IItemRepository directly (Gelato pattern)
            var query = new InternalItemsQuery
            {
                Path = path,
                IsDeadPerson = true, // Skip filter marker (Gelato pattern)
            };

            var allItems = _repo.GetItemList(query);
            _log.LogWarning("Jfresolve: Query returned {Count} items for path '{Path}'", allItems.Count, path);

            if (allItems.Count > 0)
            {
                foreach (var item in allItems)
                {
                    _log.LogWarning("Jfresolve: Found item - Type: {Type}, Name: {Name}, Path: {ItemPath}, IsFolder: {IsFolder}",
                        item.GetType().Name, item.Name, item.Path, item is Folder);
                }
            }
            else
            {
                // Try querying without Path filter to see if folder exists anywhere
                _log.LogWarning("Jfresolve: No items found with Path='{Path}', trying alternative query...", path);
                var altQuery = new InternalItemsQuery
                {
                    IsDeadPerson = true,
                };
                var allFolders = _repo.GetItemList(altQuery).OfType<Folder>().ToList();
                _log.LogWarning("Jfresolve: Found {Count} total folders in database", allFolders.Count);

                // Find folders with matching path
                var matchingFolders = allFolders.Where(f =>
                    f.Path != null && f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();

                if (matchingFolders.Any())
                {
                    _log.LogWarning("Jfresolve: Found {Count} folders with matching path via alternative query", matchingFolders.Count);
                    foreach (var f in matchingFolders.Take(3))
                    {
                        _log.LogWarning("Jfresolve: Matching folder - Type: {Type}, Name: {Name}, Path: {FolderPath}",
                            f.GetType().Name, f.Name, f.Path);
                    }
                    return matchingFolders.First();
                }

                // Log some sample folder paths to help debug
                _log.LogWarning("Jfresolve: Sample folder paths in database:");
                foreach (var f in allFolders.Take(5))
                {
                    _log.LogWarning("Jfresolve:   - {Name}: {FolderPath}", f.Name, f.Path);
                }
            }

            var folder = allItems.OfType<Folder>().FirstOrDefault();

            if (folder is null)
            {
                _log.LogWarning(
                    "Jfresolve: No folder found at path '{Path}'. This library must be scanned at least once. " +
                    "Go to: Dashboard → Libraries → [Your Library] → Scan Library (the 3-dot menu), then wait for completion.",
                    path
                );
            }
            else
            {
                _log.LogDebug("Jfresolve: Found folder '{Name}' (Type: {Type}) at path '{Path}'",
                    folder.Name, folder.GetType().Name, path);
            }

            return folder;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Cannot deserialize unknown type"))
        {
            _log.LogError(ex,
                "Jfresolve: Database has orphaned items from deleted plugins. The folder at path '{Path}' cannot be queried. " +
                "Please scan your library to rebuild the database: Dashboard → Libraries → [Your Library] → Scan Library (the 3-dot menu).",
                path);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Unexpected error querying for folder at path '{Path}'. Please scan your library first.", path);
            return null;
        }
    }

    /// <summary>
    /// Get anime folder if anime support is enabled
    /// </summary>
    public Folder? TryGetAnimeFolder()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null || !config.EnableAnimeFolder || string.IsNullOrWhiteSpace(config.AnimePath))
        {
            return null;
        }

        return TryGetFolder(config.AnimePath);
    }

    // ============ MODE-BASED PATH RESOLUTION ============

    /// <summary>
    /// Get movie folder for search operations based on configuration mode
    /// </summary>
    public Folder? TryGetMovieFolderForSearch()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null) return null;

        if (config.PathMode == Configuration.PathConfigMode.Advanced)
        {
            return TryGetFolder(config.MovieSearchPath);
        }
        return TryGetFolder(config.MoviePath);
    }

    /// <summary>
    /// Get series folder for search operations based on configuration mode
    /// </summary>
    public Folder? TryGetSeriesFolderForSearch()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null) return null;

        if (config.PathMode == Configuration.PathConfigMode.Advanced)
        {
            return TryGetFolder(config.SeriesSearchPath);
        }
        return TryGetFolder(config.SeriesPath);
    }

    /// <summary>
    /// Get anime folder for search operations based on configuration mode
    /// </summary>
    public Folder? TryGetAnimeFolderForSearch()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null) return null;

        if (config.PathMode == Configuration.PathConfigMode.Advanced)
        {
            if (!config.EnableAnimeFolderAdvanced || string.IsNullOrWhiteSpace(config.AnimeSearchPath))
                return null;
            return TryGetFolder(config.AnimeSearchPath);
        }
        else
        {
            if (!config.EnableAnimeFolder || string.IsNullOrWhiteSpace(config.AnimePath))
                return null;
            return TryGetFolder(config.AnimePath);
        }
    }

    /// <summary>
    /// Get movie folder for auto-populate operations based on configuration mode
    /// </summary>
    public Folder? TryGetMovieFolderForAutoPopulate()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null) return null;

        if (config.PathMode == Configuration.PathConfigMode.Advanced)
        {
            return TryGetFolder(config.MovieAutoPopulatePath);
        }
        return TryGetFolder(config.MoviePath);
    }

    /// <summary>
    /// Get series folder for auto-populate operations based on configuration mode
    /// </summary>
    public Folder? TryGetSeriesFolderForAutoPopulate()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null) return null;

        if (config.PathMode == Configuration.PathConfigMode.Advanced)
        {
            return TryGetFolder(config.SeriesAutoPopulatePath);
        }
        return TryGetFolder(config.SeriesPath);
    }

    /// <summary>
    /// Get anime folder for auto-populate operations based on configuration mode
    /// </summary>
    public Folder? TryGetAnimeFolderForAutoPopulate()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null) return null;

        if (config.PathMode == Configuration.PathConfigMode.Advanced)
        {
            if (!config.EnableAnimeFolderAdvanced || string.IsNullOrWhiteSpace(config.AnimeAutoPopulatePath))
                return null;
            return TryGetFolder(config.AnimeAutoPopulatePath);
        }
        else
        {
            if (!config.EnableAnimeFolder || string.IsNullOrWhiteSpace(config.AnimePath))
                return null;
            return TryGetFolder(config.AnimePath);
        }
    }

    // ============ SEARCH (returns cached virtual items) ============

    /// <summary>
    /// Search for items using TMDB API
    /// </summary>
    public async Task<List<BaseItem>> SearchTmdbAsync(string searchTerm, BaseItemKind itemKind)
    {
        var results = new List<BaseItem>();
        var config = JfresolvePlugin.Instance?.Configuration;

        // Check if TMDB API key is configured
        if (config == null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            _log.LogWarning("Jfresolve: TMDB API key not configured. Please configure it in plugin settings.");
            return results;
        }

        _log.LogInformation("Jfresolve: Searching TMDB for {ItemType} matching '{Query}'", itemKind, searchTerm);

        if (itemKind == BaseItemKind.Movie)
        {
            // Search TMDB for movies
            var tmdbResults = await _tmdbService.SearchMoviesAsync(
                searchTerm,
                config.TmdbApiKey,
                config.IncludeAdult
            );

            // Convert TMDB results to BaseItems and cache metadata
            // Only include movies with IMDB IDs
            foreach (var tmdbMovie in tmdbResults.Take(config.SearchResultLimit))
            {
                // Skip movies without IMDB ID
                if (string.IsNullOrWhiteSpace(tmdbMovie.ImdbId))
                {
                    _log.LogDebug("Jfresolve: Skipping movie '{Title}' - no IMDB ID", tmdbMovie.Title);
                    continue;
                }

                var movie = IntoBaseItem(tmdbMovie);
                SaveTmdbMetadata(movie.Id, tmdbMovie);
                results.Add(movie);
            }

            _log.LogInformation("Jfresolve: Returning {Count} movie results from TMDB for '{Query}' (filtered by IMDB ID)",
                results.Count, searchTerm);
        }
        else if (itemKind == BaseItemKind.Series)
        {
            // Search TMDB for TV shows
            var tmdbResults = await _tmdbService.SearchTvShowsAsync(
                searchTerm,
                config.TmdbApiKey,
                config.IncludeAdult
            );

            // Convert TMDB results to BaseItems and cache metadata
            // Only include TV shows with IMDB IDs
            foreach (var tmdbShow in tmdbResults.Take(config.SearchResultLimit))
            {
                // Skip TV shows without IMDB ID
                if (string.IsNullOrWhiteSpace(tmdbShow.ImdbId))
                {
                    _log.LogDebug("Jfresolve: Skipping TV show '{Name}' - no IMDB ID", tmdbShow.Name);
                    continue;
                }

                var series = IntoBaseItem(tmdbShow);
                SaveTmdbMetadata(series.Id, tmdbShow);
                results.Add(series);
            }

            _log.LogInformation("Jfresolve: Returning {Count} TV show results from TMDB for '{Query}' (filtered by IMDB ID)",
                results.Count, searchTerm);
        }

        return results;
    }

    // ============ INTO BASE ITEM (Gelato pattern) ============

    /// <summary>
    /// Convert TMDB movie to BaseItem (like Gelato's IntoBaseItem)
    /// </summary>
    public Movie IntoBaseItem(TmdbMovie tmdbMovie, string quality = "", int index = 0)
    {
        // Generate stable GUID from TMDB ID, quality and index
        var itemId = GenerateJfresolveGuid("movie", tmdbMovie.Id, quality, index);

        // Build the API controller URL for stream resolution
        var config = JfresolvePlugin.Instance?.Configuration;
        var serverUrl = config?.JellyfinServerUrl ?? "http://localhost:8096";
        var normalizedUrl = serverUrl.TrimEnd('/');

        // Construct API controller URL with quality and index parameters
        var mediaPath = $"{normalizedUrl}/Plugins/Jfresolve/resolve/movie/{tmdbMovie.ImdbId}";
        if (!string.IsNullOrEmpty(quality))
        {
            mediaPath += mediaPath.Contains('?') ? $"&quality={Uri.EscapeDataString(quality)}" : $"?quality={Uri.EscapeDataString(quality)}";
        }
        if (index > 0)
        {
            mediaPath += mediaPath.Contains('?') ? $"&index={index}" : $"?index={index}";
        }

        // Apply quality tag to Name ONLY for virtual items (not the primary item)
        // Primary item gets clean name, virtual items get quality tags
        var name = tmdbMovie.Title;
        var shouldLockName = false;

        if (!string.IsNullOrEmpty(quality))
        {
            var qualityTag = GetQualityDisplayTag(quality);
            name += index > 0 ? $" [{qualityTag} #{index + 1}]" : $" [{qualityTag}]";
            shouldLockName = true; // Lock name for quality items to prevent metadata refresh from removing tag
        }

        var movie = new Movie
        {
            Id = itemId,
            Name = name,
            OriginalTitle = tmdbMovie.OriginalTitle,
            Overview = tmdbMovie.Overview,
            ProductionYear = tmdbMovie.GetYear(),
            PremiereDate = tmdbMovie.GetReleaseDateTime(),
            CommunityRating = tmdbMovie.VoteAverage > 0 ? (float?)tmdbMovie.VoteAverage : null,
            Path = mediaPath,
            // Container removed - let Jellyfin detect it from the actual stream
            IsVirtualItem = false,
        };

        // Lock the Name field for quality items to prevent metadata refresh from overwriting
        if (shouldLockName)
        {
            movie.LockedFields = new[] { MetadataField.Name };
        }

        // Set provider IDs
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbMovie.Id.ToString());
        if (!string.IsNullOrWhiteSpace(tmdbMovie.ImdbId))
        {
            movie.SetProviderId(MetadataProvider.Imdb, tmdbMovie.ImdbId);
        }
        movie.SetProviderId("Jfresolve", $"movie:{tmdbMovie.Id}:{quality}:{index}");

        // Add poster and backdrop images
        var images = new List<ItemImageInfo>();

        if (!string.IsNullOrWhiteSpace(tmdbMovie.PosterPath))
        {
            images.Add(new ItemImageInfo
            {
                Type = ImageType.Primary,
                Path = tmdbMovie.GetPosterUrl()
            });
        }

        if (!string.IsNullOrWhiteSpace(tmdbMovie.BackdropPath))
        {
            images.Add(new ItemImageInfo
            {
                Type = ImageType.Backdrop,
                Path = tmdbMovie.GetBackdropUrl()!
            });
        }

        if (images.Count > 0)
        {
            movie.ImageInfos = images.ToArray();
        }

        return movie;
    }

    private string GetQualityDisplayTag(string quality)
    {
        return quality.ToLowerInvariant() switch
        {
            "4k" or "2160p" => "4K",
            "1080p" => "1080p",
            "720p" => "720p",
            "unknown" => "SD",
            _ => quality
        };
    }

    /// <summary>
    /// Convert TMDB TV show to BaseItem (like Gelato's IntoBaseItem)
    /// </summary>
    public Series IntoBaseItem(TmdbTvShow tmdbShow, string quality = "", int index = 0)
    {
        // Generate stable GUID from TMDB ID, quality and index
        var itemId = GenerateJfresolveGuid("tv", tmdbShow.Id, quality, index);

        // Naming for series (versions aren't usually named at the series level, but we use the same ID logic)
        var name = tmdbShow.Name;

        var series = new Series
        {
            Id = itemId,
            Name = name,
            OriginalTitle = tmdbShow.OriginalName,
            Overview = tmdbShow.Overview,
            ProductionYear = tmdbShow.GetYear(),
            PremiereDate = tmdbShow.GetFirstAirDateTime(),
            CommunityRating = tmdbShow.VoteAverage > 0 ? (float?)tmdbShow.VoteAverage : null,
            Path = $"jfresolve://stub/{itemId}",
            IsVirtualItem = false,
        };

        // Set provider IDs
        series.SetProviderId(MetadataProvider.Tmdb, tmdbShow.Id.ToString());
        if (!string.IsNullOrWhiteSpace(tmdbShow.ImdbId))
        {
            series.SetProviderId(MetadataProvider.Imdb, tmdbShow.ImdbId);
        }
        series.SetProviderId("Jfresolve", $"tv:{tmdbShow.Id}:{quality}:{index}");

        // Add poster and backdrop images
        var images = new List<ItemImageInfo>();

        if (!string.IsNullOrWhiteSpace(tmdbShow.PosterPath))
        {
            images.Add(new ItemImageInfo
            {
                Type = ImageType.Primary,
                Path = tmdbShow.GetPosterUrl()
            });
        }

        if (!string.IsNullOrWhiteSpace(tmdbShow.BackdropPath))
        {
            images.Add(new ItemImageInfo
            {
                Type = ImageType.Backdrop,
                Path = tmdbShow.GetBackdropUrl()!
            });
        }

        if (images.Count > 0)
        {
            series.ImageInfos = images.ToArray();
        }

        return series;
    }

    /// <summary>
    /// Generate stable GUID from type, ID, quality and index
    /// </summary>
    private Guid GenerateJfresolveGuid(string mediaType, int tmdbId, string quality = "", int index = 0)
    {
        var uniqueString = $"jfresolve://{mediaType}/{tmdbId}";
        if (!string.IsNullOrEmpty(quality))
        {
            uniqueString += $"/{quality}";
        }
        if (index > 0)
        {
            uniqueString += $"/{index}";
        }

        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(uniqueString));
        return new Guid(hash);
    }

    // ============ ITEM LOOKUP (Gelato pattern) ============

    public BaseItem? GetByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind)
    {
        // If we have a Jfresolve ID, we MUST match on it exactly to support versioning.
        // This prevents different quality versions (which share TMDB/IMDB IDs) from matching each other.
        if (providerIds.TryGetValue("Jfresolve", out var jfId))
        {
            var jfQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                Recursive = true,
                IsDeadPerson = true
            };

            // Search specifically for items that have THIS Jfresolve ID
            var items = _libraryManager.GetItemList(jfQuery);
            var match = items.FirstOrDefault(i => i.ProviderIds.TryGetValue("Jfresolve", out var existingId) && existingId == jfId);

            // If we found a match by Jfresolve ID, return it.
            // If we have a Jfresolve ID but NO match was found, STOP HERE and return null.
            // This signals that THIS SPECIFIC VERSION needs to be created.
            return match;
        }

        // Fallback to broad search for non-versioned items (e.g. initial search result metadata)
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            HasAnyProviderId = providerIds,
            IsDeadPerson = true,
        };

        return _libraryManager.GetItemList(query).FirstOrDefault();
    }

    public BaseItem? GetExistingItem(Dictionary<string, string> providerIds, BaseItemKind kind)
    {
        return GetByProviderIds(providerIds, kind);
    }

    // ============ INSERT META (Gelato pattern - CORE METHOD) ============

    /// <summary>
    /// Inserts metadata into the library. Skip if it already exists.
    /// This is the core insertion method copied from Gelato's InsertMeta
    /// </summary>
public async Task<(BaseItem? Item, bool Created)> InsertMeta(
    Guid guid,
    Folder parent,
    object metadata,
    bool queueRefreshItem,
    CancellationToken ct)
{
    var tasks = GetEnabledVersioningTasks();
    BaseItem? firstItem = null;
    bool anyCreated = false;

    foreach (var task in tasks)
    {
        // Determine type and create BaseItem
        BaseItem baseItem;
        BaseItemKind kind;

        if (metadata is TmdbMovie tmdbMovie)
        {
            // First item gets NO quality tag (clean title), others get quality tags
            var quality = firstItem == null ? "" : task.Quality;
            var index = firstItem == null ? 0 : task.Index;

            baseItem = IntoBaseItem(tmdbMovie, quality, index);
            kind = BaseItemKind.Movie;
        }
        else if (metadata is TmdbTvShow tmdbShow)
        {
            // For series, we only create ONE series item (no quality versioning for TV shows)
            // Jellyfin doesn't handle multiple series or episode versions properly
            // We only process the first task for the series item itself
            if (task != tasks[0]) continue;

            baseItem = IntoBaseItem(tmdbShow); // Primary series item
            kind = BaseItemKind.Series;
        }
        else
        {
            _log.LogWarning("Jfresolve: Unknown metadata type, skipping");
            return (null, false);
        }

        if (baseItem?.ProviderIds is not { Count: > 0 })
        {
            _log.LogWarning("Jfresolve: Missing provider ids, skipping");
            continue;
        }

        // Mark all items after the first as virtual (Gelato pattern)
        // This hides them from library listings but makes them available as versions
        if (firstItem != null)
        {
            baseItem.IsVirtualItem = true;
        }

        // Prevent duplicate inserts
        await _insertLock.WaitAsync(ct);
        try
        {
            var existing = GetByProviderIds(baseItem.ProviderIds, kind);
            if (existing is not null)
            {
                _log.LogDebug(
                    "Jfresolve: found existing {Kind}: {Id} for {Name}",
                    existing.GetBaseItemKind(),
                    existing.Id,
                    baseItem.Name
                );
                if (firstItem == null) firstItem = existing;
                continue;
            }

            // Insert into container
            parent.AddChild(baseItem);
            _log.LogDebug("Jfresolve: Inserted {Kind} '{Name}' with ID {Id}",
                kind, baseItem.Name, baseItem.Id);
            anyCreated = true;
            if (firstItem == null) firstItem = baseItem;

            // Create seasons/episodes before releasing lock
            if (kind == BaseItemKind.Series && metadata is TmdbTvShow tmdbShow2)
            {
                await CreateSeasonsAndEpisodesForSeries((Series)baseItem, tmdbShow2, ct);
            }
        }
        finally
        {
            _insertLock.Release();
        }

        // Save images only for the first version created (artwork is identical)
        if (anyCreated && task == tasks[0])
        {
            try
            {
                if (metadata is TmdbMovie movieMeta)
                {
                    await SaveImagesForItem(baseItem, movieMeta, ct);
                }
                else if (metadata is TmdbTvShow showMeta)
                {
                    await SaveImagesForItem(baseItem, showMeta, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Jfresolve: Failed to save images for '{Name}'", baseItem.Name);
            }
        }

        // Update repository
        if (queueRefreshItem)
        {
            try
            {
                // Basic update without full refresh for secondary items
                await baseItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct);

                // Only trigger full refresh for the first item to ensure immediate UI visibility
                if (task == tasks[0])
                {
                    var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.None,
                        ImageRefreshMode = MetadataRefreshMode.None,
                        ReplaceAllImages = false,
                        ReplaceAllMetadata = false,
                        ForceSave = true
                    };
                    await _provider.RefreshFullItem(baseItem, options, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve: Failed to update item '{Name}'", baseItem.Name);
            }
        }
    }

    if (queueRefreshItem && anyCreated && firstItem != null)
    {
        try
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.None,
                ImageRefreshMode = MetadataRefreshMode.None,
                ForceSave = true
            };
            await parent.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
            await _provider.RefreshFullItem(parent, options, ct);
        }
        catch { }
    }

    return (firstItem, anyCreated);
}



/// <summary>
/// Save image with retry logic to handle file locking issues (exponential backoff)
/// </summary>
private async Task SaveImageWithRetry(BaseItem item, string url, ImageType imageType, CancellationToken ct)
{
    const int maxRetries = 5;
    var delays = new[] { 500, 1000, 2000, 5000, 10000, 15000 }; // milliseconds

    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        try
        {
            await _provider.SaveImage(item, url, imageType, null, ct);
            return; // Success
        }
        catch (IOException ioEx) when (attempt < maxRetries && ioEx.Message.Contains("being used by another process"))
        {
            var delay = delays[attempt];
            _log.LogWarning(
                "Jfresolve: Image file locked for '{Name}' ({ImageType}), retrying in {Delay}ms (attempt {Attempt}/{Max})",
                item.Name, imageType, delay, attempt + 1, maxRetries);

            await Task.Delay(delay, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Jfresolve: Failed to save {ImageType} image for '{Name}' from {Url}",
                imageType, item.Name, url);
            throw;
        }
    }

    _log.LogError(
        "Jfresolve: Failed to save {ImageType} image for '{Name}' after {MaxRetries} attempts (file still locked)",
        imageType, item.Name, maxRetries);
}

    private async Task SaveImageToLocation(byte[] data, string path, CancellationToken ct)
    {
        // Get or create a lock for this specific file path to prevent concurrent writes
        var pathLock = _pathLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(ct);

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(path, data, ct);
        }
        finally
        {
            pathLock.Release();
        }
    }

private async Task SaveImagesForItem(BaseItem item, TmdbMovie meta, CancellationToken ct)
{
    var poster = meta.GetPosterUrl();
    if (!string.IsNullOrWhiteSpace(poster))
    {
        await SaveImageWithRetry(item, poster, ImageType.Primary, ct);
    }

    var backdrop = meta.GetBackdropUrl();
    if (!string.IsNullOrWhiteSpace(backdrop))
    {
        await SaveImageWithRetry(item, backdrop, ImageType.Backdrop, ct);
    }
}

private async Task SaveImagesForItem(BaseItem item, TmdbTvShow meta, CancellationToken ct)
{
    var poster = meta.GetPosterUrl();
    if (!string.IsNullOrWhiteSpace(poster))
    {
        await SaveImageWithRetry(item, poster, ImageType.Primary, ct);
    }

    var backdrop = meta.GetBackdropUrl();
    if (!string.IsNullOrWhiteSpace(backdrop))
    {
        await SaveImageWithRetry(item, backdrop, ImageType.Backdrop, ct);
    }
}


    /// <summary>
    /// Creates real seasons and episodes for a series from TMDB data
    /// </summary>
    public async Task CreateSeasonsAndEpisodesForSeries(Series series, TmdbTvShow tmdbShow, CancellationToken ct)
    {
        // Avoid redundant syncs
        var now = DateTime.UtcNow;
        if (_syncCache.TryGetValue(series.Id, out var lastSync))
        {
            if (now - lastSync < CacheExpiry)
            {
                _log.LogDebug("Jfresolve: Skipping sync for {Name} - synced {Seconds} seconds ago",
                    series.Name, (now - lastSync).TotalSeconds);
                return;
            }
        }

        // Get or create a lock for this specific item
        var itemLock = _itemLocks.GetOrAdd(series.Id, _ => new SemaphoreSlim(1, 1));
        await itemLock.WaitAsync(ct);

        try
        {
            // Re-check cache inside lock in case another thread just finished
            if (_syncCache.TryGetValue(series.Id, out lastSync))
            {
                if (now - lastSync < CacheExpiry)
                {
                    return;
                }
            }

            _log.LogInformation("Jfresolve: Fetching seasons and episodes for series '{Name}' (TMDB ID: {TmdbId})",
                series.Name, tmdbShow.Id);

            var config = JfresolvePlugin.Instance?.Configuration;
            if (config == null) return;

            // Updated cache time
            _syncCache[series.Id] = now;

            var fullShow = await _tmdbService.GetTvDetailsAsync(tmdbShow.Id, config.TmdbApiKey);
            if (fullShow?.Seasons == null)
            {
                _log.LogWarning("Jfresolve: No seasons found for tv show details '{Name}'", series.Name);
                return;
            }

            // Set PresentationUniqueKey on series (critical for Jellyfin to find episodes)
            series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
            var seriesPresentationKey = series.PresentationUniqueKey;

            var versionTasks = GetEnabledVersioningTasks();

            foreach (var tmdbSeason in fullShow.Seasons)
            {
                await CreateSeasonWithEpisodes(series, tmdbShow, tmdbSeason, seriesPresentationKey, config, ct, versionTasks);
            }

            _log.LogInformation("Jfresolve: Successfully created seasons for series '{Name}'", series.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Failed to create seasons/episodes for series '{Name}'", series.Name);
        }
    }

    /// <summary>
    /// Creates a season and all its episodes from TMDB data
    /// </summary>
    public async Task CreateSeasonWithEpisodes(
        Series series,
        TmdbTvShow tmdbShow,
        TmdbSeasonInfo seasonInfo,
        string seriesPresentationKey,
        Configuration.PluginConfiguration config,
        CancellationToken ct,
        List<(string Quality, int Index)> versionTasks)
    {
        try
        {
            // Check if season already exists
            var existingSeasons = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    Recursive = false,
                })
                .OfType<Season>()
                .Where(s => s.IndexNumber == seasonInfo.SeasonNumber)
                .ToList();

            Season season;
            if (existingSeasons.Any())
            {
                season = existingSeasons.First();
                _log.LogDebug("Jfresolve: Season {SeasonNumber} already exists for series '{Name}'",
                    seasonInfo.SeasonNumber, series.Name);
            }
            else
            {
                // Create season
                season = new Season
                {
                    Id = Guid.NewGuid(),
                    Name = seasonInfo.Name ?? $"Season {seasonInfo.SeasonNumber}",
                    IndexNumber = seasonInfo.SeasonNumber,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = $"{series.Path}:Season{seasonInfo.SeasonNumber}",
                    IsVirtualItem = false,
                    SeriesPresentationUniqueKey = seriesPresentationKey,
                    Overview = seasonInfo.Overview,
                    PremiereDate = seasonInfo.GetSeasonAirDateTime(),
                };

                // Copy provider IDs to season
                foreach (var providerId in series.ProviderIds)
                {
                    season.SetProviderId(providerId.Key, providerId.Value);
                }

                // Add season to series
                series.AddChild(season);

                _log.LogInformation("Jfresolve: Created Season {SeasonNumber} for series '{Name}'",
                    seasonInfo.SeasonNumber, series.Name);
            }

            // Fetch episode details for this season
            var seasonDetails = await _tmdbService.GetSeasonDetailsAsync(tmdbShow.Id, seasonInfo.SeasonNumber, config.TmdbApiKey);
            if (seasonDetails == null || seasonDetails.Episodes == null || seasonDetails.Episodes.Count == 0)
            {
                _log.LogWarning("Jfresolve: No episodes found for series '{Name}' Season {SeasonNumber}",
                    series.Name, seasonInfo.SeasonNumber);
                return;
            }

            _log.LogInformation("Jfresolve: Creating {Count} episodes for series '{Name}' Season {SeasonNumber}",
                seasonDetails.Episodes.Count, series.Name, seasonInfo.SeasonNumber);

            // Create all episodes for this season
            // Note: Quality versioning removed for episodes - Jellyfin doesn't handle episode versions well
            foreach (var tmdbEpisode in seasonDetails.Episodes)
            {
                // Create only one episode per episode number (no quality versions)
                await CreateEpisode(series, season, tmdbShow, tmdbEpisode, seriesPresentationKey, config, ct, "", 0);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Failed to create season {SeasonNumber} for series '{Name}'",
                seasonInfo.SeasonNumber, series.Name);
        }
    }

    /// <summary>
    /// Creates a single episode with proper API controller path
    /// </summary>
    public Task CreateEpisode(
        Series series,
        Season season,
        TmdbTvShow tmdbShow,
        TmdbEpisode tmdbEpisode,
        string seriesPresentationKey,
        Configuration.PluginConfiguration config,
        CancellationToken ct,
        string quality = "",
        int index = 0)
    {
        try
        {
            // Build the unique identifier part for this version
            var versionSuffix = "";
            if (!string.IsNullOrEmpty(quality))
            {
                var qualityTag = GetQualityDisplayTag(quality);
                versionSuffix = index > 0 ? $" [{qualityTag} #{index + 1}]" : $" [{qualityTag}]";
            }

            // Check if episode version already exists
            var existingEpisodes = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    ParentId = season.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = false,
                })
                .OfType<Episode>()
                .Where(e => e.IndexNumber == tmdbEpisode.EpisodeNumber && e.Name.EndsWith(versionSuffix))
                .ToList();

            if (existingEpisodes.Any())
            {
                _log.LogDebug("Jfresolve: Episode {EpisodeNumber}{Version} already exists for series '{Name}' Season {SeasonNumber}",
                    tmdbEpisode.EpisodeNumber, versionSuffix, series.Name, season.IndexNumber);
                return Task.CompletedTask;
            }

            // Generate stable GUID for this episode version
            // Combine episode info into unique string then hash
            var episodeSeed = $"tv:{tmdbShow.Id}:{season.IndexNumber}:{tmdbEpisode.EpisodeNumber}:{quality}:{index}";
            var episodeId = GenerateJfresolveGuid("episode", tmdbShow.Id, quality, (season.IndexNumber ?? 0) * 1000 + (tmdbEpisode.EpisodeNumber) + index * 10000);

            // Build the API controller URL for episode playback
            var serverUrl = config?.JellyfinServerUrl ?? "http://localhost:8096";
            var normalizedUrl = serverUrl.TrimEnd('/');
            var episodePath = $"{normalizedUrl}/Plugins/Jfresolve/resolve/series/{tmdbShow.ImdbId}?season={season.IndexNumber}&episode={tmdbEpisode.EpisodeNumber}";
            if (!string.IsNullOrEmpty(quality))
            {
                episodePath += $"&quality={Uri.EscapeDataString(quality)}";
            }
            if (index > 0)
            {
                episodePath += $"&index={index}";
            }

            // Create episode
            var episode = new Episode
            {
                Id = episodeId,
                Name = tmdbEpisode.Name + versionSuffix,
                SeriesId = series.Id,
                SeriesName = series.Name,
                SeasonId = season.Id,
                SeasonName = season.Name,
                IndexNumber = tmdbEpisode.EpisodeNumber,
                ParentIndexNumber = season.IndexNumber,
                Path = episodePath,
                IsVirtualItem = false,
                Overview = tmdbEpisode.Overview,
                PremiereDate = tmdbEpisode.GetAirDateTime(),
                CommunityRating = tmdbEpisode.VoteAverage > 0 ? (float?)tmdbEpisode.VoteAverage : null,
                SeriesPresentationUniqueKey = seriesPresentationKey,
            };

            // Set PresentationUniqueKey on episode
            episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();

            // Copy provider IDs from series
            foreach (var providerId in series.ProviderIds)
            {
                episode.SetProviderId(providerId.Key, providerId.Value);
            }
            episode.SetProviderId("Jfresolve", episodeSeed);

            // Add episode image if available
            if (!string.IsNullOrWhiteSpace(tmdbEpisode.StillPath))
            {
                episode.ImageInfos = new[]
                {
                    new ItemImageInfo
                    {
                        Type = ImageType.Primary,
                        Path = tmdbEpisode.GetStillUrl()!
                    }
                };
            }

            // Add episode to season
            season.AddChild(episode);

            _log.LogDebug("Jfresolve: Created Episode S{Season}E{Episode}{Version} '{Name}' with path {Path}",
                season.IndexNumber, tmdbEpisode.EpisodeNumber, versionSuffix, tmdbEpisode.Name, episodePath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Failed to create episode {EpisodeNumber} for series '{Name}' Season {SeasonNumber}",
                tmdbEpisode.EpisodeNumber, series.Name, season.IndexNumber);
        }

        return Task.CompletedTask;
    }

    public List<(string Quality, int Index)> GetEnabledVersioningTasks()
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        var tasks = new List<(string, int)>();
        if (config == null) return tasks;

        var qualities = new List<string>();
        if (config.Enable4KVersion) qualities.Add("4K");
        if (config.Enable1080pVersion) qualities.Add("1080p");
        if (config.Enable720pVersion) qualities.Add("720p");
        if (config.EnableUnknownVersion) qualities.Add("Unknown");

        // If no qualities are enabled, add a default empty quality task
        if (qualities.Count == 0)
        {
            tasks.Add(("", 0));
            return tasks;
        }

        var maxItems = Math.Clamp(config.MaxItemsPerQuality, 1, 10);
        foreach (var quality in qualities)
        {
            for (int i = 0; i < maxItems; i++)
            {
                tasks.Add((quality, i));
            }
        }

        return tasks;
    }

    // ============ DELETE SUPPORT ============

    /// <summary>
    /// Check if item is a Jfresolve virtual item
    /// </summary>
    public bool IsJfresolve(BaseItem item)
    {
        var jfresolveId = item.GetProviderId("Jfresolve");
        return !string.IsNullOrWhiteSpace(jfresolveId);
    }

    /// <summary>
    /// Check if user can delete the item
    /// We only check permissions since Jellyfin excludes remote items by default
    /// </summary>
    public virtual bool CanDelete(BaseItem item, User user)
    {
        var allCollectionFolders = _libraryManager.GetUserRootFolder().Children.OfType<Folder>().ToList();
        return item.IsAuthorizedToDelete(user, allCollectionFolders);
    }
}
