<#
.SYNOPSIS
  Publishes a GitHub release of the version in Directory.Build.props, as whoever `gh` is signed in as.
.DESCRIPTION
  1. Checks: on main, clean, up to date with origin, tag v<version> not used yet.
  2. Builds, runs every test, packages the zip (package.ps1).
  3. Writes manifest.json: the previous release's manifest plus this version (manifest.ps1).
  4. Tags v<version>, pushes the tag, creates the release with the zip, its .md5 and manifest.json.
  Jellyfin's repository URL is then
  https://github.com/<owner>/<repo>/releases/latest/download/manifest.json, which always points at the newest
  release's manifest (and so lists every version). Run locally, not in Actions, so the tag and the release are
  the owner's own (D39).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\release.ps1 -Changelog "First release."
#>
param(
    [Parameter(Mandatory)] [string]$Changelog,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    # Native git writes progress to stderr; under "Stop" Windows PowerShell 5.1 would turn that into an error.
    function Invoke-Git {
        $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { $out = & git.exe @args 2>$null } finally { $ErrorActionPreference = $old }
        if ($LASTEXITCODE -ne 0) { throw "git $args failed" }
        $out
    }

    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1).Trim()
    $tag = "v$version"
    $repo = (& gh repo view --json nameWithOwner --jq .nameWithOwner)
    if ($LASTEXITCODE -ne 0 -or -not $repo) { throw 'gh is not signed in, or this is not a GitHub checkout.' }

    if ((Invoke-Git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Release from main.' }
    if (Invoke-Git status --porcelain) { throw 'Commit or stash your changes first.' }
    Invoke-Git fetch -q origin main --tags | Out-Null
    if ((Invoke-Git rev-parse HEAD) -ne (Invoke-Git rev-parse origin/main)) { throw 'main is not the same as origin/main. Push or pull first.' }
    $tagExists = $true
    try { Invoke-Git rev-parse -q --verify "refs/tags/$tag" | Out-Null } catch { $tagExists = $false }
    if ($tagExists) { throw "$tag already exists. Raise <Version> in Directory.Build.props." }

    dotnet test Jellyfin.Plugin.SubtitleFixer.slnx -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & (Join-Path $PSScriptRoot 'package.ps1') -Changelog $Changelog
    $zip = Join-Path $root "artifacts\SubtitleFixer_$version.zip"

    $work = Join-Path $root 'artifacts\release'
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force $work | Out-Null
    $previous = Join-Path $work 'previous-manifest.json'
    $ErrorActionPreference = 'Continue'
    & gh release download --repo $repo --pattern manifest.json --output $previous 2>$null
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { Write-Host 'No earlier release manifest; starting a new one.'; $previous = '' }

    $manifest = Join-Path $work 'manifest.json'
    $sourceUrl = "https://github.com/$repo/releases/download/$tag/SubtitleFixer_$version.zip"
    & (Join-Path $PSScriptRoot 'manifest.ps1') -Zip $zip -Version $version -SourceUrl $sourceUrl -Out $manifest -Changelog $Changelog -Previous $previous

    if ($DryRun) { Write-Host "Dry run: would tag $tag and release $zip with $manifest."; return }

    Invoke-Git tag -a $tag -m "Subtitle Fixer $version" | Out-Null
    Invoke-Git push -q origin $tag | Out-Null
    & gh release create $tag --repo $repo --title "Subtitle Fixer $version" --notes $Changelog --verify-tag $zip "$zip.md5" $manifest
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed. The tag is pushed; fix the problem and run gh release create by hand.' }
    Write-Host "Released $tag. Repository URL: https://github.com/$repo/releases/latest/download/manifest.json"
}
finally {
    Pop-Location
}
