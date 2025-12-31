using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jfresolve.ScheduledTasks;

/// <summary>
/// Scheduled task to automatically populate the Jfresolve library with trending/popular content from TMDB.
/// </summary>
public sealed class PopulateLibraryTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PopulateLibraryTask> _log;
    private readonly JfresolveManager _jfresolveManager;
    private readonly TmdbService _tmdbService;

    public PopulateLibraryTask(
        ILibraryManager libraryManager,
        ILogger<PopulateLibraryTask> logger,
        JfresolveManager jfresolveManager,
        TmdbService tmdbService)
    {
        _libraryManager = libraryManager;
        _log = logger;
        _jfresolveManager = jfresolveManager;
        _tmdbService = tmdbService;
    }

    public string Name => "Populate Jfresolve Library";

    public string Key => "PopulateJfresolveLibrary";

    public string Description => "Automatically populates the Jfresolve library with trending or popular content from TMDB based on configuration settings.";

    public string Category => "Jfresolve";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null)
        {
            _log.LogError("Jfresolve: Configuration not available");
            return;
        }

        // Check if auto-population is enabled
        if (!config.EnableAutoPopulation)
        {
            _log.LogInformation("Jfresolve: Auto-population is disabled in configuration");
            return;
        }

        // Check TMDB API key
        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            _log.LogError("Jfresolve: TMDB API key not configured");
            return;
        }

        _log.LogInformation(
            "Jfresolve: Starting auto-population - Source: {Source}, Limit: {Limit}",
            config.PopulationSource,
            config.PopulationResultLimit);

        // Parse exclusion list
        var excludedIds = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(config.ExclusionList))
        {
            var ids = config.ExclusionList.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var idStr in ids)
            {
                if (int.TryParse(idStr.Trim(), out var id))
                {
                    excludedIds.Add(id);
                }
            }
            _log.LogInformation("Jfresolve: Loaded {Count} excluded TMDB IDs", excludedIds.Count);
        }

        try
        {
            var moviesAdded = 0;
            var seriesAdded = 0;
            var skippedDuplicates = 0;

            // Fetch content from multiple sources based on configuration
            progress.Report(10);

            var allMovies = new List<TmdbMovie>();
            var allTvShows = new List<TmdbTvShow>();

            // Fetch from Trending source if enabled
            if (config.UseTrendingSource)
            {
                _log.LogInformation("Jfresolve: Fetching trending content from TMDB...");
                var trendingMovies = await _tmdbService.GetTrendingMoviesAsync(
                    config.TmdbApiKey,
                    "week",
                    config.IncludeAdult);
                var trendingTvShows = await _tmdbService.GetTrendingTvShowsAsync(
                    config.TmdbApiKey,
                    "week",
                    config.IncludeAdult);

                allMovies.AddRange(trendingMovies);
                allTvShows.AddRange(trendingTvShows);
                _log.LogInformation("Jfresolve: Added {MovieCount} trending movies and {TvCount} trending TV shows",
                    trendingMovies.Count, trendingTvShows.Count);
            }

            progress.Report(25);

            // Fetch from Popular source if enabled
            if (config.UsePopularSource)
            {
                _log.LogInformation("Jfresolve: Fetching popular content from TMDB...");
                var popularMovies = await _tmdbService.GetPopularMoviesAsync(
                    config.TmdbApiKey,
                    config.IncludeAdult);
                var popularTvShows = await _tmdbService.GetPopularTvShowsAsync(
                    config.TmdbApiKey,
                    config.IncludeAdult);

                allMovies.AddRange(popularMovies);
                allTvShows.AddRange(popularTvShows);
                _log.LogInformation("Jfresolve: Added {MovieCount} popular movies and {TvCount} popular TV shows",
                    popularMovies.Count, popularTvShows.Count);
            }

            progress.Report(40);

            // Fetch from Top Rated source if enabled
            if (config.UseTopRatedSource)
            {
                _log.LogInformation("Jfresolve: Fetching top rated content from TMDB...");
                var topRatedMovies = await _tmdbService.GetTopRatedMoviesAsync(
                    config.TmdbApiKey,
                    config.IncludeAdult);
                var topRatedTvShows = await _tmdbService.GetTopRatedTvShowsAsync(
                    config.TmdbApiKey,
                    config.IncludeAdult);

                allMovies.AddRange(topRatedMovies);
                allTvShows.AddRange(topRatedTvShows);
                _log.LogInformation("Jfresolve: Added {MovieCount} top rated movies and {TvCount} top rated TV shows",
                    topRatedMovies.Count, topRatedTvShows.Count);
            }

            // If no sources are enabled, default to Trending for backward compatibility
            if (!config.UseTrendingSource && !config.UsePopularSource && !config.UseTopRatedSource)
            {
                _log.LogWarning("Jfresolve: No content sources enabled, defaulting to Trending");
                var defaultMovies = await _tmdbService.GetTrendingMoviesAsync(
                    config.TmdbApiKey,
                    "week",
                    config.IncludeAdult);
                var defaultTvShows = await _tmdbService.GetTrendingTvShowsAsync(
                    config.TmdbApiKey,
                    "week",
                    config.IncludeAdult);

                allMovies.AddRange(defaultMovies);
                allTvShows.AddRange(defaultTvShows);
            }

            // Remove duplicates (same TMDB ID might appear in multiple sources)
            var moviesToProcess = allMovies
                .GroupBy(m => m.Id)
                .Select(g => g.First())
                .ToList();
            var tvShowsToProcess = allTvShows
                .GroupBy(tv => tv.Id)
                .Select(g => g.First())
                .ToList();

            _log.LogInformation("Jfresolve: Total unique content after deduplication: {MovieCount} movies, {TvCount} TV shows",
                moviesToProcess.Count, tvShowsToProcess.Count);

            progress.Report(50);

            // Apply filters: unreleased content
            if (config.FilterUnreleased)
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-config.UnreleasedBufferDays);
                var beforeFilterMovies = moviesToProcess.Count;
                var beforeFilterTv = tvShowsToProcess.Count;

                moviesToProcess = moviesToProcess.Where(m =>
                {
                    var releaseDate = m.GetReleaseDateTime();
                    return releaseDate.HasValue && releaseDate.Value <= cutoffDate;
                }).ToList();

                tvShowsToProcess = tvShowsToProcess.Where(tv =>
                {
                    var releaseDate = tv.GetFirstAirDateTime();
                    return releaseDate.HasValue && releaseDate.Value <= cutoffDate;
                }).ToList();

                _log.LogInformation(
                    "Jfresolve: Filtered unreleased content - Movies: {BeforeMovies} -> {AfterMovies}, TV: {BeforeTv} -> {AfterTv}",
                    beforeFilterMovies, moviesToProcess.Count, beforeFilterTv, tvShowsToProcess.Count);
            }

            // Apply exclusion list filter
            if (excludedIds.Count > 0)
            {
                var beforeFilterMovies = moviesToProcess.Count;
                var beforeFilterTv = tvShowsToProcess.Count;

                moviesToProcess = moviesToProcess.Where(m => !excludedIds.Contains(m.Id)).ToList();
                tvShowsToProcess = tvShowsToProcess.Where(tv => !excludedIds.Contains(tv.Id)).ToList();

                if (moviesToProcess.Count < beforeFilterMovies || tvShowsToProcess.Count < beforeFilterTv)
                {
                    _log.LogInformation(
                        "Jfresolve: Filtered excluded content - Movies: {BeforeMovies} -> {AfterMovies}, TV: {BeforeTv} -> {AfterTv}",
                        beforeFilterMovies, moviesToProcess.Count, beforeFilterTv, tvShowsToProcess.Count);
                }
            }

            // Apply result limit
            var moviesToAdd = moviesToProcess.Take(config.PopulationResultLimit / 2).ToList();
            var tvShowsToAdd = tvShowsToProcess.Take(config.PopulationResultLimit / 2).ToList();

            _log.LogInformation(
                "Jfresolve: Retrieved {MovieCount} movies and {TvCount} TV shows from TMDB",
                moviesToAdd.Count,
                tvShowsToAdd.Count);

            // Get the appropriate folders - USE AUTO-POPULATE PATHS
            var movieFolder = _jfresolveManager.TryGetMovieFolderForAutoPopulate();
            var seriesFolder = _jfresolveManager.TryGetSeriesFolderForAutoPopulate();
            var animeFolder = _jfresolveManager.TryGetAnimeFolderForAutoPopulate();

            if (movieFolder == null && moviesToAdd.Any())
            {
                _log.LogWarning("Jfresolve: Movie folder not available, skipping movie population");
                moviesToAdd.Clear();
            }

            if (seriesFolder == null && tvShowsToAdd.Any())
            {
                _log.LogWarning("Jfresolve: Series folder not available, skipping TV show population");
                tvShowsToAdd.Clear();
            }

            var totalItems = moviesToAdd.Count + tvShowsToAdd.Count;
            var processedItems = 0;

            // Process movies
            foreach (var tmdbMovie in moviesToAdd)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Skip if no IMDB ID
                if (string.IsNullOrWhiteSpace(tmdbMovie.ImdbId))
                {
                    _log.LogDebug("Jfresolve: Skipping movie '{Title}' - no IMDB ID", tmdbMovie.Title);
                    processedItems++;
                    continue;
                }

                // Check for duplicates
                if (ItemExists(tmdbMovie.Id, tmdbMovie.ImdbId))
                {
                    _log.LogDebug(
                        "Jfresolve: Skipping movie '{Title}' - already exists (TMDB: {TmdbId}, IMDB: {ImdbId})",
                        tmdbMovie.Title,
                        tmdbMovie.Id,
                        tmdbMovie.ImdbId);
                    skippedDuplicates++;
                    processedItems++;
                    continue;
                }

                try
                {
                    var movie = _jfresolveManager.IntoBaseItem(tmdbMovie);
                    _jfresolveManager.SaveTmdbMetadata(movie.Id, tmdbMovie);

                    // Route anime movies to anime folder if enabled
                    Folder targetFolder;
                    if (tmdbMovie.IsAnime() && animeFolder != null)
                    {
                        targetFolder = animeFolder;
                        _log.LogDebug("Jfresolve: Routing anime movie '{Title}' to anime folder", tmdbMovie.Title);
                    }
                    else
                    {
                        targetFolder = movieFolder!;
                    }

                    await _jfresolveManager.InsertMeta(movie.Id, targetFolder, tmdbMovie, true, cancellationToken);

                    // Clear from cache after successful insertion to prevent memory buildup
                    _jfresolveManager.RemoveTmdbMetadata(movie.Id);

                    moviesAdded++;
                    _log.LogDebug(
                        "Jfresolve: Added movie '{Title}' to {Folder} (TMDB: {TmdbId}, IMDB: {ImdbId})",
                        tmdbMovie.Title,
                        targetFolder == animeFolder ? "anime folder" : "movie folder",
                        tmdbMovie.Id,
                        tmdbMovie.ImdbId);

                    // Delay to prevent concurrent image processing issues
                    await Task.Delay(500, cancellationToken);
                }
                catch (Exception ex)
                {
                    _log.LogError(
                        ex,
                        "Jfresolve: Failed to add movie '{Title}' (TMDB: {TmdbId})",
                        tmdbMovie.Title,
                        tmdbMovie.Id);
                }

                processedItems++;
                progress.Report(50 + (processedItems * 40.0 / totalItems));
            }

            // Process TV shows
            foreach (var tmdbTvShow in tvShowsToAdd)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Skip if no IMDB ID
                if (string.IsNullOrWhiteSpace(tmdbTvShow.ImdbId))
                {
                    _log.LogDebug("Jfresolve: Skipping TV show '{Name}' - no IMDB ID", tmdbTvShow.Name);
                    processedItems++;
                    continue;
                }

                // Check for duplicates
                if (ItemExists(tmdbTvShow.Id, tmdbTvShow.ImdbId))
                {
                    _log.LogDebug(
                        "Jfresolve: Skipping TV show '{Name}' - already exists (TMDB: {TmdbId}, IMDB: {ImdbId})",
                        tmdbTvShow.Name,
                        tmdbTvShow.Id,
                        tmdbTvShow.ImdbId);
                    skippedDuplicates++;
                    processedItems++;
                    continue;
                }

                try
                {
                    var series = _jfresolveManager.IntoBaseItem(tmdbTvShow);
                    _jfresolveManager.SaveTmdbMetadata(series.Id, tmdbTvShow);

                    // Route anime TV shows to anime folder if enabled
                    Folder targetFolder;
                    if (tmdbTvShow.IsAnime() && animeFolder != null)
                    {
                        targetFolder = animeFolder;
                        _log.LogDebug("Jfresolve: Routing anime TV show '{Name}' to anime folder", tmdbTvShow.Name);
                    }
                    else
                    {
                        targetFolder = seriesFolder!;
                    }

                    await _jfresolveManager.InsertMeta(series.Id, targetFolder, tmdbTvShow, true, cancellationToken);

                    // Clear from cache after successful insertion to prevent memory buildup
                    _jfresolveManager.RemoveTmdbMetadata(series.Id);

                    seriesAdded++;
                    _log.LogDebug(
                        "Jfresolve: Added TV show '{Name}' to {Folder} (TMDB: {TmdbId}, IMDB: {ImdbId})",
                        tmdbTvShow.Name,
                        targetFolder == animeFolder ? "anime folder" : "series folder",
                        tmdbTvShow.Id,
                        tmdbTvShow.ImdbId);

                    // Delay to prevent concurrent image processing issues
                    // TV shows create many items (series + seasons + episodes), so use longer delay
                    await Task.Delay(1000, cancellationToken);
                }
                catch (Exception ex)
                {
                    _log.LogError(
                        ex,
                        "Jfresolve: Failed to add TV show '{Name}' (TMDB: {TmdbId})",
                        tmdbTvShow.Name,
                        tmdbTvShow.Id);
                }

                processedItems++;
                progress.Report(50 + (processedItems * 40.0 / totalItems));
            }

            // Update last run timestamp
            config.LastPopulationRun = DateTime.UtcNow;
            JfresolvePlugin.Instance!.SaveConfiguration();

            // Note: Items should appear in UI due to UpdateToRepositoryAsync calls with queueRefreshItem=true
            // If items still don't appear, user may need to manually trigger a library scan

            progress.Report(100);

            _log.LogInformation(
                "Jfresolve: Auto-population completed - {MoviesAdded} movies added, {SeriesAdded} series added, {Skipped} duplicates skipped",
                moviesAdded,
                seriesAdded,
                skippedDuplicates);
            
            // Final UI refresh for containers to ensure everything shows up
            try
            {
                var options = new MetadataRefreshOptions(new DirectoryService(_jfresolveManager.FileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.None,
                    ImageRefreshMode = MetadataRefreshMode.None,
                    ReplaceAllImages = false,
                    ReplaceAllMetadata = false,
                    ForceSave = true
                };

                if (movieFolder != null) 
                {
                    await _jfresolveManager.Provider.RefreshFullItem(movieFolder, options, cancellationToken);
                    await movieFolder.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
                }
                if (seriesFolder != null) 
                {
                    await _jfresolveManager.Provider.RefreshFullItem(seriesFolder, options, cancellationToken);
                    await seriesFolder.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
                }
                if (animeFolder != null) 
                {
                    await _jfresolveManager.Provider.RefreshFullItem(animeFolder, options, cancellationToken);
                    await animeFolder.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
                }
                _log.LogDebug("Jfresolve: Final UI notifications and refreshes sent for all population folders");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve: Failed to send final UI notifications");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Error during auto-population");
            throw;
        }
    }

    /// <summary>
    /// Checks if an item already exists in the library by TMDB ID or IMDB ID.
    /// </summary>
    private bool ItemExists(int tmdbId, string? imdbId)
    {
        var allItems = _libraryManager.GetUserRootFolder().GetRecursiveChildren();

        foreach (var item in allItems)
        {
            // Check if item is a Jfresolve item
            if (!_jfresolveManager.IsJfresolve(item))
            {
                continue;
            }

            // Check TMDB ID
            if (item.ProviderIds.TryGetValue("Tmdb", out var existingTmdbId))
            {
                if (int.TryParse(existingTmdbId, out var parsedTmdbId) && parsedTmdbId == tmdbId)
                {
                    return true;
                }
            }

            // Check IMDB ID
            if (!string.IsNullOrWhiteSpace(imdbId) &&
                item.ProviderIds.TryGetValue("Imdb", out var existingImdbId) &&
                existingImdbId == imdbId)
            {
                return true;
            }
        }

        return false;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Don't set a default trigger - let users configure it manually
        // This prevents automatic runs until the user explicitly enables and schedules it
        return Array.Empty<TaskTriggerInfo>();
    }
}
