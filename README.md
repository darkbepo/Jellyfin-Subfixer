# Subtitle Fixer for Jellyfin

Fix out-of-sync English subtitles from inside Jellyfin. Pick seasons or episodes, and the plugin uses [SubSync](https://github.com/darkbepo/SubSync) to move every subtitle line to where it is actually spoken, then refreshes the item so the fixed subtitle is served right away.

> Only fixes English subtitles on English audio.

## Requirements
- Jellyfin 12 (Windows server).
- The **File Transformation** plugin (adds the menu entry).
- SubSync installed in a folder the Jellyfin service account can write to (not `Program Files`); first start downloads about 3.2 GB. An NVIDIA GPU is strongly recommended.

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
