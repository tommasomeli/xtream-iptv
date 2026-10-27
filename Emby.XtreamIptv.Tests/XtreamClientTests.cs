using System.Net.Http;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Threading;
using System.Threading.Tasks;
using XtreamIptv.Client;
using XtreamIptv.Client.Models;
using Emby.XtreamIptv.Tests.Fakes;
using Xunit;

namespace Emby.XtreamIptv.Tests
{
    public class XtreamClientTests
    {
        private static PluginConfiguration Config() => new PluginConfiguration
        {
            BaseUrl = "http://provider.example",
            Username = "u",
            Password = "p",
        };

        [Fact]
        public void ParseList_ReadsAnArrayWithMixedTypes()
        {
            var json = "[{\"stream_id\":\"7\",\"name\":\"Film\",\"category_id\":\"\",\"rating\":7.5,\"added\":\"123\",\"backdrop_path\":[\"http://a/b.jpg\"],\"is_adult\":\"1\"}]";
            int skipped;
            var list = XtreamClient.ParseList<VodStreamInfo>(json, out skipped);
            Assert.Single(list);
            Assert.Equal(7, list[0].StreamId);
            Assert.Null(list[0].CategoryId);
            Assert.Equal("7.5", list[0].Rating);
            Assert.Equal(123, list[0].Added);
            Assert.Equal("http://a/b.jpg", list[0].BackdropPath[0]);
            Assert.True(list[0].IsAdultContent);
            Assert.Equal(0, skipped);
        }

        [Fact]
        public void ParseList_SkipsRowsThatCannotBeRead()
        {
            var json = "[{\"stream_id\":1,\"name\":\"ok\"},{\"stream_id\":\"abc\",\"name\":\"bad id\"},5]";
            int skipped;
            var list = XtreamClient.ParseList<VodStreamInfo>(json, out skipped);
            Assert.Single(list);
            Assert.Equal(2, skipped);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{}")]
        [InlineData("{\"user_info\":{}}")]
        [InlineData("null")]
        public void ParseList_ReturnsEmptyForNonArrays(string json)
        {
            int skipped;
            Assert.Empty(XtreamClient.ParseList<SeriesInfo>(json, out skipped));
        }

        [Fact]
        public void ParseVodInfo_ToleratesAnEmptyArrayAsInfo()
        {
            var info = XtreamClient.ParseVodInfo("{\"info\":[],\"movie_data\":{\"stream_id\":1,\"name\":\"Film\"}}");
            Assert.Null(info.Info);
            Assert.Equal(1, info.MovieData.StreamId);
        }

        [Fact]
        public void ParseVodInfo_ReadsTheDetail()
        {
            var info = XtreamClient.ParseVodInfo("{\"info\":{\"plot\":\"p\",\"actors\":\"A\",\"backdrop_path\":\"http://a/b.jpg\",\"duration_secs\":\"\",\"rating\":6}}");
            Assert.Equal("p", info.Info.EffectivePlot);
            Assert.Equal("A", info.Info.EffectiveCast);
            Assert.Single(info.Info.BackdropPath);
            Assert.Null(info.Info.DurationSecs);
            Assert.Equal("6", info.Info.Rating);
            Assert.True(info.Info.HasAnyDetail);
        }

        [Fact]
        public async Task GetSeriesInfo_ParsesSeasonsKeyedEpisodes()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=get_series_info&series_id=5",
                "{\"info\":{\"name\":\"Show\",\"tmdb\":\"1\"},\"episodes\":{\"1\":[{\"id\":\"11\",\"episode_num\":\"1\",\"title\":\"Pilot\",\"info\":{\"duration_secs\":2700,\"movie_image\":\"http://i/e.jpg\",\"releasedate\":\"2008-01-20\"}}],\"0\":[]}}");
            var client = new XtreamClient(new RecordingLogger(), _ => new HttpClient(handler));

            var detail = await client.GetSeriesInfoAsync(Config(), 5, CancellationToken.None);

            Assert.Equal("Show", detail.Info.Name);
            Assert.Single(detail.Episodes);
            var ep = detail.Episodes["1"][0];
            Assert.Equal(11, ep.Id);
            Assert.Equal(2700, ep.DurationSecs);
            Assert.Equal("http://i/e.jpg", ep.Image);
            Assert.Equal("2008-01-20", ep.AirDate);
        }

        [Fact]
        public async Task ListCalls_UseTheLongTimeoutAndDetailCallsTheShortOne()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=get_vod_streams", "[]");
            handler.RespondWith("action=get_vod_info", "{}");
            var timeouts = new System.Collections.Generic.List<int>();
            var client = new XtreamClient(new RecordingLogger(), t => { timeouts.Add(t); return new HttpClient(handler); });

            await client.GetVodStreamsAsync(Config(), CancellationToken.None);
            await client.GetVodInfoAsync(Config(), 1, CancellationToken.None);

            Assert.Equal(new[] { XtreamClient.ListTimeoutSeconds, XtreamClient.DetailTimeoutSeconds }, timeouts.ToArray());
            Assert.Contains(handler.ReceivedUrls, u => u.Contains("username=u&password=p&action=get_vod_streams"));
        }

        [Fact]
        public async Task HttpErrors_Throw()
        {
            var handler = new FakeHttpHandler();
            handler.RespondWith("action=get_series", "boom", System.Net.HttpStatusCode.InternalServerError);
            var client = new XtreamClient(new RecordingLogger(), _ => new HttpClient(handler));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSeriesAsync(Config(), CancellationToken.None));
        }
    }
}
