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

## Deferred
Embedded subtitle extraction, other languages, movies, other names and formats (`.ass`, `.vtt`), Linux sidecar, parallel jobs.
