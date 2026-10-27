using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using XtreamIptv;
using XtreamIptv.Catalog;

namespace Jellyfin.Plugin.XtreamIptv
{
    /// <summary>
    /// Xtream IPTV for Jellyfin: the same plugin as the Emby one (shared Xtream code), with the host
    /// layer written against Jellyfin 12.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private static volatile Plugin _instance;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            XtreamHost.Configuration = () => _instance?.Configuration;
        }

        public override string Name => "Xtream IPTV";

        public override string Description =>
            "Live TV with guide plus a movie and series catalogue from an Xtream account, with metadata from the Xtream lists.";

        public override Guid Id => Guid.Parse("c02587d5-61e0-415a-a2d5-3ccd3107d112");

        public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin not initialized");

        /// <summary>The current instance, or null before Jellyfin has created the plugin (unit tests).</summary>
        internal static Plugin InstanceOrNull => _instance;

        /// <summary>Raised after a configuration save whose stream base (server, user, password) differs from the previous one.</summary>
        public event Action ConnectionChanged;

        /// <summary>
        /// A new server, user or password means every stored stream URL is stale: ConnectionChanged
        /// lets the catalogue start a sync that rewrites every item (signatures carry the stream base).
        /// </summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var before = Configuration == null ? null : CatalogState.StreamBase(Configuration);
            base.UpdateConfiguration(configuration);
            var after = Configuration;
            if (before == null || after == null || !(after.SyncMovies || after.SyncSeries)) return;
            if (string.Equals(before, CatalogState.StreamBase(after), StringComparison.Ordinal)) return;
            ConnectionChanged?.Invoke();
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "xtreamiptv",
                    DisplayName = "Xtream IPTV",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    EnableInMainMenu = true,
                    MenuIcon = "live_tv",
                },
            };
        }
    }
}
