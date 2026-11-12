"""The names of arcpy, arcpy.da, arcpy.management, arcpy.analysis and arcpy.conversion no test had called — ADR-224.

Each is checked against what its page documents, worked out by hand. Several are the older or pairwise names of a
tool tested elsewhere (DeleteRows, TableSelect, the Pairwise overlay tools, FeatureClassToFeatureClass,
TableToTable); those are checked to give the newer tool's answer, and that their own parameters are not dropped.
"""
import json
import math
import os
import sys
import shutil
import tempfile
import unittest
import warnings

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _ortho, _runtime  # noqa: E402
import test_attribute_rules  # noqa: E402
import test_ortho  # noqa: E402
import test_topology  # noqa: E402
import test_versions  # noqa: E402


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


def square(left, bottom, size):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(left, bottom), arcpy.Point(left, bottom + size),
                                      arcpy.Point(left + size, bottom + size), arcpy.Point(left + size, bottom)]), 3857)


class _Folder(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        _runtime.env._reset()
        arcpy.env.workspace = self.directory
        _runtime.MESSAGES.clear()

    def tearDown(self):
        _runtime.env._reset()
        _runtime.MESSAGES.clear()
        _runtime.PARAMETERS.clear()

    def features(self, name, shape, rows, fields=()):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def read(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as rows:
            return [tuple(r) for r in rows]


# ---- arcpy


class Runtime(_Folder):
    def test_add_id_message_takes_its_severity_from_the_type(self):
        arcpy.AddIDMessage("ERROR", 12, "roads")
        arcpy.AddIDMessage("WARNING", 345)
        arcpy.AddIDMessage("INFORMATIVE", 7, "a", "b")
        # GetMessages(2) answers errors only, (1) warnings and errors, (0) all, as documented.
        self.assertEqual(arcpy.GetMessages(2), "12 roads")
        self.assertEqual(arcpy.GetMessages(1), "12 roads\n345")
        self.assertEqual(arcpy.GetMessages(0).splitlines()[-1], "7 a b")

    def test_licences_are_always_there_on_the_server(self):
        # A toolbox written for Pro checks out an extension before using it; on this server every tool grapy builds is
        # available, so each check answers what arcpy answers when the licence is there.
        self.assertEqual(arcpy.CheckProduct("ArcInfo"), "Available")
        self.assertEqual(arcpy.CheckExtension("Spatial"), "Available")
        self.assertEqual(arcpy.CheckOutExtension("Spatial"), "CheckedOut")
        self.assertEqual(arcpy.CheckInExtension("Spatial"), "CheckedIn")

    def test_create_unique_name_appends_a_number_until_the_name_is_free(self):
        self.assertEqual(arcpy.CreateUniqueName("out.gpkg"), os.path.join(self.directory, "out.gpkg"))
        open(os.path.join(self.directory, "out.gpkg"), "w").close()
        self.assertEqual(arcpy.CreateUniqueName("out.gpkg"), os.path.join(self.directory, "out0.gpkg"))
        open(os.path.join(self.directory, "out0.gpkg"), "w").close()
        self.assertEqual(arcpy.CreateUniqueName("out.gpkg"), os.path.join(self.directory, "out1.gpkg"))
        other = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, other, True)
        self.assertEqual(arcpy.CreateUniqueName("out.gpkg", other), os.path.join(other, "out.gpkg"))

    def test_validate_field_name_replaces_invalid_characters_with_an_underscore(self):
        self.assertEqual(arcpy.ValidateFieldName("land use%"), "land_use_")
        self.assertEqual(arcpy.ValidateFieldName("ok_name"), "ok_name")
        # A name may not begin with a digit.
        self.assertEqual(arcpy.ValidateFieldName("2020pop"), "_2020pop")

    def test_parameters_by_index_as_value_and_as_text(self):
        first = arcpy.Parameter(name="distance", datatype="GPDouble", parameterType="Required", direction="Input")
        first.value = 12.5
        second = arcpy.Parameter(name="out", datatype="GPString", parameterType="Derived", direction="Output")
        _runtime.PARAMETERS[:] = [first, second]
        self.assertEqual(arcpy.GetParameterCount(), 2)
        self.assertEqual(arcpy.GetParameter(0), 12.5)
        self.assertEqual(arcpy.GetParameterAsText(0), "12.5")
        # An index past the last parameter answers nothing, not an error.
        self.assertIsNone(arcpy.GetParameter(5))
        self.assertEqual(arcpy.GetParameterAsText(5), "")
        arcpy.SetParameter(1, 3)
        self.assertEqual(arcpy.GetParameter(1), 3)
        arcpy.SetParameterAsText(1, "done")
        self.assertEqual(arcpy.GetParameterAsText(1), "done")

    def test_the_progressor_is_accepted_and_draws_nothing(self):
        # A server job has no progress dialog; a tool written for Pro must still run unchanged.
        self.assertIsNone(arcpy.SetProgressor("step", "Working…", 0, 10, 1))
        self.assertIsNone(arcpy.SetProgressorLabel("half way"))
        self.assertIsNone(arcpy.SetProgressorPosition(5))
        self.assertIsNone(arcpy.ResetProgressor())
        self.assertEqual(arcpy.GetMessageCount(), 0)

    def test_result_answers_its_outputs(self):
        result = arcpy.Result("a.gpkg", 3)
        self.assertEqual((result.outputCount, result.status), (2, 4))  # 4: succeeded, as documented
        self.assertEqual((result.getOutput(0), result[1], str(result)), ("a.gpkg", 3, "a.gpkg"))
        # A tool answers one.
        made = arcpy.management.CreateFeatureclass(self.directory, "made.gpkg", "POINT", spatial_reference=3857)
        self.assertIsInstance(made, arcpy.Result)
        self.assertTrue(os.path.exists(made.getOutput(0)))

    def test_the_legacy_cursor_answers_rows_then_none(self):
        path = self.features("pts", "POINT", [(1, point(0, 0)), (2, point(1, 1))], [("n", "LONG")])
        cursor = arcpy.SearchCursor(path)
        self.assertIsInstance(cursor, arcpy.Cursor)
        self.assertEqual(cursor.next().getValue("n"), 1)
        self.assertEqual(cursor.next().getValue("n"), 2)
        self.assertIsNone(cursor.next())
        self.assertEqual([row.getValue("n") for row in arcpy.SearchCursor(path)], [1, 2])
        with self.assertRaises(RuntimeError):
            cursor.insertRow(None)  # a search cursor does not insert


# ---- arcpy.da


class ContingentAndSequences(test_attribute_rules._Workspace):
    def test_contingent_values_are_answered_as_their_classes(self):
        arcpy.management.CreateDomain(self.gpkg, "kinds", "kinds", "SHORT", "CODED")
        for code, name in ((1, "House"), (2, "Shop")):
            arcpy.management.AddCodedValueToDomain(self.gpkg, "kinds", code, name)
        arcpy.management.AssignDomainToField(self.parcels, "kind", "kinds")
        arcpy.management.CreateFieldGroup(self.parcels, "use", ["kind", "value"])
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 1], ["value", "RANGE", "0;150"]])
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 2]])
        found = arcpy.da.ListContingentValues(self.parcels)
        self.assertTrue(all(isinstance(c, arcpy.da.ContingentValue) for c in found))
        first, second = found
        self.assertEqual((first.fieldGroupName, first.subtype, first.isRetired), ("use", None, False))
        kind, value = first.values
        self.assertIsInstance(kind, arcpy.da.ContingentFieldValue)
        self.assertEqual((kind.name, kind.type, kind.code), ("kind", "CODED_VALUE", 1))
        self.assertEqual((value.name, value.type, [float(v) for v in value.range]), ("value", "RANGE", [0.0, 150.0]))
        # A field the contingent value does not name is ANY.
        self.assertEqual([(v.name, v.type) for v in second.values], [("kind", "CODED_VALUE"), ("value", "ANY")])

    def test_database_sequences_are_answered_as_their_class(self):
        arcpy.management.CreateDatabaseSequence(self.gpkg, "ids", 100, 5)
        (sequence,) = arcpy.da.ListDatabaseSequences(self.gpkg)
        self.assertIsInstance(sequence, arcpy.da.DatabaseSequence)
        # Before any value is drawn the current value is the start (INFERRED, ADR-235 §5.8).
        self.assertEqual((sequence.name, sequence.startValue, sequence.incrementValue, sequence.currentValue),
                         ("ids", 100, 5, 100))


