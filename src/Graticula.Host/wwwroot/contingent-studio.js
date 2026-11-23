/*
 * Studio: a hosted layer's field groups and contingent values, under its Fields — ADR-312 §5.6.
 *
 * Read from and written to `/admin/layers/{name}/contingent-values`, whole: the page holds an unsaved copy, and one Save
 * sends it. A refusal is the server's own sentence, with its rows named as the screen names them, in `#cvsSays`, a
 * polite live region.
 *
 * <b>A group is held by a key, not by its name.</b> Each group gets a client key when it is read or added, and each
 * value names its group by that key; the name is only joined back in `cvToStored`. Renaming a group as it is typed
 * once moved every value of another group whose name the typing passed through (design review 2026-11-22, finding 1).
 *
 * <b>Nothing invalid is sent.</b> A group with no name, a name used twice, a group with no fields, a range that is not
 * two numbers or whose minimum is above its maximum, and a value that repeats an earlier one are each said under the
 * control that holds them, marked `aria-invalid`, and keep Save disabled (findings 1, 3, 8).
 *
 * <b>Re-rendering keeps focus.</b> Every control carries a `data-cvs-key`; a redraw puts focus back on the control
 * with the same key, or on the nearest thing the reader would look at next. Typing changes the copy and the messages,
 * and does not redraw, so a keystroke never moves the caret.
 *
 * <b>Unsaved changes are kept, not lost</b> (finding 12): the copy is held per layer for as long as the tab is open, as
 * the layer settings pages hold theirs (console.js `unsaved`), and closing the tab with a change asks first, as the
 * Map Viewer does.
 *
 * Uses console.js's `api`, `h` and `$`, and contingent.js. Classic script: every name starts with `cvs`.
 */

/** The page's state: the layer, what the server said about its fields, the unsaved copy, and the open group's key. */
let cvsState = null;

/** Each layer's unsaved copy, kept across leaving and coming back while the tab is open (finding 12). */
const cvsKept = new Map();

/** The last client key handed out. */
let cvsSeq = 0;

/** The kinds an entry can be, as a person reads them. */
const CVS_KIND_LABELS = { ANY: "Any", NULL: "Null", CODED_VALUE: "Value", RANGE: "Range" };

/** A new client key for a group. */
function cvsNewKey() {
  cvsSeq += 1;
  return `k${cvsSeq}`;
}

/**
 * Draws the section into `host` and reads the layer's definition.
 *
 * @param {string} layer the layer's name, as `/admin/layers` knows it
 * @param {HTMLElement} host where the section goes
 */
async function cvsMount(layer, host) {
  if (!host || !layer) return;
  host.innerHTML = `<section class="page on cvs" aria-labelledby="cvsTitle">
      <h4 id="cvsTitle" tabindex="-1">Field groups and contingent values</h4>
      <p class="hint">A <b>field group</b> is a set of fields whose values depend on each other, and its
        <b>contingent values</b> are the combinations they may hold together — for example which models go with
        which make. While somebody edits, Field Maps, ArcGIS Pro and Studio's Map Viewer offer only the values that
        fit what the other fields hold, and warn about any other. <b>The server does not refuse an edit that breaks
        them</b>, as ArcGIS's does not.</p>
      <dl class="cvslegend">
        <div><dt>Restrictive</dt><dd>editors are offered only the listed combinations, and warned about any other.</dd></div>
        <div><dt>Retired</dt><dd>kept for existing features, not offered for new edits.</dd></div>
        <div><dt>Any</dt><dd>every value.</dd></div>
        <div><dt>Null</dt><dd>empty is allowed.</dd></div>
        <div><dt>Range</dt><dd>between a minimum and a maximum, both included.</dd></div>
      </dl>
      <div id="cvsBody"><p class="hint">Reading the field groups…</p></div>
      <p class="hint" id="cvsSays" role="status" aria-live="polite"></p>
    </section>`;
  try {
    const answer = await api(`/admin/layers/${encodeURIComponent(layer)}/contingent-values`);
    const kept = cvsKept.get(layer);
    cvsTake(layer, answer);
    if (kept && kept.dirty && cvsState.writable) {
      cvsState.copy = kept.copy;
      cvsState.open = kept.open;
      cvsState.dirty = true;
      cvsDraw();
      cvsSay("Your unsaved changes to the field groups were kept. Save them, or discard them.");
    } else {
      cvsDraw();
    }
  } catch (e) {
    $("cvsBody").innerHTML = "";
    cvsSay(`The field groups could not be read: ${e.message || e}`, true);
  }
}

