<#
.SYNOPSIS
Builds the installer: tests, publishes the app, compiles the Inno Setup script.

.DESCRIPTION
The one local command that proves a release. Output is
dist\WindowInvert-Setup-<version>.exe, where the version is read from
src\WindowInvert.App\WindowInvert.App.csproj so it is never typed twice.

Requires the .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).

.PARAMETER SkipTests
Skip dotnet test. For iterating on the installer script only.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\WindowInvert.App\WindowInvert.App.csproj'
$publishDir = Join-Path $root 'dist\win-x64'
$script = Join-Path $root 'installer\WindowInvert.iss'

function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE" }
}

# A running copy holds dist\ open and the publish fails, or worse, half succeeds
# and a relaunch runs the stale binary. Refuse rather than guess.
if (Get-Process -Name 'WindowInvert.App' -ErrorAction SilentlyContinue) {
    throw 'Window Invert is running. Exit it from the tray menu first.'
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 not found. Install it with: winget install --id JRSoftware.InnoSetup -e'
}

$version = (& dotnet msbuild $project -getProperty:Version).Trim()
Assert-LastExitCode 'Reading the version'
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected version '$version' in $project" }
Write-Host "Version $version"

if (-not $SkipTests) {
    & dotnet test (Join-Path $root 'WindowInvert.sln') --nologo
    Assert-LastExitCode 'dotnet test'
}

if (Test-Path $publishDir) {
    if ($publishDir -notlike "$root\dist\*") { throw "Refusing to clear unexpected path: $publishDir" }
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $publishDir --nologo
Assert-LastExitCode 'dotnet publish'

& $iscc "/DAppVersion=$version" /Q $script
Assert-LastExitCode 'ISCC'

$setup = Join-Path $root "dist\WindowInvert-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "ISCC reported success but $setup does not exist" }
Write-Host "Installer: $setup"
