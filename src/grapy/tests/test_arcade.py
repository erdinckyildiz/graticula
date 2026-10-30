"""grapy's Arcade interpreter (ADR-235) against the worked examples of Esri's public Arcade documentation —
developers.arcgis.com/arcade, the language guide and the function reference, read 2026-10-30. Each expected value is
the one the page prints; where a page's example depends on its own time zone or data, the test says what it changed.
"""
import math
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402,F401 — the package a toolbox imports; the interpreter is its private module
from grapy._arcade import ArcadeError, NotBuiltFunction, coverage, evaluate, run  # noqa: E402
from grapy._arcade import _library  # noqa: E402
from grapy._arcade._values import Date, DateOnly, Time, to_text  # noqa: E402


def text(script):
    return to_text(evaluate(script))


class _Case(unittest.TestCase):
    def equal(self, script, expected, places=None):
        found = evaluate(script)
        if places is not None:
            self.assertAlmostEqual(found, expected, places=places, msg=script)
        elif isinstance(expected, float) and math.isnan(expected):
            self.assertTrue(isinstance(found, float) and math.isnan(found), f"{script} -> {found!r}")
        else:
            self.assertEqual(found, expected, script)


class Language(_Case):
    """The guide's structure pages."""

    def test_variables_are_declared_with_var_and_named_without_regard_to_case(self):
        self.equal("var Total = 5; total + TOTAL", 10)
        self.equal("var x; x", None)
        self.equal("var a = 1, b = 2; a + b", 3)
        with self.assertRaisesRegex(ArcadeError, "'age' is not declared"):
            evaluate("age = 35;")  # the Variables page: assigning an undeclared name is an error
        self.equal("var x = 10; x = 'text'; x", "text")  # a variable may take a value of another type

    def test_a_variable_declared_in_a_block_outlives_it_and_functions_have_their_own_scope(self):
        self.equal("if (true) { var inside = 3 } inside", 3)  # "Arcade does not support block scope"
        self.equal("var g = 1; function f() { var g = 5; return g } f() + g", 6)  # a local hides the global
        self.equal("var g = 1; function f() { g = 7; return g } f(); g", 7)  # a global is reachable from a function
        with self.assertRaisesRegex(ArcadeError, "'local' is not declared"):
            evaluate("function f() { var local = 1; return local } f(); local")

    def test_returns_are_explicit_or_the_value_of_the_last_statement(self):
        # The Return page's examples.
        self.equal('return "Hello world";', "Hello world")
        self.equal("var total = 5 + 10; return total;", 15)
        self.equal("5 + 10;", 15)
        self.equal("var total = 5 + 10; total;", 15)
        self.equal("function calculateRatio(values, index){ var value = values[index]; value / Sum(values); } "
                   "calculateRatio([1, 3], 1)", 0.75)
        self.equal("return; 5", None)

    def test_functions_are_declared_with_function_and_called_without_regard_to_case(self):
        # The Functions page's example answers 66.67.
        self.equal("function calculatePercentage(value, total){ var p = (value / total) * 100; return Round(p, 2); } "
                   "CALCULATEPERCENTAGE(50, 75)", 66.67)
        self.equal("function fact(n) { if (n <= 1) { return 1 } return n * fact(n - 1) } fact(5)", 120)
        with self.assertRaisesRegex(ArcadeError, "takes 1 argument; 2 were given"):
            evaluate("function f(a) { return a } f(1, 2)")
        self.equal("var count = Count([1, 2, 3]); count", 3)  # a variable named like a function does not hide it

    def test_the_loops_page_examples(self):
        self.equal("var result = 0; for(var z=0; z < 100; z++) { result += z; } result", 4950)
        self.equal("var result = 0; for(var z=0; z < 100; z++) { if (z % 2 != 0) { continue; } result += z; } result",
                   2450)
        self.equal("var result = 0; for(var z=0; z < 100; z++) { if (z % 2 == 0) { continue; } result += z; } result",
                   2500)
        # for…in over an array gives its indexes: summing them over seven values gives 21, not 280.
        self.equal("var a = [10,20,30,40,50,60,70]; var s = 0; for (var i in a) { s += i } s", 21)
        self.equal("var a = [10,20,30]; var s = 0; for (var i in a) { s += a[i] } s", 60)
        self.equal("var s = 0; for (var v of [10, 20, 30]) { s += v } s", 60)
        self.equal("var d = {a: 1, b: 2}; var k = ''; for (var key in d) { k += key } k", "ab")
        self.equal("var d = {a: 1, b: 2}; var s = 0; for (var kv of d) { s += kv.value } s", 3)
        self.equal("var y = 0; while (y < 10) { y++ } y", 10)
        self.equal("var y = 0; for (;;) { y++; if (y == 4) { break } } y", 4)

    def test_conditions_and_logical_operands_are_booleans_never_cast(self):
        # The Type casting page: "if and for conditions must already be boolean"; logical operators do no casting.
        with self.assertRaisesRegex(ArcadeError, "must be a Boolean"):
            evaluate("if (1) { 2 }")
        with self.assertRaisesRegex(ArcadeError, "must be a Boolean"):
            evaluate("1 && true")
        self.equal("false && (1 / 0 > 0)", False)  # short-circuit
        self.equal("!false", True)
        self.equal("if (false) { 1 } else if (true) { 2 } else { 3 }", 2)

    def test_numbers_in_their_notations_and_the_constants(self):
        self.equal("0.255e3", 255)
        self.equal("0x1F + 0b101 + 0o17", 31 + 5 + 15)
        self.equal("PI", math.pi)
        self.equal("Infinity > 1e308", True)

    def test_arrays_cannot_have_missing_elements(self):
        self.equal("[10,20,30][0]", 10)
        self.equal("var z = [1,2,3]; z[3] = 4; Count(z)", 4)  # the next index may be assigned
        with self.assertRaisesRegex(ArcadeError, "missing elements"):
            evaluate("var z = [1,2,3]; z[1000] = 4;")
        self.equal("[1, 2, 'hello'][2]", "hello")

    def test_dictionaries_are_read_without_regard_to_case_and_with_safe_access(self):
        self.equal("var d = {Name: 'x'}; d.NAME + d['name']", "xx")
        self.equal("var d = {'full name': 'Ann'}; d['full name']", "Ann")
        self.equal("var y = 2020; var d = {Population_2020: 5}; d['Population_' + y]", 5)
        self.equal("var field1 = 1; var field2 = 2; var d = {field1, field2}; d.field2", 2)  # auto-keys
        self.equal("var d = null; d?.x", None)
        self.equal("var d = {a: {b: 1}}; d?.c?.b", None)
        with self.assertRaisesRegex(ArcadeError, "no key 'c'"):
            evaluate("var d = {a: 1}; d.c")

    def test_template_literals(self):
        # The Template literals page.
        self.equal("`Hello World`", "Hello World")
        self.equal('`My name is "Dave"`', 'My name is "Dave"')
        self.equal("`Corn: 5,000\nWheat: 4,000`", "Corn: 5,000\nWheat: 4,000")
        self.equal("var scores = [70, 85, 92, 98, 100, 78, 89]; "
                   "`The average score was ${Round(Average(scores), 2)}%.`", "The average score was 87.43%.")
        self.equal("var o = {a: `in ${1 + 1}`}; `out ${o.a}`", "out in 2")

    def test_comments_and_text_escapes(self):
        self.equal("// a comment\n1 /* another */ + 1", 2)
        self.equal("'it\\'s' + \"\\t\" + '\\u0041'", "it's\tA")

    def test_a_syntax_error_names_its_line(self):
        with self.assertRaises(ArcadeError) as raised:
            evaluate("var a = 1;\nvar b = ;")
        self.assertEqual(raised.exception.line, 2)

    def test_profile_variables_are_read_only(self):
        value, _ = run("$feature.a", {"$feature": _feature({"a": 4})})
        self.assertEqual(value, 4)
        with self.assertRaisesRegex(ArcadeError, "cannot be assigned"):
            run("$feature = 1", {"$feature": _feature({"a": 4})})
        with self.assertRaisesRegex(ArcadeError, "immutable"):
            run("$feature.a = 1", {"$feature": _feature({"a": 4}, immutable=True)})
        with self.assertRaisesRegex(ArcadeError, "not available"):
            evaluate("$map")