# ---- arcpy.management


class DeleteRows(_Folder):
    def test_delete_rows_deletes_every_row_or_a_layers_selection(self):
        table = self.features("t", "POINT", [(n, point(n, 0)) for n in range(1, 6)], [("n", "LONG")])
        layer = arcpy.management.MakeFeatureLayer(table, "small", "n <= 2").getOutput(0)
        arcpy.management.DeleteRows(layer)
        self.assertEqual(self.read(table, ["n"]), [(3,), (4,), (5,)])
        arcpy.management.DeleteRows(table)
        self.assertEqual(self.read(table, ["n"]), [])


class Pyramids(_Folder):
    def test_batch_build_pyramids_builds_each_and_answers_true(self):
        import rasterio
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        paths = []
        for name, size in (("a.tif", 600), ("b.tif", 1100)):
            path = os.path.join(self.directory, name)
            arcpy.NumPyArrayToRaster((np.arange(size * size).reshape(size, size) % 7).astype(np.int32),
                                     arcpy.Point(0, 0), 1, 1).save(path)
            paths.append(path)
        result = arcpy.management.BatchBuildPyramids(";".join(paths))
        self.assertEqual(result.getOutput(0), "true")
        # Each level halves the last until the longer side is no more than 256: 600 → 300, 150; 1100 → 550, 275, 137.
        with rasterio.open(paths[0]) as a, rasterio.open(paths[1]) as b:
            self.assertEqual((a.overviews(1), b.overviews(1)), ([2, 4], [2, 4, 8]))
        self.assertEqual(int(arcpy.Raster(paths[1])._array[1099, 1099]), (1100 * 1100 - 1) % 7)


