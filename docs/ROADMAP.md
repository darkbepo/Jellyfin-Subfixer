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
- [ ] Admin settings page with Test SubSync, `AllowedUsers` enforcement, caps, clear error messages and logging.
- [ ] Stop-after-3-consecutive-crashes rule, timeouts, log hygiene.
- [x] Dashboard sidebar link to the settings (D36), pop-up page (D35), no translate prompt (D37), GPU/CPU from the last sync (D38). Verified on Testflix 2026-11-11.
Acceptance: all settings take effect; non-admin and admin paths verified.

## M5 Release
- [ ] Package zip plus checksum, `manifest.json`, release workflow, final `README.md` (document File Transformation and SubSync requirements).
- [ ] Manual test pass (`TESTING.md` checklist) on a clean Jellyfin 12 server.
