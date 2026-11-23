// The one number rule every Studio screen reads typed numbers by (number-text.js; ADR-312 §5.6, ADR-313 INFERRED (o)).
// Run with `node --test tests/js`.
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const nt = require(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "number-text.js"));
const cv = require(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "contingent.js"));
const fi = require(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "form-info.js"));

test("one comma or one point is the decimal separator", () => {
  for (const [typed, value] of [["90,5", 90.5], ["90.5", 90.5], [" 10,50 ", 10.5], ["-3", -3], [",5", 0.5], ["2e3", 2000],
    ["1500", 1500], ["1,5", 1.5], ["1.5000", 1.5], ["12,3456", 12.3456]]) {
    assert.deepEqual(nt.ntParseNumber(typed), { value }, typed);
  }
});

test("a separator before exactly three digits is ambiguous, whichever it is — 1,500 and 1.500 alike", () => {
  for (const typed of ["1,500", "1.500", "-2.250", "999,999", "12.000"]) {
    assert.equal(nt.ntAmbiguous(typed), true, typed);
    assert.ok(nt.ntParseNumber(typed).ambiguous, typed);
  }
  for (const typed of ["1,50", "1.5", "1500", "1,5000", "1234,567"]) assert.equal(nt.ntAmbiguous(typed), false, typed);
  assert.equal(nt.ntAmbiguousSaid("1.500"), "1.500 could mean 1500 or 1.5. Type 1500 or 1.5.");
  assert.equal(nt.ntAmbiguousSaid("1,500"), "1,500 could mean 1500 or 1.5. Type 1500 or 1.5.");
  assert.equal(nt.ntAmbiguousSaid("2,250"), "2,250 could mean 2250 or 2.25. Type 2250 or 2.25.");
});

test("on a whole-number field the sentence offers only the whole reading, in words", () => {
  assert.equal(nt.ntAmbiguousSaid("1,500", true), "1,500: type 1500 if you mean fifteen hundred.");
  assert.equal(nt.ntAmbiguousSaid("2.000", true), "2.000: type 2000 if you mean two thousand.");
  assert.equal(nt.ntAmbiguousSaid("12,500", true), "12,500: type 12500 if you mean twelve thousand five hundred.");
  assert.equal(nt.ntParseNumber("1.500", true).ambiguous, "1.500: type 1500 if you mean fifteen hundred.");
  assert.equal(nt.ntWords(999999), "nine hundred ninety-nine thousand nine hundred ninety-nine");
});

test("empty and wrong text are told apart", () => {
  assert.deepEqual(nt.ntParseNumber(""), { empty: true });
  assert.deepEqual(nt.ntParseNumber(null), { empty: true });
  for (const typed of ["abc", "1,2,3", "1 234", "1.234,5", "--1"]) assert.deepEqual(nt.ntParseNumber(typed), { wrong: true }, typed);
});

test("the three callers answer by the one rule", () => {
  for (const typed of ["90,5", "1.500", "1,500", "abc", "1500", ""]) {
    const read = nt.ntParseNumber(typed);
    assert.equal(cv.cvParseNumber(typed), "value" in read ? read.value : null, `contingent.js ${typed}`);
    assert.equal(fi.decimalText(typed), read.empty ? "" : "value" in read ? String(read.value) : null, `form-info.js ${typed}`);
    assert.equal(cv.cvAmbiguousNumber(typed), nt.ntAmbiguous(typed));
    assert.equal(fi.ambiguousNumber(typed), nt.ntAmbiguous(typed));
  }
  assert.equal(fi.ambiguousSaid("1.500", true), nt.ntAmbiguousSaid("1.500", true));
  assert.equal(cv.cvAmbiguousSaid("1.500"), nt.ntAmbiguousSaid("1.500"));
});
