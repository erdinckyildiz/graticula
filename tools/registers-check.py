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


def a_serving_assembly_that_reaches_for_the_network():
    """Production code constructing an outbound connection.

    **[Q-15](../docs/open-questions.md), the half that had nothing holding it.**
    That question's checklist established on 2026-08-25 that this server makes no
    outbound connection but the one to PostgreSQL -- zero `HttpClient`, zero
    `WebRequest`, zero raw `Socket` in `src/` -- and then said the uncomfortable
    part out loud: **three of the four air-gap properties are true because of
    decisions taken for other reasons, so nothing protects them.** A dependency
    that reaches for the network, or a renderer that downloads a face, would break
    air-gap without breaking a test.

    **This is what protects one of them.** Air-gapped operation is a property of
    the artefact, and a property nobody checks is a property that lasts until
    somebody needs a quick lookup.

    **`src/` only, and tests are not an oversight.** A conformance suite drives
    this server over HTTP by design -- that is a client reaching in, not the server
    reaching out, and forbidding it would forbid the tests that prove the surface
    works. `tools/` is likewise excluded: `pro-probe.py` and the CITE runs exist to
    talk to things.

    **What it cannot see.** A dependency that opens a socket inside its own code is
    invisible here, which is why this is one guard and not the answer -- and it is
    why [DEPENDENCY-LICENSES.md](../DEPENDENCY-LICENSES.md) enumerates the native
    payload rather than trusting a summary.
    """
    src = os.path.join(conditions.ROOT, "src")

    if not os.path.isdir(src):
        return ["src/ is not there, so this check is reading nothing."]

    reaching = re.compile(
        r"\bnew\s+HttpClient\b"
        r"|\bnew\s+System\.Net\.Http\.HttpClient\b"
        r"|\bWebRequest\.Create\b"
        r"|\bnew\s+Socket\s*\("
        r"|\bnew\s+TcpClient\b"
        r"|\bnew\s+ClientWebSocket\b"
        r"|\bAddHttpClient\b")

    problems = []
    read = 0

    for base, _, names in os.walk(src):
        if any(part in base for part in ("bin", "obj", "wwwroot")):
            continue

        for name in names:
            if not name.endswith(".cs"):
                continue

            path = os.path.join(base, name)

            try:
                text = io.open(path, encoding="utf-8").read()
            except OSError:
                continue

            read += 1

            # Struck-through prose and comments describe; they do not connect.
            living = "\n".join(
                line for line in text.splitlines()
                if not line.lstrip().startswith(("//", "///", "*")))

            found = reaching.search(living)

            if not found:
                continue

            shown = os.path.relpath(path, conditions.ROOT).replace(os.sep, "/")

            problems.append(
                f'{shown} constructs `{found.group(0).strip()}`. This server makes no outbound '
                "connection but the one to PostgreSQL, and Q-15's checklist rests on that being "
                "true rather than on it having been true. An air-gapped deployment cannot reach "
                "whatever this is for.")

    if read < 50:
        problems.append(
            f"only {read} source files were read, so this check is looking at almost nothing.")

    return problems


def a_test_project_ci_never_runs():
    """A test project the workflow does not run, and does not say it skips.

    **179 tests, found 2026-08-25 while closing [D-63](../docs/architecture-debt.md).**
    `ci.yml` names its suites one `dotnet test` line at a time, and three projects
    written after that list was made never joined it: `Api.Wfs` (112 tests),
    `Raster.Tiff` (49) and `Render.Skia` (18). All three pass. None had ever been
    run by CI, and D-63's cost cell said in its own words that the workflow *runs
    all eight suites*.

    **`Raster.Tiff` appeared in `ci.yml` exactly once** -- as a path to a corpus
    file, added the same morning for an unrelated fixture. A grep for the project
    name found it and looked reassuring, which is why this asks about the `dotnet
    test` line rather than about the string.

    **The directory is the truth and the list is the copy**, which is
    [D-46](../docs/architecture-debt.md)'s shape: a hand-maintained enumeration
    beside the thing it enumerates. So the check reads `tests/` and requires each
    project to be run -- or to say, in the workflow, that it is deliberately not.
    """
    workflows = os.path.join(conditions.ROOT, ".github", "workflows")
    tests = os.path.join(conditions.ROOT, "tests")

    if not os.path.isdir(tests) or not os.path.isdir(workflows):
        return ["tests/ or .github/workflows/ is not there, so this check reads nothing."]

    ran = ""

    for name in sorted(os.listdir(workflows)):
        if name.endswith((".yml", ".yaml")):
            try:
                ran += io.open(os.path.join(workflows, name), encoding="utf-8").read()
            except OSError:
                continue

    projects = [
        name for name in sorted(os.listdir(tests))
        if name.endswith(".Tests")
        and os.path.isfile(os.path.join(tests, name, name + ".csproj"))]

    if len(projects) < 5:
        return [f"only {len(projects)} test projects were found, so this check is reading "
                "nothing. A check that cannot fail is worse than no check."]

    problems = []

    for project in projects:
        # <b>The `dotnet test` line, not the name.</b> A project's name can appear in a
        # workflow for reasons that have nothing to do with running it -- a corpus path
        # did exactly that -- and a check fooled by a mention is a check that reports
        # clean on the case it exists for.
        if re.search(r"dotnet test\s+tests/" + re.escape(project) + r"\b", ran):
            continue

        problems.append(
            f"tests/{project} is never run by any workflow. Add a `dotnet test` step, or "
            "say in the workflow why it is excluded -- an untested project that nobody "
            "declared untested is coverage the build claims and does not have. D-63, D-46.")

    return problems


