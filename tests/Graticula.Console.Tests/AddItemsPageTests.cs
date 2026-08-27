using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Choosing what to put in a group: a page with pictures, not a list of names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the owner rejected the first version on sight, 2026-08-18:</b> *"going with name
/// only is not feasible. I need to see thumbnail etc for items."* It was a
/// <c>&lt;select size="8"&gt;</c> of <c>folder/service</c> strings, which on their server would be 512
/// options long. They are right at any scale and unarguable at theirs.
/// </para>
/// <para>
/// <b>What this class asserts is the part a screenshot cannot.</b> That the rows carry a drawable
/// picture rather than a hatched placeholder; that a service already in the group is shown, ticked and
/// **disabled** rather than quietly missing; that the select-all names its own number instead of being
/// a tri-state box whose scope has to be guessed; and that the footer counts the selection rather than
/// the page. Every one of those is a decision that would pass a visual review either way.
/// </para>
/// </remarks>
public sealed class AddItemsPageTests : ConsoleTest
{
    private const string Probe = "zz_add_page_probe";

    /// <summary>
    /// The page offers services with a picture each, and the count says how many you own.
    /// </summary>
    [Fact]
    public async Task The_add_page_offers_your_services_with_something_drawn_for_each()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Add page probe" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync($"/studio/#/group/{Probe}/add", token);

            await WaitForAsync(
                "document.querySelectorAll('#addRows tr').length > 0",
                "The add page listed nothing at all.");

            // <b>Not an empty state.</b> If the suite's server has no content of the signed-in
            // account's own, this test is about nothing and should say so rather than pass.
            // <b>`td.empty`, not `.empty`.</b> The empty-list message is a `<td>` spanning the
            // table; a row whose item has no cover renders a `<div class="thumb empty">`
            // placeholder, so the looser selector is true whenever *any* row lacks a cover —
            // which would make this read a full page as an empty one. Found 2026-08-19 in
            // `ListPagingTests`, where the same collision made a passing assertion
            // unsatisfiable instead.
            bool empty = await Browser.EvaluateAsync<bool>(
                "!!document.querySelector('#addRows td.empty')");

            Assert.False(
                empty,
                "The signed-in account owns no services, so there is nothing for this page to offer "
                + "and nothing to assert. Publish one first.");

            // Every offered row has a checkbox and something in the picture column.
            int rows = await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#addRows tr').length");

            int ticks = await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#addRows input[type=checkbox][data-add]').length");

            Assert.Equal(rows, ticks);

            // <b>Drawn, not hatched.</b> An empty service is held back from this page precisely so the
            // picture column has no hatched state — `.thumb.empty` here would mean *this screen did not
            // ask* where on the Services screen it means *this service has nothing to draw*.
            int hatched = await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#addRows .thumb.empty').length");

            Assert.Equal(0, hatched);

            int canvases = await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#addRows canvas.thumb[data-preview]').length");

            Assert.Equal(rows, canvases);

            // <b>And the canvas is drawn at the size it is displayed at.</b> It was 104×74 into a
            // 104×70 box for as long as previews have existed — every picture on the Services screen
            // squashed 5.4% vertically, which is the kind of thing nobody sees and everybody feels.
            int[] shape = await Browser.EvaluateAsync<int[]>(
                "(() => { const c = document.querySelector('#addRows canvas.thumb');"
                + " return [c.width, c.height, c.clientWidth, c.clientHeight]; })()")
                ?? Array.Empty<int>();

            Assert.Equal(4, shape.Length);
            Assert.Equal(shape[0], shape[2]);
            Assert.Equal(shape[1], shape[3]);

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>
    /// A service already in the group is shown, ticked, disabled and said to be there.
    /// </summary>
    /// <remarks>
    /// <b>Shown rather than filtered out, and that is the decision.</b> Somebody hunting for a service
    /// they shared last week needs to be told it is already there — finding it missing invites sharing
    /// it twice, or concluding the search is broken. The old picker offered a service that was already
    /// in the group, which is the live defect a design review found.
    /// </remarks>
    [Fact]
    public async Task A_service_already_in_the_group_is_ticked_disabled_and_labelled()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Add page probe" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            // Something of the signed-in account's own, taken from the listing the page itself reads.
            (int status, string body) = await AdminAsync(HttpMethod.Get, "/content/items");

