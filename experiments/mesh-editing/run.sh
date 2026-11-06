#!/bin/sh
# Runs the prototype once: the stub in front of a Graticula server, headless Chromium on proto.html, report.json out.
#   sh run.sh <https root> <token> <scene layer item id>
cd "$(dirname "$0")"
rm -f report.json requests.log
STUB_CERT=${STUB_CERT:?} STUB_KEY=${STUB_KEY:?} python3 stub.py "$1" "$2" "$3" > stub.out 2>&1 &
STUB=$!
sleep 1
PROFILE=$(mktemp -d)
CHROME=${CHROME:-/opt/dev/scenes-fixture/chrome}
timeout 150 "$CHROME" --headless=new --disable-gpu --enable-unsafe-swiftshader --no-first-run --ignore-certificate-errors --user-data-dir="$PROFILE" \
  --window-size=1200,800 --enable-logging=stderr --v=0 "https://127.0.0.1:18990/proto.html?item=$3" > chrome.log 2>&1 &
BROWSER=$!
for i in $(seq 1 140); do [ -s report.json ] && break; sleep 1; done
kill $BROWSER $STUB 2>/dev/null
rm -rf "$PROFILE"
[ -s report.json ] && echo "report written" || echo "no report"
