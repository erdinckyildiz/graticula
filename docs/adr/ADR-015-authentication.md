# ADR-015 — Authentication

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-13 |
| **Answers** | §41 · blocker **B4** · part of security.md §6's *not yet written* |

---

> **Scope note, 2026-08-18 — v1 serves PostGIS only, and the other engines are
> deferred rather than cut.** This decision reasons about several database engines.
> Owner decision: *"Şimdilik postgis ile gideceğiz. Sonra diğer db'ler eklenecek. V1'de
> sadece Postgis olarak kalabiliriz."* — [v1-scope](../v1-scope.md) §3a, which is the one
> place that says what the deferral means.
>
> **The multi-engine reasoning here is kept on purpose**, because it is what the second
> engine will be built from and because deleting it would make it be re-derived later
> from nothing. What it is not is a description of what v1 does. Where a sentence below
> reads as *the server supports Oracle today*, it has been corrected; where it reads as
> *this is how several engines would be supported*, it stands and waits.
>
> [D-27](../architecture-debt.md).

## 1. Context

[security.md](../security.md) §2 designed **authorization** — roles plus
owner-set sharing, delegating to database row-level security where the provider
allows it. It never designed **authentication**, and §6 lists it among the
unwritten.

It is blocker **B4** because identity is upstream of everything user-facing: the
admin API, the publisher role, item ownership, audit, RLS delegation and the
cache key all consume an identity that does not yet exist. Nothing can be
written first and have authentication threaded through later without rewriting
it.

**Two constraints shape this more than any preference.**

### 1a. The identity has to survive into the database

§2's authorization delegates to RLS using transaction-scoped `SET LOCAL ROLE`
(D-01). That means an authenticated principal is not just a token our code
understands — **it must map to a database role name**, deterministically, for
every provider that supports delegation.

This rules out identity models that are purely opaque or purely claims-shaped.
Whatever we authenticate to, it must yield a **stable, mappable principal name**,
and the mapping from principal to database role is configuration an administrator
controls.

### 1b. ArcGIS compatibility drags in a legacy token scheme, and it is worse than it looks

Q-17 committed to full ArcGIS FeatureServer compatibility so that existing
clients keep working. Those clients authenticate by calling `/generateToken` and
then sending the token **as a `token=` query parameter**.

**A credential in a URL leaks by design.** It lands in server access logs, in
every reverse proxy and load balancer log in front of us, in browser history, and
in `Referer` headers sent to third parties. This is not our design error — it is
inherited, and the whole point of Q-17 is that unmodified clients work.

We cannot refuse it without breaking the compatibility we chose. What we can do
is bound the damage, and §4 does.

---

## 2. Decision — principals, and there are three kinds

| Principal | Authenticates with | Notes |
|---|---|---|
| **User** | Local password, OIDC, SAML | A person. Owns items (§2.0), holds roles |
| **Service** | API key, or mTLS client certificate | A machine. Never owns items; roles only |
| **Anonymous** | nothing | A real principal, not the absence of one — see §2a |

**Anonymous is a principal.** Open data portals are a normal deployment of a GIS
server, and modelling anonymous as *no identity* produces `if (user == null)`
scattered through every authorization check, which is where bugs live. It gets a
name, it can hold roles, and a layer can be granted to it. Refusing anonymous
access entirely is then a configuration, not a special case.

---

## 3. Decision — tokens are opaque and server-side, not JWT

**Access tokens are opaque random strings, with state in the platform store.**

This was an awkward choice a week ago and is cheap now. [Q-70](../open-questions.md)
made the platform store **mandatory PostgreSQL**, so there is always a
transactional store within reach of every node. The main argument for JWT —
statelessness, so you need no shared store — buys nothing when a shared store is
guaranteed.

What we get instead is the thing JWT cannot give:

