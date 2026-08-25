using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Every screen a surface offers is a screen the router knows that surface owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-115](../../docs/architecture-debt.md), and this is not the repair that row names.</b>
/// The row's argument is that ADR-034's two-surface split is the single cause of four recorded
/// console defects, and its own disposition is that the split is an owner decision taken with
/// screenshots in hand — *recorded as a cost with a named alternative rather than as work to
/// do*. Unifying the surfaces is not this test's business and would reverse a decision it has
/// no standing to reverse.
/// </para>
/// <para>
/// <b>What is this test's business is the class of defect the split keeps producing.</b> Three
/// parallel tables decide screen ownership, and three consecutive comments in `console.js` warn
/// about forgetting to add a screen to one — <b>one of them recording that the failure had
/// already happened twice</b>: `/server/#/groups` landed an administrator on Services with no
/// explanation, and Studio's only service page was unreachable because the router forced every
/// `#/service/…` onto Server. Both were silent. A screen missing from the table does not throw;
/// it falls through to its surface's home.
/// </para>
/// <para>
/// <b>So the warning becomes a check.</b> The tables stay exactly as the owner set them; what
/// changes is that forgetting one fails a test instead of stranding somebody on the wrong page.
/// </para>
/// </remarks>
public sealed class ScreenOwnershipTests : ConsoleTest
{
    /// <summary>
    /// The two screens deliberately absent from the surface table, and why.
    /// </summary>
    /// <remarks>
    /// <b>Named here so that absence stays deliberate.</b> `service` and `layer` live in both
    /// surfaces and their ownership is per *page* rather than per screen — `SERVICE_PAGES` and
    /// `LAYER_PAGES` answer for them. Naming a single owner in `SCREEN_SURFACE` is what sent
    /// every Sharing link to Server; the comment in the file says so, and this list is the same
    /// statement in a form that fails.
    /// </remarks>
    private static readonly string[] OwnedPerPage = ["service", "layer"];

    private async Task<JsonDocument> TablesAsync()
    {
        (string token, _) = await SignInAsync();

        // <b>Server's home, because any console page loads the same script.</b> The tables are
        // module-level constants: which screen is open does not change them, and picking the
        // home avoids a test of ownership depending on a screen whose ownership it is checking.
        await OpenAsync("/server/#/services", token);

        // <b>Read out of the running page rather than parsed out of the file.</b> A regex over
        // JavaScript would be a second reading of the same tables, which is the shape of the
        // defect being checked. `console.js` is a classic script, so its top-level `const`
        // bindings are in the global lexical environment and an evaluation sees them.
        // <b>Wait for the script before reading it — 2026-08-25.</b> `OpenAsync`
        // returns when the page has been navigated to, not when `console.js` has
        // defined its module-level constants, and evaluating a moment early throws
        // `ReferenceError: SURFACES` rather than failing an assertion. It passed on a
        // developer machine every time and failed in CI, which is what a race between
        // a navigation and a script looks like when one side is a slower runner.
        await WaitForAsync(
            "typeof SURFACES !== 'undefined' && typeof SCREEN_SURFACE !== 'undefined'",
            "`console.js` never defined SURFACES and SCREEN_SURFACE, so the page's own "
            + "ownership tables cannot be read out of it. Either the script failed to "
            + "load or it no longer declares them at module level.");

        string? json = await Browser.EvaluateAsync<string>(
            """
            JSON.stringify({
              surfaces: Object.fromEntries(
                Object.entries(SURFACES).map(([name, s]) => [name, s.tabs.map(t => t[0])])),
              screens: SCREEN_SURFACE,
              homes: Object.fromEntries(
                Object.entries(SURFACES).map(([name, s]) => [name, s.home])),
              views: [...document.querySelectorAll('.view')]
                .map(v => v.id).filter(id => id.startsWith('view-'))
                .map(id => id.slice('view-'.length)),
            })
            """);

        // A null here means the evaluation threw rather than that the tables are empty, and the
        // two read identically in a JSON parse failure. Say which it was.
        Assert.True(
            json is not null,
            "reading SURFACES and SCREEN_SURFACE out of the page returned nothing, so console.js "
            + "either did not load or its tables are no longer top-level bindings");

        return JsonDocument.Parse(json!);
    }

