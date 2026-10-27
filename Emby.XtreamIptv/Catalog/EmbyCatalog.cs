using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Logging;

namespace Emby.XtreamIptv.Catalog
{
    /// <summary>The two places the plugin writes into: the physical library folder item and its collection.</summary>
    internal sealed class CatalogRoot
    {
        /// <summary>The Emby item of the library location on disk (its children are what a scan validates).</summary>
        public Folder Parent { get; set; }
        public CollectionFolder Collection { get; set; }
        /// <summary>The plugin's own folder under <see cref="Parent"/>, Path empty, holding every item.</summary>
        public Folder PluginFolder { get; set; }

        public BaseItem[] CollectionFolders => new BaseItem[] { Collection };
    }

    /// <summary>
    /// Everything the catalogue does to Emby's library database: finding the libraries, creating,
    /// updating and deleting items, people and remote images. Items carry the provider id under
    /// <c>ProviderIds["xtream"]</c>, which is how the plugin finds them again.
    /// </summary>
    internal sealed class EmbyCatalog
    {
        public const string ProviderKey = "xtream";
        /// <summary>The other releases of a movie, read by the media source provider (see CatalogNaming.VersionsString).</summary>
        public const string VersionsKey = "xtream-versions";
        public const string PluginFolderName = "Xtream";
        public const string MoviesFolderKey = "xtream-movies";
        public const string ShowsFolderKey = "xtream-shows";
        private const int CreateBatchSize = 500;
        private const int MaxActors = 15;

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly ILocalizationManager _localization;

        public EmbyCatalog(ILibraryManager libraryManager, ILogger logger, ILocalizationManager localization = null)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _localization = localization;
        }

        public ILibraryManager LibraryManager => _libraryManager;

        // ----- Libraries ------------------------------------------------------------------------

        /// <summary>
        /// Finds the Emby library whose location is <paramref name="diskPath"/>. Null with a message
        /// when no library uses that folder: Emby only shows items that hang under a library.
        /// </summary>
        public CatalogRoot FindRoot(string diskPath, out string problem)
        {
            problem = null;
            var wanted = Normalize(diskPath);
            foreach (var collection in CollectionFolders())
            {
                BaseItem[] physical;
                try { physical = collection.GetPhysicalFolderItems(new InternalItemsQuery()); }
                catch (Exception ex)
                {
                    _logger.Debug("Library lookup: physical folders of '{0}' unavailable: {1}", collection.Name, ex.Message);
                    physical = new BaseItem[0];
                }
                var parent = physical.OfType<Folder>().FirstOrDefault(f => Normalize(f.Path) == wanted);
                if (parent == null && Normalize(collection.Path) == wanted) parent = collection;
                if (parent != null)
                    return new CatalogRoot { Parent = parent, Collection = collection };
            }
            problem = string.Format(CultureInfo.InvariantCulture,
                "No Emby library uses the folder '{0}'. Add a library with that folder as its location.", diskPath);
            return null;
        }

