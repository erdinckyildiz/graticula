# ADR-088 — Sign-in through an OpenID Connect provider

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-24, by owner decision. Asked V-36 — SAML, OIDC and LDAP — the owner chose all three, *"hemen"*, in that order: *"Üçü de, bu sırayla"*. Asked then how OIDC should behave: set **from the console**; whether a first sign-in makes an account is **left to the operator per provider** — *"ikisi de olabilir mi. seçenek bana bırakılsın. arcgis portalda da öyle"*; an account that signs in through a provider **stays its own**, never joined to a local one by e-mail; mapping the provider's groups to roles **comes later, with LDAP** — asked a second time, in plain words, after the first question was not understood. |
| **Supersedes** | — (amends [ADR-015](ADR-015-authentication.md) §5, whose OIDC row this builds; repays part of [D-10](../architecture-debt.md)) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

**Amended 2026-09-24 — [ADR-089](ADR-089-directories-and-group-mapping.md).** The group mapping this ADR left for
later is built there, and applies to a provider's `groups` claim as well as to a directory's groups.

## 1. Context

[ADR-015](ADR-015-authentication.md) §5 lists OIDC as *supported, free*, and [D-10](../architecture-debt.md) has
recorded since 2026-08-13 that only local passwords are built. The third ArcGIS review's V-36 put it plainly:
most public-sector and defence organisations ask for their own sign-in before anything else. The owner answered.

## 2. Alternatives considered

### Alternative A — authorization code with PKCE, validated here, ending in this server's own session *(chosen)*

**Argument for.** It is what OIDC Core and the OAuth security BCP recommend for a confidential client, and it
keeps ADR-015 §3 whole: the provider's ID token is checked once and discarded, and the credential is this
server's opaque, revocable session, as it is after a password.

**Argument against.** This server must reach the provider — the first outbound connection a signed-in deployment
makes. Held to one file, named in the air-gap check ([D-274](../architecture-debt.md)).

### Alternative B — ASP.NET's OpenID Connect handler and cookie authentication

**Argument for.** Less code.

**Argument against.** It brings its own cookie scheme and session model beside ADR-015's, and its configuration
manager fetches the provider's documents through connections the air-gap check cannot see.

### Alternative C — accept the provider's tokens as bearer credentials

**Argument against.** ADR-015 §3 rejected JWTs as session tokens for revocation; accepting someone else's would
reverse that for every signed-in request.

## 3. Counterarguments to the preferred option

- **A first sign-in can make an account an operator never saw.** Only when that operator turned it on for that
  provider, only with the role chosen there, and never an administrator — making one is an administrator's act
  (ADR-035 §4g).
- **Two accounts for one person** — a local one and a provider's — when both exist. The owner's choice, over
  joining by e-mail: a provider whose users can change their own address would otherwise hand over any local
  account with the same one.
- **A provider that renames a person** keeps them on their account, because the account is found by the subject
  the provider promises never to reuse, not by the name.

## 4. Evidence

- **`OidcSignInConformanceTests`** against a running server and a provider the suite runs on the loopback address,
  written with the base library alone so it cannot agree with the server's validation by sharing its code:
  - a first sign-in makes an account, named by the part of `preferred_username` before the `@`, with the role the
    provider was set to; a second sign-in finds the same account;
  - with automatic accounts off, an unknown person is refused and nothing is made; an account an administrator made
    by the provider's name is bound at the first sign-in; the same name from another subject afterwards is refused;
  - an ID token with the wrong **nonce**, a **signature** by another key, another **audience**, another **issuer**,
    or **expired** signs nobody in and makes nothing — five cases, each asserted;
  - a callback this browser did not start is refused without a session.
  - The provider refuses a token request without the client secret or with the wrong PKCE verifier, so each
    successful case also shows the server sent both.

## 5. Decision

