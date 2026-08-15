# §66 Security gate — run 2026-08-15

**Result: FAIL, and repaired in part.** Three findings, two fixed the same day,
one recorded as debt because the fix is a piece of infrastructure rather than a
control.

---

## Why now, and what question was asked

Four surfaces were added to this server in a single day: a session cookie, glyph
files served from disk, user-supplied style documents served to browsers, and
three new write endpoints. Nobody had looked at them together, and
[architecture-completeness.md](../architecture-completeness.md) said the gate was
waiting because *"security.md has grown three rules from implementation this
week. A gate would ask what is missing, which is a different question from what
has been added."*

So that is the question this asked: **what is missing.** Not whether the new
code is correct — it has tests — but what class of control the server does not
have at all.

**Method.** The reachable surface was enumerated from the running server's own
route table rather than from the source, every route was probed live rather than
read, and the refusals were checked for what they disclose. Where a control was
believed to exist, it was tested by trying to defeat it.

---

## What was tested, and held

Recorded because a gate that lists only failures reads as an audit of a broken
system, and because these are the things that would otherwise be re-tested next
time by somebody who could not tell they had been.

| Control | How it was tested | Result |
|---|---|---|
| Every `/admin` route requires a privilege | All 25 probed anonymously, then again with well-formed bodies — because nine returned `400` from model binding before the handler ran, which could have hidden a missing check | **Holds.** Every one answers `401` naming the privilege |
| The route report is honest | `/admin/routes` reads the real `EndpointDataSource`, not a maintained list. 63 routes, 0 ungoverned | **Holds** |
| A private layer is indistinguishable from absent | Unknown service, unknown layer, unknown data source | **Holds** — `404`, same shape either way |
| `where` is parsed, not passed through | `1=1; drop table layer`, `or pg_sleep(5)=1`, `'); select 1;--`, a subquery against `principal` | **Holds** — all four refused by the parser, none reached the database |
| Refusals disclose nothing internal | 11 error paths scanned for driver names, stack frames, paths, SQL, connection details | **Holds** — zero tells |
| The login throttle cannot be used as a weapon | 20 wrong passwords, then the right one | **Holds.** Address budget (50) exceeds the account budget (10) by design, so a correct password still succeeds while an account is throttled. The 429s came from the account limit *after* verifying — I misread this as a bypass until reading `LoginThrottle` |
| A password change revokes other sessions | `RevokeOtherSessionsAsync`, reported in the response and the audit record | **Holds** |
| The browsing cookie authenticates reads only | Existing conformance test, re-verified | **Holds** |
| Glyph and style paths cannot escape | Traversal in the font stack, the range and the sprite name | **Holds** — matched against what exists, never concatenated |
| Dependencies have no known vulnerabilities | `dotnet list package --vulnerable --include-transitive` | **Holds today.** See F3 for tomorrow |

---

## F1 — Not one security header on any response, except the one surface somebody
thought about

**Severity: moderate. Fixed.**

Attachment downloads carried `Content-Disposition: attachment`,
`X-Content-Type-Options: nosniff` and a sandboxing `Content-Security-Policy` —
carefully, by hand, with a comment explaining that none of the three is optional.
**Every other response carried none of them.** Not the REST directory, not the
JSON documents, not the tiles, not the 404 that routing produces.

That shape is the finding. Protecting surfaces one at a time gives you a safe
surface and a bare default, and the bare default is where the next surface lands.

**Why it matters here specifically.** The REST directory renders user-supplied
layer names, folder names and field aliases into HTML, and the person reading
that page is an administrator holding every privilege the server has. The
encoding is correct and tested; a policy is what stands between an encoding
mistake and a stolen session.

**And `Referrer-Policy` closes a hole [security.md](../security.md) already
names.** ArcGIS compatibility puts a token in the query string. That document
lists where it leaks — logs, proxies, browser history, and `Referer` headers sent
to third parties — then gives four mitigations: log redaction, preferring the
header form, short lifetimes, revocability. **None of them addresses `Referer`.**
One header does.

