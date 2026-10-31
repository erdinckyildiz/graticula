"""Deep learning on imagery — ADR-248.

The parts that need no framework (training data, non-maximum suppression, accuracy, model definitions) are tested
against hand-worked answers and the cited papers' definitions. Training and inference run end to end on tiny synthetic
images on the CPU, from random weights, in seconds; they are skipped where this Python has no torch, and nothing here
downloads anything.

Rasters are written north to south with 10-unit cells on EPSG:3857, the top-left corner at (0, rows·10), so the cell
in row r and column k covers x 10k..10k+10 and y rows·10 − 10r − 10..rows·10 − 10r.
"""
import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
import zipfile

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import _deeplearning as dl, _dlchips as chips  # noqa: E402
from grapy._raster import Raster, _Grid  # noqa: E402
from grapy._runtime import NotBuiltError  # noqa: E402

try:
    import torch  # noqa: F401
    HAVE_TORCH = True
except ImportError:
    HAVE_TORCH = False


def raster(*arrays, cell=10.0, dtype=None):
    arrays = [np.asarray(a, dtype=dtype or np.asarray(a).dtype) for a in arrays]
    rows, columns = arrays[0].shape
    grid = _Grid(0.0, rows * cell, cell, cell, rows, columns, 3857)
    return Raster._of_bands(arrays, [np.zeros(a.shape, bool) for a in arrays], grid)


def box(x0, y0, x1, y1):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1),
                                      arcpy.Point(x0, y1), arcpy.Point(x0, y0)]), arcpy.SpatialReference(3857))


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.scratchWorkspace = self.directory
        arcpy.env.randomGenerator = None

    def features(self, name, rows, fields=(("classvalue", "LONG"),), shape="POLYGON"):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as cursor:
            return [tuple(r) for r in cursor]


# 8 × 8, two bands; a sample of class 1 over columns 1–2, rows 1–2, and one of class 2 over column 5, row 6.
PATTERN = np.arange(64, dtype=np.uint8).reshape(8, 8)


