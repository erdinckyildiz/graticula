# ADR-285 — Graticula does not provide ArcGIS Pro licences

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-11-08, **by owner decision**: *"Graticula Arcgis Pro lisansı sağlamayacaktır diye madde koyalım"* ("let us put down a clause: Graticula will not provide ArcGIS Pro licences"), after asking whether ArcGIS Pro could take its licence from Graticula, then whether it could be done for testing only, and concluding *"O zaman bunu yapmamalıyız evet"* ("then we should not do this, yes"). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Graticula answers as an ArcGIS portal: ArcGIS Pro signs in to it, browses its content and publishes to it
([ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md) and the Pro work that followed). That raises a question
a reader will ask sooner or later: *can Pro also take its licence from Graticula, as it can from an ArcGIS Enterprise
portal?*

It cannot, and this ADR says so on purpose. A Pro named-user licence comes from Esri: an organisation obtains it from
Esri and loads it into an Esri portal, and Pro checks that it came from Esri. A portal that is not Esri's has nothing
to hand out. The only way Graticula could appear to issue one is by imitating or bypassing Pro's licence check, and
that is circumventing a licence-protection mechanism, whatever it is called.

Without a written line, the question comes back each time Pro integration deepens, and the next answer may be given by
somebody who does not know it was asked.

## 2. Alternatives considered

### Alternative A — Graticula issues Pro licences

**Argument for.** An organisation could run Pro against Graticula alone, with no Esri portal or ArcGIS Online account
in the picture.

**Argument against.** It needs Pro's licence check to be imitated or defeated. That is not an engineering cost to be
weighed; it is outside what this project does
([CLAUDE.md](../../CLAUDE.md) §5: undocumented internals and protected mechanisms are not reproduced).

### Alternative B — Do it only in a test build

**Argument for.** It would let the licensing path be exercised end to end without a second portal.

**Argument against.** A bypass written "for testing" works exactly as well outside a test, and this repository is
public: the moment it is committed it is a working bypass in anyone's hands. The purpose does not change what the code
does. Rejected by the owner in the same conversation.

### Alternative C — Pass on a licence file the organisation already holds

**Argument for.** The organisation has paid for it.

**Argument against.** The file is issued for Esri's portal, and the format in which a portal hands it to Pro is Esri's
internal detail, not a published contract. Relying on it runs into the same line as Alternative A.

### Alternative D — Graticula does not provide Pro licences (chosen)

Pro keeps its licence on Esri's side; Graticula serves content and services.

## 3. Counterarguments to the preferred option

An organisation that wants to leave Esri entirely still needs an Esri licence for every Pro seat, so Graticula does
not remove Esri from that organisation's bill. That is true and it is accepted: Graticula replaces the server and
portal, not the desktop client's licence. An organisation that wants no Esri licence at all needs a different desktop
client (QGIS reads Graticula's services), not a different Graticula.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Pro works against Graticula with its licence taken elsewhere. | Every Pro publishing, arcpy and editing session against the showcase so far was licensed by Esri, not by Graticula. | Pro work under ADR-191 and its successors |
| Pro can be licensed by one portal and work in another. | Pro distinguishes the licensing portal from the active portal; single-use and concurrent licences involve no portal at all. | Esri's public ArcGIS Pro licensing documentation |

## 5. Decision

**Graticula does not provide ArcGIS Pro licences** — not in a release, not in a test build, not behind a setting.
It does not imitate, emulate or bypass Pro's licence check, and it does not pass on licence files issued for Esri's
portals. The supported arrangement is: Pro is licensed by Esri (ArcGIS Online or ArcGIS Enterprise named user, single
use, or concurrent use through ArcGIS License Manager), and Graticula is added to Pro as a portal for content and
services.

The same holds for every other Esri client whose use is licensed by Esri.

## 6. Consequences

**Positive.** The question has a written answer. Nothing in Graticula depends on how Esri's licensing works inside,
so a change on Esri's side cannot break it.

**Negative.** An organisation still pays Esri for each Pro seat. Documentation has to say this plainly, so that
nobody adopts Graticula expecting otherwise.

**Not yet known.** What Pro does when a user picks Graticula as the *licensing* portal has not been observed. If Pro
asks Graticula for a licence, the right answer is a plain refusal Pro can show, not an invented licence. That is
worth measuring when the Pro work is next open; it is not a condition on this decision, which holds either way.

**State.** None. The decision stores nothing.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | None. The decision is a line drawn by the owner, not a bet on a fact that may change. | — |

## 8. Dependencies

**Depends on**: [ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md) (Pro adds Graticula as a portal).

**Depended on by**: none yet.

## 9. Revisit triggers

- Esri publishes a documented, licensed way for a third-party portal to issue Pro licences, and an organisation holds
  the right to use it. Then this is reopened as a new question; it is not reopened by convenience or by a test need.

## 10. Dissent

None. The owner asked twice — once in general, once for testing only — and closed the question themselves.
