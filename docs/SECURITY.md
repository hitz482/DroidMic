# Security and privacy

## What is transmitted, and where

* **Only between your phone and the PC you paired**, over your local network or
  USB tethering. There is no cloud service, account, relay or analytics.
* **Audio:** 48 kHz mono PCM, encrypted with AES-128-GCM, UDP port 48111, while
  the phone shows "Microphone on". Nothing is recorded or stored on either
  device. The PC passes audio to the virtual microphone in memory only. The
  headless developer receiver writes a WAV file only if run with `--wav`.
* **Control:** TCP 48110: handshake, start/stop, 1 s keep-alives and statistics
  (packet counts, buffer level, sink name).
* **Discovery:** UDP 48112. The phone broadcasts a random nonce, and a listening
  PC replies with its name, server id, port and identity fingerprint.
  Discovery can be disabled on the PC.

To stop transmitting, press **Stop Microphone** in the app or **Stop** in the
notification. The microphone is released immediately and no automatic restart
happens. On the PC, *Disconnect phone* ends the session and *Remove* in Paired
devices revokes a phone.

## Threat model

Defended against, on the LAN:

| Threat | Mitigation |
|--------|------------|
| A stranger streams audio into your PC's microphone | Audio is accepted only from an authenticated session. Pairing needs a 6-digit PIN that is shown only after the PC user clicks *Pair phone*, is single-use, expires after 5 minutes, and locks after 5 wrong attempts. Paired phones use a 256-bit random pairing key. |
| Eavesdropping on the audio | ECDH P-256 per session, then AES-128-GCM with a key bound to the transcript and the PIN or pairing key. |
| Injecting, tampering with or replaying packets | GCM over header and payload; per-session key; 64-packet sliding replay window; packets must come from the control peer's IP; strict length and format checks before decryption. |
| A fake PC capturing your voice (impersonation) | The PC signs every handshake with its long-term ECDSA identity key. QR pairing carries the fingerprint, so the phone verifies it before revealing anything derived from the PIN. Paired phones pin the fingerprint and refuse a changed identity with a warning. |
| Offline PIN guessing | An attacker must be an active man-in-the-middle during pairing to get a value they could test PINs against. With QR pairing the fingerprint check defeats that. With manual PIN entry the phone shows the identity code to compare (trust on first use). |
| Malformed traffic crashing the PC app | Framing limits (64 KiB control messages, 3840-byte audio payloads), schema validation, bounded buffers, fuzz-style tests for malformed headers and garbage on the control port. |

Not defended: a malicious local administrator on either device; anyone who
already holds your unlocked phone. Any local process on the PC can open the
driver's control interface and inject audio into the DroidMic Microphone; this
is the same exposure as any virtual audio cable.

## Key storage

* **PC:** identity key and pairing keys are encrypted with Windows **DPAPI**
  (current user) in `%AppData%\DroidMic\secrets`.
* **Phone:** pairing keys are encrypted with an AES-256-GCM key held in the
  **Android Keystore**, never exported. App backup and device transfer are
  disabled for these preferences.
* Logs never contain PINs or keys. The PC log masks the third octet of IP
  addresses.

## Android permissions

`RECORD_AUDIO` (asked when you first start the microphone), `FOREGROUND_SERVICE_MICROPHONE`
(persistent notification while the mic is open), `POST_NOTIFICATIONS`, network
state and multicast (discovery), `CAMERA` (QR scanning only, in the scanner
screen), `WAKE_LOCK` (keeps Wi-Fi low-latency while streaming).

## Reporting vulnerabilities

Please open a private security advisory on the repository rather than a public issue.
