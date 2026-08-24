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
import subprocess
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


# A column break is a pipe that is not escaped. Markdown says so, and this
# repository's registers rely on it: D-82 quotes `owner \| manager \| member`
# and D-43 quotes a grep pattern with a pipe in it.
COLUMN = re.compile(r"(?<!\\)\|")

# Every register whose rows are read by column. Each of these files holds more
# than one table and they are not the same width -- open-questions.md alone has
# three -- so the width is taken from each table's own header rather than named
# here. Naming it here was the first version of this check and it reported 77
# problems, every one of them the check's own.
TABLES = (
    ("docs/architecture-debt.md", "debt register"),
    ("docs/open-questions.md", "open questions"),
    ("docs/architecture-assumptions.md", "assumptions"),
)

SEPARATOR = re.compile(r"^\|[\s:-]*\|[\s:|-]*$")


def ragged_register_rows():
    """Register rows with more or fewer columns than their own table's header.

    **Four debt rows were like this and it had gone unnoticed for days.** A row
    with one column too many renders as a table with a ragged edge that nobody
    scrolls far enough right to see -- and every tool that reads a named column
    reads past the break. status-page.py takes a debt row's status as its *last*
    cell, so D-43's status on the published page was the fragment
    `Failed)!"`, which discards them...` and D-75's was a recurrence the row had
    already closed. **The registers were right and the dashboard was wrong**,
    which is the failure D-30 is a debt about.

    Two causes, both mechanical: an unescaped pipe inside prose or inline code,
    and a row whose final pipe is missing. Both are found here, and the message
    names the repair rather than the rule.
    """
    complaints = []

    for path, what in TABLES:
        try:
            text = io.open(path, encoding="utf-8").read()
        except OSError as problem:
            complaints.append(path + " could not be read: " + str(problem))
            continue

        width = None
        previous = None

        for number, line in enumerate(text.splitlines(), 1):
            if not line.startswith("|"):
                # A table ends where the pipes stop, and the next one brings its
                # own header. Without this the second table in a file is measured
                # against the first one's width.
                width = None
                previous = None
                continue

            cells = [c.strip() for c in COLUMN.split(line.strip().strip("|"))]

            if SEPARATOR.match(line):
                width = len(previous) if previous else None
                continue

            if width is None:
                previous = cells
                continue

            # <b>Only rows with an identifier.</b> These registers carry
            # continuation rows and single-cell notes inside their tables, and a
            # complaint about one of those is noise that would get the whole
            # check switched off.
            if not re.match(r"^~*\s*[A-Z]+-\d+\s*~*$", cells[0]):
                continue

            if len(cells) != width:
                complaints.append(
                    f"{path}: {what} row {cells[0]} on line {number} has {len(cells)} "
                    f"columns and its table's header has {width}. An unescaped pipe in "
                    f"prose or inline code splits a row, and every tool that reads a "
                    f"named column then reads the wrong one -- the status page takes a "
                    f"debt row's last cell as its status. Escape it as \\| or rewrite "
                    f"the sentence."
                )
            elif not line.rstrip().endswith("|"):
                complaints.append(
                    f"{path}: {what} row {cells[0]} on line {number} does not end with a "
                    f"pipe, so its last cell has no closing edge."
                )

    return complaints


def assumptions_only_an_adr_knows_about():
    """Assumptions an ADR declares that never reached the register.

    [CLAUDE.md](../CLAUDE.md) §2: *assumptions go in architecture-assumptions.md
    with a status*, and §11 turns on that being true -- invalidating an
    assumption is supposed to trigger a review of every ADR depending on it, and
    an assumption the register has never heard of cannot trigger anything.

    **Seven had not, found 2026-08-23.** A-052 to A-056, A-062 and A-063 lived
    only in ADR-018, ADR-019, ADR-020 and ADR-024. The reason is visible in the
    shape of the rows: an ADR's assumption table has three columns and the
    register's has five, so copying one across is an edit rather than a paste,
    and an edit is a thing that gets postponed. This check does not care why.
    """
    register_ids = set()

    try:
        register = io.open("docs/architecture-assumptions.md", encoding="utf-8").read()
    except OSError as problem:
        return ["docs/architecture-assumptions.md could not be read: " + str(problem)]

    for line in register.splitlines():
        match = re.match(r"^\|\s*~*\s*(A-\d+)\s*~*\s*\|", line)
        if match:
            register_ids.add(match.group(1))

    missing = {}

    for name in sorted(os.listdir(conditions.ADRS)):
        if not name.startswith("ADR-") or not name.endswith(".md"):
            continue

        text = io.open(os.path.join(conditions.ADRS, name), encoding="utf-8").read()

        for line in text.splitlines():
            match = re.match(r"^\|\s*~*\s*(A-\d+)\s*~*\s*\|\s*(.{0,60})", line)

            if not match or match.group(1) in register_ids:
                continue

            missing.setdefault(match.group(1), (name, match.group(2).strip()))

    return [
        f"{ident} is declared in {where} and is not in docs/architecture-assumptions.md: "
        f"\"{opening}...\". An ADR's assumption table has three columns and the register's has "
        f"five, so it is a copy plus two cells -- how it gets validated, and what depends on it. "
        f"CLAUDE.md §11 reviews every ADR that depends on an assumption when it is invalidated, "
        f"and it cannot review one the register has never heard of."
        for ident, (where, opening) in sorted(missing.items())
    ]


