using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;

namespace Jellyfin.Plugin.XtreamIptv.Catalog
{
    /// <summary>The two places the plugin writes into: the physical library folder item and its collection.</summary>
    public sealed class CatalogRoot
    {
        /// <summary>The Jellyfin item of the library location on disk (its children are what a scan validates).</summary>
        public Folder Parent { get; set; }
        public CollectionFolder Collection { get; set; }
        /// <summary>The plugin's own folder under <see cref="Parent"/>, Path empty, holding every item.</summary>
        public Folder PluginFolder { get; set; }
    }

    /// <summary>
    /// Everything the catalogue does to Jellyfin's library database: finding the libraries, creating,
    /// updating and deleting items, people and remote images. Items carry the provider id under
    /// <c>ProviderIds["xtream"]</c>, which is how the plugin finds them again. Same roles as EmbyCatalog.
    /// </summary>
    public sealed class JellyfinCatalog
    {
        public const string ProviderKey = "xtream";
        /// <summary>The other releases of a movie, read by the media source provider (see CatalogNaming.VersionsString).</summary>
        public const string VersionsKey = "xtream-versions";
        public const string PluginFolderName = "Xtream";
        public const string MoviesFolderKey = "xtream-movies";
        public const string ShowsFolderKey = "xtream-shows";
        public const string MoviesLibraryName = "Film";
        public const string ShowsLibraryName = "Serie";
        private const int CreateBatchSize = 500;
        private const int MaxActors = 15;

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<JellyfinCatalog> _logger;
        private readonly ILocalizationManager _localization;

        public JellyfinCatalog(ILibraryManager libraryManager, ILogger<JellyfinCatalog> logger, ILocalizationManager localization = null)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _localization = localization;
        }

        public ILibraryManager LibraryManager => _libraryManager;

        // ----- Libraries ------------------------------------------------------------------------

        /// <summary>
        /// Finds the Jellyfin library whose location is <paramref name="diskPath"/>. Null with a message
        /// when no library uses that folder: Jellyfin only shows items that hang under a library.
        /// </summary>
        public CatalogRoot FindRoot(string diskPath, out string problem)
        {
            problem = null;
            try { CatalogLayout.EnsureFolder(diskPath); }
            catch (Exception ex) { _logger.LogDebug("Library lookup: placeholder for '{Path}' not written: {Message}", diskPath, ex.Message); }
            var wanted = Normalize(diskPath);
            foreach (var collection in CollectionFolders())
            {
                Folder[] physical;
                try { physical = collection.GetPhysicalFolders().ToArray(); }
                catch (Exception ex)
                {
                    _logger.LogDebug("Library lookup: physical folders of '{Name}' unavailable: {Message}", collection.Name, ex.Message);
                    physical = Array.Empty<Folder>();
                }
                var parent = physical.FirstOrDefault(f => Normalize(f.Path) == wanted);
                if (parent == null && Normalize(collection.Path) == wanted) parent = collection;
                if (parent != null)
                    return new CatalogRoot { Parent = parent, Collection = collection };
            }
            problem = string.Format(CultureInfo.InvariantCulture,
                "No Jellyfin library uses the folder '{0}'. Add a library with that folder as its location (or use Create libraries).", diskPath);
            return null;
        }

