# ADR-180 — The outbound licence is the Graticula Community License

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the decision, which is the owner's · `MEDIUM` for the licence text, which no lawyer has reviewed |
| **Decided** | 2026-10-13 |
| **Supersedes** | [ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md) |
| **Superseded by** | — |

---

## 1. Context

**The owner replaced the licence on 2026-10-13**, in their words *"uygulama lisans modelini
değiştirelim. Yeni lisans böyle"*, followed by the full text of a *Community License 1.0*.
This is the owner's decision and is recorded as one. What is `INFERRED` is listed in §5a
and nowhere else.

**It moves both of ADR-047's lines, in opposite directions.** ELv2 let anyone change
Graticula and forbade one use of it — offering it to third parties as a hosted or managed
service. The Community License permits exactly that use, by name, *"including paid managed
services"*, and forbids instead what ELv2 permitted: modifying it, creating derivative
works, and distributing or operating a modified version, without the copyright holder's
prior written permission.

| | ELv2 (ADR-047, v1.0.0 – v1.0.352) | Community License 1.0 (v1.0.353), 1.1 from v1.0.354 |
|---|---|---|
| Run it, commercially included | yes | yes |
| Deploy it for a client | yes | yes |
| Offer it to third parties as a hosted or managed service | **no** | **yes**, paid included |
| Redistribute it | yes, modified or not | **unmodified only** |
| Modify it, or build a derivative work | yes | **only with written permission** |
| Configure it through documented settings, environment, APIs, plugins | yes | yes — §3 says this is not modification |
| Patent grant | yes | **none** in 1.0 · yes from 1.1 (§5b) |

## 2. Alternatives considered

### Alternative A — Keep ELv2

**Argument for.** It is reviewed, widely recognised, carries a patent grant, and every
document in this repository already reasons from it.

**Argument against.** It is not what the owner wants. The owner gave the text; ELv2
forbids what the new text permits and permits what it forbids, so no reading of ELv2
reaches the same place.

### Alternative B — Adopt the text as given, with only the two placeholders filled (chosen)

**Argument for.** It is the owner's text. An edited licence is a new licence nobody asked
for, which is the reason ADR-047 §3 gave for leaving ELv2's inert licence-key clause
standing.

**Argument against.** The text has drafting gaps that a reviewed licence would not have,
listed in §3. Adopting it verbatim adopts them.

### Alternative C — Adopt it with repairs: define *the Software*, add a patent grant, add a contribution clause

**Argument for.** Each gap in §3 has a standard repair, and a licence that names its
subject is easier to rely on than one that does not.

**Argument against.** Repairing a licence is legal drafting, and this repository is not
the place to do it unasked. The gaps are recorded as [Q-166](../open-questions.md) for the
owner, who can amend the text as *1.1* if they choose; nothing here prevents it.

## 3. Counterarguments to the preferred option

**The strongest one: this change cannot reach back.** The repository has been public since
2026-08-25, and v1.0.0 through **v1.0.352** were released under ELv2, with images on GHCR.
A licence grants rights to whoever received a copy, and ELv2's grant is not revocable for
those copies. **Anyone holding v1.0.352 or earlier may still modify it and redistribute
the result under ELv2**, and nothing this ADR says changes that. The Community License
binds the commits after `9adeb199` and the releases from v1.0.353. This was measured on
2026-10-13 rather than assumed: the GitHub API reports the repository public, **0 forks,
1 star** — so the copies outside are few, but they exist and they are not ours to recall.

**The second: no outside contribution is possible without permission.** Writing a pull
request is modifying the Software, and §2 forbids that without prior written permission.
[CONTRIBUTING.md](../../CONTRIBUTING.md) invited contributions under the DCO; that
invitation is now conditional on permission, and the change says so (§5).

**Nobody has contributed yet**, so no one's work moves under terms they did not choose:
`git log` on 2026-10-13 shows every commit authored by the owner or by an assistant
working for them. That is luck about timing again, as ADR-047 said the first time.

**The third: the licence has no patent grant.** ELv2 and Apache-2.0 both granted patent
rights; this text grants copyright only. For a user that is a narrower promise than the one
they had on v1.0.352.

**The fourth: *the Software* is never defined, and *Community License 1.0* names no
product.** Several unrelated licences share the name. The LICENSE file at the root of this
repository identifies its subject by where it stands, which is how most short licences
work, but the name alone will not tell a reader which *Community License* is meant.
README and NOTICE therefore say *the Graticula Community License 1.0* where the name stands
on its own.

**The fifth: there is no SPDX identifier.** Image labels and package metadata expect one.
`LicenseRef-Graticula-Community-1.0` is the form SPDX defines for a licence it does not
list, and it is what the image labels carry.

## 4. Evidence

| Claim | Evidence |
|---|---|
| The text is the owner's, unedited | `LICENSE` is the pasted text byte-for-byte apart from `[YEAR]` → `2026` and `[COPYRIGHT HOLDER]` → `Erdinç Yıldız` |
| No dependency forbids the new restriction | Every linked dependency is MIT, BSD-2, BSD-3, Apache-2.0 or the PostgreSQL Licence ([DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md), measured for ADR-047 and unchanged since). None of them restricts the licence we put on *our* code; §5 of the new text leaves theirs untouched. GEOS (LGPL) and PostGIS (GPL) are separate processes and are not linked |
| Nobody else holds copyright in this code | `git log --format='%an'`, 2026-10-13: `Erdinc` 1,374 commits, `Erdinç Kadir YILDIZ` 1, `Claude` 2 |
| ELv2 copies exist outside | GitHub API, 2026-10-13: `private: false`, `forks_count: 0`, `stargazers_count: 1`; tags `v1.0.0`–`v1.0.352` published |

## 5. Decision

