"""LAS datasets (ADR-229) over LAS files the tests write themselves, against hand-worked answers.

The scene: ground points at z 0 on a 1 m grid over 0–20 (cell centres 0.5 … 19.5), class 2, single returns — except
under a 4 m × 4 m roof over 8–12, where the scanner sees only the roof: points at z 6, class 1, each the last of two
returns, with a first return above it at z 6.5. One point at (0.5, 0.5) is withheld.
"""
import os
import sys
import tempfile
import unittest

import numpy as np
import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _las, _tin, _tintools, da  # noqa: E402
from grapy._runtime import NotBuiltError  # noqa: E402

GROUND = 400 - 16          # the 1 m grid less the 16 cells under the roof
ROOF = 16


def write_las(path, x, y, z, classification, return_number, number_of_returns, intensity=None, withheld=None,
              point_format=1, version="1.2", srid=3857):
    import laspy
    from pyproj import CRS
    header = laspy.LasHeader(point_format=point_format, version=version)
    header.scales = [0.001, 0.001, 0.001]
    header.offsets = [0, 0, 0]
    header.add_crs(CRS.from_epsg(srid))
    las = laspy.LasData(header)
    las.x, las.y, las.z = np.asarray(x, float), np.asarray(y, float), np.asarray(z, float)
    las.classification = np.asarray(classification, np.uint8)
    las.return_number = np.asarray(return_number, np.uint8)
    las.number_of_returns = np.asarray(number_of_returns, np.uint8)
    las.intensity = np.asarray(intensity if intensity is not None else np.full(len(las.x), 100), np.uint16)
    if withheld is not None:
        las.withheld = np.asarray(withheld, np.uint8)
    las.write(path)
    return path


def scene(path):
    xs, ys, zs, cls, ret, nret, inten = [], [], [], [], [], [], []
    for i in range(20):
        for j in range(20):
            x, y = i + 0.5, j + 0.5
            if 8 < x < 12 and 8 < y < 12:
                xs += [x, x]
                ys += [y, y]
                zs += [6.5, 6.0]
                cls += [1, 1]
                ret += [1, 2]
                nret += [2, 2]
                inten += [50, 200]
            else:
                xs.append(x)
                ys.append(y)
                zs.append(0.0)
                cls.append(2)
                ret.append(1)
                nret.append(1)
                inten.append(100)
    withheld = np.zeros(len(xs), np.uint8)
    withheld[0] = 1
    return write_las(path, xs, ys, zs, cls, ret, nret, inten, withheld)


def rows(path, fields):
    with da.SearchCursor(path, fields) as cursor:
        return [tuple(r) for r in cursor]


