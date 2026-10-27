using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.IO;
using XtreamIptv.Client;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Logging;

namespace Emby.XtreamIptv.Catalog
{
    /// <summary>
    /// The single instances the scheduled task, the API and the auth decorator share. Built lazily
    /// from the plugin's application host, so nothing here runs before Emby has started the plugin.
    /// </summary>
    internal static class CatalogServices
    {
        public const string StateFileName = "Emby.XtreamIptv.catalog.json";

        private static readonly Lazy<ILogger> LazyLogger = new Lazy<ILogger>(
            () => Resolve<ILogManager>().GetLogger("XtreamIptv.Catalog"));

        private static readonly Lazy<XtreamClient> LazyClient = new Lazy<XtreamClient>(
            () => new XtreamClient(new EmbyLog(Logger)));

        private static readonly Lazy<EmbyCatalog> LazyLibrary = new Lazy<EmbyCatalog>(
            () => new EmbyCatalog(Resolve<ILibraryManager>(), Logger, TryResolve<ILocalizationManager>()));

        private static readonly Lazy<CatalogState> LazyState = new Lazy<CatalogState>(
            () => CatalogState.Load(StatePath()));

        private static readonly Lazy<CatalogSyncService> LazySync = new Lazy<CatalogSyncService>(
            () => new CatalogSyncService(
                Logger,
                Client,
                () => Plugin.Instance.Configuration,
                () => Plugin.Instance.SaveConfiguration(),
                Library,
                State));

        private static readonly Lazy<DetailOnOpenService> LazyDetail = new Lazy<DetailOnOpenService>(
            () => new DetailOnOpenService(
                Logger,
                Client,
                () => Plugin.InstanceOrNull?.Configuration,
                Library,
                State));

        private static readonly Lazy<LateDetailNotifier> LazyNotifier = new Lazy<LateDetailNotifier>(
            () => new LateDetailNotifier(TryResolve<ISessionManager>(), Logger));

        public static ILogger Logger => LazyLogger.Value;

        public static LateDetailNotifier Notifier => LazyNotifier.Value;

        private static IFileSystem _fileSystem;

        /// <summary>Handed in by the entry point (constructor injection is the reliable way to get it); resolved on demand otherwise.</summary>
        public static IFileSystem FileSystem
        {
            get { return _fileSystem ?? (_fileSystem = TryResolve<IFileSystem>()); }
            set { _fileSystem = value; }
        }

        public static XtreamClient Client => LazyClient.Value;

        public static EmbyCatalog Library => LazyLibrary.Value;

        public static CatalogState State => LazyState.Value;

        public static CatalogSyncService Sync => LazySync.Value;

        public static DetailOnOpenService Detail => LazyDetail.Value;

        private static string StatePath()
        {
            var paths = TryResolve<IApplicationPaths>();
            var dir = paths != null ? paths.PluginConfigurationsPath : Path.GetTempPath();
            return Path.Combine(dir, StateFileName);
        }

        private static T Resolve<T>() => Plugin.Instance.ApplicationHost.Resolve<T>();

        private static T TryResolve<T>() where T : class
        {
            try { return Plugin.InstanceOrNull?.ApplicationHost?.Resolve<T>(); }
            catch (Exception) { return null; }
        }
    }
}
