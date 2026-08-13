# Dependency Licences

Required by §55.

**Outbound licence: Apache-2.0** (Q-73, 2026-08-13). See `LICENSE` and `NOTICE`.
Every inbound dependency must be compatible with redistribution under Apache-2.0.
**This makes one class of dependency newly disqualifying: GPL and AGPL components
cannot be linked into anything we ship**, because we cannot sublicense them under
Apache-2.0. LGPL remains usable via the Tier 2 port layer. The note below records
the earlier *inbound* posture, which predates the outbound choice and is now
constrained by it.

**Project licensing posture:** open source; copyleft (GPL/AGPL) is acceptable to
the project owner. There is therefore no *exclusion* pressure on dependencies.
This register exists to track **obligations**, not to filter candidates.

---

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
| PostgreSQL | Baseline database | external | PostgreSQL Licence | `UNVERIFIED` | Separate process; not linked. |
| PostGIS | Spatial extension | external | GPL-2.0-or-later | `UNVERIFIED` | Separate process; not linked. Distribution posture differs from linking. |
| SQL Server driver | Data provider | 2 | `Microsoft.Data.SqlClient` is MIT | `UNVERIFIED` | The straightforward one. No longer needed for the platform store (Q-51). |
| **MySQL / MariaDB driver** | Data provider (Q-80) | 2 | **two drivers, two licences** | `UNVERIFIED` — **choose deliberately** | Oracle's official `MySql.Data` is **GPLv2 with a FOSS exception**, which Apache-2.0 cannot sublicense. The community `MySqlConnector` is **MIT** and can. This is a licence violation if chosen wrongly, not a preference. Honua ships `MySqlConnector`. |
| **DuckDB** | File-format query engine (Q-81) | 2 | MIT | `UNVERIFIED` | **Its spatial extension bundles GDAL**, which is why Q-87 must decide whether DuckDB runs in the serving container or the job worker — see [reviews/contradiction-sweep-1.md](reviews/contradiction-sweep-1.md) S4. |
| Oracle client / driver | Data provider | 2 | proprietary, restrictions likely | `UNVERIFIED` — **and materially worse since Q-73** | **Highest-risk row in this file, and the risk changed shape on 2026-08-13.** This row was written when the project had no outbound licence. It now has **Apache-2.0**, which warrants to every downstream user that they may redistribute freely — so shipping a driver we may not redistribute breaks that warranty for everyone who forks us, not just for us. **Likely resolution: database drivers are customer-supplied or separately-licensed components, not bundled**, which is a Q-71 packaging consequence. Oracle client libraries have historically carried redistribution restrictions. We are a client, so the customer's database licence is theirs — but our right to *ship a driver* is ours to verify. Thin/managed drivers may avoid the native client entirely; check before implementing. |
| SQLite | Embedded platform store | 2 | public domain | `UNVERIFIED` | |
| GEOS | Geometry topology | 2 | LGPL-2.1 | `UNVERIFIED` | LGPL: dynamic linking and replaceability obligations. Our port layer already satisfies the replaceability intent. |
| PROJ | Coordinate transformation | 2 | MIT-style | `UNVERIFIED` | Grid data files carry **separate** licences and must be checked individually. |
| GDAL | Raster and vector I/O | 2 | MIT-style (core) | `UNVERIFIED` | **Drivers vary.** Individual drivers and their upstream libraries carry their own licences, some copyleft, some patent-encumbered. A GDAL build is not one licence — it is a bill of materials. |
| JTS | Geometry (JVM) | 2 | EPL/EDL dual | `UNVERIFIED` | Only if ADR-001 selects the JVM. |
| NetTopologySuite | Geometry (.NET) | 2 | BSD-style | `UNVERIFIED` | Only if ADR-001 selects .NET. |
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
