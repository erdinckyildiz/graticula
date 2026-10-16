# Graticula Hybrid — approved UI/UX direction and Codex handoff

**Status:** Design direction approved by owner in conversation, 2026-10-16. **Not an ADR and not implementation approval for new capabilities.**
**Scope:** Existing `/studio/` and `/server/` UI only. Preserve accepted ADRs and backend/API behavior.

## Intent

Graticula should feel like **one product with two purpose-built workspaces**:
- **Studio** borrows the *navigation familiarity and publishing journeys* of ArcGIS Enterprise Portal, with Graticula's own clean visual identity.
- **Server** borrows the *operational information architecture and service-management workflows* of ArcGIS Server Manager, without copying its UI or introducing unsupported settings.
- Both use shared colors, typography, controls, spacing, responsive rules, and theme tokens.

The owner reviewed several concept images, preferred the **Hybrid** approach, specifically noticed that the Portal-like **horizontal top navigation had been omitted**, and approved the revised Studio concept after it was restored. The owner then requested Server be reconsidered against **ArcGIS Server Manager** and approved that direction too.

**Important:** Concept screenshots were created in a ChatGPT conversation and are **not committed to this repository** by this document. Ask the owner to attach the approved Studio and Server mockups to the Codex task before pixel-level implementation. Text below is authoritative for intent; screenshots are visual references, not specifications for new features. Mockup labels, counts, datasets and controls may be fictional.

## Non-negotiable architecture constraints

1. Respect [ADR-034](../adr/ADR-034-server-and-studio.md): one deployable, one API, shared shell/design system, two paths (`/studio/` and `/server/`), hash-addressed screens, privilege-aware navigation. Administrators can access both; publishers should not be exposed to Server-only controls.
2. Respect [ADR-091](../adr/ADR-091-server-sidebar-five-rows.md): Server's **exact five primary sections** remain **Services, Data sources, Members, Settings, Operations**. Roles, Sign-in and Apps live under Settings; Logs under Operations. Publish is an action within Services, not a sixth section. Keep existing deep links.
3. Respect [ADR-101](../adr/ADR-101-studio-is-walked-the-way-a-portal-user-walks-it.md): preserve working Studio journeys, My content scope/search/filter behavior, and the Map entry/viewer.
4. Respect [ADR-102](../adr/ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md): Item Details has **Overview · Data · Visualization · Settings**; Settings only for authorized owners/admins. One layer selector within the item. Styles live in Visualization. Tile cache lives under Settings > Tile layer, with a **Manage tiles** shortcut on Overview. Do not restore separate layer settings pages or multiple symbology entry points.
5. Preserve API authorization, browser behavior, existing routes/bookmarks, service start/stop versus sharing semantics, and existing tests.
6. The frontend is intentionally **plain HTML/CSS/JavaScript, without npm/framework/build step**. Work with the existing implementation, not a React/Vite rewrite.
7. Do not add mockup-only features such as Scene, Living Atlas, app builder, dashboards, or unsupported ArcGIS Server Manager controls. Feature availability is determined by the repository, not an AI-generated picture.

## Studio: approved design direction

- **Global horizontal Portal-like navigation at the top**, a major element of the owner's approved revision. Suggested labels are **Home · Map · Groups · Content · Organization**, but **validate each against implemented screens and roles** before adding; do not create fake destinations. Do **not** show Gallery or Scene merely because they appear in generated images.
- **Contextual left panel** for subnavigation, scopes, filters and actions belonging to the selected top-level area. Avoid duplicating the same destination in multiple nav levels.
- **My Content:** table-first, with optional card/grid view only if it can be implemented cleanly without losing sorting, search, filtering, keyboard accessibility or list density. Preserve existing content behavior and saved web maps in the same content list.
- **Item Details:** contemporary spacious layout, clear item identity/metadata and map preview, exact four tabs above, with role-based Settings visibility. No duplicate control entry points.
- **Map:** prioritize map canvas space, contextual tools and the existing viewer behavior.
- Top right: user/account controls and search only where genuinely supported.
- **Light and dark themes:** desired end state, implemented through shared tokens and accessible contrast; do not ship a decorative toggle without functional persistence/behavior.

