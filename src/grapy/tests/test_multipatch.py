"""Multipatch features and the 3D tools on them (ADR-238), against hand-worked answers.

Most tests use axis-aligned boxes, whose areas and volumes are products of their sides, and planes, whose draped
areas are the flat area over the cosine of their slope. The 3D buffer of a straight line is a prism over a regular
polygon of ``buffer_quality`` sides, whose volume is (n/2)·sin(2π/n)·r²·L.
"""
import datetime
import math
import os
import sqlite3
import sys
import tempfile
import unittest

import numpy as np
import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _multipatch as M  # noqa: E402
from grapy import _solar, da  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402


def rows(path, fields):
    with da.SearchCursor(path, fields) as cursor:
        return [tuple(r) for r in cursor]


def box(lo, hi):
    return M.from_triangles(M.box(lo, hi))


def volume(shape):
    return M.signed_volume(M.triangles(shape))


class Base(unittest.TestCase):
    srid = 3857

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]

    def multipatch(self, name, shapes, fields=()):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, "MULTIPATCH", spatial_reference=self.srid)[0]
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for shape in shapes:
                cursor.insertRow(list(shape) if isinstance(shape, tuple) else [shape])
        return path

    def features(self, name, kind, shapes, fields=(), has_z=True):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, kind, has_z="ENABLED" if has_z else "DISABLED",
                                                   spatial_reference=self.srid)[0]
        for field, field_type in fields:
            arcpy.management.AddField(path, field, field_type)
        with da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for shape in shapes:
                cursor.insertRow(list(shape) if isinstance(shape, tuple) else [shape])
        return path

    def shapes(self, path):
        return [r[0]._shape for r in rows(path, ["SHAPE@"])]

    def out(self, name):
        return os.path.join(self.gpkg, name)

    def tin(self, name, points):
        pts = self.features(name + "_pts", "POINT", [shapely.Point(*p) for p in points])
        path = os.path.join(self.directory, name)
        arcpy.ddd.CreateTin(path, self.srid, [[pts, "Shape.Z", "masspoints", ""]])
        return path

    def raster(self, name, array, left=0.0, top=None, cell=1.0, nodata=None, srid=None):
        from grapy._raster import Raster, _Grid
        array = np.asarray(array)
        top = top if top is not None else array.shape[0] * cell
        grid = _Grid(left, top, cell, cell, array.shape[0], array.shape[1], srid if srid is not None else self.srid)
        mask = np.zeros(array.shape, bool) if nodata is None else array == nodata
        return Raster._of(array, mask, grid).save(os.path.join(self.directory, name + ".tif"))


class Model(Base):
    def test_a_multipatch_class_is_a_multipolygon_z_table_its_extension_row_marks(self):
        path = self.multipatch("cube", [box((0, 0, 0), (1, 1, 1))])
        d = arcpy.Describe(path)
        self.assertEqual((d.shapeType, d.hasZ), ("MultiPatch", True))
        connection = sqlite3.connect(os.path.join(self.directory, "data.gpkg"))
        try:
            self.assertEqual(connection.execute("select geometry_type_name, z from gpkg_geometry_columns where "
                                                "table_name = 'cube'").fetchone(), ("MULTIPOLYGON", 1))
            self.assertEqual(connection.execute("select extension_name from gpkg_extensions where table_name = 'cube' "
                                                "and column_name = 'geom'").fetchone(), ("grapy_multipatch",))
        finally:
            connection.close()

    def test_patches_build_faces_as_the_multipatch_paper_orders_them(self):
        strip = M.from_patches([("TRIANGLE_STRIP", [(0, 0, 0), (0, 1, 0), (1, 0, 0), (1, 1, 0), (2, 0, 0), (2, 1, 0)])])
        firsts = [list(map(tuple, np.array(f.exterior.coords)[:3])) for f in M.faces(strip)]
        # (0, 1, 2), (2, 1, 3), (2, 3, 4), (4, 3, 5)
        self.assertEqual(firsts[1], [(1, 0, 0), (0, 1, 0), (1, 1, 0)])
        self.assertEqual(len(firsts), 4)
        fan = M.from_patches([("TRIANGLE_FAN", [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)])])
        self.assertEqual(len(M.faces(fan)), 2)
        ring = M.from_patches([("OUTER_RING", [(0, 0, 0), (4, 0, 0), (4, 4, 0), (0, 4, 0), (0, 0, 0)]),
                               ("INNER_RING", [(1, 1, 0), (1, 2, 0), (2, 2, 0), (2, 1, 0), (1, 1, 0)])])
        self.assertEqual(len(M.faces(ring)), 1)
        self.assertAlmostEqual(M.surface_area(M.triangles(ring)), 15.0)
        loose = M.from_patches([("RING", [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 0, 0)]),
                                ("RING", [(5, 0, 0), (6, 0, 0), (6, 1, 0), (5, 0, 0)])])
        self.assertEqual(len(M.faces(loose)), 2)  # rings without a first ring are outer rings without holes

    def test_is_closed_3d_marks_a_box_closed_and_a_box_missing_a_face_open(self):
        cube = M.box((0, 0, 0), (2, 2, 2))
        path = self.multipatch("two", [box((0, 0, 0), (2, 2, 2)), M.from_triangles(cube[2:])])
        arcpy.ddd.IsClosed3D(path)
        self.assertEqual(sorted(r[0] for r in rows(path, ["IsClosed"])), [0, 1])

    def test_add_z_information_reads_a_multipatchs_surface_area_and_volume(self):
        path = self.multipatch("b", [box((0, 0, 0), (2, 3, 4)), M.from_triangles(M.box((0, 0, 0), (1, 1, 1))[2:])])
        arcpy.ddd.AddZInformation(path, "SURFACE_AREA;VOLUME")
        (a1, v1), (a2, v2) = rows(path, ["SURFACE_AREA", "VOLUME"])
        self.assertAlmostEqual(a1, 52.0)
        self.assertAlmostEqual(v1, 24.0)
        self.assertAlmostEqual(a2, 5.0)
        self.assertEqual(v2, 0.0)
        line = self.features("l", "POLYLINE", [shapely.LineString([(0, 0, 0), (1, 1, 1)])])
        with self.assertRaises(NotBuiltError):
            arcpy.ddd.AddZInformation(line, "VOLUME")


