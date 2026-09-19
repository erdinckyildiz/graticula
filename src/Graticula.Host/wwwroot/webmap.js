// The web map viewer's behaviour — ADR-079. OpenLayers, vendored; nothing reaches a CDN.
//
// Addressed as `?id=<32 hex>` for a saved map, or `?service=<folder/name>[&layer=<n>]` for a new,
// unsaved map that starts with that service in it.
//
// <b>The document is the state.</b> The page holds the ArcGIS Web Map JSON it was given and edits the
// fields it understands in place — a layer's visibility, opacity, order and filter, the basemap, the
// initial view — so a map saved by ArcGIS Pro and saved again here keeps every field this page never
// reads (ADR-079 §5.2). Nothing is rebuilt from a model of our own.
//
// <b>No name here may repeat one in ground.js</b>, which is loaded first on the same page: two
// top-level declarations of one name in the shared lexical scope is a SyntaxError raised while
// parsing, and the whole file silently does nothing — view.js lost four days to exactly that.

const WM_QUERY = new URLSearchParams(location.search);
const WM_MERCATOR = "EPSG:3857";
const WM_WORLD = [-20037508.342789244, -20037508.342789244, 20037508.342789244, 20037508.342789244];

/** Features drawn per request for a feature layer, per view; more than this is said, not drawn. */
const WM_DRAW_LIMIT = 2000;

/** Colours for layers whose document carries no renderer, by position in the map. */
const WM_PALETTE = ["#b8422e", "#1f5fa8", "#2f7a55", "#92620d", "#6b3fa0", "#0b6157", "#a63a6b"];

const wm$ = id => document.getElementById(id);

