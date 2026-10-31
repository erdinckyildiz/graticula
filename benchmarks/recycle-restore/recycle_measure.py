"""ADR-203 condition 3: delete and restore of every kind, timed, on a fixture inflated to the showcase's size.

python recycle_measure.py inflate   -- grow the fixture's catalogue to the showcase's counts (2026-10-31, read-only query)
python recycle_measure.py measure   -- delete and restore each kind once, printing seconds and whether it read back
Reads the conformance suite's variables: GRATICULA_TEST_URL, GRATICULA_TEST_USER, GRATICULA_TEST_PASSWORD and
GRATICULA_TEST_PG (the datastore's Npgsql connection string; the platform schema is `gisserver`), and PSQL for psql.
Run against a throwaway fixture only: `inflate` writes hundreds of thousands of log rows.
Prints status codes, counts and timings only. Disposable, as everything under /benchmarks is.
"""
import json, os, ssl, subprocess, sys, time, uuid, urllib.request, urllib.error

ROOT = os.environ["GRATICULA_TEST_URL"].rstrip("/")
WT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PSQL = os.environ.get("PSQL", "psql")
PG = dict(p.split("=", 1) for p in os.environ["GRATICULA_TEST_PG"].split(";") if "=" in p)
ctx = ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE

# The showcase on 2026-10-31 (read-only aggregate; totals only).
SHOWCASE = {"service": 29, "layer": 31, "item": 30, "request_log": 583950, "audit_event": 28467,
            "login_attempt": 7502, "session": 5830}

def sql(text):
    env = dict(os.environ, PGPASSWORD=PG.get("Password", ""))
    out = subprocess.run([PSQL, "-h", PG.get("Host", "localhost"), "-p", PG.get("Port", "5432"), "-U", PG["Username"],
                          "-d", PG["Database"], "-At", "-v", "ON_ERROR_STOP=1",
                          "-c", "set search_path=gisserver", "-c", text], capture_output=True, text=True, env=env)
    if out.returncode != 0:
        raise RuntimeError(out.stderr)
    return [l for l in out.stdout.splitlines() if l and l != "SET"]

def call(method, path, body=None, token=None, multipart=None):
    headers = {}
    data = None
    if token: headers["Authorization"] = "Bearer " + token
    if body is not None:
        data = json.dumps(body).encode(); headers["Content-Type"] = "application/json"
    if multipart is not None:
        boundary = "----b" + uuid.uuid4().hex
        parts = []
        for name, value in multipart:
            if isinstance(value, tuple):
                fname, content = value
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{fname}"\r\n'
                             f'Content-Type: application/octet-stream\r\n\r\n'.encode() + content + b"\r\n")
            else:
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
        data = b"".join(parts) + f"--{boundary}--\r\n".encode()
        headers["Content-Type"] = "multipart/form-data; boundary=" + boundary
    req = urllib.request.Request(ROOT + path, data=data, method=method, headers=headers)
    t = time.perf_counter()
    try:
        r = urllib.request.urlopen(req, context=ctx, timeout=600)
        status, raw = r.status, r.read()
    except urllib.error.HTTPError as e:
        status, raw = e.code, e.read()
    return status, raw, time.perf_counter() - t

def js(raw):
    try: return json.loads(raw)
    except Exception: return {}

def login():
    st, raw, _ = call("POST", "/rest/auth/login", {"name": os.environ["GRATICULA_TEST_USER"], "password": os.environ["GRATICULA_TEST_PASSWORD"]})
    assert st == 200, st
    return js(raw)["token"]

def counts():
    rows = sql("select 'service',count(*) from service union all select 'layer',count(*) from layer union all "
               "select 'item',count(*) from item union all select 'request_log',count(*) from request_log union all "
               "select 'audit_event',count(*) from audit_event union all select 'login_attempt',count(*) from login_attempt "
               "union all select 'session',count(*) from session union all select 'service_usage',count(*) from service_usage")
    return {r.split("|")[0]: int(r.split("|")[1]) for r in rows}

def datastore_id(token):
    st, raw, _ = call("GET", "/admin/datasources", token=token)
    for d in js(raw).get("dataSources", js(raw).get("datasources", [])):
        if d.get("name") == "datastore":
            return d["id"]
    raise RuntimeError("no datastore source")

