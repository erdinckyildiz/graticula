# ADR-316 — A member signs in with Windows (IWA)

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — Kerberos through .NET's Negotiate handler is the platform's, and the account rules are ADR-088's. It is not `HIGH` until a domain controller has issued a ticket this server accepted end to end in the Linux image (condition 2), and because NTLM behind a proxy is known not to work (§3) |
| **Decided** | 2026-11-23, by owner decision through a question dialog, on Enterprise 12.2 band 2 group 2 ([enterprise-12-2-gap.md](../research/enterprise-12-2-gap.md)): *"İkisi de, önce PKI"* — build both, PKI first ([ADR-315](ADR-315-a-member-signs-in-with-a-client-certificate.md)), then IWA with .NET's Negotiate handler, with a keytab in a Linux container; the recommendation said it would be measured against a Samba AD container. Readings the public documentation does not settle are `INFERRED` and listed in §5.9 |
| **Supersedes** | — (builds on [ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md)'s door and [ADR-315](ADR-315-a-member-signs-in-with-a-client-certificate.md)'s migration) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Windows shops sign in to ArcGIS Enterprise without typing anything: the browser, already signed in to the domain,
answers the server's `Negotiate` challenge with a Kerberos ticket. The gap table lists *IWA (Windows authentication)*
as missing with no decision.

