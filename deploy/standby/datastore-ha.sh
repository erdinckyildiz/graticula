#!/bin/bash
# ADR-319 §5.9: what a datastore container of a primary-and-standby pair does before PostgreSQL's own entrypoint.
#
#   - On an empty volume of the standby: wait for the peer to be a primary, then clone it (pg_basebackup -R).
#   - On a volume that was a primary, when the peer is now a primary on a newer timeline: this is the old primary coming
#     back after a failover. Rewind it against the new one (pg_rewind -R) so it starts as the new one's standby, as
#     ArcGIS Data Store's former primary becomes the standby. Without this it would start read-write on the old
#     timeline; the server refuses and fences such a host (§5.3, §5.6), but it would still be no use to anybody.
#   - Otherwise: start as the files say.
#
# NOT RUN (D-494): written on a machine without Docker. The steps are the ones TheStoreFailsOverTests runs by hand.
set -euo pipefail

PGDATA="${PGDATA:-/var/lib/postgresql/data}"
PEER="${GIS_STANDBY_PEER:?GIS_STANDBY_PEER names the other datastore container}"
ROLE="${GIS_STANDBY_ROLE:-primary}"
export PGPASSWORD="${POSTGRES_PASSWORD:-}"

ask_peer() {
  psql -h "$PEER" -U "$POSTGRES_USER" -d postgres -tAc "$1" 2>/dev/null || true
}

peer_is_primary() {
  [ "$(ask_peer 'select not pg_is_in_recovery()')" = "t" ]
}

peer_timeline() {
  ask_peer "select ('x' || substr(pg_walfile_name(pg_current_wal_lsn()), 1, 8))::bit(32)::int"
}

own_timeline() {
  gosu postgres pg_controldata "$PGDATA" | awk -F: '/Latest checkpoint.s TimeLineID/ { gsub(/ /, "", $2); print $2 }'
}

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  if [ "$ROLE" = "standby" ]; then
    echo "datastore-ha: empty volume; cloning the primary at $PEER"
    until peer_is_primary; do sleep 2; done
    mkdir -p "$PGDATA" && chown postgres:postgres "$PGDATA" && chmod 700 "$PGDATA"
    gosu postgres pg_basebackup -h "$PEER" -U "$POSTGRES_USER" -D "$PGDATA" -R -X stream -c fast
  fi
elif [ ! -f "$PGDATA/standby.signal" ] && peer_is_primary; then
  theirs="$(peer_timeline)"
  mine="$(own_timeline)"
  if [ -n "$theirs" ] && [ -n "$mine" ] && [ "$theirs" -gt "$mine" ]; then
    echo "datastore-ha: $PEER is the primary on timeline $theirs and this volume is on $mine; rewinding to follow it"
    gosu postgres pg_rewind -D "$PGDATA" \
      --source-server="host=$PEER user=$POSTGRES_USER password=$PGPASSWORD dbname=postgres" --write-recovery-conf
  fi
fi

# Replication from the peer, on a volume made before the standby was added (a new one gets it from 20-replication.sh).
if [ -s "$PGDATA/PG_VERSION" ] && ! grep -q '^host[[:space:]]\+replication' "$PGDATA/pg_hba.conf"; then
  echo "host replication all all scram-sha-256" >> "$PGDATA/pg_hba.conf"
fi

exec docker-entrypoint.sh postgres \
  -c wal_level=replica -c wal_log_hints=on -c hot_standby=on -c max_wal_senders=10 -c wal_keep_size=512MB
