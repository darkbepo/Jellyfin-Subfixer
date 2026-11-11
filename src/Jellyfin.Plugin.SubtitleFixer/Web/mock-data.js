/* Subtitle Fixer mock API for ?mock=1 (UI.md, mock mode). Loaded only in mock mode.
   3 libraries, 5 series, one season with 40 episodes mixing eligible, synced, attempted and ineligible.
   Simulates the shared queue: another user's (hidden) episode is running when the page opens.
   Options: &user=member (not admin), &user=denied (not allowed), &subsync=off, &slow=1 (500 ms latency). */
(function () {
    'use strict';

    var params = new URLSearchParams(window.location.search);
    var userMode = params.get('user') || 'admin';
    var latency = params.get('slow') ? 500 : 60;
    var ME = 'user-me';
    var OTHER = 'user-other';

    var items = {};      // id -> node
    var children = {};   // parentId -> [ids]
    function add(node, parentId) {
        items[node.id] = node;
        (children[parentId] = children[parentId] || []).push(node.id);
        return node;
    }

    var libraries = [
        { id: 'lib-tv', name: 'TV Shows' },
        { id: 'lib-anime', name: 'Anime' },
        { id: 'lib-kids', name: 'Kids' },
        { id: 'lib-movies', name: 'Movies', libraryType: 'movies' }
    ];
    libraries.forEach(function (l) { add({ id: l.id, name: l.name, type: 'Library', libraryType: l.libraryType || 'tvshows' }, 'root'); });

    // 30 movies in a mix of states (MOVIES-PLAN.md).
    var movieTitles = ['Harbor of Glass', 'The Ninth Bell', 'Quiet Engines', 'Orchard Road', 'Paper Moon Hotel', 'Static Summer',
        'The Long Way North', 'Copper Sky', 'Lantern Street', 'Night Ferry', 'Salt and Ash', 'The Cartographer', 'Low Orbit',
        'Winter Garden', 'Tin Soldiers', 'Blue Hour', 'The Glass Archive', 'Second Sun', 'Field Notes', 'The Last Tram',
        'Open Season', 'Midnight Market', 'River Stones', 'The Small Print', 'Echo Valley', 'Iron Bloom', 'The Quiet Room',
        'Northern Lights', 'Paper Planes', 'Signal Fire'];
    movieTitles.forEach(function (t, i) {
        var n = i + 1;
        var facts = n % 6 === 0 ? { state: 'done' } : n % 7 === 0 ? { state: 'no_sidecar' } : n % 9 === 0 ? { state: 'no_english_audio' } :
            n === 11 ? { state: 'check', message: 'Matched only 6 lines; is this the subtitle for this video?' } : { state: 'ok' };
        add({ id: 'mov-' + n, name: t, type: 'Movie', productionYear: 1995 + (n * 7) % 30, facts: facts }, 'lib-movies');
    });

    var titles = ['The Day in Question', 'Open Water', 'A Quiet Return', 'Signals', 'The Long Night', 'Crossing', 'Paper Walls',
        'Low Tide', 'Second Chances', 'The Archive', 'North Road', 'Small Hours', 'Distant Shore', 'Old Friends', 'The Visit'];

    function makeSeries(id, name, libraryId, seasons) {
        var s = add({ id: id, name: name, type: 'Series', seriesId: id, seriesName: name }, libraryId);
        seasons.forEach(function (count, si) {
            var seasonNo = si + 1;
            var seasonId = id + '-s' + seasonNo;
            add({ id: seasonId, name: 'Season ' + seasonNo, type: 'Season', seriesId: id, seriesName: name, indexNumber: seasonNo }, id);
            for (var e = 1; e <= count; e++) {
                add({
                    id: seasonId + '-e' + e,
                    name: titles[(e + si) % titles.length],
                    type: 'Episode',
                    seriesId: id,
                    seriesName: name,
                    seasonId: seasonId,
                    indexNumber: e,
                    parentIndexNumber: seasonNo,
                    facts: factsFor(id, seasonNo, e)
                }, seasonId);
            }
        });
        return s;
    }

    // Deterministic mix per episode.
    function factsFor(seriesId, season, e) {
        if (seriesId === 'ser-mecha') { return e % 4 === 0 ? { state: 'ok' } : { state: 'no_english_audio' }; }
        if (seriesId === 'ser-kids') { return e % 3 === 0 ? { state: 'ok' } : { state: 'no_sidecar' }; }
        if (seriesId === 'ser-long' && season === 1) {
            if (e === 17) { return { state: 'no_video_file' }; }
            if (e % 7 === 0) { return { state: 'done' }; }
            if (e % 11 === 0) { return { state: 'check', message: 'Matched only 4 lines; is this the subtitle for this video?' }; }
            if (e % 9 === 0) { return { state: 'no_english_audio' }; }
            if (e % 13 === 0) { return { state: 'no_sidecar' }; }
            if (e === 23) { return { state: 'failed', message: 'SubSync could not read the audio track.' }; }
            return { state: 'ok' };
        }
        return e % 5 === 0 ? { state: 'done' } : { state: 'ok' };
    }

    makeSeries('ser-long', 'The Long Show', 'lib-tv', [40, 12, 10]);
    makeSeries('ser-harbor', 'Harbor Lights', 'lib-tv', [8, 8]);
    makeSeries('ser-quiet', 'Quiet Signal', 'lib-tv', [6]);
    makeSeries('ser-mecha', 'Mecha Dawn', 'lib-anime', [12]);
    makeSeries('ser-kids', 'Little Explorers', 'lib-kids', [10]);

    function libraryOf(node) {
        var seriesId = node.type === 'Library' ? null : node.seriesId;
        if (!seriesId) { return node.type === 'Library' ? node : null; }
        for (var i = 0; i < libraries.length; i++) {
            if ((children[libraries[i].id] || []).indexOf(seriesId) >= 0) { return items[libraries[i].id]; }
        }
        return null;
    }

    function evaluate(ep) {
        var f = ep.facts;
        switch (f.state) {
            case 'done': return { status: 'done', eligible: false, reason: 'already_synced', message: null };
            case 'check': case 'failed': return { status: 'attempted', eligible: true, reason: null, message: f.message };
            case 'no_video_file': case 'no_english_audio': case 'no_sidecar': return { status: 'none', eligible: false, reason: f.state, message: null };
            default: return { status: 'none', eligible: true, reason: null, message: null };
        }
    }

    function row(node) {
        var r = {
            id: node.id, name: node.name, type: node.type, seriesId: node.seriesId || null, seriesName: node.seriesName || null,
            seasonId: node.seasonId || null, indexNumber: node.indexNumber == null ? null : node.indexNumber,
            parentIndexNumber: node.parentIndexNumber == null ? null : node.parentIndexNumber,
            libraryType: node.libraryType || null, productionYear: node.productionYear || null,
            episodeCount: null, eligibleCount: null, syncedCount: null, status: 'none', eligible: null, reason: null, message: null
        };
        if (node.type === 'Season') {
            var eps = (children[node.id] || []).map(function (id) { return items[id]; });
            r.episodeCount = eps.length;
            r.eligibleCount = eps.filter(function (e) { return evaluate(e).eligible; }).length;
            r.syncedCount = eps.filter(function (e) { return evaluate(e).status === 'done'; }).length;
        }
        if (node.type === 'Episode' || node.type === 'Movie') {
            var ev = evaluate(node);
            r.status = ev.status; r.eligible = ev.eligible; r.reason = ev.reason; r.message = ev.message;
        }
        return r;
    }

    function crumbs(node) {
        var trail = [];
        var lib = libraryOf(node);
        if (lib) { trail.push(lib); }
        if (node.type !== 'Library') {
            trail.push(items[node.seriesId]);
            if (node.type === 'Season') { trail.push(node); }
            if (node.type === 'Episode') { trail.push(items[node.seasonId]); trail.push(node); }
        }
        return trail.map(function (n) { return { id: n.id, name: n.name, type: n.type }; });
    }

    function page(ids, start, limit) {
        return { totalCount: ids.length, items: ids.slice(start, start + limit).map(function (id) { return row(items[id]); }) };
    }

    // ---------- selection rules (mirror of the server) ----------
    function validate(ids) {
        if (!ids || !ids.length) { return { error: ['nothing_eligible', 'Select at least one season or episode.'] }; }
        var series = {}, episodes = [], seen = {}, movies = false, tv = false;
        for (var i = 0; i < ids.length; i++) {
            var n = items[ids[i]];
            if (!n) { return { error: ['unsupported_item_type', 'An item was not found or you cannot access it.'] }; }
            if (n.type !== 'Season' && n.type !== 'Episode' && n.type !== 'Movie') { return { error: ['unsupported_item_type', 'Only seasons, episodes and movies can be selected, not series or libraries.'] }; }
            if (n.type === 'Movie') { movies = true; } else { tv = true; }
            if (movies && tv) { return { error: ['mixed_selection', 'Pick either movies or episodes, not both at once.'] }; }
            if (n.type === 'Movie') { if (!seen[n.id]) { seen[n.id] = true; episodes.push(n); } continue; }
            series[n.seriesId] = true;
            if (Object.keys(series).length > 1) { return { error: ['multiple_series', 'Pick seasons or episodes of one series at a time.'] }; }
            (n.type === 'Season' ? children[n.id].map(function (id) { return items[id]; }) : [n]).forEach(function (e) {
                if (!seen[e.id]) { seen[e.id] = true; episodes.push(e); }
            });
        }
        var skipped = { alreadySynced: 0, noEnglishAudio: 0, noSidecar: 0, noVideoFile: 0 }, eligible = [], attempted = 0;
        episodes.forEach(function (e) {
            var ev = evaluate(e);
            if (ev.eligible) { eligible.push(e); if (ev.status === 'attempted') { attempted++; } }
            else if (ev.reason === 'already_synced') { skipped.alreadySynced++; }
            else if (ev.reason === 'no_english_audio') { skipped.noEnglishAudio++; }
            else if (ev.reason === 'no_sidecar') { skipped.noSidecar++; }
            else { skipped.noVideoFile++; }
        });
        if (eligible.length > 150) { return { error: ['cap_exceeded', eligible.length + (movies ? ' movies' : ' episodes') + ' can be fixed, but one request may queue at most 150. Select fewer seasons.'] }; }
        var sid = Object.keys(series)[0];
        if (movies) { return { kind: 'movies', seriesId: null, seriesName: null, selected: episodes.length, eligible: eligible, skipped: skipped, attemptedBefore: attempted }; }
        return { kind: 'episodes', seriesId: sid, seriesName: items[sid].name, selected: episodes.length, eligible: eligible, skipped: skipped, attemptedBefore: attempted };
    }

    // ---------- queue simulation ----------
    var queue = [];
    var stopReason = null;
    var jobSeq = 0;
    function job(itemId, name, seriesName, userId, status) {
        return { jobId: 'job-' + (++jobSeq), itemId: itemId, name: name, seriesName: seriesName, userId: userId, status: status || 'queued',
            message: null, source: 'manual', note: null, seconds: null, startedUtc: null, cancelRequested: false };
    }
    // Another user's episode from a library this viewer cannot see, already running.
    var other = job('hidden-1', 'S02E05 Private', 'Private Show', OTHER, 'running');
    other.startedUtc = new Date().toISOString();
    queue.push(other);
    queue.push(job('hidden-2', 'S02E06 Private', 'Private Show', OTHER));

    var outcomeSeq = 0;
    function tick() {
        var running = queue.filter(function (j) { return j.status === 'running'; })[0];
        if (running) {
            outcomeSeq++;
            var ep = items[running.itemId];
            if (running.cancelRequested) {
                running.status = 'cancelled'; running.message = 'Cancelled.';
            } else if (outcomeSeq % 6 === 0) {
                running.status = 'check'; running.message = 'Matched only 5 lines; is this the subtitle for this video?';
                if (ep) { ep.facts = { state: 'check', message: running.message }; }
            } else if (outcomeSeq % 9 === 0) {
                running.status = 'failed'; running.message = 'SubSync could not read the audio track.';
                if (ep) { ep.facts = { state: 'failed', message: running.message }; }
            } else {
                running.status = 'done';
                if (ep) { ep.facts = { state: 'done' }; }
            }
            running.seconds = 70 + (outcomeSeq * 13) % 40;
            running.finishedUtc = new Date().toISOString();
        }
        var next = queue.filter(function (j) { return j.status === 'queued'; })[0];
        if (next) { next.status = 'running'; next.startedUtc = new Date().toISOString(); }
    }
    setInterval(tick, 2500);

    function status() {
        var visible = function (j) { return j.userId === ME || j.itemId.indexOf('hidden') !== 0; };
        var map = function (j) {
            var v = visible(j);
            return { jobId: j.jobId, itemId: v ? j.itemId : null, name: v ? j.name : 'Episode in another library', seriesName: v ? j.seriesName : null,
                status: j.status, message: j.message, mine: j.userId === ME, source: j.source, note: v ? j.note : null, seconds: j.seconds, startedUtc: j.startedUtc };
        };
        var order = queue.filter(function (j) { return ['queued', 'running'].indexOf(j.status) < 0; })
            .concat(queue.filter(function (j) { return j.status === 'running'; }))
            .concat(queue.filter(function (j) { return j.status === 'queued'; }));
        var count = function (s) { return queue.filter(function (j) { return j.status === s; }).length; };
        var pending = count('queued') + count('running');
        var ran = queue.filter(function (j) { return j.seconds && ['done', 'check', 'failed'].indexOf(j.status) >= 0; });
        var avg = ran.length ? ran.reduce(function (a, j) { return a + j.seconds; }, 0) / ran.length : null;
        var ahead = null, idx = 0;
        order.filter(function (j) { return j.status === 'queued' || j.status === 'running'; }).some(function (j) {
            if (j.userId === ME) { ahead = idx; return true; }
            idx++;
            return false;
        });
        var running = queue.filter(function (j) { return j.status === 'running'; })[0];
        var stopping = running && running.cancelRequested;
        return {
            state: stopping ? 'stopping' : pending ? 'running' : 'idle',
            subsyncAvailable: params.get('subsync') !== 'off',
            stopReason: stopReason,
            current: running ? map(running) : null,
            totals: { queued: count('queued'), running: count('running'), done: count('done'), check: count('check'), failed: count('failed'), cancelled: count('cancelled'), skipped: count('skipped') },
            etaSeconds: avg == null ? null : pending * avg,
            aheadOfYours: ahead,
            items: order.map(map)
        };
    }

    // ---------- router ----------
    function ok(body, code) { return { status: code || 200, body: body }; }
    function err(code, http, message) { return { status: http, body: { error: code, message: message } }; }

    function route(method, path, body) {
        var url = new URL(path, 'http://mock/');
        var q = url.searchParams;
        var start = parseInt(q.get('startIndex') || '0', 10);
        var limit = Math.min(200, parseInt(q.get('limit') || '50', 10));
        var isAdmin = userMode === 'admin';
        var allowed = userMode !== 'denied';
        var p = url.pathname.replace(/^\//, '');

        if (p === 'Me') {
            return ok({ userId: ME, name: isAdmin ? 'Mock Admin' : 'Mock Member', isAdmin: isAdmin, allowed: allowed,
                reason: allowed ? null : 'not_configured',
                message: allowed ? null : 'Subtitle Fixer isn’t set up yet. Ask an administrator to finish its settings.' });
        }
        if (!allowed) { return err('forbidden', 403, 'An administrator has not allowed your account to use Subtitle Fixer.'); }

        if (p === 'Browse') {
            var parentId = q.get('parentId');
            if (!parentId) { var libs = page(children.root, start, limit); libs.parent = null; return ok(libs); }
            var parent = items[parentId];
            if (!parent || parent.type === 'Episode') { return err('not_found', 404, 'That item was not found or you cannot access it.'); }
            var result = page(children[parentId] || [], start, limit);
            result.parent = { id: parent.id, name: parent.name, type: parent.type, breadcrumb: crumbs(parent) };
            return ok(result);
        }
        if (p === 'Search') {
            var term = (q.get('q') || '').trim().toLowerCase();
            var hits = Object.keys(items).filter(function (id) {
                var n = items[id];
                return term && (n.type === 'Series' || n.type === 'Episode' || n.type === 'Movie') && n.name.toLowerCase().indexOf(term) >= 0;
            });
            var res = page(hits, start, limit); res.parent = null;
            return ok(res);
        }
        if (p === 'Jobs/Preview' || p === 'Jobs') {
            var v = validate(body && body.itemIds);
            if (v.error) { return err(v.error[0], 400, v.error[1]); }
            if (p === 'Jobs/Preview') {
                return ok({ kind: v.kind, seriesId: v.seriesId, seriesName: v.seriesName, selected: v.selected, eligible: v.eligible.length, skipped: v.skipped, attemptedBefore: v.attemptedBefore });
            }
            if (params.get('subsync') === 'off') { return err('subsync_unavailable', 503, 'SubSync is not configured. Ask an administrator.'); }
            if (!v.eligible.length) { return err('nothing_eligible', 400, 'None of the selected episodes can be fixed.'); }
            var pendingIds = {};
            queue.forEach(function (j) { if (j.status === 'queued' || j.status === 'running') { pendingIds[j.itemId] = true; } });
            var fresh = v.eligible.filter(function (e) { return !pendingIds[e.id]; });
            var pendingCount = Object.keys(pendingIds).length;
            if (pendingCount + fresh.length > 300) { return err('queue_full', 409, 'The queue holds at most 300 episodes and has ' + pendingCount + '.'); }
            fresh.forEach(function (e) {
                queue.push(job(e.id, e.type === 'Movie' ? e.name + ' (' + e.productionYear + ')' : 'S' + pad(e.parentIndexNumber) + 'E' + pad(e.indexNumber) + ' ' + e.name, e.seriesName || '', ME));
            });
            stopReason = null;
            return ok({ queued: fresh.length, skipped: v.selected - fresh.length, queueLength: pendingCount + fresh.length }, 202);
        }
        if (p === 'Status') { return ok(status()); }
        if (p === 'Jobs/Cancel') {
            queue.forEach(function (j) {
                if (!(isAdmin || j.userId === ME)) { return; }
                if (j.status === 'queued') { j.status = 'cancelled'; j.message = 'Cancelled.'; }
                if (j.status === 'running') { j.cancelRequested = true; }
            });
            return ok(status());
        }
        if (p === 'Jobs/Clear') {
            queue = queue.filter(function (j) { return j.status === 'queued' || j.status === 'running' || !(isAdmin || j.userId === ME); });
            return ok(status());
        }
        if (p === 'Admin/Diagnostics' || p === 'Admin/Diagnostics/State') {
            if (!isAdmin) { return err('forbidden', 403, 'Administrators only.'); }
            return ok({ pluginVersion: '0.0.0-mock', stateLocation: 'C:\\mock\\state.db', stateReady: true, stateError: null, menuRegistered: true, menuError: null,
                lastDiagnosticWrite: p === 'Admin/Diagnostics/State' ? new Date().toISOString() : null });
        }
        if (p === 'Admin/TestSubSync') {
            if (!isAdmin) { return err('forbidden', 403, 'Administrators only.'); }
            return ok({ ok: true, version: '1.2.0-mock', mode: 'GPU', message: 'SubSync is ready.', checkExitCode: 0, checkOutput: 'mock check output' });
        }
        return err('not_found', 404, 'No mock for ' + method + ' ' + p);
    }

    function pad(n) { return n < 10 ? '0' + n : String(n); }

    window.SubtitleFixerMock = {
        handle: function (method, path, body) {
            return new Promise(function (resolve) {
                setTimeout(function () { resolve(route(method, path, body)); }, latency);
            });
        }
    };
})();
