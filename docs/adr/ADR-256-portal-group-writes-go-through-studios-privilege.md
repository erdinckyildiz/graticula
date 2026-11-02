# ADR-256 — Portal group writes go through Studio's privilege

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — an owner decision, built as doors onto acts that already existed and measured through the portal, through Studio and through the ArcGIS API for Python |
| **Decided** | 2026-11-02, by owner decision (*"Aç, Studio'nun yetkisiyle"*), [Q-224](../open-questions.md) |
| **Supersedes** | [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md) §4a's *read-only* boundary for groups; nothing else in §4a — the mapping and what a group discloses stand |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ADR-040 §4a (2026-09-09) made our groups portal groups and published them **read-only**: *"Accepting a create here would
be a second write path to one table with a different authorization story, which is how two surfaces come to disagree."*

On 2026-11-01, sharing a layer to a group from Pro at the showcase's second organisation, the portal listed groups and
shared into them but answered `community/createGroup` with 404. The ArcGIS API for Python's `gis.groups.create` fails
there, and `/admin/groups` — the only door — does not take a token from `generateToken` (ADR-015 §4). The ArcGIS
veteran's review of the same night ranked membership next: a script that *makes the group, adds the users, shares*
breaks at line two. A `createGroup` was written that night (7a6b4135) through the same `IGroupDirectory.CreateAsync` and
`groups:create` the admin API uses, and reverted the same night (64646f46), because it crossed §4a without an ADR.

The owner was asked (Q-224) and answered: open them, with Studio's privilege.

## 2. Alternatives considered

### Alternative A — keep §4a: groups are made and peopled in Studio only

**Argument for.** One door, one authorization story, no mapping of ArcGIS's parameters onto ours to get wrong. A portal
client still lists, reads and shares into groups.

**Argument against.** Every script written against the ArcGIS API stops at its first group write, and the token those
scripts hold does not open `/admin` at all — so the boundary is not *use Studio's API instead* but *use a browser*. §4a's
reason was never that writes are wrong; it was that a second write path would have its own authorization.

### Alternative B — portal doors with their own checks, written against the store

**Argument for.** The original `createGroup` was this: it called `IGroupDirectory` directly and checked `groups:create`
itself. Small, and the store already enforces ADR-036's second axis (owner or manager) on every method.

**Argument against.** It is exactly §4a's failure, one level up. The store holds the standing check; the privilege check,
the public-visibility check (ADR-199), the audit row and the refusal wording live in the admin handler. The reverted
`createGroup` already disagreed with Studio in two places: it made a group `public` without asking for
`sharing:shareToPublic`, and it wrote no `group.settings` audit row for the settings it applied.

### Alternative C — portal doors onto Studio's own acts (chosen)

**Argument for.** Each admin group handler is split into an *act* — privilege, store call, audit — that returns what
happened instead of writing a response, and a thin response writer. `/admin/groups` and the portal both call the act and
word the answer their own way. There is then one authorization story, which answers §4a's reason rather than overriding
it.

**Argument against.** It touches Studio's handlers to add a portal feature; a regression there is a regression in the
screen every organisation uses. Mitigated by the handlers' answers being unchanged and by the existing admin and console
suites, which run against them.

## 3. Counterarguments to the preferred option

**A portal write door is a larger attack surface than a portal read door, and it takes `generateToken` tokens, which
`/admin` deliberately does not (ADR-015 §4).** True, and that is the point of the owner's decision: those tokens are what
ArcGIS clients hold. What keeps it safe is that a `generateToken` token on the portal carries the same principal and the
same privileges as on `/admin` — it is the *door* §4 closes, not the authority — and each door below refuses a cookie
sent from another site (`CrossSiteByCookie`), as the portal's item writes do.

**ArcGIS's parameters do not map one to one.** `update` is a patch and Studio's settings are a replace; neither join flag
is *ask and be approved*, which Studio refuses; `addUsers` names two lists. Each is resolved in §5 and the inferred
ones are labelled.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The doors exist and answer ArcGIS's shapes | `createGroup` → `{success, group}`; `update`, `delete`, `join`, `leave` → `{success, groupId}`; `addUsers` → `{notAdded}`; `removeUsers` → `{notRemoved}` | ArcGIS REST API reference, *Create Group*, *Update Group*, *Delete Group*, *Join Group*, *Leave Group*, *Add Users to Group*, *Remove Users from Group* (developers.arcgis.com/rest/users-groups-and-items/) |
| Each step is what Studio then reads | Made, changed, peopled, emptied and deleted through the portal, read back through `GET /admin/groups/{name}` after each | `PortalGroupWritesTests` (Conformance) |
| A role without Studio's privilege is refused as Studio refuses it | A `viewer`-typed member: `createGroup` refused naming `groups:create`; `addUsers` refused and nobody added; a plain member of the group refused `update` and `delete` | `PortalGroupWritesTests` |
| `portals/self` says `createGroup` exactly when `createGroup` works | Asserted for a creator and a viewer | `PortalGroupWritesTests.What_portals_self_says_…` |
| A real client works | ArcGIS API for Python against the local fixture: `gis.groups.create` (both join flags false, its default), `group.update(snippet, access)`, `group.add_users` (`notAdded` for an unknown name), `group.delete` — each answered as the API expects | Local fixture run, 2026-11-02 |

