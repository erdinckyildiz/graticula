# Dependency Licences

Required by §55.

**Outbound licence: Apache-2.0** (Q-73, 2026-08-13). See `LICENSE` and `NOTICE`.
Every inbound dependency must be compatible with redistribution under Apache-2.0.
**This makes one class of dependency newly disqualifying: GPL and AGPL components
cannot be linked into anything we ship**, because we cannot sublicense them under
Apache-2.0. LGPL remains usable via the Tier 2 port layer. The note below records
the earlier *inbound* posture, which predates the outbound choice and is now
constrained by it.

**Project licensing posture:** ~~open source; copyleft (GPL/AGPL) is acceptable
to the project owner. There is therefore no *exclusion* pressure on dependencies.
This register exists to track **obligations**, not to filter candidates.~~

**Corrected 2026-08-15 by the §66 licensing gate.** That paragraph describes the
*inbound* posture from before Q-73, and Q-73's Apache-2.0 outbound choice
reverses its conclusion: GPL and AGPL components cannot be linked into anything
shipped, so this register **does** filter candidates as well as tracking
obligations. Left struck rather than deleted, because the earlier position is why
some research notes weigh licences the way they do.

---

---

## §66 Licensing review gate — RUN 2026-08-15, and it passes

**Every dependency verified from the package itself**, by reading the `<license>`
expression in the shipped `.nuspec` rather than from documentation, a website, or
memory. That distinction is the whole point of the gate: a licence somebody
wrote down is a claim, and a licence in the artefact being redistributed is the
fact.

| Package | Version | Licence | Verified from | Compatible with Apache-2.0 outbound |
|---|---|---|---|---|
| Npgsql | 9.0.2 | **PostgreSQL** (BSD-style, permissive) | nuspec `<license type="expression">` | **Yes.** Permissive, attribution only |
| Konscious.Security.Cryptography.Argon2 | 1.3.1 | **MIT** | nuspec | **Yes** |
| xunit | 2.9.2 | **Apache-2.0** | nuspec | **Yes**, and identical to outbound |
| xunit.runner.visualstudio | 2.8.2 | **Apache-2.0** | nuspec | **Yes** |
| Microsoft.NET.Test.Sdk | 17.12.0 | **MIT** | nuspec | **Yes** |
| Microsoft.Extensions.TimeProvider.Testing | 9.0.0 | **MIT** | nuspec | **Yes** |
| **DejaVu Sans** (font, redistributed) | 2.37 | **Bitstream Vera** (permissive) + public-domain DejaVu changes | the licence text shipped in [tools/fonts/LICENSE-DejaVu.txt](tools/fonts/LICENSE-DejaVu.txt) | **Yes**, with one obligation that is not the usual one — see below |

**Result: pass.** No GPL or AGPL component is linked into anything shipped,
which is the one class the Apache-2.0 outbound choice (Q-73) made disqualifying.
Four of the six are test-only and never ship at all.

### What the gate found beyond the table

**The register described a project that no longer exists.** Its own preamble
still said *"copyleft (GPL/AGPL) is acceptable to the project owner. There is
therefore no exclusion pressure on dependencies… This register exists to track
obligations, not to filter candidates."* Q-73 chose Apache-2.0 outbound on
2026-08-13, which makes GPL and AGPL disqualifying — so the register's stated
purpose was the opposite of its actual one. Corrected below.

**Two Tier 2 candidates were avoided for reasons that were never licence
reasons, and it is worth saying so.** NetTopologySuite appears in
`/benchmarks` and in no shipped assembly, because [ADR-021](docs/adr/ADR-021-tile-encoding.md)
retired the in-process encoder — a measurement outcome, not a licensing one. A
.NET projection library was rejected by [ADR-022](docs/adr/ADR-022-geometry-server.md)
§4 because two coordinate engines disagree by metres, also not a licensing
reason. **The shipped dependency list is six packages, and the two largest
candidates were declined on evidence.**