- **Revocation that works.** Disabling an account or revoking a session takes
  effect on the next request. A JWT remains valid until it expires, so firing an
  administrator would leave their token live for the token lifetime, and the only
  fixes are a blocklist — which is server-side state, i.e. this decision arrived
  at by a worse road — or lifetimes so short they hurt.
- **Session listing.** An administrator can see and terminate active sessions,
  which is table stakes for an admin surface and impossible with bearer JWTs.
- **No signing-key rotation problem**, and no algorithm-confusion vulnerability
  class.

**Counter-argument, recorded:** a store lookup per request. It is one indexed
primary-key read against a database we already hold a pooled connection to, and
it is cacheable in-process with a short TTL bounded by the revocation delay we
are willing to accept. If measurement contradicts this, §9's condition covers it.

**JWTs still appear at the edges** — an OIDC provider issues them and we validate
one during login. We do not mint them as our own session tokens.

---

## 4. Decision — the ArcGIS token surface, and how its damage is bounded

We implement `/generateToken` and accept `token=` in the query string, because
Q-17 requires unmodified clients to work.

Four mitigations, all required:

1. **Query strings are redacted on those routes before logging.** Not
   "configured to be" — redaction is the code path, and logging the raw query on
   a token-bearing route is the bug.
2. **Header form is preferred and advertised.** Clients that can send
   `X-Esri-Authorization: Bearer …` are told to, in documentation and in the
   capability report.
3. **ArcGIS-issued tokens are short-lived by default and separately scoped.** A
   token that leaks into a `Referer` should expire before it is useful, and it
   should not be usable against the admin API. Compatibility tokens grant the
   compatibility surface.
4. **They are revocable and listed like any other session** (§3), so a leak has
   a remedy other than waiting.

**This is a deliberate weakening of the security posture, accepted in exchange
for the migration path**, and it belongs in the record rather than in a footnote.
If §2's *never degrade silently* applies to capabilities, the same honesty
applies to security trade-offs.

---

## 5. Decision — identity sources

| Source | Status | Note |
|---|---|---|
| **Local accounts** | **First-class, always present** | Not a fallback. Air-gapped sites may have no reachable IdP, and Q-15 assumes none |
| **OIDC** | Supported, free | Honua gates this at **Pro**; Q-49's positioning is that we do not |
| **SAML 2.0** | Supported, free | Honua gates at **Enterprise**. Still common in government and defence, which are plausible customers |
| **SCIM 2.0** | Supported, free | Provisioning, not authentication. Included because Q-83 put it in scope |
| **API keys** | Service principals only | Long-lived by nature, so scoped narrowly and revocable |
| **mTLS** | Off by default | [ADR-014](ADR-014-tls-and-certificates.md) §6 validates the certificate; **this ADR interprets the identity in it**. That boundary was set deliberately |

**Local accounts are first-class rather than a bootstrap convenience**, and that
is a real position: it means password storage, lockout policy and rotation are
ours to get right rather than delegated to an IdP.

Passwords are stored with **Argon2id**. Failed attempts are rate-limited per
account *and* per source address — per-account alone lets an attacker lock out
every user they can name, which converts a brute-force defence into a
denial-of-service tool.

---

## 5a. Decision — one identity store, and why federation is not needed

*Added 2026-08-16, because the owner asked whether we need separate users the way
ArcGIS has them, and the answer turned out to be recorded nowhere.*

**There is one identity store for the whole deployable, and one list of people in
it.** No second store for administration, none for content, none per surface.

This is the kind of decision §2 of `CLAUDE.md` calls informal when it goes
unwritten, and it was: [ADR-019](ADR-019-portal-server-split.md) §3 fused Portal,
Server and Data Store into one deployable, this ADR listed the identity *sources*,
and [Q-93](../open-questions.md) closed federating into somebody else's Portal.
Three decisions implied a single store and none of them said it.

