"""The Arcade functions ADR-235 built in its second pass, and the template-rule generators of §5.4, against
hand-worked answers and the function reference's own examples."""
import csv
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime  # noqa: E402
from grapy._arcade import ArcadeError, evaluate  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402


class _Case(unittest.TestCase):
    def equal(self, script, expected, places=None):
        found = evaluate(script)
        if places is not None:
            self.assertAlmostEqual(found, expected, places=places, msg=script)
        else:
            self.assertEqual(found, expected, script)


def spec(direction, angle, output=None, form=None):
    parts = [f"directionType: '{direction}'", f"angleType: '{angle}'"]
    if output:
        parts.append(f"outputType: '{output}'")
    if form is not None:
        parts.append(f"format: '{form}'")
    return "{" + ", ".join(parts) + "}"


class ConvertDirection(_Case):
    """The Geometry functions page's ConvertDirection examples, each with its documented result."""

    def test_documented_results(self):
        self.equal(f"ConvertDirection(30, {spec('North', 'Degrees')}, {spec('Quadrant', 'DMS', 'value')})",
                   ["N", 30, 0, 0, "E"])
        self.equal(f"ConvertDirection(25.99, {spec('North', 'Gradians')}, {spec('North', 'Gradians', 'value')})", 25.99)
        self.equal(f"ConvertDirection(1, {spec('North', 'DEGREES')}, {spec('Quadrant', 'Degrees', 'value')})",
                   ["N", 1, "E"])
        self.equal(f"ConvertDirection(0.9, {spec('North', 'degrees')}, {spec('North', 'gradians', 'value')})", 1.0)
        self.equal(f"ConvertDirection(180.0, {spec('North', 'degrees')}, {spec('North', 'radians', 'value')})", math.pi)
        self.equal(f"ConvertDirection(25.34, {spec('North', 'DEGREES')}, {spec('North', 'Degrees', 'text', 'DDDD.D')})",
                   "0025.3")
        self.equal(f"ConvertDirection(25.34, {spec('North', 'DEGREES')}, {spec('North', 'Degrees', 'text', 'R')})", "0")
        self.equal(f"ConvertDirection(25.34, {spec('North', 'DEGREES')}, "
                   f"{spec('North', 'Degrees', 'text', '[DD.DD]')})", "DD.DD")
        self.equal(f"ConvertDirection(25.34, {spec('North', 'DEGREES')}, {spec('quadrant', 'Degrees', 'text', 'P B')})",
                   "North East")
        self.equal(f"ConvertDirection([001,01,59.99], {spec('North', 'DMS')}, "
                   f"{spec('North', 'DMS', 'text', 'dddA mm[B] ssC')})", "001A 02B 00C")

    def test_directions_turn_into_each_other(self):
        self.equal(f"ConvertDirection(120, {spec('North', 'Degrees')}, {spec('Quadrant', 'Degrees', 'value')})",
                   ["S", 60, "E"])
        self.equal(f"ConvertDirection(['S', 30, 'W'], {spec('Quadrant', 'Degrees')}, {spec('North', 'Degrees', 'value')})",
                   210)
        self.equal(f"ConvertDirection(0, {spec('Polar', 'Degrees')}, {spec('North', 'Degrees', 'value')})", 90)
        self.equal(f"ConvertDirection(10, {spec('South', 'Degrees')}, {spec('North', 'Degrees', 'value')})", 190)
        self.equal(f"ConvertDirection('N 45 W', {spec('Quadrant', 'Degrees')}, {spec('North', 'Degrees', 'value')})", 315)
        self.equal(f"ConvertDirection(45.5, {spec('North', 'Degrees')}, {spec('North', 'DMS', 'value')})", [45, 30, 0])
        self.equal(f"ConvertDirection(300, {spec('North', 'Degrees')}, {spec('Quadrant', 'Degrees', 'text', 'p DD b')})",
                   "N 60 W")
        with self.assertRaisesRegex(ArcadeError, "angleType"):
            evaluate(f"ConvertDirection(1, {spec('North', 'Mils')}, {spec('North', 'Degrees')})")


