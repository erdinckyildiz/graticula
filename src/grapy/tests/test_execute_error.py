"""D-409: a failing tool raises arcpy.ExecuteError, as ArcGIS Pro 3.7's do (measured 2026-11-12, arcpy_top.md), so a
script's ``except arcpy.ExecuteError`` catches it; it stays an instance of the error grapy raised (ValueError,
FileNotFoundError …), and its text is the tool's error messages, each with a newline, ending with
``Failed to execute (<tool>).``. A name grapy has not built still raises NotBuiltError."""
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class ExecuteErrorOfAFailingTool(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)

    def test_a_missing_input_is_an_execute_error_and_still_a_file_not_found_error(self):
        missing = os.path.join(self.directory, "nope.gpkg") + "/x"
        with self.assertRaises(arcpy.ExecuteError) as caught:
            arcpy.analysis.Buffer(missing, os.path.join(self.directory, "out.gpkg") + "/b", "1 Meters")
        self.assertIsInstance(caught.exception, FileNotFoundError)
        text = str(caught.exception)
        self.assertTrue(text.endswith("Failed to execute (Buffer).\n"), text)
        self.assertIn("does not exist", text)
        self.assertEqual(arcpy.GetMessages(2).splitlines()[-1], "Failed to execute (Buffer).")

    def test_a_bad_value_is_an_execute_error_and_still_a_value_error(self):
        gpkg = os.path.join(self.directory, "a.gpkg")
        polys = arcpy.management.CreateFeatureclass(gpkg, "p", "POLYGON", spatial_reference=32635)[0]
        with self.assertRaises(arcpy.ExecuteError) as caught:
            arcpy.analysis.Union([polys], gpkg + "/u", "ALL", None, "SOMETIMES")
        self.assertIsInstance(caught.exception, ValueError)

    def test_a_name_not_built_is_still_not_built(self):
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.cartography.SmoothLine("x", "y", "PAEK", "1 Meters")


if __name__ == "__main__":
    unittest.main()
