"""arcpy.topographic's schema and data-movement tools, against hand-worked answers (ADR-251).

Workbooks are written by ``_topo_prod_schema.write_xlsx`` with the cells the test names; the shapefiles' dBASE headers
are read here byte by byte (dBASE III: a 32-byte header, then 32 bytes per field, the name in the first 11).
"""
import os
import struct
import sys
import tempfile
import unittest
import zipfile
from xml.etree import ElementTree

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from shapely.geometry import LineString, Point, Polygon  # noqa: E402

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _layers  # noqa: E402
from grapy import _topo_prod_schema as schema  # noqa: E402
from grapy._runtime import MESSAGES, ExecuteError, NotBuiltError  # noqa: E402

UTM = 32633


def square(x0, y0, x1, y1):
    return Polygon([(x0, y0), (x1, y0), (x1, y1), (x0, y1)])


def dbf_fields(path):
    with open(path, "rb") as handle:
        data = handle.read()
    count = struct.unpack("<I", data[4:8])[0]
    header = struct.unpack("<H", data[8:10])[0]
    names = []
    for i in range((header - 33) // 32):
        names.append(data[32 + 32 * i:43 + 32 * i].split(b"\0")[0].decode("ascii"))
    return names, count


class Fixture(unittest.TestCase):
    def setUp(self):
        _layers.LAYERS.clear()
        MESSAGES.clear()
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def gpkg(self, name):
        return arcpy.management.CreateFileGDB(self.directory, name)[0]

    def fc(self, gpkg, name, kind, fields=(), rows=(), srid=UTM):
        path = arcpy.management.CreateFeatureclass(gpkg, name, kind, spatial_reference=srid)[0]
        for field, field_type in fields:
            arcpy.management.AddField(path, field, field_type)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for row in rows:
                cursor.insertRow(list(row))
        return path

    def values(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as cursor:
            return [tuple(r) for r in cursor]

    def warnings(self):
        return [m["description"] for m in MESSAGES if m["type"] == "esriJobMessageTypeWarning"]


class Workbooks(Fixture):
    def test_a_workbook_reads_its_cells(self):
        path = os.path.join(self.directory, "book.xlsx")
        schema.write_xlsx(path, [("Dataset Mapping", ["Id", "SourceDataset", "WhereClause"],
                                  [[1, "Roads", "LANES > 1"], [2, "Rivers", None]]), ("Empty", ["A"], [])])
        with zipfile.ZipFile(path) as z:   # ECMA-376: the workbook part names each sheet
            workbook = ElementTree.fromstring(z.read("xl/workbook.xml"))
        self.assertEqual([s.get("name") for s in workbook.iter("{%s}sheet" % schema._MAIN)], ["Dataset Mapping", "Empty"])
        book = schema.read_xlsx(path)
        self.assertEqual(book["Dataset Mapping"], [{"Id": 1, "SourceDataset": "Roads", "WhereClause": "LANES > 1"},
                                                   {"Id": 2, "SourceDataset": "Rivers", "WhereClause": None}])
        self.assertEqual(book["Empty"], [])

    def test_an_xls_workbook_is_refused_by_name(self):
        with self.assertRaises(NotBuiltError) as said:
            arcpy.topographic.GenerateGeodatabaseFromExcel(os.path.join(self.directory, "old.xls"),
                                                          os.path.join(self.directory, "out.gpkg"))
        self.assertIn(".xls", str(said.exception))

    def test_a_geodatabase_is_made_from_a_workbook(self):
        # One spatial reference (EPSG:32633), one feature dataset, a line class with a subtype field TYPE (1 Road
        # default, 2 Track), a coded domain Surface (1 Paved, 2 Gravel) on SURF in subtype 1, and SPEED defaulting
        # to 50.
        path = os.path.join(self.directory, "schema.xlsx")
        sheets = [
            ("SpatialReferences", ["SpatialReference_ID", "Description", "FactoryCode", "SpatialReferenceString"],
             [[1, "WGS 84 / UTM 33N", 32633, None]]),
            ("DatasetContainers", ["Dataset_ID", "CreateDataset", "DatasetType", "DatasetName", "SpatialReference_ID"],
             [[1, True, "esriDTFeatureDataset", "Transport", 1]]),
            ("ObjectClasses", ["ObjectClass_ID", "Dataset_ID", "CreateObjectClass", "ObjectClassName",
                               "ObjectClassAlias", "ObjectClassDescription", "ObjectClassDataType",
                               "ObjectClassFeatureType", "ObjectClassGeometry", "SpatialReference_ID", "HasZ", "HasM"],
             [[1, 1, True, "Roads", "Roads", None, "esriDTFeatureClass", "esriFTSimple", "esriGeometryPolyline", 1,
               False, False],
              [2, -9999, "FALSE", "Skipped", None, None, "esriDTTable", None, None, None, False, False]]),
            ("Subtypes", ["Subtype_ID", "ObjectClass_ID", "CreateSubtype", "SubtypeField", "SubtypeNumber",
                          "SubtypeName", "SubtypeDescription", "SubtypeDefault"],
             [[1, 1, True, "TYPE", 1, "Road", "Road", True], [2, 1, True, "TYPE", 2, "Track", "Track", False]]),
            ("Fields", ["Field_ID", "ObjectClass_ID", "Subtype_ID", "CreateField", "FieldName", "FieldAlias",
                        "FieldDescription", "FieldDomainName", "FieldType", "FieldDefaultValue", "FieldEditable",
                        "FieldIsNullable"],
             [[1, 1, 1, True, "SURF", None, None, "Surface", "esriFieldTypeSmallInteger", None, True, True],
              [2, 1, -9999, True, "SPEED", None, None, None, "esriFieldTypeInteger", 50, True, True]]),
            ("Domains", ["Domain_ID", "CreateDomain", "Name", "Description", "DomainCode", "DomainMin", "DomainMax",
                         "DomainDescription", "DomainDataType", "DomainFieldType", "DomainSplitPolicy",
                         "DomainMergePolicy"],
             [[1, True, "Surface", "Road surface", 1, None, None, "Paved", "esriDTCodedValue", "Short",
               "esriSPTDefaultValue", "esriMPTDefaultValue"],
              [2, True, "Surface", "Road surface", 2, None, None, "Gravel", "esriDTCodedValue", "Short",
               "esriSPTDefaultValue", "esriMPTDefaultValue"]]),
        ]
        schema.write_xlsx(path, sheets)
        out = os.path.join(self.directory, "made.gpkg")
        arcpy.topographic.GenerateGeodatabaseFromExcel(path, out)
        roads = f"{out}/Transport/Roads"
        described = {f.name: f.type for f in arcpy.ListFields(roads)}
        self.assertEqual((described["SURF"], described["SPEED"], described["TYPE"]),
                         ("SmallInteger", "Integer", "Integer"))
        self.assertEqual(arcpy.Describe(roads).spatialReference.factoryCode, 32633)
        self.assertEqual({k: v["Name"] for k, v in arcpy.da.ListSubtypes(roads).items()}, {1: "Road", 2: "Track"})
        self.assertEqual(arcpy.da.ListSubtypes(roads)[1]["FieldValues"]["SURF"][1].name, "Surface")
        with arcpy.da.InsertCursor(roads, ["SHAPE@"]) as cursor:   # a new row takes subtype 1 and SPEED 50
            cursor.insertRow([LineString([(0, 0), (1, 1)])])
        self.assertEqual(self.values(roads, ["TYPE", "SPEED"]), [(1, 50)])
        self.assertFalse(os.path.exists(os.path.join(self.directory, "Skipped.gpkg")))
        self.assertNotIn("Skipped", [t.rsplit("/", 1)[-1] for t in schema.classes(out)])

    def test_the_workbook_of_a_geodatabase(self):
        gpkg = self.gpkg("source")
        arcpy.management.CreateDomain(gpkg, "Surface", "Road surface", "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(gpkg, "Surface", 1, "Paved")
        roads = self.fc(gpkg, "Roads", "POLYLINE", [("SURF", "SHORT")])
        arcpy.management.AssignDomainToField(roads, "SURF", "Surface")
        out = os.path.join(self.directory, "schema.xlsx")
        arcpy.topographic.GenerateExcelFromGeodatabase(gpkg, out)
        book = schema.read_xlsx(out)
        self.assertEqual(sorted(book), sorted(schema._SHEETS))
        self.assertEqual([(r["FactoryCode"], r["SpatialReference_ID"]) for r in book["SpatialReferences"]], [(32633, 1)])
        roads_row = book["ObjectClasses"][0]
        self.assertEqual((roads_row["ObjectClassName"], roads_row["ObjectClassGeometry"], roads_row["Dataset_ID"]),
                         ("Roads", "esriGeometryPolyline", -9999))
        self.assertEqual([(r["FieldName"], r["FieldType"], r["FieldDomainName"]) for r in book["Fields"]],
                         [("SURF", "esriFieldTypeSmallInteger", "Surface")])
        self.assertEqual([(r["Name"], r["DomainCode"], r["DomainDescription"], r["DomainDataType"])
                          for r in book["Domains"]], [("Surface", 1, "Paved", "esriDTCodedValue")])


class CrossReference(Fixture):
    def setUp(self):
        super().setUp()
        self.source, self.target = self.gpkg("source"), self.gpkg("target")
        self.fc(self.source, "Roads", "POLYLINE", [("NAME", "TEXT"), ("LANES", "LONG")],
                [("Main", 2, LineString([(0, 0), (1, 0)])), ("Side", 1, LineString([(0, 1), (1, 1)])),
                 ("Back", 3, LineString([(0, 2), (1, 2)]))])
        self.fc(self.source, "Rivers", "POLYLINE")
        self.fc(self.target, "Roads", "POLYLINE", [("NAME", "TEXT"), ("LANES", "DOUBLE"), ("SPEED", "LONG")])
        self.fc(self.target, "Buildings", "POLYGON")

    def test_same_names_are_mapped_with_the_documented_warnings(self):
        out = os.path.join(self.directory, "xref.gpkg")
        arcpy.topographic.CreateCrossReferenceGeodatabase(self.source, self.target, out)
        self.assertEqual(self.values(f"{out}/DatasetMapping", ["Id", "SourceDataset", "TargetDataset"]),
                         [(1, "Roads", "Roads")])
        self.assertEqual(self.values(f"{out}/FieldMapping", ["DatasetMapId", "SourceField", "TargetField"]),
                         [(1, "NAME", "NAME"), (1, "LANES", "LANES")])
        self.assertEqual(self.values(f"{out}/Version", ["Version"]), [("1",)])
        said = " ".join(self.warnings())
        for expected in ("'Rivers' has no target", "'Buildings' has no source", "'Roads.SPEED' has no source",
                         "'Roads.LANES' is Integer in the source and Double"):
            self.assertIn(expected, said)

    def test_a_mapping_workbook_loads_data(self):
        # Only roads with more than one lane; Main becomes Primary by the value mapping: two rows, Primary 2 and Back 3.
        book = os.path.join(self.directory, "mapping.xlsx")
        schema.write_xlsx(book, [
            ("Dataset Mapping", ["Id", "SourceDataset", "TargetDataset", "WhereClause", "Subtype"],
             [[1, "Roads", "Roads", "LANES > 1", None]]),
            ("Field Mapping", ["Id", "DatasetMapId", "SourceField", "TargetField", "WhereClause"],
             [[1, 1, "NAME", "NAME", None], [2, 1, "LANES", "LANES", None]]),
            ("Value Mapping", ["Id", "FieldMappingId", "FromValue", "ToValue"], [[1, 1, "Main", "Primary"]])])
        xref = os.path.join(self.directory, "xref.gpkg")
        arcpy.topographic.CreateCrossReferenceGeodatabase(self.source, self.target, xref, book)
        arcpy.topographic.LoadData(xref, self.source, self.target)
        self.assertEqual(sorted(self.values(f"{self.target}/Roads", ["NAME", "LANES"])), [("Back", 3.0), ("Primary", 2.0)])

    def test_a_missing_source_dataset_is_warned_about(self):
        xref = os.path.join(self.directory, "xref.gpkg")
        arcpy.topographic.CreateCrossReferenceGeodatabase(self.source, self.target, xref)
        empty = self.gpkg("empty")
        arcpy.topographic.LoadData(xref, empty, self.target)
        self.assertIn("Source dataset [Roads] not found or is inaccessible.", self.warnings())


class Shapefiles(Fixture):
    def test_by_subtype_with_descriptions(self):
        gpkg = self.gpkg("source")
        arcpy.management.CreateDomain(gpkg, "Surface", "", "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(gpkg, "Surface", 1, "Paved")
        roads = self.fc(gpkg, "Roads", "POLYLINE", [("TYPE", "LONG"), ("SURF", "SHORT"), ("NOTE_", "TEXT")])
        arcpy.management.SetSubtypeField(roads, "TYPE")
        arcpy.management.AddSubtype(roads, 1, "Road")
        arcpy.management.AddSubtype(roads, 2, "Track")
        arcpy.management.AssignDomainToField(roads, "SURF", "Surface")
        with arcpy.da.InsertCursor(roads, ["TYPE", "SURF", "NOTE_", "SHAPE@"]) as cursor:
            cursor.insertRow([1, 1, "x", LineString([(0, 0), (1, 0)])])
            cursor.insertRow([2, None, None, LineString([(0, 1), (1, 1)])])
        folder = tempfile.mkdtemp()
        arcpy.topographic.GeodatabaseToShape(roads, folder, "VALUES_AND_DESCRIPTIONS", "DEFENSE_BY_SUBTYPE")
        # Road: TYPE, SURF and its description, NOTE (the trailing underscore stripped); Track holds no SURF or NOTE.
        self.assertEqual(dbf_fields(os.path.join(folder, "Road.dbf")), (["TYPE", "SURF", "SURF_D", "NOTE"], 1))
        self.assertEqual(dbf_fields(os.path.join(folder, "Track.dbf")), (["TYPE"], 1))
        self.assertTrue(os.path.exists(os.path.join(folder, "Road.shp")))

    def test_mgcp_names_by_feature_code(self):
        gpkg = self.gpkg("mgcp")
        rivers = self.fc(gpkg, "WatrcrsL", "POLYLINE", [("FCODE", "TEXT")],
                         [("BH140", LineString([(0, 0), (1, 0)]))])
        folder = tempfile.mkdtemp()
        arcpy.topographic.GeodatabaseToShape(rivers, folder, "VALUES", "MGCP")
        self.assertTrue(os.path.exists(os.path.join(folder, "LBH140.shp")))
        arcpy.topographic.GeodatabaseToShape(rivers, folder, "VALUES", "MUVD")
        self.assertTrue(os.path.exists(os.path.join(folder, "CBH140.shp")))


class Extract(Fixture):
    def setUp(self):
        super().setUp()
        self.source, self.target = self.gpkg("source"), self.gpkg("target")
        self.points = self.fc(self.source, "Spots", "POINT", [("H", "LONG")],
                              [(1, Point(1, 1)), (2, Point(5, 5)), (3, Point(50, 50))])
        self.lines = self.fc(self.source, "Roads", "POLYLINE", [], [(LineString([(0, 5), (20, 5)]),)])
        self.areas = self.fc(self.source, "Sheets", "POLYGON", [], [(square(0, 0, 10, 10),), (square(40, 40, 60, 60),)])
        arcpy.management.MakeFeatureLayer(self.areas, "sheet")
        arcpy.management.SelectLayerByAttribute("sheet", "NEW_SELECTION", "fid = 1")

    def test_filters_and_schema_only(self):
        # The sheet (0,0)-(10,10) holds spots 1 and 2; the road is clipped to 10 m; Sheets goes schema only.
        arcpy.topographic.ExtractDataByFeature(f"{self.points} USE_FILTERS;{self.lines} USE_FILTERS;"
                                               f"{self.areas} SCHEMA_ONLY", self.target, filter_feature="sheet",
                                               filter_type="CLIP")
        self.assertEqual(sorted(self.values(f"{self.target}/Spots", ["H"])), [(1,), (2,)])
        self.assertAlmostEqual(self.values(f"{self.target}/Roads", ["SHAPE@LENGTH"])[0][0], 10.0)
        self.assertEqual(self.values(f"{self.target}/Sheets", ["OID@"]), [])

    def test_a_where_clause_and_a_replica(self):
        arcpy.topographic.ExtractDataByFeature(f"{self.points} ALL_ROWS", self.target, where_clause="H >= 2")
        self.assertEqual(sorted(self.values(f"{self.target}/Spots", ["H"])), [(2,), (3,)])
        with self.assertRaises(NotBuiltError) as said:
            arcpy.topographic.ExtractDataByFeature(f"{self.points} ALL_ROWS", self.target,
                                                   checkout_replica="CHECKOUT_REPLICA")
        self.assertIn("checkout_replica", str(said.exception))
        with self.assertRaises(ExecuteError):
            arcpy.topographic.ExtractDataByFeature(f"{self.points} ALL_ROWS;{self.lines} ALL_ROWS", self.target,
                                                   where_clause="H >= 2")


class TopologyFiles(Fixture):
    def test_export_then_import(self):
        gpkg = self.gpkg("topo")
        for name in ("A", "B"):
            ds = arcpy.management.CreateFeatureDataset(gpkg, name, UTM)[0]
            arcpy.management.CreateFeatureclass(ds, "Parcels", "POLYGON")
        topology = arcpy.management.CreateTopology(f"{gpkg}/A", "T", 0.01)[0]
        arcpy.management.AddFeatureClassToTopology(topology, f"{gpkg}/A/Parcels", 2, 1)
        arcpy.management.AddRuleToTopology(topology, "Must Not Overlap (Area)", f"{gpkg}/A/Parcels")
        out = arcpy.topographic.ExportTopology(topology, self.directory, "parcels")[0]
        root = ElementTree.parse(out).getroot()
        self.assertEqual((root.tag, root.get("name"), float(root.get("clusterTolerance"))), ("GrapyTopology", "T", 0.01))
        self.assertEqual([(c.get("name"), c.get("xyRank")) for c in root.findall("FeatureClass")], [("Parcels", "2")])
        self.assertEqual([(r.get("type"), r.get("origin")) for r in root.findall("Rule")],
                         [("Must Not Overlap (Area)", "Parcels")])
        with open(out, encoding="utf-8") as handle:
            text = handle.read().replace('name="T"', 'name="T2"')
        copy = os.path.join(self.directory, "copy.xml")
        with open(copy, "w", encoding="utf-8") as handle:
            handle.write(text)
        made = arcpy.topographic.ImportTopology(f"{gpkg}/B", copy)[0]
        described = arcpy.Describe(made)
        self.assertEqual([c.rsplit("/", 1)[-1] for c in described.featureClassNames], ["Parcels"])
        self.assertAlmostEqual(described.clusterTolerance, 0.01)

    def test_a_file_grapy_did_not_write_is_an_error(self):
        other = os.path.join(self.directory, "esri.xml")
        with open(other, "w", encoding="utf-8") as handle:
            handle.write("<Topology/>")
        gpkg = self.gpkg("topo")
        arcpy.management.CreateFeatureDataset(gpkg, "A", UTM)
        with self.assertRaises(ExecuteError):
            arcpy.topographic.ImportTopology(f"{gpkg}/A", other)


class GeoNames(Fixture):
    GNS = ("rk\tufi\tuni\tfull_name\tnt\tlat_dd\tlong_dd\tname_rank\n"
           "1\t100\t10\tAlpha\tN\t40.5\t20.25\t1\n"
           "2\t100\t11\tAlfa\tV\t40.5\t20.25\t2\n"
           "3\t200\t20\tBeta\tN\t41\t21\t1\n")

    def setUp(self):
        super().setUp()
        self.file = os.path.join(self.directory, "gns.txt")
        with open(self.file, "w", encoding="utf-8") as handle:
            handle.write(self.GNS)
        gpkg = self.gpkg("names")
        self.points = self.fc(gpkg, "GeoNames", "POINT", [("UFI", "LONG"), ("UNI", "LONG"), ("full_name", "TEXT")],
                              srid=4326)
        self.names = arcpy.management.CreateTable(gpkg, "GeoNamesTable")[0]
        for field, kind in (("UFI", "LONG"), ("UNI", "LONG"), ("full_name", "TEXT"), ("nt", "TEXT"),
                            ("name_rank", "LONG")):
            arcpy.management.AddField(self.names, field, kind)

    def test_one_point_per_feature_and_one_row_per_name(self):
        arcpy.topographic.GeoNamesToGeodatabase(self.file, self.points, "DON'T_ALLOW_DUPLICATES", self.names)
        # Feature 100 at (20.25, 40.5) from its first row, feature 200 at (21, 41); three names.
        self.assertEqual(self.values(self.points, ["UFI", "UNI", "full_name", "SHAPE@X", "SHAPE@Y"]),
                         [(100, 10, "Alpha", 20.25, 40.5), (200, 20, "Beta", 21.0, 41.0)])
        self.assertEqual(sorted(self.values(self.names, ["UNI", "full_name"])), [(10, "Alpha"), (11, "Alfa"), (20, "Beta")])
        arcpy.topographic.GeoNamesToGeodatabase(self.file, self.points, "DON'T_ALLOW_DUPLICATES", self.names)
        self.assertEqual(len(self.values(self.points, ["UFI"])), 2)   # nothing loaded twice
        arcpy.topographic.GeoNamesToGeodatabase(self.file, self.points, "ALLOW_DUPLICATES", self.names)
        self.assertEqual(len(self.values(self.points, ["UFI"])), 4)

    def test_update_takes_the_stored_name_or_the_approved_one(self):
        arcpy.topographic.GeoNamesToGeodatabase(self.file, self.points, "DON'T_ALLOW_DUPLICATES", self.names)
        towns = self.fc(self.gpkg("towns"), "Towns", "POINT",
                        [("NFI", "TEXT"), ("NFN", "TEXT"), ("NAM", "TEXT")],
                        [("100", None, None, Point(0, 0)), ("100", "11", "old", Point(1, 1)),
                         ("999", None, None, Point(2, 2))])
        arcpy.topographic.UpdateGeoNames(towns, self.points, self.names, "NFI", "NFN", "NAM")
        # The first has no name ID: the approved (nt N) name of feature 100, Alpha (10). The second keeps 11: Alfa.
        self.assertEqual(self.values(towns, ["NFN", "NAM"]), [("10", "Alpha"), ("11", "Alfa"), (None, None)])


if __name__ == "__main__":
    unittest.main()
