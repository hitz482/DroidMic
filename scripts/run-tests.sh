#!/usr/bin/env bash
# Runs every automated test that works on Linux/macOS (no Android SDK, WDK or
# Windows audio stack needed).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [ "${1:-}" != "--interop-only" ]; then
  echo "== .NET tests";        dotnet test "$ROOT/windows/tests/DroidMic.Tests"
  echo "== Kotlin core tests"; (cd "$ROOT/android" && ./gradlew -PcoreOnly=true :core:test --console=plain)
  echo "== Driver ring buffer tests"
  cc -std=c99 -Wall -Wextra -Werror -o "${TMPDIR:-/tmp}/droidmic_ringbuffer_test" "$ROOT/driver/tests/ringbuffer_test.c"
  "${TMPDIR:-/tmp}/droidmic_ringbuffer_test"
fi
echo "== Interop: pairing + streaming"; "$ROOT/scripts/interop-test.sh"
echo "== Interop: reconnect";           "$ROOT/scripts/interop-reconnect-test.sh"
echo "All tests passed."
