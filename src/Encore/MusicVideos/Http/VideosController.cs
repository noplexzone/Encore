using Encore.MusicVideos.Videos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Music;

namespace Encore.MusicVideos.Http
{
    /// <summary>
    /// The Videos page. Uses Lidarr's "UI" policy, so it follows whatever login Lidarr's
    /// own pages require. Changes need the X-Encore header, which a cross-site form can't send.
    /// </summary>
    [Authorize(Policy = "UI")]
    [Route("videos")]
    public class VideosController : Controller
    {
        private const string ChangeHeader = "X-Encore";
        private const string NotEnabled = "Enable Music Videos under Settings > Metadata first";

        private readonly IArtistService _artistService;
        private readonly IVideoStore _store;
        private readonly IMusicVideoService _videoService;

        public VideosController(IArtistService artistService, IVideoStore store, IMusicVideoService videoService)
        {
            _artistService = artistService;
            _store = store;
            _videoService = videoService;
        }

        [HttpGet("")]
        [HttpGet("{artistId:int}")]
        public IActionResult Page()
        {
            using var stream = typeof(VideosController).Assembly.GetManifestResourceStream("Encore.MusicVideos.videos.html")!;
            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
        }

        [HttpGet("api/status")]
        public IActionResult Status() => Ok(_videoService.Status);

        [HttpGet("api/artists")]
        public IActionResult Artists()
        {
            var settings = _videoService.Settings;

            return Ok(_artistService.GetAllArtists()
                .OrderBy(a => a.SortName ?? a.Name)
                .Select(a =>
                {
                    var videos = settings != null && Directory.Exists(a.Path) ? _store.Load(a, settings).Videos : [];
                    return new
                    {
                        id = a.Id,
                        name = a.Name,
                        downloaded = videos.Count(v => v.Status == VideoStatus.Downloaded),
                        wanted = videos.Count(v => v.Status is VideoStatus.Wanted or VideoStatus.Failed)
                    };
                }));
        }

        [HttpGet("api/{artistId:int}")]
        public IActionResult Artist(int artistId)
        {
            var settings = _videoService.Settings;
            var artist = FindArtist(artistId);

            if (settings == null)
            {
                return Problem(NotEnabled, statusCode: 409);
            }

            if (artist == null)
            {
                return NotFound();
            }

            var folderExists = Directory.Exists(artist.Path);
            var state = folderExists ? _store.Load(artist, settings) : new VideoState();

            return Ok(new
            {
                id = artist.Id,
                name = artist.Name,
                foreignArtistId = artist.ForeignArtistId,
                folder = _store.VideoFolder(artist, settings),
                folderExists,
                lastSearch = state.LastSearch,
                searching = _videoService.IsQueued(artist.Id, null),
                extraSources = state.ExtraSources,
                videos = state.Videos
                    .OrderBy(v => v.Status == VideoStatus.Ignored)
                    .ThenBy(v => v.Kind)
                    .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(v => new
                    {
                        key = v.Key,
                        url = v.Url,
                        title = v.Title,
                        sourceTitle = v.SourceTitle,
                        kind = v.Kind.ToString(),
                        kindLabel = VideoNaming.KindLabel(v.Kind),
                        source = v.Source,
                        thumbnail = v.Thumbnail,
                        duration = v.Duration,
                        year = v.Year,
                        status = v.Status.ToString(),
                        queued = _videoService.IsQueued(artist.Id, v.Key),
                        file = v.File,
                        error = v.Error,
                        quality = v.Quality?.ToString(),
                        size = v.Quality?.Size
                    })
            });
        }

        [HttpGet("api/{artistId:int}/file")]
        public IActionResult File(int artistId, [FromQuery] string key)
        {
            var settings = _videoService.Settings;
            var artist = FindArtist(artistId);

            if (settings == null || artist == null)
            {
                return NotFound();
            }

            var video = _store.Load(artist, settings).Videos.FirstOrDefault(v => v.Key == key);
            var path = video == null ? null : _store.FilePath(artist, video, settings);

            if (path == null || !System.IO.File.Exists(path))
            {
                return NotFound();
            }

            var type = path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "video/x-matroska";
            return PhysicalFile(path, type, enableRangeProcessing: true);
        }

        [HttpPost("api/{artistId:int}/search")]
        public IActionResult Search(int artistId) =>
            Change(artistId, artist => _videoService.QueueSearch(artist.Id));

        [HttpPost("api/{artistId:int}/download")]
        public IActionResult Download(int artistId, [FromBody] VideoRequest request) =>
            Change(artistId, artist =>
            {
                _videoService.SetStatus(artist, Required(request.Key), VideoStatus.Wanted);
                _videoService.QueueDownload(artist.Id, request.Key!);
            });

        [HttpPost("api/{artistId:int}/download-all")]
        public IActionResult DownloadAll(int artistId) =>
            Change(artistId, artist =>
            {
                var settings = _videoService.Settings!;

                foreach (var video in _store.Load(artist, settings).Videos.Where(v => v.Status is VideoStatus.Wanted or VideoStatus.Failed))
                {
                    _videoService.SetStatus(artist, video.Key, VideoStatus.Wanted);
                    _videoService.QueueDownload(artist.Id, video.Key);
                }
            });

        [HttpPost("api/{artistId:int}/ignore")]
        public IActionResult Ignore(int artistId, [FromBody] VideoRequest request) =>
            Change(artistId, artist => _videoService.SetStatus(artist, Required(request.Key), VideoStatus.Ignored));

        [HttpPost("api/{artistId:int}/want")]
        public IActionResult Want(int artistId, [FromBody] VideoRequest request) =>
            Change(artistId, artist => _videoService.SetStatus(artist, Required(request.Key), VideoStatus.Wanted));

        [HttpPost("api/{artistId:int}/delete")]
        public IActionResult Delete(int artistId, [FromBody] VideoRequest request) =>
            Change(artistId, artist => _videoService.DeleteVideo(artist, Required(request.Key)));

        [HttpPost("api/{artistId:int}/add")]
        public IActionResult Add(int artistId, [FromBody] VideoRequest request) =>
            Change(artistId, artist =>
            {
                var url = Required(request.Url).Trim();

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                {
                    throw new InvalidOperationException("Paste a full video, channel or playlist address");
                }

                _videoService.QueueAddUrl(artist.Id, url);
            });

        private IActionResult Change(int artistId, Action<Artist> change)
        {
            if (!Request.Headers.ContainsKey(ChangeHeader))
            {
                return BadRequest($"Missing {ChangeHeader} header");
            }

            var artist = FindArtist(artistId);

            if (_videoService.Settings == null)
            {
                return Problem(NotEnabled, statusCode: 409);
            }

            if (artist == null)
            {
                return NotFound();
            }

            if (!Directory.Exists(artist.Path))
            {
                return Problem("The artist folder doesn't exist yet", statusCode: 409);
            }

            try
            {
                change(artist);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                return Problem(ex.Message, statusCode: 400);
            }

            return Artist(artistId);
        }

        private static string Required(string? value) =>
            string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("Missing value") : value;

        private Artist? FindArtist(int artistId)
        {
            try
            {
                return _artistService.GetArtist(artistId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NzbDrone.Core.Datastore.ModelNotFoundException)
            {
                return null;
            }
        }

        public class VideoRequest
        {
            public string? Key { get; set; }
            public string? Url { get; set; }
        }
    }
}
