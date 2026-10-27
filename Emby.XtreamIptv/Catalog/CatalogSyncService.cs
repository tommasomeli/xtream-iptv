using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;

namespace Emby.XtreamIptv.Catalog
{
    /// <summary>A copy of the sync status at one moment, safe to hand to the API.</summary>
    public sealed class CatalogSyncSnapshot
    {
        public bool Running { get; set; }
        public string Phase { get; set; }
        public int Processed { get; set; }
        public int Total { get; set; }
        public DateTime? LastRunUtc { get; set; }
        public string LastSummary { get; set; }
    }

    /// <summary>Counts of one phase of a sync, as logged and summarised.</summary>
    internal sealed class PhaseCounts
    {
        public int Listed;
        public int Created;
        public int Updated;
        public int Unchanged;
        public int Removed;
        public int SkippedBySafety;
        public int Failed;
        public string Problem;

        public override string ToString()
        {
            if (Problem != null) return Problem;
            var s = string.Format(CultureInfo.InvariantCulture, "{0} listed, {1} created, {2} updated, {3} unchanged, {4} removed",
                Listed, Created, Updated, Unchanged, Removed);
            if (SkippedBySafety > 0) s += string.Format(CultureInfo.InvariantCulture, ", {0} removals held back by the safety threshold", SkippedBySafety);
            if (Failed > 0) s += string.Format(CultureInfo.InvariantCulture, ", {0} failed", Failed);
            return s;
        }
    }

    /// <summary>
    /// List-only sync of the Xtream catalogue straight into Emby's library database: one list call
    /// per kind, no detail call, no file. Movies become one Movie item each (the other releases are
    /// media sources of that item), series become Series items whose episodes are loaded when opened.
    /// People are not written here (one UpdatePeople per item is what made a first run slow): the
    /// cast comes with the detail the first time a title is opened.
    /// </summary>
    internal sealed class CatalogSyncService
    {
        private const int SafetyMinimumExisting = 10;

        private readonly ILogger _logger;
        private readonly XtreamClient _client;
        private readonly Func<PluginConfiguration> _config;
        private readonly Action _saveConfig;
        private readonly EmbyCatalog _library;
        private readonly CatalogState _state;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _statusSync = new object();

        private bool _running;
        private string _phase = "Idle";
        private int _processed;
        private int _total;
        private DateTime? _lastRunUtc;
        private string _lastSummary;
        private bool _statusSeeded;
        private CancellationTokenSource _backgroundCts;

        public CatalogSyncService(
            ILogger logger, XtreamClient client, Func<PluginConfiguration> config, Action saveConfig,
            EmbyCatalog library, CatalogState state)
        {
            _logger = logger;
            _client = client;
            _config = config;
            _saveConfig = saveConfig;
            _library = library;
            _state = state;
        }

        public bool IsRunning
        {
            get { lock (_statusSync) return _running; }
        }

        public CatalogSyncSnapshot GetStatus()
        {
            lock (_statusSync)
            {
                SeedStatusFromConfig();
                return new CatalogSyncSnapshot
                {
                    Running = _running,
                    Phase = _phase,
                    Processed = _processed,
                    Total = _total,
                    LastRunUtc = _lastRunUtc,
                    LastSummary = _lastSummary,
                };
            }
        }

        /// <summary>Starts a sync on the thread pool. False when one is already running.</summary>
        public bool TryStartBackground()
        {
            lock (_statusSync)
            {
                if (_running) return false;
            }
            if (_gate.CurrentCount == 0) return false;
            var cts = new CancellationTokenSource();
            _backgroundCts = cts;
            Task.Run(async () =>
            {
                try { await RunAsync(cts.Token, null).ConfigureAwait(false); }
                catch (Exception ex) { _logger.Error("Catalog sync: background run failed: {0}", ex.Message); }
                finally
                {
                    if (ReferenceEquals(_backgroundCts, cts)) _backgroundCts = null;
                    cts.Dispose();
                }
            });
            return true;
        }

