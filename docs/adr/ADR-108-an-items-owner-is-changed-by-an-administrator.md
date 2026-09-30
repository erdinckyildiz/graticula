# ADR-108 — An item's owner is changed by an administrator; *Move* waits for content folders

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for Change owner — one row, the member transfer's rule for one item; `MEDIUM` for deferring Move, which rests on a reading of what Move means (§3) |
| **Decided** | 2026-10-01, by owner decision (*"2 devam sırayla"*, the sixth item of the ArcGIS reviewer's list) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS reviewer: *"No per-item Change owner or Move. Transfer exists only when a member is removed."* A member's
whole content could be handed over (`POST /admin/members/{name}/transfer`); one item could not, so giving a colleague
one service meant giving them everything or recreating the service.

## 2. Alternatives considered

### Alternative A — Change owner per item, administrator only; Move deferred (chosen)

**Argument for.** Change owner is one row: the service's owner is its layers' owner (migration 11, D-24), and a web
map has one owner column. It is Portal's rule that an administrator does it. Nothing is unpublished; sharing, groups
and every address a client holds stay.

### Alternative B — Move as a change of the service's folder

**Argument against.** Portal's *Move* moves an item between a user's **content folders**, and the service's URL does
not change. Here a folder is part of the service's address (`/rest/services/{folder}/{name}`): moving it breaks every
client and every web map that points at it, and hosted services must stay in `hosted` (v1 scope, *Folders*). The
thing Portal moves does not exist here yet — content folders separate from service folders are ADR-056's item table.

## 3. Counterarguments to the preferred option

- *A Portal user expects Move.* They do; the item page says nothing about it rather than offering a Move that changes
  a URL. Whether content folders are wanted is the owner's question (§10.3).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A layer's owner is its service's | `PostgresMemberDirectory.MoveAsync`'s D-24 note | src/Graticula.Platform.Postgres |
| Portal's Move keeps the service URL | Portal documentation, *Manage your content* | publicly documented |

## 5. Decision

5.1 `PUT /admin/services/{name}/owner?folder=…` and `PUT /content/webmaps/{id}/owner`, body `{"to": member}`,
`admin:manageAllContent`. 404 for no such item or member; the audit log records `service.owner` / `webmap.owner`.

5.2 In Studio, an administrator sees *Change owner…* beside the Owner fact on a service's and a web map's Overview,
choosing from the members.

5.3 Move is not offered (§2 B).

## 6. Consequences

**Positive.** One item changes hands without the rest of a member's content.

**Negative.** No Move.

**State.** One owner column per item; nothing new.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Portal's Move is between content folders and keeps the URL | publicly documented |

## 8. Dependencies

**Depends on:** ADR-075 (who manages an item), ADR-079 (web maps), ADR-056 (the item table that content folders need).

**Depended on by:** —

## 9. Revisit triggers

- Content folders are decided (ADR-056) — Move then has somewhere to move to.

## 10. Conditions

1. **Both routes are tested against the running server**, the new owner read back. **DISCHARGED 2026-10-01** —
   `ChangeOwnerConformanceTests`.
2. **Studio's Change owner is tested.** **DISCHARGED 2026-10-01** —
   `ItemStructureTests.An_administrator_changes_an_items_owner_from_Overview`.
3. **Whether Portal's Move — content folders apart from service folders — is wanted is put to the owner.**
