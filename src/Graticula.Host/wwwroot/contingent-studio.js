/*
 * Studio: a hosted layer's field groups and contingent values, under its Fields — ADR-312 §5.6.
 *
 * Read from and written to `/admin/layers/{name}/contingent-values`, whole: the page holds an unsaved copy, and one Save
 * sends it. A refusal is the server's own sentence in `#cvsSays`, a polite live region.
 *
 * <b>Re-rendering keeps focus.</b> Every control carries a `data-cvs-key`; a redraw puts focus back on the control
 * with the same key, or on the nearest thing the reader would look at next (the new row, the open group's heading).
 * Typing into a control changes the copy and does not redraw, so a keystroke never moves the caret.
 *
 * Uses console.js's `api`, `h` and `$`, and contingent.js. Classic script: every name starts with `cvs`.
 */

/** The page's state: the layer, what the server said about its fields, the unsaved copy, and the open group. */
let cvsState = null;

/** The kinds an entry can be, as a person reads them. */
const CVS_KIND_LABELS = { ANY: "Any", NULL: "Null", CODED_VALUE: "Value", RANGE: "Range" };

/**
 * Draws the section into `host` and reads the layer's definition.
 *
 * @param {string} layer the layer's name, as `/admin/layers` knows it
 * @param {HTMLElement} host where the section goes
 */
async function cvsMount(layer, host) {
  if (!host || !layer) return;
  host.innerHTML = `<section class="page on cvs" aria-labelledby="cvsTitle">
      <h4 id="cvsTitle">Field groups and contingent values</h4>
      <p class="hint">A <b>field group</b> is a set of fields whose values depend on each other, and its
        <b>contingent values</b> are the combinations they may hold together — for example which models go with
        which make. Field Maps, ArcGIS Pro and Studio's own editor offer or check these combinations while somebody
        edits. <b>The server does not refuse an edit that breaks them</b>, as ArcGIS's does not.</p>
      <div id="cvsBody"><p class="hint">Reading the field groups…</p></div>
      <p class="hint" id="cvsSays" role="status" aria-live="polite"></p>
    </section>`;
  try {
    const answer = await api(`/admin/layers/${encodeURIComponent(layer)}/contingent-values`);
    cvsTake(layer, answer);
    cvsDraw();
  } catch (e) {
    $("cvsBody").innerHTML = "";
    cvsSay(`The field groups could not be read: ${e.message || e}`, true);
  }
}

/** Takes the admin route's answer as the page's state, keeping which group is open when it is still there. */
function cvsTake(layer, answer) {
  const open = cvsState && cvsState.layer === layer && cvsState.open !== null
    ? (cvsState.copy.groups[cvsState.open] || {}).name : null;
  const copy = cvFromStored(answer.definition || {});
  cvsState = {
    layer,
    writable: answer.writable !== false,
    why: answer.why || "",
    fields: answer.fields || [],
    subtypes: answer.subtypes || null,
    inert: answer.inert || [],
    copy,
    open: open === null ? null : Math.max(-1, copy.groups.findIndex(g => g.name === open)),
    dirty: false,
  };
  if (cvsState.open === -1) cvsState.open = null;
}

/** Says something in the section's live region. */
function cvsSay(text, bad = false) {
  const line = $("cvsSays");
  if (!line) return;
  line.textContent = text;
  line.classList.toggle("bad-inline", bad);
}

/** A field of the layer by name, or null. */
function cvsField(name) {
  return (cvsState.fields || []).find(f => f.name === name) || null;
}

/** The domain that governs a field for a subtype: the subtype's own when it has one, the field's otherwise. */
function cvsDomain(name, subtype) {
  if (cvsState.subtypes && subtype !== null && subtype !== undefined) {
    const kind = (cvsState.subtypes.types || []).find(t => Number(t.code) === Number(subtype));
    if (kind && kind.domains && kind.domains[name]) return kind.domains[name];
  }
  return (cvsField(name) || {}).domain || null;
}

/** Whether a field's type holds numbers or dates, which is where a range is offered. */
function cvsRanged(name) {
  return /SmallInteger|Integer|BigInteger|Single|Double|Date/.test((cvsField(name) || {}).type || "");
}

/** Whether a field's type holds numbers, so a typed value is sent as one. */
function cvsNumeric(name) {
  return /SmallInteger|Integer|BigInteger|Single|Double|Date|OID/.test((cvsField(name) || {}).type || "");
}

