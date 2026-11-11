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
2. **Same permissions as the rest of the plugin:** `AccessService` (everyone by default, admins only until SubSync is configured, D10), and users only see items they can access in Jellyfin. Check access on the Jellyfin item before any Bazarr call.
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
- **Only where there is no subtitle (owner, D28).** **Find subtitles** appears only on episodes and movies that have no English sidecar, which are the rows the explorer marks `reason: no_sidecar`. Everywhere else the page only offers SubSync re-timing. The server enforces the same rule: Search and Download for an item that already has an accepted sidecar (`.en`, `.eng`, `.en.hi`, `.eng.hi` `.srt`) return `409 has_subtitle`.
- **Dialog:** opens with the Resource line and the table above, sorted by score. HI is shown as a badge; match details appear on hover or long-press.
- **Phone width:** the table collapses into cards.
- **No replace confirmation is needed**, because Find subtitles never shows where a subtitle exists. The backup in hard rule 3 stays as a safeguard against races (a subtitle appearing between search and download).
- After Get, close the dialog and show the item in the shared queue panel.

## Owner decisions (2026-11-11, recorded in `DECISIONS.md`)
1. **Hearing-impaired subtitles are allowed (D6, D27).** The fixer now accepts `<video>.en.hi.srt` and `<video>.eng.hi.srt` as well as `.en.srt`/`.eng.srt`, one per episode in that order. Bazarr's HI downloads (saved as `.en.hi.srt` with Bazarr's default `hi_extension: hi`) are therefore re-timed like any other.
2. **Movies: yes.** See `MOVIES-PLAN.md`. Movie search and download through Radarr fit there; the local test setup already has Radarr with one movie (`LOCAL-TEST-ENV.md`).
3. **Bazarr's own sync stays off.** It is already off on the local test Bazarr (`subsync.use_subsync: false`). The Test Bazarr button should warn when it is on (`GET /api/system/settings`, `subsync.use_subsync`), since downloads would then be synced twice.
4. **Everyone can search and download (D28).** There is no separate Bazarr permission: whoever may use Subtitle Fixer (`AllowedUsers`, `AccessService`) may search and download. The rate limits in hard rule 6 protect the provider quotas.

## Phases and dependencies
- **Phase 1:** Bazarr client, resolver, settings, Search endpoint and the read-only dialog. Needs M1 (on `main`). Doesn't need the job engine.
  - **Status (2026-11-11): built.** Client calls, resolver and search checked live against the test Bazarr 1.6.2 for the movie (path match, IMDb fallback, 35 results). The episode side is from source only (no Sonarr on the test setup). Still to do: an end-to-end run in Jellyfin on an item with no English sidecar (the test items all have one).
- **Phase 2:** Download plus SubSync. Needs M2 (`JobQueue`, `JobRunner`, `BackupService`). Until M2 lands, Download can stay behind `BazarrEnabled` and return `501`.
  - **Status (2026-11-11): built.** `BazarrDownloadService`, `POST /Bazarr/Download` and the Get button, with fault-injection tests (overwrite, half-written file, nothing saved, wrong name, Bazarr refusal, cancellation). Not run live yet: it needs an item with no English subtitle on the test server, and the final check is SubSync re-timing the downloaded file.
- **Phase 3:** movies (the owner wants them). Follow `MOVIES-PLAN.md`; movie search and download can be built as soon as the movie side of browse exists there.

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
