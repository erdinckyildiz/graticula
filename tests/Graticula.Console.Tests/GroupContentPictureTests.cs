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
    /// An item shared into a group shows a picture the server drew, at its displayed size.
    /// </summary>
    /// <remarks>
    /// <b>`.thumb.empty` would be a pass here and must not be.</b> The hatched placeholder means
    /// *this service has nothing to draw*; a Content tab that never asked for the cover renders
    /// exactly the same mark, which is the ambiguity D-80's own note called out. So the assertion
    /// is an `img.thumb` that actually loaded — `naturalWidth` above zero, which a broken or
    /// refused picture does not have — and the count of hatched cells is zero.
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
                "document.querySelectorAll('#groupItems img.thumb').length");

            Assert.True(
                drawn > 0,
                "No row on the Content tab carries a picture, so the tab is still showing names "
                + "where it should show pictures. That is D-80.");

            // <b>And it arrived — D-58.</b> The console fetches each picture and only then
            // sets a source, so an element on its own proves nothing; `naturalWidth` above zero
            // is the browser saying it decoded an image. A row whose picture was refused is not
            // an `img` at all by then — it has been replaced by the hatch, which the assertion
            // above already forbids on this tab.
            await WaitForAsync(
                "[...document.querySelectorAll('#groupItems img.thumb')]"
                + ".every(i => i.naturalWidth > 0)",
                "The Content tab's pictures never decoded. The console asks "
                + "/admin/thumbnail for each one; if that route is not answering, every row "
                + "here is an empty element.");

            int[] shape = await Browser.EvaluateAsync<int[]>(
                "(() => { const i = document.querySelector('#groupItems img.thumb');"
                + " return [i.naturalWidth, i.naturalHeight, i.clientWidth, i.clientHeight]; })()")
                ?? [];

            Assert.Equal(4, shape.Length);

            Assert.True(
                shape[0] > 0 && shape[1] > 0,
                "The row's picture is an <img> that decoded nothing, so the thumbnail request "
                + "failed and every row shows a broken-image glyph. D-58 swapped the sampled "
                + "canvas for a server render; this is what it looks like when the render is not "
                + "reachable.");

            // <b>Not squashed.</b> One picture serves a 104x70 slot and a 168x112 one, so the
            // rendered aspect and the displayed aspect must agree to within a rounding error or
            // every map in this console is stretched.
            Assert.True(
                Math.Abs(((double)shape[0] / shape[1]) - ((double)shape[2] / shape[3])) < 0.06,
                $"The picture is {shape[0]}x{shape[1]} shown at {shape[2]}x{shape[3]}, which is a "
                + "different shape. `object-fit: cover` should be hiding this; if it is not, the "
                + "rendered size and the slot have drifted apart.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }
}
