# ADR-188 — A document is an item: a file kept in the platform store and shared like a web map

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-16, by owner decision. Asked whether to build a document item kind (ADR-056 condition 2) after being told the web map already proves the model, the owner answered *"b) yapılsın. arayüzünü stitch tasarlayabilir çok lazımsa. biz o arada devam edebiliriz."* — build it; the screen may be designed by Stitch; the rest goes ahead meanwhile. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A Portal holds more than maps and services. An organisation files the PDF that documents a dataset, the workbook a
layer was built from, the specification a field crew works to, a picture, beside the layers they describe, and shares
them with the same groups. Until this ADR the item table ([ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md),
migration 87) held two kinds, and both are ours to draw: a service and a web map. A file somebody brings is the first
item whose content this server neither made nor understands.

That difference is the whole of the risk. A web map is JSON this server writes and reads; a service is rows it
queries. A document is bytes chosen by whoever uploaded them, served back from this server's own origin to everybody
the item is shared with — which is where a stored file becomes a stored script if it is ever served as something a
browser runs.

## 2. Alternatives considered

### Alternative A — the bytes in the platform store, one row per document *(chosen)*

**Argument for.** The baseline deployment is *graticula → PostgreSQL/PostGIS* (CLAUDE.md §6). A document kept beside
its item is backed up by the backup that already exists, deleted by the cascade that already deletes items, moved with
the catalogue's database, and seen identically by every node. Nothing new has to be mounted, shared or swept.

**Argument against.** `bytea` is read whole: a 64 MB document is 64 MB of memory per download in flight, and a
platform store full of files is a slower backup. Large documents are what a file system is for.

### Alternative B — the bytes on disk under the state path, the row naming the file

**Argument for.** Streams; costs no database space; a 1 GB file is as easy as a 1 MB one.

**Argument against.** A second thing to back up, which nothing in the product does today and which a restore can
silently miss; two nodes need a shared file system the product has never been run on (deployment.md §1); a deleted
item leaves a file unless a sweeper exists, and an orphan sweeper is a delete with no user behind it.

### Alternative C — PostgreSQL large objects

**Argument for.** Streams from the database.

**Argument against.** A large object is not deleted by deleting the row that references it, so every delete path —
an item, a member, a folder — needs a second step, and `pg_dump` handling of large objects is one more thing a restore
can get wrong. It buys streaming at the price of Alternative B's orphan problem inside the database.

## 3. Counterarguments to the preferred option

The memory cost is real and is bounded rather than removed: a document is limited to `Graticula:DocumentMaxMB`
(default **64**, the same number as a data upload), and a deployment that raises it is choosing memory per download.
If documents of hundreds of megabytes turn out to be ordinary, Alternative B is the move, and the row shape below
(name, media type, size, a content column) is what would let it be made without changing any API.

## 4. Evidence

- Migration 87 already made room: *"An item with neither reference is the item that is not of anything — ADR-056
  condition 2's kind"* — `item_kind_matches` holds for a kind with both references null, so the item table needs only
  the new name in `item_kind_known`.
- The upload limits a deployment may change were made settings the same day ([Q-175](../open-questions.md)), and the
  document limit follows the same shape.