## 5. Decision

**Portal groups take writes, and every write is Studio's own act.** `AdminEndpoints.GroupActs` holds one act per write —
create, settings, delete, add or promote a member, remove a member, leave, join — each checking Studio's privilege,
calling `IGroupDirectory`, and writing Studio's audit row (now with `door: admin` or `door: portal`). `/admin/groups` and
the portal both call them.

1. **The doors.** `POST /sharing/rest/community/createGroup`, and under `community/groups/{id}/`: `update`, `delete`,
   `addUsers`, `removeUsers`, `join`, `leave`. A group is addressed by its 32-hex id and found among the groups the caller
   can see — every group for a caller with `admin:manageAllContent`, as `/admin/groups` lists them. A group the caller
   cannot see answers *Group '…' does not exist or is inaccessible*, the same as one that does not exist.
2. **Which act, which privilege.** `createGroup` → create (`groups:create`) then settings; `update` → settings (owner or
   manager, or `admin:manageAllContent`; `public` also needs `sharing:shareToPublic`, ADR-199); `delete` → delete (owner or
   `admin:manageAllContent`; never while protected); `addUsers` → add-member once per name, `users` as members and
   `admins` as managers (`groups:manageMembers`, and owner or manager); `removeUsers` → remove-member once per name
   (same); `leave` → leave (no privilege; not the owner, not from an administrative group); `join` → join (no privilege).
3. **Mapping that is INFERRED, listed for confirmation.**
   - *Neither `isInvitationOnly` nor `autoJoin`* is ArcGIS's *ask and be approved*, which Studio refuses (no request
     queue, D-67). A portal client asking for it gets **invitation**, the nearer closed policy, and the group it is
     answered with says `isInvitationOnly: true`. Refusing would refuse every `gis.groups.create` that did not name a
     policy, because the Python API sends both flags false by default. On `update`, `isInvitationOnly=false` alone leaves
     the present policy; `autoJoin=false` closes a self-join group.
   - `update` is a **patch over Studio's replace**: the present settings are read, the parameters sent are laid over
     them, and the whole set is written; an empty text field is cleared only with `clearEmptyFields=true`.
   - `addUsers` naming somebody already in the group **does not demote** a manager named in `users` or make the owner a
     member of their own group. `capabilities` on `update` that differs from what the group was made with is Studio's
     *immutable* refusal (ADR-036 §4c).
   - `addUsers` and `removeUsers` answer a refusal about the caller or the group as an error for the whole request, and
     a refusal about one name by listing that name in `notAdded` / `notRemoved`.
4. **What `portals/self` says** follows the acts: `portal:user:createGroup` where the role holds `groups:create`;
   `portal:user:joinGroup` for every signed-in member, since joining asks for no privilege; and, for
   `admin:manageAllContent`, `portal:admin:updateGroups` and `portal:admin:deleteGroups`, with
   `portal:admin:assignToGroups` where `groups:manageMembers` is held too (INFERRED: the nearest of ArcGIS's
   administrative group privileges).
5. **Joining is new in Studio as well.** Studio has offered the join policy *they add themselves* since ADR-036 §4g, and
   no act let anybody do it — D-67's shape. `IGroupDirectory.JoinAsync` adds the caller to a group whose policy is
   `self` and which they can find in their own organisation, in one statement; Studio's route is
   `PUT /admin/groups/{name}/membership`, the other half of the `DELETE` that leaves. A group findable and closed answers
   *invitation only*; one not findable answers *no group*, so joining is not a way to discover hidden groups.
