using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.XtreamIptv.Catalog;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using XtreamIptv;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;

namespace Jellyfin.Plugin.XtreamIptv.Controllers
{
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
        /// <summary>Names of the enabled libraries Jellyfin does not have yet ("Film", "Serie").</summary>
        public string[] LibrariesMissing { get; set; } = Array.Empty<string>();
        /// <summary>What is missing, in words; null when both libraries are in place.</summary>
        public string Message { get; set; }
    }

    public class CatalogPathResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class CatalogPathRequest
    {
        public string Path { get; set; }
    }

    /// <summary>Catalogue routes: category lists for the pickers, sync control, status, path check and library creation.</summary>
    [ApiController]
    [Route("XtreamIptv")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces("application/json")]
    public class CatalogController : ControllerBase
    {
        private static readonly TimeSpan CountsTtl = TimeSpan.FromSeconds(20);
        private static readonly object CountsSync = new object();
        private static DateTime _countsAt = DateTime.MinValue;
        private static int _movies, _series, _episodes;

        private readonly XtreamClient _client;
        private readonly CatalogSyncService _sync;
        private readonly JellyfinCatalog _library;
        private readonly ILogger<CatalogController> _logger;

        public CatalogController(XtreamClient client, CatalogSyncService sync, JellyfinCatalog library, ILogger<CatalogController> logger)
        {
            _client = client;
            _sync = sync;
            _library = library;
            _logger = logger;
        }

        private static PluginConfiguration Config => Plugin.InstanceOrNull?.Configuration;

        [HttpGet("Categories/Vod")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<List<Category>>> GetVodCategories()
        {
            var config = Config;
            if (!IsConnectionConfigured(config)) return new List<Category>();
            var categories = await _client.GetVodCategoriesAsync(config, CancellationToken.None).ConfigureAwait(false);
            SaveCategoryCache("VOD", () => config.CachedVodCategories = CatalogSyncService.SerializeCategoryCache(categories));
            return categories;
        }

        [HttpGet("Categories/Series")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<List<Category>>> GetSeriesCategories()
        {
            var config = Config;
            if (!IsConnectionConfigured(config)) return new List<Category>();
            var categories = await _client.GetSeriesCategoriesAsync(config, CancellationToken.None).ConfigureAwait(false);
            SaveCategoryCache("series", () => config.CachedSeriesCategories = CatalogSyncService.SerializeCategoryCache(categories));
            return categories;
        }

        [HttpPost("Catalog/Sync")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<CatalogSyncStartResult> StartSync()
        {
            var config = Config;
            if (!IsConnectionConfigured(config))
                return new CatalogSyncStartResult { Started = false, Message = "Xtream connection not configured" };
            if (string.IsNullOrWhiteSpace(config.LibraryPath))
                return new CatalogSyncStartResult { Started = false, Message = "Library path not configured" };

            var missing = MissingLibraries(config, out _);
            if (missing != null)
                return new CatalogSyncStartResult { Started = false, Message = missing };

            var started = _sync.TryStartBackground();
            return new CatalogSyncStartResult
            {
                Started = started,
                Message = started ? "Sync started" : "A sync is already running",
            };
        }

        [HttpGet("Catalog/Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<CatalogStatusResult> GetStatus()
        {
            var snapshot = _sync.GetStatus();
            var result = new CatalogStatusResult
            {
                Running = snapshot.Running,
                Phase = snapshot.Phase,
                Processed = snapshot.Processed,
                Total = snapshot.Total,
                LastRunUtc = snapshot.LastRunUtc.HasValue ? snapshot.LastRunUtc.Value.ToString("o") : null,
                LastSummary = snapshot.LastSummary,
            };
            var config = Config;
            int movies, series, episodes;
            CountLibrary(config, out movies, out series, out episodes);
            result.Movies = movies;
            result.Series = series;
            result.Episodes = episodes;
            if (config != null && !string.IsNullOrWhiteSpace(config.LibraryPath))
            {
                string[] names;
                result.Message = MissingLibraries(config, out names);
                result.LibrariesMissing = names;
            }
            return result;
        }

        [HttpPost("Catalog/ValidatePath")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<CatalogPathResult> ValidatePath([FromBody] CatalogPathRequest request)
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

            var missing = MissingLibraries(config, out _);
            if (missing != null)
                return new CatalogPathResult { Success = true, Message = "Folders ready. " + missing };
            return new CatalogPathResult
            {
                Success = true,
                Message = string.Format("Folders ready and both Jellyfin libraries found ('{0}' and '{1}').", moviesPath, showsPath),
            };
        }

        /// <summary>Creates the "Film" and "Serie" libraries (no fetchers) for the configured path when they do not exist.</summary>
        [HttpPost("Catalog/CreateLibraries")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<CatalogPathResult>> CreateLibraries([FromBody] CatalogPathRequest request)
        {
            var path = request?.Path;
            if (string.IsNullOrWhiteSpace(path)) path = Config?.LibraryPath;
            if (string.IsNullOrWhiteSpace(path))
                return new CatalogPathResult { Success = false, Message = "Library path not configured" };
            try
            {
                var lines = await _library.CreateLibrariesAsync(path).ConfigureAwait(false);
                InvalidateCounts();
                return new CatalogPathResult { Success = true, Message = string.Join(" ", lines) };
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Create libraries failed: {Message}", ex.Message);
                return new CatalogPathResult { Success = false, Message = "Could not create the libraries: " + ex.Message };
            }
        }

        /// <summary>A message naming the enabled library whose Jellyfin library is missing, null when all are in place.</summary>
        private string MissingLibraries(PluginConfiguration config, out string[] names)
        {
            var problems = new List<string>();
            var missing = new List<string>();
            try
            {
                string problem;
                if (config.SyncMovies && _library.FindRoot(CatalogLayout.MoviesPath(config), out problem) == null)
                {
                    problems.Add(problem);
                    missing.Add(JellyfinCatalog.MoviesLibraryName);
                }
                if (config.SyncSeries && _library.FindRoot(CatalogLayout.ShowsPath(config), out problem) == null)
                {
                    problems.Add(problem);
                    missing.Add(JellyfinCatalog.ShowsLibraryName);
                }
            }
            catch (Exception ex)
            {
                problems.Add("Library lookup failed: " + ex.Message);
            }
            names = missing.ToArray();
            return problems.Count == 0 ? null : string.Join(" ", problems);
        }

        /// <summary>Item counts of the two plugin folders, cached briefly: the page polls this while a sync runs.</summary>
        private void CountLibrary(PluginConfiguration config, out int movies, out int series, out int episodes)
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
                        string problem;
                        var moviesRoot = _library.FindRoot(CatalogLayout.MoviesPath(config), out problem);
                        if (moviesRoot != null)
                        {
                            var folder = _library.EnsurePluginFolder(moviesRoot, JellyfinCatalog.MoviesFolderKey);
                            movies = _library.Count(folder, BaseItemKind.Movie, false);
                        }
                        var showsRoot = _library.FindRoot(CatalogLayout.ShowsPath(config), out problem);
                        if (showsRoot != null)
                        {
                            var folder = _library.EnsurePluginFolder(showsRoot, JellyfinCatalog.ShowsFolderKey);
                            series = _library.Count(folder, BaseItemKind.Series, false);
                            episodes = _library.Count(folder, BaseItemKind.Episode, true);
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

        private static void InvalidateCounts()
        {
            lock (CountsSync) _countsAt = DateTime.MinValue;
        }

        private static bool IsConnectionConfigured(PluginConfiguration config)
            => config != null && !string.IsNullOrEmpty(config.BaseUrl)
               && !string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password);

        private void SaveCategoryCache(string kind, Action store)
        {
            try
            {
                store();
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not save the {Kind} category cache; the list is still returned: {Message}", kind, ex.Message);
            }
        }
    }
}
