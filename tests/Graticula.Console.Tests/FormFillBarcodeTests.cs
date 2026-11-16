using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The barcode question in the form client, ADR-287 §5.22, 2026-11-16: the answer is always a box that can be typed into,
/// and a Scan button is offered only where the browser has a barcode reader of its own. The reader and the camera are
/// stubbed in the page before its scripts run — a headless browser has neither — so these test what the page does with
/// them: the formats it asks for, the rear camera, the first code read put into the box, and the camera stopped after a
/// value, a Cancel, the page being left and a refusal. The server's half is <c>AFormTakesABarcodeQuestionTests</c>.
/// </summary>
public sealed class FormFillBarcodeTests : IClassFixture<FormFillClientTests.Site>
{
    private readonly FormFillClientTests.Site _site;

    /// <summary>Creates the tests over the site.</summary>
    /// <param name="site">The stand-in server and the browser, opened once for the class.</param>
    public FormFillBarcodeTests(FormFillClientTests.Site site) => _site = site;

    /// <summary>A browser with no reader, which is Safari and, on Windows, Chrome too; made certain rather than assumed.</summary>
    private const string NoReader = """
        try { Object.defineProperty(window, 'BarcodeDetector', { value: undefined, configurable: true, writable: true }); } catch (e) { }
        """;

    /// <summary>
    /// A reader that finds a code on its third look, and a camera that is a painted canvas. <c>window.__scan.mode</c> is
    /// <c>read</c>, <c>none</c> (never finds one) or <c>refuse</c> (the camera is refused).
    /// </summary>
    private const string Reader = """
        (function () {
          var log = window.__scan = { gum: 0, stopped: 0, live: 0, detects: 0, constraints: null, formats: null, mode: 'read' };
          window.BarcodeDetector = function (options) { log.formats = options && options.formats; log.detects = 0; };
          window.BarcodeDetector.getSupportedFormats = function () { return Promise.resolve(['qr_code', 'ean_13']); };
          window.BarcodeDetector.prototype.detect = function () {
            log.detects++;
            return Promise.resolve(log.mode === 'read' && log.detects >= 3 ? [{ rawValue: '4006381333931', format: 'ean_13' }] : []);
          };
          navigator.mediaDevices.getUserMedia = function (constraints) {
            log.gum++;
            log.constraints = constraints;
            if (log.mode === 'refuse') { return Promise.reject(new DOMException('Permission denied', 'NotAllowedError')); }
            var canvas = document.createElement('canvas');
            canvas.width = 32; canvas.height = 24;
            canvas.getContext('2d').fillRect(0, 0, 32, 24);
            var stream = canvas.captureStream(10);
            stream.getTracks().forEach(function (track) {
              log.live++;
              var stop = track.stop.bind(track);
              track.stop = function () { log.stopped++; log.live--; stop(); };
            });
            return Promise.resolve(stream);
          };
        })();
        """;

    private static FormFillClientTests.Form Codes() => new()
    {
        Root = FormFillClientTests.Form.Repeat("", [
            FormFillClientTests.Form.Question("kod", "barcode", "Ürün kodu"),
            FormFillClientTests.Form.Question("gizli", "barcode", "Etiket", null, null, null, null, null, "minimal"),
        ]),
    };

    private async Task<T> WithPlantedAsync<T>(string source, Func<Task<T>> body)
    {
        JsonElement planted = await _site.Browser.CallAsync("Page.addScriptToEvaluateOnNewDocument", new { source });
        string identifier = planted.GetProperty("identifier").GetString()!;

        try
        {
            return await body();
        }
        finally
        {
            await _site.Browser.CallAsync("Page.removeScriptToEvaluateOnNewDocument", new { identifier });
        }
    }

