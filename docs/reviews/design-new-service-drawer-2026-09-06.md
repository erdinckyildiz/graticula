# Design review: Server → Services → "New service" drawer

**Mode:** design review. **Screen:** the drawer opened by the "+ New service" button on
Server → Services. **Method:** Playwright against `https://127.0.0.1:8447` (the `ci` /
local `ci` conformance fixture), driving the real console at
`src/Graticula.Host/wwwroot/console.js` + `index.html`. Every finding below was produced
by clicking, typing, and reading network responses in that browser — not by reading markup
and inferring behaviour. Test artefacts (`ZZZUXReviewProbe` and its five group layers) were
created and then deleted through the console's own UI; the fixture is back to the state it
was found in (root: 0 services, `hosted`: 8, `Utilities`: 1).

## The owner's question: mantıklı mı?

**Mostly, no.** The drawer is legible sentence-by-sentence — the hints are honest about what
each field does and why — but it fails at the one thing a "New service" action most needs to
survive: telling a person what happened after they press a button. Two concrete, reproduced
defects sit under everything else in this review:

1. **Every refusal from the server is invisible.** Duplicate names, an invalid nesting target
   — anything the server rejects — throws an uncaught JavaScript error instead of reaching the
   screen. The person sees nothing: no toast, no red text, the form just sits there.
2. **Nothing stops a duplicate double-click.** No field clears and no button disables while a
   request is in flight, so two fast clicks silently created two identically-named group layers
   in this review, with no confirmation prompt and no warning that it had happened once.

Combined, these two mean the screen's only feedback loop is "did the list under the second form
change" — and that list is easy to miss (see below). A first-time user who mistypes anything gets
no error and no success; they get silence, indistinguishable from "the button did nothing."

The three-act ordering the drawer's hint recommends (service, then groups, then layers) is
*directionally* reasonable — building the container before its contents is how an administrator
who already knows the target shape would plan it. What breaks it is not the order but the
handoff: the number you need at step three is shown once, briefly, on a screen you've since
left, and is never shown again anywhere reachable from here (detailed under Finding 3). Fix the
handoff and the order stops mattering, because the id would not need to be memorised.

## Walking it as someone who has never seen this product

The root folder ("Site (root)") on this fixture genuinely has zero services (all 8 fixture
services live under `hosted`), so this was a real first-run state, not a simulated one.

1. Land on Services: header, a "0 services" folder tree (`hosted · 8`, `Utilities · 1` visible
   beside it, so the emptiness of *this* folder is legible, not ambiguous with a broken app),
   a resources panel, and one primary button, "+ New service", top right. Reasonable.
2. Click it. A right-hand panel slides in, covering roughly the right half of the screen, title
   "New service", subtitle "a container for layers, and the groups inside it". Focus lands in
   the first field (`Service name`) without me doing anything — correct, and matches a
   documented decision in the code (`console.js:11447-11453`, design review 2026-08-19, D-93).
3. Two stacked sections appear at once: "An empty service" (name/folder/sharing/description →
   Create empty service) and "A group layer inside one" (group inside/group name/nest under →
   Create group layer). Nothing is disabled or greyed to suggest the second section depends on
   the first — because it doesn't (see Finding 4).
4. I create a service. The confirmation text is helpful in content but developer-shaped in
   voice: *"The service has no layers yet. Add group layers with POST
   `/admin/services/ZZZUXReviewProbe/groups`, and layers by publishing with `serviceName` set to
   this service."* A first-time reader who has never seen an HTTP verb is being told to do
   something by naming the API call for it, in a screen that otherwise never mentions the API
   (Finding 6).
5. The "Group inside" field is pre-filled with the new service's name — a genuinely good touch,
   the one place the drawer anticipates the next step for you.
6. I create a top-level group. It succeeds; a small list appears under the form: `0  Alpha
   empty  [Delete]`. This is the only place *this screen* will ever show you a layer id.
7. I create a second group nested under id `0`. It works, and the small list now shows both
   rows — but flat, side by side, with no indent and no line connecting Beta to its parent Alpha.
   The only sign of the relationship is the text "1 child" on Alpha's row.
8. I never had to leave the drawer to do any of this — because I was only creating groups, never
   publishing a real layer. The moment a first-time user's actual goal is "add a layer to
   Group A," they are sent to a different surface entirely (Studio) with a number they must have
   already written down (Finding 3).

## Findings

Each is verified live; file:line references are to `src/Graticula.Host/wwwroot/console.js`
unless stated otherwise.

### 1. Server refusals never reach the screen — Critical

