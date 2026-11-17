/*
  The form client — ADR-287 §5.4 (fill a published form in a browser) and §5.5 (offline: the form kept on the device,
  submissions queued with their files and sent oldest first when the signal returns).

  <b>One global, because classic scripts share one namespace.</b> Everything is inside this function; the only name it
  leaves behind is window.GraticulaFormFill, a small surface the tests read (the outbox, a promise for "drawn").

  <b>Every expression goes through window.GraticulaXlsform</b> (xlsform-eval.js). There is no second evaluator here: this
  file supplies the evaluator's context — value(), current, position, choices, listOf, language, pulldata — and decides
  what to do with the answer.

  <b>The model is a tree of instances.</b> The form's root is an instance; every repeat instance is another, holding its
  own answers and the instances of the repeats inside it. An instance's id is the GlobalID its row is sent with, made
  on the device, so a record that is sent twice lands once (§5.5).

  <b>Drawing and updating are separate.</b> The DOM is built when the structure changes (a page, a repeat instance, the
  language) and otherwise only updated in place — values of calculated questions, visibility, messages — so typing
  never loses the caret. A rebuild puts focus back on the control that had it.
*/
(function () {
    'use strict';

    var X = null; // window.GraticulaXlsform, once the page has loaded

    // ---------------------------------------------------------------------------------------------------------------
    // Small helpers.
    // ---------------------------------------------------------------------------------------------------------------

    function $(id) { return document.getElementById(id); }

    function el(tag, attrs, children) {
        var node = document.createElement(tag);
        if (attrs) {
            Object.keys(attrs).forEach(function (k) {
                var v = attrs[k];
                if (v === null || v === undefined || v === false) { return; }
                if (k === 'text') { node.textContent = v; } else if (k === 'class') { node.className = v; } else { node.setAttribute(k, v === true ? '' : v); }
            });
        }
        (children || []).forEach(function (c) { if (c) { node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); } });
        return node;
    }

    function guid() {
        var raw;
        if (window.crypto && typeof crypto.randomUUID === 'function') {
            raw = crypto.randomUUID();
        } else {
            var b = new Uint8Array(16);
            crypto.getRandomValues(b);
            b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
            var h = Array.prototype.map.call(b, function (x) { return (x + 0x100).toString(16).slice(1); }).join('');
            raw = h.slice(0, 8) + '-' + h.slice(8, 12) + '-' + h.slice(12, 16) + '-' + h.slice(16, 20) + '-' + h.slice(20);
        }
        return '{' + raw.toUpperCase() + '}';
    }

    function pad(n, w) { var s = String(Math.abs(n)); while (s.length < (w || 2)) { s = '0' + s; } return (n < 0 ? '-' : '') + s; }

    function offsetText(date) {
        var minutes = -date.getTimezoneOffset();
        return (minutes >= 0 ? '+' : '-') + pad(Math.floor(Math.abs(minutes) / 60)) + ':' + pad(Math.abs(minutes) % 60);
    }

    function localDate(d) { return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }

    function localDateTime(d) {
        return localDate(d) + 'T' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '.'
            + pad(d.getMilliseconds(), 3) + offsetText(d);
    }

    function isYes(text) { return /^(yes|true|true\(\)|1)$/i.test(String(text || '').trim()); }
    function isNo(text) { return /^(no|false|false\(\)|0)$/i.test(String(text || '').trim()); }

    function has(list, item) { return Array.isArray(list) && list.indexOf(item) >= 0; }

    function isIos() {
        var ua = navigator.userAgent || '';
        return /iPad|iPhone|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The server. A GET carries the session cookie; a write needs the bearer token Studio keeps in sessionStorage.
    // ---------------------------------------------------------------------------------------------------------------

    function token() { try { return sessionStorage.getItem('gis-token'); } catch (e) { return null; } }

    var exchanged = false;

    // Trades the browsing cookie for a token of this tab's own, as webmap.js does (ADR-023 §4c). The cookie is
    // persistent when the person ticked "remember me", so after a restart this is how a queued record gets a token
    // without one ever being written to the device's storage by this page.
    async function exchangeSession() {
        if (exchanged) { return !!token(); }
        exchanged = true;
        try {
            var response = await fetch('/rest/auth/session', { method: 'POST', credentials: 'same-origin' });
            if (!response.ok) { return false; }
            var body = await response.json();
            if (!body || typeof body.token !== 'string' || !body.token) { return false; }
            try { sessionStorage.setItem('gis-token', body.token); } catch (e) { /* private mode */ }
            return true;
        } catch (e) {
            return false;
        }
    }

    function authFailure(status) { return status === 401 || status === 403 || status === 498 || status === 499; }

    // A request answering the parsed body, or throwing an Error with the server's sentence. ArcGIS refusals arrive in
    // a 200 ({error:{code,message}}), so both shapes fail here. A failure to reach the server at all is .network.
    async function api(url, options, retried) {
        var opts = options || {};
        var headers = Object.assign({ Accept: 'application/json' }, opts.headers || {});
        var held = token();
        if (held) { headers.Authorization = 'Bearer ' + held; }
        var response;
        try {
            response = await fetch(url, Object.assign({}, opts, { headers: headers, credentials: 'same-origin' }));
        } catch (e) {
            var lost = new Error('The server could not be reached.');
            lost.network = true;
            throw lost;
        }
        var text = await response.text();
        var body = null;
        try { body = text ? JSON.parse(text) : null; } catch (e) { /* not JSON */ }
        var envelope = body && typeof body === 'object' && body.error;
        if (!response.ok || envelope) {
            var status = (envelope && Number(envelope.code)) || response.status;
            if (authFailure(status) && !retried && (await exchangeSession())) {
                return api(url, options, true);
            }
            var message = (envelope && envelope.message) || (response.status + ' ' + (response.statusText || '')).trim();
            if (envelope && Array.isArray(envelope.details) && envelope.details.length) {
                message += ' ' + envelope.details.join(' ');
            }
            var failure = new Error(message);
            failure.status = status;
            failure.body = body;
            throw failure;
        }
        return body;
    }

    function formBody(fields) {
        var p = new URLSearchParams();
        Object.keys(fields).forEach(function (k) { p.append(k, fields[k]); });
        return p;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The device's storage: IndexedDB, three stores (and a fourth, 'inbox', described where the Inbox is). 'forms' holds a downloaded form; 'outbox' holds records waiting to
    // be sent, each with its files as Blobs; 'drafts' holds forms being filled in (§5.10, ADR-287: the client always
    // keeps drafts), their files as Blobs too. Nothing is removed from the outbox until the server accepted all of it.
    // ---------------------------------------------------------------------------------------------------------------

    var dbPromise = null;

    function openDb() {
        if (dbPromise) { return dbPromise; }
        dbPromise = new Promise(function (resolve) {
            try {
                if (!window.indexedDB) { resolve(null); return; }
                // Version 3 adds 'inbox' (ADR-300 §5.6): the records an Inbox downloaded, one entry per record keyed
                // form|member|GlobalID, and one per form and member keyed form|member| that says when it was refreshed.
                var request = indexedDB.open('graticula-forms', 3);
                request.onupgradeneeded = function () {
                    var db = request.result;
                    if (!db.objectStoreNames.contains('forms')) { db.createObjectStore('forms', { keyPath: 'id' }); }
                    if (!db.objectStoreNames.contains('outbox')) { db.createObjectStore('outbox', { keyPath: 'key', autoIncrement: true }); }
                    if (!db.objectStoreNames.contains('drafts')) { db.createObjectStore('drafts', { keyPath: 'id' }); }
                    if (!db.objectStoreNames.contains('inbox')) { db.createObjectStore('inbox', { keyPath: 'key' }); }
                };
                request.onsuccess = function () {
                    // A tab still open on an older version must not hold up this one's upgrade, nor this one's the next.
                    request.result.onversionchange = function () { request.result.close(); dbPromise = null; };
                    resolve(request.result);
                };
                request.onerror = function () { resolve(null); };
                request.onblocked = function () { resolve(null); };
            } catch (e) {
                resolve(null);
            }
        });
        return dbPromise;
    }

    async function dbDo(store, mode, work) {
        var db = await openDb();
        if (!db) { return undefined; }
        return new Promise(function (resolve, reject) {
            var tx = db.transaction(store, mode);
            var result;
            var request = work(tx.objectStore(store));
            if (request) { request.onsuccess = function () { result = request.result; }; }
            tx.oncomplete = function () { resolve(result); };
            tx.onerror = function () { reject(tx.error); };
            tx.onabort = function () { reject(tx.error); };
        });
    }

    function dbGet(store, key) { return dbDo(store, 'readonly', function (s) { return s.get(key); }); }
    function dbPut(store, value) { return dbDo(store, 'readwrite', function (s) { return s.put(value); }); }
    function dbDelete(store, key) { return dbDo(store, 'readwrite', function (s) { return s.delete(key); }); }
    function dbAll(store) { return dbDo(store, 'readonly', function (s) { return s.getAll(); }).then(function (r) { return r || []; }); }

    // ---------------------------------------------------------------------------------------------------------------
    // The form: its definition, the questions indexed by name, the choice lists as the evaluator reads them.
    // ---------------------------------------------------------------------------------------------------------------

    var itemId = null;
    var doc = null;          // { id, title, form, publication }
    var form = null;         // doc.form
    var defs = {};           // name → { node, repeats: [names of the repeats it sits in, outermost first] }
    var files = {};          // CSV name → { rows, text } for *_from_file and pulldata
    var missingFiles = [];   // CSV names the item does not have
    var lang = '';
    // <b>The page's own words follow the form's language</b> (UX review 2026-11-09: with Türkçe chosen, every button,
    // message and the progress line stayed English, and "Add Oda" mixed the two). Turkish is the only translation so
    // far; any other language keeps English. Keys are the English text, so an untranslated string stays English.
    var TR = {
        "This answer is required.": "Bu soru zorunlu.",
        "This answer is not allowed.": "Bu cevap kabul edilmiyor.",
        "Enter a whole number.": "Tam sayı girin.",
        "Enter a number.": "Sayı girin.",
        "Enter a latitude between -90 and 90 and a longitude between -180 and 180.": "-90 ile 90 arasında bir enlem ve -180 ile 180 arasında bir boylam girin.",
        "Type a latitude between -90 and 90 and a longitude between -180 and 180.": "-90 ile 90 arasında bir enlem ve -180 ile 180 arasında bir boylam yazın.",
        "One answer needs attention": "Bir cevaba bakmanız gerekiyor",
        "Latitude": "Enlem",
        "Longitude": "Boylam",
        "Use my location": "Konumumu kullan",
        "Finding your location…": "Konumunuz bulunuyor…",
        "Location permission was refused.": "Konum izni verilmedi.",
        "The location could not be found.": "Konum bulunamadı.",
        "This browser cannot tell its location.": "Bu tarayıcı konumunu bildiremiyor.",
        "Clear signature": "İmzayı temizle",
        "Signature captured.": "İmza alındı.",
        "Signature cleared.": "İmza temizlendi.",
        "Calculate": "Hesapla",
        "Calculated.": "Hesaplandı.",
        "Choose an answer above first.": "Önce yukarıdaki soruyu cevaplayın.",
        "Nothing to calculate yet: answer the questions it uses first.": "Henüz hesaplanacak bir şey yok: önce kullandığı soruları cevaplayın.",
        "Clear drawing": "Çizimi temizle",
        "Drawing captured.": "Çizim alındı.",
        "Drawing cleared.": "Çizim temizlendi.",
        "Drawing needs a mouse, a pen or a finger. It cannot be done with the keyboard alone.": "Çizim için fare, kalem ya da parmak gerekir. Yalnızca klavyeyle yapılamaz.",
        "Clear": "Temizle",
        "Remove": "Kaldır",
        "Add my position": "Konumumu ekle",
        "Add typed point": "Yazılan noktayı ekle",
        "Remove last point": "Son noktayı kaldır",
        "OK": "Tamam",
        "Discard": "Sil",
        "Try again": "Yeniden dene",
        "Sign in": "Giriş yap",
        "Not sent": "Gönderilmedi",
        "Nothing is waiting to be sent.": "Gönderilmeyi bekleyen kayıt yok.",
        "Forms on this device": "Bu cihazdaki formlar",
        "No form has been downloaded for offline use. Open a form from its link first.": "Çevrimdışı kullanım için indirilmiş form yok. Önce bir formu bağlantısından açın.",
        "Your answers were sent.": "Cevaplarınız gönderildi.",
        "Sending…": "Gönderiliyor…",
        "Saved on this device": "Bu cihaza kaydedildi",
        "Saved on this device. It will be sent when you are back online.": "Bu cihaza kaydedildi. Bağlantı gelince gönderilecek.",
        "Saved on this device. Sign in, then send it from the Outbox.": "Bu cihaza kaydedildi. Giriş yapın, sonra Giden kutusundan gönderin.",
        "Saving the form on this device…": "Form bu cihaza kaydediliyor…",
        "Saved for offline use": "Çevrimdışı kullanım için kaydedildi",
        "Waiting for a connection.": "Bağlantı bekleniyor.",
        "Waiting to be sent.": "Gönderilmeyi bekliyor.",
        "Waiting: sign in to send it.": "Bekliyor: göndermek için giriş yapın.",
        "Sign in to send this record.": "Bu kaydı göndermek için giriş yapın.",
        "You are offline. Records you submit are kept on this device and sent when the connection returns.": "Çevrimdışısınız. Gönderdiğiniz kayıtlar bu cihazda tutulur ve bağlantı gelince gönderilir.",
        "The server could not be reached.": "Sunucuya ulaşılamadı.",
        "The server could not be reached. Your answers are saved on this device and will be sent when the connection returns.": "Sunucuya ulaşılamadı. Cevaplarınız bu cihaza kaydedildi ve bağlantı gelince gönderilecek.",
        "The server could not be reached, and this browser cannot keep the record for later. Try again when you are online.": "Sunucuya ulaşılamadı ve bu tarayıcı kaydı sonraya saklayamıyor. Çevrimiçi olunca yeniden deneyin.",
        "The server refused this record.": "Sunucu bu kaydı kabul etmedi.",
        "The attachment was refused.": "Ek kabul edilmedi.",
        "This form is not published yet, so there is nowhere to send it.": "Bu form henüz yayımlanmadı, cevapların gönderileceği yer yok.",
        "This form is not published yet. You can look at it, but answers cannot be sent.": "Bu form henüz yayımlanmadı. İnceleyebilirsiniz ama cevaplar gönderilemez.",
        "A preview of the form as it is being designed. Answers are not sent.": "Formun tasarlanmakta olan hâlinin önizlemesi. Cevaplar gönderilmez.",
        "Zoom in close and tap the map to place the point; tap again to move it. Two fingers move the map.": "Yakınlaşın ve noktayı yerleştirmek için haritaya dokunun; taşımak için yeniden dokunun. Haritayı iki parmakla kaydırın.",
        "Zoom in close and tap the map to add each point in turn. Two fingers move the map.": "Yakınlaşın ve noktaları sırayla eklemek için haritaya dokunun. Haritayı iki parmakla kaydırın.",
        "Map of the point. Use the boxes below to type it.": "Noktanın haritası. Yazmak için aşağıdaki kutuları kullanın.",
        "Map of the points. Use the boxes below to type them.": "Noktaların haritası. Yazmak için aşağıdaki kutuları kullanın.",
        "Zoomed in. Tap again to place it.": "Yakınlaştırıldı. Yerleştirmek için yeniden dokunun.",
        "Zoom in": "Yakınlaştır",
        "Zoom out": "Uzaklaştır",
        "Points": "Noktalar",
        "Your location could not be found. You can type the coordinates instead.": "Konumunuz bulunamadı. Koordinatları yazabilirsiniz.",
        "The map could not be loaded; use the boxes below.": "Harita yüklenemedi; aşağıdaki kutuları kullanın.",
        "Opened from this device; the server could not be reached.": "Bu cihazdan açıldı; sunucuya ulaşılamadı.",
        "The form could not be loaded: you are offline and it has not been downloaded to this device.": "Form yüklenemedi: çevrimdışısınız ve form bu cihaza indirilmemiş.",
        "Discard this record? It has not been accepted by the server and cannot be recovered.": "Bu kayıt silinsin mi? Sunucu henüz kabul etmedi ve geri getirilemez.",
        "Untitled draft": "Adsız taslak",
        "Draft saved on this device.": "Taslak bu cihaza kaydedildi.",
        "This browser could not keep the draft: ": "Bu tarayıcı taslağı saklayamadı: ",
        "No drafts for this form.": "Bu form için taslak yok.",
        "Open now": "Şu an açık",
        "(started in an earlier version of the form)": "(formun önceki bir sürümünde başlandı)",
        "Resume": "Devam et",
        "Discard this draft? It cannot be recovered.": "Bu taslak silinsin mi? Geri getirilemez.",
        "Draft discarded.": "Taslak silindi.",
        "That draft is no longer on this device.": "Bu taslak artık bu cihazda yok.",
        "Draft resumed.": "Taslağa devam ediliyor.",
        "It was started in an earlier version of the form; answers to questions that no longer exist were left out.": "Formun önceki bir sürümünde başlanmıştı; artık olmayan sorulara verilen cevaplar dışarıda bırakıldı.",
        "New form started. The earlier draft is kept under Drafts.": "Yeni form başlatıldı. Önceki taslak Taslaklar'da duruyor.",
        "More help": "Daha fazla yardım",
        "Question": "Soru",
        "A field parameter needs a question name after field:.": "field: parametresinden sonra bir soru adı gerekir.",
        "Not a part of the page that can be hidden: ": "Gizlenebilen bir sayfa parçası değil: ",
        "Write it as latitude,longitude: a latitude between -90 and 90, then a longitude between -180 and 180.": "enlem,boylam olarak yazın: -90 ile 90 arasında bir enlem, sonra -180 ile 180 arasında bir boylam.",
        "Where people are sent after submitting is set in the form by its publisher, not by a link.": "Gönderdikten sonra kişilerin nereye yönlendirileceğini bağlantı değil, yayımlayan formda belirler.",
        "This form has no question with that name.": "Bu formda bu adda bir soru yok.",
        "That is a group or a repeat, not a question.": "Bu bir grup ya da tekrar, soru değil.",
        "That question sits in a repeat, which a link cannot fill.": "Bu soru bir tekrarın içinde; bağlantı onu dolduramaz.",
        "That answer is worked out from others, so a link cannot set it.": "Bu cevap diğerlerinden hesaplanıyor; bağlantı onu belirleyemez.",
        "Outside the range this question allows.": "Bu sorunun izin verdiği aralığın dışında.",
        "The choices of this question are not available.": "Bu sorunun seçenekleri kullanılamıyor.",
        "Not one of this question’s choices: ": "Bu sorunun seçeneklerinden biri değil: ",
        "Write the date as YYYY-MM-DD.": "Tarihi YYYY-AA-GG olarak yazın.",
        "Write the time as HH:MM.": "Saati SS:DD olarak yazın.",
        "Write the date and time as YYYY-MM-DDTHH:MM.": "Tarih ve saati YYYY-AA-GGTSS:DD olarak yazın.",
        "Write the place as latitude,longitude.": "Yeri enlem,boylam olarak yazın.",
        "Use yes or no.": "Evet ya da hayır kullanın.",
        "A link cannot fill this kind of question.": "Bağlantı bu tür bir soruyu dolduramaz.",
        "You stayed on this page.": "Bu sayfada kaldınız.",
        "There is no map to tap here. Type the latitude and longitude below, or use your location.": "Burada dokunulacak bir harita yok. Enlem ve boylamı aşağıya yazın ya da konumunuzu kullanın.",
        "There is no map to tap here. Type each point below, or add your position.": "Burada dokunulacak bir harita yok. Her noktayı aşağıya yazın ya da konumunuzu ekleyin.",
        "Picture not available: ": "Resim bulunamadı: ",
        "Scan": "Tara",
        "Cancel scan": "Taramayı iptal et",
        "Camera preview": "Kamera önizlemesi",
        "Opening the camera…": "Kamera açılıyor…",
        "Point the camera at the code.": "Kamerayı koda doğru tutun.",
        "Code read.": "Kod okundu.",
        "Code read: ": "Okunan kod: ",
        "Scan cancelled.": "Tarama iptal edildi.",
        "Scanning stopped because the page was left.": "Sayfadan ayrıldığınız için tarama durdu.",
        "This browser cannot scan codes. Type the code instead.": "Bu tarayıcı kod okuyamıyor. Kodu elle yazın.",
        "Camera permission was refused. Type the code instead.": "Kamera izni verilmedi. Kodu elle yazın.",
        "No camera was found. Type the code instead.": "Kamera bulunamadı. Kodu elle yazın.",
        "The camera could not be started. Type the code instead.": "Kamera açılamadı. Kodu elle yazın.",
        "The code could not be read. Type the code instead.": "Kod okunamadı. Kodu elle yazın.",
        "The code reader could not be loaded. Connect once to download it, or type the code instead.": "Kod okuyucu yüklenemedi. İndirmek için bir kez internete bağlanın ya da kodu elle yazın.",
        "Scanning needs the form to be opened at an https address. Type the code instead.": "Tarama için form https ile başlayan bir adresten açılmalı. Kodu elle yazın.",
        // ADR-287 section 5.26
        "Record": "Ses kaydet",
        "Stop recording": "Kaydı durdur",
        "Longest recording: ": "En uzun kayıt: ",
        "Opening the microphone…": "Mikrofon açılıyor…",
        "Recording. Press Stop recording to finish.": "Kayıt sürüyor. Bitirmek için Kaydı durdur'a basın.",
        "Recording saved.": "Kayıt eklendi.",
        "Recording stopped at the time limit and saved.": "Süre dolduğu için kayıt durdu ve eklendi.",
        "Recording stopped because the page was left, and saved.": "Sayfadan ayrıldığınız için kayıt durdu ve eklendi.",
        "Nothing was recorded.": "Hiçbir şey kaydedilmedi.",
        "Microphone permission was refused. Choose a recording instead.": "Mikrofon izni verilmedi. Bunun yerine bir ses dosyası seçin.",
        "No microphone was found. Choose a recording instead.": "Mikrofon bulunamadı. Bunun yerine bir ses dosyası seçin.",
        "The microphone could not be started. Choose a recording instead.": "Mikrofon açılamadı. Bunun yerine bir ses dosyası seçin.",
        "This browser cannot record. Choose a recording instead.": "Bu tarayıcı ses kaydedemiyor. Bunun yerine bir ses dosyası seçin.",
        "Recording needs the form to be opened at an https address. Choose a recording instead.": "Ses kaydı için form https ile başlayan bir adresten açılmalı. Bunun yerine bir ses dosyası seçin.",
        "Recording not available: ": "Ses kaydı bulunamadı: ",
        "Recording: ": "Ses kaydı: ",
        "0 is none at all and 10 is the most.": "0 hiç, 10 en çok anlamına gelir.",
        "Check this answer.": "Bu cevabı bir daha kontrol edin.",
        "Warning:": "Uyarı:",
        "Remove: ": "Kaldır: "
    };
    var STATIC_TEXT = {"back": ["Back", "Geri"], "next": ["Next", "İleri"], "submit": ["Submit", "Gönder"], "new-form": ["Fill in a new form", "Yeni form doldur"], "send-now": ["Send now", "Şimdi gönder"], "download-offline": ["Download for offline use", "Çevrimdışı kullanım için indir"], "outbox-heading": ["Outbox", "Giden kutusu"], "drafts-heading": ["Drafts", "Taslaklar"], "link-notices-title": ["Some settings in this link were not used", "Bu bağlantıdaki bazı ayarlar kullanılmadı"], "link-notices-dismiss": ["Dismiss", "Kapat"], "redirect-go": ["Go now", "Şimdi git"], "redirect-stay": ["Stay on this page", "Bu sayfada kal"], "draft-new": ["Start a new form", "Yeni form başlat"], "draft-discard": ["Discard this draft", "Bu taslağı sil"], "drafts-hint": ["Answers are kept on this device as you fill in the form. A draft is removed when its record is queued to be sent.", "Cevaplar, formu doldurdukça bu cihazda tutulur. Bir taslak, kaydı gönderilmek üzere kuyruğa alınınca silinir."], "done-heading": ["Submitted", "Gönderildi"], "ios-note": ["Offline on iPhone is not yet verified. Install this page to the Home Screen before relying on it without signal.", "iPhone’da çevrimdışı çalışma henüz doğrulanmadı. Sinyalsiz kullanmadan önce bu sayfayı Ana Ekran’a ekleyin."]};
    // Words of the 2026-11-15 compatibility work (ADR-287 §5.19), kept together so they stay apart from the list above.
    Object.assign(TR, {
        "Enter a year and a week number that exists.": "Var olan bir yıl ve hafta numarası girin.",
        "Use the pattern ": "Şu kalıbı kullanın: ",
        "For example: ": "Örnek: ",
        "Increase": "Artır",
        "Decrease": "Azalt",
        "Year": "Yıl",
        "Week number": "Hafta numarası",
        "needs attention": "bakmanız gerekiyor",
        "Show": "Göster",
        "No choices match.": "Eşleşen seçenek yok.",
        "Loading the choices…": "Seçenekler yükleniyor…",
        "These choices come from the server and need a connection. You can still submit the form; this question can be answered later.": "Bu seçenekler sunucudan geliyor ve bağlantı gerektiriyor. Formu yine de gönderebilirsiniz; bu soru daha sonra cevaplanabilir.",
        "These choices come from a layer of another server, which this form does not query.": "Bu seçenekler başka bir sunucunun katmanından geliyor; bu form o sunucuya sorgu göndermez.",
        "The choices could not be loaded from the layer.": "Seçenekler katmandan yüklenemedi.",
        "Looking this up…": "Sorgulanıyor…",
        "Required, but left empty because it needed a connection: ": "Zorunluydu ama bağlantı gerektirdiği için boş kaldı: ",
        "This answer comes from the server and needs a connection. It fills in by itself when you are back online; if you submit before then, it is left empty.": "Bu cevap sunucudan geliyor ve bağlantı gerektiriyor. Bağlantı gelince kendiliğinden dolar; daha önce gönderirseniz boş kalır.",
        "This answer would be looked up in a layer of another server, which this form does not query.": "Bu cevabın sorgulanacağı katman başka bir sunucunun; bu form o sunucuya sorgu göndermez.",
        "This answer could not be looked up: the layer could not be read.": "Bu cevap sorgulanamadı: katman okunamadı.",
        "Nothing was found for this answer.": "Bu cevap için bir kayıt bulunamadı.",
        "Type to narrow the choices.": "Seçenekleri daraltmak için yazın.",
        "selected": "seçildi",
        "Only the first 100 are listed; keep typing to narrow them.": "Yalnız ilk 100 seçenek listeleniyor; daraltmak için yazmaya devam edin.",
        "Choices": "Seçenekler"
    });
    // Words of the Inbox (ADR-300, 2026-11-16).
    Object.assign(STATIC_TEXT, {
        "inbox-heading": ["Inbox", "Gelen kutusu"],
        "inbox-refresh": ["Refresh", "Yenile"],
        "inbox-hint": ["Records you can open and change. Refresh downloads them to this device; once downloaded they open and can be changed without a signal, and your changes wait in the Outbox.", "Açıp değiştirebileceğiniz kayıtlar. Yenile'ye basınca bu cihaza inerler; sonra sinyal olmasa da açıp değiştirebilirsiniz. Değişiklikleriniz Giden kutusunda bekler."],
        "inbox-waiting-why": ["A record whose changes are waiting in the Outbox opens again once they are sent or discarded.", "Değişiklikleri Giden kutusunda bekleyen kayıt, değişiklikler gönderilince ya da silinince yeniden açılabilir."],
        "compare-heading": ["Changed on the server since you downloaded it", "İndirdiğinizden beri sunucuda değişti"],
        "compare-mine": ["Keep mine", "Benimki kalsın"],
        "compare-server": ["Use the server’s", "Sunucudaki kalsın"],
        "compare-close": ["Close", "Kapat"],
        "answers-heading": ["No longer on the server", "Artık sunucuda yok"],
        "answers-hint": ["The record was deleted on the server, or is no longer one the Inbox gives you, after you downloaded it. Your answers are kept on this device: copy them before you discard the record.", "Siz indirdikten sonra kayıt sunucuda silindi ya da artık Gelen kutunuza gelmiyor. Cevaplarınız bu cihazda duruyor; kaydı silmeden önce kopyalayın."],
        "answers-copy": ["Copy answers", "Cevapları kopyala"],
        "answers-close": ["Close", "Kapat"]
    });
    Object.assign(TR, {
        "On the server": "Sunucuda",
        "On the server; cannot be changed here": "Sunucuda; burada değiştirilemez",
        "(shown when online)": "(çevrimiçiyken gösterilir)",
        "Already on the server. These cannot be changed or removed here; new files are added beside them.": "Zaten sunucuda. Bunlar burada değiştirilemez ya da kaldırılamaz; yeni dosyalar yanlarına eklenir.",
        "Downloading the records…": "Kayıtlar indiriliyor…",
        "The names of their attachments could not be read.": "Eklerinin adları okunamadı.",
        "You are offline. The records already on this device are shown.": "Çevrimdışısınız. Bu cihazda zaten bulunan kayıtlar gösteriliyor.",
        "Sign in to refresh the Inbox.": "Gelen kutusunu yenilemek için giriş yapın.",
        "The Inbox could not be refreshed: ": "Gelen kutusu yenilenemedi: ",
        "Nothing downloaded to this device yet. Refresh to download the records.": "Bu cihaza henüz bir şey indirilmedi. Kayıtları indirmek için Yenile'ye basın.",
        "No records on this device.": "Bu cihazda kayıt yok.",
        "No record matches the search.": "Aramayla eşleşen kayıt yok.",
        "No longer on the server.": "Artık sunucuda yok.",
        "Changed on the server since you downloaded it.": "İndirdiğinizden beri sunucuda değişti.",
        "Your changes are waiting in the Outbox.": "Değişiklikleriniz Giden kutusunda bekliyor.",
        "Show answers": "Cevapları göster",
        "Compare": "Karşılaştır",
        "Open": "Aç",
        "Your unsent changes to this record were reopened.": "Bu kayıtta gönderilmemiş değişiklikleriniz yeniden açıldı.",
        "Record opened from the Inbox.": "Kayıt Gelen kutusundan açıldı.",
        "The server did not answer for every changed row.": "Sunucu, değiştirilen satırların hepsine cevap vermedi.",
        "Yours": "Sizinki",
        "differs": "farklı",
        "Keep the server’s answers where the server changed them? Your changes to the other answers are still sent.": "Sunucuda değişen cevaplarda sunucudaki kalsın mı? Öteki cevaplardaki değişiklikleriniz yine gönderilir.",
        "Sending your version again…": "Sizinki yeniden gönderiliyor…",
        "Keeping the server’s version…": "Sunucudaki korunuyor…",
        "Your version was sent.": "Sizinki gönderildi.",
        "The server’s version was kept. Anything else you added was sent.": "Sunucudaki kaldı. Eklediğiniz yeni satırlar ve resimler gönderildi.",
        "The server’s version was kept.": "Sunucudaki kaldı.",
        "The server’s answers were kept where it changed them, and your other changes were sent.": "Sunucunun değiştirdiği cevaplarda sunucudaki kaldı; öteki değişiklikleriniz gönderildi.",
        "Done": "Tamamlandı",
        "It was changed on the server again. Compare once more.": "Sunucuda yeniden değişti. Bir kez daha karşılaştırın.",
        "It is no longer on the server. Your answers are kept in the Outbox.": "Artık sunucuda yok. Cevaplarınız Giden kutusunda duruyor.",
        "The answers were copied.": "Cevaplar kopyalandı.",
        "This browser would not copy them; they are selected for you to copy.": "Bu tarayıcı kopyalayamadı; cevaplar sizin kopyalamanız için seçili.",
        "Changes to: ": "Değişiklik: ",
        "Changed on the server since you downloaded it. Compare the two and choose which to keep.": "İndirdiğinizden beri sunucuda değişti. İkisini karşılaştırıp hangisinin kalacağını seçin.",
        "No longer on the server. Your answers are kept here: show them to copy them.": "Artık sunucuda yok. Cevaplarınız burada duruyor; kopyalamak için Cevapları göster'e basın.",
        "Open its form to compare.": "Karşılaştırmak için formunu açın.",
        "Discard this record? It is no longer on the server, and your answers cannot be recovered.": "Bu kayıt silinsin mi? Artık sunucuda yok ve cevaplarınız geri getirilemez.",
        "Your changes were sent.": "Değişiklikleriniz gönderildi.",
        "Not sent yet: the record was changed on the server since you downloaded it. Your changes are kept in the Outbox.": "Henüz gönderilmedi: kayıt siz indirdikten sonra sunucuda değişti. Değişiklikleriniz Giden kutusunda duruyor.",
        "Not sent: the record is no longer on the server. Your answers are kept on this device, in the Outbox, to read and copy.": "Gönderilmedi: kayıt artık sunucuda yok. Cevaplarınız bu cihazda, Giden kutusunda duruyor; oradan okuyup kopyalayabilirsiniz.",
        "The server refused the answers: ": "Sunucu cevapları kabul etmedi: ",
        "differs: you changed it": "farklı: siz değiştirdiniz",
        "differs: changed on the server, kept either way": "farklı: sunucuda değişti, her iki durumda da korunur",
        "differs: changed by you and on the server": "farklı: hem sizde hem sunucuda değişti",
        "Keep mine sends the answers you changed; answers changed only on the server stay as the server has them. Use the server’s keeps the server’s answer wherever the server changed it; your changes to the other answers, and new rows and pictures, are still sent.":
            "Benimki kalsın, sizin değiştirdiğiniz cevapları gönderir; yalnız sunucuda değişen cevaplar sunucudaki gibi kalır. Sunucudaki kalsın, sunucunun değiştirdiği her cevapta sunucudakini bırakır; öteki cevaplardaki değişiklikleriniz, yeni satırlar ve resimler yine gönderilir.",
        "If this form is shared with your organization, sign in and open this link again.": "Form kuruluşunuzla paylaşıldıysa giriş yapın ve bu bağlantıyı yeniden açın."
    });
    // A form whose labels name no language follows the browser for the page's own words, as Survey123's web app does —
    // owner decision 2026-11-16 (ADR-300 §5.12). A form that names its language keeps it, whatever the browser says.
    function namesLanguage(l) { return /\([a-z]{2,3}\)|türkçe|turkish|english|ingilizce/i.test(l || ''); }
    function browserIsTurkish() {
        var said = (navigator.languages && navigator.languages[0]) || navigator.language || '';
        return /^tr\b/i.test(said);
    }
    function isTurkish() { return namesLanguage(lang) ? /\(tr\)|türkçe|turkish/i.test(lang) : browserIsTurkish(); }
    function t(s) { return isTurkish() && Object.prototype.hasOwnProperty.call(TR, s) ? TR[s] : s; }
    function applyStaticText() {
        Object.keys(STATIC_TEXT).forEach(function (id) {
            var node = document.getElementById(id);
            if (node) { node.textContent = STATIC_TEXT[id][isTurkish() ? 1 : 0]; }
        });
        var skip = document.querySelector('a.skip');
        if (skip) { skip.textContent = isTurkish() ? 'Forma geç' : 'Skip to the form'; }
        var languageLabel = document.querySelector('label[for=language] > span, .language > span');
        if (languageLabel) { languageLabel.textContent = isTurkish() ? 'Dil' : 'Language'; }
        var draftsToggle = document.getElementById('drafts-toggle');
        if (draftsToggle && draftsToggle.firstChild && draftsToggle.firstChild.nodeType === 3) { draftsToggle.firstChild.nodeValue = isTurkish() ? 'Taslaklar ' : 'Drafts '; }
        var toggle = document.getElementById('outbox-toggle');
        if (toggle && toggle.firstChild && toggle.firstChild.nodeType === 3) { toggle.firstChild.nodeValue = isTurkish() ? 'Giden kutusu ' : 'Outbox '; }
        var inboxToggle = document.getElementById('inbox-toggle');
        if (inboxToggle && inboxToggle.firstChild && inboxToggle.firstChild.nodeType === 3) { inboxToggle.firstChild.nodeValue = isTurkish() ? 'Gelen kutusu ' : 'Inbox '; }
        var searchLabel = document.querySelector('label[for=inbox-search]');
        if (searchLabel) { searchLabel.textContent = isTurkish() ? 'Ara' : 'Search'; }
    }
    var me = { name: '', email: '', fullName: '', firstName: '', lastName: '' };
    var root = null;         // the root instance
    var pageIndex = 0;
    var showAll = false;     // after a submit or Next was attempted, every message is shown

    function index(node, repeats) {
        (node.body || []).forEach(function (child) {
            if (child.kind === 'question') {
                defs[child.name] = { node: child, repeats: repeats };
            } else if (child.kind === 'group') {
                defs[child.name] = { node: child, repeats: repeats };
                index(child, repeats);
            } else if (child.kind === 'repeat') {
                defs[child.name] = { node: child, repeats: repeats };
                index(child, repeats.concat([child.name]));
            }
        });
    }

    function text(labels, fallback) {
        if (!labels) { return fallback || ''; }
        if (lang && labels[lang]) { return labels[lang]; }
        if (labels['']) { return labels['']; }
        var keys = Object.keys(labels);
        for (var i = 0; i < keys.length; i++) { if (labels[keys[i]]) { return labels[keys[i]]; } }
        return fallback || '';
    }

    function choiceRows(listName) {
        if (form.choices && Object.prototype.hasOwnProperty.call(form.choices, listName)) {
            return form.choices[listName].map(function (c) {
                var row = { name: c.name };
                if (c.auto) { row.__auto = true; } // the Other that or_other adds is offered whatever choice_filter says
                Object.keys(c.labels || {}).forEach(function (k) { row[k === '' ? 'label' : 'label::' + k] = c.labels[k]; });
                Object.keys(c.images || {}).forEach(function (k) { row[k === '' ? 'image' : 'image::' + k] = c.images[k]; });
                Object.keys(c.audio || {}).forEach(function (k) { row[k === '' ? 'audio' : 'audio::' + k] = c.audio[k]; });
                Object.keys(c.filters || {}).forEach(function (k) { row[k] = c.filters[k]; });
                return row;
            });
        }
        if (files[listName]) { return files[listName].rows; }
        return null;
    }

    function choicePicture(row) {
        if (lang && row['image::' + lang]) { return String(row['image::' + lang]); }
        if (row.image) { return String(row.image); }
        var keys = Object.keys(row);
        for (var i = 0; i < keys.length; i++) { if (keys[i].indexOf('image::') === 0 && row[keys[i]]) { return String(row[keys[i]]); } }
        return '';
    }

    function choiceLabel(row) {
        if (lang && row['label::' + lang] !== undefined && row['label::' + lang] !== '') { return String(row['label::' + lang]); }
        if (row.label !== undefined && row.label !== '') { return String(row.label); }
        var keys = Object.keys(row);
        for (var i = 0; i < keys.length; i++) { if (keys[i].indexOf('label::') === 0 && row[keys[i]]) { return String(row[keys[i]]); } }
        return String(row.name);
    }

    // RFC 4180, enough of it: quoted fields, doubled quotes, CRLF or LF.
    function parseCsv(textIn) {
        var rows = [], row = [], field = '', quoted = false, i = 0, s = String(textIn || '').replace(/^\uFEFF/, '');
        while (i < s.length) {
            var c = s.charAt(i);
            if (quoted) {
                if (c === '"' && s.charAt(i + 1) === '"') { field += '"'; i += 2; continue; }
                if (c === '"') { quoted = false; i++; continue; }
                field += c; i++; continue;
            }
            if (c === '"') { quoted = true; i++; continue; }
            if (c === ',') { row.push(field); field = ''; i++; continue; }
            if (c === '\r') { i++; continue; }
            if (c === '\n') { row.push(field); rows.push(row); row = []; field = ''; i++; continue; }
            field += c; i++;
        }
        if (field !== '' || row.length) { row.push(field); rows.push(row); }
        if (!rows.length) { return []; }
        var head = rows[0].map(function (h) { return h.trim(); });
        return rows.slice(1).filter(function (r) { return r.some(function (v) { return v !== ''; }); }).map(function (r) {
            var o = {};
            head.forEach(function (h, k) { o[h] = r[k] === undefined ? '' : r[k]; });
            return o;
        });
    }

    function csvNamesOf(formIn) {
        var names = [];
        (function walk(node) {
            (node.body || []).forEach(function (n) {
                if (n.kind === 'question') {
                    if (n.file && names.indexOf(n.file) < 0) { names.push(n.file); }
                    // A search() over a CSV reads it from the item's files, and it is kept on the device with them (section 5.23).
                    if (n.search && n.search.file && names.indexOf(n.search.file) < 0) { names.push(n.search.file); }
                    [n.calculation, n.relevant, n.constraint, n.default].forEach(function (e) {
                        var re = /pulldata\s*\(\s*['"]([^'"@]+)['"]/g, m;
                        while (e && (m = re.exec(e))) { var f = /\.csv$/i.test(m[1]) ? m[1] : m[1] + '.csv'; if (names.indexOf(f) < 0) { names.push(f); } }
                    });
                } else { walk(n); }
            });
        })(formIn.root);
        return names;
    }

    // A CSV uploaded with the form is one of the item's resources (ADR-264), read through the portal's resources
    // endpoint. Tried at its own name and under media/, the folder Survey123 keeps such files in.
    async function fetchCsv(name) {
        var candidates = [name, 'media/' + name];
        for (var i = 0; i < candidates.length; i++) {
            try {
                var headers = {};
                var held = token();
                if (held) { headers.Authorization = 'Bearer ' + held; }
                var response = await fetch('/sharing/rest/content/items/' + itemId + '/resources/' + encodeURI(candidates[i]),
                    { headers: headers, credentials: 'same-origin' });
                if (!response.ok) { continue; }
                var type = response.headers.get('content-type') || '';
                var body = await response.text();
                if (/json/i.test(type) && /"error"/.test(body)) { continue; }
                return body;
            } catch (e) {
                return null;
            }
        }
        return null;
    }

    function pulldata(file, column, keyColumn, key) {
        var f = files[file] || files[file + '.csv'];
        if (!f) { return ''; }
        for (var i = 0; i < f.rows.length; i++) {
            if (String(f.rows[i][keyColumn]) === String(key)) { return f.rows[i][column] === undefined ? '' : f.rows[i][column]; }
        }
        return '';
    }

    // ---------------------------------------------------------------------------------------------------------------
    // search() and pulldata("@layer") (ADR-287 section 5.23).
    //
    // search() fills a select's choices from a CSV uploaded with the form (xlsform.org's "Dynamic selects from
    // pre-loaded data") or from a feature layer of this server (Survey123's "Multiple choice" page). The list's first row
    // on the choices sheet says which column is the answer (name) and which is shown (label, or several columns joined).
    // A CSV is on the device with the form, so it works offline; a layer needs the network, and with none the question
    // says so and is not required until it can be answered (INFERRED).
    //
    // pulldata("@layer") answers a calculation from one record of a layer of this server (Survey123's "Query a feature
    // layer"). The evaluator is synchronous: it asks layerAnswer(), which gives what was fetched for that exact request,
    // or '' while the request is out, and starts it; when the answer arrives the form is recalculated, and a calculation
    // whose inputs changed asks again with a new request. Answers are kept for the page's life, as Survey123 caches them.
    //
    // Only this server's layers are queried, with the member's own sign-in: an address of another origin, or one that is
    // not .../FeatureServer/{n}, is never fetched (the server refuses such a form at publish; this covers an address
    // worked out at run time).
    // ---------------------------------------------------------------------------------------------------------------

    var layerCache = {};     // request key -> { state: 'pending' | 'done' | 'offline' | 'error' | 'foreign', json, message }
    var layerFields = {};    // layer address -> { state, fields: { name: esriFieldType }, geometryType }
    var searchStatus = {};   // instance id + '/' + question -> the state of its last search()
    var layerStatus = {};    // instance id + '/' + question -> the state of its calculation's last pulldata("@layer")
    var lookupPending = false;   // set while a calculation runs when a lookup it made has not answered yet (section 5.27)

    function basePath() { return location.pathname.replace(/\/forms(\/.*)?$/i, ''); }

    /** A layer address as this page fetches it, or null when it is not a layer of this server. */
    function ownLayerUrl(written) {
        var text = String(written || '').trim();
        if (!text) { return null; }
        var base = basePath();
        // A path from the root is this server's, under its path base when it runs under one.
        var path = text.charAt(0) === '/' && text.charAt(1) !== '/' && base && text.toLowerCase().indexOf(base.toLowerCase() + '/') !== 0 ? base + text : text;
        var resolved;
        try { resolved = new URL(path, location.origin + base + '/'); } catch (e) { return null; }
        if (resolved.origin !== location.origin) { return null; }
        if (!/\/rest\/services\/.+\/FeatureServer\/\d+\/?$/i.test(resolved.pathname)) { return null; }
        var params = {};
        resolved.searchParams.forEach(function (v, k) { params[k] = v; });
        return { url: resolved.origin + resolved.pathname.replace(/\/$/, ''), params: params };
    }

    function layerHeaders() {
        var headers = {};
        var held = token();
        if (held) { headers.Authorization = 'Bearer ' + held; }
        return headers;
    }

    function encodeForm(params) {
        return Object.keys(params).filter(function (k) { return params[k] !== null && params[k] !== undefined; })
            .map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(params[k]); }).join('&');
    }

    // One GET, its answer kept under its key. A thrown fetch is no network; an HTTP or ArcGIS error is said as one.
    function layerGet(key, url, params) {
        var hit = layerCache[key];
        if (hit) { return hit; }
        // No network a moment ago: not asked again on every redraw, but after a while, or when the browser says it is back.
        var off = layerCache[key + '#offline'];
        if (off && (navigator.onLine === false || Date.now() - off.at < 15000)) { return off; }
        delete layerCache[key + '#offline'];
        if (navigator.onLine === false) { return { state: 'offline' }; }
        var entry = layerCache[key] = { state: 'pending' };
        fetch(url + '?' + encodeForm(params), { headers: layerHeaders(), credentials: 'same-origin' }).then(function (response) {
            return response.text().then(function (body) {
                var json = null;
                try { json = JSON.parse(body); } catch (e) { json = null; }
                if (!response.ok || !json || json.error) {
                    entry.state = 'error';
                    entry.message = json && json.error && json.error.message ? String(json.error.message) : 'HTTP ' + response.status;
                } else {
                    entry.state = 'done';
                    entry.json = json;
                }
            });
        }, function () {
            delete layerCache[key];          // no network: asked again when it returns
            layerCache[key + '#offline'] = { state: 'offline', at: Date.now() };
        }).then(function () { setTimeout(function () { refresh(false); }, 0); });
        return entry;
    }

    // The connection came back: what failed for want of it is asked again.
    window.addEventListener('online', function () {
        Object.keys(layerCache).forEach(function (k) { if (/#offline$/.test(k)) { delete layerCache[k]; } });
        if (form && root) { setTimeout(function () { refresh(false); }, 0); }
    });

    // A layer's fields, asked once, so a value is written as the field's type wants it in a where clause.
    function fieldsOf(url) {
        if (layerFields[url]) { return layerFields[url]; }
        var entry = layerGet('fields|' + url, url, { f: 'json' });
        if (entry.state !== 'done') { return { state: entry.state, message: entry.message }; }
        var fields = {};
        (entry.json.fields || []).forEach(function (f) { fields[String(f.name).toLowerCase()] = f.type; });
        return (layerFields[url] = { state: 'done', fields: fields, geometryType: entry.json.geometryType || null });
    }

    function sqlString(v) { return "'" + String(v).replace(/'/g, "''") + "'"; }

    function sqlValue(fields, column, value) {
        var type = fields[String(column).toLowerCase()];
        var numeric = /esriFieldType(SmallInteger|Integer|BigInteger|Single|Double|OID)/.test(type || '');
        if (numeric) { return isFinite(Number(value)) && String(value).trim() !== '' ? String(Number(value)) : null; }
        return sqlString(value);
    }

    // A geopoint, geotrace or geoshape answer ("lat lon alt acc; …") as an ArcGIS geometry in WGS 84, or null.
    function answerGeometry(text) {
        var points = String(text || '').split(';').map(function (p) { return p.trim().split(/\s+/).map(Number); })
            .filter(function (p) { return p.length >= 2 && isFinite(p[0]) && isFinite(p[1]); });
        if (!points.length) { return null; }
        var sr = { wkid: 4326 };
        if (points.length === 1) { return { type: 'esriGeometryPoint', geometry: { x: points[0][1], y: points[0][0], spatialReference: sr } }; }
        var path = points.map(function (p) { return [p[1], p[0]]; });
        var closed = path.length > 3 && path[0][0] === path[path.length - 1][0] && path[0][1] === path[path.length - 1][1];
        return closed
            ? { type: 'esriGeometryPolygon', geometry: { rings: [path], spatialReference: sr } }
            : { type: 'esriGeometryPolyline', geometry: { paths: [path], spatialReference: sr } };
    }

    var SPATIAL_RELATIONS = {
        intersects: 'esriSpatialRelIntersects', contains: 'esriSpatialRelContains', crosses: 'esriSpatialRelCrosses',
        envelope_intersects: 'esriSpatialRelEnvelopeIntersects', index_intersects: 'esriSpatialRelIndexIntersects',
        index_intercepts: 'esriSpatialRelIndexIntersects', overlaps: 'esriSpatialRelOverlaps', touches: 'esriSpatialRelTouches',
        within: 'esriSpatialRelWithin'
    };

    // The columns a search() list's one row names: { name, labels: { '' | language: [columns] }, image }.
    function searchColumnsOf(q) {
        var rows = form.choices && form.choices[q.list];
        var first = rows && rows.filter(function (c) { return !c.auto; })[0];
        if (!first) { return null; }
        var labels = {};
        Object.keys(first.labels || {}).forEach(function (k) {
            labels[k] = String(first.labels[k]).split(',').map(function (c) { return c.trim(); }).filter(Boolean);
        });
        return { name: String(first.name), labels: labels, image: text(first.images || {}, '') || null };
    }

    // A record (a CSV row or a layer's attributes) as a choice row: name, label per language, picture, and every column,
    // so a choice_filter can read them.
    function choiceFromRecord(record, columns) {
        var value = record[columns.name];
        if (value === undefined || value === null || String(value) === '') { return null; }
        var row = {};
        Object.keys(record).forEach(function (k) { row[k] = record[k] === null || record[k] === undefined ? '' : String(record[k]); });
        row.name = String(value);
        Object.keys(columns.labels).forEach(function (k) {
            // INFERRED (section 5.23): several label columns are joined with a space; xlsform.org says only that they are all shown.
            var label = columns.labels[k].map(function (c) { return record[c] === undefined || record[c] === null ? '' : String(record[c]); })
                .filter(Boolean).join(' ');
            row[k === '' ? 'label' : 'label::' + k] = label;
        });
        if (columns.image && record[columns.image]) { row.image = String(record[columns.image]); }
        return row;
    }

    function distinctByName(rows) {
        var seen = {};
        return rows.filter(function (r) { if (!r || seen[r.name]) { return false; } seen[r.name] = true; return true; });
    }

    function matchesText(type, cell, needle) {
        var a = String(cell === undefined || cell === null ? '' : cell);
        if (type === 'matches') { return a === needle; }
        var hay = fold(a), want = fold(needle);
        if (type === 'contains') { return hay.indexOf(want) >= 0; }
        if (type === 'startswith') { return hay.indexOf(want) === 0; }
        if (type === 'endswith') { return hay.length >= want.length && hay.substring(hay.length - want.length) === want; }
        return false;
    }

    function searchCsv(inst, q, s, columns) {
        var f = files[s.file];
        if (!f) { return { state: 'missing' }; }
        var needle = s.text ? String(evalString(s.text, inst, q)) : '';
        var filterValue = s.filterText ? String(evalString(s.filterText, inst, q)) : '';
        var searched = s.type ? String(s.column || '').split(',').map(function (c) { return c.trim(); }).filter(Boolean) : [];
        var rows = f.rows.filter(function (r) {
            // INFERRED (section 5.23): an empty search text narrows nothing, as Survey123's layer lists show every record then.
            if (s.type && needle !== '' && !searched.some(function (c) { return matchesText(s.type, r[c], needle); })) { return false; }
            if (s.filterColumn && String(r[s.filterColumn] === undefined ? '' : r[s.filterColumn]) !== filterValue) { return false; }
            return true;
        });
        // xlsform.org: a numeric column named sortby orders the choices; otherwise they keep the file's order.
        if (f.rows.length && Object.prototype.hasOwnProperty.call(f.rows[0], 'sortby')) {
            rows = rows.slice().sort(function (a, b) { return Number(a.sortby) - Number(b.sortby); });
        }
        return { state: 'done', rows: distinctByName(rows.map(function (r) { return choiceFromRecord(r, columns); })) };
    }

    function searchLayer(inst, q, s, columns) {
        var target = ownLayerUrl(s.url);
        if (!target) { return { state: 'foreign' }; }
        var shape = fieldsOf(target.url);
        if (shape.state !== 'done') { return shape; }
        var needle = s.text ? String(evalString(s.text, inst, q)) : '';
        var filterValue = s.filterText ? String(evalString(s.filterText, inst, q)) : '';
        var params = { f: 'json', outFields: '*', returnGeometry: 'false' };
        Object.keys(target.params).forEach(function (k) { params[k] = target.params[k]; });
        Object.keys(s.query || {}).forEach(function (k) { params[k] = s.query[k]; });
        var where = [];
        if (params.where) { where.push('(' + params.where + ')'); }
        // "contains" is both a text and a spatial search; it is spatial on a layer with geometry when the text is a location.
        var spatial = !!(s.type && SPATIAL_RELATIONS[s.type] && shape.geometryType
            && (s.type !== 'contains' || /^\s*-?\d+(\.\d+)?\s+-?\d+(\.\d+)?(\s|;|$)/.test(needle)));
        if (s.type && needle !== '') {
            if (spatial) {
                var g = answerGeometry(needle);
                if (g) {
                    params.geometry = JSON.stringify(g.geometry);
                    params.geometryType = g.type;
                    params.inSR = '4326';
                    params.spatialRel = SPATIAL_RELATIONS[s.type];
                }
            } else {
                var each = String(s.column || '').split(',').map(function (c) { return c.trim(); }).filter(Boolean).map(function (c) {
                    if (s.type === 'matches') { var v = sqlValue(shape.fields, c, needle); return v === null ? '1=0' : c + ' = ' + v; }
                    var pattern = s.type === 'contains' ? '%' + needle + '%' : s.type === 'startswith' ? needle + '%' : '%' + needle;
                    return 'UPPER(' + c + ') LIKE ' + sqlString(pattern.toUpperCase());
                });
                if (each.length) { where.push('(' + each.join(' OR ') + ')'); }
            }
        }
        if (s.filterColumn) {
            var fv = sqlValue(shape.fields, s.filterColumn, filterValue);
            where.push(fv === null ? '1=0' : s.filterColumn + ' = ' + fv);
        }
        params.where = where.length ? where.join(' AND ') : '1=1';
        var entry = layerGet('search|' + target.url + '|' + encodeForm(params), target.url + '/query', params);
        if (entry.state !== 'done') { return entry; }
        var features = entry.json.features || [];
        return { state: 'done', rows: distinctByName(features.map(function (f) { return choiceFromRecord(f.attributes || {}, columns); })) };
    }

    /** The choices a search() offers now, as { state, rows }: rows only when state is 'done'. */
    function searchChoices(inst, q) {
        var columns = searchColumnsOf(q);
        var result = !columns ? { state: 'error', message: 'no columns' }
            : q.search.url ? searchLayer(inst, q, q.search, columns) : searchCsv(inst, q, q.search, columns);
        searchStatus[inst.id + '/' + q.name] = result.state;
        return result;
    }

    /** What a select whose choices cannot be shown says instead. */
    function listUnavailableText(inst, q) {
        if (q.search) {
            switch (searchStatus[inst.id + '/' + q.name]) {
                case 'pending': return t('Loading the choices…');
                case 'offline': return t('These choices come from the server and need a connection. You can still submit the form; this question can be answered later.');
                case 'foreign': return t('These choices come from a layer of another server, which this form does not query.');
                case 'error': return t('The choices could not be loaded from the layer.');
            }
        }
        return isTurkish()
            ? '"' + (q.file || (q.search && q.search.file) || q.list) + '" listesi henüz yok: formla birlikte yüklenmedi ya da okunamadı.'
            : 'The list "' + (q.file || (q.search && q.search.file) || q.list) + '" is not available yet: it was not uploaded with the form, or could not be read.';
    }

    // pulldata("@layer"): the evaluator's ctx.layer, for the question q in the instance inst.
    function layerAnswer(inst, q, request) {
        var key = inst.id + '/' + q.name;
        var target = ownLayerUrl(request.url);
        if (!target) { layerStatus[key] = 'foreign'; return ''; }
        var params = { f: 'json' };
        Object.keys(target.params).forEach(function (k) { params[k] = target.params[k]; });
        params.where = request.where && String(request.where).trim() !== '' ? String(request.where) : '1=1';
        if (request.location !== null && request.location !== undefined) {
            var g = answerGeometry(request.location);
            if (!g || g.type !== 'esriGeometryPoint') { layerStatus[key] = 'done'; return ''; }   // no location yet: nothing to ask
            params.geometry = JSON.stringify(g.geometry);
            params.geometryType = g.type;
            params.inSR = '4326';
            if (!params.spatialRel) { params.spatialRel = 'esriSpatialRelIntersects'; }
        }
        if (!params.outStatistics) {
            params.outFields = '*';
            params.returnGeometry = request.operation.indexOf('getRecord') === 0 || /^geometry/.test(request.property || '') ? 'true' : 'false';
            params.outSR = '4326';
            params.resultRecordCount = '1';
        }
        var requestKey = 'layer|' + target.url + '|' + encodeForm(params);
        var entry = layerGet(requestKey, target.url + '/query', params);
        layerStatus[key] = entry.state;
        if (entry.state === 'pending') { lookupPending = true; }
        if (entry.state !== 'done') { return ''; }
        var feature = (entry.json.features || [])[0];
        // INFERRED (section 5.23): nothing found is the empty answer, as is a layer the member cannot read.
        if (!feature) { layerStatus[key] = 'none'; return ''; }
        if (request.operation.indexOf('getRecord') === 0) { return JSON.stringify(feature); }
        var value = feature;
        String(request.property || '').split('.').forEach(function (part) { value = value === null || value === undefined ? undefined : value[part]; });
        if (value === null || value === undefined) { return ''; }
        // INFERRED: a point's geometry into a geopoint question is the point a geopoint holds, "latitude longitude".
        if (typeof value === 'object') {
            if (q.type === 'geopoint' && isFinite(value.x) && isFinite(value.y)) { return value.y + ' ' + value.x; }
            return JSON.stringify(value);
        }
        return value;
    }

    /** What a question whose calculation queries a layer says under its answer, or ''. */
    function layerStatusText(inst, q) {
        switch (layerStatus[inst.id + '/' + q.name]) {
            case 'pending': return t('Looking this up…');
            case 'offline': return t('This answer comes from the server and needs a connection. It fills in by itself when you are back online; if you submit before then, it is left empty.');
            case 'foreign': return t('This answer would be looked up in a layer of another server, which this form does not query.');
            case 'error': return t('This answer could not be looked up: the layer could not be read.');
            case 'none': return lookupInputsEmpty(inst, q) ? '' : t('Nothing was found for this answer.');
        }
        return '';
    }

    // A lookup whose every input is still unanswered found nothing because nothing was asked yet: "Nothing was found"
    // under a question the person has not reached is noise, and they read it as a fault (UX review 2026-11-16). A
    // calculation that reads no question keeps the words.
    function lookupInputsEmpty(inst, q) {
        var names = [], re = /\$\{\s*([^}\s]+)\s*\}/g, m;
        while ((m = re.exec(String(q.calculation || ''))) !== null) { names.push(m[1]); }
        return names.length > 0 && names.every(function (n) {
            var v = valueIn(inst, n);
            return v === undefined || v === null || (Array.isArray(v) ? v.length === 0 : String(v) === '');
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // pulldata("@javascript") (ADR-287 section 5.27).
    //
    // A form's script is a file uploaded with it, under scripts/ as Survey123 keeps it, read like a CSV list and kept on
    // the device with the form. It never runs in this page. It runs in a frame of script-sandbox.html with
    // sandbox="allow-scripts" and nothing else: an opaque origin, so no cookie, no storage, no token and no same-origin
    // request, and no way into this page; the frame's own policy forbids every connection and every other script, and
    // the frame runs each file in a worker, so a script that never returns blocks nothing here. What crosses is what the
    // call passes — the arguments the form wrote, as plain values — and what the function returns, both size-limited.
    //
    // The evaluator is synchronous, so a call is answered as pulldata("@layer") is (section 5.23): scriptAnswer() gives
    // what the sandbox already returned for that exact call, or '' while it is out and sends it; when the answer arrives
    // the form is recalculated. A call that does not answer within SCRIPT_TIMEOUT_MS is abandoned: the frame is removed,
    // which stops every worker in it, the answer stays empty and the question says why. The next call starts a new frame.
    //
    // The server says whether this form's scripts may run at all (form.json's "scripts"): never on a form shared with
    // everyone, nor for a caller who is not signed in; the preview, in Studio, runs them for the publisher.
    // ---------------------------------------------------------------------------------------------------------------

    var SANDBOX_VERSION = '1';           // script-sandbox.html is cached for a year: change this when the file changes
    var SCRIPT_TIMEOUT_MS = 2000;        // INFERRED (section 5.27): Survey123's page sets no limit
    var SANDBOX_START_MS = 8000;
    var SCRIPT_ARGS_MAX = 65536;         // characters of JSON a call may pass
    var scriptTexts = {};                // script file name -> its text
    var missingScripts = [];             // script names the item does not have
    var scriptCache = {};                // call key -> { state: 'pending' | 'done' | 'error' | 'timeout' | 'unavailable', value, message }
    var scriptStatus = {};               // instance id + '/' + question -> the state of its calculation's last script call
    var sandbox = null;                  // { frame, ready, sent: { file: true }, queue: [message], waiting: { id: key }, timers: { id: timer }, startTimer }
    var sandboxSeq = 0;

    Object.assign(TR, {
        "This answer is left empty. It is worked out automatically only for signed-in members, and never on a form shared with everyone.": "Bu cevap boş kaldı. Yalnız giriş yapmış üyeler için kendiliğinden hesaplanır; herkesle paylaşılan bir formda hiç hesaplanmaz.",
        "This answer is left empty. What works it out was not uploaded with the form; tell the form's owner.": "Bu cevap boş kaldı. Onu hesaplayan dosya formla birlikte yüklenmemiş; formun sahibine haber verin.",
        "This answer is left empty. Working it out took too long, so it was stopped; tell the form's owner.": "Bu cevap boş kaldı. Hesaplanması çok uzun sürdüğü için durduruldu; formun sahibine haber verin.",
        "This answer is left empty. Working it out failed; tell the form's owner: ": "Bu cevap boş kaldı. Hesaplanamadı; formun sahibine şunu iletin: ",
        "This answer is left empty for now: it cannot be worked out on this device yet. It is tried again when the connection returns.": "Bu cevap şimdilik boş: bu cihazda henüz hesaplanamıyor. Bağlantı gelince yeniden denenir."
    });

    function scriptNamesOf(formIn) {
        var names = [];
        (function walk(node) {
            (node.body || []).forEach(function (n) {
                if (n.kind === 'question') {
                    [n.calculation, n.relevant, n.constraint, n.default, n.required, n.readOnly, n.choiceFilter].forEach(function (e) {
                        var re = /pulldata\s*\(\s*['"]@javascript['"]\s*,\s*['"]([^'"]+)['"]/g, m;
                        while (typeof e === 'string' && (m = re.exec(e))) { if (names.indexOf(m[1]) < 0) { names.push(m[1]); } }
                    });
                } else { walk(n); }
            });
        })(formIn.root);
        return names;
    }

    // Whether this form's scripts may run here: the server's word, and in a preview the publisher's own.
    function scriptsAllowed() {
        if (loadedFrom === 'preview') { return true; }
        return !!doc && doc.scripts === 'allowed';
    }

    // A script is an item resource like a CSV, tried under scripts/ first. It is read as text: the server serves it as
    // text, never as a script, and it is handed to the sandbox, the one place it runs.
    async function fetchScript(name) {
        var candidates = ['scripts/' + name, name];
        for (var i = 0; i < candidates.length; i++) {
            try {
                var headers = {};
                var held = token();
                if (held) { headers.Authorization = 'Bearer ' + held; }
                var response = await fetch('/sharing/rest/content/items/' + itemId + '/resources/' + encodeURI(candidates[i]),
                    { headers: headers, credentials: 'same-origin' });
                if (!response.ok) { continue; }
                var body = await response.text();
                if (/json/i.test(response.headers.get('content-type') || '') && /"error"/.test(body)) { continue; }
                return body;
            } catch (e) {
                return null;
            }
        }
        return null;
    }

    async function loadScripts(loaded) {
        if (!scriptsAllowed()) { return; }
        var names = scriptNamesOf(form);
        for (var i = 0; i < names.length; i++) {
            if (scriptTexts[names[i]] !== undefined) { continue; }
            var body = loaded.from !== 'device' && itemId ? await fetchScript(names[i]) : null;
            if (body === null && loaded.from !== 'preview' && itemId) {
                var copy = await dbGet('forms', itemId).catch(function () { return null; });
                if (copy && copy.scripts && copy.scripts[names[i]] !== undefined) { body = copy.scripts[names[i]]; }
            }
            if (body === null) { missingScripts.push(names[i]); } else { scriptTexts[names[i]] = body; }
        }
        // Started now, while there is a connection, so the browser has the sandbox page in its cache for a reload with none.
        if (Object.keys(scriptTexts).length) { sandboxFrame(); }
    }

    function sandboxUrl() { return basePath() + '/forms/script-sandbox.html?v=' + SANDBOX_VERSION; }

    function sandboxFrame() {
        if (sandbox) { return sandbox; }
        var frame = document.createElement('iframe');
        frame.setAttribute('sandbox', 'allow-scripts');
        frame.setAttribute('title', 'Form scripts');
        frame.setAttribute('aria-hidden', 'true');
        frame.setAttribute('tabindex', '-1');
        frame.hidden = true;
        frame.className = 'script-sandbox';
        frame.src = sandboxUrl();
        var box = { frame: frame, ready: false, sent: {}, queue: [], backlog: [], waiting: {}, timers: {}, startTimer: 0 };
        // No answer from the page at all — no connection and nothing cached — is said as such, and tried again later.
        box.startTimer = setTimeout(function () { if (sandbox === box && !box.ready) { dropSandbox('unavailable'); } }, SANDBOX_START_MS);
        sandbox = box;
        document.body.appendChild(frame);
        return box;
    }

    // Removes the frame and every worker in it. The call that caused it keeps its state; every other call that was out
    // is forgotten, so it is simply asked again by the next recalculation.
    function dropSandbox(why, culprit) {
        var box = sandbox;
        if (!box) { return; }
        sandbox = null;
        clearTimeout(box.startTimer);
        Object.keys(box.timers).forEach(function (id) { clearTimeout(box.timers[id]); });
        Object.keys(box.waiting).forEach(function (id) {
            var key = box.waiting[id];
            if (key === culprit) { return; }
            if (why === 'unavailable') { scriptCache[key] = { state: 'unavailable', at: Date.now() }; } else { delete scriptCache[key]; }
        });
        box.queue.concat(box.backlog).forEach(function (m) { if (m.kind === 'call' && m.key !== culprit) { if (why === 'unavailable') { scriptCache[m.key] = { state: 'unavailable', at: Date.now() }; } else { delete scriptCache[m.key]; } } });
        if (box.frame.parentNode) { box.frame.parentNode.removeChild(box.frame); }
        setTimeout(function () { refresh(false); }, 0);
    }

    // One call at a time: a frame's worker runs its calls one after another, so a call sent behind another would have
    // its time limit running while it only waited, and a quick function typed into fast was stopped as "took too long"
    // (UX review 2026-11-17). The next call is sent when the one out answers.
    function sandboxSend(box, message) {
        if (!box.ready) { box.queue.push(message); return; }
        if (message.kind === 'call' && Object.keys(box.waiting).length) { box.backlog.push(message); return; }
        if (message.kind === 'call' && !box.sent[message.file]) {
            box.frame.contentWindow.postMessage({ kind: 'load', file: message.file, source: scriptTexts[message.file] }, '*');
            box.sent[message.file] = true;
        }
        if (message.kind === 'call') {
            box.waiting[message.id] = message.key;
            box.timers[message.id] = setTimeout(function () {
                if (sandbox !== box || !Object.prototype.hasOwnProperty.call(box.waiting, message.id)) { return; }
                scriptCache[message.key] = { state: 'timeout' };
                dropSandbox('timeout', message.key);
            }, SCRIPT_TIMEOUT_MS);
            box.frame.contentWindow.postMessage({ kind: 'call', id: message.id, file: message.file, fn: message.fn, args: message.args }, '*');
        }
    }

    window.addEventListener('message', function (event) {
        var box = sandbox;
        if (!box || event.source !== box.frame.contentWindow) { return; }
        var d = event.data || {};
        if (d.kind === 'ready' && !box.ready) {
            box.ready = true;
            clearTimeout(box.startTimer);
            var queued = box.queue;
            box.queue = [];
            queued.forEach(function (m) { sandboxSend(box, m); });
            return;
        }
        if (d.kind !== 'result' || typeof d.id !== 'number' || !Object.prototype.hasOwnProperty.call(box.waiting, d.id)) { return; }
        var key = box.waiting[d.id];
        delete box.waiting[d.id];
        clearTimeout(box.timers[d.id]);
        delete box.timers[d.id];
        if (box.backlog.length) { sandboxSend(box, box.backlog.shift()); }
        var v = d.value;
        var fine = d.ok === true && (v === null || typeof v === 'boolean' || (typeof v === 'number' && isFinite(v)) || (typeof v === 'string' && v.length <= SCRIPT_ARGS_MAX));
        scriptCache[key] = fine ? { state: 'done', value: v === null ? '' : v }
            : { state: 'error', message: String(d.error || 'the answer is not a value a question can hold').slice(0, 300) };
        setTimeout(function () { refresh(false); }, 0);
    });

    // A frame that could not start for want of a connection is tried again when the connection returns.
    window.addEventListener('online', function () {
        Object.keys(scriptCache).forEach(function (k) { if (scriptCache[k].state === 'unavailable') { delete scriptCache[k]; } });
        if (form && root) { setTimeout(function () { refresh(false); }, 0); }
    });

    // pulldata("@javascript"): the evaluator's ctx.javascript, for the question q in the instance inst.
    function scriptAnswer(inst, q, request) {
        var at = inst.id + '/' + q.name;
        if (!scriptsAllowed()) { scriptStatus[at] = 'refused'; return ''; }
        if (scriptTexts[request.file] === undefined) { scriptStatus[at] = 'missing'; return ''; }
        var args;
        try { args = JSON.stringify(request.args || []); } catch (e) { args = null; }
        if (args === null || args.length > SCRIPT_ARGS_MAX) {
            scriptStatus[at] = 'error';
            scriptStatus[at + '#message'] = 'its arguments are too long';
            return '';
        }
        var key = request.file + '|' + request.fn + '|' + args;
        var hit = scriptCache[key];
        // Unstartable a moment ago: not asked again on every redraw, but after a while.
        if (hit && hit.state === 'unavailable' && Date.now() - hit.at > 15000 && navigator.onLine !== false) { delete scriptCache[key]; hit = null; }
        if (!hit) {
            hit = scriptCache[key] = { state: 'pending' };
            sandboxSend(sandboxFrame(), { kind: 'call', id: ++sandboxSeq, key: key, file: request.file, fn: request.fn, args: JSON.parse(args) });
        }
        scriptStatus[at] = hit.state;
        scriptStatus[at + '#message'] = hit.message || '';
        if (hit.state === 'pending') { lookupPending = true; }
        return hit.state === 'done' ? hit.value : '';
    }

    var SCRIPT_STATUS_SETTLE_MS = 600;   // a script's changed status line is written once it has held this long

    /** Whether the last script call a question's calculation made is still out. */
    function scriptStatusPending(inst, q) {
        return !!q.calculation && /@javascript/.test(q.calculation) && scriptStatus[inst.id + '/' + q.name] === 'pending';
    }

    /** What a question whose calculation runs a script says under its answer, or ''. */
    function scriptStatusText(inst, q) {
        if (!q.calculation || !/@javascript/.test(q.calculation)) { return ''; }
        var at = inst.id + '/' + q.name;
        switch (scriptStatus[at]) {
            case 'refused': return t('This answer is left empty. It is worked out automatically only for signed-in members, and never on a form shared with everyone.');
            case 'missing': return t("This answer is left empty. What works it out was not uploaded with the form; tell the form's owner.");
            case 'timeout': return t("This answer is left empty. Working it out took too long, so it was stopped; tell the form's owner.");
            case 'error': return t("This answer is left empty. Working it out failed; tell the form's owner: ") + (scriptStatus[at + '#message'] || '');
            case 'unavailable': return t('This answer is left empty for now: it cannot be worked out on this device yet. It is tried again when the connection returns.');
        }
        return '';
    }

    // A new preview: the scripts are read again, and the frame that ran the old ones goes.
    function resetScripts() {
        scriptTexts = {};
        missingScripts = [];
        scriptCache = {};
        scriptStatus = {};
        if (sandbox) { var box = sandbox; sandbox = null; clearTimeout(box.startTimer); if (box.frame.parentNode) { box.frame.parentNode.removeChild(box.frame); } }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Pictures (ADR-287 §5.10, 2026-11-15): a label or a choice may show a picture, a file uploaded with the form. It is
    // an item resource, kept under media/ as Survey123 keeps it, read like a CSV list and kept on the device with the
    // form so an offline reload still shows it. A picture that cannot be found says so; it is never a broken image.
    // ---------------------------------------------------------------------------------------------------------------

    var mediaBlobs = {};     // file name -> Blob, once found (here or on the device)
    var mediaPending = {};   // file name -> the promise of the search
    var mediaUrls = {};      // file name -> its blob: address

    function mediaTypeOf(name) {
        var m = /\.([a-z0-9]+)$/i.exec(name || '');
        var ext = m ? m[1].toLowerCase() : '';
        return { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', svg: 'image/svg+xml',
            // section 5.26: a .bmp picture, and the recordings a label or a choice may play
            bmp: 'image/bmp', mp3: 'audio/mpeg', wav: 'audio/wav', ogg: 'audio/ogg', oga: 'audio/ogg', opus: 'audio/ogg',
            m4a: 'audio/mp4', aac: 'audio/aac', webm: 'audio/webm', flac: 'audio/flac' }[ext] || 'application/octet-stream';
    }

    function loadMedia(name) {
        if (Object.prototype.hasOwnProperty.call(mediaBlobs, name)) { return Promise.resolve(mediaBlobs[name]); }
        if (!mediaPending[name]) {
            mediaPending[name] = (async function () {
                var found = null;
                var candidates = ['media/' + name, name];
                for (var i = 0; i < candidates.length && !found; i++) {
                    try {
                        var headers = {};
                        var held = token();
                        if (held) { headers.Authorization = 'Bearer ' + held; }
                        var response = await fetch('/sharing/rest/content/items/' + itemId + '/resources/' + encodeURI(candidates[i]),
                            { headers: headers, credentials: 'same-origin' });
                        if (!response.ok || /json/i.test(response.headers.get('content-type') || '')) { continue; }
                        found = new Blob([await response.arrayBuffer()], { type: mediaTypeOf(name) });
                    } catch (e) {
                        break;
                    }
                }
                if (!found) {
                    // Not reachable now — offline, the service worker answers form.json from its copy and nothing else —
                    // so the copy kept on the device with the form is looked in.
                    var kept = await dbGet('forms', itemId).catch(function () { return null; });
                    if (kept && kept.media && kept.media[name]) { found = kept.media[name]; }
                }
                if (found) { mediaBlobs[name] = found; }
                delete mediaPending[name];
                return found;
            })();
        }
        return mediaPending[name];
    }

    function mediaNamesOf(formIn) {
        var names = [];
        function add(map) { Object.keys(map || {}).forEach(function (k) { if (map[k] && names.indexOf(map[k]) < 0) { names.push(map[k]); } }); }
        (function walk(node) {
            (node.body || []).forEach(function (n) { if (n.kind === 'question') { add(n.images); add(n.audio); } else { walk(n); } });
        })(formIn.root);
        Object.keys(formIn.choices || {}).forEach(function (l) { formIn.choices[l].forEach(function (c) { add(c.images); add(c.audio); }); });
        return names;
    }

    function pictureName(map) { return text(map, ''); }

    /** The picture as an element: an image once found, and in its place a sentence when it cannot be. */
    function pictureNode(tag, name, cls) {
        var holder = el(tag, { class: 'picture' + (cls ? ' ' + cls : '') });
        loadMedia(name).then(function (blob) {
            if (!blob) {
                holder.appendChild(el('span', { class: 'picture-missing', text: t('Picture not available: ') + name }));
                return;
            }
            if (!mediaUrls[name]) { mediaUrls[name] = URL.createObjectURL(blob); }
            holder.appendChild(el('img', { src: mediaUrls[name], alt: '' }));
        });
        return holder;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Instances and the evaluator's context.
    // ---------------------------------------------------------------------------------------------------------------

    function newInstance(node, parent) {
        var inst = { id: guid(), node: node, parent: parent, values: {}, files: {}, repeats: {}, rel: {}, touched: {}, relevant: true };
        initBody(inst, node.body || []);
        return inst;
    }

    function initBody(inst, body) {
        body.forEach(function (n) {
            if (n.kind === 'group') { initBody(inst, n.body || []); return; }
            if (n.kind === 'repeat') {
                // INFERRED: a repeat without repeat_count starts with one instance, as Survey123's does; it may be removed.
                inst.repeats[n.name] = n.repeatCount ? [] : [newInstance(n, inst)];
                return;
            }
            var v = initialValue(inst, n);
            if (v !== null && v !== undefined) { inst.values[n.name] = v; }
        });
    }

    function initialValue(inst, q) {
        var now = new Date();
        switch (q.type) {
            case 'start': return localDateTime(now);
            case 'today': return localDate(now);
            case 'username': return me.name || '';
            case 'email': return me.email || '';
            case 'deviceid': return deviceId();
        }
        if (q.default === null || q.default === undefined || q.default === '') { return null; }
        var d = String(q.default);
        if (/[()]|\$\{/.test(d)) {
            try { d = X.evaluateString(d, contextFor(inst, q)); } catch (e) { return null; }
        }
        if (q.type === 'select_multiple' || q.type === 'select_multiple_from_file') { d = d.split(/[\s,]+/).filter(Boolean).join(','); }
        return d;
    }

    function deviceId() {
        // ADR-287 §5.10: a random id kept by the installed client, not a hardware id.
        var key = 'graticula-forms-deviceid';
        try {
            var held = localStorage.getItem(key);
            if (held) { return held; }
            var made = 'graticula:' + guid().slice(1, -1).toLowerCase();
            localStorage.setItem(key, made);
            return made;
        } catch (e) {
            return '';
        }
    }

    function chainOf(inst) { var c = []; for (var i = inst; i; i = i.parent) { c.unshift(i); } return c; }

    function answerOf(inst, name) {
        if (inst.rel[name] === false || inst.relevant === false) { return ''; }
        var v = inst.values[name];
        return v === undefined || v === null ? '' : String(v);
    }

    function flat(list, fn) { var out = []; list.forEach(function (x) { out = out.concat(fn(x)); }); return out; }

    // ${name} as seen from an instance: the answer in this instance or an enclosing one, or — when the question sits
    // inside a repeat below where the expression is — every instance's answer, for count() and sum().
    function valueIn(scope, name, path) {
        var def = defs[name];
        if (!def) { return null; }
        var insts;
        var k = 0;
        if (path && path.length) {
            insts = [root];
            for (var p = 0; p < path.length; p++) {
                var list = flat(insts, function (i) { return i.repeats[path[p].repeat] || []; });
                var at = Math.floor(Number(path[p].index)) - 1;
                insts = at >= 0 && at < list.length ? [list[at]] : [];
                k = def.repeats.indexOf(path[p].repeat) + 1;
            }
        } else {
            var chain = chainOf(scope);
            while (k < chain.length - 1 && k < def.repeats.length && chain[k + 1].node.name === def.repeats[k]) { k++; }
            insts = [chain[k]];
        }
        var many = k < def.repeats.length;
        for (var j = k; j < def.repeats.length; j++) {
            insts = flat(insts, function (i) { return (i.repeats[def.repeats[j]] || []).filter(function (r) { return r.relevant !== false; }); });
        }
        if (def.node.kind === 'repeat') {
            return flat(insts, function (i) { return i.rel[name] === false ? [] : (i.repeats[name] || []); }).map(function () { return ''; });
        }
        if (def.node.kind === 'group') { return ''; }
        var values = insts.map(function (i) { return answerOf(i, name); });
        return many ? values : (values.length ? values[0] : '');
    }

    function positionOf(inst) {
        if (!inst.parent) { return 1; }
        return (inst.parent.repeats[inst.node.name] || []).indexOf(inst) + 1;
    }

    function contextFor(inst, q, current) {
        return {
            value: function (name, path) { return valueIn(inst, name, path); },
            current: current !== undefined ? current : (q && q.kind === 'question' ? answerOf(inst, q.name) : ''),
            position: positionOf(inst),
            choices: choiceRows,
            listOf: function (name) { var d = defs[name]; return d ? (d.node.list || d.node.file || null) : null; },
            language: lang,
            pulldata: pulldata,
            property: propertyOf,
            layer: function (request) { return q && q.kind === 'question' ? layerAnswer(inst, q, request) : ''; },
            javascript: function (request) { return q && q.kind === 'question' ? scriptAnswer(inst, q, request) : ''; }
        };
    }

    // pulldata("@property", name) and property(): what a browser can know (ADR-287 section 5.20). The member comes from the
    // portal's community/self when signed in; an anonymous form, and any value the portal does not hold, answers empty
    // (INFERRED: Survey123's page does not say). FormCatalog.Properties is the list; the checker refuses the rest by name.
    function propertyOf(name) {
        switch (name) {
            case 'online': return navigator.onLine !== false;
            case 'utcOffset': return -new Date().getTimezoneOffset() / 60;
            case 'timeZone':
                try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch (e) { return ''; }
            case 'deviceId': return deviceId();
            case 'userAgent': return navigator.userAgent || '';
            case 'email': return me.email || '';
            case 'username': return me.name || '';
            case 'userFullName': return me.fullName || '';
            case 'userFirstName': return me.firstName || '';
            case 'userLastName': return me.lastName || '';
            case 'portalUrl': return me.name ? location.origin + location.pathname.replace(/\/forms(\/.*)?$/i, '') : '';
            case 'language': return lang || (form && form.defaultLanguage) || '';
            case 'surveyName': return (form && form.title) || '';
            case 'mode': return editing ? 'edit' : 'new';   // a record opened from the Inbox is being edited (ADR-300)
            case 'status': return '';           // Survey123: empty for a new response
        }
        return '';
    }

    function evalBool(expression, inst, q, fallback, current) {
        if (expression === null || expression === undefined || String(expression).trim() === '') { return fallback; }
        if (isYes(expression)) { return true; }
        if (isNo(expression)) { return false; }
        try { return X.evaluateBoolean(expression, contextFor(inst, q, current)); } catch (e) { warn(expression, e); return fallback; }
    }

    function evalString(expression, inst, q) {
        try { return X.evaluateString(expression, contextFor(inst, q)); } catch (e) { warn(expression, e); return ''; }
    }

    var warned = {};
    function warn(expression, e) {
        if (warned[expression]) { return; }
        warned[expression] = true;
        if (window.console) { console.warn('Form expression failed: ' + expression + ' — ' + e.message); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Recalculation: relevance, calculations and repeat counts, until nothing changes.
    // ---------------------------------------------------------------------------------------------------------------

    var changed = false;
    var structureChanged = false;

    function recompute() {
        for (var pass = 0; pass < 6; pass++) {
            changed = false;
            walkInstance(root, true);
            if (!changed) { break; }
        }
    }

    function walkInstance(inst, relevant) {
        inst.relevant = relevant;
        walkBody(inst, inst.node.body || [], relevant);
    }

    function walkBody(inst, body, relevant) {
        body.forEach(function (n) {
            var r = relevant && evalBool(n.relevant, inst, n, true);
            if (inst.rel[n.name] !== r) { inst.rel[n.name] = r; changed = true; }
            if (n.kind === 'question') {
                if (r && n.calculation && calculationDue(inst, n)) {
                    lookupPending = false;
                    var v = asCalculated(n, evalString(n.calculation, inst, n));
                    noteCalculated(inst, n, v);
                    if (inst.values[n.name] !== v) { inst.values[n.name] = v; changed = true; }
                }
            } else if (n.kind === 'group') {
                walkBody(inst, n.body || [], r);
            } else if (n.kind === 'repeat') {
                var list = inst.repeats[n.name] || (inst.repeats[n.name] = []);
                if (n.repeatCount && r) {
                    var count = Math.max(0, Math.min(200, Math.floor(Number(evalString(n.repeatCount, inst, n)))));
                    if (isNaN(count)) { count = 0; }
                    while (list.length < count) { list.push(newInstance(n, inst)); changed = true; structureChanged = true; }
                    // A row the server already holds is never taken away (ADR-300 §5.7): a count below it stops there.
                    while (list.length > count && !list[list.length - 1].existing) { list.pop(); changed = true; structureChanged = true; }
                }
                list.forEach(function (child) { walkInstance(child, r); });
            }
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // calculationMode (bind::esri:parameters, ADR-287 section 5.20), as Survey123's documentation words it:
    //   always     recalculates whenever anything it reads changes, and with dynamic functions such as now()
    //   auto       (the default) calculates when the question is empty or has not been changed by the person
    //   manual     only when the question's Calculate button is pressed
    //   whenEmpty  until it gives a non-empty value, then as manual, even if the value is cleared
    // INFERRED: "auto" is read as: not changed by the person, and not recalculated by the clock alone. A calculation is
    // recalculated when what it reads has changed since it last ran, so now() in an auto calculation is taken once; one
    // that reads a question by a path (../q, /data/q), current() or instance() is judged by every answer in the form.
    // ---------------------------------------------------------------------------------------------------------------

    function calcState(inst, name) {
        if (!inst.calc) { inst.calc = {}; }
        return inst.calc[name] || (inst.calc[name] = { key: null, locked: false, ran: false });
    }

    function isFilled(inst, name) {
        var v = inst.values[name];
        return v !== undefined && v !== null && String(v) !== '';
    }

    function everyAnswer() {
        var out = [];
        (function walk(inst) {
            out.push(inst.values);
            Object.keys(inst.repeats).forEach(function (k) { inst.repeats[k].forEach(walk); });
        })(root);
        return JSON.stringify(out);
    }

    function calculationKey(inst, q) {
        var e = q.calculation;
        if (/\.\.\/|\/data\/|current\s*\(|instance\s*\(/.test(e)) { return everyAnswer() + '|' + lang; }
        var names = [];
        try { names = X.references(e); } catch (err) { return everyAnswer() + '|' + lang; }
        return JSON.stringify(names.map(function (n) { return valueIn(inst, n); })) + '|' + positionOf(inst) + '|' + lang;
    }

    function calculationDue(inst, q) {
        var mode = q.calculationMode || 'auto';
        var st = calcState(inst, q.name);
        if (mode === 'always') { return true; }
        if (mode === 'manual') { return false; }
        if (mode === 'whenEmpty') {
            if (st.locked) { return false; }
            if (isFilled(inst, q.name) && !st.ran) { st.locked = true; return false; }   // an answer already there is kept
            return true;
        }
        if (isFilled(inst, q.name) && inst.touched[q.name]) { return false; }           // the person changed it
        return !isFilled(inst, q.name) || st.key !== calculationKey(inst, q);
    }

    function noteCalculated(inst, q, value) {
        var st = calcState(inst, q.name);
        st.ran = true;
        // A lookup the calculation made is still out (pulldata("@layer"), pulldata("@javascript")): what it gave stands for
        // now, but is not this calculation's answer to these inputs, so it is neither kept nor locked, and the calculation
        // runs again when the answer arrives. Without this a calculation that wraps the lookup — concat('[', …, ']') —
        // gave '[]', which is not empty, and an "auto" calculation whose inputs had not changed was never run again.
        if (lookupPending) { st.key = null; return; }
        if ((q.calculationMode || 'auto') === 'whenEmpty' && value !== '') { st.locked = true; }
        if ((q.calculationMode || 'auto') === 'auto') { st.key = calculationKey(inst, q); }
    }

    // A number calculated from an empty answer is NaN (XPath 1.0 section 4.4). A number question cannot hold it: shown, it
    // read "NaN" in a read-only box, locked a whenEmpty calculation as answered, and blocked Submit with "Enter a whole
    // number" that the person had no way to act on (UX review 2026-11-16). It is the empty answer.
    function asCalculated(q, v) {
        if ((q.type === 'integer' || q.type === 'decimal' || q.type === 'range') && !isFinite(Number(v))) { return ''; }
        return v;
    }

    /** The Calculate button: runs the calculation now, whatever the mode, and leaves the person's own touch alone. */
    function calculateNow(inst, q) {
        lookupPending = false;
        var v = asCalculated(q, evalString(q.calculation, inst, q));
        noteCalculated(inst, q, v);
        inst.values[q.name] = v;
        refresh(false);
        scheduleDraft();    return v;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Validation, on the model rather than the screen, so a question on another page is checked too.
    // ---------------------------------------------------------------------------------------------------------------

    function filesOf(inst, name) { return inst.files[name] || []; }

    function isEmpty(inst, q) {
        if (isMedia(q)) { return filesOf(inst, q.name).length === 0 && existingFilesOf(inst, q.name).length === 0; }
        var v = inst.values[q.name];
        return v === undefined || v === null || String(v).trim() === '';
    }

    function isMedia(q) { return q.type === 'image' || q.type === 'audio' || q.type === 'video' || q.type === 'file'; }

    function hasUi(q) {
        return !(q.type === 'calculate' || q.type === 'hidden' || q.type === 'start' || q.type === 'end' || q.type === 'today'
            || q.type === 'username' || q.type === 'email' || q.type === 'deviceid' || has(q.appearance, 'hidden'));
    }

    function messageFor(inst, q) {
        if (q.kind !== 'question' || inst.relevant === false || inst.rel[q.name] === false) { return ''; }
        if (q.type === 'note') { return ''; }
        var empty = isEmpty(inst, q);
        if (empty) {
            // INFERRED (section 5.23): a choice list that needs the network and has none does not hold the record back; the
            // question says so, and the record can be completed from the Inbox later.
            if (q.search && q.search.url && searchStatus[inst.id + '/' + q.name] === 'offline') { return ''; }
            if (evalBool(q.required, inst, q, false)) { return text(q.requiredMessages) || t('This answer is required.'); }
            return '';
        }
        var v = String(inst.values[q.name] === undefined ? '' : inst.values[q.name]);
        if (q.type === 'integer' && !/^-?\d+$/.test(v.trim())) { return t('Enter a whole number.'); }
        if ((q.type === 'decimal' || q.type === 'range') && isNaN(Number(v))) { return t('Enter a number.'); }
        if (q.type === 'geopoint') {
            var parts = v.trim().split(/\s+/).map(Number);
            if (parts.length < 2 || isNaN(parts[0]) || isNaN(parts[1]) || Math.abs(parts[0]) > 90 || Math.abs(parts[1]) > 180) {
                return t('Enter a latitude between -90 and 90 and a longitude between -180 and 180.');
            }
            var threshold = Number(q.accuracyThreshold);
            if (q.accuracyThreshold && !isNaN(threshold) && parts.length >= 4 && parts[3] > threshold) {
                return isTurkish()
                    ? 'Konum ' + Math.round(parts[3]) + ' m doğrulukta; bu form ' + threshold + ' m veya daha iyisini istiyor. Daha iyi bir konumu bekleyin ya da elle girin.'
                    : 'The location is accurate to ' + Math.round(parts[3]) + ' m; this form needs ' + threshold + ' m or better. Wait for a better fix or enter it by hand.';
            }
        }
        if (q.type === 'geotrace' || q.type === 'geoshape') {
            var need = q.type === 'geotrace' ? 2 : 3;
            if (vertices(v).length < need) { return isTurkish() ? 'En az ' + need + ' nokta ekleyin.' : 'Add at least ' + need + ' points.'; }
        }
        if (q.type === 'date' && has(q.appearance, 'week-number') && !/^\d{4}-\d{2}-\d{2}/.test(v)) {
            return t('Enter a year and a week number that exists.');
        }
        if (q.type === 'text' && q.inputMask && parseMask(q.inputMask) && !maskAccepts(parseMask(q.inputMask), v)) {
            return t('Use the pattern ') + maskExample(parseMask(q.inputMask)) + '.';
        }
        if (q.constraint && !evalBool(q.constraint, inst, q, true, v)) {
            return text(q.constraintMessages) || t('This answer is not allowed.');
        }
        return '';
    }

    // Every question with a message, in form order: [{ inst, q, message, key }].
    function problems() {
        var out = [];
        (function visit(inst, body) {
            body.forEach(function (n) {
                if (inst.rel[n.name] === false) { return; }
                if (n.kind === 'question') {
                    var m = messageFor(inst, n);
                    if (m) { out.push({ inst: inst, q: n, message: m, key: keyOf(inst, n.name) }); }
                } else if (n.kind === 'group') {
                    visit(inst, n.body || []);
                } else if (n.kind === 'repeat') {
                    (inst.repeats[n.name] || []).forEach(function (child) { visit(child, n.body || []); });
                }
            });
        })(root, root.node.body || []);
        return out;
    }

    // The required search() lists left empty because their layer needed a connection (owner decision 2026-11-17,
    // ADR-287 section 5.23): the record goes, and the Outbox names what it went without.
    function leftForConnection() {
        var out = [];
        (function visit(inst, body) {
            body.forEach(function (n) {
                if (inst.rel[n.name] === false) { return; }
                if (n.kind === 'question') {
                    if (n.search && n.search.url && searchStatus[inst.id + '/' + n.name] === 'offline' && isEmpty(inst, n)
                        && evalBool(n.required, inst, n, false)) { out.push(text(n.labels, n.name)); }
                } else if (n.kind === 'group') {
                    visit(inst, n.body || []);
                } else if (n.kind === 'repeat') {
                    (inst.repeats[n.name] || []).forEach(function (child) { visit(child, n.body || []); });
                }
            });
        })(root, root.node.body || []);
        return out;
    }

    function keyOf(inst, name) {
        var parts = chainOf(inst).slice(1).map(function (i) { return i.node.name + '.' + positionOf(i); });
        parts.push(name);
        return parts.join('/');
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Drawing.
    // ---------------------------------------------------------------------------------------------------------------

    var views = [];
    var seq = 0;

    // field-list (ADR-287 §5.10): a group shown as one page. Under the pages style a top-level group already is a page, so
    // what field-list adds is for a group inside one: it is lifted out to a page of its own, with its parent's heading
    // above it for context, and what lies around it in the parent keeps pages of its own on either side. Without the
    // pages style the whole form is one page and field-list has nothing to separate; the upload says so (a notice).
    var pieceCache = typeof WeakMap === 'function' ? new WeakMap() : null;

    function pagePieces(g) {
        if (pieceCache && pieceCache.has(g)) { return pieceCache.get(g); }
        var out = [], loose = [], lifted = false;
        function piece(nodes) { return { kind: 'group', name: g.name, labels: g.labels, relevant: g.relevant, appearance: g.appearance, body: nodes }; }
        function flush() { if (loose.length) { out.push(piece(loose)); loose = []; } }
        (g.body || []).forEach(function (child) {
            if (child.kind === 'group' && has(child.appearance, 'field-list')) { flush(); out.push(piece([child])); lifted = true; } else { loose.push(child); }
        });
        flush();
        var result = lifted ? out : [g];
        if (pieceCache) { pieceCache.set(g, result); }
        return result;
    }

    function pages() {
        var out = [], loose = null;
        (root.node.body || []).forEach(function (n) {
            if (n.kind === 'group') { loose = null; pagePieces(n).forEach(function (p) { out.push([p]); }); return; }
            if (!loose) { loose = []; out.push(loose); }
            loose.push(n);
        });
        return out;
    }

    function pageRelevant(page) {
        return page.some(function (n) {
            if (root.rel[n.name] === false) { return false; }
            return n.kind !== 'question' || hasUi(n);
        });
    }

    function render() {
        var body = $('body');
        var active = document.activeElement;
        var holder = active && active.closest ? active.closest('[data-key]') : null;
        var restoreKey = holder && body.contains(holder) ? holder.getAttribute('data-key') : null;
        var restoreFocus = active && active.getAttribute ? active.getAttribute('data-focus') : null;

        body.textContent = '';
        views = [];
        if (form.pages) {
            var list = pages();
            if (pageIndex >= list.length) { pageIndex = Math.max(0, list.length - 1); }
            // <b>Before drawing, not after</b> (UX review 2026-11-09): a page of loose questions with no screen of their
            // own, such as the `start` metadata a form begins with, was drawn as an empty first page, and only then did the
            // page controls move on, so the first real page was never shown and the form could not be finished.
            var drawable = relevantPages();
            if (drawable.length && drawable.indexOf(pageIndex) < 0) { pageIndex = drawable[0]; }
            renderBody(root, list[pageIndex] || [], body, 2);
        } else {
            renderBody(root, root.node.body || [], body, 2);
        }
        update();

        if (restoreKey) {
            var target = body.querySelector('[data-key="' + cssEscape(restoreKey) + '"]');
            var control = target && (restoreFocus ? target.querySelector('[data-focus="' + cssEscape(restoreFocus) + '"]') : null);
            control = control || (target && target.querySelector('input, select, textarea, button, canvas'));
            if (control) { control.focus(); }
        }
    }

    function cssEscape(s) { return window.CSS && CSS.escape ? CSS.escape(s) : String(s).replace(/["\\]/g, '\\$&'); }

    function heading(level, textIn, attrs) {
        return el('h' + Math.min(6, Math.max(2, level)), Object.assign({ text: textIn }, attrs || {}));
    }

    // Labels may hold ${name}, which XLSForm shows as that question's answer.
    function interpolate(template, inst) {
        return String(template || '').replace(/\$\{\s*([^}\s]+)\s*\}/g, function (all, name) {
            var v = valueIn(inst, name);
            if (Array.isArray(v)) { v = v.join(', '); }
            var d = defs[name];
            if (d && d.node.list && v) {
                var rows = choiceRows(d.node.list) || [];
                v = String(v).split(',').map(function (c) {
                    var row = rows.filter(function (r) { return String(r.name) === c; })[0];
                    return row ? choiceLabel(row) : c;
                }).join(', ');
            }
            return v === null || v === undefined ? '' : String(v);
        });
    }

    function renderBody(inst, body, container, level) {
        body.forEach(function (n) {
            if (n.kind === 'question') {
                if (hasUi(n)) { renderQuestion(inst, n, container); gridPlace(container, n); }
            } else if (n.kind === 'group') {
                renderGroup(inst, n, container, level);
                gridPlace(container, n);
            } else if (n.kind === 'repeat') {
                renderRepeat(inst, n, container, level);
                gridPlace(container, n);
            }
        });
    }

    // compact and minimal on a group or a repeat (ADR-287 §5.19): collapsed at startup, opened and closed by a button that
    // is the heading's own. A group that is a page under the pages style is not collapsible: a page that opens shut would
    // be an empty screen. The state is kept by key (a group) or by the item's GlobalID (a repeat's item) for the page's
    // life, so a redraw does not close what the person opened.
    var collapseState = {};

    function collapsible(appearance) { return has(appearance, 'compact') || has(appearance, 'minimal'); }

    function isCollapsed(key) { return Object.prototype.hasOwnProperty.call(collapseState, key) ? collapseState[key] : true; }

    // A heading whose text is a button that shows and hides `panel`.
    function collapseHeading(level, label, panel, key, extra) {
        var open = !isCollapsed(key);
        panel.hidden = !open;
        panel.setAttribute('data-collapse-key', key);
        var flag = el('span', { class: 'collapse-flag', hidden: true, text: ' — ' + t('needs attention') });
        var button = el('button', { type: 'button', class: 'quiet collapse-toggle', 'data-focus': 'toggle', 'aria-expanded': open ? 'true' : 'false', 'aria-controls': panel.id }, [el('span', { text: label }), flag]);
        button.addEventListener('click', function () {
            collapseState[key] = !panel.hidden;
            panel.hidden = !panel.hidden;
            button.setAttribute('aria-expanded', panel.hidden ? 'false' : 'true');
            flagUpdate();
        });
        function flagUpdate() { flag.hidden = !(panel.hidden && panel.querySelector('.question.invalid, .matrix-row.invalid')); }
        var h = el('h' + Math.min(6, Math.max(2, level)), extra || {}, [button]);
        h.flagUpdate = flagUpdate; // pushed by the caller after the body is drawn, so the flag sees this pass's messages
        return h;
    }

    // Opens every collapsed panel around a node, so a message inside one can be reached and its control focused.
    function expandAround(node) {
        for (var p = node && node.parentElement; p; p = p.parentElement) {
            if (p.hasAttribute && p.hasAttribute('data-collapse-key') && p.hidden) {
                collapseState[p.getAttribute('data-collapse-key')] = false;
                p.hidden = false;
                var button = document.querySelector('[aria-controls="' + cssEscape(p.id) + '"]');
                if (button) {
                    button.setAttribute('aria-expanded', 'true');
                    var flag = button.querySelector('.collapse-flag');
                    if (flag) { flag.hidden = true; }
                }
            }
        }
    }

    function renderGroup(inst, g, container, level) {
        var label = text(g.labels);
        var key = keyOf(inst, g.name);
        var box = el('section', { class: 'group', 'data-key': key });
        var foldable = collapsible(g.appearance) && !(form.pages && !inst.parent);
        var target = box, toggle = null;
        if (foldable) {
            target = el('div', { class: 'collapse-panel', id: 'g' + (++seq) });
            toggle = collapseHeading(level, label || g.name, target, key);
            box.appendChild(toggle);
            box.appendChild(target);
        } else if (label) {
            box.appendChild(heading(level, label, { class: form.pages && !inst.parent ? 'page-title' : null }));
        }
        container.appendChild(box);
        if (form.grid && widthOf(g.appearance) > 1 && target === box) { target = box.appendChild(el('div', { class: 'group-body' })); }
        gridGroup(target, g);
        if (has(g.appearance, 'table-list')) { renderTable(inst, g, target); } else { renderBody(inst, g.body || [], target, level + 1); }
        if (toggle) { views.push({ update: toggle.flagUpdate }); }
        views.push({ update: function () { box.hidden = inst.rel[g.name] === false; } });
    }

    // table-list (§5.10): a group of select questions over one list drawn as a table, a question to a row and a choice
    // to a column. The server refuses any other group with the appearance, so every child here is a select over the same
    // list. A radio or a checkbox in a cell is named by its row and its column together.
    function renderTable(inst, g, box) {
        var questions = (g.body || []).filter(function (n) { return n.kind === 'question'; });
        if (!questions.length) { return; }
        var listName = questions[0].list;
        var rows = choiceRows(listName);
        if (!rows) {
            box.appendChild(el('p', { class: 'note-warn', text: 'The list "' + listName + '" is not available yet: it was not uploaded with the form, or could not be read.' }));
            return;
        }
        var tid = 't' + (++seq);
        var table = el('table', { class: 'matrix' });
        table.appendChild(el('caption', { class: 'sr-only', text: text(g.labels, g.name) }));
        var headRow = el('tr', null, [el('th', { scope: 'col' }, [el('span', { class: 'sr-only', text: t('Question') })])]);
        rows.forEach(function (row, c) {
            var shown = choicePicture(row);
            headRow.appendChild(el('th', { scope: 'col', id: tid + '-c' + c }, [shown ? pictureNode('span', shown, 'choice-picture') : null, el('span', { text: choiceLabel(row) })]));
        });
        table.appendChild(el('thead', null, [headRow]));
        var bodyNode = el('tbody');
        table.appendChild(bodyNode);
        box.appendChild(el('div', { class: 'matrix-scroll', role: 'region', tabindex: '0', 'aria-label': text(g.labels, g.name) }, [table]));

        questions.forEach(function (q, r) {
            var key = keyOf(inst, q.name);
            var multiple = q.type === 'select_multiple';
            var rid = tid + '-r' + r;
            var labelTemplate = text(q.labels, q.name);
            var labelText = el('span', { text: interpolate(labelTemplate, inst) });
            var star = el('span', { class: 'required-mark', 'aria-hidden': 'true', text: '*', hidden: true });
            var error = el('p', { class: 'error', id: rid + '-error' });
            var th = el('th', { scope: 'row', id: rid + '-label' }, [labelText, star, error]);
            var tr = el('tr', { 'data-key': key, 'data-name': q.name, class: 'matrix-row type-' + q.type }, [th]);
            var controls = [];
            function values() { return current(inst, q).split(',').filter(Boolean); }
            rows.forEach(function (row, c) {
                var value = String(row.name);
                var input = el('input', { type: multiple ? 'checkbox' : 'radio', name: rid, value: value, 'data-focus': 'opt-' + value,
                    'aria-labelledby': rid + '-label ' + tid + '-c' + c, 'aria-describedby': rid + '-error' });
                input.addEventListener('change', function () {
                    if (multiple) {
                        var now = values().filter(function (v) { return v !== value; });
                        if (input.checked) { now.push(value); }
                        var order = rows.map(function (rw) { return String(rw.name); });
                        now.sort(function (a, b) { return order.indexOf(a) - order.indexOf(b); });
                        setValue(inst, q, now.join(','));
                    } else {
                        setValue(inst, q, value);
                    }
                });
                controls.push(input);
                // The whole cell answers, as a .choice row does elsewhere: a 22 px radio alone is too small a target on a phone.
                tr.appendChild(el('td', null, [el('label', { class: 'cell-hit' }, [input])]));
            });
            bodyNode.appendChild(tr);
            views.push({
                inst: inst, q: q, key: key, node: tr,
                update: function () {
                    var relevant = inst.relevant !== false && inst.rel[q.name] !== false;
                    tr.hidden = !(relevant && evalBool(q.visible, inst, q, true));
                    if (!relevant) { return; }
                    if (labelTemplate.indexOf('${') >= 0) { labelText.textContent = interpolate(labelTemplate, inst); }
                    var required = evalBool(q.required, inst, q, false);
                    star.hidden = !required;
                    var readOnly = evalBool(q.readOnly, inst, q, false) || lockedRow(inst);
                    var set = values();
                    var message = (showAll || inst.touched[q.name]) ? messageFor(inst, q) : '';
                    error.textContent = message;
                    tr.classList.toggle('invalid', !!message);
                    controls.forEach(function (c) {
                        c.checked = set.indexOf(c.value) >= 0;
                        c.disabled = !!readOnly;
                        if (message) { c.setAttribute('aria-invalid', 'true'); } else { c.removeAttribute('aria-invalid'); }
                        if (required) { c.setAttribute('aria-required', 'true'); } else { c.removeAttribute('aria-required'); }
                    });
                }
            });
        });
    }

    function renderRepeat(inst, r, container, level) {
        var label = text(r.labels, r.name);
        var box = el('section', { class: 'repeat', 'data-key': keyOf(inst, r.name) });
        box.appendChild(heading(level, label));
        container.appendChild(box);
        var list = inst.repeats[r.name] || [];
        list.forEach(function (child, i) {
            var item = el('div', { class: 'repeat-instance', 'data-key': keyOf(child, '') });
            var foldable = collapsible(r.appearance);
            var inner = foldable ? el('div', { class: 'collapse-panel', id: 'r' + (++seq) }) : item;
            var toggle = foldable ? collapseHeading(level + 1, label + ' ' + (i + 1), inner, 'item:' + child.id) : null;
            var head = el('div', { class: 'instance-head' }, [toggle || heading(level + 1, label + ' ' + (i + 1))]);
            if (child.existing) {
                // A row the server holds, opened from the Inbox: never removed here, and changed only where the repeat allows it.
                head.appendChild(el('span', { class: 'existing-mark', text: child.existing.locked ? t('On the server; cannot be changed here') : t('On the server') }));
            } else if (!r.repeatCount) {
                var remove = el('button', { type: 'button', class: 'quiet', 'data-focus': 'remove', text: t('Remove') });
                remove.setAttribute('aria-label', isTurkish() ? label + ' ' + (i + 1) + ' kaldır' : 'Remove ' + label + ' ' + (i + 1));
                remove.addEventListener('click', function () {
                    // A field crew taps by accident; an item with answers, or with items of its own, is asked about first.
                    var answered = Object.keys(child.values || {}).some(function (k) { var v = child.values[k]; return v !== undefined && v !== null && v !== ''; })
                        || Object.keys(child.repeats || {}).some(function (k) { return (child.repeats[k] || []).length > 0; });
                    var n = list.indexOf(child) + 1;
                    if (answered && !window.confirm(isTurkish() ? label + ' ' + n + ' ve içindeki cevaplar kaldırılsın mı?' : 'Remove ' + label + ' ' + n + ' and its answers?')) { return; }
                    var at = list.indexOf(child);
                    if (at >= 0) { list.splice(at, 1); }
                    refresh(true);
                    scheduleDraft();
                    var add = document.querySelector('[data-key="' + cssEscape(keyOf(inst, r.name)) + '"] [data-focus="add"]');
                    if (add) { add.focus(); }
                });
                head.appendChild(remove);
            }
            item.appendChild(head);
            if (foldable) { item.appendChild(inner); }
            renderBody(child, r.body || [], inner, level + 2);
            if (toggle) { views.push({ update: toggle.flagUpdate }); }
            box.appendChild(item);
        });
        if (!r.repeatCount && !(editing && repeatParameter(r, 'allowAdds') === false)) {
            var add = el('button', { type: 'button', class: 'quiet', 'data-focus': 'add', text: isTurkish() ? label + ' ekle' : 'Add ' + label });
            add.addEventListener('click', function () {
                var made = newInstance(r, inst);
                collapseState['item:' + made.id] = false; // an item just added is open: the person is about to fill it in
                list.push(made);
                refresh(true);
                scheduleDraft();
                var items = document.querySelectorAll('[data-key="' + cssEscape(keyOf(inst, r.name)) + '"] > .repeat-instance');
                var last = items[items.length - 1];
                var first = last && last.querySelector('input, select, textarea');
                if (first) { first.focus(); } else if (last) { var b = last.querySelector('button'); if (b) { b.focus(); } }
            });
            box.appendChild(el('div', { class: 'inline' }, [add]));
        }
        views.push({ update: function () { box.hidden = inst.rel[r.name] === false; } });
    }

    function setValue(inst, q, value, structural) {
        inst.values[q.name] = value;
        inst.touched[q.name] = true;
        refresh(structural);
        scheduleDraft();
    }

    function renderQuestion(inst, q, container) {
        var id = 'q' + (++seq);
        var key = keyOf(inst, q.name);
        var wrap = el('div', { class: 'question type-' + q.type, 'data-key': key, 'data-name': q.name });
        var labelTemplate = text(q.labels, q.name);
        var hintText = text(q.hints);
        var group = q.type === 'select_one' || q.type === 'select_multiple' || q.type === 'select_one_from_file'
            || q.type === 'select_multiple_from_file' || (q.type === 'range' && has(q.appearance, 'rating'));
        // A drop-down and a type-ahead box are one control with one label; the rest of the selects are a set of them.
        var minimal = has(q.appearance, 'minimal') || isCombo(q);
        var useFieldset = group && !minimal;

        var labelText = el('span', { text: interpolate(labelTemplate, inst) });
        var star = el('span', { class: 'required-mark', 'aria-hidden': 'true', text: '*', hidden: true });
        var labelNode = useFieldset || q.type === 'note' || q.type === 'geopoint' || q.type === 'geotrace' || q.type === 'geoshape'
            || isMedia(q)
            ? el(useFieldset ? 'legend' : 'div', { class: 'label', id: id + '-label' }, [labelText, star])
            : el('label', { for: id, id: id + '-label' }, [labelText, star]);
        var hint = hintText ? el('p', { class: 'hint', id: id + '-hint', text: hintText }) : null;
        var picture = pictureName(q.images);
        var pictureBlock = picture ? pictureNode('div', picture, 'question-picture') : null;
        var sound = soundName(q.audio);
        var soundBlock = sound ? soundNode(sound, labelTemplate, 'question-sound') : null;
        var warning = q.warning ? el('p', { class: 'warning', id: id + '-warning', role: 'status' }) : null;
        var guidance = guidanceNodes(q, id, key);
        // No live region per question: the summary is the one alert, and two announcements per error was noise (UX review).
        var error = el('p', { class: 'error', id: id + '-error' });
        var describedBy = (hint ? id + '-hint ' : '') + id + '-error' + (warning ? ' ' + id + '-warning' : '');

        var shell = useFieldset ? el('fieldset', { 'aria-describedby': describedBy }) : wrap;
        if (useFieldset) { wrap.appendChild(shell); }
        shell.appendChild(labelNode);
        if (pictureBlock) { shell.appendChild(pictureBlock); }
        if (soundBlock) { shell.appendChild(soundBlock); }
        if (hint) { shell.appendChild(hint); }
        if (guidance) { shell.appendChild(guidance.button); shell.appendChild(guidance.panel); }

        var widget = buildWidget(inst, q, id, describedBy);
        shell.appendChild(widget.node);
        if (isMedia(q) && existingFilesOf(inst, q.name).length) { shell.appendChild(existingFilesNode(inst, q, id)); }
        if (q.calculation && (q.calculationMode === 'manual' || q.calculationMode === 'whenEmpty') && hasUi(q)) {
            var calculate = el('button', { type: 'button', class: 'quiet', 'data-focus': 'calculate', 'aria-describedby': id + '-label', text: t('Calculate') });
            var calculated = el('p', { class: 'hint', 'aria-live': 'polite' });
            calculate.addEventListener('click', function () {
                var v = calculateNow(inst, q);
                // Emptied first, so a second press is announced again; an empty result is said, not called a calculation.
                calculated.textContent = '';
                setTimeout(function () { calculated.textContent = t(v === '' ? 'Nothing to calculate yet: answer the questions it uses first.' : 'Calculated.'); }, 50);
            });
            shell.appendChild(el('div', { class: 'inline' }, [calculate]));
            shell.appendChild(calculated);
        }
        // pulldata("@layer") (section 5.23): what the lookup is doing, said where the answer is, and announced.
        var lookup = q.calculation && /@layer|@javascript/.test(q.calculation) ? el('p', { class: 'hint layer-status', id: id + '-lookup', 'aria-live': 'polite' }) : null;
        if (lookup) { shell.appendChild(lookup); }
        // "Looking this up…" is said only when the answer has not come back within half a second: a lookup per keystroke
        // in the question it reads otherwise announced it, and blanked it, once a letter (UX review 2026-11-16).
        var lookupTimer = 0;
        var settleTimer = 0;
        function sayLookup() {
            // A script's line (section 5.27) is not blanked while its next call is out, and a changed line is written
            // once it has held for a moment: blanking it and writing it back announced the same failure on every
            // keystroke in a question the script reads (UX review 2026-11-17).
            if (!layerStatusText(inst, q) && scriptStatusPending(inst, q)) { return; }
            if (q.calculation && /@javascript/.test(q.calculation) && !layerStatusText(inst, q)) {
                var line = scriptStatusText(inst, q);
                clearTimeout(settleTimer);
                settleTimer = 0;
                if (lookup.textContent === line) { return; }
                if (line === '') { lookup.textContent = ''; return; }
                settleTimer = setTimeout(function () {
                    settleTimer = 0;
                    var now = scriptStatusPending(inst, q) ? null : scriptStatusText(inst, q);
                    if (now !== null && lookup.isConnected && lookup.textContent !== now) { lookup.textContent = now; }
                }, SCRIPT_STATUS_SETTLE_MS);
                return;
            }
            var said = layerStatusText(inst, q) || scriptStatusText(inst, q);
            if (said === t('Looking this up…') && lookup.textContent !== said) {
                if (!lookupTimer) {
                    lookupTimer = setTimeout(function () {
                        lookupTimer = 0;
                        var now = layerStatusText(inst, q) || scriptStatusText(inst, q);
                        if (lookup.isConnected && lookup.textContent !== now) { lookup.textContent = now; }
                    }, 500);
                }
                return;
            }
            clearTimeout(lookupTimer);
            lookupTimer = 0;
            if (lookup.textContent !== said) { lookup.textContent = said; }
        }
        wrap.appendChild(error);
        if (warning) { wrap.appendChild(warning); }
        if (q.type === 'note' && labelNode.tagName === 'DIV') { wrap.classList.add('note'); }
        container.appendChild(wrap);

        views.push({
            inst: inst, q: q, key: key, node: wrap,
            update: function () {
                var relevant = inst.relevant !== false && inst.rel[q.name] !== false;
                var visible = relevant && evalBool(q.visible, inst, q, true);
                wrap.hidden = !visible;
                if (!relevant) { return; }
                if (labelTemplate.indexOf('${') >= 0) { labelText.textContent = interpolate(labelTemplate, inst); }
                var required = evalBool(q.required, inst, q, false);
                star.hidden = !required;
                var readOnly = !!q.calculation && q.type !== 'note' ? evalBool(q.readOnly, inst, q, true) : evalBool(q.readOnly, inst, q, false);
                if (lockedRow(inst)) { readOnly = true; }
                if (widget.sync) { widget.sync(readOnly, required); }
                if (lookup) { sayLookup(); }
                var message = (showAll || inst.touched[q.name]) ? messageFor(inst, q) : '';
                error.textContent = message;
                wrap.classList.toggle('invalid', !!message);
                if (warning) {
                    var cautious = !message && (showAll || inst.touched[q.name]) ? warningFor(inst, q) : '';
                    // Named in words, so a warning is not told from an error by its colour alone, and a screen reader
                    // hears that it is one (UX review 2026-11-17).
                    var cautionSaid = cautious ? t('Warning:') + ' ' + cautious : '';
                    if (warning.textContent !== cautionSaid) {
                        // One change, so it is announced once.
                        if (cautious) { warning.replaceChildren(el('strong', { text: t('Warning:') }), document.createTextNode(' ' + cautious)); }
                        else { warning.textContent = ''; }
                    }
                    wrap.classList.toggle('warned', !!cautious);
                }
                if (widget.controls) {
                    widget.controls().forEach(function (c) {
                        if (message) { c.setAttribute('aria-invalid', 'true'); } else { c.removeAttribute('aria-invalid'); }
                        if (required) { c.setAttribute('aria-required', 'true'); } else { c.removeAttribute('aria-required'); }
                    });
                }
            }
        });
    }

    // guidance_hint: longer help, hidden until it is asked for, as Survey123 shows it (its documentation: "hidden until the
    // respondent taps a button next to the hint"). INFERRED: it is offered even where the question has no hint, which
    // Survey123 does not do, because a column that is accepted and then shows nothing is the thing §5.10 forbids.
    var guidanceOpen = {};

    function guidanceNodes(q, id, key) {
        var words = text(q.guidanceHints);
        if (!words) { return null; }
        var open = !!guidanceOpen[key];
        var button = el('button', { type: 'button', class: 'quiet guidance-toggle', id: id + '-more', 'data-focus': 'guidance',
            'aria-expanded': open ? 'true' : 'false', 'aria-controls': id + '-guidance', 'aria-describedby': id + '-label', text: t('More help') });
        var panel = el('div', { class: 'guidance', id: id + '-guidance', hidden: open ? null : true }, [el('p', { text: words })]);
        button.addEventListener('click', function () {
            guidanceOpen[key] = panel.hidden;
            panel.hidden = !panel.hidden;
            button.setAttribute('aria-expanded', panel.hidden ? 'false' : 'true');
        });
        return { button: button, panel: panel };
    }

    function current(inst, q) { var v = inst.values[q.name]; return v === undefined || v === null ? '' : String(v); }

    function syncInput(input, value, readOnly) {
        if (document.activeElement !== input && input.value !== value) { input.value = value; }
        input.readOnly = !!readOnly;
    }

    function buildWidget(inst, q, id, describedBy) {
        switch (q.type) {
            case 'note': return noteWidget(inst, q);
            case 'integer': case 'decimal': return numberWidget(inst, q, id, describedBy);
            case 'range': return has(q.appearance, 'rating') ? ratingWidget(inst, q, id) : rangeWidget(inst, q, id, describedBy);
            case 'select_one': case 'select_multiple': case 'select_one_from_file': case 'select_multiple_from_file':
                return selectWidget(inst, q, id, describedBy);
            case 'acknowledge': return acknowledgeWidget(inst, q, id, describedBy);
            case 'barcode': return barcodeWidget(inst, q, id, describedBy);
            case 'date': return dateWidget(inst, q, id, describedBy);
            case 'time': return timeWidget(inst, q, id, describedBy);
            case 'dateTime': return dateTimeWidget(inst, q, id, describedBy);
            case 'geopoint': return geopointWidget(inst, q, id);
            case 'geotrace': case 'geoshape': return verticesWidget(inst, q, id);
            case 'image': case 'audio': case 'video': case 'file':
                if (q.type === 'image' && has(q.appearance, 'signature')) { return signatureWidget(inst, q, id, false); }
                if (q.type === 'image' && has(q.appearance, 'draw')) { return signatureWidget(inst, q, id, true); }
                if (q.type === 'image' && has(q.appearance, 'annotate')) { return annotateWidget(inst, q, id, describedBy); }
                return mediaWidget(inst, q, id, describedBy);
            default: return textWidget(inst, q, id, describedBy);
        }
    }

    function noteWidget(inst, q) {
        var out = el('div', { class: 'value-out' });
        return {
            node: out,
            sync: function () { out.textContent = q.calculation ? current(inst, q) : ''; out.hidden = !q.calculation; }
        };
    }

    function textWidget(inst, q, id, describedBy) {
        if (q.inputMask && parseMask(q.inputMask)) { return maskedTextWidget(inst, q, id, describedBy); }
        var multi = has(q.appearance, 'multiline');
        var input = multi ? el('textarea', { id: id, 'aria-describedby': describedBy, 'data-focus': 'input' })
            : el('input', { type: 'text', id: id, 'aria-describedby': describedBy, 'data-focus': 'input' });
        input.value = current(inst, q);
        input.addEventListener('input', function () { setValue(inst, q, input.value); });
        return { node: input, controls: function () { return [input]; }, sync: function (ro) { syncInput(input, current(inst, q), ro); } };
    }

    // ----- Barcode (ADR-287 §5.22). The answer is always a text box, so it can be typed on every browser; where the page
    // has a camera a Scan button opens the rear camera and puts the first code read into the box. The reading is
    // barcode-reader.js's (GraticulaBarcode): the browser's own BarcodeDetector where there is one, ZXing everywhere else
    // (owner decision 2026-11-17), loaded only when Scan is pressed and kept by the service worker, so it works offline.
    // This file never names a ZXing type.
    var barcodeFormats;      // undefined until asked; null where there is no camera; the reader where there is
    var barcodeAsked = null;
    var barcodeScan = null;  // the one scan running: { stop: function (how) }
    var barcodeSaid = {};    // what a scan said, by question key, so a redraw that replaced the widget still says it
    var barcodeTyped = {};   // keys of minimal questions whose box was shown because the camera failed
    var barcodeHideHeard = false;

    function barcodeReader() {
        if (!barcodeAsked) {
            barcodeAsked = (window.GraticulaBarcode ? window.GraticulaBarcode.reader() : Promise.resolve(null))
                .catch(function () { return null; })
                .then(function (r) { barcodeFormats = r || null; return barcodeFormats; });
        }
        return barcodeAsked;
    }

    function stopBarcodeScan(how) {
        if (barcodeScan) { var s = barcodeScan; barcodeScan = null; s.stop(how); }
    }

    function barcodeWidget(inst, q, id, describedBy) {
        var key = keyOf(inst, q.name);
        // Survey123's minimal ("only the button to start the scanner") and ODK's hidden-answer: the box is not shown, but only
        // where there is a reader. Without one, or once the camera has failed, the box is the only way to answer.
        var answerHidden = has(q.appearance, 'minimal') || has(q.appearance, 'hidden-answer');
        var input = el('input', {
            type: 'text', id: id, 'aria-describedby': describedBy + ' ' + id + '-scan', 'data-focus': 'input',
            autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', enterkeyhint: 'done'
        });
        input.value = current(inst, q);
        input.addEventListener('input', function () { setValue(inst, q, input.value); });
        var scan = el('button', { type: 'button', class: 'quiet', 'data-focus': 'scan', 'aria-describedby': id + '-label', text: t('Scan'), hidden: true });
        var cancel = el('button', { type: 'button', class: 'quiet', 'data-focus': 'cancel-scan', text: t('Cancel scan') });
        var video = el('video', { playsinline: true, autoplay: true, 'aria-label': t('Camera preview') });
        video.muted = true;
        // The instruction is the status line's alone: written here as well it stood twice under the preview (UX review 2026-11-16).
        var panel = el('div', { class: 'scanner', hidden: true }, [video, el('div', { class: 'inline' }, [cancel])]);
        // With no camera API the reason is usually the address: a page that is not https has none, on every browser.
        var insecure = window.GraticulaBarcode && !window.GraticulaBarcode.secure();
        var noReader = el('p', { class: 'hint', id: id + '-no-reader', hidden: true,
            text: insecure ? t('Scanning needs the form to be opened at an https address. Type the code instead.') : t('This browser cannot scan codes. Type the code instead.') });
        var status = el('p', { class: 'hint', id: id + '-scan', 'aria-live': 'polite' });
        // Where the box is hidden (minimal, hidden-answer) the code read is still shown, as text: a person who cannot see
        // which code went in cannot catch a wrong label, and after a draft reload nothing else said there was one
        // (UX review 2026-11-16). It describes the Scan button, which is where focus is.
        var shownValue = el('p', { class: 'value-out barcode-value', id: id + '-value', hidden: true });
        scan.setAttribute('aria-describedby', id + '-label ' + id + '-value');
        var readOnly = false;
        var mine = null;

        // Emptied first and set a moment later, so the same words twice are announced twice (the Calculate button's way).
        function flush() { if (status.isConnected && barcodeSaid[key]) { status.textContent = barcodeSaid[key]; delete barcodeSaid[key]; } }
        function say(words) { barcodeSaid[key] = words; status.textContent = ''; setTimeout(flush, 50); }
        if (barcodeSaid[key]) { setTimeout(flush, 50); }

        function apply() {
            var reader = !!barcodeFormats;
            scan.hidden = !reader;
            noReader.hidden = reader;
            input.hidden = reader && answerHidden && !barcodeTyped[key];
            showValue();
            input.setAttribute('aria-describedby', describedBy + ' ' + id + '-scan' + (reader ? '' : ' ' + id + '-no-reader'));
        }
        function showValue() {
            var v = current(inst, q);
            shownValue.hidden = !input.hidden || v === '';
            if (shownValue.textContent !== v) { shownValue.textContent = v; }
        }
        if (barcodeFormats !== undefined) { apply(); } else { barcodeReader().then(apply); }

        function start() {
            if (readOnly || !barcodeFormats || mine) { return; }
            stopBarcodeScan('replaced');
            if (!barcodeHideHeard) {
                barcodeHideHeard = true;
                // The camera is never left on behind the page: leaving it, or hiding it, stops the scan.
                window.addEventListener('pagehide', function () { stopBarcodeScan('hidden'); });
                document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'hidden') { stopBarcodeScan('hidden'); } });
            }
            var stream = null, timer = 0, done = false, reader = barcodeFormats, ready = false;
            var session = { stop: function (how) { finish(how); } };
            mine = session;
            barcodeScan = session;
            panel.hidden = false;
            scan.disabled = true;
            say(t('Opening the camera…'));
            // The reader is made ready while the camera opens: for ZXing that is the library's first load, which the
            // service worker answers from its copy offline. Without it nothing can read, so the scan stops and says so.
            reader.prepare().then(function () { ready = true; if (stream && !done) { tick(); } }, function () { finish(reader.engine === 'zxing' ? 'noreader' : 'failed'); });
            navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false }).then(function (s) {
                stream = s;
                if (done) { release(); return; }
                video.srcObject = s;
                var playing = video.play();
                if (playing && playing.catch) { playing.catch(function () { /* autoplay is muted; a refusal here leaves the preview still */ }); }
                say(t('Point the camera at the code.'));
                cancel.focus();
                if (ready) { tick(); }
            }, function (e) {
                var name = e && e.name;
                finish(name === 'NotAllowedError' || name === 'SecurityError' ? 'refused'
                    : name === 'NotFoundError' || name === 'OverconstrainedError' ? 'nocamera' : 'failed');
            });

            function tick() {
                if (done) { return; }
                // A redraw replaced this widget: its camera goes with it.
                if (!video.isConnected) { finish('gone'); return; }
                clearTimeout(timer);
                reader.detect(video).then(function (value) {
                    if (done) { return; }
                    if (value) { finish('read', value); } else { timer = setTimeout(tick, 200); }
                }, function () { finish('unreadable'); });
            }
            function release() {
                if (stream) { stream.getTracks().forEach(function (track) { try { track.stop(); } catch (e) { /* already stopped */ } }); stream = null; }
                video.srcObject = null;
            }
            function finish(how, value) {
                if (done) { return; }
                done = true;
                clearTimeout(timer);
                if (barcodeScan === session) { barcodeScan = null; }
                if (mine === session) { mine = null; }
                release();
                panel.hidden = true;
                scan.disabled = readOnly;
                var failed = {
                    refused: 'Camera permission was refused. Type the code instead.',
                    nocamera: 'No camera was found. Type the code instead.',
                    failed: 'The camera could not be started. Type the code instead.',
                    unreadable: 'The code could not be read. Type the code instead.',
                    noreader: 'The code reader could not be loaded. Connect once to download it, or type the code instead.'
                }[how];
                if (how === 'read') {
                    input.value = value;
                    say(input.hidden ? t('Code read: ') + value : t('Code read.'));
                    (input.hidden ? scan : input).focus();
                    setValue(inst, q, value);
                } else if (failed) {
                    barcodeTyped[key] = true;
                    input.hidden = false;
                    showValue();
                    say(t(failed));
                    if (video.isConnected) { input.focus(); }
                } else if (how === 'cancel') {
                    say(t('Scan cancelled.'));
                    scan.focus();
                } else if (how === 'hidden') {
                    say(t('Scanning stopped because the page was left.'));
                }
            }
        }

        scan.addEventListener('click', start);
        cancel.addEventListener('click', function () { if (mine) { mine.stop('cancel'); } });
        return {
            node: el('div', { class: 'barcode' }, [input, shownValue, el('div', { class: 'inline' }, [scan]), noReader, panel, status]),
            controls: function () { return [input]; },
            sync: function (ro) {
                readOnly = !!ro;
                syncInput(input, current(inst, q), ro);
                showValue();
                scan.disabled = readOnly || !!mine;
                if (readOnly && mine) { mine.stop('cancel'); }
            }
        };
    }

    function numberWidget(inst, q, id, describedBy) {
        if (has(q.appearance, 'spinner')) { return spinnerWidget(inst, q, id, describedBy); }
        if (q.type === 'integer' && has(q.appearance, 'distress')) { return distressWidget(inst, q, id); }
        var input = el('input', {
            type: 'text', id: id, 'aria-describedby': describedBy, 'data-focus': 'input',
            inputmode: q.type === 'integer' ? 'numeric' : 'decimal', autocomplete: 'off'
        });
        input.value = current(inst, q);
        input.addEventListener('input', function () { setValue(inst, q, input.value.trim().replace(',', '.')); });
        return { node: input, controls: function () { return [input]; }, sync: function (ro) { syncInput(input, current(inst, q), ro); } };
    }

    function rangeParams(q) {
        var p = q.parameters || {};
        var start = p.start !== undefined && p.start !== '' ? Number(p.start) : 1;
        var end = p.end !== undefined && p.end !== '' ? Number(p.end) : 10;
        var step = p.step !== undefined && p.step !== '' ? Number(p.step) : 1;
        return { start: start, end: end, step: step || 1 };
    }

    function rangeWidget(inst, q, id, describedBy) {
        var r = rangeParams(q);
        var input = el('input', {
            type: 'range', id: id, min: Math.min(r.start, r.end), max: Math.max(r.start, r.end), step: Math.abs(r.step),
            'aria-describedby': describedBy, 'data-focus': 'input'
        });
        var out = el('output', { for: id, text: '—' });
        var clear = el('button', { type: 'button', class: 'quiet', 'data-focus': 'clear', text: t('Clear') });
        input.addEventListener('input', function () { setValue(inst, q, input.value); });
        clear.addEventListener('click', function () { setValue(inst, q, ''); });
        return {
            node: el('div', { class: 'range-row' }, [input, out, clear]),
            controls: function () { return [input]; },
            sync: function (ro) {
                var v = current(inst, q);
                if (document.activeElement !== input) { input.value = v !== '' ? v : String(Math.min(r.start, r.end)); }
                out.textContent = v === '' ? '—' : v;
                input.setAttribute('aria-valuetext', v === '' ? 'not answered' : v);
                // Unanswered shows no thumb, so the slider does not look answered at its first value (UX review 2026-11-09).
                if (v === '') { input.removeAttribute('data-set'); } else { input.setAttribute('data-set', ''); }
                input.disabled = !!ro; clear.disabled = !!ro || v === '';
            }
        };
    }

    function ratingWidget(inst, q, id) {
        var r = rangeParams(q);
        var box = el('div', { class: 'choices', role: 'radiogroup' });
        var radios = [];
        var lo = Math.min(r.start, r.end), hi = Math.max(r.start, r.end), step = Math.abs(r.step);
        for (var v = lo; v <= hi + 1e-9; v += step) {
            var value = String(Math.round(v * 1e6) / 1e6);
            var radio = el('input', { type: 'radio', name: id, value: value, 'data-focus': 'opt-' + value });
            radio.addEventListener('change', (function (val) { return function () { setValue(inst, q, val); }; })(value));
            radios.push(radio);
            box.appendChild(el('label', { class: 'choice' }, [radio, el('span', { text: value })]));
        }
        var wrap = el('div', { class: 'rating' }, [box]);
        return {
            node: wrap,
            controls: function () { return radios; },
            sync: function (ro) {
                var cur = current(inst, q);
                radios.forEach(function (rd) { rd.checked = rd.value === cur; rd.disabled = !!ro; rd.parentNode.classList.toggle('on', rd.checked); });
            }
        };
    }

    // The names a select offers now: its list, through choice_filter when it has one. The filter is evaluated by the
    // evaluator itself, as instance('list')/root/item[filter]/name, so a filter means what the specification says.
    function availableChoices(inst, q) {
        var listName = q.list || q.file;
        var rows;
        var ctx = contextFor(inst, q);
        if (q.search) {
            // search() (section 5.23): the list's choices are the CSV's or the layer's rows, and a choice_filter reads those.
            var found = searchChoices(inst, q);
            if (found.state !== 'done') { return null; }
            rows = found.rows;
            var own = ctx.choices;
            ctx.choices = function (name) { return name === listName ? rows : own(name); };
        } else {
            rows = choiceRows(listName);
        }
        if (!rows) { return null; }
        if (!q.choiceFilter) { return rows; }
        try {
            var result = X.evaluate("instance('" + String(listName).replace(/'/g, '') + "')/root/item[" + q.choiceFilter + ']/name', ctx);
            var names = result && Array.isArray(result.nodes) ? result.nodes.map(function (n) { return String(n.value); }) : [];
            return rows.filter(function (row) { return row.__auto || names.indexOf(String(row.name)) >= 0; });
        } catch (e) {
            warn(q.choiceFilter, e);
            return rows;
        }
    }

    // A type-ahead box: autocomplete on a select_one, and a select_one with search(), which Survey123's web app draws as
    // one (its Multiple choice page), unless the form asks for a drop-down with minimal.
    function isCombo(q) {
        if (q.type !== 'select_one' && q.type !== 'select_one_from_file') { return false; }
        return has(q.appearance, 'autocomplete') || (!!q.search && !has(q.appearance, 'minimal'));
    }

    function selectWidget(inst, q, id, describedBy) {
        var multiple = q.type.indexOf('select_multiple') === 0;
        if (isCombo(q)) { return autocompleteWidget(inst, q, id, describedBy); }
        var minimal = has(q.appearance, 'minimal');
        var likert = has(q.appearance, 'likert') && !multiple;
        var holder = el('div', { class: likert ? 'likert' : null });
        var shown = null; // the names drawn, joined, to know when the filter changed them
        var controls = [];
        var unavailable = el('p', { class: 'note-warn', role: 'status', hidden: true });   // announced: a search() list loads after the page (section 5.23)

        function values() { return current(inst, q).split(',').filter(Boolean); }

        function draw(rows) {
            var focusedValue = document.activeElement && holder.contains(document.activeElement) ? document.activeElement.value : null;
            holder.textContent = '';
            controls = [];
            if (minimal) {
                var select = el('select', { id: id, 'aria-describedby': describedBy, 'data-focus': 'input', multiple: multiple ? true : null });
                if (!multiple) { select.appendChild(el('option', { value: '', text: '—' })); }
                rows.forEach(function (row) { select.appendChild(el('option', { value: String(row.name), text: choiceLabel(row) })); });
                select.addEventListener('change', function () {
                    var chosen = Array.prototype.filter.call(select.options, function (o) { return o.selected && o.value !== ''; })
                        .map(function (o) { return o.value; });
                    setValue(inst, q, chosen.join(','));
                });
                controls.push(select);
                holder.appendChild(select);
                if (focusedValue !== null) { select.focus(); }
                return;
            }
            var box = el('div', { class: 'choices' });
            rows.forEach(function (row, i) {
                var value = String(row.name);
                var input = el('input', { type: multiple ? 'checkbox' : 'radio', name: id, value: value, id: id + '-' + i, 'data-focus': 'opt-' + value });
                input.addEventListener('change', function () {
                    if (multiple) {
                        var now = values().filter(function (v) { return v !== value; });
                        if (input.checked) { now.push(value); }
                        // Keep the list's order, so the stored text does not depend on the order of the taps.
                        var order = rows.map(function (rw) { return String(rw.name); });
                        now.sort(function (a, b) { return order.indexOf(a) - order.indexOf(b); });
                        setValue(inst, q, now.join(','));
                    } else {
                        setValue(inst, q, value);
                    }
                });
                controls.push(input);
                var shown = choicePicture(row);
                box.appendChild(el('label', { class: 'choice', for: id + '-' + i }, [input, shown ? pictureNode('span', shown, 'choice-picture') : null, el('span', { text: choiceLabel(row) })]));
                var heard = choiceSound(row);
                if (heard) { box.appendChild(soundNode(heard, choiceLabel(row), 'choice-sound')); }
                if (focusedValue === value) { setTimeout(function () { input.focus(); }, 0); }
            });
            holder.appendChild(box);
        }

        return {
            node: el('div', null, [unavailable, holder]),
            controls: function () { return controls; },
            sync: function (ro) {
                var rows = availableChoices(inst, q);
                if (!rows) {
                    unavailable.hidden = false;
                    // Written only when it changes: a status region rewritten with the same words is announced again, and
                    // sync runs on every keystroke anywhere in the form (UX review 2026-11-16).
                    var unsaid = listUnavailableText(inst, q);
                    if (unavailable.textContent !== unsaid) { unavailable.textContent = unsaid; }
                    holder.textContent = '';
                    controls = [];
                    return;
                }
                unavailable.hidden = true;
                var names = rows.map(function (r) { return String(r.name); });
                var signature = names.join('\u0001') + '|' + lang;
                if (signature !== shown) { shown = signature; draw(rows); }
                // An answer the filter no longer offers is cleared, as ODK clients do.
                var kept = values().filter(function (v) { return names.indexOf(v) >= 0; });
                if (kept.join(',') !== values().join(',')) { inst.values[q.name] = kept.join(','); setTimeout(function () { refresh(false); }, 0); }
                var set = values();
                var seenValue = {}; // a name that appears twice in the list (ADR-287 §5.19) is shown chosen once, at its first
                controls.forEach(function (c) {
                    if (c.tagName === 'SELECT') {
                        Array.prototype.forEach.call(c.options, function (o) {
                            var on = o.value === '' ? set.length === 0 && !multiple : set.indexOf(o.value) >= 0 && !seenValue[o.value];
                            if (o.value !== '' && on) { seenValue[o.value] = true; }
                            o.selected = on;
                        });
                    } else {
                        var checked = set.indexOf(c.value) >= 0 && !seenValue[c.value];
                        if (checked) { seenValue[c.value] = true; }
                        c.checked = checked;
                    }
                    c.disabled = !!ro;
                });
            }
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Survey123's own widgets (ADR-287 §5.19): autocomplete, an input mask, a spinner and a week number.
    // ---------------------------------------------------------------------------------------------------------------

    // Letters compared without case or accent, and the Turkish dotted and dotless i as one letter, so "istanbul" finds
    // "İstanbul" and "Ilgaz" and "sehir" finds "Şehir". INFERRED (§5.19).
    function fold(s) {
        return String(s === null || s === undefined ? '' : s).toLowerCase().replace(/ı/g, 'i').replace(/i̇/g, 'i')
            .normalize('NFD').replace(/[̀-ͯ]/g, '');
    }

    var COMBO_LIMIT = 100;

    // autocomplete (select_one): a WAI-ARIA 1.2 combobox with a listbox. Typing narrows the choices to those whose label
    // contains the text; the arrows and Enter choose; Escape closes. Everything is on the device, so it works offline,
    // and the list is the question's choices after its choice_filter. The list sits in the page's flow under the box
    // instead of floating over it, so a phone's keyboard cannot cover the choice being typed for.
    function autocompleteWidget(inst, q, id, describedBy) {
        var input = el('input', {
            type: 'text', id: id, role: 'combobox', 'aria-autocomplete': 'list', 'aria-expanded': 'false', 'aria-controls': id + '-list',
            'aria-describedby': describedBy, 'data-focus': 'input', autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', enterkeyhint: 'done'
        });
        var list = el('ul', { id: id + '-list', role: 'listbox', class: 'combo-list', hidden: true, 'aria-label': text(q.labels, q.name) });
        var status = el('p', { class: 'hint combo-status', role: 'status', 'aria-live': 'polite' });
        var unavailable = el('p', { class: 'note-warn', role: 'status', hidden: true });   // announced: a search() list loads after the page (section 5.23)
        var rows = [];
        var matches = [];
        var active = -1;
        var typed = false;

        function labelOfValue(value) {
            for (var i = 0; i < rows.length; i++) { if (String(rows[i].name) === value) { return choiceLabel(rows[i]); } }
            return value;
        }

        function setOpen(open) {
            list.hidden = !open;
            input.setAttribute('aria-expanded', open ? 'true' : 'false');
            if (!open) { active = -1; input.removeAttribute('aria-activedescendant'); }
        }

        function setActive(i) {
            active = i;
            Array.prototype.forEach.call(list.children, function (li, n) { li.setAttribute('aria-selected', n === i ? 'true' : 'false'); });
            if (i >= 0 && list.children[i]) {
                input.setAttribute('aria-activedescendant', list.children[i].id);
                if (list.children[i].scrollIntoView) { list.children[i].scrollIntoView({ block: 'nearest' }); }
            } else {
                input.removeAttribute('aria-activedescendant');
            }
        }

        function choose(row) {
            var name = String(row.name);
            input.value = choiceLabel(row);
            typed = false;
            setOpen(false);
            status.textContent = choiceLabel(row) + ' — ' + t('selected');
            setValue(inst, q, name);
        }

        function draw(announce) {
            var needle = fold(input.value.trim());
            matches = rows.filter(function (row) { return !needle || fold(choiceLabel(row)).indexOf(needle) >= 0; });
            list.textContent = '';
            matches.slice(0, COMBO_LIMIT).forEach(function (row, n) {
                var li = el('li', { id: id + '-o' + n, role: 'option', class: 'combo-option', 'aria-selected': 'false', text: choiceLabel(row) });
                li.addEventListener('mousedown', function (e) { e.preventDefault(); }); // keep the focus in the box
                li.addEventListener('click', function () { choose(row); input.focus(); });
                list.appendChild(li);
            });
            setOpen(matches.length > 0);
            active = -1;
            input.removeAttribute('aria-activedescendant');
            if (announce) {
                status.textContent = matches.length === 0 ? t('No choices match.')
                    : (isTurkish() ? matches.length + ' seçenek. ' : matches.length + (matches.length === 1 ? ' choice. ' : ' choices. '))
                        + (matches.length > COMBO_LIMIT ? t('Only the first 100 are listed; keep typing to narrow them.') : '');
            }
        }

        function revert() {
            typed = false;
            input.value = current(inst, q) === '' ? '' : labelOfValue(current(inst, q));
            setOpen(false);
        }

        input.addEventListener('input', function () {
            typed = true;
            if (input.value.trim() === '' && current(inst, q) !== '') { setValue(inst, q, ''); }
            draw(true);
        });
        // A filter that leaves no choices says so once, when the person reaches for the list (UX review 2026-11-16).
        var saidEmpty = false;
        function sayEmpty() {
            if (rows.length === 0 && !input.readOnly && !saidEmpty) { saidEmpty = true; status.textContent = t('Choose an answer above first.'); }
        }
        input.addEventListener('click', function () { if (list.hidden && !input.readOnly) { draw(false); } sayEmpty(); });
        input.addEventListener('focus', sayEmpty);
        input.addEventListener('keydown', function (e) {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                e.preventDefault();
                if (list.hidden) { draw(true); }
                var count = Math.min(matches.length, COMBO_LIMIT);
                if (!count) { return; }
                setActive(e.key === 'ArrowDown' ? (active + 1) % count : (active <= 0 ? count - 1 : active - 1));
            } else if (e.key === 'Enter') {
                if (!list.hidden && active >= 0 && matches[active]) { e.preventDefault(); choose(matches[active]); }
                else if (!list.hidden && matches.length === 1) { e.preventDefault(); choose(matches[0]); }
            } else if (e.key === 'Escape') {
                if (!list.hidden) { e.preventDefault(); e.stopPropagation(); setOpen(false); } else if (typed) { e.preventDefault(); revert(); }
            }
        });
        input.addEventListener('blur', function () {
            // Leaving the box with text that names a choice chooses it; anything else goes back to the answer.
            if (typed) {
                var needle = fold(input.value.trim());
                var exact = rows.filter(function (row) { return fold(choiceLabel(row)) === needle; });
                if (needle && exact.length >= 1 && String(exact[0].name) !== current(inst, q)) { choose(exact[0]); } else { revert(); }
            }
            setOpen(false);
        });

        return {
            node: el('div', { class: 'combo' }, [unavailable, input, list, status]),
            controls: function () { return [input]; },
            sync: function (ro) {
                var available = availableChoices(inst, q);
                if (!available) {
                    unavailable.hidden = false;
                    // Written only when it changes: a status region rewritten with the same words is announced again, and
                    // sync runs on every keystroke anywhere in the form (UX review 2026-11-16).
                    var unsaid = listUnavailableText(inst, q);
                    if (unavailable.textContent !== unsaid) { unavailable.textContent = unsaid; }
                    input.disabled = true;
                    return;
                }
                unavailable.hidden = true;
                if (available.length > 0 && saidEmpty) { saidEmpty = false; if (status.textContent === t('Choose an answer above first.')) { status.textContent = ''; } }
                rows = available;
                var names = rows.map(function (r) { return String(r.name); });
                // An answer the filter no longer offers is cleared, as the other selects clear theirs.
                if (current(inst, q) !== '' && names.indexOf(current(inst, q)) < 0) { inst.values[q.name] = ''; input.value = ''; typed = false; status.textContent = ''; setTimeout(function () { refresh(false); }, 0); }
                if (document.activeElement !== input && !typed) { input.value = current(inst, q) === '' ? '' : labelOfValue(current(inst, q)); }
                input.readOnly = !!ro;
                input.disabled = false;
                if (ro) { setOpen(false); }
            }
        };
    }

    // ----- Input masks. body::esri:inputMask on a text question (Survey123's documented syntax; see FormInputMask.cs).

    var maskCache = {};

    function parseMask(source) {
        if (Object.prototype.hasOwnProperty.call(maskCache, source)) { return maskCache[source]; }
        var text = String(source || ''), result = null;
        if (text.length > 0 && text.length <= 255) {
            var cut = -1;
            for (var i = text.length - 1; i >= 0; i--) {
                if (text.charAt(i) === ';') {
                    var slashes = 0;
                    for (var j = i - 1; j >= 0 && text.charAt(j) === '\\'; j--) { slashes++; }
                    if (slashes % 2 === 0) { cut = i; break; }
                }
            }
            var placeholder = ' ', body = text;
            if (cut >= 0 && cut >= text.length - 2) { body = text.slice(0, cut); placeholder = cut === text.length - 2 ? text.charAt(text.length - 1) : ' '; }
            var parts = [], letterCase = '!', ok = true, slots = 0, required = 0;
            for (var k = 0; k < body.length && ok; k++) {
                var c = body.charAt(k);
                if (c === '\\') {
                    if (k + 1 >= body.length) { ok = false; } else { parts.push({ lit: body.charAt(++k), kind: null }); }
                } else if (c === '>' || c === '<' || c === '!') {
                    letterCase = c;
                } else if ('AaNnXx90DdHhBb#'.indexOf(c) >= 0) {
                    slots++;
                    var must = 'ANX9DHB'.indexOf(c) >= 0;
                    if (must) { required = slots; }
                    parts.push({ lit: c, kind: c.toUpperCase(), required: must, letterCase: letterCase });
                } else {
                    parts.push({ lit: c, kind: null });
                }
            }
            if (ok && slots > 0) { result = { parts: parts, placeholder: placeholder, slots: slots, required: required }; }
        }
        maskCache[source] = result;
        return result;
    }

    function maskSlotAccepts(part, c) {
        switch (part.kind) {
            case 'A': return /^[A-Za-z]$/.test(c);
            case 'N': return /^[A-Za-z0-9]$/.test(c);
            case 'X': return /^\S$/.test(c);
            case '9': case '0': return /^[0-9]$/.test(c);
            case 'D': return /^[1-9]$/.test(c);
            case '#': return /^[0-9+\-]$/.test(c);
            case 'H': return /^[0-9A-Fa-f]$/.test(c);
            case 'B': return /^[01]$/.test(c);
            default: return false;
        }
    }

    // A text made to fit the mask: the characters it holds that the slots take, in order, with the separators put
    // between them as they are reached. A separator typed where the mask expects it is the same separator, not a character
    // that lands in a slot; one the person did not type is put in when the next character arrives. `trim` leaves off the
    // separators after the last slot, which is what a deletion needs, or the last one could never be deleted.
    // Returns { text, count, caretAfter }, where caretAfter[n] is where the caret goes after the n-th slot character.
    function maskFormat(mask, raw, trim) {
        var parts = mask.parts, chars = [], pi = 0;
        var input = String(raw || '');
        for (var i = 0; i < input.length; i++) {
            var c = input.charAt(i);
            var scan = pi, literal = -1;
            while (scan < parts.length && parts[scan].kind === null) { if (parts[scan].lit === c && literal < 0) { literal = scan; } scan++; }
            if (literal >= 0) { pi = literal + 1; continue; }
            pi = scan;
            if (pi < parts.length && maskSlotAccepts(parts[pi], c)) {
                var stored = c;
                if (parts[pi].letterCase === '>') { stored = c.toUpperCase(); } else if (parts[pi].letterCase === '<') { stored = c.toLowerCase(); }
                chars.push(stored);
                pi++;
            }
        }
        var out = '', pending = '', slot = 0, caretAfter = [0];
        for (var p = 0; p < parts.length; p++) {
            if (parts[p].kind === null) { pending += parts[p].lit; continue; }
            if (slot >= chars.length) { break; }
            out += pending + chars[slot]; pending = ''; slot++;
            caretAfter[slot] = out.length;
        }
        if (!trim && chars.length === mask.slots) { out += pending; }
        return { text: out, count: chars.length, caretAfter: caretAfter };
    }

    function maskAccepts(mask, value) {
        if (value === '') { return true; }
        var whole = maskFormat(mask, value, false), trimmed = maskFormat(mask, value, true);
        return (value === whole.text || value === trimmed.text) && trimmed.count >= mask.required;
    }

    // The pattern said as an answer that fits it, for the hint and the message: "12 ABC 345" reads aloud and on a phone,
    // where "__ ___ ___" read as a row of underscores (UX review 2026-11-16). Every place is filled, optional ones too.
    // The placeholder keeps the skeleton, because it is a picture of the box rather than a sentence.
    function maskExample(mask) {
        var digit = 0, letter = 0, bit = 0;
        return mask.parts.map(function (p) {
            if (p.kind === null) { return p.lit; }
            if (p.kind === 'B') { return String(1 - (bit++ % 2)); }
            if (p.kind === 'D') { return String(digit++ % 9 + 1); }
            if (p.kind === '9' || p.kind === '0' || p.kind === '#' || p.kind === 'H') { return String((digit++ + 1) % 10); }
            var c = 'ABCDEFGHJKLMNPRSTUVYZ'.charAt(letter++ % 21);
            return p.letterCase === '<' ? c.toLowerCase() : c;
        }).join('');
    }

    function maskSkeleton(mask) {
        var blank = mask.placeholder === ' ' ? '_' : mask.placeholder;
        return mask.parts.map(function (p) { return p.kind === null ? p.lit : blank; }).join('');
    }

    function maskedTextWidget(inst, q, id, describedBy) {
        var mask = parseMask(q.inputMask);
        var digits = mask.parts.every(function (p) { return p.kind === null || '90D#'.indexOf(p.kind) >= 0; });
        var format = el('p', { class: 'hint', id: id + '-format', text: t('For example: ') + maskExample(mask) });
        var input = el('input', {
            type: 'text', id: id, 'aria-describedby': describedBy + ' ' + id + '-format', 'data-focus': 'input', autocomplete: 'off',
            autocapitalize: 'off', spellcheck: 'false', inputmode: digits ? 'numeric' : null, placeholder: maskSkeleton(mask), maxlength: String(mask.parts.length + 8)
        });
        input.value = current(inst, q);
        input.addEventListener('input', function (e) {
            var deleting = !!(e && e.inputType && e.inputType.indexOf('delete') === 0);
            var caret = input.selectionStart === null ? input.value.length : input.selectionStart;
            var before = maskFormat(mask, input.value.slice(0, caret), true).count;
            var made = maskFormat(mask, input.value, deleting);
            input.value = made.text;
            var at = made.caretAfter[Math.min(before, made.count)];
            if (document.activeElement === input && input.setSelectionRange) { input.setSelectionRange(at, at); }
            setValue(inst, q, made.text);
        });
        // On leaving, the separators after the last slot are put in, so what is stored is the whole text.
        input.addEventListener('change', function () {
            var made = maskFormat(mask, input.value, false);
            if (made.text !== input.value) { input.value = made.text; setValue(inst, q, made.text); }
        });
        return {
            node: el('div', null, [input, format]),
            controls: function () { return [input]; },
            sync: function (ro) { syncInput(input, current(inst, q), ro); }
        };
    }

    // ----- spinner (integer, decimal): buttons that step the value by one, beside the box.

    function spinnerWidget(inst, q, id, describedBy) {
        var input = el('input', {
            type: 'text', id: id, 'aria-describedby': describedBy, 'data-focus': 'input',
            inputmode: q.type === 'integer' ? 'numeric' : 'decimal', autocomplete: 'off'
        });
        input.value = current(inst, q);
        var less = el('button', { type: 'button', class: 'quiet step', 'data-focus': 'less', 'aria-label': t('Decrease'), 'aria-describedby': id + '-label', text: '−' });
        var more = el('button', { type: 'button', class: 'quiet step', 'data-focus': 'more', 'aria-label': t('Increase'), 'aria-describedby': id + '-label', text: '+' });

        function step(delta) {
            var now = Number(current(inst, q));
            if (current(inst, q) === '' || isNaN(now)) { now = 0; }
            var next = Math.round((now + delta) * 1e10) / 1e10;
            if (q.type === 'integer') { next = Math.round(next); }
            input.value = String(next);
            setValue(inst, q, String(next));
        }

        input.addEventListener('input', function () { setValue(inst, q, input.value.trim().replace(',', '.')); });
        input.addEventListener('keydown', function (e) {
            if (e.key === 'ArrowUp') { e.preventDefault(); step(1); } else if (e.key === 'ArrowDown') { e.preventDefault(); step(-1); }
        });
        less.addEventListener('click', function () { step(-1); });
        more.addEventListener('click', function () { step(1); });
        return {
            node: el('div', { class: 'spin-row' }, [less, input, more]),
            controls: function () { return [input]; },
            sync: function (ro) { syncInput(input, current(inst, q), ro); less.disabled = !!ro; more.disabled = !!ro; }
        };
    }

    // ----- week-number (date): a year and a week number, stored as the date of that ISO 8601 week's Monday. INFERRED.

    function isoWeekOf(y, m, d) {
        var date = new Date(Date.UTC(y, m - 1, d));
        var dayNr = (date.getUTCDay() + 6) % 7;
        date.setUTCDate(date.getUTCDate() - dayNr + 3);
        var weekYear = date.getUTCFullYear();
        var jan4 = new Date(Date.UTC(weekYear, 0, 4));
        var week = 1 + Math.round(((date - jan4) / 86400000 - 3 + ((jan4.getUTCDay() + 6) % 7)) / 7);
        return { year: weekYear, week: week };
    }

    function weeksInYear(y) { return isoWeekOf(y, 12, 28).week; }

    function mondayOfWeek(y, w) {
        var jan4 = new Date(Date.UTC(y, 0, 4));
        var monday = new Date(jan4.getTime() - ((jan4.getUTCDay() + 6) % 7) * 86400000 + (w - 1) * 7 * 86400000);
        return monday.getUTCFullYear() + '-' + pad(monday.getUTCMonth() + 1) + '-' + pad(monday.getUTCDate());
    }

    function weekWidget(inst, q, id, describedBy) {
        var year = el('input', { type: 'text', id: id, inputmode: 'numeric', autocomplete: 'off', maxlength: '4', 'data-focus': 'year',
            'aria-labelledby': id + '-label ' + id + '-year', 'aria-describedby': describedBy });
        var week = el('input', { type: 'text', id: id + '-w', inputmode: 'numeric', autocomplete: 'off', maxlength: '2', 'data-focus': 'week',
            'aria-labelledby': id + '-label ' + id + '-weeklabel', 'aria-describedby': describedBy });
        var box = el('div', { class: 'week-row', role: 'group', 'aria-labelledby': id + '-label' }, [
            el('div', null, [el('span', { class: 'week-label', id: id + '-year', text: t('Year') }), year]),
            el('div', null, [el('span', { class: 'week-label', id: id + '-weeklabel', text: t('Week number') }), week])
        ]);

        function fromValue() {
            var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(current(inst, q));
            if (!m) { return null; }
            var info = isoWeekOf(Number(m[1]), Number(m[2]), Number(m[3]));
            return { year: String(info.year), week: String(info.week) };
        }

        function write() {
            var y = year.value.trim(), w = week.value.trim();
            var value = '';
            if (y !== '' || w !== '') {
                var yn = Number(y), wn = Number(w);
                value = /^\d{4}$/.test(y) && /^\d{1,2}$/.test(w) && wn >= 1 && wn <= weeksInYear(yn) ? mondayOfWeek(yn, wn) : (y || '') + '-W' + (w || '');
            }
            setValue(inst, q, value);
        }

        year.addEventListener('input', write);
        week.addEventListener('input', write);
        return {
            node: box,
            controls: function () { return [year, week]; },
            sync: function (ro) {
                var held = fromValue();
                var shown = /^\d{4}$/.test(year.value.trim()) && /^\d{1,2}$/.test(week.value.trim())
                    ? mondayOfWeek(Number(year.value.trim()), Number(week.value.trim())) : '';
                var inside = document.activeElement === year || document.activeElement === week;
                if (held) {
                    if (current(inst, q).slice(0, 10) !== shown && !inside) { year.value = held.year; week.value = held.week; }
                } else if (current(inst, q) === '' && shown !== '' && !inside) {
                    year.value = ''; week.value = '';
                }
                year.readOnly = !!ro; week.readOnly = !!ro;
            }
        };
    }

    function acknowledgeWidget(inst, q, id, describedBy) {
        var box = el('input', { type: 'checkbox', id: id + '-ok', 'aria-describedby': describedBy, 'data-focus': 'input' });
        box.addEventListener('change', function () { setValue(inst, q, box.checked ? t('OK') : ''); });
        var node = el('label', { class: 'choice', for: id + '-ok' }, [box, el('span', { text: t('OK') })]);
        return {
            node: node, controls: function () { return [box]; },
            sync: function (ro) { box.checked = current(inst, q) === t('OK'); box.disabled = !!ro; }
        };
    }

    function dateWidget(inst, q, id, describedBy) {
        if (has(q.appearance, 'week-number')) { return weekWidget(inst, q, id, describedBy); }
        var monthYear = has(q.appearance, 'month-year');
        var year = has(q.appearance, 'year');
        var input = el('input', {
            type: year ? 'number' : monthYear ? 'month' : 'date', id: id, 'aria-describedby': describedBy, 'data-focus': 'input',
            min: year ? '1000' : null, max: year ? '9999' : null, inputmode: year ? 'numeric' : null,
            placeholder: monthYear ? 'YYYY-MM' : null
        });
        function shown() {
            var v = current(inst, q);
            if (!v) { return ''; }
            return year ? v.slice(0, 4) : monthYear ? v.slice(0, 7) : v.slice(0, 10);
        }
        input.value = shown();
        input.addEventListener('input', function () {
            var v = input.value.trim();
            if (!v) { setValue(inst, q, ''); return; }
            setValue(inst, q, year ? (/^\d{4}$/.test(v) ? v + '-01-01' : v) : monthYear ? (/^\d{4}-\d{2}$/.test(v) ? v + '-01' : v) : v);
        });
        return { node: input, controls: function () { return [input]; }, sync: function (ro) { syncInput(input, shown(), ro); } };
    }

    function timeWidget(inst, q, id, describedBy) {
        var input = el('input', { type: 'time', id: id, 'aria-describedby': describedBy, 'data-focus': 'input' });
        function shown() { return current(inst, q).slice(0, 5); }
        input.value = shown();
        input.addEventListener('input', function () {
            setValue(inst, q, input.value ? input.value.slice(0, 5) + ':00.000' + offsetText(new Date()) : '');
        });
        return { node: input, controls: function () { return [input]; }, sync: function (ro) { syncInput(input, shown(), ro); } };
    }

    function dateTimeWidget(inst, q, id, describedBy) {
        var input = el('input', { type: 'datetime-local', id: id, 'aria-describedby': describedBy, 'data-focus': 'input' });
        function shown() {
            var v = current(inst, q);
            if (!v) { return ''; }
            var d = new Date(v);
            return isNaN(d.getTime()) ? v.slice(0, 16) : localDate(d) + 'T' + pad(d.getHours()) + ':' + pad(d.getMinutes());
        }
        input.value = shown();
        input.addEventListener('input', function () {
            if (!input.value) { setValue(inst, q, ''); return; }
            var d = new Date(input.value);
            setValue(inst, q, isNaN(d.getTime()) ? input.value : localDateTime(d));
        });
        return { node: input, controls: function () { return [input]; }, sync: function (ro) { syncInput(input, shown(), ro); } };
    }

    // ----- Location. No map in this version: the device's position, and coordinates typed by hand.

    function locate() {
        return new Promise(function (resolve, reject) {
            if (!navigator.geolocation) { reject(new Error('This browser cannot tell its location.')); return; }
            navigator.geolocation.getCurrentPosition(resolve, function (e) {
                reject(new Error(e.code === 1 ? t('Location permission was refused.') : t('The location could not be found.')));
            }, { enableHighAccuracy: true, timeout: 30000, maximumAge: 0 });
        });
    }

    function geopointWidget(inst, q, id) {
        var status = el('p', { class: 'hint', id: id + '-status', 'aria-live': 'polite' });
        var use = el('button', { type: 'button', 'data-focus': 'locate', text: t('Use my location') });
        var lat = el('input', { type: 'text', inputmode: 'decimal', id: id + '-lat', 'data-focus': 'lat', autocomplete: 'off' });
        var lon = el('input', { type: 'text', inputmode: 'decimal', id: id + '-lon', 'data-focus': 'lon', autocomplete: 'off' });
        var clear = el('button', { type: 'button', class: 'quiet', 'data-focus': 'clear', text: t('Clear') });
        var labelId = id + '-label';
        lat.setAttribute('aria-labelledby', labelId + ' ' + id + '-latl');
        lon.setAttribute('aria-labelledby', labelId + ' ' + id + '-lonl');

        function parts() { return current(inst, q).split(/\s+/).filter(Boolean); }

        function typed() {
            var a = lat.value.trim().replace(',', '.'), b = lon.value.trim().replace(',', '.');
            if (!a && !b) { setValue(inst, q, ''); return; }
            setValue(inst, q, a + ' ' + b + ' 0 0');
        }
        lat.addEventListener('input', typed);
        lon.addEventListener('input', typed);
        clear.addEventListener('click', function () { setValue(inst, q, ''); status.textContent = ''; });
        use.addEventListener('click', function () {
            status.textContent = t('Finding your location…');
            use.disabled = true;
            locate().then(function (p) {
                var c = p.coords;
                setValue(inst, q, c.latitude.toFixed(7) + ' ' + c.longitude.toFixed(7) + ' ' + (c.altitude || 0) + ' ' + (c.accuracy || 0).toFixed(1));
                map.centre();
                status.textContent = isTurkish() ? 'Konum bulundu, ' + Math.round(c.accuracy) + ' m doğrulukta.' : 'Location found, accurate to ' + Math.round(c.accuracy) + ' m.';
            }, function (e) { status.textContent = e.message + ' You can type the coordinates instead.'; })
                .then(function () { use.disabled = false; });
        });

        var map = placeMap('geopoint', function () {
            // Unfiltered: with one box still empty the value is "41.01  0 0", and parts() would read its altitude as the
            // longitude and draw the point at 0 degrees east.
            var p = current(inst, q).split(' ');
            return p.length >= 2 && p[0] !== '' && p[1] !== '' && !isNaN(Number(p[0])) && !isNaN(Number(p[1])) ? [[Number(p[0]), Number(p[1])]] : [];
        }, function (la, lo) {
            setValue(inst, q, la + ' ' + lo + ' 0 0');
            status.textContent = isTurkish() ? 'Nokta haritadan yerleştirildi.' : 'Point placed on the map.';
        }, function (note) { status.textContent = note; });

        var node = el('div', null, [
            map.node,
            el('div', { class: 'inline' }, [use, clear]),
            el('div', { class: 'pair' }, [
                el('label', { for: id + '-lat' }, [el('span', { id: id + '-latl', text: t('Latitude') }), lat]),
                el('label', { for: id + '-lon' }, [el('span', { id: id + '-lonl', text: t('Longitude') }), lon])
            ]),
            status
        ]);
        return {
            node: node,
            controls: function () { return [lat, lon]; },
            sync: function (ro) {
                var p = parts();
                syncInput(lat, p[0] || '', ro);
                syncInput(lon, p[1] || '', ro);
                use.disabled = !!ro; clear.disabled = !!ro || !p.length;
                map.update(ro);
                if (p.length >= 4 && Number(p[3]) > 0 && !status.textContent) { status.textContent = isTurkish() ? Math.round(Number(p[3])) + ' m doğrulukta.' : 'Accurate to ' + Math.round(Number(p[3])) + ' m.'; }
            }
        };
    }

    // ----- The map a location question is answered on (§5.15).

    var mapLibrary = null;

    /** The closest a map must be before a tap places a point: about a street's width per finger. */
    var PLACE_ZOOM = 15;

    /** OpenLayers, loaded once and only when a form asks for a place: most forms never pay for it. */
    function loadMapLibrary() {
        if (window.ol) { return Promise.resolve(true); }
        if (!mapLibrary) {
            mapLibrary = new Promise(function (done) {
                document.head.appendChild(el('link', { rel: 'stylesheet', href: 'ol.css' }));
                var tag = document.createElement('script');
                tag.src = 'ol.js';
                tag.onload = function () { done(true); };
                tag.onerror = function () { done(false); };
                document.head.appendChild(tag);
            });
        }
        return mapLibrary;
    }

    var liveMaps = [];

    /**
     * The ground under every location map is the publisher's choice (§5.15, reversed by the owner 2026-11-09; settled
     * 2026-11-15): <c>osm</c>, the default, <c>topo</c> or <c>none</c>, a plain ground for a place with no street
     * map worth drawing. The server refuses any other value when the form is saved, so an unknown one here is the
     * default rather than a blank map. INFERRED: three grounds and no address of the publisher's own, because the page's
     * policy admits tiles from named origins only and a form must not widen it.
     */
    function groundKind() {
        var kind = form && form.basemap;
        return kind === 'topo' || kind === 'none' ? kind : 'osm';
    }

    function groundLayers() {
        var kind = groundKind();
        if (kind === 'none') { return []; }
        if (kind === 'topo') {
            return [new ol.layer.Tile({ source: new ol.source.XYZ({
                url: 'https://tile.opentopomap.org/{z}/{x}/{y}.png', maxZoom: 17,
                attributions: '© OpenStreetMap contributors, SRTM | © OpenTopoMap (CC-BY-SA)'
            }) })];
        }
        return [new ol.layer.Tile({ source: new ol.source.OSM() })];
    }

    /**
     * A map that shows a question's points and takes a tap as a new one. It is the pointer's way to answer; the boxes
     * beside it stay the keyboard's and the screen reader's, so the map itself is hidden from assistive technology.
     * Offline the ground is missing and the points are still drawn and still placed.
     */
    function placeMap(kind, readPoints, onTap, onNote) {
        // A region with its own name, not hidden: its zoom buttons are reachable by keyboard and must be heard (the UX
        // review of 2026-11-09 found them focusable inside an aria-hidden box). Placing stays the pointer's; the boxes are
        // the keyboard's way to answer.
        var box = el('div', { class: 'qmap', role: 'region', 'aria-label': kind === 'geopoint'
            ? t('Map of the point. Use the boxes below to type it.') : t('Map of the points. Use the boxes below to type them.') });
        // graticula:basemap none: a point tapped on a blank ground is a place nobody could see, so the map only shows
        // what was typed or found, and the hint says how to answer (decision 2026-11-15).
        var plain = groundKind() === 'none';
        var hint = el('p', { class: 'hint', text: plain
            ? (kind === 'geopoint' ? t('There is no map to tap here. Type the latitude and longitude below, or use your location.')
                : t('There is no map to tap here. Type each point below, or add your position.'))
            : kind === 'geopoint'
            ? t('Zoom in close and tap the map to place the point; tap again to move it. Two fingers move the map.')
            : t('Zoom in close and tap the map to add each point in turn. Two fingers move the map.') });
        var map = null, source = null, fitted = false, readOnly = false;

        function draw() {
            if (!source) { return; }
            var pts = readPoints().map(function (p) { return ol.proj.fromLonLat([p[1], p[0]]); });
            source.clear();
            pts.forEach(function (c) { source.addFeature(new ol.Feature(new ol.geom.Point(c))); });
            if (kind === 'geotrace' && pts.length > 1) { source.addFeature(new ol.Feature(new ol.geom.LineString(pts))); }
            if (kind === 'geoshape' && pts.length > 2) { source.addFeature(new ol.Feature(new ol.geom.Polygon([pts.concat([pts[0]])]))); }
            else if (kind === 'geoshape' && pts.length === 2) { source.addFeature(new ol.Feature(new ol.geom.LineString(pts))); }
            if (!pts.length) { fitted = false; return; }
            if (!fitted) {
                fitted = true;
                map.getView().fit(source.getExtent(), { padding: [40, 40, 40, 40], maxZoom: 17 });
            } else if (pts.length === 1 && map.getSize()) {
                // A point typed into the boxes moves as it is typed; the map follows it rather than keep the view of the
                // first half-typed value, where the real point is off the edge (UX review 2026-11-15).
                if (!ol.extent.containsCoordinate(map.getView().calculateExtent(map.getSize()), pts[0])) { map.getView().setCenter(pts[0]); }
            }
        }

        function start(tries) {
            if (!document.body.contains(box) || !box.clientWidth) {
                if (tries < 40) { setTimeout(function () { start(tries + 1); }, 50); }
                return;
            }
            loadMapLibrary().then(function (ok) {
                if (!ok || map) { if (!ok) { hint.textContent = t('The map could not be loaded; use the boxes below.'); } return; }
                source = new ol.source.Vector();
                var tokens = getComputedStyle(document.documentElement);
                var accent = tokens.getPropertyValue('--accent').trim() || '#1565d8';
                var ring = tokens.getPropertyValue('--surface').trim() || '#fff';
                var tint = /^#[0-9a-f]{6}$/i.test(accent) ? accent + '33' : accent;
                map = new ol.Map({
                    target: box,
                    layers: groundLayers().concat([new ol.layer.Vector({
                        source: source,
                        style: new ol.style.Style({
                            image: new ol.style.Circle({ radius: 7, fill: new ol.style.Fill({ color: accent }), stroke: new ol.style.Stroke({ color: ring, width: 2 }) }),
                            stroke: new ol.style.Stroke({ color: accent, width: 3 }),
                            fill: new ol.style.Fill({ color: tint })
                        })
                    })]),
                    view: link.center
                        ? new ol.View({ center: ol.proj.fromLonLat([link.center[1], link.center[0]]), zoom: PLACE_ZOOM + 1 })
                        : new ol.View({ center: ol.proj.fromLonLat([35, 39]), zoom: 5 }),
                    interactions: interactions(),
                    controls: [new ol.control.Zoom({ zoomInTipLabel: t('Zoom in'), zoomOutTipLabel: t('Zoom out') }),
                        new ol.control.Attribution({ collapsible: false })]
                });
                [['.ol-zoom-in', t('Zoom in')], ['.ol-zoom-out', t('Zoom out')]].forEach(function (b) {
                    var button = box.querySelector(b[0]);
                    if (button) { button.setAttribute('aria-label', b[1]); }
                });
                // One finger scrolls the form and a tap places a point; two fingers move the map.
                map.getViewport().style.touchAction = 'pan-x pan-y';
                map.on('singleclick', function (event) {
                    if (readOnly || plain) { return; }
                    // A tap at country scale is tens of kilometres wide: it zooms in on the place instead of answering,
                    // so one careless touch cannot answer a required question (the UX review of 2026-11-09).
                    if (map.getView().getZoom() < PLACE_ZOOM) {
                        map.getView().animate({ center: event.coordinate, zoom: PLACE_ZOOM + 1, duration: 250 });
                        if (onNote) { onNote(t('Zoomed in. Tap again to place it.')); }
                        return;
                    }
                    var ll = ol.proj.toLonLat(event.coordinate);
                    onTap(Number(ll[1].toFixed(7)), Number(ll[0].toFixed(7)));
                });
                draw();
                box.setAttribute('data-ground', groundKind());
                liveMaps.push({ box: box, map: map });
                // With the device's position already allowed, the map opens on it, as Survey123's field app does; it never
                // asks for it on its own — that question belongs to a button the person pressed. A link's center= is a
                // place somebody chose, and wins.
                if (!readPoints().length && !link.center && navigator.permissions && navigator.permissions.query) {
                    navigator.permissions.query({ name: 'geolocation' }).then(function (p) {
                        if (p.state !== 'granted') { return; }
                        return locate().then(function (pos) {
                            if (!readPoints().length) {
                                map.getView().setCenter(ol.proj.fromLonLat([pos.coords.longitude, pos.coords.latitude]));
                                map.getView().setZoom(PLACE_ZOOM + 1);
                            }
                        });
                    }).catch(function () { /* no position: the map stays where it opened */ });
                }
            });
        }

        function interactions() {
            var list = ol.interaction.defaults.defaults({ mouseWheelZoom: false, dragPan: false });
            list.push(new ol.interaction.MouseWheelZoom({ condition: ol.events.condition.platformModifierKeyOnly }));
            list.push(new ol.interaction.DragPan({
                condition: function (event) { return event.originalEvent.pointerType !== 'touch' || this.getPointerCount() === 2; }
            }));
            return list;
        }

        setTimeout(function () { start(0); }, 0);
        return {
            node: el('div', { class: 'qmapwrap' }, [box, hint]),
            update: function (ro) { readOnly = !!ro; draw(); },
            centre: function () { fitted = false; draw(); }
        };
    }

    function vertices(v) {
        return String(v || '').split(';').map(function (s) { return s.trim(); }).filter(Boolean)
            .map(function (s) { return s.split(/\s+/).map(Number); })
            .filter(function (p) { return p.length >= 2 && !isNaN(p[0]) && !isNaN(p[1]); });
    }

    function verticesWidget(inst, q, id) {
        var list = el('ol', { class: 'vertices', 'aria-label': t('Points') });
        var status = el('p', { class: 'hint', 'aria-live': 'polite' });
        var here = el('button', { type: 'button', 'data-focus': 'here', text: t('Add my position') });
        var undo = el('button', { type: 'button', class: 'quiet', 'data-focus': 'undo', text: t('Remove last point') });
        var lat = el('input', { type: 'text', inputmode: 'decimal', id: id + '-lat', 'data-focus': 'lat', autocomplete: 'off' });
        var lon = el('input', { type: 'text', inputmode: 'decimal', id: id + '-lon', 'data-focus': 'lon', autocomplete: 'off' });
        var addTyped = el('button', { type: 'button', class: 'quiet', 'data-focus': 'add-typed', text: t('Add typed point') });

        function points() { return vertices(current(inst, q)); }
        function store(pts) { setValue(inst, q, pts.map(function (p) { return p[0] + ' ' + p[1] + ' ' + (p[2] || 0) + ' ' + (p[3] || 0); }).join(';')); }

        here.addEventListener('click', function () {
            status.textContent = t('Finding your location…');
            locate().then(function (p) {
                var pts = points();
                pts.push([Number(p.coords.latitude.toFixed(7)), Number(p.coords.longitude.toFixed(7)), p.coords.altitude || 0, Number((p.coords.accuracy || 0).toFixed(1))]);
                store(pts);
                status.textContent = isTurkish() ? 'Nokta ' + pts.length + ' eklendi, ' + Math.round(p.coords.accuracy) + ' m doğrulukta.'
                    : 'Point ' + pts.length + ' added, accurate to ' + Math.round(p.coords.accuracy) + ' m.';
            }, function () { status.textContent = t('Your location could not be found. You can type the coordinates instead.'); });
        });
        addTyped.addEventListener('click', function () {
            var a = Number(lat.value.trim().replace(',', '.')), b = Number(lon.value.trim().replace(',', '.'));
            if (!lat.value.trim() || !lon.value.trim() || isNaN(a) || isNaN(b) || Math.abs(a) > 90 || Math.abs(b) > 180) {
                status.textContent = t('Type a latitude between -90 and 90 and a longitude between -180 and 180.');
                return;
            }
            var pts = points(); pts.push([a, b, 0, 0]); store(pts);
            lat.value = ''; lon.value = '';
            status.textContent = isTurkish() ? 'Nokta ' + pts.length + ' eklendi.' : 'Point ' + pts.length + ' added.';
            lat.focus();
        });
        undo.addEventListener('click', function () {
            var pts = points(); pts.pop(); store(pts);
            status.textContent = isTurkish() ? pts.length + ' nokta.' : pts.length + (pts.length === 1 ? ' point.' : ' points.');
        });
        var labelId = id + '-label';
        lat.setAttribute('aria-labelledby', labelId + ' ' + id + '-latl');
        lon.setAttribute('aria-labelledby', labelId + ' ' + id + '-lonl');
        var closed = el('p', { class: 'hint', id: id + '-closed' });

        var map = placeMap(q.type, points, function (la, lo) {
            var pts = points(); pts.push([la, lo, 0, 0]); store(pts);
            status.textContent = (isTurkish() ? 'Nokta ' : 'Point ') + pts.length + (isTurkish() ? ' haritadan eklendi.' : ' added on the map.');
        }, function (note) { status.textContent = note; });

        var node = el('div', null, [
            map.node,
            el('div', { class: 'inline' }, [here, undo]),
            el('div', { class: 'pair' }, [
                el('label', { for: id + '-lat' }, [el('span', { id: id + '-latl', text: t('Latitude') }), lat]),
                el('label', { for: id + '-lon' }, [el('span', { id: id + '-lonl', text: t('Longitude') }), lon])
            ]),
            el('div', { class: 'inline' }, [addTyped]),
            list, closed, status
        ]);
        return {
            node: node,
            controls: function () { return [lat, lon]; },
            sync: function (ro) {
                var pts = points();
                list.textContent = '';
                // Each point by its number, with its own Remove: Remove last point was the only way to correct one.
                pts.forEach(function (p, i) {
                    var remove = el('button', { type: 'button', class: 'quiet', 'data-focus': 'remove-' + i,
                        text: t('Remove'), 'aria-label': (isTurkish() ? 'Nokta ' : 'Remove point ') + (i + 1) + (isTurkish() ? ' kaldır' : '') });
                    remove.disabled = !!ro;
                    remove.addEventListener('click', function () {
                        var now = points(); now.splice(i, 1); store(now);
                        // Focus to the point that took its place, or to Remove last point when none is left.
                        setTimeout(function () {
                            var next = list.querySelectorAll('button')[Math.min(i, now.length - 1)];
                            (next || undo).focus();
                        }, 0);
                        status.textContent = isTurkish() ? 'Nokta ' + (i + 1) + ' kaldırıldı.' : 'Point ' + (i + 1) + ' removed.';
                    });
                    list.appendChild(el('li', null, [el('span', { text: (isTurkish() ? 'Nokta ' : 'Point ') + (i + 1) + ': '
                        + Number(p[0]).toFixed(5) + ', ' + Number(p[1]).toFixed(5) }), remove]));
                });
                var need = q.type === 'geotrace' ? 2 : 3;
                closed.textContent = !pts.length ? '' : pts.length < need
                    ? (isTurkish() ? 'En az ' + need + ' nokta gerekir.' : 'At least ' + need + ' points are needed.')
                    : q.type === 'geoshape' ? (isTurkish() ? pts.length + ' nokta; alan kapalı.' : pts.length + ' points; the area is closed.')
                        : (isTurkish() ? pts.length + ' noktalı çizgi.' : 'A line of ' + pts.length + ' points.');
                here.disabled = undo.disabled = addTyped.disabled = !!ro;
                undo.disabled = undo.disabled || !pts.length;
                lat.readOnly = lon.readOnly = !!ro;
                map.update(ro);
            }
        };
    }

    // ----- Files.

    function setFiles(inst, q, list) {
        inst.files[q.name] = list;
        // A record from the Inbox keeps the names of the files the server already holds in the answer, beside the new ones.
        inst.values[q.name] = existingFilesOf(inst, q.name).concat(list).map(function (f) { return f.name; }).join(' ');
        inst.touched[q.name] = true;
        refresh(false);
        scheduleDraft();
    }

    async function shrink(file, maxPixels) {
        if (!maxPixels || !window.createImageBitmap || !/^image\//.test(file.type)) { return file; }
        try {
            // max-pixels is the long edge in pixels, as Survey123's media page and ODK's question-types page both say
            // (ADR-287 section 5.24). Until 2026-11-16 this read it as the total number of pixels, so max-pixels=1280 made
            // a photo of about 36 by 36.
            var bitmap = await createImageBitmap(file);
            var edge = Math.max(bitmap.width, bitmap.height);
            if (edge <= maxPixels) { if (bitmap.close) { bitmap.close(); } return file; }
            var scale = maxPixels / edge;
            var canvas = document.createElement('canvas');
            canvas.width = Math.max(1, Math.round(bitmap.width * scale));
            canvas.height = Math.max(1, Math.round(bitmap.height * scale));
            canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
            var blob = await new Promise(function (r) { canvas.toBlob(r, 'image/jpeg', 0.9); });
            if (!blob) { return file; }
            return new File([blob], file.name.replace(/\.[^.]+$/, '') + '.jpg', { type: 'image/jpeg' });
        } catch (e) {
            return file;
        }
    }

    function mediaWidget(inst, q, id, describedBy) {
        var accept = q.accept || (q.type === 'image' ? 'image/*' : q.type === 'audio' ? 'audio/*' : q.type === 'video' ? 'video/*' : null);
        var multiple = has(q.appearance, 'multiline');
        var input = el('input', {
            type: 'file', id: id, accept: accept, multiple: multiple ? true : null, 'aria-describedby': describedBy,
            'data-focus': 'input', 'aria-labelledby': id + '-label',
            capture: captureFor(q)
        });
        var list = el('ul', { class: 'files' });
        var urls = [];
        input.addEventListener('change', async function () {
            var chosen = Array.prototype.slice.call(input.files || []);
            var maxPixels = Number((q.parameters || {})['max-pixels']) || 0;
            var prepared = [];
            for (var i = 0; i < chosen.length; i++) {
                var f = q.type === 'image' ? await shrink(chosen[i], maxPixels) : chosen[i];
                prepared.push({ name: f.name || (q.name + '-' + (i + 1)), type: f.type || 'application/octet-stream', size: f.size, blob: f });
            }
            setFiles(inst, q, multiple ? filesOf(inst, q.name).concat(prepared) : prepared);
            input.value = '';
        });
        function draw() {
            urls.forEach(function (u) { URL.revokeObjectURL(u); });
            urls = [];
            list.textContent = '';
            filesOf(inst, q.name).forEach(function (f, i) {
                var url = URL.createObjectURL(f.blob);
                urls.push(url);
                var preview = /^image\//.test(f.type) ? el('img', { src: url, alt: '' })
                    : /^audio\//.test(f.type) ? el('audio', { src: url, controls: true, 'aria-label': t('Recording: ') + f.name })
                    : /^video\//.test(f.type) ? el('video', { src: url, controls: true, muted: true, 'aria-label': f.name }) : null;
                var remove = el('button', { type: 'button', class: 'quiet', 'data-focus': 'remove-' + i, text: t('Remove') });
                remove.setAttribute('aria-label', t('Remove: ') + f.name);
                remove.addEventListener('click', function () {
                    var now = filesOf(inst, q.name).slice(); now.splice(i, 1); setFiles(inst, q, now); input.focus();
                });
                list.appendChild(el('li', null, [preview, el('span', { text: f.name + ' (' + Math.ceil(f.size / 1024) + ' KB)' }), remove]));
            });
        }
        var drawnFor = null;
        return {
            node: el('div', null, [input, q.type === 'audio' ? audioRecorderNode(inst, q, id) : null, list]),
            controls: function () { return [input]; },
            sync: function (ro) {
                var now = filesOf(inst, q.name);
                if (drawnFor !== now) { drawnFor = now; draw(); }
                input.disabled = !!ro;
            }
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ADR-287 section 5.26: recording an audio answer, recordings in labels, the distress scale, a warning that lets the
    // answer go, the camera a photo is taken with, and a grid of a group's questions. Kept together so a merge of
    // fill.js meets them in one place.
    // ---------------------------------------------------------------------------------------------------------------

    // new-front takes a photo with the front camera, as Survey123's field app does; new, new-rear and a plain image the
    // rear one. `capture` on a phone opens the camera itself; a desktop browser ignores it and offers a file.
    function captureFor(q) {
        if (q.type === 'image') { return has(q.appearance, 'new-front') ? 'user' : 'environment'; }
        if (q.type === 'video') { return 'environment'; }
        if (q.type === 'audio') { return 'user'; }
        return null;
    }

    // ----- Recording an audio answer. Survey123's web app only chooses a file; this one also records where the browser
    // can (MediaRecorder and a microphone, which need an https address), up to max-duration seconds (600 when the form
    // does not say: Survey123's web-app default). A recording is a file like a chosen one: it waits in the outbox with
    // the record when there is no signal, and is sent as an attachment.
    var recording = null;    // the one recording running: { key, recorder, stream, chunks, started, timer, tick, view, stop }
    var recordingSaid = {};  // what the last recording said, by question key, so a redraw still says it

    function canRecord() {
        return !!(window.MediaRecorder && navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
    }

    function clockOf(seconds) {
        var s = Math.max(0, Math.floor(seconds));
        return Math.floor(s / 60) + ':' + (s % 60 < 10 ? '0' : '') + (s % 60);
    }

    function recordingExtension(type) {
        if (/ogg/.test(type)) { return '.ogg'; }
        if (/mp4|aac|m4a/.test(type)) { return '.m4a'; }
        if (/mpeg/.test(type)) { return '.mp3'; }
        if (/wav/.test(type)) { return '.wav'; }
        return '.webm';
    }

    function stopRecording(how) {
        if (recording) { var r = recording; recording = null; r.stop(how); }
    }

    window.addEventListener('pagehide', function () { stopRecording('left'); });
    document.addEventListener('visibilitychange', function () { if (document.hidden) { stopRecording('left'); } });

    function audioRecorderNode(inst, q, id) {
        var key = keyOf(inst, q.name);
        var limit = Number(q.maxDuration) > 0 ? Number(q.maxDuration) : 600;
        var status = el('p', { class: 'hint', id: id + '-recording', 'aria-live': 'polite' });
        var clock = el('p', { class: 'hint recording-clock', 'aria-hidden': 'true' });
        if (!canRecord()) {
            var insecure = window.isSecureContext === false;
            return el('div', { class: 'recorder' }, [el('p', { class: 'hint', text: insecure
                ? t('Recording needs the form to be opened at an https address. Choose a recording instead.')
                : t('This browser cannot record. Choose a recording instead.') })]);
        }
        var button = el('button', { type: 'button', class: 'quiet', 'data-focus': 'record', 'aria-describedby': id + '-label ' + id + '-limit' });
        var limitNote = el('p', { class: 'hint', id: id + '-limit', text: t('Longest recording: ') + clockOf(limit) });
        function draw() {
            var on = !!(recording && recording.key === key);
            button.textContent = on ? t('Stop recording') : t('Record');
            button.setAttribute('aria-pressed', on ? 'true' : 'false');
            clock.hidden = !on;
            if (on) { clock.textContent = clockOf((Date.now() - recording.started) / 1000) + ' / ' + clockOf(limit); }
            if (!on && !saying && recordingSaid[key] && status.textContent !== recordingSaid[key]) { status.textContent = recordingSaid[key]; }
        }
        // Emptied first so the same words are announced again; written once. The redraw that follows a saved recording
        // wrote the words back at once, and the timer a second time, and both were announced (UX review 2026-11-17).
        var saying = 0;
        function say(words) {
            recordingSaid[key] = words;
            status.textContent = '';
            clearTimeout(saying);
            saying = setTimeout(function () { saying = 0; if (status.textContent !== words) { status.textContent = words; } }, 50);
        }
        button.addEventListener('click', function () {
            if (recording && recording.key === key) { stopRecording('done'); return; }
            stopRecording('other');
            stopBarcodeScan('other');
            say(t('Opening the microphone…'));
            navigator.mediaDevices.getUserMedia({ audio: true, video: false }).then(function (stream) {
                var recorder;
                try { recorder = new MediaRecorder(stream); } catch (e) {
                    stream.getTracks().forEach(function (tr) { tr.stop(); });
                    say(t('This browser cannot record. Choose a recording instead.'));
                    return;
                }
                var chunks = [];
                var session = { key: key, recorder: recorder, stream: stream, started: Date.now(), view: draw, say: say };
                var ended = 'done';
                recorder.ondataavailable = function (e) { if (e.data && e.data.size) { chunks.push(e.data); } };
                recorder.onstop = function () {
                    stream.getTracks().forEach(function (tr) { tr.stop(); });
                    var type = (recorder.mimeType || (chunks[0] && chunks[0].type) || 'audio/webm').split(';')[0];
                    if (chunks.length) {
                        var blob = new Blob(chunks, { type: type });
                        var stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\..*$/, '');
                        var file = { name: q.name + '-' + stamp + recordingExtension(type), type: type, size: blob.size, blob: blob };
                        var multiple = has(q.appearance, 'multiline');
                        setFiles(inst, q, multiple ? filesOf(inst, q.name).concat([file]) : [file]);
                        session.say(ended === 'limit' ? t('Recording stopped at the time limit and saved.')
                            : ended === 'left' ? t('Recording stopped because the page was left, and saved.') : t('Recording saved.'));
                    } else {
                        session.say(t('Nothing was recorded.'));
                    }
                    var now = document.querySelector('[data-key="' + cssEscape(key) + '"] [data-focus=record]');
                    if (ended === 'done' && now) { now.focus(); }
                    if (session.view) { session.view(); }
                };
                session.stop = function (how) {
                    ended = how;
                    clearTimeout(session.timer); clearInterval(session.tick);
                    try { if (recorder.state !== 'inactive') { recorder.stop(); } else { recorder.onstop(); } } catch (e) { recorder.onstop(); }
                };
                session.timer = setTimeout(function () { if (recording === session) { stopRecording('limit'); } }, limit * 1000);
                session.tick = setInterval(function () { if (session.view) { session.view(); } }, 1000);
                recording = session;
                recorder.start();
                say(t('Recording. Press Stop recording to finish.'));
                draw();
            }, function (err) {
                var name = err && err.name;
                say(name === 'NotAllowedError' || name === 'SecurityError' ? t('Microphone permission was refused. Choose a recording instead.')
                    : name === 'NotFoundError' ? t('No microphone was found. Choose a recording instead.')
                        : t('The microphone could not be started. Choose a recording instead.'));
            });
        });
        // A redraw makes a new widget; the recording running is drawn by whichever widget is on the page now.
        if (recording && recording.key === key) { recording.view = draw; recording.say = say; }
        draw();
        return el('div', { class: 'recorder' }, [el('div', { class: 'inline' }, [button]), clock, limitNote, status]);
    }

    // ----- Recordings in labels and choices (media::audio, and its older spelling audio). Found and kept on the device
    // with the form, as pictures are, so an offline reload still plays them.
    function soundName(map) { return text(map || {}, ''); }

    function choiceSound(row) {
        if (lang && row['audio::' + lang]) { return String(row['audio::' + lang]); }
        if (row.audio) { return String(row.audio); }
        var keys = Object.keys(row);
        for (var i = 0; i < keys.length; i++) { if (keys[i].indexOf('audio::') === 0 && row[keys[i]]) { return String(row[keys[i]]); } }
        return '';
    }

    function soundNode(name, label, cls) {
        var holder = el('div', { class: 'sound' + (cls ? ' ' + cls : '') });
        loadMedia(name).then(function (blob) {
            if (!blob) {
                holder.appendChild(el('span', { class: 'picture-missing', text: t('Recording not available: ') + name }));
                return;
            }
            if (!mediaUrls[name]) { mediaUrls[name] = URL.createObjectURL(blob); }
            holder.appendChild(el('audio', { src: mediaUrls[name], controls: true, preload: 'none', 'aria-label': t('Recording: ') + label }));
        });
        return holder;
    }

    // ----- distress (Survey123's integer appearance): "a 10-point scale used to measure feelings such as stress, pain,
    // and happiness". INFERRED: the points are 0 to 10, the convention of such scales, drawn from green to red; it is a
    // set of radio buttons, so it is answered with the arrow keys as any choice is, and Clear takes the answer away.
    function distressWidget(inst, q, id) {
        var box = el('div', { class: 'choices distress', role: 'radiogroup', 'aria-labelledby': id + '-label', 'aria-describedby': id + '-scale' });
        var radios = [];
        for (var v = 0; v <= 10; v++) {
            var value = String(v);
            var radio = el('input', { type: 'radio', name: id, value: value, id: id + '-' + v, 'data-focus': 'opt-' + value });
            radio.addEventListener('change', (function (val) { return function () { setValue(inst, q, val); }; })(value));
            radios.push(radio);
            box.appendChild(el('label', { class: 'choice distress-' + v, for: id + '-' + v }, [radio, el('span', { text: value })]));
        }
        var scale = el('p', { class: 'hint', id: id + '-scale', text: t('0 is none at all and 10 is the most.') });
        var clear = el('button', { type: 'button', class: 'quiet', 'data-focus': 'clear', text: t('Clear') });
        clear.addEventListener('click', function () { setValue(inst, q, ''); if (radios[0]) { radios[0].focus(); } });
        return {
            node: el('div', null, [scale, box, el('div', { class: 'inline' }, [clear])]),
            controls: function () { return radios; },
            sync: function (ro) {
                var cur = current(inst, q);
                radios.forEach(function (rd) { rd.checked = rd.value === cur; rd.disabled = !!ro; rd.parentNode.classList.toggle('on', rd.checked); });
                clear.disabled = !!ro || cur === '';
            }
        };
    }

    // ----- bind::esri:warning: Survey123's soft constraint. When its expression is false for an answer the warning is
    // shown beside the question, and the record can still be sent; an error, when there is one, is said instead.
    function warningFor(inst, q) {
        if (!q.warning || inst.relevant === false || inst.rel[q.name] === false || isEmpty(inst, q)) { return ''; }
        var v = String(inst.values[q.name] === undefined ? '' : inst.values[q.name]);
        return evalBool(q.warning, inst, q, true, v) ? '' : (text(q.warningMessages || {}) || t('Check this answer.'));
    }

    // ----- A grid (the settings' style fixed-grid or dynamic-grid, Survey123's groups-and-pages page): a group whose
    // appearance has a width w2…w9 divides into that many columns, a question in it spans its own width (one when it has
    // none, INFERRED) and a group or a repeat inside takes the whole row. Under fixed-grid a hidden question keeps its place.
    function widthOf(appearance) {
        var found = 0;
        (appearance || []).forEach(function (a) { var m = /^w(\d+)$/.exec(a); if (m) { found = Number(m[1]); } });
        return found;
    }

    function gridGroup(target, g) {
        var columns = form.grid ? widthOf(g.appearance) : 0;
        if (columns < 2 || has(g.appearance, 'table-list')) { return; }
        target.classList.add('grid-group');
        if (form.grid === 'fixed') { target.classList.add('grid-fixed'); }
        target.setAttribute('data-grid-columns', String(columns));
        target.style.gridTemplateColumns = 'repeat(' + columns + ', minmax(0, 1fr))';
    }

    function gridPlace(container, n) {
        var columns = Number(container.getAttribute && container.getAttribute('data-grid-columns')) || 0;
        var node = container.lastElementChild;
        if (!columns || !node) { return; }
        node.style.gridColumn = n.kind === 'question' ? 'span ' + Math.min(columns, Math.max(1, widthOf(n.appearance) || 1)) : '1 / -1';
    }

    // The signature pad, and the `draw` appearance (ADR-287 section 5.20), which is the same pad taller: a blank canvas drawn
    // on with a pointer and kept as a PNG attachment. A canvas cannot be drawn on from the keyboard, so the drawing says so,
    // in words, before anyone reaches it, and the canvas is described by that sentence.
    function signatureWidget(inst, q, id, freehand) {
        var canvas = el('canvas', { tabindex: '0', role: 'img', 'aria-labelledby': id + '-label', 'data-focus': 'pad' });
        var clear = el('button', { type: 'button', class: 'quiet', 'data-focus': 'clear', text: t(freehand ? 'Clear drawing' : 'Clear signature') });
        var status = el('p', { class: 'hint', 'aria-live': 'polite' });
        var needsPointer = null;
        if (freehand) {
            needsPointer = el('p', { class: 'hint', id: id + '-pointer', text: t('Drawing needs a mouse, a pen or a finger. It cannot be done with the keyboard alone.') });
            canvas.setAttribute('aria-describedby', id + '-pointer');
        }
        var drawing = false, dirty = false, last = null, ctx = null;

        function setup() {
            var ratio = window.devicePixelRatio || 1;
            var w = canvas.clientWidth || 320, h = canvas.clientHeight || (freehand ? 280 : 180);
            canvas.width = Math.round(w * ratio); canvas.height = Math.round(h * ratio);
            ctx = canvas.getContext('2d');
            ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
            ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, w, h);
            ctx.lineWidth = 2.2; ctx.lineCap = 'round'; ctx.lineJoin = 'round'; ctx.strokeStyle = '#101826';
            var held = filesOf(inst, q.name)[0];
            if (held && window.createImageBitmap) {
                createImageBitmap(held.blob).then(function (b) { ctx.drawImage(b, 0, 0, w, h); });
            }
        }
        function at(e) { var r = canvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; }
        canvas.addEventListener('pointerdown', function (e) {
            if (canvas.getAttribute('aria-disabled') === 'true') { return; }
            if (!ctx) { setup(); }
            drawing = true; last = at(e); canvas.setPointerCapture(e.pointerId);
            ctx.beginPath(); ctx.arc(last.x, last.y, 1.1, 0, Math.PI * 2); ctx.fillStyle = '#101826'; ctx.fill();
            dirty = true;
        });
        canvas.addEventListener('pointermove', function (e) {
            if (!drawing) { return; }
            var p = at(e);
            ctx.beginPath(); ctx.moveTo(last.x, last.y); ctx.lineTo(p.x, p.y); ctx.stroke();
            last = p;
        });
        function endStroke() {
            if (!drawing) { return; }
            drawing = false;
            if (!dirty) { return; }
            canvas.toBlob(function (blob) {
                if (!blob) { return; }
                setFiles(inst, q, [{ name: q.name + '.png', type: 'image/png', size: blob.size, blob: blob }]);
                status.textContent = t(freehand ? 'Drawing captured.' : 'Signature captured.');
            }, 'image/png');
        }
        canvas.addEventListener('pointerup', endStroke);
        canvas.addEventListener('pointercancel', endStroke);
        clear.addEventListener('click', function () { dirty = false; setFiles(inst, q, []); setup(); status.textContent = t(freehand ? 'Drawing cleared.' : 'Signature cleared.'); });
        setTimeout(setup, 0);
        return {
            node: el('div', { class: freehand ? 'signature drawing' : 'signature' }, [needsPointer, canvas, el('div', { class: 'inline' }, [clear]), status].filter(Boolean)),
            controls: function () { return [canvas]; },
            sync: function (ro) { canvas.setAttribute('aria-disabled', ro ? 'true' : 'false'); clear.disabled = !!ro; }
        };
    }

    // The `annotate` appearance (ADR-287 section 5.24): the drawing pad of section 5.20, laid over a photo the member took
    // or chose. Choosing the photo answers the question at once, so a person who cannot draw (keyboard only, a screen
    // reader) still answers it; drawing changes that answer to one flattened JPEG, saved after every line, so a reload or a
    // redraw of the page loses nothing. The original photo is not kept beside it: the question holds one picture.
    Object.assign(TR, {
        "Choose or take a photo, then draw on it if you need to. The photo can be sent as it is.": "Bir fotoğraf seçin ya da çekin, gerekirse üzerine çizin. Fotoğraf olduğu gibi de gönderilebilir.",
        "The attached photo": "Eklenen fotoğraf",
        "Draw on the photo": "Fotoğrafın üzerine çiz",
        "Remove photo": "Fotoğrafı kaldır",
        "The photo, to draw on": "Üzerine çizilecek fotoğraf",
        "Pen colour": "Kalem rengi",
        "Pen width": "Kalem kalınlığı",
        "Red": "Kırmızı",
        "Yellow": "Sarı",
        "Blue": "Mavi",
        "Black": "Siyah",
        "White": "Beyaz",
        "Thin": "İnce",
        "Medium": "Orta",
        "Thick": "Kalın",
        "Undo": "Geri al",
        "Done drawing": "Çizimi bitir",
        "Opening the photo…": "Fotoğraf açılıyor…",
        "Photo attached. You can draw on it, or leave it as it is.": "Fotoğraf eklendi. Üzerine çizebilir ya da olduğu gibi bırakabilirsiniz.",
        "This photo could not be opened for drawing. It is attached as it is.": "Bu fotoğraf çizim için açılamadı. Olduğu gibi eklendi.",
        "Last line undone.": "Son çizgi geri alındı.",
        "Drawing cleared. The photo is as it was.": "Çizim temizlendi; fotoğraf ilk hâline döndü.",
        "The photo is saved with your drawing.": "Fotoğraf çiziminizle birlikte kaydedildi.",
        "The photo is attached as it is.": "Fotoğraf olduğu gibi eklendi.",
        "Photo removed.": "Fotoğraf kaldırıldı."
    });

    // max-pixels, as Survey123's media page and ODK's question-types page both define it, is the length of the long edge
    // in pixels. With no max-pixels an annotated photo is held to 1280, the image size Survey123 documents as its default;
    // that limit is also what keeps a phone's 48-megapixel photo from being drawn on at full size.
    var ANNOTATE_DEFAULT_EDGE = 1280;
    var ANNOTATE_COLOURS = [['#d7191c', 'Red'], ['#ffd400', 'Yellow'], ['#1f6fd1', 'Blue'], ['#101826', 'Black'], ['#ffffff', 'White']];
    var ANNOTATE_WIDTHS = [[2, 'Thin'], [5, 'Medium'], [10, 'Thick']];   // CSS pixels on the screen, whatever the photo's size
    // The photo being drawn on, its lines and whether the pad is open, by instance and question, for the page's life: a
    // structural redraw builds the widget again and must find them where they were.
    var annotateSessions = {};

    function fittedSize(w, h, maxEdge) {
        var long = Math.max(w, h);
        var scale = maxEdge && long > maxEdge ? maxEdge / long : 1;
        return { w: Math.max(1, Math.round(w * scale)), h: Math.max(1, Math.round(h * scale)), scaled: scale < 1 };
    }

    function canvasBlob(canvas, type, quality) {
        return new Promise(function (resolve) { canvas.toBlob(function (b) { resolve(b); }, type, quality); });
    }

    function fileStem(name) { return String(name || '').replace(/\.[^.]+$/, '') || 'photo'; }

    // Decodes a photo into a canvas no longer than maxEdge on its long side. The size is read from an <img> first, which
    // needs only the file's header, so the decode can be asked for at the small size (createImageBitmap's resize options)
    // and a large photo is not held as full-size pixels where the browser can avoid it; where it cannot, the <img> is
    // drawn scaled. Resolves null for a file this browser cannot read as an image (HEIC in Chrome, for one).
    function decodeFitted(blob, maxEdge) {
        return new Promise(function (resolve) {
            var url = URL.createObjectURL(blob);
            var img = new Image();
            img.onerror = function () { URL.revokeObjectURL(url); resolve(null); };
            img.onload = async function () {
                try {
                    if (!img.naturalWidth || !img.naturalHeight) { resolve(null); return; }
                    var size = fittedSize(img.naturalWidth, img.naturalHeight, maxEdge);
                    var bitmap = null;
                    if (window.createImageBitmap) {
                        try { bitmap = await createImageBitmap(blob, { resizeWidth: size.w, resizeHeight: size.h, resizeQuality: 'high' }); } catch (e) { bitmap = null; }
                    }
                    var canvas = document.createElement('canvas');
                    canvas.width = size.w; canvas.height = size.h;
                    var ctx = canvas.getContext('2d');
                    ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, size.w, size.h);   // a transparent PNG becomes a JPEG on white
                    ctx.drawImage(bitmap || img, 0, 0, size.w, size.h);
                    if (bitmap && bitmap.close) { bitmap.close(); }
                    resolve({ canvas: canvas, scaled: size.scaled });
                } catch (e) {
                    resolve(null);
                } finally {
                    URL.revokeObjectURL(url);
                }
            };
            img.src = url;
        });
    }

    function annotateWidget(inst, q, id, describedBy) {
        var key = inst.id + '\u0001' + q.name;
        var maxEdge = Number((q.parameters || {})['max-pixels']) || ANNOTATE_DEFAULT_EDGE;
        var s = annotateSessions[key] || (annotateSessions[key] = {
            base: null, strokes: [], photo: null, saved: null, open: false, colour: ANNOTATE_COLOURS[0][0], width: 5, version: 0, saving: Promise.resolve()
        });
        var readOnly = false;

        var how = el('p', { class: 'hint', id: id + '-how', text: t('Choose or take a photo, then draw on it if you need to. The photo can be sent as it is.') });
        // No `capture`: Survey123's web app offers a file to browse for, and a phone's file chooser offers its camera too.
        var input = el('input', {
            type: 'file', id: id, accept: 'image/*', 'data-focus': 'input', 'aria-labelledby': id + '-label',
            'aria-describedby': (describedBy ? describedBy + ' ' : '') + id + '-how'
        });
        var preview = el('img', { class: 'annotate-preview', alt: t('The attached photo') });
        preview.addEventListener('error', function () { preview.hidden = true; });   // never a broken image; the words stay
        var openButton = el('button', { type: 'button', class: 'quiet', 'data-focus': 'open', 'aria-describedby': id + '-label', text: t('Draw on the photo') });
        var removeButton = el('button', { type: 'button', class: 'quiet', 'data-focus': 'remove', 'aria-describedby': id + '-label', text: t('Remove photo') });
        var held = el('div', { class: 'annotate-held', hidden: true }, [preview, el('div', { class: 'inline' }, [openButton, removeButton])]);

        var pointer = el('p', { class: 'hint', id: id + '-pointer', text: t('Drawing needs a mouse, a pen or a finger. It cannot be done with the keyboard alone.') });
        var canvas = el('canvas', { tabindex: '0', role: 'img', 'aria-label': t('The photo, to draw on'), 'aria-describedby': id + '-pointer', 'data-focus': 'pad' });
        var colour = el('select', { id: id + '-colour', 'data-focus': 'colour' },
            ANNOTATE_COLOURS.map(function (c) { return el('option', { value: c[0], text: t(c[1]) }); }));
        var width = el('select', { id: id + '-width', 'data-focus': 'width' },
            ANNOTATE_WIDTHS.map(function (w) { return el('option', { value: String(w[0]), text: t(w[1]) }); }));
        colour.value = s.colour; width.value = String(s.width);
        colour.addEventListener('change', function () { s.colour = colour.value; });
        width.addEventListener('change', function () { s.width = Number(width.value) || 5; });
        var undo = el('button', { type: 'button', class: 'quiet', 'data-focus': 'undo', text: t('Undo') });
        var clear = el('button', { type: 'button', class: 'quiet', 'data-focus': 'clear', text: t('Clear drawing') });
        var done = el('button', { type: 'button', 'data-focus': 'done', text: t('Done drawing') });
        var pad = el('div', { class: 'annotate-pad', hidden: true }, [
            pointer, canvas,
            el('div', { class: 'pair' }, [
                el('label', { for: id + '-colour' }, [el('span', { text: t('Pen colour') }), colour]),
                el('label', { for: id + '-width' }, [el('span', { text: t('Pen width') }), width])
            ]),
            el('div', { class: 'inline' }, [undo, clear, done])
        ]);
        var status = el('p', { class: 'hint', 'aria-live': 'polite' });

        // Emptied first, so the same words said twice are announced twice.
        function say(words) { status.textContent = ''; setTimeout(function () { status.textContent = words; }, 30); }

        function paintStroke(ctx, st) {
            ctx.strokeStyle = st.colour; ctx.fillStyle = st.colour; ctx.lineWidth = st.width;
            ctx.lineCap = 'round'; ctx.lineJoin = 'round';
            if (st.points.length === 1) {
                ctx.beginPath(); ctx.arc(st.points[0].x, st.points[0].y, st.width / 2, 0, Math.PI * 2); ctx.fill();
                return;
            }
            ctx.beginPath(); ctx.moveTo(st.points[0].x, st.points[0].y);
            for (var i = 1; i < st.points.length; i++) { ctx.lineTo(st.points[i].x, st.points[i].y); }
            ctx.stroke();
        }
        function paint() {
            if (!s.base) { return; }
            if (canvas.width !== s.base.width || canvas.height !== s.base.height) { canvas.width = s.base.width; canvas.height = s.base.height; }
            var ctx = canvas.getContext('2d');
            ctx.drawImage(s.base, 0, 0);
            s.strokes.forEach(function (st) { paintStroke(ctx, st); });
        }

        // The answer follows the pad: the photo itself while nothing is drawn, the flattened picture once something is.
        function save(words) {
            var version = ++s.version;
            s.saving = (async function () {
                var entry = s.photo;
                if (s.strokes.length) {
                    var blob = await canvasBlob(canvas, 'image/jpeg', 0.9);
                    if (!blob || version !== s.version) { return; }
                    entry = { name: fileStem(s.photo && s.photo.name) + '.jpg', type: 'image/jpeg', size: blob.size, blob: blob };
                }
                if (version !== s.version || !entry) { return; }
                s.saved = entry;
                setFiles(inst, q, [entry]);
                if (words) { say(words); }
            })();
            return s.saving;
        }

        function show() {
            var now = filesOf(inst, q.name)[0] || null;
            if (!now) { s.open = false; }
            held.hidden = !now || s.open;
            pad.hidden = !now || !s.open || !s.base;
            // The browser's file box says "No file chosen" once its value is emptied, which it is after every choice, and
            // that stood over the attached photo (UX review 2026-11-16). With a photo held it goes: Remove photo brings
            // it back for another.
            input.hidden = !!now;
            if (now && preview.getAttribute('data-for') !== String(now.size) + ':' + now.name) {
                if (preview.src) { URL.revokeObjectURL(preview.src); }
                preview.hidden = false;
                preview.src = URL.createObjectURL(now.blob);
                preview.setAttribute('data-for', String(now.size) + ':' + now.name);
            }
            if (!pad.hidden) { paint(); }
            undo.disabled = readOnly || !s.strokes.length;
            clear.disabled = readOnly || !s.strokes.length;
        }

        async function takePhoto(f) {
            say(t('Opening the photo…'));
            var fitted = await decodeFitted(f, maxEdge);
            if (!fitted) {
                var raw = { name: f.name || q.name, type: f.type || 'application/octet-stream', size: f.size, blob: f };
                s.base = null; s.strokes = []; s.photo = raw; s.saved = raw; s.open = false; s.version++;
                setFiles(inst, q, [raw]);
                show();
                say(t('This photo could not be opened for drawing. It is attached as it is.'));
                if (focusWasHere()) { removeButton.focus(); }
                return;
            }
            var photo;
            if (fitted.scaled) {
                var small = await canvasBlob(fitted.canvas, 'image/jpeg', 0.9);
                photo = { name: fileStem(f.name || q.name) + '.jpg', type: 'image/jpeg', size: small.size, blob: small };
            } else {
                photo = { name: f.name || (q.name + '.jpg'), type: f.type || 'image/jpeg', size: f.size, blob: f };
            }
            s.base = fitted.canvas; s.strokes = []; s.photo = photo; s.saved = photo; s.open = true; s.version++;
            setFiles(inst, q, [photo]);
            show();
            say(t('Photo attached. You can draw on it, or leave it as it is.'));
            if (focusWasHere()) { canvas.focus({ preventScroll: true }); }
            reveal();
        }

        // The file box the person used is hidden once it holds a photo; focus that was on it (or fell to the page when it
        // went) moves into the widget, and focus the person has since put elsewhere is left alone.
        function focusWasHere() {
            var a = document.activeElement;
            return input.isConnected && (!a || a === document.body || a === input);
        }

        // The pad brought whole onto the screen when it opens, so Done is not below the fold on a phone (UX review
        // 2026-11-16: at 390 by 844 it was 270 px below). Taller than the screen, its top is shown.
        function reveal() {
            if (pad.hidden || !pad.isConnected || !pad.scrollIntoView) { return; }
            var r = pad.getBoundingClientRect();
            if (r.top >= 0 && r.bottom <= window.innerHeight) { return; }
            pad.scrollIntoView({ block: r.height <= window.innerHeight ? 'end' : 'start' });
        }

        input.addEventListener('change', function () {
            var f = input.files && input.files[0];
            input.value = '';
            if (f) { takePhoto(f); }
        });

        openButton.addEventListener('click', async function () {
            var now = filesOf(inst, q.name)[0];
            if (!now || readOnly) { return; }
            // The same session goes on where it was; a picture from a draft or another session is the new ground, its
            // earlier lines part of it, as Survey123 says of a saved image reopened.
            if (!s.base || (now !== s.saved && now !== s.photo)) {
                say(t('Opening the photo…'));
                var fitted = await decodeFitted(now.blob, maxEdge);
                if (!fitted) { say(t('This photo could not be opened for drawing. It is attached as it is.')); return; }
                s.base = fitted.canvas; s.strokes = []; s.photo = now; s.saved = now;
            }
            s.open = true;
            show();
            canvas.focus({ preventScroll: true });
            reveal();
        });

        removeButton.addEventListener('click', function () {
            s.base = null; s.strokes = []; s.photo = null; s.saved = null; s.open = false; s.version++;
            setFiles(inst, q, []);
            show();
            input.focus();
            say(t('Photo removed.'));
        });

        var drawing = null;
        function at(e) {
            var r = canvas.getBoundingClientRect();
            return { x: (e.clientX - r.left) * canvas.width / r.width, y: (e.clientY - r.top) * canvas.height / r.height };
        }
        canvas.addEventListener('pointerdown', function (e) {
            if (readOnly || !s.base) { return; }
            var r = canvas.getBoundingClientRect();
            drawing = { colour: s.colour, width: s.width * canvas.width / (r.width || canvas.width), points: [at(e)] };
            s.strokes.push(drawing);
            canvas.setPointerCapture(e.pointerId);
            paintStroke(canvas.getContext('2d'), drawing);
            e.preventDefault();
        });
        canvas.addEventListener('pointermove', function (e) {
            if (!drawing) { return; }
            var p = at(e), last = drawing.points[drawing.points.length - 1];
            drawing.points.push(p);
            var ctx = canvas.getContext('2d');
            ctx.strokeStyle = drawing.colour; ctx.lineWidth = drawing.width; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
            ctx.beginPath(); ctx.moveTo(last.x, last.y); ctx.lineTo(p.x, p.y); ctx.stroke();
        });
        function endStroke() {
            if (!drawing) { return; }
            drawing = null;
            show();
            // Saved silently: a status after every line is noise (owner decision 2026-11-17, ADR-287 §5.24); Done says it.
            save('');
        }
        canvas.addEventListener('pointerup', endStroke);
        canvas.addEventListener('pointercancel', endStroke);

        undo.addEventListener('click', function () {
            if (!s.strokes.length) { return; }
            s.strokes.pop();
            show();
            save(t('Last line undone.'));
            if (undo.disabled) { canvas.focus(); }   // the last line went, and Undo with it: focus stays on the pad
        });
        clear.addEventListener('click', function () {
            s.strokes = [];
            show();
            save(t('Drawing cleared. The photo is as it was.'));
            canvas.focus();
        });
        done.addEventListener('click', async function () {
            s.open = false;
            await s.saving;
            show();
            openButton.focus();
            say(t(s.strokes.length ? 'The photo is saved with your drawing.' : 'The photo is attached as it is.'));
        });

        return {
            node: el('div', { class: 'annotate' }, [how, input, held, pad, status]),
            controls: function () { return [input]; },
            sync: function (ro) {
                readOnly = !!ro;
                input.disabled = readOnly;
                openButton.disabled = removeButton.disabled = done.disabled = colour.disabled = width.disabled = readOnly;
                canvas.setAttribute('aria-disabled', readOnly ? 'true' : 'false');
                show();
            }
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Refresh: recalculate, redraw if the structure moved, update every view in place.
    // ---------------------------------------------------------------------------------------------------------------

    var refreshing = false;

    function refresh(structural) {
        if (refreshing) { return; }
        refreshing = true;
        try {
            structureChanged = false;
            recompute();
            if (structural || structureChanged) { render(); } else { update(); }
            // The summary follows the answers: an item answered since it was listed leaves it, and an empty summary
            // goes (UX review 2026-11-09, second pass). Focus does not move.
            if (summaryKeys && !$('summary').hidden) {
                var still = problems().filter(function (p) { return summaryKeys.indexOf(p.key) >= 0; });
                if (still.length) { showSummary(still); } else { $('summary').hidden = true; summaryKeys = null; }
            }
        } finally {
            refreshing = false;
        }
    }

    function update() {
        views.forEach(function (v) { v.update(); });
        drawPageControls();
    }

    function relevantPages() {
        var list = pages();
        return list.map(function (p, i) { return pageRelevant(p) ? i : -1; }).filter(function (i) { return i >= 0; });
    }

    function drawPageControls() {
        if (!form.pages) {
            $('back').hidden = true; $('next').hidden = true; $('submit').hidden = false; $('pages-progress').hidden = true;
            return;
        }
        var shown = relevantPages();
        var at = shown.indexOf(pageIndex);
        if (at < 0 && shown.length) { pageIndex = shown[0]; at = 0; }
        $('back').hidden = at <= 0;
        $('next').hidden = at >= shown.length - 1;
        $('submit').hidden = at < shown.length - 1;
        var progress = $('pages-progress');
        progress.hidden = shown.length <= 1;
        progress.textContent = isTurkish() ? 'Sayfa ' + (at + 1) + ' / ' + shown.length : 'Page ' + (at + 1) + ' of ' + shown.length;
    }

    function pageOfKey(key) {
        var top = key.split('/')[0].split('.')[0];
        var list = pages();
        for (var i = 0; i < list.length; i++) {
            if (list[i].some(function (n) { return n.name === top || containsName(n, top); })) { return i; }
        }
        return 0;
    }

    function containsName(node, name) {
        return (node.body || []).some(function (n) { return n.name === name || containsName(n, name); });
    }

    function goToPage(i) {
        pageIndex = i;
        render();
        scheduleDraft();
        $('summary').hidden = true;
        var first = $('body').querySelector('h2, h3, input, select, textarea');
        if (first) {
            if (!/^(INPUT|SELECT|TEXTAREA)$/.test(first.tagName)) { first.setAttribute('tabindex', '-1'); }
            first.focus();
        }
        window.scrollTo(0, 0);
    }

    function pageProblems() {
        var list = pages()[pageIndex] || [];
        return problems().filter(function (p) { return list.some(function (n) { return n.name === p.key.split('/')[0].split('.')[0] || containsName(n, p.key.split('/')[0].split('.')[0]); }); });
    }

    var summaryKeys = null;

    function showSummary(found) {
        summaryKeys = found.map(function (p) { return p.key; });
        var summary = $('summary');
        summary.textContent = '';
        summary.appendChild(el('h2', { text: found.length === 1 ? t('One answer needs attention')
            : (isTurkish() ? found.length + ' cevaba bakmanız gerekiyor' : found.length + ' answers need attention') }));
        var ul = el('ul');
        found.forEach(function (p) {
            // Which item of which repeat, so three "Floor number" messages say which floors (UX review 2026-11-09).
            var where = chainOf(p.inst).slice(1).map(function (i) { return text(i.node.labels, i.node.name) + ' ' + positionOf(i); });
            where.push(text(p.q.labels, p.q.name));
            var a = el('a', { href: '#', text: where.join(' › ') + ': ' + p.message });
            a.addEventListener('click', function (e) { e.preventDefault(); focusProblem(p); });
            ul.appendChild(el('li', null, [a]));
        });
        summary.appendChild(ul);
        summary.hidden = false;
    }

    function focusProblem(p) {
        if (form.pages) {
            var page = pageOfKey(p.key);
            if (page !== pageIndex) { pageIndex = page; render(); }
        }
        var node = $('body').querySelector('[data-key="' + cssEscape(p.key) + '"]');
        expandAround(node);
        var control = node && (node.querySelector('[aria-invalid=true]') || node.querySelector('input:not([type=hidden]):not([type=file]), select, textarea')
            || node.querySelector('input[type=file], canvas, button'));
        if (control) { control.focus(); if (control.scrollIntoView) { control.scrollIntoView({ block: 'center' }); } }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The record: what the server is sent. Parents before children; non-relevant answers left out.
    // ---------------------------------------------------------------------------------------------------------------

    function layerFor(repeat) {
        var pub = doc.publication;
        if (!pub || !Array.isArray(pub.layers)) { return null; }
        return pub.layers.filter(function (l) { return (l.repeat || '') === repeat; })[0] || null;
    }

    function depthOf(layer) {
        // How many tables above it: the form's own is 0, a repeat at the top level 1, a repeat inside that 2.
        var d = 0, l = layer;
        while (l && (l.repeat || '') !== '' && d < 50) { d++; l = layerFor(l.parentRepeat || ''); }
        return d;
    }

    function attributeValue(q, v) {
        if (v === undefined || v === null || String(v) === '') { return null; }
        var s = String(v);
        switch (q.type) {
            case 'integer': return parseInt(s, 10);
            case 'decimal': case 'range': return Number(s);
            case 'date': case 'today': {
                var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s);
                // INFERRED: a date is midnight on the device's calendar, sent as its epoch milliseconds.
                return m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])).getTime() : null;
            }
            case 'dateTime': case 'start': case 'end': {
                var ms = Date.parse(s);
                return isNaN(ms) ? null : ms;
            }
            default: return s;
        }
    }

    function geometryOf(q, v) {
        if (!v) { return null; }
        var pts = vertices(v);
        if (!pts.length) { return null; }
        var sr = { wkid: 4326 };
        if (q.type === 'geopoint') { return { x: pts[0][1], y: pts[0][0], spatialReference: sr }; }
        var xy = pts.map(function (p) { return [p[1], p[0]]; });
        if (q.type === 'geotrace') { return { paths: [xy], spatialReference: sr }; }
        var first = xy[0], last = xy[xy.length - 1];
        if (first[0] !== last[0] || first[1] !== last[1]) { xy.push([first[0], first[1]]); }
        return { rings: [xy], spatialReference: sr };
    }

    function answered(inst) {
        // Something the person entered: a question they changed that still holds a value or a file, here or in any item
        // below. Defaults, calculations and start/today do not count, because nobody entered them. Being marked by a
        // failed Next does not count either unless a value followed.
        var found = false;
        (function walk(body) {
            body.forEach(function (n) {
                if (found || inst.rel[n.name] === false) { return; }
                if (n.kind === 'group') { walk(n.body || []); return; }
                if (n.kind === 'repeat') {
                    if ((inst.repeats[n.name] || []).some(answered)) { found = true; }
                    return;
                }
                if (n.kind !== 'question' || !inst.touched[n.name]) { return; }
                var v = inst.values[n.name];
                if (filesOf(inst, n.name).length || (v !== undefined && v !== null && String(v) !== '')) { found = true; }
            });
        })(inst.node.body || []);
        return found;
    }

    function buildRecord() {
        var now = new Date();
        var byLayer = {};
        var attachments = [];
        var problemsFound = [];

        // A record from the Inbox (ADR-300 §5.6–§5.8) goes to the Inbox view: its rows that the server holds are sent as
        // updates, each with the EditDate it was downloaded with, and its new repeat rows as adds. A row of a repeat
        // that does not allow updates is not sent at all, and one that allows them is sent only when it changed.
        var edit = !!editing;
        var known = {};    // GlobalID -> object id, for the rows the server already holds

        function emit(inst, repeatName, parent) {
            var layer = layerFor(repeatName);
            if (!layer) { problemsFound.push('No layer for ' + (repeatName || 'the form') + ' in the publication.'); return; }
            if (edit && !layer.inboxUrl) { problemsFound.push('No Inbox layer for ' + (repeatName || 'the form') + ' in the publication.'); return; }
            var url = edit ? layer.inboxUrl : layer.url;
            var submitTo = edit ? layer.inboxUrl : (layer.submitUrl || layer.url);
            var existing = edit ? inst.existing : null;
            // Decided before the answers are read, so `end` moves only on a row that is sent for another reason.
            var sendRow = !existing || !parent || (!existing.locked && changedSince(inst));
            var attributes = { globalid: inst.id };
            if (parent) { attributes.parentglobalid = parent.id; }
            var geometry = null;
            var columns = layer.columns || {};
            (function fill(body) {
                body.forEach(function (n) {
                    if (inst.rel[n.name] === false) { return; }
                    if (n.kind === 'group') { fill(n.body || []); return; }
                    if (n.kind !== 'question') { return; }
                    if (n.type === 'end' && sendRow) { inst.values[n.name] = localDateTime(now); }
                    var v = inst.values[n.name];
                    if (n.name === layer.location) { geometry = geometryOf(n, v); return; }
                    if (has(layer.attachments, n.name)) {
                        filesOf(inst, n.name).forEach(function (f) {
                            attachments.push({ url: url, submit: submitTo, globalid: inst.id, question: n.name, name: f.name, type: f.type, size: f.size, blob: f.blob, edit: edit });
                        });
                        return;
                    }
                    var column = columns[n.name];
                    if (!column) { return; }
                    var value = attributeValue(n, v);
                    if (value !== null && !(typeof value === 'number' && isNaN(value))) { attributes[column] = value; }
                    // An answer cleared on a row the server holds is sent as cleared: left out, the server would keep it.
                    else if (existing) { attributes[column] = null; }
                });
            })(inst.node.body || []);
            var bucket = byLayer[url] || (byLayer[url] = { url: url, submit: submitTo, repeat: repeatName, depth: depthOf(layer), adds: [], updates: [], expected: {} });
            if (existing) {
                attributes[existing.oidField || 'objectid'] = existing.objectId;
                bucket.oidField = existing.oidField || 'objectid';
                known[inst.id] = existing.objectId;
                if (sendRow) {
                    var update = { attributes: attributes };
                    if (geometry) { update.geometry = geometry; }
                    bucket.updates.push(update);
                    bucket.expected[inst.id] = existing.editDate;
                    if (existing.base) { (bucket.base || (bucket.base = {}))[inst.id] = existing.base; }
                }
            } else {
                var add = { attributes: attributes };
                if (geometry) { add.geometry = geometry; }
                bucket.adds.push(add);
            }

            (function children(body) {
                body.forEach(function (n) {
                    if (inst.rel[n.name] === false) { return; }
                    if (n.kind === 'group') { children(n.body || []); return; }
                    if (n.kind === 'repeat') {
                        (inst.repeats[n.name] || []).forEach(function (child) {
                            // Owner decision 2026-11-09: an item nobody answered anything in is not sent. A repeat starts
                            // with one item, and the first trial on dev sent an empty room row for it. A repeat_count
                            // asked for its items, so those are sent as they are.
                            if (!n.repeatCount && !child.existing && !answered(child)) { return; }
                            emit(child, n.name, inst);
                        });
                    }
                });
            })(inst.node.body || []);
        }

        emit(root, '', null);
        var layers = Object.keys(byLayer).map(function (k) { return byLayer[k]; }).sort(function (a, b) { return a.depth - b.depth; });
        var name = form.instanceName ? evalString(form.instanceName, root, null) : '';
        var record = {
            kind: edit ? 'edit' : 'new',
            formId: itemId,
            formVersion: form.version || '',
            title: doc.title || form.title || '',
            instanceName: name,
            created: now.getTime(),
            status: 'queued',
            message: '',
            layers: layers,
            attachments: attachments,
            progress: { done: {}, attached: {}, attempted: {}, updated: {}, known: known },
            problems: problemsFound,
            leftForConnection: leftForConnection()
        };
        if (edit) {
            record.inbox = { key: editing.key, globalId: root.id, objectId: editing.objectId, label: editing.label };
            record.answers = answersSummary(root);
        }
        return record;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Sending. Each step is written back to the record before the next, so a record cut off halfway is resumed, and a
    // step that may have reached the server is checked for there before it is sent again.
    // ---------------------------------------------------------------------------------------------------------------

    async function findByGlobalIds(url, ids) {
        if (!ids.length) { return {}; }
        var where = 'globalid IN (' + ids.map(function (g) { return "'" + g.replace(/'/g, '') + "'"; }).join(',') + ')';
        var body = await api(url + '/query?' + new URLSearchParams({ f: 'json', where: where, outFields: '*', returnGeometry: 'false' }));
        var found = {};
        var oidField = body.objectIdFieldName || 'objectid';
        (body.features || []).forEach(function (f) {
            var a = f.attributes || {};
            var g = a.globalid || a.GlobalID || a.GLOBALID;
            var o = a[oidField] !== undefined ? a[oidField] : (a.objectid !== undefined ? a.objectid : a.OBJECTID);
            if (g) { found[String(g).toUpperCase()] = o; }
        });
        return found;
    }

    // The same, or null when this caller cannot read the layer. A public form submits through a Create-only view
    // (ADR-113, the publication's submitUrl) and an anonymous filler can add through it and read nothing back, so a
    // lookup by GlobalID goes to the layer itself and is allowed to fail.
    async function lookUp(url, ids) {
        try {
            return await findByGlobalIds(url, ids);
        } catch (e) {
            if (e.network) { throw e; }
            return null;
        }
    }

    // One table's rows. Measured 2026-11-09: adding a GlobalID the layer already holds answers success:false, code 400,
    // "This would duplicate a value that must be unique in this layer.", and rollbackOnFailure undoes the rest of the
    // batch. So a row that fails is looked for by its GlobalID: found, it arrived on an earlier attempt and is done;
    // and the others of a rolled-back batch are sent again without it.
    async function sendLayer(layer, p, save) {
        var gidOf = function (a) { return a.attributes.globalid; };
        var notDone = function (a) { return p.done[gidOf(a)] === undefined; };
        for (var attempt = 0; attempt < 4; attempt++) {
            var pending = layer.adds.filter(notDone);
            if (!pending.length) { return; }
            // A step that was started before may have reached the server: look for its rows first.
            if (pending.some(function (a) { return p.attempted[gidOf(a)]; })) {
                var already = (await lookUp(layer.url, pending.map(gidOf))) || {};
                pending.forEach(function (a) { var o = already[gidOf(a).toUpperCase()]; if (o !== undefined) { p.done[gidOf(a)] = o; } });
                await save();
                pending = pending.filter(notDone);
                if (!pending.length) { return; }
            }
            pending.forEach(function (a) { p.attempted[gidOf(a)] = true; });
            await save();
            var result = await api((layer.submit || layer.url) + '/applyEdits', {
                method: 'POST',
                body: formBody({ f: 'json', useGlobalIds: 'true', rollbackOnFailure: 'true', adds: JSON.stringify(pending) })
            });
            var failed = {};
            (result.addResults || []).forEach(function (r, i) {
                var gid = pending[i] && gidOf(pending[i]);
                if (!gid) { return; }
                if (r.success && !result.rolledBack) { p.done[gid] = r.objectId; }
                else if (!r.success) { failed[gid] = (r.error && (r.error.description || r.error.message)) || 'refused'; }
            });
            await save();
            var failedIds = Object.keys(failed);
            if (!failedIds.length) { if (result.rolledBack) { continue; } return; }
            var there = await lookUp(layer.url, failedIds);
            failedIds.forEach(function (g) {
                var o = there ? there[g.toUpperCase()] : undefined;
                if (o !== undefined) { p.done[g] = o; delete failed[g]; return; }
                // Not readable (an anonymous filler behind a Create-only view): the server's own word that the GlobalID
                // is already there is taken as the row having arrived. Its object id is not known (-1).
                if (!there && p.attempted[g] && /duplicate|unique/i.test(failed[g])) { p.done[g] = -1; delete failed[g]; }
            });
            await save();
            var refusals = Object.keys(failed).map(function (g) { return failed[g]; });
            if (refusals.length) {
                var refused = new Error(refusals.filter(function (x, i, all) { return all.indexOf(x) === i; }).join(' '));
                refused.refused = true;
                throw refused;
            }
            // Every failure was a row already there: send what the rollback undid.
        }
        if (layer.adds.some(notDone)) { throw new Error('The server did not take every row of ' + layer.url + '.'); }
    }

    async function sendRecord(record, save) {
        var p = record.progress;
        for (var li = 0; li < record.layers.length; li++) {
            if (record.layers[li].updates && record.layers[li].updates.length) { await sendUpdates(record, record.layers[li], p, save); }
            await sendLayer(record.layers[li], p, save);
        }

        for (var ai = 0; ai < record.attachments.length; ai++) {
            var att = record.attachments[ai];
            var key = att.globalid + '/' + att.question + '/' + ai;
            if (p.attached[key]) { continue; }
            var oid = p.done[att.globalid];
            if (oid === undefined && p.known) { oid = p.known[att.globalid]; }   // a row the server held already (Inbox)
            if (oid === undefined) { throw new Error('The row of ' + att.question + ' was not added.'); }
            if (oid === -1) {
                var lost = new Error('The record reached the server on an earlier attempt, but this device could not learn which row it became, so '
                    + att.name + ' could not be attached. Ask the form’s owner to add it by hand.');
                lost.refused = true;
                throw lost;
            }
            if (p.attempted[key]) {
                var infos = null;
                try { infos = await api(att.url + '/' + oid + '/attachments?f=json'); } catch (err) { if (err.network) { throw err; } }
                var match = infos && (infos.attachmentInfos || []).some(function (i) { return i.name === att.name && Number(i.size) === Number(att.size); });
                if (match) { p.attached[key] = true; await save(); continue; }
            }
            p.attempted[key] = true;
            await save();
            var data = new FormData();
            data.append('f', 'json');
            data.append('keywords', att.question); // before the file, so a streaming reader has it first
            data.append('attachment', att.blob, att.name);
            // The row's GlobalID shows the answers view, which shows no row, that this device made it (2026-11-16).
            // Through the Inbox view the row is readable, so it needs no GlobalID to show it is the sender's (ADR-300 §5.8).
            var added = await api((att.submit || att.url) + '/' + oid + '/addAttachment' + (att.edit ? '' : '?parentGlobalId=' + encodeURIComponent(att.globalid)), { method: 'POST', body: data });
            if (added && added.addAttachmentResult && added.addAttachmentResult.success === false) {
                var e = new Error((added.addAttachmentResult.error && added.addAttachmentResult.error.description) || t('The attachment was refused.'));
                e.refused = true;
                throw e;
            }
            p.attached[key] = true;
            await save();
        }
    }

    var sending = false;

    async function sendOutbox() {
        if (sending) { return; }
        sending = true;
        $('send-now').disabled = true;
        setOutboxStatus('Sending…');
        try {
            var run = async function () {
                var records = (await dbAll('outbox')).sort(function (a, b) { return a.key - b.key; });
                var sent = 0, waiting = 0, refused = 0;
                for (var i = 0; i < records.length; i++) {
                    var record = records[i];
                    if (record.status === 'refused' && !record.retry) { refused++; continue; }
                    // A conflict waits for the person's choice, and a record gone from the server for them to copy it.
                    if ((record.status === 'conflict' || record.status === 'gone') && !record.retry) { waiting++; continue; }
                    var outcome = await sendOne(record);
                    if (outcome === 'sent') { sent++; } else if (outcome === 'refused') { refused++; } else { waiting++; if (outcome === 'offline') { break; } }
                }
                return { sent: sent, waiting: waiting, refused: refused };
            };
            var counts = navigator.locks && navigator.locks.request
                ? await navigator.locks.request('graticula-forms-outbox', run)
                : await run();
            setOutboxStatus(counts.sent ? counts.sent + (counts.sent === 1 ? ' record sent.' : ' records sent.') : '');
        } catch (e) {
            setOutboxStatus('The outbox could not be read: ' + e.message);
        } finally {
            sending = false;
            $('send-now').disabled = false;
            await drawOutbox();
        }
    }

    // 'sent' | 'offline' | 'sign-in' | 'refused' | 'conflict' | 'gone'
    async function sendOne(record) {
        var save = function () { return dbPut('outbox', record); };
        record.retry = false;
        try {
            await sendRecord(record, save);
            await dbDelete('outbox', record.key);
            if (record.kind === 'edit') { await inboxAfterSent(record).catch(function () { /* the next Refresh brings it */ }); }
            return 'sent';
        } catch (e) {
            if (e.conflict) {
                record.status = 'conflict';
                record.message = t('Changed on the server since you downloaded it.');
                await save();
                return 'conflict';
            }
            if (e.gone) {
                record.status = 'gone';
                record.message = t('No longer on the server.');
                await save();
                await inboxMarkGone(record).catch(function () { /* the record itself keeps the answers */ });
                return 'gone';
            }
            if (e.network) {
                record.status = 'queued';
                record.message = t('Waiting for a connection.');
                await save();
                return 'offline';
            }
            if (authFailure(e.status)) {
                record.status = 'sign-in';
                record.message = t('Sign in to send this record.');
                await save();
                return 'sign-in';
            }
            record.status = 'refused';
            record.message = e.message || t('The server refused this record.');
            await save();
            return 'refused';
        }
    }

    function setOutboxStatus(t) { $('outbox-status').textContent = t; }

    async function drawOutbox() {
        var records = (await dbAll('outbox')).sort(function (a, b) { return a.key - b.key; });
        $('outbox-count').textContent = String(records.length);
        var toggle = $('outbox-toggle');
        toggle.setAttribute('aria-label', isTurkish() ? 'Giden kutusu, bekleyen ' + records.length + ' kayıt' : 'Outbox, ' + records.length + (records.length === 1 ? ' record waiting' : ' records waiting'));
        var list = $('outbox-list');
        list.textContent = '';
        if (!records.length) {
            list.appendChild(el('li', { class: 'state', text: t('Nothing is waiting to be sent.') }));
            // Not only when the Inbox was read already: a sent change empties inboxCache to have the record read again,
            // and the guard left "Changed on the server" and a Compare button standing after it was resolved.
            if (inboxOn()) { drawInbox(); }
            return;
        }
        var keep = document.activeElement && list.contains(document.activeElement) ? document.activeElement.getAttribute('data-focus') : null;
        records.forEach(function (r) {
            var when = new Date(r.created);
            var title = (r.kind === 'edit' ? t('Changes to: ') : '') + (r.instanceName || (r.inbox && r.inbox.label) || r.title || 'Record') + ' — ' + when.toLocaleString();
            var state = r.status === 'refused' ? 'Refused by the server: ' + r.message
                : r.status === 'sign-in' ? t('Waiting: sign in to send it.')
                : r.status === 'conflict' ? t('Changed on the server since you downloaded it. Compare the two and choose which to keep.')
                : r.status === 'gone' ? t('No longer on the server. Your answers are kept here: show them to copy them.')
                : r.status === 'queued' && r.message ? r.message : t('Waiting to be sent.');
            if (r.formId === itemId && form && r.formVersion !== (form.version || '')) { state += ' (filled in an earlier version of the form)'; }
            var item = el('li', { 'data-key': String(r.key) }, [
                el('strong', { text: title }),
                el('span', { class: 'state' + (r.status === 'refused' || r.status === 'conflict' || r.status === 'gone' ? ' refused' : ''), text: state })
            ]);
            if (r.leftForConnection && r.leftForConnection.length) {
                item.appendChild(el('span', { class: 'state left-empty', text: t('Required, but left empty because it needed a connection: ') + r.leftForConnection.join(', ') + '.' }));
            }
            var actions = el('div', { class: 'row-actions' });
            if (r.status === 'conflict') {
                if (r.formId === itemId && form) {
                    var compare = el('button', { type: 'button', 'data-focus': 'compare-' + r.key, text: t('Compare') });
                    compare.addEventListener('click', function () { openCompare(r, compare); });
                    actions.appendChild(compare);
                } else {
                    actions.appendChild(el('span', { class: 'state', text: t('Open its form to compare.') }));
                }
            }
            if (r.status === 'gone') {
                var show = el('button', { type: 'button', 'data-focus': 'answers-' + r.key, text: t('Show answers') });
                show.addEventListener('click', function () { showAnswers(r, show); });
                var forget = el('button', { type: 'button', class: 'danger', 'data-focus': 'forget-' + r.key, text: t('Discard') });
                forget.addEventListener('click', async function () {
                    if (!window.confirm(t('Discard this record? It is no longer on the server, and your answers cannot be recovered.'))) { return; }
                    await dbDelete('outbox', r.key);
                    await inboxForget(r).catch(function () { /* nothing kept */ });
                    await drawOutbox();
                    $('outbox-toggle').focus();
                });
                actions.appendChild(show);
                actions.appendChild(forget);
            }
            if (r.status === 'refused') {
                var retry = el('button', { type: 'button', class: 'quiet', text: t('Try again') });
                retry.addEventListener('click', async function () { r.retry = true; await dbPut('outbox', r); sendOutbox(); });
                var discard = el('button', { type: 'button', class: 'danger', text: t('Discard') });
                discard.addEventListener('click', async function () {
                    if (!window.confirm('Discard this record? It has not been accepted by the server and cannot be recovered.')) { return; }
                    await dbDelete('outbox', r.key);
                    drawOutbox();
                });
                actions.appendChild(retry);
                actions.appendChild(discard);
            }
            if (r.status === 'sign-in') {
                actions.appendChild(el('a', { href: '/studio/', target: '_blank', rel: 'noopener', text: t('Sign in') }));
            }
            if (actions.childNodes.length) { item.appendChild(actions); }
            list.appendChild(item);
        });
        if (keep) { var again = list.querySelector('[data-focus="' + cssEscape(keep) + '"]'); if (again) { again.focus(); } }
        if (inboxOn()) { drawInbox(); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Drafts — ADR-287 §5.10 says the client always keeps them. A form being filled in is written to the 'drafts' store
    // a moment after the last change, files as Blobs, so a reload, a closed tab or a lost signal costs nothing. The most
    // recent draft of a form is opened again when the form is; the Drafts panel lists the rest to resume or discard.
    // A draft leaves when its record is queued in the outbox, and not before.
    // ---------------------------------------------------------------------------------------------------------------

    var draftId = null;        // the key the form on screen is kept under: its root's GlobalID, once something was entered
    var draftTimer = null;
    var draftChain = Promise.resolve();
    var draftSavedOnce = false;
    var DRAFT_DELAY = 700;

    function draftsOn() { return !previewing && !!form && !!itemId; }

    function snapshot(inst) {
        var out = { id: inst.id, values: {}, touched: {}, files: {}, repeats: {} };
        Object.keys(inst.values).forEach(function (k) { out.values[k] = inst.values[k]; });
        Object.keys(inst.touched).forEach(function (k) { if (inst.touched[k]) { out.touched[k] = true; } });
        Object.keys(inst.files).forEach(function (k) {
            out.files[k] = (inst.files[k] || []).map(function (f) { return { name: f.name, type: f.type, size: f.size, blob: f.blob }; });
        });
        Object.keys(inst.repeats).forEach(function (k) { out.repeats[k] = (inst.repeats[k] || []).map(snapshot); });
        // A record from the Inbox: which rows the server holds, and the files it holds for them (ADR-300).
        if (inst.existing) { out.existing = inst.existing; }
        if (inst.existingFiles) { out.existingFiles = inst.existingFiles; }
        return out;
    }

    // A draft is read back onto the form as it is now: an answer to a question the form no longer has is dropped, a
    // question it gained starts with its default, and the items of a repeat keep the GlobalIDs they were made with.
    function restoreInstance(node, parent, snap) {
        var inst = { id: snap.id || guid(), node: node, parent: parent, values: {}, files: {}, repeats: {}, rel: {}, touched: {}, relevant: true };
        if (snap.existing) { inst.existing = snap.existing; }
        if (snap.existingFiles) { inst.existingFiles = snap.existingFiles; }
        initBody(inst, node.body || []);
        if (snap.existing) { inst.values = {}; }   // a row the server holds takes no defaults: its answers are its own
        var questions = {}, repeats = {};
        (function walk(body) {
            body.forEach(function (n) {
                if (n.kind === 'group') { walk(n.body || []); } else if (n.kind === 'repeat') { repeats[n.name] = n; } else if (n.kind === 'question') { questions[n.name] = n; }
            });
        })(node.body || []);
        Object.keys(snap.values || {}).forEach(function (k) { if (questions[k]) { inst.values[k] = snap.values[k]; } });
        Object.keys(snap.touched || {}).forEach(function (k) { if (questions[k]) { inst.touched[k] = true; } });
        Object.keys(snap.files || {}).forEach(function (k) { if (questions[k] && isMedia(questions[k])) { inst.files[k] = snap.files[k]; } });
        Object.keys(repeats).forEach(function (k) {
            if (snap.repeats && Array.isArray(snap.repeats[k])) { inst.repeats[k] = snap.repeats[k].map(function (s) { return restoreInstance(repeats[k], inst, s); }); }
        });
        return inst;
    }

    function draftTitle() {
        var named = '';
        if (form.instanceName) { try { named = X.evaluateString(form.instanceName, contextFor(root, null)); } catch (e) { named = ''; } }
        if (named && String(named).trim()) { return String(named).trim().slice(0, 80); }
        var first = '';
        (function walk(body) {
            body.forEach(function (n) {
                if (first) { return; }
                if (n.kind === 'group') { walk(n.body || []); return; }
                if (n.kind !== 'question' || !root.touched[n.name]) { return; }
                var v = root.values[n.name];
                if (v !== undefined && v !== null && String(v).trim() && !isMedia(n) && n.type !== 'geopoint' && n.type !== 'geotrace' && n.type !== 'geoshape') {
                    first = String(v).trim().slice(0, 60);
                }
            });
        })(root.node.body || []);
        return first || t('Untitled draft');
    }

    function sayDraft(message) { var n = $('draft-status'); if (n) { n.textContent = message; } }

    // Above the form, written once when the page opens on a draft and cleared only by a button press, never by typing.
    function sayReopened(message) { var n = $('draft-reopened'); if (n) { n.textContent = message; } }

    function drawDraftBar() {
        var bar = $('draft-bar');
        if (bar) { bar.hidden = !draftId; }
    }

    function scheduleDraft() {
        if (!draftsOn() || !root) { return; }
        clearTimeout(draftTimer);
        draftTimer = setTimeout(function () { saveDraft(); }, DRAFT_DELAY);
    }

    // Written at once when the page is being left, because a phone may not give a timer the next second.
    function flushDraft() {
        if (draftTimer === null) { return draftChain; }
        clearTimeout(draftTimer);
        draftTimer = null;
        return saveDraft();
    }

    function saveDraft() {
        draftTimer = null;
        if (!draftsOn() || !root) { return draftChain; }
        var target = root;
        draftChain = draftChain.then(async function () {
            if (target !== root) { return; }
            if (!answered(target)) {
                // Everything was taken out again: there is nothing to keep.
                if (draftId) { await dbDelete('drafts', draftId).catch(function () { /* none */ }); draftId = null; drawDraftBar(); await drawDrafts(); }
                return;
            }
            try {
                var existing = await dbGet('drafts', target.id).catch(function () { return null; });
                await dbPut('drafts', {
                    id: target.id, formId: itemId, formVersion: form.version || '', title: draftTitle(),
                    created: (existing && existing.created) || Date.now(), updated: Date.now(),
                    lang: lang, pageIndex: pageIndex, tree: snapshot(target), inbox: editing || null
                });
                var first = !draftId;
                draftId = target.id;
                drawDraftBar();
                if (first || !draftSavedOnce) { sayDraft(t('Draft saved on this device.')); draftSavedOnce = true; }
                await drawDrafts();
                if (first) { storeFormCopy().catch(function () { /* the service worker's copy still stands */ }); }
            } catch (e) {
                sayDraft(t('This browser could not keep the draft: ') + (e && e.message ? e.message : ''));
            }
        });
        return draftChain;
    }

    async function discardDraftRecord(id) {
        clearTimeout(draftTimer);
        draftTimer = null;
        draftChain = draftChain.then(function () { return dbDelete('drafts', id).catch(function () { /* none */ }); });
        await draftChain;
        if (draftId === id) { draftId = null; drawDraftBar(); }
        await drawDrafts();
    }

    async function listDrafts() {
        var all = await dbAll('drafts');
        return all.filter(function (d) { return d.formId === itemId; }).sort(function (a, b) { return b.updated - a.updated; });
    }

    async function drawDrafts() {
        var toggle = $('drafts-toggle');
        if (!toggle) { return; }
        var list = $('drafts-list');
        var mine = draftsOn() ? await listDrafts() : [];
        $('drafts-count').textContent = String(mine.length);
        toggle.setAttribute('aria-label', isTurkish() ? 'Taslaklar, ' + mine.length : 'Drafts, ' + mine.length + (mine.length === 1 ? ' draft' : ' drafts'));
        var keep = document.activeElement && list.contains(document.activeElement) ? document.activeElement.getAttribute('data-focus') : null;
        list.textContent = '';
        if (!mine.length) { list.appendChild(el('li', { class: 'state', text: t('No drafts for this form.') })); return; }
        mine.forEach(function (d) {
            var current = d.id === draftId;
            var item = el('li', { 'data-key': d.id }, [
                el('strong', { text: d.title }),
                el('span', { class: 'state', text: (current ? t('Open now') + ' — ' : '') + new Date(d.updated).toLocaleString()
                    + (d.formVersion !== (form.version || '') ? ' ' + t('(started in an earlier version of the form)') : '') })
            ]);
            var resume = el('button', { type: 'button', class: 'quiet', 'data-focus': 'resume-' + d.id, text: t('Resume'), 'aria-label': t('Resume') + ': ' + d.title });
            var discard = el('button', { type: 'button', class: 'danger', 'data-focus': 'discard-' + d.id, text: t('Discard'), 'aria-label': t('Discard') + ': ' + d.title });
            if (current) { resume.disabled = true; }
            resume.addEventListener('click', function () { resumeDraft(d.id); });
            discard.addEventListener('click', function () { discardFromList(d); });
            item.appendChild(el('div', { class: 'row-actions' }, [resume, discard]));
            list.appendChild(item);
        });
        if (keep) { var again = list.querySelector('[data-focus="' + cssEscape(keep) + '"]'); if (again && !again.disabled) { again.focus(); } }
    }

    async function discardFromList(d) {
        if (!window.confirm(t('Discard this draft? It cannot be recovered.'))) { return; }
        var wasCurrent = d.id === draftId;
        await discardDraftRecord(d.id);
        if (wasCurrent) { startBlank(true); }
        await drawDrafts();
        sayDraft(t('Draft discarded.'));
        var next = $('drafts-list').querySelector('button:not(:disabled)');
        (next || $('drafts-toggle')).focus();
    }

    async function resumeDraft(id) {
        await flushDraft();
        var d = await dbGet('drafts', id).catch(function () { return null; });
        if (!d || d.formId !== itemId) { sayDraft(t('That draft is no longer on this device.')); await drawDrafts(); return; }
        restoreFrom(d);
        sayReopened('');
        $('drafts').hidden = true;
        $('drafts-toggle').setAttribute('aria-expanded', 'false');
        sayDraft(t('Draft resumed.') + (d.formVersion !== (form.version || '') ? ' ' + t('It was started in an earlier version of the form; answers to questions that no longer exist were left out.') : ''));
        focusFormTop();
    }

    function restoreFrom(d) {
        showAll = false;
        editing = d.inbox || null;
        root = restoreInstance(form.root, null, d.tree);
        draftId = d.id;
        drawEditingBar();
        pageIndex = Number(d.pageIndex) || 0;
        if (d.lang !== undefined && d.lang !== lang && (form.languages || []).indexOf(d.lang) >= 0) {
            lang = d.lang;
            $('language').value = lang;
            applyStaticText();
        }
        $('done').hidden = true;
        $('summary').hidden = true;
        $('form').hidden = false;
        drawDraftBar();
        drawEditingBar();
        refresh(true);
        drawDrafts();
    }

    function focusFormTop() {
        var first = $('body').querySelector('h2, h3, input, select, textarea, button');
        if (first) {
            if (!/^(INPUT|SELECT|TEXTAREA|BUTTON)$/.test(first.tagName)) { first.setAttribute('tabindex', '-1'); }
            first.focus();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The Inbox — ADR-300 §5.6–§5.8. A form whose publisher turned it on offers its signed-in members the records the
    // Inbox view gives them. Refresh downloads them, with their repeat rows and the names of their attachments, into the
    // 'inbox' store, so they can be listed, searched and opened without a signal. Opening one fills the form with its
    // answers; sending queues the change in the outbox beside new records, each row with the EditDate it was
    // downloaded with (the Graticula parameter expectedEditDates). The server refuses a row that moved since (412) or
    // is gone (404); the first is shown beside the server's version for the person to choose, the second is kept on
    // the device with its answers readable. Neither is ever dropped without the person deciding (ADR-287 condition 6).
    // ---------------------------------------------------------------------------------------------------------------

    var editing = null;      // the Inbox record on screen: { key, globalId, objectId, editDate, label }; null for a new one
    var loadedFrom = '';     // 'server' | 'device' | 'preview': where the form on screen came from
    var inboxCache = null;   // { user, meta, entries } as last read from the store
    var inboxBusy = false;
    var inboxLabels = {};    // entry key|EditDate|language -> { label, search }
    var INBOX_SHOWN = 200;   // INFERRED: a list longer than this is cut, and the search narrows it

    /** True when this page offers the Inbox: the server said the form has one, which it says only to a signed-in member. */
    function inboxOn() {
        if (previewing || !doc || !doc.publication || !doc.publication.inbox) { return false; }
        var top = layerFor('');
        // Offline the member cannot be asked; the copy of form.json kept on the device (or by the service worker) was
        // served to a signed-in one, since the server leaves publication.inbox out for anyone else.
        return !!(top && top.inboxUrl) && (!!me.name || whoUnknown || loadedFrom === 'device');
    }

    function inboxPrefix(user) { return itemId + '|' + user + '|'; }

    function normalGid(g) {
        var s = String(g || '').trim().toUpperCase();
        if (!s) { return ''; }
        return s.charAt(0) === '{' ? s : '{' + s + '}';
    }

    function attr(attributes, name) {
        if (!attributes) { return undefined; }
        if (Object.prototype.hasOwnProperty.call(attributes, name)) { return attributes[name]; }
        var lower = String(name).toLowerCase();
        var keys = Object.keys(attributes);
        for (var i = 0; i < keys.length; i++) { if (keys[i].toLowerCase() === lower) { return attributes[keys[i]]; } }
        return undefined;
    }

    function editDateOf(a) {
        var v = attr(a, 'last_edited_date');
        if (v === undefined) { v = attr(a, 'EditDate'); }
        return v === undefined || v === null ? null : Number(v);
    }

    function editorOf(a) { var v = attr(a, 'last_edited_user'); if (v === undefined) { v = attr(a, 'Editor'); } return v ? String(v) : ''; }

    /** A repeat's bind::esri:parameters setting, as true, false or undefined when the form does not say. */
    function repeatParameter(r, name) {
        if (r && typeof r[name] === 'boolean') { return r[name]; }
        var p = (r && r.parameters) || {};
        var keys = Object.keys(p);
        for (var i = 0; i < keys.length; i++) {
            if (keys[i].toLowerCase() === name.toLowerCase()) { return isYes(p[keys[i]]) ? true : isNo(p[keys[i]]) ? false : undefined; }
        }
        var written = String((r && (r.esriParameters || r['bind::esri:parameters'])) || '');
        var m = new RegExp('(?:^|[\\s,;&])' + name + '\\s*=\\s*(\\w+)', 'i').exec(written);
        return m ? (isYes(m[1]) ? true : isNo(m[1]) ? false : undefined) : undefined;
    }

    /** A row of a repeat whose existing rows may not be changed (Survey123: allowUpdates is false unless the form says true). */
    function lockedRow(inst) { return !!(inst && inst.existing && inst.existing.locked); }

    function existingFilesOf(inst, name) { return (inst.existingFiles && inst.existingFiles[name]) || []; }

    // The files the server holds for a question of a record from the Inbox: shown, linked when online, never removed.
    function existingFilesNode(inst, q, id) {
        var list = el('ul', { class: 'files existing-files', 'aria-labelledby': id + '-label ' + id + '-existing' });
        var online = navigator.onLine !== false;
        existingFilesOf(inst, q.name).forEach(function (f) {
            var children = [];
            if (online && f.url && /^image\//.test(f.contentType || '')) { children.push(el('img', { src: f.url, alt: '', loading: 'lazy' })); }
            children.push(online && f.url ? el('a', { href: f.url, target: '_blank', rel: 'noopener', text: f.name })
                : el('span', { text: f.name + ' ' + t('(shown when online)') }));
            list.appendChild(el('li', null, children));
        });
        return el('div', { class: 'existing' }, [
            el('p', { class: 'hint', id: id + '-existing', text: t('Already on the server. These cannot be changed or removed here; new files are added beside them.') }),
            list
        ]);
    }

    function canonical(values) {
        var out = {};
        Object.keys(values || {}).sort().forEach(function (k) { var v = values[k]; out[k] = v === undefined || v === null ? '' : String(v); });
        return JSON.stringify(out);
    }

    function changedSince(inst) { return !inst.existing || canonical(inst.values) !== inst.existing.original; }

    // ----- From the server's rows to answers

    function answerFromAttribute(q, v) {
        if (v === undefined || v === null || v === '') { return null; }
        var d;
        switch (q.type) {
            case 'date': case 'today':
                d = typeof v === 'number' || /^-?\d+$/.test(String(v)) ? new Date(Number(v)) : new Date(String(v));
                return isNaN(d.getTime()) ? null : localDate(d);
            case 'dateTime': case 'start': case 'end':
                d = typeof v === 'number' || /^-?\d+$/.test(String(v)) ? new Date(Number(v)) : new Date(String(v));
                return isNaN(d.getTime()) ? null : localDateTime(d);
            case 'select_multiple': case 'select_multiple_from_file':
                return String(v).split(/[\s,]+/).filter(Boolean).join(',');
            default:
                return String(v);
        }
    }

    function answerFromGeometry(q, g) {
        if (!g) { return null; }
        if (typeof g.x === 'number' && typeof g.y === 'number') { return q.type === 'geopoint' ? g.y + ' ' + g.x + ' 0 0' : null; }
        var line = (g.paths && g.paths[0]) || (g.rings && g.rings[0]);
        if (!line || !line.length) { return null; }
        var pts = line.slice();
        if (g.rings && pts.length > 1 && pts[0][0] === pts[pts.length - 1][0] && pts[0][1] === pts[pts.length - 1][1]) { pts.pop(); }
        return pts.map(function (p) { return p[1] + ' ' + p[0] + ' 0 0'; }).join(';');
    }

    // An instance of the form built from a downloaded row and the rows below it. It takes no defaults and starts no
    // empty repeat item: everything in it is what the server holds. `original` is what is compared to find a change.
    function instanceFromRow(node, parent, row, entry, repeatName) {
        var layer = layerFor(repeatName) || {};
        var inst = { id: normalGid(row.globalId), node: node, parent: parent, values: {}, files: {}, repeats: {}, rel: {}, touched: {}, relevant: true, existingFiles: {} };
        var columns = layer.columns || {};
        var held = (entry.attachments && entry.attachments[inst.id]) || [];
        var media = [];
        (function walk(body) {
            body.forEach(function (n) {
                if (n.kind === 'group') { walk(n.body || []); return; }
                if (n.kind === 'repeat') {
                    var rows = ((entry.repeats || {})[n.name] || []).filter(function (r) { return normalGid(r.parentGlobalId) === inst.id; });
                    inst.repeats[n.name] = rows.map(function (r) { return instanceFromRow(n, inst, r, entry, n.name); });
                    return;
                }
                if (n.kind !== 'question') { return; }
                if (isMedia(n)) { media.push(n); return; }
                var v = n.name === layer.location ? answerFromGeometry(n, row.geometry)
                    : columns[n.name] ? answerFromAttribute(n, attr(row.attributes, columns[n.name])) : null;
                if (v !== null) { inst.values[n.name] = v; }
            });
        })(node.body || []);
        // Attachments are matched to their question by keywords, which this client sends; one media question takes all.
        media.forEach(function (n) {
            var mine = held.filter(function (a) { return a.keywords === n.name || (media.length === 1 && !a.keywords); });
            if (!mine.length) { return; }
            inst.existingFiles[n.name] = mine.map(function (a) {
                return { id: a.id, name: a.name, contentType: a.contentType, size: a.size,
                    url: layer.inboxUrl ? layer.inboxUrl + '/' + row.objectId + '/attachments/' + a.id : '' };
            });
            inst.values[n.name] = mine.map(function (a) { return a.name; }).join(' ');
        });
        inst.existing = {
            objectId: row.objectId, oidField: entry.oidFields && entry.oidFields[repeatName || ''] || 'objectid',
            editDate: row.editDate, locked: !!parent && repeatParameter(node, 'allowUpdates') !== true,
            original: canonical(inst.values),
            // The row as it was downloaded, so a conflict can say who changed each answer and Keep mine can leave the
            // answers only the server changed as the server has them (UX review 2026-11-16).
            base: { attributes: row.attributes || {}, geometry: row.geometry || null }
        };
        return inst;
    }

    function inboxInstance(entry) {
        return instanceFromRow(form.root, null, { globalId: entry.globalId, objectId: entry.objectId, attributes: entry.attributes, geometry: entry.geometry, editDate: entry.editDate }, entry, '');
    }

    // ----- Labels and search

    function shownAnswer(q, v) {
        if (v === undefined || v === null || v === '') { return ''; }
        var listName = q.list || q.file;
        if (listName && choiceRows(listName)) {
            var rows = choiceRows(listName);
            return String(v).split(',').map(function (c) {
                var row = rows.filter(function (r) { return String(r.name) === c; })[0];
                return row ? choiceLabel(row) : c;
            }).join(', ');
        }
        if (q.type === 'date') { return String(v).slice(0, 10); }
        // A geopoint is kept as "lat lon altitude accuracy"; read back to a person it is "lat, lon", as the comparison says it.
        if (q.type === 'geopoint') { var at = String(v).trim().split(/\s+/); return at.length >= 2 ? at[0] + ', ' + at[1] : String(v); }
        if (q.type === 'dateTime') { var d = new Date(v); return isNaN(d.getTime()) ? String(v) : d.toLocaleString(); }
        return String(v);
    }

    function inboxLabelOf(entry) {
        var key = entry.key + '|' + entry.editDate + '|' + lang;
        if (inboxLabels[key]) { return inboxLabels[key]; }
        var inst = inboxInstance(entry);
        var label = '';
        if (form.instanceName) {
            var saved = root;
            try { root = inst; label = String(X.evaluateString(form.instanceName, contextFor(inst, null)) || '').trim(); } catch (e) { label = ''; } finally { root = saved; }
        }
        if (!label) {
            // The first answers that read as words: text, a choice, a number or a date, at most two.
            var parts = [];
            (function walk(body) {
                body.forEach(function (n) {
                    if (parts.length >= 2) { return; }
                    if (n.kind === 'group') { walk(n.body || []); return; }
                    if (n.kind !== 'question' || isMedia(n) || /^geo/.test(n.type) || !hasUi(n) || n.type === 'note') { return; }
                    var s = shownAnswer(n, inst.values[n.name]);
                    if (s.trim()) { parts.push(s.trim().slice(0, 60)); }
                });
            })(form.root.body || []);
            label = parts.join(' · ');
        }
        if (!label) { label = (isTurkish() ? 'Kayıt ' : 'Record ') + entry.objectId; }
        var search = [label].concat(Object.keys(entry.attributes || {}).map(function (k) { var v = entry.attributes[k]; return v === null || v === undefined ? '' : String(v); }));
        inboxLabels[key] = { label: label.slice(0, 120), search: fold(search.join(' ')) };
        return inboxLabels[key];
    }

    function inboxWhen(ms) {
        if (!ms) { return ''; }
        try { return new Date(ms).toLocaleString(isTurkish() ? 'tr-TR' : undefined, { dateStyle: 'medium', timeStyle: 'short' }); }
        catch (e) { return new Date(ms).toLocaleString(); }
    }

    // ----- The store

    async function inboxUser() {
        if (me.name) { return me.name; }
        // Offline: the member who last refreshed this form's Inbox on this device.
        var metas = (await dbAll('inbox')).filter(function (e) { return e.kind === 'meta' && e.formId === itemId; })
            .sort(function (a, b) { return b.refreshed - a.refreshed; });
        return metas.length ? metas[0].user : '';
    }

    async function inboxLoad() {
        var user = await inboxUser();
        var prefix = inboxPrefix(user);
        var mine = (await dbDo('inbox', 'readonly', function (s) { return s.getAll(IDBKeyRange.bound(prefix, prefix + '￿')); })) || [];
        inboxCache = {
            user: user,
            meta: mine.filter(function (e) { return e.kind === 'meta'; })[0] || null,
            entries: mine.filter(function (e) { return e.kind === 'record'; })
        };
        return inboxCache;
    }

    async function inboxEdits() {
        var out = {};
        (await dbAll('outbox')).forEach(function (r) { if (r.kind === 'edit' && r.inbox && r.inbox.key) { out[r.inbox.key] = r; } });
        return out;
    }

    function inboxQuery(url, params) {
        return api(url + '/query?' + new URLSearchParams(Object.assign({ f: 'json', outFields: '*', returnGeometry: 'true', outSR: '4326' }, params)));
    }

    function rowOf(feature, oidField, gidField) {
        var a = feature.attributes || {};
        return {
            globalId: normalGid(attr(a, gidField)), objectId: attr(a, oidField), parentGlobalId: normalGid(attr(a, 'parentglobalid')),
            attributes: a, geometry: feature.geometry || null, editDate: editDateOf(a)
        };
    }

    // Every row of a repeat layer below the given parents, a hundred parents to a query and every page of each.
    async function inboxChildren(url, parents) {
        var rows = [], oidField = 'objectid', gidField = 'globalid';
        for (var i = 0; i < parents.length; i += 100) {
            var ids = parents.slice(i, i + 100).map(function (g) { return "'" + g.replace(/'/g, '') + "'"; }).join(',');
            for (var offset = 0, page = 0; page < 100; page++) {
                var body = await inboxQuery(url, { where: 'parentglobalid in (' + ids + ')', resultOffset: String(offset) });
                oidField = body.objectIdFieldName || oidField;
                gidField = body.globalIdFieldName || gidField;
                var features = body.features || [];
                features.forEach(function (f) { rows.push(rowOf(f, oidField, gidField)); });
                offset += features.length;
                if (!body.exceededTransferLimit || !features.length) { break; }
            }
        }
        return { rows: rows, oidField: oidField };
    }

    // The names of a layer's attachments, by the row's GlobalID. Shown online only, so a failure costs only the names.
    async function inboxAttachments(url, rows) {
        var out = {};
        for (var i = 0; i < rows.length; i += 200) {
            var slice = rows.slice(i, i + 200);
            var byOid = {};
            slice.forEach(function (r) { byOid[String(r.objectId)] = r.globalId; });
            var body = await api(url + '/queryAttachments?' + new URLSearchParams({ f: 'json', objectIds: slice.map(function (r) { return r.objectId; }).join(','), returnMetadata: 'false' }));
            (body.attachmentGroups || []).forEach(function (g) {
                var gid = normalGid(g.parentGlobalId) || byOid[String(g.parentObjectId)];
                if (!gid) { return; }
                out[gid] = (out[gid] || []).concat((g.attachmentInfos || []).map(function (a) {
                    return { id: a.id, name: a.name, contentType: a.contentType, size: a.size, keywords: a.keywords || '' };
                }));
            });
        }
        return out;
    }

    async function inboxRefresh() {
        if (inboxBusy || !inboxOn()) { return; }
        inboxBusy = true;
        var status = $('inbox-status');
        var button = $('inbox-refresh');
        // aria-disabled, not disabled: a disabled button drops the focus it holds to the page, and the person who pressed
        // Refresh was left on <body> twice in the 2026-11-16 review. inboxBusy already refuses a second press.
        button.setAttribute('aria-disabled', 'true');
        status.textContent = t('Downloading the records…');
        try {
            var pub = doc.publication;
            var limit = Math.max(1, Math.min(5000, Number(pub.inbox.limit) || 1000));
            var top = layerFor('');
            var body = await inboxQuery(top.inboxUrl, { where: '1=1', resultRecordCount: String(limit) });
            var oidFields = { '': body.objectIdFieldName || 'objectid' };
            var gidField = body.globalIdFieldName || 'globalid';
            var features = (body.features || []).slice(0, limit);
            var cut = !!body.exceededTransferLimit || (body.features || []).length > limit;
            var parents = features.map(function (f) { return rowOf(f, oidFields[''], gidField); });

            // The repeats, parents before children, each read below the rows of the layer above it.
            var byRepeat = {};
            var idsOf = { '': parents.map(function (r) { return r.globalId; }) };
            var repeatLayers = (pub.layers || []).filter(function (l) { return (l.repeat || '') !== '' && l.inboxUrl; })
                .sort(function (a, b) { return depthOf(a) - depthOf(b); });
            for (var i = 0; i < repeatLayers.length; i++) {
                var rl = repeatLayers[i];
                var above = idsOf[rl.parentRepeat || ''] || [];
                var got = above.length ? await inboxChildren(rl.inboxUrl, above) : { rows: [], oidField: 'objectid' };
                byRepeat[rl.repeat] = got.rows;
                oidFields[rl.repeat] = got.oidField;
                idsOf[rl.repeat] = got.rows.map(function (r) { return r.globalId; });
            }

            var attachments = {};
            var namesMissing = false;
            var withFiles = (pub.layers || []).filter(function (l) { return l.inboxUrl && (l.attachments || []).length; });
            for (var j = 0; j < withFiles.length; j++) {
                var rowsHere = (withFiles[j].repeat || '') === '' ? parents : (byRepeat[withFiles[j].repeat] || []);
                if (!rowsHere.length) { continue; }
                try { Object.assign(attachments, await inboxAttachments(withFiles[j].inboxUrl, rowsHere)); }
                catch (e) { if (e.network) { throw e; } namesMissing = true; }
            }

            // Each record with the rows below it and their attachments' names.
            var children = {};
            Object.keys(byRepeat).forEach(function (name) {
                byRepeat[name].forEach(function (r) { (children[r.parentGlobalId] || (children[r.parentGlobalId] = [])).push({ repeat: name, row: r }); });
            });
            var user = me.name;
            var prefix = inboxPrefix(user);
            var now = Date.now();
            var entries = parents.map(function (p) {
                var repeats = {}, held = {};
                (function below(gid) {
                    if (attachments[gid]) { held[gid] = attachments[gid]; }
                    (children[gid] || []).forEach(function (c) { (repeats[c.repeat] || (repeats[c.repeat] = [])).push(c.row); below(c.row.globalId); });
                })(p.globalId);
                return {
                    key: prefix + p.globalId, kind: 'record', formId: itemId, user: user, globalId: p.globalId, objectId: p.objectId,
                    attributes: p.attributes, geometry: p.geometry, editDate: p.editDate, editor: editorOf(p.attributes),
                    repeats: repeats, attachments: held, oidFields: oidFields, downloaded: now
                };
            });

            // What the server no longer gives is taken off the device, except a record with a change still waiting
            // for it in the outbox or one already found gone: those are the person's, not the Inbox's.
            var edits = await inboxEdits();
            var fresh = {};
            entries.forEach(function (e) { fresh[e.key] = true; });
            var old = (await dbDo('inbox', 'readonly', function (s) { return s.getAll(IDBKeyRange.bound(prefix, prefix + '￿')); })) || [];
            var kept = old.filter(function (e) { return e.kind === 'record' && !fresh[e.key] && (e.gone || edits[e.key]); });
            var drop = old.filter(function (e) { return e.kind === 'record' && !fresh[e.key] && !(e.gone || edits[e.key]); });
            var meta = { key: prefix, kind: 'meta', formId: itemId, user: user, refreshed: now, count: entries.length, cut: cut, limit: limit };
            await dbDo('inbox', 'readwrite', function (s) {
                drop.forEach(function (e) { s.delete(e.key); });
                entries.forEach(function (e) {
                    var before = old.filter(function (o) { return o.key === e.key; })[0];
                    if (before && before.gone) { e.gone = false; }   // it came back: the server holds it again
                    s.put(e);
                });
                s.put(meta);
                return null;
            });
            inboxCache = { user: user, meta: meta, entries: entries.concat(kept) };
            inboxLabels = {};
            var said = isTurkish() ? entries.length + ' kayıt indirildi.' : (entries.length === 1 ? 'Downloaded 1 record.' : 'Downloaded ' + entries.length + ' records.');
            if (cut) { said += ' ' + (isTurkish() ? 'Gelen kutusunun sınırı ' + limit + ' kayıt; daha fazlası indirilmedi.' : 'The Inbox’s limit is ' + limit + ' records; more were not downloaded.'); }
            if (namesMissing) { said += ' ' + t('The names of their attachments could not be read.'); }
            // "Downloaded 2" above a list that counts 3 needs the third explained: it is the person's, kept on purpose.
            if (kept.length) {
                said += ' ' + (isTurkish() ? 'Ayrıca ' + kept.length + ' kayıt bu cihazda tutuluyor: sunucuda artık yok ya da değişikliği gönderilmeyi bekliyor.'
                    : (kept.length === 1 ? 'One more is kept on this device: it is no longer on the server, or its change is waiting.'
                        : kept.length + ' more are kept on this device: they are no longer on the server, or their changes are waiting.'));
            }
            status.textContent = said;
        } catch (e) {
            status.textContent = e.network ? t('You are offline. The records already on this device are shown.')
                : authFailure(e.status) ? t('Sign in to refresh the Inbox.')
                : t('The Inbox could not be refreshed: ') + (e.message || '');
        } finally {
            inboxBusy = false;
            button.removeAttribute('aria-disabled');
            await drawInbox();
        }
    }

    // ----- The list

    async function drawInbox() {
        if (!inboxOn()) { return; }
        var cache = inboxCache || (await inboxLoad());
        var edits = await inboxEdits();
        var count = cache.entries.length;
        $('inbox-count').textContent = String(count);
        $('inbox-toggle').setAttribute('aria-label', isTurkish() ? 'Gelen kutusu, ' + count + ' kayıt' : 'Inbox, ' + count + (count === 1 ? ' record' : ' records'));
        $('inbox-refreshed').textContent = cache.meta
            ? (isTurkish() ? 'Son yenileme: ' : 'Last refreshed ') + inboxWhen(cache.meta.refreshed) + (isTurkish() ? '.' : '.')
            : t('Nothing downloaded to this device yet. Refresh to download the records.');
        var list = $('inbox-list');
        var keep = document.activeElement && list.contains(document.activeElement) ? document.activeElement.getAttribute('data-focus') : null;
        var query = fold($('inbox-search').value.trim());
        var sorted = cache.entries.slice().sort(function (a, b) { return (b.editDate || 0) - (a.editDate || 0); });
        var found = sorted.filter(function (e) { return !query || inboxLabelOf(e).search.indexOf(query) >= 0; });
        var shown = found.slice(0, INBOX_SHOWN);
        $('inbox-shown').textContent = !count ? ''
            : isTurkish() ? (query ? count + ' kayıttan ' + found.length + ' tanesi eşleşiyor.' : count + ' kayıt.') + (found.length > shown.length ? ' İlk ' + shown.length + ' tanesi listeleniyor; daraltmak için arayın.' : '')
            : (query ? found.length + ' of ' + count + (count === 1 ? ' record matches.' : ' records match.') : count + (count === 1 ? ' record.' : ' records.'))
                + (found.length > shown.length ? ' The first ' + shown.length + ' are listed; search to narrow them.' : '');
        list.textContent = '';
        if (!count) { list.appendChild(el('li', { class: 'state', text: t('No records on this device.') })); return; }
        if (!found.length) { list.appendChild(el('li', { class: 'state', text: t('No record matches the search.') })); return; }
        shown.forEach(function (e) {
            var label = inboxLabelOf(e).label;
            var waiting = edits[e.key];
            // The member's own name read back to them as a login name ("by zz_ayse") is noise; it is "you".
            var byMe = e.editor && cache.user && e.editor.toLowerCase() === String(cache.user).toLowerCase();
            var editor = byMe ? (isTurkish() ? 'siz' : 'you') : e.editor;
            var state = (isTurkish() ? 'Son değişiklik: ' : 'Last changed ') + inboxWhen(e.editDate) + (editor ? (isTurkish() ? ', ' + editor : ' by ' + editor) : '') + '.';
            var flag = '';
            if (e.gone || (waiting && waiting.status === 'gone')) { flag = t('No longer on the server.'); }
            else if (waiting && waiting.status === 'conflict') { flag = t('Changed on the server since you downloaded it.'); }
            else if (waiting) { flag = t('Your changes are waiting in the Outbox.'); }
            var item = el('li', { 'data-key': e.key }, [el('strong', { text: label }), el('span', { class: 'state', text: state })]);
            if (flag) { item.appendChild(el('span', { class: 'state refused', text: flag })); }
            var actions = el('div', { class: 'row-actions' });
            if (e.gone || (waiting && waiting.status === 'gone')) {
                var show = el('button', { type: 'button', 'data-focus': 'answers-' + e.globalId, text: t('Show answers'), 'aria-label': t('Show answers') + ': ' + label });
                show.addEventListener('click', function () { showAnswers(waiting || e, show); });
                actions.appendChild(show);
            } else if (waiting && waiting.status === 'conflict') {
                var compare = el('button', { type: 'button', 'data-focus': 'compare-' + e.globalId, text: t('Compare'), 'aria-label': t('Compare') + ': ' + label });
                compare.addEventListener('click', function () { openCompare(waiting, compare); });
                actions.appendChild(compare);
            } else {
                // INFERRED: a record whose change still waits is not opened again; a second change would be sent against
                // the same EditDate and refused once the first lands. The Outbox is where it is sent or discarded.
                // Quiet: a column of identical filled buttons gave every row the same shout, and the Compare or
                // Show answers of a record that needs the person was lost among them.
                var open = el('button', { type: 'button', class: 'quiet', 'data-focus': 'open-' + e.globalId, text: t('Open'), 'aria-label': t('Open') + ': ' + label });
                if (waiting) { open.disabled = true; open.setAttribute('aria-describedby', 'inbox-waiting-why'); }
                open.addEventListener('click', function () { inboxOpen(e); });
                actions.appendChild(open);
            }
            item.appendChild(actions);
            list.appendChild(item);
        });
        if (keep) { var again = list.querySelector('[data-focus="' + cssEscape(keep) + '"]'); if (again && !again.disabled) { again.focus(); } }
    }

    function drawEditingBar() {
        var bar = $('inbox-editing');
        if (!bar) { return; }
        bar.hidden = !editing || $('form').hidden;
        $('inbox-editing-text').textContent = editing
            ? (isTurkish() ? 'Gelen kutusundan bir kaydı değiştiriyorsunuz: ' + editing.label + '. Gönder’e basınca değişiklikler bu kayda yazılır.'
                : 'Changing a record from the Inbox: ' + editing.label + '. Submit sends your changes to it.')
            : '';
    }

    async function inboxOpen(entry) {
        await flushDraft();
        var label = inboxLabelOf(entry).label;
        // A change to this record that was being made and not sent is a draft under its GlobalID: that is reopened.
        var draft = await dbGet('drafts', entry.globalId).catch(function () { return null; });
        clearRedirect();
        $('inbox').hidden = true;
        $('inbox-toggle').setAttribute('aria-expanded', 'false');
        if (draft && draft.formId === itemId && draft.inbox) {
            restoreFrom(draft);
            sayDraft(t('Your unsent changes to this record were reopened.'));
        } else {
            editing = { key: entry.key, globalId: entry.globalId, objectId: entry.objectId, editDate: entry.editDate, label: label };
            showAll = false;
            pageIndex = 0;
            draftId = null;
            sayReopened('');
            root = inboxInstance(entry);
            $('done').hidden = true;
            $('summary').hidden = true;
            $('form').hidden = false;
            drawDraftBar();
            drawEditingBar();
            refresh(true);
            drawDrafts();
            sayDraft(t('Record opened from the Inbox.'));
        }
        focusFormTop();
    }

    // After a change was accepted: the record is read again, so the next change is made against the new EditDate.
    async function inboxAfterSent(record) {
        var top = layerFor('');
        if (!top || !top.inboxUrl || !record.inbox) { return; }
        var body = await inboxQuery(top.inboxUrl, { where: "globalid = '" + String(record.inbox.globalId).replace(/'/g, '') + "'" });
        var f = (body.features || [])[0];
        var all = await dbGet('inbox', record.inbox.key).catch(function () { return null; });
        if (!all) { return; }
        if (!f) { return; }
        var row = rowOf(f, body.objectIdFieldName || 'objectid', body.globalIdFieldName || 'globalid');
        all.attributes = row.attributes; all.geometry = row.geometry; all.editDate = row.editDate; all.editor = editorOf(row.attributes);
        // The rows below it: read again too, so an added repeat row is an existing one the next time it is opened.
        var layers = (doc.publication.layers || []).filter(function (l) { return (l.repeat || '') !== '' && l.inboxUrl; }).sort(function (a, b) { return depthOf(a) - depthOf(b); });
        var ids = { '': [row.globalId] }, repeats = {};
        for (var i = 0; i < layers.length; i++) {
            var above = ids[layers[i].parentRepeat || ''] || [];
            var got = above.length ? await inboxChildren(layers[i].inboxUrl, above) : { rows: [] };
            repeats[layers[i].repeat] = got.rows;
            ids[layers[i].repeat] = got.rows.map(function (r) { return r.globalId; });
        }
        all.repeats = repeats;
        all.gone = false;
        await dbPut('inbox', all);
        inboxCache = null;
    }

    async function inboxMarkGone(record) {
        if (!record.inbox) { return; }
        var entry = await dbGet('inbox', record.inbox.key).catch(function () { return null; });
        if (entry) { entry.gone = true; await dbPut('inbox', entry); }
        inboxCache = null;
    }

    async function inboxForget(record) {
        if (!record.inbox) { return; }
        var entry = await dbGet('inbox', record.inbox.key).catch(function () { return null; });
        if (entry && entry.gone) { await dbDelete('inbox', record.inbox.key); }
        inboxCache = null;
    }

    // ----- Sending a change, and the two refusals

    // The rows of one layer the server holds, sent as updates with the EditDate each was downloaded with. A row refused
    // as stale is read again: when the server's version is already what was sent — an earlier attempt that arrived and
    // whose answer was lost — it is done; otherwise both versions are kept on the record for the person to choose.
    async function sendUpdates(record, layer, p, save) {
        if (!p.updated) { p.updated = {}; }
        var gidOf = function (u) { return u.attributes.globalid; };
        var oidField = layer.oidField || 'objectid';
        var pending = layer.updates.filter(function (u) { return !p.updated[gidOf(u)]; });
        if (!pending.length) { return; }
        var expected = {};
        pending.forEach(function (u) {
            var ms = layer.expected[gidOf(u)];
            if (ms !== undefined && ms !== null) { expected[String(u.attributes[oidField])] = ms; }
        });
        var result = await api(layer.submit + '/applyEdits', {
            method: 'POST',
            body: formBody({ f: 'json', useGlobalIds: 'true', rollbackOnFailure: 'false', updates: JSON.stringify(pending), expectedEditDates: JSON.stringify(expected) })
        });
        var results = result.updateResults || [];
        var stale = [], gone = [], refusals = [];
        pending.forEach(function (u, i) {
            var r = results.filter(function (x) { return (x.globalId && normalGid(x.globalId) === normalGid(gidOf(u))) || (x.objectId !== undefined && String(x.objectId) === String(u.attributes[oidField])); })[0] || results[i];
            if (!r) { refusals.push(t('The server did not answer for every changed row.')); return; }
            if (r.success) { p.updated[gidOf(u)] = true; return; }
            var code = r.error ? Number(r.error.code) : 0;
            if (code === 412) { stale.push(u); } else if (code === 404) { gone.push(u); } else { refusals.push((r.error && (r.error.description || r.error.message)) || t('The server refused this record.')); }
        });
        await save();
        if (stale.length) {
            var there = await inboxRowsByGid(layer.url, stale.map(gidOf));
            var conflicts = [];
            stale.forEach(function (u) {
                var f = there[normalGid(gidOf(u))];
                if (!f) { gone.push(u); return; }
                if (sameAnswers(u, f, oidField)) { p.updated[gidOf(u)] = true; layer.expected[gidOf(u)] = editDateOf(f.attributes); return; }
                conflicts.push({
                    layer: layer.url, repeat: layer.repeat || '', globalId: gidOf(u),
                    mine: u.attributes, mineGeometry: u.geometry || null,
                    server: f.attributes, serverGeometry: f.geometry || null,
                    base: (layer.base || {})[gidOf(u)] || null,
                    serverEditDate: editDateOf(f.attributes), serverEditor: editorOf(f.attributes)
                });
            });
            await save();
            if (conflicts.length) {
                record.conflicts = conflicts;
                await save();
                var c = new Error(t('Changed on the server since you downloaded it.'));
                c.conflict = true;
                throw c;
            }
        }
        if (gone.length) {
            record.goneRows = gone.map(gidOf);
            await save();
            var g = new Error(t('No longer on the server.'));
            g.gone = true;
            throw g;
        }
        if (refusals.length) {
            var refused = new Error(refusals.filter(function (x, i, all) { return all.indexOf(x) === i; }).join(' '));
            refused.refused = true;
            throw refused;
        }
    }

    async function inboxRowsByGid(url, ids) {
        var where = 'globalid IN (' + ids.map(function (g) { return "'" + String(g).replace(/'/g, '') + "'"; }).join(',') + ')';
        var body = await inboxQuery(url, { where: where });
        var gidField = body.globalIdFieldName || 'globalid';
        var out = {};
        (body.features || []).forEach(function (f) { out[normalGid(attr(f.attributes, gidField))] = f; });
        return out;
    }

    function sameValue(a, b) {
        var emptyA = a === undefined || a === null || a === '', emptyB = b === undefined || b === null || b === '';
        if (emptyA || emptyB) { return emptyA && emptyB; }
        if (typeof a === 'number' || typeof b === 'number') { return Number(a) === Number(b); }
        return String(a) === String(b);
    }

    function sameAnswers(update, feature, oidField) {
        var mine = update.attributes;
        return Object.keys(mine).every(function (k) {
            if (k === oidField || k === 'globalid' || k === 'parentglobalid') { return true; }
            return sameValue(mine[k], attr(feature.attributes, k));
        });
    }

    // ----- The comparison (412)

    var compareRecord = null;
    var compareReturn = null;

    function questionsOfLayer(repeatName) {
        var layer = layerFor(repeatName) || {};
        var out = [];
        var node = repeatName ? (defs[repeatName] || {}).node : form.root;
        (function walk(body) {
            (body || []).forEach(function (n) {
                if (n.kind === 'group') { walk(n.body); return; }
                if (n.kind !== 'question' || isMedia(n)) { return; }
                if (n.name === layer.location) { out.push({ q: n, location: true }); return; }
                if ((layer.columns || {})[n.name]) { out.push({ q: n, column: layer.columns[n.name] }); }
            });
        })(node ? node.body : []);
        return out;
    }

    function comparedText(item, attributes, geometry) {
        if (item.location) {
            var v = answerFromGeometry(item.q, geometry);
            if (!v) { return ''; }
            return vertices(v).map(function (p) { return p[0] + ', ' + p[1]; }).join('; ');
        }
        return shownAnswer(item.q, answerFromAttribute(item.q, attr(attributes, item.column)));
    }

    // Who changed an answer that differs, read against the row as it was downloaded (c.base): 'mine', 'server', 'both',
    // or 'differs' when the record predates the base being kept. 'same' when the two versions agree.
    var CHANGED_BY = {
        mine: 'differs: you changed it',
        server: 'differs: changed on the server, kept either way',
        both: 'differs: changed by you and on the server',
        differs: 'differs'
    };

    function conflictRows(c) {
        var out = [];
        questionsOfLayer(c.repeat).forEach(function (item) {
            var mine = comparedText(item, c.mine, c.mineGeometry);
            var theirs = comparedText(item, c.server, c.serverGeometry);
            if (!item.location && !Object.prototype.hasOwnProperty.call(c.mine, item.column) && !theirs) { return; }
            var kind = 'same';
            if (mine !== theirs) {
                if (!c.base) { kind = 'differs'; }
                else {
                    var was = comparedText(item, c.base.attributes, c.base.geometry);
                    var mineMoved = mine !== was, serverMoved = theirs !== was;
                    kind = mineMoved && serverMoved ? 'both' : serverMoved ? 'server' : mineMoved ? 'mine' : 'differs';
                }
            }
            out.push({ item: item, mine: mine, theirs: theirs, kind: kind });
        });
        return out;
    }

    /** Keep mine, answer by answer: what only the server changed is taken into the update, so sending it undoes nothing. */
    function mergeServerOnly(c, layer) {
        if (!c.base) { return; }
        var update = (layer.updates || []).filter(function (u) { return normalGid(u.attributes.globalid) === normalGid(c.globalId); })[0];
        if (!update) { return; }
        conflictRows(c).forEach(function (row) {
            if (row.kind !== 'server') { return; }
            if (row.item.location) { if (c.serverGeometry) { update.geometry = c.serverGeometry; } return; }
            var v = attr(c.server, row.item.column);
            update.attributes[row.item.column] = v === undefined ? null : v;
        });
        // The version just resolved against is the one the next comparison, if there is one, is read from.
        (layer.base || (layer.base = {}))[c.globalId] = { attributes: c.server, geometry: c.serverGeometry };
    }

    /**
     * Use the server's, answer by answer — owner decision 2026-11-16 (ADR-300 §5.12): the server's value wherever the
     * server changed an answer, and the member's change to an answer the server never touched is still sent. Returns
     * whether anything of the member's is left to send. Without the version it was downloaded at, the whole change gives way.
     */
    function yieldWhereServerMoved(c, layer) {
        if (!c.base) { return false; }
        var update = (layer.updates || []).filter(function (u) { return normalGid(u.attributes.globalid) === normalGid(c.globalId); })[0];
        if (!update) { return false; }
        var left = false;
        conflictRows(c).forEach(function (row) {
            if (row.kind === 'same') { return; }
            if (row.kind === 'mine') { left = true; return; }
            if (row.item.location) { if (c.serverGeometry) { update.geometry = c.serverGeometry; } return; }
            var v = attr(c.server, row.item.column);
            update.attributes[row.item.column] = v === undefined ? null : v;
        });
        (layer.base || (layer.base = {}))[c.globalId] = { attributes: c.server, geometry: c.serverGeometry };
        return left;
    }

    function openCompare(record, opener) {
        var again = !!compareRecord && !!compareReturn;   // opened once more after a second refusal: keep what it returns to
        compareRecord = record;
        compareReturn = again ? { focus: opener || compareReturn.focus, formHidden: compareReturn.formHidden, doneHidden: compareReturn.doneHidden }
            : { focus: opener || null, formHidden: $('form').hidden, doneHidden: $('done').hidden };
        // The "Saved on this device" box under the comparison read as a second, contradictory answer to the same send;
        // it waits until the comparison is closed (UX review 2026-11-16).
        $('done').hidden = true;
        var body = $('compare-body');
        body.textContent = '';
        var conflicts = record.conflicts || [];
        conflicts.forEach(function (c, i) {
            var who = c.serverEditor ? (isTurkish() ? c.serverEditor + ', ' : c.serverEditor + ' changed it on ') : (isTurkish() ? '' : 'It was changed on ');
            body.appendChild(el('p', { text: isTurkish()
                ? who + inboxWhen(c.serverEditDate) + ' tarihinde sunucuda değiştirdi. Hangisinin kalacağını seçin.'
                : who + inboxWhen(c.serverEditDate) + ' on the server. Choose which version to keep.' }));
            // A repeat row's table names its record too: "Bulgular" alone did not say which inspection it belonged to.
            var recordName = record.instanceName || (record.inbox && record.inbox.label) || record.title;
            var caption = c.repeat ? recordName + ' › ' + text((defs[c.repeat] || { node: {} }).node.labels, c.repeat) : recordName;
            var table = el('table', { class: 'compare-table', id: 'compare-table-' + i });
            table.appendChild(el('caption', { text: caption }));
            table.appendChild(el('thead', null, [el('tr', null, [
                el('th', { scope: 'col', text: t('Question') }), el('th', { scope: 'col', text: t('Yours') }), el('th', { scope: 'col', text: t('On the server') })
            ])]));
            var tbody = el('tbody');
            var differ = 0;
            conflictRows(c).forEach(function (row) {
                var different = row.kind !== 'same';
                if (different) { differ++; }
                tbody.appendChild(el('tr', { class: different ? 'differs' : null, 'data-question': row.item.q.name, 'data-changed': different ? row.kind : null }, [
                    el('th', { scope: 'row' }, [el('span', { text: text(row.item.q.labels, row.item.q.name) }), different ? el('span', { class: 'differs-flag', text: ' — ' + t(CHANGED_BY[row.kind]) }) : null]),
                    el('td', { 'data-label': t('Yours'), text: row.mine || '—' }),
                    el('td', { 'data-label': t('On the server'), text: row.theirs || '—' })
                ]));
            });
            table.appendChild(tbody);
            body.appendChild(el('div', { class: 'compare-scroll', role: 'region', tabindex: '0', 'aria-label': caption }, [table]));
            body.appendChild(el('p', { class: 'hint', text: isTurkish() ? differ + ' cevap farklı.' : (differ === 1 ? 'One answer differs.' : differ + ' answers differ.') }));
        });
        // What each button does, said before the person chooses (UX review 2026-11-16): without it, Keep mine looked
        // like "my whole record wins" and quietly undid the office's change to an answer the field worker never touched.
        if (conflicts.some(function (c) { return !!c.base; })) {
            body.appendChild(el('p', { class: 'compare-explain', text: t('Keep mine sends the answers you changed; answers changed only on the server stay as the server has them. Use the server’s keeps the server’s answer wherever the server changed it; your changes to the other answers, and new rows and pictures, are still sent.') }));
        }
        $('compare-actions').hidden = false;
        $('compare-close').hidden = true;
        $('compare-status').textContent = '';
        $('form').hidden = true;
        $('answers-view').hidden = true;
        $('compare').hidden = false;
        drawEditingBar();
        $('compare-heading').setAttribute('tabindex', '-1');
        $('compare-heading').focus();
        $('compare').scrollIntoView && $('compare').scrollIntoView({ block: 'start' });
    }

    async function resolveCompare(keepMine) {
        var record = compareRecord;
        if (!record) { return; }
        if (!keepMine && !window.confirm(t('Keep the server’s answers where the server changed them? Your changes to the other answers are still sent.'))) { return; }
        var fresh = await dbGet('outbox', record.key).catch(function () { return null; });
        if (fresh) { record = fresh; }
        var p = record.progress;
        var mineLeft = false;
        (record.conflicts || []).forEach(function (c) {
            var layer = record.layers.filter(function (l) { return l.url === c.layer; })[0];
            if (!layer) { return; }
            if (keepMine) { mergeServerOnly(c, layer); layer.expected[c.globalId] = c.serverEditDate; }
            else if (yieldWhereServerMoved(c, layer)) { mineLeft = true; layer.expected[c.globalId] = c.serverEditDate; }
            else { p.updated[c.globalId] = true; }
        });
        record.conflicts = null;
        record.status = 'queued';
        record.message = '';
        await dbPut('outbox', record);
        $('compare-actions').hidden = true;
        var status = $('compare-status');
        status.textContent = t(keepMine ? 'Sending your version again…' : 'Keeping the server’s version…');
        var outcome = navigator.onLine === false ? 'offline' : await sendOne(record);
        // "Anything else you added was sent" only when there was something else: said after nothing was added, it claimed
        // a send that did not happen (UX review 2026-11-16).
        var addedMore = (record.layers || []).some(function (l) { return (l.adds || []).length > 0; }) || (record.attachments || []).length > 0;
        var serverKept = mineLeft ? 'The server’s answers were kept where it changed them, and your other changes were sent.'
            : addedMore ? 'The server’s version was kept. Anything else you added was sent.' : 'The server’s version was kept.';
        status.textContent = outcome === 'sent' ? t(keepMine ? 'Your version was sent.' : serverKept)
            : outcome === 'conflict' ? t('It was changed on the server again. Compare once more.')
            : outcome === 'gone' ? t('It is no longer on the server. Your answers are kept in the Outbox.')
            : outcome === 'offline' ? t('Saved on this device. It will be sent when you are back online.')
            : outcome === 'sign-in' ? t('Saved on this device. Sign in, then send it from the Outbox.')
            : t('The server refused this record.') + ' ' + (record.message || '');
        await drawOutbox();
        if (outcome === 'sent' && compareReturn && compareReturn.doneHidden === false) {
            // The box this returns to said "not sent yet"; it now says what happened.
            $('done').classList.remove('queued');
            $('done-heading').textContent = keepMine ? STATIC_TEXT['done-heading'][isTurkish() ? 1 : 0] : t('Done');
            $('done-text').textContent = t(keepMine ? 'Your changes were sent.' : serverKept);
        }
        if (outcome === 'conflict') {
            var again = await dbGet('outbox', record.key).catch(function () { return null; });
            if (again) { var said = status.textContent; openCompare(again, compareReturn && compareReturn.focus); $('compare-status').textContent = said; return; }
        }
        $('compare-close').hidden = false;
        $('compare-close').focus();
    }

    function closeCompare() {
        $('compare').hidden = true;
        compareRecord = null;
        var back = compareReturn || {};
        compareReturn = null;
        if (!back.formHidden) { $('form').hidden = false; }
        if (back.doneHidden === false) { $('done').hidden = false; }
        drawEditingBar();
        var target = back.focus && document.body.contains(back.focus) && !back.focus.closest('[hidden]') ? back.focus : null;
        if (!target) { target = !$('done').hidden ? $('new-form') : $('outbox-toggle'); }
        target.focus();
    }

    // ----- A record gone from the server (404): its answers, readable and copyable

    var answersReturn = null;

    function answersSummary(inst) {
        var out = [];
        (function walk(i, prefix) {
            (function body(nodes) {
                nodes.forEach(function (n) {
                    if (i.rel[n.name] === false) { return; }
                    if (n.kind === 'group') { body(n.body || []); return; }
                    if (n.kind === 'repeat') {
                        (i.repeats[n.name] || []).forEach(function (child, k) { walk(child, prefix + text(n.labels, n.name) + ' ' + (k + 1) + ' › '); });
                        return;
                    }
                    if (n.kind !== 'question' || n.type === 'note') { return; }
                    var v = isMedia(n) ? existingFilesOf(i, n.name).concat(filesOf(i, n.name)).map(function (f) { return f.name; }).join(', ') : shownAnswer(n, i.values[n.name]);
                    if (v === '' || v === null || v === undefined) { return; }
                    out.push({ label: prefix + text(n.labels, n.name), value: String(v) });
                });
            })(i.node.body || []);
        })(inst, '');
        return out;
    }

    function showAnswers(source, opener) {
        answersReturn = { focus: opener || null, formHidden: $('form').hidden };
        var answers = source.answers;
        if (!answers && source.kind === 'record') {
            var saved = root;
            try { root = inboxInstance(source); recompute(); answers = answersSummary(root); } finally { root = saved; }
        }
        var list = $('answers-list');
        list.textContent = '';
        (answers || []).forEach(function (a) {
            list.appendChild(el('dt', { text: a.label }));
            list.appendChild(el('dd', { text: a.value }));
        });
        $('answers-view').answers = answers || [];
        $('answers-status').textContent = '';
        $('form').hidden = true;
        $('compare').hidden = true;
        $('answers-view').hidden = false;
        drawEditingBar();
        $('answers-heading').setAttribute('tabindex', '-1');
        $('answers-heading').focus();
    }

    async function copyAnswers() {
        var words = ($('answers-view').answers || []).map(function (a) { return a.label + ': ' + a.value; }).join('\n');
        var status = $('answers-status');
        status.textContent = '';
        try {
            await navigator.clipboard.writeText(words);
            status.textContent = t('The answers were copied.');
        } catch (e) {
            // No clipboard (an insecure page, or refused): the words are selected for the person to copy.
            var range = document.createRange();
            range.selectNodeContents($('answers-list'));
            var sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
            status.textContent = t('This browser would not copy them; they are selected for you to copy.');
        }
    }

    function closeAnswers() {
        $('answers-view').hidden = true;
        var back = answersReturn || {};
        answersReturn = null;
        if (!back.formHidden) { $('form').hidden = false; }
        drawEditingBar();
        var target = back.focus && document.body.contains(back.focus) && !back.focus.closest('[hidden]') ? back.focus : null;
        (target || $('outbox-toggle')).focus();
    }

    function wireInbox() {
        $('inbox-toggle').addEventListener('click', function () {
            var box = $('inbox');
            box.hidden = !box.hidden;
            $('inbox-toggle').setAttribute('aria-expanded', String(!box.hidden));
            if (!box.hidden) { drawInbox(); $('inbox-heading').setAttribute('tabindex', '-1'); $('inbox-heading').focus(); }
        });
        $('inbox-refresh').addEventListener('click', function () { inboxRefresh(); });
        $('inbox-search').addEventListener('input', function () { drawInbox(); });
        $('compare-mine').addEventListener('click', function () { resolveCompare(true); });
        $('compare-server').addEventListener('click', function () { resolveCompare(false); });
        $('compare-close').addEventListener('click', closeCompare);
        $('answers-copy').addEventListener('click', copyAnswers);
        $('answers-close').addEventListener('click', closeAnswers);
    }

    /** Shows the Inbox's button when this page offers one, and reads what the device holds. */
    async function startInbox() {
        $('inbox-toggle').hidden = !inboxOn();
        if (!inboxOn()) { $('inbox').hidden = true; return; }
        await inboxLoad();
        await drawInbox();
    }

    // The form is opened on its most recent draft, when it has one: a reload brings back what was being filled in.
    // INFERRED: Survey123 opens a blank form and keeps drafts in its Drafts folder; the ADR asks for drafts to survive
    // a reload, so the latest is reopened, said aloud, with Start a new form beside it.
    async function openInitial() {
        var mine = draftsOn() ? await listDrafts() : [];
        if (mine.length && !prefillRequested()) {
            restoreFrom(mine[0]);
            var stamp = new Date(mine[0].updated).toLocaleString();
            sayReopened(isTurkish() ? stamp + ' tarihli taslak yeniden açıldı.' : 'Draft from ' + stamp + ' reopened.');
            return;
        }
        startBlank();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Link parameters, as Survey123's web app reads them (ADR-287 §5.10, 2026-11-15): field:<question>=<answer> fills a
    // new form in, hide=<part>,<part> takes parts of the page away, center=<lat>,<lon> opens the location maps there.
    // Each value is checked against what it is for. One that does not fit is left out and SAID, in a panel above the
    // form, never dropped quietly. INFERRED: the set hide accepts is header (the title, kept for screen readers),
    // navbar (the language picker and the Drafts and Outbox buttons) and offline (the panel to keep the form on the
    // device); Survey123's own list is longer and names parts this page does not have.
    // ---------------------------------------------------------------------------------------------------------------

    var HIDE_PARTS = ['header', 'navbar', 'offline'];
    var link = { fields: {}, hide: [], center: null, notices: [], prefillNotices: [] };

    function parseLink(search) {
        var out = { fields: {}, hide: [], center: null, notices: [], prefillNotices: [] };
        new URLSearchParams(search).forEach(function (value, key) {
            if (key.indexOf('field:') === 0) {
                if (key.length === 6) { out.notices.push('field: — ' + t('A field parameter needs a question name after field:.')); return; }
                out.fields[key.slice(6)] = value;
            } else if (key === 'hide') {
                value.split(',').map(function (x) { return x.trim(); }).filter(Boolean).forEach(function (part) {
                    var p = part.toLowerCase();
                    if (HIDE_PARTS.indexOf(p) >= 0) { if (out.hide.indexOf(p) < 0) { out.hide.push(p); } }
                    else { out.notices.push('hide=' + part + ' — ' + t('Not a part of the page that can be hidden: ') + HIDE_PARTS.join(', ') + '.'); }
                });
            } else if (key === 'center') {
                var m = /^\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*$/.exec(value);
                var la = m ? Number(m[1]) : NaN, lo = m ? Number(m[2]) : NaN;
                if (!m || Math.abs(la) > 90 || Math.abs(lo) > 180) { out.notices.push('center=' + value + ' — ' + t('Write it as latitude,longitude: a latitude between -90 and 90, then a longitude between -180 and 180.')); }
                else { out.center = [la, lo]; }
            } else if (key === 'redirect') {
                out.notices.push('redirect=' + value + ' — ' + t('Where people are sent after submitting is set in the form by its publisher, not by a link.'));
            }
        });
        return out;
    }

    function shorten(v) { v = String(v); return v.length > 40 ? v.slice(0, 40) + '…' : v; }

    function drawLinkNotices() {
        var box = $('link-notices');
        if (!box) { return; }
        var all = link.notices.concat(link.prefillNotices);
        var ul = $('link-notices-list');
        ul.textContent = '';
        all.forEach(function (n) { ul.appendChild(el('li', { text: n })); });
        box.hidden = all.length === 0;
    }

    function prefillRequested() { return Object.keys(link.fields).length > 0; }

    // The stored text a link's answer becomes, or the reason it cannot.
    function prefillValue(name, raw) {
        var def = defs[name];
        if (!def) { return { error: t('This form has no question with that name.') }; }
        var q = def.node;
        if (q.kind !== 'question') { return { error: t('That is a group or a repeat, not a question.') }; }
        if (def.repeats.length) { return { error: t('That question sits in a repeat, which a link cannot fill.') }; }
        if (q.calculation || q.type === 'calculate') { return { error: t('That answer is worked out from others, so a link cannot set it.') }; }
        var v = String(raw);
        var m;
        switch (q.type) {
            case 'text': case 'barcode': case 'hidden': return { value: v };
            case 'integer': return /^-?\d+$/.test(v.trim()) ? { value: v.trim() } : { error: t('Enter a whole number.') };
            case 'decimal': {
                var d = v.trim().replace(',', '.');
                return d !== '' && isFinite(Number(d)) ? { value: d } : { error: t('Enter a number.') };
            }
            case 'range': {
                var r = rangeParams(q), n = v.trim().replace(',', '.');
                if (n === '' || !isFinite(Number(n))) { return { error: t('Enter a number.') }; }
                return Number(n) >= Math.min(r.start, r.end) && Number(n) <= Math.max(r.start, r.end) ? { value: n }
                    : { error: t('Outside the range this question allows.') };
            }
            case 'select_one': case 'select_one_from_file': case 'select_multiple': case 'select_multiple_from_file': {
                var rows = choiceRows(q.list || q.file);
                if (!rows) { return { error: t('The choices of this question are not available.') }; }
                var names = rows.map(function (row) { return String(row.name); });
                var multiple = q.type.indexOf('select_multiple') === 0;
                var wanted = multiple ? v.split(',').map(function (x) { return x.trim(); }).filter(Boolean) : [v.trim()];
                if (!wanted.length || wanted.some(function (w) { return names.indexOf(w) < 0; })) { return { error: t('Not one of this question’s choices: ') + names.join(', ') + '.' }; }
                wanted.sort(function (a, b) { return names.indexOf(a) - names.indexOf(b); });
                return { value: wanted.filter(function (w, i) { return wanted.indexOf(w) === i; }).join(',') };
            }
            case 'date': {
                var text = v.trim();
                if (has(q.appearance, 'year') && /^\d{4}$/.test(text)) { text += '-01-01'; }
                else if (has(q.appearance, 'month-year') && /^\d{4}-\d{2}$/.test(text)) { text += '-01'; }
                m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
                var day = m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : null;
                return day && day.getMonth() === Number(m[2]) - 1 ? { value: text } : { error: t('Write the date as YYYY-MM-DD.') };
            }
            case 'time': {
                m = /^(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(v.trim());
                return m && Number(m[1]) < 24 && Number(m[2]) < 60 ? { value: m[1] + ':' + m[2] + ':00.000' + offsetText(new Date()) } : { error: t('Write the time as HH:MM.') };
            }
            case 'dateTime': {
                var ms = Date.parse(v.trim());
                return isNaN(ms) ? { error: t('Write the date and time as YYYY-MM-DDTHH:MM.') } : { value: localDateTime(new Date(ms)) };
            }
            case 'geopoint': {
                m = /^\s*(-?\d+(?:\.\d+)?)\s*[,; ]\s*(-?\d+(?:\.\d+)?)\s*$/.exec(v);
                return m && Math.abs(Number(m[1])) <= 90 && Math.abs(Number(m[2])) <= 180 ? { value: m[1] + ' ' + m[2] + ' 0 0' }
                    : { error: t('Write the place as latitude,longitude.') };
            }
            case 'acknowledge':
                if (isYes(v)) { return { value: t('OK') }; }
                return isNo(v) ? { value: '' } : { error: t('Use yes or no.') };
            default:
                return { error: t('A link cannot fill this kind of question.') };
        }
    }

    function applyPrefill() {
        link.prefillNotices = [];
        Object.keys(link.fields).forEach(function (name) {
            var result = prefillValue(name, link.fields[name]);
            if (result.error) { link.prefillNotices.push('field:' + name + '=' + shorten(link.fields[name]) + ' — ' + result.error); }
            else if (result.value !== '') { root.values[name] = result.value; }
        });
        drawLinkNotices();
    }

    function applyHide() {
        link.hide.forEach(function (part) { document.documentElement.classList.add('hide-' + part); });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Submitting.
    // ---------------------------------------------------------------------------------------------------------------

    async function submit(e) {
        if (e) { e.preventDefault(); }
        showAll = true;
        refresh(false);
        var found = problems();
        if (found.length) {
            showSummary(found);
            focusProblem(found[0]);
            return;
        }
        $('summary').hidden = true;
        if (!doc.publication) {
            showMessage('This form is not published yet, so there is nowhere to send it.');
            return;
        }
        var record = buildRecord();
        if (record.problems.length) { showMessage(record.problems.join(' ')); return; }
        delete record.problems;

        var button = $('submit');
        button.disabled = true;
        try {
            var key = null;
            try { key = await dbPut('outbox', record); } catch (err) { key = null; }
            if (key === undefined || key === null) {
                // No storage on this device (a private window): send directly, and say so if it fails.
                try {
                    await sendRecord(record, function () { return Promise.resolve(); });
                    finish('Your answers were sent.', false, true);
                } catch (err) {
                    showMessage(err.network ? t('The server could not be reached, and this browser cannot keep the record for later. Try again when you are online.') : 'The server refused the answers: ' + err.message);
                }
                return;
            }
            record.key = key;
            // Queued: the record is in the outbox, so the draft has done its work (ADR-287 §5.10).
            await discardDraftRecord(root.id);
            if (!navigator.onLine) {
                finish('Saved on this device. It will be sent when you are back online.', true);
                await drawOutbox();
                return;
            }
            var outcome = await sendOne(record);
            await drawOutbox();
            if (outcome === 'sent') {
                finish(record.kind === 'edit' ? 'Your changes were sent.' : 'Your answers were sent.', false, true);
            } else if (outcome === 'conflict') {
                // Refused as stale (ADR-300 §5.5): both versions, at once, for the person to choose.
                finish('Not sent yet: the record was changed on the server since you downloaded it. Your changes are kept in the Outbox.', true);
                var held = await dbGet('outbox', record.key).catch(function () { return null; });
                openCompare(held || record, $('new-form'));
            } else if (outcome === 'gone') {
                finish('Not sent: the record is no longer on the server. Your answers are kept on this device, in the Outbox, to read and copy.', true);
            } else if (outcome === 'offline') {
                finish('The server could not be reached. Your answers are saved on this device and will be sent when the connection returns.', true);
            } else if (outcome === 'sign-in') {
                finish('Saved on this device. Sign in, then send it from the Outbox.', true);
            } else if (Object.keys(record.progress.done).length === 0 && Object.keys(record.progress.updated || {}).length === 0) {
                // INFERRED: refused before anything was accepted — nothing reached the server, so the record leaves the
                // outbox and the form stays filled in, for the person to correct and send again.
                await dbDelete('outbox', record.key);
                await drawOutbox();
                showMessage('The server refused the answers: ' + record.message);
                // The form stays on screen filled in, so it is kept as a draft again rather than lost with the record.
                saveDraft();
            } else {
                finish('Part of the record was refused by the server and it is kept in the Outbox: ' + record.message, true);
            }
        } finally {
            button.disabled = false;
        }
    }

    function showMessage(message) {
        var summary = $('summary');
        summary.textContent = '';
        summary.appendChild(el('h2', { text: t('Not sent') }));
        summary.appendChild(el('p', { text: t(message) }));
        summary.hidden = false;
        summary.focus();
    }

    var redirectTimer = null;

    // Only an http or https address is a place to send somebody on to: the server refuses anything else when the form
    // is saved, and this checks again, because the address is read from the form and then navigated to.
    function safeRedirect(value) {
        if (!value) { return null; }
        try {
            var u = new URL(String(value));
            return (u.protocol === 'http:' || u.protocol === 'https:') && !u.username && !u.password ? u.href : null;
        } catch (e) {
            return null;
        }
    }

    function clearRedirect() {
        clearInterval(redirectTimer);
        redirectTimer = null;
        var box = $('redirect');
        if (box) {
            box.hidden = true;
            $('redirect-text').textContent = '';
            $('redirect-text').classList.remove('sr-only');
            $('redirect-count').textContent = '';
            $('new-form').classList.remove('quiet');
        }
    }

    // The screen after an answer was sent: the publisher's thank-you in place of the page's own sentence, and, when the
    // publisher named one, the address the person is taken on to after five seconds. A redirect nobody can stop would
    // take the screen away before it is read, so it says where it goes, offers to go now and to stay, and says it once.
    // INFERRED: neither is an XLSForm column and neither is in the ADR; they are the form's own settings
    // (graticula:thank_you, graticula:redirect), and a link cannot set a redirect, which would make every form an open
    // redirect for anyone able to write a URL.
    function finish(message, queued, sent) {
        $('form').hidden = true;
        // "Submit sends your changes" means nothing once the form has gone; a new form or the next record redraws it.
        drawEditingBar();
        $('summary').hidden = true;
        var thanks = sent && form && form.thankYou ? form.thankYou : '';
        $('done-heading').textContent = queued ? t('Saved on this device') : STATIC_TEXT['done-heading'][isTurkish() ? 1 : 0];
        // Kept, not sent, is not a success: it is not drawn in the success green (UX review 2026-11-16).
        $('done').classList.toggle('queued', !!queued);
        $('done-text').textContent = thanks || t(message);
        // The draft this answer came from is gone (it was queued or sent): its "saved" line would now say the opposite.
        sayDraft('');
        $('done').hidden = false;
        $('done').focus();
        var target = sent && !previewing ? safeRedirect(form && form.redirect) : null;
        if (!target) { return; }
        // Ten seconds, counted down on screen. A screen reader hears the sentence once, from the live region, which is
        // kept off screen while the visible count (hidden from assistive technology) ticks (decision 2026-11-15).
        var seconds = 10;
        var host = new URL(target).host;
        $('redirect-text').textContent = isTurkish() ? host + ' adresine ' + seconds + ' saniye içinde yönlendirileceksiniz.'
            : 'You will be taken to ' + host + ' in ' + seconds + ' seconds.';
        $('redirect-text').classList.add('sr-only');
        function tick() {
            $('redirect-count').textContent = isTurkish() ? host + ' adresine ' + seconds + ' saniye içinde yönlendiriliyorsunuz.'
                : 'Taking you to ' + host + ' in ' + seconds + (seconds === 1 ? ' second.' : ' seconds.');
        }
        tick();
        $('redirect-go').href = target;
        $('redirect').hidden = false;
        // One thing leads: while the redirect waits, Go now is the primary action and a new form the quiet one.
        $('new-form').classList.add('quiet');
        redirectTimer = setInterval(function () {
            seconds -= 1;
            if (seconds > 0) { tick(); return; }
            clearInterval(redirectTimer);
            redirectTimer = null;
            location.assign(target);
        }, 1000);
    }

    function startBlank(quiet) {
        clearTimeout(draftTimer);
        draftTimer = null;
        showAll = false;
        pageIndex = 0;
        draftId = null;
        editing = null;
        drawEditingBar();
        sayReopened('');
        drawDraftBar();
        root = newInstance(form.root, null);
        clearRedirect();
        applyPrefill();
        $('done').hidden = true;
        $('summary').hidden = true;
        $('form').hidden = false;
        refresh(true);
        // Not in the designer's preview: focus there belongs to the designer, and a field taking it trapped the keyboard
        // inside the frame (the UX review of 2026-11-09).
        var first = previewing || quiet === true ? null : $('body').querySelector('input, select, textarea');
        // A place's first box is a coordinate nobody types first; focus stays at the top of the page then.
        if (first && first.closest('.type-geopoint, .type-geotrace, .type-geoshape')) { first = null; }
        if (first) { first.focus(); }
        drawDrafts();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Offline: the service worker, the downloaded form, the connection banner.
    // ---------------------------------------------------------------------------------------------------------------

    function drawConnection() {
        $('connection').textContent = navigator.onLine ? ''
            : t('You are offline. Records you submit are kept on this device and sent when the connection returns.');
    }

    // The form, its CSV lists and, with item 2, its pictures, kept in the 'forms' store: what a reload with no signal opens.
    async function storeFormCopy() {
        var csv = {};
        Object.keys(files).forEach(function (k) { csv[k] = files[k].text; });
        var media = {};
        var wanted = mediaNamesOf(form);
        for (var i = 0; i < wanted.length; i++) {
            var blob = await loadMedia(wanted[i]);
            if (blob) { media[wanted[i]] = blob; }
        }
        await dbPut('forms', { id: itemId, doc: doc, files: csv, media: media, scripts: scriptTexts, saved: Date.now() });
    }

    async function downloadForOffline() {
        var status = $('offline-status');
        status.textContent = t('Saving the form on this device…');
        try {
            await storeFormCopy();
            var persisted = false;
            if (navigator.storage && navigator.storage.persist) { try { persisted = await navigator.storage.persist(); } catch (e) { persisted = false; } }
            status.textContent = t('Saved for offline use') + (persisted ? '.' : '. This browser may still clear it if the device runs short of space.');
            try { localStorage.setItem('graticula-forms-last', itemId); } catch (e) { /* none */ }
        } catch (e) {
            status.textContent = 'The form could not be saved on this device: ' + e.message;
        }
    }

    function registerWorker() {
        if (!('serviceWorker' in navigator)) { return; }
        navigator.serviceWorker.register('sw.js', { scope: './' }).catch(function () { /* offline use is then unavailable */ });
    }

    async function drawDeviceForms() {
        var holder = $('device-forms');
        var stored = await dbAll('forms');
        holder.textContent = '';
        holder.className = 'device-forms';
        holder.appendChild(el('h2', { text: t('Forms on this device') }));
        if (!stored.length) {
            holder.appendChild(el('p', { class: 'hint', text: t('No form has been downloaded for offline use. Open a form from its link first.') }));
        } else {
            var ul = el('ul');
            stored.forEach(function (f) {
                ul.appendChild(el('li', null, [el('a', { href: 'fill.html?id=' + encodeURIComponent(f.id), text: (f.doc && f.doc.title) || f.id })]));
            });
            holder.appendChild(ul);
        }
        holder.hidden = false;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Start.
    // ---------------------------------------------------------------------------------------------------------------

    async function loadDoc() {
        try {
            var fetched = await api('/forms/' + encodeURIComponent(itemId) + '/form.json');
            return { doc: fetched, from: 'server' };
        } catch (e) {
            var kept = await dbGet('forms', itemId).catch(function () { return null; });
            if (kept && kept.doc) {
                Object.keys(kept.files || {}).forEach(function (k) { files[k] = { text: kept.files[k], rows: parseCsv(kept.files[k]) }; });
                Object.keys(kept.media || {}).forEach(function (k) { mediaBlobs[k] = kept.media[k]; });
                return { doc: kept.doc, from: 'device' };
            }
            throw e;
        }
    }

    async function whoAmI() {
        try {
            var who = await api('/rest/whoami');
            if (who && who.authenticated) {
                me.name = who.name || '';
                try {
                    var self = await api('/sharing/rest/community/self?f=json');
                    me.email = (self && self.email) || '';
                    me.fullName = (self && self.fullName) || '';
                    me.firstName = (self && self.firstName) || '';
                    me.lastName = (self && self.lastName) || '';
                } catch (e) { /* no email */ }
            }
        } catch (e) {
            // Anonymous, or offline. Offline the member cannot be asked, which the Inbox needs to tell apart (ADR-300).
            whoUnknown = !!e.network;
        }
    }

    var whoUnknown = false;  // true when the server could not be reached to ask who is signed in

    /** The designer's preview — /forms/preview, framed by Studio (ADR-287 §5.8): sent its form, sends nothing. */
    var previewing = /\/forms\/preview\/?$/i.test(location.pathname);

    async function start() {
        X = window.GraticulaXlsform;
        var params = new URLSearchParams(location.search);
        var fromPath = /\/forms\/([0-9a-f]{32})\/?$/i.exec(location.pathname);
        itemId = (params.get('id') || (fromPath && fromPath[1]) || '').toLowerCase();

        if (previewing) {
            startPreview();
            return;
        }

        link = parseLink(location.search);
        applyHide();
        registerWorker();
        drawConnection();
        window.addEventListener('online', function () { drawConnection(); exchanged = false; sendOutbox(); });
        window.addEventListener('offline', drawConnection);
        $('outbox-toggle').addEventListener('click', function () {
            var box = $('outbox');
            box.hidden = !box.hidden;
            $('outbox-toggle').setAttribute('aria-expanded', String(!box.hidden));
            if (!box.hidden) { $('outbox-heading').setAttribute('tabindex', '-1'); $('outbox-heading').focus(); }
        });
        $('send-now').addEventListener('click', function () { exchanged = false; sendOutbox(); });
        wireDrafts();
        wireInbox();
        $('link-notices-dismiss').addEventListener('click', function () { $('link-notices').hidden = true; $('form-main').focus(); });
        $('redirect-stay').addEventListener('click', function () {
            clearInterval(redirectTimer);
            redirectTimer = null;
            $('redirect').hidden = true;
            $('redirect-count').textContent = '';
            $('new-form').classList.remove('quiet');
            $('redirect-text').classList.remove('sr-only');
            $('redirect-text').textContent = t('You stayed on this page.');
            $('new-form').focus();
        });
        $('download-offline').addEventListener('click', downloadForOffline);
        wireForm();
        if (isIos()) { $('ios-note').hidden = false; }

        await drawOutbox();

        if (!itemId) {
            $('loading').hidden = true;
            await drawDeviceForms();
            return;
        }

        var loaded;
        try {
            await whoAmI();
            loaded = await loadDoc();
        } catch (e) {
            $('loading').textContent = e.network
                ? t('The form could not be loaded: you are offline and it has not been downloaded to this device.')
                : 'The form could not be loaded: ' + e.message;
            // Nobody signed in, and the form not found: it may be one shared with the organization, which this page has
            // no way to sign in to — a dead end for the member the Inbox is for (UX review 2026-11-16).
            if (!e.network && !me.name) {
                $('loading').appendChild(document.createTextNode(' ' + t('If this form is shared with your organization, sign in and open this link again.') + ' '));
                $('loading').appendChild(el('a', { href: '/studio/', target: '_blank', rel: 'noopener', text: t('Sign in') }));
            }
            return;
        }
        await begin(loaded);
        if (navigator.onLine) { sendOutbox(); }
        readyResolve();
    }

    function wireDrafts() {
        $('drafts-toggle').addEventListener('click', function () {
            var box = $('drafts');
            box.hidden = !box.hidden;
            $('drafts-toggle').setAttribute('aria-expanded', String(!box.hidden));
            if (!box.hidden) { drawDrafts(); $('drafts-heading').setAttribute('tabindex', '-1'); $('drafts-heading').focus(); }
        });
        $('draft-new').addEventListener('click', async function () {
            await flushDraft();
            startBlank(true);
            sayDraft(t('New form started. The earlier draft is kept under Drafts.'));
            focusFormTop();
        });
        $('draft-discard').addEventListener('click', async function () {
            if (!draftId || !window.confirm(t('Discard this draft? It cannot be recovered.'))) { return; }
            await discardDraftRecord(draftId);
            startBlank(true);
            sayDraft(t('Draft discarded.'));
            focusFormTop();
        });
        document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'hidden') { flushDraft(); } });
        window.addEventListener('pagehide', function () { flushDraft(); });
    }

    /** The page's own controls — Next, Back, Submit, the language — wired once, whatever form they then serve. */
    function wireForm() {
        $('form').addEventListener('submit', submit);
        $('new-form').addEventListener('click', function () { startBlank(); });
        $('next').addEventListener('click', function () {
            showAll = false;
            var found = pageProblems();
            if (found.length) {
                found.forEach(function (p) { p.inst.touched[p.q.name] = true; });
                update();
                showSummary(found);
                focusProblem(found[0]);
                return;
            }
            var shown = relevantPages();
            var at = shown.indexOf(pageIndex);
            if (at < shown.length - 1) { goToPage(shown[at + 1]); }
        });
        $('back').addEventListener('click', function () {
            var shown = relevantPages();
            var at = shown.indexOf(pageIndex);
            if (at > 0) { goToPage(shown[at - 1]); }
        });
        $('language').addEventListener('change', function () {
            lang = $('language').value;
            document.documentElement.lang = (/\(([a-z]{2,3})\)/i.exec(lang) || [])[1] || document.documentElement.lang;
            applyStaticText();
            render();
            if (!previewing) { drawOutbox(); drawEditingBar(); }
        });
    }

    /** Draws a loaded form: its lists, languages and title, then a blank record. Called again for each preview. */
    async function begin(loaded) {
        loadedFrom = loaded.from;
        doc = loaded.doc;
        form = doc.form;
        defs = {};
        index(form.root, []);

        var csvNames = csvNamesOf(form);
        for (var i = 0; i < csvNames.length; i++) {
            if (files[csvNames[i]]) { continue; }
            var body = loaded.from !== 'device' && itemId ? await fetchCsv(csvNames[i]) : null;
            if (body === null && loaded.from !== 'preview' && itemId) {
                var copy = await dbGet('forms', itemId).catch(function () { return null; });
                if (copy && copy.files && copy.files[csvNames[i]] !== undefined) { body = copy.files[csvNames[i]]; }
            }
            if (body === null) { missingFiles.push(csvNames[i]); } else { files[csvNames[i]] = { text: body, rows: parseCsv(body) }; }
        }

        mediaNamesOf(form).forEach(function (n) { loadMedia(n); });
        await loadScripts(loaded);

        var languages = Array.isArray(form.languages) ? form.languages.filter(function (l) { return l !== null && l !== undefined; }) : [];
        var keep = languages.indexOf(lang) >= 0 && loaded.from === 'preview';
        lang = keep ? lang : form.defaultLanguage && languages.indexOf(form.defaultLanguage) >= 0 ? form.defaultLanguage : (languages[0] || '');
        var select = $('language');
        select.textContent = '';
        if (languages.length > 1) {
            languages.forEach(function (l) { select.appendChild(el('option', { value: l, text: l || 'Default' })); });
            select.value = lang;
        }
        $('language-wrap').hidden = languages.length <= 1;
        var langCode = (/\(([a-z]{2,3})\)/i.exec(lang) || [])[1];
        if (langCode) { document.documentElement.lang = langCode; } else if (!namesLanguage(lang) && browserIsTurkish()) { document.documentElement.lang = 'tr'; }
        applyStaticText();

        var title = doc.title || form.title || 'Form';
        document.title = title;
        $('form-title').textContent = title;
        $('loading').hidden = true;
        if (loaded.from !== 'preview') { $('drafts-toggle').hidden = false; }
        if (loaded.from === 'device') {
            $('offline-panel').hidden = false;
            $('offline-status').textContent = t('Opened from this device; the server could not be reached.');
        } else if (loaded.from === 'server') {
            $('offline-panel').hidden = false;
            // A form already downloaded is refreshed with what the server serves now.
            var stored = await dbGet('forms', itemId).catch(function () { return null; });
            if (stored) { downloadForOffline(); }
        }
        // A preview, or a form not yet published: said at the top from the start, and Submit is off, rather than a
        // refusal after the form is filled.
        if (!doc.publication) {
            var notice = $('unpublished');
            notice.textContent = loaded.from === 'preview'
                ? t('A preview of the form as it is being designed. Answers are not sent.')
                : t('This form is not published yet. You can look at it, but answers cannot be sent.');
            notice.hidden = false;
            $('submit').disabled = true;
            $('submit').setAttribute('aria-describedby', 'unpublished');
        }

        await openInitial();
        drawLinkNotices();
        if (loaded.from !== 'preview') { await startInbox(); }
    }

    /**
     * The preview: no worker, no outbox, nothing sent. It says it is ready to its parent and draws each form the parent
     * posts — from this origin only, and only from the window that framed it — keeping the language it was shown in.
     */
    function startPreview() {
        document.documentElement.classList.add('previewing');
        ['outbox-toggle', 'drafts-toggle', 'inbox-toggle', 'draft-bar', 'connection', 'offline-panel'].forEach(function (id) { var n = $(id); if (n) { n.hidden = true; } });
        wireForm();
        var drawing = Promise.resolve();
        window.addEventListener('message', function (event) {
            if (event.origin !== location.origin || event.source !== window.parent) { return; }
            var sent = event.data || {};
            if (sent.type !== 'graticula-form-preview' || !sent.form) { return; }
            drawing = drawing.then(function () {
                files = {};
                missingFiles = [];
                resetScripts();
                itemId = String(sent.itemId || '').toLowerCase();
                $('summary').hidden = true;
                $('done').hidden = true;
                return begin({ doc: { id: itemId, title: sent.title, form: sent.form, publication: null }, from: 'preview' });
            }).then(function () {
                readyResolve();
                window.parent.postMessage({ type: 'graticula-form-preview-drawn' }, location.origin);
            }).catch(function (e) {
                $('loading').hidden = false;
                $('loading').textContent = 'The preview could not be drawn: ' + (e && e.message ? e.message : e);
            });
        });
        window.parent.postMessage({ type: 'graticula-form-preview-ready' }, location.origin);
    }

    var readyResolve;
    var ready = new Promise(function (r) { readyResolve = r; });

    window.GraticulaFormFill = Object.freeze({
        /** Resolves when the form is drawn. */
        ready: ready,
        /** The records waiting on this device, oldest first. */
        outbox: function () { return dbAll('outbox').then(function (r) { return r.sort(function (a, b) { return a.key - b.key; }).map(function (x) { return { key: x.key, status: x.status, message: x.message, done: x.progress && x.progress.done }; }); }); },
        /** Sends what is waiting now. */
        sendNow: function () { exchanged = false; return sendOutbox(); },
        /** The maps drawn now: the ground each stands on and where its view is, as [longitude, latitude, zoom]. */
        maps: function () {
            return liveMaps.filter(function (m) { return document.body.contains(m.box); }).map(function (m) {
                var c = ol.proj.toLonLat(m.map.getView().getCenter());
                return { ground: m.box.getAttribute('data-ground'), view: [c[0], c[1], m.map.getView().getZoom()] };
            });
        },
        /** The CSV lists the form names that the item does not hold. */
        missingFiles: function () { return missingFiles.slice(); },
        /** The scripts the form names that the item does not hold (section 5.27). */
        missingScripts: function () { return missingScripts.slice(); }
    });

    if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', start); } else { start(); }
})();
