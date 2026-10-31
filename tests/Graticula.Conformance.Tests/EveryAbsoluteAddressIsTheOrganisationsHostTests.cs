using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Every absolute address a document carries names the host it was asked at, under a second organisation's host as
/// under the main one's — ADR-204 condition 3; and the three clients that sign in through OAuth can do so there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read as a scan, not as a list of fields.</b> Each document is fetched at the organisation's host and every
/// <c>http(s)://authority</c> in it is read; none may be the main organisation's. A field added later that builds its
/// address from configuration rather than from the request is caught without anybody naming it. The seven addresses
/// §1b found pinned — <c>/rest/info</c>'s <c>tokenServicesUrl</c>, the portal's servers, the pro-admin
/// <c>owningSystemUrl</c>, the OGC documents, the token exchange's origin — are among the documents read.
/// </para>
/// <para>
/// <b>The control</b> is the same scan at the main host, where the documents that name an address must name that
/// host: a scan that found no addresses at all would pass the first half and prove nothing.
/// </para>
/// <para>
/// <b>And the sign-in pages</b>, which a scan of addresses does not reach and which were broken at a second
/// organisation (migration 115): ArcGIS Pro, the ArcGIS API for Python and Field Maps each open
/// <c>oauth2/authorize</c> with a built-in client id, and the Python API reads the page's <c>oAuthInfo</c> by taking
/// the first braces in it.
/// </para>
/// </remarks>
[Collection("server settings")]
public sealed class EveryAbsoluteAddressIsTheOrganisationsHostTests : ArcGisClient
{
    private static readonly Regex Address = new(@"https?://([A-Za-z0-9.\-_\[\]:]+)", RegexOptions.Compiled);

    private string _root = string.Empty;

    [Fact]
    public async Task A_second_organisations_documents_name_its_own_host_and_its_clients_can_sign_in()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        Uri root = new(_root);

        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"ab{run}", host = $"ab{run}.localhost";
        string layer = $"zz_abs_{run}";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", null, admin, new
        {
            key, name = $"Addresses {run}", hosts = new[] { host }, administrator = $"abadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            // Condition 9: no second organisation over a role the floor does not bind.
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}: {tenant}");

