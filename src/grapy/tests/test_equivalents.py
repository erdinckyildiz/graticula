"""ADR-224 condition 6: every equivalent is counted apart, names its method and source, says so in its docstring and
says so in its run."""
import importlib
import json
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime  # noqa: E402

SURFACE = os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools", "arcpy-surface.json")


def built_names():
    modules = json.load(open(SURFACE, encoding="utf-8"))["modules"]
    for module, names in modules.items():
        target = arcpy if module == "arcpy" else importlib.import_module("grapy." + module.split(".", 1)[1])
        for name in names:
            try:
                yield f"{module}.{name}", getattr(target, name)
            except Exception:  # noqa: BLE001 — a refused name raises NotBuiltError
                continue


class Equivalents(unittest.TestCase):
    def test_every_equivalent_names_its_method_and_source_and_its_docstring_says_so(self):
        marked = 0
        for qualified, tool in built_names():
            tag = getattr(tool, "__grapy_equivalent__", None)
            doc = (getattr(tool, "__doc__", None) or "").lstrip()
            if tag is None:
                self.assertFalse(doc.upper().startswith("EQUIVALENT"), f"{qualified} says EQUIVALENT but is not marked")
                continue
            marked += 1
            method, _, source = tag.rpartition(" (")
            self.assertTrue(method.strip() and source.strip(" )"), qualified)
            self.assertTrue(doc.upper().startswith("EQUIVALENT"), qualified)
        self.assertGreater(marked, 200)

    def test_an_equivalent_says_so_once_in_its_run(self):
        tool = arcpy.CreateRandomValueGenerator
        _runtime._SAID_EQUIVALENT.discard(tool.__wrapped__.__qualname__)
        mark = len(_runtime.MESSAGES)
        tool(1, "MERSENNE_TWISTER")
        tool(2, "MERSENNE_TWISTER")
        said = [m["description"] for m in _runtime.MESSAGES[mark:] if "is an equivalent" in m["description"]]
        self.assertEqual(len(said), 1)
        self.assertIn(tool.__grapy_equivalent__, said[0])


if __name__ == "__main__":
    unittest.main()
