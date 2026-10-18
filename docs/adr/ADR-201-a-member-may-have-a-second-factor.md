# ADR-201 — A member with a password here may have a second factor at sign-in

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-18, by owner decision — *"Güvenlik: MFA + API anahtarı"*, from the ArcGIS veteran's review (V-92: *"MFA hiçbir ADR'de ya da kayıtta yok … güvenlik incelemesinden geçen kurumlar ilk bunu sorar"*) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

Local accounts are first-class (ADR-015 §5), which ADR-015 itself said makes *"password storage, lockout policy and
rotation … ours to get right rather than delegated to an IdP."* A second factor was not among them. An account that
signs in through OIDC, SAML or a directory has its second step at its provider; a local account had none.

## 2. Decision

- **TOTP, RFC 6238** — HMAC-SHA1, thirty-second steps, six digits, the parameters every authenticator app reads from
  an `otpauth://` address. Written here (`Totp`), forty lines over the framework's HMAC.
- **A member turns it on themselves** at `/rest/auth/second-factor`: the server answers a secret and its address;
  nothing is asked at sign-in until a code from the app confirms it, and the confirmation answers **ten recovery
  codes, once**, kept as SHA-256 hashes and each spent by use. **Turning it off takes a code**, so a session left
  open is not enough; an administrator (`admin:manageMembers`) removes it for somebody who lost both phone and codes.
- **Only for an account with a password here.** One from a provider or a directory has its second step there.
- **The secret is sealed by the server's key** (`SecretProtector`), as a data source's password is; a code is spent
  once (the last step used only moves forward, in the statement that checks it), and a step either side of now is
  accepted for clock drift.
- **Asked after the password, at every door.** `LoginService` asks for it after the password is proven, so it tells
  nobody without the password that the account exists; a wrong code counts as a failed attempt, so the limits that
  slow password guessing slow code guessing. `/rest/auth/login` takes `code` and says `details: ["secondFactor"]`
  when it needs one; the sign-in page (`/rest/login`), the OAuth page and the console ask for it.
  **`generateToken` — on `/rest`, `/sharing/rest` and `/admin` — and HTTP Basic cannot carry it** and refuse,
  saying to sign in through the OAuth page, as Pro and the Python API do, or to give a script an API key (ADR-200).

## 3. Alternatives and counterarguments

### Alternative A — a code in generateToken's request

**For.** A script keeps working. **Against.** ArcGIS's `generateToken` has no such parameter, so no client sends
one; a script that must type a code from a phone is not a script. ADR-200's keys are the answer for scripts.

### Alternative B — WebAuthn / passkeys

**For.** Phishing-resistant. **Against.** A browser ceremony a server-rendered OAuth page for ArcGIS Pro's embedded
browser has not been shown to complete; TOTP works wherever a text field does. A later ADR may add it beside this.

**Counterargument to the chosen option: a rollback.** Migration 92 is *expand*: a build before it signs a member with
a second factor in on the password alone. That is accepted rather than closing the rollback window for a table, and
said here so an operator rolling back knows.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| RFC 6238's vector (T = 59 s → …287082) | `SecondFactorTests.The_rfc_test_vector` | RFC 6238 appendix B |
| A code is spent once; a recovery code once; enrolling again while on is refused | `SecondFactorTests` | this repository |
| Asked after the password, named in `details`; generateToken refuses saying why; an administrator removes it | `ASecondFactorIsAskedAtSignInTests` | conformance fixture |

## 5. Consequences

**Positive.** A security review's first question has an answer.

**Negative.** A member with a second factor cannot use `generateToken` with a password, which some old scripts do;
they need a key. The rollback cost above.

**State.** `principal_second_factor` (migration 92), in the catalogue and shared by every node: the sealed secret, the last step spent and the recovery codes' hashes. Nothing is held at runtime.

## 6. Conditions

1. **ArcGIS Pro signs a member with a second factor in** through this server's OAuth page, in its own window.
2. **An organisation may require it** — for administrators at least — as a setting; not built ([D-304](../architecture-debt.md)).
3. **A screen to turn it on** in the redesigned Studio profile — D-304.

## 7. Dependencies

**Depends on:** ADR-015 §3, §5; ADR-076; ADR-200.

## 8. Revisit triggers

A client observed failing at the OAuth page's code field; a request to require it organisation-wide.

## 9. Dissent

None recorded.
