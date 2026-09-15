using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>/rest/info</c> names the portal that owns this server, and a portal token is exchanged for the server's
/// the way the ArcGIS Maps SDK asks.
/// </summary>
/// <remarks>Written 2026-09-15: no <c>owningSystemUrl</c>, and a token-for-token request was refused as a
/// sign-in with no username. The request shape is the one SDK 4.30 was measured sending.</remarks>
[Collection("catalogue walk")]
public sealed class APortalTokenOpensItsServerTests : ArcGisClient
{
    [Fact]
    public async Task A_portal_token_is_exchanged_for_this_server_and_for_no_other()
    {
        string root = await RequireServerAsync();
        string? user = Environment.GetEnvironmentVariable(UserVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password),
            $"{UserVariable} and {PasswordVariable} are not set, so this test FAILS rather than skips.");

        JsonElement info = await GetJsonAsync("/rest/info");
        Assert.Equal(root.TrimEnd('/'), info.GetProperty("owningSystemUrl").GetString()!.TrimEnd('/'));

        async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, params (string Key, string Value)[] fields)
        {
            List<KeyValuePair<string, string>> pairs = [new("f", "json")];

            foreach ((string key, string value) in fields)
            {
                pairs.Add(new(key, value));
            }

            using FormUrlEncodedContent form = new(pairs);
            using HttpResponseMessage response = await Http.PostAsync(new Uri($"{root}{path}"), form);
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }

        (HttpStatusCode signedIn, JsonElement portal) = await PostAsync(
            "/sharing/rest/generateToken", ("username", user!), ("password", password!));
        Assert.True(signedIn == HttpStatusCode.OK, portal.ToString());
        string portalToken = portal.GetProperty("token").GetString()!;

        string layer = $"{root}/rest/services/hosted/anything/FeatureServer/0";

        foreach (string door in (string[])["/rest/generateToken", "/sharing/rest/generateToken"])
        {
            (HttpStatusCode exchanged, JsonElement server) = await PostAsync(
                door, ("request", "getToken"), ("serverUrl", layer), ("token", portalToken));

            Assert.True(exchanged == HttpStatusCode.OK, $"{door}: {server}");
            Assert.Equal(portalToken, server.GetProperty("token").GetString());
            Assert.Equal(portal.GetProperty("expires").GetInt64(), server.GetProperty("expires").GetInt64());
        }

        (HttpStatusCode foreign, JsonElement why) = await PostAsync(
            "/rest/generateToken", ("request", "getToken"), ("serverUrl", "https://elsewhere.example/arcgis/rest/services"), ("token", portalToken));
        Assert.True(foreign == HttpStatusCode.BadRequest, $"Another host answered {(int)foreign}: {why}");

        (HttpStatusCode stale, _) = await PostAsync(
            "/rest/generateToken", ("request", "getToken"), ("serverUrl", layer), ("token", "not-a-token-this-server-issued"));
        Assert.Equal(498, (int)stale);
    }
}
