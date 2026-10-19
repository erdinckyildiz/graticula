using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A second factor on screen — ADR-201: set up inside the sign-in when the organisation requires one (condition 2), kept
/// on My profile › Security, required on Organization › Settings › Security, and the line an administrator without one
/// is shown on every screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server's answers to the writes are scripted here</b>, because this suite never sends a write: the harness
/// records it and answers <c>{}</c> (<see cref="ConsoleTest"/>'s fetch trap). What the server does with a code is held
/// by the Host and conformance suites; what these hold is that the screens put the right question to the right reader
/// and act on each answer — the setup secret, a refused code, the recovery codes, the list of who will be asked. The
/// reads are real.
/// </para>
/// <para>
/// <b>Driven as the reader who needs each control.</b> The setup step is opened with no session, My profile as a member
/// with the <c>user</c> role, and the notice is checked for an administrator and for that member. A control that exists
/// and is not seen by its reader has shipped three times in this console with the suite green, so every wait here is
/// <see cref="ConsoleTest.Shown"/>.
/// </para>
/// </remarks>
public sealed class SecondFactorScreensTests : ConsoleTest
{
    private const string Password = "Zz-second-factor-2026!";
    private const string Secret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Right = "123456";

    /// <summary>
    /// Answers the matching writes with a script instead of the harness's <c>{}</c>. <c>answer</c> is a JavaScript
    /// function of the parsed body returning <c>[status, body]</c>; the write is still recorded.
    /// </summary>
    private static string Script(string method, string path, string answer) =>
        "(() => { const prev = window.fetch; const answer = " + answer + "; window.fetch = async (input, init) => {"
        + " const url = typeof input === 'string' ? input : input.url;"
        + " const method = ((init && init.method) || 'GET').toUpperCase();"
        + $" if (method === {JsonSerializer.Serialize(method)} && url.split('?')[0] === {JsonSerializer.Serialize(path)}) {{"
        + "  window.__writes.push(method + ' ' + url);"
        + "  const [status, body] = answer(init && init.body ? JSON.parse(init.body) : null);"
        + "  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }); }"
        + " return prev(input, init); }; return true; })()";

    private static string SetupRefusal(string message) =>
        "[401, { error: { code: 401, message: " + JsonSerializer.Serialize(message) + ", details: ['secondFactorSetup'] },"
        + $" secondFactorSetup: {{ secret: '{Secret}', address: 'otpauth://totp/Graticula%20(test):zz?secret={Secret}&issuer=Graticula' }} }}]";

    private static readonly string Codes =
        "['aaaaa-bbbbb','ccccc-ddddd','eeeee-fffff','ggggg-hhhhh','iiiii-jjjjj','kkkkk-lllll','mmmmm-nnnnn','ooooo-ppppp','qqqqq-rrrrr','sssss-ttttt']";

