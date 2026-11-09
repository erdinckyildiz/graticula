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
    // The device's storage: IndexedDB, two stores. 'forms' holds a downloaded form; 'outbox' holds records waiting to
    // be sent, each with its files as Blobs. Nothing is removed from the outbox until the server accepted all of it.
    // ---------------------------------------------------------------------------------------------------------------

    var dbPromise = null;

    function openDb() {
        if (dbPromise) { return dbPromise; }
        dbPromise = new Promise(function (resolve) {
            try {
                if (!window.indexedDB) { resolve(null); return; }
                var request = indexedDB.open('graticula-forms', 1);
                request.onupgradeneeded = function () {
                    var db = request.result;
                    if (!db.objectStoreNames.contains('forms')) { db.createObjectStore('forms', { keyPath: 'id' }); }
                    if (!db.objectStoreNames.contains('outbox')) { db.createObjectStore('outbox', { keyPath: 'key', autoIncrement: true }); }
                };
                request.onsuccess = function () { resolve(request.result); };
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
        "Opened from this device; the server could not be reached.": "Bu cihazdan açıldı; sunucuya ulaşılamadı.",
        "The form could not be loaded: you are offline and it has not been downloaded to this device.": "Form yüklenemedi: çevrimdışısınız ve form bu cihaza indirilmemiş.",
        "Discard this record? It has not been accepted by the server and cannot be recovered.": "Bu kayıt silinsin mi? Sunucu henüz kabul etmedi ve geri getirilemez."
    };
    var STATIC_TEXT = {"back": ["Back", "Geri"], "next": ["Next", "İleri"], "submit": ["Submit", "Gönder"], "new-form": ["Fill in a new form", "Yeni form doldur"], "send-now": ["Send now", "Şimdi gönder"], "download-offline": ["Download for offline use", "Çevrimdışı kullanım için indir"], "outbox-heading": ["Outbox", "Giden kutusu"], "done-heading": ["Submitted", "Gönderildi"], "ios-note": ["Offline on iPhone is not yet verified. Install this page to the Home Screen before relying on it without signal.", "iPhone’da çevrimdışı çalışma henüz doğrulanmadı. Sinyalsiz kullanmadan önce bu sayfayı Ana Ekran’a ekleyin."]};
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
        var toggle = document.getElementById('outbox-toggle');
        if (toggle && toggle.firstChild && toggle.firstChild.nodeType === 3) { toggle.firstChild.nodeValue = isTurkish() ? 'Giden kutusu ' : 'Outbox '; }
    }
    var me = { name: '', email: '' };
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
                Object.keys(c.labels || {}).forEach(function (k) { row[k === '' ? 'label' : 'label::' + k] = c.labels[k]; });
                Object.keys(c.filters || {}).forEach(function (k) { row[k] = c.filters[k]; });
                return row;
            });
        }
        if (files[listName]) { return files[listName].rows; }
        return null;
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
            pulldata: pulldata
        };
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
                if (r && n.calculation) {
                    var v = evalString(n.calculation, inst, n);
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

    function pages() {
        var out = [], loose = null;
        (root.node.body || []).forEach(function (n) {
            if (n.kind === 'group') { loose = null; out.push([n]); return; }
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

    function renderGroup(inst, g, container, level) {
        var label = text(g.labels);
        var box = el('section', { class: 'group', 'data-key': keyOf(inst, g.name) });
        if (label) { box.appendChild(heading(level, label, { class: form.pages && !inst.parent ? 'page-title' : null })); }
        container.appendChild(box);
        renderBody(inst, g.body || [], box, level + 1);
        views.push({ update: function () { box.hidden = inst.rel[g.name] === false; } });
    }

    function renderRepeat(inst, r, container, level) {
        var label = text(r.labels, r.name);
        var box = el('section', { class: 'repeat', 'data-key': keyOf(inst, r.name) });
        box.appendChild(heading(level, label));
        container.appendChild(box);
        var list = inst.repeats[r.name] || [];
        list.forEach(function (child, i) {
            var item = el('div', { class: 'repeat-instance', 'data-key': keyOf(child, '') });
            var head = el('div', { class: 'instance-head' }, [heading(level + 1, label + ' ' + (i + 1))]);
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
                    var add = document.querySelector('[data-key="' + cssEscape(keyOf(inst, r.name)) + '"] [data-focus="add"]');
                    if (add) { add.focus(); }
                });
                head.appendChild(remove);
            }
            item.appendChild(head);
            renderBody(child, r.body || [], item, level + 2);
            box.appendChild(item);
        });
        if (!r.repeatCount) {
            var add = el('button', { type: 'button', class: 'quiet', 'data-focus': 'add', text: isTurkish() ? label + ' ekle' : 'Add ' + label });
            add.addEventListener('click', function () {
                list.push(newInstance(r, inst));
                refresh(true);
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
    }

    function renderQuestion(inst, q, container) {
        var id = 'q' + (++seq);
        var key = keyOf(inst, q.name);
        var wrap = el('div', { class: 'question type-' + q.type, 'data-key': key, 'data-name': q.name });
        var labelTemplate = text(q.labels, q.name);
        var hintText = text(q.hints);
        var group = q.type === 'select_one' || q.type === 'select_multiple' || q.type === 'select_one_from_file'
            || q.type === 'select_multiple_from_file' || (q.type === 'range' && has(q.appearance, 'rating'));
        var minimal = has(q.appearance, 'minimal');
        var useFieldset = group && !minimal;

        var labelText = el('span', { text: interpolate(labelTemplate, inst) });
        var star = el('span', { class: 'required-mark', 'aria-hidden': 'true', text: '*', hidden: true });
        var labelNode = useFieldset || q.type === 'note' || q.type === 'geopoint' || q.type === 'geotrace' || q.type === 'geoshape'
            || isMedia(q)
            ? el(useFieldset ? 'legend' : 'div', { class: 'label', id: id + '-label' }, [labelText, star])
            : el('label', { for: id, id: id + '-label' }, [labelText, star]);
        var hint = hintText ? el('p', { class: 'hint', id: id + '-hint', text: hintText }) : null;
        // No live region per question: the summary is the one alert, and two announcements per error was noise (UX review).
        var error = el('p', { class: 'error', id: id + '-error' });
        var describedBy = (hint ? id + '-hint ' : '') + id + '-error';

        var shell = useFieldset ? el('fieldset', { 'aria-describedby': describedBy }) : wrap;
        if (useFieldset) { wrap.appendChild(shell); }
        shell.appendChild(labelNode);
        if (hint) { shell.appendChild(hint); }

        var widget = buildWidget(inst, q, id, describedBy);
        shell.appendChild(widget.node);
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
                return q.type === 'image' && has(q.appearance, 'signature') ? signatureWidget(inst, q, id) : mediaWidget(inst, q, id, describedBy);
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
        var multi = has(q.appearance, 'multiline');
        var input = multi ? el('textarea', { id: id, 'aria-describedby': describedBy, 'data-focus': 'input' })
            : el('input', { type: 'text', id: id, 'aria-describedby': describedBy, 'data-focus': 'input' });
        input.value = current(inst, q);
        input.addEventListener('input', function () { setValue(inst, q, input.value); });
        return { node: input, controls: function () { return [input]; }, sync: function (ro) { syncInput(input, current(inst, q), ro); } };
    }

    function numberWidget(inst, q, id, describedBy) {
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
            return rows.filter(function (row) { return names.indexOf(String(row.name)) >= 0; });
        } catch (e) {
            warn(q.choiceFilter, e);
            return rows;
        }
    }

    function selectWidget(inst, q, id, describedBy) {
        var multiple = q.type.indexOf('select_multiple') === 0;
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
                box.appendChild(el('label', { class: 'choice', for: id + '-' + i }, [input, el('span', { text: choiceLabel(row) })]));
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
                controls.forEach(function (c) {
                    if (c.tagName === 'SELECT') {
                        Array.prototype.forEach.call(c.options, function (o) { o.selected = o.value === '' ? set.length === 0 && !multiple : set.indexOf(o.value) >= 0; });
                    } else {
                        c.checked = set.indexOf(c.value) >= 0;
                    }
                    c.disabled = !!ro;
                });
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
                status.textContent = isTurkish() ? 'Konum bulundu, ' + Math.round(c.accuracy) + ' m doğrulukta.' : 'Location found, accurate to ' + Math.round(c.accuracy) + ' m.';
            }, function (e) { status.textContent = e.message + ' You can type the coordinates instead.'; })
                .then(function () { use.disabled = false; });
        });

        var node = el('div', null, [
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
                if (p.length >= 4 && Number(p[3]) > 0 && !status.textContent) { status.textContent = isTurkish() ? Math.round(Number(p[3])) + ' m doğrulukta.' : 'Accurate to ' + Math.round(Number(p[3])) + ' m.'; }
            }
        };
    }

    function vertices(v) {
        return String(v || '').split(';').map(function (s) { return s.trim(); }).filter(Boolean)
            .map(function (s) { return s.split(/\s+/).map(Number); })
            .filter(function (p) { return p.length >= 2 && !isNaN(p[0]) && !isNaN(p[1]); });
    }

    function verticesWidget(inst, q, id) {
        var list = el('ol', { class: 'vertices', 'aria-label': 'Points' });
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
                status.textContent = 'Point ' + pts.length + ' added, accurate to ' + Math.round(p.coords.accuracy) + ' m.';
            }, function (e) { status.textContent = e.message; });
        });
        addTyped.addEventListener('click', function () {
            var a = Number(lat.value.trim().replace(',', '.')), b = Number(lon.value.trim().replace(',', '.'));
            if (!lat.value.trim() || !lon.value.trim() || isNaN(a) || isNaN(b) || Math.abs(a) > 90 || Math.abs(b) > 180) {
                status.textContent = t('Type a latitude between -90 and 90 and a longitude between -180 and 180.');
                return;
            }
            var pts = points(); pts.push([a, b, 0, 0]); store(pts);
            lat.value = ''; lon.value = '';
            status.textContent = 'Point ' + pts.length + ' added.';
            lat.focus();
        });
        undo.addEventListener('click', function () { var pts = points(); pts.pop(); store(pts); status.textContent = pts.length + ' points.'; });

        var node = el('div', null, [
            el('div', { class: 'inline' }, [here, undo]),
            el('div', { class: 'pair' }, [
                el('label', { for: id + '-lat' }, [t('Latitude'), lat]),
                el('label', { for: id + '-lon' }, [t('Longitude'), lon])
            ]),
            el('div', { class: 'inline' }, [addTyped]),
            list, status
        ]);
        return {
            node: node,
            controls: function () { return [lat, lon]; },
            sync: function (ro) {
                var pts = points();
                list.textContent = '';
                pts.forEach(function (p) { list.appendChild(el('li', { text: p[0] + ', ' + p[1] })); });
                here.disabled = undo.disabled = addTyped.disabled = !!ro;
                undo.disabled = undo.disabled || !pts.length;
                lat.readOnly = lon.readOnly = !!ro;
            }
        };
    }

    // ----- Files.

    function setFiles(inst, q, list) {
        inst.files[q.name] = list;
        inst.values[q.name] = list.map(function (f) { return f.name; }).join(' ');
        inst.touched[q.name] = true;
        refresh(false);
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

    function signatureWidget(inst, q, id) {
        var canvas = el('canvas', { tabindex: '0', role: 'img', 'aria-labelledby': id + '-label', 'data-focus': 'pad' });
        var clear = el('button', { type: 'button', class: 'quiet', 'data-focus': 'clear', text: t('Clear signature') });
        var status = el('p', { class: 'hint', 'aria-live': 'polite' });
        var drawing = false, dirty = false, last = null, ctx = null;

        function setup() {
            var ratio = window.devicePixelRatio || 1;
            var w = canvas.clientWidth || 320, h = canvas.clientHeight || 180;
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
                status.textContent = t('Signature captured.');
            }, 'image/png');
        }
        canvas.addEventListener('pointerup', endStroke);
        canvas.addEventListener('pointercancel', endStroke);
        clear.addEventListener('click', function () { dirty = false; setFiles(inst, q, []); setup(); status.textContent = t('Signature cleared.'); });
        setTimeout(setup, 0);
        return {
            node: el('div', { class: 'signature' }, [canvas, el('div', { class: 'inline' }, [clear]), status]),
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
                        (inst.repeats[n.name] || []).forEach(function (child) { emit(child, n.name, inst); });
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
            var added = await api((att.submit || att.url) + '/' + oid + '/addAttachment', { method: 'POST', body: data });
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
                    finish('Your answers were sent.');
                } catch (err) {
                    showMessage(err.network ? t('The server could not be reached, and this browser cannot keep the record for later. Try again when you are online.') : 'The server refused the answers: ' + err.message);
                }
                return;
            }
            record.key = key;
            if (!navigator.onLine) {
                finish('Saved on this device. It will be sent when you are back online.', true);
                await drawOutbox();
                return;
            }
            var outcome = await sendOne(record);
            await drawOutbox();
            if (outcome === 'sent') {
                finish('Your answers were sent.');
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
        summary.appendChild(el('p', { text: message }));
        summary.hidden = false;
        summary.focus();
    }

    function finish(message, queued) {
        $('form').hidden = true;
        $('summary').hidden = true;
        $('done-heading').textContent = queued ? t('Saved on this device') : 'Submitted';
        $('done-text').textContent = message;
        $('done').hidden = false;
        $('done').focus();
    }

    function startBlank() {
        showAll = false;
        pageIndex = 0;
        root = newInstance(form.root, null);
        $('done').hidden = true;
        $('summary').hidden = true;
        $('form').hidden = false;
        refresh(true);
        var first = $('body').querySelector('input, select, textarea');
        if (first) { first.focus(); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Offline: the service worker, the downloaded form, the connection banner.
    // ---------------------------------------------------------------------------------------------------------------

    function drawConnection() {
        $('connection').textContent = navigator.onLine ? ''
            : t('You are offline. Records you submit are kept on this device and sent when the connection returns.');
    }

    async function downloadForOffline() {
        var status = $('offline-status');
        status.textContent = t('Saving the form on this device…');
        try {
            var csv = {};
            Object.keys(files).forEach(function (k) { csv[k] = files[k].text; });
            await dbPut('forms', { id: itemId, doc: doc, files: csv, saved: Date.now() });
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
                } catch (e) { /* no email */ }
            }
        } catch (e) { /* anonymous or offline */ }
    }

    async function start() {
        X = window.GraticulaXlsform;
        var params = new URLSearchParams(location.search);
        var fromPath = /\/forms\/([0-9a-f]{32})\/?$/i.exec(location.pathname);
        itemId = (params.get('id') || (fromPath && fromPath[1]) || '').toLowerCase();

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
        $('download-offline').addEventListener('click', downloadForOffline);
        $('form').addEventListener('submit', submit);
        $('new-form').addEventListener('click', startBlank);
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
            drawOutbox();
        });
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
        doc = loaded.doc;
        form = doc.form;
        defs = {};
        index(form.root, []);

        var csvNames = csvNamesOf(form);
        for (var i = 0; i < csvNames.length; i++) {
            if (files[csvNames[i]]) { continue; }
            var body = loaded.from === 'server' ? await fetchCsv(csvNames[i]) : null;
            if (body === null) { missingFiles.push(csvNames[i]); } else { files[csvNames[i]] = { text: body, rows: parseCsv(body) }; }
        }

        var languages = Array.isArray(form.languages) ? form.languages.filter(function (l) { return l !== null && l !== undefined; }) : [];
        lang = form.defaultLanguage && languages.indexOf(form.defaultLanguage) >= 0 ? form.defaultLanguage : (languages[0] || '');
        if (languages.length > 1) {
            var select = $('language');
            languages.forEach(function (l) { select.appendChild(el('option', { value: l, text: l || 'Default' })); });
            select.value = lang;
            $('language-wrap').hidden = false;
        }
        var langCode = (/\(([a-z]{2,3})\)/i.exec(lang) || [])[1];
        if (langCode) { document.documentElement.lang = langCode; }
        applyStaticText();

        var title = doc.title || form.title || 'Form';
        document.title = title;
        $('form-title').textContent = title;
        $('loading').hidden = true;
        $('offline-panel').hidden = false;
        if (loaded.from === 'device') {
            $('offline-status').textContent = t('Opened from this device; the server could not be reached.');
        } else {
            // A form already downloaded is refreshed with what the server serves now.
            var kept = await dbGet('forms', itemId).catch(function () { return null; });
            if (kept) { downloadForOffline(); }
        }
        if (!doc.publication) {
            showMessage('This form is not published yet. You can look at it, but answers cannot be sent.');
        }

        startBlank();
        if (navigator.onLine) { sendOutbox(); }
        readyResolve();
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
        /** The CSV lists the form names that the item does not hold. */
        missingFiles: function () { return missingFiles.slice(); }
    });

    if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', start); } else { start(); }
})();
