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

#### 4a-i. Amended 2026-08-25 — editing through a group is built, and it was already half-there

*Owner decision, after being shown ArcGIS's group settings: shared update should exist here
too.* §4a above said editing through a group **is not built**, and gave the reason plainly —
the owner's requirement had not asked for it and §82's question had no answer. It asks now,
and §4b had already put the capability on the group rather than on each share precisely so
that this would be an addition rather than a redesign. It was.

**What was actually wrong, and it is worse than *not built*.** `item_update` was written by
the admin API, stored, read back, and shown in the group listing — and no code path
consulted it. An operator could set a group to *all items* and watch every edit go on asking
for `features:fullEdit` alone. A setting the server keeps and does not honour is
[D-67](../architecture-debt.md), and it is the same shape as the `public` visibility removed
by §4b two decisions ago: **the screen promised something the server never did.** That the
capability was *designed* is what made it invisible — the design was cited as though it were
the behaviour.

**The rule.** A layer whose sharing scope is `group`, shared with a group whose
`item_update` is `allItems`, is editable by that group's members — whatever privileges they
hold, and only that layer. `ownItems` grants nothing: it means *the items you shared*, and
their owner may already edit them by owning them. Stated rather than left as an omission,
because a reader will otherwise wonder whether it was forgotten.

**It satisfies `features:fullEdit`, not only `features:edit`.** The narrow/wide split exists
because editor tracking is deferred ([Q-58](../open-questions.md)) and the server cannot tell
whose feature is whose, so *change your own* is unenforceable and updates ask for the wider
grant. A group with `allItems` is exactly the case where that distinction has no work to do:
every member may change everything shared with the group, by the group's own setting.

**Both faces decide it in one place.** ArcGIS `applyEdits` and OGC API Features Part 4 write
through one `IFeatureWriter` (Q-44); they now authorise through one
`Authorize.RequireEditAsync` as well. Two copies of an authorisation rule is how the same
layer comes to be editable through one face and refused through the other, and that
divergence surfaces months later on whichever face nobody tested.

**One ordering changed on the OGC face.** Its three write endpoints checked the privilege
*before* resolving the collection; the answer now depends on which groups the collection is
shared with, so resolution comes first. It leaks nothing: `TargetAsync` answers 404 for a
collection the caller cannot read, exactly as the read path does.

**The read invariant is untouched, and [ADR-018](ADR-018-authorization-and-roles.md) §3b-iii
now says so in its own words.** `LayerAccess.Evaluate` is unchanged; the editing set is a
subset of the reading set by construction; the three wider scopes are unaffected, asserted
for each by name.

**Where the answer comes from, and why it costs nothing.** The subset is read in the same
row as the caller's groups — one more aggregate on a query every authenticated request
already runs — rather than looked up at edit time, which would have put a round trip on the
write path to answer a question the read path had already answered.

**Covered by `SharedUpdateTests` for the rule and `SharedUpdateGrantsTests` for the store.**
The second matters more than it looks: the original defect was entirely in the gap between
them, so a test of the rule alone would have passed for as long as the setting did nothing.
Both were falsified — dropping the scope check failed three named scopes, and widening the
store's filter to every group failed the store test.

---

#### 4a-ii. Amended 2026-08-25 — who may see the members, and whether a member may leave

*Owner decision, from the same two ArcGIS screens as §4a-i, and their own words for the three
they wanted: "Üye listesini kim görebilir, Shared update (paylaşılan düzenleme),
Administrative group (üye ayrılamaz)". The third was §4a-i; these are the other two.*
Migration 37.

**Who may see the member list.** Two values — *any member* and *the owner and its managers* —
and the first is the default, because it is what every group did before the setting existed.
An administrator sees the list either way, as at every other group act: a group whose owner
has left still has to be administrable, and a member list an administrator cannot read is one
they cannot repair.

**Neither value reaches outside the group, and that is what makes the setting safe to add.**
It can only narrow what §4b's `visibility` already allows, so the two cannot contradict each
other and there is no ordering to enforce between them. **An organisation-wide member list was
considered and is not offered**: a group visible to the organisation is discoverable *by name*,
and *who is in it* is a different disclosure from *it exists*.

