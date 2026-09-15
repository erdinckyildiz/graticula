using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A token that has been revoked answers 498 on the ArcGIS surface, so a client asks its user to sign
/// in again — ADR-015 §4a.
/// </summary>
/// <remarks>
/// Written 2026-09-15, against the showcase: after signing out, the same token on a private layer
/// answered 404 and on the folder listing answered 200 without the layer, so a dashboard whose token
/// expired went quietly empty instead of prompting.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ARevokedTokenIs498Tests : ArcGisClient
{
    [Fact]
    public async Task A_revoked_token_is_told_it_is_invalid_and_no_token_is_still_anonymous()
    {
        string root = await RequireServerAsync();
        string? user = Environment.GetEnvironmentVariable(UserVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password),
            $"{UserVariable} and {PasswordVariable} are not set, so this test FAILS rather than skips.");

        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        // A session of its own, so revoking it does not sign the rest of the suite out.
        using HttpResponseMessage login = await http.PostAsync(
            new Uri($"{root}/rest/auth/login"),
            new StringContent(JsonSerializer.Serialize(new { name = user, password }), Encoding.UTF8, "application/json"));

        Assert.True(login.IsSuccessStatusCode, $"Signing in answered {(int)login.StatusCode}.");

        string token = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("token").GetString()!;

        using (HttpRequestMessage logout = new(HttpMethod.Post, new Uri($"{root}/rest/auth/logout")))
        {
            logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage signedOut = await http.SendAsync(logout);
            Assert.True(signedOut.IsSuccessStatusCode, $"Signing out answered {(int)signedOut.StatusCode}.");
        }

        using (HttpRequestMessage stale = new(HttpMethod.Get, new Uri($"{root}/rest/services?f=json")))
        {
            stale.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage answered = await http.SendAsync(stale);
            string body = await answered.Content.ReadAsStringAsync();

            Assert.True((int)answered.StatusCode == 498, $"A revoked token answered {(int)answered.StatusCode}: {body}");
            Assert.Equal(498, JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        using HttpResponseMessage anonymous = await http.GetAsync(new Uri($"{root}/rest/services?f=json"));

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
    }
}
