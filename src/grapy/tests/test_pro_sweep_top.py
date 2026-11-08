"""The top-level arcpy names and arcpy.da, aligned to ArcGIS Pro 3.7 by the Pro sweep (tools/pro-compare/sweep, cases
arcpy_top and da). Each test pins an answer measured in ArcGIS Pro 3.7 on 2026-11-08 with an example small enough to
work by hand."""
import json
import math
import os
import shutil
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class _Workspace(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self._old = arcpy.env.workspace
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateSQLiteDatabase(os.path.join(self.directory, "store.gpkg"), "GEOPACKAGE")[0]

    def tearDown(self):
        arcpy.env.workspace = self._old
        shutil.rmtree(self.directory, ignore_errors=True)

    def points(self, name="points"):
        fc = arcpy.management.CreateFeatureclass(self.gpkg, name, "POINT", spatial_reference=32635)[0]
        arcpy.management.AddField(fc, "val", "DOUBLE")
        with arcpy.da.InsertCursor(fc, ["val", "SHAPE@XY"]) as rows:
            rows.insertRow((1.0, (500100.0, 4500100.0)))
            rows.insertRow((2.0, (500200.0, 4500300.0)))
        return fc


def _box(x0, y0, x1, y1, srid=32635):
    # Clockwise, as arcpy's own rings run.
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x0, y1), arcpy.Point(x1, y1),
                                      arcpy.Point(x1, y0), arcpy.Point(x0, y0)]), arcpy.SpatialReference(srid))


class GeometryAsPro(unittest.TestCase):
    def test_wkt_is_multi_and_ogc_oriented(self):
        # Pro: a clockwise square's WKT is MULTIPOLYGON (((0 0, 10 0, 10 10, 0 10, 0 0))), a hole runs clockwise, and a
        # polyline is a MULTILINESTRING.
        square = arcpy.FromWKT("POLYGON ((0 0, 0 10, 10 10, 10 0, 0 0), (2 2, 4 2, 4 4, 2 4, 2 2))")
        self.assertEqual(square.WKT, "MULTIPOLYGON (((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 2 4, 4 4, 4 2, 2 2)))")
        line = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(3, 4)]))
        self.assertEqual(line.WKT, "MULTILINESTRING ((0 0, 3 4))")
        self.assertEqual(arcpy.PointGeometry(arcpy.Point(1, 2)).WKT, "POINT (1 2)")
        self.assertFalse(square.hasCurves)

    def test_point_relations_and_array(self):
        self.assertTrue(arcpy.Point(1, 1).within(arcpy.Extent(0, 0, 2, 2)))
        self.assertTrue(arcpy.Point(1, 1).disjoint(arcpy.Point(2, 2)))
        a = arcpy.Array([arcpy.Point(1, 1), arcpy.Point(2, 2)])
        a.add(arcpy.Point(3, 3))
        a.append(arcpy.Point(4, 4))
        a.insert(0, arcpy.Point(0, 0))
        a.replace(1, arcpy.Point(10, 10))
        a.remove(4)  # an index, as Pro's: [0, 10, 2, 3]
        self.assertEqual([p.X for p in a], [0, 10, 2, 3])
        self.assertEqual(a.next().X, 0)

    def test_extent(self):
        e = arcpy.Extent(0, 0, 100, 50)
        self.assertEqual(str(e), "0 0 100 50 NaN NaN NaN NaN")
        self.assertEqual((e.upperRight.X, e.upperRight.Y), (100, 50))
        self.assertTrue(e.overlaps(arcpy.Extent(50, 25, 150, 75)))
        self.assertTrue(e.disjoint(arcpy.Extent(200, 200, 300, 300)))
        # The documented signature puts the reference ninth.
        utm = arcpy.Extent(500000, 4500000, 500600, 4500600, None, None, None, None, arcpy.SpatialReference(32635))
        self.assertEqual(utm.spatialReference.factoryCode, 32635)
        wgs = utm.projectAs(arcpy.SpatialReference(4326))
        self.assertAlmostEqual(wgs.XMin, 27.0, places=6)  # the zone's central meridian

    def test_densify_cuts_each_segment_shorter_than_the_distance(self):
        # Pro: a 300 m and a 400 m segment densified at 50 m make 17 points (7 and 9 pieces), not 15.
        line = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(300, 0), arcpy.Point(300, 400)]))
        self.assertEqual(line.densify("DISTANCE", 50).pointCount, 17)

    def test_centroid_of_lines_and_multipoints(self):
        line = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(300, 0), arcpy.Point(300, 400)]))
        self.assertEqual((line.centroid.X, line.centroid.Y), (300, 200))  # off-line true centroid: the label point
        self.assertEqual((round(line.trueCentroid.X, 4), round(line.trueCentroid.Y, 4)), (235.7143, 114.2857))
        roof = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(100, 100), arcpy.Point(200, 0)]))
        self.assertEqual((round(roof.labelPoint.X, 6), round(roof.labelPoint.Y, 6)), (75, 75))
        m = arcpy.Multipoint(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(10, 0), arcpy.Point(0, 10)]))
        self.assertEqual((m.centroid.X, m.centroid.Y), (5, 5))
        vt = arcpy.ValueTable(2)
        vt.addRow("a 1")
        self.assertEqual(vt.getRow(0), "'a' '1'")

    def test_hull_rectangle_label_point_intersect_getpart(self):
        square = _box(0, 0, 200, 200)
        self.assertEqual(square.hullRectangle, "0 200 200 200 200 0 0 0")
        triangle = arcpy.Polygon(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(100, 300), arcpy.Point(200, 0)]))
        p = triangle.labelPoint
        self.assertEqual((round(p.X, 6), round(p.Y, 6)), (100, 100))  # its centroid
        a = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(300, 0)]))
        b = arcpy.Polyline(arcpy.Array([arcpy.Point(150, -50), arcpy.Point(150, 50)]))
        self.assertEqual(a.intersect(b, 1).firstPoint.X, 150)
        m = arcpy.Multipoint(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(100, 0), arcpy.Point(100, 50)]))
        self.assertEqual(m.getPart(2).X, 100)
        self.assertIsInstance(m.getPart(2), arcpy.Point)

    def test_geometry_by_type_and_from_wkb(self):
        g = arcpy.Geometry("polyline", arcpy.Array([arcpy.Point(0, 0), arcpy.Point(30, 40)]),
                           arcpy.SpatialReference(32635))
        self.assertEqual((g.type, g.length), ("polyline", 50))
        import struct
        q = arcpy.FromWKB(bytearray(struct.pack("<BII", 1, 2, 2) + struct.pack("<4d", 0, 0, 3, 4)),
                          arcpy.SpatialReference(4326))
        self.assertEqual(q.spatialReference.factoryCode, 4326)


