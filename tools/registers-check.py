#!/usr/bin/env python3
"""Checks that the committed status page still matches the registers.

**A diff would have been the obvious check and it does not work.** The status
page stamps the commit it was generated from and the minute it was generated, so
regenerating it and running `git diff --exit-code` reports a change on every run
whatever the registers say. The first version of the CI workflow did exactly
that and would have failed permanently.

So this compares the *numbers* instead. They are the reason the page exists, and
they are what goes stale when somebody edits a register and forgets to
regenerate. The timestamp going stale is not interesting; the count of
discharged conditions going stale is the thing CLAUDE.md added `conditions.py`
to prevent.

**Two further checks were added 2026-08-16, both for failures that had already
happened rather than for failures somebody imagined.** A register whose links
point at deleted files, and a live count written by hand into CLAUDE.md. See
`broken_links` and `remembered_numbers` for what each one caught.

Exits non-zero with the mismatch named. Run from the repository root.
"""

import io
import os
import re
import sys
import urllib.parse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import conditions


def duplicate_debt_ids():
    """Debt entries that reuse a number another entry already has.

    **Twice in one day, which is what makes this worth a tool.** D-29 and D-30
    were each used for two unrelated entries, and then D-36 was -- and that one
    was caught only because a workflow comment happened to reference the first.
    A debt number is how one document points at another: deployment.md and a CI
    comment both cite D-36, so a reused number silently redirects a reader to
    the wrong entry.

    Struck-through and closed rows count. A closed entry still owns its number,
    because the references to it do not disappear when it is paid.
    """
    seen = {}
    repeated = []

    try:
        text = io.open("docs/architecture-debt.md", encoding="utf-8").read()
    except OSError as problem:
        return ["docs/architecture-debt.md could not be read: " + str(problem)]

    for line in text.splitlines():
        match = re.match(r"^\|\s*~*\s*(D-\d+)\s*~*\s*\|\s*(.{0,70})", line)

        if not match:
            continue

        number = match.group(1)
        opening = match.group(2).strip()

        if number in seen:
            repeated.append(
                number + " is used twice:"
                + "\n    1. " + seen[number]
                + "\n    2. " + opening
                + "\n  A debt number is a cross-reference target. Give the newer entry the "
                  "next free number; the older one keeps its own, because the documents "
                  "citing it do not change when it is renumbered."
            )
        else:
            seen[number] = opening

    return repeated


LINK = re.compile(r"\[[^\]\n]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")

# Every document that cites another one. Not a walk of the whole repository: the
# registers and the ADRs are where a citation carries an argument, and those are
# the links whose rotting matters.
LINKING = ("CLAUDE.md", "README.md", "DEPENDENCY-LICENSES.md")


def documents():
    """The markdown files whose links are checked, repository-relative."""
    found = []

    for base, dirs, files in os.walk(os.path.join(conditions.ROOT, "docs")):
        dirs[:] = [d for d in dirs if not d.startswith(".")]
        found.extend(os.path.join(base, f) for f in files if f.endswith(".md"))

    for name in LINKING:
        path = os.path.join(conditions.ROOT, name)

        if os.path.exists(path):
            found.append(path)

    return sorted(found)


def broken_links():
    """Relative markdown links whose target does not exist.

    **Twenty-five of them on the day this was written, and twenty pointed at the
    same two deleted files.** Two research documents were removed from the
    working tree, and with them the stated evidence for four decided register
    answers and for six citations inside an ADR. Nothing failed. The registers
    went on presenting those answers as sourced, and the source was gone.

    That is worse than a dead hyperlink. This project's rule is that a decision
    carries its evidence, so a citation that resolves to nothing is a decision
    that has quietly become an assertion -- and the four in question were
    load-bearing enough that two of them are owner decisions.

    The remaining five were ordinary path bugs of the kind proofreading does not
    catch: one `../` missing, one ADR renamed, one link written from the wrong
    directory. Cheap to fix, invisible without a tool.

    Anchors are ignored. `#L42` into a source file is a line that may legitimately
    move; the file existing is the part worth gating on.
    """
    problems = []

    for path in documents():
        try:
            text = io.open(path, encoding="utf-8").read()
        except OSError as problem:
            problems.append(f"{path} could not be read: {problem}")
            continue

        for number, line in enumerate(text.splitlines(), 1):
            for href in LINK.findall(line):
                # Absolute URLs, mailto:, and same-document anchors.
                if href.startswith("#") or re.match(r"^[a-z][a-z0-9+.-]*:", href):
                    continue

                target = urllib.parse.unquote(href.split("#")[0])

                if not target:
                    continue

                resolved = os.path.normpath(os.path.join(os.path.dirname(path), target))

                if not os.path.exists(resolved):
                    where = os.path.relpath(path, conditions.ROOT).replace("\\", "/")
                    problems.append(f"{where}:{number} links to {target}, which does not exist")

    return problems


