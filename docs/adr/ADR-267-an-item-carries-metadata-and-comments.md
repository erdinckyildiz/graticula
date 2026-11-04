# ADR-267 — An item carries metadata and comments

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the wire shapes are Esri's public REST reference and the ArcGIS API for Python, measured against the fixture; whether comments are on by default, who may delete one, and the limits are this session's, `INFERRED` and listed for the owner |
| **Decided** | 2026-11-04, overnight, on the owner's instruction to take the ArcGIS veteran's review item #18 (*item metadata and item comments*). Every point the owner did not state is marked `INFERRED` below. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A Portal item carries two things this portal did not keep. **Metadata**: an XML document — ArcGIS's own format, ISO 19139
or FGDC — at `content/items/{id}/info/metadata/metadata.xml`, written by `update`'s `metadata` file part. ArcGIS Pro's
Share pane and the ArcGIS API for Python (`item.metadata`, `item.update(metadata=…)`) read and write it. **Comments**:
`content/items/{id}/comments`, `addComment`, `comments/{commentId}` and its `delete`, with `numComments` on the item and
`commentsEnabled` on `portals/self`. Both answered 404 here, and `update` refused a `metadata` part by name.

## 2. Alternatives considered

### Alternative A — Leave both out

Portal clients keep failing on them, and the veteran's review keeps the item open. Not chosen.

### Alternative B — Generate metadata from the item's facts when none was written

A minimal ArcGIS-format document from the title, summary, description, tags, extent and credits, served whenever the item
has none. **Argument for:** every item would answer something. **Argument against, which decided it:** the ArcGIS API for
Python documents *"Items with metadata have 'Metadata' in their typeKeywords"* and reads a refusal as *no metadata*; an
answer for every item tells every client that somebody wrote metadata when nobody did, and a client that saves it back
turns the made-up document into a stored one. Not chosen; recorded as `INFERRED` (§5.3).

### Alternative C — Merge an uploaded document's title, tags and summary back into the item

Pro's metadata editor and Portal's *Edit metadata* can keep the two in step. **Argument against:** which fields win when
they disagree is a product decision with no answer in the public documentation, and a silent write to the item's own
title from an upload is the kind of change this repository's update path refuses to make silently (ADR-214). Not chosen;
the document is stored as sent and the item is untouched (`INFERRED`, §5.3).

### Alternative D — Store and serve metadata as sent; keep comments as rows *(chosen)*

## 3. Counterarguments to the preferred option

- **Stored XML is a stored-XSS and XXE surface.** It is: an XML document with an XHTML namespace runs script when a
  browser renders it from this origin, and a DTD can read files or expand without bound. Both are closed by how it is
  parsed and served (§5.2), and tested.
- **Comments are user content in a product that so far had none from members to members.** Abuse handling (rate limits,
  reports) is not built; an administrator can turn comments off for the organisation and delete any comment.
- **`numComments` and `Metadata` appear on the item read by id, not on search results.** A gallery that sorts by
  `numComments` sees nothing. Condition 2.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `update` takes a `metadata` file; it is served at `info/metadata/metadata.xml` | *"The file that stores the metadata information for an item"* | Esri REST reference, *Update Item* |
| `portals/self` says `metadataEditable` and the organisation's metadata style, default `iso19139` | as quoted | same page |
| A comment is `{id, owner, created, comment}`; the list pages with `total`, `start`, `num`, `nextStart` | response example | Esri REST reference, *Item Comments* |
| Only authenticated users who can access the item add comments; author and item owner delete | as quoted | *Add Comment*, *Delete Comment* |
| The Python API reads metadata presence from `typeKeywords` and an error as none, writes through `update`'s `metadata` part, posts `comments` with `start`/`num`, reads `comments/{id}`, deletes with `…/delete` | `arcgis` 2.4 source in `D:\temp\arcgis-env`, read for the wire shape only | measured against the fixture, 2026-11-04 |

## 5. Decision

