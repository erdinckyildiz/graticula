using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's expression evaluator, <c>wwwroot/forms/xlsform-eval.js</c>, against the specifications — ADR-287
/// condition 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>No server.</b> The evaluator is a script with no dependencies, so it is loaded into a blank Chrome tab from the
/// source tree and called there. That keeps these tests about the evaluator and lets them run where no Graticula is
/// listening; they still need Chrome, and fail rather than skip without it, as the rest of this suite does.
/// </para>
/// <para>
/// <b>The specifications' own examples are cited where they are used</b>: the ODK XForms specification
/// (getodk.github.io/xforms-spec, read 2026-11-09) and XPath 1.0 (w3.org/TR/xpath-10), which it extends. The ODK page
/// gives few worked examples — <c>decimal-time()</c>'s noon and 6 pm, <c>regex()</c>'s <c>'[0-9]+'</c>,
/// <c>once(random())</c>, <c>sum()</c> counting an empty node as 0, <c>indexed-repeat()</c>'s equivalence — and every
/// one of them is here. The rest are this project's, chosen to pin a rule the specification states in words.
/// </para>
/// <para>
/// <b>The fixed context</b> (see <see cref="Harness"/>): the present is 2024-02-29T22:15:30.250Z on a device at +03:00,
/// so it is already 1 March locally — today() has to know that. random() always returns 0.25.
/// </para>
/// </remarks>
public sealed class XlsformEvaluatorTests : IClassFixture<XlsformEvaluatorTests.Page>
{
    private readonly Page _page;

    /// <summary>Creates the tests over the shared page.</summary>
    /// <param name="page">The tab the script is loaded in.</param>
    public XlsformEvaluatorTests(Page page) => _page = page;

    /// <summary>The form the expressions are evaluated in, as the client would describe it.</summary>
    private const string Harness = """
        (function () {
          var answers = {
            a: '', n: '5', m: '2.5', name: 'Ada', age: '42', cat: 'fruit',
            d: '2024-02-29', start: '2024-02-29T08:00:00+03:00', end: '2024-02-29T10:30:00+03:00',
            fruits: 'apple,pear', fruitsx: 'apple pear', mixed: 'apple, pear leek',
            rep: [{}, {}, {}], cost: ['10', '', '2.5'], checked: ['1', '0', '5'],
            loc: '41.0082 28.9784 0 5',
            corners: ['0 0 0 0', '0 1 0 0', '1 1 0 0', '1 0 0 0']
          };
          var nested = { cost: { 'rep3': '2.5', 'rep1': '10' }, room: { 'building1/floor1': 'A', 'building2/floor3': 'B' } };
          var lists = {
            food: [
              { name: 'apple', label: 'Apple', 'label::Türkçe': 'Elma', cat: 'fruit' },
              { name: 'leek', label: 'Leek', 'label::Türkçe': 'Pırasa', cat: 'veg' },
              { name: 'pear', label: 'Pear', 'label::Türkçe': 'Armut', cat: 'fruit' }
            ],
            nums: [1, 2, 3, 4, 5, 6, 7, 8].map(function (i) { return { name: 'n' + i, label: String(i) }; })
          };
          var prices = [{ code: 'A1', price: '3.5' }, { code: 'B2', price: '7' }];
          window.__base = {
            value: function (name, path) {
              if (path) {
                var key = path.map(function (p) { return p.repeat + p.index; }).join('/');
                return (nested[name] || {})[key];
              }
              return answers[name];
            },
            current: '',
            position: 2,
            timezoneOffset: 180,
            now: function () { return new Date('2024-02-29T22:15:30.250Z'); },
            random: function () { return 0.25; },
            choices: function (list) { return lists[list]; },
            property: function (name) { return { username: 'ada', email: 'ada@example.org', online: true, utcOffset: 3, deviceId: 'dev-1' }[name]; },
            listOf: function (question) { return question === 'fruits' || question === 'fruitsx' ? 'food' : null; },
            pulldata: function (file, column, keyColumn, key) {
              if (file !== 'prices') { return ''; }
              for (var i = 0; i < prices.length; i++) { if (prices[i][keyColumn] === key) { return prices[i][column]; } }
              return '';
            }
          };
          window.__ctx = function (extra) { return Object.assign({}, window.__base, extra || {}); };
          window.__run = function (e, extra) { return GraticulaXlsform.evaluateString(e, __ctx(extra)); };
          window.__num = function (e, extra) { return GraticulaXlsform.evaluateNumber(e, __ctx(extra)); };
          window.__err = function (f) { try { f(); return 'no error'; } catch (e) { return e.message + '|' + e.index; } };
          return true;
        })()
        """;

