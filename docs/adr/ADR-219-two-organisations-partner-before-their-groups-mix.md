# ADR-219 — Two organisations partner before their groups mix

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the shape, which is ArcGIS Online's · `MEDIUM` for what this version leaves out (§5, *Not built*) |
| **Decided** | 2026-10-26, by owner direction — *"devam et"* to the next item of [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) §5e item 17, *partnered collaboration*, after *"Multi tenancy soruları için Agol dokümanlarına bakabilirsin"*. Every choice below that ArcGIS Online's documents do not settle is INFERRED and listed in §5. |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

Since [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) a group can admit members of other organisations
(`PUT /admin/groups/{name}/tenants`). That switch is all there is: the group's organisation names any member of any
other organisation, `key\name`, and the member's own organisation is not asked. ADR-204 §5e item 17 set this beside
ArcGIS Online and listed it as owed — item 17.2, *who may join another organisation's group*, and item 17.3, *partnered
collaboration*.

ArcGIS Online, from its public documents (*Understand collaborations*, *Create a partnered collaboration*, *Manage
partnered collaborations*, doc.arcgis.com, read 2026-10-26):

- An administrator invites another organisation by its address; that organisation's administrator accepts or
  declines. *"Each organization continues to own and manage its own content while allowing the other to view and
  contribute."* At most 20 partnered collaborations an organisation.
- Each side chooses whether the partner may **search for its members** — the inviter when inviting, the invitee when
  accepting — and may change it later.
- A group may be limited to *"members from partnered organizations only"*, or allow members of any organisation.
- Each organisation names up to 20 **collaboration coordinators**, who create collaboration groups and invite their own
  organisation's members into them.
- **Suspended**: *"new members from partnered organizations cannot be invited to collaboration groups"*, and *"existing
  group members and roles remain unchanged."* Either side resumes it.
- **Ended**: in a group that *"only allows members of partnered organizations"*, the partner's members and content *"are
  automatically removed"*; in a group that allows any organisation, the content stays and *"any group managers that are
  from collaborating organizations are demoted to group members."*

## 2. Alternatives considered

### Alternative A — Keep the open switch alone

What exists. Nothing asks the member's organisation; a group either admits everybody or nobody from outside. It is
ADR-204's interim and its §5e item 17 says so.

### Alternative B — A partnership between two organisations, and a group limited to partners (chosen)

ArcGIS Online's model, as above, built on the switch that exists: a group admits **nobody**, **its organisation's
partners**, or **any organisation**.

### Alternative C — A per-member consent ("join external groups" privilege)

ArcGIS Online's other half (§5e item 17.2): a member's own organisation grants the privilege to join groups elsewhere.
Narrower than B and composes with it; not built here (§5).

## 3. Counterarguments to the preferred option

- **A partnership table is state that spans two organisations**, the first such row. It cannot sit under the floor's
  usual one-tenant rule; its policy shows a row to either party (§5), which is a new shape of policy.
- **Coordinators are left out.** Without them the partner organisation consents once, at the organisation level, and
  not member by member; the group's own managers still choose whom of the partner's members to add. That is less than
  ArcGIS Online gives the partner.

## 4. Evidence

The three ArcGIS Online documents cited in §1. Nothing measured: a partnership is read only when a group's membership
changes and when candidates are listed, never on a request that reads data.

## 5. Decision

1. **A partnership** joins two organisations. An organisation's administrator invites another by **its host name or its
   key** (INFERRED: ArcGIS Online invites by the organisation's address; a key is accepted too, for an organisation
   whose administrator gave it). It is `pending` until the other's administrator **accepts** (`active`) or **declines**
   (removed). Either side **suspends** it (`suspended`), **resumes** it, or **ends** it (`ended`, kept to be read, and
   either side deletes it then). At most **20** that are not ended, per organisation. One pair holds one partnership that
   is not ended.
2. **Each side says whether the partner may find its members**: `sharesMembers`, set by the inviter when inviting and
   by the invitee when accepting, changed by either for its own side at any time. Where it is granted, a group of the
   partner that admits partners lists this organisation's enabled members among its candidates, named `key\name`.
