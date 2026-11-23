// A web map layer's form evaluated headless (ADR-313): what the web map specification's formInfo, formFieldElement
// and formGroupElement pages say about visibility, required, editable and calculated values, and about hidden values,
// on the specification's own tree-inspection example. Run: node --test tests/studio-js
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const F = require(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "form-info.js"));

const fields = [
  { name: "objectid", type: "esriFieldTypeOID", editable: false, nullable: false },
  { name: "tree_type", alias: "Tree type", type: "esriFieldTypeInteger", nullable: true, editable: true,
    domain: { type: "codedValue", name: "trees", codedValues: [{ name: "Oak", code: 1 }, { name: "Maple", code: 2 }, { name: "Pine", code: 3 }] } },
  { name: "height", alias: "Height", type: "esriFieldTypeDouble", nullable: true, editable: true },
  { name: "needs_trimming", alias: "Needs trimming", type: "esriFieldTypeString", length: 3, nullable: true, editable: true,
    domain: { type: "codedValue", name: "yn", codedValues: [{ name: "Yes", code: "Yes" }, { name: "No", code: "No" }] } },
  { name: "inspector", type: "esriFieldTypeString", length: 50, nullable: false, editable: true },
  { name: "inspected", type: "esriFieldTypeDate", nullable: true, editable: true },
  { name: "label", type: "esriFieldTypeString", length: 40, nullable: true, editable: true },
  { name: "computed_by_server", type: "esriFieldTypeString", length: 40, nullable: true, editable: false },
];

/** The specification's "Form with just fields", with its three expressions. */
function treeForm(extra = {}) {
  return {
    title: "Tree inspection",
    formElements: [
      { type: "field", fieldName: "tree_type", label: "Tree type", inputType: { type: "combo-box" }, editableExpression: "expr3" },
      { type: "field", fieldName: "height", label: "Height", inputType: { type: "text-box" }, requiredExpression: "expr1" },
      { type: "field", fieldName: "needs_trimming", label: "Needs trimming", inputType: { type: "combo-box" }, visibilityExpression: "expr2" },
    ],
    expressionInfos: [
      { name: "expr1", title: "Is Pine Tree", expression: "$feature.tree_type == 3", returnType: "boolean" },
      { name: "expr2", title: "Tall Tree", expression: "$feature.height >= 50 ", returnType: "boolean" },
      { name: "expr3", title: "Is Editable", expression: "true", returnType: "boolean" },
    ],
    ...extra,
  };
}

const run = (form, values, more = {}) => F.evaluate(form, { fields, values, editType: "UPDATE", zone: "UTC", ...more });

test("the specification's example: required follows the tree type, visibility follows the height", () => {
  const pine = run(treeForm(), { tree_type: 3, height: 60 });
  assert.equal(pine.byField.get("height").required, true);
  assert.equal(pine.byField.get("needs_trimming").visible, true);
  assert.equal(pine.byField.get("tree_type").editable, true);

  const oak = run(treeForm(), { tree_type: 1, height: 20 });
  assert.equal(oak.byField.get("height").required, false);
  assert.equal(oak.byField.get("needs_trimming").visible, false);
  assert.deepEqual(oak.errors, []);
});

test("required applies only while visible, and a field that is not nullable is always required, shown or hidden", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "height", requiredExpression: "always", visibilityExpression: "never" },
      { type: "field", fieldName: "inspector", visibilityExpression: "never", requiredExpression: "nope" },
    ],
    expressionInfos: [{ name: "always", expression: "true" }, { name: "never", expression: "false" }, { name: "nope", expression: "false" }],
  };
  const result = run(form, { height: null, inspector: null });
  assert.equal(result.byField.get("height").required, false, "hidden: requiredExpression does not apply");
  assert.equal(result.byField.get("inspector").required, true, "non-nullable: always required, the expression ignored");
  assert.deepEqual(F.missing(result), ["inspector"]);
  assert.deepEqual(F.missing(run(form, { height: null, inspector: "Ann" })), []);
});

test("editable: the expression decides, but never makes a field the layer does not let be edited editable", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "label", editableExpression: "big" },
      { type: "field", fieldName: "computed_by_server", editableExpression: "yes" },
      { type: "field", fieldName: "height" },
    ],
    expressionInfos: [{ name: "big", expression: "$feature.height > 10" }, { name: "yes", expression: "true" }],
  };
  assert.equal(run(form, { height: 20 }).byField.get("label").editable, true);
  assert.equal(run(form, { height: 5 }).byField.get("label").editable, false);
  assert.equal(run(form, { height: 5 }).byField.get("computed_by_server").editable, false);
  assert.equal(run(form, { height: 5 }).byField.get("height").editable, true, "no expression: editable");
});

