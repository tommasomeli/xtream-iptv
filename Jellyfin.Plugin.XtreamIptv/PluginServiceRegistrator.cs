using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XtreamIptv.Logging;

namespace Jellyfin.Plugin.XtreamIptv
{
    /// <summary>Registers the plugin's services with Jellyfin's container (called once at startup, before the host is built).</summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<ILog>(sp => new JellyfinLog(sp.GetRequiredService<ILoggerFactory>().CreateLogger("XtreamIptv")));
            Catalog.CatalogRegistration.Register(serviceCollection);
            LiveTv.LiveTvRegistration.Register(serviceCollection);
        }
    }
}
