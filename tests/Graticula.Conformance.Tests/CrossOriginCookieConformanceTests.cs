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
/// The session cookie never counts on a request from another origin, so answering such requests with credentials
/// allowed gives a page elsewhere nothing the signed-in user's session could read — Q-169.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Since 2026-10-14 a cross-origin read is answered with the origin reflected and
/// <c>Access-Control-Allow-Credentials: true</c>, because the Maps SDK sends every request to its portal with
/// credentials and a browser refuses <c>*</c> for those. That would hand any page the response to a request carrying
/// the visitor's session cookie — the configuration CORS exists to prevent — unless the cookie means nothing on such a
/// request. This test is that property, asserted against a running server rather than argued.
/// </para>
/// <para>
/// <b>Three requests, one cookie.</b> Without an <c>Origin</c> and with this server's own, <c>/rest/whoami</c> names the
/// signed-in user; with another origin's it says anonymous — and the answer still carries the credentialed headers, so
/// a browser would hand it to the page, which is exactly why it must be anonymous.
/// </para>
/// </remarks>
public sealed class CrossOriginCookieConformanceTests : ArcGisClient
{
    [Fact]
    public async Task A_session_cookie_counts_for_this_origin_and_never_for_another()
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();

        using HttpClient browser = new(new HttpClientHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        using (FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
               {
                   new("name", Environment.GetEnvironmentVariable(UserVariable) ?? "root"),
                   new("password", Environment.GetEnvironmentVariable(PasswordVariable) ?? string.Empty),
                   new("return", "/rest/services"),
               }))
        using (HttpResponseMessage _ = await browser.PostAsync(new Uri(root + "/rest/auth/login"), form))
        {
        }

        Assert.NotNull(jar.GetCookies(new Uri(root))["gis-session"]);

        Uri own = new(root);
        string ownOrigin = own.GetLeftPart(UriPartial.Authority);

        async Task<(bool Authenticated, HttpResponseMessage Response)> WhoAmIAsync(string? origin)
        {
            HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + "/rest/whoami?f=json"));

            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            HttpResponseMessage response = await browser.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, $"/rest/whoami answered {(int)response.StatusCode}: {body}");
            return (JsonDocument.Parse(body).RootElement.GetProperty("authenticated").GetBoolean(), response);
        }

        (bool plain, _) = await WhoAmIAsync(null);
        Assert.True(plain, "The session cookie did not sign in a request with no Origin, so this test measures nothing.");

        (bool same, _) = await WhoAmIAsync(ownOrigin);
        Assert.True(same, $"The session cookie did not count on a request from this server's own origin ({ownOrigin}).");

        (bool other, HttpResponseMessage answered) = await WhoAmIAsync("https://elsewhere.example.org");
        Assert.False(other,
            "A request from another origin was signed in by the session cookie. With credentials allowed across "
            + "origins, any page could read what the visitor's session reads — Q-169's whole premise.");

        Assert.Equal("true", answered.Headers.TryGetValues("Access-Control-Allow-Credentials", out var c) ? c.Single() : null);
        Assert.Equal("https://elsewhere.example.org",
            answered.Headers.TryGetValues("Access-Control-Allow-Origin", out var o) ? o.Single() : null);
    }
}
