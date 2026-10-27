using System.Collections.Generic;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Linq;
using Emby.XtreamIptv.Catalog;
using XtreamIptv.Client.Models;
using Xunit;

namespace Emby.XtreamIptv.Tests
{
    public class CatalogNamingTests
    {
        [Theory]
        [InlineData("Inception (2010) [4K]", "Inception (2010)")]
        [InlineData("Inception 1080p", "Inception")]
        [InlineData("Inception - FHD", "Inception")]
        [InlineData("Inception (HDR) (2010)", "Inception (2010)")]
        [InlineData("Inception H.265 HEVC 2160p", "Inception")]
        [InlineData("Heidi SD", "Heidi")]
        [InlineData("HDR Movie Title", "Movie Title")]
        [InlineData("Shadow   Hunters  ", "Shadow Hunters")]
        public void CleanTitle_StripsQualityTagsAndSeparators(string raw, string expected)
        {
            Assert.Equal(expected, CatalogNaming.CleanTitle(raw, null));
        }

        [Fact]
        public void CleanTitle_KeepsWordsThatOnlyContainATag()
        {
            Assert.Equal("The 4Kids Show", CatalogNaming.CleanTitle("The 4Kids Show", null));
            Assert.Equal("HDR10 Story", CatalogNaming.CleanTitle("HDR10 Story", null));
        }

        [Fact]
        public void CleanTitle_RemovesUserTermsCommaOrNewlineSeparated()
        {
            Assert.Equal("Inception", CatalogNaming.CleanTitle("IT - Inception", "IT -"));
            Assert.Equal("Inception", CatalogNaming.CleanTitle("[NF] Inception (DUB)", "[NF],(DUB)"));
            Assert.Equal("Inception", CatalogNaming.CleanTitle("IT | Inception", "IT |\n"));
        }

        [Fact]
        public void CleanTitle_FallsBackToRawNameWhenEverythingWasRemoved()
        {
            Assert.Equal("4K", CatalogNaming.CleanTitle("4K", null));
        }

        [Fact]
        public void ExtractYear_PrefersTrailingBracketAndStripsIt()
        {
            var title = "Inception (2010)";
            var year = CatalogNaming.ExtractYear(ref title, "1999");
            Assert.Equal(2010, year);
            Assert.Equal("Inception", title);
        }

        [Fact]
        public void ExtractYear_UsesTheFieldWhenTheTitleHasNone()
        {
            var title = "Inception";
            Assert.Equal(2010, CatalogNaming.ExtractYear(ref title, "2010"));
            Assert.Equal("Inception", title);
            Assert.Equal(2010, CatalogNaming.ExtractYear(ref title, "2010-07-16"));
            Assert.Null(CatalogNaming.ExtractYear(ref title, ""));
            Assert.Null(CatalogNaming.ExtractYear(ref title, "n/a"));
        }

        [Fact]
        public void ExtractYear_TakesABracketYearAnywhereInTheNameAndRemovesIt()
        {
            var title = "Our unwritten Seoul (2025) SUB ITA";
            Assert.Equal(2025, CatalogNaming.ExtractYear(ref title, ""));
            Assert.Equal("Our unwritten Seoul SUB ITA", title);
            Assert.Equal("Our unwritten Seoul SUB ITA (2025)", CatalogNaming.DisplayName(title, 2025));

            title = "Dune [2021] Part One";
            Assert.Equal(2021, CatalogNaming.ExtractYear(ref title, "1984"));
            Assert.Equal("Dune Part One", title);
        }

        [Fact]
        public void ExtractYear_NameWinsOverTheFieldAndFirstOccurrenceWins()
        {
            var title = "Remake (2019) of the classic (1954)";
            Assert.Equal(2019, CatalogNaming.ExtractYear(ref title, "2001"));
            Assert.Equal("Remake of the classic", title);
        }

        [Fact]
        public void DisplayName_NeverDoublesATrailingYear()
        {
            Assert.Equal("Film (2020)", CatalogNaming.DisplayName("Film (2020)", 2020));
            Assert.Equal("Film (2020) (2021)", CatalogNaming.DisplayName("Film (2020)", 2021));
        }

        [Fact]
        public void BuildMovieEntries_FolderNameCarriesTheYearOnce()
        {
            var entries = CatalogNaming.BuildMovieEntries(new[] { Row(1, "Our unwritten Seoul (2025) SUB ITA") }, null, true);
            Assert.Equal("Our unwritten Seoul (2025)", entries.Single().DisplayName);
            Assert.Equal(2025, entries.Single().Year);
        }

