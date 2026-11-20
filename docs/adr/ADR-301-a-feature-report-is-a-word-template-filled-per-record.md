# ADR-301 — A feature report is a Word template filled per record, by the server's own engine

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the syntax is Survey123's public documentation and every part of it built is tested on documents written in the tests; thirteen public templates written by Survey123 users and by Esri were filled on a fixture and opened in Microsoft Word (§5.12), none in LibreOffice; every choice the public pages do not settle is this session's (§5.9–§5.12, [Q-237](../open-questions.md)) |
| **Decided** | 2026-11-17, overnight, on the owner's instruction *"gece, gerçek formlarla ve henüz olmayan her şeyi tamamla. yavaştan survey123'yi de bitirelim"* — finish what is not built yet, and finish Survey123. [ADR-287](ADR-287-a-form-is-an-xlsform-filled-online-or-off.md) §5.9 (h), confirmed by the owner 2026-11-09, left the feature report to *"a later ADR"*; this is it. Nothing here was put to the owner: every choice the public pages leave open is **INFERRED** in §5.9 and listed as [Q-237](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | §5.7 and §5.9 (p) by [ADR-302](ADR-302-a-feature-report-is-also-a-pdf-laid-out-by-officeimo.md), 2026-11-17 evening: a report is also a PDF |

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
- **Maps of this server's layers only.** A location question is drawn as a map in Survey123's report by default; here it
  is too (§5.10), but on what this server serves: a basemap from ArcGIS Online or OpenStreetMap is left out and the report
  says so, as ADR-265 leaves it out of a print. *(Was "No maps" until §5.10 was built, 2026-11-17.)*
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
| A location is drawn as a map by default; `map:"<web map id>"` (an empty id: the question's default basemap), `mapScale` (0 or none: fitted — one point at the question's scale, several at their extent), `rotate`, `mapExtent:xmin:ymin:xmax:ymax:wkid`, `size:w:h` (last), `mapFilters`, `drawingInfo:"currentLayer"` or a layer's URL (default: "a default symbol"), `$shape` (the record's geometry), `$map` (a map of no question), `where:"… !important"` to draw other records | *Map expressions* | …/analyze/featurereport-map.html |
| A summary section is `$<$summary>` … `$</>`, written once for all the records chosen, at the start or the end of the report and "not … within the contents"; a map in it draws every record; `where`, `stats:"type,field,outName":"groupBy":"having"` (`;` between several), `orderByFields`, `resultRecordCount`, `outFields` with `returnDistinctValues`; the statistics avg, count, sum, stddev, min, max, var, range, first, last, median; a filter reads the records printed unless `!important`, and in an individual report the record itself; `$inputParams` | *Report queries* | …/analyze/featurereportqueries.html |
| `$layers` "references any layer by name or ID in the same feature service as the survey layer", as in `${$layers["cities"] \| where:"1=1 !important" \| stats:"count,objectid"}`; `$shape` "prints the geometry … of the current feature on a map" | *Report expressions* | …/analyze/featurereport-expressions.html |
| Public templates — thirteen of sixteen read here (§5.12) — also write sections whose ends share their paragraphs with text, `${if question \| selected:"name"}`, `(question \| getValue:"size")` inside arithmetic, a repeat as a condition, ArcGIS's editor-tracking names (`CreationDate`, `Creator`) and `map:" "` | the templates, public ArcGIS Online items of type *Microsoft Word*, read as data and not kept in the repository; `D:\temp\report-templates\2026-11-17\SOURCES.md` lists each with its address and outcome | §5.12 |
| Records are chosen on the Data tab, then Report; Format Word or PDF; *Report name* takes placeholders, not for a merged report; several records as separate reports or one; the *Feature report* privilege | *Print survey results* | …/analyze/printsurveyresults.html |
| A placeholder Word split over runs is read whole; Word's typographic quotes read as plain; labels, `getValue`, `checked`, `countSelected`, `selectedAt`, bullets; number formats; dates in the asker's offset; repeats as paragraphs and as table rows (and an empty one leaving a well-formed table); inline and block conditions; pictures sized, packaged with their relationships and content types; files iterated; headers filled; merged records with a page break; the report name | `AFeatureReportFillsAWordTemplateTests`, 34 tests | this repository |
| Unsupported syntax is refused with part, paragraph and reason: a map's `rotate`, `mapFilters` or a `drawingInfo` address, a web map id that is not one, `$layers[…]` as a value, an unknown method or name, a repeat's question outside its section, `size` on a choice, EXIF, `attachmentsDefExp`, `$image \| src`, `$inputParams`, a statistic of text, an unknown statistic or field, `=` for `==`, a `%` number format, an unclosed section, a stray `${/}`; a summary section within the report, around one record's answer, `$feature` or `$attachment`, or sharing its paragraph with text | same class | this repository |
| Maps: a location placeholder asks for a map of its size (400 × 300 with none) with the record's point; a web map id, a scale, an extent and `drawingInfo:"currentLayer"` are asked for as written; a record with no location draws no map; the mark drawn on a known extent is at its pixel | same class, map tests | this repository |
| Summaries: written once before the records, their statistics asked of the chosen records, grouped rows written with their group's label, a repeat's query kept to its record's rows, `!important` and `$layers[…]`; a template of summaries alone writes no record | same class, summary tests | this repository |
| What public templates write (§5.12): a section spliced between paragraphs that hold text (a condition met, not met, a repeat of two rows, one section closing where the next opens), `selected`, a file's size in brackets, a repeat as a condition, `Creator` and `CreationDate` | same class, §5.12 tests | this repository |
| On the fixture, a form with a location, three records and three repeat rows each: the summary wrote *3 bina, toplam kat 17, ortalama 5.67, ortanca 5, açıklık 6, ilk Galata Kulesi*, the groups *Kırmızı: 14; Mavi: 3*, the repeat's sum 198 and each record's largest room, all through the layer's own query; the maps were PNGs of the asked size with the points marked (154 marked pixels on the summary's map of three points; the mark at the centre of each record's), `drawingInfo:"currentLayer"` drew the layer's own symbol, and a saved web map naming the form's layer drew it, as the asker. The fixture's default basemap is OpenStreetMap, at another address: it was left out and the report's `Graticula-Report-Notes` header said so. Word opened the document (4 pages, 7 pictures) and exported it to PDF | a script against the local fixture (`gisnight_report`), not kept | this session, 2026-11-17 |
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
4. **What is refused, with its place and reason**, never dropped: a map's `rotate` (a map is drawn north up, as ADR-265
   draws one), `mapFilters`, a `drawingInfo` address; `$inputParams`; pictures fetched from an address or the form's media
   (`$image | src`), `attachmentsDefExp`, EXIF properties, a geometry's `getValue:"length"` or `"area"`, functions, any
   method or name the form does not have. The upload answer and the Studio screen carry one line saying what is not
   supported (`ReportTemplate.NotSupported`). *(Amended 2026-11-17: maps are drawn, §5.10, and summaries and queries
   computed, §5.11; until then both were refused here.)*
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
7. **PDF is not offered.** *(Superseded 2026-11-17 evening by [ADR-302](ADR-302-a-feature-report-is-also-a-pdf-laid-out-by-officeimo.md): the owner asked for PDF the same day, and OfficeIMO lays the Word document out in a child process. Kept as written, because it is why the morning said no.)* ADR-265's print service writes its own map page as one JPEG inside a PDF; it has no reader of
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
   - (n) ~~A section over several paragraphs must open at the start of its paragraph and close at the end of its own;
     otherwise it is refused, not guessed.~~ *Reversed the same night by §5.12: public templates write such sections, and
     they are spliced.*
   - (o) `checked` writes ☒ or ☐.
   - (p) PDF is not offered (§5.7). *(Superseded by ADR-302.)*
   - (q) No library is adopted; the case for the Open XML SDK is in Q-237.

