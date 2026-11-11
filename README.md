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

## What it touches
Only `<video>.en.srt` or `<video>.eng.srt` next to an episode with English audio. Before each change the plugin keeps a temporary backup and restores it on any problem. Episodes already fixed are skipped.

## Troubleshooting
- "SubSync is not configured": set and test the folder in plugin settings.
- Queue stopped with a SubSync message: close the SubSync window, finish its setup, start again.
- Logs: the SubSync `logs` folder (`cli.log`, `worker.log`) and the Jellyfin log.

License: to be decided by the owner. SubSync is MIT licensed.
