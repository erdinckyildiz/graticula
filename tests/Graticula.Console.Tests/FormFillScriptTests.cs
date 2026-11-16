using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's <c>pulldata("@javascript")</c>, ADR-287 §5.27: a script uploaded with the form works an answer out,
/// in a sandboxed frame the page cannot be reached from. A script that tries to fetch, to read the cookie, the page, the
/// device's storage, or to load another script gets nothing; one that never returns is stopped and the form keeps working;
/// a form the server says may not run scripts, or whose script is missing, says so under the question; and the script
/// works with no server from the copy kept on the device. The server is the stand-in of
/// <see cref="FormFillClientTests.Site"/>; what the real server accepts and refuses is <c>AFormRunsItsScriptsInASandboxTests</c>.
/// </summary>
public sealed class FormFillScriptTests : IClassFixture<FormFillClientTests.Site>
{
    private readonly FormFillClientTests.Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillScriptTests(FormFillClientTests.Site site) => _site = site;

    private static JsonObject Q(string name, string type, string label) =>
        FormFillClientTests.Form.Question(name, type, label, null, null, null, null, null);

    private static JsonObject Calculated(string name, string expression)
    {
        JsonObject q = Q(name, "text", name);
        q["calculation"] = expression;
        return q;
    }

    private static string Call(string function, params string[] arguments) =>
        $"pulldata(\"@javascript\", \"probe.js\", \"{function}\"{string.Concat(arguments.Select(a => ", " + a))})";

    private string Script => $$"""
        var LEAK = '{{_site.Address}}/rest/services/leak/FeatureServer/0/query';
        function add(a, b) { return Number(a) + Number(b); }
        function greet(name, times) { return name === null || name === undefined ? 'nobody' : new Array(Number(times) + 1).join(name); }
        function record(a) { return { value: a, list: [1, 2] }; }
        function tryFetch(x) {
          var said = 'blocked';
          try { var r = new XMLHttpRequest(); r.open('GET', LEAK + '?xhr=' + x, false); r.send(); said = 'read:' + r.responseText; } catch (e) { said = 'blocked'; }
          try { fetch(LEAK + '?fetch=' + x).then(function () {}, function () {}); } catch (e) { }
          try { navigator.sendBeacon(LEAK + '?beacon=' + x); } catch (e) { }
          try { new WebSocket('{{_site.Address.Replace("http:", "ws:", StringComparison.Ordinal)}}/rest/services/leak/FeatureServer/0/query?ws=' + x); } catch (e) { }
          return said;
        }
        function tryCookie() { try { return typeof document === 'undefined' ? 'no document' : 'cookie:' + document.cookie; } catch (e) { return 'blocked'; } }
        function tryParent() { try { return typeof parent === 'undefined' || typeof window === 'undefined' ? 'no parent' : 'parent:' + parent.document.title; } catch (e) { return 'blocked'; } }
        function tryStorage() { try { return typeof localStorage === 'undefined' ? 'no storage' : 'storage:' + localStorage.getItem('graticula-secret'); } catch (e) { return 'blocked'; } }
        function tryDatabase() { try { indexedDB.open('graticula-forms'); return 'opened'; } catch (e) { return 'blocked'; } }
        function tryImport() { try { importScripts('{{_site.Address}}/forms/fill.js'); return 'imported'; } catch (e) { return 'blocked'; } }
        function tryAsync() { return Promise.resolve(1); }
        function fails() { throw new Error('no such parcel'); }
        function spin(x) { while (true) { } }
        """;

    private string Publish(JsonObject root, string scripts = "allowed", bool withFile = true) => _site.Publish(new FormFillClientTests.Form
    {
        Root = root,
        Scripts = scripts,
        Media = withFile ? new() { ["scripts/probe.js"] = Encoding.UTF8.GetBytes(Script) } : [],
    });

    private Task<string?> Value(string name) => _site.EvalAsync<string>($"document.querySelector('[data-name={name}] input').value");

    private Task<string?> Status(string name) => _site.EvalAsync<string>($"(document.querySelector('[data-name={name}] .layer-status') || {{}}).textContent || ''");

