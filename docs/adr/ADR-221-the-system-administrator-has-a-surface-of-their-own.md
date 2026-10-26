# ADR-221 — The system administrator has a surface of their own

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the split, which follows the API's own (`TenantRouting.SystemOnly`) · `MEDIUM` for where each screen lands, which the screens will test |
| **Decided** | 2026-10-26, by owner decision — *"Bence bir admin sayfası yapıp bu tarz özellikleri oraya taşıyalım. Hatta bir sysadmin sayfası yapalım."* Asked, the owner chose **a separate surface, `/system/`**, over a tab inside Server, and **Studio › Organization** as the organisation administrator's page; then *"kuruluş yöneticisinin system de işi yok. system sadece system admin için olacak."* Who sees `/system/` on a server of one organisation is INFERRED (§5.2). |
| **Supersedes** | [ADR-034](ADR-034-server-and-studio.md)'s *two* surfaces — now three; [ADR-187](ADR-187-primary-navigation-is-a-top-bar.md)'s Server tabs, for the screens that move |
| **Superseded by** | — |

---

## 1. Context

[ADR-204](ADR-204-one-deployment-holds-several-organisations.md) §5d split administration in two: the system
administrator runs the machine, an organisation's administrator runs the organisation. The API carries the split —
`TenantRouting.SystemOnly` answers the machine's routes only to the system administrator while there are several
organisations — and owner decisions of 2026-10-22 gave an organisation's administrator its own Server: its services,
publishing, data sources, jobs and logs.

The console did not follow. Server shows the machine's screens — Organizations, Status, General — beside the
organisation's, and hides them from an organisation's administrator one by one (`SYSTEM_SCREENS`,
`systemOnlyHidden()`). The owner asked, looking at Server › Organizations, whether it reached only the system
administrator, and then for a page of the system administrator's own.

## 2. Alternatives considered

### Alternative A — A third surface, `/system/` (chosen by the owner)

The page every surface already is (one `index.html`, its own `<base href>`), at a third address, holding only the
machine's screens. Server becomes the organisation's engine with nothing hidden in it.

### Alternative B — A *System* tab in Server, shown to the system administrator

Less work; Server keeps screens an organisation's administrator is not shown, which is what the owner wanted gone.

## 3. Counterarguments to the preferred option

- A system administrator who is also the default organisation's administrator now moves between three surfaces.
- Addresses move: `/server/organizations`, `/server/operations` and `/server/settings` must keep working.

## 4. Evidence

The screens that move are the ones the console already hides (`SYSTEM_SCREENS`) and the API already refuses
(`TenantRouting.SystemOnly`); this decision moves no authority, only where a screen is drawn.

## 5. Decision

1. **`/system/`** is served as `/studio/` and `/server/` are — the same page, its own `<base href>`, the same security
   headers, cross-origin closure, compression and request classification (each place that names the surfaces names
   three).
2. **Who sees it**: the system administrator (`whoami.systemAdministrator`). **INFERRED:** on a server of one
   organisation, where nothing is the system's alone, any holder of `admin:manageServer` — the reader who sees those
   screens in Server today. An organisation's administrator never sees it, and the API refuses its routes as before.
3. **What moves there**: everything the API keeps for the system — Organizations; the server's status, health and
   usage; the server's process log; backups; the machine's settings (general settings, style origins, the geometry
   service, the map ground); the system services (the managed datastore, the geometry service) and the pin limit.
   Exactly which screen holds which is the console's to settle (condition 2).
4. **Server keeps the organisation's engine**: its services, publishing, data sources, jobs and its own logs —
   nothing in it hidden from an organisation's administrator.
5. **Studio › Organization is the organisation administrator's page** — members, roles, sign-in and second factor,
   partnerships, utility services as they are — and gains the organisation's **name and description**, which today
   only the system administrator can change through `/admin/tenants` (condition 3).
6. **Every moved address keeps working**: a moved screen asked for under `/server/` opens under `/system/`.

## Conditions

1. **`/system/` carries every header and closure the other surfaces do**, tested by the security-header and
   one-copy suites over three surfaces. **DISCHARGED 2026-10-26**: both suites name three surfaces, and failed while
   the running build did not serve the third.
2. **The console**: `/system/` with its own top bar for the screens of §5.3; Server without them; the old addresses
   forwarded; the surface switch offering *System* only to its reader — built by the ux-designer agent, with an
   inventory of what moved. **DISCHARGED 2026-10-26**: System holds Status (with the whole server's counts),
   Organizations, General (the map ground with it), Utility services and the process log; Server keeps Services
   (with the empty-service sweep), Data sources and the organisation's logs; Studio › Organization › Settings ›
   General names the organisation; six old addresses forward. Tested by `SystemSurfaceTests` and the suites it
   changed.
3. **An organisation's administrator renames and describes their organisation** from Studio › Organization, refused
   its host names, suspension and deletion, which stay the system administrator's — tested at a second
   organisation's host. **DISCHARGED 2026-10-26** by `AnOrganisationNamesItselfTests`: its portal answers the new
   name and description, a member reads and may not change them, and its addresses are refused it.

## 6. Consequences

**Positive.** Each reader has one place: the system administrator `/system/`, the organisation administrator Studio ›
Organization and Server, nobody a screen with parts hidden from them.

**Negative.** A third surface to keep in step — every list of surfaces in the host is now three, and a fourth place that
forgets one would serve the page without its headers, which is why condition 1 is a test and not a promise.

**State.** None: no catalogue table, nothing held at runtime.

## 7. Assumptions this decision rests on

- The system administrator and an organisation's administrator are different people often enough for the split to be
  worth a surface, as in the owner's deployments (a company and its joint ventures).

## 8. Dependencies

[ADR-034](ADR-034-server-and-studio.md), [ADR-187](ADR-187-primary-navigation-is-a-top-bar.md),
[ADR-204](ADR-204-one-deployment-holds-several-organisations.md) §5d.

## 9. Revisit triggers

- A screen the system administrator needs turns out to be an organisation's too, or the reverse.

## 10. Dissent

None recorded.
