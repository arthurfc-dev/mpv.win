[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dependency = Get-Content -LiteralPath (Join-Path $projectRoot 'dependencies\libmpv.json') -Raw | ConvertFrom-Json
$destination = Join-Path $projectRoot '.local\libmpv'
$stampPath = Join-Path $destination '.archive-sha256'
function Test-ExtractedDependency {
    $dll = Join-Path $destination 'libmpv-2.dll'
    $header = Join-Path $destination 'include\mpv\render_d3d11.h'
    return ((Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $header) -and
        (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -eq $dependency.dllSha256 -and
        (Get-FileHash -LiteralPath $header -Algorithm SHA256).Hash -eq $dependency.renderApiHeaderSha256)
}
if ((Test-Path -LiteralPath $stampPath) -and
    ((Get-Content -LiteralPath $stampPath -Raw).Trim() -eq $dependency.sha256) -and
    (Test-ExtractedDependency)) {
    return
}

$archive = Join-Path $projectRoot '.local\downloads\libmpv.zip'
[void][System.IO.Directory]::CreateDirectory((Split-Path $archive -Parent))
if (-not (Test-Path -LiteralPath $archive) -or
    ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $dependency.sha256)) {
    Write-Host ('Downloading pinned libmpv ' + $dependency.version)
    $temporaryArchive = $archive + '.download'
    try {
        Invoke-WebRequest -Uri $dependency.url -OutFile $temporaryArchive -UseBasicParsing
    }
    catch {
        if (-not $dependency.mirror) { throw }
        Write-Host 'Using the authenticated pinned dependency download.'
        try {
            if ($env:GITHUB_TOKEN) {
                # GitHub Actions' read-only contents token can download private
                # release assets through the API. Never log or persist it.
                $headers = @{ Authorization = ('Bearer ' + $env:GITHUB_TOKEN); Accept = 'application/octet-stream'; 'User-Agent' = 'mpv.win-build' }
                try { Invoke-WebRequest -Uri $dependency.mirror.apiUrl -Headers $headers -OutFile $temporaryArchive -UseBasicParsing }
                finally { $headers.Clear() }
            }
            else {
                Invoke-WebRequest -Uri $dependency.mirror.url -OutFile $temporaryArchive -UseBasicParsing
            }
        }
        catch {
            throw 'Pinned libmpv could not be downloaded. While mpv.win is private, download its build-dependency release asset through GitHub and save it as .local/downloads/libmpv.zip, or provide a contents-read GITHUB_TOKEN for the build process. No newer binary will be substituted.'
        }
    }
    if ((Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash -ne $dependency.sha256) {
        throw 'libmpv archive checksum mismatch. Do not use this download.'
    }
    Move-Item -LiteralPath $temporaryArchive -Destination $archive -Force
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $dependency.sha256) {
    throw 'libmpv archive checksum mismatch. Do not use this download.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
if (-not (Test-ExtractedDependency)) {
    throw 'Extracted libmpv DLL or D3D11 render header differs from the pinned checksum.'
}
[System.IO.File]::WriteAllText($stampPath, $dependency.sha256)
