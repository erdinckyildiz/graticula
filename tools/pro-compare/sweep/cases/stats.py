"""arcpy.stats against ArcGIS Pro 3.7: one case for each name grapy builds.

The shared fixture's 30 points are too few for the regressions and clusterings, so this module builds its own: ``sp``,
60 points with a dependent ``y`` that trends across the square and two explanatory fields; ``sz``, a 6 × 6 grid of
100 m squares with the same fields; and ``ts``, three locations observed on ten days. All from the fixtures' linear
congruential sequence, so both sides see the same numbers.

Permutation p-values are random on both sides (Pro's draws are not grapy's), so the cases set env.randomGenerator on
both and the results file says which fields differ for that reason alone.
"""
import datetime
import math
import os

CASES = {}


def case(name):
    def register(function):
        CASES[name] = function
        return function
    return register


def _lcg(seed):
    state = seed
    while True:
        state = (1103515245 * state + 12345) % 2 ** 31
        yield state / 2 ** 31


def _fc(ap, I, name, shape, fields, rows):
    path = os.path.join(I["gdb"], name)
    if ap.Exists(path):
        return path
    sr = ap.SpatialReference(I["srid"])
    ap.management.CreateFeatureclass(I["gdb"], name, shape, spatial_reference=sr)
    for field, kind in fields:
        ap.management.AddField(path, field, kind)
    with ap.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
        for *values, wkt in rows:
            cursor.insertRow((*values, ap.FromWKT(wkt, sr)))
    return path


FIELDS = [("sid", "LONG"), ("y", "DOUBLE"), ("x1", "DOUBLE"), ("x2", "DOUBLE"), ("cnt", "LONG"), ("pop", "LONG"),
          ("grp", "TEXT"), ("hit", "SHORT")]


def _values(i, x, y, r):
    """The attributes of feature i at local (x, y)."""
    x1 = round(10 * next(r), 4)
    x2 = round(5 + 0.01 * x + 3 * next(r), 4)
    noise = next(r) - 0.5
    yv = round(2 + 0.004 * x + 0.006 * y + 0.8 * x1 - 0.5 * x2 + 1.5 * noise, 4)
    pop = 500 + int(2000 * next(r))
    cnt = int(pop * (0.01 + 0.00002 * x) + 5 * next(r))
    grp = "a" if (x < 300) == (y < 300) else "b"
    hit = 1 if yv > 7 else 0
    return (i, yv, x1, x2, cnt, pop, grp, hit)


def _sp(ap, I):
    r = _lcg(777)
    rows = []
    for i in range(1, 61):
        x, y = 15 + 570 * next(r), 15 + 570 * next(r)
        rows.append((*_values(i, x, y, r), f"POINT ({I['x0'] + round(x, 3)} {I['y0'] + round(y, 3)})"))
    return _fc(ap, I, "sp", "POINT", FIELDS, rows)


def _sz(ap, I):
    r = _lcg(4242)
    rows = []
    for row in range(6):
        for col in range(6):
            x, y = col * 100, row * 100
            ring = [(x, y), (x, y + 100), (x + 100, y + 100), (x + 100, y), (x, y)]
            wkt = "POLYGON ((" + ", ".join(f"{I['x0'] + a} {I['y0'] + b}" for a, b in ring) + "))"
            rows.append((*_values(row * 6 + col + 1, x + 50, y + 50, r), wkt))
    return _fc(ap, I, "sz", "POLYGON", FIELDS, rows)


def _ts(ap, I):
    """Three locations, ten daily values each."""
    r = _lcg(99)
    rows = []
    start = datetime.datetime(2026, 1, 1)
    n = 0
    for k, (x, y) in enumerate(((100, 100), (300, 400), (500, 200))):
        for d in range(10):
            n += 1
            rows.append((n, k + 1, start + datetime.timedelta(days=d), round(10 * k + d + 5 * next(r), 3),
                         f"POINT ({I['x0'] + x} {I['y0'] + y})"))
    return _fc(ap, I, "ts", "POINT", [("tid", "LONG"), ("loc", "LONG"), ("t", "DATE"), ("v", "DOUBLE")], rows)