class LasDataset(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.folder = os.path.join(self.directory, "lidar")
        os.makedirs(self.folder)
        self.las = scene(os.path.join(self.folder, "tile.las"))
        self.lasd = arcpy.management.CreateLasDataset(self.folder, os.path.join(self.directory, "scene.lasd"),
                                                      compute_stats="COMPUTE_STATS")[0]
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]

    def polygon_class(self, name, polygons, fields=()):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, "POLYGON", spatial_reference=3857)[0]
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for row in polygons:
                cursor.insertRow(list(row))
        return path

    # ---- the dataset

    def test_describe_answers_a_las_datasets_documented_properties(self):
        described = arcpy.Describe(self.lasd)
        self.assertEqual(described.dataType, "LasDataset")
        self.assertEqual((described.fileCount, described.constraintCount), (1, 0))
        self.assertEqual(described.pointCount, GROUND + 2 * ROOF)
        self.assertTrue(described.hasStatistics)
        self.assertFalse(described.needsUpdateStatistics)
        self.assertFalse(described.usesRelativePath)
        self.assertEqual(described.spatialReference.factoryCode, 3857)
        self.assertEqual((described.extent.XMin, described.extent.YMax), (0.5, 19.5))

    def test_a_lasd_is_grapys_own_json_and_one_from_arcgis_pro_is_refused(self):
        import json
        data = json.load(open(self.lasd, encoding="utf-8"))
        self.assertEqual(data["files"][0]["stats"]["classes"]["2"]["count"], GROUND)
        foreign = os.path.join(self.directory, "pro.lasd")
        open(foreign, "wb").write(b"\x00LASD\x01\x02binary")
        with self.assertRaisesRegex(NotBuiltError, "not published"):
            arcpy.Describe(foreign)

    def test_files_are_added_and_removed_and_statistics_go_stale_when_a_file_changes(self):
        other = write_las(os.path.join(self.directory, "other.las"), [30.0, 31.0, 32.0], [0.0, 1.0, 2.0], [1.0, 2.0, 3.0],
                          [2, 2, 2], [1, 1, 1], [1, 1, 1])
        arcpy.management.AddFilesToLasDataset(self.lasd, other)
        self.assertEqual(arcpy.Describe(self.lasd).fileCount, 2)
        self.assertEqual(arcpy.Describe(self.lasd).pointCount, GROUND + 2 * ROOF + 3)
        write_las(other, [30.0], [0.0], [1.0], [2], [1], [1])
        self.assertTrue(arcpy.Describe(self.lasd).needsUpdateStatistics)
        arcpy.management.LasDatasetStatistics(self.lasd)
        self.assertFalse(arcpy.Describe(self.lasd).needsUpdateStatistics)
        arcpy.management.RemoveFilesFromLasDataset(self.lasd, other)
        self.assertEqual(arcpy.Describe(self.lasd).fileCount, 1)
        self.assertTrue(os.path.exists(other))

    def test_las_dataset_statistics_writes_its_report(self):
        report = os.path.join(self.directory, "stats.csv")
        arcpy.management.LasDatasetStatistics(self.lasd, "OVERWRITE_EXISTING_STATS", report, "DATASET", "COMMA")
        lines = open(report).read().splitlines()
        self.assertEqual(lines[0].split(",")[:3], ["Class_Code", "Point_Count", "Percentage"])
        by_class = {int(line.split(",")[0]): line.split(",") for line in lines[1:]}
        self.assertEqual(int(by_class[1][1]), 2 * ROOF)
        self.assertEqual(by_class[1][3:5], ["6.000", "6.500"])
        arcpy.management.LasDatasetStatistics(self.lasd, out_file=report, summary_level="LAS_FILES")
        header, row = open(report).read().splitlines()
        self.assertEqual(row.split(" ")[:3], ["tile.las", "1.2", str(GROUND + 2 * ROOF)])

    # ---- layers and rasters

    def test_a_layers_filters_choose_the_points_a_raster_counts(self):
        out = arcpy.management.LasPointStatsAsRaster(self.lasd, os.path.join(self.directory, "count.tif"), "POINT_COUNT",
                                                     "CELLSIZE", 1)[0]
        counts = arcpy.RasterToNumPyArray(arcpy.Raster(out), nodata_to_value=0)
        self.assertEqual(counts.shape, (19, 19))   # the points span 0.5–19.5: nineteen whole cells
        self.assertEqual(int(counts.sum()), GROUND + 2 * ROOF - 1)   # the withheld point is left out
        arcpy.management.MakeLasDatasetLayer(self.lasd, "ground", class_code=[2], withheld="INCLUDE_WITHHELD")
        out = arcpy.management.LasPointStatsAsRaster("ground", os.path.join(self.directory, "ground.tif"), "POINT_COUNT",
                                                     "CELLSIZE", 1)[0]
        self.assertEqual(int(arcpy.RasterToNumPyArray(arcpy.Raster(out), nodata_to_value=0).sum()), GROUND)
        arcpy.management.MakeLasDatasetLayer(self.lasd, "first_of_many", return_values=["FIRST_OF_MANY"])
        out = arcpy.management.LasPointStatsAsRaster("first_of_many", os.path.join(self.directory, "first.tif"),
                                                     "Z_RANGE", "CELLSIZE", 1)[0]
        self.assertEqual(int((~np.isnan(arcpy.RasterToNumPyArray(arcpy.Raster(out), nodata_to_value=np.nan))).sum()), 9)
        out = arcpy.management.LasPointStatsAsRaster(self.lasd, os.path.join(self.directory, "class.tif"),
                                                     "PREDOMINANT_CLASS", "CELLSIZE", 1)[0]
        classes = arcpy.RasterToNumPyArray(arcpy.Raster(out), nodata_to_value=0)
        self.assertEqual(int(classes[9, 9]), 1)    # under the roof: row 9 from the top is y 9.5–10.5
        self.assertEqual(int(classes[2, 2]), 2)

    def test_las_dataset_to_raster_bins_and_triangulates(self):
        out = arcpy.conversion.LasDatasetToRaster(self.lasd, os.path.join(self.directory, "dsm.tif"), "ELEVATION",
                                                  "BINNING MAXIMUM NONE", "FLOAT", "CELLSIZE", 1)[0]
        dsm = arcpy.RasterToNumPyArray(arcpy.Raster(out), nodata_to_value=np.nan)
        self.assertAlmostEqual(float(np.nanmax(dsm)), 6.5, 5)
        self.assertAlmostEqual(float(dsm[2, 2]), 0.0, 5)
        arcpy.management.MakeLasDatasetLayer(self.lasd, "bare", class_code=[2])
        out = arcpy.conversion.LasDatasetToRaster("bare", os.path.join(self.directory, "dtm.tif"), "ELEVATION",
                                                  "TRIANGULATION LINEAR NO_THINNING", "FLOAT", "CELLSIZE", 1)[0]
        dtm = arcpy.RasterToNumPyArray(arcpy.Raster(out), nodata_to_value=np.nan)
        self.assertAlmostEqual(float(np.nanmax(np.abs(dtm))), 0.0, 9)   # the ground is flat, under the roof too
        arcpy.management.MakeLasDatasetLayer(self.lasd, "roofless", class_code=[2])
        out = arcpy.conversion.LasDatasetToRaster("roofless", os.path.join(self.directory, "filled.tif"), "ELEVATION",
                                                  "BINNING AVERAGE LINEAR", "INT", "CELLSIZE", 1)[0]
        filled = arcpy.RasterToNumPyArray(arcpy.Raster(out), nodata_to_value=-1)
        self.assertEqual(int(filled[9, 9]), 0)     # the hole under the roof filled across the triangulation
        with self.assertRaisesRegex(NotBuiltError, "IDW"):
            arcpy.conversion.LasDatasetToRaster(self.lasd, os.path.join(self.directory, "idw.tif"), "ELEVATION",
                                                "BINNING IDW NONE")

    # ---- features

    def test_las_point_stats_by_area_fills_each_polygons_fields(self):
        polygons = self.polygon_class("areas", [(shapely.MultiPolygon([shapely.box(8, 8, 12, 12)]),),
                                                (shapely.MultiPolygon([shapely.box(0, 10, 2, 12)]),)])
        arcpy.ddd.LasPointStatsByArea(self.lasd, polygons, ["Z_MIN", "Z_MAX", "Z_MEAN", "POINT_COUNT",
                                                            "STANDARD_DEVIATION"])
        roof, ground = rows(polygons, ["Z_MIN", "Z_MAX", "Z_MEAN", "POINT_COUNT", "STD_DEV"])
        self.assertEqual(roof[:2], (6.0, 6.5))
        self.assertAlmostEqual(roof[2], 6.25, 9)
        self.assertEqual(roof[3], 2 * ROOF)
        self.assertAlmostEqual(roof[4], 0.25, 9)
        self.assertEqual(ground[3], 4)

    def test_las_to_multipoint_and_point_file_information(self):
        arcpy.ddd.LASToMultipoint(self.folder, self.gpkg + "/roof", 1, [1], "LAST_RETURNS")
        (shape,), = rows(self.gpkg + "/roof", ["SHAPE@"])
        xyz = shapely.get_coordinates(shape._shape, include_z=True)
        self.assertEqual(len(xyz), ROOF)
        self.assertTrue(np.all(xyz[:, 2] == 6.0))
        arcpy.ddd.PointFileInformation(self.folder, self.gpkg + "/info", "LAS", "las", None, "NO_RECURSION",
                                       "NO_EXTRUSION", "DECIMAL_POINT", "SUMMARIZE")
        info = {r[0]: r[1:] for r in rows(self.gpkg + "/info", ["Class", "Pt_Count", "Z_Min", "Z_Max", "FileName"])}
        self.assertEqual(info[1][:3], (2 * ROOF, 6.0, 6.5))
        self.assertEqual(info[2][3], "tile.las")

    def test_extract_las_keeps_the_points_within_the_extent(self):
        target = os.path.join(self.directory, "clipped")
        arcpy.ddd.ExtractLas(self.lasd, target, "0 0 5 5", name_suffix="_sw",
                             out_las_dataset=os.path.join(self.directory, "clipped.lasd"))
        clipped = _las.read_file(os.path.join(target, "tile_sw.las"))
        self.assertEqual(len(clipped.points), 25 - 1)       # 5 × 5 grid points, less the withheld one
        self.assertEqual(clipped.header.version.minor, 2)
        self.assertEqual(arcpy.Describe(os.path.join(self.directory, "clipped.lasd")).pointCount, 24)

    # ---- editing classes

    def test_change_las_class_codes_and_set_them_using_features(self):
        arcpy.ddd.ChangeLasClassCodes(self.lasd, [[1, 6, "SET", "NO_CHANGE", "NO_CHANGE", "NO_CHANGE"]])
        a = _las.arrays(_las.read_file(self.las))
        self.assertEqual(int((a["classification"] == 6).sum()), 2 * ROOF)
        self.assertEqual(int(a["synthetic"].sum()), 2 * ROOF)
        self.assertEqual(arcpy.Describe(self.lasd).needsUpdateStatistics, False)
        corner = self.polygon_class("corner", [(shapely.MultiPolygon([shapely.box(15, 15, 20, 20)]),)])
        arcpy.ddd.SetLasClassCodesUsingFeatures(self.lasd, [[corner, 0, 9, "NO_CHANGE", "NO_CHANGE", "NO_CHANGE",
                                                             "NO_CHANGE"]])
        a = _las.arrays(_las.read_file(self.las))
        self.assertEqual(int((a["classification"] == 9).sum()), 25)
        with self.assertRaisesRegex(Exception, "out of range"):
            arcpy.ddd.ChangeLasClassCodes(self.lasd, [[2, 40, "NO_CHANGE", "NO_CHANGE", "NO_CHANGE", "NO_CHANGE"]])

    def test_classify_las_by_height_measures_from_a_tin_of_the_ground(self):
        arcpy.ddd.ClassifyLasByHeight(self.lasd, "GROUND", [[3, 0.5], [4, 2.0], [5, 25.0]], "NONE")
        a = _las.arrays(_las.read_file(self.las))
        self.assertEqual(int((a["classification"] == 5).sum()), 2 * ROOF)   # 6 m and 6.5 m above the ground
        self.assertEqual(int((a["classification"] == 2).sum()), GROUND)

    def test_classify_las_ground_is_an_equivalent_that_finds_the_ground_under_a_roof(self):
        arcpy.ddd.ChangeLasClassCodes(self.lasd, [[-1, 1, "NO_CHANGE", "NO_CHANGE", "NO_CHANGE", "NO_CHANGE"]])
        before = len(arcpy.GetMessages())
        arcpy.ddd.ClassifyLasGround(self.lasd, "STANDARD")
        self.assertIn("progressive morphological filter", arcpy.GetMessages()[before:] or arcpy.GetMessages())
        a = _las.arrays(_las.read_file(self.las))
        ground = a["classification"] == 2
        self.assertEqual(int(ground.sum()), GROUND - 1)   # the withheld ground point is not evaluated, as documented
        self.assertTrue(np.all(a["z"][ground] == 0))
        self.assertTrue(arcpy.ddd.ClassifyLasGround.__grapy_equivalent__.startswith("the progressive morphological"))

    # ---- surfaces from LAS

    def test_las_dataset_to_tin_and_surfaces_read_from_the_dataset(self):
        arcpy.management.MakeLasDatasetLayer(self.lasd, "everything", withheld="INCLUDE_WITHHELD")
        tin = os.path.join(self.directory, "dsm_tin")
        arcpy.ddd.LasDatasetToTin("everything", tin, "WINDOW_SIZE", "MAX", 1)
        described = arcpy.Describe(tin)
        self.assertEqual(described.nodeCount, 400)    # one point per 1 m window: the roof's highest
        self.assertAlmostEqual(_tintools.volume(_tin.load(tin), 6.5, False)[0],
                               19 * 19, 6)            # all of the data area is below 6.5
        polygons = self.polygon_class("pad", [(0.0, shapely.MultiPolygon([shapely.box(9, 9, 11, 11)]))],
                                      [("base", "DOUBLE")])
        arcpy.ddd.PolygonVolume(self.lasd, polygons, "base", "ABOVE")
        (volume,), = rows(polygons, ["Volume"])
        # Both returns stand at each roof x,y; of two nodes at one x,y the later is kept (INFERRED, ADR-229 §5.9), and
        # the last return, at 6, is written after the first.
        self.assertAlmostEqual(volume, 4 * 6.0, 6)
        arcpy.ddd.SurfaceContour("everything", self.gpkg + "/roof_lines", 3)
        self.assertTrue(rows(self.gpkg + "/roof_lines", ["Contour"]))

    def test_what_grapy_cannot_read_is_refused_with_its_reason(self):
        zlas = os.path.join(self.directory, "x.zlas")
        open(zlas, "wb").write(b"zlas")
        with self.assertRaisesRegex(NotBuiltError, "not published"):
            arcpy.management.CreateLasDataset(zlas, os.path.join(self.directory, "z.lasd"))
        with self.assertRaisesRegex(NotBuiltError, "random"):
            arcpy.ddd.LasDatasetToTin(self.lasd, os.path.join(self.directory, "r"), "RANDOM")
        with self.assertRaisesRegex(NotBuiltError, "not documented"):
            arcpy.ListLasDatasetFiles()
        with self.assertRaisesRegex(NotBuiltError, "pyramid"):
            arcpy.management.BuildLasDatasetPyramid(self.lasd)


