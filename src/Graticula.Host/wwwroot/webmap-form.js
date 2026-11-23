/*
  A map layer's form in Studio — ADR-313, 2026-11-22, by owner decision ("Tasarım + kendi düzenleyicide çalıştır").

  Two halves, one model. <b>The designer</b> is the map viewer's Forms tool, beside Pop-ups: it writes the operational
  layer's `formInfo` (developers.arcgis.com/web-map-specification/objects/formInfo/), the object ArcGIS Field Maps
  builds its form from. <b>The editing card</b> draws that form when a feature is edited, and evaluates its
  expressions as values change. The logic of both is form-info.js and arcade.js, which run headless in tests/studio-js;
  this file draws and listens.

  <b>Every name here starts with wmf</b>, because classic scripts share one namespace with webmap.js. It reads
  webmap.js's helpers (wmEscape, wmRedraw, wmFieldControl, wmReadControl …) and touches it at three places: the tool
  list, the tool's markup, and wmOpenEdit's first line.

  Focus survives a redraw the way the rest of the viewer keeps it: every control carries `data-focus`, and wmRedraw
  puts focus back on the same key (webmap.js, "the recurring defect"). The card's form is never redrawn while it is
  typed in: evaluation changes attributes in place.

  The designer is Map Viewer's form builder in shape (UX review of 2026-11-22, amended 2026-11-23): the element list,
  the card's own form drawn read-only for the sample feature, and the chosen element's properties, side by side on a
  desktop (webmap.js toggles body.rform, and webmap-form.css widens the panel); one column with the properties under
  the chosen row on a phone. What it says is announced from #layerToolSays, beside #layerTool, never redrawn.
*/
"use strict";

const WMF_F = window.GraticulaFormInfo;
const WMF_A = window.GraticulaArcade;

/** A stable id per form element, so a moved element keeps its focus keys. Elements are objects, kept by identity. */
const wmfUids = new WeakMap();
let wmfUidSeq = 0;
function wmfUid(element) {
  if (!wmfUids.has(element)) wmfUids.set(element, `e${++wmfUidSeq}`);
  return wmfUids.get(element);
}

/** The designer's draft per layer id: the form being edited, what is chosen, the sample feature, what was said. */
const wmfDrafts = new Map();

