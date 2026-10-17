# DroidMic architecture

DroidMic turns an Android phone into a microphone that Windows apps can select as
**DroidMic Microphone**.

```
 Android phone                                   Windows PC
┌────────────────────────────┐                  ┌──────────────────────────────────────────────┐
│ AudioRecord 48k mono s16   │                  │ DroidMic.exe (WPF, user mode)                 │
│   │ (VOICE_RECOGNITION,    │                  │                                              │
│   │  AGC off, NS optional) │  UDP 48111       │ AudioReceiver ─▶ AudioPipeline ─▶ SinkPump ──┼─┐
│   ▼                        │  AES-128-GCM     │  (auth, replay   (jitter buffer,   (paced by  │ │ IOCTL_DROIDMIC_
│ gain · clip detect · meter ├─────────────────▶│   window)        loss concealment, driver     │ │ WRITE_AUDIO
│   ▼                        │  10 ms frames    │                  drift resampler)  queue)     │ │
│ AudioPacketCodec (encrypt) │                  │                                              │ │
│                            │  TCP 48110       │ DroidMicServer: handshake, pairing, stats    │ │
│ DroidMicClient ◀──────────▶├─────────────────▶│ DiscoveryResponder (UDP 48112)               │ │
│ StreamSession (reconnect)  │  ECDH + PIN/key  │                                              │ │
│ MicService (FGS, mic type) │                  └──────────────────────────────────────────────┘ │
└────────────────────────────┘                  ┌──────────────────────────────────────────────┐ │
                                                │ DroidMicVAD.sys (kernel, PortCls WaveRT)     │◀┘
                                                │  ring buffer ─▶ capture stream ─▶ DMA buffer │
                                                └───────────────────────┬──────────────────────┘
                                                                        ▼
                                                 Windows audio engine (audiodg) ─▶ Discord, OBS,
                                                 Voice Recorder, Zoom, games … ("DroidMic Microphone")
```

## Module map

| Path | Language | Role |
|------|----------|------|
| `android/core` | Kotlin/JVM | Protocol, crypto, discovery, `DroidMicClient`, `StreamSession` state machine, meters. No Android dependencies, so it is unit-tested on any JVM and interop-tested against the C# receiver. |
| `android/app` | Kotlin, Compose | `MicCaptureSource` (AudioRecord), `MicService` (foreground service, type `microphone`), Keystore-encrypted pairings, UI. |
| `windows/DroidMic.Protocol` | C# | Wire format, handshake math, framing. Mirrors `android/core/.../protocol`. |
| `windows/DroidMic.AudioEngine` | C# | `JitterBuffer`, `DriftResampler`, `AudioPipeline`, `SinkPump`, `LevelMeter`. |
| `windows/DroidMic.Networking` | C# | `DroidMicServer`, `ClientSession`, `AudioReceiver`, `DiscoveryResponder`, pairing storage, `ReferenceClient`. |
| `windows/DroidMic.Platform` | C# (Windows) | Driver client (`DeviceIoControl`), `DriverSink`, endpoint detection and self-test (Core Audio/WASAPI via NAudio), DPAPI secret store, firewall helpers. |
| `windows/DroidMic.Desktop` | C# WPF | The desktop app (`DroidMic.exe`), tray, settings. |
| `windows/DroidMic.DriverSetup` | C# | Elevated tool: creates the `Root\DroidMicVAD` device node and installs or removes the driver (SetupAPI/newdev). |
| `windows/DroidMic.ReceiverCli` | C# | Headless receiver for development, CI and diagnostics (cross-platform). |
| `driver/VirtualAudioDevice` | C++ (WDK) | Kernel-mode virtual capture device. |
| `shared/protocol-spec` | JSON | Test vectors, computed independently with Python. Both implementations must match them. |

## The virtual microphone: decision record

**Requirement:** a real Windows recording endpoint that any app can select, not a
loopback device, a virtual cable that needs manual routing, or monitoring.

