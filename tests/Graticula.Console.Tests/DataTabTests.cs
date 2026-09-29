using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The item page's Data table pages, orders and says how many rows there are.
/// </summary>
/// <remarks>
/// <b>2026-09-30, from the design review's comparison with Portal's Data tab.</b> The table was the first
/// twenty rows and first twelve columns, with no total, no next page and no order. The layer used is the
/// fixture's large one, which holds more than one page by construction.
/// </remarks>
public sealed class DataTabTests : ConsoleTest
{
    [Fact]
    public async Task The_table_says_how_many_rows_there_are_and_the_next_page_shows_others()
    {
        (string token, _) = await SignInAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_LARGE") ?? "hosted/ci_many";

        await OpenAsync($"/studio/#/service/{service}?tab=data&layer=0", token);

        await WaitForAsync(
            "/^Showing 1–50 of /.test((document.getElementById('dataCount') || {}).textContent || '')",
            "The Data table did not say it shows 1–50 of a total. It showed a fixed twenty rows with no total until 2026-09-30.");

        string firstPage = await Browser.EvaluateAsync<string>(
            "document.querySelector('#dataRows tbody tr').textContent") ?? "";

        await ClickAsync("[data-data-page=\"1\"]");

        await WaitForAsync(
            "/^Showing 51–/.test((document.getElementById('dataCount') || {}).textContent || '')",
            "Next did not move the table to rows 51 onward.");

        string secondPage = await Browser.EvaluateAsync<string>(
            "document.querySelector('#dataRows tbody tr').textContent") ?? "";

        Assert.NotEqual(firstPage, secondPage);

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_column_name_orders_the_table_by_that_column()
    {
        (string token, _) = await SignInAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_LARGE") ?? "hosted/ci_many";

        await OpenAsync($"/studio/#/service/{service}?tab=data&layer=0", token);

        await WaitForAsync("!!document.querySelector('[data-data-sort]')", "The Data table's columns cannot be ordered by.");

        // The object id, pressed twice: descending, so the first row holds the largest id.
        string field = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('[data-data-sort]')].map(b => b.dataset.dataSort)"
            + ".find(n => /^objectid$/i.test(n)) || document.querySelector('[data-data-sort]').dataset.dataSort") ?? "";

        await ClickAsync($"[data-data-sort=\"{field}\"]");
        await WaitForAsync(
            $"document.querySelector('[data-data-sort=\"{field}\"]')?.closest('th')?.getAttribute('aria-sort') === 'ascending'",
            "Ordering by a column did not mark it ascending.");

        await ClickAsync($"[data-data-sort=\"{field}\"]");
        await WaitForAsync(
            $"document.querySelector('[data-data-sort=\"{field}\"]')?.closest('th')?.getAttribute('aria-sort') === 'descending'",
            "Pressing the same column again did not reverse the order.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
