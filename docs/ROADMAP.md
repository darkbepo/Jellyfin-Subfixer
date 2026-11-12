# Roadmap

Check items off as they land. Tracks: **A** backend (`Api/ Services/ Worker/ State/ Configuration/`), **B** web (`Web/`). After M0 and the M1 scaffold they run in parallel worktrees and meet only at `API.md`.

## M0 Spikes (single session)
Do `M0-SPIKES.md`. Acceptance: every spike has findings; docs corrected.
- [x] Source research for S0, S1, S2, S3, S4, S7 (2026-11-11, against Jellyfin 12.4), docs corrected.
- [ ] Owner live checks, `M0-LIVE-CHECKS.md` Part A (S0, S4, S5): no plugin needed.
- [ ] Owner live checks, Part B (S1, S2, S3, S6, S7). Done on Testflix, 2026-11-11: S7 passes; S1 and S2 pass for an admin at desktop width. Still open: S1 and S2 for a non-admin and at phone width, S3 timings (needs a TV library), S6 (needs SubSync).

## M1 Foundation (A)
- [x] Solution scaffold (net10.0, Jellyfin 12.4.0 packages, template conventions), plugin GUID, `meta.json` via `build/package.ps1`, build and test commands in `CLAUDE.md`, CI that builds and tests (`.github/workflows/build.yml`).
- [x] Test build for `M0-LIVE-CHECKS.md` Part B: menu injection, `GET /Me`, and an admin diagnostics page that times Browse/Search calls, runs `subsync-cli check`/`version`, and writes and reads one `state.db` row.
- [x] Plugin configuration class and settings page with the settings in `SPEC.md` section 6.
- [x] `SqliteStateStore` with schema and migrations (per S7 result).
- [x] `EligibilityService` (rules in `SPEC.md` section 4) and `SelectionValidator` (hard rules 1, 2, 4). Series resolution lives in `ILibraryFacade.GetNode` (every node carries its `SeriesId`), so there is no separate `SeriesResolver`.
- [x] `GET /Browse`, `GET /Search`, `POST /Jobs/Preview`, `GET /Me` per `API.md`.
- [ ] Owner: Browse and Preview against the real server, timings recorded in `ARCHITECTURE.md` (`M0-LIVE-CHECKS.md` Part B).
Acceptance: unit tests for validator, eligibility, fingerprint match and invalidation pass (56 tests, 2026-11-11); Browse and Preview work against the real server with real timings recorded in `ARCHITECTURE.md` (pending owner).

## M2 Job engine (A)
- [x] `ISubSyncWorker` and `CliWorker` (process, report parsing, kill tree, timeout).
- [x] `JobQueue` (in memory, caps, single consumer, shared and visible to all, D19), `JobRunner` implementing `JOB-FLOW.md`, `JobProcessor` with the exit-7 and 3-crash stops.
- [x] `BackupService` with the journal (D23), verification, `RecoveryService` at startup, `RefreshService`.
- [x] `POST /Jobs`, `GET /Status`, `POST /Jobs/Cancel`, `POST /Jobs/Clear`; Start button and queue panel on the test page.
- [x] Real run on a test season (Testflix, 2026-11-11, CPU):
  - S01E01 and S01E02 of a TV season synced and were served by Jellyfin immediately.
  - A second Start was refused (`nothing_eligible`, already synced).
  - Cancel mid-run left the subtitle byte-identical.
  - Hard-killing Jellyfin mid-run restored the subtitle on restart. This needed the D29 fix for orphaned SubSync.
- [ ] Owner: the same on the real server with its GPU and Jellyfin as a service (S6).
Acceptance: fault-injection tests (`TESTING.md`) all leave the original subtitle intact (done, 2026-11-11: 171 tests); real run on one test season updates subtitles and Jellyfin serves them immediately; a second run skips every episode (pending owner).

