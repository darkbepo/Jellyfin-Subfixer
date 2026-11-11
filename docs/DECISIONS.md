# Decisions

"Owner" = stated by the project owner. "Proposed" = suggested during planning; change freely, but note it here.

| # | Decision | By | Notes |
|---|---|---|---|
| D1 | Plugin wraps SubSync's CLI; SubSync is not ported or bundled | Proposed | SubSync is Python plus Whisper and CUDA, about 6 GB |
| D2 | Windows only in v1; worker behind an interface for a later sidecar | Owner | |
| D3 | Hard dependency on File Transformation | Owner | Already installed on the target server |
| D4 | Standalone page served by the plugin; Plugin Pages is the fallback | Proposed | Avoids an extra dependency and GPL-3.0 |
| D5 | One `subsync-cli` process per episode | Proposed | Per-episode result, backup and refresh; costs a model load each time |
| D6 | Only `<video>.en.srt` / `<video>.eng.srt` count as English subtitles | Owner | Owner keeps naming tidy; other names later |
| D7 | Backup exists only during the sync and is deleted on success | Owner | |
| D8 | One series per request; multiple seasons allowed; UI selection locked to one series | Owner | |
| D9 | Caps 150 per request and 300 queued, configurable | Proposed | |
| D10 | `AllowedUsers` defaults to `AdminOnly` | Proposed | Owner to confirm |
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
| D18 | Plugin license | Open (owner) | The official template, File Transformation and Plugin Pages are GPL-3.0, and the server is GPL-2.0. The plugin compiles against GPL server packages, so GPL-3.0 is the usual choice for Jellyfin plugins. Needed before the first release |

## Deferred
Embedded subtitle extraction, other languages, movies, other names and formats (`.ass`, `.vtt`), Linux sidecar, parallel jobs.
