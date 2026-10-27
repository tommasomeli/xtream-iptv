using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamIptv.Catalog
{
    /// <summary>Scheduled task "Sync Xtream catalogue", daily at 04:30 by default. Shares the service with the API.</summary>
    public class CatalogSyncTask : IScheduledTask
    {
        private readonly CatalogSyncService _sync;
        private readonly ILogger<CatalogSyncTask> _logger;

        public CatalogSyncTask(CatalogSyncService sync, ILogger<CatalogSyncTask> logger)
        {
            _sync = sync;
            _logger = logger;
        }

        public string Name => "Sync Xtream catalogue";

        public string Key => "XtreamIptvCatalogSync";

        public string Description => "Writes the Xtream movie and series lists into the catalogue libraries, metadata included";

        public string Category => "Library";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var started = await _sync.RunAsync(cancellationToken, progress).ConfigureAwait(false);
            if (!started)
                _logger.LogInformation("Catalog sync: a sync is already running, the scheduled run was skipped");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks },
            };
        }
    }
}
