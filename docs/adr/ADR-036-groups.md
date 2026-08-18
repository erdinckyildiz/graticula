# ADR-036 — Groups, and the two axes an authorization answer now has

- **Status:** `ACCEPTED WITH CONDITIONS`
- **Confidence:** `MEDIUM` — the owner settled that groups exist and what a privilege is; the three
  questions [Q-112](../open-questions.md) left open are answered here and those answers are ours.
- **Date:** 2026-08-18
- **Supersedes:** [ADR-018](ADR-018-authorization-and-roles.md) §3b's deferral of groups, and
  answers [Q-112](../open-questions.md).
- **Depends on:** [ADR-035](ADR-035-role-privileges-are-editable.md) — every group operation is a
  privilege, and there was nowhere to put one until roles became editable.

## 1. What the owner decided, and what they left to us

Verbatim, 2026-08-18:

> *"studio tarafında gruplar olacak. grup yaratabilmek de bir yetkiye bağlı. … gruba kullanıcılar ve
> nesneler atanabilir. şu an için grupla paylaşılabilecek yegane şey servisler. harita mantığı
> oturunca onlar da gelecek. başka şeyler de ileride gelebilir. … bir grubun sahibi olmayabilirsin
> ama yönetici olarak atanırsan, sen de grupta yetkili işlemler yapabilirsin."*

Settled by them:

1. **Groups exist, on the Studio side.** Already recorded twice — their words, and
   [ADR-034](ADR-034-server-and-studio.md) §6's *"Q-112 (groups), when answered, lands in Studio."*
2. **Creating one is a privilege** — `groups:create`, built with ADR-035.
3. **Members and objects are assigned to a group**, and for now the only object is a service.
   Maps follow when the map model settles; other things may follow after.
4. **A manager who is not the owner holds the group's operations inside that group.**

Left open by [Q-112](../open-questions.md), and decided below: whether a group scope exists on the
sharing axis at all (§4a), whether an update capability belongs to the *group* or to each share
(§4b), and whether it is immutable (§4c).

## 2. Q-112's three answers

### 4a. A group is a fourth sharing scope, and in v1 it confers reading only

**Yes to the scope.** `private`, `organization`, `public`, `group` — Portal's four, and the owner's
requirement is the one ADR-018 §3b named as the reason it might be wanted: a private thing readable
by the people you name and by nobody else. Esri's own word for the result is *semiprivate*.

**Reading only, in v1, and that is a scope decision rather than a hedge.** ADR-018 §3b's rule is
that *sharing governs reading* — editing is `features:edit`, which is a privilege. So a group share
makes a service readable by that group's members. **Editing through a group is not built**, because
the owner's requirement did not ask for it and §82's question has no answer yet: no concrete
problem needs it. What §4b decides is where the capability *would* live, so that adding it later is
an addition rather than a redesign.

**The check constraint has to be widened, and that is a migration.** ADR-018 §3b said the scope
column *"takes a string so adding `group` later is a value rather than a migration"*, and three
tables carry `check (sharing in ('private','organization','public'))`. That claim was corrected on
2026-08-18 before this decision needed it; the cost is one expand-only migration.

### 4b. The update capability is a property of the group, not of each share

**This is Q-112's middle question and it carries the whole design.** Two shapes were available:

- **Per group** — a group is created with an update capability (*none*, *their own items*, *all items
  shared with the group*) and every share into it inherits that. ArcGIS's shape, and the owner's
  *"portal tarafının grupları gibi"*.
- **Per share** — each item shared with a group carries its own permission for that group.

**Per group, for three reasons in descending order of weight.**

1. **It is the unit people reason in.** An operator says *"the planning team can edit these"*. They
   do not say *"this layer is editable by planning, and that one is not, and the third one is
   editable by planning and readable by survey"*. A model that cannot be stated in one sentence is
   one that gets configured wrong.
