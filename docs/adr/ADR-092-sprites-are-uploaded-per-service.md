# ADR-092 — Sprites are uploaded per service, and a style's icons are checked against them

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-29, by owner decision. The owner asked for the vector tile face's remaining gaps to be closed one at a time; this is the fourth item on that list. The shape below — one sheet per service, a separate table, the checks in both directions — is the session's design, not the owner's words, and is marked **INFERRED** where it matters (§2, §5). |
| **Depends on** | [ADR-027](ADR-027-glyphs-and-sprites.md), [ADR-028](ADR-028-style-documents.md) |
| **Discharges** | [ADR-027](ADR-027-glyphs-and-sprites.md) condition 5; [ADR-028](ADR-028-style-documents.md) condition 4 |
| **Supersedes** | — |
| **Superseded by** | — |

> **Amended 2026-09-29 by [ADR-094](ADR-094-several-styles-and-allowed-origins.md).** A service may carry
> several styles now, and the sheet is checked against **all of them**: a replacement or a removal is refused
> with 409 when any stored style names an icon it would take away by literal, and the refusal names the
> styles. The read-back's `styleUses` covers every style.

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A vector tile service could not draw a point as an icon.

- **The sprite sheet was an empty stub.** `sprite.json` answered `{}` and `sprite.png` a one-pixel
  transparent picture, for every service, since [ADR-027](ADR-027-glyphs-and-sprites.md) §5. It existed so
  that a client probing it got an answer instead of a 404.
- **So the style validator refused every `icon-image`.** [ADR-028](ADR-028-style-documents.md) added the
  refusal on the day style documents arrived, because an icon from an empty sheet draws nothing and reports
  nothing. That was the right answer to an empty sheet, and it made a valid style unstorable for a reason
  that was ours.
- **Both ADRs said when this ends.** ADR-027 condition 5 asked that the sheet stop being empty or stop being
  advertised. ADR-028 condition 4 asked that the `icon-image` refusal be *deleted, not relaxed*, when sprites
  can be uploaded.

Points drawn as circles and text are most of what a map needs. The rest — a school, a hospital, a
manhole — is an icon, and a publisher moving from ArcGIS Enterprise expects to draw one.

## 2. Alternatives considered

### Alternative A — one uploaded sheet per service, in its own table *(chosen)*

- A service carries up to two sheets, at pixel ratio 1 and 2, each an index (`sprite.json`) and a picture
  (`sprite.png`), uploaded together.
- They are stored in a new table, `service_sprite`, keyed by service and ratio.
- The style and the sheet check each other: a style naming an icon the sheet lacks is refused, and a sheet
  that would take away an icon the stored style names is refused.

**Argument for.**
- It is the unit a style already has. [ADR-028](ADR-028-style-documents.md) stores one style per service, and
  a MapLibre style names one `sprite`.
- Nothing is fetched at runtime, so Q-15's air-gap rule holds.
- A separate table keeps eight megabytes of picture off the `service` row, which every face reads on every
  request.

**Argument against.**
- Two services that want the same icons upload the same files twice.
- A sheet is made by a tool outside this product. An operator with a folder of SVG files cannot use them
  directly.

### Alternative B — a server-wide icon library, shared by every service

**Argument for.** One upload serves every service, and a shared set of icons is how an organisation keeps its
maps consistent.

**Argument against.**
- A style addresses its sheet relative to the service (`../sprites/sprite`), and a root-relative URL is
  refused by the style validator on purpose: it would cross a sharing boundary (ADR-028 §5). A shared sheet
  needs an address outside every service and a sharing rule of its own.
- Changing one shared icon changes every service's map at once, and no single publisher owns that decision.
- **INFERRED, listed for confirmation:** the owner asked for the gap to be closed, not for a library. This
  can be added later beside per-service sheets without changing them.

### Alternative C — upload icons one at a time, and build the sheet on the server

**Argument for.** An operator uploads `school.png` and names it, with no sprite tool.

**Argument against.**
- It needs an image decoder and an image encoder in the server process. ADR-027 §2 kept a font parser out
  for the same reason: an attacker-facing binary parser is a large attack surface, and this one would run on
  every upload.
