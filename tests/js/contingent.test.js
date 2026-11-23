// The check Studio's editors run before a save — ADR-312 §5.6. Run with `node --test tests/js/contingent.test.js`.
// It loads src/Graticula.Host/wwwroot/contingent.js exactly as the browser does, through its CommonJS tail.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const cv = require(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "contingent.js"));

// The resource as the conformance test reads it back from a running server (ContingentValuesTests), trimmed: the shape
// an ArcGIS server was observed to serve (ADR-314 §5.5).
const resource = {
  typeCodes: ["Unknown", "Any", "Null", "Code", "Range"],
  stringDicts: [{ domain: "Makes", entries: ["AIR", "BOE"] }],
  contingentValuesDefinition: {
    layerID: 0, layerName: "planes", geometryType: "esriGeometryPoint", hasSubType: false,
    fieldGroups: [
      {
        name: "make_model", restrictive: true,
        fields: [
          { id: 0, name: "make", fieldType: "esriFieldTypeString" },
          { id: 1, name: "model", fieldType: "esriFieldTypeInteger" },
          { id: 2, name: "size", fieldType: "esriFieldTypeDouble" },
        ],
        domains: { make: "Makes" },
        contingentValues: [
          { id: 1, types: [3, 3, 4], values: [0, 320, { range: [1.5, 4] }] },
          { id: 2, types: [3, 1, 2], values: [1] },
        ],
      },
      { name: "loose", restrictive: false, fields: [{ id: 0, name: "rating", fieldType: "esriFieldTypeString" }], domains: {}, contingentValues: [] },
    ],
  },
};

test("the resource is decoded: a text code through its dictionary, a range from one slot, Any and Null from none", () => {
  const d = cv.cvFromRest(resource);
  assert.deepEqual(d.groups.map(g => g.name), ["make_model", "loose"]);
  assert.equal(d.groups[1].restrictive, false);
  assert.deepEqual(d.values[0].entries, [
    { field: "make", kind: "CODED_VALUE", code: "AIR" },
    { field: "model", kind: "CODED_VALUE", code: 320 },
    { field: "size", kind: "RANGE", min: 1.5, max: 4 },
  ]);
  assert.deepEqual(d.values[1].entries, [
    { field: "make", kind: "CODED_VALUE", code: "BOE" },
    { field: "model", kind: "ANY" },
    { field: "size", kind: "NULL" },
  ]);
  assert.equal(d.values[0].subtype, null);
  assert.equal(d.values[0].retired, false);
});

test("a retired value served as retired: 1 is read as retired, and subtypes and ADR-312's two-slot range still read", () => {
  const d = cv.cvFromRest({
    stringDicts: [],
    contingentValuesDefinition: {
      hasSubType: true,
      fieldGroups: [{
        name: "g", restrictive: true,
        fields: [{ id: 0, name: "model", fieldType: "esriFieldTypeInteger" }, { id: 1, name: "size", fieldType: "esriFieldTypeDouble" }],
        subTypes: [{ code: 2, name: "Light", domains: {}, contingentValues: [
          { id: 7, retired: 1, types: [3, 4], values: [707, 1, 2] },
        ] }],
      }],
    },
  });
  assert.equal(d.values[0].retired, true);
  assert.equal(d.values[0].subtype, 2);
  assert.deepEqual(d.values[0].entries[1], { field: "size", kind: "RANGE", min: 1, max: 2 });
});

test("an allowed combination says nothing, and a broken one names its values and its group", () => {
  const d = cv.cvFromRest(resource);
  assert.deepEqual(cv.cvViolations(d, { make: "AIR", model: 320, size: 2 }, null), []);
  assert.deepEqual(cv.cvViolations(d, { make: "BOE", model: 9, size: null }, null), []);

  const broken = cv.cvViolations(d, { make: "AIR", model: 999, size: 2 }, null);
  assert.equal(broken.length, 1);
  assert.equal(broken[0].group, "make_model");
  assert.equal(broken[0].retired, false);
});

test("a range is inclusive at both ends, and a value outside it is broken", () => {
  const d = cv.cvFromRest(resource);
  assert.equal(cv.cvViolations(d, { make: "AIR", model: 320, size: 1.5 }, null).length, 0);
  assert.equal(cv.cvViolations(d, { make: "AIR", model: 320, size: 4 }, null).length, 0);
  assert.equal(cv.cvViolations(d, { make: "AIR", model: 320, size: 4.01 }, null).length, 1);
});

