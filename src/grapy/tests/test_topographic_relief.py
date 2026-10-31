"""arcpy.topographic's relief and shape tools, against hand-worked answers and published test values (ADR-251).

The World Magnetic Model is checked against NOAA NCEI's own WMM2025 test values (WMM2025_TEST_VALUES.txt, the
"Field" columns: date, height km, latitude, longitude, X, Y, Z, H, F, I, D), which the model's report publishes so an
implementation can be checked. The rasters are 10 m cells in EPSG:32633 written here with values the comments give.
"""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import numpy as np  # noqa: E402
import shapely  # noqa: E402
from shapely.geometry import LineString, Point, Polygon  # noqa: E402

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _layers  # noqa: E402
from grapy import _topo_prod_wmm as wmm  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

UTM = 32633


def square(x0, y0, x1, y1):
    return Polygon([(x0, y0), (x1, y0), (x1, y1), (x0, y1)])


def circle(x, y, r, n=64):
    return LineString([(x + r * np.cos(t), y + r * np.sin(t)) for t in np.linspace(0, 2 * np.pi, n + 1)[:-1]] +
                      [(x + r, y)])


class Fixture(unittest.TestCase):
    def setUp(self):
        _layers.LAYERS.clear()
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "relief")[0]

    def fc(self, name, kind, fields=(), rows=(), srid=UTM, container=None):
        path = arcpy.management.CreateFeatureclass(container or self.gpkg, name, kind, spatial_reference=srid)[0]
        for field, field_type in fields:
            arcpy.management.AddField(path, field, field_type)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for row in rows:
                cursor.insertRow(list(row))
        return path

    def raster(self, name, array, left=0.0, top=None, cell=10.0):
        import rasterio
        from rasterio.transform import from_origin
        array = np.asarray(array, dtype="float32")
        top = array.shape[0] * cell if top is None else top
        path = os.path.join(self.directory, name + ".tif")
        with rasterio.open(path, "w", driver="GTiff", width=array.shape[1], height=array.shape[0], count=1,
                           dtype="float32", crs=f"EPSG:{UTM}", transform=from_origin(left, top, cell, cell),
                           nodata=-9999) as out:
            out.write(array, 1)
        return path

    def values(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as cursor:
            return [tuple(r) for r in cursor]

    def shapes(self, path, fields=()):
        with arcpy.da.SearchCursor(path, [*fields, "SHAPE@WKB"]) as cursor:
            return [(*r[:-1], shapely.from_wkb(bytes(r[-1]))) for r in cursor]


class WorldMagneticModel(unittest.TestCase):
    # NOAA NCEI, WMM2025_TEST_VALUES.txt: date, height (km), latitude, longitude, X, Y, Z, H, F, I, D, Ddot.
    VALUES = ((2025.0, 0, 80, 0, 6521.6, 145.9, 54791.5, 6523.2, 55178.5, 83.21, 1.28, 0.52),
              (2025.0, 0, 0, 120, 39677.8, -109.6, -10580.2, 39677.9, 41064.3, -14.93, -0.16, -0.03),
              (2025.0, 0, -80, 240, 6117.5, 15751.9, -52022.5, 16898.1, 54698.2, -72.00, 68.78, -0.12),
              (2025.0, 100, 80, 0, 6216.0, 92.4, 52598.8, 6216.7, 52964.9, 83.26, 0.85, 0.52),
              (2027.5, 0, 0, 120, 39701.6, -167.4, -10381.8, 39702.0, 41036.9, -14.65, -0.24, -0.03),
              (2027.5, 100, -80, 240, 5984.0, 14760.1, -49317.7, 15927.0, 51825.7, -72.10, 67.93, -0.11))

    def test_noaas_published_test_values(self):
        for year, height, lat, lon, x, y, z, h, f, i, d, ddot in self.VALUES:
            found = wmm.field(wmm.model_for(year), lat, lon, height, year)
            for name, expected in (("X", x), ("Y", y), ("Z", z), ("H", h), ("F", f)):
                self.assertAlmostEqual(found[name], expected, delta=0.06, msg=f"{name} at {year} {lat} {lon}")
            for name, expected in (("I", i), ("D", d), ("Ddot", ddot)):
                self.assertAlmostEqual(found[name], expected, delta=0.006, msg=f"{name} at {year} {lat} {lon}")

    def test_the_model_follows_the_epoch(self):
        self.assertEqual(wmm.model_for(2021.3).name, "WMM-2020")
        self.assertEqual(wmm.model_for(2025.0).name, "WMM-2025")
        with self.assertRaises(ValueError):
            wmm.model_for(2031.0)


class Magnetism(Fixture):
    def test_components_at_a_point(self):
        # NOAA's first test value: 1 January 2025, 80°N 0°E, sea level: D 1.28°, X 6521.6 nT, F 55178.5 nT.
        path = self.fc("Points", "POINT", [("D", "DOUBLE"), ("X", "DOUBLE"), ("F", "DOUBLE")],
                       [(None, None, None, Point(0, 80))], srid=4326)
        arcpy.topographic.CalculateMagneticComponents(path, "0 Meters", "01/01/2025",
                                                      "DECLINATION D;NORTH_COMPONENT X;TOTAL_INTENSITY F")
        d, x, f = self.values(path, ["D", "X", "F"])[0]
        self.assertAlmostEqual(d, 1.28, delta=0.006)
        self.assertAlmostEqual(x, 6521.6, delta=0.06)
        self.assertAlmostEqual(f, 55178.5, delta=0.06)
        with self.assertRaises(ExecuteError):   # grid variation needs Lambert Conformal Conic
            arcpy.topographic.CalculateMagneticComponents(path, "0 Meters", "01/01/2025", "GRID_VARIATION D")
        with self.assertRaises(NotBuiltError):
            arcpy.topographic.CalculateMagneticComponents(path, "0 Meters", "01/01/2025", "DECLINATION D", "WMM.xml")

    def test_grid_variation_on_the_central_meridian_is_the_declination(self):
        # EPSG:3034 (ETRS89 LCC Europe) has its central meridian at 10°E, where grid north is true north.
        from pyproj import Transformer
        x, y = Transformer.from_crs(4326, 3034, always_xy=True).transform(10.0, 52.0)
        path = self.fc("Points", "POINT", [("GV", "DOUBLE"), ("D", "DOUBLE")], [(None, None, Point(x, y))], srid=3034)
        arcpy.topographic.CalculateMagneticComponents(path, "0 Meters", "07/01/2026", "GRID_VARIATION GV;DECLINATION D")
        gv, d = self.values(path, ["GV", "D"])[0]
        self.assertAlmostEqual(gv, d, delta=1e-6)

    def test_isogonic_lines_follow_the_model(self):
        area = self.fc("Extent", "POLYGON", [], [(square(0, 40, 10, 50),)], srid=4326)
        target = self.fc("Isogonic", "POLYLINE", [("DEC", "DOUBLE"), ("DRIFT", "TEXT")], srid=4326)
        arcpy.topographic.GenerateIsogonicLines(area, "0 Meters", "01/01/2025", 1, 0, target, "DEC", None,
                                                "ANNUAL_DRIFT", "DRIFT")
        found = self.shapes(target, ["DEC", "DRIFT"])
        self.assertTrue(found)
        model = wmm.model_for(2025.0)
        for level, drift, shape in found:
            self.assertEqual(level, round(level))   # whole degrees, base 0 interval 1
            middle = shape.interpolate(0.5, normalized=True)
            self.assertAlmostEqual(wmm.field(model, middle.y, middle.x, 0, 2025.0)["D"], level, delta=0.05)
            self.assertRegex(drift, r"^-?\d+\.\d\d$")


class MaximumElevationFigures(Fixture):
    def mef(self, specification, terrain_m, obstruction_ft, **options):
        cell = self.fc("Cell" + specification[:3], "POLYGON", [("MEF", "DOUBLE"), ("MAXVO", "DOUBLE"),
                                                               ("MAXT", "DOUBLE")],
                       [(None, None, None, square(0, 0, 100, 100))])
        terrain = self.fc("Terrain" + specification[:3], "POINT", [("Z", "DOUBLE")], [(terrain_m, Point(50, 50))])
        rows = [] if obstruction_ft is None else [(obstruction_ft, Point(20, 20))]
        towers = self.fc("Towers" + specification[:3], "POINT", [("TOP", "DOUBLE")], rows)
        arcpy.topographic.CalculateMEF(cell, terrain, f"{towers} TOP Feet", "MEF", "MAXVO", "MAXT", specification, "Z",
                                       **options)
        return self.values(cell, ["MEF"])[0][0]

    def test_the_published_rules(self):
        # STANAG 3591: obstruction 6300 + 25 + 50 = 6375 → 6400 (the page's example); terrain 1510 ft (460.248 m)
        # + 200 + 50 = 1760 → 1800, and the larger is the figure.
        stanag = dict(vo_allowance="200 Feet", vo_accuracy="25 Feet", terrain_accuracy="50 Feet")
        self.assertEqual(self.mef("STANAG_3591_ED6", 460.248, 6300, **stanag), 6400)
        self.assertEqual(self.mef("STANAG_3591_ED6", 460.248, None, **stanag), 1800)
        # JOG: the obstruction 2424 ft stands more than 150 ft above sea-level terrain: 2424 + 250 = 2674 → 2700.
        self.assertEqual(self.mef("JOG_MIL_J_89100", 0.0, 2424, vo_accuracy="250 Feet"), 2700)
        # ONC: terrain 9815 ft (2991.612 m) + 200 + 500 = 10515 → 10600.
        self.assertEqual(self.mef("ONC_MIL_O_89102", 2991.612, None, vo_allowance="200 Feet",
                                  terrain_accuracy="500 Feet"), 10600)
        # TPC below sea level: -150 ft (-45.72 m) + 200 + 250 = 300 exactly, not rounded up.
        self.assertEqual(self.mef("TPC_MIL_T_89101", -45.72, None, vo_allowance="200 Feet",
                                  terrain_accuracy="250 Feet"), 300)


class Contours(Fixture):
    def ramp(self):
        # Ten columns of 10 m: column c holds 10c + 5, so 5 … 95 west to east.
        return self.raster("ramp", [[10 * c + 5 for c in range(10)] for _ in range(10)])

    def test_coded_contours_of_a_ramp(self):
        # Contours at 20, 40, 60, 80 (between cell centres 5 and 95); index every 40: 40 and 80 code 1, the others 2.
        raster = self.ramp()
        aoi = self.fc("AOI", "POLYGON", [], [(square(0, 0, 100, 100),)])
        target = self.fc("Contours", "POLYLINE", [("ZV2", "DOUBLE"), ("HQC", "LONG")])
        arcpy.topographic.GenerateTopographicContours(raster, aoi, target, "ZV2", contour_interval=20, index_interval=40)
        found = sorted((z, code) for z, code, _ in self.shapes(target, ["ZV2", "HQC"]))
        self.assertEqual(found, [(20.0, 2), (40.0, 1), (60.0, 2), (80.0, 1)])
        line = [s for z, _, s in self.shapes(target, ["ZV2", "HQC"]) if z == 40.0][0]
        self.assertAlmostEqual(line.centroid.x, 40.0, delta=1e-6)   # column c's centre 10c + 5 holds 10c + 5
        with self.assertRaises(NotBuiltError):
            arcpy.topographic.GenerateTopographicContours(raster, aoi, target, "ZV2", resample_raster="RESAMPLE_RASTER")

    def bowl(self, peak):
        # A 21 × 21 raster whose cells hold their distance (in metres) from the centre cell, or 100 less it.
        r = np.hypot(*np.meshgrid(np.arange(21) - 10, np.arange(21) - 10)) * 10
        return self.raster("bowl" if not peak else "hill", 100 - r if peak else r)

    def test_contours_of_a_bowl_are_depressions(self):
        # In the bowl every closed contour has lower ground inside it. Interval 40 with half auxiliaries: 40 and 80
        # are main contours, 80 an index (index interval 80): depression index 5, 40 depression intermediate 6; 20 and
        # 60 are half auxiliaries in a depression: 22, and so is 100, the circle through the edge cells' centres.
        bowl = self.bowl(False)
        aoi = self.fc("AOI", "POLYGON", [], [(square(0, 0, 210, 210),)])
        target = self.fc("Contours", "POLYLINE", [("ZV2", "DOUBLE"), ("HQC", "LONG")])
        arcpy.topographic.GenerateTopographicContours(bowl, aoi, target, "ZV2", contour_interval=40, index_interval=80,
                                                      supplemental_contours="HALF_AUXILIARY")
        found = sorted({(z, code) for z, code, line in self.shapes(target, ["ZV2", "HQC"]) if line.geoms[0].is_closed})
        self.assertEqual(found, [(20.0, 22), (40.0, 6), (60.0, 22), (80.0, 5), (100.0, 22)])

    def test_identify_contours_finds_depressions(self):
        # The circle of radius 50 m round the centre (105, 105): in the bowl the ground inside is lower — a
        # depression, and 60 is not a multiple of 100: depression intermediate, 6. On the hill it is intermediate, 2.
        for peak, code in ((False, 6), (True, 2)):
            target = self.fc("C" + str(peak), "POLYLINE", [("Z", "DOUBLE"), ("HQC", "LONG")],
                             [(60.0, None, circle(105, 105, 50))])
            arcpy.topographic.IdentifyContours(target, self.bowl(peak), "Z", "HQC")
            self.assertEqual(self.values(target, ["HQC"]), [(code,)])

    def test_spot_heights_at_the_summit(self):
        # The hill peaks at 100 in the centre cell, whose centre is (105, 105); the circle at 60 encloses it, and
        # no contour encloses the circle, so the raster says it is a top. At 1:250,000 the default z factor turns
        # 100 m into 328.084 ft, rounded up to 330.
        hill = self.bowl(True)
        contours = self.fc("Contours", "POLYLINE", [("Z", "DOUBLE")], [(60.0, circle(105, 105, 40))])
        spots = self.fc("Spots", "POINT", [("ZV", "DOUBLE")])
        arcpy.topographic.GenerateSpotHeights(contours, hill, spots, "Z", "ZV", None)
        self.assertEqual([(z, s.x, s.y) for z, s in self.shapes(spots, ["ZV"])], [(100.0, 105.0, 105.0)])
        arcpy.topographic.GenerateSpotHeights(contours, hill, spots, "Z", "ZV", None, scale="1:250,000")
        self.assertEqual(self.values(spots, ["ZV"])[-1], (330,))

    def test_validate_spot_heights(self):
        # A top contour at 100 (interval 20) on a raster of 110 everywhere except 115 at the centre cell (105, 105):
        # 110 inside is valid but for the 115 (code 1); 95 is on the wrong side (3); 130 skips the 120 contour (2);
        # a spot outside every closed contour is 4.
        grid = np.full((21, 21), 110.0)
        grid[10, 10] = 115.0
        raster = self.raster("plateau", grid)
        contours = self.fc("Contours", "POLYLINE", [("Z", "DOUBLE")], [(100.0, circle(105, 105, 60))])
        spots = self.fc("Spots", "POINT", [("ZV", "DOUBLE")],
                        [(110.0, Point(100, 100)), (95.0, Point(110, 110)), (130.0, Point(90, 110)),
                         (100.0, Point(5, 5)), (116.0, Point(105, 105))])
        arcpy.topographic.ValidateSpotHeights(contours, "Z", 20, raster, spots, "ZV")
        self.assertEqual([v for (v,) in self.values(spots, ["IS_VALID"])], [1, 3, 2, 4, 0])
        arcpy.topographic.ValidateSpotHeights(contours, "Z", 20, raster, spots, "ZV", use_rasters="NO_USE_RASTERS")
        self.assertEqual([v for (v,) in self.values(spots, ["IS_VALID"])], [0, 3, 2, 4, 0])

    def test_thin_spot_heights(self):
        # Highest (120) and lowest (90) are kept first; then 110 at 3000 m, 2000 m from 90; 100 at 0 m lies 500 m
        # from 120, inside 1300 m, and is hidden.
        spots = self.fc("Spots", "POINT", [("ZV", "DOUBLE"), ("INV", "LONG")],
                        [(100.0, None, Point(0, 0)), (120.0, None, Point(500, 0)), (90.0, None, Point(1000, 0)),
                         (110.0, None, Point(3000, 0))])
        aoi = self.fc("AOI", "POLYGON", [], [(square(-10, -10, 4000, 10),)])
        arcpy.topographic.ThinSpotHeights(spots, aoi, "ZV", "INV")
        self.assertEqual([v for (v,) in self.values(spots, ["INV"])], [1, 0, 0, 0])


class Direction(Fixture):
    def test_downhill_and_with_the_flow(self):
        # The ramp rises east. A line drawn west to east runs uphill and is reversed; one drawn east to west stays.
        raster = self.raster("ramp", [[10 * c for c in range(10)] for _ in range(10)])
        lines = self.fc("Rivers", "POLYLINE", [], [(LineString([(15, 50), (85, 50)]),), (LineString([(85, 20), (15, 20)]),)])
        arcpy.topographic.SetLineDirection(lines, raster, "DOWNHILL")
        self.assertEqual([s.geoms[0].coords[0] for (s,) in self.shapes(lines)], [(85, 50), (85, 20)])
        # A network: two tributaries meeting at (50, 50), drawn against the flow, and an outlet line to the west.
        net = self.fc("Net", "POLYLINE", [], [(LineString([(50, 50), (85, 80)]),), (LineString([(50, 50), (85, 20)]),),
                                              (LineString([(15, 50), (50, 50)]),)])
        arcpy.topographic.SetLineDirection(net, raster, "DOWNHILL_FLOW")
        ends = [(s.geoms[0].coords[0], s.geoms[0].coords[-1]) for (s,) in self.shapes(net)]
        self.assertEqual(ends, [((85, 80), (50, 50)), ((85, 20), (50, 50)), ((50, 50), (15, 50))])


class Bands(Fixture):
    def test_bands_of_a_ramp(self):
        # 100 columns of 10 m holding 0 … 990: a range over 600, so four bands of 20/30/30/20 % of the area; the
        # quantiles 198, 495, 792 snap to the 20 m contours 200, 500, 800. The first band, values under 200, is
        # columns 0-19: 20 × 10 m by 10 rows × 10 m = 20000 m².
        raster = self.raster("ramp", [[10 * c for c in range(100)] for _ in range(10)])
        aoi = self.fc("AOI", "POLYGON", [], [(square(0, 0, 1000, 100),)])
        out = os.path.join(self.directory, "bands.gpkg")
        arcpy.topographic.GenerateElevationBands(raster, aoi, out, 20, 1, 0)
        found = sorted(self.shapes(out, ["Low_Value", "High_Value", "TintBand"]), key=lambda r: r[2])
        self.assertEqual([(lo, hi, k) for lo, hi, k, _ in found], [(0, 200, 1), (200, 500, 2), (500, 800, 3),
                                                                   (800, 990, 4)])
        self.assertAlmostEqual(found[0][3].area, 20000.0, delta=1e-6)

    def test_bands_from_contours(self):
        # The area (0,0)-(30,10) cut by contours 100 at x=10 and 200 at x=20: the middle face lies between them, the
        # west one below 100 and the east one above 200. Bands every 100 m: one face each, TintBand 1, 2, 3.
        contours = self.fc("Contours", "POLYLINE", [("Z", "DOUBLE")],
                           [(100.0, LineString([(10, 0), (10, 10)])), (200.0, LineString([(20, 0), (20, 10)]))])
        aoi = self.fc("AOI", "POLYGON", [], [(square(0, 0, 30, 10),)])
        out = os.path.join(self.directory, "bands.gpkg")
        arcpy.topographic.GenerateElevationBandsFromFeatures(contours, "Z", aoi, out, band_interval=100)
        found = sorted((k, lo, hi, round(s.centroid.x, 6), round(s.area, 6))
                       for lo, hi, k, s in self.shapes(out, ["Low_Value", "High_Value", "TintBand"]))
        self.assertEqual(found, [(1, 0, 100, 5.0, 100.0), (2, 100, 200, 15.0, 100.0), (3, 200, 300, 25.0, 100.0)])
        with self.assertRaises(NotBuiltError):
            arcpy.topographic.GenerateElevationBandsFromFeatures(contours, "Z", aoi, out, product="ONC")

    def test_the_elevation_guide(self):
        raster = self.raster("ramp", [[10 * c for c in range(100)] for _ in range(10)])
        dataset = arcpy.management.CreateFeatureDataset(self.gpkg, "EGB", UTM)[0]
        aoi = self.fc("AOI", "POLYGON", [], [(square(0, 0, 1000, 100),)])
        spots = self.fc("Spots", "POINT", [("ZV", "DOUBLE")], [(500.0, Point(500, 50)), (600.0, Point(600, 50))])
        arcpy.topographic.GenerateElevationGuideFeatures(dataset, aoi, raster, spot_height_features=spots,
                                                         height_field="ZV", search_distance="200 Meters",
                                                         bands_minarea=1, smooth_tolerance=0)
        self.assertEqual(len(self.values(f"{dataset}/EGB_BANDS_A", ["TintBand"])), 4)
        self.assertEqual(len(self.values(f"{dataset}/EGB_BANDS_L", ["OID@"])), 3)   # the three band limits
        self.assertEqual(self.values(f"{dataset}/EGB_SPOTS_P", ["ZV"]), [(600.0,)])   # 100 m apart: the higher kept


class Shapes(Fixture):
    def test_a_narrow_corridor(self):
        # Two 20 m squares joined by a 20 m × 4 m corridor; at a 6 m minimum width the corridor is narrow and long
        # (its centreline about 20 m against 5 m), the squares wide. The opening's 3 m disc reaches at most 3 m into
        # each mouth of the corridor, so the narrow piece is between 80 − 2 × 3 × 4 = 56 m² and 80 m².
        shape = square(0, 0, 20, 20).union(square(20, 8, 40, 12)).union(square(40, 0, 60, 20))
        polygons = self.fc("Water", "POLYGON", [], [(shape,)])
        out = os.path.join(self.directory, "narrow.gpkg")
        arcpy.topographic.IdentifyNarrowPolygons(polygons, out, "6 Meters", "5 Meters")
        found = sorted((t, round(s.area, 3), fid) for t, fid, s in self.shapes(out, ["split_type", "FID"]))
        self.assertEqual([(t, fid) for t, _, fid in found], [(0, 1), (1, 1), (1, 1)])
        self.assertTrue(56.0 <= found[0][1] <= 80.0, found[0][1])
        self.assertAlmostEqual(found[1][1] + found[2][1] + found[0][1], shape.area, delta=1e-6)

    def test_centreline_of_a_strip(self):
        # A 100 m × 10 m strip: the centreline runs along y = 5, from (5,5) to (95,5) once the corner branches are
        # pruned — 90 m. Between the connecting lines touching its ends at (0,5) and (100,5) it is 100 m long.
        strip = self.fc("River", "POLYGON", [], [(square(0, 0, 100, 10),)])
        out = os.path.join(self.directory, "centre.gpkg")
        arcpy.topographic.PolygonToCenterline(strip, out)
        (fid, line), = self.shapes(out, ["FID"])
        self.assertEqual(fid, 1)
        self.assertAlmostEqual(line.interpolate(0.5, normalized=True).y, 5.0, delta=0.5)
        self.assertAlmostEqual(line.length, 90.0, delta=1.0)   # within the densified boundary's spacing
        links = self.fc("Links", "POLYLINE", [], [(LineString([(-10, 5), (0, 5)]),), (LineString([(100, 5), (110, 5)]),)])
        out2 = os.path.join(self.directory, "centre2.gpkg")
        arcpy.topographic.PolygonToCenterline(strip, out2, links)
        found = self.shapes(out2, ["FID", "FID_Links"])
        self.assertAlmostEqual(sum(s.length for *_, s in found), 100.0, delta=0.5)
        self.assertEqual(sorted(f for _, f, _ in found if f is not None), [1, 2] if len(found) > 1 else [1])

    def test_thin_hydrology(self):
        # The main river flows south from (0,100) to (0,0) in four lines; tributaries T (31.6 m) joins at (0,50),
        # U (25 m) at (0,40) and s (8.9 m) at (0,30). Minimum length 20 m: s is hidden. Minimum spacing 15 m: T and U
        # join 10 m apart, and U, the shorter, is hidden too.
        rivers = self.fc("Rivers", "POLYLINE", [("INV", "LONG"), ("N", "TEXT")],
                         [(None, "M1", LineString([(0, 100), (0, 50)])), (None, "M2", LineString([(0, 50), (0, 40)])),
                          (None, "M3", LineString([(0, 40), (0, 30)])), (None, "M4", LineString([(0, 30), (0, 0)])),
                          (None, "T", LineString([(30, 60), (0, 50)])), (None, "U", LineString([(25, 40), (0, 40)])),
                          (None, "s", LineString([(8, 34), (0, 30)]))])
        arcpy.topographic.ThinHydrologyLines(rivers, "INV", "20 Meters")
        self.assertEqual({n for n, v in self.values(rivers, ["N", "INV"]) if v}, {"s"})
        arcpy.topographic.ThinHydrologyLines(rivers, "INV", "20 Meters", "15 Meters")
        self.assertEqual({n for n, v in self.values(rivers, ["N", "INV"]) if v}, {"s", "U"})

    def sheets(self):
        rows = [(f"S{r}{c}", square(10 * c, 10 * r, 10 * c + 10, 10 * r + 10)) for r in range(5) for c in range(5)]
        path = self.fc("Index", "POLYGON", [("JOG_SHEET", "TEXT")], rows)
        arcpy.management.MakeFeatureLayer(path, "index")
        arcpy.management.SelectLayerByAttribute("index", "NEW_SELECTION", "JOG_SHEET = 'S22'")
        return path

    def test_adjoining_sheets(self):
        # The centre sheet S22 (20,20)-(30,30) buffered by 1 m meets its eight neighbours: nine sheets; clipped to
        # them the area is (19,19)-(31,31), 144 m². Land west of x = 25 leaves 6 × 12 = 72 m² of water.
        self.sheets()
        land = self.fc("Land", "POLYGON", [], [(square(0, 0, 25, 50),)])
        dataset = arcpy.management.CreateFeatureDataset(self.gpkg, "ASG", UTM)[0]
        with self.assertRaises(NotBuiltError):
            arcpy.topographic.GenerateAdjoiningSheetsFeatures(dataset, "index")
        arcpy.topographic.GenerateAdjoiningSheetsFeatures(dataset, "index", land, "None", "CLIP_AOI", "1 Meters")
        self.assertEqual(len(self.values(f"{dataset}/ASG_SHEETS_A", ["JOG_SHEET"])), 9)
        self.assertAlmostEqual(self.shapes(f"{dataset}/ASG_AOI")[0][0].area, 144.0, delta=1e-6)
        self.assertAlmostEqual(sum(s.area for (s,) in self.shapes(f"{dataset}/ASG_COAST_A")), 72.0, delta=1e-6)

    def test_location_diagram(self):
        # S22 at the centre of five sheets by five: LD_AOI (0,0)-(50,50), all 25 sheets inside it.
        self.sheets()
        dataset = arcpy.management.CreateFeatureDataset(self.gpkg, "LD", UTM)[0]
        onc = self.fc("ONC", "POLYGON", [("NAME", "TEXT")], [("E-1", square(-100, -100, 100, 100))])
        arcpy.topographic.GenerateLocationDiagramFeatures(dataset, "index", "JOG_SHEET", None, onc, None)
        self.assertAlmostEqual(self.shapes(f"{dataset}/LD_AOI")[0][0].area, 2500.0, delta=1e-6)
        self.assertEqual(self.values(f"{dataset}/JOG_AOI", ["JOG_SHEET"]), [("S22",)])
        self.assertEqual(len(self.values(f"{dataset}/JOG_SHEETS_A", ["JOG_SHEET"])), 25)
        self.assertAlmostEqual(self.shapes(f"{dataset}/JOG_ONC")[0][0].area, 2500.0, delta=1e-6)
        with self.assertRaises(NotBuiltError):
            arcpy.topographic.GenerateLocationDiagramFeatures(dataset, "index", "JOG_SHEET", None, onc, None, "ONC",
                                                             onc)


if __name__ == "__main__":
    unittest.main()
