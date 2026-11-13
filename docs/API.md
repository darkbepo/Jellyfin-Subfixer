# REST API

Base path `/SubtitleFixer`. All data endpoints require an authenticated user (`[Authorize]`) and the `AllowedUsers` policy. Settings and test endpoints require an administrator (`[Authorize(Policy = Policies.RequiresElevation)]`). Authenticated calls send only `Authorization: MediaBrowser Token="<token>"`: Jellyfin overwrites the device's stored name and version whenever a request sends a different `Device` or `Version`, so sending ours would rename the user's browser session. Responses are camelCase (Jellyfin's own API is PascalCase). Legacy headers (`X-Emby-Token`, `X-Emby-Authorization`, `api_key`) are off by default on 12 (spike S2). A missing or bad token gets `401` from Jellyfin before our code runs. Errors are `{ "error": "<code>", "message": "<human text>" }`.

## Static (anonymous)
`GET /Page` (the page HTML), `GET /Page/app.js`, `GET /Page/styles.css`, `GET /menu.js` (avatar-menu script). These contain no data.

## GET /Me
`{ "userId":"...", "name":"...", "isAdmin":false, "allowed":true, "reason":null, "message":null }`. The page calls this first: `401` means show the sign-in link; `allowed:false` means show `message` (no `403`, so the page can explain). `reason` is `not_configured` (non-admins wait until SubSync is configured, D10) or `admins_only` (`AllowedUsers = AdminOnly`). The avatar-menu entry is shown only when `allowed` is true. Data endpoints return `403 forbidden` with the same message.

## Libraries setting (D43)
`AllowedLibraryIds` limits every endpoint that reads the library: Browse lists only the chosen libraries, Search only searches them, and an item anywhere else is `not_found` for Browse, Preview, Jobs, Retry and Bazarr, exactly as if the user had no access. Empty means every library. It applies to administrators too. Queue and log entries for items outside the chosen libraries show as "Episode in another library" to users who didn't queue them.

## GET /Browse?parentId=&startIndex=0&limit=50[&startsWith=A]
Empty `parentId` lists TV libraries. Library -> series, series -> seasons, season -> episodes.
`startsWith` (library pages only, D47): `A`–`Z` (any case) keeps the series or movies whose sort name starts with that letter (a leading "The" doesn't count), `#` keeps those that sort before A (digits, symbols). `totalCount` counts the filtered list. Any other value returns `400 bad_request`; below a library it is ignored.
Every row and breadcrumb entry carries `imageTag`, Jellyfin's tag for the item's primary image, or null when it has none (libraries always null). The page loads the poster from `../Items/{id}/Images/Primary?fillHeight=<px>&quality=90&tag=<imageTag>`, which needs no token. Season rows also carry `noSubtitleCount` (episodes with no English subtitle; Find subtitles can help with those). Episode and movie rows carry `subtitles` (D50, D52): one entry per language, English first, `{ "language":"en|da", "present", "hi", "status":"none|done|attempted", "eligible", "reason", "message", "syncedWith":"1.5.0", "outdated" }` (`outdated`: synced with SubSync before 1.4.1; D51). The row's own `eligible` is true when any language can be synced, its `status` is `done` when every subtitle it has is synced, and `reason`, `message` and `subtitleHi` describe English as before. They also carry `audioUntagged` (true when the reason is `no_english_audio` and every audio track's language is `und` or empty) and `subtitleHi` (the English subtitle on disk is `.en.hi.srt` or `.eng.hi.srt`, also when already synced; D48).
```json
{
  "parent": { "id": "...", "name": "Show", "type": "Series",
              "breadcrumb": [ {"id":"...","name":"TV","type":"Library"}, {"id":"...","name":"Show","type":"Series"} ] },
  "totalCount": 12,
  "items": [
    { "id":"...", "name":"Season 1", "type":"Season", "seriesId":"...",
      "indexNumber":1, "episodeCount":24, "eligibleCount":20, "syncedCount":3,
      "status":"none", "eligible":null, "reason":null }
  ]
}
```
`type` is `Library|Series|Season|Episode|Movie`. Library rows carry `libraryType` (`tvshows` or `movies`); a movie library lists `Movie` rows (paged, by name) with the same eligibility fields as episodes plus `productionYear`. For episodes: `eligible` true/false, `reason` one of `no_video_file|already_synced|no_english_audio|no_sidecar|null` (checked in that order), `status` one of `none|done|attempted` (attempted = `check` or `failed`, with `message`). Counts are filled for seasons only. Rows also carry `seriesName`, `indexNumber`, `parentIndexNumber`. `limit` is capped at 200. An unknown or inaccessible `parentId` returns `404 not_found`.

## GET /Search?q=&startIndex=0&limit=30
Series, episodes and movies the user can access. Items as above plus `seriesId`, `seriesName`.

## POST /Jobs/Preview
Request `{ "itemIds": ["..."] }`: seasons and episodes of one series, **or** movies (any, no series lock). Mixing movies with episodes returns `400 mixed_selection` (MOVIES-PLAN.md).
```json
{ "kind":"episodes|movies", "seriesId":"...", "seriesName":"Show", "selected":47, "eligible":35,
  "skipped": { "alreadySynced":12, "noEnglishAudio":0, "noSidecar":0, "noVideoFile":0 },
  "attemptedBefore":2 }
```
Returns `400` for `unsupported_item_type`, `multiple_series`, `cap_exceeded`, and `nothing_eligible` only when no ids were sent. A valid selection with nothing eligible returns `200` with `eligible: 0`, so the footer can still show the skipped counts; `POST /Jobs` rejects that case with `nothing_eligible`.

## POST /Jobs
Same request, plus optional `"languages": ["en","da"]` (default every language; D50) and `"again": true` (Sync again, D51: exactly one episode or movie, also when synced; a season is `400 unsupported_item_type`). Preview takes the same fields. Validates exactly like Preview, then appends the eligible episodes to the shared queue. `202 { "queued":35, "skipped":12, "queueLength":35 }`. `queued` counts subtitles (an episode can add an English and a Danish one); `skipped` = selected episodes with nothing added. An item and language is never in the queue twice. Queue items (`GET /Status`) and failed-syncs entries carry `language`; queue items also `again`. Retry re-queues the entry's language. Errors: everything Preview returns, plus `nothing_eligible` (400), `queue_full` (409, nothing is added) and `subsync_unavailable` (503: folder not set or `subsync-cli.exe` missing; the slow `check` runs only from the admin Test button).

## GET /Status
```json
{ "state":"idle|running|stopping", "subsyncAvailable":true, "stopReason":null,
  "current": { "jobId":"...", "itemId":"...", "name":"S01E04 Title", "seriesName":"Show", "status":"running",
               "message":null, "mine":true, "source":"manual", "note":null, "seconds":null, "startedUtc":"...",
               "stage":"listening", "expectedSeconds":448, "percent":41 },
  "totals": { "queued":35, "running":1, "done":3, "check":0, "failed":0, "cancelled":0, "skipped":0 },
  "etaSeconds": 2100,
  "aheadOfYours": 0,
  "items": [ { "jobId":"...", "itemId":"...", "name":"...", "seriesName":"...",
               "status":"queued|running|done|check|failed|cancelled|skipped", "message":null,
               "mine":true, "source":"manual|bazarr", "note":null, "seconds":71.2, "startedUtc":null } ] }
```
- One global queue shared by everyone, visible to every allowed user. A second Start appends to it; it never starts a second SubSync.
- `items` order: finished (oldest first), then running, then queued. With SubSync 1.3.0 up to two items are `running` at once: the item SubSync works on, and the next one sent ahead while it listens (D45, its stage `queued` until its turn). `current` is the older one. Finished items stay for one minute after they end, or until the owner clears them sooner (`POST /Jobs/Clear`); failed and check ones are in the failed-syncs log by then (D44). The queue itself is in memory and empties on restart (D11). `stopReason` stays after its items expire, until the next Start.
- `mine` marks items the caller queued. For other users' items in a library the caller can't access, `itemId`, `seriesName` and `note` are null and `name` is "Episode in another library".
- `skipped` means the episode was no longer eligible when its turn came (for example synced in the meantime); nothing was touched.
- `source`/`note`: where the job came from; Bazarr downloads use `source:"bazarr"` and a note such as "downloaded from opensubtitlescom" (BAZARR-PLAN.md). Server code queues them with `JobQueue.Enqueue(userId, items, JobSource.Bazarr, note, MaxQueuedEpisodes)`.
- `stopReason` is set when the queue stopped by itself (SubSync exit 7, or 3 consecutive crashes); the remaining queued items are then `cancelled` with that reason. Cleared by the next successful Start.
- `aheadOfYours`: pending items in front of the caller's first pending one; null when the caller has none.
- `maintenance` (D46) is set while SubSync is being updated: the message to show. `subsyncAvailable` is false meanwhile, and `POST /Jobs`, Retry and Bazarr downloads answer `503 subsync_unavailable` with it.
- `stage`, `expectedSeconds` and `percent` are set only on the running item. `stage` is what SubSync last reported (`queued`, `reading_audio`, `listening`, `matching`, `waiting_for_file`, or `working` for a stage this plugin doesn't know; null until its first report; SubSync 1.2.0 reports only the middle three). `expectedSeconds` is the server's estimate of the whole run (D40): start-up plus the video's length times a speed ratio learned from finished runs on this server, or null when Jellyfin has no runtime. `percent` (0 to 95) is the running item's share of the bar while SubSync 1.3.0 listens and reports how much of the soundtrack it has heard: listening's expected slice of the run, filled by SubSync's own percentage. It is null otherwise, and then the page draws the share from elapsed time over `expectedSeconds`. Either way the share stays below 95% until the item finishes, and the page never moves it backwards.
- ETA = pending (queued + running) x average `seconds` of finished episodes; null until one has finished (the page then uses the running item's `expectedSeconds`). The page polls every 1 s while running and every 5 s when idle, and stops while the tab is hidden.

## POST /AudioCheck
Request `{ "itemId": "..." }` (any allowed user, D49). Runs SubSync's read-only `subsync-cli detect <video> [--cpu]` on an episode or movie whose audio isn't tagged, stores the answer tied to the video's path, size and modified time, and returns `200 { "itemId", "language":"en|da|null", "probability":0.985, "english":true, "speechSeconds":90, "checkedUtc", "message":"Sounds like English (99% sure). It can be re-timed now." }`. `english` needs language `en` and probability of at least 0.5; then the item counts as English audio until its video changes. Takes about a minute on a CPU (10-minute limit); closing the page stops SubSync.
Errors: `404 not_found`, `409 no_video_file`, `409 audio_tagged` (a track says its language; checks never override tags), `503 subsync_unavailable`, `503 subsync_too_old` (needs SubSync 1.4.0), `409 subsync_busy` (a queue run, another check or a SubSync update is active: one SubSync at a time, hard rule 2), `422 check_failed` (SubSync's reason, e.g. it can't read the audio), `504 timed_out`. While a check runs, items can still be queued; the queue starts once it ends.
Browse and Search rows of untagged items carry `audioCheck { "language", "probability", "english", "checkedUtc", "message" }` while it matches the current video (else null).

## POST /Jobs/Cancel
Cancels the caller's own running and queued items; an administrator cancels all items. Returns the new Status. The running episode's subtitle is restored from its backup before it is marked `cancelled` (`state` is `stopping` meanwhile; `JOB-FLOW.md`). With SubSync 1.3.0 a cancel ends the queue's SubSync process; another user's item that was in it goes back to the head of the queue and runs again (D45).

## POST /Jobs/Clear
Removes the caller's finished items from the list (an administrator clears all). Returns the new Status.

## GET /FailedSyncs?startIndex=0&limit=20
The failed-syncs log (D44): every sync that ended `failed` or `check`, written when it ends and kept in `state.db` across restarts (the newest 500). Newest first, `limit` at most 100. Admins see everyone's entries; everyone else sees only their own (server-side filter).
```json
{ "totalCount": 2,
  "items": [ { "id": 17, "itemId": "...", "name": "S01E23 Title", "seriesName": "Show", "status": "failed|check",
               "message": "SubSync could not read the audio track.", "mine": true, "source": "manual|bazarr",
               "note": null, "finishedUtc": "2026-11-12T10:00:00Z" } ] }
```
Another user's entry in a library the viewer can't access has `itemId`, `seriesName`, `message` and `note` null and `name` "Episode in another library". `503 state_unavailable` when the state database didn't start.

## POST /FailedSyncs/{id}/Retry
Queues the entry's item again for the caller, validated exactly like `POST /Jobs` with that one id (so it is refused if it is synced meanwhile, no longer eligible, or outside the allowed libraries), then removes the entry from the log. The caller's own entries, or any entry for an admin; anything else is `404 not_found`. Returns what `POST /Jobs` returns.

## POST /FailedSyncs/Clear
Empties the log. Admin only (`403 forbidden` otherwise). Returns `{ "totalCount":0, "items":[] }`.

## Admin
- `GET /Admin/Libraries` -> `[ { "id":"...", "name":"TV Shows", "libraryType":"tvshows|movies", "allowed":true } ]`: every TV and movie library the admin can see, ignoring the `AllowedLibraryIds` setting, for the settings page (D43). `allowed` is the current setting (all true when none are chosen).
- `POST /Admin/TestSubSync` -> `{ "ok":true, "version":"SubSync 1.3.0", "mode":"GPU|CPU|unknown", "message":"..." }`
- `GET /Admin/SubSyncUpdate[?refresh=true]` -> `{ "installed":"1.3.0", "latest":"1.4.0", "updateAvailable":true, "releaseUrl":"https://github.com/darkbepo/SubSync/releases/tag/v1.4.0", "notes":"...", "state":"idle|running|done|failed", "step":"downloading|checking|waiting|installing|setup", "message":null, "checkedUtc":"..." }` (D46). GitHub is asked at most every 6 hours (`refresh=true` asks now). `updateAvailable` needs a readable installed version, a newer stable release and a checksum from GitHub. `step` only while `running`. `message` is the last update's result, or why the check failed. `releaseUrl` is only ever a page under `https://github.com/darkbepo/SubSync/releases/`.
- `POST /Admin/SubSyncUpdate` -> `202` with the status (`state:"running"`) when the update started; `200` with the status and the reason in `message` when it didn't (queue busy, nothing newer, already running, not configured). The page polls `GET` every 2 s while `running`.
- `POST /Admin/TestBazarr` with `{ "url":"...", "apiKey":"...", "pathMappings":"..." }` (unsaved form values; an empty `url` or `apiKey`, or a null `pathMappings`, uses the saved setting; the saved key is only sent to the saved address, so testing another address needs the key typed in) -> always `200 { "ok":true, "version":"1.5.3", "message":"...", "pathMappingErrors":["Line 2: ..."], "warnings":["..."] }`; `warnings` says when Bazarr's own sync is on or its setting can't be read (BAZARR-PLAN.md, owner decision 3). Calls Bazarr's `GET /api/system/status` server-side. Works whether or not `BazarrEnabled` is on. The response never contains the API key.

## Bazarr (Find subtitles, BAZARR-PLAN.md)
Any allowed user (`AllowedUsers`, owner decision 4). The API key never leaves the server; responses carry our own short-lived handles, never Bazarr's opaque `subtitle` value.
- `GET /Bazarr/Info` -> `{ "enabled": true }`. True when `BazarrEnabled` is on and the address and key are set. The page shows Find subtitles only when it is true.
- `GET /Bazarr/Search?itemId=[&language=en|da][&replace=true]` -> `200 { "itemId", "kind":"movie|episode", "resource":"<video path as Bazarr reports it>", "matchedBy":"path|ids", "results":[ { "id", "score", "language":"EN|EN:HI|DA|DA:HI", "languageCode":"en|da|pt-br", "english":true, "hearingImpaired", "provider", "releases":[..], "uploader", "matches":[..], "dontMatches":[..] } ], "hidden":1 }`.
  - Results are every language Bazarr returned (its language profile decides which), not forced (a forced subtitle saved as `.en.forced.srt` would never be re-timed), best score first (D48). `hidden` counts the forced ones left out. `english` says whether a download is re-timed.
  - `id` is a random handle kept server-side for 10 minutes, bound to the caller and the item; phase 2's Download sends it back.
  - Checks, in order: Bazarr set up (`503 bazarr_unavailable`), item accessible (`404 not_found`), no accepted sidecar of the language asked for (English without `language`), unless `replace` (`409 has_subtitle`; D28, D52), video file (`409 no_video_file`), English audio (`409 no_english_audio`), rate limit (`429 rate_limited` with `Retry-After`), item known to Bazarr (`404 bazarr_not_found`), then the search (`502 search_failed` with Bazarr's own message, `503 bazarr_unavailable` when unreachable or the key is rejected).
  - Rate limit: one search at a time per user, and at least 5 s between a user's searches. Refused requests (wrong item, has a subtitle) don't count.
  - Item mapping: by path after `BazarrPathMappings`, then by IMDb id (movies) or TVDB/IMDb id plus season and episode number (episodes). Bazarr's movie and series lists are read in pages of 250 and kept 10 minutes; a miss on a cached list reads it once more.
- `POST /Bazarr/Download` with `{ "itemId":"...", "resultId":"<id from Search>", "replace":false }` -> `202 { "queued":true, "subtitle":"Film.en.srt", "message":"..." }`, or `200` with `queued:false` when the subtitle was downloaded but the queue filled up meanwhile, or when it's **another language** than English or Danish, or Danish on audio that isn't English (`"subtitle":"Film.de.srt"`, kept as saved, never re-timed; D48). English and Danish downloads are re-timed (D50). `replace:true` is Download new (D52): the subtitle of that language that is there is replaced, the previous one kept as `<name>.old` (named in `subtitle`).
  - Checks before anything is downloaded: Bazarr set up (`503 bazarr_unavailable`), item accessible (`404 not_found`), the handle is the caller's and not expired (`410 result_expired`), English audio for an English result (`409 no_english_audio`), no subtitle of the result's language yet unless `replace` (`409 has_subtitle`), video file (`409`), for English and Danish only: SubSync configured (`503 subsync_unavailable`) and room in the queue (`409 queue_full`); then no other download for this item (`409 download_in_progress`) and none by this user (`429 rate_limited`). Another language is also refused when a subtitle in that language is already there (`409 has_subtitle`, naming the file), because Bazarr would overwrite it.
  - Then every subtitle file next to the video is copied into memory, Bazarr is asked to download (`original_format=False`, so it saves `.srt`), and the server waits up to 2 minutes for an accepted sidecar that parses as SRT and has stopped growing. Bazarr 1.6.2 downloads in a background job and doesn't report the outcome, so this is how success is detected.
  - Success: any other subtitle Bazarr changed is put back, the item's `done` record of that language is deleted, and one job is queued for that language with `source:"bazarr"` and `note:"downloaded from <provider>"`. Progress shows in `GET /Status`.
  - Failure (`502 download_failed`, or Bazarr's own error passed through): a new accepted sidecar that isn't valid is removed, and every copied file that changed is restored. If Bazarr saved under a name the fixer doesn't re-time (for example `.en.cc.srt`, or `<video>.srt` with "single language" on), the message names it.
  - Once Bazarr has accepted, the server finishes the wait even if the browser disconnects. A subtitle Bazarr writes after the 2-minute wait stays on disk unqueued; selecting the item re-times it (D33).

## Error codes
| Code | HTTP | Meaning |
|---|---|---|
| `multiple_series` | 400 | Items belong to more than one series |
| `mixed_selection` | 400 | Movies and episodes in one request |
| `unsupported_item_type` | 400 | Series or library ID (or non-TV item) submitted |
| `cap_exceeded` | 400 | More than `MaxEpisodesPerRequest` eligible episodes |
| `nothing_eligible` | 400 | Selection contains no eligible episode |
| `queue_full` | 409 | Would exceed `MaxQueuedEpisodes` |
| `subsync_unavailable` | 503 | SubSync folder not set or `check` failed |
| `forbidden` | 403 | Not allowed by `AllowedUsers` |
| `bazarr_unavailable` | 503 | Bazarr search off, not set up, unreachable, or the key was rejected |
| `bazarr_not_found` | 404 | Bazarr (Radarr/Sonarr) doesn't know the item; check path mappings |
| `has_subtitle` | 409 | Find subtitles on an item that already has an accepted English sidecar (D28) |
| `no_video_file` / `no_english_audio` | 409 | Find subtitles on an item that could never be re-timed |
| `rate_limited` | 429 | Searched again within 5 s, or a search is still running; `Retry-After` says when |
| `search_failed` | 502 | Bazarr's search answered with its own error (message passed through) |
| `result_expired` | 410 | The search result handle is older than 10 minutes, or belongs to another user or item |
| `download_in_progress` | 409 | Another download for the same item is running |
| `download_failed` | 502 | Bazarr refused the download, or no usable accepted sidecar appeared within 2 minutes |