        public void Cancel()
        {
            var cts = _backgroundCts;
            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>Runs one sync. False when another run holds the gate; true when this call ran (see the summary for the outcome).</summary>
        public async Task<bool> RunAsync(CancellationToken ct, IProgress<double> progress)
        {
            if (!_gate.Wait(0)) return false;
            var watch = Stopwatch.StartNew();
            var summary = string.Empty;
            try
            {
                SetRunning(true, "Starting", 0);
                var config = _config();
                var problem = Validate(config);
                if (problem != null)
                {
                    _logger.Warn("Catalog sync: {0}", problem);
                    summary = problem;
                    return true;
                }

                _logger.Info("Catalog sync: start (movies {0}, series {1})", config.SyncMovies ? "on" : "off", config.SyncSeries ? "on" : "off");
                var phases = (config.SyncMovies ? 1 : 0) + (config.SyncSeries ? 1 : 0);
                var phaseIndex = 0;
                var parts = new List<string>();

                if (config.SyncMovies)
                {
                    var counts = new PhaseCounts();
                    await SyncMoviesAsync(config, counts, Scaled(progress, phaseIndex, phases), ct).ConfigureAwait(false);
                    phaseIndex++;
                    _logger.Info("Catalog sync: movies: {0}", counts);
                    parts.Add("Movies: " + counts);
                    _state.Save();
                }

                if (config.SyncSeries)
                {
                    var counts = new PhaseCounts();
                    await SyncSeriesAsync(config, counts, Scaled(progress, phaseIndex, phases), ct).ConfigureAwait(false);
                    _logger.Info("Catalog sync: series: {0}", counts);
                    parts.Add("Series: " + counts);
                    _state.Save();
                }

                if (parts.Count == 0) parts.Add("Nothing enabled");
                summary = string.Join(". ", parts) + string.Format(CultureInfo.InvariantCulture, " ({0:0} s)", watch.Elapsed.TotalSeconds);
                progress?.Report(100);
                return true;
            }
            catch (OperationCanceledException)
            {
                summary = "Cancelled";
                _logger.Info("Catalog sync: cancelled");
                TrySaveState();
                return true;
            }
            catch (Exception ex)
            {
                summary = "Failed: " + ex.Message;
                _logger.ErrorException("Catalog sync: failed", ex);
                TrySaveState();
                return true;
            }
            finally
            {
                _logger.Info("Catalog sync: end, {0}", summary);
                Finish(summary);
                _gate.Release();
            }
        }

        // ----- Movies -------------------------------------------------------------------------

        private async Task SyncMoviesAsync(PluginConfiguration config, PhaseCounts counts, IProgress<double> progress, CancellationToken ct)
        {
            SetPhase("Movies: finding the library", 0);
            string problem;
            var root = _library.FindRoot(CatalogLayout.MoviesPath(config), out problem);
            if (root == null)
            {
                counts.Problem = problem;
                _logger.Warn("Catalog sync: {0}", problem);
                return;
            }
            var folder = _library.EnsurePluginFolder(root, EmbyCatalog.MoviesFolderKey);

            SetPhase("Movies: listing", 0);
            var categories = await CategoriesAsync(config, true, ct).ConfigureAwait(false);
            var excludedCategories = ExcludedCategories(config, categories);
            List<VodStreamInfo> rows;
            try
            {
                rows = await _client.GetVodStreamsAsync(config, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                counts.Problem = "get_vod_streams failed, nothing changed: " + ex.Message;
                _logger.Warn("Catalog sync: {0}", counts.Problem);
                return;
            }
            var listOk = rows.Count > 0;
            if (!listOk) _logger.Warn("Catalog sync: get_vod_streams came back empty, nothing is removed");

            var selected = new HashSet<int>(config.SelectedVodCategoryIds ?? new int[0]);
            IncludeNewCategories(config, true, categories, excludedCategories, selected);
            var filtered = rows
                .Where(r => IsSelected(r.CategoryId, selected))
                .Where(r => !(r.CategoryId.HasValue && excludedCategories.Contains(r.CategoryId.Value)) && (config.IncludeAdultContent || !r.IsAdultContent))
                .GroupBy(r => r.StreamId).Select(g => g.First())
                .ToList();
            var entries = CatalogNaming.BuildMovieEntries(filtered, config.ContentRemoveTerms, config.MergeMovieVersions);
            var streamBase = CatalogState.StreamBase(config);
            counts.Listed = entries.Count;
            _logger.Info("Catalog sync: {0} VOD rows, {1} movies after filtering and merging", rows.Count, entries.Count);

            SetPhase("Movies: reading the library", 0);
            var index = _library.IndexChildren(folder, typeof(Movie));
            SetPhase("Movies: writing", entries.Count);

            var toCreate = new List<BaseItem>();
            var pending = new List<Tuple<string, MovieEntry, Movie>>();
            var listedIds = new HashSet<string>(StringComparer.Ordinal);
            var done = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var key = CatalogState.Key(entry.Primary.StreamId);
                var signature = CatalogState.MovieSignature(entry, streamBase);
                listedIds.Add(key);

                MovieState old;
                lock (_state.SyncRoot) _state.Movies.TryGetValue(key, out old);
                var unchanged = old != null && old.Signature == signature && index.ContainsKey(key);
                if (unchanged)
                {
                    counts.Unchanged++;
                }
                else
                {
                    try
                    {
                        pending.Add(Tuple.Create(key, entry, UpsertMovie(entry, config, folder, index, toCreate, counts)));
                    }
                    catch (Exception ex)
                    {
                        counts.Failed++;
                        _logger.Warn("Catalog sync: movie '{0}' not written: {1}", entry.Title, ex.Message);
                    }
                }

                done++;
                if ((done & 255) == 0 || done == entries.Count)
                {
                    SetProgress(done);
                    progress?.Report(entries.Count == 0 ? 90 : done * 90.0 / entries.Count);
                }
                if (toCreate.Count >= 2000)
                    FlushCreates(toCreate, folder, root, ct);
            }
            FlushCreates(toCreate, folder, root, ct);

            lock (_state.SyncRoot)
            {
                foreach (var p in pending)
                {
                    _state.Movies[p.Item1] = new MovieState { Signature = CatalogState.MovieSignature(p.Item2, streamBase), ItemId = p.Item3.InternalId };
                }
            }

            if (listOk)
            {
                SetPhase("Movies: removing", 0);
                var gone = index.Where(kv => !listedIds.Contains(kv.Key)).Select(kv => kv.Value).ToList();
                RemoveUnlisted(gone, index.Count, folder, config.RemovalSafetyThreshold, counts, "movies");
                lock (_state.SyncRoot)
                {
                    foreach (var key in _state.Movies.Keys.Where(k => !listedIds.Contains(k)).ToList())
                        _state.Movies.Remove(key);
                }
            }
        }

        /// <summary>Creates or updates the one item of a movie. New items are returned without InternalId until the batch is flushed.</summary>
        private Movie UpsertMovie(MovieEntry entry, PluginConfiguration config, Folder folder,
            Dictionary<string, BaseItem> index, List<BaseItem> toCreate, PhaseCounts counts)
        {
            var row = entry.Primary;
            var key = CatalogState.Key(row.StreamId);
            BaseItem existing;
            var movie = index.TryGetValue(key, out existing) ? existing as Movie : null;
            if (movie == null)
            {
                movie = _library.NewMovie(row.StreamId);
                EmbyCatalog.ApplyMovieRow(movie, entry, config, folder);
                toCreate.Add(movie);
                index[key] = movie;
                counts.Created++;
            }
            else
            {
                EmbyCatalog.ApplyMovieRow(movie, entry, config, folder);
                _library.UpdateItem(movie, folder);
                // The row changed: plot and cast are taken again at the next open.
                _state.UnmarkDetailed(row.StreamId);
                counts.Updated++;
            }
            return movie;
        }

        private void FlushCreates(List<BaseItem> toCreate, Folder folder, CatalogRoot root, CancellationToken ct)
        {
            if (toCreate.Count == 0) return;
            _library.CreateItems(toCreate, folder, root, ct);
            _logger.Debug("Catalog sync: {0} items created", toCreate.Count);
            toCreate.Clear();
        }

        // ----- Series -------------------------------------------------------------------------

        private async Task SyncSeriesAsync(PluginConfiguration config, PhaseCounts counts, IProgress<double> progress, CancellationToken ct)
        {
            SetPhase("Series: finding the library", 0);
            string problem;
            var root = _library.FindRoot(CatalogLayout.ShowsPath(config), out problem);
            if (root == null)
            {
                counts.Problem = problem;
                _logger.Warn("Catalog sync: {0}", problem);
                return;
            }
            var folder = _library.EnsurePluginFolder(root, EmbyCatalog.ShowsFolderKey);

            SetPhase("Series: listing", 0);
            var categories = await CategoriesAsync(config, false, ct).ConfigureAwait(false);
            var excludedCategories = ExcludedCategories(config, categories);
            List<SeriesInfo> rows;
            try
            {
                rows = await _client.GetSeriesAsync(config, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                counts.Problem = "get_series failed, nothing changed: " + ex.Message;
                _logger.Warn("Catalog sync: {0}", counts.Problem);
                return;
            }
            var listOk = rows.Count > 0;
            if (!listOk) _logger.Warn("Catalog sync: get_series came back empty, nothing is removed");

            var selected = new HashSet<int>(config.SelectedSeriesCategoryIds ?? new int[0]);
            IncludeNewCategories(config, false, categories, excludedCategories, selected);
            var entries = new List<SeriesEntry>();
            foreach (var row in rows
                .Where(r => IsSelected(r.CategoryId, selected))
                .Where(r => !(r.CategoryId.HasValue && excludedCategories.Contains(r.CategoryId.Value)) && (config.IncludeAdultContent || !r.IsAdultContent))
                .GroupBy(r => r.SeriesId).Select(g => g.First())
                .OrderBy(r => r.SeriesId))
            {
                var title = CatalogNaming.CleanTitle(row.Name, config.ContentRemoveTerms);
                var year = CatalogNaming.ExtractYear(ref title, row.EffectiveReleaseDate);
                title = CatalogNaming.SanitizeFileName(title);
                if (title.Length == 0) continue;
                entries.Add(new SeriesEntry { Row = row, Title = title, Year = year });
            }
            var streamBase = CatalogState.StreamBase(config);
            counts.Listed = entries.Count;
            _logger.Info("Catalog sync: {0} series rows, {1} series after filtering", rows.Count, entries.Count);

            SetPhase("Series: reading the library", 0);
            var index = _library.IndexChildren(folder, typeof(Series));
            SetPhase("Series: writing", entries.Count);

            var toCreate = new List<BaseItem>();
            var pending = new List<Tuple<SeriesEntry, Series>>();
            var listedIds = new HashSet<string>(StringComparer.Ordinal);
            var done = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var row = entry.Row;
                var key = CatalogState.Key(row.SeriesId);
                listedIds.Add(key);
                var signature = CatalogState.SeriesSignature(entry.Title, entry.Year, row.CategoryId, row.Cover, row.Plot, row.Cast, streamBase);

                SeriesState old;
                lock (_state.SyncRoot) _state.Series.TryGetValue(key, out old);
                BaseItem existing;
                index.TryGetValue(key, out existing);
                var series = existing as Series;
                var modifiedChanged = old == null || !string.Equals(old.LastModified ?? string.Empty, row.LastModified ?? string.Empty, StringComparison.Ordinal);

                if (series != null && old != null && old.Signature == signature && !modifiedChanged)
                {
                    counts.Unchanged++;
                }
                else
                {
                    try
                    {
                        if (series == null)
                        {
                            series = _library.NewSeries(row.SeriesId);
                            EmbyCatalog.ApplySeriesRow(series, row, entry.Title, entry.Year, folder);
                            toCreate.Add(series);
                            index[key] = series;
                            counts.Created++;
                        }
                        else if (old == null || old.Signature != signature)
                        {
                            EmbyCatalog.ApplySeriesRow(series, row, entry.Title, entry.Year, folder);
                            _library.UpdateItem(series, folder);
                            counts.Updated++;
                        }
                        else
                        {
                            // Only last_modified moved: the episodes are refetched at the next open.
                            counts.Updated++;
                        }
                        pending.Add(Tuple.Create(entry, series));
                    }
                    catch (Exception ex)
                    {
                        counts.Failed++;
                        _logger.Warn("Catalog sync: series '{0}' not written: {1}", entry.Title, ex.Message);
                    }
                }

                done++;
                if ((done & 255) == 0 || done == entries.Count)
                {
                    SetProgress(done);
                    progress?.Report(entries.Count == 0 ? 90 : done * 90.0 / entries.Count);
                }
                if (toCreate.Count >= 2000)
                    FlushCreates(toCreate, folder, root, ct);
            }
            FlushCreates(toCreate, folder, root, ct);

            lock (_state.SyncRoot)
            {
                foreach (var p in pending)
                {
                    var key = CatalogState.Key(p.Item1.Row.SeriesId);
                    SeriesState s;
                    if (!_state.Series.TryGetValue(key, out s))
                    {
                        s = new SeriesState();
                        _state.Series[key] = s;
                    }
                    s.LastModified = p.Item1.Row.LastModified;
                    s.Signature = CatalogState.SeriesSignature(p.Item1.Title, p.Item1.Year, p.Item1.Row.CategoryId, p.Item1.Row.Cover, p.Item1.Row.Plot, p.Item1.Row.Cast, streamBase);
                    s.ItemId = p.Item2.InternalId;
                    // Any change marks the series stale: the next open refetches episodes and cast in one call.
                    s.Stale = true;
                }
            }

            if (listOk)
            {
                SetPhase("Series: removing", 0);
                var gone = index.Where(kv => !listedIds.Contains(kv.Key)).Select(kv => kv.Value).ToList();
                RemoveUnlisted(gone, index.Count, folder, config.RemovalSafetyThreshold, counts, "series");
                lock (_state.SyncRoot)
                {
                    foreach (var key in _state.Series.Keys.Where(k => !listedIds.Contains(k)).ToList())
                        _state.Series.Remove(key);
                }
            }
        }

        private sealed class SeriesEntry
        {
            public SeriesInfo Row;
            public string Title;
            public int? Year;
        }

        // ----- Shared -------------------------------------------------------------------------

        /// <summary>The provider's category list of one kind, or null when the call failed.</summary>
        private async Task<List<Category>> CategoriesAsync(PluginConfiguration config, bool vod, CancellationToken ct)
        {
            try
            {
                return vod
                    ? await _client.GetVodCategoriesAsync(config, ct).ConfigureAwait(false)
                    : await _client.GetSeriesCategoriesAsync(config, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warn("Catalog sync: category list failed, adult categories are filtered by the row flag only, no category is excluded or auto-included: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Category ids left out: names that read as adult (unless adult content is on) and names that
        /// contain one of the user's excluded terms. Rows still carry is_adult when the list is missing.
        /// </summary>
        internal static HashSet<int> ExcludedCategories(PluginConfiguration config, IEnumerable<Category> categories)
        {
            var result = new HashSet<int>();
            if (categories == null) return result;
            foreach (var c in categories)
            {
                if ((!config.IncludeAdultContent && CatalogNaming.IsAdultCategoryName(c.CategoryName))
                    || CatalogNaming.IsExcludedCategoryName(c.CategoryName, config.ExcludedCategoryTerms))
                    result.Add(c.CategoryId);
            }
            return result;
        }

        /// <summary>
        /// Categories the provider added since the last sync, not excluded. None on the first run,
        /// which only learns the current list: an existing selection must not swallow every category.
        /// </summary>
        internal static List<Category> NewCategories(IEnumerable<Category> current, HashSet<int> known, HashSet<int> excluded)
        {
            if (known == null || known.Count == 0) return new List<Category>();
            return current.Where(c => !known.Contains(c.CategoryId) && !excluded.Contains(c.CategoryId))
                .GroupBy(c => c.CategoryId).Select(g => g.First()).ToList();
        }

        /// <summary>
        /// With a category selection in place, a category the provider added joins it (when the option is
        /// on) and the picker cache follows, so the config page shows it ticked. The known set is learned
        /// on the first run and kept up to date only while the option is on: switched on later, it picks
        /// up everything that appeared meanwhile.
        /// </summary>
        private void IncludeNewCategories(PluginConfiguration config, bool vod, List<Category> categories, HashSet<int> excluded, HashSet<int> selected)
        {
            if (categories == null) return;
            var known = vod ? _state.KnownVodCategoryIds : _state.KnownSeriesCategoryIds;
            var kind = vod ? "VOD" : "series";
            lock (_state.SyncRoot)
            {
                if (known.Count == 0)
                {
                    known.UnionWith(categories.Select(c => c.CategoryId));
                    _logger.Info("Catalog sync: {0} {1} categories learned as the baseline for auto-include", known.Count, kind);
                    return;
                }
                if (!config.AutoIncludeNewCategories) return;
                var added = NewCategories(categories, known, excluded);
                known.UnionWith(categories.Select(c => c.CategoryId));
                if (added.Count == 0 || selected.Count == 0) return;
                foreach (var c in added) selected.Add(c.CategoryId);
                _logger.Info("Catalog sync: {0} new {1} categories included: {2}", added.Count, kind, string.Join(", ", added.Select(c => c.CategoryName)));
            }
            try
            {
                var ids = selected.OrderBy(i => i).ToArray();
                var cache = Api.CatalogApi.SerializeCategoryCache(categories);
                if (vod) { config.SelectedVodCategoryIds = ids; config.CachedVodCategories = cache; }
                else { config.SelectedSeriesCategoryIds = ids; config.CachedSeriesCategories = cache; }
                _saveConfig?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.Warn("Catalog sync: new {0} categories used for this run but the selection could not be saved: {1}", kind, ex.Message);
            }
        }

        /// <summary>An empty selection means every category.</summary>
        internal static bool IsSelected(int? categoryId, HashSet<int> selected)
            => selected.Count == 0 || (categoryId.HasValue && selected.Contains(categoryId.Value));

        /// <summary>True when removing <paramref name="candidates"/> out of <paramref name="existing"/> items is within the safety threshold.</summary>
        internal static bool RemovalAllowed(int candidates, int existing, double threshold)
        {
            if (candidates == 0) return false;
            if (threshold <= 0 || existing < SafetyMinimumExisting) return true;
            return candidates / (double)existing <= threshold;
        }

        /// <summary>
        /// Deletes the items this run did not list. Held back as a whole when they exceed the
        /// threshold share of what exists: a half answer from the provider must not wipe a library.
        /// </summary>
        private void RemoveUnlisted(List<BaseItem> gone, int existing, Folder parent, double threshold, PhaseCounts counts, string kind)
        {
            if (gone.Count == 0) return;
            if (!RemovalAllowed(gone.Count, existing, threshold))
            {
                counts.SkippedBySafety = gone.Count;
                _logger.Warn("Catalog sync: {0} of {1} {2} would be removed ({3:P0}), above the safety threshold of {4:P0}; nothing removed",
                    gone.Count, existing, kind, gone.Count / (double)existing, threshold);
                return;
            }
            foreach (var item in gone)
            {
                try
                {
                    _library.DeleteItem(item, parent);
                    counts.Removed++;
                    _logger.Debug("Catalog sync: removed '{0}'", item.Name);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Catalog sync: could not remove '{0}': {1}", item.Name, ex.Message);
                }
            }
        }

        private static string Validate(PluginConfiguration config)
        {
            if (config == null) return "No configuration";
            if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
                return "Xtream connection not configured";
            if (string.IsNullOrWhiteSpace(config.LibraryPath))
                return "Library path not configured";
            if (!config.SyncMovies && !config.SyncSeries)
                return "Neither movies nor series are enabled";
            return null;
        }

        private void TrySaveState()
        {
            try { _state.Save(); }
            catch (Exception ex) { _logger.Warn("Catalog sync: state not saved: {0}", ex.Message); }
        }

        private static IProgress<double> Scaled(IProgress<double> inner, int index, int phases)
        {
            if (inner == null || phases <= 0) return null;
            var span = 100.0 / phases;
            return new Progress<double>(v => inner.Report(index * span + v * span / 100.0));
        }

        // ----- Status -------------------------------------------------------------------------

        private void SetRunning(bool running, string phase, int total)
        {
            lock (_statusSync)
            {
                SeedStatusFromConfig();
                _running = running;
                _phase = phase;
                _processed = 0;
                _total = total;
            }
        }

        private void SetPhase(string phase, int total)
        {
            lock (_statusSync)
            {
                _phase = phase;
                _processed = 0;
                _total = total;
            }
        }

        private void SetProgress(int processed)
        {
            lock (_statusSync) _processed = processed;
        }

        private void Finish(string summary)
        {
            var now = DateTime.UtcNow;
            lock (_statusSync)
            {
                _running = false;
                _phase = "Idle";
                _lastRunUtc = now;
                _lastSummary = summary;
            }
            try
            {
                var config = _config();
                if (config != null)
                {
                    config.LastCatalogSyncTimestamp = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    config.LastCatalogSyncSummary = summary;
                    _saveConfig?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("Catalog sync: could not save the last run summary: {0}", ex.Message);
            }
        }

        /// <summary>Before the first run after a restart, the last result comes from the saved configuration.</summary>
        private void SeedStatusFromConfig()
        {
            if (_statusSeeded) return;
            _statusSeeded = true;
            try
            {
                var config = _config();
                if (config == null) return;
                if (config.LastCatalogSyncTimestamp > 0)
                    _lastRunUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(config.LastCatalogSyncTimestamp);
                _lastSummary = config.LastCatalogSyncSummary;
            }
            catch (Exception)
            {
                // No configuration yet (unit tests): status starts empty.
            }
        }
    }

    /// <summary>The two library locations under the configured path.</summary>
    internal static class CatalogLayout
    {
        public const string MoviesFolder = "Movies";
        public const string ShowsFolder = "Shows";

        public static string MoviesPath(PluginConfiguration config) => System.IO.Path.Combine(config.LibraryPath ?? string.Empty, MoviesFolder);

        public static string ShowsPath(PluginConfiguration config) => System.IO.Path.Combine(config.LibraryPath ?? string.Empty, ShowsFolder);
    }
}
