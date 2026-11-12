# Per-episode job flow

## SubSync CLI facts (from its `README-CLI.md` and `app/cli.py`, v1.2.0 and v1.3.0)
- **Which version:** `CliWorker` asks `subsync-cli version` (prints `SubSync 1.3.0`; since 1.3.0 the exe answers without starting Python). `SubSyncVersion` caches the answer per CLI path, size and modification time, so an upgrade in place is noticed; a failed read isn't cached and counts as 1.2.0. 1.2.0 is still supported.
- **Call, 1.3.0 and later:** `subsync-cli sync --pair VIDEO SUBTITLE --output SUBTITLE --overwrite --report FILE.json --progress` (add `--keep-layout`, `--cpu` per settings). `--output` (exactly one `--pair`) writes the result to that file and only that file, here the input subtitle itself, so every name (`.en`, `.eng`, `.en.hi`, `.eng.hi`) is re-timed in place; `--suffix` is unused. Without `--overwrite` an existing `--output` gives exit 6 before any work, so we always pass it.
- **Call, 1.2.0 (or unknown version):** `subsync-cli sync --pair VIDEO SUBTITLE --overwrite --suffix en|eng --report FILE.json`. There is no output-path option: it writes `<video name>.<suffix>.srt` (UTF-8) next to the video.
- Result = JSON report and exit code. Layout confirmed (S5): `{ subsync_version, finished, exit_code, seconds, summary, speech_recognition, items: [ { video, subtitle, status, output, reason, audio_language, seconds, lines_matched, …, timings } ] }`. `status`: `synced|no_match|failed|cancelled|skipped`. New in 1.3.0: top-level `speech_recognition` `{ device: gpu|cpu, name, model_load_seconds }` (null when nothing was listened to, for example a cached transcript), and per synced item `timings` `{ from_cache, read_audio, listen, match }` (`read_audio` and `listen` null when `from_cache`). `no_match` items carry neither `seconds` nor `timings`. `SubSyncReport.Parse` still also accepts older guessed layouts (fields at the top level, a top-level array, `results`/`pairs`/`files`), and reads a malformed new field as absent. Exit 7 and 10 override the report; otherwise the report's `status` decides, and `synced` with a non-zero exit counts as failed.
- **Progress, 1.3.0 and later (`--progress`):** stdout lines `PROGRESS {json}`, flushed at once, between the human-readable lines. `stage` events (`queued`, `reading_audio`, `listening`, `matching`, rarely `waiting_for_file`; `percent` 0–100 within the stage or null; during listening it's the share of the soundtrack heard, about every 15 s of audio), `device` (`gpu`/`cpu` and card name; again if a GPU problem switches to the CPU) and `item_done`. A cached transcript goes from `queued` straight to `matching`. `SubSyncProgress` ignores bad JSON, unknown events and unknown fields, and maps unknown stages to `working`; no line can fail a run.
- **Progress, 1.2.0:** with redirected output it prints only result lines, and the stage lines (`<timestamp> [1/1] <video>: Listening…`) go only to `logs\cli.log`, which `LogTail` follows during the run (D40). 1.3.0 adds `cli.log` lines (`speech model ready after 7.1 s (worker 6.2 s)`, `[1/1] <video>: read audio 9.8 s, listen 58.1 s, match 4.2 s, total 72.9 s`, and `serve: …` lines from `serve`); none of them reads as a stage. The report is written at the end.
- Exit codes: 0 all synced; 3 some failed; 4 none synced; 5 nothing to pair; 6 bad command line; 7 setup/repair impossible or another SubSync using the folder; 8 (`check`) incomplete install; 9 Ctrl+C; 10 unexpected error.
- Cancel and timeout kill the process tree (`subsync-cli.exe` plus its `python.exe` children), verified live. Jellyfin dying does **not** end SubSync by itself, so each run is put in a kill-on-close Job Object, and startup kills a recorded leftover run before recovery (D29).
- **Hearing-impaired files (D27):** from 1.3.0 `--output` re-times `.en.hi.srt`/`.eng.hi.srt` in place like any other name, and a plain `.en.srt` next to the video is left alone. With 1.2.0, `--suffix` takes only `en`/`eng` (S5), so the CLI runs with the plain tag, writes `<video>.en.srt`/`.eng.srt`, and `CliWorker` moves that over the `.hi` file. It refuses if the plain file already exists, and removes a stray result on any failure.
- With `--overwrite`, an existing result file held open elsewhere makes SubSync wait up to 5 minutes.
- Re-running an episode is nearly instant (transcript cache keyed by path, size, mtime, audio track). Such runs don't teach the time estimate (D40).
- 1.3.0 reads the first item's audio while the speech model loads, and loads the line-matching model at the same time, which shortens every run with no change here. Start-up (self-check, Python, model loading) is still paid once per episode, because the plugin starts one `sync` per episode (D5). `subsync-cli serve` (one process for many items) is not used yet.

## Naming
Input subtitle = the matching sidecar (`<videoBase>.en.srt` or `.eng.srt`). The CLI writes its result to the same path (`--suffix` = same tag). The original is always overwritten, so the backup is essential.

## Steps
1. Re-validate eligibility (state may have changed since queuing). No longer eligible: already synced or no English audio -> `skipped`, untouched, no record; video gone -> `failed` ("file missing: video"), no record; subtitle gone -> `failed` ("file missing: subtitle"). The quoted texts are what the state record stores; the queue shows the user a plain sentence instead ("The video file is missing or can't be read.", "The English subtitle next to the video is gone.").
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
The bar counts finished items plus the running item's share (D40). That share comes from SubSync's own listening percentage with 1.3.0, and otherwise from elapsed time over the server's estimate. ETA comes from the average `seconds` of finished items.
