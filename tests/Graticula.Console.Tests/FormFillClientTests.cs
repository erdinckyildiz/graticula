using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's own behaviour — drafts, guidance hints and pictures, pages, link parameters, the thank-you screen
/// and the map's ground — ADR-287 §5.10 and §5.15, without a Graticula server.
/// </summary>
/// <remarks>
/// <para>
/// <b>A server of its own, in the test.</b> <see cref="FormFillPageTests"/> publishes a real form and sends real rows,
/// and needs a running server for it. What is tested here is the page: it is served from the source tree by an
/// <see cref="HttpListener"/> that answers the handful of addresses the page asks, keeps what the page sends, and can
/// be stopped to make "the signal is gone" true for the service worker as well, which the browser's own offline switch
/// does not do. Chrome is driven with <see cref="DevTools"/>, as the other form tests do.
/// </para>
/// </remarks>
public sealed class FormFillClientTests : IClassFixture<FormFillClientTests.Site>
{
    private readonly Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillClientTests(Site site) => _site = site;

    // ------------------------------------------------------------------------------------------------------- drafts

    [Fact]
    public async Task Answers_are_kept_as_a_draft_as_they_are_entered_and_the_form_opens_on_it_after_a_reload()
    {
        string id = _site.Publish(Form.Basic());
        await _site.OpenAsync(id);

        await _site.SetAsync("ad", "Ayşe");
        await _site.SetAsync("yas", "41");
        await _site.WaitAsync($"{Site.Drafts(id)}.then(function (d) {{ return d.length === 1 && d[0].title === 'Ayşe'; }})", "the draft to be written");

        // The announcement is a live region, and says it once.
        Assert.Equal("status", await _site.EvalAsync<string>("document.getElementById('draft-status').getAttribute('role')"));
        Assert.Equal("polite", await _site.EvalAsync<string>("document.getElementById('draft-status').getAttribute('aria-live')"));
        Assert.Contains("saved", await _site.EvalAsync<string>("document.getElementById('draft-status').textContent"));

        await _site.OpenAsync(id);
        await _site.WaitAsync("document.getElementById('draft-status').textContent.indexOf('reopened') > 0", "the draft to be reopened");
        Assert.Equal("Ayşe", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] input').value"));
        Assert.Equal("41", await _site.EvalAsync<string>("document.querySelector('[data-name=yas] input').value"));
        Assert.False(await _site.EvalAsync<bool>("document.getElementById('draft-bar').hidden"), "The draft's controls are not shown.");
    }

