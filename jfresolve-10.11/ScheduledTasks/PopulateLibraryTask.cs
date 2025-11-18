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

        try
        {
            var moviesAdded = 0;
            var seriesAdded = 0;
            var skippedDuplicates = 0;

            // Fetch trending/popular content from TMDB
            // For now, we'll use trending content with "week" time window
            progress.Report(10);

            _log.LogInformation("Jfresolve: Fetching trending movies from TMDB...");
            var trendingMovies = await _tmdbService.GetTrendingMoviesAsync(
                config.TmdbApiKey,
                "week",
                config.IncludeAdult);

            progress.Report(30);

            _log.LogInformation("Jfresolve: Fetching trending TV shows from TMDB...");
            var trendingTvShows = await _tmdbService.GetTrendingTvShowsAsync(
                config.TmdbApiKey,
                "week",
                config.IncludeAdult);

            progress.Report(50);

            // Apply filters: unreleased content
            if (config.FilterUnreleased)
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-config.UnreleasedBufferDays);
                var beforeFilterMovies = trendingMovies.Count;
                var beforeFilterTv = trendingTvShows.Count;

                trendingMovies = trendingMovies.Where(m =>
                {
                    var releaseDate = m.GetReleaseDateTime();
                    return releaseDate.HasValue && releaseDate.Value <= cutoffDate;
                }).ToList();

                trendingTvShows = trendingTvShows.Where(tv =>
                {
                    var releaseDate = tv.GetFirstAirDateTime();
                    return releaseDate.HasValue && releaseDate.Value <= cutoffDate;
                }).ToList();

                _log.LogInformation(
                    "Jfresolve: Filtered unreleased content - Movies: {BeforeMovies} -> {AfterMovies}, TV: {BeforeTv} -> {AfterTv}",
                    beforeFilterMovies, trendingMovies.Count, beforeFilterTv, trendingTvShows.Count);
            }

            // Apply result limit
            var moviesToAdd = trendingMovies.Take(config.PopulationResultLimit / 2).ToList();
            var tvShowsToAdd = trendingTvShows.Take(config.PopulationResultLimit / 2).ToList();

            _log.LogInformation(
                "Jfresolve: Retrieved {MovieCount} movies and {TvCount} TV shows from TMDB",
                moviesToAdd.Count,
                tvShowsToAdd.Count);

            // Get the appropriate folders
            var movieFolder = _jfresolveManager.TryGetMovieFolder();
            var seriesFolder = _jfresolveManager.TryGetSeriesFolder();
            var animeFolder = config.EnableAnimeFolder ? _jfresolveManager.TryGetAnimeFolder() : null;

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
                    _log.LogInformation(
                        "Jfresolve: Added movie '{Title}' to {Folder} (TMDB: {TmdbId}, IMDB: {ImdbId})",
                        tmdbMovie.Title,
                        targetFolder == animeFolder ? "anime folder" : "movie folder",
                        tmdbMovie.Id,
                        tmdbMovie.ImdbId);

                    // Delay to prevent concurrent image processing issues
                    await Task.Delay(100, cancellationToken);
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
                    _log.LogInformation(
                        "Jfresolve: Added TV show '{Name}' to {Folder} (TMDB: {TmdbId}, IMDB: {ImdbId})",
                        tmdbTvShow.Name,
                        targetFolder == animeFolder ? "anime folder" : "series folder",
                        tmdbTvShow.Id,
                        tmdbTvShow.ImdbId);

                    // Delay to prevent concurrent image processing issues
                    await Task.Delay(100, cancellationToken);
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