test("a calculated value runs only when the element is not editable and the field allows edits", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "label", editableExpression: F.SYSTEM_FALSE, valueExpression: "name" },
      { type: "field", fieldName: "needs_trimming", valueExpression: "trim" },
      { type: "field", fieldName: "computed_by_server", editableExpression: F.SYSTEM_FALSE, valueExpression: "name" },
    ],
    expressionInfos: [
      { name: F.SYSTEM_FALSE, expression: "false" },
      { name: "name", expression: "Upper(DomainName($feature, 'tree_type')) + ' ' + Text($feature.height, '#.0')" },
      { name: "trim", expression: "IIf($feature.height > 50, 'Yes', 'No')" },
    ],
  };
  const result = run(form, { tree_type: 2, height: 61.25, needs_trimming: null, label: "old", computed_by_server: "kept" });
  assert.equal(result.values.label, "MAPLE 61.3");
  assert.ok(result.byField.get("label").calculated);
  assert.equal(result.values.needs_trimming, null, "editable (no editableExpression): the value expression does not run");
  assert.equal(result.values.computed_by_server, "kept", "the field is not editable: the value expression does not run");
  assert.deepEqual(result.calculated, ["label"]);
});

test("calculated values feed one another in form order, and a later one is reached again", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "label", editableExpression: "no", valueExpression: "fromTrim" },
      { type: "field", fieldName: "needs_trimming", editableExpression: "no", valueExpression: "trim" },
    ],
    expressionInfos: [
      { name: "no", expression: "false" },
      { name: "trim", expression: "IIf($feature.height > 50, 'Yes', 'No')" },
      { name: "fromTrim", expression: "'Trim: ' + $feature.needs_trimming" },
    ],
  };
  const result = run(form, { height: 80, label: null, needs_trimming: null });
  assert.equal(result.values.needs_trimming, "Yes");
  assert.equal(result.values.label, "Trim: Yes", "the first pass read null; the second reads the calculated value");
});

test("calculated values are typed as their fields are, and a value the field cannot hold is an error", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "height", editableExpression: "no", valueExpression: "h" },
      { type: "field", fieldName: "inspected", editableExpression: "no", valueExpression: "d" },
      { type: "field", fieldName: "tree_type", editableExpression: "no", valueExpression: "t" },
      { type: "field", fieldName: "needs_trimming", editableExpression: "no", valueExpression: "long" },
    ],
    expressionInfos: [
      { name: "no", expression: "false" },
      { name: "h", expression: "'12.5'" },
      { name: "d", expression: "Date(2024, 0, 15, 9, 30)" },
      { name: "t", expression: "2.6" },
      { name: "long", expression: "'Definitely'" },
    ],
  };
  const result = run(form, { needs_trimming: "No" });
  assert.equal(result.values.height, 12.5);
  assert.equal(result.values.inspected, Date.UTC(2024, 0, 15, 9, 30));
  assert.equal(result.values.tree_type, 3, "a whole-number field takes the value rounded");
  assert.equal(result.values.needs_trimming, "No", "a value too long is not written");
  assert.match(result.byField.get("needs_trimming").errors.valueExpression, /at most 3 characters/);
});

test("$feature reads a date field as a Date, and $originalFeature and $editContext answer", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "label", editableExpression: "no", valueExpression: "v" },
    ],
    expressionInfos: [
      { name: "no", expression: "false" },
      { name: "v", expression: "$editContext.editType + ' ' + Year($feature.inspected) + ' ' + DefaultValue($originalFeature.label, 'none')" },
    ],
  };
  const inspected = Date.UTC(2023, 5, 1);
  assert.equal(run(form, { inspected }, { editType: "INSERT", original: null }).values.label, "INSERT 2023 none");
  assert.equal(run(form, { inspected, label: "x" }, { original: { label: "was" } }).values.label, "UPDATE 2023 was");
});

