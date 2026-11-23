/*
 * Contingent values in the Map Viewer's attribute form and attribute table — ADR-312 §5.6.
 *
 * A layer whose document says `hasContingentValuesDefinition` has its `contingentValues` resource read once for the
 * life of the page. Then:
 *
 * - **Each coded-value list is narrowed** to the values the restrictive field groups allow, given the other fields'
 *   current values, as ArcGIS Pro's Attribute pane narrows it (owner decision 2026-11-23: *"Listeyi daralt, yine
 *   uyar"*). The value a control already holds and the value the feature held stay offered, labelled "(not listed)"
 *   or "(retired)", so narrowing never changes a value the editor did not choose.
 * - **A broken combination is said**, in the form's `#editCv` box and on the table's cells, and again with the save.
 *   NOTHING HERE STOPS A SAVE: the server writes the combination, as ArcGIS's does, and this only says so.
 *
 * The box's live part (`#editCvLead`) is rewritten only when the set of broken groups changes, so a screen reader is
 * not read a new sentence at every keystroke; what is allowed is said in a line beside it that is not live, and the
 * controls of a broken group are described by the box (design review 2026-11-22, finding 5).
 *
 * Uses contingent.js (the check) and webmap.js (wmRuntime, wmFetch, wmIdentified, wmReadControl, wmTable, wm$).
 * Classic script: every name starts with `cve`.
 */

/** Each layer's definition, as a promise of the decoded definition or null, kept for the life of the page. */
const cveHeld = new WeakMap();

/** The decoded definition of a layer, or null when its document says it has none or it cannot be read. */
function cveDefinition(layer) {
  const info = (wmRuntime.get(layer) || {}).info || {};
  if (!info.hasContingentValuesDefinition || !layer.url) return Promise.resolve(null);
  if (!cveHeld.has(layer)) cveHeld.set(layer, cveRead(layer, info).catch(() => null));
  return cveHeld.get(layer);
}

/**
 * Reads a layer's definition: the stored shape when the signed-in reader may read it — it says which values are
 * retired, so a feature that holds a retired value is not warned about and its value is labelled "(retired)", as the
 * owner decided on 2026-11-23 (re-review N1, D-480) — and otherwise the `contingentValues` resource, which serves no
 * retired value at all (ADR-312 §5.4). Groups the server does not serve (inert: a field of theirs is hidden) are left
 * out, as the resource leaves them out.
 */
async function cveRead(layer, info) {
  const name = info.catalogName || info.name;
  if (name && typeof wmMe === "object" && wmMe && wmMe.authenticated && wmToken) {
    try {
      const answer = await wmFetch(`/admin/layers/${encodeURIComponent(name)}/contingent-values`);
      if (answer && answer.definition) {
        const inert = new Set((answer.inert || []).map(n => String(n).toLowerCase()));
        const definition = cvFromStored(answer.definition);
        definition.groups = definition.groups.filter(g => !inert.has(String(g.name).toLowerCase()));
        definition.values = definition.values.filter(v => !inert.has(String(v.group).toLowerCase()));
        return definition;
      }
    } catch { /* not this reader's to read: the resource, which every reader of the layer may read */ }
  }
  return cvFromRest(await wmFetch(`${layer.url}/contingentValues?f=json`));
}

/** A feature's subtype code, or null on a layer without subtypes. */
function cveSubtype(info, attributes) {
  const field = info.subtypeField || info.typeIdField;
  if (!field) return null;
  const value = cvRead(attributes, field);
  return cvIsNull(value) ? null : Number(value);
}

/** The domain that governs a field for a subtype, from the layer's document: the subtype's own unless inherited. */
function cveDomain(info, name, subtype) {
  const field = (info.fields || []).find(f => cvSame(f.name, name)) || {};
  if (subtype !== null && subtype !== undefined) {
    const kind = (info.types || info.subtypes || []).find(t => Number(t.id ?? t.code) === Number(subtype));
    const own = kind && kind.domains && kind.domains[field.name || name];
    if (own && own.type && own.type !== "inherited") return own;
  }
  return field.domain || null;
}

