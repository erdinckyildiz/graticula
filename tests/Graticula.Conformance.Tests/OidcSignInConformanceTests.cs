using System;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Signing in through an OpenID Connect provider, end to end against a provider this suite runs — ADR-088.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole round trip a browser makes</b>: the server's start sends it to the provider with a state, a nonce and a
/// PKCE challenge; the provider sends it back with a code; the server redeems the code with its secret and verifier,
/// validates the ID token, and ends in its own session cookie. What is asserted is who that cookie says you are.
/// </para>
/// <para>
/// <b>The owner's three answers are each a case</b>: an account made at a first sign-in when the provider allows it,
/// none made when it does not, and an account an administrator made ahead of time bound at the first sign-in.
/// </para>
/// </remarks>
public sealed class OidcSignInConformanceTests : ArcGisClient
{
    internal sealed record Outcome(HttpStatusCode Callback, string? Name, string Body);

    /// <summary>Walks the redirects a browser would, with a browser's cookie jar.</summary>
    internal static async Task<Outcome> SignInAsync(string root, string providerId)
    {
        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using HttpClient browser = new(handler);

        using HttpResponseMessage start = await browser.GetAsync(new Uri($"{root}/rest/auth/oidc/{providerId}/start?return=/rest/whoami"));
        Assert.True(start.StatusCode == HttpStatusCode.Redirect, $"Starting a sign-in answered {(int)start.StatusCode}: {await start.Content.ReadAsStringAsync()}");

        using HttpResponseMessage atProvider = await browser.GetAsync(start.Headers.Location!);
        Assert.Equal(HttpStatusCode.Redirect, atProvider.StatusCode);

        using HttpResponseMessage callback = await browser.GetAsync(atProvider.Headers.Location!);
        string body = await callback.Content.ReadAsStringAsync();

        if (callback.StatusCode != HttpStatusCode.Redirect)
        {
            return new Outcome(callback.StatusCode, null, body);
        }

        Assert.Equal("/rest/whoami", callback.Headers.Location!.OriginalString);

        using HttpResponseMessage whoami = await browser.GetAsync(new Uri($"{root}/rest/whoami"));
        string who = await whoami.Content.ReadAsStringAsync();

        return new Outcome(callback.StatusCode, JsonDocument.Parse(who).RootElement.GetProperty("name").GetString(), who);
    }

    private async Task<string> ProviderAsync(FakeOidcProvider idp, string name, bool autoCreate)
    {
        (int status, string body) = await AdminAsync(
            HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new
            {
                name,
                issuer = idp.Issuer,
                clientId = idp.ClientId,
                clientSecret = idp.Secret,
                autoCreate,
                defaultRole = "viewer",
            }));

        Assert.True(status == 201, $"Adding the provider answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_first_sign_in_makes_an_account_when_the_provider_allows_it_and_the_next_finds_it()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeOidcProvider idp = new($"graticula-{suffix}", $"secret-{suffix}");

        string id = await ProviderAsync(idp, $"zz Staff {suffix}", autoCreate: true);
        string? made = null;

        try
        {
            idp.Next = ($"sub-{suffix}", $"alice{suffix}@example.org");

            Outcome first = await SignInAsync(root, id);
            Assert.True(first.Name is not null, $"The first sign-in did not end signed in ({(int)first.Callback}): {first.Body} {idp.LastRefusal}");
            made = first.Name;

            // The part of the name before the @, and a viewer, as the provider was set.
            Assert.Equal($"alice{suffix}", made);

            Outcome second = await SignInAsync(root, id);
            Assert.Equal(made, second.Name);

            (int _, string members) = await AdminAsync(HttpMethod.Get, "/admin/members");
            JsonElement member = Assert.Single(JsonDocument.Parse(members).RootElement.GetProperty("members").EnumerateArray()
                .Where(m => m.GetProperty("name").GetString() == made).ToArray());
            Assert.Contains("viewer", member.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (made is not null) await AdminAsync(HttpMethod.Delete, $"/admin/members/{made}");
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task Without_automatic_accounts_only_an_account_made_ahead_signs_in_and_the_first_sign_in_binds_it()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeOidcProvider idp = new($"graticula-{suffix}", $"secret-{suffix}");

        string id = await ProviderAsync(idp, $"zz Directory {suffix}", autoCreate: false);
        string member = $"bob{suffix}";

        try
        {
            // ---- nobody has an account: refused, and nothing is made ----
            idp.Next = ($"sub-bob-{suffix}", $"bob.{suffix}@example.org");

            Outcome refused = await SignInAsync(root, id);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Callback);
            Assert.Contains("no account", refused.Body, StringComparison.Ordinal);

            // ---- an administrator makes one ahead, by the name the provider gives ----
            (int created, string said) = await AdminAsync(
                HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = member, role = "viewer", provider = id, username = $"bob.{suffix}@example.org" }));

            Assert.True(created == 201, $"Making the account answered {created}: {said}");
            Assert.DoesNotContain("password\":\"", said, StringComparison.Ordinal);

            Outcome bound = await SignInAsync(root, id);
            Assert.Equal(member, bound.Name);

            // ---- once bound to the subject, the same name from another subject is not that account ----
            idp.Next = ($"sub-impostor-{suffix}", $"bob.{suffix}@example.org");

            Outcome impostor = await SignInAsync(root, id);
            Assert.Equal(HttpStatusCode.Forbidden, impostor.Callback);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}");
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    public async Task An_id_token_this_sign_in_did_not_ask_for_signs_nobody_in(string spoil)
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeOidcProvider idp = new($"graticula-{suffix}", $"secret-{suffix}");

        string id = await ProviderAsync(idp, $"zz Spoilt {suffix}", autoCreate: true);

        try
        {
            idp.Next = ($"sub-{suffix}", $"mallory{suffix}");
            idp.Spoil = spoil;

            Outcome outcome = await SignInAsync(root, id);

            Assert.True(outcome.Name is null, $"A token with a bad {spoil} signed in {outcome.Name}.");
            Assert.Equal(HttpStatusCode.BadGateway, outcome.Callback);

            // Nothing was made for it either.
            (int _, string members) = await AdminAsync(HttpMethod.Get, "/admin/members");
            Assert.DoesNotContain($"mallory{suffix}", members, StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task A_callback_this_browser_did_not_start_is_refused()
    {
        string root = await RequireServerAsync();

        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using HttpClient stranger = new(handler);

        using HttpResponseMessage callback = await stranger.GetAsync(new Uri($"{root}/rest/auth/oidc/callback?code=x&state=y"));

        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.False(callback.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith("gis-session=", StringComparison.Ordinal) && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)),
            "A callback nobody started set a session.");
    }
}
