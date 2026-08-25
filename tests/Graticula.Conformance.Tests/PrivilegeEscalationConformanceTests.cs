using System;
using System.Collections.Generic;
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
/// Whether any role a deployment can define reaches the administrator role.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-035 condition 7, and the escalation is one privilege long.</b> Since role privileges became
/// editable a deployment can grant <c>admin:manageMembers</c> to anything — and every act that
/// creates or removes an administrator used to check exactly that privilege. So a role holding it
/// could call <c>PUT /admin/members/{itself}/role</c> with <c>administrator</c> and be done. §4a's
/// editability contained its own defeat.
/// </para>
/// <para>
/// <b>In the conformance suite because it is a promise about the server, not about a screen.</b> It
/// needs a running instance, a real second member and a real session, and none of those can be faked
/// without testing the fake. The five refusals below are the whole of §4g as it applies to what this
/// server has built — backups and custom data providers are the reference's and are not here.
/// </para>
/// <para>
/// <b>It creates its own role and member and removes both.</b> A test that needed a privileged
/// account to exist would be a test that documents a hole in whatever deployment it runs against.
/// </para>
/// </remarks>
public sealed class PrivilegeEscalationConformanceTests : ArcGisClient
{
    private const string StewardRole = "zz_conformance_steward";
    private const string StewardName = "zz_conformance_steward_one";
    /// <summary>
    /// Whose account the reserved operations are attempted against.
    /// </summary>
    /// <remarks>
    /// <b>Read rather than assumed.</b> The administrator is whoever the run was
    /// configured with — `root` on a developer machine, `ci` in the workflow — and the
    /// point of these cases is that a role holding every privilege still cannot touch
    /// an administrator. Naming one turns the test into a claim about a fixture.
    /// </remarks>
    private static string Administrator =>
        Environment.GetEnvironmentVariable(ArcGisClient.UserVariable)
        ?? throw new InvalidOperationException(
            $"{ArcGisClient.UserVariable} is not set, so this suite cannot know which "
            + "account is the administrator these operations are reserved to.");

    private const string Password = "Conformance!2026xyz";

    /// <summary>
    /// A role granted every privilege in the catalogue still cannot make an administrator.
    /// </summary>
    /// <remarks>
    /// <b>Every privilege, not just the plausible one.</b> Granting `admin:manageMembers` alone would
    /// test one route; granting the lot tests the claim ADR-035 §4g actually makes — that no
    /// privilege, in any combination, reaches the role.
    /// </remarks>
    [Fact]
    public async Task No_privilege_lets_a_role_reach_the_administrator_role()
    {
        string root = await RequireServerAsync();

        string[] everything = await CatalogueAsync(root);

        Assert.True(
            everything.Length >= 18,
            $"The privilege catalogue reported {everything.Length} names; this test is only "
            + "meaningful if it grants all of them.");

        await MakeRoleAsync(root, everything);

        try
        {
            string steward = await MakeMemberAsync(root);

            // <b>What the privileges do let them do, first.</b> A test where everything is refused
            // proves nothing about *why* — it could be a broken account. This is the control.
            Assert.Equal(
                HttpStatusCode.OK,
                await AsStewardAsync(root, steward, HttpMethod.Get, "/admin/members", null));

            Assert.Equal(
                HttpStatusCode.OK,
                await AsStewardAsync(
                    root, steward, HttpMethod.Get, "/admin/roles", null));

            // And the five that must be refused, each by role rather than by privilege.
            (string What, HttpMethod Method, string Path, string? Body)[] reserved =
            [
                ("make themselves an administrator", HttpMethod.Put,
                    $"/admin/members/{StewardName}/role", """{"role":"administrator"}"""),

                ("make somebody else an administrator", HttpMethod.Put,
                    $"/admin/members/{StewardName}/role", """{"role":"administrator"}"""),

                ("create an administrator", HttpMethod.Post,
                    "/admin/members",
                    $$"""{"name":"zz_second_admin","role":"administrator","userType":"unrestricted"}"""),

                // <b>The configured administrator, not `root` — 2026-08-25.</b> These
                // two named `root`, which is what the administrator is called on the
                // machine this was written on. In CI it is `ci`, so both requests were
                // **404 rather than 403** and the suite reported it as *a role holding
                // every privilege could reset an administrator's password* — an
                // accusation about authorization, produced by a member that did not
                // exist. The same shape as the wildcard search test fixed in the same
                // hour, and the same lesson: a hard-coded fixture inside an assertion
                // about behaviour turns an absent row into a security finding.
                ("reset an administrator's password", HttpMethod.Put,
                    $"/admin/members/{Administrator}/password", null),

                ("remove an administrator", HttpMethod.Delete,
                    $"/admin/members/{Administrator}", null),
            ];

            foreach ((string what, HttpMethod method, string path, string? body) in reserved)
            {
                HttpStatusCode status =
                    await AsStewardAsync(root, steward, method, path, body);

                Assert.True(
                    status == HttpStatusCode.Forbidden,
                    $"A role holding every privilege could {what}: {(int)status}. ADR-035 §4g "
                    + "reserves these to the administrator role by name, because a role that can "
                    + "reach the role makes every other grant decorative.");
            }

            // <b>And taking the role away is refused too, which is the half that is easy to miss.</b>
            // Somebody clearing the way to become the only administrator does it by removing the
            // others, not by promoting themselves.
            Assert.Equal(
                HttpStatusCode.Forbidden,
                await AsStewardAsync(
                    root, steward, HttpMethod.Put,
                    $"/admin/members/{Administrator}/role",
                    """{"role":"user"}"""));
        }
        finally
        {
            await AdminAsync(root, HttpMethod.Delete, $"/admin/members/{StewardName}?deleteOwned=true", null);
            await AdminAsync(root, HttpMethod.Delete, $"/admin/roles/{StewardRole}", null);
        }
    }

