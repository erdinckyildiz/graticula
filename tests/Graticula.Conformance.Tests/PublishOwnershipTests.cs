using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Whose service a publish may add a layer to.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-104](../../docs/architecture-debt.md): a publish could modify a service the caller does
/// not own.</b> `PublishLayerAsync` puts the layer into the existing service when the name and
/// folder match — the mechanism that lets three layers share one service, working as designed —
/// and it never asked whose service that was. So `serviceName` set to a stranger's service added
/// a layer to it, and the caller needed only `content:publishFeatures`.
/// </para>
/// <para>
/// <b>The hole was narrow and it was still a hole.</b> The layer arrives with its own sharing
/// scope and its own owner, and the container's scope is explicitly not reset, so this was never
/// a way to read anything. It was a way to put a layer inside a container somebody else is
/// answerable for.
/// </para>
/// <para>
/// <b>Staged with a member this file creates and removes, and with a source that does not
/// exist.</b> The refusal is decided before the table is looked at, so proving it needs no table
/// — and a test that published a real layer into somebody's service to prove it cannot would be
/// a test that does the thing it is checking against.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class PublishOwnershipTests : ArcGisClient
{
    private const string Publisher = "zz_d104_publisher";

    /// <summary>The password this run gives its probe publisher.</summary>
    /// <remarks>
    /// <b>Made per run rather than written down.</b> A literal here is a known password for an
    /// account that exists on whatever server the suite is pointed at — briefly, and still known.
    /// The account is created and removed inside one test; the password does not need to outlive
    /// the process, so it does not.
    /// </remarks>
    private static readonly string Password = $"Zz-{Guid.NewGuid():N}-1";

    /// <summary>A publisher cannot add a layer to a service somebody else owns.</summary>
    [Fact]
    public async Task Publishing_into_a_service_somebody_else_owns_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "these tests need an administrator's token");

        (string service, string? folder) = await SomebodyElsesServiceAsync(root, token!);

        await RemoveAsync(root, token!);

        try
        {
            string mine = await SignInAsPublisherAsync(root, token!);

            (HttpStatusCode status, string body) = await PublishAsync(root, mine, service, folder);

            Assert.True(
                status == HttpStatusCode.Forbidden,
                $"publishing into '{service}' as somebody who does not own it answered "
                + $"{(int)status}: {body}");

            Assert.Contains("belongs to somebody else", body, StringComparison.Ordinal);
        }
        finally
        {
            await RemoveAsync(root, token!);
        }
    }

    /// <summary>
    /// The owner of the service is not refused, and neither is an administrator.
    /// </summary>
    /// <remarks>
    /// <b>The half a guard gets wrong.</b> A check that refuses everybody is as broken as one
    /// that refuses nobody, and it fails quietly: publishing a second layer into your own service
    /// simply stops working. The request here still fails — its data source does not exist — and
    /// what is asserted is that it fails for that reason and not for ownership.
    /// </remarks>
    [Fact]
    public async Task The_owner_and_an_administrator_are_not_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "these tests need an administrator's token");

        (string service, string? folder) = await SomebodyElsesServiceAsync(root, token!);

        (HttpStatusCode status, string body) = await PublishAsync(root, token!, service, folder);

        Assert.True(
            status != HttpStatusCode.Forbidden,
            $"an administrator publishing into '{service}' was refused as though it belonged to "
            + $"somebody else: {body}");

        Assert.DoesNotContain("belongs to somebody else", body, StringComparison.Ordinal);
    }

    /// <summary>A service with an owner, so the comparison has something to compare.</summary>
    private async Task<(string Name, string? Folder)> SomebodyElsesServiceAsync(
        string root, string token)
    {
        JsonElement listing = JsonDocument.Parse(
                (await AdminAsync(root, token, HttpMethod.Get, "/admin/featureservices", null)).Body)
            .RootElement;

        foreach (JsonElement service in listing.GetProperty("services").EnumerateArray())
        {
            string name = service.GetProperty("name").GetString()!;

            if (name.Contains("zz_", StringComparison.Ordinal))
            {
                continue;
            }

            string? folder = service.TryGetProperty("folder", out JsonElement f)
                ? f.GetString()
                : null;

            return (name, folder);
        }

        Assert.Fail("this server publishes no feature service, so there is nothing to publish into");
        return ("", null);
    }

    /// <summary>Makes a publisher and signs in as them.</summary>
    /// <remarks>
    /// <b>Two passwords, because the server issues the first one and will not take orders about
    /// it.</b> Creating a member returns a generated password marked *must change*, which does
    /// nothing except set its own replacement. So this signs in with it, replaces it, and signs
    /// in again — which is exactly the path a real new member walks.
    /// </remarks>
    private async Task<string> SignInAsPublisherAsync(string root, string token)
    {
        (HttpStatusCode created, string body) = await AdminAsync(
            root, token, HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(
                new { name = Publisher, role = "publisher", userType = "creator" }));

        Assert.True(
            created is HttpStatusCode.OK or HttpStatusCode.Created,
            $"creating the probe publisher answered {(int)created}: {body}");

        string issued = JsonDocument.Parse(body).RootElement.GetProperty("password").GetString()!;

        (string? first, string firstWhy) = await SignInAsync(root, issued);

        Assert.True(
            first is not null,
            $"the probe publisher could not sign in with what it was given. {firstWhy}");

        using (HttpRequestMessage change = new(HttpMethod.Post, $"{root}/rest/auth/password"))
        {
            change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
            change.Content = new StringContent(
                JsonSerializer.Serialize(new { currentPassword = issued, newPassword = Password }),
                Encoding.UTF8, "application/json");

            using HttpResponseMessage changed = await Http.SendAsync(change);

            Assert.True(
                changed.IsSuccessStatusCode,
                $"the probe publisher could not set its own password: "
                + await changed.Content.ReadAsStringAsync());
        }

        (string? mine, string mineWhy) = await SignInAsync(root, Password);

        Assert.True(
            mine is not null,
            $"the probe publisher could not sign in with its own password. {mineWhy}");

        return mine!;
    }

    /// <summary>Signs the probe in, and says why when it cannot.</summary>
    /// <param name="root">The server.</param>
    /// <param name="password">The password to try.</param>
    /// <returns>The token, or null with the reason beside it.</returns>
    /// <remarks>
    /// <b>[D-177](../../docs/architecture-debt.md).</b> This returned a bare null on any
    /// refusal, so every caller's assertion could say only *could not sign in*. **401 and 429
    /// are different problems**: the second is this server's per-address throttle, which a
    /// suite signing in from one address can reach on its own, and it needs a different repair
    /// from a wrong password. `AFailureSaysWhyTests` is the check that keeps this shape from
    /// coming back.
    /// </remarks>
    private async Task<(string? Token, string Why)> SignInAsync(string root, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/login");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { name = Publisher, password }),
            Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return (null, $"Signing in returned {(int)response.StatusCode}. " + Explain(body));
        }

        return JsonDocument.Parse(body)
            .RootElement.TryGetProperty("token", out JsonElement issued)
            ? (issued.GetString(), string.Empty)
            : (null, "Signing in returned 200 with no token in it. " + Explain(body));
    }

    /// <summary>The server's own words, trimmed for a failure message.</summary>
    private static string Explain(string body) =>
        string.IsNullOrWhiteSpace(body)
            ? "The response had no body to explain it."
            : "The server said: " + (body.Length <= 300 ? body : body[..300] + "…");

    /// <summary>
    /// A publish naming an existing service and a data source that does not exist.
    /// </summary>
    /// <remarks>
    /// <b>The source is deliberately absent.</b> Ownership is decided before the table is looked
    /// at, so this request reaches the refusal under test and can never create anything —
    /// whichever way the check goes.
    /// </remarks>
    private Task<(HttpStatusCode Status, string Body)> PublishAsync(
        string root, string token, string service, string? folder)
    {
        (string _, string bare) = Split(service);

        return AdminAsync(
            root, token, HttpMethod.Post, "/admin/layers",
            JsonSerializer.Serialize(new
            {
                name = "zz_d104_layer",
                dataSourceId = Guid.Empty,
                schemaName = "public",
                tableName = "zz_d104_no_such_table",
                geometryColumn = "geom",
                identityColumn = "id",
                srid = 4326,
                geometryType = "Point",
                sharing = "private",
                serviceName = bare,
                folder,
            }));
    }

    private static (string Folder, string Name) Split(string qualified)
    {
        int cut = qualified.IndexOf('/', StringComparison.Ordinal);

        return cut < 0 ? ("", qualified) : (qualified[..cut], qualified[(cut + 1)..]);
    }

    private async Task RemoveAsync(string root, string token) =>
        await AdminAsync(
            root, token, HttpMethod.Delete, $"/admin/members/{Publisher}?deleteOwned=true", null);

    private async Task<(HttpStatusCode Status, string Body)> AdminAsync(
        string root, string token, HttpMethod method, string path, string? body)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
