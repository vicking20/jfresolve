using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters;

/// <summary>
/// Intercepts image requests and redirects to TMDB poster URLs
/// Based on Gelato's ImageResourceFilter pattern
/// </summary>
public sealed class ImageResourceFilter : IAsyncResourceFilter
{
    private readonly JfresolveManager _manager;
    private readonly ILogger<ImageResourceFilter> _log;

    public ImageResourceFilter(
        JfresolveManager manager,
        ILogger<ImageResourceFilter> log
    )
    {
        _manager = manager;
        _log = log;
    }

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next
    )
    {
        // Only intercept GetItemImage action
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad
            || cad.ActionName != "GetItemImage")
        {
            await next();
            return;
        }

        var routeValues = ctx.RouteData.Values;

        // Get itemId from route
        if (!routeValues.TryGetValue("itemId", out var guidString)
            || !Guid.TryParse(guidString?.ToString(), out var guid))
        {
            await next();
            return;
        }

        // Try to get TMDB movie metadata
        var tmdbMovie = _manager.GetTmdbMetadata<TmdbMovie>(guid);
        if (tmdbMovie != null && !string.IsNullOrWhiteSpace(tmdbMovie.PosterPath))
        {
            var posterUrl = tmdbMovie.GetPosterUrl();
            _log.LogDebug("Jfresolve: Redirecting image request for {ItemId} to {PosterUrl}", guid, posterUrl);
            ctx.HttpContext.Response.Redirect(posterUrl, permanent: false);
            return;
        }

        // Try to get TMDB TV show metadata
        var tmdbShow = _manager.GetTmdbMetadata<TmdbTvShow>(guid);
        if (tmdbShow != null && !string.IsNullOrWhiteSpace(tmdbShow.PosterPath))
        {
            var posterUrl = tmdbShow.GetPosterUrl();
            _log.LogDebug("Jfresolve: Redirecting image request for {ItemId} to {PosterUrl}", guid, posterUrl);
            ctx.HttpContext.Response.Redirect(posterUrl, permanent: false);
            return;
        }

        // No TMDB metadata found, let Jellyfin handle it
        await next();
    }
}
