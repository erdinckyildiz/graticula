# ADR-084 — The page size is one number, and the server's is set from the console

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-23, by owner decision, in two answers. Asked about V-70 — a layer document saying `maxRecordCount` 50000 over a query answering 1000 — the owner wrote: *"1000 varsayılan olsun. her bir servisin sayfasından değiştirilebilsin. server tarafında genel ayarlar gibi bir yer olsun. oradan default da değiştirilebilsin. setlenmeyenler defaulttan alsınlar."* Asked then whether a service keeps two numbers (a maximum and a default page) or one, as ArcGIS has, the owner chose *"Tek sayı, ArcGIS gibi"*. |
| **Supersedes** | — (replaces the two-number model of migration 17 / Q-113, and the server figures of 2026-08-19) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A service carried two numbers since migration 17: the most rows one response may carry, and the rows a
query gets when it names none. The server carried two more in configuration: `Graticula:MaximumRecordCount`
(50000) and `Graticula:DefaultRecordCount` (1000). A layer document advertised the first pair — the
service's maximum, else the server's 50000 — while a query naming no page got the second. The third ArcGIS
review measured it on the showcase (V-70): `maxRecordCount` 50000, a parameterless query 1000, and a script
that pages by the document's number — `resultOffset` += `maxRecordCount` — skipping 49000 rows a page.

ArcGIS has one number. A query without `resultRecordCount` returns up to `maxRecordCount`, and a query asking
for more is held to it; clients and scripts are written against that.

## 2. Alternatives considered

### Alternative A — one number per service, and a server page size set from the console *(chosen)*

**Argument for.** It is ArcGIS's rule, so every client that sizes its paging from the document pages
correctly; and with one number the document and the query cannot disagree, because there is nothing to
disagree about. The server's page size in a settings table is what the owner asked for.

**Argument against.** A client that asked for more than the page used to get up to 50000 in one response,
and now gets the page and `exceededTransferLimit`, and pages. That is a behaviour change for such a client.

### Alternative B — two numbers, both settable, the document always saying the maximum

**Argument for.** Nothing an existing client gets changes.

**Argument against.** The trap V-70 found stays one careless setting away: an operator who sets them apart
rebuilds it. The owner was asked and chose A.

### Alternative C — the document says the default page, the maximum stays 50000

**Argument for.** One line changes.

**Argument against.** A client that reads `maxRecordCount` as the most it may ask for — which is what the
name says — is lied to the other way.

## 3. Counterarguments to the preferred option

- **Larger answers for a client that trusted the old maximum are gone.** True, and deliberate: a response
  up to 50000 rows was allowed because nothing said otherwise, and a client that wants them pages, as it
  would against ArcGIS.
- **A setting in the database is one more thing a query reads.** It is held for thirty seconds, and a store
  that cannot be read keeps the last value, or the configured one — a page size is never a reason to refuse
  a query.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The trap | Showcase layer: `maxRecordCount` 50000, parameterless query 1000 | Third ArcGIS review, V-70 |
| The fold keeps what a service answered | Fixture: a service with only a default page of 7 became page size 7; one with 500 and 5 kept 500; `default_record_count` empty after | Local fixture, migration 54 applied 2026-09-23 |
| Document and query agree | Server page size 100 on a 600-row layer: document 100, parameterless query 100 with `exceededTransferLimit`, `resultRecordCount=300` → 100, `=40` → 40; service page size 250 over server 100: 250 everywhere | Local fixture 2026-09-23; `AServerHasOnePageSizeTests` |
| Nothing else moved | 555 of 558 conformance tests passed against the migrated fixture; the three that failed fail for the fixture's trust authentication, database name and a degenerate test extent | Local run 2026-09-23 |

## 5. Decision

5.1 **A service has one page size — ArcGIS's `maxRecordCount`.** A query naming no `resultRecordCount` gets
it; a query naming more is held to it and told `exceededTransferLimit`; the FeatureServer and MapServer
service and layer documents give it. It is the service's own when set, else the server's, and never above
the deployment's ceiling.

