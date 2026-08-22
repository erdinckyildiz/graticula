/*
  <b>What the studio tells the server about its own failures.</b>

  <b>Why this exists at all is a measurement, not a principle.</b> On 2026-08-22 this
  viewer had a bug where every click reset the map: `map.html`'s layout wrapper and
  `map.js`'s button both used `id="frame"`, so `getElementById` returned the wrapper and the
  frame-the-layer handler was bound to the whole page. Every request succeeded. Every status
  code was 200. **No server-side log could ever have shown it**, and it was found only
  because a person drove the page with a browser and clicked. That is the gap this closes:
  the failures that matter most in a viewer leave no trace on the server.

  <b>It reports and never retries.</b> A page that is already failing must not be given a
  queue, a backoff and a second thing to get wrong. One `fetch`, failures swallowed, and if
  the report does not arrive then it does not arrive.

  <b>It is bounded on this side too, not only on the server's.</b> The endpoint rate limits
  per address, but a render loop throwing on every frame would still spend the browser's
  network budget discovering that. So: identical messages are reported once, and the page
  stops after a handful.

  ADR-045. The server side is `LogEndpoints.ReportAsync`, and it answers 204 whatever
  happens — including when it refuses — so there is nothing here to branch on.
*/
(function () {
  "use strict";

  const ENDPOINT = "/rest/studio/events";

  /*
    <b>Eight, and then silence.</b> A viewer with eight distinct failures has told the
    operator everything they need to start looking; the ninth is noise, and a page in a
    loop would otherwise report until it was closed.
  */
  const MOST = 8;

  const seen = new Set();
  let sent = 0;

  function report(kind, message, detail) {
    if (sent >= MOST || typeof message !== "string" || message.length === 0) {
      return;
    }

    // <b>Deduplicated on the message, not on the whole event.</b> The same error from the
    // same line arriving sixty times a second is one fact; including a timestamp or a
    // counter in the key would defeat the deduplication it is there to do.
    if (seen.has(message)) {
      return;
    }

    seen.add(message);
    sent++;

    try {
      fetch(ENDPOINT, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        // <b>Not `keepalive`.</b> It would let a report survive the page unloading, which
        // sounds better and means the browser holds the request open while navigating —
        // and the events worth having here are not the ones that happen on the way out.
        body: JSON.stringify({
          kind: kind,
          page: location.pathname + location.search,
          message: message.slice(0, 2000),
          detail: detail || {},
        }),
      }).catch(function () {
        // Nothing to do and nobody to tell. See the note at the top.
      });
    } catch (ignored) {
      // Same.
    }
  }

  window.addEventListener("error", function (event) {
    report("error", event.message, {
      source: event.filename,
      line: event.lineno,
      column: event.colno,
      stack: event.error && event.error.stack ? String(event.error.stack).slice(0, 1200) : null,
    });
  });

  window.addEventListener("unhandledrejection", function (event) {
    const reason = event.reason;

    report(
      "rejection",
      reason && reason.message ? String(reason.message) : String(reason),
      { stack: reason && reason.stack ? String(reason.stack).slice(0, 1200) : null });
  });

  /*
    <b>And the one the browser cannot see: a layer that refuses to draw.</b> The ArcGIS SDK
    resolves its own load failures internally, so a service that answers an error envelope
    inside an HTTP 200 — which is this server's whole convention — produces no window error
    at all. `map.js` and `view.js` call this directly when a layer fails, which is the only
    way that class of failure reaches the log.
  */
  window.reportStudio = report;
})();
