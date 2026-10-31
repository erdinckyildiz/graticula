"""ADR-229's second batch of LAS tools, over LAS files the tests write themselves, against hand-worked answers.

Most tests use test_las's scene: ground points at z 0 on a 1 m grid over 0–20 (cell centres 0.5 … 19.5), class 2,
single returns — except under a 4 m × 4 m roof over 8–12, where each cell holds two returns of class 1 at z 6.5 and
z 6. The point at (0.5, 0.5) is withheld. 384 ground points and 32 roof points, 416 in all.
"""
import os
import re
import sys
import tempfile
import unittest

import numpy as np
import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, os.path.dirname(__file__))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _las, da  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402
from test_las import scene  # noqa: E402

GROUND, ROOF = 384, 32


def las_file(path, x, y, z, classification=None, point_format=1, version="1.2", srid=3857, **fields):
    import laspy
    from pyproj import CRS
    header = laspy.LasHeader(point_format=point_format, version=version)
    header.scales = [0.001, 0.001, 0.001]
    header.offsets = [0, 0, 0]
    if srid:
        header.add_crs(CRS.from_epsg(srid))
    las = laspy.LasData(header)
    las.x, las.y, las.z = np.asarray(x, float), np.asarray(y, float), np.asarray(z, float)
    n = len(las.x)
    las.classification = np.asarray(classification if classification is not None else np.full(n, 2), np.uint8)
    las.return_number = np.ones(n, np.uint8)
    las.number_of_returns = np.ones(n, np.uint8)
    for name, values in fields.items():
        las[name] = np.asarray(values)
    las.write(path)
    return path


def read(path):
    import laspy
    return laspy.read(path)


def rows(path, fields):
    with da.SearchCursor(path, fields) as cursor:
        return [tuple(r) for r in cursor]


class Base(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.folder = os.path.join(self.directory, "lidar")
        os.makedirs(self.folder)
        self.las = scene(os.path.join(self.folder, "tile.las"))
        self.lasd = arcpy.management.CreateLasDataset(self.folder, os.path.join(self.directory, "scene.lasd"))[0]
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]

    def out(self, name):
        return os.path.join(self.directory, name)


class Conversion(Base):
    def test_convertlas_raises_the_version_and_changes_the_point_format(self):
        folder = arcpy.conversion.ConvertLas(self.las, self.out("v14"), "1.4", 6)[0]
        out = read(os.path.join(folder, "tile.las"))
        self.assertEqual((out.header.version.minor, out.header.point_format.id), (4, 6))
        self.assertEqual(len(out.points), GROUND + ROOF)
        self.assertEqual(int((np.asarray(out.classification) == 2).sum()), GROUND)
        self.assertEqual(int(np.asarray(out.withheld).sum()), 1)
        self.assertEqual(_las.srid_of(out), 3857)
        self.assertAlmostEqual(float(np.asarray(out.z).max()), 6.5)

    def test_convertlas_refuses_to_lower_a_1_4_file_and_to_lose_class_codes(self):
        source = las_file(self.out("high.las"), [0, 1], [0, 1], [0, 0], [40, 2], point_format=6, version="1.4")
        with self.assertRaisesRegex(ExecuteError, "cannot be converted to an earlier version"):
            arcpy.conversion.ConvertLas(source, self.out("down"), "1.2")
        with self.assertRaisesRegex(ExecuteError, "class codes 0–31"):
            arcpy.conversion.ConvertLas(source, self.out("fmt1"), "SAME_AS_INPUT", 1)
        with self.assertRaisesRegex(NotBuiltError, "not published"):
            arcpy.conversion.ConvertLas(source, self.out("z"), compression="ZLAS")

    def test_convertlas_defines_a_coordinate_system_for_files_missing_one(self):
        os.makedirs(os.path.join(self.directory, "bare"))
        bare = las_file(os.path.join(self.directory, "bare", "bare.las"), [0, 1], [0, 1], [0, 0], srid=0)
        folder = arcpy.conversion.ConvertLas(os.path.dirname(bare), self.out("defined"),
                                             define_coordinate_system="FILES_MISSING_PROJECTION",
                                             in_coordinate_system=32635)[0]
        self.assertEqual(_las.srid_of(read(os.path.join(folder, "bare.las"))), 32635)

    def test_projectlas_projects_web_mercator_to_geographic(self):
        # x = R·λ on the sphere of radius 6378137: 111319.49079… m is one degree of longitude.
        source = las_file(self.out("merc.las"), [0.0, 111319.490793], [0.0, 0.0], [5.0, 7.0])
        folder = arcpy.management.ProjectLAS(source, self.out("geo"), 4326,
                                             name_modifier=[["p_", "_wgs"]])[0]
        out = read(os.path.join(folder, "p_merc_wgs.las"))
        self.assertEqual(_las.srid_of(out), 4326)
        np.testing.assert_allclose(np.asarray(out.x), [0.0, 1.0], atol=1e-6)
        np.testing.assert_allclose(np.asarray(out.y), [0.0, 0.0], atol=1e-6)
        np.testing.assert_allclose(np.asarray(out.z), [5.0, 7.0])
        self.assertAlmostEqual(float(out.header.scales[0]), 1e-7)
        with self.assertRaisesRegex(NotBuiltError, "Esri's transformation names"):
            arcpy.management.ProjectLAS(source, self.out("geo2"), 4326, "WGS_1984_(ITRF00)_To_NAD_1983")


