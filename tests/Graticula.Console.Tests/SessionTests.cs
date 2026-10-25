using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// What the console shows a reader, given what they are holding.
/// </summary>
public sealed class SessionTests : ConsoleTest
{
    /// <summary>
    /// A browser with the cookie and no token is offered the form, and told why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-59's second defect.</b> The owner signed in through the services
    /// directory's own form, came to the console, pressed a button, and was told
    /// <em>"this needs the 'admin:manageServer' privilege and you are not signed
    /// in"</em> — on a page whose header had just named them as the administrator.
    /// </para>
    /// <para>
    /// <b>The cause was one question asked the wrong way round.</b> The boot gated
    /// on <c>me.authenticated</c>, and with a <c>gis-session</c> cookie
    /// <c>/rest/whoami</c> answers <c>authenticated: true</c> with
    /// <c>admin:manageServer</c> in the list. It is true — that reader is
    /// authenticated, and per ADR-023 §4c the cookie authenticates <c>GET</c> and
    /// <c>HEAD</c> and nothing else. So the whole surface painted and every write
    /// answered 401. What the console has to ask is whether it holds a token.
    /// </para>
    /// <para>
    /// <b>Both halves are asserted, because either alone passes for the wrong
    /// reason.</b> That the console is not painted would also be true for an
    /// anonymous visitor; that the reason is shown is what distinguishes this
    /// reader from one, and it is the part a person needs — being asked to sign in
    /// while signed in is exactly the confusion that cost the round trip.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cookie_without_a_token_is_offered_the_form_with_the_reason()
    {
        (_, string cookie) = await SignInAsync();

        // The cookie and deliberately not the token: this is the state the
        // directory's own sign-in form leaves a browser in.
        await OpenAsync("/server/#/services", token: null, cookie: cookie);

        // <b>Waiting for the console to have decided, not for the form to be
        // there.</b> The form is in the markup and visible from the first paint —
        // `#app` is the one that starts hidden — so waiting on the form passed
        // instantly, before `whoami` had been answered, and the assertions below
        // ran against a page that had not made up its mind yet. It failed one run
        // in three. `#who` is filled on both branches of that decision, which
        // makes it the signal that the decision has been made.
        await WaitForAsync(
            "document.getElementById('who').textContent.trim().length > 0",
            "The console never said who it thought was reading it, so /rest/whoami did not come "
            + "back within ten seconds.");

        Assert.NotEqual(
            "none",
            await Browser.EvaluateAsync<string>(
                "getComputedStyle(document.getElementById('signin')).display"));

        Assert.Equal(
            "none",
            await Browser.EvaluateAsync<string>(
                "getComputedStyle(document.getElementById('app')).display"));

        Assert.False(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('signinCookie').hidden"),
            "The form was shown without the explanation, so an administrator is being asked to "
            + "sign in while signed in and given no reason.");

        Assert.Contains(
            "read-only",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('who').textContent") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A browser holding a token is shown the console.
    /// </summary>
    /// <remarks>
    /// <b>The pair to the test above, for the same reason the row tests come in
    /// two.</b> The cheapest way to satisfy "a cookie session is not painted as an
    /// administrator" is to paint nobody, and that regression is a console that
    /// never opens for anyone.
    /// </remarks>
    [Fact]
    public async Task A_token_is_shown_the_console()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/services", token);

        await WaitForAsync(
            "!!document.getElementById('app')"
            + " && getComputedStyle(document.getElementById('app')).display !== 'none'",
            "An administrator holding a bearer token was not shown the console.");

        Assert.Equal(
            "none",
            await Browser.EvaluateAsync<string>(
                "getComputedStyle(document.getElementById('signin')).display"));
    }

    /// <summary>
    /// A member holding the password they were issued sees the form that replaces it, and nothing
    /// else, until it has been replaced.
    /// </summary>
    /// <remarks>
    /// <b>Owner report 2026-10-25: the form came up with Services written under it.</b> The boot set
    /// <c>data-session="held"</c> before it checked the flag, and the stylesheet shows <c>#app</c>
    /// with <c>!important</c> under that attribute, so the inline <c>none</c> the branch set lost.
    /// Nothing tested this screen at all. The second half — the console opening once the password is
    /// replaced — is asserted because the cheapest fix for the first is an <c>#app</c> that never
    /// shows.
    /// </remarks>
    [Fact]
    public async Task An_issued_password_is_shown_its_form_and_nothing_under_it()
    {
        string name = "zz_issued_" + Guid.NewGuid().ToString("N")[..8];
        (int made, string answer) = await AdminAsync(
            HttpMethod.Post, "/admin/members", JsonSerializer.Serialize(new { name, role = "user", userType = "creator" }));
        Assert.True(made is 200 or 201, $"Adding a member answered {made}: {answer}");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;

        try
        {
            using HttpResponseMessage login = await Http.PostAsync(
                new Uri($"{Root}/rest/auth/login"),
                new StringContent(JsonSerializer.Serialize(new { name, password = issued }), Encoding.UTF8, "application/json"));
            string body = await login.Content.ReadAsStringAsync();
            Assert.True(login.IsSuccessStatusCode, $"Signing in with the issued password answered {(int)login.StatusCode}: {body}");
            string token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;

            await OpenAsync("/studio/", token);
            await WaitForAsync(Shown("#mustchange"), "A member holding an issued password was not shown the form that replaces it.");

            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#app")),
                "The console was painted under the issued-password form, and every screen in it answers 403.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#tabs")),
                "The sections bar was shown to a member who can open none of them.");

            // <b>The harness keeps the form's POST from the server</b>, so the password is replaced through the API and
            // the same session opened again — which is the state the form's own `start()` lands in.
            using HttpRequestMessage replace = new(HttpMethod.Post, new Uri($"{Root}/rest/auth/password"))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { currentPassword = issued, newPassword = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7" }),
                    Encoding.UTF8,
                    "application/json"),
            };
            replace.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage replaced = await Http.SendAsync(replace);
            Assert.True(replaced.IsSuccessStatusCode, $"Replacing the issued password answered {(int)replaced.StatusCode}.");

            await OpenAsync("/studio/", token);
            await WaitForAsync(Shown("#app"), "The console did not open once the issued password had been replaced.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#mustchange")),
                "The issued-password form stayed on screen after the password was replaced.");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{name}?deleteOwned=true");
        }
    }
}
