#!/bin/sh
# Drives flood.js through the three shapes ADR-046 condition 1 needs, in one pass.
#
# k6 is not installed system-wide and does not need to be: it is one self-contained
# binary. Point K6 at it.
#
#   K6=/path/to/k6.exe \
#   GRATICULA_URL=https://127.0.0.1:8459 GRATICULA_USER=ci GRATICULA_PASSWORD=... \
#   sh benchmarks/admission-control/run.sh
#
# Three shapes, and each answers a different half of the condition:
#
#   1. `ramp`      — closed loop at 24, 60, 120, 240 and 480 callers, warm. This is the
#                    §1 and §4 tables' own shape, so the numbers are comparable to the
#                    ones condition 1 was written against. Clause 2 of the condition —
#                    *the admitted median stops growing with concurrency* — is read here.
#   2. `sustained` — 480 callers held for three minutes, cut into fifteen-second windows.
#                    This is the question Q-140 names: does a sustained flood shed, or
#                    only a cold one? A shed share that is high in the first window and
#                    zero afterwards means cold only. One that holds means sustained.
#   3. `rate`      — open loop, 500 to 8,000 requests per second offered regardless of
#                    what the server does. A closed loop cannot answer *what share of
#                    offered work is refused*, because a refused caller returns instantly
#                    and inflates its own denominator.
#
# Every run writes measured-<label>.json beside this script and prints its own table.

set -e

HERE=$(cd "$(dirname "$0")" && pwd)
K6=${K6:-k6}
URL=${GRATICULA_URL:-https://127.0.0.1:8459}
USER_NAME=${GRATICULA_USER:-ci}
PASSWORD=${GRATICULA_PASSWORD:-}
LAYER=${GRATICULA_LAYER:-hosted/ci_many}
ROWS=${GRATICULA_ROWS:-200}

if [ -z "$PASSWORD" ]; then
  echo "GRATICULA_PASSWORD is not set. A password in a file is a password in the" >&2
  echo "repository's history the moment the file is committed." >&2
  exit 2
fi

run() {
  label=$1
  shift
  echo "--- $label"
  "$K6" run --quiet \
    -e "URL=$URL" -e "USER=$USER_NAME" -e "PASSWORD=$PASSWORD" \
    -e "LAYER=$LAYER" -e "ROWS=$ROWS" \
    -e "LABEL=$label" -e "OUT=$HERE/measured-$label.json" \
    "$@" "$HERE/flood.js"
}

case "${1:-all}" in
  ramp|all)
    for n in 24 60 120 240 480; do
      run "vus-$n" -e MODE=vus -e "VUS=$n" -e DURATION=60s -e WARM=10s -e WINDOW=15
      sleep 5
    done
    ;;
esac

case "${1:-all}" in
  sustained|all)
    run "sustained-480" -e MODE=vus -e VUS=480 -e DURATION=180s -e WARM=15s -e WINDOW=15
    sleep 5
    ;;
esac

case "${1:-all}" in
  rate|all)
    for r in 500 1000 2000 4000 8000; do
      run "rate-$r" -e MODE=rate -e "RATE=$r" -e DURATION=60s -e WARM=10s -e WINDOW=15
      sleep 5
    done
    ;;
esac

echo "done — measured-*.json in $HERE"
