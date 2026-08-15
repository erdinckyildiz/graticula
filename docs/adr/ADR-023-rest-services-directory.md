# ADR-023 — The REST Services Directory: an HTML face for the API

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-08-15 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The project owner opened `/rest/services` in a browser, and said:

> *"Actually when I open rest/services as a url, I want to see all services in a
> list like arcgis does."*

and then, with two screenshots of an ArcGIS REST Services Directory:

> *"a similar approach"*

What they got was `{"currentVersion":10.81,"folders":["hosted"],"services":[…]}`.

**This is not a cosmetic complaint.** Typing a server URL into a browser is how
a GIS administrator finds out what a server has. It is the first thing anyone
does with an unfamiliar ArcGIS server, it is how a URL gets copied into a client,
and it is what somebody reaches for when a client is failing and they want to
know whether the service is there at all. A server that answers only JSON is
complete and, at the moment somebody is exploring it, unusable.

It is also the surface where a compatibility claim is judged before any test
runs. [ADR-005](ADR-005-api-architecture.md) commits to ArcGIS REST
compatibility; a directory that looks nothing like one tells an evaluator, in
the first five seconds, that the compatibility is partial.

**What forces a decision rather than an implementation:** rendering HTML from an
API is a real architectural commitment. It creates a second representation of
every document, and second representations drift. It also puts user-supplied
strings — layer names, field aliases, folder names — into markup read by the
most privileged account on the system.

## 2. Alternatives considered

### Alternative A — No HTML. Point people at the console.

**Argument for.** [ADR-020](ADR-020-admin-console-and-service-status.md) already
commits to an administrative console. Two browsable surfaces is one more than
necessary, and the console can be better than a directory at everything a
directory does. The API stays a pure data API, which is the cleaner thing to be.
Nobody has to think about XSS in a JSON endpoint.

**Argument against.** The console is a different product with a different URL,
and neither of those is what somebody types. The specific act being served here
— *paste the server URL, see what is on it* — is not served by a console at a
different address, and the person doing it is often doing it precisely because
something is broken and they are checking the API directly. A directory that
loads when the console does not is worth having for that reason alone.

### Alternative B — A separate HTML application at `/rest/services/html/…`

**Argument for.** Keeps the API endpoints untouched. The renderer can evolve
without any risk of changing a JSON byte.

**Argument against.** It is at a different URL, so it does not answer the request
that was made: *when I open rest/services*. And a parallel URL space is a second
routing table, which is the arrangement most likely to produce a directory that
lists a service the API does not serve.

### Alternative C — Content negotiation on the same routes (chosen)

**Argument for.** One URL, one routing table, one document. `?f=html` or a
browser's `Accept` header selects the representation, which is both ArcGIS's own
convention and the ordinary HTTP one.

**Argument against.** Every endpoint now has two exits, and the HTML exit is the
one no automated client tests. It is also the one that can carry an injection.

### Alternative D — Render from a template per document type

**Argument for.** Full control over each page; a layer page can be laid out as a
layer page rather than as a generic property list.

**Argument against.** Four templates that must each be updated when a document
gains a field. The failure is silent — the field is simply missing from the page
— and the drift is discovered by somebody debugging something else.

## 3. Counterarguments to the preferred option

**The strongest one: this is scope that serves an impression.** Nothing in
[v1-scope](../v1-scope.md) asks for a browsable directory. Every hour spent on
it is an hour not spent on D-25, D-26, D-27 or the seven unrun review gates, and
those are commitments with dates. A directory is the kind of work that feels like
progress because it is visible.

The answer is that it was asked for directly, twice, by the person the software
is for — and that the request surfaced a live security defect within an hour of
being made (§6, and [ADR-018](ADR-018-authorization-and-roles.md) §3b-i). That is
not a coincidence: the geometry service was invisible in the catalogue *because*
nothing governed it, and nothing governed it because it was not a layer. Being
unable to see the server's own service list is what hid it.