    /// <summary>
    /// A role a deployment defined can actually be given to a member.
    /// </summary>
    /// <remarks>
    /// <b>Found while writing the test above, in two handlers.</b> Both the create and the
    /// role-change path validated the requested role against <c>Roles.All</c> — the five this build
    /// ships with — so from the moment ADR-035 let a deployment define a role, it could define one and
    /// assign it to nobody. The feature was half-built, and the two handlers failed differently enough
    /// that repairing one looked like repairing it.
    /// </remarks>
    [Fact]
    public async Task A_role_the_deployment_defined_can_be_assigned()
    {
        string root = await RequireServerAsync();

        await MakeRoleAsync(root, ["content:create"]);

        try
        {
            // At creation.
            string steward = await MakeMemberAsync(root);
            Assert.NotEmpty(steward);

            // And on an existing member, which is a different handler and was wrong in the same way.
            Assert.Equal(
                HttpStatusCode.OK,
                await AdminAsync(
                    root,
                    HttpMethod.Put,
                    $"/admin/members/{StewardName}/role",
                    $$"""{"role":"{{StewardRole}}"}"""));
        }
        finally
        {
            await AdminAsync(root, HttpMethod.Delete, $"/admin/members/{StewardName}?deleteOwned=true", null);
            await AdminAsync(root, HttpMethod.Delete, $"/admin/roles/{StewardRole}", null);
        }
    }

    /// <summary>Every privilege name the server publishes.</summary>
    private async Task<string[]> CatalogueAsync(string root)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + "/admin/roles"));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument answer =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return
        [
            .. answer.RootElement.GetProperty("catalogue").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
                .Where(n => n.Length > 0),
        ];
    }

    private async Task MakeRoleAsync(string root, IReadOnlyList<string> privileges)
    {
        // Best effort: a previous run that died between creating and cleaning up left it behind.
        await AdminAsync(root, HttpMethod.Delete, $"/admin/roles/{StewardRole}", null);

        HttpStatusCode made = await AdminAsync(
            root,
            HttpMethod.Post,
            "/admin/roles",
            JsonSerializer.Serialize(new
            {
                name = StewardRole,
                description = "Created by PrivilegeEscalationConformanceTests.",
                privileges,
            }));

        Assert.True(
            made is HttpStatusCode.OK or HttpStatusCode.Created,
            $"Could not create the role this test needs: {(int)made}");
    }

    /// <summary>Creates the member and returns a session token for it.</summary>
    private async Task<string> MakeMemberAsync(string root)
    {
        await AdminAsync(root, HttpMethod.Delete, $"/admin/members/{StewardName}?deleteOwned=true", null);


        using HttpRequestMessage create = new(HttpMethod.Post, new Uri(root + "/admin/members"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    name = StewardName,
                    role = StewardRole,
                    userType = "unrestricted",
                }),
                Encoding.UTF8,
                "application/json"),
        };

        await AuthenticateAsync(create, root);

        using HttpResponseMessage made = await Http.SendAsync(create);

        Assert.True(
            made.IsSuccessStatusCode,
            $"Could not create the member: {(int)made.StatusCode} "
            + await made.Content.ReadAsStringAsync());

        using JsonDocument answer = JsonDocument.Parse(await made.Content.ReadAsStringAsync());

        string issued = answer.RootElement.GetProperty("password").GetString()!;

        // <b>The issued password must be replaced before anything else works</b>, which is the
        // server's rule and not this test's — ADR-015. So sign in with it, change it, sign in again.
        string first = await SignInAsync(root, StewardName, issued);

        using HttpRequestMessage change = new(HttpMethod.Post, new Uri(root + "/rest/auth/password"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { currentPassword = issued, newPassword = Password }),
                Encoding.UTF8,
                "application/json"),
        };

        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);

        using HttpResponseMessage changed = await Http.SendAsync(change);

        Assert.True(
            changed.IsSuccessStatusCode,
            $"Could not set the member's password: {(int)changed.StatusCode}");

        return await SignInAsync(root, StewardName, Password);
    }

    private async Task<string> SignInAsync(string root, string name, string password)
    {

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(root + "/rest/auth/login"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { name, password }),
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"'{name}' could not sign in: {(int)response.StatusCode}");

        using JsonDocument answer =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return answer.RootElement.GetProperty("token").GetString()!;
    }

    private async Task<HttpStatusCode> AsStewardAsync(
        string root, string token, HttpMethod method, string path, string? body)
    {
        using HttpRequestMessage request = new(method, new Uri(root + path));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return response.StatusCode;
    }

    private async Task<HttpStatusCode> AdminAsync(
        string root, HttpMethod method, string path, string? body)
    {
        using HttpRequestMessage request = new(method, new Uri(root + path));

        await AuthenticateAsync(request, root);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return response.StatusCode;
    }
}
