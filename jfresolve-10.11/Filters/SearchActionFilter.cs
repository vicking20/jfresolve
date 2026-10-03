using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters;

/// <summary>Returns TMDB results for Jellyfin searches.</summary>
public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly IDtoService _dtoService;
    private readonly JfresolveManager _manager;
    private readonly ILogger<SearchActionFilter> _log;

    public SearchActionFilter(
        IDtoService dtoService,
        JfresolveManager manager,
        ILogger<SearchActionFilter> log
    )
    {
        _dtoService = dtoService;
        _manager = manager;
        _log = log;
    }

    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (!JfresolvePlugin.Instance?.Configuration.EnableSearch ?? true)
        {
            await next();
            return;
        }

        if (!IsSearchAction(ctx) || !TryGetSearchTerm(ctx, out var searchTerm))
        {
            await next();
            return;
        }

        // "local:" prefix searches the local library only
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm.Substring(6).Trim();
            await next();
            return;
        }

        var requestedTypes = GetRequestedItemTypes(ctx);
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        var baseItems = await SearchTmdbAsync(searchTerm, requestedTypes);

        _log.LogInformation(
            "Jfresolve: Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} results={Results}",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            baseItems.Count
        );

        var dtos = ConvertBaseItemsToDtos(baseItems);

        var paged = dtos.Skip(start).Take(limit).ToArray();

        ctx.Result = new OkObjectResult(
            new QueryResult<BaseItemDto>
            {
                Items = paged,
                TotalRecordCount = dtos.Count
            }
        );
    }

    private async Task<List<BaseItem>> SearchTmdbAsync(string searchTerm, HashSet<BaseItemKind> requestedTypes)
    {
        var tasks = new List<Task<List<BaseItem>>>();

        foreach (var itemType in requestedTypes)
        {
            tasks.Add(_manager.SearchTmdbAsync(searchTerm, itemType));
        }

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    private List<BaseItemDto> ConvertBaseItemsToDtos(List<BaseItem> baseItems)
    {
        var options = new DtoOptions
        {
            EnableImages = true,
            EnableUserData = false,
        };

        var dtos = new List<BaseItemDto>(baseItems.Count);

        foreach (var baseItem in baseItems)
        {
            var dto = _dtoService.GetBaseItemDto(baseItem, options);

            dto.Id = baseItem.Id;

            dtos.Add(dto);
        }

        return dtos;
    }

    private bool IsSearchAction(ActionExecutingContext ctx)
    {
        var actionName = ctx.ActionDescriptor?.DisplayName ?? "";
        return actionName.Contains("GetItems", StringComparison.OrdinalIgnoreCase) ||
               actionName.Contains("GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetSearchTerm(ActionExecutingContext ctx, out string searchTerm)
    {
        searchTerm = string.Empty;

        if (ctx.ActionArguments.TryGetValue("searchTerm", out var value) && value is string term)
        {
            searchTerm = term;
            return !string.IsNullOrWhiteSpace(searchTerm);
        }

        return false;
    }

    private HashSet<BaseItemKind> GetRequestedItemTypes(ActionExecutingContext ctx)
    {
        var requested = new HashSet<BaseItemKind>(
            new[] { BaseItemKind.Movie, BaseItemKind.Series }
        );

        if (ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes != null
            && includeTypes.Length > 0)
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            requested.IntersectWith(new[] { BaseItemKind.Movie, BaseItemKind.Series });
        }

        if (ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes != null
            && excludeTypes.Length > 0)
        {
            requested.ExceptWith(excludeTypes);
        }

        // mediaTypes=Video excludes series
        if (ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
            && mediaTypes != null
            && mediaTypes.Contains(MediaType.Video))
        {
            requested.Remove(BaseItemKind.Series);
        }

        return requested;
    }
}

public static class ActionContextExtensions
{
    public static bool TryGetActionArgument<T>(
        this ActionExecutingContext ctx,
        string key,
        out T value,
        T defaultValue = default!)
    {
        if (ctx.ActionArguments.TryGetValue(key, out var objValue) && objValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = defaultValue!;
        return false;
    }
}