**The ArcGIS structure is not evidence for two stores — it is evidence against
them.** ArcGIS Server has its own security store and its own primary site
administrator; Portal has members and identity providers. Federation does not
connect the two. Esri's own documentation is explicit that it *replaces* one:
once a site is federated, the portal's security store controls all access to the
server, and that model replaces the server's identity store **including all users
and roles configured in Server Manager**. Afterwards even Server Manager is
entered with a portal account. The two stores exist because two products were
sold separately, on different licence metrics; federation is the repair for that,
not a design anybody would choose from scratch. Building two stores here would
oblige us to build federation to undo them, and Q-93 means we do not even have to
speak its protocol.

**The peer does not contradict this either.** Its console holds no user store at
all: it binds to the server by base URL, reads with an administrative API key,
and requires a forwardable operator bearer through a per-operator BFF for any
mutation, failing closed without one. What is separated there is *surfaces* and
*processes* — not people.

**What is separated, and it is not a second list of users:** the principal kinds
of §2 (user, service, anonymous), and the two authorization axes of
[ADR-018](ADR-018-authorization-and-roles.md) §3 (user type × role). *Administrator
of the server* and *author of content* are told apart by a role on one account,
which is what Portal itself does — the split ArcGIS has between its two stores is
not the split between those two jobs.

**The one thing worth taking from the primary site administrator design is the
failure mode it exists for.** Esri's guidance for disabling that account carries a
warning that reads as a scar: make sure the identity store you are moving
administration to is in working order and available, because if it becomes
corrupted or unavailable you will not be able to sign in to the site at all — and
once the account is disabled, changing the identity store is refused until it is
re-enabled. So their break-glass account is the local one that *bypasses* the
store, and the operation it guards is precisely the one that can lock everybody
out.

§5 makes local accounts first-class rather than a fallback, but the reasoning
given there is the air-gapped site — **a deployment with no IdP, which is not the
same as a deployment whose IdP broke.** The distinction costs nothing today and
that is precisely why it is written down now: §9a records that no external
identity source is built at all (D-10), so there is currently no IdP to break and
every account is local. The lockout arrives with the first one, and it arrives by
a plausible route — treating the IdP as *the* identity store and local accounts as
a legacy to be tidied away. Condition 5 falls due in that change rather than
after it, and [Q-111](../open-questions.md) holds the question until then.

**Where this section's outside evidence comes from**, per
[ADR-030](ADR-030-reading-the-reference-implementation.md) condition 1: the
federation and primary-site-administrator behaviour is from Esri's published
ArcGIS Enterprise administration documentation, which is the citable source
because it is a public description of a public product's behaviour; the peer
console's authentication model is from its **public** repository, logged in
[reference-reading-log.md](../research/reference-reading-log.md). Neither claim
rests on the anonymised checkout, and no part of this decision was derived from
reading its source.

---

## 6. Decision — first-start bootstrap

On first start with no accounts, the server generates a **one-time setup token**,
writes it to the container log, and refuses all other requests until it is used
to create the first administrator.

Considered and rejected: a default username and password, which survives into
production; and an unauthenticated setup window, which is a race with whoever
scans the network first.

The setup token is single-use, expires, and its use is the first audit entry.

---

## 6a. Decision — password rules are length only, and the floor is 8

*Added 2026-08-14, when the first password anybody actually tried to set was
refused.*

**No composition rules.** Requiring an uppercase, a digit and a symbol
measurably pushes people toward `Password1!`, which is in every wordlist. NIST
SP 800-63B dropped composition requirements for exactly this reason.

**The minimum is 8, lowered from 12.** 12 was our own number with no reasoning
recorded behind it — it was not derived from a threat model, a standard, or a
measurement, and it refused the server's own root password. The alternative on
the table was a direct write to the store to get around it, which is the
outcome that matters here: **a rule nobody can justify does not get followed,
it gets bypassed, and then the stated policy is a lie.** 8 is the floor
SP 800-63B sets for a user-chosen secret, so it is a number with a source.

