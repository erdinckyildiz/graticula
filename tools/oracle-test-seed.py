"""The Oracle tables ADR-205's tests read, made from nothing: TR_YER, 1,421 places, and TR_KARA, two polygons of which the
larger is one Oracle calls invalid and PostGIS does not (condition 8).

Synthetic and deterministic, so CI (condition 3) and any developer get the same rows without the showcase's data, whose
provenance is not this repository's to redistribute. The shapes the tests depend on are reproduced, not the places:

- TR_YER: OBJECTID NUMBER(10) 1..1421, AD, TUR ('city' every 13th, 109 of them), NUFUS as text, a point each, spread
  over 25.6-44.8 E and 35.8-42.0 N, with Ankara as id 2 at its own coordinates.
- TR_KARA: an ellipse of 4,000 vertices over Anatolia in which every hundredth vertex is repeated — ORA-13356, adjacent
  points redundant, which PostGIS's ST_IsValid accepts — and a small square of an island.

Usage, with the oracle: locator of a schema the tables may be (re)created in:
    GRATICULA_TEST_ORACLE='oracle:{...}' python tools/oracle-test-seed.py
Needs python-oracledb (pip install oracledb). Drops and recreates the two tables; touches nothing else.
"""
import json
import math
import os
import struct
import sys

import oracledb

locator = os.environ.get("GRATICULA_TEST_ORACLE", "")
if not locator.startswith("oracle:"):
    sys.exit("GRATICULA_TEST_ORACLE must be an oracle: locator")

source = json.loads(locator[len("oracle:"):])
connection = oracledb.connect(
    user=source["user"], password=source["password"],
    dsn=f"{source['host']}:{source.get('port', 1521)}/{source['service']}")
cursor = connection.cursor()

for table in ("TR_YER", "TR_KARA"):
    cursor.execute(f"drop table if exists {table} purge")
    cursor.execute("delete from user_sdo_geom_metadata where table_name = :t", t=table)

cursor.execute("""create table tr_yer (
    objectid number(10) primary key, ad varchar2(200), tur varchar2(20), nufus varchar2(20), shape sdo_geometry)""")
cursor.execute("""create table tr_kara (objectid number(10) primary key, ad varchar2(200), shape sdo_geometry)""")


def lcg(seed):
    """A fixed sequence of fractions in [0, 1), the same on every machine."""
    state = seed
    while True:
        state = (state * 6364136223846793005 + 1442695040888963407) % (1 << 64)
        yield (state >> 11) / float(1 << 53)


fractions = lcg(205)
names = ["Ankara", "Antalya", "Adana", "Anamur", "Bursa", "Konya", "Kayseri", "Samsun", "Trabzon", "Van", "Mardin"]
rows = []
for objectid in range(1, 1422):
    x = 25.6 + next(fractions) * (44.8 - 25.6)
    y = 35.8 + next(fractions) * (42.0 - 35.8)
    if objectid == 1:
        x, y = 25.6, 38.0       # the western edge
    if objectid == 2:
        x, y = 32.8597, 39.9334  # Ankara
    if objectid == 3:
        x, y = 35.0, 42.0       # the northern edge
    name = "Ankara" if objectid == 2 else f"{names[objectid % len(names)]} {objectid}"
    kind = "city" if objectid % 13 == 0 else "town"
    people = None if objectid % 17 == 0 else str(1000 + (objectid * 7919) % 100000)
    rows.append((objectid, name, kind, people, round(x, 6), round(y, 6)))

cursor.executemany(
    """insert into tr_yer values (:1, :2, :3, :4,
       sdo_geometry(2001, 4326, sdo_point_type(:5, :6, null), null, null))""", rows)


def polygon_wkb(ring):
    return struct.pack("<BII", 1, 3, 1) + struct.pack("<I", len(ring)) + b"".join(struct.pack("<dd", *p) for p in ring)


# Counter-clockwise, as Oracle wants an outer ring; every hundredth vertex twice.
ellipse = []
for i in range(4000):
    angle = 2 * math.pi * i / 4000
    point = (round(35.2 + 9.4 * math.cos(angle), 6), round(39.0 + 2.9 * math.sin(angle), 6))
    ellipse.append(point)
    if i % 100 == 50:
        ellipse.append(point)
ellipse.append(ellipse[0])

island = [(26.0, 40.0), (26.2, 40.0), (26.2, 40.2), (26.0, 40.2), (26.0, 40.0)]

for objectid, ring in ((1, ellipse), (2, island)):
    cursor.execute(
        """declare g sdo_geometry := sdo_util.from_wkbgeometry(:w);
           begin g.sdo_srid := 4326; insert into tr_kara values (:i, 'Türkiye', g); end;""",
        w=connection.createlob(oracledb.DB_TYPE_BLOB, polygon_wkb(ring)), i=objectid)

for table in ("TR_YER", "TR_KARA"):
    cursor.execute(
        """insert into user_sdo_geom_metadata values (:t, 'SHAPE',
           sdo_dim_array(sdo_dim_element('X', -180, 180, 0.05), sdo_dim_element('Y', -90, 90, 0.05)), 4326)""",
        t=table)
    cursor.execute(f"create index {table}_sx on {table} (shape) indextype is mdsys.spatial_index_v2")

connection.commit()

cursor.execute("select count(*) from tr_yer")
places = cursor.fetchone()[0]
cursor.execute("""select objectid, substr(sdo_geom.validate_geometry_with_context(shape, 0.05), 1, 5)
                    from tr_kara order by objectid""")
validity = dict(cursor.fetchall())
print(f"TR_YER {places} places; TR_KARA validity {validity}")

if places != 1421 or validity.get(1) != "13356" or validity.get(2) != "TRUE":
    sys.exit("the seed is not what the tests were written against")