2. **Per share multiplies the number of authorization facts by the number of shares.** With the
   group as the unit there is one fact per group; with the share as the unit there is one per
   (item, group) pair, and each must be read on every request that touches the item. That is a
   different performance shape and a much larger surface for a mistake to hide in.
3. **The owner named the reference.** Deviating from it here would be inventing a fourth thing when
   three of ours already match; [ADR-020](ADR-020-admin-console-and-service-status.md) §5c's
   experience is that a shape taken from the named reference survives contact with the owner and an
   invented one does not.

**The cost, stated plainly:** a group cannot hold two items with different editability. An
organisation that wants that makes two groups. That is exactly what ArcGIS forces and it is the
price of the sentence in reason 1.

### 4c. The capability is fixed when the group is created

**Immutable, and it reads as a limitation until you write down what changing it would do.** Flipping
a group from *view* to *update all items* would, in one click, make **every item already shared with
it editable by every one of its members** — a widening nobody asked for at the moment they asked for
something else, applied retroactively to shares made under different terms. Esri refuses it for that
reason and the refusal is a safety property rather than an omission.

**So the column is written at creation and every write to it is refused.** Changing the capability
means making a new group and moving the shares, which is laborious and is *visible* — each share
becomes an act somebody performed under the new terms.

**One narrowing we allow and Esri does not appear to:** a group may be created with capability
*none* and that is the default. A group whose only purpose is *these people may read this* should not
have to declare an editing posture it will never use.

## 3. The second axis, which ADR-035 condition 4 required deciding now

**An authorization answer for a group operation has two axes, and it has to from the start.**
ADR-035 condition 4 says so in terms: *"a second axis added to an authorization decision later is
the change most likely to be got wrong quietly."*

| Axis | What it answers | Where it lives |
|---|---|---|
| **Role** | *May this principal do this kind of thing at all?* | `role_privilege`, ADR-035 |
| **Membership** | *May they do it to **this** group?* | `sharing_group_member.membership` |

So `groups:manageMembers` does not mean *manage anybody's group*. It means *manage the members of a
group you own or manage*. The membership axis takes two values:

- **`member`** — belongs to the group; reads what is shared with it.
- **`manager`** — belongs, and holds the group's operations inside it without owning it. The owner's
  *"yönetici olarak atanırsan"*.

**Ownership stays distinct from both, and that is ArcGIS's rule as well.** Only the owner — or an
administrator — may delete the group or transfer it. A manager may add and remove members and share
items into it. **This matters because it is the difference between delegating work and delegating
control**, and a model that conflates them makes every helper a potential deleter.

**`admin:manageAllContent` overrides the membership axis**, as it does every other ownership check
in this server. Without that a group whose owner has left is a group nobody can administer, which is
the shape [ADR-015](ADR-015-authentication.md) §6c already had to solve for services.

## 4. The schema

Three tables, and the naming avoids a reserved word rather than quoting it everywhere:

- **`sharing_group`** — `id`, `name` (unique, case-insensitively), `title`, `description`,
  `owner_principal_id`, `item_update` (`none` | `ownItems` | `allItems`, §4c immutable),
  `created_at`, `updated_at`.
- **`sharing_group_member`** — `(group_id, principal_id)`, `membership` (`member` | `manager`),
  `added_at`, `added_by`.
- **`sharing_group_item`** — `(group_id, service_id)`, `shared_at`, `shared_by`.

**`group` is a reserved word and `"group"` would need quoting in every statement.** This project has
already been bitten by a class of defect that lives in exactly that kind of care requirement, so the
table is named for what it is instead. `item` rather than `service` in the third table's name because
the owner said other things will follow — the column is `service_id` today, and a second column
beside it is a smaller change than a renamed table.

**`sharing_group_item` is a join table and not a scope on the service.** A service's `sharing` column
says `group`; *which* groups is this table. The alternative — a group id on the service — would allow
exactly one, and the requirement is a set.

### 4d. Built and measured, 2026-08-18

Migration 26: three tables, two indexes, and the fourth scope on all three checks that carry one.
`IGroupDirectory` with eight endpoints. The read path resolves the caller's groups on the statement
that already reads their roles, and the item's groups on the query that already reads its service.

