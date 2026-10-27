using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XtreamIptv.Client.Models
{
    /// <summary>Response of <c>get_vod_info</c>: the detail asked when a movie is opened.</summary>
    public class VodInfo
    {
        [JsonPropertyName("info")]
        public VodInfoDetail Info { get; set; }

        [JsonPropertyName("movie_data")]
        public VodMovieData MovieData { get; set; }
    }

    public class VodInfoDetail
    {
        [JsonPropertyName("tmdb_id")]
        public string TmdbId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("o_name")]
        public string OriginalName { get; set; } = string.Empty;

        [JsonPropertyName("movie_image")]
        public string MovieImage { get; set; } = string.Empty;

        [JsonPropertyName("cover_big")]
        public string CoverBig { get; set; } = string.Empty;

        [JsonPropertyName("releasedate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("release_date")]
        public string ReleaseDateSnake { get; set; } = string.Empty;

        [JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; } = string.Empty;

        [JsonPropertyName("director")]
        public string Director { get; set; } = string.Empty;

        [JsonPropertyName("actors")]
        public string Actors { get; set; } = string.Empty;

        [JsonPropertyName("cast")]
        public string Cast { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("country")]
        public string Country { get; set; } = string.Empty;

        [JsonPropertyName("genre")]
        public string Genre { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("duration_secs")]
        public int? DurationSecs { get; set; }

        [JsonPropertyName("duration")]
        public string Duration { get; set; } = string.Empty;

        [JsonPropertyName("backdrop_path")]
        [JsonConverter(typeof(TolerantStringListConverter))]
        public List<string> BackdropPath { get; set; } = new List<string>();

        [JsonIgnore]
        public string EffectivePlot => FirstNonEmpty(Plot, Description);

        [JsonIgnore]
        public string EffectiveCast => FirstNonEmpty(Cast, Actors);

        [JsonIgnore]
        public string EffectiveReleaseDate => FirstNonEmpty(ReleaseDate, ReleaseDateSnake);

        [JsonIgnore]
        public string EffectivePoster => FirstNonEmpty(MovieImage, CoverBig);

        /// <summary>True when the provider answered with something worth writing.</summary>
        [JsonIgnore]
        public bool HasAnyDetail =>
            !string.IsNullOrWhiteSpace(EffectivePlot) || !string.IsNullOrWhiteSpace(EffectiveCast)
            || !string.IsNullOrWhiteSpace(Director) || !string.IsNullOrWhiteSpace(Genre)
            || !string.IsNullOrWhiteSpace(EffectiveReleaseDate) || BackdropPath.Count > 0
            || !string.IsNullOrWhiteSpace(YoutubeTrailer) || !string.IsNullOrWhiteSpace(Country);

        private static string FirstNonEmpty(string a, string b) => !string.IsNullOrWhiteSpace(a) ? a : b;
    }

    public class VodMovieData
    {
        [JsonPropertyName("stream_id")]
        public int? StreamId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; } = string.Empty;

        [JsonPropertyName("category_id")]
        public int? CategoryId { get; set; }

        [JsonPropertyName("added")]
        public string Added { get; set; } = string.Empty;
    }
}