class CoordinatesAsPro(unittest.TestCase):
    def test_mgrs(self):
        # Pro: 35T NE 00000 83000 is on zone 35's central meridian, 27 degrees east, at 40.497705 north.
        g = arcpy.FromCoordString("35TNE0000083000", "MGRS")
        self.assertAlmostEqual(g.firstPoint.X, 27.0, places=9)
        self.assertAlmostEqual(g.firstPoint.Y, 40.497705, places=6)
        self.assertEqual(g.spatialReference.factoryCode, 4326)


class SpatialReferenceAsPro(unittest.TestCase):
    def test_utm(self):
        u = arcpy.SpatialReference(32635)
        self.assertEqual(u.name, "WGS_1984_UTM_Zone_35N")
        self.assertEqual(u.PCSName, "WGS_1984_UTM_Zone_35N")
        self.assertEqual(u.GCS.name, "GCS_WGS_1984")
        self.assertEqual((u.linearUnitName, u.linearUnitCode), ("Meter", 9001))
        self.assertEqual((u.falseEasting, u.centralMeridian, u.scaleFactor), (500000.0, 27.0, 0.9996))
        self.assertEqual(u.projectionName, "Transverse_Mercator")
        self.assertEqual((u.datumName, u.semiMajorAxis), ("", 0.0))  # a projected reference's are empty
        self.assertEqual((u.XYTolerance, u.XYResolution), (0.001, 0.0001))
        self.assertTrue(u.exportToString().startswith('PROJCS["WGS_1984_UTM_Zone_35N"'))

    def test_geographic(self):
        g = arcpy.SpatialReference(4326)
        self.assertEqual((g.name, g.datumName, g.spheroidName, g.datumCode), ("GCS_WGS_1984", "D_WGS_1984",
                                                                               "WGS_1984", 6326))
        self.assertEqual((g.angularUnitCode, g.primeMeridianName), (9102, "Greenwich"))
        self.assertAlmostEqual(g.XYTolerance, 0.001 / (6378137 * math.pi / 180), places=20)  # 8.983e-9
        self.assertEqual(g.XYResolution, 1e-9)


