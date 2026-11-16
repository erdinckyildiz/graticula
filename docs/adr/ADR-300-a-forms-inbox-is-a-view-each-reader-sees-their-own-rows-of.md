# ADR-300 — A form's Inbox is a view, and each reader sees their own rows of it

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the shape is the owner's (Q-232 (a), 2026-11-09) and the mechanism is one this server already runs for tenancy; the details in §5.9 are this session's and are listed for the owner to confirm, and nothing of it has met a device |
| **Decided** | 2026-11-16. The owner chose **a view** for the Inbox on 2026-11-09 ([Q-232](../open-questions.md) (a), *"Survey123's fieldworker view"*), against this session's recommendation of a route through the form, and on 2026-11-16 asked for it to be built (*"Inbox'ı yapalım"*). Q-232 said the design goes into its own ADR before the code; this is it. |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-287](ADR-287-a-form-is-an-xlsform-filled-online-or-off.md) §5.6 (the Inbox is built as this says), [ADR-113](ADR-113-a-hosted-layer-has-views.md) §5.2 (a view's filter may name its reader), [ADR-115](ADR-115-editors-may-be-kept-to-their-own-features.md) §9 (reading only one's own rows, its revisit trigger, arrives — for views only) |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Survey123 does two things with a form. It collects new records, which Graticula's forms do since v1.0.520
([ADR-287](ADR-287-a-form-is-an-xlsform-filled-online-or-off.md)), and it lets a field worker **open records that
already exist and change them** — an inspection due, a sample to finish, yesterday's submission to correct. Survey123
calls the list of those records the *Inbox*. Where a form is used for inspections and maintenance it is half of the
work, and it is the largest thing Graticula's forms do not do.

What Esri documents of it (read 2026-11-16, *Prepare for editing existing survey data* and *Edit existing survey data*,
for the field app and the web app):

- The publisher turns the Inbox on and writes **a SQL `where` over the layer** that selects its records. The one variable
  it may use is **`${username}`**, the signed-in member: `Creator=${username}` gives each worker the records they made,
  `POSITION(${username} IN assigned_to) > 0` the ones assigned to them. A filter without the variable gives everybody the
  same records.
- *Refresh* downloads the records to the device, at most 5,000 parents; a spatial filter can limit them to the map's
  extent. Records are then opened, changed and sent with or without signal.
- Calculations run again when a record opens, except on answers the worker overwrote.
- A repeat's existing rows are not deleted; new ones are added by default, and existing ones changed only where the
  publisher allowed it (`allowUpdates=true` in `bind::esri:parameters`).
- Images may be added; existing images, files and audio are not edited.
- Esri says that **turning the Inbox on gives its readers read access to the data**, and its own answer to that is the
  *fieldworker view*: a view of the layer, shared with the workers, that limits what they read.
- Esri documents nothing about two edits of the same record. Graticula's rule is the owner's (ADR-287 §5.9 (j)): **the
  later one is refused and the device shows both**.

Q-232 found the problem in the way of this on 2026-11-09. A form's layer is private to its publisher, and a worker who
fills the form reaches it only through the answers view, which offers Create alone. To download and change records a
worker needs a read and an update, and **this server cannot let a reader see only their own rows**: ADR-115 keeps
editors to their own rows for update and delete, and refuses `allowOthersToQuery=false` with *"whoever may read the
layer queries all of it."* The owner chose to solve it the way ArcGIS does, with a view, and accepted what that costs:
a rule about who reads which rows, new on this server, which has to hold on **every** face that reads the view, not on
some — D-180's lesson, and the one ADR-113's attachment hole repaired in v1.0.521 taught again the same day this was
written.

## 2. Alternatives considered

### Alternative A — A view whose filter the database applies to whoever is reading (chosen)

The Inbox is a hosted view of the form's layer (ADR-113) with Query and Update. Its PostgreSQL view's `where` is the
publisher's Inbox filter, and where that names `${username}` the database reads the name from a setting this server
makes on every connection it uses for a request — the mechanism tenancy already uses on the platform store
(ADR-204, `graticula.tenant`). Every face that reads the view reads its relation by name, so every face gets the filter
from one place.

**Argument for.** One rule, kept by the database, which no face can forget: the query, the count, the ids, the
statistics, related records and the writer all already read the view by name (surveyed 2026-11-16). It is the owner's
choice and it is what ArcGIS does, so a publisher who knows the fieldworker view knows this. An unset name matches
nothing, so a path that forgets to set it fails closed.

