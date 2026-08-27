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
/// Who may be removed, and what the server destroys on the way to saying no.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-101](../../docs/architecture-debt.md) named an irreversible side effect on the way to a
/// refusal</b>, and reading the flow to repair it turned up a second thing the row did not have:
/// the last-administrator refusal fired for a *disabled* administrator. The count it compares
/// against is of administrators who can still sign in, and a disabled one is not in it — so
/// removing them cannot reduce it, and the refusal was about nothing. A fresh install has exactly
/// one enabled administrator, which made this the ordinary case rather than a corner.
/// </para>
/// <para>
/// <b>Staged rather than found, and staged carefully.</b> The refusal these tests are about needs
/// an administrator to remove, and this suite runs against a server somebody uses. So it makes its
/// own: a member created here, granted the role here, disabled here, and removed here. Nothing it
/// touches existed before it ran.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class LastAdministratorTests : ArcGisClient
{
    private const string Probe = "zz_d101_admin";

    /// <summary>
    /// A disabled administrator can be removed while one enabled administrator remains.
    /// </summary>
    /// <remarks>
    /// <b>The refusal is about recoverability, and this removal does not touch it.</b> The server
    /// with one enabled administrator has exactly as many after this as before. Refusing it left
    /// an operator no way to clear out an administrator account they had already disabled, and
    /// the message told them to *make another administrator first* — advice that would not have
    /// helped, since it was already true that another one existed.
    /// </remarks>
    [Fact]
    public async Task A_disabled_administrator_can_be_removed()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "these tests need an administrator's token");

        await RemoveAsync(root, token!);

        try
        {
            (HttpStatusCode created, string body) = await AdminAsync(
                root, token!, HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = Probe, role = "user", userType = "creator" }));

            Assert.True(
                created is HttpStatusCode.OK or HttpStatusCode.Created,
                $"creating the probe member answered {(int)created}: {body}");

            (HttpStatusCode granted, string why) = await AdminAsync(
                root, token!, HttpMethod.Put, $"/admin/members/{Probe}/role",
                """{"role":"administrator"}""");

            Assert.True(
                granted == HttpStatusCode.OK,
                $"granting the administrator role answered {(int)granted}: {why}");

            (HttpStatusCode disabled, string said) = await AdminAsync(
                root, token!, HttpMethod.Post, $"/admin/members/{Probe}/disable", null);

            Assert.True(
                disabled == HttpStatusCode.OK,
                $"disabling the probe member answered {(int)disabled}: {said}");

            (HttpStatusCode removed, string answer) = await AdminAsync(
                root, token!, HttpMethod.Delete, $"/admin/members/{Probe}", null);

            Assert.True(
                removed == HttpStatusCode.OK,
                $"removing a disabled administrator answered {(int)removed}: {answer}. It holds "
                + "the role and cannot sign in, so taking it away leaves the server exactly as "
                + "recoverable as it was.");
        }
        finally
        {
            await RemoveAsync(root, token!);
        }
    }

    /// <summary>
    /// An administrator still cannot remove themselves, and is told so before anything else.
    /// </summary>
    /// <remarks>
    /// <b>Asserted because the repair above moved a check into the same preamble.</b> The
    /// self-removal refusal is what an operator on a one-administrator server actually meets —
    /// the only person who can reach the Remove button on the only administrator is that
    /// administrator — and it has to keep coming first, before the disposition is read and long
    /// before anything is deleted.
    /// </remarks>
    [Fact]
    public async Task An_administrator_cannot_remove_themselves_whatever_they_ask_for()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.True(token is not null, "these tests need an administrator's token");

        string me = JsonDocument.Parse(
                (await AdminAsync(root, token!, HttpMethod.Get, "/rest/whoami", null)).Body)
            .RootElement.GetProperty("name").GetString()!;

        string[] before = [.. await EveryServiceNameAsync()];

        Assert.NotEmpty(before);

        // <b>With the most destructive disposition there is</b>, which is the point: if the
        // refusal ever moves below the dispositions, this is the request that would notice —
        // `deleteOwned=true` unpublishes every layer this member owns and removes every service,
        // and the operator running it is the one who owns most of them.
        (HttpStatusCode status, string body) = await AdminAsync(
            root, token!, HttpMethod.Delete, $"/admin/members/{me}?deleteOwned=true", null);

        Assert.Equal(HttpStatusCode.Conflict, status);

        Assert.Contains("cannot remove themselves", body, StringComparison.Ordinal);

        // And nothing went with it. The comparison is the assertion: a refusal that arrives
        // after the disposition has run answers 409 just the same.
        string[] after = [.. await EveryServiceNameAsync()];

        Assert.Equal<string>(before, after);
    }

    private async Task RemoveAsync(string root, string token)
    {
        (HttpStatusCode status, _) = await AdminAsync(
            root, token, HttpMethod.Post, $"/admin/members/{Probe}/enable", null);

        if (status == HttpStatusCode.NotFound)
        {
            return;
        }

        await AdminAsync(
            root, token, HttpMethod.Delete, $"/admin/members/{Probe}?deleteOwned=true", null);
    }

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
