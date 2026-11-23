# ADR-313 — A map's layer has a form: Studio designs its `formInfo` and runs it with its own Arcade

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the language core and the form logic are tested headless against the documentation's own examples, and the server's doors are tested to keep the object whole; no ArcGIS Field Maps device has opened a form Studio wrote (§11, condition 5) |
| **Decided** | 2026-11-22, by owner decision for Enterprise 12.2 band 2, group 1: *"Tasarım + kendi düzenleyicide çalıştır"* — Studio gets a form designer that writes the web map's `formInfo`, which Field Maps reads, and Studio's own editing form applies the same `formInfo`, through a clean-room Arcade interpreter that runs in the browser, written core first from Esri's public documentation as grapy's was ([ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md)). Every point the public pages do not settle is marked `INFERRED` in §5 and listed in §7 |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

ArcGIS Map Viewer and Field Maps Designer author a **form** for a feature layer in a web map: an ordered list of
fields, groups and text, each field with an input type, a label, a hint, and four Arcade expressions that decide
whether it is shown, required, editable and what value it is given. The form is stored in the web map, in the
operational layer's `formInfo`, and ArcGIS Field Maps builds its collection form from it. The Enterprise 12.2
comparison ([enterprise-12-2-gap.md](../research/enterprise-12-2-gap.md)) found it missing: there was no `formInfo`
anywhere in `webmap.js`, and Studio's editing card ([ADR-134](ADR-134-features-are-edited-in-the-browser.md))
offered every editable field as a plain control by type and domain.

Two things depend on each other. A designer that writes expressions it cannot run cannot test them, and an editor
that honours a form has to evaluate Arcade as the reader types. grapy has an Arcade interpreter
([ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md)), but it is Python and runs in a
user tool; Studio's editor is a page in the browser.

