# ADR-287 — A form is an XLSForm, filled in Graticula's own browser client, online or off

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `LOW` — the direction is the owner's and is not in doubt; nothing of it is built, nothing is measured, and the one claim that decides whether offline works on an iPhone (§4) is a claim about a browser, not about this server |
| **Decided** | 2026-11-08, **by owner decision**, in three answers. Asked whether Survey123 should be supported, the owner said what was meant: *"aslında survey123'nin yaptığı işleri graticula'da yapmak istiyorum"* ("what I actually want is for Graticula to do the work Survey123 does"). Asked how a form is defined, the owner chose **XLSForm** over a format of our own (§2, A and B). Asked where the first version runs, the owner chose **offline from the start** (*"Baştan çevrimdışı da"*) over web-first and over web-only (§2, C–E). Then *"Yaz"* to this ADR. Everything else here is this session's reading of those answers and is marked **INFERRED** in §5.9 and [Q-229](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [v1-scope.md](../v1-scope.md) §2 (a row is added). [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) is **not** amended, and §5.6 says why. |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ArcGIS Survey123 is how an ArcGIS organization collects data from people who are not GIS users. A publisher writes a
form as a spreadsheet or in a web designer. Survey123 publishes a hosted feature layer from it, and people fill it in
a browser or in the field app, online or with no signal. The submissions land as features, with photos as attachments.
The office then reads them as a table, a map and charts, and prints a report per record. Inspections, damage
assessment, public consultation and citizen reports all run on it.

Graticula has no forms (surveyed 2026-11-08 on `origin/main`: no `formInfo`, no XLSForm, no form designer anywhere in
`/src`). It does have most of what a form stands on, because these were built for Survey123 and Field Maps as
clients:

- **an add-only view in front of a private layer** ([ADR-113](ADR-113-a-hosted-layer-has-views.md)), the shape of a
  public survey: anyone may add, nobody may read back;
- **attachments**, added, queried and replaced (`AttachmentEndpoints`);
- **adds by `globalId`** (`GlobalIdEdits`), which makes a retried submission land once;
- **declared relationships** ([ADR-013](ADR-013-feature-service-data-model.md) §3), which a repeat needs;
- **coded-value domains, shared** ([ADR-065](ADR-065-domains-and-subtypes.md), [ADR-087](ADR-087-domains-are-shared.md)),
  which is what a choice list becomes;
- **editor tracking** ([ADR-064](ADR-064-editor-tracking-and-what-features-edit-means.md));
- **app items with resources** ([ADR-264](ADR-264-an-app-or-a-link-is-an-item-and-an-item-keeps-resources.md)), where a
  form definition can live;
- **webhooks** ([ADR-266](ADR-266-a-webhook-tells-another-system-what-changed.md)), Survey123's *tell another system a
  record came in*;
- **public groups and anonymous access** ([ADR-199](ADR-199-a-group-may-be-public.md)).

What is missing are the three parts above that: **a form definition, a client that fills it in, and a screen that reads
the results.**

The owner's question started as *do we support Survey123* and became *Graticula does Survey123's job*. The two are not
the same work. The first is compatibility with Esri's apps, and the server already serves most of what they read.
The second is a product inside Graticula. This ADR is the second. §5.9 (g) records, as INFERRED, that the first is not
a goal of it.

## 2. Alternatives considered

Two questions were put to the owner. Their options are A–B and C–E. F and G are this session's, about how the client
is built, and are INFERRED.

### Alternative A — XLSForm as the form definition (chosen, by the owner)

**Argument for.** XLSForm is a public standard, maintained by the ODK community at xlsform.org, with a public
specification of the XForms it describes. It is not Esri's. Survey123 uses it as its own format, so an organization
leaving ArcGIS brings its forms as the spreadsheets they already are. A spreadsheet is also how many non-programmers
already write forms: one row per question, a column for logic. And the clean-room line ([CLAUDE.md](../../CLAUDE.md)
§5) is easy to keep, because the citation is a public specification and not a product.

**Argument against.** A spreadsheet is a poor round trip for a visual designer. A designer that writes XLSForm has to
preserve columns it does not understand. And XLSForm's expressions are XPath, a language this repository does not yet
evaluate anywhere.

### Alternative B — A JSON form format of our own

**Argument for.** It is simple to start with, it maps to the client's own model with nothing in between, and nothing
outside this repository constrains it.

**Argument against.** No existing form comes with it. The specification, its versioning and its migration path are all
ours to write and keep forever. And an organization leaving Survey123 retypes every form.

### Alternative C — Offline from the start (chosen, by the owner)

**Argument for.** Field collection is where forms matter most, and a field crew without signal is the normal case, not
the edge case. [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) §3 already recorded the cost of not having it: *"v1 is
not for an offline field crew."* A form client designed online-first and made offline later is rewritten, because
offline changes where the state lives.

