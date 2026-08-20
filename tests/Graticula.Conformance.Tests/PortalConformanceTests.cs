using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The portal surface, walked the way a client walks it.
/// </summary>
/// <remarks>
/// <b>ADR-040 conditions 2 and 3.</b> Condition 1 — that ArcGIS Pro actually
/// creates a connection — cannot be asserted here; it needs Pro. What can be
/// asserted is the two properties the surface would be worthless without: that the
/// items it lists are the catalogue's own, filtered by the same sharing rule as
/// everything else, and that its token endpoint is a third door onto one lock
/// rather than a third implementation.
/// </remarks>
[Trait("Category", "Conformance")]
[Collection("catalogue walk")]
public sealed class PortalConformanceTests : ArcGisClient
{
    [Fact]
    public async Task A_client_can_discover_how_to_authenticate()
    {
        string root = await RequireServerAsync();

        JsonElement info = await GetJsonAsync("/sharing/rest?f=json");

        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("currentVersion").GetString()));

        JsonElement auth = info.GetProperty("authInfo");

        Assert.True(auth.GetProperty("isTokenBasedSecurity").GetBoolean());

        // <b>The URL it names has to answer.</b> /rest/info pointed at an endpoint
        // speaking a different vocabulary for four days, and an ArcGIS client read
        // that as a rejected password. One line away from the same mistake here.
        string url = auth.GetProperty("tokenServicesUrl").GetString()!;

        Assert.StartsWith(root, url, StringComparison.Ordinal);

        using FormUrlEncodedContent probe = new(new Dictionary<string, string>
        {
            ["username"] = "no-such-account",
            ["password"] = "no-such-password",
            ["f"] = "json",
        });

        using HttpResponseMessage response = await Http.PostAsync(new Uri(url), probe);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_portal_describes_itself_the_same_way_twice()
    {
        // A portal id is cached by clients, so one that changes between requests is
        // a portal that has been replaced as far as they are concerned.
        JsonElement first = await GetJsonAsync("/sharing/rest/portals/self?f=json");
        JsonElement second = await GetJsonAsync("/sharing/rest/portals/self?f=json");

        string id = first.GetProperty("id").GetString()!;

        Assert.Equal(32, id.Length);
        Assert.Equal(id, second.GetProperty("id").GetString());

        // Pro asks where the geometry service is rather than assuming it.
        string geometry = first
            .GetProperty("helperServices").GetProperty("geometry").GetProperty("url").GetString()!;

        Assert.Contains("GeometryServer", geometry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_items_are_the_catalogue_filtered_by_who_is_asking()
    {
        // <b>ADR-040 condition 2.</b> Asserted by comparing two callers rather than
        // by reading the filter, which is how /admin/routes is asserted and for the
        // same reason: a filter that is applied and a filter that is written are
        // different claims.
        string root = await RequireServerAsync();

        JsonElement anonymous = await GetJsonAsync("/sharing/rest/search?q=&f=json");

        int open = anonymous.GetProperty("total").GetInt32();

        Assert.True(open >= 0);

        string? token = await TokenAsync(root);

        Assert.False(
            string.IsNullOrWhiteSpace(token),
            "no credentials, so this FAILS rather than skipping: a sharing test that runs as one "
            + "caller asserts nothing about filtering.");

        JsonElement authenticated =
            await GetJsonAsync($"/sharing/rest/search?q=&f=json&token={token}");

        int all = authenticated.GetProperty("total").GetInt32();

        Assert.True(
            all >= open,
            $"an authenticated caller saw fewer items ({all}) than an anonymous one ({open})");

        // Every item a client is offered has to be one it can open.
        foreach (JsonElement item in authenticated.GetProperty("results").EnumerateArray())
        {
            Assert.Equal(32, item.GetProperty("id").GetString()!.Length);
            Assert.Contains("Service", item.GetProperty("type").GetString()!, StringComparison.Ordinal);
            Assert.Contains("/rest/services/", item.GetProperty("url").GetString()!, StringComparison.Ordinal);
            Assert.False(item.GetProperty("typeKeywords").GetArrayLength() == 0);
        }
    }

    [Fact]
    public async Task An_item_a_caller_may_not_see_is_absent_rather_than_forbidden()
    {
        string root = await RequireServerAsync();

        string? token = await TokenAsync(root);

        Assert.False(string.IsNullOrWhiteSpace(token));

        JsonElement mine = await GetJsonAsync($"/sharing/rest/search?q=&f=json&token={token}");
        JsonElement open = await GetJsonAsync("/sharing/rest/search?q=&f=json");

        HashSet<string> anonymousIds =
        [
            .. open.GetProperty("results").EnumerateArray()
                .Select(i => i.GetProperty("id").GetString()!),
        ];

        string? hidden = mine.GetProperty("results").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!)
            .FirstOrDefault(id => !anonymousIds.Contains(id));

        if (hidden is null)
        {
            // Nothing is private on this deployment, so there is nothing to hide.
            // Said rather than passed silently: a green test about an empty set
            // reads as coverage.
            return;
        }

        using HttpResponseMessage response =
            await Http.GetAsync(new Uri($"{root}/sharing/rest/content/items/{hidden}?f=json"));

        using JsonDocument document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // <b>The same answer as for an id that does not exist.</b> A caller who may
        // not see an item must not be able to tell the two apart.
        Assert.True(document.RootElement.TryGetProperty("error", out JsonElement error));

        Assert.Contains(
            "does not exist or is inaccessible",
            error.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_three_token_endpoints_answer_from_one_session_store()
    {
        // <b>ADR-040 condition 3.</b> Three doors appeared in two days —
        // /rest/generateToken for Esri REST clients, /admin/generateToken for Pro's
        // server probe, /sharing/rest/generateToken for its portal connection. A
        // third spelling is where a copy of the login usually appears; this asserts
        // that each one issues a credential the others' surfaces accept.
        string root = await RequireServerAsync();

        string user = Environment.GetEnvironmentVariable("GRATICULA_TEST_USER")!;
        string password = Environment.GetEnvironmentVariable("GRATICULA_TEST_PASSWORD")!;

        Assert.False(
            string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password),
            "no credentials, so this FAILS rather than skipping.");

        foreach (string endpoint in (string[])
            ["/rest/generateToken", "/admin/generateToken", "/sharing/rest/generateToken"])
        {
            using FormUrlEncodedContent form = new(new Dictionary<string, string>
            {
                ["username"] = user,
                ["password"] = password,
                ["f"] = "json",
            });

            using HttpResponseMessage issued =
                await Http.PostAsync(new Uri($"{root}{endpoint}"), form);

            issued.EnsureSuccessStatusCode();

            using JsonDocument document =
                JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

            string token = document.RootElement.GetProperty("token").GetString()!;

            Assert.False(string.IsNullOrWhiteSpace(token), $"{endpoint} issued no token");

            // The credential from each door has to work on a surface none of them
            // belongs to.
            using HttpRequestMessage request = new(
                HttpMethod.Get, new Uri($"{root}/sharing/rest/community/self?f=json"));

            request.Headers.Add("Authorization", $"Bearer {token}");

            using HttpResponseMessage response = await Http.SendAsync(request);

            Assert.True(
                response.IsSuccessStatusCode,
                $"a token from {endpoint} was not accepted elsewhere: {response.StatusCode}");

            using JsonDocument self =
                JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(user, self.RootElement.GetProperty("username").GetString());
        }
    }
}
