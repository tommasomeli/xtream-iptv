using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using XtreamIptv.Catalog;
using XtreamIptv.Client;
using XtreamIptv.Logging;

namespace Jellyfin.Plugin.XtreamIptv.Catalog
{
    /// <summary>DI registrations of the catalogue, called from PluginServiceRegistrator.</summary>
    internal static class CatalogRegistration
    {
        public const string StateFileName = "Jellyfin.Plugin.XtreamIptv.catalog.json";

        public static void Register(IServiceCollection services)
        {
            services.AddSingleton<XtreamClient>(sp => new XtreamClient(sp.GetRequiredService<ILog>()));
            services.AddSingleton<CatalogState>(sp =>
            {
                var paths = sp.GetRequiredService<IApplicationPaths>();
                return CatalogState.Load(Path.Combine(paths.PluginConfigurationsPath, StateFileName));
            });
            services.AddSingleton<JellyfinCatalog>();
            services.AddSingleton<CatalogSyncService>(sp => new CatalogSyncService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CatalogSyncService>>(),
                sp.GetRequiredService<XtreamClient>(),
                () => Plugin.InstanceOrNull?.Configuration,
                () => Plugin.InstanceOrNull?.SaveConfiguration(),
                sp.GetRequiredService<JellyfinCatalog>(),
                sp.GetRequiredService<CatalogState>()));
            services.AddSingleton<LateDetailNotifier>();
            services.AddSingleton<DetailOnOpenService>(sp => new DetailOnOpenService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DetailOnOpenService>>(),
                sp.GetRequiredService<XtreamClient>(),
                sp.GetRequiredService<JellyfinCatalog>(),
                sp.GetRequiredService<CatalogState>(),
                sp.GetRequiredService<MediaBrowser.Model.IO.IFileSystem>(),
                sp.GetRequiredService<LateDetailNotifier>()));
            services.AddSingleton<DetailOnOpenFilter>();
            services.Configure<MvcOptions>(o => o.Filters.AddService<DetailOnOpenFilter>());
            services.AddHostedService<CatalogHost>();
        }
    }
}
