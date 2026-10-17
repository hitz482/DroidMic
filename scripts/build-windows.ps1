<#
.SYNOPSIS
  Builds and tests the Windows side: desktop app (self-contained single-file
  DroidMic.exe), driver setup tool and headless receiver.
.EXAMPLE
  ./scripts/build-windows.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist/windows'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'windows/tests/DroidMic.Tests') -c $Configuration
    if ($LASTEXITCODE) { throw 'Tests failed' }
}

dotnet publish (Join-Path $root 'windows/DroidMic.Desktop') -c $Configuration -o (Join-Path $out 'app')
if ($LASTEXITCODE) { throw 'Desktop publish failed' }
dotnet publish (Join-Path $root 'windows/DroidMic.DriverSetup') -c $Configuration -o (Join-Path $out 'app/driver')
if ($LASTEXITCODE) { throw 'DriverSetup publish failed' }
dotnet publish (Join-Path $root 'windows/DroidMic.ReceiverCli') -c $Configuration -r win-x64 --self-contained -p:PublishSingleFile=true -o (Join-Path $out 'receiver-cli')
if ($LASTEXITCODE) { throw 'Receiver CLI publish failed' }

Get-ChildItem (Join-Path $out 'app') -Filter *.pdb -Recurse | Remove-Item
Write-Host "Windows app: $out/app/DroidMic.exe"
