"""ADR-301 condition 5 and ADR-302 condition 3 on a local fixture: N records with a photograph each, submitted by a
member, reported by that member through the form's Inbox — time, the server's peak working set while it answers, and
the answer's size; then a 25-submission separate PDF report. Prints numbers only; no tokens or passwords.

usage: python inbox_report.py <root> <admin name> <admin password file> <server pid> [records]"""
import io
import json
import os
import sys
import threading
import time
import uuid
import zipfile

import psutil
import requests
import urllib3
from PIL import Image

urllib3.disable_warnings()
root, admin_name, password_file, pid = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
count = int(sys.argv[5]) if len(sys.argv) > 5 else 100
admin_password = open(password_file).read().strip()
s = requests.Session()
s.verify = False
run = uuid.uuid4().hex[:6]


def login(name, password):
    r = s.post(f"{root}/rest/auth/login", json={"name": name, "password": password})
    r.raise_for_status()
    return r.json()["token"]


def call(method, path, token, **kw):
    r = s.request(method, f"{root}{path}", headers={"Authorization": f"Bearer {token}"}, **kw)
    if r.status_code >= 400:
        raise SystemExit(f"{method} {path} -> {r.status_code}: {r.text[:300]}")
    return r


admin = login(admin_name, admin_password)

# ---- a member
member = f"zz_measure_{run}"
created = call("POST", "/admin/members", admin, json={"name": member, "role": "user", "userType": "creator"}).json()
first = login(member, created["password"])
new_password = "Zz-measure-" + uuid.uuid4().hex[:10] + "!"
call("POST", "/rest/auth/password", first, json={"currentPassword": created["password"], "newPassword": new_password})
token = login(member, new_password)

# ---- a form with a photograph, the Inbox on, published and shared with the organisation
form_id = f"zz_measure_{run}"
model = {"format": "xlsform/1", "sheets": [
    {"name": "survey", "columns": ["type", "name", "label"],
     "rows": [["geopoint", "konum", "Konum"], ["text", "ad", "Ad"], ["image", "foto", "Foto"]]},
    {"name": "settings", "columns": ["form_title", "form_id"], "rows": [["Measure " + form_id, form_id]]}]}
item = call("POST", "/admin/forms", admin, json={"title": "Measure " + form_id, "model": model}).json()["id"]
call("PUT", f"/admin/forms/{item}/inbox", admin, json={"enabled": True, "limit": 1000})
call("POST", f"/admin/forms/{item}/publish", admin, json={})
publication = call("PUT", f"/admin/forms/{item}/sharing", admin, json={"sharing": "organization"}).json()["publication"]
top = next(l for l in publication["layers"] if l["repeat"] == "")
submit = top["submitUrl"]


def photograph(i):
    # A distinct 1600x1200 JPEG per record: gradients with a little noise, about the size of a phone photograph after
    # Survey123's default resize. Distinct bytes, so nothing downstream can share one picture between records.
    import numpy
    rng = numpy.random.default_rng(i)
    y, x = numpy.mgrid[0:1200, 0:1600]
    rgb = numpy.stack([(x * 7 + i * 13) % 256, (y * 5 + i * 3) % 256, ((x + y) * 3) % 256], axis=-1).astype(numpy.int16)
    rgb = numpy.clip(rgb + rng.integers(-12, 12, rgb.shape), 0, 255).astype(numpy.uint8)
    out = io.BytesIO()
    Image.fromarray(rgb).save(out, "JPEG", quality=80)
    return out.getvalue()


started = time.perf_counter()
photo_bytes = 0
object_ids = []
for i in range(count):
    g = uuid.uuid4()
    adds = json.dumps([{"geometry": {"x": 29.0 + i * 0.001, "y": 41.0, "spatialReference": {"wkid": 4326}},
                        "attributes": {"globalid": "{%s}" % g, "ad": f"Record {i + 1}"}}])
    r = call("POST", f"{submit}/applyEdits", token, data={"f": "json", "useGlobalIds": "true", "adds": adds}).json()
    oid = r["addResults"][0]["objectId"]
    object_ids.append(oid)
    jpeg = photograph(i)
    photo_bytes += len(jpeg)
    call("POST", f"{submit}/{oid}/addAttachment?parentGlobalId={requests.utils.quote('{%s}' % g)}", token,
         data={"f": "json", "keywords": "foto"}, files={"attachment": (f"foto{i}.jpg", jpeg, "image/jpeg")})
