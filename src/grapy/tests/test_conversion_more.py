"""grapy.conversion's tables, Excel, GPX, ASCII and float grids — ADR-224.

Every expected text, value and coordinate below is written out by hand; none is read back from the code under test.
"""
import json
import os
import sys
import tempfile
import unittest
import zipfile

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


def box(x0, y0, x1, y1):
    return f"POLYGON(({x0} {y0},{x1} {y0},{x1} {y1},{x0} {y1},{x0} {y0}))"


class ConversionMore(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = None
        self.parcels = self.make("parcels", "POLYGON", [("name", "TEXT"), ("kind", "TEXT"), ("value", "DOUBLE")],
                                 [("p0", "a", 1.0, box(0, 0, 50, 50)), ("p1", "a", 2.5, box(100, 0, 150, 50)),
                                  ("p2", "b", None, box(200, 0, 250, 50))])

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def make(self, name, shape, fields, rows, srid=3857):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=srid)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def out(self, name):
        return os.path.join(self.directory, name)

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as read:
            return [tuple(r) for r in read]

    def text(self, path):
        with open(path, encoding="utf-8") as read:
            return read.read()

    # ---- tables

    def test_export_table_writes_delimited_text_and_tables(self):
        csv = arcpy.conversion.ExportTable(self.parcels, self.out("p.csv"))[0]
        self.assertEqual(self.text(csv), "fid,name,kind,value\n1,p0,a,1.0\n2,p1,a,2.5\n3,p2,b,\n")
        tsv = arcpy.conversion.ExportTable(self.parcels, self.out("p.tsv"), "kind = 'a'", sort_field=[["value", "DESCENDING"]])[0]
        self.assertEqual(self.text(tsv), "fid\tname\tkind\tvalue\n2\tp1\ta\t2.5\n1\tp0\ta\t1.0\n")
        table = arcpy.conversion.ExportTable(self.parcels, self.out("t.gpkg"))[0]
        self.assertEqual(self.rows(table, ["name", "SHAPE@"]), [("p0", None), ("p1", None), ("p2", None)])
        gpkg = arcpy.management.CreateFileGDB(self.directory, "store.gdb")[0]
        arcpy.conversion.FeatureClassToGeodatabase(self.parcels, gpkg)
        arcpy.conversion.TableToGeodatabase(table, gpkg)
        self.assertTrue(arcpy.Exists(gpkg + "/parcels") and arcpy.Exists(gpkg + "/t"))
        self.assertEqual(self.rows(gpkg + "/parcels", ["SHAPE@AREA"])[0], (2500.0,))
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.conversion.FeatureClassToGeodatabase(self.parcels, self.out("x.gdb"))

    def test_table_to_excel_writes_a_sheet_a_workbook_reader_can_open(self):
        from xml.etree import ElementTree
        book = arcpy.conversion.TableToExcel(self.parcels, self.out("p.xlsx"))[0]
        main = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
        with zipfile.ZipFile(book) as package:
            sheets = [s.get("name") for s in ElementTree.fromstring(package.read("xl/workbook.xml")).iter(main + "sheet")]
            sheet = ElementTree.fromstring(package.read("xl/worksheets/sheet1.xml"))
        self.assertEqual(sheets, ["parcels"])
        cells = {c.get("r"): "".join(c.itertext()) for c in sheet.iter(main + "c")}
        self.assertEqual((cells["A1"], cells["B1"], cells["D1"], cells["B2"], cells["D3"]), ("fid", "name", "value", "p0", "2.5"))
        self.assertNotIn("D4", cells)                                         # p2's null value is an empty cell
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.conversion.TableToExcel(self.parcels, self.out("p.xls"))

    def write_xlsx(self, path, rows, shared):
        """A workbook written by hand: ``rows`` of (reference, type, text) cells, ``shared`` the shared strings."""
        cells = "".join(f'<c r="{r}"{f" t={chr(34)}{t}{chr(34)}" if t else ""}><v>{v}</v></c>' for r, t, v in rows)
        strings = "".join(f"<si><t>{s}</t></si>" for s in shared)
        with zipfile.ZipFile(path, "w") as package:
            package.writestr("xl/workbook.xml", '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
                             'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>'
                             '<sheet name="Data" sheetId="1" r:id="rId1"/></sheets></workbook>')
            package.writestr("xl/_rels/workbook.xml.rels", '<Relationships xmlns="http://schemas.openxmlformats.org/package/'
                             '2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/>'
                             '</Relationships>')
            package.writestr("xl/sharedStrings.xml", '<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
                             f"{strings}</sst>")
            package.writestr("xl/worksheets/sheet1.xml", '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/'
                             f'2006/main"><sheetData><row>{cells}</row></sheetData></worksheet>')

    def test_excel_to_table_types_each_column_from_its_values(self):
        book = self.out("hand.xlsx")
        # Header: city, n, x, (empty), big.  Strings 0..3 are city, n, x, big; 4..5 Ankara, Izmir; 6 is "mixed".
        self.write_xlsx(book, [("A1", "s", 0), ("B1", "s", 1), ("C1", "s", 2), ("E1", "s", 3),
                               ("A2", "s", 4), ("B2", "", 1), ("C2", "", 1.5), ("D2", "", 7), ("E2", "", 100000),
                               ("A3", "s", 5), ("B3", "", 2), ("C3", "e", "#N/A"), ("D3", "s", 6), ("E3", "", 3)],
                        ["city", "n", "x", "big", "Ankara", "Izmir", "mixed"])
        table = arcpy.conversion.ExcelToTable(book, self.out("hand.gpkg"))[0]
        fields = [(f.name, f.type) for f in arcpy.ListFields(table)][1:]  # a table lists no shape field (GeoPackage §2.4)
        self.assertEqual(fields, [("city", "String"), ("n", "SmallInteger"), ("x", "Double"), ("COL_D", "String"),
                                  ("big", "Integer")])
        self.assertEqual(self.rows(table, ["city", "n", "x", "COL_D", "big"]),
                         [("Ankara", 1, 1.5, "7", 100000), ("Izmir", 2, None, "mixed", 3)])
        plain = arcpy.conversion.ExcelToTable(book, self.out("plain.gpkg"), "Data", 0, "A2:B3")[0]
        self.assertEqual([f.name for f in arcpy.ListFields(plain)][1:], ["COL_A", "COL_B"])
        self.assertEqual(self.rows(plain, ["COL_A", "COL_B"]), [("Ankara", 1), ("Izmir", 2)])

    def test_features_to_json_projects_to_wgs84_when_asked(self):
        # 111319.49079327357 m east on the spherical Mercator is one degree of longitude.
        point = self.make("pt", "POINT", [("n", "TEXT")], [("a", "POINT(111319.49079327357 0)")])
        out = arcpy.conversion.FeaturesToJSON(point, self.out("pt.json"), outputToWGS84="WGS84")[0]
        with open(out, encoding="utf-8") as read:
            document = json.load(read)
        self.assertEqual(document["spatialReference"]["wkid"], 4326)
        self.assertAlmostEqual(document["features"][0]["geometry"]["x"], 1.0)

    # ---- GPX

    def test_gpx_both_ways(self):
        gpx = self.out("walk.gpx")
        with open(gpx, "w", encoding="utf-8") as written:
            written.write('<?xml version="1.0"?><gpx version="1.1" creator="hand" xmlns="http://www.topografix.com/GPX/1/1">'
                          '<wpt lat="40.5" lon="30.25"><ele>12.5</ele><time>2026-10-29T08:00:00Z</time><name>Spring</name>'
                          '<cmt>cold</cmt><desc>water</desc><sym>Flag</sym></wpt>'
                          '<trk><name>Ridge</name><desc>walk</desc><trkseg><trkpt lat="0" lon="0"><ele>1</ele></trkpt>'
                          '<trkpt lat="0" lon="1"/></trkseg><trkseg><trkpt lat="1" lon="1"/></trkseg></trk></gpx>')
        points = arcpy.conversion.GPXtoFeatures(gpx, self.out("walk.gpkg"))[0]
        self.assertEqual(self.rows(points, ["Name", "Descript", "Type", "Comment", "Symbol", "DateTimeS", "Elevation", "SHAPE@XY"]),
                         [("Spring", "water", "WPT", "cold", "Flag", "2026-10-29T08:00:00Z", 12.5, (30.25, 40.5)),
                          ("Ridge", "walk", "TRKPT", None, None, None, 1.0, (0.0, 0.0)),
                          ("Ridge", "walk", "TRKPT", None, None, None, None, (1.0, 0.0)),
                          ("Ridge", "walk", "TRKPT", None, None, None, None, (1.0, 1.0))])
        self.assertEqual(arcpy.Describe(points).spatialReference.factoryCode, 4326)
        lines = arcpy.conversion.GPXtoFeatures(gpx, self.out("ridge.gpkg"), "TRACKS_AS_LINES")[0]
        self.assertEqual(self.rows(lines, ["Name", "Type", "SHAPE@LENGTH"]), [("Ridge", "TRKPT", 2.0)])

        stops = self.make("stops", "POINT", [("n", "TEXT"), ("d", "TEXT")], [("a", "first & best", "POINT(30 40)")], srid=4326)
        written = arcpy.conversion.FeaturesToGPX(stops, self.out("stops.gpx"), "n", "d")[0]
        self.assertIn('<wpt lat="40.0" lon="30.0"><name>a</name><desc>first &amp; best</desc></wpt>', self.text(written))
        path = self.make("path", "POLYLINE", [("n", "TEXT")], [("r", "LINESTRING(0 0,1 1)")], srid=4326)
        track = self.text(arcpy.conversion.FeaturesToGPX(path, self.out("path.gpx"), "n")[0])
        self.assertIn('<trk><name>r</name><trkseg><trkpt lat="0.0" lon="0.0"></trkpt><trkpt lat="1.0" lon="1.0"></trkpt>'
                      '</trkseg></trk>', track)

    # ---- ASCII and float grids

    def raster(self, values, nodata=None):
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        made = arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0, 0), 10, 10, nodata)
        arcpy.env.outputCoordinateSystem = None
        return made.save(self.out(f"g{len(os.listdir(self.directory))}.tif"))

    def test_ascii_grid_both_ways(self):
        grid = self.raster([[1, 2], [3, -1]], nodata=-1)
        asc = arcpy.conversion.RasterToASCII(grid, self.out("g.asc"))[0]
        self.assertEqual(self.text(asc), "NCOLS 2\nNROWS 2\nXLLCORNER 0\nYLLCORNER 0\nCELLSIZE 10\nNODATA_VALUE -9999\n"
                                         "1 2\n3 -9999\n")
        hand = self.out("hand.asc")
        with open(hand, "w", encoding="ascii") as written:
            written.write("ncols 3\nnrows 2\nxllcenter 5\nyllcenter 105\ncellsize 10\nNODATA_value -1\n1 2 -1\n4 5 6\n")
        made = arcpy.Raster(arcpy.conversion.ASCIIToRaster(hand, self.out("hand.tif"))[0])
        self.assertEqual((made.extent.XMin, made.extent.YMin, made.extent.YMax), (0.0, 100.0, 120.0))
        self.assertEqual(made.pixelType, "S32")
        # Raster.read is rows, columns and bands, NoData as the raster's own NoData value, as its page documents.
        self.assertEqual(made.read(nodata_to_value=-1)[:, :, 0].tolist()[0], [1, 2, -1])
        with open(hand, "w", encoding="ascii") as written:
            written.write("NCOLS 1\nNROWS 1\nXLLCORNER 0\nYLLCORNER 0\nCELLSIZE 1\n2.5\n")
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.conversion.ASCIIToRaster(hand, self.out("frac.tif"))
        self.assertEqual(arcpy.Raster(arcpy.conversion.ASCIIToRaster(hand, self.out("frac.tif"), "FLOAT")[0]).maximum, 2.5)

    def test_float_grid_both_ways(self):
        grid = self.raster([[1.5, 2.0], [-1.0, 4.0]], nodata=-1.0)
        flt = arcpy.conversion.RasterToFloat(grid, self.out("g.flt"))[0]
        self.assertEqual(np.fromfile(flt, "<f4").tolist(), [1.5, 2.0, -9999.0, 4.0])
        self.assertEqual(self.text(self.out("g.hdr")), "NCOLS 2\nNROWS 2\nXLLCORNER 0\nYLLCORNER 0\nCELLSIZE 10\n"
                                                       "NODATA_VALUE -9999\nBYTEORDER LSBFIRST\n")
        np.array([7.0, -5.0, 1.25], ">f4").tofile(self.out("hand.flt"))
        with open(self.out("hand.hdr"), "w", encoding="ascii") as written:
            written.write("ncols 3\nnrows 1\nxllcorner 100\nyllcorner 0\ncellsize 2\nnodata_value -5\nbyteorder MSBFIRST\n")
        made = arcpy.Raster(arcpy.conversion.FloatToRaster(self.out("hand.flt"), self.out("hand.tif"))[0])
        self.assertEqual((made.extent.XMin, made.extent.XMax, made.minimum, made.maximum), (100.0, 106.0, 1.25, 7.0))
        self.assertTrue(np.isnan(made.read(nodata_to_value=np.nan)[0][1][0]))


if __name__ == "__main__":
    unittest.main()
