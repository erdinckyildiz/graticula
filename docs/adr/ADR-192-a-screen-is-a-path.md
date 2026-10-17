# ADR-192 — A screen is a path, not a fragment

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — measured on dev.graticula.com in a browser before release; the old addresses are kept |
| **Decided** | 2026-10-17, by owner decision |
| **Supersedes** | [ADR-034](ADR-034-server-and-studio.md) §5a's *"the path carries the surface and the hash the screen"* |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The owner, 2026-10-17: *"şu urldeki hashtag ne işe yarıyor. kalkmaz mı?"* — then *"şu hashtag kaldırma işine
yönelelim. dev'de"*. Studio and Server were one page each, `/studio/` and `/server/`, and the screen was the fragment:
`/studio/#/content`, `/studio/#/service/hosted/x?tab=settings`. It was the cheapest way to have a screen you can link to
and reach with Back (ADR-020 §5c) while the server returned one file. Portal's addresses are paths.

## 2. Alternatives considered

### Alternative A — keep the fragment

**Argument for.** Nothing to change; the server needs no knowledge of screens.

**Argument against.** It is the owner's objection, and the one thing in the address bar that says *this is not
Portal*.

### Alternative B — a path per screen, the page served for every screen address

**Argument for.** The address reads as Portal's; Back, bookmarks and a new tab work as they did.

**Argument against.** The server has to tell a screen from a file, and the page has to find its own files from any depth.

## 3. Counterarguments to the preferred option

- **A screen that someday ends in an extension is taken for a file.** None does; a screen is a word.
- **`base-uri` loosened from `'none'` to `'self'` on the console's pages.** An injected `<base>` can still name only
  this origin; the REST directory keeps `'none'`.

## 4. Evidence

On dev.graticula.com, 2026-10-17: clicks between tabs, Organization's strip and column and content rows changed the
address without a page load; Back and Forward returned to the right screens; `/studio/user/root` opened directly and
after a reload; `/studio/service/hosted/kontrol?tab=settings` reloaded onto its tab; `/studio/#/roles` and
`/server/#/members` landed on `/studio/roles` and `/studio/members`; no request answered 404 and the page threw nothing.

## 5. Decision

- **The screen is the path after the surface**: `/studio/content`, `/studio/groups`, `/studio/user/{name}`,
  `/studio/service/{folder}/{name}?tab=…`, `/server/services`. The query is the query.
- **The server answers every GET under `/studio/` or `/server/` whose last segment has no extension with the one page**,
  its `<base href>` the surface's, so its files load from any depth. The console's policy says `base-uri 'self'`.
  `/studio/sharing`, `/studio/rest` and `/studio/arcgisuris.xml` (ADR-191) are rewritten before this and are not pages.
- **Inside the page the router keeps its own words**: `routeHash()` reads the address as `#/…` did; `go()` moves with
  `history.pushState` in the same surface and loads the page for the other. A link written `#/…` is rewritten to its
  path as it enters the document, and a click on a screen link is a move, not a load.
- **Old addresses open**: `session.js` turns `/studio/#/x` into `/studio/x` before anything is drawn; a `#/…` set later
  (typed, or by a script) is turned the same way.

## 6. Consequences

- **State.** None.
- Tests that read `location.hash` read `routeHash()`; tests that set it still work.
- The console's content previews are painted three at a time rather than one (found the same day; not part of this
  decision, but released with it).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | No screen name has a file extension | true of every screen on 2026-10-17 |

## 8. Dependencies

**Depends on:** [ADR-020](ADR-020-admin-console-and-service-status.md) §5c, [ADR-034](ADR-034-server-and-studio.md),
[ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md).

**Depended on by:** —

## 9. Revisit triggers

- A screen whose name needs a dot.
- Another product mounted under `/studio/` or `/server/` that is not the console.

## 10. Dissent

None recorded.