test("a hidden field's value is cleared when it goes from shown to hidden, unless the form preserves it", () => {
  const first = run(treeForm(), { tree_type: 3, height: 60, needs_trimming: "Yes" });
  assert.equal(first.byField.get("needs_trimming").visible, true);

  // The height is lowered: needs_trimming is hidden, and its value goes.
  const second = run(treeForm(), { ...first.values, height: 20 }, { previousVisible: first.visible });
  assert.equal(second.byField.get("needs_trimming").visible, false);
  assert.equal(second.values.needs_trimming, null);
  assert.deepEqual(second.cleared, ["needs_trimming"]);

  // With preserveFieldValuesWhenHidden, it stays.
  const kept = run(treeForm({ preserveFieldValuesWhenHidden: true }), { ...first.values, height: 20 }, { previousVisible: first.visible });
  assert.equal(kept.values.needs_trimming, "Yes");
  assert.deepEqual(kept.cleared, []);
});

test("a field hidden when the form opens keeps its value: only a field that was shown is cleared", () => {
  const opened = run(treeForm(), { tree_type: 1, height: 20, needs_trimming: "No" });
  assert.equal(opened.byField.get("needs_trimming").visible, false);
  assert.equal(opened.values.needs_trimming, "No");
  const again = run(treeForm(), opened.values, { previousVisible: opened.visible });
  assert.equal(again.values.needs_trimming, "No");
});

test("a group hides what it holds, and a hidden group's fields are cleared like any other", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "height" },
      { type: "group", label: "Trimming", initialState: "collapsed", visibilityExpression: "tall", formElements: [
        { type: "field", fieldName: "needs_trimming" },
        { type: "text", text: "Trim {needs_trimming} — a {tree_type} of {height} m, {expr/note}" },
      ] },
    ],
    expressionInfos: [{ name: "tall", expression: "$feature.height >= 50" }, { name: "expr/note", expression: "'checked'" }],
  };
  const shown = run(form, { height: 60, needs_trimming: "Yes", tree_type: 3 });
  const group = shown.elements.find(e => e.type === "group");
  assert.equal(group.visible, true);
  assert.equal(group.expanded, false);
  assert.equal(shown.byField.get("needs_trimming").visible, true);
  assert.equal(shown.elements.find(e => e.type === "text").text, "Trim Yes — a Pine of 60 m, checked");

  const hidden = run(form, { ...shown.values, height: 10 }, { previousVisible: shown.visible });
  assert.equal(hidden.byField.get("needs_trimming").visible, false, "inside a hidden group");
  assert.equal(hidden.values.needs_trimming, null);
});

test("a failing expression is treated as absent, and its error is reported against its element (INFERRED)", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "height", visibilityExpression: "broken", requiredExpression: "notBool", editableExpression: "geometry" },
      { type: "field", fieldName: "label", editableExpression: "no", valueExpression: "missingField" },
      { type: "field", fieldName: "needs_trimming", visibilityExpression: "nowhere" },
    ],
    expressionInfos: [
      { name: "broken", expression: "$feature.height >" },
      { name: "notBool", expression: "1" },
      { name: "geometry", expression: "Area($feature) > 3" },
      { name: "no", expression: "false" },
      { name: "missingField", expression: "$feature.nope" },
    ],
  };
  const result = run(form, { height: 3, label: "typed" });
  const height = result.byField.get("height");
  assert.equal(height.visible, true, "a visibility expression that does not parse: shown");
  assert.equal(height.required, false, "a required expression answering a Number: not required");
  assert.equal(height.editable, true, "an editable expression refusing a function: editable");
  assert.match(height.errors.visibilityExpression, /expected|found/);
  assert.match(height.errors.requiredExpression, /answered Number, not a Boolean/);
  assert.match(height.errors.editableExpression, /The Arcade function Area is not built in Studio's form engine/);
  assert.equal(result.values.label, "typed", "a calculated value that fails keeps what was there");
  assert.match(result.byField.get("label").errors.valueExpression, /no field 'nope'/);
  assert.match(result.byField.get("needs_trimming").errors.visibilityExpression, /no expression named 'nowhere'/);
  assert.equal(result.errors.length, 5);
});

