"""Which of arcpy's names grapy refuses with a reason, and which it has not examined — ADR-224 condition 4.

``grapy_surface.py`` counts what is built from grapy's source. A name that is not built is either **refused**, with the
reason its module's ``__getattr__`` gives (an undocumented algorithm, Esri's data, a data model this server does not
hold …), or **unexamined**, answering only the generic *"is not built in grapy yet."* Telling the two apart means asking
grapy, so this imports it and needs its wheels; the answer is written to ``tools/grapy-refusals.json``, which the status
page reads without them.

    python tools/grapy_refusals.py            # rewrite the file
    python tools/grapy_refusals.py --check    # fail when the file is not what grapy answers now (CI's grapy job)
"""
import importlib
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "src", "grapy"))
sys.path.insert(0, os.path.join(ROOT, "tools"))

import grapy_surface  # noqa: E402

TARGET = os.path.join(ROOT, "tools", "grapy-refusals.json")


def _answer(module, name):
    """'refused' with the reason, or 'unexamined', for one name grapy does not build."""
    import grapy
    from grapy._runtime import NotBuiltError
    target = grapy if module == "arcpy" else importlib.import_module("grapy." + module.split(".", 1)[1])
    try:
        found = getattr(target, name)
        if callable(found):
            found()
    except NotBuiltError as said:
        text = str(said)
        return ("unexamined", None) if text.rstrip().endswith("is not built in grapy yet.") else ("refused", text)
    except Exception:  # noqa: BLE001 — a name that answers anything else is built under another form
        return "built", None
    return "built", None


def _equivalents(module):
    """The built names marked ``_runtime.equivalent`` (ADR-224 §5.6), with the method and source each names."""
    import grapy
    target = grapy if module == "arcpy" else importlib.import_module("grapy." + module.split(".", 1)[1])
    found = {}
    for name in dir(target):
        marked = getattr(getattr(target, name, None), "__grapy_equivalent__", None)
        if marked and not name.startswith("_"):
            found[name] = marked
    return found


def survey():
    out = {}
    for module, built, of, missing in grapy_surface.surface():
        refused, unexamined = {}, []
        for name in missing:
            state, reason = _answer(module, name)
            if state == "refused":
                refused[name] = reason
            elif state == "unexamined":
                unexamined.append(name)
        out[module] = {"built": built, "of": of, "equivalents": _equivalents(module), "refused": refused,
                       "unexamined": sorted(unexamined)}
    return out


if __name__ == "__main__":
    now = survey()
    text = json.dumps(now, indent=1, ensure_ascii=False, sort_keys=True) + "\n"
    if "--check" in sys.argv:
        kept = open(TARGET, encoding="utf-8").read() if os.path.exists(TARGET) else ""
        if kept != text:
            sys.exit("tools/grapy-refusals.json is not what grapy answers now: run python tools/grapy_refusals.py")
        print("grapy-refusals.json is current")
    else:
        open(TARGET, "w", encoding="utf-8", newline="\n").write(text)
    for module, row in now.items():
        print(f"  {module:<18} built {row['built']:>4} (equivalent {len(row['equivalents']):>3})  refused {len(row['refused']):>4}  unexamined {len(row['unexamined']):>4}  of {row['of']}")
