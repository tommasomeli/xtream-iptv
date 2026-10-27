using System.IO;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Linq;
using Emby.XtreamIptv.Catalog;
using XtreamIptv.Client.Models;
using Emby.XtreamIptv.Tests.Fakes;
using Xunit;

namespace Emby.XtreamIptv.Tests
{
    public class CatalogStateTests
    {
        private static MovieEntry Entry(string plot = "plot", long added = 10, string ext = "mp4")
        {
            return new MovieEntry
            {
                Primary = new VodStreamInfo { StreamId = 1, Name = "Film (2020)", Plot = plot, Cast = "A", CategoryId = 5, Added = added, ContainerExtension = ext },
                Title = "Film",
                Year = 2020,
            };
        }

        [Fact]
        public void MovieSignature_IsStableForEqualInput()
        {
            Assert.Equal(CatalogState.MovieSignature(Entry()), CatalogState.MovieSignature(Entry()));
        }

        [Fact]
        public void MovieSignature_ChangesWithTheFieldsTheItemsDependOn()
        {
            var baseline = CatalogState.MovieSignature(Entry());
            Assert.NotEqual(baseline, CatalogState.MovieSignature(Entry(plot: "other")));
            Assert.NotEqual(baseline, CatalogState.MovieSignature(Entry(added: 11)));
            Assert.NotEqual(baseline, CatalogState.MovieSignature(Entry(ext: "mkv")));

            var withVersion = Entry();
            withVersion.Versions.Add(new VodStreamInfo { StreamId = 2, Name = "Film (2020) 4K", ContainerExtension = "mp4" });
            Assert.NotEqual(baseline, CatalogState.MovieSignature(withVersion));

            var renamed = Entry();
            renamed.Title = "Other";
            Assert.NotEqual(baseline, CatalogState.MovieSignature(renamed));
        }

        [Fact]
        public void NewCategories_NoneOnFirstRunThenOnlyUnknownAndNotExcluded()
        {
            var current = new[] { new Category { CategoryId = 1, CategoryName = "Film" }, new Category { CategoryId = 2, CategoryName = "Saga" }, new Category { CategoryId = 3, CategoryName = "Karaoke" } };
            Assert.Empty(CatalogSyncService.NewCategories(current, new System.Collections.Generic.HashSet<int>(), new System.Collections.Generic.HashSet<int>()));
            var added = CatalogSyncService.NewCategories(current, new System.Collections.Generic.HashSet<int> { 1 }, new System.Collections.Generic.HashSet<int> { 3 });
            Assert.Single(added);
            Assert.Equal(2, added[0].CategoryId);
        }

        [Fact]
        public void ExcludedCategories_AdultAndTerms()
        {
            var config = new PluginConfiguration { ExcludedCategoryTerms = "karaoke" };
            var current = new[] { new Category { CategoryId = 1, CategoryName = "Film" }, new Category { CategoryId = 2, CategoryName = "Adult XXX" }, new Category { CategoryId = 3, CategoryName = "Karaoke" } };
            Assert.Equal(new[] { 2, 3 }, CatalogSyncService.ExcludedCategories(config, current).OrderBy(i => i).ToArray());
            config.IncludeAdultContent = true;
            Assert.Equal(new[] { 3 }, CatalogSyncService.ExcludedCategories(config, current).ToArray());
            Assert.Empty(CatalogSyncService.ExcludedCategories(config, null));
        }

        [Fact]
        public void Signatures_ChangeWithTheStreamBase()
        {
            var a = CatalogState.StreamBase(new PluginConfiguration { BaseUrl = "http://a:9191", Username = "u", Password = "p" });
            var b = CatalogState.StreamBase(new PluginConfiguration { BaseUrl = "http://b:9191", Username = "u", Password = "p" });
            var c = CatalogState.StreamBase(new PluginConfiguration { BaseUrl = "http://a:9191", Username = "u", Password = "q" });
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(a, CatalogState.StreamBase(new PluginConfiguration { BaseUrl = "http://a:9191", Username = "u", Password = "p" }));
            Assert.NotEqual(CatalogState.MovieSignature(Entry(), a), CatalogState.MovieSignature(Entry(), b));
            Assert.NotEqual(CatalogState.SeriesSignature("Show", 2008, 3, "c", "p", "c", a), CatalogState.SeriesSignature("Show", 2008, 3, "c", "p", "c", b));
        }