    [Fact]
    public async Task A_member_the_organisation_requires_a_factor_of_sets_it_up_while_signing_in_and_sees_the_codes_once()
    {
        await SignInAsync();
        string member = await MemberAsync("zzsfa");

        try
        {
            string session = await MemberTokenAsync(member);
            await OpenAsync("/studio/", token: null);
            await WaitForAsync(Shown("#u"), "The sign-in card was not drawn.");

            await Browser.EvaluateAsync<bool>(Script("POST", "/rest/auth/login",
                "b => !b.code ? " + SetupRefusal("Your organisation requires a second factor.")
                + $" : b.code !== '{Right}' ? " + SetupRefusal("The code is incorrect or has been used.")
                + $" : [200, {{ token: {JsonSerializer.Serialize(session)}, recoveryCodes: {Codes} }}]"));

            await TypeAsync("u", member);
            await TypeAsync("p", Password);
            await ClickAsync("#go");

            await WaitForAsync(Shown("#signinSetupKeyKey"), "A sign-in the organisation requires a factor of drew no setup step.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#u")), "The name and password stayed on screen beside the setup.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("a.sfopen[href^='otpauth://']")), "The setup offered no link to the authenticator app.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("[data-sf-copy-key]")), "The setup offered no way to copy the key.");
            Assert.Equal(Secret, await KeyAsync("#signinSetupKeyKey"));
            Assert.Equal("signinSetupTitle", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

            // A wrong code stays on the step and says so beside the field, in the console's words.
            await TypeAsync("signinSetupCode", "000000");
            await ClickAsync("#signinSetupGo");
            await WaitForAsync(Shown("#signinSetupError"), "A wrong code said nothing on the setup step.");
            Assert.Contains("did not match", await Browser.EvaluateAsync<string>("document.getElementById('signinSetupError').textContent") ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal("signinSetupCode", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

            await TypeAsync("signinSetupCode", "123 456");
            await ClickAsync("#signinSetupGo");

            await WaitForAsync(Shown("#signinRecoveryGo"), "Setting the factor up showed no recovery codes.");
            Assert.Equal(10, await Browser.EvaluateAsync<int>("document.querySelectorAll('#signinRecoveryCodes ol.sfcodes li').length"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('signinRecoveryGo').disabled"),
                "The console could be entered before the reader said the codes were kept.");
            Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>("document.getElementById('p').value"));
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("[data-sf-download]")), "The codes offered no download.");

            await ClickAsync("#signinRecoveryCodesSaved");
            await WaitForAsync("!document.getElementById('signinRecoveryGo').disabled", "Ticking the box did not enable Continue.");
            await ClickAsync("#signinRecoveryGo");
            await WaitForAsync(
                "getComputedStyle(document.getElementById('app')).display !== 'none'",
                "Continue after the recovery codes did not open the console.");
            Assert.Equal(3, Array.FindAll(await WritesAsync(), w => w.Contains("/rest/auth/login", StringComparison.Ordinal)).Length);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}?deleteOwned=true");
        }
    }

    [Fact]
    public async Task A_member_turns_their_own_factor_on_and_off_from_their_profile()
    {
        await SignInAsync();
        string member = await MemberAsync("zzsfb");

        try
        {
            await OpenAsync($"/studio/user/{member}?security", await MemberTokenAsync(member));

            // The read is real: a member with a password here and none set up.
            await WaitForAsync(Shown("#pfSfOn"), "My profile showed a member no way to turn two-step sign-in on.");
            Assert.Equal("pfSecTitle", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

            await Browser.EvaluateAsync<bool>(Script("POST", "/rest/auth/second-factor",
                $"() => [200, {{ secret: '{Secret}', address: 'otpauth://totp/Graticula:zz?secret={Secret}' }}]"));
            await Browser.EvaluateAsync<bool>(Script("POST", "/rest/auth/second-factor/confirm",
                $"b => b.code === '{Right}' ? [200, {{ enabled: true, recoveryCodes: {Codes} }}]"
                + " : [400, { error: { code: 400, message: 'The code does not match.' } }]"));
            await Browser.EvaluateAsync<bool>(Script("POST", "/rest/auth/second-factor/remove",
                "b => b.code === 'aaaaa-bbbbb' ? [200, { enabled: false }] : [400, { error: { code: 400, message: 'no' } }]"));

            await ClickAsync("#pfSfOn");
            await WaitForAsync(Shown("#pfSfKeyKey"), "Turn on drew no key.");
            Assert.Equal("pfSfStepTitle", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

            await TypeAsync("pfSfCode", "000000");
            await ClickAsync("#pfSfConfirm");
            await WaitForAsync(Shown("#pfSfError"), "A wrong code said nothing.");
            Assert.Equal("pfSfCode", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

            await TypeAsync("pfSfCode", Right);
            await ClickAsync("#pfSfConfirm");
            await WaitForAsync(Shown("#pfSfDone"), "Confirming showed no recovery codes.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('pfSfDone').disabled"),
                "Done worked before the codes were said to be kept.");

            await ClickAsync("#pfSfCodesSaved");
            await ClickAsync("#pfSfDone");
            await WaitForAsync(Shown("#pfSfOff"), "After Done, the profile did not say the factor is on.");
            Assert.Equal("pfSfOff", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

            // Off takes a code, and a recovery code is one.
            await ClickAsync("#pfSfOff");
            await WaitForAsync(Shown("#pfSfRemove"), "Turn off asked for no code.");
            await TypeAsync("pfSfCode", "aaaaa-bbbbb");
            await ClickAsync("#pfSfRemove");
            await WaitForAsync(Shown("#pfSfOn"), "A recovery code did not turn the factor off.");
            Assert.Contains("is off", await Browser.EvaluateAsync<string>("document.getElementById('pfSecondSays').textContent") ?? string.Empty, StringComparison.Ordinal);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}?deleteOwned=true");
        }
    }

    [Fact]
    public async Task Requiring_a_factor_is_asked_about_first_and_names_who_will_be_asked()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/signin", token);
        await WaitForAsync(Shown("input[name=sfRequirePick][value=administrators]"),
            "Organization › Settings › Security drew no two-step sign-in requirement.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('sfRequireSave').disabled"),
            "Save was offered before anything changed.");

        await Browser.EvaluateAsync<bool>(Script("PUT", "/admin/security/second-factor",
            "b => [200, { required: b.required, choices: ['off','administrators','everyone'], askedAtNextSignIn: ['zz_one', 'zz_two'] }]"));

        await ClickAsync("input[name=sfRequirePick][value=administrators]");
        await WaitForAsync("!document.getElementById('sfRequireSave').disabled", "Choosing administrators left Save off.");
        Assert.Contains("API key", await Browser.EvaluateAsync<string>("document.getElementById('sfRequireCost').textContent") ?? string.Empty, StringComparison.Ordinal);

        await ClickAsync("#sfRequireSave");
        await WaitForAsync("document.getElementById('sfRequireDialog').open", "Turning the requirement on asked nothing first.");
        Assert.Equal("sfRequireCancel", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));
        Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/admin/security/second-factor", StringComparison.Ordinal));

        await ClickAsync("#sfRequireConfirm");
        await WaitForAsync(Shown("ul.sfasked a[href$='/studio/user/zz_two']"), "Saving named nobody who will be asked at their next sign-in.");
        Assert.Contains(await WritesAsync(), w => w.StartsWith("PUT", StringComparison.Ordinal) && w.Contains("/admin/security/second-factor", StringComparison.Ordinal));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task An_administrator_without_a_factor_is_told_and_a_member_is_not()
    {
        (string token, _) = await SignInAsync();
        string member = await MemberAsync("zzsfd");

        try
        {
            string state = (await AdminAsync(HttpMethod.Get, "/rest/auth/second-factor")).Body;
            bool owes = state.Contains("\"available\":true", StringComparison.Ordinal)
                && state.Contains("\"enabled\":false", StringComparison.Ordinal);

            await OpenAsync("/studio/content", token);
            await WaitForAsync("getComputedStyle(document.getElementById('app')).display !== 'none'", "The console did not open.");

            if (owes)
            {
                await WaitForAsync(Shown("#factorNotice"), "An administrator signing in with a password alone was not told.");
                Assert.Contains("?security", await Browser.EvaluateAsync<string>("document.getElementById('factorNoticeGo').href") ?? string.Empty, StringComparison.Ordinal);
            }

            await OpenAsync("/studio/content", await MemberTokenAsync(member));
            await WaitForAsync("getComputedStyle(document.getElementById('app')).display !== 'none'", "The console did not open for a member.");
            await Task.Delay(1000);
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#factorNotice")), "A member who is not an administrator was shown the notice.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}?deleteOwned=true");
        }
    }

    /// <summary>A member with the <c>user</c> role and their own password, as a person would have one.</summary>
    private async Task<string> MemberAsync(string prefix)
    {
        string name = prefix + Guid.NewGuid().ToString("N")[..8];
        (int status, string body) = await AdminAsync(
            HttpMethod.Post, "/admin/members", JsonSerializer.Serialize(new { name, role = "user", userType = "creator" }));
        Assert.True(status is 200 or 201, $"Adding a member answered {status}: {body}");

        string issued = JsonDocument.Parse(body).RootElement.GetProperty("password").GetString()!;
        string first = (await PostAsync("/rest/auth/login", new { name, password = issued })).GetProperty("token").GetString()!;
        await PostAsync("/rest/auth/password", new { currentPassword = issued, newPassword = Password }, first);
        return name;
    }

    private async Task<string> MemberTokenAsync(string name) =>
        (await PostAsync("/rest/auth/login", new { name, password = Password })).GetProperty("token").GetString()!;

    private async Task<JsonElement> PostAsync(string path, object body, string? token = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}{path}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST {path} answered {(int)response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task TypeAsync(string id, string text) =>
        await Browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.getElementById({JsonSerializer.Serialize(id)}); e.value = {JsonSerializer.Serialize(text)}; "
            + "e.dispatchEvent(new Event('input', { bubbles: true })); return true; })()");

    private async Task<string> KeyAsync(string selector) =>
        (await Browser.EvaluateAsync<string>($"document.querySelector({JsonSerializer.Serialize(selector)}).textContent") ?? string.Empty)
        .Replace(" ", string.Empty, StringComparison.Ordinal);
}