def a_real_data_test_without_the_trait_ci_filters_on():
    """A test that needs a real extract and is not marked as needing one.

    **[ADR-048](../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md), and
    this check exists because the first attempt at it tagged instances instead of
    the class.** Three classes were traited on 2026-08-25 because those three were
    named in a CI failure. There were **seven**. The other four failed on the next
    run -- 24 tests -- and one of them was missed for a reason worth keeping: the
    search was for `planet_osm_polygon`, and `PostGisTileSourceTests` reads
    `public.osm_buildings`. Tagging what you were shown rather than what you were
    looking for is [D-46](../docs/architecture-debt.md) exactly.

    **The rule reads the sentence these classes already say.** They all refuse in
    the same words -- *"... is not loaded"* -- because they fail rather than skip
    when the extract is absent, which is deliberate. So the sentence is the
    signal: a class that says it needs real data must carry the trait CI filters
    on, or CI will run it and it will fail.

    **It cannot go stale**, because it derives from the code rather than from a
    list. A new suite that reads a real extract is caught the moment it is written,
    which is earlier than the CI run that would otherwise find it.
    """
    tests = os.path.join(conditions.ROOT, "tests")

    if not os.path.isdir(tests):
        return ["tests/ is not there, so this check is reading nothing."]

    problems = []
    seen = 0

    for base, _, names in os.walk(tests):
        if any(part in base for part in ("bin", "obj")):
            continue

        for name in names:
            if not name.endswith(".cs"):
                continue

            path = os.path.join(base, name)

            try:
                text = io.open(path, encoding="utf-8").read()
            except OSError:
                continue

            if "is not loaded" not in text:
                continue

            seen += 1

            if 'Trait("Needs", "RealCorpus")' in text:
                continue

            shown = os.path.relpath(path, conditions.ROOT).replace(os.sep, "/")

            problems.append(
                f'{shown} refuses with "is not loaded", so it needs a real extract — and it '
                'does not carry [Trait("Needs", "RealCorpus")], which is what CI filters on. '
                "CI will run it and it will fail. ADR-048.")

    if seen < 5:
        problems.append(
            f"only {seen} real-data test classes were found, so this check is reading almost "
            "nothing. A check that cannot fail is worse than no check.")

    return problems


def a_corpus_file_a_test_reads_but_a_clone_does_not_get():
    """A test naming a corpus file that an ignore rule keeps out of a clone.

    **Found by the first CI run this repository ever completed, 2026-08-25.**
    `BoundedArchiveTests` read `corpus/shapefile/points.shp` by name. That file
    exists on the machine the test was written on and reaches no clone --
    `.gitignore`'s `*.shp` hides it, and the rule beside it records that the loose
    shapefiles were **deliberately** left out because the zips are the tracked
    artefact. 274 tests passed and that one failed, and no local run could have
    told anybody: locally the file is there.

    **This is [D-62](../docs/architecture-debt.md) in data rather than in source,
    and the check written for D-62 could not see it.** That one asks git which of
    the repository's own `.cs`, `.csproj`, `.js`, `.css` and `.html` files an
    ignore rule hides. A corpus file is none of those.

    **Why this is narrow rather than *nothing under tests/ may be ignored*.** Some
    of it is meant to be ignored -- the loose shapefiles beside the zips are scratch
    that somebody chose not to commit, and a check that demanded they be tracked
    would be arguing with a decision instead of protecting one. So the rule is not
    about ignoring; it is about **reaching**: a file a test names must be a file a
    clone receives.
    """
    # <b>`Corpus` and `Corpus()`, because the second one was missed.</b>
    # `ProjectionResolutionTests` builds its path in a method rather than a
    # property, so this pattern did not see it and CI found the file two runs
    # later. A checker that only knows one spelling of the thing it checks is a
    # checker that reports clean on the case it was written for.
    corpus = re.compile(
        r"""Path\.Combine\(\s*Corpus\(?\)?\s*,\s*"([^"]+)"\s*\)"""
        r"""|"(corpus/[^"]+)\"""")

    tests = os.path.join(conditions.ROOT, "tests")

    if not os.path.isdir(tests):
        return ["tests/ is not there, so this check is reading nothing."]

    wanted = {}

    for base, _, names in os.walk(tests):
        if any(part in base for part in ("bin", "obj")):
            continue

        for name in names:
            if not name.endswith(".cs"):
                continue

            path = os.path.join(base, name)

            try:
                text = io.open(path, encoding="utf-8").read()
            except OSError:
                continue

            # <b>Read the directory the file's own `Corpus` points at.</b> Several
            # test classes define one and they do not agree, so resolving it per
            # file is the only reading that is not a guess.
            # <b>Both spellings, because the first version only knew one and reported
            # four files at the wrong path.</b> `Corpus =>` and `Corpus =` are the same
            # declaration to a reader and different regexes to a checker, and the tiff
            # tests use the second — so their corpus resolved to the project root,
            # every file looked untracked, and the check accused a directory that was
            # correct. A guard that names the wrong file sends somebody hunting.
            root = re.search(
                r"""Corpus\s*(?:\(\)\s*)?=[>]?\s*(?:.*?return\s*)?Path\.Combine\("""
                r"""\s*(?:AppContext\.BaseDirectory|at!\.FullName)\s*,\s*"""
                r"""((?:"[^"]+"\s*,?\s*)+)\)""", text, re.S)

            folders = re.findall(r'"([^"]+)"', root.group(1)) if root else []

            for found in corpus.finditer(text):
                named = found.group(1) or found.group(2)

                if named.startswith("corpus/"):
                    parts = named.split("/")
                else:
                    parts = folders + [named]

                if not parts:
                    continue

                wanted.setdefault(
                    os.path.join(os.path.dirname(path), *parts),
                    os.path.relpath(path, conditions.ROOT).replace(os.sep, "/"))

    if not wanted:
        return ["no corpus file was found named in any test, so this check is reading "
                "nothing. A check that cannot fail is worse than no check."]

    problems = []

    for target, named_in in sorted(wanted.items()):
        shown = os.path.relpath(target, conditions.ROOT).replace(os.sep, "/")

        try:
            tracked = subprocess.run(
                ["git", "-C", conditions.ROOT, "ls-files", "--error-unmatch", shown],
                capture_output=True, text=True).returncode == 0
        except OSError:
            continue

        if not tracked:
            problems.append(
                f"{named_in} reads {shown}, which git does not track — so it is on this "
                "machine and in no clone. A test that passes only where it was written "
                "proves nothing about the repository somebody else gets. D-62, Q-117.")

    return problems


