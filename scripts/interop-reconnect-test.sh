#!/usr/bin/env bash
# Reconnect test: the PC receiver disappears mid-stream (like a Wi-Fi drop or a PC
# app restart) and comes back. The phone code (Kotlin, on the JVM) must detect the
# loss, keep the microphone open, and reconnect by itself using the stored pairing
# key, without the PIN.
set -Eeuo pipefail
# On CI, report the failing command as an annotation (readable without log access).
trap 'rc=$?; [ -n "${GITHUB_ACTIONS:-}" ] && echo "::error title=$(basename "$0")::line $LINENO exit $rc: ${BASH_COMMAND//$'"'"'\n'"'"'/ }"' ERR
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
RX_PID=""
trap '[ -n "$RX_PID" ] && kill $RX_PID 2>/dev/null; rm -rf "$WORK"' EXIT
PIN=271828
RX="$ROOT/windows/DroidMic.ReceiverCli/bin/Release/net8.0/droidmic-receiver.dll"
dotnet build "$ROOT/windows/DroidMic.ReceiverCli" -c Release -v quiet -nologo >/dev/null

fail() {
  echo "FAIL: $1"
  # Annotations are readable without log access.
  if [ -n "${GITHUB_ACTIONS:-}" ]; then
    echo "::error title=reconnect test::$1 | phone: $(tail -c 600 "$WORK/phone.log" | tr '\n' ' ') | rx: $(tail -c 600 "$WORK"/rx*.log 2>/dev/null | tr '\n' ' ')"
  fi
  exit 1
}

start_rx() { # $1 = log, extra args...
  local log=$1; shift
  dotnet "$RX" --data-dir "$WORK/data" --timeout 60 "$@" > "$log" 2>&1 &
  RX_PID=$!
  for _ in $(seq 1 100); do grep -q READY "$log" && return 0; sleep 0.1; done
  fail "receiver did not start: $(tail -c 300 "$log")"
}

start_rx "$WORK/rx1.log" --pin $PIN
FP=$(awk '/^FINGERPRINT/{print $2}' "$WORK/rx1.log")
(cd "$ROOT/android" && ./gradlew -q -PcoreOnly=true --max-workers=1 :core:simulatePhone \
  --args="--uri droidmic://pair?h=127.0.0.1&p=48110&pin=$PIN&fp=$FP&n=Test --seconds 20") > "$WORK/phone.log" 2>&1 &
PHONE_PID=$!

# Gradle can take a while to start on a cold CI runner.
for _ in $(seq 1 1200); do grep -qE "^(STREAMING|FAILED)" "$WORK/phone.log" 2>/dev/null && break; sleep 0.1; done
grep -q "^STREAMING" "$WORK/phone.log" || fail "phone never started streaming"
sleep 2
echo "== Killing receiver mid-stream"
kill $RX_PID; wait $RX_PID 2>/dev/null || true
sleep 3
echo "== Restarting receiver (same identity and pairings, pairing closed)"
start_rx "$WORK/rx2.log" --wav "$WORK/after.wav" --exit-after-session

wait $PHONE_PID || true
wait $RX_PID 2>/dev/null || true
grep -E "STATE|STREAMING|FAILED" "$WORK/phone.log" | tail -6 || true
grep -E "authenticated|Streaming started|SUMMARY" "$WORK/rx2.log" | tail -4 || true

grep -q "Reconnecting" "$WORK/phone.log" || fail "phone never entered Reconnecting"
grep -q "STATE Streaming reconnects=[1-9]" "$WORK/phone.log" || fail "phone did not resume streaming"
ACC=$(sed -n 's/.*SUMMARY accepted=\([0-9]*\).*/\1/p' "$WORK/rx2.log")
[ "${ACC:-0}" -gt 200 ] || fail "restarted receiver got only ${ACC:-0} packets"
echo "PASS: reconnected with the stored pairing key; restarted receiver accepted $ACC packets"