**Argument against.** Some faces do not read the relation per caller: tiles are cached per layer, a layer's extent and
thumbnail are cached per layer, a query answer may be cached by HTTP, and background jobs run without a caller. Each
would serve one reader's rows to the next. They have to be found and closed one by one, which is exactly the cost
Q-232 named.

### Alternative B — A route through the form (recommended on 2026-11-09, not chosen)

`GET /forms/{id}/inbox` and `PUT /forms/{id}/inbox/{layer}/{globalid}` with `If-Match`, the server applying the filter.

**Argument for.** One narrow route instead of a rule every face must keep.

**Argument against.** Not what the owner chose; nothing an ArcGIS client could ever use; a second write path.

### Alternative C — The filter in each face

**Argument against.** Every face is a place to forget it. Rejected for the reason A is chosen.

## 3. Counterarguments to the preferred option

- **A setting on a pooled connection can leak to the next request.** Tenancy already relies on Npgsql resetting a
  connection returned to its pool, and the setting is sent with each command rather than trusted from the connection.
  The same is done here, and the tests read through one pool as two members in turn.
- **"Every face" is a claim that is wrong until each face is asked.** §5.4 lists the faces, and condition 1 asks each of
  them as two members, as AFormIsPublishedAndFilledInTests asks each face of the answers view.
- **Anonymous callers share one name**, `anonymous`. An Inbox for anonymous callers would be everyone's Inbox; §5.1
  refuses it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The Inbox's filter is a SQL `where` whose only variable is `${username}`; 5,000 parents; calculations rerun unless overwritten; repeats keep existing rows, `allowAdds`/`allowUpdates` | Esri, *Prepare for editing existing survey data* and *Edit existing survey data* (field app, web app), read 2026-11-16 | publicly documented |
| Every face but attachments, tiles and the cached extent/thumbnail reads a view by its relation | code survey 2026-11-16, recorded in this session | this repository |
| Attachments read past a view's filter until v1.0.521 | AFormIsPublishedAndFilledInTests failed on v1.0.520, passed on v1.0.521 | this repository |
| A per-command setting survives pooling correctly | tenancy's `graticula.tenant`, ADR-204, in production since v1.0.4xx | this repository |
| The writer refuses a stale update atomically, in the update's `where` | OGC face's 412, D-186 | this repository |
| The per-statement name costs no meaningful throughput on a layer that is not a view | 2026-11-16, Release builds of `c378e7c2` (before) and `inbox-server` (after) on one machine, the same fixture: 5 alternating rounds of 2,000 `query?where=1=1&resultRecordCount=10` on `hosted/ci_parcels`, 4 keep-alive clients, logging at Warning. Medians — signed in: 523.7 → 522.7 req/s, p50 7.28 → 7.45 ms; anonymous: 711.1 → 691.6 req/s, p50 5.56 → 5.70 ms. Round-to-round spread on the same build was larger (anonymous before: 647.6–813.1) | measured, condition 3 |
| A leading `SET` does not shift a streamed reader's result set; a pooled connection does not carry one member's name to the next | `ReaderSessionTests`, PostgreSQL 16, Npgsql 9, one connection reused by two members and nobody | measured |

## 5. Decision

1. **A form may have an Inbox, turned on by its publisher in Studio, for signed-in members only.** It has a filter, a
   `where` over the form's layer in Survey123's spelling, which may name `${username}`; **the default filter is
   `created_user = ${username}`**, each member's own submissions, which is the owner's *"readers see only the rows they
   sent"*. A record limit, per form. Publishing a form with an Inbox makes, beside the answers view, an **Inbox view**:
   `<service>_inbox`, Query and Update, no Create, no Delete, shared as the form is shared, **never with everyone** —
   an anonymous caller has no name, so the Inbox is refused to anonymous callers and a public form's Inbox is shared with
   the organisation.
2. **`${username}` is the database's to fill.** The filter is parsed by the layer's own `where` parser with the variable
   standing as a marked literal, so it accepts and refuses what a view filter does; in the view's DDL that literal
   becomes `current_setting('graticula.username', true)`. The view's rows are therefore the reader's own on every face
   that reads the view by name, and **none when no name is set**.
3. **The name is set on every command, not on the connection.** `LayerConnections` wraps its pools as
   `TenantDataSources` wraps the platform's: an `AsyncLocal` carries the request's qualified member name, set by the
   authentication middleware for a signed-in member and left empty otherwise, and every command opened through the pools
   carries it. Background work has no request and therefore no name, and sees no Inbox rows.