class Patterns(_Case):
    def test_date_only_and_time_formats(self):
        self.equal('Text(DateOnly("10 Jan 2022", "D MMM Y"))', "2022-01-10")  # the page's example
        self.equal('Text(Time("12 05 04", "HH MM ss"))', "12:05:04")       # the page's example
        self.equal('Text(DateOnly("Tuesday, 05/03/96", "dddd, MM/DD/YY"))', "1996-05-03")
        self.equal('Text(DateOnly("2021 032", "Y DDD"))', "2021-02-01")
        self.equal('Text(Time("7:05 pm", "h:mm A"))', "19:05:00")
        self.equal('DateOnly("31 Feb 2022", "D MMM Y")', None)
        self.equal('Time("25:00", "HH:mm")', None)

    def test_dates_only_and_times_as_numbers(self):
        self.equal("Number(DateOnly(1970, 0, 2))", 86400000)
        self.equal("Number(Time(0, 0, 1))", 1000)


class Geometry(_Case):
    square = "var sq = Polygon({rings: [[[0,0],[0,10],[10,10],[10,0],[0,0]]], spatialReference: {wkid: 3857}}); "
    line = "var ln = Polyline({paths: [[[0,0],[10,0]],[[0,5],[10,5]]], spatialReference: {wkid: 3857}}); "

    def test_coordinates_along_a_line(self):
        self.equal(self.line + "var r = DistanceToCoordinate(ln, 15); [r.coordinate.x, r.coordinate.y, r.partId, "
                   "r.segmentId]", [5, 5, 1, 0])
        self.equal(self.line + "DistanceToCoordinate(ln, 25)", None)
        self.equal(self.line + "var r = PointToCoordinate(ln, Point({x: 3, y: 4})); "
                   "[r.coordinate.y, r.distance, r.distanceAlong, r.partId, r.segmentId]", [5, 1, 13, 1, 0])
        self.equal("var r = MeasureToCoordinate(Polyline({hasM: true, paths: [[[0,0,0],[10,0,100],[10,10,200]]]}), 150); "
                   "[r.coordinate.x, r.coordinate.y, r.distanceAlong, r.segmentId]", [10, 5, 15, 1])
        self.equal(self.line + "MeasureToCoordinate(ln, 1)", None)  # no m-values: null, as documented

    def test_nearest_coordinate_and_vertex_and_their_side(self):
        self.equal(self.line + "var r = NearestCoordinate(ln, Point({x: 3, y: -4})); [r.coordinate.x, r.distance, "
                   "r.sideOfLine]", [3, 4, "right"])
        self.equal(self.line + "NearestCoordinate(ln, Point({x: 3, y: 1})).sideOfLine", "left")
        self.equal(self.line + "NearestCoordinate(ln, Point({x: 3, y: 0})).sideOfLine", "straddle")
        self.equal(self.square + "var r = NearestCoordinate(sq, Point({x: 5, y: 5})); [r.distance, r.sideOfLine]",
                   [0, "straddle"])
        # Outside the clockwise outer ring is its left.
        self.equal(self.square + "NearestCoordinate(sq, Point({x: 5, y: 12})).sideOfLine", "left")
        self.equal(self.line + "var r = NearestVertex(ln, Point({x: 3, y: -4})); [r.coordinate.x, r.distance]", [0, 5])

    def test_cut_answers_left_then_right_then_uncut_parts(self):
        cutter = "Polyline({paths: [[[5,-1],[5,11]]]})"
        self.equal(self.square + f"var c = Cut(sq, {cutter}); [Count(c), Area(c[0]), Centroid(c[0]).x, "
                   "Centroid(c[1]).x]", [2, 50, 2.5, 7.5])
        self.equal(self.square + "Cut(sq, Polyline({paths: [[[50,0],[50,10]]]}))", [])
        self.equal("var two = Polygon({rings: [[[0,0],[0,10],[10,10],[10,0],[0,0]], [[20,0],[20,10],[30,10],[30,0],"
                   f"[20,0]]]}}); var c = Cut(two, {cutter}); [Count(c), Area(c[2])]", [3, 100])
        self.equal(self.line + "var c = Cut(ln, Polyline({paths: [[[5,-1],[5,11]]]})); [Count(c), Length(c[0]), "
                   "Length(c[1])]", [2, 10, 10])

    def test_generalize_offset_and_3d_length(self):
        self.equal("Count(Generalize(Polyline({paths: [[[0,0],[5,0.1],[10,0]]]}), 1).paths[0])", 2)
        self.equal("Count(Generalize(Polyline({paths: [[[0,0],[5,2],[10,0]]]}), 1).paths[0])", 3)
        self.equal("TypeOf(Generalize(Point({x: 1, y: 1}), 5))", "Point")
        # Right of a clockwise ring is inside: a square of 10 offset by 1 is a square of 8.
        self.equal(self.square + "Area(Offset(sq, 1, null, 'miter'))", 64)
        self.equal(self.square + "Area(Offset(sq, -1, null, 'miter'))", 144)
        self.equal("var o = Offset(Polyline({paths: [[[0,0],[10,0]]]}), 2); o.paths[0][0].y", -2)  # right of eastward
        with self.assertRaisesRegex(ArcadeError, "square"):
            evaluate(self.square + "Offset(sq, 1, null, 'square')")
        with self.assertRaisesRegex(ArcadeError, "points"):
            evaluate("Offset(Point({x: 0, y: 0}), 1)")
        self.equal("Length3D(Polyline({hasZ: true, paths: [[[0,0,0],[3,0,4],[3,0,10]]], spatialReference: {wkid: 3857}}))",
                   11)
        self.equal("Length3D(Polyline({paths: [[[0,0],[3,4]]], spatialReference: {wkid: 3857}}), 'kilometers')", 0.005)

    def test_geodetic_buffer_and_densify(self):
        # 64 sides of a geodesic radius of 1,000 m: 32 r² sin(2π/64) = 3,136,548 m², within a part in a thousand.
        area = evaluate("AreaGeodetic(BufferGeodetic(Point({x: 10, y: 50, spatialReference: {wkid: 4326}}), 1, "
                        "'kilometers'), 'square-meters')")
        self.assertAlmostEqual(area / 3136548.0, 1.0, places=3)
        # Ten degrees of the equator, 1,113,195 m, in pieces of at most 100 km: 12 pieces, 13 points.
        self.equal("Count(DensifyGeodetic(Polyline({paths: [[[0,0],[10,0]]], spatialReference: {wkid: 4326}}), "
                   "100000, 'meters').paths[0])", 13)
        with self.assertRaisesRegex(ArcadeError, "Loxodrome"):
            evaluate("BufferGeodetic(Point({x: 0, y: 0, spatialReference: {wkid: 4326}}), 1, 'meters', 'Loxodrome')")