def _feature(attributes, immutable=False):
    from grapy._arcade._values import Dictionary, Feature
    return Feature(Dictionary(attributes), None, immutable=immutable)


class Operators(_Case):
    """The Operators and Type casting pages."""

    def test_equality_does_not_coerce(self):
        self.equal('10 == "10"', False)
        self.equal('10 != "10"', True)
        self.equal("[1,2,3] == [1,2,3]", False)  # objects are equal only when they are the same object
        self.equal("var a = [1]; a == a", True)
        self.equal("null == null", True)

    def test_relational_operators_cast_text_compared_with_a_number(self):
        self.equal('10 > "10"', False)
        self.equal('10 >= "10"', True)
        self.equal('9 < "10"', True)
        self.equal('11 < "10"', False)
        self.equal("'b' > 'a'", True)
        self.equal("[1] < [2]", False)
        with self.assertRaises(ArcadeError):
            evaluate("Date(2020, 0, 1) < Time(1, 0)")  # a Date compared with a Time is an error

    def test_plus_concatenates_when_either_side_is_text_and_otherwise_adds_numbers(self):
        self.equal('"5" + 1', "51")
        self.equal("'a' + null", "a")
        self.equal("null + 1", 1)
        self.equal("true + 1", 2)
        self.equal('"5" * 2', 10)
        self.equal('"abc" * 2', math.nan)
        self.equal("'a' + true", "atrue")

    def test_unary_operators_cast_to_numbers_and_increments_do_not(self):
        self.equal("+'  '", 0)
        self.equal("+null", 0)
        self.equal("-true", -1)
        self.equal("+'abc'", math.nan)
        self.equal("var t = 'a'; t++; t", math.nan)
        self.equal("var c = 1; var d = c++; d * 10 + c", 12)
        self.equal("var c = 1; var d = ++c; d * 10 + c", 22)

    def test_arithmetic_follows_ieee_doubles(self):
        self.equal("1 / 0", math.inf)
        self.equal("-1 / 0", -math.inf)
        self.equal("0 / 0", math.nan)
        self.equal("7 % 3", 1)
        self.equal("-7 % 3", -1)
        self.equal("var x = 10; x -= 2; x *= 3; x /= 4; x", 6)

    def test_bitwise_operators_work_on_32_bit_integers(self):
        self.equal("5 & 3", 1)
        self.equal("5 | 3", 7)
        self.equal("5 ^ 3", 6)
        self.equal("~5", -6)
        self.equal("1 << 3", 8)
        self.equal("-16 >> 2", -4)
        self.equal("-1 >>> 28", 15)

    def test_precedence(self):
        self.equal("1 + 2 * 3", 7)
        self.equal("(1 + 2) * 3", 9)
        self.equal("1 + 2 == 3 && 2 * 2 == 4", True)
        self.equal("true || false && false", True)
        self.equal("-2 * -2", 4)


