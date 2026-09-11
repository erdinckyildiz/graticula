using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The layer's Fields page — ADR-063: a label per column, and a column hidden from every client.
/// </summary>
/// <remarks>
/// <para>
/// <b>The page is driven against a layer this class imports and deletes.</b> The state it opens
/// in is set through the real API first — one column hidden, one labelled — because this
/// harness answers every write the page makes with an empty document, so a test that pressed
/// Save and then read the page back would be asserting the harness.
/// </para>
/// <para>
/// <b>What is asserted is what a reader needs</b>: the hidden column is listed here and ticked,
/// because this is the one screen that shows a hidden column at all; the object id cannot be
/// ticked, because the server would refuse it; and Save goes to the address and verb the API
/// takes, which is the half of a button this harness can see.
/// </para>
/// </remarks>
public sealed class FieldsPageTests : ConsoleTest
{
    // The base class keeps its client to itself; the import is multipart, which `AdminAsync`
    // does not send, so this class has its own — trusting the fixture's self-signed
    // certificate for the same reason the base class does.
    private static readonly HttpClient Client = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

    [Fact]
    public async Task The_fields_page_shows_the_hidden_column_and_saves_to_the_layer()
    {
        (string token, _) = await SignInAsync();
        string name = await ImportAsync(token);

        try
        {
            (int set, string said) = await AdminAsync(
                HttpMethod.Put, $"/admin/layers/{name}/fields",
                "{\"overrides\":[{\"column\":\"secret\",\"hidden\":true},"
                + "{\"column\":\"pop\",\"alias\":\"Population\"}]}");

            Assert.True(set == 200, $"Setting the starting state failed: {set} {said}");

            await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(name)}/fields", token);

            await WaitForAsync(
                "document.querySelectorAll('#fieldsRows [data-field-alias]').length > 0",
                "The Fields page drew no columns.");

            // <b>Visible, not merely present</b> — this console has shipped controls that
            // existed and rendered nowhere.
            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "!!document.getElementById('fieldsSave')?.offsetParent"),
                "The Save button is in the document and renders nowhere.");

            Assert.True(
                await Browser.EvaluateAsync<bool>("""
                (() => {
                  const rows = [...document.querySelectorAll('#fieldsRows tr')];
                  const row = rows.find(r => r.textContent.includes('secret'));
                  return !!row && row.querySelector('[data-field-hidden]').checked;
                })()
                """),
                "The hidden column is not listed as hidden. This is the only screen that shows a "
                + "hidden column at all, so if it is missing here there is nowhere to unhide it.");

            Assert.Equal(
                "Population",
                await Browser.EvaluateAsync<string>("""
                (() => {
                  const rows = [...document.querySelectorAll('#fieldsRows tr')];
                  const row = rows.find(r => r.textContent.includes('pop'));
                  return row ? row.querySelector('[data-field-alias]').value : '';
                })()
                """));

            Assert.True(
                await Browser.EvaluateAsync<bool>("""
                (() => {
                  const rows = [...document.querySelectorAll('#fieldsRows tr')];
                  const row = rows.find(r => r.querySelector('code')?.textContent === 'objectid');
                  return !!row && row.querySelector('[data-field-hidden]').disabled;
                })()
                """),
                "The object id can be ticked as hidden. The server refuses that, so offering it "
                + "is offering an error.");

            // The result of an asynchronous save is announced, or a screen reader hears nothing.
            Assert.Equal(
                "polite",
                await Browser.EvaluateAsync<string>(
                    "document.getElementById('fieldsSays').getAttribute('aria-live')"));

            await Browser.EvaluateAsync<bool>("(window.__writes = [], true)");
            await ClickAsync("#fieldsSave");

            await WaitForAsync(
                "(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/admin/layers/"
                + name + "/fields'))",
                "Pressing Save sent no PUT to this layer's fields. The recorded writes were: "
                + string.Join(" | ", await WritesAsync()));

            // After a save the rows are still there — the harness answers with an empty document,
            // and a page that took that as "the table has no columns" would blank itself.
            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "document.querySelectorAll('#fieldsRows [data-field-alias]').length > 0"),
                "The page emptied itself after saving.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(
                HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
        }
    }

    private async Task<string> ImportAsync(string token)
    {
        string name = $"zz_fp_{Guid.NewGuid():N}"[..18];

        const string GeoJson = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[29.0,41.0]},
               "properties":{"label":"a","secret":"s1","pop":10}}
            ]}
            """;

        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(Encoding.UTF8.GetBytes(GeoJson));

        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/geo+json");
        form.Add(bytes, "file", "fields.geojson");
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
