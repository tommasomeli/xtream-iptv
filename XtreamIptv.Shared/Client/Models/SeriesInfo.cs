using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XtreamIptv.Client.Models
{
    /// <summary>One row of <c>get_series</c>, also the <c>info</c> object of <c>get_series_info</c>.</summary>
    public class SeriesInfo
    {
        [JsonPropertyName("series_id")]
        public int SeriesId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("cover")]
        public string Cover { get; set; } = string.Empty;

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("cast")]
        public string Cast { get; set; } = string.Empty;

        [JsonPropertyName("director")]
        public string Director { get; set; } = string.Empty;

        [JsonPropertyName("genre")]
        public string Genre { get; set; } = string.Empty;

        /// <summary>Providers use either <c>releaseDate</c> or <c>release_date</c>; see <see cref="EffectiveReleaseDate"/>.</summary>
        [JsonPropertyName("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("release_date")]
        public string ReleaseDateSnake { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("category_id")]
        public int? CategoryId { get; set; }

        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("last_modified")]
        public string LastModified { get; set; } = string.Empty;

        /// <summary>Providers use either <c>tmdb</c> or <c>tmdb_id</c>; see <see cref="EffectiveTmdbId"/>.</summary>
        [JsonPropertyName("tmdb")]
        public string TmdbId { get; set; } = string.Empty;

        [JsonPropertyName("tmdb_id")]
        public string TmdbIdSnake { get; set; } = string.Empty;

        [JsonPropertyName("backdrop_path")]
        [JsonConverter(typeof(TolerantStringListConverter))]
        public List<string> BackdropPath { get; set; } = new List<string>();

        [JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; } = string.Empty;

        [JsonPropertyName("episode_run_time")]
        public string EpisodeRunTime { get; set; } = string.Empty;

        [JsonPropertyName("is_adult")]
        public int? IsAdult { get; set; }

        [JsonIgnore]
        public string EffectiveReleaseDate => string.IsNullOrWhiteSpace(ReleaseDate) ? ReleaseDateSnake : ReleaseDate;

        [JsonIgnore]
        public string EffectiveTmdbId => string.IsNullOrWhiteSpace(TmdbId) ? TmdbIdSnake : TmdbId;

        [JsonIgnore]
        public bool IsAdultContent => IsAdult.HasValue && IsAdult.Value != 0;
    }
}
