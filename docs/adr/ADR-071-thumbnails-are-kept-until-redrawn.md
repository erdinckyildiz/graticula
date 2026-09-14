# ADR-071 — A layer's thumbnail is drawn once and kept until somebody redraws it

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-14, by owner decision, looking at the showcase's content list: *"her seferinde thumbnail oluşturmak maliyetli. tek sefer oluşturup, gerekirse içeriye bir düğme koymak mantıklı. thumbnail yeniden oluştur gibi"* — drawing a thumbnail every time is costly; draw it once, and put a button inside to redraw it. Amended the same day, after the first release that kept pictures still made the list fill in one picture at a time: *"çok sürüyor. böyle olmamalı. hep cachete dursun bir tane. tekrar güncelleme istersek yenile butonuna basalım. ya da semboloji save ettiğimiz anda otomatik alsın."* — always in the cache, redrawn by the button or when the symbology is saved. **That pictures are kept and redrawn on request is the owner's. Keeping them on the node's disk, keying them by layer id, and also redrawing when the symbology changes are `INFERRED`** |
| **Supersedes** | — (reverses the in-memory, five-minute design `ServiceThumbnails` recorded under [D-58](../architecture-debt.md)) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`ServiceThumbnails` held each picture in memory for five minutes, and its own remarks rejected disk:
*"what concrete problem does this solve — has no answer for that yet. A restart costs one render per
service the next time somebody opens the list."* The render was measured at 70–76 ms on small layers.

Two things changed the answer. A thumbnail reads up to the deployment's record ceiling of features
twice — once to frame, once to draw — so a city's buildings cost seconds, not 70 ms. And the showcase
restarts on every release, which is every push, so every list was drawn again after each one and again
every five minutes after that.

## 2. Alternatives considered

- **A — keep on the node's disk, redraw on request** *(chosen)*.
- **B — keep in the catalogue.** Shared across nodes, but a binary column, a migration and a write on
  every first view, for pictures any node can draw again from the same data.
- **C — a longer memory lifetime.** Still lost on every release, and still stale for as long as it lasts.

## 3. Counterarguments

- **A kept picture goes stale when the data changes.** Edits, an appended file, a replaced table: none
  of them redraws it. The owner's answer is the button; the symbology write, the one change whose whole
  purpose is how the layer looks, also forgets it.
- **Node-local.** On two nodes a redraw pressed on one leaves the other's picture until its own redraw.

## 4. Evidence

| Claim | Evidence |
|---|---|
| The render reads the record ceiling twice | `ThumbnailEndpoints.PictureAsync`: `DrawnExtentAsync` reads up to `MaximumRecordCount` features to frame, then `DrawLayerAsync` reads them again to draw |
| Kept pictures survive a restart and never expire; a redraw forgets every size of one layer and no other; a torn write is drawn again; several callers at once draw once, and one leaving does not stop it | `ThumbnailsAreKeptUntilRedrawnTests` |
| Keeping alone still made viewers wait | Showcase 1.0.56, the owner's screenshot minutes after the upgrade: the content list's pictures filling in one at a time, each city layer about four seconds, because every picture's first viewer drew it |
| What it saves, end to end | `thumb-e2e.py`, fixture, linux-arm64, New York's 1.66 million buildings from GeoParquet: first picture **3,900 ms**, again 61 ms, **after a restart 56 ms**; redraw removed the file, and the next picture took 4,088 ms and wrote it back |

## 5. Decision

- A picture is kept under `<StatePath>/thumbnails/<layer id>-<width>x<height>.png`, written beside and
  moved over, with the same 256-picture memory store in front. No expiry.
- Keyed by the **layer id**: a republished layer is a new id and a new picture; a renamed service keeps its.
- `POST /admin/layers/{name}/thumbnail/redraw` forgets the layer's pictures; the next request draws it.
  The layer page shows the kept picture beside **Redraw thumbnail**, and pressing it asks for the picture
  again at once, so the new one is seen where the button is — the UX review's first finding against a
  version that only promised, in a toast, a picture drawn somewhere else.
- Setting or clearing a layer's symbology draws the pictures of every layer of that name again.
- **Nobody is the first viewer** (the amendment). `ThumbnailWarmer` draws, one at a time in the background:
  every layer without a kept picture 15 seconds after the server starts, each layer as it is published,
  and each picture a redraw or a symbology change forgot. A request that arrives while its picture is being
  drawn waits for that draw rather than starting a second, and a caller that leaves does not cancel it —
  the draw has its own two-minute bound.
- The response is `private, no-cache` with the byte-derived ETag, so a browser revalidates with a 304
  instead of holding an old picture for five minutes after a redraw.
- A disk that refuses a write is not a failure: the picture is answered from memory, as before.

## 6. Consequences

- **State.** Files under the node's state directory, one per layer and size, a few kilobytes each;
  node-local. Nothing in the catalogue.
- A deleted layer's file stays behind, unreachable, rather than hooking every path that removes a layer.
- Every picture drawn by a build before this is drawn once more after upgrading, in the background, then kept.
- The conformance test of a picture's framing presses the redraw before it measures: the server draws the
  edited layer's picture before the write suites have edited it.

## 7. Assumptions this decision rests on

| Assumption | If wrong |
|---|---|
| An administrator notices a stale picture and presses the button | Redraw on data-version change, which GeoParquet and DuckDB sources already expose |

## 8. Dependencies

D-58 (the thumbnail itself), ADR-066/067 (the large layers that made the cost visible).

## 9. Revisit triggers

- **A deployment runs more than one node** and a stale picture on one of them is reported.
- **A layer's thumbnail is reported as out of date after an edit** — redraw on the source's version.

## 10. Dissent

None recorded.

## 11. Conditions

None.