class Footprints(Base):
    def test_footprint_is_the_projected_area_with_the_lowest_and_highest_z(self):
        wall = M.from_faces([shapely.Polygon([(0, 10, 0), (4, 10, 0), (4, 10, 3), (0, 10, 3)])])
        path = self.multipatch("m", [("a", box((0, 0, 1), (2, 3, 5))), ("a", box((2, 0, 1), (3, 3, 2))), ("b", wall)],
                               fields=[("Kind", "TEXT")])
        arcpy.ddd.MultiPatchFootprint(path, self.out("fp"))
        got = rows(self.out("fp"), ["Kind", "Z_Min", "Z_Max", "SHAPE@AREA"])
        self.assertEqual([(k, a, b, round(s, 9)) for k, a, b, s in got], [("a", 1, 5, 6.0), ("a", 1, 2, 3.0),
                                                                          ("b", 0, 3, 0.0)])
        wall_box = self.shapes(self.out("fp"))[2]
        self.assertEqual(wall_box.bounds, (0.0, 10.0, 4.0, 10.0))  # a vertical wall's footprint is its extent box
        arcpy.ddd.MultiPatchFootprint(path, self.out("grouped"), "Kind")
        got = rows(self.out("grouped"), ["Kind", "Z_Min", "Z_Max", "SHAPE@AREA"])
        self.assertEqual((got[0][0], got[0][1], got[0][2]), ("a", 1, 5))
        self.assertAlmostEqual(got[0][3], 9.0)

    def test_minimum_bounding_volume_hull_and_envelope(self):
        corners = [shapely.Point(x, y, z) for x in (0, 1) for y in (0, 1) for z in (0, 1)] + [shapely.Point(.5, .5, .5)]
        pts = self.features("p", "POINT", corners)
        arcpy.ddd.MinimumBoundingVolume(pts, "Shape.Z", self.out("hull"), "CONVEX_HULL", "ALL",
                                        mbv_fields="MBV_FIELDS")
        (area, vol), = rows(self.out("hull"), ["MBV_Area", "MBV_Volume"])
        self.assertAlmostEqual((area, vol), (6.0, 1.0))
        self.assertTrue(M.is_closed(self.shapes(self.out("hull"))[0]))
        lines = self.features("l", "POLYLINE", [("g", shapely.LineString([(0, 0, 0), (2, 1, 0)])),
                                                ("g", shapely.LineString([(1, 3, 4), (1, 2, 1)]))],
                              fields=[("G", "TEXT")])
        arcpy.ddd.MinimumBoundingVolume(lines, "Shape.Z", self.out("env"), "ENVELOPE", "LIST", "G", "MBV_FIELDS")
        self.assertEqual(rows(self.out("env"), ["G", "MBV_Volume", "MBV_Area"]), [("g", 24.0, 52.0)])
        for kind in ("SPHERE", "CONCAVE_HULL"):
            with self.assertRaises(NotBuiltError):
                arcpy.ddd.MinimumBoundingVolume(pts, "Shape.Z", self.out("x"), kind)


