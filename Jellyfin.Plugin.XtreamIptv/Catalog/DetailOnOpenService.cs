using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using DirectoryService = MediaBrowser.Controller.Providers.DirectoryService;
using MetadataRefreshMode = MediaBrowser.Controller.Providers.MetadataRefreshMode;
using MetadataRefreshOptions = MediaBrowser.Controller.Providers.MetadataRefreshOptions;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using SeriesInfo = XtreamIptv.Client.Models.SeriesInfo;

namespace Jellyfin.Plugin.XtreamIptv.Catalog
{
    /// <summary>
    /// The on-demand half of the catalogue: a movie gets its provider detail the first time it is
    /// opened, a series gets its seasons and episodes the first time it is opened (or again after
    /// the provider changed it). Both write straight into Jellyfin's database.
    /// </summary>
    public sealed class DetailOnOpenService
    {
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromHours(1);
        /// <summary>A person refreshed this recently and still without a photo is not asked for again.</summary>
        internal static readonly TimeSpan PhotoRetryAfter = TimeSpan.FromDays(1);
        internal const int MaxPhotosPerTitle = 15;
        private static readonly SemaphoreSlim PhotoGate = new SemaphoreSlim(3, 3);

        private readonly ILogger<DetailOnOpenService> _logger;
        private readonly XtreamClient _client;
        private readonly JellyfinCatalog _library;
        private readonly CatalogState _state;
        private readonly IFileSystem _fileSystem;
        private readonly LateDetailNotifier _notifier;
        private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlight = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _negative = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Guid, DateTime> _photoRuns = new ConcurrentDictionary<Guid, DateTime>();

        internal DetailOnOpenService(ILogger<DetailOnOpenService> logger, XtreamClient client, JellyfinCatalog library, CatalogState state, IFileSystem fileSystem, LateDetailNotifier notifier)
        {
            _logger = logger;
            _client = client;
            _library = library;
            _state = state;
            _fileSystem = fileSystem;
            _notifier = notifier;
        }

        private static PluginConfiguration Config => Plugin.InstanceOrNull?.Configuration;

        /// <summary>Dispatches on the item type. Completes when the work is done (or nothing was needed). Never throws.</summary>
        public Task EnsureAsync(BaseItem item, CancellationToken ct)
        {
            var config = Config;
            if (config == null || !config.DetailOnOpen || item == null) return Task.CompletedTask;

            var movie = item as Movie;
            if (movie != null) return WithPhotos(EnsureMovieDetailAsync(movie, config, ct), movie, config);

            var series = item as Series;
            if (series != null) return WithPhotos(EnsureEpisodesAsync(series, config, ct), series, config);

            var season = item as Season;
            if (season != null && !season.SeriesId.Equals(Guid.Empty))
            {
                var parentSeries = _library.GetItem(season.SeriesId) as Series;
                if (parentSeries != null) return EnsureEpisodesAsync(parentSeries, config, ct);
            }
            return Task.CompletedTask;
        }

        // ----- Movies -------------------------------------------------------------------------

        private Task EnsureMovieDetailAsync(Movie movie, PluginConfiguration config, CancellationToken ct)
        {
            int streamId;
            if (!TryGetProviderId(movie, out streamId)) return Task.CompletedTask;
            if (_state.IsDetailed(streamId)) return Task.CompletedTask;
            var cacheKey = "movie:" + CatalogState.Key(streamId);
            if (IsNegative(cacheKey)) return Task.CompletedTask;

            var lazy = _inFlight.GetOrAdd(cacheKey, k => new Lazy<Task>(() => FetchMovieDetailAsync(movie, streamId, config, k, ct)));
            return lazy.Value;
        }