function wmEscape(value) {
  return String(value ?? "").replace(/[&<>"']/g,
    c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

// ----------------------------------------------------------------------------- the session
//
// A GET carries the session cookie on its own; a write needs the bearer token the console keeps in
// sessionStorage, because the server honours the cookie for GET and HEAD only.

let wmToken = (() => {
  try { return sessionStorage.getItem("gis-token"); } catch { return null; }
})();

/**
 * Trades the browsing cookie for a token of this tab's own — ADR-023 §4c, as the console does.
 *
 * <b>Because this page is reached from the services directory</b>, often in a new tab, and
 * `sessionStorage` is per tab: a reader signed in there holds the cookie and no token, and would be
 * told to sign in again to save a map they can already see. The server exchanges only for a request
 * the browser marks same-origin.
 */
async function wmExchangeSession() {
  try {
    const response = await fetch("/rest/auth/session", { method: "POST", credentials: "same-origin" });
    if (!response.ok) return false;
    const body = await response.json();
    if (!body || typeof body.token !== "string" || !body.token) return false;
    wmToken = body.token;
    try { sessionStorage.setItem("gis-token", wmToken); } catch { /* private mode: this tab keeps it */ }
    return true;
  } catch {
    return false;
  }
}

/** Who is looking: filled from /rest/whoami before the map is drawn. */
let wmMe = { authenticated: false, name: null, privileges: new Set() };

/**
 * A request to this server, answering the parsed body or throwing an Error with the server's own
 * sentence and the status beside it.
 *
 * <b>ArcGIS refusals arrive inside a 200</b> — `{ error: { code, message } }` — so both shapes are
 * failures here, and the envelope's code becomes the status a caller reads.
 */
async function wmFetch(url, options = {}) {
  const headers = { Accept: "application/json", ...(options.headers || {}) };
  if (wmToken) headers.Authorization = "Bearer " + wmToken;

  const response = await fetch(url, { ...options, headers });
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* not JSON */ }

  const envelope = body && typeof body === "object" && body.error;

  if (!response.ok || envelope) {
    const failure = new Error(
      (envelope && envelope.message) || `${response.status} ${response.statusText || ""}`.trim());
    failure.status = (envelope && Number(envelope.code)) || response.status;
    throw failure;
  }

  return body;
}

const wmJson = value => JSON.stringify(value);

// ----------------------------------------------------------------------------- the page state

const wmState = {
  id: null,                 // the saved map's id, or null while unsaved
  meta: { title: "Untitled map", snippet: "", sharing: "private", owner: null, manages: true, mine: true },
  doc: null,                // the Web Map document — the one source of truth for what is drawn
  dirty: false,             // changed since opened or saved
};

/** What the page knows about each operational layer, keyed by the layer's own JSON object. */
const wmRuntime = new WeakMap();

function wmSay(text, bad = false) {
  const line = wm$("status");
  line.textContent = text;
  line.style.color = bad ? "var(--alert)" : "";
}

function wmSayIn(id, text, bad = false) {
  const line = wm$(id);
  if (!line) return;
  line.textContent = text;
  line.classList.toggle("bad", bad);
}

function wmMarkDirty() {
  wmState.dirty = true;
  wmDrawSaved();
}

function wmDrawSaved() {
  const saved = wm$("saved");
  saved.textContent = !wmState.id
    ? "Not saved"
    : wmState.dirty ? "Unsaved changes" : "Saved";
  saved.classList.toggle("dirty", !wmState.id || wmState.dirty);
  wm$("title").textContent = wmState.meta.title || "Untitled map";
  document.title = `${wmState.meta.title || "Map"} — Graticula`;

  // <b>Save where the changes are seen, not only in a tab.</b> The review found Save reachable only by
  // opening *Map*, so a reader who filtered a layer had nothing on screen saying how to keep it.
  // Absent for somebody who cannot save; `aria-disabled` rather than `disabled` when there is nothing
  // new, because it is the button that had focus when the save it started finishes.
  const head = wm$("headSave");
  if (!head) return;
  const copy = !!wmState.id && !wmState.meta.manages;
  head.hidden = !!wmMaySave();
  head.textContent = copy ? "Save a copy" : "Save";
  const idle = !!wmState.id && !wmState.dirty && !copy;
  head.setAttribute("aria-disabled", String(idle));
  head.title = idle ? "Nothing has changed since it was saved." : copy
    ? "Save your own copy of this map; it starts private."
    : "Save this map, with the current view as the one it opens at.";
}

// A leave with unsaved changes asks first. The browser writes its own sentence; returning a value
// is what makes it ask.
//
// <b>Only for somebody who could have saved.</b> A reader who may not save — signed out, or with no
// right to create content — can switch layers off and move the view as much as they like; asking them
// to confirm losing changes they had no way to keep would be a question with one answer.
window.addEventListener("beforeunload", event => {
  if (!wmState.dirty || wmMaySave()) return undefined;
  event.preventDefault();
  event.returnValue = "";
  return "";
});

/**
 * Redraws a region and puts the keyboard back where it was.
 *
 * <b>The recurring defect in this repository, answered once here.</b> Replacing `innerHTML` drops the
 * focused element, and the browser sends focus to `<body>` — so a keyboard user who pressed *Move up*
 * lands at the top of the document with no idea where they are. Every control that can be focused
 * carries `data-focus`; after the redraw the same key is focused again, and when that control is now
 * disabled (a layer moved to the top has no *Move up*), its fallback is.
 */
function wmRedraw(container, markup) {
  const active = document.activeElement;
  const key = active && container.contains(active) ? active.dataset.focus : null;
  const fallback = active && container.contains(active) ? active.dataset.focusFallback : null;

  container.innerHTML = markup;

  if (!key) return;

  const again = container.querySelector(`[data-focus="${CSS.escape(key)}"]`);

  if (again && !again.disabled) {
    again.focus();
    return;
  }

  const other = fallback && container.querySelector(`[data-focus="${CSS.escape(fallback)}"]`);
  if (other) other.focus();
}

// ----------------------------------------------------------------------------- the map

const wmBase = new ol.layer.Tile({
  zIndex: 0,
  source: new ol.source.XYZ({
    url: typeof OSM_TILES === "string" ? OSM_TILES : "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
    crossOrigin: "anonymous",
    maxZoom: 19,
    attributions: '© <a href="https://www.openstreetmap.org/copyright" target="_blank" '
      + 'rel="noreferrer">OpenStreetMap</a> contributors',
  }),
});

/** Search results and identified features, drawn over everything. */
const wmHighlight = new ol.layer.Vector({
  zIndex: 1000,
  source: new ol.source.Vector(),
  style: new ol.style.Style({
    fill: new ol.style.Fill({ color: "rgba(255, 214, 0, 0.28)" }),
    stroke: new ol.style.Stroke({ color: "#e0a800", width: 3 }),
    image: new ol.style.Circle({
      radius: 8,
      fill: new ol.style.Fill({ color: "rgba(255, 214, 0, 0.5)" }),
      stroke: new ol.style.Stroke({ color: "#b07f00", width: 2.5 }),
    }),
  }),
});

/** What has been measured. */
const wmMeasured = new ol.layer.Vector({
  zIndex: 1001,
  source: new ol.source.Vector(),
  style: new ol.style.Style({
    fill: new ol.style.Fill({ color: "rgba(11, 97, 87, 0.15)" }),
    stroke: new ol.style.Stroke({ color: "#0b6157", width: 2.5, lineDash: [8, 5] }),
    image: new ol.style.Circle({ radius: 4, fill: new ol.style.Fill({ color: "#0b6157" }) }),
  }),
});

const wmMap = new ol.Map({
  target: "map",
  layers: [wmBase, wmHighlight, wmMeasured],
  view: new ol.View({ projection: WM_MERCATOR, center: [0, 0], zoom: 2 }),
  controls: ol.control.defaults.defaults({ attribution: true }).extend([
    new ol.control.ScaleLine({ units: "metric", target: wm$("stripScale") }),
    new ol.control.MousePosition({
      projection: "EPSG:4326",
      className: "ol-mouse-position",
      target: wm$("stripWhere"),
      coordinateFormat: c => (c ? `${c[0].toFixed(5)}, ${c[1].toFixed(5)}` : ""),
    }),
  ]),
});

/** The map's box, clamped to the world, as a query's envelope. */
function wmQueryBox(extent) {
  return [
    Math.max(extent[0], WM_WORLD[0]), Math.max(extent[1], WM_WORLD[1]),
    Math.min(extent[2], WM_WORLD[2]), Math.min(extent[3], WM_WORLD[3]),
  ];
}

/** Fits the view to a box once the map has a size; a map with no size yet fits to nothing. */
function wmFit(extent, frames = 0) {
  if (!extent || !extent.every(Number.isFinite)) return;

  const size = wmMap.getSize();

  if (!size || !size[0] || !size[1]) {
    if (frames < 60) requestAnimationFrame(() => wmFit(extent, frames + 1));
    return;
  }

  const degenerate = extent[2] - extent[0] < 1 && extent[3] - extent[1] < 1;
  const box = degenerate ? ol.extent.buffer(extent, 250) : extent;

  wmMap.getView().fit(box, { padding: [40, 40, 40, 40], maxZoom: 18 });
}

// ----------------------------------------------------------------------------- the document

/** A Web Map document with nothing in it yet, in the subset this page writes. */
function wmEmptyDocument() {
  return {
    operationalLayers: [],
    baseMap: wmBaseMapJson("osm"),
    spatialReference: { wkid: 102100, latestWkid: 3857 },
    authoringApp: "Graticula",
    authoringAppVersion: "1",
    version: "2.31",
  };
}

function wmBaseMapJson(which) {
  return which === "none"
    ? { baseMapLayers: [], title: "No basemap" }
    : {
      baseMapLayers: [{
        id: "OpenStreetMap",
        layerType: "OpenStreetMap",
        title: "OpenStreetMap",
        visibility: true,
        opacity: 1,
      }],
      title: "OpenStreetMap",
    };
}

/** Which basemap the document asks for, in the two words this page can draw. */
function wmBaseMapOf(doc) {
  const layers = (doc.baseMap && doc.baseMap.baseMapLayers) || [];
  return layers.length === 0 ? "none" : "osm";
}

function wmLayers() {
  if (!Array.isArray(wmState.doc.operationalLayers)) wmState.doc.operationalLayers = [];
  return wmState.doc.operationalLayers;
}

function wmNewId() {
  return "layer-" + Math.random().toString(16).slice(2, 10) + Date.now().toString(16).slice(-4);
}

/** What kind of layer this page takes one to be, or null for a kind it keeps and does not draw. */
function wmKind(layer) {
  switch (layer.layerType) {
    case "ArcGISFeatureLayer": return layer.url ? "feature" : null;
    case "ArcGISMapServiceLayer": return layer.url ? "image" : null;
    case "VectorTileLayer": return (layer.styleUrl || layer.url) ? "tiles" : null;
    default: return null;
  }
}

const WM_KIND_LABEL = { feature: "Features", image: "Map image", tiles: "Vector tiles" };

/** A vector tile layer's service address, from whichever of its two fields it carries. */
function wmTileService(layer) {
  const from = layer.url || layer.styleUrl || "";
  const at = from.indexOf("/VectorTileServer");
  return at < 0 ? from : from.slice(0, at + "/VectorTileServer".length);
}

/** The address to ask whether a layer can be read at all. */
function wmProbeUrl(layer) {
  const kind = wmKind(layer);
  if (kind === "tiles") return wmTileService(layer);
  return layer.url;
}

// ----------------------------------------------------------------------------- symbols

function wmColour(rgba, fallback) {
  if (!Array.isArray(rgba) || rgba.length < 3) return fallback;
  const alpha = rgba.length > 3 ? rgba[3] / 255 : 1;
  return `rgba(${rgba[0]}, ${rgba[1]}, ${rgba[2]}, ${alpha})`;
}

/** Points to pixels, as ArcGIS symbol sizes are points. */
const wmPx = pt => (Number(pt) || 0) * 1.333;

/** One ArcGIS symbol as an OpenLayers style, or null when it names nothing drawable. */
function wmSymbolStyle(symbol, fallback) {
  if (!symbol) return null;

  switch (symbol.type) {
    case "esriSFS": {
      const outline = symbol.outline || {};
      return new ol.style.Style({
        fill: new ol.style.Fill({ color: wmColour(symbol.color, "rgba(0,0,0,0)") }),
        stroke: outline.color === null || symbol.outline === null
          ? undefined
          : new ol.style.Stroke({ color: wmColour(outline.color, fallback), width: Math.max(wmPx(outline.width ?? 0.75), 0.5) }),
      });
    }
    case "esriSLS":
      return new ol.style.Style({
        stroke: new ol.style.Stroke({ color: wmColour(symbol.color, fallback), width: Math.max(wmPx(symbol.width ?? 1), 0.5) }),
      });
    case "esriSMS": {
      const outline = symbol.outline || {};
      return new ol.style.Style({
        image: new ol.style.Circle({
          radius: Math.max(wmPx(symbol.size ?? 6) / 2, 2),
          fill: new ol.style.Fill({ color: wmColour(symbol.color, fallback) }),
          stroke: new ol.style.Stroke({ color: wmColour(outline.color, "#fff"), width: Math.max(wmPx(outline.width ?? 0.75), 0.5) }),
        }),
      });
    }
    case "esriPMS":
      return symbol.imageData
        ? new ol.style.Style({
          image: new ol.style.Icon({
            src: `data:${symbol.contentType || "image/png"};base64,${symbol.imageData}`,
            width: wmPx(symbol.width || 16),
            height: wmPx(symbol.height || 16),
          }),
        })
        : null;
    default:
      return null;
  }
}

/** A plain style in one colour, for a layer whose renderer this page cannot read. */
function wmPlainStyle(colour) {
  return new ol.style.Style({
    fill: new ol.style.Fill({ color: colour + "44" }),
    stroke: new ol.style.Stroke({ color: colour, width: 2 }),
    image: new ol.style.Circle({
      radius: 5,
      fill: new ol.style.Fill({ color: colour }),
      stroke: new ol.style.Stroke({ color: "#fff", width: 1.3 }),
    }),
  });
}

/**
 * A style function from the layer document's renderer — simple, unique value and class breaks,
 * which is what this server publishes (ADR-033). Anything else draws in one colour and says so.
 */
function wmRendererStyle(info, colour) {
  const plain = wmPlainStyle(colour);
  const renderer = info && info.drawingInfo && info.drawingInfo.renderer;

  if (!renderer) return { style: plain, fields: [], own: false };

  if (renderer.type === "simple") {
    return { style: wmSymbolStyle(renderer.symbol, colour) || plain, fields: [], own: true };
  }

  const fallbackStyle = wmSymbolStyle(renderer.defaultSymbol, colour);

  if (renderer.type === "uniqueValue" && renderer.field1) {
    const byValue = new Map();
    for (const entry of renderer.uniqueValueInfos || []) {
      const style = wmSymbolStyle(entry.symbol, colour);
      if (style) byValue.set(String(entry.value), style);
    }
    return {
      style: feature => byValue.get(String(feature.get(renderer.field1))) || fallbackStyle || null,
      fields: [renderer.field1],
      own: true,
    };
  }

  if (renderer.type === "classBreaks" && renderer.field) {
    const breaks = (renderer.classBreakInfos || []).map(entry => ({
      max: Number(entry.classMaxValue),
      style: wmSymbolStyle(entry.symbol, colour),
    })).sort((a, b) => a.max - b.max);
    const minimum = Number(renderer.minValue ?? -Infinity);
    return {
      style: feature => {
        const value = Number(feature.get(renderer.field));
        if (!Number.isFinite(value) || value < minimum) return fallbackStyle || null;
        const hit = breaks.find(b => value <= b.max);
        return (hit && hit.style) || fallbackStyle || null;
      },
      fields: [renderer.field],
      own: true,
    };
  }

  return { style: plain, fields: [], own: false };
}

/** The colour a symbol reads as in a legend: its fill, or its line where the fill is empty. */
function wmSymbolColour(symbol) {
  if (!symbol) return null;
  const opaque = c => Array.isArray(c) && (c.length < 4 || c[3] > 0);
  if (symbol.type === "esriSFS") {
    if (opaque(symbol.color)) return wmColour(symbol.color, null);
    return symbol.outline && opaque(symbol.outline.color) ? wmColour(symbol.outline.color, null) : null;
  }
  return opaque(symbol.color) ? wmColour(symbol.color, null) : null;
}

/**
 * The colours a layer's legend swatch shows, from the same renderer the drawing comes from — so the
 * swatch beside a title and the features on the map cannot disagree. Up to four for a classified
 * layer; the layer's palette colour where there is no renderer this page reads.
 */
function wmSwatches(info, colour) {
  const renderer = info && info.drawingInfo && info.drawingInfo.renderer;
  if (!renderer) return [colour];

  const symbols = renderer.type === "simple"
    ? [renderer.symbol]
    : renderer.type === "uniqueValue"
      ? (renderer.uniqueValueInfos || []).map(e => e.symbol)
      : renderer.type === "classBreaks"
        ? (renderer.classBreakInfos || []).map(e => e.symbol)
        : [];

  const colours = [...new Set(symbols.map(wmSymbolColour).filter(Boolean))].slice(0, 4);
  return colours.length ? colours : [colour];
}

// ----------------------------------------------------------------------------- layers on the map

const WM_ESRI = new ol.format.EsriJSON();

/** Builds query parameters, leaving out what is empty. */
function wmParams(values) {
  const out = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && value !== "") out.set(key, String(value));
  }
  return out.toString();
}

function wmWhere(layer) {
  const expression = layer.layerDefinition && layer.layerDefinition.definitionExpression;
  return expression && String(expression).trim() ? String(expression).trim() : "1=1";
}

/**
 * The OpenLayers layer for one operational layer, built after its document has been read.
 *
 * <b>A feature layer is read by view, not whole.</b> view.js reads a layer in pages up to fifty
 * thousand features, which is right for looking at one layer and wrong for a map of several: each
 * would be read in full before anything drew. Here each view asks for what is in it, up to
 * {@link WM_DRAW_LIMIT}, and a view that holds more says so on the layer's line instead of drawing a
 * silent sample.
 */
