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
6. **Existing records are edited offline too (owner decision), and ADR-082 is amended in part.** A form may have an
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
8. **A designer in Studio edits the model.** It is a visual editor for the common cases. Columns it does not edit are
   in the model and stay untouched, and it says so on screen when a form uses something it cannot edit. **There is
   one model, never a second one behind the designer**: the JSON and the workbook are two spellings of the same rows,
   and anything the designer can make can be downloaded as XLSForm. Graticula's own additions go in
   `bind::graticula:…` and `body::graticula:…` columns, never in a field the workbook cannot hold.
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
    | Refused | `rank`, `barcode` (Safari has no barcode reader), `background-audio`, `xml-external`, `csv-external`, `select_one_external`, `select_multiple_external`, `*_from_file` from XML or GeoJSON, `or_other`, `phonenumber`, `audit` |

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
    `default_language`, `style` (`pages`; any other value is drawn the default way), `allow_choice_duplicates`,
    `clean_text_values`, `name`. `submission_url` is supported **when it names a feature layer of this server**, and the
    form then writes to that layer instead of making one; any other address is refused. `namespaces` is accepted and
    has no effect, which the upload says. Refused: `public_key` (encrypted submissions).

    **Appearances.** Supported because they change what is captured: `multiline` (on `text`; on `image` and `file`,
    several attachments), `signature` (on `image`), `month-year` and `year` (on `date`), `minimal`, `likert`, `rating`
    (on `range`), `hidden`. Drawn the default way: `horizontal`, `horizontal-compact`, `compact`, `quick`,
    `quickcompact`, `label`, `list-nolabel`, `counter`, `no-calendar`, the grid widths `w1`–`w4`, and every presentation
    appearance on Esri's list (`predictivetext`, `nopredictivetext`, `calculator`, `numbers`, `press-to-locate`,
    `hide-input`), and ODK's `tabular`, `columns`, `columns-pack` and `no-buttons`. Refused because they capture something this client cannot: `draw`, `annotate`, `map` and
    `quick map`, `search()`, `autocomplete`, `geocode`, `distress`, `spike` and its variants, `rangefinder`,
    `image-map`, `inframarker`, `getinframarkerid`, `new-front`, `new-rear`.

    **Expressions.** `${name}` references and `.` for the question's own value. Every XPath 1.0 operator the ODK
    specification lists (`|`, `and`, `or`, `mod`, `div`, `=`, `!=`, `<`, `<=`, `>`, `>=`, `+`, `-` — and `*`, which XPath 1.0 has and the page's list drops), with
    dates usable in arithmetic as it describes. The parent, child and self axes, and predicates. Functions:

    | | Functions |
    |---|---|
    | Supported | **String:** `string`, `concat`, `join`, `substr`, `substring-before`, `substring-after`, `translate`, `string-length`, `normalize-space`, `contains`, `starts-with`, `ends-with`, `uuid`; `pulldata` **in its CSV form** (a file uploaded with the form). **Boolean:** `if`, `coalesce`, `once`, `true`, `false`, `boolean`, `boolean-from-string`, `not`, `regex`, `checklist`, `weighted-checklist`. **Number:** all of the specification's, `random` included. **Node-set:** `count`, `count-non-empty`, `position`, `current`, `randomize`, and `instance` **for a choice list**. **Date and time:** all of the specification's. **Select:** `selected`, `selected-at`, `count-selected`, `jr:choice-name`. **Repeat:** `indexed-repeat`. **Geographic:** `area`, `distance`, `geofence`, `intersects` |
    | Refused | `digest`, `base64-decode`, `jr:itext`, `instance` for anything other than a choice list, and **every Esri `pulldata("@…")` form**: `@layer`, `@property`, `@json`, `@exif`, `@geopoint`, `@javascript`. `@layer` is the one most worth adding next, because it queries a feature layer, and this server has the layers. It is left out because the offline client would need a copy of that layer, which is the Inbox's problem again |

    **Esri columns.** `bind::esri:fieldType` is supported for the types this server serves: `esriFieldTypeInteger`,
    `SmallInteger`, `BigInteger`, `Double`, `Single`, `String`, `Date`, `GUID`, and `null` (the question is not stored).
    `esriFieldTypeDateOnly` is accepted and stored as `Date` at midnight, which loses nothing, and the upload says so.
    `TimeOnly` and `TimestampOffset` are refused, because this server has no such field and storing them as something
    else would change the value. `bind::esri:fieldLength`, `bind::esri:fieldAlias`, `body::esri:visible` (hidden but
    still stored), `body::accuracyThreshold` and `body::accept` are supported. `body::esri:style` and
    `bind::saveIncomplete` are drawn the default way (the client always keeps drafts). Refused: `bind::esri:parameters`
    (its `calculationMode` changes stored values), `bind::esri:warning` and `bind::esri:warning_message`,
    `body::esri:inputMask`, `bind::esri:workflow`.

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
   evaluates differently from its specification is a defect, not a limitation.
5. **Every refusal at upload names the row, the column and the reason**, and a set of real Survey123 forms is
   imported to count how many come in unedited. A-089 is validated or invalidated by that count.
6. **The Inbox's conflict rule is tested with two devices**, or a device and Studio: the same record is edited on both
   while one is offline, and the second to arrive is refused and shown both versions. A record deleted online while
   edited offline is kept on the device. Neither case may lose an edit silently.
7. **The model and the workbook round-trip.** Every form in condition 4's and condition 5's sets goes workbook →
   model → workbook → model, and the two models are identical: same rows, same columns, same values, unknown columns
   included. A difference is a defect in the reader or the writer, and forms are not released with one.

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
