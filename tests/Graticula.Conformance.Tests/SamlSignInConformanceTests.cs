using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Signing in through a SAML 2.0 identity provider, end to end against a provider this suite runs — ADR-090.
/// </summary>
/// <remarks>
/// <para>
/// <b>The round trip a browser makes</b>: the server's start sends it to the provider with a request; the provider
/// posts back a signed response; the server checks it and ends in its own session cookie.
/// </para>
/// <para>
/// <b>Every refusal is a response the library alone was measured accepting or crashing on</b>, before this was
/// written (ADR-090 §4): one that answers another request, one confirmed for another address, one replayed, and one
/// the provider sends unasked. The rest — a changed name, another key, another audience, no signature, a document
/// type — are the library's, asserted here so that an upgrade that loosens one is seen.
/// </para>
/// </remarks>
public sealed class SamlSignInConformanceTests : ArcGisClient
{
    private sealed record Started(HttpClient Browser, CookieContainer Jar, string RequestId, string Relay) : IDisposable
    {
        public void Dispose() => Browser.Dispose();
    }

    private static async Task<Started> StartAsync(string root, string providerId, FakeSamlIdp idp)
    {
        CookieContainer jar = new();
        HttpClient browser = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = jar,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        using HttpResponseMessage start = await browser.GetAsync(new Uri($"{root}/rest/auth/saml/{providerId}/start?return=/rest/whoami"));
        Assert.True(start.StatusCode == HttpStatusCode.Redirect, $"Starting a sign-in answered {(int)start.StatusCode}: {await start.Content.ReadAsStringAsync()}");

        (string requestId, string relay) = idp.Read(start.Headers.Location!);
        Assert.False(string.IsNullOrEmpty(requestId), "The request sent to the provider carries no ID.");

        return new Started(browser, jar, requestId, relay);
    }

