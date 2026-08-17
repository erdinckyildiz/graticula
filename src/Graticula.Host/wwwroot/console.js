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

const SCOPES = ["private", "organization", "public"];

// <b>A fallback, not a palette.</b> This list used to be where a layer's colour came
// from; since ADR-033 the server publishes one in the layer document and this console
// reads it. The first entry is used only when a document arrives without `drawingInfo` —
// an older server, or a layer whose document could not be read.
const PALETTE = ["#0b6157", "#a63a2b", "#1f5fa8", "#92620d", "#6b3fa0", "#2f7a55"];
const TILE_COLOUR = "#8fb8cc";
const shown = new Map();     // layer name -> { colour, layer }
let esri = null;             // the SDK modules, loaded once
let view = null;

let known = [];              // last /admin/layers listing — Server only
let content = new Map();     // name -> the /content/layers entry, which carries its address
let selected = null;         // the row the table marks: the layer last opened
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

// -------------------------------------------------------------------- surfaces

/**
 * Two surfaces over one API — ADR-034.
 *
 * <b>Server is for the operator and Studio is for the publisher</b>, and the gate is a
 * privilege the API already enforces rather than a rule invented here: Server needs
 * `admin:manageServer`, Studio needs a session. A reader without it gets no Server tab, no
 * Server link, and `#/server/anything` answers with a sentence instead of four refusals —
 * which is what the single console did, one screen at a time.
 *
 * Each surface owns its tab strip, and the strip is built rather than written down, because a
 * tab a reader cannot use must not be in the document at all.
 */
const SURFACES = {
  server: {
    title: "Server",
    needs: "admin:manageServer",
    home: "services",
    tabs: [
      ["services", "Services"],
      ["operations", "Operations"],
      ["anonymous", "Anonymous view"],
    ],
    action: null,
  },
  studio: {
    title: "Studio",
    needs: null,
    home: "content",
    tabs: [
      ["content", "My content"],
      ["sources", "Data sources"],
    ],
    action: { id: "newLayer", label: "New layer" },
  },
};

/** Which surface a screen belongs to, so an old address can be translated. */
const WAS = {
  services: "server/services",
  operations: "server/operations",
  anonymous: "server/anonymous",
  sources: "studio/sources",
  layer: "server/layer",
};

let privileges = new Set();

const may = privilege => !privilege || privileges.has(privilege);

/** Which surfaces this reader may enter, in order. */
const allowed = () => Object.keys(SURFACES).filter(name => may(SURFACES[name].needs));

/**
 * The address, read.
 *
 * <b>The hash carries the surface as its first segment</b> — `#/server/services`,
 * `#/studio/content` — so a link is unambiguous about which environment it opens. Addresses
 * from before the split are translated rather than 404'd: ADR-020 §5c took *frozen URLs* from
 * the reference as a rule, and this breaks it once, deliberately, with a redirect.
 */
