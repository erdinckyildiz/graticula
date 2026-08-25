# ADR-025 — Governance: what a gift owes, and what it does not

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-08-15 |
| **Answers** | Q-72 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[Q-72](../open-questions.md) came out of Q-49's answer. Q-49 asked what the
market case for this product is; the owner's answer dissolved the question — the
project is a gift, and a gift owes no market case. **But a gift is not free of
obligation, and Q-72 is the list of what it does owe.**

The specific pressure: **a public server product with no security contact is a
liability rather than a gift.** Somebody finds a flaw, has nowhere to send it,
and either drops it publicly or drops it entirely. Both outcomes are worse than
the five minutes it costs to say where reports go.

**Nothing is exposed today.** The GitHub repository is private, there is no
published image and no release ([D-19](../architecture-debt.md)). That is
exactly why this is being decided now: the trigger is *before the repository
goes public*, and a decision taken then is taken at the busiest moment of the
project instead of a quiet one.

[§55](../../MASTER_GIS_PLATFORM_PROMPT.md) covers licence obligations and nothing
else, so this is not a gap in a document — it is a document that does not exist.

## 2. Alternatives considered

### Alternative A — write nothing until the first release

**Argument for.** The repository is private and there is no released artefact,
so there is currently no security contact to fail to answer and no contributor
to turn away. Every commitment written now is a promise made before anybody has
asked for anything, by a project that has not yet proven it can keep a smaller
one. Writing governance for a userbase of one is ceremony.

**Argument against.** It moves the decision to the release, which is the moment
with the least attention available. And "we will write it when we need it" is
how a project ends up with a public repository and an issue titled *I think I
found something, where do I send it?*

### Alternative B — full governance now: triage SLA, release cadence, CLA

**Argument for.** The strongest possible position for an organisation deciding
whether to depend on this. The audience in [ADR-018](ADR-018-authorization-and-roles.md)
§1 is an organisation leaving ArcGIS Enterprise; such an organisation asks who
maintains this and what happens when it breaks, and a full answer is a real
competitive advantage over projects that shrug.

**Argument against.** **Every line of it would be a promise one person cannot
keep.** A triage commitment nobody meets is worse than none stated, because the
first missed one converts every other commitment on the page into a claim rather
than a fact. And a release cadence for a project with no release is a schedule
for work that has not been scoped.

### Alternative C — minimal, before the repository goes public (chosen)

**Argument for.** It writes down exactly the obligations that exist whether or
not they are written down — where a security report goes, on what terms a
contribution is accepted, and what is *not* supported — and it declines to write
the ones that do not exist yet. The dates it does commit to are dates one person
can keep.

**Argument against.** It is a partial answer that will need revisiting, and a
governance document that says "this gets rewritten later" is weaker than one
that does not.

### Alternative D — a Contributor Licence Agreement

Considered separately because it is orthogonal to how much else gets written.

**Argument for.** A CLA keeps relicensing open. If this project were ever to
have a commercial edition, or to move to a foundation that requires assignment,
contributions gathered under a DCO cannot be relicensed without tracking down
every contributor.

**Argument against.** ~~**The option a CLA preserves is one this project has
already declined.** `CLAUDE.md` §7 records that licensing is open source with no
commercial closed-source distribution constraint — the flexibility is worth
nothing here.~~

**This argument failed on 2026-08-25 and is struck rather than deleted.**
[ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md) relicensed the project
from Apache-2.0 to the Elastic License 2.0 — so the option this paragraph called
*already declined* was exercised ten days after the paragraph was written. **The
flexibility a CLA preserves was not worth nothing; it was worth exactly the move
that has just been made**, and it cost nothing only because there were no outside
contributors to track down.

**What survives is the other half**, and it was always the stronger one: the cost of asking a contributor to sign paperwork
assigning rights to a project that promises them nothing in return. That is a
bad trade in a gift, and it is measurably a deterrent to drive-by contributions,
which are the only kind this project can currently expect.

## 3. Counterarguments to the preferred option

**The strongest one: the dates are fiction until somebody tests them.** SECURITY.md
commits to acknowledgement in 7 days and a fix target of 90. Nobody has ever
sent a report, so those numbers are an intention with a table around it. The
mitigation is not a stronger promise, it is an escape hatch for the reporter: the
policy tells them that if they have heard nothing in 14 days the message was
lost and to resend, and it states plainly that these are commitments made by one
person and will slip if that person is unavailable. **A reporter who knows the
limit can work with it; a reporter who discovers it is a reporter who goes
public.**

**The second: "no triage commitment" may read as "do not bother".** It might.
The alternative reading — an implied commitment, then silence — is worse, and
the honest version at least tells a would-be contributor to fork rather than
wait. The licence exists so they can.

**The third: a DCO is weaker provenance than a CLA.** True, and the weakness is
real if this project ever needs to relicense. The trigger in §9 is that event,
which is observable, rather than a judgement that it might one day happen.

## 4. Evidence

This is a policy decision rather than a measurable one, so §4 is thin by nature
rather than by omission — what evidence exists is about what other projects do
and what this one has already decided.

| Claim | Evidence |
|---|---|
| ~~The relicensing option a CLA preserves is already declined~~ **Falsified 2026-08-25** | ~~`CLAUDE.md` §7: open source, copyleft acceptable, no commercial closed-source distribution constraint~~ **[ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md) took that option ten days later. The row is kept because a piece of evidence that turned out to be wrong is worth more here than a tidy table** |
| Nothing is exposed today, so the trigger is publication rather than a date | `gh repo view`: `"isPrivate": true`, Apache-2.0. [D-19](../architecture-debt.md): no published image, no release |
| The DCO is sufficient for a permissively licensed project not seeking assignment | The Linux kernel, Git and Docker all use it in place of a CLA; it is a public, versionless statement a contributor makes rather than an agreement they sign |
| 90 days is the disclosure norm rather than a number chosen here | Standard coordinated-disclosure practice across the industry; adopted rather than invented so that a reporter already knows the shape of it |