        private IEnumerable<CollectionFolder> CollectionFolders()
        {
            var found = new List<CollectionFolder>();
            try
            {
                found.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { typeof(CollectionFolder).Name },
                    EnableTotalRecordCount = false,
                }).OfType<CollectionFolder>());
            }
            catch (Exception ex)
            {
                _logger.Debug("Library lookup: collection query failed: {0}", ex.Message);
            }
            if (found.Count == 0)
            {
                try
                {
                    found.AddRange(_libraryManager.GetUserRootFolder().GetChildren(new InternalItemsQuery()).OfType<CollectionFolder>());
                }
                catch (Exception ex)
                {
                    _logger.Debug("Library lookup: root children failed: {0}", ex.Message);
                }
            }
            return found;
        }

        /// <summary>The plugin folder under the library location, created on first use. Its Path is empty so a library scan leaves it alone.</summary>
        public Folder EnsurePluginFolder(CatalogRoot root, string key)
        {
            var existing = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentIds = new[] { root.Parent.InternalId },
                IncludeItemTypes = new[] { typeof(Folder).Name },
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
                DateCreated = DateTimeOffset.UtcNow,
                DateModified = DateTimeOffset.UtcNow,
            };
            folder.SetProviderId(ProviderKey, key);
            folder.PresentationUniqueKey = folder.Id.ToString("N");
            folder.SetParent(root.Parent);
            _libraryManager.CreateItem(folder, root.Parent, root.CollectionFolders);
            _logger.Info("Catalog: created the '{0}' folder under '{1}'", PluginFolderName, root.Parent.Path);
            root.PluginFolder = folder;
            return folder;
        }

        // ----- Queries --------------------------------------------------------------------------

        /// <summary>Direct children of a folder of one type, keyed by their xtream provider id.</summary>
        public Dictionary<string, BaseItem> IndexChildren(BaseItem parent, Type itemType)
        {
            var result = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
            foreach (var item in Children(parent, itemType, false))
            {
                var id = item.GetProviderId(ProviderKey);
                if (!string.IsNullOrEmpty(id)) result[id] = item;
            }
            return result;
        }

        public BaseItem[] Children(BaseItem parent, Type itemType, bool recursive)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { itemType.Name },
                DtoOptions = new DtoOptions(true),
                EnableTotalRecordCount = false,
            };
            if (recursive) query.AncestorIds = new[] { parent.InternalId };
            else query.ParentIds = new[] { parent.InternalId };
            return _libraryManager.GetItemList(query);
        }

        public int Count(BaseItem parent, Type itemType, bool recursive)
        {
            if (parent == null) return 0;
            var query = new InternalItemsQuery { IncludeItemTypes = new[] { itemType.Name }, EnableTotalRecordCount = false };
            if (recursive) query.AncestorIds = new[] { parent.InternalId };
            else query.ParentIds = new[] { parent.InternalId };
            return _libraryManager.GetInternalItemIds(query).Length;
        }

        public BaseItem GetItem(long internalId) => _libraryManager.GetItemById(internalId);

        // ----- Persistence ----------------------------------------------------------------------

        /// <summary>Inserts new items in batches; after this every item has its InternalId.</summary>
        public void CreateItems(List<BaseItem> items, BaseItem parent, CatalogRoot root, CancellationToken ct)
        {
            for (int i = 0; i < items.Count; i += CreateBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = items.GetRange(i, Math.Min(CreateBatchSize, items.Count - i));
                _libraryManager.CreateItems(batch, parent, null, root.CollectionFolders, true, ct);
            }
        }

        public void CreateItem(BaseItem item, BaseItem parent, CatalogRoot root)
            => _libraryManager.CreateItem(item, parent, root.CollectionFolders);

        public void UpdateItem(BaseItem item, BaseItem parent)
            => _libraryManager.UpdateItem(item, parent, ItemUpdateType.MetadataDownload);

        public void DeleteItem(BaseItem item, BaseItem parent)
            => _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, parent, false);

        public void SetPeople(BaseItem item, string cast, string director, bool isNew)
        {
            var people = People(cast, director);
            if (people.Count == 0 && isNew) return;
            _libraryManager.UpdatePeople(item, people, isNew);
        }

        public static List<PersonInfo> People(string cast, string director)
        {
            var people = new List<PersonInfo>();
            foreach (var name in SplitList(director))
                people.Add(new PersonInfo { Name = name, Type = PersonType.Director });
            foreach (var name in SplitList(cast).Take(MaxActors))
                people.Add(new PersonInfo { Name = name, Type = PersonType.Actor });
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

        /// <summary>Shared by every release of one movie: Emby shows items with the same key as one entry with a version picker.</summary>
        public static string MoviePresentationKey(int primaryStreamId) => "xtream-movie-" + CatalogState.Key(primaryStreamId) + "_";

        /// <summary>Fills a movie item from its primary list row. One item per movie: the other releases become media sources.</summary>
        public static void ApplyMovieRow(Movie item, MovieEntry entry, PluginConfiguration config, BaseItem parent)
        {
            var row = entry.Primary;
            item.Name = entry.Title;
            item.Path = StreamUrl(config, "movie", row.StreamId, row.ContainerExtension);
            item.Container = Extension(row.ContainerExtension);
            item.IsVirtualItem = false;
            item.Overview = NullIfEmpty(row.Plot);
            item.SetGenres(SplitList(row.Genre));
            item.CommunityRating = Rating(row.Rating);
            item.ProductionYear = entry.Year;
            item.PremiereDate = Date(row.ReleaseDate);
            item.DateCreated = row.Added > 0 ? DateTimeOffset.FromUnixTimeSeconds(row.Added) : DateTimeOffset.UtcNow;
            item.DateModified = DateTimeOffset.UtcNow;
            item.PresentationUniqueKey = MoviePresentationKey(row.StreamId);
            item.SetProviderId(ProviderKey, CatalogState.Key(row.StreamId));
            // The other releases ride on the item; the media source provider turns them into versions at play time.
            var versions = CatalogNaming.VersionsString(entry);
            item.SetProviderId(VersionsKey, versions.Length > 0 ? versions : null);
            SetTmdb(item, row.TmdbId);
            SetRemoteImage(item, ImageType.Primary, row.StreamIcon);
            SetRemoteImage(item, ImageType.Backdrop, row.BackdropPath.FirstOrDefault());
            item.SetParent(parent);
        }

        /// <summary>Overlays get_vod_info onto a movie item. Returns the people to store.</summary>
        public static List<PersonInfo> ApplyVodDetail(Movie item, VodInfoDetail d)
        {
            if (!string.IsNullOrWhiteSpace(d.EffectivePlot)) item.Overview = d.EffectivePlot.Trim();
            var genres = SplitList(d.Genre).ToList();
            if (genres.Count > 0) item.SetGenres(genres);
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
            item.DateModified = DateTimeOffset.UtcNow;
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
            item.SetGenres(SplitList(row.Genre));
            item.CommunityRating = Rating(row.Rating);
            item.ProductionYear = year;
            item.PremiereDate = Date(row.EffectiveReleaseDate);
            long modified;
            item.DateCreated = long.TryParse(row.LastModified, NumberStyles.Integer, CultureInfo.InvariantCulture, out modified) && modified > 0
                ? DateTimeOffset.FromUnixTimeSeconds(modified)
                : DateTimeOffset.UtcNow;
            item.DateModified = DateTimeOffset.UtcNow;
            if (string.IsNullOrEmpty(item.PresentationUniqueKey)) item.PresentationUniqueKey = item.Id.ToString("N");
            item.SetProviderId(ProviderKey, CatalogState.Key(row.SeriesId));
            SetTmdb(item, row.EffectiveTmdbId);
            SetRemoteImage(item, ImageType.Primary, row.Cover);
            SetRemoteImage(item, ImageType.Backdrop, row.BackdropPath.FirstOrDefault());
            item.SetParent(parent);
        }

        /// <summary>
        /// The season name the way Emby's own season resolver builds it: "NameSeasonNumber" (or
        /// "Specials") localized to the series' preferred metadata language, so every client shows
        /// the same wording as seasons created by Emby itself. English when localization is unavailable.
        /// </summary>
        public string SeasonName(Series series, int number)
        {
            var fallback = number == 0 ? "Specials" : string.Format(CultureInfo.InvariantCulture, "Season {0}", number);
            if (_localization == null) return fallback;
            try
            {
                var language = series.GetPreferredMetadataLanguage(_libraryManager.GetLibraryOptions(series));
                var text = number == 0
                    ? _localization.GetLocalizedString("Specials", language)
                    : string.Format(CultureInfo.InvariantCulture, _localization.GetLocalizedString("NameSeasonNumber", language), number.ToString(CultureInfo.InvariantCulture));
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch (Exception ex)
            {
                _logger.Debug("Season name: localization failed, English used: {0}", ex.Message);
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
            item.DateModified = DateTimeOffset.UtcNow;
            if (string.IsNullOrEmpty(item.PresentationUniqueKey)) item.PresentationUniqueKey = item.Id.ToString("N");
            item.SetProviderId(ProviderKey, item.ExternalId);
            LinkToSeries(item, series);
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
            item.DateModified = DateTimeOffset.UtcNow;
            item.SetProviderId(ProviderKey, CatalogState.Key(ep.Id));
            SetTmdb(item, ep.TmdbId);
            LinkToSeries(item, series);
            EmbyCompat.LinkToSeason(item, season);
            // Emby's own key for an episode: series key + "SSS - EEEE".
            item.PresentationUniqueKey = series.PresentationUniqueKey + "-"
                + seasonNumber.ToString("000 - ", CultureInfo.InvariantCulture)
                + episodeNumber.ToString("0000", CultureInfo.InvariantCulture);
            SetRemoteImage(item, ImageType.Primary, ep.Image);
            item.SetParent(season);
        }

        private static void LinkToSeries(BaseItem item, Series series)
        {
            // SeriesId has a setter in both SDKs, but the 4.10 server has SetSeriesItem for this; the compat layer picks it.
            EmbyCompat.LinkToSeries(item, series);
            item.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
            var hasName = item as IHasSeriesName;
            if (hasName != null) hasName.SeriesName = series.Name;
        }

        /// <summary>A remote URL as the image path: Emby downloads it the first time a client asks for it.</summary>
        public static void SetRemoteImage(BaseItem item, ImageType type, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var u = url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            var current = item.GetImages(type).FirstOrDefault();
            if (current != null && string.Equals(current.Path, u, StringComparison.Ordinal)) return;
            item.SetImage(new ItemImageInfo { Path = u, Type = type, DateModified = DateTimeOffset.UtcNow }, 0);
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

        public static DateTimeOffset? Date(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            DateTime d;
            if (DateTime.TryParseExact(v, new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "dd/MM/yyyy", "yyyy-MM-dd HH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
                return new DateTimeOffset(d, TimeSpan.Zero);
            int year;
            if (v.Length == 4 && CatalogNaming.TryParseYear(v, out year))
                return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
                return new DateTimeOffset(d, TimeSpan.Zero);
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
            if (id.All(char.IsDigit) && id != "0") item.SetProviderId(MetadataProviders.Tmdb, id);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var p = path.Trim().Replace('\\', '/');
            while (p.Length > 1 && p.EndsWith("/", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            return p.ToLowerInvariant();
        }

        private static string FirstNonEmpty(string a, string b) => !string.IsNullOrWhiteSpace(a) ? a : b;

        private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
