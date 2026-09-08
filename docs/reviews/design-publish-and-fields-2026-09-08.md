# Design review — the Publish screen and the Fields view

**Run 2026-09-08**, by an independent reviewer against the running fixture, at the
owner's standing instruction that every screen goes through the ux-designer.
Recorded because [ADR-038](../adr/ADR-038-how-a-geodatabase-becomes-a-service.md)
condition 6, [ADR-057](../adr/ADR-057-composing-and-publishing-a-service.md) and
[ADR-058](../adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md)
condition 3 all carry that instruction and none of them is a place to keep
findings.

---

## What the two reviews together are evidence for

**The same two faults appeared on both screens, and the second screen was written
after the first review had already found them.** That is the strongest argument
for the standing instruction this repository has produced, and it is worth stating
before the findings rather than after:

1. **A redraw drops the cursor to `<body>`.** Every gesture rewrites the panel, the
   focused control stops existing, and focus falls to the document. A keyboard
   operator's *second* press does nothing — which is worse than the control never
   having worked, because the first press taught them it does.
2. **A paragraph that answers asynchronously has no live region.** On both screens
   the hint that changes after a request was the *only* feedback on the error
   paths, and silent to a screen reader.

Both were fixed on the Publish screen at midday. Both were rebuilt in the Fields
view, written that afternoon, and found again by the second review. Neither is
subtle; both are invisible to anybody testing with a mouse.

---

## Publish screen — findings and dispositions

| # | Finding | Disposition |
|---|---|---|
| 1 | **The Databases pane was unreachable by keyboard.** Tab went from the toolbar past every database, schema and table to the sidebar: no tab stop anywhere. A keyboard-only operator could not open a database, could not open a schema, and could not get one table into a composition — the entire task the screen exists for | **Fixed.** Rows carry `tabindex` and a role; Enter and Space dispatch the click the mouse already sends, so the two gestures cannot drift. Only a publishable table takes focus. Asserted as the task in `PublishScreenTests` |
| 2 | **Selecting two layers to group them was mouse-only.** `pubPick` branches on shift and control and its only caller was a click handler on a `div` nothing could focus, so `pubPicked.size > 1` was unreachable and *Group N layers* never appeared | **Fixed.** `role="option"`, `aria-selected`, Enter/Space carrying the modifiers, and Arrow keys that move the cursor **without** selecting — walking the list to see what is there must not destroy the selection somebody is walking it to build |
| 3 | **`#pbNameSays` had no live region.** The §5e name check answers 250 ms after typing stops; a screen reader said nothing, so somebody would guess when the answer had arrived before deciding whether to overwrite a published service | **Fixed.** `role="status" aria-live="polite"`. Two paragraphs of exactly this shape in the same file already had it; this was the third and did not |
| 4 | **Renaming drops into the browser's `prompt()`.** The only interaction on the screen that does. Costs live validation — a colliding name is caught after OK rather than while typing — and blocks the page synchronously | **Open.** Real, and a bigger change than the others: it needs a dialog with the name check wired into it, which is the Publish dialog's own control moved into a second place |
| 5 | **The expand triangle meant two different things in one voice.** On a group it opens children; on a layer it opens that layer's symbol and nothing else, so a collapsed layer was indistinguishable from a collapsed group — implying nested content that does not exist | **Fixed in the label, not the glyph.** The triangle is right for both; what was wrong was claiming the same *content*. A layer's now says *Show / Hide the symbol for X* |
| 6 | **The context menu did not say which items act on the selection.** With two layers selected it read *Zoom to layer*, *Symbol…*, *Rename layer* — all acting on the row clicked — above *Group 2 layers*, which acts on the set | **Fixed.** The singular items name the row when there is a selection to confuse them with: *Rename "roads"* beside *Group 2 layers* is unambiguous where *Rename layer* is not |
| 7 | **Tab from the last control in a dialog lands on `<body>` for one step.** Reproducible on two unrelated dialogs; the reviewer hedged it on headless focus containment and asked for a check in a real browser | **Not ours — attributed 2026-09-08.** A bare `<dialog>` containing three buttons and no application code produces the identical walk: `a → b → c → BODY → a`. It is Chromium's modal focus cycle. Recorded here so the next reviewer does not spend the afternoon on it |

**What the reviewer confirmed rather than found**, which is what the check is for:
every first-run control has a real `offsetParent`; the §5e collision flow works
exactly as ADR-057 describes, end to end; §5o's reference search resolves 4236 to
*Hu Tzu Shan 1950* rather than merely accepting it; and the copy is consistent
with the product's voice throughout.

**One methodology note the reviewer raised themselves**, and it is worth keeping:
their first pass at the *does a control render nowhere* check produced a false
alarm, because a reused `storageState` carries the session cookie and not the
write-capable token, so the app correctly showed its read-only state. Reusing a
saved session across contexts is not a valid setup for reviewing this console.

---

## Fields view — findings and dispositions

| # | Finding | Disposition |
|---|---|---|
| 1 | **Every successful Add and Delete showed a red failure toast.** `toast(message, ok = false)` defaults to the alert colour and both success paths omitted the argument, while every other success in the file passes `true`. On a screen whose whole job is an irreversible operation, a successful delete that looks like a failed one is the wrong signal at the worst moment | **Fixed** — two call sites |
| 2 | **Focus fell to `<body>` after every success.** Isolated exactly by cancelling the confirm: no redraw, focus intact | **Fixed.** The cursor returns to the name box, which is where the next act starts either way — the deleted row's button no longer exists to return to |
| 3 | **`#fldSays` had no live region**, and it is the only feedback on every error path: the empty name, the duplicate name and §5c's dependency refusal all land there and none of them toast | **Fixed** |
| 4 | **The Delete button was `ghost small`.** `.small` is not a class this stylesheet has — dead markup — and `danger` is what every other row-level Delete in the product uses | **Fixed** — `tiny danger` |

**Confirmed rather than found:** the controls render (`offsetParent` is not null),
a layer this server did not create shows the explanatory paragraph and **zero**
controls, and the copy — the confirmation naming the column, the dependency
refusal naming the holder and the route — reads as ADR-058 §5c and §5e ask.

**Two preferences the reviewer flagged as preferences**, and they were right to:
the `confirm()` fires before the server has had a chance to refuse, so a refused
delete shows an irreversibility warning for nothing — which matches how `confirm()`
is used everywhere else here; and a typed name the server rewrites (`ux two` →
`ux_two`) loses the readable label, because there is no separate alias field.
The second is worth an owner's answer rather than a fix, since ADR-058 §5b already
excludes rename and retype.
