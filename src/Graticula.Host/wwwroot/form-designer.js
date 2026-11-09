/*
  The form designer — ADR-287 §5.8, built 2026-11-09 by owner decision ("Survey123 web tasarımcısı", "Şimdi, aynı dalda").

  <b>Survey123's web designer, in its structure.</b> The form in the middle as it reads, a question at a time; a panel at
  the side with Add (the question types), Edit (the chosen question), Settings (the form's own) and Preview (the form
  as the phone draws it). Save writes; nothing is written before.

  <b>One model, and the designer edits it in place.</b> The stored form is the XLSForm's sheets as rows and columns
  (FormModel on the server). The designer reads those rows, changes the cells it knows — type, name, label, hint,
  required, relevant, constraint, default, appearance, read only, calculation, parameters, repeat_count — and leaves
  every other cell where it was. A column it does not edit is listed on screen and travels with the XLSForm untouched.
  There is no second model behind the designer to fall out of step with the workbook.

  <b>The server decides.</b> Every change is checked as it is made, through the same /check the upload uses; what it
  refuses is listed with its row and column, and Save is refused by the server for the same reasons — the designer
  never holds a rule of its own that could disagree.

  <b>Every name here starts with fd</b>, because classic scripts share one namespace (console.js defines the rest).
*/
"use strict";

/** The open designer: the form's id, its item, the model being edited, and what is chosen and shown. */
let fdState = null;

/** Unsaved models by form id, kept while this tab is open, so leaving the page and coming back loses nothing. */
const fdKept = new Map();

/** A stable key per survey row, so a redraw can find the card that had focus. Rows are arrays, kept by identity. */
const fdKeys = new WeakMap();
let fdKeySeq = 0;
const fdKey = row => { if (!fdKeys.has(row)) fdKeys.set(row, `r${++fdKeySeq}`); return fdKeys.get(row); };

/** Rows whose name the designer made from the label, and keeps making until somebody types a name. */
const fdAutoNames = new WeakSet();

/** The types offered under Add, in Survey123's order, each as the cells it writes. */
const FD_TYPES = [
  { key: "text", word: "Single line text", type: "text" },
  { key: "multiline", word: "Multiline text", type: "text", appearance: "multiline" },
  { key: "select_one", word: "Single choice", type: "select_one", choices: true },
  { key: "select_multiple", word: "Multiple choice", type: "select_multiple", choices: true },
  { key: "dropdown", word: "Dropdown", type: "select_one", appearance: "minimal", choices: true },
  { key: "likert", word: "Likert", type: "select_one", appearance: "likert", choices: true },
  { key: "integer", word: "Number", type: "integer" },
  { key: "decimal", word: "Decimal number", type: "decimal" },
  { key: "range", word: "Rating", type: "range", parameters: "start=1 end=5 step=1" },
  { key: "date", word: "Date", type: "date" },
  { key: "time", word: "Time", type: "time" },
  { key: "dateTime", word: "Date and time", type: "dateTime" },
  { key: "geopoint", word: "Map point", type: "geopoint" },
  { key: "geotrace", word: "Map line", type: "geotrace" },
  { key: "geoshape", word: "Map area", type: "geoshape" },
  { key: "image", word: "Image", type: "image" },
  { key: "signature", word: "Signature", type: "image", appearance: "signature" },
  { key: "file", word: "File", type: "file" },
  { key: "audio", word: "Audio", type: "audio" },
  { key: "note", word: "Note", type: "note" },
  { key: "acknowledge", word: "Acknowledge", type: "acknowledge" },
  { key: "hidden", word: "Hidden", type: "hidden" },
  { key: "calculate", word: "Calculation", type: "calculate" },
  { key: "group", word: "Group", type: "begin group", container: true },
  { key: "repeat", word: "Repeat", type: "begin repeat", container: true },
];

/** What a type is called on its card. */
const fdTypeWord = (base, appearance) => {
  const app = String(appearance || "").split(/\s+/);
  const hit = FD_TYPES.find(t => t.type === base && t.appearance && app.includes(t.appearance))
    || FD_TYPES.find(t => t.type === base && !t.appearance);
  return hit ? hit.word : base === "select_one_from_file" ? "Single choice from a file"
    : base === "select_multiple_from_file" ? "Multiple choice from a file" : base || "Row";
};

/** The appearances this server honours, by the types they go on — FormCatalog.SupportedAppearances. */
const FD_APPEARANCES = {
  multiline: ["text"], signature: ["image"], "month-year": ["date"], year: ["date"],
  minimal: ["select_one", "select_multiple", "select_one_from_file", "select_multiple_from_file"],
  likert: ["select_one", "select_one_from_file"], rating: ["range"],
  "field-list": ["begin group", "begin repeat"], "table-list": ["begin group", "begin repeat"],
};

/** Survey columns the designer edits; any other with a value is listed and kept as written. */
const FD_EDITED = new Set(["type", "name", "label", "hint", "required", "relevant", "constraint", "constraint_message",
  "default", "appearance", "read_only", "readonly", "calculation", "parameters", "repeat_count"]);

/** Columns that carry a language after `::`. */
const FD_TRANSLATED = new Set(["label", "hint", "constraint_message", "required_message", "guidance_hint"]);

/**
 * Whether the designer edits a survey column: one it knows, and for a column that carries a language, only the language it
 * writes — `label::English` is kept as written when the designer edits `label::Türkçe (tr)`.
 */
function fdEdits(column) {
  const c = String(column).trim();
  const base = fdBase(c);
  if (!FD_EDITED.has(base)) return false;
  if (!FD_TRANSLATED.has(base)) return !c.includes("::");
  return c.toLowerCase() === fdLangCol(base).toLowerCase();
}

// ------------------------------------------------------------------------------------------------- the model's cells

const fdSheet = name => fdState.model.sheets.find(s => String(s.name).toLowerCase() === name);

/** The sheet, made when the form has none (a blank form has all three). */
function fdSheetMade(name, columns) {
  let sheet = fdSheet(name);
  if (!sheet) {
    sheet = { name, columns: [...columns], rows: [] };
    fdState.model.sheets.push(sheet);
  }
  return sheet;
}

const fdBase = column => String(column || "").split("::")[0].trim().toLowerCase();
const fdCol = (sheet, name) => sheet.columns.findIndex(c => String(c).trim().toLowerCase() === name.toLowerCase());

/** A column name with the language the designer edits, for the columns that carry one. */
const fdLangCol = name => FD_TRANSLATED.has(name) && fdState.lang ? `${name}::${fdState.lang}` : name;

function fdGet(sheet, row, name) {
  const i = fdCol(sheet, fdLangCol(name));
  return i < 0 ? "" : String(row[i] ?? "");
}

/** Writes a cell, adding the column when it is new and there is something to write; trailing empties are dropped. */
function fdPut(sheet, row, name, value) {
  const column = fdLangCol(name);
  let i = fdCol(sheet, column);
  const text = String(value ?? "");
  if (i < 0) {
    if (!text) return;
    sheet.columns.push(column);
    i = sheet.columns.length - 1;
  }
  while (row.length <= i) row.push("");
  row[i] = text;
  while (row.length && row[row.length - 1] === "") row.pop();
}

/** The type cell read: its base (`select_one`, `begin group`, …) and its list, for a select. */
function fdType(row) {
  const raw = fdGet(fdSheet("survey"), row, "type").trim().replace(/\s+/g, " ");
  const select = /^(select[_ ]one|select[_ ]multiple|select_one_from_file|select_multiple_from_file)\s+(\S+)/i.exec(raw);
  if (select) return { base: select[1].toLowerCase().replace(" ", "_"), list: select[2] };
  const lower = raw.toLowerCase().replace(/_/g, " ");
  if (/^(begin|end) (group|repeat)$/.test(lower)) return { base: lower, list: null };
  return { base: raw, list: null };
}

/** The language the designer writes labels in: plain `label` when the form has it, else its default language's. */
function fdLanguageOf(model) {
  const survey = model.sheets.find(s => String(s.name).toLowerCase() === "survey");
  const settings = model.sheets.find(s => String(s.name).toLowerCase() === "settings");
  const columns = (survey?.columns || []).map(c => String(c).trim());
  if (!columns.length || columns.some(c => c.toLowerCase() === "label")) return "";
  const at = settings ? settings.columns.findIndex(c => String(c).trim().toLowerCase() === "default_language") : -1;
  const named = at >= 0 ? String(settings.rows[0]?.[at] || "").trim() : "";
  if (named && columns.some(c => c.toLowerCase() === `label::${named}`.toLowerCase())) return named;
  const first = columns.find(c => /^label::/i.test(c));
  return first ? first.slice(first.indexOf("::") + 2) : "";
}

