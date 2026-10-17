using System;
using System.Text.Json;
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

        string? before = await Browser.EvaluateAsync<string>("(window.routeHash ? routeHash() : '')");
        await ClickAsync(stop);

        await WaitForAsync(
            "window.__writes.length > 0",
            "Pressing Stop sent no request. Either the click did not reach the button, or the "
            + "button does nothing — which is D-57, and was true for a day.");

        string sent = string.Join('\n', await WritesAsync());

        // Named rather than matched loosely, because "some request went out" is
        // satisfied by the listing reloading itself.
        Assert.Contains("/admin/layers/", sent, StringComparison.Ordinal);
        // Since D-276 the row names its service, so the layer is not looked up by a name that
        // several services can share.
        Assert.Matches(@"/(stop|start)\?service=[^&]+$", sent);

        Assert.Equal(before, await Browser.EvaluateAsync<string>("(window.routeHash ? routeHash() : '')"));
    }

    /// <summary>
    /// Ticked services are stopped together, each through its own row's route.
    /// </summary>
    /// <remarks>
    /// <b>Server Manager's toolbar over its list</b> (owner, 2026-10-17). The bar sends one request per ticked service,
    /// to the same route the row's own button uses, and names the service in it — so two ticks are two requests.
    /// </remarks>
    [Fact]
    public async Task Two_ticked_services_are_stopped_with_one_press()
    {
        (string token, _) = await SignInAsync();

        const string ticks = "tr[data-service]:has(button[data-service-status][data-to=\"stop\"]:not([disabled])) [data-svc-pick]";

        await OpenFolderHoldingAsync(ticks, token);

        int ticked = await Browser.EvaluateAsync<int>(
            "(() => { window.confirm = () => true; const boxes = [...document.querySelectorAll("
            + JsonSerializer.Serialize(ticks) + ")].slice(0, 2);"
            + " for (const b of boxes) b.click(); return boxes.length; })()");

        Assert.True(ticked >= 1, "No started service on the list could be ticked.");

        await WaitForAsync("!!document.getElementById('svcBulkStop') && !document.getElementById('svcBulkStop').disabled",
            "Ticking services drew no Stop in the selection bar.");

        await ClickAsync("#svcBulkStop");

        await WaitForAsync(
            $"window.__writes.filter(w => w.includes('/admin/layers/') && w.includes('/stop?service=')).length >= {ticked}",
            "Stop in the selection bar did not send one stop per ticked service through the row's route.");
    }

    /// <summary>
    /// A click where the Stop icon is drawn reaches the button, as a mouse's would.
    /// </summary>
    /// <remarks>
    /// <b>2026-10-17: the icon swallowed the click.</b> Stop became an SVG inside a button, and a mouse click lands on
    /// the deepest element under the pointer — the SVG — whose dataset is empty, so the handler did nothing while
    /// keyboard Enter, and the test above, which calls <c>click()</c> on the button itself, both worked. This one asks
    /// the page what is under the button's centre and clicks that, which is the mechanism a pointer uses.
    /// </remarks>
    [Fact]
    public async Task A_click_on_the_stop_icon_reaches_its_button()
    {
        (string token, _) = await SignInAsync();

        const string stop = "tr[data-service] button[data-service-status]:not([disabled])";

        await OpenFolderHoldingAsync(stop, token);

        bool clicked = await Browser.EvaluateAsync<bool>(
            "(() => { const b = document.querySelector(" + JsonSerializer.Serialize(stop) + ");"
            + " const r = b.getBoundingClientRect();"
            + " const under = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);"
            + " if (!under) return false; under.click(); return true; })()");

        Assert.True(clicked, "Nothing is under the Stop button's centre.");

        await WaitForAsync(
            "window.__writes.length > 0",
            "A click where the Stop icon is drawn sent no request: the icon, not the button, took the click.");
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
        // The row's body, not its name: the name is a link of its own since 2026-10-17, and this test is about
        // the row's handler, which a link would bypass.
        await OpenFolderHoldingAsync("tr[data-service] td.svcbody", token);

        await ClickAsync("tr[data-service] td.svcbody");

        await WaitForAsync(
            "(window.routeHash ? routeHash() : '').startsWith('#/service/')",
            "Clicking a service row did not open the service.");
    }
}
