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
- Found by the first CI run, and invisible on Windows: on Linux the base library looks for OpenLDAP as
  `libldap-2.6.so.0`, `-2.5.so.0` or `-2.4.so.2`, and Ubuntu 24.04 — the image's base and CI's — ships 2.6 as
  `libldap.so.2`. Every directory call was a `DllNotFoundException`. `LdapDirectory` now loads `libldap.so.2` when the
  library's own names fail; a probe in `aspnet:9.0-noble` on arm64 failed without that and reached the network with
  it.

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

5.6 **Added 2026-10-31, conditions 1 and 3.** A directory may be given **a certificate authority to trust**
(`trustedCertificate`, PEM), against which its TLS certificate is checked besides the machine's own — a domain's own CA,
as Active Directory Certificate Services issues, which a server outside the domain does not trust. On Windows the chain
is built here, in the LDAP client's certificate callback, and the host the address names is checked there too; on Linux
OpenLDAP is told to read a directory of trusted certificates for that connection, written under the state directory.
Absent from a request, it is kept; empty, it is removed. And a directory may **follow groups inside groups**
(`nestedGroups`, with `groupBase` where groups are searched, the user base's domain by default): after the person's own
bind succeeds, one more search with Active Directory's `LDAP_MATCHING_RULE_IN_CHAIN` on `member`, its results added to
`memberOf`'s. Off by default, as before; OpenLDAP does not answer that rule.

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
| — | A directory's groups are in an attribute of the person's entry (AD's `memberOf`) | True of AD and of OpenLDAP with the memberof overlay; a directory that lists members on the group entry instead is not read — revisit trigger. Since 2026-10-31 groups inside groups are also read from the group entries' `member`, through AD's chain rule, when a directory asks |

## 8. Dependencies

**Depends on:** [ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md), [ADR-015](ADR-015-authentication.md),
[ADR-036](ADR-036-groups.md) (groups here).

**Depended on by:** [ADR-090](ADR-090-sign-in-through-a-saml-provider.md), whose SAML groups go through the same mapping.

## 9. Revisit triggers

- A directory whose groups list their members rather than the member listing its groups.
- An organisation that needs a removal to take effect before the next sign-in.

## 10. Dissent

None recorded.

## 11. Conditions

1. **A real Active Directory signs somebody in** — the suite's directory is honest and is ours.
   **PARTLY DISCHARGED 2026-10-16 — Samba 4's Active Directory domain controller, which is AD's schema and
   protocol but not Windows** ([tools/ad-probe](../../tools/ad-probe/), a throwaway container on the VPS's loopback
   through a tunnel, against the local fixture). Registered with the defaults the console offers — a service
   account's DN, `CN=Users` as the base and the filter that tries `sAMAccountName`, `uid` and `userPrincipalName` —
   the check answered *reachable, a directory*, and `jane.doe` signed in **as each way people type an AD name**:
   `jane.doe`, `GRATICULA\jane.doe` and `jane.doe@graticula.test`, all to one account; a wrong password was
   refused alike. The account came with AD's `displayName` (*Jane Doe*); `GIS-Publishers` by its short name gave
   the publisher role, marked as the mapping's; `CN=Planners,CN=Users,…` by its DN put her in the group it maps to.
   **The probe's limit, stated**: the DC refused a simple bind over plain LDAP — *Strong authentication is required
   for this operation*, Samba's default and where Windows DCs are heading — so the probe allowed it for the run,
   because the server under test was on Windows and would not trust the DC's self-signed certificate over
   `ldaps://`. **Still owed**: a Windows DC, over `ldaps://` or StartTLS with a certificate the server trusts.
   **PARTLY DISCHARGED 2026-10-31 — `ldaps://` with a certificate the server trusts, and no plain simple bind; the
   Windows DC is still owed and is the owner's to provide.** The probe's limit above is gone. The Samba DC now keeps its
   default — a simple bind over plain LDAP answered *Strong(er) authentication required (8)* on the DC itself — and
   publishes only 636; its certificate comes from a certificate authority made for the run, for `dc1.graticula.test`,
   `localhost` and `127.0.0.1` ([tools/ad-probe/setup.sh](../../tools/ad-probe/setup.sh)). Against the local fixture
   through a tunnel ([signin.py](../../tools/ad-probe/signin.py)): registered with no authority, Check answered `502`
   and `jane.doe` was refused `401` — the Windows server does not trust that certificate; given the authority as the
   directory's certificate to trust (§5.6), Check answered *reachable, a directory*, and `jane.doe` signed in as
   `jane.doe`, `GRATICULA\jane.doe` and `jane.doe@graticula.test` to one account, *Jane Doe*, a publisher by the
   mapping; a wrong password `401`. The server was configured the way an operator would, by pasting the CA's PEM into
   the directory's settings — on a Windows server the alternative, the machine's Trusted Root store, needs the
   administrator rights this run did not have. Regression, in the suite and on CI's Linux too:
   `DirectorySignInConformanceTests.A_directory_over_ldaps_is_trusted_by_the_authority_it_is_given_and_by_nothing_else`
   — the suite's directory speaks `ldaps://` with a certificate from a CA made per run; without the authority the
   check fails at the TLS handshake and nobody is bound, with it the check passes and the person signs in, a form that
   does not send it keeps it, and taking it away refuses again. It fails without §5.6, which had nowhere to give the
   authority. **Linux's half** (OpenLDAP told a trusted-certificates directory per connection) was written and not run
   here — the local server is Windows; that test exercises it on CI's Ubuntu runner. **Still owed, the owner's**: a
   Windows Server DC, whose LDAP signing and channel-binding defaults are where Samba's differ.
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
   **DISCHARGED 2026-10-31 — built as a directory's option rather than waiting to be asked** (the owner's *"Tamamını
   bitirelim artık"*), off by default so nothing changes for a directory already registered (§5.6). Against the same
   Samba 4 AD DC, over `ldaps://`: `jane.doe` is in `Planners` and `GIS-Publishers`, and `Planners` is in `All-GIS`;
   her `memberOf` lists the first two only, and the DC's own answer to
   `(member:1.2.840.113556.1.4.1941:=<her DN>)` is `Planners`, `All-GIS`, `GIS-Publishers`. With `All-GIS` mapped to a
   group here, she was **not** in it after signing in with nested groups off, and **was** after the next sign-in with
   them on ([tools/ad-probe/signin.py](../../tools/ad-probe/signin.py), against the local fixture). Regression:
   `DirectorySignInConformanceTests.Groups_inside_groups_are_followed_when_the_directory_is_set_to` — the suite's
   directory now answers the chain rule as AD does, walking `member` through groups; off, the group around `Planners`
   is not reached and the directory is asked no chain search; on, it is, and the mapping's role and group apply; a wrong
   password asks nothing more. It fails without the option, which the API did not take.
   *(2026-10-16, observed rather than decided: against the same Samba DC, a person in `Planners`, which is itself in
   `All-GIS`, did not join the group `All-GIS` maps to — `memberOf` lists direct groups only, as this condition says.
   Following the chain would be one more filter (`LDAP_MATCHING_RULE_IN_CHAIN`, which Samba and Windows both answer);
   whether an organisation needs it is still the question.)*