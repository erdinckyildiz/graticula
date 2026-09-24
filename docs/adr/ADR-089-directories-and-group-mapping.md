# ADR-089 — Sign-in through an LDAP directory, and groups mapped to roles and groups here

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-24, by owner decision. V-36's order — OIDC, then LDAP, then SAML — is the owner's (*"Üçü de, bu sırayla"*). Group mapping was put with LDAP when OIDC was decided. Asked then: a directory's or provider's groups map **to a role and to groups here** — *"Hem role hem gruba"*; they are applied **at every sign-in**; a role a mapping gives **is the mapping's**, not changed by hand; and the same mapping **serves an OpenID Connect provider's groups** too. Each answer was the option recommended, and each is ArcGIS Portal's enterprise-group behaviour or the nearest to it. |
| **Supersedes** | — (amends [ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md): its group mapping, left for later, is here; repays more of [D-10](../architecture-debt.md)) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Most organisations that ask for their own sign-in mean Active Directory, and many of those have no OpenID Connect
provider in front of it. And an account that signs in through an organisation is only half-managed until what it
may do follows the organisation's groups: otherwise every promotion and every leaver is a second change here.

## 2. Alternatives considered

### Alternative A — search, then bind as the person; groups applied at sign-in *(chosen)*

**Argument for.** The directory compares the password and nothing here stores one. It works with AD and OpenLDAP
alike. Applying groups at sign-in needs no connection to the directory at any other time.

**Argument against.** A person removed from a group keeps what it gave until they next sign in — at most a session's
length. A periodic sync was offered and not chosen.

### Alternative B — bind directly with a DN built from the name

**Argument against.** AD's names are not DNs, and one template fits one directory's layout.

### Alternative C — groups from a directory, synchronised hourly

**Argument against.** A second outbound schedule, and the owner chose sign-in.

## 3. Counterarguments to the preferred option

- **A local account of the same name is never asked of the directory**, so a person who has both signs in to the local
  one with its password: ADR-088's rule that accounts are never joined by a name.
- **The highest role any matched group gives**, and the provider's default when none does — so being taken out of the
  only mapped group demotes, which is what an organisation removing somebody means. The last administrator is not
  demoted by a sign-in, as Members refuses to by hand.
- **Groups here that no mapping names are not touched**, so a group an owner manages by hand stays theirs; and a
  manager of a mapped group is left a manager.

## 4. Evidence

- **`DirectorySignInConformanceTests`**, against a directory this suite runs — an LDAP server written with the base
  library's BER reader and writer, so the server's client (OpenLDAP's on Linux, Windows' on Windows) and it are two
  independent implementations:
  - a wrong password and an unknown name are both refused as `401`;
  - the directory's password signs in and makes the account, which is a publisher from `GIS-Publishers` and in the
    group `Planners` maps to, marked as signing in through the directory and with its role the mapping's;
  - changing that role by hand is `409`, naming the groups;
  - out of both groups in the directory, the next sign-in makes it a viewer and takes it out of the group;
  - a local account of the same name is never asked of the directory — no bind as the person is made;
  - an OpenID Connect provider's `groups` claim gives a role through the same mapping.
- Found writing the directory: the base library's writer refuses an ENUMERATED written as an integer, which the first
  version of the test directory did, and the platform client then reported the directory as unavailable.

## 5. Decision

5.1 **A directory is a sign-in provider of kind `ldap`** (migration 57): its address as the issuer —
`ldaps://`, or `ldap://` with StartTLS, plain `ldap://` only to this machine — the account it searches with as the
client id and that account's password as the secret, sealed; where people are, the filter that finds one (`{0}` for
the name, escaped per RFC 4515), and the attributes for a name to show, groups, and what never changes (the DN when
none).

5.2 **Signing in** is this server's own password form, the ArcGIS token endpoints and the OAuth sign-in page alike:
`LoginService` asks the directories for a name with no password here, after its address limit and inside its attempt
record. One entry found, then a bind as it; a filter that finds two finds nobody.

5.3 **Group mappings** (`/admin/identity-providers/{id}/groups`, `admin:manageSecurity`; a mapping to the
administrator role only by an administrator) map a group — by name, or a DN whose first part is that name — to a role,
a group here, or both. At every sign-in: the highest role a matched group gives, else the provider's default, when any
mapping gives roles; membership of every mapped group as the matched groups say.

5.4 **A role a mapping gives is locked on Members**, with the reason and where to change it; the API refuses it `409`.

5.5 **The console**: Server › Sign-in adds a directory beside a provider, and each has a *Groups* editor; Members says
who signs in through what, and whose role comes from where.

## 6. Consequences

**Positive.** Active Directory accounts sign in to the console, ArcGIS Pro and Field Maps with the passwords they have;
roles follow the organisation.

**Negative.** A removal takes effect at the next sign-in, not at once. A second outbound file, named in the air-gap
check. The image carries `libldap2`.

**Ports created.** `IDirectorySignIn`.

**State.** `identity_provider.kind`, `.ldap`, `.groups_claim`; `external_identity.role_managed`; `group_mapping`
(migration 57, expand).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A directory's groups are in an attribute of the person's entry (AD's `memberOf`) | True of AD and of OpenLDAP with the memberof overlay; a directory that lists members on the group entry instead is not read — revisit trigger |

## 8. Dependencies

**Depends on:** [ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md), [ADR-015](ADR-015-authentication.md),
[ADR-036](ADR-036-groups.md) (groups here).

**Depended on by:** SAML, next.

## 9. Revisit triggers

- A directory whose groups list their members rather than the member listing its groups.
- An organisation that needs a removal to take effect before the next sign-in.

## 10. Dissent

None recorded.

## 11. Conditions

1. **A real Active Directory signs somebody in** — the suite's directory is honest and is ours.
2. **The design review of the screens here**, with its findings repaired. **DISCHARGED 2026-09-24.** The review
   found three blockers, each confirmed in the code before it was repaired: a role lock that outlived the mapping that
   set it, so removing the mapping left the account unchangeable for ever (now read, not remembered: locked only while
   a mapping still gives roles, and released at the next sign-in); *Set password* on a directory's member, whose
   password here would be checked first and cut them off from the directory (now refused `409`, and the button is
   gone); and a sign-in form that told a directory's people it wanted "an account on this server" (it now names the
   directory, on the console and on `/rest/login`). Eleven more were repaired: the role asked whether or not first
   sign-in makes accounts, since it is also the role of anybody no mapped group gives one; the role order and the lock
   said on Groups; an administrator mapping warned on its row; a half-filled row marked rather than dropped; unsaved
   mappings confirmed before they are thrown away; the member's lock linking to that provider's Groups; the subject
   attribute defaulting to `objectGUID` and fixed once anybody has signed in; `memberOf`'s missing nested groups said;
   the copy per kind; the table at 390 px; `CONTOSO\jane` read as `jane`. Regressions:
   `SignInProvidersScreenTests`, three of whose tests fail with the repair taken out of the page served.
3. **Nested groups** — AD's `memberOf` lists direct groups only; whether an organisation's mapping needs the nested
   ones is not known until one is asked.
