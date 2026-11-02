using System;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Owner decisions of 2026-11-02 (ADR-255 section 4): the graticule mark is the product's one mark — the favicon, the bar,
/// the directory and the sign-in page — a server that is not healthy is a dot on the account's avatar, named in its label,
/// and a Data sources row keeps Test connection and folds the rest behind its menu.
/// </summary>
public sealed class GraticuleMarkAndAccountDotTests : ConsoleTest
{
    private static readonly string[] Favicons = ["/favicon.svg", "/favicon.ico"];

    private const string Unhealthy = """
        (async () => {
          const origin = api;
          api = async (path, options) => String(path).startsWith('/admin/health') ? { status: 'degraded' } : origin(path, options);
          try { await refreshHealth(); } finally { api = origin; }
          return document.body.dataset.health;
        })()
        """;

    private const string DotDrawn = """
        (() => {
          const e = document.querySelector('#userMenu .initial');
          const after = getComputedStyle(e, '::after');
          return after.content !== 'none' && after.content !== 'normal' && parseFloat(after.width) >= 8;
        })()
        """;

    /// <summary>The favicon is the mark: an ellipse and the diagonal, and none of the old squares.</summary>
    [Fact]
    public async Task The_favicon_is_the_graticule_mark_and_not_nine_squares()
    {
        foreach (string path in Favicons)
        {
            using HttpResponseMessage response = await Http.GetAsync(Root + path);
            string svg = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("<ellipse", svg, StringComparison.Ordinal);
            Assert.Contains("M3.4 20.6 28.6 11.4", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<rect", svg, StringComparison.Ordinal);
        }
    }

    /// <summary>The bar's logo draws the same mark.</summary>
    [Fact]
    public async Task The_bars_logo_is_the_graticule_mark()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/content", token);
        await WaitForAsync("!!document.querySelector('#brand .mark svg')", "The bar drew no logo.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => { const m = document.querySelector('#brand .mark svg'); "
                + "return !!m.querySelector('ellipse') && !!m.querySelector('path[d^=\"M3.4 20.6\"]') && !m.querySelector('rect') "
                + "&& m.getClientRects().length > 0; })()"),
            "The bar's logo is not the graticule mark.");
    }

    /// <summary>Unhealthy: a dot on the account and its name says so; healthy: neither.</summary>
    [Fact]
    public async Task An_unhealthy_server_is_a_dot_on_the_avatar_and_a_word_in_its_name()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/services", token);
        await WaitForAsync("document.body.dataset.health === 'ok'", "The health was never read as healthy.");

        Assert.False(await Browser.EvaluateAsync<bool>(DotDrawn), "A healthy server has a dot on the avatar.");
        Assert.DoesNotContain("server health", await Browser.EvaluateAsync<string>("document.querySelector('#userMenu summary').getAttribute('aria-label')") ?? string.Empty,
            StringComparison.Ordinal);

        Assert.Equal("unusable", await Browser.EvaluateAsync<string>(Unhealthy));

        Assert.True(await Browser.EvaluateAsync<bool>(DotDrawn), "An unhealthy server drew no dot on the avatar.");
        string name = await Browser.EvaluateAsync<string>("document.querySelector('#userMenu summary').getAttribute('aria-label')") ?? string.Empty;
        Assert.Contains("server health", name, StringComparison.Ordinal);
        Assert.Contains("Store unreachable", name, StringComparison.Ordinal);
    }

    /// <summary>On a phone the dot is on the bar's avatar, and with the two-step notice up both dots are drawn apart.</summary>
    [Fact]
    public async Task On_a_phone_the_dot_is_on_the_bars_avatar_and_named_in_its_label()
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/services", token);
        await WaitForAsync("document.body.dataset.health === 'ok'", "The health was never read as healthy.");

        const string Before = "(() => getComputedStyle(document.getElementById('barAvatar'), '::before').content)()";
        Assert.Matches("^(none|normal)$", await Browser.EvaluateAsync<string>(Before) ?? string.Empty);

        await Browser.EvaluateAsync<string>(Unhealthy);

        Assert.Equal("\"\"", await Browser.EvaluateAsync<string>(Before));
        Assert.Contains("server health", await Browser.EvaluateAsync<string>("document.getElementById('barAvatar').getAttribute('aria-label')") ?? string.Empty,
            StringComparison.Ordinal);

        // With the two-step notice also up, the health dot keeps the top right and the notice's dot drops to the bottom right.
        await Browser.EvaluateAsync<bool>("(document.getElementById('factorNotice').hidden = false, true)");
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => { const a = getComputedStyle(document.getElementById('barAvatar'), '::after'); return parseFloat(a.bottom) < parseFloat(a.top); })()"),
            "The two-step dot sits where the health dot does.");
        string label = await Browser.EvaluateAsync<string>("document.getElementById('barAvatar').getAttribute('aria-label')") ?? string.Empty;
        Assert.Contains("two-step", label, StringComparison.Ordinal);
        Assert.Contains("server health", label, StringComparison.Ordinal);

        await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
    }

    /// <summary>
    /// Pause, Edit and Remove are behind a row's menu; Test connection stays in the row; Escape closes the menu and returns
    /// the cursor to its button.
    /// </summary>
    [Fact]
    public async Task A_data_sources_row_keeps_Test_connection_and_folds_the_rest_behind_its_menu()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/sources", token);
        await WaitForAsync("!!document.querySelector('#sources details.rowmenu')", "No source row has a menu.");

        string shape = await Browser.EvaluateAsync<string>(
            "(() => { const r = document.querySelector('#sources details.rowmenu').closest('tr'); "
            + "const direct = [...r.querySelectorAll('td.acts > button')].map(b => b.textContent.trim()); "
            + "const inMenu = [...r.querySelectorAll('.rowmenubody button')].map(b => b.textContent.trim()); "
            + "return JSON.stringify({ direct, inMenu, hidden: !r.querySelector('.rowmenubody').checkVisibility() }); })()") ?? string.Empty;
        Assert.True(
            shape.Contains("\"Test connection\"", StringComparison.Ordinal)
            && !shape.Contains("\"direct\":[\"Pause", StringComparison.Ordinal)
            && shape.Contains("Edit", StringComparison.Ordinal) && shape.Contains("Remove", StringComparison.Ordinal)
            && shape.Contains("\"hidden\":true", StringComparison.Ordinal),
            "Test connection is not alone in the row, or the menu does not hold Edit and Remove: " + shape);

        await ClickAsync("#sources details.rowmenu > summary");
        await WaitForAsync(
            "document.querySelector('#sources details.rowmenu[open] .rowmenubody')?.checkVisibility() === true",
            "The row's menu did not open.");

        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 });
        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Escape", code = "Escape", windowsVirtualKeyCode = 27 });

        await WaitForAsync(
            "!document.querySelector('#sources details.rowmenu[open]') "
            + "&& document.activeElement?.tagName === 'SUMMARY' && !!document.activeElement.closest('#sources')",
            "Escape did not close the menu and return focus to its button.");
    }
}