function wmfClone(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

/** Whether a layer's form is one the card draws: an object with at least one element. */
function wmfFormOf(layer) {
  const form = layer && layer.formInfo;
  return form && typeof form === "object" && Array.isArray(form.formElements) && form.formElements.length ? form : null;
}

function wmfDraft(layer) {
  let draft = wmfDrafts.get(layer.id);
  if (!draft) {
    draft = { form: wmfClone(layer.formInfo) || null, applied: JSON.stringify(layer.formInfo || null), chosen: null,
      sample: null, editType: "UPDATE", said: "", saidBad: false, tested: null, problems: [], warnings: [], sampleOpen: false, rove: null };
    wmfDrafts.set(layer.id, draft);
  }
  return draft;
}

function wmfInfo(layer) {
  return (wmRuntime.get(layer) || {}).info || {};
}

/** The fields a form can hold: the layer's own, without its object id, global id, geometry or blobs. */
function wmfFields(layer) {
  const info = wmfInfo(layer);
  const hidden = new Set([info.objectIdField, info.globalIdField].filter(Boolean).map(n => n.toLowerCase()));
  return (info.fields || []).filter(f => f && f.name && !hidden.has(f.name.toLowerCase())
    && !["esriFieldTypeOID", "esriFieldTypeGlobalID", "esriFieldTypeGeometry", "esriFieldTypeBlob", "esriFieldTypeRaster", "esriFieldTypeXML"].includes(f.type));
}

const wmfKindName = { field: "Field", group: "Group", text: "Text", attachment: "Attachments", relationship: "Related records" };

function wmfElementName(element, fields) {
  if (!element) return "";
  if (element.type === "field") {
    const field = fields.find(f => f.name === element.fieldName);
    return element.label || (field && (field.alias || field.name)) || element.fieldName || "Field";
  }
  // A text's first line without markdown's marks: "## Check" is listed as "Check" (finding 14).
  if (element.type === "text") return WMF_F.textTitle(element.text) || "Text";
  return element.label || wmfKindName[element.type] || element.type || "Element";
}

// ---------------------------------------------------------------------------------------------------- designer markup

/**
 * Whether the designer is drawn in Map Viewer's shape — the element list, the form as it reads, and the chosen
 * element's properties side by side — or in one column with the properties under the chosen row. A phone, and a
 * window too narrow to give the panel room for two columns, gets the one column (UX review of ADR-313, findings 1, 2
 * and 8). The panel is widened for Forms by webmap-form.css (body.rform), as Map Viewer's form builder takes the page.
 */
const WMF_WIDE_PX = 540;
function wmfWide() {
  // The panel's own width (third review, item 1): with the Layers panel open beside Forms, a wide window still gives the
  // designer as little as 340 px, and a grid drawn for the window overran it.
  return !WM_PHONE.matches && wm$("side").clientWidth >= WMF_WIDE_PX;
}

/** The key the form's own settings are chosen with, beside the elements' "2" and "2.0". */
const WMF_FORM_KEY = "form";

function wmfChosenElement(draft) {
  return draft.form && draft.chosen && draft.chosen !== WMF_FORM_KEY ? WMF_F.locate(draft.form, draft.chosen).element || null : null;
}

/** The Forms tool's panel for a layer. */
function wmfPanel(layer, run, key) {
  const k = wmEscape(key);
  const draft = wmfDraft(layer);
  const form = draft.form;
  const fields = wmfFields(layer);
  // What was said is shown here and announced from #layerToolSays, outside the markup a redraw replaces (finding 7).
  const status = `<p class="lsnote wmf-said${draft.saidBad ? " bad" : ""}" id="wmfSays-${k}">${wmEscape(draft.said || "")}</p>`;

  if (!form) {
    return `<div class="lstyle wmf" id="wmf-${k}" data-wmf-layer="${k}">
      <p class="lsnote">${layer.formInfo ? "The form is removed in this draft." : "This layer has no form in this map."} Studio's editing card shows every
        editable field, and Field Maps builds its form from the pop-up.</p>
      <p class="hint">A form orders and groups the fields, gives each its input, and decides with Arcade when a field is
        shown, required, editable or calculated. Field Maps reads it from the saved map.</p>
      <div class="row">
        <button type="button" class="tiny primary" data-wmf-act="create" data-focus="wmf-create-${k}">Create a form from the layer's fields</button>
        ${layer.formInfo ? `<button type="button" class="tiny" data-wmf-act="discard" data-focus="wmf-discard-${k}">Keep the saved form</button>` : ""}
      </div>
      ${status}
    </div>`;
  }

  const wide = wmfWide();
  const inForm = new Set(WMF_F.fieldElements(form).map(e => e.element.fieldName));
  const free = fields.filter(f => !inForm.has(f.name));
  const unapplied = draft.applied === "null";
  const changed = JSON.stringify(form) !== draft.applied;
  const chosen = wmfChosenElement(draft);
  const formChosen = wide ? !chosen : draft.chosen === WMF_FORM_KEY;
  const stop = wmfTabStop(form, draft);

  const top = `<div class="wmf-top">
    <p class="lsnote">${unapplied ? "A new form, not applied to the map yet." : changed ? "Changed here; not applied to the map yet." : "The form this map holds for the layer."}</p>
    ${draft.problems.length ? `<div class="wmf-problems" id="wmf-problems-${k}" tabindex="-1"><p class="lsnote bad">Not applied:</p>
      <ul>${draft.problems.map(p => `<li>${wmEscape(p)}</li>`).join("")}</ul></div>` : ""}
    ${draft.warnings.length ? `<div class="wmf-warnings" id="wmf-warnings-${k}"><p class="lsnote">Applied, but check:</p>
      <ul>${draft.warnings.map(p => `<li>${wmEscape(p)}</li>`).join("")}</ul></div>` : ""}
    <div class="row wmf-acts">
      ${wmfApplyButton(k, unapplied || changed)}
      ${unapplied
        ? `<button type="button" class="tiny" data-wmf-act="remove" data-focus="wmf-remove-${k}">Discard the draft</button>`
        : `${changed ? `<button type="button" class="tiny" data-wmf-act="discard" data-focus="wmf-discard-${k}">Discard changes</button>` : ""}
      <button type="button" class="tiny" data-wmf-act="remove" data-focus="wmf-remove-${k}">Remove the form</button>`}
    </div>
    ${status}
  </div>`;

  const formRow = `<li class="wmf-row wmf-formrow${formChosen ? " chosen" : ""}">
      <div class="wmf-rowhead">
        <button type="button" class="wmf-pick" data-wmf-act="choose" data-wmf-key="${WMF_FORM_KEY}" data-focus="wmf-pick-form-${k}"
          data-wmf-stop="${WMF_FORM_KEY}" tabindex="${stop === WMF_FORM_KEY ? 0 : -1}"${stop === WMF_FORM_KEY ? ` aria-describedby="wmf-keys-${k}"` : ""}
          aria-pressed="${formChosen}"><span class="wmf-name">${wmEscape(form.title || layer.title || "Form")}</span><span class="lkind">Form · title and description</span></button>
      </div>
      ${!wide && formChosen ? wmfFormSettingsMarkup(form, layer, k) : ""}
    </li>`;

  const list = `<section class="wmf-listcol" aria-labelledby="wmf-elh-${k}">
    <h3 class="wmf-h" id="wmf-elh-${k}">Elements</h3>
    <p class="sr" id="wmf-keys-${k}">Arrow keys move between rows, Alt with an arrow moves the row, Delete removes it.</p>
    <ol class="wmf-list wmf-settings" aria-label="Form settings">${formRow}</ol>
    ${wmfListMarkup(form, fields, k, draft, wide, stop)}
    <div class="wmf-add" role="group" aria-labelledby="wmf-addh-${k}">
      <span class="lkind" id="wmf-addh-${k}">Add</span>
      <div class="row">
        <label class="sr" for="wmf-addfield-${k}">Field to add</label>
        <select id="wmf-addfield-${k}" data-focus="wmf-addfield-${k}"${free.length ? "" : " disabled"}>
          ${free.length ? free.map(f => `<option value="${wmEscape(f.name)}">${wmEscape(f.alias || f.name)}</option>`).join("") : "<option>Every field is in the form</option>"}
        </select>
        <button type="button" class="tiny" data-wmf-act="addField" data-focus="wmf-addField-${k}"${free.length ? "" : " disabled"}>Add field</button>
      </div>
      <div class="row">
        <button type="button" class="tiny" data-wmf-act="addGroup" data-focus="wmf-addGroup-${k}">Add group</button>
        <button type="button" class="tiny" data-wmf-act="addText" data-focus="wmf-addText-${k}">Add text</button>
        ${wmfInfo(layer).hasAttachments ? `<button type="button" class="tiny" data-wmf-act="addAttachment" data-focus="wmf-addAttachment-${k}">Add attachments</button>` : ""}
      </div>
    </div>
  </section>`;

  const preview = `<section class="wmf-pvcol" aria-labelledby="wmf-pvh-${k}">
    <h3 class="wmf-h" id="wmf-pvh-${k}">Preview <span class="lkind">the editing card, for the sample feature</span></h3>
    <div class="wmf-preview" id="wmf-pv-${k}">${wmfPreviewInner(layer, draft, k)}</div>
    ${wmfSampleMarkup(layer, fields, k, draft)}
  </section>`;

  if (!wide) {
    return `<div class="lstyle wmf wmf-narrow" id="wmf-${k}" data-wmf-layer="${k}">${top}${list}${preview}</div>`;
  }

  const props = `<section class="wmf-propcol" aria-labelledby="wmf-proph-${k}">
    <h3 class="wmf-h wmf-proph" id="wmf-proph-${k}">Properties</h3>
    ${chosen ? wmfEditorMarkup(form, chosen, draft.chosen, fields, k, draft) : wmfFormSettingsMarkup(form, layer, k)}
  </section>`;
  return `<div class="lstyle wmf wmf-wide" id="wmf-${k}" data-wmf-layer="${k}">${top}${list}${preview}${props}</div>`;
}

/**
 * The list's one Tab stop (UX re-review N3): the row the keyboard was last on, else the chosen element, else the form
 * row. Arrow keys move between rows (wmfOnListKey); a row's move and remove buttons are in the Tab order only on the
 * chosen row, so Tab from the list reaches the properties in a handful of presses rather than three per row.
 */
function wmfTabStop(form, draft) {
  const uids = new Set(WMF_F.walk(form).filter(e => e.element).map(e => wmfUid(e.element)));
  if (draft.rove === WMF_FORM_KEY || uids.has(draft.rove)) return draft.rove;
  const chosen = wmfChosenElement(draft);
  return chosen ? wmfUid(chosen) : WMF_FORM_KEY;
}

/** Apply: primary while there is something to apply; otherwise secondary and aria-disabled, still focusable (N12). */
function wmfApplyButton(k, pending) {
  return `<button type="button" class="tiny${pending ? " primary" : ""}" data-wmf-act="apply" data-focus="wmf-apply-${k}"${
    pending ? "" : ` aria-disabled="true"`}>Apply to this map</button>`;
}

/** Whether the draft differs from what the map holds, or was never applied. */
function wmfPending(draft) {
  return draft.applied === "null" || JSON.stringify(draft.form) !== draft.applied;
}

/** Typing changes the form without a redraw; Apply and the note follow it in place. */
function wmfSyncApply(panel, draft) {
  const button = panel.querySelector("button[data-wmf-act=apply]");
  if (!button || !draft.form) return;
  const pending = wmfPending(draft);
  button.classList.toggle("primary", pending);
  if (pending) button.removeAttribute("aria-disabled"); else button.setAttribute("aria-disabled", "true");
  // "Nothing to apply" is no longer true once something changed.
  if (pending && /^Nothing to apply/.test(draft.said)) {
    draft.said = "";
    const said = panel.querySelector(".wmf-said");
    if (said) said.textContent = "";
  }
  const note = panel.querySelector(".wmf-top > .lsnote:first-child");
  if (note) note.textContent = draft.applied === "null" ? "A new form, not applied to the map yet."
    : pending ? "Changed here; not applied to the map yet." : "The form this map holds for the layer.";
}

/** The form's own settings: its title, its description and whether a hidden field keeps its value. */
function wmfFormSettingsMarkup(form, layer, k) {
  return `<div class="wmf-editor wmf-formsettings">
    <label class="lkind" for="wmf-title-${k}">Form title</label>
    <input type="text" id="wmf-title-${k}" data-focus="wmf-title-${k}" data-wmf-prop="title" value="${wmEscape(form.title || "")}" placeholder="${wmEscape(layer.title || "")}">
    <label class="lkind" for="wmf-desc-${k}">Description</label>
    <textarea id="wmf-desc-${k}" data-focus="wmf-desc-${k}" data-wmf-prop="description" rows="2">${wmEscape(form.description || "")}</textarea>
    <label class="check"><input type="checkbox" id="wmf-keep-${k}" data-focus="wmf-keep-${k}" data-wmf-prop="preserve"${form.preserveFieldValuesWhenHidden ? " checked" : ""}>
      Keep a field's value when an expression hides it</label>
  </div>`;
}

function wmfListMarkup(form, fields, k, draft, wide, stop) {
  const row = (element, key, siblings, index, inGroup) => {
    const uid = wmfUid(element);
    const name = wmfElementName(element, fields);
    const chosen = draft.chosen === key;
    const tab = chosen ? "" : ` tabindex="-1"`;
    const children = element.type === "group" && Array.isArray(element.formElements)
      ? `<ol class="wmf-list wmf-inner" aria-label="${wmEscape(`In ${name}`)}">${element.formElements.map((c, j) => row(c, `${key}.${j}`, element.formElements, j, true)).join("")
        || `<li class="hint">Empty: move fields here from their own settings.</li>`}</ol>` : "";
    return `<li class="wmf-row${chosen ? " chosen" : ""}${inGroup ? " ingroup" : ""}">
      <div class="wmf-rowhead">
        <button type="button" class="wmf-pick" data-wmf-act="choose" data-wmf-key="${key}" data-focus="wmf-pick-${uid}"
          data-wmf-stop="${uid}" tabindex="${stop === uid ? 0 : -1}"${stop === uid ? ` aria-describedby="wmf-keys-${k}"` : ""} aria-pressed="${chosen}"><span class="wmf-name">${wmEscape(name)}</span><span class="lkind">${wmEscape(wmfKindName[element.type] || element.type)}${
          element.type === "field" ? ` · ${wmEscape(element.fieldName || "")}` : ""}${wmfBadges(form, element)}</span></button>
        <button type="button" class="tiny" data-wmf-act="up" data-wmf-key="${key}" data-focus="wmf-up-${uid}" data-focus-fallback="wmf-down-${uid}"${tab}
          aria-label="${wmEscape(`Move ${name} up`)}"${index === 0 ? " disabled" : ""}>&uarr;</button>
        <button type="button" class="tiny" data-wmf-act="down" data-wmf-key="${key}" data-focus="wmf-down-${uid}" data-focus-fallback="wmf-up-${uid}"${tab}
          aria-label="${wmEscape(`Move ${name} down`)}"${index === siblings.length - 1 ? " disabled" : ""}>&darr;</button>
        <button type="button" class="tiny" data-wmf-act="delete" data-wmf-key="${key}" data-focus="wmf-del-${uid}"${tab}
          aria-label="${wmEscape(`Remove ${name} from the form`)}">&times;</button>
      </div>
      ${chosen && !wide ? wmfEditorMarkup(form, element, key, fields, k, draft) : ""}
      ${children}
    </li>`;
  };
  const list = form.formElements || [];
  return list.length
    ? `<ol class="wmf-list" aria-labelledby="wmf-elh-${k}">${list.map((e, i) => row(e, String(i), list, i, false)).join("")}</ol>`
    : `<p class="hint">The form has no elements. Add a field below.</p>`;
}

/** What a row says about its expressions, in words, not colour. */
function wmfBadges(form, element) {
  const out = [];
  if (WMF_F.modeOf(form, element, "visibilityExpression") === "expression") out.push("shown when…");
  const required = WMF_F.modeOf(form, element, "requiredExpression");
  if (required === "true") out.push("required");
  else if (required === "expression") out.push("required when…");
  const editable = WMF_F.modeOf(form, element, "editableExpression");
  if (typeof element.valueExpression === "string") out.push("calculated");
  else if (editable === "false") out.push("read-only");
  else if (editable === "expression") out.push("editable when…");
  return out.length ? ` · ${out.join(", ")}` : "";
}

/** One expression's control: its mode, its text when it is an expression, and a Test with its answer. */
function wmfExpressionMarkup(form, element, property, k, uid, modes) {
  let mode = WMF_F.modeOf(form, element, property);
  // A fixed answer this control has no option for is the option it means: visible or editable always is the default.
  if (!modes.some(([value]) => value === mode)) mode = mode === "expression" ? "expression" : "none";
  const info = typeof element[property] === "string" ? WMF_F.expressionInfo(form, element[property]) : null;
  const titles = { visibilityExpression: "Visible", requiredExpression: "Required", editableExpression: "Editable" };
  const id = `wmf-${property}-${uid}`;
  return `<div class="wmf-expr">
    <label class="lkind" for="${id}-mode">${titles[property]}</label>
    <select id="${id}-mode" data-focus="${id}-mode" data-wmf-mode="${property}">
      ${modes.map(([value, label]) => `<option value="${value}"${mode === value ? " selected" : ""}>${wmEscape(label)}</option>`).join("")}
    </select>
    <div class="wmf-exprbody"${mode === "expression" ? "" : " hidden"}>
      <label class="lkind" for="${id}">Arcade expression, answering true or false</label>
      <textarea id="${id}" class="wmf-code" rows="2" spellcheck="false" data-focus="${id}" data-wmf-expr="${property}">${wmEscape(mode === "expression" && info ? info.expression || "" : "")}</textarea>
      <div class="row"><button type="button" class="tiny" data-wmf-act="test" data-wmf-prop="${property}" data-focus="${id}-test">Test against the sample</button></div>
      <p class="lsnote wmf-answer" id="${id}-says"></p>
    </div>
  </div>`;
}

/** A label and its input kept on one line together: a pair that breaks apart reads as two unrelated boxes (finding 11). */
function wmfPair(label, input) {
  return `<div class="wmf-pair">${label}${input}</div>`;
}

function wmfEditorMarkup(form, element, key, fields, k, draft) {
  const uid = wmfUid(element);
  const name = wmfElementName(element, fields);
  const visible = wmfExpressionMarkup(form, element, "visibilityExpression", k, uid, [["none", "Always"], ["expression", "When an expression is true"]]);
  const head = `<h3 class="wmf-h" id="wmf-edh-${uid}">${wmEscape(name)} <span class="lkind">${wmEscape(wmfKindName[element.type] || element.type)}</span></h3>`;
  const open = `<div class="wmf-editor" role="group" aria-labelledby="wmf-edh-${uid}">${head}`;

  if (element.type === "field") {
    const field = fields.find(f => f.name === element.fieldName);
    if (!field) return `${open}<p class="lsnote bad">The layer has no field ${wmEscape(element.fieldName)}; remove this element.</p></div>`;
    const types = WMF_F.inputTypesFor(field);
    const input = element.inputType || WMF_F.defaultInput(field);
    const groups = (form.formElements || []).map((e, i) => [e, i]).filter(([e]) => e.type === "group");
    const inGroup = String(key).includes(".") ? String(key).split(".")[0] : "";
    const takesHint = ["text-box", "text-area", "barcode-scanner"].includes(input.type);
    const coded = WMF_F.codedValues(field) || [];
    const value = typeof element.valueExpression === "string" ? WMF_F.expressionInfo(form, element.valueExpression) : null;
    const fieldEditable = field.editable !== false;
    const limit = WMF_F.lengthLimit(field);
    return `${open}
      ${fieldEditable ? "" : `<p class="hint">The layer does not let ${wmEscape(field.alias || field.name)} be edited, so it is shown read-only and is never calculated.</p>`}
      <label class="lkind" for="wmf-label-${uid}">Label</label>
      <input type="text" id="wmf-label-${uid}" data-focus="wmf-label-${uid}" data-wmf-el="label" value="${wmEscape(element.label || "")}" placeholder="${wmEscape(field.alias || field.name)}">
      ${takesHint ? `<label class="lkind" for="wmf-hint-${uid}">Hint, shown in the empty box</label>
      <input type="text" id="wmf-hint-${uid}" data-focus="wmf-hint-${uid}" data-wmf-el="hint" value="${wmEscape(element.hint || "")}">` : ""}
      <label class="lkind" for="wmf-eldesc-${uid}">Description</label>
      <input type="text" id="wmf-eldesc-${uid}" data-focus="wmf-eldesc-${uid}" data-wmf-el="description" value="${wmEscape(element.description || "")}">
      <label class="lkind" for="wmf-input-${uid}">Input</label>
      <select id="wmf-input-${uid}" data-focus="wmf-input-${uid}" data-wmf-input="type">
        ${types.map(t => `<option value="${t}"${t === input.type ? " selected" : ""}>${wmEscape(WMF_F.INPUT_LABELS[t] || t)}</option>`).join("")}
        ${input.type && !types.includes(input.type) ? `<option value="${wmEscape(input.type)}" selected>${wmEscape(input.type)} (kept as written)</option>` : ""}
      </select>
      ${["text-box", "text-area", "barcode-scanner"].includes(input.type) && field.type === "esriFieldTypeString" ? `<label class="lkind" for="wmf-max-${uid}">Most characters (${
        limit ? `the field holds ${limit.toLocaleString()}` : "the field has no limit"})</label>
        <input type="number" min="1"${limit ? ` max="${limit}"` : ""} id="wmf-max-${uid}" data-focus="wmf-max-${uid}" data-wmf-input="maxLength" value="${input.maxLength ?? ""}">` : ""}
      ${["combo-box", "radio-buttons"].includes(input.type) && field.nullable !== false ? `<label class="check"><input type="checkbox" id="wmf-novalue-${uid}" data-focus="wmf-novalue-${uid}" data-wmf-input="showNoValueOption"${input.showNoValueOption !== false ? " checked" : ""}> Offer “No value”</label>
        <label class="lkind" for="wmf-novaluelabel-${uid}">“No value” is called</label>
        <input type="text" id="wmf-novaluelabel-${uid}" data-focus="wmf-novaluelabel-${uid}" data-wmf-input="noValueOptionLabel" value="${wmEscape(input.noValueOptionLabel || "")}" placeholder="No value">` : ""}
      ${input.type === "switch" ? ["onValue", "offValue"].map(which => `<label class="lkind" for="wmf-${which}-${uid}">${which === "onValue" ? "On" : "Off"} means</label>
        <select id="wmf-${which}-${uid}" data-focus="wmf-${which}-${uid}" data-wmf-input="${which}">${coded.map(c => `<option value="${wmEscape(String(c.code))}"${String(c.code) === String(input[which]) ? " selected" : ""}>${wmEscape(c.name)}</option>`).join("")}</select>`).join("") : ""}
      ${input.type === "datetime-picker" ? `<label class="check"><input type="checkbox" id="wmf-time-${uid}" data-focus="wmf-time-${uid}" data-wmf-input="includeTime"${input.includeTime ? " checked" : ""}> Ask for the time as well</label>` : ""}
      ${input.type === "date-picker" ? `<div class="wmf-pairs">${
        wmfPair(`<label class="lkind" for="wmf-min-${uid}">Earliest</label>`, `<input type="date" id="wmf-min-${uid}" data-focus="wmf-min-${uid}" data-wmf-input="min" value="${wmEscape(input.min || "")}">`)}${
        wmfPair(`<label class="lkind" for="wmf-maxd-${uid}">Latest</label>`, `<input type="date" id="wmf-maxd-${uid}" data-focus="wmf-maxd-${uid}" data-wmf-input="max" value="${wmEscape(input.max || "")}">`)}</div>` : ""}
      ${groups.length ? `<label class="lkind" for="wmf-group-${uid}">In group</label>
      <select id="wmf-group-${uid}" data-focus="wmf-group-${uid}" data-wmf-move="group">
        <option value=""${inGroup === "" ? " selected" : ""}>None (at the top level)</option>
        ${groups.map(([g, i]) => `<option value="${i}"${String(i) === inGroup ? " selected" : ""}>${wmEscape(g.label || "Group")}</option>`).join("")}
      </select>` : ""}
      ${field.nullable === false
        ? `<p class="hint">Required: the layer does not let ${wmEscape(field.alias || field.name)} be empty.</p>`
        : wmfExpressionMarkup(form, element, "requiredExpression", k, uid, [["none", "No"], ["true", "Yes, always"], ["expression", "When an expression is true"]])}
      ${fieldEditable ? wmfExpressionMarkup(form, element, "editableExpression", k, uid, [["none", "Yes"], ["false", "No, read-only"], ["expression", "When an expression is true"]]) : ""}
      ${visible}
      ${fieldEditable ? `<div class="wmf-expr">
        <label class="lkind" for="wmf-valueExpression-${uid}">Calculated value (Arcade)</label>
        <p class="hint" id="wmf-valuenote-${uid}">Runs only while the field is not editable, as the web map specification says; the reader cannot type over it.</p>
        <textarea id="wmf-valueExpression-${uid}" class="wmf-code" rows="2" spellcheck="false" aria-describedby="wmf-valuenote-${uid}" data-focus="wmf-valueExpression-${uid}" data-wmf-expr="valueExpression">${wmEscape(value ? value.expression || "" : "")}</textarea>
        <div class="row"><button type="button" class="tiny" data-wmf-act="test" data-wmf-prop="valueExpression" data-focus="wmf-valueExpression-${uid}-test">Test against the sample</button></div>
        <p class="lsnote wmf-answer" id="wmf-valueExpression-${uid}-says"></p>
      </div>` : ""}
    </div>`;
  }

  if (element.type === "group") {
    return `${open}
      <label class="lkind" for="wmf-label-${uid}">Label</label>
      <input type="text" id="wmf-label-${uid}" data-focus="wmf-label-${uid}" data-wmf-el="label" value="${wmEscape(element.label || "")}">
      <label class="lkind" for="wmf-eldesc-${uid}">Description</label>
      <input type="text" id="wmf-eldesc-${uid}" data-focus="wmf-eldesc-${uid}" data-wmf-el="description" value="${wmEscape(element.description || "")}">
      <label class="check"><input type="checkbox" id="wmf-collapsed-${uid}" data-focus="wmf-collapsed-${uid}" data-wmf-el="collapsed"${element.initialState === "collapsed" ? " checked" : ""}> Start collapsed</label>
      ${visible}
    </div>`;
  }

  if (element.type === "text") {
    return `${open}
      <label class="lkind" for="wmf-text-${uid}">Text — a field or an expression in braces is its value, as {${wmEscape((fields[0] || {}).name || "field")}}</label>
      <textarea id="wmf-text-${uid}" rows="4" maxlength="1000" data-focus="wmf-text-${uid}" data-wmf-el="text">${wmEscape(element.text || "")}</textarea>
      <label class="check"><input type="checkbox" id="wmf-md-${uid}" data-focus="wmf-md-${uid}" data-wmf-el="markdown"${element.textFormat === "markdown" ? " checked" : ""}> Markdown (headings, bold, italic, lists)</label>
      ${visible}
    </div>`;
  }

  if (element.type === "attachment") {
    return `${open}
      <label class="lkind" for="wmf-label-${uid}">Label</label>
      <input type="text" id="wmf-label-${uid}" data-focus="wmf-label-${uid}" data-wmf-el="label" value="${wmEscape(element.label || "")}">
      <label class="lkind" for="wmf-keyword-${uid}">Keyword, written on each file and used to find them again</label>
      <input type="text" id="wmf-keyword-${uid}" data-focus="wmf-keyword-${uid}" data-wmf-el="attachmentKeyword" value="${wmEscape(element.attachmentKeyword || "")}">
      <label class="lkind" for="wmf-eldesc-${uid}">Description</label>
      <input type="text" id="wmf-eldesc-${uid}" data-focus="wmf-eldesc-${uid}" data-wmf-el="description" value="${wmEscape(element.description || "")}">
      <div class="wmf-pairs">${
        wmfPair(`<label class="lkind" for="wmf-minc-${uid}">At least</label>`, `<input type="number" min="0" id="wmf-minc-${uid}" data-focus="wmf-minc-${uid}" data-wmf-el="minAttachmentCount" value="${element.minAttachmentCount ?? ""}">`)}${
        wmfPair(`<label class="lkind" for="wmf-maxc-${uid}">At most</label>`, `<input type="number" min="1" id="wmf-maxc-${uid}" data-focus="wmf-maxc-${uid}" data-wmf-el="maxAttachmentCount" value="${element.maxAttachmentCount ?? ""}">`)}</div>
      <p class="hint">Field Maps takes the files. Studio's editing card does not (D-477).</p>
      ${visible}
    </div>`;
  }

  return `${open}<p class="hint">Kept in the form as it was written. Studio does not edit this kind of element.</p></div>`;
}

function wmfSampleMarkup(layer, fields, k, draft) {
  const sample = draft.sample !== null ? draft.sample
    : JSON.stringify(Object.fromEntries(fields.map(f => [f.name, null])), null, 1);
  const tested = draft.tested;
  // Open as the author left it: a redraw that closed it dropped the focus inside it to the page (finding 3).
  return `<details class="wmf-sample" id="wmf-sampleBox-${k}"${draft.sampleOpen ? " open" : ""}>
    <summary data-focus="wmf-sampleSummary-${k}">Sample feature, for testing</summary>
    <p class="hint">The preview and the tests use these values. Dates are milliseconds since 1970.</p>
    <label class="lkind" for="wmf-sample-${k}">Attributes (JSON)</label>
    <textarea id="wmf-sample-${k}" class="wmf-code" rows="5" spellcheck="false" data-focus="wmf-sample-${k}" data-wmf-sample="1">${wmEscape(sample)}</textarea>
    <div class="row">
      <label class="lkind" for="wmf-edittype-${k}">As</label>
      <select id="wmf-edittype-${k}" data-focus="wmf-edittype-${k}" data-wmf-edittype="1">
        <option value="UPDATE"${draft.editType === "UPDATE" ? " selected" : ""}>an edit (UPDATE)</option>
        <option value="INSERT"${draft.editType === "INSERT" ? " selected" : ""}>a new feature (INSERT)</option>
      </select>
      <button type="button" class="tiny" data-wmf-act="readSample" data-focus="wmf-readSample-${k}">Read a feature from the layer</button>
      <button type="button" class="tiny" data-wmf-act="testAll" data-focus="wmf-testAll-${k}">Test the form</button>
    </div>
    <div class="wmf-testedbox" id="wmf-tested-${k}">${tested ? wmfTestedMarkup(tested, fields) : ""}</div>
  </details>`;
}

function wmfTestedMarkup(result, fields) {
  if (result.error) return `<p class="lsnote bad">${wmEscape(result.error)}</p>`;
  const rows = result.elements.filter(e => e.type === "field").map(state => {
    const name = wmfElementName(state.element, fields);
    const words = [state.visible ? "shown" : "hidden", state.required ? "required" : "optional", state.editable ? "editable" : "read-only"];
    if (state.calculated) words.push(`calculated: ${JSON.stringify(result.values[state.field.name])}`);
    const errors = Object.entries(state.errors).map(([p, m]) => `${p.replace("Expression", "")}: ${m}`);
    return `<li><b>${wmEscape(name)}</b> — ${wmEscape(words.join(", "))}${errors.length ? `<br><span class="bad">${wmEscape(errors.join(" "))}</span>` : ""}</li>`;
  });
  return `<p class="lsnote">${result.errors.length ? `${result.errors.length} expression${result.errors.length === 1 ? "" : "s"} failed.` : "Every expression ran."}</p><ul class="wmf-tested">${rows.join("")}</ul>`;
}

/**
 * The form as the editing card draws it, for the sample feature: the card's own markup (wmfBodyMarkup) evaluated by the
 * card's own function (wmfShowResult), in a detached element, then made read-only and given ids of its own so it does
 * not collide with a card open beside it (finding 2). An element the sample hides is shown faded and says so, because
 * the author needs to see what they built, not only what this one feature shows.
 */
function wmfPreviewInner(layer, draft, k) {
  const form = draft.form;
  if (!form || !(form.formElements || []).length) return `<p class="hint">The preview shows the form once it has an element.</p>`;
  const result = wmfTest(layer, draft);
  if (result.error) return `<p class="lsnote bad">No preview: ${wmEscape(result.error)}</p>`;
  const host = document.createElement("div");
  host.innerHTML = `<div class="editform wmf-form wmf-pvform">
    ${form.title ? `<h4 class="wmf-title">${wmEscape(form.title)}</h4>` : ""}
    ${form.description ? `<p class="hint">${wmEscape(form.description)}</p>` : ""}
    <p class="hint wmf-reqnote" hidden>* required</p>
    ${wmfBodyMarkup(result, result.values)}
  </div>`;
  wmfShowResult(host, result, { preview: true });
  const chosen = wmfChosenElement(draft);
  const prefix = `wmfp-${wmfIdPart(k)}-`;
  for (const el of host.querySelectorAll("*")) {
    for (const attr of ["id", "for", "aria-describedby", "aria-labelledby", "name"]) {
      const value = el.getAttribute(attr);
      if (value) el.setAttribute(attr, value.split(/\s+/).filter(Boolean).map(t => prefix + t).join(" "));
    }
    for (const attr of ["data-field", "data-wmf-proxy"]) {
      if (el.hasAttribute(attr)) { el.setAttribute(`data-pv-${attr.slice(5)}`, el.getAttribute(attr)); el.removeAttribute(attr); }
    }
    if (el.tagName === "SUMMARY") el.setAttribute("tabindex", "-1");
    if (el.matches("input, select, textarea")) {
      el.setAttribute("disabled", "");
      el.removeAttribute("required");
    }
    if (chosen && el.dataset.wmfUid === wmfUid(chosen)) el.classList.add("wmf-pvchosen");
  }
  return host.innerHTML;
}

/** A layer id as an id fragment. */
function wmfIdPart(key) {
  return String(key).replace(/[^A-Za-z0-9_-]/g, "_");
}

// ---------------------------------------------------------------------------------------------------- designer actions

function wmfPanelOf(target) {
  const panel = target && target.closest ? target.closest(".wmf[data-wmf-layer]") : null;
  const layer = panel && wmLayerById(panel.dataset.wmfLayer);
  return layer ? { panel, layer, draft: wmfDraft(layer) } : null;
}

/** Reads what is typed in the panel into the draft: the form's own settings and the chosen element's. */
function wmfReadPanel(panel, layer, draft) {
  const form = draft.form;
  const sample = panel.querySelector("[data-wmf-sample]");
  if (sample) draft.sample = sample.value;
  const editType = panel.querySelector("[data-wmf-edittype]");
  if (editType) draft.editType = editType.value;
  if (!form) return;
  for (const control of panel.querySelectorAll("[data-wmf-prop]")) {
    if (control.tagName === "BUTTON") continue;
    const prop = control.dataset.wmfProp;
    if (prop === "preserve") {
      if (control.checked) form.preserveFieldValuesWhenHidden = true; else delete form.preserveFieldValuesWhenHidden;
    } else {
      const value = control.value.trim();
      if (value) form[prop] = value; else delete form[prop];
    }
  }
  const element = draft.chosen && WMF_F.locate(form, draft.chosen).element;
  if (!element) return;
  const field = element.type === "field" ? wmfFields(layer).find(f => f.name === element.fieldName) : null;
  for (const control of panel.querySelectorAll("[data-wmf-el]")) {
    const prop = control.dataset.wmfEl;
    if (prop === "collapsed") { element.initialState = control.checked ? "collapsed" : "expanded"; continue; }
    if (prop === "markdown") { element.textFormat = control.checked ? "markdown" : "plain-text"; continue; }
    if (prop === "minAttachmentCount" || prop === "maxAttachmentCount") {
      if (control.value === "") delete element[prop]; else element[prop] = Math.max(0, Math.trunc(Number(control.value)));
      continue;
    }
    const value = prop === "text" ? control.value : control.value.trim();
    if (value) element[prop] = value; else delete element[prop];
  }
  if (element.type === "field" && field) {
    const input = element.inputType = element.inputType || WMF_F.defaultInput(field);
    for (const control of panel.querySelectorAll("[data-wmf-input]")) {
      const prop = control.dataset.wmfInput;
      if (prop === "type") continue;
      if (control.type === "checkbox") { input[prop] = control.checked; continue; }
      if (prop === "maxLength") {
        if (control.value === "") delete input.maxLength; else input.maxLength = Math.max(1, Math.trunc(Number(control.value)));
        continue;
      }
      if (prop === "onValue" || prop === "offValue") {
        const coded = (WMF_F.codedValues(field) || []).find(c => String(c.code) === control.value);
        input[prop] = coded ? coded.code : control.value;
        continue;
      }
      if (control.value) input[prop] = control.value; else delete input[prop];
    }
  }
  for (const control of panel.querySelectorAll("[data-wmf-expr]")) {
    const property = control.dataset.wmfExpr;
    if (property !== "valueExpression") {
      const mode = panel.querySelector(`[data-wmf-mode="${property}"]`);
      if (!mode || mode.value !== "expression") continue;
    }
    WMF_F.setExpression(form, element, property, control.value, field);
  }
}

/** Redraws the Forms panel (through the viewer's own redraw, which keeps focus), and says something in it. */
function wmfRedraw(layer, said = null, bad = false, focus = null) {
  const draft = wmfDraft(layer);
  if (said !== null) { draft.said = said; draft.saidBad = bad; }
  // A row the redraw focuses is the list's Tab stop in the markup drawn, not only once focusin runs.
  if (focus && focus.startsWith("wmf-pick-")) draft.rove = focus.startsWith("wmf-pick-form-") ? WMF_FORM_KEY : focus.slice("wmf-pick-".length);
  wmDrawSide();
  if (focus) document.querySelector(`#layerTool [data-focus="${CSS.escape(focus)}"]`)?.focus();
  if (said) wmfAnnounce(said);
  wmfRevealChosen(layer);
}

/**
 * Says a sentence from the region beside #layerTool. A live region drawn inside the markup a redraw replaces is a new
 * node each time, and one inserted with its text already in it is often not read (finding 7); this one is never
 * replaced. The same sentence twice is still announced: a no-break space alternates at its end.
 */
function wmfAnnounce(text) {
  const line = wm$("layerToolSays");
  if (!line) return;
  const plain = String(text || "");
  line.textContent = line.textContent === plain ? `${plain} ` : plain;
}

/** Brings the chosen element into view in the preview, inside the preview's own scrolling column. */
function wmfRevealChosen(layer) {
  const panel = document.querySelector(`#layerTool .wmf[data-wmf-layer="${CSS.escape(layer.id)}"]`);
  const node = panel && panel.querySelector(".wmf-preview .wmf-pvchosen");
  if (!node) return;
  const column = node.closest(".wmf-pvcol");
  if (!column || column.scrollHeight <= column.clientHeight) return;
  const top = node.getBoundingClientRect().top - column.getBoundingClientRect().top + column.scrollTop;
  if (top < column.scrollTop || top + node.offsetHeight > column.scrollTop + column.clientHeight) column.scrollTop = Math.max(0, top - 40);
}

function wmfMove(form, key, step) {
  const { list, index } = WMF_F.locate(form, key);
  const to = index + step;
  if (to < 0 || to >= list.length) return key;
  [list[index], list[to]] = [list[to], list[index]];
  const parts = String(key).split(".");
  parts[parts.length - 1] = String(to);
  return parts.join(".");
}

function wmfSampleValues(layer, draft) {
  const fields = wmfFields(layer);
  const text = draft.sample !== null ? draft.sample : "{}";
  let parsed;
  try { parsed = JSON.parse(text || "{}"); } catch (e) { return { error: `The sample is not JSON: ${e.message}` }; }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return { error: "The sample is a JSON object of field names and values." };
  const values = Object.fromEntries(fields.map(f => [f.name, null]));
  for (const [k, v] of Object.entries(parsed)) {
    const field = fields.find(f => f.name.toLowerCase() === k.toLowerCase());
    values[field ? field.name : k] = v;
  }
  return { values };
}

function wmfTest(layer, draft) {
  const sample = wmfSampleValues(layer, draft);
  if (sample.error) return { error: sample.error };
  const info = wmfInfo(layer);
  return WMF_F.evaluate(draft.form, { fields: info.fields || [], values: sample.values, original: draft.editType === "INSERT" ? null : sample.values,
    editType: draft.editType, layer: info });
}

async function wmfReadSample(layer, draft) {
  const info = wmfInfo(layer);
  try {
    const answer = await wmFetch(`${layer.url}/query?${new URLSearchParams({ where: "1=1", outFields: "*", returnGeometry: "false",
      resultRecordCount: "1", orderByFields: info.objectIdField || "", f: "json" })}`);
    const feature = ((answer || {}).features || [])[0];
    if (!feature) return wmfRedraw(layer, "The layer has no feature to read; type the sample's values instead.", true);
    const keep = new Set(wmfFields(layer).map(f => f.name));
    draft.sample = JSON.stringify(Object.fromEntries(Object.entries(feature.attributes || {}).filter(([k]) => keep.has(k))), null, 1);
    draft.tested = null;
    // The sample is shown open, so the focus put in its box is somewhere a reader can see (finding 3).
    draft.sampleOpen = true;
    return wmfRedraw(layer, "A feature was read into the sample.", false, `wmf-sample-${layer.id}`);
  } catch (e) {
    return wmfRedraw(layer, `No feature was read: ${e.message || e}`, true);
  }
}

function wmfApply(layer, draft) {
  const fields = wmfInfo(layer).fields || [];
  const form = draft.form;
  WMF_F.prune(form);
  draft.problems = WMF_F.problems(form, fields);
  draft.warnings = [];
  if (draft.problems.length) {
    wmfRedraw(layer, `Not applied: ${draft.problems.length} ${draft.problems.length === 1 ? "problem" : "problems"}, listed above.`, true);
    document.querySelector(`#wmf-problems-${CSS.escape(layer.id)}`)?.focus();
    return;
  }
  if (WMF_F.walk(form).some(e => e.element && e.element.type === "attachment")) form.supportsAttachmentElements = true;
  layer.formInfo = wmfClone(form);
  draft.applied = JSON.stringify(layer.formInfo);
  // A field an expression reads and the layer does not have is said, not refused (finding 15).
  draft.warnings = WMF_F.warnings(form, fields);
  wmMarkDirty();
  const check = draft.warnings.length ? ` Check ${draft.warnings.length === 1 ? "one thing" : `${draft.warnings.length} things`} listed above.` : "";
  wmfRedraw(layer, `The form of ${layer.title} is set in this map. Save the map to keep it.${check}`, false, `wmf-apply-${layer.id}`);
}

function wmfOnClick(event) {
  const button = event.target instanceof Element ? event.target.closest("button[data-wmf-act]") : null;
  if (!button) return;
  const found = wmfPanelOf(button);
  if (!found) return;
  const { panel, layer, draft } = found;
  wmfReadPanel(panel, layer, draft);
  const fields = wmfFields(layer);
  const act = button.dataset.wmfAct;
  const key = button.dataset.wmfKey;
  const form = draft.form;
  draft.problems = act === "apply" ? draft.problems : [];
  if (act !== "apply") draft.warnings = [];
  // A result tested before the form changed is a result for another form (finding 9).
  if (!["testAll", "test", "choose", "readSample", "apply"].includes(act)) draft.tested = null;

  switch (act) {
    case "create":
      draft.form = WMF_F.fromFields(wmEditFields(wmfInfo(layer)), layer.title || "");
      draft.chosen = draft.form.formElements.length ? "0" : null;
      wmfRedraw(layer, `A form of ${draft.form.formElements.length} ${draft.form.formElements.length === 1 ? "field" : "fields"} is drafted. Apply it to the map when it is ready.`, false, draft.chosen ? `wmf-pick-${wmfUid(draft.form.formElements[0])}` : `wmf-apply-${layer.id}`);
      return;
    case "discard":
      draft.form = wmfClone(layer.formInfo) || null;
      draft.chosen = null;
      draft.tested = null;
      wmfRedraw(layer, "Changes discarded; the form is as the map holds it.", false, draft.form ? `wmf-apply-${layer.id}` : `wmf-create-${layer.id}`);
      return;
    case "remove":
      // A draft never applied is discarded: the map did not change, so it is not marked unsaved (finding 16).
      if (!layer.formInfo) {
        if (!confirm(`Discard the draft form of ${layer.title}? It was never applied to the map.`)) return;
        draft.form = null;
        draft.chosen = null;
        draft.tested = null;
        wmfRedraw(layer, "The draft is discarded. The map is as it was.", false, `wmf-create-${layer.id}`);
        return;
      }
      if (!confirm(`Remove the form of ${layer.title}? Studio's editing card and Field Maps then fall back to the pop-up's fields.`)) return;
      delete layer.formInfo;
      draft.form = null;
      draft.applied = "null";
      draft.chosen = null;
      draft.tested = null;
      wmMarkDirty();
      wmfRedraw(layer, `The form of ${layer.title} is removed from this map. Save the map to keep it.`, false, `wmf-create-${layer.id}`);
      return;
    case "choose":
      draft.chosen = draft.chosen === key ? null : key;
      wmfRedraw(layer, null, false, button.dataset.focus);
      return;
    case "up":
    case "down": {
      const next = wmfMove(form, key, act === "up" ? -1 : 1);
      if (draft.chosen === key) draft.chosen = next;
      const element = WMF_F.locate(form, next).element;
      wmfRedraw(layer, `${wmfElementName(element, fields)} moved ${act}.`, false, button.dataset.focus);
      return;
    }
    case "delete": {
      // Focus goes to the row that took its place, else the one before, else the group that held it, else Add group:
      // the field list it went to before is disabled when every field is in the form (finding 4).
      const neighbour = WMF_F.neighbourOf(form, key);
      const { list, index, element } = WMF_F.locate(form, key);
      list.splice(index, 1);
      WMF_F.prune(form);
      draft.chosen = null;
      wmfRedraw(layer, `${wmfElementName(element, fields)} was removed from the form.`, false,
        neighbour ? `wmf-pick-${wmfUid(neighbour)}` : `wmf-addGroup-${layer.id}`);
      return;
    }
    case "addField": {
      const name = panel.querySelector(`#wmf-addfield-${CSS.escape(layer.id)}`)?.value;
      const field = fields.find(f => f.name === name);
      if (!field) return;
      const element = { type: "field", fieldName: field.name, label: field.alias || field.name, inputType: WMF_F.defaultInput(field) };
      form.formElements.push(element);
      draft.chosen = String(form.formElements.length - 1);
      wmfRedraw(layer, `${element.label} was added at the end.`, false, `wmf-pick-${wmfUid(element)}`);
      return;
    }
    case "addGroup":
    case "addText":
    case "addAttachment": {
      const element = act === "addGroup" ? { type: "group", label: "New group", initialState: "expanded", formElements: [] }
        : act === "addText" ? { type: "text", text: "", textFormat: "plain-text" }
          : { type: "attachment", label: "Attachments", attachmentKeyword: `attachments-${form.formElements.length}`, inputType: { type: "attachment", inputMethod: "any" } };
      form.formElements.push(element);
      draft.chosen = String(form.formElements.length - 1);
      wmfRedraw(layer, `${wmfKindName[element.type]} added at the end.`, false, `wmf-pick-${wmfUid(element)}`);
      return;
    }
    case "test": {
      const property = button.dataset.wmfProp;
      const result = wmfTest(layer, draft);
      const says = button.closest(".wmf-expr")?.querySelector(".wmf-answer");
      if (!says) return;
      wmfTestAnswer(says, property, result, draft);
      return;
    }
    case "testAll":
      draft.tested = wmfTest(layer, draft);
      wmfRedraw(layer, draft.tested.error ? "The sample could not be read." : `Tested: ${draft.tested.errors.length ? `${draft.tested.errors.length} expression(s) failed` : "every expression ran"}.`, !!draft.tested.error || draft.tested.errors.length > 0, button.dataset.focus);
      return;
    case "readSample":
      wmfReadSample(layer, draft);
      return;
    case "apply":
      if (!wmfPending(draft)) {
        wmfRedraw(layer, "Nothing to apply: the map already holds this form.", false, button.dataset.focus);
        return;
      }
      wmfApply(layer, draft);
      return;
    default:
  }
}

/** A Test's answer, shown under its expression and said from the region a redraw does not replace (finding 7). */
function wmfTestAnswer(says, property, result, draft) {
  const show = (text, bad) => {
    says.textContent = text;
    says.classList.toggle("bad", bad);
    if (text) wmfAnnounce(text);
  };
  if (result.error) return show(result.error, true);
  const state = result.elements.find(e => e.key === draft.chosen);
  const error = state && state.errors[property];
  if (error) return show(`Error: ${error}`, true);
  if (!state) return show("", false);
  if (property === "valueExpression") {
    return show(state.calculated ? `Calculates ${JSON.stringify(result.values[state.field.name])}.`
      : state.editable ? "Not run: the field is editable for this sample. Make it read-only, or editable only when an expression says so."
        : "Not run.", false);
  }
  const answer = property === "visibilityExpression" ? (state.visible ? "Shown" : "Hidden")
    : property === "requiredExpression" ? (state.required ? "Required" : "Not required")
      : (state.editable ? "Editable" : "Read-only");
  return show(`${answer} for this sample.`, false);
}

/** What changed in the form makes a tested result stale: it is taken away rather than left beside the new form. */
function wmfForgetTested(panel, draft) {
  if (!draft.tested) return;
  draft.tested = null;
  const box = panel.querySelector(".wmf-testedbox");
  if (box) box.textContent = "";
}

/** The preview drawn again from the draft, in place: focus is never in it, so nothing is lost. */
function wmfRefreshPreview(panel, layer, draft) {
  const preview = panel.querySelector(".wmf-preview");
  if (preview) preview.innerHTML = wmfPreviewInner(layer, draft, wmEscape(layer.id));
}

/** Typing in the designer: into the draft, the tested result forgotten, the preview redrawn; the panel is not redrawn. */
function wmfOnInput(event) {
  const target = event.target instanceof Element ? event.target : null;
  const found = target && !target.closest(".wmf-preview") && wmfPanelOf(target);
  if (!found) return;
  const { panel, layer, draft } = found;
  wmfReadPanel(panel, layer, draft);
  wmfForgetTested(panel, draft);
  wmfRefreshPreview(panel, layer, draft);
  wmfSyncApply(panel, draft);
}

/**
 * The element list's keys (UX re-review N3): Up and Down, Home and End move between rows; Alt with Up or Down moves the
 * row; Delete removes it. Enter and Space choose, as any button does.
 */
function wmfOnListKey(event) {
  const pick = event.target instanceof Element ? event.target.closest("button.wmf-pick") : null;
  const found = pick && wmfPanelOf(pick);
  if (!found) return;
  const picks = [...found.panel.querySelectorAll("button.wmf-pick")];
  const at = picks.indexOf(pick);
  const head = pick.closest(".wmf-rowhead");
  const uid = pick.dataset.wmfStop;
  if (event.altKey && (event.key === "ArrowUp" || event.key === "ArrowDown")) {
    event.preventDefault();
    const move = head.querySelector(`button[data-wmf-act=${event.key === "ArrowUp" ? "up" : "down"}]`);
    if (!move || move.disabled) return;
    move.click();
    document.querySelector(`#layerTool [data-focus="wmf-pick-${CSS.escape(uid)}"]`)?.focus();
    return;
  }
  if (event.key === "Delete") {
    const remove = head.querySelector("button[data-wmf-act=delete]");
    if (!remove) return;
    event.preventDefault();
    remove.click();
    return;
  }
  const next = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: picks.length - 1 }[event.key];
  if (next === undefined || event.altKey || event.ctrlKey || event.metaKey) return;
  event.preventDefault();
  const to = picks[Math.max(0, Math.min(picks.length - 1, next))];
  // The stop moves with the keys themselves, not only on focusin, which a page without the window's focus does not get.
  wmfRove(found, to);
  to.focus();
}