    [Fact]
    public async Task A_script_works_an_answer_out_from_the_questions_it_is_given_and_again_when_they_change()
    {
        string id = Publish(FormFillClientTests.Form.Repeat("", [
            Q("a", "integer", "A"),
            Q("b", "integer", "B"),
            Calculated("toplam", Call("add", "${a}", "${b}")),
            Calculated("selam", Call("greet", "${ad}", "2")),
            Q("ad", "text", "Ad"),
            Calculated("kayit", Call("record", "${a}")),
            Calculated("ust", $"concat('[', {Call("add", "${a}", "10")}, ']')"),
        ]));
        await _site.OpenAsync(id);

        // An empty answer reaches the function as null, as Survey123's page says it may.
        await _site.WaitAsync("document.querySelector('[data-name=selam] input').value === 'nobody'", "an empty answer to arrive as null");

        await _site.SetAsync("a", "2");
        await _site.SetAsync("b", "3");
        await _site.WaitAsync("document.querySelector('[data-name=toplam] input').value === '5'", "the script's sum");
        await _site.SetAsync("b", "40");
        await _site.WaitAsync("document.querySelector('[data-name=toplam] input').value === '42'", "the sum for the new answer");
        await _site.SetAsync("ad", "ab");
        await _site.WaitAsync("document.querySelector('[data-name=selam] input').value === 'abab'", "a text answer and a literal");

        // An object comes back as its JSON; a script's answer is a value inside a larger expression like any other.
        await _site.WaitAsync("document.querySelector('[data-name=kayit] input').value === '{\"value\":\"2\",\"list\":[1,2]}'", "the object as JSON");
        await _site.WaitAsync("document.querySelector('[data-name=ust] input').value === '[12]'", "the answer inside concat()");

        // One frame, hidden, sandboxed with scripts alone, and nothing said under a question that worked.
        Assert.Equal(1, await _site.EvalAsync<int>("document.querySelectorAll('iframe.script-sandbox').length"));
        Assert.Equal("allow-scripts", await _site.EvalAsync<string>("document.querySelector('iframe.script-sandbox').getAttribute('sandbox')"));
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('iframe.script-sandbox').hidden"));
        Assert.Equal("", await Status("toplam"));
        Assert.Equal("polite", await _site.EvalAsync<string>("document.querySelector('[data-name=toplam] .layer-status').getAttribute('aria-live')"));
    }

    [Fact]
    public async Task A_layer_lookup_wrapped_in_a_larger_calculation_is_run_again_when_its_answer_arrives()
    {
        // The same mechanism as a script's (section 5.23's), and the same fault: while the lookup was out the calculation
        // gave '<>', which is not empty, so an "auto" calculation whose inputs had not changed was never run again.
        _site.Layers = (_, _) => """{"features":[{"attributes":{"place_name":"Berlin"}}]}""";
        // A question to take the page's first focus: a box that has focus is not written over while it is being typed in.
        string id = Publish(FormFillClientTests.Form.Repeat("", [
            Q("not", "text", "Not"),
            Calculated("yer", "concat('<', pulldata(\"@layer\", \"getValue\", \"attributes.place_name\", '/rest/services/hosted/Yerler/FeatureServer/0', '1=1'), '>')"),
        ]), scripts: "none", withFile: false);
        await _site.OpenAsync(id);
        await _site.WaitAsync("document.querySelector('[data-name=yer] input').value === '<Berlin>'", "the looked-up value inside concat()");
    }

    [Fact]
    public async Task A_script_reaches_nothing_but_its_arguments()
    {
        _site.Layers = (_, _) => """{"secret":42}""";
        _site.ClearLayerRequests();
        await _site.Browser.NavigateAsync("about:blank");
        string id = Publish(FormFillClientTests.Form.Repeat("", [
            Q("a", "text", "A"),
            Calculated("ag", Call("tryFetch", "${a}")),
            Calculated("cerez", Call("tryCookie")),
            Calculated("sayfa", Call("tryParent")),
            Calculated("depo", Call("tryStorage")),
            Calculated("veritabani", Call("tryDatabase")),
            Calculated("betik", Call("tryImport")),
            Calculated("toplam", Call("add", "1", "2")),
        ]));
        await _site.OpenAsync(id);

        // What the page holds that a script would want: a cookie, a stored value, the member's token.
        await _site.EvalAsync<bool>("document.cookie = 'graticula-secret=1; path=/'; localStorage.setItem('graticula-secret', 'kept'); sessionStorage.setItem('gis-token', 'tok'); true");
        await _site.SetAsync("a", "x1");
        await _site.WaitAsync("document.querySelector('[data-name=toplam] input').value === '3'", "the sandbox to answer");
        await _site.WaitAsync("document.querySelector('[data-name=ag] input').value !== ''", "the network probe to answer");

        Assert.Equal("blocked", await Value("ag"));
        Assert.Equal("no document", await Value("cerez"));
        Assert.Equal("no parent", await Value("sayfa"));
        Assert.Equal("no storage", await Value("depo"));
        Assert.Equal("blocked", await Value("veritabani"));
        Assert.Equal("blocked", await Value("betik"));

        // Nothing reached the server: not the synchronous request, not fetch(), not a beacon, not a socket.
        await Task.Delay(500);
        Assert.DoesNotContain(_site.LayerRequests, r => r.Contains("x1", StringComparison.Ordinal));
        Assert.Empty(_site.LayerRequests);

        // The page's own state is as it was.
        Assert.Equal("kept", await _site.EvalAsync<string>("localStorage.getItem('graticula-secret')"));
        await _site.EvalAsync<bool>("localStorage.removeItem('graticula-secret'); document.cookie = 'graticula-secret=; max-age=0; path=/'; true");
    }