/** Redraws the section, putting focus back where it was or on `focusKey`. */
function cvsDraw(focusKey) {
  const body = $("cvsBody");
  if (!body || !cvsState) return;
  const active = document.activeElement;
  const keep = focusKey || (active && body.contains(active) ? active.getAttribute("data-cvs-key") : null);
  const s = cvsState;
  const ro = !s.writable;
  const eligible = s.fields.filter(f => f.eligible);

  body.innerHTML = `
    ${ro ? `<p class="hint">${h(s.why || "This layer's contingent values are not changed here.")}</p>` : ""}
    ${s.copy.groups.length ? `<table class="cvsgroups">
      <caption class="sr-only">Field groups of ${h(s.layer)}</caption>
      <thead><tr><th scope="col">Name</th><th scope="col">Restrictive</th><th scope="col">Fields</th>
        <th scope="col">Values</th>${ro ? "" : `<th scope="col"><span class="sr-only">Remove</span></th>`}</tr></thead>
      <tbody>${s.copy.groups.map((g, i) => cvsGroupRow(g, i, eligible, ro)).join("")}</tbody>
    </table>` : `<p class="hint">This layer has no field groups.</p>`}
    ${ro ? "" : `<div class="row"><button type="button" class="tiny" data-cvs-key="addGroup" data-cvs-act="addGroup">Add field group</button></div>`}
    ${s.open !== null && s.copy.groups[s.open] ? cvsValuesMarkup(s.open, ro) : ""}
    ${s.inert.length ? `<p class="hint">Not served: ${s.inert.map(n => `<code>${h(n)}</code>`).join(", ")} ${
      s.inert.length === 1 ? "names a field" : "name fields"} this layer no longer shows. Remove ${
      s.inert.length === 1 ? "it" : "them"}, or show the field again.</p>` : ""}
    ${ro ? "" : `<div class="row" style="margin-top:10px">
      <button type="button" data-cvs-key="save" data-cvs-act="save"${s.dirty ? "" : " disabled"}>Save field groups</button>
      ${s.dirty ? `<span class="hint" id="cvsDirty">Unsaved changes.</span>` : ""}</div>`}`;

  const target = keep && body.querySelector(`[data-cvs-key="${CSS.escape(keep)}"]`);
  if (target && !target.disabled) target.focus();
  else if (keep) (body.querySelector(`[data-cvs-key="addGroup"]`) || $("cvsTitle"))?.focus?.();
}

/** One group's row: its name, restrictive, its fields as checkboxes, its values button and Remove. */
function cvsGroupRow(group, i, eligible, ro) {
  const count = cvsState.copy.values.filter(v => v.group.toLowerCase() === group.name.toLowerCase()).length;
  const open = cvsState.open === i;
  const offered = [...eligible.map(f => f.name), ...group.fields.filter(n => !eligible.some(f => f.name === n))];
  return `<tr>
    <td><input type="text" maxlength="255" aria-label="Name of field group ${i + 1}" data-cvs-key="g${i}name"
      data-cvs-group="${i}" data-cvs-prop="name" value="${h(group.name)}"${ro ? " readonly" : ""}></td>
    <td><input type="checkbox" aria-label="Field group ${h(group.name)} is restrictive" data-cvs-key="g${i}restrictive"
      data-cvs-group="${i}" data-cvs-prop="restrictive"${group.restrictive ? " checked" : ""}${ro ? " disabled" : ""}></td>
    <td><fieldset class="cvsfields"><legend class="sr-only">Fields of ${h(group.name)}</legend>${offered.map(name => {
      const f = cvsField(name);
      return `<label><input type="checkbox" data-cvs-key="g${i}f${h(name)}" data-cvs-group="${i}" data-cvs-field="${h(name)}"${
        group.fields.includes(name) ? " checked" : ""}${ro ? " disabled" : ""}> ${h(f ? f.alias || f.name : name)}</label>`;
    }).join(" ")}</fieldset></td>
    <td><button type="button" class="tiny ghost" data-cvs-key="g${i}open" data-cvs-act="open" data-cvs-group="${i}"
      aria-expanded="${open}" aria-controls="cvsValues">${count} ${count === 1 ? "value" : "values"}</button></td>
    ${ro ? "" : `<td><button type="button" class="tiny ghost" data-cvs-key="g${i}remove" data-cvs-act="removeGroup"
      data-cvs-group="${i}" aria-label="Remove field group ${h(group.name)}">Remove</button></td>`}
  </tr>`;
}