def publish_registered(token, source, name):
    return call("POST", "/admin/layers", {
        "name": name, "dataSourceId": source, "schemaName": "cifree", "tableName": "zz_free_three",
        "geometryColumn": "shape", "identityColumn": "id", "objectIdColumn": "id", "srid": 3857,
        "geometryType": "Point", "sharing": "private", "serviceName": name}, token)

def inflate():
    token = login()
    source = datastore_id(token)
    c = counts()
    need = SHOWCASE["service"] - c["service"]
    for i in range(max(0, need)):
        st, raw, _ = publish_registered(token, source, f"zz_size_{i:02d}")
        if st not in (200, 201): print("publish", st, raw[:200]); break
    c = counts()
    pr = sql("select id from principal order by created_at limit 1")[0]
    if c["request_log"] < SHOWCASE["request_log"]:
        sql(f"""insert into request_log (occurred_at, method, path, query, status, duration_ms, face, service, bytes)
                select now() - (g || ' seconds')::interval, 'GET', '/rest/services/hosted/s' || (g % 29) || '/FeatureServer/0/query',
                       'where=1%3D1&f=json', 200, g % 300, 'arcgis', 'hosted/s' || (g % 29), 1000 + g % 5000
                  from generate_series(1, {SHOWCASE['request_log'] - c['request_log']}) g""")
    if c["audit_event"] < SHOWCASE["audit_event"]:
        sql(f"""insert into audit_event (id, occurred_at, principal_id, principal_name, action, resource, detail, succeeded)
                select gen_random_uuid(), now() - (g || ' minutes')::interval, '{pr}', 'ci', 'service.read', 'hosted/s' || (g % 29),
                       jsonb_build_object('n', g), true from generate_series(1, {SHOWCASE['audit_event'] - c['audit_event']}) g""")
    if c["login_attempt"] < SHOWCASE["login_attempt"]:
        sql(f"""insert into login_attempt (id, attempted_name, attempted_at, succeeded)
                select gen_random_uuid(), 'ci', now() - (g || ' minutes')::interval, g % 7 <> 0
                  from generate_series(1, {SHOWCASE['login_attempt'] - c['login_attempt']}) g""")
    if c["session"] < SHOWCASE["session"]:
        sql(f"""insert into session (id, principal_id, token_hash, created_at, expires_at, revoked_at)
                select gen_random_uuid(), '{pr}', decode(md5(g::text), 'hex'), now() - interval '40 days', now() - interval '39 days', now() - interval '39 days'
                  from generate_series(1, {SHOWCASE['session'] - c['session']}) g""")
    # Every service has three years of daily usage, as a showcase that ran that long would.
    sql("""insert into service_usage (service_id, day, requests)
           select s.id, d::date, 10 from service s, generate_series(current_date - 1094, current_date, interval '1 day') d
           on conflict do nothing""")
    sql("analyze")
    print(json.dumps(counts()))

def usage(service_name, years):
    if years:
        sql(f"""insert into service_usage (service_id, day, requests)
                select s.id, d::date, 7 from service s, generate_series(current_date - {365 * years - 1}, current_date, interval '1 day') d
                 where s.name = '{service_name}' on conflict do nothing""")
    return int(sql(f"select count(*) from service_usage u join service s on s.id = u.service_id where s.name = '{service_name}'")[0])

def cycle(label, delete, restore_check, token):
    st, raw, t_del = delete()
    entry = js(raw).get("recycled")
    assert st == 200 and entry, (label, st, raw[:300])
    kept = int(sql(f"select count(*) from recycle_row where entry_id = '{entry}'")[0])
    st, raw, t_res = call("POST", f"/admin/recycle-bin/{entry}/restore", token=token)
    assert st == 200, (label, st, raw[:300])
    ok = restore_check()
    print(f"{label}: delete {t_del:.3f} s, restore {t_res:.3f} s, rows kept {kept}, read back {'ok' if ok else 'FAILED'}")
    return t_del, t_res, ok