# An ADR declaring what it changes, in either of the two shapes the ADRs use: a row in
# the header table, or a blockquote under it.
AMENDS = re.compile(
    r"^\|\s*\*\*(Amends|Supersedes|Superseded by)\*\*\s*\|(?P<row>.*)$"
    r"|^>\s*(Amends|Supersedes)\b(?P<quote>.*)$",
    re.MULTILINE)

NUMBER = re.compile(r"ADR-(\d+)")


def amendments_the_other_adr_does_not_know_about():
    """An ADR named as amended that never mentions the ADR that amended it.

    **[D-126](../docs/architecture-debt.md), and [D-130](../docs/architecture-debt.md)
    names it as one of three cheap checks that would each have caught the failure that
    named it.** ADR-041 un-deferred ADR-004 and shipped the renderer; ADR-004's own file
    still read `DEFERRED` with its §5 reading *Pending*, and eight further documents
    restated the deferral as current fact. That was found by a person reading everything.

    **The asymmetry is the whole defect.** An amending ADR names what it changes, because
    the author is looking at it; the amended one says nothing, because nobody opens a file
    to record that somebody else has just contradicted it. So the citation exists in one
    direction and a reader arriving from the other finds a decision that reads as current.

    **This check earned itself on the first run**, on ADR-046 amending ADR-007 §4.8 while
    ADR-007 had never heard of it — written the same day as the check, by the same hand,
    which is the argument for the check rather than against it.
    """
    complaints = []
    text = {}

    for name in sorted(os.listdir(conditions.ADRS)):
        if not name.startswith("ADR-") or not name.endswith(".md"):
            continue

        found = NUMBER.search(name)

        if found:
            text[found.group(1)] = (
                name, io.open(os.path.join(conditions.ADRS, name), encoding="utf-8").read())

    for number, (name, body) in text.items():
        for match in AMENDS.finditer(body):
            claim = match.group("row") or match.group("quote") or ""

            # <b>Only the header, not every mention.</b> A `Supersedes | —` row says
            # nothing, and an ADR discussing another one in its prose is not claiming to
            # amend it -- the claim is the declaration, which is why this reads the two
            # declaration shapes rather than searching for the word.
            for other in sorted(set(NUMBER.findall(claim))):
                if other == number or other not in text:
                    continue

                if "ADR-" + number in text[other][1]:
                    continue

                complaints.append(
                    f"{name} declares that it amends ADR-{other}, and "
                    f"{text[other][0]} never mentions ADR-{number}. A reader who opens "
                    f"the amended decision finds it reading as current: that is exactly "
                    f"how ADR-004 stayed DEFERRED after ADR-041 un-deferred it, and how "
                    f"eight other documents came to restate the deferral. Add a note in "
                    f"the amended ADR saying what changed and who changed it."
                )

    return complaints


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


def condition_counts():
    """What the ADRs say, counted the way main counts them."""
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

    return discharged, deferred, total - discharged - deferred, total


def a_condition_tally_that_disagrees_with_the_conditions():
    """A present-tense condition count in any document, checked against the truth.

    **[D-116](../docs/architecture-debt.md): a fact in three places is three places
    to be wrong**, and its own trigger is *the next time two documents disagree
    about the same number*. `remembered_numbers` already forbids a tally in
    CLAUDE.md outright, because that file is read at the start of every session.
    Everywhere else a tally is sometimes the right thing to write -- a review
    reporting what it found, a completeness row stating where the gates stand --
    so this does not forbid it. It checks it.

    **Only `N of M` where M is the number of conditions there are**, which is what
    makes this safe to run over prose. A sentence saying *24 of 99* is claiming
    something this tool can compute; a sentence saying *three of four findings*
    is not, and is left alone because 4 is not the condition total.

    **Dated history stays legal, for the reason `remembered_numbers` gives.**
    CLAUDE.md's account of the count being 22 when the truth was 24 is a record of
    an event. So a line is exempt when it carries a date, or says *was*, or names
    the count as something that used to be true -- and the exemption is narrow
    enough that a stale claim written in the present tense still fails.
    """
    discharged, deferred, live, total = condition_counts()

    if total == 0:
        return ["conditions.py found no conditions at all, so nothing can be checked against it"]

    problems = []

    # A tally about *these* conditions names their total. Anything else is a
    # sentence about something else that happens to contain two numbers.
    pattern = re.compile(r"(\d+)\s*(?:of|/)\s*" + str(total) + r"\b")

    was = re.compile(
        r"\bwas\b|\bwere\b|\bused to\b|\bat the time\b|\bthen\b|20\d\d-\d\d-\d\d")

    for path in documents():
        name = os.path.relpath(path, conditions.ROOT).replace("\\", "/")

        if name == "CLAUDE.md":
            # remembered_numbers owns that file and refuses a tally there entirely.
            continue

        try:
            text = io.open(path, encoding="utf-8").read()
        except OSError as problem:
            problems.append(f"{name} could not be read: {problem}")
            continue

        for line in text.splitlines():
            if "condition" not in line.lower():
                continue

            for match in pattern.finditer(line):
                said = int(match.group(1))

                if said in (discharged, discharged + deferred, live, total):
                    continue

                if was.search(line):
                    continue

                problems.append(
                    f'{name} says "{match.group(0)}" about the conditions and the '
                    f"conditions say {discharged} discharged, {deferred} deferred and "
                    f"{live} live of {total}. One fact, one home: cite "
                    "docs/status.html or run tools/conditions.py, or say when the "
                    "number you are quoting was true."
                )

    return problems


