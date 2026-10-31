"""The least-squares tools of arcpy.parcel (ADR-250 §5.4) on a 100 m square with answers worked out below.

    P4 (0,100) ──────── P3 (100,100)        P1 and P2 are fixed (Is Fixed = Yes).
       │      ╲     ╱      │                P3 and P4 are drawn 0.3 m east of where the dimensions put them:
       │        ╳          │                (100.3,100) and (0.3,100).
       │      ╱     ╲      │
    P1 (0,0) ────────── P2 (100,0)

P1 and P2 are control points by their Adjustment Constraint (XYZ constrained), which is what ArcGIS Pro 3.7 takes as
control (Fixed Shape alone is not). The expected values of the blunder test are Pro's, measured on 2026-10-31 on the
same network (ADR-250 condition 2, tools/pro-compare/adr250-parcel.py).

Dimensions, exact for the true square: each side 100 m, each diagonal 100√2 = 141.42135623730951 m; north azimuths
P1→P2 90°, P2→P3 0°, P3→P4 270°, P4→P1 180°, P1→P3 45°, P2→P4 315°.

With the two fixed points, the four sides and one diagonal fix P3 and P4 exactly where the square puts them: the
adjusted coordinates are (100,100) and (0,100), every correction is 0 and both points have moved 0.3 m.
"""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _layers  # noqa: E402
from grapy import _parcel as pf  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

DIAGONAL = 100.0 * math.sqrt(2.0)


def rows(path, fields, where=None):
    with arcpy.da.SearchCursor(path, fields, where) as cursor:
        return [list(r) for r in cursor]


class Square(unittest.TestCase):
    def setUp(self):
        _layers.LAYERS.clear()
        self.directory = tempfile.mkdtemp()
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "lsa")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "Parcels", 3857)[0]
        self.fabric = arcpy.parcel.CreateParcelFabric(self.fds, "Fab")[0]
        arcpy.parcel.AddParcelType(self.fabric, "Lot")
        self.lines, self.points = f"{self.fds}/Lot_Lines", f"{self.fds}/Fab_Points"
        self.adjusted = f"{self.fds}/Fab_AdjustmentPoints"
        with arcpy.da.InsertCursor(f"{self.fds}/Fab_Records", ["Name", "SHAPE@"]) as cursor:
            cursor.insertRow(["R", None])
        self.record = rows(f"{self.fds}/Fab_Records", ["GlobalID"])[0][0]

    def network(self, places, lines, fixed=("P1", "P2"), accuracy=None, shape_fixed=()):
        with arcpy.da.InsertCursor(self.points, ["Name", "AdjustmentConstraint", "IsFixed", "SHAPE@"]) as cursor:
            for name, (x, y) in places.items():
                cursor.insertRow([name, pf.XYZ_CONSTRAINED if name in fixed else pf.XY_FREE,
                                  1 if name in shape_fixed else 0, f"POINT({x} {y})"])
        with arcpy.da.InsertCursor(self.lines, ["CreatedByRecord", "Distance", "Direction", "DistanceAccuracy",
                                                "DirectionAccuracy", "SHAPE@"]) as cursor:
            for a, b, distance, direction in lines:
                (xa, ya), (xb, yb) = places[a], places[b]
                cursor.insertRow([self.record, distance, direction, accuracy, None,
                                  f"LINESTRING({xa} {ya}, {xb} {yb})"])

    def adjusted_points(self):
        return {r[0]: r[1:] for r in rows(self.adjusted, ["Name", "AdjustedX", "AdjustedY", "UpdateSource",
                                                          "Constrained"])}


DRAWN = {"P1": (0, 0), "P2": (100, 0), "P3": (100.3, 100), "P4": (0.3, 100)}
TRUE = {"P1": (0, 0), "P2": (100, 0), "P3": (100, 100), "P4": (0, 100)}
SIDES = [("P1", "P2", 100.0, None), ("P2", "P3", 100.0, None), ("P3", "P4", 100.0, None), ("P4", "P1", 100.0, None),
         ("P1", "P3", DIAGONAL, None)]
FULL = [("P1", "P2", 100.0, 90.0), ("P2", "P3", 100.0, 0.0), ("P3", "P4", 100.0, 270.0), ("P4", "P1", 100.0, 180.0),
        ("P1", "P3", DIAGONAL, 45.0), ("P2", "P4", DIAGONAL, 315.0)]