function wmBuildLayer(layer, run, index) {
  const kind = wmKind(layer);
  const colour = WM_PALETTE[index % WM_PALETTE.length];

  // A map image is drawn by the server in the service's own symbology, which this page does not read.
  run.swatches = kind === "image" ? [] : kind === "feature" ? wmSwatches(run.info, colour) : [colour];

  if (kind === "feature") {
    const renderer = wmRendererStyle(run.info, colour);
    run.ownStyle = renderer.own;

    const idField = (run.info && run.info.objectIdField) || "objectid";
    const fields = [idField, ...renderer.fields].filter(Boolean);

    const source = new ol.source.Vector({
      strategy: ol.loadingstrategy.bbox,
      loader: (extent, resolution, projection, success, failure) => {
        const box = wmQueryBox(extent);
        const url = `${layer.url}/query?` + wmParams({
          where: wmWhere(layer),
          geometry: box.join(","),
          geometryType: "esriGeometryEnvelope",
          inSR: 3857,
          spatialRel: "esriSpatialRelIntersects",
          outFields: [...new Set(fields)].join(","),
          returnGeometry: true,
          outSR: 3857,
          resultRecordCount: WM_DRAW_LIMIT,
          f: "json",
        });

        wmFetch(url).then(payload => {
          const features = WM_ESRI.readFeatures(payload, { featureProjection: WM_MERCATOR });
          source.addFeatures(features);
          const truncated = !!payload.exceededTransferLimit;
          const wasError = !!run.error;
          run.error = null;
          if (run.truncated !== truncated || wasError) {
            run.truncated = truncated;
            wmDrawLayerList();
          }
          success(features);
        }).catch(e => {
          run.error = e.message || String(e);
          wmDrawLayerList();
          source.removeLoadedExtent(extent);
          failure();
        });
      },
    });

    return new ol.layer.Vector({ source, style: renderer.style, declutter: false });
  }

  if (kind === "image") {
    const source = new ol.source.ImageArcGISRest({
      url: layer.url,
      ratio: 1,
      params: { TRANSPARENT: true },
    });

    source.on("imageloaderror", () => {
      run.error = "The server refused to draw this layer's picture.";
      wmDrawLayerList();
    });
    source.on("imageloadend", () => {
      if (run.error) { run.error = null; wmDrawLayerList(); }
    });

    return new ol.layer.Image({ source });
  }

  if (kind === "tiles") {
    const style = wmPlainStyle(colour);
    return new ol.layer.VectorTile({
      declutter: true,
      source: new ol.source.VectorTile({
        format: new ol.format.MVT(),
        url: `${wmTileService(layer)}/tile/{z}/{y}/{x}.pbf`,
        maxZoom: 22,
      }),
      style,
    });
  }

  return null;
}

/** Applies a layer's visibility, opacity and place in the stack to what is drawn. */
function wmApply(layer, index) {
  const run = wmRuntime.get(layer);
  if (!run || !run.ol) return;
  run.ol.setVisible(layer.visibility !== false);
  run.ol.setOpacity(Number.isFinite(Number(layer.opacity)) ? Number(layer.opacity) : 1);
  run.ol.setZIndex(10 + index);
}

function wmApplyAll() {
  wmLayers().forEach(wmApply);
}

/**
 * Reads one layer's document and puts it on the map, or records why it cannot be.
 *
 * <b>A layer the viewer cannot read is kept and named, not dropped</b> (ADR-079 §3): the map's
 * author can then see why a colleague sees less. 401, 403, 404 and 499 all mean *not yours to read*
 * to the person looking, and saying which would tell them whether an unreadable service exists.
 */
async function wmLoadLayer(layer) {
  const run = { status: "loading", info: null, ol: null, error: null, truncated: false, ownStyle: false };
  wmRuntime.set(layer, run);

  const kind = wmKind(layer);

  if (!kind) {
    run.status = "unsupported";
    return;
  }

  try {
    run.info = await wmFetch(`${wmProbeUrl(layer)}?f=json`);
  } catch (e) {
    run.status = [401, 403, 404, 499].includes(e.status) ? "unavailable" : "error";
    run.error = e.message || String(e);
    return;
  }

  if (!layer.title) layer.title = run.info.name || run.info.mapName || "Layer";

  const index = wmLayers().indexOf(layer);
  run.ol = wmBuildLayer(layer, run, Math.max(index, 0));

  if (run.ol) {
    wmMap.addLayer(run.ol);
    wmApply(layer, index);
    run.status = "ok";
  } else {
    run.status = "unsupported";
  }
}

/** A layer's box in Web Mercator, from its document, or null. */
function wmExtentOf(layer) {
  const run = wmRuntime.get(layer);
  const info = run && run.info;
  const box = info && (info.extent || info.fullExtent || info.initialExtent);

  if (!box || !Number.isFinite(box.xmin)) return null;

  const reference = box.spatialReference || {};
  const wkid = reference.latestWkid || reference.wkid || 4326;

  try {
    return ol.proj.transformExtent(
      [box.xmin, box.ymin, box.xmax, box.ymax],
      wkid === 102100 ? WM_MERCATOR : `EPSG:${wkid}`,
      WM_MERCATOR);
  } catch {
    return null;
  }
}

function wmRemoveFromMap(layer) {
  const run = wmRuntime.get(layer);
  if (run && run.ol) wmMap.removeLayer(run.ol);
  wmRuntime.delete(layer);
}

// ----------------------------------------------------------------------------- the Layers tab

function wmLayerState(layer) {
  const run = wmRuntime.get(layer);
  if (!run) return { text: "Waiting to load…", tone: "" };

  switch (run.status) {
    case "loading": return { text: "Loading…", tone: "" };
    case "unavailable":
      return {
        text: "Not available to you. It is not shared with you, or it no longer exists; "
          + "it stays in the map for whoever can read it.",
        tone: "bad",
      };
    case "unsupported":
      return {
        text: `Kept in the map, not drawn here: ${layer.layerType || "this kind of layer"} is not one this viewer draws.`,
        tone: "warn",
      };
    case "error":
      return { text: `Could not be read: ${run.error}`, tone: "bad" };
    default:
      if (run.error) return { text: run.error, tone: "bad" };
      if (run.truncated) {
        return {
          text: `Showing the first ${WM_DRAW_LIMIT.toLocaleString()} features in this view. Zoom in, or filter, for the rest.`,
          tone: "warn",
        };
      }
      return { text: "", tone: "" };
  }
}

/** Field types a filter can compare, and whether a value is written quoted. */
const WM_FILTER_TYPES = {
  esriFieldTypeString: "text",
  esriFieldTypeInteger: "number",
  esriFieldTypeSmallInteger: "number",
  esriFieldTypeBigInteger: "number",
  esriFieldTypeDouble: "number",
  esriFieldTypeSingle: "number",
  esriFieldTypeDate: "date",
};

/** A layer's fields a person would filter on: not the identity, not the global id, not geometry. */
function wmUserFields(info) {
  if (!info || !Array.isArray(info.fields)) return [];
  const hidden = new Set([info.objectIdField, info.globalIdField].filter(Boolean).map(n => n.toLowerCase()));
  return info.fields.filter(f => f && f.name
    && WM_FILTER_TYPES[f.type]
    && !hidden.has(f.name.toLowerCase())
    && f.type !== "esriFieldTypeOID" && f.type !== "esriFieldTypeGlobalID");
}

/** An example clause written from the layer's own first text or number field, not from an invented one. */
function wmFilterExample(info) {
  const field = wmUserFields(info).find(f => WM_FILTER_TYPES[f.type] !== "date");
  if (!field) return "";
  return WM_FILTER_TYPES[field.type] === "text" ? `e.g. ${field.name} = 'value'` : `e.g. ${field.name} > 0`;
}

/** The swatch before a layer's title, in the colours its renderer draws with. */
function wmSwatchMarkup(run) {
  const colours = (run && run.swatches) || [];
  if (!colours.length) return `<span class="swatch none" aria-hidden="true"></span>`;
  const fill = colours.length === 1
    ? colours[0]
    : `linear-gradient(90deg, ${colours.map((c, i) => `${c} ${Math.round((i / colours.length) * 100)}% ${Math.round(((i + 1) / colours.length) * 100)}%`).join(", ")})`;
  return `<span class="swatch" aria-hidden="true" style="background:${wmEscape(fill)}"></span>`;
}

function wmDrawLayerList() {
  const list = wm$("layerList");
  const layers = wmLayers();

  if (layers.length === 0) {
    wmRedraw(list, `<li class="hint" style="border:0;padding:0">This map has no layers yet.
      <b>Add layer</b> lists the services you can read.</li>`);
    return;
  }

  // Top of the list is top of the map: the document's last layer is drawn last.
  const markup = layers.map((layer, index) => ({ layer, index })).reverse().map(({ layer, index }) => {
    const key = layer.id;
    const kind = wmKind(layer);
    const state = wmLayerState(layer);
    const run = wmRuntime.get(layer);
    const on = layer.visibility !== false;
    const opacity = Math.round((Number.isFinite(Number(layer.opacity)) ? Number(layer.opacity) : 1) * 100);
    const title = layer.title || "Untitled layer";
    const filter = (layer.layerDefinition && layer.layerDefinition.definitionExpression) || "";
    const readable = run && run.status === "ok";
    const top = index === layers.length - 1;
    const bottom = index === 0;

    return `<li class="${on ? "" : "off"}" data-layer="${wmEscape(key)}">
      <div class="lhead">
        <input type="checkbox" id="vis-${wmEscape(key)}" data-act="visible" data-layer="${wmEscape(key)}"
          data-focus="vis:${wmEscape(key)}" ${on ? "checked" : ""}>
        ${wmSwatchMarkup(run)}
        <label for="vis-${wmEscape(key)}">${wmEscape(title)}</label>
        <span class="lkind">${wmEscape(kind ? WM_KIND_LABEL[kind] : layer.layerType || "Unknown")}</span>
      </div>
      ${state.text ? `<div class="lstate ${state.tone}">${wmEscape(state.text)}</div>` : ""}
      ${readable && kind === "feature" && !run.ownStyle
        ? `<div class="lstate">Drawn in one colour: its document names no renderer this viewer reads.</div>` : ""}
      <div class="lctl">
        <label class="lkind" for="op-${wmEscape(key)}">Opacity</label>
        <input type="range" id="op-${wmEscape(key)}" min="0" max="100" step="5" value="${opacity}"
          data-act="opacity" data-layer="${wmEscape(key)}" data-focus="op:${wmEscape(key)}"
          aria-valuetext="${opacity}%">
        <button class="tiny" data-act="up" data-layer="${wmEscape(key)}" data-focus="up:${wmEscape(key)}"
          data-focus-fallback="down:${wmEscape(key)}" ${top ? "disabled" : ""}
          aria-label="Move ${wmEscape(title)} up">&uarr;</button>
        <button class="tiny" data-act="down" data-layer="${wmEscape(key)}" data-focus="down:${wmEscape(key)}"
          data-focus-fallback="up:${wmEscape(key)}" ${bottom ? "disabled" : ""}
          aria-label="Move ${wmEscape(title)} down">&darr;</button>
        ${readable && wmExtentOf(layer) ? `<button class="tiny" data-act="zoom" data-layer="${wmEscape(key)}"
          data-focus="zoom:${wmEscape(key)}">Zoom to</button>` : ""}
        <button class="tiny danger" data-act="remove" data-layer="${wmEscape(key)}"
          data-focus="remove:${wmEscape(key)}" aria-label="Remove ${wmEscape(title)} from the map">Remove</button>
      </div>
      ${kind === "feature" ? wmFilterMarkup(layer, run, key, title, filter) : ""}
    </li>`;
  }).join("");

  wmRedraw(list, markup);
}

