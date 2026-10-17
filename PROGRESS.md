# Progress

Last updated: 2026-10-17. Build environment: Linux cloud container (Ubuntu 24.04,
4 vCPU). **No Windows machine, Android device, Android SDK or WDK was available.**
Google's Maven/SDK hosts were blocked by the network policy.

## Milestone status

| # | Milestone | Status |
|---|-----------|--------|
| 1 | Feasibility, architecture | ✅ Done: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (decision record and risks) |
| 2 | Network prototype (protocol, capture, receive, levels) | ✅ Done and tested end to end, Kotlin ↔ C# |
| 3 | Virtual microphone | 🟡 Driver, INF, installer tool and endpoint self-test written; **not compiled or run on Windows yet** |
| 4 | Android app | 🟡 Complete source. Framework-facing files type-check against the Android 15 API; **full APK not built here** (no SDK) |
| 5 | Windows app | 🟡 Complete. Compiles and publishes to `DroidMic.exe` (x64, self-contained). **Not run on Windows yet** |
| 6 | Testing and optimisation | 🟡 Automated tests pass. Hardware tests and latency measurement not run |
| 7 | Packaging | 🟡 Scripts, Inno Setup installer and CI ready; release signing not done |

## Test results (executed in this environment)

| Suite | Result |
|-------|--------|
| `dotnet test windows/tests/DroidMic.Tests` | **38 passed, 0 failed**: protocol vectors, AES-GCM tamper/replay/malformed handling, framing limits, jitter buffer, resampler, 10-minute drift simulations (±3000 ppm), loss concealment, burst capping, live server integration (PIN pairing, reconnect by key, wrong PIN, 5-try lockout, revoked device, busy, timeout, garbage input, foreign-source packets), C#/C driver contract |
| `gradle -PcoreOnly=true :core:test` | **8 passed, 0 failed**: same shared vectors (computed independently in Python) |
| `driver/tests/ringbuffer_test.c` | **Passed**, including a 20 000-step randomised model check |
| `scripts/interop-test.sh` | **PASS**: Kotlin phone code paired via QR URI with fingerprint pinning, streamed 5 s: 505 packets accepted, 0 lost, 0 underruns, 0 rejected; received WAV contains a 999.4 Hz tone at -9.0 dBFS RMS, no dropouts; receiver buffer 22–32 ms |
| `scripts/interop-reconnect-test.sh` | **PASS**: receiver killed mid-stream and restarted; phone reconnected with its stored key (no PIN) and the new receiver accepted 827 packets, 0 lost |
| Android framework type-check (`MicCaptureSource`, `AndroidNetwork`, Keystore repo, settings, against `android-all` API 35) | **Compiles**. It caught and fixed nullable `getSystemService` misuse |
| `dotnet publish DroidMic.Desktop -r win-x64` | **Builds**: `DroidMic.exe`, PE32+ x86-64 GUI, ≈78 MB self-contained |

## Not executed (requires hardware, admin rights or blocked tools)

None of these has been run, and none may be reported as passing until it has:

1. APK build (`:app:assembleDebug`): needs the Android SDK and Google Maven. Runs in CI.
2. Driver build, InfVerif and Driver Verifier: needs the WDK. Runs in CI (build and InfVerif only).
3. Installing the driver, the endpoint appearing in Windows, and the DroidMic self-test.
4. Windows Voice Recorder, Discord or OBS recording from DroidMic Microphone.
5. A real phone on Wi-Fi or USB tethering; Android 10/13/14 behaviour; screen-off streaming.
6. End-to-end latency measurement, 30-minute stability, CPU and battery.
7. Uninstall leaving no devices or services; clean install on a second PC.

## Known blockers

* **Driver signing.** Consumer installs need EV certificate plus Microsoft
  attestation signing (see docs/DRIVER_INSTALLATION.md). Until then the driver
  only loads on test-signing PCs.
* **First WDK compile.** The driver is written to the documented PortCls/WaveRT
  interfaces but has never been compiled; expect small header/signature fixes.

## Build commands

```bash
scripts/run-tests.sh                                     # everything runnable on Linux/macOS
dotnet publish windows/DroidMic.Desktop -c Release -o dist/windows/app
cd android && ./gradlew :app:assembleDebug               # with Android SDK
```
```powershell
./scripts/build-windows.ps1; ./scripts/build-driver.ps1; ./scripts/package-release.ps1 -SkipBuild
```

## Remaining tasks (in order)

1. Push to GitHub, let CI build the APK and the driver, and fix any compile errors.
2. On a test-mode Windows PC: install the driver, run `DriverSetup status`, the
   DroidMic self-test, then Voice Recorder, Discord and OBS. Run Driver Verifier.
3. On phones: pair by QR, PIN and manual IP; test USB tethering, screen off and
   Wi-Fi toggling; measure latency with the clap test.
4. Opus codec option (MediaCodec encoder on Android, Concentus on Windows).
5. Release signing (driver attestation, Authenticode, APK key) and the release checklist.