def gate_counts():
    """The §66 gates, counted from the one table that owns them.

    CLAUDE.md §1 says which gates have run *"is maintained in one place, the §66
    table in architecture-completeness.md, and is read there rather than restated
    here"*. This reads it there. A gate has run when its `Run` cell carries a
    date; the two that have not carry an em dash and a sentence saying what they
    are waiting for.
    """
    path = os.path.join(conditions.ROOT, "docs", "architecture-completeness.md")

    try:
        text = io.open(path, encoding="utf-8").read()
    except OSError:
        return 0, 0

    run = 0
    total = 0
    inside = False

    for line in text.splitlines():
        if line.startswith("| Gate | Run | Result |"):
            inside = True
            continue

        if inside:
            if not line.startswith("|"):
                break

            cells = [c.strip() for c in line.strip().strip("|").split("|")]

            if len(cells) < 2 or set(cells[0]) <= set("-: "):
                continue

            total += 1
            run += 1 if re.search(r"20\d\d-\d\d-\d\d", cells[1]) else 0

    return run, total


def a_gate_tally_that_disagrees_with_the_gates():
    """A present-tense §66 gate count in any document, checked against the table.

    **[D-116](../docs/architecture-debt.md)'s trigger, fired a second time.** The
    condition check one function up catches `N of M` where M is the number of
    conditions. It caught nothing about the gates, because the gates are a
    different register with a different total -- and four documents were saying
    **0 of 9 run** in the present tense while seven of the nine had run. The
    stale one that mattered was in an ADR, listing the gates among *live debts*;
    the other three were snapshots that had simply never been dated.

    **The same narrowness as the condition check**, for the same reason. Only
    `N of 9` on a line that mentions a gate, only when N is neither the number
    run nor the number outstanding, and only when the line does not say when it
    was true. A checker that guessed at which numbers were claims would be turned
    off within a week.
    """
    run, total = gate_counts()

    if total == 0:
        return ["architecture-completeness.md has no §66 gate table, so nothing can be checked "
                "against it -- and CLAUDE.md §1 sends every reader there for the tally"]

    problems = []

    pattern = re.compile(r"(\d+)\s*(?:of|/)\s*" + str(total) + r"\b")
    was = re.compile(
        r"\bwas\b|\bwere\b|\bremained\b|\bused to\b|\bat the time\b|\bthen\b|20\d\d-\d\d-\d\d")

    for path in documents():
        name = os.path.relpath(path, conditions.ROOT).replace("\\", "/")

        if name == "CLAUDE.md":
            # remembered_numbers owns that file and refuses a tally there entirely.
            continue

        if name == "docs/architecture-completeness.md":
            # The table itself. A count read off its own source is not a restatement.
            continue

        try:
            text = io.open(path, encoding="utf-8").read()
        except OSError as problem:
            problems.append(f"{name} could not be read: {problem}")
            continue

        for line in text.splitlines():
            if "gate" not in line.lower():
                continue

            for match in pattern.finditer(line):
                said = int(match.group(1))

                if said in (run, total - run, total):
                    continue

                if was.search(line):
                    continue

                problems.append(
                    f'{name} says "{match.group(0)}" about the §66 review gates and the '
                    f"gate table says {run} of {total} have run. One fact, one home: cite "
                    "the §66 table in docs/architecture-completeness.md, which is where "
                    "CLAUDE.md §1 sends every reader, or say when the number you are "
                    "quoting was true."
                )

    return problems