/**
 * A feature layer's filter: the clause, the layer's own fields to insert into it, and its error.
 *
 * <b>The fields are the layer's, because a where clause is written in its column names</b> and the
 * first version offered an invented `population > 10000` on a layer of buildings. <b>The error sits
 * beside the input</b>, tied to it with `aria-describedby`, rather than in the panel's status line
 * where it read as being about the whole map.
 */
function wmFilterMarkup(layer, run, key, title, filter) {
  const k = wmEscape(key);
  const fields = run && run.status === "ok" ? wmUserFields(run.info) : [];
  const draft = run && typeof run.filterDraft === "string" ? run.filterDraft : filter;
  const error = run && run.filterError;

  return `<div class="lfilter">
    <label class="lkind" for="flt-${k}">Filter (a where clause)</label>
    <div class="row">
      <input type="text" id="flt-${k}" value="${wmEscape(draft)}" placeholder="${wmEscape(wmFilterExample(run && run.info))}"
        data-filter="${k}" data-focus="flt:${k}" style="flex:1;width:auto" autocomplete="off" spellcheck="false"
        ${error ? `aria-invalid="true" aria-describedby="flt-err-${k}"` : ""}>
      <button class="tiny" data-act="filter" data-layer="${k}" data-focus="apply:${k}">Apply</button>
      ${filter ? `<button class="tiny" data-act="unfilter" data-layer="${k}"
        data-focus="unfilter:${k}" data-focus-fallback="flt:${k}">Clear</button>` : ""}
    </div>
    ${fields.length ? `<select class="lfields" data-insert="${k}" data-focus="fld:${k}"
        aria-label="Insert a field of ${wmEscape(title)} into its filter">
        <option value="">Insert a field…</option>
        ${fields.map(f => `<option value="${wmEscape(f.name)}">${wmEscape(f.alias && f.alias !== f.name ? `${f.name} (${f.alias})` : f.name)}</option>`).join("")}
      </select>` : ""}
    ${error ? `<p class="lerr" id="flt-err-${k}">${wmEscape(error)}</p>` : ""}
  </div>`;
}

function wmLayerById(id) {
  return wmLayers().find(layer => layer.id === id);
}

/** Moves a layer one step up or down the stack. */
function wmMove(layer, direction) {
  const layers = wmLayers();
  const at = layers.indexOf(layer);
  const to = at + direction;
  if (at < 0 || to < 0 || to >= layers.length) return;
  layers.splice(at, 1);
  layers.splice(to, 0, layer);
  wmApplyAll();
  wmMarkDirty();
  wmDrawLayerList();
  wmSayIn("layersStatus", `${layer.title} moved ${direction > 0 ? "up" : "down"}.`);
}

/** Applies a where clause to a feature layer, after asking the layer whether it reads it. */
async function wmSetFilter(layer, expression) {
  const clause = String(expression || "").trim();
  const run = wmRuntime.get(layer);

  if (clause) {
    try {
      await wmFetch(`${layer.url}/query?` + wmParams({ where: clause, returnCountOnly: true, f: "json" }));
    } catch (e) {
      // Beside the input, and the draft kept, so the reader corrects what they typed.
      if (run) {
        run.filterError = `Not applied: ${e.message}`;
        run.filterDraft = clause;
      }
      wmDrawLayerList();
      wmSayIn("layersStatus", `The filter on ${layer.title} was not applied.`, true);
      return;
    }
  }

  if (run) {
    run.filterError = null;
    run.filterDraft = undefined;
  }

  if (!layer.layerDefinition || typeof layer.layerDefinition !== "object") layer.layerDefinition = {};

  if (clause) layer.layerDefinition.definitionExpression = clause;
  else delete layer.layerDefinition.definitionExpression;

  if (run && run.ol && run.ol.getSource && run.ol.getSource().refresh) {
    run.truncated = false;
    run.ol.getSource().refresh();
  }

  wmMarkDirty();
  wmDrawLayerList();
  wmSayIn("layersStatus", clause ? `${layer.title} is filtered.` : `${layer.title} shows every feature.`);
}

wm$("layerList").addEventListener("change", event => {
  const t = event.target;

  // Insert a field name where the cursor is in that layer's filter, and go back to the filter.
  if (t.dataset && t.dataset.insert) {
    const input = wm$(`flt-${t.dataset.insert}`);
    const name = t.value;
    t.value = "";
    if (!input || !name) return;
    const at = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? at;
    const before = input.value.slice(0, at);
    const spaced = before && !/\s$/.test(before) ? " " : "";
    input.value = `${before}${spaced}${name} ${input.value.slice(end)}`.replace(/\s+$/, " ");
    const run = wmRuntime.get(wmLayerById(t.dataset.insert));
    if (run) run.filterDraft = input.value;
    input.focus();
    const caret = before.length + spaced.length + name.length + 1;
    input.setSelectionRange(caret, caret);
    return;
  }

  const layer = t.dataset && t.dataset.layer && wmLayerById(t.dataset.layer);
  if (!layer) return;

  if (t.dataset.act === "visible") {
    layer.visibility = t.checked;
    wmApply(layer, wmLayers().indexOf(layer));
    wmMarkDirty();
    wmDrawLayerList();
  }

  if (t.dataset.act === "opacity") {
    layer.opacity = Math.max(0, Math.min(1, Number(t.value) / 100));
    wmApply(layer, wmLayers().indexOf(layer));
    wmMarkDirty();
  }
});

wm$("layerList").addEventListener("input", event => {
  const t = event.target;

  // What is typed survives a redraw of the list — a view that loads more features redraws it.
  if (t.dataset.filter) {
    const run = wmRuntime.get(wmLayerById(t.dataset.filter));
    if (run) run.filterDraft = t.value;
    return;
  }

  if (t.dataset.act !== "opacity") return;
  const layer = wmLayerById(t.dataset.layer);
  if (!layer) return;
  layer.opacity = Math.max(0, Math.min(1, Number(t.value) / 100));
  t.setAttribute("aria-valuetext", `${t.value}%`);
  wmApply(layer, wmLayers().indexOf(layer));
});

wm$("layerList").addEventListener("keydown", event => {
  const t = event.target;
  if (event.key === "Enter" && t.dataset.filter) {
    event.preventDefault();
    const layer = wmLayerById(t.dataset.filter);
    if (layer) wmSetFilter(layer, t.value);
  }
});

wm$("layerList").addEventListener("click", event => {
  const t = event.target.closest("button[data-act]");
  if (!t) return;
  const layer = wmLayerById(t.dataset.layer);
  if (!layer) return;

  switch (t.dataset.act) {
    case "up": wmMove(layer, +1); break;
    case "down": wmMove(layer, -1); break;
    case "zoom": wmFit(wmExtentOf(layer)); break;
    case "filter": {
      const input = wm$(`flt-${layer.id}`);
      wmSetFilter(layer, input ? input.value : "");
      break;
    }
    case "unfilter": wmSetFilter(layer, ""); break;
    case "remove": {
      const layers = wmLayers();
      const at = layers.indexOf(layer);
      wmRemoveFromMap(layer);
      layers.splice(at, 1);
      wmApplyAll();
      wmMarkDirty();
      wmDrawLayerList();
      wmSayIn("layersStatus", `${layer.title} was removed from the map.`);
      // Focus the neighbour the reader was next to, or the Add button when nothing is left.
      const next = layers[Math.min(at, layers.length - 1)];
      const target = next ? wm$(`vis-${next.id}`) : wm$("addLayer");
      if (target) target.focus();
      break;
    }
    default: break;
  }
});

wm$("frameAll").addEventListener("click", () => {
  const boxes = wmLayers().map(wmExtentOf).filter(Boolean);
  if (!boxes.length) {
    wmSayIn("layersStatus", "No layer on this map says where it is.");
    return;
  }
  wmFit(boxes.reduce((all, box) => ol.extent.extend(all, box), ol.extent.createEmpty()));
});

// ----------------------------------------------------------------------------- Add layer

let wmServices = null;   // [{ name, type }] once read

/** Every service this reader can see, walking the directory's folders. */
async function wmReadServices() {
  const found = [];
  const root = await wmFetch("/rest/services?f=json");

  const take = directory => {
    for (const service of directory.services || []) {
      if (["FeatureServer", "MapServer", "VectorTileServer"].includes(service.type)) {
        found.push({ name: service.name, type: service.type });
      }
    }
  };

  take(root);

  for (const folder of root.folders || []) {
    try {
      take(await wmFetch(`/rest/services/${folder.split("/").map(encodeURIComponent).join("/")}?f=json`));
    } catch {
      // A folder that will not list is a folder with nothing this reader may see.
    }
  }

  return found.sort((a, b) => a.name.localeCompare(b.name) || a.type.localeCompare(b.type));
}

