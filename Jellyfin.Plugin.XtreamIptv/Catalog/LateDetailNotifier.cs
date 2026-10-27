using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamIptv.Catalog
{
    /// <summary>
    /// Tells the open clients that a title changed once a detail fetch outlived the request that
    /// started it, or once its actors got their photos: Jellyfin's own LibraryChanged message, for
    /// this item only, the moment the detail is written. The web item page does not reload on it
    /// (its lists do), so the synchronous wait cap is what carries the open; other clients may react.
    /// </summary>
    public sealed class LateDetailNotifier
    {
        private readonly ISessionManager _sessionManager;
        private readonly IUserManager _userManager;
        private readonly JellyfinCatalog _library;
        private readonly ILogger<LateDetailNotifier> _logger;
        private readonly ConcurrentDictionary<Guid, byte> _pending = new ConcurrentDictionary<Guid, byte>();

        public LateDetailNotifier(ISessionManager sessionManager, IUserManager userManager, JellyfinCatalog library, ILogger<LateDetailNotifier> logger)
        {
            _sessionManager = sessionManager;
            _userManager = userManager;
            _library = library;
            _logger = logger;
        }

        /// <summary>One notification per item however many requests waited on the same fetch.</summary>
        public void WhenDone(Task fetch, BaseItem item)
        {
            if (fetch == null || item == null || _sessionManager == null) return;
            if (!_pending.TryAdd(item.Id, 0)) return;
            fetch.ContinueWith(_ => Notify(item), TaskScheduler.Default);
        }

        /// <summary>Sends the message right away (actor photos landed after the page was answered).</summary>
        public Task NotifyNow(BaseItem item)
        {
            if (item == null || _sessionManager == null) return Task.CompletedTask;
            return Notify(item);
        }

        private async Task Notify(BaseItem item)
        {
            try
            {
                var userIds = _userManager.GetUsersIds().Distinct().ToList();
                if (userIds.Count == 0) return;
                var info = new LibraryUpdateInfo { ItemsUpdated = UpdatedIds(item, item is Series ? SeasonsOf((Series)item) : Array.Empty<BaseItem>()) };
                await _sessionManager.SendMessageToUserSessions(userIds, SessionMessageType.LibraryChanged, info, CancellationToken.None).ConfigureAwait(false);
                _logger.LogDebug("Detail on open: late detail of '{Name}' sent to {Count} user(s)", item.Name, userIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Detail on open: late notification for '{Name}' failed: {Message}", item.Name, ex.Message);
            }
            finally
            {
                byte removed;
                _pending.TryRemove(item.Id, out removed);
            }
        }

        private BaseItem[] SeasonsOf(Series series)
        {
            try { return _library.Children(series, BaseItemKind.Season, false).ToArray(); }
            catch (Exception) { return Array.Empty<BaseItem>(); }
        }

        /// <summary>The item and, for a series, its seasons: a season page is open on the season, not on the series.</summary>
        internal static string[] UpdatedIds(BaseItem item, IEnumerable<BaseItem> related)
            => new[] { item }.Concat(related ?? Array.Empty<BaseItem>()).Select(i => i.Id.ToString("N")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