class Math(_Case):
    """The Math functions page's examples."""

    def test_documented_results(self):
        self.equal("Abs(-3)", 3)
        self.equal("Abs(null)", 0)
        self.equal("Acos(0.3)", 1.266104, places=6)
        self.equal("Asin(0.3)", 0.304693, places=6)
        self.equal("Atan(1)", 0.785398, places=6)
        self.equal("Atan2(-1, -1)", -2.356194, places=6)
        self.equal("Average([0,5,10])", 5)
        self.equal("Average(0,5,10)", 5)
        self.equal("Ceil(2135.0905, 2)", 2135.1)
        self.equal("Constrain(5, 0, 10)", 5)
        self.equal("Constrain(-3, 0, 10)", 0)
        self.equal("Constrain(553, 0, 10)", 10)
        self.equal("Cos(1)", 0.540302, places=6)
        self.equal("Exp(2)", 7.389056, places=6)
        self.equal("Floor(2316.2562, 2)", 2316.25)
        self.equal("Log(10)", 2.302585, places=6)
        self.equal("Log(100, 10)", 2)
        self.equal("Log(8, 2)", 3)
        self.equal("Log(100, 15)", 1.7005483074552, places=12)
        self.equal("Log(5, -10)", math.nan)
        self.equal("Log(0, 10)", math.nan)
        self.equal("Max([23,56,89])", 89)
        self.equal("Max(23,5,120,43,9)", 120)
        self.equal("Mean([1,2,3,4,5,6,7,8,9])", 5)
        self.equal("Mean(1,2,3,4,5,6,7,8,9)", 5)
        self.equal("Min([23,56,89])", 23)
        self.equal("Min(23,5,120,43,9)", 5)
        self.equal("Pow(3, 2)", 9)
        self.equal("Product([1,2,3])", 6)
        self.equal("Product([])", 1)
        self.equal("Product([[1,2],[3,1]])", math.nan)
        self.equal("Product(1,1,1,1,1)", 1)
        self.equal("Product([{a: 10}])", math.nan)
        self.equal("Product()", 1)
        self.equal("Round(2316.2562, 2)", 2316.26)
        self.equal("Sign(1)", 1)
        self.equal("Sign(-3.2)", -1)
        self.equal("Sign(-Infinity)", -1)
        self.equal("Sign('hello')", math.nan)
        self.equal("Sign(0)", 0)
        self.equal("1 / Sign(0)", math.inf)
        self.equal("1 / Sign(-0)", -math.inf)
        # The page prints Sin(1) as 0.841741, a transposition of the sine of one radian, 0.841471.
        self.equal("Sin(1)", 0.841471, places=6)
        self.equal("Sqrt(9)", 3)
        self.equal("Stdev([23,56,89,12,45,78])", 27.5)
        self.equal("Stdev(23,56,89,12,45,78)", 27.5)
        self.equal("Sum([23,56,89,12,45,78])", 303)
        self.equal("Sum(23,56,89,12,45,78)", 303)
        self.equal("Tan(0.521)", 0.57389, places=5)
        self.equal("Variance([12,23,45,56,78,89])", 756.25)
        self.equal("Variance(12,23,45,56,78,89)", 756.25)

    def test_number_and_rounding(self):
        # Number(Date(1996,11,10)) is 850204800000 on the page, in Pacific time; grapy runs in UTC, so the same
        # instant is eight hours into the day.
        self.equal("Number(Date(1996,11,10,8,0,0))", 850204800000)
        self.equal("Number('12.5')", 12.5)
        self.equal("Number(true)", 1)
        self.equal("Number('abc')", math.nan)
        self.equal("Round(2.5)", 3)
        self.equal("Round(-2.5)", -2)
        self.equal("Round(1.005, 2)", 1.01)
        # The Number page's pattern examples.
        self.equal("Number('1,365', ',###')", 1365)
        self.equal("Number('abc10def', 'abc##def')", 10)
        self.equal("Number('10.456', '00.00##')", 10.456)
        self.equal("Number('10.4', '00.00##')", math.nan)
        self.equal("Number('12,12,456', ',##,###')", 1212456)
        self.equal("Number('-12,23,345', ',##,###;-,##,###')", -1223345)
        self.equal("Number('99.99%', '#.##%')", 0.9999)
        value = evaluate("Random()")
        self.assertTrue(0 <= value < 1)


class Texts(_Case):
    """The Text functions page's examples."""

    def test_documented_results(self):
        self.equal("Concatenate(['red', 'blue', 'green'], '/')", "red/blue/green")
        self.equal("Count('Graham County')", 13)
        self.equal("Find('380', 'Esri, 380 New York Street', 0)", 6)
        self.equal("Find('x', 'abc')", -1)
        self.equal("FromCharCode(88,89,90)", "XYZ")
        self.equal("FromCharCode(55356, 57097)", "\U0001F309")
        self.equal("FromCodePoint(88,89,90)", "XYZ")
        self.equal("FromCodePoint(127753)", "\U0001F309")
        self.equal("Left('the quick brown fox', 3)", "the")
        self.equal("Lower('HELLO')", "hello")
        self.equal("Mid('the quick brown fox', 4, 5)", "quick")
        self.equal("Proper('the quick brown fox', 'everyword')", "The Quick Brown Fox")
        self.equal("Proper('the quick brown fox', 'firstword')", "The quick brown fox")
        self.equal("Replace('the quick brown fox', 'brown', 'red')", "the quick red fox")
        self.equal("Replace('a-a-a', 'a', 'b', false)", "b-a-a")
        self.equal("Right('the quick brown fox', 3)", "fox")
        self.equal("Split('red,green,blue,orange', ',', 2)", ["red", "green"])
        self.equal("Split('a  b', ' ', -1, true)", ["a", "b"])
        self.equal('StandardizeFilename("USGS:Green River, Utah")', "USGS, Green River, Utah")
        self.equal("StandardizeGuid('{4e6f776d-c298-4b4b-86a4-57103b4d0f4a}', 'digits')",
                   "4e6f776dc2984b4b86a457103b4d0f4a")
        self.equal("StandardizeGuid('{4e6f776d-c298-4b4b-86a4-57103b4d0f4a}', 'digits-hyphen')",
                   "4e6f776d-c298-4b4b-86a4-57103b4d0f4a")
        self.equal("ToCharCode('XYZ')", 88)
        self.equal("ToCharCode('XYZ', 1)", 89)
        self.equal("ToCharCode('\\uFFFF\\uFFFE')", 65535)
        self.equal("ToCharCode('\U0001F309')", 55356)
        self.equal("ToCharCode('\U0001F309', 1)", 57097)
        self.equal("ToCodePoint('XYZ')", 88)
        self.equal("ToCodePoint('XYZ', 1)", 89)
        self.equal("ToCodePoint('\U0001F309')", 127753)
        self.equal("ToCodePoint('\U0001F309', 1)", 57097)
        self.equal("ToHex(100)", "64")
        self.equal('Concatenate("#", ToHex(65), ToHex(105), ToHex(225))', "#4169E1")
        self.equal("Trim('   hello world')", "hello world")
        self.equal("Upper('Hello')", "HELLO")
        self.equal("UrlEncode({itemID: '36ff9e8c13e042a58cfce4ad87f55d19', center: '43.567,-117.380'})",
                   "center=43.567%2C-117.380&itemID=36ff9e8c13e042a58cfce4ad87f55d19")

    def test_guids(self):
        import re
        self.assertRegex(evaluate("Guid()"), r"^\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}$")
        self.assertRegex(evaluate("Guid('digits')"), r"^[0-9a-f]{32}$")
        self.assertTrue(re.match(r"^\([0-9a-f-]{36}\)$", evaluate("Guid('digits-hyphen-parentheses')")))

    def test_number_formats(self):
        self.equal("Text(123, '0000')", "0123")
        self.equal("Text(123, '00')", "23")
        self.equal("Text(1234, '#,###')", "1,234")
        self.equal("Text(12345678.123, '#,###.00')", "12,345,678.12")
        self.equal("Text(1234.55, '$#,###.00')", "$1,234.55")
        self.equal("Text(1.236, '#.00')", "1.24")
        self.equal("Text(1234.5678, '#,##0.00#')", "1,234.568")
        self.equal("Text(-2, 'Floor #;Basement #')", "Basement 2")
        self.equal("Text(2, 'Floor #;Basement #')", "Floor 2")
        self.equal("Text(0.3, '#%')", "30%")
        self.equal("Text(100)", "100")

    def test_date_formats(self):
        # The page's own date examples print the moment it ran; these use a fixed one, a Tuesday.
        self.equal("Text(Date(2016, 9, 25, 8, 43, 11), 'dddd, MMMM D, Y @ h:m:s')", "Tuesday, October 25, 2016 @ 8:43:11")
        self.equal("Text(Date(2023, 8, 14, 10, 4, 49), 'ddd, MMM D, Y h:mm:ss A ZZ')", "Thu, Sep 14, 2023 10:04:49 AM +00:00")
        self.equal("Text(Date(2023, 0, 5, 13, 7, 9, 45), 'YYYY-MM-DD HH:mm:ss.SSS DDD d YY hh a')",
                   "2023-01-05 13:07:09.045 5 5 23 01 pm")
        self.equal("Text(Date(2023, 0, 5), '[Day] D')", "Day 5")

    def test_implicit_text_of_values(self):
        self.equal("Text([1, 'a', true, null])", '[1,"a",true,null]')
        self.equal("Text({a: 1})", '{"a":1}')
        self.equal("Text(Date(2016, 9, 20, 17, 41, 37))", "2016-10-20T17:41:37+00:00")
        self.equal("Text(DateOnly(2007, 11, 1))", "2007-12-01")
        self.equal("Text(Time(6, 30, 0))", "06:30:00")
        self.equal("Text(0.1 + 0.2)", "0.30000000000000004")
        self.equal("'' + 1e21", "1e+21")


