# ADR-202 — An item has terms of use and categories, and a member keeps favourites

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-18, by owner decision — *"Öğe modeli: geri dönüşüm kutusu + kategoriler"* and the terms of use among the small differences, from the ArcGIS veteran's review (V-96) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The veteran's review (V-96) found the portal's item without what an ArcGIS portal's has beside it: **terms of use**
(`licenseInfo`, which an open-data portal needs), **content categories** (`categorySchema` answered empty, the item
`categories: []`), **favourites**, and a **recycle bin**. Migration 87 made the item one record for every kind, so
these can be said once for all of them.

## 2. Decision

- **Terms of use** are the item's `license_info` (migration 93), free text up to 64,000 characters, set at
  `PUT /content/items/{id}/license` by the item's owner or whoever manages all content, and answered as `licenseInfo`
  on every kind's portal document. *ArcGIS's reference calls it "access and use constraints"; a service's OGC
  documents state theirs separately (ADR-167), and the two are not yet one fact* — condition 2.
- **Content categories** are a tree of the organisation's own (`item_category`), apart from migration 90's member
  categories as Portal keeps the two apart, managed at `/admin/item-categories` under `admin:manageAllContent`. An item
  is put in them at `PUT /content/items/{id}/categories` by path (`/Categories/Water/Rivers`) or id. `categorySchema`
  answers the tree under ArcGIS's one root, `hasCategorySchema` says whether there is one, and each item's document
  carries its paths.
- **Favourites** are a member's own list (`item_favorite`), marked at `PUT`/`DELETE /content/items/{id}/favorite` and
  listed at `/content/favorites` as ids and kinds. **Marking is not reading**: the list never says what an item is
  called, so an item whose sharing has since narrowed is read, or refused, by its own route.
- **Held, not threaded.** The portal builds an item's document in five places from five models; `ItemAnnotations`
  reads every item's terms and categories at start, after every write here and once a minute, so another node's write
  reaches this one within that minute.
- **The recycle bin** is a decision of its own — [Q-180](../open-questions.md), built as [ADR-203](ADR-203-a-deleted-item-waits-in-the-recycle-bin.md).

## 3. Alternatives and counterarguments

### Alternative A — favourites as a hidden group per member, as ArcGIS's `favGroupId` is

**For.** A client searching `group:<favGroupId>` would find them. **Against.** A group here grants reading
(ADR-036), so an item a member lost access to would stay readable through their favourites. Condition 3.

### Alternative B — the two new fields threaded through each item model

**For.** No cache. **Against.** Five models and their stores for two fields most items do not have. The minute's
staleness across nodes is the cost, and this deployment has one.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Terms and categories reach the portal's item; the schema lists them; a favourite is kept and dropped | `AnItemHasTermsCategoriesAndFavoritesTests` | conformance fixture |

## 5. Consequences

**Positive.** An open-data portal can state its licence; content can be browsed by category.

**Negative.** No screen yet (D-305); no `categories` search filter.

**State.** `item.license_info`, `item_category`, `item_in_category` and `item_favorite` (migration 93), in the catalogue; `ItemAnnotations` holds every item's terms and categories at runtime, node-local, read again after each write on this node and once a minute.

## 6. Conditions

1. **A screen** for categories, terms and favourites in the redesigned Studio — D-305.
2. **A service's terms and its OGC access constraints become one fact**, or are decided to stay two.
3. **`favGroupId`** — Pro's and the Python API's favourites — answered without a group granting reading. *(Discharged 2026-10-18: a member's record names a `favGroupId` derived from their own id; `search?q=group:<it>` and `content/groups/<it>/search` answer their favourites among what the search already lists to them, so it grants nothing — `AnItemHasTermsCategoriesAndFavoritesTests`.)*
4. **[Q-180](../open-questions.md)** decided and the recycle bin built. *(Discharged 2026-10-18: decided by the owner and built, [ADR-203](ADR-203-a-deleted-item-waits-in-the-recycle-bin.md).)*

## 7. Dependencies

**Depends on:** ADR-056, ADR-036, ADR-040 §4a.

## 8. Revisit triggers

A client observed searching `categories:`; a second node deployed.

## 9. Dissent

None recorded.
