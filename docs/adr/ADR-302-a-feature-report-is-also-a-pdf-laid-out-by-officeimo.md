# ADR-302 — A feature report is also a PDF, laid out by OfficeIMO in a child process

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — thirteen public Survey123 templates, filled and laid out on Windows and on the showcase's own image on arm64, compared page by page with the PDF Microsoft Word 16 exported of the same documents (§4); two of the thirteen are refused by the engine, and none of the documents is a customer's |
| **Decided** | 2026-11-17, evening, by the owner: *"bence pdf'i de ekleyelim"*, reversing the morning's *"Yalnız Word kalsın"* ([ADR-301](ADR-301-a-feature-report-is-a-word-template-filled-per-record.md) §5.14). The engine was chosen with the owner in three steps the same evening: LibreOffice was set aside as too heavy (*"kullanabileceğimiz daha lightweight bir şey yok mu?"*), MigraDoc chosen and then dropped when its licence was read (§2 B), and *"başka bir şey bak. githubta falan yok mu?"* found OfficeIMO, measured before it was adopted (*"başla"*). |
| **Supersedes** | [ADR-301](ADR-301-a-feature-report-is-a-word-template-filled-per-record.md) §5.7 and §5.9 (p), which said no PDF |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
>
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

## 1. Context

ADR-301 makes a feature report the way Survey123 does: a Word template the form's owner uploads, filled per record by
the server's own engine. Survey123 offers the same report as PDF beside Word, and many shops hand the PDF over. ADR-301
§5.7 left PDF out because a faithful `.docx` → PDF needs a page-layout engine — fonts and their metrics, line breaking,
tables, pagination, headers and footers — and that is a dependency decision or a large Tier 1 build. The owner first
confirmed Word only and then, the same day, asked for PDF.

