#!/usr/bin/env python3
"""Builds the project status page from the repository itself.

Why this is a generator and not a hand-written page
---------------------------------------------------
The first control room was written by hand and was stale within a day, twice.
A status page whose facts are copied is a status page that lies as soon as
anything moves, and it lies most convincingly about the things that changed
most recently — which is exactly what somebody opens it to see.

Everything here is read out of the documents that are the source of truth:
the ADR front-matter tables, open-questions.md, architecture-debt.md,
architecture-assumptions.md and git. Nothing is typed in. If a number here is
wrong, the document it came from is wrong, and that is the right place for it
to be wrong.

Usage:  python tools/status-page.py [output.html]
"""

import html
import io
import os
import re
import subprocess
import sys
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read(*parts):
    path = os.path.join(ROOT, *parts)
    if not os.path.exists(path):
        return ""
    return io.open(path, encoding="utf-8").read()


def git(*args):
    try:
        return subprocess.run(
            ["git", "-C", ROOT, *args], capture_output=True, text=True, check=True
        ).stdout
    except Exception:
        return ""


# ---------------------------------------------------------------- ADRs

def adrs():
    """Every ADR with its status and confidence, read from its own header."""
    out = []
    folder = os.path.join(ROOT, "docs", "adr")

    for name in sorted(os.listdir(folder)):
        if not name.startswith("ADR-") or not name.endswith(".md"):
            continue

        text = io.open(os.path.join(folder, name), encoding="utf-8").read()
        head = text[:1500]

        title = (re.search(r"^#\s*(.+)$", text, re.M) or [None, name])[1]
        status = field(head, "Status") or "UNKNOWN"
        confidence = field(head, "Confidence") or "—"

        # Conditions are numbered list items under a Conditions heading. They
        # are the project's largest open commitment and nothing counted them.
        conditions = 0
        section = re.search(r"^##\s*\d*\.?\s*Conditions\b(.*?)(^##\s|\Z)", text, re.M | re.S)
        if section:
            conditions = len(re.findall(r"^\d+\.\s", section.group(1), re.M))

        out.append({
            "id": name.split("-")[1],
            "file": name,
            "title": re.sub(r"^ADR-\d+\s*[—-]\s*", "", title).strip(),
            "status": status,
            "confidence": confidence,
            "conditions": conditions,
        })

    return out


def field(head, label):
    match = re.search(r"\|\s*\*\*" + label + r"\*\*\s*\|\s*(.+?)\s*\|", head)
    if not match:
        return None
    return match.group(1).replace("`", "").strip()


# ---------------------------------------------------------------- tables

def rows(markdown, pattern):
    """Pipe-table rows whose first cell matches, as lists of cells."""
    out = []
    for line in markdown.split("\n"):
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if cells and re.match(pattern, cells[0]):
            out.append(cells)
    return out


def first_sentence(text, limit=260):
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)
    text = re.sub(r"[*_`>]", "", text).strip()
    cut = re.split(r"(?<=[.!?])\s", text)
    lead = cut[0] if cut else text
    if len(lead) > limit:
        lead = lead[:limit].rsplit(" ", 1)[0] + "…"
    return lead


def questions():
    """Questions, with resolution taken from which section they sit in.

    <b>Not from the text.</b> The first version of this looked for a bold
    "Resolved" at the start of the cell and reported 93 of 95 questions open,
    which is exactly the kind of confidently wrong number a status page exists
    to avoid. The register groups questions under headings — "Blocking Phase 0",
    "Open, not blocking", "Answered" — and the heading is the fact.
    """
    out = []
    section = ""

    for line in read("docs", "open-questions.md").splitlines():
        heading = re.match(r"^##\s+(.+?)\s*$", line)
        if heading:
            section = heading.group(1)
            continue

        if not line.startswith("|"):
            continue

        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if not cells or not re.match(r"^Q-\d+$", cells[0]):
            continue

        body = cells[1] if len(cells) > 1 else ""
        out.append({
            "id": cells[0],
            "text": first_sentence(body),
            "owner": cells[2] if len(cells) > 2 else "",
            "section": section,
            "resolved": section.lower().startswith("answered"),
            "blocking": "blocking" in section.lower() and "not blocking" not in section.lower(),
            "withdrawn": body.startswith("~~"),
        })

    return out


