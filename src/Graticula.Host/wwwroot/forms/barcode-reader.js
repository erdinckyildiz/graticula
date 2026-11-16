/*
  The form client's barcode reader — ADR-287 §5.22, the port the barcode question talks to.

  One function, GraticulaBarcode.reader(), answers a promise of:
    - null, where the page has no camera API (navigator.mediaDevices.getUserMedia), which is every page that is not
      a secure context (http:// other than localhost) and a browser with no camera support at all; or
    - a reader: { engine, formats, prepare(), detect(source) }.
      engine   'browser' where the browser has a reader of its own (the BarcodeDetector Web API with at least one
               format: Chrome and Edge on Android, ChromeOS and macOS), 'zxing' everywhere else (Safari on iPhone and
               iPad, Firefox, Chrome on Windows and Linux).
      formats  the formats it reads, in BarcodeDetector's names ('qr_code', 'ean_13', ...).
      prepare  a promise that settles once the reader can be asked: at once for the browser's own; for ZXing it loads
               the library and rejects if it cannot be loaded.
      detect   a promise of the first code's text in a <video> or <canvas>, or null where none was found in it.

  ZXing is the JavaScript port of the ZXing library, @zxing/library 0.23.0, Apache-2.0, vendored as zxing.min.js
  (DEPENDENCY-LICENSES.md, NOTICE). Tier 2 under build-vs-adopt-policy.md: nothing outside this file names a ZXing type,
  so replacing it touches this file alone. It is loaded only when Scan is pressed on a browser without a reader of its
  own — most forms, and every Android Chrome, never pay its 362 KB — and the service worker keeps it with the page, so
  a form downloaded for offline use scans with no signal.
*/
(function () {
    'use strict';

    var LIBRARY = 'zxing.min.js';

    /** The formats ZXing is asked for, in BarcodeDetector's names, with ZXing's name for each. */
    var ZXING_FORMATS = {
        aztec: 'AZTEC', codabar: 'CODABAR', code_39: 'CODE_39', code_93: 'CODE_93', code_128: 'CODE_128',
        data_matrix: 'DATA_MATRIX', ean_8: 'EAN_8', ean_13: 'EAN_13', itf: 'ITF', pdf417: 'PDF_417',
        qr_code: 'QR_CODE', upc_a: 'UPC_A', upc_e: 'UPC_E'
    };

    /** The longest side a frame is decoded at: enough for a product code held at arm's length, small enough for a phone. */
    var MAX_SIDE = 1280;

    var asked = null;
    var loading = null;

    function hasCamera() {
        return !!(navigator.mediaDevices && typeof navigator.mediaDevices.getUserMedia === 'function');
    }

    function browserFormats() {
        return new Promise(function (resolve) {
            try {
                if (typeof window.BarcodeDetector !== 'function' || typeof window.BarcodeDetector.getSupportedFormats !== 'function') { resolve(null); return; }
                window.BarcodeDetector.getSupportedFormats().then(function (f) {
                    resolve(Array.isArray(f) && f.length ? f : null);
                }, function () { resolve(null); });
            } catch (e) { resolve(null); }
        });
    }

    function browserReader(formats) {
        var detector = null;
        return {
            engine: 'browser',
            formats: formats.slice(),
            prepare: function () {
                try { detector = detector || new window.BarcodeDetector({ formats: formats }); } catch (e) { return Promise.reject(e); }
                return Promise.resolve();
            },
            detect: function (source) {
                return this.prepare().then(function () { return detector.detect(source); }).then(function (found) {
                    var hit = (found || []).filter(function (b) { return b && typeof b.rawValue === 'string' && b.rawValue !== ''; })[0];
                    return hit ? hit.rawValue : null;
                });
            }
        };
    }

    /** The library, once and only when asked; a second ask while it loads waits on the first. */
    function loadLibrary() {
        if (window.ZXing && window.ZXing.MultiFormatReader) { return Promise.resolve(window.ZXing); }
        if (!loading) {
            loading = new Promise(function (resolve, reject) {
                var tag = document.createElement('script');
                tag.src = LIBRARY;
                tag.async = true;
                tag.onload = function () {
                    if (window.ZXing && window.ZXing.MultiFormatReader) { resolve(window.ZXing); } else { reject(new Error('The barcode library did not load.')); }
                };
                tag.onerror = function () { reject(new Error('The barcode library could not be fetched.')); };
                document.head.appendChild(tag);
            });
            // A failure is not kept: the next Scan, with a signal back, tries again.
            loading.catch(function () { loading = null; });
        }
        return loading;
    }

    function zxingReader() {
        var formats = Object.keys(ZXING_FORMATS);
        var Z = null, decoder = null, canvas = null, context = null, buffer = null;
        return {
            engine: 'zxing',
            formats: formats.slice(),
            prepare: function () {
                if (decoder) { return Promise.resolve(); }
                return loadLibrary().then(function (lib) {
                    if (decoder) { return; }
                    Z = lib;
                    var hints = new Map();
                    hints.set(Z.DecodeHintType.POSSIBLE_FORMATS, formats.map(function (f) { return Z.BarcodeFormat[ZXING_FORMATS[f]]; }));
                    decoder = new Z.MultiFormatReader();
                    decoder.setHints(hints);
                });
            },
            detect: function (source) {
                return this.prepare().then(function () {
                    var w = source.videoWidth || source.width || 0;
                    var h = source.videoHeight || source.height || 0;
                    // A video with no frame yet has nothing to read.
                    if (!w || !h || (typeof source.readyState === 'number' && source.readyState < 2)) { return null; }
                    var scale = Math.min(1, MAX_SIDE / Math.max(w, h));
                    var cw = Math.max(1, Math.round(w * scale)), ch = Math.max(1, Math.round(h * scale));
                    if (!canvas) {
                        canvas = document.createElement('canvas');
                        context = canvas.getContext('2d', { willReadFrequently: true });
                    }
                    if (canvas.width !== cw || canvas.height !== ch) { canvas.width = cw; canvas.height = ch; buffer = null; }
                    context.drawImage(source, 0, 0, cw, ch);
                    var luminance = new Z.HTMLCanvasElementLuminanceSource(canvas, false, buffer);
                    buffer = luminance.buffer || buffer;
                    try {
                        var text = decoder.decodeWithState(new Z.BinaryBitmap(new Z.HybridBinarizer(luminance))).getText();
                        return typeof text === 'string' && text !== '' ? text : null;
                    } catch (e) {
                        // Not found, a failed checksum or an unreadable format is "no code in this frame"; anything else
                        // is the reader failing, which the caller says in words.
                        if (Z.Exception && e instanceof Z.Exception) { return null; }
                        throw e;
                    }
                });
            }
        };
    }

    function reader() {
        if (!asked) {
            asked = hasCamera()
                ? browserFormats().then(function (f) { return f ? browserReader(f) : zxingReader(); })
                : Promise.resolve(null);
        }
        return asked;
    }

    window.GraticulaBarcode = {
        reader: reader,
        /** Whether the page is a secure context: where it is not, there is no camera API and the reason is the address. */
        secure: function () { return window.isSecureContext !== false; }
    };
})();