    [Fact]
    public async Task A_script_that_never_returns_is_stopped_and_the_form_keeps_working()
    {
        string id = Publish(FormFillClientTests.Form.Repeat("", [
            Q("a", "integer", "A"),
            Calculated("dongu", Call("spin", "${a}")),
            Calculated("toplam", Call("add", "${a}", "1")),
            Calculated("hata", Call("fails")),
            Calculated("soz", Call("tryAsync")),
            Calculated("yok", Call("nothingByThisName")),
            Q("not", "text", "Not"),
        ]));
        await _site.OpenAsync(id);

        await _site.SetAsync("a", "4");
        await _site.WaitAsync("document.querySelector('[data-name=dongu] .layer-status').textContent.indexOf('took too long') >= 0", "the loop to be stopped and said", seconds: 20);
        await _site.WaitAsync("document.querySelector('[data-name=toplam] input').value === '5'", "a call behind the loop to be answered by a new frame", seconds: 20);
        Assert.Equal("", await Value("dongu"));

        // The page answered all along, and still takes typing.
        await _site.SetAsync("not", "hâlâ çalışıyor");
        Assert.Equal("hâlâ çalışıyor", await Value("not"));
        Assert.True(await _site.EvalAsync<int>("document.querySelectorAll('iframe.script-sandbox').length") <= 1);

        // A function that throws, returns a promise, or is not there: the answer is empty and the question says why.
        await _site.WaitAsync("document.querySelector('[data-name=hata] .layer-status').textContent.indexOf('no such parcel') >= 0", "the thrown error to be said");
        await _site.WaitAsync("document.querySelector('[data-name=soz] .layer-status').textContent.indexOf('asynchronous calls are not supported') >= 0", "a promise to be refused");
        await _site.WaitAsync("document.querySelector('[data-name=yok] .layer-status').textContent.indexOf('no function named nothingByThisName') >= 0", "a missing function to be said");
        Assert.Equal("", await Value("hata"));

        // The form still submits.
        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation", seconds: 20);
        Assert.Contains(_site.Adds, a => a.Contains("hâlâ çalışıyor", StringComparison.Ordinal) && a.Contains("\"toplam\":\"5\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_form_whose_scripts_may_not_run_or_are_missing_says_so_and_starts_no_frame()
    {
        string refused = Publish(FormFillClientTests.Form.Repeat("", [Calculated("toplam", Call("add", "1", "2"))]), scripts: "refused");
        await _site.OpenAsync(refused);
        await _site.WaitAsync("document.querySelector('[data-name=toplam] .layer-status').textContent.indexOf('signed-in member') >= 0", "the refusal to be said");
        Assert.Equal(0, await _site.EvalAsync<int>("document.querySelectorAll('iframe').length"));
        Assert.Equal("", await Value("toplam"));

        string missing = Publish(FormFillClientTests.Form.Repeat("", [Calculated("toplam", Call("add", "1", "2"))]), withFile: false);
        await _site.OpenAsync(missing);
        await _site.WaitAsync("document.querySelector('[data-name=toplam] .layer-status').textContent.indexOf('was not uploaded') >= 0", "the missing script to be said");
        Assert.Equal(0, await _site.EvalAsync<int>("document.querySelectorAll('iframe').length"));
        Assert.Equal("probe.js", await _site.EvalAsync<string>("GraticulaFormFill.missingScripts().join(',')"));
    }

    [Fact]
    public async Task The_script_is_kept_on_the_device_and_works_with_no_server()
    {
        string id = Publish(FormFillClientTests.Form.Repeat("", [Q("a", "integer", "A"), Calculated("toplam", Call("add", "${a}", "100"))]));
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!navigator.serviceWorker.controller", "the service worker to take the page", seconds: 20);
        await _site.OpenAsync(id);
        await _site.SetAsync("a", "1");
        await _site.WaitAsync("document.querySelector('[data-name=toplam] input').value === '101'", "the script online");
        await _site.EvalAsync<bool>("document.getElementById('download-offline').click(); true");
        await _site.WaitAsync($"{FormFillClientTests.Site.IdbAll("forms")}.then(function (f) {{ var x = f.filter(function (r) {{ return r.id === '{id}'; }})[0]; return !!x && !!x.scripts && typeof x.scripts['probe.js'] === 'string'; }})",
            "the script to be stored with the form", seconds: 15);

        _site.Stop();
        try
        {
            await _site.OpenAsync(id, expectServer: false);
            await _site.SetAsync("a", "5");
            await _site.WaitAsync("document.querySelector('[data-name=toplam] input').value === '105'", "the script with no server", seconds: 20);
        }
        finally
        {
            _site.Start();
        }
    }
}