**What actually carries the weight, and it is not the length rule.** An
8-character password is weak against an offline attack on a stolen hash, and it
was weak at 12 too. The defences that do the work are Argon2id at 19 MiB and 2
iterations per guess (§3), which makes offline guessing expensive, and
`LoginService`'s rate limit, which makes online guessing slow. Neither changed.

**Added 2026-08-24: a common-password check, which is worth more than the length
rule and is not a breach check.** [D-23](../architecture-debt.md) recorded that
`correct horse battery staple` and `Passw0rd!` were treated alike, and recorded
why it had not been repaired: Pwned Passwords is about 10 GB and the k-anonymity
API leaks a hash prefix to a third party, and **neither fits a product whose
baseline is one deployable against one PostgreSQL**. That is still true, so this
is not that.

`CommonPasswords` carries a few hundred **bases** — what sits at the top of every
published list, which is where credential stuffing actually lives — and reads a
password twice before comparing: once literally, once with the letter-for-digit
substitutions undone. Either reading matching, with or without a trailing run of
digits, is a refusal. So `Passw0rd!`, `P@ssw0rd`, `password123` and `Sifre123`
all reach `password` or `sifre`, and `12345678` matches itself.

**Two readings rather than one, and that is not a detail.** A single
normalisation that undoes substitutions turns `12345678` — the most common
password there is — into letters, and the entry stops matching itself. The first
version did exactly that and the tests said so.

**A deployment that wants the real thing points at it.**
`Graticula:CommonPasswordFile` names a newline-separated list of any size, read
at startup and added to the built-in one. A named file that is absent **refuses
to start**, because a deployment that pointed at a breach corpus and silently got
a few hundred entries would believe it had a defence it does not have.

**What it does not claim.** A password it accepts may still be published. §3's
Argon2id and `LoginService`'s rate limit remain what carry the weight against
guessing; this is aimed at the one thing neither touches — a password that is
already known.

**The rule is stated in exactly one place** — `AuthEndpoints.MinimumPasswordLength`
— and every message that quotes a number reads it from there. The setup log
message stated 12 for as long as it took to notice, which is the drift that
teaches people the messages are not worth reading.

---

## 6b. Decision — a password an administrator hands over is dirty, and the server picks it

**Added 2026-08-17 on the owner's correction, which replaced something worse.** The first version
of member creation had an administrator type both the first password and any reset, and its own
hint admitted the consequence: *"this one is known to whoever typed it here."* **A note describing
a hazard is not a control.** It also put one person's habits on another person's account — their
idea of *long enough*, their reuse, their pattern across the three accounts they made that morning.

The owner: *"kullanıcıya yeni parola veremem. sistem bana yeni bir parola verir. bunu kullanıcı ile
paylaşabilirim. ama sistem otomatik olarak o parolayı kirli kabul eder. kullanıcı giriş yapınca
değiştirmek zorunda kalır."* — I cannot give the user a new password; the system gives me one, I can
share it, and the system treats that password as dirty automatically, so the user has to change it
when they sign in.

**The three parts, and each one is load-bearing:**

- **The server chooses it.** `POST /admin/members` and `PUT /admin/members/{name}/password` take
  **no password field at all** — the reset takes no body. A field would let a caller choose somebody
  else's secret, which is the thing being removed. `IssuedPassword` produces sixteen characters of a
  thirty-character alphabet in four hyphenated groups: about 78 bits, and *readable aloud*, because
  it will be. The ambiguous characters are out for Crockford's reason, and the hyphens are there so
  a reader can check they have it.
- **It is returned once and stored only as a hash.** An administrator who loses it issues another.
  The response says so, because *where do I see it again* is the next question and the answer is
  *nowhere*.
- **It is dirty from the moment it exists**, and *must change* is enforced rather than requested.
  `local_credential.must_change` (migration 22) is set by every write the member directory makes and
  cleared **only** by the self-service change. So the way out of *must change* is the member
  changing it, and no argument or endpoint can produce a permanent password on somebody else's
  account.