        private IEnumerable<CollectionFolder> CollectionFolders()
        {
            var found = new List<CollectionFolder>();
            try
            {
                found.AddRange(_libraryManager.GetUserRootFolder().Children.OfType<CollectionFolder>());
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Library lookup: root children failed: {Message}", ex.Message);
            }
            if (found.Count == 0)
            {
                try
                {
                    found.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { BaseItemKind.CollectionFolder },
                        EnableTotalRecordCount = false,
                    }).OfType<CollectionFolder>());
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Library lookup: collection query failed: {Message}", ex.Message);
                }
            }
            return found;
        }

        /// <summary>
        /// Creates the two disk folders and the "Film" (movies) and "Serie" (tvshows) libraries with no
        /// fetchers, monitoring or extraction, when they do not exist yet. Returns one line per library.
        /// </summary>
        public async Task<List<string>> CreateLibrariesAsync(string libraryPath)
        {
            var lines = new List<string>();
            var created = false;
            var config = new PluginConfiguration { LibraryPath = libraryPath };
            var wanted = new[]
            {
                Tuple.Create(MoviesLibraryName, CollectionTypeOptions.movies, CatalogLayout.MoviesPath(config)),
                Tuple.Create(ShowsLibraryName, CollectionTypeOptions.tvshows, CatalogLayout.ShowsPath(config)),
            };
            foreach (var w in wanted)
            {
                var name = w.Item1;
                var path = w.Item3;
                CatalogLayout.EnsureFolder(path);
                string problem;
                if (FindRoot(path, out problem) != null)
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "'{0}' already uses {1}.", LibraryNameFor(path) ?? name, path));
                    continue;
                }
                var taken = _libraryManager.GetVirtualFolders().FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (taken != null)
                {
                    if ((taken.Locations ?? Array.Empty<string>()).Any(l => Normalize(l) == Normalize(path)))
                    {
                        // The library exists but its folder item does not yet (empty folder at scan time): validating the roots creates it.
                        created = true;
                        lines.Add(string.Format(CultureInfo.InvariantCulture, "'{0}' uses {1}; its folder item is being created.", name, path));
                        continue;
                    }
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "A library named '{0}' exists with another location; add {1} to it or rename it.", name, path));
                    continue;
                }
                await _libraryManager.AddVirtualFolder(name, w.Item2, BuildLibraryOptions(path), false).ConfigureAwait(false);
                created = true;
                lines.Add(string.Format(CultureInfo.InvariantCulture, "Library '{0}' created for {1}.", name, path));
                _logger.LogInformation("Catalog: library '{Name}' created for {Path}", name, path);
            }
            // The folder items of a new library only exist after the root folders are validated; done here so a sync can follow at once.
            if (created)
            {
                try { await _libraryManager.ValidateTopLibraryFolders(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning("Catalog: root validation after library creation failed: {Message}", ex.Message); }
            }
            return lines;
        }

        private string LibraryNameFor(string path)
        {
            var wanted = Normalize(path);
            return _libraryManager.GetVirtualFolders()
                .FirstOrDefault(v => (v.Locations ?? Array.Empty<string>()).Any(l => Normalize(l) == wanted))?.Name;
        }

        /// <summary>Library options for a catalogue library: nothing fetched, nothing extracted, nothing watched; the plugin is the only writer.</summary>
        internal static LibraryOptions BuildLibraryOptions(string path)
        {
            var types = new[] { "Movie", "Series", "Season", "Episode" };
            return new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo { Path = path } },
                EnableRealtimeMonitor = false,
                AutomaticRefreshIntervalDays = 0,
                SaveLocalMetadata = false,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                EnableLUFSScan = false,
                EnableAutomaticSeriesGrouping = false,
                EnableEmbeddedTitles = false,
                EnableEmbeddedEpisodeInfos = false,
                MetadataSavers = Array.Empty<string>(),
                TypeOptions = types.Select(t => new TypeOptions
                {
                    Type = t,
                    MetadataFetchers = Array.Empty<string>(),
                    MetadataFetcherOrder = Array.Empty<string>(),
                    ImageFetchers = Array.Empty<string>(),
                    ImageFetcherOrder = Array.Empty<string>(),
                    ImageOptions = Array.Empty<ImageOption>(),
                }).ToArray(),
            };
        }

        /// <summary>The plugin folder under the library location, created on first use. Its Path is empty so a library scan leaves it alone.</summary>
        public Folder EnsurePluginFolder(CatalogRoot root, string key)
        {
            var existing = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = root.Parent.Id,
                IncludeItemTypes = new[] { BaseItemKind.Folder },
                DtoOptions = new DtoOptions(true),
                EnableTotalRecordCount = false,
            }).OfType<Folder>().FirstOrDefault(f => string.Equals(f.GetProviderId(ProviderKey), key, StringComparison.Ordinal));
            if (existing != null)
            {
                root.PluginFolder = existing;
                return existing;
            }

            var folder = new Folder
            {
                Id = _libraryManager.GetNewItemId(key, typeof(Folder)),
                Name = PluginFolderName,
                Path = null,
                ExternalId = key,
                IsVirtualItem = false,
                DateCreated = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
            };
            folder.SetProviderId(ProviderKey, key);
            folder.PresentationUniqueKey = folder.Id.ToString("N");
            folder.SetParent(root.Parent);
            _libraryManager.CreateItem(folder, root.Parent);
            _logger.LogInformation("Catalog: created the '{Folder}' folder under '{Path}'", PluginFolderName, root.Parent.Path);
            root.PluginFolder = folder;
            return folder;
        }

        // ----- Queries --------------------------------------------------------------------------

        /// <summary>Direct children of a folder of one type, keyed by their xtream provider id.</summary>
        public Dictionary<string, BaseItem> IndexChildren(BaseItem parent, BaseItemKind kind)
        {
            var result = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
            foreach (var item in Children(parent, kind, false))
            {
                var id = item.GetProviderId(ProviderKey);
                if (!string.IsNullOrEmpty(id)) result[id] = item;
            }
            return result;
        }

        public IReadOnlyList<BaseItem> Children(BaseItem parent, BaseItemKind kind, bool recursive)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                DtoOptions = new DtoOptions(true),
                EnableTotalRecordCount = false,
            };
            if (recursive) query.AncestorIds = new[] { parent.Id };
            else query.ParentId = parent.Id;
            return _libraryManager.GetItemList(query);
        }

        public int Count(BaseItem parent, BaseItemKind kind, bool recursive)
        {
            if (parent == null) return 0;
            var query = new InternalItemsQuery { IncludeItemTypes = new[] { kind }, EnableTotalRecordCount = false };
            if (recursive) query.AncestorIds = new[] { parent.Id };
            else query.ParentId = parent.Id;
            return _libraryManager.GetCount(query);
        }

        public BaseItem GetItem(Guid id) => id.Equals(Guid.Empty) ? null : _libraryManager.GetItemById(id);

        // ----- Persistence ----------------------------------------------------------------------

        /// <summary>Inserts new items in batches.</summary>
        public void CreateItems(List<BaseItem> items, BaseItem parent, CancellationToken ct)
        {
            for (int i = 0; i < items.Count; i += CreateBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = items.GetRange(i, Math.Min(CreateBatchSize, items.Count - i));
                _libraryManager.CreateItems(batch, parent, ct);
            }
        }

        public void CreateItem(BaseItem item, BaseItem parent)
            => _libraryManager.CreateItem(item, parent);

        public Task UpdateItemAsync(BaseItem item, BaseItem parent, CancellationToken ct)
            => _libraryManager.UpdateItemAsync(item, parent ?? item.GetParent(), ItemUpdateType.MetadataDownload, ct);

        public void DeleteItem(BaseItem item, BaseItem parent)
            => _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, parent, false);

        /// <summary>Replaces the item's people (the sync writes none; detail on open is the only writer).</summary>
        public Task SetPeopleAsync(BaseItem item, string cast, string director, CancellationToken ct)
            => _libraryManager.UpdatePeopleAsync(item, People(cast, director), ct);

        public static List<PersonInfo> People(string cast, string director)
        {
            var people = new List<PersonInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SplitList(director))
                if (seen.Add(name)) people.Add(new PersonInfo { Name = name, Type = PersonKind.Director });
            foreach (var name in SplitList(cast).Take(MaxActors))
                if (seen.Add(name)) people.Add(new PersonInfo { Name = name, Type = PersonKind.Actor });
            return people;
        }

        // ----- Item builders --------------------------------------------------------------------

        public Movie NewMovie(int streamId)
            => new Movie { Id = _libraryManager.GetNewItemId("xtream-movie-" + CatalogState.Key(streamId), typeof(Movie)) };

        public Series NewSeries(int seriesId)
            => new Series { Id = _libraryManager.GetNewItemId("xtream-series-" + CatalogState.Key(seriesId), typeof(Series)) };

        public Season NewSeason(int seriesId, int number)
            => new Season { Id = _libraryManager.GetNewItemId("xtream-season-" + CatalogState.Key(seriesId) + "-" + CatalogState.Key(number), typeof(Season)) };

        public Episode NewEpisode(int episodeId)
            => new Episode { Id = _libraryManager.GetNewItemId("xtream-episode-" + CatalogState.Key(episodeId), typeof(Episode)) };

        /// <summary>Fills a movie item from its primary list row. One item per movie: the other releases become media sources.</summary>
        internal static void ApplyMovieRow(Movie item, MovieEntry entry, PluginConfiguration config, BaseItem parent)
        {
            var row = entry.Primary;
            item.Name = entry.Title;
            item.Path = StreamUrl(config, "movie", row.StreamId, row.ContainerExtension);
            item.Container = Extension(row.ContainerExtension);
            item.IsVirtualItem = false;
            item.Overview = NullIfEmpty(row.Plot);
            item.Genres = SplitList(row.Genre).ToArray();
            item.CommunityRating = Rating(row.Rating);
            item.ProductionYear = entry.Year;
            item.PremiereDate = Date(row.ReleaseDate);
            item.DateCreated = row.Added > 0 ? DateTimeOffset.FromUnixTimeSeconds(row.Added).UtcDateTime : DateTime.UtcNow;
            item.DateModified = DateTime.UtcNow;
            item.PresentationUniqueKey = item.Id.ToString("N");
            item.SetProviderId(ProviderKey, CatalogState.Key(row.StreamId));
            // The other releases ride on the item; the media source provider turns them into versions at play time.
            var versions = CatalogNaming.VersionsString(entry);
            if (versions.Length > 0) item.SetProviderId(VersionsKey, versions);
            else item.ProviderIds?.Remove(VersionsKey);
            SetTmdb(item, row.TmdbId);
            SetRemoteImage(item, ImageType.Primary, row.StreamIcon);
            SetRemoteImage(item, ImageType.Backdrop, row.BackdropPath.FirstOrDefault());
            item.SetParent((Folder)parent);
        }

        /// <summary>Overlays get_vod_info onto a movie item. Returns the people to store.</summary>
        public static List<PersonInfo> ApplyVodDetail(Movie item, VodInfoDetail d)
        {
            if (!string.IsNullOrWhiteSpace(d.EffectivePlot)) item.Overview = d.EffectivePlot.Trim();
            var genres = SplitList(d.Genre).ToArray();
            if (genres.Length > 0) item.Genres = genres;
            var rating = Rating(d.Rating);
            if (rating.HasValue) item.CommunityRating = rating;
            var date = Date(d.EffectiveReleaseDate);
            if (date.HasValue)
            {
                item.PremiereDate = date;
                if (!item.ProductionYear.HasValue) item.ProductionYear = date.Value.Year;
            }
            if (d.DurationSecs.HasValue && d.DurationSecs.Value > 0)
                item.RunTimeTicks = TimeSpan.FromSeconds(d.DurationSecs.Value).Ticks;
            else
            {
                var minutes = CatalogNaming.RuntimeMinutes(null, d.Duration);
                if (minutes.HasValue) item.RunTimeTicks = TimeSpan.FromMinutes(minutes.Value).Ticks;
            }
            var countries = SplitList(d.Country).ToArray();
            if (countries.Length > 0) item.ProductionLocations = countries;
            SetTmdb(item, d.TmdbId);
            var backdrop = d.BackdropPath.FirstOrDefault();
            if (!string.IsNullOrEmpty(backdrop)) SetRemoteImage(item, ImageType.Backdrop, backdrop);
            if (!item.HasImage(ImageType.Primary, 0)) SetRemoteImage(item, ImageType.Primary, d.EffectivePoster);
            item.DateModified = DateTime.UtcNow;
            return People(d.EffectiveCast, d.Director);
        }

        /// <summary>Fills a series item from a get_series row (or the info object of get_series_info).</summary>
        public static void ApplySeriesRow(Series item, SeriesInfo row, string title, int? year, BaseItem parent)
        {
            item.Name = title;
            item.Path = null;
            item.ExternalId = "xtream-series-" + CatalogState.Key(row.SeriesId);
            item.IsVirtualItem = false;
            item.Overview = NullIfEmpty(row.Plot);
            item.Genres = SplitList(row.Genre).ToArray();
            item.CommunityRating = Rating(row.Rating);
            item.ProductionYear = year;
            item.PremiereDate = Date(row.EffectiveReleaseDate);
            long modified;
            item.DateCreated = long.TryParse(row.LastModified, NumberStyles.Integer, CultureInfo.InvariantCulture, out modified) && modified > 0
                ? DateTimeOffset.FromUnixTimeSeconds(modified).UtcDateTime
                : DateTime.UtcNow;
            item.DateModified = DateTime.UtcNow;
            if (string.IsNullOrEmpty(item.PresentationUniqueKey)) item.PresentationUniqueKey = item.Id.ToString("N");
            item.SetProviderId(ProviderKey, CatalogState.Key(row.SeriesId));
            SetTmdb(item, row.EffectiveTmdbId);
            SetRemoteImage(item, ImageType.Primary, row.Cover);
            SetRemoteImage(item, ImageType.Backdrop, row.BackdropPath.FirstOrDefault());
            item.SetParent((Folder)parent);
        }

        /// <summary>
        /// The season name the way Jellyfin's own season resolver builds it ("NameSeasonNumber" localized
        /// to the series' metadata language, the library's "season zero" name for specials), so every
        /// client shows the same wording as seasons created by Jellyfin itself.
        /// </summary>
        public string SeasonName(Series series, int number)
        {
            var fallback = number == 0 ? "Specials" : string.Format(CultureInfo.InvariantCulture, "Season {0}", number);
            try
            {
                if (number == 0)
                {
                    var zero = _libraryManager.GetLibraryOptions(series)?.SeasonZeroDisplayName;
                    return string.IsNullOrWhiteSpace(zero) ? fallback : zero;
                }
                if (_localization == null) return fallback;
                var language = series.GetPreferredMetadataLanguage();
                var text = string.Format(CultureInfo.InvariantCulture, _localization.GetLocalizedString("NameSeasonNumber", language), number.ToString(CultureInfo.InvariantCulture));
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Season name: localization failed, English used: {Message}", ex.Message);
                return fallback;
            }
        }

        public static void ApplySeason(Season item, Series series, int number, string name, string cover)
        {
            item.Name = name;
            item.IndexNumber = number;
            item.Path = null;
            item.ExternalId = "xtream-season-" + series.GetProviderId(ProviderKey) + "-" + CatalogState.Key(number);
            item.IsVirtualItem = false;
            item.DateCreated = series.DateCreated;
            item.DateModified = DateTime.UtcNow;
            if (string.IsNullOrEmpty(item.PresentationUniqueKey)) item.PresentationUniqueKey = item.Id.ToString("N");
            item.SetProviderId(ProviderKey, item.ExternalId);
            item.SeriesId = series.Id;
            item.SeriesName = series.Name;
            item.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
            SetRemoteImage(item, ImageType.Primary, cover);
            item.SetParent(series);
        }

        public static void ApplyEpisode(Episode item, Series series, Season season, EpisodeInfo ep, int seasonNumber, int episodeNumber, string title, PluginConfiguration config)
        {
            item.Name = title;
            item.IndexNumber = episodeNumber;
            item.ParentIndexNumber = seasonNumber;
            item.Path = StreamUrl(config, "series", ep.Id, ep.ContainerExtension);
            item.Container = Extension(ep.ContainerExtension);
            item.IsVirtualItem = false;
            item.Overview = NullIfEmpty(ep.EffectivePlot);
            item.CommunityRating = Rating(ep.EffectiveRating);
            var aired = Date(ep.AirDate);
            item.PremiereDate = aired;
            item.ProductionYear = aired.HasValue ? aired.Value.Year : series.ProductionYear;
            if (ep.DurationSecs.HasValue && ep.DurationSecs.Value > 0)
                item.RunTimeTicks = TimeSpan.FromSeconds(ep.DurationSecs.Value).Ticks;
            else
            {
                var minutes = CatalogNaming.RuntimeMinutes(null, ep.EffectiveDuration);
                item.RunTimeTicks = minutes.HasValue ? TimeSpan.FromMinutes(minutes.Value).Ticks : (long?)null;
            }
            item.DateCreated = series.DateCreated;
            item.DateModified = DateTime.UtcNow;
            item.SetProviderId(ProviderKey, CatalogState.Key(ep.Id));
            SetTmdb(item, ep.TmdbId);
            item.SeriesId = series.Id;
            item.SeriesName = series.Name;
            item.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
            item.SeasonId = season.Id;
            item.SeasonName = season.Name;
            if (string.IsNullOrEmpty(item.PresentationUniqueKey)) item.PresentationUniqueKey = item.Id.ToString("N");
            SetRemoteImage(item, ImageType.Primary, ep.Image);
            item.SetParent(season);
        }

        /// <summary>A remote URL as the image path: Jellyfin downloads it the first time a client asks for it.</summary>
        public static void SetRemoteImage(BaseItem item, ImageType type, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var u = url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            var current = item.GetImages(type).FirstOrDefault();
            if (current != null && string.Equals(current.Path, u, StringComparison.Ordinal)) return;
            item.SetImage(new ItemImageInfo { Path = u, Type = type, DateModified = DateTime.UtcNow }, 0);
        }

        // ----- Helpers --------------------------------------------------------------------------

        public static string StreamUrl(PluginConfiguration config, string kind, int id, string extension)
            => XtreamUrls.Stream(config, kind, id, Extension(extension));

        public static string Extension(string extension)
            => string.IsNullOrWhiteSpace(extension) ? "mp4" : extension.Trim().TrimStart('.');

        public static float? Rating(string value)
        {
            double r;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out r) && r > 0 && r <= 10)
                return (float)r;
            return null;
        }

        public static DateTime? Date(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            DateTime d;
            if (DateTime.TryParseExact(v, new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "dd/MM/yyyy", "yyyy-MM-dd HH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
                return DateTime.SpecifyKind(d, DateTimeKind.Utc);
            int year;
            if (v.Length == 4 && CatalogNaming.TryParseYear(v, out year))
                return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
                return DateTime.SpecifyKind(d, DateTimeKind.Utc);
            return null;
        }

        public static IEnumerable<string> SplitList(string value)
            => string.IsNullOrWhiteSpace(value)
                ? Enumerable.Empty<string>()
                : value.Split(new[] { ',', '/', '|', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

        private static void SetTmdb(BaseItem item, string tmdbId)
        {
            if (string.IsNullOrWhiteSpace(tmdbId)) return;
            var id = tmdbId.Trim();
            if (id.All(char.IsDigit) && id != "0") item.SetProviderId(MetadataProvider.Tmdb, id);
        }

        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var p = path.Trim().Replace('\\', '/');
            while (p.Length > 1 && p.EndsWith("/", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            return p.ToLowerInvariant();
        }

        private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The two library locations under the configured path.</summary>
    internal static class CatalogLayout
    {
        public const string MoviesFolder = "Movies";
        public const string ShowsFolder = "Shows";

        public static string MoviesPath(PluginConfiguration config) => Path.Combine(config.LibraryPath ?? string.Empty, MoviesFolder);

        public static string ShowsPath(PluginConfiguration config) => Path.Combine(config.LibraryPath ?? string.Empty, ShowsFolder);

        /// <summary>Name of the placeholder file kept in each library folder.</summary>
        public const string PlaceholderFile = "xtream-catalogue.txt";

        /// <summary>
        /// Creates the folder with a small text file in it. Jellyfin skips a library folder that is
        /// empty ("inaccessible or empty") and never creates its folder item, which the catalogue hangs
        /// everything under; a non-media file keeps the folder "accessible" and is ignored by the resolvers.
        /// </summary>
        public static void EnsureFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            var marker = Path.Combine(path, PlaceholderFile);
            if (!File.Exists(marker))
                File.WriteAllText(marker, "This folder belongs to the Xtream IPTV plugin. It stays empty: the catalogue lives in Jellyfin's database.\n");
        }
    }
}
