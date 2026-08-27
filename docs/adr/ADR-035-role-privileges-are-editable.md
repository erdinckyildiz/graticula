# ADR-035 — Role privileges are editable, except the administrator's

- **Status:** `ACCEPTED WITH CONDITIONS`
- **Confidence:** `MEDIUM` — the decision is the owner's and is not in doubt; the confidence is
  about the shape below, which is our reading of it.
- **Date:** 2026-08-18
- **Supersedes:** [Q-59](../open-questions.md)'s *"Fixed, not custom"*, and with it one paragraph
  of [ADR-018](ADR-018-authorization-and-roles.md) §3a.
- **Depends on:** [ADR-018](ADR-018-authorization-and-roles.md),
  [ADR-034](ADR-034-server-and-studio.md), [Q-112](../open-questions.md)

## 1. What the owner decided

Verbatim, 2026-08-18:

> *"roller aslında kullanıcıların sistemde yapabildikleri. yeni bir rol ekleyip studio tarafı için
> yetkiler verebiliriz. … grup oluşturma da bir yetki. kendi grubunu silme de bir yetki. gruba
> kullanıcı ekleyebilmek de bir yetki. yani bir grubun sahibi olmayabilirsin ama yönetici olarak
> atanırsan, sen de grupta yetkili işlemler yapabilirsin. bu gibi işlemler çeşitlendirilebilir.
> sistemde tanımlı tüm rollerin yetkileri değiştirilebilir, admin hariç. Admin yetkisi
> değiştirilemez. Ve sınırlandırılamaz. Sistemde her işlemi yapabilir."*

Four things, and they are separable:

1. **A role is a set of privileges, and the set is editable.** Every role defined in the system can
   have its privileges changed.
2. **The administrator is the exception**, and the exception is absolute: unchangeable,
   unrestrictable, and able to perform every operation in the system.
3. **New roles can be added**, including roles that exist to carry Studio-side privileges.
4. **Group operations are privileges** — creating a group, deleting your own group, adding a member
   to a group — and *being appointed a group's manager* confers them within that group without
   conferring ownership.

## 2. This reverses a recorded decision, and the reversal is the owner's to make

**[Q-59](../open-questions.md) decided the opposite on 2026-08-14, in these words:** *"**Fixed, not
custom** — a custom role is an expression over a permission catalogue, and the moment customers
write roles against it the catalogue becomes a public contract that cannot be renamed or split
without breaking their grants; nine permissions derived from three endpoints is not a vocabulary
worth freezing."*

**That argument was sound and it is now overruled, which is a different thing from it having been
wrong.** [CLAUDE.md](../../CLAUDE.md) §2 requires the distinction to be recorded rather than
smoothed over, so: the reasoning stands as written in Q-59 and the decision goes the other way,
because the owner wants a deployment to be able to define what a role may do without a rebuild.

**The cost Q-59 named does not disappear — it arrives.** The privilege catalogue becomes a public
contract. Fourteen names that could until today be renamed, split or merged freely are, from the
first deployment that writes a role against them, grants somebody holds. Three consequences follow
and they belong in this decision rather than in a surprise later:

- **A privilege can be added freely and removed only with a migration.** Adding one grants nobody
  anything until a role is edited. Removing or renaming one silently changes what an existing role
  confers, which is the failure mode Q-59 was protecting against.
- **Splitting a privilege must widen, never narrow.** If `content:publishFeatures` ever becomes two
  privileges, every role holding the old one must receive both, or a deployment loses a capability
  on upgrade without asking for it. That is the same *never silently degrade* rule
  [ADR-008](ADR-008-query-engine.md) §2 states for query results.
- **The catalogue needs a version, or at least a test that notices.** Condition 3.

## 3. What exists today, measured

**Nothing of this is built, and one part of it is a privilege with nothing behind it.**