/** The survey's rows as the form nests them: groups and repeats with their children, and blank rows left out. */
function fdTree() {
  const rows = fdSheet("survey").rows;
  const root = { kind: "root", children: [], start: -1, end: rows.length };
  const stack = [root];
  rows.forEach((row, i) => {
    const { base } = fdType(row);
    if (!base) return;
    const top = stack[stack.length - 1];
    if (base === "begin group" || base === "begin repeat") {
      const node = { kind: base === "begin group" ? "group" : "repeat", row, start: i, end: rows.length - 1, children: [], parent: top };
      top.children.push(node);
      stack.push(node);
    } else if (base === "end group" || base === "end repeat") {
      if (stack.length > 1) stack.pop().end = i;
    } else {
      top.children.push({ kind: "question", row, start: i, end: i, parent: top });
    }
  });
  return root;
}

/** Every node of the tree, in the order the form asks them. */
function fdWalk(node, out = []) {
  for (const child of node.children) {
    out.push(child);
    if (child.children) fdWalk(child, out);
  }
  return out;
}

const fdNodeOf = row => fdWalk(fdTree()).find(n => n.row === row) || null;

/** Every name in the survey, for keeping a new one unique. */
const fdNames = except => new Set(fdSheet("survey").rows.filter(r => r !== except).map(r => fdGet(fdSheet("survey"), r, "name")).filter(Boolean));

/** A label as a field name: Latin letters, digits and underscores, starting with a letter, as a column must. */
function fdSlug(text) {
  const map = { ç: "c", ğ: "g", ı: "i", İ: "i", ö: "o", ş: "s", ü: "u", Ç: "c", Ğ: "g", Ö: "o", Ş: "s", Ü: "u" };
  let slug = String(text || "").replace(/[çğıİöşüÇĞÖŞÜ]/g, c => map[c]).normalize("NFD").replace(/[̀-ͯ]/g, "")
    .toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "").slice(0, 40).replace(/_+$/, "");
  if (!slug) slug = "question";
  if (/^[0-9]/.test(slug)) slug = `q_${slug}`;
  return slug;
}

function fdUnique(base, taken) {
  if (!taken.has(base)) return base;
  for (let i = 2; ; i++) if (!taken.has(`${base}_${i}`)) return `${base}_${i}`;
}

// ------------------------------------------------------------------------------------------------------- choices

const fdListCol = sheet => { const i = fdCol(sheet, "list_name"); return i >= 0 ? "list_name" : fdCol(sheet, "list name") >= 0 ? "list name" : "list_name"; };

function fdChoices(list) {
  const sheet = fdSheetMade("choices", ["list_name", "name", "label"]);
  const column = fdListCol(sheet);
  return sheet.rows.filter(r => fdGet(sheet, r, column) === list);
}

/**
 * A list the designer made for a question is named after it, as Survey123 names it; when the question's name follows its
 * label, so does the list's — only while this question is the one that asks it, so a shared list never moves under another.
 */
function fdRenameOwnList(row, before, after) {
  const { base, list } = fdType(row);
  if (!list || list !== `${before}_list` || fdListUsers(list) !== 1) return;
  const choices = fdSheet("choices");
  const column = fdListCol(choices);
  const taken = new Set(choices.rows.map(r => fdGet(choices, r, column)));
  const renamed = fdUnique(`${after}_list`, taken);
  for (const r of choices.rows) if (fdGet(choices, r, column) === list) fdPut(choices, r, column, renamed);
  fdPut(fdSheet("survey"), row, "type", `${base} ${renamed}`);
}

/** How many select questions use a list — editing a shared list changes every one of them. */
const fdListUsers = list => fdSheet("survey").rows.filter(r => fdType(r).list === list).length;

function fdAddChoice(list, label, after) {
  const sheet = fdSheetMade("choices", ["list_name", "name", "label"]);
  const column = fdListCol(sheet);
  const taken = new Set(fdChoices(list).map(r => fdGet(sheet, r, "name")));
  const row = [];
  fdPut(sheet, row, column, list);
  fdPut(sheet, row, "name", fdUnique(fdSlug(label), taken));
  fdPut(sheet, row, "label", label);
  fdAutoNames.add(row);
  const rows = sheet.rows;
  const own = fdChoices(list);
  const at = after ? rows.indexOf(after) + 1 : own.length ? rows.indexOf(own[own.length - 1]) + 1 : rows.length;
  rows.splice(at, 0, row);
  return row;
}

// ------------------------------------------------------------------------------------------------- rules (relevant)

/** One condition of a rule, as the builder edits it. */
const FD_OPERATORS = {
  choice: [["is", "is"], ["isnot", "is not"], ["answered", "is answered"], ["empty", "is not answered"]],
  multiple: [["includes", "includes"], ["excludes", "does not include"], ["answered", "is answered"], ["empty", "is not answered"]],
  number: [["=", "="], ["!=", "≠"], ["<", "<"], ["<=", "≤"], [">", ">"], [">=", "≥"], ["answered", "is answered"], ["empty", "is not answered"]],
  text: [["is", "is"], ["isnot", "is not"], ["answered", "is answered"], ["empty", "is not answered"]],
};

const fdKindOf = base => base === "select_one" ? "choice" : base === "select_multiple" ? "multiple"
  : base === "integer" || base === "decimal" || base === "range" ? "number" : "text";

const fdQuote = v => `'${String(v).replace(/'/g, "")}'`;

/** A condition as XLSForm writes it. */
function fdConditionText(c) {
  const ref = `\${${c.q}}`;
  if (c.op === "answered") return `${ref} != ''`;
  if (c.op === "empty") return `${ref} = ''`;
  if (c.op === "includes") return `selected(${ref}, ${fdQuote(c.v)})`;
  if (c.op === "excludes") return `not(selected(${ref}, ${fdQuote(c.v)}))`;
  if (c.op === "is") return `${ref} = ${fdQuote(c.v)}`;
  if (c.op === "isnot") return `${ref} != ${fdQuote(c.v)}`;
  const n = String(c.v).trim();
  return `${ref} ${c.op} ${/^-?\d+(\.\d+)?$/.test(n) ? n : fdQuote(n)}`;
}

