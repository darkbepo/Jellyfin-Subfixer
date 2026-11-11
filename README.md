# Subtitle Fixer for Jellyfin

Fix out-of-sync English subtitles from inside Jellyfin. Pick seasons or episodes, and the plugin uses [SubSync](https://github.com/darkbepo/SubSync) to move every subtitle line to where it is actually spoken, then refreshes the item so the fixed subtitle is served right away.

> Only fixes English subtitles on English audio.

## Requirements
- Jellyfin 12 (Windows server).
- The **File Transformation** plugin (adds the menu entry).
- SubSync installed in a folder the Jellyfin service account can write to (not `Program Files`); first start downloads about 3.2 GB. An NVIDIA GPU is strongly recommended.

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
1. Install File Transformation from its plugin repository.
2. Install Subtitle Fixer (repository or manual zip into `plugins/`), restart Jellyfin.
3. Dashboard > Plugins > Subtitle Fixer: set the SubSync folder, press Test SubSync.

## Use
Avatar menu > Subtitle Fixer. Search or browse to a series, tick seasons or episodes, press Start. You can pick several seasons of one series, but not several series at once.

Every signed-in user can use it by default (Dashboard > Plugins > Subtitle Fixer can limit it to administrators). Until an administrator has set the SubSync folder, only administrators see it. There is one shared queue: everyone sees what is being synced, and SubSync works on one episode at a time.

## What it touches
Only `<video>.en.srt`, `<video>.eng.srt`, `<video>.en.hi.srt` or `<video>.eng.hi.srt` next to an episode with English audio, one per episode (in that order). Before each change the plugin keeps a temporary backup and restores it on any problem. Episodes already fixed are skipped.

## Troubleshooting
- "SubSync is not configured": set and test the folder in plugin settings.
- Queue stopped with a SubSync message: close the SubSync window, finish its setup, start again.
- Logs: the SubSync `logs` folder (`cli.log`, `worker.log`) and the Jellyfin log.

## License
Free and open source under the [GNU General Public License v3.0](LICENSE), like Jellyfin's official plugin template and most Jellyfin plugins. SubSync is a separate project under the MIT license and is not included.
