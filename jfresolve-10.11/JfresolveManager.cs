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
    private readonly ConcurrentDictionary<Guid, (object Metadata, DateTime Added)> _metadataCache;
    private readonly SemaphoreSlim _insertLock = new SemaphoreSlim(1, 1);
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
    /// Uses GUID from metadata if provided, otherwise generates stable GUID
    /// Requires IMDB ID - items without IMDB ID should be filtered out before calling this
    /// </summary>
    public Movie IntoBaseItem(TmdbMovie tmdbMovie)
    {
        // Generate stable GUID from TMDB ID (like gelato://stub/{id})
        var itemId = GenerateJfresolveGuid("movie", tmdbMovie.Id);

        // Build the API controller URL for stream resolution
        var config = JfresolvePlugin.Instance?.Configuration;
        var serverUrl = config?.JellyfinServerUrl ?? "http://localhost:8096";
        var normalizedUrl = serverUrl.TrimEnd('/');

        // Use IMDB ID to construct API controller URL
        // Note: Items without IMDB ID should be filtered before calling this method
        var mediaPath = $"{normalizedUrl}/Plugins/Jfresolve/resolve/movie/{tmdbMovie.ImdbId}";
        _log.LogDebug("Jfresolve: Movie '{Title}' path set to {Path}", tmdbMovie.Title, mediaPath);

        var movie = new Movie
        {
            Id = itemId,
            Name = tmdbMovie.Title,
            OriginalTitle = tmdbMovie.OriginalTitle,
            Overview = tmdbMovie.Overview,
            ProductionYear = tmdbMovie.GetYear(),
            PremiereDate = tmdbMovie.GetReleaseDateTime(),
            CommunityRating = tmdbMovie.VoteAverage > 0 ? (float?)tmdbMovie.VoteAverage : null,
            Path = mediaPath, // Use API controller URL for stream resolution
            IsVirtualItem = false, // Set to false like Gelato (not a version)
        };

        // Set provider IDs (critical for matching/deduplication)
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbMovie.Id.ToString());
        if (!string.IsNullOrWhiteSpace(tmdbMovie.ImdbId))
        {
            movie.SetProviderId(MetadataProvider.Imdb, tmdbMovie.ImdbId);
        }
        movie.SetProviderId("Jfresolve", $"movie:{tmdbMovie.Id}");

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

    /// <summary>
    /// Convert TMDB TV show to BaseItem (like Gelato's IntoBaseItem)
    /// </summary>
    public Series IntoBaseItem(TmdbTvShow tmdbShow)
    {
        // Generate stable GUID from TMDB ID
        var itemId = GenerateJfresolveGuid("tv", tmdbShow.Id);

        var series = new Series
        {
            Id = itemId,
            Name = tmdbShow.Name,
            OriginalTitle = tmdbShow.OriginalName,
            Overview = tmdbShow.Overview,
            ProductionYear = tmdbShow.GetYear(),
            PremiereDate = tmdbShow.GetFirstAirDateTime(),
            CommunityRating = tmdbShow.VoteAverage > 0 ? (float?)tmdbShow.VoteAverage : null,
            Path = $"jfresolve://stub/{itemId}", // Virtual path pattern like Gelato
            IsVirtualItem = false,
        };

        // Set provider IDs
        series.SetProviderId(MetadataProvider.Tmdb, tmdbShow.Id.ToString());
        if (!string.IsNullOrWhiteSpace(tmdbShow.ImdbId))
        {
            series.SetProviderId(MetadataProvider.Imdb, tmdbShow.ImdbId);
        }
        series.SetProviderId("Jfresolve", $"tv:{tmdbShow.Id}");

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
    /// Generate stable GUID from type and ID (like Gelato's StremioUri.ToGuid())
    /// Uses MD5 hash to ensure consistency
    /// </summary>
    private Guid GenerateJfresolveGuid(string mediaType, int tmdbId)
    {
        var uniqueString = $"jfresolve://{mediaType}/{tmdbId}";
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(uniqueString));
        return new Guid(hash);
    }

    // ============ ITEM LOOKUP (Gelato pattern) ============

    public BaseItem? GetByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            HasAnyProviderId = providerIds,
            IsDeadPerson = true, // Skip filter marker (Gelato pattern)
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
        // Determine type and create BaseItem
        BaseItem baseItem;
        BaseItemKind kind;

        if (metadata is TmdbMovie tmdbMovie)
        {
            baseItem = IntoBaseItem(tmdbMovie);
            kind = BaseItemKind.Movie;
        }
        else if (metadata is TmdbTvShow tmdbShow)
        {
            baseItem = IntoBaseItem(tmdbShow);
            kind = BaseItemKind.Series;
        }
        else
        {
            _log.LogWarning("Jfresolve: Unknown metadata type, skipping");
            return (null, false);
        }

        // Check if missing provider IDs
        if (baseItem?.ProviderIds is not { Count: > 0 })
        {
            _log.LogWarning("Jfresolve: Missing provider ids, skipping");
            return (null, false);
        }

        // Use lock to prevent race condition when inserting the same item from multiple threads
        await _insertLock.WaitAsync(ct);
        try
        {
            // Check if already exists (Gelato pattern) - must check inside lock
            var existing = GetByProviderIds(baseItem.ProviderIds, kind);
            if (existing is not null)
            {
                _log.LogDebug(
                    "Jfresolve: found existing {Kind}: {Id} for {Name}",
                    existing.GetBaseItemKind(),
                    existing.Id,
                    baseItem.Name
                );
                return (existing, false);
            }

            // INSERT INTO DATABASE (Gelato pattern: parent.AddChild)
            if (kind == BaseItemKind.Movie)
            {
                parent.AddChild(baseItem);
                _log.LogInformation("Jfresolve: Inserted movie '{Name}' with ID {Id}", baseItem.Name, baseItem.Id);
            }
            else if (kind == BaseItemKind.Series)
            {
                parent.AddChild(baseItem);
                _log.LogInformation("Jfresolve: Inserted series '{Name}' with ID {Id}", baseItem.Name, baseItem.Id);

                // Create seasons/episodes BEFORE releasing lock to ensure they're created before repo update
                // This prevents race condition where client sees series before seasons are added
                if (metadata is TmdbTvShow tmdbShow2)
                {
                    await CreateSeasonsAndEpisodesForSeries((Series)baseItem, tmdbShow2, ct);
                }
            }
        }
        finally
        {
            _insertLock.Release();
        }

        // Update repository to ensure item is persisted (Gelato pattern)
        if (queueRefreshItem)
        {
            try
            {
                // Update the item in the repository to trigger notifications
                await baseItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

                // Also update the parent folder to ensure UI refreshes
                await parent.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

                // Queue a refresh for the item itself to ensure it appears in the library UI
                // This is critical for auto-population to work properly
                var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = true,
                    ReplaceAllMetadata = true,
                    ForceSave = true,
                };

                _provider.QueueRefresh(baseItem.Id, refreshOptions, RefreshPriority.High);

                _log.LogDebug("Jfresolve: Updated metadata and queued refresh for '{Name}'", baseItem.Name);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve: Failed to update metadata for '{Name}'", baseItem.Name);
            }
        }

        return (baseItem, true);
    }

    /// <summary>
    /// Creates real seasons and episodes for a series from TMDB data
    /// </summary>
    public async Task CreateSeasonsAndEpisodesForSeries(Series series, TmdbTvShow tmdbShow, CancellationToken ct)
    {
        try
        {
            var config = JfresolvePlugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                _log.LogWarning("Jfresolve: Cannot fetch seasons/episodes - TMDB API key not configured");
                return;
            }

            // Set PresentationUniqueKey on series (critical for Jellyfin to find episodes)
            series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
            var seriesPresentationKey = series.PresentationUniqueKey;

            _log.LogInformation("Jfresolve: Fetching seasons and episodes for series '{Name}' (TMDB ID: {TmdbId})",
                series.Name, tmdbShow.Id);

            // Get TV show details to find out how many seasons exist
            var tvDetails = await _tmdbService.GetTvDetailsAsync(tmdbShow.Id, config.TmdbApiKey);
            if (tvDetails == null || tvDetails.Seasons == null || tvDetails.Seasons.Count == 0)
            {
                _log.LogWarning("Jfresolve: No season information found for series '{Name}'", series.Name);
                return;
            }

            // Filter out special seasons (season 0)
            var regularSeasons = tvDetails.Seasons.Where(s => s.SeasonNumber > 0).ToList();
            _log.LogInformation("Jfresolve: Found {Count} seasons for series '{Name}'", regularSeasons.Count, series.Name);

            // Fetch and create each season with its episodes
            foreach (var seasonInfo in regularSeasons)
            {
                await CreateSeasonWithEpisodes(series, tmdbShow, seasonInfo, seriesPresentationKey, config, ct);
            }

            // Refresh metadata before the final update
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.None,
                ImageRefreshMode = MetadataRefreshMode.None,
                ReplaceAllImages = false,
                ReplaceAllMetadata = false,
                ForceSave = true,
            };

            await _provider.RefreshFullItem(series, options, CancellationToken.None);

            // Update the series to notify Jellyfin of all the new seasons
            await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

            _log.LogInformation("Jfresolve: Successfully created {Count} seasons for series '{Name}'",
                regularSeasons.Count, series.Name);
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
        CancellationToken ct)
    {
        try
        {
            // Check if season already exists
            var existingSeasons = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    IsDeadPerson = true,
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
            foreach (var tmdbEpisode in seasonDetails.Episodes)
            {
                await CreateEpisode(series, season, tmdbShow, tmdbEpisode, seriesPresentationKey, config, ct);
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
        CancellationToken ct)
    {
        try
        {
            // Check if episode already exists
            var existingEpisodes = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    ParentId = season.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    IsDeadPerson = true,
                })
                .OfType<Episode>()
                                .Where(e => e.IndexNumber == tmdbEpisode.EpisodeNumber)
                .ToList();

            if (existingEpisodes.Any())
            {
                _log.LogDebug("Jfresolve: Episode {EpisodeNumber} already exists for series '{Name}' Season {SeasonNumber}",
                    tmdbEpisode.EpisodeNumber, series.Name, season.IndexNumber);
                return Task.CompletedTask;
            }

            // Build the API controller URL for episode playback
            // Note: Series without IMDB ID should be filtered before reaching this point
            var serverUrl = config?.JellyfinServerUrl ?? "http://localhost:8096";
            var normalizedUrl = serverUrl.TrimEnd('/');
            var episodePath = $"{normalizedUrl}/Plugins/Jfresolve/resolve/series/{tmdbShow.ImdbId}?season={season.IndexNumber}&episode={tmdbEpisode.EpisodeNumber}";

            // Create episode
            var episode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = tmdbEpisode.Name,
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

            _log.LogDebug("Jfresolve: Created Episode S{Season}E{Episode} '{Name}' with path {Path}",
                season.IndexNumber, tmdbEpisode.EpisodeNumber, tmdbEpisode.Name, episodePath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Failed to create episode {EpisodeNumber} for series '{Name}' Season {SeasonNumber}",
                tmdbEpisode.EpisodeNumber, series.Name, season.IndexNumber);
        }

        return Task.CompletedTask;
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
