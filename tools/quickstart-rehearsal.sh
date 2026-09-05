#!/bin/sh
# Run the README's quickstart from an empty machine and refuse to finish unless it worked.
#
# <b>Because the README's promise had stopped being true and nothing was reading it —
# [D-19](../docs/architecture-debt.md).</b> That row says the quickstart needs the repository
# and a working Docker build rather than a `docker pull`, which is about *publishing*. What
# nobody was checking is the smaller and worse thing: whether the four commands work at all.
# Measured 2026-09-02 on a clean volume, they did not. `docker compose up` reported the
# datastore **healthy** and left the server `Exited (1)` with *Failed to connect to
# 172.22.0.2:5432 -- Connection refused*, because the datastore's health check ran
# `pg_isready` with no host: that connects over the unix socket, and the postgres image runs a
# temporary server on that socket while it initialises a fresh volume, deliberately not on TCP.
# The check went green during initialisation and the server started into nothing.
#
# <b>The check's own comment named the failure it did not test for</b> -- *starting it before
# PostgreSQL accepts connections turns a race into a crash loop that looks like a bug in the
# server* -- which is why this script exists rather than a second comment.
#
# <b>It runs the README's commands, not equivalents of them.</b> A rehearsal that sets up the
# database its own way proves that a database can be set up; the thing worth proving is that
# what a reader is told to type does what they were told it would.
#
# <b>It destroys its own volumes at both ends.</b> The failure only appears on a *fresh* store,
# so a rehearsal that reused one would pass for ever after the first run.
#
# Usage:  tools/quickstart-rehearsal.sh [port]
set -eu

PORT=${1:-8543}
PROJECT=graticula-quickstart-rehearsal
COMPOSE="docker compose -p $PROJECT"

export GIS_PORT="$PORT"

clean() {
  printf '\n-- removing the rehearsal stack and its volumes\n'
  $COMPOSE down -v >/dev/null 2>&1 || true
}
trap clean EXIT

clean

# <b>Build both images before anything runs them, and `--build` on step 3 was not enough.</b>
# `compose.yaml` gives each service an `image:` as well as a `build:`, so a `run` or an `up`
# with no `--build` uses the last *released* image when one has been published. Step 3 said so
# and forced the source path; steps 1 and 2 did not, and step 2 is the one that writes to the
# database. So the rehearsal migrated the store with the released binary and then started the
# tree's server against it.
#
# <b>Which is invisible until the schema moves.</b> Measured 2026-09-05, on the commit that
# added migration 38: the released image migrated the store to 37, the built server refused to
# start against it -- *server is built for schema 38, and the platform store is at 37* -- and
# the rehearsal reported that the README's four commands do not work. They do. What did not
# work was testing two different builds and calling them one deployment.
$COMPOSE build 2>&1 | tail -1

printf '== 1. keygen ==\n'

# The README pipes this into .env by hand; here it goes straight into the variable the
# compose file reads, because a rehearsal that edits a file in the working tree is a
# rehearsal that leaves something behind.
GIS_SECRET_KEY=$($COMPOSE run --rm --no-deps server keygen 2>/dev/null | tail -1 | tr -d ' \r')
export GIS_SECRET_KEY

case "$GIS_SECRET_KEY" in
  ????????????????????????????????????????????) : ;;
  *)
    printf 'keygen did not print a 44-character base64 key; it printed %s characters.\n' \
      "${#GIS_SECRET_KEY}"
    exit 1
    ;;
esac

printf '   a key was printed\n'

printf '== 2. migrate --apply ==\n'
$COMPOSE run --rm server migrate --apply 2>&1 | tail -2

printf '== 3. up ==\n'

# `--build` is redundant now that everything is built above, and it stays because it is what
# that comment is about: without it, this pulls the last published image and tests that
# instead of the tree it was run from, which is the one thing a rehearsal in CI must not do.
$COMPOSE up -d --build 2>&1 | tail -2

# <b>The server, not the datastore.</b> The whole point is that the datastore reporting itself
# healthy proved nothing about whether the server could reach it.
i=0
while [ "$i" -lt 30 ]; do
  code=$(curl -sk -o /dev/null -w '%{http_code}' "https://127.0.0.1:$PORT/healthz/live" || true)

  if [ "$code" = "200" ]; then
    printf '   the server answers /healthz/live after %s seconds\n' "$((i * 2))"
    break
  fi

  i=$((i + 1))
  sleep 2
done

if [ "$code" != "200" ]; then
  printf '\nThe server never answered /healthz/live. What the stack looks like:\n'
  $COMPOSE ps -a
  printf '\nThe server said:\n'
  $COMPOSE logs --no-log-prefix server 2>&1 | tail -30
  exit 1
fi

state=$($COMPOSE ps -a --format '{{.Name}} {{.Status}}' | grep server || true)

case "$state" in
  *Exited*)
    printf '\nThe server container has exited: %s\n' "$state"
    $COMPOSE logs --no-log-prefix server 2>&1 | tail -30
    exit 1
    ;;
esac

printf '== 4. setup ==\n'

TOKEN=$($COMPOSE logs --no-log-prefix server 2>&1 \
  | grep -A2 'One-time setup token' | tail -1 | tr -d ' \r')

if [ -z "$TOKEN" ]; then
  printf 'The server printed no setup token, so a first-time reader has no way in.\n'
  $COMPOSE logs --no-log-prefix server 2>&1 | tail -30
  exit 1
fi

printf '   a setup token was printed\n'

answer=$(curl -sk -o /dev/null -w '%{http_code}' \
  -X POST "https://127.0.0.1:$PORT/rest/setup" \
  -H 'Content-Type: application/json' \
  -d "{\"token\":\"$TOKEN\",\"name\":\"root\",\"password\":\"a properly long quickstart password\"}")

if [ "$answer" != "200" ]; then
  printf 'Setup answered %s rather than 200.\n' "$answer"
  exit 1
fi

printf '   the administrator was created\n'

# <b>And the surfaces answer, which is the thing a reader came for.</b> Before setup every one
# of these is a 503 by design; a rehearsal that stopped at the token would not notice a server
# that stayed refusing.
for path in "/rest/info?f=json" "/rest/services?f=json"; do
  code=$(curl -sk -o /dev/null -w '%{http_code}' "https://127.0.0.1:$PORT$path")

  if [ "$code" != "200" ]; then
    printf '%s answered %s after setup, not 200.\n' "$path" "$code"
    exit 1
  fi

  printf '   %s answers 200\n' "$path"
done

printf '\nThe quickstart works, in the order the README gives it.\n'
