"""ADR-057 condition 5, Pro's half: ArcGIS Pro adds a feature service that has a group layer, and builds the tree.

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" group-service.py <…/FeatureServer> <out folder>

A new project and map, the service added by its address as Add Data does, and the layer tree Pro made printed with
each layer's kind, whether it is broken and how many features it counts. Measured with Pro 3.7 on 2026-10-14, through
a plain-HTTP relay because Pro refuses a self-signed certificate.
"""
import os
import sys
import time

import arcpy

service, out = sys.argv[1:3]
aprx = arcpy.mp.CreateArcGISProject(out, "group_" + time.strftime("%H%M%S"))
added = aprx.createMap("Group probe", "MAP").addDataFromPath(service)


def walk(layer, depth):
    kind = "group" if layer.isGroupLayer else "feature" if layer.isFeatureLayer else "other"
    count = ""
    if layer.isFeatureLayer and not layer.isBroken:
        count = f" features {arcpy.management.GetCount(layer)[0]}"
    print(f"{'   ' * depth}{layer.name} [{kind}] broken={layer.isBroken}{count}")
    if layer.isGroupLayer:
        for child in layer.listLayers():
            if child.longName.count("\\") == layer.longName.count("\\") + 1:
                walk(child, depth + 1)


walk(added, 0)