4. **The faces that do not read per caller are closed for a view whose filter names its reader** (a *per-reader view*):
   - **tiles**: off, as for the answers view (Q-231): the ceiling allows no tiles, no tile item, no seeding, no export;
   - **the layer document's extent**: not stated, and the time extent likewise;
   - **thumbnails**: the source's, never drawn from the view;
   - **HTTP caching**: `private, no-store` on every answer;
   - **extract/export, analysis and toolbox inputs, replicas and map areas**: refused, as sync already is on views;
   - **related records**: a repeat's Inbox layer is a view over the repeat's table filtered to rows whose parent the
     parent's Inbox view shows, so a repeat cannot be read past its parent;
   - **attachments**: read through the view since v1.0.521, which with this filter is the reader's own.
5. **Conflicts are refused in the write, by the row's EditDate.** A record downloaded to the Inbox carries
   `last_edited_date`. Graticula's form client sends its updates through the Inbox view's `applyEdits` with one
   Graticula parameter, `expectedEditDates` — `{objectId: epoch milliseconds}` — and the writer adds
   `and date_trunc('milliseconds', last_edited_date) = @expected` to that row's update. A row that moved is refused as
   stale (the OGC face's 412 result, `EditResult.Stale`); a row that is gone is refused as missing. Neither is ever
   applied. An ArcGIS client that does not send the parameter edits as today.
6. **The client.** The fill page shows an *Inbox* beside the new-record form when the form has one and the member is
   signed in. *Refresh* downloads the records the view gives, up to the limit, with their repeat rows and their
   attachments' names, and keeps them on the device. Opening a record fills the form with its answers; calculations run
   again except on answers changed by hand. Sending queues the record with the new-record queue, oldest first. A stale
   refusal fetches the server's version and shows both, field by field, and the member keeps theirs (sent again against
   the new EditDate) or takes the server's. A missing refusal keeps the record on the device, marked, and shown.
7. **Repeats follow Survey123:** new repeat rows are added; existing rows are changed only where `allowUpdates=true`;
   existing rows are never deleted from the Inbox.