class FeatureSets(_Case):
    data = ("var fs = FeatureSet({fields: [{name: 'id', type: 'esriFieldTypeOID'}, {name: 'kind', type: "
            "'esriFieldTypeString'}, {name: 'v', type: 'esriFieldTypeInteger'}], geometryType: 'esriGeometryPoint', "
            "spatialReference: {wkid: 3857}, features: [{attributes: {id: 7, kind: 'a', v: 1}, geometry: {x: 1, y: 1}}, "
            "{attributes: {id: 8, kind: 'a', v: 3}, geometry: {x: 2, y: 2}}, {attributes: {id: 9, kind: 'b', v: 5}, "
            "geometry: {x: 50, y: 50}}]}); ")

    def test_a_featureset_from_rest_json(self):
        self.equal(self.data + "Count(fs)", 3)
        self.equal(self.data + "First(OrderBy(fs, 'v DESC')).id", 9)
        self.equal(self.data + "Sum(Filter(fs, \"kind = 'a'\"), 'v')", 4)
        self.equal(self.data + "Count(Intersects(fs, Polygon({rings: [[[0,0],[0,10],[10,10],[10,0],[0,0]]]})))", 2)
        self.equal("var fs = FeatureSet('{\"fields\": [{\"name\": \"RANK\", \"type\": \"esriFieldTypeInteger\"}], "
                   "\"geometryType\": \"esriGeometryPoint\", \"spatialReference\": {\"wkid\": 4326}, \"features\": "
                   "[{\"geometry\": {\"x\": -151.0063, \"y\": 63.069}, \"attributes\": {\"RANK\": 1}}]}'); "
                   "Geometry(First(fs)).y", 63.069)

    def test_group_by_and_distinct(self):
        self.equal(self.data + "var g = OrderBy(GroupBy(fs, 'kind', [{name: 'n', expression: '1', statistic: 'COUNT'}, "
                   "{name: 'total', expression: 'v', statistic: 'SUM'}, {name: 's', expression: 'v', statistic: 'STDEV'}, "
                   "{name: 'm', expression: 'v * 10', statistic: 'MAX'}]), 'kind'); var out = []; "
                   "for (var r in g) { Push(out, [r.kind, r.n, r.total, r.s, r.m]) } out",
                   [["a", 2, 4, math.sqrt(2), 30], ["b", 1, 5, None, 50]])
        self.equal(self.data + "Count(Distinct(fs, 'kind'))", 2)
        self.equal(self.data + "var d = OrderBy(Distinct(fs, [{name: 'big', expression: \"CASE WHEN v > 2 THEN 'y' "
                   "ELSE 'n' END\"}]), 'big'); var out = []; for (var r in d) { Push(out, r.big) } out", ["n", "y"])
        with self.assertRaisesRegex(ArcadeError, "not a statistic"):
            evaluate(self.data + "GroupBy(fs, 'kind', {name: 'x', expression: 'v', statistic: 'MEDIAN'})")


