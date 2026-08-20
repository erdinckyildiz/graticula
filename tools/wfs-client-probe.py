#!/usr/bin/env python3
"""Reads this server's WFS with GDAL's own client, not ours.

ADR-039 condition 1, the GDAL half. Everything here is GDAL deciding: which
version to ask for, which output format to accept, how to phrase a filter, and
which way round the axes go. Our own conformance suite proves this server agrees
with our reading of the specification; this proves a client that was not written
here can read what it produces. They are different claims and only the second one
can find a misreading.

    python tools/wfs-client-probe.py [type-name] [base-url]

**It needs a Python with `osgeo`**, and on the development machine that is the one
inside ArcGIS Pro rather than anything on PATH:

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" \
        tools/wfs-client-probe.py hosted:tr_il

The `ogrinfo.exe` beside it does not run from a shell that has not set up that
environment's DLL directory -- it fails on `gdal_e.dll`. The bindings work, so the
probe uses those instead of the command line, which is the same driver either way.

**TLS is verified, not skipped.** The development certificate is self-signed and
the answer is to trust it once — `state/serving-certificate.cer`, imported into
*Trusted Root*, not *Intermediate*, which is where the import wizard's automatic
choice puts it and where it does nothing. Pass `--insecure` to fall back to
`GDAL_HTTP_UNSAFESSL`; it is a flag rather than the default because a probe that
turns verification off cannot tell you verification works, and that is one of the
things a client has to get past before anything else matters.
"""

import sys

from osgeo import gdal, ogr

gdal.UseExceptions()

args = [a for a in sys.argv[1:] if a != "--insecure"]

if "--insecure" in sys.argv:
    gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "YES")
    print("warning: TLS verification is off, so nothing below tests the certificate")

LAYER = args[0] if len(args) > 0 else "hosted:tr_il"
BASE = args[1] if len(args) > 1 else "https://127.0.0.1:8443"
URL = "WFS:" + BASE + "/wfs"

failures = []


def check(label, ok, detail=""):
    print(("  ok    " if ok else "  FAIL  ") + label + (" " + detail if detail else ""))
    if not ok:
        failures.append(label)


print("gdal", gdal.__version__, "against", BASE)

ds = ogr.Open(URL)
check("GDAL opens the service", ds is not None)

if ds is None:
    raise SystemExit(1)

names = [ds.GetLayerByIndex(i).GetName() for i in range(ds.GetLayerCount())]
check("GDAL lists the feature types", len(names) > 0, f"({len(names)} of them)")
check(f"{LAYER} is among them", LAYER in names)

if LAYER not in names:
    raise SystemExit(1)

layer = ds.GetLayerByName(LAYER)
defn = layer.GetLayerDefn()
fields = [defn.GetFieldDefn(i).GetName() for i in range(defn.GetFieldCount())]

check("DescribeFeatureType gives GDAL its fields", len(fields) > 0, "(" + ", ".join(fields) + ")")

srs = layer.GetSpatialRef()
srid = int(srs.GetAuthorityCode(None)) if srs is not None else 0
check("the coordinate reference resolves", srid > 0, f"(EPSG:{srid})")

matched = layer.GetFeatureCount()
check("resultType=hits answers", matched >= 0, f"({matched} features)")

layer.ResetReading()
first = layer.GetNextFeature()
check("a feature reads", first is not None)

geom = first.GetGeometryRef() if first is not None else None

# <b>The axis-order check, and it is the reason this probe exists.</b> If our GML
# wrote longitude first under a latitude-first reference, GDAL transposes it and
# the geometry lands in the Gulf of Guinea. Nothing errors anywhere; the numbers
# are simply wrong, which is the failure Q-96 already recorded once for tiles.
if geom is not None:
    centroid = geom.Centroid()
    x, y = centroid.GetX(), centroid.GetY()

    if srid == 4326:
        placed = 25 <= x <= 45 and 35 <= y <= 43
        where = "degrees"
    elif srid == 3857:
        placed = 2.7e6 <= x <= 5.1e6 and 4.1e6 <= y <= 5.4e6
        where = "web-mercator metres"
    else:
        placed = True
        where = f"EPSG:{srid}, not checked"

    check(f"the geometry lands where the data is ({where})", placed, f"x={x:.4f} y={y:.4f}")

# GDAL phrases both of these itself, from its own reading of our capabilities: an
# attribute filter becomes a fes:Filter and a spatial filter becomes a BBOX.
if fields:
    # Not gml_id: GDAL adds that itself from the feature's identifier, so filtering
    # on it narrows to one whatever the server does and proves nothing.
    text_field = next(
        (defn.GetFieldDefn(i).GetName()
         for i in range(defn.GetFieldCount())
         if defn.GetFieldDefn(i).GetType() == ogr.OFTString
         and defn.GetFieldDefn(i).GetName() != "gml_id"),
        None)

    if text_field and first is not None and first.GetField(text_field):
        value = first.GetField(text_field).replace("'", "''")
        layer.SetAttributeFilter(f"{text_field} = '{value}'")
        narrowed = layer.GetFeatureCount()
        layer.SetAttributeFilter(None)

        check(
            "GDAL's own attribute filter narrows the result",
            0 < narrowed <= matched,
            f"({narrowed} of {matched} on {text_field})")

minx, maxx, miny, maxy = layer.GetExtent()

layer.SetSpatialFilterRect(minx, miny, maxx, maxy)
whole = layer.GetFeatureCount()

layer.SetSpatialFilterRect(minx, miny, (minx + maxx) / 2, maxy)
half = layer.GetFeatureCount()
layer.SetSpatialFilter(None)

check("GDAL's own bbox over the whole extent finds everything", whole == matched,
      f"({whole} of {matched})")

check("and half the extent finds less", half < whole, f"({half} of {whole})")

# The other half of the condition: not just read, but converted -- which is what a
# migration actually does.
for label, fmt, extra in (
        ("GeoJSON", "GeoJSON", {}),
        ("GeoPackage", "GPKG", {}),
        ("Shapefile", "ESRI Shapefile", {}),
):
    target = f"/vsimem/probe-{fmt}"

    try:
        out = gdal.VectorTranslate(target, URL, format=fmt, layers=[LAYER], limit=25, **extra)

        # <b>Closed, then reopened, and the first version of this did not.</b>
        # Counting the open write handle reports zero for the GeoJSON driver
        # whatever it wrote — it had this probe reporting a failure against files
        # that turned out to hold 50 and 248 features. A measurement that can say
        # *broken* about something working is the instrument, not the subject.
        out = None

        reopened = ogr.Open(target)
        written = reopened.GetLayer(0).GetFeatureCount() if reopened is not None else 0
        reopened = None

        gdal.Unlink(target)
        check(f"ogr2ogr to {label}", written > 0, f"({written} features)")
    except Exception as error:  # noqa: BLE001 - the message is the finding
        check(f"ogr2ogr to {label}", False, str(error)[:120])

print()

if failures:
    print(f"{len(failures)} check(s) failed: " + "; ".join(failures))
    raise SystemExit(1)

print("GDAL reads this server's WFS")