            Assert.Equal(200, status);

            JsonElement[] mine = JsonDocument.Parse(body).RootElement
                .GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("scope").GetString() == "mine"
                            && !i.GetProperty("empty").GetBoolean())
                .ToArray();

            Assert.NotEmpty(mine);

            string qualified = mine[0].GetProperty("name").GetString()!;
            int cut = qualified.LastIndexOf('/');
            string folder = cut < 0 ? string.Empty : qualified[..cut];
            string bare = cut < 0 ? qualified : qualified[(cut + 1)..];

            (int shared, string sharedWhy) = await AdminAsync(
                HttpMethod.Put,
                $"/admin/groups/{Probe}/items/{Uri.EscapeDataString(bare)}"
                + $"?folder={Uri.EscapeDataString(folder)}",
                null);

            Assert.True(shared is 200 or 201, $"{shared} {sharedWhy}");

            await OpenAsync($"/studio/#/group/{Probe}/add", token);

            string selector = $"#addRows input[data-add=\"{qualified}\"]";

            await WaitForAsync(
                $"!!document.querySelector('{selector}')",
                "The service already in the group is not listed on the add page. It was filtered out, "
                + "which sends somebody hunting for it to share it a second time.");

            bool ticked = await Browser.EvaluateAsync<bool>(
                $"document.querySelector('{selector}').checked");

            bool locked = await Browser.EvaluateAsync<bool>(
                $"document.querySelector('{selector}').disabled");

            Assert.True(ticked, "The already-shared service is not ticked.");
            Assert.True(locked, "The already-shared service can be ticked again, which is not an act.");

            // And it says so in words, not only by being disabled.
            string row = await Browser.EvaluateAsync<string>(
                $"document.querySelector('{selector}').closest('tr').innerText") ?? string.Empty;

            Assert.Contains(Probe, row, StringComparison.OrdinalIgnoreCase);

            // <b>Excluded from Select all, which is the half a disabled tick does not cover.</b> A
            // select-all that counted it would offer to add something that is already there.
            //
            // <b>Asserted against the whole offered set, not against this page.</b> The label counts
            // the filtered set across pages — that is what makes its number worth printing — so
            // comparing it to the enabled boxes on page one is a test that fails on a working screen
            // as soon as there are more than ten services. It did, at eleven.
            string label = await Browser.EvaluateAsync<string>(
                "document.getElementById('addAllLabel').textContent") ?? string.Empty;

            Assert.Equal($"Select all {mine.Length - 1}", label.Trim());

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>
    /// Ticking a row makes the footer say what it will do, by number.
    /// </summary>
    /// <remarks>
    /// <b>The footer counts the selection, not the page.</b> The selection is what the button acts on,
    /// so a count of the page would be a promise the press does not keep — and this console has twice
    /// shipped a control whose scope had to be inferred.
    /// </remarks>
    [Fact]
    public async Task The_footer_counts_what_the_button_will_add()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Add page probe" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync($"/studio/#/group/{Probe}/add", token);

            await WaitForAsync(
                "document.querySelectorAll('#addRows input[data-add]:not([disabled])').length > 0",
                "The add page offers nothing to tick.");

            // Disabled until something is chosen, because a button that does nothing is worse than one
            // that is plainly not ready.
            Assert.True(
                await Browser.EvaluateAsync<bool>("document.getElementById('addCommit').disabled"),
                "Add items is enabled with nothing selected.");

            await ClickAsync("#addRows input[data-add]:not([disabled])");

            await WaitForAsync(
                "!document.getElementById('addCommit').disabled",
                "Ticking a service did not enable Add items.");

            string label = await Browser.EvaluateAsync<string>(
                "document.getElementById('addCommit').textContent") ?? string.Empty;

            Assert.Contains("1 item", label, StringComparison.OrdinalIgnoreCase);

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }
}
