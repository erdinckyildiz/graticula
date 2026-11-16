# ADR-287 — A form is an XLSForm, filled in Graticula's own browser client, online or off

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `LOW` — the direction and every open choice are the owner's (§5.9); nothing of it is built, nothing is measured, and the one claim that decides whether offline works on an iPhone (§4) is a claim about a browser, not about this server |
| **Decided** | 2026-11-08, **by owner decision**, in three answers. Asked whether Survey123 should be supported, the owner said what was meant: *"aslında survey123'nin yaptığı işleri graticula'da yapmak istiyorum"* ("what I actually want is for Graticula to do the work Survey123 does"). Asked how a form is defined, the owner chose **XLSForm** over a format of our own (§2, A and B). Asked where the first version runs, the owner chose **offline from the start** (*"Baştan çevrimdışı da"*) over web-first and over web-only (§2, C–E). Then *"Yaz"* to this ADR. **Two of §5.9's points were answered the same day:** the client is built here (*"Kendimiz yazalım"*, (a)), and **existing records are edited offline too** (*"Çevrimdışı da olsun"*, (e)), against this session's recommendation of online only. The second reverses this ADR's first §5.6 and amends [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) in part. **Also on 2026-11-09: a location inside a repeat makes that repeat a related layer** (*"İlişkili katman olsun"*, §5.2). **On 2026-11-09 the owner asked again whether a JSON format of our own would have been better**, was given the answer that the hard part is the expression language and not the container, and chose the combination this session proposed (*"evet böyle olsun"*): **the stored form is a JSON model that is a lossless copy of the XLSForm's rows and columns, the workbook is the import and export format, and the expressions stay XLSForm's** (§2 H, §5.1, §5.3, §5.8). What this session read into those answers was marked **INFERRED** in §5.9 and listed as [Q-229](../open-questions.md); **the owner answered all of it on 2026-11-09**, and nothing in this ADR is INFERRED any more. |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [v1-scope.md](../v1-scope.md) §2 (a row is added). [ADR-082](ADR-082-offline-sync-is-not-in-v1.md), **in part** (§5.6): Graticula's own forms edit existing records offline; ArcGIS's replica protocol stays out. |

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
The second is a product inside Graticula. This ADR is the second. §5.9 (g) records, confirmed by the owner, that the first is not
a goal of it.

## 2. Alternatives considered

Two questions were put to the owner. Their options are A–B and C–E. F and G are this session's, about how the client
is built; F was confirmed by the owner (§5.9 (a)) and G with the rest of §5.9.

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

### Alternative H — XLSForm's model, stored as JSON (chosen, by the owner, 2026-11-09)

Asked a day after A was chosen: would a JSON format of our own have been better (B)? **The answer given to the owner
was that B's hard part is not the container.** A form's difficulty is its logic: `relevant`, `constraint`,
`calculation`, `choice_filter` and repeats. A JSON format of our own still needs an expression language for those.
Either a new one is invented, whose specification, test cases and migrations this project then carries forever. Or
XLSForm's is put inside the JSON, and the result is XLSForm in another container.

**Argument for.** It takes what is true in both. B's real advantages are a designer that edits a structure instead of
a spreadsheet, readable diffs, and room for things of our own. A's real advantages are existing forms, a specified
expression language with test cases, and a format a non-programmer can write in Excel. The client was always going to
be sent JSON, because the browser does not read workbooks. Making that JSON the stored source, rather than a cache of
the workbook, costs one writer (JSON back to `.xlsx`) and removes the designer's hardest problem: preserving a
spreadsheet's unknown columns on every save. Things of our own go in XLSForm's extension columns,
`bind::graticula:…` and `body::graticula:…`, the way Esri adds `bind::esri:…`, so an exported workbook still opens in
other tools.

**Argument against.** Two representations of one form must stay exactly equivalent. A difference between them is a
form that changes when it is downloaded and uploaded again. Condition 7 tests exactly that.

### Alternative F — Adopt an existing XLSForm client (Enketo, ODK Web Forms) — not chosen (owner, §5.9 (a))

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

### Alternative G — A native mobile app — not chosen

**Argument for.** Native apps have unrestricted storage, background sync, and camera and GPS access without browser
prompts.

**Argument against.** It is a second product with two app stores, two codebases or a cross-platform framework, and
releases that are not this server's. A browser client installed to the home screen (a PWA) does everything a form
needs. The one place it is weaker, Safari's storage, is condition 2.

## 3. Counterarguments to the preferred option

**"This is a second product."** It is, and that is the strongest objection. Graticula is a GIS server with a portal.
Survey123 is a form product built on one. The answer is that most of the server half already exists (§1), and that the
owner asked for it in so many words. The cost is real and is in §6.

**"Offline from the start will delay everything else."** It will delay forms, and since the owner also chose an
offline Inbox (§5.6) it will delay them more. The answer is that the sync is Graticula's own and narrow: one client,
records chosen by a filter, and a conflict rule that refuses instead of merging. It is not ArcGIS's replica protocol,
which stays out (ADR-082).

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
   whose resource is **the form model: JSON, holding the XLSForm's sheets row by row and column by column, unknown
   columns included** (owner decision 2026-11-09, §2 H). It is the only stored source. The workbook is not kept beside
   it. *Download* writes a workbook from the model, and the client is sent the model itself. It is related to the hosted feature
   layer it writes to, and, when public, to that layer's add-only view ([ADR-113](ADR-113-a-hosted-layer-has-views.md)).
   It is shared like any item, and a public form is a form shared with everyone.
2. **Publishing a form makes its layer.** Each question becomes a field of the type it declares. A choice list
   becomes a shared coded-value domain. ~~**A form has at most one location question**, `geopoint`, `geotrace` or
   `geoshape`, which becomes the layer's geometry, and none makes a table.~~ **Each table has at most one location
   question** (owner decision 2026-11-09, *"İlişkili katman olsun"*): the top level's becomes the layer's geometry, and
   a repeat's makes that repeat a related layer with its own geometry rather than a table, as Survey123 lays it out. A
   form with no location at the top level makes a table. A `repeat` becomes a related table, joined
   by `parentglobalid` through a declared relationship (ADR-013 §3), and **a repeat inside a repeat becomes a table
   related to that table** (owner decision 2026-11-09), to any depth. `image`, `signature`, `audio` and `file` questions
   become attachments, **with the question's name as the attachment's `keywords`**, which ends the empty `keywords`
   in §4. Editor tracking is on. Republishing an edited form changes the layer the way the schema editor does
   ([ADR-058](ADR-058-the-datastore-schema-is-edited-from-the-screen.md)), adding fields and never silently dropping
   one with data in it.
3. **A workbook is imported on the server** and turned into the model, ~~through the import reader that already reads
   `.xlsx`~~ **by a small reader of its own, `FormWorkbook` (changed 2026-11-09 while building it).** The import reader
   turns a workbook's first sheet into CSV through GDAL in a separate process (ADR-112); a form has three sheets that
   matter, and condition 7's round trip cannot rest on a reader that guesses header rows and cell types. An XLSForm is
   text in cells, so `FormWorkbook` reads only the parts of SpreadsheetML that carry text, with the base library's zip
   and XML readers, DTDs refused, and writes every cell back as an inline string. Two workbook readers now exist, for
   two different jobs: a table of data (GDAL) and a form (this). A boolean cell comes back as the text `TRUE` or
   `FALSE` and rich-text formatting is not kept; neither changes what a form means. The sheets are `survey`, `choices` and `settings`. What the server cannot honour is refused at upload
   with the row and column named, never ignored (condition 5). The same check runs on every save of the model, from the
   designer or the API, so a form cannot reach a state through the designer that an upload would refuse.
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
6. **Existing records are edited offline too (owner decision), and ADR-082 is amended in part.** *(Designed 2026-11-16 in
   [ADR-300](ADR-300-a-forms-inbox-is-a-view-each-reader-sees-their-own-rows-of.md): the Inbox is a view, Q-232 (a).)* A form may have an
   *Inbox*, as Survey123's does: the records a publisher's filter selects for the signed-in user are downloaded with
   their attachments, and edited on the device with or without signal. The edits wait in the same queue as new records.
   - **Conflicts are found on the server, never resolved silently (owner decision 2026-11-09, §5.9 (j)).** Each downloaded record
     carries the time it was last edited (editor tracking, which §5.2 turns on). An edit sent back says which version it
     was made against. If the record changed on the server since then, the edit is refused, not applied. The device then
     shows both versions side by side and the user chooses. A record deleted on the server while edited on a device is
     kept on the device, marked, and shown to its author. The last writer never wins without being told.
   - **What ADR-082 still keeps out:** ArcGIS's replica protocol, `createReplica`, `synchronizeReplica`,
     `unRegisterReplica` and `extractChanges`. Field Maps' offline areas and Pro's *Download Map* still do not work
     against this server, and every layer still says `supportsDisconnectedEditing: false`. The Inbox is Graticula's own
     client talking to Graticula's own server. It is not a replica that ArcGIS's clients could open.
   - **What the Inbox downloads is bounded** by the publisher's filter and a record limit per form (owner decision 2026-11-09,
     §5.9 (k)), so that a device never tries to hold a whole layer.
7. **The results screen is in Studio.** On the form's item page: the submissions as a table and on a map, a summary
   per question (counts for choices, ranges for numbers), filtering by question, export through the existing layer
   export ([ADR-107](ADR-107-a-layer-is-exported-as-geopackage-shapefile-or-workbook.md)), and the webhooks
   ([ADR-266](ADR-266-a-webhook-tells-another-system-what-changed.md)) already offered on the layer.
   **The map, the filter and the export were added 2026-11-09 overnight**, as Survey123's Data tab has them: the
   submissions the filter selects drawn over OpenStreetMap (the latest 2,000), a filter of one question — *is*, *is not*,
   *includes*, *contains*, *is at least*, *is at most* by how its answer is stored — and the dates it was sent between,
   which the count, the latest, the summary and the map all follow; and *Download every answer as* CSV, Excel, Shapefile,
   file geodatabase, GeoPackage, GeoJSON or KML through ADR-107's export of every table the form publishes. ~~**INFERRED, for
   the owner to confirm:** the export takes every answer whatever the filter shows, and says so beside the button —
   ADR-107's export takes no filter, and a filtered export is a change to it rather than to the form.~~ **Answered by
   the owner 2026-11-09: the export follows the filter** (*"Evet, süzgeç alsın"*). ADR-107 §5.6 gave the export a
   `where`; with a filter on, the button says *Download these 4* — *Nothing to download* when none match — and the file
   holds those submissions and, under them, their repeats' answers and no others.
8. **A designer in Studio edits the model.** It is a visual editor for the common cases. Columns it does not edit are
   in the model and stay untouched, and it says so on screen when a form uses something it cannot edit. **There is
   one model, never a second one behind the designer**: the JSON and the workbook are two spellings of the same rows,
   and anything the designer can make can be downloaded as XLSForm. Graticula's own additions go in
   `bind::graticula:…` and `body::graticula:…` columns, never in a field the workbook cannot hold.
   **Built 2026-11-09, by owner decision** (*"Survey123 web tasarımcısı"*, *"Şimdi, aynı dalda"*), in
   `wwwroot/form-designer.js` at `/studio/item/{id}/design`. Survey123's web designer in structure: the form in the
   middle a question at a time, each card with move up, move down, duplicate and delete; beside it **Add** (the question
   types), **Edit** (label, hint, field name, choices, required, read only, default, appearance, the scale of a rating,
   validation, and *Show this only when*), **Settings** (form title, layer name, version, default language, groups as
   pages, record name) and **Preview**. It edits the model's rows in place and nothing else: every change is checked by
   the server's own `/check` as it is made, Save is `PUT /admin/forms/{id}/model`, and what either refuses is listed with
   a way to the question it is about. **INFERRED, for the owner to confirm:** (a) the preview is the form client itself,
   framed — `/forms/preview` serves the same page, takes the form the designer posts and sends nothing; that page alone
   may be framed, by this origin only, and Studio's policy gained `frame-src 'self'`, which admits nothing else because
   every other page here refuses framing; (b) the designer writes labels in one language — plain `label`, or the default
   language's column — and lists the others as kept; (c) a rule is built as conditions joined by *all* or *any* (is, is
   not, includes, compares, is answered), and a rule written in any other shape is shown and edited as its expression,
   with *Write it as an expression instead* always offered; validation the same way, as smallest and largest, or longest
   answer; (d) a field name follows its label until somebody types one, Turkish letters written as their Latin
   neighbours, and a choice list the designer made is named after its question and follows its name while no other
   question uses it; deleting the last question that asks a list deletes the list; (e) New item › Form offers *Design a
   blank form* beside the workbook — and since the UX review the same night the dialog asks first how to start, blank
   selected, as Survey123's does; (f) ~~a published form opens read only~~ **since §5.13 the same night, a published form
   is edited as a draft and published again from its page**; (g) unsaved changes are kept for the tab's life when the designer is
   left and put back on return, and closing the tab with changes asks first.