test("Null allows only a null, and a control's text and the server's number are one value", () => {
  const d = cv.cvFromRest(resource);
  assert.equal(cv.cvViolations(d, { make: "BOE", model: 1, size: 3 }, null).length, 1);
  assert.equal(cv.cvViolations(d, { make: "AIR", model: "320", size: "2" }, null).length, 0);
});

test("grapy's unrestricted cases: all fields null, a non-restrictive group, a subtype with no values", () => {
  const d = cv.cvFromRest(resource);
  assert.deepEqual(cv.cvViolations(d, { make: null, model: null, size: null }, null), []);
  assert.deepEqual(cv.cvViolations(d, { rating: "anything" }, null), []);

  const typed = cv.cvFromStored({
    fieldGroups: [{ name: "g", fields: ["a"] }],
    contingentValues: [{ id: 1, fieldGroup: "g", subtype: 1, entries: [["a", "CODED_VALUE", 5]] }],
  });
  assert.equal(cv.cvViolations(typed, { a: 6 }, 1).length, 1);
  assert.equal(cv.cvViolations(typed, { a: 6 }, 2).length, 0, "subtype 2 has no values, so it is not restricted");
  assert.equal(cv.cvViolations(typed, { a: 5 }, 1).length, 0);
});

test("a retired value allows a feature that already held it, and no new or changed one", () => {
  const d = cv.cvFromStored({
    fieldGroups: [{ name: "g", restrictive: true, fields: ["a", "b"] }],
    contingentValues: [
      { id: 1, fieldGroup: "g", entries: [["a", "CODED_VALUE", 1], ["b", "CODED_VALUE", 1]] },
      { id: 2, fieldGroup: "g", retired: true, entries: [["a", "CODED_VALUE", 2], ["b", "ANY", null]] },
    ],
  });
  assert.equal(cv.cvViolations(d, { a: 2, b: 7 }, null, { a: 2, b: 7 }).length, 0, "unchanged");
  assert.equal(cv.cvViolations(d, { a: 2, b: 8 }, null, { a: 2, b: 7 }).length, 1, "changed");
  assert.equal(cv.cvViolations(d, { a: 2, b: 7 }, null, null).length, 1, "new");
});

test("field names are matched without regard to case, as a column name is", () => {
  const d = cv.cvFromRest(resource);
  assert.equal(cv.cvViolations(d, { MAKE: "AIR", Model: 999, SIZE: 2 }, null).length, 1);
  assert.equal(cv.cvViolations(d, { MAKE: "AIR", Model: 320, SIZE: 2 }, null).length, 0);
});

test("the stored shape survives a round trip through Studio's editor", () => {
  const stored = {
    fieldGroups: [{ name: "g", restrictive: false, fields: ["a", "b"] }],
    contingentValues: [{ id: 4, fieldGroup: "g", subtype: null, retired: true,
      entries: [["a", "RANGE", [1, 2]], ["b", "CODED_VALUE", "x"]] }],
  };
  assert.deepEqual(cv.cvToStored(cv.cvFromStored(stored)), stored);

  const fresh = cv.cvToStored({ groups: [{ name: "g", fields: ["a"] }], values: [{ group: "g", entries: [{ field: "a", kind: "NULL" }] }] });
  assert.equal(fresh.contingentValues[0].id, undefined, "a new value has no id; the server gives it one");
  assert.deepEqual(fresh.contingentValues[0].entries, [["a", "NULL", null]]);
});

// ---- the copy: aliases and names, what IS allowed, no group name, retired said as retired (review finding 4) ----

const look = {
  alias: f => ({ make: "Make", model: "Model", size: "Size" })[f] || f,
  coded: f => f === "make",
  name: (f, v) => f === "make" ? ({ BOE: "Boeing", AIR: "Airbus" })[v] || String(v) : String(v),
};

const planes = cv.cvFromStored({
  fieldGroups: [{ name: "make_model", restrictive: true, fields: ["make", "model"] }],
  contingentValues: [
    { id: 1, fieldGroup: "make_model", entries: [["make", "CODED_VALUE", "BOE"], ["model", "CODED_VALUE", 737]] },
    { id: 2, fieldGroup: "make_model", entries: [["make", "CODED_VALUE", "BOE"], ["model", "CODED_VALUE", 747]] },
    { id: 3, fieldGroup: "make_model", entries: [["make", "CODED_VALUE", "AIR"], ["model", "CODED_VALUE", 320]] },
    { id: 4, fieldGroup: "make_model", retired: true, entries: [["make", "CODED_VALUE", "BOE"], ["model", "CODED_VALUE", 707]] },
  ],
});