def a_debt_row_that_disagrees_with_itself():
    """A row whose text says it is resolved while its status cell says it is open.

    **Found twice in one pass, 2026-08-24, and each had stood for ten days.**
    [D-09](../docs/architecture-debt.md) read *RESOLVED 2026-08-14* in its own
    text and `OPEN` in its status; [D-11](../docs/architecture-debt.md) read
    *RESOLVED 2026-08-13* and `OPEN`, and gave as its reason a fact D-09 had just
    disproved. Nothing noticed, because the two halves are read by different
    people at different times: the status column is what `status-page.py` counts
    and what a reader skims, and the text is what somebody updates when they fix
    the thing.

    **So the register disagreed with the register, inside one row.** That is
    [D-116](../docs/architecture-debt.md)'s *a fact in three places is three
    places to be wrong* at its smallest possible scale, and the cheapest to check.

    **Narrow, like the two tally checks above.** It fires only when the text
    carries an emphasised **RESOLVED**, **CLOSED** or **REPAID** -- the register's
    own vocabulary for done, emphasised because that is how this file marks a
    verdict -- and the status cell does not start with one. A row saying *partly
    resolved* is a third state and is left alone, and so is a row whose text
    merely mentions that some other debt was closed.
    """
    path = os.path.join(conditions.ROOT, "docs", "architecture-debt.md")

    try:
        text = io.open(path, encoding="utf-8").read()
    except OSError as problem:
        return [f"architecture-debt.md could not be read: {problem}"]

    # The same verdict words status-page.py counts, and in the same order, so the
    # two cannot disagree about what *done* looks like.
    done = re.compile(
        r"\s*(\*{0,2}|~~open~~\s*)(RESOLVED|CLOSED|REPAID|WITHDRAWN)\b", re.I)

    # In the text, the verdict is emphasised: that is how this file marks one.
    # PARTLY is excluded by requiring the marker to open the emphasis.
    claimed = re.compile(r"\*\*(RESOLVED|CLOSED|REPAID)\b")

    problems = []
    rows = 0

    for line in text.splitlines():
        if not line.startswith("| D-"):
            continue

        cells = [c.strip() for c in line.strip().strip("|").split(" | ")]

        if len(cells) < 7:
            # ragged_register_rows owns that failure and says it better.
            continue

        rows += 1
        identifier, body, status = cells[0], cells[1], cells[-1]

        if done.match(status):
            continue

        # <b>The same lie from inside the status cell — found 2026-08-24.</b> The check below
        # was written for a row whose *text* claims a resolution its status denies. Four rows
        # had the resolution **in the status cell** and not at the front of it: D-17, D-120,
        # D-65 and D-127 each said `CLOSED <date>` somewhere in the middle of a paragraph that
        # opened with `OPEN` or `PARTLY`. `status-page.py` reads the start of the cell, so all
        # four were counted among the open debts — and the answer to *are they all closed* was
        # wrong by four.
        #
        # <b>The verdict leads.</b> A cell that buries it is a cell whose first sentence is
        # false, and the first sentence is the whole of what a reader skims and the tool reads.
        verdict = re.search(r"\*\*(CLOSED|RESOLVED|REPAID|WITHDRAWN)\s+20\d\d-\d\d-\d\d", status)

        if verdict:
            problems.append(
                f'{identifier} says "{verdict.group(0)}" inside its status cell, which opens '
                f'"{status[:50].strip()}...". The verdict has to lead: status-page.py reads '
                "the front of the cell, so a closure buried in the middle is counted open. "
                "D-116's shape inside one cell."
            )
            continue

        if not claimed.search(body):
            continue

        problems.append(
            f'{identifier} says "{claimed.search(body).group(0)}" in its own text and its '
            f'status cell reads "{status[:60].strip()}...". One of the two is wrong, and the '
            "status cell is the one the page counts and a reader skims. D-09 and D-11 each "
            "stood like this for ten days."
        )

    if rows < 100:
        problems.append(
            f"only {rows} debt rows were parsed, which means the register's shape moved and "
            "this check is reading nothing. A check that cannot fail is worse than no check.")

    return problems


