# Graticula Hybrid — screen inventory and design coverage

Audited 2026-10-16 against `origin/main` at `4d967318`. Source of every row: `src/Graticula.Host/wwwroot/`
(`console.js`, `index.html`, `webmap.*`, `view.*`, `map.*`) and the server-rendered pages in `src/Graticula.Host/`.
Stitch projects: **Graticula Hybrid — Studio & Server redesign** (`projects/1743378018328409795`) and **Graticula Hybrid — corrections** (`projects/15636992451718109493`, five screens redrawn because edits in the first project did not apply),
design system `assets/3901723720104477982`.

Status column: `—` not designed · `draft` generated, awaiting owner review · `approved` · `exception` (deliberately not designed, reason given).

## 0. Information architecture

```text
/studio/  (signed in)                                   /server/  (admin:manageServer)
TOP NAV (Portal-like)                                   TOP NAV — Services · Site · Security · Logs (ADR-187)
├─ My content   #/content[/scope]                       ├─ Services      #/services[/folder]
│   scopes as tab strip under the title; left panel: Folders · Type          │   left panel: Manage services · Publish · Folders
│   ├─ Item       #/service/<f>/<n>?tab=…               │   ├─ Service   #/service/<f>/<n>[?section=…]
│   │   Overview · Data · Visualization · Settings      │   │   Capabilities · Limits · Layers
│   └─ Web map    #/map/<id>                            │   └─ Publish   #/publish   (action, not a section)
├─ Map          → /studio/webmap.html (link out)        ├─ Data sources  #/sources
├─ Anonymous view #/anonymous                           ├─ Members       #/members
├─ Groups       #/groups                                ├─ Settings      General #/settings · Roles #/roles
│   └─ Group    #/group/<n>/<tab>                       │                Sign-in #/signin · Apps #/apps
│       Overview · Content · Members · Settings (+Add)  └─ Operations    Status #/operations · Logs #/logs
└─ Domains      #/domains
Header right (both): Server|Studio switch (only if both allowed) · name + roles · Sign out
```

Top-nav labels are today's Studio rows (`SURFACES.studio`, console.js:2161) unchanged. The reference's
Home, Gallery, Organization, Favorites, Scene, bell, app grid and global search have no screen behind them
and are not designed.

## 1. Shared and standalone

| ID | Screen | Route | States to design | Stitch | Status |
|---|---|---|---|---|---|
| SH-01 | Sign-in | any, no token | default · error · busy · with providers · cookie-only note | `8366c311e8bf4eb096f17344cdeb13cc` (error state) | draft |
| SH-02 | Set your own password | after sign-in, `mustChangePassword` | default · error | | — |
| SH-03 | REST sign-in | `/rest/login` (RestDirectory.cs) | default · error · message | | — |
| SH-04 | OAuth authorize / approval / error | `/sharing/rest/oauth2/*` | sign-in · code shown · could not start | | — |
| SH-05 | REST Services Directory | `/rest/services?f=html` | folder · service · layer | | open (Q4) |
| SH-06 | Query page | `…/query?f=html` | form · results | | open (Q4) |
| SH-07 | Shared components | — | toast good/bad · row menu · bottom sheet (<760px) · confirm dialog · prompt dialog · error row "Could not load X" · pager | | — |

## 2. Studio