def debts():
    out = []
    for cells in rows(read("docs", "architecture-debt.md"), r"^D-\d+$"):
        status = cells[-1] if len(cells) >= 7 else ""
        out.append({
            "id": cells[0],
            "text": first_sentence(cells[1] if len(cells) > 1 else ""),
            "trigger": first_sentence(cells[4], 120) if len(cells) > 4 else "",
            "status": status,
            "open": not re.match(r"\s*\*{0,2}(RESOLVED|CLOSED)", status, re.I),
            "partly": bool(re.match(r"\s*\*{0,2}(PARTLY|PARTIALLY)", status, re.I)),
        })
    return out


def assumptions():
    out = []
    for cells in rows(read("docs", "architecture-assumptions.md"), r"^A-\d+$"):
        state = re.sub(r"[*`]", "", cells[2] if len(cells) > 2 else "").strip()
        out.append({
            "id": cells[0],
            "text": first_sentence(cells[1] if len(cells) > 1 else "", 180),
            "state": state.split()[0] if state else "UNKNOWN",
        })
    return out


# ---------------------------------------------------------------- code

def tests():
    """Test cases per project.

    <b>Cases, not methods.</b> Counting attributes gave 365 against a suite that
    reports 402, because one [Theory] with five [InlineData] rows is five tests.
    A status page that undercounts its own verification by ten percent is
    reporting a number nobody can reconcile with the console.
    """
    out = []
    folder = os.path.join(ROOT, "tests")
    if not os.path.isdir(folder):
        return out

    for project in sorted(os.listdir(folder)):
        path = os.path.join(folder, project)
        if not os.path.isdir(path):
            continue

        count = 0
        for base, dirs, files in os.walk(path):
            dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
            for name in files:
                if not name.endswith(".cs"):
                    continue

                body = io.open(os.path.join(base, name), encoding="utf-8", errors="replace").read()
                count += len(re.findall(r"^\s*\[Fact\b", body, re.M))

                # A Theory contributes one test per InlineData row. A Theory fed
                # by MemberData contributes an unknown number, so it counts as
                # one and this total is a floor rather than an estimate.
                for block in re.findall(
                        r"^[ \t]*\[Theory\b.*?(?=^[ \t]*public |\Z)", body, re.M | re.S):
                    inline = len(re.findall(r"^\s*\[InlineData\b", block, re.M))
                    count += inline if inline else 1

        if count:
            out.append((project.replace("GisServer.", "").replace(".Tests", ""), count))

    return sorted(out, key=lambda p: -p[1])


def source_size():
    lines = 0
    files = 0
    for base, dirs, names in os.walk(os.path.join(ROOT, "src")):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in names:
            if name.endswith(".cs"):
                files += 1
                lines += sum(1 for _ in io.open(os.path.join(base, name), encoding="utf-8", errors="replace"))
    return files, lines


def commits(limit=14):
    log = git("log", f"-{limit}", "--pretty=%h%x1f%ad%x1f%s", "--date=short")
    out = []
    for line in log.strip().split("\n"):
        if line.count("\x1f") == 2:
            sha, date, subject = line.split("\x1f")
            out.append({"sha": sha, "date": date, "subject": subject})
    return out


# ---------------------------------------------------------------- render

def esc(text):
    return html.escape(str(text))


def status_class(status):
    s = status.upper()
    if "REJECTED" in s:
        return "bad"
    if "REOPENED" in s or "REQUIRES" in s or "DRAFT" in s:
        return "warn"
    if "CONDITIONS" in s:
        return "cond"
    # Superseded and deferred are neither achievements nor problems, and
    # colouring them green made three retired decisions read as live ones.
    if "DEFERRED" in s or "SUPERSEDED" in s or "WITHDRAWN" in s:
        return "muted"
    return "good"


