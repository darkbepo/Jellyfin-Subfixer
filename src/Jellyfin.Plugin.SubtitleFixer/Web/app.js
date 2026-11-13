/* Subtitle Fixer page (UI.md). Vanilla JS, no build step, no external resources.
   ?mock=1 runs against mock-data.js instead of the server. The server enforces every rule; this page mirrors them. */
(function () {
    'use strict';

    var params = new URLSearchParams(window.location.search);
    var MOCK = params.get('mock') === '1';
    var PAGE = MOCK ? 25 : 50;          // small pages in mock mode so "load more" can be exercised
    var SEASON_FETCH = 200;             // server page cap
    var apiRoot = new URL('./', window.location.href).href;  // .../SubtitleFixer/
    var $ = function (id) { return document.getElementById(id); };

    // What SubSync is doing for the running item (StatusItemDto.stage). Unknown stages read as "working".
    var STAGES = {
        queued: 'starting',
        reading_audio: 'reading the audio',
        listening: 'listening to the dialogue',
        matching: 'matching the lines',
        waiting_for_file: 'waiting for the subtitle file to be free',
        working: 'working'
    };
    // The running item's highest share shown so far, so the bar never moves backwards (jobId -> 0..0.95).
    var shownPart = { jobId: null, part: 0 };
    var MESSAGES = {
        subsync_unavailable: 'SubSync is not configured. Ask an administrator.',
        forbidden: 'An administrator has not allowed your account to use Subtitle Fixer.',
        unauthorized: 'Your Jellyfin sign-in has expired. Sign in again.',
        not_found: 'That item was not found, or you cannot access it.',
        network: 'Can’t reach the server.'
    };
    var REASONS = {
        no_video_file: 'video file missing',
        already_synced: 'already synced',
        no_english_audio: 'no English audio',
        no_sidecar: 'no English .srt subtitle (.en, .eng, .en.hi or .eng.hi)'
    };

    // ---------- state ----------
    var me = null;
    var view = { mode: 'browse', parentId: null, parent: null, items: [], total: 0, query: '', loading: false, focusId: null };
    var selected = new Map();        // episodeId -> { seriesId, seasonId }
    var seasonEligible = new Map();  // seasonId -> array of eligible episode ids (complete list)
    var seasonInfo = new Map();      // seasonId -> { seriesId, eligibleCount }
    var queueByItem = new Map();     // itemId -> job status (visible items only); a pending one wins over a finished one
    var queueByLang = new Map();     // itemId|language -> job (D50: an episode can have an English and a Danish job)
    var LANG_NAMES = { en: 'English', da: 'Danish' };
    var lastStatus = null;
    var preview = { pending: false, result: null, error: null };
    var retryAction = null;
    var bazarrEnabled = false;       // GET Bazarr/Info; Find subtitles shows only when true
    var findSeq = 0;                 // ignores answers to a dialog that was closed or reopened
    var spotlight = null;            // item id to scroll to and highlight in the queue after Get
    var letters = new Map();         // libraryId -> picked letter ('A'..'Z' or '#'); none means all (D47)

    // Per-viewer view settings (D47): cards or list on library pages, and two hide switches. Defaults: cards, hide
    // not eligible, show already synced (greyed out). Browser storage may be blocked; then the defaults apply.
    var VIEW_KEY = 'subtitle-fixer-view';
    var prefs = (function () {
        var p = { layout: 'cards', hideIneligible: true, hideSynced: false, findLang: '', findHideHi: false };
        try {
            var saved = JSON.parse(localStorage.getItem(VIEW_KEY) || '{}') || {};
            if (saved.layout === 'list' || saved.layout === 'cards') { p.layout = saved.layout; }
            if (typeof saved.hideIneligible === 'boolean') { p.hideIneligible = saved.hideIneligible; }
            if (typeof saved.hideSynced === 'boolean') { p.hideSynced = saved.hideSynced; }
            if (typeof saved.findLang === 'string') { p.findLang = saved.findLang.slice(0, 40); }
            if (typeof saved.findHideHi === 'boolean') { p.findHideHi = saved.findHideHi; }
        } catch (e) { /* defaults */ }
        return p;
    })();
    function savePrefs() {
        try { localStorage.setItem(VIEW_KEY, JSON.stringify(prefs)); } catch (e) { /* not remembered */ }
    }

    // ---------- API ----------
    function token() {
        try {
            var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            var servers = (creds.Servers || []).filter(function (s) { return s && s.AccessToken; });
            servers.sort(function (a, b) { return (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0); });
            return servers.length ? servers[0].AccessToken : null;
        } catch (e) {
            return null;
        }
    }

    function ApiError(status, code, message) {
        var e = new Error(message || MESSAGES[code] || ('Request failed (' + status + ').'));
        e.status = status;
        e.code = code || (status === 0 ? 'network' : 'http_' + status);
        return e;
    }

    // Only the token is sent: Jellyfin renames the user's device when Device/Version differ (API.md).
    function api(method, path, body) {
        var started = performance.now();
        var done = function (status, data) {
            logTiming(method + ' ' + path, status, Math.round(performance.now() - started));
            if (status >= 200 && status < 300) { return data; }
            throw new ApiError(status, data && data.error, data && data.message);
        };
        if (MOCK) {
            return window.SubtitleFixerMock.handle(method, path, body).then(function (r) { return done(r.status, r.body); });
        }
        var headers = { 'Authorization': 'MediaBrowser Token="' + (token() || '') + '"' };
        if (body !== undefined) { headers['Content-Type'] = 'application/json'; }
        return fetch(apiRoot + path, { method: method, headers: headers, body: body === undefined ? undefined : JSON.stringify(body) })
            .then(function (r) {
                return r.text().then(function (text) {
                    var data = null;
                    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
                    return done(r.status, data);
                });
            }, function () {
                logTiming(method + ' ' + path, 'network error', Math.round(performance.now() - started));
                throw new ApiError(0, 'network');
            });
    }

    function failed(err, retry) {
        if (err.code === 'network') {
            retryAction = retry || null;
            $('banner-text').textContent = MESSAGES.network;
            $('banner-retry').hidden = !retry;
            $('banner').hidden = false;
            return;
        }
        if (err.status === 401) { showGate('signin'); return; }
        showHint(err.message, true);
    }

    function clearBanner() { $('banner').hidden = true; retryAction = null; }

    function logTiming(what, status, ms) {
        var list = $('timings');
        if (!list) { return; }
        var li = document.createElement('li');
        li.textContent = ms + ' ms  ' + status + '  ' + what;
        list.insertBefore(li, list.firstChild);
        while (list.children.length > 40) { list.removeChild(list.lastChild); }
    }

    // ---------- small helpers ----------
    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (text != null) { e.textContent = text; }
        return e;
    }
    function icon(name) {
        var ns = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(ns, 'svg');
        svg.setAttribute('class', 'icon kind');
        svg.setAttribute('aria-hidden', 'true');
        var use = document.createElementNS(ns, 'use');
        use.setAttribute('href', '#i-' + name);
        svg.appendChild(use);
        return svg;
    }
    function pad(n) { return n < 10 ? '0' + n : String(n); }
    function code(item) {
        return (item.parentIndexNumber != null ? 'S' + pad(item.parentIndexNumber) : '') + (item.indexNumber != null ? 'E' + pad(item.indexNumber) : '');
    }
    function plural(n, one, many) { return n + ' ' + (n === 1 ? one : many); }
    function duration(seconds) {
        if (seconds == null) { return null; }
        var m = Math.round(seconds / 60);
        if (m < 1) { return 'under a minute'; }
        if (m < 60) { return m + ' min'; }
        return Math.floor(m / 60) + ' h ' + pad(m % 60) + ' min';
    }
    function isVideo(item) { return item.type === 'Episode' || item.type === 'Movie'; }
    // Selection lock: one series at a time, or movies (MOVIES-PLAN.md). Libraries are never locked.
    function lockKey(item) {
        if (item.type === 'Movie') { return 'movies'; }
        if (item.type === 'Series') { return item.id; }
        return item.seriesId || null;
    }
    function lockedSeries() { return selected.size ? selected.values().next().value.lock : null; }
    function otherSeries(item) { var l = lockedSeries(); var k = lockKey(item); return !!(l && k && k !== l); }
    function videoLabel(item) {
        if (item.type === 'Movie') { return item.name + (item.productionYear ? ' (' + item.productionYear + ')' : ''); }
        return (code(item) ? code(item) + ' · ' : '') + item.name;
    }

    // ---------- gates ----------
    function showGate(which) {
        $('gate-loading').hidden = true;
        $('gate-signin').hidden = which !== 'signin';
        $('gate-denied').hidden = which !== 'denied';
        $('app').hidden = which !== 'app';
        $('footer').hidden = which !== 'app';
    }

    // ---------- explorer ----------
    function showHint(text, isError) {
        var h = $('list-hint');
        h.textContent = text || '';
        h.hidden = !text;
        h.classList.toggle('error', !!isError);
    }

    // Every folder opened gets a history entry, so the browser's Back (a mouse back button, Alt+Left) goes up one
    // folder instead of moving the Jellyfin page behind the pop-up. In the pop-up the page is an iframe: its entries
    // join the window's history, and the browser drops them when the pop-up closes and removes the iframe.
    function browse(parentId, opts) {
        opts = opts || {};
        view.mode = 'browse';
        view.query = '';
        if (!opts.keepSearch) { $('q').value = ''; }
        var here = history.state && history.state.subtitleFixer ? history.state : null;
        if (!opts.fromHistory && !(here && !here.guard && here.parentId === (parentId || null))) {
            try { history.pushState({ subtitleFixer: true, parentId: parentId || null }, ''); } catch (e) { /* no history: Back leaves the page */ }
        }
        return loadPage(parentId, false, opts.focusId);
    }

    function onHistory(e) {
        var s = e.state;
        if (!s || !s.subtitleFixer) { return; }
        closeFind();
        if (s.guard) {
            // Back from the top level of the pop-up closes it, rather than leaving the pop-up open over another page.
            if (EMBEDDED) { window.parent.postMessage({ type: 'subtitle-fixer-close' }, window.location.origin); }
            return;
        }
        browse(s.parentId, { fromHistory: true });
    }

    function loadPage(parentId, append, focusId) {
        if (view.loading) { return Promise.resolve(); }
        view.loading = true;
        showHint('');
        var start = append ? view.items.length : 0;
        var letter = parentId ? letters.get(parentId) : null;  // only ever set for library pages
        var path = 'Browse?startIndex=' + start + '&limit=' + PAGE + (parentId ? '&parentId=' + encodeURIComponent(parentId) : '') +
            (letter ? '&startsWith=' + encodeURIComponent(letter) : '');
        return api('GET', path).then(function (page) {
            clearBanner();
            if (!append) {
                view.parentId = parentId;
                view.parent = page.parent;
                view.items = [];
            }
            if (focusId) { view.focusId = focusId; }
            view.items = view.items.concat(page.items);
            view.total = page.totalCount;
            noteSeasons(page);
            render();
            return true;
        }, function (err) {
            failed(err, function () { return append ? loadPage(parentId, true) : loadPage(parentId, false, focusId); });
            return false;
        }).then(function (ok) {
            view.loading = false;
            // Opening a search result: keep paging until the episode is on screen.
            var target = view.focusId;
            if (ok && target && view.items.length < view.total && !view.items.some(function (i) { return i.id === target; })) {
                return loadPage(parentId, true, target);
            }
            view.focusId = null;
            // Hidden items can leave "Load more" on screen, where the scroll observer won't fire again: keep paging
            // while it is in view (D47). Ends when everything is loaded or the button scrolls out of view.
            var more = $('more');
            if (ok && view.mode === 'browse' && view.parentId === parentId && !more.hidden) {
                var box = more.getBoundingClientRect();
                if (box.top < window.innerHeight + 200 && box.bottom > 0) { return loadPage(parentId, true); }
            }
        });
    }

    // Remember what we learn about seasons, for season checkboxes and the series lock.
    function noteSeasons(page) {
        page.items.forEach(function (item) {
            if (item.type === 'Season') { seasonInfo.set(item.id, { seriesId: item.seriesId, eligibleCount: item.eligibleCount }); }
        });
        if (page.parent && page.parent.type === 'Season' && page.totalCount <= view.items.length) {
            seasonEligible.set(page.parent.id, view.items.filter(function (i) { return i.eligible; }).map(function (i) { return i.id; }));
        }
    }

    function search(q) {
        view.mode = 'search';
        view.query = q;
        view.loading = true;
        return api('GET', 'Search?q=' + encodeURIComponent(q) + '&limit=30').then(function (page) {
            clearBanner();
            if (view.mode !== 'search' || view.query !== q) { return; }
            view.items = page.items;
            view.total = page.items.length;  // search shows one page; refine the term for more
            view.searchTotal = page.totalCount;
            render();
        }, function (err) { failed(err, function () { return search(q); }); })
            .then(function () { view.loading = false; });
    }

    // ---------- view: cards, letters, hiding what can't be fixed (D47) ----------
    function isLibraryPage() { return view.mode === 'browse' && !!view.parent && view.parent.type === 'Library'; }
    function useCards() { return isLibraryPage() && prefs.layout === 'cards'; }

    // Already synced: always shown, greyed out with "Already synced" (owner, 2026-11-12), never hidden.
    function isSynced(item) {
        if (item.type === 'Season') { return item.eligibleCount === 0 && item.syncedCount > 0; }
        if (!isVideo(item) || item.status !== 'done') { return false; }
        var job = queueByItem.get(item.id);
        return !(job && (job.status === 'queued' || job.status === 'running'));
    }

    // Which hide switch an item falls under: 'synced', 'ineligible', or null (always shown). Never hidden: series,
    // libraries, anything selected or in the queue, anything that can be fixed, and, only when Bazarr is set up,
    // items Find subtitles can help with. A season counts as synced when nothing in it is left to fix but some is synced.
    function hideKind(item) {
        if (item.type === 'Season') {
            if (item.eligibleCount > 0 || (bazarrEnabled && item.noSubtitleCount > 0)) { return null; }
            return item.syncedCount > 0 ? 'synced' : 'ineligible';
        }
        if (!isVideo(item) || item.eligible || selected.has(item.id) || queueByItem.has(item.id)) { return null; }
        if (item.status === 'done') { return 'synced'; }
        // Untagged audio nobody has checked yet stays in view: Check audio can make it fixable (D49).
        if (item.audioUntagged && !item.audioCheck) { return null; }
        return canFind(item) ? null : 'ineligible';
    }

    function shown(item) {
        if (view.mode === 'browse' && !view.parentId) { return true; }
        var kind = hideKind(item);
        return !(kind === 'ineligible' && prefs.hideIneligible) && !(kind === 'synced' && prefs.hideSynced);
    }

    // Jellyfin's own image endpoint: served without a token, cached for good with the tag (M0-SPIKES S2).
    function imageUrl(id, tag, height) {
        if (MOCK || !tag) { return null; }
        return apiRoot + '../Items/' + encodeURIComponent(id) + '/Images/Primary?fillHeight=' + height + '&quality=90&tag=' + encodeURIComponent(tag);
    }

    function initials(name) {
        var words = String(name || '').replace(/^the\s+/i, '').split(/\s+/).filter(Boolean);
        return ((words[0] || '?').charAt(0) + (words[1] ? words[1].charAt(0) : '')).toUpperCase();
    }

    // A 2:3 poster, or the name's initials when there is no image or it fails to load.
    function poster(id, tag, name, height) {
        var box = el('div', 'poster');
        var fallback = function () {
            box.textContent = '';
            box.classList.add('none');
            box.appendChild(el('span', null, initials(name)));
        };
        var src = imageUrl(id, tag, height);
        if (!src) { fallback(); return box; }
        var img = el('img');
        img.alt = '';
        img.loading = 'lazy';
        img.decoding = 'async';
        img.addEventListener('error', fallback);
        img.src = src;
        box.appendChild(img);
        return box;
    }

    function renderTools() {
        var libraryPage = isLibraryPage();
        var applies = view.mode === 'search' || !!view.parentId;
        $('tools').hidden = !applies;
        $('hide-ineligible').checked = prefs.hideIneligible;
        $('hide-synced').checked = prefs.hideSynced;
        $('layout').hidden = !libraryPage;
        Array.prototype.forEach.call($('layout').querySelectorAll('button'), function (b) {
            b.setAttribute('aria-pressed', String(b.dataset.layout === prefs.layout));
        });

        var bar = $('letters');
        bar.hidden = !libraryPage;
        if (!libraryPage) { return; }
        var current = letters.get(view.parentId) || null;
        bar.textContent = '';
        ['', '#'].concat('ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('')).forEach(function (l) {
            var b = el('button', 'letter', l || 'All');
            b.type = 'button';
            b.setAttribute('aria-pressed', String((l || null) === current));
            if (l === '#') { b.setAttribute('aria-label', 'Names starting with a number or symbol'); }
            b.addEventListener('click', function () { pickLetter(l || null); });
            bar.appendChild(b);
        });
    }

    function pickLetter(l) {
        if (!view.parentId) { return; }
        if (l) { letters.set(view.parentId, l); } else { letters.delete(view.parentId); }
        loadPage(view.parentId, false);
    }

    // Above a series' seasons and episodes: its poster and name, so it's clear which show this is.
    function renderHead() {
        var head = $('list-head');
        head.textContent = '';
        var trail = view.mode === 'browse' && view.parent ? view.parent.breadcrumb : [];
        var series = trail.filter(function (c) { return c.type === 'Series'; })[0];
        head.hidden = !series;
        if (!series) { return; }
        head.appendChild(poster(series.id, series.imageTag, series.name, 240));
        var text = el('div', 'head-text');
        text.appendChild(el('strong', null, series.name));
        var sub = view.parent.type === 'Season' ? view.parent.name + ' · ' + plural(view.total, 'episode', 'episodes')
            : plural(view.total, 'season', 'seasons');
        text.appendChild(el('span', 'muted small', sub));
        head.appendChild(text);
    }

    function itemEl(item) { return useCards() ? card(item) : row(item); }

    function render() {
        renderCrumbs();
        renderTools();
        renderHead();
        var list = $('list');
        list.textContent = '';
        list.classList.toggle('cards', useCards());
        var visible = view.items.filter(shown);
        var hidden = view.items.length - visible.length;
        var hiddenSynced = view.items.filter(function (i) { return !shown(i) && hideKind(i) === 'synced'; }).length;
        var hiddenIneligible = hidden - hiddenSynced;
        visible.forEach(function (item) { list.appendChild(itemEl(item)); });

        var empty = $('list-empty');
        if (!visible.length) {
            empty.textContent = hidden ? 'Nothing to show with these filters.' :
                view.mode === 'search' ? 'No series or episodes match “' + view.query + '”.' :
                view.parentId ? (letters.get(view.parentId) ? 'Nothing starts with ' + letters.get(view.parentId) + '.' : 'Nothing here.') :
                'No TV libraries you can access.';
        }
        empty.hidden = visible.length > 0;

        var note = $('hidden-note');
        note.textContent = '';
        note.hidden = !hidden;
        // "Hidden: 3 not eligible (Show) · 5 already synced (Show)"
        var addPart = function (count, text, turnOff) {
            if (!count) { return; }
            if (note.childNodes.length > 1) { note.appendChild(document.createTextNode(' · ')); }
            note.appendChild(document.createTextNode(count + ' ' + text + ' '));
            var show = el('button', 'linkish', 'Show');
            show.type = 'button';
            show.setAttribute('aria-label', 'Show ' + text + ' items');
            show.addEventListener('click', turnOff);
            note.appendChild(show);
        };
        if (hidden) {
            note.appendChild(document.createTextNode('Hidden: '));
            addPart(hiddenIneligible, 'not eligible', function () { setHide('hideIneligible', false); });
            addPart(hiddenSynced, 'already synced', function () { setHide('hideSynced', false); });
        }

        if (view.mode === 'search') {
            showHint(view.searchTotal > view.items.length ? 'Showing ' + view.items.length + ' of ' + view.searchTotal + ' matches. Type more to narrow it down.' : '');
        } else if (lockedSeries() === 'movies' && view.parent && (view.parent.type === 'Series' || view.parent.type === 'Season')) {
            showHint('Your selection is movies. Clear it to choose episodes.');
        } else if (lockedSeries() && lockedSeries() !== 'movies' && view.items.some(function (i) { return i.type === 'Movie'; })) {
            showHint('Your selection is episodes of a series. Clear it to choose movies.');
        } else if (view.parent && view.parent.type === 'Series' && lockedSeries() && lockedSeries() !== view.parent.id) {
            showHint('Your selection is from another series. Clear it to choose from this one.');
        } else {
            showHint('');
        }

        var more = $('more');
        more.hidden = view.mode === 'search' || view.items.length >= view.total;
        more.textContent = 'Load more (' + (view.total - view.items.length) + ' left)';

        rovingInit();
        if (view.focusId) {
            var target = list.querySelector('[data-id="' + CSS.escape(view.focusId) + '"]');
            if (target) {
                focusRow(target);
                target.scrollIntoView({ block: 'center' });
                target.classList.add('flash');
                view.focusId = null;
            }
        }
    }

    function renderCrumbs() {
        var nav = $('crumbs');
        nav.textContent = '';
        var trail = view.mode === 'search'
            ? [{ id: null, name: 'Libraries' }, { id: '#search', name: 'Search: “' + view.query + '”' }]
            : [{ id: null, name: 'Libraries' }].concat(view.parent ? view.parent.breadcrumb : []);
        // At the top level the panel heading already says "Libraries"; a lone crumb would repeat it.
        nav.hidden = trail.length < 2;
        trail.forEach(function (c, i) {
            if (i) { nav.appendChild(el('span', 'sep', '›')); }
            var b = el('button', null, c.name);
            b.type = 'button';
            if (i === trail.length - 1) { b.setAttribute('aria-current', 'page'); }
            else { b.addEventListener('click', function () { browse(c.id); }); }
            nav.appendChild(b);
        });
    }

    function row(item) {
        var li = el('li', 'row');
        li.dataset.id = item.id;
        li.dataset.type = item.type;
        li.tabIndex = -1;
        if (isSynced(item)) { li.classList.add('synced'); }
        var dim = otherSeries(item);
        var lockTip = 'Clear your selection to choose from here';
        if (dim) { li.classList.add('dimmed'); li.title = lockTip; }

        // Checkbox column (seasons, episodes and movies).
        if (item.type === 'Season' || isVideo(item)) {
            var box = el('input');
            box.type = 'checkbox';
            box.setAttribute('aria-label', 'Select ' + (isVideo(item) ? videoLabel(item) : item.name));
            if (isVideo(item)) {
                var inQueue = queueByItem.get(item.id);
                var pending = inQueue && (inQueue.status === 'queued' || inQueue.status === 'running');
                box.checked = selected.has(item.id);
                box.disabled = dim || !item.eligible || !!pending;
                if (!item.eligible) { box.title = 'Not eligible: ' + reasonText(item); }
                if (pending) { box.title = 'Already in the queue'; }
            } else {
                applySeasonBox(box, item);
                box.disabled = dim || item.eligibleCount === 0;
                if (item.eligibleCount === 0) { box.title = 'No episodes in this season can be fixed'; }
            }
            if (dim) { box.title = lockTip; }
            box.addEventListener('change', function () { toggle(item, box.checked); });
            box.addEventListener('click', function (e) { e.stopPropagation(); });
            li.appendChild(box);
        } else {
            li.appendChild(el('span', 'check-slot'));
        }

        li.appendChild(icon(item.type === 'Library' ? (item.libraryType === 'movies' ? 'movie' : 'library') :
            item.type === 'Series' ? 'series' : item.type === 'Season' ? 'season' : item.type === 'Movie' ? 'movie' : 'episode'));

        var main = el('div', 'main');
        var name = el('span', 'name');
        var label = isVideo(item) ? videoLabel(item) : item.name;
        if (item.type === 'Episode' && view.mode === 'search') {
            // Search results open like browsing: an episode opens its season with the episode highlighted.
            var go = el('button', null, label);
            go.type = 'button';
            go.tabIndex = -1;
            go.title = 'Show in its season';
            go.addEventListener('click', function (e) { e.stopPropagation(); openItem(item); });
            name.appendChild(go);
        } else if (isVideo(item)) {
            name.textContent = label;
        } else {
            var open = el('button', null, item.name);
            open.type = 'button';
            open.tabIndex = -1;
            open.addEventListener('click', function (e) { e.stopPropagation(); openItem(item); });
            name.appendChild(open);
            li.classList.add('openable');
            li.addEventListener('click', function () { openItem(item); });
        }
        main.appendChild(name);
        var sub = secondary(item);
        if (sub) { main.appendChild(el('span', 'sub', sub)); }
        li.appendChild(main);

        var badges = el('div', 'badges');
        badgesFor(item).forEach(function (b) { badges.appendChild(b); });
        if (badges.children.length) { li.appendChild(badges); }

        // EN / DA subtitle chips (D52): grey when missing (click: find one), coloured when there (click: a menu).
        if (isVideo(item) && item.subtitles) { li.appendChild(subChips(item, label)); }

        if (canCheck(item)) { li.appendChild(checkButton(item, false)); }

        if (isVideo(item) && !li.classList.contains('openable')) {
            li.addEventListener('click', function (e) {
                if (e.target.closest('button,input,a')) { return; }
                var b = li.querySelector('input[type="checkbox"]');
                if (b && !b.disabled) { b.checked = !b.checked; toggle(item, b.checked); }
            });
        }
        return li;
    }

    // A card on a library page (D47): poster, name, and for movies the checkbox, year and badges. Keeps the .row
    // class, so focus, queue updates and selection treat it like a row.
    function card(item) {
        var li = el('li', 'row card');
        li.dataset.id = item.id;
        li.dataset.type = item.type;
        li.tabIndex = -1;
        if (isSynced(item)) { li.classList.add('synced'); }
        var dim = otherSeries(item);
        var lockTip = 'Clear your selection to choose from here';
        if (dim) { li.classList.add('dimmed'); li.title = lockTip; }
        var label = isVideo(item) ? videoLabel(item) : item.name;

        var art = poster(item.id, item.imageTag, item.name, 330);
        li.appendChild(art);

        if (isVideo(item)) {
            var box = el('input');
            box.type = 'checkbox';
            box.setAttribute('aria-label', 'Select ' + label);
            var inQueue = queueByItem.get(item.id);
            var pending = inQueue && (inQueue.status === 'queued' || inQueue.status === 'running');
            box.checked = selected.has(item.id);
            box.disabled = dim || !item.eligible || !!pending;
            if (!item.eligible) { box.title = 'Not eligible: ' + reasonText(item); }
            if (pending) { box.title = 'Already in the queue'; }
            if (dim) { box.title = lockTip; }
            box.addEventListener('change', function () { toggle(item, box.checked); });
            box.addEventListener('click', function (e) { e.stopPropagation(); });
            art.appendChild(box);
            if (box.checked) { li.classList.add('picked'); }
            li.addEventListener('click', function (e) {
                if (e.target.closest('button,input,a')) { return; }
                if (!box.disabled) { box.checked = !box.checked; toggle(item, box.checked); }
            });
        } else {
            li.classList.add('openable');
            li.addEventListener('click', function () { openItem(item); });
        }

        var name = el('span', 'name');
        if (isVideo(item)) {
            name.textContent = item.name;
        } else {
            var open = el('button', null, item.name);
            open.type = 'button';
            open.tabIndex = -1;
            open.addEventListener('click', function (e) { e.stopPropagation(); openItem(item); });
            name.appendChild(open);
        }
        li.appendChild(name);
        if (item.type === 'Movie' && item.productionYear) { li.appendChild(el('span', 'sub', String(item.productionYear))); }

        var badges = el('div', 'badges');
        badgesFor(item).forEach(function (b) { badges.appendChild(b); });
        if (isVideo(item) && item.subtitles) { badges.appendChild(subChips(item, label)); }
        if (canCheck(item)) { badges.appendChild(checkButton(item, true)); }
        if (badges.children.length) { li.appendChild(badges); }
        return li;
    }

    function setHide(which, on) {
        prefs[which] = on;
        savePrefs();
        render();
    }

    function setLayout(layout) {
        if (prefs.layout === layout) { return; }
        prefs.layout = layout;
        savePrefs();
        render();
    }

    function secondary(item) {
        switch (item.type) {
            case 'Library': return item.libraryType === 'movies' ? 'Movie library' : 'TV library';
            case 'Movie':
                var bits = [];
                if (view.mode === 'search') { bits.push('Movie'); }
                if (item.status === 'attempted' && item.message) { bits.push('Last try: ' + item.message); }
                return bits.join(' · ') || null;
            case 'Series': return view.mode === 'search' ? 'Series' : null;
            case 'Season':
                return plural(item.episodeCount, 'episode', 'episodes') + ', ' + item.syncedCount + ' synced, ' + item.eligibleCount + ' can be fixed';
            case 'Episode':
                var parts = [];
                if (view.mode === 'search' && item.seriesName) { parts.push(item.seriesName); }
                if (item.status === 'attempted' && item.message) { parts.push('Last try: ' + item.message); }
                return parts.join(' · ') || null;
            default: return null;
        }
    }

    // Untagged audio (und or empty) may well be English, but the file doesn't say so (D48). Check audio asks SubSync
    // which language is spoken and remembers it (D49).
    function reasonText(item) {
        if (item.reason === 'no_english_audio' && item.audioUntagged) {
            var c = item.audioCheck;
            if (!c) { return 'audio language not tagged'; }
            return c.language ? 'audio sounds like ' + String(c.language).toUpperCase() : 'no speech found in the audio';
        }
        return REASONS[item.reason] || item.reason;
    }
    function reasonTip(item) {
        if (item.reason === 'no_english_audio' && item.audioUntagged) {
            return item.audioCheck ? item.audioCheck.message
                : 'The audio track doesn’t say which language it is. Press Check audio to find out, or tag it as English (for example with MKVToolNix) and rescan the item.';
        }
        return REASONS[item.reason];
    }

    function canCheck(item) {
        if (!isVideo(item) || !item.audioUntagged || (item.audioCheck && item.audioCheck.english)) { return false; }
        var job = queueByItem.get(item.id);
        return !(job && (job.status === 'queued' || job.status === 'running'));
    }

    function checkButton(item, compact) {
        var b = el('button', 'check-btn');
        b.type = 'button';
        b.tabIndex = -1;
        var again = !!item.audioCheck;
        b.title = 'Ask SubSync which language is spoken (about a minute; nothing is changed)';
        b.setAttribute('aria-label', (again ? 'Check the audio again for ' : 'Check the audio of ') + videoLabel(item));
        b.appendChild(el('span', 'check-text', compact ? (again ? 'Again' : 'Check') : (again ? 'Check again' : 'Check audio')));
        b.addEventListener('click', function (e) { e.stopPropagation(); checkAudio(item, b); });
        // Rows are redrawn while a check runs (queue polling): keep showing it as running.
        if (checking === item.id) {
            b.disabled = true;
            b.classList.add('working');
            b.querySelector('.check-text').textContent = 'Checking…';
        } else if (checking) {
            b.disabled = true;
            b.title = 'Another check is running';
        }
        return b;
    }

    var checking = null;  // item id being checked; one at a time (the server allows one)
    function checkAudio(item, button) {
        if (checking) { return; }
        checking = item.id;
        button.disabled = true;
        button.classList.add('working');
        button.querySelector('.check-text').textContent = 'Checking…';
        Array.prototype.forEach.call($('list').querySelectorAll('.check-btn'), function (other) { if (other !== button) { other.disabled = true; } });
        // A check is a queue item (D49): it waits its turn like a sync, and the result shows in the queue.
        api('POST', 'AudioCheck', { itemId: item.id }).then(function (r) {
            checking = null;
            foldToQueue(r.queued ? 'Audio check of ' + videoLabel(item) + ' added to the queue.' : r.message, item.id);
        }, function (err) {
            checking = null;
            rerenderRows();
            failed(err, null);
        });
    }

    function badge(cls, text, title) {
        var b = el('span', 'badge ' + cls, text);
        if (title) { b.title = title; }
        return b;
    }

    function badgesFor(item) {
        var out = [];
        if (item.type === 'Season' && isSynced(item)) { out.push(badge('synced', 'All synced', 'Every episode here that can be re-timed already is')); }
        if (!isVideo(item)) { return out; }
        var job = queueByItem.get(item.id);
        if (job) {
            var checkJob = job.source === 'audio_check';
            var label = { queued: checkJob ? 'Check queued' : 'Queued', running: checkJob ? 'Checking audio…' : 'Syncing…', done: checkJob ? 'Audio checked' : 'Fixed', check: 'Check', failed: 'Failed', cancelled: 'Cancelled', skipped: 'Skipped' }[job.status] || job.status;
            out.push(badge(job.status, label, job.message));
            if (job.status === 'queued' || job.status === 'running') { return out; }
        }
        if (job && job.status === 'done') { return out; }
        if (item.status === 'done') { out.push(badge('synced', 'Already synced', 'Re-timed before; it runs again only if the video file changes')); }
        else if (!item.eligible && item.reason !== 'no_sidecar') { out.push(badge('', 'Not eligible: ' + reasonText(item), reasonTip(item))); }
        else if (item.status === 'attempted') { out.push(badge('attempted', 'Previously attempted', item.message)); }
        if (!item.subtitles && item.subtitleHi) { out.push(badge('hi', 'HI', 'The English subtitle is for the hearing impaired (.hi.srt): it includes sound descriptions')); }
        if (item.audioCheck && item.audioCheck.english) { out.push(badge('checked', 'Audio checked: English', item.audioCheck.message)); }
        return out;
    }

    // ---------- Subtitle chips (D52) ----------
    // One chip per language. Grey: no such subtitle next to the video; a click searches for one (when Find subtitles
    // is set up). Coloured: it's there (green when synced); a click opens a small menu: Sync again (or Sync) and
    // Download new. "Outdated" marks a sync by SubSync before 1.4.1, whose lines could flash by too fast to read.
    function subChips(item, label) {
        var box = el('div', 'subs');
        item.subtitles.forEach(function (s) {
            var name = LANG_NAMES[s.language] || s.language;
            var job = queueByLang.get(item.id + '|' + s.language);
            var pending = job && (job.status === 'queued' || job.status === 'running');
            var chip = el('button', 'sub-chip ' + (s.present ? 'present' : 'missing'));
            chip.type = 'button';
            chip.tabIndex = 0;  // reachable with Tab; Enter / Space opens it
            chip.dataset.lang = s.language;
            if (s.present && s.status === 'done') { chip.classList.add('synced'); }
            if (s.outdated) { chip.classList.add('outdated'); }
            if (pending) { chip.classList.add('busy'); }
            chip.appendChild(el('span', 'code', s.language.toUpperCase()));
            if (s.hi) { chip.appendChild(el('span', 'hi', 'HI')); }
            var tip;
            if (pending) {
                tip = name + ' subtitle: ' + (job.status === 'running' ? 'syncing now' : 'in the queue');
            } else if (!s.present) {
                tip = 'No ' + name + ' subtitle' + (canFindLang(item, s) ? '. Click to find one.' : '');
            } else {
                tip = name + ' subtitle' + (s.hi ? ' for the hearing impaired' : '') + ': '
                    + (s.status === 'done' ? 'synced' + (s.syncedWith ? ' with SubSync ' + s.syncedWith : '') : s.status === 'attempted' ? 'tried before' + (s.message ? ' (' + s.message + ')' : '') : 'not synced yet')
                    + (s.outdated ? '. Lines may flash by too fast to read; Sync again fixes that.' : '.') + ' Click for options.';
            }
            chip.title = tip;
            chip.setAttribute('aria-label', tip + ' (' + label + ')');
            var usable = pending ? false : s.present ? true : canFindLang(item, s);
            if (!usable) { chip.setAttribute('aria-disabled', 'true'); }
            chip.addEventListener('click', function (e) {
                e.stopPropagation();
                if (!usable) { return; }
                if (!s.present) { openFind(item, s.language, false); return; }
                openChipMenu(chip, item, s);
            });
            box.appendChild(chip);
        });
        return box;
    }

    function canFindLang(item, s) {
        if (!bazarrEnabled || s.reason === 'no_english_audio' || s.reason === 'no_video_file') { return false; }
        var job = queueByLang.get(item.id + '|' + s.language);
        return !(job && (job.status === 'queued' || job.status === 'running'));
    }

    var chipMenu = null, chipMenuFrom = null;
    function closeChipMenu(refocus) {
        if (chipMenu) { chipMenu.remove(); chipMenu = null; }
        if (refocus && chipMenuFrom && document.body.contains(chipMenuFrom)) { chipMenuFrom.focus(); }
        chipMenuFrom = null;
    }
    function openChipMenu(chip, item, s) {
        closeChipMenu();
        var name = LANG_NAMES[s.language] || s.language;
        var menu = el('div', 'chip-menu');
        menu.setAttribute('role', 'menu');
        var add = function (text, enabled, why, action) {
            var b = el('button', null, text);
            b.type = 'button';
            b.setAttribute('role', 'menuitem');
            b.disabled = !enabled;
            if (why) { b.title = why; }
            b.addEventListener('click', function (e) { e.stopPropagation(); closeChipMenu(); action(); });
            menu.appendChild(b);
        };
        var canSync = s.reason !== 'no_english_audio' && s.reason !== 'no_video_file' && !(lastStatus && lastStatus.subsyncAvailable === false);
        add(s.status === 'done' ? 'Sync again' : 'Sync', canSync, canSync ? null : 'Needs English audio and SubSync', function () { syncAgain(item, s.language); });
        if (bazarrEnabled) {
            add('Download new', canFindLang(item, s), null, function () { openFind(item, s.language, true); });
        }
        document.body.appendChild(menu);
        var r = chip.getBoundingClientRect();
        menu.style.top = (window.scrollY + r.bottom + 4) + 'px';
        menu.style.left = Math.max(8, Math.min(window.scrollX + r.left, window.scrollX + document.documentElement.clientWidth - menu.offsetWidth - 8)) + 'px';
        menu.setAttribute('aria-label', name + ' subtitle');
        chipMenu = menu;
        chipMenuFrom = chip;
        // arrow keys move between the menu's items; Tab leaves it
        menu.addEventListener('keydown', function (e) {
            var items = Array.prototype.filter.call(menu.querySelectorAll('button'), function (b) { return !b.disabled; });
            var at = items.indexOf(document.activeElement);
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                e.preventDefault();
                if (items.length) { items[(at + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length].focus(); }
            } else if (e.key === 'Tab') {
                closeChipMenu(false);
            }
        });
        var first = menu.querySelector('button:not(:disabled)');
        if (first) { first.focus(); }
    }
    document.addEventListener('click', function (e) { if (chipMenu && !chipMenu.contains(e.target)) { closeChipMenu(); } });
    document.addEventListener('keydown', function (e) {
        // only the menu closes: the pop-up's own Escape handler skips handled keys
        if (e.key === 'Escape' && chipMenu) { e.preventDefault(); e.stopPropagation(); closeChipMenu(true); }
    }, true);

    // Sync again (D51): one episode or movie, one language, also when it is already synced.
    function syncAgain(item, lang) {
        api('POST', 'Jobs', { itemIds: [item.id], languages: [lang], again: true }).then(function () {
            showHint(videoLabel(item) + ': the ' + LANG_NAMES[lang] + ' subtitle is in the queue to be synced again.');
            pollNow();
        }, function (err) { failed(err); });
    }

    // ---------- Find subtitles (BAZARR-PLAN.md phase 1: read-only) ----------
    function canFind(item) {
        if (!bazarrEnabled || !isVideo(item) || item.reason !== 'no_sidecar') { return false; }
        var job = queueByItem.get(item.id);
        return !(job && (job.status === 'queued' || job.status === 'running'));
    }

    // lang: the chip it was opened from (en / da, D52), or none; replace: Download new (the subtitle that is there
    // is replaced, and the previous one kept as .old).
    function openFind(item, lang, replace) {
        var dlg = $('find');
        var seq = ++findSeq;
        $('find-item').textContent = videoLabel(item) + (item.type === 'Episode' && item.seriesName ? ' · ' + item.seriesName : '')
            + (lang ? ' · ' + (replace ? 'new ' : '') + LANG_NAMES[lang] + ' subtitle' : '');
        $('find-list').replaceChildren();
        dlg.classList.remove('has-hits');
        setFindStatus('busy', 'Searching for subtitles. This can take a while…');
        // Rows are redrawn while the dialog is open (queue polling), so remember the item, not the button.
        dlg.returnTo = item.id;
        if (!dlg.open) { dlg.showModal(); }

        findState = { item: item, results: null, lang: lang || null, replace: !!replace };
        $('find-lang').value = lang ? lang.toUpperCase() : prefs.findLang;
        $('find-hide-hi').checked = prefs.findHideHi;
        var query = 'Bazarr/Search?itemId=' + encodeURIComponent(item.id) + (lang ? '&language=' + lang : '') + (replace ? '&replace=true' : '');
        api('GET', query).then(function (r) {
            if (seq !== findSeq || !dlg.open) { return; }
            findState.results = r.results;
            applyFindFilters();
        }, function (err) {
            if (seq !== findSeq || !dlg.open) { return; }
            setFindStatus('error', err.message);
        });
    }

    // Show only (language codes, "EN" or "DA, EN") and Hide hearing impaired, both remembered per viewer (D48).
    var findState = { item: null, results: null, lang: null, replace: false };
    function findLanguages() {
        if (findState.lang) { return [findState.lang]; }  // opened from a chip: that language, until the box is edited
        return prefs.findLang.toLowerCase().split(/[\s,;]+/).filter(Boolean);
    }
    function findKeeps(h) {
        if (prefs.findHideHi && h.hearingImpaired) { return false; }
        var langs = findLanguages();
        var code = String(h.languageCode || '').toLowerCase();
        return !langs.length || langs.some(function (l) { return code === l || code.indexOf(l + '-') === 0; });
    }
    function applyFindFilters() {
        var dlg = $('find');
        var all = findState.results;
        if (!all) { return; }
        var shownHits = all.filter(findKeeps);
        renderHits(shownHits, findState.item);
        dlg.classList.toggle('has-hits', shownHits.length > 0);
        var left = all.length - shownHits.length;
        if (!all.length) {
            setFindStatus('empty', 'No subtitles found.');
        } else if (!shownHits.length) {
            setFindStatus('empty', plural(all.length, 'subtitle', 'subtitles') + ' found, but none match your filters.');
        } else {
            setFindStatus('', plural(shownHits.length, 'subtitle', 'subtitles') + ' found, best match first.' + (left ? ' ' + left + ' hidden by your filters.' : ''));
        }
    }

    function setFindStatus(state, text) {
        var s = $('find-status');
        s.dataset.state = state;
        s.textContent = text;
    }

    // Get: the server has Bazarr download the result, then it joins the shared queue to be re-timed (BAZARR-PLAN.md
    // phase 2). The page doesn't mention Bazarr or providers: people only need the subtitle (UI.md).
    // No confirmation: Find subtitles only shows where no English subtitle exists (D28).
    function getHit(item, hit, button) {
        var dlg = $('find');
        var seq = findSeq;
        var buttons = Array.prototype.slice.call($('find-list').querySelectorAll('.hit-get'));
        buttons.forEach(function (b) { b.disabled = true; });
        button.classList.add('working');
        button.querySelector('.get-text').textContent = 'Getting…';
        setFindStatus('busy', 'Downloading the subtitle. This usually takes a few seconds…');

        api('POST', 'Bazarr/Download', { itemId: item.id, resultId: hit.id, replace: !!findState.replace }).then(function (r) {
            if (r.queued) {
                closeFind();
                foldToQueue('Subtitle downloaded. It’s in the queue to be re-timed to the audio.', item.id);
                return;
            }
            if (seq !== findSeq || !dlg.open) { return; }
            // Another language (kept, not re-timed), or English when the queue couldn't take it: the server says which.
            setFindStatus('', r.message || 'Subtitle downloaded.');
            button.classList.remove('working');
            button.querySelector('.get-text').textContent = 'Downloaded';
        }, function (err) {
            if (seq !== findSeq || !dlg.open) { return; }
            setFindStatus('error', err.message);
            button.classList.remove('working');
            button.querySelector('.get-text').textContent = 'Get';
            // A gone or replaced result can't be retried; everything else can.
            var retry = ['result_expired', 'has_subtitle', 'not_found'].indexOf(err.code) < 0;
            buttons.forEach(function (b) { b.disabled = !retry; });
        });
    }

    function renderHits(results, item) {
        var list = $('find-list');
        list.replaceChildren();
        results.forEach(function (h) {
            var li = el('li', 'hit');

            var score = el('span', 'hit-score');
            score.appendChild(el('strong', null, h.score + '%'));
            var meter = el('span', 'meter');
            var fill = el('span', h.score >= 90 ? 'good' : h.score >= 70 ? 'fair' : 'poor');
            fill.style.width = Math.max(0, Math.min(100, h.score)) + '%';
            meter.appendChild(fill);
            score.appendChild(meter);
            li.appendChild(score);

            var lang = el('span', 'hit-lang');
            lang.appendChild(el('span', 'lang', String(h.languageCode || 'en').toUpperCase()));
            if (h.hearingImpaired) { lang.appendChild(badge('hi', 'HI', 'Hearing impaired: includes sound descriptions')); }
            li.appendChild(lang);

            var rel = el('span', 'hit-release');
            var releases = h.releases && h.releases.length ? h.releases : ['—'];
            if (releases.length > 1) {
                var more = el('details');
                var sum = el('summary', null, releases[0]);
                sum.appendChild(el('span', 'more-count', ' +' + (releases.length - 1)));
                more.appendChild(sum);
                releases.slice(1).forEach(function (r) { more.appendChild(el('span', 'alt-release', r)); });
                rel.appendChild(more);
            } else {
                rel.textContent = releases[0];
                rel.title = releases[0];
            }
            li.appendChild(rel);

            var get = el('button', 'hit-get primary');
            get.type = 'button';
            get.setAttribute('aria-label', 'Get the ' + h.score + '% subtitle' + (h.releases && h.releases[0] ? ' for ' + h.releases[0] : ''));
            var gi = icon('download');
            gi.setAttribute('class', 'icon');
            get.appendChild(gi);
            get.appendChild(el('span', 'get-text', 'Get'));
            get.addEventListener('click', function () { getHit(item, h, get); });
            li.appendChild(get);

            list.appendChild(li);
        });
    }

    function humanize(key) { return String(key).replace(/_/g, ' '); }

    function closeFind() {
        findSeq++;
        if ($('find').open) { $('find').close(); }
    }

    function openItem(item) {
        if (item.type === 'Movie') { return; }  // movies have nothing to open; their row toggles the checkbox
        if (item.type === 'Episode') {
            if (item.seasonId) { browse(item.seasonId, { focusId: item.id }); }
            return;
        }
        browse(item.id);
    }

    function goUp() {
        if (view.mode === 'search') { browse(view.parentId); return; }
        var trail = view.parent ? view.parent.breadcrumb : [];
        if (trail.length >= 2) { browse(trail[trail.length - 2].id); } else if (view.parentId) { browse(null); }
    }

    // ---------- selection ----------
    function applySeasonBox(box, season) {
        var ids = seasonEligible.get(season.id);
        var n = 0;
        selected.forEach(function (v) { if (v.seasonId === season.id) { n++; } });
        var total = ids ? ids.length : season.eligibleCount;
        box.checked = total > 0 && n === total;
        box.indeterminate = n > 0 && n < total;
    }

    function loadSeasonEligible(seasonId) {
        if (seasonEligible.has(seasonId)) { return Promise.resolve(seasonEligible.get(seasonId)); }
        var collected = [];
        var next = function (start) {
            return api('GET', 'Browse?parentId=' + encodeURIComponent(seasonId) + '&startIndex=' + start + '&limit=' + SEASON_FETCH).then(function (page) {
                collected = collected.concat(page.items);
                if (collected.length < page.totalCount && page.items.length) { return next(collected.length); }
                var ids = collected.filter(function (i) { return i.eligible; }).map(function (i) { return i.id; });
                seasonEligible.set(seasonId, ids);
                return ids;
            });
        };
        return next(0);
    }

    function toggle(item, on) {
        if (isVideo(item)) {
            if (on) { selected.set(item.id, { lock: lockKey(item), seasonId: item.seasonId || null }); } else { selected.delete(item.id); }
            selectionChanged();
            return;
        }
        loadSeasonEligible(item.id).then(function (ids) {
            ids.forEach(function (id) { if (on) { selected.set(id, { lock: item.seriesId, seasonId: item.id }); } else { selected.delete(id); } });
            selectionChanged();
        }, function (err) { failed(err, function () { toggle(item, on); }); render(); });
    }

    function clearSelection() {
        selected.clear();
        selectionChanged();
    }

    // Ids to send: a fully selected season goes as the season (so the server reports its skipped episodes too),
    // everything else as single episodes.
    function selectionIds() {
        var bySeason = new Map();
        selected.forEach(function (v, id) {
            if (!bySeason.has(v.seasonId)) { bySeason.set(v.seasonId, []); }
            bySeason.get(v.seasonId).push(id);
        });
        var ids = [];
        bySeason.forEach(function (episodeIds, seasonId) {
            var all = seasonEligible.get(seasonId);
            if (seasonId && all && all.length === episodeIds.length) { ids.push(seasonId); } else { ids = ids.concat(episodeIds); }
        });
        return ids;
    }

    var previewTimer = null;
    function selectionChanged() {
        var focusedId = document.activeElement && document.activeElement.closest && document.activeElement.closest('.row') ? document.activeElement.closest('.row').dataset.id : null;
        render();
        if (focusedId) {
            var r = $('list').querySelector('[data-id="' + CSS.escape(focusedId) + '"]');
            if (r) { focusRow(r, r.querySelector('input:not(:disabled)') ? 'input' : null); }
        }
        $('clear-selection').disabled = selected.size === 0;
        clearTimeout(previewTimer);
        preview = { pending: selected.size > 0, result: null, error: null };
        renderFooter();
        if (selected.size) { previewTimer = setTimeout(runPreview, 300); }
    }

    function runPreview() {
        var ids = selectionIds();
        var size = selected.size;
        api('POST', 'Jobs/Preview', { itemIds: ids }).then(function (p) {
            if (size !== selected.size) { return; }  // selection changed meanwhile
            preview = { pending: false, result: p, error: null };
            renderFooter();
        }, function (err) {
            preview = { pending: false, result: null, error: err };
            renderFooter();
            if (err.code === 'network') { failed(err, runPreview); }
        });
    }

    function renderFooter() {
        var text = $('footer-text');
        var start = $('start');
        $('footer').classList.toggle('has-error', !!preview.error);
        start.textContent = 'Start';
        if (!selected.size) {
            text.textContent = 'Select seasons or episodes of one series, or movies.';
            start.disabled = true;
            return;
        }
        if (preview.pending) {
            text.textContent = plural(selected.size, 'episode', 'episodes') + ' selected…';
            start.disabled = true;
            return;
        }
        if (preview.error) {
            text.textContent = preview.error.message;
            start.disabled = true;
            return;
        }
        var p = preview.result;
        var s = p.skipped;
        var skipped = s.alreadySynced + s.noEnglishAudio + s.noSidecar + s.noVideoFile;
        var parts = [p.kind === 'movies' ? plural(p.selected, 'movie', 'movies') + ' selected' : p.selected + ' selected in ' + p.seriesName, p.eligible + ' will be fixed'];
        if (skipped) { parts.push(skipped + ' skipped' + (s.alreadySynced ? ' (' + s.alreadySynced + ' already synced)' : '')); }
        if (p.attemptedBefore) { parts.push(p.attemptedBefore + ' tried before'); }
        text.textContent = parts.join(', ') + '.';
        start.disabled = p.eligible === 0;
        start.textContent = p.eligible ? 'Start (' + p.eligible + ')' : 'Start';
    }

    // Anything that adds to the queue (Start, Get, Check audio) ends here (owner, 2026-11-12): back to the top of the
    // explorer, Libraries folded, the Queue open and in view, with the item highlighted when one is given.
    function foldToQueue(message, itemId) {
        if (message) { $('footer-text').textContent = message; }
        if (itemId) { spotlight = itemId; }
        browse(null);
        setPanel('libraries', false);
        setPanel('queue', true);
        return pollNow().then(function () {
            var calm = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            if (!$('queue-panel').hidden) { $('queue-panel').scrollIntoView({ behavior: calm ? 'auto' : 'smooth', block: 'start' }); }
        });
    }

    function startJobs() {
        var ids = selectionIds();
        $('start').disabled = true;
        api('POST', 'Jobs', { itemIds: ids }).then(function (r) {
            selected.clear();
            selectionChanged();
            clearTimeout(previewTimer);
            foldToQueue(plural(r.queued, 'item', 'items') + ' added to the queue' + (r.skipped ? ', ' + r.skipped + ' skipped' : '') + '.');
        }, function (err) {
            preview = { pending: false, result: null, error: err };
            renderFooter();
            if (err.code === 'network') { failed(err, startJobs); }
        });
    }

    // ---------- queue panel (polls /Status: 1 s while running, 5 s idle, paused while hidden) ----------
    var pollTimer = null;
    var showAllQueue = false;
    var lastAttention = 0;

    function renderChips(t) {
        var box = $('queue-chips');
        box.replaceChildren();
        if (!t) { box.hidden = true; return; }
        [
            ['done', t.done, 'done', 'check'],
            ['check', t.check, 'to check', 'partial'],
            ['failed', t.failed, 'failed', 'partial'],
            ['', t.cancelled, 'cancelled', null],
            ['', t.skipped, 'skipped', null]
        ].forEach(function (c) {
            if (!c[1]) { return; }
            var chip = el('span', 'chip ' + c[0]);
            if (c[3]) { var ic = icon(c[3]); ic.setAttribute('class', 'icon'); chip.appendChild(ic); }
            chip.appendChild(document.createTextNode(c[1] + ' ' + c[2]));
            box.appendChild(chip);
        });
        box.hidden = !box.children.length;
    }

    function pollNow() {
        clearTimeout(pollTimer);
        if (document.hidden) { return Promise.resolve(); }
        return api('GET', 'Status').then(function (s) {
            var wasBusy = lastStatus && lastStatus.state !== 'idle';
            renderQueue(s);
            // The queue just finished: refresh the list so statuses come from the server again.
            if (wasBusy && s.state === 'idle' && view.mode === 'browse') { loadPage(view.parentId, false); }
        }, function (err) {
            if (err.status === 401) { showGate('signin'); }
        }).then(function () {
            pollTimer = setTimeout(pollNow, lastStatus && lastStatus.state !== 'idle' ? 1000 : 5000);
        });
    }

    function renderQueue(s) {
        var previous = queueByItem;
        var previousLang = queueByLang;
        lastStatus = s;
        queueByItem = new Map();
        queueByLang = new Map();
        var isPending = function (j) { return j && (j.status === 'queued' || j.status === 'running'); };
        s.items.forEach(function (i) {
            if (!i.itemId) { return; }
            if (!isPending(queueByItem.get(i.itemId))) { queueByItem.set(i.itemId, i); }
            var key = i.itemId + '|' + (i.language || 'en');
            if (!isPending(queueByLang.get(key))) { queueByLang.set(key, i); }
        });

        var t = s.totals;
        var finished = t.done + t.check + t.failed + t.cancelled + t.skipped;
        var total = finished + t.queued + t.running;
        var panel = $('queue-panel');
        panel.hidden = total === 0 && !s.stopReason && s.subsyncAvailable;

        // The running item's share (D40): while SubSync 1.3.0 listens, the server's `percent` from SubSync's own
        // percentage; otherwise elapsed time over the server's estimate. Held below 95% until it really finishes, and
        // never moved backwards. Without either the bar counts finished items only.
        var currentPart = 0, currentPct = null;
        var cur = s.current;
        if (cur && cur.startedUtc) {
            var ran = Math.max(0, (Date.now() - new Date(cur.startedUtc).getTime()) / 1000);
            var known = false;
            if (cur.percent != null) { currentPart = Math.min(0.95, cur.percent / 100); known = true; }
            else if (cur.expectedSeconds) { currentPart = Math.min(0.95, ran / cur.expectedSeconds); known = true; }
            if (cur.stage === 'matching') { currentPart = Math.max(currentPart, 0.95); known = true; }
            if (shownPart.jobId !== cur.jobId) { shownPart = { jobId: cur.jobId, part: 0 }; }
            currentPart = Math.max(currentPart, shownPart.part);
            shownPart.part = currentPart;
            if (known) { currentPct = Math.round(currentPart * 100); }
        }

        var bar = $('bar');
        var pct = total ? Math.round((finished + currentPart) * 100 / total) : 0;
        bar.firstElementChild.style.width = pct + '%';
        bar.setAttribute('aria-valuenow', String(pct));
        bar.classList.toggle('working', s.state !== 'idle');

        // One short line while working: "<name> · 73% · about 1 min left", with "1 of 2" next to the bar.
        // Finished counts show as coloured chips instead of a sentence (UI.md, queue panel).
        var summary = '';
        var count = $('queue-count');
        count.hidden = s.state === 'idle' || !total;
        count.textContent = Math.min(total, finished + 1) + ' of ' + total;
        if (s.state !== 'idle') {
            var etaSeconds = s.etaSeconds;
            if (etaSeconds == null && cur && cur.expectedSeconds && cur.startedUtc) {
                // Nothing finished yet to average: the running item's estimate, plus the same again per queued item.
                var left = Math.max(30, cur.expectedSeconds - (Date.now() - new Date(cur.startedUtc).getTime()) / 1000);
                etaSeconds = left + t.queued * cur.expectedSeconds;
            }
            var eta = duration(etaSeconds);
            var bits = [];
            if (s.state === 'stopping') { bits.push('Stopping…'); }
            if (cur) { bits.push(cur.name); }
            if (currentPct != null) { bits.push(currentPct + '%'); } else if (cur) { bits.push(STAGES[cur.stage] || 'starting'); }
            if (eta) { bits.push('about ' + eta + ' left'); }
            summary = bits.join(' · ');
        } else if (!total) {
            summary = 'Nothing queued.';
        }
        if (!s.subsyncAvailable) { summary = (summary ? summary + ' ' : '') + (s.maintenance || MESSAGES.subsync_unavailable); }
        $('queue-summary').textContent = summary;
        $('queue-summary').title = s.state !== 'idle' && cur && STAGES[cur.stage] ? 'SubSync is ' + STAGES[cur.stage] : '';
        renderChips(s.state === 'idle' ? t : null);

        var stop = $('queue-stop');
        stop.hidden = !s.stopReason;
        stop.textContent = s.stopReason ? 'The queue stopped: ' + s.stopReason : '';

        // Running and queued first, then finished non-done (they need attention), then done.
        var order = { running: 0, queued: 1, failed: 2, check: 2, cancelled: 3, skipped: 3, done: 4 };
        var sorted = s.items.slice().sort(function (a, b) { return order[a.status] - order[b.status]; });
        var limit = showAllQueue ? sorted.length : 8;
        var list = $('queue-list');
        list.textContent = '';
        sorted.slice(0, limit).forEach(function (i) {
            var li = el('li', 'row');
            if (i.itemId) { li.dataset.item = i.itemId; }
            var main = el('div', 'main');
            main.appendChild(el('span', 'name', i.name));
            // Check audio items (D49) say so; sync items say which subtitle language (D50).
            var isCheck = i.source === 'audio_check';
            var langBit = isCheck ? 'Check audio' : (LANG_NAMES[i.language || 'en'] || i.language) + ' subtitle' + (i.again ? ', synced again' : '');
            var sub = [i.seriesName, langBit, i.message].filter(Boolean).join(' · ');
            if (sub) { main.appendChild(el('span', 'sub', sub)); }
            li.appendChild(main);
            var badges = el('div', 'badges');
            if (i.mine) { badges.appendChild(badge('mine', 'Yours')); }
            var label = { running: isCheck ? 'Checking…' : 'Syncing…', queued: 'Queued', done: isCheck ? 'Checked' : 'Done', check: 'Check', failed: 'Failed', cancelled: 'Cancelled', skipped: 'Skipped' }[i.status];
            badges.appendChild(badge(i.status, label, i.message));
            li.appendChild(badges);
            list.appendChild(li);
        });
        // After Get: bring the queue into view and highlight the downloaded item, so its re-timing can be followed.
        if (spotlight) {
            var spot = list.querySelector('[data-item="' + CSS.escape(spotlight) + '"]');
            if (spot) {
                spotlight = null;
                var calm = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
                $('queue-panel').scrollIntoView({ behavior: calm ? 'auto' : 'smooth', block: 'center' });
                spot.classList.add('flash');
            }
        }
        var more = $('queue-more');
        more.hidden = sorted.length <= 8;
        more.textContent = showAllQueue ? 'Show fewer' : 'Show all ' + sorted.length;

        var minePending = s.items.some(function (i) { return i.mine && (i.status === 'queued' || i.status === 'running'); });
        var cancel = $('cancel');
        cancel.disabled = !(minePending || (me && me.isAdmin && t.queued + t.running > 0));
        cancel.textContent = me && me.isAdmin && !minePending ? 'Cancel all' : 'Cancel mine';
        $('clear-finished').disabled = finished === 0;

        // A new failed or check outcome is in the failed-syncs log now: refresh it.
        var attention = t.failed + t.check;
        if (attention > lastAttention) { loadFailed(false); }
        lastAttention = attention;

        // A finished audio check changes what the item can do (D49): reload the folder on screen to show it.
        var checkEnded = false;
        queueByItem.forEach(function (v, k) {
            var p = previous.get(k);
            if (v.source === 'audio_check' && p && (p.status === 'queued' || p.status === 'running') && v.status !== 'queued' && v.status !== 'running') { checkEnded = true; }
        });
        if (checkEnded && view.mode === 'browse' && view.parentId) { loadPage(view.parentId, false); }

        // Re-render explorer rows whose queue status changed.
        var changed = false;
        queueByItem.forEach(function (v, k) { var p = previous.get(k); if (!p || p.status !== v.status) { changed = true; } });
        previous.forEach(function (_, k) { if (!queueByItem.has(k)) { changed = true; } });
        // D50: the other language's job of the same item can change while the item's main entry doesn't
        queueByLang.forEach(function (v, k) { var p = previousLang.get(k); if (!p || p.status !== v.status) { changed = true; } });
        previousLang.forEach(function (_, k) { if (!queueByLang.has(k)) { changed = true; } });
        if (changed) { rerenderRows(); }
    }

    function rerenderRows() {
        var list = $('list');
        // A queue change can show or hide an item (D47); then the whole list is redrawn.
        var moved = view.items.some(function (item) {
            return shown(item) !== !!list.querySelector('[data-id="' + CSS.escape(item.id) + '"]');
        });
        if (moved) {
            var focusedId = document.activeElement && document.activeElement.closest && document.activeElement.closest('.row') ? document.activeElement.closest('.row').dataset.id : null;
            render();
            var back = focusedId && list.querySelector('[data-id="' + CSS.escape(focusedId) + '"]');
            if (back) { focusRow(back); }
            return;
        }
        view.items.forEach(function (item) {
            if (!isVideo(item)) { return; }
            var old = list.querySelector('[data-id="' + CSS.escape(item.id) + '"]');
            if (!old) { return; }
            var fresh = itemEl(item);
            fresh.tabIndex = old.tabIndex;
            var hadFocus = old.contains(document.activeElement);
            list.replaceChild(fresh, old);
            if (hadFocus) { focusRow(fresh); }
        });
    }

    // ---------- collapsible panels (UI.md): Libraries and Queue open, Failed syncs closed, until the viewer says otherwise ----------
    var PANEL_KEY = 'subtitle-fixer-panels';
    var panelDefaults = { libraries: true, queue: true, failed: false };
    function savedPanels() {
        try { return JSON.parse(localStorage.getItem(PANEL_KEY) || '{}') || {}; } catch (e) { return {}; }
    }
    function isOpen(name) { return $(name + '-toggle').getAttribute('aria-expanded') === 'true'; }
    // remember: only the viewer's own clicks are remembered, not the fold after Start.
    function setPanel(name, open, remember) {
        $(name + '-toggle').setAttribute('aria-expanded', String(open));
        $(name + '-body').hidden = !open;
        var panel = $(name + '-panel');
        panel.classList.toggle('collapsed', !open);
        if (name === 'libraries') {
            $('libraries-collapsed-note').hidden = open;
        }
        if (name === 'failed' && open) { loadFailed(false); }
        if (remember) {
            try {
                var saved = savedPanels();
                saved[name] = open;
                localStorage.setItem(PANEL_KEY, JSON.stringify(saved));
            } catch (e) { /* private window or blocked storage: just don't remember */ }
        }
    }
    function initPanels() {
        var saved = savedPanels();
        Object.keys(panelDefaults).forEach(function (name) {
            setPanel(name, typeof saved[name] === 'boolean' ? saved[name] : panelDefaults[name]);
            $(name + '-toggle').addEventListener('click', function () { setPanel(name, !isOpen(name), true); });
        });
    }

    // ---------- failed-syncs log (D44): failed and check outcomes, kept after the queue list drops them ----------
    var FAILED_PAGE = 20;
    var failedLog = { items: [], total: 0, loading: false };

    function loadFailed(append) {
        if (failedLog.loading) { return; }
        failedLog.loading = true;
        var start = append ? failedLog.items.length : 0;
        api('GET', 'FailedSyncs?startIndex=' + start + '&limit=' + FAILED_PAGE).then(function (page) {
            failedLog.items = append ? failedLog.items.concat(page.items) : page.items;
            failedLog.total = page.totalCount;
            renderFailed();
        }, function (err) {
            if (err.code === 'state_unavailable') { $('failed-panel').hidden = true; return; }
            failedHint(err.message, true);
        }).then(function () { failedLog.loading = false; });
    }

    function failedHint(text, isError) {
        var h = $('failed-hint');
        h.textContent = text || '';
        h.hidden = !text;
        h.classList.toggle('error', !!isError);
    }

    function when(utc) {
        var d = new Date(utc);
        var today = new Date();
        var time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        return d.toDateString() === today.toDateString() ? 'Today ' + time : d.toLocaleDateString([], { month: 'short', day: 'numeric' }) + ' ' + time;
    }

    function renderFailed() {
        $('failed-panel').hidden = failedLog.total === 0;
        $('failed-count').textContent = failedLog.total ? String(failedLog.total) : '';
        $('failed-count').title = plural(failedLog.total, 'entry', 'entries');
        var list = $('failed-list');
        list.replaceChildren();
        failedLog.items.forEach(function (f) {
            var li = el('li', 'row');
            var main = el('div', 'main');
            main.appendChild(el('span', 'name', f.name));
            main.appendChild(el('span', 'sub', [f.seriesName, f.message, when(f.finishedUtc)].filter(Boolean).join(' · ')));
            li.appendChild(main);
            var badges = el('div', 'badges');
            if (f.mine && me && me.isAdmin) { badges.appendChild(badge('mine', 'Yours')); }
            badges.appendChild(badge(f.status, f.status === 'check' ? 'Check' : 'Failed', f.message));
            li.appendChild(badges);
            if (f.itemId) {
                var retry = el('button', 'retry');
                retry.type = 'button';
                retry.setAttribute('aria-label', 'Try ' + f.name + ' again');
                var ri = icon('retry');
                ri.setAttribute('class', 'icon');
                retry.appendChild(ri);
                retry.appendChild(document.createTextNode('Retry'));
                retry.addEventListener('click', function () { retryFailed(f, retry); });
                li.appendChild(retry);
            }
            list.appendChild(li);
        });
        $('failed-more').hidden = failedLog.items.length >= failedLog.total;
        $('failed-more').textContent = 'Load more (' + (failedLog.total - failedLog.items.length) + ' left)';
        $('failed-clear').hidden = !(me && me.isAdmin) || failedLog.total === 0;
    }

    function retryFailed(f, button) {
        button.disabled = true;
        failedHint('');
        api('POST', 'FailedSyncs/' + encodeURIComponent(f.id) + '/Retry').then(function () {
            failedHint(f.name + ' is back in the queue.');
            loadFailed(false);
            setPanel('queue', true);
            pollNow();
        }, function (err) {
            button.disabled = false;
            failedHint(err.message, true);
            if (err.code === 'network') { failed(err); }
        });
    }

    // ---------- keyboard (roving focus over rows) ----------
    function rows() { return Array.prototype.slice.call($('list').querySelectorAll('.row')); }

    function rovingInit() {
        var all = rows();
        if (!all.length) { return; }
        var current = all.filter(function (r) { return r.tabIndex === 0; })[0];
        if (!current) { all[0].tabIndex = 0; }
    }

    function focusRow(r, part) {
        rows().forEach(function (x) { x.tabIndex = -1; });
        r.tabIndex = 0;
        var target = part === 'input' ? r.querySelector('input') : r;
        (target || r).focus({ preventScroll: false });
    }

    function onListKey(e) {
        var r = e.target.closest('.row');
        if (!r) { return; }
        // a subtitle chip handles its own Enter and Space (it's a button, D52)
        if (e.target.closest('.sub-chip') && (e.key === 'Enter' || e.key === ' ')) { return; }
        var all = rows();
        var i = all.indexOf(r);
        var item = view.items.filter(function (x) { return x.id === r.dataset.id; })[0];
        // Cards: arrows move across the grid; Backspace still goes up a level (D47).
        if ($('list').classList.contains('cards') && /^Arrow/.test(e.key)) {
            var cols = all.filter(function (x) { return x.offsetTop === all[0].offsetTop; }).length || 1;
            var step = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -cols, ArrowDown: cols }[e.key];
            var to = i + step;
            if (to >= 0 && to < all.length) { focusRow(all[to]); }
            else if (to < 0 && e.key === 'ArrowUp') { $('q').focus(); }
            else if (to >= all.length && e.key === 'ArrowDown') { if (!$('more').hidden) { $('more').focus(); } else { focusRow(all[all.length - 1]); } }
            e.preventDefault();
            return;
        }
        switch (e.key) {
            case 'ArrowDown': if (i < all.length - 1) { focusRow(all[i + 1]); } else if (!$('more').hidden) { $('more').focus(); } e.preventDefault(); break;
            case 'ArrowUp': if (i > 0) { focusRow(all[i - 1]); } else { $('q').focus(); } e.preventDefault(); break;
            case 'Home': focusRow(all[0]); e.preventDefault(); break;
            case 'End': focusRow(all[all.length - 1]); e.preventDefault(); break;
            case 'Enter':
                if (item && !isVideo(item)) { openItem(item); e.preventDefault(); }
                else if (item && view.mode === 'search' && item.type === 'Episode') { openItem(item); e.preventDefault(); }
                else if (item && item.subtitles && r.querySelector('.sub-chip')) { r.querySelector('.sub-chip').focus(); e.preventDefault(); }  // into the chips (D52)
                else if (item && canFind(item)) { openFind(item); e.preventDefault(); }
                break;
            case ' ':
                if (e.target.tagName === 'INPUT') { return; }  // the checkbox handles its own space
                var box = r.querySelector('input[type="checkbox"]');
                if (box && !box.disabled) { box.checked = !box.checked; toggle(item, box.checked); }
                e.preventDefault();
                break;
            case 'ArrowLeft': case 'Backspace': goUp(); e.preventDefault(); break;
            case 'ArrowRight': if (item && !isVideo(item)) { openItem(item); e.preventDefault(); } break;
        }
    }

    // ---------- admin diagnostics ----------
    function initAdmin() {
        $('diag').hidden = false;
        var show = function (d) {
            var dl = $('diag-list');
            dl.textContent = '';
            [
                ['Plugin version', d.pluginVersion],
                ['State database', d.stateLocation],
                ['Database ready', d.stateReady ? 'yes' : 'no' + (d.stateError ? ' (' + d.stateError + ')' : '')],
                ['Menu entry registered', d.menuRegistered ? 'yes' : 'no' + (d.menuError ? ' (' + d.menuError + ')' : '')],
                ['Test row in state.db', d.lastDiagnosticWrite || 'none yet']
            ].forEach(function (pair) { dl.appendChild(el('dt', null, pair[0])); dl.appendChild(el('dd', null, pair[1])); });
        };
        var refresh = function () { return api('GET', 'Admin/Diagnostics').then(show, function (err) { failed(err, refresh); }); };
        $('diag').addEventListener('toggle', function () { if ($('diag').open) { refresh(); } });
        $('diag-refresh').addEventListener('click', refresh);
        $('diag-write').addEventListener('click', function () { api('POST', 'Admin/Diagnostics/State').then(show, function (err) { failed(err); }); });
        $('subsync').addEventListener('click', function () {
            var out = $('subsync-out');
            out.hidden = false;
            out.textContent = 'Running subsync-cli check and version (can take a minute)…';
            api('POST', 'Admin/TestSubSync').then(function (r) {
                out.textContent = r.message + '\nVersion: ' + (r.version || 'unknown') + '\nMode: ' + r.mode +
                    '\nExit code: ' + (r.checkExitCode == null ? 'none' : r.checkExitCode) + '\n\n' + (r.checkOutput || '');
            }, function (err) { out.textContent = err.message; });
        });
    }

    // ---------- wiring ----------
    function wire() {
        var searchTimer = null;
        $('q').addEventListener('input', function () {
            clearTimeout(searchTimer);
            var q = $('q').value.trim();
            searchTimer = setTimeout(function () {
                if (q.length >= 2) { search(q); } else if (view.mode === 'search') { browse(view.parentId, { keepSearch: true }); }
            }, 250);
        });
        $('q').addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { $('q').value = ''; if (view.mode === 'search') { browse(view.parentId); } }
            if (e.key === 'ArrowDown') { var first = rows()[0]; if (first) { focusRow(first); e.preventDefault(); } }
        });
        $('list').addEventListener('keydown', onListKey);
        $('list').addEventListener('focusin', function (e) {
            var r = e.target.closest('.row');
            if (r && r.tabIndex !== 0) { rows().forEach(function (x) { x.tabIndex = -1; }); r.tabIndex = 0; }
        });
        $('more').addEventListener('click', function () { loadPage(view.parentId, true); });
        $('hide-ineligible').addEventListener('change', function () { setHide('hideIneligible', $('hide-ineligible').checked); });
        $('hide-synced').addEventListener('change', function () { setHide('hideSynced', $('hide-synced').checked); });
        Array.prototype.forEach.call($('layout').querySelectorAll('button'), function (b) {
            b.addEventListener('click', function () { setLayout(b.dataset.layout); });
        });
        if ('IntersectionObserver' in window) {
            new IntersectionObserver(function (entries) {
                if (entries[0].isIntersecting && !$('more').hidden && !view.loading) { loadPage(view.parentId, true); }
            }, { rootMargin: '200px' }).observe($('more'));
        }
        $('clear-selection').addEventListener('click', clearSelection);
        $('start').addEventListener('click', startJobs);
        $('cancel').addEventListener('click', function () { api('POST', 'Jobs/Cancel').then(renderQueue, function (err) { failed(err); }); });
        $('clear-finished').addEventListener('click', function () { api('POST', 'Jobs/Clear').then(renderQueue, function (err) { failed(err); }); });
        $('queue-more').addEventListener('click', function () { showAllQueue = !showAllQueue; if (lastStatus) { renderQueue(lastStatus); } });
        $('failed-more').addEventListener('click', function () { loadFailed(true); });
        $('failed-clear').addEventListener('click', function () {
            if (!window.confirm('Clear the whole failed-syncs log for everyone?')) { return; }
            api('POST', 'FailedSyncs/Clear').then(function () { failedHint(''); loadFailed(false); }, function (err) { failedHint(err.message, true); });
        });
        $('banner-retry').addEventListener('click', function () { var a = retryAction; clearBanner(); if (a) { a(); } });
        $('find-close').addEventListener('click', closeFind);
        var langTimer = null;
        $('find-lang').addEventListener('input', function () {
            clearTimeout(langTimer);
            langTimer = setTimeout(function () { findState.lang = null; prefs.findLang = $('find-lang').value.slice(0, 40); savePrefs(); applyFindFilters(); }, 150);
        });
        $('find-hide-hi').addEventListener('change', function () { prefs.findHideHi = $('find-hide-hi').checked; savePrefs(); applyFindFilters(); });
        $('find').addEventListener('close', function () {
            findSeq++;
            var id = $('find').returnTo;
            var r = id ? $('list').querySelector('[data-id="' + CSS.escape(id) + '"]') : null;
            var back = r && (r.querySelector('.sub-chip.missing, .sub-chip') || r);
            if (back) { back.focus(); }
        });
        $('find').addEventListener('click', function (e) { if (e.target === $('find')) { closeFind(); } });  // backdrop click
        document.addEventListener('visibilitychange', function () { if (!document.hidden) { pollNow(); } });
        document.addEventListener('keydown', function (e) {
            if (e.key === '/' && document.activeElement !== $('q') && !/INPUT|TEXTAREA/.test(document.activeElement.tagName)) {
                if (!isOpen('libraries')) { setPanel('libraries', true, true); }
                $('q').focus();
                e.preventDefault();
            }
        });
    }

    function start() {
        if (!MOCK && !token()) { showGate('signin'); return; }
        api('GET', 'Me').then(function (m) {
            me = m;
            if (!m.allowed) {
                if (m.message) { $('gate-denied-text').textContent = m.message; }
                showGate('denied');
                return;
            }
            showGate('app');
            initPanels();
            wire();
            if (m.isAdmin) { initAdmin(); }
            // Optional feature: any failure just leaves Find subtitles hidden.
            api('GET', 'Bazarr/Info').then(function (i) {
                bazarrEnabled = !!(i && i.enabled);
                if (bazarrEnabled) { rerenderRows(); }
            }, function () { bazarrEnabled = false; });
            // The page's first entry: in the pop-up a guard that closes it on Back, on the full page the top level.
            try { history.replaceState(EMBEDDED ? { subtitleFixer: true, guard: true } : { subtitleFixer: true, parentId: null }, ''); } catch (e) { /* ignore */ }
            window.addEventListener('popstate', onHistory);
            browse(null);
            pollNow();
            loadFailed(false);
            renderFooter();
        }, function (err) {
            if (err.status === 401) { showGate('signin'); return; }
            $('gate-loading').textContent = err.message;
            if (err.code === 'network') { failed(err, start); }
        });
    }

    // Opened as the pop-up from the avatar menu (menu.js): no "Back to Jellyfin", Esc closes the pop-up, and links
    // that leave the page open in the whole window.
    var EMBEDDED = params.get('embedded') === '1' && window.parent !== window;
    if (EMBEDDED) {
        document.documentElement.classList.add('embedded');
        Array.prototype.forEach.call(document.querySelectorAll('a[href="../web/"]'), function (a) { a.target = '_top'; });
        document.addEventListener('keydown', function (e) {
            if (e.key !== 'Escape' || e.defaultPrevented) { return; }
            if (document.querySelector('dialog[open]')) { return; }           // Esc closes the Find dialog first
            if (document.activeElement === $('q') && $('q').value) { return; } // Esc clears the search first
            window.parent.postMessage({ type: 'subtitle-fixer-close' }, window.location.origin);
        });
    }

    if (MOCK) {
        $('mock-badge').hidden = false;
        var s = document.createElement('script');
        s.src = 'Page/mock-data.js';
        s.onload = start;
        s.onerror = function () { $('gate-loading').textContent = 'Could not load mock-data.js.'; };
        document.head.appendChild(s);
    } else {
        start();
    }
})();
