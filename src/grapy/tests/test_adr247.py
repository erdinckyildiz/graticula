"""ADR-247: refusals whose reason was grapy's own choice, read again — storage properties against a GeoPackage, the
random number generators, and toolboxes in a session."""
import os
import sqlite3
import sys
import tempfile
import textwrap
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _randomgen  # noqa: E402
from grapy.management import _seed  # noqa: E402


def _fts5():
    connection = sqlite3.connect(":memory:")
    try:
        connection.execute("create virtual table t using fts5(a)")
        return True
    except sqlite3.OperationalError:
        return False
    finally:
        connection.close()


class _Workspace(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "store")[0]

    def points(self, name="pts", srid=3857, rows=()):
        fc = arcpy.management.CreateFeatureclass(self.gpkg, name, "POINT", spatial_reference=srid)[0]
        arcpy.management.AddField(fc, "name", "TEXT")
        arcpy.management.AddField(fc, "note", "TEXT")
        arcpy.management.AddField(fc, "count", "LONG")
        arcpy.management.AddField(fc, "seen", "DATE")
        with arcpy.da.InsertCursor(fc, ["name", "note", "SHAPE@XY"]) as written:
            for row in rows:
                written.insertRow(row)
        return fc


# ---- §5.1 storage properties ------------------------------------------------------------------------------------------

@unittest.skipUnless(_fts5(), "this SQLite has no FTS5")
class FullTextIndex(_Workspace):
    def matching(self, fc, term):
        where = f'fid in (select rowid from "grapy_fts_pts" where "grapy_fts_pts" match \'{term}\')'
        with arcpy.da.SearchCursor(fc, ["OID@"], where) as rows:
            return sorted(r[0] for r in rows)

    def test_the_index_answers_match_and_follows_every_edit(self):
        fc = self.points(rows=[("Blue river", "a crossing", (1, 2)), ("Red hill", "river view", (3, 4)),
                               ("Mill", "", (5, 6))])
        self.assertEqual(str(arcpy.management.AddFullTextIndex(fc, "name;note")), fc)
        self.assertEqual(self.matching(fc, "river"), [1, 2])   # in either field
        with arcpy.da.UpdateCursor(fc, ["name"], "fid = 3") as rows:
            for _ in rows:
                rows.updateRow(["Hill farm"])
        with arcpy.da.InsertCursor(fc, ["name", "SHAPE@XY"]) as written:
            written.insertRow(["Hill", (0, 0)])
        with arcpy.da.UpdateCursor(fc, ["name"], "fid = 2") as rows:
            for _ in rows:
                rows.deleteRow()
        self.assertEqual(self.matching(fc, "hill"), [3, 4])
        self.assertEqual(self.matching(fc, "river"), [1])

    def test_what_it_does_not_take(self):
        fc = self.points()
        with self.assertRaisesRegex(arcpy.ExecuteError, "not a text field"):
            arcpy.management.AddFullTextIndex(fc, "count")
        with self.assertRaisesRegex(arcpy.ExecuteError, "does not exist"):
            arcpy.management.AddFullTextIndex(fc, "nowhere")
        arcpy.management.AddFullTextIndex(fc, ["name"], "ignored_name", "ignored_catalog", "SYNC_ON_COMMIT")
        with self.assertRaisesRegex(arcpy.ExecuteError, "already"):
            arcpy.management.AddFullTextIndex(fc, ["note"])
        # A table deleted and made again takes a new index; the old one went stale with its triggers.
        arcpy.management.Delete(fc)
        fc = self.points(rows=[("Old mill", "", (0, 0))])
        arcpy.management.AddFullTextIndex(fc, ["name"])
        where = 'fid in (select rowid from "grapy_fts_pts" where "grapy_fts_pts" match \'mill\')'
        with arcpy.da.SearchCursor(fc, ["OID@"], where) as rows:
            self.assertEqual([r[0] for r in rows], [1])


