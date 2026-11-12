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
- **Library:** Testflix has one library, **Film** (`C:\notporn`). Jellyfin lists two movies there. *22 Jump Street (2014)* is eligible for the subtitle fixer. *21 Jump Street (2012)* is not, because its audio track has no English language tag. Radarr only imported 22 Jump Street. There is **no TV library**, so the subtitle fixer itself (TV-only in v1) and Bazarr's episode endpoints can't be tested here yet; Bazarr movie search and download can.
- **Radarr:** bound to `127.0.0.1`; login disabled (`AuthenticationMethod=External`, safe only because it is localhost-bound); root folder `C:\notporn`; the movie is added **unmonitored** with no search and no download client (Radarr id `1`, TMDB `187017`).
- **Bazarr:**
  - Bound to `127.0.0.1`, no login (`auth.type: null`).
  - Connected to Radarr (Sonarr off). Language profile 1 "English" (en, not HI, not forced), set as the movie default and assigned to the movie.
  - **2026-11-12 (D48):** Danish enabled and added to profile 1 (now `en`, `da`; normal or HI, always), so Find subtitles shows Danish results: 22 Jump Street gets 8 `da` and 35 `en` from Bazarr. **Download Only Monitored** turned on for Radarr and Sonarr, so Bazarr's 6-hourly wanted search doesn't fetch Danish (or anything) on its own for the unmonitored test movie; "Sync Only Monitored" stays off, so unmonitored movies stay in Bazarr and can be searched. **Live Danish Get (2026-11-12):** with 22 Jump Street's `.en.srt` moved aside (owner allowed it), the page's Find subtitles showed 43 results (8 DA, 35 EN), "Show only DA" left 8, and Get on the 91% one saved `22 Jump Street (2014) Bluray-1080p.da.srt` (106 KB, valid Danish SRT) in about 16 s with "Subtitle Fixer only re-times English subtitles", nothing queued. A second Danish Get was refused with `409` and the file was untouched. Afterwards the `.en.srt` was put back (same hash) and the test `.da.srt` removed.
  - Providers that need no account: embeddedsubtitles, podnapisi, yifysubtitles. Analytics off.
  - Bazarr's own subtitle sync (`subsync.use_subsync`) is **off**, so downloads are not synced twice (BAZARR-PLAN.md, conflict 3).
  - The config before these changes is at `config.yaml.before-setup`.
- **Verified:** `GET /api/providers/movies?radarrid=1` returns 35 English results in about 5 s. Fields: `score, orig_score, score_without_hash, language, hearing_impaired, forced, provider, release_info, uploader, matches, dont_matches, original_format, subtitle, url`. Profile assignment is `POST /api/movies?radarrid=1&profileid=1`. The API description is served at `/api/swagger.json`.

## Not done
- No TV: add a TV library to Testflix (copied episodes), install Sonarr (`winget install TeamSonarr.Sonarr`), and connect it in Bazarr to test episodes.
- Restarting the Bazarr service from a normal (non-admin) shell is refused; use Bazarr's own restart (`POST /api/system?action=restart`) or an admin shell.
- **SubSync 1.3.1** is installed at `C:\SubSync` (first 1.2.0 from the release zip, then updated from the plugin's settings page; CPU only because this PC has no NVIDIA GPU). The plugin's SubSync folder setting points there.
- **TV library "Serier"** (`C:\notporn2`, added 2026-11-11): *Schitt's Creek* season 1, 10 episodes with English audio and `.en.srt` files whose names differ from the videos only in letter case.
  - S01E01 and S01E02 have been re-timed by the plugin.
  - Untouched originals of all `.srt` files in both libraries were kept outside the repo before testing.
- **Settings:** "Who can use it" is still **Administrators only** on Testflix, because its settings file predates the new default.
- **Real-CLI tests:** set `SUBFIXER_SUBSYNC_FOLDER=C:\SubSync` and `SUBFIXER_TEST_CLIP` to a short English clip with a `.en.srt` next to it, then run `dotnet test --filter RealSubSyncTests`. They take about 6 minutes on this CPU.

## Subtitle Fixer on Testflix
- **Installed 2026-11-12:** 0.4.0.0 in `C:\ProgramData\Jellyfin\Server\plugins\Subtitle Fixer_0.4.0.0\` (DLL plus `meta.json`), unzipped by hand; the state database migrated to version 3 on first start. Until then the server still ran 0.1.1.0 from the old `Subtitle Fixer_0.1.0.0` folder, which was removed so only one copy of the plugin is present. SubSync was then updated from the settings page, to 1.3.0 and later the same day to 1.3.1 (1.3.0's `serve` hangs on Windows, M0-SPIKES S5).
- **First install 2026-11-11:** `Subtitle Fixer_0.1.0.0\`, installed from a local test repository. That is the folder name Jellyfin's own installer uses (`<name>_<version>`); the earlier hand-made `SubtitleFixer_0.1.0.0` folder was uninstalled. Its state database is `C:\ProgramData\Jellyfin\Server\plugins\Jellyfin.Plugin.SubtitleFixer\state.db`.
- **Other plugins:** Testflix also runs File Transformation 3.0.3.0, which the menu entry needs, plus JavaScript Injector, Jellyfin Enhanced and KefinTweaks. The `/CustomTabs/Config` 404s in the browser console on Jellyfin pages come from one of those, not from us.
- **Reinstall after a change:**
  1. Build the zip with `build\package.ps1`.
  2. Exit the tray app and wait for `jellyfin.exe` to exit.
  3. Run `Expand-Archive artifacts\SubtitleFixer_<ver>.zip -DestinationPath 'C:\ProgramData\Jellyfin\Server\plugins\Subtitle Fixer_<ver>' -Force`.
- **Test a repository install:** serve `manifest.json` (from `build\manifest.ps1` with a `http://localhost:<port>/…zip` source URL) and the zip from a local folder, add `http://localhost:<port>/manifest.json` under Dashboard > Plugins > Repositories, install from the catalog, restart, then remove the test repository. Done 2026-11-11: the catalog listed the plugin, the checksum matched, and it loaded as Active.
  4. Start `C:\Program Files\Jellyfin\Server\jellyfin-windows-tray\Jellyfin.Windows.Tray.exe` again.
- **Sign-in:** a test admin account and a non-admin test account exist. Ask the owner, and never write the passwords into the repo.
