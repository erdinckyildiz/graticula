// The console's behaviour, in its own file rather than inline in the page.
//
// <b>Not a style preference — a Content-Security-Policy requirement.</b> The
// console was served with default-src 'none' and no script-src, which blocks
// inline script as firmly as it blocks a CDN. So from 2026-08-15 until this was
// found on 2026-08-16 the console ran no JavaScript at all: it rendered, because
// style-src 'unsafe-inline' was allowed, and then every button did nothing and
// the sign-in form fell back to a native GET that put the password in the URL.
// D-44.
//
// An external file can be allowed with script-src 'self', which needs no
// 'unsafe-inline' — so the fix is also the stricter policy. Still no build
// step and still no npm (A-055): one more file, served as it is written.

const $ = id => document.getElementById(id);
let token = sessionStorage.getItem("gis-token") || null;

// One colour per shown layer, so the legend means something.
const PALETTE = ["#0b6157", "#a63a2b", "#1f5fa8", "#92620d", "#6b3fa0", "#2f7a55"];
const TILE_COLOUR = "#8fb8cc";
const shown = new Map();     // layer name -> { colour, layer }
let esri = null;             // the SDK modules, loaded once
let view = null;

let known = [];              // last /admin/layers listing
let selected = null;         // layer name whose drawer is open
const layerNamed = name => known.find(l => l.name === name) || { name, hosted: false };

// ------------------------------------------------------------------ plumbing

function toast(message, ok = false) {
  const t = $("toast");
  t.textContent = message;
  t.className = "on " + (ok ? "good" : "bad");
  clearTimeout(t._timer);
  t._timer = setTimeout(() => (t.className = ""), 7000);
}

async function api(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (token) headers.Authorization = "Bearer " + token;

  const response = await fetch(path, { ...options, headers });
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* not json */ }

  if (!response.ok) {
    // The server's own message. Every refusal in this product is written to say
    // what to do about it, and replacing that with "request failed" throws away
    // the only useful part.
    throw new Error((body && body.error && body.error.message) || `${response.status} ${path}`);
  }
  return body;
}

