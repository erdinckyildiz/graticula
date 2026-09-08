# Design review — the quiesce control, 2026-09-09

**Screen:** Data sources, and the dialog behind *Quiesce…*
**Discharges:** [ADR-059](../adr/ADR-059-quiescing-a-data-source.md) condition 4.
**Standing instruction:** every screen goes through the ux-designer — the owner's, and the same
condition [ADR-038](../adr/ADR-038-how-a-geodatabase-becomes-a-service.md) and
[ADR-058](../adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md) carry.

Walked live with Playwright against the `gisname` fixture on 8451, signed in as an
administrator. Screenshots and driver scripts are in the session scratchpad, named `ux-q4-*`.

---

## 1. What the fixture was built to expose, and it did

The fixture has **two registered sources against one PostgreSQL** — `datastore` with eight
layers and `probe` with one. That is [ADR-059](../adr/ADR-059-quiescing-a-data-source.md) §5d's
own shape: a quiesce is keyed by connection string, so taking either out takes both.

**The review's first two findings are both about that pair, and they are the serious ones.**

## 2. Findings acted on

### 2a. The dialog understated what it was about to do, and never named the other source

> *"**datastore** stops answering while it is out of service. **8 layers** read from it and will
> answer 503 until the time is up or you press Resume. This worker closes its connections so a
> DBA can run their schema change; another worker holds its own and must be taken out
> separately."*

**Nine layers were about to stop.** And the sentence that follows — about another *worker* —
reads as reassurance that a sibling source is unaffected, which is the opposite of what happens.
The mirror dialog on `probe` said *1 layer* and never mentioned `datastore`'s eight.

**This is the one moment a reader can still decline**, so a number that is right about the source
and wrong about the act is the worst place to have one.

**Repaired**, and the computation is on the server rather than on the screen. `GET
/admin/datasources` gains **`sharesWith`**: the names of the other sources whose *connection
string* is identical, `Ordinal`, which is the key the register actually uses. The console could
have grouped by the `summary` it already shows — host, port and database — but two sources
differing only in their credential would look shared and would not be. The listing is the one
place that holds every decrypted string, so it is the one place that can answer exactly.

The dialog now reads:

> *"**datastore** stops answering while it is out of service. **9 layers** will answer 503 until
> the time is up or you press Resume. The same database is also registered as **probe**, so it
> goes out too — a quiesce is per database, which is where the lock is. This worker closes its
> connections so a DBA can run their schema change; another worker **process** holds its own and
> must be taken out separately."*

*process* is added to the last sentence because *worker* is undefined anywhere in this UI and the
review was right that it reads as being about the sibling source.

### 2b. The row — the durable statement — dropped the fact the toast carried

The API's own `note` says it in full: *this connection is also registered as probe, and those are
out of service too: a quiesce is per database, and that is where the DBA's lock is.* The toast
carried a shorter version. **The row's `role="alert"` paragraph, which is the one that outlives
the toast's seven seconds, carried none of it** — so `probe`'s row read as though somebody had
taken *probe* out deliberately, and an operator arriving later had nothing to connect it to.

**Repaired** with the same `sharesWith`. Both rows now name the other and say it went out too.

**Worth recording about this one**: the mechanism was right and the content was wrong. The
`role="alert"` live region fires correctly — that is the fault
[two-faults-every-new-screen](../../CLAUDE.md) names, and it did not reproduce. What was missing
was a sentence, which no accessibility check would have found.

### 2c. An empty reason box sent the placeholder as though somebody had typed it

Found in the payload rather than on the screen, which is the only place it was visible:

```text
WHY FIELD BEFORE SUBMIT: {"value":"","placeholder":"a schema change"}
REQUEST PAYLOAD SENT:    {"seconds":60,"why":"a schema change"}
```

**A false audit entry**, and the same failure class the *minutes* field had already been fixed
for in the field beside it.

**Repaired in two places, because the second one was found by fixing the first.** The console
sends `null`. But the refusal an ArcGIS client reads was doing it too — `SourceQuiesce.Says`
substituted *for a schema change* whenever `Why` was null, so the sentence a *caller* sees
carried an invented reason even after the console stopped sending one. The clause is now dropped
rather than replaced: *for no stated reason* would be a reproach aimed at somebody who is not
reading it, and quiescing without typing a reason is an ordinary thing for an operator in a hurry
to do.

### 2d. `2.5` enabled Go, and nothing said why `0` did not

`0` and `61` were refused correctly; `2.5` was not, because the range was checked and the
`step="1"` beside it was not — and the request then asked for **150 seconds**, which is not a
number anybody typed.

And a reader who typed an out-of-range value got a button that silently refused to enable: no
border, no `aria-invalid`, and nothing in the dialog's own live region, which was wired only for
a failed request.

**Repaired.** Whole minutes only; `aria-invalid` on the box; and `#quiesceSays` says *Whole
minutes only.* or *Between 1 and 60 minutes.* An empty box says nothing, because that is the
state the dialog opens in and telling somebody off for not having typed yet is worse than
silence.

## 3. Findings recorded rather than repaired

