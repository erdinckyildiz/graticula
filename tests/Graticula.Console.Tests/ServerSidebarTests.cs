using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Server's sidebar is five rows, and the screens that left it are reached from the page they belong
/// to — ADR-091.
/// </summary>
/// <remarks>
/// <b>What could go wrong is silent, so each case is asserted by what is lit.</b> A subpage that routes
/// but lights no row reads as a page from nowhere; one that lights the wrong row sends the reader back
/// to the wrong place. Every subpage keeps its address, which is why the rest of the suite still opens
/// <c>/server/#/roles</c> and <c>/server/#/logs</c> directly.
/// </remarks>
public sealed class ServerSidebarTests : ConsoleTest
{
    private const string Lit =
        "[...document.querySelectorAll('#tabs a[aria-current]')].map(a => a.dataset.tab).join(',')";

    private const string Strip =
        "[...document.querySelectorAll('.view.on nav.subpages a')]"
        + ".map(a => (a.getAttribute('aria-current') ? '*' : '') + a.textContent.trim()).join(' ')";

    [Fact]
    public async Task The_sidebar_has_five_rows()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/services", token);

        await WaitForAsync(
            "document.querySelectorAll('#tabs a[data-tab]').length > 0",
            "Server drew no sidebar.");

        string rows = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a[data-tab]')].map(a => a.dataset.tab).join(',')")
            ?? string.Empty;

        Assert.Equal("services,sources,members,settings,operations", rows);

        NothingWentWrong(await PageErrorsAsync());
    }

    [Theory]
    [InlineData("settings", "settings", "*General Roles Sign-in Apps")]
    [InlineData("roles", "settings", "General *Roles Sign-in Apps")]
    [InlineData("signin", "settings", "General Roles *Sign-in Apps")]
    [InlineData("apps", "settings", "General Roles Sign-in *Apps")]
    [InlineData("operations", "operations", "*Status Logs")]
    [InlineData("logs", "operations", "Status *Logs")]
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

        await OpenAsync("/server/#/settings", token);

        await WaitForAsync(
            "document.querySelectorAll('.view.on nav.subpages a').length === 4",
            "Settings drew no strip of four pages.");

        await ClickAsync(".view.on nav.subpages a[href='#/roles']");

        await WaitForAsync(
            "document.getElementById('view-roles')?.classList.contains('on')",
            "Pressing Roles in the Settings strip did not open Roles.");

        Assert.Equal("settings", await Browser.EvaluateAsync<string>(Lit));
        Assert.Contains("*Roles", await Browser.EvaluateAsync<string>(Strip) ?? string.Empty, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }
}