**What this gate did not check:** transitive dependencies. The six above are
direct; Npgsql and the test packages pull others, and `dotnet list package
--include-transitive` would enumerate them. That is the honest limit of a gate
run by reading six nuspecs, and it is recorded rather than implied — see
[D-06](docs/architecture-debt.md), which is narrowed rather than closed.

---

### The font is not like the other rows

**Added 2026-08-15 with [ADR-027](docs/adr/ADR-027-glyphs-and-sprites.md).** The
vector tile service ships signed-distance-field glyphs so that a style can draw a
label at all, and they are generated from **DejaVu Sans**. Two things make this
row different from every other one in the table:

- **The artefact is redistributed, not linked.** The `.ttf` is in the repository
  ([tools/fonts/DejaVuSans.ttf](tools/fonts/DejaVuSans.ttf)) and its glyph
  outlines are baked into the `.pbf` ranges the server serves. That is
  distribution of the Font Software, which is exactly what the licence governs.
- **The notice must travel with it.** Bitstream Vera requires the copyright and
  permission notice to be included in all copies, so
  [tools/fonts/LICENSE-DejaVu.txt](tools/fonts/LICENSE-DejaVu.txt) sits beside
  the font rather than only being referenced here.

One condition of that licence is worth stating because it constrains a future
change: **modified versions may not keep the Bitstream or Vera names.** If
anybody ever subsets or edits this font, the derived font must be renamed — and
the font stack name the server serves it under is user-visible, so that rename
would reach the API.

The generated ranges are derived from the outlines and are covered by the same
permission. They are not a separate licence question, and they are not a
different obligation.

## ⚠ Verification status

Every entry below is marked `UNVERIFIED`. The licences are recorded from general
knowledge and **have not been checked against the actual source distributions**.

No licence claim in this file may be relied upon in an ADR until it is verified
against the upstream `LICENSE`/`COPYING` file of the exact version we intend to
use, and marked `VERIFIED` with a date. Licences change across versions, and
bundled components frequently differ from the parent project.

## Candidate dependencies