    /// <summary>
    /// A tab a surface offers is owned by that surface.
    /// </summary>
    /// <remarks>
    /// <b>Both halves matter and they fail differently.</b> A tab missing from
    /// <c>SCREEN_SURFACE</c> is the silent fall-through — the address is accepted, the wrong
    /// screen draws, and nothing says why. A tab present but naming the *other* surface is
    /// worse: the router bounces the request to a surface whose own tab list does not include
    /// it, so it lands on that surface's home instead.
    /// </remarks>
    [Fact]
    public async Task Every_tab_is_owned_by_the_surface_that_offers_it()
    {
        using JsonDocument tables = await TablesAsync();

        JsonElement surfaces = tables.RootElement.GetProperty("surfaces");
        JsonElement screens = tables.RootElement.GetProperty("screens");

        List<string> wrong = [];
        int checked_ = 0;

        foreach (JsonProperty surface in surfaces.EnumerateObject())
        {
            foreach (JsonElement tab in surface.Value.EnumerateArray())
            {
                string name = tab.GetString()!;

                if (OwnedPerPage.Contains(name))
                {
                    continue;
                }

                checked_++;

                if (!screens.TryGetProperty(name, out JsonElement owner))
                {
                    wrong.Add(
                        $"'{name}' is a tab of {surface.Name} and is absent from SCREEN_SURFACE, "
                        + $"so /{OtherThan(surfaces, surface.Name)}/#/{name} falls through to "
                        + "that surface's home with nothing said");

                    continue;
                }

                if (owner.GetString() != surface.Name)
                {
                    wrong.Add(
                        $"'{name}' is a tab of {surface.Name} and SCREEN_SURFACE says "
                        + $"{owner.GetString()}, so the router sends it to a surface that does "
                        + "not offer it");
                }
            }
        }

        Assert.True(checked_ > 0, "no tabs were read, so this asserted nothing");

        Assert.True(
            wrong.Count == 0,
            "A screen the surface table does not agree about is the silent navigation D-115 is "
            + "about — it has happened twice and both times the address was accepted:\n  "
            + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The surface table names no screen the console cannot draw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other direction, and it rots quietly.</b> An entry for a screen that has been
    /// removed or renamed is a rule about an address nothing serves — harmless until somebody
    /// reuses the name, at which point the router obeys an ownership decision nobody made.
    /// </para>
    /// <para>
    /// <b>Against the views rather than the tabs, and the first draft of this test had that
    /// wrong.</b> It asserted that every entry is a tab somewhere and failed on `group`, which is
    /// correct as it stands: `#/group/planning` is a screen with an address and no tab, the same
    /// as `service` and `layer`. What makes an entry real is that `openScreen` can show
    /// `view-{screen}`, so that is what is asked. Reading the ids out of the page is the same
    /// move as reading the tables out of it — one source, not a second copy.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_surface_table_names_only_screens_that_exist()
    {
        using JsonDocument tables = await TablesAsync();

        JsonElement screens = tables.RootElement.GetProperty("screens");

        HashSet<string> drawable =
        [
            .. tables.RootElement.GetProperty("views").EnumerateArray()
                .Select(view => view.GetString()!),
        ];

        Assert.True(
            drawable.Count > 0,
            "no .view elements were found, so this asserted nothing about anything");

        List<string> orphans =
        [
            .. screens.EnumerateObject()
                .Select(entry => entry.Name)
                .Where(name => !drawable.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        Assert.True(
            orphans.Count == 0,
            "SCREEN_SURFACE names screens the console has no view for, so these rules route "
            + "addresses that draw nothing:\n  " + string.Join("\n  ", orphans));
    }

    /// <summary>
    /// The two screens that live in both surfaces stay out of the single-owner table.
    /// </summary>
    /// <remarks>
    /// <b>This is the defect that was actually shipped, asserted from the other side.</b>
    /// Naming Server as the owner of `service` made Studio's only service page unreachable:
    /// `sharing` belongs to Studio, the router forced every `#/service/…` onto Server before the
    /// page set was consulted, and both links to Sharing landed on Capabilities with nothing to
    /// say the page had not opened. The comment in `console.js` records it; this fails if
    /// somebody adds the entry back.
    /// </remarks>
    [Fact]
    public async Task A_screen_that_lives_in_both_surfaces_has_no_single_owner()
    {
        using JsonDocument tables = await TablesAsync();

        JsonElement screens = tables.RootElement.GetProperty("screens");

        foreach (string name in OwnedPerPage)
        {
            Assert.False(
                screens.TryGetProperty(name, out JsonElement owner),
                $"SCREEN_SURFACE names '{name}' as belonging to "
                + $"{(screens.TryGetProperty(name, out owner) ? owner.GetString() : "?")}. It "
                + "lives in both surfaces and its ownership is per page — SERVICE_PAGES and "
                + "LAYER_PAGES. A single owner here is what made Studio's Sharing page "
                + "unreachable.");
        }
    }

    private static string OtherThan(JsonElement surfaces, string surface) =>
        surfaces.EnumerateObject()
            .Select(entry => entry.Name)
            .FirstOrDefault(name => name != surface) ?? "the other surface";
}
