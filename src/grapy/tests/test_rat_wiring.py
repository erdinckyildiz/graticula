"""A raster's attribute table through the field tools, ListFields, Describe and the raster–feature conversions."""
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class RatWiring(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        # 1 1      cells of 10 m, the lower-left corner at (0, 0)
        # 2 3
        self.raster = arcpy.NumPyArrayToRaster(np.array([[1, 1], [2, 3]], dtype=np.int32), arcpy.Point(0, 0), 10, 10) \
            .save(os.path.join(self.directory, "zones.tif"))
        arcpy.env.outputCoordinateSystem = None
        arcpy.management.BuildRasterAttributeTable(self.raster)

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def test_field_tools_list_fields_and_describe_on_the_table(self):
        arcpy.management.AddField(self.raster, "label", "TEXT")
        arcpy.management.CalculateField(self.raster, "label", "'v' + str(!Value!)")
        arcpy.management.CalculateFields(self.raster, "PYTHON3", [["twice", "!Count! * 2", None, "DOUBLE"]])
        self.assertEqual(self.rows(self.raster, ["Value", "Count", "label", "twice"]),
                         [(1, 2, "v1", 4.0), (2, 1, "v2", 2.0), (3, 1, "v3", 2.0)])
        self.assertEqual([(f.name, f.type) for f in arcpy.ListFields(self.raster)],
                         [("OID", "OID"), ("Value", "Integer"), ("Count", "Integer"), ("label", "String"),
                          ("twice", "Double")])
        described = arcpy.Describe(self.raster)
        self.assertEqual((described.hasOID, described.OIDFieldName, described.primaryField),
                         (True, "OID", 0))
        self.assertEqual([f.name for f in described.fields][1:], ["Value", "Count", "label", "twice"])
        arcpy.management.CalculateField(self.raster, "twice", "0")            # a where-free update of every row
        arcpy.management.DeleteField(self.raster, "twice")
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.DeleteField(self.raster, "Value")
        arcpy.management.DeleteField(self.raster, "label", "KEEP_FIELDS")
        self.assertEqual([f.name for f in arcpy.ListFields(self.raster)], ["OID", "Value", "label"])

    def test_raster_to_polygon_and_point_carry_a_table_field(self):
        arcpy.management.AddField(self.raster, "label", "TEXT")
        arcpy.management.CalculateField(self.raster, "label", "'v' + str(!Value!)")
        polygons = arcpy.conversion.RasterToPolygon(self.raster, os.path.join(self.directory, "p.gpkg"), "NO_SIMPLIFY",
                                                    "label")[0]
        self.assertEqual(sorted(self.rows(polygons, ["gridcode", "label", "SHAPE@AREA"])),
                         [(1, "v1", 200.0), (2, "v2", 100.0), (3, "v3", 100.0)])
        points = arcpy.conversion.RasterToPoint(self.raster, os.path.join(self.directory, "pt.gpkg"), "label")[0]
        self.assertEqual([f.name for f in arcpy.ListFields(points)][1:-1], ["pointid", "label"])
        self.assertEqual(self.rows(points, ["label", "SHAPE@XY"]),
                         [("v1", (5.0, 15.0)), ("v1", (15.0, 15.0)), ("v2", (5.0, 5.0)), ("v3", (15.0, 5.0))])

    def test_a_text_value_field_makes_an_integer_raster_with_its_text(self):
        stands = os.path.join(self.directory, "stands.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "stands.gpkg", "POLYGON", spatial_reference=3857)
        arcpy.management.AddField(stands, "species", "TEXT")
        with arcpy.da.InsertCursor(stands, ["species", "SHAPE@"]) as written:
            written.insertRow(("pine", "POLYGON((0 0,10 0,10 20,0 20,0 0))"))
            written.insertRow(("oak", "POLYGON((10 0,30 0,30 20,10 20,10 0))"))
        out = arcpy.conversion.PolygonToRaster(stands, "species", os.path.join(self.directory, "stands.tif"),
                                               cellsize=10)[0]
        # oak sorts before pine: oak 1 (four cells, x 10..30), pine 2 (two cells, x 0..10).
        self.assertEqual(arcpy.Raster(out)._array.tolist(), [[2, 1, 1], [2, 1, 1]])
        self.assertEqual(self.rows(out, ["Value", "Count", "species"]), [(1, 4, "oak"), (2, 2, "pine")])


if __name__ == "__main__":
    unittest.main()