| Component | Role | Tier | Licence (claimed) | Status | Obligation notes |
|---|---|---|---|---|---|
| **Npgsql** | PostgreSQL driver | 2 | PostgreSQL Licence (BSD-style) | `UNVERIFIED` | **Actually referenced today**, by `Graticula.Platform.Postgres` and `Graticula.Providers.PostGis` only; an architecture test fails the build if it reaches Tier 1. Permissive and unproblematic under Apache-2.0 outbound. |
| **Konscious.Security.Cryptography.Argon2** | Password hashing (ADR-015 §5) | 2 | MIT | `UNVERIFIED` | **Actually referenced today**, behind `IPasswordHasher`. Managed, no native payload. **The row that deserves scrutiny disproportionate to its size**: it is a small community package holding the single most security-critical primitive in the server, and .NET ships no Argon2id of its own. The port exists so replacing it — libsodium via NSec, or a future BCL implementation — touches one file. |
| PostgreSQL | Baseline database | external | PostgreSQL Licence | `UNVERIFIED` | Separate process; not linked. |
| PostGIS | Spatial extension | external | GPL-2.0-or-later | `UNVERIFIED` | Separate process; not linked. Distribution posture differs from linking. |
| **OpenLayers 10.3.1** | The console's layer viewer (`wwwroot/ol.js`, `ol.css`) | **vendored code, 858 KB + 6 KB** | **BSD-2-Clause** | `VERIFIED 2026-08-16` — read from the published package metadata for the exact version fetched, not from the project's website; the minified build carries no header comment | **The only third-party code shipped inside this product.** Vendored rather than loaded from a CDN, which is the whole reason it was chosen over Esri's SDK (ADR-020 §4b): no runtime third-party request, so the console's policy needs no foreign origin, an air-gapped deployment works, and nobody outside learns the server exists. Pinned — a floating major version in a committed file is a silent upgrade nobody reviewed. Esri's SDK remains CDN-loaded on the compatibility-probe page only, and is not shipped. |
| **Natural Earth 1:110m** | The console's fallback map ground (`wwwroot/ground-*.geojson`) — countries, lakes, rivers, cities, and a generated graticule | **vendored data, 87 KB** | **Public domain** | `VERIFIED 2026-08-16` — Natural Earth states all its raster and vector data is in the public domain, no permission needed, attribution appreciated rather than required | **Shipped data rather than code, and now the fallback rather than the ground.** Vendored because a map with no ground cannot tell *being in the wrong place* from *having no data*, and public domain is what makes shipping possible where a tile service is not. **It is drawn only when no rendered basemap and no imported ground are in use** (ADR-020 §4c) — on a first run, in other words. Properties stripped, coordinates rounded; attribution is carried in each layer's `copyright` and shown on the map. **196 KB total.** |
| **OpenStreetMap data** | Imported by the operator into the datastore and served as our own vector tiles (ADR-020 §4d) | **not shipped** | ODbL 1.0 | `N/A — we distribute none of it` | Recorded here to be explicit that it is *not* a dependency of this product. The operator downloads and imports; this server cuts tiles from what is in their datastore. So the ODbL obligations — attribution and share-alike on a derived database — belong to them, and nothing changes for our own outbound licence. Distinct from OpenStreetMap's **tile service**, which has a usage policy and is a separate decision in ADR-020 §4c. |
| SQL Server driver | Data provider | 2 | `Microsoft.Data.SqlClient` is MIT | `UNVERIFIED` | The straightforward one. No longer needed for the platform store (Q-51). |
| **MySQL / MariaDB driver** | Data provider (Q-80) | 2 | **two drivers, two licences** | `UNVERIFIED` — **choose deliberately** | Oracle's official `MySql.Data` is **GPLv2 with a FOSS exception**, which Apache-2.0 cannot sublicense. The community `MySqlConnector` is **MIT** and can. This is a licence violation if chosen wrongly, not a preference. Honua ships `MySqlConnector`. |
| **DuckDB** | File-format query engine (Q-81) | 2 | MIT | `UNVERIFIED` | **Its spatial extension bundles GDAL**, which is why Q-87 must decide whether DuckDB runs in the serving container or the job worker — see [reviews/contradiction-sweep-1.md](docs/reviews/contradiction-sweep-1.md) S4. |
| Oracle client / driver | Data provider | 2 | proprietary, restrictions likely | `UNVERIFIED` — **and materially worse since Q-73** | **Highest-risk row in this file, and the risk changed shape on 2026-08-13.** This row was written when the project had no outbound licence. It now has **Apache-2.0**, which warrants to every downstream user that they may redistribute freely — so shipping a driver we may not redistribute breaks that warranty for everyone who forks us, not just for us. **Likely resolution: database drivers are customer-supplied or separately-licensed components, not bundled**, which is a Q-71 packaging consequence. Oracle client libraries have historically carried redistribution restrictions. We are a client, so the customer's database licence is theirs — but our right to *ship a driver* is ours to verify. Thin/managed drivers may avoid the native client entirely; check before implementing. |
| SQLite | Embedded platform store | 2 | public domain | `UNVERIFIED` | |
| GEOS | Geometry topology | 2 | LGPL-2.1 | `UNVERIFIED` | LGPL: dynamic linking and replaceability obligations. Our port layer already satisfies the replaceability intent. |
| PROJ | Coordinate transformation | 2 | MIT-style | `UNVERIFIED` | Grid data files carry **separate** licences and must be checked individually. |
| GDAL | Raster and vector I/O | 2 | MIT-style (core) | `UNVERIFIED` | **Drivers vary.** Individual drivers and their upstream libraries carry their own licences, some copyleft, some patent-encumbered. A GDAL build is not one licence — it is a bill of materials. **Confirmed from the source 2026-08-18**, and the warning above is the project's own words matching upstream's: `LICENSE.TXT` opens by saying it *"attempts to include all licenses that apply within the GDAL/OGR source tree"* and then lists BSD, public-domain, **Apache-2.0 (Esri components)**, ISC, Info-ZIP and Qhull terms beside the MIT-style core. The Esri-contributed parts under Apache-2.0 are unproblematic for us — permissive, and §7 accepts copyleft anyway — but they are the reason *"GDAL is MIT"* must not be written as a finding. **`OpenFileGDB` specifically is in-tree and reverse-engineered**, and its documentation states it *"does not depend on a third-party library"* — so the File Geodatabase route needs no Esri SDK and carries no separate licence to chase. That is the driver [Q-108](docs/open-questions.md) is about; the SDK-backed `FileGDB` driver is the one to avoid. |
| JTS | Geometry (JVM) | 2 | EPL/EDL dual | `UNVERIFIED` | Only if ADR-001 selects the JVM. |
| NetTopologySuite | Geometry (.NET) | 2 | BSD-3-Clause | `UNVERIFIED` | **Actually referenced since 2026-08-15**, by `Graticula.Overlay.Worker` and by nothing else. Not merely a tier boundary: the worker is a separate *process*, so the library never loads in the server (Q-97, [ADR-022](docs/adr/ADR-022-geometry-server.md) §9). BSD-3-Clause is permissive and unproblematic under Apache-2.0 outbound; the row stays `UNVERIFIED` because nobody has read the shipped nuspec, and D-06's trigger is the first binary that bundles it. |
| `geo` / `geo-types` | Geometry (Rust) | 2 | MIT/Apache-2.0 | `UNVERIFIED` | Only if ADR-001 selects Rust. |
| Skia | Rasterisation | 2 | BSD-3-Clause | `UNVERIFIED` | Large native build; distribution size and toolchain cost matter as much as licence. |
| Cairo | Rasterisation | 2 | LGPL-2.1 / MPL dual | `UNVERIFIED` | |
| HarfBuzz | Text shaping | 2 | MIT-style | `UNVERIFIED` | Usually pulled in by the rasteriser. |
| FreeType | Font rasterisation | 2 | FTL / GPL-2.0 dual | `UNVERIFIED` | |
| MapLibre Native | Rendering | 2 | BSD-2-Clause | `UNVERIFIED` | Licensing is mixed across components; check per module. |

