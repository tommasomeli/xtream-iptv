using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using XtreamIptv.Catalog;
using XtreamIptv.Client;

namespace Jellyfin.Plugin.XtreamIptv.Catalog
{
    /// <summary>
    /// The other releases of a movie (another language, another quality) are not items of their
    /// own: Jellyfin asks every media source provider for extra sources of an item at play time, and
    /// this one turns the releases stored on the item into them. Jellyfin probes only the item's own
    /// source, so a release is probed here the first time it is listed and the result kept.
    /// </summary>
    public class XtreamVersionSourceProvider : IMediaSourceProvider
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

        private readonly ILogger<XtreamVersionSourceProvider> _logger;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ConcurrentDictionary<string, MediaSourceInfo> _sources = new ConcurrentDictionary<string, MediaSourceInfo>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _probeFailures = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public XtreamVersionSourceProvider(ILogger<XtreamVersionSourceProvider> logger, IMediaEncoder mediaEncoder)
        {
            _logger = logger;
            _mediaEncoder = mediaEncoder;
        }

        public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            var result = new List<MediaSourceInfo>();
            try
            {
                var movie = item as Movie;
                var config = Plugin.InstanceOrNull?.Configuration;
                if (movie == null || config == null) return result;
                var versions = CatalogNaming.ParseVersions(movie.GetProviderId(JellyfinCatalog.VersionsKey));
                foreach (var v in versions)
                {
                    var id = "xtream-" + CatalogState.Key(v.StreamId);
                    var source = _sources.GetOrAdd(id, _ => new MediaSourceInfo
                    {
                        Id = id,
                        Type = MediaSourceType.Default,
                        Protocol = MediaProtocol.Http,
                        IsRemote = true,
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,
                        SupportsTranscoding = true,
                        MediaStreams = new List<MediaStream>(),
                    });
                    // Name and path follow the item and the connection as they are now.
                    source.Name = movie.Name + " - " + v.Label;
                    source.Path = XtreamUrls.Stream(config, "movie", v.StreamId, v.Extension);
                    source.Container = v.Extension;
                    if (source.MediaStreams == null || source.MediaStreams.Count == 0)
                        await ProbeAsync(source, cancellationToken).ConfigureAwait(false);
                    result.Add(source);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Versions of '{Name}' not listed: {Message}", item?.Name, ex.Message);
            }
            return result;
        }

        /// <summary>ffprobe of the release, once; a failed probe is retried after an hour and the source is still listed (Jellyfin transcodes it).</summary>
        private async Task ProbeAsync(MediaSourceInfo source, CancellationToken cancellationToken)
        {
            DateTime failedAt;
            if (_probeFailures.TryGetValue(source.Id, out failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromHours(1)) return;
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(ProbeTimeout);
                    var info = await _mediaEncoder.GetMediaInfo(new MediaInfoRequest
                    {
                        MediaSource = source,
                        MediaType = DlnaProfileType.Video,
                        ExtractChapters = false,
                    }, cts.Token).ConfigureAwait(false);
                    if (info == null || info.MediaStreams == null || info.MediaStreams.Count == 0)
                    {
                        _probeFailures[source.Id] = DateTime.UtcNow;
                        return;
                    }
                    source.MediaStreams = info.MediaStreams;
                    source.Bitrate = info.Bitrate;
                    source.RunTimeTicks = info.RunTimeTicks;
                    source.Size = info.Size;
                    source.VideoType = info.VideoType;
                    if (!string.IsNullOrEmpty(info.Container)) source.Container = info.Container;
                    source.InferTotalBitrate(true);
                    _probeFailures.TryRemove(source.Id, out failedAt);
                }
            }
            catch (Exception ex)
            {
                _probeFailures[source.Id] = DateTime.UtcNow;
                _logger.LogDebug("Version {Id} not probed: {Message}", source.Id, ex.Message);
            }
        }

        /// <summary>The sources carry their URL and need no opening; Jellyfin never calls this for them.</summary>
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Xtream versions do not need opening");
        }
    }
}