const WM_TYPE_LABEL = { FeatureServer: "Features", MapServer: "Map image", VectorTileServer: "Vector tiles" };

/** The kinds a service is added as, the one a person almost always wants first. */
const WM_TYPE_ORDER = ["FeatureServer", "VectorTileServer", "MapServer"];

/** Services whose *other ways to draw it* the reader opened, so a redraw does not close it on them. */
const wmOpenedKinds = new Set();

/** Whether the map already holds this service in this form. */
function wmOnMap(name, type) {
  const needle = `/rest/services/${name}/${type}`.toLowerCase();
  return wmLayers().some(layer => {
    let at = String(layer.url || layer.styleUrl || "");
    try { at = decodeURIComponent(at); } catch { /* an address with a stray % is compared as written */ }
    return at.toLowerCase().includes(needle);
  });
}

/**
 * One kind's control: *Add*, or *On map* where the map already has it.
 *
 * <b>*On map* carries the Add button's focus key and can take focus</b>, so the keyboard lands on the
 * sentence that replaced the button it pressed, rather than falling to the page.
 */
function wmAddControl(name, type) {
  const key = `add:${type}:${name}`;
  return wmOnMap(name, type)
    ? `<span class="onmap" tabindex="-1" data-focus="${wmEscape(key)}">On map</span>`
    : `<button class="tiny" data-add="${wmEscape(name)}" data-type="${wmEscape(type)}" data-focus="${wmEscape(key)}"
        aria-label="Add ${wmEscape(name)} as ${WM_TYPE_LABEL[type] || type}">Add</button>`;
}

/**
 * The services list: one row per service, added as features by default.
 *
 * <b>One row, not one per face.</b> The directory lists a service once for each face it answers, so the
 * first version offered `wm_buildings` three times and left the reader to guess which *Add* they meant.
 * Features is what an ArcGIS map viewer adds; the map image and the vector tiles are there for whoever
 * wants them, one disclosure away.
 */
function wmDrawAddList() {
  const list = wm$("addList");
  if (!wmServices) return;

  const byName = new Map();
  for (const s of wmServices) {
    if (!byName.has(s.name)) byName.set(s.name, new Set());
    byName.get(s.name).add(s.type);
  }

  const names = [...byName.keys()];
  const needle = wm$("addFilter").value.trim().toLowerCase();
  const shown = needle ? names.filter(n => n.toLowerCase().includes(needle)) : names;

  if (names.length === 0) {
    wmRedraw(list, "");
    wmSayIn("addStatus", "There are no services you can read yet. Publish one from Studio's "
      + "My content, or ask for one to be shared with you.");
    return;
  }

  wmSayIn("addStatus", needle ? `${shown.length} of ${names.length} services match.`
    : `${names.length} service${names.length === 1 ? "" : "s"}.`);

  wmRedraw(list, shown.slice(0, 200).map(name => {
    const types = WM_TYPE_ORDER.filter(t => byName.get(name).has(t));
    const [first, ...rest] = types;

    return `<li>
      <div class="svcrow">
        <span class="name">${wmEscape(name)}</span>
        <span class="type">${WM_TYPE_LABEL[first]}</span>
        ${wmAddControl(name, first)}
      </div>
      ${rest.length ? `<details class="kinds" data-kinds="${wmEscape(name)}"${wmOpenedKinds.has(name) ? " open" : ""}>
        <summary>Other ways to draw it</summary>
        ${rest.map(type => `<div class="svcrow"><span class="name">${WM_TYPE_LABEL[type]}</span>
          ${wmAddControl(name, type)}</div>`).join("")}
      </details>` : ""}
    </li>`;
  }).join(""));
}

wm$("addList").addEventListener("toggle", event => {
  const box = event.target;
  if (!(box instanceof HTMLDetailsElement) || !box.dataset.kinds) return;
  if (box.open) wmOpenedKinds.add(box.dataset.kinds); else wmOpenedKinds.delete(box.dataset.kinds);
}, true);

wm$("addLayer").addEventListener("click", async () => {
  const box = wm$("addBox");
  const open = box.hidden;
  box.hidden = !open;
  wm$("addLayer").setAttribute("aria-expanded", String(open));

  if (!open) return;

  wm$("addFilter").focus();

  if (wmServices) {
    wmDrawAddList();
    return;
  }

  wmSayIn("addStatus", "Reading the services you can see…");

  try {
    wmServices = await wmReadServices();
    wmDrawAddList();
  } catch (e) {
    wmSayIn("addStatus", `The services directory could not be read: ${e.message}`, true);
  }
});

wm$("addFilter").addEventListener("input", wmDrawAddList);

/*
  <b>Busy, not disabled.</b> Disabling the pressed button dropped the keyboard to `<body>` — a disabled
  element cannot hold focus — so a keyboard user who pressed *Add* was sent to the top of the page. The
  button says it is busy with `aria-disabled` and ignores a second press instead, keeps focus, and the
  redraw after it hands focus to the *On map* that replaced it.
*/
wm$("addList").addEventListener("click", async event => {
  const t = event.target.closest("button[data-add]");
  if (!t || t.getAttribute("aria-disabled") === "true") return;
  t.setAttribute("aria-disabled", "true");

  const name = t.dataset.add;

  try {
    const added = await wmAddService(name, t.dataset.type);
    wmSayIn("addStatus", added === 0
      ? `${name} has no layer to add.`
      : `Added ${added} layer${added === 1 ? "" : "s"} from ${name}.`);
    if (added > 0) wmMarkDirty();
  } catch (e) {
    wmSayIn("addStatus", `${name} could not be added: ${e.message}`, true);
  } finally {
    t.removeAttribute("aria-disabled");
    wmDrawAddList();
  }
});

function wmServiceUrl(name, type) {
  return `${location.origin}/rest/services/${name.split("/").map(encodeURIComponent).join("/")}/${type}`;
}

/**
 * Puts a service's layers on the map: each feature layer as its own entry, a map service and a
 * tile service as one. Answers how many were added.
 */
async function wmAddService(name, type, onlyLayer = null) {
  const url = wmServiceUrl(name, type);
  const entries = [];

  if (type === "FeatureServer") {
    const document_ = await wmFetch(`${url}?f=json`);
    const layers = (document_.layers || []).filter(l => l.type !== "Group Layer" && (l.subLayerIds || []).length === 0);

    // <b>Last layer first, so layer 0 is drawn on top</b> — the order ArcGIS draws a service in, and
    // the order its layer list reads. A web map's operational layers are bottom first.
    for (const one of [...layers].reverse()) {
      if (onlyLayer !== null && String(one.id) !== String(onlyLayer)) continue;
      entries.push({
        id: wmNewId(),
        layerType: "ArcGISFeatureLayer",
        url: `${url}/${one.id}`,
        title: layers.length === 1 ? name.split("/").pop() : `${one.name}`,
        visibility: true,
        opacity: 1,
      });
    }
  } else if (type === "MapServer") {
    await wmFetch(`${url}?f=json`);
    entries.push({
      id: wmNewId(), layerType: "ArcGISMapServiceLayer", url, title: name.split("/").pop(),
      visibility: true, opacity: 1,
    });
  } else if (type === "VectorTileServer") {
    await wmFetch(`${url}?f=json`);
    entries.push({
      id: wmNewId(), layerType: "VectorTileLayer", styleUrl: `${url}/resources/styles/root.json`,
      title: name.split("/").pop(), visibility: true, opacity: 1,
    });
  }

  for (const entry of entries) {
    wmLayers().push(entry);
  }

  wmDrawLayerList();
  await Promise.all(entries.map(wmLoadLayer));
  wmApplyAll();
  wmDrawLayerList();

  // A map that was empty is framed on what was just added; one that was not keeps its view.
  if (wmLayers().length === entries.length) {
    const boxes = entries.map(wmExtentOf).filter(Boolean);
    if (boxes.length) wmFit(boxes.reduce((all, box) => ol.extent.extend(all, box), ol.extent.createEmpty()));
  }

  wmSaySummary();
  wmCheckSharing();
  return entries.length;
}

// ----------------------------------------------------------------------------- identify

/** The feature layers a click or a search reads: drawn here, readable, and switched on. */
function wmQueryable({ visibleOnly }) {
  return wmLayers().filter(layer => {
    const run = wmRuntime.get(layer);
    return wmKind(layer) === "feature" && run && run.status === "ok"
      && (!visibleOnly || layer.visibility !== false);
  });
}

/**
 * Whether an attribute is the database's bookkeeping rather than something about the feature — the
 * object id, the global id, and the area and length a geodatabase keeps beside the shape. ArcGIS's
 * pop-ups leave them out by default, and a card that leads with `objectid 1` and a GUID reads as a
 * database row rather than as a place.
 */
function wmSystemField(name, info) {
  const lower = String(name).toLowerCase();
  if (info) {
    if ([info.objectIdField, info.globalIdField].filter(Boolean).some(n => n.toLowerCase() === lower)) return true;
    const field = (info.fields || []).find(f => f.name && f.name.toLowerCase() === lower);
    if (field && ["esriFieldTypeOID", "esriFieldTypeGlobalID", "esriFieldTypeGeometry"].includes(field.type)) return true;
  }
  return /^(objectid|fid|globalid|shape__?(area|length)|shape_(area|length)|st_(area|length)\(.*\))$/i.test(lower);
}

function wmAttributeRows(attributes, info) {
  const aliases = new Map(((info && info.fields) || []).map(f => [f.name, f.alias || f.name]));
  return Object.keys(attributes || {}).filter(key => !wmSystemField(key, info)).map(key => `<tr><th scope="row">${wmEscape(aliases.get(key) || key)}</th>
    <td>${wmEscape(attributes[key] === null ? "—" : attributes[key])}</td></tr>`).join("");
}

let wmIdentifyTurn = 0;

