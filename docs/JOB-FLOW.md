# Per-episode job flow

## SubSync CLI facts (from its `README-CLI.md`)
- Call: `subsync-cli sync --pair VIDEO SUBTITLE --overwrite --suffix en|eng --report FILE.json` (add `--keep-layout`, `--cpu` per settings).
- No output-path option. It writes `<video name>.<suffix>.srt` (UTF-8) next to the video.
- Result = JSON report (`status`: `synced|no_match|failed|cancelled|skipped`, plus `reason`, `audio_language`, `seconds`, `subsync_version`) and exit code. The exact report layout is unverified (S5); `SubSyncReport.Parse` accepts the fields at the top level, in a top-level array, or in the first entry of `results`/`pairs`/`items`/`files`. Exit 7 and 10 override the report; otherwise the report's `status` decides, and `synced` with a non-zero exit counts as failed.
- With redirected output it prints only result lines; the report is written at the end. No live progress.
- Exit codes: 0 all synced; 3 some failed; 4 none synced; 5 nothing to pair; 6 bad command line; 7 setup/repair impossible or another SubSync using the folder; 8 (`check`) incomplete install; 9 Ctrl+C; 10 unexpected error.
- Cancel and timeout kill the process tree (`subsync-cli.exe` plus its `python.exe` children), verified live. Jellyfin dying does **not** end SubSync by itself, so each run is put in a kill-on-close Job Object, and startup kills a recorded leftover run before recovery (D29).
- `--suffix` takes only `en`/`eng` (S5). For `.en.hi.srt`/`.eng.hi.srt` the CLI runs with the plain tag, writes `<video>.en.srt`/`.eng.srt`, and `CliWorker` moves that over the `.hi` file. It refuses if the plain file already exists, and removes a stray result on any failure.
- With `--overwrite`, an existing result file held open elsewhere makes SubSync wait up to 5 minutes.
- Re-running an episode is nearly instant (transcript cache keyed by path, size, mtime, audio track).

## Naming
Input subtitle = the matching sidecar (`<videoBase>.en.srt` or `.eng.srt`). The CLI writes its result to the same path (`--suffix` = same tag). The original is always overwritten, so the backup is essential.

## Steps
1. Re-validate eligibility (state may have changed since queuing). No longer eligible: already synced or no English audio -> `skipped`, untouched, no record; video gone -> `failed` ("file missing: video"), no record; subtitle gone -> `failed` ("file missing: subtitle").
2. **Backup**: first write a journal row (`in_flight` table: item, subtitle path, backup path), then copy the subtitle to `<subtitle>.bak` next to it. Jellyfin ignores `.bak` because it isn't a subtitle or media extension (spike S4). If the journal write or the copy fails, or a `.bak` already exists (it may be the only good copy, so it is never overwritten), do not run the CLI; record `failed` ("cannot create backup"). The journal row is removed only after the `.bak` is gone, so a crash at any moment leaves a row recovery can act on (D23).
3. Run the CLI for this pair, report to a temp file. Enforce the time limit: the larger of `EpisodeTimeoutMinutes` and twice the video's runtime plus 10 minutes (D34).
4. Read report and exit code. `synced` -> verify output (exists, non-empty, parses as SRT, at least one cue).
5. **Success**: delete the `.bak`, write the `done` record (store `subsync_version`), then refresh.
6. **Refresh**: per episode, not at the end. Use `IProviderManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(fileSystem)), RefreshPriority.High)`, the same call Jellyfin makes after a subtitle upload (default mode, no replace flags). External `.srt` files are read from disk on every request, so the new subtitle is served even before this runs; the refresh is insurance (spike S4).
7. **Anything else**: kill the process tree on cancel or timeout and wait for it to exit, restore the subtitle from the `.bak` (always, even if the CLI wrote nothing; retried up to 5 times a second apart in case a dying process still holds the file), delete the `.bak`, remove the journal row, record per the matrix. Unexpected exceptions take the same path.
8. **Startup recovery**: before the queue runs, read the journal (not a library scan, which rule 6 forbids). For each row: restore the `.bak` over the subtitle, delete the `.bak`, delete the episode's record (not synced), remove the row. A missing `.bak` just removes the row. If the restore fails, the row and the `.bak` stay for the next start.

The backup exists only during the sync and is never kept afterwards.

## Failure matrix
| Situation | Subtitle after | Record | Queue |
|---|---|---|---|
| Synced and verified | new file | `done` | continue |
| `no_match` (wrong subtitle for this video) | restored | `check` | continue |
| CLI `failed` | restored | `failed` + reason | continue |
| Output fails verification | restored | `failed` ("invalid output") | continue |
| Cannot create backup | untouched | `failed` ("cannot create backup") | continue |
| Video or subtitle missing at run time | untouched | `failed` ("file missing") | continue |
| Exit 7 | restored | none (not attempted) | stop: remaining queued items `cancelled`, `stopReason` "close the SubSync window / wait for setup" |
| Exit 10, process crash, no usable report, or unexpected exception | restored | `failed` | continue; stop the same way after 3 consecutive such failures (D14) |
| Already synced or no English audio when its turn comes | untouched | none | `skipped`, continue |
| Timeout | restored | `failed` ("timeout") | continue |
| User cancel | restored | none | stop |
| Jellyfin or plugin killed mid-run | `.bak` on disk | none | recovery on next start restores it |

## Progress and ETA
No per-episode percentage from the CLI. Progress is episodes done / total, with ETA from the average `seconds` of finished episodes. (Optional later SubSync change: a progress file with JSON lines.)