class Logical(_Case):
    """The Logical functions page's examples."""

    def test_documented_results(self):
        self.equal("Boolean('true')", True)
        self.equal("Boolean(0)", False)
        self.equal("Boolean(1)", True)
        self.equal("Boolean('hello')", False)
        self.equal("Decode(2, 1, 'Residential', 2, 'Commercial', 3, 'Mixed', 'Other')", "Commercial")
        self.equal("Decode(9, 1, 'Residential', 2, 'Commercial', 3, 'Mixed', 'Other')", "Other")
        self.equal("DefaultValue(null, 'no data')", "no data")
        self.equal("DefaultValue('', 'no data')", "no data")
        self.equal("Count(DefaultValue([], 'no data'))", 0)
        self.equal('Equals(1, "1")', False)
        self.equal('var testVal = "test"; Equals(testVal, "test")', True)
        self.equal("var array1 = Array(5); var array2 = Array(5); Equals(array1, array2)", False)
        self.equal("var array1 = Array(5); Equals(array1, array1)", True)
        self.equal("IIf(999 < 1000000, 'below', 'above')", "below")
        self.equal("IsEmpty(null)", True)
        self.equal("IsEmpty('hello world')", False)
        self.equal("IsEmpty([])", False)
        self.equal("IsEmpty([], true)", True)
        self.equal("IsEmpty([1,null,3], true)", False)
        self.equal("var dict = {}; IsEmpty(dict, true)", True)
        self.equal('var dict = {a: ""}; IsEmpty(dict, true)', False)
        self.equal("IsNan(Infinity / Infinity)", True)
        self.equal("IsNan('4')", False)
        self.equal("IsNan(0 / 0)", True)
        self.equal("TypeOf(true)", "Boolean")
        self.equal("TypeOf(Now())", "Date")
        self.equal("TypeOf('a') + TypeOf(1) + TypeOf([]) + TypeOf({}) + TypeOf(null)", "StringNumberArrayDictionaryNull")
        script = ("var density = {d}; When(density < 50, 'low', density >= 50 && density < 100, 'medium', "
                  "density >= 100, 'high', 'n/a')")
        self.equal(script.format(d=10), "low")
        self.equal(script.format(d=60), "medium")
        self.equal(script.format(d=100), "high")
        self.equal(script.format(d="NaN"), "n/a")


