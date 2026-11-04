"""ADR-265: the ArcGIS API for Python prints through this server's print service, found where the portal names it.

Signs in, reads `helperServices.printTask.url` from the portal, imports the print service as a toolbox, runs
`export_web_map_task` over one of this server's layers to an A4 Landscape PDF, downloads it and checks its first bytes,
and reads `get_layout_templates_info_task`. Prints what it found, never a response body. Needs `pip install arcgis`
(measured with 2.4.3):

    python python-print.py <server> <user> <password> <…/FeatureServer/0 url> <folder to save into>
"""

import os
import sys
import warnings

warnings.filterwarnings("ignore")

from arcgis.geoprocessing import import_toolbox
from arcgis.gis import GIS
from arcgis.features import FeatureLayer

server, user, password, layer_url, folder = sys.argv[1:6]

gis = GIS(server, user, password, verify_cert=False)
print("signed in as", gis.properties.user.username if gis.properties.get("user") else None)

task_url = gis.properties.helperServices.printTask.url
print("helperServices.printTask names the Export Web Map Task:", task_url.endswith("/Export%20Web%20Map%20Task"))

# `Item.create_thumbnail`, the API's own caller of the print service, fails in 2.4.3 before it sends anything
# (`property 'session' of 'Toolbox' object has no setter`), so the toolbox is imported the documented way instead.
tools = import_toolbox(os.path.dirname(task_url).replace("%20", " "), gis=gis)

extent = FeatureLayer(layer_url, gis).properties.extent
web_map = {
    "mapOptions": {"extent": dict(extent), "spatialReference": dict(extent.spatialReference)},
    "operationalLayers": [{"id": "probe", "title": "Probe layer", "url": layer_url}],
    "exportOptions": {"outputSize": [500, 400], "dpi": 96},
    "layoutOptions": {"titleText": "Printed by the ArcGIS API for Python"},
}

printed = tools.export_web_map_task(web_map_as_json=web_map, format="PDF", layout_template="A4 Landscape")
print("export_web_map_task answered a", type(printed).__name__, "with a url:", bool(getattr(printed, "url", None)))

path = printed.download(save_path=folder)
with open(path, "rb") as file:
    print("downloaded", os.path.getsize(path), "bytes, a PDF:", file.read(5) == b"%PDF-")

templates = tools.get_layout_templates_info_task()
print("get_layout_templates_info_task listed", len(templates), "layouts")