class _Workspace(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        _runtime.MESSAGES.clear()
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "city")[0]

    def tearDown(self):
        for name in list(arcpy.management._LAYERS):
            arcpy.management.Delete(name)

    def insert(self, path, fields, rows):
        with arcpy.da.InsertCursor(path, fields) as written:
            return [written.insertRow(r) for r in rows]

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as read:
            return [tuple(r) for r in read]

    def feature_class(self, name, shape, fields):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, shape, spatial_reference=3857)[0]
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        return path

    def import_rule(self, table, result):
        arcpy.management.ImportAttributeRules(table, result[0])


def poly(x0, y0, x1, y1):
    return f"POLYGON(({x0} {y0}, {x0} {y1}, {x1} {y1}, {x1} {y0}, {x0} {y0}))"


class ModelReads(_Workspace):
    def test_related_rows_through_a_relationship_class(self):
        poles = self.feature_class("poles", "POINT", [("pole_id", "TEXT"), ("checks", "LONG")])
        inspections = arcpy.management.CreateTable(self.gpkg, "inspections")[0]
        arcpy.management.AddField(inspections, "pole_id", "TEXT")
        arcpy.management.AddField(inspections, "comments", "TEXT")
        arcpy.management.CreateRelationshipClass(poles, inspections, f"{self.gpkg}/pole_inspection", "SIMPLE",
                                                 "inspections", "pole", "NONE", "ONE_TO_MANY", "NONE", "pole_id",
                                                 "pole_id")
        self.insert(inspections, ["pole_id", "comments"], [("P1", "ok"), ("P1", None), ("P2", "rust")])
        arcpy.management.AddAttributeRule(poles, "checks", "CALCULATION",
                                          "Count(FeatureSetByRelationshipClass($feature, 'pole_inspection', "
                                          "['comments'], false))", field="checks", triggering_events="INSERT")
        arcpy.management.AddAttributeRule(inspections, "pole", "CALCULATION",
                                          "var p = First(FeatureSetByRelationshipClass($feature, 'pole_inspection')); "
                                          "if (p == null) { return $feature.comments } p.pole_id + ':' + $feature.comments",
                                          field="comments", triggering_events="INSERT")
        self.insert(poles, ["pole_id", "SHAPE@"], [("P1", "POINT(0 0)"), ("P3", "POINT(1 1)")])
        self.assertEqual(self.rows(poles, ["pole_id", "checks"]), [("P1", 2), ("P3", 0)])
        self.insert(inspections, ["pole_id", "comments"], [("P3", "new"), ("P9", "orphan")])
        self.assertEqual(self.rows(inspections, ["comments"], "fid > 3"), [("P3:new",), ("orphan",)])

    def test_attachments_of_a_feature(self):
        from grapy import _attachments
        parcels = self.feature_class("parcels", "POLYGON", [("photos", "LONG")])
        arcpy.management.EnableAttachments(parcels)
        self.insert(parcels, ["SHAPE@"], [(poly(0, 0, 1, 1),), (poly(2, 2, 3, 3),)])
        guid = self.rows(parcels, ["GLOBALID@"], "fid = 1")[0][0]
        from grapy import _gpkg
        connection = _gpkg.connect(self.gpkg)
        _attachments.add(connection, "parcels", guid, "front.jpg", b"x" * 10)
        _attachments.add(connection, "parcels", guid, "plan.pdf", b"y" * 500)
        connection.commit()
        connection.close()
        arcpy.management.AddAttributeRule(parcels, "photos", "CALCULATION",
                                          "var all = Attachments($feature); var jpgs = Attachments($feature, "
                                          "{types: ['JPG']}); var big = Attachments($feature, {minsize: 100}); "
                                          "var pdf = 0; if (Count(all) > 1 && Find('pdf', all[1].contentType) > 0) "
                                          "{ pdf = 1000 } Count(all) * 100 + Count(jpgs) * 10 + Count(big) + pdf",
                                          field="photos", triggering_events="UPDATE")
        with arcpy.da.UpdateCursor(parcels, ["photos"]) as rows:
            for row in rows:
                rows.updateRow([-1])
        self.assertEqual(self.rows(parcels, ["photos"]), [(1211,), (0,)])

    def test_associations_of_a_utility_network_feature(self):
        from grapy import _gpkg, _utility
        fds = arcpy.management.CreateFeatureDataset(self.gpkg, "utility", 3857)[0]
        devices = arcpy.management.CreateFeatureclass(fds, "ElectricDevice", "POINT")[0]
        vaults = arcpy.management.CreateFeatureclass(fds, "StructureJunction", "POINT")[0]
        lines = arcpy.management.CreateFeatureclass(fds, "ElectricLine", "POLYLINE")[0]
        for path in (devices, vaults, lines):
            arcpy.management.AddGlobalIDs(path)
            arcpy.management.AddField(path, "found", "TEXT")
        self.insert(devices, ["SHAPE@"], [("POINT(0 0)",), ("POINT(5 0)",)])
        self.insert(vaults, ["SHAPE@"], [("POINT(0 0)",)])
        self.insert(lines, ["SHAPE@"], [("LINESTRING(0 0, 5 0)",)])
        d1, d2 = [r[0] for r in self.rows(devices, ["GLOBALID@"])]
        v1, l1 = self.rows(vaults, ["GLOBALID@"])[0][0], self.rows(lines, ["GLOBALID@"])[0][0]
        table = arcpy.management.CreateTable(fds, "Electric_Associations")[0]
        for name, kind in (("FROMNETWORKSOURCEID", "LONG"), ("FROMGLOBALID", "TEXT"), ("FROMTERMINALID", "LONG"),
                           ("TONETWORKSOURCEID", "LONG"), ("TOGLOBALID", "TEXT"), ("TOTERMINALID", "LONG"),
                           ("ASSOCIATIONTYPE", "SHORT"), ("ISCONTENTVISIBLE", "SHORT"), ("PERCENTALONG", "DOUBLE")):
            arcpy.management.AddField(table, name, kind)
        connection = _gpkg.connect(self.gpkg)
        _utility.ensure(connection)
        for source, name in ((1, "utility/ElectricDevice"), (2, "utility/StructureJunction"),
                             (3, "utility/ElectricLine")):
            connection.execute("insert into grapy_un_source values ('Electric', ?, ?, 'Electric', 'Device')",
                               (source, name))
        connection.commit()
        connection.close()
        self.insert(table, ["FROMNETWORKSOURCEID", "FROMGLOBALID", "TONETWORKSOURCEID", "TOGLOBALID", "ASSOCIATIONTYPE",
                            "ISCONTENTVISIBLE", "PERCENTALONG"],
                    [(1, d1, 1, d2, 1, None, None), (2, v1, 1, d1, 2, 1, None), (1, d2, 3, l1, 5, None, 0.25),
                     (1, d1, 3, l1, 4, None, None)])
        arcpy.management.AddAttributeRule(devices, "found", "CALCULATION", """
            var out = [];
            for (var kind of ['connected', 'container', 'content', 'midspan', 'junctionEdge']) {
              for (var a in FeatureSetByAssociation($feature, kind)) {
                Push(out, kind + ':' + a.className + ':' + DefaultValue(a.isContentVisible, '') + ':' +
                     DefaultValue(a.percentAlong, '') + ':' + DefaultValue(a.side, ''))
              }
            }
            Concatenate(out, '|')""", field="found", triggering_events="UPDATE")
        with arcpy.da.UpdateCursor(devices, ["found"]) as rows:
            for row in rows:
                rows.updateRow(["x"])
        self.assertEqual(self.rows(devices, ["found"]), [
            ("connected:ElectricDevice:::|container:StructureJunction:1::|junctionEdge:ElectricLine:::from",),
            ("connected:ElectricDevice:::|midspan:ElectricLine::0.25:",)])


