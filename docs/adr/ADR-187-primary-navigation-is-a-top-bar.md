# ADR-187 — Both surfaces navigate from a top bar, and Server's is Server Manager's four tabs

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the structure is the owner's, taken from two reference products; nothing is built yet, and §9 says what would show it wrong |
| **Decided** | 2026-10-16, by owner decision. **Studio:** the Hybrid concept with a Portal-like horizontal navigation, approved after the owner noticed the top bar had been left out ([GRATICULA-HYBRID.md](../design/GRATICULA-HYBRID.md)). **Server:** asked whether ADR-091's five sections should move to the top as ArcGIS Server Manager places them, the owner answered *"taşı"* — move them. Then, shown Server Manager's own top bar (Services, Site, Security, Logs, with data stores under Site) and asked whether ours should be grouped the same way, *"geç"* — switch to it. The priority they set the same day: *"önceliğimiz arcgis enterprise ekranlarına benzer bir yapı. arcgis portal ve arcgis server"* |
| **Amends** | [ADR-091](ADR-091-server-sidebar-five-rows.md) §5 (the five rows become four tabs, regrouped; every subpage and address it kept is still kept); [ADR-034](ADR-034-server-and-studio.md) §5a and §6 (the rail that holds both surfaces' rows) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Both surfaces draw their primary navigation as a vertical rail on the left (`#rail`, `#tabs`, `drawSurfaces`
in `console.js`), and each page then draws its own left column under it — the folder rail on Services, the
scope strip and folder bar on My content, the section nav on a service. A page with a left column therefore
has two left columns.

The two products the owner measures Graticula against do it the other way. ArcGIS Enterprise Portal has a
horizontal top bar (Home, Map, Groups, Content, Organization) and a page-owned filter panel on the left.
ArcGIS Server Manager has top tabs (Services, Site, Security, Logs) and, on Manage Services, a left panel of
folders and service filters. Their behaviour is publicly documented; nothing of their implementation is used
(CLAUDE.md §5).

## 2. Alternatives considered

### Alternative A — Top bar on both surfaces; the left panel is the page's *(chosen)*

**Argument for.** It is the shape of both reference products, so a Portal or Server Manager user finds the
sections where they look first. The left column stops being shared between the app and the page, so a page
with folders or filters has one left column, not two. The header already exists and already holds the
surface switch and the signed-in name; the sections join it.

**Argument against.** Top bars hold fewer entries than rails before they wrap. A tablet or phone width
needs a scrolling bar or a menu, where a collapsed rail of glyphs was already solved (`#shell.tight`).

### Alternative B — Top bar on Studio only; Server keeps ADR-091's rail

**Argument for.** ADR-091 is three weeks old and was the owner's; Server's sidebar was just made readable.
The Hybrid document said not to copy Studio's *Portal* navigation into Server.

**Argument against.** That sentence is about Studio's *labels* (Home, Map, Groups…), not about the bar.
Server Manager, which the document names as Server's model, has top tabs. Two surfaces of one product
navigating in two different places is the inconsistency the shared design system is meant to remove.

### Alternative C — Keep the rail on both, restyled

**Argument for.** No layout change; every test that finds a row by its place in `#tabs` keeps working.

**Argument against.** It is not what the owner approved for Studio, and keeps the double left column.

## 3. Counterarguments to the preferred option

- **ADR-091 argued for the rail and nobody has said it was hard to find.** True. What ADR-091 decided about
  subpages is kept — one table names them, each keeps its address, a subpage lights its tab. What changes is
  the grouping: ADR-091 took Portal's Organization › Settings as its model, and this takes Server Manager's.
- **"Site" reads as plural machines.** Server Manager uses it for one machine too, and Data sources, Status and
  General are each about this installation; the alternative names (Server, System) collide with the surface's
  own name or say less.
- **Portal users look for Members under Organization.** They will find it one tab away from where Portal puts
  it. Server is the administrator's surface and is measured against Server Manager, where users are Security's.
- **Studio has five rows, not Portal's.** The bar carries Studio's real rows — My content, Map, Anonymous
  view, Groups, Domains — not Portal's labels. Home, Gallery and Organization have no screen behind them and
  are not drawn (GRATICULA-HYBRID.md, constraint 7).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Portal's primary navigation is a top bar | Portal home page and Content page | [Get started with the portal, ArcGIS Enterprise](https://enterprise.arcgis.com/en/portal/latest/use/get-started-with-portal.htm) |