- **`admin:manageRoles` exists, is granted to the administrator role, and no endpoint requires
  it.** Grepped across the host: the name appears in `Privilege.cs` where it is defined and granted,
  and in `Authorize.cs` where it is mapped to a string. No `RequireAsync` call names it. It is
  exactly the *"privilege with nothing behind it"* that [D-56](../architecture-debt.md) complains
  about for `admin:manageMembers` — and it is the privilege this decision is about.
- **The `role` table carries `name` and `description`, and no privileges.** Measured against the
  live schema. Role-to-privilege lives entirely in `Privilege.BuildGrants()`, a C#
  `ImmutableDictionary` compiled into the platform assembly.
- **So Q-59's own note that *"the `role` table already carries arbitrary names, so custom roles are
  a feature rather than a migration"* is half true and the wrong half is load-bearing.** A row can
  be inserted into `role` today. It would grant nothing, and there would be no way to give it
  anything. Creating a role that confers nothing is not a feature.

**That is the second time in two days that a claimed piece of preparation for a deferred subsystem
turned out not to exist** — the first being [ADR-018](ADR-018-authorization-and-roles.md) §3b's
sharing column, which was said to take a fourth scope *"as a value rather than a migration"* and
carries a three-value check constraint on three tables. The pattern is worth naming: **a deferral
that describes how cheap it will be to undefer is describing work nobody has done.** Neither claim
was checked when written, and both read as reassurance.

## 4. The decision

### 4a. A role is a name plus a set of privileges, stored

`role_privilege(role_name, privilege)`, one row per grant, with the privilege name as text and a
foreign key to `role`. `Privilege.BuildGrants()` becomes the **seed** for the five built-in roles
rather than the answer at runtime: the migration writes today's grants into the table, so an
existing deployment upgrades to exactly the behaviour it had.

**Text rather than an enum column**, for the reason `sharing` should have been: a privilege the
schema does not know about is a privilege that cannot be added without a migration, and this
catalogue will grow. The application refuses an unknown name on write; an unknown name found on
read is ignored and logged, because a store written by a newer version must not lock out an older
one.

### 4b. The administrator role is not a row anybody can edit

**It is not stored as editable grants at all.** The temptation is to seed it like the others and
refuse edits at the API; the objection is that a store is edited by more than one API over its
life, and *"admin can do everything"* stated as data is a claim that can be falsified by an
`UPDATE`. So the authorization check short-circuits: **an administrator passes every privilege
check without consulting the table.**

That is stronger than the owner asked for and it is what they asked for: *"Admin yetkisi
değiştirilemez. Ve sınırlandırılamaz. Sistemde her işlemi yapabilir."* Unchangeable and
unrestrictable are properties of code, not of rows.

**The last-administrator refusal already in [ADR-015](ADR-015-authentication.md) §6c becomes
load-bearing for this too**: if the administrator role cannot be narrowed, then a server with no
administrator has no recovery path in band, and removing the last one is already refused.

### 4c. Group privileges are ordinary privileges, and group management is a second axis

The owner's four group operations are privileges in the same catalogue:

| Privilege | What it allows |
|---|---|
| `groups:create` | Create a group. Whoever creates it owns it |
| `groups:deleteOwn` | Delete a group you own |
| `groups:manageMembers` | Add and remove a group's members |
| `groups:shareTo` | Share an item you own with a group you belong to |

**And a second axis beside the role**, which is the part that is not just another privilege: *"bir
grubun sahibi olmayabilirsin ama yönetici olarak atanırsan, sen de grupta yetkili işlemler
yapabilirsin."* A member appointed **manager** of a group holds the group-scoped operations inside
that group without holding them anywhere else, and without owning it.

So an authorization answer for a group operation is: *does the role grant it,* **and** *is this
principal the group's owner or one of its managers?* Two axes, and the second is scoped to one
object. This is the same shape ArcGIS uses and it is why [Q-112](../open-questions.md)'s middle
question — whether the update capability belongs to the group or to each share — matters: a
per-share grant would make the second axis per-item instead of per-group, which is a different
product.

### 4d. Ordering: privileges first, then groups

