using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The question a removal puts to the operator, on the screen where they answer it.
/// </summary>
/// <remarks>
/// <b>ADR-015 §6c is a decision about who decides.</b> The server refuses a removal that did not
/// say what to do with what a member owns; the console's job is to put that question, and a dialog
/// with a disposition preselected has answered it instead of asking. So what these tests hold is
/// the shape of the asking: the counts are shown, both dispositions are offered, and neither is
/// chosen for the operator.
/// </remarks>
public sealed class MemberRemovalPageTests : ConsoleTest
{
    /// <summary>
    /// Removing yourself is refused before the question is put.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one an operator actually meets, and [D-101](../../docs/architecture-debt.md)
    /// did not separate it out.</b> A server with one administrator is the ordinary state of a
    /// fresh install, and the only person who can reach the Remove button on that administrator
    /// is that administrator. The server refuses it first, before the disposition is even read —
    /// so nothing was ever destroyed on this path — but the console still opened a panel offering
    /// to transfer or delete an estate, and every answer to it was already *no*.
    /// </para>
    /// <para>
    /// <b>The signed-in name comes from the page.</b> `whoami` sets it and the removal handler
    /// compares against it, so reading it here asks the page the same question the guard does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Removing_yourself_is_refused_before_the_panel_opens()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/members", token);

        await WaitForAsync(
            "document.querySelector('#members tr button[data-member-remove]')",
            "The Members screen offered no Remove button, so there is nothing to click.");

        string me = await Browser.EvaluateAsync<string>("signedInAs") ?? string.Empty;

        Assert.False(me.Length == 0, "the page does not know who is signed in");

        await WaitForAsync(
            $"document.querySelector('#members tr button[data-member-remove=\"{me}\"]')",
            $"'{me}' is signed in and has no row on the members screen, so this cannot be tested "
            + "the way an operator would meet it.");

        await ClickAsync($"#members tr button[data-member-remove=\"{me}\"]");

        await WaitForAsync(
            "document.getElementById('toast')?.textContent.includes('cannot remove yourself')",
            "Clicking Remove on your own account said nothing, so the panel asked which "
            + "disposition to use for a removal the server refuses on sight.");