## Server: approved design direction

- **Reference model: ArcGIS Server Manager**, not Portal's content library.
- Server retains the **five ADR-091 sections** as primary navigation; do not replace them with Studio's Home/Map/Groups/Content/Organization tabs.
- **Services** should lead with a practical folder tree/breadcrumb, dense searchable service list, type/status, and authorized operational actions (e.g., start/stop, details) that already exist.
- **Service detail** should make identity, endpoint(s), status, layers and existing operational settings easy to find. Use tabs/sections only if they map to actual capabilities and one canonical location.
- **Data sources, Members, Settings and Operations** use the same visual grammar; Logs remain under Operations.
- Do not introduce imaginary instance pooling, server clusters, geoprocessing settings, usage charts, or caching knobs based only on an image. Verify against real endpoints and ADRs first.
- Clearly distinguish **Server operational controls** from **Studio publisher/item controls**.

## Shared design system

- Reuse the existing `src/Graticula.Host/wwwroot/console.css` token system rather than arbitrarily replacing the palette. Current established tokens include navy `--nav-base: #171b49`, teal `--accent: #0d7d70`, cyan sidebar selection, and distinct blue for public sharing; these have documented semantic/accessibility reasons.
- Reuse shared header, typography scale, buttons, inputs, tables, badges for actual states, dialogs/drawers, spacing and focus styles.
- Design for desktop and narrower widths. Navigation must remain discoverable at tablet/mobile sizes; item tabs and layer selector must not become inaccessible.
- Accessible focus, keyboard operation, text contrast, and status indicators with labels rather than color alone.

## Existing implementation entry points

- `src/Graticula.Host/wwwroot/index.html` — existing shell (`#shell`, `#rail`, `#tabs`), shared markup.
- `src/Graticula.Host/wwwroot/console.css` — extensive existing tokens and component rules.
- `src/Graticula.Host/wwwroot/console.js` — router/surfaces/subpages (inspect in workspace; do not infer implementation from this document).
- `src/Graticula.Host/wwwroot/session.js` — early session/surface initialization.
- `tests/Graticula.Console.Tests/` — browser and navigation regression tests.
- Read `CLAUDE.md` and relevant newer ADRs before editing. This document is a visual handoff, **not a superseding architecture decision**.

## Incremental implementation sequence for Codex

1. **Audit first:** inspect shell, routes, role gates, current Studio/Server pages, CSS tokens, responsive behavior, and browser tests. Identify existing components/constraints and any newer ADR that supersedes assumptions here.
2. **Shared shell/navigation prototype:** implement Studio's horizontal top nav and contextual panel while preserving Server's five-section structure and all deep links. Do not rewrite the entire UI at once.
3. **Studio My Content and Item Details:** migrate appearance incrementally while retaining ADR-101/102 behavior.
4. **Server Services list/detail:** align the information hierarchy with Server Manager's workflows, using only supported actions.
5. **Shared component polish, dark mode and responsive accessibility.**
6. **Verify:** run relevant browser/unit tests, check desktop/tablet/mobile, role visibility, Back/Forward/deep links, and both themes. Record test results and remaining gaps.

## Definition of done

- Owner-approved Studio top navigation is present and usable.
- Server's operational identity is distinct and its five primary sections remain intact.
- Both surfaces visibly share a coherent Graticula design system.
- No existing permission, URL, content journey, or service action regresses.
- No invented features or controls ship.
- Changes are incremental, reviewable and backed by tests.

## Suggested Codex task

> Read `docs/design/GRATICULA-HYBRID.md`, `CLAUDE.md`, ADR-034, ADR-091, ADR-101, ADR-102, and any newer relevant ADRs. Inspect the existing frontend and browser tests. Implement **phase 1 only**: shared shell and Studio's approved Portal-like horizontal navigation with contextual left panel, while preserving Server's five-section navigation, existing URLs, permissions, and all current functionality. Keep the existing framework-free architecture. Use the attached approved mockups as visual reference only. Run relevant tests and report changes, regressions and unresolved questions.
