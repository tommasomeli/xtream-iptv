using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using XtreamIptv.Client.Models;

namespace XtreamIptv.Catalog
{
    /// <summary>A movie after title cleaning and version merging: one folder, one or more .strm files.</summary>
    internal sealed class MovieEntry
    {
        public VodStreamInfo Primary { get; set; }
        public List<VodStreamInfo> Versions { get; } = new List<VodStreamInfo>();
        public string Title { get; set; }
        public int? Year { get; set; }

        /// <summary><c>Title (Year)</c>, or just the title when the year is unknown.</summary>
        public string DisplayName => CatalogNaming.DisplayName(Title, Year);
    }

    /// <summary>One release of a movie other than the primary, as read back from the item.</summary>
    internal sealed class MovieVersion
    {
        public int StreamId { get; set; }
        public string Extension { get; set; }
        public string Label { get; set; }
    }

    /// <summary>Pure string logic of the catalogue: title cleaning, years, file names, version merging.</summary>
    internal static class CatalogNaming
    {
        private static readonly Regex QualityTagRegex = new Regex(
            @"(?<=^|[\s\[(|\-])[\[(]?(FHD|UHD|4K|HDR|HEVC|H\.?265|2160p|1080p|720p|HD|SD)[\])]?(?=$|[\s\])|\-])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Language markers providers append to a release ("47 Ronin Ita En", "Title SUB ITA", "Title MULTI"):
        // only at the end of the name, one or more, so a title that merely ends with such a word is kept.
        private static readonly Regex LanguageTagRegex = new Regex(
            @"(?<=\S)(?:[\s\-|/+&]+(?:(?:con|with|and|e)\s+)?[\[(]?(?:ITA|ENG|SUB\s?ITA|SUB|SUBS|MULTI|DUAL|DUB|AUDIO|LATINO|SPA|ESP|FRA|FRE|GER|DEU|POR|RUS|JAP|HINDI|EN|IT|FR|DE|ES)[\])]?)+\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<string> LanguageJoiners = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "con", "with", "and", "e" };

        // A bracketed year anywhere in the name: providers append tags after it ("Title (2025) SUB ITA").
        private static readonly Regex YearInTitleRegex = new Regex(@"[\(\[]((?:19|20)\d{2})[\)\]]", RegexOptions.Compiled);
        private static readonly Regex TrailingYearRegex = new Regex(@"[\(\[]((?:19|20)\d{2})[\)\]]\s*$", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex EmptyBracketsRegex = new Regex(@"[\(\[]\s*[\)\]]", RegexOptions.Compiled);
        private static readonly Regex InvalidFileCharsRegex = new Regex(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);
        // "<anything> - S01E02 - Title", "<anything> S01E02 - Title", "1x02 Title": the code must sit at the
        // start or after a separator, and be followed by a separator or the end.
        private static readonly Regex EpisodeCodeRegex = new Regex(
            @"^(?<prefix>.*?)(?<=^|[\s\-:|.\]\)])(?:S(?<s>\d{1,3})\s?E(?<e>\d{1,4})|(?<s2>\d{1,3})x(?<e2>\d{1,4}))(?=$|[\s\-:|.])(?<rest>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly char[] SeparatorTrim = { ' ', '-', '|', ':', '.', ',', '_', '/' };

        private static readonly string[] AdultWords = { "adult", "xxx", "porn", "erotic", "18+", "hentai", "for adults" };

        /// <summary>Strips quality tags, the user's terms, repeated spaces and dangling separators.</summary>
        public static string CleanTitle(string name, string removeTerms)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var result = name;
            foreach (var term in SplitTerms(removeTerms))
            {
                int idx;
                while ((idx = result.IndexOf(term, StringComparison.OrdinalIgnoreCase)) >= 0)
                    result = result.Remove(idx, term.Length);
            }
            result = QualityTagRegex.Replace(result, " ");
            result = EmptyBracketsRegex.Replace(result, " ");
            result = MultiSpaceRegex.Replace(result, " ").Trim();
            result = LanguageTagRegex.Replace(result, string.Empty);
            result = MultiSpaceRegex.Replace(result, " ").Trim();
            result = result.Trim(SeparatorTrim);
            result = MultiSpaceRegex.Replace(result, " ").Trim();
            return result.Length == 0 ? name.Trim() : result;
        }

        /// <summary>The quality tags found in a raw name, upper case, space separated ("1080p HDR").</summary>
        public static string QualityLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var tags = QualityTagRegex.Matches(name).Cast<Match>()
                .Select(m => m.Groups[1].Value.ToUpperInvariant().Replace("H265", "H.265"))
                .Distinct();
            return string.Join(" ", tags);
        }

        /// <summary>The language markers at the end of a raw name, upper case ("ITA EN"), or empty.</summary>
        public static string LanguageLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var m = LanguageTagRegex.Match(name);
            if (!m.Success) return string.Empty;
            var tokens = Regex.Split(m.Value, @"[\s\-|/+&\[\]()]+").Where(t => t.Length > 0 && !LanguageJoiners.Contains(t)).Select(t => t.ToUpperInvariant());
            return string.Join(" ", tokens);
        }

        /// <summary>What tells one release of a movie from another: quality tags, then language markers ("FHD ITA EN").</summary>
        public static string VersionLabel(string name)
        {
            var quality = QualityLabel(name);
            var language = LanguageLabel(name);
            return quality.Length > 0 && language.Length > 0 ? quality + " " + language : quality + language;
        }

        /// <summary>
        /// Year from the first bracketed <c>(YYYY)</c> anywhere in the name, else from the provider
        /// field. Every bracketed year is removed from the title, so the display name never doubles it.
        /// </summary>
        public static int? ExtractYear(ref string title, string yearField)
        {
            var match = YearInTitleRegex.Match(title ?? string.Empty);
            int year;
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out year) && IsPlausibleYear(year))
            {
                title = YearInTitleRegex.Replace(title, " ");
                title = MultiSpaceRegex.Replace(title, " ").Trim().Trim(SeparatorTrim);
                title = MultiSpaceRegex.Replace(title, " ").Trim();
                return year;
            }
            if (TryParseYear(yearField, out year)) return year;
            return null;
        }

