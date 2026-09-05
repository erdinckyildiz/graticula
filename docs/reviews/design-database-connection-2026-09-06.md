# Design review: Server → Data sources, and the "Database connection" dialog

**Mode:** design review. **Screen:** Server surface → Data sources (`#/sources`,
`index.html:548-599`), and the `Database connection` dialog it opens for both
*Add a connection…* and *Edit* (`index.html:1695-1721`, logic in
`src/Graticula.Host/wwwroot/console.js:10261-10713`). **Method:** Playwright (headless
Chrome for Testing 153.0.8010.12) against `https://127.0.0.1:8447`, signed in as
`ci`, with the local fixture password. Every finding below was produced by opening the real
dialog, typing into it, reading network requests/responses and DOM/focus state — not by
reading markup and inferring behaviour. Server-side reasoning (`AdminEndpoints.cs`,
`PostgresDataSourceProbe.cs`) is cited only to explain *why* something verified in the
browser happens.

**Test artefacts:** three temporary sources were registered against the real fixture
database (`localhost:55432/gis`, `gis`/`gis`) to exercise Edit, Save and Remove without
touching the fixture's own rows — `ux_review_temp2`, `toast_check_temp` (a third,
`raw_trap_test`, was only ever run through *Test connection* and never saved). All three
were removed through the console's own Remove control before finishing. The fixture is
back to the state it was found in: two rows, `ci_second_source`
(`127.0.0.1:55432/gis`, 1 layer) and the built-in `datastore` (`localhost:55432/gis`,
8 layers) — nothing about either row's stored connection was changed.

## The task's question: does the screen make the blank password legible, or merely true?

**Mostly yes, for the path most people take, and no for the one path the design itself
sets up.** Opening Edit visibly leaves Password empty, puts focus in it, and prints a
sentence directly under the fields explaining why and what pressing Save requires
(`console.js:10482-10487`). That's the legible case, verified: a sighted, mousing user
looking at the form will see an empty box with a green focus ring around it and a
sentence three lines below it. Where it breaks down is the case that same design makes
likely rather than unlikely — someone who edits the *other* fields (say, a hostname
after a database migration) and, having nothing to change about the password, presses
Save without retyping it. The server correctly refuses this (Finding 2 below shows it is
never silently accepted), but the sentence it refuses with is not the product's own
voice — it's a raw driver exception naming a SASL mechanism. The legibility this dialog
built for the ordinary case doesn't survive the deliberately-empty field's one specific
failure mode.

## Walking it as an administrator

1. **First-run reasoning.** The fixture already had one non-built-in row
   (`ci_second_source`) from prior use, so I could not observe a literal zero-registered
   screen live. I traced it instead: `loadSources()` renders `"None registered."` only
   when `dataSources.length === 0` (`console.js:10281-10282`), but
   `EnsureDatastoreAsync` upserts the built-in `datastore` row into that same table on
   *every server start* (`AdminEndpoints.cs:5908-5911`, `:8428-8438`). So the true
   first-run table is never empty — it always has exactly one row, the one row nobody
   can Edit or Remove. **This is Finding 8**, informational: the empty-state copy is
   correct English for a state the product cannot reach, and a `Register a source` panel
   with one button sits to the right of it regardless (`index.html:590-598`) — so the
   first-run screen a new operator actually sees is a one-row table plus that panel, not
   an empty table.

2. **The `datastore` row's explanation is inline, not hidden.** Verified in the rendered
   DOM: `postgis · this server's own hosted store: its connection comes from the
   Graticula:PlatformStore setting on every start, so it is neither edited here nor
   removed` sits directly under the name, always visible, no hover or click required
   (`console.js:10285-10289`). No Edit or Remove button is *rendered* for that row at
   all — not disabled, absent from the markup (`console.js:10294-10302`, confirmed by
   reading the live `innerHTML`). That is the right way to say "you cannot do this
   here": a disabled button invites the question "why is it disabled," an absent one
   doesn't.