    [Fact]
    public async Task A_drafts_photo_is_kept_as_a_blob_and_comes_back()
    {
        string id = _site.Publish(Form.Basic());
        await _site.OpenAsync(id);

        await _site.EvalAsync<bool>("""
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
        await _site.WaitAsync($$"""
            {{Site.IdbAll("drafts")}}.then(function (all) {
              var d = all.filter(function (x) { return x.formId === '{{id}}'; })[0];
              var f = d && d.tree.files.foto && d.tree.files.foto[0];
              return !!f && f.blob instanceof Blob && f.blob.size > 0;
            })
            """, "the photo to be stored in the draft as a Blob");

        await _site.OpenAsync(id);
        await _site.WaitAsync("document.querySelectorAll('[data-name=foto] .files li').length === 1", "the photo to be listed again");
        Assert.Contains("foto.png", await _site.EvalAsync<string>("document.querySelector('[data-name=foto] .files li').textContent"));
    }

    [Fact]
    public async Task The_drafts_panel_lists_resumes_and_discards_and_keeps_focus_somewhere_that_exists()
    {
        string id = _site.Publish(Form.Basic());
        await _site.OpenAsync(id);
        await _site.SetAsync("ad", "Birinci");
        await _site.WaitAsync($"{Site.Drafts(id)}.then(function (d) {{ return d.length === 1; }})", "the first draft");

        // A second form, started from the draft bar, leaves the first in the list.
        await _site.EvalAsync<bool>("document.getElementById('draft-new').click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=ad] input').value === ''", "a blank form");
        await _site.SetAsync("ad", "İkinci");
        await _site.WaitAsync($"{Site.Drafts(id)}.then(function (d) {{ return d.length === 2; }})", "the second draft");

        await _site.EvalAsync<bool>("document.getElementById('drafts-toggle').click(); true");
        Assert.Equal("true", await _site.EvalAsync<string>("document.getElementById('drafts-toggle').getAttribute('aria-expanded')"));
        await _site.WaitAsync("document.querySelectorAll('#drafts-list li strong').length === 2", "both drafts to be listed");
        string[] titles = await _site.EvalAsync<string[]>("Array.prototype.map.call(document.querySelectorAll('#drafts-list li strong'), function (n) { return n.textContent; })") ?? [];
        Assert.Equal(["İkinci", "Birinci"], titles);
        // Every control is a button with a name of its own.
        Assert.Equal(0, await _site.EvalAsync<int>("Array.prototype.filter.call(document.querySelectorAll('#drafts-list button'), function (b) { return !b.getAttribute('aria-label'); }).length"));

        // Resume the older one.
        await _site.EvalAsync<bool>("document.querySelector('#drafts-list li:nth-child(2) button').click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=ad] input').value === 'Birinci'", "the first draft to be resumed");
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('drafts').hidden"), "The panel stayed open over the form.");
        Assert.Contains("resumed", await _site.EvalAsync<string>("document.getElementById('draft-status').textContent"));
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('form').contains(document.activeElement)"), "Focus was left outside the form after resuming.");

        // Discard the other one, from the panel, with the browser's confirmation.
        await _site.EvalAsync<bool>("window.confirm = function () { return true; }; document.getElementById('drafts-toggle').click(); true");
        await _site.EvalAsync<bool>("""
            (function () {
              var items = document.querySelectorAll('#drafts-list li');
              var other = Array.prototype.filter.call(items, function (li) { return li.querySelector('strong').textContent === 'İkinci'; })[0];
              other.querySelector('button.danger').click();
              return true;
            })()
            """);
        await _site.WaitAsync($"{Site.Drafts(id)}.then(function (d) {{ return d.length === 1 && d[0].title === 'Birinci'; }})", "the second draft to be discarded");
        Assert.True(await _site.EvalAsync<bool>("document.getElementById('drafts').contains(document.activeElement)"), "Focus fell to the page after a discard.");
        Assert.Equal("Birinci", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] input').value"));
    }

    [Fact]
    public async Task A_draft_leaves_when_its_record_is_queued_in_the_outbox()
    {
        string id = _site.Publish(Form.Basic());
        await _site.OpenAsync(id);
        await _site.SetAsync("ad", "Gidecek");
        await _site.WaitAsync($"{Site.Drafts(id)}.then(function (d) {{ return d.length === 1; }})", "the draft");

        await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
        await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
        Assert.Equal(0, await _site.EvalAsync<int>($"{Site.Drafts(id)}.then(function (d) {{ return d.length; }})"));
        Assert.Equal("0", await _site.EvalAsync<string>("document.getElementById('drafts-count').textContent"));
        Assert.Contains(_site.Adds, a => a.Contains("Gidecek", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_draft_survives_an_offline_reload()
    {
        string id = _site.Publish(Form.Basic());
        await _site.OpenAsync(id);
        await _site.WaitAsync("!!navigator.serviceWorker.controller", "the service worker to take the page", seconds: 20);

        // The worker keeps a form's JSON from the loads it sees; this load is the first one it has.
        await _site.OpenAsync(id);
        await _site.SetAsync("ad", "Sahada");
        await _site.WaitAsync($"{Site.Drafts(id)}.then(function (d) {{ return d.length === 1; }})", "the draft");
        await _site.WaitAsync($"{Site.IdbAll("forms")}.then(function (f) {{ return f.some(function (x) {{ return x.id === '{id}'; }}); }})", "the form to be kept on the device");

        _site.Stop();
        try
        {
            await _site.OpenAsync(id, expectServer: false);
            await _site.WaitAsync("document.getElementById('draft-status').textContent.indexOf('reopened') > 0", "the draft to be reopened with no server", seconds: 20);
            Assert.Equal("Sahada", await _site.EvalAsync<string>("document.querySelector('[data-name=ad] input').value"));
        }
        finally
        {
            _site.Start();
        }
    }

    // ------------------------------------------------------------------------------------------------------ the site

    /// <summary>A compiled form, as <c>form.json</c> sends it, and the publication that says where its rows go.</summary>
    public sealed class Form
    {
        public JsonObject Root { get; init; } = new();

        public JsonObject Choices { get; init; } = new();

        public bool Pages { get; init; }

        public string Settings { get; init; } = "{}";

        public Dictionary<string, byte[]> Media { get; init; } = [];

        /// <summary>A text, a number and a photo.</summary>
        public static Form Basic() => new()
        {
            Root = Repeat("", [
                Question("ad", "text", "Ad", required: "yes"),
                Question("yas", "integer", "Yaş"),
                Question("foto", "image", "Fotoğraf"),
            ]),
        };

        public static JsonObject Repeat(string name, IEnumerable<JsonNode> body) => new()
        {
            ["kind"] = "repeat",
            ["name"] = name,
            ["labels"] = new JsonObject(),
            ["appearance"] = new JsonArray(),
            ["body"] = new JsonArray([.. body]),
        };

        public static JsonObject Group(string name, string label, IEnumerable<JsonNode> body, params string[] appearance) => new()
        {
            ["kind"] = "group",
            ["name"] = name,
            ["labels"] = new JsonObject { [""] = label },
            ["appearance"] = new JsonArray([.. appearance.Select(a => (JsonNode)a)]),
            ["body"] = new JsonArray([.. body]),
        };

        public static JsonObject Question(string name, string type, string label, string? required = null, string? list = null,
            string? hint = null, string? guidance = null, string? image = null, params string[] appearance)
        {
            JsonObject q = new()
            {
                ["kind"] = "question",
                ["name"] = name,
                ["type"] = type,
                ["list"] = list,
                ["labels"] = new JsonObject { [""] = label },
                ["hints"] = hint is null ? new JsonObject() : new JsonObject { [""] = hint },
                ["guidanceHints"] = guidance is null ? new JsonObject() : new JsonObject { [""] = guidance },
                ["images"] = image is null ? new JsonObject() : new JsonObject { [""] = image },
                ["required"] = required,
                ["appearance"] = new JsonArray([.. appearance.Select(a => (JsonNode)a)]),
                ["parameters"] = new JsonObject(),
            };
            return q;
        }

        public static JsonObject Choice(string name, string label, string? image = null) => new()
        {
            ["name"] = name,
            ["labels"] = new JsonObject { [""] = label },
            ["images"] = image is null ? new JsonObject() : new JsonObject { [""] = image },
            ["filters"] = new JsonObject(),
        };

        public JsonObject Document(string id)
        {
            JsonObject settings = JsonNode.Parse(Settings)!.AsObject();
            JsonObject form = new()
            {
                ["format"] = "graticula-form/1",
                ["title"] = "Deneme",
                ["formId"] = "deneme",
                ["version"] = "1",
                ["defaultLanguage"] = null,
                ["languages"] = new JsonArray(),
                ["pages"] = Pages,
                ["instanceName"] = null,
                ["root"] = Root.DeepClone(),
                ["choices"] = Choices.DeepClone(),
            };

            foreach ((string key, JsonNode? value) in settings)
            {
                form[key] = value?.DeepClone();
            }

            string[] attachments = [.. Walk(Root).Where(q => (string?)q["type"] is "image" or "audio" or "video" or "file").Select(q => (string)q["name"]!)];
            JsonObject columns = new(Walk(Root).Where(q => (string?)q["type"] is not ("image" or "audio" or "video" or "file" or "note"))
                .Select(q => KeyValuePair.Create((string)q["name"]!, (JsonNode?)JsonValue.Create((string)q["name"]!))));

            return new JsonObject
            {
                ["id"] = id,
                ["title"] = "Deneme",
                ["form"] = form,
                ["publication"] = new JsonObject
                {
                    ["featureServer"] = "/fs",
                    ["layers"] = new JsonArray(new JsonObject
                    {
                        ["repeat"] = "",
                        ["url"] = "/fs/0",
                        ["submitUrl"] = "/fs/0",
                        ["columns"] = columns,
                        ["attachments"] = new JsonArray([.. attachments.Select(a => (JsonNode)a)]),
                    }),
                },
            };
        }

        private static IEnumerable<JsonObject> Walk(JsonObject node)
        {
            foreach (JsonNode? child in node["body"]!.AsArray())
            {
                JsonObject c = child!.AsObject();

                if ((string?)c["kind"] == "question")
                {
                    yield return c;
                }
                else
                {
                    foreach (JsonObject inner in Walk(c))
                    {
                        yield return inner;
                    }
                }
            }
        }
    }

    /// <summary>The stand-in server and Chrome, shared by the class.</summary>
    public sealed class Site : IAsyncLifetime, IDisposable
    {
        private readonly Dictionary<string, Form> _forms = [];
        private readonly List<string> _adds = [];
        private readonly object _gate = new();
        private HttpListener? _listener;
        private int _port;
        private DevTools? _browser;

        /// <summary>The rows the page sent, as the <c>adds</c> JSON it posted.</summary>
        public IReadOnlyList<string> Adds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _adds];
                }
            }
        }

        /// <summary>Where the stand-in server listens, without a trailing slash.</summary>
        public string Address => $"http://127.0.0.1:{_port}";

        /// <summary>The tab.</summary>
        public DevTools Browser => _browser ?? throw new InvalidOperationException("The browser is not open yet.");

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Start();
            _browser = await DevTools.LaunchAsync();
        }

        /// <summary>Stops the listener.</summary>
        public void Dispose() => Stop();

        /// <inheritdoc/>
        public async Task DisposeAsync()
        {
            Stop();

            if (_browser is not null)
            {
                await _browser.DisposeAsync();
            }
        }

        /// <summary>Makes a form available and gives its item id.</summary>
        /// <param name="form">The form.</param>
        /// <returns>The id.</returns>
        public string Publish(Form form)
        {
            string id = Guid.NewGuid().ToString("N");

            lock (_gate)
            {
                _forms[id] = form;
            }

            return id;
        }

        /// <summary>Starts answering.</summary>
        public void Start()
        {
            if (_listener is { IsListening: true })
            {
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            HttpListener listener = _listener;
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;

                    try
                    {
                        context = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    _ = Task.Run(() => Answer(context));
                }
            });
        }

        /// <summary>Stops answering: from here on the page has no server.</summary>
        public void Stop()
        {
            try
            {
                _listener?.Abort();
            }
            catch (ObjectDisposedException)
            {
                // already gone
            }

            _listener = null;
        }

        /// <summary>Opens the form's page and waits for it to be drawn.</summary>
        /// <param name="id">The item id.</param>
        /// <param name="query">The address's query string, with its question mark.</param>
        /// <param name="expectServer">False when the server is stopped and the page must come from the device.</param>
        public async Task OpenAsync(string id, string query = "", bool expectServer = true)
        {
            _ = expectServer;
            await Browser.NavigateAsync("about:blank");
            await Browser.NavigateAsync($"{Address}/forms/{id}{query}");
            await WaitAsync("!document.getElementById('form').hidden || !document.getElementById('loading').hidden && document.getElementById('loading').textContent.length > 0 && document.getElementById('loading').textContent !== 'Loading the form…'",
                "the form to be drawn", seconds: 20);
        }

        /// <summary>Evaluates an expression in the page.</summary>
        /// <typeparam name="T">What it yields.</typeparam>
        /// <param name="expression">JavaScript.</param>
        public Task<T?> EvalAsync<T>(string expression) => Browser.EvaluateAsync<T>(expression);

        /// <summary>Types into a question's input, as a person would.</summary>
        /// <param name="name">The question's name.</param>
        /// <param name="value">The text.</param>
        public async Task SetAsync(string name, string value) =>
            await EvalAsync<bool>($$"""
                (function () {
                  var input = document.querySelector('[data-name={{name}}] input, [data-name={{name}}] textarea');
                  input.focus();
                  input.value = {{JsonSerializer.Serialize(value)}};
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                  return true;
                })()
                """);

        /// <summary>Waits until an expression is true in the page.</summary>
        /// <param name="expression">JavaScript yielding a boolean or a promise of one.</param>
        /// <param name="what">What is waited for, for the failure.</param>
        /// <param name="seconds">How long.</param>
        public async Task WaitAsync(string expression, string what, int seconds = 10)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            string last = string.Empty;

            while (DateTime.UtcNow < until)
            {
                try
                {
                    if (await EvalAsync<bool>(expression))
                    {
                        return;
                    }
                }
                catch (InvalidOperationException e)
                {
                    last = e.Message;
                }

                await Task.Delay(100);
            }

            string text = string.Empty;

            try
            {
                text = await EvalAsync<string>("document.body ? document.body.innerText.slice(0, 600) : ''") ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                // the page is gone
            }

            Assert.Fail($"Waited {seconds} seconds for {what}. {last} The page said: {text}");
        }

        /// <summary>All records of an IndexedDB store, read in the page as a promise.</summary>
        /// <param name="store">The store.</param>
        public static string IdbAll(string store) => $$"""
            new Promise(function (resolve) {
              var open = indexedDB.open('graticula-forms');
              open.onsuccess = function () {
                var all = open.result.transaction('{{store}}').objectStore('{{store}}').getAll();
                all.onsuccess = function () { resolve(all.result); };
              };
            })
            """;

        /// <summary>The drafts of one form, as a promise of an array.</summary>
        /// <param name="id">The form's item id.</param>
        public static string Drafts(string id) => $"{IdbAll("drafts")}.then(function (all) {{ return all.filter(function (d) {{ return d.formId === '{id}'; }}).sort(function (a, b) {{ return b.updated - a.updated; }}); }})";

        private void Answer(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url!.AbsolutePath;
                string method = context.Request.HttpMethod;

                if (method == "GET" && TryStatic(path, out string? file, out string? type))
                {
                    Send(context, 200, type!, File.ReadAllBytes(file!));
                    return;
                }

                if (method == "GET" && path.StartsWith("/forms/", StringComparison.Ordinal) && path.Length == 7 + 32 && !path.Contains('.', StringComparison.Ordinal))
                {
                    Send(context, 200, "text/html; charset=utf-8", File.ReadAllBytes(Path.Combine(FormsFolder(), "fill.html")));
                    return;
                }

                if (method == "GET" && path.EndsWith("/form.json", StringComparison.Ordinal))
                {
                    string id = path.Split('/')[2];

                    Form? form;

                    lock (_gate)
                    {
                        _forms.TryGetValue(id, out form);
                    }

                    Send(context, form is null ? 404 : 200, "application/json", Encoding.UTF8.GetBytes(form is null
                        ? """{"error":{"code":404,"message":"No such form."}}"""
                        : form.Document(id).ToJsonString()));
                    return;
                }

                if (path == "/rest/whoami")
                {
                    Send(context, 200, "application/json", """{"authenticated":false}"""u8.ToArray());
                    return;
                }

                if (path == "/rest/auth/session")
                {
                    Send(context, 401, "application/json", """{"error":{"code":401,"message":"Not signed in."}}"""u8.ToArray());
                    return;
                }

                if (method == "POST" && path.EndsWith("/applyEdits", StringComparison.Ordinal))
                {
                    using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
                    string body = reader.ReadToEnd();
                    string adds = Uri.UnescapeDataString(body.Split('&').FirstOrDefault(p => p.StartsWith("adds=", StringComparison.Ordinal))?[5..].Replace('+', ' ') ?? "[]");

                    lock (_gate)
                    {
                        _adds.Add(adds);
                    }

                    int count = JsonNode.Parse(adds)!.AsArray().Count;
                    Send(context, 200, "application/json", Encoding.UTF8.GetBytes(new JsonObject
                    {
                        ["addResults"] = new JsonArray([.. Enumerable.Range(1, count).Select(i => (JsonNode)new JsonObject { ["success"] = true, ["objectId"] = i })]),
                    }.ToJsonString()));
                    return;
                }

                if (method == "POST" && path.EndsWith("/addAttachment", StringComparison.Ordinal))
                {
                    Send(context, 200, "application/json", """{"addAttachmentResult":{"success":true}}"""u8.ToArray());
                    return;
                }

                if (path.Contains("/resources/", StringComparison.Ordinal) && path.StartsWith("/sharing/rest/content/items/", StringComparison.Ordinal))
                {
                    string[] parts = path.Split('/');
                    string id = parts[5];
                    string name = Uri.UnescapeDataString(string.Join('/', parts[7..]));

                    Form? form;

                    lock (_gate)
                    {
                        _forms.TryGetValue(id, out form);
                    }

                    if (form is not null && form.Media.TryGetValue(name, out byte[]? bytes))
                    {
                        Send(context, 200, name.EndsWith(".svg", StringComparison.Ordinal) ? "image/svg+xml" : "application/octet-stream", bytes);
                        return;
                    }

                    Send(context, 404, "application/json", """{"error":{"code":404,"message":"No such resource."}}"""u8.ToArray());
                    return;
                }

                Send(context, 404, "text/plain", "Not found."u8.ToArray());
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
            {
                // the page went away
            }
        }

        private static void Send(HttpListenerContext context, int status, string type, byte[] body)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body);
            context.Response.Close();
        }

        private static bool TryStatic(string path, out string? file, out string? type)
        {
            file = null;
            type = null;

            if (!path.StartsWith("/forms/", StringComparison.Ordinal) || path.Length <= 7)
            {
                return false;
            }

            string name = path[7..];

            if (name.Contains('/', StringComparison.Ordinal))
            {
                return false;
            }

            string? found = name switch
            {
                "ol.js" or "ol.css" => Path.Combine(FormsFolder(), "..", name),
                "fill.html" or "fill.js" or "fill.css" or "xlsform-eval.js" or "sw.js" or "manifest.webmanifest" or "icon.svg" => Path.Combine(FormsFolder(), name),
                _ => null,
            };

            if (found is null || !File.Exists(found))
            {
                return false;
            }

            file = found;
            type = Path.GetExtension(name) switch
            {
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".html" => "text/html; charset=utf-8",
                ".svg" => "image/svg+xml",
                _ => "application/manifest+json",
            };

            return true;
        }

        private static string FormsFolder()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."),
                "src", "Graticula.Host", "wwwroot", "forms");
        }
    }
}