    /// <summary>Posts a response as the provider's page would; returns the status and who the session says, if any.</summary>
    private static async Task<(HttpStatusCode Status, string? Name, string Body)> PostAsync(
        string root, HttpClient browser, string response, string relay)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string> { ["SAMLResponse"] = response, ["RelayState"] = relay });
        using HttpResponseMessage posted = await browser.PostAsync(new Uri($"{root}/rest/auth/saml/acs"), form);
        string body = await posted.Content.ReadAsStringAsync();

        if (posted.StatusCode != HttpStatusCode.Redirect)
        {
            return (posted.StatusCode, null, body);
        }

        Assert.Equal("/rest/whoami", posted.Headers.Location!.OriginalString);

        using HttpResponseMessage whoami = await browser.GetAsync(new Uri($"{root}/rest/whoami"));
        string who = await whoami.Content.ReadAsStringAsync();
        return (posted.StatusCode, JsonDocument.Parse(who).RootElement.TryGetProperty("name", out JsonElement n) ? n.GetString() : null, who);
    }

    private async Task<(string Id, string EntityId)> ProviderAsync(FakeSamlIdp idp, string name, bool uploaded)
    {
        (int status, string body) = await AdminAsync(
            HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new
            {
                kind = "saml",
                name,
                metadataUrl = uploaded ? null : idp.MetadataUrl,
                metadata = uploaded ? idp.Metadata : null,
                groupsClaim = FakeSamlIdp.GroupsAttribute,
                autoCreate = true,
                defaultRole = "viewer",
            }));

        Assert.True(status == 201, $"Adding the SAML provider answered {status}: {body}");
        JsonElement made = JsonDocument.Parse(body).RootElement;
        return (made.GetProperty("id").GetString()!, made.GetProperty("clientId").GetString()!);
    }

    [Fact]
    public async Task A_signed_response_to_this_browsers_request_signs_in_and_its_groups_give_the_role()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeSamlIdp idp = new();

        (string id, string entityId) = await ProviderAsync(idp, $"zz SAML {suffix}", uploaded: false);
        string subject = $"dana{suffix}@example.org";
        string? made = null;

        try
        {
            (int mapped, string mapSaid) = await AdminAsync(HttpMethod.Put, $"/admin/identity-providers/{id}/groups",
                JsonSerializer.Serialize(new { mappings = new object[] { new { externalGroup = "GIS-Publishers", role = "publisher" } } }));
            Assert.True(mapped == 200, $"Mapping the groups answered {mapped}: {mapSaid}");

            (int checkedIt, string checkSaid) = await AdminAsync(HttpMethod.Post, $"/admin/identity-providers/{id}/check");
            Assert.True(checkedIt == 200, $"Checking the provider answered {checkedIt}: {checkSaid}");
            Assert.Contains(idp.EntityId, checkSaid, StringComparison.Ordinal);

            // What the operator gives the provider: this server's entity id, and where responses go.
            using (HttpClient anyone = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }))
            {
                string sp = await anyone.GetStringAsync(new Uri($"{root}/rest/auth/saml/{id}/metadata"));
                Assert.Contains($"entityID=\"{entityId}\"", sp, StringComparison.Ordinal);
                Assert.Contains($"Location=\"{root}/rest/auth/saml/acs\"", sp, StringComparison.Ordinal);
            }

            using (Started first = await StartAsync(root, id, idp))
            {
                (HttpStatusCode status, string? name, string body) = await PostAsync(root, first.Browser,
                    idp.Respond(first.RequestId, subject, entityId, $"{root}/rest/auth/saml/acs", ["GIS-Publishers"]), first.Relay);

                Assert.True(name is not null, $"The response did not end signed in ({(int)status}): {body}");
                made = name;
                Assert.Equal($"dana{suffix}", made);
            }

            (int _, string members) = await AdminAsync(HttpMethod.Get, "/admin/members");
            JsonElement member = JsonDocument.Parse(members).RootElement.GetProperty("members").EnumerateArray()
                .Single(m => m.GetProperty("name").GetString() == made);
            Assert.Contains("publisher", member.GetProperty("roles").ToString(), StringComparison.Ordinal);
            Assert.Equal($"zz SAML {suffix}", member.GetProperty("signsInWith").GetString());

            // The next sign-in, by the same subject, finds the same account.
            using Started second = await StartAsync(root, id, idp);
            (_, string? again, _) = await PostAsync(root, second.Browser,
                idp.Respond(second.RequestId, subject, entityId, $"{root}/rest/auth/saml/acs", ["GIS-Publishers"]), second.Relay);
            Assert.Equal(made, again);
        }
        finally
        {
            if (made is not null) await AdminAsync(HttpMethod.Delete, $"/admin/members/{made}");
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    public static TheoryData<string> Spoilt => new()
    {
        "the name changed after signing",
        "signed with another key",
        "for another audience",
        "confirmed for another address",
        "answering another request",
        "confirmed for another request",
        "unsigned",
        "a document type",
    };

    [Theory]
    [MemberData(nameof(Spoilt))]
    public async Task A_response_the_server_cannot_trust_signs_nobody_in(string spoil)
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeSamlIdp idp = new();

        // Uploaded metadata: the provider need not be reachable once the server holds its document.
        (string id, string entityId) = await ProviderAsync(idp, $"zz SAML {suffix}", uploaded: true);

        try
        {
            using Started started = await StartAsync(root, id, idp);
            string acs = $"{root}/rest/auth/saml/acs";
            string subject = $"eve{suffix}@example.org";

            string response = spoil switch
            {
                "the name changed after signing" => idp.Respond(started.RequestId, subject, entityId, acs, afterSigning: d =>
                {
                    d.GetElementsByTagName("NameID", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!.InnerText = $"admin{suffix}@example.org";
                    return d;
                }),
                "signed with another key" => idp.Respond(started.RequestId, subject, entityId, acs, signWithAnotherKey: true),
                "for another audience" => idp.Respond(started.RequestId, subject, "https://elsewhere.example.org", acs),
                "confirmed for another address" => idp.Respond(started.RequestId, subject, entityId, "https://elsewhere.example.org/acs"),
                "answering another request" => idp.Respond("_another", subject, entityId, acs),
                "confirmed for another request" => idp.Respond(started.RequestId, subject, entityId, acs, confirmFor: "_another"),
                "unsigned" => idp.Respond(started.RequestId, subject, entityId, acs, sign: false),
                "a document type" => idp.Respond(started.RequestId, subject, entityId, acs,
                    prologue: "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;\">]>"),
                _ => throw new ArgumentOutOfRangeException(nameof(spoil)),
            };

            (HttpStatusCode status, string? name, string body) = await PostAsync(root, started.Browser, response, started.Relay);

            Assert.True(name is null, $"A response {spoil} signed somebody in as {name}.");
            Assert.True(status == HttpStatusCode.Unauthorized, $"A response {spoil} answered {(int)status}: {body}");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task A_response_is_used_once_and_one_the_provider_sends_unasked_is_refused()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        using FakeSamlIdp idp = new();

        (string id, string entityId) = await ProviderAsync(idp, $"zz SAML {suffix}", uploaded: true);
        string? made = null;

        try
        {
            using Started started = await StartAsync(root, id, idp);
            string acs = $"{root}/rest/auth/saml/acs";
            string response = idp.Respond(started.RequestId, $"finn{suffix}@example.org", entityId, acs);

            // Whoever captured the response captured the browser's cookie with it: both are sent again.
            Cookie state = started.Jar.GetCookies(new Uri($"{root}/rest/auth/saml/acs")).Single(c => c.Name == "gis-saml");

            (_, made, string body) = await PostAsync(root, started.Browser, response, started.Relay);
            Assert.True(made is not null, $"The first use did not sign in: {body}");

            using (HttpClient replayer = new(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CookieContainer = new CookieContainer(),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }))
            {
                replayer.DefaultRequestHeaders.Add("Cookie", $"gis-saml={state.Value}");
                (HttpStatusCode again, string? who, _) = await PostAsync(root, replayer, response, started.Relay);
                Assert.True(who is null, $"A response used once signed in again as {who}.");
                Assert.Equal(HttpStatusCode.Unauthorized, again);
            }

            // Sent unasked — IdP-initiated — it carries no request this browser made.
            using (HttpClient stranger = new(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }))
            {
                (HttpStatusCode unasked, string? who, _) = await PostAsync(root, stranger,
                    idp.Respond(string.Empty, $"finn{suffix}@example.org", entityId, acs), string.Empty);
                Assert.True(who is null, $"A response nobody asked for signed in as {who}.");
                Assert.Equal(HttpStatusCode.BadRequest, unasked);
            }
        }
        finally
        {
            if (made is not null) await AdminAsync(HttpMethod.Delete, $"/admin/members/{made}");
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task Metadata_over_plain_http_to_another_machine_is_refused()
    {
        await RequireServerAsync();

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new { kind = "saml", name = "zz SAML plain", metadataUrl = "http://idp.example.org/metadata" }));

        Assert.Equal(400, status);
        Assert.Contains("HTTPS", body, StringComparison.Ordinal);
    }
}
