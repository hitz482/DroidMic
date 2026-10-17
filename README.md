# DroidMic

Use your Android phone as a real microphone on a Windows PC. Windows sees a
recording device called **DroidMic Microphone**, which you can select in Sound
settings, Discord, OBS, Zoom, games or any app that records audio.

```
Phone mic ─▶ DroidMic (Android) ══ encrypted UDP over Wi-Fi/USB ══▶ DroidMic.exe ─▶ DroidMicVAD driver ─▶ "DroidMic Microphone"
```

* Low latency: 10 ms PCM frames over UDP, an adaptive jitter buffer, and clock-drift
  compensation that keeps latency from creeping up.
* Secure pairing: a QR code or 6-digit PIN that the PC user has to open first.
  The handshake is authenticated ECDH and the audio is AES-GCM encrypted.
  Nothing leaves your network, and nothing is recorded.
* Automatic PC discovery, manual IP entry, USB tethering, and automatic reconnection.

> **Project status:** see [PROGRESS.md](PROGRESS.md). The protocol, the PC receiver
> and the Android streaming core are built and tested end to end (Kotlin ↔ C# over
> real sockets). The Windows app compiles to a self-contained `DroidMic.exe`. The
> Android APK and the kernel driver are built by CI or locally with the Android
> SDK and the WDK. **The virtual microphone has not yet been verified on real
> Windows hardware, and no Microsoft-signed driver exists yet.** Read
> [DRIVER_INSTALLATION.md](docs/DRIVER_INSTALLATION.md) before installing.

## Quick start (users)

1. Install **DroidMic for Windows** (`DroidMic-x.y.z-windows-x64-setup.exe`) and keep
   *Install the virtual microphone driver* checked.
2. Install the **DroidMic APK** on the phone (Android 10+).
3. Put both on the same Wi-Fi (or connect by USB and turn on USB tethering).
4. On the PC: **Pair phone → Show PIN and QR code**.
5. On the phone: **Connect → Scan QR code** (or tap the PC in the list and type the PIN).
   Grant the microphone permission.
6. Press **Start Microphone** on the phone. The PC dashboard shows the live level.
7. In your app, choose **DroidMic Microphone** as the input device.

To stop, press **Stop Microphone** on the phone or **Stop** in its notification.

## Repository layout

| Path | Contents |
|------|----------|
| `android/` | Android app (`app/`) and the platform-independent streaming core (`core/`) |
| `windows/` | Desktop app, audio engine, networking, protocol, driver setup tool, headless receiver, installer, tests |
| `driver/` | Kernel-mode virtual audio capture driver (PortCls/WaveRT) and its host-side tests |
| `shared/protocol-spec/` | Cross-language protocol test vectors |
| `docs/` | Architecture, protocol, build, driver installation, security, troubleshooting, release checklist |
| `scripts/` | Build, test, interop and packaging scripts |

## Building

| What | How | Docs |
|------|-----|------|
| All tests that run anywhere | `scripts/run-tests.sh` (Linux/macOS) or `scripts/run-tests.ps1` | — |
| Windows app | `scripts/build-windows.ps1` → `dist/windows/app/DroidMic.exe` | [BUILD_WINDOWS.md](docs/BUILD_WINDOWS.md) |
| Driver (needs WDK) | `scripts/build-driver.ps1` | [DRIVER_INSTALLATION.md](docs/DRIVER_INSTALLATION.md) |
| Installer | `scripts/package-release.ps1` | [RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md) |
| Android APK | `cd android && ./gradlew :app:assembleDebug` | [BUILD_ANDROID.md](docs/BUILD_ANDROID.md) |

GitHub Actions (`.github/workflows/ci.yml`) runs all of this and uploads the APK,
the Windows app and the test-signed driver package as artifacts.

## How it works

See [ARCHITECTURE.md](docs/ARCHITECTURE.md), which includes why a kernel driver
is needed, and [AUDIO_PROTOCOL.md](docs/AUDIO_PROTOCOL.md).

## Privacy

Audio goes only to the PC you paired, encrypted, and only while the phone shows
"Microphone on". No recordings, accounts, cloud services or analytics. Details
are in [SECURITY.md](docs/SECURITY.md).

## License

MIT (see [LICENSE](LICENSE)). Third-party components are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
