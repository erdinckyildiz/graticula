using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The apps that may sign people in through OAuth, on a screen — ADR-076 condition 5.
/// </summary>
/// <remarks>
/// <b>The two faults every new screen here has shipped with are asserted first</b>, because they have
/// been made and fixed and made again: a redraw that drops focus, and a result that arrives
/// asynchronously in a paragraph nobody announces. Writes never reach the server in this suite; what is
/// asserted is the request the screen sends and what it does after.
/// </remarks>
public sealed class AppsScreenTests : ConsoleTest
{
    [Fact]
    public async Task Apps_lists_what_is_registered_registers_one_and_removes_one()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/apps", token);

        await WaitForAsync(
            "document.querySelectorAll('#appRows tr').length > 0 && document.getElementById('appCount').textContent.length > 0",
            "The Apps screen drew no rows and no count.");

        // ---- the first-run state of a deployment: Field Maps came registered ----
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#appRows tr')].some(r => r.textContent.includes('fieldmaps')"
                + " && r.textContent.includes('Came registered with this server'))"),
            "Field Maps, which migration 51 registers, is not listed as came-registered.");

        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#appNew")), "Register an app is not on screen.");

        // ---- the form opens with focus in it ----
        await ClickAsync("#appNew");

        await WaitForAsync(Shown("#appTitle"), "Register an app opened no form.");
        Assert.Equal("appTitle", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

        // ---- nothing is sent without a name ----
        await ClickAsync("#appSave");
        Assert.Empty(await WritesAsync());
        Assert.Equal("appTitle", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

        // ---- a registration: the request, the announcement, and where focus lands ----
        await Browser.EvaluateAsync<bool>("""
            (() => {
              document.getElementById('appTitle').value = 'Inspections map';
              document.getElementById('appRedirects').value = 'https://maps.example.org/inspections/\n\n  http://localhost:3000/  ';
              return true;
            })()
            """);

        await ClickAsync("#appSave");

        await WaitForAsync(
            "document.getElementById('appSays').textContent.includes('is registered')",
            "Registering an app announced nothing.");

        Assert.Contains(await WritesAsync(), w => w.StartsWith("POST", StringComparison.Ordinal) && w.Contains("/admin/oauth/apps", StringComparison.Ordinal));
        Assert.Equal("polite", await Browser.EvaluateAsync<string>("document.getElementById('appSays').getAttribute('aria-live')"));
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('appForm').hidden"), "The form stayed open after registering.");
        Assert.Equal("appNew", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

        // ---- a removal, after a confirmation that names what it costs ----
        await ClickAsync("[data-app-remove=\"fieldmaps\"]");

        await WaitForAsync(
            "document.getElementById('appSays').textContent.includes('is removed')",
            "Removing an app announced nothing.");

        Assert.Contains(await WritesAsync(), w => w.StartsWith("DELETE", StringComparison.Ordinal) && w.Contains("/admin/oauth/apps/fieldmaps", StringComparison.Ordinal));

        string confirmed = await Browser.EvaluateAsync<string>("(window.__confirmed || []).join(' | ')") ?? string.Empty;
        // Field Maps came registered: the confirmation says so, and names what re-registering it takes.
        Assert.Contains("came registered with this server", confirmed, StringComparison.Ordinal);
        Assert.Contains("app ID fieldmaps", confirmed, StringComparison.Ordinal);
        Assert.Contains("arcgis-fieldmaps://auth/", confirmed, StringComparison.Ordinal);

        Assert.Equal("appNew", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

        NothingWentWrong(await PageErrorsAsync());
    }
}
