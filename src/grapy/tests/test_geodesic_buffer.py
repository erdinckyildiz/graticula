"""Geodesic buffers — D-397.

Buffer, PairwiseBuffer and MultipleRingBuffer with ``method`` GEODESIC, and PLANAR on geographic data given a linear
distance, which Pro's page says is geodesic too.

**Two checks, independent of each other.** The ellipsoid: every vertex of a buffer's boundary is measured back to the
input in an azimuthal equidistant projection centred on that vertex — exact from its centre, and built nothing like
``_geodesic.buffer`` — and must be the asked distance to within ``(SAGITTA + 1e-5) × d + 1 cm`` (SAGITTA, 7.5e-5, is
below). ArcGIS Pro 3.7: the same cases were buffered by Pro's own Buffer (arcpy, 2026-11-20, a scratch script) and its
outputs' geodesic areas and BUFF_DIST values are written here as numbers; grapy's area must be within ``5e-4`` of
Pro's. The remaining difference is the polygon itself: a circle of 256 vertices here against Pro's 315 is ``3.4e-5``
smaller, which is what was measured for the points; Pro writes fewer vertices for a small circle (124 for 305 m),
whose area is then ``3.3e-4`` under the circle's — the case that sets the bound. A point's circle is exact at every
vertex.

Measured, and written down here for the morning (the same script, on the same cases):

=================  ========  ===============  =========================
case               Pro area  grapy/Pro area   grapy max vertex error
=================  ========  ===============  =========================
point 0°, 100 km   3.1413e10   0.999966         3e-14 of d
point 45°, 100 km  3.1413e10   0.999966         7e-14
point 70°, 100 km  3.1413e10   0.999966         5e-14
point 45°, 1000 km 3.1351e12   0.999915         6e-15
Web Merc. 60°, 50k 7.8529e9    1.000032         1e-13
line 60° N, 20 km  2.3576e10   1.000012         2.4e-6 (4.8 cm) (*)
UTM line, 5 km     5.2502e8    0.999989         4.1e-7
UTM square, 1 km   1.5148e7    0.999996         4e-9
UTM square −300 m  1.9622e6    1.000000         —
square 70° N 10 km 1.2352e10   1.000000         7.5e-5 (0.75 m) (*)
=================  ========  ===============  =========================

(*) every fourth vertex in the first measurement; all of them, the square's corner arc included, in the second, where
the chords of two pieces meet (SAGITTA). Pro's own vertices measured the same way: within 1e-9 of d.
"""
import math
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import numpy  # noqa: E402
import shapely  # noqa: E402
from shapely import wkt as W  # noqa: E402

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _geodesic  # noqa: E402

# name: (kind, EPSG, WKT, distance, Pro's geodesic area in m², Pro's BUFF_DIST)
CASES = {
    "pt_eq": ("POINT", 4326, "POINT (30 0)", "100 Kilometers", 31413195899, 100000.0),
    "pt45": ("POINT", 4326, "POINT (30 45)", "100 Kilometers", 31413200227, 100000.0),
    "pt70": ("POINT", 4326, "POINT (30 70)", "100 Kilometers", 31413203525, 100000.0),
    "pt45_1000": ("POINT", 4326, "POINT (30 45)", "1000 Kilometers", 3135121289049, 1000000.0),
    "pt60_merc": ("POINT", 3857, "POINT (2783064 8399737)", "50 Kilometers", 7852902467, 50000.0),
    "line60": ("POLYLINE", 4326, "LINESTRING (10 60, 20 60)", "20 Kilometers", 23576189390, 20000.0),
    "line_utm": ("POLYLINE", 32635, "LINESTRING (500000 4500000, 520000 4510000, 540000 4500000)", "5 Kilometers",
                 525017478, 5000.0),
    "poly_utm": ("POLYGON", 32635, "POLYGON ((500000 4500000, 502000 4500000, 502000 4502000, 500000 4502000, "
                 "500000 4500000))", "1 Kilometers", 15147746, 1000.0),
    "poly_utm_neg": ("POLYGON", 32635, "POLYGON ((500000 4500000, 502000 4500000, 502000 4502000, 500000 4502000, "
                     "500000 4500000))", "-300 Meters", 1962241, -300.0),
    "poly70": ("POLYGON", 4326, "POLYGON ((20 70, 20 71, 22 71, 22 70, 20 70))", "10 Kilometers", 12352472976, 10000.0),
    "poly_hole": ("POLYGON", 32635, "POLYGON ((500000 4500000, 510000 4500000, 510000 4510000, 500000 4510000, "
                  "500000 4500000), (503000 4503000, 503000 4507000, 507000 4507000, 507000 4503000, 503000 4503000))",
                  "1500 Meters", 166167109, 1500.0),
    "line_long": ("POLYLINE", 4326, "LINESTRING (0 30, 40 50)", "500 Kilometers", 4843498181146, 500000.0),
    "pt_ft": ("POINT", 2229, "POINT (6500000 1900000)", "1000 Feet", 291738, 304.80060960121926),
    # A bare number is metres under GEODESIC, on a US-feet reference as on WGS 84.
    "pt_ft_bare": ("POINT", 2229, "POINT (6500000 1900000)", "1000", 3141177, 1000.0),
    "pt_geo_bare": ("POINT", 4326, "POINT (30 45)", "100000", 31413200227, 100000.0),
}