**How *has to* is enforced: one middleware, not a screen.** A caller whose credential is dirty gets
`403` on everything except the password change, `whoami`, logout, the console's own files, and the
anonymous surfaces. **An allow-list rather than a deny-list**, because a deny-list lets every route
added afterwards through by default, and this control's whole job is that nothing else answers.

- **403 and not 401**, because the credential was accepted and the session is real — which is
  exactly why the caller can be told what to do about it. A 401 would send a client back to sign in
  and land it here again.
- **Signing in succeeds**, and it must: they need a session in order to change the password.
- **The anonymous surfaces stay open.** The services directory answers strangers by design
  (ADR-023) and health answers an outage (D-18). Refusing them would make a dirty password *less*
  than no credential at all, and would break a browser that is signed in and reading a map.
- **`/rest/whoami` reports `mustChangePassword`**, because a server that enforces a rule it does not
  advertise produces a client that signs somebody in and then watches every screen answer 403.

**The flag is read on every request and not stamped into the token**, which is the same rule sharing
and started/stopped follow. Three defects this month came from caching a fact of that kind, and this
would have been the fourth. It also gives the better behaviour: setting your own password takes
effect on the **next request**, in the session you already hold, rather than on your next sign-in.
Measured end to end — issue, sign in, `403` on `/content/layers`, change, `200` on the same session,
administrator resets, `403` again.

**What this does not do, said so the gap is a decision.** There is no invitation flow, because this
server cannot send a message at all — no mail, no token table, no expiry policy — so the alternative
to an administrator relaying a password was no member at all. The issued password does not expire;
it is single-use in effect because the account cannot do anything else until it is replaced, which
is a weaker property than an expiry and a much simpler one. And nothing checks any password against
known breached lists — §6a's common-password check is a few hundred bases and a
deployment's own file, not a breach corpus, and an issued password is generated
rather than chosen so it does not meet either.

## 6c. Decision — a member is removed by disposing of what they own, and the operator chooses how

**Owner decision, 2026-08-18, answering [Q-116](../open-questions.md).** There was no member
delete at all: `/admin/members` could create, list, set a role, set a password, disable and enable.
Disabling covers the case that actually arises — somebody leaves, their account stops working, their
content keeps serving — so the omission was defensible. What it blocked was smaller and real: a test
or a script that makes a throwaway account and does not want to leave it behind, and it left
[ADR-034](ADR-034-server-and-studio.md) condition 1 one sentence short of dischargeable.

The owner: *"bir üye silinirken, eğer ki sahip olduğu gruplar ya da nesneler varsa onlarla ilgili
bir ibare çıkar. şu kadar gruba, şu kadar nesneye sahip diye. ne yapayım diye sorar. sileyim mi,
başkasına mı aktarayım. başkasına aktar dersen, o nesneleri başkasına aktarır. grupların da
sahipliği başkasına geçer. sil dersen hem grubu hem nesneleri siler. şu anda grubumuz yok ama
olacak."*

### The shape

**A member who owns nothing is removed outright.** No question, because there is nothing to ask
about.

**A member who owns something cannot be removed by a request that did not say what to do with it.**
The refusal carries the counts — *this many services, this many folders* — and names the two
dispositions. That is the whole of the decision: the server does not choose, and it does not
proceed on a request that failed to.

- **`transfer`** moves ownership to a named member. The services keep serving, the folders keep
  their contents, and nothing is unpublished. Every URL a client holds keeps working, which is why
  this is the disposition an operator should reach for by default.
- **`delete`** removes what they owned along with them: the services, the layers inside them, and
  the folders. Data in the datastore goes with the layers, as unpublishing already does.

**Groups are in the design and not in the schema.** The owner's *"şu anda grubumuz yok ama olacak"*
is the reason they are named here anyway: a group in the ArcGIS sense — a set of members with items
shared to it — is [ADR-018](ADR-018-authorization-and-roles.md)'s deferred sharing scope, and when
it arrives it is a third owned thing that both dispositions must cover. Writing the disposition
around *owned things* rather than around *services* is what makes that an addition rather than a
redesign.

