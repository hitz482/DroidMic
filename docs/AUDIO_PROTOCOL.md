# DroidMic Protocol v1

This document is the normative contract between the Android sender
(`android/core`) and the Windows receiver (`windows/DroidMic.Protocol`,
`windows/DroidMic.Networking`). Both implementations are tested against each
other by `scripts/interop-test.sh`.

All multi-byte integers are **big-endian** unless stated otherwise. PCM samples
are **little-endian** signed 16-bit.

| Channel     | Transport | Default port | Direction |
|-------------|-----------|--------------|-----------|
| Control     | TCP       | 48110        | phone → PC connects |
| Audio       | UDP       | 48111        | phone → PC |
| Discovery   | UDP       | 48112        | phone broadcasts, PC replies unicast |

## 1. Discovery

The phone sends a UDP datagram to `255.255.255.255:48112` (and to the directed
broadcast address of its Wi-Fi subnet):

```
"DMDISC1" (7 ASCII bytes) || nonce (8 random bytes)
```

A PC that is **listening** replies unicast to the sender with UTF-8 JSON:

```json
{"magic":"DMSRV1","v":1,"nonce":"<base64 of the 8-byte nonce>",
 "serverId":"<16 hex chars>","name":"DESKTOP-ABC","ctl":48110,
 "fp":"<identity fingerprint, 12 hex chars>"}
```

Replies whose nonce does not match are ignored. Discovery is a convenience
only. Manual IP entry and QR pairing work without it (for example on
networks that block broadcast).

## 2. Control channel framing

Each message is `u32 length || UTF-8 JSON object`. `length` is at most 65536.
Every object has a string field `"t"` (type). Unknown fields are ignored and
unknown types are answered with `{"t":"error","code":"unknown_type"}`.

## 3. Handshake and authentication

Cryptographic primitives: ECDH and ECDSA on NIST P-256, SHA-256, HMAC-SHA-256,
HKDF-SHA-256 (RFC 5869), AES-128-GCM. Public keys are encoded as
X.509 SubjectPublicKeyInfo DER, base64 (standard alphabet, padded).

The PC holds a long-term **identity key** (ECDSA P-256). Its fingerprint is the
first 6 bytes of `SHA-256(SPKI)` rendered as 12 lowercase hex characters.

### 3.1 Messages

1. **phone → PC** `hello`
   ```json
   {"t":"hello","v":1,"deviceId":"<16 hex>","deviceName":"Pixel 8",
    "eph":"<SPKI b64>","nonce":"<16 bytes b64>","mode":"pin"|"paired"}
   ```
2. **PC → phone** `server_hello`
   ```json
   {"t":"server_hello","v":1,"serverId":"<16 hex>","serverName":"DESKTOP-ABC",
    "identity":"<SPKI b64>","eph":"<SPKI b64>","nonce":"<16 bytes b64>",
    "sig":"<DER ECDSA-SHA256 signature over TH, b64>"}
   ```
   or `{"t":"error","code":...}` (see §3.4) followed by close.
3. **phone → PC** `auth`: `{"t":"auth","mac":"<b64 HMAC(kC, TH)>"}`
4. **PC → phone** `auth_ok`
   ```json
   {"t":"auth_ok","mac":"<b64 HMAC(kS, TH)>","sessionId":123456789,
    "audioPort":48111,"pairing":"<b64, only when mode == pin>"}
   ```
   or `{"t":"auth_failed","reason":"bad_credentials"|"unknown_device"|"pairing_closed"|"locked_out"}`
   followed by close.

### 3.2 Transcript hash

```
TH = SHA-256( "DroidMic/v1" ||
              lp(deviceId) || lp(clientEph) || lp(clientNonce) ||
              lp(serverId) || lp(identity)  || lp(serverEph)   || lp(serverNonce) )
```

`lp(x)` is `u16 length || x`. String fields contribute their UTF-8 bytes.
Key and nonce fields contribute their decoded bytes.

The phone verifies `sig` with `identity` before sending `auth`. On the first
pairing the phone pins the identity fingerprint for that `serverId`. If the
pairing came from a QR code, the phone compares the fingerprint in the QR code
with this one before it sends anything secret. On later connections a changed
identity is rejected.

### 3.3 Key schedule