3. **Opening *Add a connection…*** puts focus in `Instance` (host) immediately
   (`console.js:10532-10534`, verified: `document.activeElement.id === "dcHost"`
   right after `showModal()`). Port is pre-filled `5432` before anything is typed —
   a sensible default a first-time registrant doesn't have to know.

4. **The Database field, tried three ways, exactly as asked:**
   - **Nothing filled in:** clicking/focusing it shows *"The host and the user first.
     This list comes from the server, so it cannot be asked for until there is a server
     to ask and somebody to ask as."* — no network request fires (verified: no
     `/admin/datasources/databases` call in the network log until Host and User are
     both non-empty).
   - **Wrong password** (`localhost` / `55432` / `gis` / a wrong password): one POST to
     `/admin/datasources/databases`, `200`, and the dialog prints *"Cannot connect. The
     password was rejected by the server. The host is reachable; the credential is
     wrong."* in a red `testresult alert` box.
   - **Right password** (`gis`/`gis`): the same request returns 4 database names
     (`confdb`, `gis`, `plain`, `postgres`); the `<datalist>` fills, the box
     auto-selects `confdb` (the first alphabetically — see note in Finding 5's
     neighbourhood below), and the message turns green: *"4 databases. The connection
     worked, so what is left is choosing one."*
   - **Keyboard parity, checked because the task called it out specifically:** typing
     the whole form with the keyboard alone and reaching Database by `Tab` (never
     clicking or focusing with the mouse) fires the same request and produces the same
     4-database result. The code deliberately listens for `mousedown`, `focus` and
     `click` together for exactly this reason (`console.js:10369-10374`), and it works
     as documented.

5. **Edit** (`ci_second_source`, and separately a temporary row): title becomes
   `"<name> — connection"`; Instance, Port, Database and User all prefill from a
   `GET /admin/datasources/{id}/connection` call; Password stays empty and receives
   focus (`console.js:10537-10548`, verified live in both cases). The explanatory
   sentence is present and reads correctly (quoted above). This part works exactly as
   documented.

6. **Escape closes the dialog from anywhere I tried it** (empty form, mid-probe,
   Advanced open) with no stuck state.

## Findings

### 1. A value left in the collapsed "Advanced" box silently overrides every visible field — High

**What I did:** opened *Add a connection…*, expanded "Write the connection string
instead," typed a connection string pointing at a host that doesn't exist
(`Host=totally-different-host;Port=9999;Database=nonexistent;Username=nobody;
Password=wrong`), then **collapsed the disclosure again** by clicking its summary a
second time. I then filled in the real, correct, visible fields (Name, `localhost`,
`55432`, `gis`, `gis`, database `gis`) and pressed *Test connection*.

**What happened, verified:** the result was *"Cannot connect. No host by that name.
Check the spelling…"* — the error for the **abandoned, invisible** hostname
(`totally-different-host`), not the real one sitting on screen in the Instance field.
Confirmed via `checkVisibility()` and a screenshot that the connection-string box really
is hidden once collapsed (this is a genuine visual collapse, not a rendering bug — an
earlier `offsetParent`/`getComputedStyle` check on the same element was misleading and
is not being reported as a separate finding); the bug is that its **value** is still
read.

**Why:** `dbconnBody()` (`console.js:10321-10336`) checks only whether `$("dcRaw").value`
is non-empty — not whether the `<details>` holding it is open — and if so, uses it for
the *entire* request, discarding Name aside (`dbconnBody` builds
`{ name, connectionString }` and drops host/port/user/password/database entirely). This
function backs *Test connection*, *Save/Register*, and the Database-field probe alike,
so the trap is not limited to one button.

**What a person loses:** the basic guarantee that what is visibly filled in on screen is
what gets tested and saved. Someone curious about the Advanced option, who types a
partial string and then decides against it, has no reason to think collapsing the box
"undid" anything — collapsing looks exactly like cancelling. If the abandoned string
happens to be syntactically valid and reachable (rather than the obviously-wrong one used
here), **Save would silently succeed against the wrong database with no error at all**,
while every visible field on screen described a different, correct one. This is worse
than a bug that fails loudly — it fails by lying about which fields were used.

