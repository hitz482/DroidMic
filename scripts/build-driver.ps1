<#
.SYNOPSIS
  Builds the DroidMic virtual audio driver package (DroidMicVAD.sys/.inf/.cat).
  Requires Visual Studio 2022 (C++ workload) + Windows SDK + WDK.
  The package is TEST-SIGNED. See docs/DRIVER_INSTALLATION.md for release signing.
.PARAMETER VerifyOnly
  Run InfVerif on an existing package instead of building.
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $VerifyOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'driver/VirtualAudioDevice'
$proj = Join-Path $src 'DroidMicVAD.vcxproj'
$outDir = Join-Path $src "x64/$Configuration"
$out = Join-Path $root 'dist/windows/app/driver'

function Find-KitTool($name) {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $tool = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) {
        $tool = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    }
    if (-not $tool) { throw "$name not found under $kits (install the Windows SDK and WDK)." }
    return $tool.FullName
}

if ($VerifyOnly) {
    $infverif = Find-KitTool 'infverif.exe'
    & $infverif /w /v (Join-Path $out 'DroidMicVAD.inf')
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

# 1. Compile and link (the WDK test-signs DroidMicVAD.sys and writes DroidMicVAD.cer).
& $msbuild $proj /p:Configuration=$Configuration /p:Platform=x64 /m /nologo /v:minimal
if ($LASTEXITCODE) { throw 'Driver build failed' }

# 2. Assemble the package.
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $outDir 'DroidMicVAD.sys') $out -Force
Copy-Item (Join-Path $src 'DroidMicVAD.inf') $out -Force

# 3. Catalog.
$inf2cat = Find-KitTool 'Inf2Cat.exe'
$catalog = Join-Path $out 'DroidMicVAD.cat'
Remove-Item $catalog -ErrorAction SilentlyContinue
$log = & $inf2cat "/driver:$out" '/os:10_X64' '/uselocaltime' '/verbose' 2>&1
$log | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -or -not (Test-Path $catalog)) {
    if ($env:GITHUB_ACTIONS) { $log | Where-Object { "$_".Trim() } | Select-Object -Last 40 | ForEach-Object { Write-Host "::error title=Inf2Cat::$_" } }
    throw 'Inf2Cat did not produce DroidMicVAD.cat'
}

# 4. Test-sign the catalog with the same WDK test certificate that signed the .sys.
$cerFile = Get-ChildItem $outDir -Filter *.cer -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $cerFile) { throw "WDK test certificate (.cer) not found in $outDir" }
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cerFile.FullName
$signtool = Find-KitTool 'signtool.exe'
& $signtool sign /fd sha256 /sha1 $cert.Thumbprint /s PrivateCertStore $catalog
if ($LASTEXITCODE) { throw 'Signing the catalog failed' }

Copy-Item $cerFile.FullName (Join-Path $out 'DroidMicTestCert.cer') -Force
Set-Content (Join-Path $out 'signing.txt') 'test'
Write-Host "Driver package (TEST-SIGNED): $out"
Get-ChildItem $out | Format-Table Name, Length
