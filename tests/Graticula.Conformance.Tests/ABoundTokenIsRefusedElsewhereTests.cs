using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A token issued with <c>client=referer</c> or <c>client=ip</c> works only from there, on every token
/// door — D-268.
/// </summary>
/// <remarks>Written 2026-09-15: the parameters were accepted and bound nothing.</remarks>
[Collection("catalogue walk")]
public sealed class ABoundTokenIsRefusedElsewhereTests : ArcGisClient
{
    [Theory]
    [InlineData("/rest/generateToken")]
    [InlineData("/sharing/rest/generateToken")]
    public async Task A_bound_token_answers_where_it_was_bound_and_498_elsewhere(string door)
    {
        string root = await RequireServerAsync();
        string? user = Environment.GetEnvironmentVariable(UserVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password),
            $"{UserVariable} and {PasswordVariable} are not set, so this test FAILS rather than skips.");

        async Task<(HttpStatusCode Status, string Body)> TokenAsync(params (string Key, string Value)[] extra)
        {
            List<KeyValuePair<string, string>> fields = [new("username", user!), new("password", password!), new("f", "json")];
            foreach ((string key, string value) in extra)
            {
                fields.Add(new(key, value));
            }

            using FormUrlEncodedContent form = new(fields);
            using HttpResponseMessage response = await Http.PostAsync(new Uri($"{root}{door}"), form);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        async Task<int> UseAsync(string token, string? referer)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{root}/rest/services?f=json&token={Uri.EscapeDataString(token)}"));

            if (referer is not null)
            {
                request.Headers.Referrer = new Uri(referer);
            }

            using HttpResponseMessage response = await Http.SendAsync(request);
            return (int)response.StatusCode;
        }

        static string Token(string body) => JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;

        (HttpStatusCode refererStatus, string refererBody) = await TokenAsync(("client", "referer"), ("referer", "https://maps.example.com/app/"));
        Assert.True(refererStatus == HttpStatusCode.OK, $"client=referer answered {(int)refererStatus}: {refererBody}");
        string bound = Token(refererBody);

        Assert.Equal(200, await UseAsync(bound, "https://maps.example.com/other"));
        Assert.Equal(498, await UseAsync(bound, "https://elsewhere.example.com/"));
        Assert.Equal(498, await UseAsync(bound, null));

        // An address this runner is not.
        (HttpStatusCode ipStatus, string ipBody) = await TokenAsync(("client", "ip"), ("ip", "203.0.113.254"));
        Assert.True(ipStatus == HttpStatusCode.OK, $"client=ip answered {(int)ipStatus}: {ipBody}");
        Assert.Equal(498, await UseAsync(Token(ipBody), null));

        (HttpStatusCode requestStatus, string requestBody) = await TokenAsync(("client", "requestip"));
        Assert.True(requestStatus == HttpStatusCode.OK, $"client=requestip answered {(int)requestStatus}: {requestBody}");
        Assert.Equal(200, await UseAsync(Token(requestBody), null));

        (HttpStatusCode unbound, string unboundBody) = await TokenAsync();
        Assert.True(unbound == HttpStatusCode.OK, unboundBody);
        Assert.Equal(200, await UseAsync(Token(unboundBody), "https://anywhere.example/"));

        (_, string refused) = await TokenAsync(("client", "referer"));
        Assert.Contains("referer", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("\"token\"", refused, StringComparison.Ordinal);
    }
}
