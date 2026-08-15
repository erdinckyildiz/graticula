# Contributing

## Before anything else: what this project promises you

**Nothing, and that is deliberate.** gis-server is given away under Apache-2.0.
It is not a product with a support contract, it has never been released, and it
is maintained by one person. A contribution policy that implied otherwise would
be the first inaccurate document in a repository whose whole method is not
writing those.

So: pull requests are welcome, and they may sit. Issues are welcome, and there
is no triage commitment. If either of those is a problem for your use, fork it —
the licence exists so that you can, and a fork is not a failure of this project.

**What is promised is security reports.** [SECURITY.md](SECURITY.md) has dates
in it, and those are real.

## Sign your work — DCO, not a CLA

Every commit must carry a `Signed-off-by` line:

```
Signed-off-by: Your Name <your.email@example.com>
```

`git commit -s` adds it. It means you agree to the
[Developer Certificate of Origin](https://developercertificate.org/) — in plain
terms, that you wrote the change or have the right to submit it, and that you
are contributing it under the project's licence.

**Why the DCO and not a Contributor Licence Agreement.** A CLA asks you to sign
paperwork assigning rights to a project that promises you nothing in return, and
its usual purpose is to keep open the option of relicensing your work
commercially. This project has no such option to keep open: the licensing
decision is open source, permanently, and there is no commercial closed-source
distribution planned (`CLAUDE.md` §7). A CLA would therefore buy the project
nothing and cost every contributor a signature — so there is not one.

## How this repository works, and why it will look strange

This is not a normal codebase and reading a few files at random will not explain
it. Two things to know before you write anything:

**Every architectural decision is an ADR.** They live in [`docs/adr/`](docs/adr/)
and carry a status and a confidence level. If your change makes or reverses a
decision, it needs one — see [`docs/adr/_template.md`](docs/adr/_template.md).
Informal decisions are the thing this project is specifically built to prevent,
because the point of it is the reasoning and not the code.

**A performance claim needs a benchmark.** The standing challenge is *where is
the benchmark proving this?* — see `CLAUDE.md` §3. "This will be faster" is not
an argument here, and several beliefs in this repository have been overturned by
measuring them, which is why the rule is enforced rather than encouraged.

Read [`CLAUDE.md`](CLAUDE.md) first. It is the actual contributor guide; this
file is the paperwork.

## What a change needs

- **A test that fails without it.** For a bug fix, write the test first and watch
  it fail — this project has caught three of its own instruments lying, each time
  by trying to make a green test go red.
- **Comments that say why, not what.** The code explains itself; the comment
  explains the decision, the alternative rejected, or the defect that made the
  line necessary. Look at any existing file for the register.
- **The whole suite green.** `dotnet test`. The integration and conformance
  suites need a database and a running server and **fail rather than skip**
  without one; the README says which variables to set.
- **No new dependency without an argument.** [`docs/build-vs-adopt-policy.md`](docs/build-vs-adopt-policy.md)
  governs it, and Tier 1 has no package references at all — a build failure, not
  a convention.

## What will be turned down

- **Anything copied from MapServer, GeoServer or QGIS Server.** This project is
  clean-room (`CLAUDE.md` §5): existing products are studied for publicly
  documented behaviour and architectural reasoning only. Do not send code, and
  do not send an algorithm you read in their source.
- **A GPL or AGPL dependency.** Apache-2.0 outbound cannot carry it.
- **Technology with no stated problem.** Kubernetes, Kafka, Redis, service mesh,
  event sourcing, CQRS and microservice decomposition are all on the challenge
  list by name (`CLAUDE.md` §6). The baseline deployment is `gis-server →
  PostgreSQL/PostGIS`, and everything beyond it must be justified and optional.
- **Scope.** v1 is [`docs/v1-scope.md`](docs/v1-scope.md) and it is
  authoritative. Things left out were left out on purpose, with the reasoning
  written down, and §3 says what each removal bought.

## Disagreement

Record it, do not smooth it over. Several ADRs carry a **Dissent** section where
the argument against the chosen option was never actually defeated — only
outweighed. That is the standard: if you think a decision here is wrong, the
useful contribution is the argument, in the ADR, under your name.