class Arrays(_Case):
    """The Array functions page's examples."""

    def test_documented_results(self):
        even = "function isEven(value) { return value % 2 == 0 } "
        self.equal(even + "All([1,2,3,4,5], isEven)", False)
        self.equal(even + "Any([1,2,3,4,5], isEven)", True)
        self.equal(even + "None([1,2,3,4,5], isEven)", False)
        self.equal(even + "Filter([1,2,3,4,5], isEven)", [2, 4])
        self.equal("Array(5)", [None] * 5)
        self.equal('Array(2, "hello")', ["hello", "hello"])
        self.equal("Array(3, 1)", [1, 1, 1])
        self.equal("var people = [{name: 'a'}]; var copied = Array(people); people[0] == copied[0]", True)
        self.equal("var people = [{name: 'a'}]; var deep = Array(people, true); people[0] == deep[0]", False)
        self.equal("Back(['orange','purple','gray'])", "gray")
        self.equal("Count([12,21,32,44,58,63])", 6)
        self.equal('DefaultValue([23,4,null,36,901], 4, "No data")', 901)
        self.equal('DefaultValue([23,4,null,36,901], 5, "No data")', "No data")
        self.equal('DefaultValue([23,4,null,36,901], 2, "No data")', "No data")
        self.equal('DefaultValue([23,4,[0,0,1,1,0],36,901], [2,3], "No data")', 1)
        self.equal('DefaultValue([23,4,[0,0,1,1,0],36,901], [2,10], "No data")', "No data")
        self.equal('DefaultValue([23,4,[0,0,1,1,0],36,901], [10,3], "No data")', "No data")
        self.equal("Distinct([1,1,2,1,1,2,2,3,4,5])", [1, 2, 3, 4, 5])
        self.equal("Distinct('high','medium','low',0,'high','high','low')", ["high", "medium", "low", 0])
        self.equal("var a = ['orange','purple','gray']; Erase(a, 1); a", ["orange", "gray"])
        self.equal("var a = ['orange','purple','gray']; Erase(a, -1); a", ["orange", "purple"])
        self.equal("First(['orange','purple','gray'])", "orange")
        self.equal("First([])", None)
        self.equal("Front(['orange','purple','gray'])", "orange")
        with self.assertRaises(ArcadeError):
            evaluate("Front([])")
        self.equal("HasValue([23,4,null,36,901], 4)", True)
        self.equal("HasValue([23,4,null,36,901], 5)", False)
        self.equal("HasValue([23,4,null,36,901], 2)", False)
        self.equal("HasValue([23,4,[0,0,1,1,0],36,901], [2,4])", True)
        self.equal("HasValue([23,4,[0,0,1,1,0],36,901], [2,10])", False)
        self.equal("HasValue([23,4,[0,0,1,1,0],36,901], [10,5])", False)
        weather = ("var data = [{weather: {precipitation: {values: [0.4, 0, 0, null, 0.1, 0.8, 1]}}}]; ")
        self.equal(weather + 'HasValue(data, [0,"weather","precipitation","values",6])', True)
        self.equal(weather + 'DefaultValue(data, [0,"weather","precipitation","values",6], "n/a")', 1)
        self.equal("Includes(['orange','purple','gray'], 'purple')", True)
        self.equal("Includes(['orange','purple','gray'], 'red')", False)
        self.equal("indexof([1,2,3,4], 3)", 2)
        self.equal("var a = ['orange','purple','gray']; Insert(a, 1, 'yellow'); a", ["orange", "yellow", "purple", "gray"])
        self.equal("var a = ['orange','purple','gray']; Insert(a, -1, 'yellow'); a",
                   ["orange", "purple", "yellow", "gray"])
        self.equal("function toCelsius(f) { return Round((f - 32) * 5 / 9, 2) } Map([82,67,96,55,34], toCelsius)",
                   [27.78, 19.44, 35.56, 12.78, 1.11])
        self.equal("var a = ['orange','purple','gray']; [Pop(a), a]", ["gray", ["orange", "purple"]])
        self.equal("Push(['orange','purple','gray'], 'red')", 4)
        cities = ("var cities = [{name: 'Columbus', population: 913921}, {name: 'Dayton', population: 137644}]; "
                  "function mostPopulated(a, b) { return IIf(a.population > b.population, a, b) } ")
        self.equal(cities + "Reduce(cities, mostPopulated).name", "Columbus")
        self.equal(cities + "var la = {name: 'Los Angeles', population: 3898747}; Reduce(cities, mostPopulated, la).name",
                   "Los Angeles")
        self.equal("var a = ['orange','purple','gray']; Resize(a, 5); a", ["orange", "purple", "gray", None, None])
        self.equal("var a = ['orange','purple','gray']; Resize(a, 5, 'red'); a",
                   ["orange", "purple", "gray", "red", "red"])
        self.equal("var a = ['orange','purple','gray']; Resize(a, 1); a", ["orange"])
        self.equal("Reverse(['orange','purple','gray'])", ["gray", "purple", "orange"])
        colours = "var c = ['orange','purple','gray','red','blue']; "
        self.equal(colours + "Slice(c, 1, 3)", ["purple", "gray"])
        self.equal(colours + "Slice(c, 3)", ["red", "blue"])
        self.equal(colours + "Count(Slice(c))", 5)
        self.equal(colours + "Slice(c, -1)", ["blue"])
        self.equal("Sort([1,'a','$','A'])", ["$", 1, "A", "a"])
        self.equal("Sort([10, 9, 1, null, 2])", [1, 2, 9, 10, None])
        people = ("var p = [{name: 'Sam', age: 25}, {name: 'Bob', age: 27}, {name: 'Emma', age: 24}]; "
                  "function compareAge(a, b) { return a.age - b.age } ")
        self.equal(people + "var s = Sort(p, compareAge); s[0].name + s[1].name + s[2].name", "EmmaSamBob")
        self.equal("Splice(['orange','purple'], 1, 2, 'red')", ["orange", "purple", 1, 2, "red"])
        self.equal("Splice([1,2], [3,4])", [1, 2, 3, 4])
        self.equal("Top([43,32,19,0,3,55], 3)", [43, 32, 19])


class Dictionaries(_Case):
    """The Dictionary functions page's examples."""

    towns = "var d = Dictionary('Port Hope', 16214, 'Grafton', '<1000', 'Cobourg', 18519); "

    def test_documented_results(self):
        self.equal(self.towns + "Count(d)", 3)
        self.equal("var d = Dictionary('field1', 1, 'field2', 2); return d.field1 + d.field2", 3)
        self.equal("var d = Dictionary('{\"id\": 1, \"population\": 200, \"city\": \"Spencer, ID\"}'); d.population",
                   200)
        self.equal(self.towns + "Erase(d, 'Grafton'); GetKeys(d)", ["Cobourg", "Port Hope"])
        self.equal('FromJSON("true")', True)
        self.equal('fromJSON("731.1")', 731.1)
        self.equal("var d = fromJSON('{\"kids\": 3, \"adults\": 4 }'); d.kids + d.adults", 7)
        self.equal("fromJSON('[\"one\", 2, \"three\", false]')", ["one", 2, "three", False])
        self.equal('fromJSON("null")', None)
        self.equal(self.towns + "GetKeys(d)", ["Cobourg", "Grafton", "Port Hope"])
        self.equal(self.towns + "GetValues(d)", [18519, "<1000", 16214])
        self.equal(self.towns + "HasKey(d, 'Cobourg')", True)
        self.equal(self.towns + "HasValue(d, 'Cobourg')", True)
        self.equal("var d = {Cobourg: null}; HasValue(d, 'Cobourg')", False)
        self.equal("var d = {}; HasValue(d, 'Cobourg')", False)
        self.equal(self.towns + "Insert(d, 'Colborne', 1474); Count(d) + d.Colborne", 1478)
        self.equal(self.towns + "Insert(d, 'Grafton', 1200); d.Grafton", 1200)
        data = "var data = {time: Date(2024, 0, 24, 12), weather: {precipitation: {value: 0.4, values: [0.4, 0, 0, null]}}}; "
        self.equal(data + "Year(DefaultValue(data, 'time', 'No time logged'))", 2024)
        self.equal("var data = {time: ''}; DefaultValue(data, 'time', 'No time logged')", "No time logged")
        self.equal(data + "DefaultValue(data, 'conditions', 'n/a')", "n/a")
        self.equal(data + 'DefaultValue(data, ["weather", "precipitation", "value"], 0)', 0.4)
        self.equal(data + 'DefaultValue(data, ["weather", "wind", "value"], "n/a")', "n/a")
        self.equal(data + 'DefaultValue(data, ["weather", "precipitation", "values", 3], 0)', 0)
        self.equal(data + 'HasValue(data, ["weather", "precipitation", "value"])', True)
        self.equal("var inputDict = {company: {name: 'x'}}; var c = Dictionary(inputDict); inputDict.company == c.company",
                   True)
        self.equal("var inputDict = {company: {name: 'x'}}; var c = Dictionary(inputDict, true); "
                   "inputDict.company == c.company", False)


