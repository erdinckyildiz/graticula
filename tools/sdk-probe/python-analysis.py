"""ADR-222 condition 6: the ArcGIS API for Python's analysis functions, run against this server, and their results counted.

Signs in with the user name and password (Q-170), lets the API find the analysis service through portals/self's
helperServices.analysis as it does on ArcGIS Enterprise, runs create_buffers, dissolve_boundaries, overlay_layers,
merge_layers and aggregate_points with their own defaults, counts each result layer, and removes what it made —
each service with its table, through the recycle bin. Needs `pip install arcgis setuptools` (measured with 2.4.3 on
Python 3.12, which needs setuptools for distutils) and three hosted layers:

    python python-analysis.py <server> <user> <password> <polygons service> <other polygons service> <points service>

such as `https://host hosted/ci_buildings hosted/ci_parcels hosted/ci_observations`.
"""
import json
import ssl
import sys
import time
import urllib.request
import warnings

warnings.filterwarnings("ignore")

from arcgis.gis import GIS
from arcgis.features import FeatureLayer
from arcgis.features.manage_data import dissolve_boundaries, merge_layers, overlay_layers
from arcgis.features.summarize_data import aggregate_points
from arcgis.features.use_proximity import create_buffers

server, user, password, first, second, points = sys.argv[1:7]
gis = GIS(server, user, password, verify_cert=False)
print("analysis service:", gis.properties.helperServices.get("analysis"))

layer = lambda service: FeatureLayer(f"{server}/rest/services/{service}/FeatureServer/0", gis=gis)
polygons, others, dots = layer(first), layer(second), layer(points)
stamp = time.strftime("%H%M%S")
made = []
failed = 0


def run(label, call):
    global failed
    try:
        item = call()
        made.append(item.url.rstrip("/").split("/")[-2])
        print(f"{label}: {item.layers[0].query(return_count_only=True)} features")
    except Exception as e:  # The probe reports every tool, whichever fails.
        failed += 1
        print(f"{label} FAILED: {type(e).__name__}: {str(e)[:400]}")


run("create_buffers", lambda: create_buffers(polygons, distances=[50], units="Meters", output_name=f"zz_pyb_{stamp}", gis=gis))
run("dissolve_boundaries", lambda: dissolve_boundaries(others, multi_part_features=True, output_name=f"zz_pyd_{stamp}", gis=gis))
run("overlay_layers", lambda: overlay_layers(polygons, others, overlay_type="Intersect", output_name=f"zz_pyo_{stamp}", gis=gis))
run("merge_layers", lambda: merge_layers(polygons, others, output_name=f"zz_pym_{stamp}", gis=gis))
run("aggregate_points", lambda: aggregate_points(dots, others, output_name=f"zz_pya_{stamp}", gis=gis))

context = ssl._create_unverified_context()
token = json.loads(urllib.request.urlopen(urllib.request.Request(
    server + "/rest/auth/login", data=json.dumps({"name": user, "password": password}).encode(), method="POST",
    headers={"Content-Type": "application/json"}), context=context).read())["token"]
headers = {"Authorization": "Bearer " + token}

for name in made:
    answer = json.loads(urllib.request.urlopen(urllib.request.Request(
        f"{server}/admin/featureservices/{name}?folder=hosted&drop=true", method="DELETE", headers=headers), context=context).read())
    if answer.get("recycled"):
        urllib.request.urlopen(urllib.request.Request(
            f"{server}/admin/recycle-bin/{answer['recycled']}", method="DELETE", headers=headers), context=context)

sys.exit(1 if failed else 0)