def _quarters(ap, I):
    """The fixture square in four 300 m quarters, zone 1..4, to overlay on sz's two groups."""
    rows = []
    for n, (dx, dy) in enumerate(((0, 0), (300, 0), (0, 300), (300, 300)), start=1):
        x, y = I["x0"] + dx, I["y0"] + dy
        rows.append((n, f"q{n}", f"POLYGON (({x} {y}, {x} {y + 300}, {x + 300} {y + 300}, {x + 300} {y}, {x} {y}))"))
    return _fc(ap, I, "quarters", "POLYGON", [("qid", "LONG"), ("zone", "TEXT")], rows)


def _seed(ap):
    ap.env.randomGenerator = "1 MERSENNE_TWISTER"


def _values_of(result, rounding=6):
    """A result's outputs, numbers rounded, paths dropped (the harness drops them too)."""
    out = []
    for i in range(result.outputCount):
        v = result.getOutput(i)
        try:
            out.append(round(float(v), rounding))
        except (TypeError, ValueError):
            out.append(str(v))
    return out


@case("stats.AverageNearestNeighbor")
def _(ap, I, O):
    return _values_of(ap.stats.AverageNearestNeighbor(_sp(ap, I), "EUCLIDEAN_DISTANCE", "NO_REPORT"))


@case("stats.BivariateSpatialAssociation")
def _(ap, I, O):
    _seed(ap)
    return ap.stats.BivariateSpatialAssociation(_sp(ap, I), "x1", "y", O("out"), "K_NEAREST_NEIGHBORS",
                                                num_neighbors=8, num_permutations=99)


@case("stats.CalculateAreas")
def _(ap, I, O):
    return ap.stats.CalculateAreas(I["polys"], O("out"))


@case("stats.CalculateCompositeIndex")
def _(ap, I, O):
    return ap.stats.CalculateCompositeIndex(_sz(ap, I), [["x1", False], ["x2", True]], "NEW_FEATURES",
                                            O("out"), "MEAN_SCALED")


@case("stats.CalculateDistanceBand")
def _(ap, I, O):
    return _values_of(ap.stats.CalculateDistanceBand(_sp(ap, I), 3, "EUCLIDEAN_DISTANCE"))


@case("stats.CalculateRates")
def _(ap, I, O):
    return ap.stats.CalculateRates(_sp(ap, I), [["cnt", "pop"]], "NO_APPEND", O("out"), "GLOBAL_EMPIRICAL_BAYES",
                                   rate_multiplier=1000)


@case("stats.CentralFeature")
def _(ap, I, O):
    return ap.stats.CentralFeature(_sp(ap, I), O("out"), "EUCLIDEAN_DISTANCE")


@case("stats.ClustersOutliers")
def _(ap, I, O):
    return ap.stats.ClustersOutliers(_sp(ap, I), "y", O("out"), "INVERSE_DISTANCE", "EUCLIDEAN_DISTANCE", "ROW",
                                     None, None, "NO_FDR", 0)


@case("stats.CollectEvents")
def _(ap, I, O):
    events = _fc(ap, I, "events", "POINT", [("eid", "LONG")], [
        (n, f"POINT ({I['x0'] + x} {I['y0'] + y})")
        for n, (x, y) in enumerate([(100, 100), (100, 100), (200, 50), (100, 100), (200, 50), (400, 400)], start=1)])
    return ap.stats.CollectEvents(events, O("out"))


@case("stats.ColocationAnalysis")
def _(ap, I, O):
    _seed(ap)
    return ap.stats.ColocationAnalysis("SINGLE_DATASET", _sp(ap, I), O("out"), "grp", None, "a", None, None, None, "b",
                                       "K_NEAREST_NEIGHBORS", 8, number_of_permutations=99)


@case("stats.DecomposeSpatialStructure")
def _(ap, I, O):
    return ap.stats.DecomposeSpatialStructure(_sp(ap, I), O("out"), "ALL", 0.25, 5, "K_NEAREST_NEIGHBORS",
                                              number_of_neighbors=8)


@case("stats.DensityBasedClustering")
def _(ap, I, O):
    return ap.stats.DensityBasedClustering(_sp(ap, I), O("out"), "DBSCAN", 3, "70 Meters")