## Rejected on architectural grounds, not licence

Recorded so the reason is not later misremembered as a licensing problem.

| Component | Reason |
|---|---|
| MapServer | Tier 3 — finished server product. Adopting it means adopting its architecture ([build-vs-adopt-policy.md](docs/build-vs-adopt-policy.md)). |
| GeoServer | Tier 3, as above. |
| QGIS Server | Tier 3, as above. |

## Obligations to state explicitly

- **AGPL.** If any AGPL component is ever linked into the server, the network-use
  clause applies: users interacting with the running server over a network gain
  source rights. For a server product this is a materially different obligation
  from GPL. Nothing currently proposed is AGPL, but the project's own licence
  choice is still open and this is the clause that matters most for it.
- **LGPL.** Requires that the user be able to replace the LGPL component. Our
  Tier 2 port layer means we satisfy this by construction rather than by
  paperwork.
- **GDAL driver bill of materials.** Must be enumerated per build before any
  distribution. This is the single most likely place for an unpleasant surprise.
- **Data files, not just code.** PROJ transformation grids, EPSG database, font
  files and any bundled sample data each carry their own terms.

## Process

1. Before an ADR adopts a dependency, its row here must be `VERIFIED`.
2. Before any distribution, the full transitive tree is enumerated and checked —
   not just the direct dependencies listed above.
3. Re-verified at every major version upgrade.

**This file is not legal advice.** Before commercial or wide public
distribution, the obligations here need review by someone qualified to give it.
