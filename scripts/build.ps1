[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $NoRestore,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'restore-libmpv.ps1')
& (Join-Path $PSScriptRoot 'restore-dependency-notices.ps1')
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio with the x64 C++ build tools is required.'
}

$installationPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installationPath) {
    throw 'No Visual Studio installation with the x64 C++ build tools was found.'
}

$msbuild = Join-Path $installationPath 'MSBuild\Current\Bin\MSBuild.exe'
$buildArguments = @((Join-Path $projectRoot 'src\Player.App\Player.App.csproj'), '/m', '/nologo', "/p:Configuration=$Configuration", '/p:Platform=x64', '/verbosity:minimal')
if ($OutputDirectory) {
    $outputPath = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/') + '\'
    $buildArguments += @("/p:OutputPath=$outputPath", '/p:AppendTargetFrameworkToOutputPath=false', '/p:AppendRuntimeIdentifierToOutputPath=false')
}
if (-not $NoRestore) {
    $buildArguments += '/restore'
}

& $msbuild @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}
