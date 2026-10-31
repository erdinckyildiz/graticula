"""ADR-240 condition 1: editor tracking on insert, ArcGIS Pro 3.7 (file geodatabase) against grapy (GeoPackage).

Run under each side's Python:
    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr240_editor_tracking.py pro  <folder>
    D:/temp/grapy-env/Scripts/python.exe adr240_editor_tracking.py grapy <folder> <grapy src>
Prints one JSON document of observations; compare the two by eye or with diff. A tool, not a test: it needs Pro.
"""
import datetime
import getpass
import json
import os
import shutil
import sys

side, folder = sys.argv[1], sys.argv[2]
if side == "grapy":
    sys.path.insert(0, sys.argv[3])
    import grapy as arcpy
else:
    import arcpy

os.makedirs(folder, exist_ok=True)
if side == "pro":
    ws = os.path.join(folder, "et_pro.gdb")
    if os.path.exists(ws):
        shutil.rmtree(ws)
    arcpy.management.CreateFileGDB(folder, "et_pro.gdb")
else:
    ws = os.path.join(folder, "et_grapy.gpkg")
    if os.path.exists(ws):
        os.remove(ws)
    arcpy.management.CreateSQLiteDatabase(ws, "GEOPACKAGE")
arcpy.env.overwriteOutput = True
sr = arcpy.SpatialReference(4326)
out = {"side": side, "login": getpass.getuser()}


def make(name):
    fc = arcpy.management.CreateFeatureclass(ws, name, "POINT", spatial_reference=sr)[0]
    arcpy.management.AddField(fc, "label", "TEXT")
    return fc


def dump(fc, fields):
    rows = []
    with arcpy.da.SearchCursor(fc, fields) as cur:
        for r in cur:
            rows.append([v.isoformat() if isinstance(v, datetime.datetime) else v for v in r])
    return rows


def ftypes(fc, names):
    got = {f.name.lower(): (f.type, f.length) for f in arcpy.ListFields(fc)}
    return {n: got.get(n.lower()) for n in names}


# 1. Default names, ADD_FIELDS, UTC: does an insert fill all four?
fc = make("t_default")
with arcpy.da.InsertCursor(fc, ["SHAPE@XY", "label"]) as cur:
    cur.insertRow([(1, 1), "before"])
before = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None).replace(microsecond=0)
arcpy.management.EnableEditorTracking(fc, "created_user", "created_date", "last_edited_user", "last_edited_date",
                                      "ADD_FIELDS", "UTC")
F = ["label", "created_user", "created_date", "last_edited_user", "last_edited_date"]
out["fields_added"] = ftypes(fc, F[1:])
with arcpy.da.InsertCursor(fc, ["SHAPE@XY", "label"]) as cur:
    cur.insertRow([(2, 2), "inserted"])
after = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None).replace(microsecond=0)
out["insert_default"] = dump(fc, F)
out["utc_window"] = [before.isoformat(), after.isoformat()]
d = arcpy.Describe(fc)
out["describe"] = {k: getattr(d, k, None) for k in ("editorTrackingEnabled", "creatorFieldName", "createdAtFieldName",
                                                     "editorFieldName", "editedAtFieldName", "isTimeInUTC")}

# 2. A value the edit gives a tracked field: kept or replaced?
with arcpy.da.InsertCursor(fc, ["SHAPE@XY", "label", "created_user", "last_edited_user", "created_date"]) as cur:
    cur.insertRow([(3, 3), "given", "someone", "someone", datetime.datetime(2000, 1, 1)])
out["insert_given_values"] = dump(fc, F)[-1]

# 3. Update: which fields move? (the pre-tracking row and the inserted row)
with arcpy.da.UpdateCursor(fc, ["label"]) as cur:
    for r in cur:
        if r[0] in ("before", "inserted"):
            cur.updateRow([r[0] + "+u"])
out["after_update"] = dump(fc, F)

# 3b. Update that sets a tracked field itself
with arcpy.da.UpdateCursor(fc, ["label", "last_edited_user"]) as cur:
    for r in cur:
        if r[0] == "given":
            cur.updateRow(["given+u", "someone_else"])
out["update_given_editor"] = [r for r in dump(fc, F) if r[0] == "given+u"]

# 4. DATABASE_TIME: local or UTC?
fc2 = make("t_local")
arcpy.management.EnableEditorTracking(fc2, "cu", "cd", "eu", "ed", "ADD_FIELDS", "DATABASE_TIME")
lb = datetime.datetime.now().replace(microsecond=0)
with arcpy.da.InsertCursor(fc2, ["SHAPE@XY", "label"]) as cur:
    cur.insertRow([(1, 1), "x"])
la = datetime.datetime.now().replace(microsecond=0)
out["database_time"] = dump(fc2, ["label", "cu", "cd", "eu", "ed"])
out["local_window"] = [lb.isoformat(), la.isoformat()]
out["describe_local_isTimeInUTC"] = arcpy.Describe(fc2).isTimeInUTC

# 5. Only an editor pair named (creator fields empty)
fc3 = make("t_editor_only")
arcpy.management.EnableEditorTracking(fc3, last_editor_field="eu", last_edit_date_field="ed", add_fields="ADD_FIELDS")
with arcpy.da.InsertCursor(fc3, ["SHAPE@XY", "label"]) as cur:
    cur.insertRow([(1, 1), "x"])
out["editor_only_insert"] = dump(fc3, ["label", "eu", "ed"])
out["editor_only_fields"] = [f.name for f in arcpy.ListFields(fc3)]

# 6. Disable: fields and values kept? later edits stamped?
arcpy.management.DisableEditorTracking(fc)
out["after_disable_fields"] = [f.name for f in arcpy.ListFields(fc)]
with arcpy.da.InsertCursor(fc, ["SHAPE@XY", "label"]) as cur:
    cur.insertRow([(4, 4), "after_disable"])
out["after_disable_rows"] = dump(fc, F)
out["describe_after_disable"] = arcpy.Describe(fc).editorTrackingEnabled

print(json.dumps(out, indent=1, default=str))
