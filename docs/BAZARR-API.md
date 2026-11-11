# Bazarr API: verified calls

Only calls listed here may go into `BazarrClient`. Each one has a source and a live check. The "believed, unverified" list in `BAZARR-PLAN.md` stays unverified until it moves here.

**How calls are marked.** "From source" means it was read in `morpheus65535/bazarr` (`master`, read 2026-11-11), not run against a server. "Live" means it was checked on the owner's Bazarr. Record the owner's Bazarr version (Bazarr > System > Status) next to each live check.

Live checks so far: the local test Bazarr **1.6.2** at `http://127.0.0.1:6767` (`docs/LOCAL-TEST-ENV.md`), 2026-11-11. The owner's production Bazarr: _not checked yet_.

## Auth
- **From source** (`bazarr/api/utils.py`, `authenticate`): the key is accepted in the `X-API-KEY` header, an `apikey` query parameter, or an `apikey` form field. A wrong or missing key gets `401`.
- We send only the header, so the key never appears in a URL or an access log.
- **Live (1.6.2):** a wrong key and a missing key both get `401` from `/api/system/status`, even with Bazarr's login disabled (login is for the web UI, not the API). Swagger (`/api/swagger.json`) lists `401 Not Authenticated` for it.
- **Live check still to do (owner):** the Test Bazarr button in Jellyfin with a wrong key shows "Bazarr rejected the API key."

## GET /api/system/status
- **From source** (`bazarr/api/system/status.py`): route `system/status` under the `/api` prefix, with `@authenticate`. It returns `{ "data": { "bazarr_version", "package_version", "sonarr_version", "radarr_version", "operating_system", "python_version", "database_engine", "database_migration", "bazarr_directory", "bazarr_config_directory", "start_time", "timezone", "cpu_cores" } }`.
- We read only `data.bazarr_version` (a string).
- Used by `POST /SubtitleFixer/Admin/TestBazarr`.
- **Live (1.6.2):** with the right key it returns `{"data":{"bazarr_version":"1.6.2", ...}}`, the same fields as in source; `radarr_version` is filled and `sonarr_version` is empty on this setup.
- **Live check still to do (owner):** Test Bazarr in Jellyfin with the right address and key shows the same version as Bazarr > System > Status. If Bazarr runs under a base URL (Settings > General > Base URL), the address must include it, for example `http://host:6767/bazarr`.


## GET /api/system/settings
- **Live (1.6.2):** returns `200` with the whole Bazarr config as a JSON object at the root (no `data` wrapper). Its top-level keys are sections such as `auth`, `general`, `subsync`, `radarr`, `sonarr` and one per provider. It is **not** in `/api/swagger.json`.
- It contains secrets: Bazarr's own API key, and every provider's username, password and API key. We read only `subsync.use_subsync` (a boolean) and never log, keep or return anything else. Don't add more reads from this call without a good reason.
- `subsync.use_subsync` is Bazarr's "Enable Automatic Subtitles Audio Synchronization" checkbox, under Settings > Subtitles > Audio Synchronization. Checked in the 1.6.2 web bundle, and in `frontend/src/pages/Settings/SubtitleProcessing` on `master`.
- Used by `POST /SubtitleFixer/Admin/TestBazarr`, after a good status call. It warns when the setting is on (BAZARR-PLAN.md, owner decision 3), and also when it can't be read.
- **Live check still to do (owner):** turn that checkbox on in Bazarr; Test connection then shows the amber warning.

Swagger (`/api/swagger.json`, 1.6.2) lists parameters but no response models, so response shapes below come from live calls (movies) and from source at tag `v1.6.2` (series, episodes).

## GET /api/movies?start=&length=
- **Swagger (1.6.2):** `start`, `length`, `radarrid[]` and filters; `200` or `401`.
- **Live (1.6.2):** `{ "data": [ ... ], "total": 1 }`. Each row has `radarrId` (int), `path` (Radarr's path, Windows-style here), `imdbId`, `title`, `year` (string), `audio_language`, `subtitles`, `missing_subtitles`, `profileId`, `monitored`, `sceneName` and artwork. **No `tmdbId`.**
- We read `radarrId`, `path`, `imdbId`, `title`, in pages of 250, and cache the list 10 minutes (`BazarrItemResolver`).

## GET /api/series?start=&length=
- **From source** (`api/series/series.py`, `v1.6.2`): same paging. Rows have `sonarrSeriesId`, `path` (series folder), `tvdbId` (int), `imdbId`, `title` and more; `{ data, total }`.
- **Not live-checked:** the test setup has no Sonarr.

## GET /api/episodes?seriesid[]=
- **From source** (`api/episodes/episodes.py`, `v1.6.2`): rows have `sonarrEpisodeId`, `sonarrSeriesId`, `season`, `episode`, `path`, `title`, `subtitles`, `missing_subtitles`. One series per call in our use, so a whole TV library is never read at once.
- **Not live-checked** (no Sonarr).

## GET /api/providers/movies?radarrid= and /api/providers/episodes?episodeid=
- **Live (1.6.2, movies):** `{ "data": [ ... ] }`; 35 results for Radarr id 1 in 4.9 to 5.6 s. Fields: `score` (int, percent), `orig_score`, `score_without_hash`, `language` (`en`), `hearing_impaired`, `forced` and `original_format` as the **strings** `"True"`/`"False"`, `provider`, `release_info` (array), `uploader`, `matches`/`dont_matches` (arrays of property names such as `release_group`, `hash`, `audio_codec`), `subtitle` (opaque handle, a 36-character GUID here) and `url`.
- **From source** (`api/providers/providers_movies.py`, `providers_episodes.py`, `v1.6.2`): `404 "Movie not found"` for an unknown id (live: confirmed for Radarr id 999999); `500` with a JSON string message, for example `"Movie file not found. Path mapping issue?"` when Bazarr can't see the file. The episode endpoint has the same result fields.
- Episode search is **not live-checked** (no Sonarr).
- We pass on `language`, `hearing_impaired`, `forced`, `score`, `provider`, `release_info`, `uploader`, `matches`, `dont_matches`. `subtitle` and `original_format` stay on the server for phase 2's download.

## Still to verify (phase 2)
`POST /api/providers/movies` and `/episodes` (download): parameters are in Swagger (`radarrid` or `seriesid`+`episodeid`, `hi`, `forced`, `original_format`, `provider`, `subtitle`, all required), response `204`. Check the live behavior, and the file name Bazarr writes, before using it.

## HTTP client
- **From source** (`Jellyfin.Server/Startup.cs`, `v12.4`): the server calls `services.AddHttpClient(...)`, so `IHttpClientFactory` is in the container. `BazarrClient` asks it for the named client `SubtitleFixer.Bazarr`. Nothing configures that name, so it gets the default handler. Each call is capped at 30 s by our own token; manual search at 90 s (D30).