class ExportTrainingData(_Fixture):
    def samples(self):
        return self.features("s", [(1, box(10, 50, 30, 70)), (2, box(50, 10, 60, 20))])

    def export(self, fmt, **kw):
        out = os.path.join(self.directory, fmt + str(len(os.listdir(self.directory))))
        chips.ExportTrainingDataForDeepLearning(raster(PATTERN, PATTERN), out, kw.pop("data", None) or self.samples(),
                                                "TIFF", 4, 4, 2, 2, metadata_format=fmt, **kw)
        return out

    def read(self, path):
        import rasterio
        with rasterio.open(path) as source:
            return source.read()

    def test_pascal_voc_boxes_are_the_devkits_1_based_inclusive_pixels(self):
        out = self.export("PASCAL_VOC_rectangles")
        # The first window (rows 0–3, columns 0–3) holds the class 1 sample on its pixels 1–2 both ways: VOC's
        # 1-based inclusive box is 2..3.
        self.assertEqual(chips.read_voc(os.path.join(out, "labels", "000000000.xml")), [(1, (1.0, 1.0, 3.0, 3.0))])
        text = open(os.path.join(out, "labels", "000000000.xml"), encoding="utf-8").read()
        for tag in ("<xmin>2</xmin>", "<ymin>2</ymin>", "<xmax>3</xmax>", "<ymax>3</ymax>", "<name>1</name>",
                    "<width>4</width>", "<depth>2</depth>"):
            self.assertIn(tag, text)
        # Each image chip is the raster's cells under its window, cell for cell.
        np.testing.assert_array_equal(self.read(os.path.join(out, "images", "000000000.tif"))[0], PATTERN[0:4, 0:4])
        manifest = json.load(open(os.path.join(out, "grapy_training.json"), encoding="utf-8"))
        self.assertEqual(manifest["metadata_format"], "PASCAL_VOC_rectangles")
        self.assertEqual([c["value"] for c in manifest["classes"]], [1, 2])

    def test_kitti_writes_fifteen_columns_using_the_class_and_box(self):
        out = self.export("KITTI_rectangles")
        line = open(os.path.join(out, "labels", "000000000.txt"), encoding="utf-8").read().split()
        self.assertEqual(len(line), 15)
        self.assertEqual(line[0], "1")
        self.assertEqual([float(v) for v in line[4:8]], [1.0, 1.0, 3.0, 3.0])
        self.assertEqual({float(v) for v in line[1:4] + line[8:]}, {0.0})

    def test_only_tiles_with_features_and_all_tiles(self):
        # Windows of 4 stepping 2 over 8 × 8: three each way, nine; the samples fall on six of them.
        self.assertEqual(len(open(os.path.join(self.export("Classified_Tiles", output_nofeature_tiles="ALL_TILES"),
                                               "map.txt")).read().splitlines()), 9)
        lines = open(os.path.join(self.export("Classified_Tiles"), "map.txt")).read().splitlines()
        self.assertEqual(len(lines), 6)

    def test_classified_tiles_burn_each_class_on_its_cells(self):
        out = self.export("Classified_Tiles")
        labels = self.read(os.path.join(out, "labels", "000000000.tif"))[0]
        expected = np.zeros((4, 4), int)
        expected[1:3, 1:3] = 1
        np.testing.assert_array_equal(labels, expected)

    def test_rcnn_masks_number_each_instance(self):
        two = self.features("two", [(1, box(10, 60, 20, 70)), (1, box(20, 50, 30, 60))])
        out = self.export("RCNN_Masks", data=two)
        bands = self.read(os.path.join(out, "labels", "000000000.tif"))
        self.assertEqual(sorted(np.unique(bands[1]).tolist()), [0, 1, 2])
        self.assertEqual(bands[0][1, 1], 1)
        self.assertNotEqual(bands[1][1, 1], bands[1][2, 2])

    def test_labeled_tiles_cut_a_chip_centred_on_each_sample(self):
        out = self.export("Labeled_Tiles")
        lines = open(os.path.join(out, "map.txt")).read().splitlines()
        self.assertEqual(len(lines), 2)
        self.assertEqual(chips.read_voc(os.path.join(out, "labels", "000000001.xml"))[0][0], 2)
        # The class 2 sample's centre (55, 15) — column 5.5, row 6.5 — is the chip's centre: its pixel centres fall in
        # columns 4–7 and rows 5–8, and row 8 is past the raster's edge, written 0.
        expected = np.zeros((4, 4), np.uint8)
        expected[:3] = PATTERN[5:8, 4:8]
        np.testing.assert_array_equal(self.read(os.path.join(out, "images", "000000001.tif"))[0], expected)

    def test_blacken_around_feature_zeroes_the_rest(self):
        out = self.export("Labeled_Tiles", blacken_around_feature="BLACKEN_AROUND_FEATURE")
        image = self.read(os.path.join(out, "images", "000000001.tif"))[0]
        self.assertEqual(int((image != 0).sum()), 1)

    def test_multilabeled_tiles_name_every_class_present(self):
        both = self.features("both", [(1, box(10, 50, 20, 60)), (2, box(20, 40, 30, 50))])
        out = self.export("MultiLabeled_Tiles", data=both)
        found = [chips.read_voc(os.path.join(out, p.split()[1])) for p in open(os.path.join(out, "map.txt"))]
        self.assertIn([1, 2], [sorted(v for v, _ in f) for f in found])

    def test_export_tiles_and_change_detection_write_the_second_image(self):
        second = raster(PATTERN + 1, PATTERN + 1)
        out = self.export("Export_Tiles", in_raster2=second)
        self.assertFalse(os.path.isdir(os.path.join(out, "labels")))
        np.testing.assert_array_equal(self.read(os.path.join(out, "images2", "000000000.tif"))[0], PATTERN[0:4, 0:4] + 1)
        change = self.export("Classified_Tiles", in_raster2=second)
        self.assertTrue(json.load(open(os.path.join(change, "grapy_training.json")))["change_detection"])

    def test_cyclegan_cuts_each_raster_on_its_own_grid(self):
        other = raster(np.ones((4, 4), np.uint8), np.ones((4, 4), np.uint8))
        out = self.export("CycleGAN", in_raster2=other)
        self.assertEqual(len(os.listdir(os.path.join(out, "images2"))), 1)

    def test_panoptic_writes_two_label_folders(self):
        out = self.export("Panoptic_Segmentation", in_instance_data=self.samples())
        self.assertEqual(sorted(os.listdir(os.path.join(out, "instance_labels"))),
                         sorted(os.listdir(os.path.join(out, "labels"))))

    def test_a_rotated_chip_is_the_window_turned(self):
        out = self.export("Classified_Tiles", rotation_angle=90, output_nofeature_tiles="ALL_TILES")
        # 90° gives four chips per window, as the page counts (45° would give eight).
        self.assertEqual(len(open(os.path.join(out, "map.txt")).read().splitlines()), 36)
        straight = self.read(os.path.join(out, "images", "000000000.tif"))[0]
        turned = self.read(os.path.join(out, "images", "000000001.tif"))[0]
        # Turning the window 90° anticlockwise on the map turns the image it holds 90° clockwise.
        np.testing.assert_array_equal(turned, np.rot90(straight, -1))
        self.assertEqual(chips.angles_of(45), [0, 45, 90, 135, 180, 225, 270, 315])

    def test_the_overlap_ratio_drops_a_sample_mostly_off_the_chip(self):
        wide = self.features("wide", [(1, box(30, 50, 70, 60))])
        loose = self.export("PASCAL_VOC_rectangles", data=wide)
        strict = self.export("PASCAL_VOC_rectangles", data=wide, min_polygon_overlap_ratio=0.9)
        count = lambda out: len(open(os.path.join(out, "map.txt")).read().splitlines())
        self.assertGreater(count(loose), count(strict))

    def test_mask_polygons_keep_the_chips_wholly_inside(self):
        mask = self.features("mask", [(1, box(0, 40, 40, 80))])
        out = self.export("Classified_Tiles", in_mask_polygons=mask, output_nofeature_tiles="ALL_TILES")
        self.assertEqual(len(open(os.path.join(out, "map.txt")).read().splitlines()), 1)

    def test_a_classified_raster_is_training_data(self):
        classes = np.zeros((8, 8), np.int32)
        classes[1:3, 1:3] = 3
        out = os.path.join(self.directory, "cr")
        chips.ExportTrainingDataForDeepLearning(raster(PATTERN), out, raster(classes), "TIFF", 4, 4, 2, 2,
                                                metadata_format="PASCAL_VOC_rectangles")
        self.assertEqual(chips.read_voc(os.path.join(out, "labels", "000000000.xml")), [(3, (1.0, 1.0, 3.0, 3.0))])

    def test_what_the_page_rules_out_is_refused(self):
        with self.assertRaises(ValueError):
            self.export("PASCAL_VOC_rectangles", blacken_around_feature="BLACKEN_AROUND_FEATURE")
        with self.assertRaises(ValueError):
            self.export("PASCAL_VOC_rectangles", in_raster2=raster(PATTERN, PATTERN))
        with self.assertRaises(NotBuiltError):
            self.export("PASCAL_VOC_rectangles", start_index=5)
        with self.assertRaises(ValueError):
            chips.ExportTrainingDataForDeepLearning(raster(PATTERN, PATTERN, PATTERN, PATTERN),
                                                    os.path.join(self.directory, "png"), self.samples(), "PNG", 4, 4)


