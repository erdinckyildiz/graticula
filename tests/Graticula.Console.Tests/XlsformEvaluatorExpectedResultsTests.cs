using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-287 condition 4, the half the first tests left open: the evaluator's <b>answers</b>, not only its errors, against
/// values somebody else wrote down. Two sources, kept apart below.
/// </summary>
/// <remarks>
/// <para>
/// <b>ODK's own vectors.</b> JavaRosa is ODK's reference implementation of the XForms specification, and its test suite
/// (github.com/getodk/javarosa, Apache-2.0, read 2026-11-17: <c>XPathEvalTest</c>, <c>RandomizeTypesTest</c>,
/// <c>GeoAreaTest</c>, <c>GeoDistanceTest</c>) states, for each expression, the value it must give. The expressions and
/// their values are copied here as data, each as JavaRosa writes it; no JavaRosa code is. Where JavaRosa's test sets
/// the time zone to UTC, so does the case (<c>Utc</c>). Four of its vectors are not here, each for a stated reason:
/// <c>decimal-time('2000-01-01T10:20:30.000+03:00')</c>, <c>6.1 - 7.8</c> and <c>1.1 * -1.1</c> are compared by JavaRosa
/// within a tolerance and are in <see cref="Measures"/>; and <c>checklist(1, 3, 'foo', 'bar')</c> is true for JavaRosa,
/// which counts any non-empty text, while the specification counts what "converts to a number &gt; 0" and ODK's
/// documentation counts "exactly the string 'yes'" — three readings, and this evaluator keeps the specification's
/// (ADR-287 §5.27, Q-239).
/// </para>
/// <para>
/// <b>The corpus's own calculations.</b> The 110 public workbooks in <c>D:\temp\forms-corpus</c> are not in the
/// repository (their licences are their authors'), so each expression is copied here with the workbook and survey row it
/// comes from, and its value for the given answers is worked out by hand from the specification, the arithmetic written
/// beside it — never read back from the evaluator.
/// </para>
/// <para>
/// <b>What these found</b> (fixed in <c>xlsform-eval.js</c> the same day): a text seed of <c>randomize()</c> gave
/// another order than ODK's, because the 64-bit integer it hashes to was kept exact rather than as the number it is; an
/// empty seed was hashed rather than taken as 0; <c>area()</c> summed edges along parallels rather than the great circles
/// <c>distance()</c> measures, 1.4 m² off ODK's 151,452 m²; <c>substr()</c> clamped a negative index to 0; and
/// <c>format-date()</c> left <c>%H</c> as written.
/// </para>
/// </remarks>
public sealed class XlsformEvaluatorExpectedResultsTests : IClassFixture<XlsformEvaluatorTests.Page>
{
    private const string None = "{}";

    private const string Utc = "{timezoneOffset: 0}";

    // JavaRosa's RandomizeTypesTest list: eight choices a..h, labelled A..H.
    private const string C8 = "{timezoneOffset: 0, value: function (n) { return { empty: '', some: 'somestring', zero: '0' }[n]; }, "
        + "choices: function (l) { return l === 'c8' ? 'abcdefgh'.split('').map(function (n) { return { name: n, label: n.toUpperCase() }; }) : null; }}";

    private readonly XlsformEvaluatorTests.Page _page;

    /// <summary>Creates the tests over the shared page.</summary>
    /// <param name="page">The tab the evaluator is loaded in.</param>
    public XlsformEvaluatorExpectedResultsTests(XlsformEvaluatorTests.Page page) => _page = page;

    private static string Js(string text) => JsonSerializer.Serialize(text);

