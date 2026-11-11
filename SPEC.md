# Subtitle Fixer: product spec

Source of truth for scope and rules. Details live in `docs/`.

## 1. Purpose
Let users re-time English subtitles on English-audio TV episodes from inside Jellyfin, using SubSync, without ever risking the existing subtitle and without repeating work on a library of ~40,000 episodes.

**Disclaimer shown on the page:** "Only fixes English subtitles on English audio."

## 2. Scope (v1)
- Jellyfin 12.x: .NET 10, `targetAbi` `12.0.0.0`, the new MUI layout with the avatar menu. Record the exact server version in `docs/M0-SPIKES.md` (owner says 12.4; 12.0 and 12.1 are confirmed to exist).
- Windows only, because SubSync is Windows only. The worker sits behind an interface so a Linux sidecar can come later.
- A "Subtitle Fixer" item in the avatar menu opens a page: file-explorer-style browser of libraries, series, seasons and episodes with live search. Seasons or individual episodes can be marked. Progress bar while syncing.
- After each episode, Jellyfin refreshes that item so the subtitle is served immediately.
- Hard dependency on the File Transformation plugin.

**Out of scope for v1** (possible later): embedded subtitle extraction, other languages, movies, Linux/macOS, parallel SubSync jobs, subtitles with any name other than `.en.srt` / `.eng.srt` and other formats (`.ass`, `.vtt`).

## 3. Hard rules
Enforced on the server; the UI mirrors them. See `docs/API.md` for error codes.
1. **One series per request.** Every submitted ID is resolved to its parent series; more than one series is rejected. Multiple seasons of the same series are allowed.
2. **Episodes only reach the queue.** Season IDs expand to episodes. Series IDs and library IDs are rejected.
3. **UI series lock.** Once anything is selected, items of other series cannot be selected until the selection is cleared (they stay visible and browsable, dimmed).
4. **Caps** (configurable): `MaxEpisodesPerRequest` default 150, `MaxQueuedEpisodes` default 300. Skipped and ineligible episodes do not count.
5. **One job at a time**; global queue.
6. **Never re-sync** an episode with a matching `done` record.
7. **Never lose a subtitle**: backup, run, verify, swap, delete backup; restore on any failure (`docs/JOB-FLOW.md`).
8. **Permissions**: `AllowedUsers` = `AdminOnly` (default) or `AllAuthenticated`. Users only see what they can access in Jellyfin.

## 4. Eligibility
An episode is eligible only if all hold:
- Item type is Episode and its video file exists.
- At least one audio stream has language `eng` or `en`.
- It has an external sidecar named exactly `<videoBase>.en.srt` or `<videoBase>.eng.srt` (video name without extension). Any other name is treated as not English and ignored. No naming heuristics. If both exist, only `.en.srt` is processed.
- No `done` record whose fingerprint still matches.

Ineligible items are shown with a reason: `no_english_audio`, `no_sidecar`, `already_synced`. Items with status `check` or `failed` stay selectable, labelled "previously attempted".

## 5. When an episode counts as "synced"
A `done` record stores video path, size and mtime (UTC). It is valid while all three still match. A replaced video makes the episode eligible again. Files are never hashed. `check` (SubSync matched too few lines and wrote nothing) is not done. Details: `docs/ARCHITECTURE.md` (state schema).

## 6. Configuration (admin settings page)
| Setting | Default | Notes |
|---|---|---|
| SubSync folder | none | Validated with `subsync-cli check`; exit code 8 = tell admin to run `subsync-cli setup` (about 3.2 GB) |
| `AllowedUsers` | `AdminOnly` | or `AllAuthenticated` |
| `MaxEpisodesPerRequest` | 150 | |
| `MaxQueuedEpisodes` | 300 | |
| `TidyUpLines` | on | Off passes `--keep-layout`. Tidy up re-cuts subtitles by sentence and speaker; words are never changed |
| `ForceCpu` | off | Passes `--cpu` |
| `EpisodeTimeoutMinutes` | 30 | Kill and restore when exceeded |

The language tag (`en` or `eng`) always matches the existing file and is not configurable. A "Test SubSync" button runs `check` and `version`.

## 7. Environment constraints
- The Jellyfin service account needs write access to the media folders and to the SubSync folder (SubSync writes its runtime, `cache\transcripts` and `logs` there). `Program Files` will not work.
- If a SubSync window is open and repairing, the CLI exits with code 7; the queue stops and tells the admin.
- Speed is about 1 to 2 min per 45-minute episode on an RTX 3050, so a season takes roughly 25 to 50 minutes. Whole-library runs are not a goal.
- Whether CUDA works when Jellyfin runs as a Windows service is unverified (spike S6).
