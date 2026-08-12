# Research Notes

**Status:** STUB — not written
**Required by:** §4, §16

---

Raw research output. Findings live here before they are distilled into
[../architecture-assessment.md](../architecture-assessment.md).

Planned topics, in priority order:

| File | Topic |
|---|---|
| `arcgis-som-soc.md` | ArcGIS Server SOM/SOC/ArcSOC runtime model (§16) — highest priority |
| `geoserver.md` | GeoServer architecture, strengths, weaknesses |
| `mapserver-qgis.md` | MapServer and QGIS Server |
| `postgis-thin-servers.md` | pg_tileserv, pg_featureserv, Martin, Tegola, TiTiler |
| `cloud-native-formats.md` | COG, STAC, PMTiles, FlatGeobuf, GeoParquet |
| `geometry-projection-libs.md` | GEOS, JTS, NTS, PROJ — maturity, licence, binding quality |
| `rendering-engines.md` | Skia, Cairo, MapLibre Native |

Every topic answers the three questions from §4:

1. What problem was this design solving?
2. Is that problem still relevant today?
3. Solved from first principles today, what would it look like?

**Clean-room constraint (§5):** publicly documented behaviour and published
architectural reasoning only. Cite sources. Do not reproduce proprietary source
or undocumented internals.