The document the engine lays out is built from an owner's template with submitters' photographs in it. The project has a
line for code that parses what someone else chose: it runs in a child process the server can stop (ADR-009 §2.2; the
import reader, the overlay worker, ADR-288's offline reader).

## 2. Alternatives considered

### Alternative A — LibreOffice, headless, in a child process

The closest layout to Word that is free, MPL-2.0 and not linked. Rejected by the owner on weight: about 400 MB added to
an image that serves maps, for one button on one screen.

### Alternative B — MigraDoc and PDFsharp, with our own `.docx` reader

A managed layout engine, about 3 MB; the `.docx` would be mapped onto its document model by our code. **Rejected on its
licence:** PDFsharp and MigraDoc 6.5.0 and later (2026-10-14) are AGPL-3.0-only (the nuspec's `licenseExpression`, read
2026-11-17). 6.2.4 is the last MIT release; pinning it would mean no fixes ever again for a library that would read
submitters' photographs. Nothing linked here may be copyleft (ADR-047 §4).

### Alternative C — A Tier 1 layout over SkiaSharp

No new dependency: SkiaSharp is here already, writes vector PDF and shapes every script through HarfBuzz. But line
breaking, tables, pagination and Word's paragraph model would be ours to write — days, and the output would still not be
Word's.

### Alternative D — Typst in a child process, with our own `.docx` → Typst writer

Apache-2.0, a 16 MB static binary for `aarch64-linux-musl`, a mature typesetter. The `.docx` → Typst translation would be
ours, and Word's layout would be approximated by a second model. Kept as the fallback if D's measurements had failed.

### Alternative E — QuestPDF

Dual-licensed since 2026-07-06 (License Selection Guide 3.0): a community licence below a revenue threshold and a paid one
above it. A deployment of this server cannot be told which it needs. Rejected.

### Alternative F — OfficeIMO, `.docx` → PDF in managed code, in a child process *(chosen)*

`OfficeIMO.Word.Pdf` (EvotecIT), MIT; its PDF writer `OfficeIMO.Pdf` is its own, its Word model is
`DocumentFormat.OpenXml` (MIT). One call, `WordDocument.Load(path).ToPdfBytes(options)`. Measured before it was chosen (§4).

## 3. Counterarguments to the preferred option

- **It is young and moves fast.** Ten releases between 2026-09-02 and 2026-11-10. §5.6 pins one two weeks old and the port
  makes the engine replaceable without touching the report.
- **It is not Word.** Two templates of thirteen are refused, three lose something (§4). The Word document stays the report
  and is offered beside every refusal (§5.4).
- **A child process per document costs a process start** — about 0.2 s of a conversion measured at 0.4–14 s.

## 4. Evidence

Thirteen public Survey123 templates (ADR-301 condition 1; `D:\temp\report-templates\2026-11-17`, not in the repository)
filled for three records each and merged, then laid out by OfficeIMO 3.5.3 and compared, first page and page count, with
the PDF Microsoft Word 16 exported of the same document. Prototype in the session's scratch folder, not committed.

| Where | Laid out | Refused | Time per document |
|---|---|---|---|
| Windows, Office fonts installed | 11 | 2 | 0.2–7.1 s |
| The showcase image (arm64), no fonts | 6 | 7 | 0.4–14.8 s |
| The showcase image, §5.2's four font packages | 11 | 2 | 0.5–14.3 s |

- **Close to Word, by eye:** eight of the eleven — coloured header bands, bordered tables, tick boxes, logos, numbered
  lists, footers with page numbers.
- **Readable, not Word's:** one lost its first table's title band and greyed its text; one lost its page-number footer;
  one moved its floating pictures and text boxes. Two came out shorter than Word's (9 pages for 12, 3 for 5): page breaks
  fall elsewhere.
- **Refused by the engine:** *"PDF footer offset must not exceed the bottom margin when footer content is enabled"* and
  *"PDF bookmark names must be unique"* — both inside OfficeIMO.
- **Without fonts** the engine falls back to the PDF standard fonts, which cannot write ☒, and five more were refused.
  With Liberation, Carlito, Caladea and DejaVu installed the Linux output matched the Windows output.

## 5. Decision

1. **A report can be asked for as `pdf`.** `POST /admin/forms/{id}/reports` takes `format: "pdf"` beside `docx`; the
   server fills the `.docx` exactly as ADR-301 does and lays each document out as a PDF — one per submission in a ZIP, or
   one for a merged report. `GET …/reports/templates` lists `pdf` in `formats` only where the engine is installed.
2. **The engine runs in `Graticula.Report.Pdf`, a child process,** started once per document with
   `OfflineUploadReader`'s bounds: a deadline of one minute, a 1 GB managed heap, a 1.5 GB working set polled from outside,
   a lower priority. OfficeIMO is referenced by that project and nothing else.
3. **The image installs the metric-compatible fonts** — `fonts-liberation2`, `fonts-crosextra-carlito`,
   `fonts-crosextra-caladea`, `fonts-dejavu-core` — so a line breaks where Word breaks it and ☒ can be written. CI installs
   the same four where it lays a report out.
4. **A document the engine cannot lay out is answered `422`** with the engine's own reason in `reason`, a sentence that
   offers Word, and `fallback: "docx"`; nothing half-made is downloaded. Studio keeps the person's choice of PDF, says
   *"Not made as a PDF: <reason>"*, and offers a *Create as Word (.docx)* button that takes focus and reads the reason with
   it — after the ux-designer review of 2026-11-17, which refused the first build's silent switch back to Word.
5. **INFERRED** ([Q-240](../open-questions.md)):
   1. one minute per document;
   2. **at most 25 PDF documents per request** in separate mode — a merged report is one document and is not counted; more
      is refused with the choices (merged, fewer, Word);
   3. the engine's default options: installed fonts, nearest installed substitute for a missing Office family, no remote
      resources.
6. **OfficeIMO.Word.Pdf 3.5.3** (2026-10-31), pinned centrally; the newest, 3.6.1, was ten days old.
7. **Studio** offers *Format: Word document (.docx) / PDF* beside the template when the server lists `pdf`, and keeps the
   choice across a redraw. With PDF chosen, the limits line names §5.5's number as the server lists it
   (`maximumPdfDocuments`), and more separate PDFs than that ticked hold *Create report* with the sentence that says why,
   so the server never has to refuse it.

## 6. Consequences

- Survey123's *Format: Word or PDF* has its counterpart, and the Word document is never taken away by it.
- The image grows by the four font packages, a few megabytes, and by the child's assemblies.
- A fourth sibling process: one more directory the image asserts (`/app/reportpdf`), one more line in the start-up log
  when it is missing.
- **State.** None in the catalogue: nothing is stored. At runtime, one child process per document and two files under the
  node's temporary folder, deleted when the document is answered — node-local, and nothing another node needs.

## 7. Conditions

1. **The two refused templates are reported upstream or worked around**, each with what OfficeIMO said, and measured
   again.
2. **The owner confirms §5.5** through [Q-240](../open-questions.md).
3. **Measured on the showcase after the image is built**, not in a container borrowed for the prototype: the thirteen
   templates again, and a 25-submission separate report with photographs — time and peak memory against §5.2's bounds.

## 8. Dependencies

**Depends on:** [ADR-301](ADR-301-a-feature-report-is-a-word-template-filled-per-record.md) (the report it lays out),
[ADR-288](ADR-288-offline-sync-is-in.md) condition 7 (the child-process shape it copies),
[ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md) §4 (nothing linked is copyleft).

**Depended on by:** —

## 9. Revisit triggers

- A customer's template refused or laid out wrong where Word is right — LibreOffice (§2 A) or Typst (§2 D) is measured
  against it.
- OfficeIMO's licence changes, as PDFsharp's did.
- A PDF report measured past the minute, or the 25 documents refused in normal use.

## 10. Dissent

None recorded. ADR-301 §5.7 argued for Word only and the owner confirmed it the same morning; the reversal is the owner's,
recorded in the header rather than smoothed over.