Graticula's own forms ([ADR-287](ADR-287-a-form-is-an-xlsform-filled-online-or-off.md)) are XLSForm with XPath
expressions — another language and another product (Survey123's). They are not merged with this one.

## 2. Alternatives considered

### Alternative A — an Arcade interpreter of our own in JavaScript, and the form logic beside it (chosen)

A lexer, a recursive-descent parser and a tree-walking evaluator in `wwwroot/arcade.js`, with the form profile's
variables and the core function library, no network and no `eval` or `Function`. The form logic — which elements are
shown, required and editable, which values are calculated, which hidden values are cleared — in `wwwroot/form-info.js`
as pure functions. Both load in Node, so both are tested headless. The designer and the editor's form in
`wwwroot/webmap-form.js`.

**For.** The designer runs exactly what the editor runs. The behaviour is tested without a browser. grapy's
interpreter is a tested map of the language: the same guide, the same casting tables, the same precedence inference.

**Against.** A second Arcade implementation in this repository, which can drift from grapy's. Mitigated by building
from the same pages and testing the same documented examples in both.

### Alternative B — the ArcGIS Maps SDK for JavaScript's Arcade

**For.** Esri's own interpreter, every function, the same answers as Map Viewer.

**Against.** It is Esri's code, loaded from Esri's CDN or vendored, under Esri's licence; the SDK's terms tie it to
ArcGIS accounts and it is an SDK for a whole map client. Clean room (CLAUDE.md §5) and the Tier 3 rule's spirit:
Studio does not embed a finished GIS client to run one language.

### Alternative C — evaluate on the server with grapy's interpreter

**For.** One interpreter.

**Against.** Every keystroke becomes a request; a form that hides a question as the reader types would wait on the
network, and grapy runs in a fenced user-tool process ([ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md)) that is not on
the serving path. Studio offline (Field Maps-like use) is impossible.

### Alternative D — translate Arcade to JavaScript and run it with `Function`

**For.** Fast and short.

**Against.** The same argument ADR-235 made against `exec`: JavaScript's semantics are wrong for Arcade's casting
(`==` without coercion, conditions that must be Booleans, case-insensitive names), and a page running text it
received as code is a script-injection door. Refused in the brief, and refused here.

## 3. Counterarguments to the preferred option

A form written for Field Maps may use functions Studio's engine does not have — `FeatureSetByName`, `Intersects`,
`GetUser`, `Area`. The form saves and Field Maps runs it; Studio's designer and editor then answer with an error
naming the function. That is honest and still a gap (D-476).

The web map specification does not say what a client does when an expression fails, how `{…}` in a text element is
resolved, what the designer names an expression, or which time zone a date is read in. Each is filled by inference
(§5, §7), and Field Maps may differ.

The `formInfo` object is the web map's, so every web map door must keep it. The server stores web map documents as
raw JSON (jsonb), and Studio's other saves send the document whole — but a door that rebuilds a layer (the group-lifting
of [ADR-079](ADR-079-a-web-map-is-a-saved-document.md)) could drop it. Checked in §5.6.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `formInfo` has `title`, `description`, `formElements[]`, `expressionInfos[]`, `preserveFieldValuesWhenHidden` (default false) and `supportsAttachmentElements` (default false) | Page read 2026-11-22 | developers.arcgis.com/web-map-specification/objects/formInfo/ |
| A field element has `fieldName`, `label` (the alias when absent), `description`, `hint` (only text and number inputs), `domain`, `inputType`, and the four expressions; `requiredExpression` applies only when the element is visible and is ignored for a non-nullable field; `editableExpression` is ignored when the field is not editable; `valueExpression` runs only when `editableExpression` is false and the field allows edits | Page read 2026-11-22 | …/objects/formFieldElement/ |
| A group has `label`, `description`, `formElements[]` (no nested groups), `initialState` `expanded` (default) or `collapsed`, `visibilityExpression` | Page read 2026-11-22 | …/objects/formGroupElement/ |
| A text element has `text`, `textFormat` `plain-text` (default) or `markdown`, `visibilityExpression`; the page's example writes `{expr/maxPSI}` in its text | Page read 2026-11-22 | …/objects/formTextElement/ |
| An attachment element has `attachmentKeyword`, `inputType` (attachment, audio, document, image, signature, video), `editableExpression`, `filenameExpression`, `label`, `description`, `visibilityExpression`, `useOriginalFilename` (default true), `maxAttachmentCount`, `minAttachmentCount` | Page read 2026-11-22 | …/objects/formAttachmentElement/ |
| A relationship element has `relationshipId`, `displayCount`, `displayType` `list`, `orderByFields[]`, `editableExpression`, `visibilityExpression`, `label`, `description` | Page read 2026-11-22 | …/objects/formRelationshipElement/ |
| An expression info has `name`, `title`, `expression`, `returnType` (boolean, date, dateOnly, dictionary, number, string, time); the authoring client may infer the return type by running it on sample features | Page read 2026-11-22 | …/objects/formExpressionInfo/ |
| Input types: `text-box` and `text-area` (`maxLength` from the field's length when absent, `minLength` 0); `combo-box` and `radio-buttons` (`showNoValueOption` default true, `noValueOptionLabel`); `switch` (`onValue`, `offValue`); `date-picker` (`min`, `max` as ISO dates); `datetime-picker` (`includeTime` default false, `min`, `max` in epoch milliseconds); `barcode-scanner` (a single-line text box where scanning is not supported); `multiple-choice` (string fields, `choices`, `choiceDelimiter` default comma) | Pages read 2026-11-22 | …/objects/formTextBoxInput/, formTextAreaInput/, formComboBoxInput/, formRadioButtonsInput/, formSwitchInput/, formDatePickerInput/, formDatetimePickerInput/, formBarcodeScannerInput/, formMultipleChoiceInput/ |
| The form calculation profile: `$feature`, `$originalFeature`, `$editcontext.editType` (INSERT, UPDATE, DELETE, NA), `$editcontext.editTag`, `$layer`, `$datastore`, `$featureSet`, `$map`; returns Number, Text, Date, DateOnly, Time | Page read 2026-11-22 | developers.arcgis.com/arcade/profiles/form-calculation/ |
| The form constraint profile: the same variables, returns Boolean; example `$feature.status == 'Completed' && !IsEmpty($feature.resolution)` | Page read 2026-11-22 | developers.arcgis.com/arcade/profiles/form-constraint/ |
| Calculated expressions update as the form changes; *"If editing is enabled for the form element, the expression will not run"*; required expressions apply only to visible elements, and a field required outside the form must have a value whether shown or hidden; *Preserve values when fields are conditionally hidden* keeps hidden values; the page's example expressions | Field Maps Designer's *Configure the form*, read 2026-11-22 | doc.esri.com/en/arcgis-field-maps/latest/prepare-maps/configure-the-form.html |
| Map Viewer's form: the input types offered by field type; a text element inserts a field or an expression by typing `{`; the same example expressions | *Create forms*, read 2026-11-22 | doc.esri.com/en/arcgis-online/latest/create-maps/create-form-mv.html |
| Neither page says what a client does when an expression fails at run time; Esri's knowledge base shows a failed calculated value drawn as `--` | Searched 2026-11-22 | support.esri.com …/problem-calculated-values-not-displayed-in-arcgis-field-000035095 |
| The language: tokens, grammar, casting and the core functions | grapy's interpreter and its sources (ADR-235 §4), and its tests `src/grapy/tests/test_arcade.py`, used as a map and checked again, not transliterated | ADR-235 |

Not read: the utility network associations element (out of scope, §5.5). developers.arcgis.com answered every page
fetched; the pages were read through a summarising fetcher, so the examples were taken as short quoted strings.

## 5. Decision

1. **The language** — `wwwroot/arcade.js`, global `GraticulaArcade`, and a CommonJS module in Node. Literals in the
   guide's notations, `var`, case-insensitive names, function scope, the operators with JavaScript's precedence
   (ADR-235 §5.8 b, INFERRED there and here), `if`/`else`, the three `for` forms, `while`, `break`, `continue`,
   `function`, `return` and the implicit return, arrays, dictionaries, features, template literals, safe access `?.`,
   and the casting tables. No `eval`, no `Function`, no network: a FeatureSet cannot be fetched.
   - **The profile** a form needs: `$feature` (the values as typed), `$originalFeature` (as read; for a new feature
     a feature with every field null, INFERRED), `$editContext.editType` (`INSERT` for a new feature, `UPDATE` for an
     edit) and `$editContext.editTag` (empty text). `$layer`, `$featureSet`, `$datastore` and `$map` are refused by
     name: each is a FeatureSet that needs the network.
   - **The functions**: Math (all but Hash), Logical, Text, Array, Dictionary, Date, Debugging, and from Feature the
     ones a form uses without a FeatureSet — `Feature`, `HasKey`, `HasValue`, `DefaultValue`, `Domain`,
     `DomainName`, `DomainCode`, `Schema`, `Expects` — and `Geometry($feature)`. Every other documented function is
     catalogued and refused with an error that names it and why; a name that is not documented answers *is not a
     function*. `GraticulaArcade.coverage()` counts them, and a test checks every one: of the **214** documented names,
     **122 are built and 92 refused**. `Number`, `DateOnly` and `Time` with a text pattern are refused for the pattern
     alone (D-479).
   - **Dates** execute in the browser's time zone (INFERRED: Map Viewer's Arcade runs in the client), a named zone
     through the platform's `Intl`, UTC and fixed offsets; Node tests pass `UTC`.
   - **A step budget** of a million statements and expressions per run, after which the expression fails with an
     error that says so: a form evaluates as the reader types, and a `while (true)` must not hang the page. The figure
     is ours; nothing public sets one.
