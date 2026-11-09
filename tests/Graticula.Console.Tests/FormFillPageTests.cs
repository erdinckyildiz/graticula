using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's page, <c>/forms/fill.html</c> — ADR-287 §5.4 (filled online) and §5.5 (offline: a submission made
/// with no signal waits on the device and is sent when the signal returns).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a <see cref="ConsoleTest"/>, on purpose.</b> That harness stops every write in the browser and answers it with
/// an empty body, so that a console test can never change the server it reads. A form's whole job is to write: the
/// test is that the rows arrive, related, with their attachment. So this class drives Chrome with
/// <see cref="DevTools"/> directly, as <see cref="XlsformEvaluatorTests"/> does, and its writes reach the server.
/// </para>
/// <para>
/// <b>It publishes a form of its own</b> through <c>/admin/forms</c> and <c>/publish</c>, the JSON model the conformance
/// suite uses, under a name nobody else uses, and removes the item, the service and its choice lists afterwards.
/// </para>
/// <para>
/// Needs <c>GRATICULA_TEST_URL</c>, <c>GRATICULA_TEST_USER</c> and <c>GRATICULA_TEST_PASSWORD</c>, and fails rather than
/// skips without them, as the rest of this suite does.
/// </para>
/// </remarks>
public sealed class FormFillPageTests : IClassFixture<FormFillPageTests.Published>
{
    private readonly Published _form;

    /// <summary>Creates the tests over the published form.</summary>
    /// <param name="form">The form, published once for the class.</param>
    public FormFillPageTests(Published form) => _form = form;

    // ------------------------------------------------------------------------------------------------------- the tests

    [Fact]
    public async Task A_form_is_filled_and_submitted_and_its_rows_repeat_and_attachment_arrive()
    {
        await using DevTools browser = await OpenAsync();
        string name = "Bina " + Guid.NewGuid().ToString("N")[..8];

        await FillBasicsAsync(browser, name, damage: "agir");
        await TypeAsync(browser, "detay", "Çatı çöktü");
        await TypeAsync(browser, "yas", "37");

        // The repeat starts with one instance; a second is added, and the calculation counts them.
        await browser.EvaluateAsync<bool>("""
            (function () {
              var add = Array.prototype.filter.call(document.querySelectorAll('button'), function (b) { return /^Add /.test(b.textContent); })[0];
              add.click();
              return true;
            })()
            """);
        string[] floors = await browser.EvaluateAsync<string[]>(
            "Array.prototype.map.call(document.querySelectorAll('[data-name=kat_no] input'), function (i) { return i.id; })") ?? [];
        Assert.Equal(2, floors.Length);
        await browser.EvaluateAsync<bool>($$"""
            (function () {
              var inputs = document.querySelectorAll('[data-name=kat_no] input');
              ['1', '2'].forEach(function (v, i) { inputs[i].value = v; inputs[i].dispatchEvent(new Event('input', { bubbles: true })); });
              return true;
            })()
            """);

        // A photo, as a camera would hand it over.
        await browser.EvaluateAsync<bool>("""
            (function () {
              var input = document.querySelector('[data-name=foto] input[type=file]');
              var bytes = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='), function (c) { return c.charCodeAt(0); });
              var dt = new DataTransfer();
              dt.items.add(new File([bytes], 'foto.png', { type: 'image/png' }));
              input.files = dt.files;
              input.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """);
        await WaitAsync(browser, "document.querySelectorAll('[data-name=foto] .files li').length === 1", "the photo to be listed");

        await SubmitAsync(browser);
        await WaitAsync(browser, "!document.getElementById('done').hidden", "the confirmation");
        Assert.Equal("Submitted", await browser.EvaluateAsync<string>("document.getElementById('done-heading').textContent"));

        JsonElement row = await OneRowAsync(name);
        Assert.Equal("agir", row.GetProperty("attributes").GetProperty(_form.Column("hasar")).GetString());
        Assert.Equal("Çatı çöktü", row.GetProperty("attributes").GetProperty(_form.Column("detay")).GetString());
        Assert.Equal(37, row.GetProperty("attributes").GetProperty(_form.Column("yas")).GetInt32());
        Assert.Equal("2", row.GetProperty("attributes").GetProperty(_form.Column("toplam")).ToString());
        Assert.Equal(29.0, row.GetProperty("geometry").GetProperty("x").GetDouble(), 6);
        Assert.Equal(41.0, row.GetProperty("geometry").GetProperty("y").GetDouble(), 6);

        string globalId = row.GetProperty("attributes").GetProperty("globalid").GetString()!;
        JsonElement children = await QueryAsync(_form.FloorsUrl, $"parentglobalid='{globalId}'");
        int[] numbers = children.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("attributes").GetProperty(_form.FloorColumn("kat_no")).GetInt32()).OrderBy(n => n).ToArray();
        Assert.Equal([1, 2], numbers);

