# ADR-091 — Server's sidebar is five rows; the rest are pages under them

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-25, by owner decision. Shown the portal's Organization › Settings beside ours, the owner said ours was harder to read (*"onların organization settings yapısı bizim yapımıza göre daha basit"*), asked for something similar rather than the same (*"aynı yapı olsun demiyorum. ama benzer bir yapı olabilir"*), and approved the five-row proposal (*"hadi yap"*). |
| **Supersedes** | — |
| **Amends** | [ADR-034](ADR-034-server-and-studio.md) — the sidebar §5 lays out; the Server/Studio split itself, [D-115](../architecture-debt.md), is untouched |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Server's sidebar had grown one row per screen, to ten: Services, Publish, Data sources, Members, Roles,
Apps, Sign-in, Settings, Operations, Logs. Each row was added for a good reason, and every one of those
reasons is still written beside it in `SURFACES`. The trouble is the sum, not any single row:

- **Settings was the emptiest row.** It held two things, the query page size and the map ground. Roles,
  Apps and Sign-in, which are settings in every sense a reader would use the word, were rows of their own.
- **Publish is an action, not a place.** It is where *New service* goes ([ADR-057](ADR-057-composing-and-publishing-a-service.md) §5h). Nobody browses to it.
- **Logs and Operations answer one question.** Operations says what the process is doing now, and Logs
  says what it has done. The note beside Logs says they belong together.

The portal the owner compared this with has five top-level administrative tabs. Everything that
configures the organization sits under Settings, in sections: member roles, logins and apps are sections
of Settings and Security, not tabs. Its behaviour is publicly documented; nothing of its implementation
is used here (CLAUDE.md §5).

## 2. Alternatives considered

### Alternative A — five rows, the rest as pages under them *(chosen)*

- **Sidebar:** Services, Data sources, Members, Settings, Operations.
- **Settings:** a strip of General (today's Settings), Roles, Sign-in and Apps.
- **Operations:** a strip of Status (today's Operations) and Logs.
- **Publish:** stays the target of *New service*, lights Services, and draws no strip.
- **Addresses:** every screen keeps its own. `#/roles` still opens Roles.

**Argument for.**
- The sidebar says what the server *has* (services, sources, members) and where to *change* or *watch*
  it (settings, operations).
- New settings get a home without a new row.
- No screen is rewritten, and no link or bookmark breaks.

**Argument against.**
- Roles is one click further away than it was.
- A strip is a second navigation device on the page.

### Alternative B — one Security page holding Sign-in and Apps together, as the portal does

**Argument for.** The portal groups logins and apps as *security*, and that is how an identity team
thinks about them.

**Argument against.**
- Both screens are long, each has its own editor, and each has its own tests keyed to its own view.
- Stacking them makes one scroll with two unrelated forms.
- Sign-in and Apps as siblings in the Settings strip give the same grouping without merging the pages.
- **INFERRED, listed for confirmation:** the owner approved *"Security (Sign-in, Apps)"* as a section.
  The implementation gives it as two strip entries rather than one page, for the reason above.

### Alternative C — leave the ten rows and reorder them into labelled groups

**Argument for.** No row moves, so nothing is hidden behind a click.

**Argument against.**
- Ten rows with headings are still ten rows.
- Settings stays near-empty beside three rows that are settings.

## 3. Counterarguments to the preferred option

- **A page reached through a strip is a page people lose.** D-90 records that nobody presses a page
  action. A strip entry is easier to miss than a sidebar row, and Roles and Apps are rarely visited. The
  answer is the strip's placement, at the top of every sibling page, and the lit row. If a reader cannot
  find Roles, this ADR was wrong.
- **Two navigation devices.** The group page already uses the same `.tabstrip` under its title, so this
  adds no new component. But the console now has a sidebar, a strip, and on layer pages a third.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The portal groups roles, logins and apps under Settings | Organization › Settings tab › Security holds Logins and Apps; Member roles is a Settings category | [Configure security settings, ArcGIS Enterprise 12.1](https://doc.esri.com/en/arcgis-enterprise/latest/administer/configure-security.html) |
| No address moved | The suites open `/server/#/roles`, `#/logs`, `#/apps`, `#/signin` and `#/publish` directly, and those files are unchanged | `tests/Graticula.Console.Tests` |
| The rows light and the strips mark correctly | `ServerSidebarTests`, plus a mocked-API run of the console 2026-09-25: each subpage lights its row, and pressing a strip entry switches the page | this change |

## 5. Decision

Server's sidebar is five rows: Services, Data sources, Members, Settings, Operations.

- Roles, Sign-in and Apps are pages of Settings, reached through a strip at the top of each, whose first
  entry is Settings itself, labelled General.
- Logs is a page of Operations, whose own first entry is labelled Status.
- Publish is a page of Services with no strip.
- `SUBPAGES` in `console.js` is the one table that says this. The router accepts every subpage as a
  screen of its row's surface, and a subpage lights its row.
- Studio's four rows are not changed by this ADR.

## 6. Consequences

**Positive.**
- The sidebar is half as long.
- Settings is a real place.
- A future setting (password policy, mail, allowed origins) is a strip entry or a section, not a row.

**State.** None. `SUBPAGES` is a constant in `console.js`; nothing is stored in the catalogue or held at runtime.

**Negative.**
- Roles, Sign-in, Apps and Logs each cost one more click from elsewhere.
- `SUBPAGES` is a second table beside `SURFACES` and `SCREEN_SURFACE`. D-115's lesson is that every such
  table is a place for a screen to be forgotten; `ServerSidebarTests` pins it.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Roles, Sign-in and Apps are visited rarely enough that one extra click costs less than four rows | Unmeasured; there is no deployment to measure |

## 8. Dependencies

**Depends on:**
- [ADR-034](ADR-034-server-and-studio.md) (surfaces and router).
- [ADR-035](ADR-035-role-privileges-are-editable.md), [ADR-045](ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md), [ADR-076](ADR-076-oauth-for-registered-apps.md) and
  [ADR-088](ADR-088-sign-in-through-an-openid-connect-provider.md), for the screens that moved.

**Depended on by:** —

## 9. Revisit triggers

- The owner, or a design review, reports a moved screen as hard to find.
- A sixth sidebar row is proposed for Server. That proposal must say why it is not a subpage.

## 10. Dissent

None recorded.
