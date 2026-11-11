# Decisions

"Owner" = stated by the project owner. "Proposed" = suggested during planning; change freely, but note it here.

| # | Decision | By | Notes |
|---|---|---|---|
| D1 | Plugin wraps SubSync's CLI; SubSync is not ported or bundled | Proposed | SubSync is Python plus Whisper and CUDA, about 6 GB |
| D2 | Windows only in v1; worker behind an interface for a later sidecar | Owner | |
| D3 | Hard dependency on File Transformation | Owner | Already installed on the target server |
| D4 | Standalone page served by the plugin; Plugin Pages is the fallback | Proposed | Avoids an extra dependency and GPL-3.0 |
| D5 | One `subsync-cli` process per episode | Proposed | Per-episode result, backup and refresh; costs a model load each time |
| D6 | Only `<video>.en.srt`, `.eng.srt`, `.en.hi.srt` and `.eng.hi.srt` count as English subtitles; one per item, in that order | Owner | HI added 2026-11-11 (D27). Owner keeps naming tidy; other names later |
| D7 | Backup exists only during the sync and is deleted on success | Owner | |
| D8 | One series per request; multiple seasons allowed; UI selection locked to one series | Owner | |
| D9 | Caps 150 per request and 300 queued, configurable | Proposed | |
| D10 | `AllowedUsers` defaults to `AllAuthenticated` (everyone signed in), but non-admins are kept out until SubSync is configured (folder set and `subsync-cli.exe` found) | Owner (2026-11-11) | A fresh install stays admin-only until set up. `GET /Me` returns `reason: not_configured`; the menu entry is hidden for them meanwhile. `AdminOnly` remains a setting |
| D11 | In-memory queue, SQLite state | Proposed | Queue is dropped on restart |
| D12 | Never hash files; fingerprint = path, size, mtime | Proposed | 40,000 episodes |
| D13 | `TidyUpLines` default on (SubSync default) | Proposed | Words are never changed |
| D14 | Stop the queue after 3 consecutive crashes | Proposed | |
| D15 | Progress is episode-level; optional SubSync progress file later | Proposed | CLI has no live progress |
| D16 | No Newtonsoft.Json dependency; build File Transformation's `JObject` payload by reflection | Proposed | `RegisterTransformation` takes the server's Newtonsoft `JObject` (File Transformation doesn't bundle Newtonsoft). Shipping our own copy would make the types differ. Take the type from the method's parameter and call `Parse` (M0 S1) |
| D17 | `Microsoft.Data.Sqlite` as a compile-only reference (`ExcludeAssets=runtime`), version not above the server's (12.4 ships 10.0.14) | Proposed | The server already loads it and its native library; a plugin falling back to the default load context gets that copy. Nothing native in our zip. JSONL store stays the fallback behind `StateStore` (M0 S7) |
| D19 | One shared queue, visible to every allowed user; each user cancels only their own items, admins cancel all; inaccessible items are shown without their name | Owner (queue visibility), Proposed (cancel scope, hiding) | A second Start appends to the queue and never starts a second SubSync |
| D20 | Bazarr API key lives in `PluginConfiguration`; the settings page never shows it back (empty field keeps the saved key) and no endpoint of ours returns it | Proposed | Jellyfin 12.4's `PluginsController` is `[Authorize(Policy = Policies.RequiresElevation)]` at class level, so only admins can read plugin configuration. The key goes to Bazarr in the `X-API-KEY` header, never in a URL, so it stays out of logs |
| D21 | Bazarr path mappings are one text setting (`jellyfin => bazarr` per line), not a list type | Proposed | Round-trips through the stock config page and XML config with no custom serializer; bad lines are reported by Test Bazarr and skipped |
| D22 | Settings page has its own scoped design (cards, switches, status pills) instead of stock `emby-*` form controls; icons use the Material Icons font jellyfin-web already ships, by ligature (`<span class="material-icons">save</span>`) | Owner (modern look), Proposed (how) | jellyfin-web 12.4 depends on `material-design-icons-iconfont` 6.7.0 and uses `class="material-icons"` itself, so no CDN or bundled font. Colors mix `currentColor`, so Jellyfin's light themes stay readable. `WebTests` checks that every setting has a field and that the page loads nothing external |
| D23 | Startup recovery reads a backup journal (`in_flight` table, written before each `.bak` is created) instead of scanning for `.bak` files | Proposed | A scan would walk the whole 40,000-episode library (rule 6). No journal, no backup, no run. An existing `.bak` is never overwritten |
| D24 | When the queue stops itself (exit 7, 3 crashes), remaining queued items are cancelled with the reason, not paused | Proposed | Simple to reason about; users press Start again, and synced episodes are skipped anyway. `stopReason` stays visible until the next Start |
| D25 | Finished queue items stay listed until their owner clears them or for 24 hours; an episode is never queued twice | Proposed | Lets everyone see recent results after the queue goes idle |
| D29 | Every SubSync run is put in a kill-on-close Windows Job Object, and its pid is recorded so startup kills a leftover run before recovery | Proposed (from a live finding) | Killing Jellyfin left `subsync-cli.exe` and its `python.exe` children running. An orphan finishing after startup recovery would overwrite the restored subtitle with its backup already deleted. Plain P/Invoke to kernel32, no new dependency |
| D18 | License: GPL-3.0 | Owner (2026-11-11: "whatever is convenient for a free to use open source plugin on GitHub") | `LICENSE` in the repo root. Matches Jellyfin's official plugin template and most Jellyfin plugins; the plugin compiles against GPL server packages |
| D26 | Movies are supported, per `MOVIES-PLAN.md` | Owner (2026-11-11) | Built 2026-11-11 with the plan's proposed rules: no mixing movies and episodes in one request (`mixed_selection`), no series lock for movies. Owner confirmed both rules on 2026-11-11 |
| D27 | Hearing-impaired sidecars (`.en.hi.srt`, `.eng.hi.srt`) are processed | Owner (2026-11-11) | Lower priority than plain English when several exist. `--suffix en.hi` / `eng.hi` keeps the name; whether SubSync accepts a dotted suffix is unverified (S5) |
| D28 | Bazarr: everyone who may use Subtitle Fixer can search and download, but Find subtitles appears only on episodes and movies with no English sidecar; everywhere else the page only re-times | Owner (2026-11-11) | No separate Bazarr permission. The server rejects Bazarr calls for items that already have a sidecar (`409 has_subtitle`) |
| D30 | Bazarr manual search gets a 90 s timeout; other Bazarr calls keep 30 s | Proposed | Search asks every enabled provider in turn: 4.9 to 5.6 s for three providers on the test Bazarr 1.6.2, and accounts like opensubtitles can be much slower. The page shows a spinner meanwhile |
| D31 | Find subtitles shows only English, non-forced results, and is refused where the item has no English audio | Proposed | Bazarr saves forced subtitles as `.en.forced.srt`, which the fixer never re-times (D6), and an English subtitle can't be re-timed to non-English audio. The dialog counts the hidden results |

## Deferred
Embedded subtitle extraction, other languages, movies, other names and formats (`.ass`, `.vtt`), Linux sidecar, parallel jobs.
