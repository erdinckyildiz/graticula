"""D-329's sweep: names once refused for a model grapy did not hold, built on ADR-236's multidimensional rasters,
ADR-238's multipatch features and ADR-240's COGO lines — each against an answer worked out by hand.

Boxes keep the multipatch answers checkable: a 4 × 2 × 3 box has volume 24, its top at 3 and its bottom at 0; a fan
from the origin to (10, ±10, 10) covers a plan triangle of area 100 whose surface rises linearly, so the solid under it
holds 100 × (0 + 10 + 10) / 3.
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
from grapy import _mdim, da, ia, sa  # noqa: E402
from grapy import _multipatch as M  # noqa: E402
from grapy._raster import _Grid  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402


def rows(path, fields):
    with da.SearchCursor(path, fields) as cursor:
        return [tuple(r) for r in cursor]


def volume(shape):
    return M.signed_volume(M.triangles(shape))


class Base(unittest.TestCase):
    srid = 3857

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]

    def out(self, name):
        return os.path.join(self.gpkg, name)

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


def box(lo, hi):
    return M.from_triangles(M.box(lo, hi))


def cube(variables, rows_=1, columns=2):
    grid = _Grid(0, 10 * rows_, 10, 10, rows_, columns, 3857)
    return _mdim.raster_of(_mdim.Cube(grid, variables))


def variable(name, times, data, dims=("StdTime",), bands=1, attrs=None):
    data = np.asarray(data, float)
    coords = [{dims[0]: t} for t in times]
    data = data.reshape(len(times), bands, 1, -1)
    return _mdim.Variable(name, list(dims), coords, data, np.zeros(data.shape, bool), attrs or {})


def days(*texts):
    return [_mdim.to_time(t) for t in texts]


# ---- ADR-236: Subset, Foreach, SummarizeCategoricalRaster, ExtractSubDataset, NetCDFFileProperties


class Multidimensional(Base):
    def test_subset_keeps_the_variables_and_slices_its_definitions_name(self):
        t = days("1980-01-01T12:00:00", "1980-06-01T12:00:00", "1981-01-01T12:00:00", "1982-01-01T12:00:00")
        r = cube([variable("temp", t, [[1, 1], [2, 2], [3, 3], [4, 4]]),
                  variable("rain", t, [[5, 5], [6, 6], [7, 7], [8, 8]])])
        self.assertEqual(ia.Subset(r, variables="rain").variableNames, ["rain"])
        # The 1st of every January: start 1980-01-01T12, no end, every year → 1980, 1981, 1982.
        yearly = ia.Subset(r, "temp", {"time": ("1980-01-01T12:00:00", None, 1, "year")})
        self.assertEqual([s["StdTime"][:10] for s in yearly.slices], ["1980-01-01", "1981-01-01", "1982-01-01"])
        # A range, both ends kept.
        first_two = sa.Subset(r, ["temp", "rain"], {"StdTime": ("1980-01-01", "1980-12-31")})
        self.assertEqual(len(first_two.slices), 4)
        # A list of single values.
        picked = ia.Subset(r, "temp", {"time": ["1981-01-01T12:00:00", "1982-01-01T12:00:00"]})
        self.assertEqual(picked.read(nodata_to_value=-1)[..., 0].tolist(), [[[3.0, 3.0]], [[4.0, 4.0]]])
        with self.assertRaises(ExecuteError):
            ia.Subset(r, "temp", {"time": "1990-01-01"})

    def test_subset_leaves_a_variable_without_the_dimension_whole(self):
        depth = _mdim.Variable("wt", ["StdZ"], [{"StdZ": 0.0}, {"StdZ": 10.0}], np.arange(4.0).reshape(2, 1, 1, 2),
                               np.zeros((2, 1, 1, 2), bool))
        t = days("2000-01-01", "2000-02-01", "2000-03-01")
        r = cube([depth, variable("rain", t, [[1, 1], [2, 2], [3, 3]])])
        out = ia.Subset(r, dimension_definitions={"depth": 0})
        self.assertEqual(out.variables, ["wt(StdZ=1)", "rain(StdTime=3)"])

    def test_foreach_applies_the_function_to_every_slice(self):
        t = days("2000-01-01", "2000-02-01")
        # Two bands per slice: red then near infrared. NDVI = (NIR − Red) / (NIR + Red).
        v = variable("img", t, [[[1, 2]], [[3, 6]], [[1, 1]], [[1, 3]]], bands=2)
        r = cube([v])
        out = ia.Foreach(r, "NDVI", {"VisibleBandID": 1, "InfraredBandID": 2})
        got = out.read(nodata_to_value=-9)[..., 0].tolist()
        self.assertAlmostEqual(got[0][0][0], (3 - 1) / (3 + 1))
        self.assertAlmostEqual(got[0][0][1], (6 - 2) / (6 + 2))
        self.assertAlmostEqual(got[1][0][0], 0.0)
        self.assertAlmostEqual(got[1][0][1], (3 - 1) / (3 + 1))
        self.assertEqual(out.variables, ["img(StdTime=2)"])
        negated = sa.Foreach(cube([variable("v", t, [[1, -2], [3, -4]])]), "Abs")
        self.assertEqual(negated.read(nodata_to_value=-9)[..., 0].tolist(), [[[1.0, 2.0]], [[3.0, 4.0]]])
        with self.assertRaisesRegex(NotBuiltError, "template"):
            ia.Foreach(r, "C:/x/ndvi.rft.xml")
        with self.assertRaises((ValueError, TypeError)):
            ia.Foreach(r, "NDVI", {"NoSuchArgument": 1})
        with self.assertRaises(ValueError):
            ia.Foreach(r, "Abs", {"NoSuchArgument": 1})

    def test_summarize_categorical_raster_counts_each_class_in_each_slice(self):
        t = days("2000-01-01", "2001-01-01")
        r = cube([variable("lc", t, [[1, 1, 2, 3], [2, 2, 2, 3]], attrs={})], rows_=1, columns=4)
        table = ia.SummarizeCategoricalRaster(r, os.path.join(self.directory, "lc.csv"))[0]
        with open(table, encoding="utf-8") as f:
            lines = [line.strip().split(",") for line in f]
        self.assertEqual(lines[0], ["StdTime", "Class_1", "Class_2", "Class_3"])
        self.assertEqual(lines[1][1:], ["2", "1", "1"])
        self.assertEqual(lines[2][1:], ["0", "3", "1"])
        # Class names from CF flag_values and flag_meanings; one area of interest over the first two cells.
        named = cube([variable("lc", t, [[1, 1, 2, 3], [2, 2, 2, 3]],
                               attrs={"flag_values": "1 2 3", "flag_meanings": "water forest urban"})],
                     rows_=1, columns=4)
        aoi = self.features("aoi", "POLYGON", [("west", shapely.box(0, 0, 20, 10))], fields=[("Zone", "TEXT")],
                            has_z=False)
        out = ia.SummarizeCategoricalRaster(named, self.out("summary"), "StdTime", aoi, "Zone")[0]
        got = rows(out, ["Zone", "water", "forest", "urban"])
        self.assertEqual(got, [("west", 2, 0, 0), ("west", 0, 2, 0)])
        with self.assertRaises(ExecuteError):
            ia.SummarizeCategoricalRaster(cube([variable("f", t[:1], [[0.5, 1, 2, 3]])], 1, 4),
                                          os.path.join(self.directory, "f.csv"))

    def test_extract_subdataset_takes_the_images_by_their_index_from_one(self):
        import rasterio
        from rasterio.transform import from_origin
        path = os.path.join(self.directory, "two.ntf")
        transform = from_origin(500000, 4000010, 10, 10)
        with rasterio.open(path, "w", driver="NITF", width=3, height=2, count=1, dtype="uint8", crs="EPSG:32633",
                           transform=transform, NUMI=2, ICORDS="N") as d:
            d.write(np.arange(6, dtype="uint8").reshape(1, 2, 3))
        with rasterio.open(path, "w", driver="NITF", width=3, height=2, count=2, dtype="uint8", crs="EPSG:32633",
                           transform=transform, APPEND_SUBDATASET="YES", ICORDS="N") as d:
            d.write(np.full((2, 2, 3), 7, dtype="uint8"))
        first = arcpy.management.ExtractSubDataset(path, os.path.join(self.directory, "first.tif"))[0]
        self.assertEqual(arcpy.RasterToNumPyArray(first).tolist(), [[0, 1, 2], [3, 4, 5]])
        both = arcpy.management.ExtractSubDataset(path, os.path.join(self.directory, "both.tif"), "1;2")[0]
        self.assertEqual(arcpy.Raster(both).bandCount, 3)
        with self.assertRaises(ExecuteError):
            arcpy.management.ExtractSubDataset(path, os.path.join(self.directory, "x.tif"), "3")

    def test_netcdf_file_properties_answers_the_typed_variables_and_dimensions(self):
        t = days("2000-01-01", "2000-01-03")
        path = cube([variable("temp", t, [[1, 2], [3, 4]], attrs={"units": "K"})]).save(
            os.path.join(self.directory, "t.nc"))
        p = arcpy.NetCDFFileProperties(path)
        self.assertEqual(sorted(p.getVariables()), ["crs", "temp"])
        self.assertEqual(p.getDimensions(), ["x", "y", "StdTime"])
        self.assertEqual(p.getDimensionsByVariable("temp"), ["StdTime", "y", "x"])
        self.assertEqual(p.getVariablesByDimension("StdTime"), ["temp"])
        self.assertEqual(p.getDimensionSize("StdTime"), 2)
        self.assertEqual(p.getDimensionValue("StdTime", 1), 10959.0)  # 2000-01-03, in days since 1970-01-01
        self.assertEqual(p.getDimensionIndex("StdTime", 10959), 1)
        self.assertEqual(p.getFieldType("temp"), "Double")
        self.assertEqual(p.getFieldType("crs"), "Integer")
        self.assertEqual(p.getFieldType("StdTime"), "Double")
        self.assertEqual(p.getAttributeValue("temp", "units"), "K")
        self.assertIn("Conventions", p.getAttributeNames())
        self.assertEqual(p.getSpatialReference("temp", "x", "y").factoryCode, 3857)
        self.assertEqual(arcpy.CreateObject("NetCDFFileProperties", path).getDimensionSize("x"), 2)

    def test_complex_answers_the_magnitude_of_complex_cells(self):
        import rasterio
        from rasterio.transform import from_origin
        path = os.path.join(self.directory, "slc.tif")
        with rasterio.open(path, "w", driver="GTiff", width=2, height=1, count=1, dtype="complex64", crs="EPSG:32633",
                           transform=from_origin(0, 10, 10, 10)) as d:
            d.write(np.array([[[3 + 4j, -1j]]], np.complex64))
        self.assertEqual(arcpy.RasterToNumPyArray(ia.Complex(path)).tolist(), [[5.0, 1.0]])
        self.assertEqual(arcpy.RasterToNumPyArray(sa.Complex(path)).tolist(), [[5.0, 1.0]])

    def test_copy_raster_copies_every_slice_with_all_slices(self):
        t = days("2000-01-01", "2000-02-01")
        source = cube([variable("v", t, [[1, 9], [3, 4]])]).save(os.path.join(self.directory, "src.nc"))
        out = arcpy.management.CopyRaster(source, "copy.nc", nodata_value=9, process_as_multidimensional="ALL_SLICES")[0]
        copied = arcpy.Raster(out, True)
        self.assertEqual(copied.read(nodata_to_value=-1)[..., 0].tolist(), [[[1.0, -1.0]], [[3.0, 4.0]]])
        with self.assertRaisesRegex(NotBuiltError, "transpose"):
            arcpy.management.CopyRaster(source, "t.nc", process_as_multidimensional="ALL_SLICES",
                                        build_multidimensional_transpose="TRANSPOSE")

    def test_voxels_are_written_by_the_one_netcdf_writer_and_read_as_stdz(self):
        path = self.features("p", "POINT", [(0.0, shapely.Point(0, 0, 0)), (10.0, shapely.Point(10, 0, 0)),
                                            (20.0, shapely.Point(0, 10, 10)), (30.0, shapely.Point(10, 10, 10))],
                             fields=[("V", "DOUBLE")])
        out = os.path.join(self.directory, "idw.nc")
        nbr = arcpy.ga.SearchNeighborhoodStandard3D(100, 4, 1, "ONE_SECTOR")
        arcpy.ga.IDW3D(path, "V", out, 2, 1, None, 5, 5, 5, search_neighborhood=nbr)
        r = arcpy.Raster(out, True)
        self.assertEqual(r.variables, ["V(StdZ=3)"])
        self.assertEqual(r.getDimensionValues("V", "StdZ"), [0.0, 5.0, 10.0])


# ---- ADR-238: multipatch features in conversion, management and ddd


class Multipatch(Base):
    def test_multipatch_to_raster_takes_the_highest_or_lowest_face_at_each_cell_centre(self):
        mp = self.multipatch("b", [box((0, 0, 1), (4, 2, 3)), box((6, 0, 0), (8, 2, 5))])
        high = arcpy.conversion.MultipatchToRaster(mp, os.path.join(self.directory, "high.tif"), 1)[0]
        cells = arcpy.RasterToNumPyArray(high, nodata_to_value=-1).tolist()
        self.assertEqual(cells, [[3, 3, 3, 3, -1, -1, 5, 5], [3, 3, 3, 3, -1, -1, 5, 5]])
        low = arcpy.conversion.MultipatchToRaster(mp, os.path.join(self.directory, "low.tif"), 1, "MINIMUM_HEIGHT")[0]
        self.assertEqual(arcpy.RasterToNumPyArray(low, nodata_to_value=-1).tolist()[0], [1, 1, 1, 1, -1, -1, 0, 0])
        # The default cell size: the shorter side of the 8 × 2 extent over 250.
        default = arcpy.conversion.MultipatchToRaster(mp, os.path.join(self.directory, "d.tif"))[0]
        self.assertAlmostEqual(arcpy.Raster(default).meanCellWidth, 2 / 250)

    def test_multipatch_to_collada_writes_a_file_per_feature_that_reads_back(self):
        mp = self.multipatch("b", [("one", box((0, 0, 0), (4, 2, 3))), ("two", box((10, 0, 0), (11, 1, 1)))],
                             fields=[("Label", "TEXT")])
        folder = os.path.join(self.directory, "dae")
        arcpy.conversion.MultipatchToCollada(mp, folder, "PREPEND_SOURCE_NAME", "Label")
        self.assertEqual(sorted(os.listdir(folder)), ["b_one.dae", "b_one.kml", "b_two.dae", "b_two.kml"])
        back = arcpy.ddd.Import3DFiles(os.path.join(folder, "b_one.dae"), self.out("back"))[0]
        (shape,), = rows(back, ["SHAPE@"])
        self.assertAlmostEqual(volume(shape._shape), 24.0)
        self.assertEqual(shapely.bounds(shape._shape).tolist(), [0.0, 0.0, 4.0, 2.0])
        arcpy.conversion.MultipatchToCollada(mp, os.path.join(self.directory, "v14"), collada_version="1.4")
        with open(os.path.join(self.directory, "v14", "1.dae"), encoding="utf-8") as f:
            self.assertIn('version="1.4.1"', f.read())

    def test_skyline_barrier_is_a_fan_from_the_observer_and_closes_down_to_its_base(self):
        observer = self.features("o", "POINT", [shapely.Point(0, 0, 0)])
        skyline = self.features("s", "POLYLINE", [shapely.LineString([(10, -10, 10), (10, 10, 10)])])
        fan = arcpy.ddd.SkylineBarrier(observer, skyline, self.out("fan"))[0]
        (oid, shape), = rows(fan, ["OBSV_PT_ID", "SHAPE@"])
        self.assertEqual(oid, 1)
        tris = M.triangles(shape._shape)
        self.assertEqual(len(tris), 1)
        self.assertEqual(sorted(map(tuple, tris[0].tolist())), [(0, 0, 0), (10, -10, 10), (10, 10, 10)])
        closed = arcpy.ddd.SkylineBarrier(observer, skyline, self.out("closed"), closed="CLOSED", base_elevation=0)[0]
        (shape,), = rows(closed, ["SHAPE@"])
        self.assertTrue(M.is_closed(shape._shape))
        self.assertAlmostEqual(volume(shape._shape), 100 * 20 / 3)
        # A maximum radius shortens every edge to 5 along its ray.
        short = arcpy.ddd.SkylineBarrier(observer, skyline, self.out("short"), max_radius_value_or_field=5)[0]
        far = np.linalg.norm(M.triangles(rows(short, ["SHAPE@"])[0][0]._shape).reshape(-1, 3), axis=1).max()
        self.assertAlmostEqual(far, 5.0)
        # Projected to the plane x = 20: the vertices double.
        plane = arcpy.ddd.SkylineBarrier(observer, skyline, self.out("plane"), max_radius_value_or_field=20,
                                         project_to_plane="PROJECT_TO_PLANE")[0]
        pts = M.triangles(rows(plane, ["SHAPE@"])[0][0]._shape).reshape(-1, 3)
        self.assertAlmostEqual(pts[:, 0].max(), 20.0)
        self.assertAlmostEqual(pts[:, 1].max(), 20.0)
        with self.assertRaisesRegex(NotBuiltError, "silhouette"):
            arcpy.ddd.SkylineBarrier(observer, self.multipatch("sil", [box((0, 0, 0), (1, 1, 1))]), self.out("x"))

    def test_export_and_import_3d_objects_round_trip_through_every_built_format(self):
        mp = self.multipatch("b", [box((100, 200, 5), (104, 202, 8))])
        folder = os.path.join(self.directory, "models")
        os.makedirs(folder)
        arcpy.management.Export3DObjects(mp, folder, ["FMT3D_DAE", "FMT3D_OBJ", "FMT3D_GLTF", "FMT3D_GLB"])
        self.assertEqual(sorted(os.listdir(os.path.join(folder, "1"))), ["1.dae", "1.glb", "1.gltf", "1.obj"])
        for ext in ("dae", "obj", "gltf", "glb"):
            target = self.out("objects_" + ext)
            arcpy.management.Import3DObjects(os.path.join(folder, "1", "1." + ext), target)
            (name, shape), = rows(target, ["Name", "SHAPE@"])
            self.assertEqual(name, "1")
            self.assertAlmostEqual(volume(shape._shape), 24.0, places=3)
            np.testing.assert_allclose(shapely.bounds(shape._shape), [100, 200, 104, 202], atol=1e-3)
            z = shapely.get_coordinates(shape._shape, include_z=True)[:, 2]
            self.assertAlmostEqual(z.min(), 5.0, places=3)
        with self.assertRaisesRegex(NotBuiltError, "Autodesk"):
            arcpy.management.Export3DObjects(mp, folder, "FMT3D_FBX")

    def test_import_3d_objects_updates_by_file_name_and_places_the_model(self):
        mp = self.multipatch("b", [box((0, 0, 0), (4, 2, 1))])
        folder = os.path.join(self.directory, "models")
        os.makedirs(folder)
        arcpy.management.Export3DObjects(mp, folder, "FMT3D_OBJ")
        target = self.out("objects")
        arcpy.management.Import3DObjects(folder, target)
        arcpy.management.Import3DObjects(folder, target, "UPDATE_EXISTING", rotate=90, translate="10 0",
                                         elevation=5)
        (shape,), = rows(target, ["SHAPE@"])
        # Turned 90° clockwise: x 0..4, y 0..2 becomes x 0..2, y −4..0; then moved 10 east and 5 up.
        np.testing.assert_allclose(shapely.bounds(shape._shape), [10, -4, 12, 0], atol=1e-9)
        self.assertAlmostEqual(shapely.get_coordinates(shape._shape, include_z=True)[:, 2].min(), 5.0)
        arcpy.management.Import3DObjects(folder, target, "ADD_ALL")
        self.assertEqual(len(rows(target, ["Name"])), 2)
        arcpy.management.Import3DObjects(folder, target, "REPLACE_ALL")
        self.assertEqual(len(rows(target, ["Name"])), 1)
        with open(os.path.join(self.directory, "a.fbx"), "wb") as f:
            f.write(b"x")
        with self.assertRaisesRegex(NotBuiltError, "Autodesk"):
            arcpy.management.Import3DObjects(os.path.join(self.directory, "a.fbx"), target)

    def test_register_with_geodatabase_and_create_table_make_multipatch_classes(self):
        path, _ = da.CreateTable(self.out("mp"), [("Name", "TEXT")], ("MULTIPATCH", arcpy.SpatialReference(3857)))
        self.assertTrue(M.is_multipatch(path))
        self.assertEqual(arcpy.Describe(path).shapeType, "MultiPatch")
        zpath, _ = da.CreateTable(self.out("zpts"), [("Name", "TEXT")], ("POINT", 3857, True, False))
        self.assertTrue(arcpy.Describe(zpath).hasZ)
        loose = arcpy.management.CreateUnRegisteredFeatureclass(self.gpkg, "loose", "POLYGON", has_z="ENABLED",
                                                                spatial_reference=3857)[0]
        arcpy.management.RegisterWithGeodatabase(loose, in_geometry_type="MULTIPATCH")
        self.assertTrue(M.is_multipatch(loose))

    def test_numpy_array_to_feature_class_writes_z_and_m(self):
        array = np.array([(1.0, 2.0, 3.0, 4.0, 7)], dtype=[("X", "f8"), ("Y", "f8"), ("Z", "f8"), ("M", "f8"),
                                                           ("K", "i4")])
        out = self.out("zm")
        da.NumPyArrayToFeatureClass(array, out, ["X", "Y", "Z", "M"], arcpy.SpatialReference(3857))
        (k, z, m), = rows(out, ["K", "SHAPE@Z", "SHAPE@M"])
        self.assertEqual((k, z, m), (7, 3.0, 4.0))


# ---- the LAS tools' multipatch parameters


class Las(unittest.TestCase):
    def setUp(self):
        from test_las import scene
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.folder = os.path.join(self.directory, "lidar")
        os.makedirs(self.folder)
        self.las = scene(os.path.join(self.folder, "tile.las"))
        self.lasd = arcpy.management.CreateLasDataset(self.folder, os.path.join(self.directory, "scene.lasd"))[0]
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]

    def test_extract_objects_writes_the_3d_shapes_as_multipatch(self):
        # The roof: 4 × 4 points 1 apart (x and y 8.5 … 11.5) at 6 and 6.5 — a 3 × 3 × 0.5 box.
        hull = arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, [(1, None)], 1.1, self.gpkg + "/hull")[0]
        (code, count, shape), = rows(hull, ["ObjectCode", "COUNT", "SHAPE@"])
        self.assertEqual((code, count), (1, 32))
        self.assertTrue(M.is_closed(shape._shape))
        self.assertAlmostEqual(volume(shape._shape), 4.5)
        concave = arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, [(1, None)], 1.1, self.gpkg + "/cc",
                                                         "CONCAVE_HULL_3D", alpha_radius=5)[0]
        (alpha, shape), = rows(concave, ["ALPHA", "SHAPE@"])
        self.assertEqual(alpha, 5.0)
        self.assertAlmostEqual(volume(shape._shape), 4.5)
        sphere = arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, [(1, None)], 1.1, self.gpkg + "/sp",
                                                        "BOUNDING_SPHERE")[0]
        (shape,), = rows(sphere, ["SHAPE@"])
        xyz = shapely.get_coordinates(shape._shape, include_z=True)
        centre = (xyz.max(axis=0) + xyz.min(axis=0)) / 2
        np.testing.assert_allclose(centre, [10, 10, 6.25], atol=1e-9)
        self.assertAlmostEqual(np.linalg.norm(xyz - centre, axis=1).max(), math.sqrt(1.5 ** 2 * 2 + 0.25 ** 2))
        with self.assertRaisesRegex(NotBuiltError, "O'Rourke"):
            arcpy.ddd.ExtractObjectsFromPointCloud(self.lasd, [(1, None)], 1.1, self.gpkg + "/bb", "BOUNDING_BOX_3D")

    def test_locate_las_points_by_proximity_measures_to_a_multipatch_nearest_face(self):
        path = arcpy.management.CreateFeatureclass(self.gpkg, "slab", "MULTIPATCH", spatial_reference=3857)[0]
        with da.InsertCursor(path, ["SHAPE@"]) as cursor:
            cursor.insertRow([box((8, 8, 7), (12, 12, 8))])
        # The upper roof returns at 6.5 are 0.5 under the slab's floor; the lower ones at 6.0 are 1.0 under.
        arcpy.ddd.LocateLasPointsByProximity(self.lasd, path, 0.6, "NEAR", self.directory + "/near.gpkg", "POINT")
        self.assertEqual(rows(path, ["NEAR"]), [(16,)])
        found = rows(self.directory + "/near.gpkg", ["DIST3D"])
        self.assertTrue(all(abs(d - 0.5) < 1e-9 for (d,) in found))

    def test_point_file_information_extrudes_each_files_z_range(self):
        out = self.gpkg + "/info"
        arcpy.ddd.PointFileInformation(self.folder, out, "LAS", "las", None, "NO_RECURSION", "EXTRUSION",
                                       "DECIMAL_POINT", "SUMMARIZE")
        self.assertTrue(M.is_multipatch(out))
        got = {c: s._shape for c, s in rows(out, ["Class", "SHAPE@"])}
        x0, y0, x1, y1 = shapely.bounds(got[1])
        self.assertAlmostEqual(volume(got[1]), (x1 - x0) * (y1 - y0) * 0.5)
        self.assertAlmostEqual(volume(got[2]), 0.0)


# ---- ADR-240: UpdateCOGO


class Cogo(Base):
    def test_update_cogo_sets_distance_and_direction_from_the_shape(self):
        lines = self.features("lot", "POLYLINE", [shapely.LineString([(0, 0), (3, 4)]),
                                                  shapely.LineString([(0, 0), (0, -2)])], has_z=False)
        arcpy.management.EnableCOGO(lines)
        arcpy.edit.UpdateCOGO(lines)
        got = rows(lines, ["Distance", "Direction", "Radius", "ArcLength"])
        self.assertAlmostEqual(got[0][0], 5.0)
        self.assertAlmostEqual(got[0][1], math.degrees(math.atan2(3, 4)))
        self.assertEqual(got[0][2:], (None, None))
        self.assertAlmostEqual(got[1][1], 180.0)
        # Ground distance is the grid length over the combined scale factor; the offset turns the direction back.
        arcpy.edit.UpdateCOGO(lines, combined_scale_factor=0.5, direction_offset=3600)
        got = rows(lines, ["Distance", "Direction"])
        self.assertAlmostEqual(got[0][0], 10.0)
        self.assertAlmostEqual(got[0][1], math.degrees(math.atan2(3, 4)) - 1)
        # UPDATE_NULL_ONLY leaves a value there; USE_MINIMUM_DIFFERENCE only replaces one beyond the tolerance.
        with da.UpdateCursor(lines, ["Distance"]) as cursor:
            for row in cursor:
                cursor.updateRow([5.2])
        arcpy.edit.UpdateCOGO(lines, "UPDATE_NULL_ONLY", direction_type="DO_NOT_UPDATE")
        self.assertEqual([r[0] for r in rows(lines, ["Distance"])], [5.2, 5.2])
        arcpy.edit.UpdateCOGO(lines, "USE_MINIMUM_DIFFERENCE", "0.5 Meters", "DO_NOT_UPDATE")
        self.assertEqual([round(r[0], 9) for r in rows(lines, ["Distance"])], [5.2, 2.0])
        arcpy.edit.UpdateCOGO(lines, "DO_NOT_UPDATE", combined_scale_factor="$feature.Distance / 5.2")
        not_cogo = self.features("plain", "POLYLINE", [shapely.LineString([(0, 0), (1, 1)])], has_z=False)
        with self.assertRaises(ExecuteError):
            arcpy.edit.UpdateCOGO(not_cogo)


# ---- refusals that now give a reason of their own


class Refusals(unittest.TestCase):
    STALE = ("multipatch features, a 3D data model grapy does not hold", "it writes multipatch features (ADR-224",
             "holds no voxels", "grapy holds no multipatch", "COGO-enabled lines, a data model",
             "needs multidimensional or colormapped", "multidimensional raster (ADR-224 group 2)",
             "multidimensional rasters by variable and dimension (ADR-224", "group 2 for SAR")

    def test_no_refusal_says_a_model_grapy_now_holds_is_missing(self):
        for module in (arcpy.conversion, arcpy.management, arcpy.ddd, arcpy.edit, arcpy.sa):
            for name, why in module._REFUSED.items():
                for stale in self.STALE:
                    self.assertNotIn(stale, str(why), f"{module.__name__}.{name}")

    def test_the_names_left_refused_say_why(self):
        for name, said in (("Add3DFormats", "additional tables"), ("Remove3DFormats", "Add3DFormats creates"),
                           ("GenerateLevelOfDetail", "integrated mesh"), ("CreateVoxelSceneLayerContent", "voxel layer"),
                           ("BuildMultidimensionalTranspose", "Cloud Raster Format"),
                           ("BuildMultidimensionalInfo", "Dimensions")):
            with self.assertRaisesRegex(NotBuiltError, said):
                getattr(arcpy.management, name)()
        with self.assertRaisesRegex(NotBuiltError, "ADR-237"):
            arcpy.ia.ExtractWater()


if __name__ == "__main__":
    unittest.main()
