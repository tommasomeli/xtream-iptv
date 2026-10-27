using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.XtreamIptv.Catalog
{
    /// <summary>
    /// Global MVC action filter: the item routes are handled by Jellyfin's own controllers, and this
    /// runs after authorization in front of every action. For a detail page, a season grid, an
    /// episode list or a folder listing of a catalogue item it loads the movie's detail or the series'
    /// episodes before the controller answers, within a time cap.
    /// </summary>
    public sealed class DetailOnOpenFilter : IAsyncActionFilter
    {
        internal const int DefaultWaitSeconds = 8;
        internal const int MaxWaitSeconds = 30;

        private readonly ILibraryManager _libraryManager;
        private readonly DetailOnOpenService _detail;
        private readonly LateDetailNotifier _notifier;
        private readonly ILogger<DetailOnOpenFilter> _logger;

        public DetailOnOpenFilter(ILibraryManager libraryManager, DetailOnOpenService detail, LateDetailNotifier notifier, ILogger<DetailOnOpenFilter> logger)
        {
            _libraryManager = libraryManager;
            _detail = detail;
            _notifier = notifier;
            _logger = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            try
            {
                var id = TryGetItemId(context);
                if (id.HasValue)
                {
                    var config = Plugin.InstanceOrNull?.Configuration;
                    if (config != null && config.DetailOnOpen)
                    {
                        var item = _libraryManager.GetItemById(id.Value);
                        if (item != null) await EnsureWithinCap(item, config.DetailWaitSeconds).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Detail on open: skipped: {Message}", ex.Message);
            }

            await next().ConfigureAwait(false);
        }

        private async Task EnsureWithinCap(BaseItem item, int configuredSeconds)
        {
            var task = _detail.EnsureAsync(item, CancellationToken.None);
            if (task.IsCompleted) return;
            var wait = WaitCap(configuredSeconds);
            if (wait <= TimeSpan.Zero)
            {
                _notifier.WhenDone(task, item);
                return;
            }
            var finished = await Task.WhenAny(task, Task.Delay(wait)).ConfigureAwait(false);
            if (ReferenceEquals(finished, task)) return;

            // The page goes out without the detail; the open clients are told when it lands.
            _logger.LogDebug("Detail on open: still running after {Seconds} s for '{Name}', the response goes out without it", wait.TotalSeconds, item.Name);
            _notifier.WhenDone(task, item);
        }

        /// <summary>The configured wait, clamped to 0..30 s; zero means answer at once and rely on the refresh.</summary>
        internal static TimeSpan WaitCap(int configuredSeconds)
            => TimeSpan.FromSeconds(Math.Max(0, Math.Min(MaxWaitSeconds, configuredSeconds)));

        /// <summary>The item a GET request is about: route value itemId (detail page), seriesId (season grid, episode list) or query parentId (folder listing).</summary>
        private static Guid? TryGetItemId(ActionExecutingContext context)
        {
            var request = context.HttpContext?.Request;
            if (request == null || !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)) return null;
            object value;
            if (context.RouteData.Values.TryGetValue("itemId", out value)) return ParseId(value);
            if (context.RouteData.Values.TryGetValue("seriesId", out value)) return ParseId(value);
            StringValues parent;
            if (request.Query.TryGetValue("parentId", out parent) || request.Query.TryGetValue("ParentId", out parent))
                return ParseId(parent.ToString());
            return null;
        }

        /// <summary>Jellyfin ids are Guids, with or without dashes; anything else is not an item.</summary>
        internal static Guid? ParseId(object value)
        {
            var text = value as string ?? value?.ToString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            Guid guid;
            if (value is Guid g) guid = g;
            else if (!Guid.TryParse(text.Trim(), out guid)) return null;
            return guid.Equals(Guid.Empty) ? (Guid?)null : guid;
        }
    }
}