**Fixed** by `SecurityHeaders`, applied before everything else so that responses
no handler wrote — the 404, the 405, the exception handler's 500 — carry them
too. `nosniff`, `no-referrer`, `X-Frame-Options: DENY`, HSTS when HTTPS is
required, and a CSP derived from what the pages actually contain: `default-src
'none'` with inline styles allowed, script forbidden outright, and `form-action
'self'` for the sign-in and query forms. Nothing already set is overwritten, so
the attachment path keeps its stricter policy.

---

## F2 — `/admin/health` counted content the server refuses to confirm exists

**Severity: moderate. Fixed.**

An anonymous caller saw **two services** in the catalogue and was told, by the
same server, that it holds **26 layers**, 387 cached tiles and 25 described
shapes — plus the assembly version.

Everywhere else, enormous care is taken so that a private layer is
indistinguishable from an absent one: `404` rather than `403`, the same answer
for *not shared with you* and *does not exist*, and a geometry service that stops
answering strangers about itself. Then this endpoint published the count.

**[D-18](../architecture-debt.md) knew the endpoint was anonymous and called it
redacted** — but the redaction was written for the store's host and port, which
had leaked through an error message. Nobody asked whether the *numbers* were a
disclosure. They are: an inventory is information about content, and this server's
whole disclosure posture is that content you may not see is content you may not
learn about.

**Fixed.** An anonymous caller now gets `status`, `platformStore.reachable` and a
sentence saying why there is nothing else. An administrator gets everything,
unchanged — asserted by its own test, because a redaction that blinds the
operator the endpoint exists for is not a fix.

The endpoint stays anonymous, and must: sessions live in the platform store, so
during the outage it exists for, nobody can authenticate.

---

## F3 — Nothing runs the checks, and a promise now depends on them

**Severity: moderate. Not fixed — recorded as [D-29](../architecture-debt.md).**

There is no CI. No `.github/workflows`, no pipeline of any kind. Every test in
this repository runs because somebody remembered to run it.

That was tolerable while the repository was private and unreleased. It stopped
being tolerable on the same day, because
[ADR-025](../adr/ADR-025-governance-and-maintenance.md) committed in writing to
acknowledging a vulnerability report in 7 days, assessing in 30 and fixing in 90.
**A project that promises to fix known vulnerabilities and has no automated check
for known-vulnerable dependencies is relying on somebody's memory to keep a
published commitment.**

`dotnet list package --vulnerable --include-transitive` is clean today. Nothing
would report it going dirty.

**Not fixed here, deliberately.** The fix is a piece of infrastructure, not a
control, and it runs into a real tension worth stating rather than solving in
passing: **this project's suites fail rather than skip when their subject is
absent** — a deliberate rule, adopted after three instruments were caught lying.
So CI cannot simply run `dotnet test`; it needs a PostGIS service container and a
running server, or it needs to run a subset and be honest that it is a subset.
That is a decision about how the project is built, and it belongs to the owner
rather than to a security review.

---

## What this gate did not cover

Named, so the absence is a known gap rather than an implied pass.

- **No adversarial testing by anybody who did not write the code.** This is a
  self-review, and §67's standing objection applies to it exactly as it applies
  to the earlier rounds. A gate run by the author finds the things the author
  can think to look for.
- **No dependency review beyond the vulnerability database.** Npgsql and Argon2
  were read for licence, not for behaviour.
- **No testing of the overlay worker as a boundary** — it takes attacker-supplied
  geometry in a separate process, and only its resource limits have been
  exercised, not its input parsing.
- **No review of the shapefile and GeoJSON readers as parsers.** They have
  bounds and tests; nobody has tried to break them with malformed input designed
  to.
- **No TLS configuration review.** Cipher suites, protocol versions and
  certificate handling were not examined.
- **Nothing was tested against a deployment.** There is not one.

---

## Disposition

| Finding | Action |
|---|---|
| F1 headers | Fixed. `SecurityHeaders`, 10 conformance tests including the routing 404 |
| F2 health inventory | Fixed. Redacted for anonymous, whole for operators, both asserted |
| F3 no CI | [D-29](../architecture-debt.md) opened. The gate's largest open recommendation |

**908 tests pass** after the repairs.

**The gate is recorded as FAIL** rather than as pass-with-notes, because two of
the three findings were live disclosures on a running server and one of them
contradicted a rule the project applies everywhere else. A gate that found real
defects and reported a pass would be the false assurance §66 exists to prevent.
