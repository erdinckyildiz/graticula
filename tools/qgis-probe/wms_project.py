"""ADR-162 condition 2: a QGIS project saved against an ArcGIS Server WMSServer address, opened against this server.

1. QGIS saves a project holding Esri's sampleserver6 `USA/MapServer/WMSServer` layer `0` (what a project saved against
   ArcGIS Server holds: the service's WMSServer address and a numeric layer name).
2. The move: the service address in that file is replaced with this server's — the one edit an administrator makes
   (or QGIS's *Handle Unavailable Layers* makes) when the service moves. Nothing else in the file is touched.
3. QGIS opens the moved project, and its layer is checked valid and rendered at the parcels' extent.
Run with D:/OSGeo4W/bin/python-qgis-ltr.bat. Prints what it found; PNGs are written beside the projects.
"""
import os
import sys

from qgis.core import (QgsApplication, QgsMapRendererParallelJob, QgsMapSettings, QgsProject, QgsRasterLayer,
                       QgsRectangle, QgsCoordinateReferenceSystem)
from qgis.PyQt.QtCore import QSize
from qgis.PyQt.QtGui import QColor

WORK = r"D:\temp\grapyc-qgis"
os.makedirs(WORK, exist_ok=True)
ESRI = "https://sampleserver6.arcgisonline.com/arcgis/services/USA/MapServer/WMSServer"
OURS = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18575/rest/services/hosted/ci_parcels/MapServer/WMSServer"

app = QgsApplication([], False)
app.initQgis()


def render(layer, extent, crs, png):
    settings = QgsMapSettings()
    settings.setLayers([layer])
    settings.setDestinationCrs(crs)
    settings.setExtent(extent)
    settings.setOutputSize(QSize(400, 400))
    settings.setBackgroundColor(QColor(255, 255, 255, 0))
    job = QgsMapRendererParallelJob(settings)
    job.start()
    job.waitForFinished()
    image = job.renderedImage()
    image.save(png)
    drawn = sum(1 for x in range(0, 400, 2) for y in range(0, 400, 2) if QColor(image.pixel(x, y)).alpha() > 0)
    return drawn, [e.message for e in job.errors()]


# 1. The project saved against ArcGIS Server.
project = QgsProject.instance()
project.clear()
uri = f"contextualWMSLegend=0&crs=EPSG:4326&dpiMode=7&featureCount=10&format=image/png&layers=0&styles&url={ESRI}"
esri = QgsRasterLayer(uri, "Cities (ArcGIS Server)", "wms")
print("esri layer valid:", esri.isValid(), esri.error().summary()[:120] if not esri.isValid() else "")
project.addMapLayer(esri)
project.setCrs(QgsCoordinateReferenceSystem("EPSG:4326"))
saved = os.path.join(WORK, "saved-against-arcgis-server.qgs")
print("saved:", project.write(saved))
drawn, errors = render(esri, QgsRectangle(-125, 25, -66, 50), QgsCoordinateReferenceSystem("EPSG:4326"),
                       os.path.join(WORK, "esri.png"))
print("esri drawn sample pixels:", drawn, errors[:2])
project.clear()

# 2. The move: the service's address, and nothing else.
text = open(saved, encoding="utf-8").read()
print("address occurrences in the saved project:", text.count(ESRI))
moved = os.path.join(WORK, "moved-to-graticula.qgs")
open(moved, "w", encoding="utf-8").write(text.replace(ESRI, OURS))

# 3. Opened against this server.
print("read moved project:", project.read(moved))
layers = list(project.mapLayers().values())
for layer in layers:
    print("layer:", layer.name(), "valid", layer.isValid(), "source layers=",
          [p for p in layer.source().split("&") if p.startswith("layers=")])
parcels = QgsRectangle(32.8585, 39.9790, 32.8817, 39.9968)
drawn, errors = render(layers[0], parcels, QgsCoordinateReferenceSystem("EPSG:4326"), os.path.join(WORK, "moved.png"))
print("moved drawn sample pixels:", drawn, errors[:2])
app.exitQgis()
