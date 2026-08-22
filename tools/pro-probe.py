"""Drive ArcGIS Pro's own client stack against this server's ImageServer, beside Esri's.

Run with Pro's Python:
    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" pro_probe.py

ADR-043 condition 1 asks for a real ArcGIS client. The Maps SDK for JavaScript paid part
of it; this is the other client, the one an administrator actually uses, driven through
the library Pro itself is built on.

**Every step runs against Esri's public Terrain3D as well, and that is the point.** The
first version of this script reported two failures and both were misread: CopyRaster
fails identically against Esri's own service, because an image service that says
`allowCopy: false` does not offer a download and there is nothing there to fix. Without
the control beside it, a step that no image service passes reads as a defect in this
one. A difference between the two columns is a finding; a matching failure is not.
"""
import os
import sys

# The development certificate is self-signed. Pro's stack refuses an untrusted chain,
# which is a correct thing for it to do and not what is under test here.
os.environ["REQUESTS_CA_BUNDLE"] = ""
os.environ["CURL_CA_BUNDLE"] = ""

import arcpy

arcpy.env.overwriteOutput = True

HERE = os.path.dirname(os.path.abspath(__file__))

OURS = sys.argv[1] if len(sys.argv) > 1 else (
    "https://127.0.0.1:8443/rest/services/hosted/pro_probe/ImageServer")

ESRI = ("https://elevation3d.arcgis.com/arcgis/rest/services"
        "/WorldElevation3D/Terrain3D/ImageServer")


def run(url, tag):
    """Every step against one service. Returns {step: 'value' or 'FAIL: reason'}."""
    answers = {}

    def step(name, fn):
        try:
            answers[name] = str(fn())
        except Exception as e:  # noqa: BLE001 - a probe records failures rather than raising
            first = str(e).strip().splitlines()[0] if str(e).strip() else repr(e)
            answers[name] = "FAIL: " + first[:90]

    layer = tag + "_layer"

    step("MakeImageServerLayer", lambda: arcpy.management.MakeImageServerLayer(
        url, layer).getOutput(0) and "made")

    described = None

    try:
        described = arcpy.Describe(layer)
    except Exception:  # noqa: BLE001
        pass

    if described is not None:
        step("Describe.bandCount", lambda: described.bandCount)
        step("Describe.spatialReference", lambda: described.spatialReference.factoryCode)
        step("Describe.extent", lambda: "{0:.0f} {1:.0f} {2:.0f} {3:.0f}".format(
            described.extent.XMin, described.extent.YMin,
            described.extent.XMax, described.extent.YMax))
        step("Describe.pixelType", lambda: described.pixelType)
    else:
        for name in ("Describe.bandCount", "Describe.spatialReference",
                     "Describe.extent", "Describe.pixelType"):
            answers[name] = "FAIL: no describe object"

    step("Raster.pixelType", lambda: arcpy.Raster(url).pixelType)
    step("Raster.bandCount", lambda: arcpy.Raster(url).bandCount)
    step("Raster.width", lambda: arcpy.Raster(url).width)

    out = os.path.join(HERE, tag + "_out.tif")
    step("CopyRaster", lambda: (arcpy.management.CopyRaster(layer, out),
                                str(os.path.getsize(out)) + " bytes")[1])

    return answers


print("arcpy " + arcpy.GetInstallInfo()["Version"])
print("  ours: " + OURS)
print("  esri: " + ESRI + "\n")

mine = run(OURS, "ours")
theirs = run(ESRI, "esri")

width = max(len(k) for k in mine)
differences = 0

print("  {0}  {1}  {2}".format("step".ljust(width), "ours".ljust(34), "esri"))
print("  " + "-" * (width + 2 + 34 + 2 + 34))

for name in mine:
    a, b = mine[name], theirs.get(name, "(not run)")
    same = (a.startswith("FAIL") == b.startswith("FAIL"))

    if not same:
        differences += 1

    print("  {0}  {1}  {2}  {3}".format(
        name.ljust(width), a[:34].ljust(34), b[:34], "" if same else "<-- differs"))

print()

if differences:
    print(str(differences) + " step(s) where this server and Esri's disagree.")
    sys.exit(1)

print("no step where this server and Esri's disagree.")
