using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.XtreamIptv.Catalog;
using XtreamIptv;
using XtreamIptv.Client.Models;
using Xunit;

namespace Jellyfin.Plugin.XtreamIptv.Tests
{
    public class DetailOnOpenFilterTests
    {
        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(8, 8)]
        [InlineData(30, 30)]
        [InlineData(90, 30)]
        public void WaitCap_ClampsToZeroThirty(int configured, int expected)
        {
            Assert.Equal(TimeSpan.FromSeconds(expected), DetailOnOpenFilter.WaitCap(configured));
        }

        [Fact]
        public void ParseId_AcceptsGuidsWithAndWithoutDashes()
        {
            var g = Guid.NewGuid();
            Assert.Equal(g, DetailOnOpenFilter.ParseId(g.ToString("N")));
            Assert.Equal(g, DetailOnOpenFilter.ParseId(g.ToString("D")));
            Assert.Equal(g, DetailOnOpenFilter.ParseId(g));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("12345")]
        [InlineData("not-a-guid")]
        [InlineData("00000000000000000000000000000000")]
        public void ParseId_RejectsAnythingElse(string value)
        {
            Assert.Null(DetailOnOpenFilter.ParseId(value));
        }
    }

    public class DetailOnOpenServiceTests
    {
        [Fact]
        public void NeedsPhoto_OnlyWithoutImageAndNotAskedRecently()
        {
            var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(DetailOnOpenService.NeedsPhoto(false, DateTime.MinValue, now));
            Assert.True(DetailOnOpenService.NeedsPhoto(false, now - DetailOnOpenService.PhotoRetryAfter, now));
            Assert.False(DetailOnOpenService.NeedsPhoto(false, now - TimeSpan.FromHours(1), now));
            Assert.False(DetailOnOpenService.NeedsPhoto(true, DateTime.MinValue, now));
        }
    }

    public class CatalogSyncServiceTests
    {
        private static PluginConfiguration Config(bool adult = false, string excluded = "")
            => new PluginConfiguration { IncludeAdultContent = adult, ExcludedCategoryTerms = excluded };

        private static Category Cat(int id, string name) => new Category { CategoryId = id, CategoryName = name };

        [Fact]
        public void ExcludedCategories_AdultAndTermsUnlessAllowed()
        {
            var cats = new[] { Cat(1, "Action"), Cat(2, "XXX Adult"), Cat(3, "Karaoke Hits"), Cat(4, "Saga: Rocky") };
            var excluded = CatalogSyncService.ExcludedCategories(Config(false, "Karaoke, Saga:"), cats);
            Assert.Equal(new[] { 2, 3, 4 }, excluded.OrderBy(i => i));
            var withAdult = CatalogSyncService.ExcludedCategories(Config(true, "Karaoke"), cats);
            Assert.Equal(new[] { 3 }, withAdult.OrderBy(i => i));
            Assert.Empty(CatalogSyncService.ExcludedCategories(Config(), null));
        }

        [Fact]
        public void NewCategories_NoneOnTheFirstRun_ThenOnlyUnknownNotExcluded()
        {
            var current = new[] { Cat(1, "A"), Cat(2, "B"), Cat(3, "C"), Cat(3, "C again") };
            Assert.Empty(CatalogSyncService.NewCategories(current, new HashSet<int>(), new HashSet<int>()));
            Assert.Empty(CatalogSyncService.NewCategories(current, null, new HashSet<int>()));
            var added = CatalogSyncService.NewCategories(current, new HashSet<int> { 1 }, new HashSet<int> { 2 });
            Assert.Equal(new[] { 3 }, added.Select(c => c.CategoryId));
        }

        [Theory]
        [InlineData(0, 100, 0.2, false)]
        [InlineData(5, 100, 0.2, true)]
        [InlineData(30, 100, 0.2, false)]
        [InlineData(30, 100, 0, true)]
        [InlineData(5, 5, 0.2, true)]
        public void RemovalAllowed_FollowsTheThreshold(int candidates, int existing, double threshold, bool expected)
        {
            Assert.Equal(expected, CatalogSyncService.RemovalAllowed(candidates, existing, threshold));
        }

        [Fact]
        public void IsSelected_EmptySelectionMeansAll()
        {
            Assert.True(CatalogSyncService.IsSelected(7, new HashSet<int>()));
            Assert.True(CatalogSyncService.IsSelected(null, new HashSet<int>()));
            Assert.True(CatalogSyncService.IsSelected(7, new HashSet<int> { 7 }));
            Assert.False(CatalogSyncService.IsSelected(8, new HashSet<int> { 7 }));
            Assert.False(CatalogSyncService.IsSelected(null, new HashSet<int> { 7 }));
        }

        [Fact]
        public void Validate_NamesTheMissingPiece()
        {
            Assert.Equal("No configuration", CatalogSyncService.Validate(null));
            Assert.Equal("Xtream connection not configured", CatalogSyncService.Validate(new PluginConfiguration()));
            var c = new PluginConfiguration { BaseUrl = "http://x", Username = "u", Password = "p", LibraryPath = "" };
            Assert.Equal("Library path not configured", CatalogSyncService.Validate(c));
            c.LibraryPath = "/config/xtream";
            c.SyncMovies = false; c.SyncSeries = false;
            Assert.Equal("Neither movies nor series are enabled", CatalogSyncService.Validate(c));
            c.SyncMovies = true;
            Assert.Null(CatalogSyncService.Validate(c));
        }

        [Fact]
        public void SerializeCategoryCache_KeepsIdAndNameOnly()
        {
            var json = CatalogSyncService.SerializeCategoryCache(new[] { Cat(5, "Drama") });
            Assert.Equal("[{\"CategoryId\":5,\"CategoryName\":\"Drama\"}]", json);
        }
    }

    public class JellyfinCatalogTests
    {
        [Fact]
        public void BuildLibraryOptions_DisablesEveryFetcherAndExtraction()
        {
            var o = JellyfinCatalog.BuildLibraryOptions("/config/xtream/Movies");
            Assert.Equal("/config/xtream/Movies", o.PathInfos.Single().Path);
            Assert.False(o.EnableRealtimeMonitor);
            Assert.False(o.EnableChapterImageExtraction);
            Assert.False(o.ExtractChapterImagesDuringLibraryScan);
            Assert.False(o.EnableTrickplayImageExtraction);
            Assert.False(o.ExtractTrickplayImagesDuringLibraryScan);
            Assert.False(o.SaveLocalMetadata);
            Assert.Equal(0, o.AutomaticRefreshIntervalDays);
            Assert.Empty(o.MetadataSavers);
            Assert.Equal(new[] { "Movie", "Series", "Season", "Episode" }, o.TypeOptions.Select(t => t.Type));
            Assert.All(o.TypeOptions, t =>
            {
                Assert.Empty(t.MetadataFetchers);
                Assert.Empty(t.ImageFetchers);
            });
        }

        [Fact]
        public void People_DirectorFirst_ActorsCapped_NoDuplicateNames()
        {
            var cast = string.Join(", ", Enumerable.Range(1, 20).Select(i => "Actor " + i)) + ", Jane Doe";
            var people = JellyfinCatalog.People(cast, "Jane Doe");
            Assert.Equal("Jane Doe", people[0].Name);
            Assert.Equal(Jellyfin.Data.Enums.PersonKind.Director, people[0].Type);
            Assert.Equal(16, people.Count);
            Assert.Equal(1, people.Count(p => p.Name == "Jane Doe"));
            Assert.All(people.Skip(1), p => Assert.Equal(Jellyfin.Data.Enums.PersonKind.Actor, p.Type));
        }

        [Theory]
        [InlineData("2021-05-03", 2021, 5, 3)]
        [InlineData("2021", 2021, 1, 1)]
        [InlineData("03/05/2021", 2021, 5, 3)]
        public void Date_ParsesProviderFormatsAsUtc(string value, int y, int m, int d)
        {
            var date = JellyfinCatalog.Date(value);
            Assert.NotNull(date);
            Assert.Equal(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc), date.Value);
            Assert.Equal(DateTimeKind.Utc, date.Value.Kind);
        }

        [Fact]
        public void Date_RejectsGarbage()
        {
            Assert.Null(JellyfinCatalog.Date(""));
            Assert.Null(JellyfinCatalog.Date("n/a"));
        }

        [Theory]
        [InlineData("7.5", 7.5f)]
        [InlineData("0", null)]
        [InlineData("11", null)]
        [InlineData("", null)]
        public void Rating_OnlyWithinOneToTen(string value, float? expected)
        {
            Assert.Equal(expected, JellyfinCatalog.Rating(value));
        }

        [Theory]
        [InlineData("/config/xtream/Movies/", "/config/xtream/movies")]
        [InlineData("\\config\\Xtream", "/config/xtream")]
        [InlineData("", "")]
        public void Normalize_IgnoresCaseSlashesAndTrailingSeparator(string path, string expected)
        {
            Assert.Equal(expected, JellyfinCatalog.Normalize(path));
        }

        [Fact]
        public void Extension_DefaultsToMp4AndStripsTheDot()
        {
            Assert.Equal("mp4", JellyfinCatalog.Extension(null));
            Assert.Equal("mkv", JellyfinCatalog.Extension(".mkv"));
        }
    }

    public class LateDetailNotifierTests
    {
        [Fact]
        public void UpdatedIds_ItemFirstThenRelatedWithoutDuplicates()
        {
            var a = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid() };
            var b = new MediaBrowser.Controller.Entities.TV.Season { Id = Guid.NewGuid() };
            var ids = LateDetailNotifier.UpdatedIds(a, new MediaBrowser.Controller.Entities.BaseItem[] { b, a });
            Assert.Equal(new[] { a.Id.ToString("N"), b.Id.ToString("N") }, ids);
        }
    }
}