### 3a. The Services screen is blind to a held source — [D-232](../architecture-debt.md)

With both sources quiesced, all eight services still report **started**, and the health panel
reads *100%*. Nothing on the screen captioned *Manage and monitor your GIS services* says that
every request against them is answering 503.

**The review is right and the repair is not small.** A service's status is a stored value
([ADR-020](../adr/ADR-020-admin-console-and-service-status.md)) and *running and refusing* is
already a distinct state from *stopped* ([ADR-031](../adr/ADR-031-service-capability-configuration.md)
§2a); what does not exist is a path from a source's held state to the listing that draws a
service. That is a real feature rather than a wording fix, and it is now a debt with its trigger
written down.

### 3b. The primary button's label fails WCAG AA over the first half of its own gradient — [D-233](../architecture-debt.md)

Computed by hand, which is what a gradient needs:
`linear-gradient(96deg, rgb(15,179,186) 0%, rgb(47,111,208) 62%, rgb(90,73,196) 100%)` under
white 15px/600 text.

| Position | Colour | Contrast vs white |
|---|---|---|
| 0% | `rgb(15,179,186)` | **2.57:1** — fails even the 3:1 non-text minimum |
| 62% | `rgb(47,111,208)` | 4.88:1 — passes AA |
| 100% | `rgb(90,73,196)` | 6.59:1 |

Contrast does not reach 4.5:1 until roughly 56% across, so **the leading half of every primary
button's label fails AA for normal text**. This is a shared style — Sign in, Add a connection…,
Resume — so it is one change across the console rather than a change to this screen, and
recorded as such. Resume is the control an administrator most needs to read while a source is
down, which is why the review raised it here.

## 4. Pushed back on

### 4a. "The dialog does not trap keyboard focus"

Reported as `Minutes → Why → Cancel → <body> → Close → Minutes`, with `<body>` in the cycle.

**Investigated on 2026-09-08 and dispositioned then** — see
[design-publish-and-fields-2026-09-08.md](design-publish-and-fields-2026-09-08.md). A bare
`<dialog>` with three buttons gives `a → b → c → BODY → a` in Chromium with no author code
involved at all; it is the browser's own sequence, not this dialog's. The review's own evidence
supports that reading: it confirmed the page behind **is** correctly `inert`, that 18 elements
carry `aria-hidden`, that `Sign out` and every sidebar link refuse `.focus()`, and that Escape
and Cancel both restore focus to the opener. What is left is one revolution through an unnamed
element in one engine.

**Not repaired, and the reason is that the repair is worse than the fault**: a hand-written focus
trap over a native `<dialog>` is a well-known source of the real version of this bug — focus
escaping into the page behind — and it would be replacing a browser's behaviour with our own on
every dialog in the console. Recorded here so the third review that finds it can read this
instead of raising it again.

### 4b. "Quiesce… has the same visual weight as Probe and Edit"

Fair as a design opinion and I would take it differently: the dialog is the disclosure, and it is
plain-language, clear, and cannot be dismissed into an outage since nothing is pre-filled. Red is
spoken for by Remove, and giving a second control a warning colour dilutes the one that means
*this destroys data*. Left as it is.

### 4c. The out-of-service row renders in neutral slate

At 6.2:1 it passes comfortably, and the review flags the *colour* rather than the contrast. A
planned, self-ending, operator-started state is not an error, and [ADR-059](../adr/ADR-059-quiescing-a-data-source.md)
§5e is explicit that it must not read like one — *a planned, deliberate, self-ending
unavailability that reads like a network fault would send whoever is on call to the wrong place*.
Neutral is the point.

### 4d. The resume toast does not name the sibling coming back

Accepted as asymmetric and left. The quiesce toast names it because that is the moment a reader
learns something they did not choose; on resume both rows are already correct and visible, and
the toast is the transient half of a fact the durable half now carries (§2b).

### 4e. `<h3 id="quiesceTitle" tabindex="-1">` is vestigial

True — nothing focuses it. Left, because `tabindex="-1"` on a dialog's heading is what makes it
*addressable* by `aria-labelledby` consumers and by anything that later wants to move focus
there; removing it to tidy would be removing an affordance to satisfy a reader of the source
rather than a reader of the screen.

## 5. What the review confirmed was already right

Stated because a review that only finds faults tells you nothing about what to keep.

- **Nothing is pre-filled and it reads as deliberate.** Go is genuinely disabled on open; Enter
  in either text field submits nothing. The earlier *15 + Enter = accidental outage* repair holds.
- **The redraw does not drop focus.** After Quiesce, focus lands on the new *Resume*; after
  Resume, on the new *Quiesce…*. Neither falls to `body` or to the first row — which is
  `focusSourceRow` doing exactly the job it was written for, and it is the third variant of that
  repair in this file.
- **The row's live region fires**, correctly, on both rows.
- **Quiesce and Resume are symmetric in both directions**, including a two-minute window lapsing
  by itself and pulling both rows back together.
- **Escape and Cancel both restore focus to the opener.**
- **The whole flow completes from the keyboard alone.**
- **Quiesce is not confusable with Remove**, which is the only coloured button in the row.
