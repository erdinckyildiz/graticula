# ADR-024 — Shapefile import, and the exception it costs

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-08-15 |
| **Answers** | Q-98 |

---

## 1. Context

**The people this product is for have shapefiles.** Not GeoJSON. The target is
an organisation already working in ArcGIS that cannot afford ArcGIS Enterprise
(§1 of [ADR-018](ADR-018-authorization-and-roles.md)), and thirty years of their
data is `.shp`. Telling them to convert first means telling them to install
QGIS, which is precisely the friction this product exists to remove.

**It did not ship with GeoJSON because of a security rule, not an oversight.**
[security.md](../security.md)'s upload section says archives are never opened —
*decompression bombs are not our problem if we never decompress* — and a
shapefile is a ZIP of three to six files. That rule made the product decision,
and [Q-98](../open-questions.md) recorded it as the owner's to settle rather
than taking it quietly.

## 2. Alternatives considered

### Alternative A — never accept shapefiles

**Argument for.** The rule stays absolute, which is the strongest form a
security rule can take: there is no code path to audit, no bound to get wrong,
and no future engineer who relaxes a number without understanding why it was
chosen. Every decompression CVE in every other product is irrelevant to us.

**Argument against.** It refuses the format the audience actually has. A GIS
server that cannot read a shapefile is not a smaller product; for most of the
people described in ADR-018 §1 it is not a usable one.

### Alternative B — loose files, no archive

**Argument for.** Accept `.shp`, `.dbf`, `.prj` and `.cpg` as separate multipart
parts. The rule survives completely intact and nothing decompresses.

**Argument against.** Their file is a `.zip`, from QGIS or ArcMap or a
government portal. Asking them to unzip it first is a smaller version of asking
them to convert it, and it would be a permanent, visible oddity in the product.

### Alternative C — a bounded exception (chosen)

**Argument for.** Opens the format at a cost that can be written down, bounded,
and tested. The bounds are checkable properties rather than promises.

**Argument against.** It is the thing the rule was written to avoid, and every
other tool that does this has had a CVE for it. A rule with an exception is a
rule somebody will extend.

### Alternative D — a separate trusted privilege

**Argument for.** Only a caller holding a new, narrower privilege reaches the
decompressor at all.

**Argument against.** Adds a privilege ArcGIS Portal does not have, to the
matrix ADR-018 adopted precisely *because* it already exists. And
`content:publishFeatures` is already a trust boundary: somebody who can create
tables in the datastore is not being held back by a ZIP.

## 3. Counterarguments to the preferred option

**The strongest one: this rule was load-bearing and now it is conditional.** Its
value was that it needed no reasoning — nothing decompressed, so nothing could
be a decompression bomb. What replaces it is five numbers and a code path, and
five numbers can be wrong in a way "never" cannot.

The answer is that the numbers are checked rather than trusted, and the one that
matters is enforced against the bytes rather than against the header. But that
is a mitigation, not a refutation: **the attack surface is genuinely larger than
it was yesterday**, and this ADR says so rather than claiming otherwise.

**The second: exceptions grow.** The next format that arrives in a ZIP —
FileGDB, GeoPackage in an archive, a KMZ — will point at this one. Condition 3
exists for that.

## 4. Evidence

| Claim | Evidence |
|---|---|
| A real decompression bomb is refused | 200 MB from 200 KB, refused on the declared ratio in milliseconds; and refused again by the reading limit with the ratio check disabled, proving the two controls are independent |
| The reader handles real data | 50 polygons exported from the PostGIS corpus: 50/50 valid in PostGIS after import, 3,663 vertices, Turkish names intact |
| Winding cannot be trusted | **2 of those 50** carry a counter-clockwise outer ring and a clockwise hole — the opposite of the specification. Read by winding they became nested shells and PostGIS reported 48/50 valid |
| Wrong encoding corrupts silently | Reading the cp1254 corpus file as UTF-8 throws nothing and returns different text |
| A path inside the archive is refused | `sub/points.shp` |
| A nested archive is not opened | `.zip` is not in the extension allow-list |

