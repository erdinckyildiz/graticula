# ADR-262 — The portal answers for its users

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — every act is Studio's own; who sees whom and the invitation's password are judgements, recorded below |
| **Decided** | 2026-11-03, by owner decision. The ArcGIS veteran's review of v1.0.499 listed it as #3, *portal user management (community/users search, portals/self/users read; then createUser/updateUserRole/disable/delete)*; asked which of the review's open items to take, the owner chose all four, this one among them. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The portal answered for one user: the caller (`community/users/{name}`, and only their own name). Studio's Organization
screen lists, creates, re-roles, disables and removes members, under `admin:manageMembers`, and an ArcGIS administrator's
first script does the same through the ArcGIS API for Python — `gis.users.search()`, `gis.users.create()`,
`user.update_role()`, `user.disable()`, `user.delete()`. Every one of those was a 404.

[Q-127](../open-questions.md) closed on not naming another member's username on the portal's item, because this surface
is reachable anonymously. That reason is about anonymous callers, and it still holds.

## 2. Alternatives considered

### Alternative A — Leave users to Studio

An administrator with two hundred members to provision by script does it by hand. Not chosen.

### Alternative B — Every member's full record to every signed-in caller

What a member's e-mail and last sign-in are is the administrator's to know, not every colleague's. Not chosen.

### Alternative C — Doors onto Studio's acts, with three audiences *(chosen)*

## 3. Decision

1. **Who sees whom.** Nobody not signed in sees any user. A signed-in member finds the organization's active members by
   their public profile — username and full name — through `community/users?q=`, as a Portal member finds a colleague
   to share with. Whoever may manage members sees everything through `portals/self/users` and its `search`: role, role
   id, user type, e-mail, whether disabled, last sign-in. **INFERRED, for the owner to confirm:** that a member may find
   other members by name at all, which a Portal allows and this portal did not. *(Confirmed by the owner 2026-11-04:
   "Kalsın".)*
2. **Roles are said as ArcGIS says them.** `administrator` is `org_admin`, `publisher` `org_publisher`, `user` `org_user`,
   `viewer` the Viewer role's id `iAAAAAAAAAAAAAAA`, and a role of this server's own is `org_user` with its name as the role
   id. The same names are read back the other way.
3. **Changes are Studio's, run inside the request**, so the privilege (`admin:manageMembers`), the refusals and the audit
   line are the Organization screen's: `portals/self/invite` is New member, `portals/self/updateuserrole` is the role
   change, `community/users/{name}/disable` and `enable` are Studio's, and `community/users/{name}/delete` is Remove
   member — which refuses a member who still owns content until it is said what becomes of it, as a Portal refuses to
   delete a user who owns items.
4. **An invitation needs a password.** This server sends no e-mail, so an invitation without one, or with one shorter than
   the sign-in rule's eight characters, is named in `notInvited` with the reason, and the rest are made. The password
   given is set as an administrator's reset sets one: the member changes it when they first sign in. The one-time
   password Studio's New member issues is read inside the request and never answered.

## 4. Consequences

- **State.** None of its own: every change is the member directory's, as Studio's.
- The Python API's `gis.users` works against this portal for search, create, role, disable, enable and delete.
- A user's `update` (full name, description) and `reset` are still not answered; Studio has the first only in part and
  the second as its own reset, and both are left for a review that asks for them.
- `community/users/{name}` for somebody else still answers as before. Search finds them; their profile page is not opened.

## 5. Conditions

None.

## 6. Dependencies

- [ADR-015](ADR-015-authentication.md) — members, roles and their removal.
- [ADR-214](ADR-214-the-portals-item-writes-do-what-they-answer.md) §5a — the pattern of a door running Studio's handler.
