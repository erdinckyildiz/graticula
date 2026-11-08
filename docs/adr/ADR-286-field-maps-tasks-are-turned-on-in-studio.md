# ADR-286 — ArcGIS Field Maps tasks are turned on from a map's Settings in Studio

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-08, **by owner decision**, in three answers the same afternoon. Asked whether Field Maps tasks were supported, the owner said *"Yap evet"* ("do it, yes") to measuring them. Asked how a map should be given tasks once Designer was found not to work against this server, the owner chose *"Studio'da görev ekranı"* ("a task screen in Studio") from three options (§2). Asked whether `returnTrueCurves=true` should be accepted, the owner answered *"Returntruecurves artık desteklensin"* ("let returnTrueCurves be supported now"), which reverses one line of [ADR-215](ADR-215-what-arcgis-ignores-is-not-refused.md) §5. Where Studio's screen sits and what it writes are this ADR's. |
| **Supersedes** | — It reverses [ADR-215](ADR-215-what-arcgis-ignores-is-not-refused.md) §5's *`returnTrueCurves=true` stays refused*. |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ArcGIS Field Maps has *tasks*: a list on the phone of the work assigned to whoever is signed in, which they pick up,
start and finish. An office assigns them; the field does them. It is the Workforce product folded into Field Maps, and
an ArcGIS organization that dispatches field crews uses it.

Until today nothing here answered it. Field Maps asks every map it opens for the item resource
`tasks-configuration.json`, and this server answered *no such resource*, which Field Maps reads as *no tasks*
([ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md) condition 5 made that answer the one Enterprise gives). That was
correct and was all.

## 2. Alternatives considered

The owner was asked how a map should be given tasks, with the finding that Esri's own tool cannot do it here (§4).

### Alternative A — A task screen in Studio (chosen, recommended)

**Argument for.** It is the only way a Graticula organization gets tasks without a second product. Studio already owns
the layer's fields and lists (ADR-065) and the map's Settings; the screen adds the fields a layer lacks, gives them their
lists, and writes the configuration.

**Argument against.** It writes a file whose format Esri does not document (§4), so a change on Esri's side can break it
without notice, and it is this server, not the reader, that would have to follow.

### Alternative B — Serve a configuration, write none

**Argument for.** Nothing undocumented is written by this server; an administrator who wants tasks uploads the file with
the ArcGIS API for Python.

**Argument against.** Nobody would. The file names the layer's lists by their internal names, and a person writing it
by hand would be doing what Designer does, without Designer.

### Alternative C — Record tasks as a limit

**Argument for.** No undocumented format at all.

**Argument against.** It was measured working (§4), so calling it a limit would be a choice and not a fact.

## 3. Counterarguments to the preferred option

**The format is read, not published.** The strongest case against is §4's last row: this server writes a file whose
shape was learned by reading other organizations' public files. The answer, which the condition in §6 enforces, is that
the file is *authored here* from the structure and from Esri's documented defaults — the field names, the codes, the
three filters and five actions in *Prepare tasks* — and that no other organization's file is copied. It is the clean-room
line [ADR-030](ADR-030-reading-the-reference-implementation.md) draws for the reference checkout, applied to the
observed wire format of a public resource: derivation disclosed here, citation to the public documentation for anything
it defines.

**Editing is not ArcGIS's.** In ArcGIS a hosted layer shared with the organization and open to editing can be edited by
any member allowed to edit. Here ([ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md), the owner's own decision) a
layer is edited by its owner, administrators and a group the owner shares it with for editing. A field crew therefore
needs the task layer shared with a shared-update group, and the screen says so. That is the dispatcher-and-crew shape
tasks are for, so it is kept and not argued here.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The task fields and their lists | `esritask_type` (integer, coded), `esritask_assignee` (string 255, coded, code = the ArcGIS username), `esritask_status` (integer, coded 0 Unassigned, 1 Assigned, 2 In Progress, 3 Completed), plus priority (0–4), due date, description and notes (string 4000). Required: type, assignee, status, each with a list. | Esri, *Field Maps — Prepare tasks* and *Configure tasks* (public documentation, read 2026-11-08) |
| What Designer sends to make a task layer | Its `addToDefinition` captured on ArcGIS Enterprise 2025.2 (Field Maps Designer 25.2.3): the fields above, `typeIdField: esritask_status` with feature types per status, `hasAttachments: true`, domain names `ESRITASK_<FIELD>_DOMAIN_<guid>`. The QA portal's hosting server then failed every layer creation with its own internal error, so Designer never finished there. | Aecon QA portal (owner's permission, 2026-11-08), request read from the browser |
| Designer cannot configure a layer of this server | A map on the QA portal holding a Graticula layer: Designer greyed the layer out and logged *Unable to load serviceItemId* — it resolves the layer's item in its own portal. | Same session |
| The configuration's shape | Of 594 public web maps on ArcGIS Online with the `Collector` keyword, 9 held `tasks-configuration.json`. Read **for structure only**: `version`, `layerId` (the operational layer's id), `displayOptions` (filters, URL-encoded SQL with `CURRENT_USER`), `expressionInfos` (Arcade), `featureActions`, `featureActionLayouts`, `fieldSchema` (the three fields by their list names, status with a `keyMap`), `groupOptions`. The maps' items carry the type keyword `Tasks`. **No file was copied; the downloaded files were deleted.** | arcgis.com public items, 2026-11-08 (§3, the disclosure) |
| A file written here works | On dev, Field Maps 26.2.2 (Android emulator) signed in as an org member opened a map configured from Studio: *Assigned to me (1)*, the task's type by its list name, the Assigned layout's Start, then Finish — status 1 → 2 → 3 written to the layer. *Pick up* on an unassigned task wrote the member's username and status 1. The *Tasks* filter in Field Maps' map search found the maps carrying the keyword. | dev.graticula.com, 2026-11-08 |
| Why the task list read 0 at first | Field Maps sends `returnTrueCurves=true` on the task list's query and this server refused it (ADR-215). Accepted on dev, the list filled at once. No curve is served here: `WkbReader` refuses one on read (ADR-005 §3.3c). | dev, 2026-11-08 |
| Editing needs the ADR-075 group | Signed in as a publisher who did not own the layer, Field Maps said *You don't have editing permissions*; after the owner shared the layer with a shared-update group holding that member, Start wrote back. | dev, 2026-11-08 |

