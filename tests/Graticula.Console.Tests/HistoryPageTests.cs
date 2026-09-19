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

            Assert.Equal(
                "Turn history off",
                await Browser.EvaluateAsync<string>("document.getElementById('historySwitch').textContent.trim()"));

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

    [Fact]
    public async Task A_layer_without_history_says_so_and_offers_to_turn_it_on()
    {
        (_, string layer) = Editable();
        (string token, _) = await SignInAsync();

        // The first-run state: nothing kept, nothing to list.
        await AdminAsync(HttpMethod.Post, $"/admin/layers/{Uri.EscapeDataString(layer)}/history", "{\"enabled\":false}");

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/history", token);

        await WaitForAsync(
            "document.getElementById('historySwitch')?.offsetParent",
            "A layer without history showed no visible way to turn it on.");

        Assert.Equal(
            "Turn history on",
            await Browser.EvaluateAsync<string>("document.getElementById('historySwitch').textContent.trim()"));

        Assert.StartsWith(
            "History is off.",
            await Browser.EvaluateAsync<string>("document.getElementById('historySays').textContent.trim()"),
            StringComparison.Ordinal);

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