def measure():
    token = login()
    source = datastore_id(token)
    print(json.dumps(counts()))
    tag = uuid.uuid4().hex[:6]
    results = []

    # A hosted service, from the corpus shapefile, with 0, 5 and 20 years of usage rows.
    shp = open(os.path.join(WT, "tests", "Graticula.Core.Tests", "corpus", "shapefile", "points.zip"), "rb").read()
    hosted = f"zz_rb_h_{tag}"
    st, raw, t = call("POST", "/admin/hosted/import", token=token, multipart=[("file", ("points.zip", shp)), ("name", hosted)])
    assert st in (200, 201, 202), (st, raw[:300])
    item = js(call("GET", f"/rest/services/hosted/{hosted}/FeatureServer?f=json", token=token)[1]).get("serviceItemId")
    def hosted_back():
        doc = js(call("GET", f"/rest/services/hosted/{hosted}/FeatureServer?f=json", token=token)[1])
        n = js(call("GET", f"/rest/services/hosted/{hosted}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json", token=token)[1]).get("count", 0)
        return doc.get("serviceItemId") == item and n > 0
    for years in (0, 5, 20):
        n = usage(hosted, years)
        results.append(cycle(f"hosted service, {n} usage rows", lambda: call("DELETE", f"/admin/featureservices/{hosted}?folder=hosted&drop=true", token=token), hosted_back, token))
    print(f"  usage rows after the last restore: {usage(hosted, 0)}")

    # A registered service, three years of usage rows.
    reg = f"zz_rb_r_{tag}"
    st, raw, _ = publish_registered(token, source, reg)
    assert st in (200, 201), (st, raw[:300])
    url = js(raw)["url"]
    regpath = url[url.index("/rest/services/") + len("/rest/services/"):url.index("/FeatureServer")]
    regfolder = regpath.rsplit("/", 1)[0] if "/" in regpath else ""
    reg_item = js(call("GET", f"/rest/services/{regpath}/FeatureServer?f=json", token=token)[1]).get("serviceItemId")
    n = usage(reg, 3)
    def reg_back():
        return js(call("GET", f"/rest/services/{regpath}/FeatureServer?f=json", token=token)[1]).get("serviceItemId") == reg_item
    results.append(cycle(f"registered service, {n} usage rows", lambda: call("DELETE", f"/admin/featureservices/{reg}?folder={regfolder}&drop=true", token=token), reg_back, token))

    # An image service with its uploaded file.
    tif = open(os.path.join(WT, "tests", "Graticula.Raster.Tiff.Tests", "corpus", "rgb-byte-3857.tif"), "rb").read()
    img = f"zz_rb_i_{tag}"
    st, raw, _ = call("POST", "/admin/coverages/upload", token=token, multipart=[("file", ("rgb.tif", tif)), ("name", img)])
    assert st in (200, 201, 202), (st, raw[:300])
    def img_back():
        st, raw, _ = call("GET", f"/rest/services/hosted/{img}/ImageServer/exportImage?bbox=-20037508,-20037508,20037508,20037508&size=64,64&format=png&f=json", token=token)
        return st == 200 and "error" not in js(raw)
    results.append(cycle("image service", lambda: call("DELETE", f"/admin/coverages/{img}?folder=hosted", token=token), img_back, token))

    # A web map.
    st, raw, _ = call("POST", "/content/webmaps", {"title": f"zz rb map {tag}", "sharing": "private", "document": {
        "operationalLayers": [], "baseMap": {"baseMapLayers": [], "title": "None"}, "version": "2.31"}}, token)
    assert st == 201, (st, raw[:300])
    wm = js(raw)["id"]
    results.append(cycle("web map", lambda: call("DELETE", f"/content/webmaps/{wm}", token=token),
                         lambda: call("GET", f"/content/webmaps/{wm}", token=token)[0] == 200, token))

    # A document, with its bytes.
    pdf = b"%PDF-1.7\n% a recycle measurement\n%%EOF\n"
    st, raw, _ = call("POST", "/content/documents", token=token, multipart=[("file", ("plan.pdf", pdf)), ("title", f"zz rb doc {tag}")])
    assert st == 201, (st, raw[:300])
    doc = js(raw)["id"]
    results.append(cycle("document", lambda: call("DELETE", f"/content/documents/{doc}", token=token),
                         lambda: call("GET", f"/content/documents/{doc}/data", token=token)[1] == pdf, token))

    # Leave nothing behind: delete each for good.
    for path in (f"/admin/featureservices/{hosted}?folder=hosted&drop=true", f"/admin/featureservices/{reg}?folder={regfolder}&drop=true",
                 f"/admin/coverages/{img}?folder=hosted", f"/content/webmaps/{wm}", f"/content/documents/{doc}"):
        st, raw, _ = call("DELETE", path, token=token)
        e = js(raw).get("recycled")
        if e: call("DELETE", f"/admin/recycle-bin/{e}", token=token)
    print("all read back" if all(r[2] for r in results) else "SOME FAILED")

if __name__ == "__main__":
    {"inflate": inflate, "measure": measure}[sys.argv[1]]()