class Colour(Base):
    def test_colorizelas_takes_each_channel_from_the_cell_a_point_falls_in(self):
        import rasterio
        from rasterio.transform import from_origin
        image = self.out("image.tif")
        bands = np.array([[[10, 20], [30, 40]], [[11, 21], [31, 41]], [[12, 22], [32, 42]]], np.uint8)
        with rasterio.open(image, "w", driver="GTiff", height=2, width=2, count=3, dtype="uint8", crs="EPSG:3857",
                           transform=from_origin(0, 20, 10, 10)) as out:
            out.write(bands)
        folder = arcpy.ddd.ColorizeLas(self.lasd, image, "RED Band_1; GREEN Band_2; BLUE Band_3", self.out("rgb"),
                                       "_rgb")[0]
        out = read(os.path.join(folder, "tile_rgb.las"))
        self.assertEqual((out.header.version.minor, out.header.point_format.id), (4, 7))
        x, y = np.asarray(out.x), np.asarray(out.y)
        lower_left = (x < 10) & (y < 10)
        upper_right = (x > 10) & (y > 10)
        self.assertTrue((np.asarray(out.red)[lower_left] == 30).all())
        self.assertTrue((np.asarray(out.green)[lower_left] == 31).all())
        self.assertTrue((np.asarray(out.blue)[upper_right] == 22).all())
        with self.assertRaisesRegex(ExecuteError, "format 8"):
            arcpy.ddd.ColorizeLas(self.lasd, image, "NIR Band_1", self.out("nir"))


class Thinning(Base):
    def test_thinlas_keeps_the_highest_point_of_each_2_m_square(self):
        folder = arcpy.ddd.ThinLas(self.lasd, self.out("thin"), "2D", "2 Meters", None, "Z_MAX")[0]
        out = read(os.path.join(folder, "tile.las"))
        z = np.asarray(out.z)
        # 100 squares; the four over the roof keep a first return at 6.5, the others a ground point.
        self.assertEqual(len(z), 100)
        self.assertEqual(int((z == 6.5).sum()), 4)
        self.assertEqual(int((z == 0).sum()), 96)

    def test_thinlas_preserves_and_excludes_by_class_code(self):
        folder = arcpy.ddd.ThinLas(self.lasd, self.out("thin"), "2D", 2, None, "Z_MIN", preserved_class_codes=[1])[0]
        out = read(os.path.join(folder, "tile.las"))
        self.assertEqual(len(out.points), 96 + ROOF)
        folder = arcpy.ddd.ThinLas(self.lasd, self.out("thin2"), "2D", 2, None, "CLOSEST_TO_CENTER",
                                   excluded_class_codes=[1], name_suffix="_x")[0]
        out = read(os.path.join(folder, "tile_x.las"))
        self.assertEqual(len(out.points), 96)

    def test_thinlas_3d_keeps_one_point_per_height_band(self):
        folder = arcpy.ddd.ThinLas(self.lasd, self.out("thin3"), "3D", 2, 0.25, "Z_AVERAGE")[0]
        out = read(os.path.join(folder, "tile.las"))
        # z bands of 0.25 m from z 0: 6 is band 24 and 6.5 band 26, so each roof square keeps two.
        self.assertEqual(len(out.points), 96 + 8)


