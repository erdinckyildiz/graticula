"""arcpy's own top-level names in grapy — lists, names, units, geometry constructors and methods, environments,
messages, FeatureSet — each against an answer worked out by hand or quoted from the function's page."""
import json
import math
import os
import sqlite3
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime  # noqa: E402


def square(x, y, side=10, srid=3857):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y), arcpy.Point(x + side, y), arcpy.Point(x + side, y + side),
                                      arcpy.Point(x, y + side)]), srid)


def line(*xys, srid=3857):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(*xy) for xy in xys]), srid)


class _Folder(unittest.TestCase):
    """A folder holding pts.gpkg (points), multi.gpkg (parcels and roads), t.gpkg (a table), r.tif, x.csv and sub/."""

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        _runtime.env._reset()
        arcpy.env.workspace = self.directory
        arcpy.env.scratchWorkspace = self.directory
        self.pts = os.path.join(self.directory, "pts.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "pts.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(self.pts, "name", "TEXT")
        with arcpy.da.InsertCursor(self.pts, ["name", "SHAPE@"]) as rows:
            rows.insertRow(("a", arcpy.PointGeometry(arcpy.Point(1, 2), 3857)))
            rows.insertRow(("b", arcpy.PointGeometry(arcpy.Point(3, 4), 3857)))
        arcpy.management.CreateFeatureclass(self.directory, "multi.gpkg/parcels", "POLYGON", spatial_reference=3857)
        arcpy.management.CreateFeatureclass(self.directory, "multi.gpkg/roads", "POLYLINE", spatial_reference=3857)
        arcpy.management.CreateTable(self.directory, "t.gpkg")
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        arcpy.NumPyArrayToRaster(np.array([[1, 2], [3, 4]]), arcpy.Point(0, 0), 10, 10).save(
            os.path.join(self.directory, "r.tif"))
        arcpy.env.outputCoordinateSystem = None
        open(os.path.join(self.directory, "x.csv"), "w").close()
        os.makedirs(os.path.join(self.directory, "sub"))

    def tearDown(self):
        _runtime.env._reset()



class Workspace(_Folder):
    def test_list_functions_read_the_workspace(self):
        self.assertEqual(arcpy.ListFeatureClasses(), ["multi.gpkg/parcels", "multi.gpkg/roads", "pts.gpkg"])
        self.assertEqual(arcpy.ListFeatureClasses(feature_type="Polygon"), ["multi.gpkg/parcels"])
        self.assertEqual(arcpy.ListFeatureClasses("p*"), ["pts.gpkg"])
        self.assertEqual(arcpy.ListTables(), ["t.gpkg"])
        self.assertEqual(arcpy.ListRasters(), ["r.tif"])
        self.assertEqual(arcpy.ListRasters("*", "PNG"), [])
        self.assertEqual(arcpy.ListDatasets(), ["r.tif"])
        self.assertEqual(arcpy.ListDatasets(feature_type="Feature"), [])
        d = self.directory
        self.assertEqual(arcpy.ListWorkspaces(), [os.path.join(d, n) for n in ("multi.gpkg", "pts.gpkg", "sub", "t.gpkg")])
        self.assertEqual(arcpy.ListWorkspaces("*", "Folder"), [os.path.join(d, "sub")])
        self.assertEqual(arcpy.ListFiles("*.csv"), ["x.csv"])
        arcpy.env.workspace = os.path.join(d, "multi.gpkg")
        self.assertEqual(arcpy.ListFeatureClasses(), ["parcels", "roads"])
        self.assertEqual(arcpy.ListFiles(), [])

    def test_list_indexes_and_field_domains(self):
        connection = sqlite3.connect(self.pts)
        connection.execute('create unique index ix_name on pts ("name")')
        connection.commit()
        connection.close()
        (index,) = arcpy.ListIndexes(self.pts)
        self.assertEqual((index.name, index.fields, index.isUnique, index.isAscending), ("ix_name", ["name"], True, True))
        self.assertEqual(arcpy.ListIndexes(self.pts, "zz*"), [])
        arcpy.management.CreateDomain(self.pts, "kinds", "Kinds", "TEXT", "CODED")
        arcpy.management.AddCodedValueToDomain(self.pts, "kinds", "a", "Apple")
        arcpy.management.AssignDomainToField(self.pts, "name", "kinds")
        self.assertEqual({f.name: f.domain for f in arcpy.ListFields(self.pts)}["name"], "kinds")

    def test_describe_rasters_workspaces_and_tables(self):
        raster = arcpy.Describe(os.path.join(self.directory, "r.tif"))
        self.assertEqual((raster.dataType, raster.width, raster.height, raster.meanCellWidth, raster.bandCount),
                         ("RasterDataset", 2, 2, 10.0, 1))
        self.assertEqual(raster.spatialReference.factoryCode, 3857)
        self.assertEqual(arcpy.Describe(self.directory).dataType, "Folder")
        self.assertEqual(arcpy.Describe(os.path.join(self.directory, "multi.gpkg")).dataType, "Workspace")
        self.assertEqual(arcpy.Describe(os.path.join(self.directory, "t.gpkg")).dataType, "Table")
        self.assertEqual(arcpy.Describe(self.pts).dataType, "FeatureClass")

    def test_names(self):
        self.assertEqual(arcpy.AddFieldDelimiters(self.directory, "POP"), '"POP"')
        self.assertEqual(arcpy.ParseFieldName("parcels.owner"), ", , parcels, owner")
        self.assertEqual(arcpy.ParseTableName("parcels"), ", , parcels")
        self.assertEqual(arcpy.ValidateTableName("land use 2"), "land_use_2")
        self.assertEqual(arcpy.CreateScratchName("tmp", "", "FeatureClass", self.directory),
                         os.path.join(self.directory, "tmp0.gpkg"))
        open(os.path.join(self.directory, "tmp0.tif"), "w").close()
        self.assertEqual(arcpy.CreateScratchName("tmp", "", "RasterDataset", self.directory),
                         os.path.join(self.directory, "tmp1.tif"))
        self.assertEqual(arcpy.CreateScratchName(workspace=os.path.join(self.directory, "multi.gpkg")),
                         os.path.join(self.directory, "multi.gpkg") + "/xx0")

    def test_schema_lock(self):
        self.assertTrue(arcpy.TestSchemaLock(self.pts))
        holder = sqlite3.connect(self.pts, isolation_level=None)
        holder.execute("begin exclusive")
        try:
            self.assertFalse(arcpy.TestSchemaLock(self.pts))
        finally:
            holder.execute("rollback")
            holder.close()

    def test_feature_set_and_record_set(self):
        out = os.path.join(self.directory, "copy.gpkg")
        arcpy.FeatureSet(self.pts).save(out)
        self.assertEqual(arcpy.management.GetCount(out)[0], "2")
        chosen = arcpy.FeatureSet(self.pts, "name = 'b'")
        document = json.loads(chosen.JSON)
        self.assertEqual([f["geometry"] for f in document["features"]], [{"x": 3.0, "y": 4.0}])
        from_json = arcpy.FeatureSet(chosen.JSON)
        with arcpy.da.SearchCursor(from_json, ["name", "SHAPE@XY"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("b", (3.0, 4.0))])
        self.assertEqual(json.loads(arcpy.FeatureSet(self.pts).GeoJSON)["type"], "FeatureCollection")
        with self.assertRaisesRegex(arcpy.NotBuiltError, "network"):
            arcpy.FeatureSet("https://example.com/arcgis/rest/services/x/FeatureServer/0")
        table = arcpy.RecordSet(os.path.join(self.directory, "t.gpkg"))
        saved = table.save(os.path.join(self.directory, "t2.gpkg"))
        self.assertEqual(arcpy.Describe(saved).dataType, "Table")


class Values(unittest.TestCase):
    def setUp(self):
        _runtime.env._reset()
        _runtime.MESSAGES.clear()

    def tearDown(self):
        _runtime.env._reset()
        _runtime.MESSAGES.clear()

    def test_unit_conversion_factors(self):
        # The pages' own examples: 620.5 MilesInt is 998.597952 Kilometers; 10,000 Hectares are 100 SquareKilometers.
        self.assertAlmostEqual(620.5 * arcpy.LinearUnitConversionFactor("MilesInt", "Kilometers"), 998.597952)
        self.assertAlmostEqual(10000 * arcpy.ArealUnitConversionFactor("Hectares", "SquareKilometers"), 100.0)
        # A US survey foot is 1200/3937 m; a US survey acre is 43,560 US square feet.
        self.assertAlmostEqual(arcpy.LinearUnitConversionFactor("FeetUS", "Meters"), 1200 / 3937)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("AcresUS", "SquareFeetUS"), 43560.0)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("AcresInt", "SquareMeters"), 4046.8564224)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "NauticalMilesUS"):
            arcpy.LinearUnitConversionFactor("NauticalMilesUS", "Meters")

    def test_geometry_from_text_binary_json_and_geohash(self):
        point = arcpy.FromWKT("POINT (1 2)", 3857)
        self.assertEqual((type(point).__name__, point.firstPoint.X, point.spatialReference.factoryCode),
                         ("PointGeometry", 1.0, 3857))
        self.assertEqual(arcpy.FromWKB(square(0, 0).WKB).area, 100.0)
        geojson = arcpy.AsShape({"type": "LineString", "coordinates": [[0, 0], [3, 4]]})
        self.assertEqual((type(geojson).__name__, geojson.spatialReference.factoryCode), ("Polyline", 4326))
        esri = arcpy.AsShape({"x": 5.0, "y": 6.0, "spatialReference": {"wkid": 102100}}, True)
        self.assertEqual((esri.firstPoint.X, esri.spatialReference.factoryCode), (5.0, 3857))
        self.assertEqual(json.loads(square(0, 0).JSON)["spatialReference"], {"wkid": 3857})
        self.assertEqual(json.loads(arcpy.PointGeometry(arcpy.Point(1, 2), 3857).JSON),
                         {"x": 1.0, "y": 2.0, "spatialReference": {"wkid": 3857}})
        # Geohash "s0": s is 11000, 0 is 00000, read longitude first and alternating. s: lon [0,180] then lat [0,90],
        # lon [0,90], lat [0,45], lon [0,45]; 0: lat [0,22.5], lon [0,22.5], lat [0,11.25], lon [0,11.25], lat [0,5.625].
        extent = arcpy.FromGeohash("s0")
        self.assertEqual((extent.XMin, extent.YMin, extent.XMax, extent.YMax), (0.0, 0.0, 11.25, 5.625))
        self.assertEqual(extent.spatialReference.factoryCode, 4326)

    def test_from_coord_string(self):
        # The page's examples are one place: 34 03 25.90452N is 34 + 3/60 + 25.90452/3600 = 34.0571957, and
        # 117 11 47.29272W is −(117 + 11/60 + 47.29272/3600) = −117.1964702.
        for text, notation in (("34.05719570N 117.19647020W", "DD"), ("34 03.43174200N 117 11.78821200W", "DDM"),
                               ("34 03 25.90452000N 117 11 47.29272000W", "DMS")):
            point = arcpy.FromCoordString(text, notation)
            self.assertAlmostEqual(point.firstPoint.Y, 34.0571957, places=7)
            self.assertAlmostEqual(point.firstPoint.X, -117.1964702, places=7)
        # UTM zone 31's central meridian is 3°E; easting 500,000 at northing 0 (band N) is (3°E, 0°).
        utm = arcpy.FromCoordString("31N 500000 0", "UTM")
        self.assertAlmostEqual(utm.firstPoint.X, 3.0)
        self.assertAlmostEqual(utm.firstPoint.Y, 0.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "MGRS"):
            arcpy.FromCoordString("11SMT8186968515", "MGRS")

    def test_environments(self):
        self.assertEqual(arcpy.ListEnvironments("work*"), ["workspace"])
        self.assertEqual(arcpy.env["workspace"], arcpy.env.workspace)
        arcpy.env.overwriteOutput = False
        arcpy.ClearEnvironment("overwriteOutput")
        self.assertTrue(arcpy.env.overwriteOutput)
        before = arcpy.env.workspace
        with arcpy.EnvManager(workspace="elsewhere", extent="0 0 1 1"):
            self.assertEqual((arcpy.env.workspace, arcpy.env.extent), ("elsewhere", "0 0 1 1"))
        self.assertEqual((arcpy.env.workspace, arcpy.env.extent), (before, None))

        @arcpy.EnvManager(cellSize=5)
        def inside():
            return arcpy.env.cellSize
        self.assertEqual((inside(), arcpy.env.cellSize), (5, None))
        arcpy.env.cellSize = 9
        arcpy.ResetEnvironments()
        self.assertIsNone(arcpy.env.cellSize)
        self.assertEqual(arcpy.GetSystemEnvironment("TEMP"), os.environ.get("TEMP") or os.environ.get("TMP"))

    def test_messages_read_back(self):
        arcpy.AddMessage("a")
        arcpy.AddWarning("b")
        arcpy.AddError("c")
        self.assertEqual((arcpy.GetMessage(1), arcpy.GetSeverity(2), arcpy.GetMaxSeverity()), ("b", 2, 2))
        self.assertEqual((arcpy.GetReturnCode(0), arcpy.GetReturnCode(2)), (0, -2147467259))
        self.assertEqual(arcpy.GetAllMessages(), [[0, 0, "a"], [50, 0, "b"], [100, -2147467259, "c"]])
        arcpy.AddReturnMessage(1)
        self.assertEqual((arcpy.GetMessageCount(), arcpy.GetSeverity(3), arcpy.GetMessage(3)), (4, 1, "b"))
        self.assertEqual(arcpy.GetSeverityLevel(), 2)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "severity_level 0"):
            arcpy.SetSeverityLevel(0)
        self.assertEqual(arcpy.GetArgumentCount(), len(_runtime.PARAMETERS))

    def test_value_table_and_field_info(self):
        table = arcpy.ValueTable(2)
        table.addRow("'c:/temp/land use.shp' 2")
        table.addRow(["roads", 5])
        self.assertEqual((table.rowCount, table.columnCount, table.getValue(0, 0), table.getValue(1, 1)),
                         (2, 2, "c:/temp/land use.shp", "5"))
        self.assertEqual(table.exportToString(), "'c:/temp/land use.shp' 2;roads 5")
        again = arcpy.ValueTable()
        again.loadFromString(table.exportToString())
        self.assertEqual(again.getRow(0), "'c:/temp/land use.shp' 2")
        typed = arcpy.ValueTable(["GPString", "GPLong"])
        typed.addRow("a 7")
        self.assertEqual(typed.getTrueRow(0), ["a", 7])
        info = arcpy.FieldInfo()
        info.addField("POP", "population", "VISIBLE", "RATIO")
        info.addField("ID", "ID", "HIDDEN", "NONE")
        self.assertEqual((info.count, info.findFieldByName("id"), info.getNewName(0), info.getSplitRule(0)),
                         (2, 1, "population", "RATIO"))
        self.assertEqual(info.exportToString(), "POP population VISIBLE RATIO;ID ID HIDDEN NONE")

    def test_refused_names_say_why(self):
        with self.assertRaisesRegex(arcpy.NotBuiltError, "fenced"):
            arcpy.SignInToPortal("https://example.com")
        with self.assertRaisesRegex(arcpy.NotBuiltError, "application"):
            arcpy.RefreshLayer()


