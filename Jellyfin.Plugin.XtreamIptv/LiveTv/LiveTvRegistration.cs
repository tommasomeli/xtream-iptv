using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using XtreamIptv.Logging;

namespace Jellyfin.Plugin.XtreamIptv.LiveTv
{
    /// <summary>Live TV services: the tuner host and the listings provider are injected as collections by Jellyfin, so they must be registered.</summary>
    internal static class LiveTvRegistration
    {
        public static void Register(IServiceCollection services)
        {
            services.AddSingleton<LiveTvService>(sp => new LiveTvService(sp.GetRequiredService<ILog>()));
            services.AddSingleton<XtreamTunerHost>();
            services.AddSingleton<ITunerHost>(sp => sp.GetRequiredService<XtreamTunerHost>());
            services.AddSingleton<IListingsProvider, XtreamListingsProvider>();
        }
    }
}
