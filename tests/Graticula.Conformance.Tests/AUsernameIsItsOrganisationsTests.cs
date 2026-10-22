using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-204 §5e item 16, owner decision 2026-10-22: a username is unique within its organisation, so one name in two is
/// two accounts — each signing in at its own host, reset and removed by its own administrator, and named
/// <c>organisation\name</c> wherever the other organisation meets it.
/// </summary>
[Collection("server settings")]
public sealed class AUsernameIsItsOrganisationsTests : ArcGisClient
{
    private string _root = string.Empty;

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? host = null, string? token = null, object? json = null)
    {
        using HttpRequestMessage request = new(method, $"{_root}{path}");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = json is null ? null : JsonContent.Create(json);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string?> SignInAsync(string name, string password, string? host = null)
    {
        (HttpStatusCode status, JsonElement body) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password });
        return status == HttpStatusCode.OK ? body.GetProperty("token").GetString() : null;
    }

    /// <summary>Signs in with the issued password, sets an own one, and returns it.</summary>
    private async Task<string> SettleAsync(string name, string issued, string? host = null)
    {
        string first = await SignInAsync(name, issued, host) ?? throw new InvalidOperationException($"{name} could not sign in.");
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first, new { currentPassword = issued, newPassword = own });
        return own;
    }

    [Fact]
    public async Task One_name_in_two_organisations_is_two_accounts_and_nothing_reaches_across()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"user{run}";
        string host = $"{key}.localhost";
        string ali = $"zzali{run}@firma.com";
        string qualified = $"default\\{ali}";
        string group = $"zz crew {run}";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"User {run}", hosts = new[] { host }, administrator = $"useradmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}.");

        try
        {
            string jvAdmin = await SignInAsync($"useradmin{run}",
                await SettleAsync($"useradmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host), host)
                ?? throw new InvalidOperationException("The organisation's administrator could not sign in.");

            (HttpStatusCode mainMade, JsonElement mainAli) = await SendAsync(HttpMethod.Post, "/admin/members", token: admin, json: new { name = ali, role = "user" });
            (HttpStatusCode jvMade, JsonElement jvAli) = await SendAsync(HttpMethod.Post, "/admin/members", host, jvAdmin, new { name = ali, role = "user" });
            Assert.Equal(HttpStatusCode.Created, mainMade);
            Assert.Equal(HttpStatusCode.Created, jvMade);

            string mainPassword = await SettleAsync(ali, mainAli.GetProperty("password").GetString()!);
            string jvPassword = await SettleAsync(ali, jvAli.GetProperty("password").GetString()!, host);

            // Each signs in at their own host; the main one's password opens nothing at the JV's.
            Assert.NotNull(await SignInAsync(ali, mainPassword));
            Assert.NotNull(await SignInAsync(ali, jvPassword, host));
            Assert.Null(await SignInAsync(ali, mainPassword, host));

            // The JV's administrator resets the JV's ali; the main one's password still works.
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/members/{Uri.EscapeDataString(ali)}/password", host, jvAdmin, new { })).Status);
            Assert.NotNull(await SignInAsync(ali, mainPassword));

            // The main ali joins a JV group as default\ali, beside the JV's own, and signs in there by that name.
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/groups", host, jvAdmin, new { name = group })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{Uri.EscapeDataString(group)}/tenants", host, jvAdmin, new { admitsOtherTenants = true })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{Uri.EscapeDataString(group)}/members/{Uri.EscapeDataString(ali)}", host, jvAdmin, new { })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{Uri.EscapeDataString(group)}/members/{Uri.EscapeDataString(qualified)}", host, jvAdmin, new { })).Status);

            (_, JsonElement described) = await SendAsync(HttpMethod.Get, $"/admin/groups/{Uri.EscapeDataString(group)}", host, jvAdmin);
            string listed = described.ToString();
            Assert.Contains($"\"{ali}\"", listed, StringComparison.Ordinal);
            Assert.Contains(JsonSerializer.Serialize(qualified), listed, StringComparison.Ordinal);
            Assert.NotNull(await SignInAsync(qualified, mainPassword, host));

            // The JV removes its ali; the main one remains.
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/admin/members/{Uri.EscapeDataString(ali)}", host, jvAdmin)).Status);
            Assert.NotNull(await SignInAsync(ali, mainPassword));

            // A backslash is not part of a name: it separates an organisation from one.
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/admin/members", token: admin, json: new { name = $"x\\{ali}", role = "user" })).Status);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{Uri.EscapeDataString(ali)}", token: admin);
        }
    }
}
