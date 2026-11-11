# Subtitle Fixer (Jellyfin 12 plugin)

A Jellyfin 12 plugin. Users pick seasons or episodes in a file-explorer-style page; the plugin runs SubSync's `subsync-cli.exe` on each one to re-time English subtitles, then tells Jellyfin to refresh the item. Windows only in v1. SubSync (https://github.com/darkbepo/SubSync) is a separate install and is never bundled or ported.

Workflow (worktrees, sessions file, review, shipping, commit identity) comes from the account-level instructions, not this file. This file is project context only.

## Read first
Read `SPEC.md` (scope and hard rules), then only the doc for your task:

| Task | Doc |
|---|---|
| What to build next, acceptance criteria | `docs/ROADMAP.md` |
| Jellyfin 12 / File Transformation facts (verified from 12.4 source) | `docs/M0-SPIKES.md` |
| Checks the owner runs on the real server | `docs/M0-LIVE-CHECKS.md` |
| Components, repo layout, state schema | `docs/ARCHITECTURE.md` |
| REST contract (backend and UI both build to this) | `docs/API.md` |
| Per-episode sync, backup, recovery, failure matrix | `docs/JOB-FLOW.md` |
| Web page behavior | `docs/UI.md` |
| Tests | `docs/TESTING.md` |
| Why something is the way it is | `docs/DECISIONS.md` |

## Non-negotiables
1. One series per request, enforced on the server (multiple seasons of that series are fine). Season IDs expand to episodes; series and library IDs are rejected.
2. One SubSync process at a time. Queue is global and capped (`MaxEpisodesPerRequest`, `MaxQueuedEpisodes`).
3. Only subtitles named exactly `<video>.en.srt` or `<video>.eng.srt` are touched, and only on episodes with English audio.
4. Never lose a subtitle: backup, run, verify, then delete the backup. Any failure, cancel or crash restores it. Startup recovery restores leftover `.bak` files.
5. An episode already synced (video fingerprint unchanged) is never processed again unless the video file changed.
6. Never load a whole library at once. Library is about 40,000 episodes: page everything, no per-file hashing.
7. Menu entry and page come from this plugin via the File Transformation plugin (hard dependency, installed on the target server).
8. Server-side checks for every rule above. The UI only mirrors them.

## Do not guess
Jellyfin 12 APIs, File Transformation behavior and web-client DOM are not in your training data reliably (10.11 memory is wrong in places). Verify against the real 12.x packages or source, then record the answer in `docs/M0-SPIKES.md` (findings) or `docs/DECISIONS.md`. If a verified fact contradicts a doc, fix the doc in the same change.

## Commands
Fill in after scaffolding (keep this list short):
```
dotnet build -c Release
dotnet test
```

## Conventions
- C# 13/.NET 10, nullable enabled, async everywhere with `CancellationToken`, `ILogger` for logging, no blocking calls on request threads.
- Windows-safe path handling (long paths, spaces, non-ASCII). Never build paths by string concatenation.
- New NuGet dependency: justify it in `docs/DECISIONS.md` first (plugin load context makes native dependencies risky).
- Embedded web files: vanilla JS and CSS, no build step, no external CDNs.
- Keep this file lean. Put detail in `docs/`, and update the relevant doc when you learn something.

## Layout
```
src/Jellyfin.Plugin.SubtitleFixer/   Api/ Services/ Worker/ State/ Web/
tests/Jellyfin.Plugin.SubtitleFixer.Tests/
docs/  SPEC.md  CLAUDE.md  README.md
```

## Done means
Builds clean, tests pass (including the fault-injection tests for the subtitle swap), docs updated, acceptance criteria in `docs/ROADMAP.md` for the task met.