        [Fact]
        public void ExtractYear_IgnoresImplausibleBracketYears()
        {
            var title = "Room (1024)";
            Assert.Null(CatalogNaming.ExtractYear(ref title, null));
            Assert.Equal("Room (1024)", title);
        }

        [Fact]
        public void DisplayName_AppendsYearWhenKnown()
        {
            Assert.Equal("Inception (2010)", CatalogNaming.DisplayName("Inception", 2010));
            Assert.Equal("Inception", CatalogNaming.DisplayName("Inception", null));
        }

        [Theory]
        [InlineData("A: B/C\\D?E*F\"G<H>I|J", "A BCDEFGHIJ")]
        [InlineData("  .Trailing dots..  ", "Trailing dots")]
        [InlineData("Tab\tInside", "TabInside")]
        public void SanitizeFileName_RemovesInvalidCharacters(string raw, string expected)
        {
            Assert.Equal(expected, CatalogNaming.SanitizeFileName(raw));
        }

        [Theory]
        [InlineData("1080p Inception", "1080P")]
        [InlineData("Inception [4K] HDR", "4K HDR")]
        [InlineData("Inception H265", "H.265")]
        [InlineData("Inception", "")]
        public void QualityLabel_ListsTheTagsOfARawName(string raw, string expected)
        {
            Assert.Equal(expected, CatalogNaming.QualityLabel(raw));
        }

        [Fact]
        public void IsExcludedCategoryName_MatchesTheUserTermsCaseInsensitively()
        {
            Assert.True(CatalogNaming.IsExcludedCategoryName("🇮🇹 Karaoke", "karaoke, Concerti"));
            Assert.True(CatalogNaming.IsExcludedCategoryName("Concerti Live", "karaoke, Concerti"));
            Assert.False(CatalogNaming.IsExcludedCategoryName("🇮🇹 Film", "karaoke, Concerti"));
            Assert.False(CatalogNaming.IsExcludedCategoryName("🇮🇹 Karaoke", ""));
            Assert.False(CatalogNaming.IsExcludedCategoryName(null, "karaoke"));
        }

        [Theory]
        [InlineData("Adult XXX", true)]
        [InlineData("FOR ADULTS", true)]
        [InlineData("Kids", false)]
        [InlineData(null, false)]
        public void IsAdultCategoryName_MatchesAdultWords(string name, bool expected)
        {
            Assert.Equal(expected, CatalogNaming.IsAdultCategoryName(name));
        }

        private static VodStreamInfo Row(int id, string name, string year = null)
            => new VodStreamInfo { StreamId = id, Name = name, Year = year ?? string.Empty, ContainerExtension = "mkv" };

        [Theory]
        [InlineData("47 Ronin Ita En", "47 Ronin")]
        [InlineData("Breaking Bad ITA con SUB ENG", "Breaking Bad")]
        [InlineData("Title ITA & ENG", "Title")]
        [InlineData("Title SUB ITA", "Title")]
        [InlineData("Title (2019) MULTI", "Title (2019)")]
        [InlineData("It", "It")]
        [InlineData("Io e Te", "Io e Te")]
        [InlineData("47 Metri -", "47 Metri")]
        [InlineData("48 Ore ()", "48 Ore")]
        public void CleanTitle_StripsTrailingLanguageMarkersOnly(string raw, string expected)
        {
            Assert.Equal(expected, CatalogNaming.CleanTitle(raw, null));
        }

        [Theory]
        [InlineData("47 Ronin Ita En", "ITA EN")]
        [InlineData("Breaking Bad ITA con SUB ENG", "ITA SUB ENG")]
        [InlineData("Title (2019) FHD SUB ITA", "FHD SUB ITA")]
        [InlineData("Title", "")]
        public void VersionLabel_JoinsQualityAndLanguage(string raw, string expected)
        {
            Assert.Equal(expected, CatalogNaming.VersionLabel(raw));
        }

