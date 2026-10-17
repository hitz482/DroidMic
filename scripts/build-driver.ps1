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

# 1. Compile and link. Signing is done below with our own test certificate.
& $msbuild $proj /p:Configuration=$Configuration /p:Platform=x64 /p:SignMode=Off /m /nologo /v:minimal
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

# 4. Test-sign the driver and catalog with a local self-signed "DroidMic Test" certificate
#    (created once per machine in CurrentUser\My). Release builds are signed by Microsoft instead.
$subject = 'CN=DroidMic Test Signing (development only)'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Subject $subject -Type CodeSigningCert -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)
}
$signtool = Find-KitTool 'signtool.exe'
foreach ($file in @((Join-Path $out 'DroidMicVAD.sys'), $catalog)) {
    $sig = & $signtool sign /v /fd sha256 /s My /sha1 $cert.Thumbprint $file 2>&1
    $sig | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE) {
        if ($env:GITHUB_ACTIONS) { $sig | Where-Object { "$_".Trim() } | ForEach-Object { Write-Host "::error title=signtool::$_" } }
        throw "Signing $file failed"
    }
}
Export-Certificate -Cert $cert -FilePath (Join-Path $out 'DroidMicTestCert.cer') | Out-Null
Set-Content (Join-Path $out 'signing.txt') 'test'
Write-Host "Driver package (TEST-SIGNED): $out"
Get-ChildItem $out | Format-Table Name, Length
