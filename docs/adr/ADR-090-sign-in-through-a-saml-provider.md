# ADR-090 — Sign-in through a SAML 2.0 identity provider

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-24, by owner decision. SAML is third in V-36's order — OIDC, then LDAP, then SAML (*"Üçü de, bu sırayla"*). Asked then: signatures are checked by **an established library** rather than by code written here (*"Hazır kütüphane"*); a sign-in is **only ever started here** — a response an identity provider sends unasked is refused (*"Hayır, yalnız bizden başlasın"*); and a provider's metadata is **read from its URL and refreshed, or uploaded** where the server cannot reach it (*"URL, ya da dosya"*). Each answer was the option recommended. |
| **Supersedes** | — (builds on [ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md) and [ADR-089](ADR-089-directories-and-group-mapping.md); repays more of [D-10](../architecture-debt.md)) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

AD FS, and Entra ID for anything registered as an enterprise application, are SAML first. Organisations that
federated an ArcGIS Portal did it with SAML more often than with anything else, and their identity teams have a
procedure for it: give us your entity id and reply URL, take our metadata.

SAML's weak point is not the protocol but XML signature checking, where most published SAML vulnerabilities live:
signature wrapping, comment truncation, canonicalisation. That is why the owner was asked who checks signatures.

## 2. Alternatives considered

### Alternative A — an established library behind this server's own code *(chosen)*

`ITfoxtec.Identity.Saml2` 4.21 (BSD-3-Clause, maintained, released 2026-09-15, tested by its authors against AD FS
and Entra ID), on `System.Security.Cryptography.Xml` 9.0.20 and `Microsoft.IdentityModel.Tokens.Saml`. No library
type crosses into the platform or into any Tier 1 signature; it is used in two files under `Graticula.Host/Saml`.

### Alternative B — written here on `SignedXml`

**Argument against.** No dependency, and every XML signature pitfall ours to find. The owner chose A.

### Alternative C — accept IdP-initiated sign-in, per provider

**Argument for.** ArcGIS Portal supports it, and some organisations launch applications from a portal page.
**Argument against.** A response nobody asked for can be delivered by anybody holding one, which is login CSRF with a
signed credential. The owner chose to refuse it; the page that refuses says to start from the sign-in page.

### Alternative D — metadata by URL only, or file only

URL only fails an air-gapped site; file only leaves a certificate rollover to somebody remembering. The owner chose
both, with the URL refreshed.

## 3. Counterarguments to the preferred option

- **A library checks what it was written to check, and the library alone was measured before this was written**
  (§4): it refuses a changed name, another key, another audience, an expired assertion, no signature, SHA-1, and two
  signature-wrapping shapes. **It accepts, unchecked**, a response to *another* request, an assertion whose bearer
  confirmation names another request or another address, and **the same response twice** — its replay cache is one
  configuration object's memory. And it answers a response with no `InResponseTo` with a `NullReferenceException`.
  Each of those is closed here, not there.
- **Certificates are trusted because the metadata names them**, with no chain: an IdP's signing certificate is
  usually self-signed, and the operator's configuring it is the trust. So metadata over plain HTTP is refused except
  to this machine, as an OpenID issuer is.
- **The state cookie is `SameSite=None`**, the only one here: the response is a cross-site POST, which a `Lax` cookie
  is not sent on.

## 4. Evidence

- **A probe, before any server code**, against the library with responses built and signed by `SignedXml` alone —
  the findings in §3.
- **`SamlSignInConformanceTests`**, against a provider this suite runs that uses nothing but the base library:
  - a signed response to this browser's request signs in, makes the account, and its `Group` attribute gives the role
    through ADR-089's mapping; the next sign-in finds the same account; Check reads the metadata; this server's own
    metadata carries its entity id and reply URL;
  - eight responses sign nobody in, each for its own reason, read in the server log: the name changed after signing,
    another key, another audience, confirmed for another address, answering another request, confirmed for another
    request, unsigned, and one with a document type;
  - a response posted twice with the cookie that came with it is refused the second time, and one sent unasked is `400`;
  - metadata over plain HTTP to another machine is refused.
  - **Control:** with this server's request check and single-use record taken out, exactly the four tests about them
    fail.
- **`SignInProvidersScreenTests`**: the new-provider form gives the entity id and reply URL and marks a missing
  metadata without sending anything; a SAML provider is listed as SAML, says whose metadata it holds, and is offered on
  the sign-in panel at its own start.

