#!/usr/bin/env python3
"""Extracts every ADR condition and reports how many are discharged.

Why this is a tool
------------------
CLAUDE.md carried "the ADR conditions, roughly twenty-five, none discharged"
into Phase 1. Both halves of that sentence were guesses, and by 2026-08-15 the
count was wrong by more than double while several had quietly been met by
shipped code. A number nobody can reproduce is a number nobody maintains.

A condition counts as discharged when it carries an emphasised marker anywhere in
its text: `**DISCHARGED` or `*(Discharged`. Both forms are already in use and
both are accepted; the emphasis is what makes it a marker rather than a word.

**The rule used to be "struck through, or DISCHARGED in the first 200
characters", and it undercounted.** Conditions in this project run to a
paragraph, so the note saying one was met naturally lands at the end — past the
window. Three conditions were discharged, said so, and were counted as open
(2026-08-15). A convention that only works for short text is a convention that
stops working exactly as a project matures.

Two things are deliberately *not* discharged, and the marker requirement is what
separates them from a false positive:

- **`PARTLY DISCHARGED`** is its own state. Half a condition met is an open
  condition with progress, and counting it as closed is how a register starts
  lying.
- **prose.** "found while discharging condition 1" is a sentence about another
  condition, and "is not discharged" says the opposite. Neither carries the
  emphasis marker, which is why the marker is required rather than the word.

Usage:  python tools/conditions.py [--list]
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ADRS = os.path.join(ROOT, "docs", "adr")


def deferred(body):
    """Whether a condition is deferred with a decision nobody is implementing.

    <b>A third state, and the register was lying without it.</b> A condition on
    a decision that v1 removed is not outstanding work — counting it beside one
    that is makes the pile look larger than it is and, worse, makes the two
    indistinguishable to whoever is deciding what to do next. Marked per item
    rather than per section so that a decision can be partly deferred, and in
    the same emphasised-marker shape as a discharge so there is one convention
    to remember.
    """
    return re.search(r"(\*\*|\*\()\s*DEFERRED", body, re.IGNORECASE) is not None


def discharged(body):
    """Whether a condition carries a discharge marker. See the module docstring."""
    if body.lstrip().startswith("~~"):
        return True

    for match in re.finditer(r"(\*\*|\*\()\s*DISCHARGED", body, re.IGNORECASE):
        # PARTLY DISCHARGED is a state of its own and is not this one.
        before = body[max(0, match.start() - 12):match.start()].upper()

        if "PARTLY" in before or "PARTIALLY" in before:
            continue

        return True

    return False


def conditions(text):
    """Numbered items under a Conditions heading, with their first line."""
    section = re.search(r"^##\s*\d*\.?\s*Conditions\b(.*?)(^##\s|\Z)", text, re.M | re.S)

    if not section:
        return []

    found = []

    for match in re.finditer(r"^(\d+)\.\s+(.*?)(?=^\d+\.\s|\Z)", section.group(1), re.M | re.S):
        body = match.group(2).strip()
        first = " ".join(body.split())[:150]
        done = discharged(body)
        found.append((int(match.group(1)), first, done, deferred(body)))

    return found


def main():
    show = "--list" in sys.argv
    total = 0
    done = 0
    postponed = 0

    for name in sorted(os.listdir(ADRS)):
        if not name.startswith("ADR-") or not name.endswith(".md"):
            continue

        text = io.open(os.path.join(ADRS, name), encoding="utf-8").read()
        items = conditions(text)

        if not items:
            continue

        met = sum(1 for _, _, d, _ in items if d)
        put_off = sum(1 for _, _, d, f in items if f and not d)
        total += len(items)
        done += met
        postponed += put_off

        marker = "".join([
            f"  ({met} discharged)" if met else "",
            f"  ({put_off} deferred)" if put_off else "",
        ])
        print(f"{name.split('-')[0]}-{name.split('-')[1]}: {len(items)} conditions{marker}")

        if show:
            for number, first, is_done, is_deferred in items:
                mark = "x" if is_done else "~" if is_deferred else " "
                print(f"    {mark} {number}. {first}")

    print()
    live = total - done - postponed

    print(f"{done} discharged, {postponed} deferred with their decision, "
          f"{live} live — of {total} ADR conditions" if total else "no conditions found")


if __name__ == "__main__":
    main()
