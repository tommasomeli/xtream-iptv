using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using XtreamIptv;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using XtreamIptv.LiveTv;

namespace Jellyfin.Plugin.XtreamIptv.LiveTv
{
    /// <summary>
    /// The Xtream account as a Jellyfin tuner: channels from the filtered live list (Dispatcharr
    /// data layered on when enabled), one media source per channel, one shared live stream per
    /// play. Jellyfin.LiveTv's BaseTunerHost is not in the SDK, so the tuner entries and the
    /// channel cache are handled here.
    /// </summary>
    public class XtreamTunerHost : ITunerHost
    {
        public const string TunerType = "xtream-tuner";
        public const string ChannelIdPrefix = TunerType + "_";

        private readonly IConfigurationManager _config;
        private readonly ILogger<XtreamTunerHost> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IStreamHelper _streamHelper;
        private readonly IServerApplicationHost _appHost;
        private readonly INetworkManager _networkManager;
        private readonly LiveTvService _liveTv;
        private readonly DispatcharrClient _dispatcharrClient;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        private volatile Dictionary<int, StreamStatsInfo> _streamStats = new Dictionary<int, StreamStatsInfo>();
        // Stream id to the codec the channel's Dispatcharr stream profile emits. Built with the
        // channel refresh, never at play time. A missing key means the profile said nothing.
        private volatile Dictionary<int, string> _streamProfileCodecs = new Dictionary<int, string>();
        private volatile Dictionary<int, string> _channelUuidMap = new Dictionary<int, string>();
        private volatile Dictionary<int, string> _tvgIdMap = new Dictionary<int, string>();
        private volatile Dictionary<int, double> _channelNumberMap = new Dictionary<int, double>();
        private volatile HashSet<int> _allowedStreamIds;
        private volatile bool _dispatcharrDataLoaded;
        private volatile bool _xtreamEndpointWarned;
        private List<ChannelInfo> _cachedChannels;
        private DateTime _cacheTime = DateTime.MinValue;

        public XtreamTunerHost(
            IConfigurationManager config,
            ILogger<XtreamTunerHost> logger,
            IFileSystem fileSystem,
            IHttpClientFactory httpClientFactory,
            IStreamHelper streamHelper,
            IServerApplicationHost appHost,
            INetworkManager networkManager,
            LiveTvService liveTv)
        {
            _config = config;
            _logger = logger;
            _fileSystem = fileSystem;
            _httpClientFactory = httpClientFactory;
            _streamHelper = streamHelper;
            _appHost = appHost;
            _networkManager = networkManager;
            _liveTv = liveTv;
            _dispatcharrClient = new DispatcharrClient(new JellyfinLog(logger));
        }

        public string Name => "Xtream IPTV";

        public string Type => TunerType;

        public bool IsSupported => true;

        public IReadOnlyDictionary<int, StreamStatsInfo> StreamStats => _streamStats;

        public IReadOnlyDictionary<int, string> TvgIdMap => _tvgIdMap;

        public int CachedChannelCount => _cachedChannels?.Count ?? 0;

        private static PluginConfiguration Configuration => Plugin.InstanceOrNull?.Configuration;

