# ADR-185 — Sharing is additive: an item's level and its groups both count

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-15, by owner decision. Asked, with Portal's rule and this server's described side by side, whether group sharing should be additive as Portal's is, the owner answered *"Portal gibi eklemeli olsun"*. [ADR-101](ADR-101-studio-is-walked-the-way-a-portal-user-walks-it.md) condition 2. |
| **Supersedes** | — (amends [ADR-036](ADR-036-groups.md) §4a and §4b: a group reaches its members at every level) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

An item here has one sharing level — `private`, `group`, `organization` or `public` — and the groups it is shared
with. Until this decision the groups counted only when the level was `group`: `LayerAccess.Evaluate` read them for
reading and `GroupConfersEditing` for editing under that one scope. So sharing into a group and setting the scope to
`group` were two acts, either of which alone read as done, and the console spent a tab — the group's Overview — on
listing shares that *reached nobody* and a button that set the scope. And a shared-update group could not edit an item
the whole organisation read.

Portal's sharing is additive: the level (owner, organisation, everyone) and the groups both count. An item shared with
the organisation and with a shared-update group is read by everybody in the organisation and edited by the group.

## 2. Alternatives considered

### Alternative A — additive, in the one rule both reading and editing read *(chosen)*

**Argument for.** Portal's model, which a Portal user expects; the two-step trap is gone rather than explained; it is
two conditions removed from `LayerAccess`, where every face already decides.

**Argument against.** The `group` scope becomes the same as `private` with groups.

### Alternative B — keep the single level

**Argument for.** Nothing to change.

**Argument against.** The two-step trap stays, and a group cannot edit an organisation-wide item. Not chosen.

## 3. Counterarguments to the preferred option

- **Widening editing is the dangerous direction.** A shared-update group now edits an item at any level — but only
  an item shared with *that* group; membership of one sharing-update group opens nothing else
  (`SharedUpdateTests.A_shared_update_group_edits_what_is_shared_with_it_at_every_level`, each scope, both halves).
- **Reading widens too**, for a private item shared with a group — to that group's members, which is what sharing it
  with them asked for. Nothing becomes readable to anybody outside the groups it is shared with.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A private service shared with a group is read by its members and not by a stranger | `ImageryGroupSharingTests.A_private_service_shared_with_a_group_answers_its_members_without_its_scope_changing` | local fixture, 2026-10-15 |
| Reading is the level or the groups, and the editing set does not widen it | `SharedUpdateTests.Reading_is_the_level_or_the_groups_and_editing_does_not_widen_it` | unit |
| The group screens say every share reaches its members | `GroupsPageTests` | console suite |

## 5. Decision

5.1 **`LayerAccess.Evaluate` reads an item's groups whatever its scope**: a signed-in caller in any group the item is
shared with reads it. An anonymous caller is in no group.

5.2 **`LayerAccess.GroupConfersEditing` likewise**: a shared-update group edits what is shared with it at every level.

5.3 **The `group` scope stays** as a stored word: it is `private` with groups, and the server's refusal of a
group-scoped item with no group is what the Share dialog relies on. No migration.

5.4 **The console stops counting shares that reach nobody**: the group's Overview says how many items its members read,
the Content and Add item lists show each item's own level, and the *Set scope to group* button is gone.

## 6. Consequences

**Positive.** One act shares; a group edits an organisation-wide item; the model is Portal's.

**Negative.** A private item's readers are now its owner and its groups' members — which an owner who shared it with
a group meant, and which nothing told them was not the case before except the console.

**Ports created.** None. **State.** None.

## 7. Assumptions this decision rests on

None new.

## 8. Dependencies

**Depends on:** [ADR-036](ADR-036-groups.md), [ADR-018](ADR-018-authorization-and-roles.md) §3b.

**Depended on by:** —

## 9. Revisit triggers

- A request to share with a group for reading *without* the item becoming readable to it, which Portal does not offer.

## 10. Dissent

None recorded.