**Suggested fix:** clear `#dcRaw` when the `<details>` is collapsed (or, simpler and more
robust, gate `dbconnBody()`'s check on `details.open` as well as the value), and/or grey
out or visibly badge the visible fields while a non-empty raw string exists.

### 2. The one probe failure this dialog doesn't rewrite is the one its own design invites — Medium/High

**What I did:** opened Edit on a real, working source, left Password blank exactly as the
dialog leaves it, changed nothing else, and pressed Save.

**What happened, verified:** `PUT /admin/datasources/{id}` → `400`, and the dialog shows:
*"Refused. Could not reach the server: No password has been provided but the backend
requires one (in SASL/SCRAM-SHA-256)."* This is the server correctly refusing to write a
passwordless connection (see Finding-adjacent note below — nothing is silently
corrupted), but the sentence itself is a raw Npgsql exception message, not a hand-written
one.

**Why:** `UpdateDataSourceAsync` probes the assembled connection before writing
(`AdminEndpoints.cs:5930-5942`) and forwards `result.Message` verbatim on refusal. That
message comes from `PostgresDataSourceProbe.Describe(Exception)`
(`PostgresDataSourceProbe.cs:442-469`), which has hand-written sentences for
`SocketException` (host not found / connection refused), `TimeoutException`, and three
specific Postgres `SqlState`s — wrong password (`28P01`), refused role (`28000`), no such
database (`3D000`). A *missing* password throws a plain `NpgsqlException` before the
server is even asked (Npgsql fails the SASL handshake client-side), which matches none of
those cases and falls through to the generic
`$"Could not reach the server: {exception.Message}"` (`:468`) — the only place in this
switch where the driver's own words reach the screen unedited.

**What a person loses:** exactly the reassurance every neighbouring failure gives. "The
password was rejected by the server. The host is reachable; the credential is wrong." is
a sentence anyone can act on. "No password has been provided but the backend requires one
(in SASL/SCRAM-SHA-256)" requires knowing what SASL and SCRAM-SHA-256 are to be sure it
isn't a different, scarier problem — at the exact moment (an Edit save) this screen's own
design has just told the person the password field would be empty. This is the specific
scenario the task asked to judge, and the honest answer is: the screen says the right
thing *about* the empty box, but not when someone acts on it without reading that
sentence.

**Suggested fix:** one more case in `Describe()`,
e.g. `NpgsqlException { Message: var m } when m.Contains("No password has been provided")
=> "No password was given. The password box is left empty on purpose — type it again;
this server does not read the old one back to fill it in for you."` — reusing language
already in the dialog's own hint (`console.js:10483-10485`) rather than inventing new
copy.

### 3. The dialog's one feedback line has no `role="alert"` or `aria-live` — Medium

**What I did:** inspected `#dcResult` — the div that reports every outcome of pressing
the Database field, *Test connection*, and Save/Register — in all three tones (empty
warning, red refusal, green success), both statically and immediately after
`dbconnSays()` writes into it.

**What happened, verified:** `role` and `aria-live` are `null` on the element itself and
on every ancestor up to the dialog, in every state. `dbconnSays()`
(`console.js:10345-10352`) sets only `className` and `innerHTML`.

**Why this is the same bug this file has already fixed once:** `removeSource`'s own
refusal message, two functions below this one, carries a comment dated to a design review
on 2026-08-19: *"The refusal had no `role="alert"`, so a screen-reader user heard nothing
while a sighted user saw red text"* (`console.js:10648-10654`), and its markup now reads
`role="alert"` (`:10659`). The geodatabase-import refusal panel elsewhere in the same file
has the same attribute (`:11926`). The Database-connection dialog was built the same week
as this review (2026-09-05, per its own header comment) and doesn't carry it.

**What a person loses:** a screen-reader user who tabs into Database, or presses *Test
connection*, hears nothing when the result appears — success or failure — in the one
dialog on this screen that talks the most. They have to manually re-navigate to find out
whether four databases came back or the password was wrong, after every single attempt.

**Suggested fix:** `<div id="dcResult" role="alert"></div>` in the static markup
(`console.js:10496`) — one attribute, matching the pattern already proven correct twice
elsewhere in this file. (`role="alert"` is fine here even for the green "ok" case: it
only fires when `dbconnSays()` writes into it, which only happens in response to a
person's own action.)

### 4. Closing the dialog always sends focus to row 1, never back to what opened it — Medium

**What I did:** with three rows in the table, opened Edit on the **third** row (not the
first), via keyboard (`Tab` to its Edit button, then `Enter`), then pressed `Escape`.
Repeated for *Add a connection…*.

**What happened, verified:** after `Escape`, `document.activeElement` was the **first
row's Probe button** (`ci_second_source`), not the Edit button that had been pressed and
still exists on screen, and not any control on the row that was actually open. Same
result closing from *Add a connection…*: focus lands on row 1, not back on the
`Add a connection…` button. This also holds after a **successful** Save, not only
Escape/Cancel, since all three paths go through the same `dialog.onclose` handler.

**Why:** `dialog.onclose = () => { dbconn = null; focusSources(); }`
(`console.js:10525-10528`), and `focusSources()` unconditionally targets
`document.querySelector("#sources td.acts button")` — the first action button of the
first row (`console.js:10707-10713`). The comment explains the real bug this fixed:
`removeSource` deletes the very row (and button) that had focus, so restoring focus to
"whatever had it" fails after a deletion (`console.js:10516-10524`). That reasoning is
correct for Remove. It was then applied to *every* close path, including Edit and Add,
where the button that opened the dialog is still sitting on screen the whole time and
would have been the more correct place to return focus.

**What a person loses:** orientation. On the paged table this screen already caps at ten
rows per page (per the `D-103` comment at `console.js:10718-10721`), editing row 8,
inspecting it, and pressing Escape can hand a keyboard user back to a different page's row
1 — they have to re-find their place from scratch, every time, on every row but the
first.

**Suggested fix:** track the invoking element (the button that was focused/clicked to
call `openDbConnection`) and refocus it in `onclose` if it's still in the document
(`element.isConnected`), falling back to `focusSources()` only when it isn't — which
covers the Remove case this logic was built for without regressing Edit and Add.

### 5. Registering a new source gets a vaguer success message than editing one — Low

**What I did:** registered a new source and read the toast; then edited that same source
(supplying the password) and read the toast again.

**What happened, verified:** register → *"toast_check_temp now reads that
connection."* Edit-save of the identical connection → *"toast_check_temp now reads
localhost:55432/confdb."*

**Why:** both go through the same line, `toast(answer.name ? \`${answer.name} now reads
${answer.summary || "that connection"}.\` : "Saved.", true)` (`console.js:10625-10627`).
`UpdateDataSourceAsync`'s response includes `summary` (`AdminEndpoints.cs:6003-6013`);
`RegisterDataSourceAsync`'s does not — it returns `{ id, name, probe }`
(`:6092-6099`) — so the fallback string is what a first-time registration always gets.

**What a person loses:** the specific confirmation of what was just typed, exactly the
first time they'd most want it — registering a source for the first time, rather than
correcting one they already trust.

**Suggested fix:** add `summary = Summarise(connection)` to `RegisterDataSourceAsync`'s
JSON body, matching Update; it is already computed for the audit log two lines above
(`:6083-6090`).

### 6. Save/Register bypasses its own `required` fields; the server's refusal then names JSON keys, not the on-screen labels — Low

**What I did:** opened *Add a connection…*, typed only a Name, and clicked *Register*
directly (not pressing Enter in a field).

**What happened, verified:** no native validation bubble appeared on the empty `Instance`
or `User name` boxes (both marked `required` in markup, `console.js:10466`, `:10472`), a
full round trip fired (`POST /admin/datasources` → `400`), and the dialog showed:
*"Refused. A connection is required: either `connectionString`, or `host` with
`username` and the database."*

**Why:** `Save`/`Register` is `<button type="button">` (`console.js:10501`); the
`required` attribute is only enforced by the browser when a `<form>` actually submits
(pressing Enter in a text field does trigger it and does show the bubble, verified
separately) — a plain button click calls `dbconnSave()` directly and never touches
constraint validation. `dbconnSave()` itself checks only `name`
(`console.js:10598-10602`); host/user emptiness is left entirely to the server's
`Assemble()` (`AdminEndpoints.cs:8524-8530`), which — correctly, for an API — names its
JSON fields (`` `connectionString` ``, `` `host` ``, `` `username` ``) rather than the
screen's labels ("Instance," "User name").

**What a person loses:** one avoidable round trip, and a small translation step matching
"`host`" back to the box labelled "Instance" a few lines above the message. Low severity
because the fields are close enough on screen that the mapping is guessable, but it is a
real inconsistency between the two ways of submitting the same form (Enter vs. clicking
the button), and worth closing at the same time as Finding 1's fix touches this same
function.

### 7. The keyboard tab cycle detours through `<body>` at both ends — Low, likely a browser characteristic rather than an authoring defect

**What I did:** tabbed forward from the last control (`Cancel`) and, separately,
shift-tabbed backward from the first field (`Instance`), in this Chromium build.

**What happened, verified:** forward from `Cancel`, the next `Tab` lands on `<body>`,
and only the *following* `Tab` reaches the dialog's close (`✕`) button. Backward from
`Instance`, `Shift+Tab` reaches `Name`, then the close button, then `<body>` — the same
detour, symmetrically, at the other end.

**Why, and why this is not attributed to the markup:** this dialog uses native
`<dialog>` + `showModal()` with no hand-rolled focus-trap code anywhere in this file to
have gotten wrong — the containment is entirely the browser's. `<body>` has no accessible
name or role, so a screen-reader user would most likely hear nothing at that stop rather
than being misdirected into a background control; the loop is not actually broken, just
one extra keypress longer at both seams.

**What a person loses:** very little in practice, but it's exactly what the task asked to
check, so it's recorded rather than waved off. I'd re-verify with a real screen reader
(NVDA/JAWS on real Chrome or Firefox) before spending any engineering time here — headless
Chrome for Testing's focus/accessibility behaviour at dialog boundaries is not guaranteed
to match every shipped browser, and if it doesn't reproduce there this is worth
downgrading to informational.

### 8. The empty-table state ("None registered.") is dead code — Informational

Covered in the walkthrough above. `dataSources.length === 0` cannot occur while
`EnsureDatastoreAsync` runs on every start (`AdminEndpoints.cs:5908-5911`). Not a defect,
but worth knowing before anyone spends effort preserving, testing against, or designing
around a state the product cannot actually be in — the real "first run" is a one-row
table whose only row has no Edit or Remove.

## The three omitted controls — pushed on, and I agree with all three

The task invited pushback on the database-platform picker, the authentication-type
picker, and a "save user/password" checkbox, each deliberately left out of this dialog
relative to the ArcGIS Pro reference it's modelled on (`index.html:1695-1713`). I looked
for a reason each would be wrong to omit and didn't find one:

- **Database platform:** `docs/v1-scope.md` restricts this product to PostGIS, full
  stop. A combo box with exactly one always-selected, never-changeable entry is the
  literal case CLAUDE.md §6 and ADR-034 rule out — a control implying a choice that
  doesn't exist. Adding it back would cost a click for zero information.
- **Authentication type:** same argument, same source (one supported method).
- **"Save user/password":** this is the one worth arguing hardest against, since in the
  reference tool it's a real, meaningful choice (a desktop session can hold a credential
  in memory only). Here it can't be: this server reconnects to a registered source
  unattended, long after whoever registered it has signed out — rendering a map,
  refreshing a cache — so a source with no stored credential could not function *at all*.
  A checkbox whose unchecked state describes a mode this product cannot run in is exactly
  the kind of control this project's own principles forbid drawing. I agree with leaving
  it out.
- **The one control drawn that the reference folds away — Port**, separated from
  Instance rather than packed into it as ArcGIS Pro's `host,port` convention would — is
  also right, and not just in theory: this fixture's own Postgres listens on `55432`, a
  non-default port, and Port populates and edits independently and correctly in both
  Add and Edit, verified.

## What already checked out clean (verified, not assumed)

- **The Database field's three-event listener genuinely provides keyboard parity**, not
  just mouse convenience — confirmed by filling the whole form via keyboard alone and
  reaching Database with `Tab`, never a click.
- **Remove is guarded correctly before it ever asks.** Pressing Remove on
  `ci_second_source` (1 layer attached) never showed the native `confirm()` dialog at
  all — it substitutes a red, always-visible refusal panel first (*"1 layer still read
  from this source. Unpublish it first…"*), confirmed no `dialog` event fired and the row
  was untouched. `removeSource`'s own refusal panel does carry `role="alert"`
  (`console.js:10659`) — this is the sibling of Finding 3's gap, done correctly here.
- **The `<details>` "Advanced" box is genuinely hidden when collapsed** (`checkVisibility()
  === false`, confirmed against a screenshot) and genuinely reachable by `Tab` when open,
  in the correct position in the tab order (between the Database field and *Test
  connection*) — not another instance of the "control exists, renders nowhere" pattern
  this console has shipped three times before. Its *value* persisting across collapse
  (Finding 1) is a different, worse problem than invisibility.
- **Contrast passes WCAG AA with room to spare** on every colour actually used in this
  dialog: alert text on its soft background, 5.87:1; warn, 5.34:1; ok, 4.53:1; muted
  label/hint grey on white, 6.18:1 (all computed from the CSS custom properties in
  `console.css`, not eyeballed).
- **Labels are correctly associated** by wrapping (`<label class="field">Name<input
  …></label>`) throughout the form — no orphaned `<input>` without an accessible name.
- **The port default (`5432`) and the port field's independence from Instance** are both
  right, discussed above.

## Summary of severities

| # | Finding | Severity |
|---|---|---|
| 1 | Abandoned "Advanced" connection string silently overrides visible fields, even after collapsing | High |
| 2 | Blank-password Edit-save is refused correctly, but with a raw driver/SASL message instead of the dialog's own voice | Medium/High |
| 3 | `#dcResult` has no `role="alert"`/`aria-live` — the dialog's only feedback line is silent to screen readers | Medium |
| 4 | Dialog close always refocuses row 1 of the table, never the control that opened it | Medium |
| 5 | Register's success toast is vaguer than Edit's, for the same underlying data | Low |
| 6 | `required` is bypassed by a button click; the resulting server refusal names JSON fields, not on-screen labels | Low |
| 7 | Tab cycle detours through `<body>` at both boundaries (likely browser-level; re-verify with real AT) | Low |
| 8 | The empty-table state this screen renders for is unreachable in practice | Informational |
| — | Omitting platform picker, auth-type picker, and "save credential" checkbox | Agree — correct as built |

## Bottom line

The dialog does the hard part well: probing-as-testing on the Database field is a
genuinely good idea, executed correctly across mouse, keyboard, and the empty/wrong/right
cases it was built for, and the Edit-prefill's missing password is the one honest way to
handle a sealed credential. What undermines it is not the headline behaviour but two
places where the form's own state can silently diverge from what's on screen or what the
person hears: a forgotten value in a box that looks closed but isn't discarded (Finding
1), and the one failure message that reverts to raw driver language at the exact moment
this dialog's own design created (Finding 2). I'd fix those two first — both are in
functions (`dbconnBody`, `Describe`) that are shared by every other path through this
dialog, so the fix pays for itself everywhere at once — then the `role="alert"` gap
(Finding 3, one attribute) and the focus-restore-to-row-1 behaviour (Finding 4, already
has the right pattern sitting nearby in `removeSource` to copy from).