/** A rule read back into conditions, or null when it was written in a shape the builder does not draw. */
function fdParseRule(text) {
  const rule = String(text || "").trim();
  if (!rule) return { all: true, conditions: [] };
  const hasAnd = / and /.test(rule), hasOr = / or /.test(rule);
  if (hasAnd && hasOr) return null;
  const parts = rule.split(hasOr ? / or / : / and /);
  const conditions = [];
  for (const part of parts) {
    const p = part.trim();
    let m;
    if ((m = /^\$\{(\w+)\}\s*!=\s*''$/.exec(p))) conditions.push({ q: m[1], op: "answered", v: "" });
    else if ((m = /^\$\{(\w+)\}\s*=\s*''$/.exec(p))) conditions.push({ q: m[1], op: "empty", v: "" });
    else if ((m = /^not\(selected\(\$\{(\w+)\},\s*'([^']*)'\)\)$/.exec(p))) conditions.push({ q: m[1], op: "excludes", v: m[2] });
    else if ((m = /^selected\(\$\{(\w+)\},\s*'([^']*)'\)$/.exec(p))) conditions.push({ q: m[1], op: "includes", v: m[2] });
    else if ((m = /^\$\{(\w+)\}\s*(=|!=)\s*'([^']*)'$/.exec(p))) conditions.push({ q: m[1], op: m[2] === "=" ? "is" : "isnot", v: m[3] });
    else if ((m = /^\$\{(\w+)\}\s*(<=|>=|!=|=|<|>)\s*(-?\d+(?:\.\d+)?)$/.exec(p))) conditions.push({ q: m[1], op: m[2], v: m[3] });
    else return null;
  }
  return { all: !hasOr, conditions };
}

/** The questions a rule on this row may ask about: answered before it, and not inside a repeat it is outside of. */
function fdRuleSources(row) {
  const nodes = fdWalk(fdTree());
  const at = nodes.findIndex(n => n.row === row);
  const repeatsOf = node => { const out = []; for (let p = node.parent; p; p = p.parent) if (p.kind === "repeat") out.push(p); return out; };
  const mine = at >= 0 ? repeatsOf(nodes[at]) : [];
  return nodes.slice(0, Math.max(at, 0)).filter(n => n.kind === "question"
    && !["note", "calculate", "hidden"].includes(fdType(n.row).base)
    && repeatsOf(n).every(r => mine.includes(r)));
}

// ----------------------------------------------------------------------------------------------- constraints

/** A constraint read as the builder's limits, or null when it is written another way. */
function fdParseLimits(text, base) {
  const rule = String(text || "").trim();
  if (!rule) return { min: "", max: "", length: "" };
  if (base === "text") {
    const m = /^string-length\(\.\)\s*<=\s*(\d+)$/.exec(rule);
    return m ? { min: "", max: "", length: m[1] } : null;
  }
  let min = "", max = "";
  for (const part of rule.split(/ and /)) {
    const m = /^\.\s*(>=|<=)\s*(-?\d+(?:\.\d+)?)$/.exec(part.trim());
    if (!m) return null;
    if (m[1] === ">=") min = m[2]; else max = m[2];
  }
  return { min, max, length: "" };
}

function fdLimitsText(base, limits) {
  if (base === "text") return limits.length ? `string-length(.) <= ${limits.length}` : "";
  return [limits.min !== "" ? `. >= ${limits.min}` : "", limits.max !== "" ? `. <= ${limits.max}` : ""].filter(Boolean).join(" and ");
}

// ------------------------------------------------------------------------------------------------ opening

/** A blank form: the three sheets, with the columns the designer writes most, and its title. */
function fdBlankModel(title) {
  return {
    format: "xlsform/1",
    sheets: [
      { name: "survey", columns: ["type", "name", "label", "hint", "required", "relevant", "constraint", "constraint_message", "default", "appearance"], rows: [] },
      { name: "choices", columns: ["list_name", "name", "label"], rows: [] },
      { name: "settings", columns: ["form_title", "form_id", "version"], rows: [[title, "", ""]] },
    ],
  };
}

/** `#/item/{id}/design`: reads the form and draws the designer. */
async function showFormDesigner(id) {
  showView("view-formdesign", "content");
  $("fdTitle").textContent = "Form designer";
  $("fdBack").href = pathOf(`#/item/${encodeURIComponent(id)}`);
  $("fdCanvas").innerHTML = `<p class="hint">Reading the form…</p>`;
  $("fdPanel").innerHTML = "";
  $("fdProblems").hidden = true;
  $("fdSays").textContent = "";

  let read;
  try {
    read = await api(`/admin/forms/${encodeURIComponent(id)}`);
  } catch (e) {
    $("fdCanvas").innerHTML = `<p class="bad-inline">${h(e.message || e)}</p>`;
    return;
  }
  if (!/\/item\/[^/]+\/design/.test(location.pathname)) return;

  const kept = fdKept.get(read.id || id);
  fdState = {
    id: read.id || id,
    title: read.title || "Form",
    published: !!read.publication,
    model: JSON.parse(kept || JSON.stringify(read.model)),
    dirty: !!kept,
    selected: null,
    tab: "add",
    check: null,
    checkTimer: 0,
    checking: 0,
    previewReady: false,
    lastForm: null,
    rawRule: new WeakSet(),
    rawLimits: new WeakSet(),
  };
  fdState.lang = fdLanguageOf(fdState.model);
  fdSheetMade("survey", ["type", "name", "label"]);
  fdSheetMade("choices", ["list_name", "name", "label"]);
  fdSheetMade("settings", ["form_title"]);
  if (!fdSheet("settings").rows.length) fdSheet("settings").rows.push([]);

  $("fdTitle").textContent = fdState.title;
  document.title = `${fdState.title} · Designer`;
  fdDrawBar();
  fdDrawNotices();
  fdDrawCanvas();
  fdSelectTab(fdState.published ? "preview" : "add", false);
  fdSay(kept ? "Your unsaved changes to this form are back. Save keeps them; leaving the tab loses them."
    : fdState.published ? "This form is published. Its questions cannot be changed yet; the designer shows it and previews it."
    : "");
  fdScheduleCheck(0);
  $("fdTitle").focus();
}

function fdSay(text) { $("fdSays").textContent = text; }

/** The bar: Save, and what state the form is in. */
function fdDrawBar() {
  const save = $("fdSave");
  save.disabled = fdState.published || !fdState.dirty;
  save.setAttribute("aria-describedby", "fdState");
  $("fdState").textContent = fdState.published ? "Published — read only"
    : fdState.dirty ? "Unsaved changes" : "Saved";
  $("fdState").className = `fdstate${fdState.dirty ? " dirty" : ""}`;
}

/** What this form uses that the designer does not edit, said once at the top: it is kept, not dropped. */
function fdDrawNotices() {
  const survey = fdSheet("survey");
  const unedited = new Set();
  survey.columns.forEach((c, i) => {
    if (!fdEdits(c) && survey.rows.some(r => String(r[i] ?? "") !== "")) unedited.add(String(c));
  });
  const sheets = fdState.model.sheets.map(s => s.name).filter(n => !["survey", "choices", "settings"].includes(String(n).toLowerCase()));
  const parts = [];
  if (unedited.size) parts.push(`the columns ${[...unedited].map(c => `<code>${h(c)}</code>`).join(", ")}`);
  if (sheets.length) parts.push(`the sheets ${sheets.map(s => `<code>${h(s)}</code>`).join(", ")}`);
  const box = $("fdNotices");
  box.hidden = !parts.length;
  box.innerHTML = parts.length ? `<p><b>This form uses what the designer does not edit:</b> ${parts.join(" and ")}. They are
    kept exactly as written, travel with the XLSForm, and are edited in the workbook.${fdState.lang
      ? ` Labels are edited in <b>${h(fdState.lang)}</b>, the form's default language.` : ""}</p>` : "";
}

/** Marks the form changed: kept for this tab, Save offered, and checked again shortly. */
function fdChanged(redrawCanvas = true) {
  fdState.dirty = true;
  fdKept.set(fdState.id, JSON.stringify(fdState.model));
  fdDrawBar();
  if (redrawCanvas) fdDrawCanvas();
  fdScheduleCheck(700);
}

// --------------------------------------------------------------------------------------------------- the canvas

function fdDrawCanvas() {
  const canvas = $("fdCanvas");
  const had = document.activeElement && canvas.contains(document.activeElement)
    ? { key: document.activeElement.closest("[data-fd-key]")?.dataset.fdKey, act: document.activeElement.dataset.fdAct } : null;
  const tree = fdTree();
  const survey = fdSheet("survey");
  const problems = fdState.check?.refusals || [];
  const rowOf = node => fdSheet("survey").rows.indexOf(node.row) + 2;

  const card = (node, index, siblings) => {
    const { base } = fdType(node.row);
    const key = fdKey(node.row);
    const label = fdGet(survey, node.row, "label");
    const name = fdGet(survey, node.row, "name");
    const required = /^(yes|true|true\(\))$/i.test(fdGet(survey, node.row, "required").trim());
    const shownWhen = fdGet(survey, node.row, "relevant").trim();
    const flagged = problems.some(p => String(p.sheet).toLowerCase() === "survey" && p.row === rowOf(node));
    const selected = fdState.selected === node.row;
    const word = node.kind === "question" ? fdTypeWord(base, fdGet(survey, node.row, "appearance")) : node.kind === "group" ? "Group" : "Repeat";
    const head = `
      <div class="fdqhead">
        <button type="button" class="fdqpick" data-fd-act="pick" aria-pressed="${selected}">
          <span class="fdqtype">${h(word)}</span>
          <span class="fdqlabel">${label ? h(label) : `<i>No label</i>`}${required ? ` <span class="fdreq" aria-label="required">*</span>` : ""}</span>
          <span class="fdqname"><code>${h(name || "—")}</code>${shownWhen ? ` · <span class="fdrule">shown when a rule holds</span>` : ""}${
            flagged ? ` · <span class="fdflag">needs a change</span>` : ""}</span>
        </button>
        ${fdState.published ? "" : `<span class="fdqacts">
          <button type="button" class="ghost tiny fdicon" data-fd-act="up" aria-label="Move ${h(label || name)} up"${index === 0 && node.parent.kind === "root" ? " disabled" : ""}>↑</button>
          <button type="button" class="ghost tiny fdicon" data-fd-act="down" aria-label="Move ${h(label || name)} down"${index === siblings.length - 1 && node.parent.kind === "root" ? " disabled" : ""}>↓</button>
          <button type="button" class="ghost tiny fdicon" data-fd-act="copy" aria-label="Duplicate ${h(label || name)}">⧉</button>
          <button type="button" class="ghost tiny fdicon" data-fd-act="delete" aria-label="Delete ${h(label || name)}">✕</button>
        </span>`}
      </div>`;
    const inner = node.children
      ? `<div class="fdchildren">${node.children.length ? node.children.map((c, i) => card(c, i, node.children)).join("")
        : `<p class="hint fdempty">Empty. Choose this ${node.kind} and add a question: it goes inside.</p>`}</div>` : "";
    return `<div class="fdq fd${node.kind}${selected ? " on" : ""}${flagged ? " flagged" : ""}" data-fd-key="${key}">${head}${inner}</div>`;
  };

  const settings = fdSheet("settings");
  const formTitle = fdGet(settings, settings.rows[0], "form_title") || fdState.title;
  canvas.innerHTML = `
    <div class="fdformhead"><h2 class="fdformtitle">${h(formTitle)}</h2></div>
    ${tree.children.length ? tree.children.map((c, i) => card(c, i, tree.children)).join("")
      : `<p class="hint fdempty">No questions yet. ${fdState.published ? "" : "Choose a type under <b>Add</b> to ask the first one."}</p>`}`;

  if (had?.key) {
    const back = canvas.querySelector(`[data-fd-key="${had.key}"] [data-fd-act="${had.act || "pick"}"]`)
      || canvas.querySelector(`[data-fd-key="${had.key}"] [data-fd-act="pick"]`);
    if (back && !back.disabled) back.focus({ preventScroll: false });
    else canvas.querySelector(`[data-fd-key="${had.key}"] [data-fd-act="pick"]`)?.focus();
  }
}

/** Moves a block among its siblings, into a group beside it, or out of the one it is in — Survey123's arrows. */
function fdMove(node, dir) {
  const rows = fdSheet("survey").rows;
  const siblings = node.parent.children;
  const k = siblings.indexOf(node);
  const other = siblings[k + dir];
  const block = rows.slice(node.start, node.end + 1);
  let at;
  if (other) at = dir < 0 ? (other.children ? other.end : other.start) : (other.children ? other.start + 1 : other.end + 1);
  else if (node.parent.kind !== "root") at = dir < 0 ? node.parent.start : node.parent.end + 1;
  else return false;
  if (dir < 0) { rows.splice(node.start, block.length); rows.splice(at, 0, ...block); }
  else { rows.splice(at, 0, ...block); rows.splice(node.start, block.length); }
  return true;
}

function fdDelete(node) {
  const survey = fdSheet("survey");
  const lists = survey.rows.slice(node.start, node.end + 1).map(r => fdType(r).list).filter(Boolean);
  survey.rows.splice(node.start, node.end - node.start + 1);
  // A list nobody asks any more goes with its question, as Survey123's designer removes it.
  const choices = fdSheet("choices");
  for (const list of new Set(lists)) {
    if (fdListUsers(list) === 0) choices.rows = choices.rows.filter(r => fdGet(choices, r, fdListCol(choices)) !== list);
  }
}

function fdDuplicate(node) {
  const survey = fdSheet("survey");
  const copies = survey.rows.slice(node.start, node.end + 1).map(r => [...r]);
  const taken = fdNames(null);
  for (const row of copies) {
    const name = fdGet(survey, row, "name");
    if (name && !/^end /.test(fdType(row).base)) {
      const unique = fdUnique(`${name}_copy`, taken);
      taken.add(unique);
      fdPut(survey, row, "name", unique);
    }
  }
  survey.rows.splice(node.end + 1, 0, ...copies);
  return copies[0];
}

/** Adds a question of a type: inside the chosen group or repeat, after the chosen question, or at the end. */
function fdAdd(spec) {
  const survey = fdSheet("survey");
  const rows = survey.rows;
  const chosen = fdState.selected ? fdNodeOf(fdState.selected) : null;
  let at = chosen ? (chosen.children ? chosen.end : chosen.end + 1) : rows.length;
  // Trailing blank rows stay last, so a workbook's spacing is not moved by an add at the end.
  if (!chosen) while (at > 0 && !fdType(rows[at - 1]).base) at--;
  const label = spec.container ? (spec.key === "repeat" ? "Repeat" : "Group") : spec.type === "note" ? "Note" : "Question";
  const name = fdUnique(spec.container ? spec.key : fdSlug(spec.word), fdNames(null));
  const row = [];
  let typeCell = spec.type;
  if (spec.choices) {
    const list = fdUnique(`${name}_list`, new Set(fdSheetMade("choices", ["list_name", "name", "label"]).rows.map(r => fdGet(fdSheet("choices"), r, fdListCol(fdSheet("choices"))))));
    typeCell = `${spec.type} ${list}`;
    fdAddChoice(list, "Choice 1");
    fdAddChoice(list, "Choice 2");
  }
  fdPut(survey, row, "type", typeCell);
  fdPut(survey, row, "name", name);
  fdPut(survey, row, "label", label);
  if (spec.appearance) fdPut(survey, row, "appearance", spec.appearance);
  if (spec.parameters) fdPut(survey, row, "parameters", spec.parameters);
  fdAutoNames.add(row);
  const added = [row];
  if (spec.container) {
    const end = [];
    fdPut(survey, end, "type", spec.key === "repeat" ? "end repeat" : "end group");
    added.push(end);
  }
  rows.splice(at, 0, ...added);
  return row;
}

// ------------------------------------------------------------------------------------------------------ the panel

const FD_TABS = [["add", "Add"], ["edit", "Edit"], ["settings", "Settings"], ["preview", "Preview"]];

function fdSelectTab(tab, focus = true) {
  fdState.tab = tab;
  for (const [key] of FD_TABS) {
    const button = $(`fdTab-${key}`);
    button.setAttribute("aria-selected", String(key === tab));
    button.tabIndex = key === tab ? 0 : -1;
  }
  $("fdPreviewPane").hidden = tab !== "preview";
  $("fdPanel").hidden = tab === "preview";
  if (tab === "add") fdDrawAdd();
  else if (tab === "edit") fdDrawEdit();
  else if (tab === "settings") fdDrawSettings();
  else fdShowPreview();
  if (focus) $(`fdTab-${tab}`).focus();
}

function fdDrawAdd() {
  const panel = $("fdPanel");
  panel.setAttribute("aria-labelledby", "fdTab-add");
  if (fdState.published) {
    panel.innerHTML = `<p class="hint">A published form's questions cannot be changed yet.</p>`;
    return;
  }
  const chosen = fdState.selected ? fdNodeOf(fdState.selected) : null;
  const where = !chosen ? "at the end of the form"
    : chosen.children ? `inside the ${chosen.kind} <b>${h(fdGet(fdSheet("survey"), chosen.row, "label") || fdGet(fdSheet("survey"), chosen.row, "name"))}</b>`
    : `after <b>${h(fdGet(fdSheet("survey"), chosen.row, "label") || fdGet(fdSheet("survey"), chosen.row, "name"))}</b>`;
  panel.innerHTML = `
    <p class="hint" id="fdAddWhere">A new question goes ${where}.</p>
    <div class="fdtypes" role="group" aria-label="Question types">
      ${FD_TYPES.map(t => `<button type="button" class="fdtype" data-fd-add="${t.key}" aria-describedby="fdAddWhere">${h(t.word)}</button>`).join("")}
    </div>`;
}

/** A labelled control in the Edit panel, with the hint under it. */
const fdField = (id, label, control, hint) => `<div class="stacked fdfield"><label for="${id}">${h(label)}</label>${control}${
  hint ? `<p class="hint" id="${id}Says">${hint}</p>` : ""}</div>`;

function fdDrawEdit() {
  const panel = $("fdPanel");
  panel.setAttribute("aria-labelledby", "fdTab-edit");
  const row = fdState.selected;
  const node = row ? fdNodeOf(row) : null;
  if (!node) {
    panel.innerHTML = `<p class="hint">Choose a question in the form to edit it.</p>`;
    return;
  }
  const survey = fdSheet("survey");
  const { base, list } = fdType(row);
  const get = name => fdGet(survey, row, name);
  const ro = fdState.published ? " disabled" : "";
  const isQuestion = node.kind === "question";
  const answers = isQuestion && !["note"].includes(base);
  const parts = [];

  parts.push(`<h3 class="fdedithead" id="fdEditHead" tabindex="-1">${h(isQuestion ? fdTypeWord(base, get("appearance")) : node.kind === "group" ? "Group" : "Repeat")}</h3>`);
  parts.push(fdField("fdLabel", base === "note" ? "Text" : "Label",
    `<textarea id="fdLabel" rows="2" data-fd-cell="label"${ro}>${h(get("label"))}</textarea>`));
  if (answers || !isQuestion) {
    parts.push(fdField("fdHint", "Hint", `<input type="text" id="fdHint" data-fd-cell="hint" value="${h(get("hint"))}"${ro}>`,
      "Shown under the label, in smaller type."));
  }
  parts.push(fdField("fdName", "Field name", `<input type="text" id="fdName" data-fd-cell="name" value="${h(get("name"))}" spellcheck="false" autocomplete="off" aria-describedby="fdNameSays"${ro}>`,
    isQuestion ? "The column its answer is stored in: letters, digits and underscores, starting with a letter. It cannot change once the form is published."
      : "The name the form knows it by."));

  if (list && (base === "select_one" || base === "select_multiple")) parts.push(fdChoicesMarkup(list, ro));
  if (base === "range") parts.push(fdRangeMarkup(get("parameters"), ro));

  if (answers && !["calculate", "hidden"].includes(base)) {
    const required = get("required").trim();
    const plain = !required || /^(yes|no|true|false|true\(\)|false\(\))$/i.test(required);
    parts.push(plain
      ? `<label class="check fdfield"><input type="checkbox" id="fdRequired"${/^(yes|true|true\(\))$/i.test(required) ? " checked" : ""}${ro}> <span>Required</span></label>`
      : fdField("fdRequiredRaw", "Required when", `<input type="text" id="fdRequiredRaw" data-fd-cell="required" value="${h(required)}" spellcheck="false"${ro}>`,
        "An expression: the answer is required while it is true."));
    const readOnly = fdGet(survey, row, fdCol(survey, "readonly") >= 0 && fdCol(survey, "read_only") < 0 ? "readonly" : "read_only");
    parts.push(`<label class="check fdfield"><input type="checkbox" id="fdReadOnly"${/^(yes|true|true\(\))$/i.test(readOnly.trim()) ? " checked" : ""}${ro}> <span>Read only</span></label>`);
    parts.push(fdField("fdDefault", "Default value", `<input type="text" id="fdDefault" data-fd-cell="default" value="${h(get("default"))}" spellcheck="false"${ro}>`,
      base === "select_one" || base === "select_multiple" ? "A choice's value, not its label." : ""));
  }

  if (base === "calculate" || (base === "hidden" && get("calculation"))) {
    parts.push(fdField("fdCalc", "Calculation", `<textarea id="fdCalc" rows="3" data-fd-cell="calculation" spellcheck="false"${ro}>${h(get("calculation"))}</textarea>`,
      "An XLSForm expression, such as <code>${a} + ${b}</code>."));
  }

  const appearances = Object.entries(FD_APPEARANCES).filter(([, types]) => types.includes(base)).map(([a]) => a);
  const appearance = get("appearance").trim();
  if (appearances.length || appearance) {
    const known = !appearance || appearances.includes(appearance);
    parts.push(known
      ? fdField("fdAppearance", "Appearance", `<select id="fdAppearance" data-fd-cell="appearance"${ro}><option value="">Default</option>${appearances.map(a =>
        `<option${a === appearance ? " selected" : ""}>${h(a)}</option>`).join("")}</select>`)
      : fdField("fdAppearance", "Appearance", `<input type="text" id="fdAppearance" data-fd-cell="appearance" value="${h(appearance)}" spellcheck="false"${ro}>`,
        "Written in the workbook; kept as it is."));
  }

  if (base === "begin repeat") {
    parts.push(fdField("fdRepeatCount", "Number of times", `<input type="text" id="fdRepeatCount" data-fd-cell="repeat_count" value="${h(get("repeat_count"))}" spellcheck="false"${ro}>`,
      "Empty lets the person add as many as they need; a number or an expression fixes it."));
  }

  if (answers && ["integer", "decimal", "range", "text"].includes(base)) parts.push(fdLimitsMarkup(row, base, ro));
  parts.push(fdRuleMarkup(row, ro));

  // The cells of this row the designer does not edit, said beside the question they belong to.
  const kept = survey.columns.map((c, i) => [String(c), String(row[i] ?? "")])
    .filter(([c, v]) => v && !fdEdits(c));
  if (kept.length) {
    parts.push(`<div class="fdkept"><p class="hint"><b>Kept as written in the workbook:</b></p><dl>${kept.map(([c, v]) =>
      `<dt><code>${h(c)}</code></dt><dd>${h(v)}</dd>`).join("")}</dl></div>`);
  }
  panel.innerHTML = parts.join("");
}

function fdChoicesMarkup(list, ro) {
  const sheet = fdSheet("choices");
  const rows = fdChoices(list);
  const users = fdListUsers(list);
  return `<fieldset class="fdchoices"><legend>Choices</legend>
    ${users > 1 ? `<p class="hint">This list, <code>${h(list)}</code>, is shared by ${users} questions; a change here changes all of them.</p>` : ""}
    <ol class="fdchoicelist">${rows.map((r, i) => `<li data-fd-choice="${i}">
      <label class="sr-only" for="fdChoiceLabel${i}">Choice ${i + 1} label</label>
      <input type="text" id="fdChoiceLabel${i}" data-fd-choice-cell="label" value="${h(fdGet(sheet, r, "label"))}" placeholder="Label"${ro}>
      <label class="sr-only" for="fdChoiceName${i}">Choice ${i + 1} value</label>
      <input type="text" id="fdChoiceName${i}" class="fdchoicename" data-fd-choice-cell="name" value="${h(fdGet(sheet, r, "name"))}" spellcheck="false" placeholder="Value"${ro}>
      ${ro ? "" : `<button type="button" class="ghost tiny fdicon" data-fd-choice-act="up" aria-label="Move choice ${i + 1} up"${i === 0 ? " disabled" : ""}>↑</button>
      <button type="button" class="ghost tiny fdicon" data-fd-choice-act="down" aria-label="Move choice ${i + 1} down"${i === rows.length - 1 ? " disabled" : ""}>↓</button>
      <button type="button" class="ghost tiny fdicon" data-fd-choice-act="delete" aria-label="Delete choice ${i + 1}"${rows.length === 1 ? " disabled" : ""}>✕</button>`}
    </li>`).join("")}</ol>
    ${ro ? "" : `<button type="button" id="fdChoiceAdd">Add a choice</button>`}
    <p class="hint">The label is what people see; the value is what is stored.</p>
  </fieldset>`;
}

function fdRangeMarkup(parameters, ro) {
  const p = Object.fromEntries(String(parameters || "").split(/\s+/).filter(Boolean).map(x => x.split("=")));
  const box = (key, label) => `<div class="stacked"><label for="fdRange-${key}">${label}</label><input type="number" id="fdRange-${key}" data-fd-range="${key}" value="${h(p[key] ?? "")}"${ro}></div>`;
  return `<fieldset class="fdrange"><legend>Scale</legend><div class="fdrow3">${box("start", "From")}${box("end", "To")}${box("step", "Step")}</div></fieldset>`;
}

function fdLimitsMarkup(row, base, ro) {
  const survey = fdSheet("survey");
  const constraint = fdGet(survey, row, "constraint");
  const limits = fdState.rawLimits.has(row) ? null : fdParseLimits(constraint, base);
  const message = fdField("fdConstraintMessage", "Message when it is not met",
    `<input type="text" id="fdConstraintMessage" data-fd-cell="constraint_message" value="${h(fdGet(survey, row, "constraint_message"))}"${ro}>`);
  if (!limits) {
    return `<fieldset class="fdlimits"><legend>Validation</legend>
      ${fdField("fdConstraint", "Accepted when", `<textarea id="fdConstraint" rows="2" data-fd-cell="constraint" spellcheck="false"${ro}>${h(constraint)}</textarea>`,
        "An XLSForm expression; <code>.</code> is this question's answer.")}${message}</fieldset>`;
  }
  const inputs = base === "text"
    ? `<div class="stacked"><label for="fdLimitLength">Longest answer, in characters</label><input type="number" min="1" id="fdLimitLength" data-fd-limit="length" value="${h(limits.length)}"${ro}></div>`
    : `<div class="fdrow3"><div class="stacked"><label for="fdLimitMin">Smallest</label><input type="number" id="fdLimitMin" data-fd-limit="min" value="${h(limits.min)}"${ro}></div>
       <div class="stacked"><label for="fdLimitMax">Largest</label><input type="number" id="fdLimitMax" data-fd-limit="max" value="${h(limits.max)}"${ro}></div></div>`;
  return `<fieldset class="fdlimits"><legend>Validation</legend>${inputs}${message}
    ${ro ? "" : `<button type="button" class="linkbtn" data-fd-raw="limits">Write it as an expression instead</button>`}</fieldset>`;
}

function fdRuleMarkup(row, ro) {
  const survey = fdSheet("survey");
  const relevant = fdGet(survey, row, "relevant");
  const rule = fdState.rawRule.has(row) ? null : fdParseRule(relevant);
  const sources = fdRuleSources(row);
  if (!rule) {
    return `<fieldset class="fdrules"><legend>Show this only when</legend>
      ${fdField("fdRelevant", "Shown when", `<textarea id="fdRelevant" rows="2" data-fd-cell="relevant" spellcheck="false"${ro}>${h(relevant)}</textarea>`,
        "An XLSForm expression, such as <code>${damage} = 'yes'</code>. Empty shows it always.")}</fieldset>`;
  }
  const sourceLabel = n => fdGet(survey, n.row, "label") || fdGet(survey, n.row, "name");
  const line = (c, i) => {
    const source = sources.find(n => fdGet(survey, n.row, "name") === c.q);
    const t = source ? fdType(source.row) : { base: "text" };
    const kind = fdKindOf(t.base);
    const needsValue = !["answered", "empty"].includes(c.op);
    const choices = t.list && (kind === "choice" || kind === "multiple") ? fdChoices(t.list) : null;
    const value = !needsValue ? "" : choices
      ? `<select id="fdRuleV${i}" data-fd-rule="v" data-fd-at="${i}" aria-label="Value of condition ${i + 1}"${ro}>${choices.map(r => {
        const v = fdGet(fdSheet("choices"), r, "name");
        return `<option value="${h(v)}"${v === c.v ? " selected" : ""}>${h(fdGet(fdSheet("choices"), r, "label") || v)}</option>`;
      }).join("")}</select>`
      : `<input type="${kind === "number" ? "number" : "text"}" id="fdRuleV${i}" data-fd-rule="v" data-fd-at="${i}" value="${h(c.v)}" aria-label="Value of condition ${i + 1}"${ro}>`;
    return `<li class="fdruleline">
      <select data-fd-rule="q" data-fd-at="${i}" aria-label="Question of condition ${i + 1}"${ro}>${sources.map(n => {
        const name = fdGet(survey, n.row, "name");
        return `<option value="${h(name)}"${name === c.q ? " selected" : ""}>${h(sourceLabel(n))}</option>`;
      }).join("")}${source ? "" : `<option value="${h(c.q)}" selected>${h(c.q)} (not before this question)</option>`}</select>
      <select data-fd-rule="op" data-fd-at="${i}" aria-label="Comparison of condition ${i + 1}"${ro}>${FD_OPERATORS[kind].map(([v, w]) =>
        `<option value="${h(v)}"${v === c.op ? " selected" : ""}>${h(w)}</option>`).join("")}</select>
      ${value}
      ${ro ? "" : `<button type="button" class="ghost tiny fdicon" data-fd-rule-del="${i}" aria-label="Remove condition ${i + 1}">✕</button>`}
    </li>`;
  };
  return `<fieldset class="fdrules"><legend>Show this only when</legend>
    ${rule.conditions.length > 1 ? `<div class="fdrulejoin"><label for="fdRuleAll">Conditions that must hold</label>
      <select id="fdRuleAll"${ro}><option value="all"${rule.all ? " selected" : ""}>All of them</option><option value="any"${rule.all ? "" : " selected"}>Any of them</option></select></div>` : ""}
    ${rule.conditions.length ? `<ol class="fdrulelist">${rule.conditions.map(line).join("")}</ol>`
      : `<p class="hint">Always shown.</p>`}
    ${ro ? "" : sources.length ? `<button type="button" id="fdRuleAdd">Add a condition</button>`
      : `<p class="hint">A rule asks about a question before this one; there is none yet.</p>`}
    ${ro ? "" : `<button type="button" class="linkbtn" data-fd-raw="rule">Write it as an expression instead</button>`}
  </fieldset>`;
}

/** The conditions as the builder shows them now, read from its controls. */
function fdRuleFromControls() {
  const conditions = [];
  for (const q of document.querySelectorAll('#fdPanel [data-fd-rule="q"]')) {
    const i = q.dataset.fdAt;
    const op = document.querySelector(`#fdPanel [data-fd-rule="op"][data-fd-at="${i}"]`)?.value || "answered";
    const v = document.querySelector(`#fdPanel [data-fd-rule="v"][data-fd-at="${i}"]`)?.value ?? "";
    conditions.push({ q: q.value, op, v });
  }
  return { all: ($("fdRuleAll")?.value || "all") === "all", conditions };
}

function fdWriteRule(rule) {
  const text = rule.conditions.map(fdConditionText).join(rule.all ? " and " : " or ");
  fdPut(fdSheet("survey"), fdState.selected, "relevant", text);
}

function fdDrawSettings() {
  const panel = $("fdPanel");
  panel.setAttribute("aria-labelledby", "fdTab-settings");
  const settings = fdSheet("settings");
  const row = settings.rows[0];
  const get = name => fdGet(settings, row, name);
  const ro = fdState.published ? " disabled" : "";
  panel.innerHTML = `
    ${fdField("fdSetTitle", "Form title", `<input type="text" id="fdSetTitle" data-fd-setting="form_title" value="${h(get("form_title"))}"${ro}>`,
      "Shown at the top of the form. The item keeps its own title, changed on its page.")}
    ${fdField("fdSetId", "Layer name", `<input type="text" id="fdSetId" data-fd-setting="form_id" value="${h(get("form_id"))}" spellcheck="false"${ro}>`,
      "XLSForm's <code>form_id</code>: the name of the layer its answers go to. Empty names it after the item's title.")}
    ${fdField("fdSetVersion", "Version", `<input type="text" id="fdSetVersion" data-fd-setting="version" value="${h(get("version"))}" spellcheck="false"${ro}>`,
      "Recorded with every answer, so a change of form can be told apart in the results.")}
    ${fdField("fdSetLanguage", "Default language", `<input type="text" id="fdSetLanguage" data-fd-setting="default_language" value="${h(get("default_language"))}" spellcheck="false"${ro}>`,
      "As the workbook writes it, such as <code>Türkçe (tr)</code>. A Turkish form's buttons are in Turkish.")}
    <label class="check fdfield"><input type="checkbox" id="fdSetPages"${/(^|\s)pages(\s|$)/.test(get("style")) ? " checked" : ""}${ro}>
      <span>Show each group as its own page</span></label>
    ${fdField("fdSetInstance", "Record name", `<input type="text" id="fdSetInstance" data-fd-setting="instance_name" value="${h(get("instance_name"))}" spellcheck="false"${ro}>`,
      "XLSForm's <code>instance_name</code>: an expression naming each record in the outbox, such as <code>concat(${building}, ' ', ${floor})</code>.")}`;
}

// ------------------------------------------------------------------------------------------------- check and preview

function fdScheduleCheck(delay) {
  clearTimeout(fdState.checkTimer);
  fdState.checkTimer = setTimeout(fdRunCheck, delay);
}

/** Checks the model as it stands, through the server's own check; the answer feeds the problem list and the preview. */
async function fdRunCheck() {
  const state = fdState;
  const ticket = ++state.checking;
  let check;
  try {
    check = await api(`/admin/forms/${encodeURIComponent(state.id)}/check`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ model: state.model }),
    });
  } catch (e) {
    check = { accepted: false, refusals: e.body?.refusals || [{ message: e.message || String(e) }], notices: [] };
  }
  if (fdState !== state || ticket !== state.checking) return;
  check = { accepted: !!check?.accepted, refusals: check?.refusals || [], notices: check?.notices || [], form: check?.form || null };
  state.check = check;
  if (check.accepted && check.form) state.lastForm = check.form;
  fdDrawProblems();
  fdDrawCanvas();
  fdSendPreview();
  $("fdPreviewSays").textContent = check.accepted ? "Up to date with your changes."
    : `Showing the last version that passed the check: ${check.refusals.length} ${check.refusals.length === 1 ? "thing needs" : "things need"} changing.`;
}