```
Z       = ECDH(clientEph, serverEph)                 // 32-byte x-coordinate
secret  = UTF-8(PIN)            when mode == "pin"
        = pairingKey (32 bytes) when mode == "paired"
PRK     = HKDF-Extract(salt = TH, IKM = Z || secret)
kC      = HKDF-Expand(PRK, "droidmic c2s confirm", 32)
kS      = HKDF-Expand(PRK, "droidmic s2c confirm", 32)
kAudio  = HKDF-Expand(PRK, "droidmic audio",       16)
kWrap   = HKDF-Expand(PRK, "droidmic pairing wrap", 16)
```

When `mode == "pin"` and authentication succeeds, the PC generates a random
32-byte `pairingKey`, stores it with `deviceId`/`deviceName`, and returns
`pairing = AES-GCM(kWrap, nonce = 12 zero bytes, plaintext = pairingKey, aad = TH)`
(ciphertext || 16-byte tag). The phone verifies `mac` before it accepts the
pairing key. Both MACs give mutual authentication. A device that does not
know the PIN or the pairing key cannot produce `kC` or `kAudio`.

### 3.4 Policy

* `mode == "pin"` is only accepted while the user has **pairing mode** open in
  the Windows app (it shows the PIN/QR). Otherwise: `error/pairing_closed`.
* Five failed PIN attempts close pairing mode and invalidate the PIN
  (`locked_out`). The user must open pairing again to get a new PIN.
* Only one streaming session is active at a time. A second authenticated device
  gets `error/busy`.
* Paired devices can be revoked from the Windows app. They then get
  `auth_failed/unknown_device`, and the phone falls back to re-pairing.

## 4. Session control (after `auth_ok`)

| Message | Direction | Fields |
|---------|-----------|--------|
| `stream_start` | phone → PC | `codec` (`"pcm16"`), `rate` (48000), `ch` (1), `frameMs` (10 or 20) |
| `stream_ack`   | PC → phone | `ok` (bool), `reason` (string, on failure) |
| `stream_stop`  | phone → PC | |
| `ping`         | phone → PC | `ts` (phone ms clock), `rtt` (last measured round trip in ms, 0 if unknown) |
| `pong`         | PC → phone | `ts` (echoed) |
| `stats`        | PC → phone | `received`, `lost`, `late`, `underruns`, `bufferMs`, `jitterMs`, `sinkName`, `sinkOk` |
| `bye`          | either | `reason` |

The phone sends `ping` every 1 s. If either side sees no traffic for 5 s it
ends the session. Closing TCP ends the session and invalidates `kAudio`.

## 5. Audio datagrams

```
off len field
 0   2  magic          'D' 'M' (0x44 0x4D)
 2   1  version        1
 3   1  type           1 = audio
 4   4  sessionId      from auth_ok
 8   4  seq            starts at 0, +1 per packet, never wraps within a session
12   4  timestamp      sample index of first sample (mod 2^32)
16   1  codec          1 = PCM s16le
17   1  channels       1 or 2
18   1  rateCode       1=16000 2=24000 3=32000 4=44100 5=48000
19   1  flags          bit0 = input clipped in this frame, bit1 = muted
20   2  frameSamples   samples per channel (max 960)
22   2  reserved       0
24   n  ciphertext     AES-128-GCM(kAudio, nonce, payload, aad = bytes 0..23)
24+n 16 tag
```

`nonce = sessionId (4) || 0x00000000 || seq (4)`

For PCM, `n = frameSamples × channels × 2`. The receiver drops a datagram
silently if any of these hold:

* the length is wrong, or the magic, version, type or codec is unknown;
* `frameSamples > 960` or the payload is larger than 3840 bytes;
* the `sessionId` is not the active session;
* the source IP differs from the control connection's peer;
* `seq` is older than the 64-packet replay window, or was already seen;
* GCM authentication fails.

## 6. Receiver behaviour (informative)

The PC holds an adaptive jitter buffer. Its target depth is 20 to 120 ms,
chosen from observed inter-arrival jitter. Packets are reordered by `seq`.
Missing frames are concealed: the last frame is repeated once with a fade, and
after that the receiver outputs silence. A drift-compensating resampler,
limited to ±0.5 %, holds the buffer near its target, so latency can't grow
without bound. The receiver's output clock is the Windows audio clock (the
virtual device's consumption rate).
