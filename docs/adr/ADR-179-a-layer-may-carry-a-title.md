# ADR-179 — A layer may carry a title

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` — one column, one setter, and the three faces that composed a title each read it first; a conformance test sets it once and reads it back from all three |
| **Decided** | 2026-10-07 by owner decision — asked whether to publish OGC's WMS 1.1 standard dataset, which needs a layer title the server could not set, the owner answered *"Yap"* |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | — |

---

## 1. Context

A layer's name is its identifier: it is in every face's address, it must be unique where it is used, and it is
written by somebody who was naming a table. WMS, WFS and OGC API Features each composed a title for a person to read
from the service's and the layer's names (`hosted/parcels — parcels`, `hosted / parcels`). Nothing let a publisher say
what the layer is.

[D-290](../architecture-debt.md) recorded ten WMS 1.1 CITE failures that measured the fixture rather than the
server: OGC's suite finds its standard dataset by layer **title** — `cite:Lakes`, `cite:Forests` and nine more — and a
layer here could not be given one.

## 2. Alternatives considered

### Alternative A — A nullable `title` on the layer, read first by the OGC faces (chosen)

**For.** One place to set it; every face that shows a title shows the same one; null keeps the composed title every
layer had, so nothing changes until somebody sets one.

**Against.** A sixth thing on the layer page, and the ArcGIS surface does not show it — ArcGIS's layer has a name
and no separate title.

### Alternative B — Let a layer's name carry any text

**Against.** A name is an address. `cite:Lakes` with a colon, or a title with spaces, would have to be escaped in
every face and would collide with WFS's `prefix:name`.

## 3. Counterarguments to the preferred option

*A field added for a test suite.* The suite is why it was noticed, not why it is worth having: the composed titles
were what a QGIS user read in the layer list, and they named tables.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Every OGC face shows it, and clearing it restores the composed one | `ALayerTitleIsWhatTheOgcFacesShowTests` | this repository |
| OGC's dataset can now be published as the suite expects | eleven layers titled `cite:<Name>` listed in WMS 1.1.1 capabilities, from OGC's own `WmsTestData.zip` | `tools/cite-data/wms11.json`, `seed_cite_wms11` |
| And the suite then passes | OGC's WMS 1.1 suite: 62 passed / 10 failed before, **129 passed / 0 failed** with the dataset (run 37593462477) — the ten were the dataset's absence, and 57 checks it had skipped ran | `tools/cite-baselines.json` |
| The migration is an Expand | run on a copy of the fixture's store first: 10 layers, no titles, the check in place; minimum reader unchanged at 55 | by hand, 2026-10-07 |

## 5. Decision

Migration 83 adds `layer.title` — text, null or 1 to 256 characters after trimming. `PUT /admin/layers/{name}/title`
sets it (`content:publishFeatures`, as the visible range); blank or null clears it. WMS (`<Title>`), WFS
(`<Title>` in the feature type list) and OGC API Features (the collection's `title`) show it when it is set, and their
composed title otherwise; OGC API Maps shows WMS's. Nothing addresses a layer by its title.

The CITE workflow publishes OGC's WMS 1.1.1 dataset — eleven layers, in EPSG:4326 as OGC publishes it — titled
`cite:<Name>` before the WMS 1.1 suite runs, and removes it afterwards, so every other suite is measured on the
fixture its baseline was.

## 6. Consequences

**Positive.** A publisher says what a layer is, once, and every OGC client's layer list shows it.

**Negative.** The ArcGIS surface does not show it. There is no Studio control for it yet — condition 1.

**State.** One nullable column on `layer`. Nothing at runtime beyond the catalogue's existing cache.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | 256 characters is enough for a title | Unmeasured: no title has been set outside the tests yet |

## 8. Dependencies

**Depends on:** ADR-039 (WFS), ADR-041 (WMS), ADR-042 (OGC API Features).

**Depended on by:** —

## Conditions

1. Studio's layer settings set and clear the title, through the design review every screen gets.

## 9. Revisit triggers

- A request to show the title on the ArcGIS surface (as the layer's `name`), which ArcGIS clients would then use as
  the identifier too.

## 10. Dissent

None recorded.
