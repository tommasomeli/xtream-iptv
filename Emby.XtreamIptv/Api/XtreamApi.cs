using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using Emby.XtreamIptv.Service;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using XtreamIptv.Service;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.XtreamIptv.Api
{
    // Every route is admin-only. Emby already requires a login on a route with no attribute,
    // but checks no role. AuthenticatedRoutesTests fails on a route without this attribute.
    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/Epg", "GET", Summary = "Gets XMLTV EPG data for Live TV channels")]
    public class GetEpgXml : IReturnVoid
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/LiveTv", "GET", Summary = "Gets M3U playlist for Live TV channels")]
    public class GetM3UPlaylist : IReturnVoid
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/Categories/Live", "GET", Summary = "Gets Live TV categories from Xtream API")]
    public class GetLiveCategories : IReturn<List<Category>>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/RefreshCache", "POST", Summary = "Invalidates M3U and EPG caches")]
    public class RefreshCache : IReturnVoid
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/TestConnection", "POST", Summary = "Tests connection to Xtream server")]
    public class TestXtreamConnection : IReturn<TestConnectionResult>
    {
        public string BaseUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string HttpUserAgent { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/TestDispatcharr", "POST", Summary = "Tests connection to Dispatcharr")]
    public class TestDispatcharrConnection : IReturn<TestConnectionResult>
    {
        public string DispatcharrUrl { get; set; }
        public string DispatcharrUser { get; set; }
        public string DispatcharrPass { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/DispatcharrProfiles", "GET", Summary = "Gets Channel Profiles from Dispatcharr")]
    public class GetDispatcharrProfiles : IReturn<List<DispatcharrProfile>>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/StreamStats", "GET", Summary = "Returns cached stream stats received from Dispatcharr for all known streams")]
    public class GetStreamStats : IReturn<object>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/SyncGuideMappings", "POST", Summary = "Detaches listing providers from the Xtream tuner so Gracenote EPG is fetched by the tuner directly")]
    public class SyncGuideMappings : IReturn<SyncGuideMappingsResult>
    {
    }

    public class SyncGuideMappingsResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int ProvidersUpdated { get; set; }
    }

    public class TestConnectionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Connection and Live TV routes. Catalogue routes live in CatalogApi.</summary>
    public class XtreamApi : BaseApiService
    {
        public async Task<object> Get(GetEpgXml request)
        {
            var liveTvService = Plugin.Instance.LiveTvService;
            var xml = await liveTvService.GetXmltvEpgAsync(CancellationToken.None).ConfigureAwait(false);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return ResultFactory.GetResult(Request, stream, "application/xml", new Dictionary<string, string>());
        }

        public async Task<object> Get(GetM3UPlaylist request)
        {
            var liveTvService = Plugin.Instance.LiveTvService;
            var m3u = await liveTvService.GetM3UPlaylistAsync(CancellationToken.None).ConfigureAwait(false);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(m3u));
            return ResultFactory.GetResult(Request, stream, "audio/x-mpegurl", new Dictionary<string, string>());
        }

        public async Task<object> Get(GetLiveCategories request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
            {
                return new List<Category>();
            }

            List<Category> categories;
            try
            {
                var liveTvService = Plugin.Instance.LiveTvService;
                categories = await liveTvService.GetLiveCategoriesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Reported as an error, not an empty list: the page shows "No categories found"
                // for an empty list, which sends people checking a provider that is fine.
                Logger.Warn("Failed to load live TV categories: {0}", ex.Message);
                throw;
            }

            SaveCategoryCache("live TV", () => config.CachedLiveCategories = SerializeCategoryCache(categories));
            return categories;
        }

        private static string SerializeCategoryCache(IEnumerable<Category> categories)
            => System.Text.Json.JsonSerializer.Serialize(
                categories.Select(c => new { c.CategoryId, c.CategoryName }).ToList());

        /// <summary>
        /// Stores a category list so the settings page can show it instantly next time. Only a
        /// convenience: a failed save is logged and must not fail a request whose list loaded.
        /// </summary>
        private void SaveCategoryCache(string kind, Action store)
        {
            try
            {
                store();
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not save the {0} category cache; the list is still returned: {1}", kind, ex.Message);
            }
        }

        public void Post(RefreshCache request)
        {
            Plugin.Instance.LiveTvService.InvalidateCache();
            XtreamTunerHost.Instance?.ClearCaches();
        }

        public async Task<object> Post(TestXtreamConnection request)
        {
            var config = Plugin.Instance.Configuration;

            // Prefer the values supplied in the request (the unsaved form values) so the
            // connection can be tested before saving; fall back to the persisted config.
            var baseUrl = !string.IsNullOrEmpty(request.BaseUrl) ? request.BaseUrl.TrimEnd('/') : config.BaseUrl;
            var username = !string.IsNullOrEmpty(request.Username) ? request.Username : config.Username;
            var password = !string.IsNullOrEmpty(request.Password) ? request.Password : config.Password;

            using (var httpClient = Plugin.CreateHttpClient())
            {
                if (!string.IsNullOrEmpty(request.HttpUserAgent))
                {
                    httpClient.DefaultRequestHeaders.Remove("User-Agent");
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", request.HttpUserAgent);
                }

                var (success, message) = await XtreamConnectionTester
                    .RunAsync(baseUrl, username, password, httpClient, CancellationToken.None)
                    .ConfigureAwait(false);

                return new TestConnectionResult { Success = success, Message = message };
            }
        }

        public async Task<object> Post(TestDispatcharrConnection request)
        {
            var result = new TestConnectionResult();
            var url = request.DispatcharrUrl;
            var user = request.DispatcharrUser ?? "";
            var pass = request.DispatcharrPass ?? "";

            if (string.IsNullOrEmpty(url))
            {
                result.Success = false;
                result.Message = "Please enter Dispatcharr URL.";
                return result;
            }

            try
            {
                var logManager = Plugin.Instance.ApplicationHost.Resolve<ILogManager>();
                var client = new DispatcharrClient(new EmbyLog(logManager.GetLogger("XtreamIptv.DispatcharrTest")));
                client.Configure(user, pass);

                var (success, message) = await client.TestConnectionDetailedAsync(
                    url, user, pass,
                    CancellationToken.None).ConfigureAwait(false);

                result.Success = success;
                result.Message = message;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Unexpected error: " + ex.Message;
            }

            return result;
        }

        public async Task<object> Get(GetDispatcharrProfiles request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableDispatcharr || string.IsNullOrEmpty(config.DispatcharrUrl))
                return new List<DispatcharrProfile>();

            try
            {
                var logManager = Plugin.Instance.ApplicationHost.Resolve<ILogManager>();
                var client = new DispatcharrClient(new EmbyLog(logManager.GetLogger("XtreamIptv.DispatcharrProfiles")));
                client.Configure(config.DispatcharrUser, config.DispatcharrPass);

                var profiles = await client.GetProfilesAsync(config.DispatcharrUrl, CancellationToken.None)
                    .ConfigureAwait(false);

                // Cache for instant UI loading on next page open
                config.CachedDispatcharrProfiles = System.Text.Json.JsonSerializer.Serialize(
                    profiles.Select(p => new { p.Id, p.Name }).ToList());
                Plugin.Instance.SaveConfiguration();

                return profiles;
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to fetch Dispatcharr profiles: {0}", ex.Message);
                return new List<DispatcharrProfile>();
            }
        }

        public object Get(GetStreamStats request)
        {
            var stats = XtreamTunerHost.Instance?.StreamStats;
            if (stats == null || stats.Count == 0)
                return new { count = 0, message = "No stream stats loaded. Stats are populated when Dispatcharr is enabled and channels have been refreshed.", streams = new object[0] };

            var entries = stats
                .OrderBy(kv => kv.Key)
                .Select(kv => new
                {
                    stream_id       = kv.Key,
                    video_codec     = kv.Value.VideoCodec,
                    resolution      = kv.Value.Resolution,
                    source_fps      = kv.Value.SourceFps,
                    bitrate         = kv.Value.Bitrate,
                    video_profile   = kv.Value.VideoProfile,
                    video_level     = kv.Value.VideoLevel,
                    video_bit_depth = kv.Value.VideoBitDepth,
                    video_ref_frames = kv.Value.VideoRefFrames,
                    audio_codec     = kv.Value.AudioCodec,
                    audio_channels  = kv.Value.AudioChannels,
                    audio_bitrate   = kv.Value.AudioBitrate,
                    sample_rate     = kv.Value.SampleRate,
                    audio_language  = kv.Value.AudioLanguage,
                })
                .ToList();

            return new { count = entries.Count, streams = entries };
        }

        public object Post(SyncGuideMappings request)
        {
            var result = new SyncGuideMappingsResult();

            var tunerHost = XtreamTunerHost.Instance;
            if (tunerHost == null)
            {
                result.Message = "Xtream tuner host is not initialized.";
                return result;
            }

            try
            {
                var updated = tunerHost.DetachListingProviders();
                result.Success = true;
                result.ProvidersUpdated = updated;
                result.Message = updated > 0
                    ? string.Format("Detached Xtream tuner from {0} listing provider(s). Gracenote EPG is now fetched by the tuner directly for channels with station IDs; all other channels use Xtream EPG.", updated)
                    : "Xtream tuner is already detached from all listing providers. Gracenote EPG will be fetched by the tuner for channels with station IDs.";
            }
            catch (Exception ex)
            {
                result.Message = "Failed: " + ex.Message;
            }

            return result;
        }
    }
}
