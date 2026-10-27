using System;
using System.Net.Http;
using XtreamIptv.Service;

namespace XtreamIptv
{
    /// <summary>
    /// The two things the shared code needs from the host plugin: the current configuration and an
    /// HttpClient factory. Each host's Plugin sets them at startup; the defaults keep unit tests working.
    /// </summary>
    public static class XtreamHost
    {
        private static Func<PluginConfiguration> _configuration = () => null;
        private static Func<int, HttpClient> _createHttpClient = DefaultHttpClient;

        public static Func<PluginConfiguration> Configuration
        {
            get { return _configuration; }
            set { _configuration = value ?? (() => null); }
        }

        public static Func<int, HttpClient> CreateHttpClient
        {
            get { return _createHttpClient; }
            set { _createHttpClient = value ?? DefaultHttpClient; }
        }

        /// <summary>An HttpClient with the rate limit handler and the configured User-Agent.</summary>
        public static HttpClient DefaultHttpClient(int timeoutSeconds)
        {
            var handler = XtreamRateLimitHandler.CreateDefault();
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var ua = Configuration()?.HttpUserAgent;
            if (!string.IsNullOrEmpty(ua))
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
            return client;
        }
    }
}