5.1 **Providers are configured from Server → Sign-in** (`/admin/identity-providers`, `admin:manageSecurity`):
name, issuer, client id, client secret, scopes, the claim that names an account, whether a first sign-in makes
one and with which role and user type, and whether it is offered. The secret is sealed with the server's key, as a
data source's credential is, and never shown back. The page gives the redirect URI to register at the provider,
and *Check* reads the provider's discovery document and keys.

5.2 **The sign-in** is `/rest/auth/oidc/{id}/start` → the provider → `/rest/auth/oidc/callback`: authorization code
with PKCE (S256), a nonce, and a state in a cookie sealed with the server's key, `SameSite=Lax`, ten minutes, bound
to the browser that started it. The code is redeemed with `client_secret_basic` unless the provider takes only
`client_secret_post`. The ID token's signature, issuer, audience, lifetime and nonce are validated; the provider's
keys are read again once when a token names one not held.

5.3 **The account** is found by the provider and the `sub` claim. An account an administrator made ahead of time —
Members, *Signs in with* — carries the provider's name for the person and no subject, and the first sign-in by that
name binds the subject. With none, a first sign-in makes one when the provider allows it, else is refused with a
page that says so. A disabled account cannot sign in.

5.4 **The end is this server's own session and cookie**, issued as a password sign-in's is; the console exchanges
the cookie for a token as it already does for a directory sign-in.

5.5 **HTTPS to the provider**, except to the loopback address.

5.6 **The one outbound file.** `Oidc/OidcClient.cs` fetches the discovery document, the keys and the token; the
library it uses — `Microsoft.IdentityModel`, MIT — is handed text to parse and validate and opens no connection.

## 6. Consequences

**Positive.** An organisation's people sign in with the account they already have; the console, the directory and
ArcGIS clients that use this server's session all benefit.

**Negative.** A deployment with a provider connects to it. Group-to-role mapping is not built, so roles are set by
hand.

**Ports created.** `IIdentityProviderStore`.

**State.** `identity_provider` and `external_identity` (migration 56, expand).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Providers publish `/.well-known/openid-configuration` and sign ID tokens with a key in their JWKS | OIDC Discovery 1.0; not yet seen against a real provider — condition 1 |

## 8. Dependencies

**Depends on:** [ADR-015](ADR-015-authentication.md) (sessions), [ADR-035](ADR-035-role-privileges-are-editable.md)
(who may make an administrator), [ADR-076](ADR-076-oauth-for-registered-apps.md) (the same flow, this server
as the client rather than the provider).

**Depended on by:** the LDAP and SAML ADRs to come; group mapping, which the owner put with LDAP.

## 9. Revisit triggers

- An organisation asking for group-to-role mapping — the owner placed it with LDAP.
- A provider that cannot do PKCE.

## 10. Dissent

None recorded.

## 11. Conditions

1. **A real provider signs somebody in** — Entra ID or Keycloak, against the showcase or a deployment. The suite's
   provider is honest but is ours; the first person to meet a real one's quirks should not be the owner.
2. **ArcGIS clients' own sign-in pages offer it.** This server's OAuth authorize page (ADR-076) — where Field Maps
   and a web app send a person — offers only a password today.
3. **The design review of every screen here**, with its findings repaired. **DISCHARGED 2026-09-24.** Its blocker
   was the console's shared change listener returning early unless a layer's Fields editor was open, so the
   first-sign-in choice and New member's provider did nothing — and ADR-087's Domains screen had the same fault in
   its new-domain kind. `SignInProvidersScreenTests` fires each change with no Fields editor open, and three of its
   four fail with the old guard put back. The rest are repaired as described: an administrator names a person by
   the provider's name, whole, which is now required; turning a provider off says it stops everyone; making accounts
   says anyone the provider signs in gets in; the form follows the provider's own order — register the redirect URI,
   then copy the issuer, client ID and secret — with each provider's issuer shape; the list scrolls in its card at
   390 px; a provider is the first way in on both sign-in pages, and an error page leads back to where the sign-in
   started.