def a_demoted_assumption_still_called_load_bearing():
    """An assumption the register has demoted, still listed as load-bearing.

    **[D-149](../docs/architecture-debt.md), and it was true in both directions.**
    `architecture-assumptions.md` listed **A-019** under *the load-bearing five*
    while line 35 of the same file recorded it as *demoted the same day: no longer
    load-bearing*, with a failure scenario about the two engines v1 removed. And
    `CLAUDE.md` called **A-003** *the load-bearing assumption under ADR-007* while
    the register had recorded it **DOWNGRADED to informational** nine days
    earlier. One file stated and denied the same fact; the other outlived a
    decision in the document every session reads first.

    **What it checks and why that is enough.** The register's own row is the
    truth, because that is where a status is maintained. So: no assumption whose
    row says it was demoted, downgraded or is no longer load-bearing may appear in
    the impact table, and none may be called *load-bearing* in CLAUDE.md. Both
    halves matter -- the first is the list somebody reads to decide what to
    validate next, the second is what a new reader is told to worry about.

    **Narrow, like the tally checks above.** It fires only on the register's own
    demotion vocabulary, and only for identifiers that actually have a row.
    """
    path = os.path.join(conditions.ROOT, "docs", "architecture-assumptions.md")

    try:
        register = io.open(path, encoding="utf-8").read()
    except OSError as problem:
        return [f"architecture-assumptions.md could not be read: {problem}"]

    demoted = re.compile(
        r"\bdemoted\b|\bDOWNGRADED\b|\bdowngraded\b|no longer load-bearing", re.I)

    # The status of an assumption is whatever its own row says. Rows in the impact
    # table start with `| **A-` and carry no status, so they are not read as one.
    status = {}
    for line in register.splitlines():
        found = re.match(r"\|\s*(A-\d+)\s*\|", line)
        if found and found.group(1) not in status:
            status[found.group(1)] = line

    if len(status) < 20:
        return [f"only {len(status)} assumption rows were parsed, so this check is reading "
                "nothing. A check that cannot fail is worse than no check."]

    problems = []

    inside = False
    for line in register.splitlines():
        if line.startswith("## Failure impact"):
            inside = True
            continue

        if inside:
            if line.startswith("## "):
                break

            # <b>Rows, not prose.</b> The section's own text names the assumptions it
            # explains — including the ones it explains the *absence* of — and reading those
            # as entries would make the correction that removed one look like the defect.
            if not line.startswith("|"):
                continue

            found = re.search(r"\*\*(A-\d+)\*\*", line)
            if found and demoted.search(status.get(found.group(1), "")):
                problems.append(
                    f"{found.group(1)} is listed under 'Failure impact' and its own row in "
                    "architecture-assumptions.md says it was demoted. The impact table is what "
                    "somebody reads to decide where to spend a measurement, so an assumption "
                    "that holds nothing up makes the pile look larger than it is. D-149.")

    claude = os.path.join(conditions.ROOT, "CLAUDE.md")

    try:
        text = io.open(claude, encoding="utf-8").read()
    except OSError as problem:
        return problems + [f"CLAUDE.md could not be read: {problem}"]

    # <b>Struck text is history</b>, and is how this file records what it used to say. It
    # spans lines here, so it is removed from the whole document before reading any of it.
    living = re.sub(r"~~.*?~~", "", text, flags=re.S)

    for line in living.splitlines():
        for found in re.finditer(r"\*\*(A-\d+)\*\*[^.]{0,60}load-bearing", line):
            if demoted.search(status.get(found.group(1), "")):
                problems.append(
                    f"CLAUDE.md calls {found.group(1)} load-bearing and the assumptions "
                    "register says it was demoted. This is the file read at the start of every "
                    "session, so it is the worst place for a claim a decision has already "
                    "reversed. D-149.")
    # <b>Every document, not two named ones -- widened 2026-08-24, hours after
    # D-149 closed.</b> The first version read the impact table and CLAUDE.md,
    # because those were the two places the defect had been found. Two more were
    # standing while it passed: `v1-scope.md` §6, which CLAUDE.md §1 calls
    # authoritative for scope, and this register's own *Priority* paragraph, four
    # lines above the table the check did read. A check shaped like the instances
    # rather than like the class is D-130 wearing a tool.
    #
    # <b>Flowed, because prose wraps.</b> The Priority sentence breaks between
    # *"A-003 is the"* and *"load-bearing"*, so a line-by-line scan cannot see it
    # -- and did not.
    #
    # <b>Reviews and research are records and are exempt.</b> A review says what
    # was believed on a date and is never rewritten; `independent-review-3-process`
    # quotes the register's own stale note, which is the thing it is for.
    exempt = ("reviews", "research", "architecture-assessment.md")

    claim = re.compile(
        r"\*{0,2}(A-\d+)\*{0,2}[^.]{0,40}?\b(?:is|are|stays?|remains?|being)\b"
        r"[^.]{0,30}?\bload-bearing\b"
        r"|\bload-bearing\b[^.]{0,40}?\*{0,2}(A-\d+)\*{0,2}")

    # <b>Word boundaries, and one of them was load-bearing itself.</b> Written
    # without them, `read` matched *threaded* in *"a threaded worker model is
    # available"* -- the sentence immediately before the real defect -- and the
    # check reported clean on the file it was reading.
    record = re.compile(
        r"no longer|stops? being|stopped being|less load-bearing|not load-bearing|"
        r"from load-bearing|to load-bearing|\bagain\b|\bused to\b|\bwas the\b|"
        r"\bwere the\b|\bsaid\b|\bsays\b|\bcalled\b|\blisted\b|\bquoted\b|\bread\b|"
        r"\bpreviously\b|\buntil\b", re.I)

    for base, _, names in os.walk(conditions.ROOT):
        if any(part in base for part in (".git", "REFERENCES", "node_modules")):
            continue

        for name in names:
            if not name.endswith(".md"):
                continue

            path = os.path.join(base, name)
            shown = os.path.relpath(path, conditions.ROOT).replace(os.sep, "/")

            if any(part in shown for part in exempt):
                continue

            try:
                text = io.open(path, encoding="utf-8").read()
            except OSError:
                continue

            flowed = re.sub(r"\s*\n\s*", " ", re.sub(r"~~.*?~~", "", text, flags=re.S))

            for found in claim.finditer(flowed):
                who = found.group(1) or found.group(2)
                if not demoted.search(status.get(who, "")):
                    continue

                # <b>The sentence, not a window of characters around it.</b> The
                # first version read 90 characters either side, and v1-scope's
                # closing paragraph -- *"no document said who would build it"* --
                # silenced a planted claim three lines below it. Whether a
                # sentence is a record is a property of that sentence; borrowing
                # the neighbour's vocabulary is how a check passes on a defect it
                # is looking straight at. Proved by planting one and watching this
                # fail, which the version above did not.
                start = flowed.rfind(".", 0, found.start()) + 1
                stop = flowed.find(".", found.end())
                sentence = flowed[start:stop if stop > 0 else len(flowed)]

                if record.search(sentence):
                    continue

                problems.append(
                    f"{shown} calls {who} load-bearing and the assumptions register says it "
                    f"was demoted: \"...{found.group(0)[:70]}...\". A decision that stops "
                    "holding something up has to stop being written down as holding it up, "
                    "or the register and the documents that quote it disagree. D-149, D-130.")

    return problems


def register_counts():
    """What each register actually holds, counted the way the status page counts it.

    **One reader for both the page and the checks.** The tallies below are the
    numbers `status-page.py` publishes, so a document restating one is checked
    against the same arithmetic the board uses rather than against a second
    implementation that could drift from it -- which would be the defect these
    checks exist to catch, one level up.
    """
    counts = {}

    debts = os.path.join(conditions.ROOT, "docs", "architecture-debt.md")
    questions = os.path.join(conditions.ROOT, "docs", "open-questions.md")
    assumptions = os.path.join(conditions.ROOT, "docs", "architecture-assumptions.md")

    try:
        text = io.open(debts, encoding="utf-8").read()
    except OSError:
        text = ""

    done = re.compile(
        r"\s*(\*{0,2}|~~open~~\s*)(RESOLVED|CLOSED|REPAID|WITHDRAWN)\b", re.I)

    rows = 0
    open_rows = 0
    for line in text.splitlines():
        if not line.startswith("| D-"):
            continue

        cells = [c.strip() for c in line.strip().strip("|").split(" | ")]

        if len(cells) < 7:
            continue

        rows += 1
        open_rows += 0 if done.match(cells[-1]) else 1

    counts["debt"] = (open_rows, rows)

    try:
        adrs = [n for n in os.listdir(conditions.ADRS)
                if n.startswith("ADR-") and n.endswith(".md")]
    except OSError:
        adrs = []

    counts["ADR"] = (len(adrs), len(adrs))

    return counts