10. **Maps — on ADR-265's renderer** (condition 4). A location placeholder not read with `getValue`, `$shape` and `$map`
    are drawn as a PNG and placed as an inline picture at `size:w:h` pixels (96 dpi). `PrintEndpoints.DrawMapImageAsync` is
    the print service's own `MAP_ONLY` drawing — the layers resolved and drawn as the caller by `ResolveAsync` and the
    renderer, the same code a print uses — with the records' geometries marked on top before the picture is encoded. The
    layers are the web map's that `map:"id"` names, read from the web map store as the asker (absent and not shared are one
    sentence, and the map is drawn on the default basemap instead); with no id, `map:""` or `map:" "`, the organisation's
    default basemap as `portals/self` gives it to the asker (ADR-086, ADR-184). The extent is `mapExtent`, else the records'
    geometries — one point at `mapScale` or 1:10,000, several fitted with a tenth around them — else the web map's own, else
    the world. `drawingInfo:"currentLayer"` adds the records' own layer (the form's, or its Inbox view, whichever the asker
    reads) to the map, kept to the records by a definition expression, and draws it with its symbols instead of the mark;
    `where`, `orderByFields` and `resultRecordCount` choose the records a map draws through §5.11's query. In a summary
    section a location draws every record chosen. **Nothing is fetched from another address** (ADR-265 §5.3): a basemap or a
    web map layer there is left out, the map drawn with what this server serves, and the report says what was left out in
    its `Graticula-Report-Notes` header, never failing; a map that cannot be drawn at all is written as nothing. The engine
    stays synchronous and makes no request of its own: a fill asks `IReportLookups` for each map and query, and the
    endpoint fills, fetches what was missing as the asker, and fills again, until nothing is missing (`ReportPrefetch`).
    Bounds: at most 300 maps a report, each at most 2,000 pixels a side and the server's image maximum.
