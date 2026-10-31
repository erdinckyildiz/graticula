"""Terrain datasets (ADR-234), against hand-worked answers.

Most tests use the plane z = x + 2y sampled on the integer grid 0–10: every level of any pyramid of it lies on that
plane, so heights, slopes, lengths, areas and volumes can be computed by hand.
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np
import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _terrain, da  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402


def plane(x, y):
    return x + 2 * y


class Terrain(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "fds", 3857)[0]
        self.grid = self.points("grid", [(x, y, plane(x, y)) for x in range(11) for y in range(11)])

    def points(self, name, xyz, kind="POINT", dataset=None):
        path = arcpy.management.CreateFeatureclass(dataset or self.fds, name, kind, has_z="ENABLED")[0]
        with da.InsertCursor(path, ["SHAPE@"]) as cursor:
            if kind == "MULTIPOINT":
                cursor.insertRow([shapely.MultiPoint(xyz)])
            else:
                for p in xyz:
                    cursor.insertRow([shapely.Point(*p)])
        return path

    def terrain(self, levels=("1 1000", "4 10000"), pyramid="WINDOWSIZE", method="ZMIN", sources=None, name="terr"):
        t = arcpy.ddd.CreateTerrain(self.fds, name, 0.5, None, None, pyramid, method)[0]
        arcpy.ddd.AddTerrainPyramidLevel(t, "", list(levels))
        arcpy.ddd.AddFeatureClassToTerrain(t, sources or [[self.grid, "Shape.Z", "masspoints", 1, "", "", False, False,
                                                            "", "", False]])
        arcpy.ddd.BuildTerrain(t)
        return t

    def level(self, t, resolution):
        out = arcpy.ddd.TerrainToPoints(t, os.path.join(self.gpkg, f"lvl{len(os.listdir(self.directory))}"),
                                        resolution, None, "POINT")[0]
        with da.SearchCursor(out, ["SHAPE@"]) as rows:
            return sorted(tuple(np.round(shapely.get_coordinates(r[0]._shape, include_z=True)[0], 9)) for r in rows)

    # ---- the model

    def test_a_terrain_is_its_own_tables_in_the_feature_datasets_geopackage(self):
        import sqlite3
        t = self.terrain()
        self.assertEqual(t, f"{self.fds}/terr")
        connection = sqlite3.connect(self.gpkg)
        try:
            registered = {r[0] for r in connection.execute(
                "select table_name from gpkg_extensions where extension_name = 'grapy_terrain'")}
            full = connection.execute("select count(*) from grapy_terrain_pyramid where resolution = 0").fetchone()[0]
        finally:
            connection.close()
        self.assertEqual(registered, set(_terrain.TABLES))
        self.assertEqual(full, 121)

    def test_describe_answers_the_settings_levels_and_sources(self):
        t = self.terrain()
        d = arcpy.Describe(t)
        self.assertEqual((d.dataType, d.pyramidType, d.windowSizeMethod), ("Terrain", "WINDOWSIZE", "ZMIN"))
        self.assertEqual(d.pyramidLevels, [(1.0, 1000), (4.0, 10000)])
        self.assertEqual(d.dataSources, ["grid"])
        self.assertTrue(d.isBuilt)
        self.assertEqual((d.extent.XMin, d.extent.YMax), (0.0, 10.0))
        self.assertEqual(d.spatialReference.factoryCode, 3857)

    def test_a_terrain_needs_a_feature_dataset_and_its_features_must_lie_in_it(self):
        with self.assertRaises(ExecuteError):
            arcpy.ddd.CreateTerrain(self.gpkg, "loose", 1)
        outside = arcpy.management.CreateFeatureclass(self.gpkg, "outside", "POINT", has_z="ENABLED",
                                                      spatial_reference=3857)[0]
        t = arcpy.ddd.CreateTerrain(self.fds, "t2", 1)[0]
        with self.assertRaises(ExecuteError):   # no pyramid level yet, as documented
            arcpy.ddd.AddFeatureClassToTerrain(t, [[self.grid, "Shape.Z", "masspoints"]])
        arcpy.ddd.AddTerrainPyramidLevel(t, "", "2 5000")
        with self.assertRaises(ExecuteError):
            arcpy.ddd.AddFeatureClassToTerrain(t, [[outside, "Shape.Z", "masspoints"]])

    def test_the_settings_the_pages_leave_undefined_are_refused(self):
        with self.assertRaises(NotBuiltError):
            arcpy.ddd.CreateTerrain(self.fds, "s", 1, None, None, "WINDOWSIZE", "ZMIN", "MILD")
        with self.assertRaises(NotBuiltError):
            arcpy.ddd.CreateTerrain(self.fds, "s", 1, None, None, "WINDOWSIZE", "ZMIN", "NONE", 1, "CONSTRAINED_DELAUNAY")

    # ---- the pyramid

    def test_window_size_thinning_keeps_what_each_method_selects(self):
        # 4 x 4 points at 0..3; windows of 2 from the extent's corner make four windows of four points.
        z = {(x, y): (x * 7 + y * 3) % 5 + 0.25 * x for x in range(4) for y in range(4)}
        pts = self.points("w", [(x, y, z[(x, y)]) for x in range(4) for y in range(4)])
        source = [[pts, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False]]
        windows = [[(x, y) for x in (a, a + 1) for y in (b, b + 1)] for a in (0, 2) for b in (0, 2)]
        for method in ("ZMIN", "ZMAX", "ZMEAN", "ZMINMAX"):
            t = self.terrain(("2 1000",), "WINDOWSIZE", method, source, name=f"w{method}")
            expected = set()
            for members in windows:
                values = [z[m] for m in members]
                if method in ("ZMIN", "ZMINMAX"):
                    expected.add(members[int(np.argmin(values))])
                if method in ("ZMAX", "ZMINMAX"):
                    expected.add(members[int(np.argmax(values))])
                if method == "ZMEAN":
                    expected.add(members[int(np.argmin(np.abs(np.array(values) - np.mean(values))))])
            self.assertEqual({(p[0], p[1]) for p in self.level(t, 2)}, expected, method)

    def test_window_size_levels_nest(self):
        t = self.terrain(("1 1000", "2 5000", "4 10000"), "WINDOWSIZE", "ZMAX")
        one, two, four = (set(self.level(t, r)) for r in (1, 2, 4))
        self.assertTrue(four <= two <= one)
        self.assertEqual(len(four), 9)   # windows of 4 from 0: [0,4) [4,8) [8,12) on each axis

    def test_windows_start_at_pros_tile_corners_on_a_utm_dataset(self):
        # ArcGIS Pro 3.7, measured 2026-10-31 on 45 terrains (D-339, tools/pro-compare/adr234-terrain-corner.py): tiles
        # of round(spacing * sqrt(200000)) from the XY domain's origin, windows from each tile's corner; an axis whose
        # tile index reaches 23,170 is centred on the data. Pro's corners on 2,500 points at 500000-500100,
        # 4400000-4400100 in UTM 35N, default domain (-5120900, -9998100): spacing 2 -> x 499678, y 4399770
        # (windows of 4, 8, 16 at 2, 6, 14 and 2, 2, 10 modulo the window); spacing 1 -> y centred, 4399826.48.
        pts = np.array([[500000.01, 4400000.01, 0], [500099.91, 4400099.95, 0]])
        ax, ay = _terrain.window_anchors(pts, 2, 32635, (0, 0))
        self.assertEqual((ax[0], ay[0]), (499678.0, 4399770.0))
        self.assertEqual([(ax[0] % w, ay[0] % w) for w in (4, 8, 16)], [(2, 2), (6, 2), (14, 10)])
        ax, ay = _terrain.window_anchors(pts, 1, 32635, (0, 0))
        self.assertEqual(ax[0], -5120900 + 12574 * 447)
        self.assertAlmostEqual(ay[0], 4400049.98 - 223.5, 6)
        self.assertEqual(_terrain.window_anchors(pts, 2, 32735, (0, 0))[1][0], 1900 + 4919 * 894)
        ax, ay = _terrain.window_anchors(pts, 2, 3857, (7, 9))   # a domain grapy does not know: the extent's corner
        self.assertEqual((ax[0], ay[0]), (7, 9))
        # BuildTerrain uses them: a 16 m window from 4399770 + 16k holds y 4400000..4400010, not one from 4400000.
        utm = arcpy.management.CreateFeatureDataset(self.gpkg, "utm", 32635)[0]
        xyz = [(500000.5, 4400000.5, 5.0), (500000.5, 4400009.5, 4.0), (500000.5, 4400010.5, 3.0)]
        p = self.points("u", xyz, dataset=utm)
        t = arcpy.ddd.CreateTerrain(utm, "ut", 2, None, None, "WINDOWSIZE", "ZMIN")[0]
        arcpy.ddd.AddTerrainPyramidLevel(t, "", ["16 1000"])
        arcpy.ddd.AddFeatureClassToTerrain(t, [[p, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False]])
        arcpy.ddd.BuildTerrain(t)
        out = arcpy.ddd.TerrainToPoints(t, os.path.join(self.gpkg, "ul"), 16, None, "POINT")[0]
        with da.SearchCursor(out, ["SHAPE@Z"]) as rows:
            self.assertEqual(sorted(r[0] for r in rows), [3.0, 4.0])   # windows [4399994, 4400010) and [4400010, ...)

    def test_each_level_chooses_from_full_resolution_and_keeps_the_coarser_levels_points(self):
        # ArcGIS Pro 3.7, measured 2026-10-31 (ADR-234 condition 4): a ZMEAN level's windows choose the point closest to
        # the mean of ALL the window's points, and a finer level keeps every coarser level's points as well. Thinning
        # from the level before keeps a different point at level 4 here.
        z = {(x, y): (x * 7 + y * 3) % 5 + 0.25 * x for x in range(4) for y in range(4)}
        order = [(x, y) for x in range(4) for y in range(4)]
        pts = self.points("m", [(x, y, z[(x, y)]) for x, y in order])
        source = [[pts, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False]]
        t = self.terrain(("2 1000", "4 5000"), "WINDOWSIZE", "ZMEAN", source, name="mean")

        def closest(members):
            d = np.abs(np.array([z[m] for m in members]) - np.mean([z[m] for m in members]))
            return members[int(np.nonzero(d <= d.min() + 1e-9)[0][0])]
        four = {closest(order)}
        two = {closest([m for m in order if (m[0] // 2, m[1] // 2) == w]) for w in [(0, 0), (0, 1), (1, 0), (1, 1)]}
        self.assertEqual({(p[0], p[1]) for p in self.level(t, 4)}, four)
        self.assertEqual({(p[0], p[1]) for p in self.level(t, 2)}, two | four)
        self.assertNotEqual(four, {closest(sorted(two, key=order.index))})   # what thinning level 2 would have kept

    def test_a_window_is_at_least_twice_the_average_point_spacing(self):
        # ArcGIS Pro 3.7's ERROR 050073, measured at spacings 0.5, 1, 1.5, 2, 3, 7 and 10: the smallest window is 2x.
        t = arcpy.ddd.CreateTerrain(self.fds, "spaced", 2, None, None, "WINDOWSIZE", "ZMIN")[0]
        with self.assertRaises(ExecuteError) as refused:
            arcpy.ddd.AddTerrainPyramidLevel(t, "", "3.9 1000")
        self.assertIn("050073", str(refused.exception))
        arcpy.ddd.AddTerrainPyramidLevel(t, "", "4 1000")
        z = arcpy.ddd.CreateTerrain(self.fds, "spacedz", 2, None, None, "ZTOLERANCE")[0]
        arcpy.ddd.AddTerrainPyramidLevel(z, "", "0.01 1000")   # a z-tolerance is a height, not bound by the spacing

    def test_projected_coordinates_lose_no_point_to_the_triangulation(self):
        # The comparison's points (tools/pro-compare/adr234-terrain-points.py): on UTM coordinates Qhull dropped 21 of 2,500 as
        # coplanar, and the z-tolerance level missed its own bound by 0.8 m. Triangulated from their corner, none is lost.
        rng = np.random.default_rng(234)
        x, y = np.round(rng.uniform(0, 100, 2500), 2), np.round(rng.uniform(0, 100, 2500), 2)
        z = np.round(50 + 8 * np.sin(x / 13.0) + 6 * np.cos(y / 9.0) + 0.05 * x + rng.normal(0, 0.6, 2500), 3)
        xyz = np.column_stack([500000.0 + x, 4400000.0 + y, z])
        from grapy import _tin
        self.assertEqual(len(np.unique(_tin._delaunay(xyz[:, :2]))), 2500)
        kept = _terrain.ztolerance_thin(xyz, np.zeros(2500, bool), 0.5)
        heights, _ = _terrain._interpolate(xyz[kept], xyz[:, :2])
        self.assertLessEqual(float(np.nanmax(np.abs(heights - z))), 0.5 + 1e-9)

    def test_a_plane_stays_a_plane_at_every_level_of_either_pyramid(self):
        for pyramid in ("WINDOWSIZE", "ZTOLERANCE"):
            t = self.terrain(("1 1000", "4 10000"), pyramid, name=f"p{pyramid}")
            for resolution in (0, 1, 4):
                raster = arcpy.ddd.TerrainToRaster(t, os.path.join(self.directory, f"{pyramid}{resolution}.tif"),
                                                   "FLOAT", "LINEAR", "CELLSIZE 1", resolution)[0]
                r = arcpy.Raster(raster)
                xs, ys = r._grid.centres()
                gx, gy = np.meshgrid(xs, ys)
                inside = ~r._mask
                self.assertTrue(inside.any())
                np.testing.assert_allclose(r._array[inside], plane(gx, gy)[inside], atol=1e-4)

    def test_z_tolerance_on_a_plane_keeps_only_the_hull_corners(self):
        t = self.terrain(("0.5 1000",), "ZTOLERANCE")
        self.assertEqual({(p[0], p[1]) for p in self.level(t, 0.5)}, {(0, 0), (10, 0), (0, 10), (10, 10)})

    def test_z_tolerance_keeps_a_bump_higher_than_the_tolerance_and_drops_a_lower_one(self):
        flat = [(x, y, 0.0) for x in range(11) for y in range(11) if (x, y) != (5, 5)] + [(5, 5, 3.0)]
        pts = self.points("bump", flat)
        source = [[pts, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False]]
        t = self.terrain(("1 1000", "5 10000"), "ZTOLERANCE", sources=source, name="bumpy")
        self.assertIn((5.0, 5.0, 3.0), self.level(t, 1))
        self.assertEqual(len(self.level(t, 5)), 4)

    def test_z_tolerance_holds_every_full_resolution_point_within_the_tolerance(self):
        rng = np.random.default_rng(11)
        xy = rng.uniform(0, 20, (300, 2))
        z = np.sin(xy[:, 0] / 3) * 4 + np.cos(xy[:, 1] / 2) * 3
        pts = self.points("rough", [tuple(p) for p in np.column_stack([xy, z])])
        source = [[pts, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False]]
        t = self.terrain(("0.5 1000", "2 5000"), "ZTOLERANCE", sources=source, name="roughterrain")
        for tolerance in (0.5, 2):
            kept = self.level(t, tolerance)
            self.assertLess(len(kept), 300)
            surface = _terrain.surface(t, tolerance)
            error = np.abs(surface.z_at(xy[:, 0], xy[:, 1]) - z)
            self.assertLessEqual(float(np.nanmax(error)), tolerance + 1e-9)

    def test_anchor_points_are_kept_at_every_level(self):
        anchor = self.points("anchor", [(3.5, 3.5, plane(3.5, 3.5))])
        t = self.terrain(("4 10000",), "WINDOWSIZE", "ZMIN", [
            [self.grid, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False],
            [anchor, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", True]], name="anchored")
        self.assertIn((3.5, 3.5, plane(3.5, 3.5)), self.level(t, 4))

    def test_a_breakline_is_enforced_only_within_its_resolution_bounds(self):
        line = arcpy.management.CreateFeatureclass(self.fds, "ridge", "POLYLINE", has_z="ENABLED")[0]
        with da.InsertCursor(line, ["SHAPE@"]) as cursor:
            cursor.insertRow([shapely.LineString([(0.5, 5.5, plane(0.5, 5.5)), (9.5, 5.5, plane(9.5, 5.5))])])
        t = self.terrain(("1 1000", "4 10000"), "WINDOWSIZE", "ZMIN", [
            [self.grid, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False],
            [line, "Shape.Z", "hardline", 2, 0, 1, False, False, "", "", False]], name="lined")

        def hard_edges(resolution):
            out = os.path.join(self.directory, f"tin{resolution}")
            arcpy.ddd.TerrainToTin(t, out, resolution)
            return len(arcpy.ddd.TinEdge(out, os.path.join(self.gpkg, f"e{resolution}"), "HARD") and
                       list(da.SearchCursor(os.path.join(self.gpkg, f"e{resolution}"), ["OID@"])))
        self.assertGreater(hard_edges(1), 0)
        self.assertEqual(hard_edges(4), 0)
        arcpy.ddd.ChangeTerrainResolutionBounds(t, "ridge", 0, 8)   # not invalidated, as documented
        self.assertTrue(arcpy.Describe(t).isBuilt)
        self.assertGreater(hard_edges(4), 0)
        with self.assertRaises(ExecuteError):
            arcpy.ddd.ChangeTerrainResolutionBounds(t, "grid", 0, 8)   # never for mass points

    # ---- levels and scales

    def test_levels_are_added_removed_and_rescaled(self):
        t = self.terrain()
        with self.assertRaises(ExecuteError):
            arcpy.ddd.RemoveTerrainPyramidLevel(t, 0)
        arcpy.ddd.ChangeTerrainReferenceScale(t, 10000, 24000)
        self.assertEqual(arcpy.Describe(t).pyramidLevels, [(1.0, 1000), (4.0, 24000)])
        arcpy.ddd.RemoveTerrainPyramidLevel(t, 4)
        self.assertEqual(arcpy.Describe(t).pyramidLevels, [(1.0, 1000)])
        with self.assertRaises(ExecuteError):
            self.level(t, 4)
        arcpy.ddd.AddTerrainPyramidLevel(t, "", "8 50000")
        self.assertFalse(arcpy.Describe(t).isBuilt)
        with self.assertRaises(ExecuteError):   # an invalidated terrain is not read until it is built
            self.level(t, 8)
        arcpy.ddd.BuildTerrain(t)
        self.assertEqual(len(self.level(t, 8)), 4)

    # ---- points

    def test_embedded_points_are_appended_replaced_and_deleted(self):
        extra = self.points("extra", [(x + 0.5, 0.5, 100.0) for x in range(4)], "MULTIPOINT")
        t = self.terrain(("4 10000",), "WINDOWSIZE", "ZMAX", [
            [self.grid, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False],
            [extra, "Shape.Z", "masspoints", 1, "", "", False, True, "spikes", "", False]], name="emb")
        arcpy.management.Delete(extra)   # an embedded class outlives its source, as documented
        self.assertEqual(sum(1 for p in self.level(t, 0) if p[2] == 100.0), 4)
        more = self.points("more", [(6.5, 6.5, 50.0), (9.5, 9.5, 60.0)])
        arcpy.ddd.AppendTerrainPoints(t, "spikes", more, arcpy.Extent(6, 6, 7, 7))
        self.assertFalse(arcpy.Describe(t).isBuilt)
        arcpy.ddd.BuildTerrain(t)
        self.assertIn((6.5, 6.5, 50.0), self.level(t, 0))
        self.assertNotIn((9.5, 9.5, 60.0), self.level(t, 0))
        arcpy.ddd.ReplaceTerrainPoints(t, "spikes", more)
        arcpy.ddd.BuildTerrain(t)
        spikes = {p for p in self.level(t, 0) if p[2] >= 50}
        self.assertEqual(spikes, {(6.5, 6.5, 50.0), (9.5, 9.5, 60.0)})
        arcpy.ddd.DeleteTerrainPoints(t, "spikes", "9 9 10 10")
        arcpy.ddd.BuildTerrain(t)
        self.assertEqual({p for p in self.level(t, 0) if p[2] >= 50}, {(6.5, 6.5, 50.0)})
        arcpy.ddd.RemoveTerrainPoints(t, "spikes", arcpy.Extent(6, 6, 7, 7))
        arcpy.ddd.BuildTerrain(t)
        self.assertEqual({p for p in self.level(t, 0) if p[2] >= 50}, set())
        with self.assertRaises(ExecuteError):   # the deprecated tool works only on embedded points
            arcpy.ddd.RemoveTerrainPoints(t, "grid", "0 0 1 1")
        only = arcpy.ddd.TerrainToPoints(t, os.path.join(self.gpkg, "only"), 0, "spikes")[0]
        self.assertEqual(len(list(da.SearchCursor(only, ["OID@"]))), 0)

    def test_points_of_a_referenced_class_are_edited_in_the_class(self):
        t = self.terrain()
        arcpy.ddd.DeleteTerrainPoints(t, "grid", "0 0 2 2")
        self.assertEqual(len(list(da.SearchCursor(self.grid, ["OID@"]))), 121 - 9)
        arcpy.ddd.BuildTerrain(t)
        self.assertEqual(len(self.level(t, 0)), 121 - 9)

    def test_removing_a_feature_class_deletes_only_embedded_points(self):
        extra = self.points("extra2", [(0.5, 0.5, 1.0)])
        t = self.terrain(("4 10000",), sources=[
            [self.grid, "Shape.Z", "masspoints", 1, "", "", False, False, "", "", False],
            [extra, "Shape.Z", "masspoints", 1, "", "", False, True, "", "", False]], name="rm")
        arcpy.ddd.RemoveFeatureClassFromTerrain(t, "extra2_embed")
        arcpy.ddd.RemoveFeatureClassFromTerrain(t, "grid")
        self.assertEqual(arcpy.Describe(t).dataSources, [])
        self.assertEqual(len(list(da.SearchCursor(self.grid, ["OID@"]))), 121)   # the referenced class stays

    # ---- reading a level

    def test_terrain_to_tin_and_points_answer_a_level(self):
        t = self.terrain(("4 10000",), "WINDOWSIZE", "ZMIN")
        tin = os.path.join(self.directory, "fromterrain")
        arcpy.ddd.TerrainToTin(t, tin, 4)
        self.assertEqual(arcpy.Describe(tin).nodeCount, 9)
        with self.assertRaises(ExecuteError):
            arcpy.ddd.TerrainToTin(t, os.path.join(self.directory, "big"), 0, 100)
        multi = arcpy.ddd.TerrainToPoints(t, os.path.join(self.gpkg, "multi"), 4)[0]
        rows = list(da.SearchCursor(multi, ["SHAPE@"]))
        self.assertEqual(len(rows), 1)
        self.assertEqual(len(rows[0][0]._shape.geoms), 9)
        arcpy.env.extent = arcpy.Extent(0, 0, 4.5, 4.5)
        try:
            clipped = os.path.join(self.directory, "clipped")
            arcpy.ddd.TerrainToTin(t, clipped, 0)
            self.assertEqual(arcpy.Describe(clipped).nodeCount, 25)
        finally:
            arcpy.env.extent = None

    def test_surface_tools_read_a_terrain_at_a_level(self):
        t = self.terrain(("4 10000",), "ZTOLERANCE")
        # z = x + 2y over 10 x 10: mean height 15, so 8500 below z = 100; 3D area 100 √6.
        out = os.path.join(self.directory, "volume.txt")
        arcpy.ddd.SurfaceVolume(t, out, "BELOW", 100, 1, 4)
        line = open(out, encoding="utf-8").read().splitlines()[1].split(",")
        self.assertEqual((float(line[-3]), float(line[-2]), float(line[-1])), (100.0, 244.95, 8500.0))
        lines = arcpy.management.CreateFeatureclass(self.gpkg, "lines", "POLYLINE", spatial_reference=3857)[0]
        polys = arcpy.management.CreateFeatureclass(self.gpkg, "polys", "POLYGON", spatial_reference=3857)[0]
        with da.InsertCursor(lines, ["SHAPE@"]) as cursor:
            cursor.insertRow([shapely.LineString([(1, 1), (9, 1)])])
        with da.InsertCursor(polys, ["SHAPE@"]) as cursor:
            cursor.insertRow([shapely.box(2, 2, 4, 4)])
        arcpy.ddd.AddSurfaceInformation(lines, t, "Z_MIN;Z_MAX;Z_MEAN;SURFACE_LENGTH;MIN_SLOPE;MAX_SLOPE;AVG_SLOPE",
                                        "LINEAR", None, 1, 4)
        values = list(da.SearchCursor(lines, ["Z_MIN", "Z_MAX", "Z_MEAN", "SURFACE_LENGTH", "MIN_SLOPE", "MAX_SLOPE",
                                              "AVG_SLOPE"]))[0]
        np.testing.assert_allclose(values, [3, 11, 7, math.sqrt(128), 100, 100, 100], atol=1e-9)
        arcpy.ddd.AddSurfaceInformation(polys, t, "Z_MIN;Z_MAX;Z_MEAN;SURFACE_AREA;AVG_SLOPE", "LINEAR", None, 1, 4)
        values = list(da.SearchCursor(polys, ["Z_MIN", "Z_MAX", "Z_MEAN", "SURFACE_AREA", "AVG_SLOPE"]))[0]
        np.testing.assert_allclose(values, [6, 12, 9, 4 * math.sqrt(6), 100 * math.sqrt(5)], atol=1e-9)
        out3d = arcpy.ddd.InterpolateShape(t, lines, os.path.join(self.gpkg, "lines3d"), None, 1, "LINEAR",
                                           "VERTICES_ONLY", 4)[0]
        shape = list(da.SearchCursor(out3d, ["SHAPE@"]))[0][0]._shape
        np.testing.assert_allclose(shapely.get_coordinates(shape, include_z=True)[:, 2], [3, 11])
        with self.assertRaises(ExecuteError):
            arcpy.ddd.SurfaceVolume(t, None, "BELOW", 100, 1, 3)   # not a level of the terrain

    def test_the_names_still_refused_give_their_own_reasons(self):
        for name in ("AddTerrainPoints", "BIMToTerrain"):
            with self.assertRaises(NotBuiltError) as caught:
                getattr(arcpy.ddd, name)()
            self.assertNotIn("not built in grapy yet", str(caught.exception))
            self.assertIn("ADR-234", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