        /// <summary>Year of a date or year string: "2019", "2019-05-01", "2019.0".</summary>
        public static bool TryParseYear(string value, out int year)
        {
            year = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var digits = Regex.Match(value.Trim(), @"^\d{4}");
            return digits.Success
                && int.TryParse(digits.Value, NumberStyles.None, CultureInfo.InvariantCulture, out year)
                && IsPlausibleYear(year);
        }

        /// <summary><c>Title (Year)</c>; a title that already ends with that year is left alone.</summary>
        public static string DisplayName(string title, int? year)
        {
            if (!year.HasValue) return title;
            var trailing = TrailingYearRegex.Match(title ?? string.Empty);
            if (trailing.Success && trailing.Groups[1].Value == year.Value.ToString(CultureInfo.InvariantCulture))
                return title;
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", title, year.Value);
        }

        /// <summary>Removes characters a file system rejects, leading or trailing dots and spaces.</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var result = InvalidFileCharsRegex.Replace(name, string.Empty);
            result = MultiSpaceRegex.Replace(result, " ");
            return result.Trim('.', ' ');
        }

        /// <summary>True for <c>is_adult</c> rows and for category names that read as adult.</summary>
        public static bool IsAdultCategoryName(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return false;
            var lower = categoryName.ToLowerInvariant();
            return AdultWords.Any(w => lower.Contains(w));
        }

        /// <summary>True when the category name contains one of the user's excluded terms (case-insensitive).</summary>
        public static bool IsExcludedCategoryName(string categoryName, string excludedTerms)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return false;
            return SplitTerms(excludedTerms).Any(t => categoryName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Cleans every row and, when merging is on, folds releases of the same title and year into
        /// one movie: the untagged release is primary, the others become versions. Output order is
        /// by primary stream id, so folder collisions resolve the same way on every run.
        /// </summary>
        public static List<MovieEntry> BuildMovieEntries(IEnumerable<VodStreamInfo> rows, string removeTerms, bool mergeVersions)
        {
            var entries = new List<MovieEntry>();
            var groups = new Dictionary<string, MovieEntry>(StringComparer.Ordinal);
            foreach (var row in rows.OrderBy(r => r.StreamId))
            {
                var title = CleanTitle(row.Name, removeTerms);
                var year = ExtractYear(ref title, row.Year);
                title = SanitizeFileName(title);
                if (title.Length == 0) continue;

                if (!mergeVersions)
                {
                    entries.Add(new MovieEntry { Primary = row, Title = title, Year = year });
                    continue;
                }

                var key = GroupKey(title, year);
                MovieEntry entry;
                if (!groups.TryGetValue(key, out entry))
                {
                    entry = new MovieEntry { Primary = row, Title = title, Year = year };
                    groups[key] = entry;
                    entries.Add(entry);
                    continue;
                }

                // The untagged release is the primary; a tagged one that arrived first steps aside.
                if (VersionLabel(entry.Primary.Name).Length > 0 && VersionLabel(row.Name).Length == 0)
                {
                    entry.Versions.Insert(0, entry.Primary);
                    entry.Primary = row;
                    entry.Title = title;
                    entry.Year = year;
                }
                else
                {
                    entry.Versions.Add(row);
                }
            }
            return mergeVersions ? MergeYearless(entries) : entries;
        }

        /// <summary>
        /// A release without a year ("47 Ronin Ita En") joins the one release with the same title that
        /// has a year ("47 Ronin (2013)"). With two or more years for that title (remakes) it stays apart.
        /// </summary>
        private static List<MovieEntry> MergeYearless(List<MovieEntry> entries)
        {
            var byTitle = entries.Where(e => e.Year.HasValue)
                .GroupBy(e => GroupKey(e.Title, null), StringComparer.Ordinal)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var result = new List<MovieEntry>(entries.Count);
            foreach (var entry in entries)
            {
                MovieEntry target;
                if (!entry.Year.HasValue && byTitle.TryGetValue(GroupKey(entry.Title, null), out target) && !ReferenceEquals(target, entry))
                {
                    target.Versions.Add(entry.Primary);
                    target.Versions.AddRange(entry.Versions);
                    continue;
                }
                result.Add(entry);
            }
            return result;
        }

        private static string GroupKey(string title, int? year)
        {
            var normalized = Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}]", string.Empty);
            return normalized + "|" + (year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        /// <summary>
        /// The releases of a movie other than the primary, as stored on the item: <c>id.ext:label|id.ext:label</c>.
        /// A label is the version's tag ("FHD ITA EN"), or the container when the release carries none.
        /// </summary>
        public static string VersionsString(MovieEntry entry)
        {
            var parts = entry.Versions.Select(v =>
            {
                var ext = (v.ContainerExtension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
                if (ext.Length == 0) ext = "mp4";
                var label = VersionLabel(v.Name);
                if (label.Length == 0) label = ext.ToUpperInvariant();
                return v.StreamId.ToString(CultureInfo.InvariantCulture) + "." + ext + ":" + label.Replace("|", " ").Replace(":", " ");
            });
            return string.Join("|", parts);
        }

        public static List<MovieVersion> ParseVersions(string value)
        {
            var result = new List<MovieVersion>();
            if (string.IsNullOrWhiteSpace(value)) return result;
            foreach (var part in value.Split('|'))
            {
                var colon = part.IndexOf(':');
                var idExt = colon < 0 ? part : part.Substring(0, colon);
                var label = colon < 0 ? string.Empty : part.Substring(colon + 1).Trim();
                var dot = idExt.IndexOf('.');
                int id;
                if (!int.TryParse(dot < 0 ? idExt : idExt.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
                var ext = dot < 0 ? "mp4" : idExt.Substring(dot + 1);
                result.Add(new MovieVersion { StreamId = id, Extension = ext, Label = label.Length > 0 ? label : ext.ToUpperInvariant() });
            }
            return result;
        }

        /// <summary>File name (without directory) of each version .strm: <c>&lt;folder&gt; - &lt;label&gt;.strm</c>.</summary>
        public static List<string> VersionFileNames(MovieEntry entry, string folderName)
        {
            var names = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entry.Versions.Count; i++)
            {
                var label = SanitizeFileName(QualityLabel(entry.Versions[i].Name));
                if (label.Length == 0 || !used.Add(label))
                {
                    label = string.Format(CultureInfo.InvariantCulture, "Version {0}", i + 2);
                    used.Add(label);
                }
                names.Add(folderName + " - " + label + ".strm");
            }
            return names;
        }

        /// <summary>
        /// Drops the "&lt;anything&gt; - SNNEMM -" prefix providers put in the episode title, whatever the
        /// prefix is (the series name in any spelling, a tag, nothing) and in the S01E02 or 1x02
        /// notation. What remains is the human title, or empty when there was only the prefix.
        /// The 1x02 form is accepted only when it matches the episode or stands at the start, so a
        /// title like "The 2x4 Problem" is not cut.
        /// </summary>
        public static string CleanEpisodeTitle(string episodeTitle, string seriesName, int season, int episode)
        {
            var result = (episodeTitle ?? string.Empty).Trim();
            if (result.Length == 0) return result;

            var m = EpisodeCodeRegex.Match(result);
            if (m.Success)
            {
                var isX = m.Groups["s2"].Success;
                var codeSeason = ParseInt(isX ? m.Groups["s2"].Value : m.Groups["s"].Value);
                var codeEpisode = ParseInt(isX ? m.Groups["e2"].Value : m.Groups["e"].Value);
                var prefix = m.Groups["prefix"].Value.Trim();
                var matches = codeSeason == season && codeEpisode == episode;
                if (!isX || matches || prefix.Length == 0 || prefix.EndsWith("-", StringComparison.Ordinal))
                {
                    result = m.Groups["rest"].Value.Trim().TrimStart(SeparatorTrim).Trim();
                    return result;
                }
            }

            // No code: a repeated series name followed by a separator is still dropped.
            if (!string.IsNullOrEmpty(seriesName))
            {
                var bare = TrailingYearRegex.Replace(seriesName, string.Empty).Trim();
                foreach (var name in new[] { seriesName, bare }.Where(n => n.Length > 0).Distinct())
                {
                    if (result.Length > name.Length && result.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                        && result.Substring(name.Length).TrimStart().StartsWith("-", StringComparison.Ordinal))
                        return result.Substring(name.Length).Trim().TrimStart(SeparatorTrim).Trim();
                }
            }
            return result;
        }

        /// <summary><c>Name - SNNEMM[ - Title]</c> without extension.</summary>
        public static string EpisodeBaseName(string seriesDisplayName, int season, int episode, string cleanTitle)
        {
            var name = string.Format(CultureInfo.InvariantCulture, "{0} - S{1:D2}E{2:D2}", seriesDisplayName, season, episode);
            var title = SanitizeFileName(cleanTitle);
            return title.Length > 0 ? name + " - " + title : name;
        }

        public static string SeasonFolderName(int season)
            => string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", season);

        /// <summary>Runtime in minutes from <c>duration_secs</c> or a "HH:MM:SS" duration.</summary>
        public static int? RuntimeMinutes(int? durationSecs, string duration)
        {
            if (durationSecs.HasValue && durationSecs.Value > 0)
                return Math.Max(1, (int)Math.Round(durationSecs.Value / 60.0));
            TimeSpan span;
            if (!string.IsNullOrWhiteSpace(duration)
                && TimeSpan.TryParse(duration.Trim(), CultureInfo.InvariantCulture, out span) && span.TotalMinutes >= 1)
                return (int)Math.Round(span.TotalMinutes);
            return null;
        }

        /// <summary>Comma or newline separated terms, trimmed, empty ones dropped.</summary>
        public static IEnumerable<string> SplitTerms(string terms)
            => string.IsNullOrWhiteSpace(terms)
                ? Enumerable.Empty<string>()
                : terms.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim()).Where(t => t.Length > 0);

        private static bool IsPlausibleYear(int year) => year >= 1880 && year <= DateTime.UtcNow.Year + 2;

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) ? v : -1;
        }
    }
}