**The second: two representations will disagree.** They always do. This is
mitigated by rendering the serialised JSON rather than the source objects — the
HTML page physically cannot show a field the API does not return — but mitigated
is not prevented, and the mitigation is a convention, not a type.

**The third, and it is not fully answered: XSS against the administrator.** Every
name on these pages is user input, and the reader holds every privilege the
server has. The defence is a single encoding helper and the discipline that
nothing is written without it, plus the tests in
`tests/GisServer.Host.Tests/RestDirectoryTests.cs`. Discipline plus tests is
weaker than a type that cannot be rendered unescaped. Recorded as condition 3.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A folder listing that filters on "is this the root?" breaks when a second folder exists | `/rest/services/Utilities` listed all five hosted layers under names that 404 | Found by opening the URL; regression test `Another_folder_does_not_list_the_hosted_layers` |
| The geometry service was reachable with no authentication | anonymous `POST …/GeometryServer/project` → `200` | Reproduced 2026-08-15; now `404`, test `An_anonymous_caller_does_not_reach_an_organisation_shared_service` |
| Every service the catalogue lists resolves | 3 catalogues walked, every `{name}/{type}` fetched | `Every_listed_service_resolves` |
| Encoding holds for hostile names | `<script>`, `"><img onerror>`, `"` in an href | `RestDirectoryTests`, 5 cases |
| Paging returns contiguous, non-overlapping pages | offsets 0/3/6 on a real layer returned objectids 1-3, 4-6, 7-9 | `Pages_do_not_overlap_or_skip` |
| `supportsPagination` and `supportsOrderBy` were false while both worked | layer document vs `FeatureServerQueryParameters` | `The_layer_document_claims_the_paging_and_ordering_it_does_support` |
| A multi-layer service renders as one page with links to each layer, and each layer's fields as a table | `EarlyAlert_Reports_HD` with GeoPoint, GeoLine, GeoFence | `MultiLayerServiceConformanceTests`, 7 cases |

**What is not measured:** nothing here is a performance question. The pages are
rendered from documents already built for the JSON path, so the cost is string
concatenation on a request that was already doing catalogue I/O.

### 4a. What the directory then exposed — added 2026-08-15

Reading the rendered pages produced the second correction of the day: *"we need
the fields also be shown. a service is a combination of layers actually."* Both
halves came from looking at output, and neither would have come from reading
code.

- **Fields were present and unreadable.** They rendered as inline
  `name: value, name: value` prose — technically complete, and hopeless for
  somebody trying to find one column's type before writing a query. Arrays of
  like-shaped objects (`fields`, `layers`) now render as tables, with columns
  taken from the **union** of the keys rather than the first element's, because
  `domain` and `length` appear on some fields and not others and dropping a
  column is invisible.
- **A service being one layer was visible the moment it was drawn.** See
  [ADR-013](ADR-013-feature-service-data-model.md) §4g. The page said
  `Layers:` and listed exactly one, always, and the URL underneath it always
  ended in `/0`.

That is now twice that this surface has exposed something the code did not.
Recorded because it is an argument about what browsing your own product is for,
not a coincidence: **rendering forces every field to be somewhere on a page**,
and a model that is wrong has nowhere to hide once it is drawn.

### 4b. The query page — added 2026-08-15

*"can you define query pages as well"*, then *"check arcgis query page on web
can create something similar."*

`f=html` on the query operation is a documented ArcGIS format, and the page it
produces is where a WHERE clause gets tested before it goes into a client. It is
also the third thing this surface has produced by being built rather than
reasoned about (§4a): writing the form meant listing which parameters to offer,
and that list had to be checked against what the query endpoint honours.

**The check found the capability document lying, in the direction nobody
audits.** `supportsPagination` and `supportsOrderBy` were `false`. `resultOffset`,
`resultRecordCount` and `orderByFields` had all been honoured since the query
endpoint was written. ADR-008 §2's never-degrade-silently is normally read as
*do not over-claim* — over-claiming puts a button in front of somebody that
returns an error, which is loud. **Under-claiming is quiet and costs the whole
capability**: a client reading `supportsPagination: false` does not page, so it
asks for the entire layer in one request or refuses the large ones, and nothing
anywhere reports a problem.

