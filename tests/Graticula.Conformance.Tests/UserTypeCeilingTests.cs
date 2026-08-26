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
/// The user type's ceiling, over HTTP, against a running server.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-15](../../docs/architecture-debt.md): the ceiling was enforced and unreachable.</b>
/// `Authorization.Resolve` intersects the role's grants with the user type's ceiling, and when
/// the row was written nothing in the HTTP surface required a privilege — the admin API did not
/// exist yet. The row says so plainly: *covered by unit tests and has never run against a
/// request*, with a failure mode of **silent over-permission**, which unit tests can demonstrate
/// and only a live path can disprove.
/// </para>
/// <para>
/// <b>The trigger the row names — *the first admin endpoint* — passed some time ago.</b> This
/// is the walk it was waiting for.
/// </para>
/// <para>
/// <b>What makes it a ceiling and not a missing grant is the refusal's own words.</b> A member
/// with no role and a member whose role is withheld by their type are both refused, and telling
/// them apart is the whole point: one is fixed by granting a role and the other is not. The
/// server says which, and this asserts that it says which.
/// </para>
/// </remarks>
public sealed class UserTypeCeilingTests : ArcGisClient
{
    private const string Probe = "zz_d15_ceiling";

    /// <summary>
    /// A role that grants publishing is withheld from a viewer, and the refusal says so.
    /// </summary>
    /// <remarks>
    /// <b>`publisher` carries `content:publishFeatures`; the `viewer` ceiling is empty.</b> So
    /// this member holds the grant and cannot use it, which is exactly the state the ceiling
    /// exists to produce and the state a silent over-permission would erase.
    /// </remarks>
    [Fact]
    public async Task A_privilege_a_role_grants_is_withheld_by_the_user_type()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs an administrator's token");

        await RemoveAsync(root, token!);

        try
        {
            string mine = await MakeAndSignInAsync(root, token!, "publisher", "viewer");

            (HttpStatusCode status, string body) = await PublishAsync(root, mine);

            Assert.Equal(HttpStatusCode.Forbidden, status);

            // <b>The distinction, not just the refusal.</b> *Ask an administrator to grant a role*
            // would be advice that cannot work: the role is already granted.
            Assert.Contains("user type", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Granting the role again will not help", body, StringComparison.Ordinal);
        }
        finally
        {
            await RemoveAsync(root, token!);
        }
    }

    /// <summary>
    /// The same role under a type that permits it is not refused for the privilege.
    /// </summary>
    /// <remarks>
    /// <b>The control, and it is what turns the test above into evidence.</b> Without it, a server
    /// that refused every publish would pass the first assertion and mean nothing. The request
    /// still fails — its data source does not exist — and what is asserted is that it fails for
    /// that reason instead of for the ceiling.
    /// </remarks>
    [Fact]
    public async Task The_same_role_under_a_type_that_permits_it_reaches_the_endpoint()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "this test needs an administrator's token");

        await RemoveAsync(root, token!);

        try
        {
            string mine = await MakeAndSignInAsync(root, token!, "publisher", "creator");

            (HttpStatusCode status, string body) = await PublishAsync(root, mine);

            Assert.True(
                status != HttpStatusCode.Forbidden,
                $"a publisher whose user type permits publishing was refused the privilege: {body}");

            Assert.DoesNotContain("user type", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await RemoveAsync(root, token!);
        }
    }

    /// <summary>A publish that reaches the privilege check and can create nothing.</summary>
    /// <remarks>
    /// <b>The data source is deliberately absent.</b> Privilege is decided before the table is
    /// looked at, so this request reaches the check under test and cannot create anything
    /// whichever way it goes.
    /// </remarks>
    private Task<(HttpStatusCode Status, string Body)> PublishAsync(string root, string token) =>
        AdminAsync(
            root, token, HttpMethod.Post, "/admin/layers",
            JsonSerializer.Serialize(new
            {
                name = "zz_d15_layer",
                dataSourceId = Guid.Empty,
                schemaName = "public",
                tableName = "zz_d15_no_such_table",
                geometryColumn = "geom",
                identityColumn = "id",
                srid = 4326,
                geometryType = "Point",
                sharing = "private",
                serviceName = "zz_d15_service",
            }));

    /// <summary>Makes a member with this role and type, and signs in as them.</summary>
    private async Task<string> MakeAndSignInAsync(
        string root, string token, string role, string userType)
    {
        (HttpStatusCode created, string body) = await AdminAsync(
            root, token, HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name = Probe, role, userType }));

        Assert.True(
            created is HttpStatusCode.OK or HttpStatusCode.Created,
            $"creating the probe member answered {(int)created}: {body}");

        string issued = JsonDocument.Parse(body).RootElement.GetProperty("password").GetString()!;
        string wanted = $"Zz-{Guid.NewGuid():N}-1";

        (string? first, string firstWhy) = await SignInAsync(root, issued);

        Assert.True(
            first is not null,
            $"the probe could not sign in with the password it was given. {firstWhy}");

        using (HttpRequestMessage change = new(HttpMethod.Post, $"{root}/rest/auth/password"))
        {
            change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
            change.Content = new StringContent(
                JsonSerializer.Serialize(new { currentPassword = issued, newPassword = wanted }),
                Encoding.UTF8, "application/json");

            using HttpResponseMessage changed = await Http.SendAsync(change);

            Assert.True(
                changed.IsSuccessStatusCode,
                "the probe could not set its own password: "
                + await changed.Content.ReadAsStringAsync());
        }

        (string? mine, string mineWhy) = await SignInAsync(root, wanted);

        Assert.True(
            mine is not null,
            $"the probe could not sign in with its own password. {mineWhy}");

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
            JsonSerializer.Serialize(new { name = Probe, password }),
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

    private async Task RemoveAsync(string root, string token) =>
        await AdminAsync(
            root, token, HttpMethod.Delete, $"/admin/members/{Probe}?deleteOwned=true", null);

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