class NamesAsPro(_Workspace):
    def test_validate_names_in_a_geopackage(self):
        self.assertEqual(arcpy.ValidateFieldName("1st", self.gpkg), "F1st")
        self.assertEqual(arcpy.ValidateTableName("1st", self.gpkg), "T1st")
        self.assertEqual(arcpy.ValidateFieldName("select", self.gpkg), "select_")
        self.assertEqual(arcpy.ValidateFieldName("sélect", self.gpkg), "sélect")
        self.assertEqual(arcpy.ValidateFieldName("a-b", self.gpkg), "a_b")
        self.assertEqual(arcpy.ValidateFieldName("", self.gpkg), "")

    def test_validate_names_in_a_folder(self):
        self.assertEqual(arcpy.ValidateFieldName("1st", self.directory), "a1st")
        self.assertEqual(arcpy.ValidateTableName("1st", self.directory), "1st")
        self.assertEqual(arcpy.ValidateTableName("a-b", self.directory), "a-b")
        self.assertEqual(arcpy.ValidateTableName("has space", self.directory), "has_space")

    def test_parse_names_in_a_geopackage(self):
        self.assertEqual(arcpy.ParseFieldName("val", self.gpkg), ", , , val")
        self.assertEqual(arcpy.ParseTableName("points", self.gpkg), ", , points")
        with self.assertRaises(RuntimeError):  # Pro: "Invalid parameter value passed to function"
            arcpy.ParseTableName("db.own.tab", self.gpkg)

    def test_export_features_follows_the_field_mapping(self):
        fc = self.points()
        arcpy.management.AddField(fc, "cat", "TEXT")
        mappings = arcpy.FieldMappings()
        mappings.addTable(fc)
        mappings.removeFieldMap(mappings.findFieldMapIndex("cat"))
        out = arcpy.conversion.ExportFeatures(fc, f"{self.gpkg}/out", field_mapping=mappings)[0]
        self.assertNotIn("cat", [f.name for f in arcpy.ListFields(out)])
        self.assertIn("val", [f.name for f in arcpy.ListFields(out)])

    def test_unique_scratch_and_exists(self):
        self.points()
        self.assertTrue(arcpy.CreateUniqueName("points", self.gpkg).endswith("points0"))
        self.assertTrue(arcpy.CreateUniqueName("other", self.gpkg).endswith("other"))
        name = os.path.basename(arcpy.CreateScratchName("ras", ".tif", "RasterDataset", self.directory))
        self.assertTrue(name.startswith("ras") and name.endswith(".tif") and not name.endswith(".tif.tif"), name)
        open(os.path.join(self.directory, "dem.txt"), "w").close()
        self.assertTrue(arcpy.Exists("dem.txt"))  # found in env.workspace

    def test_wildcards_take_only_the_asterisk(self):
        for name in ("dem.txt", "zone.txt"):
            open(os.path.join(self.directory, name), "w").close()
        self.assertEqual(arcpy.ListFiles("d*"), ["dem.txt"])
        self.assertEqual(arcpy.ListFiles("[dz]*"), [])
        self.assertEqual(arcpy.ListFiles("de?.txt"), [])

    def test_licences(self):
        self.assertEqual(arcpy.CheckProduct("ArcInfo"), "Available")
        self.assertEqual(arcpy.CheckProduct("ArcView"), "NotLicensed")
        self.assertEqual(arcpy.CheckProduct("Nonsense"), "")
        self.assertEqual(arcpy.CheckExtension("Spatial"), "Available")
        self.assertEqual(arcpy.CheckExtension("Nonsense"), "Unrecognized extension code Nonsense")


class DescribeAsPro(_Workspace):
    def test_geopackage_feature_class(self):
        fc = self.points()
        d = arcpy.Describe(fc)
        self.assertEqual((d.aliasName, d.extension, d.hasSpatialIndex, d.canVersion, d.isVersioned),
                         ("main.points", "", True, False, False))
        arcpy.AlterAliasName(fc, "Sweep points")
        self.assertEqual(arcpy.Describe(fc).aliasName, "Sweep points")
        dd = arcpy.da.Describe(fc)
        self.assertEqual((dd["areaFieldName"], dd["lengthFieldName"]), ("", ""))

    def test_workspace_and_datasets(self):
        self.points("a")
        self.points("b")
        w = arcpy.Describe(self.gpkg)
        self.assertEqual(w.workspaceFactoryProgID, "esriDataSourcesGDB.SqlWorkspaceFactory")
        arcpy.management.CreateFeatureDataset(self.gpkg, "fd", arcpy.SpatialReference(32635))
        arcpy.env.workspace = self.gpkg
        self.assertEqual(arcpy.ListDatasets(), ["fd"])
        self.assertEqual(arcpy.ListDatasets("*", "Feature"), ["fd"])

    def test_walk_feature_dataset_is_a_workspace(self):
        arcpy.management.CreateFeatureDataset(self.gpkg, "fd", arcpy.SpatialReference(32635))
        line = arcpy.management.CreateFeatureclass(f"{self.gpkg}/fd", "lns", "POLYLINE", spatial_reference=32635)[0]
        self.assertTrue(arcpy.Exists(line))
        self.points("pts")
        walked = [(os.path.basename(d), n, sorted(f)) for d, n, f in arcpy.da.Walk(self.gpkg)]
        self.assertEqual(walked, [("store.gpkg", ["fd"], ["pts"]), ("fd", [], ["lns"])])


