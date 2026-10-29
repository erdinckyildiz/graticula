"""BatchUpdateFields, the GTFS readers, and ListFields on joined layers and aliases — ADR-224. Hand-worked answers."""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class Round5(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.parcels = os.path.join(self.directory, "parcels.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "parcels.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(self.parcels, "name", "TEXT")
        arcpy.management.AddField(self.parcels, "value", "DOUBLE")
        with arcpy.da.InsertCursor(self.parcels, ["name", "value", "SHAPE@"]) as written:
            written.insertRow(("a", 1.0, "POINT(1 2)"))
            written.insertRow(("b", 2.5, "POINT(3 4)"))

    def tearDown(self):
        for name in list(arcpy.management._LAYERS):
            arcpy.management.Delete(name)

    def out(self, name):
        return os.path.join(self.directory, name)

    def write(self, name, text):
        path = self.out(name)
        with open(path, "w", encoding="utf-8") as written:
            written.write(text)
        return path

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def test_batch_update_fields(self):
        definitions = self.write("defs.csv", "Target field,Source field,Type,Decimals/Length,Alias,Script\n"
                                             "label,name,Text,,Label of the point,\n"
                                             "double,,Double,,,!value! * 2\n"
                                             "big,,Short,,,is_big(!double!)\n")
        script = self.write("script.py", "def is_big(v):\n    return 1 if v > 3 else 0\n")
        out = arcpy.management.BatchUpdateFields(self.parcels, self.out("updated.gpkg"), definitions, script)[0]
        fields = arcpy.ListFields(out)
        self.assertEqual([(f.name, f.type) for f in fields][1:-1], [("label", "String"), ("double", "Double"),
                                                                   ("big", "SmallInteger")])
        self.assertEqual(fields[1].aliasName, "Label of the point")
        # double is value × 2 (2, 5); big uses that new field: 2 is not over 3, 5 is.
        self.assertEqual(self.rows(out, ["label", "double", "big", "SHAPE@XY"]), [("a", 2.0, 0, (1.0, 2.0)),
                                                                                 ("b", 5.0, 1, (3.0, 4.0))])

    def test_list_fields_reads_aliases_and_joined_layers(self):
        arcpy.management.AlterField(self.parcels, "value", new_field_alias="Assessed value")
        self.assertEqual([f.aliasName for f in arcpy.ListFields(self.parcels)][1:-1], ["name", "Assessed value"])
        lookup = arcpy.management.CreateTable(self.directory, "lookup.gpkg")[0]
        arcpy.management.AddField(lookup, "name", "TEXT")
        arcpy.management.AddField(lookup, "label", "TEXT")
        layer = arcpy.management.AddJoin(self.parcels, "name", lookup, "name")[0]
        self.assertEqual([f.name for f in arcpy.ListFields(layer)],
                         ["parcels.fid", "parcels.name", "parcels.value", "lookup.fid", "lookup.name", "lookup.label",
                          "parcels.geom"])
        described = arcpy.Describe(layer)
        self.assertEqual((described.OIDFieldName, described.shapeFieldName), ("parcels.fid", "parcels.geom"))
        self.assertEqual([f.aliasName for f in described.fields][2], "Assessed value")
        self.assertEqual([f.name for f in arcpy.ListFields(self.parcels)], ["fid", "name", "value", "geom"])

    def test_gtfs_stops_and_shapes(self):
        folder = self.out("gtfs")
        os.makedirs(folder)
        stops = os.path.join(folder, "stops.txt")
        with open(stops, "w", encoding="utf-8") as written:
            written.write("stop_id,stop_name,stop_lat,stop_lon,location_type\n"
                          "s1,First,40.5,30.25,0\ns2,Second,41,31,0\nn1,Node,,,3\n")
        for name, text in (("routes.txt", "route_id,route_short_name,route_type\nr1,1,3\nr2,2,0\n"),
                           ("trips.txt", "route_id,service_id,trip_id,shape_id\nr1,x,t1,sh1\nr2,x,t2,sh1\n"),
                           ("stop_times.txt", "trip_id,arrival_time,departure_time,stop_id,stop_sequence\n"
                                              "t1,08:00:00,08:00:00,s1,1\nt2,09:00:00,09:00:00,s1,1\n"),
                           ("shapes.txt", "shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence\n"
                                          "sh1,0,1,2\nsh1,0,0,1\nsh1,1,1,3\n")):
            with open(os.path.join(folder, name), "w", encoding="utf-8") as written:
                written.write(text)
        points = arcpy.conversion.GTFSStopsToFeatures(stops, self.out("stops.gpkg"))[0]
        got = self.rows(points, ["stop_id", "stop_lat", "location_type", "route_info", "SHAPE@XY"])
        self.assertEqual(len(got), 2)                                       # the node with no coordinates is left out
        self.assertEqual(got[0][:3], ("s1", 40.5, 0))
        self.assertEqual(json.loads(got[0][3]), {"route_id": ["r1", "r2"], "route_type": ["0", "3"]})
        self.assertEqual(got[0][4], (30.25, 40.5))
        self.assertEqual(json.loads(got[1][3]), {"route_id": [], "route_type": []})
        self.assertEqual(arcpy.Describe(points).spatialReference.factoryCode, 4326)
        lines = arcpy.conversion.GTFSShapesToFeatures(os.path.join(folder, "shapes.txt"), self.out("shapes.gpkg"))[0]
        # Points in sequence: (0 0), (1 0), (1 1) — 2 degrees of line; one line for each of the shape's two routes.
        self.assertEqual(self.rows(lines, ["shape_id", "route_id", "route_type", "SHAPE@LENGTH"]),
                         [("sh1", "r1", 3, 2.0), ("sh1", "r2", 0, 2.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.conversion.FeaturesToGTFSStops(points, self.out("out_stops.txt"))



class TopLevelListVersions(unittest.TestCase):
    def test_list_versions_answers_names(self):
        directory = tempfile.mkdtemp()
        gpkg = arcpy.management.CreateFileGDB(directory, "versions")[0]
        arcpy.management.CreateVersion(gpkg, "DEFAULT", "edit", "PUBLIC")
        self.assertEqual(arcpy.ListVersions(gpkg), ["DEFAULT", "edit"])
        arcpy.env.workspace = gpkg
        self.assertEqual(arcpy.ListVersions(), ["DEFAULT", "edit"])


if __name__ == "__main__":
    unittest.main()