### What is owned, measured rather than assumed

Three tables carry `owner_principal_id`, and only two of them mean anything:

- **`service`** — live and read; the catalogue reports the owner, and a service holds its layers.
- **`folder`** — live; a folder created by a publisher belongs to them.
- **`layer`** — vestigial. Migration 11 moved ownership onto the service and nothing has read the
  layer column since ([D-33](../architecture-debt.md)). It is written on transfer anyway, because
  leaving a stale principal id in a column somebody may one day read is how the next
  [D-24](../architecture-debt.md) starts.

**Group layers are not owned and do not appear here.** `group_layer` has no owner column — measured,
not assumed — so a group layer belongs to whoever owns its service and moves with it. That is also
how the ambiguity in the owner's word *grup* was settled: it cannot mean a group layer, because a
group layer has no owner to transfer.

**Data sources are not owned.** Registering one is an administrative act on the deployment, and
`data_source` carries no owner column. So removing a member never orphans a credential.

### The refusals

- **The last administrator cannot be removed**, whichever disposition is asked for. A server with no
  administrator cannot be recovered in band ([D-14](../architecture-debt.md)), and doing it by
  accident while tidying up accounts is exactly how it would happen.
- **A member cannot transfer to themselves**, and the target must exist and not be disabled —
  transferring to a disabled account produces content nobody can administer.
- **A member cannot remove themselves.** Not a safety rule about the server; a rule about the
  request, because the session doing the work would be revoked halfway through it.

## 6d. Decision — a store with no administrator is recovered by a command, not by re-arming setup

*Added 2026-08-25 by owner decision, closing [Q-137](../open-questions.md) and
[D-14](../architecture-debt.md).* The owner's words were *"öyle bir şeyin olamaması lazım.
eğer ki olabiliyorsa uygulama içerisine bir cmd koyalım. kurulum yerine. bir tane
admincreator"* — that state should not be reachable, and where it still is, the answer is a
command inside the application rather than a re-armed setup.

**The state.** A platform store with accounts and nobody holding `administrator` keeps
serving reads and can do nothing administrative. §6's bootstrap does not fire, because
`AnyUserExistsAsync` is true. It was found by running the ADR-018 upgrade against a store
set up before it.

**Why not the obvious answer.** Issuing a fresh setup token on that path would print a
credential to the log every time the last administrator's grant disappeared — and *the last
administrator's grant disappeared* is a state an attacker would like to arrange. The
credential's blast radius becomes wherever logs are shipped, which is a place §6's threat
model never considered.

**What the command needs, and why that is the whole argument.** `dotnet Graticula.Host.dll
tools admincreator` needs a shell on the host and the platform store's connection string.
That is the same access the recovery `INSERT` already required, so it grants nothing to
anybody who did not already have it. What it adds is that the recovery is a supported
operation — with the password policy, the audit trail and the refusals below — rather than
a statement somebody writes at three in the morning against a schema they are reading for
the first time.

**It refuses on a healthy store**, and says so rather than succeeding quietly. A recovery
tool that also works where recovery is not needed is a way to mint an administrator, and
the fact that its user could have done it in SQL anyway is not a reason to make it one
call. This is the security property of the whole thing, and it is what
`A_store_that_already_has_an_administrator_is_refused` covers.

**The password is the operator's and is one-use.** It comes from
`GRATICULA_ADMIN_PASSWORD`, because an argument lands in shell history and in the process
table; `--password` is still accepted, because a recovery at three in the morning should
not fail on ergonomics, and the refusal text says what that costs. The floor is §6a's 8 —
`AuthEndpoints.MinimumPasswordLength`, referenced rather than restated, after a first draft
carried its own 12 and contradicted §6a in the same repository. The common-password list
applies. And the credential is written `must_change`, like every other password this server
issues (§6b), so the account signs in and reaches nothing but `POST /rest/auth/password`
until its owner sets their own — which makes the copy in the shell history stop working the
moment it is used once.

