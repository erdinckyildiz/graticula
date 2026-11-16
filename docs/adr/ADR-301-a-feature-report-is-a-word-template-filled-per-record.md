# ADR-301 — A feature report is a Word template filled per record, by the server's own engine

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the syntax is Survey123's public documentation and every part of it built is tested on documents written in the tests; no template written by a Survey123 user has been filled, no output has been opened in Word, and every choice the public pages do not settle is this session's (§5.9, [Q-237](../open-questions.md)) |
| **Decided** | 2026-11-17, overnight, on the owner's instruction *"gece, gerçek formlarla ve henüz olmayan her şeyi tamamla. yavaştan survey123'yi de bitirelim"* — finish what is not built yet, and finish Survey123. [ADR-287](ADR-287-a-form-is-an-xlsform-filled-online-or-off.md) §5.9 (h), confirmed by the owner 2026-11-09, left the feature report to *"a later ADR"*; this is it. Nothing here was put to the owner: every choice the public pages leave open is **INFERRED** in §5.9 and listed as [Q-237](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Survey123's **feature report** is the largest thing a Survey123 user does with a form's answers that Graticula did not do:
the survey's owner uploads a Word document whose text holds placeholders — `${inspector}`, `${#defects} … ${/}`,
`${if damage=="severe"} … ${/}`, `${photo | size:300:0}` — and anybody who reads the results ticks records on the Data tab,
chooses the template and downloads one document per record, or one for all of them. Inspections, permits, damage
assessments and work orders are handed over as these documents; a shop that has them cannot move its forms without them.

ADR-287 §5.9 (h) said the report would be *"built on the print service"*. Reading [ADR-265](ADR-265-the-server-prints-a-web-map.md)
for this ADR says otherwise: the print service composes a **map page** of its own design and writes it to a raster PDF
(§5.6 there, *"one page … holding the page as a JPEG"*). A feature report is a **Word document the customer designed**,
whose text, tables, fonts and page layout are theirs; nothing of the print service fills it. What the print service can
give a report is a **map picture** for `${location | map}`, which is §6's first item not built, and a PDF of a Word
document is a layout problem the print service does not solve either (§5.7).

## 2. Alternatives considered

### Alternative A — Adopt an Open XML library and a template library (DocumentFormat.OpenXml; a docxtemplater-like engine)

**For.** DocumentFormat.OpenXml is MIT, Microsoft's, and types every element of the format; a template library would bring
loops and conditions already written.

**Against.** The build-vs-adopt policy puts a template language in **Tier 1**: it is the server's own domain logic, the
thing that decides what a report says, and the syntax is Survey123's, not a library's — no .NET library reads
`${#repeat}`, `| getValue:""`, `| size:w:h` or `${if …}` as Survey123 does, so the language would be written here either way.
What the Open XML SDK would add is typed access to parts this engine reads as XML, which `System.IO.Compression` and
`System.Xml.Linq` already do in the framework. The hard part — Word splitting `${name}` over three runs — is not something
the SDK solves; it hands over the same runs. Not chosen; the case is kept in [Q-237](../open-questions.md) (q).

### Alternative B — A report of the server's own design, drawn by ADR-265's page composer

**For.** No Word parsing at all; a PDF from the start; the map is free.

**Against.** It is not Survey123's feature report. The point of the feature is that the template is the customer's
document — their letterhead, their tables, their wording — and a page laid out by this server would be a different
product. Not chosen.

### Alternative C — The server's own engine over the `.docx`: placeholders read, checked against the form, filled per record *(chosen)*

## 3. Counterarguments to the preferred option

- **Word's format is large, and an engine of our own will meet documents it does not expect.** Text boxes, fields,
  content controls, tracked changes, footnotes. The engine reads the main document, headers and footers, and moves only
  the runs a placeholder touches; everything else is copied byte for byte. A template whose placeholder sits where the
  engine cannot place a section is refused at upload with its paragraph, not silently half-filled. What it has *not*
  done is meet a real customer's template (condition 1).
