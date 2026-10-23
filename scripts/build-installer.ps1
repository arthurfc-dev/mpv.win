[CmdletBinding()]
param(
    [string] $ApplicationDirectory,
    [string] $OutputDirectory,
    [string] $Compiler
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pins = Get-Content (Join-Path $root 'dependencies/installer.json') -Raw | ConvertFrom-Json
if (-not $ApplicationDirectory) { $ApplicationDirectory = Join-Path $root '.local/release-app' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/setup' }
$ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path (Join-Path $ApplicationDirectory 'mpv-win.exe'))) {
    throw 'Build the Release app first with scripts/build.ps1 -Configuration Release -OutputDirectory .local/release-app'
}
$dependency = Get-Content (Join-Path $root 'dependencies/libmpv.json') -Raw | ConvertFrom-Json
if ((Get-FileHash (Join-Path $ApplicationDirectory 'libmpv-2.dll')).Hash -ne $dependency.dllSha256) {
    throw 'Application directory does not contain the pinned playback engine.'
}
foreach ($relative in @(
    'GCC/16.2.0/COPYING3',
    'GCC/16.2.0/COPYING.RUNTIME',
    'Microsoft.Windows.SDK.NET.Ref/10.0.26100.57/LICENSE.rtf'
)) {
    $original = Join-Path $root ('dependencies/licenses/' + $relative)
    $packaged = Join-Path $ApplicationDirectory ('licenses/' + $relative)
    if (-not (Test-Path -LiteralPath $packaged) -or (Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath $packaged).Hash) {
        throw ('Rebuild the application with the current license payload: ' + $relative)
    }
}
$sourceInventory = Join-Path $ApplicationDirectory 'licenses/inventory/libmpv-sources.json'
if (-not (Test-Path -LiteralPath $sourceInventory) -or (Get-FileHash -LiteralPath $sourceInventory).Hash -ne (Get-FileHash (Join-Path $root 'dependencies/libmpv-sources.json')).Hash) {
    throw 'Rebuild the application with the current corresponding-source download links.'
}
$noticeRoot = Join-Path $ApplicationDirectory 'licenses/libmpv-build'
$noticeManifest = Get-Content (Join-Path $noticeRoot 'notice-inventory.json') -Raw | ConvertFrom-Json
$noticeEvidence = Get-Content (Join-Path $noticeRoot 'collection-evidence.json') -Raw | ConvertFrom-Json
foreach ($file in @($noticeManifest.files) + @($noticeEvidence.additionalFiles) + @($noticeEvidence.inlineNotices)) {
    $path = [IO.Path]::GetFullPath((Join-Path $noticeRoot $file.path))
    if (-not $path.StartsWith($noticeRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe notice manifest path.' }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw ('Missing or changed dependency notice: ' + $file.path) }
}
$downloads = Join-Path $root '.local/downloads'
[void][IO.Directory]::CreateDirectory($downloads)

function Get-VerifiedFile($Url, $Path, $Algorithm, $Hash) {
    if ((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -eq $Hash) { return }
    Invoke-WebRequest -Uri $Url -OutFile ($Path + '.download') -UseBasicParsing -TimeoutSec 180
    if ((Get-FileHash -LiteralPath ($Path + '.download') -Algorithm $Algorithm).Hash -ne $Hash) {
        throw ('Download checksum mismatch: ' + [IO.Path]::GetFileName($Path))
    }
    Move-Item -LiteralPath ($Path + '.download') -Destination $Path -Force
}

if (-not $Compiler) {
    $toolDirectory = Join-Path $root ('.local/tools/inno-' + $pins.innoSetup.version)
    $Compiler = Join-Path $toolDirectory 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $Compiler)) {
        $setup = Join-Path $downloads ('innosetup-' + $pins.innoSetup.version + '-x64.exe')
        Get-VerifiedFile $pins.innoSetup.url $setup SHA256 $pins.innoSetup.sha256
        # Official Inno Setup portable mode: no installed-program registration or shortcuts.
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1', ('/DIR="' + $toolDirectory + '"'))
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw ('Compiler extraction failed: ' + $process.ExitCode) }
    }
}
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Inno Setup compiler not found.' }
$runtime = Join-Path $downloads ('dotnet-runtime-' + $pins.dotnetRuntime.version + '-win-x64.exe')
Get-VerifiedFile $pins.dotnetRuntime.url $runtime SHA512 $pins.dotnetRuntime.sha512

# Stage a new directory each time. Symbols and development import libraries do not ship.
$stage = Join-Path $root ('.local/installer-stage-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
Get-ChildItem -LiteralPath $ApplicationDirectory -Recurse -File | ForEach-Object {
    if ($_.Extension -in @('.pdb', '.lib', '.exp', '.ilk')) { return }
    $relative = $_.FullName.Substring($ApplicationDirectory.Length).TrimStart('\')
    $destination = Join-Path $stage $relative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    Copy-Item -LiteralPath $_.FullName -Destination $destination
}
[xml]$project = Get-Content (Join-Path $root 'src/Player.App/Player.App.csproj')
$version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$agreement = Join-Path $root ('.local/setup-license-' + [Guid]::NewGuid().ToString('N') + '.txt')
$sdkTerms = Join-Path $root 'dependencies/licenses/Microsoft.Windows.SDK.NET.Ref/10.0.26100.57/LICENSE.rtf'
$terms = "mpv.win source and application: GNU General Public License version 3. Microsoft Windows App SDK components are supplied under their separate Microsoft terms reproduced below. Windows SDK component terms are shown on the preceding page and retained in the application's licenses folder, together with individual dependency notices. These component terms do not replace the application's GPL license.`r`n`r`n"
$terms += [IO.File]::ReadAllText((Join-Path $root 'LICENSE'))
$terms += "`r`n`r`nMICROSOFT WINDOWS APP SDK COMPONENTS`r`n`r`n"
$terms += [IO.File]::ReadAllText((Join-Path $root 'dependencies/licenses/Microsoft.WindowsAppSDK.WinUI/2.3.9/license.txt'))
[IO.File]::WriteAllText($agreement, $terms, [Text.UTF8Encoding]::new($true))
& $Compiler /Q ('/DAppSource=' + $stage) ('/DRuntimeInstaller=' + $runtime) ('/DOutputPath=' + $OutputDirectory) ('/DAppVersion=' + $version) ('/DLicenseAgreement=' + $agreement) ('/DSdkTerms=' + $sdkTerms) (Join-Path $root 'installer/mpv-win.iss')
if ($LASTEXITCODE -ne 0) { throw ('Installer compilation failed: ' + $LASTEXITCODE) }
$installer = Join-Path $OutputDirectory ('mpv-win-' + $version + '-windows-x64-setup.exe')
$checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($installer + '.sha256'), $checksum + '  ' + [IO.Path]::GetFileName($installer) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host ('Installer: ' + $installer)