**Withheld, not filtered.** `members` comes back as `null` for a caller who may not see it,
never as `[]`. A filtered list of nine that renders as zero reads as an empty group, which is a
different false statement rather than none — the same reasoning §4d's `inside` flag already
uses one line above.

**Leaving had to be built before *cannot leave* could mean anything.** There was no way for a
member to leave a group at all: removal was an owner's, a manager's or an administrator's act.
Adding the flag alone would have shipped a checkbox governing nothing — which is precisely the
defect §4a-i had just repaired, in the same file, on the same day. So
`DELETE /admin/groups/{name}/membership` exists, it is the caller's own membership, and it
asks for **no privilege**: requiring `groups:manageMembers` would mean somebody put into a
group needs an administrator to get out of it, which is not a security property but a support
ticket.

**Its own route, not an exception inside the removal route.** `DELETE .../members/{member}` is
guarded by *owner or manager*; folding self-removal into it would give that guard an
exception, and an authorisation check with an exception is the one that gets read wrong.

**Four outcomes, and two of them have to be identical.** *Done*; *this group forbids leaving*;
*you own it — transfer or delete instead*; and **`Absent` for both *no such group* and *you
were not in it***, because two different answers would let somebody enumerate the groups they
are outside. The owner's refusal is separate from the administrative group's because the way
out of each is different, and a refusal that does not name the way out is one somebody
re-clicks.

**The owner cannot leave even when the group allows it.** A group whose owner has walked out
has nobody who can administer it — [D-14](../architecture-debt.md)'s shape one level down, and
the answer is the same one: transfer or delete, both of which already exist.

**`members_may_leave` defaults to true**, because false would make every group that already
exists an administrative one on upgrade. An administrative group is a deliberate choice.

**The refusal and the delete are one statement.** Asking whether the group allows leaving and
then deleting the row is a race with an owner turning the setting on; the `where` carries both.

**Measured end to end 2026-08-25**, against a running server with an owner and one member:
`memberList=members` showed the member two people; `memberList=managers` gave that member
`null` while the administrator still saw two; `membersMayLeave=false` refused the member with
**409** and the owner with a different **409**; `membersMayLeave=true` let the member out with
**200**, after which the group answered **404** to them and a second attempt answered **404**.
`GroupMemberListAndLeavingTests` holds the store's half, and was falsified by dropping
`members_may_leave` from the delete's `where`.

**One thing that measurement caught.** The first draft of the script sent a password when
creating the member; the server issues its own and returns it in the 201, so the sign-in
failed silently and every member-side result was a **401** wearing the answer's clothes — the
member-list check read as *withheld* when nothing had been tested at all.

---

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

> **Still true, and now conditional — 2026-08-25.** §4a-i makes a group whose `item_update` is
> `allItems` confer editing as well. The group measured here has no such setting, so this row is
> unchanged and the assertion still holds for it; what changed is that *a group share confers
> reading and nothing else* is a statement about **this** group rather than about groups. The
> conditional half is `SharedUpdateTests` and `SharedUpdateGrantsTests`.

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

### 4g. The four tabs, and the settings §4e deferred — owner decision, 2026-08-18

**The owner overruled §4f's refusal, and their reason defeats the argument it was made on.** Four
screenshots of Portal's group — *Overview*, *Content*, *Members*, *Settings* — with: *"arcgis portal
da grup seçenekleri böyle. yakında sistemde harita ekleme, icon ekleme gibi özellikler de olacak. o
mantıkta … bizim ekranımız yetersiz ve basit kalıyor."*

§4f declined the tabs on subject rather than scale: *the screen exists to compare who is in a group
with what they can therefore read, and tabs would hide half of the comparison*. **That holds for two
short tables and stops holding the moment Content becomes a page of its own** — which is what maps
and icons make it. A gallery with thumbnails, item types and its own filters is not a column beside a
member list; it is a screen. The refusal was right about today and wrong about the product, and the
owner was arguing about the product.

**Recorded rather than quietly reversed**, because §4f's argument is still the correct argument for a
two-table screen and somebody will meet it again on a different one.

