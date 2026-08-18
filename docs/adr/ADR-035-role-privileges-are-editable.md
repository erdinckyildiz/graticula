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

## 5. Consequences

- **`admin:manageRoles` gets something behind it**, closing half of what
  [D-56](../architecture-debt.md) describes.
- **A new Server screen**: roles, their privileges, and which members hold them. It is Server's
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

1. **The seed is asserted to reproduce today's grants exactly**, role by role and privilege by
   privilege, in a test that fails if either side changes. An upgrade that silently widens or
   narrows what a role confers is the worst outcome available here, and it is the outcome nobody
   would notice.
2. **The administrator short-circuit is tested from both directions**: that an administrator passes
   a privilege check with the table empty, and that no write path can remove privileges from the
   administrator role or grant the role to nobody. §4b is worth nothing if a `DELETE` can reach it.
3. **The privilege catalogue is versioned, or a test notices when it changes.** A removed or renamed
   privilege silently changes what existing roles confer. Cheapest form: a test carrying the
   expected list of names, so removing one is a deliberate act with a failing build in front of it.
4. **The group-manager axis is decided before the authorization check is written**, per §4d's
   closing paragraph. Recorded as a condition rather than left to sequencing, because it is
   invisible until it is expensive.
5. **No screen appears that its reader cannot use** — [ADR-034](ADR-034-server-and-studio.md)
   condition 1, restated because the roles screen is the first one whose whole subject is who may
   see which screens.

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
