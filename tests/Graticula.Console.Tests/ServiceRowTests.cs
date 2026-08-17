using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// What a click on a service row does, and does not do.
/// </summary>
public sealed class ServiceRowTests : ConsoleTest
{
    /// <summary>
    /// Pressing Stop stops the service instead of opening it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-59's first defect, written as the assertion that was missing.</b> The
    /// owner pressed <b>Stop</b> on a service row and the service page opened. The
    /// row is clickable and carries controls that do something else, and the
    /// dispatcher kept an exception list — <em>unless it was Delete, unless it was
    /// the sharing select</em> — which a third control was never added to. The fix
    /// asks what was clicked instead; this is what holds it.
    /// </para>
    /// <para>
    /// <b>The address is half the assertion and the recorded write is the other
    /// half.</b> Checking only that the page did not navigate would pass for a
    /// button that does nothing at all, which is a defect of the same family
    /// (D-57: stopping a service did nothing for a day). So both: the hash stayed,
    /// and the click reached a request.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Stop_on_a_service_row_stops_it_rather_than_opening_the_service()
    {
        (string token, _) = await SignInAsync();

        // Enabled only. A service holding no layers has no cover to stop and the
        // button says so by being disabled; clicking it would assert nothing.
        const string stop = "tr[data-service] button[data-service-status]:not([disabled])";

        await OpenFolderHoldingAsync(stop, token);

        string? before = await Browser.EvaluateAsync<string>("location.hash");
        await ClickAsync(stop);

        await WaitForAsync(
            "window.__writes.length > 0",
            "Pressing Stop sent no request. Either the click did not reach the button, or the "
            + "button does nothing — which is D-57, and was true for a day.");

        string sent = string.Join('\n', await WritesAsync());

        // Named rather than matched loosely, because "some request went out" is
        // satisfied by the listing reloading itself.
        Assert.Contains("/admin/layers/", sent, StringComparison.Ordinal);
        Assert.Matches(@"/(stop|start)$", sent);

        Assert.Equal(before, await Browser.EvaluateAsync<string>("location.hash"));
    }

    /// <summary>
    /// A click on the row itself still opens the service.
    /// </summary>
    /// <remarks>
    /// <b>The other side of the same dispatcher, and it needs its own test.</b>
    /// The cheapest way to make the test above pass is to stop the row navigating
    /// at all, which would be a regression nobody notices until they try to open
    /// a service. One test per direction is what makes the pair hold the rule
    /// rather than one half of it.
    /// </remarks>
    [Fact]
    public async Task A_click_on_the_row_itself_opens_the_service()
    {
        (string token, _) = await SignInAsync();
        await OpenFolderHoldingAsync("tr[data-service] span.name", token);

        await ClickAsync("tr[data-service] span.name");

        await WaitForAsync(
            "location.hash.startsWith('#/service/')",
            "Clicking a service row's name did not open the service.");
    }
}