2. **The form logic** — `wwwroot/form-info.js`, global `GraticulaFormInfo`: compile the expressions once; evaluate
   every element against the values; and say for each field whether it is shown, required, editable, and its
   calculated value, with any expression's error.
   - *Shown*: a group hidden hides what it holds. *Required*: a non-nullable field always; otherwise a visible field
     whose `requiredExpression` answers true. *Editable*: false when the field is not editable; otherwise its
     `editableExpression`, true when it has none. *Value*: the `valueExpression` runs only when the element is not
     editable and the field allows edits, as the specification says.
   - Calculated values are evaluated in the order of the form, and the form is evaluated again while a value changed,
     at most four times (INFERRED: the specification gives no order; Field Maps describes the result as updating as
     the form changes).
   - **A hidden field's value is cleared** when it goes from shown to hidden and `preserveFieldValuesWhenHidden` is
     not true, and the cleared value is what is saved.
   - **An expression that fails** is treated as if it were absent — the element is shown, not required beyond its
     field, editable as its field is, and keeps its value — and the error is shown beside the element (INFERRED:
     neither the specification nor Field Maps' documentation says; this makes a broken form still usable and visible
     as broken). A visibility, required or editable expression that answers something other than a Boolean is an
     error of the same kind.
   - A text element's `{name}` is the expression of that name when the form has one, else the field of that name,
     a coded value shown as its name (INFERRED: the specification's example writes `{expr/maxPSI}` and does not define
     it; Map Viewer inserts fields and expressions with `{`). `markdown` is drawn as a safe subset — headings,
     emphasis, lists, paragraphs — with everything else escaped (INFERRED).