class ParametersAsPro(unittest.TestCase):
    def test_outside_a_tool_they_are_the_scripts_arguments(self):
        saved = sys.argv
        sys.argv = ["script.py", "first value", "5", "#"]
        try:
            self.assertEqual(arcpy.GetParameterAsText(0), "first value")
            self.assertEqual(arcpy.GetParameter(1), "5")
            self.assertEqual(arcpy.GetArgumentCount(), 3)
            self.assertIsNone(arcpy.SetParameter(0, "x"))
            self.assertIsNone(arcpy.SetParameterAsText(0, "x"))
            self.assertIsNone(arcpy.CopyParameter(0, 1))
        finally:
            sys.argv = saved

    def test_parameter_count_of_a_tool(self):
        self.assertEqual(arcpy.GetParameterCount("Buffer_analysis"), 8)
        self.assertEqual(arcpy.GetParameterCount("analysis.Clip"), 4)

    def test_blank_field_filter_and_datatype_names(self):
        f = arcpy.Field()
        self.assertEqual((f.type, f.length, f.aliasName), ("", 0, ""))
        p = arcpy.Parameter("d", "D", "Input", "GPLinearUnit", "Optional")
        self.assertEqual(p.datatype, "Linear Unit")
        self.assertEqual(p.describe()["datatype"], "GPLinearUnit")  # the server's task resource keeps the keyword
        self.assertEqual((p.filter.type, p.filter.list), ("ValueList", []))

    def test_severity_level_block_puts_the_level_back(self):
        before = arcpy.GetSeverityLevel()
        with arcpy.SeverityLevel(2):
            self.assertEqual(arcpy.GetSeverityLevel(), 2)
        self.assertEqual(arcpy.GetSeverityLevel(), before)

    def test_tools_named_with_the_3d_alias(self):
        self.assertIn("Buffer3D_3d", arcpy.ListTools("Buffer3D*"))
        self.assertIn("3D Analyst Tools(3d)", arcpy.ListToolboxes("3D*"))

    def test_search_neighbourhood_strings(self):
        s = arcpy.SearchNeighborhoodStandard(300, 200, 45, 10, 3, "FOUR_SECTORS")
        self.assertEqual(str(s), "NBRTYPE=Standard S_MAJOR=300 S_MINOR=200 ANGLE=45 NBR_MAX=10 NBR_MIN=3 "
                                 "SECTOR_TYPE=FOUR_SECTORS")
        self.assertEqual(str(arcpy.SearchNeighborhoodSmoothCircular(250, 0.4)),
                         "NBRTYPE=SmoothCircular RADIUS=250 SMOOTH_FACTOR=0.4")
        self.assertEqual(arcpy.SearchNeighborhoodStandard3D(300, 10, 3, "FOUR_SECTORS").nbrType, "Standard3D")


