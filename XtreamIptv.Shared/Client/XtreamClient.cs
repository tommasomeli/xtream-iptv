using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using XtreamIptv.Client.Models;
using XtreamIptv.Logging;

namespace XtreamIptv.Client
{
    /// <summary>
    /// Typed <c>player_api.php</c> calls shared by the catalogue code. Every call goes through the
    /// plugin HttpClient (rate limiter + User-Agent). List calls get a long timeout because a whole
    /// account is one response; detail calls a short one because they run while a user waits.
    /// </summary>
    public sealed class XtreamClient
    {
        public const int ListTimeoutSeconds = 120;
        public const int DetailTimeoutSeconds = 30;

        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new TolerantStringConverter(),
                new TolerantNullableIntConverter(),
            },
        };

        private readonly ILog _logger;
        private readonly Func<int, HttpClient> _httpFactory;
        private readonly object _sync = new object();
        private HttpClient _listClient;
        private HttpClient _detailClient;

        /// <param name="httpFactory">Builds a client for a timeout in seconds; defaults to the host factory.</param>
        public XtreamClient(ILog logger, Func<int, HttpClient> httpFactory = null)
        {
            _logger = logger;
            _httpFactory = httpFactory ?? (timeout => XtreamHost.CreateHttpClient(timeout));
        }

        public Task<List<Category>> GetVodCategoriesAsync(PluginConfiguration config, CancellationToken ct)
            => GetListAsync<Category>(config, "get_vod_categories", null, null, ct);

        public Task<List<Category>> GetSeriesCategoriesAsync(PluginConfiguration config, CancellationToken ct)
            => GetListAsync<Category>(config, "get_series_categories", null, null, ct);

        /// <summary>The whole VOD list of the account (no category filter: one call).</summary>
        public Task<List<VodStreamInfo>> GetVodStreamsAsync(PluginConfiguration config, CancellationToken ct)
            => GetListAsync<VodStreamInfo>(config, "get_vod_streams", null, null, ct);

        public Task<List<SeriesInfo>> GetSeriesAsync(PluginConfiguration config, CancellationToken ct)
            => GetListAsync<SeriesInfo>(config, "get_series", null, null, ct);

        public async Task<SeriesDetailInfo> GetSeriesInfoAsync(PluginConfiguration config, int seriesId, CancellationToken ct)
        {
            var json = await GetStringAsync(DetailClient, XtreamUrls.PlayerApi(config, "get_series_info", "series_id", seriesId), ct).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return new SeriesDetailInfo();
                return doc.RootElement.Deserialize<SeriesDetailInfo>(JsonOptions) ?? new SeriesDetailInfo();
            }
        }

        public async Task<VodInfo> GetVodInfoAsync(PluginConfiguration config, int vodId, CancellationToken ct)
        {
            var json = await GetStringAsync(DetailClient, XtreamUrls.PlayerApi(config, "get_vod_info", "vod_id", vodId), ct).ConfigureAwait(false);
            return ParseVodInfo(json);
        }

        /// <summary>Tolerates <c>"info": []</c>, the provider's way of saying "no detail".</summary>
        internal static VodInfo ParseVodInfo(string json)
        {
            var result = new VodInfo();
            if (string.IsNullOrWhiteSpace(json)) return result;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
                JsonElement info;
                if (doc.RootElement.TryGetProperty("info", out info) && info.ValueKind == JsonValueKind.Object)
                    result.Info = SafeDeserialize<VodInfoDetail>(info);
                JsonElement data;
                if (doc.RootElement.TryGetProperty("movie_data", out data) && data.ValueKind == JsonValueKind.Object)
                    result.MovieData = SafeDeserialize<VodMovieData>(data);
            }
            return result;
        }

        private async Task<List<T>> GetListAsync<T>(PluginConfiguration config, string action, string paramName, object paramValue, CancellationToken ct) where T : class
        {
            var url = XtreamUrls.PlayerApi(config, action, paramName, paramValue);
            var json = await GetStringAsync(ListClient, url, ct).ConfigureAwait(false);
            var list = ParseList<T>(json, out var skipped);
            if (skipped > 0)
                _logger.Warn("{0}: {1} malformed rows skipped", action, skipped);
            return list;
        }

        /// <summary>
        /// A list answer is normally a JSON array. Providers return an object or an empty string for
        /// "nothing", and a single bad row must not drop the other 60 000, so rows are retried one
        /// by one when the whole array fails to parse.
        /// </summary>
        internal static List<T> ParseList<T>(string json, out int skipped) where T : class
        {
            skipped = 0;
            var result = new List<T>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
                try
                {
                    return doc.RootElement.Deserialize<List<T>>(JsonOptions) ?? result;
                }
                catch (JsonException)
                {
                    // Fall through to the per-row parse.
                }
                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                    var item = SafeDeserialize<T>(row);
                    if (item == null) { skipped++; continue; }
                    result.Add(item);
                }
            }
            return result;
        }

        private static T SafeDeserialize<T>(JsonElement element) where T : class
        {
            try { return element.Deserialize<T>(JsonOptions); }
            catch (JsonException) { return null; }
        }

        private static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken ct)
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(string.Format(CultureInfo.InvariantCulture,
                        "Provider answered HTTP {0}", (int)response.StatusCode));
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private HttpClient ListClient
        {
            get
            {
                lock (_sync)
                {
                    if (_listClient == null) _listClient = _httpFactory(ListTimeoutSeconds);
                    return _listClient;
                }
            }
        }

        private HttpClient DetailClient
        {
            get
            {
                lock (_sync)
                {
                    if (_detailClient == null) _detailClient = _httpFactory(DetailTimeoutSeconds);
                    return _detailClient;
                }
            }
        }
    }
}