class OnTins(Base):
    """The plane z = x + y over 0–10: its slope's cosine is 1/√3, so a draped area is √3 times the flat one."""

    def setUp(self):
        super().setUp()
        self.plane = self.tin("plane", [(0, 0, 0), (10, 0, 10), (10, 10, 20), (0, 10, 10), (5, 5, 10)])

    def test_tin_to_multipatch_with_a_base_is_a_closed_solid_down_to_it(self):
        arcpy.ddd.TINToMultipatch(self.plane, self.out("open"))
        top = self.shapes(self.out("open"))[0]
        self.assertAlmostEqual(M.surface_area(M.triangles(top)), 100 * math.sqrt(3))
        self.assertFalse(M.is_closed(top))
        arcpy.ddd.TINToMultipatch(self.plane, self.out("solid"), base_height=-5)
        solid = self.shapes(self.out("solid"))[0]
        self.assertTrue(M.is_closed(solid))
        self.assertAlmostEqual(volume(solid), 1500.0)  # ∫(x + y + 5) over the square
        with self.assertRaises(ExecuteError):
            arcpy.ddd.TINToMultipatch(self.plane, self.out("bad"), base_height=1)
        with self.assertRaises(NotBuiltError):
            arcpy.ddd.TINToMultipatch(self.plane, self.out("bad"), "1 Meters")

    def test_interpolate_poly_to_patch_drapes_the_polygon_with_its_two_areas(self):
        polys = self.features("sq", "POLYGON", [(7, shapely.box(0, 0, 5, 5)), (8, shapely.box(20, 20, 21, 21))],
                              fields=[("Code", "LONG")], has_z=False)
        arcpy.ddd.InterpolatePolyToPatch(self.plane, polys, self.out("patch"), 1024, 1, "Area", "SArea")
        (code, area, sarea, shape), = rows(self.out("patch"), ["Code", "Area", "SArea", "SHAPE@"])
        self.assertEqual(code, 7)
        self.assertAlmostEqual(area, 25.0)
        self.assertAlmostEqual(sarea, 25 * math.sqrt(3))
        z = shapely.get_coordinates(shape._shape, include_z=True)
        np.testing.assert_allclose(z[:, 2], z[:, 0] + z[:, 1], atol=1e-9)

    def test_extrude_between_two_tins(self):
        floor = self.tin("floor", [(0, 0, -10), (10, 0, -10), (10, 10, -10), (0, 10, -10)])
        polys = self.features("sq", "POLYGON", [shapely.box(0, 0, 5, 5)], has_z=False)
        arcpy.ddd.ExtrudeBetween(self.plane, floor, polys, self.out("solid"))
        solid = self.shapes(self.out("solid"))[0]
        self.assertTrue(M.is_closed(solid))
        self.assertAlmostEqual(volume(solid), 375.0)  # ∫(x + y + 10) over 0–5 squared
        pts = self.features("pt", "POINT", [shapely.Point(2, 3)], has_z=False)
        arcpy.ddd.ExtrudeBetween(self.plane, floor, pts, self.out("stick"))
        stick = self.shapes(self.out("stick"))[0]
        self.assertEqual(shapely.get_coordinates(stick, include_z=True)[:, 2].tolist(), [-10.0, 5.0])
        lines = self.features("ln", "POLYLINE", [shapely.LineString([(0, 0), (4, 0)])], has_z=False)
        arcpy.ddd.ExtrudeBetween(self.plane, floor, lines, self.out("wall"))
        wall = self.shapes(self.out("wall"))[0]
        self.assertAlmostEqual(M.surface_area(M.triangles(wall)), 48.0)  # ∫(x + 10) over 0–4


