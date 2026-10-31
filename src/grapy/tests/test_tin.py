"""TINs held in a GeoPackage (ADR-229), against hand-worked answers.

Most tests use the plane z = x + y over the square 0–10, sampled at its corners and centre: every triangle of it lies on
that plane, so slope is √2 (141.42 %), aspect is 225° (downhill to the south-west), and the volume under any plane can
be integrated by hand.
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
from grapy import _tin, da  # noqa: E402
from grapy._runtime import NotBuiltError  # noqa: E402


def rows(path, fields):
    with da.SearchCursor(path, fields) as cursor:
        return [tuple(r) for r in cursor]


class Tin(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]
        self.points = self.feature_class("pts", "POINT", [(0, 0, 0), (10, 0, 10), (10, 10, 20), (0, 10, 10), (5, 5, 10)])
        self.tin = os.path.join(self.directory, "plane")
        arcpy.ddd.CreateTin(self.tin, 3857, [[self.points, "Shape.Z", "masspoints", ""]])

    def feature_class(self, name, kind, shapes, has_z=True, fields=()):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, kind, has_z="ENABLED" if has_z else "DISABLED",
                                                   spatial_reference=3857)[0]
        for field, field_type in fields:
            arcpy.management.AddField(path, field, field_type)
        with da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for shape in shapes:
                if isinstance(shape, tuple) and len(shape) == 3 and not isinstance(shape[0], (tuple, list)):
                    cursor.insertRow([shapely.Point(*shape)])
                elif isinstance(shape, tuple):
                    cursor.insertRow([*shape[:-1], shape[-1]])
                else:
                    cursor.insertRow([shape])
        return path

    # ---- the model

    def test_a_tin_is_its_own_tables_in_a_geopackage_registered_as_an_extension(self):
        import sqlite3
        file, name = _tin.locate(self.tin)
        self.assertEqual((os.path.basename(file), name), ("tin.gpkg", "plane"))
        connection = sqlite3.connect(file)
        try:
            self.assertEqual(connection.execute("select count(*) from grapy_tin_node where tin = 'plane'").fetchone()[0], 5)
            self.assertEqual(connection.execute("select count(*) from grapy_tin_triangle where tin = 'plane'").fetchone()[0],
                             4)
            registered = {r[0] for r in connection.execute(
                "select table_name from gpkg_extensions where extension_name = 'grapy_tin'")}
        finally:
            connection.close()
        self.assertEqual(registered, set(_tin.TABLES))

    def test_describe_answers_a_tins_documented_properties(self):
        described = arcpy.Describe(self.tin)
        self.assertEqual(described.dataType, "Tin")
        self.assertTrue(described.isDelaunay)
        self.assertFalse(described.hasTriangleTagValues)
        self.assertEqual((described.extent.XMin, described.extent.YMax), (0.0, 10.0))
        self.assertEqual(described.spatialReference.factoryCode, 3857)
        self.assertEqual(arcpy.da.Describe(self.tin)["dataType"], "Tin")

    def test_a_tin_may_also_be_named_inside_a_geopackage(self):
        inside = self.gpkg + "/surface"
        arcpy.ddd.CopyTin(self.tin, inside)
        self.assertEqual(arcpy.Describe(inside).nodeCount, 5)

    # ---- features from a TIN

    def test_tin_triangle_answers_each_planes_slope_aspect_and_hillshade(self):
        out = self.gpkg + "/triangles"
        arcpy.ddd.TinTriangle(self.tin, out, "PERCENT", 1, "HILLSHADE 315, 45", "tag")
        found = rows(out, ["Slope_Pct", "Aspect", "Hillshade", "tag"])
        self.assertEqual(len(found), 4)
        for slope, aspect, shade, tag in found:
            self.assertAlmostEqual(slope, 100 * math.sqrt(2), 9)
            self.assertAlmostEqual(aspect, 225.0, 9)
            # 255 · (cos 45° · cos atan √2 + sin 45° · sin atan √2 · cos(315° − 225°)) = 255 · 0.7071 · 0.5774 = 104.1
            self.assertEqual(shade, 104)
            self.assertEqual(tag, 0)
        arcpy.ddd.TinTriangle(self.tin, self.gpkg + "/degrees", "DEGREE")
        self.assertAlmostEqual(rows(self.gpkg + "/degrees", ["Slope_Deg"])[0][0], math.degrees(math.atan(math.sqrt(2))), 9)

    def test_tin_node_writes_3d_points_or_2d_points_with_a_spot_field(self):
        arcpy.ddd.TinNode(self.tin, self.gpkg + "/nodes")
        zs = sorted(r[0] for r in rows(self.gpkg + "/nodes", ["SHAPE@Z"]))
        self.assertEqual(zs, [0, 10, 10, 10, 20])
        arcpy.ddd.TinNode(self.tin, self.gpkg + "/spots", "Spot", "Tag")
        self.assertEqual(sorted(r[0] for r in rows(self.gpkg + "/spots", ["Spot"])), [0, 10, 10, 10, 20])

    def test_tin_edge_writes_the_data_areas_edges_with_their_type(self):
        arcpy.ddd.TinEdge(self.tin, self.gpkg + "/edges")
        found = rows(self.gpkg + "/edges", ["EdgeType", "SHAPE@LENGTH"])
        self.assertEqual(len(found), 8)   # four sides and four half-diagonals
        self.assertTrue(all(code == 0 for code, _ in found))
        arcpy.ddd.TinEdge(self.tin, self.gpkg + "/hard", "HARD")
        self.assertEqual(rows(self.gpkg + "/hard", ["EdgeType"]), [])

    def test_tin_domain_is_the_convex_hull_without_clip_polygons(self):
        arcpy.ddd.TinDomain(self.tin, self.gpkg + "/domain", "POLYGON")
        (area,), = rows(self.gpkg + "/domain", ["SHAPE@AREA"])
        self.assertAlmostEqual(area, 100.0, 9)
        arcpy.ddd.TinDomain(self.tin, self.gpkg + "/outline", "LINE")
        (length,), = rows(self.gpkg + "/outline", ["SHAPE@LENGTH"])
        self.assertAlmostEqual(length, 40.0, 9)

    # ---- surface features

    def test_a_hard_breakline_is_made_conforming_by_densification_and_kept_as_hard_edges(self):
        rng = np.random.default_rng(7)
        cloud = [tuple(p) for p in np.column_stack([rng.uniform(0, 100, 60), rng.uniform(0, 100, 60), np.zeros(60)])]
        cloud += [(0, 0, 0), (100, 0, 0), (100, 100, 0), (0, 100, 0)]
        mass = self.feature_class("cloud", "POINT", cloud)
        ridge = self.feature_class("ridge", "POLYLINE", [shapely.MultiLineString([[(5, 7, 3), (93, 88, 3)]])])
        tin = os.path.join(self.directory, "ridged")
        arcpy.ddd.CreateTin(tin, None, [[mass, "Shape.Z", "masspoints", ""], [ridge, "Shape.Z", "hardline", ""]])
        surface = _tin.load(tin)
        hard = [shapely.LineString(surface.nodes[[i, j], :2]) for (i, j), kind in surface.edges.items() if kind == 2]
        merged = shapely.line_merge(shapely.MultiLineString(hard))
        # The hard edges are exactly the breakline: one line from end to end, no longer than it.
        self.assertEqual(merged.geom_type, "LineString")
        self.assertAlmostEqual(merged.length, math.hypot(88, 81), 6)
        self.assertGreater(len(hard), 1)  # densified
        # Every triangle is Delaunay: no node strictly inside its circumcircle.
        for t in surface.triangles:
            (ax, ay), (bx, by), (cx, cy) = surface.nodes[t, :2]
            d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by))
            ux = ((ax ** 2 + ay ** 2) * (by - cy) + (bx ** 2 + by ** 2) * (cy - ay) + (cx ** 2 + cy ** 2) * (ay - by)) / d
            uy = ((ax ** 2 + ay ** 2) * (cx - bx) + (bx ** 2 + by ** 2) * (ax - cx) + (cx ** 2 + cy ** 2) * (bx - ax)) / d
            radius = math.hypot(ax - ux, ay - uy)
            distances = np.hypot(surface.nodes[:, 0] - ux, surface.nodes[:, 1] - uy)
            self.assertFalse((distances < radius * (1 - 1e-9)).any())
        # The densification points are on the line at its height, and say where they came from.
        added = surface.origin == 1
        self.assertTrue(added.any())
        self.assertTrue(np.allclose(surface.nodes[added, 2], 3.0))
        arcpy.ddd.TinLine(tin, self.gpkg + "/breaklines")
        (code, length), = rows(self.gpkg + "/breaklines", ["Code", "SHAPE@LENGTH"])
        self.assertEqual(code, 2)
        self.assertAlmostEqual(length, math.hypot(88, 81), 6)

    def test_clip_erase_replace_and_value_fill_polygons_do_what_the_documentation_says(self):
        square = lambda a, b, z=0.0: shapely.MultiPolygon([shapely.Polygon([(a, a, z), (b, a, z), (b, b, z), (a, b, z)])])  # noqa: E731
        clip = self.feature_class("clip", "POLYGON", [square(2, 8)], has_z=False)
        hole = self.feature_class("hole", "POLYGON", [square(4, 6)], has_z=False)
        tin = os.path.join(self.directory, "clipped")
        arcpy.ddd.CreateTin(tin, None, [[self.points, "Shape.Z", "masspoints", ""], [clip, "<None>", "hardclip", ""],
                                        [hole, "<None>", "softerase", ""]])
        arcpy.ddd.TinDomain(tin, self.gpkg + "/clipped_domain", "POLYGON")
        (area,), = rows(self.gpkg + "/clipped_domain", ["SHAPE@AREA"])
        self.assertAlmostEqual(area, 36.0 - 4.0, 6)
        # The clip polygon's <None> heights came from the plane: its corner (2, 8) is a node at z 10.
        surface = _tin.load(tin)
        corner = surface.nodes[(surface.nodes[:, 0] == 2) & (surface.nodes[:, 1] == 8)]
        self.assertAlmostEqual(float(corner[0, 2]), 10.0, 9)

        flat = self.feature_class("flat", "POLYGON", [("x", square(1, 3))], has_z=False, fields=[("h", "DOUBLE")])
        with da.UpdateCursor(flat, ["h"]) as cursor:
            for row in cursor:
                cursor.updateRow([3.5])
        tags = self.feature_class("tags", "POLYGON", [(7, square(6, 9))], has_z=False, fields=[("code", "LONG")])
        tin = os.path.join(self.directory, "filled")
        arcpy.ddd.CreateTin(tin, None, [[self.points, "Shape.Z", "masspoints", ""], [flat, "h", "hardreplace", ""],
                                        [tags, "<None>", "softvaluefill", "code"]])
        surface = _tin.load(tin)
        inside = (surface.nodes[:, 0] >= 1) & (surface.nodes[:, 0] <= 3) & (surface.nodes[:, 1] >= 1) & \
                 (surface.nodes[:, 1] <= 3)
        self.assertTrue(np.allclose(surface.nodes[inside, 2], 3.5))
        self.assertTrue(arcpy.Describe(tin).hasTriangleTagValues)
        arcpy.ddd.TinPolygonTag(tin, self.gpkg + "/tagged")
        by_tag = {tag: area for tag, area in rows(self.gpkg + "/tagged", ["Tag_Value", "SHAPE@AREA"])}
        self.assertAlmostEqual(by_tag[7], 9.0, 6)
        self.assertAlmostEqual(by_tag[0], 91.0, 6)

    def test_edit_tin_adds_features_and_triangulates_again(self):
        more = self.feature_class("more", "POINT", [(2, 8, 10)])
        arcpy.ddd.EditTin(self.tin, [[more, "Shape.Z", "<None>", "masspoints", "true"]])
        self.assertEqual(arcpy.Describe(self.tin).nodeCount, 6)
        self.assertEqual(arcpy.Describe(self.tin).triangleCount, 6)

    def test_delineate_tin_data_area_masks_triangles_with_long_edges(self):
        clusters = self.feature_class("clusters", "POINT", [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (10, 0, 0),
                                                           (11, 0, 0), (11, 1, 0), (10, 1, 0)])
        tin = os.path.join(self.directory, "clusters")
        arcpy.ddd.CreateTin(tin, None, [[clusters, "Shape.Z", "masspoints", ""]])
        for method in ("ALL", "PERIMETER_ONLY"):
            arcpy.ddd.DelineateTinDataArea(tin, 2, method)
            arcpy.ddd.TinDomain(tin, self.gpkg + f"/area_{method}", "POLYGON")
            (area,), = rows(self.gpkg + f"/area_{method}", ["SHAPE@AREA"])
            self.assertAlmostEqual(area, 2.0, 9)
        arcpy.ddd.TinEdge(tin, self.gpkg + "/outside", "OUTSIDE")
        self.assertGreater(len(rows(self.gpkg + "/outside", ["EdgeType"])), 0)

    # ---- surfaces

    def test_tin_raster_interpolates_each_cell_centre_on_its_triangles_plane(self):
        out = arcpy.ddd.TinRaster(self.tin, os.path.join(self.directory, "plane.tif"), "FLOAT", "LINEAR", "CELLSIZE 1")[0]
        raster = arcpy.Raster(out)
        array = arcpy.RasterToNumPyArray(raster)
        self.assertEqual(array.shape, (11, 11))           # the lower-left cell is centred on (0, 0)
        self.assertAlmostEqual(float(array[-1, 0]), 0.0, 5)
        self.assertAlmostEqual(float(array[0, -1]), 20.0, 5)
        self.assertAlmostEqual(float(array[7, 3]), 3 + 3, 5)   # row 7 from the top is y = 3
        default = arcpy.Raster(arcpy.ddd.TinRaster(self.tin, os.path.join(self.directory, "obs.tif"))[0])
        self.assertAlmostEqual(default.meanCellWidth, 10 / 250, 12)

    def test_surface_contour_slices_each_triangle_as_a_plane(self):
        arcpy.ddd.SurfaceContour(self.tin, self.gpkg + "/contours", 5, 0, "Contour", 0, 10, "Index")
        found = sorted(rows(self.gpkg + "/contours", ["Contour", "Index", "SHAPE@LENGTH"]))
        self.assertEqual([c for c, _, _ in found], [0, 5, 10, 15, 20][1:4])
        self.assertEqual([i for _, i, _ in found], [0, 1, 0])
        self.assertAlmostEqual(found[0][2], 5 * math.sqrt(2), 9)    # x + y = 5 across the square
        self.assertAlmostEqual(found[1][2], 10 * math.sqrt(2), 9)
        arcpy.ddd.TinContour(self.tin, self.gpkg + "/legacy", 5)
        self.assertEqual(len(rows(self.gpkg + "/legacy", ["Contour"])), 3)

    def test_surface_slope_and_aspect_class_the_triangles_and_merge_them(self):
        gentle = self.feature_class("gentle", "POINT", [(0, 0, 0), (10, 0, 0.5), (10, 10, 0.5), (0, 10, 0)])
        tin = os.path.join(self.directory, "gentle")
        arcpy.ddd.CreateTin(tin, None, [[gentle, "Shape.Z", "masspoints", ""]])
        arcpy.ddd.SurfaceSlope(tin, self.gpkg + "/slope")
        (code, area), = rows(self.gpkg + "/slope", ["SlopeCode", "SHAPE@AREA"])
        self.assertEqual(code, 4)   # 5 % is in the default class 4.64–10
        self.assertAlmostEqual(area, 100.0, 9)
        arcpy.ddd.SurfaceAspect(tin, self.gpkg + "/aspect")
        (code, area), = rows(self.gpkg + "/aspect", ["AspectCode", "SHAPE@AREA"])
        self.assertEqual(code, 7)   # it falls to the west: 270°, the class W 247.5–292.5
        self.assertAlmostEqual(area, 100.0, 9)

    def test_surface_volume_is_exact_over_the_planes(self):
        text = os.path.join(self.directory, "volume.txt")
        arcpy.ddd.SurfaceVolume(self.tin, text, "BELOW", 20)
        arcpy.ddd.SurfaceVolume(self.tin, text, "ABOVE", 10)
        header, below, above = open(text).read().splitlines()
        self.assertEqual(header, "Dataset,Plane_Height,Reference,Z_Factor,Area_2D,Area_3D,Volume")
        # Below 20: ∫(20 − x − y) over the square = 2000 − 1000. Above 10: the half x + y > 10, ∫(x + y − 10) = 166.67.
        self.assertTrue(below.endswith(",20.00,Below,1,100.00,173.21,1000.00"), below)
        self.assertTrue(above.endswith(",10.00,Above,1,50.00,86.60,166.67"), above)

    def test_polygon_volume_measures_within_each_polygon(self):
        whole = self.feature_class("whole", "POLYGON", [(10.0, shapely.MultiPolygon([shapely.box(0, 0, 10, 10)])),
                                                        (0.0, shapely.MultiPolygon([shapely.box(0, 0, 5, 5)]))],
                                   has_z=False, fields=[("plane", "DOUBLE")])
        arcpy.ddd.PolygonVolume(self.tin, whole, "plane", "BELOW")
        volumes = [v for v, in rows(whole, ["Volume"])]
        self.assertAlmostEqual(volumes[0], 500 / 3, 6)   # under 10: 50 · (10 − 20/3)
        self.assertAlmostEqual(volumes[1], 0.0, 9)       # the surface is never below 0
        arcpy.ddd.PolygonVolume(self.tin, whole, "plane", "BOTH")
        above, below, area_above = rows(whole, ["Above_Vol", "Below_Vol", "Above_SA"])[0]
        self.assertAlmostEqual(above, 500 / 3, 6)
        self.assertAlmostEqual(below, 500 / 3, 6)
        self.assertAlmostEqual(area_above, 50 * math.sqrt(3), 6)
        arcpy.ddd.TinPolygonVolume(self.tin, whole, "plane", "ABOVE")
        self.assertAlmostEqual(rows(whole, ["Volume"])[1][0], 25 * 5, 6)   # ∫(x + y) over 0–5 square = 125

    def test_interpolate_shape_samples_vertices_and_triangle_edge_crossings(self):
        line = self.feature_class("line", "POLYLINE", [shapely.MultiLineString([[(1, 1), (9, 2)]]),
                                                       shapely.MultiLineString([[(20, 20), (30, 30)]])], has_z=False)
        out = self.gpkg + "/draped"
        arcpy.ddd.InterpolateShape(self.tin, line, out)
        (shape,), = rows(out, ["SHAPE@"])   # the line off the TIN is left out
        xyz = shapely.get_coordinates(shape._shape, include_z=True)
        self.assertGreater(len(xyz), 2)      # it crosses the diagonal from (10, 0) to (0, 10)
        self.assertTrue(np.allclose(xyz[:, 2], xyz[:, 0] + xyz[:, 1]))
        arcpy.ddd.InterpolateShape(self.tin, line, self.gpkg + "/vertices", vertices_only="VERTICES_ONLY")
        (shape,), = rows(self.gpkg + "/vertices", ["SHAPE@"])
        self.assertEqual(len(shapely.get_coordinates(shape._shape)), 2)

    def test_update_feature_z_puts_vertices_on_the_tin(self):
        line = self.feature_class("zline", "POLYLINE", [shapely.MultiLineString([[(1, 1, 0), (4, 5, 0)]])])
        arcpy.ddd.UpdateFeatureZ(line, self.tin)
        (shape,), = rows(line, ["SHAPE@"])
        self.assertEqual([round(z, 9) for z in shapely.get_coordinates(shape._shape, include_z=True)[:, 2]], [2, 9])

    def test_a_tin_layer_answers_for_its_tin(self):
        arcpy.management.MakeTinLayer(self.tin, "plane_layer")
        self.assertEqual(arcpy.Describe("plane_layer").dataType, "Tin")
        arcpy.ddd.TinDomain("plane_layer", self.gpkg + "/from_layer", "POLYGON")
        self.assertEqual(len(rows(self.gpkg + "/from_layer", ["SHAPE@AREA"])), 1)

    # ---- refused by name

    def test_what_grapy_cannot_do_is_refused_with_its_reason(self):
        with self.assertRaisesRegex(NotBuiltError, "natural neighbour"):
            arcpy.ddd.TinRaster(self.tin, os.path.join(self.directory, "nn.tif"), "FLOAT", "NATURAL_NEIGHBORS")
        with self.assertRaisesRegex(NotBuiltError, "BIM file workspace"):   # terrains are held since ADR-234
            arcpy.ddd.BIMToTerrain()
        with self.assertRaisesRegex(NotBuiltError, "PRE_10.0"):
            arcpy.ddd.CopyTin(self.tin, os.path.join(self.directory, "old"), "PRE_10.0")


class ConstrainedDelaunay(unittest.TestCase):
    """ADR-229 condition 6: CONSTRAINED_DELAUNAY adds each breakline segment as one edge and adds no point, and is
    Delaunay everywhere else (Sloan 1993)."""

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]
        corners = [(0, 0), (10, 0), (10, 10), (0, 10), (5, 4.5), (5, 5.5)]
        self.points = Tin.feature_class(self, "pts", "POINT", [(x, y, x + y) for x, y in corners])
        self.line = Tin.feature_class(self, "line", "POLYLINE", [shapely.MultiLineString([[(0, 5, 5), (10, 5, 15)]])])
        self.inputs = [[self.points, "Shape.Z", "masspoints", ""], [self.line, "Shape.Z", "hardline", ""]]

    def test_a_breakline_is_one_edge_and_no_point_is_added(self):
        conforming = os.path.join(self.directory, "conforming")
        constrained = os.path.join(self.directory, "constrained")
        arcpy.ddd.CreateTin(conforming, 3857, self.inputs, "DELAUNAY")
        arcpy.ddd.CreateTin(constrained, 3857, self.inputs, "CONSTRAINED_DELAUNAY")
        # The two points 1 m apart across the line make Delaunay's edge between them cross it: conforming splits the line.
        self.assertGreater(len(_tin.load(conforming).nodes), 8)
        surface = _tin.load(constrained)
        self.assertEqual(len(surface.nodes), 8)
        self.assertEqual(int(surface.origin.sum()), 0)
        xy = {(float(x), float(y)): i for i, (x, y, _) in enumerate(surface.nodes)}
        a, b = xy[(0.0, 5.0)], xy[(10.0, 5.0)]
        self.assertEqual(surface.edges, {(min(a, b), max(a, b)): 2})
        self.assertIn((min(a, b), max(a, b)), surface.all_edges())
        # Eight nodes, six on the hull (two of them on its sides): 2n − 2 − h = 8 triangles, covering the square.
        self.assertEqual(len(surface.triangles), 8)
        self.assertAlmostEqual(float(shapely.area(surface.polygons()).sum()), 100.0, 9)
        # On the edge itself the surface is the line's own interpolation; the apexes over it are the two near points.
        self.assertAlmostEqual(float(surface.z_at([2.5], [5.0])[0]), 7.5, 9)
        apexes = {tuple(surface.nodes[v, :2]) for t in surface.triangles if a in t and b in t for v in t if v not in (a, b)}
        self.assertEqual(apexes, {(5.0, 4.5), (5.0, 5.5)})
        self.assertFalse(arcpy.Describe(constrained).isDelaunay)
        self.assertTrue(arcpy.Describe(conforming).isDelaunay)

    def test_editing_a_constrained_tin_keeps_the_option_given(self):
        tin = os.path.join(self.directory, "edit")
        arcpy.ddd.CreateTin(tin, 3857, self.inputs[:1], "CONSTRAINED_DELAUNAY")
        arcpy.ddd.EditTin(tin, [[self.line, "Shape.Z", "<None>", "hardline", True]], "CONSTRAINED_DELAUNAY")
        self.assertEqual(len(_tin.load(tin).nodes), 8)
        arcpy.ddd.CopyTin(tin, os.path.join(self.directory, "copy"))
        self.assertTrue(_tin.load(os.path.join(self.directory, "copy")).constrained)

    def test_random_points_and_breaklines_give_a_constrained_delaunay_triangulation(self):
        rng = np.random.default_rng(7)
        xy = rng.uniform(0, 100, (300, 2))
        # Twenty disjoint breaklines between random pairs of points (a pair is kept when it crosses no kept one).
        kept = []
        for _ in range(400):
            i, j = rng.choice(300, 2, replace=False)
            seg = shapely.LineString([xy[i], xy[j]])
            if seg.length > 40 or any(seg.intersects(shapely.LineString([xy[p], xy[q]])) for p, q in kept):
                continue
            others = shapely.points(np.delete(xy, [i, j], axis=0))
            if shapely.dwithin(others, seg, 1e-6).any():
                continue
            kept.append((int(i), int(j)))
            if len(kept) == 20:
                break
        triangles, locked = _tin._constrain(xy, _tin._delaunay(xy), kept)
        edges = {}
        for t, (p, q, r) in enumerate(triangles):
            for u, v in ((p, q), (q, r), (r, p)):
                edges.setdefault((min(u, v), max(u, v)), []).append(t)
        for i, j in kept:
            self.assertIn((min(i, j), max(i, j)), edges)
        hull = shapely.MultiPoint(xy).convex_hull.area
        area = shapely.area(shapely.polygons(xy[np.c_[triangles, triangles[:, :1]]])).sum()
        self.assertAlmostEqual(float(area), hull, 6)
        # Every edge that is not a breakline is locally Delaunay: the opposite vertex is outside the circumcircle.
        for (u, v), ts in edges.items():
            if len(ts) != 2 or (u, v) in locked:
                continue
            t1, t2 = triangles[ts[0]], triangles[ts[1]]
            d = next(w for w in t2 if w not in (u, v))
            a, b, c = (xy[w] - xy[d] for w in t1)
            det = ((a @ a) * (b[0] * c[1] - c[0] * b[1]) - (b @ b) * (a[0] * c[1] - c[0] * a[1])
                   + (c @ c) * (a[0] * b[1] - b[0] * a[1]))
            self.assertLessEqual(det, 1e-6)


LANDXML = """<?xml version="1.0"?>
<LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2" version="1.2">
  <CoordinateSystem epsgCode="3857"/>
  <Surfaces>
    <Surface name="Site 1">
      <Definition surfType="TIN">
        <Pnts>
          <P id="1">0 0 1</P><P id="2">0 10 2</P><P id="3">10 10 3</P><P id="4">10 0 4</P><P id="5">5 5 9</P>
        </Pnts>
        <Faces>
          <F>1 2 5</F><F>2 3 5</F><F>3 4 5</F><F i="1">4 1 5</F>
        </Faces>
      </Definition>
    </Surface>
    <Surface name="grid"><Definition surfType="grid"/></Surface>
  </Surfaces>