/** The check's refusals as a list, each with a way to the question it is about. */
function fdDrawProblems() {
  const box = $("fdProblems");
  const refusals = fdState.check?.refusals || [];
  box.hidden = !refusals.length;
  if (!refusals.length) { box.innerHTML = ""; return; }
  const rows = fdSheet("survey").rows;
  box.innerHTML = `<h3>${refusals.length === 1 ? "One thing needs changing" : `${refusals.length} things need changing`} before this form can be saved</h3>
    <ul>${refusals.map(r => {
      const row = String(r.sheet || "").toLowerCase() === "survey" && r.row >= 2 ? rows[r.row - 2] : null;
      const name = row ? fdGet(fdSheet("survey"), row, "label") || fdGet(fdSheet("survey"), row, "name") : null;
      return `<li>${name ? `<button type="button" class="linkbtn" data-fd-goto="${fdKey(row)}">${h(name)}</button>: ` : r.sheet ? `${h(r.sheet)}${r.row ? ` row ${r.row}` : ""}: ` : ""}${
        r.column ? `<code>${h(r.column)}</code> — ` : ""}${h(r.message || "")}</li>`;
    }).join("")}</ul>`;
}

function fdShowPreview() {
  const frame = $("fdPreviewFrame");
  if (!frame.getAttribute("src")) frame.setAttribute("src", "/forms/preview");
  fdSendPreview();
}

