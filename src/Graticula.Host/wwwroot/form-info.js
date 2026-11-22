/*
  A web map layer's form — ADR-313, 2026-11-22. The logic of a `formInfo` (developers.arcgis.com/web-map-specification/
  objects/formInfo/), apart from any page: which elements are shown, required and editable, which values are
  calculated, and which hidden values are cleared — evaluated with Studio's Arcade (arcade.js). The form designer and
  the editing card (webmap-form.js) both call it, and tests/studio-js runs it in Node.

  What the specification says, and this follows:
  - A field element's `requiredExpression` applies only while it is visible and is ignored for a field that is not
    nullable, which is always required (formFieldElement; Field Maps' Configure the form says the same).
  - Its `editableExpression` is ignored when the field itself is not editable.
  - Its `valueExpression` runs only when `editableExpression` is false and the field allows edits.
  - A group hides what it holds; groups do not nest (formGroupElement).
  - A previously visible field's value is cleared when it is hidden, unless `preserveFieldValuesWhenHidden` is true
    (formInfo; the default is false).
  What it does not say, and is INFERRED here (ADR-313 §7): a failing expression is treated as absent and its error is
  reported; calculated values run in form order, again while one changed, at most four passes; `{name}` in a text
  element is the expression of that name, else the field.

  One global, GraticulaFormInfo, because Studio's classic scripts share one namespace.
*/
(function (root) {
  "use strict";

  const Arcade = typeof module === "object" && module.exports ? require("./arcade.js") : root.GraticulaArcade;

  const SYSTEM_TRUE = "expr/system/true";
  const SYSTEM_FALSE = "expr/system/false";
  const MAX_PASSES = 4;

  const NUMBER_TYPES = new Set(["esriFieldTypeSmallInteger", "esriFieldTypeInteger", "esriFieldTypeBigInteger",
    "esriFieldTypeSingle", "esriFieldTypeDouble"]);
  const WHOLE_TYPES = new Set(["esriFieldTypeSmallInteger", "esriFieldTypeInteger", "esriFieldTypeBigInteger"]);
  const TEXT_TYPES = new Set(["esriFieldTypeString", "esriFieldTypeGUID", "esriFieldTypeGlobalID"]);

  /** What each input type is called on screen. The specification's type names are the values. */
  const INPUT_LABELS = {
    "text-box": "Single-line text",
    "text-area": "Multiline text",
    "barcode-scanner": "Barcode",
    "combo-box": "Combo box",
    "radio-buttons": "Radio buttons",
    switch: "Switch",
    "date-picker": "Date",
    "datetime-picker": "Date and time",
  };

  const codedValues = field => (field && field.domain && field.domain.type === "codedValue" && Array.isArray(field.domain.codedValues)
    ? field.domain.codedValues : null);

  /**
   * The input types a field can take, as Map Viewer's Create forms page lists them by field type: barcode for numbers
   * and text, choices (combo box, radio buttons, switch) for numbers and text with a list of values, a date picker for
   * a date only, date and time for a date. A switch needs exactly two values, on and off (INFERRED, ADR-313 §5.3).
   */
  function inputTypesFor(field) {
    if (!field) return [];
    const coded = codedValues(field);
    const choices = coded ? ["combo-box", "radio-buttons", ...(coded.length === 2 ? ["switch"] : [])] : [];
    if (TEXT_TYPES.has(field.type)) return field.type === "esriFieldTypeString" ? ["text-box", "text-area", "barcode-scanner", ...choices] : ["text-box"];
    if (NUMBER_TYPES.has(field.type)) return ["text-box", "barcode-scanner", ...choices];
    if (field.type === "esriFieldTypeDate") return ["datetime-picker"];
    if (field.type === "esriFieldTypeDateOnly") return ["date-picker"];
    return [];
  }

  /** The input a field starts with. */
  function defaultInput(field) {
    if (codedValues(field) && inputTypesFor(field).includes("combo-box")) return { type: "combo-box", showNoValueOption: field.nullable !== false };
    if (field.type === "esriFieldTypeDate") return { type: "datetime-picker", includeTime: true };
    if (field.type === "esriFieldTypeDateOnly") return { type: "date-picker" };
    if (field.type === "esriFieldTypeString" && Number(field.length) > 255) return { type: "text-area" };
    return { type: "text-box" };
  }

  /** A form of every given field, each with its alias and its default input — what the designer starts from. */
  function fromFields(fields, title = "") {
    return {
      title,
      formElements: (fields || []).map(f => ({ type: "field", fieldName: f.name, label: f.alias || f.name, inputType: defaultInput(f) })),
      expressionInfos: [],
    };
  }

  /** Every element with a key that names its place: "2" at the top, "2.0" inside the group at 2. */
  function walk(formInfo) {
    const out = [];
    (formInfo && Array.isArray(formInfo.formElements) ? formInfo.formElements : []).forEach((element, i) => {
      out.push({ key: String(i), element, group: null });
      if (element && element.type === "group" && Array.isArray(element.formElements)) {
        element.formElements.forEach((child, j) => out.push({ key: `${i}.${j}`, element: child, group: element }));
      }
    });
    return out;
  }

  const fieldElements = formInfo => walk(formInfo).filter(e => e.element && e.element.type === "field");

  /** The element at a key, and the array that holds it with its index. */
  function locate(formInfo, key) {
    const [top, inner] = String(key).split(".").map(Number);
    const list = formInfo.formElements || [];
    if (inner === undefined || Number.isNaN(inner)) return { list, index: top, element: list[top] };
    const group = list[top];
    const children = (group && group.formElements) || [];
    return { list: children, index: inner, element: children[inner], group };
  }

  const EXPRESSION_PROPERTIES = ["visibilityExpression", "requiredExpression", "editableExpression", "valueExpression"];

  /** Every expression name an element anywhere in the form refers to. */
  function referencedNames(formInfo) {
    const names = new Set();
    for (const { element } of walk(formInfo)) {
      if (!element) continue;
      for (const property of EXPRESSION_PROPERTIES) if (typeof element[property] === "string") names.add(element[property]);
    }
    return names;
  }

  function expressionInfo(formInfo, name) {
    return (formInfo.expressionInfos || []).find(e => e && e.name === name) || null;
  }

  /** How many element properties name an expression. */
  function uses(formInfo, name) {
    let count = 0;
    for (const { element } of walk(formInfo)) {
      if (!element) continue;
      for (const property of EXPRESSION_PROPERTIES) if (element[property] === name) count++;
    }
    return count;
  }

  /** The `returnType` an expression is written with: Boolean for the conditions, the field's type for a value. */
  function returnTypeFor(property, field) {
    if (property !== "valueExpression") return "boolean";
    if (!field) return "string";
    if (NUMBER_TYPES.has(field.type)) return "number";
    if (field.type === "esriFieldTypeDate") return "date";
    if (field.type === "esriFieldTypeDateOnly") return "dateOnly";
    if (field.type === "esriFieldTypeTimeOnly") return "time";
    return "string";
  }

  const TITLES = { visibilityExpression: "Visible", requiredExpression: "Required", editableExpression: "Editable",
    valueExpression: "Calculated value" };

  /**
   * Gives an element's property an Arcade expression, or takes it away when the text is empty. An expression this
   * property alone names is rewritten in place; otherwise a new one is named `expr/<n>` (INFERRED, ADR-313 §5.3).
   */
  function setExpression(formInfo, element, property, source, field = null) {
    const text = String(source || "").trim();
    formInfo.expressionInfos = formInfo.expressionInfos || [];
    const current = element[property];
    if (!text) {
      delete element[property];
      prune(formInfo);
      return null;
    }
    const mine = typeof current === "string" && current !== SYSTEM_TRUE && current !== SYSTEM_FALSE
      && uses(formInfo, current) === 1 && expressionInfo(formInfo, current);
    const title = `${TITLES[property] || property}${element.fieldName ? ` — ${element.fieldName}` : element.label ? ` — ${element.label}` : ""}`;
    if (mine) {
      mine.expression = text;
      mine.returnType = returnTypeFor(property, field);
      if (!mine.title) mine.title = title;
      return current;
    }
    let n = 0;
    const taken = new Set(formInfo.expressionInfos.map(e => e && e.name));
    while (taken.has(`expr/${n}`)) n++;
    const name = `expr/${n}`;
    formInfo.expressionInfos.push({ name, title, expression: text, returnType: returnTypeFor(property, field) });
    element[property] = name;
    prune(formInfo);
    return name;
  }

  /** Gives a property a fixed answer — true, false, or none (null) — through the system expressions. */
  function setFixed(formInfo, element, property, answer) {
    formInfo.expressionInfos = formInfo.expressionInfos || [];
    if (answer === null || answer === undefined) {
      delete element[property];
    } else {
      const name = answer ? SYSTEM_TRUE : SYSTEM_FALSE;
      if (!expressionInfo(formInfo, name)) {
        formInfo.expressionInfos.push({ name, title: answer ? "True" : "False", expression: answer ? "true" : "false", returnType: "boolean" });
      }
      element[property] = name;
    }
    prune(formInfo);
  }

  /** What a property is set to: "none", "true", "false" (the system expressions) or "expression". */
  function modeOf(formInfo, element, property) {
    const name = element && element[property];
    if (typeof name !== "string") return "none";
    if (name === SYSTEM_TRUE) return "true";
    if (name === SYSTEM_FALSE) return "false";
    const info = expressionInfo(formInfo, name);
    if (info && /^\s*true\s*;?\s*$/.test(info.expression || "")) return "true";
    if (info && /^\s*false\s*;?\s*$/.test(info.expression || "")) return "false";
    return "expression";
  }

  /** Drops the expression infos no element names, so a form does not carry expressions nothing uses. */
  function prune(formInfo) {
    if (!Array.isArray(formInfo.expressionInfos)) return formInfo;
    const used = referencedNames(formInfo);
    formInfo.expressionInfos = formInfo.expressionInfos.filter(e => e && used.has(e.name));
    return formInfo;
  }

  // ---------------------------------------------------------------------------------------------------- values

  /** A layer value as Arcade reads it: a date's milliseconds become a Date, a date-only text a DateOnly. */
  function toArcade(value, field, zone) {
    if (value === undefined || value === null) return null;
    if (!field) return value;
    if (field.type === "esriFieldTypeDate" && Number.isFinite(Number(value))) return new Arcade.ArcDate(Number(value), Arcade.zoneOf(zone || "local"));
    if (field.type === "esriFieldTypeDateOnly" && typeof value === "string") {
      const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
      if (m) return new Arcade.ArcDateOnly(Number(m[1]), Number(m[2]), Number(m[3]));
    }
    if (field.type === "esriFieldTypeTimeOnly" && typeof value === "string") {
      const m = /^(\d{2}):(\d{2})(?::(\d{2}))?/.exec(value);
      if (m) return new Arcade.ArcTime(((Number(m[1]) * 60 + Number(m[2])) * 60 + Number(m[3] || 0)) * 1000);
    }
    return value;
  }

  const pad = n => String(n).padStart(2, "0");

  /**
   * A calculated value as the field holds it, or { problem }. A number field takes a number (a whole one rounded,
   * INFERRED), a text field text no longer than its length, a date field a date.
   */
  function fromArcade(value, field) {
    if (value === null || value === undefined) return { value: null };
    const label = field.alias || field.name;
    if (NUMBER_TYPES.has(field.type)) {
      const number = value instanceof Arcade.ArcDate ? NaN : Arcade.toNumber(value);
      if (!Number.isFinite(number)) return { problem: `${label} takes a number; the calculated value is ${Arcade.typeOf(value)} ${JSON.stringify(Arcade.toText(value))}.` };
      return { value: WHOLE_TYPES.has(field.type) ? Math.round(number) : number };
    }
    if (field.type === "esriFieldTypeDate") {
      if (value instanceof Arcade.ArcDate) return { value: value.ms };
      if (value instanceof Arcade.ArcDateOnly) return { value: new Date(value.year, value.month - 1, value.day).getTime() };
      if (typeof value === "number" && Number.isFinite(value)) return { value };
      if (typeof value === "string") {
        const parsed = Date.parse(value);
        if (Number.isFinite(parsed)) return { value: parsed };
      }
      return { problem: `${label} takes a date; the calculated value is ${Arcade.typeOf(value)}.` };
    }
    if (field.type === "esriFieldTypeDateOnly") {
      if (value instanceof Arcade.ArcDateOnly) return { value: `${value.year}-${pad(value.month)}-${pad(value.day)}` };
      if (value instanceof Arcade.ArcDate) {
        const p = Arcade.partsOf(value);
        return { value: `${p.year}-${pad(p.month + 1)}-${pad(p.day)}` };
      }
      return { problem: `${label} takes a date; the calculated value is ${Arcade.typeOf(value)}.` };
    }
    const text = Arcade.toText(value);
    if (Number(field.length) > 0 && text.length > Number(field.length)) {
      return { problem: `${label} holds at most ${field.length} characters; the calculated value has ${text.length}.` };
    }
    return { value: text };
  }

  function sameValue(a, b) {
    if ((a === null || a === undefined) && (b === null || b === undefined)) return true;
    return a === b;
  }

  // ---------------------------------------------------------------------------------------------------- evaluation

  /** Each expression of the form, parsed once: { info, program } or { info, error }. */
  function compile(formInfo) {
    const byName = new Map();
    for (const info of (formInfo && formInfo.expressionInfos) || []) {
      if (!info || typeof info.name !== "string") continue;
      try {
        byName.set(info.name, { info, program: Arcade.compile(String(info.expression || "")) });
      } catch (error) {
        byName.set(info.name, { info, error: error.message || String(error) });
      }
    }
    return { formInfo, byName };
  }

  /**
   * Evaluates a form against values. `context`:
   * - `fields` — the layer's fields, with their types, domains, `nullable` and `editable`;
   * - `values` — the attributes as typed now; `original` — as read (null for a new feature);
   * - `editType` — "INSERT" or "UPDATE"; `geometry` — the feature's ArcGIS JSON geometry, if any;
   * - `zone` — the execution time zone ("local" by default); `layer` — the layer's description, for Schema and Domain;
   * - `previousVisible` — the `visible` map of the last evaluation, so a field that was shown and is now hidden can be
   *   cleared. Without it nothing is cleared: a field hidden when the form opens keeps its value.
   *
   * Answers { values, elements, byField, visible, cleared, calculated, errors }.
   */
  function evaluate(form, context) {
    const compiled = form && form.byName ? form : compile(form);
    const formInfo = compiled.formInfo || {};
    const fields = context.fields || [];
    const fieldByName = new Map(fields.map(f => [String(f.name).toLowerCase(), f]));
    const values = { ...(context.values || {}) };
    const zone = context.zone || "local";
    const schema = { fields, objectIdField: context.layer && context.layer.objectIdField,
      globalIdField: context.layer && context.layer.globalIdField, geometryType: context.layer && context.layer.geometryType };
    const preserve = formInfo.preserveFieldValuesWhenHidden === true;
    const previous = context.previousVisible || null;
    const cleared = [];
    const calculated = [];

    const featureOf = attributes => {
      const dict = new Arcade.ArcDict();
      for (const f of fields) dict.set(f.name, toArcade(attributes ? attributes[f.name] : null, f, zone));
      if (attributes) for (const [k, v] of Object.entries(attributes)) if (!fieldByName.has(k.toLowerCase())) dict.set(k, v);
      return new Arcade.ArcFeature(dict, context.geometry ? new Arcade.ArcGeometry(context.geometry) : null, true, schema);
    };
    const original = featureOf(context.original || null);
    const editContext = new Arcade.ArcDict({ editType: context.editType || "UPDATE", editTag: "" }, true);

    let elements = [];
    let errors = [];
    for (let pass = 0; pass < MAX_PASSES; pass++) {
      const profile = { $feature: featureOf(values), $originalFeature: original, $editContext: editContext };

      const run = name => {
        const found = compiled.byName.get(name);
        if (!found) return { error: `The form has no expression named '${name}'.` };
        if (found.error) return { error: found.error };
        try {
          return { value: Arcade.run(found.program, profile, { zone }).value };
        } catch (error) {
          return { error: error.message || String(error) };
        }
      };
      const condition = (element, property, fallback, state) => {
        const name = element[property];
        if (typeof name !== "string") return fallback;
        const answer = run(name);
        if (answer.error) { state.errors[property] = answer.error; return fallback; }
        if (typeof answer.value !== "boolean") {
          state.errors[property] = `The expression answered ${Arcade.typeOf(answer.value)}, not a Boolean.`;
          return fallback;
        }
        return answer.value;
      };

      elements = [];
      errors = [];
      let changed = false;
      const groupShown = new Map();
      for (const { key, element, group } of walk(formInfo)) {
        if (!element || typeof element !== "object") continue;
        const state = { key, element, type: element.type, errors: {} };
        const inside = group ? groupShown.get(group) !== false : true;
        state.visible = inside && condition(element, "visibilityExpression", true, state);
        if (element.type === "group") {
          groupShown.set(element, state.visible);
          state.expanded = element.initialState !== "collapsed";
        } else if (element.type === "field") {
          const field = fieldByName.get(String(element.fieldName || "").toLowerCase());
          state.field = field || null;
          if (!field) {
            state.errors.field = `The layer has no field '${element.fieldName}'.`;
            state.editable = false;
            state.required = false;
          } else {
            const fieldEditable = field.editable !== false;
            state.editable = fieldEditable ? condition(element, "editableExpression", true, state) : false;
            const visibleRequired = state.visible && condition(element, "requiredExpression", false, state);
            state.required = field.nullable === false || visibleRequired;
            if (typeof element.valueExpression === "string" && !state.editable && fieldEditable) {
              const answer = run(element.valueExpression);
              if (answer.error) {
                state.errors.valueExpression = answer.error;
              } else {
                const read = fromArcade(answer.value, field);
                if (read.problem) {
                  state.errors.valueExpression = read.problem;
                } else {
                  state.calculated = true;
                  if (!calculated.includes(field.name)) calculated.push(field.name);
                  if (!sameValue(values[field.name], read.value)) {
                    values[field.name] = read.value;
                    changed = true;
                  }
                }
              }
            }
          }
        } else if (element.type === "text") {
          state.text = substitute(String(element.text || ""), name => {
            if (compiled.byName.has(name)) {
              const answer = run(name);
              return answer.error ? "" : Arcade.toText(answer.value);
            }
            const field = fieldByName.get(name.toLowerCase());
            if (!field) return `{${name}}`;
            const value = values[field.name];
            const coded = codedValues(field);
            const found = coded && coded.find(c => String(c.code) === String(value));
            if (found) return found.name;
            if (value === null || value === undefined) return "";
            return Arcade.toText(toArcade(value, field, zone));
          });
        }
        for (const [property, message] of Object.entries(state.errors)) errors.push({ key, property, message });
        elements.push(state);
      }

      // Clearing: a field that was shown and is now hidden, unless the form preserves hidden values.
      if (previous && !preserve) {
        for (const state of elements) {
          if (state.type !== "field" || !state.field || state.visible) continue;
          const name = state.field.name;
          if (previous.get(name) === true && values[name] !== null && values[name] !== undefined) {
            values[name] = null;
            if (!cleared.includes(name)) cleared.push(name);
            changed = true;
          }
        }
      }
      if (!changed) break;
    }

    const byField = new Map();
    const visible = new Map();
    for (const state of elements) {
      if (state.type === "field" && state.field) {
        byField.set(state.field.name, state);
        visible.set(state.field.name, state.visible);
      }
    }
    return { values, elements, byField, visible, cleared, calculated, errors };
  }

  /**
   * The values a save is refused for: a visible required field, or a hidden one whose field is not nullable, left
   * empty (Field Maps' Configure the form: "a value must be entered … whether the element is visible or hidden").
   */
  function missing(result) {
    const out = [];
    for (const state of result.elements) {
      if (state.type !== "field" || !state.field || !state.required) continue;
      const value = result.values[state.field.name];
      if (value === null || value === undefined || value === "") out.push(state.field.name);
    }
    return out;
  }

  /** `{name}` replaced by what `read(name)` answers; a brace that does not close is left as it is. */
  function substitute(text, read) {
    return text.replace(/\{([^{}]+)\}/g, (_m, name) => read(name.trim()));
  }

  const escapeHtml = value => String(value).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  /**
   * A text element's markdown as safe HTML: headings, bold and italic, bulleted and numbered lists, and paragraphs.
   * Everything else is escaped, links among it (INFERRED, ADR-313 §5.2).
   */
  function markdownHtml(text) {
    const inline = line => escapeHtml(line)
      .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
      .replace(/(^|[^*])\*([^*]+)\*(?!\*)/g, "$1<em>$2</em>")
      .replace(/(^|\W)_([^_]+)_(?=\W|$)/g, "$1<em>$2</em>");
    const out = [];
    let list = null;
    let paragraph = [];
    const flush = () => {
      if (paragraph.length) { out.push(`<p>${paragraph.map(inline).join("<br>")}</p>`); paragraph = []; }
      if (list) { out.push(`<${list.tag}>${list.items.map(i => `<li>${inline(i)}</li>`).join("")}</${list.tag}>`); list = null; }
    };
    for (const raw of String(text || "").split(/\r?\n/)) {
      const line = raw.trimEnd();
      const heading = /^(#{1,6})\s+(.*)$/.exec(line);
      const bullet = /^\s*[-*+]\s+(.*)$/.exec(line);
      const numbered = /^\s*\d+[.)]\s+(.*)$/.exec(line);
      if (!line.trim()) { flush(); continue; }
      if (heading) {
        flush();
        const level = Math.min(heading[1].length + 2, 6); // inside a form, a heading sits below the card's own
        out.push(`<h${level}>${inline(heading[2])}</h${level}>`);
      } else if (bullet || numbered) {
        if (paragraph.length) { out.push(`<p>${paragraph.map(inline).join("<br>")}</p>`); paragraph = []; }
        const tag = bullet ? "ul" : "ol";
        if (list && list.tag !== tag) flush();
        if (!list) list = { tag, items: [] };
        list.items.push((bullet || numbered)[1]);
      } else {
        if (list) flush();
        paragraph.push(line);
      }
    }
    flush();
    return out.join("");
  }

  // ---------------------------------------------------------------------------------------------------- checking

  /**
   * What is wrong with a form for a layer, as sentences: a field the layer does not have or that appears twice, a group
   * inside a group, an expression named and missing or that does not parse, an input type the field cannot take, a
   * switch whose values are not the field's, a text element over 1,000 characters (Field Maps' limit).
   */
  function problems(formInfo, fields) {
    const out = [];
    const byName = new Map((fields || []).map(f => [String(f.name).toLowerCase(), f]));
    const seen = new Set();
    const compiled = compile(formInfo);
    const label = element => element.label || element.fieldName || element.type;
    for (const { element, group } of walk(formInfo)) {
      if (!element || typeof element !== "object") continue;
      if (group && element.type === "group") out.push(`The group “${label(element)}” is inside the group “${label(group)}”; groups do not nest.`);
      for (const property of EXPRESSION_PROPERTIES) {
        const name = element[property];
        if (typeof name !== "string") continue;
        const found = compiled.byName.get(name);
        if (!found) out.push(`“${label(element)}” names the expression '${name}', which the form does not have.`);
        else if (found.error) out.push(`The ${(TITLES[property] || property).toLowerCase()} expression of “${label(element)}” does not parse: ${found.error}`);
      }
      if (element.type === "field") {
        const key = String(element.fieldName || "").toLowerCase();
        const field = byName.get(key);
        if (!field) { out.push(`The layer has no field '${element.fieldName}'.`); continue; }
        if (seen.has(key)) out.push(`The field ${field.name} is in the form twice.`);
        seen.add(key);
        const type = element.inputType && element.inputType.type;
        const offered = inputTypesFor(field);
        if (type && offered.length && !offered.includes(type) && INPUT_LABELS[type]) {
          out.push(`${field.alias || field.name} cannot take the input “${INPUT_LABELS[type]}”.`);
        }
        if (type === "switch") {
          const codes = (codedValues(field) || []).map(c => String(c.code));
          for (const which of ["onValue", "offValue"]) {
            if (!codes.includes(String(element.inputType[which]))) out.push(`The switch of ${field.alias || field.name} needs its ${which === "onValue" ? "on" : "off"} value to be one of the field's values.`);
          }
        }
      }
      if (element.type === "text" && String(element.text || "").length > 1000) out.push(`A text element holds at most 1,000 characters; “${String(element.text).slice(0, 30)}…” has ${String(element.text).length}.`);
    }
    return out;
  }

  const api = {
    SYSTEM_TRUE, SYSTEM_FALSE, INPUT_LABELS, EXPRESSION_PROPERTIES, inputTypesFor, defaultInput, fromFields, walk,
    fieldElements, locate, setExpression, setFixed, modeOf, prune, expressionInfo, returnTypeFor, compile, evaluate,
    missing, substitute, markdownHtml, problems, toArcade, fromArcade, codedValues,
  };

  if (typeof module === "object" && module.exports) module.exports = api;
  else root.GraticulaFormInfo = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
