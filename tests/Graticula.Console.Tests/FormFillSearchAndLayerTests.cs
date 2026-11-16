using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's <c>search()</c> and <c>pulldata("@layer")</c>, ADR-287 §5.23: choices from a CSV uploaded with the
/// form, which work offline from the copy kept on the device; choices and calculated answers from a layer of this server,
/// asked of the stand-in server's layer (<see cref="FormFillClientTests.Site.Layers"/>) with the where clause the page
/// wrote; and, with no server, a question that says in words that it needs a connection while the form still submits.
/// </summary>
public sealed class FormFillSearchAndLayerTests : IClassFixture<FormFillClientTests.Site>
{
    private const string LayerPath = "/rest/services/hosted/Yerler/FeatureServer/0";

    private static readonly byte[] PlacesCsv = Encoding.UTF8.GetBytes(
        "place_id,place_name,country,sortby\r\n"
        + "ank,Ankara,TR,2\r\n"
        + "ist,\"İstanbul, Avrupa\",TR,1\r\n"
        + "ber,Berlin,DE,3\r\n"
        + "ist,İstanbul again,TR,4\r\n");

    private readonly FormFillClientTests.Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillSearchAndLayerTests(FormFillClientTests.Site site) => _site = site;

    private static JsonObject Q(string name, string type, string label, string? required = null, string? list = null, params string[] appearance) =>
        FormFillClientTests.Form.Question(name, type, label, required, list, null, null, null, appearance);

    private static JsonObject Search(JsonObject q, string? file = null, string? url = null, string? type = null, string? column = null,
        string? text = null, string? filterColumn = null, string? filterText = null)
    {
        q["search"] = new JsonObject
        {
            ["table"] = file ?? "p?url=" + url,
            ["file"] = file,
            ["url"] = url,
            ["query"] = new JsonObject(),
            ["type"] = type,
            ["column"] = column,
            ["text"] = text,
            ["filterColumn"] = filterColumn,
            ["filterText"] = filterText,
        };
        return q;
    }

    private static JsonObject Places => new() { ["places"] = new JsonArray(FormFillClientTests.Form.Choice("place_id", "place_name")) };

    private Task<string?> Offered(string name) => _site.EvalAsync<string>(
        $"Array.prototype.map.call(document.querySelectorAll('[data-name={name}] input[type=checkbox], [data-name={name}] input[type=radio]'), function (i) {{ return i.value; }}).join(',')");

    private Task<string?> ComboOptions(string name) => _site.EvalAsync<string>($$"""
        (function () {
          var input = document.querySelector('[data-name={{name}}] input[role=combobox]');
          input.dispatchEvent(new FocusEvent('blur'));   // a list left open from before is closed, as leaving the box closes it
          input.focus();
          input.click();
          return Array.prototype.map.call(document.querySelectorAll('[data-name={{name}}] [role=option]'), function (o) { return o.textContent; }).join('|');
        })()
        """);

    // ------------------------------------------------------------------------------------------------------ CSV

