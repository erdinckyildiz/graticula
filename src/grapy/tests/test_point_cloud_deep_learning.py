"""Deep learning on point clouds — ADR-249.

The parts that need no framework (blocks, boxes, the 3D IoU, NMS, average precision, the space-filling curves, the
training data folders) are tested against hand-worked answers. Training and inference run end to end on tiny synthetic
LAS files on the CPU, from random weights and with the networks scaled down (``_dlpoints.WIDTH``), in seconds; they are
skipped where this Python has no torch, and nothing here downloads anything.

The scene: ground points of class 2 at z ≈ 0 over 20 m × 20 m, and four 3 m × 3 m "buildings" of class 6, 4 m tall,
centred at (5, 5), (15, 5), (5, 15) and (15, 15). The training boundary is the west half (x < 10).
"""
import csv
import json
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, os.path.dirname(__file__))

import grapy as arcpy  # noqa: E402
from grapy import _dlpcnets as nets, _dlpoints as P, _multipatch as M  # noqa: E402
from grapy._gpkg import Column  # noqa: E402
from grapy._runtime import NotBuiltError  # noqa: E402
from test_lastools import las_file, read  # noqa: E402

try:
    import torch  # noqa: F401
    HAVE_TORCH = True
except ImportError:
    HAVE_TORCH = False

CENTRES = ((5, 5), (15, 5), (5, 15), (15, 15))


def scene(path, seed=1):
    rng = np.random.default_rng(seed)
    n = 900
    xs, ys, zs, cs = [rng.uniform(0, 20, n)], [rng.uniform(0, 20, n)], [rng.normal(0, 0.03, n)], [np.full(n, 2)]
    for cx, cy in CENTRES:
        m = 90
        xs.append(rng.uniform(cx - 1.5, cx + 1.5, m))
        ys.append(rng.uniform(cy - 1.5, cy + 1.5, m))
        zs.append(rng.uniform(0.3, 4, m))
        cs.append(np.full(m, 6))
    x, y, z, c = map(np.concatenate, (xs, ys, zs, cs))
    return las_file(path, x, y, z, c, intensity=rng.integers(0, 100, len(x)).astype(np.uint16))


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.scratchWorkspace = self.directory         # where a .dlpk is unpacked
        arcpy.env.randomGenerator = None
        self.folder = os.path.join(self.directory, "lidar")
        os.makedirs(self.folder)
        self.las = scene(os.path.join(self.folder, "tile.las"))
        self.lasd = arcpy.management.CreateLasDataset(self.folder, os.path.join(self.directory, "scene.lasd"))[0]
        self.west = self.polygon("west", 0, 0, 10, 20)
        P.WIDTH = 0.25

    def tearDown(self):
        P.WIDTH = 1.0

    def polygon(self, name, x0, y0, x1, y1):
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POLYGON", spatial_reference=3857)
        path = os.path.join(self.directory, f"{name}.gpkg")
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            rows.insertRow([arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1),
                                                       arcpy.Point(x0, y1), arcpy.Point(x0, y0)]),
                                          arcpy.SpatialReference(3857))])
        return path

    def prepared(self, limit=128):
        return arcpy.ddd.PreparePointCloudTrainingData(self.lasd, "10 Meters", "scene.pctd", self.west,
                                                       block_point_limit=limit)[0]


class Blocks(unittest.TestCase):
    def test_each_point_is_in_four_blocks_of_the_size(self):
        rng = np.random.default_rng(0)
        x, y = rng.uniform(3, 17, 200), rng.uniform(-4, 9, 200)
        grid, (ox, oy) = P.tiles(x, y, 4.0)
        counts = np.zeros(200, int)
        for (column, row), indexes in grid.items():
            x0, y0 = ox + column * 2.0, oy + row * 2.0
            self.assertTrue(np.all((x[indexes] >= x0) & (x[indexes] < x0 + 4) & (y[indexes] >= y0)
                                   & (y[indexes] < y0 + 4)))
            counts[indexes] += 1
        self.assertTrue(np.all(counts == 4))

    def test_a_block_over_the_limit_is_dealt_into_several(self):
        parts = P.split_block(np.arange(10), 4, np.random.default_rng(0))
        self.assertEqual([len(p) for p in parts], [4, 3, 3])
        self.assertEqual(sorted(np.concatenate(parts).tolist()), list(range(10)))
        self.assertEqual(len(P.split_block(np.arange(4), 4, np.random.default_rng(0))), 1)


