# Architecture

```
Browser page (embedded JS)  <->  Plugin REST API  <->  Job queue (1 worker, in memory)
                                      |                     |
                                 State store            ISubSyncWorker -> subsync-cli.exe (one process per episode)
                                      |
                          Jellyfin library manager / provider manager
```

## Components
| Folder | Responsibility | Track |
|---|---|---|
| `Api/` | Controllers, request validation, DTOs (`API.md`) | A |
| `Domain/` | Rules with no Jellyfin types, unit-tested: `EligibilityService`, `SelectionValidator`, `ILibraryFacade` (the only seam to Jellyfin's library) | A |
| `Services/` | Jellyfin-facing services: `JellyfinLibraryFacade`, `AccessService` (`AllowedUsers`), `StartupService`; later `JobQueue`, `JobRunner`, `BackupService`, `RecoveryService`, `RefreshService`, `StatusService` | A |
| `Worker/` | `ISubSyncWorker`, `CliWorker` (spawns `subsync-cli.exe`), `WorkerResult` | A |
| `State/` | SQLite state store, migrations | A |
| `Web/` | Embedded `index.html`, `app.js`, `styles.css`, menu-injection script, mock data | B |
| `Configuration/` | Plugin configuration and the admin settings page | A (page markup: B) |
| `Bazarr/` | `IBazarrClient`/`BazarrClient` (server-side calls to Bazarr), `BazarrPathMapper`, `BazarrItemResolver` (Jellyfin item to Radarr/Sonarr ids), `BazarrSearchService` with `BazarrResultStore` and `BazarrRateLimiter`, `JellyfinBazarrLookupSource`, `BazarrDownloadService` (in-memory backup, download, verify, restore, queue as `source:bazarr`) (`BAZARR-PLAN.md`, `BAZARR-API.md`) | Bazarr |

Tracks A and B are separate worktrees; they meet only at `API.md`. Changing a contract means updating `API.md` in the same change and telling the other track's session.

## Worker interface
`ISubSyncWorker.RunAsync(videoPath, subtitlePath, languageTag, options, CancellationToken)` returns `WorkerResult { Status (Synced|NoMatch|Failed|Cancelled|TimedOut|Unavailable|Crashed), OutputPath, Reason, AudioLanguage, Seconds, SubSyncVersion, ExitCode }`. `Unavailable` (exit 7) stops the queue; `Crashed` (exit 10, no report, start failure) counts toward the 3-crash stop. `ProcessRunner` starts the CLI without a window and kills the whole process tree on cancel or timeout, waiting for it to exit before the backup is restored. v1 implementation spawns one `subsync-cli.exe` per episode. A future implementation can call an HTTP sidecar (Linux). Nothing outside `Worker/` may know about the CLI.

## Queue
In-memory, single consumer. Dropped on restart (the user can requeue; already-done episodes are skipped by state). Status snapshots are immutable objects read by the API without locking the runner.
- `Services/Jobs/JobQueue`: the one shared queue (D19, D25). `Enqueue` (caps, no duplicates), `TakeAsync` (consumer only), `Complete`, `Cancel` (own items, admins all), `Stop`, `ClearFinished`, `Snapshot`.
- `JobProcessor` (hosted): waits for startup recovery, then takes one entry at a time and applies the stop rules.
- `JobRunner`: one episode through `JOB-FLOW.md` with `BackupService`, `SubtitleVerifier`, `ISubSyncWorker`, `IRefreshService`.
- `RecoveryService`: runs in `StartupService` right after the state store opens.
- `/Status` hides other users' items in libraries the viewer can't access, using `VisibilityCache` (5-minute per-user cache) so 1-second polling stays cheap.

## State store
SQLite file in the plugin data folder, WAL mode, one writer (`StateStore`). `Microsoft.Data.Sqlite` is a compile-only reference: at runtime the plugin uses the copy Jellyfin 12.4 already loads (10.0.14), native library included (spike S7, D17). Live load check pending.

```sql
CREATE TABLE schema_version (version INTEGER NOT NULL);
CREATE TABLE synced_items (
  item_id            TEXT PRIMARY KEY,   -- Jellyfin item GUID
  video_path         TEXT NOT NULL,
  video_size         INTEGER NOT NULL,
  video_mtime_utc    TEXT NOT NULL,      -- ISO 8601 UTC
  subtitle_path      TEXT NOT NULL,
  status             TEXT NOT NULL,      -- 'done' | 'check' | 'failed'
  message            TEXT,
  audio_language     TEXT,
  subsync_version    TEXT,
  seconds            REAL,
  updated_utc        TEXT NOT NULL
);
CREATE INDEX ix_synced_status ON synced_items(status);
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);  -- small settings and the diagnostics row
-- v2: backup journal for startup recovery (D23)
CREATE TABLE in_flight (item_id TEXT PRIMARY KEY, subtitle_path TEXT NOT NULL, backup_path TEXT NOT NULL, started_utc TEXT NOT NULL);
```
Migrations are numbered steps in `SqliteStateStore`; a database with a newer version than the code knows is refused, not touched. The file is `state.db` in the plugin's data folder.
`done` is valid while `video_path`, `video_size` and `video_mtime_utc` still match the file. Lookups are batched per page of IDs.

## Libraries and queries
Never load a whole library. Browse by parent with paging; search with a search term and paging. Every query passes the signed-in `User` so library access applies. APIs (spike S3):
- TV libraries: `IUserViewManager.GetUserViews` filtered to `tvshows`.
- Children and search: `ILibraryManager.GetItemsResult(InternalItemsQuery)` with `User`, `ParentId`, `IncludeItemTypes`, `StartIndex`, `Limit`, `SearchTerm`, `IsVirtualItem = false`.
- One season's episodes: `Season.GetEpisodes(user, dtoOptions, false)`.
- Streams: `IMediaSourceManager.GetMediaStreams` (one query per item; compute only for the visible page).
- The fingerprint reads size and mtime from the file system, not from `BaseItem`.
`VirtualFolderInfo.ItemId` is still filled in on 12.4 but isn't needed.

## Packaging
`net10.0`, `Jellyfin.Controller` / `Jellyfin.Model` / `Jellyfin.Common` 12.4.0 with `ExcludeAssets=runtime`, four-part version, `meta.json`, zip plus MD5 checksum, `manifest.json` with `targetAbi` `12.0.0.0` attached to each GitHub release (`build/release.ps1`, D39). Plugin name, description and targetAbi live in `build/plugin-info.ps1`, shared by the zip's `meta.json` and the manifest. The zip contains only our own DLL: no Jellyfin, Newtonsoft or SQLite assemblies, because every DLL in the plugin folder is loaded into the plugin's own context and would shadow the server's copies (D16, D17). Declare the File Transformation dependency in the README and plugin description.

## Web client hook
At startup the plugin registers a File Transformation for `index.html` that adds `<script plugin="SubtitleFixer" src="../SubtitleFixer/menu.js" defer></script>` before `</body>`. Registration is by reflection (spike S1, D16). The page and its assets are served by our own anonymous controller routes; all data endpoints require auth.