class Tiling(Base):
    def test_tilelas_cuts_the_dataset_into_named_tiles(self):
        folder = arcpy.ddd.TileLas(self.lasd, self.out("tiles"), "t", tile_width=10, tile_height=10,
                                   tile_origin="0 0")[0]
        names = sorted(os.listdir(folder))
        self.assertEqual(names, ["t_0_0.las", "t_0_10.las", "t_10_0.las", "t_10_10.las"])
        counts = {n: len(read(os.path.join(folder, n)).points) for n in names}
        # 100 cells each, and each tile holds 4 roof cells with a second return; the withheld point is kept.
        self.assertEqual(counts, {n: 104 for n in names})
        first = read(os.path.join(folder, "t_0_0.las"))
        self.assertEqual((first.header.version.minor, first.header.point_format.id), (3, 1))
        self.assertEqual(_las.srid_of(first), 3857)

    def test_tilelas_names_tiles_by_row_and_column_and_by_ordinal(self):
        folder = arcpy.ddd.TileLas(self.lasd, self.out("rc"), "t", naming_method="ROW_COLUMN", tile_width=10,
                                   tile_origin="0 0")[0]
        self.assertEqual(sorted(os.listdir(folder)), ["t_1_1.las", "t_1_2.las", "t_2_1.las", "t_2_2.las"])
        folder = arcpy.ddd.TileLas(self.lasd, self.out("ord"), "t", naming_method="ORDINAL", tile_width=10,
                                   tile_origin="0 0")[0]
        self.assertEqual(sorted(os.listdir(folder)), ["t_1.las", "t_2.las", "t_3.las", "t_4.las"])
        self.assertEqual(len(read(os.path.join(folder, "t_2.las")).points), 104)


class Overlap(Base):
    def test_classifylasoverlap_marks_the_line_further_from_nadir(self):
        # Line 1 at x 0.5 … 9.5 scanned at 2°, line 2 at x 5.5 … 13.5 at −1° then 15°: in 2 m squares from x 0.5, the
        # squares over 4.5–10.5 hold both. Line 2's −1° point at 5.5 is nearer nadir there, so line 1's 4.5 and 5.5
        # are overlap; in the squares over 6.5–10.5 line 1 wins and line 2's 6.5, 7.5, 8.5, 9.5 are overlap.
        ax = np.arange(0.5, 10, 1.0)
        bx = np.arange(5.5, 14, 1.0)
        x = np.r_[ax, bx]
        source = np.r_[np.full(len(ax), 1), np.full(len(bx), 2)]
        angle = np.r_[np.full(len(ax), 2), [-1], np.full(len(bx) - 1, 15)]
        folder = os.path.join(self.directory, "lines")
        os.makedirs(folder)
        las_file(os.path.join(folder, "lines.las"), x, np.full(len(x), 0.5), np.zeros(len(x)), np.full(len(x), 1),
                 point_source_id=source.astype(np.uint16), scan_angle_rank=angle.astype(np.int8))
        lasd = arcpy.management.CreateLasDataset(folder, self.out("lines.lasd"))[0]
        arcpy.ddd.ClassifyLasOverlap(lasd, "2 Meters")
        out = read(os.path.join(folder, "lines.las"))
        overlap = np.asarray(out.classification) == 12
        self.assertEqual(sorted(zip(np.asarray(out.point_source_id)[overlap].tolist(),
                                    np.asarray(out.x)[overlap].tolist())),
                         [(1, 4.5), (1, 5.5), (2, 6.5), (2, 7.5), (2, 8.5), (2, 9.5)])

    def test_classifylasoverlap_sets_the_flag_in_format_6(self):
        folder = os.path.join(self.directory, "six")
        os.makedirs(folder)
        las_file(os.path.join(folder, "six.las"), [0.5, 0.6], [0.5, 0.5], [0, 0], [2, 2], point_format=6, version="1.4",
                 point_source_id=np.array([1, 2], np.uint16), scan_angle=np.array([0, 1000], np.int16))
        lasd = arcpy.management.CreateLasDataset(folder, self.out("six.lasd"))[0]
        arcpy.ddd.ClassifyLasOverlap(lasd, 2)
        out = read(os.path.join(folder, "six.las"))
        self.assertEqual(np.asarray(out.overlap).tolist(), [0, 1])
        self.assertEqual(np.asarray(out.classification).tolist(), [2, 2])