/** How the layer names its fields and codes, for the sentences contingent.js writes. */
function cveLook(info, subtype) {
  return {
    alias: name => ((info.fields || []).find(f => cvSame(f.name, name)) || {}).alias || String(name),
    coded: name => (cveDomain(info, name, subtype) || {}).type === "codedValue",
    name: (name, code) => {
      const domain = cveDomain(info, name, subtype);
      const found = domain && (domain.codedValues || []).find(c => cvMatches({ kind: "CODED_VALUE", code: c.code }, code));
      return found ? String(found.name) : String(code);
    },
  };
}

/**
 * The sentence for a feature's values said with a save, or an empty string.
 *
 * @param {object} layer the web map's layer
 * @param {object} attributes the values as they will be saved
 * @param {object|null} original what the feature held, or null for a new one
 * @param {boolean} [saved] whether the sentence is said with the save rather than before it
 * @returns {Promise<string>}
 */
async function cveWarnFor(layer, attributes, original, saved = false) {
  const definition = await cveDefinition(layer);
  if (!definition) return "";
  const info = (wmRuntime.get(layer) || {}).info || {};
  const subtype = cveSubtype(info, attributes);
  return cvWarning(cvSentences(definition, attributes, subtype, original, cveLook(info, subtype)), saved);
}

/** The values the open form holds now: what the feature held, with each control's reading over it. */
function cveFormValues(form, held) {
  const info = (wmRuntime.get(held.layer) || {}).info || {};
  const values = { ...(held.feature.attributes || {}), ...(held.preset || {}) };
  for (const control of form.querySelectorAll("[data-field]")) {
    const field = (info.fields || []).find(f => f.name === control.dataset.field);
    if (!field) continue;
    const was = values[field.name] === undefined ? null : values[field.name];
    const read = wmReadControl(control, field, was);
    values[field.name] = read.same ? was : read.problem ? control.value : read.value;
  }
  return values;
}

/**
 * Narrows one coded-value list in place. The list it was drawn with is kept on the control, so a value narrowed away
 * comes back when the other fields change; the selection is kept, and the control is not replaced, so it keeps focus.
 *
 * In the form the feature's own saved value says "(saved value)" when it is not allowed, a retired one "(retired)",
 * and a hint under the list says why it is short (N2, N11). In the table nothing is said in the options — the cell's
 * mark says it (N2) — and `plain` is true.
 *
 * @returns {Array|null} the allowance the list was narrowed by
 */
function cveNarrowSelect(select, definition, attributes, subtype, original, look = null, plain = false) {
  if (!select._cveAll) {
    select._cveAll = [...select.options].filter(o => o.value !== "").map(o => ({ code: o.value, name: o.textContent }));
    select._cveBlank = [...select.options].find(o => o.value === "") || null;
    if (select._cveBlank) select._cveBlank = { name: select._cveBlank.textContent };
  }
  const field = select.dataset.field;
  const limits = cvAllowance(definition, field, attributes, subtype, original);
  const list = cvNarrow(select._cveAll, limits, [select.value], original ? cvRead(original, field) : null);
  const signature = list.map(o => `${o.code}:${o.state}`).join("|") + (plain ? "|plain" : "");
  if (select.dataset.cveShown !== signature) {
    select.dataset.cveShown = signature;
    const chosen = select.value;
    const options = [];
    if (select._cveBlank) options.push(new Option(select._cveBlank.name, ""));
    for (const o of list) {
      const option = new Option(plain ? String(o.name) : cvOptionLabel(o), String(o.code));
      if (o.state !== "ok") option.dataset.cvState = o.state;
      options.push(option);
    }
    select.replaceChildren(...options);
    select.value = chosen;
  }
  if (!plain && select.id) {
    const offered = list.filter(o => o.state === "ok" || o.state === "retired").length;
    const because = offered < select._cveAll.length && look ? cvNarrowedBecause(limits, attributes, look) : "";
    let hint = document.getElementById(`${select.id}-cv`);
    if (because && !hint) {
      select.insertAdjacentHTML("afterend", `<p class="cvhint" id="${wmEscape(select.id)}-cv"></p>`);
      hint = document.getElementById(`${select.id}-cv`);
    }
    if (hint && hint.textContent !== because) hint.textContent = because;
    cveDescribedBy(select, `${select.id}-cv`, !!because);
  }
  return limits;
}

