using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Sign-in providers on screen — ADR-088 and ADR-089: Server › Sign-in, the sign-in panel's way in, New member
/// through one, a directory's Groups, and Members for somebody a provider signs in.
/// </summary>
/// <remarks>
/// <b>Every change event here is fired without a Fields editor open</b>, because the design review of 2026-09-24
/// found the console's change listener returning early unless one was — so the first-sign-in choice, the New member
/// provider and the Domains screen's new-domain kind all did nothing, and every test passed. The last test is that
/// finding's regression for ADR-087's screen.
/// </remarks>
[Collection("server ground")]
public sealed class SignInProvidersScreenTests : ConsoleTest
{
    private async Task<(string Id, string Name)> ProviderAsync()
    {
        string name = "zz Staff " + Guid.NewGuid().ToString("N")[..8];

        (int status, string body) = await AdminAsync(
            HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new { name, issuer = "https://login.example.org/realms/staff", clientId = "graticula" }));

        Assert.True(status == 201, $"Adding a provider answered {status}: {body}");
        return (JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!, name);
    }

    /// <summary>A directory on this machine, which nothing ever connects to in these tests.</summary>
    private async Task<(string Id, string Name)> DirectoryAsync()
    {
        string name = "zz AD " + Guid.NewGuid().ToString("N")[..8];

        (int status, string body) = await AdminAsync(
            HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new { name, kind = "ldap", issuer = "ldap://127.0.0.1:1", userBase = "DC=contoso,DC=com" }));

        Assert.True(status == 201, $"Adding a directory answered {status}: {body}");
        return (JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!, name);
    }

    /// <summary>Presses a button in the row of the provider named.</summary>
    private static string InRow(string name, string button) =>
        $"(() => {{ const row = [...document.querySelectorAll('#idpRows tr')].find(r => r.textContent.includes({JsonSerializer.Serialize(name)}));"
        + $" if (!row) return false; row.querySelector({JsonSerializer.Serialize(button)}).click(); return true; }})()";

    private static string Change(string selector, string value) =>
        $"(() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); e.value = {JsonSerializer.Serialize(value)};"
        + " if (e.type === 'radio') e.checked = true; e.dispatchEvent(new Event('change', { bubbles: true })); return true; })()";

    [Fact]
    public async Task A_new_provider_asks_for_a_user_type_only_when_a_first_sign_in_makes_an_account()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/signin", token);
        await WaitForAsync(Shown("#idpNew"), "Server › Sign-in drew no Add a provider.");

        await ClickAsync("#idpNew");
        await WaitForAsync(Shown("#idpName"), "Add a provider opened no form.");

        // The redirect URI to register at the provider is on the form, and ends where the callback is.
        Assert.EndsWith("/rest/auth/oidc/callback", await Browser.EvaluateAsync<string>("document.getElementById('idpRedirect').value") ?? string.Empty, StringComparison.Ordinal);

        // Turning people away is the default, and asks no user type. The role is asked either way: it is also the
        // role of anybody whose mapped groups give none (design review 2026-09-24, M1).
        Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('[name=idpAuto][value=no]').checked"));
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('idpAutoRow').hidden"), "A user type is asked with no account to give it to.");
        await WaitForAsync(Shown("#idpRole"), "The role for anybody no mapped group gives one is not asked.");

        await Browser.EvaluateAsync<bool>(Change("[name=idpAuto][value=yes]", "yes"));
        await WaitForAsync(Shown("#idpType"), "Choosing to make accounts showed no user type to give them.");

        await Browser.EvaluateAsync<bool>(Change("[name=idpAuto][value=no]", "no"));
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('idpAutoRow').hidden"), "Turning people away left the user type showing.");

        // An empty form is refused at the box, and nothing is sent.
        await ClickAsync("#idpSave");
        Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/admin/identity-providers", StringComparison.Ordinal));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_member_added_for_a_provider_is_named_as_the_provider_names_them_and_gets_no_password()
    {
        (string token, _) = await SignInAsync();
        (string id, string name) = await ProviderAsync();

        try
        {
            await OpenAsync("/server/#/members", token);
            await WaitForAsync(Shown("#memberNew"), "Members drew no New member.");
            await ClickAsync("#memberNew");

            await WaitForAsync(Shown("#mVia"), "New member offers no way to sign in through a provider.");

            await Browser.EvaluateAsync<bool>(Change("#mVia", id));

            await WaitForAsync(Shown("#mUsername"), "Choosing a provider asked for no name from it.");
            Assert.Contains(name, await Browser.EvaluateAsync<string>("document.getElementById('mUsernameLabel').textContent") ?? string.Empty, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('mPasswordSays').hidden"), "The password note stayed up for an account with no password.");
            Assert.Equal("Name here:", await Browser.EvaluateAsync<string>("document.getElementById('mNameLabel').textContent"));

            // The provider's name is required: an empty one is refused at the box, and nothing is sent.
            await Browser.EvaluateAsync<bool>("(() => { document.getElementById('mName').value = 'zzjane'; return true; })()");
            await ClickAsync("#mSave");
            Assert.DoesNotContain(await WritesAsync(), w => w.StartsWith("POST", StringComparison.Ordinal) && w.Contains("/admin/members", StringComparison.Ordinal));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task The_sign_in_panel_offers_each_provider_first()
    {
        (string id, string name) = await ProviderAsync();

        try
        {
            await OpenAsync("/server/");

            string mine = $"#signinProviders a[href^='/rest/auth/oidc/{id}/start?return=']";

            await WaitForAsync(Shown(mine), "The sign-in panel does not offer the provider just added.");

            Assert.Contains(name, await Browser.EvaluateAsync<string>($"document.querySelector(\"{mine}\").textContent") ?? string.Empty, StringComparison.Ordinal);

            // Before the password form, as the way in an organisation that configured one expects people to use.
            Assert.True(await Browser.EvaluateAsync<bool>(
                "!!(document.getElementById('signinProviders').compareDocumentPosition(document.getElementById('u')) & Node.DOCUMENT_POSITION_FOLLOWING)"),
                "The provider is not offered before the name and password.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task The_sign_in_panel_says_a_directorys_people_use_their_own_password()
    {
        (string id, string name) = await DirectoryAsync();

        try
        {
            await OpenAsync("/server/");

            // A directory has no button: its people use the form, and the form says so (design review, B3).
            await WaitForAsync(
                $"(document.getElementById('signinProviders')?.textContent || '').includes({JsonSerializer.Serialize(name + " name and password")})",
                "The sign-in panel does not tell a directory's people that their password works here.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task A_group_mapping_with_no_group_named_is_marked_and_not_sent()
    {
        (string token, _) = await SignInAsync();
        (string id, string name) = await DirectoryAsync();

        try
        {
            await OpenAsync("/server/#/signin", token);
            await WaitForAsync($"!!document.getElementById('idpRows') && {InRow(name, "[data-idp-groups]")}", "Server › Sign-in lists no Groups for the directory just added.");
            await WaitForAsync(Shown("#idpMapAdd"), "Groups opened no editor.");

            await ClickAsync("#idpMapAdd");
            await WaitForAsync(Shown("[data-map-role='0']"), "Add a group added no row.");

            // Administrator says, under the row, what it will do.
            await Browser.EvaluateAsync<bool>(Change("[data-map-role='0']", "administrator"));
            await WaitForAsync(
                "(document.querySelector('#idpGroups .warn-inline')?.textContent || '').includes('becomes an administrator')",
                "Mapping a group to administrator said nothing about what that does.");

            // A role with no group to give it from is marked where it is, and nothing is sent (M5).
            await ClickAsync("#idpMapSave");
            await WaitForAsync(
                "document.querySelector(\"[data-map-external='0']\")?.getAttribute('aria-invalid') === 'true'",
                "A row with a role and no group was not marked.");
            Assert.Equal("0", await Browser.EvaluateAsync<string>("document.activeElement?.dataset?.mapExternal ?? ''"));
            Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/groups", StringComparison.Ordinal));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task A_member_a_provider_signs_in_is_offered_no_password_here()
    {
        (string token, _) = await SignInAsync();
        (string id, string provider) = await ProviderAsync();
        string member = "zzext" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            (int status, string body) = await AdminAsync(
                HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = member, role = "viewer", provider = id, username = member + "@example.org" }));
            Assert.True(status is 200 or 201, $"Adding a member through the provider answered {status}: {body}");

            await OpenAsync("/server/#/members", token);

            string row = $"[...document.querySelectorAll('#view-members tr')].find(r => r.textContent.includes({JsonSerializer.Serialize(member)}))";
            await WaitForAsync($"!!({row})", "Members does not list the member just added.");

            // Their provider keeps their password (B2): no button to set one here, and the row says why.
            Assert.False(await Browser.EvaluateAsync<bool>($"!!({row}).querySelector('[data-member-password]')"), "A password is offered to a member a provider signs in.");
            Assert.Contains(provider + " keeps their password", await Browser.EvaluateAsync<string>($"({row}).textContent") ?? string.Empty, StringComparison.Ordinal);

            // And the API refuses one anyway.
            (int refused, _) = await AdminAsync(HttpMethod.Put, $"/admin/members/{member}/password");
            Assert.Equal(409, refused);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}");
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task A_new_domains_kind_redraws_its_editor_with_no_fields_editor_open()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/domains", token);
        await WaitForAsync(Shown("#domainNew"), "The Domains screen drew no New domain.");
        await ClickAsync("#domainNew");

        await WaitForAsync(Shown("#domEditName"), "New domain opened no editor.");
        await Browser.EvaluateAsync<bool>(Change("[name=domEditKind][value=range]", "range"));

        await WaitForAsync(Shown("#domEditLeast"), "Choosing a range drew no From and To.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