8. **Images may be added to a record from the Inbox; existing attachments are shown online and not changed.**
9. **INFERRED, for the owner to confirm** (as [Q-235](../open-questions.md)):
   - (a) the default filter is `created_user = ${username}`; the publisher may write any other, including one without
     `${username}`, which shows every reader the same rows, as Survey123 allows;
   - (b) a public form's Inbox is shared with the organisation, since an anonymous caller cannot have one;
   - (c) the default limit is 1,000 records until it is measured on a device (ADR-287 §5.9 (k) left it to the
     measurement; Survey123's is 5,000);
   - (d) conflicts by EditDate to the millisecond, not by the row's `xmin`, because the record already carries it and
     ADR-287 §5.6 said so; two edits in one millisecond are not told apart;
   - (e) the spatial filter (*only within the map extent*) is not built in the first version;
   - (f) the Inbox's settings live in the form's model, not in the XLSForm, as Survey123 keeps them in Connect and not in
     the workbook.

10. **Built 2026-11-16** (branch `inbox-server`, the server half; the client half is the client's). What was built beyond
    the words above, each choice that is this session's marked **INFERRED** and listed with Q-235 for the owner:
    - **(a) The name on every statement.** `ReaderSession` (Providers.PostGis): an `AsyncLocal` the authentication
      middleware sets to `RequestPrincipal.QualifiedName` for a signed-in member and to nothing otherwise. A
      connectionless command carries `SET graticula.username = '…';` in its own batch — **always, the empty name
      included**, so a statement never runs under a name it did not set; a connection opened from a layer pool runs the
      `SET` before it is handed back, only when there is a name (a round trip), relying on the reset. **Reset on close is
      forced on both layer pools** (`LayerConnections.ResetOnClose`): a registered connection string may say
      `No Reset On Close=true`, which would have handed one member's name to the next request. Every statement in
      PostGisFeatureSource, -Writer, -RelatedRecords, -AttachmentStore, -FeatureHistory, -TileSource and the importer's
      `OpenAsync` goes through it; the projector and the reference checks use positional parameters (a second statement
      in their message is refused by PostgreSQL) and read `spatial_ref_sys`, so they stay on the pool.
      **`PostGisFeatureSource`'s note that a leading `SET` shifts the result set was measured, not trusted:** Npgsql's
      reader passes over a statement that returns no rows, streamed and sequential alike (`ReaderSessionTests`).
    - **(b) `${username}`** is read on **every** ADR-113 view filter, not only the Inbox's — a Studio view may be
      per-reader too, as Survey123's fieldworker views are. Bare or quoted; inside a longer literal it is refused. The
      DDL reads `nullif(current_setting('graticula.username', true), '')`, so an empty name is no name and matches
      nothing (`created_user = ''` would otherwise match rows written anonymously). **INFERRED.**
    - **(c) Per-reader is derived from the recorded filter** (`PublishedLayer.IsPerReader`: a view whose definition holds
      `${username}`), not a flag, so no migration and the record cannot disagree with the rule. A repeat's Inbox layer
      records what its view does — `parentglobalid in (globalid of the rows layer 0 shows to ${username})` — and is
      therefore per-reader too. **INFERRED.** That text does not parse as a filter; a publisher who edits it in Studio is
      refused, which is the right answer for a table whose rows follow its parent.
    - **(d) §5.4 said thumbnails are "the source's": wrong, corrected.** The source's picture is every reader's rows. A
      per-reader view has no thumbnail (404, the placeholder), from a request and from the warmer.
    - **(e) The time extent** is not measured for a per-reader view, and — the same leak, found beside it — not for a
      layer whose ceiling refuses Query either: the dimension is stated with its span unknown.
    - **(f) Tiles** are refused twice: the view is made with its tile switch off, and `VectorTileEndpoints.Tileable`
      answers false for any per-reader layer, so a view whose filter is later changed to name its reader stops tiling
      with nothing to remember (and `SetViewDefinitionAsync` turns the switch off then too).
    - **(g) The Inbox offers Query, Create and Update — not Delete.** §5.1 said "no Create"; changed on the client
      half's finding that §5.7 adds repeat rows and §5.8 adds pictures through the Inbox. **INFERRED: Create on the
      top layer too** — a member may add a new record through the Inbox, which they could through the form anyway.
    - **(h) `WITH CASCADED CHECK OPTION`** on every per-reader view and on each repeat of an Inbox: a row added or
      changed through the view must be one the view shows its writer. So a repeat row is added only under a record the
      reader's Inbox shows, and no edit takes a record out of one's own Inbox. **INFERRED; consequence:** with a filter
      on a column the member does not write (`assigned_to = ${username}`), a new top record through the Inbox is refused
      unless it assigns itself — Survey123's own Inbox does not add top records either.
    - **(i) Who may write through it** (`Authorize.UpdatesThroughView`): a per-reader view offering Update takes updates —
      and an attachment's add — from any **signed-in** member who can read it; adds ride on Create as ADR-287 §5.11 (l)
      already made them. A plain ADR-113 view keeps ADR-075's rule. Deletes never.
    - **(j) Settings are an item resource, `graticula/inbox.json`** (`{enabled, filter, limit}`), beside the publication
      rather than in it or in the model, because a form not yet published has no publication and the Inbox may be
      turned on before. **INFERRED** (§5.9 (f) said the model). `PUT` validates the filter against the published top
      layer, or — unpublished — the layer publishing would make, or the `submission_url` layer, which must be a hosted
      layer its publisher manages; 400 with the parser's words.
    - **(k) The view is made again, never patched:** on enabling, on every publish (a view's columns are fixed when it is
      made, ADR-113 §3), and on every change of the form's sharing. It holds no rows, so remaking loses nothing; between
      drop and make an Inbox refresh fails and is retried. Turning it off drops it.
    - **(l) Relationships between the Inbox's own layers** are declared, so `queryRelatedRecords` works on the Inbox;
      each repeat's publication layer gains **`inboxRelationshipId`** beside `inboxUrl` — an addition to the contract.
    - **(m) The Inbox is filed** in the form's `Survey-<title>` folder as `<title>_inbox`. **INFERRED.**
    - **(n) `expectedEditDates`** is compared by a **layer's** `applyEdits` only; `updateFeatures` and a service's
      `applyEdits` refuse it (400) rather than ignore it. Keys are object ids or, with `useGlobalIds=true`, GlobalIDs;
      a `null` value expects a record never dated. When it is sent: a moved record answers `412`, a missing one `404`,
      each with `globalId`; with `useGlobalIds=true` a GlobalID nothing carries is a `404` entry (`objectId: -1`), not
      the request's 400. **A refused update still rolls the whole request back under `rollbackOnFailure=true`** (the
      default): a client sending several records should send one per request, or `rollbackOnFailure=false`.
    - **(o) An administrator reading the Inbox sees their own rows** — none, usually. The view is per reader for
      everybody; the source layer is the administrator's.
    - **(p) Refused as inputs:** feature export (`data-exports` and the synchronous layer export), analysis and toolbox
      inputs. Replicas and map areas were already refused for views (`SyncFlags`). **Not refused, and failing closed
      instead:** a scene layer rebuilt from a per-reader view and a print job run in the background read nobody's rows.
    - **(q) Repeat parameters:** `bind::esri:parameters` on `begin repeat` takes `allowAdds` and `allowUpdates`
      (defaults true and false, Survey123's), refuses `query` with its reason and any other key by name; `form.json`
      carries `parameters: {allowAdds, allowUpdates}` on every table.
/^>>>>>>> inbox-client$/d
11. **Client, built 2026-11-16** (branch `inbox-client`, built beside the server half and against its contract; nothing
    here has met the real server or a phone). Tests: `FormInboxClientTests`, eight, in a real Chrome against the
    stand-in Inbox view of `FormFillClientTests.Site`, which answers `expectedEditDates` with 412 and 404 as §5.5 says.
    - **The fill page.** An *Inbox* button beside Drafts and Outbox, shown only when `form.json` carries
      `publication.inbox` and a top layer with `inboxUrl`, and the member is signed in — or the server cannot be reached
      to ask, since the server sends `inbox` only to a signed-in member. Its panel: *Refresh*, a status line (a live
      region), the time of the last refresh, a search box, a count, and the records with *Open*. Refresh queries the
      top layer's `inboxUrl` (`where=1=1`, `resultRecordCount=` the limit, geometry in 4326), each repeat's `inboxUrl` with
      `parentglobalid in (…)` a hundred parents at a time and every page of each, and `queryAttachments` for every layer
      with attachments. The records are kept in a fourth IndexedDB store, `inbox` (database version 3), one entry per
      record keyed `form|member|GlobalID` with its repeat rows and attachment names, and one per form and member saying
      when it was refreshed; the list, search and opening work from it offline.
    - **Opening a record** builds the form's instances from the rows: answers from columns (dates and date-times from
      epoch milliseconds, the location from the geometry), repeats from their rows, no defaults and no empty repeat item.
      Nothing is marked as changed by hand, so calculations run again exactly as §5.20's `calculationMode` rules say for
      an answer that was not typed; one the person then changes is kept. `pulldata("@property", 'mode')` is `edit`.
      Existing attachments are listed under their question (linked and shown when online), without Remove; new files are
      added beside them. A line above the form says a record from the Inbox is open.
    - **Repeats.** A row the server holds has no Remove, and a `repeat_count` never takes it away. Its answers are
      read-only unless its repeat says `allowUpdates=true`; new rows are added unless it says `allowAdds=false`.
    - **Sending.** The change is queued in the same outbox as new records, oldest first, as a record of kind `edit`: per
      layer, `updates` (the row's object id and GlobalID, every column of a relevant question, a cleared answer sent as
      `null`) through `applyEdits` on the layer's `inboxUrl` with `useGlobalIds=true`, `rollbackOnFailure=false` and
      `expectedEditDates` keyed by object id; then new repeat rows as `adds` by the existing new-record path; then new
      files through `addAttachment` on the `inboxUrl`, without `parentGlobalId`. The top row is always sent; a repeat row
      only when it allows updates and its answers changed.
    - **412.** The rows refused as stale are read again. A row whose server version already equals what was sent is an
      earlier attempt that arrived, and is done. Otherwise the record waits in the outbox as *changed on the server*, and
      the comparison opens: per row, a table of question, *Yours*, *On the server*, each differing answer marked in words
      as well as colour, who changed it and when. *Keep mine* sends it again against the server's new EditDate; *Use the
      server's* (confirmed) drops those rows' updates and sends whatever else the record holds. The device's copy of the
      record is read again after either.
    - **404.** The record stays in the outbox marked *no longer on the server*, and its entry stays in the Inbox, marked,
      through later refreshes; *Show answers* lists every answer with its question and *Copy answers* puts them on the
      clipboard (or selects them where the browser will not). Only the person's *Discard* removes it.
    - **Studio.** On the form's item page, under its state, an *Inbox* section: on/off, the filter (hint with
      `${username}` and both Survey123 examples — the `where` parser takes `POSITION(a IN b)`, so it needs no caveat), the
      limit 1–5,000 and *Save*, the server's 400 shown beside the field it is about with focus moved there.
    - **INFERRED, for the owner to confirm:** (1) a record whose change still waits in the outbox cannot be opened again
      until that change is sent or discarded, because a second change would be sent against the same EditDate and refused;
      (2) a draft of an Inbox change is kept under the record's GlobalID and reopened when the record is opened again;
      (3) *Use the server's* keeps the person's new repeat rows and pictures and sends them, since dropping them silently
      would lose an edit nobody compared; (4) any 404 in a change stops the whole record rather than sending the rest;
      (5) the list shows at most 200 records at a time, newest change first, and the search narrows it; (6) a record's
      label is the form's `instance_name`, else its first two answers that read as words, with the last change's date and
      editor beneath; (7) attachments are matched to their question by `keywords`, which this client sends, and one media
      question takes those with none; (8) a refresh drops records the server no longer gives, except one found gone or
      with a change waiting; (9) the Inbox's records are kept per member on a shared device, and offline the member who
      refreshed last is assumed; (10) the client reads a repeat's `allowUpdates`/`allowAdds` from the repeat's
      `parameters` in `form.json` (or a boolean of that name) — **the server does not send them yet**: the checker refuses
      every `bind::esri:parameters` key but `calculationMode`, and the compiled repeat carries no parameters, so until the
      server half carries them every existing repeat row opens read-only, which is Survey123's default.
    - **Not verified without the real server:** the `inboxUrl` paths and the answer shapes of the view (field names
      `objectid`, `globalid`, `parentglobalid`, `last_edited_date`, `last_edited_user`; `updateResults` carrying
      `globalId`); `queryAttachments` on a view; whether an `adds` of repeat rows and `addAttachment` are accepted through
      a view that offers no Create; attachment images loading with the session cookie; offline on a phone.