        /// <summary>The tuner entries of this type in Jellyfin's Live TV settings.</summary>
        public IList<TunerHostInfo> GetTunerHosts()
        {
            var hosts = _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts ?? Array.Empty<TunerHostInfo>();
            return hosts.Where(i => string.Equals(i.Type, TunerType, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // ----- Channel ids ----------------------------------------------------------------------

        public static string BuildChannelId(int streamId) => ChannelIdPrefix + streamId.ToString(CultureInfo.InvariantCulture);

        /// <summary>The stream id behind a channel id of this tuner; false for any other id.</summary>
        public static bool TryParseStreamId(string channelId, out int streamId)
        {
            streamId = 0;
            if (string.IsNullOrEmpty(channelId) || !channelId.StartsWith(ChannelIdPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(channelId.Substring(ChannelIdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out streamId);
        }

        // ----- Channels -------------------------------------------------------------------------

        public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
        {
            var hosts = GetTunerHosts();
            if (hosts.Count == 0) return new List<ChannelInfo>();
            var config = Configuration;
            if (config == null || !config.EnableLiveTv) return new List<ChannelInfo>();

            // One Xtream account per plugin: every entry of this type shows the same channels, so
            // the first entry owns them (Jellyfin keys channels by id and would drop the copies).
            var tuner = hosts[0];
            if (hosts.Count > 1)
                _logger.LogWarning("{Count} Xtream tuners are configured; only the first one is used", hosts.Count);

            var ttl = TimeSpan.FromMinutes(Math.Max(1, config.M3UCacheMinutes));
            var cached = _cachedChannels;
            if (cached != null && (enableCache || DateTime.UtcNow - _cacheTime < ttl))
                return cached;

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = _cachedChannels;
                if (cached != null && (enableCache || DateTime.UtcNow - _cacheTime < ttl))
                    return cached;
                return await RefreshChannelsAsync(tuner, config, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<List<ChannelInfo>> RefreshChannelsAsync(TunerHostInfo tuner, PluginConfiguration config, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Fetching channels from the Xtream account");
            var newStats = new Dictionary<int, StreamStatsInfo>();
            var newProfileCodecs = new Dictionary<int, string>();

            async Task<List<LiveStreamInfo>> ChannelsFetch()
            {
                return await _liveTv.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
            }

            async Task<Dictionary<int, string>> CategoriesFetch()
            {
                try
                {
                    var cats = await _liveTv.GetLiveCategoriesAsync(cancellationToken).ConfigureAwait(false);
                    return cats.GroupBy(c => c.CategoryId).ToDictionary(g => g.Key, g => g.First().CategoryName);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.LogWarning("Failed to fetch live categories for channel groups: {Message}", ex.Message);
                    return new Dictionary<int, string>();
                }
            }

            async Task DispatcharrFetch()
            {
                if (!config.EnableDispatcharr || string.IsNullOrEmpty(config.DispatcharrUrl)) return;
                try
                {
                    var data = await FetchDispatcharrDataAsync(config, cancellationToken).ConfigureAwait(false);
                    newStats = data.Stats;
                    newProfileCodecs = data.ProfileCodecs;
                    _channelUuidMap = data.UuidMap;
                    _tvgIdMap = data.TvgIdMap;
                    _channelNumberMap = data.ChannelNumberMap;
                    _allowedStreamIds = data.AllowedStreamIds;
                    _dispatcharrDataLoaded = true;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.LogWarning("Failed to fetch Dispatcharr channel data: {Message}", ex.Message);
                }
            }

            var channelsTask = ChannelsFetch();
            var categoriesTask = CategoriesFetch();
            var dispatcharrTask = DispatcharrFetch();
            await Task.WhenAll(channelsTask, categoriesTask, dispatcharrTask).ConfigureAwait(false);

            var channels = channelsTask.Result;
            var categoryMap = categoriesTask.Result;

            var allowedIds = _allowedStreamIds;
            if (allowedIds != null)
            {
                var before = channels.Count;
                channels = channels.Where(c => allowedIds.Contains(c.StreamId)).ToList();
                _logger.LogInformation("Dispatcharr profile filter: {Before} to {After} channels", before, channels.Count);
            }

            var result = channels.Select(channel => BuildChannelInfo(channel, tuner, config, categoryMap, _channelNumberMap, _tvgIdMap)).ToList();

            _streamStats = newStats;
            _streamProfileCodecs = newProfileCodecs;
            _cachedChannels = result;
            _cacheTime = DateTime.UtcNow;
            _logger.LogInformation("Channel list cached with {Count} channels ({Stats} with stream stats)", result.Count, newStats.Count);
            return result;
        }

        internal static ChannelInfo BuildChannelInfo(
            LiveStreamInfo channel,
            TunerHostInfo tuner,
            PluginConfiguration config,
            IReadOnlyDictionary<int, string> categoryMap,
            IReadOnlyDictionary<int, double> channelNumberMap,
            IReadOnlyDictionary<int, string> tvgIdMap)
        {
            var cleanName = ChannelNameCleaner.CleanChannelName(channel.Name, config.ChannelRemoveTerms, config.EnableChannelNameCleaning);
            string group = null;
            if (channel.CategoryId.HasValue && categoryMap != null && categoryMap.TryGetValue(channel.CategoryId.Value, out var groupTitle) && !string.IsNullOrEmpty(groupTitle))
                group = groupTitle;

            // Dispatcharr's channel_number allows decimals (2.1); whole numbers print without ".0".
            string number;
            if (config.EnableDispatcharr && channelNumberMap != null && channelNumberMap.TryGetValue(channel.StreamId, out var dispatcharrNum))
                number = dispatcharrNum == Math.Floor(dispatcharrNum)
                    ? ((int)dispatcharrNum).ToString(CultureInfo.InvariantCulture)
                    : dispatcharrNum.ToString("G", CultureInfo.InvariantCulture);
            else
                number = channel.Num.ToString(CultureInfo.InvariantCulture);

            string callSign = null;
            if (config.EnableDispatcharr && tvgIdMap != null && tvgIdMap.TryGetValue(channel.StreamId, out var tvgId) && !string.IsNullOrEmpty(tvgId))
                callSign = tvgId;

            return new ChannelInfo
            {
                Id = BuildChannelId(channel.StreamId),
                TunerChannelId = channel.StreamId.ToString(CultureInfo.InvariantCulture),
                Name = cleanName,
                Number = number,
                CallSign = callSign,
                ImageUrl = string.IsNullOrEmpty(channel.StreamIcon) ? null : channel.StreamIcon,
                ChannelType = ChannelType.TV,
                TunerHostId = tuner?.Id,
                ChannelGroup = group,
                Tags = group != null ? new[] { group } : null,
            };
        }

        public void ClearCaches()
        {
            _cachedChannels = null;
            _cacheTime = DateTime.MinValue;
            _dispatcharrDataLoaded = false;
            _liveTv.InvalidateCache();
            _logger.LogInformation("Xtream tuner caches cleared");
        }

        // ----- Streams --------------------------------------------------------------------------

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            if (!TryParseStreamId(channelId, out var streamId)) return new List<MediaSourceInfo>();
            var hosts = GetTunerHosts();
            if (hosts.Count == 0) return new List<MediaSourceInfo>();
            var source = await BuildMediaSourceAsync(hosts[0], streamId, cancellationToken).ConfigureAwait(false);
            return source == null ? new List<MediaSourceInfo>() : new List<MediaSourceInfo> { source };
        }

        public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            if (!TryParseStreamId(channelId, out var xtreamStreamId))
                throw new FileNotFoundException("Channel " + channelId + " does not belong to the Xtream tuner");
            var hosts = GetTunerHosts();
            if (hosts.Count == 0)
                throw new FileNotFoundException("No Xtream tuner is configured");
            var tuner = hosts[0];

            if (tuner.TunerCount > 0)
            {
                var inUse = currentLiveStreams.Count(i => string.Equals(i.TunerHostId, tuner.Id, StringComparison.OrdinalIgnoreCase));
                if (inUse >= tuner.TunerCount)
                    throw new LiveTvConflictException("Xtream simultaneous stream limit has been reached.");
            }

            var mediaSource = await BuildMediaSourceAsync(tuner, xtreamStreamId, cancellationToken).ConfigureAwait(false);
            if (mediaSource == null)
                throw new FileNotFoundException("Channel " + xtreamStreamId + ": Dispatcharr proxy unavailable and fallback disabled");

            var config = Configuration;
            var userAgent = !string.IsNullOrWhiteSpace(config?.HttpUserAgent) ? config.HttpUserAgent : tuner.UserAgent;
            _logger.LogInformation("Opening live stream for channel {StreamId}", xtreamStreamId);
            return new XtreamLiveStream(mediaSource, tuner, streamId, _fileSystem, _httpClientFactory, _logger, _config, _appHost, _streamHelper, userAgent);
        }

        public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
            => Task.FromResult(new List<TunerHostInfo>());

        private async Task<MediaSourceInfo> BuildMediaSourceAsync(TunerHostInfo tuner, int streamId, CancellationToken cancellationToken)
        {
            var config = Configuration;
            if (config == null) return null;
            await EnsureStatsLoadedAsync(config, cancellationToken).ConfigureAwait(false);

            var (streamUrl, isDispatcharr) = BuildStreamUrl(config, streamId);
            if (streamUrl == null) return null;

            _streamStats.TryGetValue(streamId, out var stats);
            _streamProfileCodecs.TryGetValue(streamId, out var profileCodec);
            // On the Dispatcharr path the profile states what the proxy emits; stream_stats only
            // says what it ingested, and those differ whenever the profile transcodes.
            var declaredVideoCodec = ResolveVideoCodec(isDispatcharr, config.DispatcharrVideoCodecSource, profileCodec,
                stats?.VideoCodec != null ? MapVideoCodec(stats.VideoCodec) : null);

            var isRemote = true;
            if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
            {
                try { isRemote = !_networkManager.IsInLocalNetwork(uri.Host); }
                catch (Exception) { isRemote = true; }
            }

            return CreateMediaSourceInfo(new MediaSourceOptions
            {
                StreamId = streamId,
                StreamUrl = streamUrl,
                Stats = stats,
                DisableProbing = isDispatcharr,
                ForceAudioTranscode = config.ForceAudioTranscode,
                UserAgent = !string.IsNullOrWhiteSpace(config.HttpUserAgent) ? config.HttpUserAgent : tuner.UserAgent,
                FallbackBitrateMbps = config.FallbackTranscodeBitrateMbps,
                DeclareDvbSubtitles = config.DeclareDvbSubtitles,
                DeclaredVideoCodec = declaredVideoCodec,
                IsRemote = isRemote,
                Tuner = tuner,
            });
        }

        /// <summary>Loads the Dispatcharr maps when a play request arrives before any channel refresh.</summary>
        internal async Task EnsureStatsLoadedAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (_dispatcharrDataLoaded) return;
            if (!config.EnableDispatcharr || string.IsNullOrEmpty(config.DispatcharrUrl)) return;

            _logger.LogInformation("Dispatcharr data missing at play time, fetching on demand");
            try
            {
                var data = await FetchDispatcharrDataAsync(config, cancellationToken).ConfigureAwait(false);
                if (data.Stats.Count > 0) _streamStats = data.Stats;
                if (data.ProfileCodecs.Count > 0) _streamProfileCodecs = data.ProfileCodecs;
                if (data.UuidMap.Count > 0) _channelUuidMap = data.UuidMap;
                if (data.TvgIdMap.Count > 0) _tvgIdMap = data.TvgIdMap;
                if (data.ChannelNumberMap.Count > 0) _channelNumberMap = data.ChannelNumberMap;
                _allowedStreamIds = data.AllowedStreamIds;
                _dispatcharrDataLoaded = true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogWarning("On-demand Dispatcharr data fetch failed: {Message}", ex.Message);
            }
        }

        private (string Url, bool IsDispatcharr) BuildStreamUrl(PluginConfiguration config, int streamId)
        {
            var result = ResolveStreamUrl(config, streamId, _channelUuidMap);
            switch (result.Route)
            {
                case StreamRoute.Skipped:
                    _logger.LogWarning("Stream {StreamId}: no Dispatcharr UUID and fallback disabled, skipping", streamId);
                    break;
                case StreamRoute.DirectXtream:
                    if (config.EnableDispatcharr && !string.IsNullOrEmpty(config.DispatcharrUrl))
                        _logger.LogDebug("Stream {StreamId}: no Dispatcharr UUID, falling back to the direct Xtream URL", streamId);
                    break;
            }

            if (result.XtreamEndpointIgnored && !_xtreamEndpointWarned)
            {
                _xtreamEndpointWarned = true;
                _logger.LogWarning("Dispatcharr stream URL is set to Xtream, but the Xtream server URL is not the Dispatcharr URL. Using the Dispatcharr proxy URL instead.");
            }

            return (result.Url, result.Route == StreamRoute.DispatcharrProxy || result.Route == StreamRoute.DispatcharrXtream);
        }

        internal enum StreamRoute { DispatcharrProxy, DispatcharrXtream, DirectXtream, Skipped }

        internal struct StreamUrlResult
        {
            public string Url;
            public StreamRoute Route;
            public bool XtreamEndpointIgnored;
        }

        /// <summary>
        /// Which URL a channel plays from: a channel Dispatcharr knows plays through Dispatcharr (the
        /// proxy URL, or the Xtream URL of the connection when DispatcharrStreamEndpoint is "xtream"
        /// and that connection is the Dispatcharr server); otherwise the direct Xtream URL, unless
        /// the fallback is off.
        /// </summary>
        internal static StreamUrlResult ResolveStreamUrl(PluginConfiguration config, int streamId, IReadOnlyDictionary<int, string> uuidMap)
        {
            var result = new StreamUrlResult();
            if (config.EnableDispatcharr && !string.IsNullOrEmpty(config.DispatcharrUrl))
            {
                if (uuidMap != null && uuidMap.TryGetValue(streamId, out var uuid))
                {
                    if (string.Equals(config.DispatcharrStreamEndpoint, "xtream", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsSameServer(config.BaseUrl, config.DispatcharrUrl))
                        {
                            result.Url = XtreamUrls.Stream(config, "live", streamId, "ts");
                            result.Route = StreamRoute.DispatcharrXtream;
                            return result;
                        }
                        result.XtreamEndpointIgnored = true;
                    }

                    result.Url = string.Format(CultureInfo.InvariantCulture, "{0}/proxy/ts/stream/{1}", config.DispatcharrUrl.TrimEnd('/'), uuid);
                    result.Route = StreamRoute.DispatcharrProxy;
                    return result;
                }

                if (!config.DispatcharrFallbackToXtream)
                {
                    result.Route = StreamRoute.Skipped;
                    return result;
                }
            }

            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
            result.Url = XtreamUrls.Stream(config, "live", streamId, extension);
            result.Route = StreamRoute.DirectXtream;
            return result;
        }

        /// <summary>Same scheme, host, port and path (trailing slash ignored): behind a reverse proxy the path tells services apart.</summary>
        internal static bool IsSameServer(string a, string b)
        {
            if (!Uri.TryCreate(a?.Trim(), UriKind.Absolute, out var ua) || !Uri.TryCreate(b?.Trim(), UriKind.Absolute, out var ub))
                return false;
            return string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
                && ua.Port == ub.Port
                && string.Equals(ua.AbsolutePath.TrimEnd('/'), ub.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
        }

        // ----- Dispatcharr ----------------------------------------------------------------------

        private sealed class DispatcharrData
        {
            public Dictionary<int, string> UuidMap;
            public Dictionary<int, StreamStatsInfo> Stats;
            public Dictionary<int, string> TvgIdMap;
            public HashSet<int> AllowedStreamIds;
            public Dictionary<int, double> ChannelNumberMap;
            public Dictionary<int, string> ProfileCodecs;
        }

        private async Task<DispatcharrData> FetchDispatcharrDataAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            _dispatcharrClient.Configure(config.DispatcharrUser, config.DispatcharrPass);

            HashSet<int> enabledChannelIds = null;
            if (config.SelectedDispatcharrProfileIds != null && config.SelectedDispatcharrProfileIds.Length > 0)
            {
                var profiles = await _dispatcharrClient.GetProfilesAsync(config.DispatcharrUrl, cancellationToken).ConfigureAwait(false);
                enabledChannelIds = new HashSet<int>();
                foreach (var profile in profiles)
                {
                    if (Array.IndexOf(config.SelectedDispatcharrProfileIds, profile.Id) >= 0)
                        foreach (var chId in profile.Channels) enabledChannelIds.Add(chId);
                }
            }

            // The two profile requests ride along with the channel-data request: serial round trips
            // in front of a play request are what made the first tune slow.
            var profilesTask = _dispatcharrClient.GetStreamProfilesAsync(config.DispatcharrUrl, cancellationToken);
            var defaultIdTask = _dispatcharrClient.GetDefaultStreamProfileIdAsync(config.DispatcharrUrl, cancellationToken);

            var (uuidMap, statsMap, tvgIdMap, _, allowedStreamIds, channelNumberMap, streamProfileIdMap) =
                await _dispatcharrClient.GetChannelDataAsync(config.DispatcharrUrl, cancellationToken, enabledChannelIds).ConfigureAwait(false);

            Dictionary<int, string> profileCodecs;
            try
            {
                var profiles = await profilesTask.ConfigureAwait(false);
                var defaultProfileId = await defaultIdTask.ConfigureAwait(false);
                profileCodecs = streamProfileIdMap == null || streamProfileIdMap.Count == 0 || profiles == null || profiles.Count == 0
                    ? new Dictionary<int, string>()
                    : StreamProfileCodec.BuildCodecMap(streamProfileIdMap, profiles, defaultProfileId);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogWarning("Could not resolve Dispatcharr stream profile codecs: {Message}", ex.Message);
                profileCodecs = new Dictionary<int, string>();
            }

            return new DispatcharrData
            {
                UuidMap = uuidMap,
                Stats = statsMap,
                TvgIdMap = tvgIdMap,
                AllowedStreamIds = allowedStreamIds,
                ChannelNumberMap = channelNumberMap,
                ProfileCodecs = profileCodecs,
            };
        }

        // ----- Media source ---------------------------------------------------------------------

        internal sealed class MediaSourceOptions
        {
            public int StreamId;
            public string StreamUrl;
            public StreamStatsInfo Stats;
            public bool DisableProbing;
            public bool ForceAudioTranscode;
            public string UserAgent;
            public int FallbackBitrateMbps;
            public bool DeclareDvbSubtitles;
            public string DeclaredVideoCodec;
            public bool IsRemote = true;
            public TunerHostInfo Tuner;
        }

        /// <summary>
        /// The media source Jellyfin plays. With Dispatcharr stats the streams are declared with
        /// real indexes, which Jellyfin takes as "known" and does not probe; a Dispatcharr URL is
        /// never probed anyway (the probe's short connection makes Dispatcharr tear the channel
        /// down). Without stats the streams carry index -1 so Jellyfin probes the direct URL.
        /// </summary>
        internal static MediaSourceInfo CreateMediaSourceInfo(MediaSourceOptions o)
        {
            var stats = o.Stats;
            var tuner = o.Tuner ?? new TunerHostInfo();

            bool isAudioOnly = stats != null && stats.VideoCodec == null && !string.IsNullOrEmpty(stats.AudioCodec);
            var statsVideoCodec = stats?.VideoCodec != null ? MapVideoCodec(stats.VideoCodec) : null;
            bool hasStats = stats != null;
            bool suppressProbing = o.DisableProbing || hasStats;
            var audioCodecLower = hasStats && !string.IsNullOrEmpty(stats.AudioCodec) ? stats.AudioCodec.ToLowerInvariant() : null;

            // ForceAudioTranscode: no direct stream unless stats prove a codec Apple devices play.
            bool suppressDirectStream = o.ForceAudioTranscode
                && (!hasStats || audioCodecLower == "ac3" || audioCodecLower == "eac3" || audioCodecLower == "mp2");

            var mediaSource = new MediaSourceInfo
            {
                Id = "xtream_live_" + o.StreamId.ToString(CultureInfo.InvariantCulture),
                Path = o.StreamUrl,
                Protocol = MediaProtocol.Http,
                Container = "mpegts",
                SupportsProbing = !suppressProbing,
                IsRemote = o.IsRemote,
                IsInfiniteStream = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = !suppressDirectStream,
                SupportsTranscoding = true,
                AnalyzeDurationMs = suppressProbing ? 0 : 500,
                RequiresOpening = true,
                RequiresClosing = true,
                RequiresLooping = false,
                IgnoreDts = tuner.IgnoreDts,
                ReadAtNativeFramerate = tuner.ReadAtNativeFramerate,
                UseMostCompatibleTranscodingProfile = !tuner.AllowFmp4TranscodingContainer,
                FallbackMaxStreamingBitrate = tuner.FallbackMaxStreamingBitrate,
            };

            if (!string.IsNullOrEmpty(o.UserAgent))
                mediaSource.RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = o.UserAgent };

            if (hasStats)
            {
                var mediaStreams = new List<MediaStream>();
                if (!isAudioOnly)
                {
                    ParseResolution(stats.Resolution, out var width, out var height);
                    var videoCodec = !string.IsNullOrEmpty(o.DeclaredVideoCodec) ? o.DeclaredVideoCodec : (statsVideoCodec ?? "h264");
                    var declareCodecAttributes = ShouldDeclareCodecAttributes(videoCodec, statsVideoCodec);
                    var videoStream = new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        Codec = videoCodec,
                        IsInterlaced = false,
                        PixelFormat = "yuv420p",
                    };
                    if (width > 0) videoStream.Width = width;
                    if (height > 0) videoStream.Height = height;
                    if (stats.SourceFps.HasValue)
                    {
                        videoStream.RealFrameRate = (float)stats.SourceFps.Value;
                        videoStream.AverageFrameRate = (float)stats.SourceFps.Value;
                    }
                    if (stats.Bitrate.HasValue) videoStream.BitRate = (int)(stats.Bitrate.Value * 1000);
                    // Profile, level, bit depth and reference frames describe the ingested codec and
                    // only hold when that is the codec declared.
                    if (declareCodecAttributes)
                    {
                        if (!string.IsNullOrEmpty(stats.VideoProfile)) videoStream.Profile = stats.VideoProfile;
                        if (stats.VideoLevel.HasValue) videoStream.Level = stats.VideoLevel.Value;
                        if (stats.VideoBitDepth.HasValue) videoStream.BitDepth = stats.VideoBitDepth.Value;
                        if (stats.VideoRefFrames.HasValue) videoStream.RefFrames = stats.VideoRefFrames.Value;
                    }
                    mediaStreams.Add(videoStream);
                }

                int? audioChannels = null;
                string channelLayout = null;
                if (!string.IsNullOrEmpty(stats.AudioChannels))
                {
                    audioChannels = ParseAudioChannelCount(stats.AudioChannels);
                    channelLayout = stats.AudioChannels;
                }
                else if (audioCodecLower == "ac3" || audioCodecLower == "eac3")
                {
                    audioChannels = 6;
                    channelLayout = "5.1(side)";
                }
                else if (audioCodecLower == "mp2" || audioCodecLower == "mp1")
                {
                    audioChannels = 2;
                    channelLayout = "stereo";
                }

                var audioStream = new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = isAudioOnly ? 0 : 1,
                    Codec = audioCodecLower ?? "aac",
                    Channels = audioChannels,
                    ChannelLayout = channelLayout,
                    SampleRate = stats.SampleRate,
                };
                if (stats.AudioBitrate.HasValue) audioStream.BitRate = (int)(stats.AudioBitrate.Value * 1000);
                else if (isAudioOnly && stats.Bitrate.HasValue) audioStream.BitRate = (int)(stats.Bitrate.Value * 1000);
                else if (audioCodecLower == "ac3") audioStream.BitRate = 384000;
                else if (audioCodecLower == "eac3") audioStream.BitRate = 640000;
                else if (audioCodecLower == "aac") audioStream.BitRate = 128000;
                else if (audioCodecLower == "mp2" || audioCodecLower == "mp1") audioStream.BitRate = 256000;
                if (!string.IsNullOrEmpty(stats.AudioLanguage)) audioStream.Language = stats.AudioLanguage;
                mediaStreams.Add(audioStream);

                if (o.DeclareDvbSubtitles)
                {
                    var subLang = !string.IsNullOrEmpty(stats.AudioLanguage) ? stats.AudioLanguage : null;
                    mediaStreams.Add(new MediaStream { Type = MediaStreamType.Subtitle, Index = mediaStreams.Count, Codec = "dvb_subtitle", Language = subLang });
                    mediaStreams.Add(new MediaStream { Type = MediaStreamType.Subtitle, Index = mediaStreams.Count, Codec = "dvb_subtitle", Language = subLang, IsHearingImpaired = true });
                }

                mediaSource.MediaStreams = mediaStreams;
                mediaSource.DefaultAudioStreamIndex = isAudioOnly ? 0 : 1;
            }
            else
            {
                // Unknown streams: index -1 lets Jellyfin probe the direct URL at open time. A
                // Dispatcharr URL is not probed, so H.264/AAC stand in as the common IPTV output.
                var index = suppressProbing ? 0 : -1;
                var videoStream = new MediaStream { Type = MediaStreamType.Video, Index = index, Codec = suppressProbing ? "h264" : null, IsInterlaced = !suppressProbing };
                var audioStream = new MediaStream { Type = MediaStreamType.Audio, Index = suppressProbing ? 1 : -1, Codec = suppressProbing ? "aac" : null };
                if (o.FallbackBitrateMbps > 0)
                {
                    int videoBps = o.FallbackBitrateMbps * 1_000_000;
                    videoStream.BitRate = videoBps;
                    audioStream.BitRate = 192_000;
                    mediaSource.Bitrate = videoBps + 192_000;
                }
                mediaSource.MediaStreams = new List<MediaStream> { videoStream, audioStream };
                if (suppressProbing) mediaSource.DefaultAudioStreamIndex = 1;
            }

            mediaSource.InferTotalBitrate();
            return mediaSource;
        }

        internal static void ParseResolution(string resolution, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(resolution)) return;
            var parts = resolution.Split('x');
            if (parts.Length != 2) return;
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width);
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
        }