function fdSendPreview() {
  const frame = $("fdPreviewFrame");
  if (!fdState?.previewReady || !fdState.lastForm || !frame.contentWindow) return;
  const settings = fdSheet("settings");
  frame.contentWindow.postMessage({
    type: "graticula-form-preview",
    itemId: fdState.id,
    title: fdGet(settings, settings.rows[0], "form_title") || fdState.title,
    form: fdState.lastForm,
  }, location.origin);
}

window.addEventListener("message", event => {
  if (event.origin !== location.origin || !fdState) return;
  if (event.source !== $("fdPreviewFrame")?.contentWindow) return;
  if (event.data?.type === "graticula-form-preview-ready") { fdState.previewReady = true; fdSendPreview(); }
  if (event.data?.type === "graticula-form-preview-drawn") $("fdPreviewFrame").dataset.drawn = String(Date.now());
});

// ----------------------------------------------------------------------------------------------------- saving

async function fdSave() {
  const state = fdState;
  const button = $("fdSave");
  button.disabled = true;
  fdSay("Saving…");
  try {
    const saved = await api(`/admin/forms/${encodeURIComponent(state.id)}/model`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(state.model),
    });
    if (fdState !== state) return;
    state.dirty = false;
    fdKept.delete(state.id);
    fdDrawBar();
    const notes = (saved?.notices || []).length;
    fdSay(`Saved.${notes ? ` The check left ${notes} ${notes === 1 ? "note" : "notes"}, listed on the form's page.` : ""}`);
  } catch (e) {
    if (fdState !== state) return;
    if (e.body?.refusals?.length) {
      state.check = { accepted: false, refusals: e.body.refusals, notices: e.body.notices || [] };
      fdDrawProblems();
      fdDrawCanvas();
    }
    fdSay(`Not saved: ${lowerFirst(e.message || String(e))}`);
    fdDrawBar();
    $("fdProblems").hidden ? button.focus() : $("fdProblems").querySelector("button, h3")?.focus();
  }
}

