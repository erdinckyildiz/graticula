using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Ten rows a page on Server's listings, and the control strip that says which ten.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner 2026-08-18:</b> *"server tarafında görüntülenen max item sayısı 10 olmalı. 10 üstü
/// paging olacak."* Before this every Server listing rendered its whole result, so a deployment of
/// the scale CLAUDE.md §7 targets — 100 to 1,000 services — drew a thousand rows into one table.
/// </para>
/// <para>
/// <b>Asserted by counting rows in a browser, because that is the only place the count exists.</b>
/// The slice happens in the page's own script; nothing on the server knows about it, so there is no
/// API response that could be checked instead.
/// </para>
/// <para>
/// <b>The suite provisions its own fixture.</b> This server's largest folder held exactly ten
/// services, so the pager correctly did not appear — *absent rather than disabled when there is one
/// page* is the design, and a test that read that as a failure would be testing the fixture. Ten is
/// also the worst possible size to be handed: one either side and the boundary is visible.
/// </para>
/// </remarks>
public sealed class ListPagingTests : ConsoleTest
{
    /// <summary>Marks the services this suite creates, so a failed run leaves something named.</summary>
    private const string Prefix = "zz_paging_probe_";

    /// <summary>The names in the services table, in the order shown.</summary>
    private const string NamesExpression =
        "[...document.querySelectorAll('#services tr .name')].map(c => c.textContent.trim())"
        + ".join('|')";

    /// <summary>The services list shows ten and says which ten.</summary>
    [Fact]
    public async Task The_services_list_shows_ten_rows_and_a_pager_that_names_them()
    {
        (string token, _) = await SignInAsync();
        (string folder, int total) = await WithMoreThanOnePageAsync();

        try
        {
            await OpenFolderHoldingAsync("#servicesPager [data-page-to]", token);

            int rows = await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#services tr').length");

            Assert.Equal(10, rows);

            // <b>The strip says which rows, not which page.</b> *1–10 of 11* answers the question
            // somebody has when a list is paged; *Page 1* does not.
            string pager = await Browser.EvaluateAsync<string>(
                "(document.getElementById('servicesPager')?.textContent || '')"
                + ".replace(/[ \\n\\r\\t]+/g, ' ').trim()") ?? string.Empty;

            Assert.Contains(
                total.ToString(CultureInfo.InvariantCulture), pager, StringComparison.Ordinal);

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await ReleaseAsync(folder);
        }
    }

    /// <summary>Turning the page shows rows the first page did not.</summary>
    /// <remarks>
    /// <b>The overlap assertion is the one with teeth.</b> A pager that draws a second page of the
    /// same ten rows looks correct in a screenshot, and this project shipped exactly that class of
    /// defect — the first page of a paged query was unordered, so pages one and two overlapped
    /// (D-22). So: collect the names, turn the page, and require that none came back.
    /// </remarks>
    [Fact]
    public async Task Turning_the_page_shows_rows_the_first_page_did_not()
    {
        (string token, _) = await SignInAsync();
        (string folder, _) = await WithMoreThanOnePageAsync();

        try
        {
            await OpenFolderHoldingAsync("#servicesPager [data-page-to]", token);

            string first = await Browser.EvaluateAsync<string>(NamesExpression) ?? string.Empty;
            Assert.NotEmpty(first);

            await ClickAsync("#servicesPager [data-page-to='1']");

            await WaitForAsync(
                $"({NamesExpression}) !== {JsonSerializer.Serialize(first)}",
                "The list did not change after the page was turned, so the pager draws the same "
                + "rows twice — which is how a paged list hides half a catalogue while looking "
                + "correct.");

            string second = await Browser.EvaluateAsync<string>(NamesExpression) ?? string.Empty;

            string[] one = first.Split('|', StringSplitOptions.RemoveEmptyEntries);
            string[] two = second.Split('|', StringSplitOptions.RemoveEmptyEntries);

            Assert.NotEmpty(two);

            foreach (string name in two)
            {
                Assert.DoesNotContain(name, one);
            }

            await ClickAsync("#servicesPager [data-page-to='0']");

            await WaitForAsync(
                $"({NamesExpression}) === {JsonSerializer.Serialize(first)}",
                "Going back to the first page did not restore the rows it had, so the page index "
                + "and the rows disagree.");

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await ReleaseAsync(folder);
        }
    }