        int objectId = row.GetProperty("attributes").GetProperty("objectid").GetInt32();
        (HttpStatusCode status, string body) = await Published.RequestAsync(HttpMethod.Get,
            $"{Published.Root}{_form.TopUrl}/{objectId}/attachments?f=json", null);
        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement infos = JsonDocument.Parse(body).RootElement.GetProperty("attachmentInfos");
        Assert.Equal(1, infos.GetArrayLength());
        Assert.Equal("foto.png", infos[0].GetProperty("name").GetString());

        // ADR-287 §5.2: the attachment says which question it answers.
        Assert.True(infos[0].TryGetProperty("keywords", out JsonElement keywords), $"The attachment carries no keywords: {infos[0]}");
        Assert.Equal("foto", keywords.GetString());
    }

    [Fact]
    public async Task A_question_that_is_not_relevant_is_hidden_and_its_answer_is_not_sent()
    {
        await using DevTools browser = await OpenAsync();
        string name = "Bina " + Guid.NewGuid().ToString("N")[..8];

        await FillBasicsAsync(browser, name, damage: "agir");
        Assert.False(await HiddenAsync(browser, "detay"), "Details should be shown when the damage is heavy.");
        await TypeAsync(browser, "detay", "silinecek");

        await ChooseAsync(browser, "hasar", "yok");
        Assert.True(await HiddenAsync(browser, "detay"), "Details should be hidden when there is no damage.");

        await SubmitAsync(browser);
        await WaitAsync(browser, "!document.getElementById('done').hidden", "the confirmation");

        JsonElement row = await OneRowAsync(name);
        JsonElement attributes = row.GetProperty("attributes");
        Assert.True(!attributes.TryGetProperty(_form.Column("detay"), out JsonElement detail) || detail.ValueKind == JsonValueKind.Null,
            $"A non-relevant answer was stored: {detail}");
    }

    [Fact]
    public async Task Required_and_constraint_block_the_submit_and_say_why_next_to_the_question()
    {
        await using DevTools browser = await OpenAsync();

        await SubmitAsync(browser);
        await WaitAsync(browser, "!document.getElementById('summary').hidden", "the summary of problems");
        Assert.True(await browser.EvaluateAsync<bool>("document.getElementById('done').hidden"), "The form was sent with required answers missing.");
        Assert.Contains("Bir ad yazın", await ErrorAsync(browser, "ad"));

        // Focus moved to the first question with a problem: the location, the form's first question.
        Assert.Equal("konum", await browser.EvaluateAsync<string>(
            "(document.activeElement.closest('[data-name]') || {}).getAttribute ? document.activeElement.closest('[data-name]').getAttribute('data-name') : ''"));

        await FillBasicsAsync(browser, "Kısıt", damage: "yok");
        await TypeAsync(browser, "yas", "200");
        Assert.Contains("0 ile 120", await ErrorAsync(browser, "yas"));

        await SubmitAsync(browser);
        await Task.Delay(300);
        Assert.True(await browser.EvaluateAsync<bool>("document.getElementById('done').hidden"), "The form was sent with an answer its constraint refuses.");
        Assert.Equal("yas", await browser.EvaluateAsync<string>("document.activeElement.closest('[data-name]').getAttribute('data-name')"));

        await TypeAsync(browser, "yas", "40");
        Assert.Equal(string.Empty, await ErrorAsync(browser, "yas"));
    }

    [Fact]
    public async Task A_submission_made_offline_waits_in_the_outbox_and_is_sent_when_the_signal_returns()
    {
        await using DevTools browser = await OpenAsync();
        string name = "Çevrimdışı " + Guid.NewGuid().ToString("N")[..8];

        await FillBasicsAsync(browser, name, damage: "yok");

        await browser.CallAsync("Network.enable");
        await browser.CallAsync("Network.emulateNetworkConditions", new { offline = true, latency = 0, downloadThroughput = -1, uploadThroughput = -1 });

        try
        {
            await WaitAsync(browser, "navigator.onLine === false", "the page to see it is offline");
            await SubmitAsync(browser);
            await WaitAsync(browser, "!document.getElementById('done').hidden", "the queued confirmation");
            Assert.Equal("Saved on this device", await browser.EvaluateAsync<string>("document.getElementById('done-heading').textContent"));
            Assert.Equal(1, await browser.EvaluateAsync<int>("GraticulaFormFill.outbox().then(function (o) { return o.length; })"));
            Assert.Equal("1", await browser.EvaluateAsync<string>("document.getElementById('outbox-count').textContent"));
            Assert.Equal(0, await CountAsync(name));
        }
        finally
        {
            await browser.CallAsync("Network.emulateNetworkConditions", new { offline = false, latency = 0, downloadThroughput = -1, uploadThroughput = -1 });
        }

        // The page sends on the 'online' event by itself; nothing here presses Send now.
        await WaitAsync(browser, "GraticulaFormFill.outbox().then(function (o) { return o.length === 0; })", "the outbox to empty", seconds: 30);
        Assert.Equal("0", await browser.EvaluateAsync<string>("document.getElementById('outbox-count').textContent"));
        Assert.Equal(1, await CountAsync(name));
    }

    [Fact]
    public async Task A_record_whose_rows_already_reached_the_server_is_resent_and_lands_once()
    {
        // The case a phone meets when the signal drops after the server applied the edit but before the answer came
        // back: the record is still in the outbox, and its rows are already on the server under their GlobalIDs.
        await using DevTools browser = await OpenAsync();
        string name = "Yarım " + Guid.NewGuid().ToString("N")[..8];

        await FillBasicsAsync(browser, name, damage: "yok");
        await browser.CallAsync("Network.enable");
        await browser.CallAsync("Network.emulateNetworkConditions", new { offline = true, latency = 0, downloadThroughput = -1, uploadThroughput = -1 });
        try
        {
            await WaitAsync(browser, "navigator.onLine === false", "the page to see it is offline");
            await SubmitAsync(browser);
            await WaitAsync(browser, "!document.getElementById('done').hidden", "the queued confirmation");
            string adds = await browser.EvaluateAsync<string>("""
                new Promise(function (resolve) {
                  var open = indexedDB.open('graticula-forms');
                  open.onsuccess = function () {
                    var all = open.result.transaction('outbox').objectStore('outbox').getAll();
                    all.onsuccess = function () {
                      var record = all.result.filter(function (r) { return r.layers[0].adds[0].attributes; }).pop();
                      resolve(JSON.stringify(record.layers[0].adds));
                    };
                  };
                })
                """) ?? "[]";

            // The same rows, sent by someone else first — what an earlier, interrupted attempt would have left — and
            // while the page is still offline, because it sends by itself the moment the signal returns.
            (HttpStatusCode status, string body) = await Published.RequestAsync(HttpMethod.Post, $"{Published.Root}{_form.TopUrl}/applyEdits",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json", ["adds"] = adds, ["useGlobalIds"] = "true", ["rollbackOnFailure"] = "true",
                }));
            Assert.True(status == HttpStatusCode.OK && body.Contains("\"success\":true", StringComparison.Ordinal), $"The planted add answered {(int)status}: {body}");
        }
        finally
        {
            await browser.CallAsync("Network.emulateNetworkConditions", new { offline = false, latency = 0, downloadThroughput = -1, uploadThroughput = -1 });
        }

        await WaitAsync(browser, "GraticulaFormFill.sendNow().then(function () { return GraticulaFormFill.outbox(); }).then(function (o) { return o.length === 0; })",
            "the outbox to empty", seconds: 30);
        Assert.Equal(1, await CountAsync(name));
    }

    [Fact]
    public async Task A_form_downloaded_for_offline_use_opens_with_no_signal()
    {
        await using DevTools browser = await OpenAsync();

        await browser.EvaluateAsync<bool>("(function () { document.getElementById('download-offline').click(); return true; })()");
        await WaitAsync(browser, "/^Saved for offline use/.test(document.getElementById('offline-status').textContent)", "the form to be kept");

        // The service worker has kept the page and its scripts once it controls the page.
        await WaitAsync(browser, "navigator.serviceWorker.ready.then(function () { return !!navigator.serviceWorker.controller || caches.has('graticula-forms-shell-v1'); })",
            "the service worker", seconds: 20);
        await WaitAsync(browser, "caches.open('graticula-forms-shell-v1').then(function (c) { return c.keys(); }).then(function (k) { return k.length >= 5; })",
            "the shell to be cached", seconds: 20);

        await browser.CallAsync("Network.enable");
        await browser.CallAsync("Network.emulateNetworkConditions", new { offline = true, latency = 0, downloadThroughput = -1, uploadThroughput = -1 });

        try
        {
            await browser.NavigateAsync($"{Published.Root}/forms/fill.html?id={_form.ItemId}");
            await WaitAsync(browser, "!!window.GraticulaFormFill && !document.getElementById('form').hidden && !!document.querySelector('[data-name=ad] input')",
                "the form to be drawn offline", seconds: 20);
            Assert.False(await browser.EvaluateAsync<bool>("navigator.onLine"), "The page was not offline.");
        }
        finally
        {
            await browser.CallAsync("Network.emulateNetworkConditions", new { offline = false, latency = 0, downloadThroughput = -1, uploadThroughput = -1 });
        }
    }

    // ----------------------------------------------------------------------------------------------------- the helpers

    private async Task<DevTools> OpenAsync()
    {
        DevTools browser = await DevTools.LaunchAsync();
        await browser.PlantAsync($"try {{ sessionStorage.setItem('gis-token', {JsonSerializer.Serialize(await Published.TokenAsync())}); }} catch (e) {{}}");
        await browser.NavigateAsync($"{Published.Root}/forms/fill.html?id={_form.ItemId}");
        await WaitAsync(browser, "!!window.GraticulaFormFill && !document.getElementById('form').hidden && !!document.querySelector('[data-name=ad] input')",
            "the form to be drawn", seconds: 20);
        return browser;
    }

    private static async Task FillBasicsAsync(DevTools browser, string name, string damage)
    {
        await browser.EvaluateAsync<bool>("""
            (function () {
              var lat = document.querySelector('[data-name=konum] [data-focus=lat]');
              var lon = document.querySelector('[data-name=konum] [data-focus=lon]');
              lat.value = '41'; lat.dispatchEvent(new Event('input', { bubbles: true }));
              lon.value = '29'; lon.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);
        await TypeAsync(browser, "ad", name);
        await ChooseAsync(browser, "hasar", damage);
    }

    private static Task<bool> TypeAsync(DevTools browser, string question, string value) =>
        browser.EvaluateAsync<bool>($$"""
            (function () {
              var input = document.querySelector('[data-name={{question}}] input, [data-name={{question}}] textarea');
              input.focus();
              input.value = {{JsonSerializer.Serialize(value)}};
              input.dispatchEvent(new Event('input', { bubbles: true }));
              input.blur();
              return true;
            })()
            """);

    private static Task<bool> ChooseAsync(DevTools browser, string question, string choice) =>
        browser.EvaluateAsync<bool>($$"""
            (function () {
              var input = document.querySelector('[data-name={{question}}] input[value="{{choice}}"]');
              input.click();
              return input.checked;
            })()
            """);

    private static async Task<bool> HiddenAsync(DevTools browser, string question) =>
        await browser.EvaluateAsync<bool>($$"""
            (function () {
              var node = document.querySelector('[data-name={{question}}]');
              return !node || node.hidden || node.offsetParent === null;
            })()
            """);

    private static async Task<string> ErrorAsync(DevTools browser, string question) =>
        await browser.EvaluateAsync<string>($"(document.querySelector('[data-name={question}] .error') || {{}}).textContent || ''") ?? string.Empty;

    private static Task<bool> SubmitAsync(DevTools browser) =>
        browser.EvaluateAsync<bool>("(function () { document.getElementById('submit').click(); return true; })()");

    private static async Task WaitAsync(DevTools browser, string condition, string what, int seconds = 15)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);

        while (DateTime.UtcNow < until)
        {
            if (await browser.EvaluateAsync<bool>($"Promise.resolve((function () {{ try {{ return {condition}; }} catch (e) {{ return false; }} }})()).then(function (v) {{ return !!v; }})"))
            {
                return;
            }

            await Task.Delay(150);
        }

        string state = await browser.EvaluateAsync<string>(
            "JSON.stringify({ summary: (document.getElementById('summary') || {}).textContent, loading: (document.getElementById('loading') || {}).textContent, done: (document.getElementById('done-text') || {}).textContent, outbox: (document.getElementById('outbox-list') || {}).textContent })") ?? "";
        throw new TimeoutException($"Waited {seconds} s for {what}. Page: {state}");
    }

    private static async Task<JsonElement> QueryAsync(string layerUrl, string where)
    {
        (HttpStatusCode status, string body) = await Published.RequestAsync(HttpMethod.Get,
            $"{Published.Root}{layerUrl}/query?f=json&outFields=*&where={Uri.EscapeDataString(where)}", null);
        Assert.True(status == HttpStatusCode.OK && !body.Contains("\"error\"", StringComparison.Ordinal), $"The query answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<int> CountAsync(string name) =>
        (await QueryAsync(_form.TopUrl, $"{_form.Column("ad")}='{name.Replace("'", "''", StringComparison.Ordinal)}'")).GetProperty("features").GetArrayLength();

    private async Task<JsonElement> OneRowAsync(string name)
    {
        JsonElement features = (await QueryAsync(_form.TopUrl, $"{_form.Column("ad")}='{name.Replace("'", "''", StringComparison.Ordinal)}'")).GetProperty("features");
        Assert.Equal(1, features.GetArrayLength());
        return features[0].Clone();
    }

    // ----------------------------------------------------------------------------------------------------- the fixture

    /// <summary>A form published once for the class, and removed after it.</summary>
    public sealed class Published : IAsyncLifetime
    {
        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = false,
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        private static string? _token;

        private JsonElement _top;
        private JsonElement _floors;

        /// <summary>The server, without a trailing slash.</summary>
        public static string Root => (Environment.GetEnvironmentVariable(ConsoleTest.UrlVariable) ?? string.Empty).TrimEnd('/');

        /// <summary>The form item.</summary>
        public string ItemId { get; private set; } = string.Empty;

        /// <summary>The form's id, which names its service.</summary>
        public string FormId { get; } = "zz_fill_" + Guid.NewGuid().ToString("N")[..8];

        /// <summary>The top layer's address, below the root.</summary>
        public string TopUrl => _top.GetProperty("url").GetString()!;

        /// <summary>The repeat's table, below the root.</summary>
        public string FloorsUrl => _floors.GetProperty("url").GetString()!;

        /// <summary>The top layer's column for a question.</summary>
        public string Column(string question) => _top.GetProperty("columns").GetProperty(question).GetString()!;

        /// <summary>The repeat table's column for a question.</summary>
        public string FloorColumn(string question) => _floors.GetProperty("columns").GetProperty(question).GetString()!;

        /// <summary>An administrator's token, once for the class.</summary>
        public static async Task<string> TokenAsync()
        {
            if (_token is not null)
            {
                return _token;
            }

            string? user = Environment.GetEnvironmentVariable(ConsoleTest.UserVariable);
            string? password = Environment.GetEnvironmentVariable(ConsoleTest.PasswordVariable);
            Assert.False(string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password),
                $"{ConsoleTest.UserVariable} and {ConsoleTest.PasswordVariable} must name an account that may publish on {Root}.");

            using HttpRequestMessage request = new(HttpMethod.Post, $"{Root}/rest/auth/login")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { name = user, password }), Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await Http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Signing in as '{user}' answered {(int)response.StatusCode}: {body}");
            return _token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
        }

        /// <summary>A request with the administrator's token.</summary>
        public static async Task<(HttpStatusCode Status, string Body)> RequestAsync(HttpMethod method, string url, HttpContent? content)
        {
            using HttpRequestMessage request = new(method, url) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
            using HttpResponseMessage response = await Http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static JsonObject Sheet(string name, string[] columns, params string[][] rows) => new()
        {
            ["name"] = name,
            ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)c)]),
            ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonArray([.. r.Select(c => (JsonNode?)c)]))]),
        };

        private JsonObject Model() => new()
        {
            ["format"] = "xlsform/1",
            ["sheets"] = new JsonArray(
                Sheet("survey", ["type", "name", "label", "required", "required_message", "relevant", "constraint", "constraint_message", "calculation"],
                    ["geopoint", "konum", "Konum", "yes"],
                    ["text", "ad", "Bina adı", "yes", "Bir ad yazın."],
                    ["integer", "yas", "Bina yaşı", "", "", "", ". >= 0 and . <= 120", "Yaş 0 ile 120 arasında olmalı."],
                    ["select_one hasar", "hasar", "Hasar", "yes"],
                    ["text", "detay", "Hasarın ayrıntısı", "", "", "${hasar} = 'agir'"],
                    ["image", "foto", "Fotoğraf"],
                    ["begin repeat", "katlar", "Katlar"],
                    ["integer", "kat_no", "Kat"],
                    ["end repeat"],
                    ["calculate", "toplam", "Kat sayısı", "", "", "", "", "", "count(${katlar})"]),
                Sheet("choices", ["list_name", "name", "label"],
                    ["hasar", "yok", "Yok"],
                    ["hasar", "agir", "Ağır"]),
                Sheet("settings", ["form_title", "form_id"],
                    ["Bina denetimi", FormId])),
        };

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            Graticula.Testing.OneSuiteAtATime.Enter();
            Assert.False(string.IsNullOrWhiteSpace(Root),
                $"{ConsoleTest.UrlVariable} is not set, so these tests FAIL rather than skip: they fill a form on a running server.");

            (HttpStatusCode added, string addBody) = await RequestAsync(HttpMethod.Post, $"{Root}/admin/forms",
                new StringContent(new JsonObject { ["title"] = "Bina denetimi " + FormId, ["model"] = Model() }.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.True(added == HttpStatusCode.Created, $"Adding the form answered {(int)added}: {addBody}");
            ItemId = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString()!;

            (HttpStatusCode published, string publication) = await RequestAsync(HttpMethod.Post, $"{Root}/admin/forms/{ItemId}/publish",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.True(published == HttpStatusCode.OK, $"Publishing answered {(int)published}: {publication}");
            JsonElement layers = JsonDocument.Parse(publication).RootElement.GetProperty("layers").Clone();
            _top = layers.EnumerateArray().First(l => l.GetProperty("repeat").GetString() is null or "");
            _floors = layers.EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "katlar");
        }

        /// <inheritdoc/>
        public async Task DisposeAsync()
        {
            await RequestAsync(HttpMethod.Delete, $"{Root}/admin/featureservices/{FormId}?folder=hosted&drop=true", null);

            (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{Root}/admin/domains", null);

            if (status == HttpStatusCode.OK)
            {
                foreach (JsonElement domain in Objects(JsonDocument.Parse(body).RootElement)
                    .Where(o => o.TryGetProperty("name", out JsonElement n) && n.GetString()?.StartsWith(FormId, StringComparison.Ordinal) == true
                        && o.TryGetProperty("id", out _)))
                {
                    await RequestAsync(HttpMethod.Delete, $"{Root}/admin/domains/{domain.GetProperty("id").GetString()}", null);
                }
            }

            if (ItemId.Length > 0)
            {
                await RequestAsync(HttpMethod.Delete, $"{Root}/content/documents/{ItemId}", null);
            }
        }

        private static IEnumerable<JsonElement> Objects(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                yield return element;

                foreach (JsonProperty p in element.EnumerateObject())
                {
                    foreach (JsonElement inner in Objects(p.Value))
                    {
                        yield return inner;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in element.EnumerateArray())
                {
                    foreach (JsonElement inner in Objects(e))
                    {
                        yield return inner;
                    }
                }
            }
        }
    }
}
