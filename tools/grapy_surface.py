"""grapy's surface counted against arcpy's — ADR-223 condition 6.

arcpy's names are tools/arcpy-surface.json, read from an installed arcpy (its `about` says which). grapy's are read from
its source with `ast`, so this needs none of grapy's wheels: a module-level `def` or `class` whose name does not begin
with `_`, and in the package itself the names it imports from its private modules. A name grapy answers only through a
module `__getattr__` is one it refuses (NotBuiltError), and is not counted.

    python tools/grapy_surface.py            # one line per module
    python tools/grapy_surface.py --missing management
"""
import ast
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GRAPY = os.path.join(ROOT, "src", "grapy", "grapy")


def _defined(path, imports=False):
    if not os.path.isfile(path):
        return set()
    tree = ast.parse(open(path, encoding="utf-8").read())
    names = set()
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.ClassDef)) and not node.name.startswith("_"):
            names.add(node.name)
        elif imports and isinstance(node, ast.ImportFrom) and node.level == 1 and (node.module or "").startswith("_"):
            names.update(a.asname or a.name for a in node.names if not (a.asname or a.name).startswith("_"))
    return names


def surface():
    """[(module, built, of, missing)] for each arcpy module the reference lists, in its order."""
    reference = json.load(open(os.path.join(ROOT, "tools", "arcpy-surface.json"), encoding="utf-8"))
    out = []
    for module, names in reference["modules"].items():
        if module == "arcpy":
            have = _defined(os.path.join(GRAPY, "__init__.py"), imports=True)
        else:
            have = _defined(os.path.join(GRAPY, module.split(".", 1)[1] + ".py"))
        wanted = set(names)
        out.append((module, len(wanted & have), len(wanted), sorted(wanted - have)))
    return out


def version():
    return json.load(open(os.path.join(ROOT, "tools", "arcpy-surface.json"), encoding="utf-8"))["version"]


if __name__ == "__main__":
    rows = surface()
    if len(sys.argv) == 3 and sys.argv[1] == "--missing":
        for module, _, _, missing in rows:
            if module.endswith(sys.argv[2]):
                print("\n".join(missing))
        sys.exit(0)
    print(f"grapy against arcpy {version()}:")
    for module, built, of, _ in rows:
        print(f"  {module:<18} {built:>4} of {of}")
    print(f"  {'all':<18} {sum(r[1] for r in rows):>4} of {sum(r[2] for r in rows)}")