- **No PDF.** Survey123 offers PDF beside Word, and many shops hand the PDF over. A faithful conversion needs a layout
  engine (§5.7); this ADR offers Word only and says so on the screen, rather than a PDF button that fails.
- **No maps.** A location question is drawn as a map in Survey123's report by default; here it is refused at upload with
  the two placeholders that write its coordinates. A template that relies on maps cannot be used yet.
- **Synchronous.** Survey123 queues a report as a job and takes 2,000 records; this answers at once and takes 100
  (§5.9 (e)). A large batch is several requests.

## 4. Evidence

Every Survey123 behaviour below was read on Esri's public documentation (doc.esri.com, *ArcGIS Survey123*, latest),
2026-11-17. No Esri template, code or file was copied or committed; every template in the tests is written in the test.

| Claim | Evidence | Source |
|---|---|---|
| A template is a `.docx` with placeholders; output is Word or PDF; at most 2,000 records per request; only the survey's owner and administrators upload templates | the page's *Report templates* and *Limitations* | doc.esri.com/en/arcgis-survey123/latest/analyze/featurereporttemplates.html |
| A placeholder is `${question \| method:parameter}`; `${#image1}${$file}${/}` iterates files; `${if fruitcolor!="red"}…${/}` is a condition; arithmetic as `${a / b}`; methods `getValue`, `toFixed`, `round`, `useGrouping`; keywords `$date`, `$image`, `$map`, `$shape`, `$attachment`, `$file`, `$feature`, `$layers` | *Report expressions* | …/analyze/featurereport-expressions.html |
| A select question alone writes its label; `getValue:""` its name; `checked:"name"`, `countSelected`, `selectedAt` (from 0), `appearance:"bullets"` | *Choice expressions* | …/analyze/featurereport-choice.html |
| `round:n`, `toFixed:n`, `format:"#,##0.###"` with `.` `,` `0` `#` | *Number expressions* | …/analyze/featurereport-numbers.html |
| `format` tokens (`YYYY`, `MMM`, `Do`, `HH`, `A`, `Z`, `X`, …), an empty format is ISO 8601, `utcOffset` in three spellings, `locale` lower case, `${$date}` | *Date and date-time expressions* | …/analyze/featurereport-datetime.html |
| `size:width:height:max_width:max_height` in pixels, 0 no restriction, `size` last; `rotate`; `getValue:"name"`, `"size"`, …; `${#$attachment}`; `attachmentsDefExp` | *Image and other attachment expressions* | …/analyze/featurereport-images.html |
| A multiline answer prints as one block unless `appearance:"multiline"` | *Text expressions* | …/analyze/featurereport-text.html |
| `${#repeat}…${/}`, nested repeats inside their parent's section, `repeat.field`, `${$feature \| getValue:"position"}` | *Repeat expressions* | …/analyze/featurereport-repeats.html |
| `map`, `mapScale`, `$shape`, `drawingInfo` | *Map expressions* (not built, §6) | …/analyze/featurereport-map.html |
| Records are chosen on the Data tab, then Report; Format Word or PDF; *Report name* takes placeholders, not for a merged report; several records as separate reports or one; the *Feature report* privilege | *Print survey results* | …/analyze/printsurveyresults.html |
| A placeholder Word split over runs is read whole; Word's typographic quotes read as plain; labels, `getValue`, `checked`, `countSelected`, `selectedAt`, bullets; number formats; dates in the asker's offset; repeats as paragraphs and as table rows (and an empty one leaving a well-formed table); inline and block conditions; pictures sized, packaged with their relationships and content types; files iterated; headers filled; merged records with a page break; the report name | `AFeatureReportFillsAWordTemplateTests`, 34 tests | this repository |
| Unsupported syntax is refused with part, paragraph and reason: a map, `$shape`, `$layers[0]`, an unknown method or name, a location without coordinates, a repeat's question outside its section, `size` on a choice, EXIF, `attachmentsDefExp`, `$image \| src`, `=` for `==`, a `%` number format, an unclosed section, a stray `${/}`, a section with text before its opening | same class | this repository |
| The owner reports from the form's own layer (zipped and merged), a member reports their own record through the Inbox, the other's is "not there", a member cannot upload, a template with a map is refused at paragraph 2, PDF is refused in words, a removed template is gone | `AFormsInboxShowsEachMemberTheirOwnTests.A_feature_report_is_made_of_the_records_its_asker_may_read_from_a_template_checked_at_upload`, against a local server (`gisnight_report`) | this repository |
| Studio: ticking a submission enables *Create report*, which downloads the `.docx`; a refused upload lists its placeholder by paragraph and takes focus to the list; every new control is labelled | `FormReportScreenTests`, headless Chrome against the same server | this repository |

