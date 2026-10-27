using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MetadataRefreshMode = MediaBrowser.Controller.Providers.MetadataRefreshMode;
using MetadataRefreshOptions = MediaBrowser.Controller.Providers.MetadataRefreshOptions;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.XtreamIptv.Catalog
{
    /// <summary>
    /// The on-demand half of the catalogue: a movie gets its provider detail the first time it is
    /// opened, a series gets its seasons and episodes the first time it is opened (or again after
    /// the provider changed it). Both write straight into Emby's database.
    /// </summary>
    internal sealed class DetailOnOpenService
    {
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromHours(1);
        /// <summary>A person refreshed this recently and still without a photo is not asked for again.</summary>
        internal static readonly TimeSpan PhotoRetryAfter = TimeSpan.FromDays(1);
        internal const int MaxPhotosPerTitle = 15;
        private static readonly SemaphoreSlim PhotoGate = new SemaphoreSlim(3, 3);

        private readonly ILogger _logger;
        private readonly XtreamClient _client;
        private readonly Func<PluginConfiguration> _config;
        private readonly EmbyCatalog _library;
        private readonly CatalogState _state;
        private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlight = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _negative = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, DateTime> _photoRuns = new ConcurrentDictionary<long, DateTime>();

        public DetailOnOpenService(ILogger logger, XtreamClient client, Func<PluginConfiguration> config, EmbyCatalog library, CatalogState state)
        {
            _logger = logger;
            _client = client;
            _config = config;
            _library = library;
            _state = state;
        }

        /// <summary>Dispatches on the item type. Completes when the work is done (or nothing was needed). Never throws.</summary>
        public Task EnsureAsync(BaseItem item, CancellationToken ct)
        {
            var config = _config();
            if (config == null || !config.DetailOnOpen || item == null) return Task.CompletedTask;

            var movie = item as Movie;
            if (movie != null) return WithPhotos(EnsureMovieDetailAsync(movie, config, ct), movie, config);

            var series = item as Series;
            if (series != null) return WithPhotos(EnsureEpisodesAsync(series, config, ct), series, config);

            var season = item as Season;
            if (season != null && season.SeriesId > 0)
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
                    _logger.Debug("Detail on open: get_vod_info {0} failed: {1}", streamId, ex.Message);
                    Remember(cacheKey);
                    return;
                }
                if (info.Info == null || !info.Info.HasAnyDetail)
                {
                    _logger.Debug("Detail on open: provider has no detail for stream {0}", streamId);
                    Remember(cacheKey);
                    return;
                }

                var people = EmbyCatalog.ApplyVodDetail(movie, info.Info);
                var parent = movie.GetParent();
                _library.UpdateItem(movie, parent);
                // Replace, not add: the sync writes no people, and a changed row lands here again.
                _library.LibraryManager.UpdatePeople(movie, people, false);
                _state.MarkDetailed(streamId);
                SaveState();
                _logger.Debug("Detail on open: movie {0} updated", streamId);
            }
            catch (Exception ex)
            {
                // Warn, not Debug: a failure here leaves the movie without its detail for an hour.
                _logger.Warn("Detail on open failed for '{0}': {1}", movie.Name, ex.Message);
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
                    _logger.Debug("Detail on open: get_series_info {0} failed: {1}", seriesId, ex.Message);
                    Remember(cacheKey);
                    return;
                }
                var episodes = detail.Episodes ?? new Dictionary<string, List<EpisodeInfo>>();
                if (episodes.Values.All(l => l == null || l.Count == 0))
                {
                    _logger.Debug("Detail on open: series {0} has no episodes", seriesId);
                    Remember(cacheKey);
                    return;
                }

                var written = WriteEpisodes(series, detail, config, ct);
                _state.SetSeriesLoaded(seriesId, written);
                SaveState();
                _logger.Debug("Detail on open: series {0}: {1} episodes", seriesId, written);
            }
            catch (Exception ex)
            {
                // Warn, not Debug: a failure here leaves the series without episodes.
                _logger.Warn("Detail on open failed for series '{0}': {1}", series.Name, ex.Message);
            }
            finally
            {
                Lazy<Task> removed;
                _inFlight.TryRemove(cacheKey, out removed);
            }
        }

        /// <summary>Creates or updates every season and episode of the series, deletes the ones that vanished. Returns the episode count.</summary>
        private int WriteEpisodes(Series series, SeriesDetailInfo detail, PluginConfiguration config, CancellationToken ct)
        {
            var collections = _library.LibraryManager.GetCollectionFolders(series);
            var rootForCreate = new CatalogRoot { Collection = collections.OfType<CollectionFolder>().FirstOrDefault() };

            RefreshSeriesFromInfo(series, detail.Info);

            var seasons = _library.Children(series, typeof(Season), false).OfType<Season>()
                .Where(s => s.IndexNumber.HasValue)
                .GroupBy(s => s.IndexNumber.Value).ToDictionary(g => g.Key, g => g.First());
            var existingEpisodes = _library.Children(series, typeof(Episode), true).OfType<Episode>()
                .GroupBy(e => e.GetProviderId(EmbyCatalog.ProviderKey) ?? string.Empty)
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
                            EmbyCatalog.ApplySeason(season, series, seasonNumber, seasonName, cover);
                            _library.CreateItem(season, series, rootForCreate);
                            seasons[seasonNumber] = season;
                            createdSeasons.Add(season);
                        }
                        else if (!string.Equals(season.Name, seasonName, StringComparison.Ordinal))
                        {
                            // A season named in another language (an earlier version, or Emby's own) follows the series' language.
                            season.Name = seasonName;
                            _library.UpdateItem(season, series);
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
                                EmbyCatalog.ApplyEpisode(existing, series, season, ep, seasonNumber, episodeNumber, title, config);
                                _library.UpdateItem(existing, season);
                            }
                            else
                            {
                                var episode = _library.NewEpisode(ep.Id);
                                EmbyCatalog.ApplyEpisode(episode, series, season, ep, seasonNumber, episodeNumber, title, config);
                                toCreate.Add(episode);
                            }
                        }
                        _library.CreateItems(toCreate, season, rootForCreate, ct);
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
                    catch (Exception ex) { _logger.Debug("Detail on open: rollback of season '{0}' failed: {1}", season.Name, ex.Message); }
                }
                throw;
            }

            foreach (var kv in existingEpisodes)
            {
                if (keptEpisodes.Contains(kv.Key)) continue;
                try { _library.DeleteItem(kv.Value, kv.Value.GetParent()); }
                catch (Exception ex) { _logger.Debug("Detail on open: episode '{0}' not removed: {1}", kv.Value.Name, ex.Message); }
            }
            foreach (var kv in seasons)
            {
                if (keptSeasons.Contains(kv.Key)) continue;
                try { _library.DeleteItem(kv.Value, series); }
                catch (Exception ex) { _logger.Debug("Detail on open: season '{0}' not removed: {1}", kv.Value.Name, ex.Message); }
            }
            return count;
        }

        /// <summary>The info object of get_series_info can be richer than the list row; fill what the series still lacks. The cast is written here, never by the sync.</summary>
        private void RefreshSeriesFromInfo(Series series, SeriesInfo info)
        {
            if (info == null) return;
            try { _library.SetPeople(series, info.Cast, info.Director, false); }
            catch (Exception ex) { _logger.Debug("Detail on open: people of '{0}' not saved: {1}", series.Name, ex.Message); }
            var changed = false;
            if (string.IsNullOrWhiteSpace(series.Overview) && !string.IsNullOrWhiteSpace(info.Plot))
            {
                series.Overview = info.Plot.Trim();
                changed = true;
            }
            if (!series.HasImage(ImageType.Backdrop, 0) && info.BackdropPath.Count > 0)
            {
                EmbyCatalog.SetRemoteImage(series, ImageType.Backdrop, info.BackdropPath[0]);
                changed = true;
            }
            if (!series.HasImage(ImageType.Primary, 0) && !string.IsNullOrWhiteSpace(info.Cover))
            {
                EmbyCatalog.SetRemoteImage(series, ImageType.Primary, info.Cover);
                changed = true;
            }
            if ((series.Genres == null || series.Genres.Length == 0) && !string.IsNullOrWhiteSpace(info.Genre))
            {
                series.SetGenres(EmbyCatalog.SplitList(info.Genre));
                changed = true;
            }
            if (!changed) return;
            try { _library.UpdateItem(series, series.GetParent()); }
            catch (Exception ex) { _logger.Debug("Detail on open: series '{0}' not updated: {1}", series.Name, ex.Message); }
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
        /// Xtream has no actor photos. Emby's own person providers find them by name, so the people
        /// of the opened title that still lack a photo are refreshed here, a few at a time, and the
        /// open page is told when they land. One run per title per hour.
        /// </summary>
        internal void SchedulePeoplePhotos(BaseItem item)
        {
            var now = DateTime.UtcNow;
            DateTime last;
            if (_photoRuns.TryGetValue(item.InternalId, out last) && now - last < NegativeCacheTtl) return;
            _photoRuns[item.InternalId] = now;
            Task.Run(() => RefreshPeoplePhotosAsync(item));
        }

        private async Task RefreshPeoplePhotosAsync(BaseItem item)
        {
            try
            {
                var fileSystem = CatalogServices.FileSystem;
                if (fileSystem == null)
                {
                    _logger.Debug("Actor photos: no file system service, skipped");
                    return;
                }
                // EnableIds fills PersonInfo.Id with the Person item id (the plain overload leaves it at 0).
                var people = _library.LibraryManager.GetItemPeople(new InternalPeopleQuery
                {
                    ItemIds = new[] { item.InternalId },
                    EnableIds = true,
                }) ?? new List<PersonInfo>();
                var candidates = new List<Person>();
                var seen = new HashSet<long>();
                int noId = 0, notPerson = 0, withPhoto = 0, recent = 0;
                foreach (var info in people)
                {
                    if (info.Id <= 0) { noId++; continue; }
                    if (!seen.Add(info.Id)) continue;
                    var person = _library.GetItem(info.Id) as Person;
                    if (person == null) { notPerson++; continue; }
                    if (person.HasImage(ImageType.Primary, 0)) { withPhoto++; continue; }
                    if (!NeedsPhoto(false, person.DateLastRefreshed, DateTimeOffset.UtcNow)) { recent++; continue; }
                    candidates.Add(person);
                    if (candidates.Count >= MaxPhotosPerTitle) break;
                }
                _logger.Debug("Actor photos: '{0}': {1} people, {2} without id, {3} not resolved, {4} with photo, {5} asked recently, {6} to refresh",
                    item.Name, people.Count, noId, notPerson, withPhoto, recent, candidates.Count);
                if (candidates.Count == 0) return;

                var found = 0;
                var tasks = candidates.Select(async person =>
                {
                    await PhotoGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var options = new MetadataRefreshOptions(fileSystem)
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
                        _logger.Debug("Actor photos: '{0}' not refreshed: {1}", person.Name, ex.Message);
                    }
                    finally
                    {
                        PhotoGate.Release();
                    }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
                _logger.Debug("Actor photos: '{0}': {1} of {2} people got a photo", item.Name, found, candidates.Count);
                if (found > 0) await CatalogServices.Notifier.NotifyNow(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug("Actor photos for '{0}' failed: {1}", item.Name, ex.Message);
            }
        }

        /// <summary>No photo yet, and not asked recently (a name the providers do not know is not retried every open).</summary>
        internal static bool NeedsPhoto(bool hasImage, DateTimeOffset lastRefreshed, DateTimeOffset now)
            => !hasImage && now - lastRefreshed >= PhotoRetryAfter;

        // ----- Helpers ------------------------------------------------------------------------

        internal static bool TryGetProviderId(BaseItem item, out int id)
        {
            id = 0;
            var value = item.GetProviderId(EmbyCatalog.ProviderKey);
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
            catch (Exception ex) { _logger.Debug("Detail on open: state not saved: {0}", ex.Message); }
        }
    }
}
