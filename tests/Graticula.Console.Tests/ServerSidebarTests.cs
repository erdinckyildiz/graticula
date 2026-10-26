using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Server's sections are three tabs in the header — Services, Site, Logs — and every other Server screen is reached
/// from the list on its tab's pages: ADR-187, amended by ADR-189, which took Security to Studio's Organization. The
/// class keeps its name because ADR-091 cites it.
/// </summary>
/// <remarks>
/// <b>What could go wrong is silent, so each case is asserted by what is lit.</b> A subpage that routes
/// but lights no row reads as a page from nowhere; one that lights the wrong row sends the reader back
/// to the wrong place. Every subpage keeps its address.
/// </remarks>
public sealed class ServerSidebarTests : ConsoleTest
{
    private const string Lit =
        "[...document.querySelectorAll('#tabs a[aria-current]')].map(a => a.dataset.tab).join(',')";

    private const string Strip =
        "[...document.querySelectorAll('.view.on nav.subpages a')]"
        + ".map(a => (a.getAttribute('aria-current') ? '*' : '') + a.textContent.trim()).join(' ')";

    [Fact]
    public async Task The_top_bar_has_server_managers_tabs_without_security()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/services", token);

        await WaitForAsync(
            "document.querySelectorAll('#tabs a[data-tab]').length > 0",
            "Server drew no section bar.");

        string rows = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a[data-tab]')].map(a => a.dataset.tab).join(',')")
            ?? string.Empty;

        // <b>The same three for every reader since ADR-221</b>: Organizations went to System with Status and General,
        // and Site is named for the one page it had left.
        Assert.Equal("services,sources,logs", rows);

        string labels = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a[data-tab]')].map(a => a.textContent.trim()).join(',')")
            ?? string.Empty;

        Assert.Equal("Services,Data sources,Logs", labels);

        NothingWentWrong(await PageErrorsAsync());
    }

    [Theory]
    // Data sources draws no strip since ADR-221: Status and General, its siblings, are System's.
    [InlineData("sources", "sources", "")]
    [InlineData("logs", "logs", "")]
    public async Task A_subpage_lights_its_row_and_marks_its_strip(string screen, string row, string strip)
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/server/#/{screen}", token);

        await WaitForAsync(
            $"document.getElementById('view-{screen}')?.classList.contains('on')",
            $"#/{screen} did not open its own page.");

        Assert.Equal(row, await Browser.EvaluateAsync<string>(Lit));
        Assert.Equal(strip, await Browser.EvaluateAsync<string>(Strip));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task Publish_is_under_services_and_draws_no_strip()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/publish", token);

        await WaitForAsync(
            "document.getElementById('view-publish')?.classList.contains('on')",
            "#/publish did not open the Publish page.");

        Assert.Equal("services", await Browser.EvaluateAsync<string>(Lit));
        Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>(Strip));

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>System's bar moves between its screens, each lighting its own tab — ADR-221.</summary>
    [Fact]
    public async Task Systems_bar_moves_between_its_screens()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/system/operations", token);

        await WaitForAsync(
            "document.getElementById('view-operations')?.classList.contains('on')",
            "System did not open Status.");
        Assert.Equal("operations", await Browser.EvaluateAsync<string>(Lit));

        await ClickAsync("#tabs a[data-tab=settings]");

        await WaitForAsync(
            "document.getElementById('view-settings')?.classList.contains('on') && location.pathname === '/system/settings'",
            "Pressing General in System's bar did not open General.");

        Assert.Equal("settings", await Browser.EvaluateAsync<string>(Lit));
        Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>(Strip));

        NothingWentWrong(await PageErrorsAsync());
    }
}
