#!/usr/bin/env bash
# Runs a command; if it fails, re-emits its error lines as GitHub annotations
# (readable through the public checks API even when logs aren't accessible).
set -o pipefail
LOG="$(mktemp)"
"$@" 2>&1 | tee "$LOG"
rc=${PIPESTATUS[0]}
if [ "$rc" -ne 0 ]; then
  grep -E "(^e: |[Ee]rror|warning C[0-9]|Signability|22\.[0-9]|FAILED|What went wrong|Exception|Could not)" "$LOG" | grep -vE "^\s*at " | head -60 |
    while IFS= read -r line; do echo "::error title=$(basename "$1")::${line:0:900}"; done
fi
exit "$rc"
