using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// My content as a Portal user reads it: searchable from the start, newest first, maps among the rest.
/// </summary>
/// <remarks>
/// <para>
/// <b>From the design review of 2026-09-30, which compared Studio with Portal's Content page.</b> The
/// search box appeared only once a scope held more than one page, so a new user never saw it; the rows
/// came in the order of the service path, case-sensitive, so a layer just uploaded landed last; and saved
/// maps were a second table at the foot of the page, further away with every service published.
/// </para>
/// <para>
/// <b>Each assertion is about the mechanism rather than the fixture's contents</b>: the order is read off
/// the rows' own dates, and the map is one this test saves and removes.
/// </para>
/// </remarks>
public sealed class ContentListTests : ConsoleTest
{
    /// <summary>The search box is there on a list of one page, and the order is newest first.</summary>
    [Fact]
    public async Task The_search_is_always_there_and_the_newest_item_is_first()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentRows tr').length > 0 && !document.querySelector('#contentRows td.empty')",
            "My content listed nothing, so there is no order to check.");

        bool hidden = await Browser.EvaluateAsync<bool>("document.getElementById('contentFilter').hidden");

        Assert.False(hidden, "The search box is hidden. It appeared only past one page, which is exactly when a new user is not looking for it.");

        string sort = await Browser.EvaluateAsync<string>("document.getElementById('contentSort').value") ?? "";

        Assert.Equal("modified", sort);

        // The rows' own dates, as the server sent them, in the order drawn.
        string[] dates = await Browser.EvaluateAsync<string[]>(
            "(() => { const all = [...(contentAnswer.answer.items || []), ...((contentAnswer.maps.webMaps || []).map(mapAsItem))];"
            + " return [...document.querySelectorAll('#contentRows td.name a')].map(a => {"
            + "   const href = a.getAttribute('href') || '';"
            + "   const hit = all.find(i => i.map ? href.includes(i.map.id) : href === '#/service/' + i.name.split('/').map(encodeURIComponent).join('/'));"
            + "   return hit ? String(hit.updated || '') : '?'; }); })()")
            ?? [];

        Assert.DoesNotContain("?", dates);

        for (int i = 1; i < dates.Length; i++)
        {
            Assert.True(
                string.CompareOrdinal(dates[i - 1], dates[i]) >= 0,
                $"Row {i} was modified at {dates[i - 1]} and row {i + 1} at {dates[i]}: the list is not newest first.");
        }

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>A saved map is a row of the list, found by its title and narrowed to by its type.</summary>
    [Fact]
    public async Task A_saved_map_is_a_row_of_the_list_and_the_type_filter_finds_it()
    {
        (string token, _) = await SignInAsync();

        string title = "zz content list map " + Guid.NewGuid().ToString("N")[..6];

        string document = JsonSerializer.Serialize(new
        {
            title,
            snippet = "made by ContentListTests",
            sharing = "private",
            document = new
            {
                operationalLayers = Array.Empty<object>(),
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        });

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);

        Assert.True(status == 201, $"Saving the map through the API answered {status}: {body}");

        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync("/studio/#/content", token);

            await WaitForAsync(
                $"[...document.querySelectorAll('#contentRows td.name a')].some(a => a.textContent.trim() === {JsonSerializer.Serialize(title)})",
                "The saved map is not a row of My content. Maps were a second table at the foot of the page until 2026-09-30.");

            bool second = await Browser.EvaluateAsync<bool>("!!document.getElementById('mapRows')");

            Assert.False(second, "A second maps table is still drawn below the content list.");

            await Browser.EvaluateAsync<bool>(
                "(() => { const s = document.getElementById('contentType'); s.value = 'Web map';"
                + " s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

            await WaitForAsync(
                "[...document.querySelectorAll('#contentRows .rowmeta')].every(m => /^Web map/.test(m.textContent.trim()))",
                "With the type set to Web map, rows of another type are still listed.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    /// <summary>An item's description is written from its page, and the page shows what was written.</summary>
    [Fact]
    public async Task A_description_is_written_from_the_item_page()
    {
        (string token, _) = await SignInAsync();

        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings";

        await OpenAsync($"/studio/#/service/{service}", token);

        await WaitForAsync(
            "!!document.querySelector('#serviceDescription [data-describe]')",
            "The item page offers no way to write a description. It asked for one and offered nothing until 2026-09-30.");

        await ClickAsync("#serviceDescription [data-describe]");

        await WaitForAsync("!!document.getElementById('describeText')", "The description did not become a box to type in.");

        await Browser.EvaluateAsync<bool>(
            "(document.getElementById('describeText').value = 'Written by ContentListTests.', true)");

        await ClickAsync("#serviceDescription [data-describe-save]");

        string[] writes = await WritesAsync();

        Assert.Contains(writes, w => w.StartsWith("PUT ", StringComparison.Ordinal)
            && w.Contains("/description", StringComparison.Ordinal));

        await WaitForAsync(
            "/Written by ContentListTests/.test(document.getElementById('serviceDescription').textContent)",
            "After Save the page does not show the description that was saved.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>A search on Server's services reads every folder, not only the open one.</summary>
    [Fact]
    public async Task A_services_search_finds_a_service_in_another_folder()
    {
        (string token, _) = await SignInAsync();

        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings";
        string bare = service[(service.LastIndexOf('/') + 1)..];

        // The root, by name — whatever the first landing chose.
        await OpenAsync("/server/#/services/", token);

        await WaitForAsync("!!document.getElementById('serviceFilter')", "The services screen did not open.");

        await FilterAsync("serviceFilter", bare);

        await WaitForAsync(
            $"[...document.querySelectorAll('#services tr')].some(r => r.textContent.includes({JsonSerializer.Serialize(bare)}))",
            $"Searching the services for {bare} from the root found nothing; the search read only the open folder.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
