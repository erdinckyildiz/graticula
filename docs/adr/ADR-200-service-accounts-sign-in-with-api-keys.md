# ADR-200 — Service accounts sign in with API keys

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-18, by owner decision — *"Güvenlik: MFA + API anahtarı"*, from the ArcGIS veteran's review (V-92) |
| **Supersedes** | — (builds [ADR-015](ADR-015-authentication.md) §5's *API keys* row, part of [D-10](../architecture-debt.md)) |
| **Superseded by** | — |

---

## 1. Context

ADR-015 §2 made a *service* principal — *"a machine; never owns items; roles only"* — and §5 decided its credential:
*"API keys — service principals only — long-lived by nature, so scoped narrowly and revocable."* Migration 2 made the
`api_key` table; nothing wrote it. The veteran's review (V-92) found what that leaves: a script or an integration signs
in with a person's password, and stops working when that person changes it, leaves, or turns on a second factor.

## 2. Decision

- **A service account** is a principal of kind `service`, made by an administrator (`admin:manageSecurity`) at
  `/admin/service-accounts` with a user type and roles — never `administrator`, which is refused by name. It has no
  password and appears on no member screen.
- **A key** is `gak_` and 256 random bits, made at `/admin/service-accounts/{name}/keys` with a name and, optionally,
  a lifetime in days. **Its text is answered once**; the store keeps its SHA-256, as a session's. It is listed with when
  it was made, expires, was revoked and was last used (to the minute), and revoked one by one. Disabling the account
  stops all its keys at once.
- **It is sent as an ArcGIS token** — `token=`, `X-Esri-Authorization` or `Authorization: Bearer` — so the Maps SDK's
  `apiKey` and the ArcGIS API for Python's `api_key` use it unchanged. A session is looked up first; a token shaped
  as a key is then looked up among the keys. **It opens the ArcGIS faces and not the native administration API**, as
  a `generateToken` token does (ADR-015 §4, Q-154).
- **A machine never administers and never comes to own anything**: `Authorization.ForAMachine()` takes the
  administrator role and every privilege by which something comes to be owned — creating content, publishing,
  registering a data store, creating a group — out of whatever its roles grant, at the one place every check reads.

## 3. Alternatives and counterarguments

### Alternative A — keys for any member, as ArcGIS Online's developer keys are

**For.** No second kind of account. **Against.** A key held by a person dies with their account and acts with all
their authority; ADR-015 §5 decided against it, and the veteran's complaint was exactly that scripts borrow a person.

### Alternative B — OAuth `client_credentials` for registered apps

**For.** ArcGIS's *app credentials*, and `OAuthEndpoints` refuses that grant by name today. **Against.** A client
secret per app is a second credential store with the same lifecycle as keys; this ADR's keys cover the scripts the
review named, and the grant can follow on the same accounts (condition 2).

**Counterargument to the chosen option.** A key that never expires is a credential nobody remembers making. The
lifetime is optional, as ADR-015 §5 allows; *last used* is shown so an unused key can be found and revoked.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A key signs its account in on the ArcGIS side, not on `/admin`, and stops when revoked; an administrator machine is refused | `AnApiKeyOpensWhatItsAccountMayTests` | conformance fixture |
| A publisher machine edits and creates nothing; the administrator role confers nothing | `AMachineNeverOwnsOrAdministersTests` | this repository |
| The ArcGIS API for Python signs in with a key, searches and queries; `addItem` with it is 403 | `GIS(url, api_key=…)`, API 2.4.3 | local fixture, 2026-10-18 |
| A client secret is exchanged for a token as ArcGIS app credentials are; revoking it or disabling the account ends the token | `AppCredentialsSignAServiceAccountInTests` | conformance fixture, 2026-10-31 |

## 5. Consequences

**Positive.** Scripts and integrations stop borrowing a person, and survive that person's second factor (ADR-201).

**Negative.** A second credential to rotate. The console's screen came on 2026-10-28 ([D-303](../architecture-debt.md)).

**State.** Service accounts are `principal` rows of kind `service`, and keys and client secrets `api_key` rows holding a hash (`kind` `key` or `secret`; a token bought with a secret names it in `session.credential_id`), in the catalogue and shared by every node; nothing is held at runtime, and a revocation is seen by the next request.

## 6. Conditions

1. **The ArcGIS API for Python is driven with a key** — `GIS(url, api_key=…)` — against this server. *(Discharged 2026-10-18: ArcGIS API for Python 2.4.3, `GIS(url, api_key=…)` against the local fixture, signed in, searched content (5 items) and counted a hosted layer (8); `addItem` with the same key answered 403 — the machine creates nothing.)*
2. **`client_credentials` is decided**: built on these accounts, or refused by decision. *Decided 2026-10-31 by owner decision (*"Kur"*): built on these accounts, no second kind of account; the screen goes through the ux review. The condition stays open until it is built.* **DISCHARGED 2026-10-31** — built. `POST /sharing/rest/oauth2/token` with `grant_type=client_credentials`, `client_id` = the account's name and `client_secret` = one of its secrets answers `access_token` and `expires_in` (two hours unless `expiration` asks less, never past the deployment's session lifetime nor the secret's expiry; no refresh token, as ArcGIS issues none). A secret is `gcs_` and 256 random bits, made at `/admin/service-accounts/{name}/secrets` with a name and an optional lifetime, **answered once and kept as its SHA-256 in the `api_key` table with `kind = 'secret'`** (migration 113) — the same store and lifecycle as a key, not a second one; it is never a token itself, and a key is never a client secret. The token is a session scoped `arcgis` (not `/admin`), resolved with `ForAMachine()`; it remembers its secret (`session.credential_id`), so **revoking the secret ends every token it bought** in the same statement, and disabling the account refuses both new tokens and held ones. Verification runs under the password sign-in's throttle keyed by the client id (`LoginService.VerifyClientSecretAsync`); audit events `clientsecret.issue`, `clientsecret.revoke` and `oauth.clientcredentials` (success and failure). Evidence: `AppCredentialsSignAServiceAccountInTests` against the local fixture — a token from a secret reads an organisation-shared layer anonymous cannot, the secret sent as a token reads nothing, a wrong secret is `invalid_client`, a disabled account gets no token and its held token stops, a revoked secret gets no token and the token it bought stops — 1 of 1, and it fails against the build before, which answered `unsupported_grant_type`; `ClientSecretVerificationTests` 5 of 5 (throttle per account and per address, a key refused as a secret). The screen: Organization › Settings › Security lists an open account's client secrets beside its keys, makes one and shows it once with its client ID, and revokes after asking (`ServiceAccountsScreenTests.A_client_secret_is_listed_shown_once_with_its_client_id_and_revoked`); the ux-designer review returned *rework* twice (twelve findings, then five: the two halves run together, errors and outcomes off-screen, the toggle squeezing the name at 390 px, unquoted names in sentences, the lists one letter per line under 720 px) and *pass* on the third, axe-core clean in both themes at 390 and 1280 px.
3. **A screen** — D-303. **DISCHARGED 2026-10-28**: Organization › Settings › Security lists the accounts and their keys, makes both, shows a key once, and turns off, deletes and revokes after asking (`ServiceAccountsScreenTests`).

## 7. Dependencies

**Depends on:** ADR-015 §2, §4, §5; ADR-018 §3a; ADR-035.

## 8. Revisit triggers

A key observed reaching `/admin`; a client observed sending a key in a way this server does not read.

## 9. Dissent

None recorded.