class Versions(unittest.TestCase):
    setUp = test_versions.Versions.setUp
    tearDown = test_versions.Versions.tearDown
    rows = test_versions.Versions.rows
    set_value = test_versions.Versions.set_value
    diverge = test_versions.Versions.diverge

    def test_alter_version_renames_describes_and_changes_access(self):
        arcpy.management.RegisterAsVersioned(self.gpkg)
        arcpy.management.CreateVersion(self.gpkg, "DEFAULT", "edit", "PUBLIC", "first")
        arcpy.management.AlterVersion(self.gpkg, "edit", "fixes", "parcel fixes", "PROTECTED")
        versions = {v.name: v for v in arcpy.da.ListVersions(self.gpkg)}
        self.assertNotIn("edit", versions)
        self.assertEqual((versions["fixes"].description, versions["fixes"].access), ("parcel fixes", "Protected"))
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.AlterVersion(self.gpkg, "DEFAULT", "main")  # DEFAULT cannot be renamed
        with self.assertRaises(ValueError):
            arcpy.management.AlterVersion(self.gpkg, "fixes", access="SECRET")

    def test_reconcile_version_favours_the_target_by_default(self):
        # The diverged parcels of test_versions. BY_OBJECT both rows conflict, and this tool's own default resolution,
        # FAVOR_TARGET_VERSION, keeps DEFAULT's whole row each time: A (a, 20), B (b, 30).
        self.diverge()
        arcpy.management.ReconcileVersion(self.gpkg, "edit", "DEFAULT", post="POST")
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 20), (2, "b", 30)])

    def test_reconcile_version_by_attribute_merges_what_does_not_conflict(self):
        self.diverge()
        arcpy.management.ReconcileVersion(self.gpkg, "edit", "DEFAULT", "BY_ATTRIBUTE", post="POST")
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 20), (2, "bb", 30)])


class ClusterTolerance(unittest.TestCase):
    setUp = test_topology.Topology.setUp
    make = test_topology.Topology.make

    def tolerance(self, name):
        import sqlite3
        connection = sqlite3.connect(self.gpkg)
        try:
            return connection.execute("select cluster_tolerance from grapy_topology where name = ?", (name,)).fetchone()[0]
        finally:
            connection.close()

    def test_set_cluster_tolerance_changes_what_validation_snaps(self):
        near = self.make("near", "POLYLINE", ["LINESTRING(10.3 0.2,10.3 5)"])
        topo = arcpy.management.CreateTopology(self.fds, "snap", 0.1)[0]
        arcpy.management.AddFeatureClassToTopology(topo, self.parcels, 1, 1)
        arcpy.management.AddFeatureClassToTopology(topo, near, 2, 1)
        # (10.3, 0.2) lies 0.36 from the parcels' corner (10, 0): outside 0.1, inside 0.5.
        arcpy.management.SetClusterTolerance(topo, 0.5)
        self.assertEqual(self.tolerance("snap"), 0.5)
        arcpy.management.ValidateTopology(topo)
        with arcpy.da.SearchCursor(near, ["SHAPE@WKT"]) as rows:
            self.assertEqual(next(rows)[0], "LINESTRING (10 0, 10.3 5)")
        # 0 gives the default back, as documented.
        arcpy.management.SetClusterTolerance(topo, 0)
        self.assertGreater(self.tolerance("snap"), 0)
        self.assertLess(self.tolerance("snap"), 0.5)


