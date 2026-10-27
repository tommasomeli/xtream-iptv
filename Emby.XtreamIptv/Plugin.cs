using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using Emby.XtreamIptv.Service;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using XtreamIptv.Service;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.XtreamIptv
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        private static volatile Plugin _instance;
        private readonly IApplicationHost _applicationHost;
        private readonly IApplicationPaths _applicationPaths;
        private LiveTvService _liveTvService;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager, IApplicationHost applicationHost)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            XtreamHost.Configuration = () => _instance?.Configuration;
            _applicationHost = applicationHost;
            _applicationPaths = applicationPaths;
            _liveTvService = new LiveTvService(logManager.GetLogger("XtreamIptv.LiveTv"));
        }

        public override string Name => "Xtream IPTV";

        public override string Description =>
            "Live TV with guide plus a movie and series catalogue from an Xtream account, with metadata from the Xtream lists.";

        public override Guid Id => Guid.Parse("c02587d5-61e0-415a-a2d5-3ccd3107d112");

        public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin not initialized");

        /// <summary>Returns the current instance, or null if the plugin has not been initialised (e.g. during unit tests).</summary>
        internal static Plugin InstanceOrNull => _instance;

        public IApplicationHost ApplicationHost => _applicationHost;

        public new IApplicationPaths ApplicationPaths => _applicationPaths;

        /// <summary>
        /// Creates an HttpClient configured with the plugin's User-Agent setting.
        /// </summary>
        public static HttpClient CreateHttpClient(int timeoutSeconds = 10) => XtreamHost.CreateHttpClient(timeoutSeconds);

        public LiveTvService LiveTvService => _liveTvService;

        /// <summary>
        /// A new server, user or password means every stored stream URL is stale: the catalogue sync
        /// starts right away (its signatures carry the stream base, so every item is rewritten).
        /// </summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var before = Configuration == null ? null : CatalogState.StreamBase(Configuration);
            base.UpdateConfiguration(configuration);
            try
            {
                var after = Configuration;
                if (before == null || after == null || !(after.SyncMovies || after.SyncSeries)) return;
                if (string.Equals(before, CatalogState.StreamBase(after), StringComparison.Ordinal)) return;
                if (Catalog.CatalogServices.Sync.TryStartBackground())
                    Catalog.CatalogServices.Logger.Info("Catalog sync: connection changed, rewriting every stream URL");
            }
            catch (Exception ex)
            {
                Catalog.CatalogServices.Logger.Warn("Catalog sync after a connection change could not start: {0}", ex.Message);
            }
        }

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream("Emby.XtreamIptv.thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = GetHtmlPageName(),
                    EmbeddedResourcePath = "Emby.XtreamIptv.Configuration.Web.config.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    MenuIcon = "live_tv",
                },
                new PluginPageInfo
                {
                    // Alias: Emby's Admin Plugins page derives the settings URL from
                    // Plugin.Name with spaces stripped, so this name keeps that link working.
                    Name = "XtreamIPTV",
                    EmbeddedResourcePath = "Emby.XtreamIptv.Configuration.Web.config.html",
                },
                new PluginPageInfo
                {
                    Name = GetJsPageName(),
                    EmbeddedResourcePath = "Emby.XtreamIptv.Configuration.Web.config.js",
                },
            };
        }

        /// <summary>
        /// Stable page name for config.html. Must never change between versions: the Emby SPA
        /// would otherwise navigate to a stale URL after an update. Emby appends ?v=&lt;ServerVersion&gt;
        /// for cache-busting.
        /// </summary>
        private static string GetHtmlPageName()
        {
            return "xtreamiptv";
        }

        /// <summary>Stable JS page name. Emby appends ?v=&lt;ServerVersion&gt; automatically.</summary>
        private static string GetJsPageName()
        {
            return "xtreamiptvjs";
        }
    }
}
