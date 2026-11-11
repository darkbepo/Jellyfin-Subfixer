<#
.SYNOPSIS
  Builds the plugin and writes artifacts\SubtitleFixer_<version>.zip plus its MD5, ready to unzip into Jellyfin's plugins folder.
.DESCRIPTION
  The zip holds only our DLL and meta.json. Jellyfin, Newtonsoft and SQLite assemblies come from the server (D16, D17).
#>
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Jellyfin.Plugin.SubtitleFixer\Jellyfin.Plugin.SubtitleFixer.csproj'

dotnet build $project -c $Configuration -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$out = Join-Path $root "src\Jellyfin.Plugin.SubtitleFixer\bin\$Configuration\net10.0"
$dll = Join-Path $out 'Jellyfin.Plugin.SubtitleFixer.dll'
$version = (Get-Item $dll).VersionInfo.FileVersion

$staging = Join-Path $root "artifacts\SubtitleFixer_$version"
$zip = Join-Path $root "artifacts\SubtitleFixer_$version.zip"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item $dll $staging

$meta = [ordered]@{
    category    = 'General'
    changelog   = 'Test build for M0 live checks.'
    description = 'Re-times English subtitles on English-audio TV episodes with SubSync. Requires the File Transformation plugin and a SubSync install on the server.'
    guid        = 'aca97159-ff3b-469e-a552-db44d9b77806'
    name        = 'Subtitle Fixer'
    overview    = 'Fix out-of-sync English subtitles from inside Jellyfin.'
    owner       = 'darkbepo'
    targetAbi   = '12.0.0.0'
    timestamp   = (Get-Date).ToUniversalTime().ToString('o')
    version     = $version
    status      = 'Active'
    autoUpdate  = $false
    imagePath   = ''
    assemblies  = @('Jellyfin.Plugin.SubtitleFixer.dll')
}
# UTF-8 without BOM: Jellyfin's System.Text.Json rejects a BOM ("'0xEF' is an invalid start of a value")
# and Windows PowerShell 5.1's -Encoding utf8 always writes one.
[System.IO.File]::WriteAllText((Join-Path $staging 'meta.json'), ($meta | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
Set-Content -Path "$zip.md5" -Value $md5 -Encoding ascii

Write-Host "Wrote $zip"
Write-Host "MD5 $md5"
