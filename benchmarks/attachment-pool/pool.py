"""ADR-013 condition 2: nine slow readers against the attachment pool, and the feature path beside them.

Usage: python pool.py <base-url> <user> <password> [readers] [rate_bytes_per_s] [hold_s] [size_mb]

Defines a point layer, adds one feature with one attachment of <size_mb>, opens <readers> concurrent
downloads that read at <rate> through a 16 KB receive buffer and give up after <hold_s>, and meanwhile
asks a feature query every half second. Prints, per reader, the time to its first byte and how it ended,
and the feature queries' latencies. Drops the layer at the end.
"""
import json
import os
import socket
import ssl
import sys
import threading
import time
import urllib.parse
import urllib.request
import uuid

BASE, USER, PASSWORD = sys.argv[1], sys.argv[2], sys.argv[3]
READERS = int(sys.argv[4]) if len(sys.argv) > 4 else 9
RATE = int(sys.argv[5]) if len(sys.argv) > 5 else 100_000
HOLD = float(sys.argv[6]) if len(sys.argv) > 6 else 40.0
SIZE_MB = int(sys.argv[7]) if len(sys.argv) > 7 else 24

CTX = ssl.create_default_context()
CTX.check_hostname = False
CTX.verify_mode = ssl.CERT_NONE
HOST = urllib.parse.urlparse(BASE).hostname
PORT = urllib.parse.urlparse(BASE).port or 443


def call(method, path, token=None, body=None, content_type="application/json"):
    data = body if isinstance(body, (bytes, type(None))) else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", content_type)
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, context=CTX, timeout=120) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


status, body = call("POST", "/rest/auth/login", body={"name": USER, "password": PASSWORD})
assert status == 200, body
TOKEN = json.loads(body)["token"]

layer = "zz_attpool_" + uuid.uuid4().hex[:8]
status, body = call("POST", "/admin/hosted/define", TOKEN, {
    "name": layer, "geometryType": "Point",
    "fields": [{"name": "label", "type": "text", "nullable": True}]})
assert status == 201, body
feature = json.loads(body)["services"]["feature"]
parts = feature.strip("/").split("/")

try:
    adds = json.dumps([{"geometry": {"x": 28.97, "y": 41.0, "spatialReference": {"wkid": 4326}},
                        "attributes": {"label": "one"}}])
    status, body = call("POST", feature + "/addFeatures", TOKEN,
                        urllib.parse.urlencode({"features": adds, "f": "json"}).encode(),
                        "application/x-www-form-urlencoded")
    oid = json.loads(body)["addResults"][0]["objectId"]

    boundary = uuid.uuid4().hex
    payload = os.urandom(SIZE_MB * 1024 * 1024)
    form = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"f\"\r\n\r\njson\r\n"
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"attachment\"; filename=\"big.bin\"\r\n"
            f"Content-Type: application/octet-stream\r\n\r\n").encode() + payload + f"\r\n--{boundary}--\r\n".encode()
    status, body = call("POST", f"{feature}/{oid}/addAttachment", TOKEN, form,
                        f"multipart/form-data; boundary={boundary}")
    att = json.loads(body)["addAttachmentResult"]["objectId"]
    path = f"{feature}/{oid}/attachments/{att}"
    print(f"layer {layer}, attachment {att}: {SIZE_MB} MB; {READERS} readers at {RATE // 1000} kB/s for {HOLD:.0f} s")

    results = [None] * READERS
    start = time.monotonic()
    stop = threading.Event()

    def reader(i):
        raw = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        raw.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 16 * 1024)
        raw.settimeout(60)
        raw.connect((HOST, PORT))
        s = CTX.wrap_socket(raw, server_hostname=HOST)
        s.sendall((f"GET {path} HTTP/1.1\r\nHost: {HOST}\r\nAuthorization: Bearer {TOKEN}\r\n"
                   f"Connection: close\r\n\r\n").encode())
        t0 = time.monotonic()
        first, got, head, closed = None, 0, b"", False
        try:
            while time.monotonic() - t0 < HOLD:
                chunk = s.recv(16 * 1024)
                if not chunk:
                    closed = True
                    break
                if first is None:
                    first = time.monotonic() - t0
                    head = chunk[:64]
                got += len(chunk)
                time.sleep(len(chunk) / RATE)
            ended = ("closed by the server" if closed else "held until the hold ran out")                 if got < SIZE_MB * 1024 * 1024 else "read to the end"
        except Exception as e:
            ended = f"error {e!r}"
        finally:
            s.close()
        results[i] = (t0 - start, first, got, head.split(b"\r\n")[0].decode(errors="replace"), ended)

    threads = []
    for i in range(READERS):
        t = threading.Thread(target=reader, args=(i,))
        t.start()
        threads.append(t)
        time.sleep(0.3)  # in order, so the last one is the one past the pool

    samples = []

    def features():
        while not stop.is_set():
            t = time.monotonic()
            st, _ = call("GET", feature + "/query?where=1%3D1&outFields=*&f=json", TOKEN)
            samples.append((t - start, (time.monotonic() - t) * 1000, st))
            time.sleep(0.5)

    q = threading.Thread(target=features)
    q.start()
    for t in threads:
        t.join()
    stop.set()
    q.join()

    for i, (began, first, got, line, ended) in enumerate(results):
        ttfb = f"{first * 1000:.0f} ms" if first is not None else "never"
        print(f"reader {i + 1}: began +{began:.1f}s, first byte {ttfb}, {got / 1e6:.1f} MB, {line!r}, {ended}")
    lat = sorted(s[1] for s in samples)
    bad = [s for s in samples if s[2] != 200]
    print(f"feature queries: {len(samples)}, median {lat[len(lat) // 2]:.0f} ms, max {lat[-1]:.0f} ms, non-200 {len(bad)}")
finally:
    call("DELETE", f"/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", TOKEN)
