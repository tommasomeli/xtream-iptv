using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XtreamIptv.Client;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace Emby.XtreamIptv.Catalog
{
    /// <summary>
    /// The other releases of a movie (another language, another quality) are not items of their
    /// own: Emby asks every media source provider for extra sources of an item at play time, and
    /// this one turns the releases stored on the item into them. The source objects are kept, so the
    /// stream info Emby probes on the first play stays with them.
    /// </summary>
    public class XtreamVersionSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, MediaSourceInfo> _sources = new ConcurrentDictionary<string, MediaSourceInfo>(StringComparer.Ordinal);

        public XtreamVersionSourceProvider(ILogManager logManager)
        {
            _logger = logManager.GetLogger("XtreamIptv.Versions");
        }

        public Task<List<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            var result = new List<MediaSourceInfo>();
            try
            {
                var movie = item as Movie;
                var config = Plugin.InstanceOrNull?.Configuration;
                if (movie == null || config == null) return Task.FromResult(result);
                var versions = CatalogNaming.ParseVersions(movie.GetProviderId(EmbyCatalog.VersionsKey));
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
                    result.Add(source);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Versions of '{0}' not listed: {1}", item?.Name, ex.Message);
            }
            return Task.FromResult(result);
        }

        /// <summary>The sources carry their URL and need no opening; Emby never calls this for them. Emby 4.10 shape.</summary>
        public virtual Task<ILiveStream> OpenMediaSource(string openToken, string consumerId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Xtream versions do not need opening");
        }

        /// <summary>Emby 4.9 shape of the same method. The runtime maps whichever shape its interface declares, which needs both to be virtual.</summary>
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Xtream versions do not need opening");
        }
    }
}