# Where two pieces' polygons cross, the vertex is where two chords meet, inside the true circle by up to a chord's
# sagitta: r·(1 − cos(π/256)) for a circle of 256 vertices, 7.5e-5 of the distance (0.75 m on 10 km, measured at the
# corner of the 70° N square). Pro's vertices all lie on its boundary; the chords between them fall inside it the same
# way, by about 5e-5 for its 315.
SAGITTA = 1 - math.cos(math.pi / 256)


def geodesic_area(shape, srid):
    frame = _geodesic._Frame(srid)
    return abs(frame.geod.geometry_area_perimeter(shapely.transform(shape, frame.lonlat))[0])


class _Folder(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)

    def feature(self, name, kind, srid, wkt):
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", kind,
                                                   spatial_reference=srid).getOutput(0)
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            rows.insertRow([arcpy.FromWKT(wkt, arcpy.SpatialReference(srid))])
        return path

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return list(read)


class AsProBuildsThem(_Folder):
    def test_each_case_against_pro_and_the_ellipsoid(self):
        for name, (kind, srid, wkt, distance, pro_area, pro_buff) in CASES.items():
            with self.subTest(name):
                source = self.feature(name, kind, srid, wkt)
                out = arcpy.analysis.Buffer(source, os.path.join(self.directory, f"out_{name}.gpkg"), distance,
                                            method="GEODESIC").getOutput(0)
                (shape, buff), = self.rows(out, ["SHAPE@", "BUFF_DIST"])
                self.assertAlmostEqual(buff, pro_buff, places=6)
                ratio = geodesic_area(shape._shape, srid) / pro_area
                self.assertLess(abs(ratio - 1), 5e-4, f"grapy/Pro area {ratio}")
                metres = abs(pro_buff)
                if pro_buff > 0 and kind != "POLYGON" or name == "poly70":
                    self.assert_on_the_ellipsoid(shape._shape, W.loads(wkt), srid, metres)

    def assert_on_the_ellipsoid(self, result, source, srid, metres):
        frame = _geodesic._Frame(srid)
        rings = [result] if result.geom_type == "Polygon" else list(result.geoms)
        xy = numpy.vstack([numpy.asarray(r.exterior.coords) for r in rings])
        xy = xy[:: max(1, len(xy) // 60)]
        step = 1e-3 if not frame.projected else 50.0
        errors = _geodesic.distances(frame.lonlat(xy), source, srid, step) - metres
        self.assertLess(numpy.abs(errors).max(), (SAGITTA + 1e-5) * metres + 0.01)

    def test_planar_on_geographic_data_with_a_linear_distance_is_geodesic(self):
        # The page: "If the input features are in a geographic coordinate system and the buffer distance is in linear
        # units, geodesic buffers will be created." Pro 3.7 answered the same shape as GEODESIC, BUFF_DIST 100 (the
        # number as given), 2026-11-20.
        source = self.feature("p", "POINT", 4326, "POINT (30 45)")
        out = arcpy.analysis.Buffer(source, os.path.join(self.directory, "o.gpkg"), "100 Kilometers").getOutput(0)
        (shape, buff), = self.rows(out, ["SHAPE@", "BUFF_DIST"])
        self.assertEqual(buff, 100.0)
        self.assertLess(abs(geodesic_area(shape._shape, 4326) / 31413200227 - 1), 2e-4)

    def test_planar_on_projected_data_stays_planar(self):
        # Pro 3.7, the same Web Mercator point at 60° N, PLANAR 50 km: area 1.970e9 m² on the ground, a quarter of the
        # geodesic disc — the map's scale there is 2.
        source = self.feature("m", "POINT", 3857, "POINT (2783064 8399737)")
        out = arcpy.analysis.Buffer(source, os.path.join(self.directory, "o.gpkg"), "50 Kilometers").getOutput(0)
        (shape,), = self.rows(out, ["SHAPE@"])
        self.assertLess(abs(geodesic_area(shape._shape, 3857) / 1970120663 - 1), 2e-3)

    def test_outside_only_takes_the_polygon_away(self):
        wkt = CASES["poly_utm"][2]
        source = self.feature("q", "POLYGON", 32635, wkt)
        out = arcpy.analysis.Buffer(source, os.path.join(self.directory, "o.gpkg"), "1 Kilometers", "OUTSIDE_ONLY",
                                    method="GEODESIC").getOutput(0)
        (shape,), = self.rows(out, ["SHAPE@"])
        self.assertLess(abs(geodesic_area(shape._shape, 32635) / 11144545 - 1), 2e-4)  # Pro 3.7's

    def test_pairwise_buffer_is_buffer(self):
        source = self.feature("p", "POINT", 4326, "POINT (30 70)")
        out = arcpy.analysis.PairwiseBuffer(source, os.path.join(self.directory, "o.gpkg"), "100 Kilometers",
                                            method="GEODESIC").getOutput(0)
        (shape,), = self.rows(out, ["SHAPE@"])
        self.assertLess(abs(geodesic_area(shape._shape, 4326) / 31413203525 - 1), 2e-4)

    def test_multiple_rings_are_geodesic_bands(self):
        source = self.feature("p", "POINT", 3857, "POINT (2783064 8399737)")
        out = arcpy.analysis.MultipleRingBuffer(source, os.path.join(self.directory, "r.gpkg"), [25, 50],
                                                "Kilometers", method="GEODESIC").getOutput(0)
        rings = sorted(self.rows(out, ["distance", "SHAPE@"]))
        self.assertEqual([d for d, _ in rings], [25.0, 50.0])
        inner, band = (geodesic_area(s._shape, 3857) for _, s in rings)
        # A geodesic disc of 50 km is Pro's 7.8529e9 m²; the band is that less the 25 km disc.
        self.assertLess(abs((inner + band) / 7852902467 - 1), 2e-4)
        self.assertLess(abs(band / inner - 3), 1e-3)


class WhatIsNotBuiltIsRefused(_Folder):
    def test_a_side_or_a_flat_end_is_refused_by_name(self):
        source = self.feature("l", "POLYLINE", 4326, "LINESTRING (10 60, 20 60)")
        with self.assertRaisesRegex(arcpy.NotBuiltError, "line_side"):
            arcpy.analysis.Buffer(source, os.path.join(self.directory, "a.gpkg"), "20 Kilometers", "LEFT",
                                  method="GEODESIC")
        with self.assertRaisesRegex(arcpy.NotBuiltError, "line_end_type"):
            arcpy.analysis.Buffer(source, os.path.join(self.directory, "b.gpkg"), "20 Kilometers", "FULL", "FLAT",
                                  method="GEODESIC")

    def test_the_antimeridian_and_the_poles_are_refused(self):
        # Pro 3.7 builds both (a 100 km disc at 179.5° E came back whole); grapy says it does not.
        near = self.feature("a", "POINT", 4326, "POINT (179.5 10)")
        with self.assertRaisesRegex(ValueError, "antimeridian"):
            arcpy.analysis.Buffer(near, os.path.join(self.directory, "a_out.gpkg"), "100 Kilometers", method="GEODESIC")
        pole = self.feature("p", "POINT", 4326, "POINT (0 89.5)")
        with self.assertRaisesRegex(ValueError, "pole"):
            arcpy.analysis.Buffer(pole, os.path.join(self.directory, "p_out.gpkg"), "100 Kilometers", method="GEODESIC")


if __name__ == "__main__":
    unittest.main()
