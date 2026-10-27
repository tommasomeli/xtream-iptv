using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using XtreamIptv;
using XtreamIptv.Client.Models;

namespace Jellyfin.Plugin.XtreamIptv.LiveTv
{
    /// <summary>
    /// The guide of the Xtream tuner as a Jellyfin listings provider. Its channels are the tuner's
    /// own (same ids), so Jellyfin pairs them without mappings; programs come from the plugin's
    /// guide cache (XMLTV from the provider or the custom URL, per-channel JSON as the fallback).
    /// </summary>
    public class XtreamListingsProvider : IListingsProvider
    {
        public const string ProviderType = "xtream-epg";

        private const long MinTimestamp = 946684800L;   // 2000-01-01
        private const long MaxTimestamp = 4102444800L;  // 2100-01-01

        private readonly XtreamTunerHost _tuner;
        private readonly LiveTvService _liveTv;
        private readonly ILogger<XtreamListingsProvider> _logger;

        public XtreamListingsProvider(XtreamTunerHost tuner, LiveTvService liveTv, ILogger<XtreamListingsProvider> logger)
        {
            _tuner = tuner;
            _liveTv = liveTv;
            _logger = logger;
        }

        public string Name => "Xtream IPTV guide";

        public string Type => ProviderType;

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            var config = Plugin.InstanceOrNull?.Configuration;
            if (config == null || config.EpgSource == EpgSourceMode.Disabled) return Array.Empty<ProgramInfo>();
            if (!XtreamTunerHost.TryParseStreamId(channelId, out var streamId)) return Array.Empty<ProgramInfo>();

            List<EpgProgram> programs;
            try
            {
                programs = await _liveTv.FetchEpgForChannelCachedAsync(streamId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogWarning("Failed to fetch EPG for stream {StreamId}: {Message}", streamId, ex.Message);
                return Array.Empty<ProgramInfo>();
            }

            var result = ConvertPrograms(programs, streamId, channelId, new DateTimeOffset(startDateUtc, TimeSpan.Zero).ToUnixTimeSeconds(), new DateTimeOffset(endDateUtc, TimeSpan.Zero).ToUnixTimeSeconds());
            _logger.LogDebug("Returning {Count} programs for stream {StreamId}", result.Count, streamId);
            return result;
        }

        /// <summary>The programs inside the window, with the entries Jellyfin would reject left out.</summary>
        internal static List<ProgramInfo> ConvertPrograms(IEnumerable<EpgProgram> programs, int streamId, string channelId, long startUnix, long endUnix)
        {
            var result = new List<ProgramInfo>();
            if (programs == null) return result;
            foreach (var p in programs)
            {
                if (p.StopTimestamp <= startUnix || p.StartTimestamp >= endUnix) continue;
                if (p.StartTimestamp < MinTimestamp || p.StartTimestamp > MaxTimestamp || p.StopTimestamp < MinTimestamp || p.StopTimestamp > MaxTimestamp) continue;
                // Zero-length or reversed programs make Jellyfin's guide reject the channel.
                if (p.StopTimestamp <= p.StartTimestamp) continue;
                var title = p.IsPlainText ? p.Title : LiveTvService.DecodeBase64(p.Title);
                var description = p.IsPlainText ? p.Description : LiveTvService.DecodeBase64(p.Description);
                result.Add(BuildProgramInfo(p, streamId, channelId, title, description));
            }
            return result;
        }

        internal static ProgramInfo BuildProgramInfo(EpgProgram p, int streamId, string channelId, string title, string description)
        {
            var cats = p.Categories;
            bool Has(string word) => cats != null && cats.Exists(c => c.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
            var isMovie = Has("movie") || Has("film");
            var isSports = Has("sport");
            var isSeries = !isMovie && !isSports;

            return new ProgramInfo
            {
                Id = string.Format(CultureInfo.InvariantCulture, "xtream_epg_{0}_{1}", streamId, p.StartTimestamp),
                ChannelId = channelId,
                StartDate = DateTimeOffset.FromUnixTimeSeconds(p.StartTimestamp).UtcDateTime,
                EndDate = DateTimeOffset.FromUnixTimeSeconds(p.StopTimestamp).UtcDateTime,
                Name = string.IsNullOrEmpty(title) ? "Unknown" : title,
                Overview = string.IsNullOrEmpty(description) ? null : description,
                EpisodeTitle = string.IsNullOrEmpty(p.SubTitle) ? null : p.SubTitle,
                IsLive = p.IsLive,
                IsRepeat = p.IsPreviouslyShown,
                IsPremiere = p.IsPremiere,
                ImageUrl = IsValidHttpUrl(p.ImageUrl) ? p.ImageUrl : null,
                Genres = cats ?? new List<string>(),
                IsSports = isSports,
                IsNews = Has("news"),
                IsMovie = isMovie,
                IsKids = Has("children") || Has("kids"),
                IsSeries = isSeries,
                SeriesId = isSeries && !string.IsNullOrEmpty(title) ? title.ToLowerInvariant() : null,
            };
        }

        public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings) => Task.CompletedTask;

        public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
            => Task.FromResult(new List<NameIdPair>());

        /// <summary>The tuner's channels under the same ids, so the pairing is exact and logos carry over.</summary>
        public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            var channels = await _tuner.GetChannels(true, cancellationToken).ConfigureAwait(false);
            return channels.Select(c => new ChannelInfo
            {
                Id = c.Id,
                TunerChannelId = c.TunerChannelId,
                Name = c.Name,
                Number = c.Number,
                ImageUrl = c.ImageUrl,
                ChannelType = c.ChannelType,
                TunerHostId = c.TunerHostId,
            }).ToList();
        }

        private static bool IsValidHttpUrl(string url)
            => !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