class Suppression(_Fixture):
    def test_greedy_nms_keeps_the_most_confident_of_overlapping_boxes(self):
        import shapely
        a, b, c = shapely.box(0, 0, 10, 10), shapely.box(1, 0, 11, 10), shapely.box(20, 20, 30, 30)
        # IoU(a, b) = 90 / 110 = 0.818.
        self.assertAlmostEqual(chips.iou(a, b), 90 / 110)
        self.assertEqual(chips.greedy_nms([a, b, c], [0.9, 0.8, 0.7], 0.5), [0, 2])
        self.assertEqual(chips.greedy_nms([a, b, c], [0.9, 0.8, 0.7], 0.85), [0, 1, 2])
        self.assertEqual(chips.greedy_nms([a, b, c], [0.7, 0.8, 0.7], 0.5), [1, 2])
        # Different classes survive the first pass and meet in the second.
        self.assertEqual(chips.greedy_nms([a, b], [0.9, 0.8], 0.5, [1, 2]), [0, 1])

    def test_the_tool_runs_its_two_passes(self):
        path = self.features("det", [(1, 0.9, box(0, 0, 10, 10)), (1, 0.8, box(1, 0, 11, 10)),
                                     (2, 0.95, box(0, 1, 10, 11)), (2, 0.5, box(50, 50, 60, 60))],
                             (("Classvalue", "LONG"), ("Confidence", "DOUBLE")))
        out = os.path.join(self.directory, "kept.gpkg")
        chips.NonMaximumSuppression(path, "Confidence", out, None, 0.5)
        self.assertEqual(sorted(r[0] for r in self.rows(out, ["Confidence"])), [0.5, 0.95])


class ObjectAccuracy(_Fixture):
    def test_average_precision_is_vocs_all_point_area(self):
        # Two truths; detections in descending score: TP, FP, TP. Precision 1, 1/2, 2/3 at recall 1/2, 1/2, 1; made
        # monotone, the area is 1/2·1 + 1/2·2/3.
        self.assertAlmostEqual(chips.average_precision([0.9, 0.8, 0.7], [1, 0, 1], 2), 0.5 + 0.5 * 2 / 3)
        self.assertAlmostEqual(chips.average_precision([0.9], [1], 1), 1.0)
        self.assertEqual(chips.average_precision([], [], 0), 0.0)

    def test_a_second_detection_of_a_truth_is_a_false_positive(self):
        import shapely
        truth = [shapely.box(0, 0, 10, 10)]
        self.assertEqual(chips.match([(shapely.box(0, 0, 10, 10), 0.9), (shapely.box(0, 0, 10, 9), 0.8)], truth, 0.5),
                         [1, 0])

    def test_the_table_holds_each_class_and_all(self):
        truth = self.features("truth", [(1, box(0, 0, 10, 10)), (1, box(20, 0, 30, 10)), (2, box(40, 0, 50, 10))])
        found = self.features("found", [(1, 0.9, box(0, 0, 10, 10)), (1, 0.8, box(100, 0, 110, 10)),
                                        (1, 0.7, box(21, 0, 31, 10)), (2, 0.6, box(40, 0, 50, 10))],
                              (("classvalue", "LONG"), ("Confidence", "DOUBLE")))
        out = os.path.join(self.directory, "acc.gpkg")
        arcpy.ia.ComputeAccuracyForObjectDetection(found, truth, out)
        table = {r[0]: r[1:] for r in self.rows(out, ["Class", "Precision", "Recall", "F1_Score", "AP", "True_Positive",
                                                      "False_Positive", "False_Negative"])}
        precision, recall, f1, ap, tp, fp, fn = table["1"]
        self.assertEqual((tp, fp, fn), (2, 1, 0))
        self.assertAlmostEqual(precision, 2 / 3)
        self.assertAlmostEqual(recall, 1.0)
        self.assertAlmostEqual(f1, 0.8)
        self.assertAlmostEqual(ap, 0.5 + 0.5 * 2 / 3)
        self.assertEqual(table["All"][4:], (3, 1, 0))
        self.assertAlmostEqual(table["All"][3], (0.5 + 0.5 * 2 / 3 + 1.0) / 2)
        with self.assertRaises(NotBuiltError):
            arcpy.ia.ComputeAccuracyForObjectDetection(found, truth, out, os.path.join(self.directory, "r.pdf"))


