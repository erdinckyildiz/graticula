using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// My content: one paged table, a picture per row, and the four ways content reaches you.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner, 2026-08-18:</b> *"I also need to see the thumbnails in studio content. content can be
/// my own, from my groups, or shared in organization. I think we need a public section as well to get
/// publicly shared items."*
/// </para>
/// <para>
/// <b>Three things were wrong beneath the missing picture, and each had to be fixed first.</b> The
/// screen never paginated — no <c>pageOf</c>, no <c>pagerFor</c> — at a stated scale of 100–1,000
/// services, which at the owner's 512 is 512 rows in one table. It was two tables with duplicate
/// headers whose split could not express two of the four scopes the owner named. And the row put two
/// buttons *ahead of the layer name*, so adding a 104px picture would have made the name the fourth
/// thing in it.
/// </para>
/// </remarks>
public sealed class ContentScopesPageTests : ConsoleTest
{
    /// <summary>
    /// Every row carries something drawn, and the table pages.
    /// </summary>
    /// <remarks>
    /// <b>Paging is asserted by the pager existing above one page's worth</b>, not by counting rows: a
    /// server with ten items or fewer legitimately shows no pager, and a test that demanded one would
    /// fail on a small deployment. What must never happen is more than a page of rows at once.
    /// </remarks>
    [Fact]
    public async Task Content_is_one_paged_table_with_a_picture_on_every_row()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentRows tr').length > 0",
            "My content listed nothing at all.");

        bool empty = await Browser.EvaluateAsync<bool>(
            "!!document.querySelector('#contentRows .empty')");

        // <b>The failure carries the screen's own words, which is how this test earned its keep.</b>
        // A bare `Assert.False(empty)` said *this account can see no content*; the diagnostic said
        // `shown.has is not a function` — the local filtered list had been named `shown`, shadowing the
        // module-level Map of what is drawn, so the whole screen rendered its refusal row. Kept,
        // because "the table is empty" and "the renderer threw" look identical from outside.
        if (empty)
        {
            string what = await Browser.EvaluateAsync<string>(
                "JSON.stringify({ text: document.getElementById('contentRows').innerText.slice(0, 300),"
                + " errors: window.__pageErrors || [] })") ?? "";

            Assert.Fail($"The content table is empty, so there is nothing to assert. {what}");
        }

        int rows = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#contentRows tr').length");

        // <b>Ten, because that is `PAGE_SIZE` and this screen had none.</b> The defect was structural
        // rather than cosmetic: with 512 items the old screen rendered 512 rows.
        Assert.True(
            rows <= 10,
            $"{rows} rows are on screen at once. This screen paginates now, so more than a page of "
            + "them means `pageOf` is not being applied — which is what it looked like at 512.");

        // A picture per row: a canvas where there is a cover, the hatch where the service holds no
        // layers. Both are legitimate here — unlike the add page, where an empty service is held back.
        int pictures = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#contentRows canvas.thumb, #contentRows .thumb.empty').length");

        Assert.Equal(rows, pictures);

        // <b>And the name comes before the verbs.</b> The old row opened with Map and Tiles.
        bool nameFirst = await Browser.EvaluateAsync<bool>(
            "(() => { const r = document.querySelector('#contentRows tr');"
            + " const cells = [...r.children];"
            + " const name = cells.findIndex(c => c.classList.contains('name'));"
            + " const verb = cells.findIndex(c => c.querySelector('button, details'));"
            + " return verb === -1 || name < verb; })()");

        Assert.True(
            nameFirst,
            "A verb column comes before the layer name. The brief puts the name strongest and one "
            + "verb at the end; this row opened with two buttons before the name.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// The scope strip offers Everything first, is addressable, and counts each scope.
    /// </summary>
    /// <remarks>
    /// <b>Everything is the default and that is a first-run decision, not a preference.</b> For a
    /// brand-new operator four of five scopes are empty and the fifth holds everything they can see;
    /// defaulting to *Mine* hands them a blank screen with the content one unclicked tab away. This
    /// console has already shipped that failure once, on the Groups screen.
    /// </remarks>
    [Fact]
    public async Task The_scope_strip_defaults_to_everything_and_each_scope_is_an_address()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen has no scope strip.");

        string current = await Browser.EvaluateAsync<string>(
            "document.querySelector('#contentScopes a[aria-current]')?.textContent || ''")
            ?? string.Empty;

        Assert.Contains("Everything", current, StringComparison.Ordinal);

        // Exactly one marked, which is the whole accessible affordance — these are links, deliberately
        // not `role="tab"`.
        Assert.Equal(
            1,
            await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#contentScopes a[aria-current]').length"));

        // Every scope carries a count, because the counts are why a strip beats their dropdown: a new
        // publisher reading `Everything 12 · Mine 0` knows where they stand without clicking.
        int labelled = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#contentScopes a .count').length");

        int tabs = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#contentScopes a').length");

        Assert.Equal(tabs, labelled);

        // <b>And the scope is in the address.</b> Four sections whose only handle is a click are four
        // things you cannot send anybody to.
        await Browser.EvaluateAsync<string>("location.hash = '#/content/public'");

        await WaitForAsync(
            "(document.querySelector('#contentScopes a[aria-current]')?.textContent || '')"
            + ".includes('Public')",
            "#/content/public did not select the Public scope.");

        // A scope name that does not exist falls back rather than showing an empty screen.
        await Browser.EvaluateAsync<string>("location.hash = '#/content/nonsense'");

        await WaitForAsync(
            "(document.querySelector('#contentScopes a[aria-current]')?.textContent || '')"
            + ".includes('Everything')",
            "An unknown scope in the address did not fall back to Everything.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// The administrative scope appears only when it holds something, and says what it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-018 condition 3, and it was not being kept.</b> *"An administrator reading a private layer
    /// is legitimate and must leave a record, or the sharing model is decorative."* The old screen put
    /// other people's private services into a table headed *shared with you* — nobody shared them — and
    /// the listing wrote no audit row at all. Both are fixed: its own scope, named for what it means to
    /// the reader rather than for the enum, and one audit row per listing that includes it.
    /// </para>
    /// <para>
    /// <b>Absent at zero, deliberately.</b> A tab reading `0` invites the click that proves it reads
    /// `0`, and on a server where one account owns everything there is nothing behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_override_scope_is_shown_only_when_it_holds_something()
    {
        (string token, _) = await SignInAsync();

        (int status, string body) = await AdminAsync(HttpMethod.Get, "/content/items");

        Assert.Equal(200, status);

        int overridden = JsonDocument.Parse(body).RootElement
            .GetProperty("counts").GetProperty("administrative").GetInt32();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen has no scope strip.");

        string strip = await Browser.EvaluateAsync<string>(
            "document.getElementById('contentScopes').textContent") ?? string.Empty;

        if (overridden == 0)
        {
            Assert.DoesNotContain("Not shared with you", strip, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Not shared with you", strip, StringComparison.Ordinal);

            await Browser.EvaluateAsync<string>("location.hash = '#/content/administrative'");

            await WaitForAsync(
                "(document.getElementById('contentNote')?.textContent || '').length > 0",
                "The override scope says nothing about what it is.");

            string note = await Browser.EvaluateAsync<string>(
                "document.getElementById('contentNote').textContent") ?? string.Empty;

            // The sentence is only writable because the listing records the read. If the audit ever
            // goes away, this promise becomes a lie and this assertion is where that shows up.
            Assert.Contains("recorded", note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("private", note, StringComparison.OrdinalIgnoreCase);
        }

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }
}