    /// <summary>Expression, JavaRosa's expected value, the context (a JavaScript object literal).</summary>
    public static TheoryData<string, string, string> OdkVectors => new()
    {
        { "boolean(-1)", "true", Utc },
        { "boolean(0.0001)", "true", Utc },
        { "boolean(-0)", "false", Utc },
        { "boolean(number('NaN'))", "false", Utc },
        { "boolean(1 div 0)", "true", Utc },
        { "boolean('  ')", "true", Utc },
        { "boolean('false')", "true", Utc },
        { "boolean(date('2000-01-01'))", "true", Utc },
        { "number('100.001')", "100.001", Utc },
        { "number('.1001')", "0.1001", Utc },
        { "number('0')", "0", Utc },
        { "number(' -12345.6789  ')", "-12345.6789", Utc },
        { "number('NaN')", "NaN", Utc },
        { "number('- 17')", "NaN", Utc },
        { "number('  ')", "NaN", Utc },
        { "number('Infinity')", "NaN", Utc },
        { "number('1.1e6')", "NaN", Utc },
        { "number('34.56.7')", "NaN", Utc },
        { "number(-123.5)", "-123.5", Utc },
        { "number(-1 div 0)", "-Infinity", Utc },
        { "number(date('1970-01-02'))", "1", Utc },
        { "number(date('1969-12-31'))", "-1", Utc },
        { "number(date('2008-09-05'))", "14127", Utc },
        { "number(date('1941-12-07'))", "-10252", Utc },
        { "string(false())", "false", Utc },
        { "string(-1 div 0)", "-Infinity", Utc },
        { "string(123456.0000)", "123456", Utc },
        { "string(-123456)", "-123456", Utc },
        { "string(.557586)", "0.557586", Utc },
        { "string('  ')", "  ", Utc },
        { "string(date('1989-11-09'))", "1989-11-09", Utc },
        { "int('100.001')", "100", Utc },
        { "int('.1001')", "0", Utc },
        { "int(' -12345.6789  ')", "-12345", Utc },
        { "int('NaN')", "NaN", Utc },
        { "int('')", "NaN", Utc },
        { "int('1.1e6')", "NaN", Utc },
        { "substr('hello',0)", "hello", Utc },
        { "substr('hello',0,5)", "hello", Utc },
        { "substr('hello',1)", "ello", Utc },
        { "substr('hello',1,5)", "ello", Utc },
        { "substr('hello',1,4)", "ell", Utc },
        { "substr('hello',-2)", "lo", Utc },
        { "substr('hello',0,-1)", "hell", Utc },
        { "substring-before('hello','l')", "he", Utc },
        { "substring-before('hello','')", "", Utc },
        { "substring-after('hello','l')", "lo", Utc },
        { "substring-after('hello','')", "hello", Utc },
        { "substring-after('','q')", "", Utc },
        { "translate('hello','l','L')", "heLLo", Utc },
        { "translate('hello','','L')", "hello", Utc },
        { "translate('hello','l','')", "heo", Utc },
        { "translate('hello','lo','LO')", "heLLO", Utc },
        { "translate('hello world','hello','')", " wrd", Utc },
        { "translate('hello wor,ld',' ,',', ')", "hello,wor ld", Utc },
        { "translate('2019/01/02','/','-')", "2019-01-02", Utc },
        { "contains('abc', 'bcd')", "false", Utc },
        { "starts-with('', 'a')", "false", Utc },
        { "starts-with('', '')", "true", Utc },
        { "ends-with('', '')", "true", Utc },
        { "normalize-space('')", "", Utc },
        { "normalize-space(' \t\n ')", "", Utc },
        { "normalize-space(' a\nb\n')", "a b", Utc },
        { "normalize-space(' \ta\n\t  b \n\t c   \n')", "a b c", Utc },
        { "string-length('cocotero')", "8", Utc },
        { "date(6.5)", "1970-01-07", Utc },
        { "date(-1)", "1969-12-31", Utc },
        { "date(14127)", "2008-09-05", Utc },
        { "date(-10252)", "1941-12-07", Utc },
        { "date(date('1989-11-09'))", "1989-11-09", Utc },
        { "date('1996-02-29')", "1996-02-29", Utc },
        { "format-date('2018-01-02T10:20:30.123', \"%Y-%m-%e %H:%M:%S\")", "2018-01-2 10:20:30", Utc },
        { "decimal-date-time('2000-01-01T10:20:30.000')", "10957.430902777778", Utc },
        { "not('')", "true", Utc },
        { "boolean-from-string('false')", "false", Utc },
        { "boolean-from-string('0')", "false", Utc },
        { "boolean-from-string(1)", "true", Utc },
        { "boolean-from-string(1.0)", "true", Utc },
        { "boolean-from-string(1.0001)", "false", Utc },
        { "boolean-from-string(true())", "true", Utc },
        { "if(true(), 5, 'abc')", "5", Utc },
        { "if(false(), 5, 'abc')", "abc", Utc },
        { "if(6 > 7, 5, 'abc')", "abc", Utc },
        { "if('', 5, 'abc')", "abc", Utc },
        { "selected('apple baby crimson', 'apple')", "true", Utc },
        { "selected('apple baby crimson', 'crimson')", "true", Utc },
        { "selected('apple baby crimson', '  baby  ')", "true", Utc },
        { "selected('apple baby crimson', 'babby')", "false", Utc },
        { "selected('apple baby crimson', 'bab')", "false", Utc },
        { "selected('apple', 'apple')", "true", Utc },
        { "selected('', 'apple')", "false", Utc },
        { "count-selected('apple baby crimson')", "3", Utc },
        { "count-selected('')", "0", Utc },
        { "selected-at('apple baby crimson', 2)", "crimson", Utc },
        { "selected-at('apple baby', 2)", "", Utc },
        { "checklist(3, -1, 'foo', 'bar')", "false", Utc },
        { "checklist(3, 5, 'foo', 'bar')", "false", Utc },
        { "checklist(1, 2, 'foo', 'bar', 'baz')", "false", Utc },
        { "5.5 + 5.5", "11", Utc },
        { "1 - (2 - 3)", "2", Utc },
        { "-(8*5)", "-40", Utc },
        { "-'19'", "-19", Utc },
        { "-10 div -4", "2.5", Utc },
        { "2 * 3 div 8 * 2", "1.5", Utc },
        { "3.1 mod 3.1", "0", Utc },
        { "5 mod 3.1", "1.9", Utc },
        { "2 mod 3.1", "2", Utc },
        { "5 mod -3", "2", Utc },
        { "-5 mod 3", "-2", Utc },
        { "-5 mod -3", "-2", Utc },
        { "5 mod 0", "NaN", Utc },
        { "'123' * '456'", "56088", Utc },
        { "abs(-3.5)", "3.5", Utc },
        { "round('14.29123456789')", "14", Utc },
        { "round('14.6')", "15", Utc },
        { "round('14.29123456789', 0)", "14", Utc },
        { "round('14.29123456789', 1)", "14.3", Utc },
        { "round('14.29123456789', 1.5)", "14.3", Utc },
        { "round('14.29123456789', 2)", "14.29", Utc },
        { "round('14.29123456789', 3)", "14.291", Utc },
        { "round('14.29123456789', 4)", "14.2912", Utc },
        { "round('12345.14', 1)", "12345.1", Utc },
        { "round('-12345.14', 1)", "-12345.1", Utc },
        { "round('12345.12345', 0)", "12345", Utc },
        { "round('12345.12345', -1)", "12350", Utc },
        { "round('12345.12345', -2)", "12300", Utc },
        { "round('12350.12345', -2)", "12400", Utc },
        { "round('12345.12345', -3)", "12000", Utc },
        { "round('1 div 0', 0)", "NaN", Utc },
        { "round('14.5')", "15", Utc },
        { "round('NaN')", "NaN", Utc },
        { "round('0')", "0", Utc },
        { "round('-0.5')", "0", Utc },
        { "round('12345.15', 1)", "12345.2", Utc },
        { "round('-12345.15', 1)", "-12345.1", Utc },
        { "pow(2, 0)", "1", Utc },
        { "pow(0, 4)", "0", Utc },
        { "pow(2.5, 2)", "6.25", Utc },
        { "pow(0.5, 2)", "0.25", Utc },
        { "pow(-1, 3)", "-1", Utc },
        { "pow(4, 0.5)", "2", Utc },
        { "pow(16, 0.25)", "2", Utc },
        { "acos(0) * 2 = pi()", "true", Utc },
        { "asin(0)", "0", Utc },
        { "atan2(0, 0)", "0", Utc },
        { "exp(1)", "2.718281828459045", Utc },
        { "log10(100)", "2", Utc },
        { "sqrt(9)", "3", Utc },
        { "date('2008-09-08') - date('1983-10-06')", "9104", Utc },
        { "true() or date('')", "true", Utc },
        { "false() and date('')", "false", Utc },
        { "date('2001-12-26') > date('2001-12-25')", "true", Utc },
        { "date('1969-07-20') < date('1969-07-21')", "true", Utc },
        { "date('2004-05-01') = date('2004-05-01')", "true", Utc },
        { "regex('12345','[0-9]+')", "true", Utc },
        { "join('', randomize(instance('c8')/root/item/label, 1234))", "GFEDAHBC", C8 },
        { "join('', randomize(instance('c8')/root/item/label, '1234'))", "GFEDAHBC", C8 },
        { "join('', randomize(instance('c8')/root/item/label, 'foo'))", "EADBHGCF", C8 },
        { "join('', randomize(instance('c8')/root/item/name, '-6.8137120026589315 39.29392995851879'))", "hbdfagce", C8 },
        { "join('', randomize(instance('c8')/root/item/name, ${empty}))", "cbhafdge", C8 },
        { "join('', randomize(instance('c8')/root/item/name, ${zero}))", "cbhafdge", C8 },
        { "join('', randomize(instance('c8')/root/item/name, ${some}))", "ebcgdafh", C8 },
    };

