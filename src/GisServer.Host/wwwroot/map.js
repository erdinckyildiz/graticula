// The viewer's behaviour, in its own file rather than inline in the page.
//
// <b>Required by the console's Content-Security-Policy, not a preference.</b>
// `script-src 'self' https://js.arcgis.com` deliberately does not include
// `'unsafe-inline'` — that was the point of splitting console.js out when D-44 was
// closed. This page was left inline in the same pass, so the policy blocked it and
// the viewer rendered its header and nothing else: no map, no layer name, no
// error. A blocked script is refused by the browser after the response left, so
// the server saw a perfectly successful request.
//
// The guard is now the invariant rather than one page: no document under /console/
// may carry an inline script.

  /*
    Addressed by service and layer id, which is what the services directory
    already knows — and what this page used to guess.

    It took ?layer=name and built `{name}/FeatureServer/0`, which is the same
    assumption that made three of six layers unreachable in the console (D-45):
    right only when a layer sits alone in a service named after it. The directory
    links here with `?service=hosted/look_EarlyAlert&layer=1`, so nothing is
    inferred. The old `?layer=name` form still works, because a URL somebody
    bookmarked should not stop resolving.
  */
  const query = new URLSearchParams(location.search);
  const service = query.get("service");
  const layerId = query.get("layer");

  const url = service
    ? `${location.origin}/rest/services/${service.split("/").map(encodeURIComponent).join("/")}`
      + `/FeatureServer/${encodeURIComponent(layerId ?? "0")}`
    : `${location.origin}/rest/services/${encodeURIComponent(layerId || "landmarks")}`
      + "/FeatureServer/0";

  document.getElementById("which").textContent = service
    ? `${service} / ${layerId ?? 0}`
    : (layerId || "landmarks");

  const note = document.getElementById("note");
  function problem(title, detail) {
    note.innerHTML = `<b>${title}</b>${detail}`;
    note.className = "on";
  }

  // <b>Said out loud rather than thrown at a console nobody has open.</b> The SDK
  // arrives from Esri's CDN, so a script blocker, an offline machine or an
  // air-gapped deployment (Q-15) all leave `require` undefined — and without this
  // the page is blank with the reason only in devtools, which is the failure mode
  // that cost most of 2026-08-16.
  if (typeof require !== "function") {
    problem(
      "The ArcGIS Maps SDK did not load.",
      "This viewer is the SDK pointed at your own service, so it needs "
      + "<code>js.arcgis.com</code>. A script blocker, no network, or an air-gapped "
      + "deployment all look like this. The console itself works without it — only "
      + "the map needs it.");
    throw new Error("the map SDK is unavailable");
  }

  require([
    "esri/Map", "esri/views/MapView", "esri/layers/FeatureLayer",
    "esri/layers/GeoJSONLayer", "esri/layers/VectorTileLayer",
    "esri/layers/WebTileLayer", "esri/Basemap",
  ], (Map, MapView, FeatureLayer, GeoJSONLayer, VectorTileLayer, WebTileLayer,
      Basemap) => {

    /*
      The ground, and there are two of them.

      <b>No third-party tiles by default.</b> This page hardcoded OpenStreetMap's
      public tiles; their usage policy does not permit being an application's
      basemap and on 2026-08-16 every tile answered 403. A tile template is
      configurable and the operator's choice.

      <b>But a map with nothing behind it is not a map.</b> Coastlines with no
      coastline are just a shape floating in white, and you cannot tell being in
      the wrong place from having no data — which is exactly the confusion that
      followed removing the tiles. So we ship our own ground: Natural Earth's
      1:110m land outline, **public domain**, 87 KB, served from this origin.

      Coarse on purpose. It answers "where in the world is this" and nothing else,
      and at that job 110m is not a compromise. It also costs no third-party
      request, so it works in an air-gapped deployment (Q-15) and needs no line in
      the Content-Security-Policy.
    */
    const template = localStorage.getItem("gis-basemap") || "";
    const basemap = template
      ? new Basemap({
        baseLayers: [new WebTileLayer({
          urlTemplate: template,
          copyright: localStorage.getItem("gis-basemap-credit") || "",
        })],
      })
      : null;

    const ground = template
      ? []
      : groundLayers({ GeoJSONLayer, VectorTileLayer, WebTileLayer });

    document.getElementById("ground").textContent = template
      ? "Basemap: " + template
      : "Ground is Natural Earth 1:110m — countries and lakes — public domain and served "
        + "from here. The console's map panel takes a tile template if you have one.";

    /*
      Web Mercator, named rather than inherited — hosted data is stored in 3857
      (ADR-021), so this is the geometry as stored rather than as reprojected.

      <b>And the starting view is given as an extent in the same units, which is
      the bug this comment exists for.</b> Naming the reference without changing
      `center: [28.98, 41.015]` left a centre written in degrees being read as
      metres: twenty-nine metres east of the prime meridian instead of Istanbul,
      three and a half million metres from the data. The layer loaded, the SDK
      queried the visible envelope, the server correctly answered with nothing,
      and the page looked blank for the same reason a telescope pointed at the
      floor does.

      A world extent in 3857 cannot be misread, because it carries its own
      spatial reference. Whatever is drawn then moves the view to its own extent.
    */
    const view = new MapView({
      container: "view",
      map: new Map({ basemap, layers: ground }),
      spatialReference: { wkid: 3857 },
      extent: {
        xmin: -20037508, ymin: -20037508, xmax: 20037508, ymax: 20037508,
        spatialReference: { wkid: 3857 },
      },
      background: template ? undefined : { color: GROUND_WATER },
    });

    let current = null;

    /**
     * Says how many features are drawn and where they are, with a way back.
     *
     * <b>Because "I cannot see it" and "there is nothing there" look the same.</b>
     * Eight polygons of a few hundred metres are sub-pixel at country zoom, so a
     * correct map of real data is indistinguishable from an empty one. The count
     * comes from the server, and the extent is printed in the layer's own
     * reference and in degrees, because one of those is checkable against a
     * table and the other against knowing where places are.
     */
    async function describe(features) {
      const facts = document.getElementById("facts");

      // <b>Not swallowed.</b> This read `catch { }` with a comment calling the
      // failure not worth mentioning, and then the header said "? features" with no
      // way to find out why — which is the habit criticised all through 2026-08-16,
      // committed in the same file that criticised it. The reason is shown where the
      // count would have been.
      let count = null;
      let refusal = null;
      try {
        count = await features.queryFeatureCount();
      } catch (e) {
        refusal = e.message || String(e);
      }

      const box = features.fullExtent;
      const degrees = box && box.spatialReference && box.spatialReference.wkid === 3857
        ? `${(box.xmin / 20037508.34 * 180).toFixed(3)}, `
          + `${(Math.atan(Math.exp(box.ymin / 6378137)) * 360 / Math.PI - 90).toFixed(3)}`
        : null;

      facts.innerHTML =
        (count === null
          ? `<span style="color:#92620d">feature count refused${
            refusal ? ` — ${refusal}` : ""}</span>`
          : `<span>${count.toLocaleString()} feature${count === 1 ? "" : "s"}</span>`)
        + (box
          ? `<span><code>${Math.round(box.width)} × ${Math.round(box.height)} m</code>`
            + ` at <code>${Math.round(box.xmin)}, ${Math.round(box.ymin)}</code>`
            + (degrees ? ` — <code>${degrees}</code>` : "")
            + `</span><button id="frame">Frame layer</button>`
          : `<span>no extent in the layer document</span>`);

      const frame = document.getElementById("frame");
      if (frame) {
        frame.onclick = () => view.goTo(features.fullExtent.expand(1.4))
          .catch(error => problem("The view could not move.", String(error.message || error)));
      }
    }

    /**
     * Adds one layer and frames it.
     *
     * The SDK reports a failed layer load as a rejected promise rather than an
     * exception, and the message is the only place the actual cause appears —
     * usually an untrusted certificate, or a query parameter this server refused
     * with a reason worth reading.
     */
    function add(at) {
      // <b>One layer, replacing whatever was there.</b> This page used to add
      // every layer of a service on top of each other, which is a stack of
      // unrelated geometry rather than a look at anything. The service page's
      // "View in" gives you a picker instead, so choosing is explicit.
      if (current) {
        view.map.remove(current);
        current.destroy();
      }

      const features = new FeatureLayer({
        url: at,
        outFields: ["*"],
        popupTemplate: { title: "{name}", content: "{*}" },
      });

      current = features;
      view.map.add(features);

      // <b>After the view is ready, and the failure is said out loud.</b> goTo
      // before the view settles rejects, and an unhandled rejection is a message
      // in devtools and a blank map to everyone else. An absent extent is the
      // other half: PostGIS statistics fill it, so a table nobody has ANALYZEd
      // has none, and silently not moving looks identical to being lost.
      return features.when(
        async () => {
          await view.when();
          await describe(features);

          if (!features.fullExtent) {
            problem(
              "Drawn, but the view did not move.",
              "This layer's document carries no extent, which is what the map frames "
              + "itself with. Run <code>ANALYZE</code> on the source table and reload.");
            return;
          }

          try {
            await view.goTo(features.fullExtent.expand(1.4));
          } catch (error) {
            problem("The view could not move to the layer.", String(error.message || error));
          }
        },
        error => problem(
          "The SDK could not load a layer.",
          `${at}<br><br>${error.message || error}<br><br>If this mentions a network or certificate ` +
          `problem, open <a href="/healthz/live" target="_blank">/healthz/live</a>, accept the ` +
          `warning and reload. Otherwise it is a parameter this server refused — the message ` +
          `says which.`));
    }

    // <b>A whole service, when no layer is named.</b> The directory's service page
    // links here without one, because "View In" on a service means the service —
    // which is what an ArcGIS directory does too. Group layers are skipped: they
    // hold no features. Named with a layer, only that layer is drawn.
    if (service && layerId === null) {
      fetch(`${location.origin}/rest/services/${service}/FeatureServer?f=json`,
        { headers: { Accept: "application/json" } })
        .then(response => response.json())
        .then(doc => {
          const drawable = (doc.layers || []).filter(l => l.type !== "Group Layer");

          if (drawable.length === 0) {
            problem("Nothing to draw.", "This service lists no feature layers.");
            return;
          }

          // A picker rather than a stack. A service's layers are alternatives to
          // look at, not a composition — and with one layer the picker is a label,
          // which is honest and costs nothing.
          const picker = document.getElementById("picker");

          picker.innerHTML = drawable.map((l, i) =>
            `<button data-id="${l.id}"${i === 0 ? ' class="on"' : ""}>${
              String(l.name).replace(/[&<>"]/g, c =>
                ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]))
            }</button>`).join("");

          picker.onclick = event => {
            const id = event.target.dataset && event.target.dataset.id;
            if (!id) return;
            for (const button of picker.querySelectorAll("button")) {
              button.classList.toggle("on", button === event.target);
            }
            add(`${location.origin}/rest/services/${service}/FeatureServer/${id}`);
          };

          document.getElementById("which").textContent = service;
          add(`${location.origin}/rest/services/${service}/FeatureServer/${drawable[0].id}`);
        })
        .catch(error => problem("The service document could not be read.", String(error)));
    } else {
      add(url);
    }
  });