@case("stats.DimensionReduction")
def _(ap, I, O):
    return ap.stats.DimensionReduction(_sz(ap, I), O("out"), ["x1", "x2", "y"], "PCA", "SCALE_DATA", None, None, 2)


@case("stats.DirectionalDistribution")
def _(ap, I, O):
    return ap.stats.DirectionalDistribution(_sp(ap, I), O("out"), "1_STANDARD_DEVIATION")


@case("stats.DirectionalMean")
def _(ap, I, O):
    return ap.stats.DirectionalMean(I["lines"], O("out"), "DIRECTION")


@case("stats.ExportXYv")
def _(ap, I, O):
    path = os.path.join(I["work"], "xyv.txt")     # not O(): a text file is neither side's dataset
    ap.stats.ExportXYv(_sp(ap, I), ["y", "cnt", "grp"], "COMMA", path, "ADD_FIELD_NAMES")
    return open(path, encoding="utf-8").read().splitlines()


@case("stats.GWR")
def _(ap, I, O):
    return ap.stats.GWR(_sp(ap, I), "y", "CONTINUOUS", ["x1", "x2"], O("out"), "NUMBER_OF_NEIGHBORS", "USER_DEFINED",
                        number_of_neighbors=25)


@case("stats.GeneralizedLinearRegression")
def _(ap, I, O):
    return ap.stats.GeneralizedLinearRegression(_sp(ap, I), "y", "CONTINUOUS", O("out"), ["x1", "x2"])


@case("stats.GeographicallyWeightedRegression")
def _(ap, I, O):
    return ap.stats.GeographicallyWeightedRegression(_sp(ap, I), "y", ["x1", "x2"], O("out"), "ADAPTIVE",
                                                     "BANDWIDTH_PARAMETER", None, 25)


@case("stats.HighLowClustering")
def _(ap, I, O):
    return _values_of(ap.stats.HighLowClustering(_sp(ap, I), "cnt", "NO_REPORT", "FIXED_DISTANCE_BAND",
                                                 "EUCLIDEAN_DISTANCE", "NONE", 150))