**What I did:** submitted the service form a second time with the same name; submitted the
group form with a nesting target (`999`) that doesn't exist.

**What happened, verified:**
- `POST /admin/featureservices` → `409 {"message":"A service named 'ZZZUXReviewProbe' already
  exists at the root."}` — a good, specific, actionable message from the server.
- The browser's own error event: `PAGEERROR: A service named 'ZZZUXReviewProbe' already exists
  at the root.` — i.e. an **uncaught exception**, not a handled failure.
- `document.getElementById('toast').className` stayed `""` (never activated); `#newResult`
  stayed empty and `offsetParent === null`. Nothing on screen changed.
- Same result for the bad nesting id: `POST .../groups` → `400 {"message":"Layer 999 of
  'ZZZUXReviewProbe' is not a group layer, or does not exist. A group can only be nested inside
  another group — a feature layer cannot contain anything, and no client would know how to draw
  it."}` — again an excellent message, again never shown. `newResultVisible: false`,
  `toastClass: ""`.

**Why:** `api()` (`console.js:59-85`) throws a proper `Error` with the server's own message on
every non-2xx response — that part is correctly built and is exactly what `toast()`
(`console.js:51-57`) wants. But `createService` (`console.js:11640`) and `createGroupLayer`
(`console.js:11668`) call `await api(...)` with no `try/catch`, so the promise rejection has
nowhere to go. Contrast this with the group-delete handler a few hundred lines below
(`console.js:13924-13933`), which does wrap its `api()` call in `try { … } catch (e) {
toast(e.message); }` — so the pattern for doing this correctly already exists elsewhere in the
same file; these two handlers simply don't use it.

**What a person loses:** everything. This is the drawer's entire feedback mechanism for failure,
and it doesn't exist. A person who mistypes a nesting id — which this review shows is likely,
given Finding 3 — gets no error message telling them what a group can and cannot nest under,
even though the server already wrote that sentence for them. They are left assuming either a
bug or their own confusion, with no way to distinguish the two.

**Scope note:** `publishRegistered` (`console.js:11599`) has the identical shape — an
un-caught `await api(...)` — so this is very likely not unique to this drawer. I did not test
other screens (out of scope for this review of one screen), but per this repository's own rule
that "a fix is not finished until *what else carries this?* has an answer," this is worth a
grep across `console.js` for `await api(` calls with no enclosing `try` before it's called fixed.

### 2. No in-flight state, and creation has no confirmation — High

**What I did:** artificially slowed the group-creation request to 1.5s and checked the submit
button mid-flight; then fired two rapid, back-to-back submits of the same group.

**What happened, verified:**
- Mid-request: `{"disabled": false, "text": "Create group layer", "ariaBusy": null}` — the
  button gives no sign a request is running.
- Two rapid clicks on an identical "Beta, nest under 0" produced **two separate group layers**,
  both named Beta (ids 1 and 2 in that run; a third rapid click in a follow-up test produced a
  third). `Alpha`'s row went from "1 child" to "3 children". No name-uniqueness check, no
  confirmation dialog — compare to *deleting* a group, which does confirm
  (`console.js:13926`, `confirm("Delete group layer …")`). Creating structure is easier to
  do by accident than removing it is to do on purpose.
- Neither form clears its own fields after a successful submit: `cName` still read
  `"ZZZUXReviewProbe"` after the service was created; `gName`/`gParent` still read `"Beta"`/`"0"`
  after the group was created. The field showing the same text you just typed is the only signal
  available, and it looks identical whether the click worked or is about to be repeated.

**What a person loses:** an unintended duplicate that is expensive to notice and to undo — you
have to open the drawer, retype the exact service path, and read a flat, un-nested list to even
discover there are three "Beta"s instead of one, then delete the extra ones one at a time.

### 3. "Nest under (layer id)" — no visible way to find that number — High

This was the review's central question, and it's answered concretely rather than by inference.

**Where I looked, and what I found:**
- **This drawer's own list** (`showServiceGroups`, `console.js:9855-9892`) is the *only* place
  in the drawer that ever shows a layer id, and it has three limits, all verified: (a) it only
  lists **group** layers (`filter(l => l.type === "Group Layer")`) — a service's ordinary
  feature layers never appear here at all; (b) it only populates after you type the *exact*
  qualified path of an *already-existing* service into "Group inside" and blur the field —
  nothing prompts you to do this, and the field's own `<datalist>` (which already holds every
  valid service name) is not wired to show it automatically; (c) it is a flat list — Beta
  nested under Alpha renders as two adjacent rows with no indent, connector, or "parent:"
  label; only the sibling text "1 child"/"3 children" hints at a relationship.
