using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.XtreamIptv.Catalog;
using XtreamIptv.Client.Models;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.XtreamIptv.Api
{
    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/Categories/Vod", "GET", Summary = "Gets VOD categories from the Xtream API")]
    public class GetVodCategories : IReturn<List<Category>>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/Categories/Series", "GET", Summary = "Gets series categories from the Xtream API")]
    public class GetSeriesCategories : IReturn<List<Category>>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/Catalog/Sync", "POST", Summary = "Starts a catalogue sync in the background")]
    public class StartCatalogSync : IReturn<CatalogSyncStartResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/Catalog/Status", "GET", Summary = "Progress of the running sync, last result and library counts")]
    public class GetCatalogStatus : IReturn<CatalogStatusResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XtreamIptv/Catalog/ValidatePath", "POST", Summary = "Creates the two library folders and checks that Emby libraries use them")]
    public class ValidateCatalogPath : IReturn<CatalogPathResult>
    {
        public string Path { get; set; }
    }

    public class CatalogSyncStartResult
    {
        public bool Started { get; set; }
        public string Message { get; set; }
    }

    public class CatalogStatusResult
    {
        public bool Running { get; set; }
        public string Phase { get; set; }
        public int Processed { get; set; }
        public int Total { get; set; }
        public string LastRunUtc { get; set; }
        public string LastSummary { get; set; }
        public int Movies { get; set; }
        public int Series { get; set; }
        public int Episodes { get; set; }
    }

    public class CatalogPathResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Catalogue routes: category lists for the pickers, sync control, status and path check.</summary>
    public class CatalogApi : BaseApiService
    {
        private static readonly TimeSpan CountsTtl = TimeSpan.FromSeconds(20);
        private static readonly object CountsSync = new object();
        private static DateTime _countsAt = DateTime.MinValue;
        private static int _movies, _series, _episodes;

        public async Task<object> Get(GetVodCategories request)
        {
            var config = Plugin.Instance?.Configuration;
            if (!IsConnectionConfigured(config)) return new List<Category>();
            var categories = await CatalogServices.Client.GetVodCategoriesAsync(config, CancellationToken.None).ConfigureAwait(false);
            SaveCategoryCache("VOD", () => config.CachedVodCategories = SerializeCategoryCache(categories));
            return categories;
        }

        public async Task<object> Get(GetSeriesCategories request)
        {
            var config = Plugin.Instance?.Configuration;
            if (!IsConnectionConfigured(config)) return new List<Category>();
            var categories = await CatalogServices.Client.GetSeriesCategoriesAsync(config, CancellationToken.None).ConfigureAwait(false);
            SaveCategoryCache("series", () => config.CachedSeriesCategories = SerializeCategoryCache(categories));
            return categories;
        }

        public object Post(StartCatalogSync request)
        {
            var config = Plugin.Instance?.Configuration;
            if (!IsConnectionConfigured(config))
                return new CatalogSyncStartResult { Started = false, Message = "Xtream connection not configured" };
            if (string.IsNullOrWhiteSpace(config.LibraryPath))
                return new CatalogSyncStartResult { Started = false, Message = "Library path not configured" };

            var missing = MissingLibraries(config);
            if (missing != null)
                return new CatalogSyncStartResult { Started = false, Message = missing };

            var started = CatalogServices.Sync.TryStartBackground();
            return new CatalogSyncStartResult
            {
                Started = started,
                Message = started ? "Sync started" : "A sync is already running",
            };
        }

        public object Get(GetCatalogStatus request)
        {
            var snapshot = CatalogServices.Sync.GetStatus();
            var result = new CatalogStatusResult
            {
                Running = snapshot.Running,
                Phase = snapshot.Phase,
                Processed = snapshot.Processed,
                Total = snapshot.Total,
                LastRunUtc = snapshot.LastRunUtc.HasValue ? snapshot.LastRunUtc.Value.ToString("o") : null,
                LastSummary = snapshot.LastSummary,
            };
            int movies, series, episodes;
            CountLibrary(Plugin.Instance?.Configuration, out movies, out series, out episodes);
            result.Movies = movies;
            result.Series = series;
            result.Episodes = episodes;
            return result;
        }

        public object Post(ValidateCatalogPath request)
        {
            var path = request?.Path;
            if (string.IsNullOrWhiteSpace(path))
                return new CatalogPathResult { Success = false, Message = "Path is empty" };
            var config = new PluginConfiguration { LibraryPath = path };
            var moviesPath = CatalogLayout.MoviesPath(config);
            var showsPath = CatalogLayout.ShowsPath(config);
            try
            {
                Directory.CreateDirectory(moviesPath);
                Directory.CreateDirectory(showsPath);
            }
            catch (Exception ex)
            {
                return new CatalogPathResult { Success = false, Message = "Cannot create the library folders: " + ex.Message };
            }

            var missing = MissingLibraries(config);
            if (missing != null)
                return new CatalogPathResult { Success = true, Message = "Folders ready. " + missing };
            return new CatalogPathResult
            {
                Success = true,
                Message = string.Format("Folders ready and both Emby libraries found ('{0}' and '{1}').", moviesPath, showsPath),
            };
        }

        /// <summary>A message naming the enabled library whose Emby library is missing, null when all are in place.</summary>
        private static string MissingLibraries(PluginConfiguration config)
        {
            var problems = new List<string>();
            try
            {
                string problem;
                if (config.SyncMovies && CatalogServices.Library.FindRoot(CatalogLayout.MoviesPath(config), out problem) == null)
                    problems.Add(problem);
                if (config.SyncSeries && CatalogServices.Library.FindRoot(CatalogLayout.ShowsPath(config), out problem) == null)
                    problems.Add(problem);
            }
            catch (Exception ex)
            {
                problems.Add("Library lookup failed: " + ex.Message);
            }
            return problems.Count == 0 ? null : string.Join(" ", problems);
        }

        /// <summary>Item counts of the two plugin folders in Emby's database, cached briefly: the page polls this while a sync runs.</summary>
        private static void CountLibrary(PluginConfiguration config, out int movies, out int series, out int episodes)
        {
            lock (CountsSync)
            {
                if (DateTime.UtcNow - _countsAt < CountsTtl)
                {
                    movies = _movies; series = _series; episodes = _episodes;
                    return;
                }
                movies = series = episodes = 0;
                try
                {
                    if (config != null && !string.IsNullOrWhiteSpace(config.LibraryPath))
                    {
                        var library = CatalogServices.Library;
                        string problem;
                        var moviesRoot = library.FindRoot(CatalogLayout.MoviesPath(config), out problem);
                        if (moviesRoot != null)
                        {
                            var folder = library.EnsurePluginFolder(moviesRoot, EmbyCatalog.MoviesFolderKey);
                            movies = library.Count(folder, typeof(Movie), false);
                        }
                        var showsRoot = library.FindRoot(CatalogLayout.ShowsPath(config), out problem);
                        if (showsRoot != null)
                        {
                            var folder = library.EnsurePluginFolder(showsRoot, EmbyCatalog.ShowsFolderKey);
                            series = library.Count(folder, typeof(Series), false);
                            episodes = library.Count(folder, typeof(Episode), true);
                        }
                    }
                }
                catch (Exception)
                {
                    // Counts are informational only.
                }
                _movies = movies; _series = series; _episodes = episodes;
                _countsAt = DateTime.UtcNow;
            }
        }

        private static bool IsConnectionConfigured(PluginConfiguration config)
            => config != null && !string.IsNullOrEmpty(config.BaseUrl)
               && !string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password);

        internal static string SerializeCategoryCache(IEnumerable<Category> categories)
            => System.Text.Json.JsonSerializer.Serialize(categories.Select(c => new { c.CategoryId, c.CategoryName }).ToList());

        private void SaveCategoryCache(string kind, Action store)
        {
            try
            {
                store();
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not save the {0} category cache; the list is still returned: {1}", kind, ex.Message);
            }
        }
    }
}