#### The structure adopted

| Tab | What it holds |
|---|---|
| **Overview** | Summary and description, recently added content, and a facts rail: owner, your membership with *Leave group*, member count, created, and the three settings below stated as facts |
| **Content** | What is shared with the group, with its own search, item-type filter and per-item type and date — the tab that has to exist before maps arrive |
| **Members** | Members with their standing and when they joined, with its own search and a standing filter |
| **Settings** | The four editable policies, and deletion |

#### And Q-118 is answered by the owner, with a fourth setting it had not recorded

The Settings tab shows exactly the three axes [Q-118](../open-questions.md) held open, and one more:

- **Who can view this group?** — *only group members* / *all organization members* / *everyone
  (public)*. The group's own visibility, which answers *who may discover that this group exists* and
  is a different question from who may read its items. **Default: only group members**, which is what
  ours does today by construction.
- **How can people join?** — *by invitation* / *by request* / *by adding themselves*. **Q-118 did not
  have this at all.** It is the axis that turns a group from a thing done *to* people into one they
  can approach, and *by request* implies a queue of pending requests — which is a table and a screen
  and is **deferred**, with the column carrying the value so the decision is recorded rather than
  re-opened. **Default: by invitation**, and `request` is refused on write until the queue exists,
  because a policy the server accepts and does not honour is [D-67](../architecture-debt.md) again.
- **Who can contribute content?** — *all group members* / *group owner and managers*. **Distinct from
  §4b's `item_update`**, which governs editing what is *already* shared and stays immutable — and the
  reference's own Settings page not offering it is the evidence that they draw the same line.
  **Default: owner and managers**, which is what ours enforces today.
- **Prevent this group from being accidentally deleted** — a lock beside the delete. Ours has a
  confirmation and no lock, and a confirmation is dismissed by habit.

**Every default above is what this server already does**, so migration 27 changes no behaviour: it
gives an operator the ability to say otherwise. That is the same shape as ADR-035's seed and for the
same reason — an upgrade that quietly widened who may see a group would be the worst outcome
available.

#### What is deliberately still not taken

- **Group categories** (*"Set up group categories"*) — a taxonomy for organising items inside a group.
  It needs items worth organising, which is after maps rather than before.
- **A thumbnail.** It is a file, and where files live is ADR territory this decision should not settle
  in passing.
- **`Create web app`** — a product feature of theirs with nothing behind it here.
- **`Owner + Groups (n)`** on each item, which reports every group an item is in. Ours cannot: the
  item listing is per group. It is a read, it is useful, and it is a query rather than a decision.

### 4h. Built and measured, and one defect caught before it could destroy anything

**2026-08-18.** The page, the four tabs and the settings write exist and were exercised against a
running server on the throwaway schema.

#### The tabs

`#/group/{name}/{tab}` — four addresses, one `.tabstrip` component, links in a `<nav>` with
`aria-current` rather than `role="tablist"`. That role would owe a screen reader arrow keys, Home/End,
one tab stop across the set and `aria-controls` on four `role="tabpanel"` elements; these are
addresses, so they are navigation, and Back, middle-click and copy-link all work as a result.

**A group's page is addressable where a service's is not, and the difference is real rather than an
oversight.** `route()` splits the hash on `/` before decoding, so a service's `folder/name` cannot
survive a third segment — the inconsistency ADR-034 §5c records. A group's name is one encoded
segment and can, *including* when it contains a slash, which nothing currently prevents.

#### What §4f's argument was traded for

§4f declined the tabs because the screen existed to compare *who is in a group* with *what they can
therefore read*. **That comparison is now the first thing on Overview**, as a sentence with the
numbers set strongly: *8 services shared with this group. 3 reach its members; 5 are inert — their own
sharing scope is not `group`, and both halves have to agree.* Nowhere else counts it. If that sentence
ever stops being rendered, the tabs have cost the screen its subject for nothing, and
`Each_of_a_groups_four_tabs_shows_its_own_subject` is the assertion that says so.

#### Three fields that were in the schema and unread