def an_outbound_licence_claim_that_is_stale():
    """A document saying this project is Apache-2.0, or that it is open source.

    **[ADR-047](../docs/adr/ADR-047-the-outbound-licence-is-elastic-2.md), and it
    was eight sites rather than one.** The outbound licence changed on 2026-08-25
    from Apache-2.0 to the Elastic License 2.0, and the old one was not merely
    *mentioned* in those documents -- four of them **reasoned from it**. ADR-019
    argued from having no licence to meter, ADR-020 from a redistribution warranty,
    ADR-027 from what an outbound licence can carry, ADR-025 declined a contributor
    agreement on the ground that relicensing was *already declined*, and
    `CONTRIBUTING.md` promised contributors the decision was **open source,
    permanently**. That last one is a promise to people, not a note to ourselves.

    **This is [D-130](../docs/architecture-debt.md)'s shape with a new cause**, and
    the reason it gets a check rather than a sweep is that the sweep is what keeps
    being late.

    **What it reads and why the exemptions are shaped this way.** Struck text is how
    this repository records what it used to say, so it is removed first -- across the
    whole document, because these corrections span lines. `reviews/` and `research/`
    are records of what was believed on a date and are never rewritten.
    `phase-0-exit-plan.md` records the 2026-08-13 choice as a completed task, which is
    history rather than a claim. ADR-047 itself must be able to name the licence it
    replaced.

    **Dependencies keep their own licences** -- Apache-2.0 is correct in
    `DEPENDENCY-LICENSES.md` for xunit, NetTopologySuite and GDAL's MRF component, so
    the pattern requires the claim to be about *this* project.
    """
    exempt = ("reviews", "research", "phase-0-exit-plan.md",
              "ADR-047-the-outbound-licence-is-elastic-2.md")

    ours = re.compile(
        r"(?:we are|this product is|this project is|the project is|outbound licence:|"
        r"licensed under the)\s+(?:the\s+)?\**(Apache[- ]2\.0|Apache License)",
        re.I)

    promise = re.compile(
        r"(?:licensing|licence|license)[^.]{0,60}\bis\s+open[- ]source\b"
        r"|\bopen[- ]source\b[^.]{0,20},\s*permanently", re.I)

    problems = []

    for base, _, names in os.walk(conditions.ROOT):
        if any(part in base for part in (".git", "REFERENCES", "node_modules", "obj", "bin")):
            continue

        for name in names:
            if not name.endswith((".md", ".html")):
                continue

            shown = os.path.relpath(os.path.join(base, name), conditions.ROOT)
            shown = shown.replace(os.sep, "/")

            if any(part in shown for part in exempt):
                continue

            try:
                text = io.open(os.path.join(base, name), encoding="utf-8").read()
            except OSError:
                continue

            living = re.sub(r"~~.*?~~", "", text, flags=re.S)
            flowed = re.sub(r"\s*\n\s*", " ", living)

            for found in ours.finditer(flowed):
                problems.append(
                    f'{shown} says "{found.group(0)[:60]}". The outbound licence has been '
                    "the Elastic License 2.0 since 2026-08-25 (ADR-047). Four documents "
                    "reasoned from the old one, so this is not a cosmetic correction. D-130.")

            for found in promise.finditer(flowed):
                problems.append(
                    f'{shown} says "{found.group(0)[:60]}". This project is '
                    "source-available, not open source (ADR-047), and CONTRIBUTING.md "
                    "promised contributors otherwise until 2026-08-25. D-130.")

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

    # <b>The same words without the anchor.</b> `verdict` is anchored at the start of the
    # cell, which is right for an ordinary row and wrong for a struck one — there the
    # question it no longer is comes first. Using `search` on an anchored pattern silently
    # behaves like `match`, which is how the widened check passed its own falsification once.
    inside = re.compile(
        r"\*{0,2}(ANSWERED|Answered|RESOLVED|Resolved|Re-answered|Re-ANSWERED"
        r"|DISSOLVED|Dissolved|WITHDRAWN|Withdrawn|CLOSED|Closed)\b")

    problems = []
    rows = 0

    for line in lines[:answered]:
        cells = [c.strip() for c in COLUMN.split(line.strip().strip("|"))]

        if len(cells) < 2 or not re.match(r"^Q-\d+$", cells[0]):
            continue

        # <b>A struck question with no verdict beside it is withdrawn, and its shape is
        # settled.</b> One that is struck *and* answered is a different thing, and this
        # exemption swallowed seven of them — [D-189](../docs/architecture-debt.md). The
        # strikethrough says *this is no longer the question*; the verdict says *and here is
        # the answer*, which is what the Answered section is for.
        if cells[1].startswith("~~") and not inside.search(cells[1]):
            continue

        rows += 1

        for which, cell in enumerate(cells[1:], start=1):
            # <b>`match` at the start, or anywhere inside a struck cell.</b> An ordinary
            # cell opens with its verdict; a struck one opens with the question it no longer
            # is, and puts the verdict after the second `~~`.
            found = verdict.match(cell) or (
                inside.search(cell) if cell.startswith("~~") else None)

            if not found:
                continue

            problems.append(
                f"{cells[0]} is filed under an open heading and its own cell {which} says "
                f'"{found.group(1)}". The page classifies by section, so the answer is '
                "invisible to it and the question is counted as live uncertainty. Move the "
                "row into Answered; nine had accumulated when this check was written, and "
                "seven more when its strikethrough exemption was narrowed (D-189).")
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

    # <b>Counted against a second, dumber reading of the same lines rather than
    # against a number.</b> This was `rows < 40`, which is a floor on the *backlog*
    # dressed as a floor on the parse: it fired on 2026-08-26 because questions had
    # been answered, which is the register working. What the guard is actually for is
    # the cell splitter above going wrong -- `split(" | ")` is naive and a row written
    # with different spacing would be skipped in silence. So the honest comparison is
    # against how many rows a reader who only looks for `| Q-` can see: if those two
    # disagree, the parse is dropping rows, and no amount of answering questions can
    # make them disagree.
    visible = sum(
        1 for line in lines[:answered]
        if re.match(r"^\|\s*~*\s*Q-[\w-]+\s*~*\s*\|", line.strip()))

    if rows != visible:
        problems.append(
            f"{visible} question rows are visible above ## Answered and only {rows} parsed, so "
            "the cell splitter is dropping rows and every check above it is reading less than "
            "it looks like. A check that cannot fail is worse than no check.")

    if visible == 0:
        problems.append(
            "no question rows were found above ## Answered at all, so this check is reading "
            "nothing.")

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
            # <b>Prose, and the files that decide what ships -- widened 2026-08-26,
            # [D-170](../docs/architecture-debt.md).</b> This read `.md` and `.html`
            # only, on the reasonable-sounding ground that a rename is a documentation
            # problem. It is not: `deploy/datastore.Dockerfile` carried
            # `org.opencontainers.image.title="gis-server datastore"` for nine days
            # after ADR-032, which is the old name in the one artefact a user actually
            # receives -- the check was green the whole time because it never opened
            # the file. Dockerfiles and compose files are added rather than every
            # extension: `.cs` would drag in `HostSettings`, which reads the legacy
            # configuration keys on purpose (ADR-032 5), and a guard that needs a
            # list of excuses to stay green stops being read.
            if not (name.endswith((".md", ".html", ".yaml", ".yml"))
                    or "dockerfile" in name.lower()):
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


