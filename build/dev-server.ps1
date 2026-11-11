<#
.SYNOPSIS
  Serves the page from src\...\Web for UI work in mock mode, at the same paths the plugin uses:
  http://localhost:<port>/SubtitleFixer/Page?mock=1
.DESCRIPTION
  Static files only; no Jellyfin, no API. Open the page with ?mock=1 so it uses mock-data.js. Files are read on
  every request, so edits show up on reload. Localhost only.
#>
param(
    [int]$Port = 5178
)

$ErrorActionPreference = 'Stop'
$web = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Jellyfin.Plugin.SubtitleFixer\Web'
$types = @{ '.html' = 'text/html'; '.js' = 'application/javascript'; '.css' = 'text/css' }

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "Serving $web at http://localhost:$Port/SubtitleFixer/Page?mock=1"

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $path = $context.Request.Url.AbsolutePath
        $file = switch -Regex ($path) {
            '^/SubtitleFixer/Page/?$' { 'index.html' }
            '^/SubtitleFixer/Page/([A-Za-z0-9._-]+)$' { $Matches[1] }
            '^/SubtitleFixer/menu\.js$' { 'menu.js' }
            default { $null }
        }
        $full = if ($file) { Join-Path $web $file } else { $null }
        $response = $context.Response
        if ($full -and (Test-Path -LiteralPath $full -PathType Leaf)) {
            $bytes = [System.IO.File]::ReadAllBytes($full)
            $ext = [System.IO.Path]::GetExtension($full)
            $response.ContentType = "$($types[$ext]); charset=utf-8"
            $response.Headers['Cache-Control'] = 'no-cache'
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
        } else {
            $response.StatusCode = 404
        }
        $response.Close()
        Write-Host "$($context.Request.HttpMethod) $path -> $($response.StatusCode)"
    }
} finally {
    $listener.Stop()
}
