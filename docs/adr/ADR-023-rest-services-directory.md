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
`tests/Graticula.Host.Tests/RestDirectoryTests.cs`. Discipline plus tests is
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

**Opening the page runs nothing; the button runs the query.** The Query link on
the layer document carried `where=1=1&outFields=*&f=json` until the page
existed, so clicking it executed an unfiltered read and printed a wall of JSON.
Reported by the owner as *"I don't see a page when I click query"* and *"it
should query when query is clicked"* — the link now goes to a bare `/query`, and
a request with no filter parameters is a request for the form.

**The server then refused a request its own page generated.** An HTML form
submits every enabled control, including untouched ones, so
`spatialRel=esriSpatialRelIntersects` arrived with an empty `geometry` on every
submission — and a validation rule written for hand-built URLs answered 400.
Every parameter had been tested individually and every one passed; the failure
was in the combination the page itself produces, which is the one combination no
per-parameter test covers. `Pressing_the_query_button_works` now walks the form,
collects what a browser would send, and submits it. The rule was narrowed to
refuse only unambiguous intent — a distance, a relate pattern, or a
relationship that is not the default — since none of those can come from an
untouched form.

**An explicit `f=json` still beats a browser's `Accept` header**, and there is
now a test that sends both together. Every existing caller sends `f=json`; if
the header ever won, the query endpoint would start returning HTML to machines,
and the JSON suite would not catch it because it sends no `Accept` header at all.

### 4c. A browser could not sign in — added 2026-08-15

The owner asked:

> *"why are geometryserver and its capabilities not listed under utilities?"*

`/rest/services/Utilities` was empty in a browser and correct in `curl`. Both
answers came from the same code, and both were right:

```
anonymous  : {"services":[]}
bearer root: {"name":"Utilities/Geometry","type":"GeometryServer"}
```

**Authentication read `Authorization: Bearer` and nothing else.** A browser
following a link cannot set that header, so every page of this directory saw an
anonymous caller — permanently, with no way for the person reading it to become
anybody. The geometry service is shared with the organisation (owner correction,
same day), so it was invisible in the one surface built for browsing. So was
every private and organisation-shared feature service; the geometry service is
simply where it was noticed.

**This defect was created by this ADR.** Before the directory existed, "the
credential is a bearer header" was complete: the only clients were programs.
Adding a browsable surface added a client class that cannot hold one, and
nothing in the decision noticed.

**The fix is a session cookie that authenticates `GET` and `HEAD` only.** It
carries the same opaque token, with `HttpOnly`, `Secure`, `SameSite=Strict` — and
a fourth control that is not a flag: `Authentication.CookieToken` returns null
for any other method. A forged cross-site request can therefore only read, which
is what the directory is for; every mutation still requires the header a browser
cannot be tricked into attaching. That is stronger than an antiforgery token,
because there is no token to get wrong.

**The cost is real and stated rather than discovered later:** an HTML form cannot
`POST` to this server on a cookie. Any future write surface in the browser needs
a deliberate design, not a `<form>` tag. `The_cookie_does_not_authenticate_a_post`
holds the line, and was verified by removing the method check and watching it
fail.

**The prediction came true on 2026-08-17, and the write surface it warned about was the
console.** The owner signed in through this directory's form, went to Server, pressed **Stop**,
and was told *"this needs the `admin:manageServer` privilege and you are not signed in"* — on a
page whose header named them as the administrator. The console's design was already the right
one: it asks for a bearer token and keeps it in `sessionStorage`. What was wrong was one line of
its boot, which gated on *am I authenticated* rather than *do I hold a token* — and with a cookie
`/rest/whoami` answers `authenticated: true` with `admin:manageServer` in the list, so the whole
surface painted and every mutation was refused.

