using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The service page's <i>Kept warm</i> section — ADR-218 — pins and unpins a service against the server and says what
/// the server then says.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writes reach the server, which is what most tests here are built to prevent.</b> The harness traps every
/// write; this test lets its own route through (<c>window.__letWritesTo</c>), because what has to be proved is that the
/// state on screen is the state the server holds — a trapped write answers <c>{}</c>, and a page that drew
/// <i>Kept warm</i> from that would be asserting against the harness. The <c>finally</c> unpins over HTTP whatever
/// happened, so no run leaves a service pinned.
/// </para>
/// <para>
/// <b>And it presses the button from the keyboard and asks where focus went.</b> Every new screen in this console has
/// shipped once with a redraw that dropped the focus of the reader who pressed Save, and once with a result nobody
/// using a screen reader heard. Both are asserted here rather than remembered.
/// </para>
/// </remarks>
public sealed class KeptWarmControlTests : ConsoleTest
{
    /// <summary>Keep warm, then stop, from the Parameters page; each answer is drawn, announced and true on the server.</summary>
    [Fact]
    public async Task Keeping_a_service_warm_and_stopping_are_shown_as_the_server_holds_them()
    {
        (string token, _) = await SignInAsync();

        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_LARGE") ?? "hosted/ci_many";
        int slash = service.LastIndexOf('/');
        string folder = slash < 0 ? string.Empty : service[..slash];
        string name = slash < 0 ? service : service[(slash + 1)..];
        string route = $"/admin/services/{Uri.EscapeDataString(name)}/pinned?folder={Uri.EscapeDataString(folder)}";

        (int read, string before) = await AdminAsync(HttpMethod.Get, route);
        Assert.True(read == 200, $"GET {route} answered {read}: {before}. The test needs a feature service it can pin.");
        Assert.False(
            JsonDocument.Parse(before).RootElement.GetProperty("pinned").GetBoolean(),
            $"{service} is already pinned before the test begins, so a run left it pinned or somebody pinned it; "
            + "unpin it and run again rather than letting this test take the blame for the state it found.");

        try
        {
            await OpenAsync(
                "/server/#/service/" + string.Join("/", Array.ConvertAll(service.Split('/'), Uri.EscapeDataString))
                + "?section=limits",
                token);

            await WaitForAsync(Shown("#servicePinToggle"), "Parameters shows no Keep warm button to an administrator.");
            await WaitForAsync(
                "(document.getElementById('servicePinState')?.textContent || '').includes('Not kept warm')",
                "The section never said the service is not kept warm, which is what the server holds.");

            string count = await TextAsync("servicePinCount");
            Assert.Matches(@"^\d+ of \d+ services on this server are kept warm\.$", count);

            // Let this test's own route reach the server; every other write stays trapped.
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/pinned?'], true)");

            await PressAsync("#servicePinToggle");

            await WaitForAsync(
                "(document.getElementById('servicePinSays')?.textContent || '').includes('kept warm from now on')",
                "Pressing Keep warm said nothing in the live region, so a screen-reader user hears no result.");

            Assert.Equal("servicePinToggle", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));
            Assert.Equal("Stop keeping warm", await TextAsync("servicePinToggle"));
            Assert.StartsWith("Kept warm", await TextAsync("servicePinState"), StringComparison.Ordinal);
            Assert.Equal("Kept warm", await TextAsync("heroPin"));
            Assert.Equal("polite", await Browser.EvaluateAsync<string>(
                "document.getElementById('servicePinSays').getAttribute('aria-live')"));

            (_, string pinned) = await AdminAsync(HttpMethod.Get, route);
            JsonElement held = JsonDocument.Parse(pinned).RootElement;
            Assert.True(held.GetProperty("pinned").GetBoolean(), $"The page said kept warm and the server holds: {pinned}");
            Assert.Contains(
                $"{held.GetProperty("pinnedOnServer").GetInt32()} of {held.GetProperty("limit").GetInt32()} services",
                await TextAsync("servicePinCount"),
                StringComparison.Ordinal);

            // The warmer's first round lands within seconds of a pin (ADR-218 §5.2), and the page reads it again.
            await WaitForAsync(
                "(document.getElementById('servicePinState')?.textContent || '').includes('Last renewed')",
                "Ten seconds after the pin the page still did not say when the server last renewed the service.");

            await PressAsync("#servicePinToggle");

            await WaitForAsync(
                "(document.getElementById('servicePinSays')?.textContent || '').includes('no longer kept warm')",
                "Pressing Stop keeping warm said nothing in the live region.");

            Assert.Equal("servicePinToggle", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));
            Assert.Equal("Keep warm", await TextAsync("servicePinToggle"));
            Assert.Equal("Not kept warm.", await TextAsync("servicePinState"));
            Assert.Equal(string.Empty, await TextAsync("heroPin"));

            (_, string after) = await AdminAsync(HttpMethod.Get, route);
            Assert.False(JsonDocument.Parse(after).RootElement.GetProperty("pinned").GetBoolean(),
                $"The page said not kept warm and the server holds: {after}");

            string[] writes = await WritesAsync();
            Assert.Equal(2, Array.FindAll(writes, w => w.StartsWith("PUT ", StringComparison.Ordinal)
                && w.Contains("/pinned?", StringComparison.Ordinal) && w.EndsWith("(sent)", StringComparison.Ordinal)).Length);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, route, "{\"pinned\":false}");
        }
    }

    /// <summary>The page's text of one element, whitespace collapsed, as a reader sees it.</summary>
    private async Task<string> TextAsync(string id) =>
        await Browser.EvaluateAsync<string>(
            $"(document.getElementById({JsonSerializer.Serialize(id)})?.textContent || '').replace(/\\s+/g, ' ').trim()")
        ?? string.Empty;

    /// <summary>
    /// Focuses a button and activates it as a keyboard would, so where focus ends up afterwards is a fact about the page
    /// and not about how the test clicked.
    /// </summary>
    private async Task PressAsync(string selector)
    {
        string quoted = JsonSerializer.Serialize(selector);
        Assert.True(await Browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.querySelector({quoted}); if (!e) return false; e.focus(); return document.activeElement === e; }})()"),
            $"{selector} could not take the keyboard's focus.");

        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, text = "\r" });
        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13 });
    }
}