3. **The designer** — a *Forms* tool on a feature layer in the map viewer, beside *Pop-ups* (ADR-259's toolbar). It
   starts a form from the layer's fields or edits the one the map holds; adds, orders, groups and removes fields;
   writes text elements and groups; sets each field's label, hint, description, input type (only the types the Map
   Viewer page offers for the field's type) and their settings; makes a field required, read-only or calculated; and
   writes the four expressions, each tested against a sample feature — one read from the layer, or values typed in
   — with the result or the error shown. *Apply to this map* writes `formInfo` on the layer, and saving the map keeps
   it.
   - **Its shape is Map Viewer's form builder** (amended 2026-11-23 after the design review rejected the first one,
     §11 condition 6): the element list, the form as the editing card draws it — the card's own markup, evaluated by
     the card's own function against the sample feature, read-only, an element the sample hides shown faded and
     saying so — and the chosen element's properties, in that order on screen and in the Tab order, each column
     scrolling on its own under a head that keeps *Apply* and the status in sight. Choosing an element in the preview
     chooses it in the list. The list is one Tab stop: arrow keys move between rows, Alt with an arrow moves a row,
     Delete removes it. *Apply* is the primary action only while there is something to apply. Forms widens the right
     panel to at most 1,000 px and gives up the Layers panel while it is open, and gives it back when it closes
     (INFERRED, §7 n), as Map Viewer's builder takes the page; the Layers panel opened beside it keeps 288 px of map.
     The shape follows the panel's own width, not the window's: three columns from 752 px of panel (a window of
     about 1,220 px with the Layers panel closed), two from 540 px — the list over the preview, the properties beside
     them — and below 540 px, and on a phone, one column with the properties opened under the chosen row; on a phone
     the sheet is full height. Below a 1,000 px window the Layers panel and a right-hand tool cannot both stand with
     any map, so opening Layers closes the tool, whichever it is. An expression that reads a field the layer does not have is a warning
     shown after *Apply*, not a refusal: the form still works where the field exists.
   - Fixed choices are written as expressions, since the specification has no Boolean for them: always required is
     `requiredExpression` naming `expr/system/true`, read-only is `editableExpression` naming `expr/system/false`, with
     those expression infos holding `true` and `false` (INFERRED from the `expr/system/true` name in the
     specification's examples). Other expressions are named `expr/<n>`; their `returnType` is set from the property
     (boolean) or from the field's type (number, string, date) (INFERRED).
   - Attachment, relationship and utility network elements a form already holds are listed and kept as they are; the
     designer adds attachment elements (keyword, label, description, minimum and maximum count) when the layer has
     attachments, and does not add relationship elements (D-477).
4. **The editor** — Studio's editing card ([ADR-134](ADR-134-features-are-edited-in-the-browser.md)) draws the form
   from `formInfo` when the layer has one with elements, and as before otherwise. It evaluates the expressions as values
   change, without redrawing (so focus stays where it is), hides and shows elements, marks required ones, makes
   read-only ones read-only, fills calculated values, clears hidden values as §5.2 says, and says in a live region how
   many questions appeared or went. Save refuses a visible required field left empty, naming it. A number is typed
   in a text box with a numeric keyboard and read with a decimal comma (INFERRED, §7 o): a number box had turned
   "90,5" into 905 without a word. The same `applyEdits`
   writes the change, so the server's rules are unchanged.
5. **Out of scope**: the utility network associations element (`formUtilityNetworkAssociationsElement`), which is
   kept in the document untouched and neither drawn nor authored; `multiple-choice` and `dynamic-choice` inputs and
   the attachment inputs are kept and drawn as the nearest plain control (a text box); forms on a map's tables;
   forms stored on a feature layer item rather than a web map.
6. **The server keeps the object whole**. Every web map door stores the document as sent — Studio's `POST` and
   `PUT /content/webmaps`, the portal's `addItem` (Pro's *Share As Web Map*) and `update` (`WebMap.save()`), the item's
   `/data`, and a copy (Studio's *Save as*, which sends the document as a new map). No door rebuilds an operational
   layer: the map viewer's group-lifting copies a layer with its own keys, the console's bulk sharing and item edits
   send the document whole, and offline map areas read the basemap and the extent only. No server code is changed;
   a round trip is tested.

## 6. Consequences

**Positive.** A form authored in Studio is one Field Maps can read, and Studio's own editor follows it. The language
and the form logic are tested without a browser. A function not built says so by name.

**Negative.** Two Arcade interpreters to keep in step (grapy's and Studio's). Forms that query other layers, the
portal or geometry answer an error in Studio while working in Field Maps (D-476). Attachment and relationship elements
are not edited in Studio's form (D-477). The table view of the map viewer edits without the form (D-478).

**Ports created.** None: no dependency is adopted.

**State.** None in the catalogue and no migration: `formInfo` lives in the web map document the store already holds
(ADR-079). At runtime the designer's draft and the editor's evaluation live in the page; nothing is node-local.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | INFERRED, listed for confirmation: (a) a failing expression is treated as absent and its error shown; (b) `{name}` in a text element is an expression first, then a field; (c) markdown is a safe subset; (d) fixed required and read-only are written as `expr/system/true` and `expr/system/false`; (e) designer expression names `expr/<n>` and `returnType` from the property or field; (f) dates execute in the browser's time zone; (g) calculated values in form order, at most four passes; (h) `$originalFeature` of a new feature has every field null; (i) JavaScript's operator precedence (ADR-235 §5.8 b); (j) a calculated value for a whole-number field is rounded, and one too long for a text field is not written and is reported; (k) a switch is offered only for a field with exactly two coded values; (l) a field hidden when the form opens keeps its value, since only a shown-then-hidden field is cleared; (m) the step budget of a million steps; (n) the designer, open, takes the Layers panel's room and widens the right panel to at most 1,000 px, as Map Viewer's form builder takes the page (2026-11-23); (o) ~~in the editing card a single comma in a number is a decimal comma, never a thousands separator, and with both a comma and a point the last is the decimal separator~~ ~~a number is read by the one rule every Studio screen uses — a single comma or point is the decimal separator, nothing groups thousands, and `1,500` is refused as ambiguous; the card read `1,500` as 1.5 and the map viewer refused it until both were aligned (2026-11-23)~~ a number is read by **one function**, `ntParseNumber` in `wwwroot/number-text.js`, which webmap.js, form-info.js and contingent.js all call: a single comma or point is the decimal separator, nothing groups thousands, and one separator before exactly three digits — `1,500` **or `1.500`** — is refused as ambiguous, because a Turkish, German or Spanish reader means fifteen hundred by `1.500` (the forms reviewer's fourth render, 2026-11-23: `1.500` was read as 1.5 without a word). The separator is never read from the browser's locale: the owner writes commas, and one rule on every screen matters more. On a whole-number field the sentence offers only the whole reading (*"1,500: type 1500 if you mean fifteen hundred."*), and the form's card says what the plain card and the save say, from the same rule (2026-11-23) | Unconfirmed |

## 8. Dependencies

**Depends on**: [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (the web map document), [ADR-134](ADR-134-features-are-edited-in-the-browser.md)
(Studio's editing card), [ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md) (the language,
read from the same pages), [ADR-259](ADR-259-the-map-viewer-takes-arcgis-map-viewers-shape.md) (the toolbar).

**Depended on by**: —

## 9. Revisit triggers

- Field Maps on a device draws a Studio-authored form differently, or refuses it (condition 5).
- Esri documents what a client does with a failing expression, or how a text element's `{…}` resolves.
- A user's form needs a FeatureSet or geometry function in Studio's editor (D-476).

## 10. Dissent

None recorded.

## 11. Conditions

1. **The language core is tested headless against the documentation's own examples**, every example expression from
   the form pages among them. **DISCHARGED 2026-11-22**: `tests/studio-js/arcade.test.js` (37 tests, Node 24,
   `node --test tests/studio-js/*.test.js`) checks the guide's and the function reference's worked examples, the web
   map specification's formInfo and formExpressionInfo examples, both form profiles' examples, and the Field Maps and
   Map Viewer form pages' examples — the two printed without a function name fail as syntax errors, and the ones that
   need geometry, the portal or a FeatureSet fail naming the function.
2. **Every documented function is built or refused naming itself**, and a test calls each refused one. **DISCHARGED 2026-11-22**:
   `arcade.test.js` *every documented function is built or refused* finds none of the 214 without a status and calls
   each of the 92 refused, whose error names it and why.
3. **`formInfo` survives every server door intact** — Studio's create, update and read, the portal's `addItem`,
   `update` and `/data` — proven by a conformance test against a running server and a store test against PostGIS.
   **DISCHARGED 2026-11-22**: `AMapLayersFormSurvivesEveryDoorTests` (Conformance, against the local fixture) and
   `WebMapStoreTests.A_layers_form_is_kept_whole_through_a_create_and_an_update` (Postgres) pass; the form carries every
   element kind, the utility network element among them, on a layer and on a table.
4. **The form logic is tested headless**: visibility (and a group's), required (expression and non-nullable),
   editable, calculated values only when not editable, clearing and preserving hidden values, and a failing
   expression. **DISCHARGED 2026-11-22**: `tests/studio-js/form-info.test.js` (16 tests) on the specification's
   tree-inspection example.
5. **ArcGIS Field Maps opens a map whose form Studio wrote**, draws its fields, groups, text and input types, and
   honours its expressions. ~~Open: it needs the Android emulator, which nobody drives in this session.~~ **DISCHARGED 2026-11-22**, on dev.graticula.com (branch b2-merge) with Field Maps 26.2.2 on the Android emulator: a `formInfo` in the designer's shape (`expr/<n>`, `expr/system/true|false`), saved through Studio's `PUT /content/webmaps/{id}`, was drawn by *Collect* with its title, its markdown text element, its group (marked required because it holds a required field), the field's label, hint and `maxLength` (*Maximum 200 characters*); the field whose `visibilityExpression` was `$feature.note == 'show'` stayed hidden until *show* was typed, then appeared read-only (`editableExpression` false) with its `valueExpression` `Count($feature.note) * 10` evaluated to 40, and the submitted feature arrived on the server with `note = show`, `size = 40`. Not seen on the device: attachment and relationship elements, and input types other than a text box.
6. **The designer and the editor's form are reviewed at phone width** for focus kept across a redraw and live
   statuses, by the design review every Studio screen passes. **PARTLY DISCHARGED 2026-11-22**: `WebMapFormTests` (Console,
   two tests, against the local fixture) checks that focus stays on the control across a mode change, a redraw of the
   layer list and the card's evaluation, that the designer's status and the card's change of questions are live
   regions, and that an empty required field takes the focus; a render at 390 px measured the Forms panel at 0 px of
   horizontal overflow, with Forms in the phone's layer switch. The design review (ux-designer) ran on 2026-11-22 and
   **rejected** the designer, with sixteen findings, four of them in the card. All sixteen were repaired on 2026-11-23
   (branch `b2-fix-forms`): the designer took Map Viewer's shape (§5.3), the layer tools say what they did from
   `#layerToolSays` outside the markup a redraw replaces — Pop-ups and Labels with it — and each finding was driven in
   headless Chrome at 390 and 1440 px against the local fixture, with focus checked after every redraw;
   `WebMapFormTests` checks the preview, the status region and the required error clearing as the field is filled.
   The second review ran on 2026-11-23: **accepted with fixes** — the card ships as it is, and the designer owed
   twelve items (N1–N12: the page collapsing when the Layers panel opened beside Forms, the preview off screen in the
   two-column range, 93 Tab presses from the first row to the first property, and nine smaller). All twelve were
   repaired the same day on `b2-fix-forms` and driven in headless Chrome at 1024, 1280, 1366, 1440 and 390 px, a
   map of three layers among them; the Tab count is now 7 with 22 elements (3 on a phone). It stays PARTLY
   DISCHARGED until the designer is reviewed a third time. The third review ran on 2026-11-23: **accepted with
   fixes** — ten of the twelve held; the wide shape was still chosen by the window's width, so with the Layers panel
   open a grid overran a 340–520 px panel; the phone's sample summary had lost its disclosure triangle; and N10's
   `color-scheme: light dark` drew white calendar icons on white in the light theme under a dark system. Those, four
   smaller items, and a decimal comma the card's number boxes read as a thousands separator, were repaired the same
   day and driven at 390, 768, 1000, 1024, 1100, 1280, 1366, 1440 and 1920 px with the Layers panel closed and open.
   ~~It stays PARTLY DISCHARGED until the designer is rendered a fourth time.~~ **DISCHARGED 2026-11-23**: the
   **fourth render** verdicted *accept with fixes, the designer can ship* — eight of nine claims held at every width from
   390 to 1920 with Layers open and closed, both themes, axe, state and target gates clean — and its one Major (`1.500`
   read as 1.5 on a decimal field) and three wording minors were repaired by moving every screen onto one number rule
   (`number-text.js`), which the contingent screens' fifth render confirmed in the form card too, together with the
   designer's sticky column headings.