def remembered_numbers():
    """Live counts written by hand into CLAUDE.md.

    **CLAUDE.md said the condition count was 69 with 15 discharged, and said none
    of the nine review gates had run.** On 2026-08-16 `conditions.py` said 103
    and 29, and the completeness matrix said five of nine gates had run with three
    of them failing. Both sentences were true when written and neither was true
    any more, and the cost is specific: CLAUDE.md is read at the start of every
    session, so a stale fact there is repeated all day before anybody checks it.

    CLAUDE.md already contains the rule this enforces -- *it is re-run rather
    than remembered* -- which it was breaking in its own next clause.

    **Dated history is deliberately still allowed.** Section 2's account of the
    count being 22 of 99 when the truth was 24 is a record of an event, not a
    claim about the present, and deleting it to satisfy a checker would destroy
    the reason the tool exists. So the patterns below match a *present-tense
    tally* -- a number beside "discharged", and a number over the nine gates --
    and not a number in a sentence about what happened.
    """
    path = os.path.join(conditions.ROOT, "CLAUDE.md")

    try:
        text = io.open(path, encoding="utf-8").read()
    except OSError as problem:
        return [f"CLAUDE.md could not be read: {problem}"]

    problems = []
    flat = " ".join(text.split())

    for match in re.finditer(r"\d+\s*(?:of|/)\s*9\b", flat):
        problems.append(
            f'CLAUDE.md counts the review gates itself: "{match.group(0)}". '
            "The §66 table in docs/architecture-completeness.md is the one place "
            "that number is maintained; cite it instead of restating it."
        )

    for match in re.finditer(r"discharged", flat, re.IGNORECASE):
        window = flat[max(0, match.start() - 60):match.end() + 60]

        if re.search(r"\d", window):
            problems.append(
                f'CLAUDE.md states a condition tally: "...{window.strip()}...". '
                "Run tools/conditions.py for the number and let the status page "
                "carry it; a count in CLAUDE.md goes stale the next time an ADR "
                "is written."
            )

    return problems


