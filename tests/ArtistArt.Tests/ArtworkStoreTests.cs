using ArtistArt;
using ArtistArt.Art;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using Xunit;

namespace ArtistArt.Tests
{
    public sealed class ArtworkStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "artistart-tests-" + Guid.NewGuid().ToString("N"));
        private readonly ArtworkStore _store;
        private readonly ArtistArtSettings _settings = new();
        private readonly Artist _artist;

        public ArtworkStoreTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "music", "Artist"));
            _store = new ArtworkStore(new CopyResizer(), new FakeAppFolderInfo(Path.Combine(_root, "config")), LogManager.CreateNullLogger());
            _artist = new Artist
            {
                Id = 7,
                Path = Path.Combine(_root, "music", "Artist"),
                Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata
                {
                    Name = "Artist",
                    Images = [new MediaCover(MediaCoverTypes.Poster, "https://example.invalid/poster.jpg")]
                })
            };
        }

        private string ArtistFile(string name) => Path.Combine(_artist.Path, name);
        private string Candidate(string name) => Path.Combine(_artist.Path, "Artwork", name);
        private string Cache(string name) => Path.Combine(_root, "config", "MediaCover", "7", name);

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        [Theory]
        [InlineData("poster.jpg", true)]
        [InlineData("poster-2.png", true)]
        [InlineData("poster_live.webp", true)]
        [InlineData("folder.jpeg", true)]
        [InlineData("posterior.jpg", false)]
        [InlineData("poster.gif", false)]
        [InlineData("fanart-1.jpg", false)]
        public void Poster_candidates_are_matched_by_name(string file, bool expected) =>
            Assert.Equal(expected, ArtType.Poster.IsCandidate(file));

        [Fact]
        public void Existing_folder_images_are_kept_as_candidates_before_anything_is_overwritten()
        {
            Write(ArtistFile("folder.jpg"), "old folder");
            Write(ArtistFile("artist.png"), "old artist");

            _store.EnsureInitialized(_artist, _settings);

            Assert.Equal("old folder", File.ReadAllText(Candidate("poster-existing-folder.jpg")));
            Assert.Equal("old artist", File.ReadAllText(Candidate("poster-existing-artist.png")));
        }

        [Fact]
        public void Choosing_an_image_writes_folder_files_and_replaces_stale_formats()
        {
            Write(ArtistFile("artist.jpg"), "stale");
            Write(Candidate("poster-1.png"), "chosen");

            _store.Select(_artist, ArtType.Poster, "poster-1.png", _settings);

            Assert.Equal("chosen", File.ReadAllText(ArtistFile("artist.png")));
            Assert.Equal("chosen", File.ReadAllText(ArtistFile("folder.png")));
            Assert.False(File.Exists(ArtistFile("artist.jpg")));
        }

        [Fact]
        public void Paths_outside_the_candidate_folder_are_rejected()
        {
            Write(ArtistFile("poster.jpg"), "outside");

            Assert.Null(_store.CandidatePath(_artist, ArtType.Poster, "../poster.jpg", _settings));
            Assert.Throws<FileNotFoundException>(() => _store.Select(_artist, ArtType.Poster, "../poster.jpg", _settings));
        }

        [Fact]
        public void Chosen_image_goes_into_lidarrs_cache_with_resized_copies()
        {
            Write(Candidate("poster-1.jpg"), "chosen");
            _store.Select(_artist, ArtType.Poster, "poster-1.jpg", _settings);

            Assert.True(_store.ApplyToCache(_artist, _settings));

            Assert.Equal("chosen", File.ReadAllText(Cache("poster.jpg")));
            Assert.True(File.Exists(Cache("poster-500.jpg")));
            Assert.True(File.Exists(Cache("poster-250.jpg")));

            // Nothing changed since, so nothing is rewritten
            Assert.False(_store.ApplyToCache(_artist, _settings));
        }

        [Fact]
        public void A_fresh_image_from_lidarr_is_kept_as_a_candidate_and_then_overridden_again()
        {
            Write(Candidate("poster-1.jpg"), "chosen");
            _store.Select(_artist, ArtType.Poster, "poster-1.jpg", _settings);
            _store.ApplyToCache(_artist, _settings);

            // Our own copy in the cache must never be mistaken for Lidarr's image
            _store.CaptureLidarrImages(_artist, _settings);
            Assert.False(File.Exists(Candidate("poster-lidarr.jpg")));

            // Lidarr refreshes the artist and downloads a new poster over ours
            Write(Cache("poster.jpg"), "new lidarr poster");

            _store.CaptureLidarrImages(_artist, _settings);
            Assert.Equal("new lidarr poster", File.ReadAllText(Candidate("poster-lidarr.jpg")));

            Assert.True(_store.ApplyToCache(_artist, _settings));
            Assert.Equal("chosen", File.ReadAllText(Cache("poster.jpg")));
        }

        [Fact]
        public void Reset_puts_lidarrs_image_back()
        {
            Write(Cache("poster.jpg"), "lidarr poster");
            _store.EnsureInitialized(_artist, _settings);
            Write(Candidate("poster-1.jpg"), "chosen");
            _store.Select(_artist, ArtType.Poster, "poster-1.jpg", _settings);
            _store.ApplyToCache(_artist, _settings);

            _store.Reset(_artist, ArtType.Poster, _settings);

            Assert.Equal("lidarr poster", File.ReadAllText(Cache("poster.jpg")));
            Assert.Equal("lidarr poster", File.ReadAllText(ArtistFile("artist.jpg")));
            Assert.Empty(_store.Load(_artist, _settings).Selected);
        }

        [Fact]
        public void The_image_in_use_cannot_be_deleted()
        {
            Write(Candidate("poster-1.jpg"), "chosen");
            Write(Candidate("poster-2.jpg"), "other");
            _store.Select(_artist, ArtType.Poster, "poster-1.jpg", _settings);

            Assert.Throws<InvalidOperationException>(() => _store.DeleteCandidate(_artist, ArtType.Poster, "poster-1.jpg", _settings));
            _store.DeleteCandidate(_artist, ArtType.Poster, "poster-2.jpg", _settings);
            Assert.False(File.Exists(Candidate("poster-2.jpg")));
        }

        [Fact]
        public void Uploads_get_numbered_names_and_only_images_are_accepted()
        {
            using var image = new MemoryStream("img"u8.ToArray());
            Assert.Equal("poster-1.webp", _store.AddCandidate(_artist, ArtType.Poster, "My Photo.WEBP", image, _settings));
            Assert.Equal("poster-2.jpg", _store.AddCandidate(_artist, ArtType.Poster, "x.jpg", new MemoryStream(), _settings));
            Assert.Throws<InvalidOperationException>(() => _store.AddCandidate(_artist, ArtType.Poster, "x.exe", new MemoryStream(), _settings));
        }

        public void Dispose() => Directory.Delete(_root, true);

        private sealed class CopyResizer : IImageResizer
        {
            public void Resize(string source, string destination, int height) => File.Copy(source, destination, true);
        }

        private sealed class FakeAppFolderInfo(string appData) : IAppFolderInfo
        {
            public string AppDataFolder => appData;
            public string TempFolder => Path.GetTempPath();
            public string StartUpFolder => appData;
        }
    }
}
