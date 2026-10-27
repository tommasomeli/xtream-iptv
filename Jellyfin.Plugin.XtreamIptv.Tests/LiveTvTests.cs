using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.XtreamIptv.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using XtreamIptv;
using XtreamIptv.Client.Models;
using Xunit;

namespace Jellyfin.Plugin.XtreamIptv.Tests
{
    public class LiveTvChannelIdTests
    {
        [Fact]
        public void ChannelIdRoundTrips()
        {
            var id = XtreamTunerHost.BuildChannelId(4711);
            Assert.Equal("xtream-tuner_4711", id);
            Assert.True(XtreamTunerHost.TryParseStreamId(id, out var streamId));
            Assert.Equal(4711, streamId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("m3u_abc")]
        [InlineData("xtream-tuner_")]
        [InlineData("xtream-tuner_12a")]
        public void ForeignOrMalformedIdsAreRejected(string id)
        {
            Assert.False(XtreamTunerHost.TryParseStreamId(id, out _));
        }
    }

    public class LiveTvStreamUrlTests
    {
        private static PluginConfiguration Config(bool dispatcharr, string endpoint = "proxy", bool fallback = true, string baseUrl = "http://provider:8080", string dispatcharrUrl = "http://dispatcharr:9191")
            => new PluginConfiguration
            {
                BaseUrl = baseUrl,
                Username = "u",
                Password = "p",
                EnableDispatcharr = dispatcharr,
                DispatcharrUrl = dispatcharrUrl,
                DispatcharrStreamEndpoint = endpoint,
                DispatcharrFallbackToXtream = fallback,
                LiveTvOutputFormat = "ts",
            };

        [Fact]
        public void KnownChannelPlaysFromTheDispatcharrProxy()
        {
            var r = XtreamTunerHost.ResolveStreamUrl(Config(true), 5, new Dictionary<int, string> { [5] = "abc" });
            Assert.Equal(XtreamTunerHost.StreamRoute.DispatcharrProxy, r.Route);
            Assert.Equal("http://dispatcharr:9191/proxy/ts/stream/abc", r.Url);
        }

        [Fact]
        public void XtreamEndpointUsesTheConnectionUrlWhenItIsTheDispatcharrServer()
        {
            var r = XtreamTunerHost.ResolveStreamUrl(Config(true, "xtream", baseUrl: "http://dispatcharr:9191"), 5, new Dictionary<int, string> { [5] = "abc" });
            Assert.Equal(XtreamTunerHost.StreamRoute.DispatcharrXtream, r.Route);
            Assert.Equal("http://dispatcharr:9191/live/u/p/5.ts", r.Url);
            Assert.False(r.XtreamEndpointIgnored);
        }

        [Fact]
        public void XtreamEndpointIsIgnoredOnAnotherServer()
        {
            var r = XtreamTunerHost.ResolveStreamUrl(Config(true, "xtream"), 5, new Dictionary<int, string> { [5] = "abc" });
            Assert.Equal(XtreamTunerHost.StreamRoute.DispatcharrProxy, r.Route);
            Assert.True(r.XtreamEndpointIgnored);
        }

        [Fact]
        public void UnknownChannelFallsBackOrIsSkipped()
        {
            var fallback = XtreamTunerHost.ResolveStreamUrl(Config(true), 9, new Dictionary<int, string>());
            Assert.Equal(XtreamTunerHost.StreamRoute.DirectXtream, fallback.Route);
            Assert.Equal("http://provider:8080/live/u/p/9.ts", fallback.Url);

            var skipped = XtreamTunerHost.ResolveStreamUrl(Config(true, fallback: false), 9, new Dictionary<int, string>());
            Assert.Equal(XtreamTunerHost.StreamRoute.Skipped, skipped.Route);
            Assert.Null(skipped.Url);
        }

        [Fact]
        public void WithoutDispatcharrTheDirectUrlFollowsTheOutputFormat()
        {
            var config = Config(false);
            config.LiveTvOutputFormat = "hls";
            var r = XtreamTunerHost.ResolveStreamUrl(config, 9, null);
            Assert.Equal("http://provider:8080/live/u/p/9.m3u8", r.Url);
        }

        [Theory]
        [InlineData("http://a:1/x", "http://a:1/x/", true)]
        [InlineData("http://a:1/x", "http://a:1/y", false)]
        [InlineData("http://a:1", "https://a:1", false)]
        [InlineData("not a url", "http://a", false)]
        public void SameServerComparesSchemeHostPortAndPath(string a, string b, bool expected)
        {
            Assert.Equal(expected, XtreamTunerHost.IsSameServer(a, b));
        }
    }