function route() {
  const parts = location.hash.replace(/^#\/?/, "").split("/").filter(Boolean)
    .map(decodeURIComponent);

  // An address from before ADR-034: translate and replace, so Back does not bounce.
  if (parts.length && !(parts[0] in SURFACES) && WAS[parts[0]]) {
    location.replace(
      `#/${WAS[parts[0]]}${parts.length > 1 ? "/" + parts.slice(1).join("/") : ""}`);
    return;
  }

  const surface = parts[0] in SURFACES ? parts[0] : allowed()[0];

  // <b>Refused with a sentence, in Studio.</b> Not a 403 toast over an empty Server screen:
  // the reader cannot be here, so they are somewhere they can be, told why.
  if (!may(SURFACES[surface].needs)) {
    toast(`${SURFACES[surface].title} is for administering this server, which needs `
      + `${SURFACES[surface].needs}. You are in Studio, where your own content is.`);
    location.replace("#/studio/" + SURFACES.studio.home);
    return;
  }

  drawSurfaces(surface);

  const rest = parts.slice(1);

  if (rest[0] === "layer" && rest[1]) {
    showLayer(rest[1], EDIT_PAGES.includes(rest[2]) ? rest[2] : EDIT_PAGES[0]);
    return;
  }

  // A service, and what is in it. The address carries the folder because a service is
  // addressed by folder and name — `#/server/service/turkiye/tr_ref`.
  if (rest[0] === "service" && rest[1]) {
    showService(rest.slice(1).join("/"));
    return;
  }

  const screens = SURFACES[surface].tabs.map(([name]) => name);
  const screen = screens.includes(rest[0]) ? rest[0] : SURFACES[surface].home;

  // The folder a Server services screen is looking at, which is part of its address so that
  // "the services in turkiye" is a place you can link somebody to.
  openScreen(surface, screen, screen === "services" ? rest[1] ?? null : null);
}

window.addEventListener("hashchange", route);

/** Draws the header's surface switch and the surface's own tab strip. */
function drawSurfaces(surface) {
  const both = allowed();

  $("surfaces").hidden = both.length < 2;
  for (const link of document.querySelectorAll("#surfaces a")) {
    if (link.dataset.surface === surface) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }

  const config = SURFACES[surface];

  $("tabs").innerHTML =
    config.tabs.map(([name, label]) =>
      `<a href="#/${surface}/${name}" data-tab="${name}">${h(label)}${
        name === "services" ? '<span class="count" id="cServices"></span>' : ""}${
        name === "sources" ? '<span class="count" id="cSources"></span>' : ""}</a>`).join("")
    + (config.action
      ? `<span class="right"><button class="primary" id="${config.action.id}">${
          h(config.action.label)}</button></span>`
      : "");
}

/**
 * Goes where the map is, if we are not already there.
 *
 * <b>Drawing something you cannot see is the same as a button doing nothing.</b> The map
 * belongs to Studio's content screen (ADR-034 §5d), so Show and Tiles pressed from a layer's
 * settings page in Server used to add a layer to a map on another screen and leave the
 * operator looking at an unchanged page. Asking to see something is a good enough reason to be
 * taken to it.
 */
function toMap() {
  if (!location.hash.startsWith("#/studio/content")) location.hash = "#/studio/content";
}

/** Shows one view, and marks its tab. */
function showView(id, tab) {
  for (const link of document.querySelectorAll("#tabs a[data-tab]")) {
    if (link.dataset.tab === tab) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  for (const view of document.querySelectorAll(".view")) {
    view.classList.toggle("on", view.id === id);
  }
}

/**
 * Opens a screen, and re-reads what it shows.
 *
 * Re-read on entry, because a screen showing numbers from when the page was opened is worse
 * than one that says how old they are.
 */
function openScreen(surface, screen, folder) {
  // The editing session ends with the page it was on. Leaving it open would let a background
  // refresh redraw a screen nobody is looking at.
  editing = null;

  showView("view-" + screen, screen);

  if (screen === "services") {
    selectedFolder = folder;
    section("folders", loadFolders);
    section("services", loadServices, "services");
  }

  if (screen === "content") section("your content", loadMyContent, "mine");
  if (screen === "operations") section("operations", loadOperations);
  if (screen === "sources") section("data sources", loadSources, "sources");
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
        await show(name, doc);
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

/**
 * The colour the server says this layer is, for the legend swatch.
 *
 * <b>Read, not chosen — ADR-033 §5b.</b> This console used to pick from a palette of its
 * own, so a layer was one colour here, another in the tile style and a third in whatever
 * client somebody else was using. The layer document now carries `drawingInfo`, the SDK
 * draws the layer from it without being told to, and the only thing left for this file to
 * do is read the same colour into the legend so the swatch matches the map.
 *
 * Esri colours are `[r, g, b, a]` with the channels in 0–255.
 */
function serverColour(doc) {
  const rgba = doc?.drawingInfo?.renderer?.symbol?.color;
  if (!Array.isArray(rgba) || rgba.length < 3) return PALETTE[0];

  return "#" + rgba.slice(0, 3).map(c => Number(c).toString(16).padStart(2, "0")).join("");
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
    `<span title="The colour the server publishes for this layer"><i class="swatch"
       style="background:${h(s.colour)}"></i><b>${h(name)}</b></span>`).join("")
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
  // <b>The content listing first, because it came with the address.</b> Studio's reader may
  // have no administrative privilege at all, and `resolvePlaces` walks the services directory
  // to answer a question `/content/layers` already answered in the row being drawn.
  const entry = content.get(name);
  if (entry) return `${location.origin}${entry.url}`;

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

/**
 * Draws a layer, with **no renderer of our own**.
 *
 * <b>Passing no `renderer` is the change, and it is the point.</b> The SDK then reads
 * `drawingInfo` from the layer document — so what this console shows is what the server
 * told every client, and if the two ever disagree it is visible here first. Before
 * 2026-08-17 this file built its own symbol from a local palette, which meant the console
 * was the one viewer guaranteed *not* to show what anybody else saw.
 *
 * The document is passed in because the caller already fetched it to decide the geometry
 * type; asking for it again to read a colour would be a second request for a fact in hand.
 */
async function show(name, doc) {
  const { FeatureLayer } = await loadEsri();
  const mapView = await ensureMap();
  const colour = serverColour(doc);

  const layer = new FeatureLayer({
    url: await layerUrl(name),
    title: name,
    outFields: ["*"],
    popupTemplate: { title: name, content: "{*}" },
  });

  clearMap();
  mapView.map.add(layer);
  shown.set(name, { colour, layer });
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

// <b>The sort and filter state went with the table.</b> Server lists the services in a folder
// now (ADR-034 §5h), and the flat all-layers table this replaces owned the ordering, its ORDER
// map and a search across name, table, source and owner. That search is the one thing the new
// shape cannot do, and §5h records it rather than dropping it quietly.

/**
 * The administrative catalogue, which is what the layer editor is edited against.
 *
 * <b>It draws nothing since ADR-034.</b> The flat all-layers table it used to fill is gone:
 * Server lists services in a folder and layers appear when a service is opened (§5h). What is
 * still needed is `known` — the editor asks it whether a layer is hosted, who owns it, what
 * table is under it — so this keeps reading and stops rendering.
 *
 * It needs `admin:viewAllContent`, so it is only called on the Server surface. Studio reads
 * `/content/layers` instead, which is the listing §5f had to add.
 */
async function loadLayers() {
  const { layers } = await api("/admin/layers");
  known = layers;
  places = null;   // a start, stop, publish or delete moves what the directory holds

  // The service list moves whenever the layer list does: publishing creates a service
  // and unpublishing the last layer empties one, and an emptied service that still shows
  // "1 layer" would offer a Delete that refuses.
  section("services", loadServices, "services");

  if (!editing) return;

  // The layer being edited stopped existing — deleted here, or by somebody else.
  // Sitting on a page for it would offer to save settings onto nothing.
  if (!layers.some(l => l.name === editing.name)) {
    selected = null;
    editing = null;
    location.hash = "#/server/services";
    return;
  }
  redrawLayerPage();
}

/**
 * Redraws the open editor after the listing moved, keeping what has been typed.
 *
 * <b>A refresh must not eat an unsaved figure.</b> Start, Stop, Map and Tiles all
 * re-read `/admin/layers`, and the editor's General page shows the state they
 * change — so it has to be redrawn. Redrawing it from the server alone would
 * silently discard a limit half-entered on another page, which is the same class
 * of quiet loss as a control that displays a value it never read.
 */
function redrawLayerPage() {
  const pending = editedValues();
  const { name, page } = editing;
  editing = null;                // so showLayer rebuilds rather than flipping pages
  showLayer(name, page, pending);
}

/**
 * What this reader owns and what is shared with them — Studio's screen, from
 * `GET /content/layers` (ADR-034 §5f).
 *
 * <b>It carries the address, so nothing here has to guess one.</b> The administrative listing
 * does not say which service holds a layer or at which index — D-45 — and the console resolves
 * that through the services directory. This listing answers it directly, so Studio needs no
 * `admin:` privilege to draw a map: the URL it draws from came with the row.
 */
async function loadMyContent() {
  const { mine, shared, note } = await api("/content/layers");

  content = new Map([...(mine || []), ...(shared || [])].map(e => [e.name, e]));

  const row = (e, last) => {
    const isShown = shown.has(e.name);
    const stopped = e.status === "stopped";
    const swatch = isShown
      ? `<i class="swatch" style="background:${h(shown.get(e.name).colour)}"></i>` : "";

    return `<tr class="pick" data-pick="${h(e.name)}">
      <td class="acts">${swatch}<button class="tiny ${isShown ? "on" : ""}"
        data-show="${h(e.name)}" ${stopped
          ? "disabled title='A stopped service answers 503, so there is nothing to draw.'"
          : ""}>${isShown ? "Hide" : "Map"}</button>${e.hosted
        ? `<button class="tiny ${shown.has(tileKey(e.name)) ? "on" : ""}"
             data-tiles="${h(e.name)}" ${stopped ? "disabled" : ""}>Tiles</button>` : ""}</td>
      <td class="name">${h(e.name)}</td>
      <td>${pill(e.hosted ? "hosted" : "registered")}${
        // The folder, but not when it merely repeats the word beside it: every hosted layer
        // is in `hosted`, so saying both is noise in a column that has one job.
        e.folder && e.folder !== "hosted"
          ? ` <span class="val">${h(e.folder)}</span>` : ""}</td>
      <td>${pill(e.status)}</td>
      <td>${pill(e.sharing)}</td>
      <td class="val">${last(e)}</td>
    </tr>`;
  };

  const address = e => `<a href="${h(e.url)}?f=json" target="_blank" rel="noreferrer">${
    h(e.service)}/FeatureServer/${e.layerId}</a>`;

  $("mine").innerHTML = (mine || []).length === 0
    ? `<tr><td colspan="6" class="empty">${h(note || "Nothing yet.")} <b>New layer</b> publishes
         one.</td></tr>`
    : mine.map(e => row(e, address)).join("");

  $("sharedWithMe").innerHTML = (shared || []).length === 0
    ? `<tr><td colspan="6" class="empty">Nothing is shared with you by anybody else.</td></tr>`
    : shared.map(e => row(e, x => h(x.because))).join("");
}

/**
 * One service, and the layers it holds.
 *
 * <b>Read from the service document, not from a new admin route.</b> The document is what every
 * ArcGIS client reads to find a service's layers, so this screen and every client agree by
 * construction — and a stopped service refusing it is shown in place, because that refusal is
 * expected rather than a fault.
 */
async function showService(qualified) {
  editing = null;
  showView("view-service", "services");

  const { folder, name } = splitService(qualified);

  $("serviceCrumb").innerHTML =
    `<a href="#/server/services${folder ? "/" + encodeURIComponent(folder) : ""}">Services</a>
     › ${folder ? h(folder) : "root"} › <b>${h(name)}</b>`;
  $("serviceFacts").textContent = "";
  $("serviceLayers").innerHTML = `<tr><td colspan="6" class="empty">reading the service…</td></tr>`;

  try {
    const doc = await api(
      `/rest/services/${qualified.split("/").map(encodeURIComponent).join("/")}/FeatureServer?f=json`);

    const layers = doc.layers || [];

    $("serviceFacts").textContent =
      `${layers.length} layer${layers.length === 1 ? "" : "s"} · max ${num(doc.maxRecordCount)} rows`
      + ` · ${doc.capabilities || "no capabilities"}`;

    $("serviceLayers").innerHTML = layers.length === 0
      ? `<tr><td colspan="6" class="empty">This service holds no layers yet. Publish one into it
           by naming it in the publish form.</td></tr>`
      : layers.map(l => {
        const group = l.type === "Group Layer";
        return `<tr>
          <td class="num">${l.id}</td>
          <td class="name">${h(l.name)}</td>
          <td class="val">${h(l.type)}</td>
          <td class="val">${h((l.geometryType || "").replace("esriGeometry", "") || "—")}</td>
          <td class="val">${l.parentLayerId >= 0 ? `group ${l.parentLayerId}` : "top level"}</td>
          <td style="text-align:right">${group
            ? `<span class="val">a group, not a layer</span>`
            : `<a href="#/server/layer/${encodeURIComponent(l.name)}" class="tiny">Settings</a>`}</td>
        </tr>`;
      }).join("");
  } catch (e) {
    $("serviceLayers").innerHTML =
      `<tr><td colspan="6" class="empty" style="color:var(--stop)">${h(e.message || e)}</td></tr>`;
  }
}

/** Which folder the Server services screen is looking at: null is the root. */
let selectedFolder = null;

/** The filter over the services in that folder. */
let serviceFilter = "";

/**
 * The folder rail — ADR-034 §5h.
 *
 * <b>The root is an entry, not a special case.</b> Their reference shows *Site (root)* at the
 * top of the same list, and a rail that omits the place half the services are is a rail you
 * cannot navigate from. `hosted` and `Utilities` are entries too: `hosted` stopped being a
 * rule when folders became real, and the geometry service is a service in `Utilities`.
 */
async function loadFolders() {
  const { root, folders } = await api("/admin/folders");

  const entry = (name, label, counts, extra = "") => {
    const here = (selectedFolder ?? "") === (name ?? "");
    return `<a href="#/server/services${name ? "/" + encodeURIComponent(name) : ""}"
      class="rail-item${here ? " on" : ""}"${here ? ' aria-current="page"' : ""}>
      <span class="rail-name">${h(label)}${extra}</span>
      <span class="rail-count">${counts}</span></a>`;
  };

  $("folders").innerHTML =
    entry(null, "Site (root)", num(root.services))
    + (folders || []).map(f => entry(
        f.name,
        f.name,
        num(f.services + f.systemServices),
        f.reserved ? ' <span class="val" title="Reserved: this folder is where something the'
          + ' server does lives">·</span>' : "")).join("");
}

/**
 * The services in the selected folder, and what each holds.
 *
 * <b>One list, replacing three tables</b> — the owner's objection to what this used to be
 * (ADR-034 §5h). The system services are in it rather than beside it: the geometry service is
 * a service in `Utilities`, and now that a folder is a thing it can be listed as one.
 */
async function loadServices() {
  const [{ services }, system] = await Promise.all([
    api("/admin/featureservices"),
    api("/admin/services").catch(() => ({ services: [] })),
  ]);

  const inFolder = (folder) => (folder ?? "") === (selectedFolder ?? "");

  const rows = [
    ...(services || []).filter(s => inFolder(s.folder)).map(s => ({
      qualified: s.qualified,
      name: s.name,
      folder: s.folder,
      kind: s.kind,
      status: s.status,
      sharing: s.sharing,
      layers: s.layers,
      groups: s.groups,
      owner: s.owner,
      empty: s.empty,
      description: s.description,
      system: false,
    })),

    // A service with no layers behind it, carrying its own sharing scope — ADR-018 §3b-i. It
    // has no layers to open and no ceilings to set, so its row offers only what it has.
    ...(system.services || []).filter(y => inFolder(y.folder)).map(y => ({
      qualified: y.folder ? `${y.folder}/${y.name}` : y.name,
      name: y.name,
      folder: y.folder,
      kind: y.kind,
      status: "started",
      sharing: y.sharing,
      layers: 0,
      groups: 0,
      owner: null,
      empty: false,
      description: null,
      system: true,
    })),
  ];

  const needle = serviceFilter.trim().toLowerCase();
  const shown_ = needle
    ? rows.filter(r => [r.qualified, r.kind, r.owner].some(v => (v || "").toLowerCase().includes(needle)))
    : rows;

  $("serviceCount").textContent = needle
    ? `${shown_.length} of ${rows.length}`
    : `${rows.length} service${rows.length === 1 ? "" : "s"}`;

  const where = selectedFolder ? `the ${selectedFolder} folder` : "the root";

  $("services").innerHTML = shown_.length === 0
    ? `<tr><td colspan="8" class="empty">${rows.length === 0
        ? `Nothing in ${h(where)}. Publishing a layer creates a service; a folder can hold none.`
        : `Nothing in ${h(where)} matches <b>${h(serviceFilter)}</b>.`}</td></tr>`
    : shown_.map(r => {
      const held = [
        r.layers ? `${r.layers} layer${r.layers === 1 ? "" : "s"}` : "",
        r.groups ? `${r.groups} group${r.groups === 1 ? "" : "s"}` : "",
      ].filter(Boolean).join(", ");

      return `<tr${r.system ? "" : ` class="pick" data-service="${h(r.qualified)}"`}>
        <td class="name">${h(r.name)}${r.description
          ? `<br><span class="val" style="font-weight:400">${h(r.description)}</span>` : ""}</td>
        <td class="val">${h(r.kind)}</td>
        <td>${pill(r.status)}</td>
        <td>${r.system
          ? `<select data-service-share="${h(r.name)}">${SCOPES.map(v =>
              `<option value="${v}"${v === r.sharing ? " selected" : ""}>${v}</option>`).join("")}</select>`
          : pill(r.sharing)}</td>
        <td class="num">${r.system ? "—" : num(r.layers)}</td>
        <td class="num">${r.system ? "—" : num(r.groups)}</td>
        <td class="val">${h(r.owner || "—")}</td>
        <td style="text-align:right">${r.system ? "" : `<button class="tiny danger"
          data-service-delete="${h(r.name)}" data-folder="${h(r.folder || "")}"
          ${r.empty ? "" : `disabled title="It holds ${h(held)}. Unpublish them first — a
            service delete never removes what is in it."`}>Delete</button>`}</td>
      </tr>`;
    }).join("");

  // What opening a service does, said once rather than per row.
  $("serviceNote").innerHTML = shown_.some(r => !r.system)
    ? "Select a service to see its layers and set what it offers."
    : "";
}

// --------------------------------------------------------------------- drawer

/**
 * Closes the drawer, which now holds one thing: Create.
 *
 * <b>It used to hold the layer editor too</b>, and that was the owner's
 * correction on 2026-08-16 — settings pages inside a slide-over inside a console
 * is nested twice over. Creating is a short form you fill and dismiss, so it
 * stays here; editing a service is a place you navigate to, so it left.
 */
function closeDrawer() {
  $("drawer").classList.remove("on");
  $("drawer").setAttribute("aria-hidden", "true");
}

/**
 * The layer editor: its own page, a left column of short settings pages, one Save.
 *
 * <b>The structure is taken from ArcGIS Server Manager, on the owner's reference,
 * and not from taste.</b> Three visual concepts were built and thrown away first;
 * what was wrong with them was the information architecture, not the palette. What
 * their screen does that ours did not:
 *
 * · <b>Settings are paginated, not stacked.</b> General, Capabilities, Pooling,
 *   Caching — each page short enough to read without scrolling. Ours was eight
 *   sections in one column.
 * · <b>A breadcrumb names the object being edited</b> — theirs reads
 *   `Editing: Site (root) > EGDB > _06Z_Wind_Gust_Day_3`, so you always know what
 *   Save will change.
 * · <b>One Save and one Cancel for the session</b>, always visible, rather than a
 *   button beside every control.
 * · <b>Capabilities are names in a grid</b>, seventeen visible at once, with no
 *   prose. A greyed, checked box states a fact — *Mapping (always enabled)* — which
 *   is the same device we need for tiles on a registered layer.
 * · <b>Numbers read as sentences with the unit outside the box</b>: *The maximum
 *   time a client can use a service: [600] seconds*.
 * · <b>Help is a link.</b> Not a paragraph under each control.
 *
 * Their pages do not map one-to-one onto ours and are not forced to: we have no
 * instance pool, so *Pooling* becomes *Limits*, which is where ADR-031's ceilings
 * and Q-113's live.
 *
 * The page names are also the route: each is an address under
 * `#/layer/<name>/<page>`, so the left column is six ordinary links.
 */
const EDIT_PAGES = ["general", "capabilities", "limits", "caching", "sharing", "endpoints"];

/** Which layer's page is open, and which of its settings pages. */
let editing = null;

/**
 * What has been typed and not saved, per layer, for as long as this tab is open.
 *
 * <b>Instead of a confirmation dialog, and deliberately.</b> The exits from a
 * settings page are the breadcrumb, the tab strip and the browser's own Back — and a
 * hashchange cannot be cancelled once it has fired, so a guard there would have to
 * navigate the reader back afterwards and would leave a history entry pointing at a
 * page they were sent away from. Keeping the values is the better answer to the same
 * risk: nothing is lost, so nothing needs asking about. Returning to the layer puts
 * them back and says so.
 *
 * <b>Cancel is the one exit that clears it</b>, because Cancel is the reader saying
 * *discard*, which is the only unambiguous signal in the set.
 */
const unsaved = new Map();      // layer name -> Map(control id -> value)

/** Reads every control on the open editor, so a redraw can put them back. */
function editedValues() {
  const values = new Map();
  for (const el of document.querySelectorAll(
    "#editPages input, #editPages select, #editPages textarea")) {
    if (el.id) values.set(el.id, el.type === "checkbox" ? el.checked : el.value);
  }
  return values;
}

/** Says, next to Save, that there is something to save. */
function markUnsaved(dirty) {
  const marker = $("editDirty");
  if (marker) marker.hidden = !dirty;
}

/**
 * Opens a layer's settings page — or flips between its pages if it is already open.
 *
 * <b>Flipping rather than rebuilding is why this is one function and not two.</b>
 * All six pages are in the document at once, so moving between them is a class on a
 * section. Rebuilding on every click of the left column would re-read the server and
 * throw away a figure typed on the page you just left, which is a worse fault than
 * the one it would be fixing.
 *
 * `pending` carries the values a background refresh must not lose — redrawLayerPage
 * has the reason.
 */
function showLayer(name, page, pending = null) {
  if (editing && editing.name === name && $("view-layer").classList.contains("on")) {
    editing.page = page;
    showEditPage(page);
    return;
  }

  const l = layerNamed(name);
  selected = name;
  editing = { name, page };

  const stopped = l.status === "stopped";
  const isShown = shown.has(name);
  const tilesShown = shown.has(tileKey(name));

  // The breadcrumb is a link, not a label: it is also how you get back, and their
  // reference reads the same way — Site (root) › folder › service.
  $("editCrumb").innerHTML = `<a href="#/services">Services</a> › ${
    h(l.hosted ? "hosted" : "registered")} › <b>${h(name)}</b>`;

  $("editNav").innerHTML = EDIT_PAGES.map(p =>
    `<a href="#/layer/${encodeURIComponent(name)}/${p}">${
      p[0].toUpperCase() + p.slice(1)}</a>`).join("");

  $("editPages").innerHTML = `
    <section class="page" id="page-general">
      <h4>State</h4>
      <div class="row">
        ${pill(l.status)}${pill(l.sharing)}${pill(l.hosted ? "hosted" : "registered")}
        <span style="flex:1"></span>
        <button data-toggle="${h(name)}" data-to="${stopped ? "start" : "stop"}">
          ${stopped ? "Start" : "Stop"}</button>
        <button data-show="${h(name)}" class="${isShown ? "on" : ""}" ${stopped ? "disabled" : ""}>
          ${isShown ? "Hide on map" : "Show on map"}</button>
        ${l.hosted
          ? `<button data-tiles="${h(name)}" class="${tilesShown ? "on" : ""}" ${stopped ? "disabled" : ""}>
               ${tilesShown ? "Hide tiles" : "Show tiles"}</button>`
          : ""}
      </div>

      <h4>Contents</h4>
      <div id="contents" class="val">reading the layer document…</div>

      <h4>Identity</h4>
      <dl class="facts">
        <dt>Source table</dt><dd>${h(l.table)}</dd>
        <dt>Data source</dt><dd>${h(l.dataSource || "")}</dd>
        <dt>Owner</dt><dd>${h(l.owner || "—")}</dd>
        <dt>Layer id</dt><dd>${h(l.id || "—")}</dd>
      </dl>
    </section>

    <section class="page" id="page-capabilities">
      <h4>Faces this layer offers</h4>
      <div class="grid2">
        <label><input type="checkbox" id="capFeatures"> Feature access</label>
        <label class="${l.hosted ? "" : "off"}">
          <input type="checkbox" id="capTiles" ${l.hosted ? "" : "disabled"}> Vector tiles${
            l.hosted ? "" : " — hosted data only"}</label>
      </div>

      <h4>Operations allowed</h4>
      <div class="grid2" id="ops">
        ${["Query", "Create", "Update", "Delete", "Extract"].map(o =>
          `<label><input type="checkbox" data-op="${o}"> ${o}</label>`).join("")}
      </div>
      <p class="hint" style="margin-top:12px">A tick is a ceiling, not a grant: what a
        caller may do is this narrowed by their privileges and by what the data supports —
        ADR-031. Unticking is the only direction that has an effect.</p>
    </section>

    <section class="page" id="page-limits">
      <h4>Response</h4>
      <div class="setting"><span class="q">The most rows one response may carry:</span>
        <input type="number" id="capMaxRows" min="1" placeholder="50000"><span class="u">rows</span></div>
      <div class="setting"><span class="q">Rows returned when the caller does not ask:</span>
        <input type="number" id="capDefRows" min="1" placeholder="1000"><span class="u">rows</span></div>
      <div class="setting"><span class="q">The most one response body may reach:</span>
        <input type="number" id="capOutBytes" min="1" placeholder="67108864"><span class="u">bytes</span></div>

      <h4>Request</h4>
      <div class="setting"><span class="q">The most one request body may carry:</span>
        <input type="number" id="capInBytes" min="1" placeholder="unset"><span class="u">bytes</span></div>
      <div class="setting"><span class="q">The most edits one call may apply:</span>
        <input type="number" id="capEdits" min="1" placeholder="unset"><span class="u">edits</span></div>
      <div class="setting"><span class="q">The longest one statement may run:</span>
        <input type="number" id="capTimeout" min="1" placeholder="default"><span class="u">ms</span></div>
      <p class="hint" style="margin-top:12px">Empty means the server's own figure applies.
        A row ceiling is reported to the client the way the protocol already reports one,
        through <code>exceededTransferLimit</code>, so a truncated answer is never silent.</p>
    </section>

    <section class="page" id="page-caching">
      <h4>Tile cache</h4>
      ${l.hosted ? `
      <div class="setting"><span class="q">How long a tile stays fresh:</span>
        <input type="number" id="ttl" min="0" step="1" placeholder="server default"><span class="u">seconds</span></div>
      <div class="row" style="margin-top:10px">
        <button data-cache="${h(name)}">Set</button>
        <button data-cache="${h(name)}" data-clear="1" class="ghost">Use the server's</button>
      </div>`
      : `<p class="hint">Tiles come only from hosted data, so this layer has no tile cache.</p>`}

      <h4>Style</h4>
      <div class="row">
        <button data-style="${h(name)}">Fetch current</button>
        <button data-style-del="${h(name)}" class="ghost">Back to generated</button>
        <button class="primary" data-style-put="${h(name)}">Store</button>
      </div>
      <textarea id="styleDoc" rows="8" spellcheck="false"
        placeholder="A MapLibre style document. Fetch it first — an empty box means none is stored."></textarea>
    </section>

    <section class="page" id="page-sharing">
      <h4>Who may read it</h4>
      <div class="row">
        <select data-share="${h(name)}">
          ${SCOPES.map(v =>
            `<option value="${v}"${v === l.sharing ? " selected" : ""}>${v}</option>`).join("")}
        </select>
        <span class="val">applied when chosen, not on Save</span>
      </div>
      <p class="hint">Sharing and started/stopped are deliberately outside the settings
        Save — ADR-031 §2b. They take effect at once and are never cached, because an
        operator revoking access has to be able to trust that it happened.</p>

      <h4>Maintenance</h4>
      <div class="row">
        <button data-refresh="${h(name)}">Forget remembered shape</button>
        <button class="danger" data-delete="${h(name)}">Delete layer</button>
      </div>
    </section>

    <section class="page" id="page-endpoints">
      <h4>Addresses</h4>
      <dl class="facts" id="endpoints"><dt>—</dt><dd>resolving…</dd></dl>
    </section>`;

  showEditPage(page);
  describeContents(name, l);

  // A background refresh passes its own snapshot; otherwise anything left unsaved
  // from earlier in this session is the snapshot.
  const restore = pending ?? unsaved.get(name) ?? null;
  markUnsaved(unsaved.has(name));

  if (restore) {
    // Put back what was on screen, and do not re-read: the server's answer would
    // overwrite exactly the edits this exists to keep.
    for (const [id, value] of restore) {
      const el = $(id);
      if (!el) continue;
      if (el.type === "checkbox") el.checked = value;
      else el.value = value;
    }

    // Said out loud, because a form showing figures the server does not have is
    // only safe if the reader knows that is what they are looking at.
    if (!pending) {
      toast(`${name}: showing what you had typed and not saved. Save applies it; `
        + `Cancel throws it away.`, true);
    }
  } else {
    const ttl = $("ttl");
    if (ttl && l.cacheSeconds != null) ttl.value = l.cacheSeconds;
    section("service settings", () => loadServiceCapabilities(name));
  }

  for (const row of document.querySelectorAll("tr.sel")) row.classList.remove("sel");
  const row = document.querySelector(`tr[data-pick="${CSS.escape(name)}"]`);
  if (row) row.classList.add("sel");

  showView("view-layer", "services");
  window.scrollTo(0, 0);
}

/**
 * Lists a service's group layers, from the document any client would read.
 *
 * <b>No new endpoint, and that is ADR-020 §2 rather than laziness.</b> The service
 * document already carries the tree — a group is a layer of type `Group Layer` with its
 * children in `subLayerIds` — so asking for it here is the same request the map makes.
 * A second admin route returning the same facts would be a second place for them to
 * disagree.
 */
async function showServiceGroups(qualified) {
  const box = $("gExisting");
  if (!box) return;

  const service = (qualified || "").trim();
  if (!service) { box.innerHTML = ""; return; }

  box.textContent = "reading the service…";

  try {
    const doc = await api(`/rest/services/${service.split("/").map(encodeURIComponent).join("/")}`
      + `/FeatureServer?f=json`);

    const groups = (doc.layers || []).filter(l => l.type === "Group Layer");

    box.innerHTML = groups.length === 0
      ? `<span>No group layers in ${h(service)} yet.</span>`
      : `<div>Groups in ${h(service)}:</div>` + groups.map(g => {
        const children = (g.subLayerIds || []).length;
        return `<div class="row" style="margin:6px 0 0;align-items:center">
          <span class="pill p-registered">${g.id}</span>
          <span style="font-family:var(--sans);font-size:13.5px">${h(g.name)}</span>
          <span>${children
            ? `${children} child${children === 1 ? "" : "ren"}`
            : "empty"}</span>
          <button class="tiny danger" data-group-delete="${h(service)}#${g.id}"
            data-group-name="${h(g.name)}"
            ${children ? `disabled title="Move its ${children} child${
              children === 1 ? "" : "ren"} out first — they are not reparented for you,
              because that would move them in every saved map."` : ""}>Delete</button>
        </div>`;
      }).join("");
  } catch (e) {
    // A stopped service answers 503 here, which is expected rather than a fault, so the
    // reason is shown in place rather than as an error.
    box.innerHTML = `<span style="color:var(--stop)">${h(e.message || String(e))}</span>`;
  }
}

/** Shows one settings page, and marks it in the left column. */
function showEditPage(page) {
  for (const link of document.querySelectorAll("#editNav a")) {
    if (link.getAttribute("href").endsWith(`/${page}`)) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }

  for (const s of document.querySelectorAll("#editPages .page")) {
    s.classList.toggle("on", s.id === `page-${page}`);
  }
}

/**
 * Reads what the service is configured to offer, into the pages.
 *
 * <b>Read, never assumed.</b> The old cache box started empty and explained in a
 * paragraph that it could not show the current value. A control that displays a
 * figure it did not read is a control that lies the moment somebody changes it in
 * another window.
 */
async function loadServiceCapabilities(name) {
  const place = (await resolvePlaces()).get(name);
  if (!place) return;

  const { folder, name: service } = splitService(place.service);
  const c = await api(`/admin/services/${encodeURIComponent(service)}/capabilities`
    + `?folder=${encodeURIComponent(folder || "")}`) || {};

  const set = (id, v) => { const el = $(id); if (el && v != null) el.value = v; };

  if ($("capFeatures")) $("capFeatures").checked = c.servesFeatures !== false;
  if ($("capTiles") && !$("capTiles").disabled) $("capTiles").checked = c.servesTiles !== false;

  // An unset ceiling is every operation the caller's privileges allow, so the boxes
  // start ticked and unticking one is the narrowing.
  const allowed = c.capabilities ?? null;
  for (const box of document.querySelectorAll("#ops input[data-op]")) {
    box.checked = allowed === null || allowed.includes(box.dataset.op);
  }

  set("capMaxRows", c.maxRecordCount);
  set("capDefRows", c.defaultRecordCount);
  set("capOutBytes", c.maxResponseBytes);
  set("capInBytes", c.maxRequestBytes);
  set("capEdits", c.maxEditsPerTransaction);
  set("capTimeout", c.statementTimeoutMs);
}

/** Saves every settings page at once, which is what one Save means. */
async function saveEditing() {
  if (!editing) return;

  const place = (await resolvePlaces()).get(editing.name);
  if (!place) { toast(`${editing.name} is not in the services directory.`); return; }

  const { folder, name: service } = splitService(place.service);
  const num = id => {
    const raw = ($(id)?.value ?? "").trim();
    return raw === "" ? null : Number(raw);
  };

  const ops = [...document.querySelectorAll("#ops input[data-op]")];
  const ticked = ops.filter(b => b.checked).map(b => b.dataset.op);

  const saved = await api(`/admin/services/${encodeURIComponent(service)}/capabilities`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      folder,
      servesFeatures: $("capFeatures").checked ? null : false,
      servesTiles: $("capTiles").disabled ? null : ($("capTiles").checked ? null : false),

      // All ticked means unset — the caller's privileges decide — rather than a
      // ceiling that happens to list everything. The two behave alike today and
      // diverge the moment a capability is added.
      capabilities: ticked.length === ops.length ? null : ticked,

      maxRecordCount: num("capMaxRows"),
      defaultRecordCount: num("capDefRows"),
      maxResponseBytes: num("capOutBytes"),
      maxRequestBytes: num("capInBytes"),
      maxEditsPerTransaction: num("capEdits"),
      statementTimeoutMilliseconds: num("capTimeout"),
    }),
  });

  // Saved is saved: the stash exists to survive leaving the page, not the write.
  unsaved.delete(editing.name);
  markUnsaved(false);

  toast(saved.note ? `${service}: saved. ${saved.note}` : `${service}: saved`, true);
}

/**
 * Fills the Contents group from the layer's own service document.
 *
 * <b>Not a new admin capability</b> — ADR-020 §2. This is the same document the
 * map already fetches to choose a symbol, and the same one any ArcGIS client
 * reads. What the console was missing is that "what is in this layer" — its
 * fields, its geometry, its extent — had no answer anywhere in the UI, and it is
 * the first thing anybody asks about a layer they did not publish themselves.
 *
 * Loaded after the page paints rather than before, so opening a layer is never
 * held up by a request; and a refusal is shown in place rather than as a toast,
 * because a stopped service refusing this is expected and not an error.
 */
async function describeContents(name, layer) {
  const box = $("contents");
  if (!box) return;

  const place = (await resolvePlaces()).get(name);
  if (editing?.name === name) fillEndpoints(name, layer, place);

  try {
    const doc = await api(`${(await layerUrl(name)).replace(location.origin, "")}?f=json`);
    if (editing?.name !== name) return;   // the reader moved on while this was in flight

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
    if (editing?.name !== name) return;
    box.innerHTML = `<span style="color:var(--stop)">${h(e.message || String(e))}</span>`;
  }
}

/**
 * The layer's endpoint links, once its real address is known.
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
        <!--
          The groups a service already has, listed here and removable here. The place
          something is made is the place to unmake it: there is no other screen a group
          belongs to, since a group holds no data and has no settings of its own.
        -->
        <div id="gExisting" class="val" style="margin-top:10px"></div>
      </form>
    </div>

    <div id="newResult" class="group" style="display:none"></div>`;

  $("designForm").addEventListener("submit", createDesigned);
  $("importForm").addEventListener("submit", createImported);
  $("regForm").addEventListener("submit", publishRegistered);
  $("svcForm").addEventListener("submit", createService);
  $("grpForm").addEventListener("submit", createGroupLayer);
  $("gService").addEventListener("change", event => showServiceGroups(event.target.value));
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

  // The list under the form is the record of what this service now holds, so it moves
  // with the thing it lists rather than on the next drawer opening.
  await section("groups", () => showServiceGroups($("gService").value));
  await section("services", loadServices, "services");
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

  // Navigation is links now — the tab strip, the editor's left column, the
  // breadcrumb and Cancel — so it needs no branch here. The browser follows the
  // href, the hash changes, and route() paints. That is also what makes Back work.
  if (t.id === "newLayer") { openNewLayer(); return; }

  // <b>Making a folder is on the rail</b>, which is the only place a folder is the subject
  // rather than a field on something else (ADR-034 §5h).
  if (t.id === "newFolder") {
    const name = prompt("A folder to publish into. It becomes part of the URL: "
      + "/rest/services/<folder>/<service>/FeatureServer");
    if (!name) return;
    try {
      const r = await api("/admin/folders", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name }),
      });
      toast(r.note, true);
      await section("folders", loadFolders);
    } catch (e) { toast(e.message); }
    return;
  }
  if (t.id === "drawerClose") { closeDrawer(); return; }

  // Cancel means discard, and it is the only exit that says so unambiguously — so it
  // is the only one that throws the typed values away. The link then navigates.
  if (t.id === "editCancel" && editing) {
    unsaved.delete(editing.name);
    markUnsaved(false);
    return;
  }

  if (t.id === "editSave") {
    t.disabled = true;
    await section("settings", saveEditing);
    t.disabled = false;
    return;
  }

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

  // <b>A service row opens the service, and its layers are inside.</b> ADR-034 §5h: the
  // service is the unit on this screen, so selecting one goes to what it holds rather than
  // to a layer somebody guessed from a flat table.
  const service = t.closest("tr[data-service]");
  if (service && !d.serviceDelete && !d.serviceShare) {
    location.hash = `#/server/service/${encodeURIComponent(service.dataset.service)}`;
    return;
  }

  // A content row in Studio: the layer's own page, which is where its appearance and its
  // sharing are.
  const pick = t.closest("tr[data-pick]");
  if (pick && !d.show && !d.tiles) {
    location.hash = `#/server/layer/${encodeURIComponent(pick.dataset.pick)}`;
    return;
  }

  if (d.tiles) {
    const drawn = !shown.has(tileKey(d.tiles));
    if (drawn) await showTiles(d.tiles);
    else hide(tileKey(d.tiles));
    await loadLayers();
    if (drawn) toMap();
    return;
  }

  if (d.show) {
    if (shown.has(d.show)) { hide(d.show); await loadLayers(); return; }
    t.disabled = true;
    try {
      // The whole document, because the SDK will read it anyway and this console now
      // takes its colour from the server's `drawingInfo` rather than choosing one.
      const doc = await api(
        `${serviceRoot(layerNamed(d.show)).replace(location.origin, "")}/FeatureServer/0?f=json`);
      await show(d.show, doc);
      await loadLayers();
      toMap();
      return;
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
      selected = null;
      editing = null;                    // there is no longer a layer to have open
      location.hash = "#/services";
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

  // <b>Empty only, and the button is already disabled otherwise</b> — this is the
  // second guard rather than the first, because a disabled button is a hint and the
  // server's refusal is the rule.
  if (d.serviceDelete) {
    if (!confirm(`Delete the service "${d.serviceDelete}"? It holds no layers, so nothing `
        + `published goes with it.`)) return;
    try {
      const r = await api(`/admin/featureservices/${encodeURIComponent(d.serviceDelete)}`
        + `?folder=${encodeURIComponent(d.folder || "")}`, { method: "DELETE" });
      toast(`${d.serviceDelete}: ${r.note}`, true);
    } catch (e) { toast(e.message); }
    await section("services", loadServices, "services");
    return;
  }

  if (d.groupDelete) {
    const [service, index] = d.groupDelete.split("#");
    if (!confirm(`Delete group layer ${index} ("${d.groupName}") from ${service}?`)) return;
    try {
      const { folder, name } = splitService(service);
      const r = await api(`/admin/services/${encodeURIComponent(name)}/groups/${index}`
        + `?folder=${encodeURIComponent(folder || "")}`, { method: "DELETE" });
      toast(r.note, true);
    } catch (e) { toast(e.message); }
    await section("groups", () => showServiceGroups($("gService").value));
    await section("services", loadServices, "services");
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

  // A tick and a chosen option are `change` rather than `input`, so the note lives
  // here too: a capability unticked and then left behind is exactly the edit worth
  // keeping.
  noteEdit(event.target);

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
    await section("services", loadServices, "services");
  }
});

document.addEventListener("input", event => {
  if (event.target.id === "serviceFilter") {
    serviceFilter = event.target.value;
    section("services", loadServices, "services");
    return;
  }

  noteEdit(event.target);
});

/** Remembers an edit to the open editor, so leaving the page cannot lose it. */
function noteEdit(target) {
  if (!editing || !target.closest || !target.closest("#editPages")) return;

  // Sharing is applied when chosen rather than on Save (ADR-031 §2b), so it is not
  // an unsaved edit and marking it as one would promise a Save that does nothing.
  if (target.dataset && target.dataset.share) return;

  unsaved.set(editing.name, editedValues());
  markUnsaved(true);
}

document.addEventListener("keydown", event => {
  if (event.key === "Escape") {
    // Escape clears a filter before it closes the Create drawer, because the filter
    // is the thing you are most likely to be holding when you press it. It does not
    // leave the layer's page: Escape dismisses something floating, and a page you
    // navigated to is left with Back or Cancel.
    if (document.activeElement && document.activeElement.id === "serviceFilter"
        && $("serviceFilter").value !== "") {
      $("serviceFilter").value = "";
      serviceFilter = "";
      section("services", loadServices, "services");
      return;
    }
    if ($("drawer").classList.contains("on")) closeDrawer();
  }
});

// ----------------------------------------------------------------------- boot

async function whoami() {
  const me = await api("/rest/whoami");

  // <b>The gate's input.</b> ADR-034 §5b: Server needs `admin:manageServer`, and the router
  // reads it from here rather than probing an endpoint to see whether it is refused.
  privileges = new Set(me.privileges || []);
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
  // <b>Only what this reader may read.</b> The catalogue listing needs
  // `admin:viewAllContent`, so asking for it as a publisher is a refusal in the corner of a
  // screen they should not have been shown — which is the defect ADR-034 exists to fix, and
  // it would be silly to reproduce it during boot.
  await Promise.all([
    may("admin:manageServer") ? section("health", refreshHealth) : Promise.resolve(),
    may("admin:viewAllContent") ? section("layers", loadLayers) : Promise.resolve(),
  ]);

  // <b>The address is read after the listing, not before.</b> A link straight to
  // #/layer/tr_ilce/limits has to open that layer's page on load — that is what
  // makes it an address rather than a bookmark that lands somewhere else — and the
  // page cannot say whether the layer is hosted until the catalogue is in hand.
  route();
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