/** Takes the admin route's answer as the page's state, keeping which group is open when it is still there. */
function cvsTake(layer, answer) {
  const openName = cvsState && cvsState.layer === layer && cvsState.open
    ? (cvsGroupByKey(cvsState.open) || {}).name : null;
  const copy = cvFromStored(answer.definition || {});
  for (const g of copy.groups) g.key = cvsNewKey();
  for (const v of copy.values) v.gkey = (copy.groups.find(g => cvSame(g.name, v.group)) || {}).key || null;
  const reopened = openName ? copy.groups.find(g => g.name === openName) : null;
  cvsState = {
    layer,
    writable: answer.writable !== false,
    why: answer.why || "",
    fields: answer.fields || [],
    subtypes: answer.subtypes || null,
    inert: answer.inert || [],
    copy,
    // One group is opened by default, so its values are in view without a click (finding 6).
    open: reopened ? reopened.key : copy.groups.length === 1 ? copy.groups[0].key : null,
    dirty: false,
  };
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

/** A field as a person reads it. */
function cvsAlias(name) {
  const f = cvsField(name);
  return (f && f.alias) || name;
}

/** A group by its key, or null. */
function cvsGroupByKey(key) {
  return (cvsState.copy.groups || []).find(g => g.key === key) || null;
}

/** A group's values, in the editor's order, each with its index in the copy. */
function cvsGroupValues(group) {
  return cvsState.copy.values.map((v, vi) => ({ v, vi })).filter(({ v }) => v.gkey === group.key);
}

/** A value's name as the screen shows it: its place in its group. */
function cvsValueName(v) {
  const group = cvsGroupByKey(v.gkey);
  if (!group) return "a value";
  const n = cvsGroupValues(group).findIndex(x => x.v === v) + 1;
  return `value ${n} of ${group.name || "the unnamed group"}`;
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

/** An inline message's element, under the control it is about; empty until there is something to say. */
function cvsMsg(id) {
  return `<span class="cvsmsg bad-inline" id="cvsMsg-${h(id)}" data-cvs-msg="${h(id)}"></span>`;
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
  const open = s.open ? cvsGroupByKey(s.open) : null;

  body.innerHTML = `
    ${ro ? `<p class="hint">${h(s.why || "This layer's contingent values are not changed here.")}</p>` : ""}
    ${s.copy.groups.length ? `<table class="cvsgroups">
      <caption class="sr-only">Field groups of ${h(s.layer)}</caption>
      <thead><tr><th scope="col">Name</th><th scope="col">Restrictive</th><th scope="col">Fields</th>
        <th scope="col">Values</th>${ro ? "" : `<th scope="col"><span class="sr-only">Remove</span></th>`}</tr></thead>
      <tbody>${s.copy.groups.map((g, i) => cvsGroupRow(g, i, eligible, ro)).join("")}</tbody>
    </table>` : `<p class="hint">This layer has no field groups.</p>`}
    ${ro ? "" : `<div class="row"><button type="button" class="tiny" data-cvs-key="addGroup" data-cvs-act="addGroup">Add field group</button></div>`}
    ${open ? cvsValuesMarkup(open, ro) : ""}
    ${s.inert.length ? `<p class="hint">Not served: ${s.inert.map(n => `<code>${h(n)}</code>`).join(", ")} ${
      s.inert.length === 1 ? "names a field" : "name fields"} this layer no longer shows. Remove ${
      s.inert.length === 1 ? "it" : "them"}, or show the field again.</p>` : ""}
    ${ro ? "" : `<div class="row cvssave">
      <button type="button" class="primary" data-cvs-key="save" data-cvs-act="save" disabled>Save field groups</button>
      ${s.dirty ? `<button type="button" data-cvs-key="discard" data-cvs-act="discard">Discard changes</button>` : ""}
      <span class="hint" id="cvsDirty">${s.dirty ? "Unsaved changes." : ""}</span>
      <span class="bad-inline" id="cvsBlocked"></span></div>`}`;

  cvsCheck();
  const target = keep && body.querySelector(`[data-cvs-key="${CSS.escape(keep)}"]`);
  if (target && !target.disabled) target.focus();
  else if (keep) (body.querySelector(`[data-cvs-key="addGroup"]`) || $("cvsTitle"))?.focus?.();
}

/** One group's row: its name, restrictive, its fields as checkboxes, its values button and Remove. */
function cvsGroupRow(group, i, eligible, ro) {
  const count = cvsGroupValues(group).length;
  const open = cvsState.open === group.key;
  const offered = [...eligible.map(f => f.name), ...group.fields.filter(n => !eligible.some(f => f.name === n))];
  const named = group.name || `group ${i + 1}`;
  return `<tr>
    <td data-l="Name"><span class="cvscell"><input type="text" maxlength="255" aria-label="Name of field group ${i + 1}"
      data-cvs-key="g${i}name" data-cvs-group="${i}" data-cvs-prop="name" value="${h(group.name)}"
      aria-describedby="cvsMsg-g${i}name"${ro ? " readonly" : ""}>${cvsMsg(`g${i}name`)}</span></td>
    <td data-l="Restrictive"><input type="checkbox" aria-label="Field group ${h(named)} is restrictive" data-cvs-key="g${i}restrictive"
      data-cvs-group="${i}" data-cvs-prop="restrictive"${group.restrictive ? " checked" : ""}${ro ? " disabled" : ""}></td>
    <td data-l="Fields"><fieldset class="cvsfields" aria-describedby="cvsMsg-g${i}fields"><legend class="sr-only">Fields of ${h(named)}</legend>${
      offered.map(name => {
        const f = cvsField(name);
        return `<label><input type="checkbox" data-cvs-key="g${i}f${h(name)}" data-cvs-group="${i}" data-cvs-field="${h(name)}"${
          group.fields.includes(name) ? " checked" : ""}${ro ? " disabled" : ""}> ${h(f ? f.alias || f.name : name)}</label>`;
      }).join(" ")}</fieldset>${cvsMsg(`g${i}fields`)}</td>
    <td data-l="Values"><button type="button" class="tiny cvsopen" data-cvs-key="g${i}open" data-cvs-act="open" data-cvs-group="${i}"
      aria-expanded="${open}" aria-controls="cvsValues">${ro ? "Show" : "Edit"} ${count} ${count === 1 ? "value" : "values"}</button></td>
    ${ro ? "" : `<td class="cvsact"><button type="button" class="tiny ghost" data-cvs-key="g${i}remove" data-cvs-act="removeGroup"
      data-cvs-group="${i}" aria-label="Remove field group ${h(named)}">Remove</button></td>`}
  </tr>`;
}

/** The open group's contingent values: a row each, a control per field, its subtype, retired, and Remove. */
function cvsValuesMarkup(group, ro) {
  const s = cvsState;
  const rows = cvsGroupValues(group);
  const typed = !!s.subtypes;
  return `<div id="cvsValues" class="cvsvalues">
    <h5 id="cvsValuesTitle" tabindex="-1">Contingent values of ${h(group.name || "the unnamed group")}</h5>
    ${group.fields.length ? "" : `<p class="hint">Tick the fields of this group first.</p>`}
    ${rows.length ? `<table>
      <caption class="sr-only">Contingent values of ${h(group.name)}</caption>
      <thead><tr><th scope="col">Value</th>${typed ? `<th scope="col">Subtype</th>` : ""}${
        group.fields.map(f => `<th scope="col">${h(cvsAlias(f))}</th>`).join("")}
        <th scope="col">Retired</th>${ro ? "" : `<th scope="col"><span class="sr-only">Remove</span></th>`}</tr></thead>
      <tbody>${rows.map(({ v, vi }, n) => `<tr data-cvs-row="${vi}">
        <td data-l="Value" class="cvsid"><b>${n + 1}</b> <span class="hint">${v.id ? `id ${h(v.id)}` : "new"}</span>${cvsMsg(`v${vi}`)}</td>
        ${typed ? `<td data-l="Subtype"><select data-cvs-key="v${vi}subtype" data-cvs-value="${vi}" data-cvs-prop="subtype"
          aria-label="Subtype of value ${n + 1}"${ro ? " disabled" : ""}>${(s.subtypes.types || []).map(t =>
            `<option value="${h(t.code)}"${Number(t.code) === Number(v.subtype) ? " selected" : ""}>${h(t.name)}</option>`).join("")}
          </select></td>` : ""}
        ${group.fields.map(f => `<td data-l="${h(cvsAlias(f))}">${cvsEntryControl(v, vi, n + 1, f, ro)}</td>`).join("")}
        <td data-l="Retired" class="cvsret"><input type="checkbox" data-cvs-key="v${vi}retired" data-cvs-value="${vi}" data-cvs-prop="retired"
          aria-label="Value ${n + 1} is retired"${v.retired ? " checked" : ""}${ro ? " disabled" : ""}></td>
        ${ro ? "" : `<td class="cvsact"><button type="button" class="tiny ghost" data-cvs-key="v${vi}remove" data-cvs-act="removeValue"
          data-cvs-value="${vi}" aria-label="Remove value ${n + 1}">Remove</button></td>`}
      </tr>`).join("")}</tbody></table>` : `<p class="hint">No contingent values yet.</p>`}
    ${ro || !group.fields.length ? "" : `<button type="button" class="tiny" data-cvs-key="addValue" data-cvs-act="addValue">Add value</button>`}
  </div>`;
}

/** One field's part of one value: what kind, and the code or the range. */
function cvsEntryControl(v, vi, n, field, ro) {
  const entry = v.entries.find(e => e.field === field) || { field, kind: "ANY" };
  const label = cvsAlias(field);
  const kinds = ["ANY", ...((cvsField(field) || {}).nullable === false ? [] : ["NULL"]), "CODED_VALUE",
    ...(cvsRanged(field) ? ["RANGE"] : [])];
  const domain = cvsDomain(field, v.subtype);
  const coded = domain && domain.type === "codedValue";
  const key = `v${vi}e${field}`;
  const msg = `cvsMsg-${key}`;
  let more = "";
  if (entry.kind === "CODED_VALUE") {
    more = coded
      ? `<select data-cvs-key="${h(key)}code" data-cvs-value="${vi}" data-cvs-entry="${h(field)}" data-cvs-prop="code"
          aria-label="${h(label)} of value ${n}"${ro ? " disabled" : ""}>
          ${(domain.codedValues || []).map(c => `<option value="${h(c.code)}"${String(c.code) === String(entry.code) ? " selected" : ""}>${
            h(c.name)}</option>`).join("")}</select>`
      : `<input type="text" data-cvs-key="${h(key)}code" data-cvs-value="${vi}" data-cvs-entry="${h(field)}" data-cvs-prop="code"
          aria-label="${h(label)} of value ${n}" aria-describedby="${h(msg)}" value="${h(entry.code ?? "")}"${ro ? " readonly" : ""}>`;
  } else if (entry.kind === "RANGE") {
    more = `<span class="cvsrange"><input type="text" inputmode="decimal" class="cvsnum" data-cvs-key="${h(key)}min" data-cvs-value="${vi}"
        data-cvs-entry="${h(field)}" data-cvs-prop="min" aria-label="${h(label)} minimum of value ${n}" aria-describedby="${h(msg)}"
        value="${h(entry.minText ?? entry.min ?? "")}"${ro ? " readonly" : ""}>
      <span aria-hidden="true">to</span> <input type="text" inputmode="decimal" class="cvsnum" data-cvs-key="${h(key)}max" data-cvs-value="${vi}"
        data-cvs-entry="${h(field)}" data-cvs-prop="max" aria-label="${h(label)} maximum of value ${n}" aria-describedby="${h(msg)}"
        value="${h(entry.maxText ?? entry.max ?? "")}"${ro ? " readonly" : ""}></span>`;
  }
  return `<span class="cvscell"><span class="cvsentry"><select data-cvs-key="${h(key)}kind" data-cvs-value="${vi}" data-cvs-entry="${h(field)}" data-cvs-prop="kind"
      aria-label="What ${h(label)} allows in value ${n}" aria-describedby="cvsMsg-v${vi} ${h(msg)}"${ro ? " disabled" : ""}>
      ${kinds.map(k => `<option value="${k}"${k === entry.kind ? " selected" : ""}>${CVS_KIND_LABELS[k]}</option>`).join("")}
    </select> ${more}</span>${cvsMsg(key)}</span>`;
}

/** A typed value as it is stored: a number for a numeric field, otherwise the text. */
function cvsTyped(field, text) {
  if (text === "" || text === null || text === undefined) return null;
  if (cvsNumeric(field)) {
    const n = cvParseNumber(text);
    return n === null ? text : n;
  }
  // A coded value list's code on a numeric domain comes back from the select as text.
  const domain = cvsDomain(field, null);
  if (domain && domain.type === "codedValue" && typeof ((domain.codedValues || [])[0] || {}).code === "number") return Number(text);
  return text;
}

/**
 * What stops a save, each with the message element it is said in and the controls it marks.
 *
 * @returns {Array<{msg: string, keys: string[], text: string}>}
 */
function cvsProblems() {
  const s = cvsState;
  const out = [];
  s.copy.groups.forEach((g, i) => {
    const name = String(g.name || "").trim();
    if (!name) out.push({ msg: `g${i}name`, keys: [`g${i}name`], text: "Give the field group a name." });
    else if (s.copy.groups.some((o, j) => j < i && cvSame(String(o.name || "").trim(), name))) {
      out.push({ msg: `g${i}name`, keys: [`g${i}name`], text: `Another field group is already named “${name}”. A name is used once, whatever its case.` });
    }
    if (!g.fields.length) out.push({ msg: `g${i}fields`, keys: [], text: "Tick at least one field." });
    const values = cvsGroupValues(g);
    const repeats = cvDuplicates(values.map(x => x.v));
    for (const { v, vi } of values) {
      if (repeats.has(v)) {
        out.push({ msg: `v${vi}`, keys: v.entries.map(e => `v${vi}e${e.field}kind`), text: `Same as value ${repeats.get(v)}. Remove one.` });
      }
      for (const e of v.entries) {
        const key = `v${vi}e${e.field}`;
        if (e.kind === "RANGE") {
          const min = cvParseNumber(e.minText ?? e.min);
          const max = cvParseNumber(e.maxText ?? e.max);
          const unclear = [e.minText ?? e.min, e.maxText ?? e.max].find(t => cvAmbiguousNumber(t));
          if (unclear !== undefined) {
            out.push({ msg: key, keys: [...(cvAmbiguousNumber(e.minText ?? e.min) ? [`${key}min`] : []),
              ...(cvAmbiguousNumber(e.maxText ?? e.max) ? [`${key}max`] : [])], text: cvAmbiguousSaid(unclear) });
          } else if (min === null || max === null) {
            const which = min === null && max === null ? "the minimum and the maximum" : min === null ? "the minimum" : "the maximum";
            out.push({ msg: key, keys: [...(min === null ? [`${key}min`] : []), ...(max === null ? [`${key}max`] : [])],
              text: `Type a number for ${which}, such as 10 or 10.5.` });
          } else if (min > max) {
            out.push({ msg: key, keys: [`${key}min`, `${key}max`], text: `The minimum, ${min}, is more than the maximum, ${max}.` });
          }
        } else if (e.kind === "CODED_VALUE" && cvIsNull(e.code)) {
          out.push({ msg: key, keys: [`${key}code`], text: "Type a value, or choose Any." });
        } else if (e.kind === "CODED_VALUE" && cvsNumeric(e.field) && typeof e.code !== "number") {
          out.push({ msg: key, keys: [`${key}code`], text: `${cvsAlias(e.field)} takes a number.` });
        }
      }
    }
  });
  return out;
}

/**
 * Says every problem under its control, marks the controls, and offers Save only when there is a change and nothing
 * stops it. Writes only what changed, so it can run at every keystroke.
 */
function cvsCheck() {
  const body = $("cvsBody");
  if (!body || !cvsState) return [];
  const problems = cvsProblems();
  const said = new Map();
  for (const p of problems) if (!said.has(p.msg)) said.set(p.msg, p.text);
  for (const el of body.querySelectorAll("[data-cvs-msg]")) {
    const text = said.get(el.getAttribute("data-cvs-msg")) || "";
    if (el.textContent !== text) el.textContent = text;
  }
  const bad = new Set(problems.flatMap(p => p.keys));
  for (const el of body.querySelectorAll("[data-cvs-key]")) {
    const on = bad.has(el.getAttribute("data-cvs-key"));
    if (on) el.setAttribute("aria-invalid", "true");
    else if (el.hasAttribute("aria-invalid")) el.removeAttribute("aria-invalid");
  }
  const save = body.querySelector('[data-cvs-act="save"]');
  if (save) save.disabled = !cvsState.dirty || problems.length > 0;
  const blocked = $("cvsBlocked");
  const n = problems.length;
  if (blocked) {
    const text = cvsState.dirty && n ? `Not ready to save: ${n} ${n === 1 ? "problem is" : "problems are"} marked above.` : "";
    if (blocked.textContent !== text) blocked.textContent = text;
  }
  // `#cvsBlocked` is not live, so a Save that goes grey is silent; the change of state — not each count — is said once.
  const stopped = cvsState.dirty && n > 0;
  if (cvsState.stopped !== undefined && stopped !== cvsState.stopped && cvsState.dirty) {
    cvsSay(stopped ? `Save is unavailable: ${problems[0].text}` : "Ready to save.", stopped);
  }
  cvsState.stopped = stopped;
  return problems;
}

/** Marks the copy changed, keeps it for this layer, and offers Save, without redrawing unless asked. */
function cvsChanged(redraw, focusKey) {
  cvsState.dirty = true;
  cvsKept.set(cvsState.layer, cvsState);
  if (redraw) { cvsDraw(focusKey); return; }
  // Not redrawn for a keystroke: "Unsaved changes." and Discard are put in where they stand, so the caret stays.
  const dirty = $("cvsDirty");
  if (dirty && !dirty.textContent) dirty.textContent = "Unsaved changes.";
  const save = document.querySelector('#cvsBody [data-cvs-act="save"]');
  if (save && !document.querySelector('#cvsBody [data-cvs-act="discard"]')) {
    save.insertAdjacentHTML("afterend", ` <button type="button" data-cvs-key="discard" data-cvs-act="discard">Discard changes</button>`);
  }
  cvsCheck();
}

/** The entries a value has for its group's fields, in order, keeping what it already said about each. */
function cvsAlign(value, group) {
  value.entries = group.fields.map(f => value.entries.find(e => e.field === f) || { field: f, kind: "ANY" });
}

/**
 * Puts a group's new name into every label that carries it, in place — its heading and caption when open, Remove's and
 * Restrictive's labels, its fields' legend — so nothing reads the old name until a redraw (N10). Not a redraw: one on
 * `change` would replace the button a reader was pressing when the name lost focus, and lose that click.
 */
function cvsRenamed(group, i) {
  const named = group.name || `group ${i + 1}`;
  const body = $("cvsBody");
  if (!body) return;
  const set = (el, attr, text) => { if (el && el.getAttribute(attr) !== text) el.setAttribute(attr, text); };
  set(body.querySelector(`[data-cvs-key="g${i}restrictive"]`), "aria-label", `Field group ${named} is restrictive`);
  set(body.querySelector(`[data-cvs-key="g${i}remove"]`), "aria-label", `Remove field group ${named}`);
  const legend = body.querySelector(`[data-cvs-key="g${i}restrictive"]`)?.closest("tr")?.querySelector("fieldset legend");
  if (legend) legend.textContent = `Fields of ${named}`;
  if (cvsState.open === group.key) {
    const title = `Contingent values of ${group.name || "the unnamed group"}`;
    if ($("cvsValuesTitle")) $("cvsValuesTitle").textContent = title;
    const caption = body.querySelector("#cvsValues caption");
    if (caption) caption.textContent = title;
  }
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
      // The values follow the group by its key, so a name typed through another group's name takes nothing from it.
      group.name = t.value.trim();
      cvsRenamed(group, Number(t.getAttribute("data-cvs-group")));
      cvsChanged(false, key);
      return;
    }
    if (prop === "restrictive") { group.restrictive = t.checked; cvsChanged(false, key); return; }
    const field = t.getAttribute("data-cvs-field");
    if (field) {
      group.fields = t.checked ? [...group.fields, field] : group.fields.filter(f => f !== field);
      for (const v of s.copy.values) if (v.gkey === group.key) cvsAlign(v, group);
      cvsChanged(true, key);
    }
    return;
  }

  if (t.hasAttribute("data-cvs-value")) {
    const value = s.copy.values[Number(t.getAttribute("data-cvs-value"))];
    if (!value) return;
    const prop = t.getAttribute("data-cvs-prop");
    if (prop === "retired") { value.retired = t.checked; cvsChanged(false, key); return; }
    if (prop === "subtype") { value.subtype = Number(t.value); cvsChanged(true, key); return; }
    const field = t.getAttribute("data-cvs-entry");
    const entry = value.entries.find(e => e.field === field);
    if (!entry) return;
    if (prop === "kind") {
      entry.kind = t.value;
      delete entry.code; delete entry.min; delete entry.max; delete entry.minText; delete entry.maxText;
      if (entry.kind === "CODED_VALUE") {
        const domain = cvsDomain(field, value.subtype);
        if (domain && domain.type === "codedValue" && (domain.codedValues || []).length) entry.code = domain.codedValues[0].code;
      }
      cvsChanged(true, key);
      return;
    }
    if (prop === "code") entry.code = cvsTyped(field, t.value);
    if (prop === "min" || prop === "max") {
      // The typed text is kept as typed, so a redraw shows "10,5" rather than what it was read as, or "NaN".
      entry[`${prop}Text`] = t.value;
      entry[prop] = cvParseNumber(t.value);
    }
    cvsChanged(false, key);
  }
}