    /// <summary>Filtering sends the list back to page one.</summary>
    /// <remarks>
    /// <b>The bug every list of this shape has.</b> Stand on page two, type a filter that matches
    /// three rows, and the table is empty beside a count that says three — and the reader blames
    /// the filter rather than the pager. Cheap to prevent and invisible until somebody hits it.
    /// </remarks>
    [Fact]
    public async Task Filtering_returns_to_the_first_page()
    {
        (string token, _) = await SignInAsync();
        (string folder, _) = await WithMoreThanOnePageAsync();

        try
        {
            await OpenFolderHoldingAsync("#servicesPager [data-page-to]", token);

            await ClickAsync("#servicesPager [data-page-to='1']");

            await WaitForAsync(
                $"({NamesExpression}).length > 0",
                "The second page drew no rows at all.");

            // A filter matching this suite's own services and nothing else, so the narrowed list
            // is shorter than a page and page two would be empty if the index survived.
            await Browser.EvaluateAsync<object>(
                "(() => { const f = document.getElementById('serviceFilter');"
                + $" f.value = {JsonSerializer.Serialize(Prefix)};"
                + " f.dispatchEvent(new Event('input', { bubbles: true })); })()");

            // <b>`td.empty`, not `.empty` — and the difference cost a diagnosis.</b> The empty-list
            // message is a `<td class="empty">` spanning the table; a *row* whose service has no cover
            // renders a `<div class="thumb empty">` placeholder. `.empty` matches both, so this
            // condition could never be true when the filtered list held only services without covers —
            // which is what `zz_paging_probe_*` are, since they have no layers by construction. It
            // passed for as long as the first filtered row happened to belong to a service with a
            // cover, which is luck rather than a test.
            await WaitForAsync(
                "document.querySelectorAll('#services tr').length > 0"
                + " && !document.querySelector('#services td.empty')",
                "Filtering left the table empty while there were matches, which is the page index "
                + "surviving a filter that shortened the list.");

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await ReleaseAsync(folder);
        }
    }

    /// <summary>
    /// A folder holding more than one page, topped up if the server has fewer.
    /// </summary>
    /// <remarks>
    /// <b>Empty services, removed afterwards.</b> <c>POST /admin/featureservices</c> makes a
    /// container with no layers and no data, so nothing in the datastore is touched and the
    /// clean-up cannot lose anybody's features.
    /// </remarks>
    /// <returns>The folder to open, and how many services it holds.</returns>
    private async Task<(string Folder, int Total)> WithMoreThanOnePageAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/featureservices");

        Assert.True(status == 200, $"GET /admin/featureservices returned {status}: {body}");

        using JsonDocument listing = JsonDocument.Parse(body);

        Dictionary<string, int> perFolder = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement service in listing.RootElement.GetProperty("services").EnumerateArray())
        {
            string folder = service.TryGetProperty("folder", out JsonElement f)
                && f.ValueKind == JsonValueKind.String
                ? f.GetString()!
                : string.Empty;

            perFolder[folder] = perFolder.GetValueOrDefault(folder) + 1;
        }

        Assert.NotEmpty(perFolder);

        KeyValuePair<string, int> biggest = perFolder.OrderByDescending(e => e.Value).First();
        string at = biggest.Key;

        /*
          <b>Eleven rather than ten.</b> The boundary is crossed rather than met, because at
          exactly ten there is nothing to page and the pager is right to be absent.

          <b>And at least one, which is the fix for D-143.</b> This was `Math.Max(0, …)`, so a
          folder that already held eleven services got no probe at all — and then
          `Filtering_returns_to_the_first_page`, whose filter is this suite's own prefix and
          nothing else, narrowed the list to nothing and reported that filtering had left the
          table empty *while there were matches*. There were none. The table was right.

          <b>It failed about one run in six because the count moved.</b> Measured 2026-08-23:
          `hosted` held fourteen services, so `11 - 14` was negative and the floor of zero
          applied; other suites in the run publish and remove services in the same folder, so
          whether it sat above or below eleven depended on what else had run. The register row
          guessed at `CatalogFallback` and was wrong — the listing was accurate throughout.

          <b>The floor of one is what makes the filter meaningful rather than the count.</b>
          Eleven is what the pager needs; one is what a filter for *this suite's services*
          needs, and only the second was ever in doubt.
        */
        int wanted = Math.Max(1, 11 - biggest.Value);

        for (int i = 0; i < wanted; i++)
        {
            (int made, string why) = await AdminAsync(
                HttpMethod.Post,
                "/admin/featureservices",
                JsonSerializer.Serialize(new
                {
                    name = Prefix + i.ToString(CultureInfo.InvariantCulture),
                    folder = at.Length == 0 ? null : at,
                    description = "Created by ListPagingTests; removed when the test finishes.",
                }));

            Assert.True(
                made is 200 or 201,
                $"Could not create the service this test needs: {made} {why}");
        }

        return (at, biggest.Value + wanted);
    }

    /// <summary>Removes what <see cref="WithMoreThanOnePageAsync"/> created.</summary>
    /// <remarks>
    /// <b>Every name, not only the ones this run made.</b> A previous run that died between
    /// creating and deleting left services behind, and a suite that tidies only its own successes
    /// grows a folder over time.
    /// </remarks>
    private async Task ReleaseAsync(string folder)
    {
        for (int i = 0; i < 11; i++)
        {
            string name = Uri.EscapeDataString(Prefix + i.ToString(CultureInfo.InvariantCulture));

            await AdminAsync(
                HttpMethod.Delete,
                $"/admin/featureservices/{name}"
                + (folder.Length == 0 ? string.Empty : $"?folder={Uri.EscapeDataString(folder)}"));
        }
    }
}
