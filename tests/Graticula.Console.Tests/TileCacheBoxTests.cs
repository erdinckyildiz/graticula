using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The tile-cache box shows the lifetime the layer has, and an empty box is not zero.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-159](../../docs/architecture-debt.md), and the two halves are one defect.</b>
/// <c>showLayer</c> read <c>l.cacheSeconds</c> off the <c>/admin/layers</c> listing and
/// that listing never carried it, so the box was blank whatever the layer was set to.
/// The Set handler then read <c>Number($("ttl").value)</c>, and <c>Number("")</c> is
/// <b>0</b>, which the endpoint takes as the real answer <em>never serve a cached
/// tile</em>. An operator who opened a layer with a 60-second lifetime and pressed Set
/// turned caching off, and was told so in a toast that reads like confirmation.
/// </para>
/// <para>
/// <b>In a browser, because that is the only place the defect exists.</b> Every server
/// test passes: the endpoint stores what it is sent, and it was sent zero. Reading the
/// markup would prove the input element is written, which it always was. What has to be
/// proved is that a value reaches it, and that an empty one does not become a number.
/// </para>
/// <para>
/// <b>The second test asserts on the request the page makes, and reading the layer back
/// instead could never have worked.</b> The first version did exactly that and passed
/// against the defect. <see cref="ConsoleTest"/>'s harness replaces <c>fetch</c> for
/// anything that is not a <c>GET</c>, records it and answers with an empty document —
/// deliberately, so a suite proving a button works cannot stop somebody's services — so
/// the <c>PUT</c> never reaches the server and the layer reads back unchanged whatever
/// the page asked for. The recording is the assertion here as everywhere else in this
/// suite.
/// </para>
/// <para>
/// <b>It wraps <c>fetch</c> a second time, and that is a deliberate exception to the
/// harness's own rule.</b> <see cref="ConsoleTest.WritesAsync"/> records the method and
/// the URL and not a JSON body, on the stated grounds that a JSON body is the caller's
/// own string and reading it back asserts against the test's own construction. That
/// holds wherever the test composed the body. Here the *console* composes it, and which
/// number it puts in is the entire defect — so this one reads it, and says why.
/// </para>
/// </remarks>
public sealed class TileCacheBoxTests : ConsoleTest
{
    [Fact]
    public async Task A_layer_with_a_lifetime_shows_it_in_the_box()
    {
        string layer = await AnyLayerAsync();

        // Set a lifetime through the API, so the fixture is this test's own rather than
        // a fact about whichever machine it runs on.
        (int status, string body) = await AdminAsync(
            HttpMethod.Put,
            $"/admin/layers/{Uri.EscapeDataString(layer)}/cache",
            """{"seconds":137}""");

        Assert.True(
            status is 200 or 204,
            $"Could not set a cache lifetime on '{layer}': {status} {body}");

        try
        {
            (string token, _) = await SignInAsync();

            await OpenAsync($"/server/#/layer/{Uri.EscapeDataString(layer)}/caching", token);

            await WaitForAsync(
                "!!document.getElementById('ttl')",
                "The layer's Caching page has no tile-lifetime control at all.");

            // <b>Waiting for the value rather than reading it once.</b> The element is in
            // the markup from the first paint and the listing arrives afterwards, so a
            // single read races the fetch and would fail for the wrong reason.
            await WaitForAsync(
                "document.getElementById('ttl')?.value === '137'",
                "The tile-lifetime box never showed the 137 seconds this layer is set to. "
                + "A control that displays nothing for a value that exists is D-159: the "
                + "next person to press Set sends an empty box.");

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await AdminAsync(
                HttpMethod.Put,
                $"/admin/layers/{Uri.EscapeDataString(layer)}/cache",
                """{"seconds":null}""");
        }
    }

    [Fact]
    public async Task Pressing_set_with_an_empty_box_sends_no_lifetime_rather_than_zero()
    {
        string layer = await AnyLayerAsync();

        (int status, _) = await AdminAsync(
            HttpMethod.Put,
            $"/admin/layers/{Uri.EscapeDataString(layer)}/cache",
            """{"seconds":null}""");

        Assert.True(status is 200 or 204, $"Could not clear '{layer}'s lifetime: {status}.");

        try
        {
            (string token, _) = await SignInAsync();

            await OpenAsync($"/server/#/layer/{Uri.EscapeDataString(layer)}/caching", token);

            await WaitForAsync(
                "!!document.querySelector('[data-cache]:not([data-clear])')",
                "The layer's Caching page has no Set button.");

            // <b>Every step asserted, because a step that silently did nothing is how the
            // first version of this test passed against the defect.</b> If the box is
            // missing, or the click never lands, the page sends nothing — and "nothing
            // was sent" would read exactly like the fix working.
            bool emptied = await Browser.EvaluateAsync<bool>(
                "(() => { const b = document.getElementById('ttl'); if (!b) return false; "
                + "b.value = ''; return true; })()");

            Assert.True(emptied, "There is no tile-lifetime box on this layer's Caching page.");

            // Record what the page asks for. `api()` goes through `fetch`, so this sees the
            // body the console composed rather than anything this test composed.
            await Browser.EvaluateAsync<bool>(
                "(() => { window.__cachePuts = []; const f = window.fetch; "
                + "window.fetch = (u, o) => { if (String(u).endsWith('/cache') "
                + "&& o && o.method === 'PUT') window.__cachePuts.push(String(o.body || '')); "
                + "return f(u, o); }; return true; })()");

            bool clicked = await Browser.EvaluateAsync<bool>(
                "(() => { const b = document.querySelector('[data-cache]:not([data-clear])'); "
                + "if (!b) return false; b.click(); return true; })()");

            Assert.True(clicked, "There is no Set button on this layer's Caching page.");

            await WaitForAsync(
                "(window.__cachePuts || []).length > 0",
                "Pressing Set sent no PUT to the cache endpoint at all, so this test proved "
                + "nothing about what an empty box asks for.");

            string sent = await Browser.EvaluateAsync<string>(
                "(window.__cachePuts || []).join(' ')") ?? string.Empty;

            Assert.False(
                sent.Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Contains("\"seconds\":0", StringComparison.Ordinal),
                $"Pressing Set with an empty box asked for `{sent}`. `Number(\"\")` is 0 and "
                + "the endpoint reads 0 as *never serve a cached tile*, so this is how an "
                + "operator turns caching off for a layer without meaning to — D-159.");

            Assert.Contains("null", sent, StringComparison.Ordinal);

            string[] pressErrors = await PageErrorsAsync();
            Assert.Empty(pressErrors);
        }
        finally
        {
            await AdminAsync(
                HttpMethod.Put,
                $"/admin/layers/{Uri.EscapeDataString(layer)}/cache",
                """{"seconds":null}""");
        }
    }
}
