# The datastore image (ADR-016 §2).
#
# A thin derived image rather than stock postgis/postgis, and the reason is a
# promise: Q-32 committed to an appliance WE configure, back up and upgrade, and
# that cannot be promised about an image we do not build. Today the difference
# is small — an init script and a version stamp. It is here so that when backup
# and upgrade agents arrive they have somewhere to go that is not "edit your
# compose file".

FROM postgis/postgis:16-3.4

# Stamped so a running datastore can be asked what it is, rather than inferred
# from a tag somebody may have moved.
LABEL org.opencontainers.image.title="gis-server datastore" \
      org.opencontainers.image.description="PostGIS, configured as the gis-server platform store and hosted datastore."

ENV GIS_SERVER_DATASTORE_VERSION=1

# Runs once, on an empty data directory, in filename order.
COPY datastore/ /docker-entrypoint-initdb.d/
