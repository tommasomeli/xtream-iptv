using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Emby.XtreamIptv.Catalog
{
    /// <summary>Scheduled task "Sync Xtream catalogue", daily at 04:30 by default. Shares the service with the API.</summary>
    public class CatalogSyncTask : IScheduledTask
    {
        public string Name => "Sync Xtream catalogue";

        public string Key => "XtreamIptvCatalogSync";

        public string Description => "Writes the Xtream movie and series lists as .strm libraries, metadata included";

        public string Category => "Library";

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var started = await CatalogServices.Sync.RunAsync(cancellationToken, progress).ConfigureAwait(false);
            if (!started)
                CatalogServices.Logger.Info("Catalog sync: a sync is already running, the scheduled run was skipped");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks },
            };
        }
    }
}
