# ADR-044 — Tiles are served, because the claim had to be true

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-08-22 |
| **Supersedes** | — |
| **Superseded by** | — |

> Amends [ADR-043](ADR-043-imageserver-and-the-raster-face.md) §3.2, which left tiles
> out of the first cut. It does not supersede it: everything else in ADR-043 stands, and
> §3.2's exclusion of raster function chains and dynamic mosaicking stands with it.

---

## 1. Context

**ArcGIS Pro will not open an image service whose `capabilities` string does not contain
the word `Tilemap`.** Not *will not tile it* — will not open it at all. `arcpy.Raster` on
a service claiming `Image` answers `ERROR 000732: does not exist or is not supported`,
which is the same sentence Pro gives for a path that does not exist. An administrator
seeing it has no reason to suspect a capability string and every reason to suspect the
URL, the certificate or their credentials, all three of which are fine.

This was measured rather than read; no public documentation this project could find
states it. [Q-134](../open-questions.md) carries the method and the grid.

**So the choice was between a client and a sentence.** ADR-043 condition 5 says a
capability is not advertised until it is served, and correctness gate 2's fifth finding
is the precedent: `MapServer` claimed `Map,Query,Data` with no query route and was
repaired by removing the claim rather than adding the route. Typing `Tilemap` into the
capabilities string is one word, costs nothing, is untestable at runtime, and makes every
ArcGIS Pro workflow work. It would also be false, and falser than it looks: this face had
`tileInfo: null`, so a client reading the claim would have had no scheme in which to name
a tile. Not merely unserved — unaddressable.

**What forces a decision is that leaving it alone is also a decision.** ADR-043 §3.2's
first cut was chosen to keep the increment reviewable, not because tiles are unwanted. An
image service nobody can open from the client administrators actually use is not a
smaller version of the feature; it is the feature with its main consumer removed.

## 2. Alternatives considered

### Alternative A — Claim `Tilemap` and serve nothing

**Argument for.** One word. ArcGIS Pro works immediately. Pro demonstrably never calls
the operation — a mimic server answering only `/rest/info` and the service document, and
404ing everything else, was accepted by `arcpy.Raster` — so no client would ever discover
the claim was hollow. The cost of being caught is zero because nobody checks.

**Argument against.** *Nobody checks* is the argument every untrue capability string has
ever had, and this repository has already paid for one. Correctness gate 2 found
`Map,Query,Data` on a face with no query route, and the reason it was a finding was not
that a client broke — none had — but that the document is the contract and a false
contract is a defect whether or not it has been exercised yet. The next client is not
ArcGIS Pro.

### Alternative B — Leave the capability at `Image` and record the hole

**Argument for.** Nothing is built, nothing is claimed, the register is honest, and the
increment stays the size it was reviewed at. [D-137](../architecture-debt.md) would carry
it, and a debt row with a measured cause and a stated repair is a legitimate resting
place for work that has not been chosen yet.

**Argument against.** The hole is not small. It is *every ArcGIS Pro user cannot use
imagery*, reported through an error message that sends them to look at three things that
are not wrong. And the repair is not research: the extent arithmetic is a tiling scheme,
and drawing a tile is `exportImage` at a fixed size over a computed extent, which this
face already does. Carrying it as debt would be carrying a known cause with a known cure
because of when it was found rather than what it costs.

### Alternative C — Serve `tilemap` and not `tile`

**Argument for.** `tilemap` is the operation the capability names, and it is the cheaper
half: an extent intersection per tile, no rendering. The claim becomes literally true and
ArcGIS Pro works.

**Argument against.** `tilemap` exists so a client can find out which tiles are worth
fetching. Answering it and then having nothing to fetch is a more elaborate way of being
untrue: the client is told tile 11/769/1193 has data and asked to believe it, and the
route that would give it the data is a 404. A capability that answers the question and
not the follow-up is one operation of a two-operation contract.

## 3. Counterarguments to the preferred option

