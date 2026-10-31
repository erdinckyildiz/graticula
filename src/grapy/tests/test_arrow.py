"""arcpy.da.TableToArrowTable and arcpy.conversion.ExportToParquet in grapy, against the types *Apache Arrow in ArcGIS*
gives and shape records packed by hand from the ESRI Shapefile Technical Description (July 1998)."""
import datetime
import json
import os
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime  # noqa: E402

try:
    import pyarrow as pa
    import pyarrow.parquet as pq
except ImportError:  # pragma: no cover — CI's grapy jobs and the image install pyarrow
    pa = None


@unittest.skipIf(pa is None, "pyarrow is not installed")
class TableToArrowTable(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        _runtime.env._reset()
        self.fc = os.path.join(self.directory, "parcels.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "parcels.gpkg", "POLYGON", spatial_reference=4326)
        for name, kind in (("code", "SHORT"), ("area", "DOUBLE"), ("name", "TEXT"), ("seen", "DATE"),
                           ("doc", "BLOB")):
            arcpy.management.AddField(self.fc, name, kind)
        square = arcpy.Polygon(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(1, 0), arcpy.Point(1, 1),
                                            arcpy.Point(0, 1)]), 4326)
        with arcpy.da.InsertCursor(self.fc, ["code", "area", "name", "seen", "doc", "SHAPE@"]) as rows:
            rows.insertRow((7, 1.0, "x" * 6000, datetime.datetime(2026, 10, 31, 12, 30), b"\x01\x02", square))
            rows.insertRow((None, None, None, None, None, None))

    def tearDown(self):
        _runtime.env._reset()

    def test_the_documented_example_counts_its_rows(self):
        table = arcpy.da.TableToArrowTable(self.fc)
        self.assertIsInstance(table, pa.Table)
        self.assertEqual(arcpy.management.GetCount(table)[0], "2")

    def test_field_types_and_metadata_follow_the_page(self):
        schema = arcpy.da.TableToArrowTable(self.fc).schema
        types = {f.name: f.type for f in schema}
        self.assertEqual(types["fid"], pa.int64())
        self.assertEqual(schema.field("fid").metadata, {b"esri.oid": b"esri.int64"})
        self.assertEqual((types["code"], types["area"], types["name"]), (pa.int16(), pa.float64(), pa.string()))
        self.assertEqual(types["seen"], pa.timestamp("ms"))
        self.assertEqual(types["doc"], pa.binary())
        self.assertEqual(schema.field("doc").metadata, {b"esri.interop.type": b"esri.blob"})
        shape = schema.field("geom")
        self.assertEqual(shape.type, pa.binary())
        self.assertEqual(shape.metadata[b"esri.encoding"], b"EsriShape")
        self.assertIn(b"GCS_WGS_1984", shape.metadata[b"esri.sr_wkt"])

    def test_values_text_trimmed_at_5000_and_nulls(self):
        table = arcpy.da.TableToArrowTable(self.fc, ["code", "name", "seen"])
        self.assertEqual(table.column_names, ["code", "name", "seen"])
        self.assertEqual(table.column("code").to_pylist(), [7, None])
        self.assertEqual(len(table.column("name")[0].as_py()), 5000)
        self.assertEqual(table.column("seen")[0].as_py(), datetime.datetime(2026, 10, 31, 12, 30))

    def test_esri_shape_is_the_technical_descriptions_polygon_record(self):
        record = arcpy.da.TableToArrowTable(self.fc, ["geom"]).column("geom").to_pylist()
        # Polygon (5), box, one part, five points, part 0 at 0, the ring clockwise from (0, 0).
        ring = [(0, 0), (0, 1), (1, 1), (1, 0), (0, 0)]
        expected = struct.pack("<i4dii", 5, 0, 0, 1, 1, 1, 5) + struct.pack("<i", 0) + b"".join(
            struct.pack("<dd", *p) for p in ring)
        self.assertEqual(record[0], expected)
        self.assertEqual(record[1], None)

    def test_a_point_z_record_and_the_other_encodings(self):
        fc = os.path.join(self.directory, "wells.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "wells.gpkg", "POINT", has_z="ENABLED",
                                            spatial_reference=4326)
        with arcpy.da.InsertCursor(fc, ["SHAPE@"]) as rows:
            rows.insertRow((arcpy.PointGeometry(arcpy.Point(3, 4, 5), 4326, True),))
        shape = arcpy.da.TableToArrowTable(fc).column("geom")[0].as_py()
        self.assertEqual(shape, struct.pack("<idddd", 11, 3, 4, 5, -1e39))
        wkt = arcpy.da.TableToArrowTable(fc, geometry_encoding="WKT")
        self.assertEqual(wkt.schema.field("geom").type, pa.string())
        self.assertEqual(wkt.column("geom")[0].as_py(), "POINT Z (3 4 5)")
        self.assertEqual(json.loads(arcpy.da.TableToArrowTable(fc, geometry_encoding="GEOJSON").column("geom")[0]
                                    .as_py())["coordinates"], [3.0, 4.0, 5.0])
        self.assertEqual(json.loads(arcpy.da.TableToArrowTable(fc, geometry_encoding="ESRIJSON").column("geom")[0]
                                    .as_py())["x"], 3.0)
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.da.TableToArrowTable(fc, geometry_encoding="KML")

    def test_where_clause_and_no_geometry_column_without_a_geometry_field(self):
        table = arcpy.da.TableToArrowTable(self.fc, "code", "code = 7", geometry_encoding="WKT")
        self.assertEqual((table.num_rows, table.column_names), (1, ["code"]))

    def test_export_to_parquet_writes_geoparquet(self):
        out = os.path.join(self.directory, "parcels.parquet")
        arcpy.conversion.ExportToParquet(self.fc, out)
        read = pq.read_table(out)
        geo = json.loads(read.schema.metadata[b"geo"])
        self.assertEqual((geo["version"], geo["primary_column"]), ("1.1.0", "geom"))
        self.assertEqual(geo["columns"]["geom"]["encoding"], "WKB")
        self.assertEqual(geo["columns"]["geom"]["bbox"], [0.0, 0.0, 1.0, 1.0])
        self.assertEqual(geo["columns"]["geom"]["crs"]["id"]["code"], 4326)
        import shapely
        self.assertEqual(shapely.from_wkb(read.column("geom")[0].as_py()).area, 1.0)
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.conversion.ExportToParquet(self.fc, os.path.join(self.directory, "parcels.txt"))

    def test_create_parquet_cache_is_refused_with_its_own_reason(self):
        with self.assertRaises(arcpy.NotBuiltError) as said:
            arcpy.da.CreateParquetCache(os.path.join(self.directory, "x.parquet"))
        self.assertIn("persistent local cache", str(said.exception))


if __name__ == "__main__":
    unittest.main()
