# Milestone 0: spikes

Goal: replace assumptions with verified facts before building. One session, needs the .NET 10 SDK, internet, a Jellyfin 12 test server (File Transformation installed), SubSync installed, and one test season of episodes with `.en.srt` sidecars. Record results under each spike's **Findings**. If a result breaks a design assumption, fix the docs and tell the owner before M1.

Reference implementations to read (check licenses before copying any code): File Transformation and Plugin Pages (IAmParadox27), Achievement Badges for Jellyfin (ZL154; avatar-menu entry on 12), Jellyfin Enhanced (n00bcodr; avatar-menu entry on 12), the official Jellyfin plugin template.

## S0 Environment
Record exact Jellyfin server version, jellyfin-web version, .NET SDK, SubSync version (`subsync-cli version`), GPU. Scaffold from the official plugin template targeting net10.0 and `Jellyfin.Controller` 12.x; confirm the plugin loads. Run `subsync-cli check`.
**Findings:**

## S1 Menu injection through File Transformation
How does a plugin register a transformation (its API or interface)? Inject a script into the served web client that adds a "Subtitle Fixer" item to the avatar menu (MUI menu, so use a MutationObserver), placed below "Profile". Pass if: appears for admin and non-admin on desktop and mobile layouts, survives navigation, is absent when the plugin is disabled, and uses the `#/` route style if it links into the SPA. Fallbacks: JavaScript Injector plugin, or a direct page URL.
**Findings:**

## S2 Plugin-served page and auth
Serve an embedded HTML page from a controller route. Read the token from jellyfin-web's stored credentials and call a plugin endpoint. Jellyfin 12 switched legacy authorization off, so use the current `Authorization: MediaBrowser ...` header and confirm `401` handling. Pass if an authenticated call returns the user's id. Fallback: Plugin Pages (`PluginInterface.RegisterPage`; GPL-3.0, extra dependency).
**Findings:**

## S3 Library queries at scale
Browse libraries -> series -> seasons -> episodes with paging; search by term with paging; respect the user's library access. Read audio languages and external subtitle streams from media streams. Time a season listing and a search on the real ~40,000-episode library. Check `VirtualFolderInfo.ItemId` behavior on 12. Pass if a season lists in well under a second and search feels live.
**Findings:**

## S4 Scanner, `.bak`, refresh
(a) Does a `X.en.srt.bak` next to the video show up in the library or as a subtitle? (b) After replacing the sidecar's content, which validation-only refresh options make Jellyfin serve the new subtitle immediately (request `/Videos/{id}/{mediaSourceId}/Subtitles/{index}/Stream.srt` and compare)? Is there any subtitle cache that needs busting? Record the exact `MetadataRefreshOptions`.
**Findings:**

## S5 SubSync CLI behavior
On one real episode: `--pair`, `--overwrite`, `--suffix`, `--report` with spaces in paths. Confirm the report fields and exit codes in `JOB-FLOW.md`, per-process startup time (model load), behavior when the result file is open elsewhere, and that killing the process tree leaves no orphan processes.
**Findings:**

## S6 Running under the Jellyfin service account
Launch the CLI from the plugin while Jellyfin runs as a Windows service (and as a tray app). Does it get the GPU (CUDA) or fall back to CPU? Does it have write access to the SubSync folder and media folders? Fallbacks: `ForceCpu`, run Jellyfin as a tray app, grant folder rights.
**Findings:**

## S7 SQLite inside the plugin
Does `Microsoft.Data.Sqlite` load in the plugin's load context (native library included)? Fallback: use the assembly Jellyfin already ships, or an append-only JSONL log plus in-memory dictionary compacted at startup (40,000 records is fine).
**Findings:**

## Exit criteria
All spikes have findings; docs corrected; owner informed of any design change.
