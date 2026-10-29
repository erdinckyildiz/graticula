"""grapy against arcpy's documented behaviour, and the fence against a hostile tool — ADR-223 conditions 1, 3 and 6.

``python -m unittest discover -s src/grapy/tests`` from the repository root, with shapely and pyproj installed. The
fence tests run on Linux only, as the fence does; elsewhere they are skipped and say why.
"""
import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class FeatureClassesAndCursors(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.fc = os.path.join(self.directory, "parcels.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "parcels.gpkg", "POLYGON", spatial_reference=3857)
        arcpy.management.AddField(self.fc, "name", "TEXT")
        arcpy.management.AddField(self.fc, "value", "DOUBLE")
        with arcpy.da.InsertCursor(self.fc, ["name", "value", "SHAPE@"]) as rows:
            for i in range(3):
                square = arcpy.Polygon(arcpy.Array([arcpy.Point(i * 100, 0), arcpy.Point(i * 100 + 50, 0),
                                                    arcpy.Point(i * 100 + 50, 50), arcpy.Point(i * 100, 50)]), 3857)
                rows.insertRow((f"p{i}", float(i), square))

    def test_a_cursor_reads_back_what_was_written_with_the_documented_tokens(self):
        with arcpy.da.SearchCursor(self.fc, ["OID@", "name", "SHAPE@AREA", "SHAPE@XY"], "value > 0") as rows:
            got = list(rows)
        self.assertEqual([r[1] for r in got], ["p1", "p2"])
        self.assertAlmostEqual(got[0][2], 2500)
        self.assertEqual(got[0][3], (125.0, 25.0))
        self.assertEqual(arcpy.management.GetCount(self.fc).getOutput(0), "3")

    def test_an_update_cursor_changes_and_deletes_rows(self):
        with arcpy.da.UpdateCursor(self.fc, ["name", "value"]) as rows:
            for row in rows:
                if row[0] == "p0":
                    rows.deleteRow()
                else:
                    row[1] = row[1] * 10
                    rows.updateRow(row)
        with arcpy.da.SearchCursor(self.fc, ["value"]) as rows:
            self.assertEqual(sorted(r[0] for r in rows), [10.0, 20.0])

    def test_describe_and_list_fields_answer_as_arcpy_does(self):
        described = arcpy.Describe(self.fc)
        self.assertEqual(described.shapeType, "Polygon")
        self.assertEqual(described.spatialReference.factoryCode, 3857)
        self.assertEqual([f.name for f in arcpy.ListFields(self.fc)][1:3], ["name", "value"])
        self.assertEqual(described.extent.XMax, 250)
        self.assertTrue(arcpy.Exists(self.fc))
        self.assertIn("parcels.gpkg", arcpy.ListFeatureClasses())

    def test_buffer_clip_and_project_write_feature_classes(self):
        buffered = arcpy.analysis.Buffer(self.fc, os.path.join(self.directory, "b.gpkg"), "10 Meters").getOutput(0)
        with arcpy.da.SearchCursor(buffered, ["SHAPE@AREA"]) as rows:
            self.assertTrue(all(r[0] > 2500 for r in rows))
        clipped = arcpy.analysis.Clip(self.fc, buffered, os.path.join(self.directory, "c.gpkg")).getOutput(0)
        self.assertEqual(arcpy.management.GetCount(clipped)[0], "3")
        projected = arcpy.management.Project(self.fc, os.path.join(self.directory, "w.gpkg"), 4326).getOutput(0)
        self.assertEqual(arcpy.Describe(projected).spatialReference.factoryCode, 4326)

    def test_a_name_not_built_says_so_and_hasattr_answers_false(self):
        with self.assertRaises(NotImplementedError) as said:
            arcpy.management.MakeRouteEventLayer("a", "b")
        self.assertIn("MakeRouteEventLayer", str(said.exception))
        self.assertFalse(hasattr(arcpy, "SomethingNobodyBuilt"))

    def test_a_module_on_esris_data_or_on_a_model_not_held_is_refused_with_its_reason(self):
        with self.assertRaises(NotImplementedError) as said:
            arcpy.ba.GenerateTradeAreaRings("stores", "rings")
        self.assertIn("ADR-224 group 3", str(said.exception))
        self.assertIn("arcpy.ba.GenerateTradeAreaRings", str(said.exception))
        with self.assertRaises(NotImplementedError) as said:
            arcpy.un.TraceNetwork("network")
        self.assertIn("ADR-224 group 2", str(said.exception))

    def test_hexagon_bins_count_every_feature(self):
        points = os.path.join(self.directory, "pts.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "pts.gpkg", "POINT", spatial_reference=3857)
        with arcpy.da.InsertCursor(points, ["SHAPE@XY"]) as rows:
            for i in range(20):
                rows.insertRow((((i * 37) % 300, (i * 53) % 300),))
        out = arcpy.graticula.HexagonBins(points, os.path.join(self.directory, "hex.gpkg"), 100).getOutput(0)
        with arcpy.da.SearchCursor(out, ["point_count"]) as rows:
            self.assertGreaterEqual(sum(r[0] for r in rows), 20)


TOOLBOX = textwrap.dedent('''
    import grapy as arcpy

    class Toolbox:
        def __init__(self):
            self.label = "Probe"
            self.alias = "probe"
            self.tools = [Count, Hostile]

    class Count:
        def __init__(self):
            self.label = "Count features"
            self.description = "How many features a layer holds."

        def getParameterInfo(self):
            layer = arcpy.Parameter(name="in_features", displayName="Features", datatype="GPFeatureLayer")
            count = arcpy.Parameter(name="count", displayName="Count", datatype="GPLong", direction="Output",
                                    parameterType="Derived")
            return [layer, count]

        def execute(self, parameters, messages):
            n = int(arcpy.management.GetCount(parameters[0].valueAsText).getOutput(0))
            messages.addMessage(f"{n} features")
            parameters[1].value = n

    class Hostile:
        def __init__(self):
            self.label = "Hostile"

        def getParameterInfo(self):
            return [arcpy.Parameter(name="what", datatype="GPString")]

        def execute(self, parameters, messages):
            what = parameters[0].valueAsText
            if what == "socket":
                import socket
                socket.create_connection(("1.1.1.1", 80), timeout=3)
            elif what == "read":
                open("/etc/hostname").read()
            elif what == "environ":
                open("/proc/1/environ", "rb").read()
            elif what == "memory":
                b = bytearray(3 << 30)
            elif what == "process":
                import subprocess
                subprocess.run(["/bin/true"], check=True)
            messages.addMessage("nothing stopped it")
''')


def _run(directory, run, unfenced):
    with open(os.path.join(directory, "run.json"), "w") as out:
        json.dump(run, out)
    environment = {k: v for k, v in os.environ.items() if k in ("PATH", "SYSTEMROOT", "PYTHONPATH")}
    environment["PYTHONPATH"] = os.path.dirname(HERE)
    if unfenced:
        environment["GRAPY_UNFENCED"] = "1"
    # -c with the package's folder on the path, as the server's own launch puts it there (-I would ignore PYTHONPATH).
    subprocess.run(
        [sys.executable, "-c",
         f"import sys; sys.path.insert(0, {os.path.dirname(HERE)!r}); from grapy.runner import main; sys.exit(main({directory!r}))"],
        env=environment, timeout=120)
    with open(os.path.join(directory, "result.json")) as read:
        return json.load(read)


class Toolboxes(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        with open(os.path.join(self.directory, "probe.pyt"), "w") as out:
            out.write(TOOLBOX)
        arcpy.management.CreateFeatureclass(self.directory, "in.gpkg", "POINT", spatial_reference=4326)
        with arcpy.da.InsertCursor(os.path.join(self.directory, "in.gpkg"), ["SHAPE@XY"]) as rows:
            for i in range(4):
                rows.insertRow(((i, i),))

    def test_describe_lists_the_tools_and_their_parameters(self):
        result = _run(self.directory, {"mode": "describe", "toolbox": "probe.pyt"}, unfenced=sys.platform != "linux")
        self.assertTrue(result["ok"], result)
        self.assertEqual([t["name"] for t in result["tools"]], ["Count", "Hostile"])
        self.assertEqual(result["tools"][0]["parameters"][1]["direction"], "Output")

    def test_a_layer_crosses_in_as_geojson_and_out_again(self):
        toolbox = textwrap.dedent('''
            import grapy as arcpy

            class Toolbox:
                def __init__(self):
                    self.tools = [Halo]

            class Halo:
                def getParameterInfo(self):
                    return [arcpy.Parameter(name="in_features", datatype="GPFeatureRecordSetLayer"),
                            arcpy.Parameter(name="out_features", datatype="GPFeatureRecordSetLayer", direction="Output")]

                def execute(self, parameters, messages):
                    projected = arcpy.management.Project(parameters[0].valueAsText, "p.gpkg", 3857).getOutput(0)
                    parameters[1].value = arcpy.analysis.Buffer(projected, "halo.gpkg", "100 Meters").getOutput(0)
        ''')
        with open(os.path.join(self.directory, "halo.pyt"), "w") as out:
            out.write(toolbox)
        os.makedirs(os.path.join(self.directory, "inputs"))
        with open(os.path.join(self.directory, "inputs", "in_features.geojson"), "w") as out:
            json.dump({"type": "FeatureCollection", "features": [
                {"type": "Feature", "properties": {"name": "a", "n": 1}, "geometry": {"type": "Point", "coordinates": [28.9, 41.0]}},
                {"type": "Feature", "properties": {"name": "b", "n": 2}, "geometry": {"type": "Point", "coordinates": [29.0, 41.1]}}]}, out)
        result = _run(self.directory, {"mode": "run", "toolbox": "halo.pyt", "tool": "Halo",
                                       "inputs": {"in_features": "inputs/in_features.geojson"}}, unfenced=sys.platform != "linux")
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["layers"]["out_features"]["count"], 2)
        with open(os.path.join(self.directory, result["layers"]["out_features"]["geojson"])) as read:
            back = json.load(read)
        self.assertEqual(back["features"][0]["geometry"]["type"], "Polygon")
        self.assertEqual(sorted(f["properties"]["name"] for f in back["features"]), ["a", "b"])
        self.assertLess(abs(back["features"][0]["geometry"]["coordinates"][0][0][0] - 28.9), 0.01)

    def test_a_tool_runs_and_answers_its_output(self):
        result = _run(self.directory, {"mode": "run", "toolbox": "probe.pyt", "tool": "Count",
                                       "parameters": {"in_features": "in.gpkg"}}, unfenced=sys.platform != "linux")
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["outputs"]["count"], "4")
        self.assertIn("4 features", [m["description"] for m in result["messages"]])


@unittest.skipUnless(sys.platform == "linux", "the fence is Linux's Landlock and seccomp; elsewhere tools do not run")
class TheFenceHolds(unittest.TestCase):
    def _hostile(self, what):
        directory = tempfile.mkdtemp()
        with open(os.path.join(directory, "probe.pyt"), "w") as out:
            out.write(TOOLBOX)
        return _run(directory, {"mode": "run", "toolbox": "probe.pyt", "tool": "Hostile", "parameters": {"what": what},
                                "limits": {"memory": 1 << 30}}, unfenced=False)

    def test_the_fence_goes_up(self):
        result = self._hostile("nothing")
        self.assertTrue(result["fence"], result)

    def test_a_socket_is_refused(self):
        result = self._hostile("socket")
        self.assertFalse(result["ok"])
        self.assertIn("PermissionError", result["error"])

    def test_a_file_outside_the_run_is_refused(self):
        self.assertIn("PermissionError", self._hostile("read")["error"])

    def test_the_servers_environment_is_refused(self):
        self.assertIn("PermissionError", self._hostile("environ")["error"])

    def test_memory_past_the_limit_is_refused(self):
        self.assertIn("MemoryError", self._hostile("memory")["error"])

    def test_a_process_outside_the_runtime_is_refused(self):
        self.assertIn("PermissionError", self._hostile("process")["error"])


if __name__ == "__main__":
    unittest.main()