print(f"submitted {count} records with a photograph each ({photo_bytes / 1e6:.1f} MB of photographs) in {time.perf_counter() - started:.1f} s")

# ---- a template: the answer, and the photograph placed at 400x300
W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
buffer = io.BytesIO()
with zipfile.ZipFile(buffer, "w") as z:
    z.writestr("[Content_Types].xml", '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
               '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>'
               '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
    z.writestr("_rels/.rels", '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
               '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
    paragraphs = ["Inspection: ${ad}", "Where: ${konum | getValue:\"x\"}, ${konum | getValue:\"y\"}", "${foto | size:400:300}"]
    z.writestr("word/document.xml", f'<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="{W}"><w:body>'
               + "".join(f'<w:p><w:r><w:t xml:space="preserve">{p}</w:t></w:r></w:p>' for p in paragraphs)
               + "<w:sectPr/></w:body></w:document>")
call("POST", f"/admin/forms/{item}/reports/templates", admin,
     files={"file": ("measure.docx", buffer.getvalue(), "application/vnd.openxmlformats-officedocument.wordprocessingml.document")})

process = psutil.Process(pid)


def measured(label, body):
    peak = {"rss": process.memory_info().rss, "children": 0}
    base = peak["rss"]
    done = threading.Event()

    def sample():
        while not done.is_set():
            try:
                peak["rss"] = max(peak["rss"], process.memory_info().rss)
                kids = sum(c.memory_info().rss for c in process.children(recursive=True))
                peak["children"] = max(peak["children"], kids)
            except psutil.Error:
                pass
            time.sleep(0.05)

    sampler = threading.Thread(target=sample)
    sampler.start()
    t = time.perf_counter()
    r = s.post(f"{root}/admin/forms/{item}/reports", headers={"Authorization": f"Bearer {token}"}, json=body)
    took = time.perf_counter() - t
    done.set()
    sampler.join()
    detail = "" if r.status_code == 200 else " " + r.text[:200]
    print(f"{label}: {r.status_code} in {took:.2f} s, {len(r.content) / 1e6:.2f} MB, server working set {base / 1e6:.0f} -> peak "
          f"{peak['rss'] / 1e6:.0f} MB (+{(peak['rss'] - base) / 1e6:.0f}), child processes' peak {peak['children'] / 1e6:.0f} MB{detail}")
    return r


measured(f"{count} records, separate .docx (zip), as the member through the Inbox",
         {"template": "measure.docx", "objectIds": object_ids, "mode": "separate"})
measured(f"{count} records, merged .docx, as the member through the Inbox",
         {"template": "measure.docx", "objectIds": object_ids, "mode": "merged", "name": "All"})
measured("25 records, separate PDF (zip), as the member through the Inbox",
         {"template": "measure.docx", "objectIds": object_ids[:25], "mode": "separate", "format": "pdf"})
measured(f"{count} records, merged PDF, as the member through the Inbox",
         {"template": "measure.docx", "objectIds": object_ids, "mode": "merged", "name": "All", "format": "pdf"})

# ---- clean up what this made
for suffix in ("_inbox", "_submit", ""):
    s.delete(f"{root}/admin/featureservices/{form_id}{suffix}?folder=hosted&drop=true", headers={"Authorization": f"Bearer {admin}"})
s.delete(f"{root}/content/documents/{item}", headers={"Authorization": f"Bearer {admin}"})
s.delete(f"{root}/admin/members/{member}?deleteOwned=true", headers={"Authorization": f"Bearer {admin}"})
print("cleaned up")
