using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The form client's <c>annotate</c> appearance, ADR-287 §5.24, 2026-11-16: a photo set on the question's input opens on
/// the drawing pad, held within <c>max-pixels</c> on its long edge; a stroke drawn with a pointer changes its pixels;
/// Undo and Clear take lines off; Done leaves one flattened JPEG as the answer, kept in the draft and queued in the
/// outbox; and the photo alone, with nothing drawn, is an answer too. The server is the stand-in of
/// <see cref="FormFillClientTests.Site"/>; the checker's side is <c>AFormAnnotatesAPhotoTests</c> in the Host tests.
/// </summary>
public sealed class FormFillAnnotateTests : IClassFixture<FormFillClientTests.Site>
{
    private readonly FormFillClientTests.Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillAnnotateTests(FormFillClientTests.Site site) => _site = site;

    private string PublishPhoto(string? maxPixels = "800")
    {
        JsonObject q = FormFillClientTests.Form.Question("foto", "image", "Foto", required: "yes", appearance: ["annotate"]);
        if (maxPixels is not null)
        {
            q["parameters"] = new JsonObject { ["max-pixels"] = maxPixels };
        }

        return _site.Publish(new FormFillClientTests.Form { Root = FormFillClientTests.Form.Repeat("", [q]) });
    }

    /// <summary>A 2000 by 1000 green photo, made in the page as a JPEG and set on the question's input.</summary>
    private Task<bool> SetPhotoAsync() => _site.EvalAsync<bool>("""
        (function () {
          var c = document.createElement('canvas'); c.width = 2000; c.height = 1000;
          var x = c.getContext('2d'); x.fillStyle = '#00a000'; x.fillRect(0, 0, 2000, 1000);
          return new Promise(function (resolve) {
            c.toBlob(function (blob) {
              var input = document.querySelector('[data-name=foto] input[type=file]');
              var dt = new DataTransfer();
              dt.items.add(new File([blob], 'site.jpg', { type: 'image/jpeg' }));
              input.files = dt.files;
              input.dispatchEvent(new Event('change', { bubbles: true }));
              resolve(true);
            }, 'image/jpeg', 0.95);
          });
        })()
        """);

    private const string PadCentre = """
        (function () {
          var c = document.querySelector('[data-name=foto] .annotate-pad canvas');
          var d = c.getContext('2d').getImageData(Math.floor(c.width / 2), Math.floor(c.height / 2), 1, 1).data;
          return [d[0], d[1], d[2]];
        })()
        """;

    /// <summary>The draft's picture for the question, decoded: [r, g, b] at its centre, its width and height, its type and name.</summary>
    private static string DraftPicture(string id) => $$"""
        {{FormFillClientTests.Site.IdbAll("drafts")}}.then(function (all) {
          var d = all.filter(function (x) { return x.formId === '{{id}}'; })[0];
          var f = d && d.tree.files.foto && d.tree.files.foto[0];
          if (!f || !(f.blob instanceof Blob)) { return null; }
          return createImageBitmap(f.blob).then(function (b) {
            var c = document.createElement('canvas'); c.width = b.width; c.height = b.height;
            var x = c.getContext('2d'); x.drawImage(b, 0, 0);
            var p = x.getImageData(Math.floor(b.width / 2), Math.floor(b.height / 2), 1, 1).data;
            return { r: p[0], g: p[1], b: p[2], w: b.width, h: b.height, type: f.type, name: f.name, files: d.tree.files.foto.length };
          });
        })
        """;

    private static bool IsGreen(int[] p) => p[1] > 120 && p[0] < 80;

    private static bool IsRed(int[] p) => p[0] > 150 && p[1] < 90;

