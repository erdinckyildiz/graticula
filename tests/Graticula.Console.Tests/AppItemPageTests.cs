using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-264: an app or a link is an item with a page of its own. A Feature Service registered by another server's address
/// opened as one of this server's services, looked for a service named by the address's last folder, and said none
/// was visible — the route read every <c>/rest/services/…/FeatureServer</c> address as its own.
/// </summary>
public sealed class AppItemPageTests : ConsoleTest
{
    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set.");

    private async Task<string> AddAsync(string token, Dictionary<string, string> form)
    {
        form["f"] = "json";
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/sharing/rest/content/users/{User}/addItem"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(JsonDocument.Parse(body).RootElement.TryGetProperty("id", out JsonElement id), body);
        return id.GetString()!;
    }

    [Fact]
    public async Task A_service_elsewhere_opens_as_a_link_and_an_app_as_an_app()
    {
        (string token, string cookie) = await SignInAsync();
        string tag = Guid.NewGuid().ToString("N")[..8];
        const string Address = "https://services.example.com/arcgis/rest/services/Roads/FeatureServer";
        string link = await AddAsync(token, new() { ["type"] = "Feature Service", ["title"] = $"zz roads {tag}", ["url"] = Address });
        string app = await AddAsync(token, new() { ["type"] = "Dashboard", ["title"] = $"zz dashboard {tag}", ["text"] = "{}" });

        try
        {
            await OpenAsync($"/studio/#/item/{link}", token, cookie);
            await WaitForAsync("document.getElementById('view-document')?.classList.contains('on')"
                + $" && document.getElementById('docOpenLink')?.getAttribute('href') === '{Address}'",
                "A service registered by another server's address did not open as a link with its address.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('docReplace')"),
                "A link offered Replace file, though it has no file.");

            await OpenAsync($"/studio/#/item/{app}", token, cookie);
            await WaitForAsync("document.getElementById('view-document')?.classList.contains('on')"
                + " && /APP/.test(document.getElementById('docThumb')?.textContent || '')",
                "A Dashboard did not open as an app.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            foreach (string id in new[] { link, app })
            {
                using HttpRequestMessage delete = new(HttpMethod.Post, new Uri($"{Root}/sharing/rest/content/users/{User}/items/{id}/delete"))
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["permanentDelete"] = "true", ["f"] = "json" }),
                };
                delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage _ = await Http.SendAsync(delete);
            }
        }
    }
}
