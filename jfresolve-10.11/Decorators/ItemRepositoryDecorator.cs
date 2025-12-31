#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Jfresolve.Decorators;

/// <summary>
/// Decorates IItemRepository to filter virtual items from API listings (Gelato pattern)
/// This prevents duplicate quality items from appearing in the library
/// </summary>
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

        // Apply filters when this is an API listing
        if (ctx is not null && ctx.IsApiListing() && filter.IsDeadPerson is null)
        {
            // For movie/series/episode queries, filter out virtual items
            if (
                !filter.IncludeItemTypes.Any()
                || filter.IncludeItemTypes.Intersect(
                    new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode }
                ).Any()
            )
            {
                // Hide virtual items from library listings
                if (filter.IsVirtualItem is null)
                {
                    filter.IsVirtualItem = false;
                }
            }
        }

        return filter;
    }

    // All other methods just delegate to inner
    public void DeleteItem(params IReadOnlyList<Guid> ids) => _inner.DeleteItem(ids);
    public void SaveItems(IReadOnlyList<BaseItem> items, System.Threading.CancellationToken cancellationToken) => _inner.SaveItems(items, cancellationToken);
    public void SaveImages(BaseItem item) => _inner.SaveImages(item);
    public BaseItem RetrieveItem(Guid id) => _inner.RetrieveItem(id);
    public IReadOnlyList<BaseItem> GetLatestItemList(InternalItemsQuery filter, CollectionType collectionType) => _inner.GetLatestItemList(filter, collectionType);
    public IReadOnlyList<string> GetNextUpSeriesKeys(InternalItemsQuery filter, DateTime dateCutoff) => _inner.GetNextUpSeriesKeys(filter, dateCutoff);
    public void UpdateInheritedValues() => _inner.UpdateInheritedValues();
    public int GetCount(InternalItemsQuery filter) => _inner.GetCount(filter);
    public MediaBrowser.Model.Dto.ItemCounts GetItemCounts(InternalItemsQuery filter) => _inner.GetItemCounts(filter);
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
    public System.Threading.Tasks.Task<bool> ItemExistsAsync(Guid id) => _inner.ItemExistsAsync(id);
    public bool GetIsPlayed(Jellyfin.Database.Implementations.Entities.User user, Guid id, bool recursive) => _inner.GetIsPlayed(user, id, recursive);
    public IReadOnlyDictionary<string, MediaBrowser.Controller.Entities.Audio.MusicArtist[]> FindArtists(IReadOnlyList<string> artistNames) => _inner.FindArtists(artistNames);
}

/// <summary>
/// Extension methods for detecting API listing context
/// </summary>
public static class HttpContextExtensions
{
    public static bool IsApiListing(this HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";

        // These are the main API endpoints that list items in the library
        return path.Contains("/Items", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Views", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Latest", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Resume", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/NextUp", StringComparison.OrdinalIgnoreCase);
    }
}