    /// <summary>Expression, expected string value, extra context (a JavaScript object literal).</summary>
    public static TheoryData<string, string, string> Cases => new()
    {
        // ----- Operators and precedence (XPath 1.0 §3: or < and < equality < relational < additive < multiplicative < unary)
        { "1 + 2 * 3", "7", "{}" },
        { "(1 + 2) * 3", "9", "{}" },
        { "10 - 4 - 3", "3", "{}" },
        { "2 * 3 mod 4", "2", "{}" },
        { "7 div 2", "3.5", "{}" },
        { "-7 mod 3", "-1", "{}" },           // XPath 1.0 §3.5: the sign of the dividend
        { "7 mod -3", "1", "{}" },
        { "- 2 * 3", "-6", "{}" },
        { "--3", "3", "{}" },
        { "1 < 2 = 1", "true", "{}" },        // (1 < 2) = 1: a boolean against a number compares as booleans
        { "3 > 2 > 1", "false", "{}" },       // (true) > 1 → 1 > 1
        { "1 = 1 and 2 = 3 or 4 = 4", "true", "{}" },
        { "1 = 1 or 0 div 0 = 1 and false()", "true", "{}" },
        { "${n} * 2", "10", "{}" },
        { "${n}*2", "10", "{}" },             // '*' after an operand multiplies (XPath 1.0 §3.7)
        { "count(${cost} | ${rep})", "6", "{}" },
        { "5 div 0 = 1 div 0", "true", "{}" },

        // ----- Coercion and comparison (XPath 1.0 §3.4, §4.2–4.4), and empty answers as forms use them
        { "string(12)", "12", "{}" },
        { "string(0.5)", "0.5", "{}" },
        { "string(1 div 0)", "Infinity", "{}" },
        { "string(0 div 0)", "NaN", "{}" },
        { "string(-0)", "0", "{}" },
        { "string(1000000000000000000000)", "1000000000000000000000", "{}" },   // never an exponent
        { "string(1 = 1)", "true", "{}" },
        { "${n} = 5", "true", "{}" },
        { "${n} = '5'", "true", "{}" },
        { "${n} = '5.0'", "false", "{}" },    // node against a string compares strings
        { "${n} = 5.0", "true", "{}" },       // node against a number compares numbers
        { "${m} > 2", "true", "{}" },
        { "'10' > '9'", "true", "{}" },       // relational operators always compare numbers
        { "'abc' < 'abd'", "false", "{}" },   // ... so text that is not a number is never less than anything
        { "'abc' = 'abc'", "true", "{}" },
        { "${a} = ''", "true", "{}" },
        { "${a} != ''", "false", "{}" },
        { "${a} = 0", "false", "{}" },        // an empty answer is NaN as a number
        { "${a} > 0", "false", "{}" },
        { "${a} < 0", "false", "{}" },
        { "${a} + 1", "NaN", "{}" },
        { "string(number(${a}))", "NaN", "{}" },
        { "${missing} = ''", "false", "{}" }, // no such node: an empty node-set equals nothing
        { "count(${missing})", "0", "{}" },
        { "${cost} = 2.5", "true", "{}" },    // a node-set compares true if any node does
        { "${cost} = ''", "true", "{}" },
        { "${cost} != 10", "true", "{}" },
        { "${rep} = true()", "true", "{}" },
        { "${a} = false()", "true", "{}" },   // INFERRED: one empty answer is false, see booleanValue
        { "true() = 'false'", "true", "{}" }, // any non-empty string is true
        { "1 = true()", "true", "{}" },
        { "0 div 0 = 0 div 0", "false", "{}" },
        { "0 div 0 != 0 div 0", "true", "{}" },
        { ".", "mine", "{current:'mine'}" },
        { "string-length(.)", "4", "{current:'mine'}" },

        // ----- String
        { "string(${name})", "Ada", "{}" },
        { "string()", "mine", "{current:'mine'}" },
        { "concat(${name}, ' ', 'Lovelace')", "Ada Lovelace", "{}" },
        { "concat('[', ${cost}, ']')", "[102.5]", "{}" },     // ODK: arguments may be node-sets, every node joined
        { "join(', ', 'a', ${cost}, 'b')", "a, 10, , 2.5, b", "{}" },
        { "substr('abcdef', 2)", "cdef", "{}" },              // ODK: 0-based start
        { "substr('abcdef', 1, 3)", "bc", "{}" },             // ... and an exclusive end
        { "substr('abc', 5)", "", "{}" },
        { "substring('12345', 2, 3)", "234", "{}" },                // XPath 1.0 §4.2's examples, all of them:
        { "substring('12345', 2)", "2345", "{}" },
        { "substring('12345', 1.5, 2.6)", "234", "{}" },            // start and length are rounded first
        { "substring('12345', 0, 3)", "12", "{}" },                 // a start of 0 takes one character off the length
        { "substring('12345', 0 div 0, 3)", "", "{}" },             // NaN selects nothing
        { "substring('12345', -42, 1 div 0)", "12345", "{}" },
        { "substring('12345', -1 div 0, 1 div 0)", "", "{}" },
        { "substring(${name}, 2)", "da", "{}" },                    // a node is its string value
        { "substring-before('1999/04/01', '/')", "1999", "{}" },     // XPath 1.0 §4.2's examples
        { "substring-after('1999/04/01', '/')", "04/01", "{}" },
        { "substring-after('1999/04/01', '19')", "99/04/01", "{}" },
        { "substring-before('abc', 'x')", "", "{}" },
        { "translate('bar', 'abc', 'ABC')", "BAr", "{}" },          // XPath 1.0 §4.2's examples
        { "translate('--aaa--', 'abc-', 'ABC')", "AAA", "{}" },
        { "string-length('çağrı')", "5", "{}" },
        { "string-length(${a})", "0", "{}" },
        { "normalize-space('  a \t  b  ')", "a b", "{}" },
        { "contains('abc', 'b')", "true", "{}" },
        { "starts-with('abc', 'ab')", "true", "{}" },
        { "ends-with('abc', 'bc')", "true", "{}" },
        { "ends-with('abc', 'ab')", "false", "{}" },
        { "uuid()", "44444444-4444-4444-9444-444444444444", "{}" },  // a version-4 shape; random() is 0.25 here
        { "uuid(5)", "PPPPP", "{}" },
        { "pulldata('prices', 'price', 'code', 'A1')", "3.5", "{}" },
        { "pulldata('prices', 'price', 'code', 'none')", "", "{}" },

        // ----- pulldata("@property") and property() — ADR-287 section 5.20. What the page knows is the page's: the context
        // here says ada is signed in, online, three hours east, with the device id dev-1; anything it does not hold is empty.
        { "pulldata('@property', 'username')", "ada", "{}" },
        { "pulldata(\"@property\", \"email\")", "ada@example.org", "{}" },
        { "pulldata('@property', 'online')", "true", "{}" },
        { "if(pulldata('@property', 'online'), 'yes', 'no')", "yes", "{}" },
        { "pulldata('@property', 'utcOffset') * 2", "6", "{}" },
        { "pulldata('@property', 'userFullName')", "", "{}" },
        { "concat(pulldata('@property', 'username'), '!')", "ada!", "{}" },
        { "pulldata('@property', 'username')", "", "{property: null}" },
        { "property('username')", "ada", "{}" },
        { "property('uri:email')", "ada@example.org", "{}" },
        { "property('deviceid')", "dev-1", "{}" },
        { "property('uri:deviceid')", "dev-1", "{}" },
        { "property('userFullName')", "", "{}" },                // a Survey123 name works in property() too (INFERRED, section 5.20)
        { "property('online')", "true", "{}" },

        // ----- pulldata("@geopoint") — the point is "latitude longitude altitude accuracy". The documented example's point
        // is used for the degrees, minutes and seconds (39°57'36.3"N, seconds 36.3 and 4).
        { "pulldata('@geopoint', ${loc}, 'y')", "41.0082", "{}" },
        { "pulldata('@geopoint', ${loc}, 'x')", "28.9784", "{}" },
        { "pulldata('@geopoint', ${loc}, 'z')", "0", "{}" },
        { "pulldata('@geopoint', ${loc}, 'horizontalAccuracy')", "5", "{}" },
        { "pulldata('@geopoint', ${loc}, 'accuracy')", "5", "{}" },
        { "pulldata('@geopoint', ${loc}, 'positionSourceType')", "2", "{}" },
        { "pulldata('@geopoint', ${loc}, 'verticalAccuracy')", "", "{}" },
        { "pulldata('@geopoint', ${loc}, 'speed')", "", "{}" },
        { "pulldata('@geopoint', ${loc}, 'positionSourceInfo.deviceName')", "", "{}" },
        { "round(pulldata('@geopoint', ${loc}, 'x') + pulldata('@geopoint', ${loc}, 'y'), 4)", "69.9866", "{}" },
        { "pulldata('@geopoint', '41 29 0 0', 'x')", "29", "{}" },
        { "pulldata('@geopoint', '41 29 0 0', 'z')", "", "{}" },                   // typed: no altitude was recorded
        { "pulldata('@geopoint', '41 29 0 0', 'horizontalAccuracy')", "", "{}" },   // ... and no accuracy
        { "pulldata('@geopoint', '41 29 0 0', 'positionSourceType')", "1", "{}" },  // User
        { "pulldata('@geopoint', '', 'x')", "", "{}" },
        { "pulldata('@geopoint', ${a}, 'y')", "", "{}" },
        { "pulldata('@geopoint', 'north south', 'y')", "", "{}" },
        { "pulldata('@geopoint', '39.96008 116.45111 800.5 12.3', 'DMS.latitudeText')", "39\u00b057'36.3\"N", "{}" },
        { "pulldata('@geopoint', '39.96008 116.45111 800.5 12.3', 'DMS.latitudeMinutes')", "57", "{}" },
        { "pulldata('@geopoint', '39.96008 116.45111 800.5 12.3', 'DMS.latitudeSeconds')", "36.3", "{}" },
        { "pulldata('@geopoint', '39.96008 116.45111 800.5 12.3', 'DMS.longitudeSeconds')", "4", "{}" },
        { "pulldata('@geopoint', '39.96008 116.45111 800.5 12.3', 'DMS.longitudeText')", "116\u00b027'4.0\"E", "{}" },
        { "pulldata('@geopoint', '39.96008 116.45111 800.5 12.3', 'DMS.text')", "39\u00b057'36\"N 116\u00b027'4\"E", "{}" },
        { "pulldata('@geopoint', '-10.5 -20.25 0 3', 'DMS.latitudeHemisphere')", "S", "{}" },
        { "pulldata('@geopoint', '-10.5 -20.25 0 3', 'DMS.longitudeHemisphere')", "W", "{}" },
        { "pulldata('@geopoint', '-10.5 -20.25 0 3', 'DMS.longitudeMinutes')", "15", "{}" },
        { "pulldata('@geopoint', '10.99999 5 0 3', 'DMS.latitudeMinutes')", "0", "{}" },     // 59.9996 minutes carries into the degree
        { "pulldata('@geopoint', '10.99999 5 0 3', 'DMS.latitudeDegrees')", "11", "{}" },
        { "substring(pulldata('@geopoint', ${loc}, 'DMS'), 1, 18)", "{\"latitudeDegrees\"", "{}" },

        // ----- date-time() — ADR-287 section 5.20, INFERRED from "converts a number or string to a date object"
        { "format-date-time(date-time('2017-05-28T04:39:02+10:00'), '%Y-%m-%d %H:%M')", "2017-05-27 21:39", "{}" },   // Survey123's own example; shown at +03:00
        { "format-date-time(date-time('2024-03-01'), '%Y-%m-%d %H:%M')", "2024-03-01 00:00", "{}" },                // a date is the device's midnight
        { "decimal-date-time(date-time(${d}))", "19781.875", "{}" },                                                 // 2024-02-29 00:00 at +03:00
        { "decimal-date-time(date-time(19000.5))", "19000.5", "{}" },                                                // a number is days since 1970 UTC
        { "decimal-date-time(date-time('19000.25'))", "19000.25", "{}" },
        { "string(date-time(0))", "1970-01-01T03:00:00.000+03:00", "{}" },
        { "date-time('')", "", "{}" },
        { "date-time(${a})", "", "{}" },
        { "date-time('not a date')", "", "{}" },
        { "decimal-date-time(date-time(decimal-date-time(${start}))) = decimal-date-time(${start})", "true", "{}" },

        // ----- Boolean
        { "if(${n} > 3, 'big', 'small')", "big", "{}" },
        { "if(${a}, 'answered', 'empty')", "empty", "{}" },
        { "if(${a} = '', 'empty', 'answered')", "empty", "{}" },
        { "if(true(), 1, 1 div 0)", "1", "{}" },
        { "coalesce(${a}, ${name})", "Ada", "{}" },
        { "coalesce(${name}, 'x')", "Ada", "{}" },
        { "coalesce(${a}, ${missing})", "", "{}" },
        { "once(random())", "0.25", "{}" },                 // ODK's own example: a value made once ...
        { "once(random())", "0.7", "{current:'0.7'}" },     // ... and kept once the question has one
        { "true()", "true", "{}" },
        { "false()", "false", "{}" },
        { "boolean('')", "false", "{}" },
        { "boolean('0')", "true", "{}" },                   // XPath 1.0: any non-empty string
        { "boolean(0)", "false", "{}" },
        { "boolean(${rep})", "true", "{}" },
        { "boolean-from-string('true')", "true", "{}" },    // ODK: true for "true" or "1", false for anything else
        { "boolean-from-string('1')", "true", "{}" },
        { "boolean-from-string('yes')", "false", "{}" },
        { "not(${a})", "true", "{}" },
        { "regex('12345', '[0-9]+')", "true", "{}" },       // ODK's example pattern, '[0-9]+' becomes /[0-9]+/
        { "regex('a1b', '[0-9]+')", "true", "{}" },         // ... a search, not a whole-string match
        { "regex('abc', '^[0-9]+$')", "false", "{}" },
        { "checklist(2, 3, 'true', 'false', 1, 0, 'yes')", "true", "{}" },
        { "checklist(-1, 1, ${checked})", "false", "{}" },  // -1: no minimum; two of three are > 0
        { "weighted-checklist(5, -1, 'true', 2, 'false', 10, 1, 3)", "true", "{}" },
        { "weighted-checklist(-1, 4, 'true', 2, 'true', 3)", "false", "{}" },

        // ----- Number
        { "number('12.5')", "12.5", "{}" },
        { "number(' 7 ')", "7", "{}" },
        { "number('1e3')", "NaN", "{}" },                   // XPath 1.0's Number production has no exponent
        { "number('')", "NaN", "{}" },
        { "number(true())", "1", "{}" },
        { "number('1970-01-11')", "10", "{}" },             // ODK: a date is days since 1970-01-01
        { "number('1970-01-02T12:00:00Z')", "1.5", "{}" },  // ... and a dateTime decimal days
        { "int(7.9)", "7", "{}" },
        { "int(-7.9)", "-7", "{}" },                        // ODK: the fractional part is discarded
        { "sum(${cost})", "12.5", "{}" },                   // ODK: an empty node counts as 0, not NaN
        { "sum(${rep})", "0", "{}" },
        { "sum(${cost}) div count(${rep})", "4.166666666666667", "{}" },
        { "max(1, 7, 3)", "7", "{}" },
        { "max(${checked})", "5", "{}" },
        { "min(4, 2.5, 9)", "2.5", "{}" },
        { "round(2.5)", "3", "{}" },                        // XPath 1.0: halves go up ...
        { "round(-2.5)", "-2", "{}" },                      // ... towards positive infinity
        { "round(3.14159, 2)", "3.14", "{}" },              // ODK: a second argument gives decimals
        { "round(1.005, 2)", "1.01", "{}" },
        { "round(1234.5, -2)", "1200", "{}" },
        { "floor(1.5)", "1", "{}" },                        // XPath 1.0 §4.4's examples: floor and ceiling round towards
        { "floor(-1.5)", "-2", "{}" },                      // minus and plus infinity respectively
        { "ceiling(1.5)", "2", "{}" },
        { "ceiling(-1.5)", "-1", "{}" },
        { "floor(${n} div 2)", "2", "{}" },                 // 5 div 2 is 2.5, and the floor of it is 2
        { "ceiling(${a})", "NaN", "{}" },                   // an empty answer is NaN as a number, and NaN stays NaN
        { "pow(2, 10)", "1024", "{}" },
        { "log(1)", "0", "{}" },
        { "log10(1000)", "3", "{}" },
        { "abs(-4)", "4", "{}" },
        { "sin(0)", "0", "{}" },
        { "cos(0)", "1", "{}" },
        { "tan(0)", "0", "{}" },
        { "asin(1) * 2 = pi()", "true", "{}" },
        { "acos(1)", "0", "{}" },
        { "atan(0)", "0", "{}" },
        { "atan2(0, 1)", "0", "{}" },
        { "sqrt(16)", "4", "{}" },
        { "exp(0)", "1", "{}" },
        { "exp10(2)", "100", "{}" },
        { "pi()", "3.141592653589793", "{}" },
        { "random()", "0.25", "{}" },

        // ----- Node-set and choice lists
        { "count(${rep})", "3", "{}" },
        { "count-non-empty(${cost})", "2", "{}" },
        { "position(..)", "2", "{}" },                      // the repeat instance the expression runs in
        { "position()", "2", "{}" },
        { "count(${rep}[last()])", "1", "{}" },              // XPath 1.0 §4.1: last() is the size of the node-set filtered
        { "count(${rep}[position() = last()])", "1", "{}" },
        { "join(',', ${cost}[position() < last()])", "10,", "{}" },   // the first two of three answers, one of them empty
        { "join(',', ${cost}[last()])", "2.5", "{}" },
        { "last()", "1", "{}" },                            // INFERRED: outside a predicate a form's context size is 1
        { "count(current())", "1", "{}" },
        { "join(' ', instance('food')/root/item[cat = ${cat}]/name)", "apple pear", "{}" },   // a choice_filter
        { "join(' ', instance('food')/root/item[cat = current()/../cat]/name)", "apple pear", "{}" },
        { "count(instance('food')/root/item)", "3", "{}" },
        { "instance('food')/root/item[name = 'leek']/label", "Leek", "{}" },
        { "instance('food')/root/item[2]/name", "leek", "{}" },
        { "join(' ', instance('food')/root/item[position() > 1]/name)", "leek pear", "{}" },
        { "count(instance('food')//item)", "3", "{}" },
        { "join(' ', randomize(instance('food')/root/item/name))", "pear apple leek", "{}" },

        // ----- Repeats
        { "indexed-repeat(${cost}, ${rep}, 3)", "2.5", "{}" },
        { "indexed-repeat(${room}, ${building}, 2, ${floor}, 3)", "B", "{}" },   // ODK: nested repeats, outermost first
        { "indexed-repeat(${cost}, ${rep}, 9)", "", "{}" },

        // ----- Dates and times (now is 2024-02-29T22:15:30.250Z, the device at +03:00)
        { "today()", "2024-03-01", "{}" },
        { "now()", "2024-03-01T01:15:30.250+03:00", "{}" },  // ODK: with the offset, not normalised to UTC
        { "number(${d})", "19782", "{}" },
        { "${d} + 1", "19783", "{}" },
        { "today() - date('2024-02-28')", "2", "{}" },
        { "date('2024-03-01') - date('2023-03-01')", "366", "{}" },
        { "date(${d} + 1)", "2024-03-01", "{}" },
        { "date(${d} - 365)", "2023-03-01", "{}" },
        { "${d} < today()", "true", "{}" },
        { "${d} = date('2024-02-29')", "true", "{}" },
        { "${d} = '2024-02-29'", "true", "{}" },
        { "today() > '2024-01-01'", "true", "{}" },
        { "round((decimal-date-time(${end}) - decimal-date-time(${start})) * 24, 2)", "2.5", "{}" },
        { "round((${end} - ${start}) * 1440)", "150", "{}" },
        { "decimal-date-time('1970-01-02T12:00:00Z')", "1.5", "{}" },
        { "decimal-date-time('1970-01-01T03:00:00+03:00')", "0", "{}" },
        { "decimal-time('12:00:00.000+03:00')", "0.5", "{}" },     // ODK's example: noon is 0.5 ...
        { "decimal-time('18:00:00.000+03:00')", "0.75", "{}" },    // ... and 6 pm is 0.75
        { "decimal-time('09:00:00.000Z')", "0.5", "{}" },          // ... in the device's time zone
        { "date('2024-02-29T23:30:00Z')", "2024-03-01", "{}" },
        { "date(0)", "1970-01-01", "{}" },
        { "date('2023-02-29')", "", "{}" },
        { "date(${a})", "", "{}" },
        { "format-date('2024-03-05', '%Y|%y|%m|%n|%b|%d|%e|%a')", "2024|24|03|3|Mar|05|5|Tue", "{}" },
        { "format-date(${d}, '%e %b %Y')", "29 Feb 2024", "{}" },
        { "format-date(today(), '%d/%m/%Y')", "01/03/2024", "{}" },
        { "format-date(19723, '%Y-%m-%d')", "2024-01-01", "{}" },
        { "format-date(${a}, '%Y')", "", "{}" },
        { "format-date('nonsense', '%Y')", "", "{}" },
        { "format-date-time('2024-03-05T14:07:09.123+03:00', '%Y-%m-%d %H:%M:%S.%3')", "2024-03-05 14:07:09.123", "{}" },
        { "format-date-time('2024-03-05T06:07:09Z', '%h:%M')", "9:07", "{}" },
        { "format-date-time('2024-03-05T00:00:00+03:00', '%a %h')", "Tue 0", "{}" },
        { "format-date-time(now(), '%H:%M')", "01:15", "{}" },

        // ----- Select. Multi-select answers are comma-separated in this product (§5.10); XForms uses spaces.
        { "selected(${fruits}, 'pear')", "true", "{}" },
        { "selected(${fruitsx}, 'pear')", "true", "{}" },
        { "selected(${mixed}, 'leek')", "true", "{}" },
        { "selected(${fruits}, 'pea')", "false", "{}" },
        { "selected(${a}, 'x')", "false", "{}" },
        { "count-selected(${fruits})", "2", "{}" },
        { "count-selected(${mixed})", "3", "{}" },
        { "count-selected(${a})", "0", "{}" },
        { "selected-at(${fruits}, 0)", "apple", "{}" },     // ODK: a 0-based index ...
        { "selected-at(${fruitsx}, 1)", "pear", "{}" },
        { "selected-at(${fruits}, 2)", "", "{}" },          // ... and empty past the end
        { "selected-at(${fruits}, -1)", "", "{}" },
        { "jr:choice-name('pear', '${fruits}')", "Pear", "{}" },
        { "jr:choice-name(selected-at(${fruits}, 0), '${fruits}')", "Apple", "{}" },
        { "jr:choice-name('pear', '${fruits}')", "Armut", "{language:'Türkçe'}" },
        { "jr:choice-name('nope', '${fruits}')", "", "{}" },
        // A choices column named with its language is one name in a choice_filter, as the corpus's Survey123 forms write it.
        { "join(' ', instance('food')/root/item[label::Türkçe = 'Elma']/name)", "apple", "{}" },
        { "join(' ', instance('food')/root/item[label::Türkçe != 'Elma']/name)", "leek pear", "{}" },

        // ----- Geographic: the booleans here, the measures in Measures
        { "geofence('0.5 0.5', '0 0 0 0;0 1 0 0;1 1 0 0;1 0 0 0;0 0 0 0')", "true", "{}" },
        { "geofence('2 2', '0 0 0 0;0 1 0 0;1 1 0 0;1 0 0 0;0 0 0 0')", "false", "{}" },
        { "intersects('0 0;1 1;0 1;1 0')", "true", "{}" },              // a bow tie
        { "intersects('0 0;0 1;1 1;1 0;0 0')", "false", "{}" },         // a closed square: its ends touch, legitimately
        { "intersects('0 0;0 1;1 1')", "false", "{}" },
        { "distance(${loc}, '41.0082 28.9784')", "0", "{}" },
    };

