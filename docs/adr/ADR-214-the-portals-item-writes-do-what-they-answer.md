# ADR-214 — The portal's item writes do what they answer

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-22, overnight, under the owner's *"ben uyurken açık konuları kapatır mısın?"* — the open findings of the ArcGIS veteran's second review (2026-10-21). That the findings are to be closed is the owner's; **how each one is closed here is INFERRED** and listed in §5 for confirmation. |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS veteran's second review (2026-10-21), finding **N2**, and the second of its top five:

- `content/users/{u}/items/{id}/update` answered `{success: true}` for **any id that was not a web map** — a
  service, a document, an id that does not exist — before any ownership check, and stored nothing.
- For a web map, `text` was never read, so the JS SDK's `WebMap.save()` / `PortalItem.update({data})` and the Python
  API's `item.update(data=…)` reported success and **lost the map's changes**.
- `content/updateItems` said *"This portal keeps no categories"* although ADR-202 built them, so Pro's category
  assignment after an overwrite was refused.
- Item `delete`, `deleteItems`, `unshare` and `unshareItems` were absent: a 404 that named nothing.

A silent success is worse than a refusal: the script believes it worked. The review asked for errors first and the
operations second.

## 2. Alternatives considered

### A — Write what is kept, pass over what a client echoes, refuse the rest by name, all or nothing *(chosen)*

**Argument for.** Every answer is true. The JS SDK's `PortalItem.update` sends every property it read — `type`,
`typeKeywords`, `extent`, `url` and the rest — which this portal derives; refusing those would refuse every save, so
they are passed over as echoes. Anything else is refused with its name, and nothing is written, so a client never
holds half an update it was told was whole.

**Argument against.** The echo list is a judgement. A client that sends a *changed* `extent` or `typeKeywords` is
told success and the change is not kept — the old fault, narrowed to fields this portal computes from the data.

### B — Refuse every field that is not kept, echoes included

**Argument for.** No field is ever passed over.

**Argument against.** `WebMap.save()` would fail on every map: the SDK echoes about a dozen derived properties on
every update. The fault the review found — saves that do not save — would become saves that cannot save.

### C — Keep answering success and log what was dropped

**Argument against.** It is the fault.

## 3. Counterarguments to the preferred option

- *"Delete through the portal should be what ArcGIS's is: permanent unless the recycle bin is asked for."* Enterprise
  11.4 and later recycle by default too, and take `permanentDelete=true`. Here a `permanentDelete` was recycled all
  the same until the bin had a screen, because a script's mistake should be undoable and there was no other way back;
  since 2026-10-28 it is purged from the bin straight after, as Enterprise's is (condition 1).
- *"Refusing Pro's delete of a service it just published second-guesses the client."* It does, deliberately: Pro reads
  a publish answer it does not understand as a failure and deletes the service item it was given (dev, 2026-10-17,
  `StagedDefinition.WrittenJob`). Before this ADR that delete reached nothing; with a real delete behind the door it
  would put a just-published service in the bin. The window is fifteen minutes and the refusal says how to delete on
  purpose.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A web map's `text` is kept by `update`, with derived fields echoed | Live: `update` with `text`, `title`, `type`, `typeKeywords`, `extent` → `success`; the document read back holds the saved layer | fixture, 2026-10-22; `PortalItemWritesTests` |
| An unkept field refuses the whole update | Live: `title` + `foo` → error naming `'foo'`; title unchanged | same |
| An id naming nothing is an error | Live: `Item does not exist or is inaccessible.` | same |
| A service's description, tags and credits are written | Live: read back from the item | same |
| A service is not renamed by `update`; `text` on a service is refused | Live | same |
| `updateItems` sets categories and refuses an unknown path per item | Live: `/Categories/…` read back on the item | same |
| `deleteItems` recycles a map and a service, and answers a missing id per item | Live: both in `/admin/recycle-bin`; second id `success:false` | same |
| `unshare` from a group the item is not in names the group | Live: `notUnsharedFrom` | same |
| Pro's post-publish `update` (its `typeKeywords`) still succeeds | `typeKeywords` is an echo; `ShareAsWebMapTests` still pass | conformance, 32 of 32 |

## 5. Decision