- It needs a packing algorithm and a high-density variant of each icon.
- The tools that make sheets exist and are what style authors already use.

### Alternative D — stop advertising the sheet, and keep refusing icons

**Argument for.** No new state and no new code.

**Argument against.** It makes the refusal permanent, which is what ADR-028 condition 4 exists to prevent,
and it leaves the gap the owner asked to close.

## 3. Counterarguments to the preferred option

- **The literal check is only half a check.** A literal `icon-image` is checked against the sheet. An
  expression (`["get", "kind"]`, or a legacy `{kind}` token) is accepted and not resolved, because which names
  it produces depends on the data. A style whose expression names a missing icon still draws nothing and
  reports nothing. Resolving it would mean reading the features at write time.
- **The style is checked against the 1x sheet only.** A @2x sheet missing an icon the 1x sheet has passes the
  style check. A high-density screen then draws nothing for that icon. The upload check is applied to both
  ratios, so an icon the stored style names cannot be missing from either; an icon added to a style later is
  checked against 1x alone.
- **A sheet cannot be removed while the style names one of its icons.** An operator who wants both gone must
  remove the style first. That is one extra step, and the refusal says which.
- **Browsers that fetched the old empty stub may keep it.** It was served `immutable` for a year. A browser
  that cached `sprite.png` from a service before this release can keep drawing the empty sheet until its cache
  is cleared. Every answer is now `no-cache` with an ETag, so this happens once, not again.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A picture's size is read from its header without decoding it | `SpriteSheetTests`: signature, a first chunk that is not `IHDR`, a file too short to hold a header, sides of 0 and past 4096, a file over 8 MB | this change |
