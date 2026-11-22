// Studio's Arcade interpreter (ADR-313) against the worked examples of Esri's public Arcade documentation — the
// language guide and the function reference (developers.arcgis.com/arcade) — and every example expression of the
// web map specification's form pages and of the Field Maps and Map Viewer form documentation. Expected values are the
// ones the pages print; grapy's suite (src/grapy/tests/test_arcade.py, ADR-235) was the map of which ones to check.
// Run: node --test tests/studio-js
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const A = require(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "arcade.js"));

const UTC = { zone: "UTC" };
const run = (source, profile = {}) => A.evaluate(source, profile, UTC);
const text = (source, profile = {}) => A.toText(run(source, profile));

function equal(source, expected, profile = {}) {
  const found = run(source, profile);
  if (typeof expected === "number" && Number.isNaN(expected)) {
    assert.ok(Number.isNaN(found), `${source} -> ${found}`);
  } else {
    assert.deepStrictEqual(found, expected, source);
  }
}

function near(source, expected, places = 6) {
  const found = run(source);
  assert.ok(Math.abs(found - expected) < 0.5 * 10 ** -places, `${source} -> ${found}, expected ${expected}`);
}

function fails(source, pattern, profile = {}) {
  assert.throws(() => run(source, profile), error => {
    assert.ok(error instanceof A.ArcadeError, `${source}: ${error}`);
    assert.match(error.message, pattern, source);
    return true;
  });
}

/** A form's feature, as Studio's editor builds one: every field of the layer, with the layer's description. */
function feature(attributes, schema = null, geometry = null) {
  return new A.ArcFeature(new A.ArcDict(attributes), geometry ? new A.ArcGeometry(geometry) : null, true, schema);
}

test("variables are declared with var and named without regard to case", () => {
  equal("var Total = 5; total + TOTAL", 10);
  equal("var x; x", null);
  equal("var a = 1, b = 2; a + b", 3);
  fails("age = 35;", /'age' is not declared/);
  equal("var x = 10; x = 'text'; x", "text");
});

test("blocks have no scope and functions have their own", () => {
  equal("if (true) { var inside = 3 } inside", 3);
  equal("var g = 1; function f() { var g = 5; return g } f() + g", 6);
  equal("var g = 1; function f() { g = 7; return g } f(); g", 7);
  fails("function f() { var local = 1; return local } f(); local", /'local' is not declared/);
});

test("returns are explicit or the value of the last statement (the Return page)", () => {
  equal('return "Hello world";', "Hello world");
  equal("var total = 5 + 10; return total;", 15);
  equal("5 + 10;", 15);
  equal("var total = 5 + 10; total;", 15);
  equal("function calculateRatio(values, index){ var value = values[index]; value / Sum(values); } calculateRatio([1, 3], 1)", 0.75);
  equal("return; 5", null);
});

test("functions (the Functions page)", () => {
  equal("function calculatePercentage(value, total){ var p = (value / total) * 100; return Round(p, 2); } CALCULATEPERCENTAGE(50, 75)", 66.67);
  equal("function fact(n) { if (n <= 1) { return 1 } return n * fact(n - 1) } fact(5)", 120);
  fails("function f(a) { return a } f(1, 2)", /takes 1 argument; 2 were given/);
  equal("var count = Count([1, 2, 3]); count", 3);
  fails("function f() { return f() } f()", /recursed too deeply/);
});

test("the Loops page's examples", () => {
  equal("var result = 0; for(var z=0; z < 100; z++) { result += z; } result", 4950);
  equal("var result = 0; for(var z=0; z < 100; z++) { if (z % 2 != 0) { continue; } result += z; } result", 2450);
  equal("var result = 0; for(var z=0; z < 100; z++) { if (z % 2 == 0) { continue; } result += z; } result", 2500);
  equal("var a = [10,20,30,40,50,60,70]; var s = 0; for (var i in a) { s += i } s", 21);
  equal("var a = [10,20,30]; var s = 0; for (var i in a) { s += a[i] } s", 60);
  equal("var s = 0; for (var v of [10, 20, 30]) { s += v } s", 60);
  equal("var d = {a: 1, b: 2}; var k = ''; for (var key in d) { k += key } k", "ab");
  equal("var d = {a: 1, b: 2}; var s = 0; for (var kv of d) { s += kv.value } s", 3);
  equal("var y = 0; while (y < 10) { y++ } y", 10);
  equal("var y = 0; for (;;) { y++; if (y == 4) { break } } y", 4);
});

test("an endless loop stops at the step budget instead of hanging the page", () => {
  fails("while (true) { }", /ran too long/);
});

test("conditions and logical operands are Booleans, never cast (the Type casting page)", () => {
  fails("if (1) { 2 }", /must be a Boolean/);
  fails("1 && true", /must be a Boolean/);
  equal("false && (1 / 0 > 0)", false);
  equal("!false", true);
  equal("if (false) { 1 } else if (true) { 2 } else { 3 }", 2);
});

