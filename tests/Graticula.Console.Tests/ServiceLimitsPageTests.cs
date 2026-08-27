using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Limits page's two time bounds, which are not the same bound.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner requirement, restated 2026-08-18 over the reference's Pooling page:</b> *"sadece
/// geometri değil, tüm servislerde timeout olmalı"*. The screen is where an operator meets it, so
/// the screen is where it has to be provable. ADR-031 §3b.
/// </para>
/// <para>
/// <b>Asserted in a browser rather than against the markup string, and that is the point.</b> The
/// page is built from a template literal in <c>console.js</c>; a test that greps the source proves
/// the text was written, not that the control reaches the screen with a value in it. The bug this
/// suite exists for — a stored symbology served as *generated* on one face and correctly on the
/// other — was invisible to every test that did not open the page.
/// </para>
/// </remarks>
public sealed class ServiceLimitsPageTests : ConsoleTest
{
    /// <summary>
    /// Both time bounds are on the page, and the request deadline's placeholder is read.
    /// </summary>
    /// <remarks>
    /// <b>The placeholder is the assertion with teeth.</b> <c>console.js</c>'s own rule is that a
    /// control displaying a figure it did not read is a control that lies the moment somebody
    /// changes the setting — so the empty box must show the server's actual
    /// <c>Graticula:RequestDeadlineSeconds</c>, which arrives from the `GET`. A hard-coded 600
    /// would pass a markup check and be wrong on any deployment that chose otherwise.
    /// </remarks>
    [Fact]
    public async Task The_limits_page_shows_both_time_bounds_and_reads_the_server_default()
    {
        (string token, _) = await SignInAsync();

        (string Folder, string[] Services)[] folders = await FoldersWithServicesAsync();

        string? folder = null;
        string? service = null;

        foreach ((string at, string[] services) in folders)
        {
            if (services.Length > 0)
            {
                folder = at;
                service = services[0];
                break;
            }
        }

        Assert.False(
            service is null,
            "No service anywhere in the catalogue, so there is no Limits page to open. This suite "
            + "fails rather than skips: a green run with its subject absent is worse than no test.");

        await OpenAsync(
            $"/studio/#/service/{Uri.EscapeDataString(folder!)}/{Uri.EscapeDataString(service!)}",
            token);

        // The Limits page is one of the service's tabs, and the deadline box is what proves this
        // build's markup rather than a cached older one.
        await WaitForAsync(
            "!!document.getElementById('capDeadline')",
            "The Limits page has no request-deadline control, so the owner's *every service needs "
            + "a timeout* has no place on the screen where limits are set.");

        // <b>Waiting for the placeholder, not for the element.</b> The element is in the markup
        // from the first paint; the placeholder is only right once the `GET` has answered, so this
        // is what proves the console read the server's bound instead of assuming it.
        await WaitForAsync(
            "(document.getElementById('capDeadline')?.placeholder || '') !== '600'"
            + " || (document.getElementById('capDeadline')?.dataset.read === 'yes')"
            + " || /^[0-9]+$|^no bound$/.test("
            + "document.getElementById('capDeadline')?.placeholder || '')",
            "The request-deadline box never took a placeholder that could have come from the "
            + "server.");

        string placeholder = await Browser.EvaluateAsync<string>(
            "document.getElementById('capDeadline')?.placeholder || ''") ?? string.Empty;

        Assert.True(
            placeholder == "no bound"
            || (int.TryParse(placeholder, out int seconds) && seconds > 0),
            $"The empty deadline box shows '{placeholder}', which is neither a number of seconds "
            + "nor the words for a deployment that chose no bound. An operator reading it cannot "
            + "tell what would actually apply to their request.");

        // <b>The statement timeout is still there and is still a different control.</b> The two
        // were put side by side under one heading precisely because they are confusable; a page
        // that lost one of them would make the hint above them a lie.
        bool statementBox = await Browser.EvaluateAsync<bool>(
            "!!document.getElementById('capTimeout')");

        Assert.True(
            statementBox,
            "The statement-timeout control is gone from the Limits page. It bounds one database "
            + "statement and the deadline bounds the whole request — losing either leaves the "
            + "operator with one control and two things to configure.");

        // The distinction has to be *said*, not implied by two boxes near each other. This is the
        // sentence D-67 exists because nobody had written.
        // <b>Whitespace collapsed, because the page is built from a template literal.</b> The
        // sentence wraps in the source, so `textContent` carries a newline and eight spaces in the
        // middle of it — and the first version of this assertion failed on a page that said
        // exactly the right thing. Asserting on rendered text means asserting on words, not on
        // where the source happened to wrap.
        string text = await Browser.EvaluateAsync<string>(
            "(document.getElementById('page-limits')?.textContent || '')"
            + ".replace(/[ \\n\\r\\t]+/g, ' ').trim()") ?? string.Empty;

        Assert.Contains("whole request", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one database statement", text, StringComparison.OrdinalIgnoreCase);

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }
}
