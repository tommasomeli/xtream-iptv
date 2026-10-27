using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XtreamIptv.Client.Models
{
    /// <summary>Response of <c>get_series_info</c>.</summary>
    public class SeriesDetailInfo
    {
        [JsonPropertyName("seasons")]
        public List<SeasonInfo> Seasons { get; set; } = new List<SeasonInfo>();

        [JsonPropertyName("episodes")]
        [JsonConverter(typeof(TolerantEpisodeDictionaryConverter))]
        public Dictionary<string, List<EpisodeInfo>> Episodes { get; set; } = new Dictionary<string, List<EpisodeInfo>>();

        [JsonPropertyName("info")]
        public SeriesInfo Info { get; set; }
    }

    public class SeasonInfo
    {
        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("cover")]
        public string Cover { get; set; } = string.Empty;

        [JsonPropertyName("air_date")]
        public string AirDate { get; set; } = string.Empty;
    }

    public class EpisodeInfo
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("episode_num")]
        public int EpisodeNum { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; } = "mp4";

        // Some outputs flatten the detail fields to the episode level, Xtream keeps them in "info".
        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("duration")]
        public string Duration { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("season")]
        public int Season { get; set; }

        [JsonPropertyName("info")]
        public EpisodeDetail Info { get; set; }

        [JsonIgnore]
        public string EffectivePlot => FirstNonEmpty(Plot, Info?.Plot);

        [JsonIgnore]
        public string EffectiveRating => FirstNonEmpty(Rating, Info?.Rating);

        [JsonIgnore]
        public string EffectiveDuration => FirstNonEmpty(Duration, Info?.Duration);

        [JsonIgnore]
        public int? DurationSecs => Info?.DurationSecs;

        [JsonIgnore]
        public string Image => Info?.MovieImage;

        [JsonIgnore]
        public string AirDate => FirstNonEmpty(Info?.ReleaseDate, Info?.AirDate);

        [JsonIgnore]
        public string TmdbId => Info?.TmdbId;

        private static string FirstNonEmpty(string a, string b) => !string.IsNullOrWhiteSpace(a) ? a : b;
    }

    /// <summary>The <c>info</c> object of an episode in <c>get_series_info</c>.</summary>
    public class EpisodeDetail
    {
        [JsonPropertyName("tmdb_id")]
        public string TmdbId { get; set; } = string.Empty;

        [JsonPropertyName("releasedate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonPropertyName("air_date")]
        public string AirDate { get; set; } = string.Empty;

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        // int? so the tolerant converter of the client options covers "" and other junk values.
        [JsonPropertyName("duration_secs")]
        public int? DurationSecs { get; set; }

        [JsonPropertyName("duration")]
        public string Duration { get; set; } = string.Empty;

        [JsonPropertyName("movie_image")]
        public string MovieImage { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("season")]
        public int? Season { get; set; }
    }
}
