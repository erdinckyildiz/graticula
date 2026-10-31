"""Ortho mapping on grapy's mosaic datasets (ADR-234), against hand-worked answers and synthetic photographs.

The photographs are rendered here: four frame images of a textured ground plane z = 0, taken from 25 m with a 5 mm
lens and 10 µm pixels (a ground sample distance of 5 cm), 200 × 200 pixels, at the corners of a 5 m square — each
overlapping its neighbours by half. Every pixel's value is the texture where its ray meets the plane, so the true
orientation is known exactly and the tools are checked for recovering it.
"""
import csv
import json
import math
import os
import sys
import tempfile
import unittest

import numpy as np
import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _ortho, da  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

F, PS, SIZE, H = 5000.0, 10.0, 200, 25.0
TRUTH = {1: (0.0, 0.0, H, 0.6, -0.4, 2.0), 2: (5.0, 0.0, H, -0.3, 0.5, -1.0), 3: (0.0, 5.0, H, 0.2, 0.3, 1.5),
         4: (5.0, 5.0, H, -0.5, -0.2, -2.5)}
#: The orientation the frames table gives: the truth off by up to 0.3 m and 0.12°, as a navigation system might be.
NOISE = {1: (0.25, -0.2, 0.3, 0.1, -0.08, 0.1), 2: (-0.15, 0.3, -0.2, -0.12, 0.1, -0.05),
         3: (0.2, 0.1, 0.25, 0.08, 0.12, 0.08), 4: (-0.3, -0.25, -0.3, 0.1, -0.1, -0.1)}


def _texture():
    from scipy import ndimage
    rng = np.random.default_rng(7)
    noise = rng.normal(size=(1200, 1200))
    field = ndimage.gaussian_filter(noise, 5) * 900 + 128
    return np.clip(field, 0, 255)


TEXTURE = _texture()
T_LEFT, T_TOP, T_CELL = -8.0, 16.0, 0.02   # the texture covers -8..16 on both axes


def ground(x, y):
    """The ground's grey value at x, y (bilinear in the texture)."""
    return _ortho.sample(TEXTURE, (np.asarray(x) - T_LEFT) / T_CELL, (T_TOP - np.asarray(y)) / T_CELL)


def model(values):
    x, y, z, o, p, k = values
    return _ortho.FrameModel({"X": x, "Y": y, "Z": z, "omega": o, "phi": p, "kappa": k, "focal_length": F,
                              "pixel_size": PS, "rows": SIZE, "columns": SIZE})


def render(path, values):
    import warnings
    import rasterio
    m = model(values)
    rows, cols = np.mgrid[0:SIZE, 0:SIZE]
    gx, gy = m.to_ground(cols + 0.5, rows + 0.5, 0.0)
    image = np.round(ground(gx, gy)).astype(np.uint8)
    with warnings.catch_warnings():   # a photograph has no georeference
        warnings.simplefilter("ignore")
        with rasterio.open(path, "w", driver="GTiff", width=SIZE, height=SIZE, count=1, dtype="uint8") as out:
            out.write(image, 1)


