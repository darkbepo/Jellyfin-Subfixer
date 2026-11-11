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
