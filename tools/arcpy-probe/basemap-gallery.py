"""ADR-184 condition 2: ArcGIS Pro's basemap gallery lists this portal's gallery.

Signs arcpy in to a portal, makes a scratch project, and prints what `ArcGISProject.listBasemaps()` returns — the
names Pro offers in its Basemap gallery — then adds one of them to a map and prints the map's basemap layers. The
portal's own default and the gallery come from `portals/self`'s `basemapGalleryGroupQuery`, so the list is the
server's answer as Pro reads it.

    python basemap-gallery.py https://demo.graticula.com <credentials.json> <scratch folder>

The credentials file is `{"user": ..., "password": ...}` and is never printed. Run with ArcGIS Pro's own Python.
"""
import json
import os
import sys
import time

import arcpy

portal, credentials, scratch = sys.argv[1], sys.argv[2], sys.argv[3]
c = json.load(open(credentials, encoding="utf-8"))

before = arcpy.GetActivePortalURL()
arcpy.SignInToPortal(portal, c["user"], c["password"])
print("active portal:", arcpy.GetActivePortalURL())

try:
    os.makedirs(scratch, exist_ok=True)
    aprx = arcpy.mp.CreateArcGISProject(scratch, "gallery_" + time.strftime("%H%M%S"))
    names = aprx.listBasemaps()
    print("basemaps Pro offers:", names)

    wanted = sys.argv[4] if len(sys.argv) > 4 else None
    if wanted and wanted in names:
        m = aprx.createMap("Gallery probe", "MAP")
        m.addBasemap(wanted)
        print("added", wanted, "->", [layer.name for layer in m.listLayers()])
finally:
    # Leave Pro on the portal it was on. arcpy has no SetActivePortal: signing in to it again is the way back, and
    # this works when the same credential reaches both, as the showcase and dev.graticula.com share a database.
    if before and before.rstrip("/") != portal.rstrip("/"):
        try:
            arcpy.SignInToPortal(before, c["user"], c["password"])
        except Exception as e:  # noqa: BLE001 - say it, and leave Pro where it is
            print("could not sign back in to", before, "-", type(e).__name__)
        print("active portal now:", arcpy.GetActivePortalURL())
