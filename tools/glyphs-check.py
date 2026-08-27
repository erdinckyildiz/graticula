#!/usr/bin/env python3
"""Checks the checked-in glyph ranges against their generator, in two layers.

[ADR-027](../docs/adr/ADR-027-glyphs-and-sprites.md) condition 3
----------------------------------------------------------
*The checked-in ranges are provably the output of the checked-in tool. A regeneration that
changes the bytes should fail something. Today nothing notices, and generated artefacts
drifting from their generator is a matter of time.*

That ADR's §97 names the risk in its own words -- **checked-in binaries drift from their
generator** -- and thirty-one binary files nobody can read are the worst possible place for it.

Why two layers, which is a finding rather than a design preference
------------------------------------------------------------------
The first version of this script regenerated every range and compared bytes. It passed on the
machine it was written on, and **CI failed with 22 of 31 ranges different** -- some the same
size with a different hash, some a different size entirely. The rasteriser is Pillow's, and
Pillow's is FreeType's, and neither promises identical output across *versions*.

**Across platforms it does, which took a container to find out.** The reading at the time was
that the ranges were machine-dependent and could only ever be verified by their author. They
are not: with Pillow, FreeType, numpy and scipy pinned, Linux and Windows agree byte for byte.
What CI had was different *versions*, because it installed whatever `pip` offered that day.

**Layer 1, everywhere: the manifest.** `provenance.json` is written by the generator and holds
a SHA-256 for every range plus the font's. This verifies all of them, in both directions -- a
file that was edited, one that went missing, and one nobody committed. It needs no font stack
and no rasteriser, so it runs in CI and on any clone.

**Layer 2, where the rasteriser matches: regeneration.** If Pillow, FreeType, numpy and
scipy are the versions `provenance.json` records, the generator is run and the bytes are
compared. That is the only thing that proves the files are its *output* rather than merely
unedited.

**Four versions, not the platform.** Regenerating inside `tools/glyphs.Dockerfile` — Linux,
Python 3.12 — reproduces byte-for-byte what Windows and Python 3.10 produced, because the
pinned Pillow, FreeType, numpy and scipy are the same. The 22-of-31 difference that started
this was CI installing whatever `pip` had that day, not the operating system. Anybody can
reproduce the ranges by running that container.

**Where the environment does not match, layer 2 is skipped and says so out loud**, naming what
differs. It does not pass silently: an unverifiable claim reported as verified is the failure
this repository has written down six times.

Usage:  python tools/glyphs-check.py
"""

import filecmp
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GLYPHS = os.path.join(ROOT, "src", "Graticula.Host", "glyphs")
PROVENANCE = os.path.join(GLYPHS, "provenance.json")
GENERATOR = os.path.join(ROOT, "tools", "make-glyphs.py")
FONT = os.path.join(ROOT, "tools", "fonts", "DejaVuSans.ttf")


def digest(path):
    with io.open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def ranges_on_disk():
    """Every .pbf under the glyph folder, keyed by file name."""
    found = {}

    for folder, _, files in os.walk(GLYPHS):
        for name in files:
            if name.endswith(".pbf"):
                found[name] = os.path.join(folder, name)

    return found


def manifest_layer(provenance):
    """Layer 1: the checked-in bytes are the ones the generator last wrote."""
    problems = []
    recorded = provenance.get("ranges", {})
    present = ranges_on_disk()

    if not recorded:
        problems.append(
            "provenance.json records no ranges, so it proves nothing. Run "
            "`python tools/make-glyphs.py`.")
        return problems

    for name, expected in sorted(recorded.items()):
        if name not in present:
            problems.append(
                name + " is in provenance.json and not on disk. A range the style asks for "
                "and the image does not carry is a label that does not draw, and the air-gap "
                "rule forbids fetching it at runtime.")
            continue

        actual = digest(present[name])

        if actual != expected:
            problems.append(
                name + " does not match provenance.json: recorded " + expected[:16]
                + ", on disk " + actual[:16] + " (" + str(os.path.getsize(present[name]))
                + " bytes). Either it was edited or it was regenerated without the manifest "
                "being written. Run `python tools/make-glyphs.py`.")

    for name in sorted(present):
        if name not in recorded:
            problems.append(
                name + " is on disk and not in provenance.json. It came from somewhere other "
                "than the generator, or the manifest is stale.")

    if os.path.exists(FONT):
        font = digest(FONT)

        if font != provenance.get("fontSha256"):
            problems.append(
                "The font has changed since the ranges were generated: provenance.json "
                "records " + str(provenance.get("fontSha256"))[:16] + " and "
                "tools/fonts/DejaVuSans.ttf is " + font[:16] + ". Every range is stale.")

    return problems


def environment():
    """This machine's versions, in provenance.json's shape, or None if it cannot say."""
    try:
        import platform

        import numpy
        import PIL
        import scipy
        from PIL import ImageFont
    except ImportError:
        return None

    return {
        "python": platform.python_version(),
        "system": platform.system(),
        "pillow": PIL.__version__,
        "freetype": ImageFont.core.freetype2_version,
        "numpy": numpy.__version__,
        "scipy": scipy.__version__,
    }


