"""Drives a headless Chromium browser over CDP: opens a page, waits for window.__result, saves a screenshot.

Used for the ArcGIS Maps SDK checks of ADR-086 condition 2, ADR-104 condition 3 and ADR-110 condition 2 (2026-10-14),
and ADR-184 condition 1 (basemap-gallery.html, 2026-10-15):
the pages beside this file load the SDK from js.arcgis.com, point it at a server, and write what they found to
window.__result. Serve this folder over HTTP on another origin than the server (python -m http.server 8763), then

    python drive.py "http://127.0.0.1:8763/ground.html?portal=https://<server>" ground.png
    python drive.py "http://127.0.0.1:8763/webmap.html?portal=https://<server>&id=<web map item id>" webmap.png

BROWSER names the executable (Brave or Chrome; Edge loads enterprise extensions headless and is not used).
Needs `pip install websocket-client`. It prints the page's result, the server requests that failed and how many tile
requests were answered.
"""
import base64
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.request

import websocket

url, shot = sys.argv[1], sys.argv[2]
port = 9351
profile = tempfile.mkdtemp(prefix="sdk-probe-")
brave = subprocess.Popen([
    os.environ.get("BROWSER", r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"),
    "--headless=new", "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--disable-extensions",
    "--no-first-run", "--ignore-certificate-errors", "--window-size=1280,800",
    f"--remote-debugging-port={port}", f"--user-data-dir={profile}", "about:blank"])
try:
    for _ in range(50):
        try:
            pages = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/json"))
            page = next(p for p in pages if p.get("type") == "page")
            break
        except Exception:
            time.sleep(0.3)
    ws = websocket.create_connection(page["webSocketDebuggerUrl"], suppress_origin=True)
    ids = iter(range(1, 10**6))
    events = []

    def call(method, **params):
        i = next(ids)
        ws.send(json.dumps({"id": i, "method": method, "params": params}))
        while True:
            m = json.loads(ws.recv())
            if m.get("id") == i:
                return m.get("result", {})
            if m.get("method") in ("Network.loadingFailed", "Network.requestWillBeSent", "Network.responseReceived"):
                events.append(m)

    call("Page.enable")
    call("Network.enable")
    call("Page.navigate", url=url)
    result = None
    for _ in range(120):
        time.sleep(0.5)
        r = call("Runtime.evaluate", expression="window.__result || ''", returnByValue=True)
        value = r.get("result", {}).get("value")
        if value:
            result = value
            break
    png = call("Page.captureScreenshot", format="png")
    with open(shot, "wb") as f:
        f.write(base64.b64decode(png["data"]))
    print(result or "no result in 60 s")
    reqs = {}
    tiles = []
    for e in events:
        p = e["params"]
        if e["method"] == "Network.requestWillBeSent" and any(
                part in p["request"]["url"] for part in ("/rest/services", "/sharing/rest")):
            reqs[p["requestId"]] = p["request"]["url"][:220]
        if e["method"] == "Network.loadingFailed" and p["requestId"] in reqs:
            print("FAILED", reqs[p["requestId"]], p.get("errorText"), p.get("corsErrorStatus"), p.get("blockedReason"))
        if e["method"] == "Network.responseReceived" and p["requestId"] in reqs:
            u = reqs[p["requestId"]]
            if "/tile/" in u:
                tiles.append((p["response"]["status"], u))
            else:
                print("OK", p["response"]["status"], u)
    print("tile responses:", len(tiles), sorted({s for s, _ in tiles}))
finally:
    brave.kill()