class Boxes(unittest.TestCase):
    def test_a_multipatch_box_is_read_back_with_its_yaw(self):
        box = np.array([10.0, 20.0, 1.0, 4.0, 2.0, 2.0, math.pi / 6])
        shape = M.from_triangles(M.prism(P.footprint(box), 0.0, 2.0))
        np.testing.assert_allclose(P.box_of(shape), box, atol=1e-9)

    def test_3d_iou_is_intersection_volume_over_union_volume(self):
        a = np.array([0, 0, 1, 2, 2, 2, 0.0])
        b = np.array([1, 0, 1, 2, 2, 2, 0.0])          # half the footprint, the whole height: 4 / (8 + 8 − 4)
        self.assertAlmostEqual(P.iou3d(a, b), 1 / 3)
        c = np.array([0, 0, 2, 2, 2, 2, 0.0])          # the whole footprint, half the height
        self.assertAlmostEqual(P.iou3d(a, c), 1 / 3)
        d = np.array([0, 0, 1, 2, 2, 2, math.pi / 2])  # the same box turned a quarter
        self.assertAlmostEqual(P.iou3d(a, d), 1.0)
        self.assertEqual(P.iou3d(a, np.array([5, 5, 1, 1, 1, 1, 0.0])), 0.0)

    def test_nms_keeps_the_most_confident_of_overlapping_boxes(self):
        boxes = [np.array([0, 0, 1, 2, 2, 2, 0.0]), np.array([0.2, 0, 1, 2, 2, 2, 0.0]),
                 np.array([1, 0, 1, 2, 2, 2, 0.0])]
        # IoU(1, 0) = 7.2 / 8.8 = 0.82 and IoU(1, 2) = 4.8 / 11.2 = 0.43
        self.assertEqual(P.nms3d(boxes, [0.5, 0.9, 0.7], 0.5), [1, 2])
        self.assertEqual(sorted(P.nms3d(boxes, [0.5, 0.9, 0.7], 0.3)), [1])
        self.assertEqual(sorted(P.nms3d(boxes, [0.5, 0.9, 0.7], 0.95)), [0, 1, 2])

    def test_residuals_round_trip(self):
        anchor = np.array([[1.0, 2.0, 0.5, 4.0, 2.0, 1.5, 0.0]])
        box = np.array([[1.4, 1.7, 0.8, 4.4, 1.8, 1.2, 0.3]])
        np.testing.assert_allclose(P.decode(anchor, P.encode(anchor, box)), box, atol=1e-12)

    def test_average_precision_is_voc_all_point(self):
        truths = {0: [np.array([0, 0, 1, 2, 2, 2, 0.0])], 1: [np.array([5, 5, 1, 2, 2, 2, 0.0])]}
        detections = [(0, truths[0][0], 0.9), (1, np.array([9, 9, 1, 2, 2, 2, 0.0]), 0.8), (1, truths[1][0], 0.7)]
        # TP, FP, TP over two truths: ½ · 1 + ½ · ⅔
        self.assertAlmostEqual(P.average_precision(detections, truths), 0.5 + 1 / 3)
        self.assertIsNone(P.average_precision([], {0: []}))


class Curves(unittest.TestCase):
    def test_hilbert_codes_are_a_path_of_unit_steps(self):
        c = np.array([(x, y, z) for x in range(8) for y in range(8) for z in range(8)])
        h = nets.hilbert(c, bits=3)
        self.assertEqual(sorted(h.tolist()), list(range(512)))
        walk = c[np.argsort(h)]
        self.assertTrue(np.all(np.abs(np.diff(walk, axis=0)).sum(1) == 1))

    def test_z_order_interleaves_the_bits(self):
        self.assertEqual(nets.z_order(np.array([[1, 0, 0], [0, 1, 0], [0, 0, 1], [3, 3, 3]]), bits=2).tolist(),
                         [4, 2, 1, 63])
        transposed = nets.serialise(np.array([[1, 0, 0]]), "z-trans", bits=2)
        self.assertEqual(transposed.tolist(), [2])


