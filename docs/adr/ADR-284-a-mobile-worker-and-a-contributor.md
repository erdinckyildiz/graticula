# ADR-284 — Contributor and Mobile Worker, as ArcGIS has them: one ceiling, two names

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-11-08, **by owner decision**: *"ArcGIS'e birebir uy"* ("match ArcGIS exactly"), chosen from three options after Esri's own description of the two types was read (§4). It replaces a first proposal the owner had accepted an hour earlier (*"Yap evet"*) — a Contributor that makes content — which rested on a premise the evidence then refuted, and which was never committed. |
| **Supersedes** | — It amends [ADR-018](ADR-018-authorization-and-roles.md) §3a's list of user types. |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ADR-018 §3a gave this server four user types — `viewer`, `editor`, `creator`, `unrestricted` — each a ceiling on what
any role may confer, each containing the one below. `editor` holds one privilege, `features:user:edit`: it edits
features and makes nothing, not even a web map. It was reported to ArcGIS clients as `editorUT`.

The day ArcGIS Field Maps first collected into this server (ADR-077 condition 5), the owner asked whether Graticula
has ArcGIS's **Mobile Worker**, and then whether an editor — *someone who may also edit maps* — and a Mobile Worker,
*who only collects in the field*, should not be two types. The first answer given here said ArcGIS had widened its
Editor into a Contributor that makes content, and proposed a fifth type to match. **That was wrong**, and the rendered
review of the screens is what caught it (§4).

## 2. Alternatives considered

### Alternative A — one ceiling, two ArcGIS names *(chosen)*

`editor` is ArcGIS's Contributor (`editorUT`); a new `fieldworker` is its Mobile Worker (`fieldWorkerUT`), with the
same ceiling. **For:** it is what ArcGIS is: the two differ by the apps a licence includes, not by what either may do
with data. An imported organisation keeps each member's type; a client is told the type the member was given.
**Against:** two types that grant the same thing; an administrator may look for a difference that is not there,
which the screens answer in words (§5.4).

### Alternative B — one type only, named Mobile Worker

**Against:** a portal client inviting a Contributor (`editorUT`) would get a Mobile Worker back, and an organisation
moved from ArcGIS would lose a distinction its administrators made.

### Alternative C — a Contributor that makes and shares content but publishes nothing

The first proposal, built and tested before §4 was read. **Against:** ArcGIS's Contributor *"cannot analyze, create,
or share items or data"*; a client told `editorUT` about such a member would be told something false. What it was
for — *makes maps, publishes no layers* — is in ArcGIS a **role** on a Creator, not a user type, and is one here too:
a Creator holding the built-in `user` role, which has `content:create` and no publishing.

## 3. Counterarguments to the preferred option

*A type that changes nothing is clutter.* It changes what a client and an import see, which is the only thing the
ArcGIS name is for; and the console says so beside it. *Field apps are where Mobile Worker differs, and this server
does not gate apps* — true: a Contributor collects in Field Maps here exactly as a Mobile Worker does, since both hold
`features:user:edit`, and in ArcGIS the Contributor's app list decides that instead.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS's Contributor and Mobile Worker make and share nothing | *"Contributors cannot analyze, create, or share items or data."* *"Mobile Workers cannot analyze, create, or share items or data."* | ArcGIS Online help, *User types*, doc.arcgis.com, read 2026-11-08 (the Enterprise page refused the connection twice) |
| They differ by apps | *"Mobile Workers … have access to a selection of apps … ideal for users who primarily interact with ArcGIS content through field apps"* | same page |
| A real Enterprise names `editorUT` *Contributor* and `fieldWorkerUT` *Mobile Worker* | `portals/self/userLicenseTypes` | Aecon's ArcGIS Enterprise 2025.2 QA portal and 2026.1 production portal, 2026-11-08 |
| …and gives both the same role | 79 Contributors and 248 Mobile Workers, every one in the role *Data Editor* (`features:user:edit` and viewing) | production portal, read only, aggregates only |
| Neither can be created without a licence seat | eight `createUser` attempts on QA refused, `maxUsers: 0` for both types | QA portal, nothing left behind |
| The rendered review caught the wrong premise | ux-designer review of the first proposal's screens, finding 2 | 2026-11-08 |

## 5. Decision

1. **`editor` is ArcGIS's Contributor**, reported as `editorUT` as it always was; called *Contributor* on every screen.
   Its code and ceiling (`features:user:edit`) do not change.
2. **`fieldworker` is ArcGIS's Mobile Worker**, reported as `fieldWorkerUT`, with the same ceiling. The ladder holds:
   Viewer ⊆ Contributor = Mobile Worker ⊆ Creator ⊆ Unrestricted, in ArcGIS's order.
3. **ArcGIS's words in, this server's types out.** A portal invitation maps `editorUT`/`contributor`/`editor` to
   `editor`, `fieldWorkerUT`/`fieldworker` to `fieldworker`, a `GISProfessional…` type to `creator`, anything else to
   `creator` as before.
4. **The screens say the two are the same**, and name the type, never its code — including the sentence that says
   what changing a member's type does, which printed the code until the review found it.
5. **Migration 135 inserts the one row** (expand). Migration 3 is frozen to the four types it has always written.

## 6. Consequences

**Positive.** Every ArcGIS type a field crew is given has a home here, reported back as itself. Nothing anybody holds
changed.

**Negative.** Two names for one ceiling. A webhook's `userLicenseTypeUpdatedTo` still carries this server's code, not
ArcGIS's id.

**State.** One row in the shared `user_type` table (migration 135), which every node reads; a member's type is the
existing `principal.user_type`. Nothing held at runtime beyond the ceilings compiled into the build.

## 7. Conditions

1. **The Enterprise *User types* page is read** for the same two sentences (the ArcGIS Online page is the evidence
   today; the Enterprise one refused the connection), and §4 cites it.
