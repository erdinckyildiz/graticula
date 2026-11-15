using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's Survey123 compatibility, ADR-287 §5.20, 2026-11-15: <c>pulldata("@property")</c> and
/// <c>property()</c> answered from the signed-in member and the device, <c>pulldata("@geopoint")</c> from what the client
/// recorded, <c>calculationMode</c>, and the <c>draw</c> appearance. The server is the stand-in of
/// <see cref="FormFillClientTests.Site"/>; the checker's side is <c>AFormReadsSurvey123PropertiesAndCalculationModesTests</c>
/// in the Host tests.
/// </summary>
public sealed class FormFillClientCompatibilityTests : IClassFixture<FormFillClientTests.Site>
{
    private readonly FormFillClientTests.Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillClientCompatibilityTests(FormFillClientTests.Site site) => _site = site;

    private static JsonObject Question(string name, string type, string label, string? calculation = null, string? mode = null,
        string? readOnly = null, params string[] appearance)
    {
        JsonObject q = FormFillClientTests.Form.Question(name, type, label, appearance: appearance);
        q["calculation"] = calculation;
        q["calculationMode"] = mode;
        q["readOnly"] = readOnly;
        return q;
    }

    private string Publish(params JsonNode[] body) =>
        _site.Publish(new FormFillClientTests.Form { Root = FormFillClientTests.Form.Repeat("", body) });

    private Task<string?> ValueAsync(string name) =>
        _site.EvalAsync<string>($"document.querySelector('[data-name={name}] input, [data-name={name}] textarea').value");

    // ------------------------------------------------------------------------------------------------ properties

    private static JsonNode[] PropertyQuestions() =>
    [
        Question("user", "text", "User", "pulldata('@property', 'username')"),
        Question("full", "text", "Full", "pulldata(\"@property\", \"userFullName\")"),
        Question("first", "text", "First", "pulldata('@property', 'userFirstName')"),
        Question("last", "text", "Last", "pulldata('@property', 'userLastName')"),
        Question("mail", "text", "Mail", "pulldata('@property', 'email')"),
        Question("odkmail", "text", "ODK mail", "property('uri:email')"),
        Question("odkuser", "text", "ODK user", "property('username')"),
        Question("online", "text", "Online", "pulldata('@property', 'online')"),
        Question("offset", "text", "Offset", "pulldata('@property', 'utcOffset')"),
        Question("agent", "text", "Agent", "pulldata('@property', 'userAgent')"),
        Question("survey", "text", "Survey", "pulldata('@property', 'surveyName')"),
        Question("mode", "text", "Mode", "pulldata('@property', 'mode')"),
        Question("status", "text", "Status", "pulldata('@property', 'status')"),
        Question("device", "text", "Device", "pulldata('@property', 'deviceId')"),
        Question("odkdevice", "text", "ODK device", "property('deviceid')"),
        Question("portal", "text", "Portal", "pulldata('@property', 'portalUrl')"),
    ];

    [Fact]
    public async Task An_anonymous_form_answers_the_member_properties_empty_and_the_device_ones_as_the_browser_has_them()
    {
        _site.SignedIn = false;
        string id = Publish(PropertyQuestions());
        await _site.OpenAsync(id);

        foreach (string empty in new[] { "user", "full", "first", "last", "mail", "odkmail", "odkuser", "status", "portal" })
        {
            Assert.Equal("", await ValueAsync(empty));
        }

        Assert.Equal("true", await ValueAsync("online"));
        Assert.Equal(await _site.EvalAsync<string>("String(-new Date().getTimezoneOffset() / 60)"), await ValueAsync("offset"));
        Assert.Equal(await _site.EvalAsync<string>("navigator.userAgent"), await ValueAsync("agent"));
        Assert.Equal("Deneme", await ValueAsync("survey"));
        Assert.Equal("new", await ValueAsync("mode"));

        string? device = await ValueAsync("device");
        Assert.StartsWith("graticula:", device, StringComparison.Ordinal);
        Assert.Equal(device, await ValueAsync("odkdevice"));
    }

