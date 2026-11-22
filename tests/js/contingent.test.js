// The check Studio's editors run before a save — ADR-312 §5.6. Run with `node --test tests/js/contingent.test.js`.
// It loads src/Graticula.Host/wwwroot/contingent.js exactly as the browser does, through its CommonJS tail.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const cv = require(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "contingent.js"));

// The resource as the conformance test reads it back from a running server (ContingentValuesTests), trimmed.
const resource = {
  typeCodes: ["Unknown", "Any", "Null", "Code", "Range"],
  stringDicts: [{ domain: "Makes", entries: ["BOE", "AIR"] }],
  contingentValuesDefinition: {
    layerID: 0, layerName: "planes", geometryType: "esriGeometryPoint", hasSubType: false,
    fieldGroups: [
      {
        name: "make_model", restrictive: true,
        fields: [
          { name: "make", id: 2, fieldType: "esriFieldTypeString" },
          { name: "model", id: 3, fieldType: "esriFieldTypeInteger" },
          { name: "size", id: 4, fieldType: "esriFieldTypeDouble" },
        ],
        subTypes: [{
          code: 0, name: "", domains: { make: "Makes" },
          contingentValues: [
            { id: 1, types: [3, 3, 4], values: [1, 320, 1.5, 4] },
            { id: 2, types: [3, 1, 2], values: [0] },
          ],
        }],
      },
      { name: "loose", restrictive: false, fields: [{ name: "rating", id: 5, fieldType: "esriFieldTypeString" }], subTypes: [] },
    ],
  },
};

test("the resource is decoded: a text code through its dictionary, a range from two slots, Any and Null from none", () => {
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
});

test("an allowed combination says nothing, and a broken one names its values and its group", () => {
  const d = cv.cvFromRest(resource);
  assert.deepEqual(cv.cvViolations(d, { make: "AIR", model: 320, size: 2 }, null), []);
  assert.deepEqual(cv.cvViolations(d, { make: "BOE", model: 9, size: null }, null), []);

  const broken = cv.cvViolations(d, { make: "AIR", model: 999, size: 2 }, null);
  assert.equal(broken.length, 1);
  assert.equal(broken[0].group, "make_model");
  assert.match(cv.cvWarning(broken), /make = AIR, model = 999, size = 2 is not a combination the field group “make_model” allows\./);
  assert.match(cv.cvWarning(broken), /can still be saved/);
  assert.match(cv.cvWarning(broken, true), /Saved anyway/);
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
