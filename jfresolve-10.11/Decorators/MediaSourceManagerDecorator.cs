using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Decorators;

/// <summary>
/// Decorator for IMediaSourceManager to support quality versioning for Jfresolve items
/// </summary>
public class MediaSourceManagerDecorator : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceManagerDecorator> _log;
    private readonly IItemRepository _repo;
    private readonly IDirectoryService _directoryService;

    public MediaSourceManagerDecorator(
        IMediaSourceManager inner,
        ILogger<MediaSourceManagerDecorator> log,
        IItemRepository repo,
        IDirectoryService directoryService)
    {
        _inner = inner;
        _log = log;
        _repo = repo;
        _directoryService = directoryService;
    }

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        if (!IsJfresolve(item))
        {
            return await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken);
        }

        _log.LogDebug("Jfresolve: GetPlaybackMediaSources for {ItemId} ({Name})", item.Id, item.Name);

        BaseItem primaryItem = item;

        if (item.IsVirtualItem)
        {
            _log.LogDebug("Jfresolve: Item is virtual, finding primary item");
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { item.GetBaseItemKind() },
                HasAnyProviderId = item.ProviderIds,
                IsVirtualItem = false,
            };

            primaryItem = _repo.GetItemList(query).FirstOrDefault() ?? item;
            _log.LogDebug("Jfresolve: Using primary item {PrimaryId} ({PrimaryName})", primaryItem.Id, primaryItem.Name);
        }

        var sources = (await _inner.GetPlaybackMediaSources(primaryItem, user, allowMediaProbe, enablePathSubstitution, cancellationToken)).ToList();

        foreach (var info in sources)
        {
            ApplyTrick(info);
        }

        var primarySource = sources.FirstOrDefault();
        if (primarySource != null && NeedsProbe(primarySource))
        {
            _log.LogInformation("Jfresolve: Probing primary item {Name}", primaryItem.Name);
            await ProbeItem(primaryItem, cancellationToken);

            sources = (await _inner.GetPlaybackMediaSources(primaryItem, user, allowMediaProbe, enablePathSubstitution, cancellationToken)).ToList();
            foreach (var info in sources)
            {
                ApplyTrick(info);
            }
        }

        var virtualQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { primaryItem.GetBaseItemKind() },
            HasAnyProviderId = primaryItem.ProviderIds,
            IsVirtualItem = true,
        };

        var virtualItems = _repo.GetItemList(virtualQuery)
            .OfType<Video>()
            .Where(v => IsJfresolve(v))
            .OrderBy(v => v.Name)
            .ToList();

        _log.LogDebug("Jfresolve: Found {Count} virtual quality items", virtualItems.Count);

        foreach (var virtualItem in virtualItems)
        {
            var virtualSources = (await _inner.GetPlaybackMediaSources(virtualItem, user, allowMediaProbe, enablePathSubstitution, cancellationToken)).ToList();
            var virtualSource = virtualSources.FirstOrDefault();

            if (virtualSource != null && NeedsProbe(virtualSource))
            {
                _log.LogInformation("Jfresolve: Probing virtual item {Name}", virtualItem.Name);
                await ProbeItem(virtualItem, cancellationToken);
            }

            var qualityStreams = _inner.GetMediaStreams(virtualItem.Id);
            var qualityContainer = virtualItem.Container;

            var qualitySource = new MediaSourceInfo
            {
                Id = virtualItem.Id.ToString("N"),
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                MediaStreams = qualityStreams,
                MediaAttachments = _inner.GetMediaAttachments(virtualItem.Id),
                Name = virtualItem.Name,
                Path = virtualItem.Path,
                RunTimeTicks = virtualItem.RunTimeTicks,
                Container = qualityContainer,
                Size = virtualItem.Size,
                Type = MediaSourceType.Grouping,
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true,
            };

            if (virtualItem is Video video)
            {
                qualitySource.VideoType = video.VideoType;
                qualitySource.IsoType = video.IsoType;
                qualitySource.Video3DFormat = video.Video3DFormat;
                qualitySource.Timestamp = video.Timestamp;
            }

            sources.Add(qualitySource);
        }

        if (sources.Count > 0)
        {
            sources[0].Type = MediaSourceType.Default;
        }

        _log.LogDebug("Jfresolve: Returning {Count} total playback sources", sources.Count);
        return sources;
    }

    /// <summary>
    /// Check if a media source needs to be probed
    /// </summary>
    private bool NeedsProbe(MediaSourceInfo? source)
    {
        if (source == null) return false;

        var noVideoStreams = source.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true;
        var runtimeTooShort = (source.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;

        return noVideoStreams || runtimeTooShort;
    }

    /// <summary>
    /// Probe an item to populate its MediaStreams
    /// </summary>
    private async Task ProbeItem(BaseItem item, CancellationToken cancellationToken)
    {
        var wasVirtual = item.IsVirtualItem;
        item.IsVirtualItem = false;

        try
        {
            _log.LogInformation("Jfresolve: Probing {Name} - Path: {Path}, IsVirtual: {IsVirtual}", item.Name, item.Path, item.IsVirtualItem);

            await item.RefreshMetadata(
                new MetadataRefreshOptions(_directoryService)
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                },
                cancellationToken
            ).ConfigureAwait(false);

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            var streams = _inner.GetMediaStreams(item.Id);
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream != null)
            {
                _log.LogInformation("Jfresolve: Probed {Name} - Codec: {Codec}, Width: {Width}, Height: {Height}",
                    item.Name, videoStream.Codec, videoStream.Width, videoStream.Height);
            }
            else
            {
                _log.LogWarning("Jfresolve: Probed {Name} but NO video stream found!", item.Name);
            }
        }
        finally
        {
            item.IsVirtualItem = wasVirtual;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
    {
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user).ToList();

        if (!IsJfresolve(item))
        {
            return sources;
        }

        _log.LogDebug("Jfresolve: GetStaticMediaSources for {ItemId} ({Name})", item.Id, item.Name);

        foreach (var info in sources)
        {
            ApplyTrick(info);
        }

        var primaryStreams = sources.Count > 0 ? sources[0].MediaStreams : new List<MediaStream>();
        var primaryContainer = sources.Count > 0 ? sources[0].Container : null;

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { item.GetBaseItemKind() },
            HasAnyProviderId = item.ProviderIds,
            Recursive = false,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            IsVirtualItem = true,
        };

        var virtualItems = _repo.GetItemList(query)
            .OfType<Video>()
            .Where(v => IsJfresolve(v))
            .OrderBy(v => v.Name)
            .ToList();

        _log.LogDebug("Jfresolve: Found {Count} virtual quality items for {Name}", virtualItems.Count, item.Name);

        foreach (var virtualItem in virtualItems)
        {
            var qualitySource = new MediaSourceInfo
            {
                Id = virtualItem.Id.ToString("N"),
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                MediaStreams = primaryStreams,
                MediaAttachments = _inner.GetMediaAttachments(virtualItem.Id),
                Name = virtualItem.Name,
                Path = virtualItem.Path,
                RunTimeTicks = virtualItem.RunTimeTicks,
                Container = primaryContainer,
                Size = virtualItem.Size,
                Type = MediaSourceType.Grouping,
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true,
            };

            if (virtualItem is Video video)
            {
                qualitySource.VideoType = video.VideoType;
                qualitySource.IsoType = video.IsoType;
                qualitySource.Video3DFormat = video.Video3DFormat;
                qualitySource.Timestamp = video.Timestamp;
            }

            sources.Add(qualitySource);
        }

        if (sources.Count > 0)
        {
            sources[0].Type = MediaSourceType.Default;
        }

        _log.LogDebug("Jfresolve: Returning {Count} total quality options for {Name}", sources.Count, item.Name);
        return sources;
    }

    private void ApplyTrick(MediaSourceInfo info)
    {
        if (string.IsNullOrEmpty(info.Path) || !info.Path.Contains("/Plugins/Jfresolve/resolve/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        info.Protocol = MediaProtocol.Http;
        info.IsRemote = true;
        info.SupportsDirectPlay = false;
        info.SupportsDirectStream = false;
        info.SupportsTranscoding = true;
    }

    private bool IsJfresolve(BaseItem item)
    {
        if (item == null) return false;
        return item.ProviderIds.ContainsKey("Jfresolve");
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => _inner.AddParts(providers);

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => _inner.GetMediaStreams(itemId);

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => _inner.GetMediaStreams(query);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);

    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string? liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
        => _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStream(request, cancellationToken);

    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStream(id, cancellationToken);

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream? GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    public ILiveStream? GetLiveStreamInfoByUniqueId(string uniqueId) => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
        => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) => _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
        => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string? cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
        => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
}