class Dates(_Case):
    """The Date functions page's examples. INFERRED (ADR-235 §5.8): grapy runs in UTC, where the page ran in Pacific
    time; examples that print a zone are checked by their parts."""

    def test_constructors_and_parts(self):
        self.equal("Month(Date(1987,05,02)) + Day(Date(1987,05,02))", 5 + 2)
        self.equal("Hour(Date(1990, 10, 2, 2, 23, 0, 0, '-05:00'))", 2)
        self.equal("Number(Date('2016-10-20T17:41:37+00:00'))", 1476985297000)
        self.equal("Hour(Date('2016-10-20T17:41:37'))", 17)
        self.equal("Number(Date(1476987783555))", 1476987783555)
        self.equal("Number(Date(null))", 0)
        self.equal("Day(DateOnly(1996, 11, 10))", 10)
        self.equal("Hour(Date(2023, 1, 1, 12, 59, 23))", 12)
        self.equal("Hour(Time(2, 59, 23))", 2)
        self.equal("ISOMonth(Date(1980, 11, 31))", 12)
        self.equal("ISOMonth(DateOnly(1996, 0, 10))", 1)
        self.equal("ISOWeek(Date(1980, 11, 31))", 1)
        self.equal("ISOWeek(DateOnly(1996, 11, 10))", 50)
        self.equal("ISOWeekday(Date(1980, 11, 31))", 3)
        self.equal("ISOWeekday(DateOnly(1996, 11, 10))", 2)
        self.equal("ISOYear(Date(1980, 11, 31))", 1981)
        self.equal("ISOYear(DateOnly(1996, 11, 10))", 1996)
        self.equal("Millisecond(Date(2023, 1, 1, 12, 59, 23, 999))", 999)
        self.equal("Millisecond(Time(2, 59, 23, 450))", 450)
        self.equal("Minute(Date(2013, 1, 1, 2, 15, 23))", 15)
        self.equal("Minute(Time(2, 59, 23))", 59)
        self.equal("Month(Date(1980, 11, 31))", 11)
        self.equal("Month(DateOnly(1996, 0, 31))", 0)
        self.equal("Second(Date(2023, 1, 1, 2, 59, 01))", 1)
        self.equal("Second(Time(2, 59, 23))", 23)
        self.equal("Week(Date(1974,0,3))", 0)
        self.equal("Week(Date(1974,0,3), 4)", 1)
        self.equal("Week(Date(1974,0,3), 5)", 0)
        self.equal("Week(Date(1945,8,23))", 38)
        self.equal("Week(Date(2022,7,20))", 33)
        self.equal("Week(DateOnly(1996, 11, 10))", 49)
        self.equal("Weekday(Date(1980, 11, 31))", 3)
        self.equal("Weekday(DateOnly(1996, 11, 10))", 2)
        self.equal("Year(DateOnly(1996, 11, 10))", 1996)
        self.equal("Month(Date(2020, 12, 1))", 0)  # month 12 rolls over to January of the next year
        self.equal("Year(Date(2020, 12, 1))", 2021)

    def test_date_only_and_time(self):
        self.equal("Text(DateOnly(1996, 11, 10))", "1996-12-10")
        self.equal("Text(DateOnly(1476987783555))", "2016-10-20")
        self.equal('Text(DateOnly("2023-05-11T13:43:18.990+01:00"))', "2023-05-11")
        self.equal("Text(DateOnly(Date(2008,10,11,10,30)))", "2008-11-11")
        self.equal("DateOnly(1996, 13, 10)", None)
        self.equal("Text(Time(13, 20))", "13:20:00")
        self.equal("Text(Time(2, 59, 23))", "02:59:23")
        self.equal("Text(Time(15, 47, 0, 474))", "15:47:00.474")
        self.equal("Text(Time(8119800))", "02:15:19.800")
        self.equal("Time(86400001)", None)
        self.equal('Text(Time("1:20 PM"))', "13:20:00")
        self.equal("Text(Time(Date(2008,10,11,10,30)))", "10:30:00")
        self.equal("Text(Time(Time(23,32,0)))", "23:32:00")
        self.equal("Time(25, 0)", None)
        self.equal("Text(Date(DateOnly(2022,10,11), Time('11:20 am')))", "2022-11-11T11:20:00+00:00")

    def test_date_add_and_date_diff(self):
        self.equal("var d = DateAdd(Date(2023,9,1,12,0), 7, 'days'); [Month(d), Day(d), Hour(d)]", [9, 8, 12])
        self.equal("Text(DateAdd(DateOnly(2023,5,4), 7, 'days'))", "2023-06-11")
        self.equal("Text(DateAdd(DateOnly(2023,5,4), 12, 'hours'))", "2023-06-04")
        self.equal("Text(DateAdd(Time(11,30), 7, 'hours'))", "18:30:00")
        self.equal('Text(DateAdd(Time(11,30), 90, "seconds"))', "11:31:30")
        self.equal("Text(DateAdd(Time(11,30), 25, 'hours'))", "12:30:00")
        self.equal("DateDiff(Time(23,0), Time(9,0), 'hours')", 14)
        self.equal("Text(DateAdd(Date(2020, 0, 31), 1, 'months'), 'Y-MM-DD')", "2020-02-29")
        self.equal("DateDiff(Date(2020, 0, 2), Date(2020, 0, 1))", 86400000)
        self.equal("DateDiff(Date(2020, 0, 2), Date(2020, 0, 1), 'hours')", 24)
        self.equal("DateDiff(Date(2021, 0, 1), Date(2020, 0, 1), 'years')", 1)
        self.equal("DateDiff(Date(2020, 2, 16), Date(2020, 1, 1), 'months')", 1 + 15 / 31)
        self.equal("Floor(DateDiff(DateOnly(2023, 0, 1), DateOnly(1996, 11, 10), 'years'))", 26)

    def test_time_zones(self):
        self.equal("TimeZoneOffset(Date(2011, 10, 11, 8, 0, 0, 0, '-05:00'))", -18000000)
        self.equal("TimeZone(Date(2024, 1, 9, 0, 0, 0, 0, '+07:00'))", "+07:00")
        self.equal("Hour(ChangeTimeZone(Date(2011, 10, 11, 8, 0, 0, 0, '-05:00'), '-08:00'))", 5)
        self.equal("TimeZone(ToUTC(Now()))", "UTC")
        self.equal("TimeZone(Now())", "UTC")
        self.equal("Hour(Today()) + Minute(Today())", 0)
        self.assertIsInstance(evaluate("Timestamp()"), Date)
        self.assertIsInstance(evaluate("DateOnly()"), DateOnly)
        self.assertIsInstance(evaluate("Time()"), Time)


