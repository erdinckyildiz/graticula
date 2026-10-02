using System;
using System.IO;
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
    /// Walks an operator's route to the import form: New item, Feature layer, Upload a file, Next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three screens, because the owner's reference is three screens.</b> The form used to be one
    /// of four stacked in a drawer that opened on the surface's action; it is now the third step of a
    /// <c>New item</c> dialog, and the shape came from two ArcGIS Portal screenshots the owner sent —
    /// the <c>New item</c> grid with its drop zone, and <c>Create a feature layer</c> with its radio
    /// list and its <c>Next</c>.
    /// </para>
    /// <para>
    /// <b>Written as one helper rather than pasted into three tests</b>, so that the next time the
    /// route changes these tests move with it in one place. Each step waits on
    /// <c>offsetParent</c> — this console has shipped a control that existed and could not be seen
    /// three times, and a walker that clicks blind would report the fourth as a passing test.
    /// </para>
    /// </remarks>
    private async Task OpenImportFormAsync()
    {
        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen never rendered, so its page action is not there to press.");

        await ClickAsync("#newLayer");

        await WaitForAsync(
            Shown("#kindFeatureLayer"),
            "The New item dialog did not open, or its Feature layer tile is not visible. A closed "
            + "`dialog` is `display: none`, so `offsetParent` answers both questions at once.");

        await ClickAsync("#kindFeatureLayer");

        await WaitForAsync(
            Shown(".pickrow input[value='import']"),
            "The Create a feature layer step has no Upload a file option, so there is no route to "
            + "the import form at all.");

        await ClickAsync(".pickrow input[value=\"import\"]");
        await ClickAsync("#itemNext");

        await WaitForAsync(
            Shown("#iFile"),
            "Next did not reach the import form, or the form is not visible.");
    }

    /// <summary>
    /// A zipped shapefile can be chosen, and the form takes the code the server requires.
    /// </summary>
    [Fact]
    public async Task The_import_form_accepts_an_archive_and_asks_for_the_coordinate_system()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await OpenImportFormAsync();

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
                Shown("#iSrid")),
            "There is no field for the coordinate system, so a shapefile can be selected and never "
            + "accepted — the server requires `srid` and refuses to infer it from the .prj.");

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
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

        await OpenImportFormAsync();

        // <b>The dialog's body, not the form's `.group` wrapper.</b> There is no wrapper any more:
        // the form is the third screen of a dialog rather than one section of four in a drawer, and
        // the sentence that names what it takes is a sibling paragraph above it. Reading the body
        // reads both, which is what an operator does.
        string copy = await Browser.EvaluateAsync<string>(
            "document.getElementById('addItemBody').innerText") ?? string.Empty;

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
        NothingWentWrong(errors);
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
    /// `ShapefileCorpusTests` already covers, against files this project did not write.
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

        await OpenImportFormAsync();

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
        NothingWentWrong(errors);
    }
    /// <summary>
    /// A CSV is offered, and choosing one asks for its coordinate columns and sends them — ADR-112.
    /// </summary>
    [Fact]
    public async Task A_table_upload_asks_for_its_columns()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await OpenImportFormAsync();

        string accepts = await Browser.EvaluateAsync<string>(
            "document.getElementById('iFile').getAttribute('accept') || ''") ?? string.Empty;

        Assert.Contains(".csv", accepts, StringComparison.Ordinal);
        Assert.Contains(".xlsx", accepts, StringComparison.Ordinal);

        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#iX")), "The X column is asked for before a table is chosen.");

        await Browser.EvaluateAsync<bool>("""
            (() => {
              const held = new DataTransfer();
              held.items.add(new File(['ad,easting,northing\nA,500000,4400000\n'], 'sites.csv', { type: 'text/csv' }));
              const input = document.getElementById('iFile');
              input.files = held.files;
              input.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """);

        await WaitForAsync(Shown("#iX"), "Choosing a CSV did not ask which columns hold its coordinates.");
        Assert.Contains("easting", await Browser.EvaluateAsync<string>("document.getElementById('iNote').textContent") ?? "",
            StringComparison.Ordinal);

        await Browser.EvaluateAsync<bool>("""
            (() => {
              document.getElementById('iName').value = 'zz_sites';
              document.getElementById('iX').value = 'easting';
              document.getElementById('iY').value = 'northing';
              document.getElementById('iSrid').value = '5254';
              document.getElementById('importForm').requestSubmit();
              return true;
            })()
            """);

        await WaitForAsync(
            "(window.__writes || []).some(w => w.includes('/admin/hosted/import'))",
            "The form did not post the table.");

        string wrote = (await WritesAsync()).First(w => w.Contains("/admin/hosted/import", StringComparison.Ordinal));

        Assert.Contains("[file,name,sharing,srid,x,y]", wrote, StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }
    /// <summary>ADR-123: New item offers an imagery layer; a GeoTIFF goes to its form and is uploaded there.</summary>
    [Fact]
    public async Task A_GeoTIFF_is_offered_its_own_form_and_uploaded_from_it()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen never rendered.");
        await ClickAsync("#newLayer");
        await WaitForAsync(Shown("#kindImagery"), "New item offers no imagery layer.");

        // A GeoTIFF dropped on the first screen lands on the imagery form, named after itself.
        await Browser.EvaluateAsync<bool>("""
            (() => { takeFile([new File([new Uint8Array([73, 73, 42, 0])], 'Ortho 2026.tif', { type: 'image/tiff' })]); return true; })()
            """);
        await WaitForAsync(Shown("#imgFile"), "A GeoTIFF did not open the imagery form.");
        Assert.Equal("Ortho_2026", await Browser.EvaluateAsync<string>("document.getElementById('imgName').value"));

        await Browser.EvaluateAsync<bool>("(window.__writes = [], true)");
        await ClickAsync("#itemSubmit");
        await WaitForAsync(
            "window.__writes.some(w => w.startsWith('POST') && w.includes('/admin/coverages/upload') && w.includes('[file,name]'))",
            "Upload and publish sent no image.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// ADR-139's review: while an image uploads and its pyramids are built, every way out of the dialog stops it — the ✕
    /// and Escape stopped nothing, and the page jumped to the new service minutes later — and the wait says what it is.
    /// </summary>
    [Fact]
    public async Task An_upload_in_progress_is_stopped_by_every_way_out_and_says_what_it_is_doing()
    {
        (string token, _) = await SignInAsync();

        foreach (string way in new[] { "#itemCancel", "#addItemClose", "escape" })
        {
            await OpenAsync("/studio/#/content", token);
            await WaitForAsync("document.querySelectorAll('#contentScopes a').length > 0", "The content screen never rendered.");
            await ClickAsync("#newLayer");
            await WaitForAsync(Shown("#kindImagery"), "New item offers no imagery layer.");
            await Browser.EvaluateAsync<bool>("""
                (() => { takeFile([new File([new Uint8Array(4096)], 'Ortho 2026.tif', { type: 'image/tiff' })]); return true; })()
                """);
            await WaitForAsync(Shown("#imgFile"), "A GeoTIFF did not open the imagery form.");

            // The request is held, as a large one is, instead of answered at once as the harness answers writes.
            await Browser.EvaluateAsync<bool>("""
                (() => {
                  XMLHttpRequest.prototype.send = function () { window.__held = this; this.abort = () => this.onabort && this.onabort(); };
                  return true;
                })()
                """);
            await ClickAsync("#itemSubmit");
            await WaitForAsync("!!window.__held", "Upload and publish sent nothing.");

            Assert.Equal("itemCancel", await Browser.EvaluateAsync<string>("document.activeElement.id"));
            Assert.True(await Browser.EvaluateAsync<bool>(
                "document.getElementById('itemBack').hidden && document.getElementById('imgName').disabled && document.getElementById('itemSubmit').disabled"),
                "During the upload Back was offered or the fields could still be changed.");

            // Every byte sent: the server is building pyramids now, and the dialog says so with a running clock.
            await Browser.EvaluateAsync<bool>(
                "(window.__held.upload.onprogress({ lengthComputable: true, loaded: 4096, total: 4096 }), true)");
            await WaitForAsync(
                "/Preparing the image… 0:0\\d/.test(document.getElementById('imgProgressSays').textContent)"
                + " && !document.getElementById('imgProgress').hasAttribute('value')"
                + " && !document.getElementById('imgProgressHint').hidden"
                + " && document.getElementById('itemCancel').textContent === 'Stop'",
                "Once uploaded, the dialog did not say it was preparing the image.");

            if (way == "escape")
            {
                await Browser.EvaluateAsync<bool>(
                    "(document.getElementById('addItem').dispatchEvent(new Event('cancel', { cancelable: true })), true)");
            }
            else
            {
                await ClickAsync(way);
            }

            await WaitForAsync(
                "document.getElementById('addItem').open && /Nothing was published/.test(document.getElementById('imgResult').textContent)",
                $"{way} during the upload did not stop it and say so.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "!document.getElementById('itemBack').hidden && !document.getElementById('imgName').disabled && !document.getElementById('itemSubmit').disabled"),
                "After stopping, the form was not given back.");
            NothingWentWrong(await PageErrorsAsync());
        }
    }

    /// <summary>ADR-123: an image service's page sets its stretch and colours, and says what its values are.</summary>
    [Fact]
    public async Task An_image_services_display_is_set_on_its_page()
    {
        (string token, _) = await SignInAsync();
        string name = $"zz_disp_{Guid.NewGuid():N}"[..16];

        DirectoryInfo? at = new(AppContext.BaseDirectory);
        while (at is not null && at.GetFiles("*.sln").Length == 0) at = at.Parent;
        byte[] tiff = File.ReadAllBytes(Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", "gray-float32-deflate.tif"));

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(tiff), "file", "f.tif");
            form.Add(new StringContent(name), "name");
            using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{Root}/admin/coverages/upload")) { Content = form };
            upload.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage made = await Http.SendAsync(upload);
            Assert.True(made.IsSuccessStatusCode, $"Uploading answered {(int)made.StatusCode}: {await made.Content.ReadAsStringAsync()}");
        }

        try
        {
            await OpenAsync($"/studio/#/service/hosted/{name}?tab=settings&section=imagery", token);
            await WaitForAsync(Shown("#covStretch"), "An image service's page offers no Display settings.");
            Assert.Equal("auto", await Browser.EvaluateAsync<string>("document.getElementById('covStretch').value"));
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#serviceNav [data-service-page=feature]')"),
                "An image service is offered a Feature layer page.");
            Assert.Contains("values run", await Browser.EvaluateAsync<string>("document.getElementById('coverageDisplay').textContent") ?? "",
                StringComparison.Ordinal);

            // The picture is drawn under the controls before anything is saved.
            await WaitForAsync("(document.getElementById('covPreview').src || '').startsWith('blob:')",
                "The Display settings show no picture of what they draw.");

            // An empty range is not zero: nothing is sent, and the page says why.
            await Browser.EvaluateAsync<bool>("""
                (() => {
                  window.__writes = [];
                  const s = document.getElementById('covStretch');
                  s.value = 'fixed';
                  s.dispatchEvent(new Event('change'));
                  document.getElementById('covMin').value = '';
                  return true;
                })()
                """);
            await ClickAsync("#covSave");
            await WaitForAsync("document.getElementById('covRangeSays').textContent.includes('lower than To')",
                "An empty From was not refused.");
            Assert.Empty(await WritesAsync());

            await Browser.EvaluateAsync<bool>("""
                (() => {
                  const s = document.getElementById('covStretch');
                  s.value = 'auto';
                  s.dispatchEvent(new Event('change'));
                  window.__writes = [];
                  document.getElementById('covRamp').value = 'terrain';
                  return true;
                })()
                """);
            await ClickAsync("#covSave");
            await WaitForAsync(
                $"window.__writes.some(w => w.startsWith('PUT') && w.includes('/admin/coverages/{name}/style'))",
                "Save sent nothing.");

            // ADR-136: shown through a raster function, the value controls rest and the function says what it shows.
            await Browser.EvaluateAsync<bool>(
                "(() => { const f = document.getElementById('covFunction'); f.value = 'slope'; f.dispatchEvent(new Event('change')); return true; })()");
            await WaitForAsync("document.getElementById('covValueControls').hidden"
                + " && document.getElementById('covFunctionSays').textContent.includes('steep')"
                + " && document.getElementById('covLegendSays').textContent.includes('flat')",
                "Choosing Slope did not set the value controls aside and say what it shows.");

            // Its Overview has no Layers section, and General says the uploaded file goes with it.
            await OpenAsync($"/studio/#/service/hosted/{name}", token);
            await WaitForAsync("serviceOpenKind === 'ImageServer' && document.getElementById('serviceLayersHead').hidden",
                "An image service's page still offers a Layers section.");
            // ADR-126: its owner describes and tags it there, as a feature service's owner does.
            await WaitForAsync("!!document.querySelector('#serviceDescription [data-describe]')",
                "An image service's page offers no way to describe it.");
            await OpenAsync($"/studio/#/service/hosted/{name}?tab=settings&section=general", token);
            await WaitForAsync("(document.getElementById('svcDeleteNote')?.textContent || '').includes('cannot be recovered')",
                "Settings › General does not say the uploaded image is deleted with the service.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }
}