    [Fact]
    public async Task A_csv_search_offers_the_rows_its_search_and_filter_leave_in_sortby_order_once_each()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                Q("ulke", "text", "Ülke"),
                Search(Q("yerler", "select_multiple", "Yerler", list: "places"), file: "places.csv", type: "matches", column: "country", text: "${ulke}"),
                Search(Q("ara", "select_multiple", "Ara", list: "places"), file: "places.csv", type: "contains", column: "place_name", text: "'istan'"),
            ]),
            Choices = Places,
            Media = new() { ["places.csv"] = PlacesCsv },
        });
        await _site.OpenAsync(id);

        // No search text yet: every row, ordered by sortby, a name offered once.
        await _site.WaitAsync("document.querySelectorAll('[data-name=yerler] input[type=checkbox]').length === 3", "the CSV's choices");
        Assert.Equal("ist,ank,ber", await Offered("yerler"));
        Assert.Contains("İstanbul, Avrupa", await _site.EvalAsync<string>("document.querySelector('[data-name=yerler]').textContent") ?? "");

        // matches is exact, on the column the search names.
        await _site.SetAsync("ulke", "TR");
        await _site.WaitAsync("document.querySelectorAll('[data-name=yerler] input[type=checkbox]').length === 2", "the search to narrow the choices");
        Assert.Equal("ist,ank", await Offered("yerler"));

        // contains ignores case and accents, as the type-ahead box does (INFERRED, §5.23).
        Assert.Equal("ist", await Offered("ara"));
    }

    [Fact]
    public async Task A_select_one_with_search_is_a_type_ahead_box_and_a_choice_filter_reads_the_csv_rows()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                Search(Q("ilk", "select_multiple", "İlk", list: "places"), file: "places.csv"),
                With(Search(Q("ikinci", "select_one", "İkinci", list: "places"), file: "places.csv", type: "matches", column: "country",
                    text: "'TR'", filterColumn: "country", filterText: "'TR'"), "choiceFilter", "not(selected(${ilk}, name))"),
            ]),
            Choices = Places,
            Media = new() { ["places.csv"] = PlacesCsv },
        });
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!document.querySelector('[data-name=ikinci] input[role=combobox]')", "the type-ahead box");

        Assert.Equal("İstanbul, Avrupa|Ankara", await ComboOptions("ikinci"));

        await _site.EvalAsync<bool>("var c = document.querySelector('[data-name=ilk] input[value=ank]'); c.click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=ilk] input[value=ank]').checked", "the first answer");
        Assert.Equal("İstanbul, Avrupa", await ComboOptions("ikinci"));
    }

    [Fact]
    public async Task A_csv_that_is_not_among_the_forms_files_is_said_in_words()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [Search(Q("yer", "select_multiple", "Yer", list: "places"), file: "yok.csv")]),
            Choices = Places,
        });
        await _site.OpenAsync(id);

        Assert.Contains("\"yok.csv\" is not available yet", await _site.EvalAsync<string>("document.querySelector('[data-name=yer] .note-warn').textContent") ?? "");
    }

    [Fact]
    public async Task A_csv_search_works_offline_from_the_copy_kept_on_the_device()
    {
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                Q("ulke", "text", "Ülke"),
                Search(Q("yerler", "select_multiple", "Yerler", list: "places"), file: "places.csv", type: "matches", column: "country", text: "${ulke}"),
            ]),
            Choices = Places,
            Media = new() { ["places.csv"] = PlacesCsv },
        });
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!navigator.serviceWorker.controller", "the service worker to take the page", seconds: 20);
        await _site.OpenAsync(id);
        await _site.SetAsync("ulke", "DE");
        await _site.WaitAsync($"{FormFillClientTests.Site.IdbAll("forms")}.then(function (f) {{ return f.some(function (x) {{ return x.id === '{id}' && x.files && x.files['places.csv']; }}); }})",
            "the form and its CSV to be kept on the device");

        _site.Stop();
        try
        {
            await _site.OpenAsync(id, expectServer: false);
            await _site.WaitAsync("document.querySelectorAll('[data-name=yerler] input[type=checkbox]').length > 0", "the CSV's choices with no server", seconds: 20);
            await _site.SetAsync("ulke", "DE");
            await _site.WaitAsync("document.querySelectorAll('[data-name=yerler] input[type=checkbox]').length === 1", "the search with no server");
            Assert.Equal("ber", await Offered("yerler"));
        }
        finally
        {
            _site.Start();
        }
    }

    // ------------------------------------------------------------------------------------------------------ layer

    private static string LayerAnswer(string path, System.Collections.Specialized.NameValueCollection query)
    {
        if (!path.EndsWith("/query", StringComparison.Ordinal))
        {
            return """{"name":"Yerler","geometryType":"esriGeometryPoint","fields":[{"name":"objectid","type":"esriFieldTypeOID"},{"name":"place_id","type":"esriFieldTypeString"},{"name":"place_name","type":"esriFieldTypeString"},{"name":"country","type":"esriFieldTypeString"},{"name":"pop","type":"esriFieldTypeInteger"}]}""";
        }

        string where = query["where"] ?? "1=1";
        JsonArray features = [];

        foreach ((string pid, string name, string country, int pop) in new[] { ("ank", "Ankara", "TR", 5), ("ist", "İstanbul", "TR", 15), ("ber", "Berlin", "DE", 4) })
        {
            bool keep = (!where.Contains("country = ", StringComparison.Ordinal) || where.Contains($"country = '{country}'", StringComparison.Ordinal))
                && (!where.Contains("pop = ", StringComparison.Ordinal) || where.Contains($"pop = {pop}", StringComparison.Ordinal))
                && (!where.Contains("UPPER(place_name) LIKE '%", StringComparison.Ordinal) || where.Contains($"'%{name.ToUpperInvariant()[..2]}", StringComparison.Ordinal));

            if (keep)
            {
                features.Add(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["place_id"] = pid, ["place_name"] = name, ["country"] = country, ["pop"] = pop },
                    ["geometry"] = new JsonObject { ["x"] = 32.85, ["y"] = 39.93 },
                });
            }
        }

        if (query["resultRecordCount"] == "1" && features.Count > 1)
        {
            JsonNode first = features[0]!;
            features = [first.DeepClone()];
        }

        return new JsonObject { ["features"] = features }.ToJsonString();
    }

    [Fact]
    public async Task A_layer_search_asks_this_servers_layer_with_the_search_as_a_where_clause()
    {
        _site.Layers = LayerAnswer;
        _site.ClearLayerRequests();
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                Q("ulke", "text", "Ülke"),
                Search(Q("yer", "select_multiple", "Yer", list: "places"), url: LayerPath, type: "matches", column: "country", text: "${ulke}"),
                Search(Q("nufus", "select_multiple", "Nüfus", list: "places"), url: LayerPath, type: "matches", column: "pop", text: "'15'"),
            ]),
            Choices = Places,
        });
        await _site.OpenAsync(id);
        await _site.WaitAsync("document.querySelectorAll('[data-name=yer] input[type=checkbox]').length === 3", "the layer's choices");

        await _site.SetAsync("ulke", "TR");
        await _site.WaitAsync("document.querySelectorAll('[data-name=yer] input[type=checkbox]').length === 2", "the layer's choices narrowed");
        Assert.Equal("ank,ist", await Offered("yer"));
        Assert.Contains(_site.LayerRequests, r => r.Contains("country = 'TR'", StringComparison.Ordinal) && r.Contains("f=json", StringComparison.Ordinal));

        // A number field is compared with a number, which the layer's own description says it is.
        Assert.Equal("ist", await Offered("nufus"));
        Assert.Contains(_site.LayerRequests, r => r.Contains("pop = 15", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pulldata_layer_fills_a_calculation_and_asks_again_when_what_it_reads_changes()
    {
        _site.Layers = LayerAnswer;
        _site.ClearLayerRequests();
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                Q("kod", "text", "Kod"),
                With(Q("ad", "text", "Ad"), "calculation",
                    $"pulldata(\"@layer\", \"getValue\", \"attributes.place_name\", '{LayerPath}', concat(\"country = '\", ${{kod}}, \"'\"))"),
                With(Q("kayit", "text", "Kayıt"), "calculation", $"pulldata(\"@layer\", \"getRecord\", '{LayerPath}', \"country = 'DE'\")"),
            ]),
        });
        await _site.OpenAsync(id);

        await _site.SetAsync("kod", "DE");
        await _site.WaitAsync("document.querySelector('[data-name=ad] input').value === 'Berlin'", "the looked-up value");
        Assert.Equal("polite", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .layer-status').getAttribute('aria-live')"));
        Assert.Contains(_site.LayerRequests, r => r.Contains("where=country = 'DE'", StringComparison.Ordinal) && r.Contains("resultRecordCount=1", StringComparison.Ordinal));
        await _site.WaitAsync("document.querySelector('[data-name=kayit] input').value.indexOf('\"place_name\":\"Berlin\"') >= 0", "the whole record as JSON");

        // The input changed, so the calculation asks again.
        await _site.SetAsync("kod", "TR");
        await _site.WaitAsync("document.querySelector('[data-name=ad] input').value === 'Ankara'", "the value for the new input");

        // Nothing found is the empty answer, said under the question.
        await _site.SetAsync("kod", "XX");
        await _site.WaitAsync("document.querySelector('[data-name=ad] .layer-status').textContent.indexOf('Nothing was found') >= 0", "nothing found to be said");
        Assert.Equal("", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] input').value"));
    }

    [Fact]
    public async Task A_layer_of_another_server_is_never_asked_and_the_question_says_so()
    {
        _site.Layers = LayerAnswer;
        _site.ClearLayerRequests();
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                With(Q("ad", "text", "Ad"), "calculation",
                    "pulldata(\"@layer\", \"getValue\", \"attributes.place_name\", 'https://services.example.com/arcgis/rest/services/X/FeatureServer/0', '1=1')"),
                Search(Q("yer", "select_multiple", "Yer", list: "places"), url: "https://services.example.com/arcgis/rest/services/X/FeatureServer/0"),
                With(Q("admin", "text", "Admin"), "calculation", "pulldata(\"@layer\", \"getValue\", \"attributes.x\", '/admin/users', '1=1')"),
            ]),
            Choices = Places,
        });
        await _site.OpenAsync(id);

        await _site.WaitAsync("document.querySelector('[data-name=ad] .layer-status').textContent.indexOf('another server') >= 0", "the foreign layer to be said");
        Assert.Contains("another server", await _site.EvalAsync<string>("document.querySelector('[data-name=yer] .note-warn').textContent") ?? "");
        Assert.Contains("another server", await _site.EvalAsync<string>("document.querySelector('[data-name=admin] .layer-status').textContent") ?? "");
        Assert.Empty(_site.LayerRequests);
    }

    [Fact]
    public async Task With_no_server_a_layer_question_says_it_needs_a_connection_and_the_form_still_submits()
    {
        _site.Layers = LayerAnswer;
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                Q("kod", "text", "Kod"),
                With(Q("ad", "text", "Ad"), "calculation",
                    $"pulldata(\"@layer\", \"getValue\", \"attributes.place_name\", '{LayerPath}', concat(\"country = '\", ${{kod}}, \"'\"))"),
                Search(Q("yer", "select_one", "Yer", "yes", "places"), url: LayerPath, type: "matches", column: "country", text: "${kod}"),
            ]),
            Choices = Places,
        });
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!document.querySelector('[data-name=yer] input[role=combobox]')", "the layer's choices");
        await _site.EvalAsync<bool>("window.__errors = []; window.addEventListener('error', function (e) { window.__errors.push(String(e.message)); }); true");

        _site.Stop();
        try
        {
            await _site.SetAsync("kod", "DE");
            await _site.WaitAsync("document.querySelector('[data-name=ad] .layer-status').textContent.indexOf('needs a connection') >= 0", "the value to say it needs a connection");
            await _site.WaitAsync("document.querySelector('[data-name=yer] .note-warn').textContent.indexOf('need a connection') >= 0", "the list to say it needs a connection");

            // The required list that cannot load does not hold the record back: Submit queues it in the outbox.
            await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
            await _site.WaitAsync("!document.getElementById('done').hidden", "the record to be queued with no server", seconds: 20);
            Assert.Equal(0, await _site.EvalAsync<int>("window.__errors.length"));
        }
        finally
        {
            _site.Start();
        }
    }

    [Fact]
    public async Task The_words_are_turkish_when_the_form_is()
    {
        _site.Layers = LayerAnswer;
        string id = _site.Publish(new FormFillClientTests.Form
        {
            Root = FormFillClientTests.Form.Repeat("", [
                With(Q("ad", "text", "Ad"), "calculation",
                    "pulldata(\"@layer\", \"getValue\", \"attributes.place_name\", 'https://services.example.com/arcgis/rest/services/X/FeatureServer/0', '1=1')"),
            ]),
            Settings = """{"defaultLanguage":"Türkçe (tr)","languages":["Türkçe (tr)"]}""",
        });
        await _site.OpenAsync(id);

        await _site.WaitAsync("document.querySelector('[data-name=ad] .layer-status').textContent.length > 0", "the status");
        Assert.Contains("başka bir sunucunun", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] .layer-status').textContent") ?? "");
    }

    private static JsonObject With(JsonObject node, string key, JsonNode? value)
    {
        node[key] = value;
        return node;
    }
}