test("a broken combination is said with the names the controls show and what the first value allows", () => {
  const said = cv.cvSentences(planes, { make: "BOE", model: 999 }, null, null, look);
  assert.equal(said.lead, "of Make and Model");
  assert.equal(said.detail, "Boeing allows Model 737 or 747.");
  assert.doesNotMatch(said.detail + said.lead, /make_model|field group|BOE/);
  assert.equal(cv.cvWarning(said), "Not a listed combination of Make and Model. Boeing allows Model 737 or 747. You can still save.");
  assert.match(cv.cvWarning(said, true), /Saved anyway\.$/);
});

test("the live lead changes only when the set of broken groups does, not with each keystroke", () => {
  const a = cv.cvSentences(planes, { make: "BOE", model: 9 }, null, null, look);
  const b = cv.cvSentences(planes, { make: "BOE", model: 99 }, null, null, look);
  const c = cv.cvSentences(planes, { make: "BOE", model: 999 }, null, null, look);
  assert.equal(a.key, "make_model");
  assert.equal(a.key, b.key);
  assert.equal(b.key, c.key);
  assert.equal(a.lead, c.lead);
  assert.equal(cv.cvSentences(planes, { make: "BOE", model: 747 }, null, null, look).key, "");
});

test("a retired combination gets its own sentence, not 'not allowed'", () => {
  const said = cv.cvSentences(planes, { make: "BOE", model: 707 }, null, null, look);
  assert.equal(said.broken[0].retired, true);
  assert.equal(said.detail, "Boeing with Model 707 is retired: features that have it keep it, and it is not offered for new edits.");
  assert.equal(cv.cvSentences(planes, { make: "BOE", model: 707 }, null, { make: "BOE", model: 707 }, look).key, "", "kept unchanged");
});

test("a first value no combination lists is said so", () => {
  const said = cv.cvSentences(planes, { make: "XXX", model: 1 }, null, null, look);
  assert.equal(said.detail, "No listed combination has Make XXX.");
});

test("ranges, Any and Null are said in words", () => {
  const d = cv.cvFromRest(resource);
  const said = cv.cvSentences(d, { make: "AIR", model: 1, size: 2 }, null, null, look);
  assert.equal(said.detail, "Airbus allows Model 320 and Size from 1.5 to 4.");
  const boe = cv.cvSentences(d, { make: "BOE", model: 1, size: 3 }, null, null, look);
  assert.equal(boe.detail, "Boeing allows any Model and Size empty.");
});

// ---- narrowing (owner decision 2026-11-23: "Listeyi daralt, yine uyar") ----

const models = [{ code: 737, name: "737" }, { code: 747, name: "747" }, { code: 320, name: "A320" }, { code: 707, name: "707" }];
const makes = [{ code: "BOE", name: "Boeing" }, { code: "AIR", name: "Airbus" }, { code: "XXX", name: "Other" }];

test("a list is narrowed to the values the other fields' current values allow", () => {
  const limits = cv.cvAllowance(planes, "model", { make: "BOE", model: null }, null, null);
  assert.deepEqual(cv.cvNarrow(models, limits).map(o => o.code), [737, 747]);
  const air = cv.cvAllowance(planes, "model", { make: "AIR" }, null, null);
  assert.deepEqual(cv.cvNarrow(models, air).map(o => o.code), [320]);
});

test("a field the other fields leave empty is narrowed to the values any combination lists", () => {
  const limits = cv.cvAllowance(planes, "make", { make: null, model: null }, null, null);
  assert.deepEqual(cv.cvNarrow(makes, limits).map(o => o.code), ["BOE", "AIR"]);
  const by747 = cv.cvAllowance(planes, "make", { model: 747 }, null, null);
  assert.deepEqual(cv.cvNarrow(makes, by747).map(o => o.code), ["BOE"]);
});

