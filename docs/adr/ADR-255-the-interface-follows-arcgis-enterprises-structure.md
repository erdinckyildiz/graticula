# ADR-255 — The interface follows ArcGIS Enterprise's structure

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — an owner decision taken on mockups, built for one window width at a time; §6 says what is not yet done |
| **Decided** | 2026-11-02, by owner decision (*"Ben çok beğendim. Buna geç."*) |
| **Supersedes** | The layout parts of [ADR-187](ADR-187-primary-navigation-is-a-top-bar.md) (the utility line over the bar) and [ADR-189](ADR-189-studio-holds-the-organization-and-server-the-engine.md) (page heads that carry a paragraph); nothing else in either |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The owner was shown mockups of every Studio, Server and System screen at 1,280 × 800, a sign-in page at two widths, and the
phone layout built the same day, all drawn after ArcGIS Enterprise's structure, and approved them. This is layout and
presentation: no control is added, none is removed, and no handler or API changes.

## 2. Decision

One bar of 56 pixels on every surface — the mark and the name, the surface's sections as links with an underline under the
current one, the search (Studio), and the account menu. The account menu now also holds the switch between Server, Studio
and System, the server's health, the theme and Sign out; the utility line that stood over the bar is gone. The two-step notice
is one thin line, edge to edge. A page's head is its title and one control. No breadcrumbs. No explanatory paragraph under a
heading. Item pages: a small thumbnail with a pencil, the title, one meta line, *Open in Map Viewer*, *Share* and ⋯ at the
right, tabs under them; Description and Layers on the left and Item information on the right. Sign-in is a navy brand panel
with the Graticula mark and a card, and on a phone the panel is the top and the card a sheet rising from below.

## 3. What moved where

| Was | Is |
|---|---|
| The utility line (`#utilbar`) over the header | Dissolved. `#surfaces` and `#healthDot` are moved into the account menu's sheet on a wide window (`console.js`, `deskParked`) and back where they were on a phone, which lists them in its drawer. Moved, never copied: ids, `refreshHealth` and the router's `aria-current` are unchanged. |
| The organization's name in the utility line | Still `#tenantName`, now a grid cell of the bar between the name and the sections |
| A three-line amber notice in a box | One line of 36 pixels; *Set it up* and *Not now* stay. After *Not now* the account menu keeps *Set up two-step sign-in* (`#accountTwoStep`), on a phone in the drawer |
| The breadcrumb on item and group pages | Hidden on a wide window; the bar's back arrow (`#barBack`, which the phone already drew) goes to the nearest parent, the folder where there is one |
| An item's buttons (favourite, categorize, download, delete, Move…) | Behind ⋯; the action row folds on a wide window as it does on a phone (`phoneFoldActions`) |
| *Redraw thumbnail* as a labelled button under the picture | A pencil on the picture, same element and same name |
| Overview's three cards | Description and Layers left, Item information right |
| The paragraph under a title on Content, Groups, Members, Roles, Organization settings, and on Server's and System's list screens | Not drawn on a wide window. Where it carried a fact that a screen needs, the fact is in the empty state or the control's name |
| Sign-in: one card on paper, the mark inside the card's head | Brand panel and card; the card keeps the code step, the providers, *From another organization?*, the refusal, *Forgot password?* and the must-change-password page |

## 4. What was left out, and why

- **Group scope control, group row ⋯.** The mockup's *My groups* control and row actions have nothing behind them: the list is
  the caller's groups, or every group for an administrator, and a row opens a page. Nothing was invented.
- **The mark.** The mockup's mark (circle, a meridian, three parallels, a diagonal) is on the sign-in page. `/favicon.svg` is
  still the nine squares in a blue circle. Whether the favicon and the bar's mark should become the mockup's is the owner's.
- **Health in the bar.** The mockup shows *[N] service failing* as a link in Server's bar. It is in the account menu here,
  as the brief says; a failing server is therefore one click further than the mockup draws it. Listed for confirmation.
- **Some screens are restyled by the new bar and page head only** — Publish, Data sources, Logs, System's screens and
  Organization's sub-pages keep their present bodies, which already have the mockups' structure; their per-screen differences
  are not yet rebuilt (§6).

## 5. Consequences

**State.** None: markup, stylesheet and script only; nothing in the catalogue and nothing at runtime beyond the one `sessionStorage` key the two-step notice already used.

Console tests that asserted the old arrangement were changed, each for the reason above (the utility line, the breadcrumb,
the buttons now behind ⋯). `PhoneLayoutTests` and a new `DesktopLayoutTests` hold both widths.

## 6. Not done

The per-screen bodies of Server's service page, Publish, Sources, Logs and System's General (one save bar) are not rebuilt to
their mockups; the bar, the page head and the item page are. Recorded here rather than implied.
