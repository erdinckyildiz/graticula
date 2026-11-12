"""arcpy.ia.ConvertVideoMetadata (ADR-237): a metadata table matched to the motion imagery fields and converted to
the units the tool's page requires; and the video tools refused, with their reason."""
import csv
import os
import sys
import shutil
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402


class ConvertVideoMetadata(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.source = os.path.join(self.dir, "drone.csv")
        with open(self.source, "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["time_ms", "lat", "lon_dms", "alt_ft", "Platform Heading Angle", "battery"])
            w.writerow([1546300800231, 36.9, "76 7 48 W", 1000, 270.5, 88])
            w.writerow([1546300800731, 36.91, "76 8 24 W", 1010, 271.0, 87])

    def test_matched_fields_are_renamed_and_converted_and_the_rest_left_out(self):
        out = os.path.join(self.dir, "out.csv")
        mapping = os.path.join(self.dir, "map.csv")
        arcpy.ia.ConvertVideoMetadata(self.source, out, mapping, [
            # Latitude and longitude take XY, DD, DDM or DMS, as Pro 3.7 checks them (measured 2026-11-08; this test
            # passed Degrees and Radians before, which Pro refuses): 76° 7' 48" W is -76.13.
            ["Precision Timestamp", "time_ms", "Milliseconds"], ["Sensor Latitude", "lat", "DD"],
            ["Sensor Longitude", "lon_dms", "DMS"], ["Sensor True Altitude", "alt_ft", "Feet"]])
        with open(out, newline="") as f:
            rows = list(csv.reader(f))
        # The targets as Pro 3.7 names them, and only they (measured 2026-11-08; "Precision Timestamp" and the other
        # fields copied after them before).
        self.assertEqual(rows[0], ["Precision Time Stamp", "Sensor Latitude", "Sensor Longitude", "Sensor True Altitude",
                                   "Platform Heading Angle"])
        first = rows[1]
        self.assertEqual(first[0], "1546300800231000")  # microseconds, as the page requires
        self.assertAlmostEqual(float(first[2]), -76.13, places=9)
        self.assertAlmostEqual(float(first[3]), 304.8, places=9)  # 1000 ft
        self.assertEqual(first[4], "270.5")  # a field already named correctly is copied as it is
        self.assertEqual(len(first), 5)
        with open(mapping, newline="") as f:
            lines = list(csv.reader(f))
        # Pro 3.7's mapping file: 10000 plus the ST 0601 tag, a row for each field matched, the unit given.
        self.assertEqual(lines[0], ["MISB Tag", "LDS Name", "Local metadata heading", "Units"])
        table = {int(r[0]): r for r in lines[1:]}
        self.assertEqual(len(table), 5)
        self.assertEqual(table[10013][1:], ["Sensor Latitude", "lat", "DD"])
        self.assertEqual(table[10005][1:], ["Platform Heading Angle", "Platform Heading Angle", ""])

    def test_the_value_table_text_form_and_an_iso_time(self):
        source = os.path.join(self.dir, "iso.csv")
        with open(source, "w", newline="") as f:
            csv.writer(f).writerows([["when", "lat"], ["2019-01-01T00:00:00.231Z", "1"]])
        out = os.path.join(self.dir, "iso_out.csv")
        arcpy.ia.ConvertVideoMetadata(source, out, None, "'Precision Timestamp' when ISO8601;'Sensor Latitude' lat ''")
        with open(out, newline="") as f:
            rows = list(csv.reader(f))
        # A date and time is a MISB precision time stamp, counting the 27 leap seconds since 1972 (Pro 3.7, measured
        # 2026-11-08): 1546300800.231 s POSIX is 1546300827231000 µs.
        self.assertEqual(rows[1], ["1546300827231000", "1"])

    def test_pro_s_date_time_keywords(self):
        # Pro 3.7's keywords for Precision Time Stamp (measured 2026-11-08), e.g. a day-first 12-hour clock: 1 January
        # 2019, 01:00:00.231 PM is 1546347600.231 s POSIX, 1546347627231000 µs with the leap seconds.
        source = os.path.join(self.dir, "fmt.csv")
        with open(source, "w", newline="") as f:
            csv.writer(f).writerows([["when", "lat"], ["01/01/2019 01:00:00.231 PM", "1"]])
        out = os.path.join(self.dir, "fmt_out.csv")
        arcpy.ia.ConvertVideoMetadata(source, out, None, [["Precision Time Stamp", "when",
                                                           "DD/MM/YYYY hh:MM:SS.SSS AM/PM"],
                                                          ["Sensor Latitude", "lat", "DD"]])
        with open(out, newline="") as f:
            rows = list(csv.reader(f))
        self.assertEqual(rows[1], ["1546347627231000", "1.0"])

    def test_what_it_cannot_read_is_refused(self):
        out = os.path.join(self.dir, "x.csv")
        with self.assertRaises(ExecuteError):
            arcpy.ia.ConvertVideoMetadata(self.source, out, None, [["Sensor Speed", "lat", "Degrees"]])
        with self.assertRaises(ExecuteError):
            arcpy.ia.ConvertVideoMetadata(self.source, out, None, [["Sensor Latitude", "nothere", "DD"]])
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