def a_question_the_status_page_cannot_see():
    """A row in the questions register that the generated page silently drops.

    **Found 2026-08-25, and it had been true for as long as the page existed.**
    `tools/status-page.py` matched question ids with `^Q-\\d+$` while this file
    has always used `^Q-[\\w-]+$`. Nine rows carry a suffix -- Q-58a, Q-58b,
    Q-58c, Q-06a, Q-06b, Q-17a, Q-17b, Q-17c, Q-24-old -- so the page dropped
    every one of them without a word. Eight are answered and cost only their
    place in the history. **Q-58c is open**, is in the register, and the open
    count the board printed was short by exactly it.

    **The failure is not the pattern, it is that there were two of them.** One
    rule -- *what is a question row* -- read by two tools that never compared
    notes, and the reader nobody could see was the one that drifted. CLAUDE.md
    §2 records the same shape between `conditions.py` and the status page, which
    is why that rule now has a single implementation. This check is the cheaper
    version of the same fix: let the two disagree, and fail the build when they
    do.

    So it compares *sets of ids*, not counts. A count that matches by accident --
    one row dropped, one row added -- would pass a tally and still be wrong.
    """
    questions = os.path.join(conditions.ROOT, "docs", "open-questions.md")

    try:
        text = io.open(questions, encoding="utf-8").read()
    except OSError as problem:
        return [f"open-questions.md could not be read: {problem}"]

    in_register = set()

    for line in text.splitlines():
        if not line.startswith("|"):
            continue

        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]

        if cells and re.match(r"^Q-[\w-]+$", cells[0]):
            in_register.add(cells[0])

    if not in_register:
        return ["no question rows were parsed from open-questions.md, so this check "
                "is reading nothing."]

    # <b>The page's own reader, imported rather than imitated.</b> Re-implementing
    # it here would create the third reader of the same rule and reproduce the
    # defect this check exists to catch.
    try:
        import importlib.util

        where = os.path.join(os.path.dirname(os.path.abspath(__file__)), "status-page.py")
        spec = importlib.util.spec_from_file_location("status_page", where)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        on_page = {row["id"] for row in module.questions()}
    except Exception as problem:
        return [f"tools/status-page.py could not be asked what it reads: {problem}"]

    missing = sorted(in_register - on_page)
    invented = sorted(on_page - in_register)
    complaints = []

    for question in missing:
        complaints.append(
            f"{question} is a row in docs/open-questions.md that tools/status-page.py does not "
            "read, so it appears on no board and is counted in no total. Two readers of one "
            "rule disagreed once before, between conditions.py and the status page; the fix "
            "is to make the page's pattern match the register's, not to renumber the question."
        )

    for question in invented:
        complaints.append(
            f"{question} is on the status page and is not a row in docs/open-questions.md. "
            "The page is generated from the register, so this means the page's parser is "
            "reading something that is not a question row."
        )

    return complaints


def a_test_a_register_cites_that_is_not_there():
    """A test name a register row claims, against the tests that exist.

    **A row that names a test is making a claim about the repository**, and it is the
    kind that decays silently: the test is renamed as part of some later repair, the
    row goes on naming the old one, and the next reader chasing the evidence finds
    nothing. D-64 cited `ShapefileReaderTests.A_hole_is_recognised_by_containment_rather_than_winding`
    for a fortnight after that class became `ShapefileCorpusTests`.

    **Method, not method name, is checked too.** A class that survives a rename while
    the method inside it is renamed is the same decay one level down, and it is the
    more common one.

    **External suites are named here on purpose.** The registers cite the OGC CITE
    engines' own test names -- those are evidence about somebody else's suite and
    there is nothing in `tests/` for them to match. Each is listed with the engine it
    belongs to rather than waved through by a pattern, because a pattern would also
    wave through our own typos.
    """
    external = {
        # ogccite/ets-wfs20, quoted by D-158's re-run.
        "PropertyIsNilOperatorTests",
    }

    sources = {}

    for folder, dirs, names in os.walk(os.path.join(conditions.ROOT, "tests")):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]

        for name in names:
            if name.endswith(".cs"):
                try:
                    sources[name[:-3]] = io.open(
                        os.path.join(folder, name), encoding="utf-8").read()
                except OSError:
                    continue

    if not sources:
        return ["tests/ holds no .cs files, so this check is reading nothing."]

    cited = re.compile(r"`?([A-Z][A-Za-z0-9]*Tests)(?:\.([A-Za-z_][A-Za-z0-9_]*))?`?")

    # <b>A row is allowed to name a test that is gone, if it says it is gone.</b> The
    # same escape `the_former_product_name` gives the old product name, and for the
    # same reason: a register that cannot record its own history is worse than one
    # with a stale name in it. What is refused is a row that cites evidence in the
    # present tense and sends the reader nowhere.
    excuses = (
        "no longer exists", "was renamed", "renamed to", "became ",
        "used to be", "has been replaced", "no longer a test",
    )

    problems = []
    seen = set()

    for register in ("architecture-debt.md", "open-questions.md"):
        where = os.path.join(conditions.ROOT, "docs", register)

        try:
            lines = io.open(where, encoding="utf-8").read().splitlines()
        except OSError:
            continue

        for number, line in enumerate(lines, 1):
            if not line.strip().startswith("|"):
                continue

            flat = " ".join(line.lower().split())
            excused = any(word in flat for word in excuses)

            for cls, method in cited.findall(line):
                if (cls, method) in seen or cls in external or excused:
                    continue

                seen.add((cls, method))

                if cls not in sources:
                    problems.append(
                        f"docs/{register}:{number} cites {cls}, which is not a test class in "
                        "tests/. Either it was renamed and the row was not, or it never "
                        "existed -- and a row that names evidence nobody can find is worse "
                        "than one that names none.")
                elif method and not re.search(rf"\b{re.escape(method)}\b", sources[cls]):
                    problems.append(
                        f"docs/{register}:{number} cites {cls}.{method}, and {cls} has no such "
                        "member. The class survived a rename and the method did not, which is "
                        "the same decay one level down.")

    return problems