        /// <summary>"5.1", "7.1", "stereo", "mono", "2.0" to a channel count; null when unknown.</summary>
        internal static int? ParseAudioChannelCount(string layout)
        {
            if (string.IsNullOrEmpty(layout)) return null;
            var lower = layout.ToLowerInvariant().Trim();
            if (lower == "mono") return 1;
            if (lower == "stereo") return 2;
            var dot = lower.IndexOf('.');
            if (dot > 0
                && int.TryParse(lower.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out int main)
                && int.TryParse(lower.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int lfe))
                return main + lfe;
            if (int.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out int plain)) return plain;
            return null;
        }

        internal const string CodecSourceStats = "stats";
        internal const string CodecSourceH264 = "h264";
        internal const string CodecSourceHevc = "hevc";

        internal static string MapVideoCodec(string dispatcharrCodec)
        {
            var upper = dispatcharrCodec.ToUpperInvariant();
            if (upper == "H264" || upper == "AVC") return "h264";
            if (upper == "HEVC" || upper == "H265") return "hevc";
            if (upper == "MPEG2VIDEO") return "mpeg2video";
            return dispatcharrCodec.ToLowerInvariant();
        }

        /// <summary>
        /// The video codec declared for a channel. Dispatcharr path: the profile wins (it states what
        /// the proxy emits), "stats" trusts the ingested codec, "h264"/"hevc" force one. A direct
        /// Xtream URL carries the provider's own bytes, so the ingested codec is right there.
        /// </summary>
        internal static string ResolveVideoCodec(bool isDispatcharr, string codecSource, string profileCodec, string statsCodec)
        {
            if (!isDispatcharr) return statsCodec;
            switch ((codecSource ?? string.Empty).Trim().ToLowerInvariant())
            {
                case CodecSourceStats: return statsCodec;
                case CodecSourceH264: return "h264";
                case CodecSourceHevc: return "hevc";
                default: return profileCodec ?? statsCodec;
            }
        }

        internal static bool ShouldDeclareCodecAttributes(string declaredCodec, string statsCodec)
            => !string.IsNullOrEmpty(declaredCodec) && !string.IsNullOrEmpty(statsCodec)
               && string.Equals(declaredCodec, statsCodec, StringComparison.OrdinalIgnoreCase);
    }
}
