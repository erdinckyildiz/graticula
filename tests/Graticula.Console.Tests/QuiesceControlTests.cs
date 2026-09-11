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
    /// The dialog names what actually stops answering, including the other source on the same
    /// database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A design review's first finding, 2026-09-09, and it was wrong at the one moment a
    /// reader can still decline.</b> A quiesce is per database ([ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md)
    /// §5d), so a second source registered against the same one goes out with it. The dialog on
    /// <c>datastore</c> said <b>8 layers</b> while nine were about to stop, and never named
    /// <c>probe</c> — while the sentence beside it, about another <i>worker</i>, reads as
    /// reassurance that a sibling source is unaffected.
    /// </para>
    /// <para>
    /// <b>The fixture is the shape this needs</b>: two registered sources against one
    /// PostgreSQL, which is `GRATICULA_TEST_PG` and the datastore being the same database. If a
    /// deployment ever has no such pair this test says so rather than passing vacuously.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_dialog_names_the_other_source_on_the_same_database()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/sources", token);

        await WaitForAsync(
            "document.querySelectorAll('#sources [data-source-quiesce]').length > 0",
            "The Data sources screen offers no way to quiesce anything.");

        string shared = await Browser.EvaluateAsync<string>("""
        (() => {
          const one = [...document.querySelectorAll('#sources [data-source-quiesce]')]
            .find(b => (b.dataset.sourceShares || '').length > 0);

          return one ? one.dataset.sourceName : '';
        })()
        """) ?? string.Empty;

        Assert.False(
            string.IsNullOrEmpty(shared),
            "No listed data source shares a database with another, so this test has nothing to "
            + "check. The fixture needs two sources registered against one PostgreSQL — which is "
            + "the ordinary shape when somebody registers the datastore's own database a second "
            + "time, and the shape ADR-059 §5d is about.");

        await ClickAsync($"#sources [data-source-quiesce][data-source-name='{shared}']");

        await WaitForAsync(
            "(() => { const e = document.getElementById('quiesceMinutes'); "
            + "return !!e && e.offsetParent !== null; })()",
            "Quiesce did not open the product's own dialog.");

        string said = await Browser.EvaluateAsync<string>(
            "document.getElementById('quiesceWhat').textContent.replace(/\\s+/g, ' ').trim()")
            ?? string.Empty;

        string sibling = await Browser.EvaluateAsync<string>($$"""
        document.querySelector("#sources [data-source-name='{{shared}}'][data-source-quiesce]")
          .dataset.sourceShares.split(',')[0]
        """) ?? string.Empty;

        Assert.True(
            said.Contains(sibling, StringComparison.Ordinal),
            $"The dialog for '{shared}' never names '{sibling}', which goes out of service with "
            + $"it. What it says is: {said}");

        // <b>And the number counts them in.</b> Naming the sibling and still quoting this
        // source's own layer count would be half the repair, and the half a reader weighs.
        int mine = await Browser.EvaluateAsync<int>($$"""
        Number(document.querySelector(
          "#sources [data-source-name='{{shared}}'][data-source-quiesce]").dataset.sourceLayers)
        """);

        int all = await Browser.EvaluateAsync<int>($$"""
        Number(document.querySelector(
          "#sources [data-source-name='{{shared}}'][data-source-quiesce]")
          .dataset.sourceSharedLayers)
        """);

        Assert.True(
            all > mine,
            $"'{shared}' shares a database with '{sibling}' and the layers that stop answering "
            + $"({all}) are no more than its own ({mine}). Either the sibling has no layers — in "
            + "which case this fixture cannot show the defect — or the count is not counting.");

        Assert.True(
            said.Contains($"{all} layers", StringComparison.Ordinal),
            $"The dialog quotes a layer count other than {all}, which is what actually stops "
            + $"answering. What it says is: {said}");
    }

    /// <summary>
    /// The minutes box refuses a fraction, and says why it is refusing.
    /// </summary>
    /// <remarks>
    /// <b>Two findings from the same review.</b> <c>2.5</c> passed the range check and enabled
    /// Go — the range was tested and the <c>step</c> was not — and the request then asked for 150
    /// seconds, which is not a thing anybody typed. And a reader who typed <c>0</c> got a button
    /// that silently refused to enable: no border, no <c>aria-invalid</c>, and nothing in the
    /// dialog's own live region, which was wired only for a failed request.
    /// </remarks>
    [Theory]
    [InlineData("0", "Between 1 and 60 minutes.")]
    [InlineData("61", "Between 1 and 60 minutes.")]
    [InlineData("2.5", "Whole minutes only.")]
    public async Task A_window_the_dialog_will_not_accept_says_why(string typed, string expected)
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/sources", token);

        await WaitForAsync(
            "document.querySelectorAll('#sources [data-source-quiesce]').length > 0",
            "The Data sources screen offers no way to quiesce anything.");

        await ClickAsync("#sources [data-source-quiesce]");

        await WaitForAsync(
            "(() => { const e = document.getElementById('quiesceMinutes'); "
            + "return !!e && e.offsetParent !== null; })()",
            "Quiesce did not open the product's own dialog.");

        await Browser.EvaluateAsync<bool>($$"""
        (() => { const box = document.getElementById('quiesceMinutes'); box.value = '{{typed}}';
          box.dispatchEvent(new Event('input', { bubbles: true })); return true; })();
        """);

        Assert.True(
            await Browser.EvaluateAsync<bool>("document.getElementById('quiesceGo').disabled"),
            $"'{typed}' minutes enabled Go. The dialog's own `step=\"1\"` and range say it is not "
            + "a window this server accepts, and sending it asks for a number nobody typed.");

        Assert.Equal(
            expected,
            (await Browser.EvaluateAsync<string>(
                "document.getElementById('quiesceSays').textContent") ?? "").Trim());

        Assert.Equal(
            "true",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('quiesceMinutes').getAttribute('aria-invalid')"));
    }

    /// <summary>
    /// An empty reason box sends no reason, rather than the grey one it was showing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The review found this in the payload rather than by reading the screen</b>, which is
    /// the only place it was visible: <c>input.value</c> was genuinely empty and the request
    /// carried <c>"why":"a schema change"</c> — the placeholder — so the row, the toast and the
    /// audit all recorded a reason nobody gave. It is the same failure the *minutes* field had
    /// already been fixed for, in the field beside it.
    /// </para>
    /// <para>
    /// <b><c>fetch</c> is wrapped here rather than read out of <c>window.__writes</c></b>, and
    /// the harness says why it does not record JSON bodies: <i>a JSON body is the caller's own
    /// string and reading it back would be asserting against the test's own construction.</i>
    /// That is right where a test builds the body and wrong here, where the product builds it out
    /// of two form fields — which is the whole of what this asserts.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_empty_reason_is_sent_as_no_reason()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/sources", token);

        await WaitForAsync(
            "document.querySelectorAll('#sources [data-source-quiesce]').length > 0",
            "The Data sources screen offers no way to quiesce anything.");

        await ClickAsync("#sources [data-source-quiesce]");

        await WaitForAsync(
            "(() => { const e = document.getElementById('quiesceMinutes'); "
            + "return !!e && e.offsetParent !== null; })()",
            "Quiesce did not open the product's own dialog.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          window.__bodies = [];
          const was = window.fetch;

          window.fetch = async (input, init) => {
            if (init && typeof init.body === 'string') {
              window.__bodies.push(init.body);
            }

            return was(input, init);
          };

          const box = document.getElementById('quiesceMinutes');
          box.value = '1';
          box.dispatchEvent(new Event('input', { bubbles: true }));
          return true;
        })();
        """);

        Assert.Equal(
            string.Empty,
            (await Browser.EvaluateAsync<string>(
                "document.getElementById('quiesceWhy').value") ?? "?").Trim());

        // <b>And it is showing a placeholder, which is the thing that used to be sent.</b>
        Assert.False(
            string.IsNullOrEmpty(await Browser.EvaluateAsync<string>(
                "document.getElementById('quiesceWhy').placeholder")),
            "The reason box shows no placeholder, so this test is guarding against a defect that "
            + "can no longer happen the way it did — read it again before deleting it.");

        await ClickAsync("#quiesceGo");

        await WaitForAsync(
            "(window.__bodies || []).length > 0",
            "Pressing Go sent no request body at all.");

        string body = (await Browser.EvaluateAsync<string>("window.__bodies[0]")) ?? "";

        Assert.DoesNotContain("schema change", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"why\":null", body.Replace(" ", ""), StringComparison.Ordinal);
    }

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

        // <b>Found, rewritten and pressed in one expression — and the first version stopped one
        // step short.</b> Pressing Quiesce makes the pane re-read its listing, so the row this needs
        // is replaced somewhere between the press and here. The first repair rewrote the control
        // atomically and then clicked it in a *second* call, which reopened the same gap one step
        // later: on `3fcb840` the rewrite landed on the old row, the re-read (11 ms, 431 bytes —
        // D-259's diagnostic line, which is how this was read rather than guessed) drew a fresh
        // row with a Quiesce button, and the click waited ten seconds for a Resume control that
        // had been thrown away. The console's handler reads the pressed element's own `data-*` at
        // the moment of the click, so pressing it inside the same expression works whether or not
        // the row is about to be replaced. D-259.
        await WaitForAsync(
            """
            (() => {
              const control =
                document.querySelector('#sources [data-source-resume]')
                || document.querySelector('#sources [data-source-quiesce]');
              if (!control) return false;
              if (control.hasAttribute('data-source-quiesce')) {
                control.setAttribute(
                  'data-source-resume', control.getAttribute('data-source-quiesce'));
                control.removeAttribute('data-source-quiesce');
              }
              control.click();
              return true;
            })()
            """,
            "No row on the Data sources screen offered a control to rewrite, so the pane never "
            + "settled after the quiesce was sent.");

        await WaitForAsync(
            "(window.__writes || []).some(w => w.startsWith('DELETE') && w.includes('/quiesce'))",
            "Pressing Resume sent no DELETE. The recorded writes were: "
            + string.Join(" | ", await WritesAsync()));

        NothingWentWrong(await PageErrorsAsync());
    }
}
