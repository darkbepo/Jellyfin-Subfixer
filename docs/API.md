# REST API

Base path `/SubtitleFixer`. All data endpoints require an authenticated user (`[Authorize]`) and the `AllowedUsers` policy. Settings and test endpoints require an administrator (`[Authorize(Policy = Policies.RequiresElevation)]`). Authenticated calls send only `Authorization: MediaBrowser Token="<token>"`: Jellyfin overwrites the device's stored name and version whenever a request sends a different `Device` or `Version`, so sending ours would rename the user's browser session. Responses are camelCase (Jellyfin's own API is PascalCase). Legacy headers (`X-Emby-Token`, `X-Emby-Authorization`, `api_key`) are off by default on 12 (spike S2). A missing or bad token gets `401` from Jellyfin before our code runs. Errors are `{ "error": "<code>", "message": "<human text>" }`.

## Static (anonymous)
`GET /Page` (the page HTML), `GET /Page/app.js`, `GET /Page/styles.css`, `GET /menu.js` (avatar-menu script). These contain no data.

## GET /Me
`{ "userId":"...", "name":"...", "isAdmin":false, "allowed":true }`. The page calls this first: `401` means show the sign-in link, `allowed:false` means show "not allowed" (no `403`, so the page can explain).

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
`type` is `Library|Series|Season|Episode`. For episodes: `eligible` true/false, `reason` one of `no_video_file|already_synced|no_english_audio|no_sidecar|null` (checked in that order), `status` one of `none|done|attempted` (attempted = `check` or `failed`, with `message`). Counts are filled for seasons only. Rows also carry `seriesName`, `indexNumber`, `parentIndexNumber`. `limit` is capped at 200. An unknown or inaccessible `parentId` returns `404 not_found`.

## GET /Search?q=&startIndex=0&limit=30
Series and episodes the user can access. Items as above plus `seriesId`, `seriesName`.

## POST /Jobs/Preview
Request `{ "itemIds": ["..."] }` (seasons and episodes only).
```json
{ "seriesId":"...", "seriesName":"Show", "selected":47, "eligible":35,
  "skipped": { "alreadySynced":12, "noEnglishAudio":0, "noSidecar":0, "noVideoFile":0 },
  "attemptedBefore":2 }
```
Returns `400` for `unsupported_item_type`, `multiple_series`, `cap_exceeded`, and `nothing_eligible` only when no ids were sent. A valid selection with nothing eligible returns `200` with `eligible: 0`, so the footer can still show the skipped counts; `POST /Jobs` rejects that case with `nothing_eligible`.

## POST /Jobs
Same request; validates exactly like Preview, then queues the eligible episodes. `202 { "queued":35, "skipped":12, "queueLength":35 }`.

## GET /Status
```json
{ "state":"idle|running|stopping", "subsyncAvailable":true,
  "current": { "itemId":"...", "name":"S01E04 ...", "startedUtc":"..." },
  "totals": { "queued":35, "done":3, "check":0, "failed":0, "cancelled":0 },
  "etaSeconds": 2100,
  "items": [ { "itemId":"...", "name":"...", "status":"queued|running|done|check|failed|cancelled", "message":null, "mine":true } ] }
```
One global queue shared by everyone, visible to every allowed user. A second Start appends to it; it never starts a second SubSync. `mine` marks items the caller queued. For items in a library the caller can't access, `itemId` is null and `name` is "Episode in another library". ETA = remaining episodes x average `seconds` of finished ones; null until one has finished.

## POST /Jobs/Cancel
Cancels the caller's own running and queued items; an administrator cancels all items. `200`. Restores the subtitle of the running episode if it is cancelled (`JOB-FLOW.md`).

## Admin
- `POST /Admin/TestSubSync` -> `{ "ok":true, "version":"1.2.0", "mode":"GPU|CPU|unknown", "message":"..." }`
- `POST /Admin/TestBazarr` with `{ "url":"...", "apiKey":"...", "pathMappings":"..." }` (unsaved form values; an empty `url` or `apiKey`, or a null `pathMappings`, uses the saved setting) -> always `200 { "ok":true, "version":"1.5.3", "message":"...", "pathMappingErrors":["Line 2: ..."] }`. Calls Bazarr's `GET /api/system/status` server-side. Works whether or not `BazarrEnabled` is on. The response never contains the API key.

## Error codes
| Code | HTTP | Meaning |
|---|---|---|
| `multiple_series` | 400 | Items belong to more than one series |
| `unsupported_item_type` | 400 | Series or library ID (or non-TV item) submitted |
| `cap_exceeded` | 400 | More than `MaxEpisodesPerRequest` eligible episodes |
| `nothing_eligible` | 400 | Selection contains no eligible episode |
| `queue_full` | 409 | Would exceed `MaxQueuedEpisodes` |
| `subsync_unavailable` | 503 | SubSync folder not set or `check` failed |
| `forbidden` | 403 | Not allowed by `AllowedUsers` |
