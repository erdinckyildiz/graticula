using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The recycle bin, as a scope of My content — ADR-203 condition 2, D-305.
/// </summary>
/// <remarks>
/// Built on 2026-10-18 with an API and no screen, so a mistaken delete was undone only by somebody who knew the route.
/// The bin is answered in the page, because the fixture's is empty and the harness traps every write; the server's
/// half is <c>ADeletedMapComesBackTests</c> and <c>ADeletedImageServiceComesBackTests</c>.
/// </remarks>
public sealed class RecycleBinScreenTests : ConsoleTest
{
    [Fact]
    public async Task A_deleted_item_is_listed_in_the_bin_and_restored_from_it()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/content", token);
        await WaitForAsync("document.querySelectorAll('#contentRows tr').length > 0", "My content did not draw.");

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            const method = ((init && init.method) || "GET").toUpperCase();
            if (method !== "GET" || new URL(url, location.href).pathname !== "/admin/recycle-bin") return real(input, init);
            return new Response(JSON.stringify({ retentionDays: 14, entries: [{
              id: "33333333-3333-3333-3333-333333333333", item: "abc", kind: "service", title: "hosted/zz_parcels_old",
              deleted: "2026-10-27T10:00:00Z", expires: "2026-11-10T10:00:00Z", tables: 2 },
              { id: "44444444-4444-4444-4444-444444444444", item: "def", kind: "web map", title: "zz old map",
              deleted: "2026-10-26T10:00:00Z", expires: "2026-11-09T10:00:00Z", tables: 0 },
              ...Array.from({ length: 11 }, (_, i) => ({ id: `55555555-5555-5555-5555-5555555555${String(i).padStart(2, "0")}`,
                item: "f" + i, kind: "service", title: "zz filler " + i, deleted: "2026-10-20T10:00:00Z",
                expires: "2026-11-03T10:00:00Z", tables: 1 }))] }),
              { status: 200, headers: { "Content-Type": "application/json" } });
          };
          navigateTo("#/content/recycle");
          return true;
        })();
        """);

        await WaitForAsync(Shown("#recyclePanel"), "My content has no recycle bin to open (ADR-203, D-305).");
        await WaitForAsync("(document.getElementById('recycleRows')?.textContent || '').includes('zz_parcels_old')",
            "The bin does not list what is in it.");

        // The list's own controls are out of the way while the bin shows.
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('contentTable').hidden && document.getElementById('contentToolbar').hidden"),
            "The content list and its toolbar are still shown beside the bin.");
        Assert.Contains("14 days", await Browser.EvaluateAsync<string>("document.getElementById('recycleAbout').textContent") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains("2 tables of data", await Browser.EvaluateAsync<string>("document.getElementById('recycleRows').textContent") ?? string.Empty,
            StringComparison.Ordinal);

        // The bare name, its folder and its kind under it, in words (a design review found "web map" lower-case).
        string rows = await Browser.EvaluateAsync<string>("document.getElementById('recycleRows').innerText") ?? string.Empty;
        Assert.Contains("Service in hosted", rows, StringComparison.Ordinal);
        Assert.Contains("Web map", rows, StringComparison.Ordinal);

        // Searched by name.
        await Browser.EvaluateAsync<bool>("(el = document.getElementById('recycleFilter'), el.value = 'map', el.dispatchEvent(new Event('input', { bubbles: true })), true)");
        await WaitForAsync("document.querySelectorAll('#recycleRows tr').length === 1 && document.getElementById('recycleRows').textContent.includes('zz old map')",
            "The bin's search did not narrow it to the one name.");
        await Browser.EvaluateAsync<bool>("(el = document.getElementById('recycleFilter'), el.value = '', el.dispatchEvent(new Event('input', { bubbles: true })), true)");

        // Paged at the list's size, and the arrow pressed keeps focus once the page is drawn again (a design review).
        await WaitForAsync("!!document.querySelector('#recyclePager [data-page=\"recycleRows\"]')", "Thirteen entries are not paged.");
        await Browser.EvaluateAsync<bool>("(() => { const a = [...document.querySelectorAll('#recyclePager [data-page]')].pop(); a.focus(); a.click(); return true; })()");
        await WaitForAsync("document.activeElement?.dataset?.page === 'recycleRows'", "After Next, focus left the pager.");
        await Browser.EvaluateAsync<bool>("(() => { const a = document.querySelector('#recyclePager [data-page]'); a.click(); return true; })()");

        await Browser.EvaluateAsync<bool>("(document.querySelector('[data-recycle-restore]').focus(), true)");
        await ClickAsync("[data-recycle-restore]");
        await WaitForAsync("(document.getElementById('recycleSays')?.textContent || '').includes('restored')", "Restore said nothing.");
        Assert.Contains(await WritesAsync(), w => w.StartsWith("POST /admin/recycle-bin/33333333-3333-3333-3333-333333333333/restore", StringComparison.Ordinal));

        // The row that had focus is drawn away; focus goes to the bin's heading, not to the page (a design review).
        Assert.Equal("recycleTitle", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

        // Back to Everything: the list and its toolbar return.
        await Browser.EvaluateAsync<bool>("(navigateTo('#/content/all'), true)");
        await WaitForAsync("!document.getElementById('contentTable').hidden && !document.getElementById('contentToolbar').hidden && document.getElementById('recyclePanel').hidden",
            "Leaving the bin did not bring the content list back.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
