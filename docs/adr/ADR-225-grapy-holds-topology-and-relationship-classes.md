# ADR-225 — grapy holds topology rules and relationship classes in the GeoPackage

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-30, by owner decision on scope — group 2 begins with *"Topoloji ve ilişki sınıfları"*, then network datasets ([ADR-226](ADR-226-grapy-holds-network-datasets.md)), then versioning ([ADR-227](ADR-227-grapy-holds-versions-and-archives.md)), per [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7. The storage design below is INFERRED and listed for confirmation in §5.5. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`arcpy.management` refuses about forty tools because a GeoPackage has no geodatabase topology and no relationship class:
CreateTopology, AddRuleToTopology, ValidateTopology, ExportTopologyErrors, CreateRelationshipClass,
TableToRelationshipClass, MigrateRelationshipClass and the rest, plus the cursors' relationship reads
(`da.SearchRelatedRecords`). A toolbox written against arcpy that checks "must not overlap" or follows a
one-to-many relationship stops at the first such call.

A topology is a set of feature classes, a cluster tolerance, ranked classes and rules, and the errors validation
finds. A relationship class is two tables, a cardinality, keys and, for many-to-many, a table of pairs. Both are
documented as data and as behaviour on Esri's public pages. Neither needs anything the GeoPackage cannot hold.

## 2. Alternatives considered

### Alternative A — grapy's own tables in the GeoPackage (chosen)

`grapy_topology`, `grapy_topology_rule`, `grapy_topology_error`, `grapy_relationship_class`, registered in
`gpkg_extensions` as a grapy extension. **For:** the file stays a valid GeoPackage, and any reader ignores what it
does not know. **Against:** only grapy understands it, so another program sees the classes but not the rules.

### Alternative B — a sidecar file beside the GeoPackage

**For:** it leaves the GeoPackage untouched. **Against:** a copy of the file loses its topology, and `Copy`,
`Rename` and `Delete` would each need to know about the sidecar. Rejected.

### Alternative C — the OGC GeoPackage Related Tables Extension for relationships

**For:** it is a published OGC extension, so other readers understand it. **Against:** it has no cardinality and
no composite (cascade) semantics. **Taken in part:** relationship classes are written to it as well as to
grapy's table, so another reader sees the pairs. Behaviour is grapy's.

## 3. Counterarguments to the preferred option

Esri's topology validation has properties its pages describe only in words: cracking and clustering within the
tolerance, how dirty areas shrink. A grapy validation will report the documented rules' violations but may not
cluster vertices the way Pro does. Under [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6,
where the documentation does not define the answer the tool is an *equivalent*, marked and counted as one.
ValidateTopology's error *list* is defined by the rules. Its *coordinates after clustering* are not.

## 4. Evidence

The rule list and each rule's meaning are published, on the *Geodatabase topology rules* page and in the
AddRuleToTopology reference. Relationship class cardinality, keys, the many-to-many table and composite deletes are
published on the CreateRelationshipClass page. No benchmark is at stake: a user tool validates the data it was given.

## 5. Decision

1. A topology and a relationship class are rows in grapy's own tables inside the GeoPackage, registered as an
   extension. Relationship pairs are also written to the OGC Related Tables Extension.
2. Rules are evaluated with shapely on the classes' features. The documented rules that are fully defined (must not
   overlap, must not have gaps, must be covered by, must not intersect, must not have dangles, must not self-overlap
   and the rest) are built. A rule whose documented meaning needs a curve, Z or an undefined tolerance behaviour is
   refused by name.
3. **Clustering within the cluster tolerance is an equivalent** (§3): vertices within the tolerance snap to the
   lowest-ranked class's vertex, ties by object id. It is marked as an equivalent and says so in its run.
4. Relationship classes honour cardinality, composite deletes, and `da.SearchRelatedRecords`. Attributed
   relationships keep their attributes in the many-to-many table.
5. **INFERRED, for confirmation:** the table names and columns, writing to both stores, and the clustering rule.

## 6. Consequences

- **State.** In the GeoPackage the tool works on: four grapy tables plus the OGC extension's tables. Nothing in the
  catalogue or the server. A hosted layer imported from a tool's output does not carry the topology (ADR-223 §5.5
  crosses features, not models).
- About forty refused names become buildable. The surface counter shows them as they are built.

## 7. Assumptions this decision rests on

- The documented rule meanings are precise enough to evaluate with shapely's predicates. Each rule is checked
  against its page before it is built.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md), [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md),
the GeoPackage standard (OGC 12-128r18) and its Related Tables Extension (OGC 18-000).

## 9. Revisit triggers

- A user's toolbox needs the topology to survive publishing as a hosted layer. The server would then need the model
  too, which is a different decision.

## 10. Dissent

None recorded.

## 11. Conditions

1. **CreateTopology, AddFeatureClassToTopology, AddRuleToTopology, ValidateTopology and ExportTopologyErrors** are
   built, each tested against a hand-worked violation.
2. **CreateRelationshipClass and TableToRelationshipClass** are built, with cardinality, composite deletes and
   `da.SearchRelatedRecords`, and a relationship written to the OGC Related Tables Extension is read back by its
   rules.
3. **Every remaining topology and relationship name** is built or refused with its reason.
