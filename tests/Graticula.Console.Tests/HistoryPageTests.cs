using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A layer's History page in Studio — ADR-078.
/// </summary>
/// <remarks>
/// <para>
/// <b>The history is made by the server, then read by the page.</b> The harness stops the page's own
/// writes from reaching the server, so a page that turned history on and then read it back would be
/// reading nothing. The test turns it on and edits through the API as the administrator, and the page
/// is asked only to show what is there and to send the right request when a version is put back.
/// </para>
/// <para>
/// <b>Visible, not merely present.</b> Three times in this console a control existed and nobody could
/// see it; every control asserted here is asserted with a layout box.
/// </para>
/// </remarks>
public sealed class HistoryPageTests : ConsoleTest
{
    private const string EditableVariable = "GRATICULA_TEST_EDITABLE";

    [Fact]
    public async Task A_change_opens_its_features_versions_and_an_older_one_can_be_put_back()
    {
        (string service, string layer) = Editable();
        (string token, _) = await SignInAsync();

        (int status, string body) = await AdminAsync(
            HttpMethod.Post, $"/admin/layers/{Uri.EscapeDataString(layer)}/history", "{\"enabled\":true}");
        Assert.True(status == 200, $"History could not be turned on: {status} {body}");

        try
        {
            long objectId = await ChangeOneFeatureAsync(service, token);

            await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/history", token);

            await WaitForAsync(
                $"document.querySelector('#historyBody tr[data-hist-feature=\"{objectId}\"]')?.offsetParent",
                "The change made through applyEdits never appeared, visibly, in the history list.");

            await ClickAsync($"#historyBody tr[data-hist-feature=\"{objectId}\"]");

            await WaitForAsync(
                "document.querySelector('#historyFeature [data-hist-restore]')?.offsetParent",
                "Choosing the change did not show the feature's versions with a visible way to put one back.");

            Assert.StartsWith(
                "History is on.",
                await Browser.EvaluateAsync<string>("document.getElementById('historySays').textContent.trim()"),
                StringComparison.Ordinal);

            await ClickAsync("#historyFeature [data-hist-restore]");

            await WaitForAsync(
                "window.__writes.some(w => w.includes('/history/') && w.endsWith('/restore'))",
                "Putting a version back sent no restore request.");

            string[] writes = await WritesAsync();
            Assert.Contains(writes, w => w.StartsWith("POST ", StringComparison.Ordinal)
                && w.Contains($"/admin/layers/{Uri.EscapeDataString(layer)}/history/{objectId}/restore", StringComparison.Ordinal));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(
                HttpMethod.Post, $"/admin/layers/{Uri.EscapeDataString(layer)}/history", "{\"enabled\":false}");
        }
    }

    /// <summary>
    /// A layer without history says so in Data › History and sends the reader to Settings › Feature layer, where the
    /// switch is — ADR-102 §5.4, moved there on 2026-10-01 with editor tracking beside it.
    /// </summary>
    [Fact]
    public async Task A_layer_without_history_says_so_and_Settings_turns_it_on()
    {
        (string service, string layer) = Editable();
        (string token, _) = await SignInAsync();

        // The first-run state: nothing kept, nothing to list.
        await AdminAsync(HttpMethod.Post, $"/admin/layers/{Uri.EscapeDataString(layer)}/history", "{\"enabled\":false}");

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/history", token);

        await WaitForAsync(
            "document.querySelector('#historySays a[href*=\"section=feature\"]')?.offsetParent",
            "A layer without history did not say where to turn it on.");

        Assert.StartsWith(
            "History is off.",
            await Browser.EvaluateAsync<string>("document.getElementById('historySays').textContent.trim()"),
            StringComparison.Ordinal);

        Assert.True(await Browser.EvaluateAsync<bool>("!document.getElementById('historySwitch')?.offsetParent"),
            "Data › History still carries its own switch beside the link to Settings.");

        await OpenAsync($"/studio/#/service/{service}?tab=settings&section=feature", token);

        string toggle = $"#featureLayers [data-history-toggle=\"{layer}\"]";

        await WaitForAsync(
            $"document.querySelector('{toggle}')?.offsetParent && document.querySelector('{toggle}').textContent.trim() === 'Turn on'",
            "Settings › Feature layer has no visible Turn on for the layer's history.");

        await ClickAsync(toggle);

        await WaitForAsync(
            $"window.__writes.some(w => w.startsWith('POST') && w.includes('/admin/layers/{Uri.EscapeDataString(layer)}/history'))",
            "Turn on sent no request to the layer's history.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Recording who edits is a one-way action in Settings › Feature layer, and Data › Fields points there.</summary>
    [Fact]
    public async Task Recording_who_edits_is_started_from_Settings()
    {
        (string service, string layer) = Editable();
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/service/{service}?tab=settings&section=feature", token);

        string state = $"#featureLayers [data-tracking-state=\"{layer}\"]";

        await WaitForAsync(
            $"!/reading/.test(document.querySelector('{state}')?.textContent || 'reading')",
            "Settings › Feature layer never said whether the layer records who edits.");

        bool tracked = await Browser.EvaluateAsync<bool>($"/^Recorded/.test(document.querySelector('{state}').textContent)");

        if (!tracked)
        {
            await ClickAsync($"#featureLayers [data-track-edits=\"{layer}\"]");

            await WaitForAsync(
                "window.__writes.some(w => w.startsWith('POST') && w.includes('/editor-tracking'))",
                "Start recording sent no request.");
        }

        Assert.True(await Browser.EvaluateAsync<bool>("!document.getElementById('fieldsTrack')"),
            "Data › Fields still carries its own tracking button.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>The seeded editable layer, as its service address and its layer name.</summary>
    private static (string Service, string Layer) Editable()
    {
        string? named = Environment.GetEnvironmentVariable(EditableVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(named),
            $"{EditableVariable} is not set, so this test FAILS rather than skips.");

        return (named!, named!.Split('/')[^1]);
    }

    /// <summary>Changes one attribute of the layer's first feature through applyEdits, and says which.</summary>
    private async Task<long> ChangeOneFeatureAsync(string service, string token)
    {
        string layerUrl = $"{Root}/rest/services/{service}/FeatureServer/0";

        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using JsonDocument page = JsonDocument.Parse(await http.GetStringAsync(
            $"{layerUrl}/query?where=1%3D1&outFields=*&returnGeometry=false&resultRecordCount=1&f=json"));

        string idField = page.RootElement.GetProperty("objectIdFieldName").GetString()!;
        JsonElement attributes = page.RootElement.GetProperty("features")[0].GetProperty("attributes");
        long objectId = attributes.GetProperty(idField).GetInt64();

        string textField = attributes.EnumerateObject()
            .First(p => p.Value.ValueKind == JsonValueKind.String
                && !string.Equals(p.Name, idField, StringComparison.Ordinal)
                && !p.Name.Contains("globalid", StringComparison.OrdinalIgnoreCase))
            .Name;

        // <b>A value no earlier run wrote.</b> An update that changes nothing is not a change, and the
        // trigger records none — which the first version of this test learned on its second run.
        string updates = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["attributes"] = new Dictionary<string, object> { [idField] = objectId, [textField] = "history page test " + Guid.NewGuid().ToString("n")[..8] },
            },
        });

        using FormUrlEncodedContent form = new(
            [new("updates", updates), new("f", "json")]);

        using HttpResponseMessage response = await http.PostAsync($"{layerUrl}/applyEdits", form);
        string answer = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode && answer.Contains("\"success\":true", StringComparison.Ordinal),
            $"The edit the page is meant to show was refused: {answer}");

        return objectId;
    }
}