- ArcGIS Portal's own item types for these files are `PDF`, `Microsoft Word`, `Microsoft Excel`, `Microsoft
  Powerpoint`, `CSV` and `Image` (the ArcGIS REST API's *Items and item types* page), and a portal client downloads one
  from `/sharing/rest/content/items/{id}/data`.

## 5. Decision

1. **A document is an item of kind `document`** with neither a service nor a web map behind it. Its title, snippet,
   description, tags, owner, sharing scope, groups, folder and delete protection are the item's, exactly as a web map's
   are, so every rule that decides who sees an item decides who sees a document without being told about it.
2. **Its bytes live in a `document` table of the platform store** — the item's id, the file name it was uploaded
   under, its media type, its size, the bytes, and its created and updated times (the item carries no times, as
   migration 87 said an item of nothing would need). Deleting the item deletes the row by cascade.
3. **Only files that are documents are accepted, and the server decides what a file is.** PDF, Word, Excel and
   PowerPoint (OOXML), CSV, PNG, JPEG, GIF and WebP — recognised by their first bytes and their extension together,
   never by the content type the client sent; a CSV, which has no signature, must be UTF-8 that does not open like
   markup. HTML, SVG, scripts, executables and anything unrecognised are refused, because each is something a browser
   may run. **Plain text was in the first draft and is not accepted**: ArcGIS has no item type for it, and calling it
   one of the others would tell a portal client something false.
4. **A document is always served as a download**: `Content-Disposition: attachment`, the media type this server
   decided, `X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox`. Nothing uploaded is ever rendered
   by this origin.
5. **The portal face reads documents; Studio writes them.** Search, a member's content, a folder, a group's content
   and the item page list a document with its ArcGIS type, and `/items/{id}/data` downloads it, under the item's
   sharing. Uploading, replacing and deleting are Studio's admin API — [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md)
   §5 and [Q-159](../open-questions.md)'s *"şimdilik hayır"* stand for `addItem` from ArcGIS clients.
6. **The limit is a setting**, `Graticula:DocumentMaxMB`, default 64, at least one; a refusal names it.
7. **The screen is designed outside this change.** The API is built and tested first; Studio's *add a document* and
   the document's item page are drawn by Stitch or by the redesign ([ADR-187](ADR-187-primary-navigation-is-a-top-bar.md)),
   from the brief in `docs/design/document-items.md`.

## 6. Consequences

**Positive.** A portal user can file a dataset's documentation beside it and share both with one group. The item model
carries a kind it does not understand, which is the claim ADR-056 made.

**Negative.** The platform store grows with uploaded files, and its backup with it. Until the screen exists, a document
is added through the admin API only. A download holds the whole file in memory. **A catalogue manifest does not carry
documents**, as it does not carry web maps: a manifest describes what is published, and moving files between
deployments is a backup's job.

**State.** In the catalogue (the platform store, shared by every node): an `item` row of kind `document` and a
`document` row holding the file, its name, kind, size and times (migration 89). Nothing at runtime: a download reads
the row and holds the bytes only while it writes them.

**Ports created.** None.

## 7. Assumptions this decision rests on

- **[A-084](../architecture-assumptions.md)** — documents filed beside layers are mostly small, well inside 64 MB.
  `UNVALIDATED`; the setting is the escape, and Alternative B the move if it is wrong.
- **[A-085](../architecture-assumptions.md)** — the first bytes and the extension together are enough to refuse every
  type a browser would run. `REASONED`; every answer being a sandboxed attachment is the second line.

## 8. Dependencies

**Depends on** [ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md),
[ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md), [ADR-185](ADR-185-sharing-is-additive.md).

**Depended on by** —

## 9. Revisit triggers

- A deployment raising `DocumentMaxMB` past a few hundred megabytes, or a platform store whose backup is dominated by
  documents → Alternative B.
- An ArcGIS client that needs to *add* a document (`addItem`) → Q-159 reopens.

## 10. Conditions

1. **Every listing that shows a web map shows a document**, and one test creates a document and walks each of them:
   the admin content list, search, a member's content, a folder, a group's content, the item and its data.
   *(Discharged 2026-10-16 — `ADocumentIsAnItemTests` files a PDF and finds it in Studio's list, by `type:PDF` in
   the portal's search, in the member's content and then in a folder of it, as the item, and as data from both faces;
   a group's content is held at the store by `DocumentStoreTests`, which shares one into a group and reads it back
   listed as *Document* — the label the first build would have written as *Web map*. OGC API Records lists it with a
   link to its file.)*
2. **Nothing uploaded is served as active content.** A test uploads an HTML file renamed `.pdf` and an SVG and sees
   both refused, and asserts the four download headers of §5.4 on a document that was accepted.
   *(Discharged 2026-10-16 — `ADocumentIsAnItemTests` sends HTML named `.pdf`, an SVG, markup named `.csv` and an HTML
   file, each refused 415, and asserts `attachment`, the decided media type, `nosniff` and `sandbox` on the accepted
   PDF from Studio, from the portal, and to an anonymous caller once it is public; twenty cases of the recognition
   itself are `ADocumentIsWhatItsBytesSayTests`.)*
3. **A document follows its item through every move an item makes**: shared, unshared, moved to a folder, given to
   another member when its owner is removed, and deleted with its owner's content.
   *(Discharged 2026-10-16 — shared and unshared, `DocumentStoreTests`; moved to a folder, both test files;
   transferred with its folder and deleted with its owner, `MemberRemovalTests`. The first build would have failed the
   last two, found by tracing every path a web map takes before writing any: a removed member's document survived
   owned by nobody, and one filed in a folder stopped the removal. With the two steps taken out the test fails at the
   transferred document, which is how it was checked.)*
4. **Studio can add, replace and delete a document**, on a screen designed from the brief — waits on the screen.
   *(Open. The brief is [document-items.md](../design/document-items.md); the screen is ST-31 in the inventory.)*
   **DISCHARGED 2026-10-28**, the console's UI having come back that day. Content lists documents beside services and
   web maps — a tile with the kind's glyph and letters, the kind in words, its size — and they are ticked, moved,
   shared and deleted in bulk with the rest. *New item › Document* uploads one (a PDF, Word, PowerPoint file or picture
   dropped on New item lands there too; a CSV or Excel table stays the layer's) with a determinate bar, refuses no file,
   a kind not accepted and a file over the limit before sending, shows the server's own refusal with the file kept,
   and lands on the new item. The item page (`#/item/<id>`, shaped like a web map's) has Download (asking first,
   `check=1` added to `/data`), Share with groups, Replace file, Move, Edit details, and Protect and Delete under
   Settings. `DocumentScreensTests` drives it against a real document the test files and purges. A ux-designer review
   sent the first build back — the page reused the web map's markup and none of its styles, the forms were unstyled,
   focus fell to the page after Delete, and the server answered an HTML file named `.pdf` with *'.pdf' is none of
   these* beside a list naming PDF, which now says the name is right and the inside is not
   (`ADocumentIsWhatItsBytesSayTests`). **Not built, from the same review:** a picture of the file as its thumbnail,
   Add favorite, the item id with a copy link, terms of use and credits, and the folder in the breadcrumb — a web map
   has the first three and a document should — [D-321](../architecture-debt.md).

## 11. Dissent

None recorded. The choice of the platform store over the file system is the one a reviewer could reasonably make the
other way, and §3 says when it should be.
