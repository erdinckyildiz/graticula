# ADR-060 — The axis order comes from the authority's register, carried as data

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the answer, which is the authority's own · `HIGH` for the defect, measured on a running server · `MEDIUM` for carrying the register as data rather than reading it |
| **Date** | 2026-09-09 |
| **Answers** | [Q-123](../open-questions.md) |
| **Touches** | [ADR-009](ADR-009-raster-engine.md) §2.2 · [ADR-042](ADR-042-ogc-api-features.md) · WFS, WMS and OGC API Features |

---

## 1. Context

Two questions this server asks about a spatial reference were answered by one expression in
`AxisOrder`:

```csharp
srid is >= 4000 and <= 4999
```

— for *does the authority list latitude or northing first*, and, in an identical second method,
for *is this measured in degrees*. The file said so itself: **the same block, asked a different
question**.

**It was a heuristic and the file stated it as one**, which is the honest half. What it also
said was that the general answer was not available: *"this deployment's `spatial_ref_sys.srtext`
carries **no AXIS clauses at all**, so the database cannot be asked."* That sentence had been
false for fifteen days — Q-123 recorded on 2026-08-25 that 6,732 of 8,500 rows do carry one —
and the file never learned. [D-130](../architecture-debt.md)'s propagation shape, in the file a
reader checks first.

### What it costs, measured rather than argued

Against PROJ's own register (EPSG v10.008, the one PostGIS 3.4.3 ships):

| | codes |
|---|---|
| EPSG codes whose first axis is north or south | **2,125** |
| of those, **outside** 4000–4999 | **1,444** |
| geographic 2D/3D codes outside 4000–4999 | **307** |

**Among the 1,444 is every Turkish national grid** — 5251–5259, 5263–5264, 5269–5275 — and
**TUREF itself, EPSG:5252**, which is *geographic* and outside the block.

**Measured on the running fixture, one feature, one second apart:**

```
srsName=urn:ogc:def:crs:EPSG::4326  →  39.979061 32.858576   (lat, lon)   correct
srsName=urn:ogc:def:crs:EPSG::5252  →  32.858576 39.979061   (lon, lat)   transposed
```

5252's axis definition is *identical* to 4326's. It was transposed because its number is 5252.

**And in the read direction, which is the proof rather than a second symptom.** A WFS `bbox` in
EPSG:5253's authority order matched **0** features; the same box written easting-first matched
**2**. So the server was wrong in both directions at once, and consistently enough that a client
doing both would have seen nothing wrong.

`ST_Transform` confirms which ordinate is which: 5253 is `+x_0=500000`, so `1000592` is the
easting and `4443679` the northing, and EPSG defines 5253 **Northing first**.

### Where the truth is not

Three routes were measured and none of them answers it:

- **`spatial_ref_sys.srtext`** carries `AXIS` for 6,732 of 8,500 rows and is a
  *visualisation-order* rendering: only 435 put north first where the authority says 1,551 do,
  and every code this decision is about carries no `AXIS` at all.
- **`postgis_srs('EPSG', …)`** returns WKT1 — `PROJCS[...]` with no `AXIS` for 5253 — and
  PostGIS 3.4 exposes no WKT2 route.
- **`ST_Transform`** normalises: the URN form, the `EPSG:` form and the bare integer all return
  the same longitude-first point, because PostGIS calls
  `proj_normalize_for_visualization` on every path.

**PROJ's `proj.db` answers it exactly**, in an `axis` table keyed by coordinate system, and
PostGIS ships one — `DATABASE_PATH=/usr/share/proj/proj.db` in the container this project runs
against.

## 2. Alternatives considered

### Alternative A — Keep the range, narrow the claim

Write down that it is wrong for 1,444 codes and move on.

**Rejected.** The row's own trigger said *it becomes urgent the first time a customer publishes a
national geographic grid*, and that understates it: WFS `srsName` and WMS `CRS` reach every code
in the register regardless of what any layer is stored in, so it is urgent the first time a
client **asks** for one. For a product whose owner's national grids are all in the wrong set,
this is not a corner.

### Alternative B — `srtext LIKE 'GEOGCS%'`, the free route

