#!/usr/bin/env bash
# End-to-end interop test: the Android core (Kotlin, run on the JVM with a test tone)
# streams to the Windows receiver (C#, headless) over real TCP/UDP sockets on
# loopback. Verifies pairing, encryption and that the received WAV contains the tone.
set -euo pipefail
# On CI, report the failing command as an annotation (readable without log access).
trap 'rc=$?; [ -n "${GITHUB_ACTIONS:-}" ] && echo "::error title=$(basename "$0")::line $LINENO exit $rc: ${BASH_COMMAND//$'"'"'\n'"'"'/ }"' ERR
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'kill $RX_PID 2>/dev/null || true; rm -rf "$WORK"' EXIT
SECONDS_TO_STREAM="${SECONDS_TO_STREAM:-5}"
PIN=482913

echo "== Building receiver"
dotnet build "$ROOT/windows/DroidMic.ReceiverCli" -c Release -v quiet -nologo >/dev/null

echo "== Starting receiver"
dotnet "$ROOT/windows/DroidMic.ReceiverCli/bin/Release/net8.0/droidmic-receiver.dll" \
  --pin $PIN --wav "$WORK/received.wav" --data-dir "$WORK/data" \
  --control-port 48110 --audio-port 48111 --exit-after-session --timeout 60 > "$WORK/receiver.log" 2>&1 &
RX_PID=$!
for _ in $(seq 1 100); do grep -q READY "$WORK/receiver.log" && break; sleep 0.1; done
grep -q READY "$WORK/receiver.log" || { cat "$WORK/receiver.log"; exit 1; }
FP=$(awk '/^FINGERPRINT/{print $2}' "$WORK/receiver.log")

echo "== Streaming ${SECONDS_TO_STREAM}s of 1 kHz from the JVM phone simulator"
(cd "$ROOT/android" && ./gradlew -q -PcoreOnly=true --max-workers=1 :core:simulatePhone \
  --args="--uri droidmic://pair?h=127.0.0.1&p=48110&pin=$PIN&fp=$FP&n=Test --seconds $SECONDS_TO_STREAM") | tee "$WORK/phone.log"

wait $RX_PID || true
echo "== Receiver log"
grep -E "STATUS|STATS|SUMMARY|Paired|warn|fail" "$WORK/receiver.log" | tail -12

echo "== Analysing received audio"
python3 - "$WORK/received.wav" "$SECONDS_TO_STREAM" <<'PY'
import sys, wave, struct, math
path, secs = sys.argv[1], float(sys.argv[2])
w = wave.open(path)
assert w.getframerate() == 48000 and w.getnchannels() == 1 and w.getsampwidth() == 2
n = w.getnframes()
data = struct.unpack('<%dh' % n, w.readframes(n))
active = [i for i, s in enumerate(data) if abs(s) > 1000]
assert active, "no signal received"
seg = data[active[0]:active[-1]]
dur = len(seg) / 48000
rms = math.sqrt(sum(s * s for s in seg) / len(seg))
crossings = sum(1 for a, b in zip(seg, seg[1:]) if a < 0 <= b)
freq = crossings / dur
level = 20 * math.log10(rms / 32768)
# Count dropouts: runs of >= 5 ms of near-silence inside the active region.
gaps, run = 0, 0
for s in seg:
    run = run + 1 if abs(s) < 200 else 0
    if run == 240: gaps += 1
print(f"signal: {dur:.2f}s, {freq:.1f} Hz, RMS {level:.1f} dBFS, dropouts {gaps}")
assert abs(freq - 1000) < 5, "wrong frequency"
assert -10 < level < -8, "wrong level (expected about -9 dBFS RMS for a -6 dBFS sine)"
assert dur > secs - 1.0, "too little audio"
assert gaps == 0, "audio has dropouts"
print("PASS")
PY
