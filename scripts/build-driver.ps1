<#
.SYNOPSIS
  Builds the DroidMic virtual audio driver package (DroidMicVAD.sys/.inf/.cat).
  Requires Visual Studio 2022 (C++ workload) + Windows SDK + WDK.
  Local builds are TEST-SIGNED. See docs/DRIVER_INSTALLATION.md for release signing.
.PARAMETER VerifyOnly
  Run InfVerif on an existing build instead of building.
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $VerifyOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'driver/VirtualAudioDevice/DroidMicVAD.vcxproj'
$pkgDir = Join-Path $root "driver/VirtualAudioDevice/x64/$Configuration/DroidMicVAD"
$out = Join-Path $root 'dist/windows/app/driver'

function Find-Tool($name) {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
}

if ($VerifyOnly) {
    $infverif = Find-Tool 'infverif.exe'
    if (-not $infverif) { throw 'InfVerif not found (install the WDK).' }
    & $infverif.FullName /w /v (Join-Path $pkgDir 'DroidMicVAD.inf')
    if ($LASTEXITCODE) { throw 'InfVerif reported problems' }
    Write-Host 'InfVerif: OK'
    return
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio 2022 with the C++ workload.' }
if (-not (Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\build")) {
    throw 'The Windows Driver Kit (WDK) is not installed. See docs/BUILD_WINDOWS.md.'
}

& $msbuild $proj /p:Configuration=$Configuration /p:Platform=x64 /m /nologo /v:minimal
if ($LASTEXITCODE) { throw 'Driver build failed' }

New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $pkgDir '*') $out -Force
# The test certificate WDK used to sign this build, so developers can trust it explicitly.
$cer = Get-ChildItem (Join-Path $root "driver/VirtualAudioDevice/x64/$Configuration") -Filter *.cer -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cer) { Copy-Item $cer.FullName (Join-Path $out 'DroidMicTestCert.cer') -Force }
Set-Content (Join-Path $out 'signing.txt') 'test'
Write-Host "Driver package (TEST-SIGNED): $out"
