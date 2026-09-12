using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Values and subtypes on the layer's Fields page — ADR-065.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the page sends is read, not only that it sent something.</b> This harness answers every
/// write with an empty document and records the address, not a JSON body, so these tests wrap
/// <c>fetch</c> once more to keep the body of the PUT. A Values editor that drew every row and
/// posted a list without the value just added is the defect a URL cannot show.
/// </para>
/// <para>
/// <b>The first-run state is its own test, and it provisions nothing first</b> — this console has
/// shipped controls that worked on a populated screen and did not exist on an empty one. A layer
/// that has never had values or subtypes is the state every layer starts in.
/// </para>
/// </remarks>
public sealed class FieldsValuesPageTests : ConsoleTest
{
    private static readonly HttpClient Client = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

    /// <summary>Keeps the body of every write the page sends, beside the harness's own record.</summary>
    private const string KeepBodies = """
        (() => {
          window.__bodies = [];
          const inner = window.fetch;
          window.fetch = (input, init) => {
            const method = ((init && init.method) || "GET").toUpperCase();
            if (method !== "GET" && typeof (init && init.body) === "string") window.__bodies.push(init.body);
            return inner(input, init);
          };
          return true;
        })()
        """;

    [Fact]
    public async Task A_layer_with_no_values_offers_them_and_a_subtype_column()
    {
        (string token, _) = await SignInAsync();
        string name = await ImportAsync(token);

        try
        {
            await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(name)}/fields", token);

            await WaitForAsync(
                "document.querySelectorAll('#fieldsRows [data-field-values]').length > 0",
                "The Fields page drew no Values buttons on a layer that has never had values.");

            Assert.Equal(
                "Any",
                await Browser.EvaluateAsync<string>(ValuesButtonText("material")));

            // Visible, and starting at no subtypes.
            Assert.True(
                await Browser.EvaluateAsync<bool>(Shown("#subtypeField")),
                "The subtype column choice is not on screen for a layer that has whole-number columns.");

            Assert.Equal("", await Browser.EvaluateAsync<string>("document.getElementById('subtypeField').value"));

            // The object id is not offered a list: the database assigns it.
            Assert.False(
                await Browser.EvaluateAsync<bool>("""
                (() => {
                  const row = [...document.querySelectorAll('#fieldsRows tr')]
                    .find(r => r.querySelector('code')?.textContent === 'objectid');
                  return !!row && !!row.querySelector('[data-field-values]');
                })()
                """),
                "The object id offers values. The server refuses a domain on it.");

            // Choosing a column makes one subtype to start from, with its name box on screen.
            await ChooseAsync("subtypeField", "kind");

            await WaitForAsync(Shown("[data-subtype-name=\"0\"]"), "Choosing a subtype column drew no subtype to fill in.");

            Assert.Equal("subtypeField", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
        }
    }