test("numbers in their notations, and the constants", () => {
  equal("0.255e3", 255);
  equal("0x1F + 0b101 + 0o17", 31 + 5 + 15);
  equal("PI", Math.PI);
  equal("Infinity > 1e308", true);
  equal("TextFormatting.NewLine", "\n");
});

test("arrays cannot have missing elements (the Types page)", () => {
  equal("[10,20,30][0]", 10);
  equal("var z = [1,2,3]; z[3] = 4; Count(z)", 4);
  fails("var z = [1,2,3]; z[1000] = 4;", /missing elements/);
  equal("[1, 2, 'hello'][2]", "hello");
});

test("dictionaries are read without regard to case and with safe access", () => {
  equal("var d = {Name: 'x'}; d.NAME + d['name']", "xx");
  equal("var d = {'full name': 'Ann'}; d['full name']", "Ann");
  equal("var y = 2020; var d = {Population_2020: 5}; d['Population_' + y]", 5);
  equal("var field1 = 1; var field2 = 2; var d = {field1, field2}; d.field2", 2);
  equal("var d = null; d?.x", null);
  equal("var d = {a: {b: 1}}; d?.c?.b", null);
  fails("var d = {a: 1}; d.c", /no key 'c'/);
});

test("template literals (the Template literals page)", () => {
  equal("`Hello World`", "Hello World");
  equal('`My name is "Dave"`', 'My name is "Dave"');
  equal("`Corn: 5,000\nWheat: 4,000`", "Corn: 5,000\nWheat: 4,000");
  equal("var scores = [70, 85, 92, 98, 100, 78, 89]; `The average score was ${Round(Average(scores), 2)}%.`", "The average score was 87.43%.");
  equal("var o = {a: `in ${1 + 1}`}; `out ${o.a}`", "out in 2");
});

test("comments, text escapes and a syntax error's line", () => {
  equal("// a comment\n1 /* another */ + 1", 2);
  equal("'it\\'s' + \"\\t\" + '\\u0041'", "it's\tA");
  assert.throws(() => run("var a = 1;\nvar b = ;"), e => e instanceof A.ArcadeError && e.line === 2);
});