- **The Server surface's own service detail page** (Services → click a row → Capabilities /
  Limits) has **no layer list at all** — verified by opening `hosted/ci_EarlyAlert`'s detail and
  reading its full rendered text: two tabs, no ids, no layers.
- **The only place in the whole console that shows every layer's id** is a different surface
  entirely: Studio → My content → a service → **Overview** tab. Verified on
  `hosted/ci_EarlyAlert`: a "LAYERS" list reading `id 0`, `id 1`, `group layer · 1 layer · id 2`,
  `id 3`, each labelled in small print under the layer's name. This is a genuinely good
  reference — and it is on the surface whose job is publishing, not the one whose drawer is
  asking for the number.
- IDs are a **single sequence shared across group and data layers together**, not a per-type
  counter — `ci_EarlyAlert`'s group is id `2`, sandwiched between feature layers `0`/`1` and `3`.
  A person who only ever sees this drawer's group-only list (e.g. `Alpha=0, Beta=1` in an
  otherwise-empty service) could reasonably assume ids are allocated per-group and be wrong the
  moment a real data layer is published alongside — invalidating a number they wrote down
  earlier.

**What a person loses:** to nest a new group under anything other than "top level," they must
leave the Server surface, switch to Studio, find the right service, read a small-print id off an
unrelated tab, switch back, and type a bare integer with no reference to what it meant by the
time they're typing it. If they publish a layer and later come back to add a group, they need to
have written that id down somewhere the console itself never offered to remember for them.

**This is exactly what the planned redesign already fixes**, and I'd say plainly: don't patch
this by building a lookup screen. A Contents tree with drag-and-drop and multi-select grouping
removes the numeric id from the human-facing surface entirely — a person drags a layer into a
group by name, and the server does the id-resolution it already does today. If that redesign is
delayed, the cheapest interim fix is turning "Nest under" from a `<input type=number>` into a
`<select>` populated from the same groups list this drawer already fetches for the "existing
groups" panel (labelled by name, valued by id) — which would remove *this specific* need to
leave the screen without waiting for the larger rebuild.

### 4. One drawer, framed as one task, actually doing three — Medium

**What I did:** with the drawer opened by "+ New service," ignored the top form entirely and
typed an unrelated, already-published, already-populated service (`hosted/ci_EarlyAlert`) into
"Group inside."

**What happened:** it worked exactly as it would for a freshly-created service — the existing
groups populated, and I could have added a group to that old service without ever touching the
"An empty service" form above it.

**Why this matters:** the drawer's title ("New service") and subtitle ("a container for layers,
and the groups inside it") both frame this as being about *something new*. The second form is
not scoped to what you just made in this drawer session — it is a general "add a group layer to
any service" utility that happens to live here. That means there are really three tasks behind
one button: (1) create a new service, (2) add a group to the service you just created — the one
case the pre-fill anticipates — and (3) add a group to an old, unrelated, already-shipped
service, which has no other home in this console at all. Only task (1) matches the button's own
label. An administrator who wants task (3) — "I already have `ci_EarlyAlert`, I just want one
more group in it" — has no reason to guess that the way there is a button that says "New."

**What a person loses:** discoverability. The feature they need exists, verified, and works —
but it is filed under a name that describes a different task.

**Redesign note:** worth confirming directly, since I can't verify it here — does the described
Publish Service screen (Contents tree + registered Databases + one Publish action) still offer
a way to create a structural, empty service with zero layers, or does the new design assume a
service is always created by publishing something into it? If the latter, task (1) — "container
that exists before its layers do," which the current drawer's own hint calls out by name — needs
a home in the new design too, or it quietly disappears. This is a question to confirm with
whoever owns that design, not a finding against it; I have only the prompt's description of it,
not the screen itself, so I'm flagging the gap rather than asserting an answer either way.

### 5. The current folder isn't carried into the drawer — Medium

**What I did:** navigated into the `hosted` folder (`#/services/hosted`), which is where every
real service in this fixture lives, then opened "+ New service" from there.

**What happened, verified:** `cFolder.value === ""`; the field opens blank, defaulting to the
root, with only a grey placeholder reading `hosted` as an *example*. In this fixture that
placeholder text happens to be the literal name of the folder you were just standing in —
coincidence, not a real pre-fill — which makes it easy to misread the empty, grey example text
as "yes, it remembered where I was."

**What a person loses:** a service created from inside a folder view lands at the root unless
you notice the field is empty and retype the folder name yourself. In a deployment where the
example placeholder doesn't happen to match a real folder, this is lower-risk (nobody would
mistake `"hosted"` for their own folder `"survey-2027"`); in this fixture specifically it is
genuinely easy to miss.

