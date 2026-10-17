# ADR-189 — Studio holds the organization and Server the engine

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — every part is an owner decision of one day, taken against Portal and Server Manager screenshots and checked on dev.graticula.com; §9 says what would show it wrong |
| **Decided** | 2026-10-17, by owner decision |
| **Supersedes** | [ADR-187](ADR-187-primary-navigation-is-a-top-bar.md) §5 in part (Server's fourth tab, Security, and Studio's Anonymous view and Domains tabs); [ADR-087](ADR-087-domains-are-shared.md)'s placement of the Domains screen |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

On 2026-10-17 the owner set Studio beside Portal's Content page and asked why *Domains* was a top tab. Over the
day the question widened into where each screen belongs, in their words:

- *"domains ayrı bir sekme değil. Yeni feature layer oluşturulurken kullanılacak bir şey. O yüzden orada olmalı.
  Create update delete'i de o ekrandan olmalı."*
- *"Ya Anonymous View da saçma"* — and, asked where its probe should go, *"tamamen kaldır"*.
- *"Sanki server motor gibi"* — members, roles and settings were Server's, and Portal keeps them in its Organization.
  Answered *"Taşı o zaman"*, then *"Basemap Gallery falan da Studio'ya gitmeli. Server group bilmez"*.
- Settings would grow, and Portal groups them down the left: *"yap"*.
- *"server service'lerinden map'i kaldıralım. server serverliğini bilsin, studio studioluğunu"*.
- *"new olan her şey kapatılabilir modal olmalı"*.
- *"My Content saçma oluyor. Content diyelim"*, and *"Mine olanların tamamı public. Public sekmesinde gelmiyor ama."*

The thread through all of them is one rule: **Server is the engine — what one installation runs; Studio is the
organization — who it is, what it owns and what its maps offer.** A screen sat in Server because it was written
there, not because it was about the engine.

## 2. Alternatives considered

### Alternative A — keep members and settings in Server, as Server Manager has them

**Argument for.** Server Manager has its own Security page and an administrator of a stand-alone ArcGIS Server finds
users there. Nothing moves, no address changes.

**Argument against.** A federated Server hands its users to Portal; the stand-alone page is the case Graticula does
not resemble, since Graticula *is* its own portal. And the members, roles and sign-in providers are rows in the shared
catalogue, the same for every machine that will ever serve it; a screen that implies they belong to one machine
says the wrong thing the day there are two.

### Alternative B — move them, and leave Settings one long page

**Argument for.** Three cards fit on one page.

**Argument against.** It grows card by card; Portal's Settings is a column of short pages for that reason.

### Alternative C — keep Anonymous view, folded into the Public scope

**Argument for.** It is the only check in the product of what a stranger actually receives.

**Argument against.** The owner chose removal. What it checked is held by `AnonymousAccessConformanceTests`, which run
on every push; a screen an operator must remember to press is a weaker check than one that runs unasked.

## 3. Counterarguments to the preferred option

- **An administrator coming from a stand-alone ArcGIS Server looks for users under Server.** The old addresses
  (`/server/#/members`, `#/roles`, `#/signin`, `#/apps`, `#/settings`'s map cards) land on the Studio page now; the
  word *Organization* is Portal's, which is where a federated Server sends them too.
- **Domains are reached only while designing a layer or from a Fields page.** A domain nothing uses can still be
  deleted from either list; there is no page that lists them on its own.
- **Overlapping content scopes count an item twice.** *Mine* 29 and *Public* 18 add to more than *Everything* 29. That
  is what Portal's filters do, and the alternative — Public reading 0 on a server whose items are public — was the
  defect the owner reported.

## 4. Evidence

Checked in a headless browser on dev.graticula.com, 2026-10-17, against a copy of the showcase's data: every moved
screen opens at its old and new address; the define form makes a domain, offers it to a Text field and not to an
Integer one, and the layer is created with the domain bound; each create dialog opens with focus on its first field,
closes with ✕ and Escape, and says a refusal inside itself; Public counts 18 and Organization 25 of 29.

## 5. Decision

**Studio's bar:** Content · Map · Groups · Organization. Organization is shown only to a reader holding
`admin:manageMembers`, `admin:manageRoles`, `admin:manageSecurity` or `admin:manageServer`.

**Organization** has a strip, *Members* and *Settings*, and Settings a column of pages: *Map* (basemap gallery, default
basemap, map ground — from Server's General), *Member roles*, *Security* (sign-in providers and registered apps, one
page), *Utility services* (the geometry service — from Server's General). A new setting is a card on one of these or a
new entry in the column.

**Server's bar:** Services · Site · Logs. Site's General keeps what the engine does: the query page size and the
origins a style may fetch from. Server's service rows have no *Map*.

**Domains** are not a screen. The list — make, change, delete — is under the fields of *New item › Create a feature
layer › Define your own layer*, where each field chooses one, and opens in a dialog from a layer's Fields page.
`POST /admin/hosted/define` takes a shared domain's id per field and binds it as the table is made, judged by the rules
the Fields page is judged by; a refusal leaves nothing made.

**Anonymous view** is removed: screen, probe and the sentences that pointed at it.

**Content**, not *My content*. Its scopes overlap: *Mine* and *From my groups* are how an item reached the reader;
*Organization* is everything shared with the organization or the public, and *Public* everything public, the reader's
own included.

**Every "new" is a dialog** that ✕, Cancel and Escape put away: member, role, app, provider, domain, group — beside
item, folder, view and data source, which were already. A refusal is said inside it and what was typed stays.

## 6. Consequences

- **State.** None new in the catalogue: the screens moved, and `/admin/hosted/define` writes the field overrides
  the Fields page already wrote. Nothing is held at runtime.
- Screen addresses did not change; which surface and tab light for them did. `SCREEN_SURFACE` routes the old ones.
- A test that opens a moved screen under `/server/` still opens it.
- [ADR-087](ADR-087-domains-are-shared.md)'s rule that a domain's values are changed only in its list stands; the list
  moved.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS Enterprise users are the readers both surfaces are measured against | stated by the owner 2026-10-16 (ADR-187 §7) |

## 8. Dependencies

**Depends on:** [ADR-034](ADR-034-server-and-studio.md), [ADR-087](ADR-087-domains-are-shared.md),
[ADR-187](ADR-187-primary-navigation-is-a-top-bar.md).

**Depended on by:** [ADR-190](ADR-190-members-are-searched-categorized-and-have-profiles.md).

## 9. Revisit triggers

- More than one machine serves one catalogue, and Server's pages have to say which machine they describe.
- A reader without an Organization privilege needs one of its pages (a member's own profile is not one: it is
  `#/user/{name}`, open to them).

## 10. Dissent

None recorded.
