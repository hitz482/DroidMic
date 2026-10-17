# Building the Android app

## Prerequisites

* JDK 17 (21 also works for the build itself)
* Android SDK with platform **35** and build-tools (Android Studio Ladybug or newer
  installs these; or `sdkmanager "platforms;android-35" "build-tools;35.0.0"`)
* `ANDROID_HOME` set, or `android/local.properties` containing `sdk.dir=...`

## Commands

```bash
cd android
./gradlew :app:assembleDebug          # -> app/build/outputs/apk/debug/app-debug.apk
./gradlew :app:installDebug           # install on a connected phone (USB debugging on)
./gradlew :core:test                  # protocol/session unit tests

# Release (minified). Signed only if these are set; nothing secret is in the repo:
export DROIDMIC_KEYSTORE=/path/release.jks DROIDMIC_KEYSTORE_PASSWORD=... \
       DROIDMIC_KEY_ALIAS=droidmic DROIDMIC_KEY_PASSWORD=...
./gradlew :app:assembleRelease
```

Without an Android SDK you can still build and test the protocol/session core and
run the phone simulator against a receiver:

```bash
./gradlew -PcoreOnly=true :core:test
./gradlew -PcoreOnly=true :core:simulatePhone --args="--host 192.168.1.20 --pin 123456 --seconds 10"
```

## Module layout

* `core/`: pure Kotlin/JVM: protocol, crypto, discovery, client, `StreamSession`
  (connect → stream → keep-alive → reconnect), meters and gain.
* `app/`: Android: `MicCaptureSource` (AudioRecord), `MicService` (foreground
  service, type microphone), `KeystorePairingRepository`, Compose UI (Home,
  Connect, Audio, Diagnostics).

Versions: AGP 8.7.3, Kotlin 2.1.21, Compose BOM 2024.12.01, minSdk 29 (Android 10),
target/compileSdk 35.