class GeometryMethods(unittest.TestCase):
    def test_measures_along_a_line(self):
        # (0,0) → (10,0) → (10,10): 20 long. (10,5) is 15 along it; a quarter of it is (5,0).
        bend = line((0, 0), (10, 0), (10, 10))
        self.assertEqual(bend.measureOnLine(arcpy.Point(10, 5)), 15.0)
        self.assertEqual(bend.measureOnLine(arcpy.Point(10, 5), True), 0.75)
        self.assertEqual(bend.positionAlongLine(15).firstPoint.Y, 5.0)
        self.assertEqual(bend.positionAlongLine(0.25, True).firstPoint.X, 5.0)
        self.assertEqual(bend.positionAlongLine(99).firstPoint.Y, 10.0)   # past the end: the end
        self.assertEqual(list(bend.segmentAlongLine(5, 15)._shape.coords), [(5, 0), (10, 0), (10, 5)])
        # (5,2) is left of the eastbound first segment, (5,−2) right of it; both 5 along and 2 away.
        near, along, away, right = bend.queryPointAndDistance(arcpy.Point(5, 2))
        self.assertEqual((near.firstPoint.X, near.firstPoint.Y, along, away, right), (5.0, 0.0, 5.0, 2.0, False))
        self.assertTrue(bend.queryPointAndDistance(arcpy.Point(5, -2))[3])
        self.assertEqual(bend.snapToLine(arcpy.Point(12, 7)).firstPoint.X, 10.0)
        self.assertEqual(bend.reverseOrientation().firstPoint.Y, 10.0)
        self.assertEqual(sorted(tuple(p.coords[0]) for p in bend.boundary()._shape.geoms), [(0, 0), (10, 10)])
        self.assertEqual([(p.X, p.Y) for p in bend.getPart(0)], [(0, 0), (10, 0), (10, 10)])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "geodesic"):
            bend.positionAlongLine(5, geodesic=True)

    def test_cut_move_rotate_scale(self):
        # A northbound line through x = 5 cuts the 10 square: west of it is left (area 50, centroid x 2.5).
        left, right = square(0, 0).cut(line((5, -1), (5, 11)))
        self.assertEqual((left.area, left.centroid.X, right.area, right.centroid.X), (50.0, 2.5, 50.0, 7.5))
        uncut = square(0, 0).cut(line((20, -1), (20, 11)))
        self.assertIsNone(uncut[0])
        self.assertEqual(uncut[1].area, 100.0)
        point = arcpy.PointGeometry(arcpy.Point(1, 0), 3857)
        self.assertEqual((point.move(2, 3).firstPoint.X, point.move(2, 3).firstPoint.Y), (3.0, 3.0))
        turned = point.rotate(arcpy.Point(0, 0), math.pi / 2)
        self.assertAlmostEqual(turned.firstPoint.X, 0.0)
        self.assertAlmostEqual(turned.firstPoint.Y, 1.0)
        self.assertEqual(point.scale(arcpy.Point(0, 0), 2, 1).firstPoint.X, 2.0)

    def test_lengths_areas_angles_and_distances(self):
        # One degree of longitude on the equator is a·π/180 = 6378137 · 0.0174532925… = 111319.4908 m on WGS 84.
        equator = line((0, 0), (1, 0), srid=4326)
        self.assertAlmostEqual(equator.getLength(), 6378137 * math.pi / 180, places=3)
        self.assertAlmostEqual(line((0, 0), (1000, 0)).getLength("PLANAR", "Kilometers"), 1.0)
        self.assertAlmostEqual(square(0, 0, 100).getArea("PLANAR", "Hectares"), 1.0)
        self.assertEqual(square(0, 0, 100).getArea("PLANAR"), 10000.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "LOXODROME"):
            equator.getLength("LOXODROME")
        origin = arcpy.PointGeometry(arcpy.Point(0, 0), 3857)
        angle, distance = origin.angleAndDistanceTo(arcpy.PointGeometry(arcpy.Point(3, 4), 3857), "PLANAR")
        self.assertAlmostEqual(angle, math.degrees(math.atan2(3, 4)))
        self.assertAlmostEqual(distance, 5.0)
        east = origin.pointFromAngleAndDistance(90, 10, "PLANAR")
        self.assertAlmostEqual(east.firstPoint.X, 10.0)
        self.assertAlmostEqual(east.firstPoint.Y, 0.0)
        # On WGS 84, due north from (0,0) to (0,1) is azimuth 0; due east along the equator by a·π/180 is 1° east.
        here = arcpy.PointGeometry(arcpy.Point(0, 0), 4326)
        self.assertAlmostEqual(here.angleAndDistanceTo(arcpy.PointGeometry(arcpy.Point(0, 1), 4326))[0], 0.0)
        moved = here.pointFromAngleAndDistance(90, 6378137 * math.pi / 180)
        self.assertAlmostEqual(moved.firstPoint.X, 1.0, places=9)
        self.assertAlmostEqual(moved.firstPoint.Y, 0.0, places=9)

    def test_densify_intersect_relations_and_projection(self):
        # Ten units at most four apart: three equal segments, four vertices.
        self.assertEqual(line((0, 0), (10, 0)).densify("DISTANCE", 4).pointCount, 4)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "ANGLE"):
            line((0, 0), (10, 0)).densify("ANGLE", 4)
        overlap = square(0, 0).intersect(square(5, 5), 4)
        self.assertEqual(overlap.area, 25.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "lower"):
            square(0, 0).intersect(square(5, 5), 1)
        edge = arcpy.PointGeometry(arcpy.Point(0, 5), 3857)
        self.assertFalse(square(0, 0).contains(edge))
        self.assertTrue(square(0, 0).contains(edge, "BOUNDARY"))
        self.assertTrue(square(0, 0).contains(square(0, 0, 5)))
        self.assertFalse(square(0, 0).contains(square(0, 0, 5), "PROPER"))
        self.assertTrue(square(0, 0, 5).within(square(0, 0)))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "transformation_name"):
            edge.projectAs(4326, "WGS_1984_(ITRF00)_To_NAD_1983")