/** The Draw interaction while measuring; a click then adds a point instead of identifying. */
let wmMeasuring = null;

/**
 * What a click on the map found, across every feature layer that is switched on.
 *
 * <b>A card only when there is something to show.</b> The first version opened *What is here* on
 * every click and then said *nothing here* in it — a panel over the map that the reader then had to
 * close. Asking, finding nothing, and failing to ask are said in the header's status line instead;
 * a card appears for features, or for a layer that could not be asked.
 */
async function wmIdentify(coordinate) {
  const card = wm$("identify");
  const turn = ++wmIdentifyTurn;
  const layers = wmQueryable({ visibleOnly: true });

  wmHighlight.getSource().clear();
  card.hidden = true;

  if (layers.length === 0) {
    wmSay("No feature layer is switched on, so a click has nothing to ask. Map image and tile layers are drawn, not identified.");
    return;
  }

  const tolerance = wmMap.getView().getResolution() * 6;
  const box = [coordinate[0] - tolerance, coordinate[1] - tolerance, coordinate[0] + tolerance, coordinate[1] + tolerance];

  wmSay(`Asking ${layers.length} layer${layers.length === 1 ? "" : "s"} what is here…`);

  const answers = await Promise.all(layers.map(async layer => {
    try {
      const payload = await wmFetch(`${layer.url}/query?` + wmParams({
        where: wmWhere(layer),
        geometry: box.join(","),
        geometryType: "esriGeometryEnvelope",
        inSR: 3857,
        spatialRel: "esriSpatialRelIntersects",
        outFields: "*",
        returnGeometry: true,
        outSR: 3857,
        resultRecordCount: 10,
        f: "json",
      }));
      return { layer, payload };
    } catch (e) {
      return { layer, error: e.message || String(e) };
    }
  }));

  if (turn !== wmIdentifyTurn) return;

  let count = 0;
  let failed = 0;
  const sections = answers.map(({ layer, payload, error }) => {
    const info = (wmRuntime.get(layer) || {}).info;
    if (error) {
      failed++;
      return `<h3>${wmEscape(layer.title)}</h3><p>Could not be asked: ${wmEscape(error)}</p>`;
    }

    const features = (payload && payload.features) || [];
    if (!features.length) return "";

    count += features.length;
    wmHighlight.getSource().addFeatures(WM_ESRI.readFeatures(payload, { featureProjection: WM_MERCATOR }));

    return `<h3>${wmEscape(layer.title)} <span class="lkind">${features.length}${payload.exceededTransferLimit ? "+" : ""}</span></h3>`
      + features.map(f => `<table class="feature">${wmAttributeRows(f.attributes, info)
        || `<tr><td>No attributes besides its identifiers.</td></tr>`}</table>`).join("");
  }).join("");

  if (!sections) {
    wmSay(`Nothing here on the ${layers.length} layer${layers.length === 1 ? "" : "s"} switched on.`);
    return;
  }

  card.innerHTML = `<div class="top"><b>What is here</b>
      <button class="tiny" data-close aria-label="Close">&times;</button></div>${sections}`;
  card.hidden = false;

  wmSay(count
    ? `${count} feature${count === 1 ? "" : "s"} here${failed ? `; ${failed} layer${failed === 1 ? "" : "s"} could not be asked` : ""}.`
    : `${failed} layer${failed === 1 ? "" : "s"} could not be asked.`);
}

wm$("identify").addEventListener("click", event => {
  if (event.target.closest("[data-close]")) wmCloseIdentify();
});

function wmCloseIdentify() {
  wm$("identify").hidden = true;
  wmHighlight.getSource().clear();
  wm$("map").focus();
}

document.addEventListener("keydown", event => {
  if (event.key === "Escape" && !wm$("identify").hidden) wmCloseIdentify();
});

wmMap.on("singleclick", event => {
  if (wmMeasuring) return;
  wmIdentify(event.coordinate);
});

// ----------------------------------------------------------------------------- search

/** The features the last search found, by the index their result button carries. */
let wmSearchHits = [];

