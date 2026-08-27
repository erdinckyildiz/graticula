#!/usr/bin/env python3
"""Proves the checked-in glyph ranges are the output of the checked-in generator.

[ADR-027](../docs/adr/ADR-027-glyph-ranges.md) condition 3
----------------------------------------------------------
*The checked-in ranges are provably the output of the checked-in tool. A regeneration that
changes the bytes should fail something. Today nothing notices, and generated artefacts
drifting from their generator is a matter of time.*

That ADR's §97 already names the risk in its own words -- **checked-in binaries drift from
their generator** -- and until this script nothing in the repository could tell the difference
between a range file that came out of `make-glyphs.py` and one that came from somewhere else.
Thirty-one binary files nobody can read are the worst possible place for that.

How it proves it
----------------
By running the generator. A manifest of hashes would prove the files have not been *edited*,
which is a different and weaker claim: it says nothing about whether the generator would still
produce them. This regenerates every range into a temporary directory from the checked-in font
and compares the bytes.

Measured 2026-08-27: 31 ranges, 4306 KB, and every one byte-identical -- so the generator is
deterministic, which had never been asserted anywhere either.

Why it fails rather than skips when the libraries are missing
-------------------------------------------------------------
`numpy`, `Pillow` and `scipy` are build-time dependencies of the generator and are not
otherwise needed to run this server. A check that goes green because its subject could not be
loaded is worse than no check -- this repository has written that trap four times -- so this
says what to install and exits non-zero. Run it where the generator can run.

Usage:  python tools/glyphs-check.py
"""

import filecmp
import hashlib
import io
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CHECKED = os.path.join(ROOT, "src", "Graticula.Host", "glyphs")
GENERATOR = os.path.join(ROOT, "tools", "make-glyphs.py")


def digest(path):
    with io.open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def main():
    try:
        import numpy  # noqa: F401
        import scipy  # noqa: F401
        from PIL import ImageFont  # noqa: F401
    except ImportError as missing:
        print(
            "The glyph generator needs numpy, Pillow and scipy, and one of them is not "
            "installed here: " + str(missing) + ".\n"
            "This check FAILS rather than skips, because a check that goes green when its "
            "subject cannot be loaded is worse than no check.\n"
            "  python -m pip install numpy pillow scipy",
            file=sys.stderr)
        return 2

    if not os.path.isdir(CHECKED):
        print("There are no checked-in glyph ranges at " + CHECKED, file=sys.stderr)
        return 2

    fresh = tempfile.mkdtemp(prefix="graticula-glyphs-")

    try:
        run = subprocess.run(
            [sys.executable, GENERATOR, fresh],
            capture_output=True, text=True, cwd=ROOT, timeout=900)

        if run.returncode != 0:
            print("The generator itself failed:\n" + run.stdout + run.stderr, file=sys.stderr)
            return 2

        problems = []
        checked_files = 0

        for folder, _, files in os.walk(CHECKED):
            for name in sorted(files):
                here = os.path.join(folder, name)
                relative = os.path.relpath(here, CHECKED)
                there = os.path.join(fresh, relative)
                checked_files += 1

                if not os.path.exists(there):
                    problems.append(
                        relative + " is checked in and the generator does not produce it. "
                        "Either it was added by hand or the generator's range list changed "
                        "without the files being regenerated.")
                    continue

                if not filecmp.cmp(here, there, shallow=False):
                    problems.append(
                        relative + " differs from what the generator produces: checked in "
                        + digest(here)[:16] + " (" + str(os.path.getsize(here)) + " bytes), "
                        "regenerated " + digest(there)[:16] + " ("
                        + str(os.path.getsize(there)) + " bytes). Run "
                        "`python tools/make-glyphs.py` and commit the result, or find out why "
                        "the generator changed.")

        # The other direction: something the generator makes and nobody checked in.
        for folder, _, files in os.walk(fresh):
            for name in sorted(files):
                relative = os.path.relpath(os.path.join(folder, name), fresh)

                if not os.path.exists(os.path.join(CHECKED, relative)):
                    problems.append(
                        relative + " is produced by the generator and is not checked in. A "
                        "range the style asks for and the image does not carry is a label "
                        "that does not draw, and Q-15 forbids fetching it at runtime.")

        if problems:
            for problem in problems:
                print(problem, file=sys.stderr)

            print("\n" + str(len(problems)) + " glyph ranges disagree with their generator "
                  "(ADR-027 condition 3).", file=sys.stderr)
            return 1

        print(str(checked_files) + " glyph ranges are byte-identical to what "
              "tools/make-glyphs.py produces from the checked-in font.")
        return 0

    finally:
        shutil.rmtree(fresh, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