98.3% accurate for *is this geographic* — it fixes all 307 out-of-block geographic codes,
including 5252 — using a statement already in this tree at `DeclaredReference.cs:124`. It is one
cached query and no new data.

**Rejected as the whole answer, and it is the closest call here.** It says nothing about the
1,091 projected northing-first codes, which is where 16 of the 18 Turkish grids are. A repair
that fixes TUREF and leaves TM27 transposed is a repair a reader cannot distinguish from a
complete one, which is the failure [CLAUDE.md](../../CLAUDE.md) §2 names. **Its measured
accuracy is recorded here rather than discarded**: if the register ever has to be dropped, this
is the fallback and its cost is known.

### Alternative C — Read `proj.db` at runtime

It is plain SQLite; reading it needs no GDAL.

**Rejected on deployment rather than on principle.** The file lives in the *database's* container,
not the server's; shipping a copy into the host means shipping 8.9 MB and a SQLite dependency
into a process [ADR-009](ADR-009-raster-engine.md) §2.2 deliberately keeps GDAL and PROJ out of.
A process hop per SRID behind a cache would work and is more moving parts than the answer needs.

### Alternative D — Ask PROJ through the datastore

`PostGisProjector` already exists and already caches per-SRID answers behind `IProjector`
(`DomainOfAsync`, built 2026-08-27) — so the port, the cache and the *null means this deployment
cannot say* convention are all built.

**Rejected because the datastore cannot answer.** §1 measured all three routes PostGIS offers.
This alternative is the right shape attached to a source that does not have the fact.

## 3. Decision

**The authority's answer is generated from PROJ's register at build time and carried in the
product as data.**

`tools/axis-order.py` reads a `proj.db` and writes
`src/Graticula.Core/Geometry/AxisOrderRegister.cs`: two `int[]` of run bounds — **359 runs** for
north/south-first and **262** for geographic — with a binary search over them. `AxisOrder`'s two
methods delegate; every face keeps calling what it called.

### 3a. The two questions stop sharing an answer

`IsLatitudeFirst` reads the north/south-first register; `IsGeographic` reads the geographic one.
They were the same expression and they are genuinely different questions: **5253 is north-first
and in metres, 3824 is north-first and in degrees, 3857 is neither**, and a geocentric code
inside 4000–4999 — 4936 — is neither while the old expression called it both.

### 3b. Runs rather than a set, and why the file is readable

2,125 codes compress to 359 `(first, last)` pairs because EPSG allocates in blocks. The lookup
is a binary search either way; what the runs buy is **a diff a person can read** when the
register moves.

### 3c. Both orientations count as north-first

A southing-first grid puts the north-south ordinate first exactly as a northing-first one does.
The question is *which ordinate comes first*, not which way it counts.

### 3d. The method keeps the name `IsLatitudeFirst`

It now answers for northings too. Renaming it would touch every face for nothing a reader gains;
the remark says what it means.

## 4. Consequences

- **Every face that swaps is right for 1,444 more codes**, and unchanged for the ones it was
  already right about — measured: 4326 and 3857 byte-identical before and after.
- **307 geographic codes stop being served as metres.** They had no ±180/±90 projection domain,
  reported `esriMeters` for degrees, and had their scale denominator computed with the wrong
  metres-per-unit.
- **The register goes stale.** EPSG revises; this file does not. That is the cost this decision
  accepts, and condition 2 turns it into a failing test rather than a wrong map.
**State.** None. This decision adds nothing to the catalogue and holds nothing at runtime: the
register is a `static readonly int[]` compiled into `Graticula.Core`, identical in every worker,
read-only, and computed at build time rather than at start. There is no node-local against shared
distinction to draw, which is the whole point of §3's choice over Alternative C — a lookup would
have had a cache, and a cache is state.

- **The generated file is 621 lines of data.** It is `<auto-generated>`, it names the register it
  came from, and it is not edited by hand.

## 5. Assumptions

- **A-082**: the EPSG register's axis definitions change rarely enough that regenerating with the
  PostGIS image is soon enough. `UNVALIDATED` — condition 2 is what would show it false.