**It repairs as well as creates.** A partial restore can leave the account and lose the
grant; refusing that case would send somebody to SQL for the thing this exists to take away
from them. A grant that does not take is reported with its own exit code rather than
swallowed, because an account that can sign in and do nothing looks like success from the
shell.

**Measured against a real store, 2026-08-25**, not only against fakes. A throwaway schema
at migration 36 holding two principals and no administrator: the four refusals produced
exit 2, 2, 2 and 3; the success path created `admin` with the `administrator` grant and
`unrestricted` type; signing in as it returned **200**; a second run refused with exit 3.
**One thing was found that way and could not have been found otherwise** — the first
draft's success message sent the operator to the members screen, and the members screen
answers **403** while the issued password is still in place. The message now names
`POST /rest/auth/password`, and `The_success_message_names_the_route_that_answers` fails if
it stops.

**What is unchanged.** §6's bootstrap still runs only on a genuinely empty store, and
ADR-035 §4b's guards still refuse every API route that could produce this state. The
command is for the three roads that remain: a migration, a partial restore, and direct
access to the platform store.

---

## 7. What this hands to other decisions

- **RLS delegation (§1a)** gets a stable principal name and an
  administrator-controlled mapping to database roles.
- **Cache keys** get an identity for D-02's grant fingerprints.
- **Ownership (§2.0)** gets an owner: user principals only. A service principal
  cannot own an item, because ownership carries sharing decisions and a machine
  has no judgement about them.
- **Q-75's question is answered in part.** Publishing *data* and publishing
  *code* are different grants. A Python geoprocessing tool is executable code on
  our server, and the role that permits it is **separate from the publisher
  role**, defaulting to administrators only. A publisher uploading a shapefile
  and a publisher uploading a script are not the same risk and must not share a
  permission.
- **Audit** gets a subject. Every mutating request records principal, source
  address, and — for compatibility tokens — that it arrived by that route.

---

## 8. Consequences

- [security.md](../security.md) §6 loses *authentication itself* from its
  unwritten list; **privilege escalation paths and secret handling remain**.
- The platform store gains accounts, sessions, API keys and audit tables. All
  small, all precious, all in the ADR-002 backup path.
- The admin API gains user, role, session and key management — a substantial
  surface, and its **primary user is the GIS administrator** (Q-06a).
- **D-04 multi-tenant isolation is still not addressed.** Authentication tells us
  *who*; it does nothing about one tenant's expensive query degrading another's.
  Explicitly out of scope here so it is not assumed covered.

## 9. Conditions

1. **Session lookup cost is measured**, not assumed. If a per-request indexed
   read against the platform store is material at the concurrency ADR-007
   targets, the in-process cache TTL becomes a stated revocation delay rather
   than an implementation detail.
   **OPEN.** Implemented as one indexed read per authenticated request, with no
   cache. Unmeasured, so A-046 remains `UNVALIDATED`.
2. **Token redaction is tested by asserting on log output**, not by reading the
   code. §4.1 fails silently otherwise, and silently is the only way it fails.
   **DISCHARGED 2026-08-20 — and it became due on 2026-08-20 and was missed by half a
   day.** This condition said it *"becomes due in the same change that adds
   `/generateToken`, not before"*. That change shipped that morning with the `token=`
   query parameter and without the redaction, and `Authentication`'s class remark
   still read *"not accepted yet … so it waits for them"* — the code and its own
   documentation disagreeing in the direction of a skipped guard.

   **[Security gate 2](../reviews/security-gate-2.md) found the consequence rather
   than the contradiction**: a `root` session token read out of this server's log file
   and replayed against a private layer.

   `QueryRedaction` is the code path §4.1 asks for — the framework's own request
   logging writes the raw URL before any middleware runs, so it is filtered off and
   this server writes its own line. Seventeen unit tests cover the function and
   `TokenIsNotLoggedTests` asserts on the log itself, which is the form this condition
   demands: a correct function that nothing calls redacts nothing.