`sharing_group_item.shared_at` and `shared_by`, and `service.kind`, all present since the tables were
created and none of them reaching a screen. `shared_by` is worth more here than the reference's *owner*
column: with `contribute: members` any member may share their own service in, so when one of thirty is
inert, *who put it here* names the person to talk to. And `sharing_group_member.added_at` for Members —
a group's member list is an access-control list, and *when did this person gain access to everything
shared here* is an audit question nothing could answer.

#### The defect: the settings write replaces every field, and its documentation said the opposite

**Caught by a design review before either screen existed, and it would have destroyed data an operator
typed.** The port described `title`, `summary` and `description` as *"or null to leave it"* while the
statement writes `set title = @title`. So:

- a Settings tab posting only its four policies would have **erased the title, the summary and the
  description**;
- an Overview summary editor posting only a summary would have **silently unlocked a delete-locked
  group** and erased the description.

Both halves were then measured rather than reasoned about, because a claim about data loss is exactly
the kind that reads as true and is not:

| Sent | Result |
|---|---|
| Whole object, overlaid on the last read — three policies changed | `title` and `description` **kept**, `visibility organization`, `joinPolicy self`, `contribute members` |
| Policies only, nothing else — what a naive caller sends | `title` **null**, `description` **null** |

**Left as a replace rather than made into a `coalesce` patch**, because *clearing* a description has to
be expressible and a store where null means *leave* cannot express it. The fix is therefore on both
sides: the port's documentation now says what the code does and why a partial caller is a bug, and the
console has exactly one function that builds that body, overlaying a patch on what it last read.
`Writing_settings_replaces_the_text_fields_too` pins the behaviour so that helper stays necessary
rather than decorative.

#### The four refusals, measured

| Act | Answer |
|---|---|
| `joinPolicy: request` | **400**, naming what is missing — a queue of pending requests, a table, a screen and a decision about who reviews them, and that accepting it anyway would be D-67 again |
| Delete a locked group, **as `root`** | **409**, and the group is still there on the next read |
| Same after unlocking | **200**, then **404** |
| Settings on a group you neither own nor manage | refused on the membership axis, not on a privilege |

**The lock binding an administrator is the decision, not an oversight.** Every other refusal in the
store yields to `administrator: true`. A protection the most privileged caller passes through is a
protection against typing rather than against deleting, and the operator who sets the lock is usually
the one who would fat-finger it. `A_delete_lock_binds_an_administrator` exists to stop that being
'fixed' into consistency with its neighbours.

#### The second defect: the setting this decision is about did nothing, for an hour

**`visibility` was stored, reported by two endpoints, and read by no `where` clause.** `ListAsync`'s
condition was `@all or owner or member`. So a group set to *everybody, including anonymous callers* was
discoverable by exactly the people who could already see it — while the console offered the control and
the endpoint's own note said *"it can now be found by anybody"*.

**That is [D-67](../architecture-debt.md) precisely, and it shipped in the same change that refuses
`join_policy = 'request'` on the ground that a policy stored and unenforced is D-67 over again.** One of
the two had to move, and the inconsistency was mine rather than the design's. Found by asking what reads
the column, which is a question worth asking of every setting on the day it is added.

**Enforced rather than refused, because enforcing was one disjunct.** `or g.visibility in
('organization', 'public')`, and then the part that matters: a caller who reaches a group only that way
comes back `GroupStanding.Outside`, and the describe endpoint **withholds** the member and item lists on
that standing rather than filtering them — a filtered list of nine members rendering as zero reads as an
empty group, which is a different false statement rather than none. It reports `inside: false` so a
reader and a script both know the lists are withheld and not empty. That is where §4g's *"being able to
see that a group exists is not being able to read what is in it"* is actually kept.

| Visibility | An outsider lists it | Describe | Member list |
|---|---|---|---|
| `members` | no | **404** | — |
| `organization` | yes | 200, `inside: false` | **withheld** |
| A member, same group | yes | 200, `inside: true` | 2 members |

#### And `public` is now refused, for the reason that refused `request`

`public` would mean *discoverable by anybody, including an anonymous caller*, and **there is nowhere for
that to happen**: `/admin/groups` refuses an anonymous caller outright, so `public` and `organization`
are enforced identically. Accepting it reports a discovery this server does not perform.