**Argument against.** It makes the first version several times larger: storage on the device, a submission queue,
retries, sign-in that has expired by the time the signal comes back, and form versions that change while records wait
on a phone. It also brings in a claim about Safari's storage that this project cannot measure without an iPhone (§4).

### Alternative D — Web first, offline second (recommended by this session, not chosen)

**Argument for.** Most Survey123 use is a browser form, often public, and that ships much sooner. Offline would follow
as its own ADR, on a client already in use.

**Argument against.** The owner's: the field case is the point. And the rewrite cost in C's argument.

### Alternative E — Web only

**Argument for.** It is the smallest, and Field Maps already covers field collection against this server.

**Argument against.** Field Maps cannot collect offline against this server either (ADR-082), so *Field Maps covers
it* is not true for the case that matters.

### Alternative F — Adopt an existing XLSForm client (Enketo, ODK Web Forms) — INFERRED, not chosen

**Argument for.** This is the strongest alternative and should be stated as such. Both are Apache-2.0, mature, and
implement XForms evaluation completely, offline included in Enketo's case. Years of edge cases in XPath, repeats and
cascading selects would come for free. Neither is a GIS server, so [build-vs-adopt](../build-vs-adopt-policy.md) Tier 3
does not forbid it.

**Argument against.** The owner's standing requirement behind the policy is *"if a bug appears I want it fixed
in-house"*, and the form client is the product here, not a numerically delicate library: it is Tier 1 by the policy's
own test (§1, *adopting one would mean adopting its architecture*). Both need a JavaScript build pipeline, and
Graticula's browser code is classic scripts with no bundler. Both bring their own UI, which would be a second visual
language inside Studio. And both go through XForms XML as an intermediate step, which a server that reads the
spreadsheet itself does not need.

### Alternative G — A native mobile app — INFERRED, not chosen

**Argument for.** Native apps have unrestricted storage, background sync, and camera and GPS access without browser
prompts.

**Argument against.** It is a second product with two app stores, two codebases or a cross-platform framework, and
releases that are not this server's. A browser client installed to the home screen (a PWA) does everything a form
needs. The one place it is weaker, Safari's storage, is condition 2.

## 3. Counterarguments to the preferred option

**"This is a second product."** It is, and that is the strongest objection. Graticula is a GIS server with a portal.
Survey123 is a form product built on one. The answer is that most of the server half already exists (§1), and that the
owner asked for it in so many words. The cost is real and is in §6.

**"Offline from the start will delay everything else."** It will delay forms. The answer is §5.5's narrow definition
of offline: adds only, no sync. That is a queue, not a replication protocol, and it is the reason this ADR does not
reopen ADR-082.

**"Writing an XPath evaluator is how subtle bugs get in."** True. The answer is condition 4: the evaluator is tested
against the XLSForm and XForms specifications' own examples, and against real public forms, before it is called done.

**"Survey123 forms will not import cleanly anyway."** Partly true. Survey123 adds its own columns and functions
(`bind::esri:fieldType`, `pulldata("@layer", …)`, `body::esri:style`, among others), which Esri documents publicly.
Some of them will be read and some refused. Condition 5 makes refusal explicit, per column, so a form never imports
silently wrong.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Graticula has no form support today | `git grep -i "formInfo\|XLSForm"` over `/src`: no hits, 2026-11-08 | this repository, `origin/main` c08c7f8e |
| The server half exists (§1) | the ADRs and files named in §1 | this repository |
| Attachment `keywords` are always empty | `AttachmentEndpoints.cs` writes `keywords = string.Empty`; Survey123 ties a photo to its question by keyword | this repository |
| A workbook can already be read | `.xlsx` is read through the import reader's XLSX driver ([ADR-112](ADR-112-a-table-with-coordinates-is-published.md) §5) | this repository |
| XLSForm is a public, non-Esri standard | xlsform.org and the ODK XForms specification | public |
| **Safari keeps an installed web app's storage** | **not measured.** WebKit's public statements are that script-writable storage of a site not used for seven days may be cleared, and that a web app added to the home screen is exempt. **Until it is measured on a device, offline on iOS is a claim and not a feature.** | condition 2 |
| Chrome on Android keeps a site's storage when `navigator.storage.persist()` is granted | not measured here; an Android emulator exists ([ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md) condition 5's) | condition 2 |

The decision itself (do this, in XLSForm, offline in the first version) is the owner's product choice and is not a
measurable question. The two device claims are, so they are conditions and not assertions.

## 5. Decision