class ClusterTolerance(_Workspace):
    def test_the_default_is_a_millimetre_in_the_data_units(self):
        self.assertAlmostEqual(float(str(arcpy.management.CalculateDefaultClusterTolerance(self.points()))), 0.001,
                               delta=1e-12)
        geographic = self.points("geo", 4326)
        # 0.001 m on a sphere of WGS 84's semi-major axis: 0.001 / (6378137 · π / 180) degrees.
        self.assertAlmostEqual(float(str(arcpy.management.CalculateDefaultClusterTolerance(geographic))),
                               8.983152841195214e-09, delta=1e-20)

    def test_a_topology_class_answers_its_topology_tolerance(self):
        fds = arcpy.management.CreateFeatureDataset(self.gpkg, "fds", 3857)[0]
        fc = arcpy.management.CreateFeatureclass(fds, "parcels", "POLYGON")[0]
        topo = arcpy.management.CreateTopology(fds, "topo", 0.25)[0]
        arcpy.management.AddFeatureClassToTopology(topo, fc, 1, 1)
        self.assertEqual(float(str(arcpy.management.CalculateDefaultClusterTolerance(fc))), 0.25)


class Migrations(_Workspace):
    def test_object_ids_are_64_bit_already(self):
        fc = self.points(rows=[("a", "", (0, 0))])
        self.assertEqual(str(arcpy.management.MigrateObjectIDTo64Bit([fc])), fc)
        connection = sqlite3.connect(self.gpkg)
        connection.execute("insert into pts (fid, name) values (?, 'big')", (2 ** 40 + 7,))
        connection.commit()
        connection.close()
        with arcpy.da.SearchCursor(fc, ["OID@"], "name = 'big'") as rows:
            self.assertEqual([r[0] for r in rows], [2 ** 40 + 7])

    def test_date_fields_keep_milliseconds_already(self):
        import datetime
        fc = self.points()
        self.assertEqual(str(arcpy.management.MigrateDateFieldToHighPrecision(fc, ["seen"])), fc)
        with arcpy.da.InsertCursor(fc, ["seen", "SHAPE@XY"]) as written:
            written.insertRow([datetime.datetime(2026, 10, 31, 12, 30, 15, 123000), (0, 0)])
        with arcpy.da.SearchCursor(fc, ["seen"]) as rows:
            self.assertIn("12:30:15.123", str(next(rows)[0]))
        with self.assertRaisesRegex(arcpy.ExecuteError, "not a date field"):
            arcpy.management.MigrateDateFieldToHighPrecision(fc, "name")
        connection = sqlite3.connect(self.gpkg)
        connection.execute('alter table pts add column "day" DATE')
        connection.commit()
        connection.close()
        with self.assertRaisesRegex(arcpy.ExecuteError, "not a date field"):
            arcpy.management.MigrateDateFieldToHighPrecision(fc, "day")

    def test_the_rest_say_their_own_reason(self):
        for name, said in (("AlterGeometryIndexPreference", "SAP HANA"), ("CalculateDefaultGridIndex", "no grid sizes"),
                           ("CompressFileGeodatabaseData", "compressed"), ("UncompressFileGeodatabaseData", "compressed"),
                           ("RecoverFileGDB", ".gdbtable"), ("GenerateFgdbLicense", ".sdlic"),
                           ("GenerateLicensedFgdb", ".licdef"), ("UpgradeDataset", "earlier ArcGIS release"),
                           ("MigrateTextField", "Oracle")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                getattr(arcpy.management, name)("x")


# ---- §5.2 random number generators ------------------------------------------------------------------------------------

class RandomGenerators(_Workspace):
    def tearDown(self):
        arcpy.env.randomGenerator = None

    def test_mersenne_twister_is_init_genrand_seeded_mt19937(self):
        # Matsumoto and Nishimura's mt19937 seeded with 5489: its first output, and its 10,000th, which the C++
        # standard ([rand.predef]) gives for std::mt19937.
        bits = _randomgen._numpy_seed(arcpy.CreateRandomValueGenerator(5489, "MERSENNE_TWISTER"))
        drawn = bits.random_raw(10000)
        self.assertEqual(int(drawn[0]), 3499211612)
        self.assertEqual(int(drawn[-1]), 4123659995)

    def test_the_object_and_its_string(self):
        made = arcpy.CreateRandomValueGenerator(99)
        self.assertIsInstance(made, arcpy.RandomNumberGenerator)
        self.assertEqual(made.exportToString(), "99 ACM599")   # the page's default generator
        made.loadFromString("0 mersenne_twister")
        self.assertEqual(made.exportToString(), "0 MERSENNE_TWISTER")
        with self.assertRaises(ValueError):
            made.loadFromString("0 RANDU")
        with self.assertRaises(ValueError):
            arcpy.CreateRandomValueGenerator(1, "LCG")

    def test_the_environment_takes_every_documented_form(self):
        arcpy.env.randomGenerator = "99 ACM599"            # the environment page's example
        self.assertEqual(_seed(), 99)
        arcpy.env.randomGenerator = arcpy.CreateRandomValueGenerator(99, "ACM599")
        self.assertEqual(_seed(), 99)
        arcpy.env.randomGenerator = 7
        self.assertEqual(_seed(), 7)
        arcpy.env.randomGenerator = "4 STANDARD_C"
        self.assertEqual(_seed(), 4)
        arcpy.env.randomGenerator = None
        self.assertEqual(_seed(), 0)

    def draw(self, name):
        out = arcpy.management.CreateRandomPoints(self.gpkg, name, constraining_extent="0 0 100 100",
                                                  number_of_points_or_field=5)[0]
        with arcpy.da.SearchCursor(out, ["SHAPE@XY"]) as rows:
            return [r[0] for r in rows]

    def test_a_tool_draws_from_the_generator_set(self):
        arcpy.env.randomGenerator = "3 ACM599"
        acm = self.draw("a")
        arcpy.env.randomGenerator = arcpy.CreateRandomValueGenerator(3, "ACM599")
        self.assertEqual(self.draw("b"), acm)
        arcpy.env.randomGenerator = arcpy.CreateRandomValueGenerator(3, "MERSENNE_TWISTER")
        twister = self.draw("c")
        self.assertNotEqual(twister, acm)
        arcpy.env.randomGenerator = "3 MERSENNE_TWISTER"
        self.assertEqual(self.draw("d"), twister)            # a tool starts from the seed, so it repeats itself

    def test_it_is_counted_as_an_equivalent(self):
        self.assertIn("Matsumoto and Nishimura", arcpy.CreateRandomValueGenerator.__grapy_equivalent__)


# ---- §5.3 toolboxes -----------------------------------------------------------------------------------------------------

TOOLBOX = textwrap.dedent('''
    import grapy as arcpy


    class Toolbox:
        def __init__(self):
            self.label = "Sums"
            self.alias = "sums"
            self.tools = [Double, Fail]


    class Double:
        def __init__(self):
            self.label = "Double"
            self.description = "Twice a number."

        def getParameterInfo(self):
            value = arcpy.Parameter(name="value", displayName="Value", datatype="GPDouble")
            factor = arcpy.Parameter(name="factor", displayName="Factor", datatype="GPDouble",
                                     parameterType="Optional")
            factor.value = 2
            out = arcpy.Parameter(name="out", displayName="Out", datatype="GPDouble", direction="Output",
                                  parameterType="Derived")
            return [value, factor, out]

        def execute(self, parameters, messages):
            seen = [p.name for p in arcpy.GetParameterInfo()]
            result = float(parameters[0].value) * float(parameters[1].value)
            messages.addMessage(f"{seen} -> {result}")
            parameters[2].value = result


    class Fail:
        def getParameterInfo(self):
            return []

        def execute(self, parameters, messages):
            messages.addMessage("starting")
            raise ValueError("no luck")
''')


class Toolboxes(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.pyt = os.path.join(self.directory, "Sums.pyt")
        with open(self.pyt, "w", encoding="utf-8") as written:
            written.write(TOOLBOX)

    def tearDown(self):
        from grapy import _toolboxes
        _toolboxes._SESSION.clear()

    def test_a_python_toolbox_is_imported_and_its_tools_run_both_ways(self):
        module = arcpy.ImportToolbox(self.pyt)
        self.assertEqual(module.__all__, ["Double", "Fail"])
        self.assertEqual(str(arcpy.sums.Double(4)), "8.0")
        self.assertEqual(arcpy.Double_sums(4, 3).getOutput(0), "12.0")
        self.assertEqual(arcpy.sums.Double(value=5, factor="#")[0], "10.0")
        self.assertIn("['value', 'factor', 'out'] -> 10.0", arcpy.GetMessages())
        with self.assertRaisesRegex(arcpy.ExecuteError, "no luck"):
            arcpy.sums.Fail()
        self.assertIs(arcpy.AddToolbox(self.pyt, "twice"), arcpy.twice)

    def test_the_session_lists_and_removes_it(self):
        arcpy.ImportToolbox(self.pyt)
        self.assertIn("Sums(sums)", arcpy.ListToolboxes())
        self.assertIn("Analysis Tools(analysis)", arcpy.ListToolboxes("anal*"))
        self.assertEqual(arcpy.ListTools("*_SUMS"), ["Double_sums", "Fail_sums"])
        self.assertIn("Buffer_analysis", arcpy.ListTools("*_analysis"))
        arcpy.RemoveToolbox("sums")
        self.assertEqual(arcpy.ListTools("*_sums"), [])
        self.assertTrue(hasattr(arcpy, "Double_sums"))   # "only from the underlying geoprocessor object"
        with self.assertRaises(ValueError):
            arcpy.RemoveToolbox("sums")

    def test_usage_and_parameters(self):
        arcpy.ImportToolbox(self.pyt)
        self.assertEqual(arcpy.Usage("Double_sums"), "Double_sums(value, {factor})")
        self.assertEqual(arcpy.Usage("Buffer_analysis"),
                         "Buffer_analysis(in_features, out_feature_class, buffer_distance_or_field, {line_side}, "
                         "{line_end_type}, {dissolve_option}, {dissolve_field}, {method})")
        # The GetParameterValue page's example: CreateFeatureClass's geometry_type defaults to POLYGON, by name or
        # by position.
        self.assertEqual(arcpy.GetParameterValue("CreateFeatureClass_management", "geometry_type"), "POLYGON")
        self.assertEqual(arcpy.GetParameterValue("CreateFeatureClass_management", 2), "POLYGON")
        self.assertEqual(arcpy.GetParameterValue("CreateFeatureClass_management", "template"), "")
        self.assertEqual(arcpy.GetParameterValue("sums.Double", "factor"), "2")
        self.assertEqual([p.name for p in arcpy.GetParameterInfo("Double_sums")], ["value", "factor", "out"])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "data type and direction"):
            arcpy.GetParameterInfo("Buffer_analysis")

    def test_command_answers_messages_or_only_the_errors(self):
        arcpy.ImportToolbox(self.pyt)
        self.assertEqual(arcpy.Command("Double_sums 4 #"), "['value', 'factor', 'out'] -> 8.0")
        said = arcpy.Command("Fail_sums")
        self.assertIn("no luck", said)
        self.assertNotIn("starting", said)

    def test_what_stays_refused(self):
        for path, said in (("c:/tools/My_Analysis_Tools.atbx", "not published"), ("old.tbx", "not published"),
                           ("https://machine.domain.com/arcgis/services/;Utilities/PrintingTools", "network"),
                           ("c:/logistics/logistics.ags;World/ServiceAreas", "network")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                arcpy.ImportToolbox(path)
        bare = os.path.join(self.directory, "Bare.pyt")
        with open(bare, "w", encoding="utf-8") as written:
            written.write(TOOLBOX.replace('self.alias = "sums"', 'self.alias = ""'))
        with self.assertRaisesRegex(ValueError, "module_name"):
            arcpy.ImportToolbox(bare)
        with self.assertRaisesRegex(ValueError, "grapy's own modules"):
            arcpy.ImportToolbox(bare, "management")


if __name__ == "__main__":
    unittest.main()