/** Makes one row the list's Tab stop, and remembers it for the next redraw. */
function wmfRove(found, pick) {
  found.draft.rove = pick.dataset.wmfStop;
  const keys = found.panel.querySelector('[id^="wmf-keys-"]');
  for (const other of found.panel.querySelectorAll("button.wmf-pick")) {
    other.tabIndex = other === pick ? 0 : -1;
    // Described once, on the stop: on every row it was read again at each arrow press.
    if (other === pick && keys) other.setAttribute("aria-describedby", keys.id); else other.removeAttribute("aria-describedby");
  }
}

/** The roving stop follows the keyboard, so a redraw puts the Tab stop where the author was. */
function wmfOnListFocus(event) {
  const pick = event.target instanceof Element ? event.target.closest("button.wmf-pick") : null;
  const found = pick && wmfPanelOf(pick);
  if (found) wmfRove(found, pick);
}

/** The sample's open state, kept so a redraw draws it as the author left it (finding 3). `toggle` does not bubble. */
function wmfOnToggle(event) {
  const box = event.target instanceof Element && event.target.matches(".wmf-sample") ? event.target : null;
  const found = box && wmfPanelOf(box);
  if (found) found.draft.sampleOpen = box.open;
}

/** A click on an element in the preview chooses it, as Map Viewer's form builder does. */
function wmfOnPreviewClick(event) {
  const node = event.target instanceof Element ? event.target.closest(".wmf-preview [data-wmf-uid]") : null;
  if (!node || event.target.closest("summary")) return;
  const found = wmfPanelOf(node);
  if (!found || !found.draft.form) return;
  const hit = WMF_F.walk(found.draft.form).find(e => e.element && wmfUid(e.element) === node.dataset.wmfUid);
  if (!hit) return;
  wmfReadPanel(found.panel, found.layer, found.draft);
  found.draft.chosen = hit.key;
  wmfRedraw(found.layer, null, false, `wmf-pick-${node.dataset.wmfUid}`);
}