| ID | Screen | Route | States / dialogs | Stitch | Status |
|---|---|---|---|---|---|
| ST-01 | My content | `#/content[/all\|mine\|group\|organization\|public\|administrative]` | loading · nothing at all · empty scope · empty folder · no match · load error · N selected (bulk bar) · non-manager row | corrections project `15636992451718109493` · `d4ece2c6f98642aca9b61aa50d959ce5` (Portal tab strip, map previews; earlier Balanced `c55b6a8f…` superseded) · superseded: `54e4ce2f…` A (scopes in the panel), `8a7e7024…` B, `61c391ed…` Wide and `1bba9f0e…` Compact (invented storage quota / collapse) | draft |
| ST-02 | New item | dialog from ST-01 | choose · feature-layer kind · Define your own · Upload a file · Publish a table · Imagery · reading layers · choose layers · publishing · refused · created | `c28682b0d67d4cc59ff26472a32e9b86` (step 1) | draft |
| ST-03 | Share | dialog | level · group sharing · read-only | `7c0de9d0cd2e4b6d9dafff38d198e81a` (level) | draft |
| ST-04 | Folder dialog | dialog | new · rename · delete (blocked) · move · bulk move · bulk share | | — |
| ST-05 | Item › Overview | `#/service/…?tab=overview` | owner · reader (no Settings tab) · stopped · image service · not in your content | `d169421ae44b48e5a6ca655d95ebcd2f` | draft |
| ST-06 | Export data | dialog | choose · job running · cancelled · done | `b922e58553214379986a88ff559f3140` | draft |
| ST-07 | Update data | dialog | add · upsert (match on) · replace (danger) · dry-run mapping · done | `cf4a77d055e040ca83f4510642900e1a` | draft |
| ST-08 | Create view / Change filter | dialog | default | | — |
| ST-09 | Change owner | dialog | search · chosen | | — |
| ST-10 | Item › Data › Table | `?tab=data&view=table&layer=` | rows · empty · sorted · paging | `4d23264e736f443cb4b44b1ad7d924e9` | draft |
| ST-11 | Item › Data › Fields | `?tab=data&view=fields` | editor · values editor · add/remove columns · registered (not alterable) | `c6af4ec09334431cb7b937db0c2f269d` | draft |
| ST-12 | Item › Data › History | `?tab=data&view=history` | on · off (link to Settings) · not hosted · versions | `f54b83ec847546c6804ab221f28adf8a` | draft |
| ST-13 | Item › Visualization | `?tab=visualization&mode=features\|tiles` | features · tiles · drawing · error | `174e3304bedb43b88c554e6a151a16aa` | draft |
| ST-14 | Item › Visualization › Styles | `?tab=visualization&panel=style` | single · unique · class breaks · generated (empty) · override stored · read-only band | `d29588ff29cd41ce84bb8bf81c920896` | draft |
| ST-15 | Item › Settings › General | `?tab=settings&section=general` | sharing · OGC/INSPIRE · protected · not protected | `78648f56998641939661a78005b247e7` | draft |
| ST-16 | Item › Settings › Feature layer | `?section=feature` | editing (ceiling greyed) · per-layer blocks · read-only | `4f8d45be03b945fe9a63ee09efb8ef49` (duplicate `ea6d96bf…` not used) | draft |
| ST-17 | Item › Settings › Tile layer | `?section=tiles` | status · pre-build · building · offline · advanced · untiled | `bc2d2c9e86b948e2915ae7b028025445` | draft |
| ST-18 | Image item › Settings | `?section=imagery\|images\|classes\|general` | display · images · classes · download/elevation | | — |
| ST-19 | Web map item | `#/map/<id>` | owner · reader · no layers · error · map Share dialog | `fa845a9774284713a9997871c68d9e59` | draft |
| ST-20 | Anonymous view | `#/anonymous` | not run · results | `87c8031ee00642d6abd9f2fe82f15d13` | draft |
| ST-21 | Groups | `#/groups` | list · new group form · none · no match | `6785d62f6a664e91b15f9572b24df39d` (Portal filters; earlier `4933ba00…` not used) | draft |
| ST-22 | Group › Overview | `#/group/<n>/overview` | member · manager · owner | `41da328bb11b4d49808851a99bada908` | draft |
| ST-23 | Group › Content | `/content` | list · empty · manager | | — |
| ST-24 | Group › Add items | `/add` | picking · already in group | | — |
| ST-25 | Group › Members | `/members` | list · add-member picker | | — |
| ST-26 | Group › Settings | `/settings` | manager · non-manager hint · unsaved | | — |
| ST-27 | Domains | `#/domains` | list · inline editor · empty · in-use | `a85b9d1967c54c148ec8b890ed88bf8d` | draft |
| ST-28 | Map Viewer | `/studio/webmap.html?id=` | Layers · Search · Measure · Bookmarks · Print · Map; identify/edit · table · time bar · sketch · signed-out read-only · not found | `0c3cf40f6a1844688ae9956517561471` | draft |
| ST-29 | Layer viewer | `/studio/view.html` | features · map image | | — |
| ST-30 | SDK viewer | `/studio/map.html` | 2D · scene | | exception? (test page, Q4) |

## 3. Server