class Weighted(Square):
    def test_the_two_free_points_are_put_where_the_dimensions_say(self):
        self.network(DRAWN, SIDES)
        arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(self.fabric, "WEIGHTED_LEAST_SQUARES")
        found = self.adjusted_points()
        for name, (x, y) in TRUE.items():
            self.assertAlmostEqual(found[name][0], x, delta=1e-6)
            self.assertAlmostEqual(found[name][1], y, delta=1e-6)
        self.assertEqual(found["P3"][2], pf.YES)
        self.assertEqual(found["P1"][2], pf.YES)          # Pro marks every point of a weighted adjustment
        self.assertEqual(found["P1"][3], "CCC")
        self.assertEqual(found["P3"][3], "FFC")
        corrections = [r[0] for r in rows(f"{self.fds}/Fab_AdjustmentLines", ["MeasurementCorrection"])]
        self.assertEqual(len(corrections), 5)
        for c in corrections:
            self.assertAlmostEqual(c, 0.0, delta=1e-6)
        vectors = rows(f"{self.fds}/Fab_AdjustmentVectors", ["SHAPE@"])
        self.assertEqual(sorted(round(v[0].length, 6) for v in vectors), [0.3, 0.3])

    def test_apply_moves_the_points_lines_and_parcels(self):
        # The parcel drawn on the drawn corners has area 100 × 100 (a parallelogram); applied, a square of 10,000 m².
        self.network(DRAWN, SIDES)
        with arcpy.da.InsertCursor(f"{self.fds}/Lot", ["CreatedByRecord", "SHAPE@"]) as cursor:
            cursor.insertRow([self.record, "POLYGON((0 0, 100 0, 100.3 100, 0.3 100, 0 0))"])
        arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(self.fabric, "WEIGHTED_LEAST_SQUARES")
        arcpy.parcel.ApplyParcelLeastSquaresAdjustment(self.fabric, "0.5 Meters")      # 0.3 m < 0.5 m: nothing moves
        self.assertEqual(sorted(r[0].firstPoint.X for r in rows(self.points, ["SHAPE@"])), [0.0, 0.3, 100.0, 100.3])
        arcpy.parcel.ApplyParcelLeastSquaresAdjustment(self.fabric, None, "UPDATE_ATTRIBUTES")
        moved = {r[0]: (round(r[1].firstPoint.X, 6), round(r[1].firstPoint.Y, 6), r[2])
                 for r in rows(self.points, ["Name", "SHAPE@", "XYUncertainty"])}
        self.assertEqual(moved["P3"][:2], (100.0, 100.0))
        self.assertEqual(moved["P4"][:2], (0.0, 100.0))
        self.assertIsNotNone(moved["P3"][2])
        self.assertIsNotNone(moved["P1"][2])                # a control keeps its place but takes its uncertainty
        self.assertEqual(moved["P1"][:2], (0.0, 0.0))
        for shape, distance in rows(self.lines, ["SHAPE@", "Distance"]):
            self.assertAlmostEqual(shape.length, distance, delta=1e-6)     # the lines now agree with their dimensions
        self.assertAlmostEqual(rows(f"{self.fds}/Lot", ["SHAPE@"])[0][0].area, 10000.0, delta=1e-6)

    def test_fixed_shape_is_no_control_and_no_control_holds_the_points_at_10_m(self):
        # Pro 3.7 on this network with Fixed Shape on P1 and P2 and no constraint: "XYZ point constraints not found",
        # no error, every point free (FFC) and held at 10 m — P1 adjusted to (0.0750, 0.0751), its 95 % ellipse
        # 17.311 by 12.242 m.
        self.network(DRAWN, SIDES, fixed=(), shape_fixed=("P1", "P2"))
        arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(self.fabric, "WEIGHTED_LEAST_SQUARES")
        self.assertIn("XYZ point constraints not found", arcpy.GetMessages())
        p1 = rows(self.adjusted, ["AdjustedX", "AdjustedY", "Constrained", "EllipseMajor", "EllipseMinor"], "Name = 'P1'")[0]
        self.assertAlmostEqual(p1[0], 0.0750, delta=1e-3)
        self.assertAlmostEqual(p1[1], 0.0751, delta=1e-3)
        self.assertEqual(p1[2], "FFC")
        self.assertAlmostEqual(p1[3], 17.311, delta=0.01)
        self.assertAlmostEqual(p1[4], 12.242, delta=0.01)

    def test_a_weighted_point_counts_as_control(self):
        # P2 weighted (XY Accuracy 0.01 m, not fixed) with P1 fixed: two controls, the same answer.
        self.network(DRAWN, SIDES, fixed=("P1",))
        with arcpy.da.UpdateCursor(self.points, ["XYAccuracy"], "Name = 'P2'") as cursor:
            for _ in cursor:
                cursor.updateRow([0.01])
        arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(self.fabric, "WEIGHTED_LEAST_SQUARES")
        found = self.adjusted_points()
        self.assertAlmostEqual(found["P3"][0], 100.0, delta=1e-6)
        self.assertAlmostEqual(found["P4"][0], 0.0, delta=1e-6)

    def test_geodetic_azimuth_types_are_refused_by_name(self):
        self.network(DRAWN, SIDES)
        arcpy.management.AddField(self.lines, "AzimuthType", "LONG")
        with arcpy.da.UpdateCursor(self.lines, ["AzimuthType"]) as cursor:
            for _ in cursor:
                cursor.updateRow([3])
        with self.assertRaises(NotBuiltError) as said:
            arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(self.fabric, "WEIGHTED_LEAST_SQUARES")
        self.assertIn("AzimuthType", str(said.exception))