**30 tests**: 16 on the reader, 14 on the archive bounds, plus the corpus
generator checked in at [tools/make-shapefile-corpus.py](../../tools/make-shapefile-corpus.py).

## 5. Decision

Hosted-data import accepts a ZIP containing one shapefile. **Archives are opened
by exactly one class, `BoundedArchive`, under five bounds** — 256 MB total and
per member *enforced while reading*, 32 members, a 100× compression-ratio
refusal, and an extension allow-list of `.shp`, `.dbf`, `.prj`, `.cpg` that does
not include `.zip`. Nothing is written to disk and no path inside the archive is
honoured. The reader is ours, Tier 1, alongside the GeoJSON reader.

**The layer keeps the reference it arrived in.** Amended the same day by owner
direction: the importer does not transform, and the tile path projects per
request ([ADR-021](ADR-021-tile-encoding.md) §5a, Q-96). A shapefile in a
national grid is stored in that grid.

**Rings are grouped by containment, not by winding.** **The DBF encoding is
asked for when the file does not declare it.** **The SRID is asked for**: a
`.prj` is WKT, and matching WKT to an EPSG code by comparing strings is how a
layer comes to be declared as a system it is not in — the file's `.prj` is
echoed back in the refusal so the caller can see what it claimed.

## 6. Consequences

**Positive.** The format the audience has is the format the product reads. The
reader is verified against a corpus another implementation wrote and against
real geometry, which is better evidence than the GeoJSON reader has. The
containment rule fixed a defect that would have put invalid geometry in the
database silently.

**Negative.**

- **The attack surface grew** and is now maintained rather than absent.
- **Two round trips for a first-time user**: the SRID and possibly the encoding
  are refusals before the import works. Deliberate, and it will be the most
  common complaint about this feature.
- **Z and M values are read past and dropped.** The geometry model is
  two-dimensional and the layer document says `hasZ: false`, so carrying them
  would store something no surface can serve. Stated at import, and it is a
  loss.
- **MultiPatch is not read at all.**
- **The whole file is held in memory**, bounded by the archive limits. The
  GeoJSON path streams; this one cannot, because attributes are matched to
  shapes by position and both are needed at once.

**Ports created.** None. `System.IO.Compression` is base class library.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-062 | 256 MB uncompressed is above any shapefile a person uploads through a browser and below what troubles the machine | `UNVALIDATED` — reasoned from the corpus, not measured against a real estate's largest layer |
| A-063 | Refusing an undeclared encoding costs less than silently corrupting text | `UNVALIDATED` by use. It is the owner's choice and the reasoning is sound; whether people find it obstructive is a question only a deployment answers |

## 8. Conditions

1. **The archive bounds are exercised by a test that would fail if any one of
   them were removed.** *(Discharged — 14 tests, including one that disables the
   ratio check to prove the reading limit holds alone.)*
2. **The reader is verified against files this project did not write.**
   *(Discharged — the corpus is written by pyshp and by PostGIS, and the winding
   defect was found because of it.)*
3. **A second archive format does not reuse this exception without its own
   ADR.** FileGDB, GeoPackage-in-a-zip and KMZ will each point at this decision;
   each needs its own bounds and its own argument, because "we already
   decompress" is not one.
4. **The largest real shapefile anybody imports is measured against the 256 MB
   ceiling**, before the first deployment that matters. A-062 is a guess.
5. **Dropping Z and M is stated in the import response**, not only in this
   document. A loss the caller is not told about at the time is a loss they find
   later.

## 9. Dissent

**Recorded.** The absolute rule was better *as a rule* than what replaces it,
and nothing here disputes that. Alternative A is not wrong about security; it is
wrong about the product. Anyone reading this later should understand that the
exception was taken knowingly, priced, and bounded — and that the argument
against it was never defeated, only outweighed.
