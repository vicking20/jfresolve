using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters;

/// <summary>
/// Intercepts item detail/playback requests and materializes virtual TMDB items into the database
/// Copied from Gelato's InsertActionFilter pattern
/// </summary>
public class InsertActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly ILibraryManager _library;
    private readonly ILogger<InsertActionFilter> _log;
    private readonly JfresolveManager _manager;

    public int Order => 1;

    public InsertActionFilter(
        ILibraryManager library,
        JfresolveManager manager,
        ILogger<InsertActionFilter> log
    )
    {
        _library = library;
        _manager = manager;
        _log = log;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // Check if this is an insertable action and get the GUID
        if (
            !ctx.IsInsertableAction()
            || !ctx.TryGetRouteGuid(out var guid)
            || _manager.GetTmdbMetadata<object>(guid) is not object metadata
        )
        {
            await next();
            return;
        }

        _log.LogDebug("Jfresolve: InsertActionFilter triggered for GUID {Guid}", guid);

        // Create temporary BaseItem to check provider IDs
        BaseItem item;
        if (metadata is TmdbMovie tmdbMovie)
        {
            item = _manager.IntoBaseItem(tmdbMovie);
        }
        else if (metadata is TmdbTvShow tmdbShow)
        {
            item = _manager.IntoBaseItem(tmdbShow);
        }
        else
        {
            await next();
            return;
        }

        // Check if already exists (Gelato pattern: FindExistingItem)
        var existing = FindExistingItem(item);
        if (existing is not null)
        {
            _log.LogInformation(
                "Jfresolve: Media already exists; redirecting to canonical id {Id}",
                existing.Id
            );

            // For series, check if it needs updating (on-access update check)
            if (existing is MediaBrowser.Controller.Entities.TV.Series existingSeries)
            {
                var lastModified = existingSeries.DateModified;
                var daysSinceUpdate = (DateTime.UtcNow - lastModified).TotalDays;

                // Check for updates if series hasn't been updated in 7+ days
                if (daysSinceUpdate >= 7)
                {
                    _log.LogInformation(
                        "Jfresolve: Series '{Name}' last updated {Days:F1} days ago, checking for updates",
                        existingSeries.Name,
                        daysSinceUpdate
                    );

                    var config = JfresolvePlugin.Instance?.Configuration;
                    if (config != null && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
                    {
                        try
                        {
                            // Use UpdateSeriesTask logic to check and update
                            var updateTask = ctx.HttpContext.RequestServices
                                .GetService(typeof(ScheduledTasks.UpdateSeriesTask)) as ScheduledTasks.UpdateSeriesTask;

                            if (updateTask != null)
                            {
                                var wasUpdated = await updateTask.CheckAndUpdateSeries(
                                    existingSeries,
                                    config,
                                    CancellationToken.None
                                );

                                if (wasUpdated)
                                {
                                    _log.LogInformation(
                                        "Jfresolve: Series '{Name}' updated with new content",
                                        existingSeries.Name
                                    );
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex,
                                "Jfresolve: Failed to check for updates to series '{Name}'",
                                existingSeries.Name
                            );
                        }
                    }
                }
            }

            _manager.ReplaceGuid(ctx, existing.Id);
            await next();
            return;
        }

        // Get root folder (movie or series) - USE SEARCH PATHS
        var isSeries = metadata is TmdbTvShow;
        var isMovie = metadata is TmdbMovie;
        Folder? root = null;

        if (isSeries)
        {
            // Check if this is anime and anime folder is enabled
            var tvShow = (TmdbTvShow)metadata;
            if (tvShow.IsAnime())
            {
                var animeFolder = _manager.TryGetAnimeFolderForSearch();
                if (animeFolder != null)
                {
                    root = animeFolder;
                    _log.LogInformation("Jfresolve: Adding anime series '{Name}' to anime search folder", tvShow.Name);
                }
                else
                {
                    // Fall back to regular series folder if anime folder not configured or unavailable
                    root = _manager.TryGetSeriesFolderForSearch();
                    _log.LogDebug("Jfresolve: Anime folder not available, using series search folder for '{Name}'", tvShow.Name);
                }
            }
            else
            {
                root = _manager.TryGetSeriesFolderForSearch();
            }
        }
        else if (isMovie)
        {
            // Check if this is anime movie and anime folder is enabled
            var movie = (TmdbMovie)metadata;
            if (movie.IsAnime())
            {
                var animeFolder = _manager.TryGetAnimeFolderForSearch();
                if (animeFolder != null)
                {
                    root = animeFolder;
                    _log.LogInformation("Jfresolve: Adding anime movie '{Title}' to anime search folder", movie.Title);
                }
                else
                {
                    // Fall back to regular movie folder if anime folder not configured or unavailable
                    root = _manager.TryGetMovieFolderForSearch();
                    _log.LogDebug("Jfresolve: Anime folder not available, using movie search folder for '{Title}'", movie.Title);
                }
            }
            else
            {
                root = _manager.TryGetMovieFolderForSearch();
            }
        }
        else
        {
            root = _manager.TryGetMovieFolderForSearch();
        }

        if (root is null)
        {
            _log.LogWarning("Jfresolve: No {Type} folder configured or available", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        // Insert the item (Gelato pattern: InsertMetaAsync)
        var baseItem = await InsertMetaAsync(guid, root, metadata);
        if (baseItem is not null)
        {
            _manager.ReplaceGuid(ctx, baseItem.Id);
            _manager.RemoveTmdbMetadata(guid);
        }

        await next();
    }

    /// <summary>
    /// Find existing item by provider IDs (Gelato's FindExistingItem)
    /// </summary>
    public BaseItem? FindExistingItem(BaseItem item)
    {
        return _manager.GetExistingItem(item.ProviderIds, item.GetBaseItemKind());
    }

    /// <summary>
    /// Insert metadata into database (Gelato's InsertMetaAsync pattern)
    /// </summary>
    public async Task<BaseItem?> InsertMetaAsync(Guid guid, Folder root, object metadata)
    {
        BaseItem? baseItem = null;
        var created = false;

        try
        {
            // Pass queueRefreshItem = true to trigger metadata refresh (Gelato pattern)
            (baseItem, created) = await _manager.InsertMeta(guid, root, metadata, queueRefreshItem: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Error inserting metadata for GUID {Guid}", guid);
            return null;
        }

        if (baseItem is not null && created)
        {
            _log.LogInformation("Jfresolve: inserted new media: {Name}", baseItem.Name);
        }

        return baseItem;
    }
}

/// <summary>
/// Extension methods for InsertActionFilter (Gelato pattern)
/// </summary>
public static class InsertActionFilterExtensions
{
    public static bool IsInsertableAction(this ActionExecutingContext ctx)
    {
        var actionName = ctx.ActionDescriptor?.DisplayName ?? "";
        return actionName.Contains("GetItem", StringComparison.OrdinalIgnoreCase) ||
               actionName.Contains("GetPlaybackInfo", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetRouteGuid(this ActionExecutingContext ctx, out Guid guid)
    {
        guid = Guid.Empty;

        // Try to get from route data
        var routeData = ctx.RouteData.Values;
        foreach (var key in new[] { "id", "itemId", "Id", "ItemId" })
        {
            if (routeData.TryGetValue(key, out var value))
            {
                if (value is Guid g)
                {
                    guid = g;
                    return true;
                }
                if (value is string str && Guid.TryParse(str, out g))
                {
                    guid = g;
                    return true;
                }
            }
        }

        return false;
    }
}
