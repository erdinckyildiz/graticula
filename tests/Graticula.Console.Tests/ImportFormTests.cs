using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The import form offers what the server accepts, and says what it needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because the console denied a feature this server has shipped, measured and tested.</b>
/// The form's own copy read *"GeoJSON only — a shapefile is a ZIP and this server does not open
/// archives (Q-98)"*, and its file input carried <c>accept=".json,.geojson,application/geo+json"</c>
/// — so a shapefile could not even be selected. Both were true when written and both were made false
/// by [ADR-024](../../docs/adr/ADR-024-shapefile-import.md), which answered Q-98, opened the archive
/// under stated bounds, and shipped an 860-line reader verified against a corpus this project did not
/// write.
/// </para>
/// <para>
/// <b>The same shape as D-83.</b> There, the server took a fourth sharing scope and the console's
/// <c>SCOPES</c> had three, so the one instruction the group page gave could not be followed. Here the
/// server takes an archive and the console's <c>accept</c> list does not, so the feature could not be
/// reached at all. In both cases the capability was complete and the product did not have it — which
/// is worse than a missing feature, because the copy actively tells the operator to stop trying.
/// </para>
/// <para>
/// <b>These tests assert the form's contract, not its wording.</b> What must hold is that a
/// <c>.zip</c> is selectable, that the coordinate system can be given, and that the copy no longer
/// says archives are refused. The sentences may be rewritten.
/// </para>
/// </remarks>
public sealed class ImportFormTests : ConsoleTest
{
    /// <summary>
    /// A zipped shapefile can be chosen, and the form takes the code the server requires.
    /// </summary>
    [Fact]
    public async Task The_import_form_accepts_an_archive_and_asks_for_the_coordinate_system()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen never rendered, so its page action is not there to press.");

        // The drawer is opened by the surface's own action, which is how an operator reaches it.
        await ClickAsync("#newLayer");

        await WaitForAsync(
            "document.getElementById('iFile')?.offsetParent !== null",
            "Create did not open, or its import form is not visible. `offsetParent`, because this "
            + "console has shipped a control that existed and could not be seen three times.");

        // <b>The archive is selectable.</b> A browser's file picker filters on this attribute, so a
        // `.zip` missing from it is a shapefile the operator cannot choose however good the reader is.
        string accepts = await Browser.EvaluateAsync<string>(
            "document.getElementById('iFile').getAttribute('accept') || ''") ?? string.Empty;

        Assert.Contains(".zip", accepts, StringComparison.Ordinal);
        Assert.Contains(".geojson", accepts, StringComparison.Ordinal);

        // <b>And the code the server requires can be given.</b> Without this field the form can only
        // ever import GeoJSON — a shapefile is refused for a missing `srid` and there is nowhere to
        // put one, which is the loop the old form left an operator in.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('iSrid')?.offsetParent !== null"),
            "There is no field for the coordinate system, so a shapefile can be selected and never "
            + "accepted — the server requires `srid` and refuses to infer it from the .prj.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// The form no longer says this server refuses archives.
    /// </summary>
    /// <remarks>
    /// <b>Asserted as an absence, which is unusual and is the point.</b> The claim was not merely
    /// stale — it cited Q-98 as the reason, and Q-98 is answered. A reader who believes the copy
    /// converts their data elsewhere before uploading it, which is the friction ADR-024 §1 says this
    /// product exists to remove. So what must never come back is the *claim*, not any one phrasing.
    /// </remarks>
    [Fact]
    public async Task The_import_form_no_longer_claims_archives_are_refused()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen never rendered.");

        await ClickAsync("#newLayer");

        await WaitForAsync(
            "document.getElementById('importForm')?.offsetParent !== null",
            "The import form never became visible.");

        string copy = await Browser.EvaluateAsync<string>(
            "document.getElementById('importForm').closest('.group').innerText") ?? string.Empty;

        foreach (string denial in new[]
        {
            "GeoJSON only",
            "does not open archives",
            "Q-98",
        })
        {
            Assert.DoesNotContain(denial, copy, StringComparison.OrdinalIgnoreCase);
        }

        // And it names what it does take, because an operator holding a `.shp` needs to be told.
        Assert.Contains("shapefile", copy, StringComparison.OrdinalIgnoreCase);

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// Choosing an archive and giving a code sends both, and leaving the code empty sends neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted on what the form sends, which is where the defect was.</b> The endpoint already had
    /// conformance coverage and passed throughout; what had never been exercised is the console's own
    /// <c>FormData</c>. A form that posts to the right address without the field the server requires
    /// looks identical from outside — so the write trap now records the field names, and this is the
    /// first test to read them.
    /// </para>
    /// <para>
    /// <b>Both directions, because only sending it is half a contract.</b> A shapefile needs
    /// <c>srid</c>; GeoJSON is WGS 84 by its own specification and does not. Sending an empty string
    /// would be a value rather than an absence, and the server would have to decide what an empty
    /// coordinate system means — which is a question it should never be asked.
    /// </para>
    /// <para>
    /// <b>The bytes are arbitrary and the request never reaches the server.</b> The harness stubs
    /// every write, so this asserts the form's contract rather than the reader's — which
    /// `ShapefileReader`'s own corpus already covers, against files this project did not write.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("4326", true)]
    [InlineData("", false)]
    public async Task The_form_sends_the_coordinate_system_only_when_it_is_given(
        string srid, bool expected)
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen never rendered.");

        await ClickAsync("#newLayer");

        await WaitForAsync(
            "document.getElementById('iFile')?.offsetParent !== null",
            "The import form never became visible.");

        // A file input cannot be typed into; `DataTransfer` is how a browser hands one over.
        string planted = $$"""
            (() => {
              const file = new File([new Uint8Array([80, 75, 3, 4, 0, 0])], 'layer.zip',
                { type: 'application/zip' });
              const held = new DataTransfer();
              held.items.add(file);
              document.getElementById('iFile').files = held.files;
              document.getElementById('iName').value = 'zz_form_contract';
              document.getElementById('iSrid').value = '{{srid}}';
              document.getElementById('importForm').requestSubmit();
              return document.getElementById('iFile').files.length === 1;
            })()
            """;

        Assert.True(
            await Browser.EvaluateAsync<bool>(planted),
            "The file could not be planted on the input, so nothing was submitted.");

        await WaitForAsync(
            "(window.__writes || []).some(w => w.includes('/admin/hosted/import'))",
            "The form did not post to /admin/hosted/import at all.");

        string[] writes = await WritesAsync();

        string wrote = writes.First(w => w.Contains("/admin/hosted/import", StringComparison.Ordinal));

        // The three the form always sends, so a missing one is a broken form rather than a policy.
        foreach (string always in new[] { "file", "name", "sharing" })
        {
            Assert.Contains(always, wrote, StringComparison.Ordinal);
        }

        Assert.Equal(expected, wrote.Contains("srid", StringComparison.Ordinal));

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }
}