class Lines(Base):
    def test_a_line_through_a_box_meets_it_twice_and_splits_into_three(self):
        cube = self.multipatch("cube", [box((0, 0, 0), (2, 2, 2))])
        line = self.features("l", "POLYLINE", [shapely.LineString([(-1, 1, 1), (3, 1, 1)]),
                                               shapely.LineString([(-1, 5, 1), (3, 5, 1)])])
        result = arcpy.ddd.Intersect3DLineWithMultiPatch(line, cube, "IDS_ONLY", self.out("pts"), self.out("parts"))
        self.assertEqual(result[2], 2)
        self.assertEqual([(r[0], r[1], r[2]) for r in rows(self.out("pts"), ["LINE_OID", "MPATCH_OID", "DIST_3D"])],
                         [(1, 1, 1.0), (1, 1, 3.0)])
        got = rows(self.out("parts"), ["LINE_OID", "FROM_MP_ID", "TO_MP_ID", "DIST_3D", "LENGTH_3D"])
        self.assertEqual(got, [(1, -1, 1, 0.0, 1.0), (1, 1, 1, 1.0, 2.0), (1, 1, -1, 3.0, 1.0), (2, -1, -1, 0.0, 4.0)])

    def test_inside_3d_judges_points_lines_and_solids(self):
        cube = self.multipatch("cube", [box((0, 0, 0), (2, 2, 2))])
        pts = self.features("p", "POINT", [shapely.Point(1, 1, 1), shapely.Point(5, 5, 5), shapely.Point(2, 1, 1)])
        arcpy.ddd.Inside3D(pts, cube, os.path.join(self.gpkg, "t1"), "COMPLEX")
        self.assertEqual(rows(os.path.join(self.gpkg, "t1"), ["Target_ID", "Status", "Contain_ID"]),
                         [(1, "Inside", 1), (3, "Inside", 1)])
        line = self.features("l", "POLYLINE", [shapely.LineString([(-1, 1, 1), (1, 1, 1)])])
        arcpy.ddd.Inside3D(line, cube, os.path.join(self.gpkg, "t2"))
        self.assertEqual(rows(os.path.join(self.gpkg, "t2"), ["Target_ID", "Status"]), [(1, "Partially_Inside")])
        solids = self.multipatch("s", [box((0.5, 0.5, 0.5), (1, 1, 1)), box((1, 1, 1), (3, 3, 3)),
                                       box((5, 5, 5), (6, 6, 6))])
        arcpy.ddd.Inside3D(solids, cube, os.path.join(self.gpkg, "t3"))
        self.assertEqual(rows(os.path.join(self.gpkg, "t3"), ["Target_ID", "Status"]),
                         [(1, "Inside"), (2, "Partially_Inside")])


class Booleans(Base):
    def test_intersect_union_and_difference_of_two_overlapping_boxes(self):
        one = self.multipatch("one", [("a", box((0, 0, 0), (2, 2, 2)))], fields=[("Name", "TEXT")])
        two = self.multipatch("two", [("b", box((1, 1, 1), (3, 3, 3))), ("c", box((0.2, 0.2, 0.2), (0.4, 0.4, 0.4)))],
                              fields=[("Name", "TEXT")])
        arcpy.ddd.Intersect3D(one, self.out("common"), two)
        got = rows(self.out("common"), ["Name", "Name_1", "SHAPE@"])
        self.assertEqual([(a, b) for a, b, _ in got], [("a", "b"), ("a", "c")])
        self.assertAlmostEqual(volume(got[0][2]._shape), 1.0)
        self.assertAlmostEqual(volume(got[1][2]._shape), 0.008)
        both = self.multipatch("both", [box((0, 0, 0), (2, 2, 2)), box((1, 1, 1), (3, 3, 3)),
                                        box((10, 10, 10), (11, 11, 11)), box((2, 0, 0), (3, 1, 1))])
        arcpy.ddd.Union3D(both, self.out("u"), disable_optimization="DISABLE", out_table=os.path.join(self.gpkg, "ut"))
        volumes = sorted(round(volume(s), 9) for s in self.shapes(self.out("u")))
        self.assertEqual(volumes, [1.0, 1.0, 15.0])  # the one touching only at a face is not merged
        self.assertEqual(sorted(rows(os.path.join(self.gpkg, "ut"), ["Input_ID"])), [(1,), (2,), (3,), (4,)])
        arcpy.ddd.Union3D(both, self.out("u2"), output_all="DISABLE")
        self.assertEqual([round(volume(s), 9) for s in self.shapes(self.out("u2"))], [15.0])
        arcpy.ddd.Difference3D(one, two, self.out("d"), os.path.join(self.gpkg, "dt"))
        self.assertAlmostEqual(volume(self.shapes(self.out("d"))[0]), 8 - 1 - 0.008)
        self.assertEqual(sorted(rows(os.path.join(self.gpkg, "dt"), ["Minuend_ID", "Subtrahend"])), [(1, 1), (1, 2)])
        inside = self.multipatch("in", [box((0.5, 0.5, 0.5), (1, 1, 1))])
        arcpy.ddd.Difference3D(inside, one, self.out("gone"))
        self.assertEqual(self.shapes(self.out("gone")), [])
        with self.assertRaises(NotBuiltError):
            arcpy.ddd.Intersect3D(one, self.out("x"), two, "SURFACE")

    def test_a_solid_boolean_needs_closed_multipatches(self):
        open_ = self.multipatch("open", [M.from_triangles(M.box((0, 0, 0), (1, 1, 1))[2:])])
        with self.assertRaises(ExecuteError):
            arcpy.ddd.Union3D(open_, self.out("x"))


