"""ADR-257 §11: the ArcGIS API for Python's create_service and create_view, run as they are against this server.

create_service makes an empty hosted feature service; FeatureLayerCollection.manager.add_to_definition then adds a
layer; edit_features adds two points and a query counts them. create_view on that service makes an empty view and
fills it with add_to_definition inside the client; the view is queried. Everything made is deleted at the end.
Needs `pip install arcgis` (measured with 2.4.3). Signs in with a token from this server's /rest/auth/login, as
python-append.py does:

    python python-create-service.py <server> <user> <password>
"""
import json
import ssl
import sys
import urllib.request
import uuid
import warnings

warnings.filterwarnings("ignore")

from arcgis.gis import GIS
from arcgis.features import FeatureLayerCollection

server, user, password = sys.argv[1:4]

answer = urllib.request.urlopen(urllib.request.Request(
    server + "/rest/auth/login", data=json.dumps({"name": user, "password": password}).encode(),
    headers={"Content-Type": "application/json"}), context=ssl._create_unverified_context())
gis = GIS(server, token=json.load(answer)["token"], verify_cert=False)
print("signed in as", gis.users.me.username if gis.users.me else None)

tag = uuid.uuid4().hex[:6]
name = f"zz_pyempty_{tag}"
view_name = f"zz_pyview_{tag}"
made = []

try:
    item = gis.content.create_service(
        name, service_type="featureService",
        create_params={"name": name, "serviceDescription": "made empty by the Python API", "capabilities": "Query,Editing",
                       "spatialReference": {"wkid": 102100}, "hasStaticData": False, "maxRecordCount": 1000})
    made.append(item)
    print("create_service:", item.type, item.id, item.url)

    flc = FeatureLayerCollection.fromitem(item)
    print("empty: layers", len(flc.layers), "tables", len(flc.tables))

    added = flc.manager.add_to_definition({"layers": [{
        "name": f"zz_pypoints_{tag}", "type": "Feature Layer", "geometryType": "esriGeometryPoint",
        "objectIdField": "OBJECTID", "hasAttachments": False,
        "extent": {"xmin": 25, "ymin": 35, "xmax": 45, "ymax": 43, "spatialReference": {"wkid": 4326}},
        "fields": [
            {"name": "OBJECTID", "type": "esriFieldTypeOID", "alias": "OBJECTID", "nullable": False, "editable": False},
            {"name": "label", "type": "esriFieldTypeString", "alias": "Label", "length": 64, "nullable": True,
             "editable": True, "domain": None, "defaultValue": None},
        ],
    }]})
    print("add_to_definition:", json.dumps(added))

    flc = FeatureLayerCollection.fromitem(item)
    layer = flc.layers[0]
    print("layer:", layer.properties.name, layer.properties.geometryType,
          [f["name"] for f in layer.properties.fields])

    edits = layer.edit_features(adds=[
        {"attributes": {"label": "Ankara"}, "geometry": {"x": 32.85, "y": 39.93, "spatialReference": {"wkid": 4326}}},
        {"attributes": {"label": "Izmir"}, "geometry": {"x": 27.14, "y": 38.42, "spatialReference": {"wkid": 4326}}},
    ])
    print("edit_features:", [r.get("success") for r in edits["addResults"]])
    print("query count:", layer.query(where="1=1", return_count_only=True))
    print("query labels:", sorted(f.attributes["label"] for f in layer.query(where="1=1", out_fields="label").features))

    view = flc.manager.create_view(name=view_name)
    made.insert(0, view)
    print("create_view:", view.type, view.id, view.url)

    vflc = FeatureLayerCollection.fromitem(view)
    print("view: layers", len(vflc.layers), "isView", vflc.properties.get("isView"))
    print("view query count:", vflc.layers[0].query(where="1=1", return_count_only=True))
finally:
    # A create_view that stopped half way leaves its empty view behind, which the list above never held.
    if not any(thing.title == view_name for thing in made):
        made[:0] = [found for found in gis.content.search(f"title:{view_name}") if found.title == view_name]
    for thing in made:
        try:
            print("delete", thing.title, thing.delete())
        except Exception as e:  # noqa: BLE001 — the probe reports what it could not take back
            print("delete", thing.title, "failed:", e)
