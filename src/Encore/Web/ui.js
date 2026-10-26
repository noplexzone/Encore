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
    '.encore-actions{display:flex;padding:15px 10px}' +
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
    '.encore-status.Unmonitored{color:var(--helpTextColor,#909293)}' +
    '.encore-row-actions{width:40px;white-space:nowrap;text-align:right}' +
    '.encore-monitor-cell{width:24px;padding-right:0 !important}' +
    '.encore-thumb-cell{width:96px}' +
    '.encore-icon-btn{display:inline-flex;align-items:center;justify-content:center;width:24px;height:24px;padding:0;border:0;' +
    'background:none;color:inherit;cursor:pointer}' +
    '.encore-icon-btn:hover{color:var(--iconButtonHoverColor,#666)}' +
    '.encore-icon-btn[disabled]{color:var(--disabledColor,#999);cursor:not-allowed}' +
    '.encore-monitor{color:var(--textColor,#515253)}' +
    '.encore-svg{width:100%;height:100%;max-width:16px;max-height:16px}' +
    '.encore-monitor .encore-svg{max-width:none;max-height:none}' +
    '.encore-head .encore-icon-btn .encore-svg{max-width:20px;max-height:20px}' +
    '.encore-spin{animation:encore-spin 1s linear infinite}' +
    '@keyframes encore-spin{to{transform:rotate(360deg)}}' +
    '.encore-count.warning{background:var(--warningColor,#ffa500)}' +
    '.encore-empty{padding:15px;text-align:center;color:var(--helpTextColor,#909293)}' +
    '.encore-foot{display:flex;justify-content:center;padding:8px;border-top:1px solid var(--borderColor,#e5e5e5);' +
    'background:var(--collapseButtonBackgroundColor,#fafafa);font-size:13px}' +
    '.encore-foot a{color:var(--linkColor,#00a65b)}' +
    '@media (max-width:768px){.encore-videos{padding:0 0 20px}.encore-section{border-left:0;border-right:0;border-radius:0}' +
    '.encore-hide-small,.encore-muted{display:none}.encore-thumb{width:64px;height:36px}' +
    '.encore-head{font-size:20px}' +
    '.encore-thumb-cell{width:64px}}';

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

  // Font Awesome icons, the same ones Lidarr uses for its own buttons
  var BOOKMARK = '0 0 384 512|M0 48V487.7C0 501.1 10.9 512 24.3 512c5 0 9.9-1.5 14-4.4L192 400 345.7 507.6c4.1 2.9 9 4.4 14 4.4c13.4 0 24.3-10.9 24.3-24.3V48c0-26.5-21.5-48-48-48H48C21.5 0 0 21.5 0 48z';
  var BOOKMARK_OUTLINE = '0 0 384 512|M0 48C0 21.5 21.5 0 48 0l0 48 0 393.4 130.1-92.9c8.3-6 19.6-6 27.9 0L336 441.4 336 48 48 48 48 0 336 0c26.5 0 48 21.5 48 48l0 440c0 9-5 17.2-13 21.3s-17.6 3.4-24.9-1.8L192 397.5 37.9 507.5c-7.3 5.2-16.9 5.9-24.9 1.8S0 497 0 488L0 48z';
  var SEARCH = '0 0 512 512|M416 208c0 45.9-14.9 88.3-40 122.7L502.6 457.4c12.5 12.5 12.5 32.8 0 45.3s-32.8 12.5-45.3 0L330.7 376c-34.4 25.2-76.8 40-122.7 40C93.1 416 0 322.9 0 208S93.1 0 208 0S416 93.1 416 208zM208 352a144 144 0 1 0 0-288 144 144 0 1 0 0 288z';
  var SPINNER = '0 0 512 512|M304 48a48 48 0 1 0 -96 0 48 48 0 1 0 96 0zm0 416a48 48 0 1 0 -96 0 48 48 0 1 0 96 0zM48 304a48 48 0 1 0 0-96 48 48 0 1 0 0 96zm464-48a48 48 0 1 0 -96 0 48 48 0 1 0 96 0zM142.9 437A48 48 0 1 0 75 369.1 48 48 0 1 0 142.9 437zm0-294.2A48 48 0 1 0 75 75a48 48 0 1 0 67.9 67.9zM369.1 437A48 48 0 1 0 437 369.1 48 48 0 1 0 369.1 437z';
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
      if (v.status === 'Wanted') return v.monitored ? 'Missing' : 'Unmonitored';
      return v.status;
    }

    // Lidarr's MonitorToggleButton: a filled bookmark when monitored, an outline when not
    function monitorToggle(monitored, size, title, onToggle) {
      var disabled = !data || !data.monitored;
      return el('button', {
        'class': 'encore-icon-btn encore-monitor',
        style: 'width:' + size + 'px;height:' + size + 'px',
        title: disabled ? 'Artist is not monitored' : title,
        disabled: disabled ? 'disabled' : null,
        onclick: function (e) {
          e.stopPropagation();
          if (!disabled) onToggle(!monitored);
        }
      }, [icon(monitored ? BOOKMARK : BOOKMARK_OUTLINE, 'encore-svg')]);
    }

    function searchButton(title, spinning, onPress) {
      return el('button', {
        'class': 'encore-icon-btn',
        title: title,
        disabled: spinning ? 'disabled' : null,
        onclick: function (e) { e.stopPropagation(); onPress(); }
      }, [icon(spinning ? SPINNER : SEARCH, 'encore-svg' + (spinning ? ' encore-spin' : ''))]);
    }

    function setMonitored(key, monitored) {
      data.videos.forEach(function (v) { if (key === null || v.key === key) v.monitored = monitored; });
      if (key === null) data.monitorNew = monitored;
      render();
      act('/monitor', { key: key, monitored: monitored });
    }

    function row(v) {
      var downloading = !!(worker && worker.artistId === artist.id && worker.videoKey === v.key);
      var sub = [v.source, duration(v.duration), v.year].filter(Boolean).join(' · ');

      return el('tr', {}, [
        el('td', { 'class': 'encore-monitor-cell' }, [
          monitorToggle(v.monitored, 14, v.monitored ? 'Monitored, click to unmonitor' : 'Unmonitored, click to monitor', function (on) { setMonitored(v.key, on); })
        ]),
        el('td', { 'class': 'encore-thumb-cell' }, [
          v.thumbnail ? el('img', { 'class': 'encore-thumb', src: v.thumbnail, loading: 'lazy', alt: '' }) : el('div', { 'class': 'encore-thumb' })
        ]),
        el('td', {}, [
          el('a', { 'class': 'encore-title', href: v.url, target: '_blank', rel: 'noopener', title: v.sourceTitle, text: v.title }),
          sub ? el('div', { 'class': 'encore-sub', text: sub }) : null
        ]),
        el('td', { 'class': 'encore-hide-small', text: v.kindLabel }),
        el('td', { 'class': 'encore-hide-small', text: [v.quality, size(v.size)].filter(Boolean).join(' · ') }),
        el('td', { 'class': 'encore-status ' + (v.monitored ? v.status : 'Unmonitored'), title: v.error || null, text: statusText(v) }),
        el('td', { 'class': 'encore-row-actions' }, [
          searchButton(v.status === 'Downloaded' ? 'Download again' : 'Download', v.queued || downloading, function () { act('/download', { key: v.key }); })
        ])
      ]);
    }

    function render() {
      var videos = data ? data.videos : [];
      var have = videos.filter(function (v) { return v.status === 'Downloaded'; });
      // Counted like Lidarr counts albums: the monitored ones plus any already on disk
      var counted = videos.filter(function (v) { return v.monitored || v.status === 'Downloaded'; });
      var anyMonitored = videos.some(function (v) { return v.monitored; });
      var groupMonitored = !!data && (data.monitorNew || anyMonitored);
      var missing = counted.length - have.length;
      var bytes = have.reduce(function (sum, v) { return sum + (v.size || 0); }, 0);
      var searching = !!data && (data.searching || videos.some(function (v) { return v.queued; }) || !!(worker && worker.artistId === artist.id));

      var note = '';
      if (error) note = error;
      else if (data && data.searching) note = 'Searching…';
      else if (worker && worker.artistId === artist.id && worker.activity) note = worker.activity + (worker.progress ? ' · ' + worker.progress : '');
      else if (bytes) note = size(bytes);

      var kind = !counted.length ? '' : !missing ? 'success' : groupMonitored && data.monitored ? 'danger' : 'warning';

      var head = el('div', { 'class': 'encore-head', onclick: function () {
        expanded = !expanded;
        remember('encore.videos.expanded', expanded ? '1' : '0');
        render();
      } }, [
        el('div', { 'class': 'encore-left' }, [
          monitorToggle(groupMonitored, 24,
            groupMonitored ? 'Unmonitor all music videos, including ones found later' : 'Monitor all music videos, including ones found later',
            function (on) { setMonitored(null, on); }),
          el('span', { 'class': 'encore-label', text: 'Music Videos' }),
          el('span', {
            'class': 'encore-count ' + kind,
            title: have.length + ' downloaded, ' + missing + ' monitored and missing, ' + (videos.length - counted.length) + ' unmonitored',
            text: have.length + ' / ' + counted.length
          }),
          note ? el('span', { 'class': 'encore-muted', title: note, text: note }) : null
        ]),
        el('div', { 'class': 'encore-expand' }, [icon(CHEVRON, 'encore-chevron')]),
        el('div', { 'class': 'encore-actions' }, [
          searchButton('Look for new music videos and download the monitored ones that are missing', searching, function () {
            act('/download-all').then(function () { return act('/search'); });
          })
        ])
      ]);

      var body;
      if (!data) {
        body = el('div', { 'class': 'encore-empty', text: error || 'Loading…' });
      } else if (!videos.length) {
        body = el('div', { 'class': 'encore-empty', text: data.lastSearch ? 'No music videos found yet' : 'Not searched yet. Use the search button to look for music videos.' });
      } else {
        body = el('table', { 'class': 'encore-table' }, [
          el('thead', {}, [el('tr', {}, [
            el('th', {}), el('th', {}), el('th', { text: 'Title' }), el('th', { 'class': 'encore-hide-small', text: 'Type' }),
            el('th', { 'class': 'encore-hide-small', text: 'Quality' }), el('th', { text: 'Status' }), el('th', {})
          ])]),
          el('tbody', {}, videos.map(row))
        ]);
      }

      var foot = el('div', { 'class': 'encore-foot' }, [
        el('a', { href: urlBase + '/videos/' + artist.id, text: 'Manage music videos' })
      ]);

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
