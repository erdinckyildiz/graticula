"""ADR-052 condition 2: every property in §3.2's table is checked against the published CIM specification.

    python tools/cim-spec-check.py [commit]

Reads the table in docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md, fetches Esri's published
specification (github.com/Esri/cim-spec, docs/v3, at a pinned commit) and reports each type the specification does
not define and each property it does not give that type, its base types' properties included. It exits non-zero on
any. A property written as `groups[].classes[]` or `values[].fieldValues` is checked by its first name, the one the
type itself carries.

**The specification is fetched, not copied here.** It is licensed CC BY-ND 4.0: implementing it is free, and
redistributing it is allowed only unmodified. Putting a copy in this repository is a licensing decision for the
owner (DEPENDENCY-LICENSES.md), and reading it where it is published needs none.
"""
import json
import re
import sys
import urllib.request

PINNED = "740cc791df1a23fd3ce553296dfd12c7672880c1"  # main on 2026-10-15
ADR = "docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md"
API = "https://api.github.com/repos/Esri/cim-spec/contents/docs/v3?ref={}"
RAW = "https://raw.githubusercontent.com/Esri/cim-spec/{}/docs/v3/{}"


def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "graticula-cim-spec-check"})) as r:
        return r.read().decode("utf-8")


def specification(commit):
    """Every `## Type` in the specification, with the property names of every table under it."""
    types = {}
    files = [e["name"] for e in json.loads(fetch(API.format(commit))) if e["name"].endswith(".md")]

    for name in files:
        current = None

        for line in fetch(RAW.format(commit, name)).splitlines():
            heading = re.match(r"^## (CIM\w+)\s*$", line)

            if heading:
                current = types.setdefault(heading.group(1), set())
                continue

            row = re.match(r"^\|\s*(\w+)\s*\|", line)

            if current is not None and row and row.group(1) not in ("Property",):
                current.add(row.group(1))

    return types


def table():
    """§3.2's rows: the types each names and the properties it says are read."""
    text = open(ADR, encoding="utf-8").read()
    section = text[text.index("### 3.2 "):]
    section = section[:section.index("\n### ", 10)]
    rows = []

    for line in section.splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]

        if len(cells) < 2 or not cells[0].startswith("`CIM"):
            continue

        types = re.findall(r"`(CIM\w+)`", cells[0])
        properties = [re.split(r"[\[.]", p)[0] for p in re.findall(r"`([^`]+)`", cells[1].split(" — ")[0])]
        rows.append((types, properties))

    return rows


def main():
    commit = sys.argv[1] if len(sys.argv) > 1 else PINNED
    known = specification(commit)
    problems = []
    checked = 0

    for types, properties in table():
        for kind in types:
            if kind not in known:
                problems.append(f"{kind}: not a type the specification defines")
                continue

            for prop in properties:
                checked += 1

                if prop not in known[kind]:
                    problems.append(f"{kind}.{prop}: not a property the specification gives {kind}")

    print(f"cim-spec {commit[:12]}: {len(known)} types read; {checked} properties in ADR-052 §3.2 checked")

    for problem in problems:
        print("  " + problem)

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