9. **INFERRED, for the owner to confirm** (listed as [Q-229](../open-questions.md)). **All answered 2026-11-09:**
   (j) confirmed as written (*"Reddet, ikisini göster"*); (b) confirmed except that **a repeat inside a repeat is in
   the first version** (*"İlk sürümde olsun"*, §5.2, §5.10); (c), (d), (f), (g), (h), (i) and (k) confirmed as written
   (*"Hepsi önerildiği gibi"*). Nothing in this list is INFERRED any more.
   - (a) the client is built here and Enketo or ODK Web Forms is not adopted (§2 F) — **confirmed by the owner
     2026-11-08:** *"Kendimiz yazalım"*;
   - (b) the first version's question types and functions are condition 3's list, and anything else is refused at
     upload rather than half supported;
   - (c) Esri's public XLSForm columns are read where they map onto something here (`bind::esri:fieldType`,
     `bind::esri:fieldLength`, `bind::esri:fieldAlias`), and the rest are refused by name;
   - (d) a repeat is a related table through `parentglobalid`, as Survey123 lays it out, so data leaving for or
     arriving from Survey123 keeps its shape;
   - (e) ~~editing existing records is online only~~ — **decided otherwise by the owner 2026-11-08:** *"Çevrimdışı da
     olsun"*, existing records are edited offline too (§5.6);
   - (f) **when a form changes while records wait on a phone**, a queued record is sent as filled; a field it carries
     that no longer exists is refused by the server, and the record is kept on the device, marked, and shown to its
     author rather than lost or silently trimmed;
   - (g) Esri's Survey123 apps opening Graticula's forms is **not** a goal;
   - (h) a report per record (Survey123's *feature report*) is a later ADR, built on the print service
     ([ADR-265](ADR-265-the-server-prints-a-web-map.md)) and not on Word templates;
   - (i) the server checks what a database can check (types, lengths, domains, required fields that are not
     conditional) and does **not** re-evaluate the form's XPath logic. A second evaluator in C# would drift from the
     browser's, and a hostile client could skip the logic anyway, so logic is a usability contract and the schema is
     the integrity contract;
   - (j) **an offline edit made against a record that has since changed on the server is refused, and the device shows
     both versions for the user to choose** (§5.6), rather than the last writer winning;
   - (k) the Inbox downloads only the records the publisher's filter selects, up to a limit per form that the publisher
     sets, with a default chosen when it is measured on a device.

10. **The first version's list (condition 3).** Written 2026-11-08 from three public sources, read that day: the
    XLSForm specification ([xlsform.org/en](https://xlsform.org/en/)), the ODK XForms specification's function table
    ([getodk.github.io/xforms-spec](https://getodk.github.io/xforms-spec/)), and Esri's public Survey123 XLSForm pages
    (`doc.arcgis.com/en/survey123/create/main/`: *xlsformessentials*, *esricustomcolumns*, *xlsformsettings*,
    *xlsformformulas*, *xlsformsappearance*). Nothing here comes from Survey123's code. **What is on the list and what
    is not was confirmed by the owner 2026-11-09** (§5.9 (b)), with nested repeats moved in.

    **The rule that sorts everything below.** Anything that changes *what is stored*, or *which answer is valid*, is
    either **supported** with its full meaning or **refused** at upload with the row, the column and the reason. It is
    never accepted and quietly ignored. Anything that changes only *how a question looks* may instead be **drawn the
    default way**. The upload then says which of these it found, by row, so the publisher knows before anyone fills the
    form in. That third state exists so that a Survey123 form is not refused for a column width (A-089), and it is
    never used for anything that touches data.

    **Question types.**

    | | Types |
    |---|---|
    | Supported | `integer`, `decimal`, `range`, `text`, `select_one`, `select_multiple`, `note`, `geopoint`, `geotrace`, `geoshape`, `date`, `time`, `dateTime`, `image`, `audio`, `video`, `file`, `calculate`, `hidden`, `acknowledge`, **`barcode` (since 2026-11-16, §5.22)**; `select_one_from_file` and `select_multiple_from_file` **from a CSV** uploaded with the form |
    | Supported metadata | `start`, `end`, `today`, `username` and `email` (from the signed-in member, empty for an anonymous form), `deviceid` (a random id kept by the installed client, not a hardware id) |
    | Refused | `rank`, ~~`barcode` (Safari has no barcode reader)~~ *(lifted 2026-11-16 by owner decision: the answer is always typeable and the camera scans where the browser can read a code, §5.22)*, `background-audio`, `xml-external`, `csv-external`, `select_one_external`, `select_multiple_external`, `*_from_file` from XML or GeoJSON, `phonenumber`, `audit` (`or_other` was on this list until 2026-11-15 and is supported: §5.19 (1)) |

    **Groups and repeats.** `begin group` / `end group` with `relevant`, nested to any depth. `begin repeat` /
    `end repeat` with `relevant`, `repeat_count` (a number or an expression) and a label. ~~A repeat inside a repeat
    is refused in the first version~~ **A repeat inside a repeat is supported, to any depth** (owner decision
    2026-11-09): building → floor → room is a table related to a table related to the layer, and the offline queue sends
    a parent before its children. ~~**A location question inside a repeat is refused**: the form's one location is at
    its top level (§5.2), so a repeat is a table, not a layer.~~ **A location question inside a repeat makes the repeat a
    related layer** (owner decision 2026-11-09, §5.2); a second location in the same table is refused. `field-list`, `table-list` and the
    `pages` style are supported, because they decide what one screen holds and a field crew relies on that.

    **Survey sheet columns.** Supported: `type`, `name`, `label`, `hint`, `guidance_hint`, `required`,
    `required_message`, `constraint`, `constraint_message`, `relevant`, `default`, `read_only` (and the spelling
    `readonly`, which the specification's own example uses), `calculation`, `choice_filter`, `appearance`,
    `repeat_count`, the `image` media column, and every `::language` variant of the text columns. `parameters` is
    supported for `range` (`start`, `end`, `step`) and for `image` (`max-pixels`), and refused for anything else.
    Refused: `trigger`, and the `audio` and `video` media columns.

    **Choices sheet.** `list_name`, `name`, `label`, `label::language`, `image`, and any further column a
    `choice_filter` reads. Refused: `geometry` (choosing on a map).

    **Settings sheet.** Supported: `form_title`, `form_id` (the layer's name), `version`, `instance_name`,
    `default_language`, `style` (`pages`; any other value is drawn the default way), `allow_choice_duplicates` (and **since §5.19 (4) a duplicate choice name in a `select_one` list is accepted without it**, as Survey123 accepts it),
    `clean_text_values`, `name`. `submission_url` is supported **when it names a feature layer of this server**, and the
    form then writes to that layer instead of making one; any other address is refused. **Built 2026-11-15 as §5.17; until then the column was read and nothing used it, publishing made a new layer, and this sentence was untrue.** `namespaces` is accepted and
    has no effect, which the upload says. Refused: `public_key` (encrypted submissions).

    **Appearances.** Supported because they change what is captured: `multiline` (on `text`; on `image` and `file`,
    several attachments), `signature` (on `image`), `month-year` and `year` (on `date`), `minimal`, `likert`, `rating`
    (on `range`), `hidden`, **and since 2026-11-15 (§5.19): `minimal` and `compact` on `begin group` and `begin repeat`
    (collapsed behind a button), `autocomplete` (on `select_one`), `spinner` (on `integer` and `decimal`) and
    `week-number` (on `date`)**, and `draw` (on `image`; **built 2026-11-15, §5.20 (e)**), **and since 2026-11-16 (§5.22) `minimal` and ODK's `hidden-answer` on `barcode`**. Drawn the default way: `horizontal`, `horizontal-compact`, `compact` (on a select), `quick`,
    `quickcompact`, `label`, `list-nolabel`, `counter`, `no-calendar`, the grid widths `w1`–`w4`, and every presentation
    appearance on Esri's list (`predictivetext`, `nopredictivetext`, `calculator`, `numbers`, `press-to-locate`,
    `hide-input`), and ODK's `tabular`, `columns`, `columns-pack` and `no-buttons`; and `minimal` on a type it means nothing on, where Survey123 ignores it too (§5.19 (2)). Refused because they capture something this client cannot: ~~`annotate`~~ (**built 2026-11-16, §5.24**), `map` and
    `quick map`, ~~`search()`~~ (**supported since 2026-11-16, §5.23**), `autocomplete` on `select_multiple` (§5.19 (3)), `geocode`, `distress`, `spike` and its variants, `rangefinder`,
    `image-map`, `inframarker`, `getinframarkerid`, `new-front`, `new-rear`.

    **Expressions.** `${name}` references and `.` for the question's own value. Every XPath 1.0 operator the ODK
    specification lists (`|`, `and`, `or`, `mod`, `div`, `=`, `!=`, `<`, `<=`, `>`, `>=`, `+`, `-` — and `*`, which XPath 1.0 has and the page's list drops), with
    dates usable in arithmetic as it describes. The parent, child and self axes, and predicates. Functions:

    | | Functions |
    |---|---|
    | Supported | **String:** `string`, `concat`, `join`, `substr`, `substring` (XPath 1.0 §4.2, beside ODK's `substr`), `substring-before`, `substring-after`, `translate`, `string-length` (one argument, required, as the specification says), `normalize-space`, `contains`, `starts-with`, `ends-with`, `uuid`; `pulldata` **in its CSV form** (a file uploaded with the form), **and `pulldata("@property", name)` and `pulldata("@geopoint", ${q}, part)`** (§5.20); `property(name)` (ODK's, §5.20). **Boolean:** `if`, `coalesce`, `once`, `true`, `false`, `boolean`, `boolean-from-string`, `not`, `regex`, `checklist`, `weighted-checklist`. **Number:** all of the specification's, `random` included, and XPath 1.0 §4.4's `floor` and `ceiling`. **Node-set:** `count`, `count-non-empty`, `position`, `last` (XPath 1.0 §4.1: the size of the node-set a predicate filters), `current`, `randomize`, and `instance` **for a choice list**. **Date and time:** all of the specification's, and `date-time` (Survey123's, §5.20; the specification's table has no other date function this list lacks). **Select:** `selected`, `selected-at`, `count-selected`, `jr:choice-name`. **Repeat:** `indexed-repeat`. **Geographic:** `area`, `distance`, `geofence`, `intersects` |
    | Refused | `digest`, `base64-decode`, `jr:itext`, and the XPath 1.0 core functions that name a node, an ID, a namespace or a language, which a form's answers do not have: `lang`, `id`, `name`, `local-name`, `namespace-uri` (each refused with its reason, on purpose, 2026-11-15); `instance` for anything other than a choice list, and **the Esri `pulldata("@…")` forms other than `@property` and `@geopoint`**: ~~`@layer`~~ (**supported since 2026-11-16, §5.23**), `@json`, `@exif`, `@javascript`; and, by name with a reason each, the properties a browser cannot know and the parts of a geopoint that are not built (§5.20). ~~`@layer` is the one most worth adding next, because it queries a feature layer, and this server has the layers. It is left out because the offline client would need a copy of that layer, which is the Inbox's problem again~~ — built 2026-11-16 by owner decision, online only, as Survey123's is (§5.23) |

    **Esri columns.** `bind::esri:fieldType` is supported for the types this server serves: `esriFieldTypeInteger`,
    `SmallInteger`, `BigInteger`, `Double`, `Single`, `String`, `Date`, `GUID`, and `null` (the question is not stored).
    `esriFieldTypeDateOnly` is accepted and stored as `Date` at midnight, which loses nothing, and the upload says so.
    `TimeOnly` and `TimestampOffset` are refused, because this server has no such field and storing them as something
    else would change the value. `bind::esri:fieldLength`, `bind::esri:fieldAlias`, `body::esri:visible` (hidden but
    still stored), `body::accuracyThreshold`, `body::accept` **and `body::esri:inputMask` (on `text`, §5.19 (5))** are supported. `body::esri:style` and
    `bind::saveIncomplete` are drawn the default way (the client always keeps drafts — built 2026-11-15, §5.16 (a)). `bind::esri:parameters` is supported
    for its `calculationMode` (`auto`, `manual`, `always`, `whenEmpty`) and for nothing else, which is refused by name (§5.20). Refused: `bind::esri:warning` and `bind::esri:warning_message`,
    `bind::esri:workflow`.

    **What a question becomes in the layer** (follows Survey123's table where one exists, for §5.9 (d)):

    | Question | Field |
    |---|---|
    | `integer` | Integer |
    | `decimal` | Double |
    | `range` | Integer when `start`, `end` and `step` are whole, otherwise Double |
    | `text`, `calculate`, `hidden`, `time`, `acknowledge`, `username`, `email`, `deviceid` | String, 255 unless `bind::esri:fieldLength` says otherwise |
    | `select_one` | String, with the choice list as a shared coded-value domain |
    | `select_multiple` | String, the chosen names **comma-separated, as Survey123 stores them** (XForms itself uses spaces; confirmed 2026-11-09) |
    | `date`, `dateTime`, `start`, `end`, `today` | Date |
    | `note` | no field, unless it has a `calculation` |
    | `geopoint`, `geotrace`, `geoshape` | the geometry of its table, which is then a layer (one per table, §5.2) |
    | `image`, `audio`, `video`, `file` | attachment, `keywords` = the question's name |
    | `begin repeat` | a related table, `parentglobalid` |

11. **Built overnight 2026-11-09, while the owner slept (*"gece: bitirebileceğin tüm işleri bitir"*). Every choice
    below was this session's, INFERRED, and listed as [Q-230](../open-questions.md).** **All confirmed by the owner
    2026-11-09:** (l) *"Kalsın"*, the rest *"Hepsi yazıldığı gibi"*. Nothing in this list is INFERRED any more.
    - (a) **Names.** The service is named after `form_id`, or the item's title when there is none, made an identifier;
      each repeat's layer is `{service}_{repeat}`, because a layer name is unique across the server. A question's column
      is its name with accented letters written as their Latin letters (`şehir_adı` → `sehir_adi`), then the importer's
      rule; the client is sent the map.
    - (b) **A form's layers are stored in WGS 84 (EPSG:4326)**, what a device's position is given in, not the Web
      Mercator a hand-designed layer defaults to.
    - (c) **`select_multiple` has no domain**: its value is several names joined by commas, which a coded-value domain
      would refuse. `select_one` has one shared domain per choice list, named `{service}_{list}`; a domain of that name
      with the same codes is shared, one with other codes refuses the publish.
    - (d) **A text field is 255 characters** unless `bind::esri:fieldLength` says otherwise (Survey123's default).
    - (e) **The publisher relates the form's own new layers**, without the administrator privilege
      `/admin/relationships` asks for: relating the tables one has just made is part of making them.
    - (f) **What publishing made is the item resource `graticula/publication.json`**: the service, each layer's id,
      address, geometry, parent, relationship and question-to-column map. The form item carries the type keywords
      `Form`, `Graticula Form` and `XLSForm`, and not `Survey123` (§5.9 (g)).
    - (g) **A failed publish removes what it made**: the service, dropped, and the choice lists it created.
    - (h) ~~**A published form's questions cannot be changed yet**, and it cannot be published twice: both answer 409.
      §5.2's republishing, which adds fields the way the schema editor does, is the next piece of work, not done.~~
      **Built the next night as §5.13.**
    - (i) **The client's expression evaluator** (`wwwroot/forms/xlsform-eval.js`) made ten readings of its own:
      `area` and `distance` on a sphere (the spec's spherical law of cosines; Chamberlain–Duquette for area, which the
      spec does not name), up to about 0.5% from WGS 84 geodesics; an empty answer is false as a boolean, so
      `if(${q}, …)` means *q is answered*; a date string is a number of days wherever text becomes a number; the
      select functions split on commas and spaces; a dateTime without an offset is the device's local time; `%b` and
      `%a` are English unless the form's language is one the browser knows; `round(x, d)` rounds halves up;
      `checklist` counts `true`, `'true'` and numbers above 0; `/data/…/x` and `../x` find a question by its last name;
      `randomize` follows the spec's generator but is not checked against ODK's test vectors.
    - (j) **Attachment keywords are kept for hosted layers only.** A hosted layer's companion table gains a
      `keywords` column, added the first time an old table is written to (checked first, since `ADD COLUMN` locks the
      table even when the column exists); `addAttachment` and `updateAttachment` take `keywords` as a part sent before
      the file or in the query, the attachment documents return it, and `queryAttachments` filters by it, which it
      refused until now. A registered database's attachment table (MySQL, Oracle, SQL Server, ADR-207) has no such
      column, and keywords sent there are refused with a 400, not dropped. A form publishes hosted layers only, so
      every form attachment keeps its question.
    - (k) **§5.6's conflict rule is not built on the server yet.** It has to be checked inside the edit's own
      transaction, where `applyEdits` writes, or two writers a moment apart can still lose one edit silently, which
      is the one thing §5.6 forbids. Checking it in the client before sending leaves exactly that window. The Inbox's
      offline editing therefore waits for it; adding new records offline does not, because an add cannot conflict.
    - (l) **A form shared to the organization or to everyone takes answers through a view that only adds**
      (`PUT /admin/forms/{id}/sharing`): the first share makes `{service}_submit`, Create only, **with its vector tiles turned off**, shared as the form is (tiles off since 2026-11-09: a hosted view draws tiles by default and tiles answer to `servesTiles`, not to `Query`, so the first trial's view gave every submitted answer and its point to anyone asking for a tile — the view is now made private, its tiles turned off, and only then shared; its documents state no extent and `generateRenderer` refuses, see [D-180](../architecture-debt.md); since the owner's [Q-231](../open-questions.md) decision the same day a Create-only view serves no tiles anyway, and the tiles-off setting is kept as a second lock),
      and gives each layer of the publication its `submitUrl`; going back to private takes the view back to private.
      **And a view whose capabilities include `Create` now takes new rows from anybody who can read it** — adds only,
      through `applyEdits` and `addAttachment`; updates and deletes keep [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md)'s
      rule, and so does every write to a layer that is not a view. This is a change to who may write, made overnight and **confirmed by the owner the next morning** (*"Kalsın"*).
      [ADR-113](ADR-113-a-hosted-layer-has-views.md) §6 said *"Survey-style add only public views work"*; measured
      2026-11-09, an anonymous add through such a view was refused with 401, because ADR-075's owner rule was asked
      first and views were never excepted. Without the exception no public survey can exist. A form is shared to `private`, `organization` or `public` only: sharing it to groups or to all organizations would need the submit view shared the same way, which is not built, so those are refused.
    - (m) **The form client** (`/forms/{id}`, `wwwroot/forms/fill.*`, a service worker scoped to `/forms/`) made these
      readings: **no credential is kept at rest** — a queued record is sent with the session's token, or one traded for
      the persistent session cookie as the map viewer does, and otherwise waits marked *sign in*, so a lost phone
      carries nothing that signs in; every submission passes through the device's outbox, and one the server refuses
      before accepting any part leaves it with the form still filled in, while a partly accepted one stays, marked; a
      repeat without `repeat_count` starts with one instance, **and an item in which nothing was entered is not sent** (owner decision 2026-11-09, after the first trial on dev stored an empty room row; an item counts as answered when the person changed a question in it, or in an item below it, and a value or file remains — defaults and calculations do not count; items a `repeat_count` asked for are sent as they are); a `date` is midnight on the device's calendar and a `time`
      is stored as text with its offset; an answer a choice filter stops offering is cleared; past `accuracyThreshold`
      a captured position blocks submission and a typed one does not; the page's own words follow the form's language when it is Turkish and are English otherwise (changed after the
      UX review the same night, which found Turkish forms with English buttons); a GlobalID added twice is refused with 400 and rolled back, which the
      client reads as *already arrived*, looks the row up and sends the rest; the page is served from the cache and
      refreshed behind, `form.json` from the network first. Geotrace and geoshape are captured as vertices, ~~with no map~~ **and since §5.15 on a map as well**.

12. **A form's items are arranged as Survey123 arranges them — owner decision 2026-11-09** (*"Survey123 gibi"*). Until
    then the layer and the answers view were listed under their service names (`ux_demo_dev1`, `ux_demo_dev1_submit`) at
    the root of the owner's content, beside a vector tile item for each. Now publishing titles the layer with the form's
    title and sharing titles the view with the form's title and `_form`; the form, the layer and the view are filed in
    the owner's folder `Survey-` and the form's title, made on first use and found by title afterwards; and the view has
    no vector tile item, because a view without `Query` serves no tiles at all ([Q-231](../open-questions.md), ADR-031
    §2a). **Service names and addresses do not change**: an item's title was never its address (ADR-260).
    **INFERRED, for the owner to confirm:** (a) a form item its owner already filed in a folder of their own stays there,
    where Survey123 moves it regardless; (b) a service made by somebody other than the form's owner — an administrator
    publishing a member's form — is titled but not filed, since a folder is one member's (ADR-114); (c) filing never
    fails a publish: a title or folder that cannot be written leaves the item at the root under its name; (d) the folder
    is left behind when the form is deleted, as any emptied folder is, and Survey123 does the same.

13. **A published form is changed as a draft and published again — built 2026-11-09 overnight**, in the order the owner
    set (*"sırası ile devam et"*), as Survey123 republishes. The item's model is the draft the designer saves; the model
    as last published is kept beside it (`graticula/published-model.json`) and is what `form.json` serves, so a saved change
    never sends an answer to a column that does not exist yet. The form's page says *changed since* and offers *Publish
    again…*, which lists the new fields and the questions taken out before anything is done. Publishing again adds a field
    for each new question (through the schema editor's own add-field act), keeps every field a question was taken out of
    with what was answered in it, renames nothing, gives every field its label as alias again, adds new choices to a list
    and keeps the old ones, and makes the answers view again — its columns are fixed when it is made (ADR-113 §3).
    Everything that would be refused is found first and listed together. **INFERRED, for the owner to confirm:** (a) a
    question whose field exists with another type is refused, not converted — **confirmed by the owner 2026-11-09**; (b)
    the layer name (`form_id`) cannot change; (c) ~~a new repeat is refused — it needs a new table and a relationship
    inside a live service, not built~~ **reversed by the owner 2026-11-09: a new repeat is added** (*"Repeat
    eklensin"*) — a new table and its relationship inside the live service, and the answers view made again with it; (d) a choice
    taken out of a list stays in the layer's domain, so an answer already given stays valid; (e) a longer text length in
    the form does not widen an existing column; (f) a renamed question is a new field, and the old one keeps its answers,
    as a question taken out does; (g) the answers view is dropped and made again, so for those seconds a submission is
    refused and waits in the device's outbox; (h) a failure part way keeps the fields already added, empty, and says to
    publish again to finish.

14. **A form is shared with groups — built 2026-11-09 overnight**, as Survey123 shares one. The form's Share offers
    *Groups* beside owner only, organization and everyone; the form item is shared with the groups chosen, and its
    answers view is shared as `group` with the same groups, so a group's members open the form and send answers and
    nobody outside it can (`A_form_shared_with_a_group_takes_answers_from_its_members_only`). The set is replaced as a
    whole: a group added is shared, a group left out is unshared, on the form and on its view. Sharing with all
    organizations is still refused (§5.11 (l)). **INFERRED, for the owner to confirm:** (a) the groups offered are the
    ones the caller owns or manages, as a document's Share offers them; (b) moving a form from groups to the organization
    or everyone leaves its group shares in place, as a Portal item keeps them, and they matter again only when it is
    shared with groups again.

15. **A location question is answered on a map — built 2026-11-09 overnight**, as Survey123's field app and web form
    answer it. A point, a line and an area each have a map over OpenStreetMap: a tap places the point, or adds the next
    vertex, and the shape is drawn as it grows; *Use my location*, *Add my position*, the coordinate boxes and *Remove
    last point* stay, and are the keyboard's and the screen reader's way, so the map is hidden from assistive
    technology rather than half-offered to it. The library is OpenLayers, the one Studio draws with, served under
    `/forms/` and kept in the service worker's shell (`graticula-forms-shell-v4`, bumped 2026-11-15 when the evaluator gained `floor`, `ceiling`, `last` and `substring`; v3 is the survey-forms branch's), so a form opened offline still has its
    map — without its ground, since tiles are not kept. The forms' policy admits images from `tile.openstreetmap.org`
    and nothing else from there. **INFERRED, for the owner to confirm:** (a) ~~OpenStreetMap is the ground, as in Studio's
    previews, and not a basemap the publisher chooses~~ **reversed by the owner 2026-11-09: the publisher chooses the
    ground** (*"Yayımlayan seçsin"*), as Survey123's form settings do — OpenStreetMap stays the default; (b) the map opens over Türkiye until a point is placed, rather
    than asking for the device's location on its own; (c) one finger scrolls the form and two move the map, and the wheel
    zooms only with Ctrl, so a map in a long form never traps the page; (d) §5.5's *its basemap kept on the device* is
    still not built — offline, the map shows the points on an empty ground. **After its UX review the same night:** a tap below street
    scale zooms in on the place instead of answering, so one careless touch at country scale cannot answer a required
    question; with the device's position already allowed the map opens on it; the map is a named region whose zoom
    buttons are heard, not a hidden box with focusable controls; a line's or area's points are numbered, each with its
    own Remove, and the area says when it is closed.

16. **What §5.10 listed as supported and the code accepted without doing — built 2026-11-15, overnight, while the owner
    slept (*"Survey123 konusunda eksik ne kaldı, onlara bak"*).** A gap review found six places where §5.10 said *supported*
    and the client did nothing, which is the one thing §5.10's own rule forbids. Each is built below. **Every choice in
    this list was this session's and is INFERRED, for the owner to confirm**; nothing in it is the owner's, and it is
    not yet a question in [open-questions.md](../open-questions.md). Evidence is the tests named in each point, run in a
    real Chrome against a stand-in server (`FormFillClientTests`, `FormFillClientScreensTests`) and, for what the server
    sends and refuses, `AFormTellsItsClientWhatItShowsTests`. **Nothing here was measured on a phone.**
    - (a) **Drafts** — §5.10's *"the client always keeps drafts"*, which had no code. A third IndexedDB store, `drafts`
      (database version 2), holds a form being filled in: the answers as the instance tree, each repeat item with the
      GlobalID it was made with, and every attachment as the `Blob` the person chose, so a draft takes the same room
      in storage as the record it becomes. It is written 700 ms after the last change, at once when the page is hidden,
      and a draft nobody entered anything in is not kept — a prefilled link or a defaulted form is not yet a draft. The
      form opens on its most recent draft after a reload, and an offline reload does too: the first draft also
      keeps a copy of the form on the device (the *Download for offline use* copy), because the service worker's own copy
      of `form.json` exists only from the second load. **Drafts** in the header lists them, each with **Resume** and
      **Discard** (confirmed); **Start a new form** and **Discard this draft** sit above the form while one is open. A draft
      is removed when its record is **queued in the outbox**, and returns if the server refuses the record before
      accepting any part of it and the form stays filled in (§5.11 (m)). One polite live region says *saved* once per
      draft, *resumed*, *discarded* and *reopened*; after a discard, focus goes to the next button in the list or to the
      Drafts button, and after a resume to the form. A draft started on an older version of the form is read back onto
      the form as it is: answers to questions that no longer exist are left out, and it says so.
      **(1) The owner's, 2026-11-15** (asked that night, *"Son taslak açılsın"*): Survey123 opens a blank form and keeps
      drafts in its Drafts folder; here the latest draft is reopened, because §5.10 asks for drafts to survive a reload
      and a form that forgets on reload has no drafts to list. A link with `field:` answers in it starts a new form
      instead, and the draft stays in the list. **INFERRED:** (2) Several
      drafts of one form may exist; the device holds as many as the person starts. (3) Drafts are never sent without
      the person pressing Submit. (4) A draft is kept with no credential, as the outbox is (§5.11 (m)).
    - (b) **`guidance_hint`** is sent per language (`guidanceHints`) and drawn behind a **More help** button
      (`aria-expanded`, `aria-controls`; the panel is the question's own, and stays open when the page is drawn again).
      Survey123's documentation says the guidance is hidden until a button next to the hint is tapped, so that is the
      choice, not the XLSForm specification's print layout. **INFERRED:** it is offered even where the question has no
      `hint`, which Survey123 does not do (*"can only be used if there is already a hint"*), because accepting a
      column and showing nothing is what §5.10 forbids. A guidance hint on a group or repeat is refused at its row.
    - (c) **Pictures** (`media::image`, and its older spelling `image`, per language) on a question and on a choice. A
      picture is a file **uploaded with the form as an item resource at `media/<name>`** — the folder Survey123 keeps
      them in, and where §5.5 already reads a form's CSV — and the model stores only its name. A name is a plain file name
      ending `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp` or `.svg`: a folder, a web address or another extension is refused at
      upload with its row and column, and a **publish is refused for every picture the item does not hold**, listed by
      sheet, row and column (an upload comes before the item and its files, so the upload cannot ask). The client reads
      them like a CSV, draws them as images, keeps them on the device with the form, and says *Picture not available:
      name* where one cannot be read; it never draws a broken image. A picture is decoration beside text that says the
      same, so its `alt` is empty. **INFERRED:** a picture on a group or repeat is refused rather than drawn, because a
      heading has nowhere to put it; `minimal` over choices with pictures is refused, because a drop-down cannot show
      them; and `audio` and `video` stay refused (§5.10). Not tested against a real Survey123 project folder.
    - (d) **`field-list` and `table-list`.** *`table-list`*: a group of `select_one` / `select_multiple` questions over
      one choice list, drawn as a table — a question to a row, a choice to a column, each cell named by its row and its
      column, the table a keyboard-reachable scrolling region on a narrow screen. The server refuses any other group with
      the appearance at its row: a child that is not a select, a second list, a `choice_filter`, no questions; and
      `table-list` on a `begin repeat` is refused by the appearance's own type rule (it was *supported* for repeats in
      the list above, which was a claim without a meaning). *`field-list`*: this client **has pages**, but only as the
      `pages` style, where a top-level group already is a page. What `field-list` adds is for a group *inside* a group:
      it is lifted out to a page of its own, its parent's heading above it, and what lies around it keeps pages on either
      side. **Without the pages style the whole form is one page, so `field-list` changes nothing there, and the upload
      says so with a notice** — said, not silent, and not refused, because refusing it would refuse a Survey123 form that is
      right. **INFERRED:** this meaning of `field-list` for a form that has pages but not one question per screen.
    - (e) **Link parameters**, read from the page's address: `field:<question>=<answer>`, `hide=header,navbar,offline`
      and `center=<lat>,<lon>`. Each value is checked against its question: whole number, number, a date that exists
      (`YYYY-MM-DD`; `YYYY-MM` for `month-year`, `YYYY` for `year`), a time, a date and time, a place as `lat,lon`, a
      choice or several that are on the list, `yes`/`no` for an acknowledgement, a number within a range. A value that
      does not fit, a question that does not exist, one inside a repeat, a calculation, a group, a type a link cannot
      fill (photos, lines, areas, metadata), an unknown `hide` part and a `center` out of range are **left out and listed
      by parameter in a region above the form** that is announced and can be dismissed; a prefilled answer is not
      "touched", so a required question is not scolded before anyone reached it. `center` opens every location map
      there at zoom 16, close enough for one tap to place a point, and keeps the device's position from moving it.
      **INFERRED:** (1) `hide` parts: `header` hides the title visually and keeps it for screen readers, `navbar` the
      language picker and the Drafts and Outbox buttons, `offline` the keep-on-device panel; Survey123's list is longer
      and names parts this page does not have. (2) `center` is latitude first, as the owner's brief wrote it. (3) A
      `redirect=` link parameter is **not** honoured and is listed as ignored: it would make every form an open
      redirect for anyone able to write a URL, and §5.16 (f) gives the publisher the setting instead. I could not read
      Survey123's own page for its link parameters (its documentation moved and the new address answered 404), so
      this list is from memory of the product and from the owner's brief, not from a citation. Not measured on a phone.
    - (f) **The thank-you screen and the redirect** are two settings of the form, `graticula:thank_you` (up to 2,000
      characters, shown instead of the page's own sentence) and `graticula:redirect` (an **absolute http or https address
      with no credentials**; anything else is refused at its settings cell, and the client checks again before
      navigating). Neither is an XLSForm column and the ADR named none; they follow §5.1's rule for things of our own, and
      the designer's Settings tab has a field for each. After an answer was **sent** — not queued, not part-refused — the
      page says where it is taking the person and in how many seconds (five), once, in a live region, and offers **Go now**
      and **Stay on this page**; staying moves focus to *Fill in a new form*. A redirect nobody can stop would take the
      screen away before it is read. **INFERRED:** all of the above, including the five seconds and the absence of a
      redirect for a queued record, which is sent later by a page nobody is looking at.
    - (g) **The map's ground is the publisher's** — §5.15 (a)'s reversal, which had no code. A settings column
      `graticula:basemap` takes **`osm`** (the default, and what any unknown value draws — the server refuses unknown values
      at save, so this is a floor and not a feature), **`topo`** (OpenTopoMap) or **`none`** (the points alone, on a plain
      ground). The designer's Settings tab offers the three; the form's policy admits `https://tile.opentopomap.org` for
      images and nothing else new. **INFERRED:** three grounds and not a publisher's own tile address, because the
      page's content-security policy names origins, and a form that could name one would widen the policy for every
      form on the server; Survey123's settings offer a list of ArcGIS basemaps, which this server cannot draw.
      OpenTopoMap's tile terms were not read tonight; if it is to stay, someone should.
    - (h) **The service worker's cache is `graticula-forms-shell-v3`**, so installed clients take all of this; no new file
      was added to the shell.
17. **A form that names a layer writes to it — built 2026-11-15, while the owner slept (§5.10's settings row said it
    was supported; the code parsed `submission_url` and nothing read it, so publishing silently made a new layer).**
    This is Survey123's *survey from an existing feature layer*, built from the public ArcGIS REST address forms and from
    what the catalogue holds, not from any product's code. Every choice below is this session's and **INFERRED, for the
    owner to confirm**; the questions they answer were not put to the owner.
    - **What it does.** On publish, `submission_url` is read (`SubmissionTarget`): an absolute address of this server, or a
      relative one, ending in `/FeatureServer` or `/FeatureServer/{n}`, with the `/arcgis` and `/server` prefixes, the
      server's path base, a trailing slash and a query accepted. An address that is not shaped so is refused wherever a
      form is checked or saved, at `settings` row 2, column `submission_url`; one on another host or port is refused at
      publish with the same row and column and the host named, because the server does not reach out to another one and
      a form's results screen, sharing and view are all this server's. The service is looked up, must be the caller's to
      manage, and must not be a view. Then every table of the form is matched to a layer or table of the service
      (`SubmissionMatcher`), **every mismatch is listed, and nothing is written until none is left**: a question whose
      field does not exist (row of the question, column `name`), whose type does not fit (column `type`, or
      `bind::esri:fieldType` when that decided it), whose stated `bind::esri:fieldLength` is longer than the field holds,
      or whose choice list has a code the field's own coded domain would refuse; an attachment question on a layer that
      keeps none; a location question of another kind than the layer's geometry, or on a table, or a layer with geometry
      and no location question; a layer with no GlobalID field; a repeat for which there is no table related to its
      parent by `globalid` and `parentglobalid`, with that field present. The form's publication is then written in the
      shape a new layer's is, marked `existing`, so the form client, the results screen and sharing read it unchanged,
      and the form item is linked to the service. **Measured, not claimed:** against a real server over PostGIS
      (`AFormThatNamesALayerWritesToItTests`) the target's fields are identical before and after publishing, publishing
      again and refusing, a record sent through the answers view lands in the target, and a layer in Web Mercator takes
      the client's WGS 84 geometry.
    - **(a) The target's schema is never altered.** No field, GlobalID column, editor tracking or choice list is added,
      where publishing a new layer adds all of them. Survey123 does not add fields to an existing layer when it publishes
      to one, as far as its public documentation says; this is read from that and not measured on a Survey123 install.
      A layer somebody else designed is theirs.
    - **(b) Hosted layers in the `hosted` folder only.** A registered table (PostGIS, MySQL, Oracle, SQL Server) is
      refused at publish, even for a form that is never shared, because the Create-only `_submit` view is made over a
      hosted layer's rows ([ADR-113](ADR-113-a-hosted-layer-has-views.md): `CreateViewAsync` takes hosted layers with an
      integer key, nothing else), so such a form could be published and then never shared. Studio's results screen also
      addresses a form's service in the `hosted` folder. A registered target is not impossible, only not built; the
      refusal says why.
    - **(c)** An address that names the service and no layer means the service's first layer of the kind the form writes
      (a layer when the form has a location question, a table when it has none); Survey123's own addresses name the
      layer.
    - **(d)** A repeat's table is found among the tables related from its parent's layer, by the repeat's name, the name
      made an identifier, or `{service}_{identifier}` (what publishing makes), and must be related as a form's repeat
      is.
    - **(e)** A number goes into a wider field (small integer into integer, big integer or double; integer into big
      integer or double; single into double) and nothing else changes type, because converting changes the value. A text
      length is compared only when the form states one: the default 255 would refuse most layers somebody else designed.
      A choice list's domain is compared only when the field already has one; a field without one gets none.
    - **(f) The answers view is made over the layers the form writes to, not over every layer of the service**, so a
      service with other layers does not hand a public survey a way to add to them. Its name is the service's with
      `_submit`, the service's part cut to 33 characters when longer so the whole fits a view's 40 (this also changes a new form
      whose name was longer, which could not be shared before). The service itself is not retitled or filed; the form
      item and the view are, as §5.12 says.
    - **(g) Publishing again changes nothing in the layer.** The form is matched again, the publication is rewritten with
      the questions as they are now, and no field is added. A form that writes to a layer cannot be moved to another, cannot
      stop naming it, and a form published with a layer of its own cannot start naming one (each refused at
      `submission_url`, because the answers are already where they are). A new repeat is matched to an existing related
      table or refused; the answers view is made again only when the set of layers changed.
    - **(h) Deleting the form deletes the form item.** No route here deletes a service; the service a form names is not
      the form's, and its records stay (`AFormThatNamesALayerWritesToItTests`). The answers view, for any form,
      new layer or existing, is not removed by deleting the item either, which predates this and is not decided here.
    - **Not done:** a field the layer requires (not nullable) that no question fills is not found at publish; the first
      submission is refused by the layer, and the message says which field. Editor tracking is whatever the layer already
      has; a form does not turn it on.
18. **The corpus, the XPath core functions, and what the first real forms did — built 2026-11-15** (conditions 4, 5 and
    7). Numbers from one run of the checks on the `forms-corpus` branch; the corpus itself is not in the repository.
    Items 16 and 17 are the survey-forms branch's, and this item takes 18 so that the two do not collide.
    - **The corpus.** 110 public workbooks: pyxform's test fixtures, ARCED's and stats4sd's examples, ODK's own Web Forms
      and Central fixtures, the XLSForm.org example, eddiemulwa's ODK demo forms, and **61 Survey123-authored forms**:
      Esri's `Survey123Community` repository (Apache-2.0; 44 readable, including its Samples and Templates), laffchem's
      `survey123-files` (9, no licence stated), SailingGeographer's `Survey123` (5, no licence stated) and palavido-dev's
      `survey123-designer` (3, MIT). Each file's source URL, commit and licence are in `SOURCES.md` beside the corpus
      (`D:\temp\forms-corpus`, outside the repository). Nothing from it is committed, and nothing was run from it.
      108 read and 2 did not: an empty 0-byte file, and a zip with no workbook part.
    - **Condition 5 counts.** 31 of the 108 readable workbooks import unedited (29%). 12 of those have notices, which
      say what is drawn the default way or kept with no effect, and need no edit. Of the 61 Survey123-authored forms,
      **17 import unedited (28%)**, all of them from Esri's repository, and 0 of the other 44. There are 1,539 refusals
      in 77 forms. The largest reasons, counted per refusal: a survey row that holds cells but no question type, 953
      (one form, which carries a 951-row "French Translation" table beside its questions); an appearance this server does
      not know, 204; a duplicate choice, 53; `or_other`, 28; a column type this server does not know, 28; `pulldata("@…")`
      from Survey123 sources, 49; `autocomplete`, 27; `annotate`, 19; `property()`, 13; `search()`, 11. The full table, by
      reason, by column and by form, is the JSON report the harness writes.
    - **Condition 4 counts.** 1,441 expressions were found in the survey and settings sheets, and the server accepts 1,373
      of them. Every one of those parses and runs in the client without an error (`XlsformCorpusEvaluationTests`). The 68
      the server refuses are refused on purpose and are not run.
    - **Condition 7 counts.** All 108 readable workbooks go workbook → model → workbook → model, and the model's JSON
      round-trips, with the two models identical (`AFormRoundTripsThroughItsWorkbookTests`).
    - **Defects the corpus exposed, and their fixes**, each with a test built from a minimal workbook or model:
      1. Excel's `_xHHHH_` escapes were not decoded: 239 occurrences in 57 workbooks, so a label showed `_x000a_` where
         Excel shows a line break. The reader now decodes them. The writer escapes carriage returns, other control
         characters, and an underscore that begins an escape, so the round trip is exact.
      2. A date-formatted numeric cell was read as its serial number: four cells on form sheets (`survey` 3, `settings` 1)
         and 17 on other sheets, in 14 workbooks. A date in a `default` or a `version` now reads as the date (INFERRED, below).
      3. A corrupt zip made the reader throw a framework exception instead of refusing the file in words (one file).
      4. Formatting alone counted against the row and column limits, and five workbooks were refused as having more than
         512 columns or 50,000 rows with no content there. Empty cells and rows no longer count (INFERRED, below).
      5. A literal `default` (HTML, or a parenthesis in text) was scanned as XPath and failed in the client: three forms.
         A `default` is a formula only with a question reference or when it starts with a call (INFERRED, below).
      6. A label's language written with one colon, `label:English (en)`, which pyxform's own fixtures use, was read as an
         unknown column, and seven questions were refused as having no label. It is read as the double-colon form (INFERRED).
      7. A `choice_filter` naming a choices column with its language, `label::English`, was accepted by the server and
         refused by the client. Found by condition 4's run; fixed in the evaluator's lexer (INFERRED, below).
      8. `select_multiple X or other` was refused as "more than one word after the type", not as `or_other`. It is now
         refused as `or_other`, with its reason.
      9. `string-length()` with no argument was accepted by the evaluator; the specification makes its argument required.
    - **XPath core functions** (the check of XPath 1.0 §4 against the catalog). Added: `floor` and `ceiling` (§4.4),
      `last` (§4.1), and `substring` (§4.2, beside ODK's `substr`), each tested with XPath's own examples. Refused on
      purpose, and listed in §5.10: `lang`, `id`, `name`, `local-name`, `namespace-uri`. None of the new functions appears
      in the corpus, so they come from the specification, not from the forms, and are recorded as such. The evaluator
      has 231 case rows, 8 measure rows and 19 parse-error rows, and the catalog and the evaluator agree on both the
      functions and the refusals (`The_evaluator_refuses_exactly_FormCatalog_RefusedFunctions`).
    - **What condition 4 does not check.** The corpus run checks that every accepted expression parses and runs, and that
      the catalog and the evaluator agree. It does not compare results with expected values, because the corpus has none;
      the expected values are the specifications' examples only. A form whose logic gives a wrong answer without an error
      is not caught by this run.
    - **INFERRED, for the owner to confirm:**
      (a) `last()` outside a predicate is 1, the context size of a form's expression; inside a predicate it is the size of
      the node-set being filtered;
      (b) a `default` is a formula only when it contains `${` or starts with a function call, and is text otherwise;
      (c) a single-colon label language, `label:English (en)`, means the double-colon form;
      (d) a date-formatted numeric cell is read as the date it shows (`2020-01-31`, `2020-01-31T12:00:00`, or `12:00:00`).
      This changes §5.3's "a number is kept as the text the workbook stored" for dates alone. Serial 60 is Excel's phantom
      29 February 1900;
      (e) formatting alone, an empty cell or row, does not count against the row and column limits;
      (f) a `choice_filter` name with a language suffix is one name, and only the XPath axes may stand before `::`;
      (g) a survey sheet with a block of text beside the questions stays refused, because nothing is ignored silently. The
      owner may prefer a rule that lets a publisher keep notes beside a form; the one corpus form that does this is refused;
      (h) A-089 is INVALIDATED. That changes no decision here, since refusing what §5.10 leaves out is the first version's
      stated limit. It means the first version will refuse most Survey123 forms that use `pulldata("@…")`, `property()`,
      `search()`, `autocomplete` or `annotate`. Whether that is the right first version is the owner's question.
    - The service worker's shell cache was bumped to `graticula-forms-shell-v4` on this work's branch, because the evaluator
      changed; merged beside the design review's v5, the shell cache is v5.

19. **What a real Survey123 form says that the first version refused — built 2026-11-15, overnight, while the owner slept.**
    The owner's decision that night, in his words as they were given to the session: *increase compatibility with real
    Survey123 forms.* Measured on the corpus of §5.18 (110 workbooks, 61 of them Survey123-authored), **before: 17 of the 61
    import unedited (28%, 31 of all 108 readable); after: 31 of the 61 (51%, 47 of all 108)**. The same
    `AFormCorpusTests` run, the same corpus, the checker the only thing changed. What still blocks the Survey123 forms,
    counted by the forms it blocks: `pulldata("@geopoint")` 6, `pulldata("@property")` 6, `draw` 5, `annotate` 4, `date-time()` 2 and
    `property()` 2 are the parallel branch's work tonight (expressions and drawing); a row with no name 5, `barcode` 2,
    `pulldata("@layer")` 2, `search()` 2, `bind::esri:parameters` 2 and an attachment question with a field type 2 are not started. Every item below is **supported** with its full meaning or refused at
    its row and column; none is accepted and ignored (§5.10's rule). Public sources, read 2026-11-15:
    Survey123's own pages on XLSForm appearances, on multiple choice, on its custom columns and on settings
    (`doc.esri.com/en/arcgis-survey123/latest/create/main/`), the XLSForm specification, and Qt's public documentation
    of an input mask, whose grammar Survey123 says it uses. No Survey123 code was read. Every choice that those pages
    do not settle is **INFERRED, for the owner to confirm** and is marked so here; nothing in this list is the owner's but
    the direction. **Nothing here was measured on a phone**; the client was run in Chrome on a desktop, against a stand-in
    server (`FormFillCompatTests`, 17 tests), and the checker in `AFormImportsSurvey123SpellingsTests`.
    - **(1) `select_one X or_other` and `select_multiple X or_other`** (also written `or other`; 8 forms). XLSForm's
      meaning: the list gains a choice named `other`, labelled Other, and a text question `{name}_other`, labelled *Specify
      other*, follows the question and is relevant when `other` is among the answers, as `selected(${name}, 'other')`.
      Survey123's page says its tools make the extra field `{name}_other` in the layer, which the layer plan does here
      because the question is an ordinary text question of the table (255 characters, a field of its own). Both are made
      **by the checker, in the definition only**: the model keeps the row exactly as written (`select_one renk or_other`), so
      the workbook, the JSON and the round trip (condition 7) are unchanged and a republish adds the `renk_other` field
      like any new question and adds the `other` code to the list's existing domain like any new choice (the designer
      edits the model, where the row is still one question). **Where a Turkish or English label for the two comes from:**
      one per label language of the form, Turkish (*Diğer*, *Diğerini belirtin*) for a language that says Türkçe, Turkish or
      `(tr)`, English for the rest. **INFERRED:** (a) the choice is added to the shared list itself, so every question
      over that list offers Other, not a copy under another name. Survey123's page says nothing of a shared list, and I
      did not read what XLSForm's own tools do with one. A copy was built first and dropped: it makes a second domain, and
      a form already published with the first domain has fields that a republish does not move to the second, so an
      answer *other* would be refused by the layer; sharing the list keeps the domain the field has. The cost is that a
      question over the same list without `or_other` offers an Other with no text question after it. (b) A list that already
      has a choice named `other` is used as it is and gains none. (c) The choice is offered whatever a `choice_filter` says
      (the client is told it is automatic), because a filter reads columns the added choice has no value in and would
      otherwise take it away. (d) `or_other` after anything but a `select_one` or `select_multiple` list, on `*_from_file`, or
      where `{name}_other` is already a name, is refused at the type cell. Survey123's page says the *Specify other*
      label does not show in languages other than English; here it does, which is a difference in the form's favour.
    - **(2) `minimal` and `compact` on `begin group` and `begin repeat`** (7 forms). Survey123's page on appearances,
      verbatim in its headings: **`compact`** is listed for `select_one`, `select_multiple`, `begin group` and `begin repeat`
      and presents the questions of a group or repeat *"in a collapsed state at startup"*; **`minimal`** is listed for
      `select_one`, `select_multiple`, `barcode` and `begin repeat`, and `minimal compact` for `begin repeat` as both. So
      `compact` on a group and `minimal`/`compact` on a repeat are the documented ones. **INFERRED: `minimal` on a
      `begin group` collapses it too**, which the page does not list but which seven corpus forms (one an Esri sample)
      say, and which the owner's brief also read; and **`minimal`/`compact` on any other type is drawn the default way
      with a notice** ("Survey123 ignores it there"): one corpus form writes `minimal` on a text, an integer, a date, an image
      and a hidden question, and Survey123 publishes it, so the page's silence on them is not a refusal. In the
      client the group's or repeat item's heading is a **button** (`aria-expanded`, `aria-controls` the panel it opens,
      a 44 px target) that starts collapsed and is opened and closed by the person; a repeat's *item* is what collapses (one
      heading per item, *Room 1*, and its Remove button stays beside it, outside the toggle), and an item the person just
      added starts open, since the next thing they do is fill it in. What was opened stays open when the page is drawn again, by the group's key
      or the item's GlobalID, and the focus is put back on the button (`FormFillCompatTests`). **A message inside a
      collapsed panel is not hidden from the person**: the button says *needs attention* in its own name, Submit goes to the
      first problem and opens whatever is shut around it, and so does a link in the summary. **INFERRED:** a group that
      is a page under the pages style is not collapsible, because a page that opens shut is an empty screen; and nothing about
      the collapsed state is kept in a draft, so a resumed draft opens with its groups collapsed again.
    - **(3) `autocomplete`** (7 forms). Survey123's page: *"a select
      question is presented with a text field and a drop-down menu. Entering text in the field limits the drop-down menu to
      only answers that contain that text"*, and for `select_multiple` it is *"only supported in Survey123 Mobile"*, not in
      the field app or the web app. So **supported on `select_one` and `select_one_from_file`, and refused on `select_multiple`
      with that reason**, and over choices that have pictures (a type-ahead list cannot show them, as a drop-down
      cannot, §5.16 (c)). The widget is a **WAI-ARIA 1.2 combobox with a listbox**: the box is `role=combobox`,
      `aria-autocomplete=list`, `aria-expanded`, `aria-controls` and `aria-activedescendant`; the list is `role=listbox`
      with `role=option` items of 44 px; ArrowDown and ArrowUp move through it, Enter chooses, Escape closes (and, closed,
      puts the answer back); Tab or a tap elsewhere chooses a choice the text names exactly and otherwise puts the
      answer back; emptying the box empties the answer. A polite live region says how many choices match and which one was
      chosen. The list is drawn in the page's flow under the box, not floating, so a phone's keyboard does not cover it;
      it lists at most 100 choices and says so when there are more. It works with no signal (the test stops the server
      before typing) and **follows `choice_filter`**, clearing an answer the filter no longer offers as the other selects do.
      The box is named by the question's label, not by a fieldset. **INFERRED:** matching is *contains*, as the page says,
      and **ignores case and accents and treats the Turkish dotted and dotless i as one letter**, so *istanbul* finds
      *İstanbul* and *Ilgaz*; the page does not say.
    - **(4) Duplicate choice names** (6 forms; 53 refusals). **Evidence:** XLSForm's own tools refuse a duplicate name in a list
      unless the settings sheet says `allow_choice_duplicates`; Survey123's page on multiple choice says duplicates are
      *allowed for `select_one`* and *"not supported for `select_multiple` questions"* and *"not supported for multilingual
      surveys"*, that `jr:choice-name()` returns *"the label of the first duplicate choice in the list"*, and that a
      select_one reverts to *"the first duplicate choice"* when opened in some places. It describes no warning. The
      forms in the corpus that have them are Esri's own samples and templates (a list of airport codes, a list of
      countries, numeric scales), which Survey123 publishes. **So the decision follows Survey123 and not XLSForm's tools:**
      a duplicate is **accepted with a notice** (the row, and the row of the first) when every use of the list is a
      `select_one` in a form with labels in one language, or when the settings say `allow_choice_duplicates`; and it is
      **refused**, with Survey123's own reason and the way out, when a `select_multiple` uses the list or the form has more than
      one label language, and the setting does not say otherwise. One corpus form is refused for this (a
      `select_multiple`). Both choices are offered; they are one answer, stored as the name; the layer's coded-value domain
      has one code per name, and **keeps the label of the first**, which is what the page says `jr:choice-name()` does; the
      client shows a name that appears twice chosen **once, at its first place**. **INFERRED:** that the setting lifts the
      refusal for `select_multiple` and for several languages as well, as it did before (the first version already accepted it
      there), though Survey123's page says those are unsupported rather than refused.
    - **(5) `body::esri:inputMask`** (4 forms). Survey123's page on its custom columns, for the column: it applies to `text`
      questions only (*use `constraint` for numbers*), is in Connect and the field app and **blocks submission until every
      mask is satisfied**; the syntax is mask characters and separators, optionally ending `;` and a character that stands for a blank, with
      `A a` (ASCII letter, required or optional), `N n` (letter or digit), `X x` (any non-blank), `9 0` (digit), `D d` (digit
      1–9), `#` (digit, plus or minus, optional), `H h` (hexadecimal), `B b` (binary), the case switches `>` `<` `!`
      and `\` to make a special character a separator. **Supported on `text`**; on any other type it is refused at
      its row and column with that reason. The server keeps the column in the model and checks, at every save, that it is a mask
      (`FormInputMask`): at least one mask character, no trailing backslash, at most 255 characters, and **that the longest answer it
      allows fits the field it is stored in** (the 255 of §5.11 (d), or `bind::esri:fieldLength`), the one thing a database
      can check about it. It does not re-apply the mask to an answer it is sent, for the reason §5.9 (i) gives about every
      other rule of a form. In the client the box formats as the person types (`(555) 123-4567` from `5551234567`), drops
      what a place does not take, puts a separator in when the next character arrives and takes it off when it is deleted,
      keeps the caret where it was, shows the mask as the placeholder (blanks as the stated character, `_` otherwise) and
      in a line of text under the box that the box is described by, and sets the numeric keyboard when every place is a
      digit. An answer short of its last required place is refused, in words, with the pattern, and blocks Submit like any
      other message. **INFERRED:** (a) places are filled **in order**, so an optional place before a required one cannot
      be left blank, which Qt's mask allows and no corpus mask needs; (b) the stored text is **what the box shows**,
      separators included (`123-45`), as the field app's text is, which its page does not say; (c) the separators after
      the last place are put in when the box is left, not while typing; (d) the page's first example prints `\>A<xxxxxxxxxxxx`
      for *"a capital letter followed by lowercase"*, which reads right only as `>A<xxxxxxxxxxxx`, so the backslash is taken
      for a Markdown escape of the page and not for a separator. A mask that `FormInputMask` cannot read is refused, so the
      client's own copy of the grammar (`fill.js`) is only ever asked about masks the server has read; the two are
      held to the same cases by the two test classes.
    - **(6) `spinner` and `week-number`** (3 forms and 1). Survey123's page: **`spinner`** (`integer`, `decimal`) *"adds increase
      and decrease buttons to the value entry"*; **`week-number`** (`date`) *"lets the user select a week number"*; neither is
      listed for the web app, so this is the field app's behaviour offered in the browser. A spinner is a box with a labelled
      **Decrease** and **Increase** button either side, 44 px each, that step the value by one, and the arrow keys do the
      same in the box. **INFERRED:** the step is one for a decimal too; an empty box steps from zero; a decimal does
      not drift (0.1 and one is 1.1). A week number is a **year and a week number**, two labelled boxes in a group, and
      the date stored is **the Monday of that ISO 8601 week** (2026 week 1 is 29 December 2025), since the page does not say which day the
      field app stores; a week that does not exist (53 in a 52-week year) is refused in words and **what was typed is
      not emptied**, and a date set another way (a default, a draft) shows its year and week. **INFERRED:** the Monday, and
      a year and week as two boxes rather than the browser's `week` input, which Safari does not have.
    - **What was not done, and why.** `minimal`/`compact` on a select stay as they were (a drop-down; layout). `autocomplete`
      on `select_multiple` is refused, not built. The designer (`form-designer.js`) lists appearances by type from its own
      table and does not offer the five new ones; a form that has them is edited as the model it is and the server's `/check`
      accepts it, but a person cannot yet choose them from the designer's Edit tab: that table is the designer's and is
      not changed tonight. **No device was used**: how the combobox behaves with a phone's keyboard and screen reader, the
      44 px targets as measured on a real display, and the numeric keyboard for a mask are unmeasured.
    - The service worker's shell cache is `graticula-forms-shell-v6`, because `fill.js` and `fill.css` changed.
20. **Survey123's device and user properties, a geopoint's parts, the calculation modes, `date-time()` and drawing on a blank
    canvas — built 2026-11-15, by owner decision** (*"increase compatibility with real Survey123 forms"*). The corpus of
    §5.18 said which Survey123 features block the most forms; this item takes five. Built from Survey123's public pages
    (`doc.esri.com/en/arcgis-survey123/latest/create/main/`: *xlsformformulas*, *prepopulateanswers* for the device, user
    and survey properties and the calculation mode, *geopoints* for a geopoint's parts), read 2026-11-15, and from the
    ODK XForms specification's function table; nothing from Survey123's code. **Every choice below that those pages do
    not settle is this session's and INFERRED, for the owner to confirm.** What is measured is the corpus count, the
    server's checker (`AFormReadsSurvey123PropertiesAndCalculationModesTests`) and the client in a real Chrome against a
    stand-in server (`XlsformEvaluatorTests`, `FormFillClientCompatibilityTests`). **Nothing here was measured on a phone.**
    - **The count, from the corpus, run before and after on this branch.** Survey123-authored forms that import
      unedited: **17 of 61 (28%) before, 24 of 61 (39%) after.** All readable workbooks: 31 of 108 before, 38 of 108
      after. Survey123-authored forms each reason held back before this item, as `corpus_blockers.py` counts them:
      `pulldata("@property")` 6, `pulldata("@geopoint")` 6, `draw` 5, `bind::esri:parameters` 2, `date-time()` 2,
      `property()` 2. The counts overlap, and a form imports only when nothing else holds it, so seven came in and the rest
      are held by something else as well (the list in (f)). The expressions of condition 4 were run again with these
      functions accepted: every one the server accepts parses and runs in the client.
    - **(a) `pulldata("@property", name)` and `property(name)`.** Supported, because a browser knows them: `online`,
      `utcOffset` (hours), `timeZone` (the browser's IANA name), `deviceId` (the random id the installed client keeps,
      the same as the `deviceid` metadata question, §5.10), `userAgent`, `email`, `username`, `userFullName`,
      `userFirstName`, `userLastName`, `portalUrl`, `language`, `surveyName`, `mode`, `status`. The signed-in member's
      names come from the portal's `community/self`, the same call that already fills `email`; this server's own record
      holds the account name and no separate first or last name, so `userFullName` and `userFirstName` are the account
      name and `userLastName` is empty. **Refused at upload by name, each with its reason:** `deviceModel` and `OS`
      (a browser does not tell a page; Survey123's web app does not either), `appVersion`, `portalInfo` (a JSON object no
      question holds), `token` (a credential that would be written into the layer), `submissionUrl`, `locale` and
      `localeInfo` (the last three exist for `@layer`, `search()` and `@javascript`, which are refused). A name that is not
      on Survey123's list is refused as unknown; a name worked out at run time cannot be checked at upload, and the
      client then refuses it when it runs. **INFERRED:** (1) an anonymous form, and any value the portal does not hold,
      answers an empty string, since Survey123's page says nothing; (2) `online` is a true boolean, `utcOffset` a number,
      so `if(pulldata("@property", 'online'), …)` means what it says; (3) `mode` is always `new` and `status` always empty,
      the value Survey123 gives for a new response, because this client does not yet edit a sent record *(since 2026-11-16 `mode` is `edit` for a record opened from the Inbox, [ADR-300](ADR-300-a-forms-inbox-is-a-view-each-reader-sees-their-own-rows-of.md) §5.11; `status` stays empty)*; (4) `language` is
      the active language's name as the form writes it, `surveyName` the form's title, `portalUrl` this server's address
      for a signed-in member and empty otherwise; (5) **`property(name)` takes the three names ODK documents, `deviceid`,
      `username` and `email`, with or without the `uri:` prefix ODK's own forms write, and also every name
      `pulldata("@property")` takes**, because a Survey123 form in the corpus writes `property('userFullName')`
      and I could not find `property()` on Survey123's pages to say otherwise; `subscriberid`, `simserial` and
      `phonenumber` are refused by name (a browser cannot read a SIM or a phone number). The `uri:deviceid` that three of
      pyxform's own test forms write as a question *type* is a different thing and is still refused.
    - **(b) `pulldata("@geopoint", ${q}, part)`.** A geopoint answer is kept as latitude, longitude, altitude and accuracy,
      so the client answers: `x` (longitude), `y` (latitude), `z`, `horizontalAccuracy`, `positionSourceType`, and the
      point in degrees, minutes and seconds (`DMS` as the documented JSON object, and each `DMS.…` part, checked against the
      documented example 39°57'36.3"N). The parts Survey123 documents and a browser does not record (`verticalAccuracy`,
      `speed`, `verticalSpeed`, `direction`, `magneticVariation`, `fixType`, every `positionSourceInfo.…`, the GNSS errors
      and dilutions, `satellitesVisible` and `satellitesInUse`) are accepted and answer **empty**. **Refused by name:**
      `UTM`, its parts, and `MGRS` (the conversions are not built; `x` and `y` are in degrees), a fourth argument
      (precision, which only those use), and any name Survey123 does not document. **INFERRED:** (1) empty for a part not
      recorded, because Survey123's page says only that availability depends on the hardware; (2) a point typed or placed
      on the map is stored with accuracy 0, and the client reads that as *no sensor reading*: `z`, `horizontalAccuracy`
      answer empty and `positionSourceType` is 1 (*User*), where a device's reading is 2 (*System Location*); (3) a device
      that reports no altitude is stored as 0, so `z` is `0` there, which the stored form cannot tell from a real sea-level
      reading; (4) `accuracy`, which two Survey123 forms in the corpus use and the page does not list, is
      `horizontalAccuracy`; (5) the seconds are rounded to a tenth for the parts and to a whole second in `DMS.text`,
      as the documented example shows, and a rounding that reaches 60 carries into the minute.
    - **(c) `bind::esri:parameters` takes `calculationMode`: `auto`, `manual`, `always`, `whenEmpty`** (case ignored), on a
      question that has a calculation and is one of the types Survey123 lists (text, decimal, integer, note, date, time,
      dateTime, select_one, select_multiple, geopoint, calculate, hidden). Any other parameter is refused by name; one form
      in the corpus carries watermark parameters for a photo there (`topLeftWatermark`, `color`, `size`) and stays refused.
      The client: *always* recalculates whenever anything changes, whatever the person typed; *auto*, the default,
      calculates while the question is empty and recalculates when a value it reads has changed, and leaves what the person
      typed; *manual* calculates only when its **Calculate** button is pressed; *whenEmpty* calculates until it gives a value,
      then waits for the button, even if the value is cleared. The button is a button, named by its question, with a polite
      status saying *Calculated.* (1) **A calculation with no mode is `auto`, which is a change — the owner's, 2026-11-16**
      (asked with both behaviours named, *"Survey123 gibi"*): until this item every calculation was recalculated on every
      change, which is what `always` means and what ODK does, and a calculation the person edited (a `read_only` of `no`)
      was overwritten; now such a calculation keeps the person's answer, and one that reads only `now()` or `today()` is
      taken once and not again as the clock moves. A form that wants the old behaviour says `calculationMode=always`.
      **INFERRED:** (2) "when a value it reads has changed"
      is the answers named by `${…}` in the calculation, its repeat position and the language, and a calculation that reads
      by a path (`../q`, `/data/q`), `current()` or `instance()` is judged by every answer in the form; (3) `manual` on a
      question the person does not see (`calculate`, `hidden`, or the `hidden` appearance) is refused, since the button is on
      the question and the value would never be calculated, though Survey123's page lists `calculate` and `hidden` among
      the types; (4) whether the calculation has run, and whether `whenEmpty` is waiting, are not kept in a draft: a draft
      reopened takes an answer that is there as the person's and calculates the rest again; (5) a `whenEmpty` question that
      already has a default is taken as having its value.
    - **(d) `date-time()`**, Survey123's (its table: *converts a number or string to a date object*, example
      `date-time('2017-05-28T04:39:02+10:00')`). A number is days since 1970-01-01 UTC, so it undoes `decimal-date-time()`;
      a date and time is itself; **INFERRED:** a date alone is midnight on the device's clock. **ODK's function table was
      read for other date functions and none is missing:** `today`, `now`, `format-date`, `format-date-time`, `date`,
      `decimal-date-time` and `decimal-time` are all here, and `date-time` is not on that table, only on Survey123's. XForms
      1.0's own `local-date()`, `days-from-date()` and the rest are not ODK's and are not added.
    - **(e) The `draw` appearance** on an `image`: a blank canvas drawn on with a mouse, a pen or a finger, kept as one PNG
      attachment named after the question, as the signature pad is, because it is the signature pad's code made taller and
      with its own words (*Clear drawing*, *Drawing captured.*). It is held in the draft as a `Blob` and travels the
      outbox as a photo does, so it works in a draft and offline by the same path; the draft was tested, **an offline
      send of a drawing was not run**. **Accessibility:** a canvas cannot be drawn on from a keyboard, so the question says
      so in words before the canvas, *Drawing needs a mouse, a pen or a finger. It cannot be done with the keyboard alone.*,
      and the canvas is described by that sentence; it is not a promise of an alternative. **A required drawing therefore
      cannot be completed by a person who uses only a keyboard**, and the publisher should not make one required where such
      people fill the form in. `annotate` (drawing over a photo) stays refused. The designer lists *Drawing* beside
      *Signature*. The signature pad itself has no such statement and is unchanged. **INFERRED:** the canvas is 280
      pixels tall against the signature's 180, which Survey123's page does not say.
    - **(f) What still keeps a Survey123 form out.** The sole blockers after this item, from the same run: `or_other` (3
      forms), `autocomplete` (2) and one each of `esri:inputMask`, `spinner`, duplicate choices, a row with no name, an audio
      media column, the `grid` appearance, `pulldata("@javascript")`, a parameter on a question that takes none, and
      `annotate`, and one row that holds cells and no question type. Some of those are another branch's tonight, the others are left. **`pulldata("@layer")`, `@json` and `@javascript` stay refused**,
      each with its reason in the upload (`FormCatalog.RefusedPulldataSources`); `@exif` too. *How `@layer` could be
      built, written down and not built:* `pulldata("@layer", property, layerUrl, field, value)` is a query on a feature
      layer, `where field = value`, `outFields property`. Online, the client could send exactly that to the layer through
      `/rest/services/…/query`, as it already sends `applyEdits`, with the member's token, and a layer the form's audience
      may not read would refuse as it should. Offline is the whole difficulty: the answer has to be on the device already,
      so the form would name the layers it reads, and *Download for offline use* would keep the rows of each, bounded by a
      limit the publisher sets (the Inbox's rule, §5.9 (k)), as a CSV is kept today for the CSV form of `pulldata`, and an
      offline lookup would read that copy. That makes `@layer` a CSV `pulldata` fed from a layer, which is a smaller
      piece than the Inbox, and the one decision it needs from the owner is the row limit.
    - The service worker's shell cache is `graticula-forms-shell-v7`, because `fill.js`, `fill.css` and `xlsform-eval.js`
      changed; the other forms branch took v6.

21. **The two compatibility branches together, and a note with no name — 2026-11-15.** §5.19 and §5.20 were built in
    parallel and merged; measured on the merged checker with the same corpus: **42 of the 61 Survey123-authored forms
    import unedited (68%, from 17), and 59 of all 108 readable (55%, from 31).** One more change was made at the merge:
    **a `note` with no name and no calculation is given a name from its row (`note_row_{row}`) and a notice, instead of
    refused** — it stores nothing, and Esri's own published samples carry such rows. The workbook and the model keep it
    nameless. A note that calculates makes a field and is still refused without a name, as is every other question
    (`AFormImportsSurvey123SpellingsTests`). **INFERRED**, for the owner to confirm. What still blocks Survey123-authored
    forms, by the forms it blocks: `annotate` 4; `barcode` 2; `pulldata("@layer")` 2; `search()` 2; an attachment question
    given a field type 2; a `submission_url` naming a layer this server does not hold 2 (correctly refused: §5.17); and
    one form each for an unknown column, a field length out of range, `distress`, a calculating note with no name,
    `audio`, `bind::esri:warning`, `grid`, `pulldata("@javascript")` and a parameter on a question that takes none.
    The merge also set the shell cache to `graticula-forms-shell-v8` (the two branches had v6 and v7), and the page test
    that named a cache version now finds the shell cache by its prefix.

22. **The `barcode` question — 2026-11-16, by owner decision (*"Yap"*, after it was proposed as the next step).** §5.10
    refused `barcode` because *Safari has no barcode reader, so the question could not be answered on an iPhone*. That
    reason was about one way of answering, not about the question: a barcode answer is text, and text can be typed on every
    browser. So the refusal is lifted by making the box the answer everywhere and the camera an extra where the browser
    can read a code, and the iPhone case is protected by the box rather than by refusing the form.
    - **What Esri's public pages say, and what each settles.** *Barcodes*
      (`https://doc.arcgis.com/en/survey123/create/main/barcodes.htm`, now served at
      `https://doc.esri.com/en/arcgis-survey123/latest/create/main/barcodes.html`): *"Users select the barcode button to
      scan a barcode with the device's camera. The value read from the barcode is displayed in the text box."*;
      *"By setting the appearance column to minimal, a barcode question displays only the button to start the scanner. The
      text box that contains the barcode value is not shown."*; the `barcodeType` parameter goes in **`body::esri:style`**,
      and for the **web app** specifically: *"Barcode types cannot be manually selected in the Survey123 web app"*, *"The
      web app doesn't support using an expression to set the barcodeType parameter"*, and *"all barcode types will be
      accepted when barcodeType is set to all, 1d, or 2d."* The web designer's page
      (`https://doc.esri.com/en/arcgis-survey123/latest/create/web-designer/textnumberdatetime.html`), which is the web
      app's own question: *"The coded value can be manually entered if it is human readable"*, and the list of formats
      *"cannot be limited by the survey author in the web designer or by the respondent in the web app."* *XLSForm
      parameters* (`…/create/main/xlsformparameters.html`): `barcodeType` and `placeholderText` (both in
      `body::esri:style`) are the only parameters that name barcode; `calculationMode` is **not** allowed on it. *Question
      types overview* (`…/create/main/question-types-overview.html`): *"Scan a barcode or QR code."* **None of these pages
      gives the field type or length a barcode question makes, or says which browsers the web app scans in.** ODK's
      *Question types* (`https://docs.getodk.org/form-question-types/`, Barcode): *"Scans, decodes, and captures the
      content of a barcode, using the device camera"*, with the appearances `front` (the front camera) and
      `hidden-answer` (the value read is not shown, for long or encoded values).
    - **The server.** `barcode` is an input type (`FormCatalog.InputTypes`) and off `RefusedTypes`. It makes a **text
      field of 255** (`FormLayerPlan.DefaultTextLength`, the length every text field here gets, §5.9 (d)), and
      `bind::esri:fieldLength` sets another length (1–10000) as it does for `text`. Appearances: **`minimal`** (Survey123's)
      and **`hidden-answer`** (ODK's, the same meaning) are supported on `barcode`; `hidden` and the presentation appearances
      behave as on any question; `front` is refused as unknown and `new-front`/`new-rear` stay refused (*choosing the camera
      is not in the first version*); `multiline` is refused (*applies to file, image, text*). `body::esri:style` is accepted
      with its existing notice (*changes only how questions look and is drawn the default way*), which for `barcodeType`
      is true of the web app as Esri describes it and for `placeholderText` is true here. The `parameters` column is refused
      (*'barcode' takes no parameters*), as is `body::esri:inputMask` (*belongs on a text question*) and `calculationMode`
      (Survey123's list leaves `barcode` out). A calculation, a constraint, `required`, `default`, `read_only` and
      `relevant` work as on `text`, and a barcode's answer reads as a string in another question's expression. The designer
      offers **Barcode** on the Text shelf, and lists `minimal` and `hidden-answer` for it.
    - **The client** (`fill.js`, `barcodeWidget`). The answer is **always a text box** (`autocomplete`, `autocapitalize`
      and `spellcheck` off), labelled by the question, so a code is typed on an iPhone, on a desktop and on any browser.
      The page asks once whether the browser has a reader: `BarcodeDetector` exists, `BarcodeDetector.getSupportedFormats()`
      gives **at least one format**, and `navigator.mediaDevices.getUserMedia` exists (it does not on a page that is not
      HTTPS). **Where it has one**, a **Scan** button (described by the question's label) opens the camera with
      `facingMode: { ideal: 'environment' }` and no audio, shows the live preview (labelled *Camera preview*) with a **Cancel
      scan** button, which takes focus, and asks the reader every 200 ms with every format the browser offers. The first
      non-empty value read goes into the box, becomes the answer, is announced (*Code read.*) and focus goes to the box.
      **The camera's tracks are stopped** on a value, on Cancel (focus back to Scan), on `pagehide` and on the page being
      hidden (`visibilitychange`), when a redraw has removed the preview, when another barcode question starts scanning, when
      the question becomes read-only, and on every error; only one camera is ever open. **Where it has none**, there is no
      Scan button, and a line under the box says *This browser cannot scan codes. Type the code instead.*, which the box is
      described by. **Failures are said in words** in the question's polite live region, and each says what to do: *Camera
      permission was refused.*, *No camera was found.* (`NotFoundError`, `OverconstrainedError`), *The camera could not be
      started.* (anything else, including the camera being in use), *The code could not be read.* (the reader failed),
      each followed by *Type the code instead.* and focus on the box. A message survives a redraw of the question.
      `minimal` and `hidden-answer` hide the box **only where there is a reader**; without one, and after the camera has
      failed for that question, the box is shown, because it is then the only way to answer. Every string is in the Turkish
      table. The preview is in `fill.css` (as wide as the question, at most half the screen high).
      *(Amended 2026-11-17: "a reader" is now any page with a camera API — the browser's own where it has one, ZXing
      otherwise — and the no-reader hint is left for pages with no camera API; see the last item of this section.)*
    - **Offline.** Decoding is the browser's, on the device, and the camera needs no network, so scanning works with no
      signal exactly as it does online; the answer is text and goes into the draft and the outbox as any text does. Nothing
      is downloaded to scan. Where the browser has no reader, offline or not, the code is typed. *(Amended 2026-11-17:
      where ZXing reads, it is downloaded once, with the page's shell, and kept by the service worker; see the last item
      of this section.)*
    - **The headers.** The form pages' `Content-Security-Policy` (`SecurityHeaders.FormsPolicy`) was read and **left
      unchanged**: a camera stream is given to the `<video>` as `srcObject`, which is not a URL fetch and so not governed
      by `media-src`, and the reader is a browser API, not a script source. **This server sends no `Permissions-Policy`
      header on any path** (searched across `src/`), so the camera keeps the browser default, which allows it for the page's
      own origin, and the designer's same-origin preview frame inherits the same. A reverse proxy in front of the server
      that adds `Permissions-Policy: camera=()` would remove the Scan button's camera; the client then says *The camera
      could not be started* or *permission was refused*, and the box still works. `getUserMedia` needs a secure context,
      which a deployment already is (HTTPS) and `localhost` is.
    - ~~**No decoding library.** None is carried, by the brief and by the build-vs-adopt policy. *Open point for the owner,
      not decided here:* Safari on iPhone and iPad, Firefox, and Chrome on Windows and Linux have **no `BarcodeDetector`**
      (MDN's compatibility table, as known to this session and not measured here, has it in Chrome and Edge on Android, ChromeOS and macOS only), so on those a person types the code. If the
      owner wants scanning on an iPhone, the only route a web client has is a decoding library run in the page on camera
      frames (for example ZXing's JavaScript port, Apache-2.0); that is a Tier 2 adoption under
      [build-vs-adopt-policy.md](../build-vs-adopt-policy.md), behind our own port, and it is a separate decision with its
      own size and licence record.~~ **Decided by the owner 2026-11-17** (*"ZXing'i benimse"*): ZXing's JavaScript port is
      adopted, behind our own port, with its size and licence record in `DEPENDENCY-LICENSES.md` — the last item of this
      section.
    - **Tests.** Server: `AFormTakesABarcodeQuestionTests` (accepted and off the refused list; text field of 255 and of the
      given length; stored as text or not at all, a numeric field type refused; `minimal` and `hidden-answer` accepted and
      sent to the client, `hidden-answer` refused off a barcode; `barcodeType` accepted with a notice; `front`, `new-front`,
      `parameters`, an input mask, `calculationMode` and `multiline` refused in words; another question reads it).
      `AFormIsCheckedAgainstTheFirstVersionsListTests` and the conformance refusal test name another refused type in its
      place. Client, in headless Chrome with the reader and the camera stubbed before the page's scripts run
      (`FormFillBarcodeTests`): with no reader there is no Scan button, the hint is shown and describes the box, a `minimal`
      question shows its box, and typed codes are sent; with a reader, Scan asks for the rear camera without audio and for
      the reader's formats, the code read is in the box, announced, focused and sent, and the one camera track is stopped;
      Cancel, `pagehide` and a refused camera each stop the camera and say so, and a refused camera shows a `minimal`
      question's box; the no-reader hint is Turkish in a Turkish browser.
    - **The corpus.** Measured with `AFormCorpusTests` on the same 110 workbooks (108 readable), against the last full report: **60 of the 108 import unedited (from 59), and 43 of the 61 Survey123-authored forms (from 42).** The one form this item unblocks is `esri-Samples_Barcode_Scanning.xlsx` (a plain barcode and a `minimal` one). `esri-Samples_001_Question_Types.xlsx` is still refused, now only for a `bind::esri:fieldLength` of `null` on another row, which is not this item's. The pyxform fixtures and the ODK Web Forms all-question-types workbook no longer name `barcode` or `hidden-answer` among their refusals and stay refused for their other reasons (`public_key`, audio in a label, `simserial`, `phonenumber` and others).
    - **INFERRED, for the owner to confirm:** (1) the default length is **255**, the length every text field here gets,
      because no Esri page states a barcode's field type or length; (2) a barcode is stored only as `esriFieldTypeString`
      or not at all, and a numeric, date or GUID field type is refused, because a code with leading zeros (an EAN, a UPC)
      would change; Survey123's pages do not say; (3) `minimal` and `hidden-answer` hide the box only where there is a
      reader, and a failed camera shows it, where Survey123 hides it always (its field app always has a scanner); (4)
      `barcodeType` is accepted and not enforced: every format the browser reads is read, which is what Esri says its web
      app does for `all`, `1d` and `2d`, and a specific list is not narrowed — the page does not say what the web app does
      with one; (5) the first value read is taken, with no confirmation step, as Survey123 puts *the value read* in the box
      and the person can still edit it; (6) ODK's `front` is not built, because the brief asked for the rear camera, and is
      refused as unknown rather than accepted and ignored.
    - The service worker's shell cache is not changed here (`fill.js` and `fill.css` changed); the merge sets it.
    - **ZXing, where the browser has no reader — owner decision 2026-11-17** (*"ZXing'i benimse"*, the choice the open
      point above asked for). A barcode question now **scans with the camera on every page that has a camera API**, not
      only where the browser has `BarcodeDetector`: Safari on iPhone and iPad above all, and Firefox and Chrome on Windows
      and Linux.
      - **What was adopted.** `@zxing/library` **0.23.0**, the JavaScript port of ZXing, **Apache-2.0**, its build
        `umd/index.min.js` copied byte for byte to `wwwroot/forms/zxing.min.js` (**362,150 bytes, 108 KB gzipped**). Fetched
        from `registry.npmjs.org` only, into an empty directory, and checked against the registry's published `sha512`
        integrity (matched); no install script was run. The package ships no smaller decoder-only build — one UMD bundle
        holds every reader and writer, and its ES-module builds would need a bundler this project does not run. It bundles
        `ts-custom-error` 3.3.1 (MIT), verified the same way. The row in `DEPENDENCY-LICENSES.md` holds the record and
        `NOTICE` the two licence texts in full, following OpenLayers and qrcode-generator. Its README says the project is
        **in maintenance mode**, which is the reason for the port below rather than a reason against it: the decoders are a
        port of ZXing's long-standing Java ones.
      - **Our port** (`wwwroot/forms/barcode-reader.js`, Tier 2 under
        [build-vs-adopt-policy.md](../build-vs-adopt-policy.md)): `GraticulaBarcode.reader()` answers a promise of `null`
        where the page has no `navigator.mediaDevices.getUserMedia`, and otherwise a reader with `engine` (`browser` or
        `zxing`), `formats`, `prepare()` and `detect(videoOrCanvas)` → the first code's text or `null`. The browser's own
        `BarcodeDetector` is used wherever it exists and gives a format, so Android Chrome is unchanged; ZXing everywhere
        else, with thirteen formats (Aztec, Codabar, Code 39, 93 and 128, Data Matrix, EAN-8, EAN-13, ITF, PDF417, QR,
        UPC-A, UPC-E). `fill.js` talks only to the port and never names a ZXing type (a test checks the file). ZXing reads
        **a snapshot of the live video every 200 ms**, drawn to a canvas of at most 1280 px on its longer side.
      - **Loaded lazily.** `zxing.min.js` is fetched only when **Scan is pressed on a browser without a reader of its
        own** — at the same moment the camera is asked for — so a form without a barcode question, and every browser with
        `BarcodeDetector`, never fetches it. If it cannot be loaded the camera is stopped and the question says *The code
        reader could not be loaded. Connect once to download it, or type the code instead.*, the box is shown and takes
        focus; a later Scan tries again.
      - **Offline.** The port and the library are in the service worker's shell list beside `ol.js`, so they are kept
        when the worker installs — the visit that makes "Download for offline use" possible — and a form opened with no
        signal scans with ZXing exactly as online. Measured with the stand-in server stopped. The cost is that **every
        device that opens a form downloads 108 KB more (gzipped) once, into its cache**, whether or not the form has a
        barcode; that was chosen over fetching it only on the first scan, which would leave a device that never scanned
        online unable to scan offline. The `SHELL_CACHE` name is not changed here; the merge sets it.
      - **Where Scan appears now.** Wherever `getUserMedia` exists. Where it does not, the hint stays, and says why when
        it can: on a page that is not a secure context (an `http://` address other than `localhost`, where browsers give
        no camera API at all, iPhone Safari included) it reads *Scanning needs the form to be opened at an https address.
        Type the code instead.*; elsewhere *This browser cannot scan codes. Type the code instead.* A deployment is HTTPS,
        so the first is a misconfiguration's message rather than a user's.
      - **iPhone.** The preview `<video>` already had `playsinline` and `muted` (Safari plays a camera inline and
        autoplays only with both), and the camera is stopped on a value, Cancel, `pagehide`, the page being hidden and
        every error, as before. **Not measured on an iPhone** — this session has none; the headless Chrome tests below
        remove `BarcodeDetector`, which is the condition iPhone Safari is in, but not Safari's camera or its speed.
      - **The headers.** `SecurityHeaders.FormsPolicy` is unchanged: the library is a script from this origin
        (`script-src 'self'`), and it contains no `eval` and no `new Function` (searched, and checked by the test), so no
        `unsafe-eval` is needed. Reading a canvas the page drew from its own camera taints nothing.
      - **Tests** (`FormFillBarcodeTests`). With `BarcodeDetector` removed and the camera a canvas showing a real QR code
        drawn by the vendored `qrcode.js`: the Scan button appears, ZXing is not in the page before Scan and is after it,
        the code is read into the box, announced and sent, the one camera track is stopped, a second scan reuses the one
        script; a library that cannot be fetched stops the camera and says so; the port and the library are in the
        service worker's cache and a form opened with the server stopped still scans; the vendored file's SHA-256 is the
        pinned one and it evaluates no strings; a page that is not a secure context gives the https hint. The earlier
        no-reader tests now remove the camera API as well, since a camera alone now means a Scan button.
        `FormFillPageTests`' offline test also checks the real server's cache holds both files.
      - **INFERRED, for the owner to confirm:** (7) the library is part of the offline shell for every form, as `ol.js`
        is, rather than fetched only by forms with a barcode question — 108 KB gzipped per device, once; (8) the browser's
        own reader is preferred wherever it exists, and ZXing is never used beside it, because it is faster and costs no
        download; (9) ZXing reads at most 1280 px of each frame and does not run its slower "try harder" mode, to keep a
        200 ms cadence on a phone — a code that is hard to read is typed; (10) a library that fails to load is said as its
        own message, distinct from a camera failure, because what helps (a signal) is different.

23. **`search()` and `pulldata("@layer")` — built 2026-11-16, by owner decision (*"Yap"*).** §5.10 refused both: `search()`
    because *"dynamic choices from a CSV are not in the first version"*, `@layer` because *"an offline client would need a
    copy of that layer on the device"*. The owner lifted both. Learned from public pages only, read that day, and no
    Esri-written file is in the repository:
    [xlsform.org, *Dynamic selects from pre-loaded data*](https://xlsform.org/en/#dynamic-selects-from-pre-loaded-data);
    [Survey123, *Multiple choice* — *Create a dynamic choice list using the search appearance*](https://doc.esri.com/en/arcgis-survey123/latest/create/main/multiplechoice.html);
    [Survey123, *Formulas* — *Query a feature layer* and its *Request parameters*](https://doc.esri.com/en/arcgis-survey123/latest/create/main/xlsformformulas.html);
    and, for offline only, which neither Survey123 page covers, Esri's own public blog posts
    [*Dynamic Choice Lists using Search Appearance*](https://community.esri.com/t5/arcgis-survey123-blog/dynamic-choice-lists-using-search-appearance/m-p/1060838)
    and [*Survey123 Tricks of the Trade: pulldata("@layer")*](https://community.esri.com/t5/arcgis-survey123-blog/survey123-tricks-of-the-trade-pulldata-quot-layer/m-p/1224415).
    - **What the sources say.** *xlsform.org:* `search()` goes in the appearance column, after a style such as `quick`
      if there is one; the list on the choices sheet has one row whose `name` is the CSV column that identifies a choice
      and whose `label` is the column shown — *"a comma-separated list of all columns"* for several — and an `image`
      column may name the column that holds a picture's file name; `search('csv')` offers *"all distinct rows"*,
      `contains`, `startswith`, `endswith` and `matches` (*"exactly"*) narrow by a column, and *"two extra parameters"*
      add a filter column and its exact value; a numeric `sortby` column orders the choices, otherwise they keep the
      file's order. *Survey123, Multiple choice:* select_one and select_multiple support it, written
      `search(tableName, searchType, searchColumn, searchText, filterColumn, filterText)`; the table is the CSV in the
      survey's media folder *"or the URL of a feature layer … preceded by a name"* — `"airports?url=…/FeatureServer/0&resultRecordCount=10"`
      — and the choices row then names fields; a `where=` may be appended; a non-spatial table takes the four text types,
      a spatial one `intersects`, `contains`, `crosses`, `envelope_intersects`, `index_intercepts` (sic), `overlaps`,
      `touches` and `within`; parameters with special characters *"must be encoded"* for the web app, where such a
      question is drawn like one with `autocomplete`. *Survey123, Query a feature layer:* `getRecord(url[, where])`,
      `getValue(property, url[, where])`, `getRecordAt(url, location[, where])`, `getValueAt(property, url, location[, where])`;
      the property is a path such as `"attributes.COUNTRY"` or `"geometry"`; the location *"must be a geopoint
      question"*; without a where clause *"a default of "1=1" is used"*; the first record of the answer is used; request
      parameters follow the URL after `?` (`orderByFields`, `resultOffset`, `outStatistics`), except `f`, `outFields`,
      `outSR`, `resultRecordCount`, `returnCountOnly`, `returnGeometry`, `returnIDsOnly` and `token`; answers are
      cached. *The blog posts:* a layer list is not populated offline, and `pulldata("@layer")` *"only works while
      online"*; a layer the person cannot reach gives an empty output.
    - **Server.** `FormLayerQuery` reads `search(…)` out of the appearance cell (the rest of the cell is still checked
      token by token, so `quick`, `minimal` and `autocomplete` mean what they meant); the question carries it as
      `Search`, the client is sent it read (`search`: file or address, parameters, type, column, text, filter), and the
      list's row makes no coded-value domain, because it names columns and not answers. `pulldata("@layer")` is checked
      by operation, number of arguments, property (`attributes.<field>` or `geometry`), a geopoint question for the
      location, and its address. Refused, at the row and column with the reason: `search()` on a question without
      choices or on a `*_from_file` one; 2, 3 or 5 arguments; an unquoted table, type or column; an unknown type; a
      spatial type over a CSV; a CSV named with a folder or an address; two `search()`s in one cell; a layer list with
      no row; `or_other` on a `search()` list; an address that is not `…/FeatureServer/{n}`; a parameter Survey123
      excludes (each with Survey123's reason, `token` with this server's) or one not on the list a form may add.
      **At upload, at check and at every publish** each address the form writes out is checked as a `submission_url`
      is (§5.17): on the host and port that answered the request, there in the catalogue, and readable by the person
      publishing — a layer they cannot read is answered as one that is not there, as the REST API answers it
      (`FormEndpoints.QueriedLayerProblemsAsync`, measured against PostGIS in `AFormQueriesOnlyThisServersLayersTests`).
    - **Client.** A CSV `search()` reads the CSV the form already fetches and keeps on the device with its other files
      (`csvNamesOf` now lists it), so it works offline with no change to the service worker: matched, filtered, ordered
      by `sortby`, each name once, every CSV column on the choice so that a `choice_filter` reads it (the corpus has
      `not(selected(${main_activities}, name))` on a `search()` list). A layer `search()` asks the layer's description once
      (for its field types, so a number is compared as a number) and then `…/query` with `outFields=*`, the search as
      `UPPER(column) LIKE '%TEXT%'` (or `column = value` for `matches`, several columns joined by `OR`), the filter as
      `column = value`, the form's own `where` and parameters, and for a spatial type the geopoint, geotrace or geoshape
      answer as the geometry. A select_one with `search()` is the type-ahead box unless it says `minimal`.
      `pulldata("@layer")` goes through the evaluator's `ctx.layer`: the evaluator stays synchronous, the page answers
      from what it has fetched for that exact request and '' until then, starts the request, and recalculates when it
      arrives; a calculation whose inputs changed makes a new request, so it re-runs as its inputs change. A question
      whose calculation queries a layer says under its answer, in a polite live region, *Looking this up…*, *needs a
      connection*, *could not be read* or *Nothing was found*. Every query is a GET with the member's own token, to
      `…/rest/services/…/FeatureServer/{n}` on the page's own origin and nothing else — an address worked out at run
      time that is anything else is never fetched and is said — so `connect-src 'self'` is unchanged and no endpoint
      was added. Words are in English and Turkish.
    - **Offline.** The CSV kind works with no signal (tested: the page reloaded from the device with the server stopped).
      The layer kind, as Survey123's does, does not: the list says *"These choices come from the server and need a
      connection. You can still submit the form; this question can be answered later."* and the calculation says it
      needs a connection; nothing throws, and the form submits into the outbox (tested with the server stopped). A
      request that failed for want of the network is asked again when the browser says it is back online, or after
      15 seconds.
    - **Corpus** (`AFormCorpusTests`, same 110 workbooks, against the last full report): **accepted unedited 59 → 62**,
      refused 49 → 46, server-accepted expressions 1418 → 1434 of 1441. Unblocked: `stats4sd-Data_Confirmation_Example`,
      `stats4sd-Google_Maps_Link_Example`, `stats4sd-Multiple_select_choice_filter_example` (their CSVs are not in the
      corpus; until the CSV is added to the form's files the question says the list is not available yet, as a
      `select_one_from_file` does). Still refused, for other reasons: the two `laff-surveys_Apopka_Quarterly_*_PM_Inspections` forms (their
      `submission_url` is an item address, not a layer's, and an `image` question is given a field type; their
      `search()` and `pulldata("@layer")` now pass the shape check, and name `services.arcgis.com`, which publish
      refuses as another server's); `eddie-odk_simprints_verification_demo.xls` (`trigger`); and
      `stats4sd-Repeat_over_not_selcted_options_2` (prose written in its appearance column).
    - **INFERRED, confirmed by the owner 2026-11-17 (§5.25):** (a) **only this server's layers are queried** — a relative address or this
      server's own host, checked at upload and publish, and by the client for an address worked out at run time; a
      layer elsewhere is refused in words, because the page's `connect-src` is `'self'` and a member's token must not
      go to another host. (b) The query goes with the member's own sign-in, so an anonymous public form sees only
      public layers and a member only what they may read; a layer the publisher cannot read is refused at publish.
      (c) An address written as `concat("<address>", '?…')` is checked for its written part; one in a question
      (`${url}`) is accepted and left to the client. (d) With no network a layer-list question is not required, so the
      record can be sent and completed from the Inbox later. (e) An empty search text narrows nothing (every row), for
      every type, as a community report says Survey123's layer list behaves; Survey123's pages do not say. (f) `contains`,
      `startswith` and `endswith` ignore case (and, over a CSV, accents and the Turkish dotted i, as the type-ahead
      does); `matches` is exact. (g) Several label columns are joined with a space. (h) Rows with the same name are
      offered once, the first kept (xlsform.org says *distinct*). (i) `contains` over a layer is spatial when the layer
      has geometry and the search text is a location. (j) Nothing found, or a layer that cannot be read, is the empty
      answer, and the question says which. (k) A `geometry` answered into a geopoint question is `latitude longitude`;
      anywhere else it, and `getRecord`'s record, is JSON text (`pulldata("@json")`, which reads it, is still refused).
      (l) The parameters a `search()` address may carry are `where`, `orderByFields`, `resultRecordCount`,
      `resultOffset`, `returnDistinctValues`, `distance`, `units` and `time`; a `pulldata("@layer")` address,
      `where`, `orderByFields`, `resultOffset`, `outStatistics`, `groupByFieldsForStatistics`, `having`, `distance`,
      `units`, `time`, `returnDistinctValues`, `spatialRel` and `objectIds`; any other is refused by name. (m) A map
      service layer (`…/MapServer/{n}`), which Survey123 also queries, is refused: only feature layers are. (n)
      `pulldata("@property", "submissionUrl")` stays refused, with a new reason: answers here go to a view made for
      the form when it is shared, not to the layer, so a query written with it would read the view.

24. **Drawing over a photo: the `annotate` appearance — built 2026-11-16, by owner decision** (*"Yap"*, the same day).
    Until this item `annotate` was refused at upload (*"annotating a photo is not in the first version"*, §5.10). It is
    now accepted on an `image` question and built in the client, on the drawing of §5.20 (e). Read 2026-11-16, and
    nothing from Survey123's code or any Esri file:
    - Survey123's appearance page (`doc.esri.com/en/arcgis-survey123/latest/create/main/xlsformsappearance.html`):
      `annotate` *"allows the user to open a canvas window on which to sketch that also supports annotation on images"*,
      on `image`, listed for both the web app and the field app.
    - Survey123's media page (`…/create/main/xlsformmedia.html`): with `annotate` *"an image question displays a button to
      open the device's camera to take a photo that you can annotate with graphics and text"*; `method=camera`,
      `browse` or `map` in `body::esri:style` choose the source; **`max-pixels` on `annotate` sets "the longest side of
      the image" and keeps its aspect ratio** (on `draw`, a square canvas of that side); with no `max-pixels`, *"the
      default image size is 1280 pixels"*; a default image *"is replaced by a provided image"*; from 3.13 *"EXIF data is
      copied from the source image to the annotated image"*. The same page says draw and annotate are *"only available
      in Survey123 Connect and the Survey123 field app"* and that *"regardless of the method set, the Survey123 web app
      only displays a browse for file option"* — which disagrees with the appearance page's web-app column. Recorded, not
      resolved: this client draws, because the owner asked for annotation and a browse button that cannot annotate is
      the `annotate` refusal under another name.
    - Survey123's field-app page (`…/capture/field-app/drawannotate.html`): eight tools (freehand, smart shape, marker,
      label, line, polyline, arrow, double arrow), colour, width and opacity per tool, undo and redo, a **Reset** that
      *"deletes all graphic elements and removes the image"*, and *"once the image is saved and reopened, these graphic
      elements can no longer be manipulated individually"*. It does not say whether the unannotated photo is kept or
      sent, nor the saved format. Survey123's palettes page (`…/create/connect/drawannotatepalettes.html`): custom
      palettes (`palette=` in `body::esri:style`) *"are not supported in the Survey123 web app"*.
    - ODK's question-types page (`docs.getodk.org/form-question-types/`): *"Adding the annotate appearance allows the
      user to draw on the image before submitting it"*; *"Collect renames images after annotation"*; `max-pixels` —
      *"the image is resized proportionally so that the long edge matches the provided pixel value"*, scaled JPEGs saved
      at 80 % quality.

    **What was built.**
    - **(a) The server.** `annotate` moves from `FormCatalog.RefusedAppearances` to `SupportedAppearances`, on `image`
      only; on any other type it is refused in words (*"The appearance 'annotate' applies to image, not to 'text'"*), as
      `draw` is. Refused in words where this client would half-build it: `multiline annotate` (Survey123's media page
      allows it; this client annotates one photo per question), and `annotate` beside `draw` or `signature`. **A
      `default` on an `image` question is now refused**, with any appearance: Survey123 uses it as a picture from the
      media folder to draw over, which is not built, and until today the client took it as the answer's text, a file
      name with no file. No form in the corpus has one. `max-pixels` was already accepted on `image` and reaches the
      client unchanged. `body::esri:style` stays a column drawn the default way with a notice (§5.10), so `method=` and
      `palette=` change nothing, which is what Survey123's own web app does with them.
    - **(b) The client** (`annotateWidget` in `fill.js`). A file input (`accept="image/*"`, no `capture`, so a phone's
      chooser offers both the camera and the gallery) with words under it: *Choose or take a photo, then draw on it if
      you need to. The photo can be sent as it is.* **Choosing the photo answers the question at once**; the drawing pad
      then opens over it, the photo scaled to fit the screen while the canvas keeps the photo's own pixels. A pen of five
      colours and three widths (both `<select>`s with labels), **Undo** (the last line), **Clear drawing** (every line;
      the photo is as it was) and **Done drawing**. Every line is saved as it ends, so the answer is always the picture as
      it stands: the photo alone while nothing is drawn, one flattened JPEG (quality 0.9, named after the photo, `.jpg`)
      once something is. Done closes the pad and puts focus on **Draw on the photo**, which reopens it; **Remove photo**
      empties the answer and puts focus on the input; choosing again replaces the photo and starts with no lines. The
      session (photo, lines, pad open) is kept by record and question for the page's life, so a structural redraw of the
      page does not lose lines or Undo. A polite live region says each result (*Photo attached…*, ~~*Line saved on the
      photo.*~~ — silent since 2026-11-17, §5.25 —, *Last line undone.*, *Drawing cleared. The photo is as it was.*, *Photo removed.*). Every new string has
      its Turkish in the same table.
    - **(c) `max-pixels`, and a correction.** The photo is held to `max-pixels` on its long edge, or to **1280** when the
      form sets none — Survey123's documented default. The `<img>` is read for its size first (its header only), then
      decoded at the small size with `createImageBitmap`'s resize options, falling back to drawing the `<img>` scaled; a
      48-megapixel phone photo is therefore drawn on as about 1.2 megapixels (1280 × 960 × 4 bytes ≈ 5 MB of canvas)
      and never held as a full-size canvas by this code. **The plain `image` question's `max-pixels` was wrong**: the
      client read it as a total number of pixels, so `max-pixels=1280` shrank a photo to about 36 × 36. Both public pages
      say long edge; it now means long edge there too. The plain photo keeps no default limit, as before.
    - **(d) Offline and drafts.** Nothing here needs the network. The answer is an ordinary file entry (`name`, `type`,
      `size`, `blob`) set through `setFiles`, so it is kept in the draft as a `Blob` and queued in the outbox with the
      record exactly as a photo is (§5.5, §5.10), and a reloaded draft shows it and reopens it for more lines.
    - **(e) Keyboard and screen-reader users.** Drawing is pointer-only by nature, and the pad says so in words before
      the canvas (§5.20 (e)'s sentence), the canvas described by it. **The question does not need the drawing**: choosing
      a photo answers it, so a required `annotate` question can be completed from the keyboard, which a required `draw`
      cannot. Every control is a native input, select or button with a visible name, reached by Tab; focus is moved
      deliberately where a button disappears or disables itself (Undo of the last line and Clear move it to the canvas).
    - **(f) A file the browser cannot read as an image** (HEIC in Chrome, for one) is attached as it is, and the client
      says so: *This photo could not be opened for drawing. It is attached as it is.* The preview of such a file is
      hidden rather than shown broken.

    **Measured** — the server's checker (`AFormAnnotatesAPhotoTests`, 8 cases), the client in headless Chrome against
    the stand-in server (`FormFillAnnotateTests`, 4: a photo set on the input opens the pad at 800 × 400 for a 2000 × 1000
    photo under `max-pixels=800` and at 1280 × 640 with none; a stroke drawn with synthesized mouse events turns the
    canvas's centre from the photo's green to the pen's red; Undo and Clear return it to green, in the pad and in the
    draft; Done leaves one JPEG; the drafted drawing comes back after a reload and reopens; the photo alone, with nothing
    drawn, is queued in the outbox as a JPEG `Blob` with the server stopped; an unreadable file is attached and said).
    **Nothing was measured on a phone**: the memory of the decode on a real 48-megapixel photo, a finger on a real touch
    screen, and a screen reader on the pad are unmeasured.

    **The count, from the corpus, run before and after on this branch:** all readable workbooks importing unedited,
    **59 of 108 before, 60 after**. `sailing-DrainageStructureStreamCrossingSurvey.xlsx` comes in. The others that used
    `annotate` are held by something else: `esri-Samples_002_Appearance.xlsx` by `distress`;
    `sailing-RapidStormDamageAssestment.xlsx` by a choice with no name, a `.bmp` picture and two questions with no label;
    the ODK web-forms all-question-types workbook by many ODK-only appearances; and **`sailing-RoadLogs.xlsx` by its own
    `annotate` on a `select_one`** (a help question), which is refused as `draw` would be. Whether Survey123 ignores
    `annotate` there, as it ignores `minimal` off its types (§5.19 (2)), no public page says; if the owner wants it
    ignored with a notice, adding `annotate` to `FormCatalog.PresentationOnOtherTypes` brings that form in.

    **INFERRED, confirmed by the owner 2026-11-17 (§5.25):** (1) the client draws in the web form although Survey123's media page says its
    web app offers only a file to browse for (the appearance page says otherwise); (2) **the unannotated photo is not
    uploaded beside the drawing** — the question holds one picture, the drawn one; no public page says what Survey123
    sends; (3) the photo alone is the answer until something is drawn, so an annotate question can be answered without
    drawing; (4) lines are saved as they end, and Done only closes the pad, where the field app saves when the pad is
    left; (5) the tools are a freehand pen of five colours (red, the default, yellow, blue, black, white) and three widths
    (2, 5 and 10 screen pixels, scaled to the photo), with Undo and Clear; Survey123's shapes, arrows, markers, labels,
    opacity and redo are not built; (6) the flattened picture is a JPEG at quality 0.9, named after the chosen photo with
    `.jpg`; Survey123 names no format and ODK says only that Collect renames; (7) **the EXIF of the photo is not copied
    to the drawn picture** (a canvas writes none), where Survey123 3.13 copies it; the photo sent unscaled and undrawn
    keeps its own; (8) reopened after a reload, the drawn picture is the new ground and its earlier lines can no longer
    be undone, as Survey123's field-app page says of a saved image; within one page's life Undo reaches back across Done;
    (9) the 1280 default applies to `annotate` only, not to a plain `image`, whose behaviour without `max-pixels` is
    unchanged; (10) `max-size` (Survey123's web-app file-size limit) stays refused as a parameter, as before.

    The service worker's shell cache is not changed here: `fill.js` and `fill.css` changed, and the merge sets the
    version.

25. **The owner's answers on §5.22–§5.24 — 2026-11-17** ([Q-236](../open-questions.md)). Four choices were put
    to the owner together, and each was answered:
    - **Barcode on browsers with no reader: adopt ZXing** (*"ZXing'i benimse"*). §5.22 left iPhone Safari, Firefox and
      desktop Chrome with a typed box only. A JavaScript port of ZXing is adopted as a Tier 2 library behind the forms
      client's own barcode port, vendored and loaded only when it is needed; the version, the licence and the
      verification are recorded in `DEPENDENCY-LICENSES.md` and in §5.22 by the change that adopts it.
    - **search() and pulldata("@layer") query this server's layers only** (*"Yalnız bu sunucu"*): §5.23 (a) and (b)
      stand. A form naming another server's layer is refused at upload in words, which keeps the two Apopka corpus
      forms out (they name `services.arcgis.com`).
    - **`annotate` on a question that is not an image is ignored, with a notice** (*"Yok say, uyarı göster"*), instead
      of refused. No public page says what Survey123 does with it there, and `sailing-RoadLogs.xlsx` carries it on a
      `select_one`. The `multiline`, `draw` and `signature` combinations on an image stay refused (§5.24).
    - **Every other INFERRED point in §5.22, §5.23 and §5.24 is confirmed as written, with the two changes the UX
      review of 2026-11-16 asked for** (*"Yazıldığı gibi, ux'in ikisi de olsun"*): **a line drawn on a photo is saved
      without an announcement** — *Done drawing* says the result, and a status after every stroke was noise to the
      people who hear it, who are mostly not drawing; and **a record sent with a required search() list left empty
      because its layer needed a connection (§5.23 (d)) says so in the Outbox** — *Required, but left empty because it
      needed a connection:* and the questions' labels, kept on the queued record, so the member knows what to complete
      from the Inbox once it has been sent.

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
- **The offline Inbox makes this a sync, not only a queue.** Downloading records, editing them on a device for days
  and sending them back brings in conflicts, deletions made while a device was away, and attachments replaced on both
  sides. §5.6's rule keeps it honest, but it is the hardest part of the client to get right and to test.
- Field Maps and Pro still cannot edit offline against this server. The Inbox serves Graticula's own client only.
- The browser client takes a dependency on service workers and IndexedDB, which this repository has not used before.

**State.** No new table is promised here. The form is an item and one item resource, its JSON model; its layer, domains, relationship
and view are those that already exist. The device holds the queue and the downloaded Inbox; the server holds nothing
per device, and finds a conflict from the record's own last-edited time, not from a register of devices.

**Ports created.** None. Reading the workbook goes through the import reader's existing seam.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-088 | A field crew can install the form client to the home screen, or uses Android, so that the device keeps its queue | `UNVALIDATED` — new with this ADR; condition 2 |
| A-089 | Most forms an organization brings from Survey123 use the common XLSForm subset of condition 3 rather than Esri-only functions | `INVALIDATED` 2026-11-15 — 17 of 61 Survey123-authored forms import unedited (condition 5, §5.18); the register holds the detail |

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

1. **The owner confirms §5.9's INFERRED points**, (a) to (k), through [Q-229](../open-questions.md).
   ~~**PARTLY DISCHARGED 2026-11-08:** (a) confirmed, (e) decided the other way (§5.6). The rest are open.~~
   **DISCHARGED 2026-11-09:** every point answered (§5.9's header); (b) changed for nested repeats, (e) the other way.
2. **Offline is measured on a device before it is called supported.** On Android in Chrome (the emulator is enough):
   fill ten records with photos in flight mode, close the browser, restart the device, restore the signal, and every
   record arrives once. On an iPhone in Safari, installed to the home screen: the same, and the queue survives eight
   days unopened. The iPhone half needs a device the owner supplies; until then the client says *offline on iPhone
   is not yet verified* where it offers offline use. **PARTLY DISCHARGED 2026-11-16 — the Android half, measured on the emulator:** Chrome 124 on the
   `FieldMaps_API35` image (Android 15), the form downloaded for offline use by a member signed in with *Keep me signed
   in*. Flight mode on **and** the `adb reverse` tunnel removed, because the tunnel reaches the server through adb and
   would have carried requests that flight mode alone does not stop; the page read `navigator.onLine` false and a fetch
   to the server failed. Ten records filled, each with a 0.94 MB JPEG; the server held none. Chrome force-stopped, the
   device rebooted with flight mode still on, the signal restored, the form opened: *10 records sent*, and the layer
   held ten rows, ten distinct records, each with one attachment of exactly the size the device made, each
   `created_user` the member — so after the restart the session cookie was traded for a token as §5.5 says. Two
   limits: the photos were made in the page and handed to the file input, so the camera picker was not exercised; and
   a member who left *Keep me signed in* clear waits for sign-in after a restart, by design. **The iPhone half is
   open**, and the client's note stays.
3. **The first version's list is written down** in this ADR before the client is built: question types, functions
   and appearances, each one either supported or refused. That list is what the evaluator is tested against and what
   condition 5 refuses by. **DISCHARGED 2026-11-08:** §5.10. What is on it was confirmed through
   condition 1 (§5.9 (b)), not here.
4. **The expression evaluator is tested against the specifications' own examples** (xlsform.org's and the XForms
   specification's) and against at least twenty public XLSForms, before forms are released. A form whose logic
   evaluates differently from its specification is a defect, not a limitation. **PARTLY DISCHARGED 2026-11-15:** 110 public workbooks, 108 readable, from the sources in §5.18. Of 1,441 expressions found, the 1,373 the server accepts all parse and run in the client, and the catalog and the evaluator agree on every function and refusal. The specifications' examples are covered: the ODK examples already in `XlsformEvaluatorTests` (decimal-time at noon and 6 pm, `regex('[0-9]+')`, `once(random())`, `sum()` of empty nodes, the `indexed-repeat()` equivalence) and XPath 1.0 §4.1, §4.2 and §4.4's examples for `last`, `substring`, `floor` and `ceiling`, with the §4.2 examples of `substring-before`, `substring-after` and `translate`. **Not discharged:** the corpus has no expected results, so a wrong answer without an error is not caught; the expected values for corpus forms, and ODK's own `randomize` vectors, are still to write.
5. **Every refusal at upload names the row, the column and the reason**, and a set of real Survey123 forms is
   imported to count how many come in unedited. A-089 is validated or invalidated by that count. **DISCHARGED 2026-11-15:** a corpus of 110 public workbooks (108 read) was checked, and every refusal in the run has a row and a reason. Refusals with no column are whole-sheet ones, by name. Of the 108, 31 import unedited (29%); of the 61 Survey123-authored forms, 17 do (28%, all from Esri's repository). **A-089 is INVALIDATED** (`architecture-assumptions.md`). The counts and the reasons are in §5.18. **Raised 2026-11-15, same corpus, same run, after §5.19: 31 of the 61 Survey123-authored forms import unedited (51%, up from 17, 28%), and 47 of the 108 (44%, up from 31, 29%).** What still blocks them is in §5.19. **With §5.19, §5.20 and §5.21 together, measured after the three were merged: 42 of the 61 (68%) and 59 of the 108 (55%).** The rest is in §5.21.
6. **The Inbox's conflict rule is tested with two devices**, or a device and Studio: the same record is edited on both
   while one is offline, and the second to arrive is refused and shown both versions. A record deleted online while
   edited offline is kept on the device. Neither case may lose an edit silently. **DISCHARGED 2026-11-16** on the emulator and the
   server, recorded under [ADR-300](ADR-300-a-forms-inbox-is-a-view-each-reader-sees-their-own-rows-of.md) condition 2.
7. **The model and the workbook round-trip.** Every form in condition 4's and condition 5's sets goes workbook →
   model → workbook → model, and the two models are identical: same rows, same columns, same values, unknown columns
   included. A difference is a defect in the reader or the writer, and forms are not released with one. **DISCHARGED 2026-11-15:** all 108 readable corpus workbooks go workbook → model → workbook → model, with identical models, and each model's JSON round-trips (`AFormRoundTripsThroughItsWorkbookTests`). The corpus run found three reader defects that broke the round trip or changed a value, and they are fixed with tests built from minimal workbooks: Excel's `_xHHHH_` escapes (239 occurrences, 57 workbooks), date-formatted numbers read as serials, and carriage returns in text. The writer was not measured against its pre-change self on these inputs.

## 10. Revisit triggers

- Condition 2 fails on iOS and Safari offers no remedy: then iPhone offline is either dropped or moves to a native
  shell, by a new ADR.
- An organization needs Field Maps or Pro to edit offline against this server: that reopens
  [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) for the replica protocol, which this ADR's Inbox does not provide.
- The evaluator's defect count stays above what condition 4 tolerates for more than a release: then §2 F (adopt) is
  reconsidered.
- XLSForm's specification makes a change the client cannot follow.

## 11. Dissent

**This session recommended web first (§2 D), and the owner chose offline from the start.** The recommendation is
recorded, not withdrawn. Its argument was time to first use; the owner's was that the field case is the point. The
owner's choice is the decision.

**This session also recommended an online-only Inbox (§5.9 (e)), and the owner chose offline.** The argument was that
an offline Inbox turns a queue into a sync. That argument still stands as the cost of §5.6, not as an objection to it,
and §5.6's conflict rule and condition 6 are how this ADR answers it.