11. **Summary sections and queries.** `$<$summary>` … `$</>` at the start or the end of the document is written once,
    before or after the records, with the records chosen as its records; what stands between the summaries is written once
    per record, and a template of summaries alone writes none. A template with a summary is one document for all the records
    chosen, whatever `mode` asks. A layer — the form's by its name, a repeat by its name, or `$layers[n]` / `$layers["name"]`
    in the order publishing gives the form's tables — takes `where` (texts and answers joined with `+`, and `!important`),
    `stats`, `orderByFields`, `resultRecordCount`, `outFields` and `returnDistinctValues`: as a value
    (`${survey | stats:"sum,qty"}`) the first statistic of the first row; as a section (`${#survey | stats:…:"group"}` …
    `${/}`) once per row, its statistics' names and the fields grouped by readable inside, a grouped choice by its label; with
    no statistics, once per record the query finds. **Each is asked of the layer's own query, as the asker** —
    `FeatureServerQueryParameters` reads the parameters, `IFeatureSummaries.StatisticsAsync` answers `outStatistics` under the
    layer's connection lease, rows are read by the source — kept to the records printed (the record in an individual
    report, its rows for a repeat; the records chosen in a summary) unless `!important`. Question names in a clause are
    written as the layer's columns before the layer's own where-clause reader reads it. `median` is the 0.5 continuous
    percentile; `range` is max − min; `first` and `last` the first and last non-empty value in object id order.
12. **What public templates write.** Sixteen public templates were read (§4, condition 1); what thirteen of them needed, and
    Survey123's pages do not describe, is built:
    - a section whose ends share their paragraphs with text — `Before ${if x}` … `${/} after`, or `${/} ${if y}` in one
      paragraph — is **spliced**: each end's paragraph is cut there and the half before an end joins the paragraph written
      after it, so a section written once is the template without its tags and one not written joins the text around it, as
      the XML between two tags is;
    - `${if question | selected:"name"}` is true when the choice is selected;
    - `(question | getValue:"size")` inside an expression is the value its methods give, as in `${if (permit | getValue:"size")>0}`;
    - a repeat in a condition is its number of rows, so `${if photos}` asks whether there are any;
    - `CreationDate`, `Creator`, `EditDate` and `Editor`, ArcGIS's editor-tracking names, read the layer's own fields;
    - `map:" "` is the default basemap.
    Three were not filled: one is not a Survey123 template (its placeholders are `{{…}}`); one names `$file` outside a section
    over files, and EXIF; one asks a geoshape's `getValue:"length":"feet":"geodesic"`. Each is refused at upload with its
    paragraph and reason.
