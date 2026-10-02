# ADR-135 — A service counts its use

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — what is counted is measured; that a count is what an administrator needs is Portal's view, taken |
| **Decided** | 2026-10-02 by owner decision (*"3. Eklensin"*, answering whether to add a usage count) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md) (`numViews` on an item) |

---

## 1. Context

The ArcGIS reviewer's third pass over Studio, 2026-10-01: an administrator cannot clean up what nobody uses, because
nothing says what is used. Every item answered `numViews: 0`. Portal shows an item's usage on its page and lets an
administrator find what is idle.

## 2. Alternatives considered

### Alternative A — Count each service's answered requests in memory, write a row a service a day (chosen)

A request a service's face answers successfully — FeatureServer, MapServer, ImageServer, VectorTileServer — adds one
to an in-memory count, an interlocked add on the request path and nothing else. Once a minute the host writes every
count since the last in one statement into `service_usage` (migration 75), a row a service a day, and reads the sums
back into a snapshot that listings read. The owner sees the count on the item page and sorts My content by least used;
the portal face's `numViews` is the all-time count; the administrator reads every service's at `/admin/usage`.

### Alternative B — A row a request

**Against:** the server's whole traffic in the platform store, for a question a daily sum answers.

### Alternative C — Read the server's request log

**Against:** logging is configurable and off by level in production (the benchmark rig's own finding); a count that
disappears with a log level is not a count.

## 3. Counterarguments to the preferred option

- A server stopped between two writes loses up to a minute of counts. A count, not an audit.
- A request is counted whoever asks — Studio's own previews and thumbnails too. Portal's views count its own pages as
  well; the measure is "asked for", not "asked for by a stranger".
- A failed write keeps its counts for the next minute rather than losing them.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Five requests to a service are counted as five (a failed one is not), shown with its item, and are the portal's `numViews`; the administrator reads every service's | `ServiceUsageTests` | this repository |
| The migration applies to a populated store and the schema tests hold | `PostgresPlatformSchemaStoreTests`, `UpgradeOnAFullStoreTests` | this repository |

## 5. Decision

Services count the requests they answer, a row a day, written once a minute; the count is the item's usage in Studio
and its `numViews` on the portal face.

## 6. Consequences

**Positive.** An owner sees whether an item is used; an administrator finds what is idle.

**Negative.** A table that grows a row a service a day.

**State.** Migration 75, `service_usage`, expand-only.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A usage count is wanted | Stated by the owner, 2026-10-02 |

## 8. Dependencies

**Depends on:** ADR-040, ADR-114 (My content).

**Depended on by:** —

## 9. Revisit triggers

- `service_usage` measured large enough to need its old days rolled up.