/** Handles a button: add, open, remove, discard, save. */
async function cvsClick(event) {
  const t = event.target instanceof Element ? event.target.closest("[data-cvs-act]") : null;
  if (!t || !cvsState) return;
  const s = cvsState;
  const act = t.getAttribute("data-cvs-act");

  if (act === "addGroup") {
    let n = s.copy.groups.length + 1;
    while (s.copy.groups.some(g => cvSame(g.name, `group_${n}`))) n++;
    const group = { key: cvsNewKey(), name: `group_${n}`, restrictive: true, fields: [] };
    s.copy.groups.push(group);
    s.open = group.key;
    cvsChanged(true, `g${s.copy.groups.length - 1}name`);
    cvsSay(`Field group group_${n} added. Name it and tick its fields.`);
    return;
  }
  if (act === "open") {
    const i = Number(t.getAttribute("data-cvs-group"));
    const group = s.copy.groups[i];
    s.open = group && s.open !== group.key ? group.key : null;
    cvsDraw(`g${i}open`);
    return;
  }
  if (act === "removeGroup") {
    const i = Number(t.getAttribute("data-cvs-group"));
    const group = s.copy.groups[i];
    const held = cvsGroupValues(group).length;
    const named = group.name || `group ${i + 1}`;
    if (held && !confirm(`Remove the field group “${named}” and its ${held} contingent ${held === 1 ? "value" : "values"}?`)) return;
    s.copy.groups.splice(i, 1);
    s.copy.values = s.copy.values.filter(v => v.gkey !== group.key);
    if (s.open === group.key) s.open = null;
    cvsChanged(true, "addGroup");
    cvsSay(`Field group ${named} removed. Save to keep the change.`);
    return;
  }
  if (act === "addValue") {
    const group = cvsGroupByKey(s.open);
    if (!group) return;
    const subtype = s.subtypes ? ((s.subtypes.types || [])[0] || {}).code ?? null : null;
    s.copy.values.push({ id: 0, gkey: group.key, group: group.name, subtype, retired: false,
      entries: group.fields.map(f => ({ field: f, kind: "ANY" })) });
    const vi = s.copy.values.length - 1;
    cvsChanged(true, `v${vi}e${group.fields[0]}kind`);
    cvsSay(`Value ${cvsGroupValues(group).length} added: every field allows Any until you choose.`);
    return;
  }
  if (act === "removeValue") {
    const vi = Number(t.getAttribute("data-cvs-value"));
    const name = cvsValueName(s.copy.values[vi]);
    s.copy.values.splice(vi, 1);
    cvsChanged(true, "addValue");
    cvsSay(`${name.charAt(0).toUpperCase()}${name.slice(1)} removed. Save to keep the change.`);
    return;
  }
  if (act === "discard") {
    if (!confirm("Discard your changes to the field groups?")) return;
    cvsKept.delete(s.layer);
    const host = $("cvsBody") && $("cvsBody").closest(".cvs") && $("cvsBody").closest(".cvs").parentElement;
    await cvsMount(s.layer, host);
    cvsSay("Changes discarded.");
    $("cvsTitle")?.focus();
    return;
  }
  if (act === "save") await cvsSave(t);
}