def build():
    a = adrs()
    q = questions()
    d = debts()
    s = assumptions()
    t = tests()
    files, lines = source_size()

    q_open = [x for x in q if not x["resolved"] and not x["withdrawn"]]
    q_blocking = [x for x in q_open if x["blocking"]]
    d_open = [x for x in d if x["open"]]
    conditions = sum(x["conditions"] for x in a)
    validated = sum(1 for x in s if x["state"].startswith("VALIDATED"))
    total_tests = sum(c for _, c in t)
    generated = git("log", "-1", "--pretty=%ad", "--date=format:%Y-%m-%d %H:%M").strip() or "unknown"

    by_status = Counter(x["status"] for x in a)

    def kpi(value, label, note="", tone=""):
        return (f'<div class="kpi {tone}"><div class="kpi-v">{esc(value)}</div>'
                f'<div class="kpi-l">{esc(label)}</div>'
                f'<div class="kpi-n">{esc(note)}</div></div>')

    parts = []
    parts.append(HEAD)

    parts.append('<header class="top">')
    parts.append('<div class="eyebrow">gis-server · Phase 1 — Implementation</div>')
    parts.append('<h1>Control Room</h1>')
    parts.append('<p class="lede">Everything below is read out of the repository at build time — '
                 'the ADR headers, <code>open-questions.md</code>, <code>architecture-debt.md</code>, '
                 '<code>architecture-assumptions.md</code> and git. Nothing on this page is typed by hand, '
                 'because the two versions that were went stale within a day.</p>')
    parts.append(f'<p class="stamp">Generated from commit <code>{esc(commits(1)[0]["sha"] if commits(1) else "?")}</code> · {esc(generated)}</p>')
    parts.append('</header>')

    # ---- the numbers
    parts.append('<section><h2>Where it stands</h2><div class="kpis">')
    plain = by_status.get("ACCEPTED", 0)
    parts.append(kpi(
        len(a), "ADRs",
        # Worth stating rather than showing a zero: not one decision in this
        # project is accepted without conditions attached to it.
        "none accepted unconditionally" if plain == 0 else f"{plain} accepted outright",
        "warn" if plain == 0 else ""))
    parts.append(kpi(conditions, "ADR conditions",
                     "the largest open commitment", "warn" if conditions > 10 else ""))
    parts.append(kpi(len(q_open), "open questions",
                     f"{sum(1 for x in q if x['resolved'])} answered"
                     + (f" · {len(q_blocking)} blocking" if q_blocking else "")))
    partly = sum(1 for x in d if x["partly"])
    parts.append(kpi(len(d_open), "open debts",
                     f"{len(d) - len(d_open)} repaid" + (f" · {partly} partly" if partly else ""),
                     "warn" if len(d_open) > 10 else ""))
    parts.append(kpi(f"{validated}/{len(s)}", "assumptions validated", "the rest are unproven", "warn"))
    parts.append(kpi(total_tests, "tests", f"{files} source files, {lines:,} lines", "good"))
    parts.append('</div></section>')

    # ---- ADRs
    parts.append('<section><h2>Decisions</h2>')
    parts.append('<p class="sub">Every architectural decision is an ADR. Status and confidence are read from '
                 'each file\'s own header, so a decision cannot quietly change grade here without changing there.</p>')
    parts.append('<div class="scroll"><table><thead><tr>'
                 '<th>ADR</th><th>Decision</th><th>Status</th><th>Confidence</th><th class="num">Conditions</th>'
                 '</tr></thead><tbody>')
    for x in a:
        parts.append(
            f'<tr><td class="id">{esc(x["id"])}</td><td>{esc(x["title"])}</td>'
            f'<td><span class="pill {status_class(x["status"])}">{esc(x["status"])}</span></td>'
            f'<td class="muted-t">{esc(x["confidence"])}</td>'
            f'<td class="num">{x["conditions"] or ""}</td></tr>')
    parts.append('</tbody></table></div></section>')

    # ---- questions
    parts.append('<section><h2>Open questions</h2>')
    parts.append('<p class="sub">Uncertainty is recorded rather than hidden. An empty list here would mean '
                 'the recording stopped, not that the uncertainty did.</p>')
    parts.append('<div class="cards">')
    for x in sorted(q_open, key=lambda y: (not y["blocking"], y["id"])):
        mark = ' blocking' if x["blocking"] else ''
        tag = '<span class="owner blocking-tag">BLOCKING</span>' if x["blocking"]               else f'<span class="owner">{esc(x["owner"])}</span>'
        parts.append(f'<article class="card{mark}"><div class="card-h">'
                     f'<span class="id">{esc(x["id"])}</span>{tag}</div>'
                     f'<p>{esc(x["text"])}</p></article>')
    parts.append('</div></section>')

    # ---- debt
    parts.append('<section><h2>Architecture debt</h2>')
    parts.append('<p class="sub">Temporary compromises, each with the trigger that repays it. '
                 'A compromise with no trigger is a permanent decision wearing a temporary label.</p>')
    parts.append('<div class="scroll"><table><thead><tr>'
                 '<th>ID</th><th>Debt</th><th>Repay when</th><th>State</th></tr></thead><tbody>')
    for x in d:
        tone = "cond" if x["partly"] else "warn" if x["open"] else "good"
        label = "PARTLY" if x["partly"] else "OPEN" if x["open"] else "RESOLVED"
        parts.append(f'<tr class="{"" if x["open"] else "done"}"><td class="id">{esc(x["id"])}</td>'
                     f'<td>{esc(x["text"])}</td><td class="muted-t">{esc(x["trigger"])}</td>'
                     f'<td><span class="pill {tone}">{label}</span></td></tr>')
    parts.append('</tbody></table></div></section>')

    # ---- assumptions
    parts.append('<section><h2>Assumptions</h2>')
    parts.append('<p class="sub">Invalidating one triggers a review of every ADR that rests on it. '
                 'The unvalidated majority is the honest state of the project, not an oversight.</p>')
    parts.append('<div class="scroll"><table><thead><tr>'
                 '<th>ID</th><th>Assumption</th><th>State</th></tr></thead><tbody>')
    for x in s:
        tone = "good" if x["state"].startswith("VALIDATED") else \
               "bad" if x["state"].startswith("INVALID") else "warn"
        parts.append(f'<tr><td class="id">{esc(x["id"])}</td><td>{esc(x["text"])}</td>'
                     f'<td><span class="pill {tone}">{esc(x["state"])}</span></td></tr>')
    parts.append('</tbody></table></div></section>')

    # ---- tests
    parts.append('<section><h2>What is verified</h2>')
    parts.append('<p class="sub">Counted from <code>[Fact]</code> and <code>[Theory]</code> attributes in the '
                 'test sources, so this number cannot drift from the suite that produces it.</p>')
    parts.append('<div class="bars">')
    top = max((c for _, c in t), default=1)
    for name, count in t:
        parts.append(f'<div class="bar"><div class="bar-l">{esc(name)}</div>'
                     f'<div class="bar-t"><div class="bar-f" style="width:{100*count/top:.1f}%"></div></div>'
                     f'<div class="bar-n">{count}</div></div>')
    parts.append('</div></section>')

    # ---- progress
    parts.append('<section><h2>Recent work</h2><ol class="log">')
    for c in commits():
        parts.append(f'<li><code>{esc(c["sha"])}</code><time>{esc(c["date"])}</time>'
                     f'<span>{esc(c["subject"])}</span></li>')
    parts.append('</ol></section>')

    parts.append('<footer><p>gis-server · Apache-2.0 · generated by <code>tools/status-page.py</code></p></footer>')
    parts.append('</main>')

    return "\n".join(parts)