1. **Two tables keyed by the item** (migration 131): `item_metadata` (one document per item, at most 4 MB) and
   `item_comment` (id, item, author's id and name, text up to 2,048 characters, time). Both cascade from `item`, so the
   recycle bin keeps them with a deleted item and restores them with it (ADR-203 follows the store's keys), and a
   permanent delete removes them. Both are floored by tenant row-level security, as migration 128's `item_resource`.
   A comment names its author rather than referencing `principal`, so restoring an item never fails because a commenter
   was removed since.
2. **Metadata.** `update` — for a service, a web map, a document or an app — takes a `metadata` file part (or a field
   of that name), checked before anything in the update is written: at most 4 MB; parsed with `DtdProcessing.Prohibit`,
   no `XmlResolver` and no entity characters; well-formed to its end; its root `metadata` (ArcGIS, FGDC) or `MD_Metadata` /
   `MI_Metadata` (ISO 19139). Kept byte for byte. `info/metadata/metadata.xml` serves it to whoever may read the item,
   as `application/xml` with `nosniff` and `Content-Security-Policy: sandbox; default-src 'none'`; an item without one is
   answered 400 *Metadata for item not found.* The item read by id carries `Metadata` in `typeKeywords` when it has one.
   `portals/self` says `metadataEditable: true` and `metadataFormats: "iso19139"`.
3. **`INFERRED`, confirmed by the owner 2026-11-04** (*"Hepsi önerildiği gibi"*): metadata is **not generated** from the item's facts and an upload is **not merged back** into them
   (§2 B and C).
4. **Comments.** `comments` (a page, oldest first), `comments/{id}`, `addComment` (`comment`), `comments/{id}/update`
   (its author only) and `comments/{id}/delete` (its author, the item's owner, or whoever may manage all content). Read
   by whoever may read the item; added by a signed-in caller who may read it. The item read by id carries `numComments`.
   Text is stored as given and escaped wherever Studio shows it. Replies (`parentId`) are not kept.
5. **`INFERRED`, confirmed by the owner 2026-11-04** (*"Hepsi önerildiği gibi"*): **anonymous callers cannot comment**, even on a public item; **comments are on by default**, as
   Portal's are; an administrator with `admin:manageServer` turns them off for the organisation
   (`/admin/settings/item-comments`, an organisational setting), which `portals/self`'s `commentsEnabled` then says and
   `addComment` refuses. Comments already left are kept and still listed.
6. **Studio.** A document's and a web map's page has a panel under the description: whether the item has metadata,
   *Download metadata* when it does, *Upload / Replace metadata…* for whoever manages the item, the comments with Delete
   where the reader may, and an add box. Organization › Settings › General has a *Comments on items* checkbox for an
   administrator.

## 6. Consequences

- **State.** Two tables in the catalogue, `item_metadata` and `item_comment`, tenant-floored and carried by the recycle
  bin; one organisational setting, `item_comments`; nothing cached at runtime.
- Pro's and the Python API's metadata round trip works here (measured: `item.metadata = …` writes, reads back, and a DTD
  is refused); `item.add_comment`, `item.comments` and `Comment.delete()` work.
- The ArcGIS API for Python 2.4 writes the error body to `metadata.xml` and returns its path for an item with none — its
  own handling, the same against a Portal; the item's `typeKeywords` is the reliable presence flag.
- No abuse controls on comments beyond the length, a per-item ceiling of 10,000, the organisation switch and deletion.

## 7. Conditions

1. A service's item page in Studio shows the same panel. *(Only documents and web maps have it; the service page's
   structure is the larger one and was left for the ux review.)*
2. Search and content listings carry `numComments` and the `Metadata` type keyword, read in one query per page, so a
   client that sorts or filters by them sees the truth.
3. `addItem` takes a `metadata` part as `update` does.

## 8. Dependencies

- [ADR-188](ADR-188-a-document-is-an-item.md) — documents as items.
- [ADR-203](ADR-203-a-deleted-item-waits-in-the-recycle-bin.md) — the recycle bin, which follows the store's keys.
- [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) — tenant floors and organisational settings.
- [ADR-214](ADR-214-the-portals-item-writes-do-what-they-answer.md) — an update is made whole or not at all.
- [ADR-264](ADR-264-an-app-or-a-link-is-an-item-and-an-item-keeps-resources.md) — item resources, whose store and
  serving rules this copies.
