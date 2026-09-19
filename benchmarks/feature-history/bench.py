"""What keeping a layer's history costs a write — ADR-078 §4a.

The same applyEdits batches against the same hosted layer, with history off and then on, end to end
through a running server: 500 attribute updates in one batch, 500 adds in one batch, and those 500
deleted in one batch. Seven timed runs after two warm-ups, median reported.

Usage:  python bench.py https://127.0.0.1:18472 <user> <password> <folder/service> <layer name> <text field>
"""

import json
import ssl
import statistics
import sys
import time
import urllib.parse
import urllib.request

BASE, USER, PASSWORD, SERVICE, LAYER, FIELD = sys.argv[1:7]
CTX = ssl._create_unverified_context()
FS = f"/rest/services/{SERVICE}/FeatureServer/0"
N = 500
WARM, RUNS = 2, 7


def call(method, path, body=None, form=None, token=None):
    headers = {"Authorization": "Bearer " + token} if token else {}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if form is not None:
        data = urllib.parse.urlencode(form).encode()
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    request = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    with urllib.request.urlopen(request, context=CTX) as response:
        return json.loads(response.read() or b"null")


token = call("POST", "/rest/auth/login", {"name": USER, "password": PASSWORD})["token"]
page = call("GET", FS + f"/query?where=1%3D1&outFields=*&resultRecordCount={N}&f=json", token=token)
oid = page["objectIdFieldName"]
ids = [f["attributes"][oid] for f in page["features"]][:N]
template = page["features"][0]
assert len(ids) == N, f"the layer has {len(ids)} features; {N} are needed"


def timed(form):
    t0 = time.perf_counter()
    answer = call("POST", FS + "/applyEdits", form={**form, "f": "json"}, token=token)
    elapsed = (time.perf_counter() - t0) * 1000
    results = answer.get("updateResults") or answer.get("addResults") or answer.get("deleteResults")
    assert results and all(r["success"] for r in results), answer
    return elapsed, results


def update(i):
    edits = [{"attributes": {oid: x, FIELD: f"run {i}"}} for x in ids]
    return timed({"updates": json.dumps(edits)})[0]


def add_then_delete():
    adds = [{"attributes": {FIELD: "added"}, "geometry": template.get("geometry")} for _ in range(N)]
    added_ms, results = timed({"adds": json.dumps(adds)})
    new_ids = ",".join(str(r["objectId"]) for r in results)
    deleted_ms, _ = timed({"deletes": new_ids})
    return added_ms, deleted_ms


def measure():
    for i in range(WARM):
        update(i)
        add_then_delete()
    updates, adds, deletes = [], [], []
    for i in range(RUNS):
        updates.append(update(100 + i))
        a, d = add_then_delete()
        adds.append(a)
        deletes.append(d)
    return statistics.median(updates), statistics.median(adds), statistics.median(deletes)


call("POST", f"/admin/layers/{LAYER}/history", {"enabled": False}, token=token)
off = measure()
call("POST", f"/admin/layers/{LAYER}/history", {"enabled": True}, token=token)
on = measure()
call("POST", f"/admin/layers/{LAYER}/history", {"enabled": False}, token=token)

print(f"| {N} features in one applyEdits | history off | history on | ratio |")
print("|---|---|---|---|")
for label, a, b in zip(("updates", "adds", "deletes"), off, on):
    print(f"| {label} | {a:.0f} ms | {b:.0f} ms | {b / a:.2f}× |")
