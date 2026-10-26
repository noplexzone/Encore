// Encore: adds "Change image" and "Videos" buttons over the artist image on Lidarr's artist page.
// Lidarr's UI is a single page app, so this watches for the artist page to appear.
(function () {
  if (window.__encore) return;
  window.__encore = true;

  var urlBase = '__URL_BASE__';
  var cache = {};
  var pending = false;

  var style = document.createElement('style');
  style.textContent =
    '.encore-overlay{position:absolute;display:flex;flex-direction:column;justify-content:flex-end;gap:6px;padding:10px;' +
    'box-sizing:border-box;opacity:0;transition:opacity .15s;background:linear-gradient(transparent 45%,rgba(0,0,0,.75));z-index:2}' +
    '.encore-overlay:hover,.encore-overlay:focus-within{opacity:1}' +
    '.encore-overlay a{display:block;padding:6px 8px;border-radius:4px;background:rgba(0,0,0,.65);color:#fff;text-align:center;' +
    'text-decoration:none;font-size:13px;border:1px solid rgba(255,255,255,.35)}' +
    '.encore-overlay a:hover{background:#00a65b;border-color:#00a65b}' +
    '@media (hover:none){.encore-overlay{opacity:1;background:none}}';
  document.head.appendChild(style);

  function foreignArtistId() {
    var path = location.pathname;
    if (urlBase && path.indexOf(urlBase) === 0) path = path.slice(urlBase.length);
    var match = path.match(/^\/artist\/([0-9a-f-]{36})\/?$/i);
    return match ? match[1].toLowerCase() : null;
  }

  // CSS module classes look like "ArtistDetails-poster" or "ArtistDetails/poster/x1y2z", depending on the build
  function findPoster() {
    var images = document.querySelectorAll('img[class*="ArtistDetails"]');
    for (var i = 0; i < images.length; i++) {
      if (/ArtistDetails[-\/]poster/.test(images[i].className)) return images[i];
    }
    return null;
  }

  function lookup(id) {
    if (!cache[id]) {
      cache[id] = fetch(urlBase + '/encore/api/artist/' + id, { credentials: 'same-origin' })
        .then(function (r) { return r.ok ? r.json() : null; })
        .catch(function () { delete cache[id]; return null; });
    }
    return cache[id];
  }

  function place(overlay, poster) {
    var parent = poster.offsetParent || poster.parentElement;
    if (getComputedStyle(parent).position === 'static') parent.style.position = 'relative';
    overlay.style.left = poster.offsetLeft + 'px';
    overlay.style.top = poster.offsetTop + 'px';
    overlay.style.width = poster.offsetWidth + 'px';
    overlay.style.height = poster.offsetHeight + 'px';
    overlay.style.display = poster.offsetWidth ? 'flex' : 'none';
  }

  function decorate() {
    pending = false;
    var id = foreignArtistId();
    var poster = findPoster();
    var existing = document.querySelector('.encore-overlay');

    if (!id || !poster) {
      if (existing) existing.remove();
      return;
    }

    if (existing && existing.getAttribute('data-artist') === id && existing.parentElement === (poster.offsetParent || poster.parentElement)) {
      place(existing, poster);
      return;
    }

    lookup(id).then(function (artist) {
      if (!artist || foreignArtistId() !== id || (!artist.artwork && !artist.videos)) return;
      var current = document.querySelector('.encore-overlay');
      if (current) current.remove();

      var overlay = document.createElement('div');
      overlay.className = 'encore-overlay';
      overlay.setAttribute('data-artist', id);

      if (artist.artwork) {
        var art = document.createElement('a');
        art.href = urlBase + '/artwork/' + artist.id;
        art.textContent = 'Change image';
        overlay.appendChild(art);
      }

      if (artist.videos) {
        var videos = document.createElement('a');
        videos.href = urlBase + '/videos/' + artist.id;
        videos.textContent = 'Music videos';
        overlay.appendChild(videos);
      }

      (poster.offsetParent || poster.parentElement).appendChild(overlay);
      place(overlay, poster);
    });
  }

  function schedule() {
    if (!pending) {
      pending = true;
      setTimeout(decorate, 150);
    }
  }

  new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('resize', schedule);
  schedule();
})();
