# ADR-220 — An item may be shared with every organisation on the server

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the reading rule, which is one line in the one place reading is decided · `MEDIUM` for the search, which is Studio's and not yet the ArcGIS REST search's (§5.5) |
| **Decided** | 2026-10-26, by owner decision — *"Belki public olmasın ama tenantlarla olsun istiyorum. Olamaz mı?"*, then *"Ya da search the my organization gibi, all tenants olabilir"*, then *"Başla"* to the proposal below: a sharing level and a search beyond one's own organisation, together. The privilege's default and the search's shape are INFERRED (§5). |
| **Supersedes** | [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) §5c's *"no 'share with tenant X' scope — ADR-018's four scopes stay four"*, for this one level |
| **Superseded by** | — |

---

## 1. Context

Since [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) an item reaches another organisation only through a
group that admits it ([ADR-219](ADR-219-two-organisations-partner-before-their-groups-mix.md) adds partnerships to that).
Sharing levels are Portal's four: the owner, groups, the organisation, everyone. *Organization* means one's own
organisation; *everyone* means the internet. The owner asked for the level between: every organisation on this server,
but not the public.

ArcGIS has no such level. ArcGIS Online is one deployment of many organisations, and an item reaches other
organisations there by being public — found by searching beyond *My organization* — or through a group. The owner's
second sentence is that search.

## 2. Alternatives considered

### Alternative A — A fifth level, *all organizations on this server* (chosen)

One value, read by the one function that decides reading (`LayerAccess.Evaluate`): any signed-in caller, of this
organisation or another, and no anonymous one.

### Alternative B — A server-owned group every member is in

ArcGIS clients would see an ordinary group share and keep it. But every organisation's group list would carry a group
nobody owns, whose membership is everybody — a concept to explain, and a membership table to keep in step with every
member made anywhere.

### Alternative C — Search alone

Searching beyond one's organisation finds only what one may read; without a level, that is public items and groups.
The owner's first sentence is exactly what it would not give.

## 3. Counterarguments to the preferred option

- **ArcGIS clients know four levels.** They are told `org` for this one (§5.3). A client that shares the item to the
  organisation would narrow it, unless the server keeps it — which it does, and which is a server quietly declining to
  do what a client asked, in the safe direction only for the client's reader and not the owner's intent.
- **It is a route between organisations that no group records**: *what can JV1 see of ours?* now has two answers, the
  groups that admit it and this level.

## 4. Evidence

The reading decision is made in one place (`LayerAccess.Evaluate`), after the floor has narrowed the rows to the host's
organisation; so the level cannot reach a row of another organisation at all, and is read at the item's own host by an
outsider — the route ADR-204 already tests. Mapped by an Explore pass of 2026-10-26 before any line was written.

## 5. Decision

1. **`SharingScope.AllOrganizations`**, stored and written as `allorganizations` (the form every other level takes,
   the enum's name in lower case). Migration 108 widens the check on `item.sharing`, the one column that holds a level since migration 87. Read by any
   signed-in caller — an outsider reaching this host from another organisation included — and by no anonymous caller.
   A system service is already every organisation's and refuses it (`400`).
2. **Its own privilege, `sharing:shareToAllOrganizations`**, as public sharing has its own: it takes an organisation's
   content past its own members, and each organisation decides who of its members may do that by giving the privilege
   to a custom role. **INFERRED:** no built-in role but the administrator holds it; the *creator* user type's ceiling
   allows it.
3. **ArcGIS clients see `org`.** A share from an ArcGIS client to the organisation (`org=true`, Pro's `account=true`)
   keeps an item already shared with every organisation where it is, rather than narrowing it.
4. **Found from home**: `GET /admin/search/elsewhere?q=` lists, for a member at their own organisation's host, the items
   of other organisations they may read — shared with every organisation, public, or shared into a group of theirs
   that admits other organisations — each with its organisation and the address to open it at, where the member reads
   as an outsider. Studio's search offers *My organization / All organizations* over it.
5. **Not built: the ArcGIS REST search beyond the organisation.** `/sharing/rest/search` stays the host's
   organisation's. Answering other organisations' items there would also need `/sharing/rest/content/items/{id}` to
   answer them at a host that is not theirs, with addresses at theirs; until then an ArcGIS client would find an item
   it cannot open. **INFERRED** that Studio's search is enough for the owner's request.

6. **A member of any organisation is somebody at every host** — found by this decision's own test, which a member of
   another organisation could not pass: their token was refused (`498`) wherever no group of that organisation
   admitted them, because the session was looked up under the floor and the floor hides such a member
   ([ADR-204](ADR-204-one-deployment-holds-several-organisations.md) §5e item 7). A level every organisation reads
   needs every organisation's members recognised, so **a session is looked up on the store's own pool** — by its
   secret, never listed — and the member is then an outsider there as before: no privilege, no role, their groups
   only (`Authorization.AsOutsider`). Service accounts' API keys are not changed: a machine stays its organisation's.
   **And an outsider is told their own organisation's id** in the portal's user document, as ArcGIS Online tells a
   member of another organisation, where they were told this one's and a client could take them for a member of it —
   true of a group's outsiders since ADR-204, found in the same probe.

## Conditions

1. **An item at this level is read by another organisation's member at its host and by no anonymous caller**, and an
   item at the organisation's level is not — tested by `SharedWithEveryOrganisationTests`. **DISCHARGED 2026-10-26.**
2. **Sharing at it takes its own privilege**, tested by a publisher refused it. **DISCHARGED 2026-10-26** by `SharedWithEveryOrganisationTests`.
3. **The search beyond the organisation lists what may be read and nothing else**, tested by the organisation-level
   item absent from it. **DISCHARGED 2026-10-26** by `SharedWithEveryOrganisationTests`.
4. **An ArcGIS client's share to the organisation keeps the level**, tested through `shareItems`. **DISCHARGED
   2026-10-26** by `SharedWithEveryOrganisationTests`.
5. **The console** offers the level in every share dialog where the server holds several organisations and the reader
   holds the privilege, says what it means, and offers the search toggle — built by the ux-designer agent.
   **DISCHARGED 2026-10-26**: every share control, the badge, and Studio's *My organization / All organizations*
   search; an outsider's item page asks nothing it is refused and offers no settings; tested by
   `SharedWithAllOrganizationsTests`.

## 6. Consequences

**Positive.** What the owner asked: content every organisation's members read without putting it on the internet, and
a way to find it.

**Negative.** A level ArcGIS does not have, shown to its clients as another, and a second route between organisations
beside groups.

**State.** In the catalogue: the value `allorganizations` in `item.sharing`, and the privilege in
`role_privilege` where a custom role holds it. At runtime: nothing new.

## 7. Assumptions this decision rests on

- Every organisation on one server is trusted to read what another chose to share with all of them — true of the
  owner's deployments (a company and its joint ventures), and the reason the level is a privilege each organisation
  grants.

## 8. Dependencies

[ADR-204](ADR-204-one-deployment-holds-several-organisations.md) §5b–5c, [ADR-018](ADR-018-authorization-and-roles.md)
(the levels), [ADR-036](ADR-036-groups.md) (groups).

## 9. Revisit triggers

- An ArcGIS client is seen narrowing the level in a way §5.3 does not catch.
- The ArcGIS REST search is asked to reach other organisations (§5.5).

## 10. Dissent

None recorded.