>>>>>>> inbox-client

## 6. Consequences

**Positive.** The largest gap between Graticula's forms and Survey123 closes, through the arrangement an ArcGIS
publisher already knows. Reading only one's own rows becomes possible on this server, kept in one place.

**Negative.** A new per-request setting on every layer command — measured, condition 3. Per-reader views give up
tiles, cached extents and HTTP caching. A second parameter on `applyEdits` that only Graticula's client sends.

**State.** In the catalogue: the form's Inbox settings (on, filter, limit) in its model, and the Inbox view as
ADR-113 records any view. At runtime: the request's member name in an `AsyncLocal`, per request, node-local and
never shared. On the device: the downloaded records, in the form client's own store.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Npgsql resets a pooled connection's session settings, as tenancy assumes | relied on since ADR-204 |

## 8. Dependencies

**Depends on:** ADR-113, ADR-115, ADR-204, ADR-287, D-186.

**Depended on by:** —

## 9. Conditions

1. **Every face of a per-reader view is asked as two members**, each seeing their own rows and not the other's: query,
   count, ids, statistics, renderer, related records, attachments (list, query, download, add), OGC API Features, WFS,
   WMS/MapServer, tiles, the documents' extent, and an export or analysis job naming the view.
   **DISCHARGED 2026-11-16:** `AFormsInboxShowsEachMemberTheirOwnTests.Each_member_reads_their_own_rows_on_every_face_of_the_Inbox_and_nobody_else_does`
   asks each of them as each of two members, and anonymously; MapServer is asked by its query and `identify` (WMS draws
   through the same source per request and was not asked separately). The thumbnail, the HTTP caching and the
   Inbox's update and conflict rules are asked there and in its two sibling tests.
2. **A conflict is tested as ADR-287 condition 6 says**: the same record changed in Studio and on a device that is
   offline; the device's edit is refused and both versions are shown; a record deleted online while edited offline is
   kept on the device. Neither loses an edit silently.
3. **The per-command setting is measured**: the query benchmark before and after, on a layer that is not a view.
   **DISCHARGED 2026-11-16:** §4, *the per-statement name costs no meaningful throughput* — medians within 3% (signed in
   −0.2%, anonymous −2.7%, p50 +0.15 ms), inside the spread between rounds of the same build. Measured on a shared
   development machine, not a quiet one; a regression smaller than that spread would not show.
4. **The owner confirms §5.9** through Q-235.

## 10. Revisit triggers

- A face added later that reads a layer without its relation, or caches per layer.
- An ArcGIS client (Field Maps, Survey123) is to use the Inbox view: it sends no `expectedEditDates`.

## 11. Dissent

This session recommended a route through the form on 2026-11-09, and the cost it named is the one §5.4 pays. Recorded,
not reopened: the owner decided.