</LandXML>
"""


class LandXmlAndDifference(unittest.TestCase):
    """LandXMLToTin, SurfaceDifference and TinDifference (ADR-229 condition 6)."""

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]

    def tin_of(self, name, corners):
        points = Tin.feature_class(self, name + "_pts", "POINT", corners)
        out = os.path.join(self.directory, name)
        arcpy.ddd.CreateTin(out, 3857, [[points, "Shape.Z", "masspoints", ""]])
        return out

    def test_landxml_faces_become_the_tins_triangles(self):
        xml = os.path.join(self.directory, "surfaces.xml")
        open(xml, "w", encoding="utf-8").write(LANDXML)
        arcpy.ddd.LandXMLToTin(xml, self.directory, "s_", "1")
        tin = os.path.join(self.directory, "s_Site1")
        surface = _tin.load(tin)
        self.assertTrue(surface.constrained)
        self.assertEqual(surface.srid, 3857)
        # P is northing, easting, elevation: point 2 is x 10, y 0.
        self.assertAlmostEqual(float(surface.z_at([10.0], [0.0])[0]), 2.0, 9)
        inside = {frozenset(map(tuple, surface.nodes[t][:, :2].tolist())) for t in surface.triangles[surface.inside]}
        xy = {"1": (0.0, 0.0), "2": (10.0, 0.0), "3": (10.0, 10.0), "4": (0.0, 10.0), "5": (5.0, 5.0)}
        faces = {frozenset(xy[i] for i in f.split()) for f in ("1 2 5", "2 3 5", "3 4 5")}
        self.assertEqual(inside, faces)
        # The invisible face, west of the centre, is outside the data area.
        self.assertTrue(np.isnan(surface.z_at([1.0], [5.0])[0]))
        self.assertAlmostEqual(float(surface.z_at([5.0], [5.0])[0]), 9.0, 9)
        # A base name already used writes nothing.
        arcpy.ddd.LandXMLToTin(xml, self.directory, "s_", "Site 1")
        self.assertEqual(len(_tin.load(tin).triangles), len(surface.triangles))

    def test_surface_difference_classifies_and_measures_against_hand_integrals(self):
        # Input z = y and reference z = x over the same 10 m square: the difference y − x is positive north-west of the
        # diagonal. Each side: area 50, volume ∫∫|y − x| = 10³ / 6, and the input's area 50 √2 (its slope is 1).
        square = [(0, 0), (10, 0), (10, 10), (0, 10), (3, 7)]
        tin = self.tin_of("north", [(x, y, y) for x, y in square])
        ref = self.tin_of("east", [(x, y, x) for x, y in square[:4] + [(6, 2)]])
        out = self.gpkg + "/diff"
        raster = os.path.join(self.directory, "diff.tif")
        arcpy.ddd.SurfaceDifference(tin, ref, out, out_raster=raster, raster_cell_size=1,
                                    out_tin_folder=self.directory, out_tin_basename="d")
        found = {code: (volume, area, shape) for volume, area, code, shape in rows(out, ["Volume", "SArea", "Code",
                                                                                        "SHAPE@AREA"])}
        self.assertEqual(sorted(found), [-1, 1])
        for code in (1, -1):
            volume, area, shape = found[code]
            self.assertAlmostEqual(shape, 50.0, 6)
            self.assertAlmostEqual(volume, 1000 / 6, 6)
            self.assertAlmostEqual(area, 50 * math.sqrt(2), 6)
        diff = _tin.load(os.path.join(self.directory, "d"))
        self.assertAlmostEqual(float(diff.z_at([2.0], [8.0])[0]), 6.0, 9)
        cells = arcpy.RasterToNumPyArray(raster, nodata_to_value=np.nan)
        self.assertTrue(np.nanmax(np.abs(cells)) <= 10 + 1e-6)

    def test_only_the_overlap_is_compared_and_equal_surfaces_are_code_zero(self):
        west = self.tin_of("west", [(0, 0, 5), (10, 0, 5), (10, 10, 5), (0, 10, 5)])
        east = self.tin_of("eastern", [(5, 0, 5), (15, 0, 5), (15, 10, 5), (5, 10, 5)])
        out = self.gpkg + "/same"
        arcpy.ddd.TinDifference(west, east, out)
        (volume, area, code, shape), = rows(out, ["Volume", "SArea", "Code", "SHAPE@AREA"])
        self.assertEqual((code, volume), (0, 0.0))
        self.assertAlmostEqual(shape, 50.0, 6)
        self.assertAlmostEqual(area, 50.0, 6)


if __name__ == "__main__":
    unittest.main()