wm$("searchForm").addEventListener("submit", async event => {
  event.preventDefault();

  const text = wm$("searchText").value.trim();
  const results = wm$("searchResults");

  if (!text) {
    wmSayIn("searchStatus", "Type something to look for.");
    results.innerHTML = "";
    return;
  }

  const layers = wmQueryable({ visibleOnly: false });

  if (!layers.length) {
    wmSayIn("searchStatus", "This map has no feature layer you can read, so there is nothing to search.");
    results.innerHTML = "";
    return;
  }

  wmSayIn("searchStatus", `Searching ${layers.length} layer${layers.length === 1 ? "" : "s"}…`);
  results.innerHTML = "";

  // UPPER(field) LIKE '%TEXT%', which this server's where clause reads (WhereClause.cs) and which is
  // how the Maps SDK's own search asks.
  const quoted = text.toUpperCase().replace(/'/g, "''");

  const answers = await Promise.all(layers.map(async layer => {
    const info = wmRuntime.get(layer).info || {};
    const fields = (info.fields || []).filter(f => f.type === "esriFieldTypeString").map(f => f.name);
    if (!fields.length) return { layer, skipped: true };

    const match = fields.map(name => `UPPER(${name}) LIKE '%${quoted}%'`).join(" OR ");
    const where = wmWhere(layer) === "1=1" ? match : `(${wmWhere(layer)}) AND (${match})`;

    try {
      const payload = await wmFetch(`${layer.url}/query?` + wmParams({
        where, outFields: "*", returnGeometry: true, outSR: 3857, resultRecordCount: 25, f: "json",
      }));
      return { layer, payload, info, fields };
    } catch (e) {
      return { layer, error: e.message || String(e) };
    }
  }));

  wmSearchHits = [];
  const blocks = [];

  for (const answer of answers) {
    if (answer.skipped || !answer.payload && !answer.error) continue;
    if (answer.error) {
      blocks.push(`<li class="group">${wmEscape(answer.layer.title)}</li><li class="said bad">${wmEscape(answer.error)}</li>`);
      continue;
    }

    const features = WM_ESRI.readFeatures(answer.payload, { featureProjection: WM_MERCATOR });
    if (!features.length) continue;

    const labelField = answer.info.displayField || answer.fields[0];
    blocks.push(`<li class="group">${wmEscape(answer.layer.title)}</li>`
      + features.map(feature => {
        const at = wmSearchHits.push({ feature, layer: answer.layer }) - 1;
        const label = feature.get(labelField) ?? answer.fields.map(f => feature.get(f)).find(Boolean) ?? `#${feature.getId()}`;
        return `<li><button data-hit="${at}" data-focus="hit:${at}">${wmEscape(label)}</button></li>`;
      }).join(""));
  }

  const found = wmSearchHits.length;
  results.innerHTML = blocks.join("");
  wmSayIn("searchStatus", found
    ? `${found} match${found === 1 ? "" : "es"} for “${text}”.`
    : `Nothing matches “${text}” in the text fields of ${layers.length} layer${layers.length === 1 ? "" : "s"}.`);
});

wm$("searchResults").addEventListener("click", event => {
  const t = event.target.closest("button[data-hit]");
  if (!t) return;
  const hit = wmSearchHits[Number(t.dataset.hit)];
  if (!hit) return;

  wmHighlight.getSource().clear();
  wmHighlight.getSource().addFeature(hit.feature.clone());

  const geometry = hit.feature.getGeometry();
  if (geometry) wmFit(geometry.getExtent());

  const info = (wmRuntime.get(hit.layer) || {}).info;
  const attributes = { ...hit.feature.getProperties() };
  delete attributes[hit.feature.getGeometryName()];

  const card = wm$("identify");
  card.innerHTML = `<div class="top"><b>${wmEscape(hit.layer.title)}</b>
    <button class="tiny" data-close aria-label="Close">&times;</button></div>
    <table class="feature">${wmAttributeRows(attributes, info)}</table>`;
  card.hidden = false;
  wmSay(`Showing ${t.textContent.trim()} from ${hit.layer.title}.`);
});

// ----------------------------------------------------------------------------- measure

function wmFormatLength(metres) {
  return metres >= 1000 ? `${(metres / 1000).toLocaleString(undefined, { maximumFractionDigits: 3 })} km`
    : `${metres.toLocaleString(undefined, { maximumFractionDigits: 1 })} m`;
}

function wmFormatArea(square) {
  if (square >= 1e6) return `${(square / 1e6).toLocaleString(undefined, { maximumFractionDigits: 3 })} km²`;
  if (square >= 1e4) return `${(square / 1e4).toLocaleString(undefined, { maximumFractionDigits: 2 })} ha`;
  return `${square.toLocaleString(undefined, { maximumFractionDigits: 1 })} m²`;
}

function wmMeasureOf(geometry) {
  return geometry.getType() === "Polygon"
    ? wmFormatArea(ol.sphere.getArea(geometry, { projection: WM_MERCATOR }))
      + ` · perimeter ${wmFormatLength(ol.sphere.getLength(geometry, { projection: WM_MERCATOR }))}`
    : wmFormatLength(ol.sphere.getLength(geometry, { projection: WM_MERCATOR }));
}

function wmStopMeasuring() {
  const live = wm$("measureLive");
  if (live) live.textContent = "";
  if (wmMeasuring) wmMap.removeInteraction(wmMeasuring);
  wmMeasuring = null;
  wm$("measureLine").setAttribute("aria-pressed", "false");
  wm$("measureArea").setAttribute("aria-pressed", "false");
}

function wmStartMeasuring(type) {
  const already = wmMeasuring && wmMeasuring.get("kind") === type;
  wmStopMeasuring();
  wm$("identify").hidden = true;
  wmHighlight.getSource().clear();
  if (already) {
    wmSayIn("measureStatus", "Measuring stopped.");
    return;
  }

  const draw = new ol.interaction.Draw({ source: wmMeasured.getSource(), type });
  draw.set("kind", type);

  // <b>The running figure goes to a line that is not announced.</b> It changes on every pointer move,
  // and a live region that does would read a number out for each pixel; the result is announced once,
  // when the shape is finished.
  draw.on("drawstart", event => {
    event.feature.getGeometry().on("change", change => {
      wm$("measureLive").textContent = wmMeasureOf(change.target);
    });
  });

  draw.on("drawend", event => {
    const said = wmMeasureOf(event.feature.getGeometry());
    wm$("measureLive").textContent = "";
    wmSayIn("measureStatus", `${type === "Polygon" ? "Area" : "Distance"}: ${said}`);
    const item = document.createElement("li");
    item.textContent = `${type === "Polygon" ? "Area" : "Distance"} — ${said}`;
    wm$("measureList").prepend(item);
  });

  wmMap.addInteraction(draw);
  wmMeasuring = draw;
  wm$(type === "Polygon" ? "measureArea" : "measureLine").setAttribute("aria-pressed", "true");
  wmSayIn("measureStatus", type === "Polygon"
    ? "Click the corners of the area; double-click the last one."
    : "Click along the line; double-click the last point.");
}

wm$("measureLine").addEventListener("click", () => wmStartMeasuring("LineString"));
wm$("measureArea").addEventListener("click", () => wmStartMeasuring("Polygon"));
wm$("measureClear").addEventListener("click", () => {
  wmStopMeasuring();
  wmMeasured.getSource().clear();
  wm$("measureList").innerHTML = "";
  wmSayIn("measureStatus", "Measurements cleared.");
});

// ----------------------------------------------------------------------------- tabs

const WM_TABS = ["layers", "search", "measure", "map"];

function wmShowTab(name, focus = false) {
  for (const tab of WM_TABS) {
    const button = wm$(`tab-${tab}`);
    const on = tab === name;
    button.setAttribute("aria-selected", String(on));
    button.tabIndex = on ? 0 : -1;
    wm$(`pane-${tab}`).hidden = !on;
    if (on && focus) button.focus();
  }

  // Measuring belongs to its tab; leaving it puts the map back to identifying.
  if (name !== "measure") wmStopMeasuring();
  if (name === "map") wmDrawMapForm();
}

wm$("tabs").addEventListener("click", event => {
  const tab = event.target.closest("[role=tab]");
  if (tab) wmShowTab(tab.id.replace("tab-", ""));
});

wm$("tabs").addEventListener("keydown", event => {
  const at = WM_TABS.indexOf((document.activeElement.id || "").replace("tab-", ""));
  if (at < 0) return;
  const step = { ArrowRight: 1, ArrowLeft: -1 }[event.key];
  if (step) {
    event.preventDefault();
    wmShowTab(WM_TABS[(at + step + WM_TABS.length) % WM_TABS.length], true);
  } else if (event.key === "Home" || event.key === "End") {
    event.preventDefault();
    wmShowTab(WM_TABS[event.key === "Home" ? 0 : WM_TABS.length - 1], true);
  }
});

// ----------------------------------------------------------------------------- the Map tab

function wmMaySave() {
  if (!wmToken || !wmMe.authenticated) return "Sign in to Studio to save this map.";
  if (!wmMe.privileges.has("content:create")) return "Your role cannot create content, so it cannot save maps.";
  return null;
}

function wmDrawMapForm() {
  const meta = wmState.meta;
  const blocked = wmMaySave();
  const ownsIt = !wmState.id || meta.manages;

  wm$("mapTitle").value = meta.title || "";
  wm$("mapSnippet").value = meta.snippet || "";
  wm$("mapSharing").value = meta.sharing || "private";
  wm$("mapBasemap").value = wmBaseMapOf(wmState.doc);

  // <b>Not changed while saving</b> — see wmSave — so these only say whether saving is possible.
  wm$("mapSave").disabled = !!blocked || !ownsIt;
  wm$("mapSaveAs").disabled = !!blocked || !wmState.id;
  wm$("mapDelete").hidden = !wmState.id || !meta.manages;

  // Editable for somebody who may not save over it: what they type is what *Save as* keeps.
  for (const id of ["mapTitle", "mapSnippet"]) {
    wm$(id).disabled = !!blocked;
  }

  // <b>Who can open it is a control only for somebody who can save it.</b> A reader who cannot was
  // shown a disabled select, which reads as a setting they are being refused rather than as a fact.
  wm$("sharingField").hidden = !!blocked;

  const owner = meta.owner ? `<b>${wmEscape(meta.owner)}</b>` : "somebody else";
  const scope = { private: "only its owner", organization: "everyone signed in", public: "anyone with the link" };

  wm$("mapOwner").innerHTML = (!wmState.id
    ? "This map has not been saved yet."
    : meta.mine
      ? `Yours. Last saved ${wmEscape(new Date(meta.modified).toLocaleString())}.`
      : `Owned by ${owner}; ${wmEscape(scope[meta.sharing] || meta.sharing)} can open it. ${blocked
        ? ""
        : meta.manages
          ? "You can change it because you administer content."
          : "You can change what you see and <b>Save as new map</b> to keep your own copy."}`)
    + (blocked ? ` ${wmEscape(blocked)} <a href="/studio/">Open Studio</a>` : "");

  wmDrawSharingNote();
}

// ----------------------------------------------------------------------------- sharing reach

/** Each service's scope by qualified name, from Studio's content listing; null until read. */
let wmServiceScopes = null;

/** Layers whose services are shared more narrowly than the map, and layers nobody here can check. */
let wmNarrower = { narrow: [], unknown: [] };

const WM_SCOPE_RANK = { private: 0, group: 1, organization: 2, public: 3 };

/** The qualified service name a layer on this server comes from, or null for another server's. */
function wmServiceOf(layer) {
  let address;
  try { address = new URL(layer.url || layer.styleUrl || "", location.href); } catch { return null; }
  if (address.origin !== location.origin) return null;
  let path = address.pathname;
  try { path = decodeURIComponent(path); } catch { /* compared as written */ }
  const match = /\/rest\/services\/(.+?)\/(FeatureServer|MapServer|VectorTileServer)(\/|$)/.exec(path);
  return match ? match[1] : null;
}

/**
 * Works out which of the map's layers people it is shared with would not see.
 *
 * <b>From what this page can learn cheaply, and it says where that runs out.</b> Studio's own content
 * listing — `/content/items`, which a signed-in reader already has — carries each service's scope. A
 * layer from a service this reader cannot see at all, or from another server, cannot be checked and is
 * counted as such rather than assumed fine. A group-shared service reaches fewer people than an
 * organisation map, so it counts as narrower too.
 */
async function wmCheckSharing() {
  if (!wmMe.authenticated) return;

  if (wmServiceScopes === null) {
    try {
      const answer = await wmFetch("/content/items");
      wmServiceScopes = new Map((answer.items || []).map(i => [String(i.name).toLowerCase(), i.sharing]));
    } catch {
      wmServiceScopes = new Map();
    }
  }

  const narrow = [];
  const unknown = [];
  const mapRank = WM_SCOPE_RANK[wmState.meta.sharing] ?? 0;

  for (const layer of wmLayers()) {
    const service = wmServiceOf(layer);
    const scope = service ? wmServiceScopes.get(service.toLowerCase()) : undefined;
    if (!scope) {
      if (mapRank > 0) unknown.push(layer);
      continue;
    }
    if ((WM_SCOPE_RANK[scope] ?? 0) < mapRank) narrow.push({ layer, scope });
  }

  wmNarrower = { narrow, unknown };
  wmDrawSharingNote();
}

function wmSharingWarning() {
  const { narrow, unknown } = wmNarrower;
  if (!narrow.length && !unknown.length) return "";

  const reach = wmState.meta.sharing === "public" ? "people you share it with" : "others in your organisation";
  const parts = [];

  if (narrow.length === 1) {
    const { layer, scope } = narrow[0];
    parts.push(`1 layer is from a ${scope === "group" ? "group-only" : scope} service; ${reach} won't see it: ${layer.title}.`);
  } else if (narrow.length) {
    const byScope = {};
    for (const { scope } of narrow) byScope[scope] = (byScope[scope] || 0) + 1;
    const said = Object.entries(byScope).map(([scope, n]) =>
      `${n} ${scope === "group" ? "group-only" : scope}`).join(", ");
    parts.push(`${narrow.length} layers are from services shared more narrowly (${said}); `
      + `${reach} won't see them: ${narrow.map(n => n.layer.title).join(", ")}.`);
  }

  if (unknown.length) {
    parts.push(`${unknown.length} layer${unknown.length === 1 ? "" : "s"} could not be checked, because the service is not one you can see or is on another server.`);
  }

  return parts.join(" ");
}

function wmDrawSharingNote() {
  const note = wm$("sharingNote");
  if (!note) return;
  const text = (wmState.meta.sharing || "private") === "private" ? "" : wmSharingWarning();
  note.textContent = text;
  note.hidden = !text;
}

wm$("mapTitle").addEventListener("input", () => {
  wmState.meta.title = wm$("mapTitle").value;
  wmMarkDirty();
});
wm$("mapSnippet").addEventListener("input", () => {
  wmState.meta.snippet = wm$("mapSnippet").value;
  wmMarkDirty();
});
wm$("mapSharing").addEventListener("change", () => {
  wmState.meta.sharing = wm$("mapSharing").value;
  wmMarkDirty();
  wmCheckSharing();
});
wm$("mapBasemap").addEventListener("change", () => {
  const which = wm$("mapBasemap").value;
  wmState.doc.baseMap = wmBaseMapJson(which);
  wmBase.setVisible(which !== "none");
  wmMarkDirty();
});

/** Writes the current view into the document, as ArcGIS's map viewer does on save. */
function wmCaptureView() {
  const extent = wmMap.getView().calculateExtent(wmMap.getSize());
  const box = wmQueryBox(extent);

  if (!wmState.doc.initialState || typeof wmState.doc.initialState !== "object") wmState.doc.initialState = {};
  if (!wmState.doc.initialState.viewpoint || typeof wmState.doc.initialState.viewpoint !== "object") {
    wmState.doc.initialState.viewpoint = {};
  }

  wmState.doc.initialState.viewpoint.targetGeometry = {
    xmin: box[0], ymin: box[1], xmax: box[2], ymax: box[3],
    spatialReference: { wkid: 102100, latestWkid: 3857 },
  };

  if (!wmState.doc.spatialReference) wmState.doc.spatialReference = { wkid: 102100, latestWkid: 3857 };
  if (!wmState.doc.version) wmState.doc.version = "2.31";
}

/** True while a save is in flight; a second press is ignored rather than the button disabled. */
let wmSaving = false;

/**
 * Saves the map, or a copy of it.
 *
 * <b>Read from the page's state, not from the Map tab's inputs</b>, because the header's Save works
 * without that tab ever having been opened. <b>No button is disabled while it runs</b>: the one pressed
 * had focus, and a disabled button drops it to `<body>`. They say `aria-busy` instead, a second press
 * is ignored, and focus is put back on the button pressed if anything took it away.
 */
async function wmSave(asNew, invoker = null) {
  if (wmSaving) return;

  const blocked = wmMaySave();
  if (blocked) {
    wmSayIn("mapStatus", blocked, true);
    wmSay(blocked, true);
    return;
  }

  const title = String(wmState.meta.title || "").trim();
  if (!title) {
    wmShowTab("map");
    wmSayIn("mapStatus", "A map needs a title.", true);
    wm$("mapTitle").focus();
    return;
  }

  const creating = asNew || !wmState.id;

  // <b>A copy starts private.</b> Save as makes something new of one's own; it inherited the
  // original's scope, so copying a public map published a second public map nobody chose to publish.
  const sharing = asNew ? "private" : (wmState.meta.sharing || "private");

  if (!asNew && sharing !== "private" && (creating || sharing !== wmState.meta.savedSharing)) {
    await wmCheckSharing();
    const warning = wmSharingWarning();
    if (warning && !confirm(`${warning}\n\nSave it as ${sharing} anyway?`)) {
      wmSayIn("mapStatus", "Not saved. Change who can open it, or the layers, first.");
      if (invoker) invoker.focus();
      return;
    }
  }

  wmCaptureView();

  const body = {
    title: asNew && title === wmState.meta.savedTitle ? `${title} (copy)` : title,
    snippet: String(wmState.meta.snippet || "").trim(),
    sharing,
    document: wmState.doc,
  };

  wmSaving = true;
  const buttons = ["mapSave", "mapSaveAs", "headSave"].map(wm$).filter(Boolean);
  for (const b of buttons) b.setAttribute("aria-busy", "true");

  const said = creating ? "Saving a new map…" : "Saving…";
  wmSayIn("mapStatus", said);
  wmSay(said);

  try {
    const saved = await wmFetch(creating ? "/content/webmaps" : `/content/webmaps/${wmState.id}`, {
      method: creating ? "POST" : "PUT",
      headers: { "Content-Type": "application/json" },
      body: wmJson(body),
    });

    wmAdopt(saved);
    wmState.dirty = false;
    history.replaceState(null, "", `?id=${encodeURIComponent(saved.id)}`);
    wmDrawSaved();
    wmDrawMapForm();
    const done = creating ? `Saved as “${saved.title}”${asNew ? ", private" : ""}.` : "Saved.";
    wmSayIn("mapStatus", done);
    wmSay(done);
  } catch (e) {
    wmDrawMapForm();
    wmSayIn("mapStatus", `Not saved: ${e.message}`, true);
    wmSay(`Not saved: ${e.message}`, true);
  } finally {
    wmSaving = false;
    for (const b of buttons) b.removeAttribute("aria-busy");
    const lost = !document.activeElement || document.activeElement === document.body;
    if (invoker && lost && !invoker.hidden && !invoker.disabled) invoker.focus();
  }
}

/** Takes the server's answer about a saved map as the page's own. */
function wmAdopt(saved) {
  wmState.id = saved.id;
  wmState.meta = {
    title: saved.title,
    savedTitle: saved.title,
    snippet: saved.snippet || "",
    sharing: saved.sharing,
    owner: saved.owner,
    manages: saved.manages,
    mine: saved.mine,
    modified: saved.modified,
    savedSharing: saved.sharing,
  };
}

wm$("mapForm").addEventListener("submit", event => {
  event.preventDefault();
  wmSave(false, wm$("mapSave"));
});
wm$("mapSaveAs").addEventListener("click", () => wmSave(true, wm$("mapSaveAs")));

// The header's Save: a copy, for somebody who can read the map and not change it.
wm$("headSave").addEventListener("click", () => {
  const button = wm$("headSave");
  if (button.getAttribute("aria-disabled") === "true") return;
  wmSave(!!wmState.id && !wmState.meta.manages, button);
});

wm$("mapDelete").addEventListener("click", async () => {
  if (!wmState.id) return;
  if (!confirm(`Delete “${wmState.meta.title}”? Anybody it was shared with loses it too. This cannot be undone.`)) return;

  wmSayIn("mapStatus", "Deleting…");

  try {
    await wmFetch(`/content/webmaps/${wmState.id}`, { method: "DELETE" });
    wmState.dirty = false;
    location.href = "/studio/#/content";
  } catch (e) {
    wmSayIn("mapStatus", `Not deleted: ${e.message}`, true);
  }
});

// ----------------------------------------------------------------------------- start

/** Draws a document: basemap, layers, view. */
async function wmOpen(doc) {
  wmState.doc = doc && typeof doc === "object" ? doc : wmEmptyDocument();

  for (const layer of wmLayers()) {
    if (!layer.id) layer.id = wmNewId();
  }

  wmBase.setVisible(wmBaseMapOf(wmState.doc) !== "none");

  const target = wmState.doc.initialState && wmState.doc.initialState.viewpoint
    && wmState.doc.initialState.viewpoint.targetGeometry;

  if (target && Number.isFinite(target.xmin)) {
    const reference = target.spatialReference || {};
    const wkid = reference.latestWkid || reference.wkid || 102100;
    try {
      wmFit(ol.proj.transformExtent([target.xmin, target.ymin, target.xmax, target.ymax],
        wkid === 102100 || wkid === 3857 ? WM_MERCATOR : `EPSG:${wkid}`, WM_MERCATOR));
    } catch {
      // A view in a reference this page does not know opens at the world instead.
    }
  }

  wmDrawLayerList();
  await Promise.all(wmLayers().map(wmLoadLayer));
  wmApplyAll();
  wmDrawLayerList();

  // Nothing said where to look: frame what could be read.
  if (!target) {
    const boxes = wmLayers().map(wmExtentOf).filter(Boolean);
    if (boxes.length) wmFit(boxes.reduce((all, box) => ol.extent.extend(all, box), ol.extent.createEmpty()));
  }

  wmSaySummary();
  wmCheckSharing();
}

/** The header's line about the map as it now is, so it is never left describing an earlier state. */
function wmSaySummary() {
  const layers = wmLayers();
  const unavailable = layers.filter(l => (wmRuntime.get(l) || {}).status === "unavailable").length;
  wmSay(layers.length === 0
    ? "This map has no layers yet."
    : `${layers.length} layer${layers.length === 1 ? "" : "s"}`
      + (unavailable ? `, ${unavailable} not available to you.` : "."));
}

async function wmStart() {
  try {
    let me = await wmFetch("/rest/whoami");

    if (me.authenticated && !wmToken && await wmExchangeSession()) {
      me = await wmFetch("/rest/whoami");
    }

    wmMe = {
      authenticated: !!me.authenticated,
      name: me.name,
      privileges: new Set(me.privileges || []),
    };
  } catch {
    // Anonymous, or the server is not answering; the map will say which when it loads.
  }

  const id = WM_QUERY.get("id");
  const service = WM_QUERY.get("service");

  if (id) {
    try {
      const saved = await wmFetch(`/content/webmaps/${encodeURIComponent(id)}`);
      wmAdopt(saved);
      wmDrawSaved();
      await wmOpen(saved.document);
    } catch (e) {
      wmState.doc = wmEmptyDocument();
      wmDrawSaved();
      wm$("title").textContent = "Map not found";
      wmSay(e.status === 404
        ? "This map does not exist, or it is not shared with you."
        : `The map could not be read: ${e.message}`, true);

      // <b>Nothing to work on, so no tools to work with.</b> The tabs, Add layer and Save offered to
      // edit a map that is not there. Studio's sign-in has no return address, so the sentence says to
      // come back to this link rather than promising a redirect that does not exist.
      wm$("tabs").hidden = true;
      for (const tab of WM_TABS) wm$(`pane-${tab}`).hidden = true;
      wm$("headSave").hidden = true;
      wm$("saved").textContent = "";
      const gone = wm$("notFound");
      gone.hidden = false;
      gone.innerHTML = e.status === 404
        ? `<p>No map <code>${wmEscape(id)}</code> that you can open.</p><p>${wmMe.authenticated
          ? "It may have been deleted, or its owner has not shared it with you."
          : `If it is shared with your organisation, <a href="/studio/">sign in to Studio</a>, then open this link again.`}</p>
          <p><a href="/studio/#/content">Your maps</a></p>`
        : `<p>${wmEscape(e.message)}</p>`;
      return;
    }
    return;
  }

  wmState.doc = wmEmptyDocument();

  if (service) {
    wmState.meta.title = service.split("/").pop();
    wmDrawSaved();
    await wmOpen(wmState.doc);

    const layer = WM_QUERY.get("layer");
    let added = 0;
    let last = null;

    for (const type of ["FeatureServer", "VectorTileServer", "MapServer"]) {
      try {
        added = await wmAddService(service, type, type === "FeatureServer" ? layer : null);
        if (added > 0) break;
      } catch (e) {
        last = e;
      }
    }

    wmSay(added > 0
      ? `${added} layer${added === 1 ? "" : "s"} from ${service}. Not saved yet.`
      : `${service} could not be added${last ? `: ${last.message}` : "."}`, added === 0);
    return;
  }

  wmDrawSaved();
  await wmOpen(wmState.doc);
  wmSay("A new, empty map. Add layer lists the services you can read.");
}

wmStart().catch(e => wmSay(`The map could not start: ${e.message || e}`, true));
