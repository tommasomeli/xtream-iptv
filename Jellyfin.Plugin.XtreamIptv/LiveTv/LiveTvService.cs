using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XtreamIptv;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using STJ = System.Text.Json;

namespace Jellyfin.Plugin.XtreamIptv.LiveTv
{
    /// <summary>
    /// Channel list, categories and guide data from the Xtream account, plus the M3U and XMLTV
    /// documents the routes serve. Same behaviour as the Emby LiveTvService; the tuner host and the
    /// listings provider are its consumers.
    /// </summary>
    public class LiveTvService : IDisposable
    {
        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private readonly ILog _logger;
        private readonly Func<PluginConfiguration> _config;
        private readonly SemaphoreSlim _m3uLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _epgLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _xmltvLock = new SemaphoreSlim(1, 1);
        private readonly object _perChannelEpgLock = new object();

        private Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)> _perChannelEpgCache
            = new Dictionary<int, (List<EpgProgram>, DateTime)>();

        // Bulk XMLTV cache: epg_channel_id to programs (from xmltv.php or the custom URL).
        private Dictionary<string, List<EpgProgram>> _xmltvCache;
        private DateTime _xmltvCacheTime = DateTime.MinValue;
        private bool _xmltvFailed;
        private DateTime _xmltvFailTime = DateTime.MinValue;
        private Dictionary<int, string> _epgChannelIdByStreamId = new Dictionary<int, string>();

        private string _cachedM3U;
        private string _cachedEpgXml;
        private DateTime _m3uCacheTime = DateTime.MinValue;
        private DateTime _epgCacheTime = DateTime.MinValue;
        private bool _disposed;

        public LiveTvService(ILog logger, Func<PluginConfiguration> config = null)
        {
            _logger = logger;
            _config = config ?? (() => Plugin.Instance.Configuration);
        }

        private PluginConfiguration Config => _config();

        /// <summary>The M3U playlist of the filtered channels (cached M3UCacheMinutes).</summary>
        public async Task<string> GetM3UPlaylistAsync(IReadOnlyDictionary<int, string> tvgIdMap, CancellationToken cancellationToken)
        {
            var config = Config;
            await _m3uLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedM3U != null && DateTime.UtcNow - _m3uCacheTime < TimeSpan.FromMinutes(Math.Max(1, config.M3UCacheMinutes)))
                    return _cachedM3U;

                _logger.Info("Generating M3U playlist");
                var channelsTask = GetFilteredChannelsAsync(cancellationToken);
                var categoriesTask = GetLiveCategoriesAsync(cancellationToken);
                Dictionary<int, string> categoryMap;
                try
                {
                    await Task.WhenAll(channelsTask, categoriesTask).ConfigureAwait(false);
                    categoryMap = categoriesTask.Result.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to fetch live categories for M3U group-title; categories will be omitted: {0}", ex.Message);
                    await channelsTask.ConfigureAwait(false);
                    categoryMap = new Dictionary<int, string>();
                }

