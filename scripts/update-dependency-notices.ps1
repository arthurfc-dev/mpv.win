[CmdletBinding()]
param([string]$NuGetRoot = $env:NUGET_PACKAGES)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
if (-not $NuGetRoot) { $NuGetRoot = Join-Path $env:USERPROFILE '.nuget\packages' }
$NuGetRoot = [IO.Path]::GetFullPath($NuGetRoot)
$lock = Get-Content -LiteralPath (Join-Path $root 'src\Player.App\packages.lock.json') -Raw | ConvertFrom-Json
$packages = @{}
foreach ($target in $lock.dependencies.PSObject.Properties) {
    foreach ($package in $target.Value.PSObject.Properties) {
        $packages[$package.Name + '/' + $package.Value.resolved] = $package.Value
    }
}
$inventory = @()
foreach ($key in ($packages.Keys | Sort-Object)) {
    $id, $version = $key.Split('/')
    $packageRoot = Join-Path $NuGetRoot ($key.ToLowerInvariant())
    $nuspecPath = Join-Path $packageRoot ($id.ToLowerInvariant() + '.nuspec')
    if (-not (Test-Path -LiteralPath $nuspecPath)) { throw "Restore the locked package first: $key" }
    [xml]$nuspec = [IO.File]::ReadAllText($nuspecPath)
    $metadata = $nuspec.package.metadata
    if ($metadata.id -ne $id -or $metadata.version -ne $version) { throw "Package metadata does not match $key" }
    $packageArchive = Join-Path $packageRoot ($id.ToLowerInvariant() + '.' + $version + '.nupkg')
    $hash = [Security.Cryptography.SHA512]::Create()
    $packageStream = [IO.File]::OpenRead($packageArchive)
    try { $actualHash = [Convert]::ToBase64String($hash.ComputeHash($packageStream)) }
    finally { $packageStream.Dispose(); $hash.Dispose() }
    # NuGet's lock contentHash excludes a signed package's signature. It is not
    # the SHA-512 of the complete ZIP. Check those two recorded hashes separately.
    $archiveHash = ([IO.File]::ReadAllText($packageArchive + '.sha512')).Trim()
    $cacheMetadata = Get-Content -LiteralPath (Join-Path $packageRoot '.nupkg.metadata') -Raw | ConvertFrom-Json
    if ($actualHash -cne $archiveHash) { throw "Package archive differs from its restored checksum: $key" }
    if ($cacheMetadata.contentHash -cne $packages[$key].contentHash) { throw "Restored package content hash differs from the lock file: $key" }
    $destination = Join-Path $root ('dependencies\licenses\' + $key)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $texts = @()
    # Root license/notice files are the package's actual terms, not the possibly
    # different license of its public source repository. Keep WinUI tool notices too.
    $candidates = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object {
        $_.Name -match '(?i)^(license|licence|notice|third[-_]?party|sdk_license)'
    })
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    if ($licenseNode -and $licenseNode.type -eq 'file') {
        $declaredLicense = Join-Path $packageRoot $licenseNode.InnerText
        if (-not (Test-Path -LiteralPath $declaredLicense)) { throw "Declared package license is missing: $key" }
        $candidates += Get-Item -LiteralPath $declaredLicense
    }
    $toolNotice = Join-Path $packageRoot 'tools\NOTICE.txt'
    if (Test-Path -LiteralPath $toolNotice) { $candidates += Get-Item -LiteralPath $toolNotice }
    $archive = [IO.Compression.ZipFile]::OpenRead($packageArchive)
    try { foreach ($file in ($candidates | Sort-Object FullName -Unique)) {
        $relative = $file.FullName.Substring($packageRoot.TrimEnd('\','/').Length).TrimStart('\','/')
        $targetFile = Join-Path $destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
        $entry = $archive.GetEntry($relative.Replace('\','/'))
        if (-not $entry) { throw "Notice is absent from the verified package archive: $key/$relative" }
        $inputStream = $entry.Open()
        $outputStream = [IO.File]::Create($targetFile)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
        $texts += ('licenses/' + $key + '/' + $relative.Replace('\','/'))
    } } finally { $archive.Dispose() }
    $licenseUrl = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
    if (-not $licenseNode -and $licenseUrl) {
        $linkFile = Join-Path $destination 'LICENSE-URL.txt'
        [IO.File]::WriteAllText($linkFile, $licenseUrl.InnerText + "`n", [Text.UTF8Encoding]::new($false))
        $texts += 'licenses/' + $key + '/LICENSE-URL.txt'
    }
    if (-not $texts.Count) { throw "No license or license reference was found for $key" }
    $inventory += [ordered]@{
        id = $id; version = $version; nugetContentHash = $packages[$key].contentHash
        archiveSha512 = $actualHash
        scope = $(if ($id -like 'Microsoft.Windows.SDK.BuildTools*') { 'build-tool' } else { 'app-local-runtime' })
        packageUrl = "https://www.nuget.org/packages/$id/$version"
        licenseDeclaration = $(if ($licenseNode) { $licenseNode.InnerText } else { $licenseUrl.InnerText })
        licenseTexts = @($texts)
    }
}
$result = [ordered]@{ schemaVersion = 1; source = 'src/Player.App/packages.lock.json'; packages = @($inventory) }
[IO.File]::WriteAllText((Join-Path $root 'dependencies\nuget-inventory.json'), ($result | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Collected notices for $($inventory.Count) locked NuGet packages."
