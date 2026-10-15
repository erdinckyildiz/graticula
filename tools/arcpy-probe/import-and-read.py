"""Import the Esri-written archive into a server, publish both classes, and print what the served layers and the
shared-domain list say. Usage: python import_probe.py <server> <user> <password> <archive.zip> <service name>"""
import json
import ssl
import sys
import time
import urllib.request
import uuid

server, user, password, path, service = sys.argv[1:6]
ctx = ssl._create_unverified_context()


def call(method, url, body=None, headers=None):
    req = urllib.request.Request(server + url, data=body, method=method, headers=headers or {})
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, context=ctx) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


token = None
token = json.loads(call("POST", "/rest/auth/login", json.dumps({"name": user, "password": password}).encode(),
                        {"Content-Type": "application/json"})[1])["token"]


def settle(job):
    for _ in range(120):
        found = json.loads(call("GET", f"/admin/jobs/{job}")[1])
        if found["status"] not in ("queued", "running"):
            return found
        time.sleep(1)
    return found


boundary = uuid.uuid4().hex
data = open(path, "rb").read()
body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"name\"\r\n\r\n{service}\r\n"
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"esri_written.gdb.zip\"\r\n"
        f"Content-Type: application/zip\r\n\r\n").encode() + data + f"\r\n--{boundary}--\r\n".encode()
status, opened = call("POST", "/admin/hosted/import", body, {"Content-Type": f"multipart/form-data; boundary={boundary}"})
print("import", status, opened[:300])
inspected = settle(json.loads(opened)["job"])
print("inspection", inspected["status"], inspected.get("failure"), (inspected.get("detail") or "")[:1500])

status, published = call("POST", "/admin/hosted/geodatabase", json.dumps(
    {"archive": json.loads(opened)["job"], "service": service, "layers": ["pipes", "valves"]}).encode(),
    {"Content-Type": "application/json"})
print("publish", status, published[:300])
if status == 202:
    landed = settle(json.loads(published)["job"])
    print("published", landed["status"], landed.get("failure"), (landed.get("detail") or "")[:2500])

for i in (0, 1):
    _, doc = call("GET", f"/rest/services/hosted/{service}/FeatureServer/{i}?f=json")
    d = json.loads(doc)
    print(f"\nlayer {i}", d.get("name"), "typeIdField", d.get("typeIdField"), "subtypeField", d.get("subtypeField"))
    for f in d.get("fields", []):
        if f.get("domain"):
            dom = f["domain"]
            print("  field", f["name"], dom.get("type"), dom.get("name"),
                  [c["code"] for c in dom.get("codedValues", [])] or dom.get("range"))
    for t in d.get("types", []):
        print("  type", t.get("id"), t.get("name"), "templates", len(t.get("templates", [])),
              {k: (v.get("name"), [c["code"] for c in v.get("codedValues", [])] or v.get("range"))
               for k, v in (t.get("domains") or {}).items() if v.get("type") != "inherited"})

_, shared = call("GET", "/admin/domains")
print("\nshared domains:")
for d in json.loads(shared) if shared.startswith("[") else json.loads(shared).get("domains", []):
    print(" ", json.dumps(d)[:300])
