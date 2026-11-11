# Subtitle Fixer for Jellyfin

Fix out-of-sync English subtitles from inside Jellyfin. Pick seasons, episodes or movies, and the plugin uses [SubSync](https://github.com/darkbepo/SubSync) to move every subtitle line to where it is actually spoken. It then refreshes the item, so the fixed subtitle is served right away. If an item has no subtitle at all, users can also find one through Bazarr, and it is re-timed as soon as it arrives.

> Only fixes English subtitles on English audio.

## Requirements
- **Jellyfin 12** on a **Windows** server. SubSync is Windows-only, so Linux, Docker and macOS servers aren't supported.
- The **File Transformation** plugin, which adds the menu entry. Install it first.
- **SubSync**, installed on the same server in a folder the account Jellyfin runs as can write to (not `Program Files`). Download it from its [releases](https://github.com/darkbepo/SubSync/releases), then run `subsync-cli setup` once in that folder. Setup downloads about 1.8 GB on a server without an NVIDIA card, and more with one (the CUDA libraries).
- An NVIDIA graphics card is strongly recommended (see below). It also works on CPU, just more slowly.
- Optional: **Bazarr**, if you want the Find subtitles button.

## How long it takes
SubSync listens to the whole soundtrack (Whisper speech recognition) to place each line, so time grows with the video's length, and the graphics card matters most. Rough estimates per item:

| Hardware | 45-min episode | 2-hour movie | 10-episode season |
|---|---|---|---|
| High-end NVIDIA GPU (RTX 3080, 4070 or better) | about 1 min | 2 to 4 min | about 10 min |
| Entry NVIDIA GPU (GTX 1660, RTX 3050, 3060) | 1 to 2 min | 4 to 6 min | 15 to 25 min |
| Modern CPU only, 8+ cores (recent Core i7/i9, Ryzen 7/9) | 10 to 15 min | 30 to 40 min | 2 to 2.5 hours |
| Older or low-power CPU (4 cores, mini PC, NAS) | 30 to 45 min | 1.5 to 3 hours | most of a day |

- Only two rows rest on measurements: an RTX 3050 (1 to 2 min per 45-minute episode), and an i7-13850HX on CPU (a 22-minute episode in about 6.5 minutes, roughly a third of real time). The rest are scaled from those, so expect your own numbers to differ.
- AMD and Intel graphics cards don't help: SubSync's GPU mode needs NVIDIA (CUDA), so those servers run at CPU speed.
- SubSync works on one item at a time, so a queue takes the sum of its items.
- The time limit per item adjusts itself: twice the video's length plus 10 minutes, or the **Minimum time limit** setting if that is longer (30 minutes by default). A 2-hour movie gets about 4 hours, so CPU-only servers need no change. If a run is stopped anyway, the subtitle is put back untouched.

## Install
1. Install **File Transformation** from its plugin repository.
2. In Jellyfin, open **Dashboard > Plugins > Repositories**, add this repository and save:
   ```
   https://github.com/darkbepo/Jellyfin-Subfixer/releases/latest/download/manifest.json
   ```
3. Open **Catalog**, install **Subtitle Fixer**, and restart Jellyfin.
4. Open **Dashboard > Subtitle Fixer** (in the plugin section of the dashboard sidebar). Set the **SubSync folder**, save, and press **Test SubSync**. It should say "SubSync is ready".

To install by hand instead, stop Jellyfin and unzip `SubtitleFixer_<version>.zip` from the [releases](https://github.com/darkbepo/Jellyfin-Subfixer/releases) into a new folder named `Subtitle Fixer_<version>` inside Jellyfin's `plugins` folder (`C:\ProgramData\Jellyfin\Server\plugins` for a default install). Then start Jellyfin. Using the same folder name as the catalog means a later catalog update replaces it instead of leaving two copies.

## Settings
| Setting | Default | What it does |
|---|---|---|
| SubSync folder | empty | Where `subsync-cli.exe` is. Until it is set, only administrators can open Subtitle Fixer |
| Tidy up lines | on | Re-cuts subtitle lines by sentence and speaker. Words are never changed |
| Force CPU | off | Skip the GPU. Use it only if CUDA fails |
| Who can use it | Everyone signed in | Or administrators only |
| Per request | 150 | Most episodes or movies one Start may queue |
| Queue size | 300 | Most items waiting in the shared queue |
| Minimum time limit | 30 min | See "How long it takes" |
| Bazarr | off | Address, API key and optional path mappings (when Bazarr sees the media under different paths). **Test Bazarr** checks them before you save |

Changes apply as soon as you save; no restart.

## Use
Open the avatar menu (top right) and choose **Subtitle Fixer**. It opens as a window over Jellyfin. Esc or ✕ closes it, and Ctrl-click opens it as a full page.

- Search or browse to a series, tick seasons or episodes, and press **Start**. You can pick several seasons of one series, but not several series at once. Movies can be picked together, but not mixed with episodes.
- Before you start, the footer shows how many items will be fixed and how many are skipped (already fixed, no English audio, no subtitle).
- There is one shared queue, and SubSync works on one item at a time. Everyone sees what is running and roughly when theirs is due, and can cancel their own items.
- **Find subtitles** (when Bazarr is set up) appears only on episodes and movies that have no English subtitle. It lists what Bazarr's providers have; pick one, and it is downloaded and queued for re-timing.

## What it touches
Only `<video>.en.srt`, `<video>.eng.srt`, `<video>.en.hi.srt` or `<video>.eng.hi.srt` next to an episode or movie with English audio, one per item (the first that exists, in that order). Embedded subtitles and other languages are left alone.

- Before each change the plugin copies the subtitle to a `.bak` next to it, and puts it back on any failure, cancel or time-out. If Jellyfin stops mid-run, the backup is restored on the next start.
- Items already fixed are skipped. Replacing the video file makes it eligible again.
- Find subtitles adds a subtitle only where there was none.

## Troubleshooting
- **"Subtitle Fixer isn't set up yet":** an administrator still has to set and save the SubSync folder.
- **Test SubSync says it's not fully installed:** run `subsync-cli setup` in the SubSync folder on the server.
- **The queue stopped with a SubSync message:** close any open SubSync window, or let its setup finish, then press Start again.
- **"Crashed 3 times in a row":** the queue stops itself. SubSync's output for each failure is in the Jellyfin log.
- **GPU or CPU:** Test SubSync shows which one the last run used. If a server with an NVIDIA card says CPU, check that the account Jellyfin runs as can use the card, or turn on Force CPU if CUDA keeps failing.
- **Test Bazarr fails:** the message names the exact address it tried. Use Bazarr's own address, as in its browser tab but without any page path (for example `http://server:6767/`). Add Bazarr's URL Base if it has one (Bazarr > Settings > General). If Bazarr's page can be read, the message suggests the right address.
- **The settings page looks old after an update:** reload the page (Ctrl+F5) so the browser fetches the new version.
- **Find subtitles says the item isn't found:** Bazarr sees the files under different paths. Add a path mapping in the Bazarr settings and press Test Bazarr.
- **Logs:** the Jellyfin log (one line per finished item), and the SubSync `logs` folder (`cli.log`, `worker.log`).

## Releasing (maintainers)
Raise `<Version>` in `Directory.Build.props`, push to `main`, then run:
```
powershell -ExecutionPolicy Bypass -File build\release.ps1 -Changelog "What changed."
```
The script checks the tree is clean and pushed, runs the tests, builds the zip, and writes `manifest.json`, which keeps every earlier version. It then tags the release and publishes it with `gh`, under your own account. `-DryRun` does everything except the tag and the release.

## License
Free and open source under the [GNU General Public License v3.0](LICENSE), like Jellyfin's official plugin template and most Jellyfin plugins. SubSync is a separate project under the MIT license and is not included.
