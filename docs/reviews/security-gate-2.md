# Security gate 2 — five new doors, and what got through one of them

**Run 2026-08-20** by an independent reviewer that did not write the code, per §67.
Scope: WFS 2.0, the ArcGIS portal surface, WMS 1.3.0/1.1.1, ArcGIS MapServer, OGC API
Features, the three token endpoints added with them, and the shared code they rest on
— `PredicateSql`, `SafeXml`, the renderer.

**The previous gate ([security-gate-1](security-gate-1.md), FAIL, repaired in part)
ran 2026-08-15 against a server with three protocol faces.** There are seven now, five
added in two days, and none of them had ever been examined.

**Against the running server.** One 5 MB body and one deeply nested document were the
only oversized probes, each single-shot. Nothing was mutated and the server was never
restarted.

## Result

**FAIL, on one finding, and it is the one that matters most.**

**The sharing model held everywhere** — which is the thing five brand-new doors are
most likely to get wrong, and the reason this gate is not worse than it is. **The XML
hardening that failed the last gate held under the exact attack that broke it.** What
failed is a credential written into this server's own log, on a channel whose
documentation said it was not open yet.

---

## 1. Session tokens were written to the log in full — HIGH

**The `?token=` query credential was accepted on every route and recorded verbatim.**
The reviewer sent a sentinel, found it in the live log, then found a real `root`
session token there, harvested it, and replayed it:

```
GET /rest/services/…/d53_import_539fd2/FeatureServer/0/query?…&token=k6l_HLk9…
→ 200 {"count":50}          ← a private layer, through a token taken from a log file
```

**The documentation said the door was shut.** `Authentication`'s own class remark
read:

> *The ArcGIS `token=` query parameter is not accepted yet. … Accepting the parameter
> without the redaction would be the weakening without the bound, so it waits for
> them.*

The parameter was added on 2026-08-20 with the ArcGIS token endpoints. The remark was
not updated and **the redaction it names as the precondition was not written**.
`SecurityHeaders` meanwhile cited "the log redaction" as an existing mitigation when
setting `Referrer-Policy: no-referrer`.

**This is a condition that was correctly parked and then missed.**
[ADR-015](../adr/ADR-015-authentication.md) §4 lists four mitigations as *all
required*, the first being *"redaction is the code path, and logging the raw query on
a token-bearing route is the bug"*; its condition 2 says the redaction *"becomes due
in the same change that adds `/generateToken`, not before"*. That change shipped
without it.

**Repaired the same day.**

- `QueryRedaction` is a pure function that replaces the value of `token`, `password`,
  `access_token` and five more, case-insensitively, keeping the parameter so an
  operator can still see that a caller authenticated through the URL.
- ASP.NET's own request logging — which writes the full URL before any middleware runs
  — is filtered off, and this server writes its own line instead. **A code path, not a
  setting**, which is what §4.1 asks for: a filter would leave the raw query one
  configuration change away from returning.
- Seventeen unit tests on the function, and **`TokenIsNotLoggedTests` asserts on the
  log itself** — a sentinel is sent and the file is searched — which is the form
  ADR-015 condition 2 demands, because *"§4.1 fails silently otherwise, and silently is
  the only way it fails"*.

**What remains is narrower and stays in [D-120](../architecture-debt.md):** the
credential is out of this server's log, and every proxy and browser history between a
client and here is still outside our reach. The header form is tried first.

## 2. `layerDefs` was accepted and ignored — LOW

`/MapServer/export` and `/identify` take a `layerDefs` parameter and do nothing with
it. Three exports were byte-identical with no `layerDefs`, with
`layerDefs=il='Adana'`, and with `layerDefs=1=1; DROP--`.

**Not injectable — the opposite.** A caller who uses it to restrict what is drawn is
silently shown everything, which contradicts the rule every other surface here
follows: WFS's `FilterReader`, the OGC face and `PortalQuery` all refuse a filter they
cannot evaluate rather than dropping it.

