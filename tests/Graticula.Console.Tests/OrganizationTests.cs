using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Studio's Organization — ADR-189: Members and Settings across the top, and Settings a column of short pages down
/// its left, as Portal's Organization is. Members, roles, sign-in, apps and the map settings came from Server, and
/// their old addresses land here.
/// </summary>
public sealed class OrganizationTests : ConsoleTest
{
    private const string Lit =
        "[...document.querySelectorAll('#tabs a[aria-current]')].map(a => a.dataset.tab).join(',')";

    private const string Top =
        "[...document.querySelectorAll('.view.on > nav.orgstrip a')]"
        + ".map(a => (a.getAttribute('aria-current') ? '*' : '') + a.textContent.trim()).join(' ')";

    private const string Side =
        "[...document.querySelectorAll('.view.on > nav.subpages a')]"
        + ".map(a => (a.getAttribute('aria-current') ? '*' : '') + a.textContent.trim()).join('|')";

    [Fact]
    public async Task Studios_bar_is_content_map_groups_and_organization()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync("document.querySelectorAll('#tabs a').length > 0", "Studio drew no section bar.");

        Assert.Equal("Content,Map,Groups,Organization", await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a')].map(a => a.textContent.trim()).join(',')"));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Theory]
    [InlineData("members", "view-members", "*Members Settings", "")]
    [InlineData("orgsettings", "view-orgsettings", "Members *Settings", "*Map|Member roles|Security|Utility services")]
    [InlineData("roles", "view-roles", "Members *Settings", "Map|*Member roles|Security|Utility services")]
    [InlineData("signin", "view-signin", "Members *Settings", "Map|Member roles|*Security|Utility services")]
    [InlineData("apps", "view-signin", "Members *Settings", "Map|Member roles|*Security|Utility services")]
    [InlineData("utilities", "view-utilities", "Members *Settings", "Map|Member roles|Security|*Utility services")]
    public async Task Each_page_marks_its_place_in_the_strip_and_the_column(string screen, string view, string top, string side)
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/{screen}", token);

        await WaitForAsync(
            $"document.getElementById('{view}')?.classList.contains('on')",
            $"#/{screen} did not open {view}.");

        Assert.Equal("members", await Browser.EvaluateAsync<string>(Lit));
        Assert.Equal(top, await Browser.EvaluateAsync<string>(Top));
        Assert.Equal(side, await Browser.EvaluateAsync<string>(Side));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_servers_old_address_for_a_moved_page_opens_it_in_studio()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/roles", token);

        await WaitForAsync(
            "location.pathname.startsWith('/studio') && document.getElementById('view-roles')?.classList.contains('on')",
            "/server/#/roles did not land on Studio's Member roles.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task The_column_moves_between_the_settings_pages()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/orgsettings", token);

        await WaitForAsync(
            "document.querySelectorAll('.view.on > nav.subpages a').length === 4",
            "Settings drew no column of four pages.");

        await ClickAsync(".view.on > nav.subpages a[href='#/utilities']");

        await WaitForAsync(
            "document.getElementById('view-utilities')?.classList.contains('on')",
            "Pressing Utility services did not open it.");

        Assert.Contains("*Utility services", await Browser.EvaluateAsync<string>(Side) ?? string.Empty, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }
}
