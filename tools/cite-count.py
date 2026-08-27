#!/usr/bin/env python3
"""Count an EARL report's outcomes, and compare them with the recorded baseline.

Why this counts rather than reads
---------------------------------
[D-158](../docs/architecture-debt.md): the two hand runs of these suites left evidence
files rather than a sentence, and nothing re-runs either. A report is thousands of lines
of RDF; what a build needs from it is three numbers and whether they got worse.

**Worse, not different.** The recorded note on the 2026-08-26 run is explicit that scope
is not comparable between runs -- 420/0/390 one day and 395/2/360 another, because a
different number of assertions was attempted. So a total that moves is not a regression
and this does not treat it as one. **A failure is.** The baseline records how many
failures each suite had when a person last looked at them and decided they were
acceptable; more than that is a regression and fails the build.

Usage:  tools/cite-count.py <report.rdf> [--baseline tools/cite-baselines.json]
"""

import io
import json
import os
import re
import sys

OUTCOME = re.compile(r'earl:outcome\s+rdf:resource="[^"]*#(passed|failed|untested|inapplicable|cantTell)"')

HERE = os.path.dirname(os.path.abspath(__file__))
BASELINES = os.path.join(HERE, "cite-baselines.json")


def counts(path):
    text = io.open(path, encoding="utf-8", errors="replace").read()

    tally = {}

    for outcome in OUTCOME.findall(text):
        tally[outcome] = tally.get(outcome, 0) + 1

    return tally


def main(argv):
    if not argv:
        print(__doc__)
        return 2

    path = argv[0]
    baselines = argv[2] if len(argv) > 2 and argv[1] == "--baseline" else BASELINES

    suite = os.path.splitext(os.path.basename(path))[0]
    tally = counts(path)

    if not tally:
        print(f"{suite}: the report has no earl:outcome at all, so the run did not happen "
              f"or the file is not an EARL report ({os.path.getsize(path)} bytes).")
        return 1

    passed = tally.get("passed", 0)
    failed = tally.get("failed", 0)
    untested = tally.get("untested", 0)

    print(f"{suite}: {passed} passed, {failed} failed, {untested} untested "
          f"({sum(tally.values())} assertions)")

    try:
        recorded = json.load(io.open(baselines, encoding="utf-8"))
    except OSError:
        print(f"  no baseline file at {baselines}; nothing to compare with.")
        return 0

    if suite not in recorded:
        print(f"  {suite} has no baseline. Add one to {os.path.basename(baselines)} "
              "with the run that established it, or this suite proves nothing over time.")
        return 1

    allowed = recorded[suite]["failed"]

    if failed > allowed:
        print(f"  REGRESSION: {failed} failures against a baseline of {allowed} "
              f"({recorded[suite]['run']}). The totals are allowed to move -- scope is not "
              "comparable between runs -- but a failure is a failure.")
        return 1

    if failed < allowed:
        print(f"  {allowed - failed} fewer failures than the baseline of {allowed}. "
              "If that is real, lower the baseline in the same commit that earns it.")

    print(f"  within the baseline of {allowed} ({recorded[suite]['run']}).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