window.addEventListener("beforeunload", event => {
  if (fdState?.dirty && $("view-formdesign")?.classList.contains("on")) { event.preventDefault(); event.returnValue = ""; }
});

// ------------------------------------------------------------------------------------------------- the events

function fdPick(row, tab = "edit") {
  fdState.selected = row;
  fdDrawCanvas();
  fdSelectTab(tab, false);
}

document.addEventListener("click", event => {
  if (!fdState || !(event.target instanceof Element) || !event.target.closest("#view-formdesign")) return;
  const t = event.target.closest("button");
  if (!t) return;

  if (t.id === "fdSave") { fdSave(); return; }
  const tab = FD_TABS.find(([key]) => t.id === `fdTab-${key}`);
  if (tab) { fdSelectTab(tab[0]); return; }

  if (t.dataset.fdAdd) {
    const spec = FD_TYPES.find(x => x.key === t.dataset.fdAdd);
    const row = fdAdd(spec);
    fdState.selected = row;
    fdChanged();
    fdSelectTab("edit", false);
    fdSay(`${spec.word} added. Its label is selected for typing.`);
    $("fdLabel")?.focus();
    $("fdLabel")?.select();
    return;
  }

  if (t.dataset.fdGoto) {
    const row = fdSheet("survey").rows.find(r => fdKey(r) === t.dataset.fdGoto);
    if (row) { fdPick(row); $("fdEditHead")?.focus(); }
    return;
  }

  const card = t.closest("[data-fd-key]");
  if (card && t.dataset.fdAct) {
    const row = fdSheet("survey").rows.find(r => fdKey(r) === card.dataset.fdKey);
    const node = row ? fdNodeOf(row) : null;
    if (!node) return;
    const name = fdGet(fdSheet("survey"), row, "label") || fdGet(fdSheet("survey"), row, "name");
    if (t.dataset.fdAct === "pick") { fdPick(row); return; }
    if (t.dataset.fdAct === "up" || t.dataset.fdAct === "down") {
      if (fdMove(node, t.dataset.fdAct === "up" ? -1 : 1)) { fdChanged(); fdSay(`Moved ${name} ${t.dataset.fdAct}.`); }
      return;
    }
    if (t.dataset.fdAct === "copy") {
      fdState.selected = fdDuplicate(node);
      fdChanged();
      fdSelectTab("edit", false);
      fdSay(`${name} duplicated; the copy is selected.`);
      return;
    }
    if (t.dataset.fdAct === "delete") {
      const all = fdWalk(fdTree());
      const at = all.indexOf(all.find(n => n.row === row));
      fdDelete(node);
      fdState.selected = null;
      fdChanged();
      fdSelectTab("add", false);
      fdSay(`${name} deleted.`);
      const next = fdWalk(fdTree())[Math.max(0, at - 1)];
      (next ? $("fdCanvas").querySelector(`[data-fd-key="${fdKey(next.row)}"] [data-fd-act="pick"]`) : $("fdTab-add"))?.focus();
      return;
    }
  }

  // Choices.
  if (t.id === "fdChoiceAdd") {
    const { list } = fdType(fdState.selected);
    fdAddChoice(list, `Choice ${fdChoices(list).length + 1}`);
    fdChanged();
    fdDrawEdit();
    const inputs = document.querySelectorAll('#fdPanel [data-fd-choice-cell="label"]');
    inputs[inputs.length - 1]?.focus();
    inputs[inputs.length - 1]?.select();
    return;
  }
  if (t.dataset.fdChoiceAct) {
    const { list } = fdType(fdState.selected);
    const own = fdChoices(list);
    const i = Number(t.closest("[data-fd-choice]").dataset.fdChoice);
    const rows = fdSheet("choices").rows;
    const row = own[i];
    if (t.dataset.fdChoiceAct === "delete") rows.splice(rows.indexOf(row), 1);
    else {
      const other = own[i + (t.dataset.fdChoiceAct === "up" ? -1 : 1)];
      const a = rows.indexOf(row), b = rows.indexOf(other);
      rows[a] = other; rows[b] = row;
    }
    fdChanged();
    fdDrawEdit();
    const target = t.dataset.fdChoiceAct === "delete" ? Math.max(0, i - 1) : i + (t.dataset.fdChoiceAct === "up" ? -1 : 1);
    document.querySelector(`#fdPanel [data-fd-choice="${target}"] [data-fd-choice-act="${t.dataset.fdChoiceAct === "delete" ? "delete" : t.dataset.fdChoiceAct}"]:not([disabled])`)?.focus()
      || document.querySelector(`#fdPanel [data-fd-choice="${target}"] input`)?.focus();
    return;
  }

  // Rules and limits.
  if (t.id === "fdRuleAdd") {
    const rule = fdRuleFromControls();
    const source = fdRuleSources(fdState.selected).at(-1);
    const name = fdGet(fdSheet("survey"), source.row, "name");
    const kind = fdKindOf(fdType(source.row).base);
    const first = fdType(source.row).list ? fdGet(fdSheet("choices"), fdChoices(fdType(source.row).list)[0] || [], "name") : "";
    rule.conditions.push({ q: name, op: FD_OPERATORS[kind][0][0], v: first });
    fdWriteRule(rule);
    fdChanged();
    fdDrawEdit();
    document.querySelectorAll('#fdPanel [data-fd-rule="q"]').item(rule.conditions.length - 1)?.focus();
    return;
  }
  if (t.dataset.fdRuleDel !== undefined) {
    const rule = fdRuleFromControls();
    rule.conditions.splice(Number(t.dataset.fdRuleDel), 1);
    fdWriteRule(rule);
    fdChanged();
    fdDrawEdit();
    ($("fdRuleAdd") || $("fdEditHead"))?.focus();
    return;
  }
  if (t.dataset.fdRaw) {
    (t.dataset.fdRaw === "rule" ? fdState.rawRule : fdState.rawLimits).add(fdState.selected);
    fdDrawEdit();
    $(t.dataset.fdRaw === "rule" ? "fdRelevant" : "fdConstraint")?.focus();
  }
});