function wmfOnChange(event) {
  const target = event.target instanceof Element ? event.target : null;
  const found = target && wmfPanelOf(target);
  if (!found) return;
  const { panel, layer, draft } = found;
  const form = draft.form;
  if (target.closest(".wmf-preview")) return;
  wmfForgetTested(panel, draft);
  // A mode or an input type changes what the panel shows: read, apply, redraw.
  if (target.dataset.wmfMode && form) {
    wmfReadPanel(panel, layer, draft);
    const element = WMF_F.locate(form, draft.chosen).element;
    const property = target.dataset.wmfMode;
    if (target.value === "none") WMF_F.setFixed(form, element, property, null);
    else if (target.value === "true" || target.value === "false") WMF_F.setFixed(form, element, property, target.value === "true");
    else if (target.value === "expression" && WMF_F.modeOf(form, element, property) !== "expression") {
      // An expression is started from the fixed answer, so it is valid from the first moment.
      const was = WMF_F.modeOf(form, element, property);
      WMF_F.setExpression(form, element, property, was === "false" ? "false" : "true");
    }
    if (property === "editableExpression" && target.value === "none" && typeof element.valueExpression === "string") {
      WMF_F.setExpression(form, element, "valueExpression", "");
      wmfRedraw(layer, "The field is editable again, so its calculated value was removed: a calculation runs only on a read-only field.", false, target.dataset.focus);
      return;
    }
    wmfRedraw(layer, null, false, target.dataset.focus);
    return;
  }
  if (target.dataset.wmfInput === "type" && form) {
    wmfReadPanel(panel, layer, draft);
    const element = WMF_F.locate(form, draft.chosen).element;
    const field = wmfFields(layer).find(f => f.name === element.fieldName);
    const coded = WMF_F.codedValues(field) || [];
    const type = target.value;
    element.inputType = type === "switch" ? { type, onValue: coded[0] && coded[0].code, offValue: coded[1] && coded[1].code }
      : type === "combo-box" || type === "radio-buttons" ? { type, showNoValueOption: field.nullable !== false }
        : type === "datetime-picker" ? { type, includeTime: true } : { type };
    if (!["text-box", "text-area", "barcode-scanner"].includes(type)) delete element.hint;
    wmfRedraw(layer, null, false, target.dataset.focus);
    return;
  }
  if (target.dataset.wmfMove === "group" && form) {
    wmfReadPanel(panel, layer, draft);
    const { list, index, element } = WMF_F.locate(form, draft.chosen);
    // The group is found before the element leaves its list, and by identity after, since leaving shifts indexes.
    const into = target.value === "" ? null : form.formElements[Number(target.value)];
    list.splice(index, 1);
    if (!into) {
      form.formElements.push(element);
      draft.chosen = String(form.formElements.length - 1);
    } else {
      into.formElements = into.formElements || [];
      into.formElements.push(element);
      draft.chosen = `${form.formElements.indexOf(into)}.${into.formElements.length - 1}`;
    }
    wmfRedraw(layer, `${wmfElementName(element, wmfFields(layer))} moved ${target.value === "" ? "to the top level" : "into the group"}.`, false, target.dataset.focus);
    return;
  }
  // Anything else typed or ticked: into the draft, without a redraw, so the caret stays; the preview follows.
  wmfReadPanel(panel, layer, draft);
  wmfRefreshPreview(panel, layer, draft);
  wmfSyncApply(panel, draft);
}