    /// <summary>Expression, JavaRosa's expected number, the tolerance it is compared within, the context.</summary>
    public static TheoryData<string, double, double, string> Measures
    {
        get
        {
            // GeoAreaTest and GeoDistanceTest: five points near Patras, an area of 151452 m² ± 0.5 and a perimeter less its
            // closing edge of 1801 m ± 0.5; a quarter of the equator, 40,075,000 / 4 m, within 1e-7 — so the circumference
            // is the one the specification names, to the metre.
            const string Patras = "38.253094215699576 21.756382658677467 0 0; 38.25021274773806 21.756382658677467 0 0; 38.25007793942195 21.763892843919166 0 0; "
                + "38.25290886154963 21.763935759263404 0 0; 38.25146813817506 21.758421137528785 0 0";
            const double QuarterOfTheEquator = 40_075_000.0 / 4;

            return new TheoryData<string, double, double, string>
            {
                { $"area('{Patras};')", 151452, 0.5, None },
                { $"distance('{Patras}')", 1801, 0.5, None },
                { $"distance(concat('{Patras}', ''))", 1801, 0.5, None },
                { "distance('0 1 0 0; 0 91 0 0;')", QuarterOfTheEquator, 1e-7, None },
                { "distance('0 1 0 0; 0 91 0 0; 0 1 0 0;')", QuarterOfTheEquator * 2, 1e-7, None },
                { "distance('0 1 0 0;')", 0, 0, None },
                { "area('38.253094215699576 21.756382658677467 0 0;')", 0, 0, None },
                { "area('38.253094215699576 21.756382658677467 0 0; 38.25021274773806 21.756382658677467 0 0;')", 0, 0, None },

                // XPathEvalTest's vectors that it compares as doubles.
                { "decimal-time('2000-01-01T10:20:30.000+03:00')", 0.30590277777810115, 1e-9, Utc },
                { "6.1 - 7.8", -1.7, 1e-12, None },
                { "1.1 * -1.1", -1.21, 1e-12, None },
                { "exp(1)", Math.E, 1e-15, None },
                { "acos(0)", Math.PI / 2, 1e-15, None },
                { "log(" + Math.Exp(2).ToString("R", CultureInfo.InvariantCulture) + ")", 2, 1e-12, None },
            };
        }
    }