class TrainingData(_Fixture):
    def test_the_folder_holds_both_splits_and_its_manifest(self):
        folder = self.prepared()
        self.assertTrue(folder.endswith(".pctd"))
        document = json.load(open(os.path.join(folder, P.MANIFEST), encoding="utf-8"))
        self.assertEqual(document["format"], P.FORMAT)
        self.assertEqual(document["attributes"], ["INTENSITY", "RETURN_NUMBER", "NUMBER_OF_RETURNS"])
        _, training, validation = P.read_training(folder, "classification", "t")
        for split, west in ((training, True), (validation, False)):
            for k in range(len(split)):
                b = split.block(k)
                self.assertLessEqual(len(b["xyz"]), 128)
                self.assertTrue(np.all((b["xyz"][:, 0] < 10) == west))
        west = int((np.asarray(read(self.las).x) <= 10).sum())     # the boundary covers its edge
        self.assertEqual(document["splits"]["training"]["points"], 4 * west)     # each point in four blocks

    def test_codes_of_interest_and_excluded_classes(self):
        folder = arcpy.ddd.PreparePointCloudTrainingData(self.lasd, "6 Meters", "roofs.pctd", self.west,
                                                         class_codes_of_interest=[6], excluded_class_codes=[2])[0]
        _, training, _ = P.read_training(folder, "classification", "t")
        codes = np.concatenate([training.block(k)["classification"] for k in range(len(training))])
        self.assertEqual(set(codes.tolist()), {6})

    def test_relative_height_is_z_less_the_surface(self):
        surface = arcpy.sa.CreateConstantRaster(1.0, "FLOAT", 1, arcpy.Extent(-1, -1, 21, 21))
        surface.save(os.path.join(self.directory, "ground.tif"))
        folder = arcpy.ddd.PreparePointCloudTrainingData(self.lasd, "10 Meters", "rel.pctd", self.west,
                                                         reference_height=os.path.join(self.directory, "ground.tif"))[0]
        _, training, _ = P.read_training(folder, "classification", "t")
        b = training.block(0)
        np.testing.assert_allclose(b["attributes"][:, 7], b["xyz"][:, 2] - 1.0, atol=1e-5)

    def test_a_training_boundary_is_required_without_a_validation_cloud(self):
        with self.assertRaisesRegex(ValueError, "training boundary"):
            arcpy.ddd.PreparePointCloudTrainingData(self.lasd, "10 Meters", "x.pctd")

    def test_a_folder_pro_wrote_is_refused_by_name(self):
        folder = os.path.join(self.directory, "pro.pctd")
        os.makedirs(os.path.join(folder, "train"))
        with self.assertRaisesRegex(NotBuiltError, "HDF5 blocks whose layout Esri does not publish"):
            P.read_training(folder, "classification", "arcpy.ddd.TrainPointCloudClassificationModel")