/** Adds or removes one id in a control's `aria-describedby`, keeping the others. */
function cveDescribedBy(control, id, on) {
  const ids = (control.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean).filter(x => x !== id);
  if (on) ids.push(id);
  if (ids.length) control.setAttribute("aria-describedby", ids.join(" "));
  else control.removeAttribute("aria-describedby");
}

/**
 * Makes the form's warning box: never hidden (a live region that is hidden when its text arrives may not be read), a
 * live lead and a detail line that is not live. Done once per form.
 */
function cvePrepare(line) {
  if (line.dataset.cveReady) return;
  line.dataset.cveReady = "1";
  line.hidden = false;
  line.removeAttribute("aria-live");
  line.removeAttribute("role");
  line.innerHTML = `<span class="cvlead" id="editCvLead" role="status" aria-live="polite"></span>
    <span class="cvmore" id="editCvMore"></span>`;
}

/** The turn of the latest check, so a slow first read does not say something about values typed since. */
let cveTurn = 0;

/** Checks the open form, narrows its lists and says what it finds in `#editCv`; nothing when the layer has none. */
async function cveCheckForm(form) {
  if (!form) return;
  const held = wmIdentified.get(form.dataset.editing);
  const line = form.querySelector("#editCv");
  if (!held || !line) return;
  cvePrepare(line);
  const turn = ++cveTurn;
  const definition = await cveDefinition(held.layer);
  if (turn !== cveTurn || !form.isConnected || !definition) return;
  const info = (wmRuntime.get(held.layer) || {}).info || {};
  const values = cveFormValues(form, held);
  const original = held.isNew ? null : (held.feature.attributes || {});
  const subtype = cveSubtype(info, values);
  const said = cvSentences(definition, values, subtype, original, cveLook(info, subtype));

  // The live lead only when the set of broken groups changes; the detail beside it, which is not live, every time.
  const lead = line.querySelector("#editCvLead");
  if (line.dataset.cveKey !== said.key) {
    line.dataset.cveKey = said.key;
    lead.innerHTML = said.key ? `<b>Not a listed combination</b> ${wmEscape(said.lead)}.` : "";
  }
  const more = line.querySelector("#editCvMore");
  // Not live: what is allowed, or — when nothing is broken but the feature already holds a combination a reader of
  // the resource cannot call retired or unlisted — that it holds one (N1).
  const detail = said.key ? `${said.detail} You can still save.` : said.note;
  if (more.textContent !== detail) more.textContent = detail;
  line.classList.toggle("on", !!said.key);
  line.classList.toggle("held", !said.key && !!said.note);

  for (const control of form.querySelectorAll("[data-field]")) {
    cveDescribedBy(control, "editCv", said.fields.some(f => cvSame(f, control.dataset.field)));
  }
  const look = cveLook(info, subtype);
  for (const select of form.querySelectorAll("select[data-field]")) {
    cveNarrowSelect(select, definition, values, subtype, original, look);
  }
}

// ------------------------------------------------------------------------------------------------ the attribute table

/** A row as the table's status names it: its display value as the cell shows it once saved, and its place in the table. */
function cveRowName(info, attributes, index) {
  const place = `row ${(wmTable.page || 0) * (typeof WM_TABLE_PAGE === "number" ? WM_TABLE_PAGE : 0) + index + 1}`;
  const shown = info.displayField ? cvRead(attributes, info.displayField) : null;
  if (cvIsNull(shown)) return place.charAt(0).toUpperCase() + place.slice(1);
  return `${cveLook(info, cveSubtype(info, attributes)).name(info.displayField, shown)} ${place}`;
}

/** One table row's values now: what the feature holds, with each cell control's reading over it. */
function cveRowValues(info, feature, row) {
  const values = { ...(feature.attributes || {}) };
  for (const control of row.querySelectorAll("[data-cell]")) {
    const field = (info.fields || []).find(f => f.name === control.dataset.field);
    if (!field) continue;
    const was = values[field.name] === undefined ? null : values[field.name];
    const read = wmReadControl(control, field, was);
    values[field.name] = read.same ? was : read.problem ? control.value : read.value;
  }
  return values;
}

/**
 * Marks one row's cells whose group is broken (`data-cv`, a tooltip and a description) and narrows its lists. A row
 * the server just saved is checked against what it now holds, so a combination saved anyway stays marked.
 */