Both are now `true`, and there is a second place they had to go:
`advancedQueryCapabilities`, which is where the ArcGIS specification puts them
and what a modern client reads. The flat flags are the older shape and are kept.

**Pagination is an honest claim only because the order is deterministic.** Esri's
documentation requires a paginated query with a constant where clause to keep a
consistent sort order across pages, and PostgreSQL's `LIMIT`/`OFFSET` without an
`ORDER BY` does not — page two can repeat page one. The provider already orders
by the identity column whenever an offset is given, which is what makes the
declaration true rather than merely convenient. Asserted by
`Pages_do_not_overlap_or_skip`, which is the only thing that would notice if that
ordering were removed: the responses would stay well-formed and become wrong.

**The form offered only what the server honoured — and the owner rejected that,
correctly.** *"I wanted something similar to this, with all capabilities."* A
form with twelve of ArcGIS's forty controls is not a smaller version of that
page; it is a different page, and an administrator who knows Esri's is left
hunting for a field that is simply not drawn.

The position that replaced it, and it is better than either extreme:

- **Every ArcGIS parameter is on the page, in ArcGIS's order.** A screenshot of
  the two should differ only in what is greyed out.
- **Most of them now work**, because listing the controls forced the question of
  why they did not. See [ADR-008](ADR-008-query-engine.md) §4a and §4b: the
  where-clause parser, all nine spatial relationships, every input geometry
  type, distance and units, ids-only, extent-only, distinct, statistics with
  grouping and having, precision, generalisation, and output reprojection.
- **The eight that cannot work are disabled with the reason beside them.** A
  disabled input is not submitted, so the request still matches exactly what the
  enabled controls describe — and *"Not supported: no layer here declares
  timeInfo"* answers the question on the spot, where a missing control answers
  nothing and an enabled one produces an error the page invited.

So the form is still a live capability report. It just reports the whole surface
instead of only the part that works, which is more information rather than less.

**Results stream, like the JSON path.** A-037 measured allocation as the binding
constraint; buffering a page of features to build a table would reintroduce the
peak the JSON writer is careful to avoid. Rows are written as they arrive, which
is why `RestDirectory` grew an `OpenPage` that leaves `<main>` unclosed. The
first row is still pulled before a byte is written, for the reason the JSON
writer documents: executing the query is what raises the statement timeout, and
once the header is in the pipe the status cannot be taken back.

**Geometry is summarised, not printed** — *"polygon, 5 vertices"* — because a
polygon's coordinate list is thousands of numbers and pasting it into a cell
makes the attributes impossible to find. The coordinates are one click away in
the JSON.

**An explicit `f=json` still beats a browser's `Accept` header**, and there is
now a test that sends both together. Every existing caller sends `f=json`; if
the header ever won, the query endpoint would start returning HTML to machines,
and the JSON suite would not catch it because it sends no `Accept` header at all.

## 5. Decision

`/rest/services`, its folders, each `FeatureServer` and layer document, and the
`GeometryServer` document each answer **HTML when `f=html` is given or when the
`Accept` header asks for `text/html`, and JSON otherwise** — an explicit `f`
always winning, so every existing client is unaffected. The HTML is rendered by
one module, `RestDirectory`, which **walks the serialised JSON of the same
document the JSON path returns** rather than reading the source objects, so the
two representations cannot describe different fields. **Every value passes
through one HTML-encoding helper**, and every value that reaches a URL passes
through a segment-wise URL escape. The page is deliberately plain and loads no
external resource, because it must be legible when it is the only thing working.

## 6. Consequences

**Positive.**

- The request that was made is answered: a browser at `/rest/services` shows
  folders and services, with working links.
- The catalogue became the place where *all* services are listed, which is what
  exposed the geometry service having no sharing at all. That defect had shipped
  and would not have been found by a test of the sharing code, because the
  sharing code was correct.
- A `?f=json` link on every page, so the directory is a route into the API rather
  than a substitute for it.
