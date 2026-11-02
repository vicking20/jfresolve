using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters;

/// <summary>Serves TMDB artwork for search results not yet in the library.</summary>
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
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad
            || cad.ActionName != "GetItemImage")
        {
            await next();
            return;
        }

        var routeValues = ctx.RouteData.Values;

        if (!routeValues.TryGetValue("itemId", out var guidString)
            || !Guid.TryParse(guidString?.ToString(), out var guid))
        {
            await next();
            return;
        }

        var tmdbMovie = _manager.GetTmdbMetadata<TmdbMovie>(guid);
        if (tmdbMovie != null && !string.IsNullOrWhiteSpace(tmdbMovie.PosterPath))
        {
            var posterUrl = tmdbMovie.GetPosterUrl();
            _log.LogDebug("Jfresolve: Redirecting image request for {ItemId} to {PosterUrl}", guid, posterUrl);
            ctx.HttpContext.Response.Redirect(posterUrl, permanent: false);
            return;
        }

        var tmdbShow = _manager.GetTmdbMetadata<TmdbTvShow>(guid);
        if (tmdbShow != null && !string.IsNullOrWhiteSpace(tmdbShow.PosterPath))
        {
            var posterUrl = tmdbShow.GetPosterUrl();
            _log.LogDebug("Jfresolve: Redirecting image request for {ItemId} to {PosterUrl}", guid, posterUrl);
            ctx.HttpContext.Response.Redirect(posterUrl, permanent: false);
            return;
        }

        await next();
    }
}