class Geometry(_Case):
    """The Geometry functions, on hand-worked shapes."""

    square = "var sq = Polygon({rings: [[[0,0],[0,10],[10,10],[10,0],[0,0]]], spatialReference: {wkid: 3857}}); "

    def test_construction_and_properties(self):
        self.equal(self.square + "TypeOf(sq)", "Polygon")
        self.equal(self.square + "sq.rings[0][1].y", 10)
        self.equal(self.square + "sq.spatialReference.wkid", 3857)
        self.equal(self.square + "RingIsClockwise(sq.rings[0])", True)
        self.equal("var p = Point({x: 1, y: 2, spatialReference: {wkid: 4326}}); p.x + p.y", 3)
        self.equal("var p = Point('{\"x\": 1, \"y\": 2}'); TypeOf(p)", "Point")
        self.equal("Count(Polyline({paths: [[[0,0],[3,4]]]}).paths[0])", 2)
        self.equal("Count(Multipoint({points: [[0,0],[3,4]]}).points)", 2)
        self.equal(self.square + "var e = Extent(sq); e.xmax - e.xmin", 10)
        self.equal("Extent({xmin: 0, ymin: 0, xmax: 2, ymax: 3}).height", 3)
        self.equal(self.square + "Text(Point({x: 1, y: 2, spatialReference: {wkid: 3857}}))",
                   '{"x":1,"y":2,"spatialReference":{"wkid":3857}}')
        self.equal("var d = Dictionary(Point({x: 1, y: 2})); d.x = d.x * 2; Geometry(d).x", 2)
        with self.assertRaisesRegex(ArcadeError, "Curves"):
            evaluate("Polygon({curveRings: []})")

    def test_measures(self):
        self.equal(self.square + "Area(sq)", 100)
        self.equal(self.square + "Area(sq, 'square-kilometers')", 0.0001)
        self.equal(self.square + "Length(sq)", 40)
        self.equal(self.square + "Length(sq, 'kilometers')", 0.04)
        self.equal("Length(Polyline({paths: [[[0,0],[3,4]]]}))", 5)
        self.equal("Distance(Point({x: 0, y: 0}), Point({x: 3, y: 4}))", 5)
        # One degree of longitude on the equator of WGS 84: 111,319.49 m (a = 6,378,137 m, 2πa/360).
        self.equal("DistanceGeodetic(Point({x: 0, y: 0, spatialReference: {wkid: 4326}}), "
                   "Point({x: 1, y: 0, spatialReference: {wkid: 4326}}), 'meters')", 111319.49, places=1)
        self.equal("LengthGeodetic(Polyline({paths: [[[0,0],[1,0]]], spatialReference: {wkid: 4326}}))", 111319.49,
                   places=1)
        area = evaluate("AreaGeodetic(Polygon({rings: [[[0,0],[0,1],[1,1],[1,0],[0,0]]], spatialReference: {wkid: 4326}}),"
                        " 'square-kilometers')")
        self.assertAlmostEqual(area, 12308.78, delta=1.0)  # a one-degree cell at the equator on WGS 84
        with self.assertRaisesRegex(ArcadeError, "geographic"):
            evaluate("Area(Polygon({rings: [[[0,0],[0,1],[1,1],[0,0]]], spatialReference: {wkid: 4326}}), "
                     "'square-meters')")

    def test_relations_and_overlays(self):
        self.equal(self.square + "Intersects(sq, Point({x: 5, y: 5}))", True)
        self.equal(self.square + "Contains(sq, Point({x: 5, y: 5}))", True)
        self.equal(self.square + "Within(Point({x: 5, y: 5}), sq)", True)
        self.equal(self.square + "Disjoint(sq, Point({x: 50, y: 5}))", True)
        self.equal(self.square + "Touches(sq, Point({x: 10, y: 5}))", True)
        self.equal(self.square + "Crosses(Polyline({paths: [[[-1,5],[11,5]]]}), sq)", True)
        self.equal(self.square + "Overlaps(sq, Polygon({rings: [[[5,5],[5,15],[15,15],[15,5],[5,5]]]}))", True)
        self.equal(self.square + "EnvelopeIntersects(sq, Point({x: 10, y: 10}))", True)
        self.equal(self.square + "Relate(sq, Point({x: 5, y: 5}), 'T*****FF*')", True)
        self.equal(self.square + "Equals(sq, Polygon({rings: [[[10,0],[0,0],[0,10],[10,10],[10,0]]]}))", True)
        self.equal(self.square + "Area(Intersection(sq, Polygon({rings: [[[5,5],[5,15],[15,15],[15,5],[5,5]]]})))", 25)
        self.equal(self.square + "Area(Union(sq, Polygon({rings: [[[5,5],[5,15],[15,15],[15,5],[5,5]]]})))", 175)
        self.equal(self.square + "Area(Union([sq, Polygon({rings: [[[5,5],[5,15],[15,15],[15,5],[5,5]]]})]))", 175)
        self.equal(self.square + "Area(Difference(sq, Polygon({rings: [[[5,5],[5,15],[15,15],[15,5],[5,5]]]})))", 75)
        self.equal(self.square + "Area(SymmetricDifference(sq, Polygon({rings: [[[5,5],[5,15],[15,15],[15,5],[5,5]]]})))",
                   150)
        self.equal(self.square + "Intersection(sq, Point({x: 50, y: 50}))", None)
        self.equal("Area(ConvexHull(Multipoint({points: [[0,0],[4,0],[0,4],[1,1]]})))", 8)

    def test_construction_functions(self):
        self.equal(self.square + "var c = Centroid(sq); [c.x, c.y]", [5, 5])
        self.equal(self.square + "Intersects(sq, Centroid(sq, 'labelPoint'))", True)
        # A buffer is a polygon of 64 sides (INFERRED, ADR-235 §5.8): 32 r² sin(2π/64) = 313.65 for r = 10.
        self.equal("Area(Buffer(Point({x: 0, y: 0, spatialReference: {wkid: 3857}}), 10))", 313.65, places=2)
        self.equal("Area(Buffer(Point({x: 0, y: 0, spatialReference: {wkid: 3857}}), 1, 'kilometers')) > 3.1e6", True)
        self.equal(self.square + "Area(Clip(sq, Extent({xmin: 0, ymin: 0, xmax: 5, ymax: 5})))", 25)
        self.equal("Count(Densify(Polyline({paths: [[[0,0],[10,0]]]}), 1).paths[0])", 11)
        self.equal(self.square + "IsSimple(sq)", True)
        self.equal("IsSelfIntersecting(Polygon({rings: [[[0,0],[10,10],[10,0],[0,10],[0,0]]]}))", True)
        self.equal("Count(MultiPartToSinglePart(Multipoint({points: [[0,0],[1,1]]})))", 2)
        self.equal("var r = Rotate(Polyline({paths: [[[0,0],[1,0]]]}), 90, Point({x: 0, y: 0})); "
                   "Round(r.paths[0][1].y, 9)", 1)
        self.equal("Angle(Point({x: 0, y: 0}), Point({x: 0, y: 1}))", 90)
        self.equal("Angle(Point({x: 1, y: 0}), Point({x: 0, y: 0}), Point({x: 0, y: 1}))", 90)
        self.equal("Bearing(Point({x: 0, y: 0}), Point({x: 1, y: 0}))", 90)
        self.equal("Bearing(Point({x: 0, y: 0}), Point({x: 0, y: -1}))", 180)
        self.equal("Bearing(Point({x: 0, y: 1}), Point({x: 0, y: 0}), Point({x: 1, y: 0}))", 90)

    def test_feature_functions_on_a_feature_made_in_the_script(self):
        script = "var f = Feature(Point({x: 1, y: 2}), 'city_name', 'Spokane', 'population', 210721); "
        self.equal(script + "f.city_name", "Spokane")
        self.equal(script + "Geometry(f).y", 2)
        self.equal(script + "HasKey(f, 'population')", True)
        self.equal(script + "FeatureInFilter(f, 'population > 200000')", True)
        self.equal(script + "var limit = 300000; FeatureInFilter(f, 'population > @limit')", False)
        self.equal(script + "FeatureInFilter(f, '')", True)
        self.equal(script + "f.population = 1; f.population", 1)
        self.equal(script + "SetGeometry(f, Point({x: 9, y: 9})); Geometry(f).x", 9)
        self.equal("var f = Feature({attributes: {a: 1}, geometry: {x: 10, y: 20}}); Geometry(f).x + f.a", 11)
        self.equal("var f = Feature('{\"geometry\": {\"x\": 10, \"y\": 20}, \"attributes\": {\"hello\": 10}}'); f.hello",
                   10)
        self.equal("var f = Feature(Point({x: 1, y: 2}), {hello: 10}); f.hello", 10)
        self.equal("var f = Feature(Point({x: 1, y: 2}), {hello: 10}); var g = Feature(f); g.hello = 3; f.hello", 10)
        self.equal("TimeReceived(Feature(null, 'a', 1))", None)
        self.equal("Expects(Feature(null, 'a', 1), 'a')", None)


