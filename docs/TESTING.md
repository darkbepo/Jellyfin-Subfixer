# Testing

Use fakes: a fake `ISubSyncWorker` (scriptable results), a temp-directory file system, a fake clock, and a fake library facade over the Jellyfin services.

## Unit tests
- `SelectionValidator`: multiple series rejected; season expands to episodes; series or library ID rejected; cap and queue-full behavior; skipped items not counted.
- `EligibilityService`: each reason code; `.en.srt` vs `.eng.srt` vs other names; both present uses `.en.srt`; English audio detection (`en` and `eng`).
- State: `done` valid while path, size, mtime match; any change invalidates; `check` and `failed` stay selectable.
- `CliWorker`: report parsing for each status; exit-code mapping; timeout; kill tree.

## Fault injection on the subtitle swap
After each case the original subtitle must be byte-identical to before (except the success case) and no `.bak` may remain.
| Case | Expected |
|---|---|
| CLI fails | restored |
| CLI reports `no_match` | restored, `check` |
| CLI writes empty or invalid file | restored, `failed` |
| Cancel mid-run | restored, process tree gone |
| Timeout | restored |
| Backup copy fails | CLI never started |
| Process killed (simulate crash) leaving `.bak` | startup recovery restores and deletes it |
| Success | new content, `.bak` deleted, record `done`, refresh requested |

## Real SubSync tests
`RealSubSyncTests` run the real `subsync-cli.exe` through `CliWorker`: a normal sync, the HI move, the HI refusal (1.2.0 only), the HI in-place sync next to a plain file (1.3.0 and later only), `no_match`, and cancel with no processes left. They check the installed version themselves.

`ServeSessionTests`, `CliWorkerServeTests` and `ServeQueueTests` (any OS) cover `serve` (D45) with `FakeServe`, a scripted stand-in that handles requests one after another like SubSync. Queue cases: a season through one process with the next item sent while one listens (each backed up before it was sent), a refused request, a crash with two items in it (restored, counted once: three crashes, six items), cancelling one of two items (the other requeued and run by a new process), cancelling all, Jellyfin stopping, a timeout (the other requeued), waiting behind another item not counting toward the time limit, settings changing mid-queue (old process closed through stdin, new one with the new options), exit 7 at start and mid-run, the fallback to one `sync` per item, and items settled before SubSync. Each checks the original subtitle is byte-identical, no `.bak` remains and the journal is empty. `ServeProcessTests` run the real `ServeProcess` against a `/bin/sh` stand-in (Unix only; skipped on Windows CI).

`SubSyncUpdateTests.cs` (any OS) covers the SubSync updater (D46): reading GitHub's release JSON (asset, checksum, tags, pre-releases and drafts, the release link), the installer against an in-memory zip whose "exes" are text files that state their version (both exes replaced, a command-line-only folder, exes one folder down, checksum mismatch, no checksum, no exe in the zip, a path in the zip pointing outside, the wrong version before and after the swap with rollback, SubSync running from the folder, a failed setup, an interrupted swap tidied up), and the service (offered only when newer, 6-hour cache and Check again, a failed check retried after 10 minutes, the queue closed until setup ends and reopened after a failure, a busy queue refusing). The settings page's update section was checked in headless Chromium with a stand-in `ApiClient`.

`CliWorkerPlaybackTests` (any OS) run `CliWorker` against recorded SubSync output through a scripted process runner: 1.3.0 in place with `--output` and `--progress`, the 1.2.0 HI move and refusal, a GPU-to-CPU switch, a cached run, a cached `no_match`, and junk lines. `SubSyncVersionTests`, `SubSyncProgressTests`, `SubSyncReport13Tests`, `RunTimeEstimate13Tests` and `SubSyncStage13Tests` cover version detection and caching, the progress lines, both report versions, the estimate (cached runs, the 1% / 10 s rule, 1.3.0 learning, the listening percent) and the new `cli.log` lines. They do nothing unless `SUBFIXER_SUBSYNC_FOLDER` and `SUBFIXER_TEST_CLIP` are set (CI doesn't set them). `KillOnCloseJob` and `RunningWorkerFile` have their own process tests (Windows only).

## API tests
Error codes in `API.md`; permissions (`AdminOnly`, `AllAuthenticated`); users cannot see inaccessible libraries; Status visible to allowed users.

## UI tests
Run in mock mode: selection model, series lock (dimmed and disabled, unlocks when empty), season checkbox states, preview-driven Start, progress and summary rendering, phone width.

## Manual checklist (real Jellyfin 12 server)
- [ ] Menu item appears for admin and non-admin (per `AllowedUsers`), absent when plugin disabled.
- [ ] Browse and search feel live on the ~40,000-episode library.
- [ ] Series lock works; multi-season selection works.
- [ ] Sync a season: progress, ETA, subtitles served right after each episode.
- [ ] Second run skips all synced episodes. Replacing a video makes it eligible again.
- [ ] Cancel mid-run restores the subtitle.
- [ ] Kill Jellyfin mid-run, restart: subtitle restored.
- [ ] SubSync window open and repairing: queue stops with the right message.