    public class LiveTvMediaSourceTests
    {
        private static StreamStatsInfo Stats() => new StreamStatsInfo
        {
            Resolution = "1920x1080",
            VideoCodec = "hevc",
            AudioCodec = "ac3",
            SourceFps = 50,
            Bitrate = 8000,
            AudioChannels = "5.1",
            VideoProfile = "Main",
            VideoLevel = 41,
        };

        [Fact]
        public void StatsDeclareIndexedStreamsSoJellyfinDoesNotProbe()
        {
            var source = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions
            {
                StreamId = 7, StreamUrl = "http://d/proxy/ts/stream/x", Stats = Stats(), DisableProbing = true, DeclaredVideoCodec = "h264",
            });

            Assert.Equal("xtream_live_7", source.Id);
            Assert.False(source.SupportsProbing);
            Assert.Contains(source.MediaStreams, s => s.Index != -1);
            var video = source.MediaStreams.Single(s => s.Type == MediaStreamType.Video);
            Assert.Equal("h264", video.Codec);
            Assert.Equal(1920, video.Width);
            Assert.Equal(50f, video.RealFrameRate);
            // The profile transcodes (hevc in, h264 out): the ingested codec's attributes do not carry over.
            Assert.Null(video.Profile);
            Assert.Null(video.Level);
            var audio = source.MediaStreams.Single(s => s.Type == MediaStreamType.Audio);
            Assert.Equal("ac3", audio.Codec);
            Assert.Equal(6, audio.Channels);
            Assert.Equal(1, source.DefaultAudioStreamIndex);
        }

        [Fact]
        public void MatchingDeclaredCodecKeepsTheCodecAttributes()
        {
            var source = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions
            {
                StreamId = 7, StreamUrl = "http://d/proxy/ts/stream/x", Stats = Stats(), DisableProbing = true, DeclaredVideoCodec = "hevc",
            });
            var video = source.MediaStreams.Single(s => s.Type == MediaStreamType.Video);
            Assert.Equal("Main", video.Profile);
            Assert.Equal(41, video.Level);
        }

