<#
.SYNOPSIS
  Builds the Android APK. Requires JDK 17 and the Android SDK (ANDROID_HOME).
.PARAMETER Release
  Build a release APK. Signed only if DROIDMIC_KEYSTORE, DROIDMIC_KEYSTORE_PASSWORD,
  DROIDMIC_KEY_ALIAS and DROIDMIC_KEY_PASSWORD are set.
#>
param([switch] $Release)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $root 'android')
try {
    $gradlew = if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.\gradlew.bat' } else { './gradlew' }
    $task = if ($Release) { ':app:assembleRelease' } else { ':app:assembleDebug' }
    & $gradlew :core:test $task --console=plain
    if ($LASTEXITCODE) { throw 'Android build failed' }
    $dist = Join-Path $root 'dist/android'
    New-Item -ItemType Directory -Force $dist | Out-Null
    Get-ChildItem 'app/build/outputs/apk' -Recurse -Filter *.apk | Copy-Item -Destination $dist
    Write-Host "APK(s) in $dist"
} finally {
    Pop-Location
}
