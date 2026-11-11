# Shared by package.ps1 (meta.json in the zip) and manifest.ps1 (repository manifest), so both always agree.
$PluginInfo = [ordered]@{
    guid        = 'aca97159-ff3b-469e-a552-db44d9b77806'
    name        = 'Subtitle Fixer'
    owner       = 'darkbepo'
    category    = 'General'
    overview    = 'Fix out-of-sync English subtitles from inside Jellyfin.'
    description = 'Re-times English subtitles on English-audio episodes and movies with SubSync, from a page in the avatar menu. Windows servers only. Requires the File Transformation plugin and a SubSync install on the server.'
    targetAbi   = '12.0.0.0'
}