def a_port_documented_as_a_contract():
    """No document declares a port interface, which is how a plugin API starts.

    Why this is a check
    -------------------
    [ADR-006](../docs/adr/ADR-006-plugin-model.md) decided *not yet* on a plugin model,
    and its first condition is that **ports are not documented as public contracts while
    that decision stands** -- *"publishing them informally is how a contract is created by
    accident"*.

    The accident has a shape, and it is a page rather than a line: somebody writes *how to
    write a provider*, pastes `public interface IFeatureSource` into it, and from that
    afternoon the interface cannot change without breaking whoever read it. Nobody decides
    that; it is decided by the paste.

    So what is refused is a **declaration**, not a mention. The registers name
    `IFeatureSource.CountUpToAsync` and should: that is a fact about how this server
    works, in a row explaining a measurement. A signature offered for somebody to
    implement against is a different act, and it is the one this forbids.

    Checked 2026-08-27 for the first time and found nothing to fix, which is the point:
    the condition has been kept by nobody having written that page yet, and this is what
    keeps it kept.

    ADR-006 is excused. It is the decision, and a decision that says *not yet* may show
    what it is saying not-yet to.
    """
    problems = []

    docs = os.path.join(conditions.ROOT, "docs")

    for name in sorted(os.listdir(docs)):
        if not name.endswith(".md"):
            continue

        for line in io.open(os.path.join(docs, name), encoding="utf-8"):
            if re.search(r"^\s*(public\s+)?interface\s+I[A-Z]\w+", line):
                problems.append(
                    f"docs/{name} declares '{line.strip()[:60]}'. ADR-006 condition 1: ports "
                    "are not documented as public contracts while that decision stands, "
                    "because publishing them informally is how a contract is created by "
                    "accident. Name the port and what it does; do not paste its signature.")

    for name in sorted(os.listdir(conditions.ADRS)):
        if not name.endswith(".md") or name.startswith("ADR-006"):
            continue

        for line in io.open(os.path.join(conditions.ADRS, name), encoding="utf-8"):
            if re.search(r"^\s*(public\s+)?interface\s+I[A-Z]\w+", line):
                problems.append(
                    f"docs/adr/{name} declares '{line.strip()[:60]}'. ADR-006 condition 1 "
                    "-- see the check's own note. ADR-006 itself is excused; nothing else is.")

    return problems


def an_adr_that_does_not_say_where_its_state_lives():
    """Every ADR after 019 inventories its own state, catalogue against runtime.

    Why this is a check
    -------------------
    [ADR-012](../docs/adr/ADR-012-clustering.md) is deferred, and the one thing it
    requires while deferred is that *every other ADR must state which of its state is
    node-local and which is shared* -- because "that inventory is the real precondition
    for clustering, and collecting it late is expensive". ADR-019 condition 3 extends it
    to the axis that matters before any of that: catalogue against runtime.

    **Measured 2026-08-27: 1 of 29 subsequent ADRs said anything at all**, and that one
    was ADR-029 mentioning node-local about the tile cache in passing. Twenty-nine
    decisions had been taken without the inventory either of them asks for, and nothing
    noticed, because a requirement written in one ADR's prose is a requirement nobody
    reads twice. The backlog was written the same day; this is what stops it coming
    back.

    What it does not check
    ----------------------
    **Whether the sentence is true.** A line reading `**State.** None.` on an ADR that
    adds a table would pass, and no reasonable check catches that. What this buys is that
    the question is asked at all -- which, at 1 of 29, was the whole failure.

    ADR-019 and below are exempt: the condition says *every subsequent ADR*, and the
    inventory for the earlier ones lives in ADR-002 §5 and ADR-012 itself.
    """
    problems = []

    for name in sorted(os.listdir(conditions.ADRS)):
        if not name.startswith("ADR-") or not name.endswith(".md"):
            continue

        try:
            number = int(name[4:7])
        except ValueError:
            continue

        if number <= 19:
            continue

        path = os.path.join(conditions.ADRS, name)
        text = io.open(path, encoding="utf-8").read()

        if "**State.**" in text:
            continue

        problems.append(
            f"docs/adr/{name} does not say where its state lives. ADR-019 condition 3 and "
            "ADR-012 both ask for it: one **State.** line in Consequences, saying what this "
            "decision puts in the catalogue and what it holds at runtime -- and node-local "
            "against shared where they differ. 'None' is a complete answer for a decision "
            "that stores nothing, and most of them do.")

    return problems


SECRET_NAMES = '(?i)(secret[_a-z]*key|api[_-]?key|private[_-]?key|password|passwd|pwd)\\s*[:=]\\s*(?P<value>\\"[^\\"\\n]*\\"|\'[^\'\\n]*\'|[^\\s;,\\"\']+)'
GENERATED_KEY = '^(?:[A-Za-z0-9+/]{20,}={0,2}|[0-9a-fA-F]{32,})$'


# <b>A path that exists on one machine.</b> A Windows or POSIX home directory, or the
# session-scoped scratchpad this project's tooling writes to, spelled out in a committed file.
# The scratchpad form is the one that actually happened: it carries a session identifier, so
# it is not merely unportable, it is unportable *and* different tomorrow.
MACHINE_PATH = (
    r"(?i)(?:[A-Z]:[\\/]Users[\\/][^\\/\s\"']+"
    r"|/(?:home|Users)/[^/\s\"']+/"
    r"|AppData[\\/]Local[\\/]Temp[\\/]claude)")