5.2 **The server's page size** is stored in `server_setting` (migration 54, a table of named values) and set
from the console's new **Settings** screen or `PUT /admin/settings` under `admin:manageServer`. On the
screen, as on a service's Limits page, **an empty box means the default** and only a value set here is
shown in it (condition 1). Unset, it is
`Graticula:DefaultRecordCount`, else 1000 — so a deployment configured before the screen keeps its number.
Every change is audited with what it replaced. WFS takes it as the default `count`.

5.3 **The ceiling stays configuration.** `Graticula:MaximumRecordCount` (50000) is what no page may exceed,
set where the deployment is set; the screen says what it is and refuses a page above it.

5.4 **The default page is retired.** `defaultRecordCount` on a service's settings is refused with the reason,
so a script that sends it learns rather than being ignored. Migration 54 folds it: a service that set only a
default takes it as its page size, which keeps what a parameterless query got; one that set both keeps its
maximum, which is what its document said. The column is emptied and read by nothing, and dropped by a later
contract, so the release before this one can still start against a migrated store.

## 6. Consequences

**Positive.** The document's number is the number: a client paging by it gets every row. An operator sets
the server's page size on a screen, and a service's page shows what an empty box means.

**Negative.** A client that asked for more than 1000 in one request gets 1000 and pages. A dead column
stays for a release (§5.4, condition 2).

**Ports created.** `IServerSettingStore`, which the next server-wide setting uses rather than a migration.

**State.** Catalogue: `server_setting` rows in the platform store, shared by every node. Runtime: the page
size held for thirty seconds per node, replaced at once on the node that wrote it.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients size paging from `maxRecordCount` | The review measured a script doing so; the SDKs document it |

## 8. Dependencies

**Depends on:** [ADR-031](ADR-031-service-capability-configuration.md) (a service's limits narrow and never
widen — the ceiling), Q-113 (cost ceilings).

**Depended on by:** —

## 9. Revisit triggers

- A client measured needing responses larger than its service's page size in one request.
- A second node, where thirty seconds of disagreement after a change is measured to matter.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The Settings screen passes the design review every screen here goes through**, with its first-run
   state tested — `SettingsScreenTests`. **DISCHARGED 2026-09-24.** The review, run in a browser against the
   migrated fixture at 1280 and 390 pixels, found three major faults and all three are repaired:
   - **Save pressed by habit stored the default as the operator's own.** The box held the value in force, so
     `Graticula:DefaultRecordCount` silently stopped applying after one Enter. The box now holds only what
     was set here and is empty otherwise, with the default as its placeholder — the Limits page's model, so
     an empty box means the same thing on both screens; an empty Save with nothing stored sends nothing, and
     text the browser cannot read as a number is refused rather than read as empty.
   - **The Limits page refused 0 in an exception's words and 1.5 as a status line.** Both are checked in the
     page with the Settings screen's sentence, and the server refuses a page size below one in that
     sentence; every other refusal on the page loses what .NET appends for a developer.
   - **At 390 pixels the service's navigation never collapsed**, and the page size box was cut off at the
     edge. The narrow-screen rule named the layer editor only, though its comment named both.
   Minor ones repaired with them: a refusal is marked and `aria-invalid`; a stale sentence is cleared on
   arrival and on typing; both boxes are described by the line that says where the value comes from; every
   box on the Limits page has a label; the sidebar's links keep a name at rail width; *configured value*
   became *default*, the owner's word; Studio's overview says *Page size*.
   **One finding was not taken as written.** The review asked the Limits page to refuse a page size above
   the ceiling, as Settings does. The page's own rule, stated at its foot for every limit on it, is that a
   value above the server's is held down rather than refused; making one field an exception would put two
   rules on one page. The rule stays and is now said where it applies: the sentence under the box names the
   ceiling, and a save that was held down says so. Settings refuses, because what it sets is the server's
   own default and *raise the ceiling first* is something the reader can do.
2. **`default_record_count` is dropped by a contract migration** in a release after this one, raising the
   minimum reader to 54, once a store migrated by this release has run a release.
3. **The showcase keeps its numbers across the migration**: run on a copy first, and its services' page
   sizes and documents read before and after.