def a_register_tally_that_disagrees_with_the_register():
    """A present-tense count of debts or ADRs, checked against the registers.

    **[D-116](../docs/architecture-debt.md)'s remaining half.** Two checks above
    already cover the ADR conditions and the §66 gates, each against its own
    total. The board also publishes counts of debts and ADRs, and nothing
    compared a document restating one of those against the thing itself -- which
    is how *four documents said `0 of 9`* went unnoticed for four days, and there
    was no reason the same could not happen to a debt count.

    **The same narrowness, for the same reason.** Only a bare number immediately
    before the register's own word -- *38 open debts*, *46 ADRs* -- and only when
    it is neither the count nor a plausible neighbour of it. A checker that
    guessed at which numbers were claims would be turned off within a week, and
    the two checks above earn their keep by being boring.

    **Dated history stays legal**, as it does above: a line that says when its
    number was true is a record of an event, and deleting those to satisfy a tool
    would destroy the reason the tool exists.
    """
    counts = register_counts()

    if counts.get("debt", (0, 0))[1] < 50:
        return ["architecture-debt.md parsed fewer than fifty rows, so this check is reading "
                "nothing. A check that cannot fail is worse than no check."]

    # <b>A claim about the present, and these words say it is not one.</b> `read`, `said`
    # and `quoted` are how this repository records what a document used to hold — *Scope
    # read: all 17 ADRs* is a reviewer naming what they opened on the day, and *this table
    # said 12 ADRs* is a correction quoting the thing it corrected. Deleting either to
    # satisfy a checker would destroy the record the checker exists to protect.
    was = re.compile(
        r"\bwas\b|\bwere\b|\bremained\b|\bused to\b|\bat the time\b|\bthen\b"
        r"|\bread\b|\bsaid\b|\bquoted\b|20\d\d-\d\d-\d\d")

    # <b>And a sentence about somebody else's repository is not about this one.</b>
    # ADR-030 counts the reference's ADRs, which is the whole subject of that document.
    elsewhere = re.compile(r"\breference\b|\bpeer\b|\btheir\b", re.I)

    words = {
        "debt": r"open debts?",
        "ADR": r"ADRs\b",
    }

    problems = []

    for path in documents():
        name = os.path.relpath(path, conditions.ROOT).replace("\\", "/")

        if name == "CLAUDE.md":
            # remembered_numbers owns that file and refuses a tally there entirely.
            continue

        try:
            text = io.open(path, encoding="utf-8").read()
        except OSError as problem:
            problems.append(f"{name} could not be read: {problem}")
            continue

        # <b>A document that stamps its own date is a record of that date.</b> An independent
        # review opens *Produced 2026-08-13 by a reviewer with no access to…*; the phase-0
        # assessment opens *Written 2026-08-13*. Every number in one of those is a statement
        # about the day it was made, and the way this repository keeps them honest is the
        # header rather than a date on every line — which is what
        # [architecture-assessment.md](../docs/architecture-assessment.md) already does, and
        # why it was repaired with a header instead of a rewrite.
        #
        # <b>Derived rather than a path list</b>, so a review filed somewhere new is covered
        # and a live document that grows a date is not silently exempted: only the first
        # fifteen lines are read, which is where a document declares what it is.
        stamped = re.search(
            r"\b(Produced|Written|Run|Recorded|Reviewed)\b[^\n]{0,40}20\d\d-\d\d-\d\d",
            "\n".join(text.splitlines()[:15]))

        if stamped:
            continue

        rows = text.splitlines()

        for n, line in enumerate(rows):
            # <b>A sentence is not a line.</b> ADR-030's *the reference's `docs/` tree already
            # carries … 74 ADRs* wraps, so the subject and the number sit on different lines and
            # reading one line at a time asks the question of half a sentence.
            sentence = (rows[n - 1] + " " + line) if n else line

            if was.search(sentence) or elsewhere.search(sentence):
                continue

            for kind, word in words.items():
                for found in re.finditer(r"(\d+)\s+" + word, line, re.I):
                    said = int(found.group(1))
                    current, total = counts[kind]

                    if said in (current, total):
                        continue

                    problems.append(
                        f'{name} says "{found.group(0)}" and the register holds {current} '
                        f"of {total}. One fact, one home: cite docs/status.html, or say when "
                        "the number you are quoting was true."
                    )

    return problems