        try
        {
            (string owner, string ownPassword) = await SettleAsync($"abadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);

            (HttpStatusCode defined, JsonElement design) = await SendAsync(HttpMethod.Post, "/admin/hosted/define", host, owner, new
            {
                name = layer, geometryType = "point", fields = new[] { new { name = "label", type = "Text" } }, sharing = "public",
            });
            Assert.True(defined == HttpStatusCode.Created, $"Defining the layer answered {(int)defined}: {design}");

            (_, string portal) = await GetTextAsync("/sharing/rest/portals/self?f=json", host, owner);
            string portalId = JsonDocument.Parse(portal).RootElement.GetProperty("id").GetString()!;
            (_, string search) = await GetTextAsync($"/sharing/rest/search?q={layer}&f=json", host, owner);
            string itemId = JsonDocument.Parse(search).RootElement.GetProperty("results").EnumerateArray().First().GetProperty("id").GetString()!;

            // The documents that carry this server's own address, each asked at the organisation's host.
            string[] naming =
            [
                "/rest/info?f=json",
                "/sharing/rest?f=json",
                "/sharing/rest/portals/self?f=json",
                $"/sharing/rest/portals/{portalId}/servers?f=json",
                $"/sharing/rest/search?q={layer}&f=json",
                $"/sharing/rest/content/items/{itemId}?f=json",
                "/pro-admin/info?f=json",
                "/ogc/features/v1?f=json",
                "/ogc/features/v1/collections?f=json",
                "/ogc/features/v1/api?f=json",
                "/ogc/tiles/v1?f=json",
                "/ogc/records/v1?f=json",
                "/ogc/styles/v1?f=json",
                "/wfs?service=WFS&version=2.0.0&request=GetCapabilities",
                "/wms?service=WMS&version=1.3.0&request=GetCapabilities",
                "/wmts?service=WMTS&request=GetCapabilities",
            ];

            // And documents that may carry one, read for any that is not the organisation's.
            string[] others =
            [
                "/rest/services?f=json",
                "/rest/services?f=html",
                $"/rest/services/hosted/{layer}/FeatureServer?f=json",
                $"/rest/services/hosted/{layer}/FeatureServer/0?f=json",
                $"/rest/services/hosted/{layer}/FeatureServer?f=html",
                $"/rest/services/hosted/{layer}/MapServer?f=json",
                $"/rest/services/hosted/{layer}/VectorTileServer?f=json",
                $"/rest/services/hosted/{layer}/VectorTileServer/resources/styles/root.json",
                "/sharing/rest/community/self?f=json",
                $"/sharing/rest/content/items/{itemId}/data?f=json",
            ];

            List<string> wrong = [];

            foreach (string path in naming.Concat(others))
            {
                (HttpStatusCode status, string body) = await GetTextAsync(path, host, owner);
                Assert.True(status == HttpStatusCode.OK, $"{path} at {host} answered {(int)status}: {body[..Math.Min(300, body.Length)]}");

                string[] authorities = [.. Address.Matches(body).Select(m => m.Groups[1].Value).Distinct()];
                wrong.AddRange(authorities
                    .Where(a => a.Equals(root.Authority, StringComparison.OrdinalIgnoreCase)
                        || a.Split(':')[0].Equals(root.Host, StringComparison.OrdinalIgnoreCase))
                    .Select(a => $"{path} names {a}"));

                if (naming.Contains(path))
                {
                    Assert.True(authorities.Any(a => a.Split(':')[0].Equals(host, StringComparison.OrdinalIgnoreCase)),
                        $"{path} at {host} names no address of its own, so there is nothing to check: {string.Join(", ", authorities)}");
                }
            }

            Assert.True(wrong.Count == 0, "Addresses of the main organisation in a second organisation's documents: " + string.Join("; ", wrong));

            // The control: at the main host the same documents name the main host.
            foreach (string path in naming.Where(p => !p.Contains(portalId, StringComparison.Ordinal) && !p.Contains(itemId, StringComparison.Ordinal)
                && !p.Contains(layer, StringComparison.Ordinal)))
            {
                (_, string body) = await GetTextAsync(path, null, admin);
                Assert.True(Address.Matches(body).Any(m => m.Groups[1].Value.Split(':')[0].Equals(root.Host, StringComparison.OrdinalIgnoreCase)),
                    $"{path} at the main host does not name it; the scan above would have found nothing either.");
            }

            // The token exchange takes a server of this host and refuses the main one's.
            Assert.Equal(HttpStatusCode.OK, await ExchangeAsync($"https://{host}", host, owner));
            Assert.Equal(HttpStatusCode.BadRequest, await ExchangeAsync($"{root.Scheme}://{root.Authority}", host, owner));

            // Pro, the Python API and Field Maps sign in through OAuth with a client id they carry.
            foreach (string client in new[] { "arcgisprodesktop", "pythonapi", "fieldmaps" })
            {
                (HttpStatusCode opened, string page) = await GetTextAsync(
                    $"/sharing/rest/oauth2/authorize?client_id={client}&response_type=code&redirect_uri=urn%3Aietf%3Awg%3Aoauth%3A2.0%3Aoob&expiration=-1",
                    host, null);
                Assert.True(opened == HttpStatusCode.OK, $"{client}'s sign-in at {host} answered {(int)opened}.");

                // As the Python API reads it: the first braces of the page, parsed.
                string first = Regex.Match(page, @"\{.*\}").Value;
                Assert.True(TryParse(first, out JsonElement info) && info.TryGetProperty("oauth_state", out _),
                    $"{client}'s sign-in page at {host} does not begin with oAuthInfo: {first[..Math.Min(120, first.Length)]}");
            }

            // The Python API's password sign-in under /studio, the portal address Pro must be given (ADR-191), with the
            // cookies a client keeps by path: the form's cookie must come back to /studio/sharing/oauth2/signin.
            using HttpClientHandler handler = new()
            {
                AllowAutoRedirect = false,
                CookieContainer = new CookieContainer(),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using HttpClient python = new(handler);

            using HttpRequestMessage open = new(HttpMethod.Get,
                $"{_root}/studio/sharing/rest/oauth2/authorize?client_id=pythonapi&response_type=code"
                + "&redirect_uri=urn%3Aietf%3Awg%3Aoauth%3A2.0%3Aoob&expiration=20160&allow_verification=false");
            open.Headers.Host = host;
            using HttpResponseMessage opened2 = await python.SendAsync(open);
            string signInPage = await opened2.Content.ReadAsStringAsync();
            Assert.True(TryParse(Regex.Match(signInPage, @"\{.*\}").Value, out JsonElement state),
                $"The Python API's sign-in page under /studio answered {(int)opened2.StatusCode} without oAuthInfo first.");

            using HttpRequestMessage signIn = new(HttpMethod.Post, $"{_root}/studio/sharing/oauth2/signin")
            {
                Content = new FormUrlEncodedContent([
                    new("oauth_state", state.GetProperty("oauth_state").GetString()!),
                    new("username", $"abadmin{run}"),
                    new("password", ownPassword),
                ]),
            };
            signIn.Headers.Host = host;
            using HttpResponseMessage signedIn = await python.SendAsync(signIn);
            Assert.True(signedIn.StatusCode == HttpStatusCode.Redirect,
                $"The Python API's password sign-in under /studio at {host} answered {(int)signedIn.StatusCode}.");
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", null, admin);
        }
    }

    private static bool TryParse(string text, out JsonElement element)
    {
        try
        {
            element = JsonDocument.Parse(text).RootElement.Clone();
            return element.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private async Task<HttpStatusCode> ExchangeAsync(string serverUrl, string host, string token)
    {
        using HttpRequestMessage request = Request(HttpMethod.Post, "/rest/generateToken", host, null);
        request.Content = new FormUrlEncodedContent([new("f", "json"), new("serverUrl", serverUrl), new("token", token)]);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return response.StatusCode;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? host, string? token)
    {
        HttpRequestMessage request = new(method, $"{_root}{path}");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? host, string? token, object? json = null)
    {
        using HttpRequestMessage request = Request(method, path, host, token);
        request.Content = json is null ? null : JsonContent.Create(json);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(HttpStatusCode Status, string Body)> GetTextAsync(string path, string? host, string? token)
    {
        using HttpRequestMessage request = Request(HttpMethod.Get, path, host, token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(string Token, string Password)> SettleAsync(string name, string issued, string? host)
    {
        (_, JsonElement first) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, null, new { name, password = issued });
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first.GetProperty("token").GetString(),
            new { currentPassword = issued, newPassword = own });
        (_, JsonElement again) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, null, new { name, password = own });
        return (again.GetProperty("token").GetString()!, own);
    }
}
