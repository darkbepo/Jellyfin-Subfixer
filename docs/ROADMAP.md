# Roadmap

Check items off as they land. Tracks: **A** backend (`Api/ Services/ Worker/ State/ Configuration/`), **B** web (`Web/`). After M0 and the M1 scaffold they run in parallel worktrees and meet only at `API.md`.

## M0 Spikes (single session)
Do `M0-SPIKES.md`. Acceptance: every spike has findings; docs corrected.
- [x] Source research for S0, S1, S2, S3, S4, S7 (2026-11-11, against Jellyfin 12.4), docs corrected.
- [ ] Owner live checks, `M0-LIVE-CHECKS.md` Part A (S0, S4, S5): no plugin needed.
- [ ] Owner live checks, Part B (S1, S2, S3, S6, S7): needs the M1 test build.

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
- [ ] `ISubSyncWorker` and `CliWorker` (process, report parsing, kill tree, timeout).
- [ ] `JobQueue` (in memory, caps, single consumer), `JobRunner` implementing `JOB-FLOW.md`.
- [ ] `BackupService`, verification, `RecoveryService` at startup, `RefreshService`.
- [ ] `POST /Jobs`, `GET /Status`, `POST /Jobs/Cancel`.
Acceptance: fault-injection tests (`TESTING.md`) all leave the original subtitle intact; real run on one test season updates subtitles and Jellyfin serves them immediately; a second run skips every episode.

## M3 Web UI (B)
- [ ] Explorer, search, selection with series lock, footer preview, progress panel, disclaimer, error mapping, in mock mode first (`UI.md`).
- [ ] Menu injection (S1 result) and page serving (S2 result).
- [ ] Switch from mock to the real API.
Acceptance: the `UI.md` behaviors work on desktop and phone width against the real server; the series lock cannot be bypassed in the UI, and the API still rejects anything the UI would not send.

## M4 Settings and hardening (A, page markup B)
- [ ] Admin settings page with Test SubSync, `AllowedUsers` enforcement, caps, clear error messages and logging.
- [ ] Stop-after-3-consecutive-crashes rule, timeouts, log hygiene.
Acceptance: all settings take effect; non-admin and admin paths verified.

## M5 Release
- [ ] Package zip plus checksum, `manifest.json`, release workflow, final `README.md` (document File Transformation and SubSync requirements).
- [ ] Manual test pass (`TESTING.md` checklist) on a clean Jellyfin 12 server.