## 5. Decision

1. **Templates are the form item's resources** (ADR-264), at `graticula/report-templates/<file name>.docx`, so they are
   recycled and deleted with the form. `POST /admin/forms/{id}/reports/templates` (a `file`, multipart) adds or replaces
   one, by whoever manages the form, as Survey123's page says only the owner and administrators do;
   `GET …/reports/templates` lists them for whoever may open the form; `DELETE …/reports/templates/{name}` removes one.
   **Every template is checked against the form when it is uploaded** (the published form, or the draft before
   publishing): refused, the answer is `400` with each placeholder's part, paragraph number, paragraph text, placeholder and
   reason, and nothing is kept. At report time it is checked again against the published form, and a template that no
   longer fits (a question removed by a republish) is `409` with the same list.
2. **The template engine is ours** (`Forms/Reports`, Tier 1): `ReportExpression` reads a placeholder — kind (value,
   `${#…}`, `${if …}`, `${/}`), an expression of names, dotted names, numbers, quoted texts, `+ - * / %`, comparisons,
   `&& || !` and brackets, and its methods; `ReportEvaluator` resolves names in a scope (the record, its repeat rows, a
   file) and applies the methods; `ReportTemplate` opens the package with `System.IO.Compression` and the main document,
   every header and every footer with `System.Xml.Linq`, in four steps: (1) **merge runs** — each paragraph's text is read
   whole and each placeholder's characters are moved into the run it starts in; (2) **isolate** — each placeholder gets a run
   of its own, keeping that run's formatting; (3) **pair and lift** each section to the level it repeats at: inside one
   paragraph the runs between; over paragraphs side by side, those paragraphs, the tags standing at their paragraph's start
   and end; over rows of one table, those rows (Survey123's repeat-as-table); anywhere else refused; (4) **compile** to a
   tree of static XML, elements, values and sections, checked, then filled per record.
3. **What is filled**: question values, with select questions by their labels in the form's default language;
   `getValue` (raw value, file properties, `x`/`y` of a location, `$feature` position); `format` (numbers by Survey123's
   four placeholder characters, dates by its token table); `round`, `toFixed`, `useGrouping`, `utcOffset`, `locale`;
   `appearance:"multiline"` and, for select_multiple, `"bullets"`; `checked`, `countSelected`, `selectedAt`; `${#repeat}`
   sections (nested inside their parent's), `${#question}` and `${#$attachment}` sections over files with `$file`;
   `${if …}` sections; `$date`; the layer's own fields (`objectid`, `globalid`, `created_user`, `created_date`, …);
   pictures — photographs, signatures and annotated images are image questions' attachments — placed as inline drawings at
   `size:w:h:maxW:maxH` pixels (96 dpi) and `rotate`, the bytes added under `word/media/` with their relationship and
   content type. A file that is not a PNG, JPEG or GIF is written as its name.
4. **What is refused, with its place and reason**, never dropped: maps (`| map`, `mapScale`, `drawingInfo`, `$shape`,
   `$map`, a location placeholder with no `getValue`), summary sections and statistics (`$summary`, `$layers`, `| stats`,
   `| where`), pictures fetched from an address or the form's media (`$image | src`), `attachmentsDefExp`, EXIF properties,
   functions and indexes, any method or name the form does not have. The upload answer and the Studio screen carry one line
   saying what is not supported (`ReportTemplate.NotSupported`).
5. **Records are read as the asker, through the layer.** `POST /admin/forms/{id}/reports` takes
   `{template, objectIds, mode, name, utcOffsetMinutes, format}` from any signed-in member who may open the form. The
   rows come from the form's own layers when the asker may read them (`ServiceLookup.ReadableQuietlyAsync`, the layer's
   Query ceiling, `ServiceContexts`' source — the same doors and source a query takes, so view filters hold), and otherwise
   from the form's **Inbox** view, whose filter the database applies per reader (ADR-300). Repeat rows are read through
   the repeat's layer by their parent's GlobalID; attachments through `AttachmentEndpoints.StoreOfAsync`, which this ADR
   extracted from the attachment doors so a report and a client read them by one set of rules. Only the attachments the
   template names are read, and only pictures' bytes. A chosen record the asker may not read is answered as one that is
   not there (`404`), the same sentence for both.
6. **Output.** One record: a `.docx`. Several: `separate` (default, as Survey123's API) — a `.docx` each in a ZIP — or
   `merged` — one document, each record's body after the last with a page break between, the section properties once at
   the end. `name` takes value placeholders for a separate report and none for a merged one (Survey123's rule).
7. **PDF is not offered.** ADR-265's print service writes its own map page as one JPEG inside a PDF; it has no reader of
   Word documents. A faithful `.docx` → PDF needs a layout engine — fonts and their metrics, line breaking, tables,
   pagination, headers and footers — which is a dependency decision (LibreOffice in a child process, as GDAL is kept;
   or a commercial converter) or a large Tier 1 build. Neither is taken overnight: the request answers `400` for `pdf`
   naming this section and [Q-237](../open-questions.md), and Studio shows no PDF control.
8. **Studio.** On the form's Results, *Latest submissions* gains a tick box per row (and one for all shown), each labelled
   by when it was sent; a *Reports* section lists the templates (with *Remove* and *Add a template* for whoever manages
   the form), chooses one, how several are delivered, and a file name, and *Create report* downloads the file. Status is
   announced in a live region; a refused upload lists each placeholder with its paragraph and takes focus to the list.
   With no signal, the request fails before it reaches the server and the screen says, in words, that reports are made on
   the server. The form filler (`fill.js`) is untouched: a report is made of sent records, not on a device.
9. **INFERRED** — every choice Survey123's public pages do not settle, made the conservative way and listed for the owner
   as [Q-237](../open-questions.md):
   - (a) Templates are the form item's resources; one with the same file name replaces the earlier one.
   - (b) Any signed-in member who may open the form may make a report, of the records they may read through the form's
     layers or its Inbox; no new *feature report* privilege is added (Survey123 has one). Anonymous callers get none.
   - (c) Several records default to `separate`, zipped; `merged` fills headers and footers with the first record.
   - (d) The default file name is the form's title and the record's object id; a merged report's, the title.
   - (e) Bounds: 100 records a request, answered at once; a template at most 10 MB and 64 MB unpacked; at most 200 MB of
     pictures a report; two reports at once per node, a third waiting five seconds and refused `503` with `Retry-After`.
   - (f) Dates are written in the asker's offset, which Studio sends from the browser; a `date` question is its calendar
     day and is not shifted; with no `format`, dates are ISO (`YYYY-MM-DD`, `YYYY-MM-DD HH:mm`), where Survey123 uses its
     locale's default; `locale` is a .NET culture.
   - (g) A number with no format is written with no group separators and its own decimals; `useGrouping:true` groups.
   - (h) Labels are the form's default language's, as its layer's aliases are.
   - (i) A picture with no `size` is placed at its own pixel size, at most 600 px wide; a file that is not a picture is
     written as its name.
   - (j) A text answer's line breaks are spaces unless `appearance:"multiline"` (Survey123's documented default).
   - (k) The layer's own fields are names a template may use.
   - (l) A select_multiple with no method is its labels joined by commas.
   - (m) `appearance:"bullets"` is one paragraph of lines each starting "•", not a Word list.
   - (n) A section over several paragraphs must open at the start of its paragraph and close at the end of its own;
     otherwise it is refused, not guessed.
   - (o) `checked` writes ☒ or ☐.
   - (p) PDF is not offered (§5.7).
   - (q) No library is adopted; the case for the Open XML SDK is in Q-237.

## 6. Consequences

**Positive.** A form's results become the documents a shop hands over, from templates written in Word with Survey123's
syntax; a template that would not fill is refused when it is uploaded, at its paragraph, not when a report is due; a member
reports only on what they can read, by the same doors a query uses.

**Negative.** Not built, each refused or said: **maps** in a report (the next step, on ADR-265's renderer: draw the
record's geometry and its layer as a PNG at `size` and place it as a picture); **PDF** (§5.7); summary sections and
statistics; `$image | src` (a fetch from an address is an SSRF door, ADR-265 §5.3's reasoning; the form's own media could
be read without one); `attachmentsDefExp`; EXIF; Survey123's 2,000-record queued jobs, and *Recent tasks*. A Word list
(`w:numPr`) inside a repeat keeps numbering across rows as Word decides. Templates are checked against a schema, not
rendered, at upload: a template Word opens and this engine reads may still look different from Survey123's output.

**Ports created.** None: no Tier 2 dependency.

- **State.** Item resources only (the templates). Reports are made in memory and handed over; nothing is kept.

## 7. Conditions

1. **Real templates are filled and opened.** At least five templates written by Survey123 users (public ones, read for
   their syntax, not committed), filled from a fixture and opened in Microsoft Word and in LibreOffice without a repair
   prompt; each failure either fixed or refused at upload with its reason. *Progress 2026-11-17, not a discharge:* one
   template written for the purpose (a placeholder split over two runs as Word writes it, curly quotes in a `format`,
   a choice's label and `getValue`, a date format, an `${if}`, a photo with `size`, a repeat over table rows, `$date`)
   filled on the fixture for two records with photos and three repeat rows each, as a zip of two and as one merged
   document. Microsoft Word 16, driven hidden with alerts off, opened all three without a repair prompt, counted the
   pages (2 merged, 1 each), the pictures and the four table rows, and exported them to PDF; the text read back was the
   expected text. Not yet: Survey123 users' own templates, and LibreOffice, which this machine does not have.
2. **The owner confirms the INFERRED list** of §5.9, (a) to (q), through [Q-237](../open-questions.md).
3. **PDF is decided** (Q-237): a converter adopted behind a port with its ADR, a Tier 1 layout, or Word only, written down.
4. **Maps are drawn** for `${location | map}` and `$shape` on ADR-265's renderer, or the refusal stays and is recorded as
   the decision.
5. **The cost is measured**: 100 records with a photograph each, through the Inbox, on the showcase — time, peak memory and
   size — against §5.9 (e)'s bounds, which are this session's guesses.

## 8. Dependencies

**Depends on:** [ADR-287](ADR-287-a-form-is-an-xlsform-filled-online-or-off.md) (forms, §5.9 (h)),
[ADR-264](ADR-264-an-app-or-a-link-is-an-item-and-an-item-keeps-resources.md) (item resources),
[ADR-300](ADR-300-a-forms-inbox-is-a-view-each-reader-sees-their-own-rows-of.md) (the Inbox a member reads through),
[ADR-113](ADR-113-a-hosted-layer-has-views.md) (views and their attachments),
[ADR-265](ADR-265-the-server-prints-a-web-map.md) (read for PDF and maps; not used yet).

**Depended on by:** —

## 9. Revisit triggers

- A customer template measured failing in Word after filling (condition 1).
- A shop that needs PDF, or maps in reports, to move its forms (conditions 3, 4).
- A report request measured past ten seconds at 100 records, or refused `503` in normal use.

## 10. Dissent

None recorded. ADR-287 §5.9 (h) expected the report to be built on the print service; §1 records why it is not.
