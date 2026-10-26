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
  11.4 and later recycle by default too, and take `permanentDelete=true`. Here a `permanentDelete` is recycled all the
  same, because the bin has no screen yet (the veteran's N7) and a script's mistake should be undoable. That is a
  difference, named in §5.
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
2. **`permanentDelete` is recycled** like any delete, until the bin has a screen.
3. **Pro's rollback**: a delete of a service Pro published in the last fifteen minutes is refused, and a delete of the
   service-definition item Pro uploaded forgets the upload, never the service.
4. **A service's title is its name**: an `update` with another title is refused rather than renaming the service.
5. **Description and summary are one text for a service** (as its item has always said): a changed `description` is
   written; a changed `snippet` only when the description was not changed.

## Conditions

1. **The recycle bin has a screen** (the veteran's N7), after which `permanentDelete` can mean what it says.
2. **Pro's Share pane un-sharing** is seen to call `unshare`/`unshareItems` and to read the answer — not yet driven.
3. **A file item** (a document a member uploaded) is updated and deleted through the same door; today it is
   `Item does not exist or is inaccessible.`, which is at least not a success.

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
