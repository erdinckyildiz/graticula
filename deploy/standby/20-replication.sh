#!/bin/bash
# ADR-319 §5.9: a datastore volume made with the standby override allows replication from its peer. Run by the image's
# entrypoint once, when it initialises an empty volume; datastore-ha.sh does the same for a volume made before.
set -e
echo "host replication all all scram-sha-256" >> "$PGDATA/pg_hba.conf"