/** Typing in the Edit and Settings panels writes the cell at once; the canvas and the check follow. */
document.addEventListener("input", event => {
  if (!fdState || !(event.target instanceof Element) || !event.target.closest("#fdPanel")) return;
  const t = event.target;
  const survey = fdSheet("survey");
  const row = fdState.selected;

  if (t.dataset.fdCell && row) {
    const cell = t.dataset.fdCell;
    fdPut(survey, row, cell, t.value);
    if (cell === "name") fdAutoNames.delete(row);
    if (cell === "label" && fdAutoNames.has(row) && fdNodeOf(row)?.kind === "question") {
      const before = fdGet(survey, row, "name");
      const name = fdUnique(fdSlug(t.value), fdNames(row));
      fdPut(survey, row, "name", name);
      if ($("fdName")) $("fdName").value = name;
      fdRenameOwnList(row, before, name);
    }
    fdChanged();
    return;
  }

  if (t.dataset.fdChoiceCell && row) {
    const { list } = fdType(row);
    const choice = fdChoices(list)[Number(t.closest("[data-fd-choice]").dataset.fdChoice)];
    const sheet = fdSheet("choices");
    fdPut(sheet, choice, t.dataset.fdChoiceCell, t.value);
    if (t.dataset.fdChoiceCell === "name") fdAutoNames.delete(choice);
    if (t.dataset.fdChoiceCell === "label" && fdAutoNames.has(choice)) {
      const taken = new Set(fdChoices(list).filter(r => r !== choice).map(r => fdGet(sheet, r, "name")));
      const value = fdUnique(fdSlug(t.value), taken);
      fdPut(sheet, choice, "name", value);
      const box = t.closest("li").querySelector('[data-fd-choice-cell="name"]');
      if (box) box.value = value;
    }
    fdChanged(false);
    return;
  }

  if (t.dataset.fdRange && row) {
    const keys = ["start", "end", "step"];
    const values = Object.fromEntries(keys.map(k => [k, $(`fdRange-${k}`)?.value ?? ""]));
    fdPut(survey, row, "parameters", keys.filter(k => values[k] !== "").map(k => `${k}=${values[k]}`).join(" "));
    fdChanged(false);
    return;
  }

  if (t.dataset.fdLimit && row) {
    const base = fdType(row).base;
    const limits = { min: $("fdLimitMin")?.value ?? "", max: $("fdLimitMax")?.value ?? "", length: $("fdLimitLength")?.value ?? "" };
    fdPut(survey, row, "constraint", fdLimitsText(base, limits));
    fdChanged(false);
    return;
  }

  if (t.dataset.fdRule === "v" && row) {
    fdWriteRule(fdRuleFromControls());
    fdChanged();
    return;
  }

  if (t.dataset.fdSetting) {
    const settings = fdSheet("settings");
    fdPut(settings, settings.rows[0], t.dataset.fdSetting, t.value);
    fdChanged(t.dataset.fdSetting === "form_title");
  }
});