wm$("layerTool").addEventListener("click", wmfOnClick);
wm$("layerTool").addEventListener("click", wmfOnPreviewClick);
wm$("layerTool").addEventListener("change", wmfOnChange);
wm$("layerTool").addEventListener("input", wmfOnInput);
wm$("layerTool").addEventListener("toggle", wmfOnToggle, true);
wm$("layerTool").addEventListener("keydown", wmfOnListKey);
wm$("layerTool").addEventListener("focusin", wmfOnListFocus);
// The designer's shape follows the window: one column on a phone or a narrow window, the wide shape otherwise.
// Redrawn when the panel crosses the width the wide shape needs: a window resized, the Layers panel opened beside it.
WM_PHONE.addEventListener("change", () => { if (wmSide.tool === "form") wmDrawSide(); });
if (typeof ResizeObserver === "function") {
  new ResizeObserver(() => {
    if (wmSide.tool !== "form") return;
    const drawn = document.querySelector("#layerTool .wmf[data-wmf-layer]");
    // Only a panel holding a form has two shapes; the empty one has one.
    if (!drawn || !(drawn.classList.contains("wmf-wide") || drawn.classList.contains("wmf-narrow"))) return;
    if (drawn.classList.contains("wmf-wide") !== wmfWide()) wmDrawSide();
  }).observe(wm$("side"));
}

