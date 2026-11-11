# Per-episode job flow

## SubSync CLI facts (from its `README-CLI.md`)
- Call: `subsync-cli sync --pair VIDEO SUBTITLE --overwrite --suffix en|eng --report FILE.json` (add `--keep-layout`, `--cpu` per settings).
- No output-path option. It writes `<video name>.<suffix>.srt` (UTF-8) next to the video.
- Result = JSON report (`status`: `synced|no_match|failed|cancelled|skipped`, plus `reason`, `audio_language`, `seconds`, `subsync_version`) and exit code.
- With redirected output it prints only result lines; the report is written at the end. No live progress.
- Exit codes: 0 all synced; 3 some failed; 4 none synced; 5 nothing to pair; 6 bad command line; 7 setup/repair impossible or another SubSync using the folder; 8 (`check`) incomplete install; 9 Ctrl+C; 10 unexpected error.
- Killing `subsync-cli.exe` makes Windows end all child processes (Job Object): cancel = kill the process tree.
- With `--overwrite`, an existing result file held open elsewhere makes SubSync wait up to 5 minutes.
- Re-running an episode is nearly instant (transcript cache keyed by path, size, mtime, audio track).

## Naming
Input subtitle = the matching sidecar (`<videoBase>.en.srt` or `.eng.srt`). The CLI writes its result to the same path (`--suffix` = same tag). The original is always overwritten, so the backup is essential.

## Steps
1. Re-validate eligibility and the series rule (state may have changed since queuing).
2. **Backup**: copy the subtitle to `<subtitle>.bak` next to it. Jellyfin ignores `.bak` because it isn't a subtitle or media extension (spike S4). If the copy fails, do not run the CLI; record `failed`.
3. Run the CLI for this pair, report to a temp file. Enforce `EpisodeTimeoutMinutes`.
4. Read report and exit code. `synced` -> verify output (exists, non-empty, parses as SRT, at least one cue).
5. **Success**: delete the `.bak`, write the `done` record (store `subsync_version`), then refresh.
6. **Refresh**: per episode, not at the end. Use `IProviderManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(fileSystem)), RefreshPriority.High)`, the same call Jellyfin makes after a subtitle upload (default mode, no replace flags). External `.srt` files are read from disk on every request, so the new subtitle is served even before this runs; the refresh is insurance (spike S4).
7. **Anything else**: kill the process tree on cancel or timeout, restore the subtitle from the `.bak` (always, even if the CLI wrote nothing), delete the `.bak`, record per the matrix.
8. **Startup recovery**: scan for `.bak` files. For each: restore it over the subtitle, delete the `.bak`, mark the episode not synced.

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
| Exit 7 | restored | none (not attempted) | stop, show "close the SubSync window / wait for setup" |
| Exit 10 or process crash | restored | `failed` | continue; stop after 3 consecutive such failures (proposed) |
| Timeout | restored | `failed` ("timeout") | continue |
| User cancel | restored | none | stop |
| Jellyfin or plugin killed mid-run | `.bak` on disk | none | recovery on next start restores it |

## Progress and ETA
No per-episode percentage from the CLI. Progress is episodes done / total, with ETA from the average `seconds` of finished episodes. (Optional later SubSync change: a progress file with JSON lines.)
