using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.XtreamIptv.LiveTv;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using XtreamIptv;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using XtreamIptv.Service;

namespace Jellyfin.Plugin.XtreamIptv.Controllers
{
    public class TestConnectionRequest
    {
        public string BaseUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string HttpUserAgent { get; set; }
    }

    public class TestDispatcharrRequest
    {
        public string DispatcharrUrl { get; set; }
        public string DispatcharrUser { get; set; }
        public string DispatcharrPass { get; set; }
    }

    public class TestConnectionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class SyncGuideMappingsResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int ProvidersUpdated { get; set; }
    }

    public class LiveTvRegisterResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string TunerId { get; set; }
        public string GuideId { get; set; }
        public bool TunerCreated { get; set; }
        public bool GuideCreated { get; set; }
    }

    public class LiveTvStatusResult
    {
        public bool TunerRegistered { get; set; }
        public bool GuideRegistered { get; set; }
        public string TunerId { get; set; }
        public string GuideId { get; set; }
        public bool LiveTvEnabled { get; set; }
        public int CachedChannels { get; set; }
    }

    /// <summary>Connection and Live TV routes (same contract as the Emby plugin). Catalogue routes live in CatalogController.</summary>
    [ApiController]
    [Route("XtreamIptv")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class LiveTvController : ControllerBase
    {
        private readonly LiveTvService _liveTv;
        private readonly XtreamTunerHost _tuner;
        private readonly ITunerHostManager _tunerHostManager;
        private readonly IListingsManager _listingsManager;
        private readonly IConfigurationManager _config;
        private readonly ILogger<LiveTvController> _logger;

        public LiveTvController(
            LiveTvService liveTv,
            XtreamTunerHost tuner,
            ITunerHostManager tunerHostManager,
            IListingsManager listingsManager,
            IConfigurationManager config,
            ILogger<LiveTvController> logger)
        {
            _liveTv = liveTv;
            _tuner = tuner;
            _tunerHostManager = tunerHostManager;
            _listingsManager = listingsManager;
            _config = config;
            _logger = logger;
        }

        [HttpGet("Epg")]
        [Produces("application/xml")]
        public async Task<ActionResult> GetEpgXml(CancellationToken cancellationToken)
        {
            var xml = await _liveTv.GetXmltvEpgAsync(cancellationToken).ConfigureAwait(false);
            return Content(xml, "application/xml");
        }

        [HttpGet("LiveTv")]
        public async Task<ActionResult> GetM3UPlaylist(CancellationToken cancellationToken)
        {
            var m3u = await _liveTv.GetM3UPlaylistAsync(_tuner.TvgIdMap, cancellationToken).ConfigureAwait(false);
            return Content(m3u, "audio/x-mpegurl");
        }

        [HttpGet("Categories/Live")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<List<Category>>> GetLiveCategories(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            if (string.IsNullOrEmpty(config.BaseUrl) || string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
                return new List<Category>();

            // A failure is reported as an error, not an empty list: the page shows "No categories
            // found" for an empty list, which sends people checking a provider that is fine.
            var categories = await _liveTv.GetLiveCategoriesAsync(cancellationToken).ConfigureAwait(false);
            SaveCategoryCache("live TV", () => config.CachedLiveCategories = SerializeCategoryCache(categories));
            return categories;
        }

        [HttpPost("RefreshCache")]
        public ActionResult RefreshCache()
        {
            _tuner.ClearCaches();
            return NoContent();
        }

        [HttpPost("TestConnection")]
        public async Task<ActionResult<TestConnectionResult>> TestConnection([FromBody] TestConnectionRequest request, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            request ??= new TestConnectionRequest();
            // The unsaved form values win, so the connection can be tested before saving.
            var baseUrl = !string.IsNullOrEmpty(request.BaseUrl) ? request.BaseUrl.TrimEnd('/') : config.BaseUrl;
            var username = !string.IsNullOrEmpty(request.Username) ? request.Username : config.Username;
            var password = !string.IsNullOrEmpty(request.Password) ? request.Password : config.Password;

            using (var httpClient = XtreamHost.CreateHttpClient(10))
            {
                if (!string.IsNullOrEmpty(request.HttpUserAgent))
                {
                    httpClient.DefaultRequestHeaders.Remove("User-Agent");
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", request.HttpUserAgent);
                }
                var (success, message) = await XtreamConnectionTester.RunAsync(baseUrl, username, password, httpClient, cancellationToken).ConfigureAwait(false);
                return new TestConnectionResult { Success = success, Message = message };
            }
        }

        [HttpPost("TestDispatcharr")]
        public async Task<ActionResult<TestConnectionResult>> TestDispatcharr([FromBody] TestDispatcharrRequest request, CancellationToken cancellationToken)
        {
            request ??= new TestDispatcharrRequest();
            if (string.IsNullOrEmpty(request.DispatcharrUrl))
                return new TestConnectionResult { Success = false, Message = "Please enter Dispatcharr URL." };
            try
            {
                var client = new DispatcharrClient(new JellyfinLog(_logger));
                client.Configure(request.DispatcharrUser ?? string.Empty, request.DispatcharrPass ?? string.Empty);
                var (success, message) = await client.TestConnectionDetailedAsync(request.DispatcharrUrl, request.DispatcharrUser ?? string.Empty, request.DispatcharrPass ?? string.Empty, cancellationToken).ConfigureAwait(false);
                return new TestConnectionResult { Success = success, Message = message };
            }
            catch (Exception ex)
            {
                return new TestConnectionResult { Success = false, Message = "Unexpected error: " + ex.Message };
            }
        }

        [HttpGet("DispatcharrProfiles")]
        public async Task<ActionResult<List<DispatcharrProfile>>> GetDispatcharrProfiles(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableDispatcharr || string.IsNullOrEmpty(config.DispatcharrUrl))
                return new List<DispatcharrProfile>();
            try
            {
                var client = new DispatcharrClient(new JellyfinLog(_logger));
                client.Configure(config.DispatcharrUser, config.DispatcharrPass);
                var profiles = await client.GetProfilesAsync(config.DispatcharrUrl, cancellationToken).ConfigureAwait(false);
                config.CachedDispatcharrProfiles = System.Text.Json.JsonSerializer.Serialize(profiles.Select(p => new { p.Id, p.Name }).ToList());
                Plugin.Instance.SaveConfiguration();
                return profiles;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to fetch Dispatcharr profiles: {Message}", ex.Message);
                return new List<DispatcharrProfile>();
            }
        }

        [HttpGet("StreamStats")]
        public ActionResult<object> GetStreamStats()
        {
            var stats = _tuner.StreamStats;
            if (stats == null || stats.Count == 0)
                return new { count = 0, message = "No stream stats loaded. Stats are populated when Dispatcharr is enabled and channels have been refreshed.", streams = Array.Empty<object>() };

            var entries = stats.OrderBy(kv => kv.Key).Select(kv => new
            {
                stream_id = kv.Key,
                video_codec = kv.Value.VideoCodec,
                resolution = kv.Value.Resolution,
                source_fps = kv.Value.SourceFps,
                bitrate = kv.Value.Bitrate,
                video_profile = kv.Value.VideoProfile,
                video_level = kv.Value.VideoLevel,
                video_bit_depth = kv.Value.VideoBitDepth,
                video_ref_frames = kv.Value.VideoRefFrames,
                audio_codec = kv.Value.AudioCodec,
                audio_channels = kv.Value.AudioChannels,
                audio_bitrate = kv.Value.AudioBitrate,
                sample_rate = kv.Value.SampleRate,
                audio_language = kv.Value.AudioLanguage,
            }).ToList();
            return new { count = entries.Count, streams = entries };
        }

        /// <summary>Emby-only operation (its Guide Data provider); kept so the page gets a clear answer.</summary>
        [HttpPost("SyncGuideMappings")]
        public ActionResult<SyncGuideMappingsResult> SyncGuideMappings()
            => new SyncGuideMappingsResult { Success = false, Message = "Not applicable on Jellyfin: the Xtream guide is attached through the registered listings provider." };

        /// <summary>Creates the tuner and the guide provider in Jellyfin's Live TV settings when missing. Idempotent.</summary>
        [HttpPost("LiveTv/Register")]
        public async Task<ActionResult<LiveTvRegisterResult>> Register()
        {
            var result = new LiveTvRegisterResult();
            try
            {
                var options = _config.GetConfiguration<LiveTvOptions>("livetv");
                var tuner = (options.TunerHosts ?? Array.Empty<TunerHostInfo>()).FirstOrDefault(t => string.Equals(t.Type, XtreamTunerHost.TunerType, StringComparison.OrdinalIgnoreCase));
                if (tuner == null)
                {
                    tuner = await _tunerHostManager.SaveTunerHost(new TunerHostInfo
                    {
                        Type = XtreamTunerHost.TunerType,
                        FriendlyName = "Xtream IPTV",
                        Url = Plugin.Instance.Configuration.BaseUrl,
                        AllowStreamSharing = true,
                        TunerCount = 0,
                    }, dataSourceChanged: false).ConfigureAwait(false);
                    result.TunerCreated = true;
                }

                options = _config.GetConfiguration<LiveTvOptions>("livetv");
                var guide = (options.ListingProviders ?? Array.Empty<ListingsProviderInfo>()).FirstOrDefault(p => string.Equals(p.Type, XtreamListingsProvider.ProviderType, StringComparison.OrdinalIgnoreCase));
                if (guide == null)
                {
                    // SaveListingProvider queues the guide refresh, which also loads the new tuner's channels.
                    guide = await _listingsManager.SaveListingProvider(new ListingsProviderInfo
                    {
                        Type = XtreamListingsProvider.ProviderType,
                        EnableAllTuners = true,
                        ListingsId = "xtream",
                    }, false, false).ConfigureAwait(false);
                    result.GuideCreated = true;
                }

                result.TunerId = tuner.Id;
                result.GuideId = guide.Id;
                result.Success = true;
                result.Message = result.TunerCreated || result.GuideCreated
                    ? "Tuner and guide registered; the guide refresh has been queued."
                    : "Tuner and guide were already registered.";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live TV registration failed");
                result.Success = false;
                result.Message = "Registration failed: " + ex.Message;
            }
            return result;
        }

        [HttpGet("LiveTv/Status")]
        public ActionResult<LiveTvStatusResult> Status()
        {
            var options = _config.GetConfiguration<LiveTvOptions>("livetv");
            var tuner = (options.TunerHosts ?? Array.Empty<TunerHostInfo>()).FirstOrDefault(t => string.Equals(t.Type, XtreamTunerHost.TunerType, StringComparison.OrdinalIgnoreCase));
            var guide = (options.ListingProviders ?? Array.Empty<ListingsProviderInfo>()).FirstOrDefault(p => string.Equals(p.Type, XtreamListingsProvider.ProviderType, StringComparison.OrdinalIgnoreCase));
            return new LiveTvStatusResult
            {
                TunerRegistered = tuner != null,
                GuideRegistered = guide != null,
                TunerId = tuner?.Id,
                GuideId = guide?.Id,
                LiveTvEnabled = Plugin.InstanceOrNull?.Configuration?.EnableLiveTv ?? false,
                CachedChannels = _tuner.CachedChannelCount,
            };
        }

        private static string SerializeCategoryCache(IEnumerable<Category> categories)
            => System.Text.Json.JsonSerializer.Serialize(categories.Select(c => new { c.CategoryId, c.CategoryName }).ToList());

        /// <summary>The category cache only makes the page open faster: a failed save never fails a request whose list loaded.</summary>
        private void SaveCategoryCache(string kind, Action store)
        {
            try
            {
                store();
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not save the {Kind} category cache; the list is still returned: {Message}", kind, ex.Message);
            }
        }
    }
}
