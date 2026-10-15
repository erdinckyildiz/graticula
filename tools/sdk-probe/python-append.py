"""ADR-105 condition 2: the ArcGIS API for Python's FeatureLayer.append, run against this server, and the layer counted.

Signs in with a token from this server's own /rest/auth/login — the API's username-and-password path reads Portal's
sign-in page, which this server's is not (Q-170) — uploads a GeoJSON file through the service's uploads/upload,
appends it by its upload id, and counts the layer before and after. Needs `pip install arcgis` (measured with 2.4.3)
and a hosted layer to append to:

    python python-append.py <server> <user> <password> <…/FeatureServer url> <file.geojson>
"""
import json
import ssl
import sys
import urllib.request
import warnings

warnings.filterwarnings("ignore")

from arcgis.gis import GIS
from arcgis.features import FeatureLayer, FeatureLayerCollection

server, user, password, service, path = sys.argv[1:6]

answer = urllib.request.urlopen(urllib.request.Request(
    server + "/rest/auth/login", data=json.dumps({"name": user, "password": password}).encode(),
    headers={"Content-Type": "application/json"}), context=ssl._create_unverified_context())
gis = GIS(server, token=json.load(answer)["token"], verify_cert=False)
print("signed in as", gis.users.me.username if gis.users.me else None)

collection = FeatureLayerCollection(service, gis)
layer = FeatureLayer(service + "/0", gis)
print("supportsAppend", getattr(layer.properties, "supportsAppend", None),
      "formats", getattr(layer.properties, "supportedAppendFormats", None))

before = layer.query(where="1=1", return_count_only=True)
upload_id = collection.upload(path)
print("upload id", upload_id)

result = layer.append(upload_id=upload_id, upload_format="geojson", return_messages=True)
print("append", json.dumps(result, default=str)[:600])

after = layer.query(where="1=1", return_count_only=True)
print("count before", before, "after", after)