// ---------------------------------------------------------------------------------------------------- the editing card

/** The card's open form: its layer, the compiled form, what was read, and the last evaluation. */
const wmfEditing = { key: null, compiled: null, result: null, fields: null, info: null };

/** The control a field element reads and writes: a [data-field] input, select or hidden proxy. */
function wmfControlMarkup(element, field, value, input, describedBy = `edit-${field.name}-says`) {
  const id = `edit-${field.name}`;
  const described = wmEscape(describedBy);
  const name = wmEscape(field.name);
  const coded = WMF_F.codedValues(field) || [];
  const type = input.type;
  const placeholder = element.hint && ["text-box", "text-area", "barcode-scanner"].includes(type) ? ` placeholder="${wmEscape(element.hint)}"` : "";

  if (type === "radio-buttons" && coded.length) {
    const noValue = field.nullable !== false && input.showNoValueOption !== false;
    const choices = [...(noValue ? [{ code: "", name: input.noValueOptionLabel || "No value" }] : []), ...coded];
    return `<input type="hidden" id="${wmEscape(id)}" data-field="${name}" value="${wmEscape(value === null || value === undefined ? "" : String(value))}">
      <div class="wmf-radios" role="radiogroup" aria-labelledby="${wmEscape(id)}-label" aria-describedby="${described}">${choices.map((c, i) => `<label class="check"><input type="radio" name="wmf-r-${name}"
        data-wmf-proxy="${name}" value="${wmEscape(String(c.code))}" id="wmf-r-${name}-${i}"${String(c.code) === String(value ?? "") ? " checked" : ""}> ${wmEscape(c.name)}</label>`).join("")}</div>`;
  }
  if (type === "switch" && coded.length === 2) {
    const on = String(input.onValue ?? coded[0].code);
    const off = String(input.offValue ?? coded[1].code);
    return `<input type="hidden" id="${wmEscape(id)}" data-field="${name}" value="${wmEscape(value === null || value === undefined ? "" : String(value))}">
      <label class="check wmf-switch"><input type="checkbox" role="switch" id="wmf-s-${name}" data-wmf-proxy="${name}" data-on="${wmEscape(on)}" data-off="${wmEscape(off)}"${String(value) === on ? " checked" : ""}
        aria-describedby="${described}"> ${wmEscape((coded.find(c => String(c.code) === on) || {}).name || on)}</label>`;
  }
  if (type === "combo-box" && coded.length) {
    const noValue = field.nullable !== false && input.showNoValueOption !== false;
    return `<select id="${wmEscape(id)}" data-field="${name}" aria-describedby="${described}">
      ${noValue || value === null || value === undefined ? `<option value=""${value === null || value === undefined ? " selected" : ""}>${wmEscape(input.noValueOptionLabel || "—")}</option>` : ""}
      ${coded.map(c => `<option value="${wmEscape(String(c.code))}"${String(c.code) === String(value) ? " selected" : ""}>${wmEscape(c.name)}</option>`).join("")}</select>`;
  }
  if (field.type === "esriFieldTypeDate" && type === "datetime-picker" && !input.includeTime) {
    const local = wmLocalInput(value);
    return `<input type="hidden" id="${wmEscape(id)}" data-field="${name}" value="${wmEscape(local)}">
      <input type="date" id="wmf-d-${name}" data-wmf-proxy="${name}" value="${wmEscape(local.slice(0, 10))}" aria-labelledby="${wmEscape(id)}-label" aria-describedby="${described}">`;
  }
  if (type === "text-area" && field.type === "esriFieldTypeString") {
    const max = input.maxLength || field.length;
    return `<textarea id="${wmEscape(id)}" data-field="${name}" rows="3"${max ? ` maxlength="${max}"` : ""}${placeholder} aria-describedby="${described}">${
      wmEscape(value === null || value === undefined ? "" : String(value))}</textarea>`;
  }
  if (field.type === "esriFieldTypeString" && ["text-box", "barcode-scanner"].includes(type)) {
    const max = input.maxLength || field.length;
    return `<input type="text" id="${wmEscape(id)}" data-field="${name}"${max ? ` maxlength="${max}"` : ""}${placeholder}
      value="${wmEscape(value === null || value === undefined ? "" : String(value))}" aria-describedby="${described}">`;
  }
  // A number is typed in a text box and read by WMF_F.decimalText into the field's control: in a number box Chrome made
  // "90,5" 905 without a word (2026-11-23). The control keeps what wmReadControl reads; the box is what is shown.
  if (WM_NUMBER_TYPES.has(field.type) && !coded.length) {
    const whole = /Integer/.test(field.type);
    const domain = field.domain || {};
    const range = domain.type === "range" && Array.isArray(domain.range) ? ` min="${wmEscape(String(domain.range[0]))}" max="${wmEscape(String(domain.range[1]))}"` : "";
    const shown = value === null || value === undefined ? "" : wmEscape(String(value));
    return `<input type="hidden" id="${wmEscape(id)}" data-field="${name}" value="${shown}"${range}>
      <input type="text" inputmode="${whole ? "numeric" : "decimal"}" autocomplete="off" id="wmf-n-${name}" data-wmf-proxy="${name}" data-wmf-number="${whole ? "whole" : "decimal"}"
        value="${shown}"${placeholder} aria-describedby="${described}">`;
  }
  // Every other input — a date with its time, a type the card does not draw — is the card's own control.
  return wmFieldControl(field, value).replace(/<(input|select|textarea)\b/, m => (placeholder ? `${m}${placeholder}` : m))
    .replace(`aria-describedby="${wmEscape(id)}-says"`, `aria-describedby="${described}"`);
}