How ArcGIS does it, from Esri's public documentation —
[Use Integrated Windows Authentication with your portal](https://enterprise.arcgis.com/en/portal/latest/administer/windows/use-integrated-windows-authentication-with-your-portal.htm)
(read 2026-11-23 through the copy of the 11.5 page Fairfax County publishes, because enterprise.arcgis.com reset the
connection):

- *"When you use IWA, logins are managed through Microsoft Windows Active Directory"*, and *"users do not sign in and
  out of the organization"*: they are signed in with their Windows credentials.
- It is done at the web tier: ArcGIS Web Adaptor (IIS) with Anonymous Authentication off and Windows Authentication on;
  *"You cannot use ArcGIS Web Adaptor (Java Platform) to perform Integrated Windows Authentication."* HTTPS only.
- The identity store's example names a user `mydomain\winaccount`, with a `caseSensitive` flag that is false unless
  the directory is case sensitive.
- Accounts are added beforehand or *automatically*, and by default see what is shared with the organisation.

Not documented there, and so not copied: Kerberos against NTLM, how a Linux web tier would do it, and what ArcGIS Pro
sends.

## 2. Alternatives considered

### Alternative A — .NET's Negotiate handler on one sign-in route *(chosen)*

`Microsoft.AspNetCore.Authentication.Negotiate`, invoked only by `GET /rest/auth/iwa/{id}/start`: it answers
`401 WWW-Authenticate: Negotiate`, takes the browser's ticket, and the name it yields ends in ADR-088's door and this
server's own session. Native SSPI on Windows; GSSAPI with a keytab on Linux.

**Argument for.** The platform's, maintained by Microsoft, MIT. The session that follows is the one every other sign-in
makes, so revocation and the OAuth page work unchanged. Off by default and costs nothing when off.

**Argument against.** One more package. Kerberos needs a service principal and a keytab the domain's administrators
make, which is their work, not ours.

### Alternative B — the web tier does it, as ArcGIS's IIS Web Adaptor does, and forwards the name

**Argument for.** Exactly ArcGIS's shape: IIS or Apache `mod_auth_gssapi` authenticates and passes `REMOTE_USER`.

**Argument against.** A forwarded user name is a password nobody needs: anything that reaches the server with the header
is whoever it says. ADR-315 §3 accepted that risk for a certificate only because the header holds a certificate the
server checks again; a name cannot be checked again. And the baseline deployment is `graticula → PostgreSQL`, with no
IIS in it.

### Alternative C — every request authenticated by Negotiate, no session

**Argument against.** As ADR-315 Alternative C, and worse: Kerberos is a challenge and a round trip, and NTLM is a
connection-bound handshake, per request. The session is the credential after the first one.

## 3. Counterarguments to the preferred option

- **NTLM does not survive a proxy.** NTLM authenticates a TCP connection in three legs; Caddy pools upstream
  connections, so the legs may arrive on different ones. Kerberos is one request and works through a proxy. Behind a
  proxy this is Kerberos only, and the guide says so.
- **HTTP/2 cannot carry Negotiate.** Kestrel answers a Negotiate challenge on HTTP/2 by asking the client to retry over
  HTTP/1.1, which browsers do; a client that cannot is not served.
- **The keytab is a secret on disk**: it holds the service account's keys. It is mounted read-only and named by
  configuration, never stored in the platform store.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS IWA is the web tier's Windows authentication, accounts named by the domain account | Esri's portal IWA page | §1 |
| A ticket from a domain controller signs a member in through the Linux image | Samba AD on the VPS, condition 2 | §11 |
| The image carries `libgssapi_krb5.so.2`, and not NTLM for Linux | measured in `graticula:1.0.529`, condition 3 | §11 |
| On Windows, SSPI completes Negotiate for the machine's user | `AMemberSignsInWithWindowsTests` | condition 1 |

## 5. Decision

5.1 **An IWA provider is a sign-in provider of kind `iwa`** (migration 147, ADR-315's): its name is what the button
says; whether a first sign-in makes an account, its role and its user type are the provider's, as for every other.
Its issuer is stored as `negotiate` and its client id empty. **At most one is on in an organisation**, as for PKI.

5.2 **Turned on by configuration, off by default**: `Graticula:Iwa:Enabled=true` registers the Negotiate handler. On
Linux, `Graticula:Iwa:Keytab` names the keytab (it sets `KRB5_KTNAME` in the process's own environment, with libc's
`setenv`, when that is not set already — §11 condition 2 says why) and
the machine's `krb5.conf` (or `KRB5_CONFIG`) names the realm. The service principal is `HTTP/<the name clients use>`.
Without `Enabled`, an `iwa` provider can be saved but is not a button anywhere and its route answers 503 — no button
without working code behind it.

5.3 **Sign-in is `GET /rest/auth/iwa/{id}/start`.** The route runs the handler itself — the request handler for NTLM's
middle leg, then `AuthenticateAsync` — and, when there is no ticket yet, challenges with `401 Negotiate` and a page that
says what to do if the browser does not answer it. A ticket's name ends in ADR-088's door
(`OidcEndpoints.FinishAsync`): the session cookie, or an app's code from the OAuth page (`?oauth=`), so ArcGIS Pro's
OAuth sign-in through its browser reaches it. It is a button wherever providers are buttons, as ADR-315 §5.5 made them
for PKI; in Studio it reads *Sign in with* and the provider's name, which the operator calls *Windows*.

5.4 **The name.** The handler gives `DOMAIN\user` on Windows and `user@REALM` on Linux. The account is found by that
name without case (Esri's `caseSensitive: false`). **The account made at a first sign-in is named `user`** — the part
after a backslash and before an `@` — `INFERRED`: ArcGIS's own example names the identity store's user
`mydomain\winaccount`, and a name with a backslash reads here as another organisation's member (ADR-204 §5e item 16).
A server that moves between Windows and Linux sees each person under a second name, and the first sign-in after the
move makes a second account — §9.

5.5 **No groups.** Kerberos tickets carry group SIDs in the PAC, which the Linux handler does not read without an LDAP
connection; ADR-089's directory serves an organisation that wants groups. The provider's default role applies.

5.6 **`generateToken` is unchanged.** A script on a domain machine signs in through the OAuth page or with a password
as before. `INFERRED`: what ArcGIS's `generateToken` does on an IWA portal is not documented publicly.

5.7 **The image carries GSSAPI.** .NET's Negotiate on Linux loads `libgssapi_krb5.so.2`; the server image is
`aspnet:9.0-noble` with `curl` and `libldap2`, and whether it already has the library is measured (condition 3), not
assumed.

5.8 **Not done here.** NTLM behind a proxy; per-request Negotiate (§2 C); groups from the ticket (§5.5); constrained
delegation to a database.

5.9 **`INFERRED`, for confirmation:** the account name `user` (§5.4); one IWA provider per organisation (§5.1);
`generateToken` unchanged (§5.6); the session after the ticket rather than per-request Negotiate (§2); the button's
name being the provider's, which the operator calls *Windows* (§5.3).

## 6. Consequences

**Positive.** A domain's people open Studio and are signed in by Windows, as in ArcGIS Enterprise.

**Negative.** A package (`Microsoft.AspNetCore.Authentication.Negotiate`, MIT). A keytab to make and rotate, which is
the domain administrators'. Kerberos only behind a proxy.

**Ports created.** None: the handler is used in one file under `Graticula.Host/Iwa`, and no handler type crosses into
the platform.

**State.** `identity_provider.kind` admits `iwa` (migration 147).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Browsers on a domain machine answer `Negotiate` for the server's name | True when the name is in the intranet zone (Edge, Chrome) or `network.negotiate-auth.trusted-uris` (Firefox); the guide says so |

## 8. Dependencies

**Depends on:** [ADR-015](ADR-015-authentication.md), [ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md),
[ADR-315](ADR-315-a-member-signs-in-with-a-client-certificate.md).

**Depended on by:** —

## 9. Revisit triggers

- An organisation that needs NTLM through a proxy, or groups from the ticket.
- A deployment that moves between Windows and Linux hosts with IWA members.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The route, the button and the refusals are tested** without a domain: off, no button and 503; on, a challenge with
   `Negotiate` and a page; a provider of kind `iwa` saved, listed and offered.
   **DISCHARGED 2026-11-23** — `AMemberSignsInWithWindowsTests` (tests/Graticula.Host.Tests, 7 pass against two
   single-node sites of real server processes): off, no button, the route 503 and Check 503; on, the button on
   `/rest/auth/providers` and `/rest/login`, `401` with `WWW-Authenticate: Negotiate` and the page, Check 200; a second
   Windows provider refused while one is on; the account name from `DOMAIN\user` and `user@REALM`; and **on Windows a
   whole Negotiate handshake** — the test's own Windows user through SSPI (`UseDefaultCredentials`, over the loopback)
   signed in, was made an account named by the account alone, and was the session's member.
2. **A Kerberos ticket from a domain controller signs a member in end to end**, in a Linux container built like the
   server image: Samba AD, a service principal and keytab, a client with `kinit` and `curl --negotiate`.
   **DISCHARGED 2026-11-23** — [tools/iwa-probe/setup.sh](../../tools/iwa-probe/setup.sh) on the VPS (arm64), every
   container new and named `g2-iwa-*` on its own network with no port published, all removed afterwards with their
   volumes: a Samba 4 AD domain controller for `G2IWA.TEST` (Debian bookworm's samba), a service account with the SPN
   `HTTP/graticula.g2iwa.test` and its keytab exported; the platform store from the 1.0.529 datastore image; **the server
   in the shipped image `graticula:1.0.529` with `/app` replaced, read-only, by this branch's build**, given
   `Graticula__Iwa__Enabled=true`, `Graticula__Iwa__Keytab` and a mounted `krb5.conf`; and a Debian client.
   `kinit jane.doe` and `curl --negotiate --http1.1` at `/rest/auth/iwa/{id}/start` answered `302` to `/rest/whoami`,
   which named **`jane.doe`**, an account made at that first sign-in; without a ticket, `401`. **One defect was found
   and repaired by it**: `Graticula:Iwa:Keytab` was set with `Environment.SetEnvironmentVariable`, which on Linux changes
   .NET's copy of the environment and not the process's, so GSSAPI never read the keytab and every ticket was answered
   `401`; it is now set with libc's `setenv`, and the same run with the keytab named only by `Graticula__Iwa__Keytab`
   (no `KRB5_KTNAME` in the container's environment) signed `jane.doe` in.
3. **The server image has what Negotiate needs on Linux**, measured in the image, not assumed.
   **DISCHARGED 2026-11-23** — measured in `ghcr.io/erdinckyildiz/graticula:1.0.529` on the VPS (arm64), in a throwaway
   container with no network (`g2-iwa-libcheck`): `/usr/lib/aarch64-linux-gnu/libgssapi_krb5.so.2` is there,
   `libgssapi-krb5-2` 1.20.1-6ubuntu2.10 with `libkrb5-3` and `libkrb5support0`, installed only because `libcurl4t64`,
   `libpq5`, `libssh-4` and `libk5crypto3` depend on it; there is no `/etc/krb5.conf` (the operator mounts one) and no
   `gss-ntlmssp` (so Kerberos only on Linux). `deploy/server.Dockerfile` now names `libgssapi-krb5-2` itself, so a change
   to those packages cannot take it away silently.
4. **A browser on a domain machine signs in with no prompt**, and ArcGIS Pro through the OAuth page. Needs a Windows
   domain machine; not driven by this work.
5. **The design review of Server › Sign-in's Windows form and Studio's button**, with its findings repaired.
