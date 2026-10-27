using System;
using MediaBrowser.Model.Plugins;

namespace XtreamIptv
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Xtream connection
        public string BaseUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string HttpUserAgent { get; set; } = string.Empty;

        // Live TV
        public bool EnableLiveTv { get; set; } = true;
        public string LiveTvOutputFormat { get; set; } = "ts";
        // Cap (in Mbps) applied to the MediaSource bitrate hint when no Streamflow
        // stats are available. 0 = no hint (Emby Web defaults to ~200 Mbps for "Auto"
        // bandwidth). Set to a value below your hardware encoder's cap (e.g. 8 for
        // most consumer GPUs) if transcoding falls back to software with logs like
        // "Bitrate (X Mbit/s) exceeds maximum supported rate".
        public int FallbackTranscodeBitrateMbps { get; set; }

        // EPG / Guide Data
        public EpgSourceMode EpgSource { get; set; } = EpgSourceMode.XtreamServer;
        public string CustomEpgUrl { get; set; } = string.Empty;
        public bool DeferEpgToGuideData { get; set; } = true;

        public int EpgCacheMinutes { get; set; } = 30;
        public int EpgDaysToFetch { get; set; } = 2;
        public int M3UCacheMinutes { get; set; } = 15;

        // Category filtering
        public int[] SelectedLiveCategoryIds { get; set; } = new int[0];
        public bool IncludeAdultChannels { get; set; }

        // Channel name cleaning
        public string ChannelRemoveTerms { get; set; } = string.Empty;
        public bool EnableChannelNameCleaning { get; set; } = true;

        // Dispatcharr
        public bool EnableDispatcharr { get; set; }
        public string DispatcharrUrl { get; set; } = string.Empty;
        public string DispatcharrUser { get; set; } = string.Empty;
        public string DispatcharrPass { get; set; } = string.Empty;
        public bool DispatcharrFallbackToXtream { get; set; } = true;
        public bool ForceAudioTranscode { get; set; }
        public bool DeclareDvbSubtitles { get; set; }

        /// <summary>
        /// Where the video codec declared to Emby for Dispatcharr channels comes from.
        /// <para>
        /// <c>auto</c> (default) reads the channel's Dispatcharr stream profile and declares
        /// what that profile emits, falling back to <c>stream_stats.video_codec</c> when the
        /// profile passes video through or cannot be read. This is the fix for issue #66:
        /// <c>stream_stats.video_codec</c> is the codec Dispatcharr <em>ingested</em>, so on a
        /// transcoding profile (HEVC source, H.264 output) declaring it makes Emby force the
        /// wrong decoder and the channel fails to play.
        /// </para>
        /// <para>
        /// <c>stats</c> always declares the reported codec, the behaviour before #66, and is
        /// the escape hatch if profile detection ever reads a profile wrongly. <c>h264</c> and
        /// <c>hevc</c> declare that codec on every Dispatcharr channel, for installs where the
        /// profile endpoint is unreachable but the output is known.
        /// </para>
        /// <para>
        /// Exposed as <c>Video codec for Dispatcharr channels</c> in the Dispatcharr section of
        /// the plugin config page. An empty value (config XML written before this setting
        /// existed) is read as <c>auto</c>.
        /// </para>
        /// </summary>
        public string DispatcharrVideoCodecSource { get; set; } = "auto";
        /// <summary>
        /// Which Dispatcharr URL a channel plays from. <c>proxy</c> (default) uses
        /// <c>/proxy/ts/stream/{uuid}</c>, which Dispatcharr's stats list as an anonymous client.
        /// <c>xtream</c> uses the Xtream URL of the connection above
        /// (<c>/live/{user}/{pass}/{id}.ts</c>), so Dispatcharr shows that account and applies its
        /// stream limits. Both run through the same proxy code inside Dispatcharr. The Xtream URL
        /// is only used when <see cref="BaseUrl"/> is the Dispatcharr server; otherwise it would
        /// skip Dispatcharr. See ADR-022.
        /// </summary>
        public string DispatcharrStreamEndpoint { get; set; } = "proxy";
        public int[] SelectedDispatcharrProfileIds { get; set; } = new int[0];
        public string CachedDispatcharrProfiles { get; set; } = string.Empty;

        // Catalog (movies and series as .strm libraries, metadata from the Xtream lists)
        public bool SyncMovies { get; set; } = true;
        public bool SyncSeries { get; set; } = true;
        /// <summary>Root folder of the generated libraries: Movies/ and Shows/ are created inside it.</summary>
        public string LibraryPath { get; set; } = "/config/xtream";
        public int[] SelectedVodCategoryIds { get; set; } = new int[0];
        public int[] SelectedSeriesCategoryIds { get; set; } = new int[0];
        public bool IncludeAdultContent { get; set; }
        /// <summary>Comma-separated terms: movie and series categories whose name contains one are left out of the catalogue.</summary>
        public string ExcludedCategoryTerms { get; set; } = string.Empty;
        /// <summary>A category the provider adds after the selection was made joins the selection by itself (movies and series alike).</summary>
        public bool AutoIncludeNewCategories { get; set; } = true;
        /// <summary>Comma-separated terms removed from titles, in addition to the built-in quality tags.</summary>
        public string ContentRemoveTerms { get; set; } = string.Empty;
        /// <summary>Releases of the same title that differ only by quality tag become one movie with several versions.</summary>
        public bool MergeMovieVersions { get; set; } = true;
        /// <summary>Ask the provider for plot, cast and backdrop the first time an item is opened in a client.</summary>
        public bool DetailOnOpen { get; set; } = true;
        /// <summary>How long a detail page request waits for the provider before it is answered without the detail (the client is then refreshed when it arrives).</summary>
        public int DetailWaitSeconds { get; set; } = 8;
        /// <summary>When a title is opened, the actors of its cast that have no photo yet are refreshed by Emby's own person providers in the background.</summary>
        public bool ActorPhotosOnOpen { get; set; } = true;
        /// <summary>Concurrent series detail requests during a sync.</summary>
        public int SyncParallelism { get; set; } = 4;
        /// <summary>Maximum Xtream requests per second, 0 = unlimited.</summary>
        public int XtreamRequestsPerSecond { get; set; }
        /// <summary>A sync never removes more than this share of the existing titles at once (provider glitches).</summary>
        public double RemovalSafetyThreshold { get; set; } = 0.20;

        // UI caches (category lists shown before the provider is queried again)
        public string CachedLiveCategories { get; set; } = string.Empty;
        public string CachedVodCategories { get; set; } = string.Empty;
        public string CachedSeriesCategories { get; set; } = string.Empty;

        // Last sync (read-only in the UI)
        public long LastCatalogSyncTimestamp { get; set; }
        public string LastCatalogSyncSummary { get; set; } = string.Empty;
    }

    public enum EpgSourceMode
    {
        XtreamServer = 0,
        CustomUrl    = 1,
        Disabled     = 2,
    }
}
