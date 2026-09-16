using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Studio shows somebody else's item and does not offer to change it — ADR-075, D-271.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server's answer, read, and nothing decided here.</b> Since ADR-075 an item's sharing, fields,
/// symbology and the rest are changed by its owner or an administrator and the endpoints answer 403 to
/// anybody else — while Studio went on drawing a sharing button on every row and every settings page on
/// every layer. The listings now carry <c>manages</c>, computed by <c>LayerAccess.MayManage</c>, and the
/// console follows it.
/// </para>
/// <para>
/// <b>Made false in the browser rather than by signing in as somebody else</b>, because this suite never
/// writes to the server — creating a second member is a write — and signs in as an administrator, who
/// manages everything. What is under test is the console's reading of the flag; the flag's computation
/// is pinned against the rule in <c>ALayerIsEditedByItsOwnerTests</c>.
/// </para>
/// </remarks>
public sealed class SomebodyElsesLayerIsReadNotOfferedTests : ConsoleTest
{
    /// <summary>Every listing answer, with every item and layer marked as not the reader's to change.</summary>
    private const string NobodyManages = """
        (() => {
          const inner = window.fetch.bind(window);
          const listings = ["/content/items", "/content/layers", "/admin/layers"];
          const mark = v => {
            if (Array.isArray(v)) { v.forEach(mark); return; }
            if (v && typeof v === "object") {
              if ("manages" in v) v.manages = false;
              Object.values(v).forEach(mark);
            }
          };
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            const response = await inner(input, init);
            if (!listings.some(l => new URL(url, location.href).pathname === l)) return response;
            const body = await response.clone().json().catch(() => null);
            if (body === null) return response;
            mark(body);
            return new Response(JSON.stringify(body), { status: response.status, headers: { "Content-Type": "application/json" } });
          };
        })();
        """;

    [Fact]
    public async Task A_row_and_a_layer_page_the_reader_does_not_manage_are_shown_and_not_offered()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await Browser.PlantAsync(NobodyManages);

        // ---- the content list: the sharing pill is shown, and is not a button ----
        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentRows tr').length > 0 && !document.querySelector('#contentRows td.empty')",
            "My content listed nothing, so there is no row to read.");

        Assert.Equal(0, await Browser.EvaluateAsync<int>("document.querySelectorAll('#contentRows button[data-share]').length"));

        Assert.True(
            await Browser.EvaluateAsync<bool>("document.querySelectorAll('#contentRows td span[title*=\"owner or an administrator\"]').length > 0"),
            "No row says who changes its sharing, so a reader who cannot press it is not told why.");

        // ---- a layer's settings: readable, not operable, and the page says why ----
        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "!!document.querySelector('#editPages section.page.on .ownership')",
            "A layer the reader does not manage opened with no note saying so.");

        string note = await Browser.EvaluateAsync<string>(
            "document.querySelector('#editPages section.page.on .ownership').textContent") ?? string.Empty;
        Assert.Contains("only its owner or an administrator changes them", note, StringComparison.Ordinal);

        // <b>Visible, because the note is only worth something if it is seen</b> — the fault this suite has
        // caught three times is a control that exists and is not on screen.
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#editPages section.page.on .ownership")), "The ownership note is not on screen.");

        // <b>Store is not offered, and the page's own tabs still work.</b> The first version made whole
        // sections inert, which took the tabs with them and looked exactly like enabled.
        Assert.True(
            await Browser.EvaluateAsync<bool>("document.querySelector('[data-symbology-put]').disabled"),
            "Store is pressable on a layer the reader does not manage.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#symItemTabs a, #symItemTabs button')].length > 0"
                + " && [...document.querySelectorAll('#symItemTabs button')].every(b => !b.disabled)"
                + " && !document.querySelector('#editPages section.page[inert]')"),
            "The page's tabs were locked with its controls, so a reader cannot move to another page.");

        // Nothing was sent: an inert page cannot be the source of a write.
        Assert.Empty(await WritesAsync());
        NothingWentWrong(await PageErrorsAsync());
    }
}