class Building(Base):
    def test_classifylasbuilding_finds_the_flat_roof_and_not_the_tree_or_the_shed(self):
        rng = np.random.default_rng(7)
        xs, ys, zs, cls = [], [], [], []
        for i in range(40):
            for j in range(40):
                x, y = i + 0.5, j + 0.5
                if 10 < x < 20 and 10 < y < 20:          # a 10 m × 10 m flat roof at 5 m
                    xs.append(x), ys.append(y), zs.append(5.0), cls.append(1)
                elif 30 < x < 32 and 30 < y < 32:        # a 2 m × 2 m shed roof at 3 m: too small
                    xs.append(x), ys.append(y), zs.append(3.0), cls.append(1)
                else:
                    xs.append(x), ys.append(y), zs.append(0.0), cls.append(2)
        tree = rng.uniform([3, 30, 2], [7, 34, 8], (40, 3))  # a tree: points scattered from 2 m to 8 m
        xs += tree[:, 0].tolist()
        ys += tree[:, 1].tolist()
        zs += tree[:, 2].tolist()
        cls += [1] * 40
        folder = os.path.join(self.directory, "town")
        os.makedirs(folder)
        path = las_file(os.path.join(folder, "town.las"), xs, ys, zs, cls)
        lasd = arcpy.management.CreateLasDataset(folder, self.out("town.lasd"))[0]
        arcpy.ddd.ClassifyLasBuilding(lasd, "2 Meters", "20 SquareMeters")
        out = np.asarray(read(path).classification)
        self.assertEqual(int((out == 6).sum()), 100)
        self.assertTrue((out[np.asarray(zs) == 5.0] == 6).all())
        self.assertEqual(int((out[-40:] == 6).sum()), 0)
        self.assertTrue((out[np.asarray(zs) == 3.0] == 1).all())
        self.assertIn("ClassifyLasBuilding", dir(arcpy.ddd))
        self.assertTrue(getattr(arcpy.ddd.ClassifyLasBuilding, "__grapy_equivalent__", ""))


class Proximity(Base):
    def test_locatelaspointsbyproximity_counts_and_reclassifies_within_3d_distance(self):
        lines = arcpy.management.CreateFeatureclass(self.gpkg, "wire", "POLYLINE", has_z="ENABLED",
                                                    spatial_reference=3857)[0]
        points = arcpy.management.CreateFeatureclass(self.gpkg, "pole", "POINT", has_z="ENABLED",
                                                     spatial_reference=3857)[0]
        with da.InsertCursor(lines, ["SHAPE@"]) as cursor:
            cursor.insertRow([arcpy.Polyline(arcpy.Array([arcpy.Point(0, 10, 0), arcpy.Point(20, 10, 0)]),
                                             arcpy.SpatialReference(3857), True)])
        with da.InsertCursor(points, ["SHAPE@"]) as cursor:
            cursor.insertRow([arcpy.PointGeometry(arcpy.Point(15.5, 15.5, 0), arcpy.SpatialReference(3857), True)])
        # The line at z 0 along y = 10: the ground rows y 9.5 and 10.5 are 0.5 m off, but not under the roof (x 8.5 …
        # 11.5), whose points are 6 m up: 2 × (20 − 4) = 32.
        arcpy.ddd.LocateLasPointsByProximity(self.lasd, lines, "0.6 Meters", "NEAR", self.out("found.gpkg"), "POINT",
                                             17)
        self.assertEqual(rows(lines, ["NEAR"]), [(32,)])
        found = rows(self.out("found.gpkg"), ["DIST3D"])
        self.assertEqual(len(found), 32)
        self.assertTrue(all(abs(d - 0.5) < 1e-9 for (d,) in found))
        self.assertEqual(int((np.asarray(read(self.las).classification) == 17).sum()), 32)
        # The point at (15.5, 15.5, 0) and its four neighbours 1 m away.
        arcpy.ddd.LocateLasPointsByProximity(self.lasd, points, 1.01)
        self.assertEqual(rows(points, ["COUNT"]), [(5,)])

    def test_polygons_are_refused_for_their_own_reason(self):
        polygons = arcpy.management.CreateFeatureclass(self.gpkg, "area", "POLYGON", has_z="ENABLED",
                                                       spatial_reference=3857)[0]
        with self.assertRaisesRegex(NotBuiltError, "outline or its surface"):
            arcpy.ddd.LocateLasPointsByProximity(self.lasd, polygons, 1)


