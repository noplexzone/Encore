using Encore.ArtistArt.Art;
using Encore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Music;

namespace Encore.ArtistArt.Http
{
    /// <summary>
    /// The image picker. Uses Lidarr's "UI" policy, so it follows whatever login Lidarr's
    /// own pages require. Changes need the X-Encore header, which a cross-site form can't send.
    /// </summary>
    [Authorize(Policy = "UI")]
    [Route("artwork")]
    [NoCache]
    public class ArtworkController : Controller
    {
        private const string ChangeHeader = "X-Encore";

        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp"
        };

        private readonly IArtistService _artistService;
        private readonly IArtworkStore _store;
        private readonly IArtistArtService _artService;

        public ArtworkController(IArtistService artistService, IArtworkStore store, IArtistArtService artService)
        {
            _artistService = artistService;
            _store = store;
            _artService = artService;
        }

        [HttpGet("")]
        [HttpGet("{artistId:int}")]
        public IActionResult Page()
        {
            using var stream = typeof(ArtworkController).Assembly.GetManifestResourceStream("Encore.ArtistArt.picker.html")!;
            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
        }

        [HttpGet("api/artists")]
        public IActionResult Artists()
        {
            return Ok(_artistService.GetAllArtists()
                .OrderBy(a => a.SortName ?? a.Name)
                .Select(a => new { id = a.Id, name = a.Name }));
        }

        [HttpGet("api/{artistId:int}")]
        public IActionResult Artist(int artistId)
        {
            var settings = _artService.Settings;
            var artist = FindArtist(artistId);

            if (settings == null)
            {
                return Problem("Enable Artist Art under Settings > Metadata first", statusCode: 409);
            }

            if (artist == null)
            {
                return NotFound();
            }

            if (Directory.Exists(artist.Path))
            {
                _store.EnsureInitialized(artist, settings);
            }

            var state = _store.Load(artist, settings);

            return Ok(new
            {
                id = artist.Id,
                name = artist.Name,
                foreignArtistId = artist.ForeignArtistId,
                folder = _store.CandidateFolder(artist, settings),
                folderExists = Directory.Exists(artist.Path),
                types = ArtType.All.Select(t => new
                {
                    key = t.Key,
                    label = t.Label,
                    selected = state.Selected.GetValueOrDefault(t.Key),
                    candidates = _store.Candidates(artist, t, settings).Select(c => new
                    {
                        file = c.File,
                        size = c.Size,
                        modified = c.Modified,
                        fromLidarr = c.FromLidarr
                    })
                })
            });
        }

        [HttpGet("api/{artistId:int}/image/{type}/{file}")]
        public IActionResult Image(int artistId, string type, string file)
        {
            var settings = _artService.Settings;
            var artist = FindArtist(artistId);
            var artType = ArtType.Find(type);

            if (settings == null || artist == null || artType == null)
            {
                return NotFound();
            }

            var path = _store.CandidatePath(artist, artType, file, settings);

            if (path == null)
            {
                return NotFound();
            }

            return PhysicalFile(path, ContentTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream"));
        }

        [HttpPost("api/{artistId:int}/select")]
        public IActionResult Select(int artistId, [FromBody] SelectRequest request) =>
            Change(artistId, request.Type, (artist, type) => _artService.Select(artist, type, request.File ?? string.Empty));

        [HttpPost("api/{artistId:int}/reset")]
        public IActionResult Reset(int artistId, [FromBody] SelectRequest request) =>
            Change(artistId, request.Type, (artist, type) => _artService.Reset(artist, type));

        [HttpPost("api/{artistId:int}/delete")]
        public IActionResult Delete(int artistId, [FromBody] SelectRequest request) =>
            Change(artistId, request.Type, (artist, type) => _store.DeleteCandidate(artist, type, request.File ?? string.Empty, _artService.Settings!));

        [HttpPost("api/{artistId:int}/upload/{type}")]
        [RequestSizeLimit(50 * 1024 * 1024)]
        public IActionResult Upload(int artistId, string type, IFormFile file) =>
            Change(artistId, type, (artist, artType) =>
            {
                using var stream = file.OpenReadStream();
                _store.AddCandidate(artist, artType, file.FileName, stream, _artService.Settings!);
            });

        private IActionResult Change(int artistId, string? type, Action<Artist, ArtType> change)
        {
            if (!Request.Headers.ContainsKey(ChangeHeader))
            {
                return BadRequest($"Missing {ChangeHeader} header");
            }

            var artist = FindArtist(artistId);
            var artType = ArtType.Find(type);

            if (_artService.Settings == null)
            {
                return Problem("Enable Artist Art under Settings > Metadata first", statusCode: 409);
            }

            if (artist == null || artType == null)
            {
                return NotFound();
            }

            if (!Directory.Exists(artist.Path))
            {
                return Problem("The artist folder doesn't exist yet", statusCode: 409);
            }

            try
            {
                change(artist, artType);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                return Problem(ex.Message, statusCode: 400);
            }

            return Artist(artistId);
        }

        private Artist? FindArtist(int artistId)
        {
            try
            {
                return _artistService.GetArtist(artistId);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (NzbDrone.Core.Datastore.ModelNotFoundException)
            {
                return null;
            }
        }

        public class SelectRequest
        {
            public string? Type { get; set; }
            public string? File { get; set; }
        }
    }
}
