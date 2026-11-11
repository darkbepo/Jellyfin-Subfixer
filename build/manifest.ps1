<#
.SYNOPSIS
  Writes a Jellyfin plugin repository manifest (manifest.json) that adds one version of Subtitle Fixer.
.DESCRIPTION
  Format verified against Jellyfin 12.4 (MediaBrowser.Model/Updates/PackageInfo.cs and VersionInfo.cs):
  an array of packages, each with versions[] of { version, changelog, targetAbi, sourceUrl, checksum, timestamp }.
  The installer downloads sourceUrl (must end in .zip), compares its MD5 with checksum, and unpacks it into
  plugins\<name>_<version> (Emby.Server.Implementations/Updates/InstallationManager.cs).
  Versions already in -Previous are kept; the same version is replaced. Newest first.
#>
param(
    [Parameter(Mandatory)] [string]$Zip,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$SourceUrl,
    [Parameter(Mandatory)] [string]$Out,
    [string]$Changelog = '',
    [string]$Previous = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'plugin-info.ps1')

$checksum = (Get-FileHash $Zip -Algorithm MD5).Hash.ToLowerInvariant()
$entry = [ordered]@{
    version   = $Version
    changelog = $Changelog
    targetAbi = $PluginInfo.targetAbi
    sourceUrl = $SourceUrl
    checksum  = $checksum
    timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
}

$versions = @($entry)
if ($Previous -and (Test-Path $Previous)) {
    $old = Get-Content $Previous -Raw -Encoding utf8 | ConvertFrom-Json
    $mine = @($old | Where-Object { $_.guid -eq $PluginInfo.guid })
    foreach ($v in @($mine | ForEach-Object { $_.versions })) {
        if ($v -and $v.version -ne $Version) { $versions += $v }
    }
}
$versions = @($versions | Sort-Object -Property @{ Expression = { [version]$_.version } } -Descending)

$package = [ordered]@{
    name        = $PluginInfo.name
    description = $PluginInfo.description
    overview    = $PluginInfo.overview
    owner       = $PluginInfo.owner
    category    = $PluginInfo.category
    guid        = $PluginInfo.guid
    versions    = $versions
}

# -InputObject keeps the top-level array (piping one item would unwrap it).
$json = ConvertTo-Json -InputObject @($package) -Depth 6
# UTF-8 without BOM, like meta.json (Jellyfin's System.Text.Json rejects a BOM).
[System.IO.File]::WriteAllText($Out, $json, [System.Text.UTF8Encoding]::new($false))
$null = $json | ConvertFrom-Json
Write-Host "Wrote $Out ($($versions.Count) version(s); $Version md5 $checksum)"
