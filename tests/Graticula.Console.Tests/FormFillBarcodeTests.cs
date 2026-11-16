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

    /// <summary>
    /// A page with no camera API at all: no reader of the browser's own and no <c>getUserMedia</c>, which is every page not
    /// opened at a secure address. Since 2026-11-17 a browser without a reader of its own but with a camera scans with
    /// ZXing, so the camera has to go too for the box to be the only way.
    /// </summary>
    private const string NoReader = """
        try { Object.defineProperty(window, 'BarcodeDetector', { value: undefined, configurable: true, writable: true }); } catch (e) { }
        try { Object.defineProperty(Navigator.prototype, 'mediaDevices', { get: function () { return undefined; }, configurable: true }); } catch (e) { }
        """;

    /// <summary>
    /// A browser with a camera but no reader of its own — Safari on iPhone, Firefox, Chrome on Windows — whose camera is a
    /// canvas showing a real QR code, drawn with the console's vendored <c>qrcode.js</c> (planted before this script), and
    /// repainted ten times a second so the stream keeps giving frames. <c>window.__scan.text</c> is what the code says.
    /// </summary>
    private const string ZxingCamera = """
        (function () {
          try { Object.defineProperty(window, 'BarcodeDetector', { value: undefined, configurable: true, writable: true }); } catch (e) { }
          var log = window.__scan = { gum: 0, stopped: 0, live: 0, constraints: null, text: 'GRT-4006381333931' };
          navigator.mediaDevices.getUserMedia = function (constraints) {
            log.gum++;
            log.constraints = constraints;
            var canvas = document.createElement('canvas');
            canvas.width = 320; canvas.height = 240;
            var g = canvas.getContext('2d');
            function paint() {
              g.fillStyle = '#fff';
              g.fillRect(0, 0, 320, 240);
              var qr = qrcode(0, 'M');
              qr.addData(log.text);
              qr.make();
              var n = qr.getModuleCount(), size = Math.floor(200 / n), x0 = Math.floor((320 - n * size) / 2), y0 = Math.floor((240 - n * size) / 2);
              g.fillStyle = '#000';
              for (var r = 0; r < n; r++) { for (var c = 0; c < n; c++) { if (qr.isDark(r, c)) { g.fillRect(x0 + c * size, y0 + r * size, size, size); } } }
            }
            paint();
            var timer = setInterval(paint, 100);
            var stream = canvas.captureStream(10);
            stream.getTracks().forEach(function (track) {
              log.live++;
              var stop = track.stop.bind(track);
              track.stop = function () { clearInterval(timer); log.stopped++; log.live--; stop(); };
            });
            return Promise.resolve(stream);
          };
        })();
        """;

    /// <summary>The vendored library's SHA-256, as copied byte for byte from <c>@zxing/library</c> 0.23.0's <c>umd/index.min.js</c>.</summary>
    private const string ZxingSha256 = "3ede94153fb0c5b67a12d7adff6decd827c2b22714fdc6faecf27a8f20937ea6";

    private static string Wwwroot()
    {
        System.IO.DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return System.IO.Path.Combine(directory!.FullName, "src", "Graticula.Host", "wwwroot");
    }

    /// <summary>The QR generator and the ZXing camera, planted together.</summary>
    private static string QrCamera() => System.IO.File.ReadAllText(System.IO.Path.Combine(Wwwroot(), "qrcode.js")) + "\n" + ZxingCamera;

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

    /// <summary>
    /// UX review 2026-11-16: a minimal question read a code into a box nobody could see, so a wrong label went unnoticed,
    /// and after a draft reload nothing showed there was an answer at all. The code is shown as text, said with the
    /// announcement, and describes the Scan button; the instruction under the preview is said once, not twice.
    /// </summary>
    [Fact]
    public async Task A_minimal_question_shows_the_code_it_read_and_keeps_showing_it_after_a_reload() =>
        await WithPlantedAsync(Reader, async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!document.querySelector('[data-name=gizli] [data-focus=scan]').hidden", "the Scan button");
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] .barcode-value').hidden"), "An empty code was shown.");

            await _site.EvalAsync<bool>("window.__scan.mode = 'none'; document.querySelector('[data-name=gizli] [data-focus=scan]').click(); true");
            await _site.WaitAsync("window.__scan.live === 1", "the preview");
            Assert.Equal(0, await _site.EvalAsync<int>("document.querySelectorAll('[data-name=gizli] .scanner .hint').length"));
            await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] [data-focus=cancel-scan]').click(); true");

            await _site.EvalAsync<bool>("window.__scan.mode = 'read'; document.querySelector('[data-name=gizli] [data-focus=scan]').click(); true");
            await _site.WaitAsync("document.querySelector('[data-name=gizli] [aria-live]').textContent === 'Code read: 4006381333931'", "the code said with the announcement");
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] input').hidden"), "minimal showed the box.");
            Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] .barcode-value').hidden"), "The code read is not shown.");
            Assert.Equal("4006381333931", await _site.EvalAsync<string>("document.querySelector('[data-name=gizli] .barcode-value').textContent"));
            Assert.Contains(await _site.EvalAsync<string>("document.querySelector('[data-name=gizli] .barcode-value').id") ?? "-",
                await _site.EvalAsync<string>("document.querySelector('[data-name=gizli] [data-focus=scan]').getAttribute('aria-describedby')") ?? "", StringComparison.Ordinal);
            Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=gizli] [data-focus=scan]')"));

            // The draft comes back: the code is still there to see.
            await _site.WaitAsync($"{FormFillClientTests.Site.IdbAll("drafts")}.then(function (all) {{ return all.some(function (d) {{ return d.formId === '{id}' && JSON.stringify(d.tree).indexOf('4006381333931') >= 0; }}); }})", "the draft to hold the code");
            await _site.OpenAsync(id);
            await _site.WaitAsync("document.getElementById('draft-reopened').textContent.indexOf('reopened') > 0", "the draft to be reopened");
            await _site.WaitAsync("!document.querySelector('[data-name=gizli] .barcode-value').hidden && document.querySelector('[data-name=gizli] .barcode-value').textContent === '4006381333931'", "the drafted code to be shown");
            return true;
        });

    /// <summary>
    /// Owner decision 2026-11-17 (*"ZXing'i benimse"*): a browser with a camera and no reader of its own scans with the
    /// vendored ZXing. The library is not fetched until Scan is pressed, and the code in the camera's picture — a real QR
    /// code, not a stubbed answer — goes into the box, after which the camera is stopped.
    /// </summary>
    [Fact]
    public async Task Without_a_browser_reader_scan_loads_zxing_only_when_pressed_reads_a_real_code_and_stops_the_camera() =>
        await WithPlantedAsync(QrCamera(), async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!document.querySelector('[data-name=kod] [data-focus=scan]').hidden", "the Scan button");

            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [id$=-no-reader]').hidden"), "The no-reader hint was shown beside a camera.");
            Assert.True(await _site.EvalAsync<bool>("typeof window.BarcodeDetector === 'undefined'"), "The planted browser had a reader of its own.");
            // minimal hides the box here too: there is a scanner to fill it.
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] input').hidden"));
            // Lazy: nothing of ZXing is in the page before Scan.
            Assert.True(await _site.EvalAsync<bool>("!window.ZXing && !document.querySelector('script[src$=\"zxing.min.js\"]')"), "ZXing was loaded before Scan was pressed.");

            await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=scan]').click(); true");
            await _site.WaitAsync("document.querySelector('[data-name=kod] input').value === 'GRT-4006381333931'", "the code read into the box", seconds: 20);

            Assert.True(await _site.EvalAsync<bool>("!!window.ZXing && !!document.querySelector('script[src$=\"zxing.min.js\"]')"), "The code was read without ZXing.");
            Assert.Equal(1, await _site.EvalAsync<int>("window.__scan.gum"));
            Assert.Equal("environment", await _site.EvalAsync<string>("window.__scan.constraints.video.facingMode.ideal"));
            Assert.False(await _site.EvalAsync<bool>("window.__scan.constraints.audio"));
            Assert.Equal(0, await _site.EvalAsync<int>("window.__scan.live"));
            Assert.Equal(1, await _site.EvalAsync<int>("window.__scan.stopped"));
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] .scanner').hidden"), "The preview stayed open.");
            Assert.Null(await _site.EvalAsync<string>("document.querySelector('[data-name=kod] video').srcObject"));
            // iOS Safari plays a camera inline only with playsinline, and autoplays only muted.
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] video').hasAttribute('playsinline') && document.querySelector('[data-name=kod] video').muted"));
            await _site.WaitAsync("document.querySelector('[data-name=kod] [aria-live]').textContent === 'Code read.'", "the announcement");
            Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=kod] input')"), "Focus was not on the box with the code.");

            // The second scan reuses the loaded library: one script tag, and the minimal question shows what it read.
            await _site.EvalAsync<bool>("window.__scan.text = 'ETK-42'; document.querySelector('[data-name=gizli] [data-focus=scan]').click(); true");
            await _site.WaitAsync("document.querySelector('[data-name=gizli] [aria-live]').textContent === 'Code read: ETK-42'", "the second code", seconds: 20);
            Assert.Equal(1, await _site.EvalAsync<int>("document.querySelectorAll('script[src$=\"zxing.min.js\"]').length"));
            Assert.Equal(0, await _site.EvalAsync<int>("window.__scan.live"));

            await _site.EvalAsync<bool>("document.getElementById('submit').click(); true");
            await _site.WaitAsync("!document.getElementById('done').hidden", "the confirmation");
            Assert.Contains(_site.Adds, a => a.Contains("GRT-4006381333931", StringComparison.Ordinal) && a.Contains("ETK-42", StringComparison.Ordinal));
            return true;
        });

    /// <summary>
    /// The library cannot be fetched — offline on a device that never had the page's shell, or a proxy that strips it: the
    /// scan stops, the camera with it, and the person is told in words, with the box shown to type into.
    /// </summary>
    [Fact]
    public async Task A_reader_that_cannot_be_loaded_stops_the_camera_and_says_so() =>
        await WithPlantedAsync(QrCamera() + """
            (function () {
              var d = Object.getOwnPropertyDescriptor(HTMLScriptElement.prototype, 'src');
              Object.defineProperty(HTMLScriptElement.prototype, 'src', {
                configurable: true, get: d.get,
                set: function (v) { d.set.call(this, /zxing\.min\.js$/.test(String(v)) ? 'zxing-missing.js' : v); }
              });
            })();
            """, async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!document.querySelector('[data-name=gizli] [data-focus=scan]').hidden", "the Scan button");

            await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] [data-focus=scan]').click(); true");
            await _site.WaitAsync("document.querySelector('[data-name=gizli] [aria-live]').textContent === 'The code reader could not be loaded. Connect once to download it, or type the code instead.'",
                "the reader failure said in words", seconds: 20);
            Assert.False(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] input').hidden"), "The box stayed hidden with no reader.");
            Assert.True(await _site.EvalAsync<bool>("document.activeElement === document.querySelector('[data-name=gizli] input')"));
            await _site.WaitAsync("window.__scan.live === 0", "the camera to be stopped");
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=gizli] .scanner').hidden"));
            return true;
        });

    /// <summary>
    /// Offline is a first-class mode: the service worker keeps the port and the vendored library with the page, so a form
    /// opened with no server at all still scans with ZXing.
    /// </summary>
    [Fact]
    public async Task The_reader_is_kept_with_the_page_and_scans_with_no_server() =>
        await WithPlantedAsync(QrCamera(), async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!!navigator.serviceWorker.controller", "the service worker to take the page", seconds: 20);
            await _site.WaitAsync("caches.keys().then(function (n) { return caches.open(n.filter(function (x) { return x.indexOf('graticula-forms-shell-') === 0; })[0] || 'none'); })"
                + ".then(function (c) { return c.keys(); }).then(function (k) { return k.some(function (r) { return r.url.endsWith('/zxing.min.js'); })"
                + " && k.some(function (r) { return r.url.endsWith('/barcode-reader.js'); }); })", "the reader to be kept", seconds: 20);
            // The person's own step: Download for offline use.
            await _site.EvalAsync<bool>("document.getElementById('download-offline').click(); true");
            await _site.WaitAsync("/^Saved for offline use/.test(document.getElementById('offline-status').textContent)", "the form to be kept on the device", seconds: 20);

            _site.Stop();
            try
            {
                await _site.OpenAsync(id, expectServer: false);
                await _site.WaitAsync("!document.querySelector('[data-name=kod] [data-focus=scan]').hidden", "the Scan button offline", seconds: 20);
                await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=scan]').click(); true");
                await _site.WaitAsync("document.querySelector('[data-name=kod] input').value === 'GRT-4006381333931'", "the code read with no server", seconds: 20);
                Assert.Equal(0, await _site.EvalAsync<int>("window.__scan.live"));
            }
            finally
            {
                _site.Start();
            }

            return true;
        });

    /// <summary>
    /// The vendored file is the one whose integrity was checked (DEPENDENCY-LICENSES.md): byte for byte the package's own
    /// build, so a hand edit or a silent upgrade fails here. And it needs nothing the form page's policy refuses — no
    /// <c>eval</c> and no <c>new Function</c> under <c>script-src 'self'</c> — and fetches nothing.
    /// </summary>
    [Fact]
    public void The_vendored_reader_is_the_pinned_build_and_evaluates_no_strings()
    {
        string path = System.IO.Path.Combine(Wwwroot(), "forms", "zxing.min.js");
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        Assert.Equal(ZxingSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());

        string text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("eval(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new Function", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", text, StringComparison.Ordinal);

        // The port is the only file that names ZXing; the form filler talks to the port.
        string fill = System.IO.File.ReadAllText(System.IO.Path.Combine(Wwwroot(), "forms", "fill.js"));
        Assert.DoesNotMatch(@"ZXing\s*\.|zxing\.min\.js", fill);
        Assert.Contains("'zxing.min.js'", System.IO.File.ReadAllText(System.IO.Path.Combine(Wwwroot(), "forms", "sw.js")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_that_is_not_https_says_scanning_needs_https() =>
        await WithPlantedAsync(NoReader + "Object.defineProperty(window, 'isSecureContext', { get: function () { return false; } });", async () =>
        {
            string id = _site.Publish(Codes());
            await _site.OpenAsync(id);
            await _site.WaitAsync("!document.querySelector('[data-name=kod] [id$=-no-reader]').hidden", "the no-camera hint");
            Assert.Equal("Scanning needs the form to be opened at an https address. Type the code instead.",
                await _site.EvalAsync<string>("document.querySelector('[data-name=kod] [id$=-no-reader]').textContent"));
            Assert.True(await _site.EvalAsync<bool>("document.querySelector('[data-name=kod] [data-focus=scan]').hidden"));
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
