# ADR-007 condition 2 / A-015 — what binding a service context costs

**Run 2026-08-27** against a running server on this machine, nine published layers over
two data sources. `bind.py` is the harness.

[ADR-007](../../docs/adr/ADR-007-service-runtime.md) condition 2 is *A-015 must be
measured before §4.3's lazy binding is relied upon*, and
[A-015](../../docs/architecture-assumptions.md) is:

> Per-service warm state is small — connections, schema, symbology, fonts, CRS — making
> bind/unbind cheap.

**Lazy binding's whole cost is the cold bind**, so that is what is timed.
`ServiceContexts.Lifetime` is thirty seconds, so a request after thirty-one seconds of
quiet rebuilds the context and the next one does not. The request is identical either
way; the difference is the bind.

## Time

Three rounds, nine layers, each round preceded by 31 seconds of quiet.

| | samples | p50 | min | max |
|---|---|---|---|---|
| cold — the context is rebuilt | 27 | **13.6 ms** | 12.6 ms | 39.2 ms |
| warm — it is not | 54 | **10.4 ms** | 9.8 ms | 22.6 ms |

**+3.2 ms, or +31%.** That is one shape query against the layer's own database: the field
list and the extent. The 39.2 ms maximum is the first touch of the run, where the
connection pool is cold as well as the context — which is the honest worst case for a
service nobody has asked for in a while.

## Memory

Measured as **allocation**, from the runtime's own `allocatedBytes`, because a heap
reading is the GC's opinion rather than a count. Nine layers, each asked for once.

| | allocated | per request |
|---|---|---|
| each bound for the first time | 1,071,584 bytes | 119,064 |
| all already warm | 803,056 bytes | 89,228 |
| **the bind's own share** | **268,528 bytes** | **29,836 per context** |

**About 30 KB allocated per bind**, most of it transient — the query, the reader, the
strings. What is *retained* is a `LayerDescription`: a field list and an envelope, which
is smaller again and could not be isolated from outside, because nothing on this surface
forces a collection.

A first attempt read `heapBytes` before and after and got +883 KB over nine layers. That
number is an upper bound with a GC's timing inside it, and it is reported here only to
say why it is not the number above.

## What this answers

**A-015 holds.** Both halves: the warm state is a field list and an envelope, and the
bind is one query at 3.2 ms. §4.3's lazy binding is safe to rely on — a service that has
gone quiet costs 3 ms to bring back, which is a third of what serving its document costs
anyway.

**And the shape of the failure it was guarding against does not arise.** The assumption's
own note says that if bind/unbind is expensive, *"§4.4 eviction becomes costly so the
budget shrinks; §4.12 pinning becomes the norm rather than the exception — which
recreates per-service resource allocation, the thing §3 said killed ArcSOC."* At 3.2 ms
and 30 KB there is nothing to pin: evicting a context and rebuilding it is cheaper than
remembering which ones not to.

**What is not settled.** Nine layers on one machine, all small; a layer with two hundred
columns would bind more slowly and nothing here says by how much. Connections are pooled
per data source rather than per service ([Q-04](../connection-budget/RESULTS.md)), so
"connections" in A-015's list are not part of a bind at all — which is worth saying,
because the assumption names them and they turned out to belong to a different lifetime.