def regeneration_layer(provenance):
    """Layer 2: the bytes are what the generator produces, here and now."""
    problems = []
    fresh = tempfile.mkdtemp(prefix="graticula-glyphs-")

    try:
        run = subprocess.run(
            [sys.executable, GENERATOR, fresh],
            capture_output=True, text=True, cwd=ROOT, timeout=900)

        if run.returncode != 0:
            return ["The generator itself failed:\n" + run.stdout + run.stderr]

        present = ranges_on_disk()

        for name, here in sorted(present.items()):
            relative = os.path.relpath(here, GLYPHS)
            there = os.path.join(fresh, relative)

            if not os.path.exists(there):
                problems.append(
                    relative + " is checked in and the generator does not produce it.")
                continue

            if not filecmp.cmp(here, there, shallow=False):
                problems.append(
                    relative + " differs from what the generator produces: checked in "
                    + digest(here)[:16] + " (" + str(os.path.getsize(here)) + " bytes), "
                    "regenerated " + digest(there)[:16] + " ("
                    + str(os.path.getsize(there)) + " bytes).")

        return problems

    finally:
        shutil.rmtree(fresh, ignore_errors=True)


def main():
    if not os.path.exists(PROVENANCE):
        print(
            "There is no " + os.path.relpath(PROVENANCE, ROOT) + ", so nothing can say where "
            "the checked-in glyph ranges came from. Run `python tools/make-glyphs.py`.",
            file=sys.stderr)
        return 2

    provenance = json.load(io.open(PROVENANCE, encoding="utf-8"))

    problems = manifest_layer(provenance)

    if problems:
        for problem in problems:
            print(problem, file=sys.stderr)

        print("\n" + str(len(problems)) + " glyph ranges disagree with their manifest "
              "(ADR-027 condition 3).", file=sys.stderr)
        return 1

    print(str(len(provenance.get("ranges", {})))
          + " glyph ranges match provenance.json, and so does the font.")

    here = environment()
    recorded = provenance.get("environment", {})

    if here is None:
        print(
            "Not regenerating: numpy, Pillow or scipy is not installed here, so this run "
            "proves the bytes are unedited and does not prove they are the generator's "
            "output. `python -m pip install numpy pillow scipy` on a machine matching "
            + json.dumps(recorded, sort_keys=True) + ".")
        return 0

    # <b>The four that decide the bytes, and not the two that do not — D-193.</b> This
    # compared every recorded field, including `python` and `system`, on the belief that the
    # ranges were platform-dependent. Measured 2026-08-27 by regenerating inside a container
    # with the versions pinned: **Windows/Python 3.10 and Linux/Python 3.12 produce
    # byte-identical ranges** when Pillow, FreeType, numpy and scipy match. So the platform
    # was never the variable; CI's `pip install numpy pillow scipy` fetching whatever was
    # current was. Comparing the platform refused to verify runs that would have verified.
    #
    # `python` and `system` are still recorded, because a difference in the bytes with these
    # four equal would be a finding and the record is where it would be seen.
    # <b>These four, and they are necessary rather than sufficient -- D-193.</b> Matching
    # them is what makes a byte comparison *worth attempting*; it is not what makes it agree.
    # Measured 2026-08-27: a container with these four pinned reproduces the ranges
    # byte-for-byte from a Windows original, and a GitHub runner with the same four differs in
    # 22 of 31. So something below the version strings decides the bytes -- the CPU numpy and
    # scipy compile for is the open suspect -- and a run that gets here and disagrees is a
    # finding rather than a broken check.
    decides = ("pillow", "freetype", "numpy", "scipy")

    differences = sorted(
        key for key in decides if recorded.get(key) != here.get(key))

    if differences:
        print(
            "Not regenerating: this machine is not the one that produced these ranges, and "
            "the rasteriser does not promise identical bytes across "
            + ", ".join(differences) + ". Recorded "
            + json.dumps({k: recorded.get(k) for k in differences}, sort_keys=True)
            + ", here " + json.dumps({k: here.get(k) for k in differences}, sort_keys=True)
            + ". Measured 2026-08-27: 22 of 31 ranges differ between Windows and Linux, so a "
            "byte comparison from here would report drift that is not drift.")
        return 0

    problems = regeneration_layer(provenance)

    if problems:
        for problem in problems:
            print(problem, file=sys.stderr)

        print("\n" + str(len(problems)) + " glyph ranges are not what the generator produces "
              "on the machine that produced them, which is drift (ADR-027 condition 3). Run "
              "`python tools/make-glyphs.py` and commit the result, or find out why the "
              "generator changed.", file=sys.stderr)
        return 1

    print("and regenerating them here produces the same bytes: this environment is the one "
          "provenance.json records.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