| ID | Screen | Route | States / dialogs | Stitch | Status |
|---|---|---|---|---|---|
| SV-01 | Services | `#/services[/<folder>]` | list · search across folders · empty folder · no match · health/resources · start/stop · delete confirm · New folder | `c9e0aae219b24fa2a51ade1f3b4322ff` (four tabs; five-tab `3e82f2be…` superseded) | draft |
| SV-02 | Group layers drawer | from SV-01 | create · list · delete blocked | | — |
| SV-03 | Service page | `#/service/<f>/<n>?section=capabilities\|limits\|layers` | feature service · image service · system service (timeouts/workers) · layer highlighted | General `bdf61745239f4bb0aa3cafa1b38a886e` · Parameters corrections project `15636992451718109493` · `972dbf7cbee34bf8a10c3d3286295153` · Capabilities corrections project `15636992451718109493` · `b66c2c436f44466990a71131563d34ee` | draft |
| SV-04 | Publish | `#/publish` | composer (Contents · Preview · Databases) · map properties · symbol · rename · Publish dialog | `896d5461135d4edd9f11f271d7c1990b` | draft |
| SV-05 | Data sources | `#/sources` | list · probe result · paused · none · connection dialog (5 kinds) · pause dialog · remove confirm | `ac5c90d9d17044b0a14b392ec8d64eef` (four tabs; old five-tab `87e26d57…` not used) | draft |
| SV-06 | Members | `#/members` | list · new member form · issued password · set password · remove-member transfer panel | `8be1b04da9ae44a886662ffdb40c794e` (Security › Members, filters; `973dcbc4…` not used) | draft |
| SV-07 | Settings › General | `#/settings` | query results · basemap gallery · map ground · style sources · geometry service | `48fe90da2dd84608b8d1254a9ed835fd` (edited to Site › General) | draft |
| SV-08 | Settings › Roles | `#/roles` | list + editor · administrator (locked) · new role | corrections project `15636992451718109493` · `244dce06cf074fbb9aee2057d7af4502` (real 18 privileges) | draft |
| SV-09 | Settings › Sign-in | `#/signin` | list · OIDC / SAML / LDAP form · groups mapping · check result | corrections project `15636992451718109493` · `6175c392aa824f55b875f8980c601cd1` | draft |
| SV-10 | Settings › Apps | `#/apps` | list · register form · empty | `c9ba2401d2e64b7588df667f627beb78` | draft |
| SV-11 | Operations › Status | `#/operations` | store · runtime · caches · datastore · route governance · empty services · every route · store unreachable | `d4315127bab14bf8ac0e780143abe922` (edited to Site › Status) | draft |
| SV-12 | Operations › Logs | `#/logs` | Administration · Requests · Studio viewer · Server warnings · expanded row · empty | `f77f91686fd64c43a2698dbef0b529bc` (edited: four tabs, sources in left panel) | draft |
| SV-13 | Legacy layer page | `#/layer/<n>` fallback | — | — | exception: only reached when a redirect cannot place the layer |

## 4. Gaps found by the audit (design input, not changes)

- **No dark theme exists** (no `prefers-color-scheme`/`data-theme` anywhere); the design system defines one.
- Native `prompt()` (New folder, New role) and ~20 `confirm()` calls: SH-07 designs in-app replacements.
- Three layer choosers on the item (head select, hidden `#visLayer`, Styles rail list) — ADR-102 §5.1 says one.
- Dead hooks: `#serviceOps`/`#serviceSpend`, `#serviceAddress`, `#capSharing`, `data-group-reach`, `tr[data-pick]`.
- `EveryScreenTests.cs:43-47` lists 6 screens; the router has 20.
- Map Viewer and `view.html` carry their own token sets (`--accent #0b6157`), not console.css's.

## 5. Known deviations — the Stitch draft is wrong here, implement the text instead

Stitch applied an edit only when its call returned in time. Each item below was sent as an edit and verified (HTML read back, 2026-10-16) as **not** applied, so the implementation follows this list, not the picture.

- **Logo** — one mark everywhere: navy #171b49 square, white 3×3 grid. Overview `d169421a…`, Share `7c0de9d0…`, Export `b922e585…` and New item `c28682b0…` drew a globe.
- **Header** — no avatar or initials circle (Styles `d29588ff…` drew "EY").
- **Data › Table `4d23264e…`** — dates are plain Ink text, not teal.
- **Settings › General `78648f56…`** — the delete-protection checkbox uses the teal accent, not the browser's blue.
- **New item `c28682b0…`** — the dialog is right; its backdrop shows a superseded My content with emoji folders.
- **Apps `c9ba2401…`** — no "OAuth 2.0 PKCE supported" footer.
- **Server left panels** — never add "Documentation", "Server Settings", "Operations API" or "Multi-Cluster Access" blocks; Stitch added them unasked (removed where verified).
- **Light/dark** — one dark specimen was requested (Services); every screen takes dark from the tokens in DESIGN.md, not from separate drawings.

Resolved by redrawing in the corrections project (no longer deviations): Parameters (the false "cluster / memory pool" box), Roles (the product's 18 privileges from `Privilege.cs`), Capabilities (editor shell), Sign-in, My content (no storage quota, map previews).

## 6. Not designed, by decision

- SH-05 REST Services Directory, SH-06 Query page, ST-30 SDK test viewer — public API faces and a test page; out of scope unless the owner adds them.
- SV-13 legacy layer page — reached only when a redirect cannot place the layer.