**Not repaired**, and recorded as [D-125](../architecture-debt.md): the fix is to
refuse the parameter, and refusing a parameter is a compatibility decision rather than
a bug fix.

## 3. Three token endpoints, one interchangeable credential — INFO

`/rest/generateToken`, `/admin/generateToken` and `/sharing/rest/generateToken` each
mint a token, and each token works on all three surfaces including the admin one. All
three require the same username and password, so this is not an escalation — but there
is no separation between a portal token and an administrative one, and a token minted
at the public sharing endpoint opens `/admin`. **Recorded for visibility**, because it
is a design choice nobody wrote down as one.

---

## What held, with the method and the guard

**Sharing, on all five new faces — the headline result.** `hosted/d53_import_539fd2`
is private. It is absent from every anonymous listing — the REST directory, WFS and
WMS capabilities, OGC `/collections`, portal `search` (11 items anonymous against 12
for `root`) — and refused on every direct path even when named exactly:

| Face | Anonymous request | Answer |
|---|---|---|
| FeatureServer | `/FeatureServer/0/query` | 404 |
| MapServer | `/MapServer/export?f=image` | 404 |
| VectorTileServer | `/tile/6/20/40.pbf` | 404 |
| WFS | `GetFeature typeNames=graticula:d53…` | refused, not a feature type |
| WMS | `GetMap layers=d53…` | `LayerNotDefined` |
| OGC API Features | `/collections/d53…/items` | 404 |
| Portal | `/content/items/{known id}` | refused |

**And private is indistinguishable from nonexistent** — identical status *and* message,
checked pairwise — so there is no 403-against-404 oracle and no message names a
resource the caller may not know about.

**Injection — held on every path, and it was pushed hard.** Every face's filters
converge on `PredicateSql`, which binds values as parameters and re-resolves every
column name against the layer's real columns before quoting. Tried and refused:
ArcGIS `where` with `1=1; DROP`, subqueries and `UNION`; `orderByFields`, `outFields`,
`outStatistics.onStatisticField`, `groupByFieldsForStatistics`; OGC property names,
values, `sortby` and `properties`; WFS `ValueReference` and `sortBy`; WMS `TIME`. The
portal's `q` never reaches SQL at all. No error, no delay, no leak on any of them.

**XML on WFS — held, and this is the one that had failed.** The external-entity read
of a local file is refused by `DtdProcessing.Prohibit`; billion-laughs by the same;
a 5 MB body is rejected at the 4 MB ceiling *before* parsing. **The exact D-122 vector
— a `gml:MultiSurface` nested deeply enough to overflow the stack — was caught at 32
levels** by the shared depth budget that repair added, and the server stayed up. That
is a guard that was written after an incident and then tested by somebody who did not
write it.

**One request cannot exhaust the server.** Image dimensions are capped at 4096² on
both WMS and MapServer, naming the limit rather than clamping silently; OGC `limit` at
1,000; WFS `count` and ArcGIS `resultRecordCount` at 50,000, streamed — 46,041 rows
came back as 31 MB in half a second at constant memory.

**Credentials, besides finding 1.** No username enumeration: a wrong password and a
nonexistent account give the same sentence. An invalid token fails closed to anonymous
rather than open. The session cookie is `HttpOnly; Secure; SameSite=Strict` and
GET/HEAD only, so a mutation needs the bearer header and CSRF is closed by
construction.

**The renderer.** No face accepts a caller-supplied style — no `SLD_BODY`, no
`dynamicLayers`, and WMS does not advertise SLD. Stored symbology is bounded at
262,144 characters and the canvas at 4096². **The one case the reviewer could not
exercise** is a privileged publisher storing a hostile style that is later rendered for
anonymous viewers, because testing it needs a mutation and the no-mutation rule
forbade it. Named rather than skipped.

**Disclosure.** Forced errors carry no stack traces, no SQL and no connection strings.
Path traversal against the console was refused in every spelling tried. `/.env`,
`/appsettings.json` and source paths are not served.
