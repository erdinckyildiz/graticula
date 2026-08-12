# osm2pgsql, for loading OpenStreetMap extracts into the experiment PostGIS.
#
# A separate image rather than apt-installing into the postgis container, so the
# database container stays exactly the published image and can be recreated
# without losing tooling.
FROM debian:bookworm-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      osm2pgsql \
      postgresql-client \
      ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /data
ENTRYPOINT ["/bin/bash"]
