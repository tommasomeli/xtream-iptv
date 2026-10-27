using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtreamIptv.Client.Models
{
    /// <summary>
    /// Reads a field that providers send either as an array of strings (<c>backdrop_path</c>),
    /// a single string, an empty object or null. Anything unreadable becomes an empty list.
    /// </summary>
    internal sealed class TolerantStringListConverter : JsonConverter<List<string>>
    {
        public override bool HandleNull { get { return true; } }

        public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var result = new List<string>();
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    AddIfNotEmpty(result, reader.GetString());
                    return result;
                case JsonTokenType.StartArray:
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndArray) break;
                        if (reader.TokenType == JsonTokenType.String)
                            AddIfNotEmpty(result, reader.GetString());
                        else if (reader.TokenType == JsonTokenType.Number)
                            AddIfNotEmpty(result, reader.GetDouble().ToString(CultureInfo.InvariantCulture));
                        else if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                            reader.Skip();
                    }
                    return result;
                case JsonTokenType.StartObject:
                    reader.Skip();
                    return result;
                default:
                    return result;
            }
        }

        private static void AddIfNotEmpty(List<string> list, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value.Trim());
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            if (value != null)
                foreach (var s in value) writer.WriteStringValue(s);
            writer.WriteEndArray();
        }
    }
}