@unittest.skipUnless(HAVE_TORCH, "torch is not installed")
class Classification(_Fixture):
    def train(self, architecture, epochs=3, **kw):
        folder = self.prepared()
        return arcpy.ddd.TrainPointCloudClassificationModel(folder, self.directory, f"m_{architecture}",
                                                            attributes=["INTENSITY"], max_epochs=epochs,
                                                            learning_rate=0.01, architecture=architecture, **kw)

    def test_every_architecture_trains_and_writes_its_model(self):
        for architecture in ("RANDLANET", "SQN", "POINTCNN", "POINT_TRANSFORMER_V3"):
            with self.subTest(architecture=architecture):
                result = self.train(architecture, epochs=1)
                package, model_stats, epoch_stats = result[0], result[1], result[2]
                self.assertTrue(package.endswith(f"m_{architecture}.dlpk") and os.path.isfile(package))
                definition = json.load(open(package[:-5] + ".emd", encoding="utf-8"))
                self.assertEqual((definition["ModelType"], definition["grapy"]["architecture"]),
                                 ("PointCloudClassification", architecture))
                self.assertEqual(definition["grapy"]["classes"], [2, 6])
                self.assertTrue(os.path.isfile(os.path.join(self.directory, f"m_{architecture}.checkpoints",
                                                            f"m_{architecture}_epoch_1.pth")))
                with open(model_stats, encoding="utf-8") as handle:
                    self.assertEqual(next(csv.reader(handle)), ["Epoch", "Class_Code", "Precision", "Recall",
                                                                "F1_Score"])
                with open(epoch_stats, encoding="utf-8") as handle:
                    self.assertEqual(len(list(csv.reader(handle))), 2)

    def test_pointcnn_learns_the_buildings_and_classifies_the_cloud(self):
        package = self.train("POINTCNN", epochs=4)[0]
        evaluated = arcpy.ddd.EvaluatePointCloudClassificationModel([[package, 4]], self.lasd, self.directory, "ev")
        with open(evaluated[1], encoding="utf-8") as handle:
            row = list(csv.DictReader(handle))[0]
        self.assertGreater(float(row["Accuracy"]), 0.9)
        with open(evaluated[0], encoding="utf-8") as handle:
            matrix = list(csv.DictReader(handle))
        self.assertEqual(sum(int(r["Total"]) for r in matrix), 900 + 4 * 90)
        # Keep the ground as it is, and give every other point the model's class.
        las = read(self.las)
        building = np.asarray(las.classification) == 6
        las.classification = np.where(building, 1, 2).astype(np.uint8)
        las.write(self.las)
        arcpy.ddd.ClassifyPointCloudUsingTrainedModel(self.lasd, package, [2, 6], "PRESERVE_SELECTED", [2])
        after = np.asarray(read(self.las).classification)
        self.assertTrue(np.all(after[~building] == 2))
        self.assertGreater(float((after[building] == 6).mean()), 0.9)
        self.assertLessEqual(set(after[building].tolist()), {2, 6})

    def test_excluded_classes_and_output_classes_are_left_alone(self):
        package = self.train("RANDLANET", epochs=1)[0]
        arcpy.ddd.ClassifyPointCloudUsingTrainedModel(self.lasd, package, [6], excluded_class_codes=[6])
        after = np.asarray(read(self.las).classification)
        self.assertEqual(int((after == 6).sum()) >= 360, True)      # the buildings were not evaluated
        self.assertLessEqual(set(after.tolist()), {2, 6})

    def test_pretrained_model_refines_and_its_attributes_must_match(self):
        package = self.train("RANDLANET", epochs=1)[0]
        folder = os.path.join(self.directory, "scene.pctd")
        again = arcpy.ddd.TrainPointCloudClassificationModel(folder, self.directory, "refined", package,
                                                             attributes=["INTENSITY"], max_epochs=1,
                                                             learning_rate=0.001, learning_rate_strategy="FIXED")[0]
        self.assertEqual(json.load(open(again[:-5] + ".emd"))["grapy"]["architecture"], "RANDLANET")
        with self.assertRaisesRegex(ValueError, "attributes"):
            arcpy.ddd.TrainPointCloudClassificationModel(folder, self.directory, "bad", package, max_epochs=1)

    def test_target_classes_need_a_background(self):
        with self.assertRaisesRegex(ValueError, "background_class"):
            self.train("RANDLANET", epochs=1, target_classes=[6])

    def test_relative_height_models_need_the_surface(self):
        package = self.train("RANDLANET", epochs=1)[0]
        definition = json.load(open(package[:-5] + ".emd"))
        definition["grapy"]["relative_height"] = True
        with open(package[:-5] + ".emd", "w") as handle:
            json.dump(definition, handle)
        with self.assertRaisesRegex(ValueError, "reference_height"):
            arcpy.ddd.ClassifyPointCloudUsingTrainedModel(self.lasd, package[:-5] + ".emd", [6])

    def test_models_grapy_did_not_write_are_refused_by_name(self):
        esri = os.path.join(self.directory, "esri.emd")
        with open(esri, "w") as handle:
            json.dump({"Framework": "PyTorch", "ModelConfiguration": "_point_cnn",
                       "InferenceFunction": "ArcGISPointCloud.py", "ModelType": "PointCloudClassification"}, handle)
        with self.assertRaisesRegex(NotBuiltError, "arcgis.learn"):
            arcpy.ddd.ClassifyPointCloudUsingTrainedModel(self.lasd, esri, [6])
        with self.assertRaisesRegex(NotBuiltError, "network"):
            arcpy.ddd.ClassifyPointCloudUsingTrainedModel(self.lasd, "https://www.arcgis.com/home/item.html?id=1",
                                                          [6])