class ControlPoints(unittest.TestCase):
    """ComputeControlPoints on the rendered photographs of test_ortho, with an orthophoto of the same ground as the
    reference: each control point must land in each image where the true orientation projects its ground point."""

    setUpClass = classmethod(test_ortho.Ortho.setUpClass.__func__)
    setUp = test_ortho.Ortho.setUp
    tearDown = test_ortho.Ortho.tearDown
    frames = test_ortho.Ortho.frames
    mosaic = test_ortho.Ortho.mosaic

    def orthophoto(self):
        import rasterio
        from rasterio.transform import from_origin
        cell, left, top, size = 0.02, -2.0, 7.0, 450
        xs = left + cell * (np.arange(size) + 0.5)
        ys = top - cell * (np.arange(size) + 0.5)
        gx, gy = np.meshgrid(xs, ys)
        image = np.round(test_ortho.ground(gx, gy)).astype(np.uint8)
        path = os.path.join(self.directory, "ortho.tif")
        with rasterio.open(path, "w", driver="GTiff", width=size, height=size, count=1, dtype="uint8", crs="EPSG:3857",
                           transform=from_origin(left, top, cell, cell)) as out:
            out.write(image, 1)
        return path

    def test_control_points_land_where_the_true_orientation_projects_them(self):
        md = self.mosaic()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            out = arcpy.management.ComputeControlPoints(md, self.orthophoto(), os.path.join(self.gpkg, "gcp"),
                                                        "MEDIUM", None, "MEDIUM", "REGULAR", None, "HIGH")[0]
        records = _ortho.read_points(out)
        self.assertGreater(len(records), 10)
        models = {i: test_ortho.model(v) for i, v in test_ortho.TRUTH.items()}
        worst = 0.0
        for r in records:
            self.assertEqual(r["Type"], _ortho.GCP)
            x, y, _ = r["xyz"]
            c, row = models[r["ImageID"]].project(np.array([x]), np.array([y]), np.array([0.0]))
            worst = max(worst, math.hypot(c[0] - r["ImageX"], row[0] - r["ImageY"]))
        self.assertLess(worst, 1.0)
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.ComputeControlPoints(md, self.orthophoto(), os.path.join(self.gpkg, "gcp2"),
                                                  out_image_feature_points=os.path.join(self.gpkg, "fp"))


# ---- arcpy.analysis