test("a value held now that is not allowed stays offered; only the feature's own saved value is labelled (N2)", () => {
  const limits = cv.cvAllowance(planes, "model", { make: "AIR", model: 747 }, null, null);
  const typed = cv.cvNarrow(models, limits, [747]);
  assert.deepEqual(typed.map(o => [o.code, o.state]), [[747, "kept"], [320, "ok"]]);
  assert.equal(cv.cvOptionLabel(typed[0]), "747", "a value chosen in this edit says nothing about itself");
  const saved = cv.cvNarrow(models, limits, [747], 747);
  assert.deepEqual(saved.map(o => [o.code, o.state]), [[747, "saved"], [320, "ok"]]);
  assert.equal(cv.cvOptionLabel(saved[0]), "747 (saved value)");
});

test("on the REST resource, which cannot say what is retired, an unchanged held combination is a note, not a warning (N1)", () => {
  const rest = { ...planes, values: planes.values.filter(v => !v.retired), retiredKnown: false };
  const held = { make: "BOE", model: 707 };
  const said = cv.cvSentences(rest, held, null, held, look);
  assert.equal(said.key, "", "no live lead when nothing changed");
  assert.equal(said.note, "This feature holds a combination the layer does not offer for new edits.");
  assert.equal(cv.cvWarning(said), "");
  const changed = cv.cvSentences(rest, { make: "BOE", model: 999 }, null, held, look);
  assert.equal(changed.key, "make_model", "a change is warned about as before");
  const fresh = cv.cvSentences(rest, held, null, null, look);
  assert.equal(fresh.key, "make_model", "a new feature is warned about as before");
});

test("on the stored shape, which knows what is retired, a held retired combination says nothing and is labelled retired (N1)", () => {
  const held = { make: "BOE", model: 707 };
  const said = cv.cvSentences(planes, held, null, held, look);
  assert.equal(said.key, "");
  assert.equal(said.note, "");
  assert.equal(cv.cvFromStored({}).retiredKnown, true);
  // Since ADR-314 the REST resource serves `retired: 1` too, so a definition read from it knows as well.
  assert.equal(cv.cvFromRest({}).retiredKnown, true);
});

test("a narrowed list says why (N11)", () => {
  const limits = cv.cvAllowance(planes, "make", { model: 737 }, null, null);
  assert.equal(cv.cvNarrowedBecause(limits, { model: 737 }, look), "Showing values that go with Model 737. Clear Model to see all.");
  assert.equal(cv.cvNarrowedBecause(cv.cvAllowance(planes, "make", {}, null, null), {}, look), "");
});

test("retired values are not offered for a new edit, and an existing feature's retired value stays, labelled", () => {
  const fresh = cv.cvAllowance(planes, "model", { make: "BOE" }, null, null);
  assert.deepEqual(cv.cvNarrow(models, fresh).map(o => o.code), [737, 747]);
  const existing = cv.cvAllowance(planes, "model", { make: "BOE", model: 707 }, null, { make: "BOE", model: 707 });
  const list = cv.cvNarrow(models, existing, [707]);
  assert.deepEqual(list.map(o => [o.code, o.state]), [[737, "ok"], [747, "ok"], [707, "retired"]]);
  assert.equal(cv.cvOptionLabel(list[2]), "707 (retired)");
});

test("text from a control and a numeric code are one value when narrowing", () => {
  const limits = cv.cvAllowance(planes, "make", { model: "747" }, null, null);
  assert.deepEqual(cv.cvNarrow(makes, limits).map(o => o.code), ["BOE"]);
  const asText = models.map(m => ({ code: String(m.code), name: m.name }));
  assert.deepEqual(cv.cvNarrow(asText, cv.cvAllowance(planes, "model", { make: "AIR" }, null, null)).map(o => o.code), ["320"]);
});

test("nothing narrows a field outside every restrictive group, or one whose other fields are already unlisted", () => {
  assert.equal(cv.cvAllowance(planes, "rating", { make: "BOE" }, null, null), null);
  assert.equal(cv.cvAllowance(planes, "model", { make: "XXX" }, null, null), null);
  const loose = cv.cvFromStored({ fieldGroups: [{ name: "g", restrictive: false, fields: ["a", "b"] }],
    contingentValues: [{ id: 1, fieldGroup: "g", entries: [["a", "CODED_VALUE", 1], ["b", "CODED_VALUE", 1]] }] });
  assert.equal(cv.cvAllowance(loose, "a", { b: 1 }, null, null), null);
});

