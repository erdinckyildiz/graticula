"""Walks a whole layer in 5,000-row pages and checks that the pages tile it exactly.

The owner's question: can a client page like ArcGIS does — 5,000, then start at 5,001,
and so on. The parameters exist (`resultOffset`, `resultRecordCount`), the document
advertises `supportsPagination`, and D-21 records a defect where they were silently
wrong. So this asks the only question that matters about paging: **does the union of
the pages equal the layer, with nothing repeated and nothing missed?**

Nothing here is written; every request is a read.
"""

import json
import ssl
import sys
import time
import urllib.parse
import urllib.request
import os

# <b>Read from the environment, never written here.</b> These scripts sign in to a
# development server, and a password in a file is a password in the repository's
# history the moment the file is committed -- where removing it later removes it
# from the tip and from nowhere else. Set GRATICULA_DEV_PASSWORD before running.
DEV_PASSWORD = os.environ.get("GRATICULA_DEV_PASSWORD", "")

BASE = "https://127.0.0.1:8443"
SERVICE = sys.argv[1] if len(sys.argv) > 1 else "hosted/tr_yol"
PAGE = int(sys.argv[2]) if len(sys.argv) > 2 else 5000


def context():
    c = ssl.create_default_context()
    c.check_hostname = False
    c.verify_mode = ssl.CERT_NONE
    return c


def token(ctx):
    request = urllib.request.Request(
        f"{BASE}/rest/auth/login",
        data=json.dumps({"name": "root", "password": DEV_PASSWORD}).encode(),
        headers={"Content-Type": "application/json"})

    with urllib.request.urlopen(request, context=ctx, timeout=30) as answer:
        return json.load(answer)["token"]


def get(ctx, bearer, url):
    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {bearer}"})

    with urllib.request.urlopen(request, context=ctx, timeout=180) as answer:
        return json.load(answer)


def main():
    ctx = context()
    bearer = token(ctx)
    layer = f"{BASE}/rest/services/{SERVICE}/FeatureServer/0"

    document = get(ctx, bearer, f"{layer}?f=json")
    advertised = document.get("maxRecordCount")

    total = get(ctx, bearer, f"{layer}/query?where=1%3D1&returnCountOnly=true&f=json")["count"]

    print(f"{SERVICE}: {total} rows, the document advertises maxRecordCount={advertised}")
    print(f"walking it in pages of {PAGE}\n")

    seen = set()
    duplicates = 0
    pages = 0
    offset = 0
    started = time.monotonic()

    while True:
        query = urllib.parse.urlencode({
            "where": "1=1",
            "outFields": "objectid",
            "returnGeometry": "false",
            "resultRecordCount": PAGE,
            "resultOffset": offset,
            "f": "json",
        })

        answer = get(ctx, bearer, f"{layer}/query?{query}")
        features = answer.get("features", [])
        pages += 1

        ids = [f["attributes"]["objectid"] for f in features]

        for one in ids:
            if one in seen:
                duplicates += 1
            seen.add(one)

        print(f"  page {pages:>3}  offset {offset:>6}  {len(ids):>5} rows  "
              f"first {ids[0] if ids else '-':>6}  last {ids[-1] if ids else '-':>6}  "
              f"exceeded={answer.get('exceededTransferLimit')}")

        if len(features) == 0:
            break

        offset += len(features)

        if pages > 1000:
            print("  stopping: more than 1000 pages, which is not what this layer should need")
            break

    elapsed = time.monotonic() - started

    print()
    print(f"pages: {pages}, distinct object ids collected: {len(seen)}, of {total} rows")
    print(f"duplicates across pages: {duplicates}")
    print(f"missing: {total - len(seen)}")
    print(f"{elapsed:.1f} s")

    ok = duplicates == 0 and len(seen) == total
    print("\nthe pages tile the layer exactly." if ok else "\nTHE PAGES DO NOT TILE THE LAYER.")


if __name__ == "__main__":
    main()