- Four real defects found by opening URLs: the second-folder filter; a `404`
  that Kestrel turned into a connection reset because the filter wrote a response
  without reading the POST body; the service model itself (§4a); and a creation
  response handing back a URL that 404s, because it built the address from the
  layer's name after the layer had been published into a differently named
  service.

**Negative.**

- Every catalogue and metadata endpoint now has a second exit that no ArcGIS
  client exercises. Conformance tests check the JSON; only the unit tests check
  the HTML.
- A permanent XSS surface, defended by convention (§3, condition 3).
- The breadcrumb has to know that `FeatureServer` and `VectorTileServer` are
  type segments rather than resources. That is a small piece of ArcGIS URL
  grammar living in a renderer, and it will need a line when a service type is
  added.
- The conformance suite grew an optional sign-in, because an organisation-shared
  service cannot be tested anonymously. It defaults to anonymous, so the suite
  can still tell the difference between public and shared — but a suite that can
  sign in is a suite that can hide an authorization regression if somebody makes
  the login unconditional.

**Ports created.** None. No Tier 2 dependency is adopted; the renderer is
`StringBuilder` and `System.Text.Json`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-061 | An administrator's first act against an unfamiliar server is to open its URL in a browser | `UNVALIDATED` — asserted from the owner's request and from ArcGIS's own design; the validation route Q-49 would have provided was dissolved, so this has no route, like A-003 |

## 8. Dependencies

**Depends on:** [ADR-005](ADR-005-api-architecture.md) (the documents rendered),
[ADR-018](ADR-018-authorization-and-roles.md) (what a caller may see listed),
[ADR-022](ADR-022-geometry-server.md) (the geometry service document).

**Depended on by:** [ADR-020](ADR-020-admin-console-and-service-status.md) — the
console and this directory are now two browsable surfaces, and which one owns
which job needs stating before both grow.

## 9. Revisit triggers

- **A service type is added** and its document does not render, or its breadcrumb
  splits at the wrong segment.
- **The HTML and JSON disagree about any field** — that falsifies the
  render-the-serialised-document rule, and the rule is the whole defence against
  drift.
- **Any encoding escape is found**, at which point convention has failed and the
  answer is a type that cannot be rendered raw, not another test.
- **The console reaches feature parity with this directory**, at which point
  keeping both needs an argument.

## 10. Conditions

1. **No page writes a user-supplied value without the encoding helper**, and the
   test suite covers the text-node and attribute cases separately, because they
   are different escapes. *(Discharged — `RestDirectoryTests`, 5 cases.)*
2. **Every service the catalogue lists resolves.** A directory whose entries 404
   is worse than none: the client reports the server as broken rather than as
   empty. *(Discharged — `Every_listed_service_resolves`, walking all three
   catalogues.)*
3. **The encoding discipline is replaced by something structural** — a type that
   carries "already escaped", or a renderer that cannot be handed a raw string —
   before this surface grows beyond the four document kinds it has. Convention
   held here because there is one helper and one file; neither will stay true.
4. **The relationship with [ADR-020](ADR-020-admin-console-and-service-status.md)
   is stated** before the console ships anything that overlaps this directory.
   Two browsable surfaces with no boundary is how both end up half-built.
5. **The conformance suite's sign-in stays optional and stays off by default**,
   so that "is this resource public?" remains a question the suite can answer.

## 11. Dissent

**Recorded, and not resolved.** The scope argument in §3 is a real one and it was
not defeated on the merits — it was overridden by the owner asking for the
feature directly. A directory is visible progress at a moment when the project's
outstanding commitments (§1 of [CLAUDE.md](../../CLAUDE.md): nine review gates,
twenty-five ADR conditions, A-003 with no validation route) are not visible and
are older. Anyone reading this later should know that the trade was made
knowingly, and that the argument against it was that this is the more pleasant
work.

The counter-evidence is that it found a shipped authentication hole in under an
hour. That is an argument that browsing your own product is a form of review, not
an argument that the scope reasoning was wrong.
