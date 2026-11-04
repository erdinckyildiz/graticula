# ADR-268 — A PDF's first page is its picture, drawn by PDFium in the import reader

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the payload and the isolation, measured; `MEDIUM` for the default width |
| **Decided** | 2026-11-04, by owner decision on [D-321](../architecture-debt.md): asked whether a PDF's first page should become its picture with PDFium, *"Evet, PDFium ekle"*, and, when the session's permission check refused to run the downloaded library, *"sen çalıştır"*. |
| **Supersedes** | — ([ADR-254](ADR-254-gdal-without-poppler.md)'s revisit trigger *"a product feature needs a PDF reader … it is PDFium or nothing, and it is a separate decision"* — this is that decision) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A document item ([ADR-188](ADR-188-a-document-is-an-item.md)) has had a picture since migration 121, and only when its
owner gives one. Portal draws a PDF's first page for it, and D-321 named that as what a Portal user finds on a document's
page and does not find here. It was parked twice: first because the only PDF renderer in the payload was Poppler, which
is GPL-2.0-or-later, and then because ADR-254 removed Poppler and left nothing that draws a page. ADR-254 said what the
next step would be: PDFium, under BSD-3-Clause, or nothing, decided on its own.

## 2. Alternatives considered

### Alternative A — Nothing; the owner sets the picture by hand

**Argument for.** No native dependency. Portal's *Edit thumbnail* asks the same of its users, and Studio already has
*Set thumbnail…*.

**Argument against.** Every PDF looks the same in a content list until somebody makes a picture of it. That person would
make it by opening the file and taking a screenshot, which is exactly what a server can do for them.

### Alternative B — GDAL's PDF driver

**Argument for.** GDAL is already in the reader.

**Argument against.** ADR-254 removed it on purpose. Its renderer was Poppler, and its PDFium build is not packaged by
conda-forge. Bringing it back reopens a closed licence question to draw one page.

### Alternative C — PDFium, as its own library, in the import reader (chosen)

**Argument for.** BSD-3-Clause, the renderer Chromium uses, and prebuilt for every platform this product runs on by a
project that publishes its build arguments. One file per platform.

**Argument against.** About 8 MB more per image. It is also a parser of a stranger's file, written in C++.

## 3. Counterarguments to the preferred option

- **A memory-unsafe parser of untrusted files.** That is true of every PDF reader, and it is why the parser runs in
  the import reader's process (ADR-009 §2.2), never in the serving one. The host bounds that process, kills it and
  counts its deaths. The build has V8 and XFA off (`args.gn`), so a document's scripts and forms are not run.
- **A third-party binary rather than a build of our own.** It is pinned by SHA-256, its source and build arguments are
  public, and the lock names the release. Building PDFium from source means building Chromium's toolchain for two
  architectures, which is out of proportion to one picture.
- **A picture nobody asked for.** It is drawn only into an empty slot, and an owner's picture always wins (§5).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| One library per platform, small | `libpdfium.so` is 8.1 MB on linux-arm64 (3.7 MB packed) and 8.0 MB on linux-x64; `pdfium.dll` is 7.6 MB on win-x64. | `chromium/8125`, 2026-10-26, measured 2026-11-04 |
| Needs nothing a server image lacks | `DT_NEEDED`: `libc.so.6`, `libm.so.6`, `libpthread.so.0`, `libgcc_s.so.1`; highest GLIBC symbol 2.16 (x64) and 2.17 (arm64); no libstdc++ (libc++ is linked in). `tools/pdfium-natives.py` refuses anything else. | read from the ELF files |
| Licences permit it | PDFium BSD-3-Clause. Its `licenses/` lists abseil, AGG 2.3, dragonbox, fast_float, FreeType (the FTL is taken), HarfBuzz, highway, ICU, lcms, libjpeg-turbo, OpenJPEG, libpng, llvm-libc, simdutf and zlib. The three files that mention the GPL do not bind: ICU's are autoconf exceptions for scripts that are not shipped, and llvm-libc's and dragonbox's are the LLVM exception to Apache-2.0. | the archive |
| Fast, small | Page 1 at 600 px wide: 4–12 ms; peak working set 23–24 MB against 10 MB for bare Python, so about 13 MB for PDFium. Same result on a 149-page, 4.3 MB document and a 10-page, 0.5 MB one. | win-x64, 2026-11-04 |
| Works through the reader | `{"op":"pdfpage"}` answered a 600 × 849 PNG of the 149-page document (14.8 KB). A file that is not a PDF was refused with PDFium's error 3. | `Graticula.Import.Reader`, 2026-11-04 |
| Works end to end | `ADocumentIsAnItemTests.A_pdfs_first_page_is_its_picture_until_its_owner_gives_one`: upload, the owner's picture kept through a replacement, a CSV taking the drawn picture away. | fixture, 2026-11-04 |

## 5. Decision

1. **PDFium from [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries), release `chromium/8125`,**
   pinned per platform by URL and SHA-256 in `deploy/pdfium/pdfium-natives.lock.json`. `tools/pdfium-natives.py fetch`
   lays out `pdfium/<rid>/` beside the reader, with the library, its `VERSION` and every notice in `licenses/`. It
   refuses a changed checksum, an archive member outside the folder, and a library that needs more than glibc's own
   libraries.
2. **Only the import reader loads it.** It loads the library by full path, resolves each entry point, and opens the
   document from memory. The host never loads it. GDAL still has no PDF driver, and `gdal-natives.py` still refuses a
   PDFium inside GDAL's closure. The image's name check allows `pdfium` only under `/app/importer/pdfium/`.
3. **A PDF's page 1 is drawn 600 px wide**, the width of ArcGIS's own thumbnails, and at most twice that tall, on
   white, with its annotations, as a PNG. **`INFERRED`: the width.**
4. **Drawn after a document is filed or its file replaced**, through Studio, the portal's `addItem` (which uses Studio's
   handler) or the portal's `update`, **into an empty slot or over a picture this server drew, never over an owner's.**
   Migration 132's `document.thumbnail_drawn` keeps the two apart. A file that is no longer a PDF takes a drawn picture
   with it. Setting a picture by hand marks it as the owner's. Removing it gives the slot back to the server.
