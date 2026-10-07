# ADR-078 condition 2: an archived layer on the fixture, a version before and after an edit.
# Reads an administrator token from C:/temp/fx.token; writes the moment to C:/temp/hist-moment.txt.
import json, ssl, sys, time, urllib.parse, urllib.request
B = "https://127.0.0.1:18491"
C = ssl._create_unverified_context()
T = open("C:/temp/fx.token").read().strip()

def call(m, p, body=None, form=None):
    h = {"Authorization": "Bearer " + T}
    d = None
    if body is not None:
        d = json.dumps(body).encode(); h["Content-Type"] = "application/json"
    if form is not None:
        d = urllib.parse.urlencode(form).encode(); h["Content-Type"] = "application/x-www-form-urlencoded"
    try:
        with urllib.request.urlopen(urllib.request.Request(B + p, data=d, method=m, headers=h), context=C) as r:
            return json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        print(m, p, e.code, e.read()[:300]); raise

call("POST", "/admin/hosted/define", body={"name": "zz_hist_sdk", "geometryType": "Point",
     "fields": [{"name": "label", "type": "Text"}], "sharing": "public"})
FS = "/rest/services/hosted/zz_hist_sdk/FeatureServer/0"
adds = [{"attributes": {"label": "Ankara, before"}, "geometry": {"x": 3650000, "y": 4850000, "spatialReference": {"wkid": 3857}}},
        {"attributes": {"label": "İzmir, deleted later"}, "geometry": {"x": 3020000, "y": 4650000, "spatialReference": {"wkid": 3857}}}]
r = call("POST", FS + "/applyEdits", form={"adds": json.dumps(adds), "f": "json"})
ids = [x["objectId"] for x in r["addResults"]]
print(call("POST", "/admin/layers/zz_hist_sdk/history", body={"enabled": True}))
time.sleep(1.5)
before = int(time.time() * 1000)
time.sleep(1.5)
call("POST", FS + "/applyEdits", form={"updates": json.dumps([{"attributes": {"objectid": ids[0], "label": "Ankara, after"},
     "geometry": {"x": 3660000, "y": 4860000, "spatialReference": {"wkid": 3857}}}]), "deletes": str(ids[1]), "f": "json"})
print("moment", before)
open("C:/temp/hist-moment.txt", "w").write(str(before))
meta = call("GET", FS + "?f=json")
print({k: meta.get(k) for k in ("isDataArchived", "isDataVersioned")}, meta.get("advancedQueryCapabilities", {}).get("supportsQueryWithHistoricMoment"))
