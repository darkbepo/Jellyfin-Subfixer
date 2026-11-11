# Bazarr subtitle search: plan for a separate session

Brief for the session that builds this feature. Read `CLAUDE.md` and `SPEC.md` first, then this file. The feature lives in the same plugin and follows the same non-negotiables unless this file says otherwise.

## Goal
From the Subtitle Fixer page, a user picks an episode (later also a movie) and presses **Find subtitles**. The plugin asks the owner's Bazarr for manual-search results and shows the same data Bazarr's own "Search Subtitles" dialog shows:

| Column | Meaning in Bazarr's dialog |
|---|---|
| Score | Match score in percent |
| Language | `EN`, or `EN:HI` for hearing-impaired |
| Provider | e.g. embeddedsubtitles, opensubtitlescom, gestdown, supersubtitles |
| Release | Release name; several names collapse behind a dropdown |
| Uploader | Uploader name, or `-` |
| Match | Green check = matches the file; yellow = some properties don't match (tooltip lists which) |
| Get | Download button |

The dialog's header also shows the **Resource**: the video path Bazarr matched.

The user presses **Get**. The plugin tells Bazarr to download that result. When the download lands, the new subtitle goes into the **existing SubSync queue** to be re-timed, through the normal backup, run, verify, refresh flow (`JOB-FLOW.md`).

## Hard rules for this feature
1. **The Bazarr API key never reaches the browser.** It's stored in plugin settings (admin only), and the plugin calls Bazarr server-side. The page only talks to our own endpoints.
2. **Same permissions as the rest of the plugin:** `AllowedUsers`, and users only see items they can access in Jellyfin. Check access on the Jellyfin item before any Bazarr call.
3. **Never lose a subtitle.** A Bazarr download can overwrite an existing sidecar, so back up the current `<video>.<tag>.srt` before asking Bazarr to download. Restore it if the download fails or produces nothing. After a good download, the SubSync job makes its own backup as usual.
4. **One SubSync at a time.** Downloads queue into the same global queue (`JobQueue`, M2); never start SubSync directly.
5. **A fresh download must be synced, even on an already-synced episode.** The `done` record is keyed on the video fingerprint, so a successful download has to delete that item's record before queuing. Otherwise the job is skipped as `already_synced`.
6. **Rate limits:** at most one search per user every few seconds and one download in flight per item. Provider accounts (opensubtitles etc.) have quotas.
7. **Server-side checks for everything,** as with the rest of the plugin.

## Do not guess the Bazarr API
Bazarr's API is not reliably in training data. Before writing the client, verify each call against the owner's Bazarr (its version is in Bazarr > System > Status). Bazarr serves its own API docs; look for a Swagger/OpenAPI page under `/api/` on the owner's instance. Record verified calls, field names and the Bazarr version in `docs/BAZARR-API.md` (new file).

**Believed, unverified (check each):**
- Auth: header `X-API-KEY: <key>`.
- Series and episodes:
  - `GET /api/series` returns series with `sonarrSeriesId`, `tvdbId`, `imdbId` and a path.
  - `GET /api/episodes?seriesid[]=<sonarrSeriesId>` returns episodes with `sonarrEpisodeId`, season, episode and path.
- Episode manual search: `GET /api/providers/episodes?episodeid=<sonarrEpisodeId>` returns results with provider, release info, score, language, hearing_impaired, forced, uploader, matches/dont_matches, and an opaque `subtitle` value to send back when downloading.
- Episode download: `POST /api/providers/episodes` with the series and episode ids, `provider`, `subtitle` (the opaque value), `hi`, `forced`, `original_format`.
- Movies: the same shape under `/api/movies` and `/api/providers/movies`, keyed by `radarrid`.

## Mapping a Jellyfin item to Bazarr
Bazarr knows items by Sonarr and Radarr ids, not Jellyfin ids. Resolve in this order and cache the result:
1. **By path (preferred):** Jellyfin's `Episode.Path` against Bazarr's episode path.
   - The paths can differ if Bazarr runs elsewhere. Add an admin setting for path mappings (Jellyfin prefix to Bazarr prefix), like Bazarr's own path mapping.
   - Compare case-insensitively on Windows.
2. **By ids:** the series' TVDB or IMDb id from Jellyfin's provider ids, matched to Bazarr's `tvdbId`/`imdbId`, then season and episode number.
3. **No match:** show "Bazarr doesn't know this episode" and stop.

Show the matched path (the Resource line) in the dialog so the user can see the match is right.