`update`, `updateItems`, `delete`, `deleteItems`, `unshare` and `unshareItems` are answered for a service (by any of
its faces' item ids) and a web map, by their owner or whoever may manage all content (`PortalEndpoints.ItemWrites`).
An update writes the fields this portal keeps for that kind of item, passes over the fields a client echoes and this
portal derives, and otherwise refuses — naming the fields — and writes nothing. Delete is the console's own delete,
called rather than rewritten, so delete protection, views, what is kept beside a registered layer and the recycle bin
hold through this door too. **INFERRED, for the owner to confirm:** *(Confirmed by the owner 2026-10-26 — Q-189: "189'un hepsini onayladım.")*

1. **The echo list** (`EchoedFields`) — what a client may send back without being refused.
2. **`permanentDelete` is recycled** like any delete, until the bin has a screen. *(It has one since 2026-10-28 — Studio › My content › Recycle bin — and a `permanentDelete=true` is now
   recycled and then purged through the bin's own purge, audited as `recyclebin.purge`; condition 1.)*
3. **Pro's rollback**: a delete of a service Pro published in the last fifteen minutes is refused, and a delete of the
   service-definition item Pro uploaded forgets the upload, never the service.
4. **A service's title is its name**: an `update` with another title is refused rather than renaming the service.
5. **Description and summary are one text for a service** (as its item has always said): a changed `description` is
   written; a changed `snippet` only when the description was not changed.

### 5a. Four more doors, 2026-11-02

Added overnight under the owner's *"her işi tamamla"*, from the ArcGIS veteran's review of 2026-11-02: four things
Studio already does that the ArcGIS API for Python could not ask for, each a 404 until now
(`PortalEndpoints.ItemControls`). Each door finds the item as the writes above find it — a service by any of its faces'
ids, a web map, a document — refuses who may not, and then **runs Studio's own handler inside the request**, so the
privilege check, the store call and the audit line are Studio's; only the refusal is reworded into the portal's
envelope. Nothing is written a second way.

1. **`items/{id}/protect` and `unprotect`** (`item.protect(True/False)`), with or without a folder in the path: the
   delete protection Studio's Settings › General sets (ADR-102 condition 2), by the item's owner or whoever may manage
   all content. Answers `{"success":true}`; the item's `protected` then reads accordingly.
2. **`items/{id}/reassign`** (`item.reassign_to`), with `targetUsername` and an optional `targetFoldername`: Studio's
   Change owner, which is an administrator's act (`admin:manageAllContent`) there and so here. The receiver is named
   bare and found inside the request's organisation (`graticula_principal_named`, ADR-204 §5e item 16); a name with
   `\` is refused before anything is looked up, as the owner's path refuses one. A folder the receiver does not have
   refuses the whole request before the owner changes. Answers `{"success":true,"itemId":…}`.
3. **`{folderId}/updateFolder`** (`Folder.rename`), with `title`: Studio's folder rename, under `createFolder`'s rules —
   1 to 128 characters, and a title the owner already has is `409 Folder '…' already exists.`
4. **`content/items/{id}/groups`** (`item.shared_with`): the groups the item is shared with that the caller may see,
   as `{"admin":[],"member":[],"other":[]}` — groups the caller owns or manages, those they are a member of, and the
   rest — each published as every portal group is (`PortalGroup`). Its owner's question or an administrator's;
   anybody else is told *Item does not exist or is inaccessible.*

**INFERRED, for the owner to confirm:** the four answers' shapes and how each maps onto ours — in particular that
reassignment stays inside the organisation, that a missing target folder refuses rather than filing the item at the
receiver's root, that `groups` sorts by the caller's standing in each group, and that an image service (which the
item writes above do not reach either) is not among the items these doors know. Measured: unit tests
`ThePortalsItemControlsAreStudiosTests`, and `PortalItemControlsTests` in the conformance suite, which CI runs.

### 5b. Range searches, 2026-11-03

From the ArcGIS veteran's review of v1.0.499: `q=modified:[1700000000000 TO 1900000000000]` and
`q=created:[0 TO 9999999999999]` answered `total: 0` on the showcase while `q=*` found twenty-four items. The parser
(`PortalQuery`) did not know a range, read `[1700000000000` and `1900000000000]` as words, and answered nothing — the
rule that keeps Pro from mistaking a layer for Esri's geocoder, applied to a question it had no reason to refuse.

**What ArcGIS documents** (developers.arcgis.com/rest/users-groups-and-items/search-reference): a range is
`field:[low TO high]`, square brackets inclusive and braces `{low TO high}` exclusive; `created` and `modified` take
epoch milliseconds, and the reference's own examples write them zero-padded to nineteen digits.

**What is built.** A range is one token after `field:`, read in either bracket on either side, with `*` for an open
side; the values are epoch milliseconds, zero-padded or not. It is answered on the item's own `created`, `modified`,
`numViews` and `size` — the numbers an item this portal writes carries, read from the same object the search returns
— and an item without the field is outside every range. **A range on any other field, or one that does not parse, is
refused with a sentence** (`PortalQuery.Refusal`), at `search`, a group's `search` and `community/groups`, rather than
answered with nothing: a script asking what changed this week cannot tell *nothing* from *not understood*. An unknown
field that is not a range still answers nothing, as before. Measured: `PortalQueryTests` (ranges, bounds, refusals)
and `PortalItemWritesTests.A_date_range_search_finds_what_q_star_finds_and_a_range_without_a_field_is_refused`, both
failing against v1.0.499; and the ArcGIS API for Python's `gis.content.search("modified:[0 TO 9999999999999]")` found
what its search for `*` found (`tools/sdk-probe/python-portal-doors.py`).

**INFERRED, for the owner to confirm:** that `numViews` and `size` take ranges (the reference lists ranges for dates;
this portal holds both numbers on every item, a service's `size` as `-1`), and that refusing an unreadable range is
better than ArcGIS's own behaviour, which is not documented for that case. **Left out:** ranges on text fields
(`title:[a TO m]`), which the reference does not describe for items and nothing here would answer truthfully.

### 5c. The recycle bin through the portal, 2026-11-03

Same review. Condition 1 made `permanentDelete=true` purge, but `portals/self` never said there was a bin, so the
ArcGIS API for Python (`gis/__init__.py`, `Item.delete` and `ContentManager.delete_items`) **dropped the parameter,
warned *Recycle bin not enabled on this organization*, and deleted into the bin** — the script believed the item gone
for good. Its `user.recyclebin` had nothing to list, and `RecycleItem.restore()` met a 404.

**What ArcGIS documents:** the organisation's `recycleBinSupported` and `recycleBinEnabled` on `portals/self`;
`permanentDelete` on `delete` and `deleteItems`; `content/users/{u}/items/{id}/restore` with an optional `folder`; and a
member's bin listed by `content/users/{u}` with `inRecycleBin=true` (which the Python API's `RecycleBin.content` sends,
with `foldersContent`, `start`, `num`, `sortField` and `sortOrder`).

**What is built** (`PortalEndpoints.RecycleBin`), each a door onto ADR-203's bin and Studio's handlers:

1. `portals/self` says `recycleBinSupported: true` and `recycleBinEnabled: true` — the bin is always on.
2. `content/users/{u}?inRecycleBin=true` lists the caller's own entries — those they own, or deleted when nobody owned
   them — as items with `id`, `owner`, `title`, `type`, `modified` (when deleted), `deleted` and `expires`, paged and
   sorted as every listing is.
3. `items/{id}/restore` (with or without a folder in the path) runs Studio's Restore (`RestoreFromRecycleBinAsync`),
   so its privilege, its refusal and its `recyclebin.restore` audit line are Studio's; it answers
   `{"success":true,"itemId":…}`. A service is found by its own id or any of its faces' item ids.
4. A `delete` with `permanentDelete=true` of an id **already in the bin** — the Python API's `RecycleItem.delete()` —
   runs Studio's Delete for good (`PurgeFromRecycleBinAsync`, audited `recyclebin.purge`). An id in neither the catalogue
   nor the bin is still *Item does not exist or is inaccessible.*

**INFERRED, for the owner to confirm:** an entry's portal `type` is read from its kind, because the bin keeps what was
deleted and not the item document — a service is a `Feature Service`, a web map a `Web Map`, an image service an
`Image Service`, a document `Document` and a toolbox `Toolbox` (the last two are not ArcGIS type names); and a
`restore` that names a `folder` is refused rather than honoured, because Studio's restore puts an item back in the
folder it was deleted from. **Left out:** the organisation's bin as one listing for administrators (the Python API offers it
as `gis.admin.org_recyclebin` on its ArcGIS Online administrator only, `gis/admin/agoladmin.py`); Studio's own screen
remains the administrator's view of every entry. Measured: `PortalItemWritesTests.The_recycle_bin_is_listed_restored_from_and_emptied_through_the_portal`,
and the ArcGIS API for Python 2.4.3 against the local fixture
(`tools/sdk-probe/python-portal-doors.py`): `item.delete()` put a web map in `user.recyclebin.content`, its
`RecycleItem.restore()` brought back the same id, and `item.delete(permanent=True)` sent `permanentDelete`, without the
warning, and left nothing in the bin.

## Conditions

1. **The recycle bin has a screen** (the veteran's N7), after which `permanentDelete` can mean what it says.
   **DISCHARGED 2026-10-28.** Studio › My content › *Recycle bin* lists what the reader may handle, with search, a
   page of ten, Restore and Delete for good behind a confirmation (`RecycleBinScreenTests`); and `delete` and
   `deleteItems` with `permanentDelete=true` recycle the item and then purge its entry through the bin's own purge,
   so the tables, files and audit line are the screen's. Measured: `PortalItemWritesTests.A_permanent_delete_through_the_portal_leaves_nothing_in_the_recycle_bin`
   failed against the build before it, with both maps still in the bin, and passes now.
2. **Pro's Share pane un-sharing** is seen to call `unshare`/`unshareItems` and to read the answer — not yet driven.
   *(2026-10-31: still not driven, and it cannot be from arcpy: un-sharing is the Share pane's, Pro's active portal
   must be this server for the pane to act on its items, and this session may neither change Pro's active portal nor
   drive Pro's windows. **Owed, by a person:** in Pro signed in to `https://dev.graticula.com/studio` with
   `Graticula:ProPublishCapture` on, Catalog › Portal › My Content › a shared item › Share › clear *Everyone* and a
   group › Save; dev's `wire.log` then shows which of `unshare`/`unshareItems` Pro called and what it was answered.)* *(2026-11-01, at the showcase's second organisation `kontrol`, Pro's active portal set there by the owner: **Pro's sharing engine un-shares through `shareItems`, not `unshareItems`.** An overwrite through `UploadServiceDefinition` shared to everyone and the organisation, then a second with `PRIVATE` and `NO_SHARE_ORGANIZATION`, made each of the item's two faces public and then private again, as the portal's own search answered; the request log shows `publish`, `updateItems` and one `shareItems` per overwrite, each `200`, and no `unshare`. The group half — share to a group, then not — met a missing door first: the portal had no `community/createGroup` (404). It was written that night and reverted the same night: ADR-040 §4a makes portal groups read-only on purpose, so it waits on the owner (Q-224). The Share pane itself is still Pro's window.)* *(2026-11-02: Q-224 answered — portal group writes are open through Studio's own acts, [ADR-256](ADR-256-portal-group-writes-go-through-studios-privilege.md); the group half of this check can now be run.)*
3. **A file item** (a document a member uploaded) is updated and deleted through the same door; today it is
   `Item does not exist or is inaccessible.`, which is at least not a success. **DISCHARGED 2026-10-27.** The portal's
   item writes know a document: `update` takes its title, summary, description, tags, terms, categories and its file —
   recognised and bounded by ADR-188's own rules, all checked before any is written — and refuses by name what a
   document does not keep; `delete` goes through Studio's own delete, so protection holds and it waits in the recycle
   bin; `unshare` takes it out of a group. Measured: `ADocumentIsAnItemTests.A_document_is_updated_and_deleted_through_the_portal`
   answered *Item does not exist or is inaccessible* against the build before it, and passes now.

## 6. Consequences

**Positive.** No item write answers success without having happened. `WebMap.save()` and `item.update(data=…)` save.
Python's `item.delete()`, `delete_items`, `unshare_items` work. Pro's category assignment after an overwrite is kept.

**Negative.** A client that sends a field this portal does not keep is refused outright, where ArcGIS would store it;
a script updating `accessInformation` on a web map, or `text` on a service, now fails where it used to "succeed".
That is the point, and it will surface as support questions.

**State.** Nothing new: every write lands in what the console's own writes already keep — the catalogue's service and
web map rows, the item annotations (ADR-202) and the recycle bin (ADR-203). Pro's rollback window reads the
node-local staging list that publishing already held (`Staged`), so on a second node a Pro rollback could reach a
service the other node staged; nothing here has run on two nodes, as ADR-191 already says of that list.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The JS SDK's echo set is the one `EchoedFields` lists. Measured from the SDK's `PortalItem` properties, not from a capture. | INFERRED |

## 8. Dependencies

**Depends on:** ADR-202 (categories and terms), ADR-203 (recycle bin), ADR-102 condition 2 (delete protection),
ADR-113 (views), ADR-191 (Pro publishing).

**Depended on by:** —

## 9. Revisit triggers

- A real client's update is refused for a field it merely echoed: add it to `EchoedFields`, with where it came from.
- The recycle bin gets a screen: condition 1.

## 10. Dissent

The veteran's own ordering put *"return an error for everything not done"* first and the operations second; both
were done in one step here.