@case("stats.HotSpotAnalysisComparison")
def _(ap, I, O):
    _seed(ap)
    sp = _sp(ap, I)
    one = ap.stats.HotSpots(sp, "y", O("hs1"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE", 150)[0]
    two = ap.stats.HotSpots(sp, "x2", O("hs2"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE", 150)[0]
    return ap.stats.HotSpotAnalysisComparison(one, two, O("out"), 8, 99)


@case("stats.HotSpots")
def _(ap, I, O):
    return ap.stats.HotSpots(_sp(ap, I), "y", O("out"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE", 150)


@case("stats.IncrementalSpatialAutocorrelation")
def _(ap, I, O):
    return ap.stats.IncrementalSpatialAutocorrelation(_sp(ap, I), "y", 5, 80, 30, "EUCLIDEAN", "ROW_STANDARDIZATION",
                                                      O("table"))


@case("stats.LocalBivariateRelationships")
def _(ap, I, O):
    _seed(ap)
    return ap.stats.LocalBivariateRelationships(_sp(ap, I), "y", "x1", O("out"), 30, 99, "NO_POPUP")


@case("stats.MGWR")
def _(ap, I, O):
    return ap.stats.MGWR(_sp(ap, I), "y", "CONTINUOUS", ["x1", "x2"], O("out"), "NUMBER_OF_NEIGHBORS", "GOLDEN_SEARCH")


@case("stats.MeanCenter")
def _(ap, I, O):
    return ap.stats.MeanCenter(_sp(ap, I), O("out"), None, "grp")


@case("stats.MedianCenter")
def _(ap, I, O):
    return ap.stats.MedianCenter(_sp(ap, I), O("out"))


@case("stats.MultiDistanceSpatialClustering")
def _(ap, I, O):
    return ap.stats.MultiDistanceSpatialClustering(_sp(ap, I), O("table"), 5,
                                                   "0_PERMUTATIONS_-_NO_CONFIDENCE_ENVELOPE", "NO_DISPLAY", None, 50,
                                                   25)


@case("stats.MultivariateClustering")
def _(ap, I, O):
    _seed(ap)
    return ap.stats.MultivariateClustering(_sz(ap, I), O("out"), ["x1", "x2"], "K_MEANS", "OPTIMIZED_SEED_LOCATIONS",
                                           None, 3)


@case("stats.NeighborhoodSummaryStatistics")
def _(ap, I, O):
    return ap.stats.NeighborhoodSummaryStatistics(_sp(ap, I), O("out"), ["y"], "ALL", "INCLUDE_FOCAL", "IGNORE_NULLS",
                                                  "NUMBER_OF_NEIGHBORS", None, 6)


@case("stats.OptimizedHotSpotAnalysis")
def _(ap, I, O):
    return ap.stats.OptimizedHotSpotAnalysis(_sp(ap, I), O("out"), "y")


@case("stats.OptimizedOutlierAnalysis")
def _(ap, I, O):
    _seed(ap)
    return ap.stats.OptimizedOutlierAnalysis(_sp(ap, I), O("out"), "y")


@case("stats.OrdinaryLeastSquares")
def _(ap, I, O):
    return ap.stats.OrdinaryLeastSquares(_sp(ap, I), "sid", O("out"), "y", ["x1", "x2"], O("coef"), O("diag"))


@case("stats.PrepareData")
def _(ap, I, O):
    _seed(ap)
    return ap.stats.PrepareData(_sp(ap, I), O("out"), "NONE", None, "y", "NUMERIC", [["x1", "false"], ["x2", "false"]])


@case("stats.SAR")
def _(ap, I, O):
    return ap.stats.SAR(_sp(ap, I), "y", ["x1", "x2"], O("out"), "AUTO", "K_NEAREST_NEIGHBORS", number_of_neighbors=8)


@case("stats.SimilaritySearch")
def _(ap, I, O):
    sp = _sp(ap, I)
    target = ap.analysis.Select(sp, O("target"), "sid = 1")[0]
    candidates = ap.analysis.Select(sp, O("candidates"), "sid > 1")[0]
    return ap.stats.SimilaritySearch(target, candidates, O("out"), "NO_COLLAPSE", "MOST_SIMILAR", "ATTRIBUTE_VALUES", 5,
                                     ["x1", "x2"], ["sid"])


@case("stats.SpatialAssociationBetweenZones")
def _(ap, I, O):
    return _values_of(ap.stats.SpatialAssociationBetweenZones(_sz(ap, I), "grp", _quarters(ap, I), "zone"))


@case("stats.SpatialAutocorrelation")
def _(ap, I, O):
    return _values_of(ap.stats.SpatialAutocorrelation(_sp(ap, I), "y", "NO_REPORT", "INVERSE_DISTANCE",
                                                      "EUCLIDEAN_DISTANCE", "ROW"))


@case("stats.SpatialOutlierDetection")
def _(ap, I, O):
    return ap.stats.SpatialOutlierDetection(_sp(ap, I), O("out"), 5, 5, None, "LOCAL")


@case("stats.SpatiallyConstrainedMultivariateClustering")
def _(ap, I, O):
    _seed(ap)
    return ap.stats.SpatiallyConstrainedMultivariateClustering(_sz(ap, I), O("out"), ["x1", "x2"], "NONE", None, None,
                                                               None, 3, "CONTIGUITY_EDGES_ONLY")


@case("stats.StandardDistance")
def _(ap, I, O):
    return ap.stats.StandardDistance(_sp(ap, I), O("out"), "1_STANDARD_DEVIATION")


@case("stats.TimeSeriesSmoothing")
def _(ap, I, O):
    return ap.stats.TimeSeriesSmoothing(_ts(ap, I), "t", "v", "LOCATION", "BACKWARD", "3 Days", "NEW_OUTPUT",
                                        O("out"))


@case("stats.EvaluateBinSizes")
def _(ap, I, O):
    _seed(ap)
    return ap.stats.EvaluateBinSizes(_sp(ap, I), O("out"), O("charts"), O("boundary"), "HEXAGON", "CONVEX_HULL")