class Laz(unittest.TestCase):
    """ADR-229 condition 5: LAZ is read and written through lazrs, which CI and the image install. The test does not skip
    without a backend — on a machine with none it fails, which is the point."""

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.las = scene(os.path.join(self.directory, "tile.las"))

    @staticmethod
    def header(path):
        """(point format byte, VLR (user id, record id) pairs) read from the file as LAS 1.2 §2.2 lays them out."""
        import struct
        data = open(path, "rb").read()
        assert data[:4] == b"LASF"
        header_size, = struct.unpack_from("<H", data, 94)
        count, = struct.unpack_from("<I", data, 100)
        vlrs, at = [], header_size
        for _ in range(count):
            user, record, length = struct.unpack_from("<16sHH", data, at + 2)
            vlrs.append((user.rstrip(b"\x00").decode("ascii"), record))
            at += 54 + length
        return data[104], vlrs

    def test_a_laz_file_is_written_compressed_and_read_back_point_for_point(self):
        import laspy
        self.assertTrue(laspy.LazBackend.detect_available(), "no LAZ backend: install lazrs (ADR-229 condition 5)")
        folder = os.path.join(self.directory, "laz")
        arcpy.conversion.ConvertLas(self.las, folder, compression="LAZ")
        laz = os.path.join(folder, "tile.laz")
        point_format, vlrs = self.header(laz)
        # LASzip marks compressed points by setting bit 7 of the point format and adds its VLR, 'laszip encoded' 22204.
        self.assertEqual(point_format, 128 + 1)
        self.assertIn(("laszip encoded", 22204), vlrs)
        self.assertLess(os.path.getsize(laz), os.path.getsize(self.las))
        lasd = arcpy.management.CreateLasDataset(laz, os.path.join(self.directory, "laz.lasd"),
                                                 compute_stats="COMPUTE_STATS")[0]
        described = arcpy.Describe(lasd)
        self.assertEqual(described.pointCount, GROUND + 2 * ROOF)
        self.assertEqual((described.extent.XMin, described.extent.YMax), (0.5, 19.5))
        report = os.path.join(self.directory, "stats.csv")
        arcpy.management.LasDatasetStatistics(lasd, "OVERWRITE_EXISTING_STATS", report, "DATASET", "COMMA")
        by_class = {int(line.split(",")[0]): line.split(",") for line in open(report).read().splitlines()[1:]}
        self.assertEqual((int(by_class[1][1]), int(by_class[2][1])), (2 * ROOF, GROUND))
        self.assertEqual(by_class[1][3:5], ["6.000", "6.500"])

    def test_a_laz_written_by_laspy_alone_reaches_a_tool(self):
        import laspy
        laz = os.path.join(self.directory, "own.laz")
        write_las(laz, [1.0, 2.0, 3.0], [1.0, 2.0, 3.0], [10.0, 20.0, 30.0], [2, 2, 6], [1, 1, 1], [1, 1, 1])
        self.assertGreaterEqual(self.header(laz)[0], 128)
        lasd = arcpy.management.CreateLasDataset(laz, os.path.join(self.directory, "own.lasd"),
                                                 compute_stats="COMPUTE_STATS")[0]
        self.assertEqual(arcpy.Describe(lasd).pointCount, 3)
        self.assertEqual(sorted(laspy.read(laz).z), [10.0, 20.0, 30.0])


if __name__ == "__main__":
    unittest.main()
