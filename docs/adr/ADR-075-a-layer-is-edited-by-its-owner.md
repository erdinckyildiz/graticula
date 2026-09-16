# ADR-075 — A layer is edited by its owner, an administrator, and the group its owner shares it with

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-16, by owner decision. Asked what an ArcGIS layer's *anonymous editing* and *editors see only their own* settings should become here (V-31), the owner answered: *"herkese açık bile olsa katman, bir sahibi olmalı. Düzenleme hakkı ona ve admin olan kullanıcılara ait. Adminler her şeyi düzenleyebilir."* — even a public layer has an owner; editing belongs to the owner and to administrators; administrators may edit everything. **Two readings were put back to the owner rather than inferred**, because both reversed an earlier owner decision: the owner **kept** shared update (a group the owner shares a layer with for editing still edits — ADR-036 §4a, 2026-08-25), and chose **administrators alone** for layers that predate ownership. **The rest is this ADR's**: that changing what a layer *is* stays with the owner and administrators and does not pass through a group, that the rule applies to every administrative endpoint that changes an item, and that sharing an item into a group requires owning the item |
| **Supersedes** | ADR-064's *`features:edit` reaches the caller's own features* (§2), and the privilege-only reading of editing in ADR-018 §3b |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Writing to a layer was a **privilege**. `features:edit` added features to every layer the caller
could read; `features:fullEdit` changed and deleted anybody's features in it; and since ADR-064 a
layer that records its creators let `features:edit` change the caller's own. None of it asked
**whose layer it was**. So a public layer was writable by every member whose role edits, and the
person answerable for the layer had no say over who wrote to it. A shared-update group
(ADR-036 §4a) was the only per-item grant, and it widened a privilege rather than replacing one.

Applying the owner's rule meant finding every door that writes, and that sweep found more than
editing. **Every administrative endpoint that changes an item asked for a privilege and never for
the item's owner — most of them not even whether the caller could read it:**

| Endpoint | Asked for | What any holder could do to somebody else's layer |
|---|---|---|
| `PUT /admin/layers/{name}/sharing`, `PUT /admin/services/{name}/sharing` | `sharing:shareToOrganization` or `…ToPublic` | **Open a private layer to the organisation (any *user*) or to the internet (any *publisher*)** |
| `POST /admin/hosted/{layer}/truncate`, and ArcGIS's `/rest/admin/…/truncate` | `content:publishFeatures` | **Empty it — irreversibly** |
| `POST`/`DELETE /admin/hosted/{layer}/fields…`, `/rest/admin/…/addToDefinition`, `deleteFromDefinition` | `content:publishFeatures` | Add or drop a column |
| `/admin/hosted/{layer}/editor-tracking`, `/global-ids` | `content:publishFeatures` | Change its schema |
| `PUT /admin/layers/{name}/fields`, `/symbology`, `/time-field`, `/visible-range`, `/cache`; `/admin/services/{name}/style`, `/groups`; thumbnail redraw | `content:publishFeatures` or `content:publishTiles` | Hide its columns, change its domains, restyle it |
| `GET /admin/layers/{name}/classify`, `POST …/symbology/preview`, `GET …/fields`, `GET …/symbology`, `GET /admin/services/{name}/style`, `POST …/visible-range/suggestion` | `content:publishFeatures` | **Read a private layer's value distribution, extent and field list without being able to open the layer** |
| `PUT /admin/groups/{group}/items/{service}` | standing in the group | **Put somebody else's service into one's own shared-update group — and so read and edit it** |

The last row is the one that would have undone the rule: running a group is not owning what goes
into it.

## 2. Alternatives considered

### Alternative A — the owner's rule, with the owner's delegation, applied at every door *(chosen)*

**Argument for.** It is what the owner said, with the one delegation the owner chose to keep. One
rule (`LayerAccess.MayEdit`) for writing features and one (`LayerAccess.MayManage`) for changing the
item, each called from every door, so no face can admit what another refuses.

**Argument against.** It narrows what existing roles mean. A *data editor* — whose description was
*"edit features in layers shared with them"* — now edits only through a shared-update group, and a
*publisher* can no longer touch a layer it did not publish. That is the point of the decision, and
it will surprise whoever relied on the old reading.

### Alternative B — keep privileges, add ownership as an optional per-layer setting

ArcGIS's own shape: an owner opts a layer into *editors may only edit their own*. **Rejected by the
owner's answer**, which makes ownership the default rather than a setting.

### Alternative C — ownership for editing features only, leaving the administrative endpoints

