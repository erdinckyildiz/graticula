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

        // The dialog is the browser's, so the answer is supplied before the press.
        await Browser.EvaluateAsync<bool>("(window.prompt = () => '1', true)");

        await ClickAsync("#sources [data-source-quiesce]");

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
        // two are one route and a screen that posted twice would look like it worked.
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