function cveCheckRow(definition, info, row) {
  const index = Number(row.dataset.row);
  const feature = wmTable.rows[index];
  if (!feature) return null;
  const original = feature.attributes || {};
  const values = cveRowValues(info, feature, row);
  const subtype = cveSubtype(info, values);
  const said = cvSentences(definition, values, subtype, original, cveLook(info, subtype));
  const text = said.key ? `Not a listed combination ${said.lead}. ${said.detail}` : "";
  for (const control of row.querySelectorAll("[data-cell]")) {
    const cell = control.closest("td");
    const on = !!said.key && said.fields.some(f => cvSame(f, control.dataset.field));
    if (on) {
      cell.dataset.cv = "1";
      cell.title = text;
      control.setAttribute("aria-description", text);
    } else if (cell.dataset.cv) {
      delete cell.dataset.cv;
      cell.removeAttribute("title");
      control.removeAttribute("aria-description");
    }
    if (control.tagName === "SELECT") cveNarrowSelect(control, definition, values, subtype, original, null, true);
  }
  return said.key ? { index, feature, said } : null;
}

/** Checks every row of the table in edit mode, or one. */
async function cveCheckTable(only = null) {
  const layer = wmTable.layer;
  if (!layer || !wmTable.editing) return;
  const definition = await cveDefinition(layer);
  if (!definition || wmTable.layer !== layer) return;
  const info = (wmRuntime.get(layer) || {}).info || {};
  const rows = only ? [only] : document.querySelectorAll("#tableGrid tr[data-row]");
  for (const row of rows) cveCheckRow(definition, info, row);
}

/**
 * The sentence for the table's save, naming each saved row whose values the layer does not list, or an empty string:
 * "2 hold combinations the layer does not list: Airbus row 1, Boeing row 4."
 *
 * @param {object} layer the table's layer
 * @param {Array} changes what wmTableChanges returned
 * @returns {Promise<string>}
 */
async function cveTableWarning(layer, changes) {
  const definition = await cveDefinition(layer);
  if (!definition) return "";
  const info = (wmRuntime.get(layer) || {}).info || {};
  const named = [];
  for (const change of changes) {
    const feature = wmTable.rows[change.index];
    if (!feature) continue;
    const before = feature.attributes || {};
    const after = { ...before, ...change.changed };
    if (cvViolations(definition, after, cveSubtype(info, after), before).some(b => !b.held)) named.push(cveRowName(info, after, change.index));
  }
  if (!named.length) return "";
  return `${named.length} ${named.length === 1 ? "holds a combination" : "hold combinations"} the layer does not list: ${cvJoin(named)}.`;
}

if (typeof wm$ === "function" && wm$("identify")) {
  for (const kind of ["input", "change"]) {
    wm$("identify").addEventListener(kind, event => {
      const form = event.target instanceof Element ? event.target.closest("form.editform") : null;
      if (form) cveCheckForm(form);
    });
  }
}

if (typeof wm$ === "function" && wm$("tablePanel") && wm$("tableGrid")) {
  // A cell typed in checks its row; a select fires both events, which is harmless — the second finds nothing new.
  for (const kind of ["input", "change"]) {
    wm$("tablePanel").addEventListener(kind, event => {
      const row = event.target instanceof Element && event.target.matches("[data-cell]") ? event.target.closest("tr[data-row]") : null;
      if (row) cveCheckTable(row);
    });
  }
  // Before a save the reason a cell is marked is said where the table's own words are, not only in a tooltip (N9).
  wm$("tablePanel").addEventListener("focusin", event => {
    const control = event.target instanceof Element && event.target.matches("[data-cell]") ? event.target : null;
    if (!control) return;
    const cell = control.closest("td");
    const status = wm$("tableStatus");
    if (cell && cell.dataset.cv && cell.title) {
      if (status.textContent !== cell.title) wmSayIn("tableStatus", cell.title);
      status.dataset.cveSaid = "1";
    } else if (status.dataset.cveSaid) {
      delete status.dataset.cveSaid;
      wmSayIn("tableStatus", wmTable.countSaid || "");
    }
  });
  // The table is drawn whole by webmap.js (wmLoadTable); each drawing is checked. Only the grid's own children are
  // watched, so narrowing a list (a change deep inside) does not start another check.
  new MutationObserver(() => { cveCheckTable(); }).observe(wm$("tableGrid"), { childList: true });
}
