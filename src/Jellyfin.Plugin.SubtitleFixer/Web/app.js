/* Subtitle Fixer page, M1 test build: browse, search, selection preview and admin diagnostics.
   Vanilla JS, no build step. All data goes through our API with the user's Jellyfin token. */
(function () {
    'use strict';

    var apiRoot = new URL('./', window.location.href).href; // .../SubtitleFixer/
    var $ = function (id) { return document.getElementById(id); };

    // ---------- auth ----------
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

    // Only Token is sent: Jellyfin overwrites the device's name and version when Device/Version differ.
    function api(method, path, body) {
        var started = performance.now();
        var headers = { 'Authorization': 'MediaBrowser Token="' + (token() || '') + '"' };
        if (body !== undefined) { headers['Content-Type'] = 'application/json'; }
        return fetch(apiRoot + path, { method: method, headers: headers, body: body === undefined ? undefined : JSON.stringify(body) })
            .then(function (r) {
                var ms = Math.round(performance.now() - started);
                return r.text().then(function (text) {
                    var data = null;
                    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
                    logTiming(method + ' ' + path, r.status, ms);
                    if (!r.ok) {
                        var err = new Error((data && data.message) || ('Request failed (' + r.status + ')'));
                        err.status = r.status;
                        err.code = data && data.error;
                        throw err;
                    }
                    return data;
                });
            });
    }

    function logTiming(what, status, ms) {
        var li = document.createElement('li');
        li.textContent = ms + ' ms  ' + status + '  ' + what;
        var list = $('timings');
        list.insertBefore(li, list.firstChild);
        while (list.children.length > 30) { list.removeChild(list.lastChild); }
    }

    function showError(err) {
        var box = $('error');
        box.textContent = err ? err.message : '';
        box.hidden = !err;
    }

    // ---------- browse ----------
    var PAGE = 50;
    var current = { parentId: null, loaded: 0, total: 0 };
    var selected = new Map();   // episodeId -> seriesId
    var lockedSeries = null;

    function badge(item) {
        var span = document.createElement('span');
        span.className = 'badge';
        if (item.type === 'Season') {
            span.textContent = item.episodeCount + ' episodes, ' + item.syncedCount + ' synced, ' + item.eligibleCount + ' can be fixed';
            return span;
        }
        if (item.type !== 'Episode') { return null; }
        if (item.status === 'done') { span.classList.add('done'); span.textContent = 'Synced'; }
        else if (!item.eligible) { span.classList.add('no'); span.textContent = 'Not eligible: ' + (item.reason || '').replace(/_/g, ' '); }
        else if (item.status === 'attempted') { span.classList.add('attempted'); span.textContent = 'Previously attempted'; if (item.message) { span.title = item.message; } }
        else { span.textContent = 'Can be fixed'; }
        return span;
    }

    function row(item, inSearch) {
        var li = document.createElement('li');
        var selectable = item.type === 'Episode' && item.eligible;
        var otherSeries = lockedSeries && item.seriesId && item.seriesId !== lockedSeries;

        if (item.type === 'Episode' || item.type === 'Season') {
            var box = document.createElement('input');
            box.type = 'checkbox';
            box.setAttribute('aria-label', 'Select ' + item.name);
            if (item.type === 'Episode') {
                box.checked = selected.has(item.id);
                box.disabled = !selectable || !!otherSeries;
            } else {
                box.disabled = !!otherSeries || item.eligibleCount === 0;
            }
            if (otherSeries) { box.title = 'Clear your selection to choose another series'; li.style.opacity = '0.5'; }
            box.addEventListener('change', function () { toggle(item, box.checked); });
            li.appendChild(box);
        }

        var name = document.createElement('span');
        name.className = 'name';
        if (item.type !== 'Episode') {
            var open = document.createElement('button');
            open.type = 'button';
            open.textContent = item.name;
            open.addEventListener('click', function () { browse(item.id); });
            name.appendChild(open);
        } else {
            name.textContent = (item.parentIndexNumber != null ? 'S' + pad(item.parentIndexNumber) : '') +
                (item.indexNumber != null ? 'E' + pad(item.indexNumber) + ' ' : '') + item.name;
        }
        if (inSearch && item.seriesName && item.type === 'Episode') {
            var sub = document.createElement('span');
            sub.className = 'sub';
            sub.textContent = item.seriesName;
            name.appendChild(sub);
        }
        li.appendChild(name);

        var b = badge(item);
        if (b) { li.appendChild(b); }
        return li;
    }

    function pad(n) { return n < 10 ? '0' + n : String(n); }

    function crumbs(parent) {
        var nav = $('crumbs');
        nav.textContent = '';
        var trail = [{ id: null, name: 'Libraries' }].concat(parent ? parent.breadcrumb : []);
        trail.forEach(function (c, i) {
            var b = document.createElement('button');
            b.type = 'button';
            b.textContent = c.name;
            b.disabled = i === trail.length - 1;
            b.addEventListener('click', function () { browse(c.id); });
            nav.appendChild(b);
        });
    }

    function browse(parentId, append) {
        showError(null);
        var start = append ? current.loaded : 0;
        var q = 'Browse?startIndex=' + start + '&limit=' + PAGE + (parentId ? '&parentId=' + encodeURIComponent(parentId) : '');
        return api('GET', q).then(function (page) {
            if (!append) {
                $('list').textContent = '';
                current = { parentId: parentId, loaded: 0, total: 0, items: [] };
                crumbs(page.parent);
            }
            page.items.forEach(function (item) { current.items.push(item); $('list').appendChild(row(item, false)); });
            current.loaded += page.items.length;
            current.total = page.totalCount;
            $('more').hidden = current.loaded >= current.total;
        }).catch(showError);
    }

    // ---------- selection (mirrors the server rules; the server still enforces them) ----------
    function toggle(item, on) {
        if (item.type === 'Episode') {
            if (on) { selected.set(item.id, item.seriesId); } else { selected.delete(item.id); }
            selectionChanged();
            return;
        }
        // Season: load its episodes and select the eligible ones.
        api('GET', 'Browse?parentId=' + encodeURIComponent(item.id) + '&limit=200').then(function (page) {
            page.items.forEach(function (ep) {
                if (!ep.eligible) { return; }
                if (on) { selected.set(ep.id, ep.seriesId); } else { selected.delete(ep.id); }
            });
            selectionChanged();
        }).catch(showError);
    }

    var previewTimer = null;
    function selectionChanged() {
        lockedSeries = selected.size ? selected.values().next().value : null;
        $('clear').disabled = selected.size === 0;
        $('preview').disabled = selected.size === 0;
        $('selection').textContent = selected.size ? selected.size + ' episodes selected' : '';
        // Re-render current rows so the series lock applies.
        var list = $('list');
        list.textContent = '';
        (current.items || []).forEach(function (item) { list.appendChild(row(item, false)); });
        clearTimeout(previewTimer);
        if (selected.size) { previewTimer = setTimeout(preview, 300); }
    }

    function preview() {
        if (!selected.size) { return; }
        api('POST', 'Jobs/Preview', { itemIds: Array.from(selected.keys()) }).then(function (p) {
            var skipped = p.skipped.alreadySynced + p.skipped.noEnglishAudio + p.skipped.noSidecar + p.skipped.noVideoFile;
            $('selection').textContent = p.selected + ' selected in ' + p.seriesName + ': ' + p.eligible + ' would be fixed, ' +
                skipped + ' skipped (' + p.skipped.alreadySynced + ' already synced)' +
                (p.attemptedBefore ? ', ' + p.attemptedBefore + ' previously attempted' : '') + '. Queuing arrives in M2.';
        }).catch(function (err) { $('selection').textContent = err.message; });
    }

    // ---------- search ----------
    var searchTimer = null;
    function search() {
        var q = $('q').value.trim();
        var out = $('results');
        if (q.length < 2) { out.textContent = ''; return; }
        api('GET', 'Search?q=' + encodeURIComponent(q) + '&limit=30').then(function (page) {
            out.textContent = '';
            page.items.forEach(function (item) { out.appendChild(row(item, true)); });
            if (!page.items.length) { var li = document.createElement('li'); li.textContent = 'No matches.'; out.appendChild(li); }
        }).catch(showError);
    }

    // ---------- admin ----------
    function showDiagnostics(d) {
        var dl = $('diag');
        dl.textContent = '';
        [
            ['Plugin version', d.pluginVersion],
            ['State database', d.stateLocation],
            ['Database ready', d.stateReady ? 'yes' : 'no' + (d.stateError ? ' (' + d.stateError + ')' : '')],
            ['Menu entry registered', d.menuRegistered ? 'yes' : 'no' + (d.menuError ? ' (' + d.menuError + ')' : '')],
            ['Test row in state.db', d.lastDiagnosticWrite || 'none yet']
        ].forEach(function (pair) {
            var dt = document.createElement('dt'); dt.textContent = pair[0];
            var dd = document.createElement('dd'); dd.textContent = pair[1];
            dl.appendChild(dt); dl.appendChild(dd);
        });
    }

    function initAdmin() {
        $('admin').hidden = false;
        var refresh = function () { return api('GET', 'Admin/Diagnostics').then(showDiagnostics).catch(showError); };
        $('diag-refresh').addEventListener('click', refresh);
        $('diag-write').addEventListener('click', function () { api('POST', 'Admin/Diagnostics/State').then(showDiagnostics).catch(showError); });
        $('subsync').addEventListener('click', function () {
            var out = $('subsync-out');
            out.hidden = false;
            out.textContent = 'Running subsync-cli check and version (can take a minute)…';
            api('POST', 'Admin/TestSubSync').then(function (r) {
                out.textContent = r.message + '\nVersion: ' + (r.version || 'unknown') + '\nMode: ' + r.mode +
                    '\nExit code: ' + (r.checkExitCode == null ? 'none' : r.checkExitCode) + '\n\n' + (r.checkOutput || '');
            }).catch(function (err) { out.textContent = err.message; });
        });
        refresh();
    }

    // ---------- start ----------
    function start() {
        if (!token()) { $('who').hidden = true; $('signin').hidden = false; return; }
        api('GET', 'Me').then(function (me) {
            $('who').textContent = 'Signed in as ' + me.name + (me.isAdmin ? ' (administrator)' : '');
            if (!me.allowed) { $('denied').hidden = false; return; }
            $('app').hidden = false;
            $('more').addEventListener('click', function () { browse(current.parentId, true); });
            $('clear').addEventListener('click', function () { selected.clear(); selectionChanged(); });
            $('preview').addEventListener('click', preview);
            $('q').addEventListener('input', function () { clearTimeout(searchTimer); searchTimer = setTimeout(search, 250); });
            browse(null);
            if (me.isAdmin) { initAdmin(); }
        }).catch(function (err) {
            if (err.status === 401) { $('who').hidden = true; $('signin').hidden = false; return; }
            showError(err);
        });
    }

    start();
})();
