<#
.SYNOPSIS
  Runs every automated test that works on the current machine.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet test (Join-Path $root 'windows/tests/DroidMic.Tests')
if ($LASTEXITCODE) { throw '.NET tests failed' }
Push-Location (Join-Path $root 'android')
try {
    $gradlew = if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.\gradlew.bat' } else { './gradlew' }
    & $gradlew -PcoreOnly=true :core:test --console=plain
    if ($LASTEXITCODE) { throw 'Kotlin tests failed' }
} finally { Pop-Location }
if (Get-Command bash -ErrorAction SilentlyContinue) {
    bash (Join-Path $root 'scripts/run-tests.sh') --interop-only
    if ($LASTEXITCODE) { throw 'Interop tests failed' }
}
Write-Host 'All tests passed.'