So it is refused on write and still read correctly — the same shape as `request`, and the same argument.
Two identical situations treated differently on one screen is the inconsistency an operator notices, and
the console renders both as disabled options carrying their reason. **Where a public group is actually
discovered is [Q-119](../open-questions.md)**, and it is a decision about anonymous surfaces rather than
about groups.

#### Every guard was falsified before being believed

Six store guards, six surgical breaks, six failures and every other test unaffected: the replace turned
into a `coalesce` patch, the lock made to yield to an administrator, the `request` refusal moved to
*after* the write, an unknown stored visibility made to read as `public`, the visibility disjunct removed
from the listing, and the `public` refusal removed. That
last one is the direction rather than the mapping — a row written by a newer build carrying a
visibility this one does not know must not make a group public by accident, because the safe reading of
*"I do not understand this"* is the one that shows it to fewer people.

#### What is still not built, and is recorded rather than implied

- **No pictures on Overview.** `drawPreview` can draw a service from one query, but group items do not
  carry a service's cover and this pass did not add it to the query. *Recently shared* is four rows of
  name, kind and date. Rendering `.thumb.empty` for each instead would be worse: on the Services screen
  that hatching means *this service has nothing to draw*, and here it would mean *this screen did not
  ask*.
- **Content's filter rail is reserved by not being built.** One grid column today; `232px minmax(0,
  1fr)` and a `.rail-item` list when there is something to put in it. A *Group categories* heading over
  an empty state linking to a feature that does not exist is the thing this console already refused
  once, when it dropped the notification bell.
- **`joinPolicy: request` is rendered disabled rather than omitted**, and the harder reason is not
  honesty: the column stores three values and only the write path refuses the third, so a group can
  already *hold* `request`. Render two options and such a group reads as *by invitation* while the store
  says otherwise — the console lying about a policy. You can save away from it and not back to it.

### 4i. What the design review found, and it was more than the page

**The page was reviewed by a design pass on the day it was built, per the owner's standing
instruction** — *"bundan sonra yapacağın tüm tasarımlarda ui-ux designer'i de kullan."* It found three
defects that made the feature unusable and could not have been found by reading the code, because each
of them looked correct in the source.

- **[D-81] The whole write surface was invisible to its owner.** `mayManage` and `mayDelete` were
  computed in the listing handler and never in the describe one, so the page read `undefined` seven
  times. An administrator who owned the group was told, on the Settings tab, that these were *"the
  owner's and its managers' to set"*. A plain member's view was accidentally correct. **Seven console
  tests passed**, all of them about shape.
- **[D-82] The members sort did the opposite of its own comment**, and the tab's manager/member divider
  was built from the comment.
- **[D-83] `SCOPES` had three values where the server takes four**, so the one instruction this page
  gives could not be followed anywhere in the console.

Two more, repaired in the review itself: the *Share a service* picker doubled the folder, so it
answered 404 for every service on a server where every service is in a folder — the page's Content tab
had therefore never held a row; and both of the page's pagers were absent from the click handler's list,
so on a fourteen-member group four rows were unreachable. **Together those two mean the page's entire
write surface had never once run** before it was reviewed.

#### And one thing the review changed my mind about

**Overview as built was not worth landing on.** Four blocks, three of them restatements: the tally
restated the tab count in three of its four states, *Recently shared* was Content's first page minus its
verb, and four of the ten facts were Settings read twice. The review put the choice as *make Overview
the repair desk or delete it and land on Content*.

**It is the repair desk.** It now leads with the number nowhere else says — *"9 of the 11 services
shared here reach nobody"* — and lists exactly those services with the verb that fixes each. That is
only possible because D-83 was fixed in the same pass: before it, the console could not set a service's
scope at all. So the tab holds the one thing no other tab does, which is what is wrong with this group
and how to clear it, and the both-sides count moved to Content where the shares are: *"2 of 11 reach the
14 people in this group."*

#### Three accessibility failures, all in the new work

- **Four unlabelled comboboxes.** The Settings tab's questions were `<span class="q">`, so every select
  on the one tab that is nothing but form controls had an empty accessible name. `<label for>` now, and
  a test asserts that nothing on the tab lacks a name.
- **The lock was named by its state**, `" Locked"`, rather than its subject.
- **`--faint` was 2.99:1 on white, below AA** — found on two new uses that carry information, and then
  true of all twenty, nearly every one of them text. Darkened to `#6b7787` (4.55:1), same hue, one step
  lighter than `--muted` as it was always meant to be. It was never a decoration token; it was a second
  muted grey that happened to be too light to read.

