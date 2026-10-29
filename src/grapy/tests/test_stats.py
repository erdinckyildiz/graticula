"""arcpy.stats in grapy, each tool against an answer worked by hand from the formula Esri's "How … works" page gives.

The workhorse is four points on a line, 10 apart, holding 1, 2, 3 and 4. Every number below is derived in the comment
above it from that formula, step by step; none is read back from the code under test. p-values are two-sided from the
normal distribution, ``erfc(|z| / sqrt 2)``.
"""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.stats  # noqa: E402

stats = grapy.stats


def two_sided(z):
    return math.erfc(abs(z) / math.sqrt(2))


class Stats(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.scratchWorkspace = self.directory
        arcpy.env.outputCoordinateSystem = None

    def make(self, name, shape, fields, rows, srid=3857):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=srid)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def points(self, name, xys, values=None, more=()):
        fields = [("v", "DOUBLE")] + list(more)
        rows = []
        for i, xy in enumerate(xys):
            extra = [m[i] for m in values[1:]] if values and len(values) > 1 else []
            rows.append(((values[0][i] if values else 0.0), *extra, arcpy.PointGeometry(arcpy.Point(*xy), 3857)))
        return self.make(name, "POINT", fields, rows)

    def line4(self):
        return self.points("line4", [(0, 0), (10, 0), (20, 0), (30, 0)], [[1.0, 2.0, 3.0, 4.0]])

    def out(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    # ---- Average Nearest Neighbor

    def test_average_nearest_neighbor_by_the_documented_formula(self):
        square = self.points("sq", [(0, 0), (10, 0), (0, 10), (10, 10)])
        # Each corner's nearest neighbour is 10 away: D_O = 10. The minimum enclosing rectangle is the square, A = 100.
        # D_E = 0.5 / sqrt(4/100) = 2.5; ANN = 4; SE = 0.26136 / sqrt(16/100) = 0.6534; z = 7.5 / 0.6534.
        result = stats.AverageNearestNeighbor(square, "EUCLIDEAN_DISTANCE")
        ratio, z, p, expected, observed, report = (result[i] for i in range(6))
        self.assertAlmostEqual(float(ratio), 4.0)
        self.assertAlmostEqual(float(z), 7.5 / 0.6534)
        self.assertAlmostEqual(float(expected), 2.5)
        self.assertAlmostEqual(float(observed), 10.0)
        self.assertAlmostEqual(float(p), two_sided(7.5 / 0.6534))
        self.assertEqual(report, "")
        # With Area = 400: D_E = 0.5 / sqrt(0.01) = 5; ANN = 2; SE = 0.26136 / sqrt(16/400) = 1.3068; z = 5 / 1.3068.
        given = stats.AverageNearestNeighbor(square, "EUCLIDEAN_DISTANCE", "NO_REPORT", 400)
        self.assertAlmostEqual(float(given[0]), 2.0)
        self.assertAlmostEqual(float(given[1]), 5 / 1.3068)
        self.assertAlmostEqual(float(given[2]), two_sided(5 / 1.3068))

    def test_manhattan_distance_and_the_rotated_enclosing_rectangle(self):
        # Three points on the diagonal y = x and one off it: (0,0), (10,10), (20,20), (11,9). The minimum enclosing
        # rectangle is turned 45°: its long side runs (0,0)–(20,20), 20·sqrt 2, and its short side reaches the point
        # (11,9), whose distance from y = x is |11 − 9| / sqrt 2 = sqrt 2. A = 20·sqrt 2 · sqrt 2 = 40.
        # Manhattan nearest neighbours: (0,0)→(10,10) 20; (10,10)→(11,9) 2; (20,20)→(11,9) 20 and →(10,10) 20, so 20;
        # (11,9)→(10,10) 2. D_O = (20 + 2 + 20 + 2) / 4 = 11. D_E = 0.5 / sqrt(4/40) = 0.5 / sqrt 0.1.
        path = self.points("diag", [(0, 0), (10, 10), (20, 20), (11, 9)])
        result = stats.AverageNearestNeighbor(path, "MANHATTAN_DISTANCE")
        self.assertAlmostEqual(float(result[4]), 11.0)
        self.assertAlmostEqual(float(result[3]), 0.5 / math.sqrt(0.1))

    def test_a_report_and_a_geographic_reference_are_refused_by_name(self):
        square = self.points("sq", [(0, 0), (10, 0), (0, 10), (10, 10)])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "Generate_Report"):
            stats.AverageNearestNeighbor(square, "EUCLIDEAN_DISTANCE", "GENERATE_REPORT")
        degrees = self.make("deg", "POINT", [], [(arcpy.PointGeometry(arcpy.Point(30, 40), 4326),),
                                                 (arcpy.PointGeometry(arcpy.Point(31, 40), 4326),)], srid=4326)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "chordal"):
            stats.AverageNearestNeighbor(degrees, "EUCLIDEAN_DISTANCE")

    # ---- Global Moran's I

    def test_global_morans_i_with_a_fixed_band_row_standardized(self):
        # Band 10: neighbours 0:{1}, 1:{0,2}, 2:{1,3}, 3:{2}. Row-standardized: w01 = 1; w10 = w12 = .5;
        # w21 = w23 = .5; w32 = 1. Mean 2.5, z = (−1.5, −.5, .5, 1.5), Σz² = 5, S0 = 4.
        # ΣΣ w z_i z_j = .75 + (.375 − .125) + (−.125 + .375) + .75 = 2. I = (4/4)(2/5) = 0.4. E[I] = −1/3.
        # S1 = ½ΣΣ(w_ij + w_ji)²: pairs (0,1) 1.5² twice, (1,2) 1² twice, (2,3) 1.5² twice → 2.25 + 1 + 2.25 = 5.5.
        # Row sums 1; column sums (.5, 1.5, 1.5, .5); S2 = 1.5² + 2.5² + 2.5² + 1.5² = 17.
        # D = nΣz⁴/(Σz²)² = 4 · 10.25 / 25 = 1.64.
        # A = 4[(16 − 12 + 3)5.5 − 4·17 + 3·16] = 4 · 18.5 = 74; B = 1.64[12·5.5 − 8·17 + 6·16] = 1.64 · 26 = 42.64;
        # C = 3·2·1·16 = 96. E[I²] = 31.36/96 = 49/150; V = 49/150 − 1/9 = 97/450; z = (0.4 + 1/3)/sqrt(97/450).
        result = stats.SpatialAutocorrelation(self.line4(), "v", "NO_REPORT", "FIXED_DISTANCE_BAND",
                                              "EUCLIDEAN_DISTANCE", "ROW", 10)
        z = (0.4 + 1 / 3) / math.sqrt(97 / 450)
        self.assertAlmostEqual(float(result[0]), 0.4)
        self.assertAlmostEqual(float(result[1]), z)
        self.assertAlmostEqual(float(result[2]), two_sided(z))
        self.assertEqual(result[3], "")
        # Left out, the band is the largest nearest-neighbour distance — 10 here — so the answer is the same.
        default = stats.SpatialAutocorrelation(self.line4(), "v", "NO_REPORT", "FIXED_DISTANCE_BAND",
                                               "EUCLIDEAN_DISTANCE", "ROW")
        self.assertAlmostEqual(float(default[0]), 0.4)

    def test_morans_i_with_inverse_distance_and_no_standardization(self):
        # Points at 0, 10, 20, 30, band 0 (no threshold): w_ij = 1/d. Pairs at 10: (0,1),(1,2),(2,3) → .1;
        # at 20: (0,2),(1,3) → .05; at 30: (0,3) → 1/30.
        # ΣΣ w z_i z_j = 2[.1(.75 − .25 + .75) + .05(−.75 − .75) + (1/30)(−2.25)] = 2[.125 − .075 − .075] = −.05.
        # S0 = 2(.3 + .1 + 1/30) = 13/15. I = (4 / (13/15)) · (−.05 / 5) = −0.6/13.
        result = stats.SpatialAutocorrelation(self.line4(), "v", "NO_REPORT", "INVERSE_DISTANCE",
                                              "EUCLIDEAN_DISTANCE", "NONE", 0)
        self.assertAlmostEqual(float(result[0]), -0.6 / 13)

    def test_weights_from_a_file_and_zone_of_indifference_are_refused(self):
        for concept, said in (("GET_SPATIAL_WEIGHTS_FROM_FILE", ".swm"), ("ZONE_OF_INDIFFERENCE", "diminishing")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                stats.SpatialAutocorrelation(self.line4(), "v", "NO_REPORT", concept, "EUCLIDEAN_DISTANCE", "ROW")

    # ---- General G

    def test_general_g_with_binary_weights(self):
        # Band 10, no standardization: w = 1 for (0,1), (1,2), (2,3) both ways. x = 1, 2, 3, 4.
        # ΣΣ w x_i x_j = 2(2 + 6 + 12) = 40; ΣΣ_{j≠i} x_i x_j = (Σx)² − Σx² = 100 − 30 = 70; G = 4/7.
        # W = 6, E[G] = 6/12 = .5. S1 = ½ · 6 · 2² = 12. Row sums (1,2,2,1), S2 = Σ(2·row)² = 4 + 16 + 16 + 4 = 40.
        # D0 = 7·12 − 4·40 + 3·36 = 32; D1 = −(12·12 − 8·40 + 6·36) = −40; D2 = −(8·12 − 7·40 + 6·36) = −32;
        # D3 = 4·3·12 − 2·5·40 + 8·36 = 32; D4 = 12 − 40 + 36 = 8. Σx = 10, Σx² = 30, Σx³ = 100, Σx⁴ = 354.
        # A = 32·900 − 40·354 − 32·100·30 = −81360; B = 32·10·100 + 8·10⁴ = 112000; C = 70² · 24 = 117600.
        # E[G²] = 30640/117600 = 383/1470; V = 383/1470 − 1/4 = 31/2940; z = (4/7 − 1/2)/sqrt(31/2940).
        result = stats.HighLowClustering(self.line4(), "v", "NO_REPORT", "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE",
                                         "NONE", 10)
        z = (4 / 7 - 0.5) / math.sqrt(31 / 2940)
        self.assertAlmostEqual(float(result[0]), 4 / 7)
        self.assertAlmostEqual(float(result[1]), z)
        self.assertAlmostEqual(float(result[2]), two_sided(z))
        negative = self.points("neg", [(0, 0), (10, 0), (20, 0), (30, 0)], [[1.0, -2.0, 3.0, 4.0]])
        with self.assertRaisesRegex(ValueError, "negative"):
            stats.HighLowClustering(negative, "v", "NO_REPORT", "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE", 10)

    # ---- Gi*

    def test_hot_spots_gi_star_counts_each_feature_in_its_own_neighbourhood(self):
        # x = 1, 2, 3, 4, band 10, w_ii = 1. X̄ = 2.5, S = sqrt(30/4 − 6.25) = sqrt 1.25.
        # Feature 0: {0, 1}: Σwx = 3, Σw = 2, num = 3 − 5 = −2, den = sqrt1.25 · sqrt((4·2 − 4)/3) = sqrt(5/3).
        # Feature 1: {0, 1, 2}: Σwx = 6, Σw = 3, num = −1.5, den = sqrt1.25 · sqrt((12 − 9)/3) = sqrt 1.25.
        out = stats.HotSpots(self.line4(), "v", self.out("gi"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE",
                             10)
        self.assertEqual((out[1], out[2], out[3]), ("GiZScore", "GiPValue", "SOURCE_ID"))
        got = self.rows(out[0], ["SOURCE_ID", "v", "GiZScore", "GiPValue", "Gi_Bin"])
        z0, z1 = -2 / math.sqrt(5 / 3), -1.5 / math.sqrt(1.25)
        for (source, v, z, p, b), (want_z, want_source) in zip(got, [(z0, 1), (z1, 2), (-z1, 3), (-z0, 4)]):
            self.assertEqual(source, want_source)
            self.assertAlmostEqual(z, want_z)
            self.assertAlmostEqual(p, two_sided(want_z))
            self.assertEqual(b, 0)   # |z| ≤ 1.55: p > 0.10

    def test_gi_bin_reads_the_confidence_level(self):
        # Ten points 10 apart, x = 1 ×5 then 0 ×5, band 10. X̄ = .5, S = sqrt(5/10 − .25) = .5.
        # An interior feature among ones: Σwx = 3, Σw = 3, num = 3 − 1.5 = 1.5,
        #   den = .5 sqrt((10·3 − 9)/9) = .5 sqrt(21/9); z = 1.5 / (.5 sqrt(21/9)) ≈ 1.964, p ≈ 0.0495 → bin 2.
        # Feature 0: {0, 1}: Σwx = 2, Σw = 2, num = 1, den = .5 sqrt((20 − 4)/9) = 2/3; z = 1.5, p ≈ 0.134 → bin 0.
        # Feature 4: {3, 4, 5}: Σwx = 2, num = .5, z = .5 / (.5 sqrt(21/9)) ≈ 0.65 → bin 0. The zeros mirror them.
        path = self.points("ten", [(10 * i, 0) for i in range(10)], [[1.0] * 5 + [0.0] * 5])
        out = stats.HotSpots(path, "v", self.out("bins"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE", 10)
        got = self.rows(out[0], ["GiZScore", "Gi_Bin"])
        self.assertAlmostEqual(got[2][0], 1.5 / (0.5 * math.sqrt(21 / 9)))
        self.assertLess(two_sided(got[2][0]), 0.05)
        self.assertEqual([b for _, b in got], [0, 2, 2, 2, 0, 0, -2, -2, -2, 0])

    def test_gi_star_with_inverse_distance_k_nearest_and_contiguity(self):
        # Inverse distance, no band: points at 0, 2, 4 holding 1, 2, 3; w = 1/d, w_ii = 1.
        # Feature 0: w = (1, 1/2, 1/4): Σwx = 1 + 1 + .75 = 2.75, Σw = 1.75, X̄ = 2, num = 2.75 − 3.5 = −.75;
        # S = sqrt(14/3 − 4) = sqrt(2/3); Σw² = 1.3125; den = sqrt(2/3) sqrt((3·1.3125 − 1.75²)/2) = sqrt(2/3)·sqrt .4375.
        three = self.points("three", [(0, 0), (2, 0), (4, 0)], [[1.0, 2.0, 3.0]])
        out = stats.HotSpots(three, "v", self.out("inv"), "INVERSE_DISTANCE", "EUCLIDEAN_DISTANCE", "NONE", 0)
        self.assertAlmostEqual(self.rows(out[0], ["GiZScore"])[0][0], -0.75 / (math.sqrt(2 / 3) * math.sqrt(0.4375)))
        # A distance under 1 weighs 1: at 0, 0.5, 4 the first two are each other's weight 1, and (0,4) is 1/4.
        # Feature 0: w = (1, 1, 1/4): Σwx = 1 + 2 + .75 = 3.75, Σw = 2.25, num = 3.75 − 4.5 = −.75;
        # Σw² = 2.0625; den = sqrt(2/3) sqrt((3·2.0625 − 2.25²)/2) = sqrt(2/3) sqrt .5625.
        near = self.points("near", [(0, 0), (0.5, 0), (4, 0)], [[1.0, 2.0, 3.0]])
        out = stats.HotSpots(near, "v", self.out("near_out"), "INVERSE_DISTANCE", "EUCLIDEAN_DISTANCE", "NONE", 0)
        self.assertAlmostEqual(self.rows(out[0], ["GiZScore"])[0][0], -0.75 / (math.sqrt(2 / 3) * math.sqrt(0.5625)))
        # K nearest, k = 1, points at 0, 10, 15, 30 holding 1..4: 0→1, 1→2, 2→1, 3→2. With itself:
        # feature 0 {0,1}: Σwx = 3 → z = −2/sqrt(5/3); feature 1 {1,2}: Σwx = 5 = 2X̄ → z = 0;
        # feature 3 {3,2}: Σwx = 7 → z = 2/sqrt(5/3).
        knn = self.points("knn", [(0, 0), (10, 0), (15, 0), (30, 0)], [[1.0, 2.0, 3.0, 4.0]])
        out = stats.HotSpots(knn, "v", self.out("knn_out"), "K_NEAREST_NEIGHBORS", "EUCLIDEAN_DISTANCE", "NONE",
                             number_of_neighbors=1)
        got = [r[0] for r in self.rows(out[0], ["GiZScore"])]
        self.assertAlmostEqual(got[0], -2 / math.sqrt(5 / 3))
        self.assertAlmostEqual(got[1], 0.0)
        self.assertAlmostEqual(got[3], 2 / math.sqrt(5 / 3))
        # Contiguity: squares A(0,0), B(10,0), D(10,10) and E(30,30), each 10 wide, holding 1, 2, 3, 4.
        # A shares an edge with B and only a corner with D. Edges: A's {A, B}: Σwx = 3, Σw = 2 → z = −2/sqrt(5/3).
        # Corners: A's {A, B, D}: Σwx = 6, Σw = 3 → num = −1.5, den = sqrt1.25 · 1 → z = −1.5/sqrt 1.25.
        def square(x, y):
            return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y), arcpy.Point(x + 10, y), arcpy.Point(x + 10, y + 10),
                                              arcpy.Point(x, y + 10)]), 3857)
        cells = self.make("cells", "POLYGON", [("v", "DOUBLE")], [(1.0, square(0, 0)), (2.0, square(10, 0)),
                                                                   (3.0, square(10, 10)), (4.0, square(30, 30))])
        edges = stats.HotSpots(cells, "v", self.out("edges"), "CONTIGUITY_EDGES_ONLY", "EUCLIDEAN_DISTANCE", "NONE")
        corners = stats.HotSpots(cells, "v", self.out("corners"), "CONTIGUITY_EDGES_CORNERS", "EUCLIDEAN_DISTANCE",
                                 "NONE")
        self.assertAlmostEqual(self.rows(edges[0], ["GiZScore"])[0][0], -2 / math.sqrt(5 / 3))
        self.assertAlmostEqual(self.rows(corners[0], ["GiZScore"])[0][0], -1.5 / math.sqrt(1.25))

    def test_keywords_are_taken_as_labels_too(self):
        # Esri's own sample passes "Fixed Distance Band", "Euclidean Distance", "None": the same answer as above.
        out = stats.HotSpots(self.line4(), "v", self.out("labels"), "Fixed Distance Band", "Euclidean Distance", "None",
                             10)
        self.assertAlmostEqual(self.rows(out[0], ["GiZScore"])[0][0], -2 / math.sqrt(5 / 3))

    def test_fdr_is_benjamini_hochberg(self):
        # The ten points of test_gi_bin_reads_the_confidence_level: six p-values ≈ 0.0495, two ≈ 0.134, two ≈ 0.513.
        # At α = .10 the largest k with p_(k) ≤ k·.10/10 is 6 (0.0495 ≤ 0.06; 0.134 > 0.07): those six are kept.
        # At α = .05, p_(k) ≤ k·.005 holds for no k ≤ 6 (0.0495 > 0.03): none. So the six are bin ±1.
        path = self.points("ten", [(10 * i, 0) for i in range(10)], [[1.0] * 5 + [0.0] * 5])
        out = stats.HotSpots(path, "v", self.out("fdr"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE", 10,
                             None, None, "APPLY_FDR")
        self.assertEqual([r[0] for r in self.rows(out[0], ["Gi_Bin"])], [0, 1, 1, 1, 0, 0, -1, -1, -1, 0])

    # ---- Local Moran's I

    def test_local_morans_i_with_the_documented_variance(self):
        # Band 10, row-standardized, as in Global Moran's I above. S² = Σz²/(n−1) = 5/3.
        # I_0 = (−1.5/(5/3)) · (1 · −.5) = .45;  I_1 = (−.5/(5/3)) · (.5·−1.5 + .5·.5) = .15. E[I_i] = −1/3.
        # Feature 0: b2 = Σ_{k≠0} z⁴ / (Σ_{k≠0} z²)² = 5.1875 / 2.75² ; Σw² = 1; (Σw)² = 1;
        #   A = (4 − b2)/3, B = (2 b2 − 4)/6, V = A − B − 1/9.
        # Feature 1: b2 = 10.1875 / 4.75²; Σw² = .5; A = (4 − b2)·.5/3, B = (2 b2 − 4)/6, V = A − B − 1/9.
        def z_of(index, b2, squares):
            variance = (4 - b2) * squares / 3 - (2 * b2 - 4) / 6 - 1 / 9
            return (index + 1 / 3) / math.sqrt(variance)
        z0 = z_of(0.45, 5.1875 / 2.75 ** 2, 1.0)
        z1 = z_of(0.15, 10.1875 / 4.75 ** 2, 0.5)
        out = stats.ClustersOutliers(self.line4(), "v", self.out("lmi"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE",
                                     "ROW", 10, None, "NO_FDR", 0)
        self.assertEqual(tuple(out[i] for i in range(1, 6)), ("LMiIndex", "LMiZScore", "LMiPValue", "COType", "SOURCE_ID"))
        got = self.rows(out[0], ["LMiIndex", "LMiZScore", "LMiPValue", "COType"])
        for (index, z, p, kind), (want_i, want_z) in zip(got, [(0.45, z0), (0.15, z1), (0.15, z1), (0.45, z0)]):
            self.assertAlmostEqual(index, want_i)
            self.assertAlmostEqual(z, want_z)
            self.assertAlmostEqual(p, two_sided(want_z))
            self.assertEqual(kind, "")   # none is significant at 0.05

    def test_local_morans_i_permutations_and_a_feature_with_no_neighbours(self):
        # Three points, each the other two's neighbour, values 0, 3, 6 (deviations −3, 0, 3), row-standardized
        # weights .5: a permutation of the other two values leaves .5·(a + b) unchanged, so every draw equals the
        # observed I_i. Then M = R and the folded count is R − M = 0: the pseudo p-value is (0 + 1)/(9 + 1) = 0.1.
        three = self.points("three", [(0, 0), (10, 0), (20, 0)], [[0.0, 3.0, 6.0]])
        out = stats.ClustersOutliers(three, "v", self.out("perm"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE",
                                     "ROW", 100, None, "NO_FDR", 9)
        self.assertEqual([r[0] for r in self.rows(out[0], ["LMiPValue"])], [0.1, 0.1, 0.1])
        lonely = self.points("lonely", [(0, 0), (10, 0), (20, 0), (30, 0), (500, 0)], [[1.0, 2.0, 3.0, 4.0, 5.0]])
        out = stats.ClustersOutliers(lonely, "v", self.out("nn"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "ROW",
                                     10, None, "NO_FDR", 0)
        self.assertEqual(self.rows(out[0], ["COType"])[4], ("NN",))

    # ---- Measuring geographic distributions

    def test_mean_center_weighted_by_case_with_its_dimension(self):
        # Case a: (0,0) w 1 and (10,0) w 3 → X = (0 + 30)/4 = 7.5, Y = 0; dimension mean of 2 and 4 = 3.
        # Case b: (0,10) alone.
        path = self.points("mc", [(0, 0), (10, 0), (0, 10)], [[1.0, 3.0, 1.0], ["a", "a", "b"], [2.0, 4.0, 9.0]],
                           more=[("case", "TEXT"), ("dim", "DOUBLE")])
        out = stats.MeanCenter(path, self.out("mean"), "v", "case", "dim").getOutput(0)
        self.assertEqual(self.rows(out, ["XCOORD", "YCOORD", "case", "dim", "SHAPE@XY"]),
                         [(7.5, 0.0, "a", 3.0, (7.5, 0.0)), (0.0, 10.0, "b", 9.0, (0.0, 10.0))])

    def test_median_center_is_where_the_diagonals_cross(self):
        # For four points in convex position the point of least total distance is where the diagonals cross:
        # (0,0)–(10,10) is y = x; (10,0)–(0,4) is y = 4 − 0.4x; x = 4 − 0.4x → x = y = 20/7.
        # The attribute median of 1, 2, 3, 10 is (2 + 3)/2 = 2.5.
        path = self.points("med", [(0, 0), (10, 0), (10, 10), (0, 4)], [[1.0, 2.0, 3.0, 10.0]])
        out = stats.MedianCenter(path, self.out("median"), None, None, "v").getOutput(0)
        (x, y, v), = self.rows(out, ["XCOORD", "YCOORD", "v"])
        self.assertAlmostEqual(x, 20 / 7, places=6)
        self.assertAlmostEqual(y, 20 / 7, places=6)
        self.assertEqual(v, 2.5)
        # Weighted: a weight above the others' total pull holds the centre on that point. (10,0) weighing 100 against
        # three unit weights, whose pull can be at most 3, is the weighted median.
        weighted = self.points("medw", [(0, 0), (10, 0), (10, 10), (0, 4)], [[1.0, 100.0, 1.0, 1.0]])
        (x, y), = self.rows(stats.MedianCenter(weighted, self.out("medw_out"), "v").getOutput(0), ["XCOORD", "YCOORD"])
        self.assertAlmostEqual(x, 10.0, places=6)
        self.assertAlmostEqual(y, 0.0, places=6)

    def test_central_feature_by_accumulated_distance(self):
        # Points at 0, 1, 10: sums 11, 10, 19 → the one at 1. Weighted 1, 1, 100 (each destination's weight):
        # 0: 1·1 + 100·10 = 1001; 1: 1·1 + 100·9 = 901; 10: 1·10 + 1·9 = 19 → the one at 10.
        path = self.points("cf", [(0, 0), (1, 0), (10, 0)], [[1.0, 1.0, 100.0]])
        plain = stats.CentralFeature(path, self.out("cf1"), "EUCLIDEAN_DISTANCE").getOutput(0)
        self.assertEqual(self.rows(plain, ["SHAPE@XY"]), [((1.0, 0.0),)])
        heavy = stats.CentralFeature(path, self.out("cf2"), "EUCLIDEAN_DISTANCE", "v").getOutput(0)
        self.assertEqual(self.rows(heavy, ["v", "SHAPE@XY"]), [(100.0, (10.0, 0.0))])
        # Manhattan: (0,0), (3,4), (6,0): sums 7 + 6 = 13, 7 + 7 = 14, 6 + 7 = 13 → two tie, both copied.
        tie = self.points("tie", [(0, 0), (3, 4), (6, 0)])
        both = stats.CentralFeature(tie, self.out("cf3"), "MANHATTAN_DISTANCE").getOutput(0)
        self.assertEqual(self.rows(both, ["SHAPE@XY"]), [((0.0, 0.0),), ((6.0, 0.0),)])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "Self_Potential_Weight_Field"):
            stats.CentralFeature(path, self.out("cf4"), "EUCLIDEAN_DISTANCE", None, "v")

    def test_standard_distance_and_the_standard_deviational_ellipse(self):
        # Corners of a 10 square: centre (5,5), SD = sqrt(Σ25/4 + Σ25/4) = sqrt 50. Two deviations: radius 2 sqrt 50.
        square = self.points("sq", [(0, 0), (10, 0), (0, 10), (10, 10)])
        circle = stats.StandardDistance(square, self.out("sd"), "2_STANDARD_DEVIATIONS").getOutput(0)
        (cx, cy, sd, area), = self.rows(circle, ["CenterX", "CenterY", "StdDist", "SHAPE@AREA"])
        self.assertEqual((cx, cy), (5.0, 5.0))
        self.assertAlmostEqual(sd, 2 * math.sqrt(50))
        # A regular 360-gon with its vertices on the circle: area = (360/2) sin(2π/360) r², r² = 200.
        self.assertAlmostEqual(area, 180 * math.sin(2 * math.pi / 360) * 200)
        # (1,1), (−1,−1), (.5,−.5), (−.5,.5): Σx̃² = Σỹ² = 2.5, Σx̃ỹ = 1 + 1 − .25 − .25 = 1.5.
        # σ1² = (5 + sqrt(0 + 4·2.25))/8 = 1, σ2² = (5 − 3)/8 = .25. Times sqrt 2: long sqrt 2, short sqrt 2 / 2.
        # The long axis runs north-east: 45° clockwise from noon.
        tilted = self.points("tilt", [(1, 1), (-1, -1), (0.5, -0.5), (-0.5, 0.5)])
        ellipse = stats.DirectionalDistribution(tilted, self.out("dd")).getOutput(0)
        (cx, cy, sx, sy, rotation, shape), = self.rows(ellipse, ["CenterX", "CenterY", "XStdDist", "YStdDist",
                                                                 "Rotation", "SHAPE@"])
        self.assertAlmostEqual(cx, 0.0)
        self.assertAlmostEqual(sx, math.sqrt(2) / 2)
        self.assertAlmostEqual(sy, math.sqrt(2))
        self.assertAlmostEqual(rotation, 45.0)
        self.assertTrue(shape.contains(arcpy.PointGeometry(arcpy.Point(0.9, 0.9), 3857)))
        self.assertFalse(shape.contains(arcpy.PointGeometry(arcpy.Point(0.6, -0.6), 3857)))   # past the short semi-axis, sqrt 2 / 2
        # East–west spread: (−2,0), (2,0), (0,−1), (0,1): var x = 2, var y = .5 → long sqrt2·sqrt2 = 2 at 90°.
        flat = self.points("flat", [(-2, 0), (2, 0), (0, -1), (0, 1)])
        (sx, sy, rotation), = self.rows(stats.DirectionalDistribution(flat, self.out("dd2")).getOutput(0),
                                        ["XStdDist", "YStdDist", "Rotation"])
        self.assertAlmostEqual(sx, 1.0)
        self.assertAlmostEqual(sy, 2.0)
        self.assertAlmostEqual(rotation, 90.0)

    def test_linear_directional_mean(self):
        # East (0,0)→(10,0), θ = 0, and north (0,0)→(0,10), θ = 90°: Σsin = 1, Σcos = 1 → DirMean 45, CompassA 45,
        # CirVar = 1 − sqrt 2 / 2. Centroids (5,0) and (0,5): AveX = AveY = 2.5. AveLen 10.
        def line(*xys):
            return arcpy.Polyline(arcpy.Array([arcpy.Point(*xy) for xy in xys]), 3857)
        path = self.make("ldm", "POLYLINE", [], [(line((0, 0), (10, 0)),), (line((0, 0), (0, 10)),)])
        out = stats.DirectionalMean(path, self.out("ldm_out")).getOutput(0)
        (compass, mean, variance, ax, ay, length, shape), = self.rows(
            out, ["CompassA", "DirMean", "CirVar", "AveX", "AveY", "AveLen", "SHAPE@"])
        self.assertAlmostEqual(compass, 45.0)
        self.assertAlmostEqual(mean, 45.0)
        self.assertAlmostEqual(variance, 1 - math.sqrt(2) / 2)
        self.assertEqual((ax, ay, length), (2.5, 2.5, 10.0))
        start, end = shape._shape.coords
        self.assertAlmostEqual(start[0], 2.5 - 5 / math.sqrt(2))
        self.assertAlmostEqual(end[1], 2.5 + 5 / math.sqrt(2))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "Orientation_Only"):
            stats.DirectionalMean(path, self.out("ldm2"), "ORIENTATION_ONLY")

    def test_ripleys_k_as_l_of_d(self):
        # Corners of a 10 square: the minimum enclosing rectangle is the square, A = 100, n = 4.
        # d = 10: each corner has its two sides' neighbours, ΣΣk = 8; L = sqrt(100·8 / (π·4·3)) = sqrt(800/(12π)).
        # d = 15: the diagonals (14.14) count too, ΣΣk = 12; L = sqrt(1200/(12π)) = sqrt(100/π).
        square = self.points("sq", [(0, 0), (10, 0), (0, 10), (10, 10)])
        table = stats.MultiDistanceSpatialClustering(square, self.out("k"), 2, Beginning_Distance=10,
                                                     Distance_Increment=5).getOutput(0)
        got = self.rows(table, ["ExpectedK", "ObservedK", "DiffK"])
        for (expected, observed, diff), (want_d, want_l) in zip(got, [(10, math.sqrt(800 / (12 * math.pi))),
                                                                      (15, math.sqrt(100 / math.pi))]):
            self.assertEqual(expected, want_d)
            self.assertAlmostEqual(observed, want_l)
            self.assertAlmostEqual(diff, want_l - want_d)
        # Defaults: the maximum distance is 25 percent of the rectangle's longer side, 2.5; ten bands of 0.25 from 0.25.
        # No pair is that close, so every ObservedK is 0.
        default = stats.MultiDistanceSpatialClustering(square, self.out("k2"), 10).getOutput(0)
        got = self.rows(default, ["ExpectedK", "ObservedK"])
        for (expected, observed), k in zip(got, range(1, 11)):
            self.assertAlmostEqual(expected, 0.25 * k)
            self.assertEqual(observed, 0.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "Compute_Confidence_Envelope"):
            stats.MultiDistanceSpatialClustering(square, self.out("k3"), 10, "99_PERMUTATIONS")
        with self.assertRaisesRegex(arcpy.NotBuiltError, "Boundary_Correction_Method"):
            stats.MultiDistanceSpatialClustering(square, self.out("k4"), 10,
                                                 Boundary_Correction_Method="RIPLEY_EDGE_CORRECTION_FORMULA")

    # ---- Utilities

    def test_distance_band_from_neighbor_count_and_collect_events(self):
        # Points at 0, 10, 20, 30. Second-nearest: 20, 10, 10, 20 → min 10, average 15, max 20.
        result = stats.CalculateDistanceBand(self.line4(), 2, "EUCLIDEAN_DISTANCE")
        self.assertEqual([float(result[i]) for i in range(3)], [10.0, 15.0, 20.0])
        events = self.points("events", [(0, 0), (5, 5), (0, 0), (0, 0)])
        collected = stats.CollectEvents(events, self.out("collected"))
        self.assertEqual((collected[1], float(collected[2])), ("ICOUNT", 3.0))
        self.assertEqual(self.rows(collected[0], ["ICOUNT", "SHAPE@XY"]), [(3, (0.0, 0.0)), (1, (5.0, 5.0))])

    def test_neighborhood_summary_statistics_by_the_documented_formulas(self):
        # Points at 0, 10, 20, 30, 40 holding 1, 2, 3, 4, 10; two nearest neighbours, the focal feature included.
        # Feature 0: {1, 2, 3}: mean 2, s = sqrt(2/3), Σ(x − x̄)³ = 0 → skewness 0. Quantiles with weights 1/3:
        # p = .5 falls inside (1/3, 2/3) → 2; p = .25 inside (0, 1/3) → 1; p = .75 inside (2/3, 1) → 3. IQR 2, QI 0.
        # Its distances (focal left out): 10 and 20 → mean 15, s 5; p = .5 is exactly 1/2 → (10 + 20)/2 = 15.
        # Feature 4: {3, 4, 10}: mean 17/3, deviations −8/3, −5/3, 13/3; Σ(x − x̄)³/3 = 1560/81, s² = 258/27;
        # skewness cbrt(1560/81)/sqrt(258/27). Median 4, Q1 3, Q3 10, IQR 7, QI = (8 − 13)/7 = −5/7.
        path = self.points("nss", [(10 * i, 0) for i in range(5)], [[1.0, 2.0, 3.0, 4.0, 10.0]])
        out = stats.NeighborhoodSummaryStatistics(path, self.out("nss_out"), "v", "ALL", "INCLUDE_FOCAL", "IGNORE_NULLS",
                                                  "NUMBER_OF_NEIGHBORS", None, 2).getOutput(0)
        fields = ["v", "MEAN_v", "MED_v", "STD_v", "IQR_v", "SKEW_v", "QIMB_v", "NUM_NBRS_v",
                  "MEAN_DIST", "MED_DIST", "STD_DIST", "NUM_NBRS_DIST"]
        got = self.rows(out, fields)
        expected0 = (1.0, 2.0, 2.0, math.sqrt(2 / 3), 2.0, 0.0, 0.0, 3, 15.0, 15.0, 5.0, 2)
        for value, want in zip(got[0], expected0):
            self.assertAlmostEqual(value, want)
        skew = (1560 / 81) ** (1 / 3) / math.sqrt(258 / 27)
        expected4 = (10.0, 17 / 3, 4.0, math.sqrt(258 / 27), 7.0, skew, -5 / 7, 3)
        for value, want in zip(got[4], expected4):
            self.assertAlmostEqual(value, want)
        # Gaussian, band 10 (the bandwidth defaults to it): feature 4 has itself (weight 1) and the point at 30
        # (weight exp(−½)). Mean = (10 + 4e^−½)/(1 + e^−½). Normalised, 4 weighs e^−½/(1 + e^−½) ≈ 0.378 < 0.5, so the
        # median is 10.
        gauss = stats.NeighborhoodSummaryStatistics(path, self.out("nss_g"), "v", "ALL", "INCLUDE_FOCAL",
                                                    "IGNORE_NULLS", "DISTANCE_BAND", 10, None, None,
                                                    "GAUSSIAN").getOutput(0)
        mean, median = self.rows(gauss, ["MEAN_v", "MED_v"])[4]
        e = math.exp(-0.5)
        self.assertAlmostEqual(mean, (10 + 4 * e) / (1 + e))
        self.assertEqual(median, 10.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "kernel_bandwidth"):
            stats.NeighborhoodSummaryStatistics(path, self.out("nss_b"), "v", "ALL", "INCLUDE_FOCAL", "IGNORE_NULLS",
                                                "NUMBER_OF_NEIGHBORS", None, 2, None, "BISQUARE")

    def test_neighborhood_summary_statistics_on_a_delaunay_triangulation(self):
        # A(0,0), B(10,0), C(5,8), D(5,−3). The circle through A, B, C has centre (5, 39/16) and r² = 25 + (39/16)²
        # ≈ 30.94; D is (−3 − 39/16)² ≈ 29.57 from its centre's height, inside it, so ABC is not a Delaunay triangle and
        # the diagonal is CD: A's neighbours are C and D, not B. Values 1, 2, 4, 8: A's mean, focal excluded, is 6.
        path = self.points("tri", [(0, 0), (10, 0), (5, 8), (5, -3)], [[1.0, 2.0, 4.0, 8.0]])
        out = stats.NeighborhoodSummaryStatistics(path, self.out("tri_out"), "v", "MEAN", "EXCLUDE_FOCAL").getOutput(0)
        self.assertEqual(self.rows(out, ["MEAN_v", "NUM_NBRS_v"])[0], (6.0, 2))
        # The corners of a square lie on one circle: either diagonal is a Delaunay triangulation.
        square = self.points("sq", [(0, 0), (10, 0), (0, 10), (10, 10)], [[1.0, 2.0, 3.0, 4.0]])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "not unique"):
            stats.NeighborhoodSummaryStatistics(square, self.out("sq_out"), "v")

    def test_similarity_search_by_standardized_values_and_profiles(self):
        # Target T (a=0, b=0); candidates C1 (1, 0), C2 (2, 2), C3 (4, 0). Over all four: a has mean 1.75 and
        # population variance (1.75² + .75² + .25² + 2.25²)/4 = 2.1875; b mean .5, variance (3·.25 + 2.25)/4 = .75.
        # SIMINDEX = Δa²/2.1875 + Δb²/.75: C1 1/2.1875; C2 4/2.1875 + 4/.75; C3 16/2.1875 — so C1, C2, C3.
        target = self.points("target", [(0, 0)], [[0.0], [0.0]], more=[("b", "DOUBLE")])
        candidates = self.points("cands", [(10, 0), (20, 0), (30, 0)], [[1.0, 2.0, 4.0], [0.0, 2.0, 0.0]],
                                 more=[("b", "DOUBLE")])
        out = stats.SimilaritySearch(target, candidates, self.out("sim"), "NO_COLLAPSE", "MOST_SIMILAR",
                                     "ATTRIBUTE_VALUES", 2, ["v", "b"]).getOutput(0)
        got = self.rows(out, ["MATCH_ID", "CAND_ID", "SIMRANK", "SIMINDEX"])
        self.assertEqual([r[:3] for r in got], [(1, None, None), (None, 1, 1), (None, 2, 2)])
        self.assertAlmostEqual(got[1][3], 1 / 2.1875)
        self.assertAlmostEqual(got[2][3], 4 / 2.1875 + 4 / 0.75)
        both = stats.SimilaritySearch(target, candidates, self.out("both"), "NO_COLLAPSE", "BOTH", "ATTRIBUTE_VALUES",
                                      10, ["v", "b"]).getOutput(0)
        # Three candidates give at most one each way: C1 most similar, C3 least.
        self.assertEqual(self.rows(both, ["CAND_ID", "SIMRANK", "DSIMRANK"])[1:], [(1, 1, None), (3, None, 1)])
        # Profiles: standardized target (−1.75/√2.1875, −.5/√.75) and C1 (−.75/√2.1875, −.5/√.75); cosine of the two.
        profiles = stats.SimilaritySearch(target, candidates, self.out("prof"), "NO_COLLAPSE", "MOST_SIMILAR",
                                          "ATTRIBUTE_PROFILES", 0, ["v", "b"]).getOutput(0)
        t = (-1.75 / math.sqrt(2.1875), -0.5 / math.sqrt(0.75))
        c1 = (-0.75 / math.sqrt(2.1875), -0.5 / math.sqrt(0.75))
        cosine = (t[0] * c1[0] + t[1] * c1[1]) / (math.hypot(*t) * math.hypot(*c1))
        ranked = {r[0]: r[1] for r in self.rows(profiles, ["CAND_ID", "SIMINDEX"])[1:]}
        self.assertAlmostEqual(ranked[1], cosine)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "RANKED_ATTRIBUTE_VALUES"):
            stats.SimilaritySearch(target, candidates, self.out("r"), "NO_COLLAPSE", "MOST_SIMILAR",
                                   "RANKED_ATTRIBUTE_VALUES", 2, ["v", "b"])

    def test_spatial_association_between_zones_is_the_v_measure(self):
        # Input zones a = [0,10]×[0,10], b = [10,20]×[0,10]; overlay x = [0,15]×[0,10], y = [15,20]×[0,10].
        # Overlaps: a∩x 100, b∩x 50, b∩y 50 of 200: p = .5, .25, .25; p_a = p_b = .5; p_x = .75, p_y = .25.
        # H(O) = −(.75 ln .75 + .25 ln .25); H(I) = ln 2; H(O|I) = −(.25 ln .5 + .25 ln .5) = .5 ln 2;
        # H(I|O) = −(.5 ln(.5/.75) + .25 ln(.25/.75)). V is the harmonic mean of 1 − H(O|I)/H(O) and 1 − H(I|O)/H(I).
        def box(x0, x1):
            return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, 0), arcpy.Point(x1, 0), arcpy.Point(x1, 10),
                                              arcpy.Point(x0, 10)]), 3857)
        zones = self.make("zones", "POLYGON", [("zone", "TEXT")], [("a", box(0, 10)), ("b", box(10, 20))])
        overlay = self.make("over", "POLYGON", [("kind", "TEXT")], [("x", box(0, 15)), ("y", box(15, 20))])
        result = stats.SpatialAssociationBetweenZones(zones, "zone", overlay, "kind")
        h_o = -(0.75 * math.log(0.75) + 0.25 * math.log(0.25))
        h_i = math.log(2)
        overlay_in_input = 1 - 0.5 * math.log(2) / h_o
        input_in_overlay = 1 - (-(0.5 * math.log(0.5 / 0.75) + 0.25 * math.log(0.25 / 0.75))) / h_i
        self.assertAlmostEqual(result[5], overlay_in_input)
        self.assertAlmostEqual(result[6], input_in_overlay)
        self.assertAlmostEqual(result[4], 2 * overlay_in_input * input_in_overlay / (overlay_in_input + input_in_overlay))
        # The same zones against themselves correspond perfectly.
        self.assertAlmostEqual(stats.SpatialAssociationBetweenZones(zones, "zone", zones, "zone")[4], 1.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "output_features"):
            stats.SpatialAssociationBetweenZones(zones, "zone", overlay, "kind", self.out("sab"))

    def test_spatial_outlier_detection_by_local_outlier_factor(self):
        # Points at x = 0, 1, 2, 3, 10 with k = 2. k-distances 2, 1, 1, 2, 8. Each of the first four has two
        # reachability distances 1 and 2 (e.g. for 0: max(kd(1)=1, 1), max(kd(2)=1, 2)) → lrd 1/1.5 = 2/3; the point at
        # 10 has max(2, 7) and max(1, 8) → lrd 1/7.5. LOF: 1 for the first four, (2/3)/(2/15) = 5 for the last.
        # 20 percent of 5 points is one: the threshold is the highest LOF, 5.
        path = self.points("lof", [(0, 0), (1, 0), (2, 0), (3, 0), (10, 0)])
        out = stats.SpatialOutlierDetection(path, self.out("lof_out"), 2, 20, None, "LOCAL").getOutput(0)
        got = self.rows(out, ["LOF", "IS_OUTLIER"])
        for (lof, flag), (want, outlier) in zip(got, [(1, 0), (1, 0), (1, 0), (1, 0), (5, 1)]):
            self.assertAlmostEqual(lof, want)
            self.assertEqual(flag, outlier)
        only = stats.SpatialOutlierDetection(path, self.out("lof_only"), 2, 20, None, "LOCAL", keep_type="KEEP_OUTLIER")
        self.assertEqual(self.rows(only.getOutput(0), ["SHAPE@XY"]), [((10.0, 0.0),)])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "whole"):
            stats.SpatialOutlierDetection(path, self.out("x"), 2, 30, None, "LOCAL")
        # GLOBAL: points at 0, 1, 3, 6, 10, 15, 40 have nearest-neighbour distances 1, 1, 2, 3, 4, 5, 25. Quartiles by
        # definition 7: Q1 at position 1.5 is 1.5, Q3 at 4.5 is 4.5; IQR 3. MEDIUM fence 4.5 + 1.5·3 = 9: only 25.
        spread = self.points("spread", [(x, 0) for x in (0, 1, 3, 6, 10, 15, 40)])
        out = stats.SpatialOutlierDetection(spread, self.out("g")).getOutput(0)
        got = self.rows(out, ["NEIGHBOR_DIST", "IS_OUTLIER"])
        self.assertEqual(got, [(1.0, 0), (1.0, 0), (2.0, 0), (3.0, 0), (4.0, 0), (5.0, 0), (25.0, 1)])

    # ---- equivalents (ADR-224 §5.6)

    def test_ordinary_least_squares_by_the_textbook(self):
        # x = 0..4, y = 1, 3, 4, 8, 9. x̄ = 2, ȳ = 5; Sxy = 8 + 2 + 0 + 3 + 8 = 21, Sxx = 10: β1 = 2.1, β0 = .8.
        # Fitted .8, 2.9, 5, 7.1, 9.2; residuals .2, .1, −1, .9, −.2; RSS 1.9; TSS 46; σ² = 1.9/3; SE(β1) = √(σ²/10).
        # White HC0 for the slope: Σ(x−x̄)²e²/Sxx² = (4·.04 + .01 + .81 + 4·.04)/100 = .0114.
        # Koenker: e² = .04, .01, 1, .81, .04 on x has slope S(x,e²)/Sxx = .8/10 = .08, explained .064 of .9374: n·R².
        # Jarque–Bera: m2 = .38, m3 = −.27/5, m4 = 1.6594/5; S = m3/m2^1.5, K = m4/m2²; JB = 5/6·(S² + (K−3)²/4).
        # AICc = 5 ln(2π·1.9/5) + 5·7/(5 − 2 − 2).
        path = self.points("ols", [(10 * i, 0) for i in range(5)], [[1.0, 3.0, 4.0, 8.0, 9.0], [0.0, 1, 2, 3, 4],
                                                                     [1, 2, 3, 4, 5]],
                           more=[("x", "DOUBLE"), ("uid", "LONG")])
        result = stats.OrdinaryLeastSquares(path, "uid", self.out("ols_out"), "v", "x", self.out("coef"),
                                            self.out("diag"))
        coef = {r[0]: r[1:] for r in self.rows(result[1], ["Variable", "Coef", "StdError", "Robust_SE"])}
        self.assertAlmostEqual(coef["Intercept"][0], 0.8)
        self.assertAlmostEqual(coef["x"][0], 2.1)
        self.assertAlmostEqual(coef["x"][1], math.sqrt(1.9 / 3 / 10))
        self.assertAlmostEqual(coef["x"][2], math.sqrt(0.0114))
        diag = dict(self.rows(result[2], ["Diag_Name", "Diag_Value"]))
        self.assertAlmostEqual(diag["R2"], 1 - 1.9 / 46)
        self.assertAlmostEqual(diag["K(BP)"], 5 * 0.064 / 0.9374)
        m2, m3, m4 = 0.38, -0.27 / 5, 1.6594 / 5
        self.assertAlmostEqual(diag["JB"], 5 / 6 * ((m3 / m2 ** 1.5) ** 2 + (m4 / m2 ** 2 - 3) ** 2 / 4))
        self.assertAlmostEqual(diag["AICc"], 5 * math.log(2 * math.pi * 1.9 / 5) + 35)
        residuals = [r[0] for r in self.rows(result[0], ["Residual"])]
        for got, want in zip(residuals, [0.2, 0.1, -1.0, 0.9, -0.2]):
            self.assertAlmostEqual(got, want)
        self.assertIn("equivalent", stats.OrdinaryLeastSquares.__grapy_equivalent__ + " equivalent")

    def test_generalized_linear_regression_logit_and_poisson(self):
        # Logistic: at x = 0 one of four is 1, at x = 1 three of four. The MLE fits the two proportions exactly:
        # β0 = logit(1/4) = −ln 3, β1 = logit(3/4) − logit(1/4) = 2 ln 3.
        xs = [0, 0, 0, 0, 1, 1, 1, 1]
        binary = self.points("bin", [(i * 10, 0) for i in range(8)], [[1.0, 0, 0, 0, 1, 1, 1, 0], xs],
                             more=[("x", "DOUBLE")])
        result = stats.GeneralizedLinearRegression(binary, "v", "BINARY", self.out("glr_b"), "x")
        self.assertAlmostEqual(result[1], -math.log(3), places=8)
        self.assertAlmostEqual(result[2], 2 * math.log(3), places=8)
        # Poisson: counts 1, 3 at x = 0 (mean 2) and 4, 6 at x = 1 (mean 5): β0 = ln 2, β1 = ln(5/2).
        counts = self.points("cnt", [(i * 10, 0) for i in range(4)], [[1.0, 3, 4, 6], [0.0, 0, 1, 1]],
                             more=[("x", "DOUBLE")])
        result = stats.GeneralizedLinearRegression(counts, "v", "COUNT", self.out("glr_c"), "x")
        self.assertAlmostEqual(result[1], math.log(2), places=8)
        self.assertAlmostEqual(result[2], math.log(2.5), places=8)
        predicted = [r[0] for r in self.rows(result[0], ["PREDICTED"])]
        for got, want in zip(predicted, [2, 2, 5, 5]):
            self.assertAlmostEqual(got, want, places=8)

    def test_gwr_local_fits_with_a_bisquare_band(self):
        # Points at x = 0..4 with v = x and y = v². Bisquare band 2: weights (1 − (d/2)²)² — 1 at 0, .5625 at 1, 0 at 2.
        # At 0: points 0 and 1 only — the line through (0,0) and (1,1): intercept 0, slope 1.
        # At 1: points 0, 1, 2 weighted .5625, 1, .5625; weighted means v̄ = 1, ȳ = 3.25/2.125; S_vy = .5625(1.529…+2.470…)
        #   = 2.25, S_vv = 1.125: slope 2, intercept 3.25/2.125 − 2.
        # At 2: points 1, 2, 3, ȳ = 9.625/2.125, S_vy = .5625·8 = 4.5: slope 4, intercept 9.625/2.125 − 8.
        # At 4: points 3 and 4: slope 7, intercept −12.
        path = self.points("gwr", [(i, 0) for i in range(5)], [[0.0, 1, 4, 9, 16], [0.0, 1, 2, 3, 4]],
                           more=[("x", "DOUBLE")])
        out = stats.GWR(path, "v", "CONTINUOUS", "x", self.out("gwr_out"), "DISTANCE_BAND", "USER_DEFINED",
                        distance_band=2).getOutput(0)
        got = self.rows(out, ["INTERCEPT", "C_x", "PREDICTED"])
        for (intercept, slope, _), (want_i, want_s) in zip(got, [(0, 1), (3.25 / 2.125 - 2, 2), (9.625 / 2.125 - 8, 4),
                                                                  (None, None), (-12, 7)]):
            if want_i is None:
                continue
            self.assertAlmostEqual(intercept, want_i)
            self.assertAlmostEqual(slope, want_s)
        self.assertAlmostEqual(got[2][2], 9.625 / 2.125)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "model_type"):
            stats.GWR(path, "v", "COUNT", "x", self.out("g2"), "DISTANCE_BAND", "USER_DEFINED", distance_band=2)

    def test_incremental_spatial_autocorrelation(self):
        # Six points 10 apart holding 1, 3, 2, 5, 4, 6: deviations −2.5, −.5, −1.5, 1.5, .5, 2.5, Σz² = 17.5. At 10,
        # row-standardized: Σ z_i Σ w_ij z_j = 1.25 + 1 − .75 − .75 + 1 + 1.25 = 3, S0 = 6, so I = 3/17.5.
        path = self.points("isa", [(10 * i, 0) for i in range(6)], [[1.0, 3, 2, 5, 4, 6]])
        result = stats.IncrementalSpatialAutocorrelation(path, "v", 4, 10, 10, Output_Table=self.out("isa_t"))
        rows = self.rows(result[0], ["Distance", "MoransI", "ZScore"])
        self.assertEqual([r[0] for r in rows], [10.0, 20.0, 30.0, 40.0])
        self.assertAlmostEqual(rows[0][1], 3 / 17.5)
        # The first peak is the first band whose z-score is above both neighbours'; it is read as a distance.
        peak = next(r[0] for a, r, b in zip(rows, rows[1:], rows[2:]) if r[2] > a[2] and r[2] > b[2])
        self.assertEqual((result[2], result[3]), (peak, peak))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "Distance_Increment"):
            stats.IncrementalSpatialAutocorrelation(path, "v", 4)

    def test_multivariate_clustering_is_k_means(self):
        # Values 0, 1, 10, 11: two clusters are {0, 1} and {10, 11} whatever the seeds. Pseudo F for two clusters:
        # SSW = .5 + .5 = 1, SSB = 101 − 1 = 100 (mean 5.5): (100/1)/(1/2) = 200 — unchanged by scaling. For three:
        # the best split leaves a pair, SSW = .5, SSB = 100.5: (100.5/2)/(.5/1) = 100.5. Two wins.
        path = self.points("mc", [(10 * i, 0) for i in range(4)], [[0.0, 1, 10, 11]])
        result = stats.MultivariateClustering(path, self.out("mc_out"), ["v"], output_table=self.out("mc_t"))
        self.assertEqual([r[0] for r in self.rows(result[0], ["CLUSTER_ID"])], [1, 1, 2, 2])
        table = self.rows(result[1], ["NUM_CLUSTERS", "PSEUDO_F"])
        self.assertEqual(table[0][0], 2)
        self.assertAlmostEqual(table[0][1], 200.0)
        self.assertAlmostEqual(table[1][1], 100.5)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "K_MEDOIDS"):
            stats.MultivariateClustering(path, self.out("km"), ["v"], "K_MEDOIDS")

    def test_density_based_clustering_is_dbscan(self):
        # x = 0, 1, 2, 10, 11, 12, 30; three points within 1 make a core: 1 and 11 are cores, 0, 2, 10 and 12 border
        # points, 30 noise.
        path = self.points("db", [(x, 0) for x in (0, 1, 2, 10, 11, 12, 30)])
        out = stats.DensityBasedClustering(path, self.out("db_out"), "DBSCAN", 3, 1).getOutput(0)
        self.assertEqual([r[0] for r in self.rows(out, ["CLUSTER_ID"])], [1, 1, 1, 2, 2, 2, -1])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "HDBSCAN"):
            stats.DensityBasedClustering(path, self.out("h"), "HDBSCAN", 3)

    def test_optimized_hot_spot_analysis_follows_its_documented_steps(self):
        # A 12 × 12 lattice of incidents 1 apart, plus five more on (0, 0). Unique points: every nearest neighbour is 1
        # away, so no locational outlier and ANN = MNN = 1. Not dense: N random points in the 11 × 11 hull lie
        # 0.5/√(144/121) ≈ 0.458 apart, and twice that is not under 11/100. So the cell is 2·max(1, 1) = 2: a 6 × 6
        # fishnet from (0, 0), four incidents a cell, nine in the first.
        xys = [(x, y) for y in range(12) for x in range(12)] + [(0, 0)] * 5
        path = self.points("incidents", xys)
        before = arcpy.GetMessageCount()
        out = stats.OptimizedHotSpotAnalysis(path, self.out("ohsa"), None, "COUNT_INCIDENTS_WITHIN_FISHNET_POLYGONS")
        said = [arcpy.GetMessage(i) for i in range(before, arcpy.GetMessageCount())]
        self.assertIn("Cell size: 2.0", said)
        counts = sorted(r[0] for r in self.rows(out[0], ["Join_Count"]))
        self.assertEqual(counts, [4] * 35 + [9])
        # With a distance band given, the analysis is HotSpots with the FDR correction on the same weighted features.
        weighted = os.path.join(arcpy.env.scratchWorkspace, "optimizedhotspotanalysis_weighted.gpkg")
        given = stats.OptimizedHotSpotAnalysis(path, self.out("ohsa2"), None, "COUNT_INCIDENTS_WITHIN_FISHNET_POLYGONS",
                                               Distance_Band=4)
        direct = stats.HotSpots(weighted, "Join_Count", self.out("direct"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE",
                                "NONE", 4, None, None, "APPLY_FDR")
        self.assertEqual(self.rows(given[0], ["GiZScore", "Gi_Bin"]), self.rows(direct[0], ["GiZScore", "Gi_Bin"]))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "SNAP_NEARBY"):
            stats.OptimizedHotSpotAnalysis(path, self.out("snap"), None, "SNAP_NEARBY_INCIDENTS_TO_CREATE_WEIGHTED_POINTS")
        outliers = stats.OptimizedOutlierAnalysis(path, self.out("ooa"), None, "COUNT_INCIDENTS_WITHIN_FISHNET_POLYGONS",
                                                  None, None, "QUICK_199", None, 4)
        self.assertEqual(len(self.rows(outliers[0], ["LMiPValue"])), 36)

    def test_a_tool_not_built_is_refused_by_name(self):
        with self.assertRaisesRegex(arcpy.NotBuiltError, "GenerateSpatialWeightsMatrix"):
            stats.GenerateSpatialWeightsMatrix("x")


if __name__ == "__main__":
    unittest.main()
