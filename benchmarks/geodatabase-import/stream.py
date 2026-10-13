"""ADR-038 condition 1: a geodatabase layer past ImportLimits.Default's 50 MB and million features, imported end to end.

Usage: python stream.py <base-url> <user> <password> <reader-exe> <work-dir> [features]

Writes a GeoJSON of <features> points (default 1,200,000) with four attributes, has the import reader turn it into a
File Geodatabase (the reader's own `export`, so the archive is what GDAL writes), zips it, uploads it through
/admin/hosted/import, publishes the layer through /admin/hosted/geodatabase, and waits for the job. Prints the sizes,
the time each step took, the row count the published layer answers, and the job's own report. Drops the service at
the end.

The server's memory is read by the caller from outside (the process's peak working set, with the server restarted
before each run), because the process being measured is the one this script is talking to.
"""
import io
import json
import os
import random
import ssl
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import uuid
import zipfile

BASE, USER, PASSWORD, READER, WORK = sys.argv[1:6]
FEATURES = int(sys.argv[6]) if len(sys.argv) > 6 else 1_200_000
SERVICE = "zz_stream_" + uuid.uuid4().hex[:8]

CTX = ssl.create_default_context()
CTX.check_hostname = False
CTX.verify_mode = ssl.CERT_NONE


def call(method, path, body=None, headers=None, token=None, timeout=600):
    request = urllib.request.Request(BASE + path, data=body, method=method, headers=dict(headers or {}))
    if token:
        request.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(request, context=CTX, timeout=timeout) as response:
            return response.status, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


def settled(token, job):
    while True:
        status, body = call("GET", f"/admin/jobs/{job}", token=token)
        found = json.loads(body)
        if found.get("status") not in ("queued", "running"):
            return found
        time.sleep(1)


os.makedirs(WORK, exist_ok=True)
geojson = os.path.join(WORK, "points.geojson")
gdb = os.path.join(WORK, "points.gdb")

def write_archive():
    """The points as GeoJSON, then as a File Geodatabase written by the reader's own export."""
    started = time.time()
    random.seed(20261013)
    with open(geojson, "w", encoding="utf-8") as out:
        out.write('{"type":"FeatureCollection","features":[\n')
        for i in range(FEATURES):
            x = 26.0 + random.random() * 18.0
            y = 36.0 + random.random() * 6.0
            out.write(("," if i else "") + json.dumps({
                "type": "Feature",
                "geometry": {"type": "Point", "coordinates": [round(x, 6), round(y, 6)]},
                "properties": {"code": i, "name": f"point {i:07d}", "height": round(random.random() * 2000, 2),
                               "kind": random.choice(["well", "spring", "borehole", "gauge"])},
            }) + "\n")
        out.write("]}\n")
    print(f"geojson: {FEATURES:,} features, {os.path.getsize(geojson) / 1e6:,.1f} MB, {time.time() - started:.1f} s")

    started = time.time()
    answer = subprocess.run([READER], input=json.dumps({
        "op": "export", "in": geojson.replace("\\", "/"), "out": gdb.replace("\\", "/"), "format": "fgdb",
        "layer": "points",
    }) + "\n", capture_output=True, text=True, timeout=1800)
    said = [line for line in answer.stdout.splitlines() if line.startswith("{")]
    print(f"export: {said[-1] if said else answer.stderr[-400:]} ({time.time() - started:.1f} s)")


# The same archive for every run once written, so two builds are measured importing the same bytes.
if os.path.isdir(gdb):
    print(f"geodatabase: reusing {gdb}")
else:
    write_archive()

size = sum(os.path.getsize(os.path.join(gdb, name)) for name in os.listdir(gdb))
buffer = io.BytesIO()
with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
    for name in os.listdir(gdb):
        archive.write(os.path.join(gdb, name), f"points.gdb/{name}")
zipped = buffer.getvalue()
print(f"geodatabase: {size / 1e6:,.1f} MB on disk, {len(zipped) / 1e6:,.1f} MB zipped")

status, body = call("POST", "/rest/auth/login", json.dumps({"name": USER, "password": PASSWORD}).encode(),
                    {"Content-Type": "application/json"})
token = json.loads(body)["token"]

boundary = uuid.uuid4().hex
form = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"name\"\r\n\r\n{SERVICE}\r\n"
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"points.gdb.zip\"\r\n"
        f"Content-Type: application/zip\r\n\r\n").encode() + zipped + f"\r\n--{boundary}--\r\n".encode()

try:
    started = time.time()
    status, body = call("POST", "/admin/hosted/import", form,
                        {"Content-Type": f"multipart/form-data; boundary={boundary}"}, token)
    if status != 202:
        sys.exit(f"upload answered {status}: {body[:400]}")
    upload = json.loads(body)["job"]
    inspected = settled(token, upload)
    print(f"inspection: {inspected['status']} ({time.time() - started:.1f} s)")

    started = time.time()
    status, body = call("POST", "/admin/hosted/geodatabase", json.dumps(
        {"archive": upload, "service": SERVICE,
         "layers": ["points"]}).encode(), {"Content-Type": "application/json"}, token)
    if status not in (200, 201, 202):
        sys.exit(f"publish answered {status}: {body[:400]}")
    published = settled(token, json.loads(body)["job"]) if status == 202 else {"status": "done", "detail": body}
    print(f"publish: {published['status']} ({time.time() - started:.1f} s)")
    print(f"  detail: {(published.get('detail') or '')[:600]}")
    print(f"  failure: {(published.get('failure') or '')[:600]}")

    status, body = call("GET", f"/rest/services/hosted/{SERVICE}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json",
                        token=token)
    print(f"count: {body[:200]}")
finally:
    status, body = call("DELETE", f"/admin/featureservices/{SERVICE}?folder=hosted&drop=true", token=token)
    print(f"dropped: {status}")