class PixelAccuracy(_Fixture):
    def test_kappa_is_cohens(self):
        # Classified rows, reference columns: po = 0.85, pe = (50·55 + 50·45)/100² = 0.5, kappa = 0.35 / 0.5.
        self.assertAlmostEqual(chips.kappa(np.array([[45, 5], [10, 40]])), 0.7)

    def test_a_perfect_classification_scores_one(self):
        classes = np.repeat(np.array([[1, 1, 2, 2]], np.int32), 4, axis=0)
        out = os.path.join(self.directory, "cm.gpkg")
        arcpy.ia.ComputeAccuracyForPixelClassification(raster(classes), raster(classes), out, 8)
        rows = {r[0]: r[1:] for r in self.rows(out, ["ClassValue", "C_1", "C_2", "Total", "U_Accuracy", "Kappa"])}
        self.assertEqual(set(rows), {"C_1", "C_2", "Total", "P_Accuracy", "Kappa", "IoU"})
        self.assertEqual(rows["C_1"][:3], (4.0, 0.0, 4.0))
        self.assertEqual(rows["C_1"][3], 1.0)
        self.assertEqual(rows["Kappa"][4], 1.0)
        self.assertEqual(rows["P_Accuracy"][3], 1.0)

    def test_the_matrix_counts_errors_against_polygons(self):
        classes = np.ones((4, 4), np.int32)
        truth = self.features("t", [(1, box(0, 20, 40, 40)), (2, box(0, 0, 40, 20))])
        out = os.path.join(self.directory, "cm2.gpkg")
        arcpy.ia.ComputeAccuracyForPixelClassification(raster(classes), truth, out, 16, "RANDOM")
        rows = {r[0]: r[1:] for r in self.rows(out, ["ClassValue", "C_1", "C_2", "Total", "U_Accuracy"])}
        self.assertEqual(rows["C_1"][:3], (8.0, 8.0, 16.0))
        self.assertEqual(rows["C_1"][3], 0.5)


class ModelDefinitions(_Fixture):
    def emd(self, **keys):
        document = {"Framework": "PyTorch", "ModelConfiguration": "grapy", "ModelType": "ImageClassification",
                    "ModelFile": "m.pth", "grapy": {"architecture": "UNET"}}
        document.update(keys)
        path = os.path.join(self.directory, "m.emd")
        json.dump(document, open(path, "w"))
        return path

    def test_arcgis_learn_and_other_frameworks_are_refused_by_name(self):
        with self.assertRaises(NotBuiltError) as said:
            dl.read_definition(self.emd(InferenceFunction="ArcGISImageClassifier.py"), "Tool")
        self.assertIn("arcgis.learn", str(said.exception))
        with self.assertRaises(NotBuiltError) as said:
            dl.read_definition(self.emd(Framework="TensorFlow"), "Tool")
        self.assertIn("TensorFlow", str(said.exception))
        with self.assertRaises(NotBuiltError):
            dl.read_definition(self.emd(ModelConfiguration="_FasterRCNN"), "Tool")
        self.assertEqual(dl.read_definition(self.emd(), "Tool")["ModelType"], "ImageClassification")

    def test_a_package_holds_one_definition(self):
        path = self.emd()
        package = os.path.join(self.directory, "m.dlpk")
        with zipfile.ZipFile(package, "w") as z:
            z.write(path, "m.emd")
        self.assertEqual(dl.read_definition(package, "Tool").grapy["architecture"], "UNET")
        with zipfile.ZipFile(package, "a") as z:
            z.write(path, "other.emd")
        with self.assertRaises(ValueError):
            dl.read_definition(package, "Tool")

    def test_without_torch_a_tool_names_the_missing_package(self):
        code = textwrap.dedent(f"""
            import sys
            class Block:
                def find_spec(self, name, path=None, target=None):
                    if name.split('.')[0] in ('torch', 'torchvision'):
                        raise ImportError('no ' + name)
            sys.meta_path.insert(0, Block())
            sys.path.insert(0, {os.path.dirname(os.path.dirname(__file__))!r})
            import grapy
            assert 'torch' not in sys.modules
            from grapy import _deeplearning
            try:
                _deeplearning.framework('TrainDeepLearningModel')
            except _deeplearning.FrameworkMissing as e:
                print(e)
        """)
        said = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, timeout=120)
        self.assertEqual(said.returncode, 0, said.stderr)
        self.assertIn("'torch' and 'torchvision'", said.stdout)

    def test_the_refused_names_say_why(self):
        for name, words in (("DetectObjectsUsingText", "Grounding DINO"), ("DetectControlPoints", "trained by Esri")):
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.ia, name)()
            self.assertIn(words, str(said.exception))


