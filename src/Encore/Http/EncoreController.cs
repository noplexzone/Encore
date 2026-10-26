using Encore.ArtistArt;
using Encore.MusicVideos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Music;

namespace Encore.Http
{
    /// <summary>
    /// The script added to Lidarr's pages, and what it needs to know about the artist on screen.
    /// </summary>
    [Route("encore")]
    public class EncoreController : Controller
    {
        private readonly IArtistService _artistService;
        private readonly IArtistArtService _artService;
        private readonly IMusicVideoService _videoService;
        private readonly IConfigFileProvider _configFileProvider;

        public EncoreController(IArtistService artistService, IArtistArtService artService, IMusicVideoService videoService, IConfigFileProvider configFileProvider)
        {
            _artistService = artistService;
            _artService = artService;
            _videoService = videoService;
            _configFileProvider = configFileProvider;
        }

        // Loaded by Lidarr's login page too, so it can't require a login itself; it holds no data
        [AllowAnonymous]
        [HttpGet("ui.js")]
        public IActionResult Script()
        {
            using var stream = typeof(EncoreController).Assembly.GetManifestResourceStream("Encore.ui.js")!;
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd().Replace("__URL_BASE__", _configFileProvider.UrlBase);
            return Content(script, "text/javascript; charset=utf-8");
        }

        [Authorize(Policy = "UI")]
        [HttpGet("api/artist/{foreignArtistId}")]
        public IActionResult Artist(string foreignArtistId)
        {
            var artist = _artistService.FindById(foreignArtistId);

            if (artist == null)
            {
                return NotFound();
            }

            return Ok(new
            {
                id = artist.Id,
                artwork = _artService.Settings != null,
                videos = _videoService.Settings != null
            });
        }
    }
}
