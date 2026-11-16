using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.27 against a real server and PostGIS: a form whose <c>pulldata("@javascript")</c> names a script is
/// published only when the script is among the item's files and the form is not shared with everyone; it cannot be
/// shared with everyone afterwards; its script is served as text, never as a script; the client is told whether it may
/// run it; and the sandbox page carries its own policy.
/// </summary>
[Trait("Needs", "TwoNodes")]
[Collection("one sync node")]
public sealed class AFormsScriptGoesOutOnlyWhereItMayRunTests(OneStartedNode started)
{
    private TwoNodeSite.Node Node => started.Node;

    private static JsonArray Row(params string[] cells) => new([.. cells.Select(c => (JsonNode?)c)]);

    private static JsonObject Model(string formId, string calculation) => new()
    {
        ["format"] = "xlsform/1",
        ["sheets"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "survey",
                ["columns"] = Row("type", "name", "label", "calculation"),
                ["rows"] = new JsonArray(Row("integer", "a", "A", ""), Row("integer", "b", "B", ""), Row("calculate", "c", "", calculation)),
            },
            new JsonObject
            {
                ["name"] = "settings",
                ["columns"] = Row("form_title", "form_id"),
                ["rows"] = new JsonArray(Row("Form " + formId, formId)),
            }),
    };

    private const string Calls = "pulldata('@javascript', 'calc.js', 'add', ${a}, ${b})";

    private async Task<string> AddAsync(string token, string formId, string calculation)
    {
        (int status, string body) = await Node.SendAsync(HttpMethod.Post, "/admin/forms", token, new { title = "Form " + formId, model = Model(formId, calculation) });
        Assert.True(status == 201, $"Adding the form answered {status}: {body}");
        return (string)JsonNode.Parse(body)!["id"]!;
    }

    private async Task AddScriptAsync(string token, string id, string text = "function add(a, b) { return Number(a) + Number(b); }")
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/sharing/rest/content/users/{TwoNodeSite.Administrator}/items/{id}/addResources");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        ByteArrayContent file = new(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/javascript");
        request.Content = new MultipartFormDataContent
        {
            { file, "file", "calc.js" },
            { new StringContent("scripts"), "resourcesPrefix" },
            { new StringContent("json"), "f" },
        };
        using HttpResponseMessage answer = await Node.Http.SendAsync(request);
        string body = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.IsSuccessStatusCode && body.Contains("\"success\":true", StringComparison.Ordinal), $"Adding the script answered {(int)answer.StatusCode}: {body}");
    }

    [Fact]
    public async Task A_form_with_a_script_is_published_with_its_file_and_never_shared_with_everyone()
    {
        string token = await Node.SignInAsync();
        string id = await AddAsync(token, "zz_script_" + Guid.NewGuid().ToString("N")[..8], Calls);

        // No file yet: refused at its row, with where to put it.
        (int refused, string why) = await Node.SendAsync(HttpMethod.Post, $"/admin/forms/{id}/publish", token, new { });
        Assert.True(refused == 400, $"Publishing with no script answered {refused}: {why}");
        Assert.Contains("The script 'calc.js' is not among this form's files. Add it to the item's files as scripts/calc.js.", why, StringComparison.Ordinal);

        await AddScriptAsync(token, id);
        (int published, string made) = await Node.SendAsync(HttpMethod.Post, $"/admin/forms/{id}/publish", token, new { });
        Assert.True(published == 200, $"Publishing with the script answered {published}: {made}");

        // The script is served as text and is never sniffed: no page here can load it as a script.
        using (HttpRequestMessage get = new(HttpMethod.Get, $"/sharing/rest/content/items/{id}/resources/scripts/calc.js"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage answer = await Node.Http.SendAsync(get);
            Assert.Equal("text/plain", answer.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", answer.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("function add", await answer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // The member is told the form's scripts may run.
        (int read, string client) = await Node.SendAsync(HttpMethod.Get, $"/forms/{id}/form.json", token);
        Assert.True(read == 200, client);
        Assert.Equal("allowed", (string?)JsonNode.Parse(client)!["scripts"]);

        // Shared with the organization, yes; with everyone, no — and its sharing is left as it was.
        (int org, string orgBody) = await Node.SendAsync(HttpMethod.Put, $"/admin/forms/{id}/sharing", token, new { sharing = "organization" });
        Assert.True(org == 200, $"Sharing with the organization answered {org}: {orgBody}");
        (int everyone, string everyoneBody) = await Node.SendAsync(HttpMethod.Put, $"/admin/forms/{id}/sharing", token, new { sharing = "public" });
        Assert.True(everyone == 409, $"Sharing a form with a script with everyone answered {everyone}: {everyoneBody}");
        Assert.Contains("runs 'calc.js'", everyoneBody, StringComparison.Ordinal);
        Assert.Contains("\"sharing\":\"organization\"", (await Node.SendAsync(HttpMethod.Get, $"/admin/forms/{id}", token)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_form_already_shared_with_everyone_is_not_published_again_with_a_script()
    {
        string token = await Node.SignInAsync();
        string formId = "zz_public_" + Guid.NewGuid().ToString("N")[..8];
        string id = await AddAsync(token, formId, "${a} + ${b}");
        (int published, string made) = await Node.SendAsync(HttpMethod.Post, $"/admin/forms/{id}/publish", token, new { });
        Assert.True(published == 200, made);
        (int shared, string sharedBody) = await Node.SendAsync(HttpMethod.Put, $"/admin/forms/{id}/sharing", token, new { sharing = "public" });
        Assert.True(shared == 200, sharedBody);

        // A script added afterwards: the draft is saved, publishing it again is refused, and anyone reading the form —
        // here the draft is not what is served, but a form never published would be — is told its scripts do not run.
        await AddScriptAsync(token, id);
        (int saved, string savedBody) = await Node.SendAsync(HttpMethod.Put, $"/admin/forms/{id}/model", token, Model(formId, Calls));
        Assert.True(saved == 200, savedBody);
        (int again, string againBody) = await Node.SendAsync(HttpMethod.Post, $"/admin/forms/{id}/publish", token, new { });
        Assert.True(again == 400, $"Publishing a public form with a script answered {again}: {againBody}");
        Assert.Contains("a form shared with everyone runs no script", againBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_form_never_published_and_shared_with_everyone_is_sent_with_its_scripts_refused()
    {
        string token = await Node.SignInAsync();
        string id = await AddAsync(token, "zz_draft_" + Guid.NewGuid().ToString("N")[..8], "${a} + ${b}");
        (int shared, string sharedBody) = await Node.SendAsync(HttpMethod.Put, $"/admin/forms/{id}/sharing", token, new { sharing = "public" });
        Assert.True(shared == 200, sharedBody);

        // Saving a draft is not publishing, so nothing refuses the script there; what the page is told is the last word.
        string formId = "zz_draft_x";
        (int saved, string savedBody) = await Node.SendAsync(HttpMethod.Put, $"/admin/forms/{id}/model", token, Model(formId, Calls));
        Assert.True(saved == 200, savedBody);
        (int read, string client) = await Node.SendAsync(HttpMethod.Get, $"/forms/{id}/form.json", null);
        Assert.True(read == 200, client);
        Assert.Equal("refused", (string?)JsonNode.Parse(client)!["scripts"]);
        (int member, string memberClient) = await Node.SendAsync(HttpMethod.Get, $"/forms/{id}/form.json", token);
        Assert.True(member == 200, memberClient);
        Assert.Equal("refused", (string?)JsonNode.Parse(memberClient)!["scripts"]);
    }

    [Fact]
    public async Task The_sandbox_page_carries_its_own_policy_and_the_form_page_may_frame_it()
    {
        using HttpResponseMessage sandbox = await Node.Http.GetAsync("/forms/script-sandbox.html?v=1");
        Assert.True(sandbox.IsSuccessStatusCode);
        string policy = sandbox.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("default-src 'none'; script-src 'sha256-", policy, StringComparison.Ordinal);
        Assert.EndsWith("; worker-src blob:; base-uri 'none'; form-action 'none'; frame-ancestors 'self'", policy, StringComparison.Ordinal);
        Assert.Equal("SAMEORIGIN", sandbox.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("immutable", sandbox.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);

        using HttpResponseMessage page = await Node.Http.GetAsync("/forms/fill.html");
        Assert.Contains("frame-src 'self';", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
    }
}
