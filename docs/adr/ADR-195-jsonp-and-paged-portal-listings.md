# ADR-195 — JSONP is answered without the session cookie, and portal listings page

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-17, overnight, under the owner's *"sabaha kadar kapatabildiğini kapatır mısın?"* — the closing of the veteran review's small findings. The paging default is `INFERRED` and is [Q-178](../open-questions.md). |
| **Supersedes** | the code comment in `PortalEndpoints.GroupsAsync` that put paging off *"until a deployment has enough"* |
| **Superseded by** | — |

---

## 1. Context

The fifth ArcGIS veteran review (2026-10-17, dev.graticula.com, anonymous) found that `callback=` was ignored, so
nothing that reads the REST API through a `<script>` tag (JSONP: old embeds, the JS 3.x era, Dojo's `io.script`) could
read it; and that the portal's four listings — search, groups, a group's content, a folder's content — answered every
result with `nextStart: -1` whatever `start` and `num` asked, and ignored `sortField`.

## 2. Alternatives considered

### JSONP

- **A — wrap the answer, and read such a request as if it carried no session cookie (chosen).**
- **B — wrap the answer and authenticate as usual.** Refused: a script request carries the browser's cookies and no
  `Origin`, so it is the one cross-site read the CORS rule of Q-169 cannot see. `SameSite=Strict` keeps the cookie off
  another site's request, not off a sibling host's.
- **C — keep ignoring `callback`.** The clients that send it get nothing at all.

### Paging

- **A — honour `start`, `num` (at most 100) and `sortField`/`sortOrder`; without `num`, every result (chosen,
  `INFERRED`).**
- **B — ArcGIS's default of ten without `num`.** Exact, but this server's own callers and tests that send no `num`
  were written against the whole list, and every client that pages — Pro, the Maps SDK, the ArcGIS API for Python —
  sends `num`. Q-178 asks the owner.
- **C — page in SQL.** The listings are built from the objects the item filter reads by reflection; cutting the page
  from those changes the answer now, and SQL changes the cost when a deployment is large enough to feel it.

## 3. Counterarguments to the preferred option

- **A JSONP answer is always 200.** A browser does not run a script it was answered 4xx for, so the error would never
  reach the callback; the error object carries its own code, as ArcGIS's does.
- **Paging in memory still builds the whole list.** True, and the revisit trigger below.

## 4. Evidence

`/rest/services?f=json&callback=cb` answered `{"currentVersion"…` and `search?q=*&num=2&start=3` answered
`start 1, num 32, nextStart -1` on dev the same night. Tests: `SmallCompatibilityTests`, `IgnoredParameterTests`
(the wrapped answer is the plain one byte for byte), `JsonpTests`, `PortalPageTests`.

## 5. Decision

1. **`callback` wraps any JSON answer as `/**/name(answer);`**, `application/javascript`, `nosniff`, status 200. The
   name is a JavaScript identifier or a dotted path of them, at most 128 characters; anything else is refused and not
   echoed.
2. **A request with `callback` is authenticated as if it had no session cookie** — anonymous, or the token the page
   put in its own address.
3. **The four portal listings honour `start`, `num` (≤ 100), `sortField` and `sortOrder`,** and `nextStart` says
   where the next page begins. Without `num`, every result — Q-178.

## 6. Consequences

**Positive.** JSONP clients read the server; a client that pages is sent a page.

**Negative.** A JSONP caller never sees what a signed-in browser session could; it must use a token.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | No client of this server sends `num` expecting more than 100 | `INFERRED` — ArcGIS's own ceiling |

## 8. Dependencies

**Depends on:** [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md), Q-169 (cross-origin reads).

**Depended on by:** —

## 9. Revisit triggers

- A deployment whose listings take long enough to notice: page in SQL.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The owner confirms the paging default** (Q-178): every result without `num`, or ArcGIS's ten.
