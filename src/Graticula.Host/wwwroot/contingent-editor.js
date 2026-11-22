/*
 * Contingent values in the Map Viewer's attribute form and attribute table — ADR-312 §5.6.
 *
 * A layer whose document says `hasContingentValuesDefinition` has its `contingentValues` resource read once for the
 * life of the page. While somebody types, the form checks the feature's restrictive field groups and says a broken
 * combination in `#editCv`, a polite live region; when they save, the form and the table say it again beside "saved".
 * NOTHING HERE STOPS A SAVE: the server writes the combination, as ArcGIS's does, and this only says so.
 *
 * Uses contingent.js (the check) and webmap.js (wmRuntime, wmFetch, wmIdentified, wmReadControl, wm$). Classic script:
 * every name starts with `cve`.
 */

/** Each layer's definition, as a promise of the decoded definition or null, kept for the life of the page. */
const cveHeld = new WeakMap();

/** The decoded definition of a layer, or null when its document says it has none or it cannot be read. */
function cveDefinition(layer) {
  const info = (wmRuntime.get(layer) || {}).info || {};
  if (!info.hasContingentValuesDefinition || !layer.url) return Promise.resolve(null);
  if (!cveHeld.has(layer)) {
    cveHeld.set(layer, wmFetch(`${layer.url}/contingentValues?f=json`).then(answer => cvFromRest(answer)).catch(() => null));
  }
  return cveHeld.get(layer);
}

/** A feature's subtype code, or null on a layer without subtypes. */
function cveSubtype(info, attributes) {
  const field = info.subtypeField || info.typeIdField;
  if (!field) return null;
  const value = cvRead(attributes, field);
  return cvIsNull(value) ? null : Number(value);
}

/**
 * The sentence for a feature's values, or an empty string.
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
  return cvWarning(cvViolations(definition, attributes, cveSubtype(info, attributes), original), saved);
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

/** The turn of the latest check, so a slow first read does not say something about values typed since. */
let cveTurn = 0;

/** Checks the open form and says what it finds in `#editCv`; says nothing when the layer has no definition. */
async function cveCheckForm(form) {
  if (!form) return;
  const held = wmIdentified.get(form.dataset.editing);
  const line = form.querySelector("#editCv");
  if (!held || !line) return;
  const turn = ++cveTurn;
  const said = await cveWarnFor(held.layer, cveFormValues(form, held), held.isNew ? null : (held.feature.attributes || {}));
  if (turn !== cveTurn || !form.isConnected) return;
  // Only written when it changes, so a screen reader is not told the same sentence at every keystroke.
  if (line.textContent !== said) line.textContent = said;
  line.hidden = !said;
}

/**
 * The sentence for the table's changed rows, naming each row whose values break a group, or an empty string.
 *
 * @param {object} layer the table's layer
 * @param {Array} changes what wmTableChanges returned
 * @returns {Promise<string>}
 */
async function cveTableWarning(layer, changes) {
  const definition = await cveDefinition(layer);
  if (!definition) return "";
  const info = (wmRuntime.get(layer) || {}).info || {};
  const said = [];
  for (const change of changes) {
    const feature = wmTable.rows[change.index];
    if (!feature) continue;
    const before = feature.attributes || {};
    const after = { ...before, ...change.changed };
    const broken = cvViolations(definition, after, cveSubtype(info, after), before);
    if (broken.length) said.push(`${wmTable.named ? wmTable.named(feature) : `Row ${change.index + 1}`}: ${broken.map(b => `${b.shown} is not a combination “${b.group}” allows`).join("; ")}`);
  }
  return said.length ? `Saved anyway — ${said.join(". ")}. The layer's contingent values guide editing and do not refuse it.` : "";
}

if (typeof wm$ === "function" && wm$("identify")) {
  for (const kind of ["input", "change"]) {
    wm$("identify").addEventListener(kind, event => {
      const form = event.target instanceof Element ? event.target.closest("form.editform") : null;
      if (form) cveCheckForm(form);
    });
  }
}
