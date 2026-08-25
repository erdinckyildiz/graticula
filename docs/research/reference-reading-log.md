# Reference reading log — kept outside this repository

**This file is a placeholder, and the thing it replaces was removed from every
commit of this history on 2026-08-25, before the repository was made public.**

## What was here

[ADR-030](../adr/ADR-030-reading-the-reference-implementation.md) permits reading
an anonymised checkout of a peer GIS server and attaches three conditions, the
second of which is that **every read is logged**. This file was that log: 22
entries, each naming what was read, what was learned, and which decision it fed.

## Why it is not published

**Most of it would have been fine to publish and one part of it would not.** Of
the 22 entries, the large majority record public sources — the peer's own GitHub
repositories, their published documentation, and black-box requests to a running
server. Reading a public competitor and writing down what you found is ordinary
work, and this project does it openly: the conclusions are in the ADRs that cite
them.

**One entry is different.** On 2026-08-17, at the owner's request, the log records
reading the *source contents* of the local checkout — three type names and the
shape of a converter contract. ADR-030 permits that read. What it also records is
that **the checkout's `LICENSE` file was scrubbed along with the product name**, so
whether that source was licensed to be read is not a question this project can
answer from the material it has.

Keeping such notes privately is a different act from publishing them. The decision
on 2026-08-25 was to keep the reading log out of the public repository and remove
it from history, so that the question never rests on somebody's later reading of
what a scrubbed licence might have said.

## What survives, and it is the part that matters

**Every derivation is disclosed in the decision it produced**, which is ADR-030's
first condition and is where the reasoning belongs anyway. [ADR-033](../adr/ADR-033-symbology.md)
§2 and §4 say which of its choices came from the reference. [ADR-020](../adr/ADR-020-admin-console-and-service-status.md)
§5b–§5d name what the peer's console taught. [ADR-029](../adr/ADR-029-affinity-routing-is-not-the-default.md)
cites their published architecture as evidence. Nothing in this repository asks
you to take a derivation on trust.

**What is lost is the audit trail**, not the argument: you can no longer check
that the disclosures are complete by reading the log beside them. That is a real
cost and it is stated rather than glossed.

## Where it is

With the project owner, outside this repository. Ask them.
