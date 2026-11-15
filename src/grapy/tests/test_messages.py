"""ADR-289 §5.3: the message model — every answer here measured in ArcGIS Pro 3.7, 2026-11-12, by the Pro sweep's
arcpy_top message cases and the probes beside them. GetMessages and its family answer the last tool's messages, with
the start and end lines Pro writes; a script's own AddMessage prints and joins nothing; the run's list, which the
server's job answers (ADR-223), keeps everything said."""
import contextlib
import io
import os
import re
import sys
import tempfile
import textwrap
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime, _toolboxes  # noqa: E402

TIME = r"(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), [A-Z][a-z]+ \d{1,2}, \d{4} \d{1,2}:\d{2}:\d{2} [AP]M"
START = re.compile(rf"Start Time: {TIME}")
SUCCEEDED = re.compile(rf"Succeeded at {TIME} \(Elapsed Time: \d+\.\d\d seconds\)")
FAILED = re.compile(rf"Failed at {TIME} \(Elapsed Time: \d+\.\d\d seconds\)")

TOOLBOX = textwrap.dedent('''
    import json
    import os

    import grapy as arcpy


    class Toolbox:
        def __init__(self):
            self.label = "Messages"
            self.alias = "msgs"
            self.tools = [Outer, Warns, Fails, Ids]


    class Outer:
        def getParameterInfo(self):
            return [arcpy.Parameter(name="rows", displayName="Rows", datatype="GPString")]

        def execute(self, parameters, messages):
            seen = {}
            arcpy.AddMessage("outer says hi")
            seen["before"] = arcpy.GetAllMessages()
            arcpy.management.GetCount(parameters[0].valueAsText)
            seen["after"] = arcpy.GetAllMessages()
            arcpy.AddReturnMessage(1)
            messages.addMessage("via messages")
            with open(os.environ["GRAPY_TEST_SEEN"], "w") as out:
                json.dump(seen, out)


    class Warns:
        def getParameterInfo(self):
            return []

        def execute(self, parameters, messages):
            arcpy.AddWarning("a warning")
            arcpy.AddMessage("after the warning")


    class Fails:
        def getParameterInfo(self):
            return []

        def execute(self, parameters, messages):
            arcpy.AddMessage("about to fail")
            arcpy.AddError("an error")


    class Ids:
        def getParameterInfo(self):
            return []

        def execute(self, parameters, messages):
            arcpy.AddIDMessage("WARNING", 12, "x")
            arcpy.AddIDMessage("INFORMATIVE", 12, "x")
            arcpy.AddMessage("ERROR 000732: looks like an id")
''')


