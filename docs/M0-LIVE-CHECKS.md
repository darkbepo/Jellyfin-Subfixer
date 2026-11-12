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

### Libraries setting, auto-clear and failed-syncs log (D43, D44)
On the settings page, turn off one TV library (leave at least one on) and save.
**Record:**
- The page no longer lists that library, and searching for a series that's only in it finds nothing (this is the check for the per-library search in `M0-SPIKES.md` S3). A series in an allowed library is still found.
- A season from the turned-off library can't be queued: `POST /SubtitleFixer/Jobs` with its id returns `404 not_found` or `400`.
- Turn every library back on and save: the settings page shows all switches on and search finds everything again.
- Start one episode. About a minute after it finishes, it leaves the queue list. If it ended failed or check, it's in the Failed syncs panel, and still there after restarting Jellyfin. Retry puts it back in the queue.

### SubSync 1.3.0, Phase 1 (M7)
Install SubSync 1.3.0 over 1.2.0 (same folder), then on the settings page press Test SubSync: it shows `SubSync 1.3.0`.
**Record:**
- A `.en.hi.srt` episode (put a copy of some other subtitle next to it as `.en.srt`) syncs in place: the `.en.hi.srt` changes, the `.en.srt` is byte-identical, no `.bak` is left.
- During a long episode the bar moves in steps while SubSync listens, never backwards, and the line shows a percentage from the start.
- After that sync, Test SubSync says "Last sync: GPU (<card>)".
- Retry a no-match item from the Failed syncs panel (it finishes in seconds from SubSync's cache), then start a fresh episode: its first estimate (`expectedSeconds` in `/SubtitleFixer/Status`, or the "about N min left" line) is still about as long as a real run, not a few seconds.
- SubSync's `logs\cli.log` for one run: the `speech model ready after …` and `read audio … total …` lines, and the per-item seconds in the Jellyfin log.

### SubSync 1.3.0, Phase 2: one SubSync for the queue (D45)
**Record:**
- Start a whole season. In Task Manager (Details, add the "Command line" column) there is one `subsync-cli.exe serve --overwrite` for the whole season, not one per episode, and it is gone a few seconds after the last episode. The Jellyfin log ends the run with "queue run finished: N item(s) through 1 SubSync process(es) in S s".
- While an episode is listening, the next one already shows "syncing" on the page; at most two do.
- Season time: S from that log line, against the same season with Phase 1 (one SubSync per episode; to measure that, run the season on Phase 1's build, or add up the per-item seconds in the Jellyfin log of a Phase 1 run). Note GPU or CPU.
- Cancel mid-season (while an episode is listening, so the next one is already in SubSync): every subtitle of the season is byte-identical to before for the unfinished episodes (compare with copies), no `.bak` is left, and the queue shows the cancelled items. As a non-admin with another user's episode in SubSync, cancel your own: the other user's episode runs again afterwards.
- Change Tidy up lines mid-season and save: the episode in progress finishes, then a new `serve` with `--keep-layout` appears.
- Stop the Jellyfin service mid-season: no `subsync-cli.exe` or `python.exe` is left, and after a restart every subtitle is intact.

### SubSync update from the settings page (D46)
Needs a SubSync release newer than the installed one. To test before one exists, install the previous release (v1.2.0) into a copy of the SubSync folder and point the setting at that copy.
**Record:**
- The SubSync card says "SubSync 1.2.0 installed · 1.3.0 available", with What's new and the release link.
- With the SubSync window open, Update ends with "SubSync is running from that folder…" and nothing changed.
- With the window closed and the queue empty, Update runs through downloading, checking, installing and setup, and ends with "SubSync 1.3.0 is installed and ready". Meanwhile Start on the main page answers "SubSync is being updated". Afterwards Test SubSync shows `SubSync 1.3.0`, there are no `.old` files or `.subfixer-update` folder, and a sync works.
- Whether the zip has the exes at its top level or in a folder (the updater accepts both; record which).