| Option | Real capture endpoint | Redistributable | Effort and risk | Verdict |
|--------|-----------------------|-----------------|-----------------|---------|
| **Own PortCls/WaveRT driver** (SysVAD / SimpleAudioSample architecture) | Yes | Yes, once signed through Microsoft | High: kernel code, signing | **Chosen** |
| Audio Processing Object (APO) | No: APOs process an *existing* endpoint's stream | n/a | n/a | Rejected |
| User-mode "virtual endpoint" API | No such public API on Windows 10/11 for capture endpoints. (The Windows 11 virtual audio device APIs are for Bluetooth/Teams-style scenarios and are not general purpose.) | n/a | n/a | Rejected |
| Third-party virtual cable (VB-CABLE, VAC) | Yes, but users must route audio and pick "CABLE Output" | Licence forbids bundling (VB-CABLE is donationware with no redistribution rights; VAC is commercial) | Low | Rejected as default; documented as a workaround |
| Microsoft "Virtual Audio Driver" community forks (MIT, SysVAD-based) | Yes | Yes (MIT) but unsigned | Medium | Not used; our driver is smaller and tailored |

**Driver design** (`driver/VirtualAudioDevice`):

* Root-enumerated PortCls adapter (`Root\DroidMicVAD`). It has two subdevices: a
  **WaveRT capture filter** (one pin: 48 kHz, 16-bit, mono PCM) and a **topology
  filter** whose microphone bridge pin's Name GUID is mapped by the INF to
  "DroidMic Microphone".
* The capture stream has no hardware clock. A 1 ms high-resolution `EX_TIMER`
  advances the position from `KeQueryPerformanceCounter` (with exact remainder
  carry) and copies samples from the driver's ring into the cyclic WaveRT
  buffer up to that position. `GetPosition` also updates, so the audio engine
  never reads past written data. Event notifications
  (`IMiniportWaveRTStreamNotification`) are signalled at each period boundary.
* The desktop app writes audio through a **private device interface**
  (`GUID_DEVINTERFACE_DROIDMIC_CONTROL`, METHOD_BUFFERED IOCTLs). PortCls owns the
  dispatch table, so the adapter wraps CREATE/CLEANUP/CLOSE/DEVICE_CONTROL/PNP. It
  handles only file objects opened on its reference string and forwards
  everything else to `PcDispatchIrp`.
* Ring buffer: 1 s, bounded. On overflow the oldest samples are dropped. While no
  recording client is running only the newest 20 ms are kept, so a new client
  never hears stale audio. Underruns produce silence. When the app's handle
  closes (including a crash) the ring is cleared.
* One stream instance. Shared-mode clients all go through one audio-engine
  stream, and a second stream would split the ring's samples.
* The ring code is platform-neutral C, unit-tested on Linux in CI.

## Transport

* **Control:** TCP, length-prefixed JSON. Handshake: ECDH P-256, then mutual HMAC
  over a transcript hash keyed by HKDF(ECDH ‖ PIN or pairing key), and a server
  ECDSA identity signature with fingerprint pinning (from the QR code, or trust
  on first use). See [AUDIO_PROTOCOL.md](AUDIO_PROTOCOL.md) and [SECURITY.md](SECURITY.md).
* **Audio:** UDP datagrams of 10 ms (or 20 ms) PCM frames, AES-128-GCM with the
  header as AAD and a nonce from session id and sequence number. There is a
  64-packet replay window, and packets must come from the control peer's IP.
  UDP avoids TCP head-of-line blocking: a lost packet costs 10 ms of
  concealment instead of stalling the whole stream behind a retransmission.
* **Keep-alive:** 1 s ping/pong (the phone measures RTT); 5 s timeout on both
  sides; the phone reconnects with exponential backoff (0.5 → 10 s). After two
  failed attempts it rediscovers the PC by server id, in case DHCP moved it.
* **Discovery:** UDP broadcast to 255.255.255.255 and each subnet's directed
  broadcast (covers USB tethering). Manual IP entry always works.
