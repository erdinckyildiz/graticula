# ADR-082 — Offline sync is not in v1

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-23, by the owner, asked after the third ArcGIS review (V-61): *"Şimdilik yok, kayda geç"* — not for now, put it on record. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ArcGIS takes a feature service offline through three operations on the service —
`createReplica`, `synchronizeReplica` and `unRegisterReplica` — and one on the layer,
`extractChanges`. Field Maps' offline map areas and ArcGIS Pro's *Download Map* are built on
them, and an organisation with a field crew asks for them in its first week. The second
ArcGIS review found them missing (V-29) and the third found them missing again (V-61), and no
document said whether that was a decision or a gap. This one says it is a decision.

The server advertises what is true: `supportsDisconnectedEditing` is `false` on every layer and
`Sync` is never in a capabilities string, so a client does not offer an offline area it cannot
make. Nothing here changes a response.

## 2. Alternatives considered

### Alternative A — build it now

**Argument for.** It is the feature that decides adoption for field work, and since
[ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md) a hosted layer can keep its history,
which is most of what a change set needs.

**Argument against.** A replica is a contract with a device that may not come back for weeks:
a registry of replicas and their generations, change extraction since a generation, conflict
rules for an edit made offline against a row edited online, attachments in both directions, and
a mobile geodatabase or JSON package format written the way the clients read it. None of it can
be tested without the clients on a device, which this project does not have today (ADR-076
condition 4 is waiting on the same thing). It would take weeks and would be the least tested
surface in the server.

### Alternative B — design it first, build it later

**Argument for.** A design written now would find what v1's data model makes hard before
anybody depends on it.

**Argument against.** The design's hard questions — conflict rules and the package format — are
questions about client behaviour that only a device answers, so a design written now would be
written against guesses and rewritten when the device arrives.

### Alternative C — not in v1, recorded *(chosen, by the owner)*

**Argument for.** v1 stays the size it is, the capability report stays honest, and the decision
is visible to whoever asks next instead of being rediscovered as a missing route.

**Argument against.** An organisation whose field crew works offline cannot adopt v1 for that
work, and will not.

## 3. Counterarguments to the preferred option

The strongest one is §2A's: this is not a nice-to-have for the people it affects, it is the
whole job. That is true and it is accepted — v1 is not for an offline field crew, and saying so
is better than advertising a surface that fails on a device in a place with no signal.

## 4. Evidence

- `supportsDisconnectedEditing = false` in `FeatureServerMetadataWriter`, and no `Sync`
  capability anywhere in `/src`.
- V-29 (second review) and V-61 (third review), both measuring the same absence.

## 5. Decision

**Offline sync — `createReplica`, `synchronizeReplica`, `unRegisterReplica`,
`extractChanges` — is not in v1.** It is added to [v1-scope](../v1-scope.md) §3d beside the
other deferred protocol surface, and the layer document goes on saying
`supportsDisconnectedEditing: false`.

## 6. Consequences

- Field Maps offline areas and Pro's *Download Map* do not work against this server, and the
  server says so in the documents those clients read rather than at the device.
- ADR-078's history is not shaped for replicas and does not have to be yet.

**State.** None: the decision stores nothing in the catalogue and holds nothing at runtime.

## 7. Assumptions this decision rests on

- That the first organisations to use v1 publish and read rather than collect offline.

## 8. Dependencies

- [v1-scope.md](../v1-scope.md) §3d.
- [ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md), which a later design would build on.

## 9. Revisit triggers

- An organisation adopting the server needs offline collection.
- A device with Field Maps is available to test against — the same trigger as ADR-076 condition 4.

## 10. Dissent

None recorded. The reviewer who raised V-61 called it the first question a field organisation
asks, and that stands as the cost of this decision rather than as an objection to it.
