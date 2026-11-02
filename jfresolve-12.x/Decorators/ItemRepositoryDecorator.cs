#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Jfresolve.Decorators;

/// <summary>Hides virtual quality versions from library listings.</summary>
public sealed class JfresolveItemRepository : IItemRepository
{
    private readonly IItemRepository _inner;
    private readonly IHttpContextAccessor _http;

    public JfresolveItemRepository(IItemRepository inner, IHttpContextAccessor http)
    {
        _inner = inner;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        return _inner.GetItems(ApplyFilters(filter));
    }

    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) =>
        _inner.GetItemIdsList(ApplyFilters(filter));

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter)
    {
        return _inner.GetItemList(ApplyFilters(filter));
    }

    private InternalItemsQuery ApplyFilters(InternalItemsQuery filter)
    {
        var ctx = _http?.HttpContext;

        if (ctx is not null && ctx.IsApiListing() && filter.IsDeadPerson is null)
        {
            if (
                !filter.IncludeItemTypes.Any()
                || filter.IncludeItemTypes.Intersect(
                    new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode }
                ).Any()
            )
            {
                if (filter.IsVirtualItem is null)
                {
                    filter.IsVirtualItem = false;
                }
            }
        }

        return filter;
    }

    public BaseItem RetrieveItem(Guid id) => _inner.RetrieveItem(id);
    public IReadOnlyList<BaseItem> GetLatestItemList(InternalItemsQuery filter, CollectionType collectionType) => _inner.GetLatestItemList(filter, collectionType);
    public QueryResult<(BaseItem Item, MediaBrowser.Model.Dto.ItemCounts ItemCounts)> GetGenres(InternalItemsQuery filter) => _inner.GetGenres(filter);
    public QueryResult<(BaseItem Item, MediaBrowser.Model.Dto.ItemCounts ItemCounts)> GetMusicGenres(InternalItemsQuery filter) => _inner.GetMusicGenres(filter);
    public QueryResult<(BaseItem Item, MediaBrowser.Model.Dto.ItemCounts ItemCounts)> GetStudios(InternalItemsQuery filter) => _inner.GetStudios(filter);
    public QueryResult<(BaseItem Item, MediaBrowser.Model.Dto.ItemCounts ItemCounts)> GetArtists(InternalItemsQuery filter) => _inner.GetArtists(filter);
    public QueryResult<(BaseItem Item, MediaBrowser.Model.Dto.ItemCounts ItemCounts)> GetAlbumArtists(InternalItemsQuery filter) => _inner.GetAlbumArtists(filter);
    public QueryResult<(BaseItem Item, MediaBrowser.Model.Dto.ItemCounts ItemCounts)> GetAllArtists(InternalItemsQuery filter) => _inner.GetAllArtists(filter);
    public IReadOnlyList<string> GetMusicGenreNames() => _inner.GetMusicGenreNames();
    public IReadOnlyList<string> GetStudioNames() => _inner.GetStudioNames();
    public IReadOnlyList<string> GetGenreNames() => _inner.GetGenreNames();
    public IReadOnlyList<string> GetAllArtistNames() => _inner.GetAllArtistNames();
    public IReadOnlyList<string> GetMediaStreamLanguages(InternalItemsQuery filter, MediaStreamType mediaStreamType) => _inner.GetMediaStreamLanguages(filter, mediaStreamType);
    public IReadOnlyList<string> GetTagNames(InternalItemsQuery filter) => _inner.GetTagNames(filter);
    public QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery filter) => _inner.GetQueryFiltersLegacy(filter);
    public System.Threading.Tasks.Task<bool> ItemExistsAsync(Guid id) => _inner.ItemExistsAsync(id);
    public bool GetIsPlayed(Jellyfin.Database.Implementations.Entities.User user, Guid id, bool recursive) => _inner.GetIsPlayed(user, id, recursive);
}

public static class HttpContextExtensions
{
    public static bool IsApiListing(this HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";

        return path.Contains("/Items", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Views", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Latest", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Resume", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/NextUp", StringComparison.OrdinalIgnoreCase);
    }
}
