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
    | Supported | `integer`, `decimal`, `range`, `text`, `select_one`, `select_multiple`, `note`, `geopoint`, `geotrace`, `geoshape`, `date`, `time`, `dateTime`, `image`, `audio`, `video`, `file`, `calculate`, `hidden`, `acknowledge`; `select_one_from_file` and `select_multiple_from_file` **from a CSV** uploaded with the form |
    | Supported metadata | `start`, `end`, `today`, `username` and `email` (from the signed-in member, empty for an anonymous form), `deviceid` (a random id kept by the installed client, not a hardware id) |
    | Refused | `rank`, `barcode` (Safari has no barcode reader), `background-audio`, `xml-external`, `csv-external`, `select_one_external`, `select_multiple_external`, `*_from_file` from XML or GeoJSON, `phonenumber`, `audit` (`or_other` was on this list until 2026-11-15 and is supported: §5.19 (1)) |

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
    `week-number` (on `date`)**, and `draw` (on `image`; **built 2026-11-15, §5.20 (e)**). Drawn the default way: `horizontal`, `horizontal-compact`, `compact` (on a select), `quick`,
    `quickcompact`, `label`, `list-nolabel`, `counter`, `no-calendar`, the grid widths `w1`–`w4`, and every presentation
    appearance on Esri's list (`predictivetext`, `nopredictivetext`, `calculator`, `numbers`, `press-to-locate`,
    `hide-input`), and ODK's `tabular`, `columns`, `columns-pack` and `no-buttons`; and `minimal` on a type it means nothing on, where Survey123 ignores it too (§5.19 (2)). Refused because they capture something this client cannot: `annotate`, `map` and
    `quick map`, `search()`, `autocomplete` on `select_multiple` (§5.19 (3)), `geocode`, `distress`, `spike` and its variants, `rangefinder`,
    `image-map`, `inframarker`, `getinframarkerid`, `new-front`, `new-rear`.

    **Expressions.** `${name}` references and `.` for the question's own value. Every XPath 1.0 operator the ODK
    specification lists (`|`, `and`, `or`, `mod`, `div`, `=`, `!=`, `<`, `<=`, `>`, `>=`, `+`, `-` — and `*`, which XPath 1.0 has and the page's list drops), with
    dates usable in arithmetic as it describes. The parent, child and self axes, and predicates. Functions:

    | | Functions |
    |---|---|
    | Supported | **String:** `string`, `concat`, `join`, `substr`, `substring` (XPath 1.0 §4.2, beside ODK's `substr`), `substring-before`, `substring-after`, `translate`, `string-length` (one argument, required, as the specification says), `normalize-space`, `contains`, `starts-with`, `ends-with`, `uuid`; `pulldata` **in its CSV form** (a file uploaded with the form), **and `pulldata("@property", name)` and `pulldata("@geopoint", ${q}, part)`** (§5.20); `property(name)` (ODK's, §5.20). **Boolean:** `if`, `coalesce`, `once`, `true`, `false`, `boolean`, `boolean-from-string`, `not`, `regex`, `checklist`, `weighted-checklist`. **Number:** all of the specification's, `random` included, and XPath 1.0 §4.4's `floor` and `ceiling`. **Node-set:** `count`, `count-non-empty`, `position`, `last` (XPath 1.0 §4.1: the size of the node-set a predicate filters), `current`, `randomize`, and `instance` **for a choice list**. **Date and time:** all of the specification's, and `date-time` (Survey123's, §5.20; the specification's table has no other date function this list lacks). **Select:** `selected`, `selected-at`, `count-selected`, `jr:choice-name`. **Repeat:** `indexed-repeat`. **Geographic:** `area`, `distance`, `geofence`, `intersects` |
    | Refused | `digest`, `base64-decode`, `jr:itext`, and the XPath 1.0 core functions that name a node, an ID, a namespace or a language, which a form's answers do not have: `lang`, `id`, `name`, `local-name`, `namespace-uri` (each refused with its reason, on purpose, 2026-11-15); `instance` for anything other than a choice list, and **the Esri `pulldata("@…")` forms other than `@property` and `@geopoint`**: `@layer`, `@json`, `@exif`, `@javascript`; and, by name with a reason each, the properties a browser cannot know and the parts of a geopoint that are not built (§5.20). `@layer` is the one most worth adding next, because it queries a feature layer, and this server has the layers. It is left out because the offline client would need a copy of that layer, which is the Inbox's problem again |

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
      the value Survey123 gives for a new response, because this client does not yet edit a sent record; (4) `language` is
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
   is not yet verified* where it offers offline use.
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
   edited offline is kept on the device. Neither case may lose an edit silently.
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