HEAD = """<title>Control Room</title>
<style>
:root{
  --ink:#141b1f; --ink-2:#41525c; --ink-3:#74878f;
  --ground:#f6f4ef; --panel:#ffffff; --line:#e0dcd2;
  --accent:#0f6d5f; --accent-soft:#e2efeb;
  --good:#1f6f4a; --good-bg:#e3f1e8;
  --warn:#8a5a10; --warn-bg:#f8eeda;
  --bad:#9a3126;  --bad-bg:#f8e4e1;
  --cond:#4a4694; --cond-bg:#e8e7f6;
}
@media (prefers-color-scheme: dark){:root:not([data-theme="light"]){
  --ink:#e9e6df; --ink-2:#a8b6bc; --ink-3:#76888f;
  --ground:#10151a; --panel:#171e24; --line:#2a343c;
  --accent:#57bda9; --accent-soft:#17322f;
  --good:#7fcfa4; --good-bg:#182c23;
  --warn:#e0b36a; --warn-bg:#322714;
  --bad:#e79187;  --bad-bg:#33201e;
  --cond:#a5a1e8; --cond-bg:#232145;
}}
:root[data-theme="dark"]{
  --ink:#e9e6df; --ink-2:#a8b6bc; --ink-3:#76888f;
  --ground:#10151a; --panel:#171e24; --line:#2a343c;
  --accent:#57bda9; --accent-soft:#17322f;
  --good:#7fcfa4; --good-bg:#182c23;
  --warn:#e0b36a; --warn-bg:#322714;
  --bad:#e79187;  --bad-bg:#33201e;
  --cond:#a5a1e8; --cond-bg:#232145;
}
*{box-sizing:border-box}
body{
  margin:0; background:var(--ground); color:var(--ink);
  font:16px/1.6 ui-sans-serif,-apple-system,"Segoe UI",system-ui,sans-serif;
  -webkit-font-smoothing:antialiased;
}
main{max-width:1080px;margin:0 auto;padding:0 24px 96px}
code{font-family:ui-monospace,"Cascadia Code",Menlo,Consolas,monospace;font-size:.88em}

.top{padding:64px 0 40px;border-bottom:1px solid var(--line);margin-bottom:8px}
.eyebrow{font-size:12px;letter-spacing:.13em;text-transform:uppercase;color:var(--accent);font-weight:650}
h1{font-size:clamp(34px,5.5vw,54px);line-height:1.04;margin:.28em 0 .32em;letter-spacing:-.025em;text-wrap:balance}
.lede{max-width:64ch;color:var(--ink-2);font-size:17px;margin:0 0 14px}
.stamp{color:var(--ink-3);font-size:13px;margin:0}

section{padding:44px 0;border-bottom:1px solid var(--line)}
section:last-of-type{border-bottom:0}
h2{font-size:13px;letter-spacing:.12em;text-transform:uppercase;color:var(--ink-3);margin:0 0 6px;font-weight:650}
.sub{max-width:66ch;color:var(--ink-2);margin:0 0 22px;font-size:15px}

.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(155px,1fr));gap:12px;margin-top:20px}
.kpi{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:18px}
.kpi-v{font-size:32px;font-weight:640;letter-spacing:-.03em;font-variant-numeric:tabular-nums;line-height:1}
.kpi-l{font-size:13px;color:var(--ink-2);margin-top:8px}
.kpi-n{font-size:12px;color:var(--ink-3);margin-top:3px}
.kpi.warn .kpi-v{color:var(--warn)} .kpi.good .kpi-v{color:var(--good)}

.scroll{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--panel)}
table{width:100%;border-collapse:collapse;font-size:14.5px;min-width:640px}
th{text-align:left;font-size:11.5px;letter-spacing:.09em;text-transform:uppercase;color:var(--ink-3);
   padding:12px 14px;border-bottom:1px solid var(--line);font-weight:650;white-space:nowrap}
td{padding:12px 14px;border-bottom:1px solid var(--line);vertical-align:top}
tr:last-child td{border-bottom:0}
td.id{font-family:ui-monospace,Menlo,monospace;color:var(--ink-3);white-space:nowrap;font-size:13px}
td.num{text-align:right;font-variant-numeric:tabular-nums;color:var(--ink-2)}
.muted-t{color:var(--ink-3);font-size:13.5px}
tr.done td{opacity:.55}

.pill{display:inline-block;padding:2px 9px;border-radius:999px;font-size:11.5px;font-weight:600;white-space:nowrap}
.pill.good{background:var(--good-bg);color:var(--good)}
.pill.warn{background:var(--warn-bg);color:var(--warn)}
.pill.bad{background:var(--bad-bg);color:var(--bad)}
.pill.cond{background:var(--cond-bg);color:var(--cond)}
.pill.muted{background:var(--line);color:var(--ink-3)}

.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:12px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:16px;
      border-left:3px solid var(--accent)}
.card-h{display:flex;justify-content:space-between;align-items:baseline;gap:10px;margin-bottom:7px}
.card .id{font-family:ui-monospace,Menlo,monospace;font-size:12.5px;color:var(--accent);font-weight:650}
.card .owner{font-size:11.5px;color:var(--ink-3);text-transform:uppercase;letter-spacing:.07em}
.card p{margin:0;font-size:14px;color:var(--ink-2);line-height:1.5}
.card.blocking{border-left-color:var(--bad)}
.card.blocking .id{color:var(--bad)}
.blocking-tag{color:var(--bad)!important;font-weight:700}

.bars{display:flex;flex-direction:column;gap:9px}
.bar{display:grid;grid-template-columns:190px 1fr 48px;align-items:center;gap:14px}
.bar-l{font-size:13.5px;color:var(--ink-2);text-align:right;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.bar-t{height:9px;background:var(--line);border-radius:999px;overflow:hidden}
.bar-f{height:100%;background:var(--accent);border-radius:999px}
.bar-n{font-variant-numeric:tabular-nums;font-size:13.5px;color:var(--ink-3)}

.log{list-style:none;margin:0;padding:0;display:flex;flex-direction:column}
.log li{display:grid;grid-template-columns:78px 100px 1fr;gap:12px;align-items:baseline;
        padding:9px 0;border-bottom:1px solid var(--line);font-size:14px}
.log li:last-child{border-bottom:0}
.log code{color:var(--accent)}
.log time{color:var(--ink-3);font-size:12.5px;font-variant-numeric:tabular-nums}
.log span{color:var(--ink-2)}

footer{padding:36px 0;color:var(--ink-3);font-size:13px}
@media(max-width:640px){
  .bar{grid-template-columns:110px 1fr 40px}
  .log li{grid-template-columns:1fr;gap:2px}
}
</style>
<main>
"""


if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "docs", "status.html")
    io.open(target, "w", encoding="utf-8", newline="\n").write(build())
    print(f"wrote {target}")
