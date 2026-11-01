using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A caller with no token is challenged on the ArcGIS services surface — <c>499 Token Required</c> — for a service it
/// may not see and for one that does not exist alike; only a signed-in caller is told 404. D-347.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters, measured.</b> The ArcGIS Maps SDK asks for a portal item's service without a token first and
/// attaches the token it holds only when the answer challenges it. This server answered 404 — its deliberate <i>not
/// visible to you</i> — which challenges nothing, so an organisation-shared hosted layer never loaded in an app signed
/// in to the portal (<c>tools/sdk-probe/organisation.html</c>, SDK 4.30, 2026-10-31). With the challenge it asks again
/// with the token, loads the layer and counts its features (the same probe, 2026-11-01).
/// </para>
/// <para>
/// <b>The disclosure the 404 existed to prevent is checked here too</b>: the challenge for a service that exists and
/// is not shared, and for a name that exists nowhere, are compared byte for byte.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b> because it publishes a service of its own (D-185).
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AnAnonymousCallerIsChallengedTests : ArcGisClient
{
    private static readonly string Service = $"zz_challenge_{Guid.NewGuid():N}"[..24];

    [Fact]
    public async Task An_organisation_shared_service_challenges_a_caller_with_no_token_as_an_absent_one_does()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(string.IsNullOrEmpty(token), "The suite's account could not sign in.");

        (int published, string said) = await PublishOneAsync(Service, Service, sharing: "organization", skip: 3);
        Assert.True(published is 200 or 201, $"publishing an organisation-shared service: {published} {said}");

        try
        {
            string shared = $"/rest/services/{Service}/FeatureServer?f=json";
            string absent = $"/rest/services/zz_none_{Guid.NewGuid():N}/FeatureServer?f=json";

            // No token: the challenge an ArcGIS client signs in on — 499 in the body, 401 on the line, not stored.
            using HttpClient anonymous = new(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
            (HttpStatusCode status, string body, string? cacheControl) = await GetAsync(anonymous, root + shared);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal(499, JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal("Token Required", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.Equal("no-store", cacheControl);

            // And the same answer, to the byte, for a name that exists nowhere: nothing is learnt about which names exist.
            (HttpStatusCode absentStatus, string absentBody, _) = await GetAsync(anonymous, root + absent);
            Assert.Equal((status, body), (absentStatus, absentBody));

            // A layer and a query under it are challenged the same way.
            (HttpStatusCode layer, _, _) = await GetAsync(anonymous, root + $"/rest/services/{Service}/FeatureServer/0/query?where=1%3D1&f=json");
            Assert.Equal(HttpStatusCode.Unauthorized, layer);

            // With the token: the shared service is served, and only now is an absent one called absent.
            string with = "&token=" + Uri.EscapeDataString(token!);
            (HttpStatusCode served, string document, _) = await GetAsync(anonymous, root + shared + with);
            Assert.True(served == HttpStatusCode.OK, $"The organisation-shared service, with a token: {(int)served} {document}");
            Assert.True(JsonDocument.Parse(document).RootElement.TryGetProperty("layers", out _), document);

            (HttpStatusCode notThere, _, _) = await GetAsync(anonymous, root + absent + with);
            Assert.Equal(HttpStatusCode.NotFound, notThere);

            // The native and OGC faces keep their 404: 499 is the ArcGIS services vocabulary only.
            (HttpStatusCode ogc, _) = await AnonymousAsync($"/ogc/features/v1/collections/{Service}");
            Assert.Equal(HttpStatusCode.NotFound, ogc);
        }
        finally
        {
            await UnpublishAsync(Service, Service);
        }
    }

    private static async Task<(HttpStatusCode Status, string Body, string? CacheControl)> GetAsync(HttpClient http, string url)
    {
        using HttpResponseMessage response = await http.GetAsync(new Uri(url));
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.CacheControl?.ToString());
    }
}
