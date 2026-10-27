using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Emby.XtreamIptv.Catalog;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Emby.XtreamIptv.Tests
{
    /// <summary>
    /// The plugin compiles against the 4.9 SDK and runs on 4.10, whose BaseItem lost the AlbumId
    /// setter. The resolver must pick the property path on 4.9 and the method path on 4.10.
    /// </summary>
    public class EmbyCompatTests
    {
        [Fact]
        public void OnTheReferenced49Sdk_ThePropertyPathIsChosen()
        {
            var album = EmbyCompat.ResolveAlbumLink(typeof(BaseItem));
            Assert.False(album.IsMethod);
            Assert.Equal("AlbumId", album.Name);

            var series = EmbyCompat.ResolveSeriesLink(typeof(BaseItem));
            Assert.False(series.IsMethod);
            Assert.Equal("SeriesId", series.Name);

            Assert.Equal(album.Name, EmbyCompat.AlbumMember.Name);
            Assert.Equal(series.Name, EmbyCompat.SeriesMember.Name);
        }

        [Fact]
        public void LinkToSeason_AndLinkToSeries_SetTheIdsOn49()
        {
            var series = new MediaBrowser.Controller.Entities.TV.Series { InternalId = 77 };
            var season = new MediaBrowser.Controller.Entities.TV.Season { InternalId = 78 };
            var episode = new MediaBrowser.Controller.Entities.TV.Episode();

            EmbyCompat.LinkToSeries(episode, series);
            EmbyCompat.LinkToSeason(episode, season);

            Assert.Equal(77, episode.SeriesId);
            Assert.Equal(78, episode.AlbumId);
        }

        [Fact]
        public void OnThe410Sdk_TheMethodPathIsChosen()
        {
            var lib = Find410Lib();
            Assert.True(lib != null, "lib/emby4_10 not found next to the test tree");

            var context = new SdkLoadContext(lib);
            var controller = context.LoadFromAssemblyPath(Path.Combine(lib, "MediaBrowser.Controller.dll"));
            var baseItem = controller.GetType("MediaBrowser.Controller.Entities.BaseItem", true);

            var album = EmbyCompat.ResolveAlbumLink(baseItem);
            Assert.True(album.IsMethod);
            Assert.Equal("SetAlbumItem", album.Name);

            var series = EmbyCompat.ResolveSeriesLink(baseItem);
            Assert.True(series.IsMethod);
            Assert.Equal("SetSeriesItem", series.Name);

            // The member the old code assigned directly must really be gone there.
            var albumId = baseItem.GetProperty("AlbumId", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(albumId);
            Assert.Null(albumId.GetSetMethod(false));
        }

        private static string Find410Lib()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "lib", "emby4_10", "MediaBrowser.Controller.dll");
                if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>Loads the 4.10 SDK beside the referenced 4.9 one; MediaBrowser.* dependencies come from the same folder.</summary>
        private sealed class SdkLoadContext : AssemblyLoadContext
        {
            private readonly string _lib;

            public SdkLoadContext(string lib) : base("emby4_10", isCollectible: false)
            {
                _lib = lib;
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                if (assemblyName.Name != null && assemblyName.Name.StartsWith("MediaBrowser.", StringComparison.Ordinal))
                {
                    var path = Path.Combine(_lib, assemblyName.Name + ".dll");
                    if (File.Exists(path)) return LoadFromAssemblyPath(path);
                }
                return null;
            }
        }
    }
}