13. **INFERRED**, added to (a)–(q) for [Q-237](../open-questions.md):
    - (r) A map with no `size` is 400 × 300 pixels, Survey123's examples' size; at most 2,000 a side.
    - (s) "The question's default basemap" is the organisation's default basemap, as `portals/self` gives it to the asker; the
      form's own `graticula:basemap` setting is not read, because it names tiles from another address.
    - (t) One point with no `mapScale` is drawn at 1:10,000 (Survey123 uses the question's scale); several are fitted with a
      tenth of their extent around them, at least 100 m.
    - (u) The mark is a red dot ringed in white (a line red, an area red at a quarter opacity), where Survey123 draws "a
      default symbol".
    - (v) `drawingInfo` takes `"currentLayer"` only; a layer's address is refused, as ADR-265 fetches nothing.
    - (w) A record with no location writes no map.
    - (x) A template with a summary section is one document, whatever `mode` asks.
    - (y) `$layers[n]` counts the form's tables in the order publishing gives them: the form's layer, then its repeats.
    - (z) A statistic with no output name is read as `type_field`; `first` and `last` are in object id order and not per
      group; a query reads at most 1,000 rows.
    - (aa) A summary section reads no record's answer outside a `${#layer}` section; a statistic over no rows writes nothing.
    - (ab) §5.12's readings of what public templates write, each of which Survey123's pages do not describe.

14. **The owner's answers, 2026-11-17 morning** ([Q-237](../open-questions.md)). **No PDF: a report is a Word
    document only** (*"Yalnız Word kalsın"*) *(reversed the same evening — [ADR-302](ADR-302-a-feature-report-is-also-a-pdf-laid-out-by-officeimo.md))* — §5.7 stands as the decision rather than a gap, and a person who needs a
    PDF saves the document as one in Word. **A reader of a form sees its Results and makes reports** (*"Okuyucuya da
    Sonuçlar ve rapor"*), reversing what §5.8 had left — Studio showed Results only to whoever manages the form, so a
    reader the server would serve had no way to a report. Built in eeff8d39, with no server change: a reader's Results
    take the publication from `/forms/{id}/form.json` and read the records from the form's top layer when the reader
    can query it, else from its Inbox view, else none — in the order the report endpoint already reads them (§5.5) —
    and one sentence under the heading says which (*Every submission to this form…*, *Only the submissions this form's
    Inbox gives you…*, or *You cannot see this form's answers… ask its owner…*, with no table). The Inbox sentence
    first said *Your own submissions*, which is false when the Inbox's filter assigns records rather than picking a
    member's own; the UX review of 2026-11-17 replaced it, and made an Inbox that gives a reader nothing say so instead
    of *No answers yet*. The Form panel, publishing,
    sharing, the designer and template Add/Remove stay the owner's. A reader who reads the layer itself gets the
    download only where the layer allows Extract, and never through the Inbox view, which refuses exports (ADR-300
    §5.4). Every other INFERRED point, (a) to (ab), is confirmed as written (*"Hepsi yazıldığı gibi"*).

## 6. Consequences

**Positive.** A form's results become the documents a shop hands over, from templates written in Word with Survey123's
syntax; a template that would not fill is refused when it is uploaded, at its paragraph, not when a report is due; a member
reports only on what they can read, by the same doors a query uses.

**Negative.** Not built, each refused or said: a map's `rotate`, `mapFilters`, and layers at another address — a
basemap from ArcGIS Online or OpenStreetMap is left out of a report's map, as of a print (ADR-265 §5.3); **PDF** (§5.7);
`$inputParams`; a geometry's length and area; `$image | src` (a fetch from an address is an SSRF door, ADR-265 §5.3's reasoning; the form's own media could
be read without one); `attachmentsDefExp`; EXIF; Survey123's 2,000-record queued jobs, and *Recent tasks*. A Word list
(`w:numPr`) inside a repeat keeps numbering across rows as Word decides. Templates are checked against a schema, not
rendered, at upload: a template Word opens and this engine reads may still look different from Survey123's output.

**Ports created.** None: no Tier 2 dependency. `IReportLookups` is the engine's own seam between the synchronous fill and
the requests a report makes, not a port to a library.

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
   **PARTLY DISCHARGED 2026-11-17 — Word, not LibreOffice.** Sixteen public templates (ArcGIS Online items of type *Microsoft
   Word*, two of them published by the ArcGIS_Survey123 account and two by LocalGovTryItLive; `SOURCES.md` in
   `D:\temp\report-templates\2026-11-17`, not in the repository) were each uploaded against a form built from its own
   placeholders, three records filled with photographs, choices and repeat rows, and reported merged and separate. Thirteen
   filled — a summary of grouped statistics with sixteen `where … !important` sums, a 1,538-placeholder inspection of 82
   pages and 162 pictures, `checked` boxes, `selected` conditions, files' sizes in brackets, `$shape` and location maps,
   bullets, multiline answers. Microsoft Word 16, hidden with alerts off, opened all twenty-five documents (thirteen merged,
   twelve single) without a repair prompt and exported each to PDF. What they needed is §5.12; the three not filled are
   refused at upload with their reasons. **Not done: LibreOffice**, which is not on this machine and was not installed
   (the brief forbids installing software on it); the half of this condition it names stays open.
2. **The owner confirms the INFERRED list** of §5.9, (a) to (q), through [Q-237](../open-questions.md). **DISCHARGED 2026-11-17:** every point confirmed, (p) as Word only and (ac) the other way (§5.14).
3. **PDF is decided** (Q-237): a converter adopted behind a port with its ADR, a Tier 1 layout, or Word only, written down. **DISCHARGED 2026-11-17:** Word only, by owner decision (§5.14) — and decided again the same evening the other way: a converter behind a port, with its ADR, [ADR-302](ADR-302-a-feature-report-is-also-a-pdf-laid-out-by-officeimo.md).
4. **Maps are drawn** for `${location | map}` and `$shape` on ADR-265's renderer, or the refusal stays and is recorded as
   the decision. **DISCHARGED 2026-11-17** — §5.10: drawn by `PrintEndpoints.DrawMapImageAsync`, ADR-265's `MAP_ONLY`
   drawing, as the asker, the records marked; measured on the fixture (§4). `rotate` and `mapFilters` stay refused, and a
   basemap at another address is left out and said, as ADR-265 §5.3 leaves it out of a print.
5. **The cost is measured**: 100 records with a photograph each, through the Inbox, on the showcase — time, peak memory and
   size — against §5.9 (e)'s bounds, which are this session's guesses. **PARTLY DISCHARGED 2026-11-20 — on a local fixture, not on the showcase** (branch night-server; Windows, PostgreSQL 16 +
   PostGIS, the host built from c4bc8b60 plus the night's commits, on a machine running four other builds). A member made for
   the run submitted 100 records to a form with an Inbox (limit 1,000), each with a distinct 1600×1200 JPEG (49.2 MB in all,
   about 490 KB each), and asked for reports of all 100 **through the Inbox** (the member cannot read the form's layers), with
   a template placing the photograph at 400×300; the server's working set polled every 50 ms from outside:
   - **separate `.docx`, zipped: 4.3 s, 45.5 MB**, working set 275 → 512 MB (+238 MB);
   - **merged `.docx`: 3.8 s, 45.3 MB**, +227 MB;
   - **merged PDF** (ADR-302): 10.9 s, 49.2 MB, server +298 MB, the engine's child 474 MB at its peak.
   Against §5.9 (e): 100 records were answered at once well inside a request's patience; the pictures were a quarter of the
   200 MB bound; the server's peak rise (≈ 240 MB for 49 MB of photographs, about five times the pictures) is what the bound of
   two reports at once per node multiplies — two such reports would add about 0.5 GB. **Left: the same run on the showcase**
   (arm64, its own memory and disk), which tonight's brief does not let this session touch; the script is
   [benchmarks/feature-report-cost/inbox_report.py](../../benchmarks/feature-report-cost/inbox_report.py) and takes the server's address, an administrator and the server's process id.

## 8. Dependencies

**Depends on:** [ADR-287](ADR-287-a-form-is-an-xlsform-filled-online-or-off.md) (forms, §5.9 (h)),
[ADR-264](ADR-264-an-app-or-a-link-is-an-item-and-an-item-keeps-resources.md) (item resources),
[ADR-300](ADR-300-a-forms-inbox-is-a-view-each-reader-sees-their-own-rows-of.md) (the Inbox a member reads through),
[ADR-113](ADR-113-a-hosted-layer-has-views.md) (views and their attachments),
[ADR-265](ADR-265-the-server-prints-a-web-map.md) (its renderer draws a report's maps, §5.10; read for PDF).

**Depended on by:** —

## 9. Revisit triggers

- A customer template measured failing in Word after filling (condition 1).
- A shop that needs PDF to move its forms (condition 3), or a map's `rotate` or another origin's basemap in a report.
- A public template measured failing that §5.12 does not read.
- A report request measured past ten seconds at 100 records, or refused `503` in normal use.

## 10. Dissent

None recorded. ADR-287 §5.9 (h) expected the report to be built on the print service; §1 records why it is not.