                _cachedM3U = GenerateM3U(channelsTask.Result, config, categoryMap, tvgIdMap);
                _m3uCacheTime = DateTime.UtcNow;
                return _cachedM3U;
            }
            finally
            {
                _m3uLock.Release();
            }
        }

        /// <summary>The XMLTV guide of the filtered channels (cached EpgCacheMinutes).</summary>
        public async Task<string> GetXmltvEpgAsync(CancellationToken cancellationToken)
        {
            var config = Config;
            await _epgLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedEpgXml != null && DateTime.UtcNow - _epgCacheTime < TimeSpan.FromMinutes(Math.Max(1, config.EpgCacheMinutes)))
                    return _cachedEpgXml;

                _logger.Info("Generating XMLTV EPG");
                var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                _cachedEpgXml = await GenerateXmltvAsync(channels, config, cancellationToken).ConfigureAwait(false);
                _epgCacheTime = DateTime.UtcNow;
                return _cachedEpgXml;
            }
            finally
            {
                _epgLock.Release();
            }
        }

        public async Task<List<Category>> GetLiveCategoriesAsync(CancellationToken cancellationToken)
        {
            var url = XtreamUrls.PlayerApi(Config, "get_live_categories");
            using (var httpClient = XtreamHost.CreateHttpClient(30))
            {
                var json = await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var categories = STJ.JsonSerializer.Deserialize<List<Category>>(json, JsonOptions) ?? new List<Category>();
                return categories.OrderBy(c => c.CategoryName, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public void InvalidateCache()
        {
            _cachedM3U = null;
            _cachedEpgXml = null;
            _m3uCacheTime = DateTime.MinValue;
            _epgCacheTime = DateTime.MinValue;
            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache = new Dictionary<int, (List<EpgProgram>, DateTime)>();
            }
            _xmltvCache = null;
            _xmltvCacheTime = DateTime.MinValue;
            _xmltvFailed = false;
            _xmltvFailTime = DateTime.MinValue;
            _epgChannelIdByStreamId = new Dictionary<int, string>();
            _logger.Info("Live TV cache invalidated");
        }

        /// <summary>The account's live channels after the category and adult filters.</summary>
        public async Task<List<LiveStreamInfo>> GetFilteredChannelsAsync(CancellationToken cancellationToken)
        {
            var config = Config;
            List<LiveStreamInfo> allChannels;

            if (config.SelectedLiveCategoryIds != null && config.SelectedLiveCategoryIds.Length > 0)
            {
                allChannels = new List<LiveStreamInfo>();
                var semaphore = new SemaphoreSlim(5);
                var tasks = config.SelectedLiveCategoryIds.Select(async categoryId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try { return await FetchChannelsByCategoryAsync(categoryId, cancellationToken).ConfigureAwait(false); }
                    finally { semaphore.Release(); }
                });
                foreach (var result in await Task.WhenAll(tasks).ConfigureAwait(false))
                    allChannels.AddRange(result);
                allChannels = allChannels.GroupBy(c => c.StreamId).Select(g => g.First()).ToList();
            }
            else
            {
                allChannels = await FetchAllChannelsAsync(cancellationToken).ConfigureAwait(false);
            }

            // Categories were already applied by requesting them one by one.
            allChannels = ApplyChannelFilters(allChannels, config, filterCategories: false);
            _logger.Info("Fetched {0} Live TV channels", allChannels.Count);
            return allChannels;
        }

        /// <summary>
        /// The user's channel filters: selected categories (none selected means all) and the adult
        /// setting. The normal fetch passes filterCategories false because it requests each category
        /// separately and a provider's category_id on the result is not reliable enough to filter twice.
        /// </summary>
        internal static List<LiveStreamInfo> ApplyChannelFilters(List<LiveStreamInfo> channels, PluginConfiguration config, bool filterCategories = true)
        {
            IEnumerable<LiveStreamInfo> result = channels;
            if (filterCategories && config.SelectedLiveCategoryIds != null && config.SelectedLiveCategoryIds.Length > 0)
            {
                var selected = new HashSet<int>(config.SelectedLiveCategoryIds);
                result = result.Where(c => c.CategoryId.HasValue && selected.Contains(c.CategoryId.Value));
            }
            if (!config.IncludeAdultChannels)
                result = result.Where(c => !c.IsAdultChannel);
            return result.ToList();
        }

        private async Task<List<LiveStreamInfo>> FetchAllChannelsAsync(CancellationToken cancellationToken)
        {
            var url = XtreamUrls.PlayerApi(Config, "get_live_streams");
            using (var httpClient = XtreamHost.CreateHttpClient(30))
            {
                var json = await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                return STJ.JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, JsonOptions) ?? new List<LiveStreamInfo>();
            }
        }

        private async Task<List<LiveStreamInfo>> FetchChannelsByCategoryAsync(int categoryId, CancellationToken cancellationToken)
        {
            var url = XtreamUrls.PlayerApi(Config, "get_live_streams", "category_id", categoryId);
            using (var httpClient = XtreamHost.CreateHttpClient(30))
            {
                try
                {
                    var json = await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    return STJ.JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, JsonOptions) ?? new List<LiveStreamInfo>();
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Warn("Failed to fetch channels for category {0}: {1}", categoryId, ex.Message);
                    return new List<LiveStreamInfo>();
                }
            }
        }

        internal static string GenerateM3U(
            List<LiveStreamInfo> channels,
            PluginConfiguration config,
            Dictionary<int, string> categoryNames,
            IReadOnlyDictionary<int, string> tvgIdMap = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");
            foreach (var channel in channels.OrderBy(c => c.Num))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(channel.Name, config.ChannelRemoveTerms, config.EnableChannelNameCleaning);
                // tvg-id: the Dispatcharr tvg_id override, else the Xtream epg_channel_id, else the stream id.
                string epgId;
                if (tvgIdMap != null && tvgIdMap.TryGetValue(channel.StreamId, out var dispatcharrTvgId) && !string.IsNullOrEmpty(dispatcharrTvgId))
                    epgId = dispatcharrTvgId;
                else
                    epgId = !string.IsNullOrEmpty(channel.EpgChannelId) ? channel.EpgChannelId : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                var extinf = new StringBuilder();
                extinf.Append("#EXTINF:-1");
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-id=\"{0}\"", EscapeAttribute(epgId));
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-name=\"{0}\"", EscapeAttribute(cleanName));
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-chno=\"{0}\"", channel.Num);
                if (!string.IsNullOrEmpty(channel.StreamIcon))
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-logo=\"{0}\"", EscapeAttribute(channel.StreamIcon));
                if (channel.CategoryId.HasValue && categoryNames.TryGetValue(channel.CategoryId.Value, out var groupTitle) && !string.IsNullOrEmpty(groupTitle))
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " group-title=\"{0}\"", EscapeAttribute(groupTitle));
                extinf.AppendFormat(CultureInfo.InvariantCulture, ",{0}", cleanName);
                sb.AppendLine(extinf.ToString());
                sb.AppendLine(BuildStreamUrl(config, channel));
            }
            return sb.ToString();
        }

        internal static string BuildStreamUrl(PluginConfiguration config, LiveStreamInfo channel)
        {
            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
            return XtreamUrls.Stream(config, "live", channel.StreamId, extension);
        }

        private async Task<string> GenerateXmltvAsync(List<LiveStreamInfo> channels, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<tv generator-info-name=\"Xtream IPTV\">");
            foreach (var channel in channels.OrderBy(c => c.Num))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(channel.Name, config.ChannelRemoveTerms, config.EnableChannelNameCleaning);
                var channelId = !string.IsNullOrEmpty(channel.EpgChannelId) ? channel.EpgChannelId : channel.StreamId.ToString(CultureInfo.InvariantCulture);
                sb.AppendFormat(CultureInfo.InvariantCulture, "  <channel id=\"{0}\">\n", EscapeXml(channelId));
                sb.AppendFormat(CultureInfo.InvariantCulture, "    <display-name>{0}</display-name>\n", EscapeXml(cleanName));
                if (!string.IsNullOrEmpty(channel.StreamIcon))
                    sb.AppendFormat(CultureInfo.InvariantCulture, "    <icon src=\"{0}\" />\n", EscapeXml(channel.StreamIcon));
                sb.AppendLine("  </channel>");
            }

            if (config.EpgSource != EpgSourceMode.Disabled)
            {
                var epgData = await FetchEpgDataAsync(channels, config, cancellationToken).ConfigureAwait(false);
                foreach (var program in epgData.OrderBy(p => p.StartTimestamp))
                {
                    var channelId = !string.IsNullOrEmpty(program.ChannelId) ? program.ChannelId : program.EpgId;
                    sb.AppendFormat(CultureInfo.InvariantCulture, "  <programme start=\"{0}\" stop=\"{1}\" channel=\"{2}\">\n",
                        FormatXmltvTime(program.StartTimestamp), FormatXmltvTime(program.StopTimestamp), EscapeXml(channelId));
                    sb.AppendFormat(CultureInfo.InvariantCulture, "    <title>{0}</title>\n", EscapeXml(program.IsPlainText ? program.Title : DecodeBase64(program.Title)));
                    var desc = program.IsPlainText ? program.Description : DecodeBase64(program.Description);
                    if (!string.IsNullOrEmpty(desc))
                        sb.AppendFormat(CultureInfo.InvariantCulture, "    <desc>{0}</desc>\n", EscapeXml(desc));
                    if (program.IsLive) sb.AppendLine("    <live />");
                    if (program.IsNew) sb.AppendLine("    <new />");
                    if (program.IsPreviouslyShown) sb.AppendLine("    <previously-shown />");
                    if (program.IsPremiere) sb.AppendLine("    <premiere />");
                    sb.AppendLine("  </programme>");
                }
            }

            sb.AppendLine("</tv>");
            return sb.ToString();
        }

        private async Task<List<EpgProgram>> FetchEpgDataAsync(List<LiveStreamInfo> channels, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var allPrograms = new List<EpgProgram>();
            var semaphore = new SemaphoreSlim(5);
            var now = DateTimeOffset.UtcNow;
            var endUnix = now.AddDays(Math.Max(1, config.EpgDaysToFetch)).ToUnixTimeSeconds();
            var nowUnix = now.ToUnixTimeSeconds();

            var tasks = channels.Select(async channel =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var programs = await FetchEpgForChannelCachedAsync(channel.StreamId, cancellationToken).ConfigureAwait(false);
                    var channelId = !string.IsNullOrEmpty(channel.EpgChannelId) ? channel.EpgChannelId : channel.StreamId.ToString(CultureInfo.InvariantCulture);
                    foreach (var program in programs)
                    {
                        if (string.IsNullOrEmpty(program.ChannelId)) program.ChannelId = channelId;
                    }
                    return programs.Where(p => p.StopTimestamp > nowUnix && p.StartTimestamp < endUnix).ToList();
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Debug("Failed to fetch EPG for channel {0}: {1}", channel.StreamId, ex.Message);
                    return new List<EpgProgram>();
                }
                finally
                {
                    semaphore.Release();
                }
            });

            foreach (var result in await Task.WhenAll(tasks).ConfigureAwait(false))
                allPrograms.AddRange(result);
            _logger.Info("Fetched {0} EPG programs for {1} channels", allPrograms.Count, channels.Count);
            return allPrograms;
        }

        /// <summary>
        /// Guide data of one channel, cached per channel. The bulk XMLTV document is tried first
        /// (it carries the live, repeat, new and premiere flags); the per-channel JSON call is the
        /// fallback on the Xtream source only.
        /// </summary>
        public async Task<List<EpgProgram>> FetchEpgForChannelCachedAsync(int streamId, CancellationToken cancellationToken)
        {
            var config = Config;
            if (config.EpgSource == EpgSourceMode.Disabled) return new List<EpgProgram>();
            var cacheTtl = TimeSpan.FromMinutes(Math.Max(1, config.EpgCacheMinutes));

            lock (_perChannelEpgLock)
            {
                if (_perChannelEpgCache.TryGetValue(streamId, out var entry) && DateTime.UtcNow - entry.CacheTime < cacheTtl)
                    return entry.Programs;
            }

            var xmltvCacheFresh = _xmltvCache != null && DateTime.UtcNow - _xmltvCacheTime < cacheTtl;
            if (xmltvCacheFresh)
            {
                var programs = PopulateFromXmltvCache(streamId);
                if (programs != null) return programs;
            }

            // A failed bulk fetch is retried once per cache window, not per channel.
            var xmltvRetryAllowed = !_xmltvFailed || (DateTime.UtcNow - _xmltvFailTime) >= cacheTtl;
            if (!xmltvCacheFresh && xmltvRetryAllowed)
            {
                if (await TryFetchXmltvEpgAsync(cancellationToken).ConfigureAwait(false))
                {
                    var programs = PopulateFromXmltvCache(streamId);
                    if (programs != null) return programs;
                }
            }

            if (config.EpgSource == EpgSourceMode.CustomUrl)
                return new List<EpgProgram>();

            var epgListings = await FetchEpgForChannelAsync(streamId, cancellationToken).ConfigureAwait(false);
            var jsonPrograms = epgListings?.Listings ?? new List<EpgProgram>();
            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache[streamId] = (jsonPrograms, DateTime.UtcNow);
            }
            return jsonPrograms;
        }

        private List<EpgProgram> PopulateFromXmltvCache(int streamId)
        {
            if (!_epgChannelIdByStreamId.TryGetValue(streamId, out var epgChannelId))
                epgChannelId = streamId.ToString(CultureInfo.InvariantCulture);
            var cache = _xmltvCache;
            if (cache == null || !cache.TryGetValue(epgChannelId, out var xmltvPrograms)) return null;
            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache[streamId] = (xmltvPrograms, DateTime.UtcNow);
            }
            return xmltvPrograms;
        }

        private async Task<bool> TryFetchXmltvEpgAsync(CancellationToken cancellationToken)
        {
            await _xmltvLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var config = Config;
                var cacheTtl = TimeSpan.FromMinutes(Math.Max(1, config.EpgCacheMinutes));
                if (_xmltvCache != null && DateTime.UtcNow - _xmltvCacheTime < cacheTtl) return true;

                string url;
                if (config.EpgSource == EpgSourceMode.CustomUrl && !string.IsNullOrWhiteSpace(config.CustomEpgUrl))
                {
                    url = config.CustomEpgUrl;
                    _logger.Info("Fetching bulk XMLTV EPG from the custom URL");
                }
                else
                {
                    url = XtreamUrls.Xmltv(config);
                    _logger.Info("Fetching bulk XMLTV EPG from the Xtream server");
                }

                try
                {
                    var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                    var mapping = new Dictionary<int, string>(channels.Count);
                    foreach (var ch in channels)
                        mapping[ch.StreamId] = !string.IsNullOrEmpty(ch.EpgChannelId) ? ch.EpgChannelId : ch.StreamId.ToString(CultureInfo.InvariantCulture);

                    var now = DateTimeOffset.UtcNow;
                    var filterEndUnix = now.AddDays(Math.Max(1, config.EpgDaysToFetch)).ToUnixTimeSeconds();
                    Dictionary<string, List<EpgProgram>> xmltvData;
                    using (var httpClient = XtreamHost.CreateHttpClient(180))
                    using (var stream = await httpClient.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
                    {
                        xmltvData = XmltvParser.Parse(stream, now.ToUnixTimeSeconds(), filterEndUnix);
                    }

                    _xmltvCache = xmltvData;
                    _epgChannelIdByStreamId = mapping;
                    _xmltvCacheTime = DateTime.UtcNow;
                    _xmltvFailed = false;
                    _logger.Info("XMLTV EPG fetched: {0} channels with program data", xmltvData.Count);
                    return true;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    _xmltvFailed = true;
                    _xmltvFailTime = DateTime.UtcNow;
                    _logger.Warn(config.EpgSource == EpgSourceMode.CustomUrl
                        ? "Custom EPG URL fetch failed, no fallback: {0}"
                        : "XMLTV EPG fetch failed, falling back to per-channel JSON: {0}", ex.Message);
                    return false;
                }
            }
            finally
            {
                _xmltvLock.Release();
            }
        }

        private async Task<EpgListings> FetchEpgForChannelAsync(int streamId, CancellationToken cancellationToken)
        {
            var url = XtreamUrls.PlayerApi(Config, "get_simple_data_table", "stream_id", streamId);
            using (var httpClient = XtreamHost.CreateHttpClient(30))
            {
                var json = await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                return STJ.JsonSerializer.Deserialize<EpgListings>(json, JsonOptions);
            }
        }

        private static string FormatXmltvTime(long unixTimestamp)
            => DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        internal static string DecodeBase64(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch (FormatException) { return value; }
        }

        private static string EscapeAttribute(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\"", "&quot;").Replace("&", "&amp;");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _m3uLock.Dispose();
            _epgLock.Dispose();
            _xmltvLock.Dispose();
            _disposed = true;
        }
    }
}