## 5. Decision

1. **`returnTrueCurves=true` is accepted** and ignored, with the reason that no curve is served (owner decision; ADR-215
   §5 amended).
2. **`GET/PUT/DELETE /content/webmaps/{id}/tasks`.** `PUT` names one feature layer of the map — a hosted point, line or
   polygon layer of this server, owned by the caller or by any owner for an administrator (ADR-075's door for adding a
   field) — the task types and the assignees. It adds the task fields the layer lacks (refusing a field of the wrong
   kind, a hidden one, or one holding subtypes), gives type, assignee, status and priority their lists as shared domains
   named `<field>_<layer id>` (ADR-087), changing a list the layer already has where it is kept, and writes
   `tasks-configuration.json` as the map's item resource. `DELETE` removes the configuration and nothing else.
3. **The configuration** is written by `FieldMapsTasks.Configuration`: the three documented default filters, one layout
   per status, the actions *Pick up*, *Start*, *Finish* (automatic) and *Status*, *Note* (prompted), and `fieldSchema`
   naming the layer's own lists.
4. **A web map carrying the configuration has the type keyword `Tasks`**, in its item, in My Content and in search —
   asked once per listing (`IItemResourceStore.ItemsHoldingAsync`).
5. **Studio: a map's Settings › Tasks for ArcGIS Field Maps** — the layer, task types one per line, the members to
   assign to (the organization's members, from the portal's user search), *Turn on tasks*, *Change types or people*,
   *Turn off tasks*, and a sentence that editors need the layer shared with an editing group, linked to the layer.
6. **A token sent as a part of a multipart form under 8 MB authenticates**, as ArcGIS reads one: the ArcGIS API for
   Python sends `addResources` that way, and it arrived anonymous.

## 6. Consequences

**Positive.** An organization can dispatch field work to Field Maps from this server alone, measured on the app.

**Negative.** This server writes an undocumented file, and a change to Field Maps' reading of it is found by a user,
not by a test (condition 2 narrows that). Designer's own task features — custom status values, custom actions,
Arcade-driven layouts, task views for restricted crews — are not offered; the screen writes the defaults. Assignees are
account names given by the caller and are not checked against the directory, as ArcGIS's list is not.

**State.** No new table. Per map, one item resource (`tasks-configuration.json`); per layer, up to seven columns and
four shared domains. Nothing node-local.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | **INFERRED:** Field Maps keeps reading `tasks-configuration.json` in the shape observed on 2026-11-08 (version `1.0.0`). Not Esri's commitment; measured once on Field Maps 26.2.2. | Unvalidated beyond one version |

## 8. Dependencies

**Depends on**: [ADR-065](ADR-065-domains-and-subtypes.md) and [ADR-087](ADR-087-domains-are-shared.md) (the lists),
[ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md) (who edits), [ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md)
condition 5 (Field Maps' sign-in and Collect), [ADR-215](ADR-215-what-arcgis-ignores-is-not-refused.md) (amended).

**Depended on by**: none yet.

## 9. Conditions

1. **Measured on a device again after any change to the writer** — a Field Maps release that stops showing a configured
   map's tasks is found by this, not by a user. Discharged for the first version by §4's device row.
2. **A Field Maps version other than 26.2.2 opens a Studio-configured map and shows its tasks**, within the next Field
   Maps upgrade on the emulator.
3. **The owner confirms the INFERRED parts**: where the screen sits (the map's Settings, not the layer's), the domain
   naming `<field>_<layer id>`, that turning off removes only the configuration, and the clean-room reading of §3.

## 10. Revisit triggers

- Esri documents the task configuration format, or publishes an API that writes it: then the writer follows the
  document and §3's disclosure is closed.
- Field Maps shows a Studio-configured map without its tasks (condition 1).
- A request for custom statuses, custom actions or task views.

## 11. Dissent

None recorded. The clean-room reading in §3 is the part a reviewer is most likely to dispute, and it is listed for the
owner in condition 3 rather than taken as settled.