class Consistency(Square):
    def test_exact_dimensions_have_no_corrections(self):
        # Six distances and twelve directions (four sets of three), all exact: every correction 0, and the free
        # network's datum leaves the points where they are. As Pro writes a set: its backsight's row (no correction)
        # and an angle row for each of the two others — 4 + 8 rows.
        self.network(TRUE, FULL)
        arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(self.fabric, "CONSISTENCY_CHECK")
        lines = rows(f"{self.fds}/Fab_AdjustmentLines", ["MeasurementType", "MeasurementCorrection", "Outlier"])
        self.assertEqual(sum(1 for r in lines if r[0] == pf.DISTANCE), 6)
        self.assertEqual(sum(1 for r in lines if r[0] == pf.DIRECTION_SET), 12)
        self.assertEqual(sum(1 for r in lines if r[0] == pf.DIRECTION_SET and r[1] is None), 4)
        for _, correction, outlier in lines:
            if correction is not None:
                self.assertAlmostEqual(correction, 0.0, delta=1e-7)
        found = self.adjusted_points()
        for name, (x, y) in TRUE.items():
            self.assertAlmostEqual(found[name][0], x, delta=1e-6)
            self.assertAlmostEqual(found[name][1], y, delta=1e-6)
            self.assertEqual(found[name][2], pf.NO)          # a consistency check is not applied

    def test_a_blunder_is_the_outlier(self):
        # P2→P3 recorded as 100.05 m instead of 100 m, every distance to 0.01 m and every direction to the default
        # 30": a 0.05 m blunder, five standard deviations. Of all measurements it alone fails the 1.96 test, and its
        # statistic is the largest in magnitude. The corrections are Pro 3.7's to its 4 decimals (metres) and 6
        # (degrees), its normal statistic to its 2; each angle runs from the direction before it in azimuth order, the
        # backsight being the smallest azimuth (at P1: P4 0°, then P3 45°, then P2 90°).
        blundered = [(a, b, 100.05 if (a, b) == ("P2", "P3") else d, z) for a, b, d, z in FULL]
        self.network(TRUE, blundered, accuracy=0.01)
        arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(self.fabric, "CONSISTENCY_CHECK")
        measured = rows(f"{self.fds}/Fab_AdjustmentLines", ["Measurement", "NormalStatistic", "Outlier", "MeasurementType",
                                                            "Point1Name", "Point2Name", "Point3Name",
                                                            "MeasurementCorrection"])
        worst = max((r for r in measured if r[1] is not None), key=lambda r: abs(r[1]))
        self.assertEqual(worst[0], 100.05)
        self.assertAlmostEqual(worst[1], -3.07, delta=0.01)
        self.assertEqual([r[0] for r in measured if r[2] == pf.YES], [100.05])
        pro = {("P1", "P2", None): -0.0032, ("P2", "P3", None): -0.0189, ("P3", "P4", None): -0.0032,
               ("P4", "P1", None): 0.0004, ("P1", "P3", None): 0.0088, ("P2", "P4", None): 0.0088,
               ("P1", "P4", "P2"): 0.014232, ("P1", "P4", "P3"): -0.005445, ("P2", "P3", "P1"): 0.008786,
               ("P2", "P3", "P4"): -0.003341, ("P3", "P2", "P4"): -0.003341, ("P3", "P2", "P1"): -0.005445,
               ("P4", "P3", "P1"): -0.005445, ("P4", "P3", "P2"): 0.014232}
        found = {(r[4], r[5], r[6]): r[7] for r in measured if r[7] is not None}
        self.assertEqual(set(found), set(pro))
        for key, value in pro.items():
            self.assertAlmostEqual(found[key], value, delta=0.6e-4 if key[2] is None else 1e-6, msg=str(key))
        backsights = {(r[4], r[5]) for r in measured if r[3] == pf.DIRECTION_SET and r[7] is None}
        self.assertEqual(backsights, {("P1", "P4"), ("P2", "P3"), ("P3", "P2"), ("P4", "P3")})


if __name__ == "__main__":
    unittest.main()
