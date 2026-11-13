# Subtitle Fixer: product spec

Source of truth for scope and rules. Details live in `docs/`.

## 1. Purpose
Let users re-time English and Danish subtitles on English-audio TV episodes and movies from inside Jellyfin, using SubSync, without ever risking the existing subtitle and without repeating work on a library of ~40,000 episodes.

**Disclaimer shown on the page:** "Only fixes English and Danish subtitles on English audio."

## 2. Scope (v1)
- Jellyfin 12.x: .NET 10, `targetAbi` `12.0.0.0`, the new MUI layout with the avatar menu. Record the exact server version in `docs/M0-SPIKES.md` (owner says 12.4; 12.0 and 12.1 are confirmed to exist).
- Windows only, because SubSync is Windows only. The worker sits behind an interface so a Linux sidecar can come later.
- A "Subtitle Fixer" item in the avatar menu opens a page: file-explorer-style browser of libraries, series, seasons and episodes with live search. Seasons or individual episodes can be marked. Progress bar while syncing.
- After each episode, Jellyfin refreshes that item so the subtitle is served immediately.
- Hard dependency on the File Transformation plugin.

**Movies** (`docs/MOVIES-PLAN.md`, D26): movie libraries, movie search and movie sync, with the same eligibility, backup and queue rules. A request is either episodes of one series or movies, never both.

**Danish** (0.7.0.0, D50): Danish subtitles (`.da.srt`, `.dan.srt`, `.da.hi.srt`, `.dan.hi.srt`) are re-timed too, on English audio, with SubSync 1.5.0 or later. A Danish subtitle is timed by the English one next to the same video when there is one. Each subtitle language of an item is synced, recorded and backed up on its own.

**Out of scope for v1** (possible later): embedded subtitle extraction, languages other than English and Danish, Linux/macOS, parallel SubSync jobs, subtitles with any name other than the `.en` / `.eng` / `.da` / `.dan` names above (with `.hi`), and other formats (`.ass`, `.vtt`).

## 3. Hard rules
Enforced on the server; the UI mirrors them. See `docs/API.md` for error codes.
1. **One series per request.** Every submitted ID is resolved to its parent series; more than one series is rejected. Multiple seasons of the same series are allowed. Movies have no series: any movies may go together, but movies and episodes are never mixed (`mixed_selection`).
2. **Only episodes and movies reach the queue.** Season IDs expand to episodes. Series IDs and library IDs are rejected.
3. **UI series lock.** Once anything is selected, items of other series cannot be selected until the selection is cleared (they stay visible and browsable, dimmed).
4. **Caps** (configurable): `MaxEpisodesPerRequest` default 150, `MaxQueuedEpisodes` default 300. Skipped and ineligible episodes do not count.
5. **One SubSync at a time**; global queue. With SubSync 1.3.0 that one process may hold the next item too, sent ahead while the current one listens (D45).
6. **Never re-sync** an episode with a matching `done` record, except when a user chooses **Sync again** on that one episode or movie and language (D51).
7. **Never lose a subtitle**: backup, run, verify, swap, delete backup; restore on any failure (`docs/JOB-FLOW.md`).
8. **Permissions**: `AllowedUsers` = `AllAuthenticated` (default) or `AdminOnly`. Admins are always allowed; everyone else only once SubSync is configured, so a fresh install is admin-only until set up (D10). Users only see what they can access in Jellyfin, within the libraries chosen in `AllowedLibraryIds` (D43). Bazarr search and download follow the same rule (D28).

## 4. Eligibility
Each subtitle language of an item (English, Danish; D50) is eligible only if all hold:
- Item type is Episode or Movie and its video file exists.
- At least one audio stream has language `eng` or `en`. When no stream is tagged (`und` or empty), a **Check audio** result counts instead: SubSync's `detect` heard English with at least 50% confidence in this same video (D49). A check never overrides a language tag. Danish subtitles need English audio too.
- It has an external sidecar named exactly `<videoBase>.en.srt`, `.eng.srt`, `.en.hi.srt` or `.eng.hi.srt` (Danish: `.da.srt`, `.dan.srt`, `.da.hi.srt`, `.dan.hi.srt`); video name without extension; `.hi` = hearing-impaired. Any other name is ignored. No naming heuristics. One subtitle per item and language: the first that exists in that order is processed (D6, D27).
- No `done` record of that language whose fingerprint still matches (unless Sync again, D51).
- Danish only with SubSync 1.5.0 or later; with an older one a Danish item fails with "update SubSync" and nothing is touched.

Starting a selection queues every eligible language of every selected item, English first. A Danish subtitle that isn't there is simply not synced; it isn't counted as skipped. Ineligible items are shown with a reason: `no_video_file`, `already_synced`, `no_english_audio`, `no_sidecar`. Items with status `check` or `failed` stay selectable, labelled "previously attempted".

## 5. When an episode counts as "synced"
A `done` record stores video path, size and mtime (UTC). It is valid while all three still match. A replaced video makes the episode eligible again. Files are never hashed. `check` (SubSync matched too few lines and wrote nothing) is not done. Details: `docs/ARCHITECTURE.md` (state schema).

## 6. Configuration (admin settings page)
| Setting | Default | Notes |
|---|---|---|
| SubSync folder | none | Validated with `subsync-cli check`; exit code 8 = tell admin to run `subsync-cli setup` (about 3.2 GB) |
| `AllowedUsers` | `AllAuthenticated` | or `AdminOnly`. Non-admins wait until SubSync is configured (D10) |
| `AllowedLibraryIds` | empty (every library) | TV and movie libraries people may pick from, for everyone including admins; enforced on every library read (D43) |
| `MaxEpisodesPerRequest` | 150 | |
| `MaxQueuedEpisodes` | 300 | |
| `TidyUpLines` | on | Off passes `--keep-layout`. Tidy up re-cuts subtitles by sentence and speaker; words are never changed |
| `ForceCpu` | off | Passes `--cpu` |
| `EpisodeTimeoutMinutes` | 30 | Minimum time limit per item. The effective limit is the larger of this and twice the video's length plus 10 minutes (D34); kill and restore when exceeded |
| `BazarrEnabled` | off | Bazarr subtitle search (`docs/BAZARR-PLAN.md`) |
| `BazarrUrl` | none | Bazarr base address as the server reaches it, including any base path |
| `BazarrApiKey` | none | Write-only on the settings page; never returned by our endpoints (D20) |
| `BazarrPathMappings` | none | One `jellyfin prefix => bazarr prefix` per line; whole folders, case-insensitive, longest prefix wins |

The language tag (`en`, `eng`, `en.hi` or `eng.hi`) always matches the existing file and is not configurable. A "Test SubSync" button runs `check` and `version`. A "Test Bazarr" button calls Bazarr's status endpoint with the form's unsaved values and shows the version.

## 7. Environment constraints
- The Jellyfin service account needs write access to the media folders and to the SubSync folder (SubSync writes its runtime, `cache\transcripts` and `logs` there). `Program Files` will not work.
- If a SubSync window is open and repairing, the CLI exits with code 7; the queue stops and tells the admin.
- Speed is about 1 to 2 min per 45-minute episode on an RTX 3050, so a season takes roughly 25 to 50 minutes. Whole-library runs are not a goal.
- Whether CUDA works when Jellyfin runs as a Windows service is unverified (spike S6).
