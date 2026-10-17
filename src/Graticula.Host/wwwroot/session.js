/*
  <b>An address from before 2026-10-17 is turned into today's, first.</b> The screen was the hash then —
  `/studio/#/content` — and is the path now — `/studio/content`. A bookmark, a link in a mail or a test that opens the
  old shape lands on the new one in place, without a second load and without an entry in the history.
*/
(() => {
  const surface = /^\/(studio|server)(\/|$)/.exec(location.pathname);
  if (surface && location.hash.startsWith("#/")) {
    history.replaceState(null, "", `/${surface[1]}/${location.hash.slice(2)}`);
  }
})();

/*
  <b>Which of the two screens this page is, decided before anything is painted.</b>

  <b>The bug this fixes, reported by the owner:</b> *"server'dan studio ya geçerken 1 sn
  liğine password ekranı gelip kapanıyor."* Switching surface is a whole-page navigation —
  Server is `/server/` and Studio is `/studio/`, which is ADR-034 §5c's design and not an
  accident — so every switch is a cold load. The sign-in panel had no initial `display`, so
  the browser painted it the moment the HTML arrived, and only once `console.js` had loaded
  and run did `start()` hide it again.

  <b>It is not the network, and that is why the fix cannot live in `start()`.</b> Measured:
  `/rest/whoami` answers in 6 to 15 ms. `console.js` is 454 KB and loaded `defer`, so it runs
  after the document is parsed — the wrong screen is on display for as long as half a
  megabyte of JavaScript takes to arrive and compile. Reproduced by delaying `console.js` by
  800 ms: the sign-in screen stayed for the whole 824 ms with a perfectly valid token in
  storage.

  <b>Its own file, and the first attempt put it inline in the head instead.</b> The console's
  Content-Security-Policy is `script-src 'self' https://js.arcgis.com` with no
  `'unsafe-inline'`, so the browser refused it silently on every load — and
  `No_console_page_carries_an_inline_script` refused it too, a test this repository already
  had from D-44, whose failure message says exactly what to do: *move the code to a file
  beside the page.* On the development machine the flash looked cured because `console.js`
  loads in about 20 ms there; that was the machine, not the fix.

  <b>Loaded without `defer` on purpose.</b> This is a few hundred bytes and it must run before
  the body is painted; `defer` would put it back after parsing, which is the whole problem.
  It is the one script here that blocks, and it blocks for one small same-origin file.

  <b>The token is already there — passing it in a URL would be a regression, not a
  shortcut.</b> `sessionStorage` is per origin rather than per path, so the token survives the
  navigation between surfaces untouched. An authorization token in a query string is D-120, it
  is what `QueryRedaction` exists to strip out of the logs, and since ADR-045 it would be
  written to a table with an index on it.

  <b>This is a claim about storage, not about identity.</b> A token that has expired still
  shows the console for a moment before `start()`'s `whoami` corrects it — which is the right
  way round: the common case is a valid session, and a brief console before a sign-in is a
  smaller lie than a sign-in screen in front of an operator who is signed in.

  <b>One name, three places.</b> `start()` writes this attribute in both directions once
  `whoami` has answered, and signing out clears it; searching for `data-session` finds all of
  them.
*/
try {
  document.documentElement.dataset.session =
    sessionStorage.getItem("gis-token") ? "held" : "none";
} catch (ignored) {
  // Private mode, or storage disabled. Sign-in is the honest default.
  document.documentElement.dataset.session = "none";
}

/*
  <b>And which theme, decided before anything is painted too — ADR-187 step 4.</b> The reader chooses dark
  with the header's button and `console.js` remembers it in `localStorage`; setting it here rather than there
  keeps a dark reader from seeing one light frame on every load.
*/
try {
  if (localStorage.getItem("gis-theme") === "dark") document.documentElement.dataset.theme = "dark";
} catch (ignored) {
  // No storage, no remembered choice: the light theme is the default.
}