**ADR-036 condition 1, measured end to end** — one service at `group` scope, shared with one group,
one member in it and one member out:

| Caller | `GET .../tr_yer/FeatureServer` |
|---|---|
| anonymous | **404** |
| a member who is not in the group | **404** |
| **a member of the group** | **200** |
| an administrator | 200 |

And the assertion that keeps §4a honest: the same member's `addFeatures` → **403**. A group share
confers reading and nothing else.

**Condition 2, measured** — a member holding `groups:manageMembers` tried to add somebody to a group
they neither own nor manage and was refused, with the refusal naming why: *"The privilege to manage a
group's members is not the privilege to manage every group's members."*

**Two defects found by that measurement, and both are the same defect as each other.**

- **Five parsers of the sharing scope, and adding a fourth value left all five behind.** Setting a
  service to `group` answered 400 — `TryReadScope` did not know the word, nor did the four other
  parsers in the host and the store. The service stayed public, and *every* caller in the table above
  read it, which is how a test of this shape passes while proving nothing.
- **The user-type ceiling withheld all four group privileges.** A ceiling that does not list a
  privilege withholds it, so `groups:create` granted to a role was refused for every member whose
  user type is `creator` — which is every member who is not unrestricted. The refusal was correct and
  said so: *"your role grants it and your user type does not permit it."*

**Both are [D-71]'s pattern for the second and third time in one day:** adding a value to an
enumeration leaves every reader of the old set silently wrong, and the compiler cannot find them
because the old set still compiles. **One of them was caught by a test that already existed** —
`The_sharing_scopes_the_code_knows_are_the_ones_the_check_constraint_allows`, which asserts the schema
admits every scope the code knows. Its premise had to be corrected (it read migration 5, and
migration 26 is where the answer now lives) and it is the only thing in the repository that noticed.
The other four parsers and the ceiling were found by running the feature.

### 4e. The screen's shape, from the reference the owner sent

Owner, 2026-08-18: *"add member asks for name. why not search a user and add user from the list"* —
followed by two screenshots of Portal's Groups, and *"looks good"*.

**The objection was right and the cause was an authorization one.** Adding somebody to a group needs
to know who exists; reading the member directory needs `admin:manageMembers`. So a publisher who owns
a group could not fill a picker, and the console asked them to type a name from memory — where a typo
is a 404 about a member who does exist. **The repair is a narrower endpoint, not a wider privilege:**
`GET /admin/groups/{name}/candidates` returns *names only*, to somebody who already owns or manages
that group, excluding whoever is already in it and every disabled account. Granting
`admin:manageMembers` — which carries creating accounts, changing roles, disabling and deleting — so
that a dropdown could be populated would have been [D-20](../architecture-debt.md)'s complaint in
reverse.

**The service picker needed no new endpoint.** `/content/layers` is what any signed-in member may
read about their own things, and it is the *right* set rather than merely the available one: you share
what you published. It is per layer and a group is shared a service, so three layers of one service
offer one choice.

**Taken from the screenshots, beyond the pickers:**

- **Leave group**, which did not exist in any form. A member could be removed by a manager and could
  not walk out — which makes joining a group something done *to* somebody. Absent for the owner, whom
  the store refuses to remove, rather than present and refusing.
- **Where you stand, said in words** — their *"You are a member"*. Ours says *"You are a member of
  this group"* or, for an owner, why they cannot leave.
- **Search**, over name, title, description and owner. Their list showed *1-60 of 71*; somebody
  looking for a group remembers one of those four fields and not which one.

**Deliberately not taken, because each is a decision rather than a shape**, and inventing an
authorization axis quietly is what §3 spent a section refusing to do:

| Theirs | Why it is not here |
|---|---|
| **Viewable by: Organization / Group members / Everyone** — a group has its own visibility, separate from what is shared with it | A fifth thing to be visible, on an object that is itself a visibility mechanism. It answers *who may discover that this group exists*, which is a real question and a different one from *who may read its items*. Recorded as [Q-118](../open-questions.md) |
| **Members list: visible to all group members / to the owner only** | Whether members may see each other. Ours are always visible to members, which is the more open of the two and was not chosen — it fell out. [Q-118](../open-questions.md) |
| **Contributors: all group members / owner only** — who may add items to the group | Distinct from §4b's `item_update`, which is about editing what is *already* shared. Who may *contribute* is a third setting and ours is *owners and managers* by construction. [Q-118](../open-questions.md) |
| **Featured groups**, **My organization's groups** as separate tabs | Both are discovery over groups you are not in, which needs the visibility above to mean anything |
| **Special groups** — Shared Update, Distributed, Administrative, Organization Settings | Product features of theirs that have no counterpart here, and adopting a tab because it exists is what §82 refuses |

### 4f. What a design review found, 2026-08-18

The owner asked for the screen to be read by a UX reviewer. Nine findings, all verified against the
source before being acted on, and **the headline was one fact showing up in five places: the screen
was written and its stylesheet was not.**

- **Two classes the markup used do not exist.** `tr.pick.on` — which both this renderer and Roles'
  write onto the open row — had no rule, so **clicking a row changed nothing visible**, and the panel
  it opens is below the fold on a 1366-wide window. `class="head"` had none either, so the title
  rendered at browser default against every other page's, and the editor's buttons stacked instead of
  forming a row. Both repairs fix Roles too.
- **`prompt()` was not a style problem, it was a data-loss one.** Creating a group used two chained
  dialogs; the name was sent as the title as well and the description was never sent — and **there is
  no endpoint to set either afterwards**, so two of a group's four fields could not be filled from
  this console at all, and the Description column was structurally always empty. The capability was
  free text against a case-sensitive enum, and a refusal discarded both prompts' input. A browser that
  has offered *"prevent this page from creating additional dialogs"* made New group silently do
  nothing. Replaced with an in-page form using the panel pattern already on the screen.
- **The two-step trap is now shown rather than warned about.** A service reaches a group's members
  only when its own scope is `group` as well; that was prose in two places — a *per-service* fact
  delivered as a *per-screen* caveat, which the operator then carries to another page and checks one
  at a time. `ItemsAsync` already joined `service`, so the scope is one more column: each share reads
  *reaching members* or *inert here*, and the heading says *n of N reaching members*. Both paragraphs
  deleted.
- **A standing is not a state, so it stopped being a badge.** `owner` / `manager` / `member` all fell
  through to the same grey `pill` with no colour family and no icon — three values, one visual, and
  the border and dot carried nothing the word did not. A fourth meaningless pill also weakens the ones
  that do mean something. Now weight: `owner` and `manager` bold, `member` muted.
- **The filtered-empty state rendered nothing at all.** The empty branch tested the unfiltered count
  while the rows came from the filtered list, so a search matching nothing left a blank body under a
  live header. Three states now, and the *"nothing shared yet"* one carries the two-step rule, which
  is where the operator is standing when it applies.
- **The picker's filter was wired to `change`, so typing in it did nothing.** A `<input type=search>`
  reports `change` on blur or Enter; `#groupFilter` twenty lines away was already on `input`.

**Two things the review recommended and this decision declined.** Adopting the reference's
*Overview / Content / Members / Settings* tabs: rejected on subject rather than scale — the whole
point of the screen is the relation between *who is in it* and *what they can therefore read*, and
tabs would hide half of the comparison. The two tables are side by side instead. And *Viewable by*
with a lock icon: that is [Q-118](../open-questions.md), and copying it to match a screenshot is
exactly the invented-concept failure the memory of this project warns about.

**One finding was a promise with nothing behind it:** the standing line said *"Transfer it or delete
it"* and there is no transfer — no route, no method. Cut until there is one.

## 5. Consequences

