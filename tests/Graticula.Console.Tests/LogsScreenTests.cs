using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Logs screen, opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written after a browser review found two defects that every API test passed
/// through.</b>
/// [ADR-045](../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md)
/// condition 5 asks the screen to answer a real question end to end, and the conformance
/// suite asserted the API half comprehensively while the screen itself was inert in two
/// ways:
/// </para>
/// <para>
/// <b>The per-source filter did nothing, on all three sources.</b> `drawLogControls` rebuilt
/// the control's markup on every read, before the query was composed, so the value the reader
/// had just chosen went into an element that no longer existed and the query read a fresh
/// empty one. Every request the API received was correct; the screen simply never asked for
/// what had been typed.
/// </para>
/// <para>
/// <b>And switching source was one click behind.</b> A debounced keystroke starts a read;
/// clicking a source tab starts another; the first resolved second and painted the previous
/// source's rows under the newly highlighted tab. Two correct requests, one wrong screen.
/// </para>
/// <para>
/// <b>Both are the same class of bug and neither is visible from outside the browser</b>,
/// which is what this file is for.
/// </para>
/// </remarks>
public sealed class LogsScreenTests : ConsoleTest
{
    [Fact]
    public async Task The_logs_screen_opens_from_a_bare_address_and_draws_its_controls()
    {
        // <b>Opened directly, not clicked into from another screen.</b> This console has
        // shipped a control that existed in the DOM and was invisible three times, and on
        // 2026-08-22 a screen broke because it read state a previous page had left behind. A
        // bookmark, a shared link and a reload all arrive this way.
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/logs", token);

        await WaitForAsync(
            "document.querySelectorAll('#logSources button[data-log-source]').length === 3",
            "The Logs screen drew no source selector, so there is no way to reach two of the "
            + "three logs.");

        // <b>Visible, not merely present.</b> `offsetParent` and a box, because the DOM is
        // not the screen.
        bool visible = await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const ids = ['logSources', 'logText', 'logWho', 'logSince', 'logRows'];
              return ids.every(id => {
                const e = document.getElementById(id);
                if (!e || e.offsetParent === null) return false;
                const box = e.getBoundingClientRect();
                return box.width > 0 && box.height > 0;
              });
            })()
            """);

        Assert.True(
            visible,
            "Something on the Logs screen is in the markup and not on the screen. A control "
            + "that exists and cannot be seen is a control that does nothing, and this "
            + "console has shipped that three times.");
    }

    [Fact]
    public async Task The_action_filter_actually_filters()
    {
        // <b>The defect this test exists for.</b> The select was rebuilt between being set
        // and being read, so all three per-source filters were inert — verified by a review
        // that clicked them, and passed by every test that only called the API.
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/logs", token);

        await WaitForAsync(
            "document.querySelectorAll('#logRows tr.logrow').length > 0",
            "The audit trail drew no rows, so there is nothing to filter.");

        // Pick an action the log actually holds, from the control itself — a hard-coded one
        // would make this test a statement about the development data.
        string action = await Browser.EvaluateAsync<string>(
            """
            (() => {
              const select = document.getElementById('logOwnValue');
              if (!select || !select.options) return '';
              for (const option of select.options) {
                if (option.value) return option.value;
              }
              return '';
            })()
            """) ?? string.Empty;

        Assert.False(
            action.Length == 0,
            "The action filter offers no action, though the audit trail holds thousands of "
            + "rows across dozens of them.");

        // Choose it the way a reader does: set the value and let the change event run.
        // <b>A placeholder and a replace, not an interpolated raw string.</b> The script is
        // full of braces and every one of them would need doubling; getting that wrong is a
        // compile error at best and a silently different script at worst.
        await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const select = document.getElementById('logOwnValue');
              select.value = __ACTION__;
              select.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """.Replace("__ACTION__", Quote(action), StringComparison.Ordinal));

        // <b>Every row is the chosen action.</b> A screen that ignored the filter would still
        // have rows — that is why the assertion is on their content and not on their count.
        await WaitForAsync(
            """
            (() => {
              const rows = [...document.querySelectorAll('#logRows tr.logrow')];
              if (rows.length === 0) return false;
              const want = __ACTION__;
              return rows.every(r => (r.cells[1]?.textContent || '').includes(want));
            })()
            """.Replace("__ACTION__", Quote(action), StringComparison.Ordinal),
            $"The screen still shows rows other than '{action}' after that action was chosen, "
            + "so the per-source filter is not reaching the query.");
    }

    [Fact]
    public async Task Switching_source_shows_that_source_and_not_the_previous_one()
    {
        // <b>The race, asserted through its symptom.</b> Typing starts a debounced read;
        // clicking a source starts another; the older one resolving second painted the
        // previous log under the new tab. The test types first on purpose.
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/logs", token);

        await WaitForAsync(
            "document.querySelectorAll('#logRows tr.logrow').length > 0",
            "The audit trail drew no rows to switch away from.");

        // Type into the shared filter, then switch source immediately — the sequence that
        // put two reads in flight at once.
        await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const box = document.getElementById('logText');
              box.value = 'service';
              box.dispatchEvent(new Event('input', { bubbles: true }));
              document.querySelector('#logSources button[data-log-source="requests"]').click();
              return true;
            })()
            """);

        // <b>The tab and the table have to agree.</b> The highlighted source said Requests
        // while the rows were still audit actions, which is the one state this must not reach.
        await WaitForAsync(
            """
            (() => {
              const chosen = document.querySelector('#logSources button[aria-selected="true"]');
              if (!chosen || chosen.dataset.logSource !== 'requests') return false;
              const rows = [...document.querySelectorAll('#logRows tr.logrow')];
              if (rows.length === 0) return true;
              // A request's *what* is a method and a path; an audit action is a dotted name.
              return rows.every(r => /^(GET|POST|PUT|DELETE|HEAD|PATCH) /
                .test((r.cells[1]?.textContent || '').trim()));
            })()
            """,
            "After switching to Requests the table still holds rows that are not requests, so "
            + "an older read painted over the newer one.");
    }

    [Fact]
    public async Task The_dropped_notice_is_shown_on_the_log_it_describes()
    {
        // <b>It was on all three tabs and it describes one.</b> Only the request log drops:
        // the audit trail fails the request instead, and studio events are written straight
        // through. Telling a reader of the audit trail that nothing had been dropped invited
        // them to wonder what could be.
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/logs", token);

        await WaitForAsync(
            "document.querySelectorAll('#logSources button[data-log-source]').length === 3",
            "The Logs screen drew no source selector.");

        await WaitForAsync(
            "document.getElementById('logWriter')?.hidden === true",
            "The dropped-entries notice is showing on the audit trail, which cannot drop "
            + "anything.");

        await Browser.EvaluateAsync<bool>(
            "document.querySelector('#logSources button[data-log-source=\"requests\"]').click()"
            + " || true");

        await WaitForAsync(
            "document.getElementById('logWriter')?.hidden === false",
            "The dropped-entries notice is hidden on the request log, which is the one log "
            + "that drops. ADR-045 condition 6 asks for the loss to be visible.");

        string said = await Browser.EvaluateAsync<string>(
            "document.getElementById('logWriter')?.textContent || ''") ?? string.Empty;

        Assert.True(
            said.Contains("dropped", StringComparison.OrdinalIgnoreCase),
            $"The notice reads '{said}', which does not tell an operator whether anything was "
            + "lost.");
    }

    [Fact]
    public async Task A_row_opens_its_detail_by_keyboard_as_well_as_by_click()
    {
        // <b>The detail row is the only place a request's duration, query and face are
        // shown</b>, and it was unreachable without a mouse: the row had no `tabindex` and
        // no role, so a keyboard could not focus it and Enter did nothing.
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/logs", token);

        await WaitForAsync(
            "document.querySelectorAll('#logRows tr.logrow').length > 0",
            "No rows, so no detail to open.");

        bool focusable = await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const row = document.querySelector('#logRows tr.logrow');
              return row.getAttribute('tabindex') === '0' && row.getAttribute('role') === 'button';
            })()
            """);

        Assert.True(
            focusable,
            "A log row cannot be focused by a keyboard, so its detail cannot be opened "
            + "without a mouse.");

        // Focus it and press Enter, the way a keyboard reader would.
        await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const row = document.querySelector('#logRows tr.logrow');
              row.focus();
              row.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
              return true;
            })()
            """);

        await WaitForAsync(
            """
            (() => {
              const row = document.querySelector('#logRows tr.logrow');
              const detail = row.nextElementSibling;
              return detail && !detail.hidden && row.getAttribute('aria-expanded') === 'true';
            })()
            """,
            "Enter on a focused log row did not reveal its detail, or did not say that it "
            + "had.");
    }

    [Fact]
    public async Task The_studio_log_says_that_empty_is_the_good_outcome()
    {
        // <b>A near-empty log is the hardest case on this screen and the most likely.</b> A
        // server whose viewer has reported nothing is a server whose viewer is working;
        // without a sentence saying so, an empty table reads as a broken feature.
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/logs", token);

        await WaitForAsync(
            "document.querySelectorAll('#logSources button[data-log-source]').length === 3",
            "The Logs screen drew no source selector.");

        await Browser.EvaluateAsync<bool>(
            "document.querySelector('#logSources button[data-log-source=\"studio\"]').click()"
            + " || true");

        // Either it has rows, or it says why having none is fine. Both are correct answers;
        // a blank table is not.
        await WaitForAsync(
            """
            (() => {
              const body = document.getElementById('logRows');
              if (!body) return false;
              if (body.querySelectorAll('tr.logrow').length > 0) return true;
              const text = body.textContent || '';
              return text.includes('the good outcome');
            })()
            """,
            "The studio log is empty and says nothing about it, so a working viewer looks "
            + "like a broken screen.");
    }

    /// <summary>A string as a JavaScript literal.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Its quoted form.</returns>
    /// <remarks>
    /// <b>Through the JSON serialiser rather than by wrapping it in quotes.</b> An action
    /// name comes from the server and nothing stops one holding a quote or a backslash; a
    /// hand-rolled wrap would turn that into a script that does something else.
    /// </remarks>
    private static string Quote(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
