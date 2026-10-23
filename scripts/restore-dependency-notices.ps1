[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pin = Get-Content (Join-Path $root 'dependencies/libmpv-notices.json') -Raw | ConvertFrom-Json
$destination = Join-Path $root '.local/libmpv-notices'
$stamp = Join-Path $destination '.archive-sha256'
function Test-Notices {
    $manifest = Join-Path $destination 'notice-inventory.json'
    $evidence = Join-Path $destination 'collection-evidence.json'
    if (-not (Test-Path -LiteralPath $manifest) -or -not (Test-Path -LiteralPath $evidence)) { return $false }
    $inventory = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $extra = Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
    foreach ($file in @($inventory.files) + @($extra.additionalFiles) + @($extra.inlineNotices)) {
        $path = [IO.Path]::GetFullPath((Join-Path $destination $file.path))
        if (-not $path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe notice manifest path.' }
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { return $false }
    }
    return $inventory.files.Count -eq $pin.noticeFileCount
}
if ((Test-Path -LiteralPath $stamp) -and (Get-Content -LiteralPath $stamp -Raw).Trim() -eq $pin.sha256 -and (Test-Notices)) { return }
$archive = Join-Path $root '.local/downloads/libmpv-notices.zip'
[void][IO.Directory]::CreateDirectory((Split-Path $archive -Parent))
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $pin.sha256) {
    $headers = @{ 'User-Agent' = 'mpv.win-build' }
    $url = $pin.url
    if ($env:GITHUB_TOKEN) {
        $headers.Authorization = 'Bearer ' + $env:GITHUB_TOKEN
        $headers.Accept = 'application/octet-stream'
        $url = $pin.apiUrl
    }
    try { Invoke-WebRequest -Uri $url -Headers $headers -OutFile ($archive + '.download') -UseBasicParsing -TimeoutSec 180 }
    finally { $headers.Clear() }
    if ((Get-FileHash -LiteralPath ($archive + '.download')).Hash -ne $pin.sha256) { throw 'Dependency notice archive checksum mismatch.' }
    Move-Item -LiteralPath ($archive + '.download') -Destination $archive -Force
}
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
if (-not (Test-Notices)) { throw 'Extracted dependency notices failed verification.' }
[IO.File]::WriteAllText($stamp, $pin.sha256)
