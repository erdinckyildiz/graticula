"""Reads a File Geodatabase, so the server does not have to.

ADR-037: job workers come in two kinds, and this is the second one. A .NET worker runs work our own
runtime must bound and kill; this runs work whose value is in somebody else's ecosystem — foreign
formats now, foreign tools later (Q-17b). GDAL lives here and in no other artefact, which keeps A-016
and ADR-009 §2.2 intact: the serving container ships none.

The contract is `GeometryWorkerPool`'s, deliberately. One JSON request per line on stdin, one JSON
response per line on stdout, and nothing else on stdout ever — diagnostics go to stderr, which the
server leaves attached so a traceback reaches its log instead of vanishing. The server owns the
timeout and kills us; there is no cooperative deadline here, because a worker that could be trusted
to stop on request would not need to be a separate process.

Two operations, and the split is the picker's:

    {"op": "layers",  "archive": "/data/x.gdb.zip"}
    {"op": "convert", "archive": "/data/x.gdb.zip", "layer": "roads", "out": "/work/roads.parquet"}

`layers` is cheap and answers *what is in here*. `convert` is the expensive one. Keeping them apart
means the console can offer a choice before anything is read in full, which matters at the scale the
owner's own archives are: one of the three holds 55 layers.

**Nothing is extracted to disk.** GDAL's `/vsizip/` reads inside the archive, member by member, and
that was measured against three real geodatabases rather than assumed — ADR-037's disk-dependency
consequence was withdrawn because of it.
"""

from __future__ import annotations

import json
import sys
import traceback
from typing import Any

import pyogrio


# The Python worker's own version, reported in every response so a server log can tell which build
# answered. Bumped by hand; there is no package here to carry one.
VERSION = "1"


def _vsi(archive: str) -> str:
    """Turns a path to a `.gdb.zip` into something GDAL will open without unpacking it.

    A geodatabase is a *directory*, so a `.gdb` arrives zipped and `/vsizip/` is how GDAL reads into
    one. A path that is already a `/vsi*` handle is passed through, which is what lets a caller point
    at object storage later without this function learning about it.
    """
    if archive.startswith("/vsi"):
        return archive

    if archive.endswith(".zip"):
        return f"/vsizip/{archive}"

    return archive


def _layers(archive: str) -> dict[str, Any]:
    """Answers what is in the archive, without reading any of it.

    Every layer the driver reports is listed, including the ones nobody wants to publish — the
    `__ATTACH` tables a geodatabase carries for attachments have no geometry, and one of the owner's
    archives holds six of them beside six feature classes. **Filtering them out here would be
    deciding for the screen**, and the screen needs to say *why* something is not offered rather
    than quietly shortening its list. So the geometry type is reported and the caller decides.
    """
    found = pyogrio.list_layers(_vsi(archive))

    layers = []

    for name, geometry in found:
        info = pyogrio.read_info(_vsi(archive), layer=name)

        layers.append({
            "name": name,

            # None for a table — a geodatabase's attachment and relationship tables have no geometry,
            # and that is the fact a picker needs to tell them apart from a feature class.
            "geometry": geometry,

            "features": int(info.get("features", 0)),

            # <b>The authoritative code, not a guess.</b> Our shapefile import demands `srid` from the
            # operator because a `.prj` is bare WKT and matching it to a code by comparing strings is
            # how a layer comes to declare a system it is not in. GDAL resolves it through PROJ's
            # authority database instead, so here there is nothing to ask.
            "crs": info.get("crs"),

            "fields": [
                {"name": field, "type": str(dtype)}
                for field, dtype in zip(
                    info.get("fields", []), info.get("dtypes", []), strict=False)
            ],
        })

    return {"layers": layers}


def _convert(archive: str, layer: str, out: str) -> dict[str, Any]:
    """Writes one layer to GeoParquet, which is the boundary Q-74 chose.

    Q-74 asked how data crosses between our runtime and Python's, rejected handing Python a database
    connection, and chose *materialise to a file the tool reads* — concluding the format wants to be
    Arrow or GeoParquet because `geopandas`, `pyarrow` and `shapely` read them without a shim. This is
    that decision executed in the other direction: the same boundary carries a geodatabase in.

    **`use_arrow` is not optional here.** It is the path that makes a large layer affordable, and
    without it this becomes a row-at-a-time loop in Python over data measured in hundreds of
    thousands of features.
    """
    frame = pyogrio.read_dataframe(_vsi(archive), layer=layer, use_arrow=True)

    # <b>What is being lost, counted before it is lost.</b> ADR-024 condition 5 made the shapefile
    # import report a dropped Z or M at the moment of the loss rather than in a document somebody
    # reads later, and most of the owner's real layers are 3D — so this will fire nearly every time
    # rather than occasionally. Reported, not prevented: PostGIS holds Z, but our import path does not
    # carry it yet, and saying so is the honest half we can do today.
    geometry = frame.geometry if hasattr(frame, "geometry") else None

    has_z = bool(geometry is not None and geometry.has_z.any())

    frame.to_parquet(out, geometry_encoding="WKB", write_covering_bbox=False)

    return {
        "layer": layer,
        "out": out,
        "features": int(len(frame)),
        "crs": str(frame.crs) if getattr(frame, "crs", None) is not None else None,

        # The caller decides what to say about it; this only reports.
        "hasZ": has_z,
    }


def _handle(request: dict[str, Any]) -> dict[str, Any]:
    """Runs one request, or names why it cannot."""
    operation = request.get("op")

    if operation == "layers":
        return _layers(request["archive"])

    if operation == "convert":
        return _convert(request["archive"], request["layer"], request["out"])

    if operation == "ping":
        # <b>A liveness answer, because the server needs one before it trusts a new process.</b>
        # `GeometryWorkerPool` gives a newly launched worker a window to become responsive; the same
        # applies here and an import job is a bad first request to find out with.
        return {"pong": True, "gdal": pyogrio.__gdal_version_string__}

    raise ValueError(
        f"'{operation}' is not an operation. This worker answers 'layers', 'convert' and 'ping'.")


def main() -> int:
    """One line in, one line out, until stdin closes.

    Every response carries `ok`. A failure is a response rather than an exit: the server has a job
    row to write a reason into, and a worker that died silently would leave that row saying only
    *failed* — which the store refuses precisely because nobody can act on it.
    """
    for line in sys.stdin:
        line = line.strip()

        if not line:
            continue

        try:
            request = json.loads(line)
        except json.JSONDecodeError as bad:
            _write({"ok": False, "error": f"The request was not JSON: {bad}"})
            continue

        try:
            answer = _handle(request)
            answer["ok"] = True
            answer["worker"] = VERSION
            _write(answer)
        except Exception as failed:  # noqa: BLE001 — every failure is reportable, none is fatal
            # The traceback goes to stderr for the server's log; the caller gets a sentence.
            traceback.print_exc(file=sys.stderr)

            _write({
                "ok": False,
                "worker": VERSION,
                "error": f"{type(failed).__name__}: {failed}",
            })

    return 0


def _write(answer: dict[str, Any]) -> None:
    """Writes one response and flushes it.

    **The flush is load-bearing.** A pipe buffers, and a server waiting on a line that is sitting in
    this process's buffer looks exactly like a worker that hung — which the server would then kill,
    correctly and for the wrong reason.
    """
    sys.stdout.write(json.dumps(answer, separators=(",", ":")) + "\n")
    sys.stdout.flush()


if __name__ == "__main__":
    sys.exit(main())
