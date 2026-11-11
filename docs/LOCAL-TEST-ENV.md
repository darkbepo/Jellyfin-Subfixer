# Local test environment (owner's PC)

Set up 2026-11-11 for testing on this machine. Everything listens on `127.0.0.1` only. **No API keys are stored in this repo**; read them from each app's own config file on this PC.

| Service | Version | Address | Runs as | API key lives in |
|---|---|---|---|---|
| Jellyfin "Testflix" | 12.4.0 | `http://localhost:8096` | Tray app (`C:\Program Files\Jellyfin\Server`), data `C:\ProgramData\Jellyfin\Server` | Create one in Dashboard > API Keys when needed |
| Radarr | 6.0.4 | `http://127.0.0.1:7878` | Windows service `Radarr`, data `C:\ProgramData\Radarr` | `C:\ProgramData\Radarr\config.xml` (`<ApiKey>`) |
| Bazarr | 1.6.2 (winget package 1.17) | `http://127.0.0.1:6767` | Windows service `Bazarr` (nssm), app `C:\Bazarr`, data `C:\ProgramData\Bazarr` | `C:\ProgramData\Bazarr\config\config.yaml` (`auth.apikey`) |

Read a key in PowerShell without printing others:
```powershell
([xml](Get-Content C:\ProgramData\Radarr\config.xml)).Config.ApiKey
(Select-String -Path C:\ProgramData\Bazarr\config\config.yaml -Pattern '^\s+apikey:\s*(\S+)' | Select-Object -First 1).Matches[0].Groups[1].Value   # first apikey = Bazarr's own (auth section)
```

## What is configured
- **Library:** Testflix has one library, **Film** (`C:\notporn`), with one movie (22 Jump Street, 2014) and three `.srt` files. There is **no TV library**, so the subtitle fixer itself (TV-only in v1) and Bazarr's episode endpoints can't be tested here yet; Bazarr movie search and download can.
- **Radarr:** bound to `127.0.0.1`; login disabled (`AuthenticationMethod=External`, safe only because it is localhost-bound); root folder `C:\notporn`; the movie is added **unmonitored** with no search and no download client (Radarr id `1`, TMDB `187017`).
- **Bazarr:**
  - Bound to `127.0.0.1`, no login (`auth.type: null`).
  - Connected to Radarr (Sonarr off). Language profile 1 "English" (en, not HI, not forced), set as the movie default and assigned to the movie.
  - Providers that need no account: embeddedsubtitles, podnapisi, yifysubtitles. Analytics off.
  - Bazarr's own subtitle sync (`subsync.use_subsync`) is **off**, so downloads are not synced twice (BAZARR-PLAN.md, conflict 3).
  - The config before these changes is at `config.yaml.before-setup`.
- **Verified:** `GET /api/providers/movies?radarrid=1` returns 35 English results in about 5 s. Fields: `score, orig_score, score_without_hash, language, hearing_impaired, forced, provider, release_info, uploader, matches, dont_matches, original_format, subtitle, url`. Profile assignment is `POST /api/movies?radarrid=1&profileid=1`. The API description is served at `/api/swagger.json`.

## Not done
- No TV: add a TV library to Testflix (copied episodes), install Sonarr (`winget install TeamSonarr.Sonarr`), and connect it in Bazarr to test episodes.
- Restarting the Bazarr service from a normal (non-admin) shell is refused; use Bazarr's own restart (`POST /api/system?action=restart`) or an admin shell.
- The plugin is not installed on Testflix yet (`build\package.ps1`, then unzip into `C:\ProgramData\Jellyfin\Server\plugins\SubtitleFixer_<version>\` and restart the tray app).