class RastersAsPro(_Workspace):
    def test_numpy_round_trip(self):
        a = np.arange(12, dtype="float32").reshape(3, 4)
        a[1, 1] = -1
        r = arcpy.NumPyArrayToRaster(a, arcpy.Point(0, 0), 5, 5, -1)
        self.assertEqual(r.noDataValue, -1.0)
        path = os.path.join(self.directory, "r.tif")
        r.save(path)
        saved = arcpy.Raster(path)
        self.assertEqual(saved.format, "TIFF")
        self.assertEqual(saved.compressionType, "None")
        # The sample standard deviation: 11 cells 0..11 without 5, n - 1 in the divisor.
        values = np.delete(np.arange(12, dtype=float), 5)
        self.assertAlmostEqual(saved.standardDeviation, float(values.std(ddof=1)), places=5)
        # A window: from the cell holding (5, 0), 2 columns, 1 row up — the bottom row's second and third cells.
        window = arcpy.RasterToNumPyArray(saved, arcpy.Point(5, 0), 2, 1)
        self.assertEqual(window.dtype, np.float32)
        self.assertEqual(window.tolist(), [[9.0, 10.0]])

    def test_netcdf_coordinate_variables_and_time(self):
        from scipy.io import netcdf_file
        path = os.path.join(self.directory, "t.nc")
        with netcdf_file(path, "w") as nc:
            nc.createDimension("time", 2)
            t = nc.createVariable("time", "f8", ("time",))
            t[:] = [0.0, 1.0]
            t.units = "days since 2000-01-01"
            v = nc.createVariable("temp", "f4", ("time",))
            v[:] = [1.0, 2.0]
        p = arcpy.NetCDFFileProperties(path)
        self.assertEqual(sorted(p.getVariables()), ["temp", "time"])
        self.assertEqual(sorted(p.getVariablesByDimension("time")), ["temp", "time"])
        self.assertEqual(p.getFieldType("time"), "Date")


class CursorsAsPro(_Workspace):
    def test_projected_tokens_and_star(self):
        fc = self.points()
        with arcpy.da.SearchCursor(fc, ["SHAPE@XY"], spatial_reference=arcpy.SpatialReference(4326)) as rows:
            x, y = next(rows)[0]
        self.assertAlmostEqual(x, 27.0011828, places=6)  # Pro's answer for (500100, 4500100) in zone 35N
        self.assertAlmostEqual(y, 40.6517574, places=6)
        with arcpy.da.SearchCursor(fc, "*") as rows:
            self.assertEqual(rows.fields, ("fid", "geom", "val"))  # the table's own names, in its order
            first = next(rows)
        self.assertEqual(first[1], (500100.0, 4500100.0))  # the shape as SHAPE@XY
        with arcpy.da.SearchCursor(fc, ["SHAPE@WKT"]) as rows:
            self.assertEqual(next(rows)[0], "POINT (500100 4500100)")

    def test_legacy_cursor_releases_its_lock_on_del(self):
        fc = self.points()
        cursor = arcpy.UpdateCursor(fc)
        row = cursor.next()
        row.setValue("val", 9.0)
        cursor.updateRow(row)
        del row, cursor
        self.assertTrue(arcpy.TestSchemaLock(fc))

    def test_copy_features_into_a_geometry_list(self):
        fc = self.points()
        shapes = arcpy.management.CopyFeatures(fc, arcpy.Geometry())
        self.assertEqual([s.firstPoint.X for s in shapes], [500100.0, 500200.0])

    def test_record_set_json_has_no_geometry(self):
        table = arcpy.management.CreateTable(self.gpkg, "rows")[0]
        arcpy.management.AddField(table, "n", "LONG")
        with arcpy.da.InsertCursor(table, ["n"]) as rows:
            rows.insertRow((1,))
        self.assertNotIn("geometryType", json.loads(arcpy.RecordSet(table).JSON))
        self.assertTrue(arcpy.FeatureSet().JSON)


class DomainsAsPro(_Workspace):
    def test_range_domain_has_no_coded_values_and_contingent_subtype_zero(self):
        arcpy.management.CreateDomain(self.gpkg, "speed", "Speed", "DOUBLE", "RANGE")
        arcpy.management.SetValueForRangeDomain(self.gpkg, "speed", 0, 130)
        arcpy.management.CreateDomain(self.gpkg, "kinds", "k", "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(self.gpkg, "kinds", 1, "a")
        found = {d.name: d for d in arcpy.da.ListDomains(self.gpkg)}
        self.assertIsNone(found["speed"].codedValues)
        self.assertEqual(found["kinds"].codedValues, {1: "a"})
        table = arcpy.management.CreateTable(self.gpkg, "t")[0]
        arcpy.management.AddField(table, "kcode", "SHORT")
        arcpy.management.AddField(table, "speed", "DOUBLE")
        arcpy.management.AssignDomainToField(table, "kcode", "kinds")
        arcpy.management.AssignDomainToField(table, "speed", "speed")
        arcpy.management.CreateFieldGroup(table, "grp", ["kcode", "speed"])
        arcpy.management.AddContingentValue(table, "grp", [["kcode", "CODED_VALUE", 1], ["speed", "RANGE", "50;130"]])
        self.assertEqual(arcpy.da.ListContingentValues(table)[0].subtype, 0)


if __name__ == "__main__":
    unittest.main()