- **A-083**: no deployment needs an axis answer for a code PROJ's register does not carry.
  `UNVALIDATED`. A code outside the register answers *not north-first, not geographic*, which is
  the old behaviour for everything outside 4000–4999 and is the safer of the two defaults for a
  projected grid.

## 6. Conditions

1. **The defect is demonstrated and the repair is measured on a running server**, on both the
   write and the read path, because a fix asserted only in unit tests is a fix asserted against
   the same table it was built from.
   ***(DISCHARGED 2026-09-09.)*** Before: `EPSG::5252` → `32.858576 39.979061`, and a `bbox` in
   EPSG:5253's authority order matched **0** features against the easting-first box's **2**.
   After: `EPSG::5252` → `39.979061 32.858576`, `EPSG::5253` → `4443679 1000592` (northing
   first), and the `bbox` result is exactly reversed — **2** for the authority's order, **0** for
   easting-first. `EPSG::4326` and `EPSG::3857` are unchanged in both directions.

2. **A stale register is a failing test rather than a wrong map.** The whole cost of §3's choice
   is staleness, and a cost nothing measures is a cost nobody pays until a customer does.
   `AxisOrderRegisterTests` must regenerate from a `proj.db` when one is available and fail when
   it disagrees with the file in the tree.
   ***(DISCHARGED 2026-09-09.)*** `TheAxisRegisterIsNotStaleTests` — named for what it asserts
   rather than for the file it asserts about, which is why the class this line asked for by name
   never existed — runs `tools/axis-order.py` into a temporary file and compares bytes, so the
   generator is *inside* what is checked rather than reimplemented beside it; a test that queried
   `proj.db` itself would be a second copy of the two SQL statements that would drift. Measured
   against the register PostGIS 3.4.3 ships, taken the way the tool's own docstring says
   (`docker cp gis-experiment-postgis:/usr/share/proj/proj.db`): **36 of 36, and regenerating
   changes nothing.** Absent `GRATICULA_TEST_PROJ_DB` it **fails rather than skips** — the rule
   this repository has broken four times, and the only version of this condition worth having,
   because staleness is exactly what nobody notices unaided.

3. **The table that guards this spans all four quadrants.** The guard that existed —
   `GateFindingsTests`'s theory over 4326, 4258 and 4269 — could not fail for any reason Q-123
   describes, because every code in it is inside the block.
   ***(DISCHARGED 2026-09-09.)*** `AxisOrderAgainstTheRegisterTests` is ten codes across
   geographic-inside, geographic-outside, projected-easting-first and projected-northing-first,
   with the expected column read from PROJ's `axis` table rather than from this server.
   **Falsified**: restoring the range expression fails **seven of thirteen**, including all five
   north-first codes outside the block.

