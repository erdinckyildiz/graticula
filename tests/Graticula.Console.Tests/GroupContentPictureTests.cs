using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A group's Content tab draws its items rather than listing their names.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-80](../../docs/architecture-debt.md): a group's items cannot be drawn, so the tab that
/// should show them shows their names.</b> The row's own account of the repair was two
/// subselects on `ItemsAsync` and a `drawPreview` per row. Both exist now — what did not exist
/// was anything asserting it, and the group fixture the other tests use is empty, so every
/// assertion about Content has been about its empty state.
/// </para>
/// <para>
/// <b>Which is exactly the shape this repository has been caught by three times: a control that
/// exists in the markup and is never seen.</b> So this shares a real service into a real group
/// and asks the browser what the row holds.
/// </para>
/// </remarks>
public sealed class GroupContentPictureTests : ConsoleTest
{
    private const string Probe = "zz_group_picture_probe";

    /// <summary>
    /// An item shared into a group shows a canvas with something to draw, at its displayed size.
    /// </summary>
    /// <remarks>
    /// <b>`.thumb.empty` would be a pass here and must not be.</b> The hatched placeholder means
    /// *this service has nothing to draw*; a Content tab that never asked for the cover renders
    /// exactly the same mark, which is the ambiguity D-80's own note called out. So the assertion
    /// is a `canvas` carrying a `data-preview`, and the count of hatched cells is zero.
    /// </remarks>
    [Fact]
    public async Task An_item_shared_into_a_group_is_drawn_on_its_content_tab()
    {
        (string token, _) = await SignInAsync();

        (int listed, string catalogue) = await AdminAsync(HttpMethod.Get, "/admin/featureservices");

        Assert.True(listed is 200, $"the catalogue answered {listed}: {catalogue}");

        // A service with at least one layer, because a service with none has no cover by
        // definition and would make this test assert the placeholder it exists to forbid.
        string? drawable = null;

        foreach (JsonElement service in JsonDocument.Parse(catalogue)
                     .RootElement.GetProperty("services").EnumerateArray())
        {
            string name = service.GetProperty("name").GetString() ?? string.Empty;

            if (name.StartsWith("zz_", StringComparison.Ordinal)
                || name.StartsWith("corpus_", StringComparison.Ordinal))
            {
                continue;
            }

            if (service.TryGetProperty("cover", out JsonElement cover)
                && cover.ValueKind is not JsonValueKind.Null)
            {
                drawable = service.GetProperty("qualified").GetString();

                break;
            }
        }

        Assert.False(
            drawable is null,
            "No service in this catalogue has a cover layer, so there is nothing a Content tab "
            + "could draw and nothing to assert. Publish one first.");

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Group picture probe" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            // <b>The bare name in the path, the folder in the query.</b> The route segment is one
            // segment: a qualified name in it answers *'hosted%2Fx' is not something this server
            // has*, which the console learnt in its own commit and this test would otherwise have
            // to learn again.
            int cut = drawable!.LastIndexOf('/');

            string folderOf = cut < 0 ? string.Empty : drawable[..cut];
            string bare = cut < 0 ? drawable : drawable[(cut + 1)..];

            (int shared, string said) = await AdminAsync(
                HttpMethod.Put,
                $"/admin/groups/{Probe}/items/{Uri.EscapeDataString(bare)}"
                + $"?folder={Uri.EscapeDataString(folderOf)}");

            Assert.True(shared is 200 or 201 or 204, $"sharing answered {shared}: {said}");

            await OpenAsync($"/studio/#/group/{Probe}/content", token);

            await WaitForAsync(
                "document.querySelectorAll('#groupItems tr').length > 0",
                "The Content tab listed nothing at all.");

            bool empty = await Browser.EvaluateAsync<bool>(
                "!!document.querySelector('#groupItems td.empty')");

            Assert.False(
                empty,
                "The Content tab says the group is empty, and a service was just shared into it.");

            int hatched = await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#groupItems .thumb.empty').length");

            Assert.True(
                hatched == 0,
                $"{hatched} row(s) show the hatched placeholder. On this tab that mark cannot be "
                + "read: it means *this service has nothing to draw* on the Services screen, and "
                + "*this screen did not ask* here. D-80 is that ambiguity.");

            int drawn = await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#groupItems canvas.thumb[data-preview]').length");

            Assert.True(
                drawn > 0,
                "No row on the Content tab carries a canvas to draw into, so the tab is still "
                + "showing names where it should show pictures. That is D-80.");

            // The same squashing check the add page carries: drawn at the size it is displayed at,
            // or every picture in this console is stretched by a few per cent for ever.
            int[] shape = await Browser.EvaluateAsync<int[]>(
                "(() => { const c = document.querySelector('#groupItems canvas.thumb');"
                + " return [c.width, c.height, c.clientWidth, c.clientHeight]; })()")
                ?? [];

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
}
