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
import os
import sys
import time
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


# ------------------------------------------------------------------------------ the claiming loop


def _serve(dsn: str, scratch: str, idle_seconds: float = 2.0) -> int:
    """Claims jobs from the platform store and runs them until killed.

    **ADR-011 §3.2's mechanism, from this side.** The worker claims its own work with
    `SELECT … FOR UPDATE SKIP LOCKED`; the server never invokes it, which is what lets the two live in
    separate containers (ADR-016 §2) without the server holding a Docker socket or the worker exposing
    a listener.

    **It touches the platform store and never the datastore.** ADR-037 §5: the job table is ours to
    claim from, and the features are not ours to write — this writes GeoParquet and the server imports
    it. The first version of that ADR said *never holds a database connection*, which contradicted
    ADR-011 and has been corrected to name the store.

    **Polling rather than `LISTEN`/`NOTIFY`, and the interval is a number rather than a detail.**
    ADR-011 §3.3 chose push with polling as the fallback, and says the interval *"must be a documented
    number, because it is the floor on how long an administrator waits after pressing a button."* Two
    seconds is that floor. Push is the optimisation and is not built; correctness does not depend on it,
    which §3.3 also requires.

    **There is no lease.** A job this worker claims and then dies holding stays `running` for ever, and
    nothing reclaims it. ADR-011 §3.3 names the reclaim sweep; this is not it, and the first stuck job
    is the evidence a timeout would otherwise be guessed from.
    """
    import psycopg

    print(f"worker {VERSION} claiming from the job table every {idle_seconds}s", file=sys.stderr)

    with psycopg.connect(dsn, autocommit=False) as connection:
        while True:
            job = _claim(connection)

            if job is None:
                time.sleep(idle_seconds)
                continue

            job_id, kind, detail = job

            print(f"took {job_id} ({kind})", file=sys.stderr)

            try:
                asked = json.loads(detail) if detail else {}

                if kind == "geodatabase.inspect":
                    answer = _layers(asked["archive"])
                elif kind == "geodatabase.import":
                    answer = _convert(asked["archive"], asked["layer"], asked["out"])
                else:
                    raise ValueError(f"'{kind}' is not a job this worker runs.")

                _finish(connection, job_id, "done", json.dumps(answer), None)
                print(f"done {job_id}", file=sys.stderr)
            except Exception as failed:  # noqa: BLE001 — a job's failure is data, not a crash
                traceback.print_exc(file=sys.stderr)

                # <b>The reason is written to the row, because the store refuses a failure without
                # one.</b> A job that says only *failed* is one nobody can act on.
                _finish(
                    connection, job_id, "failed", None, f"{type(failed).__name__}: {failed}")


def _claim(connection: object) -> tuple[str, str, str | None] | None:
    """Takes the oldest queued job this worker can run, or nothing.

    The statement is the one `PostgresJobStore.ClaimAsync` uses, and it is here rather than called
    through the server because the worker is a separate container with no route into it.
    """
    with connection.cursor() as cursor:  # type: ignore[attr-defined]
        cursor.execute(
            """
            with taken as (
                select id from job
                 where status = 'queued' and kind in ('geodatabase.inspect', 'geodatabase.import')
                 order by created_at
                 limit 1
                 for update skip locked
            )
            update job
               set status = 'running', started_at = now()
             where id in (select id from taken)
            returning id::text, kind, detail
            """)

        row = cursor.fetchone()

    connection.commit()  # type: ignore[attr-defined]

    return row


def _progress(connection: object, job_id: str, percent: int) -> None:
    """Reports how far along, while running."""
    with connection.cursor() as cursor:  # type: ignore[attr-defined]
        cursor.execute(
            "update job set progress = %s where id = %s and status = 'running'",
            (max(0, min(100, percent)), job_id))

    connection.commit()  # type: ignore[attr-defined]


def _finish(
    connection: object,
    job_id: str,
    status: str,
    detail: str | None,
    failure: str | None,
) -> None:
    """Records the ending, and completes the progress only on success."""
    with connection.cursor() as cursor:  # type: ignore[attr-defined]
        cursor.execute(
            """
            update job
               set status      = %s,
                   finished_at = now(),
                   progress    = case when %s = 'done' then 100 else progress end,
                   detail      = coalesce(%s, detail),
                   failure     = %s
             where id = %s and status in ('queued', 'running')
            """,
            (status, status, detail, failure, job_id))

    connection.commit()  # type: ignore[attr-defined]


if __name__ == "__main__":
    # <b>Two modes, and the pipe one is not a convenience.</b> `--serve` is how the worker runs in a
    # deployment; reading stdin is how it is tested and measured without a database, which is how
    # every number in file-geodatabase-readers.md §8d was taken.
    if "--serve" in sys.argv:
        sys.exit(_serve(
            os.environ["GRATICULA_PLATFORM_STORE"],
            os.environ.get("GRATICULA_IMPORT_SCRATCH", "/import")))

    sys.exit(main())