    [Fact]
    public async Task A_list_edited_on_the_page_is_what_Save_sends_with_the_subtypes()
    {
        (string token, _) = await SignInAsync();
        string name = await ImportAsync(token);

        try
        {
            (int set, string said) = await AdminAsync(
                HttpMethod.Put, $"/admin/layers/{name}/fields",
                """
                {"overrides":[{"column":"material","domain":{"type":"codedValue","name":"Material",
                  "codedValues":[{"code":"CU","name":"Copper"},{"code":"PVC","name":"PVC"}]}}],
                 "subtypes":{"field":"kind","defaultCode":1,
                  "types":[{"code":1,"name":"Main"},{"code":2,"name":"Lateral"}]}}
                """);

            Assert.True(set == 200, $"Setting the starting state failed: {set} {said}");

            await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(name)}/fields", token);

            await WaitForAsync(
                "document.querySelectorAll('#fieldsRows [data-field-values]').length > 0",
                "The Fields page drew no Values buttons.");

            Assert.Equal("List of 2", await Browser.EvaluateAsync<string>(ValuesButtonText("material")));

            // A label typed before anything redraws is still there after it — the redraw writes the page
            // from its state, and this page lost typed labels that way before.
            await TypeAsync(Row("label") + " [data-field-alias]", "Name");

            await ClickAsync(Row("material") + " [data-field-values]");

            await WaitForAsync(Shown("#fieldsValues"), "Pressing Values opened no editor.");

            // Under the row that opened it, not under the table.
            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    $"document.querySelector('{Row("material")}').nextElementSibling?.contains(document.getElementById('fieldsValues')) === true"),
                "The Values editor did not open directly under the material row.");

            Assert.Equal(
                "valuesKind",
                await Browser.EvaluateAsync<string>("document.activeElement?.getAttribute('name') || ''"));

            Assert.Equal(2, await Browser.EvaluateAsync<int>("document.querySelectorAll('[data-values-code]').length"));

            await ClickAsync("#valuesAdd");

            await WaitForAsync(
                "document.querySelectorAll('[data-values-code]').length === 3",
                "Add a value drew no third row.");

            Assert.Equal(
                "2",
                await Browser.EvaluateAsync<string>("document.activeElement?.getAttribute('data-values-code') || ''"));

            await TypeAsync("[data-values-code=\"2\"]", "DI");
            await TypeAsync("[data-values-name=\"2\"]", "Ductile iron");

            await ClickAsync("#valuesDone");

            await WaitForAsync("!document.getElementById('fieldsValues')", "Done left the editor open.");

            Assert.Equal("List of 3", await Browser.EvaluateAsync<string>(ValuesButtonText("material")));
            Assert.Equal("Name", await Browser.EvaluateAsync<string>($"document.querySelector('{Row("label")} [data-field-alias]').value"));

            // Focus back on the button that opened it, not dropped to the page.
            Assert.True(
                await Browser.EvaluateAsync<bool>("document.activeElement?.hasAttribute('data-field-values') === true"),
                "Focus did not come back to the Values button after Done.");

            // ---- a subtype's starting value ----
            await ClickAsync("[data-subtype-open=\"0\"]");

            await WaitForAsync(Shown("[data-subtype-starts=\"diameter\"]"), "What differs drew no starting values.");

            await TypeAsync("[data-subtype-starts=\"diameter\"]", "300");

            await Browser.EvaluateAsync<bool>(KeepBodies);
            await ClickAsync("#fieldsSave");

            await WaitForAsync(
                "(window.__bodies || []).length > 0",
                "Pressing Save sent no body. The recorded writes were: " + string.Join(" | ", await WritesAsync()));

            string body = await Browser.EvaluateAsync<string>("window.__bodies[0]") ?? string.Empty;
            JsonElement sent = JsonDocument.Parse(body).RootElement;

            JsonElement material = default;
            JsonElement label = default;

            foreach (JsonElement entry in sent.GetProperty("overrides").EnumerateArray())
            {
                if (entry.GetProperty("column").GetString() == "material") material = entry;
                if (entry.GetProperty("column").GetString() == "label") label = entry;
            }

            Assert.True(material.ValueKind == JsonValueKind.Object, $"Save sent no override for material: {body}");
            Assert.Equal(3, material.GetProperty("domain").GetProperty("codedValues").GetArrayLength());
            Assert.Equal("DI", material.GetProperty("domain").GetProperty("codedValues")[2].GetProperty("code").GetString());
            Assert.Equal("Name", label.GetProperty("alias").GetString());

            JsonElement subtypes = sent.GetProperty("subtypes");

            Assert.Equal("kind", subtypes.GetProperty("field").GetString());
            Assert.Equal(2, subtypes.GetProperty("types").GetArrayLength());
            Assert.Equal(300, subtypes.GetProperty("types")[0].GetProperty("defaultValues").GetProperty("diameter").GetInt32());

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
        }
    }

    private static string Row(string column) => $"#fieldsRows tr[data-column=\"{column}\"]";

    private static string ValuesButtonText(string column) =>
        $"document.querySelector('{Row(column)} [data-field-values]')?.textContent.trim() || ''";

    /// <summary>Types into a box and says so with an <c>input</c> event, as a person typing does.</summary>
    private async Task TypeAsync(string selector, string text)
    {
        string quoted = JsonSerializer.Serialize(selector);
        string value = JsonSerializer.Serialize(text);

        bool typed = await Browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.querySelector({quoted}); if (!e) return false; e.focus(); "
            + $"e.value = {value}; e.dispatchEvent(new Event('input', {{ bubbles: true }})); return true; }})()");

        Assert.True(typed, $"There is no box matching '{selector}' on this screen.");
    }

    /// <summary>Chooses an option and says so with a <c>change</c> event.</summary>
    private async Task ChooseAsync(string id, string value)
    {
        string quotedId = JsonSerializer.Serialize(id);
        string quotedValue = JsonSerializer.Serialize(value);

        bool chosen = await Browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.getElementById({quotedId}); if (!e) return false; e.focus(); "
            + $"e.value = {quotedValue}; e.dispatchEvent(new Event('change', {{ bubbles: true }})); "
            + "return e.value === " + quotedValue + "; })()");

        Assert.True(chosen, $"'{id}' has no option '{value}'.");
    }

    private async Task<string> ImportAsync(string token)
    {
        string name = $"zz_fv_{Guid.NewGuid():N}"[..18];

        const string GeoJson = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[29.0,41.0]},
               "properties":{"label":"a","kind":1,"material":"CU","diameter":110}}
            ]}
            """;

        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(Encoding.UTF8.GetBytes(GeoJson));

        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/geo+json");
        form.Add(bytes, "file", "values.geojson");
        form.Add(new StringContent(name), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/admin/hosted/import"))
        {
            Content = form,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"The import failed: {(int)response.StatusCode} {body}");

        return name;
    }
}
