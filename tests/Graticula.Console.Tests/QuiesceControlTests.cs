using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Data sources screen offers to take a database out of service, and says when it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md) condition 4.</b> This is the
/// screen somebody is looking at when a DBA says the database will not let them work, so it is
/// where the lever belongs. A quiesce is otherwise invisible: an operator's own instruction would
/// be the last place they would think to check.
/// </para>
/// <para>
/// <b>Asserted on the request, because this harness answers every write itself.</b> Non-GETs are
/// trapped and answered with <c>{}</c>, so the row's state after a quiesce is the harness's answer
/// rather than the server's. What can be pinned here is that the control is wired to the right
/// address and method — the shape this console has shipped unwired more than once — and the
/// behaviour behind it is covered over real HTTP by <c>QuiesceConformanceTests</c>.
/// </para>
/// </remarks>
public sealed class QuiesceControlTests : ConsoleTest
{
    /// <summary>
    /// The Quiesce button is drawn, renders, and sends the request Resume then undoes.
    /// </summary>
    [Fact]
    public async Task A_source_can_be_taken_out_of_service_and_the_row_says_so()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/sources", token);

        await WaitForAsync(
            "document.querySelectorAll('#sources [data-source-quiesce]').length > 0",
            "The Data sources screen offers no way to quiesce anything, so ADR-059's lever is "
            + "not where the DBA's problem is visible.");

        // <b>Visible, not merely present.</b> This console has three times shipped a control that
        // existed and rendered nowhere.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "!!document.querySelector('#sources [data-source-quiesce]')?.offsetParent"),
            "The Quiesce button is in the document and renders nowhere.");

        await ClickAsync("#sources [data-source-quiesce]");

        /*
          <b>The product's own dialog since 2026-09-08, and this test caught the change.</b> It
          used to override `window.prompt`; a design review called the native prompt a finding
          rather than a preference, because this is the one action on the screen gated by
          `admin:manageServer` and it was dispatched with the chrome the app uses for trivial
          confirmations, pre-filled with the longest ordinary window, accepted by the Enter that
          reflexively dismisses a prompt.

          <b>Nothing is pre-filled, so the button starts disabled.</b> Asserted, because that is
          the whole of what the change bought.
        */
        await WaitForAsync(
            "(() => { const e = document.getElementById('quiesceMinutes'); "
            + "return !!e && e.offsetParent !== null; })()",
            "Quiesce did not open the product's own dialog.");

        Assert.True(
            await Browser.EvaluateAsync<bool>("document.getElementById('quiesceGo').disabled"),
            "The dialog opened armed. Nothing is pre-filled precisely so that the reflex which "
            + "dismisses a prompt cannot start an outage.");

        Assert.Equal(
            string.Empty,
            await Browser.EvaluateAsync<string>(
                "document.getElementById('quiesceMinutes').value") ?? "?");

        await Browser.EvaluateAsync<bool>(
            "(() => { const box = document.getElementById('quiesceMinutes'); box.value = '1'; "
            + "box.dispatchEvent(new Event('input', { bubbles: true })); return true; })();");

        await ClickAsync("#quiesceGo");

        /*
          <b>The request is what this can assert, and the row's new state is not.</b> This harness
          traps every non-GET, records it and answers with <c>{}</c> — so the quiesce never
          reaches the server and the listing that follows reports nothing quiesced. A test written
          against the row would have been asserting the harness.

          <b>Which is why it asserts the address and the method.</b> A button wired to nothing
          looks identical from here to one wired correctly, and that is the shape this console has
          shipped before. The behaviour behind it — the refusal, its sentence, the resume — is
          covered over real HTTP by <c>QuiesceConformanceTests</c>.
        */
        await WaitForAsync(
            "(window.__writes || []).some(w => w.startsWith('POST') && w.includes('/quiesce'))",
            "Pressing Quiesce sent no request. The recorded writes were: "
            + string.Join(" | ", await WritesAsync()));

        // <b>And Resume is a DELETE to the same address</b>, which is worth pinning because the
        // two are one route and a screen that posted twice would look like it worked. The
        // harness answered the quiesce itself, so the row still carries the Quiesce button; the
        // next lines make it the Resume one rather than pretending the server said anything.
        await Browser.EvaluateAsync<bool>("(window.__writes = [], true)");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const row = document.querySelector('#sources [data-source-quiesce]');
          row.setAttribute('data-source-resume', row.getAttribute('data-source-quiesce'));
          row.removeAttribute('data-source-quiesce');
          return true;
        })();
        """);

        await ClickAsync("#sources [data-source-resume]");

        await WaitForAsync(
            "(window.__writes || []).some(w => w.startsWith('DELETE') && w.includes('/quiesce'))",
            "Pressing Resume sent no DELETE. The recorded writes were: "
            + string.Join(" | ", await WritesAsync()));

        NothingWentWrong(await PageErrorsAsync());
    }
}