// Escaped by default. Everything below interpolates catalogue text — layer
// names, table names, a connection error from PostgreSQL — into markup, and any
// of it can contain a bracket.
function h(value) {
  return String(value ?? "").replace(/[&<>"']/g,
    c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
const pill = value => `<span class="pill p-${h(String(value).toLowerCase())}">${h(value)}</span>`;

const nf = new Intl.NumberFormat();
const num = value => nf.format(value ?? 0);

function bytes(value) {
  if (!value) return "0<small>B</small>";
  const units = ["B", "KB", "MB", "GB"];
  let n = value, i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n < 10 && i > 0 ? n.toFixed(1) : Math.round(n)}<small>${units[i]}</small>`;
}

function duration(ms) {
  const s = Math.round((ms ?? 0) / 1000);
  if (s < 60) return `${s}<small>s</small>`;
  if (s < 3600) return `${Math.floor(s / 60)}<small>m</small> ${s % 60}<small>s</small>`;
  const h_ = Math.floor(s / 3600);
  return `${h_}<small>h</small> ${Math.floor((s % 3600) / 60)}<small>m</small>`;
}

function metric(label, value, note) {
  return `<div class="metric"><dt>${h(label)}</dt><dd>${value}</dd>${
    note ? `<dd class="val" style="font-size:12px;margin-top:2px">${h(note)}</dd>` : ""}</div>`;
}

// ----------------------------------------------------------------------- tabs

function openTab(name) {
  for (const button of document.querySelectorAll("nav button[data-tab]")) {
    if (button.dataset.tab === name) button.setAttribute("aria-current", "page");
    else button.removeAttribute("aria-current");
  }
  for (const view of document.querySelectorAll(".view")) {
    view.classList.toggle("on", view.id === "view-" + name);
  }
  // Re-read on entry, because an operations screen showing numbers from when the
  // page was opened is worse than one that says how old they are.
  if (name === "operations") section("operations", loadOperations);
  if (name === "sources") section("data sources", loadSources, "sources");
}

// ---------------------------------------------------------------------- map

/**
 * Loads the SDK once and teaches it to authenticate.
 *
 * The map's requests go straight from the browser to /rest/services/… and would
 * otherwise carry no credential — so a private or organization layer would 404
 * for the map while the console can see it perfectly. ADR-015 §4's answer to
 * this in the ArcGIS world is a `token=` query parameter, which we deliberately
 * do not implement because a credential in a URL leaks into every proxy log.
 *
 * An interceptor puts it in the Authorization header instead, which is the form
 * ADR-015 §4 says clients should prefer. That it works with the real SDK is the
 * argument for not rushing `token=`.
 */
const SDK = "https://js.arcgis.com/4.29/";

/**
 * Fetches the map SDK the first time a map is wanted, and not before.
 *
 * <b>It used to be a blocking script tag in the head, which made the whole
 * console hostage to a third-party CDN</b> — D-44. Loaded here, a blocked or
 * unreachable CDN costs the map and nothing else, and says so in words instead of
 * leaving buttons that do nothing.
 */
function loadSdk() {
  if (window.require) return Promise.resolve();
  if (loadSdk._started) return loadSdk._started;

  loadSdk._started = new Promise((resolve, reject) => {
    const css = document.createElement("link");
    css.rel = "stylesheet";
    css.href = SDK + "esri/themes/light/main.css";
    document.head.append(css);

    const script = document.createElement("script");
    script.src = SDK;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(
      "The map library could not be loaded from " + SDK + ". Everything else in this "
      + "console works without it; only the map needs it. A script blocker, an offline "
      + "machine or an air-gapped deployment will all look like this."));
    document.head.append(script);
  });

  return loadSdk._started;
}

async function loadEsri() {
  if (esri) return esri;
  await loadSdk();

  return new Promise((resolve, reject) => {
    require([
      "esri/Map", "esri/views/MapView", "esri/layers/FeatureLayer",
      "esri/layers/VectorTileLayer", "esri/layers/GeoJSONLayer",
      "esri/layers/WebTileLayer", "esri/Basemap", "esri/config",
    ], (Map, MapView, FeatureLayer, VectorTileLayer, GeoJSONLayer, WebTileLayer, Basemap,
        config) => {
      config.request.interceptors.push({
        urls: location.origin,
        before: params => {
          params.requestOptions.headers = {
            ...(params.requestOptions.headers || {}),
            Authorization: "Bearer " + token,
          };
        },
      });

      esri = {
        Map, MapView, FeatureLayer, VectorTileLayer, GeoJSONLayer, WebTileLayer, Basemap,
      };
      resolve(esri);
    }, reject);
  });
}

/**
 * The basemap URL template, or null for none. The reader's own setting.
 *
 * <b>There is no default, and removing the one there was is the point.</b> This
 * console shipped `https://tile.openstreetmap.org/...` hardcoded, chosen because
 * Esri's basemaps need an API key tied to an ArcGIS account and the people this
 * product is for do not have one. That reasoning was right about Esri and wrong
 * about OpenStreetMap: their tile servers are volunteer-run and their usage policy
 * does not permit being an application's basemap. On 2026-08-16 every tile came
 * back **403 Access blocked**, which is the policy being enforced rather than a
 * fault to route around.
 *
 * The likely trigger was ours too: `Referrer-Policy: no-referrer` — set for a good
 * reason, because an ArcGIS token can be in a URL — leaves those requests with no
 * Referer, and a refererless browser request is exactly what that policy blocks.
 * Weakening the header to satisfy a third party would be the wrong trade, and it
 * would not make the use legitimate anyway.
 *
 * So: no basemap unless somebody names one. Layers draw on a plain ground, which
 * is all this console needs them to do, and an operator with a tile server of
 * their own can point at it. Kept in `localStorage` because it is a preference of
 * the person reading, not state of the server — ADR-020 §2 is untouched: this adds
 * no capability the admin API does not have, because it adds no server capability
 * at all.
 */
const BASEMAP_KEY = "gis-basemap";
const basemapUrl = () => localStorage.getItem(BASEMAP_KEY) || "";

/**
 * The view, once it is actually usable.
 *
 * <b>A promise rather than the view, and that is the bug this fixes.</b>
 * `ensureMap` used to construct the `MapView` and return it on the same tick,
 * and `showTiles` then called `goTo` on it. A view that has not finished
 * initialising has no animation manager, so `goTo` failed with *"Cannot read
 * properties of undefined (reading 'animation')"* — and only **sometimes**,
 * because a view that happened to be ready worked. Reported 2026-08-16 as
 * happening on the first click.
 *
 * Holding the view in a variable made it worse in a second way: the
 * `if (view) return view` guard returned the half-built view to a second click,
 * so two clicks in the initialising window both raced. Memoising the *promise*
 * means the second caller waits for the first. `loadSdk` in this file already
 * does exactly this, which is the argument for it.
 */
let viewReady = null;

async function ensureMap() {
  // <b>Before the view is built, not after — and getting this backwards cost a
  // silent hang.</b> `#view` lives inside `#mapPanel`, which is `display: none`
  // until it is shown, and both callers used to reveal the panel *after* asking
  // for the view. That was survivable while nothing waited on the view; once
  // `view.when()` was awaited, the first click built a MapView in a hidden,
  // zero-sized container and the promise never settled. Memoising it then made it
  // permanent: every later click joined the same pending promise, so the button
  // did nothing at all, with no error and nothing in any log. Reported by the
  // owner as *"I press it and nothing happens"*, which is the second time this
  // console has failed that way.
  $("mapPanel").classList.add("on");

  if (viewReady) return viewReady;

  viewReady = buildMap();

  try {
    return await viewReady;
  } catch (e) {
    // A failed build must not be remembered as the answer, or every later click
    // returns the same rejection with nothing having been retried.
    viewReady = null;
    view = null;
    throw e;
  }
}

async function buildMap() {
  const { Map, MapView, GeoJSONLayer, VectorTileLayer, WebTileLayer, Basemap } =
    await loadEsri();

  const url = basemapUrl();

  // <b>Our own ground when no tiles are configured</b> — countries, lakes and a
  // graticule from ground.js, so this page and the standalone viewer draw the same
  // map. Removing the OpenStreetMap default left geometry floating in white, where
  // being in the wrong place and having no data look identical.
  // VectorTileLayer passed as well, so an imported ground can replace the vendored
  // world here exactly as it does in the viewer — one rule, ground.js decides.
  // WebTileLayer passed, so ground.js can draw OpenStreetMap here exactly as the
  // viewer does. Without it this page fell through to the vendored Natural Earth
  // files and the owner asked a fourth time why they were still showing.
  const ground = url
    ? []
    : groundLayers({ GeoJSONLayer, VectorTileLayer, WebTileLayer });

  const map = new Map(url
    ? {
      basemap: new Basemap({
        baseLayers: [new WebTileLayer({
          urlTemplate: url,
          copyright: localStorage.getItem(BASEMAP_KEY + "-credit") || "",
        })],
      }),
    }
    : { layers: ground });

  // <b>Web Mercator, named rather than inherited.</b> With no basemap the view
  // takes its reference from whatever loads first, so the same layer could be
  // drawn in 4326 one day and 3857 the next — and hosted data is *stored* in 3857
  // (ADR-021), so asking for anything else makes the server reproject on every
  // request to answer a question nobody asked. Naming it means what is on screen
  // is the geometry as stored.
  // The starting view is an extent carrying its own reference, not a centre — a
  // centre written in degrees is read as metres here, which is how the viewer
  // ended up three and a half million metres from its data and looking blank.
  view = new MapView({
    container: "view",
    map,
    spatialReference: { wkid: 3857 },
    extent: {
      xmin: -20037508, ymin: -20037508, xmax: 20037508, ymax: 20037508,
      spatialReference: { wkid: 3857 },
    },

    // The ocean. Without it the sea is white and the graticule appears to float,
    // which reads as a drawing rather than a map.
    background: url ? undefined : { color: GROUND_WATER },
  });

  // The line the failure was about. Everything a caller does with the view —
  // goTo, add, hitTest — needs it initialised, so it is waited for here once
  // rather than remembered as a rule each caller has to follow.
  //
  // <b>And it is raced against a clock, because a hang is not a rejection.</b>
  // A view that never becomes ready leaves this awaiting forever: no error, no
  // status, nothing logged, and a button that does nothing — which is the exact
  // failure this console has now produced twice for two different reasons. A
  // timeout cannot fix whatever is wrong, but it turns silence into a sentence,
  // and a sentence is what makes the next one diagnosable.
  await Promise.race([
    view.when(),
    new Promise((_, reject) => setTimeout(
      () => reject(new Error(
        "The map did not become ready within 15 seconds. The view is built inside "
        + "the map panel, so this is usually the panel being hidden or having no "
        + "height, or WebGL being unavailable in this browser.")),
      15000)),
  ]);

  return view;
}

/**
 * Rebuilds the map after the basemap setting changes.
 *
 * Everything shown is re-added rather than kept, because the layers belong to the
 * old view and moving them between views is more fragile than asking for them
 * again — the service is the source of truth and the request is cheap.
 */
async function resetBasemap() {
  const showing = [...shown.keys()];
  for (const key of showing) hide(key);

  if (view) {
    view.destroy();
    view = null;
  }

  // Cleared with the view. Leaving it set hands the next caller a promise for a
  // destroyed view, which is the same class of fault as returning an unready one.
  viewReady = null;

  for (const key of showing) {
    const tiles = key.endsWith(" · tiles");
    const name = tiles ? key.slice(0, -" · tiles".length) : key;
    try {
      if (tiles) await showTiles(name);
      else {
        const doc = await api(
          `${(await layerUrl(name)).replace(location.origin, "")}?f=json`);
        await show(name, doc.geometryType);
      }
    } catch (e) { toast(`${name}: ${e.message || e}`); }
  }

  drawBasemapControl();
}

/**
 * Says which ground is actually drawn, rather than which setting is empty.
 *
 * <b>It used to read "none — layers draw on a plain ground" while OpenStreetMap or
 * Natural Earth was on screen.</b> That is the same fault as the viewer's state
 * line naming Natural Earth while OSM tiles were drawn: a status line that reports
 * a *setting* instead of the *outcome* is worth less than no status line, because
 * it is read as the outcome. So it names what ground.js decided.
 */
function drawBasemapControl() {
  const url = basemapUrl();
  const imported = chosenGroundTiles();

  $("basemapState").textContent = url
    ? url
    : imported.length
      ? `${imported.join(", ")} — imported tiles as the ground`
      : "OpenStreetMap — the default ground; name your own tiles to replace it";

  $("basemapInput").value = url;
}

function symbolFor(geometryType, colour) {
  if (geometryType === "esriGeometryPolygon") {
    return { type: "simple-fill", color: colour + "55", outline: { color: colour, width: 1.6 } };
  }
  if (geometryType === "esriGeometryPolyline") {
    return { type: "simple-line", color: colour, width: 2.4 };
  }
  return { type: "simple-marker", size: 9, color: colour, outline: { color: "#fff", width: 1.2 } };
}

function drawLegend() {
  if (shown.size === 0) {
    $("legend").innerHTML = "Nothing shown.";
    return;
  }

  // One layer at a time, so the legend says what is on screen and in which
  // reference — the second part because a map with no basemap gives no other clue
  // about where in the world it is looking.
  $("legend").innerHTML = [...shown.entries()].map(([name, s]) =>
    `<span><i class="swatch" style="background:${h(s.colour)}"></i><b>${h(name)}</b></span>`).join("")
    + `<span style="margin-left:auto">EPSG:3857</span>`;
}

// Hosted services live under /rest/services/hosted and registered ones do
// not. The server redirects the wrong path, so building the wrong URL still
// works — but it costs a round trip on every request, and a console that
// relies on a compatibility redirect is a console teaching the wrong shape.
function serviceRoot(layer) {
  const folder = layer.hosted ? "hosted/" : "";
  return `${location.origin}/rest/services/${folder}${encodeURIComponent(layer.name)}`;
}

/**
 * Where a layer actually lives: which service holds it, and at which index.
 *
 * <b>This exists because the admin listing does not say, and assuming cost us a
 * bug.</b> `/admin/layers` returns a layer's name, table, status and sharing —
 * not the service it belongs to and not its layer id. Every URL in this console
 * was built as `/rest/services/{folder}/{layerName}/FeatureServer/0`, which is
 * right only when a layer is alone in a service named after it.
 *
 * A multi-layer service breaks that completely. `look_EarlyAlert` is one
 * FeatureServer holding sites at 0, routes at 1, a group at 2 and reports at 3 —
 * so asking for `/rest/services/hosted/look_EarlyAlert_routes/FeatureServer/0`
 * asks for a service that does not exist. The server answers its deliberate
 * *"no layer is visible to you… deliberately the same for absent and forbidden"*
 * 404, and it reads as a permission problem to an administrator who has every
 * privilege there is. That is the right answer to the wrong question.
 *
 * So the mapping is read from the services directory — the same public documents
 * an ArcGIS client walks — and cached until the catalogue changes. Group layers
 * are skipped: they hold no features and have no table behind them.
 *
 * The proper fix is upstream: the listing should carry the service and the index.
 * Recorded as a gap in the API rather than papered over here — ADR-020 §2.
 */
let places = null;

async function resolvePlaces() {
  if (places) return places;

  const found = new Map();

  for (const folder of ["hosted", ""]) {
    let directory;
    try {
      directory = await api(`/rest/services${folder ? "/" + folder : ""}?f=json`);
    } catch { continue; }

    for (const service of directory.services || []) {
      if (service.type !== "FeatureServer") continue;

      try {
        const doc = await api(`/rest/services/${service.name}/FeatureServer?f=json`);
        for (const layer of doc.layers || []) {
          if (layer.type === "Group Layer") continue;
          found.set(layer.name, { service: service.name, id: layer.id });
        }
      } catch {
        // A stopped service answers 503 and one shared away from us answers 404.
        // Both mean "not addressable right now", which is not an error here.
      }
    }
  }

  places = found;
  return places;
}

/**
 * The FeatureServer URL for a layer, resolved rather than guessed.
 *
 * Falls back to the old shape when the directory does not know the name, so a
 * layer that is stopped — and therefore absent from the directory — still gets a
 * URL to try instead of no answer at all.
 */
async function layerUrl(name) {
  const place = (await resolvePlaces()).get(name);
  if (place) return `${location.origin}/rest/services/${place.service}/FeatureServer/${place.id}`;
  return `${serviceRoot(layerNamed(name))}/FeatureServer/0`;
}

const tileKey = name => `${name} · tiles`;

// Tiles, through Esri's own VectorTileLayer rather than anything of ours.
//
// This is the only check that A-057 actually names: that the ST_AsMVT output
// is acceptable to a real client. Rendering a tile ourselves proved the
// geometry is right, which is a different and smaller claim — our decoder and
// our encoder could agree about a mistake, and a client that refuses the
// service is a service nobody can use no matter how correct the bytes are.
async function showTiles(name) {
  const { VectorTileLayer } = await loadEsri();
  const mapView = await ensureMap();

  // The tile service belongs to the service, not to the layer, so a member of a
  // multi-layer service draws all of its siblings' tiles too. That is what the
  // server offers and the legend says which layer asked for it.
  const place = (await resolvePlaces()).get(name);
  const root = place
    ? `${location.origin}/rest/services/${place.service}`
    : serviceRoot(layerNamed(name));

  const layer = new VectorTileLayer({
    url: `${root}/VectorTileServer`,
    title: `${name} (tiles)`,
  });

  clearMap();
  mapView.map.add(layer);
  shown.set(tileKey(name), { colour: TILE_COLOUR, layer, tiles: true });
  drawLegend();
  $("mapPanel").classList.add("on");

  try {
    // <b>The same clock as the view's, for the same reason.</b> A tile layer that
    // reads the service document and then gives up leaves this awaiting forever:
    // that is exactly what happened when the document's fullExtent and tileInfo
    // declared different references — the SDK fetched the metadata, the style and
    // the sprites, requested no tile, and settled nothing. Only the server log
    // showed it. D-49.
    await Promise.race([
      layer.when(),
      new Promise((_, reject) => setTimeout(
        () => reject(new Error(
          "The tile layer did not become ready within 15 seconds. It fetched the service "
          + "document and stopped, which usually means the document is inconsistent — check "
          + "that fullExtent and tileInfo agree about the spatial reference.")),
        15000)),
    ]);

    if (layer.fullExtent) await mapView.goTo(layer.fullExtent.expand(1.2));
  } catch (e) {
    hide(tileKey(name));
    toast(`${name} tiles: ${e.message || e}`);
  }
}

/**
 * Takes everything off the map.
 *
 * <b>Showing replaces rather than accumulates, on the owner's correction of
 * 2026-08-16.</b> Every Map click used to add another layer, so a few clicks
 * produced a stack of unrelated geometry in six colours and a legend nobody could
 * read — and looking at one layer meant reloading the page. The question a viewer
 * answers is "what does this layer look like", which has one subject.
 *
 * Comparing two layers is a real thing to want and it is not this. If it is added
 * it should be asked for deliberately, with a control that says so, rather than
 * arrived at by clicking twice.
 */
function clearMap() {
  for (const key of [...shown.keys()]) hide(key);
}

async function show(name, geometryType) {
  const { FeatureLayer } = await loadEsri();
  const mapView = await ensureMap();

  const layer = new FeatureLayer({
    url: await layerUrl(name),
    title: name,
    outFields: ["*"],
    renderer: { type: "simple", symbol: symbolFor(geometryType, PALETTE[0]) },
    popupTemplate: { title: name, content: "{*}" },
  });

  clearMap();
  mapView.map.add(layer);
  shown.set(name, { colour: PALETTE[0], layer });
  drawLegend();
  $("mapPanel").classList.add("on");

  try {
    await layer.when();

    // Zoom to the layer just shown. fullExtent comes from the layer document,
    // which this server fills from PostGIS statistics — so it may be absent,
    // and an absent extent is said out loud rather than left as a map that
    // quietly did not move.
    if (layer.fullExtent) {
      await mapView.goTo(layer.fullExtent.expand(1.4));
    } else {
      toast(`${name} is shown, but its extent is unknown so the map did not move. `
        + "Run ANALYZE on the source table and reload.");
    }
  } catch (e) {
    // The SDK reports a refused request here, and the server's message is the
    // useful part — a stopped service, a layer not shared, a parameter refused.
    hide(name);
    toast(`${name}: ${e.message || e}`);
  }
}

function hide(name) {
  const entry = shown.get(name);
  if (!entry) return;
  if (view) view.map.remove(entry.layer);
  shown.delete(name);
  drawLegend();
  if (shown.size === 0) $("mapPanel").classList.remove("on");
}

// ------------------------------------------------------------------- services

// How the list is filtered and ordered. Both belong to the reader, so they
// survive a reload of the data and are never reset by an action taken on a row —
// stopping a layer must not throw away the search that found it.
let filter = "";
let order = { key: "name", down: false };

// Sorting by a secondary key inside each group, so equal statuses stay in a
// stable and readable order instead of whatever the server happened to send.
const ORDER = {
  name: l => l.name.toLowerCase(),
  where: l => (l.hosted ? "0" : "1") + l.name.toLowerCase(),
  status: l => l.status + l.name.toLowerCase(),
  sharing: l => l.sharing + l.name.toLowerCase(),
  table: l => (l.table || "").toLowerCase(),
  owner: l => (l.owner || "").toLowerCase() + l.name.toLowerCase(),
};

function visibleLayers() {
  const needle = filter.trim().toLowerCase();

  // Name, table, source and owner all match, because "which layer is on that
  // table" is a question asked as often as "where is this layer".
  const rows = needle
    ? known.filter(l => [l.name, l.table, l.dataSource, l.owner]
        .some(v => (v || "").toLowerCase().includes(needle)))
    : [...known];

  const key = ORDER[order.key] || ORDER.name;
  rows.sort((a, b) => String(key(a)).localeCompare(String(key(b))));
  if (order.down) rows.reverse();
  return rows;
}

async function loadLayers() {
  const { layers } = await api("/admin/layers");
  known = layers;
  places = null;   // a start, stop, publish or delete moves what the directory holds
  $("cServices").textContent = layers.length;
  drawLayers();

  if (selected && !layers.some(l => l.name === selected)) closeDrawer();
  else if (selected) openLayer(selected);
}

function drawLayers() {
  const rows = visibleLayers();

  for (const th of document.querySelectorAll("#layerHead th[data-sort]")) {
    const active = th.dataset.sort === order.key;
    th.setAttribute("aria-sort", active ? (order.down ? "descending" : "ascending") : "none");
  }

  $("layerCount").textContent = filter.trim()
    ? `${rows.length} of ${known.length}`
    : `${known.length} layer${known.length === 1 ? "" : "s"}`;

  $("layers").innerHTML = known.length === 0
    ? `<tr><td colspan="7" class="empty">No layers yet. <b>New layer</b> designs an empty
         feature class or imports a file into one.</td></tr>`
    : rows.length === 0
      ? `<tr><td colspan="7" class="empty">Nothing matches <b>${h(filter)}</b>.</td></tr>`
      : rows.map(l => {
        const isShown = shown.has(l.name);
        const tilesShown = shown.has(tileKey(l.name));
        const stopped = l.status === "stopped";
        const swatch = isShown
          ? `<i class="swatch" style="background:${h(shown.get(l.name).colour)}"></i>` : "";

        // Map and Tiles are back on the row, not only in the drawer. Comparing two
        // layers on one map is the common case, and making it cost two drawer
        // openings was a step backwards from the console this replaced.
        return `<tr class="pick${selected === l.name ? " sel" : ""}" data-pick="${h(l.name)}">
          <td class="acts">${swatch}<button class="tiny ${isShown ? "on" : ""}"
              data-show="${h(l.name)}"
              ${stopped ? "disabled title='A stopped service answers 503, so there is nothing to draw.'"
                        : `title="${isShown ? "Take it off the map" : "Draw it on the map"}"`}
              >${isShown ? "Hide" : "Map"}</button>${l.hosted
            ? `<button class="tiny ${tilesShown ? "on" : ""}" data-tiles="${h(l.name)}"
                 ${stopped ? "disabled" : ""}
                 title="Draw it as vector tiles, through Esri's own VectorTileLayer">Tiles</button>`
            : ""}</td>
          <td class="name">${h(l.name)}</td>
          <td>${pill(l.hosted ? "hosted" : "registered")}</td>
          <td>${pill(l.status)}</td>
          <td>${pill(l.sharing)}</td>
          <td class="val">${h(l.table)}${l.arcGisServable ? "" : " · no object id"}</td>
          <td class="val">${h(l.owner || "")}</td>
        </tr>`;
      }).join("");
}

async function loadSystemServices() {
  const { services } = await api("/admin/services");
  $("systemServices").innerHTML = (services || []).length === 0
    ? `<tr><td colspan="5" class="empty">None.</td></tr>`
    : services.map(s => `<tr>
        <td class="name">${h(s.name)}</td>
        <td class="val">${h(s.kind)}</td>
        <td class="val">${h(s.folder || "")}</td>
        <td>${pill(s.sharing)}</td>
        <td style="text-align:right">
          <select data-service-share="${h(s.name)}">
            ${["private", "organization", "public"].map(v =>
              `<option value="${v}"${v === s.sharing ? " selected" : ""}>${v}</option>`).join("")}
          </select>
        </td></tr>`).join("");
}

// --------------------------------------------------------------------- drawer

function closeDrawer() {
  selected = null;
  $("drawer").classList.remove("on");
  $("drawer").setAttribute("aria-hidden", "true");
  for (const row of document.querySelectorAll("tr.sel")) row.classList.remove("sel");
}

function openLayer(name) {
  const l = layerNamed(name);
  selected = name;

  const isShown = shown.has(name);
  const tilesShown = shown.has(tileKey(name));
  const stopped = l.status === "stopped";

  $("drawerTitle").textContent = name;
  $("drawerSub").textContent = `${l.hosted ? "hosted" : "registered"} · ${l.dataSource || ""}`;

  $("drawerBody").innerHTML = `
    <div class="group">
      <div class="row" style="margin-bottom:0">
        <button data-show="${h(name)}" class="${isShown ? "on" : ""}"
          ${stopped ? "disabled title='A stopped service answers 503, so there is nothing to draw.'" : ""}>
          ${isShown ? "Hide on map" : "Show on map"}</button>
        ${l.hosted
          ? `<button data-tiles="${h(name)}" class="${tilesShown ? "on" : ""}" ${stopped ? "disabled" : ""}
               title="Draw this as vector tiles, through Esri's VectorTileLayer.">
               ${tilesShown ? "Hide tiles" : "Show tiles"}</button>`
          : `<button disabled title="Tiles come only from hosted data — data this server owns as
               system of record (Q-67). This layer is registered, so it has a FeatureServer and no
               VectorTileServer.">No tiles</button>`}
        <button data-toggle="${h(name)}" data-to="${stopped ? "start" : "stop"}">
          ${stopped ? "Start" : "Stop"}</button>
      </div>
    </div>

    <div class="group">
      <h3>Contents</h3>
      <div id="contents" class="val">reading the layer document…</div>
      <p class="hint" style="margin-top:8px">Read from the service's own layer document, which is
        what any ArcGIS client reads — so this is the server's answer rather than the
        catalogue's, and a disagreement between them is worth knowing about.</p>
    </div>

    <div class="group">
      <h3>Identity</h3>
      <dl class="facts">
        <dt>Source table</dt><dd>${h(l.table)}</dd>
        <dt>Data source</dt><dd>${h(l.dataSource || "")}</dd>
        <dt>Owner</dt><dd>${h(l.owner || "—")}</dd>
        <dt>Layer id</dt><dd>${h(l.id || "—")}</dd>
        <dt>ArcGIS servable</dt><dd>${l.arcGisServable ? "yes" : "no — the table has no object id"}</dd>
      </dl>
    </div>

    <div class="group">
      <h3>Sharing</h3>
      <div class="row" style="margin-bottom:6px">
        <select data-share="${h(name)}">
          ${["private", "organization", "public"].map(v =>
            `<option value="${v}"${v === l.sharing ? " selected" : ""}>${v}</option>`).join("")}
        </select>
      </div>
      <p class="hint">Who may read it. Separate from started/stopped, which is whether it runs
        at all — ADR-020 §3.</p>
    </div>

    ${l.hosted ? `
    <div class="group">
      <h3>Tile cache lifetime</h3>
      <div class="row" style="margin-bottom:6px">
        <label class="field">Seconds<input type="number" id="ttl" min="0" step="1"
          style="width:110px" placeholder="server default"></label>
        <button data-cache="${h(name)}">Set</button>
        <button data-cache="${h(name)}" data-clear="1" class="ghost">Use default</button>
      </div>
      <p class="hint">Set by whoever knows how volatile the data is. <code>0</code> means never
        served from cache and <code>no-store</code> downstream. Changing this purges nothing:
        new freshness does not make an existing tile wrong.
        <b>The box starts empty because the layer listing does not carry the current value</b> —
        so this sets it and cannot show it, which is a gap in the API rather than in this
        screen (ADR-020 §2).</p>
    </div>` : ""}

    <div class="group">
      <h3>Style</h3>
      <div class="row" style="margin-bottom:6px">
        <button data-style="${h(name)}">Fetch current</button>
        <button data-style-del="${h(name)}" class="ghost">Back to generated</button>
      </div>
      <textarea id="styleDoc" rows="8" spellcheck="false"
        placeholder="A MapLibre style document. Fetch first to see what is served now."></textarea>
      <div class="row" style="margin:8px 0 0">
        <button class="primary" data-style-put="${h(name)}">Store style</button>
      </div>
    </div>

    <div class="group">
      <h3>Endpoints</h3>
      <dl class="facts" id="endpoints"><dt>—</dt><dd>resolving…</dd></dl>
    </div>

    <div class="group">
      <h3>Maintenance</h3>
      <div class="row" style="margin-bottom:6px">
        <button data-refresh="${h(name)}">Forget remembered shape</button>
        <button class="danger" data-delete="${h(name)}">Delete layer</button>
      </div>
      <p class="hint">The shape of a table is remembered for a while (D-17); refresh forgets it
        now, which is what you want after altering the table at the source. Deleting removes the
        publication${l.hosted ? " and, because this layer is hosted, its table" : ", and leaves the customer's table alone"}.</p>
    </div>`;

  const ttl = $("ttl");
  if (ttl && l.cacheSeconds != null) ttl.value = l.cacheSeconds;

  describeContents(name, l);

  for (const row of document.querySelectorAll("tr.sel")) row.classList.remove("sel");
  const row = document.querySelector(`tr[data-pick="${CSS.escape(name)}"]`);
  if (row) row.classList.add("sel");

  $("drawer").classList.add("on");
  $("drawer").setAttribute("aria-hidden", "false");
}

/**
 * Fills the drawer's Contents group from the layer's own service document.
 *
 * <b>Not a new admin capability</b> — ADR-020 §2. This is the same document the
 * map already fetches to choose a symbol, and the same one any ArcGIS client
 * reads. What the console was missing is that "what is in this layer" — its
 * fields, its geometry, its extent — had no answer anywhere in the UI, and it is
 * the first thing anybody asks about a layer they did not publish themselves.
 *
 * Loaded after the drawer paints rather than before, so opening a layer is never
 * held up by a request; and a refusal is shown in place rather than as a toast,
 * because a stopped service refusing this is expected and not an error.
 */
async function describeContents(name, layer) {
  const box = $("contents");
  if (!box) return;

  const place = (await resolvePlaces()).get(name);
  if (selected === name) fillEndpoints(name, layer, place);

  try {
    const doc = await api(`${(await layerUrl(name)).replace(location.origin, "")}?f=json`);
    if (selected !== name) return;   // the reader moved on while this was in flight

    const fields = (doc.fields || []);
    const geometry = (doc.geometryType || "").replace("esriGeometry", "") || "none";
    const box2 = doc.extent;

    box.innerHTML = `
      <dl class="facts">
        <dt>Geometry</dt><dd>${h(geometry)}</dd>
        <dt>Reference</dt><dd>${h(doc.extent?.spatialReference?.wkid ?? "—")}</dd>
        <dt>Fields</dt><dd>${num(fields.length)}</dd>
        <dt>Extent</dt><dd>${box2
          ? `${Number(box2.xmin).toFixed(1)}, ${Number(box2.ymin).toFixed(1)} →
             ${Number(box2.xmax).toFixed(1)}, ${Number(box2.ymax).toFixed(1)}`
          : "unknown — run ANALYZE on the source table"}</dd>
      </dl>
      ${fields.length ? `<div style="margin-top:8px">${fields.map(f =>
        `<span class="pill p-private" style="margin:0 4px 4px 0">${h(f.name)}
           <span style="color:var(--faint);font-weight:400">${
             h((f.type || "").replace("esriFieldType", ""))}</span></span>`).join("")}</div>` : ""}`;
  } catch (e) {
    if (selected !== name) return;
    box.innerHTML = `<span style="color:var(--stop)">${h(e.message || String(e))}</span>`;
  }
}

/**
 * The drawer's endpoint links, once the layer's real address is known.
 *
 * <b>Tiles are per service, not per layer, and saying so matters.</b> A member of
 * a multi-layer service shares one VectorTileServer with its siblings, so the tile
 * link for `look_EarlyAlert_routes` serves the whole of `look_EarlyAlert`.
 * Labelling it as this layer's tiles would be a quiet lie.
 */
function fillEndpoints(name, layer, place) {
  const box = $("endpoints");
  if (!box) return;

  if (!place) {
    box.innerHTML = `<dt>None</dt><dd>Not in the services directory. A stopped layer is
      absent from it, which is expected; otherwise the catalogue and the directory
      disagree and that is worth looking into.</dd>`;
    return;
  }

  const service = `${location.origin}/rest/services/${place.service}`;
  const shared = !name.endsWith(place.service.split("/").pop());

  box.innerHTML = `
    <dt>Feature</dt><dd><a href="${h(service)}/FeatureServer/${place.id}?f=json"
      target="_blank" rel="noreferrer">FeatureServer/${place.id}</a></dd>
    <dt>Service</dt><dd><a href="${h(service)}/FeatureServer?f=json"
      target="_blank" rel="noreferrer">${h(place.service)}</a></dd>
    ${layer.hosted ? `<dt>Tiles</dt><dd><a href="${h(service)}/VectorTileServer?f=json"
      target="_blank" rel="noreferrer">VectorTileServer</a>${shared
        ? ` <span class="val">— the whole service, not this layer alone</span>` : ""}</dd>` : ""}
    <dt>Directory</dt><dd><a href="${h(service)}" target="_blank" rel="noreferrer">browse</a></dd>`;
}

// -------------------------------------------------------------------- sources

async function loadSources() {
  const { dataSources } = await api("/admin/datasources");
  $("cSources").textContent = dataSources.length;

  $("sources").innerHTML = dataSources.length === 0
    ? `<tr><td colspan="5" class="empty">None registered.</td></tr>`
    : dataSources.map(d => `<tr>
        <td class="name">${h(d.name)}</td>
        <td class="val">${h(d.kind)}</td>
        <td class="num">${num(d.layerCount)}</td>
        <td class="val">${h(d.id || "")}</td>
        <td style="text-align:right"><button data-probe="${h(d.id)}"
          data-probe-name="${h(d.name)}">Probe</button></td>
      </tr>`).join("");
}

function renderProbe(name, r) {
  const tables = r.tables || [];
  $("probe").innerHTML = `
    <h2>${h(name)} — probed just now</h2>
    <div class="panel pad">
      <div class="row" style="margin-bottom:10px">
        ${pill(r.outcome)}
        <span style="font-size:13.5px">${h(r.message)}</span>
      </div>
      <dl class="facts" style="grid-template-columns:auto 1fr auto 1fr">
        <dt>PostgreSQL</dt><dd>${h(r.serverVersion || "—")}</dd>
        <dt>PostGIS</dt><dd>${h(r.postgisVersion || "—")}</dd>
        <dt>Can publish</dt><dd>${r.canPublish ? "yes" : "no"}</dd>
        <dt>Tables visible</dt><dd>${num(tables.length)}</dd>
      </dl>
    </div>
    ${tables.length ? `<div class="panel" style="margin-top:14px">
      <table>
        <thead><tr><th>Schema</th><th>Table</th><th>Geometry</th><th class="num">SRID</th>
          <th>Object id</th><th>Writable</th></tr></thead>
        <tbody>${tables.map(t => `<tr>
          <td class="val">${h(t.schemaName)}</td>
          <td class="name">${h(t.tableName)}</td>
          <td class="val">${h(t.geometryType)} · ${h(t.geometryColumn)}</td>
          <td class="num">${h(t.srid)}</td>
          <td class="val">${h(t.objectIdColumn || "—")}</td>
          <td>${t.writable ? "yes" : "read only"}</td>
        </tr>`).join("")}</tbody>
      </table>
    </div>` : ""}`;
}

// ----------------------------------------------------------------- operations

async function loadOperations() {
  let health;
  try { health = await api("/admin/health"); }
  catch (e) { $("storeMetrics").innerHTML = metric("Platform store", "unreachable", e.message); return; }

  const store = health.platformStore || {};
  $("storeMetrics").innerHTML =
    metric("Status", h(health.status === "ok" ? "ok" : "degraded")) +
    metric("Reachable", store.reachable ? "yes" : "no", store.error || "") +
    metric("Layers", num(store.layers)) +
    metric("Version", h(health.version || "—"));

  const r = health.runtime || {};
  const pause = r.uptimeMilliseconds
    ? (100 * (r.gcPauseMilliseconds || 0) / r.uptimeMilliseconds) : 0;
  $("runtimeMetrics").innerHTML =
    metric("Uptime", duration(r.uptimeMilliseconds)) +
    metric("Managed heap", bytes(r.heapBytes)) +
    metric("Allocated since start", bytes(r.allocatedBytes)) +
    metric("GC pause", `${pause.toFixed(pause < 1 ? 2 : 1)}<small>% of wall-clock</small>`) +
    metric("Collections", `${num(r.gen0)}<small>/</small>${num(r.gen1)}<small>/</small>${num(r.gen2)}`,
           "gen0 / gen1 / gen2") +
    metric("Cores", `${num(r.cores)}${r.serverGc ? "" : ""}`, r.serverGc ? "server GC" : "workstation GC");

  const tiles = health.tileCache || {};
  const shapes = health.describedShapes || {};
  $("cacheMetrics").innerHTML =
    metric("Tiles cached", num(tiles.entries)) +
    metric("On disk", `${num(tiles.megabytes)}<small>MB</small>`) +
    metric("Building now", num(tiles.building)) +
    metric("Shapes remembered", num(shapes.count)) +
    metric("Shape lifetime", `${num(shapes.lifetimeSeconds)}<small>s</small>`);
  $("cacheNotes").textContent = [tiles.note, shapes.note].filter(Boolean).join(" ");

  const { routes, ungoverned } = await api("/admin/routes");
  $("routeMetrics").innerHTML =
    metric("Routes", num(routes.length)) +
    metric("Ungoverned", num(ungoverned),
           ungoverned === 0 ? "ADR-018 condition 5 holds" : "ADR-018 condition 5 is failing");
  $("routes").innerHTML = routes.map(route => `<tr>
      <td class="val">${h(route.pattern)}</td>
      <td class="val">${h((route.methods || []).join(", "))}</td>
      <td>${route.governed ? h(route.by || "") : pill("ungoverned")}</td>
    </tr>`).join("");

  // Stamped, because a runtime figure with no time on it is read as "now" for as
  // long as the tab stays open — and uptime and GC pause are exactly the numbers
  // somebody compares across a few minutes.
  $("opsWhen").textContent = "read at " + new Date().toLocaleTimeString();
}

async function refreshHealth() {
  try {
    const health = await api("/admin/health");
    const ok = health.status === "ok";
    $("healthDot").className = "dot " + (ok ? "" : "bad");
    $("healthLine").textContent = ok
      ? `platform store reachable · ${health.platformStore.layers} layers`
      : "DEGRADED — platform store unreachable";
  } catch {
    $("healthDot").className = "dot unknown";
    $("healthLine").textContent = "health unreachable";
  }
}

// ------------------------------------------------------------------ new layer

const FIELD_TYPES =
  ["Text", "Integer", "BigInteger", "SmallInteger", "Double", "Single", "Boolean", "Date", "Guid"];

function openNewLayer() {
  selected = null;
  // Retitled 2026-08-16: it held only hosted layers, and now it also publishes a
  // table this server does not hold and creates services and groups. A heading
  // that names one of four things is worse than a general one.
  $("drawerTitle").textContent = "Create";
  $("drawerSub").textContent = "a layer, a service, or a group inside one";
  $("drawerBody").innerHTML = `
    <div class="group">
      <h3>Design a schema</h3>
      <p class="hint">For data you are going to collect. Creates an empty feature class you
        fill through the feature service. <code>objectid</code> and <code>geom</code> are made
        for you, stored in Web Mercator so the layer can serve tiles.</p>
      <form id="designForm" autocomplete="off">
        <div class="row">
          <label class="field">Name<input type="text" id="dName" placeholder="inspections" required></label>
          <label class="field">Geometry<select id="dGeom">
            <option>Point</option><option>MultiPoint</option><option>LineString</option>
            <option>MultiLineString</option><option selected>Polygon</option><option>MultiPolygon</option>
          </select></label>
          <label class="field">Sharing<select id="dShare">
            <option value="private" selected>private</option>
            <option value="organization">organization</option>
            <option value="public">public</option>
          </select></label>
        </div>
        <table>
          <thead><tr><th>Field</th><th>Type</th><th>Required</th><th></th></tr></thead>
          <tbody id="dFields"></tbody>
        </table>
        <div class="row" style="margin:10px 0 0">
          <button type="button" id="dAdd" class="ghost">Add field</button>
          <button class="primary" type="submit">Create empty layer</button>
        </div>
      </form>
    </div>

    <div class="group">
      <h3>Import a file</h3>
      <p class="hint">For data you already have. Reads the schema from the file. GeoJSON only —
        a shapefile is a ZIP and this server does not open archives (Q-98). Coordinates must be
        WGS 84 longitude, latitude; they are reprojected to Web Mercator once on the way in.</p>
      <form id="importForm" autocomplete="off">
        <div class="row">
          <label class="field">Name<input type="text" id="iName" placeholder="parks" required></label>
          <label class="field">Sharing<select id="iShare">
            <option value="private" selected>private</option>
            <option value="organization">organization</option>
            <option value="public">public</option>
          </select></label>
        </div>
        <div class="row">
          <label class="field">GeoJSON<input id="iFile" type="file"
            accept=".json,.geojson,application/geo+json" required></label>
        </div>
        <button class="primary" type="submit">Import and publish</button>
      </form>
    </div>

    <div class="group">
      <h3>Publish a registered table</h3>
      <p class="hint">For data that already lives in a PostGIS database this server can reach.
        <b>Nothing is copied</b> — the layer reads the table where it is. The tables are read from
        the database when you pick a connection, not remembered from when it was registered, so a
        table that has been dropped or revoked does not appear. Only tables with a geometry column
        are offered, because the rest cannot be a feature layer.</p>
      <form id="regForm" autocomplete="off">
        <div class="row">
          <label class="field" style="flex:1 1 190px">Connection<select id="rSource">
            <option value="">choose a connection…</option>
          </select></label>
          <label class="field" style="flex:2 1 250px">Table<select id="rTable" disabled>
            <option value="">pick a connection first</option>
          </select></label>
        </div>
        <div id="rFacts" style="display:none"></div>
        <div class="row">
          <label class="field">Layer name<input type="text" id="rName" placeholder="parcels" required></label>
          <label class="field">Identity column
            <input type="text" id="rIdentity" placeholder="nominate one" required></label>
          <label class="field">Sharing<select id="rShare">
            <option value="private" selected>private</option>
            <option value="organization">organization</option>
            <option value="public">public</option>
          </select></label>
          <label class="field">Into service <span class="val">(optional)</span>
            <input type="text" id="rService" list="serviceNames" placeholder="a service of its own">
          </label>
        </div>
        <!--
          Empty, required, and not prefilled — which is a decision from the
          register rather than a UI preference. Q-57: identity for a registered
          table is "declared, not inferred", and the administrator nominates the
          column. Filling this from the probe would make the server's inference
          look like the operator's choice, and this is the column that decides
          which row an edit lands on.
        -->
        <p class="hint">Which column identifies a feature for the life of the layer. It is your
          nomination, not something read from the table (Q-57): we will not synthesise one, because
          a row number is not stable and a side mapping table would drift on the owner's first
          edit.</p>
        <datalist id="serviceNames"></datalist>
        <p class="hint">Naming an existing service adds this layer to it at the next free index —
          that is how several related layers become one service. Leaving it empty gives the layer a
          single-layer service named after it.</p>
        <button class="primary" type="submit">Publish</button>
      </form>
    </div>

    <div class="group">
      <h3>A service, and groups inside one</h3>
      <p class="hint">A service is a container of layers, so it can exist before its layers do —
        and that is the order you need when the structure matters: create the service, add the
        groups, then publish layers into it naming the group to nest under.</p>
      <form id="svcForm" autocomplete="off">
        <div class="row">
          <label class="field">Service name<input type="text" id="cName" placeholder="EarlyAlert" required></label>
          <label class="field">Folder <span class="val">(optional)</span>
            <input type="text" id="cFolder" placeholder="hosted"></label>
          <label class="field">Sharing<select id="cShare">
            <option value="private" selected>private</option>
            <option value="organization">organization</option>
            <option value="public">public</option>
          </select></label>
        </div>
        <div class="row">
          <label class="field" style="flex:1 1 100%">Description <span class="val">(optional)</span>
            <input type="text" id="cDesc" placeholder="what it is for"></label>
        </div>
        <button type="submit">Create empty service</button>
      </form>

      <form id="grpForm" autocomplete="off" style="margin-top:16px">
        <div class="row">
          <label class="field" style="flex:2 1 220px">Group inside
            <input type="text" id="gService" list="serviceNames" placeholder="hosted/EarlyAlert" required></label>
          <label class="field">Group name<input type="text" id="gName" placeholder="Reports" required></label>
          <label class="field">Nest under <span class="val">(layer id)</span>
            <input type="number" id="gParent" min="0" placeholder="top level"></label>
        </div>
        <button type="submit">Create group layer</button>
      </form>
    </div>

    <div id="newResult" class="group" style="display:none"></div>`;

  $("designForm").addEventListener("submit", createDesigned);
  $("importForm").addEventListener("submit", createImported);
  $("regForm").addEventListener("submit", publishRegistered);
  $("svcForm").addEventListener("submit", createService);
  $("grpForm").addEventListener("submit", createGroupLayer);
  $("rSource").addEventListener("change", loadRegisteredTables);
  $("rTable").addEventListener("change", showChosenTable);
  $("dAdd").addEventListener("click", () => addFieldRow());
  addFieldRow();

  // Both lists are filled after the drawer is on screen. A form that cannot be
  // used until a request finishes should say so in its own control rather than
  // hold the drawer shut.
  section("connections", fillConnectionChoices);
  section("service names", fillServiceChoices);

  $("drawer").classList.add("on");
  $("drawer").setAttribute("aria-hidden", "false");
}

// ------------------------------------------- publishing what is already there

// The tables the last probe found, by their select value. Held because the
// publish request needs six fields the operator should not have to retype from a
// table they just chose — schema, table, geometry column, its type, the SRID and
// the object-id column.
let probed = new Map();

async function fillConnectionChoices() {
  const { dataSources = [] } = await api("/admin/datasources");
  const select = $("rSource");
  if (!select) return;

  select.innerHTML = `<option value="">choose a connection…</option>` +
    dataSources.map(s => `<option value="${h(s.id)}">${h(s.name)}</option>`).join("");

  if (!dataSources.length) {
    select.innerHTML = `<option value="">no connection is registered</option>`;
  }
}

/**
 * Service names for both the publish target and the group's parent.
 *
 * Read from the services directory rather than from a listing, because the
 * directory is the document that says what exists and at which path — the same
 * reason `resolvePlaces` reads it.
 */
async function fillServiceChoices() {
  const list = $("serviceNames");
  if (!list) return;

  const paths = [...new Set([...(await resolvePlaces()).values()].map(p => p.service))];
  list.innerHTML = paths.sort().map(p => `<option value="${h(p)}"></option>`).join("");
}

/**
 * Splits a directory path into the folder and the bare service name.
 *
 * <b>The two endpoints want it two ways, which is why this exists.</b> Publishing
 * takes a bare `serviceName` and no folder — the folder is implicit in where
 * hosted layers go. Creating a group takes the bare name in the path and the
 * folder in the body. Offering one datalist of directory paths and splitting here
 * keeps the operator from having to know which is which.
 */
function splitService(text) {
  const value = (text || "").trim().replace(/^\/+|\/+$/g, "");
  const cut = value.lastIndexOf("/");
  return cut < 0
    ? { folder: null, name: value }
    : { folder: value.slice(0, cut), name: value.slice(cut + 1) };
}

async function loadRegisteredTables() {
  const id = $("rSource").value;
  const select = $("rTable");
  probed = new Map();
  $("rFacts").style.display = "none";
  $("rName").value = "";

  if (!id) {
    select.disabled = true;
    select.innerHTML = `<option value="">pick a connection first</option>`;
    return;
  }

  select.disabled = true;
  select.innerHTML = `<option value="">reading the database…</option>`;

  let capability;
  try {
    capability = await api(`/admin/datasources/${encodeURIComponent(id)}/capability`);
  } catch (e) {
    // The refusal belongs in the control, not only in a toast that will fade
    // while the operator is still looking at an empty list.
    select.innerHTML = `<option value="">could not read: ${h(e.message || e)}</option>`;
    toast(`connection: ${e.message || e}`);
    return;
  }

  const tables = capability.tables || [];

  if (!capability.canPublish) {
    select.innerHTML = `<option value="">${h(capability.message
      || "this connection cannot publish")}</option>`;
    return;
  }

  if (!tables.length) {
    select.innerHTML = `<option value="">no table with a geometry column is visible</option>`;
    return;
  }

  for (const t of tables) {
    probed.set(`${t.schemaName}.${t.tableName}`, t);
  }

  select.innerHTML = `<option value="">choose a table…</option>` +
    [...probed.keys()].sort().map(k => `<option value="${h(k)}">${h(k)}</option>`).join("");
  select.disabled = false;
}

function showChosenTable() {
  const t = probed.get($("rTable").value);
  const facts = $("rFacts");

  if (!t) {
    facts.style.display = "none";
    return;
  }

  // The layer name defaults to the table's, which is what an operator publishing
  // one table almost always wants, and is still editable.
  if (!$("rName").value.trim()) $("rName").value = t.tableName;

  facts.style.display = "";
  facts.innerHTML = `<dl class="facts">
      <dt>Geometry</dt><dd>${h(t.geometryType)} in <code>${h(t.geometryColumn)}</code></dd>
      <dt>SRID</dt><dd>${h(t.srid)}</dd>
      <dt>Object id</dt><dd>${t.objectIdColumn
        ? `<code>${h(t.objectIdColumn)}</code>`
        : `<span class="bad-inline">none — see below</span>`}</dd>
      <dt>Writable</dt><dd>${t.writable ? "yes" : "read only"}</dd>
    </dl>`
    + (t.objectIdColumn
      ? `<p class="hint">The probe found <code>${h(t.objectIdColumn)}</code> as an integer
         object-id column. <button type="button" class="ghost tiny" id="rUseOid">Nominate
         ${h(t.objectIdColumn)}</button> — or name a different column, if identity and the ArcGIS
         object id are not the same thing in this table.</p>`
      : `<p class="hint bad-inline">This table has no integer object-id column, so the layer will
         publish and will <b>not</b> be servable through the ArcGIS surface — ADR-013 §2a. It
         stays servable natively. The identity column is still required, and per Q-57 a table
         keyed by UUID or text is exactly the case that needs DDL before a client can read
         it.</p>`);

  // Wired here rather than once at open, because the paragraph holding it is
  // rebuilt for every table.
  const use = $("rUseOid");
  if (use) use.onclick = () => { $("rIdentity").value = t.objectIdColumn; };
}

async function publishRegistered(event) {
  event.preventDefault();

  const t = probed.get($("rTable").value);
  if (!t) { toast("Choose a table to publish."); return; }

  const into = splitService($("rService").value);

  const created = await api("/admin/layers", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: $("rName").value.trim(),
      dataSourceId: $("rSource").value,
      schemaName: t.schemaName,
      tableName: t.tableName,
      geometryColumn: t.geometryColumn,
      identityColumn: $("rIdentity").value.trim(),
      objectIdColumn: t.objectIdColumn,
      srid: t.srid,
      geometryType: t.geometryType,
      sharing: $("rShare").value,
      // Bare name: the publish endpoint takes no folder for the target service.
      serviceName: into.name || null,
    }),
  });

  reportCreated("Published", [
    `<b>${h(created.name)}</b> is layer ${created.layerId} of service
     <code>${h(created.service)}</code>, shared ${h(created.sharing)}.`,
    created.arcGisServable
      ? `<a href="/rest/services/${h(created.service)}/FeatureServer/${created.layerId}?f=json"
          target="_blank" rel="noreferrer">the layer document</a>`
      : `<span class="bad-inline">Not servable through the ArcGIS surface.</span>`,
    created.note ? `<span class="val">${h(created.note)}</span>` : "",
  ]);

  places = null;                       // the directory changed
  await section("layers", loadLayers, "layers");
  await section("service names", fillServiceChoices);
}

async function createService(event) {
  event.preventDefault();

  const folder = $("cFolder").value.trim();
  const created = await api("/admin/featureservices", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: $("cName").value.trim(),
      folder: folder || null,
      description: $("cDesc").value.trim() || null,
      sharing: $("cShare").value,
    }),
  });

  reportCreated("Service created", [
    `<b>${h(created.name)}</b> at <code>${h(created.url)}</code>, shared ${h(created.sharing)}.`,
    // The server's own next-step note, which names the two calls that fill it.
    created.note ? `<span class="val">${h(created.note)}</span>` : "",
  ]);

  // Pre-fill the group form, because creating an empty service and then adding a
  // group to it is the sequence the note just described.
  $("gService").value = folder ? `${folder}/${created.name}` : created.name;

  places = null;
  await section("service names", fillServiceChoices);
}

async function createGroupLayer(event) {
  event.preventDefault();

  const where = splitService($("gService").value);
  if (!where.name) { toast("Name the service the group goes in."); return; }

  const parent = $("gParent").value.trim();

  const created = await api(
    `/admin/services/${encodeURIComponent(where.name)}/groups`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: $("gName").value.trim(),
        folder: where.folder,
        parentLayerId: parent === "" ? null : Number(parent),
      }),
    });

  reportCreated("Group created", [
    `<b>${h(created.name)}</b> is layer ${created.layerId} of
     <code>${h($("gService").value.trim())}</code>${created.parentLayerId >= 0
       ? `, nested under layer ${created.parentLayerId}` : ", at the top level"}.`,
    `<span class="val">Publish a layer into this service and name ${created.layerId} as the
     parent to put it inside this group.</span>`,
  ]);

  places = null;
}

function reportCreated(title, lines) {
  const box = $("newResult");
  box.style.display = "";
  box.innerHTML = `<h3>${h(title)}</h3>` + lines.filter(Boolean).join("<br>");
}

function addFieldRow(name = "", type = "Text") {
  const row = document.createElement("tr");
  row.innerHTML =
    `<td><input type="text" class="fName" value="${h(name)}" placeholder="inspector"></td>
     <td><select class="fType">${FIELD_TYPES.map(t =>
       `<option${t === type ? " selected" : ""}>${t}</option>`).join("")}</select></td>
     <td style="text-align:center"><input class="fReq" type="checkbox"></td>
     <td><button type="button" class="ghost tiny fDel">Remove</button></td>`;
  row.querySelector(".fDel").onclick = () => row.remove();
  $("dFields").append(row);
}

// Reports what the server said rather than "done". The import response carries
// the row count, the inferred field types and the reprojection note — all of
// which are things somebody uploading a file wants to check before trusting it.
function reportNew(created) {
  const fields = (created.fields || [])
    .map(f => `${h(f.name)} <span class="val">${h(f.type)}</span>`).join(", ");
  const box = $("newResult");
  box.style.display = "";
  box.innerHTML =
    `<h3>Created</h3>
     <b>${h(created.name)}</b> — ${num(created.rows)} row${created.rows === 1 ? "" : "s"},
     table <code>${h(created.table)}</code>.<br>
     ${fields ? `Fields: ${fields}.<br>` : ""}
     ${created.storedIn && created.storedIn.sourceSR !== created.storedIn.storedSR
        ? `<span class="val">${h(created.storedIn.note)}</span><br>` : ""}
     <a href="${h(created.services.feature)}?f=json" target="_blank" rel="noreferrer">feature service</a>
     · <a href="${h(created.services.tiles)}?f=json" target="_blank" rel="noreferrer">tile service</a>`;
}

async function createDesigned(event) {
  event.preventDefault();
  const fields = [...document.querySelectorAll("#dFields tr")].map(row => ({
    name: row.querySelector(".fName").value.trim(),
    type: row.querySelector(".fType").value,
    nullable: !row.querySelector(".fReq").checked,
  })).filter(f => f.name);

  try {
    reportNew(await api("/admin/hosted/define", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: $("dName").value.trim(),
        geometryType: $("dGeom").value,
        sharing: $("dShare").value,
        fields,
      }),
    }));
    $("dName").value = "";
    $("dFields").innerHTML = "";
    addFieldRow();
    await loadLayers();
  } catch (e) {
    // The server's message is the useful part — a name in use, a reserved
    // field, a type it does not know.
    const box = $("newResult");
    box.style.display = "";
    box.innerHTML = `<h3>Refused</h3>${h(e.message || String(e))}`;
  }
}

async function createImported(event) {
  event.preventDefault();
  const file = $("iFile").files[0];
  if (!file) return;

  const body = new FormData();
  body.append("name", $("iName").value.trim());
  body.append("sharing", $("iShare").value);
  body.append("file", file);

  // No Content-Type header: the browser sets it with the multipart boundary,
  // and setting it by hand produces a body the server cannot parse.
  try {
    reportNew(await api("/admin/hosted/import", { method: "POST", body }));
    $("iName").value = "";
    $("iFile").value = "";
    await loadLayers();
  } catch (e) {
    const box = $("newResult");
    box.style.display = "";
    box.innerHTML = `<h3>Refused</h3>${h(e.message || String(e))}`;
  }
}

// -------------------------------------------------------------------- actions

/**
 * Every click, and no branch of it may fail in silence.
 *
 * <b>The wrapper is the point.</b> This handler is `async`, so a rejection inside
 * any branch becomes an unhandled promise rejection: the browser logs it to a
 * console nobody has open and the page does nothing at all. Some branches had
 * their own `try`; the tiles branch did not, which is how a hanging map read as a
 * dead button. Guarding the whole handler rather than that one branch is
 * deliberate — D-46 is the register entry for fixing the instance and leaving the
 * class, and a new branch added later inherits this instead of remembering it.
 */
document.addEventListener("click", async event => {
  try {
    await handleClick(event);
  } catch (e) {
    toast(e.message || String(e));
  }
});

async function handleClick(event) {
  const t = event.target;
  const d = t.dataset || {};

  if (t.closest("nav") && d.tab) { openTab(d.tab); return; }
  if (t.id === "newLayer") { openNewLayer(); return; }
  if (t.id === "drawerClose") { closeDrawer(); return; }

  if (t.id === "basemapSet" || t.id === "basemapClear") {
    const url = t.id === "basemapClear" ? "" : $("basemapInput").value.trim();

    // A tile template has to carry all three, or every request asks for the same
    // tile and the map looks broken in a way that is hard to read.
    if (url && !["{level}", "{col}", "{row}"].every(token => url.includes(token))) {
      toast("A tile template needs {level}, {col} and {row} — those are what the map "
        + "substitutes per tile.");
      return;
    }

    if (url) localStorage.setItem(BASEMAP_KEY, url);
    else localStorage.removeItem(BASEMAP_KEY);

    if (view) await resetBasemap();
    else drawBasemapControl();
    return;
  }

  if (t.id === "opsRefresh") {
    t.disabled = true;
    await section("operations", loadOperations);
    await section("health", refreshHealth);
    t.disabled = false;
    return;
  }

  // Asked for rather than run on entry. It is three requests per layer against
  // our own server, and a report that quietly generates load every time somebody
  // clicks a tab is a report people learn to avoid.
  if (t.id === "anonRun") {
    t.disabled = true;
    await section("the anonymous view", loadAnonymous, "anonRows");
    t.disabled = false;
    return;
  }

  // Sorting is local: the listing is already in hand, so re-ordering it must not
  // cost a request or lose the drawer that is open.
  if (t.matches("th button.sort")) {
    order = order.key === d.sort ? { key: d.sort, down: !order.down } : { key: d.sort, down: false };
    drawLayers();
    return;
  }

  const pick = t.closest("tr[data-pick]");
  if (pick && !d.show && !d.tiles) { openLayer(pick.dataset.pick); return; }

  if (d.tiles) {
    if (shown.has(tileKey(d.tiles))) hide(tileKey(d.tiles));
    else await showTiles(d.tiles);
    await loadLayers();
    return;
  }

  if (d.show) {
    if (shown.has(d.show)) { hide(d.show); await loadLayers(); return; }
    t.disabled = true;
    try {
      // The geometry type comes from the layer document, which is what the SDK
      // will read anyway — asking first only chooses the symbol.
      const doc = await api(
        `${serviceRoot(layerNamed(d.show)).replace(location.origin, "")}/FeatureServer/0?f=json`);
      await show(d.show, doc.geometryType);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.toggle) {
    t.disabled = true;
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.toggle)}/${d.to}`, { method: "POST" });
      if (r.to === "stopped") hide(d.toggle);
      toast(`${d.toggle}: ${r.from} → ${r.to}. ${r.note}`, true);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.cache) {
    const seconds = d.clear ? null : Number($("ttl").value);
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.cache)}/cache`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seconds: d.clear ? null : seconds }),
      });
      toast(r.note, true);
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.refresh) {
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.refresh)}/refresh`,
        { method: "POST" });
      // The server's own note, which says what was forgotten and how many tiles
      // went with it. Writing our own sentence here would drop the tile count.
      toast(`${d.refresh}: ${r.note}`, true);
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.delete) {
    // A confirmation, because this is the one action here that destroys
    // something. The name has to be right: a mis-click on the wrong row of a
    // table of similar names is exactly how the wrong layer goes.
    if (!confirm(`Delete "${d.delete}"? This removes the publication`
        + `${layerNamed(d.delete).hosted ? " and its hosted table" : ""}.`)) return;
    try {
      await api(`/admin/layers/${encodeURIComponent(d.delete)}`, { method: "DELETE" });
      hide(d.delete);
      closeDrawer();
      toast(`${d.delete} deleted.`, true);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.style) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.style)}/style`);
      // <b>Two different bodies from one endpoint, and putting the wrong one in
      // the box would be a trap.</b> With a style stored, the response *is* the
      // document, byte for byte. With none stored it is a wrapper saying so —
      // and pasting that wrapper back as a style is what would happen if this
      // filled the box either way.
      if (r && r.stored === false) {
        $("styleDoc").value = "";
        toast(r.note || "No style stored; a generated one is served.", true);
      } else {
        $("styleDoc").value = JSON.stringify(r, null, 1);
      }
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.stylePut) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.stylePut)}/style`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: $("styleDoc").value,
      });
      toast(`${r.name}: ${r.replaced ? "style replaced" : "style stored"}, ${num(r.bytes)} bytes.`, true);
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.styleDel) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.styleDel)}/style`,
        { method: "DELETE" });
      $("styleDoc").value = "";
      toast(r.note || "Back to the generated style.", true);
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.probe) {
    t.disabled = true;
    try { renderProbe(d.probeName, await api(`/admin/datasources/${d.probe}/capability`)); }
    catch (e) { toast(e.message); }
    t.disabled = false;
    return;
  }

  if (t.id === "sTest" || t.id === "sAdd") {
    event.preventDefault();
    const body = { name: $("sName").value.trim(), connectionString: $("sConn").value };
    const path = t.id === "sTest" ? "/admin/datasources/test" : "/admin/datasources";
    t.disabled = true;
    try {
      const r = await api(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      // The test answers 200 even when the source is unusable: the request
      // succeeded and the answer is "no". So the outcome is read from the body.
      $("sResult").innerHTML = `<div class="row" style="margin:0">${
        pill(r.outcome || (r.name ? "registered" : "done"))}<span style="font-size:13.5px">${
        h(r.message || `Registered as ${r.name}.`)}</span></div>`;
      if (t.id === "sAdd") { $("sName").value = ""; $("sConn").value = ""; await loadSources(); }
    } catch (e) { $("sResult").innerHTML = `<div class="row" style="margin:0">${
      pill("unusable")}<span style="font-size:13.5px">${h(e.message)}</span></div>`; }
    t.disabled = false;
  }
}

document.addEventListener("change", async event => {
  const d = event.target.dataset || {};

  if (d.share) {
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.share)}/sharing`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sharing: event.target.value }),
      });
      toast(`${d.share}: shared ${r.from} → ${r.to}`, true);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.serviceShare) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.serviceShare)}/sharing`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sharing: event.target.value }),
      });
      // This one answers {name, sharing} rather than the {from, to} a layer's
      // sharing returns, so it says the new scope and not the transition.
      toast(`${r.name}: now shared ${r.sharing}`, true);
    } catch (e) { toast(e.message); }
    await loadSystemServices();
  }
});

document.addEventListener("input", event => {
  if (event.target.id !== "layerFilter") return;
  filter = event.target.value;
  drawLayers();
});

document.addEventListener("keydown", event => {
  if (event.key === "Escape") {
    // Escape clears a filter before it closes the drawer, because the filter is
    // the thing you are most likely to be holding when you press it.
    if (document.activeElement && document.activeElement.id === "layerFilter"
        && $("layerFilter").value !== "") {
      $("layerFilter").value = "";
      filter = "";
      drawLayers();
      return;
    }
    if ($("drawer").classList.contains("on")) closeDrawer();
  }
});

// ----------------------------------------------------------------------- boot

async function whoami() {
  const me = await api("/rest/whoami");
  $("who").innerHTML = me.authenticated
    ? `<b>${h(me.name)}</b> · ${h(me.roles.join(", ") || "no roles")} · ${h(me.userType)}`
    : "anonymous";
  return me;
}

$("signinForm").addEventListener("submit", async event => {
  event.preventDefault();

  const button = $("go");
  const error = $("signinError");
  error.hidden = true;
  button.disabled = true;
  button.textContent = "Signing in…";

  try {
    const r = await api("/rest/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: $("u").value, password: $("p").value }),
    });
    token = r.token;
    sessionStorage.setItem("gis-token", token);
    await start();
  } catch (e) {
    // The server's own words. A refused sign-in says whether the name is
    // unknown, the password wrong, or the account throttled, and each sends the
    // reader somewhere different.
    error.textContent = e.message || String(e);
    error.hidden = false;
    $("p").select();
  } finally {
    button.disabled = false;
    button.textContent = "Sign in";
  }
});

/**
 * Signs out, and says so if it could not.
 *
 * <b>The refusal used to be swallowed</b> — `catch { /* already gone *\/ }` — which
 * is exactly wrong here, because a failed logout is the one case that produces the
 * symptom the owner reported on 2026-08-16: *"it does not sign out, it comes back
 * to the same page."* The session is carried in two forms, a bearer token this
 * page holds and a `gis-session` cookie the browser holds and script cannot read
 * (`httponly`). Only the server can end the second, so if the request fails and
 * the page reloads anyway, the cookie signs the operator straight back in and the
 * console looks like the button does nothing.
 *
 * So the order is: ask the server first, and only clear local state and leave once
 * it has answered. If it refuses, stay and say why — a console that cannot sign
 * out must not pretend it did.
 */
$("signout").addEventListener("click", async event => {
  const button = event.currentTarget;
  button.disabled = true;

  try {
    await api("/rest/auth/logout", { method: "POST" });
  } catch (e) {
    button.disabled = false;
    toast(`Could not sign out: ${e.message || e}. The session is still open — the `
      + `browser's session cookie can only be cleared by the server.`);
    return;
  }

  token = null;
  sessionStorage.removeItem("gis-token");

  // <b>Replaced rather than reloaded.</b> A reload re-runs the same URL and may be
  // served from cache; `replace` also takes the signed-out state out of the back
  // button, so Back cannot return to a rendered console the caller can no longer
  // read anything into.
  location.replace(location.pathname);
});

async function start() {
  const me = await whoami();
  if (!me.authenticated) {
    $("signin").style.display = "";
    $("app").style.display = "none";
    $("tabs").style.display = "none";
    return;
  }

  $("signin").style.display = "none";
  $("app").style.display = "";
  $("tabs").style.display = "";
  $("signout").style.display = "";
  drawLegend();
  drawBasemapControl();

  // <b>Each section fails on its own.</b> This was one Promise.all, so a single
  // refused endpoint rejected the whole boot and the console showed nothing at
  // all — the same shape of failure as the CSP one (D-44): a small fault
  // presenting as a dead page, with the reader given no way to tell which. Now a
  // section that cannot load says so in its own place and the rest still works,
  // which is also how somebody diagnoses a half-broken server.
  await Promise.all([
    section("health", refreshHealth),
    section("layers", loadLayers, "layers"),
    section("system services", loadSystemServices, "systemServices"),
    section("data sources", loadSources, "sources"),
  ]);
}

// ------------------------------------------------------- the anonymous view

/**
 * One request as a caller with no credential would make it.
 *
 * <b>This whole surface rests on omitting one header.</b> `api()` attaches the
 * session token to everything, which is right for a console and wrong for the
 * only question asked here. ADR-015 §3 makes a session an opaque bearer token
 * rather than a cookie, so there is nothing else to suppress — `credentials:
 * "omit"` is there anyway, so that moving to a cookie session later cannot
 * quietly turn this tab into a liar.
 *
 * The status is returned rather than thrown on. Every other caller in this file
 * wants a refusal to be an error; here a refusal is the measurement.
 */
async function anon(path) {
  try {
    const response = await fetch(path, { credentials: "omit" });
    return { status: response.status, ok: response.ok };
  } catch (e) {
    // A dropped connection is not a refusal, and reporting it as one would
    // invent an authorization result out of a network failure.
    return { status: 0, ok: false, error: e.message || String(e) };
  }
}

// What each code means to the person reading the table. The 404 line is the
// important one: ADR-018's refusal is deliberately identical for a layer that
// does not exist and one that exists and is not shared, so this column cannot
// be read as "missing" — which is exactly the mistake D-45 recorded.
const MEANING = {
  0: "no answer — the request did not complete",
  200: "readable with no credential",
  401: "a credential is asked for",
  403: "refused, and the reason is authorization",
  404: "not found, or found and not shared — deliberately the same answer",
  503: "the service is stopped",
};

/**
 * What the catalogue intended, against what an anonymous caller actually got.
 *
 * Only disagreement is marked. A table where every cell is coloured tells the
 * reader nothing about which row to look at, and the point of this screen is
 * that most rows are boring.
 */
function reality(layer, results) {
  if (layer.status === "stopped") {
    return { bad: false, text: "stopped — nothing is expected to answer" };
  }
  const anyReadable = results.some(r => r.ok);
  if (layer.sharing === "public") {
    return results.every(r => r.ok)
      ? { bad: false, text: "as intended" }
      : { bad: true, text: "shared public, and not readable" };
  }
  // private and organization are alike from out here: an anonymous caller is
  // not in the organization either.
  return anyReadable
    ? { bad: true, text: `shared ${layer.sharing}, and readable without a credential` }
    : { bad: false, text: "as intended" };
}

const codeCell = r =>
  `<td class="num" title="${h(r.error || MEANING[r.status] || "")}">${
    r.status || "—"}</td>`;

/**
 * Probes every catalogued layer as an anonymous client and reports the two
 * mismatches that matter.
 *
 * <b>The mapping is resolved with your session and the probe is made without
 * it</b>, and that split is the design: you cannot ask what a stranger sees at
 * a URL you were unable to find. Group layers are skipped for the same reason
 * `resolvePlaces` skips them — they hold no features, so a count query against
 * one is not a question about sharing.
 */
async function loadAnonymous() {
  const body = $("anonRows");
  const places = await resolvePlaces();
  const layers = known.filter(l => places.has(l.name));
  const skipped = known.length - layers.length;

  $("anonSummary").innerHTML = "";
  body.innerHTML = `<tr><td colspan="6" class="empty">Probing ${layers.length} layers…</td></tr>`;

  const rows = [];
  // Four at a time. Ninety-odd requests fired at once is a load test of our own
  // server dressed up as a report, and it would make the slowest row look like
  // a refusal.
  for (let i = 0; i < layers.length; i += 4) {
    const batch = layers.slice(i, i + 4);
    rows.push(...await Promise.all(batch.map(async layer => {
      const place = places.get(layer.name);
      const base = `/rest/services/${place.service}/FeatureServer`;
      const results = await Promise.all([
        anon(`${base}?f=json`),
        anon(`${base}/${place.id}?f=json`),
        anon(`${base}/${place.id}/query?where=1%3D1&returnCountOnly=true&f=json`),
      ]);
      return { layer, place, results, said: reality(layer, results) };
    })));
    body.innerHTML = `<tr><td colspan="6" class="empty">Probing ${
      Math.min(i + 4, layers.length)} of ${layers.length}…</td></tr>`;
  }

  rows.sort((a, b) => (b.said.bad ? 1 : 0) - (a.said.bad ? 1 : 0)
    || a.layer.name.localeCompare(b.layer.name));

  body.innerHTML = rows.map(({ layer, place, results, said }) => `
    <tr>
      <td><code>${h(layer.name)}</code><br><span class="val">${h(place.service)} · ${place.id}</span></td>
      <td>${pill(layer.sharing)}</td>
      ${results.map(codeCell).join("")}
      <td${said.bad ? ' class="bad-inline"' : ' class="val"'}>${h(said.text)}</td>
    </tr>`).join("")
    || `<tr><td colspan="6" class="empty">No layer resolved to a service, so nothing was probed.</td></tr>`;

  const exposed = rows.filter(r => r.said.bad && r.layer.sharing !== "public").length;
  const unreachable = rows.filter(r => r.said.bad && r.layer.sharing === "public").length;

  $("anonSummary").innerHTML = [
    exposed
      ? `<p class="bad-inline"><b>${exposed}</b> layer${exposed === 1 ? " is" : "s are"} readable
         without a credential while shared private or organization.</p>`
      : `<p class="val">Nothing shared private or organization answered an anonymous caller.</p>`,
    unreachable
      ? `<p class="bad-inline"><b>${unreachable}</b> layer${unreachable === 1 ? " is" : "s are"}
         shared public but did not answer — an ArcGIS client would see this as the layer missing.</p>`
      : `<p class="val">Every public layer answered.</p>`,
    skipped
      ? `<p class="val">${skipped} catalogued layer${skipped === 1 ? "" : "s"} could not be located in
         the services directory and ${skipped === 1 ? "was" : "were"} not probed — a stopped service is
         absent from the directory, so this is not by itself a fault. It is also the gap ADR-020 §2
         records: the admin listing does not carry the service and index.</p>`
      : "",
  ].join("");
}

/**
 * Runs a loader and, if it fails, reports it where it happened.
 *
 * The toast carries the server's message because it is the useful part; the
 * placeholder is there so the empty table is not read as "nothing published".
 */
async function section(what, load, placeholder) {
  try {
    await load();
  } catch (e) {
    toast(`${what}: ${e.message || e}`);
    if (placeholder) {
      const target = $(placeholder);
      if (target) {
        target.innerHTML =
          `<tr><td colspan="9" class="empty">Could not load ${h(what)}: ${h(e.message || e)}
           <br>This is not an empty list — the request was refused.</td></tr>`;
      }
    }
    return null;
  }
}

start().catch(e => toast(e.message));
