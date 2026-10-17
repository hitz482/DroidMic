<#
.SYNOPSIS
  Produces versioned release artifacts in dist/:
    DroidMic-<ver>-windows-x64-setup.exe  (Inno Setup installer)
    DroidMic-<ver>-windows-x64.zip        (portable app + driver package)
  Run build-windows.ps1 and build-driver.ps1 first, or omit -SkipBuild.
#>
param([switch] $SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'windows/Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-windows.ps1') -Configuration Release
    & (Join-Path $PSScriptRoot 'build-driver.ps1') -Configuration Release
}
$app = Join-Path $root 'dist/windows/app'
if (-not (Test-Path (Join-Path $app 'DroidMic.exe'))) { throw 'Build the app first (build-windows.ps1).' }

$zip = Join-Path $root "dist/DroidMic-$version-windows-x64.zip"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -Force

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning 'Inno Setup 6 not found; skipping the installer (choco install innosetup).'
} else {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$app" "/O$(Join-Path $root 'dist')" (Join-Path $root 'windows/DroidMic.Installer/DroidMic.iss')
    if ($LASTEXITCODE) { throw 'Installer build failed' }
}
Get-ChildItem (Join-Path $root 'dist') -File | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Set-Content (Join-Path $root 'dist/SHA256SUMS.txt')
Write-Host "Release artifacts in $(Join-Path $root 'dist')"
