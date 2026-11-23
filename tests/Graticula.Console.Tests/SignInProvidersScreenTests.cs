using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Sign-in providers on screen — ADR-088 and ADR-089: Organization › Security, the sign-in panel's way in, New member
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

        await OpenAsync("/studio/#/signin", token);
        await SecuritySectionAsync("idp");
        await WaitForAsync(Shown("#idpNew"), "Organization › Security drew no Add a provider.");

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
            await OpenAsync("/studio/#/members", token);
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
            await OpenAsync("/studio/#/signin", token);
            await SecuritySectionAsync("idp");
            await WaitForAsync($"!!document.getElementById('idpRows') && {InRow(name, "[data-idp-groups]")}", "Organization › Security lists no Groups for the directory just added.");
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

            await OpenAsync("/studio/#/members", token);

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

    /// <summary>
    /// ADR-089 conditions 1 and 3 on screen: a new directory's form takes the certificate authority to trust beside its
    /// address, and offers groups inside groups under Advanced — opened as a person opens it, so the box is one they can
    /// see — and Add sends both.
    /// </summary>
    [Fact]
    public async Task A_new_directory_sends_the_authority_to_trust_and_groups_inside_groups()
    {
        (string token, _) = await SignInAsync();

        using RSA key = RSA.Create(2048);
        using X509Certificate2 authority = new CertificateRequest("CN=zz Contoso CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        string pem = authority.ExportCertificatePem();

        await OpenAsync("/studio/#/signin", token);
        await SecuritySectionAsync("idp");
        await WaitForAsync(Shown("#idpNew"), "Organization › Security drew no Add a provider.");
        await ClickAsync("#idpNew");
        await WaitForAsync(Shown("[name=idpKind][value=ldap]"), "Add a provider offers no directory.");

        await Browser.EvaluateAsync<bool>(Change("[name=idpKind][value=ldap]", "ldap"));
        await WaitForAsync(Shown("#idpTrusted"), "A directory's form asks for no certificate authority to trust.");
        Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('idpNested').checked"), "Groups inside groups are on for a new directory.");

        await Browser.EvaluateAsync<bool>("(() => { document.getElementById('idpNested').closest('details').querySelector('summary').click(); return true; })()");
        await WaitForAsync(Shown("#idpNested"), "Opening Advanced did not show Follow groups inside groups.");
        await ClickAsync("#idpNested");

        await Browser.EvaluateAsync<bool>("(() => {"
            + " document.getElementById('idpName').value = 'zz Contoso';"
            + " document.getElementById('idpIssuer').value = 'ldaps://dc01.contoso.com';"
            + " document.getElementById('idpUserBase').value = 'OU=Staff,DC=contoso,DC=com';"
            + $" document.getElementById('idpTrusted').value = {JsonSerializer.Serialize(pem)};"
            + " return true; })()");

        await Browser.EvaluateAsync<bool>(KeepBodies);
        await ClickAsync("#idpSave");
        await WaitForAsync("window.__bodies.some(b => b.includes('\\\"nestedGroups\\\":true') && b.includes('BEGIN CERTIFICATE'))",
            "Add sent no directory with its authority to trust and groups inside groups on.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_new_saml_provider_gives_this_servers_identifier_and_reply_url_and_asks_for_metadata()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/signin", token);
        await SecuritySectionAsync("idp");
        await WaitForAsync(Shown("#idpNew"), "Organization › Security drew no Add a provider.");
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

    /// <summary>
    /// ADR-090 condition 3 on screen: a SAML provider set to require encrypted assertions says when this server's key was
    /// made and until when its certificate says it is good, offers a new key, and Save sends the setting as it stands.
    /// </summary>
    [Fact]
    public async Task A_saml_provider_requiring_encryption_says_its_key_and_offers_a_new_one()
    {
        (string token, _) = await SignInAsync();
        string name = "zz SAML enc " + Guid.NewGuid().ToString("N")[..8];

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new { name, kind = "saml", metadata = SamlMetadata(), encryptAssertions = true }));
        Assert.True(status == 201, $"Adding a SAML provider answered {status}: {body}");
        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync("/studio/#/signin", token);
            await SecuritySectionAsync("idp");
            string row = $"[...document.querySelectorAll('#idpRows tr')].find(r => r.textContent.includes({JsonSerializer.Serialize(name)}))";
            await WaitForAsync($"!!({row})", "Organization › Security does not list the SAML provider just added.");
            await Browser.EvaluateAsync<bool>($"(() => {{ ({row}).querySelector('[data-idp-edit]').click(); return true; }})()");

            await WaitForAsync(Shown("#idpEncrypt"), "Editing the SAML provider offers no encryption setting.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('idpEncrypt').checked"), "Require encrypted assertions is not shown on.");
            await WaitForAsync("(document.getElementById('idpEncryptHint')?.textContent || '').includes('good until')",
                "The encryption hint does not say until when the key's certificate is good.");
            await WaitForAsync(Shown("#idpRotateKey"), "A provider with a key offers no new key.");
            await WaitForAsync(Shown("#idpKeyCert"), "A provider requiring encryption offers no certificate to download.");

            // A new key leaves what has been typed but not saved where it is (design review 2026-10-31).
            await Browser.EvaluateAsync<bool>("(() => { document.getElementById('idpName').value = 'zz typed, not saved'; return true; })()");
            await ClickAsync("#idpRotateKey");
            await WaitForAsync("(window.__writes || []).some(w => w.startsWith('POST') && w.includes('/saml/encryption-key'))",
                "Make a new key sent nothing.");
            Assert.Equal("zz typed, not saved", await Browser.EvaluateAsync<string>("document.getElementById('idpName').value"));
            await Browser.EvaluateAsync<bool>($"(() => {{ document.getElementById('idpName').value = {JsonSerializer.Serialize(name)}; return true; }})()");

            await Browser.EvaluateAsync<bool>(KeepBodies);
            await ClickAsync("#idpSave");
            await WaitForAsync("window.__bodies.some(b => b.includes('\\\"encryptAssertions\\\":true'))",
                "Saving did not send the encryption setting as it stands.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
        }
    }

    [Fact]
    public async Task A_saml_provider_is_listed_as_saml_and_offered_on_the_sign_in_panel_at_its_own_start()
    {
        (string token, _) = await SignInAsync();
        (string id, string name) = await SamlAsync();

        try
        {
            await OpenAsync("/studio/#/signin", token);
            await SecuritySectionAsync("idp");
            string row = $"[...document.querySelectorAll('#idpRows tr')].find(r => r.textContent.includes({JsonSerializer.Serialize(name)}))";
            await WaitForAsync($"!!({row})", "Organization › Security does not list the SAML provider just added.");
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

    /// <summary>
    /// ADR-315 on screen: a certificate sign-in says first what this server receives, asks which field of the
    /// certificate names the account, sends it, is listed as a certificate with no Groups, and Check says what the
    /// server receives. Added turned off, so it never takes the place of a PKI provider the fixture already holds.
    /// </summary>
    [Fact]
    public async Task A_certificate_sign_in_says_what_this_server_receives_and_which_field_names_the_account()
    {
        (string token, _) = await SignInAsync();
        string name = "zz Card " + Guid.NewGuid().ToString("N")[..8];
        string? id = null;

        try
        {
            await OpenAsync("/studio/#/signin", token);
            await SecuritySectionAsync("idp");
            await WaitForAsync(Shown("#idpNew"), "Organization › Security drew no Add a provider.");
            await ClickAsync("#idpNew");
            await WaitForAsync(Shown("[name=idpKind][value=pki]"), "Add a provider offers no certificate sign-in.");

            await Browser.EvaluateAsync<bool>(Change("[name=idpKind][value=pki]", "pki"));
            await WaitForAsync(Shown("#idpClaim"), "Choosing a certificate asked for no field.");
            Assert.Equal("cn", await Browser.EvaluateAsync<string>("document.getElementById('idpClaim').value"));
            Assert.True(await Browser.EvaluateAsync<bool>("!document.getElementById('idpIssuer') && !document.getElementById('idpSecret')"),
                "A certificate sign-in is asked for an issuer or a secret.");
            string said = await Browser.EvaluateAsync<string>("document.getElementById('idpPkiTransport').textContent") ?? string.Empty;
            Assert.True(said.Contains("This server", StringComparison.Ordinal), $"The form does not say what this server receives: {said}");

            await Browser.EvaluateAsync<bool>($"(() => {{ document.getElementById('idpName').value = {JsonSerializer.Serialize(name)}; return true; }})()");
            await Browser.EvaluateAsync<bool>(Change("#idpClaim", "email"));
            await Browser.EvaluateAsync<bool>("(() => { document.getElementById('idpEnabled').checked = false; return true; })()");
            await Browser.EvaluateAsync<bool>(KeepBodies);

            // This test's own provider reaches the server — added turned off — and the finally removes it.
            await Browser.EvaluateAsync<bool>("(() => { window.__letWritesTo = ['/admin/identity-providers']; return true; })()");
            await ClickAsync("#idpSave");
            await WaitForAsync("window.__bodies.some(b => b.includes('\\\"kind\\\":\\\"pki\\\"') && b.includes('\\\"usernameClaim\\\":\\\"email\\\"'))",
                "Add sent no certificate sign-in named by e-mail.");

            string row = $"[...document.querySelectorAll('#idpRows tr')].find(r => r.textContent.includes({JsonSerializer.Serialize(name)}))";
            await WaitForAsync("(document.getElementById('idpSays')?.textContent || '').length > 0", "Add said nothing.");
            string saved = await Browser.EvaluateAsync<string>("document.getElementById('idpSays').textContent") ?? string.Empty;
            Assert.True(saved.Contains("is saved", StringComparison.Ordinal), $"Add did not save the certificate sign-in: {saved}");
            await WaitForAsync($"!!({row})", "Organization › Security does not list the certificate sign-in just added.");
            string cell = await Browser.EvaluateAsync<string>($"({row}).querySelector('.idpissuer').textContent") ?? string.Empty;
            Assert.Contains("Certificate", cell, StringComparison.Ordinal);
            Assert.Contains("e-mail address", cell, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>($"!({row}).querySelector('[data-idp-groups]')"), "A certificate sign-in offers Groups, which a certificate does not carry.");

            id = await Browser.EvaluateAsync<string>($"(idpList.find(p => p.name === {JsonSerializer.Serialize(name)}) || {{}}).id || ''");

            // Design review 2026-11-23 (4 and 11): whether this server is set up for certificates is the fixture's, so
            // the test reads it and holds the screen to each answer — the row says it when it is not, and Check says it
            // as a warning with the guide's section rather than as a refusal.
            bool setUp = await Browser.EvaluateAsync<bool>("!!(idpMeta.clientCertificates || {}).enabled");
            string named = await Browser.EvaluateAsync<string>($"({row}).querySelector('td.name').textContent") ?? string.Empty;
            Assert.True(named.Contains("not set up for certificates", StringComparison.Ordinal) != setUp,
                $"The row does not say whether this server is set up for certificates (set up: {setUp}): {named}");

            await Browser.EvaluateAsync<bool>(InRow(name, "[data-idp-check]"));
            if (setUp)
            {
                await WaitForAsync("(document.getElementById('idpSays')?.textContent || '').includes('receives certificates')",
                    "Check did not say what this server receives.");
            }
            else
            {
                await WaitForAsync("(document.getElementById('idpSays')?.textContent || '').includes('Deployment guide §6')",
                    "Check on a server not set up for certificates did not point at the deployment guide's section.");
                Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('idpSays').classList.contains('warn-inline')"),
                    "A server not set up for certificates is said as a refusal, not a warning.");
            }

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (string.IsNullOrEmpty(id))
            {
                (int _, string list) = await AdminAsync(HttpMethod.Get, "/admin/identity-providers");
                foreach (JsonElement p in JsonDocument.Parse(list).RootElement.GetProperty("providers").EnumerateArray())
                {
                    if (p.GetProperty("name").GetString() == name) id = p.GetProperty("id").GetString();
                }
            }

            if (!string.IsNullOrEmpty(id))
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{id}");
            }
        }
    }

    /// <summary>
    /// ADR-316 on screen: a Windows sign-in says first whether this server is set up for it, asks for no address, field
    /// or secret, sends its kind, and is listed as Windows with no Groups. Added turned off.
    /// </summary>
    [Fact]
    public async Task A_windows_sign_in_says_whether_this_server_is_set_up_for_it()
    {
        (string token, _) = await SignInAsync();
        string name = "zz Windows " + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await OpenAsync("/studio/#/signin", token);
            await SecuritySectionAsync("idp");
            await WaitForAsync(Shown("#idpNew"), "Organization › Security drew no Add a provider.");
            await ClickAsync("#idpNew");
            await WaitForAsync(Shown("[name=idpKind][value=iwa]"), "Add a provider offers no Windows sign-in.");

            await Browser.EvaluateAsync<bool>(Change("[name=idpKind][value=iwa]", "iwa"));
            await WaitForAsync(Shown("#idpIwaTransport"), "Choosing Windows said nothing about this server.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "!document.getElementById('idpIssuer') && !document.getElementById('idpSecret') && !document.getElementById('idpClaim')"),
                "A Windows sign-in is asked for an issuer, a secret or a field.");
            // Design review 2026-11-23 (5): the template literal ate the backslash, and the example read CONTOSOjane.
            string form = await Browser.EvaluateAsync<string>("document.getElementById('idpForm').textContent") ?? string.Empty;
            Assert.Contains("CONTOSO\\jane", form, StringComparison.Ordinal);

            await Browser.EvaluateAsync<bool>($"(() => {{ document.getElementById('idpName').value = {JsonSerializer.Serialize(name)}; return true; }})()");
            await Browser.EvaluateAsync<bool>("(() => { document.getElementById('idpEnabled').checked = false; return true; })()");
            await Browser.EvaluateAsync<bool>(KeepBodies);
            await Browser.EvaluateAsync<bool>("(() => { window.__letWritesTo = ['/admin/identity-providers']; return true; })()");
            await ClickAsync("#idpSave");
            await WaitForAsync("window.__bodies.some(b => b.includes('\\\"kind\\\":\\\"iwa\\\"'))", "Add sent no Windows sign-in.");

            string row = $"[...document.querySelectorAll('#idpRows tr')].find(r => r.textContent.includes({JsonSerializer.Serialize(name)}))";
            await WaitForAsync($"!!({row})", "Organization › Security does not list the Windows sign-in just added.");
            string cell = await Browser.EvaluateAsync<string>($"({row}).querySelector('.idpissuer').textContent") ?? string.Empty;
            Assert.Contains("Windows", cell, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>($"!({row}).querySelector('[data-idp-groups]')"), "A Windows sign-in offers Groups.");
            bool setUp = await Browser.EvaluateAsync<bool>("!!(idpMeta.windows || {}).enabled");
            string named = await Browser.EvaluateAsync<string>($"({row}).querySelector('td.name').textContent") ?? string.Empty;
            Assert.True(named.Contains("not set up for Windows sign-in", StringComparison.Ordinal) != setUp,
                $"The row does not say whether this server is set up for Windows sign-in (set up: {setUp}): {named}");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            (int _, string list) = await AdminAsync(HttpMethod.Get, "/admin/identity-providers");
            foreach (JsonElement p in JsonDocument.Parse(list).RootElement.GetProperty("providers").EnumerateArray())
            {
                if (p.GetProperty("name").GetString() == name)
                {
                    await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{p.GetProperty("id").GetString()}");
                }
            }
        }
    }

    /// <summary>
    /// Design review 2026-11-23 (1, 2 and 12): a refused Add is said inside the dialog, where it can be read, with the
    /// focus kept on Add; Escape and ✕ give the focus back to Add a provider rather than to the page's body; and
    /// <c>?tab=idp</c> opens Security at Sign-in, which Members' <i>change on Sign-in</i> links to. Nothing reaches the
    /// server: the refusal is the page's own fetch answered here.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_said_in_the_dialog_and_closing_it_gives_the_focus_back()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/signin?tab=idp", token);
        await WaitForAsync("document.getElementById('view-signin')?.dataset.secpage === 'idp'",
            "?tab=idp did not open Security at Sign-in.");
        await WaitForAsync(Shown("#idpNew"), "Organization › Security drew no Add a provider.");

        const string Focused = "document.activeElement && document.activeElement.id";

        await ClickAsync("#idpNew");
        await WaitForAsync("document.getElementById('idpDialog').open", "Add a provider opened no dialog.");
        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 });
        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 });
        await WaitForAsync("!document.getElementById('idpDialog').open", "Escape did not close the dialog.");
        await WaitForAsync($"{Focused} === 'idpNew'", "Escape left the focus on the page's body, not on Add a provider.");

        await ClickAsync("#idpNew");
        await WaitForAsync("document.getElementById('idpDialog').open", "Add a provider opened no dialog the second time.");
        await ClickAsync("#idpDialogClose");
        await WaitForAsync($"{Focused} === 'idpNew'", "✕ left the focus on the page's body, not on Add a provider.");

        await ClickAsync("#idpNew");
        await WaitForAsync(Shown("[name=idpKind][value=pki]"), "Add a provider offers no certificate sign-in.");
        await Browser.EvaluateAsync<bool>(Change("[name=idpKind][value=pki]", "pki"));
        await Browser.EvaluateAsync<bool>("(() => { document.getElementById('idpName').value = 'zz refused'; return true; })()");
        await Browser.EvaluateAsync<bool>(
            "(() => { const f = window.fetch; window.fetch = (i, init) => {"
            + " const url = typeof i === 'string' ? i : i.url;"
            + " if (init && init.method === 'POST' && /\\/admin\\/identity-providers$/.test(url))"
            + "   return Promise.resolve(new Response(JSON.stringify({ error: { message: 'Refused here.' } }),"
            + "     { status: 409, headers: { 'Content-Type': 'application/json' } }));"
            + " return f(i, init); }; return true; })()");
        await ClickAsync("#idpSave");

        await WaitForAsync("(document.getElementById('idpFormSays')?.textContent || '').includes('Refused here.')",
            "A refused Add was not said inside the dialog.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('idpDialog').open"), "A refused Add closed the dialog.");
        Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>("document.getElementById('idpSays').textContent"));
        Assert.Equal("idpSave", await Browser.EvaluateAsync<string>(Focused));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_new_domains_kind_redraws_its_editor_with_no_fields_editor_open()
    {
        (string token, _) = await SignInAsync();

        // The list of domains is under Define your own layer since ADR-189, and its editor is a dialog.
        await OpenAsync("/studio/#/content", token);
        await WaitForAsync(Shown("#newLayer"), "Content drew no New item.");
        await ClickAsync("#newLayer");
        await WaitForAsync(Shown("#kindFeatureLayer"), "New item offered no feature layer.");
        await ClickAsync("#kindFeatureLayer");
        await WaitForAsync(Shown("[data-route=design]"), "Create a feature layer offered no Define your own layer.");
        await ClickAsync("[data-route=design]");
        await ClickAsync("#itemNext");
        await WaitForAsync(Shown("#domainNew"), "Define your own layer drew no New domain.");
        await ClickAsync("#domainNew");

        await WaitForAsync(Shown("#domEditName"), "New domain opened no editor.");
        await Browser.EvaluateAsync<bool>(Change("[name=domEditKind][value=range]", "range"));

        await WaitForAsync(Shown("#domEditLeast"), "Choosing a range drew no From and To.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
