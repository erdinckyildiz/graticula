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

        // <b>Organizations is the system administrator's, ADR-204 §5d</b> — a fourth tab for them and for nobody else, so
        // which three or four is asked of the server rather than assumed of the fixture.
        bool system = await Browser.EvaluateAsync<bool>("!!whoNow?.systemAdministrator");

        Assert.Equal(system ? "services,sources,organizations,logs" : "services,sources,logs", rows);

        string labels = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a[data-tab]')].map(a => a.textContent.trim()).join(',')")
            ?? string.Empty;

        Assert.Equal(system ? "Services,Site,Organizations,Logs" : "Services,Site,Logs", labels);

        NothingWentWrong(await PageErrorsAsync());
    }

    [Theory]
    [InlineData("sources", "sources", "*Data sources Status General")]
    [InlineData("operations", "sources", "Data sources *Status General")]
    [InlineData("settings", "sources", "Data sources Status *General")]
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

    [Fact]
    public async Task The_strip_moves_between_siblings()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/sources", token);

        await WaitForAsync(
            "document.querySelectorAll('.view.on nav.subpages a').length === 3",
            "Site drew no list of three pages.");

        await ClickAsync(".view.on nav.subpages a[href$='/operations']");

        await WaitForAsync(
            "document.getElementById('view-operations')?.classList.contains('on')",
            "Pressing Status in Site's list did not open Status.");

        Assert.Equal("sources", await Browser.EvaluateAsync<string>(Lit));
        Assert.Contains("*Status", await Browser.EvaluateAsync<string>(Strip) ?? string.Empty, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }
}