    /// <summary>A calculation, constraint or relevant from a corpus workbook; its value worked out by hand; the answers.</summary>
    public static TheoryData<string, string, string> Corpus => new()
    {

        // arced-xlsform-if-statement_xlsform-if-statement.xlsx (survey rows 5, 11, 16, 21, 28, 39): if() as an XLSForm tutorial writes it.
        { "if(${age} >= 18, 'adult', 'child')", "adult", "{value: function (n) { return {age: '18'}[n]; }}" },
        { "if(${age} >= 18, 'adult', 'child')", "child", "{value: function (n) { return {age: '17'}[n]; }}" },
        { "if(${fcs} <= 21, 'poor', if(${fcs} <= 35, 'borderline', 'acceptable'))", "poor", "{value: function (n) { return {fcs: '21'}[n]; }}" },
        { "if(${fcs} <= 21, 'poor', if(${fcs} <= 35, 'borderline', 'acceptable'))", "borderline", "{value: function (n) { return {fcs: '35'}[n]; }}" },
        { "if(${fcs} <= 21, 'poor', if(${fcs} <= 35, 'borderline', 'acceptable'))", "acceptable", "{value: function (n) { return {fcs: '35.5'}[n]; }}" },
        { "if(${child_age} >= 6 and ${grade} = '2', 'eligible', 'not_eligible')", "eligible", "{value: function (n) { return {child_age: '6', grade: '2'}[n]; }}" },
        { "if(${child_age} >= 6 and ${grade} = '2', 'eligible', 'not_eligible')", "not_eligible", "{value: function (n) { return {child_age: '6', grade: '3'}[n]; }}" },
        { "if(selected(${water_sources}, '3') or selected(${water_sources}, '4'), 1, 0)", "1", "{value: function (n) { return {water_sources: '1,4'}[n]; }}" },
        { "if(selected(${water_sources}, '3') or selected(${water_sources}, '4'), 1, 0)", "0", "{value: function (n) { return {water_sources: '1,2'}[n]; }}" },
        { "if(${has_latrine} = '1', 2, 0) + if(${has_soap} = '1', 1, 0) + if(${treats_water} = '1', 1, 0)", "3", "{value: function (n) { return {has_latrine: '1', has_soap: '0', treats_water: '1'}[n]; }}" },
        { "if(${preg_unit} = '1', . <= 42, . <= 10)", "true", "{current: '40', value: function (n) { return {preg_unit: '1'}[n]; }}" },
        { "if(${preg_unit} = '1', . <= 42, . <= 10)", "false", "{current: '40', value: function (n) { return {preg_unit: '2'}[n]; }}" },

        // esri-Community_Daily_Fishing_Log.xlsx (rows 18-21): count, max, min and sum over a repeat's answers.
        { "count(${species})", "3", "{value: function (n) { return {species: ['cod', 'hake', 'cod']}[n]; }}" },
        { "max(${fishsize})", "15.5", "{value: function (n) { return {fishsize: ['12', '15.5', '9']}[n]; }}" },
        { "min(${fishsize})", "9", "{value: function (n) { return {fishsize: ['12', '15.5', '9']}[n]; }}" },
        { "sum(${fishweight}) div 16", "3", "{value: function (n) { return {fishweight: ['16', '8', '24']}[n]; }}" },

        // esri-Community_Desertification_Expert_System.xlsx (rows 39, 46, 54, 56): the MEDALUS indices. dr = (1.2·1.5·1.4·1.3)^0.25 = 3.276^0.25 = 1.34535…;
        // sqi = 8.7516^0.167 = 1.43658…; cqi = 2.688^0.33 = 1.38584…. The form's own gap is pinned too: 1.375 is in no band, so N/A.
        { "round(pow((${sqi}*${cqi}*${vqi}*${mqi}), 0.25),2)", "1.35", "{value: function (n) { return {sqi: '1.2', cqi: '1.5', vqi: '1.4', mqi: '1.3'}[n]; }}" },
        { "round(pow((${soil_texture}*${parent_material}*${rock_fragment}*${soil_depth}*${slope_gradient}*${drainage}), 0.167),3)", "1.437", "{value: function (n) { return {soil_texture: '2', parent_material: '1.5', rock_fragment: '1.2', soil_depth: '1.7', slope_gradient: '1.3', drainage: '1.1'}[n]; }}" },
        { "round(pow((${rainfall}*${aridity_index}*${aspect}), 0.33),3)", "1.386", "{value: function (n) { return {rainfall: '1.4', aridity_index: '1.6', aspect: '1.2'}[n]; }}" },
        { "if((${dr}>1.53), \"C3\",if((${dr}>1.42 and ${dr}<=1.53), \"C2\", if((${dr}>1.38 and ${dr}<1.41), \"C1\",if((${dr}>1.33 and ${dr}<1.37), \"F3\", if((${dr}>1.27 and ${dr}<1.32), \"F2\", if((${dr}>1.23 and ${dr}<1.26), \"F1\", if((${dr}>1.17 and ${dr}<1.22), \"P\",if((${dr}<1.17), \"N\",  \"N/A\"))))))))", "F3", "{value: function (n) { return {dr: '1.35'}[n]; }}" },
        { "if((${dr}>1.53), \"C3\",if((${dr}>1.42 and ${dr}<=1.53), \"C2\", if((${dr}>1.38 and ${dr}<1.41), \"C1\",if((${dr}>1.33 and ${dr}<1.37), \"F3\", if((${dr}>1.27 and ${dr}<1.32), \"F2\", if((${dr}>1.23 and ${dr}<1.26), \"F1\", if((${dr}>1.17 and ${dr}<1.22), \"P\",if((${dr}<1.17), \"N\",  \"N/A\"))))))))", "N/A", "{value: function (n) { return {dr: '1.375'}[n]; }}" },
        { "if((${dr}>1.53), \"C3\",if((${dr}>1.42 and ${dr}<=1.53), \"C2\", if((${dr}>1.38 and ${dr}<1.41), \"C1\",if((${dr}>1.33 and ${dr}<1.37), \"F3\", if((${dr}>1.27 and ${dr}<1.32), \"F2\", if((${dr}>1.23 and ${dr}<1.26), \"F1\", if((${dr}>1.17 and ${dr}<1.22), \"P\",if((${dr}<1.17), \"N\",  \"N/A\"))))))))", "C2", "{value: function (n) { return {dr: '1.53'}[n]; }}" },

        // esri-Community_Residential_Septic_Tank_Inspection.xlsx (row 49): a checklist passes when all five are selected.
        { "if(count-selected(${treatmenttankcl})!=5,'Failed','Passed')", "Passed", "{value: function (n) { return {treatmenttankcl: 'inlet,outlet,baffle,lid,riser'}[n]; }}" },
        { "if(count-selected(${treatmenttankcl})!=5,'Failed','Passed')", "Failed", "{value: function (n) { return {treatmenttankcl: 'inlet,outlet'}[n]; }}" },

        // esri-Community_Roadkill_Report.xlsx (row 19) and esri-Samples_004_Constraints.xlsx (rows 2, 6): an e-mail pattern, a search unless anchored.
        { "regex(., '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,4}')", "true", "{current: 'ada@example.org'}" },
        { "regex(., '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,4}')", "false", "{current: 'ada@example'}" },
        { "regex(., '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+[.][A-Za-z]{2,4}$')", "false", "{current: 'ada@example.org '}" },
        { "not(selected(., 'na') and (selected(.,'blue') or selected(.,'red') or selected(.,'yellow')  or selected(.,'green')))", "false", "{current: 'na,blue'}" },
        { "not(selected(., 'na') and (selected(.,'blue') or selected(.,'red') or selected(.,'yellow')  or selected(.,'green')))", "true", "{current: 'na'}" },

        // esri-Samples_009_Calculations.xlsx (rows 6, 7, 11, 12): substr() of a multi-select answer, a date 14 days on (2024-02-20 + 14 =
        // 2024-03-05, at +03:00 and at UTC), and an age that turns 34 the day after today (1990-03-02 → 12,418 days → 33.998 years).
        { "if(selected(${select_other}, 'other'), concat(substr(${select_other}, 6), ' and ', ${other}), '0')", "red and purple", "{value: function (n) { return {select_other: 'other,red', other: 'purple'}[n]; }}" },
        { "count-selected(${select_other})", "2", "{value: function (n) { return {select_other: 'other,red'}[n]; }}" },
        { "date(decimal-date-time(${date}) +  14)", "2024-03-05", "{timezoneOffset: 180, value: function (n) { return {date: '2024-02-20'}[n]; }}" },
        { "date(decimal-date-time(${date}) +  14)", "2024-03-05", "{timezoneOffset: 0, value: function (n) { return {date: '2024-02-20'}[n]; }}" },
        { "int((decimal-date-time(today()) - decimal-date-time(${date})) div 365.25)", "33", "{timezoneOffset: 180, value: function (n) { return {date: '1990-03-02'}[n]; }}" },
        { "int((decimal-date-time(today()) - decimal-date-time(${date})) div 365.25)", "34", "{timezoneOffset: 180, value: function (n) { return {date: '1990-03-01'}[n]; }}" },

        // esri-Samples_Dates___Time.xlsx (rows 20, 27-29): minutes between two times (45), and the masks' substr()/regex() constraints.
        { "int((decimal-date-time(${lunchends}) - decimal-date-time(${lunchstarts})) *24*60)", "45", "{value: function (n) { return {lunchstarts: '2024-03-05T12:00:00.000+03:00', lunchends: '2024-03-05T12:45:00.000+03:00'}[n]; }}" },
        { "(int(substr(.,0,2))<24) and (int(substr(.,3,5))<60)", "true", "{current: '23:59'}" },
        { "(int(substr(.,0,2))<24) and (int(substr(.,3,5))<60)", "false", "{current: '24:00'}" },
        { "(int(substr(.,0,2))<24) and (int(substr(.,3,5))<60)", "false", "{current: '12:75'}" },
        { "(int(substr(.,0,2))<24) and (int(substr(.,2,4))<60)", "true", "{current: '2359'}" },
        { "regex(.,'^([0-1][0-9]|[2][0-3])([0-5][0-9])([A-Z])$')", "true", "{current: '1430Z'}" },
        { "regex(.,'^([0-1][0-9]|[2][0-3])([0-5][0-9])([A-Z])$')", "false", "{current: '2460Z'}" },

        // esri-Samples_Relevant.xlsx (rows 9, 13): int() truncates (25.9 + 25.9 → 25 + 25), and a bare 'false' is a path to nothing, so false;
        // 2024-02-16 is 14 days before today, which is not more than 14.
        { "int(${questiona})+ int(${questionb}) > 50", "true", "{value: function (n) { return {questiona: '30', questionb: '25.9'}[n]; }}" },
        { "int(${questiona})+ int(${questionb}) > 50", "false", "{value: function (n) { return {questiona: '25.9', questionb: '25.9'}[n]; }}" },
        { "boolean(if( ${lastinspectiondate}='',false, int(decimal-date-time(today())- decimal-date-time(${lastinspectiondate})) > 14))", "false", "{timezoneOffset: 180, value: function (n) { return {lastinspectiondate: ''}[n]; }}" },
        { "boolean(if( ${lastinspectiondate}='',false, int(decimal-date-time(today())- decimal-date-time(${lastinspectiondate})) > 14))", "true", "{timezoneOffset: 180, value: function (n) { return {lastinspectiondate: '2024-02-10'}[n]; }}" },
        { "boolean(if( ${lastinspectiondate}='',false, int(decimal-date-time(today())- decimal-date-time(${lastinspectiondate})) > 14))", "false", "{timezoneOffset: 180, value: function (n) { return {lastinspectiondate: '2024-02-16'}[n]; }}" },

        // esri-Samples_Repeat_Aggregate.xlsx (rows 10, 19): min() of dates is a number of days (1975-12-31 is day 2190), sum() of areas.
        { "min(${occupant_birthdate})", "2190", "{value: function (n) { return {occupant_birthdate: ['1980-05-01', '1975-12-31', '2001-01-01']}[n]; }}" },
        { "sum(${roomarea})", "39.75", "{value: function (n) { return {roomarea: ['12.5', '20', '7.25']}[n]; }}" },

        // laff-surveys_Hydrostatic_Testing_of_Ductile_Iron_Pipe.xlsx (rows 19, 27).
        { "if(${pValueEnd} < ${pValueStart}, ${pValueStart} - ${pValueEnd}, 'None')", "5", "{value: function (n) { return {pValueStart: '150', pValueEnd: '145'}[n]; }}" },
        { "if(${pValueEnd} < ${pValueStart}, ${pValueStart} - ${pValueEnd}, 'None')", "None", "{value: function (n) { return {pValueStart: '150', pValueEnd: '150'}[n]; }}" },
        { "if(${waterSupplied} < ${lValue} * 2, 'Satisfactory', 'Unsatisfactory')", "Satisfactory", "{value: function (n) { return {waterSupplied: '3', lValue: '1.6'}[n]; }}" },

        // laff-surveys_Pipe_Break_Calculations.xlsx (rows 15-17, 24-25, 34): π·8·0.25/144 = 0.0436332… → 0.043633; 0.7·0.043633·√(60·2.308·64.4)·7.48·60
        // = 1294.5011 → 1294.5; ×30×60 = 2330100; (6/12)²·0.785 = 0.19625; 0.19625·√(40·2.308·64.4)·0.7·7.48·60 = 4753.9147 → 4753.91; 10·0.5/12 → 0.416667.
        { "round((pi() * ${cirPipeDiameter} * ${cirWidthOfBreak}) div 144, 6)", "0.043633", "{value: function (n) { return {cirPipeDiameter: '8', cirWidthOfBreak: '0.25'}[n]; }}" },
        { "round(0.7 * ${cirAreaOfHole} * sqrt(${cirPSI} * 2.308 * 64.4) * 7.48 * 60, 2)", "1294.5", "{value: function (n) { return {cirAreaOfHole: '0.043633', cirPSI: '60'}[n]; }}" },
        { "round(${cirGPM} * ${cirLeakTime} * 60, 2)", "2330100", "{value: function (n) { return {cirGPM: '1294.5', cirLeakTime: '30'}[n]; }}" },
        { "round(pow((${holePipeDiameter} div 12), 2) * 0.785, 6)", "0.19625", "{value: function (n) { return {holePipeDiameter: '6'}[n]; }}" },
        { "round(${holeAreaOfHole} * sqrt(${holePSI} * 2.308 * 64.4) * 0.7 * 7.48 * 60, 2)", "4753.91", "{value: function (n) { return {holeAreaOfHole: '0.19625', holePSI: '40'}[n]; }}" },
        { "round(${splitLength} * (${splitWidth} div 12), 6)", "0.416667", "{value: function (n) { return {splitLength: '10', splitWidth: '0.5'}[n]; }}" },
    };

