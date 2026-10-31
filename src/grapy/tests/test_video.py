"""arcpy.ia.ConvertVideoMetadata (ADR-237): a metadata table matched to the motion imagery fields and converted to
the units the tool's page requires; and the video tools refused, with their reason."""
import csv
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402


class ConvertVideoMetadata(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.source = os.path.join(self.dir, "drone.csv")
        with open(self.source, "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["time_ms", "lat", "lon_rad", "alt_ft", "Platform Heading Angle", "battery"])
            w.writerow([1546300800231, 36.9, math.radians(-76.13), 1000, 270.5, 88])
            w.writerow([1546300800731, 36.91, math.radians(-76.14), 1010, 271.0, 87])

    def test_matched_fields_are_renamed_and_converted_and_the_rest_copied(self):
        out = os.path.join(self.dir, "out.csv")
        mapping = os.path.join(self.dir, "map.csv")
        arcpy.ia.ConvertVideoMetadata(self.source, out, mapping, [
            ["Precision Timestamp", "time_ms", "Milliseconds"], ["Sensor Latitude", "lat", "Degrees"],
            ["Sensor Longitude", "lon_rad", "Radians"], ["Sensor True Altitude", "alt_ft", "Feet"]])
        with open(out, newline="") as f:
            rows = list(csv.reader(f))
        self.assertEqual(rows[0], ["Precision Timestamp", "Sensor Latitude", "Sensor Longitude", "Sensor True Altitude",
                                   "Platform Heading Angle", "battery"])
        first = rows[1]
        self.assertEqual(first[0], "1546300800231000")  # microseconds, as the page requires
        self.assertAlmostEqual(float(first[2]), -76.13, places=9)
        self.assertAlmostEqual(float(first[3]), 304.8, places=9)  # 1000 ft
        self.assertEqual(first[4], "270.5")  # a field already named correctly is copied as it is
        self.assertEqual(first[5], "88")
        with open(mapping, newline="") as f:
            table = {int(r[0]): r for r in list(csv.reader(f))[1:]}
        self.assertEqual(len(table), 13)
        self.assertEqual(table[13][1:], ["Sensor Latitude", "lat"])  # MISB ST 0601 tag 13
        self.assertEqual(table[5][2], "Platform Heading Angle")
        self.assertEqual(table[16][2], "")

    def test_the_value_table_text_form_and_an_iso_time(self):
        source = os.path.join(self.dir, "iso.csv")
        with open(source, "w", newline="") as f:
            csv.writer(f).writerows([["when", "lat"], ["2019-01-01T00:00:00.231Z", "1"]])
        out = os.path.join(self.dir, "iso_out.csv")
        arcpy.ia.ConvertVideoMetadata(source, out, None, "'Precision Timestamp' when ISO8601;'Sensor Latitude' lat ''")
        with open(out, newline="") as f:
            rows = list(csv.reader(f))
        self.assertEqual(rows[1], ["1546300800231000", "1"])

    def test_what_it_cannot_read_is_refused(self):
        out = os.path.join(self.dir, "x.csv")
        with self.assertRaises(ExecuteError):
            arcpy.ia.ConvertVideoMetadata(self.source, out, None, [["Sensor Speed", "lat", "Degrees"]])
        with self.assertRaises(ExecuteError):
            arcpy.ia.ConvertVideoMetadata(self.source, out, None, [["Sensor Latitude", "nothere", "Degrees"]])
        with self.assertRaises(ExecuteError):
            arcpy.ia.ConvertVideoMetadata(self.source, out, None, [["Sensor Latitude", "lat", "Grads"]])
        with self.assertRaises(ExecuteError):
            arcpy.ia.ConvertVideoMetadata(self.source.replace(".csv", ".json"), out)

    def test_the_tools_that_need_a_video_stream_are_refused_with_that_reason(self):
        for name in ("ExtractVideoFramesToImages", "GenerateVideoMetadataStationary", "VideoMetadataToFeatureClass",
                     "VideoMultiplexer"):
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.ia, name)("a.ts")
            self.assertIn("no video demuxer, muxer or decoder", str(said.exception))


if __name__ == "__main__":
    unittest.main()