        private async Task FetchMovieDetailAsync(Movie movie, int streamId, PluginConfiguration config, string cacheKey, CancellationToken ct)
        {
            try
            {
                VodInfo info;
                try
                {
                    info = await _client.GetVodInfoAsync(config, streamId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Detail on open: get_vod_info {Id} failed: {Message}", streamId, ex.Message);
                    Remember(cacheKey);
                    return;
                }
                if (info.Info == null || !info.Info.HasAnyDetail)
                {
                    _logger.LogDebug("Detail on open: provider has no detail for stream {Id}", streamId);
                    Remember(cacheKey);
                    return;
                }

                var people = JellyfinCatalog.ApplyVodDetail(movie, info.Info);
                await _library.UpdateItemAsync(movie, movie.GetParent(), ct).ConfigureAwait(false);
                // Replace, not add: the sync writes no people, and a changed row lands here again.
                await _library.LibraryManager.UpdatePeopleAsync(movie, people, ct).ConfigureAwait(false);
                _state.MarkDetailed(streamId);
                SaveState();
                _logger.LogDebug("Detail on open: movie {Id} updated", streamId);
            }
            catch (Exception ex)
            {
                // Warn, not Debug: a failure here leaves the movie without its detail for an hour.
                _logger.LogWarning("Detail on open failed for '{Name}': {Message}", movie.Name, ex.Message);
            }
            finally
            {
                Lazy<Task> removed;
                _inFlight.TryRemove(cacheKey, out removed);
            }
        }

        // ----- Series -------------------------------------------------------------------------

        private Task EnsureEpisodesAsync(Series series, PluginConfiguration config, CancellationToken ct)
        {
            int seriesId;
            if (!TryGetProviderId(series, out seriesId)) return Task.CompletedTask;
            var state = _state.FindSeries(seriesId);
            if (state == null) return Task.CompletedTask;
            if (!state.Stale && state.Episodes > 0) return Task.CompletedTask;
            var cacheKey = "series:" + CatalogState.Key(seriesId);
            if (IsNegative(cacheKey)) return Task.CompletedTask;

            var lazy = _inFlight.GetOrAdd(cacheKey, k => new Lazy<Task>(() => FetchEpisodesAsync(series, seriesId, config, k, ct)));
            return lazy.Value;
        }

        private async Task FetchEpisodesAsync(Series series, int seriesId, PluginConfiguration config, string cacheKey, CancellationToken ct)
        {
            try
            {
                SeriesDetailInfo detail;
                try
                {
                    detail = await _client.GetSeriesInfoAsync(config, seriesId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Detail on open: get_series_info {Id} failed: {Message}", seriesId, ex.Message);
                    Remember(cacheKey);
                    return;
                }
                var episodes = detail.Episodes ?? new Dictionary<string, List<EpisodeInfo>>();
                if (episodes.Values.All(l => l == null || l.Count == 0))
                {
                    _logger.LogDebug("Detail on open: series {Id} has no episodes", seriesId);
                    Remember(cacheKey);
                    return;
                }

                var written = await WriteEpisodesAsync(series, detail, config, ct).ConfigureAwait(false);
                _state.SetSeriesLoaded(seriesId, written);
                SaveState();
                _logger.LogDebug("Detail on open: series {Id}: {Count} episodes", seriesId, written);
            }
            catch (Exception ex)
            {
                // Warn, not Debug: a failure here leaves the series without episodes.
                _logger.LogWarning("Detail on open failed for series '{Name}': {Message}", series.Name, ex.Message);
            }
            finally
            {
                Lazy<Task> removed;
                _inFlight.TryRemove(cacheKey, out removed);
            }
        }

        /// <summary>Creates or updates every season and episode of the series, deletes the ones that vanished. Returns the episode count.</summary>
        private async Task<int> WriteEpisodesAsync(Series series, SeriesDetailInfo detail, PluginConfiguration config, CancellationToken ct)
        {
            await RefreshSeriesFromInfoAsync(series, detail.Info, ct).ConfigureAwait(false);

            var seasons = _library.Children(series, BaseItemKind.Season, false).OfType<Season>()
                .Where(s => s.IndexNumber.HasValue)
                .GroupBy(s => s.IndexNumber.Value).ToDictionary(g => g.Key, g => g.First());
            var existingEpisodes = _library.Children(series, BaseItemKind.Episode, true).OfType<Episode>()
                .GroupBy(e => e.GetProviderId(JellyfinCatalog.ProviderKey) ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var seasonCovers = (detail.Seasons ?? new List<SeasonInfo>())
                .GroupBy(s => s.SeasonNumber).ToDictionary(g => g.Key, g => g.First().Cover);

            var keptEpisodes = new HashSet<string>(StringComparer.Ordinal);
            var keptSeasons = new HashSet<int>();
            var displayName = CatalogNaming.DisplayName(series.Name, series.ProductionYear);
            var count = 0;
            var createdSeasons = new List<Season>();
            var seasonsWithEpisodes = new HashSet<int>();

            try
            {
                foreach (var seasonEntry in detail.Episodes)
                {
                    ct.ThrowIfCancellationRequested();
                    var list = seasonEntry.Value ?? new List<EpisodeInfo>();
                    if (list.Count == 0) continue;
                    int keySeason;
                    var haveKeySeason = int.TryParse(seasonEntry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out keySeason) && keySeason >= 0;

                    var bySeason = new Dictionary<int, List<Tuple<EpisodeInfo, int>>>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        var ep = list[i];
                        var seasonNumber = haveKeySeason ? keySeason : (ep.Season > 0 ? ep.Season : (ep.Info?.Season ?? 1));
                        var episodeNumber = ep.EpisodeNum >= 0 ? ep.EpisodeNum : i + 1;
                        List<Tuple<EpisodeInfo, int>> bucket;
                        if (!bySeason.TryGetValue(seasonNumber, out bucket))
                        {
                            bucket = new List<Tuple<EpisodeInfo, int>>();
                            bySeason[seasonNumber] = bucket;
                        }
                        bucket.Add(Tuple.Create(ep, episodeNumber));
                    }

                    foreach (var pair in bySeason)
                    {
                        var seasonNumber = pair.Key;
                        keptSeasons.Add(seasonNumber);
                        Season season;
                        string cover;
                        seasonCovers.TryGetValue(seasonNumber, out cover);
                        var seasonName = _library.SeasonName(series, seasonNumber);
                        if (!seasons.TryGetValue(seasonNumber, out season))
                        {
                            season = _library.NewSeason(GetProviderIdOrZero(series), seasonNumber);
                            JellyfinCatalog.ApplySeason(season, series, seasonNumber, seasonName, cover);
                            _library.CreateItem(season, series);
                            seasons[seasonNumber] = season;
                            createdSeasons.Add(season);
                        }
                        else if (!string.Equals(season.Name, seasonName, StringComparison.Ordinal))
                        {
                            // A season named in another language (an earlier version, or Jellyfin's own) follows the series' language.
                            season.Name = seasonName;
                            await _library.UpdateItemAsync(season, series, ct).ConfigureAwait(false);
                        }

                        var toCreate = new List<BaseItem>();
                        foreach (var t in pair.Value)
                        {
                            var ep = t.Item1;
                            var episodeNumber = t.Item2;
                            var key = CatalogState.Key(ep.Id);
                            var title = CatalogNaming.CleanEpisodeTitle(ep.Title, displayName, seasonNumber, episodeNumber);
                            if (title.Length == 0) title = string.Format(CultureInfo.InvariantCulture, "Episode {0}", episodeNumber);
                            keptEpisodes.Add(key);
                            count++;

                            Episode existing;
                            if (existingEpisodes.TryGetValue(key, out existing))
                            {
                                JellyfinCatalog.ApplyEpisode(existing, series, season, ep, seasonNumber, episodeNumber, title, config);
                                await _library.UpdateItemAsync(existing, season, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                var episode = _library.NewEpisode(ep.Id);
                                JellyfinCatalog.ApplyEpisode(episode, series, season, ep, seasonNumber, episodeNumber, title, config);
                                toCreate.Add(episode);
                            }
                        }
                        _library.CreateItems(toCreate, season, ct);
                        seasonsWithEpisodes.Add(seasonNumber);
                    }
                }
            }
            catch (Exception)
            {
                // A season created in this pass without its episodes would show as an empty grid: take it back.
                foreach (var season in createdSeasons.Where(s => !s.IndexNumber.HasValue || !seasonsWithEpisodes.Contains(s.IndexNumber.Value)))
                {
                    try { _library.DeleteItem(season, series); }
                    catch (Exception ex) { _logger.LogDebug("Detail on open: rollback of season '{Name}' failed: {Message}", season.Name, ex.Message); }
                }
                throw;
            }

            foreach (var kv in existingEpisodes)
            {
                if (keptEpisodes.Contains(kv.Key)) continue;
                try { _library.DeleteItem(kv.Value, kv.Value.GetParent()); }
                catch (Exception ex) { _logger.LogDebug("Detail on open: episode '{Name}' not removed: {Message}", kv.Value.Name, ex.Message); }
            }
            foreach (var kv in seasons)
            {
                if (keptSeasons.Contains(kv.Key)) continue;
                try { _library.DeleteItem(kv.Value, series); }
                catch (Exception ex) { _logger.LogDebug("Detail on open: season '{Name}' not removed: {Message}", kv.Value.Name, ex.Message); }
            }
            return count;
        }

        /// <summary>The info object of get_series_info can be richer than the list row; fill what the series still lacks. The cast is written here, never by the sync.</summary>
        private async Task RefreshSeriesFromInfoAsync(Series series, SeriesInfo info, CancellationToken ct)
        {
            if (info == null) return;
            try { await _library.SetPeopleAsync(series, info.Cast, info.Director, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug("Detail on open: people of '{Name}' not saved: {Message}", series.Name, ex.Message); }
            var changed = false;
            if (string.IsNullOrWhiteSpace(series.Overview) && !string.IsNullOrWhiteSpace(info.Plot))
            {
                series.Overview = info.Plot.Trim();
                changed = true;
            }
            if (!series.HasImage(ImageType.Backdrop, 0) && info.BackdropPath.Count > 0)
            {
                JellyfinCatalog.SetRemoteImage(series, ImageType.Backdrop, info.BackdropPath[0]);
                changed = true;
            }
            if (!series.HasImage(ImageType.Primary, 0) && !string.IsNullOrWhiteSpace(info.Cover))
            {
                JellyfinCatalog.SetRemoteImage(series, ImageType.Primary, info.Cover);
                changed = true;
            }
            if ((series.Genres == null || series.Genres.Length == 0) && !string.IsNullOrWhiteSpace(info.Genre))
            {
                series.Genres = JellyfinCatalog.SplitList(info.Genre).ToArray();
                changed = true;
            }
            if (!changed) return;
            try { await _library.UpdateItemAsync(series, series.GetParent(), ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug("Detail on open: series '{Name}' not updated: {Message}", series.Name, ex.Message); }
        }

        // ----- Actor photos -------------------------------------------------------------------

        /// <summary>Once the detail (and so the cast) is written, the photos run in the background; the request never waits for them.</summary>
        private Task WithPhotos(Task detail, BaseItem item, PluginConfiguration config)
        {
            if (!config.ActorPhotosOnOpen) return detail;
            detail.ContinueWith(_ => SchedulePeoplePhotos(item), TaskScheduler.Default);
            return detail;
        }

        /// <summary>
        /// Xtream has no actor photos. Jellyfin's own person providers (TMDB) find them by name, so the
        /// people of the opened title that still lack a photo are refreshed here, a few at a time, and
        /// the open clients are told when they land. One run per title per hour.
        /// </summary>
        internal void SchedulePeoplePhotos(BaseItem item)
        {
            var now = DateTime.UtcNow;
            DateTime last;
            if (_photoRuns.TryGetValue(item.Id, out last) && now - last < NegativeCacheTtl) return;
            _photoRuns[item.Id] = now;
            Task.Run(() => RefreshPeoplePhotosAsync(item));
        }

        private async Task RefreshPeoplePhotosAsync(BaseItem item)
        {
            try
            {
                var people = _library.LibraryManager.GetPeople(item) ?? new List<PersonInfo>();
                var candidates = new List<Person>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int notPerson = 0, withPhoto = 0, recent = 0;
                foreach (var info in people)
                {
                    if (string.IsNullOrWhiteSpace(info.Name) || !seen.Add(info.Name)) continue;
                    var person = _library.LibraryManager.GetPerson(info.Name);
                    if (person == null) { notPerson++; continue; }
                    if (person.HasImage(ImageType.Primary, 0)) { withPhoto++; continue; }
                    if (!NeedsPhoto(false, person.DateLastRefreshed, DateTime.UtcNow)) { recent++; continue; }
                    candidates.Add(person);
                    if (candidates.Count >= MaxPhotosPerTitle) break;
                }
                _logger.LogDebug("Actor photos: '{Name}': {People} people, {NotResolved} not resolved, {WithPhoto} with photo, {Recent} asked recently, {ToRefresh} to refresh",
                    item.Name, people.Count, notPerson, withPhoto, recent, candidates.Count);
                if (candidates.Count == 0) return;

                var found = 0;
                var tasks = candidates.Select(async person =>
                {
                    await PhotoGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                            // Like a refresh asked from the UI: only then do the person providers search by name.
                            IsAutomated = false,
                        };
                        await person.RefreshMetadata(options, CancellationToken.None).ConfigureAwait(false);
                        if (person.HasImage(ImageType.Primary, 0)) Interlocked.Increment(ref found);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Actor photos: '{Name}' not refreshed: {Message}", person.Name, ex.Message);
                    }
                    finally
                    {
                        PhotoGate.Release();
                    }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
                _logger.LogDebug("Actor photos: '{Name}': {Found} of {Count} people got a photo", item.Name, found, candidates.Count);
                if (found > 0) await _notifier.NotifyNow(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Actor photos for '{Name}' failed: {Message}", item.Name, ex.Message);
            }
        }

        /// <summary>No photo yet, and not asked recently (a name the providers do not know is not retried every open).</summary>
        internal static bool NeedsPhoto(bool hasImage, DateTime lastRefreshed, DateTime now)
            => !hasImage && now - lastRefreshed >= PhotoRetryAfter;

        // ----- Helpers ------------------------------------------------------------------------

        internal static bool TryGetProviderId(BaseItem item, out int id)
        {
            id = 0;
            var value = item.GetProviderId(JellyfinCatalog.ProviderKey);
            return !string.IsNullOrEmpty(value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        private static int GetProviderIdOrZero(BaseItem item)
        {
            int id;
            return TryGetProviderId(item, out id) ? id : 0;
        }

        private bool IsNegative(string key)
        {
            DateTime until;
            if (!_negative.TryGetValue(key, out until)) return false;
            if (until > DateTime.UtcNow) return true;
            _negative.TryRemove(key, out until);
            return false;
        }

        private void Remember(string key) => _negative[key] = DateTime.UtcNow + NegativeCacheTtl;

        private void SaveState()
        {
            try { _state.Save(); }
            catch (Exception ex) { _logger.LogDebug("Detail on open: state not saved: {Message}", ex.Message); }
        }
    }
}