test("the designer's writes: expressions named expr/<n>, fixed answers as the system expressions, unused ones pruned", () => {
  const form = F.fromFields(fields.filter(f => f.editable !== false && f.type !== "esriFieldTypeOID"), "Trees");
  assert.equal(form.formElements.length, 6);
  assert.deepEqual(form.formElements[0].inputType, { type: "combo-box", showNoValueOption: true });
  assert.deepEqual(form.formElements.find(e => e.fieldName === "inspected").inputType, { type: "datetime-picker", includeTime: true });

  const height = form.formElements.find(e => e.fieldName === "height");
  const name = F.setExpression(form, height, "visibilityExpression", "$feature.tree_type == 3");
  assert.equal(name, "expr/0");
  assert.equal(F.expressionInfo(form, "expr/0").returnType, "boolean");
  assert.equal(F.setExpression(form, height, "visibilityExpression", "$feature.tree_type != 3"), "expr/0", "rewritten in place");
  assert.equal(F.modeOf(form, height, "visibilityExpression"), "expression");

  const label = form.formElements.find(e => e.fieldName === "label");
  F.setFixed(form, label, "editableExpression", false);
  F.setExpression(form, label, "valueExpression", "'T' + $feature.height", fields.find(f => f.name === "label"));
  assert.equal(label.editableExpression, F.SYSTEM_FALSE);
  assert.equal(F.modeOf(form, label, "editableExpression"), "false");
  assert.equal(F.expressionInfo(form, label.valueExpression).returnType, "string");
  F.setFixed(form, height, "requiredExpression", true);
  assert.equal(F.expressionInfo(form, F.SYSTEM_TRUE).expression, "true");

  F.setExpression(form, height, "visibilityExpression", "");
  assert.equal(height.visibilityExpression, undefined);
  assert.equal(F.expressionInfo(form, "expr/0"), null, "an expression nothing names is pruned");
  F.setFixed(form, height, "requiredExpression", null);
  assert.equal(F.expressionInfo(form, F.SYSTEM_TRUE), null);
  assert.deepEqual(F.problems(form, fields), []);
});

test("input types follow Map Viewer's table by field type", () => {
  const byName = n => fields.find(f => f.name === n);
  assert.deepEqual(F.inputTypesFor(byName("tree_type")), ["text-box", "barcode-scanner", "combo-box", "radio-buttons"]);
  assert.deepEqual(F.inputTypesFor(byName("needs_trimming")), ["text-box", "text-area", "barcode-scanner", "combo-box", "radio-buttons", "switch"]);
  assert.deepEqual(F.inputTypesFor(byName("inspected")), ["datetime-picker"]);
  assert.deepEqual(F.inputTypesFor({ name: "d", type: "esriFieldTypeDateOnly" }), ["date-picker"]);
});

test("problems: unknown and repeated fields, nested groups, missing and broken expressions, wrong inputs, long text", () => {
  const form = {
    formElements: [
      { type: "field", fieldName: "height", inputType: { type: "switch", onValue: 1, offValue: 0 } },
      { type: "field", fieldName: "height" },
      { type: "field", fieldName: "nope" },
      { type: "field", fieldName: "needs_trimming", inputType: { type: "switch", onValue: "Yes", offValue: "Maybe" }, visibilityExpression: "gone" },
      { type: "group", label: "Outer", formElements: [{ type: "group", label: "Inner", formElements: [] }] },
      { type: "text", text: "x".repeat(1001), visibilityExpression: "bad" },
    ],
    expressionInfos: [{ name: "bad", expression: "1 +" }],
  };
  const found = F.problems(form, fields);
  for (const pattern of [/cannot take the input “Switch”/, /in the form twice/, /no field 'nope'/, /off value/, /'gone', which the form does not have/,
    /groups do not nest/, /does not parse/, /at most 1,000 characters/]) {
    assert.ok(found.some(p => pattern.test(p)), `${pattern} in ${JSON.stringify(found, null, 1)}`);
  }
});

test("markdown is drawn as a safe subset: everything else is escaped", () => {
  assert.equal(F.markdownHtml("### Some **descriptive text** to include in the form."),
    "<h5>Some <strong>descriptive text</strong> to include in the form.</h5>");
  assert.equal(F.markdownHtml("- one\n- *two*\n\n1. first"), "<ul><li>one</li><li><em>two</em></li></ul><ol><li>first</li></ol>");
  assert.equal(F.markdownHtml("<img src=x onerror=alert(1)> [a](javascript:alert(1))"),
    "<p>&lt;img src=x onerror=alert(1)&gt; [a](javascript:alert(1))</p>");
});

test("an element of a kind Studio does not edit is kept and evaluated for visibility only", () => {
  const form = {
    formElements: [
      { type: "attachment", attachmentKeyword: "front-of-house", label: "Front of house", visibilityExpression: "v" },
      { type: "relationship", relationshipId: 0, label: "Inspections" },
      { type: "utilityNetworkAssociations", label: "Associations" },
    ],
    expressionInfos: [{ name: "v", expression: "$feature.height > 1" }],
  };
  const result = run(form, { height: 2 });
  assert.deepEqual(result.elements.map(e => [e.type, e.visible]), [["attachment", true], ["relationship", true], ["utilityNetworkAssociations", true]]);
  assert.deepEqual(F.problems(form, fields), []);
});