## 5. Decision

5.1 **A SAML provider is a sign-in provider of kind `saml`** (migration 58): its issuer is its metadata URL, or its
entity id when the metadata was uploaded; its client id is this server's entity id there, by default
`<origin>/rest/auth/saml`; its username claim is the attribute naming the account (`NameID` for the subject), and its
groups claim the attribute listing groups (Entra ID's by default). The metadata document itself is kept in
`identity_provider.saml`, so every node reads the same certificates without asking the provider.

5.2 **Metadata is read from its URL** when the provider is saved or checked, and again at a sign-in that finds the
copy older than a day; a read that fails keeps the copy and says so in the log. **Uploaded metadata is never read
again.** A document type is refused, and so is metadata with no signing certificate or no HTTP-Redirect sign-on.

5.3 **Sign-in** is `GET /rest/auth/saml/{id}/start`, an `AuthnRequest` by HTTP redirect, and `POST
/rest/auth/saml/acs`. The request id and relay state ride in a cookie sealed with the server's key. A response is
accepted when the library accepts it **and** it answers that request, **and** a bearer confirmation names that
request and this server's reply URL and has not expired, **and** its assertion id has not been used — recorded in
`saml_assertion_used` until it would have expired, which makes it single-use across nodes.

5.4 **What ends the sign-in is ADR-088's door**, `OidcEndpoints.FinishAsync`, now shared: the same account rules
(made at a first sign-in when the provider allows it; never joined to a local account), ADR-089's group mapping, and
this server's own session.

5.5 **`GET /rest/auth/saml/{id}/metadata`** is this server's metadata for the provider, public as the sign-in page is.

5.6 **The console**: Server › Sign-in adds a SAML provider beside the other two — this server's identifier and reply
URL first, then the provider's metadata URL or file — and says whose metadata is held and when its signing
certificate expires, with a warning inside thirty days.

## 6. Consequences

**Positive.** AD FS and Entra ID enterprise applications sign people in to this server; groups follow the
organisation as a directory's do.

**Negative.** Five new packages, recorded in [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md), all permissive. A third outbound file in the air-gap
check. Encrypted assertions are not read: this server has no key to decrypt them with — condition 3. Single logout is
not done: signing out here ends this server's session only.

**Ports created.** None; SAML is a host concern behind `IIdentityProviderStore`.

**State.** `identity_provider.saml`; `saml_assertion_used` (migration 58, expand).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The provider signs its assertion or its response with a key its metadata names | True of AD FS, Entra ID, Okta and Shibboleth as configured by default |
| — | `NameID` is stable for a person | Entra ID and AD FS send the user principal name unless told otherwise, which changes when somebody is renamed — revisit trigger |

## 8. Dependencies

**Depends on:** [ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md),
[ADR-089](ADR-089-directories-and-group-mapping.md), [ADR-015](ADR-015-authentication.md).

**Depended on by:** —

## 9. Revisit triggers

- An organisation whose provider encrypts assertions and will not stop.
- A person renamed at the provider and arriving here as somebody new.
- An organisation that needs IdP-initiated sign-in and accepts the risk the owner declined.

## 10. Dissent

None recorded.

## 11. Conditions

1. **A real AD FS or Entra ID signs somebody in** — the suite's provider is honest and is ours.
2. **The design review of the screens here**, with its findings repaired. **DISCHARGED 2026-09-24.** Its blocker
   was real and every test had passed over it: saving read the secret box, which a SAML form does not have, so **no
   SAML provider could be added or saved from the console at all** — the tests had checked the form and never pressed
   Add with metadata given. Repaired, and `SignInProvidersScreenTests` now chooses a file as a person does, presses Add
   and reads what was sent, and saves an existing provider; with the old line put back into the page served, both
   fail. Ten more were repaired: the missing-metadata message moved to the box it is about and cleared by a file or a
   URL; Check says what to do about a certificate running out; an uploaded provider's empty URL box no longer shows a
   URL as its placeholder; the page's introduction names SAML; the groups hint mentions Entra ID only on Entra's
   attribute; Copy says Copied where it was pressed; the reply URL readable at 390 px and the provider table
   scrolling in its own box; the held-metadata sentence; and, from the reviewer's opinions, a certificate within
   thirty days of expiring said under the provider's name, and AD FS's groups attribute named outside Advanced.
3. **Encrypted assertions** — whether an organisation asked requires them, and if one does, a key for this server.
