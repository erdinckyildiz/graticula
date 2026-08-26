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
#
# <b>Said `gis-server` until 2026-08-26 — [D-170](../docs/architecture-debt.md).</b>
# ADR-032 renamed the product on 2026-08-17 and `registers-check.py` has enforced it
# since, but only across `.md` and `.html`, so the one place carrying the old name into
# the artefact a user receives was never read. The two names ADR-032 §5 keeps are the
# `GisServer:*` configuration keys and the `gisserver` schema, both of which exist so
# that no deployment has to be reconfigured; an image label is neither — it is what the
# product calls itself to whoever inspects it.
LABEL org.opencontainers.image.title="Graticula datastore" \
      org.opencontainers.image.description="PostGIS, configured as the Graticula platform store and hosted datastore."

# Renamed with the label, and free to rename because nothing reads it: it is a stamp
# for a person running `docker inspect`, not a configuration key, and there is no
# released image for it to be a compatibility surface with ([D-19](../docs/architecture-debt.md)).
ENV GRATICULA_DATASTORE_VERSION=1

# Runs once, on an empty data directory, in filename order.
COPY datastore/ /docker-entrypoint-initdb.d/