class Pairwise(_Folder):
    def setUp(self):
        super().setUp()
        self.squares = self.features("sq", "POLYGON", [(1, "a", square(0, 0, 10)), (2, "a", square(20, 0, 10)),
                                                       (3, "b", square(40, 0, 10))],
                                     [("n", "LONG"), ("kind", "TEXT")])
        self.cutter = self.features("cut", "POLYGON", [(square(5, -5, 20),)])

    def area(self, path):
        return sorted(round(r[0], 6) for r in self.read(path, ["SHAPE@AREA"]))

    def test_pairwise_clip_and_erase_answer_clip_and_erase(self):
        # The cutter 5..25 × −5..15 takes 5 × 10 of the first square and 5 × 10 of the second; the third is outside.
        clipped = arcpy.analysis.PairwiseClip(self.squares, self.cutter, os.path.join(self.directory, "c.gpkg"))[0]
        self.assertEqual(self.area(clipped), [50.0, 50.0])
        erased = arcpy.analysis.PairwiseErase(self.squares, self.cutter, os.path.join(self.directory, "e.gpkg"))[0]
        self.assertEqual(self.area(erased), [50.0, 50.0, 100.0])
        self.assertEqual(sorted(self.read(erased, ["n"])), [(1,), (2,), (3,)])

    def test_pairwise_buffer_dissolves_as_documented(self):
        pts = self.features("p", "POINT", [("a", point(0, 0)), ("a", point(100, 0)), ("b", point(200, 0))],
                            [("kind", "TEXT")])
        each = arcpy.analysis.PairwiseBuffer(pts, os.path.join(self.directory, "b1.gpkg"), "10 Meters")[0]
        self.assertEqual(len(self.read(each, ["kind"])), 3)
        # LIST: one shape for each kind, with only the listed field — two discs for "a", one for "b".
        listed = arcpy.analysis.PairwiseBuffer(pts, os.path.join(self.directory, "b2.gpkg"), "10 Meters", "LIST",
                                               "kind")[0]
        got = {kind: (shape.partCount, round(shape.area / (math.pi * 100), 2))
               for kind, shape in self.read(listed, ["kind", "SHAPE@"])}
        self.assertEqual(got, {"a": (2, 2.0), "b": (1, 1.0)})
        self.assertNotIn("n", [f.name for f in arcpy.ListFields(listed)])
        everything = arcpy.analysis.PairwiseBuffer(pts, os.path.join(self.directory, "b3.gpkg"), "10 Meters", "ALL")[0]
        self.assertEqual(len(self.read(everything, ["OID@"])), 1)

    def test_a_geodesic_buffer_is_refused_rather_than_answered_planar(self):
        # Found while writing this test: method was accepted and ignored, so a GEODESIC request on Web Mercator data
        # answered a planar disc — at 60° N twice the geodesic size. It is refused by name now, in each buffer tool.
        pts = self.features("g", "POINT", [("a", point(0, 8399737.89))], [("kind", "TEXT")])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.PairwiseBuffer(pts, os.path.join(self.directory, "g1.gpkg"), "10 Meters", method="GEODESIC")
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.Buffer(pts, os.path.join(self.directory, "g2.gpkg"), "10 Meters", method="GEODESIC")
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.MultipleRingBuffer(pts, os.path.join(self.directory, "g3.gpkg"), [10, 20], "Meters",
                                              method="GEODESIC")
        planar = arcpy.analysis.PairwiseBuffer(pts, os.path.join(self.directory, "g4.gpkg"), "10 Meters",
                                               method="PLANAR")[0]
        self.assertEqual(len(self.read(planar, ["OID@"])), 1)

    def test_table_select_answers_select(self):
        chosen = arcpy.analysis.TableSelect(self.squares, os.path.join(self.directory, "s.gpkg"), "kind = 'a'")[0]
        self.assertEqual(sorted(self.read(chosen, ["n"])), [(1,), (2,)])


# ---- arcpy.conversion


class OlderConversions(_Folder):
    def setUp(self):
        super().setUp()
        self.squares = self.features("sq", "POLYGON", [(1, square(0, 0, 10)), (2, square(10, 0, 10)),
                                                       (3, square(0, 10, 10))], [("n", "LONG")])

    def test_feature_class_to_feature_class_exports_by_folder_and_name(self):
        gpkg = arcpy.management.CreateFileGDB(self.directory, "out")[0]
        out = arcpy.conversion.FeatureClassToFeatureClass(self.squares, gpkg, "big", "n >= 2")[0]
        self.assertEqual(sorted(self.read(out, ["n"])), [(2,), (3,)])
        self.assertEqual(sorted(round(a, 6) for (a,) in self.read(out, ["SHAPE@AREA"])), [100.0, 100.0])

    def test_table_to_table_exports_the_rows(self):
        out = arcpy.conversion.TableToTable(self.squares, self.directory, "rows.gpkg", "n <> 2")[0]
        self.assertEqual(sorted(self.read(out, ["n"])), [(1,), (3,)])

    def test_feature_to_raster_takes_the_field_at_each_cell_centre(self):
        # Three 10 × 10 squares on a 5-unit grid over 0..20 × 0..20: the top-left quarter is 3, the bottom-left 1, the
        # bottom-right 2, and the top-right quarter, under no square, is NoData.
        path = os.path.join(self.directory, "r.tif")
        arcpy.conversion.FeatureToRaster(self.squares, "n", path, 5)
        got = arcpy.RasterToNumPyArray(arcpy.Raster(path), nodata_to_value=-1).tolist()
        self.assertEqual(got, [[3, 3, -1, -1], [3, 3, -1, -1], [1, 1, 2, 2], [1, 1, 2, 2]])


if __name__ == "__main__":
    unittest.main()
