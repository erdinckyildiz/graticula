# ADR-047 — The outbound licence is Elastic License 2.0

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` for the decision · `MEDIUM` for what it will cost in adoption |
| **Decided** | 2026-08-25 |
| **Supersedes** | The Apache-2.0 choice recorded in [phase-0-exit-plan.md](../phase-0-exit-plan.md) B0, 2026-08-13 |
| **Superseded by** | — |

---

## 1. Context

**The repository is about to become public**, and the reason is
[Q-135](../open-questions.md): GitHub Actions has never run a single step here — nine
0-second `startup_failure`s — and a public repository removes the spending limit that is
the leading explanation. [D-63](../architecture-debt.md) has four claims resting on CI
that are unproven because of it.

Going public forces the licence question to be answered properly rather than left at the
one taken in five minutes on 2026-08-13, when the blocking problem was that *there was no
licence at all* and any licence was better than none.

**The owner's requirement, stated 2026-08-25:** *"bize herkesin açıkça kullanabileceği,
ama kodu ticari kullanamayacağı bir lisans lazım"*, narrowed in the next sentence to
*"yani parayla alıp satamasın"* — everybody may use it openly; nobody may take it and
sell it.

**This reverses a recorded decision** ([CLAUDE.md](../../CLAUDE.md) §7: *open source,
copyleft acceptable, no commercial closed-source distribution constraint*), so it is an
ADR and it is recorded as the owner's decision rather than inferred from it — §2's rule.

## 2. Alternatives considered

### Alternative A — Stay Apache-2.0

**Argument for.** It is genuinely open source, it is what the repository already
declares, and it is the licence every claim in this codebase currently reasons from —
[ADR-019](ADR-019-portal-server-split.md) §37, [ADR-020](ADR-020-admin-console-and-service-status.md),
[ADR-027](ADR-027-glyphs-and-sprites.md) and the Oracle row in
[DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) all lean on *we may be
redistributed freely*. It costs nothing to keep and it is the maximum-adoption answer.

**Argument against.** It permits exactly what the owner asked to prevent: anyone may take
Graticula, close it, host it and sell it, and owe nothing back. For a server whose whole
value is being run for other people, that is not a hypothetical.

### Alternative B — AGPL-3.0

**Argument for.** Real open source, already listed as acceptable in CLAUDE.md §7, and the
standard answer to the hosting problem: a competitor may run it as a service, but must
publish their modifications, which removes most of the incentive to fork it into a closed
product. Selling copies stays legal and stays pointless, because the buyer may
redistribute for free.

**Argument against.** It answers the hosting problem by *obliging publication* rather than
by refusing the use, and many companies' legal policies reject AGPL outright — including
as an internal dependency. So it can cost the ordinary user, who was never the problem,
in order to inconvenience the reseller, who is. It is also a poor fit for the owner's
sentence: they asked for something not to be allowed, not for it to be allowed with
conditions.

### Alternative C — A non-commercial licence (PolyForm Noncommercial, CC BY-NC)

**Argument for.** It is the literal reading of *ticari kullanamayacağı*.

**Argument against.** *Commercial* has no agreed boundary, and the ambiguity lands on the
users this project is for: a municipality with paid staff, a university on a funded
contract, a consultant deploying for a client. Every one of them has to ask a lawyer
before running a GIS server. It would also forbid a company from running Graticula
internally, which the owner's second sentence shows was never the intent. Creative
Commons themselves advise against NC licences for software.

### Alternative D — Business Source License 1.1

**Argument for.** Restricts production use for a chosen period and converts to an open
licence afterwards, so the restriction is time-boxed rather than permanent.

**Argument against.** The change date is a promise about a future this project has not
planned, and BSL's *additional use grant* has to be drafted per project. More machinery
than the question needs.

## 3. Counterarguments to the preferred option

**The strongest one: this is not open source, and the README said *given away*.** ELv2
fails the OSI definition on field-of-endeavour discrimination. The practical costs are
real and should be listed rather than waved at: no Linux distribution will package it,
some corporate policies auto-reject source-available licences, and *open source* can no
longer be written anywhere in this repository without being false. The project's own
prose has to change with the licence, and that is the largest part of the work this
decision creates.

**The second: it does not do what the owner's first sentence asked.** ELv2 does not
forbid commercial use, and it does not forbid selling copies. Somebody may charge money
for Graticula, for support, for an installation, or for a product that embeds it. What
they may not do is offer it to third parties **as a hosted or managed service**. That is
narrower than *parayla alıp satamasın* read literally — and it is the part that matters
for a server, because hosting is how a GIS server is monetised by somebody who did not
write it.

**The third: the licence-key clause is inert here.** ELv2 forbids circumventing licence
key functionality; Graticula has none and [ADR-019](ADR-019-portal-server-split.md) §37
records that there is nothing to meter. The clause costs nothing and is left standing
rather than edited, because an edited licence is a new licence nobody has reviewed.

**The fourth: relicensing has a one-way component.** Apache-2.0 was declared on
2026-08-13 and is irrevocable for anything already **published** under it — and
*published* is the word that matters, because a licence grants rights to whoever
receives a copy and nobody has received one.

**Corrected 2026-08-25, an hour after this ADR was written.** The first version of this
paragraph said *no commit has been pushed*, and that is false: this history has been
pushed since 2026-08-12 to the repository still carrying the former working title
(`erdinckyildiz/gis-server` — ADR-032 renamed the product, not the remote), most recently on 2026-08-24. The
claim that holds is the narrower one — **that repository is private, with 0 forks and 0
stars**, and no release exists ([D-19](../architecture-debt.md)). So no third party has
ever obtained a copy under Apache-2.0, and there is nobody holding rights this change
would have to respect.

**The window closes the moment the repository is public**, not at the first push, and
the correction matters because those are days apart rather than the same instant.

## 4. Evidence

| Claim | Evidence |
|---|---|
| No copyleft dependency blocks a restrictive outbound licence | Every referenced package is permissive: BitMiracle.LibTiff.NET BSD-3, Konscious.Argon2 MIT, MaxRev.Gdal.Core MIT, NetTopologySuite BSD-3/Apache-2.0, Npgsql PostgreSQL Licence, SkiaSharp MIT — `Directory.Packages.props` read 2026-08-25 |
| GDAL's native payload carries no copyleft either | [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) enumerates all fourteen `LICENSE.TXT` components against the drivers actually built — *"all of them permissive, none of them copyleft"*, measured from the running build rather than assumed |
| GEOS (LGPL) and PostGIS (GPL-2.0-or-later) do not constrain us | Both are a separate process, not linked. `NativeDependencyTests` holds the confinement in both directions |
| Nothing has been **published** under Apache-2.0 | `gh repo view` on the remote still named for the former working title 2026-08-25: `isPrivate: true`, `forkCount: 0`, `stargazerCount: 0`, created 2026-08-12. Commits *have* been pushed there since 2026-08-12 — the first draft of this row said otherwise and was wrong — but a push to a private repository distributes to nobody, and there is no release ([D-19](../architecture-debt.md)) |
| The licence text is the canonical one | Taken byte-for-byte from `elastic/elasticsearch`'s `licenses/ELASTIC-LICENSE-2.0.txt`, not retyped |

## 5. Decision

**The outbound licence is Elastic License 2.0.** `LICENSE` carries the canonical text
unmodified. `NOTICE` states the restriction in plain words beside the licence name,
because a reader who has to infer it from a licence name has been told nothing.

- **What anyone may do:** read, run, modify, distribute, embed, and charge money for any
  of that.
- **The one thing nobody may do:** provide Graticula to third parties as a hosted or
  managed service giving them a substantial set of its features.
- **Third-party obligations are unchanged.** Attribution and bill-of-material duties
  travel with the artefact regardless of what we license our own code as, and
  `DEPENDENCY-LICENSES.md` stays the authoritative list.

**`open source` stops being a true description of this project** and is removed wherever
it appears. The accurate phrase is **source-available**.

## 6. Consequences

**Positive.**

- The thing the owner asked to prevent is prevented, in the one form that matters for a
  server.
- The decision is now written down with its alternatives, which the 2026-08-13 choice
  never was — it was a five-minute repair to a blocking problem, and it has been quoted
  as a considered position ever since.
- The dependency question is answered by measurement rather than by hope, and the answer
  is recorded where the next licence question will look.

**Negative.**

- **Four documents now make false present-tense claims** and every one of them reasons
  *from* the old licence rather than merely mentioning it: ADR-019 §37, ADR-020 §189,
  ADR-027 §57 and DEPENDENCY-LICENSES.md's Oracle row. This is
  [D-130](../architecture-debt.md)'s propagation failure with a new cause, and it is
  repaired in the same change rather than left for a sweep to find.
- Adoption is narrower than Apache-2.0's, unmeasurably so. Nobody should pretend to know
  by how much.
- A contributor licence question arrives with the first outside pull request: ELv2 has no
  contribution mechanism of its own. [ADR-025](ADR-025-governance-and-maintenance.md)
  owns that and it is not answered here.

## 7. Conditions

1. **Every claim that this project is Apache-2.0 or open source is corrected before the
   repository is made public** — not afterwards, because a public repository with a
   contradictory README is a licence question asked by strangers.
   *(Discharged 2026-08-25: six sites corrected and `an_outbound_licence_claim_that_is_stale`
   added to `tools/registers-check.py`, which fails the build if one comes back.)*
2. **The history is swept for secrets before the repository is public.** Already known:
   `tok` was committed on 2026-08-15 carrying a session token and removed the same day,
   so it is in the pack and would be published. This condition is not about the licence
   and is a precondition of the same event.
   *(**Discharged 2026-08-27, and the ordering it asks for was not kept.** The repository
   was created public on 2026-08-25; this is the first sweep of the whole history rather
   than of a pending range, so it ran two days after the event it was written to precede.
   That is stated rather than smoothed over, because a precondition met afterwards is a
   different fact from a precondition met.

   **What was swept:** every object in `git rev-list --all` — 691 commits, 7,589 distinct
   blobs — read out of the pack rather than off the working tree, so a file deleted in a
   later commit is still examined. The patterns were GitHub and AWS tokens, PEM private
   keys, Slack and OpenAI-shaped keys, JWTs, `Authorization: Bearer`, `SecretKey`
   assignments and `Password=` with a value long enough to be one.

   **The result is 406 matches and no secret.** Every one is an identifier rather than a
   value — `string password = IssuedPassword.Issue()`, `mustChangePassword`,
   `password = context.Request.Query["password"]` — or a fixture written to be obviously
   fake: `Password=hunter2` in `ErrorResponseTests`, `Username=gis;Password=secret` in the
   console tests, `Password=a-realistic-length-of-secret`. CI's own throwaway Postgres
   carries `Password=gis` beside the lines that create the container, and its
   `Graticula__SecretKey` is `AAAA…=`, a key of zeros for a store destroyed at the end of
   the job.

   **`tok` is not in the history at all.** The condition named it as known and it does not
   appear as a path in any commit, so either it was never staged or the 2026-08-20
   `filter-branch` of the unpushed range took it with the development password it was run
   for. Three narrower checks agree: the dev server's real `Graticula__SecretKey` value has
   never been committed (`git log -S`, no commits), no `.env`, `*.pem`, `*.key`, `*.pfx`,
   `secrets.*` or `credentials.*` path has ever existed in the tree, and the only
   `Graticula__SecretKey` assignments in history are the CI zeros and a `/dev/urandom` one.

   **What this does not cover, and it is the reason [D-183](../architecture-debt.md) is
   open:** a sweep is a moment, and GitHub's own `secret_scanning` and
   `secret_scanning_push_protection` — the things that would make the next commit safe
   rather than this one — are switched off on a public repository.)*
3. **ADR-025's contributor question is answered before the first outside contribution is
   merged**, not before the repository is public. Reading and forking need no agreement;
   merging somebody else's code does.

## 8. Revisit triggers

- **Somebody the project wants asks for Apache-2.0 or AGPL as a condition of using it.**
  That is the cost of this decision arriving in a form that can be counted, and it is the
  only evidence that would reopen it.
- **A commercial hosting question arrives from the owner's own side** — if Graticula is
  ever to be offered as a service by its author, ELv2 permits that and this ADR should
  say so explicitly rather than leave it inferred.
- **A dependency changes licence to something copyleft.** Then the outbound licence is no
  longer a free choice, and `DEPENDENCY-LICENSES.md` is where that would first show.