        Assert.Equal(
            "none",
            await Browser.EvaluateAsync<string>(
                "getComputedStyle(document.getElementById('removeMember')).display"));
    }

    /// <summary>
    /// The only administrator is refused before the question is put.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-101](../../docs/architecture-debt.md): an irreversible side effect on the way to a
    /// refusal.</b> The server refuses removing the last administrator, and the refusal is real —
    /// but the console opened the disposition panel first, and answering *delete what they own*
    /// unpublishes every layer and removes every service they own before the removal is even
    /// attempted. The listing already carries the roles, so the console knew the answer before
    /// it asked the question.
    /// </para>
    /// <para>
    /// <b>The membership is set from the test rather than read from the server</b>, so this
    /// holds whether the deployment has one administrator or five. `administrators` is a
    /// top-level binding filled by the listing; writing it is the same move this file already
    /// makes with `fetch`, and it means the test states its own premise instead of inheriting
    /// one from whoever last edited the members screen.
    /// </para>
    /// <para>
    /// <b>Nothing destructive can leave the page either way.</b> `fetch` is trapped for the whole
    /// test, so even a missing guard cannot reach the server.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_only_administrator_is_refused_before_the_panel_opens()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/members", token);

        await WaitForAsync(
            "document.querySelector('#members tr button[data-member-remove]')",
            "The Members screen offered no Remove button, so there is nothing to click.");

        // Every request from the page is answered here, and every one is recorded. A removal
        // that got past the guard would show up as a DELETE that this test can name.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          window.__asked = [];
          const real = window.fetch.bind(window);
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            window.__asked.push(((init && init.method) || "GET") + " " + url);
            if (url.includes("/holdings")) {
              return new Response(JSON.stringify({
                name: "somebody", owns: true, services: ["hosted/theirs"],
                folders: [], groups: 0, note: "owns things",
              }), { status: 200, headers: { "Content-Type": "application/json" } });
            }
            return real(input, init);
          };
          return true;
        })();
        """);

        // <b>Somebody other than the operator, and then that somebody is declared the
        // only administrator — 2026-08-25.</b> This read the *first* Remove button and
        // declared its member the only administrator, which works on a members list
        // that does not begin with the signed-in account. Where it does, the first
        // button is your own: the console refuses that with *cannot remove yourself*
        // before it ever reaches the administrator check, and this test waited for a
        // message it had made unreachable.
        //
        // The two facts have to agree — the member clicked must be the member declared —
        // so both are taken from the same selector rather than from two places that
        // happen to line up.
        string selector = await SomebodyElseAsync();

        string who = await Browser.EvaluateAsync<string>(
            $"document.querySelector(\"{selector}\").dataset.memberRemove") ?? string.Empty;

        Assert.False(who.Length == 0, "the Remove button names no member");

        // <b>This member, and nobody else, can administer.</b>
        await Browser.EvaluateAsync<bool>($"(() => {{ administrators = ['{who}']; return true; }})()");

        await ClickAsync(selector);

        await WaitForAsync(
            "document.getElementById('toast')?.textContent.includes('only administrator')",
            "Removing the only administrator said nothing. The server refuses it, so the operator "
            + "learns only after choosing a disposition — and one of the two dispositions has "
            + "already deleted their services by then.");

        Assert.Equal(
            "none",
            await Browser.EvaluateAsync<string>(
                "getComputedStyle(document.getElementById('removeMember')).display"));

        string asked = await Browser.EvaluateAsync<string>(
            "JSON.stringify(window.__asked)") ?? "[]";

        Assert.DoesNotContain("/holdings", asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// With somebody else to administer, the question is put as before.
    /// </summary>
    /// <remarks>
    /// <b>The other half, and it is the one a guard gets wrong.</b> A check that refuses too
    /// often is as broken as one that never fires, and it fails quietly: the operator simply
    /// cannot remove anybody. This asserts the panel still opens when the server would allow the
    /// removal.
    /// </remarks>
    [Fact]
    public async Task With_a_second_administrator_the_panel_opens_as_before()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/members", token);

        await WaitForAsync(
            "document.querySelector('#members tr button[data-member-remove]')",
            "The Members screen offered no Remove button, so there is nothing to click.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch.bind(window);
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            if (!url.includes("/holdings")) { return real(input, init); }
            return new Response(JSON.stringify({
              name: "somebody", owns: true, services: ["hosted/theirs"],
              folders: [], groups: 0, note: "owns things",
            }), { status: 200, headers: { "Content-Type": "application/json" } });
          };
          return true;
        })();
        """);

        string who = await Browser.EvaluateAsync<string>(
            "document.querySelector('#members tr button[data-member-remove]')"
            + ".dataset.memberRemove") ?? string.Empty;

        await Browser.EvaluateAsync<bool>(
            $"(() => {{ administrators = ['{who}', 'somebody-else']; return true; }})()");

        await ClickAsync(await SomebodyElseAsync());

        await WaitForAsync(
            "getComputedStyle(document.getElementById('removeMember')).display !== 'none'",
            "The removal panel did not open for a member the server would let go, so the guard "
            + "refuses more than the server does.");
    }

    /// <summary>
    /// A member who owns something gets the panel, with what they own named on it.
    /// </summary>
    /// <remarks>
    /// <b>The holdings are trapped rather than made.</b> This suite answers writes from inside the
    /// page (see <see cref="ConsoleTest"/>), and creating a member who owns services on the
    /// operator's own server to prove a dialog renders would be a test that changes the estate to
    /// look at a screen. The server's half is measured in
    /// <c>MemberRemovalTests</c> and against the running server on the ADR.
    /// </remarks>
    [Fact]
    public async Task Owning_something_puts_the_choice_on_the_screen()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/members", token);

        await WaitForAsync(
            "document.querySelector('#members tr button[data-member-remove]')",
            "The Members screen offered no Remove button, so ADR-015 §6c's question has nowhere to "
            + "be asked.");

        // The holdings this member will appear to have. Two kinds, so the panel cannot pass by
        // rendering one line and calling it a list.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch.bind(window);
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            if (!url.includes("/holdings")) { return real(input, init); }
            return new Response(JSON.stringify({
              name: "somebody", owns: true,
              services: ["hosted/theirs", "shelf/also_theirs"],
              folders: ["shelf"], groups: 0,
              note: "owns things",
            }), { status: 200, headers: { "Content-Type": "application/json" } });
          };
          return true;
        })();
        """);

        await ClickAsync(await SomebodyElseAsync());

        await WaitForAsync(
            "getComputedStyle(document.getElementById('removeMember')).display !== 'none'",
            "The removal panel never opened for a member who owns something, so the operator was "
            + "given no way to answer the question the server asks.");

        string held = await Browser.EvaluateAsync<string>(
            "document.getElementById('removeHolds').textContent") ?? string.Empty;

        // Named, not counted. A refusal saying *2 services* does not let anybody judge whether
        // transferring them is right, which is the whole reason the endpoint returns the names.
        Assert.Contains("hosted/theirs", held, StringComparison.Ordinal);
        Assert.Contains("shelf/also_theirs", held, StringComparison.Ordinal);
        Assert.Contains("shelf", held, StringComparison.Ordinal);

        // Both dispositions are offered.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "!!document.getElementById('removeTransfer') "
                + "&& !!document.getElementById('removeDelete')"),
            "The panel offered fewer than two dispositions, so it is not a choice.");

        // And neither is chosen: the transfer target starts empty, so pressing Transfer without
        // picking somebody cannot silently reassign to whoever happens to be first.
        Assert.Equal(
            string.Empty,
            await Browser.EvaluateAsync<string>(
                "document.getElementById('removeTo').value"));
    }

    /// <summary>
    /// Transfer with nobody chosen does not send a request.
    /// </summary>
    /// <remarks>
    /// <b>The failure this prevents is the quiet one.</b> An empty select and a pressed button
    /// would produce <c>?transferTo=</c> — which the server reads as *no disposition* and refuses
    /// with a 409 about holdings, sending the operator back to a screen they had just answered.
    /// Catching it here means the message names the thing they actually left out.
    /// </remarks>
    [Fact]
    public async Task Transfer_without_a_recipient_asks_rather_than_sending()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/members", token);

        await WaitForAsync(
            "document.querySelector('#members tr button[data-member-remove]')",
            "The Members screen offered no Remove button.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch.bind(window);
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            if (!url.includes("/holdings")) { return real(input, init); }
            return new Response(JSON.stringify({
              name: "somebody", owns: true, services: ["hosted/theirs"], folders: [], groups: 0,
              note: "owns things",
            }), { status: 200, headers: { "Content-Type": "application/json" } });
          };
          return true;
        })();
        """);

        await ClickAsync(await SomebodyElseAsync());

        await WaitForAsync(
            "getComputedStyle(document.getElementById('removeMember')).display !== 'none'",
            "The removal panel never opened.");

        await Browser.EvaluateAsync<bool>("(window.__writes = [], true)");
        await ClickAsync("#removeTransfer");

        // <b>Nothing left the page.</b> The harness records every non-GET; an empty recipient must
        // produce no request at all rather than one the server has to refuse.
        await Task.Delay(600);

        Assert.Empty(await WritesAsync());

        Assert.NotEqual(
            "none",
            await Browser.EvaluateAsync<string>(
                "getComputedStyle(document.getElementById('removeMember')).display"));
    }

    /// <summary>A Remove button for a member who is not the one signed in.</summary>
    /// <remarks>
    /// <para>
    /// <b>Four tests in this file clicked the first Remove button on the screen —
    /// 2026-08-25.</b> On a members list that begins with the signed-in administrator
    /// that button is <em>yours</em>, and removing yourself is refused before the panel
    /// opens, which is exactly what the first test in this file asserts. So four tests
    /// waited ten seconds for a panel that was correctly never going to open and
    /// reported it as <em>the removal panel never opened</em> — an accusation against
    /// the console, produced by clicking the one row it must refuse.
    /// </para>
    /// <para>
    /// <b>It passed where it was written because that members list did not begin with
    /// the operator.</b> Position is not a property of a member; who they are is. Found
    /// by the first CI run to reach this suite.
    /// </para>
    /// </remarks>
    private async Task<string> SomebodyElseAsync()
    {
        string me = await Browser.EvaluateAsync<string>("signedInAs") ?? string.Empty;

        Assert.False(me.Length == 0, "the page does not know who is signed in");

        string selector =
            $"#members tr button[data-member-remove]:not([data-member-remove='{me}'])";

        await WaitForAsync(
            $"document.querySelector(\"{selector}\")",
            "The Members screen offers a Remove button only for the account that is "
            + "signed in, and removing yourself is refused before the panel opens — so "
            + "this test needs a second member to exist.");

        return selector;
    }
}
