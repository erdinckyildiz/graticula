"""Topo to Raster, Segment Mean Shift and its dependents, and the combinatorial tools — ADR-242.

Rasters are written north to south with 10-unit cells on EPSG:3857, the top-left corner at (0, rows·10).
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ddd, ia, sa  # noqa: E402
from grapy._topo import _pits  # noqa: E402

ND = -1


def grid(values, cell=10.0, nodata=None):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0.0, 0.0), cell, cell, nodata)


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)

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

    def read(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as rows:
            return [tuple(r) for r in rows]


# *How the combinatorial math tools work*'s example: two 4 × 4 rasters, listed cell by cell, left to right and top to
# bottom; -1 is NoData.
FIRST = [[1, 1, 0, 0], [-1, 1, 2, 2], [4, 0, 0, 2], [4, 0, 1, 1]]
SECOND = [[0, 1, 1, 0], [3, 3, 1, 2], [-1, 0, 0, 2], [3, 2, 1, 0]]


class Combinatorial(_Fixture):
    def test_the_pages_worked_example(self):
        a, b = grid(FIRST, nodata=-1), grid(SECOND, nodata=-1)
        # The page's Returned Value table, put back cell by cell.
        self.assertEqual(cells(sa.CombinatorialAnd(a, b)),
                         [[0, 1, 0, 0], [ND, 2, 3, 4], [ND, 0, 0, 4], [5, 0, 1, 0]])
        self.assertEqual(cells(sa.CombinatorialOr(a, b)),
                         [[1, 2, 3, 0], [ND, 4, 5, 6], [ND, 0, 0, 6], [7, 8, 2, 1]])
        self.assertEqual(cells(sa.CombinatorialXOr(a, b)),
                         [[1, 0, 2, 0], [ND, 0, 0, 0], [ND, 0, 0, 0], [0, 3, 0, 1]])

    def test_the_parentage_table_and_counts(self):
        out = sa.CombinatorialOr(grid(FIRST, nodata=-1), grid(SECOND, nodata=-1))
        rows = {r[0]: r[1:] for r in out._rat.rows}
        # The page's counts: (1,0) twice, (1,1) twice, (0,1) once, (0,0) three times (all false, value 0) …
        self.assertEqual(rows[1], [2, 1, 0])
        self.assertEqual(rows[2], [2, 1, 1])
        self.assertEqual(rows[3], [1, 0, 1])
        self.assertEqual(rows[0], [3, None, None])
        self.assertEqual(rows[7], [1, 4, 3])
        self.assertEqual(len(out._rat.fields), 4)

    def test_a_constant_and_integer_inputs(self):
        self.assertEqual(cells(sa.CombinatorialAnd(grid([[0, 3, 3, 5]]), 2)), [[0, 1, 1, 2]])
        self.assertEqual(cells(ia.CombinatorialXOr(grid([[0, 3]]), 0)), [[0, 1]])
        with self.assertRaises(ValueError):
            sa.CombinatorialAnd(grid([[0.5, 1.0]]), grid([[1, 1]]))


def _image(rows, columns, seed=7):
    """A 3-band, 8-bit image of a left half in one colour and a right half in another, with noise of ±3."""
    rng = np.random.default_rng(seed)
    left = np.array([200, 40, 40])
    right = np.array([40, 60, 200])
    bands = []
    for b in range(3):
        band = np.where(np.arange(columns)[None, :] < columns // 2, left[b], right[b]) * np.ones((rows, 1))
        bands.append(np.clip(band + rng.integers(-3, 4, (rows, columns)), 0, 255).astype(np.uint8))
    return sa.CompositeBand([grid(b) for b in bands])


class Segmentation(_Fixture):
    def test_two_regions_become_two_segments(self):
        out = sa.SegmentMeanShift(_image(12, 16))
        self.assertEqual(out.bandCount, 3)
        bands = [arcpy.RasterToNumPyArray(out.getRasterBands()[b]) for b in range(3)]
        colours = {tuple(int(bands[b][r, c]) for b in range(3)) for r in range(12) for c in range(16)}
        self.assertEqual(len(colours), 2)
        left = tuple(int(bands[b][0, 0]) for b in range(3))
        # Each segment takes its average colour: the noise averages out within a grey level of the base colour.
        for got, want in zip(left, (200, 40, 40)):
            self.assertLessEqual(abs(got - want), 1)

    def test_a_small_region_joins_its_best_fitting_neighbour(self):
        image = np.full((10, 10), 100, np.uint8)
        image[:, 5:] = 180
        image[2:4, 2:4] = 60  # four pixels nearer the left's 100 than the right's 180
        out = sa.SegmentMeanShift(grid(image.tolist()), min_segment_size=5)
        got = arcpy.RasterToNumPyArray(out)
        self.assertEqual(len(np.unique(got)), 2)
        self.assertEqual(got[2, 2], got[0, 0])
        kept = sa.SegmentMeanShift(grid(image.tolist()), min_segment_size=1)
        self.assertEqual(len(np.unique(arcpy.RasterToNumPyArray(kept))), 3)

    def test_the_maximum_size_divides(self):
        from grapy import _segment
        colours = np.full((8, 8, 1), 100.0)
        labels = _segment.segment(colours, np.zeros((8, 8), bool), 6, 5.5, 0)
        self.assertEqual(int(labels.max()), 1)
        labels = _segment.segment(colours, np.zeros((8, 8), bool), 6, 5.5, 0, 16)
        self.assertEqual(sorted(np.bincount(labels.ravel())[1:].tolist()), [16, 16, 16, 16])

    def test_seg_mean_shift_function_and_detail_ranges(self):
        out = ia.SegMeanShift(_image(12, 16), spectral_detail=15.5, spatial_detail=15, min_num_pixels_per_segment=20)
        self.assertEqual(out.bandCount, 3)
        with self.assertRaises(ValueError):
            sa.SegmentMeanShift(_image(6, 6), spectral_detail=25)
        with self.assertRaises(ValueError):
            ia.SegMeanShift(grid([[1, 2]]))

    def test_compute_segment_attributes(self):
        image = np.zeros((10, 20), np.uint8)
        image[:, 10:] = 50
        extra = grid(np.where(np.arange(20)[None, :] < 10, 1.0, 3.0) * np.ones((10, 1)) +
                     np.where(np.arange(10)[:, None] % 2 == 0, 0.5, -0.5))
        out = sa.ComputeSegmentAttributes(grid(image.tolist()), extra,
                                          ["COLOR", "MEAN", "STD", "COUNT", "COMPACTNESS", "RECTANGULARITY"])
        names = [f[0] for f in out._rat.fields]
        self.assertEqual(names, ["Value", "Gray", "Count", "Mean_1", "Std_1", "Compactness", "Rectangularity"])
        rows = {r[0]: r for r in out._rat.rows}
        self.assertEqual(cells(out)[0][0], 1)
        self.assertEqual(rows[1][1:3], [0, 100])
        self.assertEqual(rows[2][1:3], [50, 100])
        self.assertAlmostEqual(rows[1][3], 1.0)
        self.assertAlmostEqual(rows[2][3], 3.0)
        self.assertAlmostEqual(rows[1][4], 0.5)
        # A 100 × 100 square: rectangularity 1; compactness its area over the circle through its corners, 2/π.
        self.assertAlmostEqual(rows[1][6], 1.0)
        self.assertAlmostEqual(rows[1][5], 2 / math.pi)
        defaults = sa.ComputeSegmentAttributes(grid(image.tolist()))
        self.assertEqual([f[0] for f in defaults._rat.fields],
                         ["Value", "Gray", "Count", "Compactness", "Rectangularity"])
        with self.assertRaises(ValueError):
            sa.ComputeSegmentAttributes(grid(image.tolist()), used_attributes=["MEAN"])

    def test_tiling_artifacts_are_joined_across_seams_only(self):
        image = np.full((8, 8), 100, np.uint8)
        image[:, 4:] = 103  # one segment cut at a seam of 4 columns, its two parts 3 grey levels apart
        image[:4, :] = np.where(np.arange(8) < 4, 30, 33)[None, :]  # another cut at the same seam
        joined = sa.RemoveRasterSegmentTilingArtifacts(grid(image.tolist()), 4, 100)
        got = arcpy.RasterToNumPyArray(joined)
        self.assertEqual(len(np.unique(got)), 2)
        self.assertEqual(got[7, 0], got[7, 7])
        # Without the seam at column 4, the same boundary is the segmentation's own and is kept.
        kept = sa.RemoveRasterSegmentTilingArtifacts(grid(image.tolist()), 3, 100)
        self.assertEqual(len(np.unique(arcpy.RasterToNumPyArray(kept))), 4)


class TopoToRaster(_Fixture):
    def cone(self):
        """Contours every 10 of the cone z = 100 − 0.4·r round (200, 200)."""
        rows = []
        for z in range(10, 100, 10):
            r = (100 - z) / 0.4
            ring = [arcpy.Point(200 + r * math.cos(t), 200 + r * math.sin(t)) for t in np.linspace(0, 2 * math.pi, 73)]
            rows.append((float(z), arcpy.Polyline(arcpy.Array(ring), 3857)))
        return self.features("cone", "POLYLINE", rows, [("h", "DOUBLE")])

    def test_a_cone_sampled_as_contours_is_recovered(self):
        out = sa.TopoToRaster([sa.TopoContour([[self.cone(), "h"]])], 10, "0 0 400 400")
        got = arcpy.RasterToNumPyArray(out)
        self.assertEqual(got.shape, (40, 40))
        self.assertEqual(out.pixelType, "F32")
        xs = 5 + 10 * np.arange(40)
        gx, gy = np.meshgrid(xs, 400 - xs)
        r = np.hypot(gx - 200, gy - 200)
        ring = (r > 25) & (r < 190)
        # Within a quarter of the contour interval between the top and the outer contours.
        self.assertLess(np.abs(got - (100 - 0.4 * r))[ring].max(), 2.5)

    def plane_with_a_dip(self):
        rows = []
        for x in range(5, 200, 20):
            for y in range(5, 200, 20):
                z = 50 + 0.1 * x + 0.05 * y - (8 if (x, y) == (105, 105) else 0)
                rows.append((z, arcpy.PointGeometry(arcpy.Point(x, y), 3857)))
        return self.features("spots", "POINT", rows, [("z", "DOUBLE")])

    def test_drainage_enforcement_removes_a_spurious_sink(self):
        spots = self.plane_with_a_dip()
        inputs = [sa.TopoPointElevation([[spots, "z"]])]
        dip = (19, 21)  # the cell below the low spot at (105, 105), as the free surface lets it drain
        loose = arcpy.RasterToNumPyArray(sa.TopoToRaster(inputs, 5, "0 0 200 200", enforce="NO_ENFORCE",
                                                         data_type="SPOT"))
        self.assertTrue(_pits(loose, np.zeros(loose.shape, bool))[dip])
        sinks = os.path.join(self.directory, "sinks.gpkg")
        streams = os.path.join(self.directory, "streams.gpkg")
        enforced = arcpy.RasterToNumPyArray(sa.TopoToRaster(inputs, 5, "0 0 200 200", data_type="SPOT",
                                                            tolerance_1=10, out_sink_features=sinks,
                                                            out_stream_features=streams))
        self.assertEqual(int(_pits(enforced, np.zeros(enforced.shape, bool)).sum()), 0)
        self.assertEqual(self.read(sinks, ["Elevation"]), [])
        self.assertIn(3, {code for (code,) in self.read(streams, ["Code"])})
        # Tolerance 1 of 0, the default for spot data: clearing would move the low spot's height, so the sink stays
        # and is reported.
        kept = os.path.join(self.directory, "kept.gpkg")
        conservative = arcpy.RasterToNumPyArray(sa.TopoToRaster(inputs, 5, "0 0 200 200", data_type="SPOT",
                                                                out_sink_features=kept))
        self.assertTrue(_pits(conservative, np.zeros(conservative.shape, bool))[dip])
        self.assertIn((107.5, 102.5), [xy for (xy,) in self.read(kept, ["SHAPE@XY"])])

    def test_parameter_file_round_trip_and_ddd(self):
        spots = self.plane_with_a_dip()
        parameters = os.path.join(self.directory, "ttr.txt")
        diagnostics = os.path.join(self.directory, "diag.txt")
        first = sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]])], 10, "0 0 200 200", margin=5,
                                data_type="SPOT", tolerance_1=10, out_parameter_file=parameters,
                                out_diagnostic_file=diagnostics)
        text = open(parameters, encoding="utf-8").read()
        for line in ("ENFORCE ON", "DATATYPE SPOT", "TOLERANCES 10.0 200.0", "CELL_SIZE 10.0", "MARGIN 5",
                     "EXTENT 0.0 0.0 200.0 200.0"):
            self.assertIn(line, text)
        self.assertIn("sinks found", open(diagnostics, encoding="utf-8").read())
        again = sa.TopoToRasterByFile(parameters)
        np.testing.assert_allclose(arcpy.RasterToNumPyArray(again), arcpy.RasterToNumPyArray(first), atol=1e-3)
        saved = os.path.join(self.directory, "ddd.tif")
        ddd.TopoToRaster(f"{spots} z PointElevation", saved, 10, "0 0 200 200", 5, "", "", "ENFORCE", "SPOT", 20,
                         "", 1, 0, 10)
        np.testing.assert_allclose(arcpy.RasterToNumPyArray(arcpy.Raster(saved)), arcpy.RasterToNumPyArray(first),
                                   atol=1e-3)
        with open(parameters, "a", encoding="utf-8") as out:
            out.write("NOT_A_KEYWORD 1\n")
        with self.assertRaises(arcpy.ExecuteError):
            sa.TopoToRasterByFile(parameters)

    def test_boundary_coast_lake_exclusion_and_stream(self):
        spots = self.plane_with_a_dip()
        square = arcpy.Polygon(arcpy.Array([arcpy.Point(*p) for p in ((0, 0), (0, 100), (100, 100), (100, 0))]), 3857)
        boundary = self.features("boundary", "POLYGON", [(square,)])
        lake_shape = arcpy.Polygon(arcpy.Array([arcpy.Point(*p) for p in ((140, 140), (140, 170), (170, 170),
                                                                          (170, 140))]), 3857)
        lake = self.features("lake", "POLYGON", [(lake_shape,)])
        stream = self.features("stream", "POLYLINE", [(arcpy.Polyline(arcpy.Array([arcpy.Point(195, 52),
                                                                                  arcpy.Point(5, 52)]), 3857),)])
        out = sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]]), sa.TopoBoundary([boundary]),
                               sa.TopoLake([lake]), sa.TopoStream([stream])], 10, "0 0 200 200", margin=5,
                              data_type="SPOT", tolerance_1=10)
        got = cells(out, nodata=-9999)
        self.assertEqual(got[0][0], -9999)  # top-left, outside the boundary
        self.assertNotEqual(got[19][0], -9999)
        lake_cells = [got[r][c] for r in range(3, 6) for c in range(14, 17)]
        self.assertEqual(len(set(lake_cells)), 1)  # flat at the lowest shore value
        coast = self.features("coast", "POLYGON", [(square,)])
        below = cells(sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]]), sa.TopoCoast([coast])], 10,
                                      "0 0 200 200", margin=5, data_type="SPOT", minimum_z_value=0))
        self.assertEqual(below[0][19], -1.0)  # outside the coast: below the minimum z limit

    def test_stream_descends_and_cliff_breaks(self):
        spots = self.plane_with_a_dip()
        # A stream drawn uphill (east, where the plane rises): it is pinned to descend in its own direction.
        stream = self.features("up", "POLYLINE", [(arcpy.Polyline(arcpy.Array([arcpy.Point(15, 152),
                                                                              arcpy.Point(185, 152)]), 3857),)])
        out = arcpy.RasterToNumPyArray(sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]]),
                                                        sa.TopoStream([stream])], 10, "0 0 200 200", margin=5,
                                                       data_type="SPOT", enforce="NO_ENFORCE"))
        row = out[4, 1:19]  # y = 152 lies in row 4
        self.assertTrue(all(b < a for a, b in zip(row, row[1:])))
        # A cliff along x = 100 drawn south to north has its low side on the left (west), as the plane has; drawn
        # north to south it contradicts the data, and every cell pair across it is reported with code 8.
        errors = os.path.join(self.directory, "errors.gpkg")
        wrong = self.features("cliff", "POLYLINE", [(arcpy.Polyline(arcpy.Array([arcpy.Point(100, 199),
                                                                                arcpy.Point(100, 1)]), 3857),)])
        sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]]), sa.TopoCliff([wrong])], 10, "0 0 200 200", margin=0,
                        data_type="SPOT", enforce="NO_ENFORCE", out_stream_cliff_error_feature=errors)
        codes = [code for (code,) in self.read(errors, ["Code"])]
        self.assertEqual(set(codes), {8})
        self.assertGreaterEqual(len(codes), 15)  # of the 20 rows the cliff crosses
        right = self.features("right", "POLYLINE", [(arcpy.Polyline(arcpy.Array([arcpy.Point(100, 1),
                                                                                arcpy.Point(100, 199)]), 3857),)])
        fine = os.path.join(self.directory, "fine.gpkg")
        sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]]), sa.TopoCliff([right])], 10, "0 0 200 200", margin=0,
                        data_type="SPOT", enforce="NO_ENFORCE", out_stream_cliff_error_feature=fine)
        # Drawn the right way, only the cells beside the low spot at (105, 105), on the high side, are flagged.
        self.assertEqual(sorted(xy for (xy,) in self.read(fine, ["SHAPE@XY"])),
                         [(100.0, 95.0), (100.0, 105.0), (100.0, 115.0)])

    def test_inputs_and_validation(self):
        spots = self.plane_with_a_dip()
        self.assertEqual(sa.TopoContour([["c", "h"]]).inFeatures, [["c", "h"]])
        self.assertIs(ia.TopoSink, sa.TopoSink)
        with self.assertRaises(ValueError):
            sa.TopoToRaster([sa.TopoPointElevation([spots])], 10)
        with self.assertRaises(ValueError):
            sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]])], 10, maximum_iterations=0)
        with self.assertRaises(ValueError):
            sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]])], 10, tolerance_2=0)


if __name__ == "__main__":
    unittest.main()
