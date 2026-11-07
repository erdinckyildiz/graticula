# ADR-275 — A scene may be embedded by the sites the operator lists

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07. **The owner's decision** ([ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.4): *an embed address of the viewer alone, framable only by the origins the operator lists*. The address, the setting and the policy are this ADR's. |
| **Supersedes** | — (it narrows ADR-020's *frame-ancestors 'none'* everywhere to everywhere but one address, when a site is named) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ArcGIS Online and Enterprise let a scene be embedded in a website. Every Graticula page answers
`frame-ancestors 'none'` and `X-Frame-Options: DENY`, so nothing can be framed. That is right for every page that
changes something, because a framed page can be clickjacked.

## 2. Alternatives considered

### Alternative A — one embed address, framable by a configured list (chosen; the owner's)

`/studio/scene-embed.html?id=…` is the scene viewer's own page in its embed view: the scene and its tools, without
Studio's bar, the title row or the panel, and without anything that changes the scene. It links to the scene's own
page in a new tab. Its policy is the console's with `frame-ancestors` set to the sites `Graticula:EmbedOrigins`
names. Every other address is unchanged.

### Alternative B — let the scene page itself be framed

**Rejected.** It is an editor, and a framed editor is the clickjacking case the policy exists for.

### Alternative C — `frame-ancestors *`

**Rejected by the owner's decision**: only listed sites.

## 3. Counterarguments to the preferred option

- **A framed page is still this site's.** A signed-in reader's cookie may travel with it, where the browser sends a
  third-party cookie at all. The embed view changes nothing whoever reads it, so the cookie can show a private scene
  to its own reader and do nothing more. A public scene needs no cookie.

## 4. Evidence

- `EmbedOriginsTests` (host):
  - a list is read and normalised, and duplicates are kept once;
  - anything that is not an origin (a path, a wildcard, a userinfo, another scheme, `'self'`) stops the server and
    names it;
  - only `frame-ancestors` is widened, and nothing changes when no site is named.
- `SceneViewerToolsTests.The_embed_address_shows_the_scene_alone_and_only_it_may_be_framed` (console) was run on the
  fixture with `EmbedOrigins=https://maps.example.org` and again without it:
  - the embed view opens the saved scene and offers nothing that changes it;
  - Studio's bar and the panel are not drawn;
  - its link opens the scene's page;
  - its policy names the site, with no `X-Frame-Options`, or, with no site named, is `'none'` and `DENY` like every
    other page;
  - the scene page stays `DENY` throughout.

## 5. Decision

1. `Graticula:EmbedOrigins`: origins, separated by commas or spaces. It is empty by default. An entry that is not an
   http or https origin stops the server at start, naming the entry.
2. `/studio/scene-embed.html` is served from `scene.html` itself, with its body marked `embed`, so the two cannot drift
   (D-130). `scCanEdit()` is false there, whoever reads it.
3. That address alone gets `frame-ancestors <the list>` and no `X-Frame-Options`, which cannot name a list. Every other
   address keeps `'none'` and `DENY`.

## 6. Consequences

- Scene Viewer's *Embed in a website* is built (3d-parity-arcgis.md §H). *Create a web app from the scene* is not.
- **State.** None. It is a setting.

## 7. Assumptions this decision rests on

- An operator lists the sites they trust, and lists them as origins.

## 8. Dependencies

ADR-020 (the console's policy), ADR-094 (the policy's other operator list, style origins), ADR-272 §5.4.

## 9. Conditions

1. **Server › Settings shows the list**, as it shows the style origins, so an operator sees what may frame a scene
   without reading the configuration. *(Open.)*
2. **Studio's scene page offers the embed code** (`<iframe src=…>`) when a site is listed, as Scene Viewer's *Share*
   does. *(Discharged 2026-11-07: *Embed in a website* in the scene's details, for a saved scene whose reader may change
   it, naming the sites; hidden when none is listed. Both ways are in the console test of §4.)*

## 10. Revisit triggers

- A request to embed a web map or an app the same way: the same setting, one more address each.
