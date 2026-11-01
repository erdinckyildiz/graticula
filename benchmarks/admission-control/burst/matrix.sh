#!/bin/sh
# The burst matrix of 2026-11-01 (ADR-046 §5a): 1, 3 and 5 HTTP/2 clients, bursts of 64, 128 and 256, five rounds each.
# BURST_USER and BURST_PASSWORD name the fixture account; the probe's --url defaults to https://127.0.0.1:18572.
# Runs the burst matrix against the fixture. Usage: sh matrix.sh <label-prefix> <uncached-zoom> [scenarios] [clients]
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
B=${BURST:-"$HERE/bin/Release/net9.0/burst.exe"}
P=$1
UZ=$2
SCEN=${3:-"tile-cached tile-uncached export query viewer"}
CL=${4:-"1 3 5"}
OUT="$HERE/$P.jsonl"
: > "$OUT"
off=${UOFF:-0}
for c in $CL; do
  for s in $SCEN; do
    for n in 64 128 256; do
      "$B" --scenario=$s --burst=$n --clients=$c --rounds=5 --gap=1000 --uz=$UZ --uoff=$off --label="$P-$s-$n-x$c" --out="$OUT" | grep -v "^ "
      off=$((off + n * c * 5))
      sleep 1
    done
  done
done
