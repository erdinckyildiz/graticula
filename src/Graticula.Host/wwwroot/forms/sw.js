/*
  The form client's service worker — ADR-287 §5.5. Scope /forms/.

  What it keeps: the page, its scripts and stylesheet (the shell), and the form.json of every form opened while online.
  The shell is served from the cache and refreshed behind it, so the page opens with no signal and picks up a new
  release on the next open. A form's JSON is asked of the server first and the copy is used only when the server
  cannot be reached, so a form edited by its publisher is never shown stale to someone who is online.

  What it does not touch: anything that is not a GET, and anything outside /forms/ — the FeatureServer writes, the
  sign-in, the outbox's sends. Those go to the network as they are; the outbox, not this worker, is what holds a
  record that could not be sent.
*/
'use strict';

var SHELL_CACHE = 'graticula-forms-shell-v4';
var FORMS_CACHE = 'graticula-forms-data-v1';
var SHELL = ['fill.html', 'fill.js', 'fill.css', 'xlsform-eval.js', 'manifest.webmanifest', 'icon.svg', 'ol.js', 'ol.css'];

self.addEventListener('install', function (event) {
    event.waitUntil(caches.open(SHELL_CACHE).then(function (cache) { return cache.addAll(SHELL); }).then(function () { return self.skipWaiting(); }));
});

self.addEventListener('activate', function (event) {
    event.waitUntil(caches.keys().then(function (keys) {
        return Promise.all(keys.filter(function (k) { return k.indexOf('graticula-forms-') === 0 && k !== SHELL_CACHE && k !== FORMS_CACHE; })
            .map(function (k) { return caches.delete(k); }));
    }).then(function () { return self.clients.claim(); }));
});

function scopePath() { return new URL(self.registration.scope).pathname; }

self.addEventListener('fetch', function (event) {
    var request = event.request;
    if (request.method !== 'GET') { return; }
    var url = new URL(request.url);
    if (url.origin !== self.location.origin || url.pathname.indexOf(scopePath()) !== 0) { return; }

    // A form's JSON: the network first, the kept copy when there is no network.
    if (/\/forms\/[0-9a-f]{32}\/form\.json$/i.test(url.pathname)) {
        event.respondWith(fetch(request).then(function (response) {
            if (response.ok) {
                var copy = response.clone();
                caches.open(FORMS_CACHE).then(function (cache) { cache.put(url.pathname, copy); });
            }
            return response;
        }).catch(function () {
            return caches.open(FORMS_CACHE).then(function (cache) { return cache.match(url.pathname); }).then(function (hit) {
                return hit || new Response(JSON.stringify({ error: { code: 503, message: 'Offline, and this form was not kept on the device.' } }),
                    { status: 503, headers: { 'Content-Type': 'application/json' } });
            });
        }));
        return;
    }

    // The page itself, at fill.html?id=… or /forms/{id}: the shell's page, whatever the query.
    var name = url.pathname.slice(scopePath().length);
    var isPage = request.mode === 'navigate' && (name === '' || name === 'fill.html' || /^[0-9a-f]{32}\/?$/i.test(name));
    var key = isPage ? 'fill.html' : (SHELL.indexOf(name) >= 0 ? name : null);
    if (!key) { return; }

    event.respondWith(caches.open(SHELL_CACHE).then(function (cache) {
        return cache.match(key).then(function (hit) {
            var refreshed = fetch(isPage ? key : request).then(function (response) {
                if (response.ok) { cache.put(key, response.clone()); }
                return response;
            });
            if (hit) {
                event.waitUntil(refreshed.catch(function () { /* offline: the copy stands */ }));
                return hit;
            }
            return refreshed;
        });
    }));
});