// The UX review of ADR-313's designer (2026-11-22): logic that moved out of webmap-form.js to be tested here.

test("an expression reading a field the layer lacks is a warning, not a problem (finding 15)", () => {
  const form = treeForm({
    formElements: [
      { type: "field", fieldName: "height", label: "Height", visibilityExpression: "v" },
      { type: "text", text: "Tree {tree_type}: {nosuch} and {e}" },
    ],
    expressionInfos: [
      { name: "v", expression: "$feature.nosuch > 1 && $feature[\"Height\"] > 2 && $feature['also_missing'] == 1", returnType: "boolean" },
      { name: "e", expression: "1", returnType: "number" },
    ],
  });
  assert.deepEqual(F.problems(form, fields), [], "a missing field read by an expression does not stop the form being applied");
  const said = F.warnings(form, fields);
  assert.equal(said.length, 3, JSON.stringify(said, null, 1));
  assert.ok(said.some(w => /reads \$feature\.nosuch, which the layer does not have/.test(w)));
  assert.ok(said.some(w => /\$feature\.also_missing/.test(w)));
  assert.ok(said.some(w => /\{nosuch\}, which is neither a field/.test(w)));
  assert.ok(!said.some(w => /\$feature\.Height|\$feature\.tree_type|shows \{e\}|shows \{tree_type\}/.test(w)), "field names are matched without regard to case, and an expression name is not a field");
  assert.deepEqual(F.warnings(treeForm(), fields), []);
  assert.deepEqual(F.featureNames("$feature.a + $feature [ 'b' ] + $feature[\"c d\"]"), ["a", "b", "c d"]);
});

test("a text element is listed by its first line without markdown's marks (finding 14)", () => {
  assert.equal(F.textTitle("## Check"), "Check");
  assert.equal(F.textTitle("\n\n**Bold** and _italic_\nsecond"), "Bold and italic");
  assert.equal(F.textTitle("- a bullet"), "a bullet");
  assert.equal(F.textTitle("Tree {tree_type} is _old_"), "Tree {tree_type} is old", "an underscore inside a field name is kept");
  assert.equal(F.textTitle(""), "");
  assert.equal(F.textTitle("x".repeat(80)).length, 60);
});

test("a text field of 2^30 - 2 characters, what an unbounded column reports, has no limit worth naming (finding 10)", () => {
  assert.equal(F.lengthLimit({ length: 2 ** 30 - 3 }), 2 ** 30 - 3);
  assert.equal(F.lengthLimit({ length: 1073741822 }), null);
  assert.equal(F.lengthLimit({ length: 2 ** 30 }), null);
  assert.equal(F.lengthLimit({ length: 255 }), 255);
  assert.equal(F.lengthLimit({}), null);
  assert.equal(F.lengthLimit(null), null);
});

test("removing an element hands focus to the next, else the previous, else its group (finding 4)", () => {
  const a = { type: "field", fieldName: "height" };
  const b = { type: "field", fieldName: "label" };
  const inner = { type: "field", fieldName: "inspector" };
  const group = { type: "group", label: "G", formElements: [inner] };
  const form = { formElements: [a, b, group] };
  assert.equal(F.neighbourOf(form, "0"), b);
  assert.equal(F.neighbourOf(form, "2"), b);
  assert.equal(F.neighbourOf(form, "2.0"), group, "the only element of a group hands focus to the group");
  assert.equal(F.neighbourOf({ formElements: [a] }, "0"), null);
});

test("a typed number is read with a decimal comma, and text that is not a number is refused (third review)", () => {
  for (const [typed, read] of [["90,5", "90.5"], ["90.5", "90.5"], [" 1 234,5 ", "1234.5"], ["1.234,5", "1234.5"], ["1,234.5", "1234.5"],
    ["1.234.567", "1234567"], ["-3", "-3"], ["", ""], [",5", "0.5"], ["2e3", "2000"]]) {
    assert.equal(F.decimalText(typed), read, typed);
  }
  for (const typed of ["abc", "9,0,5x", "1-2", "--1"]) assert.equal(F.decimalText(typed), null, typed);
  assert.equal(F.decimalText(null), "");
});
