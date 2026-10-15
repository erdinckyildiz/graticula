# Contributing

## Changes are not accepted from outside

**Pull requests are not merged here** — owner decision, 2026-10-15
([ADR-047](docs/adr/ADR-047-the-outbound-licence-is-elastic-2.md) condition 3). Graticula is maintained by one person
under the [Graticula Community License 1.1](LICENSE)
([ADR-180](docs/adr/ADR-180-the-outbound-licence-is-the-community-licence.md)), and accepting outside code would mean
either a contributor agreement or giving up the ability to change that licence. Neither is wanted, so the simpler
answer is taken: the code here is written here.

What that leaves open:

- **Issues are welcome.** A defect, a measurement, a client that does not open something — say what you did and what
  happened. There is no triage commitment, and an issue may sit.
- **Reading, running and forking are what the licence is for.** Read [LICENSE](LICENSE) for what it allows and the one
  thing it does not: offering Graticula to others as a hosted or managed service.
- **Security reports are promised.** [SECURITY.md](SECURITY.md) has the routes and the dates, and those are real.
- **An argument against a decision is a contribution.** Several ADRs carry a **Dissent** section where the argument
  against the chosen option was never defeated, only outweighed. If you think a decision here is wrong, open an issue
  with the argument; it may end up in that section.

This file used to describe a DCO and how a change would be reviewed, and to say the project was given away under
Apache-2.0. The licence moved to the Elastic License 2.0 on 2026-08-25 and to the Community License on 2026-10-13, and
the question of outside contributions was answered on 2026-10-15; the old text is in this file's history.

## How this repository works, and why it will look strange

This is not a normal codebase and reading a few files at random will not explain it. Two things to know before reading:

**Every architectural decision is an ADR.** They live in [`docs/adr/`](docs/adr/) and carry a status and a confidence
level. Informal decisions are the thing this project is specifically built to prevent, because the point of it is the
reasoning and not the code.

**A performance claim needs a benchmark.** The standing challenge is *where is the benchmark proving this?* — see
[`CLAUDE.md`](CLAUDE.md) §3. Several beliefs in this repository have been overturned by measuring them, which is why
the rule is enforced rather than encouraged.

Read [`CLAUDE.md`](CLAUDE.md) first; it is how the work here is done.