class Messages(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.mark = len(_runtime.MESSAGES)   # other tests' messages are left alone: some read them later
        _runtime._STACK[0].last = []   # no tool has run yet
        self.pts = os.path.join(self.directory, "pts.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "pts.gpkg", "POINT", spatial_reference=32635)
        with arcpy.da.InsertCursor(self.pts, ["SHAPE@XY"]) as rows:
            for i in range(30):
                rows.insertRow([(500000 + i, 4500000 + i)])
        path = os.path.join(self.directory, "Messages.pyt")
        with open(path, "w", encoding="utf-8") as written:
            written.write(TOOLBOX)
        with contextlib.redirect_stdout(io.StringIO()):
            arcpy.ImportToolbox(path)
        os.environ["GRAPY_TEST_SEEN"] = os.path.join(self.directory, "seen.json")

    def tearDown(self):
        arcpy.SetSeverityLevel(2)
        _toolboxes._SESSION.clear()

    @staticmethod
    def quiet(function, *args):
        """A call with what it prints captured: (answer, printed)."""
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            return function(*args), out.getvalue()

    def test_a_tool_answers_its_start_and_end_lines(self):
        result = arcpy.management.GetCount(self.pts)
        # Pro on a GeoPackage writes no "Row Count" line; it writes one on a file geodatabase, which grapy does not.
        kinds = [m[:2] for m in arcpy.GetAllMessages()]
        self.assertEqual(kinds, [[2, 0], [3, 0]])
        self.assertRegex(arcpy.GetMessage(0), START)
        self.assertRegex(arcpy.GetMessage(1), SUCCEEDED)
        self.assertEqual(arcpy.GetMessages(), arcpy.GetMessage(0) + "\n" + arcpy.GetMessage(1))
        self.assertEqual((arcpy.GetMessageCount(), arcpy.GetMaxSeverity(), arcpy.GetMessages(1), arcpy.GetMessages(2)),
                         (2, 0, "", ""))
        self.assertEqual(arcpy.GetMessage(-1), arcpy.GetMessages())       # Pro answers every message for -1
        # Past the last message Pro raises; before any tool has run it answers "" and 0.
        with self.assertRaisesRegex(RuntimeError, "Error in getting message"):
            arcpy.GetMessage(2)
        with self.assertRaisesRegex(RuntimeError, "Error in getting severity"):
            arcpy.GetSeverity(5)
        with self.assertRaisesRegex(RuntimeError, "Error in getting return code"):
            arcpy.GetReturnCode(-1)
        with self.assertRaisesRegex(RuntimeError, "Error in adding return message"):
            arcpy.AddReturnMessage(5)
        _runtime._STACK[0].last = []
        self.assertEqual((arcpy.GetMessage(1), arcpy.GetSeverity(0), arcpy.GetReturnCode(-1)), ("", 0, 0))
        # The Result keeps the tool's messages, and its inputs: GetCount's one, Buffer's seven but its output.
        arcpy.analysis.Buffer(self.pts, os.path.join(self.directory, "b.gpkg"), "10 Meters")
        self.assertEqual((result.messageCount, result.maxSeverity, result.getSeverity(0)), (2, 0, 0))
        self.assertRegex(result.getMessage(0), START)
        self.assertEqual((result.getMessage(99), result.getMessages(1)), ("", ""))
        self.assertEqual([m[:2] for m in result.getAllMessages()], [[2, 0], [3, 0]])
        self.assertEqual((result.inputCount, result.getInput(0)), (1, self.pts))
        with self.assertRaisesRegex(IndexError, "Error in getting input"):
            result.getInput(1)
        buffered = arcpy.analysis.Buffer(self.pts, os.path.join(self.directory, "c.gpkg"), "10 Meters")
        self.assertEqual((buffered.inputCount, buffered.getInput(1)), (7, "10 Meters"))

    def test_a_scripts_own_message_prints_and_joins_nothing(self):
        arcpy.management.GetCount(self.pts)
        before = arcpy.GetAllMessages()
        _, printed = self.quiet(lambda: (arcpy.AddMessage("hello"), arcpy.AddWarning("careful"),
                                         arcpy.AddError("broken"), arcpy.AddReturnMessage(0)))
        lines = printed.splitlines()
        self.assertEqual(lines[:3], ["hello", "WARNING: careful", "ERROR: broken"])
        self.assertRegex(lines[3], START)
        self.assertEqual(arcpy.GetAllMessages(), before)
        # The run's list, which the server's job answers, keeps each of them (ADR-223).
        said = [m["description"] for m in _runtime.MESSAGES[self.mark:]]
        self.assertEqual(said[-4:-1], ["hello", "careful", "broken"])

    def test_a_failed_tool_leaves_its_errors_and_its_failure_lines(self):
        missing = os.path.join(self.directory, "nothing.gpkg")
        with self.assertRaises(Exception):
            arcpy.management.GetCount(missing)
        said = arcpy.GetAllMessages()
        self.assertEqual(said[0][0], 2)
        self.assertEqual(said[-2], [100, -2147467259, "Failed to execute (GetCount)."])
        self.assertEqual(said[-1][:2], [3, 0])
        self.assertRegex(said[-1][2], FAILED)
        self.assertEqual(arcpy.GetMaxSeverity(), 2)
        self.assertTrue(arcpy.GetMessages(2).endswith("Failed to execute (GetCount).\n"))

    def test_a_script_tool_answers_the_last_tool_it_ran_and_keeps_its_own(self):
        import json
        self.quiet(arcpy.Outer_msgs, self.pts)
        seen = json.load(open(os.environ["GRAPY_TEST_SEEN"], encoding="utf-8"))
        self.assertEqual(seen["before"], [])      # nothing before it runs a tool
        self.assertEqual([m[:2] for m in seen["after"]], [[2, 0], [3, 0]])
        said = arcpy.GetAllMessages()
        self.assertEqual([m[:2] for m in said], [[2, 0], [0, 0], [3, 0], [0, 0], [3, 0]])
        self.assertEqual([said[1][2], said[3][2]], ["outer says hi", "via messages"])
        self.assertEqual(said[2][2], seen["after"][1][2])   # AddReturnMessage keeps the end line's type 3

    def test_warnings_errors_and_their_codes(self):
        self.quiet(arcpy.Warns_msgs)
        self.assertEqual([m[:2] for m in arcpy.GetAllMessages()], [[2, 0], [50, 0], [0, 0], [3, 0]])
        self.assertEqual((arcpy.GetMessages(1), arcpy.GetMessages(2), arcpy.GetMaxSeverity()), ("a warning\n", "", 1))
        with self.assertRaises(arcpy.ExecuteError) as failed:
            self.quiet(arcpy.Fails_msgs)
        self.assertEqual(str(failed.exception), "an error\nFailed to execute (Fails).\n")
        self.assertEqual([arcpy.GetReturnCode(i) for i in range(arcpy.GetMessageCount())], [0, 0, 1, -2147467259, 0])
        self.assertEqual([arcpy.GetSeverity(i) for i in range(arcpy.GetMessageCount())], [0, 0, 2, 2, 0])
        self.assertEqual(arcpy.GetMessages(1), "")
        self.quiet(arcpy.Ids_msgs)
        self.assertEqual([m[:2] for m in arcpy.GetAllMessages()], [[2, 0], [50, 12], [0, 12], [0, 0], [3, 0]])

    def test_severity_levels(self):
        arcpy.SetSeverityLevel(1)
        with self.assertRaises(arcpy.ExecuteWarning) as warned:
            self.quiet(arcpy.Warns_msgs)
        self.assertIsInstance(warned.exception, RuntimeError)
        self.assertEqual(str(warned.exception), "a warning\n")
        self.assertEqual(arcpy.GetMessageCount(), 4)            # the tool ran to its end first
        self.assertEqual(str(arcpy.management.GetCount(self.pts)), "30")
        arcpy.SetSeverityLevel(0)
        failed, _ = self.quiet(arcpy.Fails_msgs)
        self.assertEqual((str(failed), failed.status, failed.outputCount, failed.messageCount, failed.maxSeverity),
                         ("", 4, 0, 5, 2))
        refused = arcpy.management.GetCount(os.path.join(self.directory, "nothing.gpkg"))
        self.assertEqual((str(refused), refused.status, refused.inputCount), ("", 5, 0))
        with self.assertRaises(ValueError):
            arcpy.SetSeverityLevel(3)

    def test_command_answers_the_tools_messages_and_leaves_getmessages_alone(self):
        arcpy.management.GetCount(self.pts)
        before = arcpy.GetAllMessages()
        answer = arcpy.Command(f"GetCount_management {self.pts}").splitlines()
        self.assertEqual(len(answer), 2)
        self.assertRegex(answer[0], START)
        self.assertRegex(answer[1], SUCCEEDED)
        failed = arcpy.Command(f"GetCount_management {os.path.join(self.directory, 'nothing.gpkg')}").splitlines()
        self.assertEqual(failed[-2], "Failed to execute (GetCount).")
        self.assertEqual(arcpy.GetAllMessages(), before)

    def test_a_tool_inside_a_tool_is_not_a_tool_of_its_own(self):
        # Buffer's dissolve uses grapy's other tools; what the script reads is Buffer's lines alone.
        arcpy.analysis.Buffer(self.pts, os.path.join(self.directory, "d.gpkg"), "10 Meters", dissolve_option="ALL")
        self.assertEqual([m[0] for m in arcpy.GetAllMessages()], [2, 3])

    def test_the_times_are_written_as_pro_writes_them(self):
        import time
        stamp = time.mktime((2026, 11, 12, 9, 26, 54, 0, 0, -1))
        self.assertEqual(_runtime._when(stamp), "Thursday, November 12, 2026 9:26:54 AM")
        self.assertEqual(_runtime._when(stamp + 4 * 3600), "Thursday, November 12, 2026 1:26:54 PM")
        self.assertEqual([_runtime._elapsed(s) for s in (0.05, 12.5, 61.5, 125.2)],
                         ["0.05 seconds", "12.50 seconds", "1 minutes 1 seconds", "2 minutes 5 seconds"])


if __name__ == "__main__":
    unittest.main()