**A scheme without a cache is a strange thing to publish, and Esri's own model says so.**
Every ArcGIS `tileInfo` in the wild sits beside `singleFusedMapCache: true`, because in
Esri's model a tiling scheme is what a cache is built to. Publishing `tileInfo` with
`singleFusedMapCache: false` is a combination their tooling does not commonly produce,
and a client could reasonably read it as a mistake.

The answer is that the two are genuinely different facts and the conflation is theirs
rather than ours: a scheme is an agreement about how to name a piece of ground, and a
cache is a decision to keep the picture of it. This server publishes the first and not
the second, and says so in both fields. But it is a place where this server is
technically right and conventionally unusual, which is a real cost.

**Tiles that are rendered rather than cached are slow, and a tiling scheme is a promise
of speed.** A client that sees `tileInfo` expects tiles to be cheap, because everywhere
else they are files on disk. Here every tile decompresses a window of a COG. A client
walking a large area will find this face much slower than a cached one, and nothing in
the document warns it.

`singleFusedMapCache: false` is exactly that warning, and it is the field a client is
supposed to read for it. That is a thinner defence than it sounds, because it relies on
clients reading a field most of them ignore.

**This is scope growth found by chasing one client's undocumented behaviour.** ADR-043
§3.2 drew a line, and the line is being moved eight days later because ArcGIS Pro would
not open a service. That is a bad reason to grow a design if it generalises — designing
around one client's quirks is how a codebase acquires a shape nobody can explain.

It does not generalise here, and the test is what the increment leaves behind: a tiling
scheme, a tilemap and a tile route are all things a raster server needs on their own
terms. If the artefact would have been built anyway and the only thing the quirk changed
is *when*, the quirk is a scheduling input rather than a design one.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Pro refuses a service without `Tilemap` in `capabilities` | `Image` → refused; `Image,Tilemap` → accepted; `Image,Mensuration` → refused; `Image,Catalog` → refused, all against arcpy 3.6 | [Q-134](../open-questions.md) |
| It is that word and not merely a second token | two other capability words tried, neither works | the grid above |
| `allowAnalysis: true` is necessary too | `Image,Tilemap,Mensuration` with `allowAnalysis: false` → refused | the grid above |
| Pro never calls the operation | a server answering only `/rest/info` and the service document, 404ing all else, was accepted | Q-134's mimic experiment |
| A tile is the same picture as the export of its ground | byte-identical PNG, SHA-256 `0d722ef1d0ed141d…`, 9804 bytes both ways | `A_tile_is_the_same_picture_as_an_export_of_the_same_ground` |
| The published scales are reproduced exactly | Web Mercator level 0 = 591657527.591555; WGS 84 level 0 = 295828763.795777 | `TilingSchemeTests` |
| Pro opens, describes and draws once served | nine-step probe, no step where this server and Esri's own Terrain3D disagree | `tools/pro-probe.py` |

## 5. Decision

**This face publishes a tiling scheme and serves the two operations that scheme makes
addressable — `tilemap` and `tile` — and only then claims `Tilemap` in its capabilities
string.** The scheme is Web Mercator's published one for a coverage in EPSG:3857, ArcGIS's
published WGS 84 one for EPSG:4326, and one derived from the coverage's own extent for any
other reference. `tileInfo` is populated and `singleFusedMapCache` stays false, because
there is a scheme and there is no cache: a tile is rendered when it is asked for, through
the same code path `exportImage` uses. `exportTilesAllowed` stays false, being the flag
that would claim bulk export of a cache that does not exist.

## 6. Consequences

**Positive.** ArcGIS Pro opens imagery published here, which is the client an
administrator uses. Tiled access to imagery exists for every other client that wants it.
The capabilities string is true, and the conformance suite now asks each word in it to
answer rather than reading it out of the document — which is a stronger form of ADR-043
condition 5 than that condition asked for.

**Negative.** A rendered tile costs what an export costs, so a client walking a large
area at speed will find this face slow in a way a cached service is not, and only
`singleFusedMapCache: false` warns it. The derived scheme for an unusual reference is
interoperable with nothing, and its stated scales assume a metre per ground unit, which is
wrong for a geographic reference that is not 4326 — the resolutions beside them are exact,
so a client choosing by resolution is unaffected and one choosing by scale picks a
neighbouring level. And the increment grew after its ADR was accepted, which is a thing to
notice rather than a thing to be relaxed about.