#### The copy that was explaining itself

The review read every string cold, which nobody had. The ones that were the build log talking to the
operator: a lock toast that appended a lecture about who can discover the group (the endpoint returns a
visibility note on every call, because the write is whole-object — now shown only when visibility
actually moved); a share toast carrying `PUT /admin/services/{name}/sharing`; a three-line hint under a
closed select explaining an option nobody had seen. And a promise the page could not keep: *"No summary.
**Settings** takes one"*, pointing at a field that existed nowhere in this console — `title` and
`description` were create-only and `summary` was unreachable. Settings has all three now, which the
overlay made safe to add.

### 4j. Where a verb lives — owner correction, 2026-08-18

*"add member shall be inside members section. share a service is not like that. it shall be add
item."*

Both managing verbs were in the page head, above all four tabs — the panel's habit surviving into a page
that has somewhere better to put them. **A verb belongs on the tab whose subject it changes**: *Add
member* on Members, *Add item* on Content. The head keeps only what applies to the group as a whole,
which is *Leave group*.

**And the rename is the more durable half.** *Share a service* named the kind of thing being added, and
a service is the only kind there is today — so the label would have had to change the moment maps and
icons arrive, which is the change §4g adopted the tabs for. *Add item* is the word the tab is already
built around. It also reads from the group's side: what the button does is put something *into* this
group.

## 4b. A group is visible to its members or to the organisation — amended 2026-08-25

**Owner decision, closing [Q-119](../open-questions.md): there is no public group.**
`GroupVisibility` has two values, migration 36 tightens the constraint, and the console
offers two controls where it offered three.

**The third value never worked and the setting said it did.** *Everybody, including an
anonymous caller* needs somewhere an anonymous caller can look, and `/admin/groups`
refuses one outright — so `public` and `organization` were enforced identically by the
same disjunct while the console promised more. That is [D-67](../architecture-debt.md)'s
shape: a setting stored and unenforced. It was already refused on write for exactly that
reason; what changed is that a refusal is a thing an operator meets and a missing option
is not.

**The alternatives were both a new anonymous read path.** Either a discovery surface
outside `/admin` that lists public groups to anybody, or letting an unauthenticated caller
into the admin listing filtered to public groups only. Both add an unauthenticated reader
to a product whose sharing model answers *404* rather than *403* precisely so that nobody
learns what exists — and neither is owed by anything anybody is asking for.

**Demoted rather than narrowed.** Migration 36 moves any stored `public` to
`organization`, not to `members`: a group somebody made discoverable is a group they
wanted found, and `organization` is what it was actually being enforced as. The migration
should demote nothing, and runs anyway, because *should* is a claim about deployments
nobody here has seen and a check constraint that fails on upgrade is a server that will
not start.

**A caller that still sends `public` is told what happened** rather than that the word is
unrecognised — clients outlive settings.

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

**State.** *Catalogue*: **groups, their membership, what is shared with them, and
each group's settings** — visibility, join policy, contributor policy and the delete lock. Also
the `item_update` flag, fixed at creation. Every one of these decides who may read something, so
none of it can be runtime. *Runtime*: a caller's group set is resolved per request and held only
for that request.

## 6. Conditions

1. **DISCHARGED 2026-08-18**, measured in §4d: 404 / 404 / 200 / 200 across anonymous,
   non-member, member and administrator, with the member's `addFeatures` refused.
   **A group share is proven to confer reading and nothing else** — for a group whose
   `item_update` is not `allItems`, which since §4a-i (2026-08-25) is the qualification this
   sentence needs and did not before. A member of a group a service is
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