function wmfElementMarkup(state, values) {
  const element = state.element;
  const uid = wmfUid(element);
  if (element.type === "field") {
    const field = state.field;
    if (!field) return `<p class="hint wmf-el" data-wmf-uid="${uid}">${wmEscape(element.fieldName)}: the layer has no such field.</p>`;
    const input = element.inputType || WMF_F.defaultInput(field);
    const id = `edit-${field.name}`;
    const coded = WMF_F.codedValues(field) || [];
    const number = WM_NUMBER_TYPES.has(field.type) && !coded.length && !["radio-buttons", "switch", "combo-box"].includes(input.type);
    const usesProxy = (input.type === "radio-buttons" && coded.length)
      || (input.type === "switch" && coded.length === 2)
      || (field.type === "esriFieldTypeDate" && input.type === "datetime-picker" && !input.includeTime)
      || number;
    const forId = !usesProxy ? id : number ? `wmf-n-${field.name}` : input.type === "radio-buttons" ? ""
      : input.type === "switch" ? `wmf-s-${field.name}` : `wmf-d-${field.name}`;
    const labelTag = forId ? `<label class="field" id="${wmEscape(id)}-label" for="${wmEscape(forId)}">` : `<span class="field" id="${wmEscape(id)}-label">`;
    return `<div class="editfield wmf-el" data-wmf-uid="${uid}" data-wmf-field="${wmEscape(field.name)}">
      ${labelTag}${wmEscape(element.label || field.alias || field.name)}<span class="wmf-req" aria-hidden="true" hidden> *</span><span class="wmf-ro lkind" hidden> read-only</span><span class="wmf-calc lkind" hidden> calculated</span>${forId ? "</label>" : "</span>"}
      ${element.description ? `<p class="hint wmf-desc" id="${wmEscape(id)}-desc">${wmEscape(element.description)}</p>` : ""}
      ${wmfControlMarkup(element, field, values[field.name], input, element.description ? `${id}-desc ${id}-says` : `${id}-says`)}
      <p class="fieldsays" id="${wmEscape(id)}-says"></p>
      <p class="fieldsays wmf-exprsays" id="wmf-x-${uid}"></p>
    </div>`;
  }
  if (element.type === "text") {
    return `<div class="wmf-el wmf-text" data-wmf-uid="${uid}"></div>`;
  }
  if (element.type === "attachment") {
    return `<p class="hint wmf-el" data-wmf-uid="${uid}">${wmEscape(element.label || "Attachments")}: photos and files are added in Field Maps; this form does not take them.</p>`;
  }
  if (element.type === "relationship") {
    return `<p class="hint wmf-el" data-wmf-uid="${uid}">${wmEscape(element.label || "Related records")}: related records are not edited in this form.</p>`;
  }
  return "";
}

/** A form's elements as the card draws them, groups holding theirs: the card and the designer's preview both draw this. */
function wmfBodyMarkup(evaluated, values) {
  const top = [];
  for (const state of evaluated.elements) {
    if (state.key.includes(".")) continue;
    if (state.type === "group") {
      const children = evaluated.elements.filter(s => s.key.startsWith(`${state.key}.`));
      top.push(`<details class="wmf-group wmf-el" data-wmf-uid="${wmfUid(state.element)}"${state.expanded ? " open" : ""}>
        <summary>${wmEscape(state.element.label || "Group")}</summary>
        ${state.element.description ? `<p class="hint">${wmEscape(state.element.description)}</p>` : ""}
        ${children.map(c => wmfElementMarkup(c, values)).join("")}
      </details>`);
    } else {
      top.push(wmfElementMarkup(state, values));
    }
  }
  return top.join("");
}

/** The values the form's controls hold now, typed as their fields are; a value that does not read is null. */
function wmfReadValues(form, held) {
  const before = held.feature.attributes || {};
  const values = { ...before, ...(held.isNew ? held.preset || {} : {}) };
  for (const control of form.querySelectorAll("[data-field]")) {
    const field = (wmfEditing.info.fields || []).find(f => f.name === control.dataset.field);
    if (!field) continue;
    const was = before[field.name] === undefined ? null : before[field.name];
    const read = wmReadControl(control, field, was);
    values[field.name] = read.problem ? null : read.same ? was : read.value;
  }
  return values;
}

/** Writes a value into a field's control and its proxy, as the control shows it. */
function wmfWriteValue(form, field, value) {
  const control = form.querySelector(`[data-field="${CSS.escape(field.name)}"]`);
  if (!control) return;
  if (field.type === "esriFieldTypeDate") control.value = wmLocalInput(value);
  else control.value = value === null || value === undefined ? "" : String(value);
  for (const proxy of form.querySelectorAll(`[data-wmf-proxy="${CSS.escape(field.name)}"]`)) {
    if (proxy.type === "radio") proxy.checked = proxy.value === control.value;
    else if (proxy.type === "checkbox") proxy.checked = control.value === proxy.dataset.on;
    else if (proxy.dataset.wmfNumber) proxy.value = control.value;
    else if (proxy.type === "date") proxy.value = control.value.slice(0, 10);
  }
}

/** Evaluates the form against what the controls hold, and shows the answer: no redraw, so focus stays. */
function wmfApplyResult(form, held, announce) {
  const values = wmfReadValues(form, held);
  const previous = wmfEditing.result ? wmfEditing.result.visible : null;
  const result = WMF_F.evaluate(wmfEditing.compiled, { fields: wmfEditing.info.fields || [], values,
    original: held.isNew ? null : held.feature.attributes || {}, editType: held.isNew ? "INSERT" : "UPDATE",
    geometry: held.isNew ? held.geometry : held.feature.geometry || null, layer: wmfEditing.info, previousVisible: previous });
  const { appeared, went } = wmfShowResult(form, result, { preview: false });
  wmfEditing.result = result;
  if (announce && (appeared || went)) {
    const parts = [];
    if (appeared) parts.push(`${appeared} ${appeared === 1 ? "question appeared" : "questions appeared"}`);
    if (went) parts.push(`${went} ${went === 1 ? "question was hidden" : "questions were hidden"}`);
    const live = form.querySelector("#wmfLive");
    if (live) live.textContent = `${parts.join(", ")}.`;
  }
  return result;
}

/**
 * Shows an evaluated form on its drawn elements: what is hidden, required, read-only and calculated, the text elements'
 * text, and an expression that failed. No redraw, so focus stays. In the designer's preview (`preview`), an element the
 * sample hides stays in sight, faded and saying so, and no value is written. Answers how many questions came and went.
 */