test("two groups over one field narrow it to what both allow", () => {
  const d = cv.cvFromStored({
    fieldGroups: [{ name: "g1", fields: ["a", "b"] }, { name: "g2", fields: ["a", "c"] }],
    contingentValues: [
      { id: 1, fieldGroup: "g1", entries: [["a", "CODED_VALUE", 1], ["b", "CODED_VALUE", 1]] },
      { id: 2, fieldGroup: "g1", entries: [["a", "CODED_VALUE", 2], ["b", "CODED_VALUE", 1]] },
      { id: 3, fieldGroup: "g2", entries: [["a", "CODED_VALUE", 2], ["c", "ANY", null]] },
      { id: 4, fieldGroup: "g2", entries: [["a", "CODED_VALUE", 3], ["c", "ANY", null]] },
    ],
  });
  const list = cv.cvNarrow([1, 2, 3].map(n => ({ code: n, name: String(n) })), cv.cvAllowance(d, "a", { b: 1, c: "x" }, null, null));
  assert.deepEqual(list.map(o => o.code), [2]);
});

test("a range entry narrows a coded list to the codes inside it", () => {
  const d = cv.cvFromStored({
    fieldGroups: [{ name: "g", fields: ["k", "n"] }],
    contingentValues: [{ id: 1, fieldGroup: "g", entries: [["k", "CODED_VALUE", "x"], ["n", "RANGE", [2, 3]]] }],
  });
  const codes = [1, 2, 3, 4].map(n => ({ code: n, name: String(n) }));
  assert.deepEqual(cv.cvNarrow(codes, cv.cvAllowance(d, "n", { k: "x" }, null, null)).map(o => o.code), [2, 3]);
});

// ---- Studio's group keys (review finding 1) ----

test("a value names its group by the group's key, so renaming one group never moves another's values", () => {
  const stored = cv.cvToStored({
    groups: [{ key: "k1", name: "make_model2", fields: ["a"] }, { key: "k2", name: "make_model", fields: ["a"] }],
    values: [
      { id: 1, group: "make_model", gkey: "k1", entries: [{ field: "a", kind: "ANY" }] },
      { id: 2, group: "make_model", gkey: "k2", entries: [{ field: "a", kind: "ANY" }] },
    ],
  });
  assert.deepEqual(stored.contingentValues.map(v => v.fieldGroup), ["make_model2", "make_model"]);
  assert.equal(stored.fieldGroups[0].key, undefined, "the key is the editor's, and is not sent");
});

// ---- Studio's authoring checks (review findings 3 and 8) ----

test("a typed number takes a decimal comma, and text that is not a number is refused rather than stored as NaN", () => {
  assert.equal(cv.cvParseNumber("10,5"), 10.5);
  assert.equal(cv.cvParseNumber(" 10.5 "), 10.5);
  assert.equal(cv.cvParseNumber("-3"), -3);
  assert.equal(cv.cvParseNumber("1e3"), 1000);
  assert.equal(cv.cvParseNumber("abc"), null);
  assert.equal(cv.cvParseNumber(""), null);
  assert.equal(cv.cvParseNumber("1,500,000"), null, "a thousands separator is ambiguous and refused");
  assert.equal(cv.cvParseNumber("1,500"), null, "N3: one comma before three digits is ambiguous and refused");
  assert.equal(cv.cvAmbiguousNumber("1,500"), true);
  assert.equal(cv.cvAmbiguousNumber("10,5"), false);
  assert.equal(cv.cvAmbiguousSaid("1,500"), "1,500 could mean 1500 or 1,5. Type 1500, or 1,5 for the decimal.");
  assert.equal(cv.cvParseNumber("10,50"), 10.5, "two decimals are not ambiguous");
  assert.equal(cv.cvParseNumber(4), 4);
});

test("a value that repeats an earlier one of its group is named by the earlier one's place", () => {
  const v = (code, extra = {}) => ({ subtype: null, entries: [{ field: "make", kind: "CODED_VALUE", code }, { field: "model", kind: "ANY" }], ...extra });
  const values = [v("BOE"), v("AIR"), v("BOE"), v("BOE", { subtype: 2 }), v("BOE")];
  const repeats = cv.cvDuplicates(values);
  assert.equal(repeats.get(values[2]), 1);
  assert.equal(repeats.get(values[4]), 1);
  assert.equal(repeats.has(values[1]), false);
  assert.equal(repeats.has(values[3]), false, "another subtype is another combination");
  const r1 = { subtype: null, entries: [{ field: "n", kind: "RANGE", min: 1, max: 2 }] };
  const r2 = { subtype: null, entries: [{ field: "n", kind: "RANGE", min: 1, max: 3 }] };
  assert.equal(cv.cvDuplicates([r1, r2]).size, 0);
  assert.equal(cv.cvDuplicates([r1, { ...r1 }]).size, 1);
});

