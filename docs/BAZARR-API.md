# Bazarr API: verified calls

Only calls listed here may go into `BazarrClient`. Each one has a source and a live check. The "believed, unverified" list in `BAZARR-PLAN.md` stays unverified until it moves here.

**How calls are marked.** "From source" means it was read in `morpheus65535/bazarr` (`master`, read 2026-11-11), not run against a server. "Live" means it was checked on the owner's Bazarr. Record the owner's Bazarr version (Bazarr > System > Status) next to each live check.

Owner's Bazarr version: _not checked yet_.

## Auth
- **From source** (`bazarr/api/utils.py`, `authenticate`): the key is accepted in the `X-API-KEY` header, an `apikey` query parameter, or an `apikey` form field. A wrong or missing key gets `401`.
- We send only the header, so the key never appears in a URL or an access log.
- **Live check:** the Test Bazarr button with a wrong key shows "Bazarr rejected the API key."

## GET /api/system/status
- **From source** (`bazarr/api/system/status.py`): route `system/status` under the `/api` prefix, with `@authenticate`. It returns `{ "data": { "bazarr_version", "package_version", "sonarr_version", "radarr_version", "operating_system", "python_version", "database_engine", "database_migration", "bazarr_directory", "bazarr_config_directory", "start_time", "timezone", "cpu_cores" } }`.
- We read only `data.bazarr_version` (a string).
- Used by `POST /SubtitleFixer/Admin/TestBazarr`.
- **Live check:** Test Bazarr with the right address and key shows the same version as Bazarr > System > Status. If Bazarr runs under a base URL (Settings > General > Base URL), the address must include it, for example `http://host:6767/bazarr`.

## Still to verify (phase 1)
Series and episode lists, episode manual search, episode download, and the movie equivalents. See `BAZARR-PLAN.md`, "Do not guess the Bazarr API". Check each against the owner's instance (its Swagger page under `/api/`) before writing the client call.

## HTTP client
- **From source** (`Jellyfin.Server/Startup.cs`, `v12.4`): the server calls `services.AddHttpClient(...)`, so `IHttpClientFactory` is in the container. `BazarrClient` asks it for the named client `SubtitleFixer.Bazarr`. Nothing configures that name, so it gets the default handler, and each call is capped at 30 s.
