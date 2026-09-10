#!/bin/sh
# Three targets, five concurrencies, one generator, one machine.
#
#   info   — Graticula /rest/info, the path D-249 measured and called a control
#   live   — Graticula /healthz/live, the one path whose middleware skips ResolveAsync
#   bare   — a different server entirely, answering the same 182 bytes over the same TLS
#
# info vs live isolates ResolveAsync inside one process. live vs bare is everything else
# the pipeline costs. Neither number means anything without the other, which is why they
# are taken in the same pass with the levels interleaved.
set -e

K6=/c/temp/k6.exe
OUT=/c/temp/barectl/out
mkdir -p "$OUT"

for n in 1 4 16 32 128; do
  for who in info live bare; do
    case "$who" in
      info) u="https://127.0.0.1:8443/rest/info?f=json" ;;
      live) u="https://127.0.0.1:8443/healthz/live" ;;
      bare) u="https://127.0.0.1:8555/rest/info" ;;
    esac
    "$K6" run --quiet \
      -e "URL=$u" -e "VUS=$n" -e DURATION=20s -e WARM=6s \
      -e "LABEL=$who-$n" -e "OUT=$OUT/$who-$n.json" \
      /c/temp/barectl/bare.js 2>/dev/null || echo "$who-$n FAILED"
  done
done
echo "done"
