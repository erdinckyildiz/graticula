# Graticula Hybrid design system

Stitch project `projects/1743378018328409795`, design system `assets/3901723720104477982`. Screen-by-screen mapping in [screen-inventory.md](screen-inventory.md); navigation decided in [ADR-187](../adr/ADR-187-primary-navigation-is-a-top-bar.md).

Graticula is a self-hosted enterprise GIS application server. One product, two workspaces:
**Studio** (publishers: content, items, maps, groups, sharing) and **Server** (administrators:
services, data sources, members, settings, operations). Both share this system.

## Character
Calm, dense, professional, operational. An enterprise GIS tool used all day by GIS analysts and
server administrators, in the lineage of ArcGIS Enterprise Portal (Studio) and ArcGIS Server Manager
(Server), with its own identity. Tables over cards. No decoration, no gradients, no glassmorphism,
no illustrations, no emoji, no stat-card rows, no glowing dots. Plain sentences in UI copy.

## Colour tokens (light)
- Paper (page background) #f6f8fa; Surface (panels, tables) #ffffff; Surface-2 #f1f4f8; Surface-3 #e7ecf3
- Ink (text) #101826; Muted #566275; Faint #616b7a; Rule #e3e8ef; Rule-strong #cfd7e3; Control edge #7e889a
- Brand navy #171b49 (wordmark mark, dark surfaces), navy deep #101336
- Accent teal #0d7d70 — primary buttons, links, selected tab underline, focus; hover #12a293; soft #e3f2f0; line #b8ded8
- Blue #2f56c8 is reserved for ONE meaning: shared with Everyone (public). Soft #eaefff.
- Status: OK #1c7d4d on #e6f4ec (dot #21b56a); Warning #8a5a08 on #fdf2e0 (dot #e0a020); Error #a5342a on #fceceb (dot #e0503f)
- Studio identity accent (small badge after the wordmark only): violet #6a4bc0. Server badge: teal.

## Colour tokens (dark)
Paper #0e1220; Surface #151a2b; Surface-2 #1b2134; Rule #2a3147; Ink #e6e9f2; Muted #9aa3b8;
Accent teal #2bb3a3; Public blue #7f9cf0; status colours lightened to keep 4.5:1 contrast.

## Typography
Inter (implementation maps to the system UI stack). Page title 26px/600; section head 17px/600;
body 15px/400; small 13.5px; label 11.5px uppercase-free, 600. Monospace for URLs, IDs, SQL, EPSG codes.

## Spacing and shape
4 / 8 / 12 / 16 / 22 / 32 px. Radius 5px for controls, 8px for panels and dialogs. Shadows only on
dialogs and menus. 1px rules separate regions.

## Shell
- Top bar 56px, white (dark: surface). Left: navy mark + "GRATICULA" wordmark + workspace badge
  ("Studio" or "Server"). Then horizontal primary navigation — Studio: My content · Map · Anonymous view · Groups · Domains; Server: Services · Site · Security · Logs (ADR-187), selected item has a 2px teal
  underline and ink text. Right: Studio/Server switch (only for people who may enter both), signed-in
  name with role line, Sign out. No notification bell, no app launcher grid, no global search.
- Contextual left panel 248px under the top bar: sub-navigation, scopes, folders, filters of the
  selected area. Selected row: teal-soft background, teal text, 3px teal bar on the left.
- Content area: breadcrumb, page title + one-line lede, primary action top-right (one teal button).
- Under 760px the left panel becomes a drawer behind a "Filters" / menu button, the top nav scrolls
  horizontally, row menus become bottom sheets.

## Components
- Tables: 44px rows, header in Muted 13px, zebra off, hover Surface-2, thumbnail 40x28 with 4px radius,
  name as a teal link with a muted meta line under it, row ⋯ menu at the end. Pager "1–10 of 47" with
  prev/next and page numbers.
- Pills: status pills always carry a word (Started, Stopped, Refusing) not colour alone. Sharing pills:
  Owner (grey), Groups (grey with count), Organization (teal-soft), Everyone (blue-soft).
- Segmented tabs for item tabs: text tabs with teal underline.
- Buttons: primary teal filled; secondary white with Control edge border; danger red text/border; ghost.
- Dialogs: 8px radius, title + close X, body, footer right-aligned Cancel / primary.
- Toast: bottom-left, one at a time, word + sentence.
- Focus: 2px teal ring offset 2px on every interactive element.
- Empty states: one sentence saying what is true and one action. No illustrations.
