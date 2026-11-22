/*
 * Contingent values — ADR-312. The check Studio's editors run before a save, and the two readings of a definition it
 * runs on: the ArcGIS resource a layer serves (`…/FeatureServer/{id}/contingentValues`) and the stored shape Studio's
 * admin route sends (grapy's, ADR-235). Pure functions, no DOM, so Node's test runner loads this file as it is
 * (tests/Graticula.Console.Tests/contingent.test.js).
 *
 * The check WARNS and never blocks: the server does not refuse an invalid combination, which is ArcGIS's behaviour
 * (ADR-312 §5.7), and Studio's editor saves after saying so (§5.6).
 *
 * Classic script: every name here is global, so each starts with `cv`.
 */

/** The type codes the resource prints, by index (ADR-312 §4). */
const CV_TYPE_CODES = ["Unknown", "Any", "Null", "Code", "Range"];

/** The kinds of a stored entry, as grapy writes them. */
const CV_KINDS = ["ANY", "NULL", "CODED_VALUE", "RANGE"];

/**
 * A definition read from the ArcGIS resource, in the stored shape's words.
 *
 * INFERRED (ADR-312 §5.9): a text code is an index into its dictionary's `entries`, named by the subtype's `domains`
 * entry for the field or by the field itself; a range takes two slots; Any and Null take none.
 *
 * @param {object} resource the `contingentValues` answer, or one entry of `queryContingentValues` with the answer's
 *   `stringDicts` passed as `dicts`
 * @param {Array} [dicts] the answer's `stringDicts`, when `resource` is a definition rather than the whole answer
 * @returns {{groups: Array, values: Array}} groups `{name, restrictive, fields}` and values
 *   `{id, group, subtype, retired, entries: [{field, kind, code, min, max}]}`
 */
function cvFromRest(resource, dicts) {
  const definition = (resource && (resource.contingentValuesDefinition || resource)) || {};
  const strings = new Map(((dicts || (resource && resource.stringDicts)) || []).map(d => [d.domain, d.entries || []]));
  const groups = [];
  const values = [];
  for (const group of definition.fieldGroups || []) {
    const fields = (group.fields || []).map(f => f.name);
    const texts = new Set((group.fields || []).filter(f => f.fieldType === "esriFieldTypeString").map(f => f.name));
    groups.push({ name: group.name, restrictive: group.restrictive !== false, fields });
    for (const kind of group.subTypes || []) {
      const subtype = definition.hasSubType ? kind.code : null;
      for (const value of kind.contingentValues || []) {
        const slots = value.values || [];
        let at = 0;
        const entries = fields.map((field, i) => {
          const type = CV_TYPE_CODES[(value.types || [])[i]] || "Unknown";
          if (type === "Any") return { field, kind: "ANY" };
          if (type === "Null") return { field, kind: "NULL" };
          if (type === "Range") {
            const entry = { field, kind: "RANGE", min: slots[at], max: slots[at + 1] };
            at += 2;
            return entry;
          }
          if (type === "Code") {
            const slot = slots[at++];
            if (texts.has(field) && typeof slot === "number") {
              const dictionary = strings.get((kind.domains || {})[field]) || strings.get(field) || [];
              return { field, kind: "CODED_VALUE", code: dictionary[slot] };
            }
            return { field, kind: "CODED_VALUE", code: slot };
          }
          return { field, kind: "ANY" };
        });
        values.push({ id: value.id, group: group.name, subtype, retired: false, entries });
      }
    }
  }
  return { groups, values };
}

/**
 * A definition read from the stored shape Studio's admin route answers (grapy's: `[field, type, value]` entries).
 *
 * @param {object} stored `{fieldGroups, contingentValues}`
 * @returns {{groups: Array, values: Array}} the shape {@link cvFromRest} returns
 */
function cvFromStored(stored) {
  const groups = ((stored && stored.fieldGroups) || []).map(g => ({
    name: g.name, restrictive: g.restrictive !== false, fields: [...(g.fields || [])],
  }));
  const values = ((stored && stored.contingentValues) || []).map(v => ({
    id: v.id,
    group: v.fieldGroup,
    subtype: v.subtype === undefined ? null : v.subtype,
    retired: !!v.retired,
    entries: (v.entries || []).map(([field, kind, value]) => kind === "RANGE"
      ? { field, kind, min: (value || [])[0], max: (value || [])[1] }
      : kind === "CODED_VALUE" ? { field, kind, code: value } : { field, kind }),
  }));
  return { groups, values };
}

/**
 * The stored shape of a definition, which Studio's admin route takes back.
 *
 * @param {{groups: Array, values: Array}} definition
 * @returns {object} `{fieldGroups, contingentValues}`
 */