Recorded here rather than only in the console, because **the decision in §4c is not what needs
changing.** The two obvious accommodations are both worse: minting a token from a cookie needs a
`GET` that hands out credentials, which is the request this section exists to refuse; and letting
the cookie authenticate a `POST` behind an antiforgery token gives up the property that there is
no token to get wrong. What the episode shows is that a deliberate asymmetry has to be *visible
to the surface that lives with it*. So the console now says it: a tokenless reader is shown the
form with the reason, and the header says *read-only session* rather than naming a role they
cannot exercise.

**Two things broke on the way, both worth recording.** The sign-in form returned
`415`: the endpoint bound its body from JSON, and a browser cannot post JSON from
a form — so the page this ADR had just added could not sign anybody in. And the
`return` parameter is now filtered by `Safe()`, because a sign-in page that
forwards anywhere is a phishing primitive: our link, our credential prompt,
somebody else's landing page. `//host` is rejected explicitly; it passes a
"starts with a slash" check and is a protocol-relative URL.

### 4d. Capabilities that could not be clicked — added 2026-08-15

The second half of the same question. The GeometryServer document did list its
operations, like this:

```html
<th>Supported Operations</th><td><ul><li>project</li><li>areasAndLengths</li>…
```

Words in a list. ArcGIS renders each operation as a link to a page you can fill
in and run, and that page is how anybody learns what an operation takes. "It is
listed" was technically true and answered nothing.

**Each operation now has a form page**, built from a parameter table beside the
handlers (`GeometryPage.Operations`) — the form's field names are the field names
the handler reads, so a rename breaks a round-trip test rather than drifting
quietly.

**The forms use `GET`, and that is a decision.** Every operation on this surface
is a pure function of its input: nothing is stored, and running one twice differs
from running it once only in the electricity. `GET` is the honest verb, it gives
each answer a URL somebody can keep or paste into a ticket, and — the part that
forced it — it is the only verb the browsing cookie authenticates. A form
posting on a cookie is exactly the shape of request §4c refuses. `POST` is
unchanged for clients with a bearer token and a body too large for a URL, which
is what ArcGIS clients send.

**Following a link shows the form; it does not run the operation.** The layer
query page shipped doing the opposite and had to be corrected
("*the query page queries directly. It shouldn't be that way*"), so the check is
a route-group filter rather than eight handlers each remembering, and the trigger
is the presence of geometry rather than the presence of any parameter — a browser
submits prefilled fields, and keying off "did anything arrive" is the same bug
in a new place.

Refusals render too: a refused operation is reachable by clicking, so `buffer`
answers `501` as a page carrying the measurement and the Q-97 reference, and a
bad parameter re-renders the form with the message above it and **the values
kept** — a refusal that empties the box somebody pasted a polygon into is a
refusal they work around by not using the page.

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

**Amended 2026-08-15 (§4c, §4d).** A browser signs in at `/rest/login`, which
sets a session cookie that **authenticates `GET` and `HEAD` only**; every
mutation still requires `Authorization: Bearer`. Each GeometryServer operation
has a form page at its own URL, listed as a link from the service document and
run with `GET`, because every operation on that surface is a pure function and
because `GET` is the only verb the cookie authenticates.

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
6. **The cookie never becomes a general credential.** It authenticates `GET` and
   `HEAD`, and the day somebody needs a browser to write, that needs its own
   decision — antiforgery tokens, a same-origin check, or a deliberate narrowing
   — not a quiet removal of the method test. *(Held by
   `The_cookie_does_not_authenticate_a_post`, verified by breaking it.)* *(Discharged — `The_cookie_does_not_authenticate_a_post`, which signs in, proves the cookie authenticates a read, then posts the identical request and requires a refusal. Verified by removing the method check and watching it fail alone.)*

7. **A form page exists for every operation the service document links**, and the
   field names on it are the field names the handler reads. Two lists that can
   disagree is a directory that documents an API the server does not have. *(Discharged — `GeometryPage.Operations` is the single list the service document links from and the forms are built from, so a rename breaks both together. `Each_operation_is_a_link` walks all seven; `Following_the_link_shows_a_form_rather_than_running_it` proves the link opens a form rather than running the operation.)*

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