class Outliers(Base):
    def test_locateoutliers_finds_a_spike_and_a_point_below_the_hard_limit(self):
        # A 5 × 5 grid at z 0 with a spike of 10 at (2, 2) and the corner (4, 4) at −1.5: the spike's every neighbour
        # differs by 10 over at most √2 (707 %), so it fails the comparison filter, and z max 5 too (REASON 1); the
        # corner differs by 1.5 over at least 1 m — 150 %, not more than the tolerance — so only the hard limit
        # −1 catches it (REASON 0); no neighbour of either has half its neighbours exceeding.
        x, y = np.meshgrid(np.arange(5.0), np.arange(5.0))
        z = np.zeros((5, 5))
        z[2, 2], z[4, 4] = 10.0, -1.5
        folder = os.path.join(self.directory, "spiky")
        os.makedirs(folder)
        las_file(os.path.join(folder, "spiky.las"), x.ravel(), y.ravel(), z.ravel())
        lasd = arcpy.management.CreateLasDataset(folder, self.out("spiky.lasd"))[0]
        out = arcpy.ddd.LocateOutliers(lasd, self.out("outliers.gpkg"), "APPLY_HARD_LIMIT", -1, 5,
                                       "APPLY_COMPARISON_FILTER", 1, 150, 0.5)[0]
        found = sorted((r, s.firstPoint.X, s.firstPoint.Y, s.firstPoint.Z) for r, s in rows(out, ["REASON", "SHAPE@"]))
        self.assertEqual(found, [(0, 4.0, 4.0, -1.5), (1, 2.0, 2.0, 10.0)])
        capped = arcpy.ddd.LocateOutliers(lasd, self.out("capped.gpkg"), "APPLY_HARD_LIMIT", -1, 5,
                                          outlier_cap=1)[0]
        self.assertEqual(len(rows(capped, ["REASON"])), 1)


class Interpolation(Base):
    def test_interpolatefrompointcloud_triangulates_a_dsm_and_a_dtm(self):
        out = arcpy.management.InterpolateFromPointCloud(self.lasd, self.out("dsm.tif"), 1)[0]
        raster = arcpy.Raster(out)
        # Cells start at the lowest point (0.5, 0.5): the centre (10, 10) lies among roof points whose last-written
        # height is 6, and (2, 2) on the ground.
        self.assertEqual((raster.width, raster.height), (19, 19))
        array = arcpy.RasterToNumPyArray(raster)
        self.assertAlmostEqual(float(array[18 - 9, 9]), 6.0)
        self.assertAlmostEqual(float(array[18 - 1, 1]), 0.0)
        with self.assertRaisesRegex(NotBuiltError, "ReuseGround 1"):
            arcpy.management.InterpolateFromPointCloud(self.lasd, self.out("dtm.tif"), 1, surface_type="DTM")
        out = arcpy.management.InterpolateFromPointCloud(self.lasd, self.out("dtm.tif"), 1, surface_type="DTM",
                                                         options=[["ReuseGround", "1"]])[0]
        self.assertAlmostEqual(float(np.nanmax(arcpy.RasterToNumPyArray(arcpy.Raster(out)))), 0.0)
        with self.assertRaisesRegex(NotBuiltError, "power"):
            arcpy.management.InterpolateFromPointCloud(self.lasd, self.out("idw.tif"), 1, "IDW")