        [Fact]
        public void NoStatsOnADirectUrlLeavesTheStreamsForProbing()
        {
            var source = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions
            {
                StreamId = 7, StreamUrl = "http://p/live/u/p/7.ts", Stats = null, DisableProbing = false,
            });
            Assert.True(source.SupportsProbing);
            Assert.All(source.MediaStreams, s => Assert.Equal(-1, s.Index));
            Assert.Null(source.DefaultAudioStreamIndex);
        }

        [Fact]
        public void NoStatsOnDispatcharrDeclaresTheCommonCodecsAndNeverProbes()
        {
            var source = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions
            {
                StreamId = 7, StreamUrl = "http://d/proxy/ts/stream/x", Stats = null, DisableProbing = true, FallbackBitrateMbps = 8,
            });
            Assert.False(source.SupportsProbing);
            Assert.Equal(0, source.AnalyzeDurationMs);
            Assert.Equal("h264", source.MediaStreams.Single(s => s.Type == MediaStreamType.Video).Codec);
            Assert.Equal("aac", source.MediaStreams.Single(s => s.Type == MediaStreamType.Audio).Codec);
            Assert.Equal(8_192_000, source.Bitrate);
        }

        [Fact]
        public void AudioOnlyChannelHasNoVideoStream()
        {
            var stats = new StreamStatsInfo { AudioCodec = "aac", AudioBitrate = 128, AudioChannels = "stereo" };
            var source = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions { StreamId = 1, StreamUrl = "http://d/x", Stats = stats, DisableProbing = true });
            Assert.DoesNotContain(source.MediaStreams, s => s.Type == MediaStreamType.Video);
            var audio = source.MediaStreams.Single();
            Assert.Equal(0, audio.Index);
            Assert.Equal(2, audio.Channels);
            Assert.Equal(128000, audio.BitRate);
            Assert.Equal(0, source.DefaultAudioStreamIndex);
        }

        [Fact]
        public void ForceAudioTranscodeDisablesDirectStreamOnAc3OrUnknownAudio()
        {
            var ac3 = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions { StreamId = 1, StreamUrl = "http://d/x", Stats = Stats(), ForceAudioTranscode = true });
            Assert.False(ac3.SupportsDirectStream);
            var unknown = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions { StreamId = 1, StreamUrl = "http://d/x", Stats = null, ForceAudioTranscode = true });
            Assert.False(unknown.SupportsDirectStream);
            var aac = Stats();
            aac.AudioCodec = "aac";
            var fine = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions { StreamId = 1, StreamUrl = "http://d/x", Stats = aac, ForceAudioTranscode = true });
            Assert.True(fine.SupportsDirectStream);
        }

        [Fact]
        public void DvbSubtitlesAndUserAgentAreDeclaredWhenAsked()
        {
            var source = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions { StreamId = 1, StreamUrl = "http://d/x", Stats = Stats(), DeclareDvbSubtitles = true, UserAgent = "VLC" });
            Assert.Equal(2, source.MediaStreams.Count(s => s.Type == MediaStreamType.Subtitle));
            Assert.Equal("VLC", source.RequiredHttpHeaders["User-Agent"]);
        }

        [Fact]
        public void TunerSettingsCarryOver()
        {
            var tuner = new TunerHostInfo { IgnoreDts = true, AllowFmp4TranscodingContainer = true, FallbackMaxStreamingBitrate = 30000000 };
            var source = XtreamTunerHost.CreateMediaSourceInfo(new XtreamTunerHost.MediaSourceOptions { StreamId = 1, StreamUrl = "http://d/x", Tuner = tuner });
            Assert.True(source.IgnoreDts);
            Assert.False(source.UseMostCompatibleTranscodingProfile);
            Assert.Equal(30000000, source.FallbackMaxStreamingBitrate);
        }

        [Theory]
        [InlineData(true, "auto", "h264", "hevc", "h264")]
        [InlineData(true, "", null, "hevc", "hevc")]
        [InlineData(true, "stats", "h264", "hevc", "hevc")]
        [InlineData(true, "hevc", "h264", "mpeg2video", "hevc")]
        [InlineData(false, "h264", "h264", "hevc", "hevc")]
        public void VideoCodecResolution(bool isDispatcharr, string source, string profile, string stats, string expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ResolveVideoCodec(isDispatcharr, source, profile, stats));
        }

        [Theory]
        [InlineData("5.1", 6)]
        [InlineData("stereo", 2)]
        [InlineData("mono", 1)]
        [InlineData("2", 2)]
        [InlineData("weird", null)]
        public void AudioChannelCounts(string layout, int? expected)
        {
            Assert.Equal(expected, XtreamTunerHost.ParseAudioChannelCount(layout));
        }
    }

    public class LiveTvChannelInfoTests
    {
        private static readonly PluginConfiguration DispatcharrConfig = new PluginConfiguration { EnableDispatcharr = true, EnableChannelNameCleaning = true };

        [Fact]
        public void DispatcharrNumberGroupAndCallSignAreApplied()
        {
            var tuner = new TunerHostInfo { Id = "t1" };
            var channel = new LiveStreamInfo { StreamId = 12, Num = 3, Name = "Rai 1 HD", StreamIcon = "http://logo/1.png", CategoryId = 4 };
            var info = XtreamTunerHost.BuildChannelInfo(channel, tuner, DispatcharrConfig,
                new Dictionary<int, string> { [4] = "Italy" },
                new Dictionary<int, double> { [12] = 2.1 },
                new Dictionary<int, string> { [12] = "rai1.it" });

            Assert.Equal("xtream-tuner_12", info.Id);
            Assert.Equal("12", info.TunerChannelId);
            Assert.Equal("2.1", info.Number);
            Assert.Equal("rai1.it", info.CallSign);
            Assert.Equal("Italy", info.ChannelGroup);
            Assert.Equal("t1", info.TunerHostId);
            Assert.Equal("http://logo/1.png", info.ImageUrl);
        }

        [Fact]
        public void WholeDispatcharrNumbersPrintWithoutDecimals()
        {
            var channel = new LiveStreamInfo { StreamId = 12, Num = 3, Name = "Rai 1" };
            var info = XtreamTunerHost.BuildChannelInfo(channel, null, DispatcharrConfig, null, new Dictionary<int, double> { [12] = 5.0 }, null);
            Assert.Equal("5", info.Number);
        }

        [Fact]
        public void WithoutDispatcharrTheXtreamNumberIsUsed()
        {
            var channel = new LiveStreamInfo { StreamId = 12, Num = 3, Name = "Rai 1" };
            var info = XtreamTunerHost.BuildChannelInfo(channel, null, new PluginConfiguration(), null, new Dictionary<int, double> { [12] = 9 }, null);
            Assert.Equal("3", info.Number);
            Assert.Null(info.CallSign);
            Assert.Null(info.ImageUrl);
        }
    }

    public class LiveTvProgramTests
    {
        private static EpgProgram Program(long start, long stop, string title = "Show", bool plain = true)
            => new EpgProgram { StartTimestamp = start, StopTimestamp = stop, Title = title, IsPlainText = plain, Categories = new List<string> { "Sports" } };

        [Fact]
        public void ProgramsOutsideTheWindowOrReversedAreLeftOut()
        {
            var programs = new[]
            {
                Program(1_700_000_000, 1_700_003_600),
                Program(1_700_003_600, 1_700_007_200),
                Program(1_700_100_000, 1_700_100_100),
                Program(1_700_005_000, 1_700_004_000),
                Program(10, 20),
            };
            var result = XtreamListingsProvider.ConvertPrograms(programs, 5, "xtream-tuner_5", 1_700_001_000, 1_700_010_000);
            Assert.Equal(2, result.Count);
            Assert.All(result, p => Assert.Equal("xtream-tuner_5", p.ChannelId));
            Assert.Equal("xtream_epg_5_1700000000", result[0].Id);
            Assert.True(result[0].IsSports);
            Assert.False(result[0].IsSeries);
        }

        [Fact]
        public void Base64TitlesAreDecoded()
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Telegiornale"));
            var result = XtreamListingsProvider.ConvertPrograms(new[] { Program(100_000_000_0, 100_000_360_0, encoded, plain: false) }, 1, "xtream-tuner_1", 0, long.MaxValue);
            Assert.Equal("Telegiornale", result.Single().Name);
        }

        [Fact]
        public void SeriesGetAStableSeriesIdAndMoviesDoNot()
        {
            var series = new EpgProgram { StartTimestamp = 1_700_000_000, StopTimestamp = 1_700_003_600, Title = "Drama", IsPlainText = true, Categories = new List<string> { "Series" } };
            var movie = new EpgProgram { StartTimestamp = 1_700_000_000, StopTimestamp = 1_700_003_600, Title = "Film", IsPlainText = true, Categories = new List<string> { "Movie" } };
            var converted = XtreamListingsProvider.ConvertPrograms(new[] { series, movie }, 1, "c", 0, long.MaxValue);
            Assert.Equal("drama", converted[0].SeriesId);
            Assert.True(converted[0].IsSeries);
            Assert.Null(converted[1].SeriesId);
            Assert.True(converted[1].IsMovie);
        }
    }

    public class LiveTvPlaylistTests
    {
        [Fact]
        public void TvgIdPrefersDispatcharrThenEpgIdThenStreamId()
        {
            var config = new PluginConfiguration { BaseUrl = "http://p", Username = "u", Password = "p", LiveTvOutputFormat = "ts", EnableChannelNameCleaning = false };
            var channels = new List<LiveStreamInfo>
            {
                new LiveStreamInfo { StreamId = 1, Num = 1, Name = "A", EpgChannelId = "a.epg" },
                new LiveStreamInfo { StreamId = 2, Num = 2, Name = "B", EpgChannelId = "b.epg" },
                new LiveStreamInfo { StreamId = 3, Num = 3, Name = "C", CategoryId = 9 },
            };
            var m3u = LiveTvService.GenerateM3U(channels, config, new Dictionary<int, string> { [9] = "News" }, new Dictionary<int, string> { [1] = "a.dispatcharr" });
            Assert.Contains("tvg-id=\"a.dispatcharr\"", m3u);
            Assert.Contains("tvg-id=\"b.epg\"", m3u);
            Assert.Contains("tvg-id=\"3\"", m3u);
            Assert.Contains("group-title=\"News\"", m3u);
            Assert.Contains("http://p/live/u/p/3.ts", m3u);
        }

        [Fact]
        public void ChannelFiltersHonourCategoriesAndAdultFlag()
        {
            var config = new PluginConfiguration { SelectedLiveCategoryIds = new[] { 1 }, IncludeAdultChannels = false };
            var channels = new List<LiveStreamInfo>
            {
                new LiveStreamInfo { StreamId = 1, CategoryId = 1 },
                new LiveStreamInfo { StreamId = 2, CategoryId = 2 },
                new LiveStreamInfo { StreamId = 3, CategoryId = 1, IsAdult = 1 },
            };
            var filtered = LiveTvService.ApplyChannelFilters(channels, config);
            Assert.Single(filtered);
            Assert.Equal(1, filtered[0].StreamId);
        }
    }
}
