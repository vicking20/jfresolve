using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Providers;

public sealed class JfresolveSeriesProvider
    : IRemoteMetadataProvider<Series, SeriesInfo>,
      IHasOrder
{
    private readonly ILogger<JfresolveSeriesProvider> _log;
    private readonly ILibraryManager _libraryManager;
    private readonly JfresolveManager _manager;
    private readonly TmdbService _tmdbService;
    private readonly IProviderManager _provider;
    private readonly ConcurrentDictionary<Guid, DateTime> _syncCache = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(2);

    public JfresolveSeriesProvider(
        ILogger<JfresolveSeriesProvider> logger,
        ILibraryManager libraryManager,
        TmdbService tmdbService,
        IProviderManager provider,
        JfresolveManager manager
    )
    {
        _log = logger;
        _libraryManager = libraryManager;
        _manager = manager;
        _tmdbService = tmdbService;
        _provider = provider;

        // Hook into Jellyfin's refresh system - THIS IS THE KEY!
        _provider.RefreshStarted += OnProviderManagerRefreshStarted;
    }

    public string Name => "Jfresolve";
    public int Order => 0;

    private async void OnProviderManagerRefreshStarted(
        object? sender,
        GenericEventArgs<BaseItem> genericEventArgs)
    {
        var series = genericEventArgs.Argument as Series;
        if (series is null)
        {
            return;
        }

        // Check if this is a Jfresolve series
        if (!series.ProviderIds.ContainsKey("Jfresolve"))
        {
            return;
        }

        // Avoid re-syncing too frequently
        var now = DateTime.UtcNow;
        if (_syncCache.TryGetValue(series.Id, out var lastSync))
        {
            if (now - lastSync < CacheExpiry)
            {
                _log.LogDebug("Jfresolve: Skipping {Name} - synced {Seconds} seconds ago",
                    series.Name, (now - lastSync).TotalSeconds);
                return;
            }
        }

        _syncCache[series.Id] = now;

        try
        {
            var config = JfresolvePlugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                return;
            }

            // Get TMDB ID from series provider IDs
            if (!series.ProviderIds.TryGetValue("Tmdb", out var tmdbId) ||
                !int.TryParse(tmdbId, out var tmdbIdInt))
            {
                _log.LogDebug("Jfresolve: Series {Name} has no TMDB ID", series.Name);
                return;
            }

            _log.LogInformation("Jfresolve: Auto-syncing seasons for {Name} (TMDB ID: {TmdbId})",
                series.Name, tmdbIdInt);

            // Fetch external IDs to get IMDB ID
            var externalIds = await _tmdbService.GetExternalIdsAsync(tmdbIdInt, "tv", config.TmdbApiKey);

            // Create TmdbTvShow object for our existing method
            var tmdbShow = new TmdbTvShow
            {
                Id = tmdbIdInt,
                Name = series.Name,
                OriginalName = series.OriginalTitle ?? series.Name,
                Overview = series.Overview ?? string.Empty,
                ImdbId = externalIds?.ImdbId
            };

            // Use our existing CreateSeasonsAndEpisodesForSeries method
            await _manager.CreateSeasonsAndEpisodesForSeries(series, tmdbShow, CancellationToken.None);

            _log.LogInformation("Jfresolve: Successfully auto-synced seasons for {Name}", series.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Failed to auto-sync seasons for series {Name}", series.Name);
        }
    }

    public Task<MetadataResult<Series>> GetMetadata(
        SeriesInfo info,
        CancellationToken cancellationToken
    )
    {
        var result = new MetadataResult<Series> { HasMetadata = false, QueriedById = true };
        return Task.FromResult(result);
    }

    public bool SupportsSearch => false;

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeriesInfo searchInfo,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
    }

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    )
    {
        throw new NotImplementedException();
    }
}