1. **A form is an item.** A `Form` item ([ADR-264](ADR-264-an-app-or-a-link-is-an-item-and-an-item-keeps-resources.md))
   whose resource is the XLSForm workbook, as uploaded or as the designer wrote it. It is related to the hosted feature
   layer it writes to, and, when public, to that layer's add-only view ([ADR-113](ADR-113-a-hosted-layer-has-views.md)).
   It is shared like any item, and a public form is a form shared with everyone.
2. **Publishing a form makes its layer.** Each question becomes a field of the type it declares. A choice list
   becomes a shared coded-value domain. **A form has at most one location question**, `geopoint`, `geotrace` or
   `geoshape`, which becomes the layer's geometry, and none makes a table. A `repeat` becomes a related table, joined
   by `parentglobalid` through a declared relationship (ADR-013 §3). `image`, `signature`, `audio` and `file` questions
   become attachments, **with the question's name as the attachment's `keywords`**, which ends the empty `keywords`
   in §4. Editor tracking is on. Republishing an edited form changes the layer the way the schema editor does
   ([ADR-058](ADR-058-the-datastore-schema-is-edited-from-the-screen.md)), adding fields and never silently dropping
   one with data in it.
3. **The workbook is read on the server**, through the import reader that already reads `.xlsx`. The sheets are
   `survey`, `choices` and `settings`. What the server cannot honour is refused at upload with the row and column
   named, never ignored (condition 5).
4. **The client is Graticula's own.** It is a page under `/forms/{itemId}`, written as this repository writes its other
   browser code, with an XLSForm expression evaluator of its own, and installable to the home screen as a web app
   with a service worker. The first version covers the question types and logic in condition 3's list: `relevant`,
   `constraint`, `required`, `calculation`, `default`, `readonly`, `choice_filter`, groups, repeats, and multilingual
   labels (`label::Türkçe`). Submission is `applyEdits` with `globalId`s, followed by `addAttachment` per file.
5. **Offline means: the form, its lists and its basemap are kept on the device, and submissions wait in a queue.** On
   first open, or on *Download for offline use*, the client keeps the form definition, the choice lists and, when the
   form has a map, a vector tile package of the area through the existing `exportTiles`
   ([ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md)). A submission made without signal is stored on the
   device with its files and sent when the signal returns, oldest first. Because every record carries its own
   `globalId`, a submission that was half sent is sent again and lands once. A queued record is never deleted until
   the server has accepted it.
