# REST API

Base path `/SubtitleFixer`. All data endpoints require an authenticated user (`[Authorize]`) and the `AllowedUsers` policy. Settings and test endpoints require an administrator (`[Authorize(Policy = Policies.RequiresElevation)]`). Authenticated calls send only `Authorization: MediaBrowser Token="<token>"`: Jellyfin overwrites the device's stored name and version whenever a request sends a different `Device` or `Version`, so sending ours would rename the user's browser session. Responses are camelCase (Jellyfin's own API is PascalCase). Legacy headers (`X-Emby-Token`, `X-Emby-Authorization`, `api_key`) are off by default on 12 (spike S2). A missing or bad token gets `401` from Jellyfin before our code runs. Errors are `{ "error": "<code>", "message": "<human text>" }`.

## Static (anonymous)
`GET /Page` (the page HTML), `GET /Page/app.js`, `GET /Page/styles.css`, `GET /menu.js` (avatar-menu script). These contain no data.

## GET /Me
`{ "userId":"...", "name":"...", "isAdmin":false, "allowed":true, "reason":null, "message":null }`. The page calls this first: `401` means show the sign-in link; `allowed:false` means show `message` (no `403`, so the page can explain). `reason` is `not_configured` (non-admins wait until SubSync is configured, D10) or `admins_only` (`AllowedUsers = AdminOnly`). The avatar-menu entry is shown only when `allowed` is true. Data endpoints return `403 forbidden` with the same message.

## GET /Browse?parentId=&startIndex=0&limit=50
Empty `parentId` lists TV libraries. Library -> series, series -> seasons, season -> episodes.
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
Same request; validates exactly like Preview, then appends the eligible episodes to the shared queue. `202 { "queued":35, "skipped":12, "queueLength":35 }`. `skipped` = selected episodes not added (ineligible, or already queued or running; an episode is never in the queue twice). Errors: everything Preview returns, plus `nothing_eligible` (400), `queue_full` (409, nothing is added) and `subsync_unavailable` (503: folder not set or `subsync-cli.exe` missing; the slow `check` runs only from the admin Test button).

## GET /Status
```json
{ "state":"idle|running|stopping", "subsyncAvailable":true, "stopReason":null,
  "current": { "jobId":"...", "itemId":"...", "name":"S01E04 Title", "seriesName":"Show", "status":"running",
               "message":null, "mine":true, "source":"manual", "note":null, "seconds":null, "startedUtc":"..." },
  "totals": { "queued":35, "running":1, "done":3, "check":0, "failed":0, "cancelled":0, "skipped":0 },
  "etaSeconds": 2100,
  "aheadOfYours": 0,
  "items": [ { "jobId":"...", "itemId":"...", "name":"...", "seriesName":"...",
               "status":"queued|running|done|check|failed|cancelled|skipped", "message":null,
               "mine":true, "source":"manual|bazarr", "note":null, "seconds":71.2, "startedUtc":null } ] }
```
- One global queue shared by everyone, visible to every allowed user. A second Start appends to it; it never starts a second SubSync.
- `items` order: finished (oldest first), then running, then queued. Finished items stay until the owner clears them (`POST /Jobs/Clear`) or for 24 hours. The queue itself is in memory and empties on restart (D11).
- `mine` marks items the caller queued. For other users' items in a library the caller can't access, `itemId`, `seriesName` and `note` are null and `name` is "Episode in another library".
- `skipped` means the episode was no longer eligible when its turn came (for example synced in the meantime); nothing was touched.
- `source`/`note`: where the job came from; Bazarr downloads use `source:"bazarr"` and a note such as "downloaded from opensubtitlescom" (BAZARR-PLAN.md). Server code queues them with `JobQueue.Enqueue(userId, items, JobSource.Bazarr, note, MaxQueuedEpisodes)`.
- `stopReason` is set when the queue stopped by itself (SubSync exit 7, or 3 consecutive crashes); the remaining queued items are then `cancelled` with that reason. Cleared by the next successful Start.
- `aheadOfYours`: pending items in front of the caller's first pending one; null when the caller has none.
- ETA = pending (queued + running) x average `seconds` of finished episodes; null until one has finished. The page polls every 1 s while running and every 5 s when idle, and stops while the tab is hidden.

## POST /Jobs/Cancel
Cancels the caller's own running and queued items; an administrator cancels all items. Returns the new Status. The running episode's subtitle is restored from its backup before it is marked `cancelled` (`state` is `stopping` meanwhile; `JOB-FLOW.md`).

## POST /Jobs/Clear
Removes the caller's finished items from the list (an administrator clears all). Returns the new Status.

## Admin
- `POST /Admin/TestSubSync` -> `{ "ok":true, "version":"1.2.0", "mode":"GPU|CPU|unknown", "message":"..." }`
- `POST /Admin/TestBazarr` with `{ "url":"...", "apiKey":"...", "pathMappings":"..." }` (unsaved form values; an empty `url` or `apiKey`, or a null `pathMappings`, uses the saved setting; the saved key is only sent to the saved address, so testing another address needs the key typed in) -> always `200 { "ok":true, "version":"1.5.3", "message":"...", "pathMappingErrors":["Line 2: ..."], "warnings":["..."] }`; `warnings` says when Bazarr's own sync is on or its setting can't be read (BAZARR-PLAN.md, owner decision 3). Calls Bazarr's `GET /api/system/status` server-side. Works whether or not `BazarrEnabled` is on. The response never contains the API key.

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