class LegacyAndScriptTools(_Folder):
    def test_legacy_cursors_read_change_and_insert_rows(self):
        # pts holds a at (1,2) and b at (3,4). Sorted by name descending, b comes first.
        names = [row.getValue("name") for row in arcpy.SearchCursor(self.pts, sort_fields="name D")]
        self.assertEqual(names, ["b", "a"])
        cursor = arcpy.SearchCursor(self.pts, "name = 'a'", None, "name")
        row = cursor.next()
        self.assertEqual((row.name, row.Shape.firstPoint.X, row.isNull("name")), ("a", 1.0, False))
        self.assertIsNone(cursor.next())
        del cursor
        updater = arcpy.UpdateCursor(self.pts)
        for row in updater:
            if row.getValue("name") == "a":
                row.setValue("name", "A")
                updater.updateRow(row)
            else:
                updater.deleteRow(row)
        del updater
        inserter = arcpy.InsertCursor(self.pts)
        new = inserter.newRow()
        new.setValue("name", "c")
        new.Shape = arcpy.PointGeometry(arcpy.Point(5, 6), 3857)
        inserter.insertRow(new)
        del inserter
        with arcpy.da.SearchCursor(self.pts, ["name", "SHAPE@XY"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("A", (1.0, 2.0)), ("c", (5.0, 6.0))])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "sort_fields"):
            arcpy.UpdateCursor(self.pts, sort_fields="name")

    def test_script_tool_helpers(self):
        first, second = arcpy.Parameter("a", datatype="GPString"), arcpy.Parameter("b", datatype="GPString")
        first.value = "copied"
        _runtime.PARAMETERS[:] = [first, second]
        try:
            arcpy.CopyParameter("a", "b")
            self.assertEqual(second.value, "copied")
            second.value = None
            arcpy.CopyParameter(0, 1)
            self.assertEqual(second.value, "copied")
        finally:
            _runtime.PARAMETERS.clear()
        table = arcpy.CreateObject("ValueTable", 2)
        self.assertEqual((type(table).__name__, table.columnCount), ("ValueTable", 2))
        self.assertEqual(arcpy.CreateObject("point", 1, 2).Y, 2)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "ArcSDESQLExecute"):
            arcpy.CreateObject("ArcSDESQLExecute")
        with arcpy.SeverityLevel(2):
            self.assertEqual(arcpy.GetSeverityLevel(), 2)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "severity_level 1"):
            arcpy.SeverityLevel(1)
        self.assertTrue(arcpy.IsSynchronous("Buffer"))
        self.assertTrue(arcpy.Value("").isEmpty)
        self.assertIs(arcpy.SearchNeighborhoodStandardCircular, arcpy.ga.SearchNeighborhoodStandardCircular)

    def test_is_being_edited_and_feature_datasets_exist(self):
        self.assertFalse(arcpy.IsBeingEdited(self.pts))
        with arcpy.da.Editor(self.pts):
            self.assertTrue(arcpy.IsBeingEdited(self.pts))
        self.assertFalse(arcpy.IsBeingEdited(self.pts))
        multi = os.path.join(self.directory, "multi.gpkg")
        dataset = arcpy.management.CreateFeatureDataset(multi, "transport", 3857).getOutput(0)
        self.assertTrue(arcpy.Exists(dataset))
        self.assertFalse(arcpy.Exists(multi + "/nowhere"))

    def test_each_refused_name_says_why(self):
        for name, said in (("GetInstallInfo", "not ArcGIS"), ("ListTools", "toolbox catalogue"),
                           ("AIO", "network"), ("ListTransformations", "catalogue"), ("Multipatch", "z values"),
                           ("Annotation", "group 2"), ("Chart", "application"), ("ViewContext", "could not be found"),
                           ("NetCDFFileProperties", "netCDF"), ("Render", "symbology")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                getattr(arcpy, name)
        for name, said in (("SAR", "estimation"), ("BuildBalancedZones", "random"),
                           ("GenerateSpatialWeightsMatrix", ".swm"), ("TimeSeriesSmoothing", "field names")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                getattr(arcpy.stats, name)()
        for name, said in (("CreateOverpass", "symbolized"), ("SetControlPointByAngle", "control points"),
                           ("CalculateUTMZone", "projection-engine")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                getattr(arcpy.cartography, name)()


if __name__ == "__main__":
    unittest.main()
