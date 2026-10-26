using System.Text.Json;
using Encore.MusicVideos;
using Encore.MusicVideos.Discovery;
using Encore.MusicVideos.Tools;
using Encore.MusicVideos.Videos;
using NLog;
using NzbDrone.Core.Music;
using Xunit;

namespace Encore.Tests
{
    public sealed class MusicVideoTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "musicvideos-tests-" + Guid.NewGuid().ToString("N"));
        private readonly Artist _artist;

        public MusicVideoTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Oasis"));
            _artist = new Artist { Id = 3, Path = Path.Combine(_root, "Oasis"), ForeignArtistId = "39ab1aed-75e0-4140-bd47-540276886b60" };
            _artist.Metadata = new NzbDrone.Core.Datastore.LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = "Oasis" });
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Theory]
        [InlineData("Oasis - Wonderwall (Official Video)", VideoKind.Official)]
        [InlineData("Oasis - Live Forever (Official HD Remastered Video)", VideoKind.Official)]
        [InlineData("The Weeknd - Blinding Lights (Official Music Video)", VideoKind.Official)]
        [InlineData("BTS (방탄소년단) 'Dynamite' Official MV", VideoKind.Official)]
        [InlineData("Daft Punk - Around The World (Official Music Video Remastered)", VideoKind.Official)]
        [InlineData("Stromae - Alors On Danse (Clip Officiel)", VideoKind.Official)]
        [InlineData("Radiohead - Creep [Video]", VideoKind.Official)]
        [InlineData("Oasis - Wonderwall (Live at Knebworth, 10 August 1996)", VideoKind.Live)]
        [InlineData("Billie Eilish - bad guy (Live From The Grammys)", VideoKind.Live)]
        [InlineData("Phoebe Bridgers: Tiny Desk Concert", VideoKind.Live)]
        [InlineData("Oasis - Wonderwall (Official Lyric Video)", VideoKind.Lyric)]
        [InlineData("Taylor Swift - Anti-Hero (Lyrics)", VideoKind.Lyric)]
        [InlineData("Oasis - Wonderwall (Official Audio)", VideoKind.Other)]
        [InlineData("Daft Punk - Around The World (Official Visualizer)", VideoKind.Other)]
        [InlineData("Oasis - Wonderwall (Official Video) - Behind The Scenes", VideoKind.Other)]
        [InlineData("Oasis - Wonderwall (Official Video) #shorts", VideoKind.Other)]
        [InlineData("Noel talks about Wonderwall", VideoKind.Other)]
        [InlineData("Wonderwall", VideoKind.Other)]
        public void Titles_are_classified(string title, VideoKind expected) =>
            Assert.Equal(expected, VideoTitles.Classify(title));

        [Theory]
        [InlineData("Oasis - Wonderwall (Official Video)", 0)]
        [InlineData("Oasis - Wonderwall (Official HD Remastered Video)", 3)]
        [InlineData("Rammstein - Du Hast (Official 4K Video)", 3)]
        [InlineData("Numb (Official Music Video) [4K UPGRADE] – Linkin Park", 5)]
        public void Titles_hint_at_better_copies(string title, int expected) =>
            Assert.Equal(expected, VideoTitles.QualityHint(title));

        [Fact]
        public void Max_resolution_caps_the_best_quality_sort() =>
            Assert.Equal("res:1080,fps,hdr:12,vcodec:vp9,vbr", YtDlp.FormatSort(new MusicVideosSettings { MaxResolution = (int)MaxResolution.P1080 }));

        [Theory]
        [InlineData("Oasis - Wonderwall (Official Video)", "Oasis", "Wonderwall")]
        [InlineData("Oasis - Live Forever (Official HD Remastered Video)", "Oasis", "Live Forever")]
        [InlineData("The Weeknd - Blinding Lights (Official Music Video) [4K]", "The Weeknd", "Blinding Lights")]
        [InlineData("Daft Punk – Get Lucky (Official Audio) ft. Pharrell Williams, Nile Rodgers", "Daft Punk", "Get Lucky ft. Pharrell Williams, Nile Rodgers")]
        [InlineData("Wonderwall (Official Video)", "Oasis", "Wonderwall")]
        [InlineData("Oasis - Wonderwall (Live at Knebworth, 10 August 1996)", "Oasis", "Wonderwall (Live at Knebworth, 10 August 1996)")]
        [InlineData("Simon & Garfunkel - The Boxer (Official Video) | HD", "Simon and Garfunkel", "The Boxer")]
        [InlineData("Jay-Z - 99 Problems", "Jay-Z", "99 Problems")]
        [InlineData("Someone Else - Wonderwall (Official Video)", "Oasis", "Someone Else - Wonderwall")]
        public void Titles_are_cleaned(string title, string artist, string expected) =>
            Assert.Equal(expected, VideoTitles.CleanTitle(title, artist));

        [Fact]
        public void Normalize_ignores_case_accents_punctuation_and_featured_artists()
        {
            Assert.Equal(VideoTitles.Normalize("Beyoncé"), VideoTitles.Normalize("BEYONCE"));
            Assert.Equal(VideoTitles.Normalize("Don't Stop Me Now"), VideoTitles.Normalize("dont stop me now"));
            Assert.Equal(VideoTitles.Normalize("Get Lucky"), VideoTitles.Normalize("Get Lucky (feat. Pharrell Williams)"));
        }

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=bx1Bh8ZvH84", "bx1Bh8ZvH84")]
        [InlineData("https://youtube.com/watch?feature=share&v=bx1Bh8ZvH84&t=10", "bx1Bh8ZvH84")]
        [InlineData("https://youtu.be/bx1Bh8ZvH84?si=abc", "bx1Bh8ZvH84")]
        [InlineData("https://music.youtube.com/watch?v=bx1Bh8ZvH84", "bx1Bh8ZvH84")]
        [InlineData("https://www.youtube.com/shorts/bx1Bh8ZvH84", "bx1Bh8ZvH84")]
        [InlineData("https://www.youtube.com/@oasis", null)]
        [InlineData("https://vimeo.com/12345", null)]
        public void YouTube_ids_are_read_from_addresses(string url, string? expected) =>
            Assert.Equal(expected, YouTube.IdFromUrl(url));

        [Theory]
        [InlineData("https://www.youtube.com/@oasis", true)]
        [InlineData("https://www.youtube.com/channel/UCmMfnMVi4f5wmFkNqfiUFvA", true)]
        [InlineData("https://www.youtube.com/user/oasisvevo", true)]
        [InlineData("https://www.youtube.com/playlist?list=PL123", true)]
        [InlineData("https://www.youtube.com/watch?v=bx1Bh8ZvH84&list=PL123", false)]
        [InlineData("https://www.youtube.com/watch?v=bx1Bh8ZvH84", false)]
        public void Channels_and_playlists_are_recognised(string url, bool expected) =>
            Assert.Equal(expected, YouTube.IsCollection(url));

        [Theory]
        [InlineData("https://www.youtube.com/@oasis", "https://www.youtube.com/@oasis/videos")]
        [InlineData("https://www.youtube.com/@oasis/", "https://www.youtube.com/@oasis/videos")]
        [InlineData("https://www.youtube.com/@oasis/videos", "https://www.youtube.com/@oasis/videos")]
        [InlineData("https://www.youtube.com/playlist?list=PL123", "https://www.youtube.com/playlist?list=PL123")]
        public void Channels_are_read_from_their_videos_tab(string url, string expected) =>
            Assert.Equal(expected, YouTube.VideosTab(url));

        [Fact]
        public void Downloads_ask_for_the_best_streams_and_never_keep_webm()
        {
            var settings = new MusicVideosSettings();
            var args = YtDlp.DownloadArguments(new ToolPaths("yt-dlp", "/usr/bin/ffmpeg", "deno:/bin/deno"), settings, "https://youtu.be/x", "/tmp/job");

            Assert.Equal("bv*+ba/b", After(args, "-f"));
            Assert.Equal("mkv", After(args, "--merge-output-format"));
            Assert.Equal("mkv", After(args, "--remux-video"));
            Assert.Equal("/usr/bin/ffmpeg", After(args, "--ffmpeg-location"));
            Assert.Equal("deno:/bin/deno", After(args, "--js-runtimes"));
            Assert.Equal("res,fps,hdr:12,vcodec:vp9,vbr", After(args, "-S"));
            Assert.Equal("https://youtu.be/x", args[^1]);
            Assert.Equal("--", args[^2]);
        }

        [Fact]
        public void Compatible_mode_prefers_h264_and_aac_within_the_resolution_limit()
        {
            var settings = new MusicVideosSettings
            {
                Container = (int)VideoContainer.Mp4,
                CodecPreference = (int)VideoCodecPreference.Compatible,
                MaxResolution = (int)MaxResolution.P1080,
                CookiesFile = "/config/cookies.txt",
                ExtraArguments = "--limit-rate 5M --sleep-interval '2'"
            };

            var args = YtDlp.DownloadArguments(new ToolPaths("yt-dlp", null, null), settings, "https://youtu.be/x", "/tmp/job");

            Assert.Equal("vcodec:h264,res:1080,fps,acodec:aac", After(args, "-S"));
            Assert.Equal("mp4", After(args, "--remux-video"));
            Assert.Equal("/config/cookies.txt", After(args, "--cookies"));
            Assert.Equal("5M", After(args, "--limit-rate"));
            Assert.Equal("2", After(args, "--sleep-interval"));
            Assert.DoesNotContain("--ffmpeg-location", args);
        }

        [Fact]
        public void Extra_arguments_are_split_like_a_shell()
        {
            Assert.Equal(["-a", "b c", "d", ""], YtDlp.SplitArguments("-a \"b c\"  d ''"));
            Assert.Empty(YtDlp.SplitArguments("   "));
        }

        [Fact]
        public void Playlist_entries_are_read()
        {
            var entries = YtDlp.ParseEntries(
            [
                "WARNING: something",
                YtDlp.EntryPrefix + "{\"id\": \"bx1Bh8ZvH84\", \"url\": \"https://www.youtube.com/watch?v=bx1Bh8ZvH84\", \"title\": \"Oasis - Wonderwall (Official Video)\", \"duration\": 258.0, \"channel\": \"Oasis\", \"uploader\": null}",
                YtDlp.EntryPrefix + "{\"id\": \"6hzrDeceEKc\", \"url\": \"6hzrDeceEKc\", \"title\": \"Oasis - Don't Look Back In Anger\", \"duration\": null, \"channel\": null, \"uploader\": \"OasisVEVO\"}",
                YtDlp.EntryPrefix + "{broken"
            ]);

            Assert.Equal(2, entries.Count);
            Assert.Equal(258, entries[0].Duration);
            Assert.Equal("https://www.youtube.com/watch?v=6hzrDeceEKc", entries[1].Url);
            Assert.Equal("OasisVEVO", entries[1].Channel);
        }

        [Fact]
        public void Download_results_are_read()
        {
            var result = YtDlp.ParseResult("{\"filepath\": \"/tmp/job/video.mkv\", \"width\": 3840, \"height\": 2160, \"fps\": 25, \"vcodec\": \"av01.0.12M.08\", \"acodec\": \"opus\", \"abr\": 130.5, \"vbr\": 9000.1, \"filesize\": null, \"filesize_approx\": 312345678, \"title\": \"Oasis - Wonderwall\", \"upload_date\": \"20091027\", \"release_year\": null, \"duration\": 258.4}");

            Assert.Equal("/tmp/job/video.mkv", result.File);
            Assert.Equal(2009, result.Year);
            Assert.Equal(258, result.Duration);
            Assert.Equal(312345678, result.Quality.Size);
            Assert.Equal("2160p · av01 · opus 131k", result.Quality.ToString());
        }

        [Fact]
        public void MusicBrainz_video_recordings_and_links_are_read()
        {
            using var search = JsonDocument.Parse("""
                {"count": 2, "recordings": [
                  {"id": "r1", "title": "Wonderwall", "length": 258000, "video": true, "disambiguation": "music video"},
                  {"id": "r2", "title": "Wonderwall", "video": false}
                ]}
                """);
            using var recording = JsonDocument.Parse("""
                {"relations": [
                  {"type": "free streaming", "url": {"resource": "https://www.youtube.com/watch?v=bx1Bh8ZvH84"}},
                  {"type": "free streaming", "ended": true, "url": {"resource": "https://www.youtube.com/watch?v=oldoldold00"}}
                ]}
                """);
            using var artist = JsonDocument.Parse("""
                {"relations": [
                  {"type": "youtube", "url": {"resource": "https://www.youtube.com/channel/UCmMfnMVi4f5wmFkNqfiUFvA"}},
                  {"type": "youtube", "url": {"resource": "https://www.youtube.com/user/oasisvevo"}},
                  {"type": "official homepage", "url": {"resource": "https://oasisinet.com/"}},
                  {"type": "free streaming", "url": {"resource": "https://www.youtube.com/watch?v=bx1Bh8ZvH84"}}
                ]}
                """);

            var recordings = MusicBrainz.ParseRecordings(search.RootElement);
            Assert.Single(recordings);
            Assert.Equal(new MbRecording("r1", "Wonderwall", "music video", 258), recordings[0]);

            Assert.Equal(["https://www.youtube.com/watch?v=bx1Bh8ZvH84"], MusicBrainz.ParseUrls(recording.RootElement));
            Assert.Equal(["https://www.youtube.com/channel/UCmMfnMVi4f5wmFkNqfiUFvA", "https://www.youtube.com/user/oasisvevo"],
                MusicBrainz.ParseVideoChannels(artist.RootElement));
        }

        [Fact]
        public void Channel_listings_become_cleaned_videos()
        {
            var video = VideoFinder.FromListing(_artist, new ListedVideo("bx1Bh8ZvH84", "https://www.youtube.com/watch?v=bx1Bh8ZvH84", "Oasis - Wonderwall (Official HD Remastered Video)", 258, "Oasis"), VideoFinder.SourceChannel);

            Assert.Equal("youtube:bx1Bh8ZvH84", video.Key);
            Assert.Equal("Wonderwall", video.Title);
            Assert.Equal(VideoKind.Official, video.Kind);
            Assert.Equal("https://i.ytimg.com/vi/bx1Bh8ZvH84/hqdefault.jpg", video.Thumbnail);
        }

        [Theory]
        [InlineData("{Artist Name} - {Track Title}", "Oasis - Wonderwall")]
        [InlineData("{Track Title}-video", "Wonderwall-video")]
        [InlineData("{Artist Name} - {Track Title} ({Video Type}, {Year})", "Oasis - Wonderwall (Official Video, 1995)")]
        public void File_names_follow_the_format(string format, string expected)
        {
            var video = new MusicVideo { Title = "Wonderwall", Kind = VideoKind.Official, Year = 1995 };
            Assert.Equal(expected, VideoNaming.FileName(format, "Oasis", video));
        }

        [Fact]
        public void File_names_are_made_safe()
        {
            Assert.Equal("AC-DC - Who Made Who - Live", VideoNaming.CleanFileName("AC/DC - Who Made Who: Live?"));
            Assert.Equal("Video", VideoNaming.CleanFileName("???"));
            Assert.Equal("Trailing", VideoNaming.CleanFileName("Trailing..."));
        }

        [Fact]
        public void Nfo_describes_the_video()
        {
            var video = new MusicVideo { Key = "youtube:bx1Bh8ZvH84", Url = "https://www.youtube.com/watch?v=bx1Bh8ZvH84", Title = "Wonderwall", Kind = VideoKind.Official, Year = 1995, Duration = 258, RecordingId = "r1" };
            var nfo = VideoNaming.Nfo("Oasis", "mbid", video);

            Assert.StartsWith("<?xml", nfo);
            Assert.Contains("<title>Wonderwall</title>", nfo);
            Assert.Contains("<artist>Oasis</artist>", nfo);
            Assert.Contains("<year>1995</year>", nfo);
            Assert.Contains("<runtime>4</runtime>", nfo);
            Assert.Contains("<uniqueid type=\"youtube\" default=\"true\">bx1Bh8ZvH84</uniqueid>", nfo);
            Assert.Contains("<musicbrainztrackid>r1</musicbrainztrackid>", nfo);
        }

        [Fact]
        public void Placed_videos_get_their_name_thumbnail_and_nfo_and_never_overwrite_another()
        {
            var settings = new MusicVideosSettings();
            var store = new VideoStore(LogManager.CreateNullLogger());
            var folder = Path.Combine(_artist.Path, "Music Videos");

            string Downloaded(string name)
            {
                var path = Path.Combine(_root, "tmp", name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, name);
                return path;
            }

            var official = new MusicVideo { Key = "youtube:a", Title = "Wonderwall", Kind = VideoKind.Official };
            var other = new MusicVideo { Key = "youtube:b", Title = "Wonderwall", Kind = VideoKind.Official };

            Assert.Equal("Oasis - Wonderwall.mkv", store.Place(_artist, official, Downloaded("video.mkv"), Downloaded("thumb.jpg"), settings));
            official.File = "Oasis - Wonderwall.mkv";
            Assert.Equal("Oasis - Wonderwall (2).mkv", store.Place(_artist, other, Downloaded("video.mkv"), null, settings));

            Assert.True(File.Exists(Path.Combine(folder, "Oasis - Wonderwall.mkv")));
            Assert.True(File.Exists(Path.Combine(folder, "Oasis - Wonderwall-thumb.jpg")));
            Assert.True(File.Exists(Path.Combine(folder, "Oasis - Wonderwall.nfo")));
            Assert.True(File.Exists(Path.Combine(folder, "Oasis - Wonderwall (2).mkv")));

            // Downloading the same video again replaces it under the same name
            Assert.Equal("Oasis - Wonderwall.mkv", store.Place(_artist, official, Downloaded("video.mkv"), null, settings));

            store.DeleteFiles(_artist, official, settings);
            Assert.False(File.Exists(Path.Combine(folder, "Oasis - Wonderwall.mkv")));
            Assert.False(File.Exists(Path.Combine(folder, "Oasis - Wonderwall.nfo")));
            Assert.False(File.Exists(Path.Combine(folder, "Oasis - Wonderwall-thumb.jpg")));
        }

        [Fact]
        public void State_round_trips_through_the_video_folder()
        {
            var settings = new MusicVideosSettings { VideoFolder = string.Empty };
            var store = new VideoStore(LogManager.CreateNullLogger());

            store.Update(_artist, settings, s =>
            {
                s.Videos.Add(new MusicVideo { Key = "youtube:a", Title = "Wonderwall", Kind = VideoKind.Live, Monitored = false });
                s.CheckedRecordings.Add("r1");
                s.MonitorNew = false;
            });

            Assert.True(File.Exists(Path.Combine(_artist.Path, VideoStore.StateFileName)));
            var state = store.Load(_artist, settings);
            Assert.Equal(VideoKind.Live, state.Videos[0].Kind);
            Assert.False(state.Videos[0].Monitored);
            Assert.False(state.MonitorNew);
            Assert.Contains("r1", state.CheckedRecordings);
            Assert.Contains("\"Live\"", File.ReadAllText(Path.Combine(_artist.Path, VideoStore.StateFileName)));
        }

        [Fact]
        public void Ignored_videos_from_older_files_read_as_unmonitored()
        {
            var settings = new MusicVideosSettings { VideoFolder = string.Empty };
            var store = new VideoStore(LogManager.CreateNullLogger());
            File.WriteAllText(Path.Combine(_artist.Path, VideoStore.StateFileName),
                "{\"Videos\":[{\"Key\":\"youtube:a\",\"Status\":\"Ignored\"},{\"Key\":\"youtube:b\",\"Status\":\"Ignored\",\"File\":\"b.mkv\"},{\"Key\":\"youtube:c\",\"Status\":\"Wanted\"}]}");

            var videos = store.Load(_artist, settings).Videos;

            Assert.Equal([false, false, true], videos.Select(v => v.Monitored));
            Assert.Equal([VideoStatus.Wanted, VideoStatus.Downloaded, VideoStatus.Wanted], videos.Select(v => v.Status));
            Assert.True(store.Load(_artist, settings).MonitorNew);
        }

        private static string? After(List<string> args, string flag)
        {
            var index = args.IndexOf(flag);
            return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
        }
    }
}
