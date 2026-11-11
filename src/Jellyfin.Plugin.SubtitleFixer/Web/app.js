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
    var queueByItem = new Map();     // itemId -> job status (visible items only)
    var lastStatus = null;
    var preview = { pending: false, result: null, error: null };
    var retryAction = null;

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
    function lockedSeries() { return selected.size ? selected.values().next().value.seriesId : null; }
    function otherSeries(item) { var l = lockedSeries(); return !!(l && item.seriesId && item.seriesId !== l); }

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

    function browse(parentId, opts) {
        opts = opts || {};
        view.mode = 'browse';
        view.query = '';
        if (!opts.keepSearch) { $('q').value = ''; }
        return loadPage(parentId, false, opts.focusId);
    }

    function loadPage(parentId, append, focusId) {
        if (view.loading) { return Promise.resolve(); }
        view.loading = true;
        showHint('');
        var start = append ? view.items.length : 0;
        var path = 'Browse?startIndex=' + start + '&limit=' + PAGE + (parentId ? '&parentId=' + encodeURIComponent(parentId) : '');
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

    function render() {
        renderCrumbs();
        var list = $('list');
        list.textContent = '';
        view.items.forEach(function (item) { list.appendChild(row(item)); });

        var empty = $('list-empty');
        if (!view.items.length) {
            empty.textContent = view.mode === 'search' ? 'No series or episodes match “' + view.query + '”.' :
                view.parentId ? 'Nothing here.' : 'No TV libraries you can access.';
        }
        empty.hidden = view.items.length > 0;

        if (view.mode === 'search') {
            showHint(view.searchTotal > view.items.length ? 'Showing ' + view.items.length + ' of ' + view.searchTotal + ' matches. Type more to narrow it down.' : '');
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
        var dim = otherSeries(item);
        if (dim) { li.classList.add('dimmed'); li.title = 'Clear your selection to choose another series'; }

        // Checkbox column (seasons and episodes only).
        if (item.type === 'Season' || item.type === 'Episode') {
            var box = el('input');
            box.type = 'checkbox';
            box.setAttribute('aria-label', 'Select ' + (item.type === 'Episode' ? code(item) + ' ' : '') + item.name);
            if (item.type === 'Episode') {
                var inQueue = queueByItem.get(item.id);
                var pending = inQueue && (inQueue.status === 'queued' || inQueue.status === 'running');
                box.checked = selected.has(item.id);
                box.disabled = dim || !item.eligible || !!pending;
                if (!item.eligible) { box.title = 'Not eligible: ' + (REASONS[item.reason] || item.reason); }
                if (pending) { box.title = 'Already in the queue'; }
            } else {
                applySeasonBox(box, item);
                box.disabled = dim || item.eligibleCount === 0;
                if (item.eligibleCount === 0) { box.title = 'No episodes in this season can be fixed'; }
            }
            if (dim) { box.title = 'Clear your selection to choose another series'; }
            box.addEventListener('change', function () { toggle(item, box.checked); });
            box.addEventListener('click', function (e) { e.stopPropagation(); });
            li.appendChild(box);
        } else {
            li.appendChild(el('span', 'check-slot'));
        }

        li.appendChild(icon(item.type === 'Library' ? 'library' : item.type === 'Series' ? 'series' : item.type === 'Season' ? 'season' : 'episode'));

        var main = el('div', 'main');
        var name = el('span', 'name');
        var label = item.type === 'Episode' ? (code(item) ? code(item) + ' · ' : '') + item.name : item.name;
        if (item.type === 'Episode' && view.mode === 'search') {
            // Search results open like browsing: an episode opens its season with the episode highlighted.
            var go = el('button', null, label);
            go.type = 'button';
            go.tabIndex = -1;
            go.title = 'Show in its season';
            go.addEventListener('click', function (e) { e.stopPropagation(); openItem(item); });
            name.appendChild(go);
        } else if (item.type === 'Episode') {
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

        if (item.type === 'Episode' && !li.classList.contains('openable')) {
            li.addEventListener('click', function (e) {
                if (e.target.closest('button,input,a')) { return; }
                var b = li.querySelector('input[type="checkbox"]');
                if (b && !b.disabled) { b.checked = !b.checked; toggle(item, b.checked); }
            });
        }
        return li;
    }

    function secondary(item) {
        switch (item.type) {
            case 'Library': return 'TV library';
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

    function badge(cls, text, title) {
        var b = el('span', 'badge ' + cls, text);
        if (title) { b.title = title; }
        return b;
    }

    function badgesFor(item) {
        var out = [];
        if (item.type !== 'Episode') { return out; }
        var job = queueByItem.get(item.id);
        if (job) {
            var label = { queued: 'Queued', running: 'Syncing…', done: 'Fixed', check: 'Check', failed: 'Failed', cancelled: 'Cancelled', skipped: 'Skipped' }[job.status] || job.status;
            out.push(badge(job.status, label, job.message));
            if (job.status === 'queued' || job.status === 'running') { return out; }
        }
        if (job && job.status === 'done') { return out; }
        if (item.status === 'done') { out.push(badge('synced', 'Synced')); }
        else if (!item.eligible) { out.push(badge('', 'Not eligible: ' + (REASONS[item.reason] || item.reason), REASONS[item.reason])); }
        else if (item.status === 'attempted') { out.push(badge('attempted', 'Previously attempted', item.message)); }
        return out;
    }

    function openItem(item) {
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
        if (item.type === 'Episode') {
            if (on) { selected.set(item.id, { seriesId: item.seriesId, seasonId: item.seasonId }); } else { selected.delete(item.id); }
            selectionChanged();
            return;
        }
        loadSeasonEligible(item.id).then(function (ids) {
            ids.forEach(function (id) { if (on) { selected.set(id, { seriesId: item.seriesId, seasonId: item.id }); } else { selected.delete(id); } });
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
            text.textContent = 'Select seasons or episodes of one series.';
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
        var parts = [p.selected + ' selected in ' + p.seriesName, p.eligible + ' will be fixed'];
        if (skipped) { parts.push(skipped + ' skipped' + (s.alreadySynced ? ' (' + s.alreadySynced + ' already synced)' : '')); }
        if (p.attemptedBefore) { parts.push(p.attemptedBefore + ' tried before'); }
        text.textContent = parts.join(', ') + '.';
        start.disabled = p.eligible === 0;
        start.textContent = p.eligible ? 'Start (' + p.eligible + ')' : 'Start';
    }

    function startJobs() {
        var ids = selectionIds();
        $('start').disabled = true;
        api('POST', 'Jobs', { itemIds: ids }).then(function (r) {
            selected.clear();
            selectionChanged();
            clearTimeout(previewTimer);
            $('footer-text').textContent = plural(r.queued, 'episode', 'episodes') + ' added to the queue' +
                (r.skipped ? ', ' + r.skipped + ' skipped' : '') + '.';
            pollNow();
            $('queue-panel').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }, function (err) {
            preview = { pending: false, result: null, error: err };
            renderFooter();
            if (err.code === 'network') { failed(err, startJobs); }
        });
    }

    // ---------- queue panel (polls /Status: 1 s while running, 5 s idle, paused while hidden) ----------
    var pollTimer = null;
    var showAllQueue = false;

    function pollNow() {
        clearTimeout(pollTimer);
        if (document.hidden) { return; }
        api('GET', 'Status').then(function (s) {
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
        lastStatus = s;
        queueByItem = new Map();
        s.items.forEach(function (i) { if (i.itemId) { queueByItem.set(i.itemId, i); } });

        var t = s.totals;
        var finished = t.done + t.check + t.failed + t.cancelled + t.skipped;
        var total = finished + t.queued + t.running;
        var panel = $('queue-panel');
        panel.hidden = total === 0 && !s.stopReason && s.subsyncAvailable;

        var bar = $('bar');
        var pct = total ? Math.round(finished * 100 / total) : 0;
        bar.firstElementChild.style.width = pct + '%';
        bar.setAttribute('aria-valuenow', String(pct));
        bar.classList.toggle('working', s.state !== 'idle');

        var summary;
        if (s.state === 'idle') {
            summary = total ? 'Finished: ' + t.done + ' done, ' + t.check + ' check, ' + t.failed + ' failed' +
                (t.cancelled ? ', ' + t.cancelled + ' cancelled' : '') + (t.skipped ? ', ' + t.skipped + ' skipped' : '') + '.' : 'Nothing queued.';
        } else {
            var started = s.items.map(function (i) { return i.startedUtc; }).filter(Boolean).sort()[0];
            var elapsed = started ? duration((Date.now() - new Date(started).getTime()) / 1000) : null;
            var eta = duration(s.etaSeconds);
            summary = (s.state === 'stopping' ? 'Stopping… ' : '') + finished + ' of ' + total + ' finished.' +
                (s.current ? ' Now: ' + s.current.name + (s.current.mine ? ' (yours)' : '') + '.' : '') +
                (elapsed ? ' Elapsed ' + elapsed + '.' : '') + (eta ? ' About ' + eta + ' left.' : '') +
                (s.aheadOfYours ? ' ' + plural(s.aheadOfYours, 'episode', 'episodes') + ' ahead of yours.' : '');
        }
        if (!s.subsyncAvailable) { summary += ' ' + MESSAGES.subsync_unavailable; }
        $('queue-summary').textContent = summary;

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
            var main = el('div', 'main');
            main.appendChild(el('span', 'name', i.name));
            var sub = [i.seriesName, i.message, i.note].filter(Boolean).join(' · ');
            if (sub) { main.appendChild(el('span', 'sub', sub)); }
            li.appendChild(main);
            var badges = el('div', 'badges');
            if (i.mine) { badges.appendChild(badge('mine', 'Yours')); }
            var label = { running: 'Syncing…', queued: 'Queued', done: 'Done', check: 'Check', failed: 'Failed', cancelled: 'Cancelled', skipped: 'Skipped' }[i.status];
            badges.appendChild(badge(i.status, label, i.message));
            li.appendChild(badges);
            list.appendChild(li);
        });
        var more = $('queue-more');
        more.hidden = sorted.length <= 8;
        more.textContent = showAllQueue ? 'Show fewer' : 'Show all ' + sorted.length;

        var minePending = s.items.some(function (i) { return i.mine && (i.status === 'queued' || i.status === 'running'); });
        var cancel = $('cancel');
        cancel.disabled = !(minePending || (me && me.isAdmin && t.queued + t.running > 0));
        cancel.textContent = me && me.isAdmin && !minePending ? 'Cancel all' : 'Cancel mine';
        $('clear-finished').disabled = finished === 0;

        // Re-render explorer rows whose queue status changed.
        var changed = false;
        queueByItem.forEach(function (v, k) { var p = previous.get(k); if (!p || p.status !== v.status) { changed = true; } });
        previous.forEach(function (_, k) { if (!queueByItem.has(k)) { changed = true; } });
        if (changed) { rerenderRows(); }
    }

    function rerenderRows() {
        var list = $('list');
        view.items.forEach(function (item) {
            if (item.type !== 'Episode') { return; }
            var old = list.querySelector('[data-id="' + CSS.escape(item.id) + '"]');
            if (!old) { return; }
            var fresh = row(item);
            fresh.tabIndex = old.tabIndex;
            var hadFocus = old.contains(document.activeElement);
            list.replaceChild(fresh, old);
            if (hadFocus) { focusRow(fresh); }
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
        var all = rows();
        var i = all.indexOf(r);
        var item = view.items.filter(function (x) { return x.id === r.dataset.id; })[0];
        switch (e.key) {
            case 'ArrowDown': if (i < all.length - 1) { focusRow(all[i + 1]); } else if (!$('more').hidden) { $('more').focus(); } e.preventDefault(); break;
            case 'ArrowUp': if (i > 0) { focusRow(all[i - 1]); } else { $('q').focus(); } e.preventDefault(); break;
            case 'Home': focusRow(all[0]); e.preventDefault(); break;
            case 'End': focusRow(all[all.length - 1]); e.preventDefault(); break;
            case 'Enter':
                if (item && item.type !== 'Episode') { openItem(item); e.preventDefault(); }
                else if (item && view.mode === 'search') { openItem(item); e.preventDefault(); }
                break;
            case ' ':
                if (e.target.tagName === 'INPUT') { return; }  // the checkbox handles its own space
                var box = r.querySelector('input[type="checkbox"]');
                if (box && !box.disabled) { box.checked = !box.checked; toggle(item, box.checked); }
                e.preventDefault();
                break;
            case 'ArrowLeft': case 'Backspace': goUp(); e.preventDefault(); break;
            case 'ArrowRight': if (item && item.type !== 'Episode') { openItem(item); e.preventDefault(); } break;
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
        $('banner-retry').addEventListener('click', function () { var a = retryAction; clearBanner(); if (a) { a(); } });
        document.addEventListener('visibilitychange', function () { if (!document.hidden) { pollNow(); } });
        document.addEventListener('keydown', function (e) {
            if (e.key === '/' && document.activeElement !== $('q') && !/INPUT|TEXTAREA/.test(document.activeElement.tagName)) { $('q').focus(); e.preventDefault(); }
        });
    }

    function start() {
        if (!MOCK && !token()) { showGate('signin'); return; }
        api('GET', 'Me').then(function (m) {
            me = m;
            $('who').textContent = 'Signed in as ' + m.name + (m.isAdmin ? ' (administrator)' : '');
            $('who').hidden = false;
            if (!m.allowed) {
                if (m.message) { $('gate-denied-text').textContent = m.message; }
                showGate('denied');
                return;
            }
            showGate('app');
            wire();
            if (m.isAdmin) { initAdmin(); }
            browse(null);
            pollNow();
            renderFooter();
        }, function (err) {
            if (err.status === 401) { showGate('signin'); return; }
            $('gate-loading').textContent = err.message;
            if (err.code === 'network') { failed(err, start); }
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