def a_developers_own_path_in_a_committed_file():
    """
    An absolute path into one machine's home or scratch directory, in a file that ships.

    <b>Found 2026-09-02 by running the rehearsals rather than reading them.</b> Two of the six
    -- `rollback-rehearsal.sh` and `stale-component-rehearsal.sh` -- carried
    `S="C:/Users/<name>/AppData/Local/Temp/claude/<project>/<session id>/scratchpad"` and
    `cd "c:/Personal/Projects/GIS"`, so they could not run on any machine but one, in any
    session but one. Four benchmark scripts carried the same directory. The repository is
    given away; a tool that only its author can run is a tool that is not really there, and
    the session identifier is noise nobody meant to publish.

    <b>It reads `tools/` and `.github/` strictly and `benchmarks/` too.</b> `/benchmarks` is
    disposable by CLAUDE.md §1 and is never promoted, which is a reason not to polish it and
    not a reason to publish somebody's home directory in it -- the numbers there are evidence
    other people are invited to reproduce.

    <b>Documentation is exempt, deliberately.</b> A path in prose is often the point: an ADR
    recording what a run did, or a debt row quoting a message that contained one. What this
    forbids is a path a program *uses*.
    """
    problems = []

    machine = re.compile(MACHINE_PATH)

    try:
        listing = subprocess.run(
            ["git", "-C", conditions.ROOT, "ls-files",
             "--cached", "--others", "--exclude-standard"],
            capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return problems

    for path in listing.stdout.split("\n"):
        path = path.strip()

        if not path.startswith(("tools/", "benchmarks/", ".github/", "src/", "tests/")):
            continue

        if path.endswith((".md", ".html", ".png", ".jpg", ".gif", ".pdf")):
            continue

        if path.endswith("registers-check.py"):
            continue

        whole = os.path.join(conditions.ROOT, path)

        try:
            with open(whole, encoding="utf-8") as handle:
                text = handle.read()
        except (OSError, UnicodeDecodeError):
            continue

        for number, line in enumerate(text.split("\n"), start=1):
            if line.lstrip().startswith(("#", "//", "*")):
                continue

            if machine.search(line):
                problems.append(
                    f"{path}:{number} names a path that exists on one machine:\n"
                    f"    {line.strip()[:120]}\n"
                    "  A committed script has to run on somebody else's computer. Take the "
                    "directory from the environment with a temporary default, and find the "
                    "repository from the script's own location rather than from where it was "
                    "written.")

    return problems


def a_secret_committed_to_a_public_repository():
    """
    A generated key written into a tracked file.

    <b>D-191, found by a pre-push scan on 2026-08-27 and already two commits too late.</b>
    Three rehearsal scripts carried `Graticula__SecretKey` as a base64 literal.
    `Graticula:SecretKey` is the AES-256 key that seals every registered data source's
    credentials (ADR-032, layer 2), so a key in a public repository is a key nobody may ever use
    for anything real -- and the likeliest way somebody does is copying it out of a script in
    the repository that shows how to start the server.

    <b>Entropy rather than the word, and the first version proved why.</b> Written to flag any
    secret-shaped name set to a literal, it found 161 things and the two that mattered were
    lost in them: a test fixture's own throwaway password is not a secret, and a check
    somebody has to ignore is a check somebody turns off. What this looks for is what a
    *generated* key looks like -- twenty characters of base64 or thirty-two of hex, which is a
    shape nobody types -- with an entropy floor under that, because `AAAAAAAA...=` is base64
    for thirty-two zero bytes and is exactly as secret as the number nought. Both CI workflows
    carry one, and they are placeholders rather than leaks.

    <b>Two versions could not fail, and the falsification caught both.</b> The name is
    matched without a leading word boundary: `_` is a word character, the setting that leaked
    is spelled `Graticula__SecretKey`, and `\\bsecretkey` cannot match after two
    underscores. The second listed only what git already tracks, so the untracked file the key
    was put back into was never read. Both times the check reported clean with the key in the
    working tree -- the fourth and fifth times in this repository that a check has passed its
    own falsification, and the reason nothing here is trusted until it has been made to fail.

    <b>Not a substitute for reading the diff.</b> It catches the shape that already got
    through once. The repository is public and the rule is that every commit is read before it
    is pushed.
    """
    problems = []

    names = re.compile(SECRET_NAMES)
    generated = re.compile(GENERATED_KEY)

    try:
        # <b>`--others` as well as the index, and that was the second falsification.</b>
        # Plain `ls-files` lists what git already tracks, so a brand-new script carrying a key
        # is invisible to this check -- which is the case that matters most, because a secret
        # arrives in a file that is being added rather than in one that has been there for
        # months. The falsification put the key back into an untracked file and the check
        # reported clean. `--exclude-standard` keeps `.gitignore` honoured, so `bin/` and
        # `obj/` do not come in with it.
        listing = subprocess.run(
            ["git", "-C", conditions.ROOT, "ls-files",
             "--cached", "--others", "--exclude-standard"],
            capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return problems

    for path in listing.stdout.split("\n"):
        path = path.strip()

        if not path.startswith(("tools/", "src/", "tests/", "docs/", ".github/")):
            continue

        if path.endswith((".png", ".jpg", ".gif", ".pdf", ".ico", ".woff", ".woff2")):
            continue

        # This file names the shape it looks for.
        if path.endswith("registers-check.py"):
            continue

        try:
            text = io.open(
                os.path.join(conditions.ROOT, path), encoding="utf-8").read()
        except (UnicodeDecodeError, OSError):
            continue

        for number, line in enumerate(text.split("\n"), start=1):
            for hit in names.finditer(line):
                bare = hit.group("value").strip("'").strip(chr(34))

                if not generated.match(bare):
                    continue

                # <b>A key with no entropy is not a secret, and both CI workflows hold one.</b>
                # `AAAAAAAA...=` is base64 for thirty-two zero bytes and is exactly as secret
                # as the number nought: it is the shape a placeholder takes when a setting
                # insists on a valid AES-256 key. Eight distinct characters is far below
                # anything a generator produces and far above what a placeholder uses.
                if len(set(bare.rstrip("="))) < 8:
                    continue

                problems.append(
                    path + ":" + str(number) + " sets " + hit.group(1) + " to what looks like "
                    "a generated key. This repository is public, so a committed key is a "
                    "published one -- and this is the key class that seals data source "
                    "credentials. Read it from the environment instead: D-191, and "
                    "tools/rotate-rehearsal.sh is the shape.")

    return problems


BACKTICK_PATH = '`([A-Za-z0-9_./-]+\\.(?:md|cs|py|sh|ya?ml|json|csproj|sln|html|js|css)|[A-Za-z0-9_-]+/[A-Za-z0-9_./-]+)`'


def a_file_a_security_promise_names_that_is_not_there():
    """
    A path named in `SECURITY.md` or `CONTRIBUTING.md` that does not exist.

    <b>[ADR-025](../docs/adr/ADR-025-governance-and-maintenance.md) condition 4</b>: *the claims
    SECURITY.md makes about scope stay true. If any is changed or removed, that list is wrong,
    and a scope statement that is wrong invites the wrong reports and dismisses the right
    ones.* A condition worded as *stays true* is a promise somebody has to keep remembering,
    and this repository's whole debt register is what that costs -- so the mechanical half of
    it is checked here.

    <b>Paths only, because only paths can be checked.</b> Whether *the cookie authenticates
    GET and HEAD only* is still true cannot be read off a filename; it was verified by hand on
    2026-08-27 against `Authentication.cs`, and so were the other three trade-offs. What a
    check can do is catch the drift that already happened: the out-of-scope list named *the
    `docker-compose` file* and the file is `compose.yaml`, so a reporter looking for it would
    not have found it.

    <b>These two files rather than every document.</b> A wrong path in an ADR is a nuisance;
    a wrong path in the file that tells a stranger what is in scope is how a real report gets
    dismissed as out of scope, or a known trade-off gets reported as news.
    """
    problems = []

    named = re.compile(BACKTICK_PATH)

    for promise in ("SECURITY.md", "CONTRIBUTING.md"):
        full = os.path.join(conditions.ROOT, promise)

        if not os.path.exists(full):
            problems.append(
                promise + " does not exist, and ADR-025 condition 1 requires it before this "
                "repository is public. It is public.")
            continue

        text = io.open(full, encoding="utf-8").read()

        # <b>Line numbers, because the same path can appear twice.</b> The first run reported
        # `architecture-debt.md` twice with nothing to tell the two apart, and one of the two
        # was a bullet written a minute earlier.
        seen = {}
        for number, line in enumerate(text.split(chr(10)), start=1):
            for hit in named.finditer(line):
                seen.setdefault(hit.group(1), number)

        for path, number in seen.items():

            # A bare filename with an extension may be prose rather than a path -- but only
            # if nothing in the repository is called that either.
            if os.path.exists(os.path.join(conditions.ROOT, path)):
                continue

            problems.append(
                promise + ":" + str(number) + " names `" + path + "`, which is not in this "
                "repository. A scope statement that points at a file nobody can find "
                "dismisses the right reports and invites the wrong ones -- ADR-025 "
                "condition 4.")

    return problems


AN_ADR_CALLING_A_QUESTION_OPEN = '\\b(Q-\\d{1,3})\\b(?:(?!\\. [A-Z]).){0,160}?\\b(opens? it|is open|remains open|still open|is unanswered|has no answer|opens the question|is still unanswered)\\b'


def an_adr_that_calls_an_answered_question_open():
    """
    An ADR saying a question is open when the register says it is answered.

    <b>[D-130](../docs/architecture-debt.md)'s shape, and it took a running server to find one
    on 2026-08-27.</b> ADR-009 §2.1 said *what is still not written, and is not decided here:
    registering a COG in place ... Q-121 opens it.* Q-121 was answered by owner decision on
    2026-08-21, recorded in ADR-043 §3.3, and the behaviour was **built** -- the server tells
    an operator *imagery is registered in place, so the path is read at every request* while
    the ADR still called it undecided. Six days, in the document a reader goes to for the
    decision.

    <b>Why the existing checks did not catch it.</b>
    `amendments_the_other_adr_does_not_know_about` looks for ADRs amending each other, and
    this is an ADR pointing at the *question register*. `broken_links` only cares whether the
    file exists. Nothing read the sentence.

    <b>The sentence, not the link.</b> A reference to an answered question is ordinary and
    often right -- *Q-121 asked this* is history. What is wrong is an ADR **asserting the
    question is still open** when it is not, because that is a reader's cue to stop looking.
    So this matches the claim rather than the mention: *opens it*, *is open*, *remains open*,
    *still open*, *is unanswered*, *has no answer*.

    <b>Two rows that were not answered were flagged first, and the fix was to stop having a
    second opinion.</b> *Does the row contain the word ANSWERED anywhere* is not the
    register's rule -- Q-106 reads `Open, and narrowed`, and prose further along its row used
    one of the words. The rule is the one `an_answered_question_still_filed_as_open` applies:
    below the `## Answered` heading, or an anchored verdict opening the cell.

    <b>The gap between the question and the claim took three tries, and each failure was a
    different kind.</b> *No full stop between them*, to keep them in one sentence, could not
    match `[Q-121](../open-questions.md) opens it` at all -- the ordinary way an ADR names a
    question is a link, and a link is full of full stops. The stale sentence went back in and
    the check stayed green: the seventh time in this repository, and again only the
    falsification knew. *Anything between them* then matched ADR-016's *"closes most of
    Q-15's checklist. What remains open there is ..."*, which is a true sentence about a
    checklist rather than a claim about the question. What separates the two is a **sentence
    boundary** -- a full stop, a space and a capital -- so that is what the gap may not
    contain.

    <b>A struck sentence is history and is skipped.</b> Correcting one of these in place --
    striking the old text and writing what replaced it -- is the repair this repository
    prefers, and a check that then flagged the struck copy would punish the fix.
    """
    problems = []

    # <b>The register's own rule for *answered*, not a second one.</b> Written first as
    # "does the row contain the word ANSWERED anywhere", which flagged two rows that were not
    # answered at all -- Q-106 is `Open, and narrowed`, and prose further along its row used
    # one of the words. CLAUDE.md records what it cost the last time two tools each decided
    # for themselves what a status means: they disagreed, and the wrong number was the one on
    # the status page. So this is the same test `an_answered_question_still_filed_as_open`
    # applies -- below the `## Answered` heading, or an anchored verdict opening the cell.
    answered = set()

    lines = io.open(
        os.path.join(conditions.ROOT, "docs", "open-questions.md"),
        encoding="utf-8").read().splitlines()

    boundary = next(
        (i for i, line in enumerate(lines) if line.startswith("## Answered")), len(lines))

    verdict = re.compile(
        r"^~{0,2}\*{0,2}(ANSWERED|Answered|RESOLVED|Resolved|Re-answered|Re-ANSWERED"
        r"|DISSOLVED|Dissolved|WITHDRAWN|Withdrawn)\b")

    for index, line in enumerate(lines):
        cells = [c.strip() for c in COLUMN.split(line.strip().strip("|"))]

        if len(cells) < 2 or not re.match(r"^Q-\d+$", cells[0]):
            continue

        if index >= boundary or verdict.match(cells[1]):
            answered.add(cells[0])

    claim = re.compile(AN_ADR_CALLING_A_QUESTION_OPEN, re.IGNORECASE)

    for name in sorted(os.listdir(conditions.ADRS)):
        if not name.startswith("ADR-") or not name.endswith(".md"):
            continue

        text = io.open(os.path.join(conditions.ADRS, name), encoding="utf-8").read()

        for number, line in enumerate(text.split(chr(10)), start=1):
            # Struck text is the record of what was believed, not a live claim.
            if line.count("~~") >= 1:
                continue

            for hit in claim.finditer(line):
                question = hit.group(1)

                if question in answered:
                    problems.append(
                        name + ":" + str(number) + " says " + question + " " + hit.group(2)
                        + ", and open-questions.md records it as answered. An ADR is where a "
                        "reader goes for the decision, so a question called open there is a "
                        "cue to stop looking -- D-130. Strike the sentence and write what "
                        "replaced it, rather than deleting it.")

    return problems


# ---------------------------------------------------------------------------
# <b>Every pattern above must match the thing it was written for, and prove it here.</b>
#
# **Seven times on 2026-08-27 alone, a check in this file could not fail**, and five of the
# seven were the same bug: a regular expression that matched nothing. An anchored pattern used
# with `search`; `\b` before a name that follows two underscores; `[^.]` between a question
# and a claim, when the ordinary way to name a question is a link full of full stops; a
# Perl-mode `grep` with a quoted literal that returned nothing on this machine; a listing that
# omitted untracked files. Each time the check reported *clean* over a repository that had the
# defect in it, and each time only a deliberate falsification found out.
#
# **A pattern that matches nothing looks exactly like a repository with nothing wrong.** So
# each one carries an example of what it is for and one of what it must ignore, and this runs
# at import: a pattern that stops matching its own example fails the build immediately,
# wherever it is used, instead of quietly passing forever.
#
# **This is not a substitute for falsifying a check against the real registers.** It catches
# the pattern being dead, not the check being wrong about what it reads -- the sixth failure,
# where *answered* was decided by looking for a word rather than by the register's own rule,
# would have sailed through this. It removes the cheapest and commonest of the seven.
PATTERN_EXAMPLES = [
    (MACHINE_PATH,
     ['S="C:/Users/someone/AppData/Local/Temp/claude/p/abc/scratchpad"',
      'cd /home/someone/work/graticula',
      'OUT="/Users/someone/tmp"'],
     ['S=${GRATICULA_WORK:-$(mktemp -d)}',
      'cd "$(dirname -- "$0")/.."',
      'the path is /var/lib/graticula and belongs to the deployment']),

    (SECRET_NAMES,
     ['Graticula__SecretKey="bm90LWEta2V5LWp1c3QtYW4tZXhhbXBsZS0zMmJ5dGU="',
      "  Graticula__SecretKey: $GRATICULA_SECRET_KEY",
      'password = "hunter2"'],
     ["nothing here sets anything",
      "the secret key is discussed but never assigned"]),

    (GENERATED_KEY,
     # <b>Not the key that leaked.</b> D-191 took it out of this
     # repository, and putting it back as a test fixture would undo that: base64 of
     # `not-a-key-just-an-example-32byte`, which has the shape and none of the meaning.
     ["bm90LWEta2V5LWp1c3QtYW4tZXhhbXBsZS0zMmJ5dGU=",
      "0123456789abcdef0123456789abcdef"],
     ["gis", "changeme", "short", "$GRATICULA_SECRET_KEY"]),

    (BACKTICK_PATH,
     ["see `docs/architecture-debt.md` for the row",
      "the file is `compose.yaml`",
      "in `tools/registers-check.py`"],
     ["a plain `word` in backticks", "`GET /admin/health`"]),

    (AN_ADR_CALLING_A_QUESTION_OPEN,
     ["[Q-121](../open-questions.md) opens it.",
      "Q-77 is open and nothing has been decided",
      "the licence -- [Q-106](../open-questions.md) -- is still open"],
     ["Q-121 asked this, and ADR-043 3.3 answered it",
      "This closes most of Q-15's checklist. What remains open there is the grid data.",
      "Q-49's test with real GIS teams was dissolved"]),
]


def _prove_the_patterns_can_match():
    """Run at import. A pattern that no longer matches its own example is a dead check."""
    for pattern, matches, ignores in PATTERN_EXAMPLES:
        compiled = re.compile(pattern)

        for example in matches:
            if not compiled.search(example):
                raise SystemExit(
                    "tools/registers-check.py: the pattern " + pattern[:60] + "... no longer "
                    "matches an example it was written for:\n  " + example + "\n"
                    "A pattern that matches nothing looks exactly like a repository with "
                    "nothing wrong. Fix the pattern, or -- if the example is genuinely no "
                    "longer the shape being looked for -- change the example and say why in "
                    "the commit.")

        for example in ignores:
            if compiled.search(example):
                raise SystemExit(
                    "tools/registers-check.py: the pattern " + pattern[:60] + "... matches "
                    "something it must ignore:\n  " + example + "\n"
                    "A check somebody has to ignore is a check somebody turns off.")


_prove_the_patterns_can_match()


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
                + an_outbound_licence_claim_that_is_stale()
                + a_corpus_file_a_test_reads_but_a_clone_does_not_get()
                + a_real_data_test_without_the_trait_ci_filters_on()
                + a_test_project_ci_never_runs()
                + a_serving_assembly_that_reaches_for_the_network()
                + source_the_repository_would_not_receive()
                + a_question_the_status_page_cannot_see()
                + a_test_a_register_cites_that_is_not_there()
                + an_adr_that_does_not_say_where_its_state_lives()
                + a_port_documented_as_a_contract()
                + a_developers_own_path_in_a_committed_file()
                + a_secret_committed_to_a_public_repository()
                + a_file_a_security_promise_names_that_is_not_there()
                + an_adr_that_calls_an_answered_question_open())

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