**The owner asked, and the answer is yes:** *"aslında gruplardan önce yetki mekanizmasını mı
tanımlamak mantıklı acaba"*. Three reasons, in order of weight.

1. **Groups cannot be built without it.** Every group operation in §4c is a privilege, and there is
   nowhere to put a privilege that a deployment can grant. Building groups first means hard-coding
   who may create one, then rewriting it.
2. **The privilege mechanism is useful on its own** — a deployment that wants a role between
   `user` and `publisher` can have one the day this ships, with no groups involved.
3. **It is the smaller and more certain of the two.** The schema is one table and a seed; the
   semantics are already decided by §4a–§4b. Groups still have [Q-112](../open-questions.md)'s
   three open questions in front of them, and answering those while also inventing the privilege
   store would mean two designs settling against each other at once.

**What must be decided *with* the privilege work rather than after it:** whether the group-manager
axis of §4c is part of the authorization model from the start. If it is not, group management gets
retrofitted into a check that was written for one axis — and a second axis added to an
authorization decision later is the change most likely to be got wrong quietly.


### 4e. Privileges are not independent, and making roles editable is what exposes that

**This is a gap in §4a as first written, found by reading the reference the owner named.** Esri's
public documentation of role privileges states the dependency directly: *"This privilege is required
if you grant any of the privileges that allow members to publish, register data stores, or create
notebooks"* — about **create, update and delete content** — and, for version management, that
*"Manage all"* **automatically grants** *Edit* and *Edit with full control*.
([Privileges for roles](https://doc.esri.com/en/arcgis-enterprise/latest/administer/privileges-for-roles-orgs.html),
which is the public specification and therefore the citation, per
[ADR-030](ADR-030-reading-the-reference-implementation.md) condition 3.)

**Our own grants already encode exactly that, and they encode it in the one place editing
destroys.** `Privilege.BuildGrants()` builds each role out of the one below it:

```csharp
ImmutableHashSet<Privilege> user      = viewer.Add(ContentCreate).Add(SharingShareToOrganization);
ImmutableHashSet<Privilege> publisher = user.Union(dataEditor)
                                            .Add(ContentPublishFeatures) …
```

`publisher` holds `content:create` **because it is built on `user`**, not because anybody listed it.
The nesting *is* the dependency statement, and it is unwritten anywhere else. Flatten those five
sets into `role_privilege` rows and the nesting is gone: an operator can then tick
`content:publishFeatures`, leave `content:create` off, and save a role that claims to publish and
cannot create the thing it would publish. **Nothing in §4a would have refused that**, and the role
would look correct on the screen.

**Two different relations, and they are resolved differently — which matters, because treating them
alike is how one of them becomes wrong.**

- **Implication.** `features:fullEdit` is a superset of `features:edit`; `admin:manageAllContent` is
  a superset of `admin:viewAllContent`. These are resolved **in the authorization check**, not in the
  stored grants: a role holding only `fullEdit` passes an `edit` check. Storing both would make the
  screen show two ticks for one decision, and unticking the narrower one while the wider stays would
  be a state that means nothing.
- **Prerequisite.** `content:publishFeatures` needs `content:create`, and neither contains the
  other. These are **refused on write, with the missing privilege named** — not auto-added. Esri
  auto-adds for version management and we do not, because auto-adding silently grants something the
  operator did not tick, and *never silently widen* is the rule this project applied to the
  statement timeout the same day: a value that cannot be honoured exactly is refused rather than
  adjusted. A refusal that says *"publishFeatures needs create; tick it or untick this"* teaches the
  model; an auto-add hides it.

**Which relation each pair is has to be written down**, because today it lives in the shape of five
`ImmutableHashSet` expressions. That is condition 6.

### 4f. The screen, with its shape taken from the reference the owner named

Owner, 2026-08-18, with the Create-role dialog on screen and an arrow on one control: *"Ekran
görüntüsünde işaretlediğim de güzel bir özellik."* The marked control is **Set from existing role**.

**Set from existing role is the feature, and it is worth stating why rather than just copying it.**
Sixty-five privileges in the reference, fourteen here and growing: without it, creating a role means
ticking boxes from nothing, and the realistic use is *"publisher, but without share-to-public"* —
a small edit to an existing set. Starting from empty makes the operator reconstruct a set somebody
already designed, and the errors that produces are omissions, which are invisible. It also composes
with §4a's seed: the five built-in roles are rows like any other, so *set from existing* has
something to copy on a fresh install.

The rest of the dialog's shape, and what we take from each part:

| Reference | Here |
|---|---|
| Two sections, **General** and **Administrative** | **Taken, and it is [ADR-034](ADR-034-server-and-studio.md)'s split arriving from the other direction.** General is Studio's vocabulary — content, sharing, features; Administrative is Server's. Seven of our fourteen fall each side, which is a coincidence worth nothing and a confirmation worth something |
| Per-category counts — *Enabled: 0/14* | **Taken.** A collapsed category that cannot say how much of it is on forces the operator to open all of them |
| **Enable all** per category and per section, **Expand all** | **Taken.** With fourteen privileges it is a convenience; the reference has thirty-two in one section, and that is where this becomes necessary rather than pleasant |
| A **privilege compatibility** control, and a derived line — *"Compatible with Creator user type and 2 others"* | **The derived line, yes. The slider, not yet.** Esri's rule is stated plainly: *"When you create a custom role that includes administrative privileges, only members assigned a Creator, Professional, or Professional Plus user types can be assigned to the custom role."* So which user types may hold a role is a **consequence of what is ticked**, and the screen should say it rather than ask it — [ADR-018](ADR-018-authorization-and-roles.md) §3a's ceiling, shown at the moment it is being created. Whether a *control* that bounds the ticking in advance is worth having is a separate question and is not answered here: we have three user types and no licences to meter, so the line may be the whole of it |

**One privilege has no section, and it is the one the owner moved.**
`content:registerDataStore` is named for content and granted only to the administrator, by owner
decision 2026-08-17 (*"data sources studio'nun değil server'in bir seçeneği"*). The reference lists
*Register data stores* under **General → Content**. So on this screen it appears in the section it is
*granted* from — Administrative — while carrying a name from the other one. That is the only place
our catalogue's names and sections disagree, it is a consequence of a deliberate narrowing, and it is
listed here so that whoever builds the screen does not read it as a mistake and move it back.

### 4g. Some operations belong to no role, however privileged

**The reference is more precise than *"admin can do everything"*, and the precision is worth
taking.** Certain capabilities are reserved for the **default administrator** and are not available
to any custom role at any privilege level: changing a role to or from administrator, deleting another
administrator, resetting an administrator's password, creating backups, registering custom data
providers.

**That is a third category beside §4b's two**, and it sharpens what the owner asked for. *"Admin
yetkisi değiştirilemez. Ve sınırlandırılamaz"* is §4b: the administrator role's grants are not data.
This adds the converse — **a custom role cannot be edited up into an administrator.** Without it,
§4a's editability contains its own defeat: a role with `admin:manageRoles` could grant itself
everything else, and a role with `admin:manageMembers` could make its holder an administrator. The
[ADR-015](ADR-015-authentication.md) §6c refusal to remove the last administrator is the same family
of rule, and this is the wider version of it.

Condition 7.

### 4h. Built and measured, 2026-08-18

**Migration 25** adds `role_privilege(role_name, privilege)` and seeds it from `Roles.Grants`, so
an upgrading deployment keeps exactly the grants it had — verified against the live store, role for
role. The four `groups:*` privileges are in the catalogue and granted to nobody, so the migration
cannot widen anything.

**`IRoleGrants`** is the seam where the compiled constant used to be. `PostgresRoleGrants` holds the
answer, refreshes on every write and re-reads after thirty seconds as a backstop for the
multi-process deployment ADR-007 permits. An unreachable store keeps the last known answer rather
than falling back to the compiled table, because falling back would resurrect the grants a
deployment had edited away.

**Measured end to end**, with one member holding `user` and one token throughout:

| `user` grants | member creates a feature service |
|---|---|
| `content:create` | **403** |
| `content:create`, `content:publishFeatures` | **201** |
| `content:create` (revoked again) | **403** |

Same session, no re-authentication, and `GET /admin/roles` reported the stored set and the in-force
set as equal at each step.

**Three defects found by doing that rather than by reading it**, and each was invisible to the code:

- **The forced refresh was a no-op.** `RefreshAsync` shared its body with the cheap path and kept
  the freshness check inside it, so an explicit refresh returned without reading whenever the held
  answer was under thirty seconds old — which is always, immediately after a request. A revocation
  reported success and took up to thirty seconds.
- **`WithheldByUserType` read the compiled table.** It is the method that exists so a refusal can
  say *your role grants this and your user type does not permit it*, and after roles became editable
  it gave the wrong half of that sentence in exactly the case it was written for.
- **`/admin/members` reported the compiled grants**, and listed only the five built-in roles. It
  agreed with the store until something edited a role, which is the worst kind of agreement: it
  survives every test and breaks the first time the feature is used.

**And one process defect worth recording because it cost more than all three.** Several builds
failed silently: a running server holds its dependencies' DLLs, so `dotnet build` reports `MSB3021`
rather than a compiler error, and a grep for `CS\d+` misses it entirely. The server ran a mixture of
old and new assemblies while two measurements contradicted each other. The fix is mechanical — the
development script now stops the server, builds, and refuses to start on any error — and the lesson
is that *"0 errors"* from a filtered build log is not a build result.

## 5. Consequences

- **`admin:manageRoles` gets something behind it**, closing half of what
  [D-56](../architecture-debt.md) describes.
- **A new Server screen**, shaped by §4f: two sections, per-category counts, expand-all and
  enable-all, *set from existing role*, and a line saying which user types may hold what has been
  ticked. Roles, their privileges, and which members hold them. It is Server's
  because granting a capability is administrative — the same split
  [ADR-034](ADR-034-server-and-studio.md) §5c draws everywhere else — even though the privileges it
  hands out are mostly Studio's.
- **`RolesTests` stops asserting a compiled table and starts asserting a seeded one.** The test that
  matters most is new: **the seed produces exactly today's grants**, because that is what makes the
  upgrade a non-event.
- **The user-type ceiling is unaffected and becomes more important.** [ADR-018](ADR-018-authorization-and-roles.md)
  §3a's ceiling caps what any role may confer on a principal. With roles editable, it is the only
  thing standing between an edited role and a privilege escalation nobody reviewed — Q-16's
  migration case is exactly this.
- **Fourteen privilege names become a contract.** §2's three consequences.

## 6. Conditions

1. **DISCHARGED 2026-08-18.** **The seed is asserted to reproduce today's grants exactly**, role by
   role and privilege by privilege, in a test that fails if either side changes. An upgrade that silently widens or
   narrows what a role confers is the worst outcome available here, and it is the outcome nobody
   would notice.
2. **DISCHARGED 2026-08-18** — `AdministratorAuthorityTests` resolves an administrator against an
   empty grant store and against a viewer user type, and `RoleDirectoryTests` refuses every write to
   the role and then checks its rows survived the refusal. **The administrator short-circuit is
   tested from both directions**: that an administrator passes
   a privilege check with the table empty, and that no write path can remove privileges from the
   administrator role or grant the role to nobody. §4b is worth nothing if a `DELETE` can reach it.
3. **DISCHARGED 2026-08-18** — `RolePrivilegeCatalogueTests` carries the eighteen names as an
   independent list and names the missing one in its failure. **The privilege catalogue is versioned,
   or a test notices when it changes.** A removed or renamed
   privilege silently changes what existing roles confer. Cheapest form: a test carrying the
   expected list of names, so removing one is a deliberate act with a failing build in front of it.
4. **DISCHARGED 2026-08-18** — decided in
   [ADR-036](ADR-036-groups.md) §3 and built before any group check was written: the axis is
   `sharing_group_member.membership`, and it is **per group rather than global**. Every write in
   `PostgresGroupDirectory` resolves the caller's standing in *that* group first and refuses
   `NotYours` otherwise, so managing one group never becomes managing every group — which is the
   escalation this condition existed to make somebody decide about rather than discover.
   `Only_an_owner_a_manager_or_an_administrator_manages_a_groups_members` is the assertion, and it
   fails against a store that trusts its caller. **Recorded as a condition rather than left to
   sequencing, because it is invisible until it is expensive** — and it was: ADR-036's whole
   authorization shape depends on the answer.
5. **No screen appears that its reader cannot use** — [ADR-034](ADR-034-server-and-studio.md)
   condition 1, restated because the roles screen is the first one whose whole subject is who may
   see which screens.
   *(Discharged 2026-08-27 with ADR-034 condition 1, by the same evidence and for the same
   reason a restated condition exists: `SurfaceTests.Without_admin_manageServer_there_is_no_Server_surface_to_see`
   withholds the privilege, opens the Server surface anyway, and asserts
   `offsetParent === null` rather than the `[hidden]` attribute — because an author display
   beats the attribute, which is [D-46](../architecture-debt.md) instance 9. A restatement is
   discharged by its original's evidence or it is not a restatement.)*
6. **DISCHARGED 2026-08-18** — `Roles.Prerequisites` and `Roles.Implies`, with the store refusing a
   missing prerequisite by name and the resolver applying implications; both directions tested,
   including the case where a wider privilege satisfies a prerequisite for the narrower.
   **Every implication and every prerequisite between privileges is written down as data, and
   tested.** §4e. Today they exist only in the shape of `BuildGrants()`'s nesting, which the move to
   stored grants deletes. The test that matters: for each prerequisite pair, a role holding the
   dependent privilege without its prerequisite is refused on write; for each implication pair, a
   role holding only the wider privilege passes a check for the narrower.
7. **DISCHARGED 2026-08-18.** Five acts are now reserved to the administrator role by name rather
   than by privilege: changing a role **to or from** administrator, creating an administrator,
   resetting an administrator's password, and removing an administrator. All five asked
   `admin:manageMembers`, which a deployment may grant to anything — so the escalation was one
   privilege long, and taking the role *away* was the half that mattered as much as granting it,
   because somebody clearing the way does it by removing the others. `PrivilegeEscalationConformance-
   Tests` grants a role **every privilege in the catalogue** and tries all five against a running
   server, with two calls that must succeed as the control; falsified by disabling the guard.
   **No custom role, at any privilege level, can produce an administrator.**

   **And the search found a second defect in two places.** Both the create and the role-change
   handler validated the requested role against `Roles.All` — the five this build ships with — so a
   deployment could define a role and **assign it to nobody**. The feature was half-built in two
   handlers that failed differently enough for repairing one to look like repairing it. Both now read
   the store, and a test assigns a defined role at creation and on an existing member. §4g. Concretely:
   changing a role to or from administrator, deleting an administrator, and resetting an
   administrator's password are refused to everybody except the built-in administrator role.
   Asserted by a test that gives a custom role every privilege in the catalogue and then tries all
   three.

## 7. Dissent

**Q-59's argument is not withdrawn and is recorded here as the standing objection**: a privilege
catalogue that deployments write roles against cannot be refactored. Every future split of a
privilege becomes a compatibility exercise. The counter-argument is the owner's requirement, and it
is a product decision rather than a technical one — a server given away to operators who cannot
rebuild it needs to be configurable by the people running it.

**A second objection, ours, and smaller:** §4b's short-circuit means the administrator role is not
described by the same mechanism as every other role, so the roles screen will show one row that
behaves differently from the rest. That is a real inconsistency and it is the safe one — the
alternative is a screen where the administrator's privileges look editable and the API refuses.
