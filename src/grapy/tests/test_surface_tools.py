"""Surface Parameters, Zonal Fill, Observer Points, Viewshed 2 and Spline with Barriers — ADR-243.

Rasters are written north to south with 10-unit cells on EPSG:3857, the top-left corner at (0, rows·10), so the cell
in row r and column k has its centre at (5 + 10k, rows·10 − 5 − 10r).
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import _splinebarriers, _surfaceparams, _viewshed2, _zonalfill  # noqa: E402

ND = -1


def grid(values, cell=10.0, nodata=None):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    try:
        return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0.0, 0.0), cell, cell, nodata)
    finally:
        arcpy.env.outputCoordinateSystem = None


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), arcpy.SpatialReference(3857))


def line(*xy):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x, y) for x, y in xy]), arcpy.SpatialReference(3857))


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def features(self, name, shape, rows, fields=()):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path


def surface(function, size=9, cell=10.0):
    """A size × size raster of function(x, y), x east and y north of the centre cell's centre."""
    r, c = np.indices((size, size))
    x, y = (c - size // 2) * cell, -(r - size // 2) * cell
    return grid(function(x, y).astype(float), cell)


class SurfaceParameters(_Fixture):
    def test_slope_and_aspect_of_a_plane(self):
        plane = surface(lambda x, y: 0.5 * x + 0.2 * y)
        slope = cells(_surfaceparams.SurfaceParameters(plane, "SLOPE"))
        self.assertAlmostEqual(slope[4][4], math.degrees(math.atan(math.hypot(0.5, 0.2))), places=4)
        # The 3 × 3 window leaves the edge cells without a neighbour: NoData, as documented.
        self.assertEqual(slope[0][0], ND)
        percent = cells(_surfaceparams.SurfaceParameters(plane, "SLOPE", output_slope_measurement="PERCENT_RISE"))
        self.assertAlmostEqual(percent[4][4], 100 * math.hypot(0.5, 0.2), places=3)
        # Rising east and north, it faces down to the south-west: atan2(−0.5, −0.2) = 248.2°.
        aspect = cells(_surfaceparams.SurfaceParameters(plane, "ASPECT"))
        self.assertAlmostEqual(aspect[4][4], math.degrees(math.atan2(-0.5, -0.2)) % 360, places=3)
        flat = cells(_surfaceparams.SurfaceParameters(surface(lambda x, y: 0 * x + 7), "ASPECT"))
        self.assertEqual(flat[4][4], -1)

    def test_the_neighbourhood_distance_widens_the_window(self):
        plane = surface(lambda x, y: 0.5 * x)
        # 25 units on 10-unit cells rounds up to 3 cells: a 7 × 7 window, three cells lost at each edge.
        slope = cells(_surfaceparams.SurfaceParameters(plane, neighborhood_distance="25 Meters"))
        self.assertEqual(slope[2][4], ND)
        self.assertAlmostEqual(slope[3][4], math.degrees(math.atan(0.5)), places=4)

    def test_curvatures_of_a_paraboloid_cap(self):
        # z = −(x² + y²)/(2R): at the top the surface is a cap of radius R, mean curvature 1/R and Gaussian 1/R².
        radius = 1000.0
        cap = surface(lambda x, y: -(x * x + y * y) / (2 * radius))
        mean = cells(_surfaceparams.SurfaceParameters(cap, "MEAN_CURVATURE"))[4][4]
        gaussian = cells(_surfaceparams.SurfaceParameters(cap, "GAUSSIAN_CURVATURE"))[4][4]
        self.assertAlmostEqual(mean, 1 / radius, places=7)
        self.assertAlmostEqual(gaussian, 1 / radius ** 2, places=10)
        self.assertAlmostEqual(cells(_surfaceparams.SurfaceParameters(cap, "CASORATI_CURVATURE"))[4][4], 1 / radius,
                               places=7)

    def test_the_curvatures_obey_minars_identities(self):
        # The basic trio: mean = (profile + tangential)/2 and Gaussian = tangential · profile − torsion²
        # (Minár, Evans and Jenčo 2020).
        values = surface(lambda x, y: 0.3 * x + 0.1 * y + 0.001 * x * x - 0.002 * y * y + 0.0015 * x * y)
        got = {kind: cells(_surfaceparams.SurfaceParameters(values, kind))[4][4] for kind in (
            "MEAN_CURVATURE", "PROFILE_CURVATURE", "TANGENTIAL_CURVATURE", "GAUSSIAN_CURVATURE",
            "CONTOUR_GEODESIC_TORSION")}
        self.assertAlmostEqual(got["MEAN_CURVATURE"], (got["PROFILE_CURVATURE"] + got["TANGENTIAL_CURVATURE"]) / 2,
                               delta=1e-7)
        self.assertAlmostEqual(got["GAUSSIAN_CURVATURE"], got["TANGENTIAL_CURVATURE"] * got["PROFILE_CURVATURE"]
                               - got["CONTOUR_GEODESIC_TORSION"] ** 2, delta=1e-9)

    def test_slope_aspect_helper_and_adaptive_window(self):
        r, c = np.indices((9, 9))
        z = 0.5 * (c - 4) * 10.0
        slope, aspect = _surfaceparams.slope_aspect(z, np.zeros(z.shape, bool), 10.0, "30", adaptive=True)
        self.assertAlmostEqual(slope[4, 4], math.degrees(math.atan(0.5)), places=6)
        self.assertAlmostEqual(aspect[4, 4], 270.0, places=6)
        self.assertTrue(math.isnan(slope[0, 4]))

    def test_a_geographic_raster_is_refused(self):
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(4326)
        try:
            raster = arcpy.NumPyArrayToRaster(np.zeros((3, 3)), arcpy.Point(0.0, 0.0), 0.1, 0.1)
        finally:
            arcpy.env.outputCoordinateSystem = None
        with self.assertRaises(arcpy.NotBuiltError):
            _surfaceparams.SurfaceParameters(raster)


class ZonalFill(_Fixture):
    def test_each_zone_takes_the_least_weight_on_its_boundary(self):
        zones = grid([[1, 1, 1, 1], [1, 1, 1, 1], [1, 1, 1, 1], [1, 1, 1, 2]])
        weights = grid([[9, 9, 9, 9], [9, 0, 0, 9], [9, 0, 6, 9], [9, 9, 9, 3]])
        # Zone 1's boundary is its outer ring and the cells beside zone 2; the zeros inside it are not on it, so the
        # least is the 6 next to zone 2. Zone 2 is all boundary.
        self.assertEqual(cells(_zonalfill.ZonalFill(zones, weights)),
                         [[6, 6, 6, 6], [6, 6, 6, 6], [6, 6, 6, 6], [6, 6, 6, 3]])


class Visibility(_Fixture):
    def test_observer_points_encode_which_observers_see_each_cell(self):
        ridge = grid([[0.0, 0.0, 10.0, 0.0, 0.0]])
        observers = self.features("obs", "POINT", [(point(5, 5),), (point(45, 5),)])
        out = _viewshed2.ObserverPoints(ridge, observers)
        # Observer 1 (bit 1) sees the west up to the ridge, observer 2 (bit 2) the east; both see the ridge.
        self.assertEqual(cells(out), [[1, 1, 3, 2, 2]])
        self.assertEqual(out._rat.rows, [[1, 2, 1, 0], [2, 2, 0, 1], [3, 1, 1, 1]])
        self.assertEqual([f[0] for f in out._rat.fields], ["Value", "Count", "OBS1", "OBS2"])

    def test_viewshed2_honours_its_radii_and_units(self):
        ridge = grid([[0.0, 0.0, 10.0, 0.0, 0.0]])
        observer = self.features("one", "POINT", [(point(5, 5),)])
        self.assertEqual(cells(_viewshed2.Viewshed2(ridge, observer)), [[1, 1, 1, 0, 0]])
        self.assertEqual(cells(_viewshed2.Viewshed2(ridge, observer, outer_radius="15 Meters")), [[1, 1, 0, 0, 0]])
        # Nearer than the inner radius is not visible, though it still blocks.
        self.assertEqual(cells(_viewshed2.Viewshed2(ridge, observer, inner_radius=15)), [[1, 0, 1, 0, 0]])
        # An eye 100 feet (30.48 m) up sees over the ridge: the cell at 30 needs an eye of at least 30, from
        # −h/30 ≥ (10 − h)/20, and the cell at 40 one of at least 20.
        high = _viewshed2.Viewshed2(ridge, observer, observer_offset="100 Feet")
        self.assertEqual(cells(high), [[1, 1, 1, 1, 1]])
        with self.assertRaises(arcpy.NotBuiltError):
            _viewshed2.Viewshed2(ridge, observer, analysis_type="OBSERVERS")


class Spline(_Fixture):
    def test_a_plane_is_reproduced(self):
        rows = [(0.5 * x + 0.25 * y + 3, point(x, y)) for x, y in ((0, 0), (100, 0), (0, 100), (100, 100), (40, 70))]
        points = self.features("plane", "POINT", rows, [("z", "DOUBLE")])
        out = _splinebarriers.SplineWithBarriers(points, "z", Output_cell_size=10)
        xs, ys = out._grid.centres()
        want = 0.5 * xs[None, :] + 0.25 * ys[:, None] + 3
        np.testing.assert_allclose(out._array, want, atol=1e-2)

    def test_a_barrier_cuts_the_surface(self):
        rows = [(0.0, point(x, y)) for x in (0, 30) for y in (0, 50, 100)] + \
               [(10.0, point(x, y)) for x in (70, 100) for y in (0, 50, 100)]
        points = self.features("sides", "POINT", rows, [("z", "DOUBLE")])
        wall = self.features("wall", "POLYLINE", [(line((50, -10), (50, 110)),)])
        cut = _splinebarriers.SplineWithBarriers(points, "z", wall, Output_cell_size=10)._array
        whole = _splinebarriers.SplineWithBarriers(points, "z", None, Output_cell_size=10)._array
        # With the wall each side is flat at its own value; without it the surface climbs across the middle.
        np.testing.assert_allclose(cut[:, :5], 0.0, atol=1e-2)
        np.testing.assert_allclose(cut[:, 5:], 10.0, atol=1e-2)
        self.assertTrue(0.5 < whole[5, 4] < 9.5)


if __name__ == "__main__":
    unittest.main()