def a_debt_row_with_an_empty_cell():
    """A debt row that leaves out what it was, why it was acceptable, or what it costs.

    **[CLAUDE.md §62](../CLAUDE.md) states the shape and nothing enforced it.**
    Every entry records what was compromised, why it was acceptable at the time,
    and the observable condition that makes it unacceptable -- and *an entry
    without a trigger is not debt, it is an undocumented permanent decision
    wearing a disguise*. `ragged_register_rows` catches a row with the **wrong
    number** of cells, which is a pipe in prose; this catches a row with the right
    number and nothing in them.

    **Found 2026-08-24: D-29 and D-30 each had three empty cells** -- taken on,
    why it was acceptable, and what it costs if unpaid. Both were readable rows
    with long claims and long statuses, so nothing about them looked thin, and
    the missing halves are the two that make a debt reviewable at all: *when did
    we accept this* and *what does it cost us*.

    **The trigger column is not checked here.** `ragged_register_rows` already
    refuses a row that cannot be parsed, and a trigger that is present but vague
    is a judgement rather than a check -- that is what a review gate is for.
    """
    path = os.path.join(conditions.ROOT, "docs", "architecture-debt.md")

    try:
        text = io.open(path, encoding="utf-8").read()
    except OSError as problem:
        return [f"architecture-debt.md could not be read: {problem}"]

    # The register's own column order, from its header.
    names = ["id", "the debt", "taken on", "why it was acceptable",
             "trigger to repay", "cost if unpaid", "status"]

    problems = []
    rows = 0

    for line in text.splitlines():
        if not line.startswith("| D-"):
            continue

        cells = [c.strip() for c in line.strip().strip("|").split(" | ")]

        if len(cells) != len(names):
            # ragged_register_rows owns that failure and says it better.
            continue

        rows += 1

        empty = [names[i] for i, cell in enumerate(cells) if cell == ""]

        if empty:
            problems.append(
                f"{cells[0]} leaves {', '.join(empty)} empty. §62 asks every entry for what was "
                "compromised, why it was acceptable at the time, and what it costs unpaid -- a "
                "row missing those is readable and not reviewable."
            )

    if rows < 100:
        problems.append(
            f"only {rows} debt rows were parsed, which means the register's shape moved and this "
            "check is reading nothing. A check that cannot fail is worse than no check.")

    return problems


def an_answered_question_still_filed_as_open():
    """A question whose own row opens with its answer, still under an Open heading.

    **Nine of them on 2026-08-24, and the page counted every one as live
    uncertainty.** `status-page.py` takes resolution from the section heading
    rather than from the text, deliberately: the version that read the text
    reported 93 of 95 questions open, which is the kind of confidently wrong
    number a status page exists to avoid. The cost of that choice is this --
    a row that answers itself in place is a row the page cannot see the answer
    in -- and it accumulated silently over eleven days.

    **Eight had the answer in the question cell and one in *Resolves in*
    ([Q-89](../docs/open-questions.md)), which is why the first sweep found
    eight.** So every cell is read, not the second one.

    **Narrow, because a question may legitimately mention another's answer.**
    Q-92's original text opened *"Q-59 has stopped being merely open"* and its
    body said *"Q-59 is answered"*; neither is this defect. The rule is that the
    verdict is the cell's **first word** -- bold markers allowed, nothing else
    before it -- which is what all nine looked like and what a passing mention
    never does.
    """
    path = os.path.join(conditions.ROOT, "docs", "open-questions.md")

    try:
        lines = io.open(path, encoding="utf-8").read().splitlines()
    except OSError as problem:
        return [f"open-questions.md could not be read: {problem}"]

    answered = next(
        (i for i, line in enumerate(lines) if line.startswith("## Answered")), len(lines))

    verdict = re.compile(
        r"^\*{0,2}(ANSWERED|Answered|RESOLVED|Resolved|Re-answered|Re-ANSWERED"
        r"|DISSOLVED|Dissolved|WITHDRAWN|Withdrawn)\b")

    problems = []
    rows = 0

    for line in lines[:answered]:
        cells = [c.strip() for c in COLUMN.split(line.strip().strip("|"))]

        if len(cells) < 2 or not re.match(r"^Q-\d+$", cells[0]):
            continue

        # A struck question is withdrawn and its shape is settled.
        if cells[1].startswith("~~"):
            continue

        rows += 1

        for which, cell in enumerate(cells[1:], start=1):
            found = verdict.match(cell)
            if not found:
                continue

            problems.append(
                f'{cells[0]} is filed under an open heading and its own cell {which} opens '
                f'"{found.group(1)}". The page classifies by section, so the answer is invisible '
                "to it and the question is counted as live uncertainty. Move the row into "
                "Answered; nine had accumulated when this check was written.")
            break

    if rows < 30:
        problems.append(
            f"only {rows} open questions were parsed, so this check is reading nothing. A check "
            "that cannot fail is worse than no check.")

    return problems