class Ortho(unittest.TestCase):
    def tearDown(self):
        arcpy.env.resamplingMethod = None

    @classmethod
    def setUpClass(cls):
        cls.images = tempfile.mkdtemp()
        for i, values in TRUTH.items():
            render(os.path.join(cls.images, f"img{i}.tif"), values)

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.cellSize = None
        arcpy.env.resamplingMethod = "BILINEAR"
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "om")[0]

    def frames(self, orientation, name="frames.csv", camera_in_frames=True):
        path = os.path.join(self.directory, name)
        with open(path, "w", newline="", encoding="utf-8") as handle:
            written = csv.writer(handle)
            head = ["Raster", "CameraID", "PerspectiveX", "PerspectiveY", "PerspectiveZ", "Omega", "Phi", "Kappa"]
            if camera_in_frames:
                head += ["FocalLength", "PixelSize"]
            written.writerow(head)
            for i, values in orientation.items():
                row = [os.path.join(self.images, f"img{i}.tif"), "cam", *values]
                written.writerow(row + ([F, PS] if camera_in_frames else []))
        return path

    def mosaic(self, orientation=None, name="md"):
        md = arcpy.management.CreateMosaicDataset(self.gpkg, name, 3857)[0]
        arcpy.management.AddRastersToMosaicDataset(md, "Frame Camera", self.frames(orientation or TRUTH, f"{name}.csv"))
        return md

    def noisy(self):
        return {i: tuple(np.add(TRUTH[i], NOISE[i])) for i in TRUTH}

    def gcps(self):
        """Six ground control points on z = 0, measured where the true orientation puts them."""
        points = []
        for pid, (x, y) in enumerate([(1, 1), (4, 1.5), (1.5, 4), (3.5, 3.5), (6, 2), (2.5, 6)], start=1):
            seen = []
            for i, values in TRUTH.items():
                c, r = model(values).project(np.array([x]), np.array([y]), np.array([0.0]))
                if 5 <= c[0] <= SIZE - 5 and 5 <= r[0] <= SIZE - 5:
                    seen.append({"imageId": i, "x": float(c[0]), "y": float(r[0])})
            points.append({"pointId": 1000 + pid, "x": x, "y": y, "z": 0.0, "xyAccuracy": 0.01, "zAccuracy": 0.01,
                           "spatialReference": {"wkid": 3857}, "imagePoints": seen})
        return json.dumps(points)

    # ---- the frame camera model

    def test_collinearity_answers_a_hand_worked_nadir_photograph(self):
        # Nadir from 25 m with f = 5 mm: a ground point 1 m east and 2 m north lands 200 µm and 400 µm from the
        # principal point — 20 and 40 pixels of 10 µm right of and above the centre (100, 100).
        m = model((0, 0, 25, 0, 0, 0))
        c, r = m.project(np.array([1.0]), np.array([2.0]), np.array([0.0]))
        self.assertAlmostEqual(float(c[0]), 120.0)
        self.assertAlmostEqual(float(r[0]), 60.0)
        x, y = m.to_ground(np.array([120.0]), np.array([60.0]), 0.0)
        self.assertAlmostEqual(float(x[0]), 1.0)
        self.assertAlmostEqual(float(y[0]), 2.0)
        self.assertAlmostEqual(m.gsd(), 0.05)
        # Kappa 90°: the image is turned a quarter round, so east becomes up the image.
        turned = model((0, 0, 25, 0, 0, 90))
        c, r = turned.project(np.array([1.0]), np.array([0.0]), np.array([0.0]))
        self.assertAlmostEqual(float(c[0]), 100.0, places=6)
        self.assertAlmostEqual(abs(float(r[0]) - 100.0), 20.0, places=6)

    def test_conventions_answer_as_pro_measured(self):
        # ArcGIS Pro 3.7, 2026-10-31 (ADR-234 condition 5, tools/pro-compare/adr234-frames.py): a 300 x 200 image, 10 µm
        # pixels, f = 50 mm, from 1,000 m over AverageZ 10, Omega 5, Phi -3, Kappa 30; Pro orthorectified images whose
        # bands are their own column and row, and these are the image positions it sampled at these ground points.
        base = {"Z": 1000.0, "omega": 5.0, "phi": -3.0, "kappa": 30.0, "focal_length": 50000.0, "pixel_size": 10.0,
                "rows": 200, "columns": 300, "average_z": 10.0}
        cases = [
            ({"X": 504000.0, "Y": 4400000.0, "affine": [-1400, 9.5, 1.0, 1100, 0.8, -10.5]},
             [(504052.3978782685, 4400090.962891743, 150.26117, 99.43208),
              (504059.5978782685, 4400074.162891743, 130.31876, 184.69641)]),
            ({"X": 509000.0, "Y": 4400000.0, "radial": [0, 0.01, 0, 0], "tangential": [0.01, -0.005]},
             [(509049.9978782685, 4400085.962891743, 139.36250, 97.61153),
              (509056.9978782685, 4400070.362891743, 131.49394, 184.40790),
              (509070.79787826847, 4400104.962891743, 283.21765, 66.82097)]),
        ]
        for change, samples in cases:
            m = _ortho.FrameModel({**base, **change})
            for x, y, col, row in samples:
                c, r = m.project(np.array([x]), np.array([y]), np.array([10.0]))
                self.assertAlmostEqual(float(c[0]), col, delta=0.003)
                self.assertAlmostEqual(float(r[0]), row, delta=0.003)
        # LowPS and the centre of the default frame at 500000, 4400000, as Pro wrote them
        m = _ortho.FrameModel({**base, "X": 500000.0, "Y": 4400000.0})
        fields = _ortho.item_fields(m, _ortho.footprint(m))
        self.assertAlmostEqual(fields["LowPS"], 0.23896208925696555, places=4)
        self.assertAlmostEqual(fields["CenterX"], 500052.16141720634, places=2)
        self.assertAlmostEqual(fields["CenterY"], 4400086.690151874, places=2)

    def test_the_rotation_matrix_is_orthonormal_and_wolfs(self):
        m = _ortho.rotation(10, 20, 30)
        np.testing.assert_allclose(m @ m.T, np.eye(3), atol=1e-12)
        o, p, k = np.radians([10, 20, 30])
        self.assertAlmostEqual(m[2, 0], math.sin(p))
        self.assertAlmostEqual(m[0, 0], math.cos(p) * math.cos(k))
        self.assertAlmostEqual(m[1, 2], math.cos(o) * math.sin(p) * math.sin(k) + math.sin(o) * math.cos(k))

    def test_frame_camera_items_are_added_from_a_frames_table(self):
        md = self.mosaic()
        with da.SearchCursor(md, ["OID@", "LowPS", "CenterX", "CenterY", "SHAPE@"]) as rows:
            items = list(rows)
        self.assertEqual(len(items), 4)
        for _, low, cx, cy, shape in items:   # as Pro 3.7 writes them: see test_conventions_answer_as_pro_measured
            self.assertAlmostEqual(low, (shape._shape.bounds[2] - shape._shape.bounds[0]) / SIZE)
            self.assertAlmostEqual((cx, cy), (shape._shape.centroid.x, shape._shape.centroid.y))
        self.assertAlmostEqual(items[0][1], 0.05, delta=0.003)
        self.assertAlmostEqual(items[0][4]._shape.area, 100.0, delta=1.0)   # a 10 m square, slightly tilted
        with self.assertRaises(NotBuiltError):   # reading the mosaic as a raster would need on-the-fly ortho
            arcpy.Raster(md).maximum
        # The camera may come from a cameras table instead, matched on CameraID.
        cameras = os.path.join(self.directory, "cameras.csv")
        with open(cameras, "w", newline="", encoding="utf-8") as handle:
            csv.writer(handle).writerows([["CameraID", "FocalLength", "PixelSize"], ["cam", F, PS]])
        md2 = arcpy.management.CreateMosaicDataset(self.gpkg, "md2", 3857)[0]
        arcpy.management.AddRastersToMosaicDataset(md2, "Frame Camera", self.frames(TRUTH, "f2.csv", False),
                                                   aux_inputs=[["CameraFile", cameras]])
        self.assertEqual(len(list(da.SearchCursor(md2, ["OID@"]))), 4)

    # ---- tie points

    def test_tie_points_land_where_the_true_orientation_projects_them(self):
        md = self.mosaic()
        tie = arcpy.management.ComputeTiePoints(md, os.path.join(self.gpkg, "tie"), "MEDIUM", None, None, "MEDIUM",
                                                "REGULAR", "HIGH")[0]
        records = _ortho.read_points(tie)
        self.assertGreater(len(records), 40)
        models = {i: model(v) for i, v in TRUTH.items()}
        worst = 0.0
        for r in records:
            x, y, z = r["xyz"]
            self.assertAlmostEqual(z, 0.0, delta=0.15)
            c, row = models[r["ImageID"]].project(np.array([x]), np.array([y]), np.array([0.0]))
            worst = max(worst, math.hypot(c[0] - r["ImageX"], row[0] - r["ImageY"]))
        self.assertLess(worst, 0.5)

    # ---- block adjustment

    def test_block_adjustment_recovers_the_true_orientation(self):
        md = self.mosaic(self.noisy())
        tie = arcpy.management.ComputeTiePoints(md, os.path.join(self.gpkg, "tie"), "MEDIUM", None, None, "MEDIUM",
                                                "REGULAR", "MEDIUM")[0]
        arcpy.management.AppendControlPoints(tie, self.gcps())
        solution = os.path.join(self.gpkg, "solution")
        points = os.path.join(self.gpkg, "solution_points")
        arcpy.management.ComputeBlockAdjustment(md, tie, "Frame", solution, points)
        # Over a 10 m footprint from 25 m, a shift of the camera and a tilt that moves the image the same way are
        # nearly one unknown, so the orientation is checked where it matters: a grid of ground points projects into
        # each adjusted image within half a pixel of where the true orientation projects it — and the frames table's
        # orientation missed by pixels.
        gx, gy = np.meshgrid(np.linspace(-2, 7, 10), np.linspace(-2, 7, 10))
        gz = np.zeros(gx.shape)
        with da.SearchCursor(solution, ["ImageID", "PerspectiveX", "PerspectiveY", "PerspectiveZ", "Omega", "Phi",
                                        "Kappa", "RMSE"]) as rows:
            for row in rows:
                truth = TRUTH[row[0]]
                np.testing.assert_allclose(row[1:4], truth[:3], atol=0.1)
                np.testing.assert_allclose(row[4:7], truth[3:], atol=0.25)
                self.assertLess(row[7], 0.5)
                true_c, true_r = model(truth).project(gx, gy, gz)
                c, r = model(row[1:7]).project(gx, gy, gz)
                c0, r0 = model(np.add(truth, NOISE[row[0]])).project(gx, gy, gz)
                self.assertLess(float(np.max(np.hypot(c - true_c, r - true_r))), 0.5)
                self.assertGreater(float(np.max(np.hypot(c0 - true_c, r0 - true_r))), 3.0)
        report = os.path.join(self.directory, "report.json")
        arcpy.management.GenerateBlockAdjustmentReport(md, solution, points, report, tie, "JSON")
        summary = json.load(open(report, encoding="utf-8"))["summary"]
        self.assertEqual(summary["images"], 4)
        self.assertLess(summary["gcpRmseXY"], 0.02)
        brief = os.path.join(self.directory, "brief.json")
        arcpy.management.GenerateBlockAdjustmentReport(md, solution, points, brief, None, "BRIEF JSON")
        self.assertEqual(list(json.load(open(brief, encoding="utf-8"))), ["summary"])
        html = os.path.join(self.directory, "report.html")
        arcpy.management.GenerateBlockAdjustmentReport(md, solution, points, html)
        self.assertIn("<h2>Images</h2>", open(html, encoding="utf-8").read())

        # Applied, the ortho of the adjusted block matches the ground better than before.
        before = self.ortho_error(md, "before.tif")
        arcpy.management.ApplyBlockAdjustment(md, "ADJUST", solution, None, None, None, tie, "ADJUST_FOOTPRINTS",
                                              points, "ADJUST_TIEPOINTS")
        after = self.ortho_error(md, "after.tif")
        self.assertLess(after, before / 2)
        self.assertLess(after, 2.0)
        arcpy.management.ApplyBlockAdjustment(md, "RESET", None, None, None, None, None, "ADJUST_FOOTPRINTS")
        self.assertAlmostEqual(self.ortho_error(md, "reset.tif"), before, places=6)

    def ortho_error(self, md, name):
        out = arcpy.management.CreateOrthoCorrectedRasterDataset(md, os.path.join(self.directory, name),
                                                                 "CONSTANT_ELEVATION", 0)[0]
        r = arcpy.Raster(out)
        xs, ys = r._grid.centres()
        gx, gy = np.meshgrid(xs, ys)
        ok = ~r._mask
        return float(np.mean(np.abs(r._array[ok].astype(float) - ground(gx, gy)[ok])))

    def test_orthorectification_of_the_true_block_reproduces_the_ground(self):
        md = self.mosaic()
        arcpy.env.cellSize = 0.05
        try:
            out = arcpy.management.CreateOrthoCorrectedRasterDataset(md, os.path.join(self.directory, "o.tif"),
                                                                     "CONSTANT_ELEVATION", 0)[0]
        finally:
            arcpy.env.cellSize = None
        r = arcpy.Raster(out)
        self.assertAlmostEqual(r._grid.width, 0.05)
        xs, ys = r._grid.centres()
        gx, gy = np.meshgrid(xs, ys)
        ok = ~r._mask
        self.assertGreater(ok.mean(), 0.8)
        # Rounded to bytes twice and interpolated twice: within two grey levels of a texture whose spread is 50.
        truth = ground(gx, gy)[ok]
        self.assertLess(float(np.mean(np.abs(r._array[ok] - truth))), 2.0)
        self.assertGreater(float(np.corrcoef(r._array[ok].astype(float), truth)[0, 1]), 0.999)
        # A DEM 3 m below a z-offset of +3 is the same plane, so the same ortho.
        import rasterio
        from rasterio.transform import from_origin
        dem = os.path.join(self.directory, "dem.tif")
        with rasterio.open(dem, "w", driver="GTiff", width=60, height=60, count=1, dtype="float32", crs="EPSG:3857",
                           transform=from_origin(-10, 20, 0.5, 0.5)) as written:
            written.write(np.full((60, 60), -3.0, np.float32), 1)
        out2 = arcpy.management.CreateOrthoCorrectedRasterDataset(md, os.path.join(self.directory, "o2.tif"), "DEM", None,
                                                                  dem, 1, 3)[0]
        r2 = arcpy.Raster(out2)
        np.testing.assert_array_equal(r2._mask, r._mask)
        np.testing.assert_array_equal(r2._array[ok], r._array[ok])

    def test_an_rpc_raster_is_orthorectified_by_its_rational_functions(self):
        import rasterio
        from rasterio.rpc import RPC
        zero = [0.0] * 20
        one = [1.0] + [0.0] * 19
        sample_num = list(zero)
        sample_num[1] = 1.0           # sample = L: east
        line_num = list(zero)
        line_num[2] = -1.0            # line = −P: north is up
        rpc = RPC(height_off=0, height_scale=100, lat_off=40, lat_scale=0.01, line_den_coeff=one,
                  line_num_coeff=line_num, line_off=50, line_scale=50, long_off=30, long_scale=0.01,
                  samp_den_coeff=one, samp_num_coeff=sample_num, samp_off=50, samp_scale=50)
        path = os.path.join(self.directory, "rpc.tif")
        columns = np.tile(np.arange(100, dtype=np.uint8), (100, 1))
        with rasterio.open(path, "w", driver="GTiff", width=100, height=100, count=1, dtype="uint8", rpcs=rpc) as out:
            out.write(columns, 1)
        result = arcpy.management.CreateOrthoCorrectedRasterDataset(path, os.path.join(self.directory, "rpco.tif"),
                                                                    "CONSTANT_ELEVATION", 0)[0]
        r = arcpy.Raster(result)
        self.assertEqual(r.spatialReference.factoryCode, 4326)
        xs, ys = r._grid.centres()
        # A cell at longitude λ sees sample (λ − 30) / 0.01 × 50 + 50 (+0.5 from the pixel's centre), whose value is
        # its column.
        expected = np.floor((xs - 30) / 0.01 * 50 + 50 + 0.5)
        ok = ~r._mask[50]
        np.testing.assert_array_equal(r._array[50][ok], expected[ok])
        with self.assertRaises(NotBuiltError):
            arcpy.management.CreateOrthoCorrectedRasterDataset(path, os.path.join(self.directory, "g.tif"),
                                                               "CONSTANT_ELEVATION", 0, None, None, None, "GEOID")

    # ---- control points and stereo

    def test_control_points_are_matched_appended_and_analysed(self):
        md = self.mosaic()
        # One GCP measured on image 1 only: MatchControlPoints finds it on the other images that see it.
        x, y = 2.5, 2.5
        c, r = model(TRUTH[1]).project(np.array([x]), np.array([y]), np.array([0.0]))
        one = json.dumps([{"pointId": 7, "x": x, "y": y, "z": 0, "spatialReference": {"wkid": 3857},
                           "imagePoints": [{"imageId": 1, "x": float(c[0]), "y": float(r[0])}]}])
        matched = arcpy.management.MatchControlPoints(md, one, os.path.join(self.gpkg, "matched"))[0]
        records = _ortho.read_points(matched)
        self.assertEqual(sorted(r["ImageID"] for r in records), [1, 2, 3, 4])
        for rec in records:
            cc, rr = model(TRUTH[rec["ImageID"]]).project(np.array([x]), np.array([y]), np.array([0.0]))
            self.assertLess(math.hypot(cc[0] - rec["ImageX"], rr[0] - rec["ImageY"]), 0.5)
        with self.assertRaises(ExecuteError):   # a GCP with no initial tie point
            arcpy.management.MatchControlPoints(md, json.dumps([{"pointId": 1, "x": 1, "y": 1, "z": 0,
                                                                 "spatialReference": {"wkid": 3857}}]),
                                                os.path.join(self.gpkg, "none"))
        coverage, overlap = os.path.join(self.gpkg, "coverage"), os.path.join(self.gpkg, "overlap")
        arcpy.management.AnalyzeControlPoints(md, matched, coverage, overlap)
        with da.SearchCursor(coverage, ["ImageID", "PointCount"]) as rows:
            self.assertEqual(sorted(rows), [(1, 1), (2, 1), (3, 1), (4, 1)])
        with da.SearchCursor(overlap, ["ImageIDs", "PointCount", "Area"]) as rows:
            found = {row[0]: (row[1], row[2]) for row in rows}
        self.assertEqual(len(found), 6)                        # every pair of four overlapping images
        self.assertEqual(found["1;4"][0], 1)
        self.assertAlmostEqual(found["1;2"][1], 50.0, delta=3.0)  # about half of a 10 m square
        # A plain point class appends as GCPs, z from a field.
        plain = arcpy.management.CreateFeatureclass(self.gpkg, "plain", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(plain, "elev", "DOUBLE")
        arcpy.management.AddField(plain, "label", "TEXT")
        with da.InsertCursor(plain, ["elev", "label", "SHAPE@"]) as cursor:
            cursor.insertRow([12.5, "A", shapely.Point(3, 3)])
            cursor.insertRow([9.0, "B", shapely.Point(50, 50)])
        arcpy.management.AppendControlPoints(matched, plain, "elev", "label", None, 0.05, 0.1, "NONE",
                                             arcpy.Extent(0, 0, 10, 10), "GCP")
        added = [r for r in _ortho.read_points(matched) if r["Tag"] == "A"]
        self.assertEqual(len(added), 1)
        self.assertEqual((added[0]["xyz"][2], added[0]["PointID"], added[0]["XYAccuracy"]), (12.5, 8, 0.05))

    def test_stereo_pairs_meet_the_pages_thresholds(self):
        md = self.mosaic()
        arcpy.management.BuildStereoModel(md, 1, 90, 0.4)
        with da.SearchCursor(os.path.join(self.gpkg, "md_Stereo"), ["ImageID1", "ImageID2", "IntersectionAngle",
                                                                     "Overlap"]) as rows:
            pairs = list(rows)
        # Side by side 5 m apart at 25 m: rays meet at the overlap's centre at about 2·atan(2.5 / 25) = 11.4°.
        self.assertEqual(sorted((a, b) for a, b, _, _ in pairs), [(1, 2), (1, 3), (2, 4), (3, 4)])
        for _, _, angle, share in pairs:
            self.assertAlmostEqual(angle, math.degrees(2 * math.atan(2.5 / 25)), delta=0.6)
            self.assertAlmostEqual(share, 0.5, delta=0.05)
        arcpy.management.BuildStereoModel(md, 15)   # wider than any pair here
        self.assertEqual(len(list(da.SearchCursor(os.path.join(self.gpkg, "md_Stereo"), ["OID@"]))), 0)

    def test_frame_and_camera_parameters_export_and_read_back(self):
        md = self.mosaic()
        out = os.path.join(self.directory, "exported.csv")
        arcpy.management.ExportFrameAndCameraParameters(md, out)
        md2 = arcpy.management.CreateMosaicDataset(self.gpkg, "again", 3857)[0]
        arcpy.management.AddRastersToMosaicDataset(md2, "Frame Camera", out)
        connection = _ortho._gpkg.connect(self.gpkg)
        try:
            first, second = _ortho.models(connection, "md"), _ortho.models(connection, "again")
        finally:
            connection.close()
        for i in first:
            self.assertEqual((first[i].X, first[i].kappa, first[i].f), (second[i].X, second[i].kappa, second[i].f))
        with self.assertRaises(NotBuiltError):
            arcpy.management.ExportFrameAndCameraParameters(md, out, "PIX4D_CALIBRATED_CAMERA_PARAMETERS")

    # ---- refused, each for its own reason

    def test_what_the_pages_leave_undefined_is_refused_by_name(self):
        md = self.mosaic()
        tie = os.path.join(self.gpkg, "t")
        with self.assertRaises(NotBuiltError):
            arcpy.management.ComputeTiePoints(md, tie, "MEDIUM", None, None, "MEDIUM", "RANDOM", "LOW")
        with self.assertRaises(NotBuiltError):
            arcpy.management.ComputeBlockAdjustment(md, tie, "POLYORDER1", os.path.join(self.gpkg, "s"))
        with self.assertRaises(NotBuiltError):
            arcpy.management.ComputeBlockAdjustment(md, tie, "Frame", os.path.join(self.gpkg, "s"), None, None,
                                                    [["CalibrateK", 1]])
        with self.assertRaises(NotBuiltError):
            arcpy.management.GenerateBlockAdjustmentReport(md, "s", "p", os.path.join(self.directory, "r.pdf"), None,
                                                           "PDF")
        for name in ("ComputeCameraModel", "ComputeDepthMap", "RemoveDepthMap", "ComputeFiducials",
                     "UpdateInteriorOrientation", "GeneratePointCloud", "GenerateExcludeArea"):
            with self.assertRaises(NotBuiltError) as caught:
                getattr(arcpy.management, name)()
            self.assertNotIn("does not hold yet", str(caught.exception))
        for name in ("DetectControlPoints", "Geometric", "TransformImageShapes"):
            with self.assertRaises(NotBuiltError) as caught:
                getattr(arcpy.ia, name)()
            self.assertIn("ADR-234", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
