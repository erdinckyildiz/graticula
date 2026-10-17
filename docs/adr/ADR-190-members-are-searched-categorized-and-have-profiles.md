# ADR-190 — Members are searched in the database, sorted into categories, and have profiles

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the shape is Portal's and the owner's; the search has not been measured past a few hundred accounts (§9) |
| **Decided** | 2026-10-17, by owner decision |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The owner, 2026-10-17: *"5 kullanıcı yönetirsin de, n tane kullanıcı olursa sorgulaması vs olmayacak mı?"* — then a
Portal Members page (search, filters down the left including categories and role, sortable columns, a row menu) and
*"categoryleme, category'e göre filtreleme. sorgulama. gibi gibi"*, then Portal's profile page: *"kullanıcıların profil
sayfaları olsa. her kullanıcı ya da admin onlara fotoğraf koyabilse."*

Measured before answering: `/admin/members` read every account on every visit and the page sliced it in the browser;
there was no search; the transfer picker and the last-administrator guard read the same whole list.

## 2. Alternatives considered

### Alternative A — search in the browser over the whole list

**Argument for.** No server change; instant filtering.

**Argument against.** It is the defect: the list is read whole, and at the scale the product is for (ADR context:
100–1,000 services, an organization behind them) that read is what stops working.

### Alternative B — last sign-in as a column written at every sign-in

**Argument for.** A sort over a column is cheaper than a `max()` per member.

**Argument against.** Every sign-in path would have to learn to write it. A session row is written by every one of them
already and is kept after it ends, so `max(session.created_at)` is the answer with no new write path. Revisit if §9's
measurement says otherwise.

### Alternative C — categories as tags on the account

**Argument for.** One text array column.

**Argument against.** Portal's categories are a tree an administrator draws and renames; a renamed tag would have to
be rewritten on every account.

## 3. Counterarguments to the preferred option

- **A profile's photo is bytes in the platform store.** A megabyte at most, its own row so listing members never reads
  it, and served with a cache lifetime under an address that changes when the photo does. A store of ten thousand
  photographed members holds ten gigabytes at the cap — the cap is there to be lowered if that is ever real.
- **A profile visibility of `public` lets a stranger read a member's name and bio.** It is the member's choice or an
  administrator's, as Portal's is, and the default is `organization`.

## 4. Evidence

On dev.graticula.com, 2026-10-17, against a copy of the showcase's store at schema 90: search, role, user type,
status, last-login, group and category filters each return what they say, a parent category matches members in its
children, a bulk categorize leaves a box half-ticked for members only some of whom are in a category and changes
nobody through it; a public profile and its photo are read with no credential, an `organization` one is a 404 to the
same caller, and an anonymous edit is a 403.

## 5. Decision

**Search.** `GET /admin/members?num=…` matches, orders and pages in the database (`IMemberDirectory.SearchMembersAsync`):
`q` (sign-in name, display name, e-mail), `role`, `type`, `status`, `group`, `signedIn` (`7d`, `30d`, `90d`,
`older90d`, `never`), `category` (a category matches those under it), `sort` (`name`, `lastSignIn`, `userType`, `role`,
`created`), `order`, `start`, `num` ≤ 500; it answers `total` and, per member, `lastSignIn`, `email`, `photo`,
`categories`. Without `num` the endpoint answers every member, as before.

**Categories.** A tree (`member_category`), a member in any number (`principal_category`); drawn, renamed and deleted
from *Edit categories* on the Members page, assigned from a row's menu or in bulk. `admin:manageMembers`.

**Profiles.** `#/user/{name}`, and *My profile* in the account menu: a photo (PNG, JPEG or GIF, ≤ 1 MB, the bytes
checked against the type), a display name, an e-mail address, a bio, and a visibility — `private` (the member and
whoever manages members), `organization` (anybody signed in), `public`. Changed by the member or by an administrator
who manages members. `GET|PUT /rest/members/{name}/profile`, `GET|PUT|DELETE /rest/members/{name}/photo`; a profile
the caller may not read is a 404.

**Migration 90**, expand: `principal.email`, `principal.bio`, `principal.profile_visibility`, `principal_photo`,
`member_category`, `principal_category`.

## 6. Consequences

- **State.** In the shared catalogue (migration 90): three `principal` columns, `principal_photo`, `member_category`,
  `principal_category`. Last sign-in is read from `session`, also shared. Nothing node-local; a photo's bytes are
  read per request and cached by the browser, not by the server.
- The Members page holds twenty-five members, so the transfer picker and the last-administrator guard ask their own
  small questions (`status=active`, `role=administrator`).
- ArcGIS clients' `community/users` does not yet answer a search, nor carry the photo or bio — §9.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Session rows are kept after they end, so the newest is the last sign-in | true of the code on 2026-10-17: nothing deletes from `session` |

## 8. Dependencies

**Depends on:** [ADR-015](ADR-015-authentication.md), [ADR-018](ADR-018-authorization-and-roles.md),
[ADR-189](ADR-189-studio-holds-the-organization-and-server-the-engine.md).

**Depended on by:** —

## 9. Revisit triggers

- A deployment with thousands of members: measure the search's `max(session.created_at)` per member, and add a column
  if it is the cost.
- Something starts deleting old sessions: last sign-in would forget, and needs its own column first.
- ArcGIS Pro's share-with-users needs `community/users?q=` and the profile's thumbnail — the next step on this ADR.

## 10. Dissent

None recorded.
