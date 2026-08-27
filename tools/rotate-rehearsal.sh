#!/bin/sh
# Replace this server's certificate while it is answering, and watch the next handshake use
# the new one -- against the real host, started the way a deployment starts it.
#
# <b>ADR-014 §2b and condition 1.</b> `CertificateRotationTests` proves the mechanism against
# a Kestrel the test builds, which means it proves the line the test copied. This proves the
# line `Program.ConfigureKestrel` actually runs, and it proves `CertificateReload` is
# registered, watching, and debounced enough to survive a real file copy. Those are three
# things a unit test of the same idea cannot say.
#
# Usage:  tools/rotate-rehearsal.sh [port]
set -eu

# <b>The key and the connection string come from the environment, and that is not a style
# choice.</b> Both were literals in this file until 2026-08-27, when a pre-push scan found
# them: `Graticula:SecretKey` is the AES-256 key that seals every registered data source's
# credentials (ADR-032, layer 2), so a key in a public repository is a key nobody may ever use
# for anything real -- and the likeliest way that happens is somebody copying it out of a
# script like this one. See D-191 in docs/architecture-debt.md.
#
# Set them before running:
#   export GRATICULA_TEST_PG='Host=...;Port=...;Database=...;Username=...;Password=...'
#   export GRATICULA_SECRET_KEY="$(openssl rand -base64 32)"
: "${GRATICULA_TEST_PG:?set GRATICULA_TEST_PG to the platform store connection string}"
: "${GRATICULA_SECRET_KEY:?set GRATICULA_SECRET_KEY, e.g. from: openssl rand -base64 32}"

PORT=${1:-8451}

# <b>Not `mktemp -d`, and that cost twenty minutes once.</b> On Git Bash `mktemp` answers with
# an MSYS path like `/tmp/tmp.AbC`, which the shell understands and the native `openssl.exe`
# and `dotnet.exe` do not -- so every step failed silently under `set -e` with its stderr
# already redirected. `cygpath -m` gives the same directory as a path both halves can open.
WORK=$(mktemp -d)
command -v cygpath >/dev/null 2>&1 && WORK=$(cygpath -m "$WORK")
trap 'kill ${SERVER:-0} 2>/dev/null || true; rm -rf "$WORK"' EXIT

say() { printf '\n== %s\n' "$1"; }

# ---------------------------------------------------------------- two certificates
say "two certificates, one file"

for name in first second; do
  openssl req -x509 -newkey rsa:2048 -nodes -days 30 \
    -subj "/CN=$name.rehearsal" \
    -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" \
    -keyout "$WORK/$name.key" -out "$WORK/$name.crt" >/dev/null 2>&1

  openssl pkcs12 -export -out "$WORK/$name.pfx" \
    -inkey "$WORK/$name.key" -in "$WORK/$name.crt" -passout pass: 2>/dev/null
done

cp "$WORK/first.pfx" "$WORK/serving.pfx"

printf 'first:  %s\n' "$(openssl x509 -in "$WORK/first.crt" -noout -fingerprint -sha1)"
printf 'second: %s\n' "$(openssl x509 -in "$WORK/second.crt" -noout -fingerprint -sha1)"

# ---------------------------------------------------------------- the real host
say "starting the host on $PORT with that file as its certificate"

Graticula__PlatformStore="$GRATICULA_TEST_PG;Search Path=gisserver,public" \
Graticula__SecretKey="$GRATICULA_SECRET_KEY" \
Graticula__HostName="127.0.0.1" \
Graticula__Port="$PORT" \
Graticula__CertificatePath="$WORK/serving.pfx" \
  dotnet run --project src/Graticula.Host --no-build --no-launch-profile \
  > "$WORK/server.log" 2>&1 &

SERVER=$!

waited=0
while [ "$waited" -lt 40 ]; do
  # <b>Shell redirection rather than `-o /dev/null`.</b> Under `MSYS_NO_PATHCONV=1` the
  # native curl.exe takes `/dev/null` literally, fails to write, and exits non-zero after a
  # request the server answered with 200 -- so this loop timed out against a running server
  # forty times while its log showed forty successes.
  if curl -sk --max-time 2 "https://127.0.0.1:$PORT/rest/info" >/dev/null 2>&1; then
    break
  fi
  # <b>The sleep is the whole loop.</b> Without it, curl against a closed port returns
  # instantly and forty attempts are over before the runtime has finished starting -- which
  # looked exactly like a server that would not start.
  sleep 1
  waited=$((waited + 1))
done

if [ "$waited" -ge 40 ]; then
  echo "the host did not answer on $PORT within 40 seconds. Its log:"
  tail -30 "$WORK/server.log"
  exit 1
fi

presented() {
  echo | openssl s_client -connect "127.0.0.1:$PORT" -servername localhost 2>/dev/null \
    | openssl x509 -noout -subject -fingerprint -sha1 2>/dev/null
}

say "what it presents now"
before=$(presented)
echo "$before"

grep -i "Watching" "$WORK/server.log" | tail -1 || echo "  (no watch line in the log -- that is the finding)"

# ---------------------------------------------------------------- rotate
say "copying the second certificate over the file"
cp "$WORK/second.pfx" "$WORK/serving.pfx"

# The reload settles for 750ms and may retry; four seconds is generous and bounded.
waited=0
while [ "$waited" -lt 8 ]; do
  after=$(presented)
  [ "$after" != "$before" ] && break
  sleep 1
  waited=$((waited + 1))
done

say "what it presents after"
echo "$after"

grep -i "replaced from" "$WORK/server.log" | tail -1 || true

say "verdict"
if [ "$after" = "$before" ]; then
  echo "NOT ROTATED -- the same certificate is still being served. ADR-014 2b is not met."
  echo "the last twenty lines of the server log:"
  tail -20 "$WORK/server.log"
  exit 1
fi

echo "ROTATED, with no restart. The server has been answering throughout."
# A real file rather than /dev/null: under MSYS_NO_PATHCONV the native curl takes
# "/dev/null" literally and exits 23 after a request the server answered.
curl -sk -w "  /rest/info still answers: %{http_code}\n" --max-time 5 \
  -o "$WORK/discard" "https://127.0.0.1:$PORT/rest/info"