4. **What is advertised and what is served are the same set.** WFS advertises **no** `OtherCRS`
   and serves any resolvable code; WMS advertises three and answers 200 for a fourth, where
   WMS 1.3.0 §7.3.3.3 requires `InvalidCRS`. That is what turns a bounded defect into an
   unbounded one — this decision fixes the answer, and a reference nobody advertised is still a
   reference nobody agreed to serve.

   **Not discharged, and narrowed rather than left as written — measured face by face on the
   running fixture, 2026-09-09.** What this text did not say is that it is a property of *faces*
   rather than of the server, and the faces do not agree: **two of the five already hold, two do
   not, and one has nowhere to hold.** Nothing here was repaired; what changed is that the shape
   of the remaining work is now a measurement instead of a sentence.

   | face | advertises | serves | |
   |---|---|---|---|
   | OGC API Features | `CRS84`, `EPSG/0/4326`, the storage reference and `EPSG/0/3857` — three on this fixture, where storage is one of the two | exactly those | **holds** |
   | VectorTileServer | `102100`/`3857` in `tileInfo` and in `fullExtent` | that, and there is no parameter to ask for another | **holds** |
   | WFS | one `DefaultCRS` per feature type, **zero** `OtherCRS` | every code PROJ's register carries | **does not** |
   | WMS 1.3.0 and 1.1.1 | `CRS:84`, `EPSG:4326`, `EPSG:3857`, plus each layer's own | every code PROJ's register carries | **does not** |
   | FeatureServer, MapServer | one `spatialReference` — its own | every code PROJ's register carries | **no set to compare** |

   **The two that hold, driven rather than read.** A collection served every reference it lists
   and refused `EPSG/0/5253`, `EPSG/0/32636`, an unresolvable code and even the `urn:` form
   of a reference it *does* list — on `crs`, on `bbox-crs` and on the single-feature path, which
   are two separate implementations of the same rule. The tile face returned a **byte-identical**
   tile for `?outSR=5253&crs=EPSG:5253&bboxSR=5253`.

   **The two that do not, and it is not a blank 200.** WFS answered
   `srsName=urn:ogc:def:crs:EPSG::5253` with a document declaring exactly that `srsName`, against
   a capabilities document carrying no `OtherCRS` anywhere; 5252, 32636 and 2039 the same. WMS
   drew `ci_buildings` over its own extent in EPSG:5253 with **741 inked pixels of 16,384** where
   the advertised EPSG:3857 draws **722**, and in EPSG:32636 with 722 — the layer, drawn, in a
   reference no document on this server offers. `GetFeatureInfo` answers in it too.

   **Confirmed from outside, by a tool that has no opinion about this repository.** The CITE WFS
   2.0 run of 2026-08-26 records `BasicGetFeatureTests#getFeatureInOtherCRS` (OGC 09-025r2
   §7.9.2.4.4) as *untested*, skipped with **"No alternative (non-default) CRS supported for any
   feature type with data."** The suite read the capabilities document, concluded this server
   supports no alternative reference, and did not run the test — while the server was serving
   thousands. That is the defect stated by an independent reader
   ([cite-wfs20-2026-08-26.rdf](../reviews/cite-wfs20-2026-08-26.rdf)).

   **A second, smaller finding that is cheap and separable.** `crs=NOTACRS` is refused with
   `code="InvalidCRS"`, correctly. `crs=EPSG:999999` — well-formed, and PROJ does not have it —
   is refused with a `ServiceException` carrying **no `code` attribute at all**, because that
   refusal comes from `ErrorResponse`'s projector branch rather than from the CRS validator. So
   the one case §7.3.3.3 is named for is the one case the code is missing from. This does not
   need the set question answered first.

   **Why the rest is a decision rather than a repair, which is why it stays open.** The two
   directions are not symmetric. *Serve only what is advertised* removes capability people use —
   a client asking a national grid of a layer stored in Web Mercator is the case this ADR exists
   for. *Advertise what is served* is not available in full: the served set is PROJ's whole
   register, and a WFS document carrying 8,500 `OtherCRS` elements per feature type, nine feature
   types here, is not a document. What is left is **a chosen set, advertised and enforced on
   every face** — and *which* set is a scope decision, not a defect fix.

   **Pinned meanwhile.** `AdvertisedReferencesAreTheServedOnesTests` asserts the two faces that
   hold and asserts the gap on the two that do not, so the day either starts refusing an
   unadvertised reference the build fails naming that face and this condition. **Falsified**:
   pointed at a code PROJ does not carry it fails both arms — *"WFS answered 400 … If that is a
   refusal, condition 4's WFS half is repaired"* — and pointed at an advertised reference the
   Features arm fails on all three paths.

## 7. Revisit triggers

- **A deployment needs a code PROJ's register does not carry** — a custom or a very new EPSG
  code. A-083 is what that falsifies, and Alternative C is the design it points at.
- **The register's cost stops being staleness.** If regenerating turns out to need doing often,
  the trade in §3 changes and Alternative C's process hop starts looking cheap.
- **PostGIS exposes the authority's axis order.** A `postgis_srs` that returned WKT2, or a PROJ
  function reachable through SQL, would make Alternative D live and this file removable.

## 8. Dissent

**The register is a copy of somebody else's data, and copies rot.** Every argument in §3 is about
where to put a fact whose owner is EPSG; carrying it means this project has taken on maintaining
a snapshot of a register that is revised several times a year, and the honest version of §4's
last bullet is that nobody has yet had to regenerate it. Condition 2 is what makes that a
measured cost rather than a hoped-about one, and it is the condition to watch.