    /// <summary>Expression, expected number, tolerance.</summary>
    public static TheoryData<string, double, double> Measures
    {
        get
        {
            // <b>The expected figures are closed forms, not the evaluator's output read back.</b> On a sphere of
            // radius R, one degree of a meridian or the equator is R·π/180, and a box between two parallels and two
            // meridians has area R²·Δλ·(sin φ₂ − sin φ₁), which the Chamberlain–Duquette sum gives exactly. R is the
            // equatorial circumference the specification names, 40,075,000 m, over 2π.
            double r = 40_075_000 / (2 * Math.PI);
            double degree = r * Math.PI / 180;
            double box = r * r * (Math.PI / 180) * Math.Sin(Math.PI / 180);
            double phi = Math.PI / 180;
            double alongOneNorth = r * Math.Acos((Math.Sin(phi) * Math.Sin(phi)) + (Math.Cos(phi) * Math.Cos(phi) * Math.Cos(phi)));

            return new TheoryData<string, double, double>
            {
                { "distance('0 0', '0 1')", degree, 0.001 },
                { "distance('0 0 0 0;0 1 0 0;1 1 0 0')", 2 * degree, 0.001 },
                { "distance('0 0', '0 1', '1 1')", 2 * degree, 0.001 },          // ODK: several geopoint arguments
                { "distance(${corners})", (2 * degree) + alongOneNorth, 0.001 },  // the last leg is the great circle between 1°N points
                { "area('0 0 0 0;0 1 0 0;1 1 0 0;1 0 0 0;0 0 0 0')", box, 0.01 },
                { "area('0 0;0 1;1 1;1 0')", box, 0.01 },                         // an unclosed ring is closed
                { "area(${corners})", box, 0.01 },                                // ODK: a node-set of geopoints
                { "area('0 0;0 1')", 0, 0 },
            };
        }
    }