class Buffers(Base):
    def test_a_straight_line_buffers_to_a_prism_over_a_regular_polygon(self):
        lines = self.features("l", "POLYLINE", [shapely.LineString([(0, 0, 0), (10, 0, 0)])])
        arcpy.ddd.Buffer3D(lines, self.out("b"), "1 Meters", "STRAIGHT", 20)
        shape = self.shapes(self.out("b"))[0]
        self.assertTrue(M.is_closed(shape))
        self.assertAlmostEqual(volume(shape), 10 * 10 * math.sin(2 * math.pi / 20), places=6)

    def test_a_point_buffers_to_a_sphere_and_round_joints_close_a_bend(self):
        pts = self.features("p", "POINT", [shapely.Point(0, 0, 0)])
        arcpy.ddd.Buffer3D(pts, self.out("s"), 2, buffer_quality=40)
        sphere = self.shapes(self.out("s"))[0]
        self.assertTrue(M.is_closed(sphere))
        self.assertLess(volume(sphere), 4 / 3 * math.pi * 8)
        self.assertGreater(volume(sphere), 0.97 * 4 / 3 * math.pi * 8)
        bend = self.features("b", "POLYLINE", [(1.0, shapely.LineString([(0, 0, 0), (10, 0, 0), (10, 10, 0)]))],
                             fields=[("R", "DOUBLE")])
        arcpy.ddd.Buffer3D(bend, self.out("round"), "R", "ROUND", 20)
        arcpy.ddd.Buffer3D(bend, self.out("straight"), "R", "STRAIGHT", 20)
        for name in ("round", "straight"):
            self.assertTrue(M.is_closed(self.shapes(self.out(name))[0]))
        self.assertGreater(volume(self.shapes(self.out("round"))[0]), volume(self.shapes(self.out("straight"))[0]))


class Fences(Base):
    def test_a_fence_fills_between_two_flat_surfaces_and_up_to_a_ceiling(self):
        low = self.raster("low", np.zeros((10, 20)), cell=1.0)
        high = self.raster("high", np.full((10, 20), 10.0), cell=1.0)
        line = self.features("l", "POLYLINE", [shapely.LineString([(2, 5), (12, 5)])], has_z=False)
        arcpy.ddd.FenceDiagram(line, [low, high], self.out("f"))
        self.assertAlmostEqual(M.surface_area(M.triangles(self.shapes(self.out("f"))[0])), 100.0)
        arcpy.ddd.FenceDiagram(line, f"{low};{high}", self.out("g"), ceiling_height=15, sample_distance="2 Meters")
        self.assertAlmostEqual(M.surface_area(M.triangles(self.shapes(self.out("g"))[0])), 150.0)


