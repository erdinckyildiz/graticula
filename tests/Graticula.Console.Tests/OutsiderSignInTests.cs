using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// How a member of another organization is told to name themselves — ADR-204 §5e item 16. A bare name is always a
/// member of the organization whose host it is typed at; somebody from another one is <c>organization\name</c>, signs in
/// by that, and is added to a group by it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves of the sign-in rule are asserted: the hint where there are several organizations, and nothing where
/// there is one.</b> A single-organization server is most deployments, and a sentence about other organizations there
/// is a sentence about something that does not exist.
/// </para>
/// <para>
/// <b>Every organization made here is purged in <c>finally</c></b>, for the reason <see cref="OrganizationReadersTests"/>
/// gives: while a second one exists the whole server answers as several.
/// </para>
/// </remarks>
public sealed class OutsiderSignInTests : ConsoleTest
{
    private async Task<(int Status, string Body)> AtHostAsync(
        string host, string? token, HttpMethod method, string path, object? body = null, string? accept = null)
    {
        Uri root = new(Root);
        using HttpRequestMessage request = new(method, new Uri($"{Root}{path}"));
        request.Headers.Host = $"{host}:{root.Port}";

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<string[]> TenantKeysAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/tenants");
        Assert.True(status == 200, $"GET /admin/tenants answered {status}: {body}");
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("key").GetString()!)];
    }

    /// <summary>An organization at its own host, and its administrator's usable token — or null where the store refuses one.</summary>
    private async Task<(string Host, string Token)?> MakeOrganizationAsync(string key)
    {
        string host = $"{key}.localhost";
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/tenants",
            JsonSerializer.Serialize(new { key, name = "Zz Outsiders", hosts = new[] { host }, administrator = $"{key}_admin" }));

        if (made == 409 && answer.Contains("superuser", StringComparison.Ordinal))
        {
            return null;
        }

        Assert.True(made == 201, $"Making {key} answered {made}: {answer}");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("administrator").GetProperty("password").GetString()!;
        string name = $"{key}_admin";

        (int s, string b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name, password = issued });
        Assert.True(s == 200, $"Signing in as {name} answered {s}: {b}");
        string first = JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;

        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        (s, b) = await AtHostAsync(host, first, HttpMethod.Post, "/rest/auth/password", new { currentPassword = issued, newPassword = chosen });
        Assert.True(s is >= 200 and < 300, $"Replacing {name}'s issued password answered {s}: {b}");

        (s, b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name, password = chosen });
        Assert.True(s == 200, $"Signing in again as {name} answered {s}: {b}");
        return (host, JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!);
    }

    private async Task PurgeAsync(string key)
    {
        if ((await TenantKeysAsync()).Contains(key))
        {
            (int purged, string said) = await AdminAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true");
            Assert.True(purged == 200, $"Purging {key} answered {purged}: {said}. The server still holds a second organization.");
        }
    }

    /// <summary>Types a name and a wrong password into the console's card, lets that one request through, and returns what the card said.</summary>
    private async Task<string> FailedSignInSaysAsync(string name)
    {
        await Browser.EvaluateAsync<bool>($$"""
            (() => {
              window.__letWritesTo = ["/rest/auth/login"];
              document.getElementById("signinError").hidden = true;
              document.getElementById("u").value = {{JsonSerializer.Serialize(name)}};
              document.getElementById("p").value = "not-the-password-{{Guid.NewGuid():N}}";
              document.getElementById("go").click();
              return true;
            })()
            """);

        await WaitForAsync("!document.getElementById('signinError').hidden", "A wrong password said nothing on the sign-in card.");
        return await Browser.EvaluateAsync<string>("document.getElementById('signinError').textContent") ?? string.Empty;
    }

    [Fact]
    public async Task On_a_server_of_one_organization_the_sign_in_says_nothing_about_other_organizations()
    {
        Assert.Equal(["default"], await TenantKeysAsync());

        await OpenAsync("/server/");
        await WaitForAsync(Shown("#u"), "The sign-in card did not draw.");

        // The title is filled from whoami, so once it is, drawTenancy has run and an absence below is an absence.
        await WaitForAsync("document.getElementById('signinTitle').textContent === 'Sign in to Graticula'",
            "The sign-in card's title was not drawn from whoami.");
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#signinOther")),
            "A server of one organization tells the reader how a member of another one signs in.");
        Assert.Null(await Browser.EvaluateAsync<string?>("document.getElementById('u').getAttribute('aria-describedby')"));

        string said = await FailedSignInSaysAsync("zz-nobody-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.DoesNotContain("another organization", said, StringComparison.Ordinal);

        (int status, string page) = await AtHostAsync(new Uri(Root).Host, null, HttpMethod.Get, "/rest/login", accept: "text/html");
        Assert.Equal(200, status);
        Assert.DoesNotContain("signin-other", page, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task At_an_organizations_host_the_sign_in_says_how_a_member_of_another_signs_in_and_a_group_how_to_add_one()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string key = "zzo" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (await MakeOrganizationAsync(key) is not (string host, string token))
            {
                return;
            }

            // <b>The card, as somebody with no session at that host sees it.</b>
            await OpenOnHostAsync(host, "/server/", null);
            await WaitForAsync(Shown("#signinOther"),
                "The sign-in card at a second organization's host says nothing to a member of another organization.");
            Assert.Contains("organization\\name", await Browser.EvaluateAsync<string>("document.getElementById('signinOther').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Equal("signinOther", await Browser.EvaluateAsync<string>("document.getElementById('u').getAttribute('aria-describedby')"));

            // A bare name that is refused is told about the qualified form; a qualified one is not told it again. Neither
            // sentence depends on whether the name is an account: the first name is nobody's anywhere.
            string bare = await FailedSignInSaysAsync("zz-nobody-" + Guid.NewGuid().ToString("N")[..8]);
            Assert.Contains("The name or password is incorrect.", bare, StringComparison.Ordinal);
            Assert.Contains("sign in as organization\\name", bare, StringComparison.Ordinal);

            string qualified = await FailedSignInSaysAsync("default\\zz-nobody-" + Guid.NewGuid().ToString("N")[..8]);
            Assert.DoesNotContain("another organization", qualified, StringComparison.Ordinal);

            // The services directory's own sign-in says the same.
            (int status, string page) = await AtHostAsync(host, null, HttpMethod.Get, "/rest/login", accept: "text/html");
            Assert.Equal(200, status);
            Assert.Contains("id=\"signin-other\"", page, StringComparison.Ordinal);
            Assert.Contains("aria-describedby=\"signin-other\"", page, StringComparison.Ordinal);

            // <b>A group that does not admit other organizations offers no field for one</b>, and says where the switch is.
            string group = "zz_outsiders_" + Guid.NewGuid().ToString("N")[..6];
            (int made, string body) = await AtHostAsync(host, token, HttpMethod.Post, "/admin/groups", new { name = group, itemUpdate = "none" });
            Assert.True(made == 201, $"Making {group} answered {made}: {body}");

            await OpenOnHostAsync(host, $"/studio/#/group/{group}/members", token);
            await WaitForAsync(Shown("#groupAdd"), "Members offers no Add member to the organization's administrator.");
            await ClickAsync("#groupAdd");
            await WaitForAsync(Shown("#groupPickOtherOff"),
                "A group that does not admit other organizations does not say how to let them in.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('groupPickOther')"),
                "A group that does not admit other organizations offers a field the server would refuse.");
            // Studio writes its hash links as paths, so the address is read for where it ends.
            Assert.EndsWith($"group/{group}/settings", await Browser.EvaluateAsync<string>(
                "document.querySelector('#groupPickOtherOff a')?.getAttribute('href') || ''") ?? string.Empty, StringComparison.Ordinal);

            // Admitted, the field is there, wide enough for a qualified name, and a name with no organization is caught
            // before it is sent — it would otherwise name one of this organization's own members.
            (int admitted, string why) = await AtHostAsync(host, token, HttpMethod.Put, $"/admin/groups/{group}/tenants", new { admitsOtherTenants = true });
            Assert.True(admitted == 200, $"Admitting other organizations answered {admitted}: {why}");

            await OpenOnHostAsync(host, $"/studio/#/group/{group}/members", token);
            await WaitForAsync(Shown("#groupAdd"), "Members did not draw again.");
            await ClickAsync("#groupAdd");
            await WaitForAsync(Shown("#groupPickOther"), "A group that admits other organizations offers no way to add a member of one.");
            Assert.Equal("groupPickOtherHint", await Browser.EvaluateAsync<string>("document.getElementById('groupPickOther').getAttribute('aria-describedby')"));
            Assert.True(await Browser.EvaluateAsync<bool>(
                "document.getElementById('groupPickOther').getBoundingClientRect().width >= document.querySelector('#groupPicker .picker').getBoundingClientRect().width * 0.8"),
                "The field for a qualified name is narrower than the picker it is in, and cuts the name in two.");

            await Browser.EvaluateAsync<bool>("""
                (() => {
                  document.getElementById("groupPickOther").value = "ali@firma.com";
                  document.getElementById("groupPickAdd").click();
                  return true;
                })()
                """);
            await WaitForAsync(Shown("#groupPickSays"), "A name with no organization in front was not questioned.");
            Assert.Contains("backslash", await Browser.EvaluateAsync<string>("document.getElementById('groupPickSays').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Equal("alert", await Browser.EvaluateAsync<string>("document.getElementById('groupPickSays').getAttribute('role')"));
            Assert.Equal("groupPickOther", await Browser.EvaluateAsync<string>("document.activeElement?.id"));
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#groupPickOther")), "The picker closed under its own question.");
            Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/members/", StringComparison.Ordinal));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
        }
    }
}