5. **A picture is never why an upload fails.** No reader, a password, a damaged file, or a deadline of 20 s passed: the
   document is kept without a picture, and the reason is logged.

## 6. Consequences

**Positive.** Every PDF in a content list shows what it is, as in Portal. D-321 closes.

**Negative.** About 8 MB more per image. A second untrusted-file parser runs in the reader. The upload request waits for
the drawing, which takes milliseconds measured, plus the reader's start when it is not running.

**Ports created.** None beyond the reader's process boundary, which is the port: the host asks `pdfpage` and receives
PNG bytes, and no PDFium type crosses it.

- **State.** Built and tested on the fixture (win-x64). The images carry PDFium for linux-x64 and linux-arm64; condition
  1 is the check that they work.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | bblanchon's builds stay published at a pinned URL; a missing one fails the build, never a deployment. | Accepted |

## 8. Dependencies

**Depends on:** [ADR-009](ADR-009-raster-engine.md) §2.2 (an untrusted file's parser stays out of the serving process),
[ADR-188](ADR-188-a-document-is-an-item.md), [ADR-254](ADR-254-gdal-without-poppler.md).

**Depended on by:** —

## 9. Revisit triggers

- A PDFium security release: the lock moves to it, as GDAL's does.
- bblanchon stops publishing an architecture this product ships.
- A member asks for a page other than the first, or for pictures of Office documents. The second is LibreOffice, a
  different and much larger decision.

## 10. Conditions

1. **The images draw a page.** A release's linux-arm64 image draws page 1 of a PDF uploaded to the showcase, and its
   size is recorded beside 1.0.503's.
2. **The width is confirmed by the owner, or changed** (§5.3, `INFERRED`).

## 11. Dissent

None recorded.
