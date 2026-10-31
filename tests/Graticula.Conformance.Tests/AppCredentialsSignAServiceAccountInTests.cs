using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A script holding a service account's client secret signs in the way ArcGIS's app credentials do — POST
/// <c>oauth2/token</c>, <c>grant_type=client_credentials</c> — and reads what its account may; a revoked secret and a
/// disabled account get nothing, and revoking the secret ends the tokens it bought (ADR-200 condition 2).
/// </summary>
[Collection("catalogue walk")]
public sealed class AppCredentialsSignAServiceAccountInTests : ArcGisClient
{
    private const string Account = "zz_app_machine";
    private const string Service = "zz_app_private";
    private const string Layer = "zz_app_private_layer";
    private static readonly string[] UserRole = ["user"];

    [Fact]
    public async Task A_client_secret_buys_a_token_that_reads_a_private_layer_until_it_is_revoked()
    {
        string root = await RequireServerAsync();

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/service-accounts",
                JsonSerializer.Serialize(new { name = Account, roles = UserRole }));
            Assert.True(made == 201, $"making the account: {made} {madeBody}");

            (int issued, string issuedBody) = await AdminAsync(HttpMethod.Post, $"/admin/service-accounts/{Account}/secrets",
                JsonSerializer.Serialize(new { name = "conformance", expiresInDays = 1 }));
            Assert.True(issued == 201, $"issuing a secret: {issued} {issuedBody}");
            JsonElement made1 = JsonDocument.Parse(issuedBody).RootElement;
            string secret = made1.GetProperty("clientSecret").GetString()!;
            Assert.StartsWith("gcs_", secret, StringComparison.Ordinal);
            Assert.Equal(Account, made1.GetProperty("clientId").GetString());

            // Listed without its text, and not among the keys.
            (int listed, string listBody) = await AdminAsync(HttpMethod.Get, $"/admin/service-accounts/{Account}/secrets");
            Assert.Equal(200, listed);
            Assert.DoesNotContain(secret, listBody, StringComparison.Ordinal);
            Assert.Contains("conformance", listBody, StringComparison.Ordinal);
            (_, string keysBody) = await AdminAsync(HttpMethod.Get, $"/admin/service-accounts/{Account}/keys");
            Assert.DoesNotContain("conformance", keysBody, StringComparison.Ordinal);

            (int published, string publishBody) = await PublishOrganisationLayerAsync();
            Assert.True(published is 200 or 201, $"publishing: {published} {publishBody}");

            string query = $"/rest/services/{Service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true";

            (_, string anonymous) = await AnonymousAsync(query);
            Assert.DoesNotContain("\"count\"", anonymous, StringComparison.Ordinal);

            // The secret is exchanged, never sent as a token itself.
            (_, string secretAsToken) = await AnonymousAsync($"{query}&token={Uri.EscapeDataString(secret)}");
            Assert.DoesNotContain("\"count\"", secretAsToken, StringComparison.Ordinal);

            (HttpStatusCode tokenStatus, string tokenBody) = await AppTokenAsync(root, Account, secret);
            Assert.True(tokenStatus == HttpStatusCode.OK, $"client_credentials: {tokenStatus} {tokenBody}");
            JsonElement answer = JsonDocument.Parse(tokenBody).RootElement;
            string token = answer.GetProperty("access_token").GetString()!;
            long expiresIn = answer.GetProperty("expires_in").GetInt64();
            Assert.InRange(expiresIn, 60 * 60, 2 * 60 * 60);
            Assert.False(answer.TryGetProperty("refresh_token", out _), "client_credentials issued a refresh token");

            (HttpStatusCode readStatus, string read) = await AnonymousAsync($"{query}&token={Uri.EscapeDataString(token)}");
            Assert.True(readStatus == HttpStatusCode.OK && read.Contains("\"count\"", StringComparison.Ordinal),
                $"the app token did not read the organisation's layer: {readStatus} {read}");

            (_, string who) = await AnonymousAsync($"/rest/whoami?token={Uri.EscapeDataString(token)}");
            JsonElement whoami = JsonDocument.Parse(who).RootElement;
            Assert.Equal(Account, whoami.GetProperty("name").GetString());
            Assert.Equal("Service", whoami.GetProperty("kind").GetString());