# ---- training and inference, on synthetic images

def scene(size=64, seed=0, squares=3, side=12, shift=0.0):
    """A 3-band image of noise with bright squares, and the squares' cells: a task a network learns in a few epochs."""
    rng = np.random.default_rng(seed)
    image = rng.normal(20, 4, (3, size, size))
    truth = np.zeros((size, size), np.int32)
    for k in range(squares):
        r, c = rng.integers(2, size - side - 2, 2)
        image[:, r:r + side, c:c + side] += 120 + shift
        truth[r:r + side, c:c + side] = 1
    return image.astype(np.float32), truth


@unittest.skipUnless(HAVE_TORCH, "torch is not installed in this Python")
class Training(_Fixture):
    def setUp(self):
        super().setUp()
        image, truth = scene()
        self.image, self.truth = image, truth
        self.raster = raster(*image)
        from rasterio.features import shapes
        import shapely.geometry
        polygons = [shapely.geometry.shape(g) for g, v in shapes(truth, mask=truth == 1,
                                                                 transform=self.raster._grid.transform())]
        self.samples = self.features("sq", [(1, p) for p in polygons])

    def export(self, fmt, size=32, stride=16, **kw):
        out = os.path.join(self.directory, f"chips_{fmt}")
        chips.ExportTrainingDataForDeepLearning(self.raster, out, kw.pop("data", self.samples), "TIFF", size, size,
                                                stride, stride, metadata_format=fmt,
                                                output_nofeature_tiles="ALL_TILES", **kw)
        return out

    _trained = {}
    _models = tempfile.mkdtemp()

    def trained(self, fmt, model_type):
        """One model of a type, trained once for the tests that only run it (its data is the same in every test)."""
        if (fmt, model_type) not in self._trained:
            self._trained[(fmt, model_type)] = self.train(fmt, model_type, epochs=1,
                                                          folder=os.path.join(self._models, model_type))
        return self._trained[(fmt, model_type)]

    def train(self, fmt, model_type, epochs=2, folder=None, **kw):
        out = folder or os.path.join(self.directory, f"model_{model_type}")
        result = arcpy.ia.TrainDeepLearningModel(self.export(fmt, **kw.pop("export", {})), out, epochs, model_type,
                                                 2, learning_rate=kw.pop("learning_rate", 1e-3),
                                                 backbone_model=kw.pop("backbone_model", "RESNET18"),
                                                 augmentation=kw.pop("augmentation", "NONE"), **kw)
        return result.getOutput(0)

    def test_unet_trains_and_classifies_pixels(self):
        emd = self.train("Classified_Tiles", "UNET", epochs=6, learning_rate=1e-2)
        for suffix in (".emd", ".pth", ".dlpk"):
            self.assertTrue(os.path.isfile(os.path.splitext(emd)[0] + suffix))
        document = json.load(open(emd))
        self.assertEqual((document["Framework"], document["ModelType"], document["ImageWidth"]),
                         ("PyTorch", "ImageClassification", 32))
        classified = arcpy.ia.ClassifyPixelsUsingDeepLearning(self.raster, os.path.splitext(emd)[0] + ".dlpk",
                                                              "padding 4;batch_size 4")
        values = np.asarray(classified._array)
        self.assertEqual(values.shape, (64, 64))
        self.assertTrue(set(np.unique(values)) <= {0, 1})
        # Six epochs on bright squares: most cells are right.
        self.assertGreater(float((values == self.truth).mean()), 0.8)

    def test_the_tiles_cover_the_raster_once_each(self):
        cover = np.zeros((50, 70), int)
        for r, c in __import__("grapy._dltools", fromlist=["tiles"]).tiles(50, 70, 32, 4):
            r0, c0 = max(r + 4, 0), max(c + 4, 0)
            cover[r0:min(r + 28, 50), c0:min(c + 28, 70)] += 1
        self.assertEqual(set(np.unique(cover)), {1})

    def test_a_detector_trains_and_writes_objects(self):
        for model_type in ("FASTERRCNN", "RETINANET"):
            emd = self.trained("PASCAL_VOC_rectangles", model_type)
            out = os.path.join(self.directory, f"objects_{model_type}.gpkg")
            arcpy.ia.DetectObjectsUsingDeepLearning(self.raster, out, emd, "threshold 0;padding 4", "NMS",
                                                    max_overlap_ratio=0.3)
            fields = [f.name for f in arcpy.ListFields(out)]
            for name in ("Class", "Classvalue", "Confidence"):
                self.assertIn(name, fields)
            rows = self.rows(out, ["Confidence", "SHAPE@"])
            self.assertTrue(all(0 <= r[0] <= 1 for r in rows))

    def test_mask_rcnn_writes_outlines(self):
        emd = self.train("RCNN_Masks", "MASKRCNN", epochs=1)
        out = os.path.join(self.directory, "masks.gpkg")
        arcpy.ia.DetectObjectsUsingDeepLearning(self.raster, out, emd, "threshold 0")
        self.assertTrue(os.path.isfile(out))

    def test_the_classifier_labels_objects(self):
        emd = self.train("Labeled_Tiles", "FEATURE_CLASSIFIER", epochs=1)
        out = os.path.join(self.directory, "labelled.gpkg")
        arcpy.ia.ClassifyObjectsUsingDeepLearning(self.raster, out, emd, self.samples)
        rows = self.rows(out, ["ClassLabel", "Confidence"])
        self.assertEqual(len(rows), 3)
        self.assertTrue(all(r[0] == "1" and r[1] == 1.0 for r in rows))

    def test_change_detection(self):
        after_image, _ = scene(seed=1)
        after = raster(*after_image)
        changed = (self.truth == 1).astype(np.int32)
        out = os.path.join(self.directory, "change")
        chips.ExportTrainingDataForDeepLearning(self.raster, out, raster(changed), "TIFF", 32, 32, 16, 16,
                                                output_nofeature_tiles="ALL_TILES", metadata_format="Classified_Tiles",
                                                in_raster2=after)
        model = os.path.join(self.directory, "cd")
        emd = arcpy.ia.TrainDeepLearningModel(out, model, 1, "CHANGEDETECTOR", 2, learning_rate=1e-3,
                                              backbone_model="RESNET18", augmentation="NONE").getOutput(0)
        result = arcpy.ia.DetectChangeUsingDeepLearning(self.raster, after, os.path.join(self.directory, "c.tif"), emd)
        self.assertEqual(Raster(result.getOutput(0))._array.shape, (64, 64))

    def test_pix2pix_and_cyclegan_translate(self):
        target = raster(*(self.image[:1] * 0.5))
        emd = self.train("Export_Tiles", "PIX2PIX", epochs=1, backbone_model=None, export={"in_raster2": target})
        out = arcpy.ia.TranslatePixelsUsingDeepLearning(self.raster, emd, "padding 4")
        self.assertEqual(out.bandCount, 1)
        emd = self.train("CycleGAN", "CYCLEGAN", epochs=1, backbone_model=None, export={"in_raster2": target})
        back = arcpy.ia.TranslatePixelsUsingDeepLearning(target, emd, "direction BtoA;padding 4")
        self.assertEqual(back.bandCount, 3)
        with self.assertRaises(NotBuiltError):
            self.train("Export_Tiles", "PIX2PIX", epochs=1, backbone_model="RESNET18", export={"in_raster2": target})

    def test_a_pretrained_model_is_fine_tuned_with_its_backbone_frozen(self):
        emd = self.train("Classified_Tiles", "PSPNET", epochs=1)
        tuned = os.path.join(self.directory, "tuned")
        arcpy.ia.TrainDeepLearningModel(self.export("Classified_Tiles"), tuned, 1, "PSPNET", 2, learning_rate=1e-3,
                                        backbone_model="RESNET18", pretrained_model=emd, augmentation="DEFAULT")
        before, after = torch.load(emd.replace(".emd", ".pth")), torch.load(os.path.join(tuned, "tuned.pth"))
        self.assertTrue(torch.equal(before["backbone.layer1.0.conv1.weight"], after["backbone.layer1.0.conv1.weight"]))
        self.assertFalse(torch.equal(before["head.last.4.weight"], after["head.last.4.weight"]))

    def test_deeplab_model_to_ecd_feeds_classify_raster(self):
        emd = self.train("Classified_Tiles", "DEEPLAB", epochs=1)
        info = os.path.join(self.directory, "info.json")
        json.dump({"Classifier": "PyTorch", "NumberRasterBands": 3, "Classes": [{"Value": 1, "Name": "Roof",
                                                                                   "Color": [255, 0, 0]}]},
                  open(info, "w"))
        ecd = os.path.join(self.directory, "dl.ecd")
        arcpy.sa.DeepLearningModelToEcd(emd, info, ecd)
        self.assertEqual(json.load(open(ecd))["classes"][1]["name"], "Roof")
        classified = arcpy.ia.ClassifyRaster(self.raster, ecd)
        self.assertTrue(set(np.unique(classified._array)) <= {0, 1})
        json.dump({"Classifier": "CNTK", "Classes": []}, open(info, "w"))
        with self.assertRaises(NotBuiltError):
            arcpy.sa.DeepLearningModelToEcd(emd, info, ecd)

    def test_autodl_searches_and_keeps_the_best(self):
        out = os.path.join(self.directory, "auto.dlpk")
        result = arcpy.ia.TrainUsingAutoDL(self.export("Classified_Tiles"), out, None, 0.0003, "BASIC",
                                           ["UnetClassifier", "FCN"], "SAVE_ALL_MODELS")
        self.assertTrue(os.path.isfile(result.getOutput(0)))
        self.assertTrue(os.path.isfile(os.path.join(self.directory, "FCN.dlpk")))
        with self.assertRaises(NotBuiltError):
            arcpy.ia.TrainUsingAutoDL(self.export("Classified_Tiles"), out, networks=["ATSS"])

    def test_extract_features_regularizes_detections(self):
        emd = self.trained("PASCAL_VOC_rectangles", "FASTERRCNN")
        result = arcpy.ia.ExtractFeaturesUsingAIModels(self.raster, "Infer and Postprocess", self.directory, "fx",
                                                       additional_models=[[emd, "Polygon Regularization"]],
                                                       confidence_threshold=0.0,
                                                       out_summary=os.path.join(self.directory, "summary.gpkg"))
        self.assertTrue(os.path.isfile(result.getOutput(0).split(".gpkg")[0] + ".gpkg"))
        for refused in ("Line Regularization", "Polygon Segmentation"):
            with self.assertRaises(NotBuiltError):
                arcpy.ia.ExtractFeaturesUsingAIModels(self.raster, "Infer and Postprocess", self.directory, "fx",
                                                      additional_models=[[emd, refused]])
        with self.assertRaises(NotBuiltError):
            arcpy.ia.ExtractFeaturesUsingAIModels(self.raster, "Infer and Postprocess", self.directory, "fx",
                                                  pretrained_models="Building Footprint Extraction - USA")

    def test_what_grapy_does_not_build_is_refused_by_name(self):
        folder = self.export("Classified_Tiles")
        for kw, words in (({"model_type": "YOLOV3"}, "YOLOv3"), ({"model_type": "MMSEGMENTATION"}, "OpenMMLab"),
                          ({"backbone_model": "DARKNET53"}, "Darknet"), ({"backbone_model": "timm:resnet31"}, "timm"),
                          ({"tensorboard": "ENABLE_TENSORBOARD"}, "tensorboard"), ({"augmentation": "FILE"}, "fastai"),
                          ({"monitor": "MULTI_LABEL_FBETA"}, "beta"), ({"arguments": "mixup True"}, "mixup")):
            args = dict({"model_type": "UNET", "backbone_model": "RESNET18"}, **kw)
            with self.assertRaises(NotBuiltError) as said:
                arcpy.ia.TrainDeepLearningModel(folder, os.path.join(self.directory, "x"), 1, args.pop("model_type"),
                                                **args)
            self.assertIn(words, str(said.exception))

    def test_augmentation_turns_boxes_with_the_image(self):
        from grapy import _dltools as tools
        item = {"image": torch.zeros(1, 8, 8), "boxes": torch.tensor([[0.0, 0.0, 2.0, 1.0]]),
                "labels": torch.tensor([1])}
        item["image"][0, 0, 0:2] = 1
        # A quarter turn of the normalised grid: the image's top-left 2 × 1 block goes where its box goes.
        theta = np.array([[0, -1, 0], [1, 0, 0], [0, 0, 1]], float)
        turned = tools._warp(item, theta, (8, 8), torch)
        x0, y0, x1, y1 = turned["boxes"][0].tolist()
        cells = torch.nonzero(turned["image"][0] > 0.5)
        self.assertEqual((cells[:, 1].min().item(), cells[:, 0].min().item()), (int(x0), int(y0)))
        self.assertEqual((cells[:, 1].max().item() + 1, cells[:, 0].max().item() + 1), (int(x1), int(y1)))

    def test_red_band_initialisation_copies_the_first_channel(self):
        conv = torch.nn.Conv2d(3, 4, 3)
        wider = dl.adapt_first_convolution(conv, 5, "RED_BAND")
        self.assertTrue(torch.equal(wider.weight[:, 3], conv.weight[:, 0]))
        self.assertTrue(torch.equal(wider.weight[:, :3], conv.weight))


