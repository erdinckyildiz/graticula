using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Sign-in providers on screen — ADR-088 and ADR-089: Server › Sign-in, the sign-in panel's way in, New member
/// through one, a directory's Groups, Members for somebody a provider signs in, and a SAML provider (ADR-090).
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

    /// <summary>A SAML provider, from a metadata document made here; nothing ever signs in through it.</summary>
    private async Task<(string Id, string Name)> SamlAsync()
    {
        string name = "zz SAML " + Guid.NewGuid().ToString("N")[..8];

        (int status, string body) = await AdminAsync(
            HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new { name, kind = "saml", metadata = SamlMetadata() }));

        Assert.True(status == 201, $"Adding a SAML provider answered {status}: {body}");
        return (JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!, name);
    }

    /// <summary>A provider's metadata with a signing certificate made here.</summary>
    private static string SamlMetadata()
    {
        using RSA key = RSA.Create(2048);
        using X509Certificate2 certificate = new CertificateRequest("CN=zz-idp", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        string metadata = "<md:EntityDescriptor xmlns:md=\"urn:oasis:names:tc:SAML:2.0:metadata\" entityID=\"https://idp.example.org/zz\">"
            + "<md:IDPSSODescriptor protocolSupportEnumeration=\"urn:oasis:names:tc:SAML:2.0:protocol\">"
            + "<md:KeyDescriptor use=\"signing\"><ds:KeyInfo xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\"><ds:X509Data><ds:X509Certificate>"
            + Convert.ToBase64String(certificate.RawData)
            + "</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>"
            + "<md:SingleSignOnService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect\" Location=\"https://idp.example.org/sso\"/>"
            + "</md:IDPSSODescriptor></md:EntityDescriptor>";

        return metadata;
    }

    /// <summary>Keeps every JSON body the page sends, which the harness records only by method and address.</summary>
    private const string KeepBodies =
        "(() => { window.__bodies = []; const f = window.fetch; window.fetch = (i, init) => {"
        + " if (init && typeof init.body === 'string') window.__bodies.push(init.body); return f(i, init); }; return true; })()";

    /// <summary>Chooses a file in a file box as a person would, so the page's own change listener reads it.</summary>
    private static string Choose(string selector, string fileName, string content) =>
        $"(() => {{ const box = document.querySelector({JsonSerializer.Serialize(selector)}); const d = new DataTransfer();"
        + $" d.items.add(new File([{JsonSerializer.Serialize(content)}], {JsonSerializer.Serialize(fileName)}, {{ type: 'text/xml' }}));"
        + " box.files = d.files; box.dispatchEvent(new Event('change', { bubbles: true })); return true; })()";

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
    public async Task A_new_saml_provider_gives_this_servers_identifier_and_reply_url_and_asks_for_metadata()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/signin", token);
        await WaitForAsync(Shown("#idpNew"), "Server › Sign-in drew no Add a provider.");
        await ClickAsync("#idpNew");
        await WaitForAsync(Shown("[name=idpKind][value=saml]"), "Add a provider offers no SAML.");

        await Browser.EvaluateAsync<bool>(Change("[name=idpKind][value=saml]", "saml"));
        await WaitForAsync(Shown("#idpMetadataUrl"), "Choosing SAML asked for no metadata.");

        // What the operator gives the provider is on the form before anything is asked of them.
        Assert.EndsWith("/rest/auth/saml/acs", await Browser.EvaluateAsync<string>("document.getElementById('idpAcs').value") ?? string.Empty, StringComparison.Ordinal);
        Assert.EndsWith("/rest/auth/saml", await Browser.EvaluateAsync<string>("document.getElementById('idpClient').value") ?? string.Empty, StringComparison.Ordinal);
        Assert.True(await Browser.EvaluateAsync<bool>("!document.getElementById('idpIssuer')"), "A SAML provider is asked for an OpenID issuer.");

        // A name and no metadata: said at the metadata box, and nothing is sent.
        await Browser.EvaluateAsync<bool>("(() => { document.getElementById('idpName').value = 'zz nothing to trust'; return true; })()");
        await ClickAsync("#idpSave");
        await WaitForAsync("document.getElementById('idpMetadataUrl').getAttribute('aria-invalid') === 'true'", "Saving with no metadata was not marked.");
        await WaitForAsync(Shown("#idpMetadataMissing"), "Saving with no metadata said nothing at the metadata box.");
        Assert.Equal("idpMetadataUrl", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));
        Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/admin/identity-providers", StringComparison.Ordinal));

        // A file chosen: the mark goes, and Add sends the provider with the file's metadata — the design review of
        // 2026-09-24 found Add throwing on the secret box a SAML form does not have, so nothing was ever sent.
        await Browser.EvaluateAsync<bool>(KeepBodies);
        await Browser.EvaluateAsync<bool>(Choose("#idpMetadataFile", "adfs.xml", SamlMetadata()));
        await WaitForAsync("(document.getElementById('idpMetadataFileHint')?.textContent || '').includes('adfs.xml')", "Choosing a file said nothing.");
        Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('idpMetadataUrl').hasAttribute('aria-invalid')"), "The missing-metadata mark stayed after a file was chosen.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('idpMetadataMissing').hidden"), "The missing-metadata message stayed after a file was chosen.");

        await ClickAsync("#idpSave");
        await WaitForAsync("window.__bodies.some(b => b.includes('\\\"kind\\\":\\\"saml\\\"') && b.includes('EntityDescriptor'))",
            "Add sent no SAML provider with the metadata chosen.");
        Assert.Contains(await WritesAsync(), w => w.StartsWith("POST", StringComparison.Ordinal) && w.Contains("/admin/identity-providers", StringComparison.Ordinal));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_saml_provider_is_listed_as_saml_and_offered_on_the_sign_in_panel_at_its_own_start()
    {
        (string token, _) = await SignInAsync();
        (string id, string name) = await SamlAsync();

        try
        {
            await OpenAsync("/server/#/signin", token);
            string row = $"[...document.querySelectorAll('#idpRows tr')].find(r => r.textContent.includes({JsonSerializer.Serialize(name)}))";
            await WaitForAsync($"!!({row})", "Server › Sign-in does not list the SAML provider just added.");
            Assert.Contains("SAML", await Browser.EvaluateAsync<string>($"({row}).querySelector('.idpissuer').textContent") ?? string.Empty, StringComparison.Ordinal);

            // Edit says whose metadata is held and until when its certificate holds.
            await Browser.EvaluateAsync<bool>($"(() => {{ ({row}).querySelector('[data-idp-edit]').click(); return true; }})()");
            await WaitForAsync(
                "(document.getElementById('idpMetadataFileHint')?.textContent || '').includes('https://idp.example.org/zz')",
                "Editing the SAML provider does not say whose metadata is held.");

            // Saving it unchanged sends it, keeping the metadata held.
            await ClickAsync("#idpSave");
            await WaitForAsync(
                $"(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/admin/identity-providers/{id}'))",
                "Saving the SAML provider sent nothing.");

            await OpenAsync("/server/");
            string mine = $"#signinProviders a[href^='/rest/auth/saml/{id}/start?return=']";
            await WaitForAsync(Shown(mine), "The sign-in panel does not offer the SAML provider at its own start.");

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