The narrow repair. **Rejected because it leaves the larger holes**: a rule that stops a publisher
adding a point to your layer while letting them truncate it, or make it public, is not the rule the
owner stated.

## 3. Decision — who writes features

`LayerAccess.MayEdit(owner, scope, groups, caller, authorization)` answers, in order:

1. **Anonymous: never.** Stated because a public layer is where somebody will expect otherwise.
2. **`admin:manageAllContent`: every layer**, with an owner or without.
3. **The owner, if their role still holds `features:edit`.** Ownership decides *which* layers; the
   role decides *whether this account edits at all*, so a publisher reduced to viewer stops writing.
4. **A shared-update group the layer is shared with** — ADR-036 §4a, kept by owner decision.
5. Nobody else. A layer with no owner is edited by administrators alone.

**Every writing face asks it once**: ArcGIS `applyEdits` at layer and service level, `addFeatures`,
`updateFeatures`, `deleteFeatures`, the three OGC API Features writes, and attachments — which
asked for the privilege alone and not for the layer. Adding and changing have one answer now,
because the question is no longer which privilege.

**The layer document follows the same call.** Its capability string offers `Create`, `Update` and
`Delete` exactly to the callers `MayEdit` admits — which also repairs a disagreement in the other
direction: a shared-update group member was admitted by the endpoint and offered only `Query` by
the document.

**`ownershipBasedAccessControlForFeatures` is no longer emitted.** It said *others may not update or
delete* on every tracked layer, which was ADR-064's rule. Every ground above reaches every feature,
so the object would tell a group's members they cannot change what the server lets them change.

## 4. Decision — who changes what an item is

`LayerAccess.MayManage(owner, caller, authorization)`: **the owner, or `admin:manageAllContent`** —
and **not** a shared-update group. A group the owner shares a layer with for editing writes its
features; changing its sharing, fields, symbology, style, definition, or emptying it, stays with the
person answerable for it. ArcGIS draws the same line.

Every endpoint in §1's table that changes an item now asks it after its own privilege check
(`AdminEndpoints.ManagesAsync`), answering **404 to a caller who cannot read the item and 403 to
one who can**, so an administrative endpoint is not a way to learn a private item exists. The
reading endpoints ask `LayerAccess.Evaluate` — sharing governs reading, on this surface too.

**Sharing an item into a group requires owning the item** (`GroupChange.ItemNotYours`). Taking one
out does not: that narrows access, and a group's owner may keep their group tidy.

## 5. What is kept dormant, and why

ADR-064's per-feature predicate — `EditBatch.OwnOnly`, and the writer's creator comparison — is
**unreachable from any endpoint** and is not deleted. It is the mechanism ArcGIS's *editors may only
update and delete their own features* layer setting would need, applied to a shared-update group,
and it is tested at the writer. Recorded as [D-270](../architecture-debt.md) so it does not become a
permanent decision by being forgotten.

## 6. Conditions

1. **The conformance run proves the rule end to end**: an account whose role edits and which does
   not own a public layer is offered `Query` and refused every add, update and delete on both
   faces; the owner shares the layer with a shared-update group containing that account, which then
   changes every feature — the owner's and one with no creator — with the creator still recorded by
   the server; and the group member cannot truncate it.
   *(Open until `A_layer_is_written_to_by_its_owner_and_the_group_it_is_shared_with_for_editing` has
   passed in CI.)*
2. **The rule is pinned without a database**: `ALayerIsEditedByItsOwnerTests` covers each ground,
   each refusal, anonymous, an owner whose role lost editing, and `MayManage` against a group.
   **DISCHARGED** 2026-09-16 — 11 cases.
3. **Studio stops offering a publisher the pages of a layer it does not own.** The console decides
   which pages to show by surface and privilege, so a publisher who can read somebody else's layer
   may be shown Symbology and Fields pages that now answer 403. The refusal is clear; the offer is
   still an over-claim. *(Open — [D-271](../architecture-debt.md). Not measured on a running console
   in this change.)*

## 7. Consequences

- The person who publishes a layer decides who writes to it.
- A *data editor* edits through groups; *publisher* no longer means *may change any layer*.
- Five kinds of cross-owner damage are closed: making a private layer public, emptying it, dropping
  its columns, restyling it, and editing it through a group one runs.
- A private layer's value distribution is no longer readable through the symbology endpoints by a
  publisher who cannot open the layer.

**State.** None new. Owners and group shares are already in the catalogue; both rules are computed
per request from them and from the caller's resolved authorization.
