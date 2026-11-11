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
| D18 | Plugin license | Open (owner) | The official template, File Transformation and Plugin Pages are GPL-3.0, and the server is GPL-2.0. The plugin compiles against GPL server packages, so GPL-3.0 is the usual choice for Jellyfin plugins. Needed before the first release |

## Deferred
Embedded subtitle extraction, other languages, movies, other names and formats (`.ass`, `.vtt`), Linux sidecar, parallel jobs.
