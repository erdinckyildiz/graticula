using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A member's user type can be changed after they exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-018's privilege table has said <c>admin:manageRoles</c> grants *roles and user types*
/// since it was written, and until 2026-09-05 only the first half existed.</b> A type was chosen
/// when the member was created and fixed for the life of the account: a viewer who became a
/// publisher had to be deleted and made again, which is a different account with different
/// content ownership wearing the same name. The owner asked how to change one, read the Members
/// screen, and found a column of plain text beside a column of controls.
/// </para>
/// <para>
/// <b>What this suite asserts is the endpoint and the separation, not the ceiling itself.</b>
/// That a lowered ceiling actually withdraws privileges is <c>UserTypeCeilingTests</c>, which is
/// what discharged ADR-018's own condition on §1. Repeating it here would be a second copy of a
/// proof; what is new is that the ceiling can be moved at all, that the move is recorded from
/// and to, and that it leaves the roles exactly where they were.
/// </para>
/// </remarks>
public sealed class MemberUserTypeTests : ArcGisClient
{
    /// <summary>The probe, named so a stray one is obviously ours and obviously disposable.</summary>
    private const string Probe = "zz_usertype_probe";

    /// <summary>
    /// The ceiling moves, says what it moved from, and leaves the role alone.
    /// </summary>
    /// <remarks>
    /// <b>The role assertion is the point rather than a bonus.</b> ADR-018 §1 makes what a member
    /// may do the intersection of two independent facts, and an endpoint that quietly adjusted the
    /// role to match the new ceiling would make them one fact — which reads as helpful and means
    /// an administrator can no longer say what they granted.
    /// </remarks>
    [Fact]
    public async Task A_members_user_type_can_be_changed_and_the_role_stays_where_it_was()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "these tests need an administrator's token");

        await RemoveAsync(root, token!);

        try
        {
            (HttpStatusCode created, string made) = await AdminAsync(
                root, token!, HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = Probe, role = "user", userType = "creator" }));

            Assert.True(
                created is HttpStatusCode.OK or HttpStatusCode.Created,
                $"creating the probe member answered {(int)created}: {made}");

            Assert.Equal("creator", await UserTypeAsync(root, token!));

            (HttpStatusCode moved, string said) = await AdminAsync(
                root, token!, HttpMethod.Put, $"/admin/members/{Probe}/usertype",
                """{"userType":"viewer"}""");

            Assert.True(
                moved == HttpStatusCode.OK,
                $"lowering the probe's user type answered {(int)moved}: {said}");

            using (JsonDocument answer = JsonDocument.Parse(said))
            {
                // <b>From and to, because an audit row that cannot say what changed is a row that
                // records that something did.</b> The old value is read out of the same statement
                // that writes the new one — a subquery in `returning` would report the new value
                // twice and the change would read as *creator to creator*.
                Assert.Equal("creator", answer.RootElement.GetProperty("from").GetString());
                Assert.Equal("viewer", answer.RootElement.GetProperty("to").GetString());
            }

            Assert.Equal("viewer", await UserTypeAsync(root, token!));
            Assert.Equal("user", await RoleAsync(root, token!));

            // <b>And back up, because a one-way control is a trap.</b> Raising a ceiling grants
            // nothing by itself — the roles still decide — so this must be as ordinary as
            // lowering it was.
            (HttpStatusCode raised, string back) = await AdminAsync(
                root, token!, HttpMethod.Put, $"/admin/members/{Probe}/usertype",
                """{"userType":"unrestricted"}""");

            Assert.True(
                raised == HttpStatusCode.OK,
                $"raising the probe's user type answered {(int)raised}: {back}");

            Assert.Equal("unrestricted", await UserTypeAsync(root, token!));
        }
        finally
        {
            await RemoveAsync(root, token!);
        }
    }

    /// <summary>
    /// A type that is not one is refused with the list of the ones that are.
    /// </summary>
    /// <remarks>
    /// <b>Because the alternative is a ceiling nothing recognises.</b>
    /// <c>UserTypes.CeilingOf</c> answers an unknown type with the empty set — a clamp rather than
    /// a hole, which is the right failure — but a member left holding one would have every
    /// privilege withdrawn by a typo, and nothing on any screen would say why.
    /// </remarks>
    [Fact]
    public async Task A_user_type_that_is_not_one_is_refused_with_the_list()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "these tests need an administrator's token");

        await RemoveAsync(root, token!);

        try
        {
            (HttpStatusCode created, string made) = await AdminAsync(
                root, token!, HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = Probe, role = "user", userType = "creator" }));

            Assert.True(
                created is HttpStatusCode.OK or HttpStatusCode.Created,
                $"creating the probe member answered {(int)created}: {made}");

            (HttpStatusCode refused, string why) = await AdminAsync(
                root, token!, HttpMethod.Put, $"/admin/members/{Probe}/usertype",
                """{"userType":"wizard"}""");

            Assert.True(
                refused == HttpStatusCode.BadRequest,
                $"'wizard' as a user type answered {(int)refused}: {why}");

            Assert.Contains("viewer", why, System.StringComparison.Ordinal);
            Assert.Contains("unrestricted", why, System.StringComparison.Ordinal);

            // <b>And nothing moved.</b> A refusal that had already written would leave the member
            // holding a ceiling the server does not recognise, which withdraws everything.
            Assert.Equal("creator", await UserTypeAsync(root, token!));
        }
        finally
        {
            await RemoveAsync(root, token!);
        }
    }

    /// <summary>
    /// A member who does not exist is a 404 rather than a silent success.
    /// </summary>
    [Fact]
    public async Task A_member_who_does_not_exist_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "these tests need an administrator's token");

        (HttpStatusCode answered, string why) = await AdminAsync(
            root, token!, HttpMethod.Put, "/admin/members/zz_nobody_at_all/usertype",
            """{"userType":"editor"}""");

        Assert.True(
            answered == HttpStatusCode.NotFound,
            $"changing a missing member's type answered {(int)answered}: {why}");
    }

    /// <summary>The probe's user type, read back from the listing.</summary>
    /// <param name="root">The server.</param>
    /// <param name="token">An administrator's.</param>
    /// <returns>The type, or the empty string when the probe is not listed.</returns>
    private async Task<string> UserTypeAsync(string root, string token) =>
        await FieldAsync(root, token, "userType");

    /// <summary>The probe's first role, read back from the listing.</summary>
    /// <param name="root">The server.</param>
    /// <param name="token">An administrator's.</param>
    /// <returns>The role, or the empty string when it holds none.</returns>
    private async Task<string> RoleAsync(string root, string token)
    {
        (HttpStatusCode _, string body) = await AdminAsync(
            root, token, HttpMethod.Get, "/admin/members", null);

        using JsonDocument answer = JsonDocument.Parse(body);

        foreach (JsonElement member in answer.RootElement.GetProperty("members").EnumerateArray())
        {
            if (member.GetProperty("name").GetString() == Probe
                && member.TryGetProperty("roles", out JsonElement roles)
                && roles.GetArrayLength() > 0)
            {
                return roles[0].GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>One string field of the probe's row in the member listing.</summary>
    /// <param name="root">The server.</param>
    /// <param name="token">An administrator's.</param>
    /// <param name="field">The field to read.</param>
    /// <returns>Its value, or the empty string when the probe is not listed.</returns>
    private async Task<string> FieldAsync(string root, string token, string field)
    {
        (HttpStatusCode _, string body) = await AdminAsync(
            root, token, HttpMethod.Get, "/admin/members", null);

        using JsonDocument answer = JsonDocument.Parse(body);

        foreach (JsonElement member in answer.RootElement.GetProperty("members").EnumerateArray())
        {
            if (member.GetProperty("name").GetString() == Probe)
            {
                return member.GetProperty(field).GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>Takes the probe away, whatever state it is in.</summary>
    /// <param name="root">The server.</param>
    /// <param name="token">An administrator's.</param>
    /// <returns>The task.</returns>
    private async Task RemoveAsync(string root, string token)
    {
        // <b>Disabled first, because a member who owns nothing can still hold a role.</b> The
        // delete refuses an enabled administrator, and the probe is given one in the test above.
        await AdminAsync(root, token, HttpMethod.Post, $"/admin/members/{Probe}/disable", null);
        await AdminAsync(root, token, HttpMethod.Delete, $"/admin/members/{Probe}", null);
    }

    /// <summary>An admin request with an administrator's token.</summary>
    /// <param name="root">The server.</param>
    /// <param name="token">The token.</param>
    /// <param name="method">The verb.</param>
    /// <param name="path">The path.</param>
    /// <param name="body">The JSON body, or null.</param>
    /// <returns>The status and the body.</returns>
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