| An icon outside the picture is refused and named | `SpriteSheetTests`: past the right edge and the bottom edge, a rectangle whose sum overflows an integer, non-integer and negative numbers, a duplicate name, too many icons, a `pixelRatio` that does not match the sheet | this change |
| The blanket refusal is gone and the check that replaced it works | `StyleDocumentTests`: no sheet refuses any `icon-image`; a literal (string or `["literal", …]`) must be in the sheet, case-sensitively; an expression or a `{token}` is accepted when there is a sheet | this change |
| A sheet cannot take away an icon the stored style names | `ASpriteSheetMayNotTakeAwayAStylesIconTests`; `SpriteConformanceTests.The_sheet_is_held_while_the_stored_style_names_one_of_its_icons` | this change |
| A stored style whose sheet is gone is not served | `AStyleThatOutlivesItsLayersIsNotServedTests.A_style_drawing_an_icon_with_no_sheet_behind_it_is_not_served` | this change |
| The store round-trips a sheet and removes it whole | `PostgresAdminCatalogTests.A_sprite_sheet_survives_the_round_trip_and_is_removed_whole` | this change |
| Upload, serve, ETag and 304, and the @2x fallback work over HTTP | `SpriteConformanceTests` | this change. **Written, compiled, and not yet run against a live server when this ADR was written** — the platform-store and conformance suites need a database and a running server the authoring session did not have |
| A MapLibre client asks for `sprite@2x` on a high-density screen and reads `pixelRatio` from the index | [MapLibre style specification, `sprite`](https://maplibre.org/maplibre-style-spec/sprite/) | public specification |

## 5. Decision

A vector tile service may carry an uploaded sprite sheet, at pixel ratio 1 and 2, and a style's icons are
checked against it.

- **Storage.** Migration 60 adds `service_sprite (service_id, pixel_ratio, index_json, image, updated_at)`,
  one row per service and ratio, removed with its service. The index is text, stored as it was sent. The
  table repeats the bounds: an index of 1 MB, a picture of 8 MB, a ratio of 1 or 2.
- **Checks on upload** (`SpriteSheet`).
  - The picture is a PNG: the signature, then a 13-byte `IHDR`. Its width and height are read from bytes 16
    to 23 and must each be between 1 and 4096. It is never decoded.
  - The index is a JSON object of at most 10,000 icons. Each name is 1 to 256 characters and appears once.
    Each value has whole-number `x` and `y` of at least 0 and `width` and `height` of at least 1, and the
    rectangle lies inside the picture. A `pixelRatio`, when present, equals the sheet's ratio. Other members
    are kept and not checked.
- **Admin API**, behind `content:publishFeatures` and the ADR-075 manage check, like the style:
  - `PUT /admin/services/{name}/sprite?ratio=1|2` takes `multipart/form-data` with the parts `index` and
    `image`. The ratio defaults to 1. 400 for a file that fails a check; 413 past the bound; **409** for a
    @2x sheet with no 1x sheet, and for a sheet that lacks an icon the stored style names by literal.
  - `GET /admin/services/{name}/sprite` says, per ratio, how many icons, the picture's size and when it was
    uploaded, and which icons the stored style names.
  - `DELETE /admin/services/{name}/sprite` removes both ratios. 409 while the stored style names an icon.
  - *(Amended 2026-09-29 by [ADR-094](ADR-094-several-styles-and-allowed-origins.md): "the stored style" is
    every stored style of the service, the default and the named ones.)*
- **Serving.** `sprite.json` and `sprite.png` serve the 1x sheet; `sprite@2x.json` and `sprite@2x.png` serve
  the @2x sheet, or the 1x one when there is no @2x sheet. No sheet stored is the empty sheet, as before.
  Every answer carries an ETag from its bytes, answers `If-None-Match` with 304, and is `no-cache` — public
  only for an anonymous caller of a public service, the rule the tiles follow.
- **The style.** The blanket `icon-image` refusal is **deleted**. In its place: an `icon-image` on a service
  with no sheet is refused, and says to upload one; a literal icon name not in the 1x sheet is refused, and
  names it. An expression is accepted when there is a sheet. The same check runs where the style is served
  (ADR-028 condition 3), so a style whose sheet went away by some other door gives way to the generated one.
- **INFERRED, listed for confirmation:** 409 rather than 400 for the two consistency refusals; the 4096-pixel
  bound; that the style is checked against the 1x sheet only; that a sheet read while the platform store is
  unreachable fails with 503 rather than serving the empty sheet.

## 6. Consequences

**Positive.**
- A style can draw icons. ADR-027 condition 5 and ADR-028 condition 4 are discharged.
- The refusal a valid style met for a reason that was ours is gone, and the silent blank it prevented is
  still prevented, now by a check that names the missing icon.
- No image is decoded in the server process.

**Negative.**
- Sheets are per service. Shared icons are uploaded once per service.
- An `icon-image` expression is not checked (§3).
- A browser holding the old immutable empty sheet may keep it until its cache is cleared (§3).
- A sheet is made with a tool outside this product.
- The per-layer symbology document ([ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md)) still
  refuses a picture marker. A per-layer document has no sheet of its own, and this ADR does not give it one.

**Ports created.** None.

**State.** *Catalogue*: `service_sprite`, one row per service and pixel ratio, holding the index as text and
the picture as bytes. *Runtime*: none. The sprite routes read the store on each request, as the style route
does; nothing is cached in the process.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Publishers can produce a sprite sheet with an existing tool | Unvalidated; no publisher has been asked |
| — | 8 MB and 4096 pixels are above any real sheet | Reasoned from sheet sizes in public styles, not measured on this product's users |

## 8. Dependencies

**Depends on:**
- [ADR-027](ADR-027-glyphs-and-sprites.md), which put the sprite routes in place and left them empty.
- [ADR-028](ADR-028-style-documents.md), whose validator this changes and whose serve-time check this extends.
- [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md), for who may change a service.
- [ADR-068](ADR-068-responses-are-compressed.md), for why the ETag is weak.

**Depended on by:** —

## 9. Revisit triggers

- Somebody asks to share one set of icons across services. Alternative B becomes the work.
- Somebody asks to upload a single icon, or an SVG. Alternative C comes back, with its parser.
- A style whose `icon-image` expression names a missing icon is reported as a blank map. §3's first
  counterargument has then cost somebody something.
- A per-layer picture marker is asked for. ADR-052's refusal needs a sheet to point at.

## 10. Dissent

None recorded.
