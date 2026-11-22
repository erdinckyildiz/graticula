/*
 * Contingent values — ADR-312. The check Studio's editors run before a save, the narrowing of a coded-value list to the
 * values the field groups allow, the sentences both are said with, and the two readings of a definition they run on:
 * the ArcGIS resource a layer serves (`…/FeatureServer/{id}/contingentValues`) and the stored shape Studio's admin route
 * sends (grapy's, ADR-235). Pure functions, no DOM, so Node's test runner loads this file as it is
 * (tests/js/contingent.test.js).
 *
 * The check WARNS and never blocks: the server does not refuse an invalid combination, which is ArcGIS's behaviour
 * (ADR-312 §5.7), and Studio's editor saves after saying so (§5.6). The list is NARROWED, as ArcGIS Pro's Attribute
 * pane narrows it (owner decision 2026-11-23, §5.6): a value the groups do not allow is not offered, except the value the
 * control already holds and the value the feature held, which stay and are labelled.
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
 * A value names its group by the group's `key` when it carries a `gkey` (Studio's editor gives each group one, so a
 * rename never moves another group's values — the design review of 2026-11-22, finding 1); otherwise by `group`.
 *
 * @param {{groups: Array, values: Array}} definition
 * @returns {object} `{fieldGroups, contingentValues}`
 */