/** Checkboxes and selects, whose change is the edit. */
document.addEventListener("change", event => {
  if (!fdState || !(event.target instanceof Element) || !event.target.closest("#fdPanel")) return;
  const t = event.target;
  const survey = fdSheet("survey");
  const row = fdState.selected;

  if (t.id === "fdRequired" && row) { fdPut(survey, row, "required", t.checked ? "yes" : ""); fdChanged(); return; }
  if (t.id === "fdReadOnly" && row) {
    const column = fdCol(survey, "readonly") >= 0 && fdCol(survey, "read_only") < 0 ? "readonly" : "read_only";
    fdPut(survey, row, column, t.checked ? "yes" : "");
    fdChanged();
    return;
  }
  if (t.id === "fdAppearance" && t.tagName === "SELECT" && row) { fdPut(survey, row, "appearance", t.value); fdChanged(); return; }
  if ((t.dataset.fdRule === "q" || t.dataset.fdRule === "op" || t.id === "fdRuleAll") && row) {
    const rule = fdRuleFromControls();
    // A new question or comparison may want a different kind of value; the panel is drawn again to offer it.
    if (t.dataset.fdRule === "q") {
      const c = rule.conditions[Number(t.dataset.fdAt)];
      const source = fdRuleSources(row).find(n => fdGet(survey, n.row, "name") === c.q);
      const kind = fdKindOf(source ? fdType(source.row).base : "text");
      if (!FD_OPERATORS[kind].some(([v]) => v === c.op)) c.op = FD_OPERATORS[kind][0][0];
      const list = source && fdType(source.row).list;
      if (list && (kind === "choice" || kind === "multiple")) c.v = fdGet(fdSheet("choices"), fdChoices(list)[0] || [], "name");
    }
    fdWriteRule(rule);
    fdChanged();
    const focusId = t.dataset.fdAt !== undefined ? `[data-fd-rule="${t.dataset.fdRule}"][data-fd-at="${t.dataset.fdAt}"]` : "#fdRuleAll";
    fdDrawEdit();
    document.querySelector(`#fdPanel ${focusId}`)?.focus();
    return;
  }
  if (t.id === "fdSetPages") {
    const settings = fdSheet("settings");
    const style = fdGet(settings, settings.rows[0], "style").split(/\s+/).filter(s => s && s !== "pages");
    if (t.checked) style.push("pages");
    fdPut(settings, settings.rows[0], "style", style.join(" "));
    fdChanged(false);
  }
});

/** Arrow keys move between the panel's tabs, as a tab list's do. */
document.addEventListener("keydown", event => {
  if (!fdState || !(event.target instanceof Element) || event.target.getAttribute("role") !== "tab" || !event.target.closest("#fdTabs")) return;
  const keys = FD_TABS.map(([key]) => key);
  const at = keys.indexOf(fdState.tab);
  const to = event.key === "ArrowRight" ? (at + 1) % keys.length : event.key === "ArrowLeft" ? (at + keys.length - 1) % keys.length
    : event.key === "Home" ? 0 : event.key === "End" ? keys.length - 1 : -1;
  if (to < 0) return;
  event.preventDefault();
  fdSelectTab(keys[to]);
});

/** New item › Form › Design a blank form: the item is made with an empty model and the designer opens on it. */
async function fdCreateBlank(button) {
  const says = $("formNewSays");
  const title = $("formNewTitle").value.trim();
  if (!title) {
    says.textContent = "Give the form a title first; a blank form has no workbook to take one from.";
    says.classList.add("bad-inline");
    $("formNewTitle").setAttribute("aria-invalid", "true");
    $("formNewTitle").focus();
    return;
  }
  button.disabled = true;
  says.classList.remove("bad-inline");
  says.textContent = `Making ${title}…`;
  try {
    const made = await api("/admin/forms", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title, model: fdBlankModel(title) }),
    });
    $("addItem").close();
    navigateTo(`#/item/${encodeURIComponent(made.id)}/design`);
  } catch (e) {
    button.disabled = false;
    says.textContent = `Not made: ${lowerFirst(e.message || String(e))}`;
    says.classList.add("bad-inline");
  }
}

document.addEventListener("click", event => {
  const t = event.target instanceof Element ? event.target.closest("#formBlank") : null;
  if (t) fdCreateBlank(t);
});