| Server Manager's sections are top tabs (Services, Site, Security, Logs) with folders on the left of Manage Services, and data stores under Site | Server Manager tour; the owner's screenshot of Manage Services, 2026-10-16 | [What is ArcGIS Server Manager?](https://enterprise.arcgis.com/en/server/latest/administer/windows/what-is-manager-.htm) |
| Today's rows and subpages | `SURFACES` :2093, `SUBPAGES` :2206, `drawSurfaces` :2691 | `src/Graticula.Host/wwwroot/console.js` at `4d967318` |
| The approved concepts | `studio-hybrid.png`, `server-hybrid.png` — held by the owner, not committed | GRATICULA-HYBRID.md |

## 5. Decision

**Both surfaces draw their primary sections as a horizontal bar in the shared header**, after the wordmark
and the surface badge. **Studio's** are its existing rows in their existing order: My content, Map, Anonymous
view, Groups, Domains.

**Server's are ArcGIS Server Manager's four: Services, Site, Security, Logs.** ADR-091's five sections and
their subpages fall into them without remainder:

| Tab | Pages (address unchanged) |
|---|---|
| Services | Manage services `#/services`, Publish a service `#/publish`, a service `#/service/…` |
| Site | Data sources `#/sources`, Status `#/operations`, General `#/settings` |
| Security | Members `#/members`, Roles `#/roles`, Sign-in `#/signin`, Apps `#/apps` |
| Logs | Logs `#/logs` |

*Site* is this server installation, as it is in Server Manager for a single machine; it does not promise
machines, clusters or web adaptors, and no page for them is drawn. *Members* sits under Security as Server
Manager's users do, not under an Organization tab as Portal's do, because Server is Server Manager-shaped
(ADR-034 §5h).

**The left panel belongs to the page.** It holds what the selected tab needs — Services' sub-navigation and
its folders and health; Site's and Security's pages, which replace ADR-091's strips; My content's folders and
type filter — and a page that needs nothing there has no left panel (the item page, the service page's own
sections excepted).

**Nothing else moves.** Every address keeps working (`#/roles` still opens Roles and lights Settings); the
gate on each surface is unchanged (ADR-034 §5b); `SURFACES` and `SUBPAGES` stay the tables that say what
the sections and subpages are. Under 760 px the bar scrolls horizontally and the left panel becomes a
drawer behind a button.

## 6. Consequences

**Positive.** Both surfaces read as one product with the reference products' shape. A page with folders or
filters has one left column.

**Negative.** Browser tests that locate rows in `#rail`/`#tabs` (`ServerSidebarTests` and others) are
rewritten with the header, and `SUBPAGES`/`ROW_OF` regroup Server's pages under four tabs. The collapsed rail (`gis-rail` in `localStorage`) and the decorative `#railArt`
go. ADR-091's subpage strips become left-panel rows on Site and Security — the "second navigation
device" ADR-091 §3 worried about is now the left panel rather than a strip.

**State.** None. Presentation only; no route, table, endpoint or stored value changes.

*(Built 2026-10-16, step 1 of the redesign: the rail, its illustration and *Collapse* are gone; `#tabs` is a
bar in the header beside the mark and the surface badge; `SURFACES.server.tabs` is the four tabs and
`SUBPAGES` the three lists; a page with siblings gets `.view.withside`, a left column that becomes a
horizontal bar below 1,100 pixels. A design review measured the first build and found the bar shrinking
under the Server|Studio switch at every laptop width — Studio's last tab hidden at 1,440 and reachable by
keyboard while hidden — so the bar does not shrink: the health sentence gives way first (its dot carries
it as a label), then the product name, and below 1,100 pixels the bar takes a row of its own. Two console
tests changed with the layout rather than with a defect: `SettingsScreenTests` now finds Settings as
*General* in Site's list, and `SymbologyEditorTests` measures 1,300 pixels with nothing collapsed, since
there is nothing to collapse.)*

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS Enterprise users are the readers both surfaces are measured against | `INFERRED` in ADR-101 §7 and ADR-102 §7; stated by the owner 2026-10-16 |

## 8. Dependencies

**Depends on:** [ADR-034](ADR-034-server-and-studio.md), [ADR-091](ADR-091-server-sidebar-five-rows.md),
[ADR-101](ADR-101-studio-is-walked-the-way-a-portal-user-walks-it.md),
[ADR-102](ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md).

**Depended on by:** —

## 9. Revisit triggers

- A design review at tablet or phone width finds a section unreachable from the scrolled bar.
- A fifth Server tab or a sixth Studio row is proposed: ADR-091 §9's rule — say why it is not a subpage — applies
  to the bar as it did to the rail.

## 10. Dissent

None recorded.
