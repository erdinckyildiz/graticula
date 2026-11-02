using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A service an ArcGIS client created empty — ADR-257 §11, owner decision 2026-11-02 — as Studio and Server draw it
/// before its <c>addToDefinition</c> arrives.
/// </summary>
/// <remarks>
/// <b>Made the way a client makes it</b>: the portal's <c>createService</c> with no layers, which is what the ArcGIS API
/// for Python's <c>create_service</c> and <c>create_view</c> send first. Each check is that the control a reader needs is
/// on screen — drawn and visible — not that it exists in the document, because this repository has shipped three
/// controls that existed and could not be seen.
/// </remarks>
public sealed class EmptyServicePageTests : ConsoleTest
{
    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    private async Task<string> CreateEmptyAsync(string token, string name)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{Root}/sharing/rest/content/users/{User}/createService")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["createParameters"] = JsonSerializer.Serialize(new { name, capabilities = "Query" }),
                ["outputType"] = "featureService",
                ["f"] = "json",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_service_created_empty_says_so_on_its_page_and_can_be_found_and_deleted_in_Server()
    {
        (string token, _) = await SignInAsync();
        string name = $"zz_emptypage_{Guid.NewGuid():N}"[..24];

        string made = await CreateEmptyAsync(token, name);
        Assert.Contains("\"success\":true", made, StringComparison.Ordinal);

        try
        {
            // Studio's page for it: the Layers card is drawn and its one row says why it is empty, visibly.
            await OpenAsync($"/studio/#/service/hosted/{name}", token);
            await WaitForAsync(
                Shown("#serviceLayerRows td[data-empty-service]"),
                "The service page of an empty service does not show the sentence saying it holds no layers yet.");

            string said = await Browser.EvaluateAsync<string>(
                "document.querySelector('#serviceLayerRows td[data-empty-service]').innerText") ?? string.Empty;
            Assert.Contains("addToDefinition", said, StringComparison.Ordinal);
            NothingWentWrong(await PageErrorsAsync());

            // Server's services list: filtered to it, the row is there and its Delete is on screen.
            await OpenAsync(ServicesIn("hosted"), token);
            await WaitForAsync("!!document.getElementById('serviceFilter') && document.querySelectorAll('#services tr').length > 0",
                "Server's services list did not draw.");
            await Browser.EvaluateAsync<object>(
                "(() => { const f = document.getElementById('serviceFilter');"
                + $" f.value = {JsonSerializer.Serialize(name)};"
                + " f.dispatchEvent(new Event('input', { bubbles: true })); })()");
            await WaitForAsync(
                Shown($"button[data-service-delete=\"{name}\"]"),
                "Server's services list does not show the empty service with its Delete.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{Uri.EscapeDataString(name)}?folder=hosted");
        }
    }
}
