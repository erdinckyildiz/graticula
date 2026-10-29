"""grapy's fourth batch, and every remaining name refused with its reason — ADR-224. Answers are worked out by hand."""
import os
import sqlite3
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.conversion  # noqa: E402
import grapy.management  # noqa: E402


class Round4(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = None
        self.parcels = os.path.join(self.directory, "parcels.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "parcels.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(self.parcels, "name", "TEXT")
        with arcpy.da.InsertCursor(self.parcels, ["name", "SHAPE@"]) as written:
            written.insertRow(("a", "POINT(111319.49079327357 0)"))

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def out(self, name):
        return os.path.join(self.directory, name)

    def raster(self, values, nodata=None, name="r.tif", cell=10.0):
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        made = arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0, 0), cell, cell, nodata)
        arcpy.env.outputCoordinateSystem = None
        return made.save(self.out(name))

    def test_cell_values_and_world_files(self):
        # 1 2      cells of 10 with the top-left corner at (0, 20); -1 is NoData.
        # 3 -1
        path = self.raster([[1, 2], [3, -1]], nodata=-1)
        self.assertEqual(arcpy.management.GetCellValue(path, "15 15")[0], "2")
        self.assertEqual(arcpy.management.GetCellValue(path, "15 5")[0], "NoData")
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.GetCellValue(path, "25 5")
        world = arcpy.management.ExportRasterWorldFile(path)[0]
        self.assertTrue(world.endswith("r.tfw"))
        with open(world, encoding="ascii") as read:
            self.assertEqual(read.read(), "10.0\n0.0\n0.0\n-10.0\n5.0\n15.0\n")

    def test_batch_project_names_outputs_and_skips_taken_names(self):
        folder = self.out("projected")
        os.makedirs(folder)
        arcpy.management.BatchProject(self.parcels, folder, 4326)
        arcpy.management.BatchProject(self.parcels, folder, 4326)
        first, second = os.path.join(folder, "parcels.gpkg"), os.path.join(folder, "parcels_1.gpkg")
        self.assertTrue(arcpy.Exists(first) and arcpy.Exists(second))
        with arcpy.da.SearchCursor(first, ["SHAPE@XY"]) as rows:
            x, y = next(rows)[0]
        self.assertAlmostEqual(x, 1.0)                 # 111319.49… m on the spherical Mercator is one degree
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.BatchProject(self.parcels, folder, 4326, self.parcels)

    def test_alter_fields_keeps_aliases_in_the_schema_extension(self):
        arcpy.management.AlterFields(self.parcels, [["name", "label", "Label of the parcel"]])
        self.assertEqual([f.name for f in arcpy.ListFields(self.parcels)][1:-1], ["label"])
        connection = sqlite3.connect(self.parcels)
        self.assertEqual(connection.execute("select column_name, title from gpkg_data_columns").fetchall(),
                         [("label", "Label of the parcel")])
        connection.close()
        arcpy.management.AlterField(self.parcels, "label", clear_field_alias="TRUE")
        connection = sqlite3.connect(self.parcels)
        self.assertEqual(connection.execute("select title from gpkg_data_columns").fetchall(), [(None,)])
        connection.close()
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.AlterField(self.parcels, "label", field_type="LONG")

    def test_alter_domain_and_coded_value_description(self):
        mg = arcpy.management
        mg.CreateDomain(self.parcels, "kinds", "Kinds", "TEXT")
        mg.AddCodedValueToDomain(self.parcels, "kinds", "a", "first")
        mg.AssignDomainToField(self.parcels, "name", "kinds")
        mg.AlterCodedValueDescription(self.parcels, "kinds", "a", "Agricultural")
        mg.AlterDomain(self.parcels, "kinds", "uses", "Land uses", "DUPLICATE")
        connection = sqlite3.connect(self.parcels)
        self.assertEqual(connection.execute("select name, description, split_policy from grapy_domains").fetchall(),
                         [("uses", "Land uses", "DUPLICATE")])
        self.assertEqual(connection.execute("select constraint_name from gpkg_data_columns").fetchall(), [("uses",)])
        connection.close()
        self.assertEqual(arcpy.da.SearchCursor(mg.DomainToTable(self.parcels, "uses", self.out("d.gpkg"), "c", "d")[0],
                                               ["d"]).next(), ("Agricultural",))
        with self.assertRaises(arcpy.ExecuteError):
            mg.AlterDomain(self.parcels, "uses", merge_policy="SUM_VALUES")

    def test_file_compare_and_delete_multiple(self):
        a, b, c = self.out("a.txt"), self.out("b.txt"), self.out("c.txt")
        for path, text in ((a, "same\n"), (b, "same\n"), (c, "other\n")):
            with open(path, "w", encoding="ascii") as written:
                written.write(text)
        self.assertEqual(arcpy.management.FileCompare(a, b)[1], "true")
        self.assertEqual(arcpy.management.FileCompare(a, c, "BINARY")[1], "false")
        copy = arcpy.management.Copy(self.parcels, self.out("copy.gpkg"))[0]
        arcpy.management.DeleteMultiple([copy, c])
        self.assertFalse(os.path.exists(copy) or os.path.exists(c))

    def test_batch_statistics_and_pyramids(self):
        import rasterio
        folder = self.out("rasters")
        os.makedirs(os.path.join(folder, "deep"))
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        arcpy.NumPyArrayToRaster(np.array([[1, 3]]), arcpy.Point(0, 0), 1, 1).save(os.path.join(folder, "one.tif"))
        arcpy.NumPyArrayToRaster(np.array([[5, 9]]), arcpy.Point(0, 0), 1, 1).save(os.path.join(folder, "deep", "two.tif"))
        arcpy.env.outputCoordinateSystem = None
        arcpy.management.BuildPyramidsandStatistics(folder, build_pyramids="NONE")
        with rasterio.open(os.path.join(folder, "deep", "two.tif")) as read:
            self.assertEqual(float(read.tags(1)["STATISTICS_MEAN"]), 7.0)
        self.assertEqual(arcpy.management.BatchCalculateStatistics([os.path.join(folder, "one.tif")], 2)[0], "true")
        with rasterio.open(os.path.join(folder, "one.tif")) as read:
            self.assertEqual(float(read.tags(1)["STATISTICS_MAXIMUM"]), 1.0)           # every second column: only 1

    def test_every_remaining_name_has_its_reason(self):
        generic = []
        for module, prefix in ((grapy.management, "arcpy.management."), (grapy.conversion, "arcpy.conversion.")):
            for name in module._REFUSED:
                self.assertFalse(hasattr(module, name) and name in vars(module), name)
                with self.assertRaises(arcpy.NotBuiltError) as raised:
                    getattr(module, name)()
                self.assertNotIn("is not built in grapy yet", str(raised.exception), name)
            with self.assertRaises(arcpy.NotBuiltError) as raised:
                getattr(module, "NoSuchToolAtAll")()
            generic.append(str(raised.exception))
        self.assertIn("needs a data model this server does not hold yet (ADR-224 group 2): mosaic datasets",
                      str(self._refusal(grapy.management, "CreateMosaicDataset")))
        self.assertIn("no network", str(self._refusal(grapy.conversion, "WFSToFeatureClass")))
        self.assertTrue(all("is not built in grapy yet" in g for g in generic))

    def _refusal(self, module, name):
        try:
            getattr(module, name)()
        except arcpy.NotBuiltError as error:
            return error


if __name__ == "__main__":
    unittest.main()