def the_former_product_name():
    """`gis-server` used as a live name rather than as history.

    **ADR-032 condition 4.** The product was named Graticula on 2026-08-17, replacing
    the working title. A rename is only finished when nothing keeps saying the old
    name, and 26 documents carried it -- so this is checked rather than remembered,
    exactly like the tally check above.

    **What is deliberately allowed**, because a repository that cannot record its own
    history is worse than one with a stale name:

    · A sentence that says the name is former -- the words *working title*, *renamed*,
      *was called*, *formerly*, *until 2026-08-17* near the mention.
    · Identifiers. The `gisserver` schema, and container paths that predate the rename,
      deployment names rather than the product's, and ADR-032 5 keeps the schema out of
      this on purpose: renaming it would mean a data migration for nothing an operator
      can see.
    · This file, and the ADR that decided the rename.

    So the pattern is the hyphenated product name in prose, and the escape is to say
    what it was.
    """
    allowed = {
        os.path.join("docs", "adr", "ADR-032-the-product-is-named-graticula.md"),

        # <b>The owner's brief, and it is an input rather than our prose.</b> It is
        # dated, it is what the project was asked to build, and a repository that
        # rewrites its own brief to match a later decision loses the ability to show
        # what was asked for. ADR-032 is where the name changed; this is where the
        # request came from.
        "MASTER_GIS_PLATFORM_PROMPT.md",
    }

    excuses = (
        "working title", "former", "formerly", "renamed", "was called", "used to be",
        "until 2026-08-17", "replaced by graticula", "no longer",
    )

    problems = []

    for folder, _, names in os.walk(conditions.ROOT):
        if any(part in folder for part in (".git", "bin", "obj", "node_modules", ".vs")):
            continue

        for name in sorted(names):
            if not name.endswith((".md", ".html")):
                continue

            path = os.path.join(folder, name)
            relative = os.path.relpath(path, conditions.ROOT)

            if relative in allowed or relative.startswith("REFERENCES"):
                continue

            try:
                text = io.open(path, encoding="utf-8").read()
            except OSError:
                continue

            # Not preceded by a letter: `arcgis-server` and every Esri URL containing
            # it are not this product, and they were five of the first run's thirty hits.
            for match in re.finditer(r"(?<![A-Za-z])gis-server", text, re.IGNORECASE):
                # Whitespace flattened first: the initial run flagged README's own
                # sentence because its excuse fell across a line break, which is where
                # prose puts it about half the time.
                near = text[max(0, match.start() - 200):match.end() + 200].lower()
                window = " ".join(near.split())

                # An identifier rather than the product name: the solution file, a
                # path, a container or image name.
                tail = text[match.end():match.end() + 8].lower()
                if tail.startswith((".sln", "/", "-host", ".csproj")):
                    continue

                if any(excuse in window for excuse in excuses):
                    continue

                line = text[:match.start()].count(chr(10)) + 1
                problems.append(
                    f"{relative}:{line} says gis-server as a live name. The product is "
                    "Graticula (ADR-032). Either use the new name, or say the old one is "
                    "the former working title -- history is allowed, a stale name is not."
                )

    return problems


def main() -> int:
    # <b>The same walk conditions.py's own main does</b>, rather than a second
    # implementation of it. CLAUDE.md records what happened when two tools each
    # decided for themselves what "discharged" means: they disagreed, and the
    # wrong number was the one on the status page.
    total = 0
    discharged = 0
    deferred = 0

    for name in sorted(os.listdir(conditions.ADRS)):
        if not name.startswith("ADR-") or not name.endswith(".md"):
            continue

        text = io.open(os.path.join(conditions.ADRS, name), encoding="utf-8").read()

        for _, _, done, put_off in conditions.conditions(text):
            total += 1
            discharged += 1 if done else 0
            deferred += 1 if put_off and not done else 0

    live = total - discharged - deferred

    # <b>Every check reports before anything exits.</b> The first version ran
    # duplicate_debt_ids twice and returned after each, which was harmless only
    # because both calls found the same thing -- and it meant a run could fix one
    # class of problem and discover the next one on the following run. Somebody
    # repairing registers deserves the whole list at once.
    problems = (duplicate_debt_ids() + broken_links() + remembered_numbers()
                + the_former_product_name())

    if problems:
        for line in problems:
            print(line)

        print(f"\n{len(problems)} problems in the registers.")
        return 1

    try:
        page = io.open("docs/status.html", encoding="utf-8").read()
    except OSError as problem:
        print(f"docs/status.html could not be read: {problem}")
        return 1

    # The page renders the pair as "29/100" in a KPI value.
    expected = f"{discharged}/{total}"

    if expected not in page:
        actual = re.findall(r">(\d+/\d+)<", page)
        print(
            f"docs/status.html says {actual or 'nothing'} where the registers now say "
            f"{expected}. Run: python tools/status-page.py docs/status.html"
        )
        return 1

    # And the live count, which is the number somebody choosing what to do next
    # actually reads.
    if str(live) not in page:
        print(
            f"docs/status.html does not mention the live condition count {live}. "
            "Run: python tools/status-page.py docs/status.html"
        )
        return 1

    print(
        f"registers and status page agree: {discharged} discharged, {deferred} deferred, "
        f"{live} live, of {total}."
    )

    return 0


if __name__ == "__main__":
    sys.exit(main())