class ImageSpace(_Fixture):
    """Transform Image Shapes on a mosaic dataset of one Frame Camera photograph (ADR-234): a nadir view from 25 m with
    a 5 mm lens and 10 µm pixels, 64 × 64 pixels — 5 cm a pixel on the ground."""

    def mosaic(self):
        import rasterio
        import warnings
        image, _ = scene()
        path = os.path.join(self.directory, "photo.tif")
        with warnings.catch_warnings():   # a photograph has no georeference
            warnings.simplefilter("ignore")
            with rasterio.open(path, "w", driver="GTiff", width=64, height=64, count=1, dtype="float32") as out:
                out.write(image[0], 1)
        frames = os.path.join(self.directory, "frames.csv")
        with open(frames, "w", encoding="utf-8") as handle:
            handle.write("Raster,PerspectiveX,PerspectiveY,PerspectiveZ,Omega,Phi,Kappa,FocalLength,PixelSize\n")
            handle.write(f"{path},100,200,25,0,0,0,5000,10\n")
        gpkg = arcpy.management.CreateFileGDB(self.directory, "om")[0]
        md = arcpy.management.CreateMosaicDataset(gpkg, "md", 3857)[0]
        arcpy.management.AddRastersToMosaicDataset(md, "Frame Camera", frames)
        return md

    def test_an_image_shape_is_carried_to_the_ground_through_the_frame(self):
        md = self.mosaic()
        shapes = self.features("ishapes", [("POLYGON ((0 0, 64 0, 64 32, 0 32, 0 0))", 1, None),
                                           ("POLYGON ((32 32, 33 32, 33 33, 32 32))", 1, None)],
                               (("iShape", "TEXT"), ("ImageID", "LONG")))
        out = os.path.join(self.directory, "ground.gpkg")
        arcpy.ia.TransformImageShapes(shapes, out, md)
        ground = [r[0] for r in self.rows(out, ["SHAPE@"])]
        # The image's top half: 64 pixels of 5 cm across, 32 down, the photograph's centre (100, 200) on its bottom
        # edge's middle — a nadir view at Z = 0 from 25 m with f = 5 mm.
        x0, y0, x1, y1 = ground[0]._shape.bounds
        self.assertAlmostEqual(x0, 100 - 1.6, places=6)
        self.assertAlmostEqual(x1, 100 + 1.6, places=6)
        self.assertAlmostEqual(y0, 200, places=6)
        self.assertAlmostEqual(y1, 200 + 1.6, places=6)
        # Over a DEM 5 m high everywhere the rays meet the ground 20 m below the lens: four fifths the size.
        dem = raster(np.full((10, 10), 5.0), cell=1.0)
        dem._grid = _Grid(90, 210, 2, 2, 10, 10, 3857)
        out2 = os.path.join(self.directory, "ground_dem.gpkg")
        arcpy.ia.TransformImageShapes(shapes, out2, md, dem)
        x0, y0, x1, y1 = self.rows(out2, ["SHAPE@"])[0][0]._shape.bounds
        self.assertAlmostEqual(x1 - x0, 3.2 * 0.8, places=6)

    def test_the_input_needs_its_image_fields(self):
        md = self.mosaic()
        plain = self.features("plain", [(1, box(0, 0, 1, 1))])
        with self.assertRaises(ValueError):
            arcpy.ia.TransformImageShapes(plain, os.path.join(self.directory, "x.gpkg"), md)

    @unittest.skipUnless(HAVE_TORCH, "torch is not installed in this Python")
    def test_pixel_space_detections_round_trip(self):
        md = self.mosaic()
        image, truth = scene()
        one = raster(image[0])
        from rasterio.features import shapes
        import shapely.geometry
        samples = self.features("one", [(1, shapely.geometry.shape(g)) for g, _ in
                                        shapes(truth, mask=truth == 1, transform=one._grid.transform())])
        folder = os.path.join(self.directory, "voc")
        chips.ExportTrainingDataForDeepLearning(one, folder, samples, "TIFF", 32, 32, 16, 16,
                                                output_nofeature_tiles="ALL_TILES")
        emd = arcpy.ia.TrainDeepLearningModel(folder, os.path.join(self.directory, "det1"), 1, "RETINANET", 2,
                                              learning_rate=1e-3, backbone_model="RESNET18",
                                              augmentation="NONE").getOutput(0)
        out = os.path.join(self.directory, "pix.gpkg")
        arcpy.ia.DetectObjectsUsingDeepLearning(md, out, emd, "threshold 0;padding 0", use_pixelspace="PIXELSPACE")
        rows = self.rows(out, ["iShape", "ImageID", "SHAPE@"])
        self.assertTrue(rows)
        self.assertEqual({r[1] for r in rows}, {1})
        again = os.path.join(self.directory, "again.gpkg")
        arcpy.ia.TransformImageShapes(out, again, md)
        for (_, _, first), (second,) in zip(rows, self.rows(again, ["SHAPE@"])):
            self.assertLess(first._shape.symmetric_difference(second._shape).area, 1e-9)


