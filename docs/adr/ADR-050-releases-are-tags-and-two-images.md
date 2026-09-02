# ADR-050 — A release is a tag, and what it publishes is two images

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` for the registry and the trigger · `MEDIUM` for the tag scheme |
| **Decided** | 2026-09-02 |
| **Answers** | the release half of [Q-72](../open-questions.md), and closes [D-19](../architecture-debt.md) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

[D-19](../architecture-debt.md) has said since 2026-08-14 that there is **no published image
and no release**, so the quickstart needs a clone and a working Docker build rather than a
`docker pull` — and it calls that difference *most of the adoption*. The target user in
[product-context.md](../product-context.md) has a lapsed licence and a deadline.

Two things changed on 2026-09-02. The four commands in the README were run for the first time
and **did not work** — the datastore's health check reported ready while PostgreSQL was still
on the unix socket, so the server exited — and they are now a CI job of their own. And
[ADR-016](ADR-016-packaging-deployment-upgrade.md) condition 1's remaining half, which this row
was said to block, turned out not to need published tags at all:
`tools/stale-component-rehearsal.sh` arranges a stale component on a scratch schema.

So what is left is the thing itself: somewhere to publish to, a rule for what a version means,
and a process that runs without somebody remembering it.

[Q-72](../open-questions.md) was answered on 2026-08-15 for governance — a security contact,
DCO rather than a CLA, no triage commitment — and left the release process unwritten. This is
that part.

## 2. Alternatives considered

### Alternative A — Docker Hub

The registry most readers already have credentials for, and the one a `docker pull` with no
host prefix resolves to.

**Argument for.** Shortest possible instruction: `docker pull graticula/graticula`. No
namespace to explain.

**Argument against.** It needs an account this project does not have, a stored credential in
CI, and a rate limit that anonymous pulls hit. A release process whose first failure mode is
*somebody rotated a token* is a release process that stops working while nobody is watching.
It also separates the images from the source: the repository is on GitHub and the packages
would not be.

### Alternative B — Publish on every push to `main`

Build and push an image for each commit, tagged with the short SHA, and let `latest` follow.

**Argument for.** Nothing to remember. The newest code is always pullable, and a bug fixed at
lunchtime is available at lunchtime.

**Argument against.** It makes *which version is running* a question about time rather than
about a version, and [ADR-016](ADR-016-packaging-deployment-upgrade.md) §4b's refusal — a
component built for schema *n* against a store that requires *m* — needs a real answer to
exactly that. It also publishes every experiment: this repository's own register records
several days where `main` carried a defect for hours.

### Alternative C — A tag triggers a release; GHCR holds the images

`vMAJOR.MINOR.PATCH` on the git side. The workflow builds both images, pushes them under the
version and under `latest`, and creates a GitHub release.

**Argument for.** GHCR needs no second account and no stored secret — `GITHUB_TOKEN` with
`packages: write` is enough — and the packages sit beside the source. A tag is a deliberate
act, which is what a version should be.

**Argument against.** `ghcr.io/erdinckyildiz/graticula` is longer to type than
`graticula/graticula`, and a reader who has never used GHCR has one more thing to learn.

## 3. Counterarguments to the preferred option

**`latest` is a moving tag, and this project has spent its life arguing against those.**
ADR-016 §4b exists because an old component against a new store must be *identifiable*, and a
tag that means *whatever was newest* cannot identify anything. The answer is not that the
objection is wrong but that it is answered elsewhere: both images carry
`org.opencontainers.image.version` and `.revision`, so `docker inspect` gives the exact release
and commit without starting anything, and the quickstart gets the short instruction a first
reader needs. If that ever proves insufficient, `latest` goes and the README pins.

**Publishing at 0.x invites somebody to run it.** The register is public and says what is
unfinished, but a version number and a `docker pull` line are an invitation, and a reader who
skips the register will not know that the ArcGIS face has no optimistic concurrency
([D-186](../architecture-debt.md)) or that one console test fails per CI run
([D-173](../architecture-debt.md)). Marking every `v0.*` a pre-release and putting the register
in the release notes is a mitigation rather than an answer.

**A release process nobody has run is a claim.** This ADR is written before the first tag is
pushed, so what is here is a design and not yet evidence. The one thing that reduces it is that
the workflow runs the quickstart rehearsal **before** it pushes either image, so the first
failure it can have is the one that matters.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Both images build | `docker build` of `deploy/server.Dockerfile` and `deploy/datastore.Dockerfile`, 2026-09-02 | measured |
| The four commands work | `tools/quickstart-rehearsal.sh` from an empty machine: key printed, 37 migrations applied, `/healthz/live` in 2 s, setup token, `/rest/info` and `/rest/services` 200 | measured 2026-09-02, and a CI job |
| Published tags were not needed for ADR-016 condition 1 | `tools/stale-component-rehearsal.sh` arranges a stale component on a scratch schema | ADR-016 condition 1, discharged 2026-08-27 |
| GHCR needs no stored credential | `GITHUB_TOKEN` with `packages: write` | GitHub documentation |

## 5. Decision

**A release is a `vMAJOR.MINOR.PATCH` git tag.** Pushing one runs `.github/workflows/release.yml`,
which first runs the quickstart rehearsal, then builds both images and pushes them to
**GHCR** — `ghcr.io/<owner>/graticula` and `ghcr.io/<owner>/graticula-datastore` — under the
version **and** under `latest`, with `org.opencontainers.image.version` and `.revision` baked in
as labels, and then creates a GitHub release. **Every `v0.*` is marked a pre-release**, and the
notes point at the debt register rather than around it. `compose.yaml` names the published
images, so `docker compose up` pulls a release when there is one and builds from source when
there is not; `--build` forces the source path, which is what the rehearsal uses so that CI
tests the tree rather than the last release.

## 6. Consequences

**Positive.** The quickstart becomes `docker compose up` for somebody who has not cloned
anything. A version is a deliberate act with a rehearsal in front of it. An operator can ask a
running container exactly which release and which commit it is, which is what ADR-016 §4b's
refusal needs. And the release cannot be cut over a broken quickstart, because the rehearsal
runs first.

**Negative.** `latest` moves, and a deployment that follows it cannot say what it is from the
tag alone — the label answers, and somebody has to know to read it. Publishing at 0.x invites
use of a product whose register lists twenty open debts. And this adds a second workflow to
keep working: a release that fails at the push step leaves images half-published, which the
next tag overwrites rather than repairs.

**State.** None in the product. The release state is GitHub's — tags, packages and releases —
and nothing in the server reads it.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A reader who cannot pull will still clone, so the source path must keep working | Held: `compose.yaml` keeps both, and the rehearsal exercises the build |

## 8. Dependencies

**Depends on**: [ADR-016](ADR-016-packaging-deployment-upgrade.md) (what a component is and how
it refuses a store), [ADR-025](ADR-025-governance-and-maintenance.md) (what is promised about
maintenance), [ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md) (the licence in the image
labels).

**Depended on by**: [D-19](../architecture-debt.md) closes on this.

## 9. Revisit triggers

- Somebody reports being unable to tell which release a container is, which would take `latest`
  away and pin the README.
- A release is cut whose quickstart fails anyway, which would mean the rehearsal is not
  checking what a reader does.
- The project reaches 1.0, at which point *every v0.\* is a pre-release* stops applying and the
  rule needs restating rather than inheriting.

## 10. Dissent

**Publishing before the v1 scope is finished is arguable in both directions**, and the argument
against is not weak: a `docker pull` line is read by people who will never open
`architecture-debt.md`, and this project's whole method is that what is unfinished is written
down. The reason it stands is that the alternative — a server nobody can run without a clone
and a build — was measured as *most of the adoption* by the row that has carried this since
August, and a gift nobody can unwrap is not a gift.