        [Fact]
        public void BuildMovieEntries_YearlessReleaseJoinsTheOnlyDatedOneAndCaseDoesNotMatter()
        {
            var rows = new[] { Row(1, "47 Ronin", "2013"), Row(2, "47 Ronin Ita En"), Row(3, "40 Sono I Nuovi 20"), Row(4, "40 sono i nuovi 20 (2017) FHD") };
            var entries = CatalogNaming.BuildMovieEntries(rows, null, true);
            Assert.Equal(2, entries.Count);
            var ronin = entries.Single(e => e.Title == "47 Ronin");
            Assert.Equal(1, ronin.Primary.StreamId);
            Assert.Equal(2013, ronin.Year);
            Assert.Single(ronin.Versions);
            Assert.Equal(2, ronin.Versions[0].StreamId);
            var forty = entries.Single(e => e.Year == 2017);
            Assert.Equal("40 sono i nuovi 20", forty.Title);
            Assert.Single(forty.Versions);
            Assert.Equal(3, forty.Versions[0].StreamId);
        }

        [Fact]
        public void VersionsString_RoundTripsWithLabelsAndContainers()
        {
            var entry = new MovieEntry { Primary = Row(1, "47 Ronin", "2013"), Title = "47 Ronin", Year = 2013 };
            entry.Versions.Add(Row(2, "47 Ronin Ita En"));
            entry.Versions.Add(new VodStreamInfo { StreamId = 3, Name = "47 Ronin (2013) FHD", ContainerExtension = "mp4" });
            entry.Versions.Add(new VodStreamInfo { StreamId = 4, Name = "47 Ronin", ContainerExtension = "" });
            var text = CatalogNaming.VersionsString(entry);
            Assert.Equal("2.mkv:ITA EN|3.mp4:FHD|4.mp4:MP4", text);
            var parsed = CatalogNaming.ParseVersions(text);
            Assert.Equal(3, parsed.Count);
            Assert.Equal(2, parsed[0].StreamId);
            Assert.Equal("mkv", parsed[0].Extension);
            Assert.Equal("ITA EN", parsed[0].Label);
            Assert.Equal("MP4", parsed[2].Label);
            Assert.Empty(CatalogNaming.ParseVersions(null));
            Assert.Empty(CatalogNaming.ParseVersions("junk"));
        }

        [Fact]
        public void BuildMovieEntries_YearlessReleaseStaysApartWhenTwoRemakesExist()
        {
            var rows = new[] { Row(1, "Suspiria", "1977"), Row(2, "Suspiria", "2018"), Row(3, "Suspiria Ita En") };
            var entries = CatalogNaming.BuildMovieEntries(rows, null, true);
            Assert.Equal(3, entries.Count);
        }

        [Fact]
        public void BuildMovieEntries_MergesReleasesOfTheSameTitleAndYear()
        {
            var rows = new[]
            {
                Row(3, "Inception (2010) 4K"),
                Row(1, "Inception (2010)"),
                Row(2, "Inception (2010) 1080p"),
                Row(4, "Inception (2014)"),
                Row(5, "Inception", "2010"),
            };
            var entries = CatalogNaming.BuildMovieEntries(rows, null, true);

            Assert.Equal(2, entries.Count);
            var merged = entries.Single(e => e.Year == 2010);
            Assert.Equal(1, merged.Primary.StreamId);
            Assert.Equal(new[] { 2, 3, 5 }, merged.Versions.Select(v => v.StreamId).OrderBy(v => v).ToArray());
            Assert.Equal("Inception", merged.Title);
            Assert.Equal(4, entries.Single(e => e.Year == 2014).Primary.StreamId);
        }

        [Fact]
        public void BuildMovieEntries_UntaggedReleaseBecomesPrimaryEvenWhenListedLater()
        {
            var rows = new[] { Row(1, "Inception (2010) 4K"), Row(2, "Inception (2010)") };
            var entries = CatalogNaming.BuildMovieEntries(rows, null, true);
            Assert.Single(entries);
            Assert.Equal(2, entries[0].Primary.StreamId);
            Assert.Equal(1, entries[0].Versions.Single().StreamId);
        }

        [Fact]
        public void BuildMovieEntries_WithoutMergingEveryRowIsAMovie()
        {
            var rows = new[] { Row(1, "Inception (2010) 4K"), Row(2, "Inception (2010)") };
            Assert.Equal(2, CatalogNaming.BuildMovieEntries(rows, null, false).Count);
        }

        [Fact]
        public void BuildMovieEntries_DropsRowsWithoutAUsableTitle()
        {
            var rows = new[] { Row(1, "   "), Row(2, "???") };
            var entries = CatalogNaming.BuildMovieEntries(rows, null, true);
            Assert.Empty(entries);
        }