**The outbound licence is the Community License 1.0**, the owner's text, in `LICENSE` —
**amended the same day to 1.1, §5b.**
`NOTICE` states in plain words what it allows and what it forbids, because a reader who has
to infer that from a licence name has been told nothing — which was ADR-047's reason too,
and holds more strongly for a name nobody recognises.

- **What anyone may do:** use and run it, commercially included; deploy it for themselves
  or for clients; host and operate it for third parties, paid included; redistribute it
  unmodified; read the source.
- **What needs the copyright holder's written permission:** modifying it, building on it,
  and distributing or operating a modified version.
- **What is not modification:** configuration through documented settings, environment
  variables, APIs, plugins and extension interfaces, so long as the source is not altered.
- **Third-party obligations are unchanged.** `DEPENDENCY-LICENSES.md` stays the list.

**`source-available` is still the accurate phrase and `open source` is still false.**
`tools/registers-check.py` keeps failing the build on the latter, and now also fails it on
a document that still names ELv2 as this project's licence.

### 5a. What was inferred — confirmed by the owner 2026-10-13, [Q-166](../open-questions.md)

*"1. Evet. 2. Uygun. 3. Şimdilik verilmeyecek."* The holder's name and the identifier
stand as written below. **Contribution permission is not being given for now**, which is
stronger than item 3 assumed: CONTRIBUTING.md no longer says *ask in an issue*, it says
contributions are not accepted, and §9 of the 1.1 text says the same.

1. **The copyright holder is `Erdinç Yıldız`**, as `NOTICE` has said since 2026-08-25. One
   commit is authored as *Erdinç Kadir YILDIZ*; the legal name is the owner's to give.
2. **The identifier is `LicenseRef-Graticula-Community-1.0`** in image labels, and
   *Community License 1.0* in the three OpenAPI documents' `license.name`.
3. **Contributions:** CONTRIBUTING.md now says a pull request needs the copyright holder's
   permission before it is written, and that the DCO sign-off still applies when it is.
   How permission is given — an issue, an e-mail, a standing grant for pull requests — is
   not decided.

### 5b. The 1.1 amendment, 2026-10-13

Asked about §3's drafting gaps, the owner asked *"Nasıl yapalım?"*, was given four
additions, and approved them: *"Olur ekle"*. **Nothing in 1.0 was changed; four things
were added**, so the decision in §5 stands word for word.

| | What 1.1 adds | Why |
|---|---|---|
| Title | *Graticula* Community License 1.1 | Several unrelated licences are called *Community License*; the name now names its product (§3, the fourth) |
| §0 | A definition of *the Software*: source, object code, container images and documentation, excluding third-party components | The text used the word nine times and never said what it covered — images and documentation in particular (§3, the fourth) |
| §1 | A patent licence, ending for anyone who sues claiming the Software infringes a patent | ELv2 had one and 1.0 did not (§3, the third). It costs nothing where the holder has no patents |
| §9 | Contributions are not accepted without written agreement; one accepted is licensed to the holder irrevocably, relicensing included | Records the owner's *"şimdilik verilmeyecek"* in the licence, and keeps relicensing possible if that changes — the move ADR-047 and this ADR could make only because nobody else had contributed |

**Identifiers move with the version**: `LicenseRef-Graticula-Community-1.1` in image labels
and *Graticula Community License 1.1* in the OpenAPI documents. **v1.0.353 alone was
released under 1.0** and stays under it; 1.1 binds from v1.0.354.

**Still not reviewed by a lawyer.** The additions are adapted from Apache-2.0's and ELv2's
patent clauses and from common contribution terms, not drafted by somebody qualified to.
The owner was told so and chose to proceed.

## 6. Consequences

**Positive.**

- The licence says what the owner wants it to say.
- Hosting providers — the people ELv2 shut out — may now run Graticula for their
  customers, which is the commonest way a GIS server reaches somebody who cannot run one.

**Negative.**

- **No fork can live in the open.** Under ELv2 a fork could fix a bug and publish the fix;
  now it needs permission first. Every repair has to come back to this repository.
- Adoption costs that ELv2 had remain — no distribution packages it, some corporate
  policies reject unrecognised licences — and the unrecognised name adds one.
- **About thirty files named ELv2**, eight of them reasoning from it. They are corrected in the
  same change; [D-130](../architecture-debt.md)'s shape is the one being avoided.

**State.** The licence name is written into three OpenAPI documents and two image labels,
which is runtime output; nothing stores it and nothing reads it.

## 7. Conditions

1. **Every claim that this project is ELv2-licensed is corrected in the same change**, and
   `an_outbound_licence_claim_that_is_stale` in `tools/registers-check.py` fails the build
   if one comes back. *(**Discharged 2026-10-13**, in the commit that carries this ADR.
   Struck text, `reviews/`, `research/` and ADR-047 keep the old name, because they record
   what was true when they were written.)*
2. **The three inferences in §5a are confirmed or changed by the owner** — Q-166.
   *(**Discharged 2026-10-13**: all three confirmed, the third narrowed to *no
   contributions for now* — §5a.)*
3. **The owner decides whether the drafting gaps in §3 are repaired** — patent grant,
   definition of *the Software*, a contribution clause. Not repairing them is a valid
   answer; not deciding is not. Q-166.
   *(**Discharged 2026-10-13**: repaired, as the 1.1 text — §5b.)*

## 8. Revisit triggers

- **Somebody asks permission to modify it.** The first request is when §5a.3's open
  question stops being hypothetical.
- **Somebody the project wants declines to use it** because of the licence's name, its
  missing patent grant, or the modification clause.
- **A dependency changes licence to something copyleft.** A copyleft component linked into
  the artefact would require that modification be allowed, and §2 forbids it.