- **The sharing check gains a fourth value** on `layer`, `service` and `system_service`. Expand-only.
- **The read path gains a second question.** Resolving whether a principal may read a
  `group`-scoped service means asking whether they are in a group it is shared with. That is a query
  per request unless it is held, and it is held — the same shape and the same argument as
  `PostgresRoleGrants`.
- **A member's removal gains a third owned thing**, which [ADR-015](ADR-015-authentication.md) §6c
  wrote its disposition around *owned things* precisely to accommodate: transfer moves a group's
  ownership, delete takes the group with the member. §6c said this would be an addition rather than a
  redesign, and it is.
- **Studio gains a Groups screen**, per ADR-034 §6.
- **`groups:shareTo` becomes enforceable**, which it was not when ADR-035 defined it.

## 6. Conditions

1. **DISCHARGED 2026-08-18**, measured in §4d: 404 / 404 / 200 / 200 across anonymous,
   non-member, member and administrator, with the member's `addFeatures` refused.
   **A group share is proven to confer reading and nothing else.** A member of a group a service is
   shared with can read it; the same member cannot edit it without `features:edit`; a non-member
   cannot read it. All three in one test, because the middle one is the assertion that keeps §4a
   honest.
2. **DISCHARGED 2026-08-18** — `GroupDirectoryTests` covers owner, manager, plain member,
   outsider and administrator against one group, and the end-to-end run confirmed the refusal
   through the API. **The membership axis is tested for the direction that matters** — that `groups:manageMembers`
   does not let somebody manage a group they neither own nor manage. A privilege that turns out to be
   global is the escalation this ADR would have introduced.
3. **DISCHARGED 2026-08-18**, and in the stronger form: there is no write path to change it at
   all, so the immutability is an absence rather than a refusal. Tested by creating a group at each
   of the three values and reading it back. **The capability is immutable, and the refusal says
   why.** Asserted by trying to change it, in a
   test that names the retroactive widening it prevents.
4. **DISCHARGED 2026-08-18.** The holdings preflight counts them, transfer moves ownership and
   makes the receiver a manager, and delete removes them — which it must, because
   `owner_principal_id` is `on delete restrict` and the disposition could not otherwise succeed.
   **Removing a member disposes of their groups**, both ways, extending
   [ADR-015](ADR-015-authentication.md) §6c's two dispositions rather than adding a third.
5. **DISCHARGED 2026-08-18**, and the condition was reworded by building it. It said the screen
   should be *absent for a role without `groups:create`*, which is wrong: **you can belong to a group
   without being able to create one**, and a member who cannot see the group they were added to has
   been added to nothing. So the screen is present for everybody and its *controls* follow the
   membership axis — the server sends `mayManage` and `mayDelete` per row, and the console hides what
   they deny rather than offering it and reporting a 403. Asserted from the other direction too: the
   owner of a group **is** offered them, which is what proves the flags are read rather than
   hard-coded off. **No screen appears that its reader cannot use** — ADR-034 condition 1.

   **The screen was owed for half a day, and the owner asked for it:** *"grubu nereden oluşturuyoruz.
   içinde olduğum grupların listesini nereden görüyorum?"* The store and the API were built and
   measured, and the answer to both questions was *nowhere*. Worth recording because the order was
   deliberate — ADR-035 §4d built the privilege mechanism before the groups that need it — and the
   cost of that order is exactly this: a working subsystem with no way in.

## 7. Dissent

**The per-group capability is the decision most likely to be regretted**, and the objection is worth
recording rather than answered: an organisation that wants one item in a group editable and the rest
readable has to make two groups, and will experience that as the product being wrong. The
counter-argument is §4b's reason 1 — the alternative cannot be stated in a sentence — and the
mitigation is that two groups is laborious rather than impossible.

**And the immutability is a bet.** It prevents a retroactive widening that nobody would notice; it
also means an operator who chose *none* and later wants editing must rebuild. If that turns out to
be the common case rather than the rare one, §4c is the paragraph to reopen, and reopening it means
deciding what happens to shares made under the old terms — which is the question immutability
exists to avoid answering.