@unittest.skipUnless(HAVE_TORCH and sys.platform == "linux", "the fence is Linux's; torch is needed")
class TrainingInsideTheFence(unittest.TestCase):
    """ADR-248 §11 condition 2: a fenced tool imports torch and trains under the server's default limits."""

    def test_a_fenced_tool_trains_a_network(self):
        from test_grapy import _run
        directory = tempfile.mkdtemp()
        toolbox = textwrap.dedent('''
            import grapy as arcpy

            class Toolbox:
                def __init__(self):
                    self.tools = [Learn]

            class Learn:
                def getParameterInfo(self):
                    return []

                def execute(self, parameters, messages):
                    import torch
                    from grapy import _deeplearning as dl, _dlnets as nets
                    dl.threads()
                    model = nets.build("UNET", "RESNET18", 3, 2, (32, 32))
                    optimiser = torch.optim.Adam(model.parameters(), 1e-3)
                    x, y = torch.randn(2, 3, 32, 32), torch.randint(0, 2, (2, 32, 32))
                    for _ in range(3):
                        loss = torch.nn.functional.cross_entropy(model(x), y)
                        optimiser.zero_grad()
                        loss.backward()
                        optimiser.step()
                    messages.addMessage("trained %.3f" % float(loss))
        ''')
        with open(os.path.join(directory, "learn.pyt"), "w") as out:
            out.write(toolbox)
        result = _run(directory, {"mode": "run", "toolbox": "learn.pyt", "tool": "Learn",
                                  "limits": {"memory": 2 << 30, "processes": 1024}}, unfenced=False)
        self.assertTrue(result["ok"], result)
        self.assertTrue(result["fence"], result)


if __name__ == "__main__":
    unittest.main()