function cvToStored(definition) {
  return {
    fieldGroups: definition.groups.map(g => ({ name: g.name, restrictive: g.restrictive !== false, fields: [...g.fields] })),
    contingentValues: definition.values.map(v => {
      const out = {
        fieldGroup: v.group,
        subtype: v.subtype === undefined ? null : v.subtype,
        retired: !!v.retired,
        entries: v.entries.map(e => e.kind === "RANGE" ? [e.field, "RANGE", [e.min, e.max]]
          : e.kind === "CODED_VALUE" ? [e.field, "CODED_VALUE", e.code] : [e.field, e.kind, null]),
      };
      if (v.id) out.id = v.id;
      return out;
    }),
  };
}

/** Whether a value is no value: null, undefined or an empty string. */
function cvIsNull(value) {
  return value === null || value === undefined || value === "";
}

/**
 * Whether one entry allows one value. A number and the text of the same number are one value, as a form's control
 * and the server's answer write them differently.
 */
function cvMatches(entry, value) {
  if (!entry || entry.kind === "ANY") return true;
  if (entry.kind === "NULL") return cvIsNull(value);
  if (cvIsNull(value)) return false;
  if (entry.kind === "RANGE") {
    const n = Number(value);
    return Number.isFinite(n) && n >= Number(entry.min) && n <= Number(entry.max);
  }
  if (typeof entry.code === "number" || typeof value === "number") {
    return Number(entry.code) === Number(value) && Number.isFinite(Number(value));
  }
  return String(entry.code) === String(value);
}

/** Reads a value from an attribute bag without regard to the case of its name, as a column name is matched. */
function cvRead(attributes, field) {
  if (!attributes) return null;
  if (Object.prototype.hasOwnProperty.call(attributes, field)) return attributes[field];
  const lower = field.toLowerCase();
  for (const key of Object.keys(attributes)) if (key.toLowerCase() === lower) return attributes[key];
  return null;
}

/**
 * The restrictive field groups a feature's values break — ADR-312 §5.6, grapy's rule (ADR-235 §5.8 h): a group with no
 * values for the feature's subtype does not restrict, a feature whose group fields are all null is not restricted, and
 * a retired value allows only a feature that already held it.
 *
 * @param {{groups: Array, values: Array}} definition
 * @param {object} attributes the feature's values as they will be saved
 * @param {number|null} subtype the feature's subtype code, or null on a layer without subtypes
 * @param {object|null} [original] what the feature held before, or null for a new one
 * @returns {Array<{group: string, fields: string[], shown: string}>} one per broken group, empty when none
 */
function cvViolations(definition, attributes, subtype, original = null) {
  const broken = [];
  if (!definition) return broken;
  for (const group of definition.groups || []) {
    if (!group.restrictive) continue;
    const mine = (definition.values || []).filter(v =>
      String(v.group).toLowerCase() === String(group.name).toLowerCase()
      && (v.subtype === null || v.subtype === undefined ? subtype === null || subtype === undefined : Number(v.subtype) === Number(subtype)));
    if (!mine.length) continue;
    const current = group.fields.map(f => cvRead(attributes, f));
    if (current.every(cvIsNull)) continue;
    const unchanged = !!original && group.fields.every(f => String(cvRead(original, f)) === String(cvRead(attributes, f)));
    const allowed = mine.some(v => (!v.retired || unchanged)
      && group.fields.every((f, i) => cvMatches(v.entries.find(e => e.field.toLowerCase() === f.toLowerCase()), current[i])));
    if (!allowed) {
      broken.push({
        group: group.name,
        fields: [...group.fields],
        shown: group.fields.map((f, i) => `${f} = ${cvIsNull(current[i]) ? "null" : current[i]}`).join(", "),
      });
    }
  }
  return broken;
}

/**
 * The sentence a broken group is said with. It names the values and says the save goes ahead, because it does.
 *
 * @param {Array} broken what {@link cvViolations} returned
 * @param {boolean} [saved] whether the save has been made, which changes the last sentence's tense
 * @returns {string} empty when nothing is broken
 */
function cvWarning(broken, saved = false) {
  if (!broken || !broken.length) return "";
  return broken.map(b => `${b.shown} is not a combination the field group “${b.group}” allows.`).join(" ")
    + (saved
      ? " Saved anyway: the layer's contingent values guide editing and do not refuse it."
      : " It can still be saved; the layer's contingent values guide editing and do not refuse it.");
}

if (typeof module !== "undefined" && module.exports) {
  module.exports = { cvFromRest, cvFromStored, cvToStored, cvMatches, cvViolations, cvWarning, CV_KINDS, CV_TYPE_CODES };
}