    /// <summary>Each of ODK's vectors gives JavaRosa's value.</summary>
    [Theory]
    [MemberData(nameof(OdkVectors))]
    public async Task An_expression_gives_the_value_ODKs_reference_implementation_gives(string expression, string expected, string context)
    {
        string got = await _page.Browser.EvaluateAsync<string>($"__run({Js(expression)}, {context})") ?? "(null)";
        Assert.True(expected == got, $"{expression} → '{got}', expected '{expected}'");
    }

    /// <summary>The vectors compared within a tolerance.</summary>
    [Theory]
    [MemberData(nameof(Measures))]
    public async Task A_number_is_within_ODKs_tolerance(string expression, double expected, double tolerance, string context)
    {
        double got = await _page.Browser.EvaluateAsync<double>($"__num({Js(expression)}, {context})");
        Assert.True(Math.Abs(got - expected) <= tolerance,
            $"{expression} → {got.ToString("R", CultureInfo.InvariantCulture)}, expected {expected.ToString("R", CultureInfo.InvariantCulture)} ± {tolerance.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Each corpus calculation gives the value worked out by hand.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task A_real_forms_calculation_gives_the_value_worked_out_by_hand(string expression, string expected, string context)
    {
        string got = await _page.Browser.EvaluateAsync<string>($"__run({Js(expression)}, {context})") ?? "(null)";
        Assert.True(expected == got, $"{expression} → '{got}', expected '{expected}'");
    }
}
