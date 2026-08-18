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

        await ClickAsync("#members tr button[data-member-remove]");

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

        await ClickAsync("#members tr button[data-member-remove]");

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
}