class Lidar(Base):
    def las(self, name, x, y, z, classes):
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from test_las import write_las
        folder = os.path.join(self.directory, name)
        os.makedirs(folder, exist_ok=True)
        write_las(os.path.join(folder, "a.las"), x, y, z, classes, np.ones(len(x)), np.ones(len(x)))
        lasd = os.path.join(self.directory, name + ".lasd")
        arcpy.management.CreateLasDataset(folder, lasd)
        return lasd

    def test_las_building_multipatch_closes_a_flat_roof_down_to_the_ground_field(self):
        gx, gy = np.meshgrid(np.arange(0.25, 4, 0.5), np.arange(0.25, 4, 0.5))
        x, y = np.concatenate([gx.ravel(), [10.0]]), np.concatenate([gy.ravel(), [10.0]])
        z = np.concatenate([np.full(gx.size, 6.0), [0.0]])
        classes = np.concatenate([np.full(gx.size, 6), [2]])
        lasd = self.las("roof", x, y, z, classes)
        footprints = self.features("fp", "POLYGON", [(1.0, shapely.box(0, 0, 4, 4))], fields=[("Ground", "DOUBLE")],
                                   has_z=False)
        arcpy.ddd.LasBuildingMultipatch(lasd, footprints, "Ground", self.out("bldg"), sampling_resolution=1)
        shape = self.shapes(self.out("bldg"))[0]
        self.assertTrue(M.is_closed(shape))
        self.assertAlmostEqual(volume(shape), 16 * 5.0)  # a 4 × 4 roof at 6 over ground at 1
        with self.assertRaises(NotBuiltError):
            arcpy.ddd.LasBuildingMultipatch(lasd, footprints, "Ground", self.out("x"), simplification="1 Meters")

    def test_set_las_class_codes_using_raster_takes_each_points_cell_and_leaves_nodata(self):
        x = np.array([0.5, 1.5, 2.5, 3.5])
        lasd = self.las("cls", x, np.full(4, 0.5), np.zeros(4), np.full(4, 2))
        codes = self.raster("codes", np.array([[9, 40, -1, 6]], np.int32), cell=1.0, nodata=-1)
        arcpy.ddd.SetLasClassCodesUsingRaster(lasd, codes)
        from grapy import _las
        found, _ = _las.points(lasd, "test", filtered=False)
        # 40 is beyond LAS 1.2 format 1's 0–31 and is ignored; NoData changes nothing
        self.assertEqual(found["classification"].tolist(), [9, 2, 2, 6])


class Sun(Base):
    srid = 32633

    def test_noaa_solar_coordinates_match_meeus_example_25a_and_28b(self):
        jd = _solar.julian_day(datetime.datetime(1992, 10, 13))
        self.assertEqual(jd, 2448908.5)
        apparent, declination, eot = _solar.coordinates(jd)
        self.assertAlmostEqual(declination, -7.78507, places=3)
        self.assertAlmostEqual(apparent, 199.90988, delta=0.002)
        self.assertAlmostEqual(eot, 13 + 42.6 / 60, delta=0.05)  # 13 m 42.6 s
        self.assertEqual(_solar.offset("Pacific_Standard_Time"), -8.0)
        self.assertEqual(_solar.offset("UTC+5:30"), 5.5)

    def test_a_boxs_shadow_volume_is_its_sweep_along_the_rays(self):
        x0, y0 = 500000.0, 4500000.0
        path = self.multipatch("b", [box((x0, y0, 0), (x0 + 10, y0 + 20, 30))])
        when = datetime.datetime(2026, 6, 21, 9, 30)
        arcpy.ddd.SunShadowVolume(path, when, self.out("sh"), time_zone="UTC")
        (source, oid, at, azimuth, elevation, shape), = rows(self.out("sh"), ["SOURCE", "SOURCE_ID", "DATE_TIME",
                                                                              "AZIMUTH", "VERT_ANGLE", "SHAPE@"])
        lon, lat = _solar.place(x0 + 5, y0 + 10, self.srid)
        az, el = _solar.position(when, lon, lat)
        self.assertEqual((source, oid), ("b", 1))
        self.assertEqual(str(at), str(when))
        self.assertAlmostEqual(azimuth, az, delta=1e-3)  # computed at the feature, a few metres from its centre
        self.assertAlmostEqual(elevation, el, delta=1e-3)
        d = -np.array(_solar.towards(azimuth, elevation))
        length = 30 / abs(d[2])
        a, b, c = 10, 20, 30
        expected = a * b * c + length * (b * c * abs(d[0]) + a * c * abs(d[1]) + a * b * abs(d[2]))
        self.assertAlmostEqual(volume(shape._shape) / expected, 1.0, places=6)
        self.assertTrue(M.is_closed(shape._shape))

    def test_shadow_frequency_counts_the_cells_a_box_shades(self):
        x0, y0 = 500000.0, 4500000.0
        ground = self.raster("g", np.zeros((60, 60)), left=x0 - 30, top=y0 + 30, cell=1.0)
        path = self.multipatch("b", [box((x0 - 2.3, y0 - 2.3, 0), (x0 + 2.3, y0 + 2.3, 10))])
        when = datetime.datetime(2026, 6, 21, 10, 0)
        arcpy.ddd.SunShadowFrequency(path, ground, os.path.join(self.directory, "freq.tif"), start_time=when,
                                     end_time=when, time_zone="UTC")
        counts = arcpy.RasterToNumPyArray(os.path.join(self.directory, "freq.tif"))
        lon, lat = _solar.place(x0, y0 + 0.0, self.srid)
        az, el = _solar.position(when, lon, lat)
        reach = 10 / math.tan(math.radians(el))
        dx, dy = -reach * math.sin(math.radians(az)), -reach * math.cos(math.radians(az))
        foot = shapely.box(x0 - 2.3, y0 - 2.3, x0 + 2.3, y0 + 2.3)
        shade = shapely.convex_hull(shapely.union(foot, shapely.affinity.translate(foot, dx, dy)))
        xs = x0 - 30 + np.arange(60) + 0.5
        ys = y0 + 30 - np.arange(60) - 0.5
        gx, gy = np.meshgrid(xs, ys)
        expected = shapely.contains_xy(shade, gx, gy).astype(int)
        self.assertEqual(int(np.sum(counts != expected)), 0)
        self.assertGreater(int(counts.sum()), 25)