class Coverage(unittest.TestCase):
    """ADR-235 §5.3: every function of the function reference is built or refused with its reason — and one refused
    says so by name."""

    def test_every_documented_function_has_a_status(self):
        found = coverage()
        self.assertEqual([n for n, s in found.items() if s not in ("built", "refused")], [])
        counts = {s: sum(1 for v in found.values() if v == s) for s in ("built", "refused")}
        self.assertEqual(sum(counts.values()), len(found))
        self.assertEqual(len(found), len({n.lower() for names in _library.DOCUMENTED.values() for n in names}))
        self.assertEqual(counts, {"built": 188, "refused": 26})

    def test_a_refused_function_names_itself_and_why(self):
        for name, status in coverage().items():
            if status == "built":
                continue
            with self.assertRaises(NotBuiltFunction, msg=name) as raised:
                evaluate(f"{name}()")
            self.assertIn(f"The Arcade function {name} is refused in grapy: ", str(raised.exception))
            for word in ("yet", "scheduled", "later", "batch"):
                self.assertNotIn(word, str(raised.exception), name)

    def test_unknown_names_and_values_that_are_no_function(self):
        with self.assertRaisesRegex(ArcadeError, "'Foo' is not a function"):
            evaluate("Foo(1)")
        with self.assertRaisesRegex(ArcadeError, "is a Number, not a function"):
            evaluate("var f = 1; f(2)")

    def test_console_collects_its_lines(self):
        value, console = run("Console('a', 1); Console({b: 2}); 5")
        self.assertEqual((value, console), (5, ['a 1', '{"b":2}']))
        self.assertEqual(evaluate("GetEnvironment().timeZone"), "UTC")


if __name__ == "__main__":
    unittest.main()