/** The open group's contingent values: a row each, a control per field, its subtype, retired, and Remove. */
function cvsValuesMarkup(gi, ro) {
  const s = cvsState;
  const group = s.copy.groups[gi];
  const rows = s.copy.values.map((v, vi) => ({ v, vi })).filter(({ v }) => v.group.toLowerCase() === group.name.toLowerCase());
  const typed = !!s.subtypes;
  return `<div id="cvsValues" class="cvsvalues">
    <h5 id="cvsValuesTitle" tabindex="-1">Contingent values of ${h(group.name)}</h5>
    ${group.fields.length ? "" : `<p class="hint">Tick the fields of this group first.</p>`}
    ${rows.length ? `<table>
      <caption class="sr-only">Contingent values of ${h(group.name)}</caption>
      <thead><tr><th scope="col">Id</th>${typed ? `<th scope="col">Subtype</th>` : ""}${
        group.fields.map(f => `<th scope="col">${h((cvsField(f) || {}).alias || f)}</th>`).join("")}
        <th scope="col">Retired</th>${ro ? "" : `<th scope="col"><span class="sr-only">Remove</span></th>`}</tr></thead>
      <tbody>${rows.map(({ v, vi }) => `<tr>
        <td>${v.id ? h(v.id) : `<span class="hint">new</span>`}</td>
        ${typed ? `<td><select data-cvs-key="v${vi}subtype" data-cvs-value="${vi}" data-cvs-prop="subtype"
          aria-label="Subtype of value ${v.id || "new"}"${ro ? " disabled" : ""}>${(s.subtypes.types || []).map(t =>
            `<option value="${h(t.code)}"${Number(t.code) === Number(v.subtype) ? " selected" : ""}>${h(t.name)}</option>`).join("")}
          </select></td>` : ""}
        ${group.fields.map(f => `<td>${cvsEntryControl(v, vi, f, ro)}</td>`).join("")}
        <td><input type="checkbox" data-cvs-key="v${vi}retired" data-cvs-value="${vi}" data-cvs-prop="retired"
          aria-label="Value ${v.id || "new"} is retired"${v.retired ? " checked" : ""}${ro ? " disabled" : ""}></td>
        ${ro ? "" : `<td><button type="button" class="tiny ghost" data-cvs-key="v${vi}remove" data-cvs-act="removeValue"
          data-cvs-value="${vi}" aria-label="Remove value ${v.id || "new"}">Remove</button></td>`}
      </tr>`).join("")}</tbody></table>` : `<p class="hint">No contingent values yet.</p>`}
    ${ro || !group.fields.length ? "" : `<button type="button" class="tiny" data-cvs-key="addValue" data-cvs-act="addValue">Add value</button>`}
  </div>`;
}

/** One field's part of one value: what kind, and the code or the range. */
function cvsEntryControl(v, vi, field, ro) {
  const entry = v.entries.find(e => e.field === field) || { field, kind: "ANY" };
  const label = (cvsField(field) || {}).alias || field;
  const id = v.id || "new";
  const kinds = ["ANY", ...((cvsField(field) || {}).nullable === false ? [] : ["NULL"]), "CODED_VALUE",
    ...(cvsRanged(field) ? ["RANGE"] : [])];
  const domain = cvsDomain(field, v.subtype);
  const coded = domain && domain.type === "codedValue";
  const key = `v${vi}e${field}`;
  let more = "";
  if (entry.kind === "CODED_VALUE") {
    more = coded
      ? `<select data-cvs-key="${h(key)}code" data-cvs-value="${vi}" data-cvs-entry="${h(field)}" data-cvs-prop="code"
          aria-label="${h(label)} value of ${id}"${ro ? " disabled" : ""}>
          ${(domain.codedValues || []).map(c => `<option value="${h(c.code)}"${String(c.code) === String(entry.code) ? " selected" : ""}>${
            h(c.name)}</option>`).join("")}</select>`
      : `<input type="text" data-cvs-key="${h(key)}code" data-cvs-value="${vi}" data-cvs-entry="${h(field)}" data-cvs-prop="code"
          aria-label="${h(label)} value of ${id}" value="${h(entry.code ?? "")}"${ro ? " readonly" : ""}>`;
  } else if (entry.kind === "RANGE") {
    more = `<input type="text" inputmode="decimal" class="cvsnum" data-cvs-key="${h(key)}min" data-cvs-value="${vi}"
        data-cvs-entry="${h(field)}" data-cvs-prop="min" aria-label="${h(label)} minimum of ${id}" value="${h(entry.min ?? "")}"${ro ? " readonly" : ""}>
      to <input type="text" inputmode="decimal" class="cvsnum" data-cvs-key="${h(key)}max" data-cvs-value="${vi}"
        data-cvs-entry="${h(field)}" data-cvs-prop="max" aria-label="${h(label)} maximum of ${id}" value="${h(entry.max ?? "")}"${ro ? " readonly" : ""}>`;
  }
  return `<select data-cvs-key="${h(key)}kind" data-cvs-value="${vi}" data-cvs-entry="${h(field)}" data-cvs-prop="kind"
      aria-label="What ${h(label)} allows in value ${id}"${ro ? " disabled" : ""}>
      ${kinds.map(k => `<option value="${k}"${k === entry.kind ? " selected" : ""}>${CVS_KIND_LABELS[k]}</option>`).join("")}
    </select> ${more}`;
}