3. **A group admits nobody, its organisation's partners, or any organisation.** The switch gains `partnersOnly`. A group
   limited to partners takes a member of another organisation only while the two organisations' partnership is
   `active`. Limiting a group that holds members of organisations that are not partners is refused (`409`), naming how
   many; they are removed first (INFERRED: ArcGIS Online's documents do not say).
4. **Suspended**: nobody of the partner is added to a group limited to partners; who is in stays.
5. **Ended**: in either organisation's groups limited to partners, the other's members are removed; in groups that admit
   any organisation, the other's managers become members. The members of a group cannot share into another
   organisation's group (ADR-204 §5e item 8 — an outsider may do nothing at that host), so there is no partner content
   to remove.
6. **The row is either party's**: its policy, `partnership_parties`, shows it to the inviter's and the invitee's
   requests and to the root pool, and to nobody else. Not named `tenant_floor`: that name marks a table carrying
   `tenant_id`, and a tenant's purge walks every table that has it — found when the first purge after migration 107
   failed on the missing column. Ending a partnership changes membership in both organisations'
   groups, so that one statement runs on the root pool, narrowed to the two.
6a. **Found by the ux-designer agent's pass, 2026-10-26, and changed the same night:**
   - **A refused limit changed the group anyway.** The switch was written before the stranger count, and the count
     read members of other organisations through the floor, which shows them only while the group admits other
     organisations — so a closed group counted none, was opened to every organisation, and only then refused. The
     count now runs first, on the root pool narrowed to the group's organisation; tested.
   - **Since when** a partnership has its status is kept apart from its last change (`statusChangedAt`), so a change
     of visibility does not move *partners since*.
   - **What ending would do is asked first**: `GET /admin/partnerships/{id}/ending` lists each affected group, whose
     it is, and how many would leave it or stop managing it — ArcGIS Online shows the affected groups before it ends
     one.
7. **Who acts**: the organisation's administrator, as ArcGIS Online's *manage collaborations* privilege is an
   administrator's. The system administrator does not partner organisations; each organisation does.
8. **Not built**: collaboration coordinators (§3); the per-member *join external groups* privilege (Alternative C,
   ADR-204 §5e item 17.2); an e-mail with the invitation; a CSV of invitations; ArcGIS Online's *distributed*
   collaboration with ArcGIS Enterprise. A partnership is between organisations of one deployment.

## Conditions

1. **A partner's member is added to a group limited to partners only while the partnership is active**, tested by
   adding one while it is pending, active, suspended and ended. **DISCHARGED 2026-10-26** by `TwoOrganisationsPartnerBeforeTheirGroupsMixTests`.
2. **Ending a partnership removes the other's members from groups limited to partners and demotes its managers
   elsewhere, in both organisations**, tested from each side. **PARTLY DISCHARGED 2026-10-26** by `TwoOrganisationsPartnerBeforeTheirGroupsMixTests`, from the side that ended it; the other side's groups are cleaned by the same statement and are not yet tested. **DISCHARGED 2026-10-27** — `Ending_from_the_invited_side_cleans_both_organisations_groups`: the invited organisation ends it, with each side holding the other's member in its partners-only group and the other's manager in its open group; afterwards both partners-only groups have lost the other's member and both open groups keep the other's as a member, not a manager. Every addition was asserted to have been made first.
3. **A third organisation reads no partnership it is not party to**, tested at its host. **DISCHARGED 2026-10-26** by `TwoOrganisationsPartnerBeforeTheirGroupsMixTests`: it lists none and changing one answers 404.
4. **The console** lets an organisation's administrator invite, accept, decline, suspend, resume and end, set its
   members' visibility, and limit a group to partners — built by the ux-designer agent, as every screen is. **DISCHARGED 2026-10-26**: Studio › Organization › Settings › Collaborations (*Partnered collaborations*), with the
   ending's preview in its confirmation, and the group's three-way *who can be added*; tested by
   `PartneredCollaborationsScreenTests`.

## 6. Consequences

**Positive.** The member's organisation now consents before a group of another organisation may take its members, and
can withdraw that consent; a group can be kept to partners rather than open to every organisation on the server.

**Negative.** A second kind of cross-organisation state, and a policy that names two tenants. The open switch stays —
a group that admits any organisation still takes anybody — because ArcGIS Online keeps it too.

**State.** In the catalogue: the `partnership` table (one row a pair of organisations, kept after it ends until either
deletes it) and `sharing_group.admits_partners_only`. At runtime: nothing; every check reads the row, so every node
agrees at once.

## 7. Assumptions this decision rests on

- ArcGIS Online's partnered collaboration is the model a GIS administrator expects (the owner's direction of
  2026-10-25).

## 8. Dependencies

[ADR-204](ADR-204-one-deployment-holds-several-organisations.md) §5c (a group admitting other organisations), §5e items
16 (names `key\name`) and 17.

## 9. Revisit triggers

- An owner asks for coordinators, or for the partner's say over each member.
- A deployment needs a partnership with an organisation on another server.

## 10. Dissent

None recorded.