@unittest.skipUnless(HAVE_TORCH, "torch is not installed")
class Detection(_Fixture):
    def boxes(self, name, keep):
        rows = []
        for cx, cy in CENTRES:
            if keep(cx):
                box = np.array([cx, cy, 2.0, 3.0, 3.0, 4.0, 0.0])
                rows.append(([6], M.from_triangles(M.prism(P.footprint(box), 0.0, 4.0))))
        return M.write(os.path.join(self.directory, name + ".gpkg"), 3857, [Column("Kind", "Integer")], rows)

    def test_prepare_train_and_detect(self):
        train, valid = self.boxes("train", lambda x: x < 10), self.boxes("valid", lambda x: x >= 10)
        folder = arcpy.ddd.PreparePointCloudObjectDetectionTrainingData(
            self.lasd, train, valid, "10 Meters", "objects", self.west, "Kind", validation_code_field="Kind")[0]
        self.assertTrue(folder.endswith(".pcotd"))
        document, training, _ = P.read_training(folder, "object_detection", "t")
        self.assertEqual(document["splits"]["training"]["object_counts"], {"6": 8})     # two objects, four blocks each
        first = next(training.block(k)["boxes"][0] for k in range(len(training)) if len(training.block(k)["boxes"]))
        np.testing.assert_allclose(first[[0, 1, 2, 3, 4, 5, 7]], [5, 5, 2, 3, 3, 4, 6], atol=1e-6)
        self.assertAlmostEqual(math.sin(2 * first[6]), 0.0, delta=1e-9)     # a square: yaw 0 or a quarter turn
        result = arcpy.ddd.TrainPointCloudObjectDetectionModel(
            folder, self.directory, "det", max_epochs=2, learning_rate=0.003, object_descriptions=[[6, "Building"]],
            architecture_settings=[["VOXEL_WIDTH", 0.5], ["VOXEL_HEIGHT", 0.5]])
        definition = json.load(open(result[0][:-5] + ".emd", encoding="utf-8"))
        g = definition["grapy"]
        self.assertEqual(definition["ModelType"], "PointCloudObjectDetection")
        # 10 m blocks of 0.5 m voxels; the height range 1.25 times the tallest top (4 m above the lowest ground point)
        self.assertAlmostEqual(g["z_range"], 1.25 * (4.0 - min(training.block(k)["origin"][2] for k in range(2))),
                               delta=0.2)
        self.assertEqual(g["grid"], [math.ceil(g["z_range"] / 0.5), 20, 20])
        out = arcpy.ddd.DetectObjectsFromPointCloudUsingTrainedModel(self.lasd, result[0], [(6, 0.0, 0.1)],
                                                                     os.path.join(self.directory, "found.gpkg"))[0]
        self.assertTrue(M.is_multipatch(out))
        found = M.read(out)[2]
        self.assertTrue(found)
        boxes = [P.box_of(shape) for _, _, shape in found]
        for k, (_, values, _) in enumerate(found):
            self.assertEqual((values["Object_Code"], values["Description"]), (6, "Building"))
            for j in range(k):
                self.assertLessEqual(P.iou3d(boxes[k], boxes[j]), 0.1 + 1e-9)

    def test_point_transformer_detection_is_refused_by_name(self):
        with self.assertRaisesRegex(NotBuiltError, "names no head"):
            arcpy.ddd.TrainPointCloudObjectDetectionModel("x.pcotd", self.directory, "m",
                                                          architecture="POINT_TRANSFORMER_V3")


class Refused(unittest.TestCase):
    def test_extract_rails_says_why(self):
        with self.assertRaisesRegex(NotBuiltError, "how the crown is traced"):
            arcpy.ddd.ExtractRailsFromPointCloud()

    def test_import_grapy_does_not_import_torch(self):
        import subprocess
        code = ("import sys; sys.path.insert(0, %r); import grapy; import grapy.ddd; "
                "print('torch' in sys.modules)" % os.path.join(os.path.dirname(__file__), ".."))
        out = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, check=True).stdout
        self.assertEqual(out.strip(), "False")


if __name__ == "__main__":
    unittest.main()