test("profile variables are read-only, and the network ones are refused by name", () => {
  const f = feature({ a: 4 });
  equal("$feature.a", 4, { $feature: f });
  equal("$FEATURE.A", 4, { $feature: f });
  fails("$feature = 1", /cannot be assigned/, { $feature: f });
  fails("$feature.a = 1", /immutable/, { $feature: f });
  fails("$feature.missing", /has no field 'missing'/, { $feature: f });
  fails("Count($layer)", /'\$layer' is not available in Studio's form engine: .*no network access/);
  fails("$map", /'\$map' is not available/);
  fails("$whatever", /'\$whatever' is not available here/);
});

test("operators: equality does not coerce, relational operators cast", () => {
  equal('10 == "10"', false);
  equal('10 != "10"', true);
  equal("[1,2,3] == [1,2,3]", false);
  equal("var a = [1]; a == a", true);
  equal("null == null", true);
  equal('10 > "10"', false);
  equal('10 >= "10"', true);
  equal('9 < "10"', true);
  equal("'b' > 'a'", true);
  equal("[1] < [2]", false);
  fails("Date(2020, 0, 1) < Time(1, 0)", /cannot be compared with a Time/);
});

test("operators: + concatenates with text, otherwise adds numbers; unary and increments", () => {
  equal('"5" + 1', "51");
  equal("'a' + null", "a");
  equal("null + 1", 1);
  equal("true + 1", 2);
  equal('"5" * 2', 10);
  equal('"abc" * 2', NaN);
  equal("'a' + true", "atrue");
  equal("+'  '", 0);
  equal("+null", 0);
  equal("-true", -1);
  equal("+'abc'", NaN);
  equal("var t = 'a'; t++; t", NaN);
  equal("var c = 1; var d = c++; d * 10 + c", 12);
  equal("var c = 1; var d = ++c; d * 10 + c", 22);
});

test("operators: arithmetic follows IEEE doubles; bitwise on 32-bit integers; precedence", () => {
  equal("1 / 0", Infinity);
  equal("-1 / 0", -Infinity);
  equal("0 / 0", NaN);
  equal("7 % 3", 1);
  equal("-7 % 3", -1);
  equal("var x = 10; x -= 2; x *= 3; x /= 4; x", 6);
  equal("5 & 3", 1);
  equal("5 | 3", 7);
  equal("5 ^ 3", 6);
  equal("~5", -6);
  equal("1 << 3", 8);
  equal("-16 >> 2", -4);
  equal("-1 >>> 28", 15);
  equal("1 + 2 * 3", 7);
  equal("(1 + 2) * 3", 9);
  equal("1 + 2 == 3 && 2 * 2 == 4", true);
  equal("true || false && false", true);
  equal("-2 * -2", 4);
});

test("the Math functions page's examples", () => {
  equal("Abs(-3)", 3);
  equal("Abs(null)", 0);
  near("Acos(0.3)", 1.266104);
  near("Asin(0.3)", 0.304693);
  near("Atan(1)", 0.785398);
  near("Atan2(-1, -1)", -2.356194);
  equal("Average([0,5,10])", 5);
  equal("Average(0,5,10)", 5);
  equal("Ceil(2135.0905, 2)", 2135.1);
  equal("Constrain(5, 0, 10)", 5);
  equal("Constrain(-3, 0, 10)", 0);
  equal("Constrain(553, 0, 10)", 10);
  near("Cos(1)", 0.540302);
  near("Exp(2)", 7.389056);
  equal("Floor(2316.2562, 2)", 2316.25);
  near("Log(10)", 2.302585);
  equal("Log(100, 10)", 2);
  equal("Log(8, 2)", 3);
  near("Log(100, 15)", 1.7005483074552, 12);
  equal("Log(5, -10)", NaN);
  equal("Log(0, 10)", NaN);
  equal("Max([23,56,89])", 89);
  equal("Max(23,5,120,43,9)", 120);
  equal("Mean([1,2,3,4,5,6,7,8,9])", 5);
  equal("Min(23,5,120,43,9)", 5);
  equal("Pow(3, 2)", 9);
  equal("Product([1,2,3])", 6);
  equal("Product([])", 1);
  equal("Product([[1,2],[3,1]])", NaN);
  equal("Product([{a: 10}])", NaN);
  equal("Product()", 1);
  equal("Round(2316.2562, 2)", 2316.26);
  equal("Round(2.5)", 3);
  equal("Round(-2.5)", -2);
  equal("Round(1.005, 2)", 1.01);
  equal("Sign(1)", 1);
  equal("Sign(-3.2)", -1);
  equal("Sign(-Infinity)", -1);
  equal("Sign('hello')", NaN);
  equal("Sign(0)", 0);
  equal("1 / Sign(0)", Infinity);
  equal("1 / Sign(-0)", -Infinity);
  // The page prints Sin(1) as 0.841741, a transposition of the sine of one radian, 0.841471 (as ADR-235 found).
  near("Sin(1)", 0.841471);
  equal("Sqrt(9)", 3);
  equal("Stdev([23,56,89,12,45,78])", 27.5);
  equal("Sum([23,56,89,12,45,78])", 303);
  equal("Sum(23,56,89,12,45,78)", 303);
  near("Tan(0.521)", 0.57389, 5);
  equal("Variance([12,23,45,56,78,89])", 756.25);
  equal("Number(Date(1996,11,10,8,0,0))", 850204800000);
  equal("Number('12.5')", 12.5);
  equal("Number(true)", 1);
  equal("Number('abc')", NaN);
  const r = run("Random()");
  assert.ok(r >= 0 && r < 1);
});

test("the Text functions page's examples", () => {
  equal("Concatenate(['red', 'blue', 'green'], '/')", "red/blue/green");
  equal("Count('Graham County')", 13);
  equal("Find('380', 'Esri, 380 New York Street', 0)", 6);
  equal("Find('x', 'abc')", -1);
  equal("FromCharCode(88,89,90)", "XYZ");
  equal("FromCharCode(55356, 57097)", "\u{1F309}");
  equal("FromCodePoint(127753)", "\u{1F309}");
  equal("Left('the quick brown fox', 3)", "the");
  equal("Lower('HELLO')", "hello");
  equal("Mid('the quick brown fox', 4, 5)", "quick");
  equal("Proper('the quick brown fox', 'everyword')", "The Quick Brown Fox");
  equal("Proper('the quick brown fox', 'firstword')", "The quick brown fox");
  equal("Replace('the quick brown fox', 'brown', 'red')", "the quick red fox");
  equal("Replace('a-a-a', 'a', 'b', false)", "b-a-a");
  equal("Right('the quick brown fox', 3)", "fox");
  equal("Split('red,green,blue,orange', ',', 2)", ["red", "green"]);
  equal("Split('a  b', ' ', -1, true)", ["a", "b"]);
  equal('StandardizeFilename("USGS:Green River, Utah")', "USGS, Green River, Utah");
  equal("StandardizeGuid('{4e6f776d-c298-4b4b-86a4-57103b4d0f4a}', 'digits')", "4e6f776dc2984b4b86a457103b4d0f4a");
  equal("StandardizeGuid('{4e6f776d-c298-4b4b-86a4-57103b4d0f4a}', 'digits-hyphen')", "4e6f776d-c298-4b4b-86a4-57103b4d0f4a");
  equal("ToCharCode('XYZ', 1)", 89);
  equal("ToCharCode('\\uFFFF\\uFFFE')", 65535);
  equal("ToCharCode('\u{1F309}', 1)", 57097);
  equal("ToCodePoint('\u{1F309}')", 127753);
  equal("ToHex(100)", "64");
  equal('Concatenate("#", ToHex(65), ToHex(105), ToHex(225))', "#4169E1");
  equal("Trim('   hello world')", "hello world");
  equal("Upper('Hello')", "HELLO");
  equal("UrlEncode({itemID: '36ff9e8c13e042a58cfce4ad87f55d19', center: '43.567,-117.380'})",
    "center=43.567%2C-117.380&itemID=36ff9e8c13e042a58cfce4ad87f55d19");
  assert.match(run("Guid()"), /^\{[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\}$/);
  assert.match(run("Guid('digits')"), /^[0-9a-f]{32}$/);
});

test("Text's number and date formats", () => {
  equal("Text(123, '0000')", "0123");
  equal("Text(123, '00')", "23");
  equal("Text(1234, '#,###')", "1,234");
  equal("Text(12345678.123, '#,###.00')", "12,345,678.12");
  equal("Text(1234.55, '$#,###.00')", "$1,234.55");
  equal("Text(1.236, '#.00')", "1.24");
  equal("Text(1234.5678, '#,##0.00#')", "1,234.568");
  equal("Text(-2, 'Floor #;Basement #')", "Basement 2");
  equal("Text(2, 'Floor #;Basement #')", "Floor 2");
  equal("Text(0.3, '#%')", "30%");
  equal("Text(100)", "100");
  equal("Text(Date(2016, 9, 25, 8, 43, 11), 'dddd, MMMM D, Y @ h:m:s')", "Tuesday, October 25, 2016 @ 8:43:11");
  equal("Text(Date(2023, 8, 14, 10, 4, 49), 'ddd, MMM D, Y h:mm:ss A ZZ')", "Thu, Sep 14, 2023 10:04:49 AM +00:00");
  equal("Text(Date(2023, 0, 5, 13, 7, 9, 45), 'YYYY-MM-DD HH:mm:ss.SSS DDD d YY hh a')", "2023-01-05 13:07:09.045 5 5 23 01 pm");
  equal("Text(Date(2023, 0, 5), '[Day] D')", "Day 5");
  equal("Text([1, 'a', true, null])", '[1,"a",true,null]');
  equal("Text({a: 1})", '{"a":1}');
  equal("Text(Date(2016, 9, 20, 17, 41, 37))", "2016-10-20T17:41:37+00:00");
  equal("Text(DateOnly(2007, 11, 1))", "2007-12-01");
  equal("Text(Time(6, 30, 0))", "06:30:00");
  equal("Text(0.1 + 0.2)", "0.30000000000000004");
  equal("'' + 1e21", "1e+21");
});

test("the Logical functions page's examples", () => {
  equal("Boolean('true')", true);
  equal("Boolean(0)", false);
  equal("Boolean('hello')", false);
  equal("Decode(2, 1, 'Residential', 2, 'Commercial', 3, 'Mixed', 'Other')", "Commercial");
  equal("Decode(9, 1, 'Residential', 2, 'Commercial', 3, 'Mixed', 'Other')", "Other");
  equal("DefaultValue(null, 'no data')", "no data");
  equal("DefaultValue('', 'no data')", "no data");
  equal("Count(DefaultValue([], 'no data'))", 0);
  equal('Equals(1, "1")', false);
  equal("var array1 = Array(5); var array2 = Array(5); Equals(array1, array2)", false);
  equal("var array1 = Array(5); Equals(array1, array1)", true);
  equal("IIf(999 < 1000000, 'below', 'above')", "below");
  equal("IsEmpty(null)", true);
  equal("IsEmpty('hello world')", false);
  equal("IsEmpty([])", false);
  equal("IsEmpty([], true)", true);
  equal("var dict = {}; IsEmpty(dict, true)", true);
  equal("IsNan(Infinity / Infinity)", true);
  equal("IsNan('4')", false);
  equal("TypeOf(true)", "Boolean");
  equal("TypeOf(Now())", "Date");
  equal("TypeOf('a') + TypeOf(1) + TypeOf([]) + TypeOf({}) + TypeOf(null)", "StringNumberArrayDictionaryNull");
  const when = d => `var density = ${d}; When(density < 50, 'low', density >= 50 && density < 100, 'medium', density >= 100, 'high', 'n/a')`;
  equal(when(10), "low");
  equal(when(60), "medium");
  equal(when(100), "high");
  equal(when("NaN"), "n/a");
});

test("the Array functions page's examples", () => {
  const even = "function isEven(value) { return value % 2 == 0 } ";
  equal(`${even}All([1,2,3,4,5], isEven)`, false);
  equal(`${even}Any([1,2,3,4,5], isEven)`, true);
  equal(`${even}None([1,2,3,4,5], isEven)`, false);
  equal(`${even}Filter([1,2,3,4,5], isEven)`, [2, 4]);
  equal("Array(5)", [null, null, null, null, null]);
  equal('Array(2, "hello")', ["hello", "hello"]);
  equal("var people = [{name: 'a'}]; var copied = Array(people); people[0] == copied[0]", true);
  equal("var people = [{name: 'a'}]; var deep = Array(people, true); people[0] == deep[0]", false);
  equal("Back(['orange','purple','gray'])", "gray");
  equal("Count([12,21,32,44,58,63])", 6);
  equal('DefaultValue([23,4,null,36,901], 4, "No data")', 901);
  equal('DefaultValue([23,4,null,36,901], 2, "No data")', "No data");
  equal('DefaultValue([23,4,[0,0,1,1,0],36,901], [2,3], "No data")', 1);
  equal("Distinct([1,1,2,1,1,2,2,3,4,5])", [1, 2, 3, 4, 5]);
  equal("Distinct('high','medium','low',0,'high','high','low')", ["high", "medium", "low", 0]);
  equal("var a = ['orange','purple','gray']; Erase(a, -1); a", ["orange", "purple"]);
  equal("First([])", null);
  fails("Front([])", /empty array/);
  equal("HasValue([23,4,[0,0,1,1,0],36,901], [2,4])", true);
  equal("Includes(['orange','purple','gray'], 'purple')", true);
  equal("Includes(['orange','purple','gray'], 'red')", false);
  equal("indexof([1,2,3,4], 3)", 2);
  equal("var a = ['orange','purple','gray']; Insert(a, -1, 'yellow'); a", ["orange", "purple", "yellow", "gray"]);
  equal("function toCelsius(f) { return Round((f - 32) * 5 / 9, 2) } Map([82,67,96,55,34], toCelsius)", [27.78, 19.44, 35.56, 12.78, 1.11]);
  equal("var a = ['orange','purple','gray']; [Pop(a), a]", ["gray", ["orange", "purple"]]);
  equal("Push(['orange','purple','gray'], 'red')", 4);
  const cities = "var cities = [{name: 'Columbus', population: 913921}, {name: 'Dayton', population: 137644}]; "
    + "function mostPopulated(a, b) { return IIf(a.population > b.population, a, b) } ";
  equal(`${cities}Reduce(cities, mostPopulated).name`, "Columbus");
  equal("var a = ['orange','purple','gray']; Resize(a, 5, 'red'); a", ["orange", "purple", "gray", "red", "red"]);
  equal("Reverse(['orange','purple','gray'])", ["gray", "purple", "orange"]);
  equal("var c = ['orange','purple','gray','red','blue']; Slice(c, 1, 3)", ["purple", "gray"]);
  equal("var c = ['orange','purple','gray','red','blue']; Slice(c, -1)", ["blue"]);
  equal("Sort([1,'a','$','A'])", ["$", 1, "A", "a"]);
  equal("Sort([10, 9, 1, null, 2])", [1, 2, 9, 10, null]);
  equal("var p = [{name: 'Sam', age: 25}, {name: 'Bob', age: 27}, {name: 'Emma', age: 24}]; function compareAge(a, b) { return a.age - b.age } "
    + "var s = Sort(p, compareAge); s[0].name + s[1].name + s[2].name", "EmmaSamBob");
  equal("Splice(['orange','purple'], 1, 2, 'red')", ["orange", "purple", 1, 2, "red"]);
  equal("Top([43,32,19,0,3,55], 3)", [43, 32, 19]);
});

test("the Dictionary functions page's examples", () => {
  const towns = "var d = Dictionary('Port Hope', 16214, 'Grafton', '<1000', 'Cobourg', 18519); ";
  equal(`${towns}Count(d)`, 3);
  equal("var d = Dictionary('{\"id\": 1, \"population\": 200, \"city\": \"Spencer, ID\"}'); d.population", 200);
  equal(`${towns}Erase(d, 'Grafton'); GetKeys(d)`, ["Cobourg", "Port Hope"]);
  equal('FromJSON("true")', true);
  equal('fromJSON("731.1")', 731.1);
  equal("var d = fromJSON('{\"kids\": 3, \"adults\": 4 }'); d.kids + d.adults", 7);
  equal("fromJSON('[\"one\", 2, \"three\", false]')", ["one", 2, "three", false]);
  equal(`${towns}GetKeys(d)`, ["Cobourg", "Grafton", "Port Hope"]);
  equal(`${towns}GetValues(d)`, [18519, "<1000", 16214]);
  equal(`${towns}HasKey(d, 'Cobourg')`, true);
  equal("var d = {Cobourg: null}; HasValue(d, 'Cobourg')", false);
  equal(`${towns}Insert(d, 'Colborne', 1474); Count(d) + d.Colborne`, 1478);
  const data = "var data = {time: Date(2024, 0, 24, 12), weather: {precipitation: {value: 0.4, values: [0.4, 0, 0, null]}}}; ";
  equal(`${data}Year(DefaultValue(data, 'time', 'No time logged'))`, 2024);
  equal(`${data}DefaultValue(data, ["weather", "precipitation", "value"], 0)`, 0.4);
  equal(`${data}DefaultValue(data, ["weather", "precipitation", "values", 3], 0)`, 0);
  equal("var inputDict = {company: {name: 'x'}}; var c = Dictionary(inputDict, true); inputDict.company == c.company", false);
});

test("the Date functions page's examples (run in UTC)", () => {
  equal("Month(Date(1987,05,02)) + Day(Date(1987,05,02))", 7);
  equal("Hour(Date(1990, 10, 2, 2, 23, 0, 0, '-05:00'))", 2);
  equal("Number(Date('2016-10-20T17:41:37+00:00'))", 1476985297000);
  equal("Hour(Date('2016-10-20T17:41:37'))", 17);
  equal("Number(Date(1476987783555))", 1476987783555);
  equal("Number(Date(null))", 0);
  equal("ISOMonth(Date(1980, 11, 31))", 12);
  equal("ISOWeek(Date(1980, 11, 31))", 1);
  equal("ISOWeek(DateOnly(1996, 11, 10))", 50);
  equal("ISOWeekday(Date(1980, 11, 31))", 3);
  equal("ISOYear(Date(1980, 11, 31))", 1981);
  equal("Millisecond(Time(2, 59, 23, 450))", 450);
  equal("Minute(Time(2, 59, 23))", 59);
  equal("Month(DateOnly(1996, 0, 31))", 0);
  equal("Week(Date(1974,0,3))", 0);
  equal("Week(Date(1974,0,3), 4)", 1);
  equal("Week(Date(1945,8,23))", 38);
  equal("Week(Date(2022,7,20))", 33);
  equal("Week(DateOnly(1996, 11, 10))", 49);
  equal("Weekday(Date(1980, 11, 31))", 3);
  equal("Weekday(DateOnly(1996, 11, 10))", 2);
  equal("Month(Date(2020, 12, 1))", 0);
  equal("Year(Date(2020, 12, 1))", 2021);
  equal("Text(DateOnly(1476987783555))", "2016-10-20");
  equal('Text(DateOnly("2023-05-11T13:43:18.990+01:00"))', "2023-05-11");
  equal("Text(DateOnly(Date(2008,10,11,10,30)))", "2008-11-11");
  equal("DateOnly(1996, 13, 10)", null);
  equal("Text(Time(15, 47, 0, 474))", "15:47:00.474");
  equal("Text(Time(8119800))", "02:15:19.800");
  equal("Time(86400001)", null);
  equal('Text(Time("1:20 PM"))', "13:20:00");
  equal("Time(25, 0)", null);
  equal("Text(Date(DateOnly(2022,10,11), Time('11:20 am')))", "2022-11-11T11:20:00+00:00");
  equal("var d = DateAdd(Date(2023,9,1,12,0), 7, 'days'); [Month(d), Day(d), Hour(d)]", [9, 8, 12]);
  equal("Text(DateAdd(DateOnly(2023,5,4), 7, 'days'))", "2023-06-11");
  equal("Text(DateAdd(DateOnly(2023,5,4), 12, 'hours'))", "2023-06-04");
  equal("Text(DateAdd(Time(11,30), 25, 'hours'))", "12:30:00");
  equal("DateDiff(Time(23,0), Time(9,0), 'hours')", 14);
  equal("Text(DateAdd(Date(2020, 0, 31), 1, 'months'), 'Y-MM-DD')", "2020-02-29");
  equal("DateDiff(Date(2020, 0, 2), Date(2020, 0, 1))", 86400000);
  equal("DateDiff(Date(2021, 0, 1), Date(2020, 0, 1), 'years')", 1);
  equal("DateDiff(Date(2020, 2, 16), Date(2020, 1, 1), 'months')", 1 + 15 / 31);
  equal("Floor(DateDiff(DateOnly(2023, 0, 1), DateOnly(1996, 11, 10), 'years'))", 26);
  equal("TimeZoneOffset(Date(2011, 10, 11, 8, 0, 0, 0, '-05:00'))", -18000000);
  equal("TimeZone(Date(2024, 1, 9, 0, 0, 0, 0, '+07:00'))", "+07:00");
  equal("Hour(ChangeTimeZone(Date(2011, 10, 11, 8, 0, 0, 0, '-05:00'), '-08:00'))", 5);
  equal("TimeZone(Now())", "UTC");
  equal("Hour(Today()) + Minute(Today())", 0);
});

test("a named time zone is read through the platform's Intl, across a daylight saving change", () => {
  // New York is UTC−5 in January and UTC−4 in July.
  equal("TimeZoneOffset(Date(2024, 0, 15, 12, 0, 0, 0, 'America/New_York'))", -5 * 3600000);
  equal("TimeZoneOffset(Date(2024, 6, 15, 12, 0, 0, 0, 'America/New_York'))", -4 * 3600000);
  equal("Hour(ChangeTimeZone(Date(2024, 6, 15, 12, 0, 0, 0, 'UTC'), 'America/New_York'))", 8);
  fails("Date(2024, 0, 1, 0, 0, 0, 0, 'Nowhere/Atlantis')", /time zone 'Nowhere\/Atlantis' is not known/);
});

test("the execution zone is the caller's: the same expression in two zones", () => {
  const program = A.compile("Hour(Date(1700000000000))");
  assert.equal(A.run(program, {}, { zone: "UTC" }).value, 22);
  assert.equal(A.run(program, {}, { zone: "+03:00" }).value, 1);
});

test("Console collects its lines and GetEnvironment names this engine", () => {
  const { value, console } = A.run("Console('a', 1); Console({b: 2}); 5", {}, UTC);
  assert.deepEqual([value, console], [5, ["a 1", '{"b":2}']]);
  equal("GetEnvironment().timeZone", "UTC");
  equal("GetEnvironment().engine", "graticula-studio");
});

// ---------------------------------------------------------------------------------------------------- the forms' examples

const trees = { fields: [
  { name: "tree_type", type: "esriFieldTypeInteger", domain: { type: "codedValue", name: "trees", codedValues: [
    { name: "Oak", code: 1 }, { name: "Maple", code: 2 }, { name: "Pine", code: 3 }] } },
  { name: "height", type: "esriFieldTypeDouble" },
  { name: "water_damage", type: "esriFieldTypeString", domain: { type: "codedValue", name: "yn", codedValues: [
    { name: "Yes", code: "Y" }, { name: "No", code: "N" }] } },
] };

test("the web map specification's formInfo examples (developers.arcgis.com/web-map-specification/objects/formInfo)", () => {
  const pine = { $feature: feature({ tree_type: 3, height: 60, tree_height: 60 }, trees) };
  const oak = { $feature: feature({ tree_type: 1, height: 20, tree_height: 20 }, trees) };
  equal("$feature.tree_type == 3", true, pine);
  equal("$feature.tree_type == 3", false, oak);
  equal("$feature.height >= 50 ", true, pine);
  equal("$feature.height >= 50 ", false, oak);
  equal("true", true);
  equal("$feature.tree_height > 50", true, pine);
  equal("$feature.tree_height > 50", false, oak);
  // A field not typed yet is null, and null compares as zero.
  equal("$feature.height >= 50", false, { $feature: feature({ height: null }) });
});

test("the formExpressionInfo page's examples", () => {
  equal("$feature.pop2000 > 1000", true, { $feature: feature({ pop2000: 1500 }) });
  equal("$feature.pop2000 > 1000", false, { $feature: feature({ pop2000: 900 }) });
  assert.equal(A.typeOf(run("Today()")), "Date");
});

test("the attachment element's filenameExpression, with $formElement", () => {
  const profile = { $formElement: new A.ArcDict({ attachmentKeyword: "image-of-tree" }) };
  assert.match(run('StandardizeFilename(`${$formElement.attachmentKeyword}_${Text(Now(), "YMMDDHHmmss")}`)', profile),
    /^image-of-tree_\d{14}$/);
});

test("the form calculation profile's example (developers.arcgis.com/arcade/profiles/form-calculation)", () => {
  const script = `var fullName = IIF(
!IsEmpty($feature.InspectorFirst) && !IsEmpty($feature.InspectorLast),
$feature.InspectorFirst + " " + $feature.InspectorLast,
"N/A"
);
return fullName;`;
  equal(script, "Ada Lovelace", { $feature: feature({ InspectorFirst: "Ada", InspectorLast: "Lovelace" }) });
  equal(script, "N/A", { $feature: feature({ InspectorFirst: "Ada", InspectorLast: null }) });
  equal(script, "N/A", { $feature: feature({ InspectorFirst: "", InspectorLast: "Lovelace" }) });
});

test("the form constraint profile's example (developers.arcgis.com/arcade/profiles/form-constraint)", () => {
  const script = "$feature.status == 'Completed' && !IsEmpty($feature.resolution)";
  equal(script, true, { $feature: feature({ status: "Completed", resolution: "Fixed" }) });
  equal(script, false, { $feature: feature({ status: "Completed", resolution: "" }) });
  equal(script, false, { $feature: feature({ status: "Open", resolution: "Fixed" }) });
});

test("Field Maps' and Map Viewer's form examples (doc.esri.com configure-the-form, create-form-mv)", () => {
  // Visibility: DomainName reads the layer's coded values.
  equal('DomainName($feature, "water_damage") == "Yes"', true, { $feature: feature({ water_damage: "Y" }, trees) });
  equal('DomainName($feature, "water_damage") == "Yes"', false, { $feature: feature({ water_damage: "N" }, trees) });
  equal('DomainCode($feature, "water_damage", "No")', "N", { $feature: feature({ water_damage: "Y" }, trees) });
  equal("DomainName($feature, 'tree_type')", "Pine", { $feature: feature({ tree_type: 3 }, trees) });
  equal("Domain($feature, 'tree_type').name", "trees", { $feature: feature({ tree_type: 3 }, trees) });
  // The pages print the required and editable examples without a function name — "($feature, "debris_length") > 3" —
  // which is not an expression; it fails as a syntax error naming what was expected.
  fails('($feature, "debris_length") > 3', /'\)' was expected/, { $feature: feature({ debris_length: 4 }) });
  // A calculated date.
  assert.equal(A.typeOf(run("return Today();")), "Date");
  // The calculated area: with no geometry it returns nothing; with one, Area is refused by name.
  const area = "if (IsEmpty(Geometry($feature))) { return; } else { return Area($feature, 'square-meters') }";
  equal(area, null, { $feature: feature({}) });
  fails(area, /The Arcade function Area is not built in Studio's form engine: geometry operations/, {
    $feature: feature({}, null, { rings: [[[0, 0], [0, 10], [10, 10], [0, 0]]] }) });
  // The calculated user name, printed on one line: GetUser is refused by name before Portal is reached.
  fails('var userInfo = GetUser(portal("https://www.arcgis.com"),"")return (userinfo["fullName"])', /The Arcade function GetUser is not built/);
  // The ZIP code from an intersecting layer: $map is refused by name.
  fails('var parcels_FS = Intersects($feature, FeatureSetByName($map,"Redlands parcels")); if (Count(parcels_FS) == 1) { return Text(First(parcels_FS).ZIP_CODE); }',
    /Intersects is not built|'\$map' is not available/, { $feature: feature({}) });
});

test("$editContext.editType and $originalFeature (the form profiles' variables)", () => {
  const profile = {
    $feature: feature({ status: "Done" }),
    $originalFeature: feature({ status: "Open" }),
    $editContext: new A.ArcDict({ editType: "UPDATE", editTag: "" }),
  };
  equal("$editcontext.editType == 'UPDATE' && $originalFeature.status != $feature.status", true, profile);
  equal("IIf($editContext.editType == 'INSERT', 'new', 'old')", "old", profile);
});

// ---------------------------------------------------------------------------------------------------- coverage

test("every documented function is built or refused, and a refused one names itself and why", () => {
  const found = A.coverage();
  const unknown = Object.entries(found).filter(([, s]) => s !== "built" && s !== "refused").map(([n]) => n);
  assert.deepEqual(unknown, []);
  const documented = new Set(Object.values(A.DOCUMENTED).flat().map(n => n.toLowerCase()));
  assert.equal(Object.keys(found).length, documented.size);
  for (const [name, status] of Object.entries(found)) {
    if (status === "built") continue;
    assert.throws(() => run(`${name}()`), error => {
      assert.ok(error instanceof A.NotBuiltFunction, name);
      assert.ok(error.message.startsWith(`The Arcade function ${name} is not built in Studio's form engine: `), error.message);
      for (const word of ["yet", "scheduled", "later"]) assert.ok(!error.message.includes(` ${word}`), `${name}: ${error.message}`);
      return true;
    });
  }
});

test("the core a form uses is built", () => {
  for (const name of ["IIf", "When", "Decode", "IsEmpty", "DefaultValue", "Includes", "Count", "Text", "Number",
    "Round", "Date", "DateAdd", "DateDiff", "Today", "Now", "Concatenate", "Upper", "Lower", "Left", "Right", "Mid",
    "Find", "Replace", "Split", "Trim", "Boolean", "HasKey", "HasValue", "DomainName", "DomainCode", "Geometry"]) {
    assert.equal(A.status(name), "built", name);
  }
});

test("an unknown name and a value that is no function", () => {
  fails("Foo(1)", /'Foo' is not a function/);
  fails("var f = 1; f(2)", /is a Number, not a function/);
  fails("Number('1,365', ',###')", /Number is not built in Studio's form engine for a text pattern/);
});

test("the interpreter has no door to JavaScript: no eval, no Function, no network", () => {
  const source = require("node:fs").readFileSync(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "arcade.js"), "utf8");
  const code = source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "");
  for (const pattern of [/\beval\s*\(/, /new\s+Function\s*\(/, /\bfetch\s*\(/, /XMLHttpRequest/, /\bimport\s*\(/, /WebSocket/]) {
    assert.ok(!pattern.test(code), `arcade.js contains ${pattern}`);
  }
  fails("constructor", /'constructor' is not declared/);
  fails("var d = {}; d.constructor", /no key 'constructor'/);
  fails("__proto__", /'__proto__' is not declared/);
});
