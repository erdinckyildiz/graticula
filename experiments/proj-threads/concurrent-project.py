"""Q-23, exercised: is a projection this server performs affected by concurrency?

Q-23 asks whether a PROJ transformation object is thread-affine, because that decides
whether prepared transformations are shared or duplicated per thread. In v1 nothing in
this process holds one: `PostGisProjector` is the only `IProjector`, every transform is
`ST_Transform` inside PostgreSQL, and each connection has its own PROJ context. So the
question has no subject — which is an argument, and this is the measurement beside it.

Twenty-four threads project the same points at once and every answer must equal the
serial answer to the last representable digit. A shared, thread-affine transformation
object misused across threads does not usually throw; it returns *slightly* wrong
numbers, which is why the assertion is exact equality rather than a tolerance.
"""

import concurrent.futures as futures
import json
import ssl
import urllib.parse
import urllib.request
import os

# <b>Read from the environment, never written here.</b> These scripts sign in to a
# development server, and a password in a file is a password in the repository's
# history the moment the file is committed -- where removing it later removes it
# from the tip and from nowhere else. Set GRATICULA_DEV_PASSWORD before running.
DEV_PASSWORD = os.environ.get("GRATICULA_DEV_PASSWORD", "")

BASE = "https://127.0.0.1:8443"
PROJECT = f"{BASE}/rest/services/Utilities/Geometry/GeometryServer/project"

# Istanbul, Ankara, Hamilton, and two points where a datum shift is largest.
POINTS = [(28.9784, 41.0082), (32.8597, 39.9334), (-79.8711, 43.2557),
          (0.0, 0.0), (-179.9, -89.9), (179.9, 89.9)]

FROM, TO = 4326, 3857


def context():
    ssl_context = ssl.create_default_context()
    ssl_context.check_hostname = False
    ssl_context.verify_mode = ssl.CERT_NONE
    return ssl_context


def token(ssl_context):
    request = urllib.request.Request(
        f"{BASE}/rest/auth/login",
        data=json.dumps({"name": "root", "password": DEV_PASSWORD}).encode(),
        headers={"Content-Type": "application/json"})

    with urllib.request.urlopen(request, context=ssl_context, timeout=30) as answer:
        return json.load(answer)["token"]


def project(bearer, ssl_context):
    body = urllib.parse.urlencode({
        "geometries": json.dumps({
            "geometryType": "esriGeometryPoint",
            "geometries": [{"x": x, "y": y} for x, y in POINTS],
        }),
        "inSR": FROM,
        "outSR": TO,
        "f": "json",
    }).encode()

    request = urllib.request.Request(
        PROJECT, data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded",
                 "Authorization": f"Bearer {bearer}"})

    with urllib.request.urlopen(request, context=ssl_context, timeout=60) as answer:
        got = json.load(answer)

    # Exact repr, so a difference in the last bit is a difference.
    return json.dumps(got.get("geometries", got), sort_keys=True)


def main():
    ssl_context = context()
    bearer = token(ssl_context)

    alone = project(bearer, ssl_context)
    print("serial answer:", alone[:150], "\n")

    rounds, threads = 40, 24

    with futures.ThreadPoolExecutor(max_workers=threads) as pool:
        answers = [f.result() for f in [
            pool.submit(project, bearer, ssl_context) for _ in range(rounds * threads)]]

    distinct = set(answers) | {alone}

    print(f"{len(answers)} concurrent projections over {threads} threads, "
          f"{len(distinct)} distinct answers.")

    if len(distinct) == 1:
        print("Every concurrent answer is identical to the serial one, digit for digit.")
    else:
        print("DIVERGED. The distinct answers:")
        for one in distinct:
            print("  ", one[:200])


if __name__ == "__main__":
    main()
