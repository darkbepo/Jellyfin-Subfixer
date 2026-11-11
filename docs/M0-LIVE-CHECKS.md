# M0 live checks (owner, Windows server)

The source research in `M0-SPIKES.md` answered what it could. These checks need your Jellyfin 12 server, SubSync and the GPU. Part A needs nothing from this repo and can be done now. Part B needs the first test build of the plugin (comes with the M1 scaffold). Send back the values under **Record**, or paste them into the matching spike's Findings.

Run everything in PowerShell on the server. Pick one test episode that has an `.en.srt` sidecar and copy the video and subtitle into a test folder first, so nothing in the real library is touched.

## Setup
1. Dashboard > API Keys > add a key named `subfixer-test`. Then:
```powershell
$h = @{ Authorization = 'MediaBrowser Token="PASTE_KEY_HERE"' }
$base = 'http://localhost:8096'
```
2. Find the test episode's id: Jellyfin web, open the episode; the id is the `id=` value in the address bar.

## Part A: now, no plugin needed

### S0 Environment
```powershell
Invoke-RestMethod "$base/System/Info" -Headers $h | Select-Object Version, OperatingSystemDisplayName
& "C:\path\to\SubSync\subsync-cli.exe" version
& "C:\path\to\SubSync\subsync-cli.exe" check; "exit $LASTEXITCODE"
nvidia-smi --query-gpu=name,driver_version --format=csv
Get-Service -Name Jellyfin* | Select-Object Name, Status, StartType
```
**Record:** server version, SubSync version, `check` exit code, GPU name and driver, whether Jellyfin runs as a service or tray app, and the service account name (`services.msc` > Jellyfin > Log On).

### S4 `.bak` and refresh
1. Next to the test episode, copy `X.en.srt` to `X.en.srt.bak`. Run a library scan (Dashboard > Libraries > Scan). Open the episode's subtitle menu.
2. Get the subtitle stream index:
```powershell
$id = 'EPISODE_ID'
$item = Invoke-RestMethod "$base/Items/$id/PlaybackInfo" -Headers $h
$ms = $item.MediaSources[0]; $ms.Id
$ms.MediaStreams | Where-Object Type -eq 'Subtitle' | Select-Object Index, Language, IsExternal, Path
```
3. Fetch the served subtitle, edit the `.en.srt` on disk (change one line of text), fetch again without refreshing, and compare:
```powershell
Invoke-WebRequest "$base/Videos/$id/$($ms.Id)/Subtitles/INDEX/Stream.srt" -Headers $h -OutFile before.srt
# edit X.en.srt now
Invoke-WebRequest "$base/Videos/$id/$($ms.Id)/Subtitles/INDEX/Stream.srt" -Headers $h -OutFile after.srt
Compare-Object (Get-Content before.srt) (Get-Content after.srt)
```
4. Delete the `.bak` and restore the original `.srt`.

**Record:** does `.bak` appear anywhere in Jellyfin (expected: no)? Does `after.srt` show your edit without any refresh (expected: yes)?

### S5 SubSync CLI
Use the test copy in a folder whose path has spaces, for example `D:\Sub Sync Test\Show S01E01.mkv` with `Show S01E01.en.srt`.
```powershell
$cli = "C:\path\to\SubSync\subsync-cli.exe"
$v = "D:\Sub Sync Test\Show S01E01.mkv"; $s = "D:\Sub Sync Test\Show S01E01.en.srt"
$t = Measure-Command { & $cli sync --pair $v $s --overwrite --suffix en --report "D:\Sub Sync Test\report.json" }
"exit $LASTEXITCODE, $($t.TotalSeconds) s"; Get-Content "D:\Sub Sync Test\report.json"
```
Then:
- Run it again right away (expect nearly instant, from the transcript cache) and time it.
- Open the `.en.srt` in Notepad++ or another editor that keeps it open, run again, and note what happens.
- Start a run, wait 10 s, then kill it with `taskkill /T /F /IM subsync-cli.exe`, and check Task Manager for leftover `python`/`subsync` processes.

**Record:** exit codes, the full `report.json`, first-run and second-run seconds, behavior with the file held open, and whether any processes were left after the kill.

## Part B: after the first test build
Build the zip with `build\package.ps1` (or download `plugin-zip` from the GitHub Actions run). Stop Jellyfin, unzip it into a new folder `plugins\Subtitle Fixer_0.1.0.0\` (the name Jellyfin's installer uses, so a later repository update replaces it) in Jellyfin's data folder (next to the File Transformation plugin's folder), and start Jellyfin. Dashboard > Plugins > Subtitle Fixer: set the SubSync folder and save. The test page is the avatar-menu entry, or `http://<server>:8096/SubtitleFixer/Page`.

### S1 Menu entry
Check as an admin and a non-admin, on desktop width and on a phone (or a narrow window):
- "Subtitle Fixer" is directly below Profile in the avatar menu.
- After navigating to other pages and reopening the menu, it is still there.
- Clicking it closes the menu and opens the page.
- Dashboard > Plugins > disable Subtitle Fixer > restart: the entry is gone.

### S2 Auth
- On the page, the header shows your user name (from `GET /SubtitleFixer/Me`).
- Sign out of Jellyfin and reload the page: it shows the sign-in link.
- As a non-admin, the admin Test SubSync button is refused.

### S3 Timings
The test page shows the time for each request. **Record:** time to list a large season, time for a search of 3 letters and of a full show name, and whether a restricted user sees only their libraries.

### S6 Service account and GPU
With Jellyfin running as a service, use the test page's "Run SubSync check" button, which runs `subsync-cli check` and `version` as the service account. **Record:** GPU or CPU mode reported, and any access-denied errors for the SubSync folder or the media folder. If Jellyfin can also run as a tray app, repeat there.

### M2 real run (after S5)
Pick a test season whose subtitles you have copies of. On the test page: browse to it, tick the season, Start. Watch the queue panel.
**Record:**
- Per-episode time and whether the `.en.srt` files changed.
- Whether Jellyfin plays the new subtitle right away.
- That no `.bak` files are left in the season folder.
- What a second Start on the same season does (expected: every episode skipped as already synced).
Then cancel a run halfway and check that episode's subtitle is unchanged. Stop the Jellyfin service mid-episode, start it again, and check the subtitle was restored and the `.bak` is gone.

### S7 SQLite
The test page writes a row to `state.db` in the plugin's data folder (it shows the full path). Restart Jellyfin; the test page still shows that row.