class Import(Base):
    CUBE_OBJ = ("# a unit cube\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv 0 0 1\nv 1 0 1\nv 1 1 1\nv 0 1 1\n"
                "f 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3/1 4/1 8/1 7/1\nf -8 -4 -1 -5\n")

    def write(self, name, text):
        path = os.path.join(self.directory, name)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        return path

    def test_an_obj_cube_is_a_closed_unit_volume_and_y_is_up_turns_it(self):
        obj = self.write("cube.obj", self.CUBE_OBJ)
        arcpy.ddd.Import3DFiles(obj, self.out("m"))
        (name, shape), = rows(self.out("m"), ["Name", "SHAPE@"])
        self.assertEqual(name, "cube")
        self.assertTrue(M.is_closed(shape._shape))
        self.assertAlmostEqual(volume(shape._shape), 1.0)
        tall = self.write("tall.obj", "v 0 0 0\nv 1 0 0\nv 0 5 0\nf 1 2 3\n")
        arcpy.ddd.Import3DFiles(tall, self.out("y"), y_is_up="Y_IS_UP")
        z = shapely.get_coordinates(self.shapes(self.out("y"))[0], include_z=True)[:, 2]
        self.assertEqual(float(z.max()), 5.0)

    def test_a_collada_cube_honours_its_unit_up_axis_and_node_transform(self):
        positions = "0 0 0 1 0 0 1 1 0 0 1 0 0 0 1 1 0 1 1 1 1 0 1 1"
        tris = M.box((0, 0, 0), (1, 1, 1))
        corners = {tuple(map(float, p)): i for i, p in enumerate(np.array(positions.split(), float).reshape(-1, 3))}
        index = " ".join(str(corners[tuple(map(float, p))]) for t in tris for p in t)
        dae = f"""<?xml version="1.0"?>
<COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
 <asset><unit meter="0.5"/><up_axis>Z_UP</up_axis></asset>
 <library_geometries><geometry id="g"><mesh>
  <source id="p"><float_array id="pa" count="24">{positions}</float_array>
   <technique_common><accessor source="#pa" count="8" stride="3"/></technique_common></source>
  <vertices id="v"><input semantic="POSITION" source="#p"/></vertices>
  <triangles count="12"><input semantic="VERTEX" source="#v" offset="0"/><p>{index}</p></triangles>
 </mesh></geometry></library_geometries>
 <library_visual_scenes><visual_scene id="s"><node><translate>10 0 0</translate>
  <instance_geometry url="#g"/></node></visual_scene></library_visual_scenes>
 <scene><instance_visual_scene url="#s"/></scene>
</COLLADA>"""
        path = self.write("cube.dae", dae)
        arcpy.ddd.Import3DFiles(path, self.out("d"))
        shape = self.shapes(self.out("d"))[0]
        self.assertAlmostEqual(volume(shape), 0.125)
        self.assertEqual(shape.bounds[0], 5.0)  # translated 10 in file units, then scaled by 0.5

    def test_a_vrml_indexed_face_set_under_a_transform(self):
        wrl = """#VRML V2.0 utf8
DEF House Transform { translation 100 0 0 children [
  Shape { geometry IndexedFaceSet { coord DEF C Coordinate { point [ 0 0 0, 1 0 0, 1 1 0, 0 1 0,
      0 0 1, 1 0 1, 1 1 1, 0 1 1 ] }
    coordIndex [ 0 3 2 1 -1, 4 5 6 7 -1, 0 1 5 4 -1, 1 2 6 5 -1, 2 3 7 6 -1, 3 0 4 7 -1 ] } }
  Shape { geometry Box { size 1 1 1 } } ] }
Transform { translation 0 0 5 children Shape { geometry IndexedFaceSet { coord USE C coordIndex [ 0 1 2 -1 ] } } }
"""
        path = self.write("house.wrl", wrl)
        arcpy.ddd.Import3DFiles(path, self.out("w"))
        shape = self.shapes(self.out("w"))[0]
        self.assertAlmostEqual(volume(M.from_faces(M.faces(shape)[:6])), 1.0)
        arcpy.ddd.Import3DFiles(path, self.out("roots"), "ONE_ROOT_ONE_FEATURE")
        self.assertEqual([r[0] for r in rows(self.out("roots"), ["Name"])], ["House", "Transform"])

    def test_3ds_and_openflight_are_refused_by_name(self):
        for name in ("a.3ds", "a.flt"):
            with self.assertRaises(NotBuiltError):
                arcpy.ddd.Import3DFiles(self.write(name, "x"), self.out("x"))


