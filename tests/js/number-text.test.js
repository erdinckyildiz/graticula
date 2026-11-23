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
  assert.equal(nt.ntAmbiguousSaid("1.500"), "1.500 could mean 1500 or 1.5. Type 1500, or 1.5 for the decimal.");
  assert.equal(nt.ntAmbiguousSaid("1,500"), "1,500 could mean 1500 or 1.5. Type 1500, or 1,5 for the decimal.");
  assert.equal(nt.ntAmbiguousSaid("2,250"), "2,250 could mean 2250 or 2.25. Type 2250, or 2,25 for the decimal.");
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

test("M1: a whole part of 0, or one starting with 0, groups no thousands and is never ambiguous", () => {
  for (const [typed, value] of [["0.125", 0.125], ["0,500", 0.5], ["-0.250", -0.25], ["012.500", 12.5], ["00,125", 0.125]]) {
    assert.equal(nt.ntAmbiguous(typed), false, typed);
    assert.deepEqual(nt.ntParseNumber(typed), { value }, typed);
  }
});

test("M1: three decimals that are all needed are offered with a fourth zero, in the reader's separator", () => {
  assert.equal(nt.ntAmbiguousSaid("2.125"), "2.125 could mean 2125 or 2.125. Type 2125, or 2.1250 for the decimal.");
  assert.equal(nt.ntAmbiguousSaid("2,125"), "2,125 could mean 2125 or 2.125. Type 2125, or 2,1250 for the decimal.");
  assert.equal(nt.ntAmbiguousSaid("1,000"), "1,000 could mean 1000 or 1. Type 1000, or 1 for the decimal.");
  assert.deepEqual(nt.ntParseNumber("2.1250"), { value: 2.125 });
  assert.equal(cv.cvParseNumber("2.1250"), 2.125, "contingent.js accepts it");
  assert.equal(fi.decimalText("2,1250"), "2.125", "form-info.js accepts it");
  assert.equal(nt.ntAmbiguousSaid("2.125", true), "2.125: type 2125 if you mean two thousand one hundred twenty-five.");
});

test("M1: an accepted suggestion is never refused", () => {
  const forms = [];
  for (let whole = 1; whole <= 999; whole += 37) {
    for (const fraction of ["000", "001", "010", "100", "125", "500", "999"]) for (const sep of [".", ","]) {
      forms.push(`${whole}${sep}${fraction}`, `-${whole}${sep}${fraction}`);
    }
  }
  for (const typed of forms) {
    assert.equal(nt.ntAmbiguous(typed), true, typed);
    const said = nt.ntAmbiguousSaid(typed);
    const offered = said.match(/Type (\S+), or (\S+) for the decimal\.$/);
    assert.ok(offered, said);
    for (const suggestion of offered.slice(1)) {
      const read = nt.ntParseNumber(suggestion);
      assert.ok("value" in read, `${typed}: "${suggestion}" was refused (${said})`);
    }
    assert.equal(nt.ntParseNumber(offered[1]).value, Number(typed.replace(/[.,]/, "")), typed);
    assert.equal(nt.ntParseNumber(offered[2]).value, Number(typed.replace(",", ".")), typed);
    const whole = nt.ntAmbiguousSaid(typed, true).match(/type (\S+) if you mean/);
    assert.ok("value" in nt.ntParseNumber(whole[1]), typed);
  }
});
