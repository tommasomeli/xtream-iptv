using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamIptv.Catalog
{
    /// <summary>
    /// Startup hook of the catalogue: a configuration save that changes the stream base (server,
    /// user, password) starts a sync, which rewrites every movie path and marks every series stale.
    /// </summary>
    internal sealed class CatalogHost : IHostedService
    {
        private readonly CatalogSyncService _sync;
        private readonly ILogger<CatalogHost> _logger;
        private Plugin _plugin;

        public CatalogHost(CatalogSyncService sync, ILogger<CatalogHost> logger)
        {
            _sync = sync;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _plugin = Plugin.InstanceOrNull;
                if (_plugin == null)
                {
                    _logger.LogWarning("Catalog: plugin instance not available at startup, connection changes will not start a sync");
                    return Task.CompletedTask;
                }
                _plugin.ConnectionChanged += OnConnectionChanged;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Catalog: startup hook failed: {Message}", ex.Message);
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            var plugin = _plugin;
            _plugin = null;
            if (plugin != null) plugin.ConnectionChanged -= OnConnectionChanged;
            _sync.Cancel();
            return Task.CompletedTask;
        }

        private void OnConnectionChanged()
        {
            try
            {
                if (_sync.TryStartBackground())
                    _logger.LogInformation("Catalog sync: connection changed, rewriting every stream URL");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Catalog sync after a connection change could not start: {Message}", ex.Message);
            }
        }
    }
}