### 6. Success copy names the API instead of the action — Low

Verified text, taken directly from the rendered `#newResult` panel after creating a service:
*"The service has no layers yet. Add group layers with `POST
/admin/services/ZZZUXReviewProbe/groups`, and layers by publishing with `serviceName` set to
this service."* This is accurate and, for a developer, useful — but it's the only place in the
drawer where HTTP method names and endpoint paths appear in front-facing copy; every other hint
in the drawer is written in plain sentences about what a service or group *is*. A first-time
user with no API context is told to do a thing by being given the API call for it. Low severity
because the form to do it is one scroll away in the same panel — this is a tone/voice
inconsistency, not a broken path.

### 7. Confirmation sits at the very bottom of a scrolling panel — Low/Medium

Verified: the drawer body genuinely scrolls (`overflow-y: auto`, measured `scrollHeight: 921` vs
`clientHeight: 820` in a 900px-tall window after two groups existed — this will only grow as
more groups are added), and `#newResult` is the last element in the DOM, after both forms and
the existing-groups list. It is not clipped or hidden — `offsetParent !== null`, reachable by
scrolling — but combined with Findings 1 and 2 (no error path, no loading state, no field reset),
the one place that *would* tell you "yes, that worked" is the thing most likely to be off-screen
when you look up from the form you just submitted.

## What already checked out clean (verified, not assumed)

Worth recording so a future pass doesn't re-spend time here:

- **Focus management on open/close is correct and deliberate.** Opening moves focus to
  `#cName`; Escape closes the drawer and returns focus to whatever opened it (or `#app` if that
  element is gone) — `console.js:8934-8967`, `console.js:14402`. This matches the pattern this
  console has been burned by before (D-93) and I found no exception to it here.
- **`aria-hidden`/`inert` are toggled together and correctly**, both open (`false`/`false`) and
  closed (`true`/`true`) — verified by reading the live attributes, not the source.
- **The "existing groups" Delete buttons are real, reachable controls, not the invisible-control
  pattern this console has shipped three times before.** Checked `offsetParent` (not just
  presence) on all of them: all `true`. The disabled one (a group with children) is genuinely
  `disabled`, not merely styled to look inert, and its tooltip explains why.
- **Required-field validation exists** on `Service name`, `Group inside`, and `Group name` (all
  `required`); browser-native validation blocks an empty submit before it reaches the network.
- **Contrast of the "(optional)" / "(layer id)" hint text measures 6.18:1** against its white
  background (`rgb(86,98,117)` on `rgb(255,255,255)`) — passes WCAG AA with room to spare.
- **The lack of a focus trap is a documented, deliberate choice, and it reads correctly on
  screen.** Tabbing through both forms eventually reaches the left navigation behind the drawer
  — by design (`console.js:11447-11452`, "a panel and not a modal"). I don't disagree with this:
  the drawer is a partial-width overlay that leaves the rest of the page visibly in place with no
  scrim, so a keyboard user tabbing into the still-visible navigation is not landing somewhere
  invisible or nonsensical. This is the one place in the review where I checked a design decision
  the owner might expect pushback on, and didn't find grounds for it.

## Summary of severities

| # | Finding | Severity |
|---|---|---|
| 1 | Server refusals (409/400/etc.) never reach the screen — uncaught exceptions | Critical |
| 2 | No in-flight/disabled state; duplicate names created by two fast clicks, no confirmation | High |
| 3 | "Nest under (layer id)" has no discovery path on this surface | High |
| 4 | One button/title covers three tasks; two of them have no other name to be found under | Medium |
| 5 | Current folder context is not carried into the drawer's Folder field | Medium |
| 7 | Confirmation is the last thing in a scrolling panel, easy to miss | Low/Medium |
| 6 | Success copy quotes HTTP method + endpoint instead of describing the action | Low |

## Bottom line

The drawer is not "wrong" the way a broken control is wrong — every control I pressed did what
its code says it does. It's wrong the way a form with no error states is wrong: it was verified
and demoed on the happy path, and the happy path is not where a first-time user spends their
first ten minutes. Given that a full replacement (Contents tree, drag-and-drop, multi-select
grouping, one Publish action) is already designed and would structurally remove Findings 3 and
4, I would not invest in reshaping this drawer. I would fix Finding 1 wherever it appears in
`console.js` regardless of which screen ships next — an uncaught exception in place of user
feedback is a defect independent of which screen surrounds it — and treat Findings 2, 5, 6, 7 as
not worth separate work if the replacement is genuinely near.
