#!/usr/bin/env python3
"""Extracts every ADR condition and reports how many are discharged.

Why this is a tool
------------------
CLAUDE.md carried "the ADR conditions, roughly twenty-five, none discharged"
into Phase 1. Both halves of that sentence were guesses, and by 2026-08-15 the
count was wrong by more than double while several had quietly been met by
shipped code. A number nobody can reproduce is a number nobody maintains.

A condition counts as discharged when its text is struck through (`~~`) or opens
with a DISCHARGED marker. That convention is the only thing this relies on, and
it is stated here so it can be followed rather than guessed at.

Usage:  python tools/conditions.py [--list]
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ADRS = os.path.join(ROOT, "docs", "adr")


def conditions(text):
    """Numbered items under a Conditions heading, with their first line."""
    section = re.search(r"^##\s*\d*\.?\s*Conditions\b(.*?)(^##\s|\Z)", text, re.M | re.S)

    if not section:
        return []

    found = []

    for match in re.finditer(r"^(\d+)\.\s+(.*?)(?=^\d+\.\s|\Z)", section.group(1), re.M | re.S):
        body = match.group(2).strip()
        first = " ".join(body.split())[:150]
        done = body.lstrip().startswith("~~") or "DISCHARGED" in body[:200].upper()
        found.append((int(match.group(1)), first, done))

    return found


def main():
    show = "--list" in sys.argv
    total = 0
    done = 0

    for name in sorted(os.listdir(ADRS)):
        if not name.startswith("ADR-") or not name.endswith(".md"):
            continue

        text = io.open(os.path.join(ADRS, name), encoding="utf-8").read()
        items = conditions(text)

        if not items:
            continue

        met = sum(1 for _, _, d in items if d)
        total += len(items)
        done += met

        marker = "" if met == 0 else f"  ({met} discharged)"
        print(f"{name.split('-')[0]}-{name.split('-')[1]}: {len(items)} conditions{marker}")

        if show:
            for number, first, is_done in items:
                print(f"    {'x' if is_done else ' '} {number}. {first}")

    print()
    print(f"{done} of {total} ADR conditions discharged "
          f"({100 * done / total:.0f}%)" if total else "no conditions found")


if __name__ == "__main__":
    main()