## M3 Web UI (B)
- [x] Explorer, search, selection with series lock, footer preview, progress panel, disclaimer, error mapping, in mock mode first (`UI.md`). Tested 2026-11-11 in mock mode at desktop and 375 px width: drill-down, paging, season tri-state, series lock, keyboard, search-to-season, Start, live row statuses, cancel, member/denied/signed-out/SubSync-off states.
- [x] Menu injection (S1 result) and page serving (S2 result) (built in M1).
- [x] Switch from mock to the real API (same code; `?mock=1` only swaps the transport).
- [ ] Owner: the same behaviors against the real server (needs a TV library; Testflix has only movies, `LOCAL-TEST-ENV.md`).
Acceptance: the `UI.md` behaviors work on desktop and phone width against the real server (pending); the series lock cannot be bypassed in the UI, and the API still rejects anything the UI would not send (`SelectionValidatorTests`).

## M4 Settings and hardening (A, page markup B)
- [x] Admin settings page with Test SubSync, `AllowedUsers` enforcement, caps, clear error messages and logging.
- [x] Stop-after-3-consecutive-crashes rule, timeouts, log hygiene. Each finished item logs one line (status, name, item id, seconds; warning level for failed and check), and every queue stop logs its reason. SubSync's output is logged only when it fails. The Bazarr key travels in a header and is never logged.
- Live on Testflix 2026-11-11, with an admin and a non-admin account: `AllowedUsers = AdminOnly` gives the non-admin `/Me` `allowed:false` (`admins_only`) and `403 forbidden` on Browse, Status and Preview. `AllAuthenticated` with no SubSync folder gives `not_configured` and `403`, and Start returns `503 subsync_unavailable`. Configured, the non-admin can browse, see the queue and preview, but gets `403` from Test SubSync, Test Bazarr, Diagnostics and the plugin's configuration. `MaxEpisodesPerRequest = 1` gives `400 cap_exceeded` on Preview and Start, and `MaxQueuedEpisodes = 1` gives `409 queue_full` with nothing queued. Saved settings applied without a restart.
- [x] Dashboard sidebar link to the settings (D36), pop-up page (D35), no translate prompt (D37), GPU/CPU from the last sync (D38). Verified on Testflix 2026-11-11.
Acceptance: all settings take effect; non-admin and admin paths verified.

