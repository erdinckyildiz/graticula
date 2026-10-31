"""ADR-244: the arcpy.sa and arcpy.ddd tools whose pages describe an algorithm without specifying it, built as
equivalents — every expected value below is worked out by hand.

Rasters are written north to south with 10-unit cells on EPSG:3857, the lower-left corner at (0, 0), so in a raster of
R rows the cell in row r and column k has its centre at (5 + 10k, 10R − 5 − 10r). At the equator Web Mercator's scale
is 1, so its metres are ground metres.
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ddd, sa  # noqa: E402

ND = -1


def grid(values, nodata=None, srid=3857):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(srid)
    return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0.0, 0.0), 10.0, 10.0, nodata)


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


def line(*xys):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x, y) for x, y in xys]), arcpy.SpatialReference(3857))


def polygon(*xys):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y) for x, y in xys]), arcpy.SpatialReference(3857))


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def features(self, name, shape, rows, fields=()):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def table(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as rows:
            return [list(r) for r in rows]

    def shapes(self, path, fields=()):
        with arcpy.da.SearchCursor(path, [*fields, "SHAPE@"]) as rows:
            return [(list(r[:-1]), r[-1]._shape) for r in rows]


RIDGE = [[0.0, 0.0, 10.0, 0.0, 0.0]]


class Visibility(_Fixture):
    def test_observer_points_encodes_each_observer_and_writes_obs_fields(self):
        # From the west end the ridge hides the two eastern cells (sa.Viewshed's own case); from the east end, by
        # symmetry, the two western ones. Observer 1 has bit 1 and observer 2 bit 2.
        observers = self.features("obs", "POINT", [(point(5, 5),), (point(45, 5),)])
        agl = os.path.join(self.directory, "agl.tif")
        out = sa.ObserverPoints(grid(RIDGE), observers, out_agl_raster=agl)
        self.assertEqual(cells(out), [[1, 1, 3, 2, 2]])
        rat = out._rat
        self.assertEqual(rat.names(), ["Value", "Count", "OBS1", "OBS2"])
        self.assertEqual(rat.rows, [[1, 2, 1, 0], [2, 2, 0, 1], [3, 1, 1, 1]])
        # Every cell is seen by one of them, so no cell needs any height.
        self.assertEqual(cells(arcpy.Raster(agl)), [[0.0] * 5])
        saved = os.path.join(self.directory, "op.tif")
        ddd.ObserverPoints(grid(RIDGE), observers, saved)
        self.assertEqual(cells(arcpy.Raster(saved)), [[1, 1, 3, 2, 2]])

    def test_observer_points_allows_sixteen(self):
        many = self.features("many", "POINT", [(point(5, 5),)] * 17)
        with self.assertRaises(ValueError):
            sa.ObserverPoints(grid(RIDGE), many)

    def test_geodesic_viewshed_steps_along_the_ground_path(self):
        # The eye at 0 + 1. To the cell at x = 35 the line is cut into 3 steps; at t = 2/3 it stands at
        # 1 − 2/3 = 0.333 over the ridge's 10, so the cell is hidden and must rise 9.667 / (2/3) = 14.5; to x = 45,
        # at t = 1/2 the line is at 0.5 and must rise 9.5 / 0.5 = 19. The Earth's curvature over 40 m is 0.0001 m.
        observer = self.features("one", "POINT", [(point(5, 5),)])
        agl = os.path.join(self.directory, "agl2.tif")
        self.assertEqual(cells(sa.Viewshed2(grid(RIDGE), observer, out_agl_raster=agl)), [[1, 1, 1, 0, 0]])
        got = cells(arcpy.Raster(agl))[0]
        for value, expected in zip(got, [0.0, 0.0, 0.0, 14.5, 19.0]):
            self.assertAlmostEqual(value, expected, delta=0.01)
        # A ground radius of 15 keeps the two nearest cells.
        self.assertEqual(cells(sa.Viewshed2(grid(RIDGE), observer, outer_radius=15)), [[1, 1, 0, 0, 0]])
        # Looking only east of north-east (azimuth 45 to 135) keeps everything here; 180–360 keeps only the eye's cell.
        self.assertEqual(cells(sa.Viewshed2(grid(RIDGE), observer, horizontal_start_angle=180,
                                            horizontal_end_angle=360)), [[1, 0, 0, 0, 0]])
        with self.assertRaises(arcpy.NotBuiltError):
            sa.Viewshed2(grid(RIDGE), observer, vertical_error=1)

    def test_geodesic_viewshed_observers_and_its_relationship_table(self):
        observers = self.features("two", "POINT", [(point(5, 5),), (point(45, 5),)])
        table = os.path.join(self.directory, "regions.gpkg")
        out = sa.Viewshed2(grid(RIDGE), observers, analysis_type="OBSERVERS",
                           out_observer_region_relationship_table=table)
        self.assertEqual(cells(out), [[1, 1, 3, 2, 2]])
        self.assertEqual(sorted(self.table(table, ["Observer", "Region"])), [[1, 1], [1, 3], [2, 2], [2, 3]])
        saved = os.path.join(self.directory, "v2.tif")
        ddd.Viewshed2(grid(RIDGE), observers, saved, analysis_type="OBSERVERS")
        self.assertEqual(cells(arcpy.Raster(saved)), [[1, 1, 3, 2, 2]])


class Zonal(_Fixture):
    def test_zonal_fill_takes_the_lowest_weight_on_each_zone_boundary(self):
        # Zone 2 is the 3 × 3 block in the middle: its eight outer cells touch zone 1, its centre does not, so the
        # centre's weight 0 (a sink) is ignored and the zone fills to 5, the lowest of its ring. Zone 1 is all
        # boundary; its lowest weight is 20.
        zones = [[1] * 5, [1, 2, 2, 2, 1], [1, 2, 2, 2, 1], [1, 2, 2, 2, 1], [1] * 5]
        weight = [[30, 31, 32, 33, 34], [35, 6, 7, 8, 36], [37, 9, 0, 10, 20], [38, 11, 5, 12, 39], [40] * 5]
        out = sa.ZonalFill(grid(zones), grid(weight))
        self.assertTrue(out.isInteger)
        expected = [[20] * 5, [20, 5, 5, 5, 20], [20, 5, 5, 5, 20], [20, 5, 5, 5, 20], [20] * 5]
        self.assertEqual(cells(out), expected)
        # A NoData zone cell stays NoData.
        zones[0][0] = ND
        self.assertEqual(cells(sa.ZonalFill(grid(zones, nodata=ND), grid(weight)))[0][:2], [ND, 20])


class Surface(_Fixture):
    def test_slope_and_aspect_of_a_plane(self):
        # z = 2x + y: slope arctan √5 = 65.905°, 223.607 percent; downhill (−2, −1) is 243.435° from north.
        plane = grid([[2 * (5 + 10 * k) + (35 - 10 * r) for k in range(4)] for r in range(4)])
        self.assertAlmostEqual(cells(sa.SurfaceParameters(plane))[1][1], math.degrees(math.atan(math.sqrt(5))),
                               places=3)
        self.assertAlmostEqual(cells(sa.SurfaceParameters(plane, output_slope_measurement="PERCENT_RISE"))[1][2],
                               100 * math.sqrt(5), places=2)
        # Web Mercator's grid north is true north, so geodesic and projected azimuths agree.
        for projected in ("GEODESIC_AZIMUTHS", "PROJECT_GEODESIC_AZIMUTHS"):
            aspect = cells(sa.SurfaceParameters(plane, "ASPECT", project_geodesic_azimuths=projected))
            self.assertAlmostEqual(aspect[2][1], 243.435, places=3)
        for kind in ("MEAN_CURVATURE", "PROFILE_CURVATURE", "GAUSSIAN_CURVATURE", "CASORATI_CURVATURE"):
            self.assertAlmostEqual(cells(sa.SurfaceParameters(plane, kind))[1][1], 0.0, delta=1e-6)

    def test_curvatures_of_a_valley(self):
        # z = 0.01·(x − 25)²: at x = 25 the surface is flat with r = 0.02, so mean = −r/2 = −0.01 (concave, negative),
        # Gaussian 0 and Casorati √(2·0.01²) = 0.014142. At x = 35, p = 0.2: profile curvature
        # −r·p²/(p²·(1 + p²)^1.5) = −0.02/1.04^1.5 = −0.018858, tangential 0.
        valley = grid([[0.01 * (5 + 10 * k - 25) ** 2 for k in range(5)] for _ in range(4)])
        at = (1, 2)
        self.assertAlmostEqual(cells(sa.SurfaceParameters(valley, "MEAN_CURVATURE"))[at[0]][at[1]], -0.01, delta=1e-6)
        self.assertAlmostEqual(cells(sa.SurfaceParameters(valley, "GAUSSIAN_CURVATURE"))[1][2], 0.0, delta=1e-9)
        self.assertAlmostEqual(cells(sa.SurfaceParameters(valley, "CASORATI_CURVATURE"))[1][2], 0.0141421, delta=1e-6)
        self.assertAlmostEqual(cells(sa.SurfaceParameters(valley, "PROFILE_CURVATURE"))[1][3], -0.02 / 1.04 ** 1.5,
                               delta=1e-6)
        self.assertAlmostEqual(cells(sa.SurfaceParameters(valley, "TANGENTIAL_CURVATURE"))[1][3], 0.0, delta=1e-9)
        # The biquadratic surface is exact on a 3 × 3 window, so it agrees.
        self.assertAlmostEqual(cells(sa.SurfaceParameters(valley, "MEAN_CURVATURE", "BIQUADRATIC"))[1][2], -0.01,
                               delta=1e-6)
        # A 25-unit neighbourhood rounds up to a 7 × 7 window, which still fits the quadratic exactly.
        big = grid([[0.01 * (5 + 10 * k - 45) ** 2 for k in range(9)] for _ in range(8)])
        self.assertAlmostEqual(cells(sa.SurfaceParameters(big, "MEAN_CURVATURE", neighborhood_distance="25 Meters"))
                               [4][4], -0.01, delta=1e-6)
        saved = os.path.join(self.directory, "sp.tif")
        ddd.SurfaceParameters(valley, saved, "MEAN_CURVATURE")
        self.assertAlmostEqual(cells(arcpy.Raster(saved))[1][2], -0.01, delta=1e-6)

    def test_refused_values(self):
        plane = grid([[1.0, 2.0, 3.0]] * 3)
        with self.assertRaises(arcpy.NotBuiltError):
            sa.SurfaceParameters(plane, use_adaptive_neighborhood="ADAPTIVE_NEIGHBORHOOD")
        with self.assertRaises(arcpy.NotBuiltError):
            sa.SurfaceParameters(plane, "ASPECT", use_equatorial_aspect="EQUATORIAL_ASPECT")
        with self.assertRaises(arcpy.NotBuiltError):
            sa.SurfaceParameters(grid([[1.0, 2.0, 3.0]] * 3, srid=4326))


class Barriers(_Fixture):
    def setUp(self):
        super().setUp()
        self.points = self.features("pts", "POINT", [(0.0, point(0, 0)), (100.0, point(100, 0))], [("z", "DOUBLE")])
        self.wall = self.features("wall", "POLYLINE", [(line((50, -10), (50, 10)),)])

    def test_spline_with_barriers_is_free_across_the_barrier(self):
        # Without a barrier the surface honours both points and climbs between them.
        free = cells(sa.SplineWithBarriers(self.points, "z", None, 10))[0]
        self.assertAlmostEqual(free[0], 0.0, delta=1e-6)
        self.assertAlmostEqual(free[-1], 100.0, delta=1e-4)
        self.assertTrue(all(a < b for a, b in zip(free, free[1:])))
        # With the wall each side holds one point, and the least-curvature surface through one value is flat.
        cut = cells(sa.SplineWithBarriers(self.points, "z", self.wall, 10))[0]
        for value, expected in zip(cut, [0.0] * 5 + [100.0] * 5):
            self.assertAlmostEqual(value, expected, delta=1e-6)
        # Full smoothing leaves the surface tied only to the points' mean.
        for value in cells(sa.SplineWithBarriers(self.points, "z", None, 10, 1.0))[0]:
            self.assertAlmostEqual(value, 50.0, delta=1e-3)
        saved = os.path.join(self.directory, "swb.tif")
        ddd.SplineWithBarriers(self.points, "z", self.wall, 10, saved)
        self.assertAlmostEqual(cells(arcpy.Raster(saved))[0][9], 100.0, delta=1e-6)

    def test_contour_with_barriers_does_not_contour_across_the_barrier(self):
        # Values rise 10 per cell eastward: with base 5 and interval 10, contour 5 lies at x = 10, 15 at x = 20 and 25
        # at x = 30; the explicit 12 at x = 17. An indexed interval of 20 marks 5 and 25 as indexed.
        surface = grid([[0, 10, 20, 30], [0, 10, 20, 30]])
        out = os.path.join(self.directory, "c1.gpkg")
        sa.ContourWithBarriers(surface, out, in_base_contour=5, in_contour_interval=10, in_indexed_contour_interval=20,
                               in_contour_list=[12])
        got = sorted((v, t, round(s.centroid.x, 6)) for (v, t), s in self.shapes(out, ["Contour", "Type"]))
        self.assertEqual(got, [(5.0, 2, 10.0), (12.0, 3, 17.0), (15.0, 1, 20.0), (25.0, 2, 30.0)])
        # A barrier at x = 20 separates the second and third columns: nothing is contoured across it.
        wall = self.features("wall2", "POLYLINE", [(line((20, -5), (20, 25)),)])
        out2 = os.path.join(self.directory, "c2.gpkg")
        ddd.ContourWithBarriers(surface, out2, wall, in_base_contour=5, in_contour_interval=10,
                                in_indexed_contour_interval=20, in_contour_list=[12])
        self.assertEqual(sorted(v for (v,), _ in self.shapes(out2, ["Contour"])), [5.0, 25.0])
        # The documented values file: a comment, base/interval/indexed, and an explicit value.
        values = os.path.join(self.directory, "values.txt")
        with open(values, "w", encoding="utf-8") as handle:
            handle.write("# levels\n5 10 20\n12\n")
        out3 = os.path.join(self.directory, "c3.gpkg")
        sa.ContourWithBarriers(surface, out3, in_contour_values_file=values)
        self.assertEqual(sorted((v, t) for (v, t), _ in self.shapes(out3, ["Contour", "Type"])),
                         [(5.0, 2), (12.0, 3), (15.0, 1), (25.0, 2)])
        with self.assertRaises(arcpy.NotBuiltError):
            sa.ContourWithBarriers(surface, out3, in_contour_type="POLYGONS", in_contour_interval=10)


def _one(shape):
    return shape.geoms[0] if shape.geom_type == "MultiPolygon" else shape


def _angles(shape):
    shape = _one(shape)
    ring = list(shape.exterior.coords)[:-1]
    out = []
    for i, p in enumerate(ring):
        q = ring[(i + 1) % len(ring)]
        out.append(math.degrees(math.atan2(q[1] - p[1], q[0] - p[0])) % 180)
    return out


class Footprints(_Fixture):
    def test_right_angles_squares_a_rough_rectangle(self):
        rough = self.features("rough", "POLYGON", [(7, polygon((0, 0), (10, 0.2), (20, 0), (20.1, 10), (0, 10), (0, 0)))], [("kind", "LONG")])
        out = os.path.join(self.directory, "reg.gpkg")
        ddd.RegularizeBuildingFootprint(rough, out, "RIGHT_ANGLES", 0.5)
        [((kind, status), shape)] = self.shapes(out, ["kind", "STATUS"])
        self.assertEqual((kind, status), (7, 0))
        self.assertEqual(len(_one(shape).exterior.coords) - 1, 4)
        base = _angles(shape)[0] % 90
        for angle in _angles(shape):
            self.assertAlmostEqual(min((angle - base) % 90, 90 - (angle - base) % 90), 0.0, delta=1e-6)
        self.assertAlmostEqual(shape.area, 200.0, delta=4.0)

    def test_a_rotated_rectangle_keeps_its_orientation(self):
        c, s = math.cos(math.radians(30)), math.sin(math.radians(30))
        corners = [(0, 0), (20, 0.3), (20, 10), (0, 10)]
        turned = [(x * c - y * s, x * s + y * c) for x, y in corners]
        source = self.features("turned", "POLYGON", [(polygon(*turned, turned[0]),)])
        out = os.path.join(self.directory, "turned_out.gpkg")
        ddd.RegularizeBuildingFootprint(source, out, "RIGHT_ANGLES", 0.5)
        [(_, shape)] = self.shapes(out)
        for angle in _angles(shape):
            self.assertAlmostEqual(min((angle - 30) % 90, 90 - (angle - 30) % 90), 0.0, delta=0.5)

    def test_diagonals_keep_a_chamfered_corner(self):
        chamfer = self.features("chamfer", "POLYGON", [(polygon((0, 0), (20, 0), (20, 10), (10, 20), (0, 20), (0, 0)),)])
        out = os.path.join(self.directory, "diag.gpkg")
        ddd.RegularizeBuildingFootprint(chamfer, out, "RIGHT_ANGLES_AND_DIAGONALS", 0.5)
        [(_, shape)] = self.shapes(out)
        self.assertIn(135.0, [round(a, 6) for a in _angles(shape)])
        self.assertAlmostEqual(shape.area, 350.0, delta=1e-6)
        square = os.path.join(self.directory, "square.gpkg")
        # Under RIGHT_ANGLES the diagonal turns level through its midpoint (15, 15) and meets the roof y = 20 by a
        # step at x = 10: (20, 15), (10, 15), (10, 20) — the area stays 20·15 + 10·5 = 350. At a tolerance of 0.5 that
        # step is too far from the original, so the footprint is copied with STATUS 1.
        ddd.RegularizeBuildingFootprint(chamfer, square, "RIGHT_ANGLES", 2.0)
        [((status,), shape)] = self.shapes(square, ["STATUS"])
        self.assertEqual(status, 0)
        self.assertNotIn(135.0, [round(a, 6) for a in _angles(shape)])
        self.assertAlmostEqual(_one(shape).area, 350.0, delta=1e-6)
        ddd.RegularizeBuildingFootprint(chamfer, square, "RIGHT_ANGLES", 0.5)
        self.assertEqual(self.shapes(square, ["STATUS"])[0][0], [1])

    def test_circle_is_kasas_least_squares_circle(self):
        ring = [(50 + 10 * math.cos(2 * math.pi * k / 32), 50 + 10 * math.sin(2 * math.pi * k / 32)) for k in range(32)]
        source = self.features("round", "POLYGON", [(polygon(*ring, ring[0]),)])
        out = os.path.join(self.directory, "circle.gpkg")
        ddd.RegularizeBuildingFootprint(source, out, "CIRCLE", 1.0, min_radius=5, max_radius=20)
        [((status,), shape)] = self.shapes(out, ["STATUS"])
        self.assertEqual(status, 0)
        self.assertAlmostEqual(shape.centroid.x, 50.0, delta=1e-6)
        radius = max(math.dist((50, 50), p) for p in _one(shape).exterior.coords)
        self.assertAlmostEqual(radius, 10.0, delta=1e-6)
        # A maximum radius of 5 cannot be met, so the original is kept with STATUS 1.
        kept = os.path.join(self.directory, "kept.gpkg")
        ddd.RegularizeBuildingFootprint(source, kept, "CIRCLE", 1.0, min_radius=1, max_radius=5)
        [((status,), shape)] = self.shapes(kept, ["STATUS"])
        self.assertEqual((status, len(_one(shape).exterior.coords) - 1), (1, 32))

    def test_adjacent_footprints_keep_their_shared_boundary(self):
        # Two buildings share a rough wall (10, 0)–(10.2, 5)–(10, 10); the third is alone in its own group.
        rows = [(1, polygon((0, 0), (10, 0), (10.2, 5), (10, 10), (0, 10), (0, 0))),
                (1, polygon((10, 0), (20, 0), (20, 10), (10, 10), (10.2, 5), (10, 0))),
                (2, polygon((40, 0), (50, 0.2), (50, 10), (40, 10), (40, 0)))]
        source = self.features("row", "POLYGON", rows, [("block", "LONG")])
        out = os.path.join(self.directory, "adjacent.gpkg")
        ddd.RegularizeAdjacentBuildingFootprint(source, "block", out, "RIGHT_ANGLES", 0.5)
        got = self.shapes(out, ["block", "STATUS"])
        self.assertEqual([v for v, _ in got], [[1, 0], [1, 0], [2, 0]])
        a, b = _one(got[0][1]), _one(got[1][1])
        self.assertAlmostEqual(a.intersection(b).area, 0.0, delta=1e-9)
        self.assertAlmostEqual(a.intersection(b).length, 10.0, delta=0.5)
        self.assertAlmostEqual(a.union(b).area, a.area + b.area, delta=1e-6)
        for shape in (a, b):
            self.assertEqual(len(shape.exterior.coords) - 1, 4)


class Profile(_Fixture):
    def test_stack_profile_samples_at_cell_edges(self):
        # z = x on a 3 × 3 grid; the line y = 15 from x = 5 to 25 crosses the cell edges x = 10 and x = 20.
        surface = os.path.join(self.directory, "dem.tif")
        grid([[5.0, 15.0, 25.0]] * 3).save(surface)
        lines = self.features("prof", "POLYLINE", [(line((5, 15), (25, 15)),)])
        table = os.path.join(self.directory, "profile.gpkg")
        legacy = os.path.join(self.directory, "legacy.gpkg")
        ddd.StackProfile(lines, [surface], table, out_legacy_table=legacy)
        rows = self.table(table, ["DISTANCE", "Z", "LINE_ID", "SRC_NAME", "LINE_TARG"])
        self.assertEqual([[round(d, 6), round(z, 6)] for d, z, *_ in rows], [[0, 5], [5, 10], [15, 20], [20, 25]])
        self.assertEqual(rows[0][2:], [1, "dem", "1 dem"])
        self.assertEqual([[round(v, 6) for v in r] for r in self.table(legacy, ["FIRST_DIST", "FIRST_Z", "SEC_DIST",
                                                                               "SEC_Z"])],
                         [[0, 5, 5, 10], [5, 10, 15, 20], [15, 20, 20, 25]])
        with self.assertRaises(arcpy.NotBuiltError):
            ddd.StackProfile(lines, [surface], table, out_graph="chart")


class StillRefused(unittest.TestCase):
    def test_topo_to_raster_names_its_own_reason(self):
        for module in (sa, ddd):
            for name in ("TopoToRaster", "TopoToRasterByFile"):
                with self.assertRaises(arcpy.NotBuiltError) as said:
                    getattr(module, name)()
                self.assertIn("ANUDEM 5.3", str(said.exception))


if __name__ == "__main__":
    unittest.main()