    /// <summary>A horizontal line through the middle of the pad, with the mouse, as a person draws it.</summary>
    private async Task StrokeAsync()
    {
        int[] at = await _site.EvalAsync<int[]>("""
            (function () {
              var c = document.querySelector('[data-name=foto] .annotate-pad canvas');
              c.scrollIntoView({ block: 'center' });
              var r = c.getBoundingClientRect();
              return [Math.round(r.left + r.width * 0.2), Math.round(r.top + r.height / 2), Math.round(r.left + r.width / 2), Math.round(r.left + r.width * 0.8)];
            })()
            """) ?? [];
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at[0], y = at[1] });
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x = at[0], y = at[1], button = "left", clickCount = 1 });
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at[2], y = at[1], button = "left" });
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at[3], y = at[1], button = "left" });
        await _site.Browser.CallAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x = at[3], y = at[1], button = "left", clickCount = 1 });
    }

    [Fact]
    public async Task A_photo_opens_on_the_pad_within_max_pixels_and_a_stroke_changes_its_pixels_and_done_keeps_one_jpeg()
    {
        string id = PublishPhoto("800");
        await _site.OpenAsync(id);

        // Before a photo: the input, and words that say the photo alone will do.
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] .annotate-pad').hidden"));
        Assert.Contains("can be sent as it is", await _site.EvalAsync<string>("document.getElementById(document.querySelector('[data-name=foto] input[type=file]').getAttribute('aria-describedby').split(' ').pop()).textContent"), StringComparison.Ordinal);

        await SetPhotoAsync();
        await _site.WaitAsync("!document.querySelector('[data-name=foto] .annotate-pad').hidden", "the pad to open over the photo");

        // Held to 800 on its long edge, its shape kept.
        int[] size = await _site.EvalAsync<int[]>("(function () { var c = document.querySelector('[data-name=foto] .annotate-pad canvas'); return [c.width, c.height]; })()") ?? [];
        Assert.Equal([800, 400], size);
        Assert.True(IsGreen(await _site.EvalAsync<int[]>(PadCentre) ?? []), "The photo is not on the pad.");
        // The photo is already the answer, before anything is drawn, and it is the small one.
        await _site.WaitAsync($"{DraftPicture(id)}.then(function (p) {{ return !!p && p.w === 800 && p.h === 400 && p.g > 120; }})", "the photo alone to be the answer in the draft");
        await _site.WaitAsync("document.querySelector('[data-name=foto] .annotate > p[aria-live]').textContent.indexOf('Photo attached') === 0", "the photo to be announced");

        // Every control has a name and is reached by the keyboard.
        Assert.Equal(0, await _site.EvalAsync<int>("""
            Array.prototype.filter.call(document.querySelectorAll('[data-name=foto] .annotate-pad button, [data-name=foto] .annotate-pad select'), function (c) {
              var named = c.tagName === 'SELECT' ? c.labels.length > 0 && c.labels[0].textContent.trim().length > 0 : c.textContent.trim().length > 0;
              return !named || c.tabIndex < 0;
            }).length
            """));
        string describedBy = await _site.EvalAsync<string>("document.querySelector('[data-name=foto] .annotate-pad canvas').getAttribute('aria-describedby')") ?? "";
        Assert.Contains("keyboard", await _site.EvalAsync<string>($"document.getElementById('{describedBy}').textContent") ?? "", StringComparison.Ordinal);

        // A stroke turns the centre red, and the answer becomes the flattened picture.
        await StrokeAsync();
        Assert.True(IsRed(await _site.EvalAsync<int[]>(PadCentre) ?? []), "The stroke did not change the pixels.");
        await _site.WaitAsync($"{DraftPicture(id)}.then(function (p) {{ return !!p && p.r > 150 && p.g < 90; }})", "the drawn line to be in the draft's picture");

        // Done closes the pad, moves focus to the button that reopens it, and leaves one JPEG of the photo's size.
        await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] [data-focus=done]').click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=foto] .annotate-pad').hidden && !document.querySelector('[data-name=foto] .annotate-held').hidden", "the pad to close");
        Assert.Equal("open", await _site.EvalAsync<string>("document.activeElement.getAttribute('data-focus')"));
        await _site.WaitAsync("document.querySelector('[data-name=foto] .annotate > p[aria-live]').textContent.indexOf('saved with your drawing') > 0", "Done to be announced");
        await _site.WaitAsync($"{DraftPicture(id)}.then(function (p) {{ return !!p && p.files === 1 && p.type === 'image/jpeg' && p.name === 'site.jpg' && p.w === 800 && p.h === 400 && p.r > 150; }})",
            "one flattened JPEG to be the answer");
    }

    [Fact]
    public async Task Undo_and_clear_take_the_lines_off_and_leave_the_photo()
    {
        string id = PublishPhoto("800");
        await _site.OpenAsync(id);
        await SetPhotoAsync();
        await _site.WaitAsync("!document.querySelector('[data-name=foto] .annotate-pad').hidden", "the pad to open");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] [data-focus=undo]').disabled"), "Undo is offered with nothing to undo.");

        await StrokeAsync();
        Assert.True(IsRed(await _site.EvalAsync<int[]>(PadCentre) ?? []));
        await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] [data-focus=undo]').click(); true");
        Assert.True(IsGreen(await _site.EvalAsync<int[]>(PadCentre) ?? []), "Undo left the line.");
        await _site.WaitAsync($"{DraftPicture(id)}.then(function (p) {{ return !!p && p.g > 120 && p.r < 80; }})", "the undone line to leave the draft's picture");
        await _site.WaitAsync("document.querySelector('[data-name=foto] .annotate > p[aria-live]').textContent === 'Last line undone.'", "Undo to be announced");
        Assert.Equal("pad", await _site.EvalAsync<string>("document.activeElement.getAttribute('data-focus')"));

        // Change the pen, draw twice, and clear: back to the photo.
        await _site.EvalAsync<bool>("(function () { var s = document.querySelector('[data-name=foto] [data-focus=colour]'); s.value = '#1f6fd1'; s.dispatchEvent(new Event('change')); return true; })()");
        await StrokeAsync();
        int[] blue = await _site.EvalAsync<int[]>(PadCentre) ?? [];
        Assert.True(blue[2] > 150 && blue[0] < 90, "The pen's colour was not used.");
        await StrokeAsync();
        await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] [data-focus=clear]').click(); true");
        Assert.True(IsGreen(await _site.EvalAsync<int[]>(PadCentre) ?? []), "Clear left a line.");
        await _site.WaitAsync("document.querySelector('[data-name=foto] .annotate > p[aria-live]').textContent.indexOf('The photo is as it was') > 0", "Clear to be announced");
        await _site.WaitAsync($"{DraftPicture(id)}.then(function (p) {{ return !!p && p.g > 120 && p.r < 80 && p.files === 1; }})", "the cleared picture to be the photo again");
    }

    [Fact]
    public async Task The_photo_alone_answers_the_question_and_survives_a_draft_reload_and_goes_to_the_outbox()
    {
        string id = PublishPhoto(null);   // no max-pixels: 1280 on the long edge
        await _site.OpenAsync(id);
        await SetPhotoAsync();
        await _site.WaitAsync("!document.querySelector('[data-name=foto] .annotate-pad').hidden", "the pad to open");
        int[] size = await _site.EvalAsync<int[]>("(function () { var c = document.querySelector('[data-name=foto] .annotate-pad canvas'); return [c.width, c.height]; })()") ?? [];
        Assert.Equal([1280, 640], size);
        await StrokeAsync();
        await _site.WaitAsync($"{DraftPicture(id)}.then(function (p) {{ return !!p && p.r > 150; }})", "the line to be in the draft");

        // The draft comes back with the drawn picture, which opens again as the ground for more lines.
        await _site.OpenAsync(id);
        await _site.WaitAsync("document.getElementById('draft-reopened').textContent.indexOf('reopened') > 0", "the draft to be reopened");
        Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] .annotate-held').hidden"), "The drafted photo is not shown.");
        await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] [data-focus=open]').click(); true");
        await _site.WaitAsync("!document.querySelector('[data-name=foto] .annotate-pad').hidden", "the pad to reopen from the draft");
        Assert.True(IsRed(await _site.EvalAsync<int[]>(PadCentre) ?? []), "The draft's drawing did not come back.");
        Assert.Equal("pad", await _site.EvalAsync<string>("document.activeElement.getAttribute('data-focus')"));

        // Remove, then answer with the photo alone and send with no server: the outbox keeps it as a JPEG Blob.
        await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] [data-focus=done]').click(); true");
        await _site.WaitAsync("!document.querySelector('[data-name=foto] .annotate-held').hidden", "the pad to close");
        await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] [data-focus=remove]').click(); true");
        await _site.WaitAsync("document.querySelector('[data-name=foto] .annotate-held').hidden", "the photo to be removed");
        Assert.Equal("input", await _site.EvalAsync<string>("document.activeElement.getAttribute('data-focus')"));
        await SetPhotoAsync();
        await _site.WaitAsync("!document.querySelector('[data-name=foto] .annotate-pad').hidden", "the pad to open on the new photo");
        Assert.True(IsGreen(await _site.EvalAsync<int[]>(PadCentre) ?? []), "The old drawing was kept over a new photo.");

        _site.Stop();
        try
        {
            await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
            await _site.WaitAsync($$"""
                {{FormFillClientTests.Site.IdbAll("outbox")}}.then(function (all) {
                  return all.some(function (r) {
                    return JSON.stringify(r).indexOf('{{id}}') >= 0 && (r.attachments || []).some(function (a) {
                      return a.question === 'foto' && a.blob instanceof Blob && a.blob.size > 0 && a.type === 'image/jpeg';
                    });
                  });
                })
                """, "the photo to be queued in the outbox as a JPEG", seconds: 20);
        }
        finally
        {
            _site.Start();
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_readable_image_is_attached_as_it_is_and_said_so()
    {
        string id = PublishPhoto("800");
        await _site.OpenAsync(id);
        await _site.EvalAsync<bool>("""
            (function () {
              var input = document.querySelector('[data-name=foto] input[type=file]');
              var dt = new DataTransfer();
              dt.items.add(new File([new Uint8Array([1, 2, 3, 4])], 'broken.heic', { type: 'image/heic' }));
              input.files = dt.files;
              input.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """);
        await _site.WaitAsync("document.querySelector('[data-name=foto] .annotate > p[aria-live]').textContent.indexOf('could not be opened for drawing') > 0", "the refusal to draw to be said");
        Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=foto] .annotate-pad').hidden"));
        await _site.WaitAsync($$"""
            {{FormFillClientTests.Site.IdbAll("drafts")}}.then(function (all) {
              var d = all.filter(function (x) { return x.formId === '{{id}}'; })[0];
              var f = d && d.tree.files.foto && d.tree.files.foto[0];
              return !!f && f.name === 'broken.heic';
            })
            """, "the file to be kept as the answer");
    }
}