## 5. Decision

**Three documents, written before the repository becomes public, and no more
than three.**

[SECURITY.md](../../SECURITY.md) names GitHub private vulnerability reporting as
the primary route and an email as fallback, states what is in scope — including
the four deliberate trade-offs a report should not merely rediscover — commits
to acknowledgement in 7 days, assessment in 30 and a 90-day coordinated
disclosure deadline that is **published at 90 days with or without a fix**, and
says outright that these are one person's commitments and where their limit is.

[CONTRIBUTING.md](../../CONTRIBUTING.md) requires **DCO sign-off and not a CLA**,
states that there is **no issue-triage commitment and no support**, points at
`CLAUDE.md` as the real contributor guide, and names what will be turned down —
anything clean-room-tainted, any GPL/AGPL dependency, any technology with no
stated problem, and anything outside v1 scope.

**What is deliberately not written**: a triage SLA, a release cadence, a
supported-versions table, and a CLA. Each is named in the documents as absent
rather than left to be inferred.

## 6. Consequences

**Positive.**

- The obligation that actually exists — somewhere to send a security report —
  exists before the exposure does, which is the only order in which that is
  worth anything.
- CONTRIBUTING.md turned out to be a useful forcing function: writing down what
  will be turned down required the clean-room rule, the benchmark rule and the
  ADR rule to be stated as *rules a stranger must follow* rather than habits the
  author has. They read differently that way.
- The absence of a CLA is now a recorded decision with a reason, rather than an
  omission somebody later "fixes".

**Negative.**

- **The security dates are untested and will be tested by a real report, at a
  time not of our choosing.** §3 mitigates this for the reporter and does not
  fix it for us.
- **A one-person project saying so in writing is a competitive weakness**, and
  the audience most likely to read it is the enterprise evaluator ADR-018 §1
  describes. The alternative is saying something else, which would not be true.
- **The email in SECURITY.md is a personal address**, which is what exists.
  Condition 3.
- Three more documents to keep true, in a repository whose recurring defect is
  documents drifting behind decisions.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-064 | A security report will arrive at some point after publication, and the cost of having nowhere to send it exceeds the cost of the policy | `UNVALIDATED`, and untestable in advance — this is a bet on an event, taken because the downside is asymmetric |
| A-065 | Requiring a DCO rather than a CLA does not deter contribution, and requiring a CLA would | `UNVALIDATED`. Widely believed and consistent with practice in comparable projects; nobody has contributed here yet, so this project has no evidence of its own |

## 8. Dependencies

**Depends on**: `CLAUDE.md` §7 (licensing), which is owner-set and is the reason
Alternative D fails. [ADR-016](ADR-016-packaging-deployment-upgrade.md) for what
a release is, once there is one.

**Depended on by**: nothing yet. [D-19](../architecture-debt.md) — the first
release must fill in the supported-versions table these documents leave empty.

## 9. Revisit triggers

- **The repository becomes public** — at which point condition 1 must already be
  discharged, and this ADR is checked rather than assumed.
- **The first release ships.** The supported-versions table stops being "there
  are no versions" and a release cadence becomes a statement about observed
  behaviour rather than a schedule for unscoped work.
- **A second maintainer joins.** SECURITY.md's "one person, and that is the
  honest limit" paragraph is then false and must be replaced with the
  arrangement that supersedes it.
- **A relicensing need appears** — a foundation, a dual licence, anything
  requiring assignment. The DCO decision is then genuinely tested and this ADR
  reopens.
- **The first security report arrives.** Whether the dates held is evidence, and
  it is the only evidence this decision can ever get.

## 10. Conditions

1. **SECURITY.md and CONTRIBUTING.md exist before the repository is made
   public**, and GitHub private vulnerability reporting is enabled on it — the
   policy names that route first, so a policy pointing at a disabled feature
   would be worse than no policy. *(Documents written 2026-08-15. The GitHub
   setting is the owner's to enable and is not discharged.)*
2. **The supported-versions table is filled in at the first release**, not left
   saying "there are no versions" in a repository that has some.
3. **The security contact is confirmed by the owner.** The address in
   SECURITY.md is the one configured in git, chosen as the obvious default and
   flagged here rather than assumed. A personal address on a public repository
   is the owner's call, and a role address or an alias may be preferred.
4. **The claims SECURITY.md makes about scope stay true.** It names four
   deliberate trade-offs by ADR — the archive bounds, the read-only cookie,
   credentials in URLs, the overlay deadline. If any is changed or removed, that
   list is wrong, and a scope statement that is wrong invites the wrong reports
   and dismisses the right ones.

## 11. Dissent

**Recorded, and it is Alternative B's.** An organisation evaluating whether to
depend on this will read "no triage commitment, no support, maintained by one
person" and, quite reasonably, not depend on it. That is a real cost to the
positioning, and nothing here disputes it.

The answer is not that the cost is small — it is that the alternative is a page
of commitments that would be broken, and a broken commitment costs more than an
absent one because it retroactively devalues the ones that were kept. **The
security dates are on the page precisely because they are the subset that can be
kept.** Somebody reading this later should understand that the sparseness is a
judgement about capacity, not a view about what a project ought to offer.
