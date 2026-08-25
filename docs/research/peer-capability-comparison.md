# Peer capability comparison — kept outside this repository

**This file is a placeholder, and the thing it replaces was removed from every
commit of this history on 2026-08-25, before the repository was made public.**

## What was here

A surface-by-surface comparison against the closest peer GIS server found: which
of their capabilities this project holds, which it holds partially, and which are
absent — separating a deliberate v1 scope cut from an API gap from a UI gap.

## Why it is not published

It is the other half of [reference-reading-log.md](reference-reading-log.md), and
it goes with it for the same reason. Much of the comparison was built from public
material and would have been unremarkable to publish. Parts of it were built from
the anonymised local checkout, whose `LICENSE` was scrubbed along with the product
name — so the question of what that source permitted cannot be answered from the
material this project holds, and the answer should not depend on somebody's later
guess.

**Separating the two halves file-by-file was possible and was not done**, because
a partial redaction that leaves the reader unable to tell which half they are
reading is worse than a clean absence.

## What survives

The comparison's *conclusions* are in the decisions they informed, cited there
rather than here:

- [ADR-020](../adr/ADR-020-admin-console-and-service-status.md) §5b–§5d — the
  console's shape, and the four design rules taken as rules.
- [ADR-029](../adr/ADR-029-affinity-routing-is-not-the-default.md) §4 — that a
  peer reaches the same scale without affinity routing, which is evidence in a
  decision rather than a description of their product.
- [Q-101](../open-questions.md), [Q-102](../open-questions.md),
  [Q-103](../open-questions.md), [Q-104](../open-questions.md),
  [Q-105](../open-questions.md) — five open questions opened by the comparison,
  each stating its own subject well enough to be answered without it.

Those five questions are the useful residue: what a comparison is *for* is finding
the questions you had not asked, and they are all still here.

## Where it is

With the project owner, outside this repository. Ask them.
