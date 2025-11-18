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

/// <summary>
/// Intercepts search requests and returns TMDB results (based on Gelato's SearchActionFilter pattern)
/// </summary>
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
        // Check if search is enabled in configuration
        if (!JfresolvePlugin.Instance?.Configuration.EnableSearch ?? true)
        {
            await next();
            return;
        }

        // Check if this is a search action and get search term
        if (!IsSearchAction(ctx) || !TryGetSearchTerm(ctx, out var searchTerm))
        {
            await next();
            return;
        }

        // Handle "local:" prefix - pass through to default Jellyfin search
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm.Substring(6).Trim();
            await next();
            return;
        }

        // Get requested item types from query parameters
        var requestedTypes = GetRequestedItemTypes(ctx);
        if (requestedTypes.Count == 0)
        {
            // No supported types requested, let Jellyfin handle it
            await next();
            return;
        }

        // Get pagination parameters
        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        // Search TMDB for all requested types
        var baseItems = await SearchTmdbAsync(searchTerm, requestedTypes);

        _log.LogInformation(
            "Jfresolve: Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} results={Results}",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            baseItems.Count
        );

        // Convert BaseItems to DTOs (similar to Gelato's ConvertMetasToDtos)
        var dtos = ConvertBaseItemsToDtos(baseItems);

        // Apply pagination
        var paged = dtos.Skip(start).Take(limit).ToArray();

        // Return search results
        ctx.Result = new OkObjectResult(
            new QueryResult<BaseItemDto>
            {
                Items = paged,
                TotalRecordCount = dtos.Count
            }
        );
    }

    /// <summary>
    /// Search TMDB for all requested item types (similar to Gelato's SearchMetasAsync)
    /// </summary>
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

    /// <summary>
    /// Convert BaseItems to DTOs (based on Gelato's ConvertMetasToDtos)
    /// </summary>
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

            // Use the BaseItem's ID (already set in JfresolveManager)
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

        // Check for includeItemTypes parameter
        if (ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes != null
            && includeTypes.Length > 0)
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            // Only keep Movie and Series (we only support these types)
            requested.IntersectWith(new[] { BaseItemKind.Movie, BaseItemKind.Series });
        }

        // Remove excluded types
        if (ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes != null
            && excludeTypes.Length > 0)
        {
            requested.ExceptWith(excludeTypes);
        }

        // If mediaTypes=Video, exclude Series (Gelato pattern)
        if (ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
            && mediaTypes != null
            && mediaTypes.Contains(MediaType.Video))
        {
            requested.Remove(BaseItemKind.Series);
        }

        return requested;
    }
}

// Helper extension methods
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