* **Codec:** PCM only (768 kbit/s per 48 kHz mono stream; trivial on Wi-Fi/USB).
  Opus is the planned bandwidth option. Android 10+ ships an Opus encoder via
  `MediaCodec`, and Concentus (MIT, pure C#) can decode on Windows. It is not
  implemented yet.

## Latency control (PC)

`AudioPipeline` is fed by the network thread and drained by `SinkPump` at the
pace of the driver's consumption, which is the Windows audio clock:

1. **Jitter buffer:** a 64-frame, sequence-ordered, preallocated store. Late and
   duplicate frames are dropped.
2. **Adaptive target:** 2 × frame + 3 × RFC 3550 jitter, clamped to the preset
   (Low 20–60 ms, Balanced 30–100 ms, Stable 60–200 ms). It rises fast and falls slowly.
3. **Drift compensation:** a linear-interpolating resampler whose ratio is steered
   (±0.5 %) by the smoothed buffer error. The phone's and the PC's clocks never
   match exactly, and without this the latency would creep or underrun.
   Unit-tested over 10 simulated minutes at ±3000 ppm.
4. **Hard cap:** if a burst (Wi-Fi power-save flush) pushes the depth past
   max(2 × target, target + 60 ms), the oldest frames are dropped.
5. **Concealment:** the first missing frame repeats the last frame with a fade.
   After that the output is silence. On starvation the pipeline rebuffers.

**Latency budget** (estimates; on-device measurements still to do, see PROGRESS.md):

| Stage | Typical |
|-------|---------|
| AudioRecord buffer and 10 ms framing | 10–20 ms |
| Wi-Fi one-way | 2–10 ms |
| Jitter buffer (Balanced) | 30–40 ms |
| Driver ring (target 15 ms) and engine period (10 ms) | 20–25 ms |
| **Total** | **≈ 60–95 ms** (Low preset ≈ 45–70 ms) |

On loopback the measured receiver buffer settled at 22–32 ms with 0 loss and
0 underruns (scripts/interop-test.sh).

## Android capture

* Source `VOICE_RECOGNITION` by default: tuned for flat response, with AGC and
  AEC off on most devices. `UNPROCESSED` is offered when the device reports
  support, and `VOICE_COMMUNICATION` (platform AEC/NS) as "Voice chat".
  AutomaticGainControl is explicitly disabled. NoiseSuppressor is optional.
* The capture thread runs at `THREAD_PRIORITY_URGENT_AUDIO`. Gain, clipping and
  meter run in place, with no per-frame allocation.
* `MicService` is a `foregroundServiceType="microphone"` service started only from
  a user action in the visible activity (Android 11+ while-in-use rule; Android 14
  FGS type enforcement). It shows a persistent notification with a Stop action,
  never restarts itself (`START_NOT_STICKY`), and holds a low-latency Wi-Fi lock
  while streaming.
* `AudioRecordingCallback.isClientSilenced` detects when another app (or a call)
  takes the microphone, and the UI says so.

## Feasibility and risks (Milestone 1 report)

| Item | Status / risk |
|------|---------------|
| Toolchain in the build container | .NET 8 available. The Android SDK and Google Maven are blocked by network policy, so the APK is built by CI or Android Studio. No WDK, so the driver is built by CI (`windows-2022`) or locally. |
| Driver signing | **Highest risk.** Test signing is for development only. Consumer installs need an EV code-signing certificate, a Partner Center (Hardware Dev Center) account and attestation signing. See [DRIVER_INSTALLATION.md](DRIVER_INSTALLATION.md). |
| Driver correctness | Written to the documented PortCls/WaveRT contracts and modeled on SysVAD. It has not been compiled or run on Windows in this environment, so expect a fix-up iteration on first WDK build and Driver Verifier run. |
| Wi-Fi conditions | Client isolation and guest networks block traffic (detected and explained in both UIs); Wi-Fi power save is handled by a low-latency lock and the jitter buffer cap. |
| USB | USB tethering works through the same IP path (discovery covers the tether subnet). `adb reverse` for UDP is not supported by adb, so it is not offered. |
