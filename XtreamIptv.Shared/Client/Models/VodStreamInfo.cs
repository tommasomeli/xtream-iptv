using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XtreamIptv.Client.Models
{
    /// <summary>One row of <c>get_vod_streams</c>. String fields are read through the tolerant converters of the client options.</summary>
    public class VodStreamInfo
    {
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stream_icon")]
        public string StreamIcon { get; set; } = string.Empty;

        [JsonPropertyName("rating")]
        public string Rating { get; set; } = string.Empty;

        [JsonPropertyName("category_id")]
        public int? CategoryId { get; set; }

        [JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; } = "mp4";

        [JsonPropertyName("added")]
        public long Added { get; set; }

        [JsonPropertyName("is_adult")]
        public int? IsAdult { get; set; }

        [JsonPropertyName("tmdb_id")]
        public string TmdbId { get; set; } = string.Empty;

        [JsonPropertyName("plot")]
        public string Plot { get; set; } = string.Empty;

        [JsonPropertyName("genre")]
        public string Genre { get; set; } = string.Empty;

        [JsonPropertyName("year")]
        public string Year { get; set; } = string.Empty;

        [JsonPropertyName("director")]
        public string Director { get; set; } = string.Empty;

        [JsonPropertyName("cast")]
        public string Cast { get; set; } = string.Empty;

        [JsonPropertyName("release_date")]
        public string ReleaseDate { get; set; } = string.Empty;

        /// <summary>Dispatcharr lists carry the YouTube trailer id (or URL) under <c>trailer</c>.</summary>
        [JsonPropertyName("trailer")]
        public string Trailer { get; set; } = string.Empty;

        [JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; } = string.Empty;

        [JsonPropertyName("backdrop_path")]
        [JsonConverter(typeof(TolerantStringListConverter))]
        public List<string> BackdropPath { get; set; } = new List<string>();

        [JsonIgnore]
        public bool IsAdultContent => IsAdult.HasValue && IsAdult.Value != 0;
    }
}
