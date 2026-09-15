using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The sign-in an Esri client performs, in its own vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written after ArcGIS Pro could not sign in, on 2026-08-19.</b> Pro read
/// <c>/rest/info</c>, saw <c>isTokenBasedSecurity</c>, followed
/// <c>tokenServicesUrl</c> to <c>/rest/auth/login</c>, posted a form with
/// <c>username</c>, and was told its credentials were wrong. They were correct.
/// The endpoint speaks JSON and wants a field called <c>name</c>, and nothing in
/// this suite had ever tried the other spelling.
/// </para>
/// <para>
/// <b>Every check here is a thing a client does, not a thing an endpoint has.</b>
/// The gap was not a missing route — <c>/rest/auth/login</c> worked perfectly for
/// everything that spoke to it in JSON. It was that the document telling clients
/// how to authenticate pointed at a door with a different lock.
/// </para>
/// </remarks>
[Trait("Category", "Conformance")]
[Collection("catalogue walk")]
public sealed class ArcGisTokenTests : ArcGisClient
{
    [Fact]
    public async Task The_advertised_token_service_is_one_an_esri_client_can_use()
    {
        string root = await RequireServerAsync();

        JsonElement info = await GetJsonAsync("/rest/info");
        JsonElement auth = info.GetProperty("authInfo");

        Assert.True(auth.GetProperty("isTokenBasedSecurity").GetBoolean());

        string url = auth.GetProperty("tokenServicesUrl").GetString()!;

        // <b>The document must point somewhere that answers.</b> It pointed at an
        // endpoint that answered 400 to every Esri client for four days.
        using FormUrlEncodedContent probe = new(new Dictionary<string, string>
        {
            ["username"] = "no-such-account",
            ["password"] = "no-such-password",
            ["f"] = "json",
        });

        using HttpResponseMessage response = await Http.PostAsync(new Uri(url), probe);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(body);

        // A refusal, in the shape a client renders rather than one it ignores.
        JsonElement error = document.RootElement.GetProperty("error");

        Assert.Equal("Unable to generate token.", error.GetProperty("message").GetString());
        Assert.True(error.GetProperty("details").GetArrayLength() > 0);

        // <b>And the status is truthful.</b> Esri's own service answers 200 with a
        // failure inside, which hides it from every proxy, log and monitor.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_from_that_service_authenticates_both_ways_an_esri_client_sends_it()
    {
        string root = await RequireServerAsync();

        string? user = Environment.GetEnvironmentVariable("GRATICULA_TEST_USER");
        string? password = Environment.GetEnvironmentVariable("GRATICULA_TEST_PASSWORD");

        Assert.False(
            string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password),
            "GRATICULA_TEST_USER and GRATICULA_TEST_PASSWORD are not set, so this FAILS rather "
            + "than skipping. A sign-in test that passes without credentials asserts nothing.");

        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["username"] = user!,
            ["password"] = password!,
            ["client"] = "requestip",
            ["expiration"] = "60",
            ["f"] = "json",
        });

        using HttpResponseMessage issued =
            await Http.PostAsync(new Uri($"{root}/rest/generateToken"), form);

        issued.EnsureSuccessStatusCode();

        using JsonDocument document =
            JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        string token = document.RootElement.GetProperty("token").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(token));

        // Milliseconds since the epoch, which is what an Esri client reads, and in
        // the future — a token that has already expired is a token that never worked.
        long expires = document.RootElement.GetProperty("expires").GetInt64();

        Assert.True(expires > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // <b>And no later than the sixty minutes asked for</b> — ADR-015 §4 mitigation 3. Until
        // 2026-09-15 `expiration` was ignored and every token lived about twelve hours. Two minutes
        // of slack for the clock between here and the server.
        Assert.True(
            expires <= DateTimeOffset.UtcNow.AddMinutes(62).ToUnixTimeMilliseconds(),
            $"A token asked for 60 minutes expires at {DateTimeOffset.FromUnixTimeMilliseconds(expires):u}.");

        // <b>The token has to do something, and "200 OK" does not show that.</b>
        // The catalogue filters by who is asking, so an authenticated caller sees
        // at least what an anonymous one sees, and the test is that the credential
        // was applied rather than ignored.
        int anonymous = await ServiceCountAsync($"{root}/rest/services?f=json");

        int byQuery = await ServiceCountAsync($"{root}/rest/services?f=json&token={token}");

        Assert.True(
            byQuery >= anonymous,
            $"a token in the query string was ignored: {byQuery} services against {anonymous}");

        using HttpRequestMessage headed = new(
            HttpMethod.Get, new Uri($"{root}/rest/services?f=json"));

        headed.Headers.Add("X-Esri-Authorization", $"Bearer {token}");

        using HttpResponseMessage response = await Http.SendAsync(headed);

        response.EnsureSuccessStatusCode();

        using JsonDocument headedBody =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(byQuery, Count(headedBody.RootElement));
    }

    [Fact]
    public async Task The_administrative_token_endpoint_answers_the_probe_a_client_makes()
    {
        string root = await RequireServerAsync();

        // <b>Pro asks with no credentials at all, to find out whether the endpoint
        // exists.</b> It answered 404 until 2026-08-20, so the connection could not
        // be created and the whole REST surface behind it was never reached. The
        // answer now is a refusal a client can read, which is a different thing
        // from an absence.
        using HttpResponseMessage probe =
            await Http.GetAsync(new Uri($"{root}/admin/generateToken?f=json"));

        // <b>200, deliberately, and it is the only failure here that gets one.</b>
        // A probe carrying no credentials is asking whether the endpoint exists,
        // not signing in. Pro reads a 401 to that question as *no such server* and
        // stops; a wrong password still gets 401 from the same handler.
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);

        using JsonDocument refused =
            JsonDocument.Parse(await probe.Content.ReadAsStringAsync());

        // The administrative API spells an error differently from the REST one, and
        // answering the REST shape here is a document the client cannot read.
        Assert.Equal("error", refused.RootElement.GetProperty("status").GetString());
        Assert.True(refused.RootElement.GetProperty("messages").GetArrayLength() > 0);

        string? user = Environment.GetEnvironmentVariable("GRATICULA_TEST_USER");
        string? password = Environment.GetEnvironmentVariable("GRATICULA_TEST_PASSWORD");

        Assert.False(
            string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password),
            "GRATICULA_TEST_USER and GRATICULA_TEST_PASSWORD are not set, so this FAILS rather "
            + "than skipping.");

        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["username"] = user!,
            ["password"] = password!,
            ["client"] = "requestip",
            ["expiration"] = "60",
            ["f"] = "json",
        });

        using HttpResponseMessage issued =
            await Http.PostAsync(new Uri($"{root}/admin/generateToken"), form);

        issued.EnsureSuccessStatusCode();

        using JsonDocument granted =
            JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        string token = granted.RootElement.GetProperty("token").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(token));

        // <b>A real credential on the ArcGIS surface.</b>
        using HttpRequestMessage authorised = new(
            HttpMethod.Get, new Uri($"{root}/rest/services?f=json"));

        authorised.Headers.Add("Authorization", $"Bearer {token}");

        using HttpResponseMessage listed = await Http.SendAsync(authorised);

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        // <b>And not a key to the administration API — ADR-015 §4, Q-154 (owner decision
        // 2026-09-15).</b> This test asserted the opposite until that day: one kind of session, with
        // privileges from the account whatever the door. A token from any generateToken is scoped to
        // the ArcGIS surfaces, so a token that leaks from a client's log cannot administer the server;
        // the refusal says where a token that can is issued.
        using HttpRequestMessage administrative = new(
            HttpMethod.Get, new Uri($"{root}/admin/layers"));

        administrative.Headers.Add("Authorization", $"Bearer {token}");

        using HttpResponseMessage refusedAdmin = await Http.SendAsync(administrative);
        string why = await refusedAdmin.Content.ReadAsStringAsync();

        Assert.True(refusedAdmin.StatusCode == HttpStatusCode.Forbidden, $"/admin/layers with an ArcGIS token answered {(int)refusedAdmin.StatusCode}: {why}");
        Assert.Contains("/rest/auth/login", why, StringComparison.Ordinal);

        // The console's own sign-in still opens it: the scope is the door's, not the account's.
        string? console = await TokenAsync(root);
        using HttpRequestMessage signedIn = new(HttpMethod.Get, new Uri($"{root}/admin/layers"));
        signedIn.Headers.Add("Authorization", $"Bearer {console}");
        using HttpResponseMessage opened = await Http.SendAsync(signedIn);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
    }

    private async Task<int> ServiceCountAsync(string url)
    {
        using JsonDocument document =
            JsonDocument.Parse(await Http.GetStringAsync(new Uri(url)));

        return Count(document.RootElement);
    }

    private static int Count(JsonElement catalogue) =>
        catalogue.TryGetProperty("services", out JsonElement services)
            ? services.GetArrayLength()
            : 0;
}