def an_open_question_that_asks_without_saying_why():
    """An open question recorded as a sentence and nothing else.

    **[CLAUDE.md §2](../CLAUDE.md) says uncertainty is recorded, not hidden**, and
    a question with no context is a third thing: recorded and unusable. Four were
    like that on 2026-08-24 -- **Q-01**, *which language, on measured evidence?*,
    carried the question and the word *Council*, so a reader met it and could
    conclude the decision had never been taken. It had: ADR-001 chose C# on
    2026-08-12 and the whole product is written in it. What is open is the
    *evidence*, and the row could not say so because it said nothing.

    **What makes a question usable is not length.** It is that somebody meeting
    it can tell what is undecided, what the candidate answers cost, and where to
    look -- so the check asks for any of those: a date, an emphasised phrase, or
    a link. A one-line question with a pointer passes; a one-line question with
    nothing does not.

    **Answered rows are exempt.** A question that has been answered is history
    and its shape is settled -- the section heading says which is which, so the
    check reads only what is above `## Answered`.
    """
    path = os.path.join(conditions.ROOT, "docs", "open-questions.md")

    try:
        lines = io.open(path, encoding="utf-8").read().splitlines()
    except OSError as problem:
        return [f"open-questions.md could not be read: {problem}"]

    answered = next(
        (i for i, line in enumerate(lines) if line.startswith("## Answered")), len(lines))

    problems = []
    rows = 0

    for line in lines[:answered]:
        cells = [c.strip() for c in line.strip().strip("|").split(" | ")]

        if len(cells) < 2 or not re.match(r"^Q-[\w-]+$", cells[0]):
            continue

        rows += 1
        body = re.sub(r"\s+", " ", cells[1])

        # A date, an emphasis, or a link — any one of them means somebody wrote
        # down what they knew rather than only what they were unsure about.
        if len(body) < 90 and not re.search(r"\*\*|20\d\d-\d\d-\d\d|\]\(", body):
            problems.append(
                f'{cells[0]} is recorded as "{body}" and nothing else — no candidate answers, no '
                "date, no pointer. §2 asks for uncertainty to be recorded; a question somebody "
                "cannot act on is recorded and unusable, and Q-01 read as though the decision "
                "had never been taken when it had."
            )

    if rows < 40:
        problems.append(
            f"only {rows} open questions were parsed, so this check is reading nothing. A check "
            "that cannot fail is worse than no check.")

    return problems


def source_the_repository_would_not_receive():
    """Source files an ignore rule keeps out of the repository.

    **D-62.** `.gitignore` carried `secrets/` under *Local configuration and secrets*,
    meant for a deployment's own credential files. Unanchored, that pattern matches a
    directory of that name at any depth -- and case-insensitively on Windows -- so it
    swallowed `src/Graticula.Platform/Secrets/`. The two files that seal every registered
    data source credential were never committed, and a clone of this repository could not
    build.

    **Nothing reported it, and that is the whole reason this check exists.** An ignored
    file is not an untracked one: `git status` says nothing about it by definition, the
    working copy compiles because the file is on disk, and every check anybody runs
    passes. It was found by deleting the working copy by accident and recovering the
    source from a build output that happened to sit outside the repository.

    **Ignored, not merely untracked.** A file somebody has just written and not yet
    added is untracked, which is normal and is not what this is about. A file an ignore
    rule *hides* is the pathological case, and asking `git check-ignore` is the exact
    question.

    **Why this rather than a CI job that clones and builds.** That is the broader answer
    and it is [Q-117](docs/open-questions.md); this is the narrow one, it runs wherever
    the registers are checked, and it catches the failure at the moment the rule is
    written rather than at the next push.
    """
    problems = []

    roots = [os.path.join(conditions.ROOT, "src"), os.path.join(conditions.ROOT, "tests")]
    candidates = []

    for root in roots:
        if not os.path.isdir(root):
            continue

        for here, directories, files in os.walk(root):
            # Build output is ignored on purpose and is not source.
            directories[:] = [d for d in directories if d not in ("bin", "obj")]

            for name in files:
                if name.endswith((".cs", ".csproj", ".js", ".css", ".html")):
                    candidates.append(os.path.join(here, name))

    if not candidates:
        return problems

    # <b>One call, not one per file.</b> `git check-ignore --stdin` answers for a list;
    # 700 processes would make this the slowest check here by two orders of magnitude.
    #
    # <b>NUL-separated and in bytes, and both halves of that were a defect here.</b>
    # With `text=True`, Python translates the newlines in the *input* to CRLF on
    # Windows, so git received a carriage return at the end of each path, took it as
    # part of the filename, and echoed it back C-quoted -- which printed a path with a
    # stray escape on the end and looked like this tool was broken rather than the rule
    # it was reporting. `-z` reads and writes NUL-separated with no quoting at all,
    # which removes the translation and the un-quoting together.
    try:
        answer = subprocess.run(
            ["git", "check-ignore", "-z", "--stdin", "--no-index"],
            input=b"\0".join(c.encode("utf-8") for c in candidates),
            capture_output=True, cwd=conditions.ROOT, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as e:
        return [f"could not ask git which files are ignored: {e}"]

    for chunk in answer.stdout.decode("utf-8", "replace").split("\0"):
        hidden = chunk.strip()

        if not hidden:
            continue

        relative = os.path.relpath(hidden, conditions.ROOT).replace(os.sep, "/")

        problems.append(
            f"{relative} is source and .gitignore hides it, so a clone of this "
            "repository would not receive it and could not build. An ignored file is "
            "invisible to `git status`, which is why this is checked rather than "
            "noticed -- see D-62, where two files that seal every data source "
            "credential were absent for six days. Anchor the pattern to the "
            "repository root, or narrow it."
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
    problems = (duplicate_debt_ids() + ragged_register_rows()
                + assumptions_only_an_adr_knows_about()
                + amendments_the_other_adr_does_not_know_about() + broken_links()
                + remembered_numbers() + the_former_product_name()
                + a_condition_tally_that_disagrees_with_the_conditions()
                + a_gate_tally_that_disagrees_with_the_gates()
                + a_debt_row_that_disagrees_with_itself()
                + a_demoted_assumption_still_called_load_bearing()
                + a_register_tally_that_disagrees_with_the_register()
                + a_debt_row_with_an_empty_cell()
                + an_open_question_that_asks_without_saying_why()
                + an_answered_question_still_filed_as_open()
                + source_the_repository_would_not_receive())

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
