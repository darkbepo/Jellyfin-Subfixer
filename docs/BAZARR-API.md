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

## Still to verify (phase 1)
Series and episode lists, episode manual search, episode download, movie download, and the movie list. Not yet written into the client. Movie manual search was seen working live on 1.6.2 (`GET /api/providers/movies?radarrid=1`: 35 results in about 5 s, with `score`, `orig_score`, `score_without_hash`, `language`, `hearing_impaired`, `forced`, `provider`, `release_info`, `uploader`, `matches`, `dont_matches`, `original_format`, `subtitle`, `url`); record its full shape here from `/api/swagger.json` before using it. The test setup has no Sonarr, so episode calls can only be checked from source for now. See `BAZARR-PLAN.md`, "Do not guess the Bazarr API". Check each against the owner's instance (its Swagger page under `/api/`) before writing the client call.

## HTTP client
- **From source** (`Jellyfin.Server/Startup.cs`, `v12.4`): the server calls `services.AddHttpClient(...)`, so `IHttpClientFactory` is in the container. `BazarrClient` asks it for the named client `SubtitleFixer.Bazarr`. Nothing configures that name, so it gets the default handler, and each call is capped at 30 s.
