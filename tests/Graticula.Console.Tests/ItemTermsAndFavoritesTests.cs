using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// An item's terms of use and a member's favourites, on Studio's item page and in My content — ADR-202, D-305.
/// </summary>
/// <remarks>
/// Both were built on 2026-10-18 with an API and no screen, so terms were set only through the API and a favourite
/// could be marked by a client and seen nowhere here. The harness traps every write and answers it with <c>{}</c>; what
/// is asserted is what the page sends and shows.
/// </remarks>
public sealed class ItemTermsAndFavoritesTests : ConsoleTest
{
    private static string Service => Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings";

    private async Task OpenItemAsync()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync($"/studio/#/service/{Service}", token);
        await WaitForAsync(Shown("#itemFavorite"), "The item page offers no favourite (D-305).");
    }

    [Fact]
    public async Task Terms_of_use_are_written_beside_the_description_and_shown_under_it()
    {
        await OpenItemAsync();

        await ClickAsync("#serviceDescription [data-describe]");
        await WaitForAsync("!!document.getElementById('describeTerms')", "Edit details offers no terms of use (D-305).");

        await Browser.EvaluateAsync<bool>("(document.getElementById('describeTerms').value = 'CC BY 4.0.\\nCite ItemTermsTests.', true)");
        await ClickAsync("#serviceDescription [data-describe-save]");

        await WaitForAsync("!!document.querySelector('#serviceDescription details.itemterms')",
            "After Save the page does not show the terms that were saved.");

        string[] writes = await WritesAsync();
        Assert.Contains(writes, w => w.StartsWith("PUT /content/items/", StringComparison.Ordinal) && w.Contains("/license", StringComparison.Ordinal));

        // Kept as written: the line break is a line break, and the text is not read as markup.
        Assert.Contains("Cite ItemTermsTests", await Browser.EvaluateAsync<string>(
            "document.querySelector('#serviceDescription details.itemterms p').textContent") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("pre-line", await Browser.EvaluateAsync<string>(
            "getComputedStyle(document.querySelector('#serviceDescription details.itemterms p')).whiteSpace"));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task An_item_is_put_in_the_organizations_content_categories_from_its_page()
    {
        await OpenItemAsync();

        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            const method = ((init && init.method) || "GET").toUpperCase();
            if (method !== "GET" || new URL(url, location.href).pathname !== "/admin/item-categories") return real(input, init);
            return new Response(JSON.stringify({ categories: [
              { id: "c1", parent: null, name: "Water", path: "/Categories/Water", items: 0 },
              { id: "c2", parent: "c1", name: "Rivers", path: "/Categories/Water/Rivers", items: 0 },
            ] }), { status: 200, headers: { "Content-Type": "application/json" } });
          };
          return true;
        })();
        """);

        await WaitForAsync(Shown("#serviceDescription [data-item-categorize]"), "The item page offers no Categorize (D-305).");
        await ClickAsync("#serviceDescription [data-item-categorize]");
        await WaitForAsync("document.getElementById('catDialog').open && document.querySelectorAll('[data-cat-assign]').length === 2",
            "Categorize did not offer the organization's two categories.");

        // The tree: Rivers is under Water.
        Assert.True(await Browser.EvaluateAsync<bool>(
            "!!document.querySelector('[data-cat-assign=\"c1\"]').closest('li').querySelector('[data-cat-assign=\"c2\"]')"),
            "Rivers is not drawn under Water.");

        await ClickAsync("[data-cat-assign='c2']");
        await ClickAsync("#catAssignSave");
        await WaitForAsync("!document.getElementById('catDialog').open", "Save did not close Categorize.");

        string id = await Browser.EvaluateAsync<string>("serviceItem.itemId") ?? string.Empty;
        Assert.Contains(await WritesAsync(), w => w.Contains($"/content/items/{id}/categories", StringComparison.Ordinal));
        Assert.Contains("Water › Rivers", await Browser.EvaluateAsync<string>(
            "document.querySelector('#serviceDescription .itemcats')?.textContent || ''") ?? string.Empty, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The one category dialog edits members' categories as it did before it learned content's, at the same route.
    /// </summary>
    [Fact]
    public async Task The_category_dialog_still_edits_member_categories()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/members", token);
        await WaitForAsync(Shown("#mfCatEdit"), "Members offers no Edit categories.");

        await ClickAsync("#mfCatEdit");
        await WaitForAsync("document.getElementById('catDialog').open", "Edit categories opened nothing.");
        Assert.Equal("Member categories", await Browser.EvaluateAsync<string>("document.getElementById('catDialogTitle').textContent"));

        await Browser.EvaluateAsync<bool>("(document.getElementById('catNewName').value = 'zz-dialog', true)");
        await ClickAsync("#catNewAdd");
        await WaitForAsync("window.__writes.some(w => w.includes('/admin/member-categories'))", "Add sent nothing to the member categories.");
        Assert.Contains(await WritesAsync(), w => w.StartsWith("POST /admin/member-categories", StringComparison.Ordinal));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_favourite_is_marked_on_the_item_page_and_listed_under_Favorites()
    {
        await OpenItemAsync();

        string before = await Browser.EvaluateAsync<string>("document.getElementById('itemFavorite').textContent") ?? string.Empty;
        // Focused first: the harness's click does not move focus, and what is asserted is that the button keeps it.
        await Browser.EvaluateAsync<bool>("(document.getElementById('itemFavorite').focus(), true)");
        await ClickAsync("#itemFavorite");
        await WaitForAsync($"document.getElementById('itemFavorite').textContent !== {System.Text.Json.JsonSerializer.Serialize(before)}",
            "Pressing the favourite changed nothing on it.");

        string[] writes = await WritesAsync();
        string id = await Browser.EvaluateAsync<string>("serviceItem.itemId") ?? string.Empty;
        Assert.Contains(writes, w => w.Contains($"/content/items/{id}/favorite", StringComparison.Ordinal));
        Assert.NotEqual(before, await Browser.EvaluateAsync<string>("document.getElementById('itemFavorite').textContent"));
        Assert.Equal("itemFavorite", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

        // My content lists it under Favorites, from the ids /content/favorites answers.
        await Browser.EvaluateAsync<bool>($$"""
        (() => {
          const real = window.fetch;
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            if (new URL(url, location.href).pathname !== "/content/favorites") return real(input, init);
            return new Response(JSON.stringify({ favorites: [{ id: "{{id}}".replace(/-/g, ""), kind: "service" }] }),
              { status: 200, headers: { "Content-Type": "application/json" } });
          };
          navigateTo("#/content/favorites");
          return true;
        })();
        """);

        await WaitForAsync("(document.querySelector('#contentScopes a[aria-current=page]')?.textContent || '').includes('Favorites')",
            "My content has no Favorites to open.");
        await WaitForAsync("document.querySelectorAll('#contentRows tr').length === 1 && !document.querySelector('#contentRows td.empty')",
            "Favorites does not list the one item marked.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
