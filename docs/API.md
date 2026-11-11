# REST API

Base path `/SubtitleFixer`. All data endpoints require an authenticated user (`[Authorize]`) and the `AllowedUsers` policy. Settings and test endpoints require an administrator (`[Authorize(Policy = Policies.RequiresElevation)]`). Authenticated calls send `Authorization: MediaBrowser Client="Subtitle Fixer", Device="…", DeviceId="…", Version="…", Token="<token>"`. Legacy headers (`X-Emby-Token`, `X-Emby-Authorization`, `api_key`) are off by default on 12 (spike S2). A missing or bad token gets `401` from Jellyfin before our code runs. Errors are `{ "error": "<code>", "message": "<human text>" }`.

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
`type` is `Library|Series|Season|Episode`. For episodes: `eligible` true/false, `reason` one of `no_english_audio|no_sidecar|already_synced|null`, `status` one of `none|done|attempted` (attempted = `check` or `failed`, with `message`). Counts are filled for seasons only.

## GET /Search?q=&startIndex=0&limit=30
Series and episodes the user can access. Items as above plus `seriesId`, `seriesName`.

## POST /Jobs/Preview
Request `{ "itemIds": ["..."] }` (seasons and episodes only).
```json
{ "seriesId":"...", "seriesName":"Show", "selected":47, "eligible":35,
  "skipped": { "alreadySynced":12, "noEnglishAudio":0, "noSidecar":0 },
  "attemptedBefore":2 }
```

## POST /Jobs
Same request; validates exactly like Preview, then queues the eligible episodes. `202 { "queued":35, "skipped":12, "queueLength":35 }`.

## GET /Status
```json
{ "state":"idle|running|stopping", "subsyncAvailable":true,
  "current": { "itemId":"...", "name":"S01E04 ...", "startedUtc":"..." },
  "totals": { "queued":35, "done":3, "check":0, "failed":0, "cancelled":0 },
  "etaSeconds": 2100,
  "items": [ { "itemId":"...", "name":"...", "status":"queued|running|done|check|failed|cancelled", "message":null } ] }
```
Global queue, visible to every allowed user (names only). ETA = remaining episodes x average `seconds` of finished ones; null until one has finished.

## POST /Jobs/Cancel
Cancels the current and all queued items. `200`. Restores the subtitle of the running episode (`JOB-FLOW.md`).

## Admin
- `POST /Admin/TestSubSync` -> `{ "ok":true, "version":"1.2.0", "mode":"GPU|CPU|unknown", "message":"..." }`

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