function cvToStored(definition) {
  const byKey = new Map(definition.groups.filter(g => g.key).map(g => [g.key, g.name]));
  return {
    fieldGroups: definition.groups.map(g => ({ name: g.name, restrictive: g.restrictive !== false, fields: [...g.fields] })),
    contingentValues: definition.values.map(v => {
      const out = {
        fieldGroup: v.gkey && byKey.has(v.gkey) ? byKey.get(v.gkey) : v.group,
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

/** Two names as a column name is compared: without regard to case. */
function cvSame(a, b) {
  return String(a).toLowerCase() === String(b).toLowerCase();
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
  const lower = String(field).toLowerCase();
  for (const key of Object.keys(attributes)) if (key.toLowerCase() === lower) return attributes[key];
  return null;
}

/** A value's entry for one field, or undefined (which allows anything). */
function cvEntry(value, field) {
  return (value.entries || []).find(e => cvSame(e.field, field));
}

/** A group's values for a subtype, retired ones included. */
function cvValuesFor(definition, group, subtype) {
  return ((definition && definition.values) || []).filter(v => cvSame(v.group, group.name)
    && (v.subtype === null || v.subtype === undefined
      ? subtype === null || subtype === undefined
      : Number(v.subtype) === Number(subtype)));
}

/** Whether a value allows every field of its group as `attributes` holds them. */
function cvFits(value, group, attributes) {
  return group.fields.every(f => cvMatches(cvEntry(value, f), cvRead(attributes, f)));
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
 * @returns {Array<{group: string, fields: string[], retired: boolean}>} one per broken group, empty when none;
 *   `retired` when the combination is one a retired value lists, so it is broken only because it is new or changed
 */
function cvViolations(definition, attributes, subtype, original = null) {
  const broken = [];
  if (!definition) return broken;
  for (const group of definition.groups || []) {
    if (!group.restrictive) continue;
    const mine = cvValuesFor(definition, group, subtype);
    if (!mine.length) continue;
    if (group.fields.every(f => cvIsNull(cvRead(attributes, f)))) continue;
    const unchanged = !!original && group.fields.every(f => String(cvRead(original, f)) === String(cvRead(attributes, f)));
    const allowed = mine.some(v => (!v.retired || unchanged) && cvFits(v, group, attributes));
    if (!allowed) {
      broken.push({
        group: group.name,
        fields: [...group.fields],
        retired: mine.some(v => v.retired && cvFits(v, group, attributes)),
      });
    }
  }
  return broken;
}

/**
 * What the restrictive field groups allow one field to hold, given the other fields' current values — the narrowing
 * ArcGIS Pro's Attribute pane applies to a coded-value list (owner decision 2026-11-23, ADR-312 §5.6).
 *
 * A field another field has no value for is not narrowed by it. INFERRED: a group whose other fields already hold a
 * combination no value lists does not narrow this field — the warning says that combination is not listed, and a list
 * narrowed to nothing would leave the editor no way out but the field they did not choose.
 *
 * @param {{groups: Array, values: Array}} definition
 * @param {string} field the field whose list is narrowed
 * @param {object} attributes the feature's values now
 * @param {number|null} subtype the feature's subtype code, or null
 * @param {object|null} [original] what the feature held, or null for a new one
 * @returns {Array<{group: string, live: Array, kept: Array}>|null} per narrowing group the entries this field may take
 *   from live values and from the retired value the feature already held; null when nothing narrows the field
 */
function cvAllowance(definition, field, attributes, subtype, original = null) {
  const limits = [];
  for (const group of (definition && definition.groups) || []) {
    if (!group.restrictive) continue;
    const at = group.fields.findIndex(f => cvSame(f, field));
    if (at < 0) continue;
    const mine = cvValuesFor(definition, group, subtype);
    if (!mine.length) continue;
    const others = group.fields.filter((f, i) => i !== at && !cvIsNull(cvRead(attributes, f)));
    const fits = v => others.every(f => cvMatches(cvEntry(v, f), cvRead(attributes, f)));
    const live = mine.filter(v => !v.retired && fits(v));
    const kept = original ? mine.filter(v => v.retired && fits(v) && cvFits(v, group, original)) : [];
    if (!live.length && !kept.length) continue;
    limits.push({ group: group.name, live: live.map(v => cvEntry(v, field)), kept: kept.map(v => cvEntry(v, field)) });
  }
  return limits.length ? limits : null;
}

/**
 * Whether an allowance lets a field hold a value.
 *
 * @returns {"ok"|"retired"|null} "retired" when only the retired value the feature held allows it
 */
function cvAllows(limits, value) {
  if (!limits) return "ok";
  let state = "ok";
  for (const limit of limits) {
    if (limit.live.some(e => cvMatches(e, value))) continue;
    if (limit.kept.some(e => cvMatches(e, value))) { state = "retired"; continue; }
    return null;
  }
  return state;
}

/**
 * A coded-value list narrowed to what an allowance lets the field hold, in the domain's order.
 *
 * @param {Array<{code, name}>} codedValues the domain's values
 * @param {Array|null} limits what {@link cvAllowance} returned
 * @param {Array} [keep] values that stay offered even when not allowed — the control's current value and the feature's
 *   stored one — so narrowing never changes what is held without the editor choosing it
 * @returns {Array<{code, name, state: "ok"|"retired"|"unlisted"}>}
 */
function cvNarrow(codedValues, limits, keep = []) {
  const out = [];
  for (const c of codedValues || []) {
    const state = cvAllows(limits, c.code);
    if (state) out.push({ code: c.code, name: c.name, state });
    else if (keep.some(k => !cvIsNull(k) && cvMatches({ kind: "CODED_VALUE", code: c.code }, k))) {
      out.push({ code: c.code, name: c.name, state: "unlisted" });
    }
  }
  return out;
}

/** How a narrowed list's entry is shown. */
function cvOptionLabel(option) {
  if (option.state === "retired") return `${option.name} (retired)`;
  if (option.state === "unlisted") return `${option.name} (not listed)`;
  return String(option.name);
}

/** "A", "A and B", "A, B and C" — or with "or". */
function cvJoin(items, word = "and") {
  const list = [...items];
  if (list.length <= 1) return list.join("");
  return `${list.slice(0, -1).join(", ")} ${word} ${list[list.length - 1]}`;
}

/** The plain reading of field names and codes, when the caller has no layer to read aliases and names from. */
const CV_PLAIN_LOOK = { alias: f => String(f), coded: () => false, name: (f, v) => String(v) };

/** A value as a person reads it: a code's name, or the field's alias and the value. */
function cvValueSaid(look, field, value, alone = false) {
  if (look.coded(field)) return look.name(field, value);
  return alone ? String(value) : `${look.alias(field)} ${value}`;
}

/** What one field may hold across some values' entries, as a phrase: "Model 737 or 747", "any Size". */
function cvPhrase(entries, field, look) {
  const alias = look.alias(field);
  if (entries.some(e => !e || e.kind === "ANY")) return `any ${alias}`;
  const parts = [];
  const seen = new Set();
  for (const e of entries) {
    let part;
    if (e.kind === "NULL") part = "empty";
    else if (e.kind === "RANGE") part = `from ${e.min} to ${e.max}`;
    else part = cvValueSaid(look, field, e.code, true);
    if (!seen.has(part)) { seen.add(part); parts.push(part); }
  }
  return `${alias} ${cvJoin(parts, "or")}`;
}

/**
 * The sentence for one broken group: what the first field's value allows the others, or that it is retired.
 * It names values as the editor's controls show them, and never the group's name or a stored code.
 */
function cvExplain(definition, broken, attributes, subtype, look) {
  const group = ((definition && definition.groups) || []).find(g => cvSame(g.name, broken.group));
  if (!group) return "";
  const held = group.fields.filter(f => !cvIsNull(cvRead(attributes, f)));
  if (broken.retired) {
    const combination = held.map((f, i) => cvValueSaid(look, f, cvRead(attributes, f), i === 0 && look.coded(f))).join(" with ");
    return `${combination} is retired: features that have it keep it, and it is not offered for new edits.`;
  }
  const anchor = held[0];
  const value = cvRead(attributes, anchor);
  const live = cvValuesFor(definition, group, subtype).filter(v => !v.retired && cvMatches(cvEntry(v, anchor), value));
  const said = cvValueSaid(look, anchor, value);
  if (!live.length) return `No listed combination has ${look.coded(anchor) ? `${look.alias(anchor)} ${said}` : said}.`;
  const others = group.fields.filter(f => !cvSame(f, anchor));
  return `${said} allows ${cvJoin(others.map(f => cvPhrase(live.map(v => cvEntry(v, f)), f, look)))}.`;
}

/**
 * What an editor is told about a feature's values: a lead that names the fields of the broken groups and changes only
 * when the set of broken groups does (so a live region is not rewritten at each keystroke — finding 5), and a detail
 * that says what is allowed.
 *
 * @param {{groups: Array, values: Array}} definition
 * @param {object} attributes the values as they will be saved
 * @param {number|null} subtype
 * @param {object|null} original what the feature held, or null for a new one
 * @param {object} [look] `{alias(field), coded(field), name(field, code)}`: how the layer names fields and codes
 * @returns {{key: string, broken: Array, fields: string[], lead: string, detail: string}} all empty when nothing is broken
 */
function cvSentences(definition, attributes, subtype, original = null, look = CV_PLAIN_LOOK) {
  const broken = cvViolations(definition, attributes, subtype, original);
  if (!broken.length) return { key: "", broken, fields: [], lead: "", detail: "" };
  const fields = [];
  for (const b of broken) for (const f of b.fields) if (!fields.some(x => cvSame(x, f))) fields.push(f);
  return {
    key: broken.map(b => String(b.group).toLowerCase()).sort().join("|"),
    broken,
    fields,
    lead: `of ${cvJoin(fields.map(f => look.alias(f)))}`,
    detail: broken.map(b => cvExplain(definition, b, attributes, subtype, look)).join(" "),
  };
}

/**
 * The whole warning as one plain sentence, for a save's message.
 *
 * @param {object} said what {@link cvSentences} returned
 * @param {boolean} [saved] whether the save has been made, which changes the last sentence
 * @returns {string} empty when nothing is broken
 */
function cvWarning(said, saved = false) {
  if (!said || !said.broken || !said.broken.length) return "";
  return `Not a listed combination ${said.lead}. ${said.detail} ${saved ? "Saved anyway." : "You can still save."}`;
}

// ------------------------------------------------------------------------------- Studio's authoring (contingent-studio.js)

/**
 * A typed number, or null when the text is not one. A decimal comma is read as a point, as a reader who writes "10,5"
 * means ten and a half (design review 2026-11-22, finding 3); a thousands separator is not accepted, since "1,500"
 * would be ambiguous.
 */
function cvParseNumber(text) {
  const t = String(text ?? "").trim();
  if (!/^[-+]?(\d+([.,]\d*)?|[.,]\d+)([eE][-+]?\d+)?$/.test(t)) return null;
  const n = Number(t.replace(",", "."));
  return Number.isFinite(n) ? n : null;
}

/** Whether two values of one group list the same combination for the same subtype. */
function cvSameCombination(a, b) {
  if (String(a.subtype ?? "") !== String(b.subtype ?? "")) return false;
  if (a.entries.length !== b.entries.length) return false;
  return a.entries.every(e => {
    const o = cvEntry(b, e.field);
    if (!o || o.kind !== e.kind) return false;
    if (e.kind === "RANGE") return Number(o.min) === Number(e.min) && Number(o.max) === Number(e.max);
    if (e.kind === "CODED_VALUE") return String(o.code) === String(e.code);
    return true;
  });
}

/**
 * Each value that repeats an earlier value of its group, with the earlier one's place in the group (1-based), as the
 * editor numbers rows (finding 8).
 *
 * @param {Array} values the values of one group, in the editor's order
 * @returns {Map<object, number>} value → the place of the value it repeats
 */
function cvDuplicates(values) {
  const out = new Map();
  values.forEach((v, i) => {
    const j = values.slice(0, i).findIndex(w => cvSameCombination(v, w));
    if (j >= 0) out.set(v, j + 1);
  });
  return out;
}

/**
 * The server's refusal in the editor's words: "Contingent value 6" becomes the row's name as the screen shows it, and a
 * field's name becomes its alias (finding 3).
 *
 * @param {string} message the server's sentence
 * @param {Map<number, string>} names a value's id → its name on the screen, e.g. "Value 3 of make_model"
 * @param {(field: string) => string} [alias]
 */
function cvRenameRefusal(message, names, alias = f => f) {
  return String(message)
    .replace(/Contingent value (\d+)/g, (all, id) => names.get(Number(id)) || all)
    .replace(/field '([^']+)'/g, (all, f) => `field ${alias(f)}`);
}

if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    cvFromRest, cvFromStored, cvToStored, cvMatches, cvViolations, cvAllowance, cvAllows, cvNarrow, cvOptionLabel,
    cvJoin, cvSentences, cvWarning, cvExplain, cvParseNumber, cvSameCombination, cvDuplicates, cvRenameRefusal,
    CV_KINDS, CV_TYPE_CODES,
  };
}