            // Not the native administration API, as a key's token.
            (HttpStatusCode adminStatus, _) = await AnonymousAsync($"/admin/service-accounts?token={Uri.EscapeDataString(token)}");
            Assert.True(adminStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"the admin API answered {adminStatus}");

            // A wrong secret is one answer.
            (HttpStatusCode wrongStatus, string wrong) = await AppTokenAsync(root, Account, "gcs_" + new string('x', 43));
            Assert.Equal(HttpStatusCode.BadRequest, wrongStatus);
            Assert.Contains("invalid_client", wrong, StringComparison.Ordinal);

            // A disabled account gets no token, and the token it holds stops.
            (int off, _) = await AdminAsync(HttpMethod.Put, $"/admin/service-accounts/{Account}/state",
                JsonSerializer.Serialize(new { disabled = true }));
            Assert.Equal(200, off);
            (HttpStatusCode disabledStatus, string disabled) = await AppTokenAsync(root, Account, secret);
            Assert.True(disabledStatus == HttpStatusCode.BadRequest && disabled.Contains("invalid_client", StringComparison.Ordinal),
                $"a disabled account was issued a token: {disabledStatus} {disabled}");
            (_, string whileOff) = await AnonymousAsync($"{query}&token={Uri.EscapeDataString(token)}");
            Assert.DoesNotContain("\"count\"", whileOff, StringComparison.Ordinal);

            (int on, _) = await AdminAsync(HttpMethod.Put, $"/admin/service-accounts/{Account}/state",
                JsonSerializer.Serialize(new { disabled = false }));
            Assert.Equal(200, on);

            (_, string again) = await AppTokenAsync(root, Account, secret);
            string second = JsonDocument.Parse(again).RootElement.GetProperty("access_token").GetString()!;

            // Revoked: no new token, and the one it bought is ended.
            (int revoked, string revokedBody) = await AdminAsync(HttpMethod.Delete,
                $"/admin/service-accounts/{Account}/secrets/{made1.GetProperty("id").GetString()}");
            Assert.True(revoked == 204, $"revoking: {revoked} {revokedBody}");

            (HttpStatusCode afterStatus, string after) = await AppTokenAsync(root, Account, secret);
            Assert.True(afterStatus == HttpStatusCode.BadRequest && after.Contains("invalid_client", StringComparison.Ordinal),
                $"a revoked secret was issued a token: {afterStatus} {after}");

            (_, string afterRevoke) = await AnonymousAsync($"{query}&token={Uri.EscapeDataString(second)}");
            Assert.DoesNotContain("\"count\"", afterRevoke, StringComparison.Ordinal);
        }
        finally
        {
            await UnpublishAsync(Service, Layer);
            await AdminAsync(HttpMethod.Delete, $"/admin/service-accounts/{Account}");
        }
    }

    /// <summary>A free table published as an organisation's layer with an object id, so the ArcGIS face serves it.</summary>
    private async Task<(int Status, string Body)> PublishOrganisationLayerAsync()
    {
        if (await DatastoreIdAsync() is not { Length: > 0 } datastore || await FreeTableAsync() is not { } table)
        {
            return (0, "the fixture has no datastore or no free table");
        }

        string json = JsonSerializer.Serialize(new
        {
            name = Service,
            sharing = "organization",
            nodes = new[]
            {
                new
                {
                    layer = new
                    {
                        name = Layer,
                        dataSourceId = datastore,
                        schemaName = table.Schema,
                        tableName = table.Table,
                        geometryColumn = table.Geometry,
                        geometryType = table.Type,
                        identityColumn = table.Identity,
                        objectIdColumn = table.Identity,
                        srid = table.Srid,
                    },
                },
            },
        });

        return await AdminAsync(HttpMethod.Post, "/admin/publish", json);
    }

    private async Task<(HttpStatusCode Status, string Body)> AppTokenAsync(string root, string clientId, string secret)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["f"] = "json",
        });

        using HttpResponseMessage response = await Http.PostAsync(new Uri($"{root}/sharing/rest/oauth2/token"), form);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
