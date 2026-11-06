# ADR-272 — The owner's decisions on 3D scope, 2026-11-07

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-11-07, **by the owner**. The owner answered ten questions that the parity comparison ([3d-parity-arcgis.md](../reviews/3d-parity-arcgis.md), *For the owner*) and the two nights' ADRs had left for them. The owner asked to be given them in order: *"Benden beklediklerini sırası ile sorarsan cevaplarım"* ("ask me in order what you expect from me and I'll answer"). Each answer below is a choice the owner selected, recorded as given. |
| **Supersedes** | v1-scope's *"3D Tiles stays out"* (Scenes row); the open *Owner* rows of 3d-parity-arcgis.md listed below. |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Two overnight sessions brought Graticula's 3D toward ArcGIS Online and Enterprise ([ADR-269](ADR-269-scenes-web-scenes-scene-packages-and-generated-i3s.md),
[ADR-270](ADR-270-scene-viewer-to-arcgis-enterprise-parity.md), [ADR-271](ADR-271-the-portal-search-answers-a-box.md)).
What remained was the owner's to decide: scope, cost and security. This ADR records the answers so that nothing is
decided twice and nothing is decided by inference.

## 2. Alternatives considered

Each question was put with its options and a recommendation. The options are in §5, beside the answer.

## 3. Counterarguments to the preferred option

Three answers go further than the recommendation put to the owner, and the record says so:

- **3D Tiles in scope.** The recommendation was to keep it out until a user asked.
- **3D editing now.** The recommendation was to start it after the 2D editor's open debts.
- **Partial cache updates.** The recommendation was scheduled full rebuilds first.

The owner chose the larger scope on all three. Each needs an ADR of its own before it is built.

## 4. Evidence

The owner's selections, 2026-11-07, in this session.

## 5. Decision

1. **Push.** `scenes` is pushed to origin and CI is run on it. `main` is not touched and no PR is opened.
2. **The Maps SDK is raised** above 4.29, as its own change, with the console suite run against the new version. Volume
   measurement, viewshed and dimensions follow, along with a slide's legend, pop-up and slice. **This is the first of
   the large items** (answer 9).
3. **Packages above the document limit live on the state volume.** The store keeps a reference and a hash. This is
   D-386's open half; how it backs up and how several nodes see it is the follow-up ADR's to settle.
4. **Embedding.** A viewer-only embed address may be framed by the origins the operator lists. Nothing else changes
   from `frame-ancestors 'none'`.
5. **3D Tiles, `.3tz` and Gaussian splat are taken into scope.** v1-scope's Scenes row is amended.
6. **3D editing starts now**: editing 3D object and point layers in the scene, and creating an empty 3D object layer.
7. **Scene layer caches get scheduled full rebuilds and partial updates** of what changed.
8. **The `INFERRED` readings are confirmed**: ADR-270 §5.1–5.17 (what *"aynı arcgis enterprise'da olduğu gibi"* takes
   in) and ADR-271 §5.2 and §7 (an item without an extent is not found by a box; the box is in degrees).
9. **Order.** The SDK upgrade comes first. The order of the rest is not decided here.
10. **Pro validations** (ADR-269 conditions 1–3, Pro half; ADR-270 condition 2) stay the owner's, to be done when the
    owner can. They stay open until then.

## 6. Consequences

- New ADRs are owed, one per item: SDK upgrade, large packages, embed address, 3D Tiles, 3D editing, cache rebuilds.
  None of them is written by this one.
- **State.** None of its own. Item 3 will put package files on the state volume, which is that ADR's to describe.

## 7. Assumptions this decision rests on

None of its own; each follow-up ADR states its own.

## 8. Dependencies

ADR-269, ADR-270, ADR-271, D-386.

## 9. Conditions

None. The work is owed through the follow-up ADRs, not through conditions here.

## 10. Revisit triggers

The owner changes any answer.

## 11. Dissent

The recommendations that were not taken (§3) are kept there.