/** A typed value as it is stored: a number for a numeric field, otherwise the text. */
function cvsTyped(field, text) {
  if (text === "" || text === null || text === undefined) return null;
  if (cvsNumeric(field)) {
    const n = Number(text);
    return Number.isFinite(n) ? n : text;
  }
  // A coded value list's code on a numeric domain comes back from the select as text.
  const domain = cvsDomain(field, null);
  if (domain && domain.type === "codedValue" && typeof ((domain.codedValues || [])[0] || {}).code === "number") return Number(text);
  return text;
}

/** Marks the copy changed and offers Save, without redrawing unless asked. */
function cvsChanged(redraw, focusKey) {
  cvsState.dirty = true;
  if (redraw) { cvsDraw(focusKey); return; }
  // Not redrawn for a keystroke: the Save button is offered where it stands, so the caret stays where it was.
  const save = document.querySelector('#cvsBody [data-cvs-act="save"]');
  if (save) save.disabled = false;
  if (save && !$("cvsDirty")) save.insertAdjacentHTML("afterend", ` <span class="hint" id="cvsDirty">Unsaved changes.</span>`);
}

/** The entries a value has for its group's fields, in order, keeping what it already said about each. */
function cvsAlign(value, group) {
  value.entries = group.fields.map(f => value.entries.find(e => e.field === f) || { field: f, kind: "ANY" });
}

/** Handles a typed or ticked control: changes the copy, and redraws only when the shape of the page changes. */
function cvsInput(event) {
  const t = event.target;
  if (!(t instanceof Element) || !cvsState) return;
  const s = cvsState;
  const key = t.getAttribute("data-cvs-key");

  if (t.hasAttribute("data-cvs-group") && !t.hasAttribute("data-cvs-act")) {
    const group = s.copy.groups[Number(t.getAttribute("data-cvs-group"))];
    if (!group) return;
    const prop = t.getAttribute("data-cvs-prop");
    if (prop === "name") {
      const before = group.name;
      group.name = t.value.trim();
      for (const v of s.copy.values) if (v.group === before) v.group = group.name;
      cvsChanged(false);
      return;
    }
    if (prop === "restrictive") { group.restrictive = t.checked; cvsChanged(false); return; }
    const field = t.getAttribute("data-cvs-field");
    if (field) {
      group.fields = t.checked ? [...group.fields, field] : group.fields.filter(f => f !== field);
      for (const v of s.copy.values) if (v.group === group.name) cvsAlign(v, group);
      cvsChanged(true, key);
    }
    return;
  }

  if (t.hasAttribute("data-cvs-value")) {
    const value = s.copy.values[Number(t.getAttribute("data-cvs-value"))];
    if (!value) return;
    const prop = t.getAttribute("data-cvs-prop");
    if (prop === "retired") { value.retired = t.checked; cvsChanged(false); return; }
    if (prop === "subtype") { value.subtype = Number(t.value); cvsChanged(true, key); return; }
    const field = t.getAttribute("data-cvs-entry");
    const entry = value.entries.find(e => e.field === field);
    if (!entry) return;
    if (prop === "kind") {
      entry.kind = t.value;
      delete entry.code; delete entry.min; delete entry.max;
      if (entry.kind === "CODED_VALUE") {
        const domain = cvsDomain(field, value.subtype);
        if (domain && domain.type === "codedValue" && (domain.codedValues || []).length) entry.code = domain.codedValues[0].code;
      }
      cvsChanged(true, key);
      return;
    }
    if (prop === "code") entry.code = cvsTyped(field, t.value);
    if (prop === "min" || prop === "max") entry[prop] = t.value === "" ? null : Number(t.value);
    cvsChanged(false);
  }
}