    /// <summary>Expression, the start of the message, the 0-based character it points at.</summary>
    public static TheoryData<string, string, int> ParseErrors => new()
    {
        { "1 +", "Expected a value but found the end of the expression", 3 },
        { "foo(1)", "Unknown function 'foo'", 0 },
        { "string-length()", "string-length() takes 1 argument, not 0", 0 },   // ODK: the argument is required
        { "lang('en')", "lang() is not supported: lang() reads the xml:lang", 0 },
        { "name()", "name() is not supported: node names are not in the first version", 0 },
        { "1 + digest('a')", "digest() is not supported: digests are not in the first version", 4 },
        { "concat('a'", "Expected ')' but found the end of the expression", 10 },
        { "${a", "A reference has no closing '}'", 0 },
        { "${a b}", "'${a b}' is not a question name", 0 },
        { "1 2", "Unexpected '2'", 2 },
        { "'abc", "A string is never closed", 0 },
        { "1 # 2", "Unexpected '#'", 2 },
        { "pulldata('@layer', 'a', 'b', 'c')", "'a' is not an operation of pulldata(\"@layer\")", 0 },
        { "pulldata('@layer', 'getValue', 'attributes.A')", "pulldata(\"@layer\", \"getValue\") takes 2 to 3 arguments after the operation, not 1", 0 },
        { "pulldata('@json', 'a')", "pulldata(\"@json\") is not supported: reading a JSON answer", 0 },
        { "pulldata('@javascript', 'f.js', 'run')", "pulldata(\"@javascript\") is not supported: running a script", 0 },
        { "pulldata('@exif', ${a}, 'x')", "pulldata(\"@exif\") is not supported: reading the metadata of a photo", 0 },
        { "pulldata('@mystery', 'a')", "pulldata(\"@mystery\") is not supported: it is not a source Survey123 documents", 0 },
        { "pulldata('prices', 'price')", "pulldata() takes 4 arguments, not 2", 0 },
        { "pulldata('@property')", "pulldata() takes 2 to 6 arguments, not 1", 0 },
        { "pulldata('@property', 'username', 'x')", "pulldata(\"@property\") takes the name of one property", 0 },
        { "pulldata('@property', 'token')", "pulldata(\"@property\") does not give 'token': an access token is a credential", 0 },
        { "pulldata('@property', 'deviceModel')", "pulldata(\"@property\") does not give 'deviceModel'", 0 },
        { "pulldata('@property', 'shoeSize')", "'shoeSize' is not a property Survey123 documents", 0 },
        { "1 + pulldata('@geopoint', ${loc}, 'UTM.easting')", "pulldata(\"@geopoint\") does not give 'UTM.easting'", 4 },
        { "pulldata('@geopoint', ${loc}, 'bogus')", "'bogus' is not a part of a geopoint Survey123 documents", 0 },
        { "pulldata('@geopoint', ${loc})", "pulldata(\"@geopoint\") takes a geopoint question and the name of a part", 0 },
        { "property('phonenumber')", "property('phonenumber') is not supported: a browser cannot read the device's phone number", 0 },
        { "property('uri:simserial')", "property('uri:simserial') is not supported", 0 },
        { "property('shoe')", "'shoe' is not a property ODK or Survey123 documents", 0 },
        { "property('token')", "property('token') is not supported: an access token is a credential", 0 },
        { "property()", "property() takes 1 argument, not 0", 0 },
        { "round()", "round() takes 1 to 2 arguments, not 0", 0 },
        { "if(1, 2)", "if() takes 3 arguments, not 2", 0 },
        { "weighted-checklist(1, 2, 'x')", "weighted-checklist() takes min, max and pairs", 0 },
        { "instance('x')/root/@id", "Attributes are not supported", 19 },
        { "ancestor::x", "The ancestor axis is not supported", 0 },
        { "(1 + 2", "Expected ')'", 6 },
    };