class Generators(_Workspace):
    def test_calculate_geometry_fields(self):
        parcels = self.feature_class("parcels", "POLYGON", [(n, "DOUBLE") for n in (
            "a", "a_ha", "p", "cx", "parts", "points", "holes", "xmax", "curves")])
        result = arcpy.management.CalculateGeometryFieldsAttributeRule(
            parcels, self.directory, [["a", "AREA"], ["a_ha", "AREA"], ["p", "PERIMETER_LENGTH"], ["cx", "CENTROID_X"],
                                      ["parts", "PART_COUNT"], ["points", "POINT_COUNT"], ["holes", "HOLE_COUNT"],
                                      ["xmax", "EXTENT_MAX_X"], ["curves", "CURVE_COUNT"]], area_unit="HECTARES")
        self.assertTrue(result[0].endswith(os.path.join("parcels", "parcels.csv")))
        with open(result[0], encoding="utf-8", newline="") as read:
            row = next(csv.DictReader(read))
        self.assertEqual((row["NAME"], row["TRIGGERINSERT"], row["TRIGGERUPDATE"], row["TRIGGERINGFIELDS"][:9]),
                         ("Calculate geometry fields", "True", "True", '["geom", '))
        self.assertTrue(any("equivalent" in m["description"] for m in _runtime.MESSAGES))
        arcpy.management.ImportAttributeRules(parcels, result[0])
        # A 200 m square with a 100 m square hole: 30,000 m², 3 ha; perimeter 800 + 400; 10 points; one hole.
        self.insert(parcels, ["SHAPE@"], [("POLYGON((0 0, 0 200, 200 200, 200 0, 0 0), (50 50, 150 50, 150 150, "
                                           "50 150, 50 50))",)])
        self.assertEqual(self.rows(parcels, ["a", "a_ha", "p", "cx", "parts", "points", "holes", "xmax", "curves"]),
                         [(3.0, 3.0, 1200.0, 100.0, 1.0, 10.0, 1.0, 200.0, 0.0)])
        second = arcpy.management.CalculateGeometryFieldsAttributeRule(parcels, self.directory, "p LENGTH")
        self.assertTrue(second[0].endswith("parcels_1.csv"))
        with self.assertRaisesRegex(NotBuiltError, "CENTROID_Z is refused"):
            arcpy.management.CalculateGeometryFieldsAttributeRule(parcels, self.directory, "a CENTROID_Z")
        with self.assertRaisesRegex(NotBuiltError, "coordinate_format"):
            arcpy.management.CalculateGeometryFieldsAttributeRule(parcels, self.directory, "a POINT_X",
                                                                  coordinate_format="DD")

    def test_generate_id(self):
        assets = self.feature_class("assets", "POINT", [("kind", "TEXT"), ("asset_id", "TEXT")])
        result = arcpy.management.GenerateIDAttributeRule(
            assets, self.directory, "asset_id", definition_method="BUILDER",
            id_builder=[["kind = 'pump'", "Pumps", "pump_seq", 10, 5, "PMP", None, 4, "-", None],
                        [None, "Others", "other_seq", None, None, None, "X", None, "_", None]])
        self.assertEqual([s.name for s in arcpy.da.ListDatabaseSequences(self.gpkg)], ["other_seq", "pump_seq"])
        self.assertTrue(os.path.exists(result[1]) and os.path.exists(result[2]))
        arcpy.management.ImportAttributeRules(assets, result[0])
        self.insert(assets, ["kind", "asset_id"], [("pump", None), ("valve", None), ("pump", None), ("pump", "KEEP")])
        self.assertEqual(self.rows(assets, ["asset_id"]), [("PMP-0010",), ("1_X",), ("PMP-0015",), ("KEEP",)])
        with self.assertRaisesRegex(NotBuiltError, "CODED_VALUES is refused"):
            arcpy.management.GenerateIDAttributeRule(assets, self.directory, "asset_id", definition_method="CODED_VALUES")
        with self.assertRaisesRegex(NotBuiltError, "Sequence by lookup is refused"):
            arcpy.management.GenerateIDAttributeRule(assets, self.directory, "asset_id",
                                                     id_builder="# # seq 2 # # # # poly_class::lookup")

    def test_spatial_join(self):
        parcels = self.feature_class("parcels", "POLYGON", [("zone", "TEXT"), ("rate", "DOUBLE"), ("n", "LONG")])
        zones = self.feature_class("zones", "POLYGON", [("code", "TEXT"), ("rate", "DOUBLE")])
        self.insert(zones, ["code", "rate", "SHAPE@"], [("A", 2.0, poly(0, 0, 10, 10)), ("B", 4.0, poly(10, 0, 20, 10)),
                                                        ("C", 9.0, poly(100, 0, 110, 10))])
        mappings = arcpy.FieldMappings()
        for output, source, rule in (("zone", "code", "Join"), ("rate", "rate", "Mean"), ("n", "code", "Count")):
            field_map = arcpy.FieldMap()
            field_map.addInputField(zones, source)
            out = field_map.outputField
            out.name = output
            field_map.outputField = out
            field_map.mergeRule = rule
            field_map.joinDelimiter = "+"
            mappings.addFieldMap(field_map)
        result = arcpy.management.GenerateSpatialJoinAttributeRule(parcels, self.directory, [zones], mappings,
                                                                   search_options=[[zones, "CENTROID", "INTERSECTS",
                                                                                    "6 Meters"]])
        arcpy.management.ImportAttributeRules(parcels, result[0])
        # A parcel centred on x = 10 reaches A and B within 6 m; one centred on x = 50 reaches none.
        self.insert(parcels, ["SHAPE@"], [(poly(8, 4, 12, 6),), (poly(48, 4, 52, 6),)])
        self.assertEqual(self.rows(parcels, ["zone", "rate", "n"]), [("A+B", 3.0, 2), (None, None, 0)])

    def test_symbol_rotation(self):
        valves = self.feature_class("valves", "POINT", [("rot", "DOUBLE")])
        mains = self.feature_class("mains", "POLYLINE", [("size", "LONG")])
        self.insert(mains, ["size", "SHAPE@"], [(8, "LINESTRING(0 0, 10 10)"), (4, "LINESTRING(0 10, 10 0)")])
        arithmetic = arcpy.management.GenerateSymbolRotationAttributeRule(valves, self.directory, "rot",
                                                                          [[mains, "size"]])
        arcpy.management.ImportAttributeRules(valves, arithmetic[0])
        # At (5, 5) both mains meet; MIN takes size 4, running south-east: arithmetic -45°, so 315.
        self.insert(valves, ["SHAPE@"], [("POINT(5 5)",), ("POINT(2 2)",), ("POINT(50 50)",)])
        self.assertEqual(self.rows(valves, ["rot"]), [(315.0,), (45.0,), (None,)])
        arcpy.management.DeleteAttributeRule(valves, "Symbol rotation rot")
        geographic = arcpy.management.GenerateSymbolRotationAttributeRule(
            valves, self.directory, "rot", [[mains, "size"]], rotation_options=[[None, "GEOGRAPHIC", "MAX", 10]])
        arcpy.management.ImportAttributeRules(valves, geographic[0])
        self.insert(valves, ["SHAPE@"], [("POINT(5 5)",)])
        self.assertEqual(self.rows(valves, ["rot"], "fid = 4"), [(55.0,)])  # size 8 runs north-east: 45 + 10

    def test_update_related(self):
        parcels = self.feature_class("parcels", "POLYGON", [("owner", "TEXT"), ("flags", "LONG")])
        bills = arcpy.management.CreateTable(self.gpkg, "bills")[0]
        arcpy.management.AddField(bills, "parcel", "LONG")
        arcpy.management.AddField(bills, "owner", "TEXT")
        signs = self.feature_class("signs", "POINT", [("note", "TEXT"), ("flags", "LONG")])
        self.insert(bills, ["parcel", "owner"], [(1, None), (1, "old"), (2, None)])
        self.insert(signs, ["note", "flags", "SHAPE@"], [("n", 1, "POINT(5 5)"), (None, 0, "POINT(50 50)")])
        arcpy.management.AddField(parcels, "parcel", "LONG")
        result = arcpy.management.GenerateUpdateRelatedAttributeRule(parcels, self.directory, [
            [bills, "owner", "MATCH_FIELDS", "owner", "SET_VALUE", None, "parcel", "parcel"],
            [signs, "note", "SPATIAL_ONLY", "owner", "CONCATENATE", "1 Meters", None, None],
            [signs, "flags", "SPATIAL_ONLY", "flags", "BITWISE_OR", None, None, None]])
        with open(result[0], encoding="utf-8", newline="") as read:
            self.assertEqual(next(csv.DictReader(read))["EXCLUDECLIENTEVALUATION"], "True")
        arcpy.management.ImportAttributeRules(parcels, result[0])
        self.insert(parcels, ["parcel", "owner", "flags", "SHAPE@"], [(1, "Ann", 6, poly(0, 0, 10, 10))])
        self.assertEqual(self.rows(bills, ["parcel", "owner"]), [(1, "Ann"), (1, "Ann"), (2, None)])
        self.assertEqual(self.rows(signs, ["note", "flags"]), [("n-Ann", 7), (None, 0)])
        with self.assertRaisesRegex(ExecuteError, "no relationship class"):
            arcpy.management.GenerateUpdateRelatedAttributeRule(parcels, self.directory, [
                [bills, "owner", "RELATIONSHIP_CLASS", "owner", "SET_VALUE", None, None, None]])
        with self.assertRaisesRegex(ExecuteError, "not for tables"):
            arcpy.management.GenerateUpdateRelatedAttributeRule(parcels, self.directory, [
                [bills, "owner", "SPATIAL_ONLY", "owner", "SET_VALUE", None, None, None]])


if __name__ == "__main__":
    unittest.main()
