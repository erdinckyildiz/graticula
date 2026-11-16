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
    // The device's storage: IndexedDB, three stores. 'forms' holds a downloaded form; 'outbox' holds records waiting to
    // be sent, each with its files as Blobs; 'drafts' holds forms being filled in (§5.10, ADR-287: the client always
    // keeps drafts), their files as Blobs too. Nothing is removed from the outbox until the server accepted all of it.
    // ---------------------------------------------------------------------------------------------------------------

    var dbPromise = null;

    function openDb() {
        if (dbPromise) { return dbPromise; }
        dbPromise = new Promise(function (resolve) {
            try {
                if (!window.indexedDB) { resolve(null); return; }
                var request = indexedDB.open('graticula-forms', 2);
                request.onupgradeneeded = function () {
                    var db = request.result;
                    if (!db.objectStoreNames.contains('forms')) { db.createObjectStore('forms', { keyPath: 'id' }); }
                    if (!db.objectStoreNames.contains('outbox')) { db.createObjectStore('outbox', { keyPath: 'key', autoIncrement: true }); }
                    if (!db.objectStoreNames.contains('drafts')) { db.createObjectStore('drafts', { keyPath: 'id' }); }
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
        "Picture not available: ": "Resim bulunamadı: "
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
        "Type to narrow the choices.": "Seçenekleri daraltmak için yazın.",
        "selected": "seçildi",
        "Only the first 100 are listed; keep typing to narrow them.": "Yalnız ilk 100 seçenek listeleniyor; daraltmak için yazmaya devam edin.",
        "Choices": "Seçenekler"
    });
    function isTurkish() { return /\(tr\)|türkçe|turkish/i.test(lang || ''); }
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
        return { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', svg: 'image/svg+xml' }[ext] || 'application/octet-stream';
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
            (node.body || []).forEach(function (n) { if (n.kind === 'question') { add(n.images); } else { walk(n); } });
        })(formIn.root);
        Object.keys(formIn.choices || {}).forEach(function (l) { formIn.choices[l].forEach(function (c) { add(c.images); }); });
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
            property: propertyOf
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
            case 'mode': return 'new';          // there is no editing of a sent record in this client yet
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
                    while (list.length > count) { list.pop(); changed = true; structureChanged = true; }
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
        if (isMedia(q)) { return filesOf(inst, q.name).length === 0; }
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
                if (hasUi(n)) { renderQuestion(inst, n, container); }
            } else if (n.kind === 'group') {
                renderGroup(inst, n, container, level);
            } else if (n.kind === 'repeat') {
                renderRepeat(inst, n, container, level);
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
                    var readOnly = evalBool(q.readOnly, inst, q, false);
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
            if (!r.repeatCount) {
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
        if (!r.repeatCount) {
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
        var minimal = has(q.appearance, 'minimal') || (has(q.appearance, 'autocomplete') && (q.type === 'select_one' || q.type === 'select_one_from_file'));
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
        var guidance = guidanceNodes(q, id, key);
        // No live region per question: the summary is the one alert, and two announcements per error was noise (UX review).
        var error = el('p', { class: 'error', id: id + '-error' });
        var describedBy = (hint ? id + '-hint ' : '') + id + '-error';

        var shell = useFieldset ? el('fieldset', { 'aria-describedby': describedBy }) : wrap;
        if (useFieldset) { wrap.appendChild(shell); }
        shell.appendChild(labelNode);
        if (pictureBlock) { shell.appendChild(pictureBlock); }
        if (hint) { shell.appendChild(hint); }
        if (guidance) { shell.appendChild(guidance.button); shell.appendChild(guidance.panel); }

        var widget = buildWidget(inst, q, id, describedBy);
        shell.appendChild(widget.node);
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
        wrap.appendChild(error);
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
                if (widget.sync) { widget.sync(readOnly, required); }
                var message = (showAll || inst.touched[q.name]) ? messageFor(inst, q) : '';
                error.textContent = message;
                wrap.classList.toggle('invalid', !!message);
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
            case 'date': return dateWidget(inst, q, id, describedBy);
            case 'time': return timeWidget(inst, q, id, describedBy);
            case 'dateTime': return dateTimeWidget(inst, q, id, describedBy);
            case 'geopoint': return geopointWidget(inst, q, id);
            case 'geotrace': case 'geoshape': return verticesWidget(inst, q, id);
            case 'image': case 'audio': case 'video': case 'file':
                if (q.type === 'image' && has(q.appearance, 'signature')) { return signatureWidget(inst, q, id, false); }
                if (q.type === 'image' && has(q.appearance, 'draw')) { return signatureWidget(inst, q, id, true); }
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

    function numberWidget(inst, q, id, describedBy) {
        if (has(q.appearance, 'spinner')) { return spinnerWidget(inst, q, id, describedBy); }
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
        var rows = choiceRows(listName);
        if (!rows) { return null; }
        if (!q.choiceFilter) { return rows; }
        try {
            var result = X.evaluate("instance('" + String(listName).replace(/'/g, '') + "')/root/item[" + q.choiceFilter + ']/name', contextFor(inst, q));
            var names = result && Array.isArray(result.nodes) ? result.nodes.map(function (n) { return String(n.value); }) : [];
            return rows.filter(function (row) { return row.__auto || names.indexOf(String(row.name)) >= 0; });
        } catch (e) {
            warn(q.choiceFilter, e);
            return rows;
        }
    }

    function selectWidget(inst, q, id, describedBy) {
        var multiple = q.type.indexOf('select_multiple') === 0;
        if (has(q.appearance, 'autocomplete') && !multiple) { return autocompleteWidget(inst, q, id, describedBy); }
        var minimal = has(q.appearance, 'minimal');
        var likert = has(q.appearance, 'likert') && !multiple;
        var holder = el('div', { class: likert ? 'likert' : null });
        var shown = null; // the names drawn, joined, to know when the filter changed them
        var controls = [];
        var unavailable = el('p', { class: 'note-warn', hidden: true });

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
                    unavailable.textContent = 'The list "' + (q.file || q.list) + '" is not available yet: it was not uploaded with the form, or could not be read.';
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
        var unavailable = el('p', { class: 'note-warn', hidden: true });
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
                    unavailable.textContent = 'The list "' + (q.file || q.list) + '" is not available yet: it was not uploaded with the form, or could not be read.';
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
        inst.values[q.name] = list.map(function (f) { return f.name; }).join(' ');
        inst.touched[q.name] = true;
        refresh(false);
        scheduleDraft();
    }

    async function shrink(file, maxPixels) {
        if (!maxPixels || !window.createImageBitmap || !/^image\//.test(file.type)) { return file; }
        try {
            var bitmap = await createImageBitmap(file);
            var pixels = bitmap.width * bitmap.height;
            if (pixels <= maxPixels) { return file; }
            var scale = Math.sqrt(maxPixels / pixels);
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
            capture: q.type === 'image' || q.type === 'video' ? 'environment' : q.type === 'audio' ? 'user' : null
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
                    : /^audio\//.test(f.type) ? el('audio', { src: url, controls: true })
                    : /^video\//.test(f.type) ? el('video', { src: url, controls: true, muted: true }) : null;
                var remove = el('button', { type: 'button', class: 'quiet', 'data-focus': 'remove-' + i, text: t('Remove') });
                remove.setAttribute('aria-label', 'Remove ' + f.name);
                remove.addEventListener('click', function () {
                    var now = filesOf(inst, q.name).slice(); now.splice(i, 1); setFiles(inst, q, now); input.focus();
                });
                list.appendChild(el('li', null, [preview, el('span', { text: f.name + ' (' + Math.ceil(f.size / 1024) + ' KB)' }), remove]));
            });
        }
        var drawnFor = null;
        return {
            node: el('div', null, [input, list]),
            controls: function () { return [input]; },
            sync: function (ro) {
                var now = filesOf(inst, q.name);
                if (drawnFor !== now) { drawnFor = now; draw(); }
                input.disabled = !!ro;
            }
        };
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

        function emit(inst, repeatName, parent) {
            var layer = layerFor(repeatName);
            if (!layer) { problemsFound.push('No layer for ' + (repeatName || 'the form') + ' in the publication.'); return; }
            var attributes = { globalid: inst.id };
            if (parent) { attributes.parentglobalid = parent.id; }
            var geometry = null;
            var columns = layer.columns || {};
            (function fill(body) {
                body.forEach(function (n) {
                    if (inst.rel[n.name] === false) { return; }
                    if (n.kind === 'group') { fill(n.body || []); return; }
                    if (n.kind !== 'question') { return; }
                    if (n.type === 'end') { inst.values[n.name] = localDateTime(now); }
                    var v = inst.values[n.name];
                    if (n.name === layer.location) { geometry = geometryOf(n, v); return; }
                    if (has(layer.attachments, n.name)) {
                        filesOf(inst, n.name).forEach(function (f) {
                            attachments.push({ url: layer.url, submit: layer.submitUrl || layer.url, globalid: inst.id, question: n.name, name: f.name, type: f.type, size: f.size, blob: f.blob });
                        });
                        return;
                    }
                    var column = columns[n.name];
                    if (!column) { return; }
                    var value = attributeValue(n, v);
                    if (value !== null && !(typeof value === 'number' && isNaN(value))) { attributes[column] = value; }
                });
            })(inst.node.body || []);
            var add = { attributes: attributes };
            if (geometry) { add.geometry = geometry; }
            (byLayer[layer.url] || (byLayer[layer.url] = { url: layer.url, submit: layer.submitUrl || layer.url, depth: depthOf(layer), adds: [] })).adds.push(add);

            (function children(body) {
                body.forEach(function (n) {
                    if (inst.rel[n.name] === false) { return; }
                    if (n.kind === 'group') { children(n.body || []); return; }
                    if (n.kind === 'repeat') {
                        (inst.repeats[n.name] || []).forEach(function (child) {
                            // Owner decision 2026-11-09: an item nobody answered anything in is not sent. A repeat starts
                            // with one item, and the first trial on dev sent an empty room row for it. A repeat_count
                            // asked for its items, so those are sent as they are.
                            if (!n.repeatCount && !answered(child)) { return; }
                            emit(child, n.name, inst);
                        });
                    }
                });
            })(inst.node.body || []);
        }

        emit(root, '', null);
        var layers = Object.keys(byLayer).map(function (k) { return byLayer[k]; }).sort(function (a, b) { return a.depth - b.depth; });
        var name = form.instanceName ? evalString(form.instanceName, root, null) : '';
        return {
            formId: itemId,
            formVersion: form.version || '',
            title: doc.title || form.title || '',
            instanceName: name,
            created: now.getTime(),
            status: 'queued',
            message: '',
            layers: layers,
            attachments: attachments,
            progress: { done: {}, attached: {}, attempted: {} },
            problems: problemsFound
        };
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
            await sendLayer(record.layers[li], p, save);
        }

        for (var ai = 0; ai < record.attachments.length; ai++) {
            var att = record.attachments[ai];
            var key = att.globalid + '/' + att.question + '/' + ai;
            if (p.attached[key]) { continue; }
            var oid = p.done[att.globalid];
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
            var added = await api((att.submit || att.url) + '/' + oid + '/addAttachment?parentGlobalId=' + encodeURIComponent(att.globalid), { method: 'POST', body: data });
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

    // 'sent' | 'offline' | 'sign-in' | 'refused'
    async function sendOne(record) {
        var save = function () { return dbPut('outbox', record); };
        record.retry = false;
        try {
            await sendRecord(record, save);
            await dbDelete('outbox', record.key);
            return 'sent';
        } catch (e) {
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
        toggle.setAttribute('aria-label', 'Outbox, ' + records.length + (records.length === 1 ? ' record waiting' : ' records waiting'));
        var list = $('outbox-list');
        list.textContent = '';
        if (!records.length) {
            list.appendChild(el('li', { class: 'state', text: t('Nothing is waiting to be sent.') }));
            return;
        }
        records.forEach(function (r) {
            var when = new Date(r.created);
            var title = (r.instanceName || r.title || 'Record') + ' — ' + when.toLocaleString();
            var state = r.status === 'refused' ? 'Refused by the server: ' + r.message
                : r.status === 'sign-in' ? t('Waiting: sign in to send it.')
                : r.status === 'queued' && r.message ? r.message : t('Waiting to be sent.');
            if (r.formId === itemId && form && r.formVersion !== (form.version || '')) { state += ' (filled in an earlier version of the form)'; }
            var item = el('li', { 'data-key': String(r.key) }, [
                el('strong', { text: title }),
                el('span', { class: 'state' + (r.status === 'refused' ? ' refused' : ''), text: state })
            ]);
            var actions = el('div', { class: 'row-actions' });
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
        return out;
    }

    // A draft is read back onto the form as it is now: an answer to a question the form no longer has is dropped, a
    // question it gained starts with its default, and the items of a repeat keep the GlobalIDs they were made with.
    function restoreInstance(node, parent, snap) {
        var inst = { id: snap.id || guid(), node: node, parent: parent, values: {}, files: {}, repeats: {}, rel: {}, touched: {}, relevant: true };
        initBody(inst, node.body || []);
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
                    lang: lang, pageIndex: pageIndex, tree: snapshot(target)
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
        root = restoreInstance(form.root, null, d.tree);
        draftId = d.id;
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
            case 'text': case 'hidden': return { value: v };
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
                finish('Your answers were sent.', false, true);
            } else if (outcome === 'offline') {
                finish('The server could not be reached. Your answers are saved on this device and will be sent when the connection returns.', true);
            } else if (outcome === 'sign-in') {
                finish('Saved on this device. Sign in, then send it from the Outbox.', true);
            } else if (Object.keys(record.progress.done).length === 0) {
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
        $('summary').hidden = true;
        var thanks = sent && form && form.thankYou ? form.thankYou : '';
        $('done-heading').textContent = queued ? t('Saved on this device') : STATIC_TEXT['done-heading'][isTurkish() ? 1 : 0];
        $('done-text').textContent = thanks || message;
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
        await dbPut('forms', { id: itemId, doc: doc, files: csv, media: media, saved: Date.now() });
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
        } catch (e) { /* anonymous or offline */ }
    }

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
            if (!previewing) { drawOutbox(); }
        });
    }

    /** Draws a loaded form: its lists, languages and title, then a blank record. Called again for each preview. */
    async function begin(loaded) {
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
        if (langCode) { document.documentElement.lang = langCode; }
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
    }

    /**
     * The preview: no worker, no outbox, nothing sent. It says it is ready to its parent and draws each form the parent
     * posts — from this origin only, and only from the window that framed it — keeping the language it was shown in.
     */
    function startPreview() {
        document.documentElement.classList.add('previewing');
        ['outbox-toggle', 'drafts-toggle', 'draft-bar', 'connection', 'offline-panel'].forEach(function (id) { var n = $(id); if (n) { n.hidden = true; } });
        wireForm();
        var drawing = Promise.resolve();
        window.addEventListener('message', function (event) {
            if (event.origin !== location.origin || event.source !== window.parent) { return; }
            var sent = event.data || {};
            if (sent.type !== 'graticula-form-preview' || !sent.form) { return; }
            drawing = drawing.then(function () {
                files = {};
                missingFiles = [];
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
        missingFiles: function () { return missingFiles.slice(); }
    });

    if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', start); } else { start(); }
})();