    /// <summary>Each case gives its expected value.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task An_expression_evaluates_as_the_specification_says(string expression, string expected, string extra)
    {
        string got = await _page.Browser.EvaluateAsync<string>($"__run({Js(expression)}, {extra})") ?? "(null)";
        Assert.True(expected == got, $"{expression} → '{got}', expected '{expected}'");
    }

    /// <summary>Areas and distances, within a tolerance.</summary>
    [Theory]
    [MemberData(nameof(Measures))]
    public async Task A_measure_is_on_a_sphere_the_size_of_the_equator(string expression, double expected, double tolerance)
    {
        double got = await _page.Browser.EvaluateAsync<double>($"__num({Js(expression)})");
        Assert.True(Math.Abs(got - expected) <= tolerance, $"{expression} → {got.ToString(CultureInfo.InvariantCulture)}, expected {expected.ToString(CultureInfo.InvariantCulture)} ± {tolerance.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>A malformed expression is refused with what is wrong and where.</summary>
    [Theory]
    [MemberData(nameof(ParseErrors))]
    public async Task A_malformed_expression_names_the_character(string expression, string message, int index)
    {
        string got = await _page.Browser.EvaluateAsync<string>($"__err(function () {{ GraticulaXlsform.parse({Js(expression)}); }})") ?? string.Empty;
        string[] parts = got.Split('|');

        Assert.True(parts[0].StartsWith(message, StringComparison.Ordinal), $"{expression} → '{got}', expected it to start '{message}'");
        Assert.EndsWith($" at character {index + 1}", parts[0], StringComparison.Ordinal);
        Assert.Equal(index.ToString(CultureInfo.InvariantCulture), parts[1]);
    }

    /// <summary>What can only fail when the expression runs.</summary>
    [Fact]
    public async Task A_list_that_is_not_a_choice_list_and_a_count_of_text_are_refused_when_run()
    {
        string notAList = await _page.Browser.EvaluateAsync<string>("__err(function () { __run(\"count(instance('nope')/root/item)\"); })") ?? string.Empty;
        Assert.Contains("instance('nope') is not a choice list of this form", notAList, StringComparison.Ordinal);

        string text = await _page.Browser.EvaluateAsync<string>("__err(function () { __run(\"count('x')\"); })") ?? string.Empty;
        Assert.Contains("Expected a node-set", text, StringComparison.Ordinal);

        string pattern = await _page.Browser.EvaluateAsync<string>("__err(function () { __run(\"regex('a', '(')\"); })") ?? string.Empty;
        Assert.Contains("is not a regular expression", pattern, StringComparison.Ordinal);
    }

    /// <summary>The specification's seeded shuffle: the same seed, the same order; every item kept.</summary>
    [Fact]
    public async Task Randomize_with_a_seed_is_reproducible_and_a_permutation()
    {
        const string Names = "instance('nums')/root/item/name";

        string first = await _page.Browser.EvaluateAsync<string>($"__run({Js($"join(' ', randomize({Names}, 42))")})") ?? string.Empty;
        string again = await _page.Browser.EvaluateAsync<string>($"__run({Js($"join(' ', randomize({Names}, 42.9))")})") ?? string.Empty;
        string other = await _page.Browser.EvaluateAsync<string>($"__run({Js($"join(' ', randomize({Names}, 7))")})") ?? string.Empty;
        string text = await _page.Browser.EvaluateAsync<string>($"__run({Js($"join(' ', randomize({Names}, 'a seed'))")})") ?? string.Empty;
        string textAgain = await _page.Browser.EvaluateAsync<string>($"__run({Js($"join(' ', randomize({Names}, 'a seed'))")})") ?? string.Empty;

        string[] sorted = Enumerable.Range(1, 8).Select(i => "n" + i.ToString(CultureInfo.InvariantCulture)).ToArray();

        Assert.Equal(first, again);                              // the specification: a decimal seed is truncated
        Assert.Equal(text, textAgain);                           // a text seed is hashed, reproducibly
        Assert.NotEqual(first, other);
        Assert.NotEqual(string.Join(' ', sorted), first);
        Assert.Equal(sorted, first.Split(' ').Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(sorted, text.Split(' ').Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>The references an expression reads, for the client's recalculation order.</summary>
    [Fact]
    public async Task References_lists_each_question_once()
    {
        string got = await _page.Browser.EvaluateAsync<string>(
            "GraticulaXlsform.references(\"${a} + ${b} * count(instance('food')/root/item[cat = ${a}])\").join(',')") ?? string.Empty;

        Assert.Equal("a,b", got);
    }

    /// <summary>
    /// <b>FormCatalog.Functions is the one list</b> (ADR-287 §5.10): the server's checker accepts by it and the client
    /// must evaluate exactly it. Read from the C# source as text, because this project has no reference to the host and
    /// should not (see the project file).
    /// </summary>
    [Fact]
    public async Task The_evaluator_implements_exactly_FormCatalog_Functions()
    {
        string[] catalog = CatalogFunctions();
        string[] evaluator = await _page.Browser.EvaluateAsync<string[]>("GraticulaXlsform.functions.slice()") ?? [];

        string[] missing = catalog.Except(evaluator, StringComparer.Ordinal).ToArray();
        string[] extra = evaluator.Except(catalog, StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0, "In FormCatalog.Functions but not in the evaluator: " + string.Join(", ", missing));
        Assert.True(extra.Length == 0, "In the evaluator but not in FormCatalog.Functions: " + string.Join(", ", extra));
        Assert.True(catalog.Length > 60, $"Only {catalog.Length} functions were read from FormCatalog.cs; the parse is wrong.");
    }

    /// <summary>
    /// The refused names are the same in both places too: FormCatalog.RefusedFunctions is what the server refuses at upload,
    /// and the evaluator must refuse the same names, so that a form the server lets through never reaches a function the
    /// client does not have a reason for.
    /// </summary>
    [Fact]
    public async Task The_evaluator_refuses_exactly_FormCatalog_RefusedFunctions()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Graticula.Host", "Forms", "FormCatalog.cs"));
        Match block = Regex.Match(source, @"RefusedFunctions\s*=\s*new Dictionary<string, string>\(StringComparer\.Ordinal\)\s*\{(?<body>.*?)\}\.ToFrozenDictionary", RegexOptions.Singleline);
        Assert.True(block.Success, "FormCatalog.cs has no 'RefusedFunctions' dictionary; this test reads it by that shape.");

        string[] catalog = Regex.Matches(block.Groups["body"].Value, @"\[""([^""]+)""\]\s*=").Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] evaluator = await _page.Browser.EvaluateAsync<string[]>("GraticulaXlsform.refused.slice()") ?? [];

        Assert.True(catalog.Length >= 8, $"Only {catalog.Length} refused functions were read from FormCatalog.cs; the parse is wrong.");
        Assert.Equal(catalog, evaluator.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// ADR-287 section 5.20's lists are the same in the checker and the evaluator, in both directions: a form the server
    /// lets through never reaches a name the client does not know, and the client does not accept what the server refuses.
    /// </summary>
    [Fact]
    public async Task The_evaluators_property_geopoint_and_mode_lists_are_exactly_FormCatalogs()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Graticula.Host", "Forms", "FormCatalog.cs"));

        string[] Set(string name)
        {
            Match block = Regex.Match(source, name + @"\s*=\s*Set\((?<body>.*?)\);", RegexOptions.Singleline);
            Assert.True(block.Success, $"FormCatalog.cs has no '{name} = Set(...)'; this test reads it by that shape.");
            return Regex.Matches(Regex.Replace(block.Groups["body"].Value, @"//[^\n]*", string.Empty), "\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        string[] Keys(string name)
        {
            Match block = Regex.Match(source, name + @"\s*=\s*new Dictionary<string, string>\(StringComparer\.Ordinal\)\s*\{(?<body>.*?)\}\.ToFrozenDictionary", RegexOptions.Singleline);
            Assert.True(block.Success, $"FormCatalog.cs has no '{name}' dictionary; this test reads it by that shape.");
            return Regex.Matches(block.Groups["body"].Value, @"\[""([^""]+)""\]\s*=").Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        async Task<string[]> Evaluator(string key) =>
            (await _page.Browser.EvaluateAsync<string[]>($"GraticulaXlsform.catalog.{key}.slice()") ?? []).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(Set("Properties"), await Evaluator("properties"));
        Assert.Equal(Keys("RefusedProperties"), await Evaluator("refusedProperties"));
        Assert.Equal(Keys("OdkProperties"), await Evaluator("odkProperties"));
        Assert.Equal(Keys("RefusedOdkProperties"), await Evaluator("refusedOdkProperties"));
        Assert.Equal(Set("GeopointParts"), await Evaluator("geopointParts"));
        Assert.Equal(Set("GeopointPartsAnsweredEmpty"), await Evaluator("geopointPartsAnsweredEmpty"));
        Assert.Equal(Keys("RefusedGeopointParts"), await Evaluator("refusedGeopointParts"));
        Assert.Equal(Keys("RefusedPulldataSources"), await Evaluator("refusedPulldataSources"));
        Assert.Equal(Set("CalculationModes"), await Evaluator("calculationModes"));
    }

    /// <summary>Every function on the list is called by at least one case above (condition 4: tested, not just present).</summary>
    [Fact]
    public void Every_catalog_function_is_exercised_by_a_case()
    {
        IEnumerable<string> expressions = Cases.Select(row => (string)row[0])
            .Concat(Measures.Select(row => (string)row[0]))
            .Append("randomize(")    // the seeded shuffle has its own test
            .Append("references");

        string all = string.Join("\n", expressions);

        string[] untested = CatalogFunctions()
            .Where(name => !Regex.IsMatch(all, "(?<![A-Za-z0-9:_-])" + Regex.Escape(name) + @"\s*\("))
            .ToArray();

        Assert.True(untested.Length == 0, "No case calls: " + string.Join(", ", untested));
    }

    private static string[] CatalogFunctions()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Graticula.Host", "Forms", "FormCatalog.cs"));
        Match block = Regex.Match(source, @"FrozenSet<string>\s+Functions\s*=\s*Set\((?<body>.*?)\);", RegexOptions.Singleline);

        Assert.True(block.Success, "FormCatalog.cs has no 'Functions = Set(...)'; this test reads it by that shape.");

        string body = Regex.Replace(block.Groups["body"].Value, @"//[^\n]*", string.Empty);

        return Regex.Matches(body, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (the folder with CLAUDE.md) was not found.");
    }

    private static string Js(string text) => JsonSerializer.Serialize(text);

    /// <summary>One Chrome tab with the evaluator loaded, shared by the class: the tests only read from it.</summary>
    public sealed class Page : IAsyncLifetime
    {
        private DevTools? _browser;

        /// <summary>The tab.</summary>
        public DevTools Browser => _browser ?? throw new InvalidOperationException("The browser is not open yet.");

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            _browser = await DevTools.LaunchAsync();

            string script = await File.ReadAllTextAsync(
                Path.Combine(RepositoryRoot(), "src", "Graticula.Host", "wwwroot", "forms", "xlsform-eval.js"));

            await _browser.EvaluateAsync<bool>(script + "\n;true");

            Assert.True(await _browser.EvaluateAsync<bool>("typeof GraticulaXlsform === 'object'"), "The script did not define GraticulaXlsform.");
            Assert.True(await _browser.EvaluateAsync<bool>(Harness), "The test harness did not load.");
        }

        /// <inheritdoc/>
        public async Task DisposeAsync()
        {
            if (_browser is not null)
            {
                await _browser.DisposeAsync();
            }
        }
    }
}