test("the server's refusal names rows and fields as the screen does", () => {
  const names = new Map([[6, "Value 3 of make_model"]]);
  assert.equal(
    cv.cvRenameRefusal("Contingent value 6, field 'size': 5 is outside the range.", names, f => (f === "size" ? "Size" : f)),
    "Value 3 of make_model, field Size: 5 is outside the range.");
  assert.equal(cv.cvRenameRefusal("Contingent value 9 names no subtype.", names), "Contingent value 9 names no subtype.");
});

// ---- third review ----

test("R1: beside a retired pair, only the value that is itself retired says so; a live make does not", () => {
  const held = { make: "BOE", model: 707 };
  const makes3 = [{ code: "BOE", name: "Boeing" }, { code: "AIR", name: "Airbus" }];
  const make = cv.cvNarrow(makes3, cv.cvAllowance(planes, "make", held, null, held), ["BOE"], "BOE");
  assert.deepEqual(make.map(o => cv.cvOptionLabel(o)), ["Boeing"], "Boeing is listed with 737 and 747, so it is not retired");
  const model = cv.cvNarrow(models, cv.cvAllowance(planes, "model", held, null, held), [707], 707);
  assert.deepEqual(model.map(o => cv.cvOptionLabel(o)), ["737", "747", "707 (retired)"]);
});

test("R2: the hint names only fields that remove a value, joins them with and, and names the list for its field", () => {
  const twoGroups = cv.cvFromStored({
    fieldGroups: [{ name: "mm", fields: ["make", "model"] }, { name: "ms", fields: ["make", "size"] }],
    contingentValues: [
      { id: 1, fieldGroup: "mm", entries: [["make", "CODED_VALUE", "BOE"], ["model", "CODED_VALUE", 737]] },
      { id: 2, fieldGroup: "mm", entries: [["make", "CODED_VALUE", "AIR"], ["model", "CODED_VALUE", 320]] },
      { id: 3, fieldGroup: "ms", entries: [["make", "CODED_VALUE", "BOE"], ["size", "RANGE", [1, 100]]] },
      { id: 4, fieldGroup: "ms", entries: [["make", "CODED_VALUE", "AIR"], ["size", "RANGE", [1, 100]]] },
    ],
  });
  const domain = [{ code: "BOE" }, { code: "AIR" }];
  const now = { model: 737, size: 46 };
  const limits = cv.cvAllowance(twoGroups, "make", now, null, null);
  assert.equal(cv.cvNarrowedBecause(limits, now, look, domain, "make"), "Showing makes that go with Model 737. Clear Model to see all.",
    "Size 46 allows both makes, so it is not named");
  const tight = { model: 737, size: 46 };
  const both = cv.cvFromStored({ ...JSON.parse(JSON.stringify({ fieldGroups: [{ name: "mm", fields: ["make", "model"] }, { name: "ms", fields: ["make", "size"] }] })),
    contingentValues: [
      { id: 1, fieldGroup: "mm", entries: [["make", "CODED_VALUE", "BOE"], ["model", "CODED_VALUE", 737]] },
      { id: 2, fieldGroup: "ms", entries: [["make", "CODED_VALUE", "BOE"], ["size", "RANGE", [1, 100]]] },
    ] });
  assert.equal(cv.cvNarrowedBecause(cv.cvAllowance(both, "make", tight, null, null), tight, look, domain, "make"),
    "Showing makes that go with Model 737 and Size 46. Clear them to see all.");
});

test("R7: the quiet line for a definition that cannot say what is retired does not claim the combination was once offered", () => {
  const rest = { ...planes, values: planes.values.filter(v => !v.retired), retiredKnown: false };
  const held = { make: "AIR", model: 747 };
  assert.equal(cv.cvSentences(rest, held, null, held, look).note, "This feature holds a combination the layer does not offer for new edits.");
});