function wmfShowResult(form, result, { preview = false } = {}) {
  let appeared = 0;
  let went = 0;
  for (const state of result.elements) {
    const node = form.querySelector(`[data-wmf-uid="${wmfUid(state.element)}"]`);
    if (!node) continue;
    if (preview) {
      node.classList.toggle("wmf-pvhidden", !state.visible);
    } else {
      // Questions are fields: a group or a text that comes and goes is not counted, its fields are.
      if (node.hidden === state.visible && state.type === "field") { if (state.visible) appeared++; else went++; }
      node.hidden = !state.visible;
    }
    if (state.type === "group") continue;
    if (state.type === "text") {
      node.innerHTML = state.element.textFormat === "markdown" ? WMF_F.markdownHtml(state.text) : wmEscape(state.text).replace(/\n/g, "<br>");
      // An empty text has no height in the preview, so it could be neither seen nor clicked.
      if (preview && !String(state.text || "").trim()) node.innerHTML = `<span class="hint">Empty text: write it in its properties.</span>`;
      continue;
    }
    if (state.type !== "field" || !state.field) continue;
    const field = state.field;
    if (!preview && (result.calculated.includes(field.name) || result.cleared.includes(field.name))) wmfWriteValue(form, field, result.values[field.name]);
    const control = form.querySelector(`[data-field="${CSS.escape(field.name)}"]`);
    const shown = [...form.querySelectorAll(`[data-wmf-proxy="${CSS.escape(field.name)}"]`)];
    const targets = shown.length ? shown : control ? [control] : [];
    for (const t of targets) {
      if (t.tagName === "SELECT" || t.type === "radio" || t.type === "checkbox" || t.type === "date") t.disabled = !state.editable;
      else t.readOnly = !state.editable;
      t.setAttribute("aria-required", String(!!state.required));
    }
    node.querySelector(".wmf-req").hidden = !state.required;
    node.querySelector(".wmf-ro").hidden = state.editable || !!state.calculated;
    node.querySelector(".wmf-calc").hidden = !state.calculated;
    const errors = Object.entries(state.errors);
    node.querySelector(".wmf-exprsays").textContent = errors.length
      ? `This field's ${errors.map(([p]) => p.replace("Expression", "")).join(" and ")} expression failed, so it is ignored: ${errors.map(([, m]) => m).join(" ")}`
      : "";
  }
  const note = form.querySelector(".wmf-reqnote");
  if (note) note.hidden = !result.elements.some(s => s.type === "field" && s.required && s.visible);
  if (preview) {
    for (const node of form.querySelectorAll(".wmf-pvhidden")) {
      const where = node.matches("details") ? node.querySelector(":scope > summary") : node;
      if (where) where.insertAdjacentHTML(node.matches("details") ? "beforeend" : "afterbegin", " <span class=\"wmf-pvtag lkind\">Hidden for the sample</span> ");
    }
  }
  return { appeared, went };
}

/**
 * Turns the card into the layer's form, when the map holds one for it — ADR-313 §5.4. Answers false when it does not,
 * and wmOpenEdit draws the plain form as before.
 */
function wmfOpenEdit(key) {
  const held = wmIdentified.get(key);
  const formInfo = held && wmfFormOf(held.layer);
  if (!formInfo || !WMF_F || !WMF_A) return false;
  const info = wmfInfo(held.layer);
  const card = wm$("identify");
  wmEditing.key = key;
  wmEditing.dirty = false;
  wmEditing.scroll = card.scrollTop;
  wmfEditing.key = key;
  wmfEditing.info = info;
  wmfEditing.compiled = WMF_F.compile(formInfo);
  wmfEditing.result = null;
  const values = { ...(held.feature.attributes || {}) };
  const opened = WMF_F.evaluate(wmfEditing.compiled, { fields: info.fields || [], values, original: held.isNew ? null : values,
    editType: held.isNew ? "INSERT" : "UPDATE", layer: info });
  card.innerHTML = `<div class="top"><b>${held.isNew ? "New feature" : `Edit ${wmEscape(wmFeatureName(held))}`} <span class="lkind">${
      wmEscape(held.layer.title)}</span></b>
      <button class="tiny" data-close aria-label="Close">&times;</button></div>
    <form class="editform wmf-form" data-editing="${wmEscape(key)}" data-wmf="1" novalidate>
      ${formInfo.title ? `<h3 class="wmf-title">${wmEscape(formInfo.title)}</h3>` : ""}
      ${formInfo.description ? `<p class="hint">${wmEscape(formInfo.description)}</p>` : ""}
      ${held.isNew ? `<p class="hint">${wmEscape(wmShapeSaid(held.geometry))}</p>` : ""}
      <p class="hint wmf-reqnote" hidden>* required</p>
      ${wmfBodyMarkup(opened, values)}
      <p class="sr" id="wmfLive" aria-live="polite"></p>
      <p class="said cvwarn" id="editCv" aria-live="polite" hidden></p>
      <p class="said" id="editSays" role="status" aria-live="polite" tabindex="-1"></p>
      <div class="row editacts">
        <button type="submit" class="primary">${held.isNew ? "Create" : "Save"}</button>
        <button type="button" data-edit-cancel>Cancel</button>
      </div>
    </form>`;
  card.hidden = false;
  card.scrollTop = 0;
  const form = card.querySelector("form.wmf-form");
  wmfApplyResult(form, held, false);
  const first = [...form.querySelectorAll("input:not([type=hidden]), select, textarea")]
    .find(c => !c.disabled && !c.readOnly && !c.closest("[hidden]") && c.getClientRects().length);
  (first || form.querySelector("button[type=submit]"))?.focus();
  // ADR-312: the contingent-values warning is said in a form's card as in the plain one (contingent-editor.js).
  if (typeof cveCheckForm === "function") cveCheckForm(form);
  return true;
}

/** A proxy (radio, switch, date) writes its field's control; then the form is evaluated again. */
function wmfOnCardInput(event) {
  const target = event.target instanceof Element ? event.target : null;
  const form = target && target.closest("form.wmf-form");
  if (!form) return;
  const held = wmIdentified.get(form.dataset.editing);
  if (!held || wmfEditing.key !== form.dataset.editing) return;
  if (target.dataset.wmfProxy) {
    const control = form.querySelector(`[data-field="${CSS.escape(target.dataset.wmfProxy)}"]`);
    if (control) {
      if (target.type === "radio") control.value = target.value;
      else if (target.type === "checkbox") control.value = target.checked ? target.dataset.on : target.dataset.off;
      else if (target.dataset.wmfNumber) wmfReadNumber(form, target, control, event.type === "change");
      else if (target.type === "date") {
        // The time of day the feature had is kept when only its day changes.
        const time = (control.value || "T00:00:00").slice(10) || "T00:00:00";
        control.value = target.value ? `${target.value}${time}` : "";
      }
    }
    wmEditing.dirty = true;
  }
  const result = wmfApplyResult(form, held, true);
  wmfClearAnswered(form, result);
}

/**
 * A number box's text into its field's control: "90,5" and "90.5" are 90.5, and "1,500" or "1.500" is refused, by the
 * rule every Studio screen reads numbers with (number-text.js through WMF_F.decimalText). Text that is not a number is
 * kept as typed, so the save refuses it naming the field. What is wrong is said as it is typed, in the words the save
 * would use — wmReadControl's, so the card has one wording for one fault (the forms reviewer's fourth render, item 3) —
 * and "Not saved" goes once nothing is marked (item 4).
 */
function wmfReadNumber(form, box, control, settle = true) {
  const read = WMF_F.decimalText(box.value);
  control.value = read === null ? box.value : read;
  const says = form.querySelector(`#${CSS.escape(`${control.id}-says`)}`);
  const field = (wmfEditing.info.fields || []).find(f => f.name === control.dataset.field);
  const typed = box.value.trim();
  const checked = field && typed !== "" ? wmReadControl(control, field, null) : {};
  // While typing only an ambiguous number is said; "a" or a lone "-" is a number not finished yet, said when the box is
  // left (fourth render, m2). Clearing is immediate either way.
  const problem = checked.problem && (settle || WMF_F.ambiguousNumber(typed)) ? checked.problem : "";
  if (problem) {
    box.setAttribute("aria-invalid", "true");
    if (says) { says.textContent = problem; says.dataset.wmfNumber = "1"; }
  } else {
    if (says && says.dataset.wmfNumber) {
      box.removeAttribute("aria-invalid");
      says.textContent = "";
      delete says.dataset.wmfNumber;
    }
    // The save marks the field's control itself (wmReadEdit); once the box reads right, that mark goes too, or the
    // card's "Not saved" would stay for a field that is no longer wrong (item 4).
    if (control.getAttribute("aria-invalid") === "true") {
      control.setAttribute("aria-invalid", "false");
      box.removeAttribute("aria-invalid");
      if (says && !says.dataset.wmfRequired) says.textContent = "";
    }
  }
  if (typeof wmClearNotSaved === "function") wmClearNotSaved(form);
}

/**
 * Takes the required error off a field once it is no longer missing: its aria-invalid and the sentence under it. Only
 * the sentence the required check wrote is cleared (finding 5); another check's stays until its own check runs.
 */
function wmfClearAnswered(form, result) {
  const missing = new Set(WMF_F.missing(result));
  for (const control of form.querySelectorAll('[aria-invalid="true"]')) {
    const name = control.dataset.field || control.dataset.wmfProxy;
    if (!name || missing.has(name)) continue;
    const says = form.querySelector(`#${CSS.escape(`edit-${name}-says`)}`);
    if (says && !says.dataset.wmfRequired) continue;
    control.removeAttribute("aria-invalid");
    if (says) { says.textContent = ""; delete says.dataset.wmfRequired; }
  }
}

/**
 * Before the card's own save: a visible required field left empty is refused, naming it (ADR-313 §5.4). The card's
 * save then reads the controls, calculated and cleared values among them, and writes them through applyEdits.
 */
function wmfOnSubmit(event) {
  const form = event.target instanceof HTMLFormElement && event.target.matches("form.wmf-form") ? event.target : null;
  if (!form) return;
  const held = wmIdentified.get(form.dataset.editing);
  if (!held) return;
  const result = wmfApplyResult(form, held, false);
  const missing = WMF_F.missing(result);
  for (const node of form.querySelectorAll(".editfield .fieldsays:not(.wmf-exprsays)")) { node.textContent = ""; delete node.dataset.wmfRequired; }
  for (const control of form.querySelectorAll('[aria-invalid="true"]')) control.removeAttribute("aria-invalid");
  if (!missing.length) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  let first = null;
  for (const name of missing) {
    const state = result.byField.get(name);
    const label = state.element.label || state.field.alias || name;
    const says = form.querySelector(`#${CSS.escape(`edit-${name}-says`)}`);
    if (says) { says.textContent = `${label} is required.`; says.dataset.wmfRequired = "1"; }
    const control = form.querySelector(`[data-wmf-proxy="${CSS.escape(name)}"]`) || form.querySelector(`[data-field="${CSS.escape(name)}"]`);
    control?.setAttribute("aria-invalid", "true");
    if (!first && control && control.type !== "hidden" && !control.closest("[hidden]")) first = control;
  }
  const said = form.querySelector("#editSays");
  if (said) said.textContent = `Not saved: ${missing.length === 1 ? "a required field is" : `${missing.length} required fields are`} empty.`;
  const group = first && first.closest("details");
  if (group) group.open = true;
  (first || said)?.focus();
}

wm$("identify").addEventListener("input", wmfOnCardInput);
wm$("identify").addEventListener("change", wmfOnCardInput);
// Capture, so it runs before the card's own submit listener and can stop it.
document.addEventListener("submit", wmfOnSubmit, true);
