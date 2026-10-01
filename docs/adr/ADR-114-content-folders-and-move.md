# ADR-114 — A member's content has folders, and an item is moved between them

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` — a table, a column per item kind, and Portal's documented REST shape |
| **Decided** | 2026-10-01, by owner decision (*"Tamamı"*, the fourth of five questions from the ArcGIS review's second pass: content folders separate from service folders) |
| **Supersedes** | ADR-108's deferral of *Move* |
| **Superseded by** | — |

---

## 1. Context

Portal's *My content* has folders down its left side, and an item's *Move* puts it in one. They are the member's own:
they belong to one user, change nothing about the item's URL, and are unrelated to ArcGIS Server's service folders,
which are part of a service's address. This server had only the second kind — `/admin/folders`, the folder in
`/rest/services/{folder}/{name}` — and the portal face answered every member's `folders` with `[]`, so ArcGIS Pro's
*Catalog › Portal › My Content* showed no folders at all, and a search for `ownerfolder:` was accepted and ignored.

The reviewer's warning, ranked first: **moving an item between content folders must not change its URL**. Mixing the
two kinds of folder is the mistake a long-time ArcGIS user would notice at once.

## 2. Alternatives considered

### Alternative A — A `content_folder` table, and a nullable folder on each item kind (chosen)

**Argument for.** Two item kinds exist (a service and a web map); each gets a column that names a folder or none (the
root, *My content* itself). A folder is a row with an owner and a title, unique per owner without regard to case. The
URL-bearing service folder is untouched.

### Alternative B — Reuse the service folder

**Argument against.** It is part of the URL. Moving an item would break every map and script that uses it — the
failure named above.

### Alternative C — Wait for ADR-056's item table

**Argument against.** ADR-111 made the same choice for tags: a column per item kind now, moved into the item table
when it is written.

## 3. Counterarguments to the preferred option

- *An image service (coverage) is an item too*, and it stays at the root in v1: it has no folder column yet.
- *An administrator cannot list another member's folders* from Studio yet; they move an item among its owner's
  folders only through the API, naming the folder's id.
- *Portal's write operations here take a form*, which a browser may send cross-site without a preflight. Each is
  refused when the request is signed in only by the session cookie from another origin, as `exportTiles` is.
- *A folder's id is a GUID, not its title*, as Portal's is — so renaming a folder breaks nothing that holds its id.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Portal's content folders: per user, `ownerFolder` on the item, `createFolder`, `delete` (empty only), `move`, `moveItems` | ArcGIS REST API *User Content*, *Create Folder*, *Delete Folder*, *Move Item*, *Move Items* | publicly documented |

## 5. Decision

5.1 **Catalogue.** Migration 72: `content_folder (id, owner_principal_id, title, created_at)`, a title unique per
owner without regard to case; `service.content_folder_id` and `web_map.content_folder_id`, null for the root,
`on delete restrict`.

5.2 **Rules.** A folder is its owner's. An item is moved by its owner, or by an administrator **into one of the
owner's folders** — never into the administrator's own, which is what *Change owner* is for. A folder is deleted only
when empty: a folder with items is refused rather than emptied, so no delete of a folder deletes an item (§7). *Change owner*
puts the item at the new owner's root. Moving changes no URL, id or sharing.

5.3 **Native surface.** `GET /content/folders` (the caller's own), `POST
/content/folders` `{title}`, `PUT /content/folders/{id}` `{title}`, `DELETE /content/folders/{id}`, and `POST
/content/move` `{items: [{service, folder} | {webmap}], to: id | null}` answering per item. `/content/items` gives each
item its `contentFolder`.

5.4 **Portal face.** `/sharing/rest/content/users/{u}` lists the root's items and `folders: [{id, title, username,
created}]`; `/content/users/{u}/{folderId}` lists one folder's; `createFolder`, `{folderId}/delete`,
`items/{id}/move` and `moveItems` do what 5.3 does; an item says `ownerFolder`; `ownerfolder:` in a search filters.

5.5 **Studio.** *My content* lists the caller's folders beside the items, with New folder, rename and delete, and
filters by the folder chosen; *Move* moves the ticked items, and an item's page offers it too.

## 6. Consequences

**Positive.** Pro and the Python API see a member's folders; the service folder keeps its one meaning.

**Negative.** A third place (after tags) that ADR-056's item table will absorb.

**State.** One table and two columns.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Refusing to delete a folder that holds items is acceptable to an ArcGIS user | `INFERRED` — the reviewer said Portal refuses; the public REST reference for *Delete Folder*, as I recall it, says the folder's items are deleted with it. Not checked against a running Portal. Refusing is chosen because it cannot lose data. **Confirmed by the owner 2026-10-01** (*"Tamam mantıklı ikisi de"*) |

## 8. Dependencies

**Depends on:** ADR-108 (change owner), ADR-111 (a column per item kind until ADR-056).

**Depended on by:** —

## 9. Revisit triggers

- ADR-056's item table is written.

## 10. Conditions

1. **Folders are made, renamed, listed and deleted only when empty; items move between them without their URL
   changing** — **DISCHARGED 2026-10-01**, `ContentFoldersConformanceTests`.
2. **The portal face lists folders and each folder's items, moves items, and filters `ownerfolder:`** —
   **DISCHARGED 2026-10-01**, `ContentFoldersConformanceTests`.
3. **Studio's My content shows folders and moves items** — **DISCHARGED 2026-10-01**, `ContentFolderScreenTests`.