    [Fact]
    public async Task A_signed_in_member_is_what_the_portal_says_they_are()
    {
        _site.SignedIn = true;

        try
        {
            string id = Publish(PropertyQuestions());
            await _site.OpenAsync(id);
            await _site.WaitAsync("document.querySelector('[data-name=user] input').value === 'ada'", "the member to be read");

            Assert.Equal("ada", await ValueAsync("user"));
            Assert.Equal("ada", await ValueAsync("odkuser"));
            Assert.Equal("Ada Lovelace", await ValueAsync("full"));
            Assert.Equal("Ada", await ValueAsync("first"));
            Assert.Equal("Lovelace", await ValueAsync("last"));
            Assert.Equal("ada@example.org", await ValueAsync("mail"));
            Assert.Equal("ada@example.org", await ValueAsync("odkmail"));
            Assert.Equal(await _site.EvalAsync<string>("location.origin"), await ValueAsync("portal"));
        }
        finally
        {
            _site.SignedIn = false;
        }
    }

    // ------------------------------------------------------------------------------------------------- geopoint

    [Fact]
    public async Task A_geopoint_part_is_what_was_recorded_and_empty_where_nothing_was()
    {
        string id = Publish(
            Question("konum", "geopoint", "Konum"),
            Question("x", "text", "x", "pulldata('@geopoint', ${konum}, 'x')"),
            Question("y", "text", "y", "pulldata('@geopoint', ${konum}, 'y')"),
            Question("z", "text", "z", "pulldata('@geopoint', ${konum}, 'z')"),
            Question("acc", "text", "acc", "pulldata('@geopoint', ${konum}, 'horizontalAccuracy')"),
            Question("vacc", "text", "vacc", "pulldata('@geopoint', ${konum}, 'verticalAccuracy')"),
            Question("speed", "text", "speed", "pulldata('@geopoint', ${konum}, 'speed')"),
            Question("source", "text", "source", "pulldata('@geopoint', ${konum}, 'positionSourceType')"),
            Question("dms", "text", "dms", "pulldata('@geopoint', ${konum}, 'DMS.latitudeText')"),
            Question("sec", "text", "sec", "pulldata('@geopoint', ${konum}, 'DMS.longitudeSeconds')"));
        await _site.OpenAsync(id);

        // Before anything is answered every part is empty, and a typed point has a position and nothing a sensor gave.
        Assert.Equal("", await ValueAsync("x"));
        await _site.EvalAsync<bool>("""
            (function () {
              var boxes = document.querySelectorAll('[data-name=konum] .pair input');
              boxes[0].value = '39.96008'; boxes[0].dispatchEvent(new Event('input', { bubbles: true }));
              boxes[1].value = '116.45111'; boxes[1].dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);
        Assert.Equal("116.45111", await ValueAsync("x"));
        Assert.Equal("39.96008", await ValueAsync("y"));
        Assert.Equal("", await ValueAsync("z"));
        Assert.Equal("", await ValueAsync("acc"));
        Assert.Equal("1", await ValueAsync("source"));      // User: the person typed it

        // A device's reading carries an altitude and an accuracy; what a browser does not give stays empty.
        await _site.EvalAsync<bool>("""
            (function () {
              navigator.geolocation.getCurrentPosition = function (ok) {
                ok({ coords: { latitude: 39.96008, longitude: 116.45111, altitude: 800.5, accuracy: 12.3 } });
              };
              document.querySelector('[data-name=konum] [data-focus=locate]').click();
              return true;
            })()
            """);
        await _site.WaitAsync("document.querySelector('[data-name=acc] input').value === '12.3'", "the reading to be kept");
        Assert.Equal("800.5", await ValueAsync("z"));
        Assert.Equal("", await ValueAsync("vacc"));
        Assert.Equal("", await ValueAsync("speed"));
        Assert.Equal("2", await ValueAsync("source"));      // System Location
        Assert.Equal("39°57'36.3\"N", await ValueAsync("dms"));
        Assert.Equal("4", await ValueAsync("sec"));
    }

    // ------------------------------------------------------------------------------------------ calculationMode

    private async Task<string> TypeAsync(string name, string value)
    {
        await _site.SetAsync(name, value);
        return value;
    }

    [Fact]
    public async Task Always_takes_the_calculation_back_from_a_person_and_auto_does_not()
    {
        string id = Publish(
            Question("n", "integer", "n"),
            Question("auto", "text", "auto", "${n} * 2", null, "no"),
            Question("always", "text", "always", "${n} * 2", "always", "no"));
        await _site.OpenAsync(id);

        await TypeAsync("n", "2");
        Assert.Equal("4", await ValueAsync("auto"));
        Assert.Equal("4", await ValueAsync("always"));

        await TypeAsync("auto", "99");
        await TypeAsync("always", "99");
        Assert.Equal("99", await ValueAsync("auto"));
        Assert.Equal("99", await ValueAsync("always"));

        await TypeAsync("n", "3");
        Assert.Equal("99", await ValueAsync("auto"));        // the person's answer stands
        Assert.Equal("6", await ValueAsync("always"));       // recalculated whatever was typed
    }

    [Fact]
    public async Task Auto_takes_the_clock_once_and_always_takes_it_again()
    {
        string id = Publish(
            Question("n", "integer", "n"),
            Question("auto", "text", "auto", "string(now())"),
            Question("always", "text", "always", "string(now())", "always"));
        await _site.OpenAsync(id);

        string? autoFirst = await ValueAsync("auto");
        string? alwaysFirst = await ValueAsync("always");
        Assert.NotEqual("", autoFirst);
        await Task.Delay(50);
        await TypeAsync("n", "1");

        Assert.Equal(autoFirst, await ValueAsync("auto"));
        Assert.NotEqual(alwaysFirst, await ValueAsync("always"));
    }

    [Fact]
    public async Task Manual_waits_for_its_button_and_the_button_says_it_ran()
    {
        string id = Publish(
            Question("n", "integer", "n"),
            Question("total", "text", "total", "${n} * 2", "manual"));
        await _site.OpenAsync(id);

        await TypeAsync("n", "5");
        Assert.Equal("", await ValueAsync("total"));

        Assert.Equal("BUTTON", await _site.EvalAsync<string>("document.querySelector('[data-name=total] [data-focus=calculate]').tagName"));
        Assert.Equal("Calculate", await _site.EvalAsync<string>("document.querySelector('[data-name=total] [data-focus=calculate]').textContent"));
        Assert.False(string.IsNullOrEmpty(await _site.EvalAsync<string>("document.querySelector('[data-name=total] [data-focus=calculate]').getAttribute('aria-describedby')")),
            "The button is not tied to its question's label.");

        await _site.EvalAsync<bool>("document.querySelector('[data-name=total] [data-focus=calculate]').click(); true");
        Assert.Equal("10", await ValueAsync("total"));
        Assert.Equal("polite", await _site.EvalAsync<string>("document.querySelector('[data-name=total] p[aria-live]').getAttribute('aria-live')"));
        Assert.Equal("Calculated.", await _site.EvalAsync<string>("document.querySelector('[data-name=total] p[aria-live]').textContent"));

        await TypeAsync("n", "6");
        Assert.Equal("10", await ValueAsync("total"));       // not until the button is pressed again
        await _site.EvalAsync<bool>("document.querySelector('[data-name=total] [data-focus=calculate]').click(); true");
        Assert.Equal("12", await ValueAsync("total"));
    }

    [Fact]
    public async Task WhenEmpty_calculates_until_it_has_a_value_and_then_waits_for_the_button()
    {
        string id = Publish(
            Question("n", "integer", "n"),
            Question("first", "text", "first", "${n}", "whenEmpty", "no"));
        await _site.OpenAsync(id);

        Assert.Equal("", await ValueAsync("first"));
        await TypeAsync("n", "1");
        Assert.Equal("1", await ValueAsync("first"));
        await TypeAsync("n", "2");
        Assert.Equal("1", await ValueAsync("first"));         // it has its value now

        await TypeAsync("first", "");                         // even cleared, it stays as manual
        await TypeAsync("n", "3");
        Assert.Equal("", await ValueAsync("first"));

        await _site.EvalAsync<bool>("document.querySelector('[data-name=first] [data-focus=calculate]').click(); true");
        Assert.Equal("3", await ValueAsync("first"));
    }

    // ------------------------------------------------------------------------------------------------- draw

    [Fact]
    public async Task A_drawing_is_a_canvas_that_says_it_needs_a_pointer_and_is_kept_in_the_draft_as_a_png()
    {
        string id = Publish(Question("kroki", "image", "Kroki", null, null, null, "draw"));
        await _site.OpenAsync(id);

        // The statement is in words, before the canvas, and the canvas is described by it.
        string describedBy = await _site.EvalAsync<string>("document.querySelector('[data-name=kroki] canvas').getAttribute('aria-describedby')") ?? "";
        Assert.False(string.IsNullOrEmpty(describedBy));
        string statement = await _site.EvalAsync<string>($"document.getElementById('{describedBy}').textContent") ?? "";
        Assert.Contains("mouse, a pen or a finger", statement, StringComparison.Ordinal);
        Assert.Contains("keyboard", statement, StringComparison.Ordinal);
        Assert.True(await _site.EvalAsync<bool>($"!!document.querySelector('[data-name=kroki] .drawing')"));
        Assert.Equal("Clear drawing", await _site.EvalAsync<string>("document.querySelector('[data-name=kroki] [data-focus=clear]').textContent"));

        // A stroke with a pointer.
        int[] at = await _site.EvalAsync<int[]>("""
            (function () {
              var c = document.querySelector('[data-name=kroki] canvas');
              c.scrollIntoView({ block: 'center' });
              var r = c.getBoundingClientRect();
              return [Math.round(r.left + 40), Math.round(r.top + 40), Math.round(r.left + 200), Math.round(r.top + 120)];
            })()
            """) ?? [];
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at[0], y = at[1] });
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x = at[0], y = at[1], button = "left", clickCount = 1 });
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at[2], y = at[3], button = "left" });
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x = at[2], y = at[3], button = "left", clickCount = 1 });

        await _site.WaitAsync($$"""
            {{FormFillClientTests.Site.IdbAll("drafts")}}.then(function (all) {
              var d = all.filter(function (x) { return x.formId === '{{id}}'; })[0];
              var f = d && d.tree.files.kroki && d.tree.files.kroki[0];
              return !!f && f.blob instanceof Blob && f.blob.size > 0 && f.type === 'image/png' && f.name === 'kroki.png';
            })
            """, "the drawing to be stored in the draft as a PNG");
        Assert.Equal("Drawing captured.", await _site.EvalAsync<string>("document.querySelector('[data-name=kroki] .signature p[aria-live]').textContent"));

        // It comes back with the draft, and clearing it takes it out.
        await _site.OpenAsync(id);
        await _site.WaitAsync("document.getElementById('draft-reopened').textContent.indexOf('reopened') > 0", "the draft to be reopened");
        await _site.EvalAsync<bool>("document.querySelector('[data-name=kroki] [data-focus=clear]').click(); true");
        await _site.WaitAsync($$"""
            {{FormFillClientTests.Site.IdbAll("drafts")}}.then(function (all) {
              var d = all.filter(function (x) { return x.formId === '{{id}}'; })[0];
              return !d || !(d.tree.files.kroki && d.tree.files.kroki.length);   // a draft with nothing left in it is removed
            })
            """, "the cleared drawing to leave the draft");
    }

    [Fact]
    public async Task A_signature_is_still_a_signature_with_no_statement_about_drawing()
    {
        string id = Publish(Question("imza", "image", "Imza", null, null, null, "signature"));
        await _site.OpenAsync(id);

        Assert.True(await _site.EvalAsync<bool>("!document.querySelector('[data-name=imza] .drawing')"));
        Assert.Equal("Clear signature", await _site.EvalAsync<string>("document.querySelector('[data-name=imza] [data-focus=clear]').textContent"));
        Assert.True(string.IsNullOrEmpty(await _site.EvalAsync<string>("document.querySelector('[data-name=imza] canvas').getAttribute('aria-describedby') || ''")));
    }
}
