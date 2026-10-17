# Release checklist

## Version

- [ ] Bump `Version` in `windows/Directory.Build.props`, `versionCode`/`versionName` in
      `android/app/build.gradle.kts`, `DriverVer` in `DroidMicVAD.inf`.
- [ ] If the wire format changed: bump `Protocol.VERSION` on both sides and update
      `docs/AUDIO_PROTOCOL.md` and `shared/protocol-spec/test-vectors.json`.

## Automated

- [ ] CI green: .NET tests, Kotlin tests, ring buffer tests, both interop scripts,
      APK build + lint, Windows app + driver build + InfVerif.
- [ ] Driver: no warnings at /W4; InfVerif `/w` clean; Driver Verifier run (standard
      flags) through the manual matrix below with no bugchecks.

## Signing

- [ ] Driver attestation-signed by Microsoft (see DRIVER_INSTALLATION.md §2);
      `signing.txt` says `release`. **If not, the release notes say the driver is test-signed.**
- [ ] DroidMic.exe and the installer Authenticode-signed (recommended; avoids SmartScreen warnings).
- [ ] Release APK signed with the release key, stored outside the repository.

## Manual test matrix

Record device models, OS builds and results in PROGRESS.md. Never tick an item that wasn't run.

- [ ] Android 10, 13 and 14+ phones on the same Wi-Fi: pair by QR, by discovery + PIN, by manual IP.
- [ ] Level meter moves on phone and PC.
- [ ] "DroidMic Microphone" appears in Sound settings; DroidMic self-test passes.
- [ ] Windows Voice Recorder (Sound Recorder) records the phone.
- [ ] Discord input device works; OBS Audio Input Capture works.
- [ ] Stop on the phone stops transmission immediately (PC meter drops, packets stop).
- [ ] Wi-Fi off/on on the phone: reconnects without the PIN.
- [ ] Wrong PIN rejected; 5 wrong PINs lock pairing.
- [ ] Driver absent: dashboard reports it; app still receives.
- [ ] 30-minute stream: buffer stays within the preset bounds (no latency creep), CPU < 2 % on PC.
- [ ] Screen off for 10 minutes: still streaming (foreground service).
- [ ] Measure end-to-end latency (clap test: phone mic + PC mic recorded together in Audacity) and record it.
- [ ] Uninstall: no DroidMic device in Device Manager (show hidden), no `droidmicvad.inf` in `pnputil /enum-drivers`, no firewall rule.
- [ ] Clean install on a second Windows PC.

## Publish

- [ ] `scripts/package-release.ps1` → installer, zip, SHA256SUMS.txt.
- [ ] Tag `vX.Y.Z`; attach APK, installer, zip, checksums; release notes include known limitations.