        [Fact]
        public void VersionFileNames_UseTheQualityLabelAndFallBackToNumbers()
        {
            var entry = new MovieEntry { Primary = Row(1, "Inception (2010)"), Title = "Inception", Year = 2010 };
            entry.Versions.Add(Row(2, "Inception (2010) 1080p"));
            entry.Versions.Add(Row(3, "Inception (2010) 1080p"));
            entry.Versions.Add(Row(4, "Inception (2010) DUB"));
            var names = CatalogNaming.VersionFileNames(entry, "Inception (2010)");
            Assert.Equal(new List<string>
            {
                "Inception (2010) - 1080P.strm",
                "Inception (2010) - Version 3.strm",
                "Inception (2010) - Version 4.strm",
            }, names);
        }

        [Theory]
        [InlineData("Breaking Bad (2008) - S01E01 - Pilot", "Breaking Bad (2008)", 1, 1, "Pilot")]
        [InlineData("Breaking Bad - S01E01 - Pilot", "Breaking Bad (2008)", 1, 1, "Pilot")]
        [InlineData("S01E01 - Pilot", "Breaking Bad (2008)", 1, 1, "Pilot")]
        [InlineData("Breaking Bad - S01E01", "Breaking Bad (2008)", 1, 1, "")]
        [InlineData("Pilot", "Breaking Bad (2008)", 1, 1, "Pilot")]
        [InlineData("S02E05 Other", "Breaking Bad (2008)", 1, 1, "Other")]
        [InlineData("", "Breaking Bad (2008)", 1, 1, "")]
        public void CleanEpisodeTitle_DropsARepeatedPrefix(string raw, string series, int s, int e, string expected)
        {
            Assert.Equal(expected, CatalogNaming.CleanEpisodeTitle(raw, series, s, e));
        }

        [Theory]
        [InlineData("Breaking Bad (2008) ITA con SUB ENG - S01E01 - Questione di chimica", "Questione di chimica")]
        [InlineData("Breaking Bad ITA S01E01 - Questione di chimica", "Questione di chimica")]
        [InlineData("breaking bad - s01e01 - Questione di chimica", "Questione di chimica")]
        [InlineData("[NF] Breaking Bad - S01E01: Questione di chimica", "Questione di chimica")]
        [InlineData("Breaking Bad - 1x01 - Questione di chimica", "Questione di chimica")]
        [InlineData("1x01 Questione di chimica", "Questione di chimica")]
        [InlineData("S01E01 Questione di chimica", "Questione di chimica")]
        [InlineData("Breaking Bad (2008) ITA con SUB ENG - S01E01", "")]
        [InlineData("Breaking Bad - 1x01", "")]
        [InlineData("S01E01", "")]
        [InlineData("The 2x4 Problem", "The 2x4 Problem")]
        [InlineData("Questione di chimica", "Questione di chimica")]
        public void CleanEpisodeTitle_StripsAnyPrefixUpToTheEpisodeCode(string raw, string expected)
        {
            Assert.Equal(expected, CatalogNaming.CleanEpisodeTitle(raw, "Breaking Bad ITA con SUB ENG (2008)", 1, 1));
        }

        [Fact]
        public void CleanEpisodeTitle_WithoutACode_DropsOnlyTheSeriesNamePrefix()
        {
            Assert.Equal("Pilot", CatalogNaming.CleanEpisodeTitle("Breaking Bad (2008) - Pilot", "Breaking Bad (2008)", 1, 1));
            Assert.Equal("Breaking Bad Returns", CatalogNaming.CleanEpisodeTitle("Breaking Bad Returns", "Breaking Bad (2008)", 1, 1));
        }

        [Fact]
        public void EpisodeBaseName_FollowsTheSeasonEpisodePattern()
        {
            Assert.Equal("Breaking Bad (2008) - S01E07 - Pilot Part 1", CatalogNaming.EpisodeBaseName("Breaking Bad (2008)", 1, 7, "Pilot: Part 1"));
            Assert.Equal("Breaking Bad (2008) - S00E03", CatalogNaming.EpisodeBaseName("Breaking Bad (2008)", 0, 3, ""));
            Assert.Equal("Season 00", CatalogNaming.SeasonFolderName(0));
        }

        [Fact]
        public void RuntimeMinutes_FromSecondsOrClockString()
        {
            Assert.Equal(45, CatalogNaming.RuntimeMinutes(2700, null));
            Assert.Equal(90, CatalogNaming.RuntimeMinutes(null, "01:30:00"));
            Assert.Null(CatalogNaming.RuntimeMinutes(null, "junk"));
            Assert.Null(CatalogNaming.RuntimeMinutes(0, ""));
        }
    }
}