class Objects(Base):
    def test_extractobjectsfrompointcloud_clusters_the_roof_into_one_object(self):
        hull = arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, [(1, None)], 1.1, self.out("roof.gpkg"),
                                                      "CONVEX_HULL_2D")[0]
        (code, count, low, high, shape), = rows(hull, ["ObjectCode", "COUNT", "MIN_Z", "MAX_Z", "SHAPE@"])
        self.assertEqual((code, count, low, high), (1, ROOF, 6.0, 6.5))
        self.assertAlmostEqual(shape.area, 9.0)
        concave = arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, "1 7", "1.1 Meters", self.out("concave.gpkg"),
                                                         "CONCAVE_HULL_2D", 10)[0]
        (code, alpha, shape), = rows(concave, ["ObjectCode", "ALPHA", "SHAPE@"])
        self.assertEqual(code, 7)
        self.assertAlmostEqual(alpha, 1.1)
        self.assertAlmostEqual(shape.area, 9.0)
        centre = arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, [(1, None)], 1.1, self.out("centre.gpkg"),
                                                        "CENTERPOINT")[0]
        (point,), = rows(centre, ["SHAPE@"])
        self.assertEqual((point.firstPoint.X, point.firstPoint.Y, point.firstPoint.Z), (10.0, 10.0, 6.25))
        none = arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, [(1, None)], 1.1, self.out("none.gpkg"),
                                                      "CONVEX_HULL_2D", 40)[0]
        self.assertEqual(rows(none, ["COUNT"]), [])
        with self.assertRaisesRegex(NotBuiltError, "O'Rourke"):
            arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, [(1, None)], 1.1, self.out("mp.gpkg"), "BOUNDING_BOX_3D")


class Refused(unittest.TestCase):
    def test_the_rest_of_the_batch_is_refused_for_its_own_reason(self):
        for name, said in (("ExtractPowerLinesFromPointCloud", "catenary"), ("ExtractRailsFromPointCloud", "deep "),
                           ("ExtractMeshFeaturesUsingPointCloud", "integrated mesh")):
            with self.assertRaisesRegex(NotBuiltError, said):
                getattr(arcpy.ddd, name)()

    def test_no_name_is_refused_as_a_batch(self):
        for module in (arcpy.ddd, arcpy.management, arcpy.conversion):
            self.assertFalse([k for k, v in module._REFUSED.items() if "batch" in str(v)])

    # A schedule is not a reason (ADR-224): "not held yet", "until defined", "today", "later". The words are looked for
    # in what every refused name of every module of arcpy-surface.json answers, as tools/grapy_refusals.py asks it —
    # the dictionaries above missed "with today" in arcpy.da.
    SCHEDULE = re.compile(r"\b(yet|today|until|later|for now|not chosen|currently|at present|eventually|so far|"
                          r"at the moment|in a later|batch)\b", re.IGNORECASE)

    def test_no_refusal_anywhere_reads_as_a_schedule(self):
        tools = os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools")
        sys.path.insert(0, os.path.abspath(tools))
        try:
            import grapy_refusals
        finally:
            sys.path.pop(0)
        scheduled = [f"{module}.{name}: {reason}" for module, row in grapy_refusals.survey().items()
                     for name, reason in row["refused"].items() if self.SCHEDULE.search(reason)]
        self.assertEqual(scheduled, [])

    def test_the_schedule_words_are_caught(self):
        for said in ("pyarrow is not among the wheels a user tool runs with today",
                     "needs a data model this server does not hold yet (ADR-224 group 2)",
                     "refused until its rules are defined", "left for later"):
            self.assertTrue(self.SCHEDULE.search(said), said)


if __name__ == "__main__":
    unittest.main()