/** Handles a button: add, open, remove, save. */
async function cvsClick(event) {
  const t = event.target instanceof Element ? event.target.closest("[data-cvs-act]") : null;
  if (!t || !cvsState) return;
  const s = cvsState;
  const act = t.getAttribute("data-cvs-act");

  if (act === "addGroup") {
    let n = s.copy.groups.length + 1;
    while (s.copy.groups.some(g => g.name.toLowerCase() === `group_${n}`)) n++;
    s.copy.groups.push({ name: `group_${n}`, restrictive: true, fields: [] });
    s.open = s.copy.groups.length - 1;
    cvsChanged(true, `g${s.open}name`);
    cvsSay(`Field group group_${n} added. Name it and tick its fields.`);
    return;
  }
  if (act === "open") {
    const i = Number(t.getAttribute("data-cvs-group"));
    s.open = s.open === i ? null : i;
    cvsDraw(`g${i}open`);
    return;
  }
  if (act === "removeGroup") {
    const i = Number(t.getAttribute("data-cvs-group"));
    const group = s.copy.groups[i];
    const held = s.copy.values.filter(v => v.group.toLowerCase() === group.name.toLowerCase()).length;
    if (held && !confirm(`Remove the field group “${group.name}” and its ${held} contingent ${held === 1 ? "value" : "values"}?`)) return;
    s.copy.groups.splice(i, 1);
    s.copy.values = s.copy.values.filter(v => v.group.toLowerCase() !== group.name.toLowerCase());
    s.open = null;
    cvsChanged(true, "addGroup");
    cvsSay(`Field group ${group.name} removed. Save to keep the change.`);
    return;
  }
  if (act === "addValue") {
    const group = s.copy.groups[s.open];
    const subtype = s.subtypes ? ((s.subtypes.types || [])[0] || {}).code ?? null : null;
    s.copy.values.push({ id: 0, group: group.name, subtype, retired: false, entries: group.fields.map(f => ({ field: f, kind: "ANY" })) });
    const vi = s.copy.values.length - 1;
    cvsChanged(true, `v${vi}e${group.fields[0]}kind`);
    cvsSay("Value added: every field allows Any until you choose.");
    return;
  }
  if (act === "removeValue") {
    const vi = Number(t.getAttribute("data-cvs-value"));
    s.copy.values.splice(vi, 1);
    cvsChanged(true, "addValue");
    cvsSay("Value removed. Save to keep the change.");
    return;
  }
  if (act === "save") await cvsSave(t);
}

/** Sends the whole definition; the server's answer becomes the page, and its refusal is said. */
async function cvsSave(button) {
  const s = cvsState;
  button.disabled = true;
  cvsSay("Saving the field groups…");
  try {
    const answer = await api(`/admin/layers/${encodeURIComponent(s.layer)}/contingent-values`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ definition: cvToStored(s.copy) }),
    });
    cvsTake(s.layer, answer);
    cvsDraw("save");
    const groups = cvsState.copy.groups.length;
    const values = cvsState.copy.values.length;
    cvsSay(`Saved: ${groups} field ${groups === 1 ? "group" : "groups"}, ${values} contingent ${values === 1 ? "value" : "values"}.`);
  } catch (e) {
    button.disabled = false;
    cvsSay(`Not saved: ${e.message || e}`, true);
  }
}

if (typeof document !== "undefined") {
  // One listener for typing, ticking and choosing: a checkbox and a select fire `input` as well as `change`, and
  // listening to both would apply a tick twice.
  document.addEventListener("input", event => {
    if (event.target instanceof Element && event.target.closest("#cvsBody")) cvsInput(event);
  });
  document.addEventListener("click", event => {
    if (event.target instanceof Element && event.target.closest("#cvsBody")) cvsClick(event);
  });
}
