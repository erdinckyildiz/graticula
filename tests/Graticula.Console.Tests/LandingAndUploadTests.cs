using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Three things ADR-101 promised and v1.0.213 did not deliver, each checked the way it actually runs.
/// </summary>
/// <remarks>
/// <b>Found by the verification review of 2026-09-30, on the showcase's own release.</b> The landing was
/// rewritten to Server by code meant for Server's first screen, which runs on Studio's boot too; the upload's
/// "open the new item" matched a service address the server never sends (it sends the layer's); and the
/// Settings tab's sharing radios could store <c>private</c> over a group share. Each was missed because what
/// was tested was not what runs: a stubbed call, a trapped write that answers <c>{}</c>, and a dialog tested
/// while its second copy was not.
/// </remarks>
public sealed class LandingAndUploadTests : ConsoleTest
{
    private static readonly HttpClient Client = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
    { Timeout = TimeSpan.FromSeconds(60) };

    [Fact]
    public async Task The_console_address_lands_in_Studio_and_stays_there()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/console", token);

        await WaitForAsync(
            "location.pathname.startsWith('/studio/') && !!document.querySelector('#view-content.on')",
            "/console did not open Studio's content list.");

        // Long enough for every boot-time read to have answered and rewritten the address, if one does.
        await Task.Delay(3000);

        string where = await Browser.EvaluateAsync<string>("location.pathname + location.hash") ?? "";

        Assert.True(
            where.StartsWith("/studio/", StringComparison.Ordinal) && !where.Contains("services", StringComparison.Ordinal),
            $"After the console settled the address is {where}. Server's first-landing rewrite ran on Studio and a reload would open Server.");
    }

    [Fact]
    public async Task A_real_import_answer_opens_the_new_item()
    {
        (string token, _) = await SignInAsync();
        string name = "zz_landing_upload_" + Guid.NewGuid().ToString("N")[..6];

        const string GeoJson = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[29.0,41.0]},"properties":{"label":"a"}}
            ]}
            """;

        using MultipartFormDataContent form = new();
        using ByteArrayContent bytes = new(Encoding.UTF8.GetBytes(GeoJson));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/geo+json");
        form.Add(bytes, "file", "landing.geojson");
        form.Add(new StringContent(name), "name");

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/admin/hosted/import")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Client.SendAsync(request);
        string answer = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"The import failed: {(int)response.StatusCode} {answer}");

        try
        {
            await OpenAsync("/studio/#/content", token);
            await WaitForAsync("typeof openCreated === 'function'", "The console did not load.");

            // The server's own answer, handed to the function the upload form calls with it.
            await Browser.EvaluateAsync<bool>($"(openCreated({answer}, 'private'), true)");

            await WaitForAsync(
                $"location.hash === '#/service/hosted/{name}'",
                $"The import's own answer did not open the new item. It answered: {answer}");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
        }
    }

    [Fact]
    public async Task A_group_share_shows_as_itself_on_the_Settings_tab()
    {
        (string token, _) = await SignInAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_GROUPED") ?? "hosted/ci_EarlyAlert";
        string bare = service[(service.LastIndexOf('/') + 1)..];
        const string Group = "zz_settings_share_probe";

        (int listed, string items) = await AdminAsync(HttpMethod.Get, "/content/items");
        Assert.Equal(200, listed);

        string before = "private";
        foreach (JsonElement i in JsonDocument.Parse(items).RootElement.GetProperty("items").EnumerateArray())
        {
            if (i.GetProperty("name").GetString() == service) before = i.GetProperty("sharing").GetString() ?? "private";
        }

        await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name = Group, title = "Settings share probe" }));

        try
        {
            (int put, string why) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{Group}/items/{bare}?folder=hosted");
            Assert.True(put is 200 or 201 or 204, $"{put} {why}");

            (put, why) = await AdminAsync(HttpMethod.Put, $"/admin/services/{bare}/sharing?folder=hosted",
                JsonSerializer.Serialize(new { sharing = "group" }));
            Assert.True(put == 200, $"{put} {why}");

            await OpenAsync($"/studio/#/service/{service}?tab=settings", token);

            // <b>Stated, not offered as radios</b> (ADR-102): the Settings radios were the second control that could
            // store private over a group share. General says the level and the Share dialog changes it.
            await WaitForAsync(
                "document.getElementById('generalSharing')?.dataset.sharing === 'group'",
                "A group-scoped service's Settings do not say it is shared with groups.");

            bool radios = await Browser.EvaluateAsync<bool>("!!document.querySelector('#view-service input[name=\"capSharing\"]')");

            Assert.False(radios, "Settings still offers sharing radios beside the Share dialog.");
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, $"/admin/services/{bare}/sharing?folder=hosted",
                JsonSerializer.Serialize(new { sharing = before }));
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Group}");
        }
    }
}