## M5 Release
- [x] Package zip plus checksum, `manifest.json`, release workflow, final `README.md` (document File Transformation and SubSync requirements). The release is `build\release.ps1`, run by the owner (D39). The repository install was tested on Testflix 2026-11-11 from a local manifest: catalog, checksum, install folder, load.
- [x] First release published 2026-11-11: [v0.1.0.0](https://github.com/darkbepo/Jellyfin-Subfixer/releases/tag/v0.1.0.0) (zip, .md5, manifest.json; tag and release by darkbepo). The downloaded zip matches the manifest checksum, and Testflix's catalog lists it from the real repository URL.
- [ ] Owner: install it from the repository URL on the real server.
- [ ] Manual test pass (`TESTING.md` checklist) on a clean Jellyfin 12 server.

## M6 Owner feedback (2026-11-12)
- [x] Pop-up header: no "Signed in as …" line; the close button is an SVG icon, transparent until hovered (`UI.md`).
- [x] Settings: choose the libraries people can pick from, enforced on the server for every library read (D43).
- [x] Libraries and Queue are collapsible panels; after Start the explorer goes back to Libraries, folds, and the queue opens.
- [x] Finished queue items clear after a minute; failed and check outcomes go to a failed-syncs log with Retry (D44).
- [x] Simpler progress line ("<item> · 73% · about 4 min left", "2 of 5" by the bar), no misleading elapsed time; coloured chips when finished.
- [x] Find subtitles shows only Score, Language, Release, Match and Get, and doesn't mention Bazarr or providers.
- [x] Diagnostics stays admin-only (already the case: page and endpoints).
- Tested 2026-11-12: build clean, 326 tests (new: library scope, queue expiry, failed-syncs log and its migration, log on failed and check only); mock mode in a headless browser at 1000 px and 390 px, in the pop-up from a stand-in menu, and the settings page's library switches with a stubbed `ApiClient`.
- [ ] Owner: the `M0-LIVE-CHECKS.md` "Libraries setting, auto-clear and failed-syncs log" checks on the real server.
Acceptance: the items above work against the real server; a user can't browse, search, queue or retry anything outside the chosen libraries through the API.

## M7 SubSync 1.3.0 (2026-11-12)
Phase 1: use 1.3.0 with one SubSync process per episode (D5 unchanged), and keep 1.2.0 working.
- [x] Version detection: `subsync-cli version`, cached per CLI path, size and modification time; unreadable counts as 1.2.0.
- [x] `--output <subtitle> --overwrite` from 1.3.0: every name re-timed in place, no `.hi` workaround (D27). The 1.2.0 path is unchanged.
- [x] `--progress` from 1.3.0: stage, listening percent mapped into the bar (`percent` in `/Status`), GPU/CPU from `device` events (D38, D40). No `LogTail` then.
- [x] Report: optional `speech_recognition` and `timings`; layout confirmed (S5).
- [x] Estimate: cached runs are skipped (1.3.0 `from_cache`; 1.2.0 runs under 1% of the video's length or under 10 s); 1.3.0 learns start-up, reading, listening and matching (D40). Fixes a Retry or no-match re-sync dragging the estimate down, on 1.2.0 too.
- Tested 2026-11-12 on Linux: build clean, 412 tests, including recorded-output playback of `CliWorker` for both versions, and the page in mock mode in headless Chromium (bar follows `percent`, never moves backwards). Windows-only process tests and the real CLI were not run.
- [ ] Owner, on the real server with SubSync 1.3.0: a `.en.hi.srt` episode syncs in place; the bar follows SubSync's real percentage; GPU shows on the settings page; a Retry of a no-match item doesn't throw off the estimate. Optionally `RealSubSyncTests` with `SUBFIXER_SUBSYNC_FOLDER` and `SUBFIXER_TEST_CLIP`.

Phase 2: `subsync-cli serve`, one process per queue run, replacing D5 (D45, owner approved 2026-11-12 with the cancel/timeout requeue and the timeout clock changes).
- [x] `ServeSession` over `IServeTransport` (`ServeProcess`: stdin/stdout, Job Object, leftover-run record); `ISubSyncWorker.StartSessionAsync` (fallback by default); `CliWorker` starts `serve --overwrite [--keep-layout] [--cpu]` from 1.3.0.
- [x] `JobRunner` in steps (prepare, run, finish, restore); `JobProcessor.RunQueueAsync`: back up before sending, send the next item at `listening` (at most two in SubSync), close stdin when empty, restart on changed settings.
- [x] Cancel, timeout, crash, exit 7 and Jellyfin stopping restore every item without a result; the other item of a cancel or timeout is requeued untried; one crash counts once (D14); the time limit starts at the item's first stage or the previous result.
- [x] Fallback to one `sync` per item for 1.2.0 or when `serve` can't start.
- Tested 2026-11-12 on Linux: build clean, 443 tests (31 new for `serve`, all fault-injection tests unchanged and passing), repeated 9 times and 3 times on one CPU core with no failure; three deliberate code faults (crash counted per item, other item cancelled instead of requeued, clock started at send) each made a test fail. `ServeProcess` was run against a `/bin/sh` stand-in. Not run: the real `serve`, and `ServeProcess` on Windows.
- [ ] Owner, on the real server with SubSync 1.3.0 (`M0-LIVE-CHECKS.md`, Phase 2): a whole season through one SubSync process, cancel mid-season leaves every subtitle intact, and a season's total time against Phase 1.

## M8 SubSync updates from the settings page (D46, 2026-11-12)
- [x] Check GitHub's latest stable SubSync release when the settings page opens (cached 6 h; Check again).
- [x] Update SubSync: download, checksum against GitHub's, take both exes from the zip, check their version, swap with rollback, run `subsync-cli setup`; only with an empty queue, which stays closed until it has finished.
- [x] `GET`/`POST /Admin/SubSyncUpdate` (admin only), settings page section, `maintenance` in `/Status`.
- Tested 2026-11-12 on Linux: build clean, 484 tests (41 new); the settings page's update section in headless Chromium with a stand-in `ApiClient` (offered, running, done; a CSS bug that kept the button visible was found and fixed). Not run: the real GitHub API and release zip, and the swap on Windows.
- [ ] Owner: the `M0-LIVE-CHECKS.md` "SubSync update" checks.
