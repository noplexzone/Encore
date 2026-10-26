// Encore: adds to Lidarr's artist page a "Change image" button over the artist image and a
// Music Videos section below the albums. Lidarr's UI is a single page app, so this watches for
// the artist page to appear.
(function () {
  if (window.__encore) return;
  window.__encore = true;

  var urlBase = '__URL_BASE__';
  var lookups = {};
  var pending = false;

  var css =
    '.encore-overlay{position:absolute;display:flex;flex-direction:column;justify-content:flex-end;gap:6px;padding:10px;' +
    'box-sizing:border-box;opacity:0;transition:opacity .15s;background:linear-gradient(transparent 55%,rgba(0,0,0,.75));z-index:2}' +
    '.encore-overlay:hover,.encore-overlay:focus-within{opacity:1}' +
    '.encore-overlay a{display:block;padding:6px 8px;border-radius:4px;background:rgba(0,0,0,.65);color:#fff;text-align:center;' +
    'text-decoration:none;font-size:13px;border:1px solid rgba(255,255,255,.35)}' +
    '.encore-overlay a:hover{background:#00a65b;border-color:#00a65b}' +
    '@media (hover:none){.encore-overlay{opacity:1;background:none}}' +

    // Built to match Lidarr's Albums / Singles sections (ArtistDetailsSeason)
    '.encore-videos{padding:0 20px 20px}' +
    '.encore-section{border:1px solid var(--borderColor,#e5e5e5);border-radius:4px;background:var(--cardBackgroundColor,#fff)}' +
    '.encore-head{display:flex;align-items:center;font-size:24px;cursor:pointer;user-select:none}' +
    '.encore-left{display:flex;align-items:center;flex:0 1 auto;padding:15px 10px;min-width:0}' +
    '.encore-expand{display:flex;flex:1 1 auto;justify-content:center}' +
    '.encore-label{margin:0 10px 0 5px;white-space:nowrap}' +
    '.encore-count{white-space:nowrap;padding:3px 7px;border-radius:2px;font-weight:bold;font-size:14px;color:#fff;background:var(--themeLightColor,#3a3f51)}' +
    '.encore-count.success{background:var(--successColor,#00a65b)}' +
    '.encore-count.danger{background:var(--dangerColor,#f05050)}' +
    '.encore-muted{margin-left:10px;color:var(--helpTextColor,#909293);font-size:14px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.encore-actions{display:flex;gap:6px;padding:15px 10px;font-size:14px}' +
    '.encore-btn{display:inline-block;padding:4px 10px;border:1px solid var(--defaultBorderColor,#eaeaea);border-radius:4px;' +
    'background:var(--defaultBackgroundColor,#fff);color:var(--defaultColor,#333);font-size:13px;line-height:1.5;' +
    'cursor:pointer;text-decoration:none;white-space:nowrap}' +
    '.encore-btn:hover{border-color:var(--defaultHoverBorderColor,#adadad);background:var(--defaultHoverBackgroundColor,#f5f5f5);' +
    'text-decoration:none;color:var(--defaultColor,#333)}' +
    '.encore-btn[disabled]{opacity:.6;cursor:default}' +
    '.encore-icon{flex:0 0 24px;width:24px;height:24px;color:var(--textColor,#515253)}' +
    '.encore-chevron{flex:0 0 24px;width:24px;height:24px;color:var(--textColor,#515253);transition:transform .15s}' +
    '.encore-section.open .encore-chevron{transform:rotate(180deg)}' +
    '.encore-body{display:none;border-top:1px solid var(--borderColor,#e5e5e5)}' +
    '.encore-section.open .encore-body{display:block}' +
    '.encore-table{width:100%;border-collapse:collapse;font-size:14px}' +
    '.encore-table th{padding:8px;text-align:left;font-weight:bold;white-space:nowrap}' +
    '.encore-table td{padding:8px;border-top:1px solid var(--borderColor,#eee);vertical-align:middle}' +
    '.encore-table tr:hover td{background:var(--tableRowHoverBackgroundColor,rgba(0,0,0,.05))}' +
    '.encore-thumb{width:96px;height:54px;object-fit:cover;border-radius:3px;display:block;background:#000}' +
    '.encore-title{color:inherit}' +
    '.encore-sub{color:var(--helpTextColor,#909293);font-size:12px}' +
    '.encore-status{white-space:nowrap}' +
    '.encore-status.Downloaded{color:var(--successColor,#00a65b)}' +
    '.encore-status.Failed{color:var(--dangerColor,#f05050)}' +
    '.encore-status.Ignored{color:var(--helpTextColor,#909293)}' +
    '.encore-row-actions{white-space:nowrap;text-align:right}' +
    '.encore-row-actions .encore-btn{margin-left:4px}' +
    '.encore-empty{padding:15px;text-align:center;color:var(--helpTextColor,#909293)}' +
    '.encore-foot{display:flex;justify-content:center;padding:8px;border-top:1px solid var(--borderColor,#e5e5e5);' +
    'background:var(--collapseButtonBackgroundColor,#fafafa);font-size:13px}' +
    '.encore-foot a{cursor:pointer}' +
    '.encore-ignored td{opacity:.6}' +
    '@media (max-width:768px){.encore-videos{padding:0 0 20px}.encore-section{border-left:0;border-right:0;border-radius:0}' +
    '.encore-hide-small,.encore-muted{display:none}.encore-thumb{width:64px;height:36px}' +
    '.encore-head{font-size:20px}.encore-row-actions{white-space:normal}' +
    '.encore-row-actions .encore-btn{display:block;margin:0 0 4px auto}}';

  var style = document.createElement('style');
  style.textContent = css;
  document.head.appendChild(style);

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (key) {
      if (key === 'text') node.textContent = attrs[key];
      else if (key.indexOf('on') === 0) node.addEventListener(key.slice(2), attrs[key]);
      else if (attrs[key] !== null && attrs[key] !== undefined && attrs[key] !== false) node.setAttribute(key, attrs[key]);
    });
    (children || []).forEach(function (child) { if (child) node.appendChild(child); });
    return node;
  }

  function request(method, path, body) {
    var options = { method: method, credentials: 'same-origin', headers: {} };
    if (method !== 'GET') options.headers['X-Encore'] = '1';
    if (body) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
    return fetch(urlBase + path, options).then(function (r) {
      if (r.ok) return r.json();
      return r.text().then(function (text) {
        var message = text;
        try { message = JSON.parse(text).detail || text; } catch (e) {}
        throw new Error(message || ('Request failed: ' + r.status));
      });
    });
  }

  function remember(key, value) {
    try {
      if (value === undefined) return localStorage.getItem(key);
      localStorage.setItem(key, value);
    } catch (e) {}
    return null;
  }

  function foreignArtistId() {
    var path = location.pathname;
    if (urlBase && path.indexOf(urlBase) === 0) path = path.slice(urlBase.length);
    var match = path.match(/^\/artist\/([0-9a-f-]{36})\/?$/i);
    return match ? match[1].toLowerCase() : null;
  }

  function lookup(id) {
    if (!lookups[id]) {
      lookups[id] = request('GET', '/encore/api/artist/' + id).catch(function () { delete lookups[id]; return null; });
    }
    return lookups[id];
  }

  // CSS module classes look like "ArtistDetails-poster" or "ArtistDetails/poster/x1y2z", depending on the build
  function findByClass(tag, pattern) {
    var nodes = document.querySelectorAll(tag + '[class*="ArtistDetails"]');
    for (var i = 0; i < nodes.length; i++) {
      if (pattern.test(nodes[i].className)) return nodes[i];
    }
    return null;
  }

  // ---- Change image button over the artist image ----

  function placeOverlay(overlay, poster) {
    var parent = poster.offsetParent || poster.parentElement;
    if (getComputedStyle(parent).position === 'static') parent.style.position = 'relative';
    overlay.style.left = poster.offsetLeft + 'px';
    overlay.style.top = poster.offsetTop + 'px';
    overlay.style.width = poster.offsetWidth + 'px';
    overlay.style.height = poster.offsetHeight + 'px';
    overlay.style.display = poster.offsetWidth ? 'flex' : 'none';
  }

  function decoratePoster(id, artist) {
    var poster = findByClass('img', /ArtistDetails[-\/]poster/);
    var existing = document.querySelector('.encore-overlay');

    if (!poster || !artist || !artist.artwork) {
      if (existing) existing.remove();
      return;
    }

    var parent = poster.offsetParent || poster.parentElement;

    if (!existing || existing.getAttribute('data-artist') !== id || existing.parentElement !== parent) {
      if (existing) existing.remove();
      existing = el('div', { 'class': 'encore-overlay', 'data-artist': id }, [
        el('a', { href: urlBase + '/artwork/' + artist.id, text: 'Change image' })
      ]);
      parent.appendChild(existing);
    }

    placeOverlay(existing, poster);
  }

  // ---- Music Videos section below the albums ----

  var section = null;

  // Font Awesome's film and circle-chevron-down, as Lidarr uses Font Awesome for its own icons
  var FILM = '0 0 512 512|M0 96C0 60.7 28.7 32 64 32l384 0c35.3 0 64 28.7 64 64l0 320c0 35.3-28.7 64-64 64L64 480c-35.3 0-64-28.7-64-64L0 96zM48 368l0 32c0 8.8 7.2 16 16 16l32 0c8.8 0 16-7.2 16-16l0-32c0-8.8-7.2-16-16-16l-32 0c-8.8 0-16 7.2-16 16zm368-16c-8.8 0-16 7.2-16 16l0 32c0 8.8 7.2 16 16 16l32 0c8.8 0 16-7.2 16-16l0-32c0-8.8-7.2-16-16-16l-32 0zM48 240l0 32c0 8.8 7.2 16 16 16l32 0c8.8 0 16-7.2 16-16l0-32c0-8.8-7.2-16-16-16l-32 0c-8.8 0-16 7.2-16 16zm368-16c-8.8 0-16 7.2-16 16l0 32c0 8.8 7.2 16 16 16l32 0c8.8 0 16-7.2 16-16l0-32c0-8.8-7.2-16-16-16l-32 0zM48 112l0 32c0 8.8 7.2 16 16 16l32 0c8.8 0 16-7.2 16-16l0-32c0-8.8-7.2-16-16-16l-32 0c-8.8 0-16 7.2-16 16zM416 96c-8.8 0-16 7.2-16 16l0 32c0 8.8 7.2 16 16 16l32 0c8.8 0 16-7.2 16-16l0-32c0-8.8-7.2-16-16-16l-32 0zM160 128l0 64c0 17.7 14.3 32 32 32l128 0c17.7 0 32-14.3 32-32l0-64c0-17.7-14.3-32-32-32L192 96c-17.7 0-32 14.3-32 32zm32 160c-17.7 0-32 14.3-32 32l0 64c0 17.7 14.3 32 32 32l128 0c17.7 0 32-14.3 32-32l0-64c0-17.7-14.3-32-32-32l-128 0z';
  var CHEVRON = '0 0 512 512|M256 0a256 256 0 1 0 0 512A256 256 0 1 0 256 0zM135 241c-9.4-9.4-9.4-24.6 0-33.9s24.6-9.4 33.9 0l87 87 87-87c9.4-9.4 24.6-9.4 33.9 0s9.4 24.6 0 33.9L273 345c-9.4 9.4-24.6 9.4-33.9 0L135 241z';

  function icon(spec, className) {
    var parts = spec.split('|');
    var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', parts[0]);
    svg.setAttribute('class', className);
    svg.setAttribute('aria-hidden', 'true');
    var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', parts[1]);
    path.setAttribute('fill', 'currentColor');
    svg.appendChild(path);
    return svg;
  }

  function duration(seconds) {
    if (!seconds) return '';
    var m = Math.floor(seconds / 60), s = seconds % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  function size(bytes) {
    if (!bytes) return '';
    return bytes > 1073741824 ? (bytes / 1073741824).toFixed(2) + ' GB' : Math.round(bytes / 1048576) + ' MB';
  }

  function VideoSection(id, artist) {
    var self = this;
    // Collapsed at first, like Lidarr's own sections
    var expanded = remember('encore.videos.expanded') === '1';
    var showIgnored = false;
    var data = null;
    var worker = null;
    var timer = null;
    var error = null;

    self.id = id;
    self.node = el('div', { 'class': 'encore-videos' });

    function act(path, body) {
      return request('POST', '/videos/api/' + artist.id + path, body)
        .then(refresh)
        .catch(function (e) { error = e.message; render(); });
    }

    function refresh() {
      return Promise.all([
        request('GET', '/videos/api/' + artist.id),
        request('GET', '/videos/api/status').catch(function () { return null; })
      ]).then(function (results) {
        data = results[0];
        worker = results[1];
        error = null;
        render();
        schedulePoll();
      }).catch(function (e) {
        error = e.message;
        render();
      });
    }

    function busy() {
      if (!data) return false;
      if (data.searching) return true;
      if (worker && worker.artistId === artist.id) return true;
      return data.videos.some(function (v) { return v.queued; });
    }

    function schedulePoll() {
      clearTimeout(timer);
      if (self.node.isConnected) timer = setTimeout(refresh, busy() ? 3000 : 60000);
    }

    function statusText(v) {
      if (worker && worker.artistId === artist.id && worker.videoKey === v.key) return 'Downloading' + (worker.progress ? ' ' + worker.progress : '');
      if (v.queued) return 'Queued';
      if (v.status === 'Wanted') return 'Missing';
      return v.status;
    }

    function row(v) {
      var actions = [];
      var file = urlBase + '/videos/api/' + artist.id + '/file?key=' + encodeURIComponent(v.key);

      if (v.status === 'Downloaded') {
        actions.push(el('a', { 'class': 'encore-btn', href: file, target: '_blank', rel: 'noopener', text: 'Play' }));
      } else if (v.status !== 'Ignored' && !v.queued) {
        actions.push(el('button', { 'class': 'encore-btn', text: 'Download', onclick: function () { act('/download', { key: v.key }); } }));
      }

      if (v.status === 'Ignored') {
        actions.push(el('button', { 'class': 'encore-btn', text: 'Want', onclick: function () { act('/want', { key: v.key }); } }));
      } else if (v.status !== 'Downloaded') {
        actions.push(el('button', { 'class': 'encore-btn', text: 'Ignore', onclick: function () { act('/ignore', { key: v.key }); } }));
      }

      var sub = [v.source, duration(v.duration), v.year].filter(Boolean).join(' · ');

      return el('tr', { 'class': v.status === 'Ignored' ? 'encore-ignored' : null }, [
        el('td', { style: 'width:96px' }, [
          v.thumbnail ? el('img', { 'class': 'encore-thumb', src: v.thumbnail, loading: 'lazy', alt: '' }) : el('div', { 'class': 'encore-thumb' })
        ]),
        el('td', {}, [
          el('a', { 'class': 'encore-title', href: v.url, target: '_blank', rel: 'noopener', title: v.sourceTitle, text: v.title }),
          sub ? el('div', { 'class': 'encore-sub', text: sub }) : null
        ]),
        el('td', { 'class': 'encore-hide-small', text: v.kindLabel }),
        el('td', { 'class': 'encore-hide-small', text: [v.quality, size(v.size)].filter(Boolean).join(' · ') }),
        el('td', { 'class': 'encore-status ' + v.status, title: v.error || null, text: statusText(v) }),
        el('td', { 'class': 'encore-row-actions' }, actions)
      ]);
    }

    function render() {
      var videos = data ? data.videos : [];
      var visible = videos.filter(function (v) { return showIgnored || v.status !== 'Ignored'; });
      var counted = videos.filter(function (v) { return v.status !== 'Ignored'; });
      var have = counted.filter(function (v) { return v.status === 'Downloaded'; });
      var ignored = videos.length - counted.length;
      var missing = counted.length - have.length;
      var bytes = have.reduce(function (sum, v) { return sum + (v.size || 0); }, 0);

      var note = '';
      if (error) note = error;
      else if (data && data.searching) note = 'Searching…';
      else if (worker && worker.artistId === artist.id && worker.activity) note = worker.activity + (worker.progress ? ' · ' + worker.progress : '');
      else if (bytes) note = size(bytes);

      var head = el('div', { 'class': 'encore-head', onclick: function (e) {
        if (e.target.closest('.encore-actions')) return;
        expanded = !expanded;
        remember('encore.videos.expanded', expanded ? '1' : '0');
        render();
      } }, [
        el('div', { 'class': 'encore-left' }, [
          icon(FILM, 'encore-icon'),
          el('span', { 'class': 'encore-label', text: 'Music Videos' }),
          el('span', {
            'class': 'encore-count ' + (!counted.length ? '' : missing ? 'danger' : 'success'),
            title: have.length + ' downloaded, ' + missing + ' missing' + (ignored ? ', ' + ignored + ' ignored' : ''),
            text: have.length + ' / ' + counted.length
          }),
          note ? el('span', { 'class': 'encore-muted', title: note, text: note }) : null
        ]),
        el('div', { 'class': 'encore-expand' }, [icon(CHEVRON, 'encore-chevron')]),
        el('div', { 'class': 'encore-actions' }, [
          missing ? el('button', { 'class': 'encore-btn encore-hide-small', text: 'Download missing', onclick: function () { act('/download-all'); } }) : null,
          el('button', { 'class': 'encore-btn', text: 'Search', disabled: data && data.searching ? 'disabled' : null, onclick: function () { act('/search'); } }),
          el('a', { 'class': 'encore-btn encore-hide-small', href: urlBase + '/videos/' + artist.id, text: 'Manage' })
        ])
      ]);

      var body;
      if (!data) {
        body = el('div', { 'class': 'encore-empty', text: error || 'Loading…' });
      } else if (!visible.length) {
        body = el('div', { 'class': 'encore-empty', text: data.lastSearch ? 'No music videos found yet' : 'Not searched yet. Click Search to look for music videos.' });
      } else {
        body = el('table', { 'class': 'encore-table' }, [
          el('thead', {}, [el('tr', {}, [
            el('th', {}), el('th', { text: 'Title' }), el('th', { 'class': 'encore-hide-small', text: 'Type' }),
            el('th', { 'class': 'encore-hide-small', text: 'Quality' }), el('th', { text: 'Status' }), el('th', {})
          ])]),
          el('tbody', {}, visible.map(row))
        ]);
      }

      var foot = ignored ? el('div', { 'class': 'encore-foot' }, [
        el('a', { text: showIgnored ? 'Hide ignored' : 'Show ' + ignored + ' ignored', onclick: function () { showIgnored = !showIgnored; render(); } })
      ]) : null;

      var box = el('div', { 'class': 'encore-section' + (expanded ? ' open' : '') }, [
        head,
        el('div', { 'class': 'encore-body' }, [body, foot])
      ]);

      self.node.replaceChildren(box);
    }

    self.attach = function (anchor) {
      if (self.node.previousElementSibling !== anchor || !self.node.isConnected) {
        anchor.insertAdjacentElement('afterend', self.node);
        refresh();
      }
    };

    self.destroy = function () {
      clearTimeout(timer);
      self.node.remove();
    };

    render();
  }

  function decorateVideos(id, artist) {
    var content = findByClass('div', /ArtistDetails[-\/]contentContainer/);

    if (!content || !artist || !artist.videos) {
      if (section) { section.destroy(); section = null; }
      return;
    }

    if (section && section.id !== id) { section.destroy(); section = null; }
    if (!section) section = new VideoSection(id, artist);
    section.attach(content);
  }

  // ---- Watching the page ----

  function decorate() {
    pending = false;
    var id = foreignArtistId();

    if (!id) {
      var overlay = document.querySelector('.encore-overlay');
      if (overlay) overlay.remove();
      if (section) { section.destroy(); section = null; }
      return;
    }

    lookup(id).then(function (artist) {
      if (foreignArtistId() !== id) return;
      decoratePoster(id, artist);
      decorateVideos(id, artist);
    });
  }

  function schedule(mutations) {
    // Our own changes don't need another pass
    if (mutations && mutations.every(function (m) { return m.target.closest && m.target.closest('.encore-videos,.encore-overlay'); })) return;
    if (!pending) {
      pending = true;
      setTimeout(decorate, 150);
    }
  }

  new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('resize', function () { schedule(); });
  schedule();
})();