6. **Left out.** `invite`: ArcGIS's invitation is a pending offer the invitee accepts; Studio has no invitation record —
   *invitation* there means an owner or manager adds you — so it does not map cleanly. `updateUsers` (changing a
   member's role in place), `reassign` (ownership transfer), `protect`/`unprotect` as separate operations, and the
   join-request queue (`pendingUsers`, `acceptJoinRequests`) are not built. Groups have no recycle bin, from either
   door.
7. **Studio offers the join — built 2026-11-03.** `GET /admin/groups` and `GET /admin/groups/{name}` carry `mayJoin`,
   worked out by the server as `mayManage` and `mayLeave` are: outside the group, policy `self`, the group visible to
   the organisation or the public, and the caller at home (a member of another organisation reaches a group only through
   ADR-204's admission). It reads the row the store already returned and decides only whether a button is drawn;
   `JoinAsync` stays the authority. Studio › Groups has the head's one control (ADR-255 §2) — **All groups**, **My
   groups**, **Can join** — after ArcGIS Enterprise's *My groups* / *My organization's groups*, the third because finding
   a group to join is why a member opens the page; a row the reader is outside says *not a member*, and *· can join*
   where they can. The act is on the group's page, in the head: **Join group** where `mayJoin`, **Leave group** where
   `mayLeave` (it had followed `standing` alone, so an outsider and a member of an administrative group were offered a
   Leave the server refused). An invitation-only group draws neither, and its Facts say *by invitation*; the Facts now
   say *not a member* to an outsider, where they said *a member*. Joining re-reads the page and puts the focus on Leave
   group, which stands where Join did; leaving a group the reader can still find stays on its page with the focus on
   Join group, and leaving any other returns to the list as before. The toast is the live region. The listing's
   sentence said *groups you own or belong to* while it carried the findable groups as well, and says *groups you are
   in or can find*. No row button: a row is itself a link to the page, and two targets in one row is the fault the
   phone layout removed.

## 6. Consequences

**Positive.** An ArcGIS script that makes a group, peoples it and shares into it runs. Studio and the portal cannot
disagree about who may change a group, because there is one check. The reverted `createGroup`'s two divergences — public
without `sharing:shareToPublic`, settings without an audit row — are gone. The self-join policy Studio offered is
honoured.

**Negative.** Studio's group handlers changed shape to serve a second door; a future change to an act changes both
surfaces at once, which is the intent and also the risk. The INFERRED mappings in §5.3 are guesses about intent where
ArcGIS's documentation is silent or where ours has no equivalent. ~~Studio's Groups screen has no *Join* button yet; the
route exists and the screen does not offer it.~~ **Built 2026-11-03 — §5.7.**

**The screen's review, 2026-11-03.** Rendered as a plain member at 1,280 × 800 and 390 × 844 (headless Chromium against
the local fixture) and measured rather than looked at. *Phone:* the three scope buttons are 44 pixels tall and share the
head's row with the title with no sideways scroll; Join group and Leave group are 44 pixels and the head's full width, in
the same place (12, 140, 366 × 44), so the head is 168 pixels before and after joining and nothing below it moves.
*Desktop:* the scope control sits beside the title; Join group is the head's one primary button at its right, Leave group
the head's quiet one in the same slot, and the head is 64 pixels before, after and with neither. *Labels:* "Join group"
and "Leave group", as Studio's other verbs are named; the count beside the search says *1 group you can join*. *Focus:*
on Leave group after joining and on Join group after leaving, measured in the browser, and on the pressed scope button
after a scope change, since the control is not redrawn. *An invitation-only group:* no button, and *not a member — its
owner or a manager adds members*. *Left as found:* a group row at 390 is 92 pixels whatever its standing — a member's
row measures the same — two over the phone layout's 90, which the existing test does not see because it measures an
owner's shorter row; and an outsider's tabs count *Members 0* where the member list is withheld rather than empty.

**State.** Nothing new in the catalogue: the portal writes the same `sharing_group` and `sharing_group_member` rows
Studio writes, and a join is a `sharing_group_member` row whose `added_by` is the member. Nothing held at runtime.

**Ports created.** None. `IGroupDirectory` gains `JoinAsync` and `GroupChange.InvitationOnly`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A `generateToken` token on the portal carries the same principal and privileges as a sign-in token (ADR-015 §4 closes a door, not an authority) | Held by the existing portal item writes |

## 8. Dependencies

**Depends on:** [ADR-036](ADR-036-groups.md) (groups, the two axes), [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md) §4a
(the mapping), [ADR-199](ADR-199-a-group-may-be-public.md) (public groups need the public-sharing privilege), [ADR-015](ADR-015-authentication.md) §4.

**Depended on by:** none yet.

## 9. Revisit triggers

- Studio builds an invitation record or a join-request queue: `invite` and *ask and be approved* then map, and §5.3's
  first INFERRED mapping is replaced.
- A portal client is found sending a group parameter this door passes over and depending on it.
- Any group write is added to `/admin/groups` without an act both doors call.

## 10. Dissent

None recorded. The ArcGIS veteran's review argued for opening the writes; §4a's author argued against a second
authorization story, and §5's design is that answer.
