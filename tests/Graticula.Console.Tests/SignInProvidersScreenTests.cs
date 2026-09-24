using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Sign-in providers on screen — ADR-088: Server › Sign-in, the sign-in panel's way in, and New member through one.
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

    private static string Change(string selector, string value) =>
        $"(() => {{ const e = document.querySelector({JsonSerializer.Serialize(selector)}); e.value = {JsonSerializer.Serialize(value)};"
        + " if (e.type === 'radio') e.checked = true; e.dispatchEvent(new Event('change', { bubbles: true })); return true; })()";

    [Fact]
    public async Task A_new_provider_asks_for_a_role_only_when_a_first_sign_in_makes_an_account()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/signin", token);
        await WaitForAsync(Shown("#idpNew"), "Server › Sign-in drew no Add a provider.");

        await ClickAsync("#idpNew");
        await WaitForAsync(Shown("#idpName"), "Add a provider opened no form.");

        // The redirect URI to register at the provider is on the form, and ends where the callback is.
        Assert.EndsWith("/rest/auth/oidc/callback", await Browser.EvaluateAsync<string>("document.getElementById('idpRedirect').value") ?? string.Empty, StringComparison.Ordinal);

        // Turning people away is the default, and asks no role.
        Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('[name=idpAuto][value=no]').checked"));
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('idpAutoRow').hidden"), "A role is asked with no account to give it to.");

        await Browser.EvaluateAsync<bool>(Change("[name=idpAuto][value=yes]", "yes"));
        await WaitForAsync(Shown("#idpRole"), "Choosing to make accounts showed no role to give them.");

        await Browser.EvaluateAsync<bool>(Change("[name=idpAuto][value=no]", "no"));
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('idpAutoRow').hidden"), "Turning people away left the role showing.");

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
