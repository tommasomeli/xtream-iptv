using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.XtreamIptv.Catalog
{
    /// <summary>
    /// Wraps Emby's authentication service at startup. The item routes are handled by the core, so
    /// the one place that runs before them is the authenticator every authenticated request passes
    /// through; that is where a movie's detail or a series' episodes are loaded before the response.
    /// </summary>
    public sealed class DetailOnOpenEntryPoint : IServerEntryPoint
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private DetailOnOpenAuthService _decorator;

        public DetailOnOpenEntryPoint(ILogManager logManager, ILibraryManager libraryManager, IFileSystem fileSystem)
        {
            _logger = logManager.GetLogger("XtreamIptv.DetailOnOpen");
            _libraryManager = libraryManager;
            CatalogServices.FileSystem = fileSystem;
        }

        public void Run()
        {
            try
            {
                var inner = AuthenticatedAttribute.AuthService;
                if (inner == null)
                {
                    _logger.Warn("Detail on open: Emby has not set its authentication service yet; detail on open is off until the next restart");
                    return;
                }
                if (inner is DetailOnOpenAuthService)
                {
                    _logger.Debug("Detail on open: already installed");
                    return;
                }
                _decorator = new DetailOnOpenAuthService(inner, _libraryManager, CatalogServices.Notifier, _logger);
                AuthenticatedAttribute.AuthService = _decorator;
                _logger.Info("Detail on open: installed in front of {0}", inner.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.Warn("Detail on open: could not install: {0}", ex.Message);
            }
        }

        public void Dispose()
        {
            var mine = _decorator;
            if (mine == null) return;
            _decorator = null;
            try
            {
                // Only unwrap when we are still the outermost; another decorator installed later keeps its chain.
                if (ReferenceEquals(AuthenticatedAttribute.AuthService, mine))
                    AuthenticatedAttribute.AuthService = mine.Inner;
            }
            catch (Exception ex)
            {
                _logger.Debug("Detail on open: restore failed: {0}", ex.Message);
            }
        }
    }

    /// <summary>The decorator: authenticate first, then the extra work, which can never fail the request.</summary>
    internal sealed class DetailOnOpenAuthService : IAuthService
    {
        /// <summary><c>/Users/{id}/Items/{itemId}</c>, with or without the emby or mediabrowser prefix.</summary>
        internal static readonly Regex ItemRouteRegex = new Regex(
            @"^/(emby/|mediabrowser/)?Users/[^/]+/Items/([^/?]+)/?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary><c>/Shows/{id}/Seasons</c> and <c>/Shows/{id}/Episodes</c>: the season grid and the episode list.</summary>
        internal static readonly Regex ShowsRouteRegex = new Regex(
            @"^/(emby/|mediabrowser/)?Shows/([^/?]+)/(Seasons|Episodes)/?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary><c>/Users/{id}/Items</c> and <c>/Items</c>: a folder listing, the item is the ParentId parameter.</summary>
        internal static readonly Regex ItemsListRouteRegex = new Regex(
            @"^/(emby/|mediabrowser/)?(Users/[^/]+/)?Items/?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal const int DefaultWaitSeconds = 8;
        internal const int MaxWaitSeconds = 30;

        private readonly ILibraryManager _libraryManager;
        private readonly LateDetailNotifier _notifier;
        private readonly ILogger _logger;

        public IAuthService Inner { get; }

        public DetailOnOpenAuthService(IAuthService inner, ILibraryManager libraryManager, LateDetailNotifier notifier, ILogger logger)
        {
            Inner = inner;
            _libraryManager = libraryManager;
            _notifier = notifier;
            _logger = logger;
        }

        public void Authenticate(IRequest request, IAuthenticationAttributes authAttribtues)
        {
            // Outside the try: the inner result or exception must reach the caller unchanged.
            Inner.Authenticate(request, authAttribtues);

            try
            {
                if (request == null) return;
                var itemId = TryGetItemId(request.Verb, request.PathInfo, name => request.QueryString == null ? null : request.QueryString.Get(name));
                if (itemId == null) return;
                var config = Plugin.InstanceOrNull?.Configuration;
                if (config == null || !config.DetailOnOpen) return;

                var item = ResolveItem(itemId);
                if (item == null) return;

                var task = CatalogServices.Detail.EnsureAsync(item, CancellationToken.None);
                if (task.IsCompleted) return;
                var wait = WaitCap(config.DetailWaitSeconds);
                if (task.Wait(wait)) return;

                // The page goes out without the detail; the open client is told to reload when it lands.
                _logger.Debug("Detail on open: still running after {0} s for '{1}', the response goes out without it", wait.TotalSeconds, item.Name);
                _notifier.WhenDone(task, item);
            }
            catch (Exception ex)
            {
                _logger.Debug("Detail on open: skipped: {0}", ex.Message);
            }
        }

        /// <summary>The configured wait, clamped to 0..30 s; zero means answer at once and rely on the refresh.</summary>
        internal static TimeSpan WaitCap(int configuredSeconds)
            => TimeSpan.FromSeconds(Math.Max(0, Math.Min(MaxWaitSeconds, configuredSeconds)));

        /// <summary>The item id a GET request is about (detail page, season grid, episode list, folder listing); null for any other request.</summary>
        internal static string TryGetItemId(string verb, string pathInfo, Func<string, string> query)
        {
            if (!string.Equals(verb, "GET", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(pathInfo)) return null;
            var m = ItemRouteRegex.Match(pathInfo);
            if (m.Success) return m.Groups[2].Value;
            m = ShowsRouteRegex.Match(pathInfo);
            if (m.Success) return m.Groups[2].Value;
            if (ItemsListRouteRegex.IsMatch(pathInfo) && query != null)
            {
                var parent = query("ParentId");
                return string.IsNullOrWhiteSpace(parent) ? null : parent.Trim();
            }
            return null;
        }

        private BaseItem ResolveItem(string id)
        {
            long numeric;
            if (long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numeric))
                return _libraryManager.GetItemById(numeric);
            Guid guid;
            if (Guid.TryParse(id, out guid))
                return _libraryManager.GetItemById(guid);
            return null;
        }
    }

    /// <summary>
    /// Tells the open clients that a title changed once a detail fetch outlived the request that
    /// started it, or once its actors got their photos. Emby's own LibraryChanged notifier batches updates and sends them 40 s after the
    /// last one; this sends the same message, for this item only, the moment the detail is written,
    /// so the detail page reloads itself instead of waiting for the user to close and reopen it.
    /// </summary>
    internal sealed class LateDetailNotifier
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<long, byte> _pending = new ConcurrentDictionary<long, byte>();

        public LateDetailNotifier(ISessionManager sessionManager, ILogger logger)
        {
            _sessionManager = sessionManager;
            _logger = logger;
        }

        /// <summary>One notification per item however many requests waited on the same fetch.</summary>
        public void WhenDone(Task fetch, BaseItem item)
        {
            if (fetch == null || item == null || _sessionManager == null) return;
            if (!_pending.TryAdd(item.InternalId, 0)) return;
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
                var userIds = _sessionManager.Sessions.Select(s => s.UserInternalId).Where(id => id != 0).Distinct().ToArray();
                if (userIds.Length == 0) return;
                var info = new LibraryUpdateInfo { ItemsUpdated = UpdatedIds(item, item is Series ? SeasonsOf((Series)item) : new BaseItem[0]) };
                await _sessionManager.SendMessageToUserSessions(userIds, "LibraryChanged", info, CancellationToken.None).ConfigureAwait(false);
                _logger.Debug("Detail on open: late detail of '{0}' sent to {1} user(s)", item.Name, userIds.Length);
            }
            catch (Exception ex)
            {
                _logger.Debug("Detail on open: late notification for '{0}' failed: {1}", item.Name, ex.Message);
            }
            finally
            {
                byte removed;
                _pending.TryRemove(item.InternalId, out removed);
            }
        }

        private static BaseItem[] SeasonsOf(Series series)
        {
            try { return CatalogServices.Library.Children(series, typeof(Season), false).ToArray(); }
            catch (Exception) { return new BaseItem[0]; }
        }

        /// <summary>The item and, for a series, its seasons: a season page is open on the season, not on the series.</summary>
        internal static string[] UpdatedIds(BaseItem item, IEnumerable<BaseItem> related)
            => new[] { item }.Concat(related ?? new BaseItem[0]).Select(i => i.GetClientId()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