3. **Lockout is tested for the denial-of-service inversion** — that locking one
   account cannot be used to lock out an organisation.
   **DISCHARGED**, and the shape of the fix matters more than the test.
   A second limit does not by itself remove the inversion — what removes it is
   *where each limit sits relative to verifying the password*. The address limit
   is a gate before verification, because Argon2id is expensive and the endpoint
   would otherwise be a CPU amplifier. The account limit is consulted only
   *after* verification has already failed, so a request carrying the correct
   password is honoured no matter how much of the account's budget an attacker
   has spent. **Nothing here ever locks an account.** Pinned by
   `LoginServiceTests.The_account_limit_cannot_be_used_to_lock_someone_out`.
4. **The bootstrap token cannot be reused**, tested, including after a restart
   that occurs mid-setup.
   **DISCHARGED.** The token is stored hashed, and redemption marks it used by a
   *conditional* update in the same transaction that creates the administrator —
   so two concurrent redemptions produce one administrator, and a failure partway
   rolls the token back to unused rather than spending it on nobody. A restart is
   simulated by a fresh store over the same database. Pinned by
   `SetupStoreTests.A_token_survives_a_restart_and_is_still_single_use` and
   `..._Two_concurrent_redemptions_produce_exactly_one_administrator`.
5. **A local administrator can still sign in when the configured external
   identity source is unreachable or misconfigured**, and it is tested by
   breaking one rather than by reading §5.
   **NOT YET APPLICABLE, and for the same reason as condition 2.** §9a records
   that OIDC, SAML and SCIM are not built (D-10), so there is no external source
   to break and no lockout to have: today every account is local. The condition
   becomes due **in the same change that adds the first external identity
   source**, because that is the change that creates the failure — and the order
   matters, since the natural implementation is to treat an IdP as the identity
   store and local accounts as legacy, which is exactly how the lockout §5a
   describes arrives. See [Q-111](../open-questions.md).

### 9a. What is implemented, as of 2026-08-13

**Real:** local password accounts with Argon2id (parameters stored per
credential, re-hashed on login when the cost is raised); opaque server-side
sessions with immediate revocation; the first-start bootstrap; and both rate
limits.

**Not built, tracked as D-10:** OIDC, SAML 2.0, SCIM 2.0, API keys, mTLS
identity, and the ArcGIS `/generateToken` surface.

**Authentication without authorization, tracked as D-11.** A principal is
resolved and nothing consults it. Q-59 has not decided what the roles are, and
inventing them here is what review O3 warned against. The server says this at
every startup rather than letting it be assumed.

## 10. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-046 | An opaque-token session lookup per request is affordable at the concurrency ADR-007 targets | `UNVALIDATED` — §3's central bet. If false, the fallback is a longer in-process cache TTL, which trades revocation latency for throughput, and that trade should be a stated number rather than a default |
| A-047 | Every provider supporting RLS delegation can accept a principal name we generate, via administrator-controlled mapping | `UNVALIDATED` — §1a. PostgreSQL roles, SQL Server users and Oracle proxy authentication have different naming rules, length limits and case behaviour. A mapping that works on one may not on another |

## 11. Dissent

**Against opaque tokens.** The industry default is JWT and a reader will expect
it; choosing otherwise invites the question at every review. The answer is that
the usual reason for JWT — avoiding shared state — was deleted by Q-70, and
revocation is worth more to an administrator than statelessness is to us.

**Against implementing `/generateToken` at all.** It is a credential-in-URL
scheme we would never design, and §4 admits it weakens the posture. But Q-17's
entire value is that unmodified ArcGIS clients keep working, and a compatibility
layer that requires client changes is not a compatibility layer. The mitigations
bound it; they do not make it good.
