using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jfresolve.ScheduledTasks;

/// <summary>
/// Scheduled task to check for new seasons/episodes in existing Jfresolve series
/// </summary>
public sealed class UpdateSeriesTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<UpdateSeriesTask> _log;
    private readonly JfresolveManager _jfresolveManager;
    private readonly TmdbService _tmdbService;

    public UpdateSeriesTask(
        ILibraryManager libraryManager,
        ILogger<UpdateSeriesTask> logger,
        JfresolveManager jfresolveManager,
        TmdbService tmdbService)
    {
        _libraryManager = libraryManager;
        _log = logger;
        _jfresolveManager = jfresolveManager;
        _tmdbService = tmdbService;
    }

    public string Name => "Update Jfresolve Series";

    public string Key => "UpdateJfresolveSeries";

    public string Description => "Checks existing Jfresolve series for new seasons and episodes from TMDB";

    public string Category => "Jfresolve";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = JfresolvePlugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            _log.LogWarning("Jfresolve: Cannot update series - TMDB API key not configured");
            return;
        }

        _log.LogInformation("Jfresolve: Starting series update check");

        try
        {
            // Get all Jfresolve series from the library
            var allSeries = _libraryManager.GetUserRootFolder()
                .GetRecursiveChildren()
                .OfType<Series>()
                .Where(s => _jfresolveManager.IsJfresolve(s))
                .ToList();

            if (allSeries.Count == 0)
            {
                _log.LogInformation("Jfresolve: No series found to update");
                return;
            }

            _log.LogInformation("Jfresolve: Found {Count} series to check for updates", allSeries.Count);

            var updatedCount = 0;
            var processedCount = 0;

            foreach (var series in allSeries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var wasUpdated = await CheckAndUpdateSeries(series, config, cancellationToken);
                    if (wasUpdated)
                    {
                        updatedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Jfresolve: Error updating series '{Name}'", series.Name);
                }

                processedCount++;
                progress.Report((double)processedCount / allSeries.Count * 100);
            }

            _log.LogInformation(
                "Jfresolve: Series update complete - {Checked} checked, {Updated} updated",
                processedCount,
                updatedCount);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Error during series update task");
            throw;
        }
    }

    /// <summary>
    /// Check if a series has updates and apply them if found
    /// Returns true if series was updated
    /// </summary>
    public async Task<bool> CheckAndUpdateSeries(
        Series series,
        Configuration.PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        // Get TMDB ID
        var tmdbIdStr = series.GetProviderId("Tmdb");
        if (string.IsNullOrWhiteSpace(tmdbIdStr) || !int.TryParse(tmdbIdStr, out var tmdbId))
        {
            _log.LogDebug("Jfresolve: Series '{Name}' has no TMDB ID, skipping", series.Name);
            return false;
        }

        // Fetch latest TV details from TMDB
        var tvDetails = await _tmdbService.GetTvDetailsAsync(tmdbId, config.TmdbApiKey);
        if (tvDetails == null || tvDetails.Seasons == null)
        {
            _log.LogDebug("Jfresolve: Could not fetch TMDB details for series '{Name}'", series.Name);
            return false;
        }

        // Get IMDB ID from TMDB details (needed for episode paths)
        var imdbId = series.GetProviderId("Imdb");
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            _log.LogDebug("Jfresolve: Series '{Name}' has no IMDB ID, skipping", series.Name);
            return false;
        }

        // Create minimal TmdbTvShow object for season/episode creation
        var tmdbShow = new TmdbTvShow
        {
            Id = tmdbId,
            Name = series.Name,
            ImdbId = imdbId
        };

        // Get existing seasons
        var existingSeasons = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = series.Id,
            IncludeItemTypes = new[] { BaseItemKind.Season },
            IsDeadPerson = true,
        }).OfType<Season>().ToList();

        // Get latest seasons from TMDB (exclude season 0 - specials)
        var latestSeasons = tvDetails.Seasons.Where(s => s.SeasonNumber > 0).ToList();

        var hasUpdates = false;

        // Check for new seasons
        var maxExistingSeason = existingSeasons.Any() ? existingSeasons.Max(s => s.IndexNumber ?? 0) : 0;
        var newSeasons = latestSeasons.Where(s => s.SeasonNumber > maxExistingSeason).ToList();

        if (newSeasons.Any())
        {
            _log.LogInformation(
                "Jfresolve: Found {Count} new season(s) for '{Name}' (existing: {ExistingCount}, latest: {LatestCount})",
                newSeasons.Count,
                series.Name,
                maxExistingSeason,
                latestSeasons.Count);

            foreach (var newSeason in newSeasons)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await _jfresolveManager.CreateSeasonWithEpisodes(
                    series,
                    tmdbShow,
                    newSeason,
                    series.PresentationUniqueKey!,
                    config,
                    cancellationToken);

                hasUpdates = true;
            }
        }

        // Check existing seasons for new episodes
        foreach (var existingSeason in existingSeasons)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var seasonNumber = existingSeason.IndexNumber;
            if (!seasonNumber.HasValue)
            {
                continue;
            }

            // Get existing episodes
            var existingEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = existingSeason.Id,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsDeadPerson = true,
            }).OfType<Episode>().ToList();

            // Fetch season details from TMDB
            var seasonDetails = await _tmdbService.GetSeasonDetailsAsync(tmdbId, seasonNumber.Value, config.TmdbApiKey);
            if (seasonDetails == null || seasonDetails.Episodes == null)
            {
                continue;
            }

            var maxExistingEpisode = existingEpisodes.Any() ? existingEpisodes.Max(e => e.IndexNumber ?? 0) : 0;
            var newEpisodes = seasonDetails.Episodes.Where(e => e.EpisodeNumber > maxExistingEpisode).ToList();

            if (newEpisodes.Any())
            {
                _log.LogInformation(
                    "Jfresolve: Found {Count} new episode(s) for '{Name}' Season {Season}",
                    newEpisodes.Count,
                    series.Name,
                    seasonNumber.Value);

                foreach (var newEpisode in newEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await _jfresolveManager.CreateEpisode(
                        series,
                        existingSeason,
                        tmdbShow,
                        newEpisode,
                        series.PresentationUniqueKey!,
                        config,
                        cancellationToken);

                    hasUpdates = true;
                }
            }
        }

        if (hasUpdates)
        {
            // Update the series to notify Jellyfin
            await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
            _log.LogInformation("Jfresolve: Updated series '{Name}' with new content", series.Name);
        }

        return hasUpdates;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No default trigger - users can configure weekly schedule manually
        // Recommended: Weekly on Sunday at 4 AM
        return Array.Empty<TaskTriggerInfo>();
    }
}