/**
 * Sends the whole definition; the server's answer becomes the page, and its refusal is said with the rows named as
 * the screen names them. A new value is sent with the id the server would give it — one more than the highest — so a
 * refusal that names an id can be said as "value 3 of make_model".
 */
async function cvsSave(button) {
  const s = cvsState;
  const problems = cvsCheck();
  if (problems.length) {
    const first = $("cvsBody").querySelector('[aria-invalid="true"]');
    cvsSay(`Not saved: ${problems[0].text}`, true);
    first?.focus();
    return;
  }
  const stored = cvToStored(s.copy);
  const names = new Map();
  let next = Math.max(0, ...stored.contingentValues.map(v => Number(v.id) || 0));
  stored.contingentValues.forEach((v, i) => {
    if (!v.id) v.id = ++next;
    const name = cvsValueName(s.copy.values[i]);
    names.set(v.id, name.charAt(0).toUpperCase() + name.slice(1));
  });
  button.disabled = true;
  cvsSay("Saving the field groups…");
  try {
    const answer = await api(`/admin/layers/${encodeURIComponent(s.layer)}/contingent-values`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ definition: stored }),
    });
    cvsKept.delete(s.layer);
    cvsTake(s.layer, answer);
    cvsDraw("addGroup");
    const groups = cvsState.copy.groups.length;
    const values = cvsState.copy.values.length;
    cvsSay(`Saved: ${groups} field ${groups === 1 ? "group" : "groups"}, ${values} contingent ${values === 1 ? "value" : "values"}.`);
  } catch (e) {
    button.disabled = false;
    cvsSay(`Not saved: ${cvRenameRefusal(e.message || String(e), names, cvsAlias)}`, true);
    // Focus stays where the reader pressed, rather than falling to the page (finding 3).
    button.focus();
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
  // Closing or reloading the tab with a change not saved asks first, as the Map Viewer does (finding 12).
  window.addEventListener("beforeunload", event => {
    if (![...cvsKept.values()].some(k => k.dirty)) return undefined;
    event.preventDefault();
    event.returnValue = "";
    return "";
  });
}