class Voxels(Base):
    srid = 32633

    def points(self):
        return self.features("p", "POINT", [(0.0, shapely.Point(0, 0, 0)), (10.0, shapely.Point(10, 0, 0)),
                                            (20.0, shapely.Point(0, 10, 10)), (30.0, shapely.Point(10, 10, 10))],
                             fields=[("V", "DOUBLE")])

    def test_idw3d_weights_by_inverse_3d_distance(self):
        from scipy.io import netcdf_file
        out = os.path.join(self.directory, "idw.nc")
        nbr = arcpy.ga.SearchNeighborhoodStandard3D(100, 4, 1, "ONE_SECTOR")
        result = arcpy.ga.IDW3D(self.points(), "V", out, 2, 1, os.path.join(self.gpkg, "cv"), 5, 5, 5,
                                search_neighborhood=nbr)
        with netcdf_file(out, "r", mmap=False) as nc:
            values = nc.variables["V"][:].copy()
            self.assertEqual(values.shape, (3, 3, 3))
            self.assertEqual(nc.variables["x"][:].tolist(), [0.0, 5.0, 10.0])
        self.assertAlmostEqual(values[0, 0, 0], 0.0)
        # (5, 0, 0): 5 from the two values 0 and 10, 15 from 20 and 30 → (10/25 + 50/225) / (2/25 + 2/225) = 7
        self.assertAlmostEqual(values[0, 0, 1], 7.0)
        self.assertEqual(result[2], 4)
        self.assertEqual(len(rows(os.path.join(self.gpkg, "cv"), ["Measured", "Predicted", "Error"])), 4)
        with self.assertRaises(NotBuiltError):
            arcpy.ga.IDW3D(self.points(), "V", out)
        with self.assertRaises(NotBuiltError):
            arcpy.ga.IDW3D(self.points(), "V", out,
                           search_neighborhood=arcpy.ga.SearchNeighborhoodStandard3D(100, 4, 1, "FOUR_SECTORS"))
        estimated = arcpy.ga.IDW3D(self.points(), "V", out, search_neighborhood=nbr)
        self.assertEqual(estimated[2], 4)

    def test_nearest_neighbor_3d_codes_categories_in_order_and_breaks_ties_to_the_first(self):
        from scipy.io import netcdf_file
        pts = self.features("c", "POINT", [("b", shapely.Point(0, 0, 0)), ("a", shapely.Point(10, 0, 0)),
                                           ("c", shapely.Point(0, 10, 10))], fields=[("K", "TEXT")])
        out = os.path.join(self.directory, "nn.nc")
        arcpy.ga.NearestNeighbor3D(pts, "K", out, 5, 5, 5)
        with netcdf_file(out, "r", mmap=False) as nc:
            values = nc.variables["K"][:].copy()
            self.assertEqual(nc.variables["K"].categories, b"0=a; 1=b; 2=c")
        self.assertEqual(int(values[0, 0, 0]), 1)   # b
        self.assertEqual(int(values[0, 0, 2]), 0)   # a
        self.assertEqual(int(values[0, 0, 1]), 0)   # equally near a and b: the first, a
        self.assertEqual(int(values[2, 2, 0]), 2)   # c


class Refused(unittest.TestCase):
    def test_the_multipatch_names_left_refused_give_their_own_reasons(self):
        for name in ("EncloseMultiPatch", "ExtractLOD2Buildings", "ExtractMultipatchFromMesh",
                     "ExtractMeshFeaturesUsingPointCloud"):
            why = arcpy.ddd._REFUSED[name]
            self.assertNotIn("not hold yet", why)
            self.assertIn("ADR-238", why)
        with self.assertRaises(NotBuiltError):
            arcpy.Multipatch()


if __name__ == "__main__":
    unittest.main()