        [Fact]
        public void SeriesSignature_CoversNameYearCategoryCoverPlotAndCast()
        {
            var baseline = CatalogState.SeriesSignature("Show", 2008, 3, "http://a/c.jpg", "plot", "cast");
            Assert.Equal(baseline, CatalogState.SeriesSignature("Show", 2008, 3, "http://a/c.jpg", "plot", "cast"));
            Assert.NotEqual(baseline, CatalogState.SeriesSignature("Show", 2009, 3, "http://a/c.jpg", "plot", "cast"));
            Assert.NotEqual(baseline, CatalogState.SeriesSignature("Show", 2008, 4, "http://a/c.jpg", "plot", "cast"));
            Assert.NotEqual(baseline, CatalogState.SeriesSignature("Show", 2008, 3, "http://a/d.jpg", "plot", "cast"));
            Assert.NotEqual(baseline, CatalogState.SeriesSignature("Show", 2008, 3, "http://a/c.jpg", "longer plot", "cast"));
            Assert.NotEqual(baseline, CatalogState.SeriesSignature("Show", 2008, 3, "http://a/c.jpg", "plot", "other cast"));
        }

        [Fact]
        public void SaveAndLoad_RoundTripWithItemIdsVersionsStaleAndDetailed()
        {
            using (var temp = new TempDirectory())
            {
                var path = Path.Combine(temp.Path, "state.json");
                var state = CatalogState.Load(path);
                state.Movies["1"] = new MovieState { Signature = "sig", ItemId = 101, Versions = { { "2", 102 } } };
                state.Series["7"] = new SeriesState { LastModified = "123", Signature = "s", ItemId = 707, Stale = true, Episodes = 0 };
                state.MarkDetailed(1);
                state.Save();

                var loaded = CatalogState.Load(path);
                Assert.Equal("sig", loaded.Movies["1"].Signature);
                Assert.Equal(101, loaded.Movies["1"].ItemId);
                Assert.Equal(102, loaded.Movies["1"].Versions["2"]);
                Assert.Equal("123", loaded.Series["7"].LastModified);
                Assert.Equal(707, loaded.Series["7"].ItemId);
                Assert.True(loaded.Series["7"].Stale);
                Assert.True(loaded.IsDetailed(1));
                loaded.UnmarkDetailed(1);
                Assert.False(loaded.IsDetailed(1));
                Assert.False(loaded.IsDetailed(2));
                Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
            }
        }

        [Fact]
        public void SetSeriesLoaded_ClearsStaleAndStoresTheCount()
        {
            var state = new CatalogState();
            state.Series["7"] = new SeriesState { Stale = true };
            state.SetSeriesLoaded(7, 12);
            var s = state.FindSeries(7);
            Assert.False(s.Stale);
            Assert.Equal(12, s.Episodes);
            Assert.Null(state.FindSeries(8));
        }

        [Fact]
        public void Load_ReturnsAnEmptyStateForMissingCorruptOrOldFiles()
        {
            using (var temp = new TempDirectory())
            {
                Assert.Empty(CatalogState.Load(Path.Combine(temp.Path, "missing.json")).Movies);
                var corrupt = Path.Combine(temp.Path, "corrupt.json");
                File.WriteAllText(corrupt, "{ not json");
                Assert.Empty(CatalogState.Load(corrupt).Series);
                var old = Path.Combine(temp.Path, "old.json");
                File.WriteAllText(old, "{\"Version\":1,\"Movies\":{\"1\":{\"Signature\":\"x\",\"Folder\":\"Film\"}}}");
                Assert.Empty(CatalogState.Load(old).Movies);
            }
        }

        [Theory]
        [InlineData(0, 100, 0.2, false)]
        [InlineData(5, 100, 0.2, true)]
        [InlineData(21, 100, 0.2, false)]
        [InlineData(20, 100, 0.2, true)]
        [InlineData(9, 9, 0.2, true)]
        [InlineData(50, 100, 0, true)]
        public void RemovalAllowed_AppliesTheThresholdAboveTheMinimumSize(int candidates, int existing, double threshold, bool expected)
        {
            Assert.Equal(expected, CatalogSyncService.RemovalAllowed(candidates, existing, threshold));
        }

        [Fact]
        public void IsSelected_EmptySelectionMeansAll()
        {
            Assert.True(CatalogSyncService.IsSelected(5, new System.Collections.Generic.HashSet<int>()));
            Assert.True(CatalogSyncService.IsSelected(null, new System.Collections.Generic.HashSet<int>()));
            Assert.True(CatalogSyncService.IsSelected(5, new System.Collections.Generic.HashSet<int> { 5 }));
            Assert.False(CatalogSyncService.IsSelected(6, new System.Collections.Generic.HashSet<int> { 5 }));
            Assert.False(CatalogSyncService.IsSelected(null, new System.Collections.Generic.HashSet<int> { 5 }));
        }
    }
}