    [Fact]
    public async Task Without_a_reader_there_is_no_scan_button_the_box_says_it_can_be_typed_and_a_typed_code_is_sent() =>
        await WithPlantedAsync(NoReader, async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!document.querySelector('[data-name=kod] [id$=-no-reader]').hidden", "the no-reader hint");

            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=scan]').hidden"), "A Scan button was offered with no reader.");
            Assert.Contains("Type the code instead", await _site.EvalAsync<string>("document.querySelector('[data-name=kod] [id$=-no-reader]').textContent") ?? "");
            // The hint is read with the box, and the box is labelled by the question.
            Assert.Contains("-no-reader", await _site.EvalAsync<string>("document.querySelector('[data-name=kod] input').getAttribute('aria-describedby')") ?? "");
            Assert.StartsWith("Ürün kodu", await _site.EvalAsync<string>("document.querySelector('[data-name=kod] input').labels[0].textContent") ?? "");
            // minimal hides the box only where a scanner can fill it; here it is the only way to answer.
            Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] input').hidden"), "A minimal barcode with no reader could not be answered.");

            await _site.SetAsync("kod", "8690000000017");
            await _site.SetAsync("gizli", "ETK-42");
            await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
            await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
            Assert.Contains(_site.Adds, a => a.Contains("8690000000017", StringComparison.Ordinal) && a.Contains("ETK-42", StringComparison.Ordinal));
            return true;
        });

    [Fact]
    public async Task With_a_reader_scan_opens_the_rear_camera_puts_the_code_in_the_box_and_stops_the_camera() =>
        await WithPlantedAsync(Reader, async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!document.querySelector('[data-name=kod] [data-focus=scan]').hidden", "the Scan button");

            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [id$=-no-reader]').hidden"));
            Assert.Equal("Scan", await _site.EvalAsync<string>("document.querySelector('[data-name=kod] [data-focus=scan]').textContent"));
            // minimal: only the button, as Survey123 draws it, where a scanner can fill the box.
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] input').hidden"), "minimal showed the box beside a scanner.");

            await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=scan]').click(); true");
            await _site.WaitAsync("document.querySelector('[data-name=kod] input').value === '4006381333931'", "the code read into the box");

            Assert.Equal(1, await _site.EvalAsync<int>("window.__scan.gum"));
            Assert.Equal("environment", await _site.EvalAsync<string>("window.__scan.constraints.video.facingMode.ideal"));
            Assert.False(await _site.EvalAsync<bool>("window.__scan.constraints.audio"));
            Assert.Equal("qr_code,ean_13", await _site.EvalAsync<string>("window.__scan.formats.join(',')"));
            Assert.Equal(0, await _site.EvalAsync<int>("window.__scan.live"));
            Assert.Equal(1, await _site.EvalAsync<int>("window.__scan.stopped"));
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] .scanner').hidden"), "The preview stayed open.");
            Assert.Null(await _site.EvalAsync<string>("document.querySelector('[data-name=kod] video').srcObject"));
            await _site.WaitAsync("document.querySelector('[data-name=kod] [aria-live]').textContent === 'Code read.'", "the announcement");
            Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=kod] input')"), "Focus was not on the box with the code.");

            // The code is the answer: it is sent.
            await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
            await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
            Assert.Contains(_site.Adds, a => a.Contains("4006381333931", StringComparison.Ordinal));
            return true;
        });

    [Fact]
    public async Task Cancel_leaving_the_page_and_a_refused_camera_each_stop_it_and_say_so() =>
        await WithPlantedAsync(Reader, async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!document.querySelector('[data-name=kod] [data-focus=scan]').hidden", "the Scan button");
            await _site.EvalAsync<bool>("window.__scan.mode = 'none'; true");

            // Cancel: the preview is shown with its Cancel focused, and Cancel stops the camera and gives focus back.
            await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=scan]').click(); true");
            await _site.WaitAsync("window.__scan.live === 1 && document.activeElement === document.querySelector('[data-name=kod] [data-focus=cancel-scan]')", "the preview");
            Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] .scanner').hidden"));
            Assert.Equal("Camera preview", await _site.EvalAsync<string>("document.querySelector('[data-name=kod] video').getAttribute('aria-label')"));
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=scan]').disabled"));
            await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=cancel-scan]').click(); true");
            Assert.Equal(0, await _site.EvalAsync<int>("window.__scan.live"));
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] .scanner').hidden"));
            await _site.WaitAsync("document.querySelector('[data-name=kod] [aria-live]').textContent === 'Scan cancelled.'", "the cancel announcement");
            Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=kod] [data-focus=scan]')"));
            Assert.Equal("", await _site.EvalAsync<string>("document.querySelector('[data-name=kod] input').value"));

            // Leaving the page: the camera is not left on behind it.
            await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=scan]').click(); true");
            await _site.WaitAsync("window.__scan.live === 1", "the second preview");
            await _site.EvalAsync<bool>("window.dispatchEvent(new PageTransitionEvent('pagehide')); true");
            Assert.Equal(0, await _site.EvalAsync<int>("window.__scan.live"));
            await _site.WaitAsync("document.querySelector('[data-name=kod] [aria-live]').textContent.indexOf('page was left') >= 0", "the page-left announcement");

            // A refused camera: said in words, and a minimal question shows its box, which is now the only way to answer.
            await _site.EvalAsync<bool>("window.__scan.mode = 'refuse'; true");
            await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] [data-focus=scan]').click(); true");
            await _site.WaitAsync("document.querySelector('[data-name=gizli] [aria-live]').textContent === 'Camera permission was refused. Type the code instead.'", "the refusal");
            Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] input').hidden"), "The box stayed hidden after the camera was refused.");
            Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=gizli] input')"));
            Assert.Equal(0, await _site.EvalAsync<int>("window.__scan.live"));
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] .scanner').hidden"));
            return true;
        });

    [Fact]
    public async Task The_scan_words_are_turkish_where_the_browser_is() =>
        await WithPlantedAsync(NoReader + "Object.defineProperty(navigator, 'language', { get: function () { return 'tr-TR'; } }); Object.defineProperty(navigator, 'languages', { get: function () { return ['tr-TR']; } });", async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!document.querySelector('[data-name=kod] [id$=-no-reader]').hidden", "the no-reader hint");
            Assert.Equal("Bu tarayıcı kod okuyamıyor. Kodu elle yazın.", await _site.EvalAsync<string>("document.querySelector('[data-name=kod] [id$=-no-reader]').textContent"));
            return true;
        });
}
