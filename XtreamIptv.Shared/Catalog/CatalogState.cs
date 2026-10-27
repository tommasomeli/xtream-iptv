using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtreamIptv.Catalog
{
    /// <summary>What the last sync stored in Emby for one movie (one item per release).</summary>
    internal sealed class MovieState
    {
        public string Signature { get; set; }
        /// <summary>Emby internal id of the primary item.</summary>
        public long ItemId { get; set; }
        /// <summary>Emby internal id of each secondary version, keyed by stream id.</summary>
        public Dictionary<string, long> Versions { get; set; } = new Dictionary<string, long>();
    }

    /// <summary>What the last sync stored in Emby for one series.</summary>
    internal sealed class SeriesState
    {
        public string LastModified { get; set; }
        /// <summary>Signature of the list row (name, year, category, cover, plot hash), so a changed row is updated.</summary>
        public string Signature { get; set; }
        public long ItemId { get; set; }
        /// <summary>Episodes loaded once; set again when last_modified changes so the next open refetches.</summary>
        public bool Stale { get; set; } = true;
        public int Episodes { get; set; }
    }

    /// <summary>
    /// Sync memory, one JSON file in the plugin configuration folder: which provider id is which
    /// Emby item, the signatures that decide "unchanged", and which movies already got their detail.
    /// Shared by the sync and the open hook, so every access goes through <see cref="SyncRoot"/>.
    /// </summary>
    internal sealed class CatalogState
    {
        public const int CurrentVersion = 2;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, MovieState> Movies { get; set; } = new Dictionary<string, MovieState>();
        public Dictionary<string, SeriesState> Series { get; set; } = new Dictionary<string, SeriesState>();
        /// <summary>Stream ids whose get_vod_info detail is already in Emby.</summary>
        public HashSet<string> Detailed { get; set; } = new HashSet<string>();
        /// <summary>Provider categories seen by a sync; a category not in here is new (auto-include).</summary>
        public HashSet<int> KnownVodCategoryIds { get; set; } = new HashSet<int>();
        public HashSet<int> KnownSeriesCategoryIds { get; set; } = new HashSet<int>();

        [JsonIgnore]
        public object SyncRoot { get; } = new object();

        [JsonIgnore]
        public string Path { get; set; }

        public static CatalogState Load(string path)
        {
            CatalogState state = null;
            try
            {
                if (File.Exists(path))
                    state = JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(path), Options);
            }
            catch (Exception)
            {
                // A corrupt state only costs one full update of unchanged items.
            }
            if (state == null || state.Version != CurrentVersion) state = new CatalogState();
            if (state.Movies == null) state.Movies = new Dictionary<string, MovieState>();
            if (state.Series == null) state.Series = new Dictionary<string, SeriesState>();
            if (state.Detailed == null) state.Detailed = new HashSet<string>();
            if (state.KnownVodCategoryIds == null) state.KnownVodCategoryIds = new HashSet<int>();
            if (state.KnownSeriesCategoryIds == null) state.KnownSeriesCategoryIds = new HashSet<int>();
            state.Path = path;
            return state;
        }

        /// <summary>Writes through a temp file and a move, under the lock, so a reader never sees a half file.</summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(Path)) return;
            lock (SyncRoot)
            {
                Version = CurrentVersion;
                var json = JsonSerializer.Serialize(this, Options);
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var temp = Path + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(Path)) File.Delete(Path);
                File.Move(temp, Path);
            }
        }

        public bool IsDetailed(int streamId)
        {
            lock (SyncRoot) return Detailed.Contains(Key(streamId));
        }

        public void MarkDetailed(int streamId)
        {
            lock (SyncRoot) Detailed.Add(Key(streamId));
        }

        /// <summary>The list row changed: the next open fetches the detail (and the cast) again.</summary>
        public void UnmarkDetailed(int streamId)
        {
            lock (SyncRoot) Detailed.Remove(Key(streamId));
        }

        public SeriesState FindSeries(int seriesId)
        {
            lock (SyncRoot)
            {
                SeriesState s;
                return Series.TryGetValue(Key(seriesId), out s) ? s : null;
            }
        }

        public void SetSeriesLoaded(int seriesId, int episodes)
        {
            lock (SyncRoot)
            {
                SeriesState s;
                if (Series.TryGetValue(Key(seriesId), out s))
                {
                    s.Stale = false;
                    s.Episodes = episodes;
                }
            }
        }

        public static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);

        /// <summary>Everything the Emby items depend on: title and year, the NFO-like fields of the row, the version set.</summary>
        /// <summary>
        /// What every stream URL starts with (server, user, password), hashed. Part of each signature so
        /// that a changed connection rewrites every item's path at the next sync.
        /// </summary>
        public static string StreamBase(PluginConfiguration config)
            => Hash(Client.XtreamUrls.Stream(config, string.Empty, string.Empty, string.Empty));

        public static string MovieSignature(MovieEntry entry, string streamBase = null)
        {
            var p = entry.Primary;
            var fields = string.Join("\u001f", new[]
            {
                p.Plot, p.Cast, p.Director, p.Genre, p.Rating, p.StreamIcon, p.TmdbId, p.ReleaseDate,
                string.IsNullOrEmpty(p.Trailer) ? p.YoutubeTrailer : p.Trailer,
            });
            var versions = new List<string>();
            versions.Add(VersionKey(p.StreamId, p.ContainerExtension));
            foreach (var v in entry.Versions)
                versions.Add(VersionKey(v.StreamId, v.ContainerExtension) + ":" + CatalogNaming.VersionLabel(v.Name));
            return string.Join("|", new[]
            {
                entry.Title,
                entry.Year.HasValue ? entry.Year.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                p.CategoryId.HasValue ? p.CategoryId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                p.Added.ToString(CultureInfo.InvariantCulture),
                Hash(fields),
                Hash(string.Join(",", versions)),
                streamBase ?? string.Empty,
                MovieItemSchema,
            });
        }

        /// <summary>Bumped when what the sync writes on an item changes, so every item is rewritten once.</summary>
        private const string MovieItemSchema = "v3";

        public static string SeriesSignature(string title, int? year, int? categoryId, string cover, string plot = null, string cast = null, string streamBase = null)
        {
            return string.Join("|", new[]
            {
                title,
                year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                categoryId.HasValue ? categoryId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                Hash((cover ?? string.Empty) + "\u001f" + (plot ?? string.Empty) + "\u001f" + (cast ?? string.Empty)),
                streamBase ?? string.Empty,
            });
        }

        private static string VersionKey(int id, string ext)
            => id.ToString(CultureInfo.InvariantCulture) + "." + (ext ?? string.Empty);

        /// <summary>FNV-1a 64 bit, hex. Stable across runs, which a string hash code is not.</summary>
        public static string Hash(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            if (value != null)
            {
                foreach (var b in Encoding.UTF8.GetBytes(value))
                {
                    hash ^= b;
                    hash *= prime;
                }
            }
            return hash.ToString("x16");
        }
    }
}