## Where the code goes
New folder `src/Jellyfin.Plugin.SubtitleFixer/Bazarr/`:
- `IBazarrClient`, `BazarrClient`: typed HTTP client using `IHttpClientFactory`, 30 s timeout, `CancellationToken` everywhere. No new NuGet dependency; `System.Net.Http.Json` is in the framework.
- `BazarrItemResolver`: the Jellyfin-to-Bazarr mapping above.
- `BazarrDownloadService`: backup, download, verify, restore on failure, clear the `done` record, queue the SubSync job.

Settings, added to `PluginConfiguration` and the config page: `BazarrUrl`, `BazarrApiKey`, `BazarrPathMappings`, `BazarrEnabled` (default off). Add a **Test Bazarr** button that calls a cheap Bazarr endpoint (status or system info) and shows the version.

## API (add to `API.md` in the same change)
- `GET /SubtitleFixer/Bazarr/Search?itemId=…` returns `{ resource, results: [ { id, score, language, hearingImpaired, forced, provider, releases: [..], uploader, matches: [..], dontMatches: [..] } ] }`.
  - `id` is our own short-lived handle (kept server-side for about 10 minutes) for Bazarr's opaque `subtitle` value, so the browser never handles provider internals.
- `POST /SubtitleFixer/Bazarr/Download { itemId, resultId }` returns `202 { queued: true }`. Progress then shows in the normal queue (`GET /Status`), with a "downloaded from <provider>" note.
- Error codes:
  - `bazarr_unavailable` (503)
  - `bazarr_not_found` (404, item unknown to Bazarr)
  - `result_expired` (410)
  - `rate_limited` (429)
  - `download_failed` (502)

## UI (add to `UI.md` in the same change)
- **Find subtitles** button on an episode row. It opens a dialog with the Resource line and the table above, sorted by score. HI is shown as a badge, and match details appear on hover or long-press.
- The dialog must work at phone width: the table collapses into cards.
- **Get** has a confirmation step when a subtitle already exists: "This replaces the current English subtitle. It will be re-timed afterwards."
- After Get, close the dialog and show the item in the shared queue panel.

## Conflicts with the current spec (owner decides before building)
1. **Hearing-impaired subtitles.** Bazarr names them `<video>.en.hi.srt`, which v1 rules ignore (D6).
   - Options: (a) only allow non-HI downloads at first; (b) save HI results as `.en.srt`; (c) extend the naming rule to `.en.hi.srt`.
   - Suggested: (a) at first.
2. **Movies.** v1 is TV only, and Bazarr search covers movies. The SubSync side would need Movie support in eligibility, browse and refresh.
   - Suggested: episodes first, movies as phase 3.
3. **Bazarr's own sync.** Bazarr has its own automatic subtitle synchronization option. If it's on, downloads get synced twice.
   - Suggested: the owner turns it off in Bazarr for this flow, or the plugin skips SubSync when Bazarr already synced. Decide after checking the owner's Bazarr settings.
4. **Who may download.** Downloads use the owner's provider quotas.
   - Suggested: a separate `BazarrAllowedUsers` setting, default `AdminOnly`.

Record the answers in `DECISIONS.md`.

## Phases and dependencies
- **Phase 1:** Bazarr client, resolver, settings, Search endpoint and the read-only dialog. Needs M1 (on `main`). Doesn't need the job engine.
- **Phase 2:** Download plus SubSync. Needs M2 (`JobQueue`, `JobRunner`, `BackupService`). Until M2 lands, Download can stay behind `BazarrEnabled` and return `501`.
- **Phase 3:** movies, if the owner wants them.

## Working alongside the main track
- Work in your own worktree and branch, cut from `main` after M1 is merged.
- Own `Bazarr/` and the Bazarr parts of the page.
- Changes to shared files (`PluginConfiguration`, `API.md`, `UI.md`, `DECISIONS.md`, the queue's job type) should be small, additive and described in the PR. A queue job will need a `source` field (`manual` or `bazarr`) and a note; agree that with the M2 owner through `API.md`.
- Commit as the owner (`darkbepo <darkbepo@live.com>`), with no co-author or attribution lines. Only working, tested code goes to `main`.

## Done means
Builds clean, tests pass, docs updated. Tests cover:
- the resolver: path mapping, id fallback, no match;
- the download service: backup and restore on a failed download, `done` record cleared, job queued;
- the API: the key never appears in any response, permissions apply, rate limits work.

A real run against the owner's Bazarr finds and downloads a subtitle, and SubSync then re-times it.