6. **This is not ADR-082's sync, and ADR-082 stands.** Sync, in ADR-082, is the replica protocol: a device holding a
   copy of the layer, and edits flowing both ways with conflict rules. A form queue only adds new rows, which cannot
   conflict with anything online. Editing an existing record (Survey123's *Inbox*) is **online only** in the first
   version (§5.9 (e)). An offline inbox would be a downloaded copy, and that would reopen ADR-082.
7. **The results screen is in Studio.** On the form's item page: the submissions as a table and on a map, a summary
   per question (counts for choices, ranges for numbers), filtering by question, export through the existing layer
   export ([ADR-107](ADR-107-a-layer-is-exported-as-geopackage-shapefile-or-workbook.md)), and the webhooks
   ([ADR-266](ADR-266-a-webhook-tells-another-system-what-changed.md)) already offered on the layer.
8. **A designer in Studio writes XLSForm.** It is a visual editor for the common cases. It reads and writes the same
   workbook, keeps columns it does not understand untouched, and says so on screen when a form uses something it cannot
   edit. There is one format, never a second one behind the designer.
9. **INFERRED, for the owner to confirm** (listed as [Q-229](../open-questions.md)):
   - (a) the client is built here and Enketo or ODK Web Forms is not adopted (§2 F);
   - (b) the first version's question types and functions are condition 3's list, and anything else is refused at
     upload rather than half supported;
   - (c) Esri's public XLSForm columns are read where they map onto something here (`bind::esri:fieldType`,
     `bind::esri:fieldLength`, `bind::esri:fieldAlias`), and the rest are refused by name;
   - (d) a repeat is a related table through `parentglobalid`, as Survey123 lays it out, so data leaving for or
     arriving from Survey123 keeps its shape;
   - (e) editing existing records is online only;
   - (f) **when a form changes while records wait on a phone**, a queued record is sent as filled; a field it carries
     that no longer exists is refused by the server, and the record is kept on the device, marked, and shown to its
     author rather than lost or silently trimmed;
   - (g) Esri's Survey123 apps opening Graticula's forms is **not** a goal;
   - (h) a report per record (Survey123's *feature report*) is a later ADR, built on the print service
     ([ADR-265](ADR-265-the-server-prints-a-web-map.md)) and not on Word templates;
   - (i) the server checks what a database can check (types, lengths, domains, required fields that are not
     conditional) and does **not** re-evaluate the form's XPath logic. A second evaluator in C# would drift from the
     browser's, and a hostile client could skip the logic anyway, so logic is a usability contract and the schema is
     the integrity contract.

## 6. Consequences

**Positive.** An organization can collect data with Graticula alone, from the public or from a field crew without
signal, and read it in Studio. Forms written for Survey123 come with it, as far as condition 5's list reaches. The
layers a form makes are ordinary hosted layers, so everything already built applies to them: views, sharing, history,
export, webhooks, Pro and Field Maps.

**Negative.**
- **Forms are now the largest unbuilt surface in the product**: an expression evaluator, an offline client, a
  designer and a results screen. Each will be the least tested part of the server until condition 4 is met.
- **Offline on iOS depends on Safari**, which is not under this project's control. If condition 2 fails, iPhone users
  are told to install the web app, and an iPhone that has not done so does not keep a queue, and is told so before any
  record is filled.
- A Survey123 form that uses what is refused has to be edited before it imports. The refusal names the row, but
  someone has to do the work.
- **Offline inbox is out**, and an organization whose crews edit existing records in the field still cannot do that
  offline (ADR-082's cost, which this ADR narrows but does not remove).
- The browser client takes a dependency on service workers and IndexedDB, which this repository has not used before.

**State.** No new table is promised here. The form is an item and an item resource; its layer, domains, relationship
and view are those that already exist. The device holds the queue; the server holds nothing per device.

**Ports created.** None. Reading the workbook goes through the import reader's existing seam.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-088 | A field crew can install the form client to the home screen, or uses Android, so that the device keeps its queue | `UNVALIDATED` — new with this ADR; condition 2 |
| A-089 | Most forms an organization brings from Survey123 use the common XLSForm subset of condition 3 rather than Esri-only functions | `UNVALIDATED` — new with this ADR; condition 5 counts it |

## 8. Dependencies

**Depends on**: [ADR-013](ADR-013-feature-service-data-model.md) (relationships, attachments),
[ADR-058](ADR-058-the-datastore-schema-is-edited-from-the-screen.md) (republishing),
[ADR-064](ADR-064-editor-tracking-and-what-features-edit-means.md), [ADR-065](ADR-065-domains-and-subtypes.md) and
[ADR-087](ADR-087-domains-are-shared.md) (choice lists), [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md)
(offline basemap), [ADR-112](ADR-112-a-table-with-coordinates-is-published.md) (reading `.xlsx`),
[ADR-113](ADR-113-a-hosted-layer-has-views.md) (add-only views), [ADR-199](ADR-199-a-group-may-be-public.md),
[ADR-264](ADR-264-an-app-or-a-link-is-an-item-and-an-item-keeps-resources.md) (items),
[ADR-266](ADR-266-a-webhook-tells-another-system-what-changed.md). It leaves
[ADR-082](ADR-082-offline-sync-is-not-in-v1.md) standing (§5.6).

**Depended on by**: the feature-report ADR, when written (§5.9 (h)).

## 9. Conditions

1. **The owner confirms §5.9's INFERRED points**, (a) to (i), through [Q-229](../open-questions.md).
2. **Offline is measured on a device before it is called supported.** On Android in Chrome (the emulator is enough):
   fill ten records with photos in flight mode, close the browser, restart the device, restore the signal, and every
   record arrives once. On an iPhone in Safari, installed to the home screen: the same, and the queue survives eight
   days unopened. The iPhone half needs a device the owner supplies; until then the client says *offline on iPhone
   is not yet verified* where it offers offline use.
3. **The first version's list is written down** in this ADR before the client is built: question types, functions
   and appearances, each one either supported or refused. That list is what the evaluator is tested against and what
   condition 5 refuses by.
4. **The expression evaluator is tested against the specifications' own examples** (xlsform.org's and the XForms
   specification's) and against at least twenty public XLSForms, before forms are released. A form whose logic
   evaluates differently from its specification is a defect, not a limitation.
5. **Every refusal at upload names the row, the column and the reason**, and a set of real Survey123 forms is
   imported to count how many come in unedited. A-089 is validated or invalidated by that count.

## 10. Revisit triggers

- Condition 2 fails on iOS and Safari offers no remedy: then iPhone offline is either dropped or moves to a native
  shell, by a new ADR.
- An organization needs to edit existing records offline: that reopens [ADR-082](ADR-082-offline-sync-is-not-in-v1.md),
  not this ADR.
- The evaluator's defect count stays above what condition 4 tolerates for more than a release: then §2 F (adopt) is
  reconsidered.
- XLSForm's specification makes a change the client cannot follow.

## 11. Dissent

**This session recommended web first (§2 D), and the owner chose offline from the start.** The recommendation is
recorded, not withdrawn. Its argument was time to first use; the owner's was that the field case is the point. The
owner's choice is the decision, and §5.5's narrow definition of offline is how this ADR tries to keep its cost within
reach.
