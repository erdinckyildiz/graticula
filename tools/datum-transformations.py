#!/usr/bin/env python3
"""Generates `src/Graticula.Core/Geometry/TransformationRegister.cs` from PROJ's own register.

**Why this exists.** ArcGIS's `findTransformations` lists the datum transformations
between two geographic references, ranked by accuracy, and a client then names one
in `project`. The list lives in PROJ's `proj.db`; this server has no PROJ in its
serving process (ADR-009 §2.2), and PostGIS exposes no SQL that enumerates the
candidate operations between two references (Q-100, measured 2026-08-26). So, as
ADR-060 did for axis order, the list is read from the register at build time and
carried as data: every non-deprecated EPSG and ESRI transformation between two
geographic 2D references, with its accuracy, its area of use, and whether it needs
a grid. Applying one stays the datastore's work (`ST_TransformPipeline`).

**Run it like this**, with the database PostGIS itself is using — the same file
`tools/axis-order.py` reads, so the two registers are one vintage:

    docker cp gis-experiment-postgis:/usr/share/proj/proj.db /tmp/proj.db
    python tools/datum-transformations.py /tmp/proj.db

`TheTransformationRegisterIsNotStaleTests` regenerates and compares.
"""

import os
import sqlite3
import sys

GEOGRAPHIC_2D = """
SELECT auth_name, code FROM geodetic_crs
WHERE type = 'geographic 2D' AND deprecated = 0 AND auth_name IN ('EPSG', 'ESRI')
  AND code GLOB '[0-9]*'
"""

OPERATIONS = """
SELECT o.table_name, o.auth_name, o.code, o.name,
       o.source_crs_auth_name, o.source_crs_code, o.target_crs_auth_name, o.target_crs_code,
       o.accuracy
FROM coordinate_operation_view o
WHERE o.deprecated = 0
  AND o.auth_name IN ('EPSG', 'ESRI')
  AND o.code GLOB '[0-9]*'
  AND o.table_name IN ('helmert_transformation', 'grid_transformation',
                       'other_transformation', 'concatenated_operation')
"""

AREA = """
SELECT e.west_lon, e.south_lat, e.east_lon, e.north_lat
FROM usage u JOIN extent e ON e.auth_name = u.extent_auth_name AND e.code = u.extent_code
WHERE u.object_table_name = ? AND u.object_auth_name = ? AND u.object_code = ?
"""

# A concatenated operation needs a grid when any of its steps does.
STEPS = """
SELECT step_auth_name, step_code FROM concatenated_operation_step
WHERE operation_auth_name = ? AND operation_code = ?
"""


def area(connection, table, auth, code):
    """The union of an operation's areas of use, as west, south, east, north."""
    boxes = connection.execute(AREA, (table, auth, code)).fetchall()

    if not boxes:
        return (-180.0, -90.0, 180.0, 90.0)

    # An area crossing the antimeridian (west > east) is taken as the whole width: a
    # filter that keeps too much is wrong by one extra candidate, one that keeps too
    # little hides the only transformation a Pacific dataset has.
    west = min(b[0] for b in boxes)
    east = max(b[2] for b in boxes)

    if any(b[0] > b[2] for b in boxes):
        west, east = -180.0, 180.0

    return (west, min(b[1] for b in boxes), east, max(b[3] for b in boxes))


def needs_grid(connection, table, auth, code, grids):
    """Whether applying it reads a grid file, which a datastore may not have."""
    if table == 'grid_transformation':
        return True

    if table == 'concatenated_operation':
        return any((a, c) in grids for a, c in connection.execute(STEPS, (auth, code)))

    return False


def number(value):
    """A number as C# reads it, without a trailing .0 where none is needed."""
    text = repr(float(value))
    return text[:-2] if text.endswith('.0') else text


def main(argv):
    """Writes the register, or says why it cannot."""
    if len(argv) not in (2, 3):
        print(__doc__, file=sys.stderr)
        return 2

    where = argv[1]
    destination = argv[2] if len(argv) == 3 else os.path.join(
        "src", "Graticula.Core", "Geometry", "TransformationRegister.cs")

    if not os.path.exists(where):
        print(f"{where} is not there. Copy it out of the PostGIS container first.", file=sys.stderr)
        return 1

    connection = sqlite3.connect(where)
    have = dict(connection.execute("SELECT key, value FROM metadata"))
    version = f"{have.get('EPSG.VERSION', 'unknown')} ({have.get('EPSG.DATE', 'no date')})"

    geographic = {(a, c) for a, c in connection.execute(GEOGRAPHIC_2D)}
    grids = {(a, c) for a, c in connection.execute(
        "SELECT auth_name, code FROM grid_transformation")}

    rows = []

    for table, auth, code, name, sa, sc, ta, tc, accuracy in connection.execute(OPERATIONS):
        if (sa, sc) not in geographic or (ta, tc) not in geographic:
            continue

        w, s, e, n = area(connection, table, auth, code)
        rows.append((
            'E' if auth == 'EPSG' else 'S', int(code), int(sc), int(tc),
            '' if accuracy is None else number(accuracy),
            number(w), number(s), number(e), number(n),
            'g' if needs_grid(connection, table, auth, code, grids) else '',
            name.replace('|', '/')))

    rows.sort(key=lambda r: (r[0], r[1]))
    body = "\n".join("|".join(str(v) for v in row) for row in rows)
    grid_count = sum(1 for r in rows if r[9] == 'g')

    text = f'''// <auto-generated>
//   Generated by tools/datum-transformations.py from PROJ's register — EPSG {version}.
//   Do not edit by hand: run the tool again against a newer proj.db instead.
// </auto-generated>

namespace Graticula.Geometries;

/// <summary>
/// Every datum transformation between two geographic references the register knows: {len(rows):,}
/// of them, {grid_count:,} needing a grid — ADR-160.
/// </summary>
/// <remarks>
/// <b>Generated rather than looked up</b>, as <see cref="AxisOrderRegister"/> is (ADR-060): the list lives in
/// PROJ's <c>proj.db</c>, which the serving process does not carry and PostGIS exposes no SQL to enumerate.
/// A line is authority (E for EPSG, S for ESRI), code, source, target, accuracy in metres (empty when the
/// register gives none), area of use as west, south, east, north, <c>g</c> when it reads a grid, and its name.
/// </remarks>
public static partial class TransformationRegister
{{
    private const string Lines = """
{body}
""";
}}
'''

    with open(destination, "w", encoding="utf-8", newline="") as handle:
        handle.write(text)

    print(f"wrote {destination}", file=sys.stderr)
    print(f"  EPSG register {version}", file=sys.stderr)
    print(f"  {len(rows)} transformations, {grid_count} needing a grid", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