**Ports created.** None. `TilingScheme` is Tier 1 arithmetic in `Graticula.Core`, with no
library type in any signature.

**State.** *Catalogue*: none. *Runtime*: none. A tiling scheme is arithmetic over
the coverage's reference and extent, computed when a document asks for it — which is why
`singleFusedMapCache` stays false: there is no stored cache for it to describe.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS Pro's gate on `Tilemap` is behaviour rather than a defect of 3.6 | Unverified against another Pro version, and it does not change the decision: the scheme and the two routes are wanted on their own terms |

## 8. Dependencies

**Depends on** ADR-043 (the face and its port), ADR-009 (the ArcGIS REST contract),
ADR-041 (the renderer a tile is drawn on).

**Depended on by** nothing yet.

## 9. Conditions

1. **Each capability the document claims is asked to answer, from outside.** A test that
   reads `capabilities` and compares it with a string proves the claim was made, not that
   it was true — which is the gap that let `Map,Query,Data` stand.

   **DISCHARGED 2026-08-22.** `Every_capability_the_document_claims_has_a_route_that_answers`
   splits the string and exercises each word over HTTP: `Image` must return a PNG from
   `exportImage`, `Tilemap` must have a populated `tileInfo` with levels and must answer a
   `tilemap` request. **A word the test does not recognise fails it**, so adding a
   capability without saying which route answers it is a failing build rather than a
   document nobody re-read.

2. **A tile and an export of the same ground are the same picture.** The two paths share
   their drawing code; nothing in either answer would say so if they diverged, and a tiled
   map that disagrees with the image beside it is a defect that looks like a rendering
   subtlety.

   **DISCHARGED 2026-08-22.** `A_tile_is_the_same_picture_as_an_export_of_the_same_ground`
   computes a tile's extent from the published `tileInfo` — the client's arithmetic, not
   the server's — and asserts the two responses are byte-identical. Measured at 9804 bytes
   and SHA-256 `0d722ef1…` both ways.

3. **The published schemes reproduce the published numbers.** A level whose resolution is
   a millionth off from a basemap's does not line up with it, and the failure reads as a
   projection error rather than an arithmetic one.

   **DISCHARGED 2026-08-22.** `TilingSchemeTests` asserts the constants as literals rather
   than recomputing them: Web Mercator level 0 at 156543.03392800014 and 591657527.591555,
   WGS 84 level 0 at 0.703125 and 295828763.795777. **Getting the second of those right
   required using Esri's 39.37 inches per metre rather than the exact 39.3700787**, which
   is recorded in the constant's own remarks because being exactly right there would have
   been being wrong.

4. **A tile off the edge of a coverage is a picture, not an error.** A client walking a
   grid asks for the corners.

   **DISCHARGED 2026-08-22.**
   `A_tile_off_the_edge_of_the_coverage_is_a_picture_rather_than_an_error` asks for a tile
   far outside any scheme's occupied area and asserts a PNG comes back.

5. **The scheme is not claimed to be a cache.** `singleFusedMapCache` and
   `exportTilesAllowed` both stay false, and the reason is written where a reader of the
   code will meet it rather than only here.

   **DISCHARGED 2026-08-22.** Both fields are false in the service document and
   `TilingScheme`'s own remarks open with the distinction. The conformance suite reads
   `tileInfo` for its levels and never asserts a cache.

6. **ArcGIS Pro is re-run against the served version, not the temporarily-claimed one.**
   The measurement that produced this decision was taken with `Tilemap` typed in and no
   routes behind it, which is exactly the state this ADR exists to avoid shipping.

   **DISCHARGED 2026-08-22.** `tools/pro-probe.py` was run against a coverage published by
   the built server, with the scheme and both routes in place: nine steps, and no step
   where this server and Esri's own `WorldElevation3D/Terrain3D/ImageServer` disagree.
