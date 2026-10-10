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

/** How far past a visible range's bound still counts as inside it — rounding, not a wider range. */
const WM_RANGE_SLACK = 1.001;

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

/** The operator's map ground, drawn in its services' own styles, bottom first — ADR-086. */
const wmGround = new ol.layer.Group({ zIndex: 0, visible: false });

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
  layers: [wmBase, wmGround, wmHighlight, wmMeasured],
  view: new ol.View({ projection: WM_MERCATOR, center: [0, 0], zoom: 2 }),
  // <b>The wheel zooms whether or not the map has focus — 2026-10-04.</b> OpenLayers' own default is
  // `onFocusOnly: true`, which does nothing on a target with no `tabindex` and everything on this one:
  // `#map` carries `tabindex="0"` for the keyboard, so the wheel was ignored on first open and again
  // after every click in the side panel, until a drag gave the map focus back. The owner reported it as
  // *the wheel does not zoom until I pan*. The guard is for a map inside a scrolling page, where the wheel
  // belongs to the page; this map fills the window and nothing behind it scrolls.
  interactions: ol.interaction.defaults.defaults({ onFocusOnly: false }),
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

/**
 * Fits the view to a box once the map has a size; a map with no size yet fits to nothing.
 *
 * <b>`margin` is for framing data, and a saved view takes none — 2026-09-30.</b> The saved viewpoint is
 * the box the author was looking at; fitting it inside a 40-pixel margin zoomed out by that margin, and
 * saving the result stored the larger box. The design review measured zoom 13.77 → 13.63 → 13.49 over
 * three save-and-reopen rounds, on a map Studio promises opens *as you left it*.
 */
function wmFit(extent, frames = 0, margin = 40) {
  if (!extent || !extent.every(Number.isFinite)) return;

  const size = wmMap.getSize();

  if (!size || !size[0] || !size[1]) {
    if (frames < 60) requestAnimationFrame(() => wmFit(extent, frames + 1, margin));
    return;
  }

  const degenerate = extent[2] - extent[0] < 1 && extent[3] - extent[1] < 1;
  const box = degenerate ? ol.extent.buffer(extent, 250) : extent;

  wmMap.getView().fit(box, { padding: [margin, margin, margin, margin], maxZoom: 18 });
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

/**
 * The operator's map ground as Web Map basemap layers — ADR-086, 2026-09-30.
 *
 * <b>The same list the portal publishes as `defaultBasemap`</b>, so a map saved with it opens on the same
 * ground in Map Viewer and in Pro. Until 2026-09-30 this page offered OpenStreetMap or nothing, while
 * every other page of the server drew the ground the operator chose on the Settings screen.
 */
function wmGroundBaseMapLayers() {
  const ids = typeof SERVER_GROUND !== "undefined" && Array.isArray(SERVER_GROUND) ? SERVER_GROUND : [];
  return ids.map(id => ({
    id,
    layerType: "VectorTileLayer",
    title: id.split("/").pop(),
    styleUrl: `${location.origin}/rest/services/${id}/VectorTileServer/resources/styles/root.json`,
    visibility: true,
    opacity: 1,
  }));
}

function wmBaseMapJson(which) {
  if (which === "ground") {
    return { baseMapLayers: wmGroundBaseMapLayers(), title: "Map ground" };
  }

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
  if (layers.length === 0) return "none";
  return layers.some(l => l.layerType === "VectorTileLayer") ? "ground" : "osm";
}

/** Builds the ground layers named by a basemap once, from each service's own style. */
function wmDrawGround(baseMapLayers) {
  const drawn = wmGround.getLayers();
  drawn.clear();

  for (const entry of baseMapLayers.filter(l => l.layerType === "VectorTileLayer")) {
    const service = entry.styleUrl
      ? entry.styleUrl.slice(0, entry.styleUrl.indexOf("/VectorTileServer") + "/VectorTileServer".length)
      : `${location.origin}/rest/services/${entry.id}/VectorTileServer`;

    const tiles = new ol.layer.VectorTile({
      declutter: true,
      source: new ol.source.VectorTile({ format: new ol.format.MVT(), url: `${service}/tile/{z}/{y}/{x}.pbf`, maxZoom: 22 }),
      style: wmPlainStyle("#b8c2cc"),
    });

    wmFetch(entry.styleUrl || `${service}/resources/styles/root.json`)
      .then(style => { if (style && Array.isArray(style.layers)) tiles.setStyle(wmGlStyle(style)); })
      .catch(() => { /* a plain grey ground rather than none */ });

    drawn.push(tiles);
  }
}

/** Shows the basemap a document asks for: the ground, OpenStreetMap, or nothing. */
function wmShowBaseMap(doc) {
  const which = wmBaseMapOf(doc);
  wmBase.setVisible(which === "osm");
  wmGround.setVisible(which === "ground");
  if (which === "ground") wmDrawGround((doc.baseMap && doc.baseMap.baseMapLayers) || []);
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
    // ADR-123: an image service, drawn the way a map image is and asked for its pixel values.
    case "ArcGISImageServiceLayer": return layer.url ? "imagery" : null;
    case "VectorTileLayer": return (layer.styleUrl || layer.url) ? "tiles" : null;
    default: return null;
  }
}

const WM_KIND_LABEL = { feature: "Features", image: "Map image", imagery: "Imagery", tiles: "Vector tiles" };

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
/**
 * Draws vector tiles the way the service's own style says — the style ArcGIS Pro and the ArcGIS Maps SDK
 * read from `resources/styles/root.json`.
 *
 * <b>2026-09-30, from the design review.</b> A vector tile layer on a web map was drawn in one flat
 * palette colour: this viewer never read the style, so a layer classified orange and blue on its item
 * page was blue here, and the same service looked different in the two places a publisher checks it.
 *
 * <b>The part of the style language this server writes, and nothing more.</b> `VectorTileServerMetadataWriter`
 * emits fill, line and circle layers, each with a `source-layer`, legacy filters (`==`, `!=`, `in`,
 * `!in`, `all`, `any`, comparisons) and constant paint values; a zoom-dependent value is read as its
 * `stops`. Symbol layers — labels and icons — are not drawn here, and a filter this does not understand
 * lets the feature through rather than hiding it, so the failure is *drawn without its class* rather
 * than *missing*. A full interpreter is ADR territory (the design review proposed MapLibre); this is
 * the repair that makes today's viewer honest.
 */
function wmGlFilter(filter, feature) {
  if (!Array.isArray(filter) || filter.length === 0) return true;

  const [op, ...args] = filter;
  const value = key => key === "$type"
    ? ({ Point: "Point", MultiPoint: "Point", LineString: "LineString", MultiLineString: "LineString",
         Polygon: "Polygon", MultiPolygon: "Polygon" })[feature.getType ? feature.getType() : ""]
    : feature.get(key);

  switch (op) {
    case "all": return args.every(f => wmGlFilter(f, feature));
    case "any": return args.some(f => wmGlFilter(f, feature));
    case "none": return !args.some(f => wmGlFilter(f, feature));
    case "has": return value(args[0]) !== undefined;
    case "!has": return value(args[0]) === undefined;
    // eslint-disable-next-line eqeqeq
    case "==": return value(args[0]) == args[1];
    // eslint-disable-next-line eqeqeq
    case "!=": return value(args[0]) != args[1];
    case "in": { const v = value(args[0]); return args.slice(1).some(x => x == v); } // eslint-disable-line eqeqeq
    case "!in": { const v = value(args[0]); return !args.slice(1).some(x => x == v); } // eslint-disable-line eqeqeq
    case "<": return value(args[0]) < args[1];
    case "<=": return value(args[0]) <= args[1];
    case ">": return value(args[0]) > args[1];
    case ">=": return value(args[0]) >= args[1];
    default: return true;
  }
}

/** A paint value at a zoom: a constant, or the last `stops` entry at or below the zoom. */
function wmGlValue(value, zoom, fallback) {
  if (value === undefined || value === null) return fallback;
  if (typeof value !== "object" || Array.isArray(value)) {
    return Array.isArray(value) ? fallback : value;
  }
  const stops = Array.isArray(value.stops) ? value.stops : [];
  let chosen = stops.length ? stops[0][1] : fallback;
  for (const [z, v] of stops) if (zoom >= z) chosen = v;
  return chosen;
}

/** A colour with an opacity folded in. */
function wmGlColour(colour, opacity) {
  try {
    const [r, g, b, a] = ol.color.asArray(colour || "#000");
    return [r, g, b, (a ?? 1) * (Number.isFinite(opacity) ? opacity : 1)];
  } catch {
    return [0, 0, 0, Number.isFinite(opacity) ? opacity : 1];
  }
}

/** An OpenLayers style function for a style document's fill, line and circle layers. */
function wmGlStyle(style) {
  const layers = (style.layers || []).filter(l => ["fill", "line", "circle"].includes(l.type));
  const cache = new Map();

  return (feature, resolution) => {
    const zoom = Math.log2(156543.03392804097 / resolution);
    const sourceLayer = feature.get("layer");
    const out = [];

    for (const l of layers) {
      if (l["source-layer"] && l["source-layer"] !== sourceLayer) continue;
      if (Number.isFinite(l.minzoom) && zoom < l.minzoom) continue;
      if (Number.isFinite(l.maxzoom) && zoom >= l.maxzoom) continue;
      if (!wmGlFilter(l.filter, feature)) continue;

      const key = `${l.id}@${Math.floor(zoom)}`;
      let drawn = cache.get(key);

      if (!drawn) {
        const paint = l.paint || {};

        if (l.type === "fill") {
          const opacity = wmGlValue(paint["fill-opacity"], zoom, 1);
          const outline = wmGlValue(paint["fill-outline-color"], zoom, null);
          drawn = new ol.style.Style({
            fill: new ol.style.Fill({ color: wmGlColour(wmGlValue(paint["fill-color"], zoom, "#000"), opacity) }),
            stroke: outline ? new ol.style.Stroke({ color: wmGlColour(outline, opacity), width: 1 }) : undefined,
          });
        } else if (l.type === "line") {
          const width = wmGlValue(paint["line-width"], zoom, 1);
          const dash = wmGlValue(paint["line-dasharray"], zoom, null);
          drawn = new ol.style.Style({
            stroke: new ol.style.Stroke({
              color: wmGlColour(wmGlValue(paint["line-color"], zoom, "#000"), wmGlValue(paint["line-opacity"], zoom, 1)),
              width,
              lineDash: Array.isArray(dash) ? dash.map(d => d * width) : undefined,
            }),
          });
        } else {
          const opacity = wmGlValue(paint["circle-opacity"], zoom, 1);
          const edge = wmGlValue(paint["circle-stroke-width"], zoom, 0);
          drawn = new ol.style.Style({
            image: new ol.style.Circle({
              radius: wmGlValue(paint["circle-radius"], zoom, 5),
              fill: new ol.style.Fill({ color: wmGlColour(wmGlValue(paint["circle-color"], zoom, "#000"), opacity) }),
              stroke: edge > 0
                ? new ol.style.Stroke({ color: wmGlColour(wmGlValue(paint["circle-stroke-color"], zoom, "#fff"), opacity), width: edge })
                : undefined,
            }),
          });
        }

        cache.set(key, drawn);
      }

      out.push(drawn);
    }

    return out;
  };
}

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

  // ADR-121: a symbol whose size follows a number — each size made once and kept.
  const sizeInfo = (renderer.visualVariables || []).find(v => v && v.type === "sizeInfo" && v.field);
  if (renderer.type === "simple" && sizeInfo) {
    const sized = new Map();
    const lo = Number(sizeInfo.minDataValue);
    const hi = Number(sizeInfo.maxDataValue);
    return {
      style: feature => {
        const value = Number(feature.get(sizeInfo.field));
        if (!Number.isFinite(value)) return null;
        const t = hi > lo ? Math.max(0, Math.min(1, (value - lo) / (hi - lo))) : 0;
        const pt = Math.round(Number(sizeInfo.minSize) + t * (Number(sizeInfo.maxSize) - Number(sizeInfo.minSize)));
        if (!sized.has(pt)) {
          const symbol = { ...renderer.symbol, size: pt };
          if (Array.isArray(symbol.color)) symbol.color = [...symbol.color.slice(0, 3), 204];
          const style = wmSymbolStyle(symbol, colour) || plain;
          // The large draw first and the small over them, so no small value is hidden under a large one.
          if (style.setZIndex) style.setZIndex(Number(sizeInfo.maxSize) - pt);
          sized.set(pt, style);
        }
        return sized.get(pt);
      },
      fields: [sizeInfo.field],
      own: true,
    };
  }

  if (renderer.type === "simple") {
    return { style: wmSymbolStyle(renderer.symbol, colour) || plain, fields: [], own: true };
  }

  // A heat map is drawn as its own kind of layer (wmBuildLayer); here it only names the weight it reads.
  if (renderer.type === "heatmap") {
    return { style: plain, fields: renderer.field ? [renderer.field] : [], own: true, heat: renderer };
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

  if (renderer.type === "heatmap") return WM_HEAT_STOPS.slice(-3).map(([, c]) => `rgb(${c[0]}, ${c[1]}, ${c[2]})`);

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

  // <b>The map's own style first, then the layer's — ADR-104.</b> A layer styled in this map carries its renderer
  // in the Web Map document's `layerDefinition.drawingInfo`, where ArcGIS clients look for it too.
  const own = layer.layerDefinition && layer.layerDefinition.drawingInfo;
  const info = own && own.renderer ? { ...run.info, drawingInfo: own } : run.info;

  // A map image is drawn by the server in the service's own symbology, which this page does not read.
  // An image is shown by its own small picture in the list rather than by a colour (design review 2026-10-01).
  run.swatches = kind === "image" ? [] : kind === "imagery" ? null : kind === "feature" ? wmSwatches(info, colour) : [colour];
  if (kind === "imagery") run.thumb = `${layer.url}/exportImage?size=32,32&format=png&f=image`;

  if (kind === "feature") {
    const renderer = wmRendererStyle(info, colour);
    run.ownStyle = renderer.own;

    const idField = (run.info && run.info.objectIdField) || "objectid";
    const label = wmLabelOf(layer);
    const fields = [idField, ...renderer.fields, label && label.field].filter(Boolean);

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
          ...wmTimeParam(layer),
          f: "json",
        });

        wmFetch(url).then(payload => {
          const features = WM_ESRI.readFeatures(payload, { featureProjection: WM_MERCATOR });
          source.addFeatures(features);
          const truncated = !!payload.exceededTransferLimit;
          const wasError = !!run.error;
          run.error = null;
          // Redrawn when what the list says changes, and only then: a redraw takes the focus with it.
          const recount = truncated && run.shown !== features.length;
          run.shown = features.length;
          if (run.truncated !== truncated || wasError || recount) {
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

    const drawn = renderer.heat ? wmHeatLayer(source, renderer.heat)
      : new ol.layer.Vector({ source, style: renderer.style, declutter: false });

    // <b>The layer's visible range, as ArcGIS clients honour it — 2026-10-10.</b> Outside it a layer is not drawn,
    // and a layer that is not drawn asks for nothing. Without it, 1,270,971 Istanbul buildings opened with one
    // query for the whole city, answered with its first thousand rows.
    // OpenLayers leaves a layer out *at* its maxResolution and ArcGIS draws it at its minScale, so the bound is
    // widened by a hair: the wheel stops on whole zooms, and 1:72,224 is exactly zoom 13.
    const range = wmScaleRange(wmLayerRange(layer, run.info));
    if (range.maxResolution) drawn.setMaxResolution(range.maxResolution * WM_RANGE_SLACK);
    if (range.minResolution) drawn.setMinResolution(range.minResolution / WM_RANGE_SLACK);
    if (!label) return drawn;

    // ADR-117: the labels are a second layer over the same source, decluttered among themselves only — in one
    // layer a point's own symbol claimed the space and every label beside it was dropped (design review 2026-10-01).
    const group = new ol.layer.Group({
      layers: [drawn, new ol.layer.Vector({ source, style: wmLabelStyle(label), declutter: true, ...wmScaleRange(label) })],
    });
    group.getSource = () => source;
    return group;
  }

  if (kind === "image" || kind === "imagery") {
    // OpenLayers asks an ImageServer for `exportImage` and a MapServer for `export`; the image service is asked for a
    // PNG, so the transparency of its no-data shows the map beneath.
    const source = new ol.source.ImageArcGISRest({
      url: layer.url,
      ratio: 1,
      params: kind === "imagery"
        ? { FORMAT: "png", ...(layer.renderingRule ? { RENDERINGRULE: JSON.stringify(layer.renderingRule) } : {}) }
        : { TRANSPARENT: true },
    });

    source.on("imageloaderror", () => {
      run.error = "This layer's picture could not be drawn. Sign in again, or check that it is shared with you.";
      wmDrawLayerList();
    });
    source.on("imageloadend", () => {
      if (run.error) { run.error = null; wmDrawLayerList(); }
    });

    return new ol.layer.Image({ source });
  }

  if (kind === "tiles") {
    const drawn = new ol.layer.VectorTile({
      declutter: true,
      source: new ol.source.VectorTile({
        format: new ol.format.MVT(),
        url: `${wmTileService(layer)}/tile/{z}/{y}/{x}.pbf`,
        maxZoom: 22,
      }),
      // The palette colour until the service's own style has been read, and for good if it cannot be.
      style: wmPlainStyle(colour),
    });

    // <b>The style the layer names, or the service's default</b> — Web Map `styleUrl` first, since an
    // author may have pointed a layer at a named style (ADR-054).
    const styleUrl = layer.styleUrl || `${wmTileService(layer)}/resources/styles/root.json`;

    wmFetch(styleUrl)
      .then(style => {
        if (style && Array.isArray(style.layers)) {
          drawn.setStyle(wmGlStyle(style));
          run.ownStyle = true;
          // The legend swatches from the same style, so the line in the list matches the map.
          run.swatches = [...new Set(style.layers.map(l => (l.paint || {})["fill-color"]
            || (l.paint || {})["line-color"] || (l.paint || {})["circle-color"])
            .filter(c => typeof c === "string"))].slice(0, 4);
          wmDrawLayerList();
        }
      })
      .catch(() => { /* the palette colour stays, which is what this drew before */ });

    return drawn;
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
    // A layer with time, added to a map that had none, brings the time slider (ADR-132).
    if (run.info && run.info.timeInfo) wmDrawTime();
    // ADR-154: a classified image's legend is its classes, read once from the service (ux review 9).
    if (kind === "imagery" && run.info && run.info.hasRasterAttributeTable) {
      wmFetch(`${layer.url}/legend?f=json`).then(said => {
        run.classLegend = ((said.layers || [])[0] || {}).legend || [];
        wmDrawLayerList();
      }).catch(() => {});
    }
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
  if (run && run.ol) wmDropDrawn(run.ol);
  wmRuntime.delete(layer);
}

/**
 * Takes a drawn layer off the map and disposes it. <b>Disposed, not only removed</b>: a heat map draws with WebGL, and
 * one that was only removed went on answering its source's loads and threw while drawing (ADR-121).
 */
function wmDropDrawn(drawn) {
  wmMap.removeLayer(drawn);
  const parts = drawn instanceof ol.layer.Group ? drawn.getLayers().getArray().slice() : [drawn];
  for (const part of parts) {
    try { part.dispose(); } catch { /* already gone */ }
  }
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
      if (run.info && wmKind(layer) === "feature" && !wmInRange(layer, run.info)) {
        return { text: wmRangeNote(wmLayerRange(layer, run.info)), tone: "" };
      }
      if (run.truncated) {
        return {
          text: `Showing the first ${(run.shown || WM_DRAW_LIMIT).toLocaleString()} features in this view. Zoom in, or filter, for the rest.`,
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
  // An image's own picture, fetched with the reader's credential — an <img> carries only the cookie — once.
  if (run && run.thumbUrl) return `<img class="swatch" alt="" aria-hidden="true" src="${wmEscape(run.thumbUrl)}">`;
  if (run && run.thumb && !run.thumbAsked) {
    run.thumbAsked = true;
    const headers = wmToken ? { Authorization: "Bearer " + wmToken } : {};
    fetch(run.thumb, { headers })
      .then(r => r.ok ? r.blob() : null)
      .then(blob => { if (blob) { run.thumbUrl = URL.createObjectURL(blob); wmDrawLayerList(); } })
      .catch(() => { /* the slot stays plain */ });
  }
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
      ${readable && wmTimeNarrowed(layer) ? `<div class="lstate">Time: only features ${wmEscape(wmTimeBetween())}.</div>` : ""}
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
        ${readable && kind === "feature" ? `<button class="tiny" data-act="style" data-layer="${wmEscape(key)}"
          aria-label="Style of ${wmEscape(title)}" data-focus="style:${wmEscape(key)}" aria-expanded="${run.styleOpen ? "true" : "false"}"
          aria-controls="sty-${wmEscape(key)}">Style</button>
          <button class="tiny" data-act="popup" data-layer="${wmEscape(key)}"
          aria-label="Pop-up of ${wmEscape(title)}" data-focus="popup:${wmEscape(key)}" aria-expanded="${run.popupOpen ? "true" : "false"}"
          aria-controls="pop-${wmEscape(key)}">Pop-up</button>
          <button class="tiny" data-act="labels" data-layer="${wmEscape(key)}"
          aria-label="Labels of ${wmEscape(title)}" data-focus="labels:${wmEscape(key)}" aria-expanded="${run.labelOpen ? "true" : "false"}"
          aria-controls="lab-${wmEscape(key)}">Labels</button>
          ${wmEditable(layer, "Create") ? `<button class="tiny" data-act="addFeature" data-layer="${wmEscape(key)}"
          data-focus="addFeature:${wmEscape(key)}" aria-pressed="${wmAdding.layer === layer ? "true" : "false"}"
          aria-label="Add a feature to ${wmEscape(title)}">Add feature</button>` : ""}
          <button class="tiny" data-act="table" data-layer="${wmEscape(key)}" data-focus="table:${wmEscape(key)}"
          aria-pressed="${wmTable.layer === layer ? "true" : "false"}"
          aria-label="Attribute table of ${wmEscape(title)}">Table</button>` : ""}
        ${readable && kind === "imagery" && wmFunctionsOffered(run.info) ? `<div class="lrule">
          <label class="lkind" for="rule-${wmEscape(key)}">Shown as</label>
          <select id="rule-${wmEscape(key)}" data-act="renderingRule" data-layer="${wmEscape(key)}" data-focus="rule:${wmEscape(key)}"
            aria-label="Shown as, ${wmEscape(title)}">
            ${[["", `Service default${run.info.defaultRasterFunction ? ` (${wmFunctionLabel(run.info.defaultRasterFunction)})` : ""}`],
              ["None", "Raw values (no function)"], ...wmFunctionOptions(run.info)].map(([v, n]) =>
              `<option value="${v}"${((layer.renderingRule || {}).rasterFunction || "") === v ? " selected" : ""}>${wmEscape(n)}</option>`).join("")}
          </select>
          ${wmRuleArgsMarkup(layer, key, run.info)}
          ${wmFunctionKey(wmShownFunction(layer, run.info))}</div>` : ""}
        ${readable && kind === "imagery" && run.classLegend && run.classLegend.length && !layer.renderingRule ? `<ul class="lslegend">${
          run.classLegend.slice(0, 8).map(row => `<li><img class="swatch" alt="" src="data:${wmEscape(row.contentType || "image/png")};base64,${wmEscape(row.imageData || "")}">${wmEscape(row.label || "")}</li>`).join("")}
          ${run.classLegend.length > 8 ? `<li class="lkind">and ${run.classLegend.length - 8} more</li>` : ""}</ul>` : ""}
        ${readable && kind === "imagery" ? `<button class="tiny" data-act="pixels" data-layer="${wmEscape(key)}"
          data-focus="pixels:${wmEscape(key)}" aria-pressed="${layer.popupEnabled === false ? "false" : "true"}"
          title="Show pixel values when the map is clicked">Pixel values</button>` : ""}
        ${readable && wmExtentOf(layer) ? `<button class="tiny" data-act="zoom" data-layer="${wmEscape(key)}"
          aria-label="Zoom to ${wmEscape(title)}"
          data-focus="zoom:${wmEscape(key)}">Zoom to</button>` : ""}
        <button class="tiny danger" data-act="remove" data-layer="${wmEscape(key)}"
          data-focus="remove:${wmEscape(key)}" aria-label="Remove ${wmEscape(title)} from the map">Remove</button>
      </div>
      ${readable && kind === "feature" && run.styleOpen ? wmStyleMarkup(layer, run, key, title) : ""}
      ${readable && kind === "feature" && run.popupOpen ? wmPopupPanel(layer, run, key, title) : ""}
      ${readable && kind === "feature" && run.labelOpen ? wmLabelPanel(layer, run, key) : ""}
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

/**
 * A feature layer's style in this map — ADR-104, Portal's *Styles* in its simple form.
 *
 * <b>Which level it changes is said on it.</b> *Apply* styles the layer in this map only and is saved with the map;
 * *Save as the layer's default* changes it everywhere, and is offered only to a role that may publish — the server
 * decides whether this user owns the layer.
 */
function wmStyleMarkup(layer, run, key, title) {
  const k = wmEscape(key);
  const draft = { ...wmDraftFromRenderer(layer), ...(run.styleDraft || {}) };
  const how = draft.how;
  const fields = wmUserFields(run.info);
  const texts = fields.filter(f => WM_FILTER_TYPES[f.type] !== "date");
  const numbers = fields.filter(f => WM_FILTER_TYPES[f.type] === "number");
  const geometry = (run.info && run.info.geometryType) || "";
  const own = !!(layer.layerDefinition && layer.layerDefinition.drawingInfo);
  const option = (value, label, chosen) => `<option value="${wmEscape(value)}"${value === chosen ? " selected" : ""}>${wmEscape(label)}</option>`;

  return `<div class="lstyle" id="sty-${k}">
    <p class="lsnote">${own
      ? (wmState.dirty ? "Styled in this map — not saved yet. Save the map to keep it." : "Styled in this map.")
      : "Drawn in the layer's own style, as every map draws it."}</p>
    <label class="lkind" for="styHow-${k}">Style</label>
    <select id="styHow-${k}" data-style-how="${k}" data-focus="styHow:${k}">
      ${option("default", "The layer's own style", how)}
      ${option("single", "One colour", how)}
      ${texts.length ? option("unique", "A colour per value", how) : ""}
      ${numbers.length ? option("breaks", "Counts and amounts (colour)", how) : ""}
      ${numbers.length && /Point/.test(geometry) ? option("size", "Counts and amounts (size)", how) : ""}
      ${/Point/.test(geometry) ? option("heat", "Heat map", how) : ""}
    </select>
    ${how === "single" ? `<div class="row">
      <label class="lkind" for="styColour-${k}">Colour</label>
      <input type="color" id="styColour-${k}" value="${wmEscape(draft.colour || "#1f5fa8")}">
      <label class="lkind" for="stySize-${k}">${/Point/.test(geometry) ? "Size" : "Width"}</label>
      <input type="number" id="stySize-${k}" min="0.5" max="40" step="0.5" style="width:5em"
        value="${wmEscape(String(draft.size || (/Point/.test(geometry) ? 8 : 1.5)))}">
    </div>` : ""}
    ${how === "unique" ? `<div class="row">
      <label class="lkind" for="styField-${k}">Field</label>
      <select id="styField-${k}">${texts.map(f => option(f.name, f.alias || f.name, draft.field)).join("")}</select>
    </div>` : ""}
    ${how === "breaks" ? `<div class="row">
      <label class="lkind" for="styField-${k}">Field</label>
      <select id="styField-${k}">${numbers.map(f => option(f.name, f.alias || f.name, draft.field)).join("")}</select>
      <label class="lkind" for="styClasses-${k}">Classes</label>
      <input type="number" id="styClasses-${k}" min="2" max="9" step="1" style="width:4em" value="${wmEscape(String(draft.classes || 5))}">
    </div>
    <div class="row">
      <label class="lkind" for="styMethod-${k}">Method</label>
      <select id="styMethod-${k}">
        ${option("esriClassifyNaturalBreaks", "Natural breaks", draft.method)}
        ${option("esriClassifyEqualInterval", "Equal interval", draft.method)}
        ${option("esriClassifyQuantile", "Quantile", draft.method)}
      </select>
      <label class="lkind" for="styRamp-${k}">Colours</label>
      <select id="styRamp-${k}">
        ${Object.entries(WM_RAMPS).map(([name, ramp]) => option(name, ramp.label, draft.ramp)).join("")}
      </select>
    </div>` : ""}
    ${how === "size" ? `<div class="row">
      <label class="lkind" for="styField-${k}">Field</label>
      <select id="styField-${k}">${numbers.map(f => option(f.name, f.alias || f.name, draft.field)).join("")}</select>
      <label class="lkind" for="styColour-${k}">Colour</label>
      <input type="color" id="styColour-${k}" value="${wmEscape(draft.colour || "#1f5fa8")}">
    </div>
    <div class="row">
      <label class="lkind" for="styMin-${k}">Smallest (pt)</label>
      <input type="number" id="styMin-${k}" min="1" max="40" step="1" style="width:4em" value="${wmEscape(String(draft.minSize || 6))}">
      <label class="lkind" for="styMax-${k}">Largest (pt)</label>
      <input type="number" id="styMax-${k}" min="2" max="80" step="1" style="width:4em" value="${wmEscape(String(draft.maxSize || 30))}">
    </div>` : ""}
    ${how === "heat" ? `<div class="row">
      <label class="lkind" for="styRadius-${k}">Area of influence (px)</label>
      <input type="number" id="styRadius-${k}" min="2" max="60" step="1" style="width:4em" value="${wmEscape(String(draft.radius || 24))}">
      <label class="lkind" for="styWeight-${k}">Weight by</label>
      <select id="styWeight-${k}">${option("", "Every point the same", draft.field || "")}${numbers.map(f => option(f.name, f.alias || f.name, draft.field)).join("")}</select>
    </div>
    <p class="lsnote">Hot where points are dense. A heat map shows density, so you can't click it for one point.</p>` : ""}
    <div class="row">
      <button class="tiny" data-act="styleApply" data-layer="${k}" data-focus="styleApply:${k}">Apply to this map</button>
    </div>
    ${own ? wmStyleLegend(layer) : ""}
    ${wmMe.privileges.has("content:publishFeatures") && own && !["size", "heat"].includes(how)
      ? `<div class="lsdefault">
          <button class="tiny" data-act="styleDefault" data-layer="${k}" data-focus="styleDefault:${k}">Make default everywhere…</button>
          <p class="lkind">Changes ${wmEscape(title)} in every map that has not styled it itself.</p>
        </div>` : ""}
    ${run.styleError ? `<p class="lerr" role="alert">${wmEscape(run.styleError)}</p>` : ""}
  </div>`;
}

/**
 * What the panel shows first: the style the map already has, read back from its renderer — a map reopened with a
 * colour per value opens on *A colour per value* and its field, not on *One colour* in blue, where one press of
 * Apply would have replaced it (design review 2026-10-01).
 */
function wmDraftFromRenderer(layer) {
  const renderer = layer.layerDefinition && layer.layerDefinition.drawingInfo && layer.layerDefinition.drawingInfo.renderer;
  if (!renderer) return { how: "default" };

  const hex = colour => Array.isArray(colour)
    ? "#" + colour.slice(0, 3).map(n => Math.max(0, Math.min(255, Number(n) || 0)).toString(16).padStart(2, "0")).join("")
    : undefined;

  if (renderer.type === "heatmap") {
    return { how: "heat", radius: renderer.blurRadius, field: renderer.field || "" };
  }
  const sizeInfo = (renderer.visualVariables || []).find(v => v && v.type === "sizeInfo");
  if (renderer.type === "simple" && sizeInfo) {
    return { how: "size", field: sizeInfo.field, minSize: sizeInfo.minSize, maxSize: sizeInfo.maxSize,
      colour: hex((renderer.symbol || {}).color) };
  }
  if (renderer.type === "simple") {
    const symbol = renderer.symbol || {};
    return { how: "single", colour: hex(symbol.color), size: symbol.size || symbol.width || (symbol.outline || {}).width };
  }
  if (renderer.type === "uniqueValue") return { how: "unique", field: renderer.field1 };
  if (renderer.type === "classBreaks") {
    return { how: "breaks", field: renderer.field, classes: (renderer.classBreakInfos || []).length || 5 };
  }
  return { how: "default" };
}

/** What the colours mean: the values or class ranges of the map's own renderer, the first eight. */
function wmStyleLegend(layer) {
  const renderer = layer.layerDefinition && layer.layerDefinition.drawingInfo && layer.layerDefinition.drawingInfo.renderer;
  if (!renderer) return "";
  if (renderer.type === "heatmap") {
    return `<p class="lslegend heatlegend"><span class="heatramp" aria-hidden="true"></span> Sparse to dense${
      renderer.field ? `, weighted by ${wmEscape(renderer.field)} (low values fade)` : ""}</p>`;
  }
  const sizeInfo = (renderer.visualVariables || []).find(v => v && v.type === "sizeInfo");
  if (sizeInfo) {
    // Three circles, largest first, each labelled with the value it stands for — as ArcGIS shows it.
    const lo = Number(sizeInfo.minDataValue);
    const hi = Number(sizeInfo.maxDataValue);
    const a = Number(sizeInfo.minSize);
    const b = Number(sizeInfo.maxSize);
    const colour = wmSymbolColour(renderer.symbol) || "#1f5fa8";
    const steps = [[b, hi], [(a + b) / 2, (lo + hi) / 2], [a, lo]];
    return `<ul class="lslegend sizelegend"><li class="lkind">${wmEscape(sizeInfo.field)}</li>${steps.map(([pt, value]) => {
      const px = Math.round(wmPx(pt));
      return `<li><span class="sizedot" aria-hidden="true" style="width:${px}px;height:${px}px;background:${wmEscape(colour)}"></span>${
        wmEscape(Number(value).toLocaleString("en-US", { maximumFractionDigits: 2 }))}</li>`;
    }).join("")}</ul>`;
  }
  const rows = renderer.type === "uniqueValue" ? (renderer.uniqueValueInfos || [])
    : renderer.type === "classBreaks" ? (renderer.classBreakInfos || []) : [];
  if (!rows.length) return "";
  const shown = rows.slice(0, 8);
  return `<ul class="lslegend">${shown.map(row => `<li><span class="swatch" aria-hidden="true"
      style="background:${wmEscape(wmSymbolColour(row.symbol) || "transparent")}"></span>${wmEscape(String(row.label ?? row.value ?? ""))}</li>`).join("")}
    ${rows.length > shown.length ? `<li class="lkind">and ${rows.length - shown.length} more</li>` : ""}</ul>`;
}

/**
 * A layer's pop-up in this map — ADR-110, Portal's *Configure pop-ups* in its plain form: shown or not, a title with
 * `{field}` in it, and which fields appear under what label. Saved in the Web Map as `popupInfo`, where ArcGIS
 * clients read it.
 */
function wmPopupPanel(layer, run, key, title) {
  const k = wmEscape(key);
  const fields = wmUserFields(run.info);
  const popup = layer.popupInfo || null;
  const shown = new Map(((popup && popup.fieldInfos) || []).map(f => [f.fieldName, f]));
  const enabled = layer.popupEnabled !== false;

  const textMode = !!(popup && popup.description);

  return `<div class="lstyle ${textMode ? "popmode-text" : "popmode-fields"}" id="pop-${k}">
    <p class="lsnote">${popup ? "Customised for this map." : "Showing every field (the default)."}</p>
    <label class="check"><input type="checkbox" id="popOn-${k}"${enabled ? " checked" : ""}> Show a pop-up when a feature is clicked</label>
    <label class="lkind" for="popTitle-${k}">Title — a field in braces is its value, as {${wmEscape((fields[0] || {}).name || "name")}}</label>
    <input type="text" id="popTitle-${k}" value="${wmEscape((popup && popup.title) || "")}" placeholder="${wmEscape(title)}">
    <fieldset class="popcontent"><legend>Content</legend>
      <label class="check"><input type="radio" name="popMode-${k}" value="fields"${popup && popup.description ? "" : " checked"}> A list of fields</label>
      <label class="check"><input type="radio" name="popMode-${k}" value="text"${popup && popup.description ? " checked" : ""}> Text</label>
    </fieldset>
    <div class="poptextpart">
      <label class="lkind" for="popText-${k}">Text</label>
      <textarea id="popText-${k}" rows="4" data-pop-text="${k}"
        placeholder="${wmEscape(`{${(fields[0] || {}).name || "field"}}: {${(fields[1] || fields[0] || {}).name || "field"}}`)}&#10;Written here, with a field in braces filled in.">${wmEscape((popup && popup.description) || "")}</textarea>
      <p class="lkind popinsert">Insert a field: ${fields.map(f => `<button type="button" class="tiny" data-act="popInsert"
        data-layer="${k}" data-field="${wmEscape(f.name)}">{${wmEscape(f.name)}}</button>`).join(" ")}</p>
    </div>
    <label class="lkind" for="popImage-${k}">Image — a field holding its web address (https://…)</label>
    <select id="popImage-${k}">
      <option value="">No image</option>
      ${fields.filter(f => WM_FILTER_TYPES[f.type] === "text").map(f => `<option value="${wmEscape(f.name)}"${wmPopupImageField(popup) === f.name ? " selected" : ""}>${wmEscape(f.alias || f.name)}</option>`).join("")}
    </select>
    <p class="lkind">Shown only for features whose value starts with http:// or https://.</p>
    <table class="popfields"><thead><tr><th class="popshow">Show</th><th>Field</th>
      <th><span class="popfieldshead">Label and format</span><span class="poptexthead">Format of {field} in the text</span></th></tr></thead><tbody>
      ${fields.map(f => {
        const at = shown.get(f.name);
        const visible = popup ? !!(at && at.visible !== false) : true;
        return `<tr><td><input type="checkbox" data-pop-field="${wmEscape(f.name)}"${visible ? " checked" : ""}
            aria-label="Show ${wmEscape(f.name)}"></td><td>${wmEscape(f.name)}</td>
          <td><input type="text" data-pop-label="${wmEscape(f.name)}" value="${wmEscape((at && at.label) || f.alias || f.name)}"
            aria-label="Label of ${wmEscape(f.name)}">${(() => {
              const control = wmFormatControl(f, at && at.format);
              return control ? `<div class="popfmt">${control}</div>` : "";
            })()}</td></tr>`;
      }).join("")}
    </tbody></table>
    <div class="row">
      <button class="tiny" data-act="popupApply" data-layer="${k}" data-focus="popupApply:${k}">Apply to this map</button>
      ${popup ? `<button class="tiny" data-act="popupReset" data-layer="${k}" data-focus="popupReset:${k}">Every field again</button>` : ""}
    </div>
    <p class="lsnote" id="popSays-${k}" role="status" aria-live="polite">${wmEscape(run.popupSaid || "")}</p>
  </div>`;
}

/** Reads a layer's Pop-up panel into the Web Map's `popupInfo` and `popupEnabled`. */
function wmApplyPopup(layer, reset = false) {
  const k = layer.id;
  const panel = wm$(`pop-${k}`);
  if (!panel) return;

  layer.popupEnabled = wm$(`popOn-${k}`).checked;
  if (layer.popupEnabled) delete layer.popupEnabled;

  const run = wmRuntime.get(layer);
  const known = new Set(wmUserFields(run && run.info).map(f => f.name));
  const textMode = (panel.querySelector(`input[name="popMode-${k}"]:checked`) || {}).value === "text";
  const text = textMode ? (wm$(`popText-${k}`).value || "").trim() : "";
  const unknown = [...(`${wm$(`popTitle-${k}`).value} ${text}`.matchAll(/\{([^}]+)\}/g))]
    .map(m => m[1].trim()).filter(n => !known.has(n));

  if (!reset && unknown.length) {
    if (run) run.popupSaid = `${unknown.map(n => `{${n}}`).join(", ")} ${unknown.length === 1 ? "is not a field" : "are not fields"} of this layer; nothing was applied.`;
    wmDrawLayerList();
    wmSayIn("layersStatus", run ? run.popupSaid : "", true);
    return;
  }

  if (reset) {
    delete layer.popupInfo;
  } else {
    layer.popupInfo = {
      title: wm$(`popTitle-${k}`).value.trim() || undefined,
      // A pop-up that does not say so hides a feature's attachments in ArcGIS clients (Field Maps among them); the
      // map's pop-up chooses fields, not whether photos stay visible (ArcGIS review, 2026-10-01).
      showAttachments: true,
      // ADR-122: text in place of the field list, and an image from a field holding its address.
      ...(text ? { description: text } : {}),
      ...(wm$(`popImage-${k}`).value
        ? { mediaInfos: [{ type: "image", title: "", caption: "", value: { sourceURL: `{${wm$(`popImage-${k}`).value}}` } }] }
        : {}),
      fieldInfos: [...panel.querySelectorAll("[data-pop-field]")].map(box => {
        const name = box.dataset.popField;
        const format = wmReadFormat(panel, name);
        return {
          fieldName: name,
          label: (panel.querySelector(`[data-pop-label="${CSS.escape(name)}"]`) || {}).value || name,
          visible: box.checked,
          ...(format ? { format } : {}),
        };
      }),
    };
  }

  const said = reset
    ? `${layer.title} shows every field again. Save the map to keep it.`
    : `${layer.title}'s pop-up is set in this map. Save the map to keep it.`;
  if (run) run.popupSaid = said;
  wmMarkDirty();
  wmDrawLayerList();
  wmSayIn("layersStatus", said);
}

/** The colour ramps the classes panel offers, light to strong. */
const WM_RAMPS = {
  reds: { label: "Yellow to red", from: [255, 255, 178, 255], to: [189, 0, 38, 255] },
  blues: { label: "Light to dark blue", from: [222, 235, 247, 255], to: [8, 81, 156, 255] },
  greens: { label: "Light to dark green", from: [229, 245, 224, 255], to: [0, 109, 44, 255] },
  purples: { label: "Light to dark purple", from: [239, 237, 245, 255], to: [84, 39, 143, 255] },
};

/** One symbol of a colour, in the shape the layer's geometry takes. */
function wmSimpleSymbol(geometry, hex, size) {
  const rgb = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
  if (/Point/.test(geometry)) {
    return { type: "esriSMS", style: "esriSMSCircle", color: [...rgb, 255], size,
      outline: { type: "esriSLS", style: "esriSLSSolid", color: [255, 255, 255, 255], width: 0.75 } };
  }
  if (/Polyline/.test(geometry)) {
    return { type: "esriSLS", style: "esriSLSSolid", color: [...rgb, 255], width: size };
  }
  return { type: "esriSFS", style: "esriSFSSolid", color: [...rgb, 150],
    outline: { type: "esriSLS", style: "esriSLSSolid", color: [...rgb, 255], width: size } };
}

/** Draws a layer again after its style changed: the source's fields may have changed with it. */
function wmRestyle(layer) {
  const run = wmRuntime.get(layer);
  if (!run) return;
  // Only the drawn layer goes: `wmRemoveFromMap` also forgets the layer's state, which this keeps.
  if (run.ol) wmDropDrawn(run.ol);
  const index = wmLayers().indexOf(layer);
  run.ol = wmBuildLayer(layer, run, Math.max(index, 0));
  if (run.ol) {
    wmMap.addLayer(run.ol);
    wmApply(layer, index);
  }
}

/** Reads the panel, builds the renderer — here or with the layer's `generateRenderer` — and puts it on the map. */
async function wmApplyStyle(layer) {
  const run = wmRuntime.get(layer);
  const k = layer.id;
  const how = (wm$(`styHow-${k}`) || {}).value || "default";
  const geometry = (run.info && run.info.geometryType) || "";
  run.styleError = null;

  if (!layer.layerDefinition || typeof layer.layerDefinition !== "object") layer.layerDefinition = {};

  try {
    if (how === "default") {
      delete layer.layerDefinition.drawingInfo;
    } else if (how === "single") {
      const colour = wm$(`styColour-${k}`).value;
      const size = Number(wm$(`stySize-${k}`).value) || 1;
      run.styleDraft = { ...(run.styleDraft || {}), how, colour, size };
      layer.layerDefinition.drawingInfo = { renderer: { type: "simple", symbol: wmSimpleSymbol(geometry, colour, size) } };
    } else if (how === "heat") {
      // ADR-121: ArcGIS's heatmap renderer, as Map Viewer writes one.
      const radius = Math.max(2, Math.min(60, Number(wm$(`styRadius-${k}`).value) || 24));
      const field = wm$(`styWeight-${k}`).value;
      run.styleDraft = { ...(run.styleDraft || {}), how, radius, field };
      layer.layerDefinition.drawingInfo = { renderer: {
        type: "heatmap", blurRadius: radius, ...(field ? { field } : {}), maxPixelIntensity: 100, minPixelIntensity: 0,
        colorStops: WM_HEAT_STOPS.map(([ratio, color]) => ({ ratio, color })),
      } };
    } else if (how === "size") {
      // ADR-121: one symbol whose size follows a number, the range read from the layer's own statistics.
      const field = wm$(`styField-${k}`).value;
      const colour = wm$(`styColour-${k}`).value;
      const minSize = Math.max(1, Number(wm$(`styMin-${k}`).value) || 6);
      const maxSize = Math.max(minSize + 1, Number(wm$(`styMax-${k}`).value) || 30);
      const stats = await wmFetch(`${layer.url}/query?` + wmParams({
        where: "1=1", returnGeometry: false, f: "json",
        outStatistics: JSON.stringify([
          { statisticType: "min", onStatisticField: field, outStatisticFieldName: "lo" },
          { statisticType: "max", onStatisticField: field, outStatisticFieldName: "hi" }]),
      }));
      const row = (((stats || {}).features || [])[0] || {}).attributes || {};
      const lo = Number(row.lo ?? row.LO);
      const hi = Number(row.hi ?? row.HI);
      if (!Number.isFinite(lo) || !Number.isFinite(hi)) throw new Error(`the layer has no values of ${field} to size by`);
      run.styleDraft = { ...(run.styleDraft || {}), how, field, colour, minSize, maxSize };
      layer.layerDefinition.drawingInfo = { renderer: {
        type: "simple", symbol: wmSimpleSymbol(geometry, colour, minSize),
        visualVariables: [{ type: "sizeInfo", field, minDataValue: lo, maxDataValue: hi, minSize, maxSize }],
      } };
    } else {
      const field = wm$(`styField-${k}`).value;
      const base = wmSimpleSymbol(geometry, "#888888", /Point/.test(geometry) ? 8 : 1);
      let definition;

      if (how === "unique") {
        run.styleDraft = { ...(run.styleDraft || {}), how, field };
        definition = { type: "uniqueValueDef", uniqueValueFields: [field], baseSymbol: base };
      } else {
        const classes = Math.min(9, Math.max(2, Number(wm$(`styClasses-${k}`).value) || 5));
        const method = wm$(`styMethod-${k}`).value;
        const rampName = wm$(`styRamp-${k}`).value;
        const ramp = WM_RAMPS[rampName] || WM_RAMPS.reds;
        run.styleDraft = { ...(run.styleDraft || {}), how, field, classes, method, ramp: rampName };
        definition = {
          type: "classBreaksDef", classificationField: field, classificationMethod: method, breakCount: classes,
          baseSymbol: base,
          colorRamp: { type: "algorithmic", fromColor: ramp.from, toColor: ramp.to, algorithm: "esriCIELabAlgorithm" },
        };
      }

      const renderer = await wmFetch(`${layer.url}/generateRenderer?` + wmParams({
        classificationDef: JSON.stringify(definition), f: "json" }));
      layer.layerDefinition.drawingInfo = { renderer };
    }
  } catch (e) {
    run.styleError = `Not applied: ${e.message || e}`;
    wmDrawLayerList();
    wmSayIn("layersStatus", `The style of ${layer.title} was not applied.`, true);
    return;
  }

  wmRestyle(layer);
  wmMarkDirty();
  wmDrawLayerList();
  wmSayIn("layersStatus", how === "default"
    ? `${layer.title} is drawn in its own style again.`
    : `${layer.title} is styled in this map. Save the map to keep it.`);
}

/** Sends this map's style of a layer to the layer as its default, then lets the map follow it. */
async function wmSaveStyleAsDefault(layer) {
  const run = wmRuntime.get(layer);
  const drawing = layer.layerDefinition && layer.layerDefinition.drawingInfo;
  if (!run || !drawing) return;

  const name = (run.info && run.info.name) || layer.title;
  if (!confirm(`Make this the default style of '${name}'? Every map and client that has not styled it itself will draw it this way.`)) return;

  try {
    await wmFetch(`/admin/layers/${encodeURIComponent(name)}/symbology`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(drawing),
    });
  } catch (e) {
    run.styleError = `Not saved as the default: ${e.message || e}`;
    wmDrawLayerList();
    wmSayIn("layersStatus", `${name}'s default style was not changed.`, true);
    return;
  }

  // The default now is what this map drew; the map's copy is dropped so it follows the layer from here on.
  delete layer.layerDefinition.drawingInfo;
  run.info = await wmFetch(`${wmProbeUrl(layer)}?f=json`).catch(() => run.info);
  run.styleError = null;
  run.styleDraft = { how: "default" };
  wmRestyle(layer);
  wmMarkDirty();
  wmDrawLayerList();
  wmSayIn("layersStatus", `${name}'s default style is now this one.`);
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

  // The table shows the filtered layer too, from its first page (design review 2026-10-01).
  if (wmTable.layer === layer && !wmTableChanges().length) {
    wmTable.page = 0;
    wmLoadTable();
  }

  wmMarkDirty();
  wmDrawLayerList();
  wmSayIn("layersStatus", clause ? `${layer.title} is filtered.` : `${layer.title} shows every feature.`);
}

wm$("layerList").addEventListener("change", event => {
  const t = event.target;

  // ADR-136: an imagery layer shown through a raster function — the Web Map's own `renderingRule`.
  // ADR-158: a band function's argument changed — the rule rebuilt and the layer drawn again.
  if (t.dataset && t.dataset.act === "ruleArg") {
    const layer = wmLayerById(t.dataset.layer);
    if (!layer || !layer.renderingRule) return;
    const args = { ...(layer.renderingRule.rasterFunctionArguments || {}) };
    if (layer.renderingRule.rasterFunction === "ExtractBand") {
      const ids = [...(args.BandIDs || [0, 1, 2])];
      ids[{ R: 0, G: 1, B: 2 }[t.dataset.arg]] = Number(t.value);
      args.BandIDs = ids;
    } else if (t.dataset.arg === "BandIndexes") {
      args.BandIndexes = t.value.trim();
    } else {
      args[t.dataset.arg] = Number(t.value);
    }
    if (args.VisibleBandID != null && args.VisibleBandID === args.InfraredBandID) {
      // The select put back to the band still drawn, so what is shown is what the map uses.
      t.value = String(layer.renderingRule.rasterFunctionArguments[t.dataset.arg]);
      wmSayIn("layersStatus", "Not applied: choose two different bands for red and infrared.", true);
      return;
    }
    wmApplyRuleArgs(layer, args);
    return;
  }

  if (t.dataset && t.dataset.act === "renderingRule") {
    const layer = wmLayerById(t.dataset.layer);
    const run = layer && wmRuntime.get(layer);
    if (!layer) return;
    // ADR-158: a band function starts with its arguments — false colour, NDVI's red and near infrared, an expression.
    const defaults = wmRuleDefaults(t.value, ((run && run.info) || {}).bandCount || 1);
    if (t.value) layer.renderingRule = defaults ? { rasterFunction: t.value, rasterFunctionArguments: defaults } : { rasterFunction: t.value };
    else delete layer.renderingRule;
    const source = run && run.ol && run.ol.getSource && run.ol.getSource();
    if (source && source.updateParams) source.updateParams({ RENDERINGRULE: layer.renderingRule ? JSON.stringify(layer.renderingRule) : undefined });
    wmMarkDirty();
    wmDrawLayerList();
    if (run) { run.ruleError = null; run.ruleDraft = undefined; }
    wmSayIn("layersStatus", t.value === "None" ? `${layer.title} is shown as its raw values.`
      : t.value ? `${layer.title} is shown as ${t.value === "NDVI" ? "NDVI" : wmFunctionLabel(t.value).toLowerCase()}.`
      : `${layer.title} is back to the service default.`);
    return;
  }

  if (t.dataset && t.dataset.styleHow) {
    const layer = wmLayerById(t.dataset.styleHow);
    const run = layer && wmRuntime.get(layer);
    if (run) {
      run.styleDraft = { ...(run.styleDraft || {}), how: t.value };
      wmDrawLayerList();
    }
    return;
  }

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

  // ADR-117: a label setting takes effect as it is changed, as ArcGIS Map Viewer's do (design review 2026-10-01).
  if (t.dataset.act === "labelsApply") wmApplyLabels(layer);
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
    case "style": {
      const run = wmRuntime.get(layer);
      if (run) {
        const opening = !run.styleOpen;
        // One panel at a time: two open ones push the list past the window.
        for (const other of wmLayers()) { const r = wmRuntime.get(other); if (r) { r.styleOpen = false; r.popupOpen = false; r.labelOpen = false; } }
        run.styleOpen = opening;
        run.styleError = null;
      }
      wmDrawLayerList();
      break;
    }
    case "styleApply": wmApplyStyle(layer); break;
    case "table":
      if (!wmMayLeaveTable()) break;
      wmTable.layer === layer ? wmCloseTable() : wmOpenTable(layer);
      wmDrawLayerList();
      break;
    case "addFeature": wmAdding.layer === layer ? wmStopAdding() : wmStartAdding(layer); wmDrawLayerList(); break;
    // ADR-123: whether a click on this image answers its pixel — `popupEnabled`, as ArcGIS saves it.
    case "pixels":
      layer.popupEnabled = layer.popupEnabled === false;
      wmMarkDirty();
      wmDrawLayerList();
      break;
    case "popup": {
      const run = wmRuntime.get(layer);
      if (run) {
        const opening = !run.popupOpen;
        for (const other of wmLayers()) { const r = wmRuntime.get(other); if (r) { r.popupOpen = false; r.styleOpen = false; r.labelOpen = false; } }
        run.popupOpen = opening;
      }
      wmDrawLayerList();
      break;
    }
    case "labels": {
      const run = wmRuntime.get(layer);
      if (run) {
        const opening = !run.labelOpen;
        for (const other of wmLayers()) { const r = wmRuntime.get(other); if (r) { r.popupOpen = false; r.styleOpen = false; r.labelOpen = false; } }
        run.labelOpen = opening;
      }
      wmDrawLayerList();
      break;
    }
    case "labelsReset": wmApplyLabels(layer, true); break;
    case "popInsert": {
      // Puts `{field}` where the cursor is in the text, and goes back to it.
      const area = wm$(`popText-${layer.id}`);
      const button = event.target.closest("[data-field]");
      if (!area || !button) break;
      const token = `{${button.dataset.field}}`;
      const at = area.selectionStart ?? area.value.length;
      const end = area.selectionEnd ?? at;
      area.value = area.value.slice(0, at) + token + area.value.slice(end);
      area.focus();
      area.setSelectionRange(at + token.length, at + token.length);
      break;
    }
    case "popupApply": wmApplyPopup(layer); break;
    case "popupReset": wmApplyPopup(layer, true); break;
    case "styleDefault": wmSaveStyleAsDefault(layer); break;
    case "remove": {
      const layers = wmLayers();
      const at = layers.indexOf(layer);
      wmRemoveFromMap(layer);
      layers.splice(at, 1);
      wmApplyAll();
      wmMarkDirty();
      if (wmTable.layer === layer) wmCloseTable();
      wmDrawTime();
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
      if (["FeatureServer", "MapServer", "VectorTileServer", "ImageServer"].includes(service.type)) {
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

const WM_TYPE_LABEL = { FeatureServer: "Features", MapServer: "Map image", VectorTileServer: "Vector tiles", ImageServer: "Imagery" };

/** The kinds a service is added as, the one a person almost always wants first. */
const WM_TYPE_ORDER = ["FeatureServer", "ImageServer", "VectorTileServer", "MapServer"];

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
  } else if (type === "ImageServer") {
    await wmFetch(`${url}?f=json`);
    entries.push({
      id: wmNewId(), layerType: "ArcGISImageServiceLayer", url, title: name.split("/").pop(),
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

/**
 * One feature's pop-up: the map's own for the layer when it has one — ADR-110, the Web Map's `popupInfo`, a title
 * written with `{field}` and the fields shown, in order, under their labels — otherwise every attribute, as before.
 */
function wmPopupMarkup(layer, attributes, info) {
  const popup = layer.popupInfo;

  if (!popup || !Array.isArray(popup.fieldInfos)) {
    return `<table class="feature">${wmAttributeRows(attributes, info)
      || `<tr><td>No attributes besides its identifiers.</td></tr>`}</table>`;
  }

  // ADR-118: a field's value in the format the map's pop-up gives it.
  const formats = new Map(popup.fieldInfos.filter(f => f && f.format).map(f => [f.fieldName, f.format]));
  const types = new Map(((info && info.fields) || []).map(f => [f.name, f.type]));
  const value = name => {
    const v = (attributes || {})[name];
    return v === null || v === undefined ? "—" : wmFormatValue(v, formats.get(name), types.get(name));
  };
  const title = popup.title ? String(popup.title).replace(/\{([^}]+)\}/g, (_, name) => value(name.trim())) : "";
  const rows = popup.fieldInfos.filter(f => f && f.visible !== false && f.fieldName)
    .map(f => `<tr><th scope="row">${wmEscape(f.label || f.fieldName)}</th><td>${wmEscape(value(f.fieldName))}</td></tr>`).join("");

  // ADR-122: text with its fields filled in — drawn as text, whatever markup it holds — and an image from a field.
  const body = popup.description
    ? `<p class="ptext">${wmEscape(String(popup.description).replace(/\{([^}]+)\}/g, (_, name) => value(name.trim())))
        .replace(/\n/g, "<br>")}</p>`
    : `<table class="feature">${rows || `<tr><td>This map's pop-up shows no fields for this layer.</td></tr>`}</table>`;
  const imageField = wmPopupImageField(popup);
  const source = imageField ? String((attributes || {})[imageField] ?? "") : "";
  const image = /^https?:\/\//i.test(source)
    ? `<img class="pimage" src="${wmEscape(source)}" alt="${wmEscape(title || "Image")}" loading="lazy" referrerpolicy="no-referrer">`
    : "";

  return `${title ? `<h4 class="ptitle">${wmEscape(title)}</h4>` : ""}${body}${image}`;
}

function wmAttributeRows(attributes, info) {
  const aliases = new Map(((info && info.fields) || []).map(f => [f.name, f.alias || f.name]));
  const types = new Map(((info && info.fields) || []).map(f => [f.name, f.type]));
  return Object.keys(attributes || {}).filter(key => !wmSystemField(key, info)).map(key => `<tr><th scope="row">${wmEscape(aliases.get(key) || key)}</th>
    <td>${wmEscape(attributes[key] === null ? "—" : wmFormatValue(attributes[key], null, types.get(key)))}</td></tr>`).join("");
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
  wmLastClick = coordinate;
  wmIdentified.clear();
  const card = wm$("identify");
  const turn = ++wmIdentifyTurn;
  // A layer whose pop-up the map switched off is not asked (ADR-110, `popupEnabled`).
  const isHeat = layer => ((((layer.layerDefinition || {}).drawingInfo || {}).renderer) || {}).type === "heatmap";
  const candidates = wmQueryable({ visibleOnly: true }).filter(layer => layer.popupEnabled !== false);
  // ADR-121: a heat map shows density, so it is not asked for one feature.
  const layers = candidates.filter(layer => !isHeat(layer));
  // ADR-123: an imagery layer switched on answers what its pixel is there.
  const pixels = wmLayers().filter(layer => wmKind(layer) === "imagery" && layer.visibility !== false
    && layer.popupEnabled !== false && (wmRuntime.get(layer) || {}).status === "ok");
  if (layers.length === 0 && pixels.length === 0 && candidates.length > 0) {
    const heat = candidates[0];
    wmSay(`${heat.title} is a heat map, which shows density, not single features. To click one, set its Style to One colour.`);
    return;
  }

  wmHighlight.getSource().clear();
  card.hidden = true;

  if (layers.length === 0 && pixels.length === 0) {
    wmSay("No feature or imagery layer is switched on, so a click has nothing to ask. Map image and tile layers are drawn, not identified.");
    return;
  }

  const tolerance = wmMap.getView().getResolution() * 6;
  const box = [coordinate[0] - tolerance, coordinate[1] - tolerance, coordinate[0] + tolerance, coordinate[1] + tolerance];

  wmSay(`Asking ${layers.length + pixels.length} layer${layers.length + pixels.length === 1 ? "" : "s"} what is here…`);

  const pixelAnswers = Promise.all(pixels.map(async layer => {
    try {
      const said = await wmFetch(`${layer.url}/identify?` + wmParams({
        geometry: JSON.stringify({ x: coordinate[0], y: coordinate[1], spatialReference: { wkid: 102100, latestWkid: 3857 } }),
        geometryType: "esriGeometryPoint",
        ...(layer.renderingRule ? { renderingRule: JSON.stringify(layer.renderingRule) } : {}),
        f: "json",
      }));
      // ADR-154: a classified pixel's class name comes beside its value.
      return { layer, value: said && said.value, fn: said && said.rasterFunction, className: said?.attributes?.ClassName };
    } catch (e) {
      return { layer, error: e.message || String(e) };
    }
  }));

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
        ...wmTimeParam(layer),
        f: "json",
      }));
      return { layer, payload };
    } catch (e) {
      return { layer, error: e.message || String(e) };
    }
  }));

  const pixelFound = await pixelAnswers;
  if (turn !== wmIdentifyTurn) return;

  let count = 0;
  let valued = 0;
  let failed = 0;
  const pixelSections = pixelFound.map(({ layer, value, error, fn, className }) => {
    if (error) {
      failed++;
      return { layer, html: `<h3>${wmEscape(layer.title)}</h3><p>Could not be asked: ${wmEscape(error)}</p>` };
    }
    if (value === null || value === undefined || value === "" || value === "NoData") return { layer, html: "" };
    valued++;
    return { layer, html: `<h3>${wmEscape(layer.title)}</h3>${wmPixelMarkup(layer, value, fn, className)}` };
  });
  const featureSections = answers.map(({ layer, payload, error }) => {
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
      + features.map(f => {
        const key = wmRemember(layer, f);
        return `<div class="feat" data-feat="${wmEscape(key)}">${wmPopupMarkup(layer, f.attributes, info)}${wmEditable(layer)
          ? `<div class="row featacts"><button type="button" class="tiny" data-edit="${wmEscape(key)}"
              aria-label="Edit ${wmEscape(wmFeatureName(wmIdentified.get(key)))}">Edit</button>${wmEditable(layer, "Delete")
              ? `<button type="button" class="tiny danger" data-delete="${wmEscape(key)}"
              aria-label="Delete ${wmEscape(wmFeatureName(wmIdentified.get(key)))}">Delete</button>` : ""}</div>` : ""}</div>`;
      }).join("");
  }).map((html, i) => ({ layer: answers[i].layer, html }));

  // <b>In the layer list's order — the top of the map first</b>, so an image drawn under a feature layer answers
  // after it, and of two images the one on top first (design review 2026-10-01).
  const order = wmLayers();
  const sections = [...pixelSections, ...featureSections]
    .sort((a, b) => order.indexOf(b.layer) - order.indexOf(a.layer))
    .map(one => one.html).join("");

  const asked = layers.length + pixels.length;
  if (!sections) {
    wmSay(asked === 1 && pixels.length === 1
      ? `${pixels[0].title} has no pixel here.`
      : `Nothing here on the ${asked} layer${asked === 1 ? "" : "s"} switched on${
        layers.some(wmTimeNarrowed) ? ` ${wmTimeBetween()}` : ""}.`);
    return;
  }

  // Where the click was asked about, which a pixel has no outline to show.
  if (valued) wmHighlight.getSource().addFeature(new ol.Feature(new ol.geom.Point(coordinate)));

  card.innerHTML = `<div class="top"><b>What is here</b>
      <button class="tiny" data-close aria-label="Close">&times;</button></div>${sections}`;
  card.hidden = false;

  const found = [count ? `${count} feature${count === 1 ? "" : "s"}` : "", valued ? `${valued} pixel value${valued === 1 ? "" : "s"}` : ""]
    .filter(Boolean).join(" and ");
  wmSay(found
    ? `${found} here${failed ? `; ${failed} layer${failed === 1 ? "" : "s"} could not be asked` : ""}.`
    : `${failed} layer${failed === 1 ? "" : "s"} could not be asked.`);
}

/**
 * A pixel's value as a reader reads it — ADR-123: one row a band, named red, green and blue for a colour image,
 * and each a number to the precision its type holds rather than the seventeen digits a double prints.
 */
function wmPixelMarkup(layer, value, fn = null, className = null) {
  const parts = String(value).trim().split(/\s+/);
  const info = (wmRuntime.get(layer) || {}).info || {};
  const colour = parts.length >= 3;
  const names = parts.map((_, i) => colour && i < 3 ? ["Red", "Green", "Blue"][i]
    : String((info.bandNames || [])[i] || `Band ${i + 1}`).replace(/_/g, " "));
  const shown = text => {
    const number = Number(text);
    return Number.isFinite(number) ? number.toLocaleString(undefined, { maximumSignificantDigits: 7 }) : text;
  };
  if (className && parts.length === 1 && !fn) {
    return `<table class="feature pixel"><tr><th scope="row">Class</th><td>${wmEscape(className)} (value ${wmEscape(shown(parts[0]))})</td></tr></table>`;
  }
  return `<table class="feature pixel">${parts.map((part, i) => `<tr><th scope="row">${
    wmEscape(parts.length === 1 ? (fn ? wmFunctionLabel(fn) : "Pixel value") : names[i])}</th><td>${
    wmEscape(fn ? wmFunctionValue(fn, part) : shown(part))}</td></tr>`).join("")}</table>`;
}

wm$("identify").addEventListener("click", event => {
  if (event.target.closest("[data-close]")) wmCloseIdentify();
});

function wmCloseIdentify() {
  if (wmEditing.key) {
    if (!wmMayLeaveEdit()) return;
    const held = wmIdentified.get(wmEditing.key) || {};
    wmEditing.key = null;
    wmEditing.dirty = false;
    if (held.isNew) {
      wmSketch.getSource().clear();
      wmSay("Nothing was added.");
    }
  }
  wm$("identify").hidden = true;
  wmHighlight.getSource().clear();
  wm$("map").focus();
}

document.addEventListener("keydown", event => {
  if (event.key === "Escape" && wmAdding.draw) {
    const layer = wmAdding.layer;
    wmStopAdding();
    wmSketch.getSource().clear();
    wmDrawLayerList();
    wmSay("Adding stopped; nothing was added.");
    if (layer) document.querySelector(`[data-act="addFeature"][data-layer="${CSS.escape(layer.id)}"]`)?.focus();
    return;
  }
  if (event.key !== "Escape" || wm$("identify").hidden) return;
  // In the form, Escape goes back to the card — asking first when something has been typed (ADR-134).
  if (wmEditing.key) {
    if (wmMayLeaveEdit()) wmCancelEdit();
    return;
  }
  wmCloseIdentify();
});

wmMap.on("singleclick", event => {
  if (wmMeasuring || wmAdding.draw) return;
  // The click that placed a new point arrives here too, a moment after the form it opened (design review 2026-10-01).
  if (wmEditing.key && (wmIdentified.get(wmEditing.key) || {}).isNew) return;
  if (!wmMayLeaveEdit()) return;
  wmEditing.key = null;
  wmEditing.dirty = false;
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

const WM_TABS = ["layers", "search", "measure", "bookmarks", "print", "map"];

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
  if (name === "print" && !wm$("printTitle").value) wm$("printTitle").value = wmState.meta.title || "";
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
  const groundOption = wm$("mapBasemap").querySelector('option[value="ground"]');
  if (groundOption) {
    groundOption.hidden = wmGroundBaseMapLayers().length === 0 && wmBaseMapOf(wmState.doc) !== "ground";
  }
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
  const match = /\/rest\/services\/(.+?)\/(FeatureServer|MapServer|VectorTileServer|ImageServer)(\/|$)/.exec(path);
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
  wmShowBaseMap(wmState.doc);
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

  // <b>A first save asks for a name, once — 2026-09-30.</b> A new map saved from the header became
  // *Untitled map* without a word, and a list of those is a list nobody can tell apart. The title box is
  // shown with the placeholder name selected; pressing Save again keeps it, if that is what was meant.
  if (!wmState.id && !asNew && title === "Untitled map" && !wmState.named) {
    wmState.named = true;
    wmShowTab("map");
    wmSayIn("mapStatus", "Give the map a name, then press Save again.");
    wm$("mapTitle").focus();
    wm$("mapTitle").select();
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
    // ADR-119: the map's picture, as it looks now — Portal's Map Viewer does the same on save.
    const picture = await wmSendThumbnail(saved.id);
    wmDrawSaved();
    wmDrawMapForm();
    // An open Style panel said *not saved yet*; it is now.
    if (wmLayers().some(l => (wmRuntime.get(l) || {}).styleOpen)) wmDrawLayerList();
    const done = (creating ? `Saved as “${saved.title}”${asNew ? ", private" : ""}.` : "Saved.")
      + (picture === "taken" ? " Its picture is the view you saved."
        : picture === "empty" ? " The view was empty, so it has no new picture." : " Its picture could not be taken.");
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

  wmShowBaseMap(wmState.doc);

  const target = wmState.doc.initialState && wmState.doc.initialState.viewpoint
    && wmState.doc.initialState.viewpoint.targetGeometry;

  if (target && Number.isFinite(target.xmin)) {
    const reference = target.spatialReference || {};
    const wkid = reference.latestWkid || reference.wkid || 102100;
    try {
      wmFit(ol.proj.transformExtent([target.xmin, target.ymin, target.xmax, target.ymax],
        wkid === 102100 || wkid === 3857 ? WM_MERCATOR : `EPSG:${wkid}`, WM_MERCATOR), 0, 0);
    } catch {
      // A view in a reference this page does not know opens at the world instead.
    }
  }

  wmDrawBookmarks();
  wmTime.span = null;
  wmDrawLayerList();
  await Promise.all(wmLayers().map(wmLoadLayer));
  wmApplyAll();
  wmDrawLayerList();
  wmDrawTime();

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
  // The portal's answer first, so a map's basemap choices include the operator's ground (ADR-086).
  if (typeof SERVER_GROUND_READY !== "undefined") await SERVER_GROUND_READY.catch(() => null);

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

  // <b>A reader who is not signed in reads the map; nothing about it is theirs to change — 2026-09-30.</b>
  // The design review opened a public map signed out and was offered Add layer, Remove, a filter box and
  // a *My content* link that led to a sign-in. Moving the view and switching layers stay: those are
  // reading. The Studio link becomes the way to sign in.
  document.body.classList.toggle("reader", !wmMe.authenticated);
  if (!wmMe.authenticated && wm$("studio")) wm$("studio").textContent = "Sign in";

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

  // <b>A new map starts on the server's ground when the operator chose one</b> — the portal's default
  // basemap, as a new map in Map Viewer starts on the organisation's (ADR-086 §5.3).
  if (wmGroundBaseMapLayers().length > 0) wmState.doc.baseMap = wmBaseMapJson("ground");

  if (service) {
    wmState.meta.title = service.split("/").pop();
    wmDrawSaved();
    await wmOpen(wmState.doc);

    const layer = WM_QUERY.get("layer");
    let added = 0;
    let last = null;

    for (const type of ["FeatureServer", "VectorTileServer", "MapServer", "ImageServer"]) {
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


// ---------------------------------------------------------------- labels (ADR-117)

/** Where ArcGIS puts a label, by the geometry the layer holds. */
function wmLabelPlacement(geometryType) {
  return /Point/.test(geometryType || "") ? "esriServerPointLabelPlacementAboveRight"
    : /Polyline/.test(geometryType || "") ? "esriServerLinePlacementAboveAlong"
    : "esriServerPolygonPlacementAlwaysHorizontal";
}

/**
 * The label a layer shows in this map, read from the Web Map's `labelingInfo` — the field, the size and the colours —
 * or null when it shows none. Only a label that is one field's value is read: `$feature["name"]`, `$feature.name`
 * or the older `[name]`; anything else is an expression this viewer does not run, and the layer draws without one.
 */
function wmLabelOf(layer) {
  if (!layer.showLabels) return null;
  const info = ((layer.layerDefinition || {}).drawingInfo || {}).labelingInfo;
  const one = Array.isArray(info) ? info[0] : null;
  if (!one) return null;
  const text = (one.labelExpressionInfo && one.labelExpressionInfo.expression) || one.labelExpression || "";
  const m = /^\s*\$feature\[\s*["']([^"']+)["']\s*\]\s*$/.exec(text) || /^\s*\$feature\.([A-Za-z_][\w]*)\s*$/.exec(text)
    || /^\s*\[([^\]]+)\]\s*$/.exec(text);
  if (!m) return null;
  const symbol = one.symbol || {};
  return {
    minScale: Number(one.minScale) || 0,
    maxScale: Number(one.maxScale) || 0,
    field: m[1],
    size: Number((symbol.font || {}).size) || 10,
    colour: wmColour(symbol.color, "#1f2933"),
    halo: Number(symbol.haloSize) > 0 ? wmColour(symbol.haloColor, "#ffffff") : null,
  };
}

/**
 * The scales a map's labels are drawn between, as OpenLayers resolutions — ADR-131. ArcGIS's `minScale` is the most
 * zoomed-out scale they show at and `maxScale` the most zoomed-in, zero meaning no limit; a Web Mercator metre at the
 * equator is 1 / (96 dpi × 39.37 in/m) of a scale's denominator, which is how ArcGIS converts them.
 */
function wmScaleRange(label) {
  const perScale = 1 / (96 * 39.37);
  return {
    ...(label.minScale > 0 ? { maxResolution: label.minScale * perScale } : {}),
    ...(label.maxScale > 0 ? { minResolution: label.maxScale * perScale } : {}),
  };
}

/**
 * The scales a feature layer is drawn between: the map's own, when its author set one, else the layer's — the order
 * ArcGIS reads a web map's `layerDefinition` over the service.
 */
function wmLayerRange(layer, info) {
  const own = (layer && layer.layerDefinition) || {};
  const has = value => Number(value) > 0;
  return has(own.minScale) || has(own.maxScale)
    ? { minScale: Number(own.minScale) || 0, maxScale: Number(own.maxScale) || 0 }
    : { minScale: Number((info || {}).minScale) || 0, maxScale: Number((info || {}).maxScale) || 0 };
}

/** Whether the map is now inside a feature layer's range. */
function wmInRange(layer, info) {
  const range = wmLayerRange(layer, info);
  const now = wmMap.getView().getResolution() * 96 * 39.37;
  return (!range.minScale || now <= range.minScale * WM_RANGE_SLACK)
    && (!range.maxScale || now >= range.maxScale / WM_RANGE_SLACK);
}

/** What the layer list says about a layer the map is outside the range of. */
function wmRangeNote(range) {
  const scale = value => `1:${Math.round(value).toLocaleString()}`;
  const now = wmMap.getView().getResolution() * 96 * 39.37;
  return range.minScale && now > range.minScale * WM_RANGE_SLACK
    ? `Not drawn at this scale. Zoom in past ${scale(range.minScale)} to see it.`
    : `Not drawn at this scale. Zoom out past ${scale(range.maxScale)} to see it.`;
}

/*
  <b>A layer whose last answer was cut short is asked again when the view moves — 2026-10-10.</b> OpenLayers' `bbox`
  strategy remembers every extent it loaded, so the city-wide first read — a thousand of 1,270,971 buildings — made
  every view inside the city count as loaded, and zooming in to a street asked for nothing: the owner zoomed to the
  ground and the same thousand stayed. Marking the extent unloaded in the loader instead would ask again on the next
  frame of the same view, forever; a refresh per move asks once per view.

  The layer list is redrawn when a layer crosses its range, so its sentence follows the map.
*/
wmMap.on("moveend", () => {
  // Before a map is open there are no layers to ask about, and `wmLayers` reads the open map's document.
  if (!wmState.doc) return;
  let crossed = false;
  for (const layer of wmLayers()) {
    const run = wmRuntime.get(layer);
    if (!run || !run.ol || !run.info || wmKind(layer) !== "feature") continue;

    const inside = wmInRange(layer, run.info);
    if (run.inRange !== inside) {
      crossed = true;
      run.inRange = inside;
    }

    if (run.truncated && inside && run.ol.getSource && run.ol.getSource().refresh) {
      run.truncated = false;
      run.ol.getSource().refresh();
    }
  }
  if (crossed) wmDrawLayerList();
});

/** The scales a Labels panel offers, ArcGIS's named ones, from the world down to a building. */
const WM_SCALES = [
  [0, "No limit"], [50000000, "Continent"], [10000000, "Country"], [5000000, "Region"], [1000000, "County"],
  [300000, "City"], [150000, "Town"], [75000, "Neighborhood"], [40000, "Streets"], [20000, "Street"],
  [10000, "Buildings"], [5000, "Building"],
];

/**
 * A scale choice's options, with the one the map already has selected — and kept as its own option when it is not one
 * of the named scales, so a range set in ArcGIS at another scale survives a change made here.
 */
function wmScaleOptions(current, side, other) {
  const now = Number(current) || 0;
  const limit = Number(other) || 0;
  const named = WM_SCALES.filter(([v]) => v > 0);
  // *Zoomed out to* runs from no limit inward; *zoomed in to* runs outward to no limit — the order a slider has.
  const list = side === "out"
    ? [[0, "No limit"], ...named]
    : [...named, [0, "No limit"]];
  if (now && !named.some(([v]) => v === now)) list.unshift([now, "Custom"]);
  // The two cannot cross: a choice that would leave no scale between them is not offered (design review 2026-10-01).
  const crosses = v => v && limit && (side === "out" ? v <= limit : v >= limit);
  return list.map(([v, n]) => `<option value="${v}"${v === now ? " selected" : ""}${crosses(v) ? " disabled" : ""}>${
    v ? `${n} (1:${v.toLocaleString()})` : n}</option>`).join("");
}

/** The map's scale now, as ArcGIS writes it: the denominator of 1:n. */
function wmCurrentScale() {
  return Math.round((wmMap.getView().getResolution() || 0) * 96 * 39.37);
}

/** The Labels panel's line under the range: where the map is now, and whether its labels show there. */
function wmScaleNote(layer) {
  const label = wmLabelOf(layer);
  const now = wmCurrentScale();
  const shown = !label || ((!label.minScale || now <= label.minScale) && (!label.maxScale || now >= label.maxScale));
  return `Current map scale 1:${now.toLocaleString()}${label && !shown ? " — labels are hidden at this scale" : ""}.`;
}

/** A feature's label alone, as the label layer draws it. */
function wmLabelStyle(label) {
  const font = `${Math.round(label.size * 1.33)}px sans-serif`;
  return feature => {
    const value = feature.get(label.field);
    if (value === null || value === undefined || value === "") return null;
    return new ol.style.Style({
      text: new ol.style.Text({
        text: String(value),
        font,
        fill: new ol.style.Fill({ color: label.colour }),
        stroke: label.halo ? new ol.style.Stroke({ color: label.halo, width: 3 }) : undefined,
        offsetY: /Point/.test(feature.getGeometry()?.getType() || "") ? -12 : 0,
        placement: /LineString/.test(feature.getGeometry()?.getType() || "") ? "line" : "point",
        overflow: false,
      }),
    });
  };
}

/** A colour as the colour input wants it. */
function wmHex(rgba, fallback) {
  if (!Array.isArray(rgba)) return fallback;
  return "#" + rgba.slice(0, 3).map(v => Math.max(0, Math.min(255, Number(v) || 0)).toString(16).padStart(2, "0")).join("");
}

function wmLabelPanel(layer, run, key) {
  const k = wmEscape(key);
  // A date would print as milliseconds, so it is not offered; the rest are, the layer's display field first.
  const fields = wmUserFields(run.info).filter(f => !/Date/.test(f.type || ""));
  const label = wmLabelOf(layer);
  const raw = (((layer.layerDefinition || {}).drawingInfo || {}).labelingInfo || [])[0];
  const symbol = (raw && raw.symbol) || {};
  const unread = layer.showLabels && raw && !label;
  const display = (run.info && run.info.displayField) || (fields.find(f => /String/.test(f.type || "")) || fields[0] || {}).name;
  const chosen = label ? label.field : display;
  const on = raw ? !!layer.showLabels : true;

  return `<div class="lstyle" id="lab-${k}">
    <label class="check"><input type="checkbox" id="labOn-${k}" data-act="labelsApply" data-layer="${k}"
      data-focus="labOn:${k}"${on ? " checked" : ""}> Show labels</label>
    <label class="lkind" for="labField-${k}">Label field</label>
    <select id="labField-${k}" data-act="labelsApply" data-layer="${k}" data-focus="labField:${k}">${fields.map(f =>
      `<option value="${wmEscape(f.name)}"${chosen === f.name ? " selected" : ""}>${wmEscape(f.alias || f.name)}</option>`).join("")}</select>
    <div class="row">
      <label class="lkind" for="labSize-${k}">Size (pt)</label>
      <input type="number" id="labSize-${k}" min="6" max="36" step="1" value="${label ? label.size : 10}" style="width:5em"
        data-act="labelsApply" data-layer="${k}" data-focus="labSize:${k}">
      <label class="lkind" for="labColour-${k}">Colour</label>
      <input type="color" id="labColour-${k}" value="${wmHex(symbol.color, "#1f2933")}"
        data-act="labelsApply" data-layer="${k}" data-focus="labColour:${k}">
      <label class="check"><input type="checkbox" id="labHalo-${k}"${!raw || Number(symbol.haloSize) > 0 ? " checked" : ""}
        data-act="labelsApply" data-layer="${k}" data-focus="labHalo:${k}"> Halo</label>
    </div>
    <fieldset class="lrange"><legend class="lkind">Visible range</legend>
      <div class="row">
        <label class="lkind" for="labFrom-${k}">Zoomed out to</label>
        <select id="labFrom-${k}" data-act="labelsApply" data-layer="${k}" data-focus="labFrom:${k}">${
          wmScaleOptions((raw || {}).minScale, "out", (raw || {}).maxScale)}</select>
        <label class="lkind" for="labTo-${k}">Zoomed in to</label>
        <select id="labTo-${k}" data-act="labelsApply" data-layer="${k}" data-focus="labTo:${k}">${
          wmScaleOptions((raw || {}).maxScale, "in", (raw || {}).minScale)}</select>
      </div>
      <p class="lsnote" id="labScale-${k}">${wmEscape(wmScaleNote(layer))}</p>
    </fieldset>
    ${raw ? `<div class="row"><button class="tiny" data-act="labelsReset" data-layer="${k}" data-focus="labelsReset:${k}"
      data-focus-fallback="labOn:${k}">Remove labels</button></div>` : ""}
    <p class="lsnote" id="labSays-${k}" role="status" aria-live="polite">${wmEscape(run.labelSaid
      || (label ? `Labels: ${label.field}, ${label.size} pt.` : unread
        ? "This map labels the layer with an expression this viewer does not draw; a change here replaces it."
        : "Choose a field to label with."))} Labels that would overlap are hidden.</p>
  </div>`;
}

/** The Labels panel's scale range, the wider first whichever way round it was chosen; zero is no limit. */
function wmReadScales(k) {
  const from = Number(wm$(`labFrom-${k}`)?.value) || 0;
  const to = Number(wm$(`labTo-${k}`)?.value) || 0;
  // The options cannot cross; a range read from elsewhere that does is no range rather than labels never drawn.
  return from && to && to >= from ? { minScale: 0, maxScale: 0 } : { minScale: from, maxScale: to };
}

/**
 * Reads a layer's Labels panel into the Web Map — `showLabels` and `layerDefinition.drawingInfo.labelingInfo`, where
 * ArcGIS Pro and Field Maps read a map's labels. A layer with no style of its own in this map gets the service's
 * renderer beside the labels, so a client that takes `drawingInfo` whole does not lose its symbols.
 */
function wmApplyLabels(layer, reset = false) {
  const k = layer.id;
  const run = wmRuntime.get(layer);
  const drawing = ((layer.layerDefinition = layer.layerDefinition || {}).drawingInfo = layer.layerDefinition.drawingInfo || {});

  if (reset) {
    delete drawing.labelingInfo;
    delete layer.showLabels;
    if (!drawing.renderer && Object.keys(drawing).length === 0) delete layer.layerDefinition.drawingInfo;
  } else {
    const field = wm$(`labField-${k}`).value;
    if (!field) return;
    const size = Math.max(6, Math.min(36, Number(wm$(`labSize-${k}`).value) || 10));
    const hex = wm$(`labColour-${k}`).value || "#1f2933";
    const rgb = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
    const halo = wm$(`labHalo-${k}`).checked;
    // A halo is the opposite of the text, so pale text keeps its edge too.
    const pale = (0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]) > 160;

    if (!drawing.renderer && run && run.info && run.info.drawingInfo && run.info.drawingInfo.renderer) {
      drawing.renderer = run.info.drawingInfo.renderer;
    }

    drawing.labelingInfo = [{
      labelExpressionInfo: { expression: `$feature["${field.replace(/"/g, '\\"')}"]` },
      labelExpression: `[${field}]`,
      useCodedValues: true,
      labelPlacement: wmLabelPlacement(run && run.info && run.info.geometryType),
      symbol: {
        type: "esriTS",
        color: [...rgb, 255],
        haloColor: pale ? [31, 41, 51, 255] : [255, 255, 255, 255],
        haloSize: halo ? 1 : 0,
        font: { family: "Arial", size },
      },
      // ADR-131: the scales the labels show between — zoomed out no further than *from*, in no further than *to*.
      ...wmReadScales(k),
    }];
    layer.showLabels = wm$(`labOn-${k}`).checked;
  }

  const now = wmLabelOf(layer);
  const scale = v => `1:${v.toLocaleString()}`;
  const range = !now || (!now.minScale && !now.maxScale) ? ""
    : now.minScale && now.maxScale ? `, from ${scale(now.minScale)} to ${scale(now.maxScale)}`
    : now.minScale ? `, once zoomed in past ${scale(now.minScale)}`
    : `, until zoomed in past ${scale(now.maxScale)}`;
  const said = reset
    ? `No labels on ${layer.title}. Save the map to keep it.`
    : layer.showLabels
      ? `Labels: ${now ? `${now.field}, ${now.size} pt${range}` : "set"}. Save the map to keep them.`
      : `Labels for ${layer.title} are off. Turn on Show labels to see them.`;
  if (run) run.labelSaid = said;
  wmMarkDirty();
  wmRestyle(layer);
  wmDrawLayerList();
}


// ---------------------------------------------------------------- pop-up formats (ADR-118)

const WM_NUMBER_TYPES = new Set(["esriFieldTypeSmallInteger", "esriFieldTypeInteger", "esriFieldTypeBigInteger",
  "esriFieldTypeSingle", "esriFieldTypeDouble"]);

/** ArcGIS's date formats a pop-up names, with how each reads. */
const WM_DATE_FORMATS = [
  ["shortDate", "12/21/1997"],
  ["dayShortMonthYear", "21 Dec 1997"],
  ["longMonthDayYear", "December 21, 1997"],
  ["longDate", "Sunday, December 21, 1997"],
  ["shortDateShortTime", "12/21/1997 6:00 PM"],
  ["shortDateLE", "21/12/1997"],
  ["shortDateLEShortTime", "21/12/1997 6:00 PM"],
];

/** The Format cell of one field: decimal places and thousands for a number, a date format for a date. */
function wmFormatControl(field, format) {
  const n = wmEscape(field.name);
  if (WM_NUMBER_TYPES.has(field.type)) {
    const places = format && Number.isFinite(Number(format.places)) ? String(format.places) : "";
    return `<select data-pop-places="${n}" aria-label="Decimal places of ${n}">
        <option value=""${places === "" ? " selected" : ""}>Default places</option>
        ${[0, 1, 2, 3, 4].map(p => `<option value="${p}"${places === String(p) ? " selected" : ""}>${p} decimal places</option>`).join("")}
      </select>
      <label class="check"><input type="checkbox" data-pop-sep="${n}" aria-label="Thousands separator for ${n}"${
        format && format.digitSeparator ? " checked" : ""}> 1,000 separator</label>`;
  }
  if (field.type === "esriFieldTypeDate") {
    const chosen = (format && format.dateFormat) || "";
    return `<select data-pop-date="${n}" aria-label="Date format of ${n}">
        <option value=""${chosen ? "" : " selected"}>Default (12/21/1997 6:00 PM)</option>
        ${WM_DATE_FORMATS.map(([key, shown]) => `<option value="${key}"${chosen === key ? " selected" : ""}>${shown}</option>`).join("")}
      </select>`;
  }
  return "";
}

/** A field's format as ArcGIS writes it, from its Format cell, or null for none. */
function wmReadFormat(panel, name) {
  const places = panel.querySelector(`[data-pop-places="${CSS.escape(name)}"]`);
  if (places) {
    const separator = !!panel.querySelector(`[data-pop-sep="${CSS.escape(name)}"]`)?.checked;
    if (places.value === "" && !separator) return null;
    return { ...(places.value === "" ? {} : { places: Number(places.value) }), digitSeparator: separator };
  }
  const date = panel.querySelector(`[data-pop-date="${CSS.escape(name)}"]`);
  return date && date.value ? { dateFormat: date.value } : null;
}

/** A value in its format: a number with its places and separators, a date in the format named. */
function wmFormatValue(value, format, type) {
  if (type === "esriFieldTypeDate" && Number.isFinite(Number(value))) {
    const d = new Date(Number(value));
    const months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
      "November", "December"];
    const days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    const [day, month, year] = [d.getDate(), d.getMonth(), d.getFullYear()];
    const h12 = d.getHours() % 12 || 12;
    const time = `${h12}:${String(d.getMinutes()).padStart(2, "0")} ${d.getHours() < 12 ? "AM" : "PM"}`;
    const us = `${month + 1}/${day}/${year}`;
    const le = `${day}/${month + 1}/${year}`;
    return ({
      shortDate: us,
      dayShortMonthYear: `${day} ${months[month].slice(0, 3)} ${year}`,
      longMonthDayYear: `${months[month]} ${day}, ${year}`,
      longDate: `${days[d.getDay()]}, ${months[month]} ${day}, ${year}`,
      shortDateShortTime: `${us} ${time}`,
      shortDateLE: le,
      shortDateLEShortTime: `${le} ${time}`,
    })[(format && format.dateFormat) || "shortDateShortTime"] || `${us} ${time}`;
  }
  if (WM_NUMBER_TYPES.has(type) && format && Number.isFinite(Number(value))) {
    const places = Number.isFinite(Number(format.places)) ? Number(format.places) : undefined;
    return Number(value).toLocaleString("en-US", {
      minimumFractionDigits: places, maximumFractionDigits: places ?? 20, useGrouping: !!format.digitSeparator,
    });
  }
  return String(value);
}


// ---------------------------------------------------------------- the map's picture (ADR-119)

/**
 * The view as a 600 × 400 PNG, sent as the map's picture — composed the way OpenLayers' own export example does it,
 * after the map has finished drawing, the whole view fitted inside the frame rather than cropped. <b>Not sent when it
 * cannot be read</b> (a basemap from another origin taints the canvas) <b>or when it is one colour</b> — a picture of
 * nothing is worse than none. Answers what happened, for the save's status line.
 */
function wmSendThumbnail(id) {
  return new Promise(resolve => {
    // <b>At most three seconds' wait</b>: a layer that never finishes loading — one the map cannot read — means
    // `rendercomplete` never comes, and a save must not wait on its picture for ever. What is drawn by then is taken.
    let done = false;
    const take = () => { if (!done) { done = true; resolve(wmTakeThumbnail(id)); } };
    try {
      wmMap.once("rendercomplete", take);
      setTimeout(take, 3000);
      wmMap.renderSync();
    } catch {
      if (!done) { done = true; resolve("not taken"); }
    }
  });
}

/**
 * The map as one picture: every layer's canvas drawn in order onto one, as OpenLayers' own export example does — the
 * thumbnail's and the print's (ADR-133), so the two cannot disagree about what the map looks like. Null with no size.
 */
function wmComposeMap() {
  const size = wmMap.getSize();
  if (!size || !size[0] || !size[1]) return null;
  const whole = document.createElement("canvas");
  whole.width = size[0];
  whole.height = size[1];
  const context = whole.getContext("2d");
  const ground = getComputedStyle(wmMap.getViewport()).backgroundColor;
  context.fillStyle = ground && ground !== "rgba(0, 0, 0, 0)" ? ground : "#ffffff";
  context.fillRect(0, 0, whole.width, whole.height);

  for (const canvas of wmMap.getViewport().querySelectorAll(".ol-layer canvas, canvas.ol-layer")) {
    if (!(canvas.width > 0)) continue;
    const opacity = canvas.parentNode.style.opacity || canvas.style.opacity;
    context.globalAlpha = opacity === "" ? 1 : Number(opacity);
    const transform = canvas.style.transform;
    const matrix = transform
      ? transform.match(/^matrix\(([^(]*)\)$/)[1].split(",").map(Number)
      : [parseFloat(canvas.style.width) / canvas.width, 0, 0, parseFloat(canvas.style.height) / canvas.height, 0, 0];
    context.setTransform(...matrix);
    const background = canvas.parentNode.style.backgroundColor;
    if (background) {
      context.fillStyle = background;
      context.fillRect(0, 0, canvas.width, canvas.height);
    }
    context.drawImage(canvas, 0, 0);
  }

  context.globalAlpha = 1;
  context.setTransform(1, 0, 0, 1, 0, 0);
  return whole;
}

async function wmTakeThumbnail(id) {
  try {
    const whole = wmComposeMap();
    if (!whole) return "not taken";
    const context = whole.getContext("2d");

    const out = document.createElement("canvas");
    out.width = 600;
    out.height = 400;
    const frame = out.getContext("2d");
    frame.fillStyle = context.fillStyle;
    frame.fillRect(0, 0, 600, 400);
    const scale = Math.min(600 / whole.width, 400 / whole.height);
    const w = whole.width * scale;
    const h = whole.height * scale;
    frame.drawImage(whole, (600 - w) / 2, (400 - h) / 2, w, h);

    // One colour throughout is a picture of nothing.
    const pixels = frame.getImageData(0, 0, 600, 400).data;
    let varied = false;
    for (let i = 4; i < pixels.length && !varied; i += 4 * 37) {
      varied = pixels[i] !== pixels[0] || pixels[i + 1] !== pixels[1] || pixels[i + 2] !== pixels[2];
    }
    if (!varied) return "empty";

    const blob = await new Promise(done => out.toBlob(done, "image/png"));
    if (!blob) return "not taken";
    await wmFetch(`/content/webmaps/${encodeURIComponent(id)}/thumbnail`, {
      method: "PUT", headers: { "Content-Type": "image/png" }, body: blob,
    });
    return "taken";
  } catch {
    // A tainted canvas, or a refused upload: the picture stays as it was.
    return "not taken";
  }
}

// ADR-119: a view the reader moved is a change — the saved view and the picture come from it — but a view the page
// moved itself (opening, zooming to a layer) is not.
let wmMovedByHand = false;
for (const kind of ["pointerdown", "wheel", "keydown"]) {
  wmMap.getViewport().addEventListener(kind, () => { wmMovedByHand = true; }, { passive: true });
}
wmMap.on("moveend", () => {
  if (wmMovedByHand && wmState.doc) {
    wmMovedByHand = false;
    if (!wmState.dirty) wmMarkDirty();
  }
});


// ---------------------------------------------------------------- editing (ADR-134)

/** The features the open card shows, by `layerId:objectId`, so an edit works on what was read. */
const wmIdentified = new Map();

/** Where the card was asked about, so it can be asked again after a save or a cancel. */
let wmLastClick = null;

/** The form open in the card — which feature, whether it has been typed in, and where the card was scrolled. */
const wmEditing = { key: null, dirty: false, scroll: 0, saving: false };

function wmRemember(layer, feature) {
  const info = (wmRuntime.get(layer) || {}).info || {};
  const oid = (feature.attributes || {})[info.objectIdField || "objectid"];
  const key = `${layer.id}:${oid}`;
  wmIdentified.set(key, { layer, feature, oid });
  return key;
}

/** A feature as a person names it: its display field's value, or its id. */
function wmFeatureName(held) {
  const info = (wmRuntime.get(held.layer) || {}).info || {};
  const shown = info.displayField && (held.feature.attributes || {})[info.displayField];
  return shown !== null && shown !== undefined && shown !== "" ? String(shown) : `feature ${held.oid}`;
}

/**
 * Whether the reader may try to change this layer's features — ADR-134: a signed-in reader, on a layer whose service
 * offers the operation. The server decides the rest (ownership, ADR-075 and ADR-115), and its refusal is shown as said.
 */
function wmEditable(layer, operation = "Update") {
  const info = (wmRuntime.get(layer) || {}).info || {};
  return !!(wmMe.authenticated && wmToken && String(info.capabilities || "").split(",").map(c => c.trim()).includes(operation));
}

/** The fields an attribute form offers: the layer's editable ones, not its bookkeeping or editor tracking. */
function wmEditFields(info) {
  const tracking = new Set(Object.values(info.editFieldsInfo || {}).filter(v => typeof v === "string").map(v => v.toLowerCase()));
  return (info.fields || []).filter(f => f.editable !== false && !wmSystemField(f.name, info)
    && !["esriFieldTypeOID", "esriFieldTypeGlobalID", "esriFieldTypeGeometry", "esriFieldTypeBlob", "esriFieldTypeRaster"].includes(f.type)
    && !tracking.has(String(f.name).toLowerCase()));
}

/** A date's milliseconds as a `datetime-local` value to the second, in the reader's own time. */
function wmLocalInput(ms) {
  if (ms === null || ms === undefined || !Number.isFinite(Number(ms))) return "";
  const d = new Date(Number(ms));
  const pad = n => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

/** The whole-number limits ArcGIS's integer types hold. */
const WM_INT_RANGE = { esriFieldTypeSmallInteger: [-32768, 32767], esriFieldTypeInteger: [-2147483648, 2147483647] };

/** One field's control, as its type and domain ask: a list for coded values, a number, a date, a line or a box of text. */
function wmFieldControl(field, value, cell = null) {
  const id = wmEscape(cell ? `cell-${field.name}-${cell.row}` : `edit-${field.name}`);
  const name = wmEscape(field.name);
  const common = `id="${id}" data-field="${name}"${cell
    ? ` data-cell="${cell.row}" aria-label="${wmEscape(cell.label)}"`
    : ` aria-describedby="${id}-says"`}${field.nullable === false ? " required aria-required=\"true\"" : ""}`;
  const domain = field.domain || {};
  if (domain.type === "codedValue") {
    return `<select ${common}>
      ${field.nullable === false ? "" : `<option value=""${value === null || value === undefined ? " selected" : ""}>—</option>`}
      ${(domain.codedValues || []).map(c => `<option value="${wmEscape(String(c.code))}"${String(c.code) === String(value) ? " selected" : ""}>${
        wmEscape(c.name)}</option>`).join("")}</select>`;
  }
  if (WM_NUMBER_TYPES.has(field.type)) {
    const whole = /Integer/.test(field.type);
    const range = domain.type === "range" && Array.isArray(domain.range) ? ` min="${domain.range[0]}" max="${domain.range[1]}"` : "";
    // In a table cell a number is typed, as text: arrows move between rows and the wheel scrolls (design review).
    return cell
      ? `<input type="text" inputmode="${whole ? "numeric" : "decimal"}" ${common}${range}
          value="${value === null || value === undefined ? "" : wmEscape(String(value))}">`
      : `<input type="number" ${common} step="${whole ? 1 : "any"}"${range}
          value="${value === null || value === undefined ? "" : wmEscape(String(value))}">`;
  }
  if (field.type === "esriFieldTypeDate") {
    return `<input type="datetime-local" step="1" ${common} value="${wmEscape(wmLocalInput(value))}">`;
  }
  const text = value === null || value === undefined ? "" : String(value);
  // A long value, or a field with no limit, gets a box rather than a line.
  return !cell && (field.length > 255 || text.length > 80)
    ? `<textarea ${common} rows="3"${field.length ? ` maxlength="${field.length}"` : ""}>${wmEscape(text)}</textarea>`
    : `<input type="text" ${common}${field.length ? ` maxlength="${field.length}"` : ""} value="${wmEscape(text)}">`;
}

/** Turns the card into one feature's attribute form. */
function wmOpenEdit(key) {
  const held = wmIdentified.get(key);
  if (!held) return;
  const info = (wmRuntime.get(held.layer) || {}).info || {};
  const fields = wmEditFields(info);
  const card = wm$("identify");
  wmEditing.key = key;
  wmEditing.dirty = false;
  wmEditing.scroll = card.scrollTop;
  const required = fields.some(f => f.nullable === false);
  card.innerHTML = `<div class="top"><b>${held.isNew ? "New feature" : `Edit ${wmEscape(wmFeatureName(held))}`} <span class="lkind">${
      wmEscape(held.layer.title)}</span></b>
      <button class="tiny" data-close aria-label="Close">&times;</button></div>
    <form class="editform" data-editing="${wmEscape(key)}" novalidate>
      ${held.isNew ? `<p class="hint">${wmEscape(wmShapeSaid(held.geometry))}</p>` : ""}
      ${required ? `<p class="hint">* required</p>` : ""}
      ${fields.length ? fields.map(f => `<div class="editfield">
        <label class="field" for="${wmEscape(`edit-${f.name}`)}">${wmEscape(f.alias || f.name)}${
          f.nullable === false ? " <span aria-hidden=\"true\">*</span>" : ""}</label>
        ${wmFieldControl(f, (held.feature.attributes || {})[f.name])}
        <p class="fieldsays" id="${wmEscape(`edit-${f.name}-says`)}"></p></div>`).join("")
        : `<p class="hint">This layer has no attribute that can be changed.</p>`}
      <p class="said" id="editSays" role="status" aria-live="polite" tabindex="-1"></p>
      <div class="row editacts">
        ${fields.length || held.isNew ? `<button type="submit" class="primary">${held.isNew ? "Create" : "Save"}</button>` : ""}
        <button type="button" data-edit-cancel>Cancel</button>
      </div>
    </form>`;
  card.hidden = false;
  card.scrollTop = 0;
  card.querySelector("[data-field]")?.focus();
}

/**
 * One control's value, typed as its field is, against what the feature held: `same` when it has not changed (a date
 * compared at the control's own precision, so a date not touched is not rewritten), or the problem to say.
 */
function wmReadControl(control, field, was) {
  const label = field.alias || field.name;
  const raw = control.value;
  if (raw === "") return field.nullable === false ? { problem: `${label} is required.` } : { value: null, same: was === null };
  if (field.type === "esriFieldTypeDate") {
    if (raw === wmLocalInput(was)) return { same: true };
    const value = new Date(raw).getTime();
    return Number.isFinite(value) ? { value } : { problem: `${label} is not a date.` };
  }
  if (WM_NUMBER_TYPES.has(field.type) || ((field.domain || {}).type === "codedValue"
    && typeof ((field.domain.codedValues || [])[0] || {}).code === "number")) {
    const value = Number(raw);
    const limits = WM_INT_RANGE[field.type];
    if (!Number.isFinite(value)) return { problem: `${label} takes a number.` };
    if (/Integer/.test(field.type) && !Number.isInteger(value)) return { problem: `${label} takes a whole number.` };
    if (limits && (value < limits[0] || value > limits[1])) {
      return { problem: `${label} takes a whole number from ${limits[0].toLocaleString()} to ${limits[1].toLocaleString()}.` };
    }
    const min = control.getAttribute("min");
    const max = control.getAttribute("max");
    if (min !== null && min !== "" && value < Number(min)) return { problem: `${label} is at least ${min}.` };
    if (max !== null && max !== "" && value > Number(max)) return { problem: `${label} is at most ${max}.` };
    return { value, same: value === was };
  }
  return { value: raw, same: raw === (was === null ? null : String(was)) };
}

/** Reads the form's changed values, typed as their fields are; a problem is said under its field. */
function wmReadEdit(form, held) {
  const info = (wmRuntime.get(held.layer) || {}).info || {};
  const before = held.feature.attributes || {};
  const changed = {};
  let first = null;
  for (const control of form.querySelectorAll("[data-field]")) {
    const field = (info.fields || []).find(f => f.name === control.dataset.field);
    if (!field) continue;
    const read = wmReadControl(control, field, before[field.name] === undefined ? null : before[field.name]);
    control.setAttribute("aria-invalid", read.problem ? "true" : "false");
    wm$(`${control.id}-says`).textContent = read.problem || "";
    if (read.problem && !first) first = control;
    if (!read.problem && !read.same) changed[field.name] = read.value;
  }
  return { changed, first };
}

/** Sends one feature's changed attributes through the layer's applyEdits, and says what the server said. */
async function wmSaveEdit(form) {
  const key = form.dataset.editing;
  const held = wmIdentified.get(key);
  if (!held || wmEditing.saving) return;
  const says = wm$("editSays");
  const { changed, first } = wmReadEdit(form, held);
  if (first) {
    says.textContent = "Not saved: correct the field marked below it.";
    first.focus();
    return;
  }
  if (!Object.keys(changed).length && !held.isNew) {
    says.textContent = "Nothing has changed.";
    return;
  }
  const info = (wmRuntime.get(held.layer) || {}).info || {};
  wmEditing.saving = true;
  says.textContent = "Saving…";
  try {
    const edit = held.isNew
      ? { adds: JSON.stringify([{ geometry: held.geometry, attributes: { ...(held.preset || {}), ...changed } }]) }
      : { updates: JSON.stringify([{ attributes: { [info.objectIdField || "objectid"]: held.oid, ...changed } }]) };
    const answer = await wmFetch(`${held.layer.url}/applyEdits`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ ...edit, rollbackOnFailure: "true", f: "json" }),
    });
    const result = ((answer || {})[held.isNew ? "addResults" : "updateResults"] || [])[0];
    if (result && result.success === false) {
      says.textContent = `Not saved: ${((result.error || {}).description) || "the server refused the change."}`;
      says.focus();
      return;
    }
    const name = held.isNew ? `A new feature in ${held.layer.title}` : wmFeatureName(held);
    if (!held.isNew) wmTableTakeSaved(held.layer, held.oid, changed, name);
    const focusKey = held.isNew && result && result.objectId !== undefined ? `${held.layer.id}:${result.objectId}` : key;
    wmEditing.key = null;
    wmEditing.dirty = false;
    wmSketch.getSource().clear();
    wmRefreshLayer(held.layer);
    if (wmLastClick) await wmIdentify(wmLastClick);
    // After the card is asked again, so its own "… here" does not speak over it.
    const shownNow = !!document.querySelector(`#identify [data-edit="${CSS.escape(focusKey)}"]`);
    wmSay(held.isNew
      ? (shownNow ? `${name} created.` : `${name} created, but the layer's filter or the time window hides it here.`)
      : `${name} saved.`);
    (document.querySelector(`#identify [data-edit="${CSS.escape(focusKey)}"]`) || wm$("identify").querySelector("button"))?.focus();
  } catch (e) {
    says.textContent = `Not saved: ${e.message || e}`;
    says.focus();
  } finally {
    wmEditing.saving = false;
  }
}

/** Leaves the form for the card it came from, where it was scrolled, on the feature's Edit. */
async function wmCancelEdit() {
  const key = wmEditing.key;
  if (key && (wmIdentified.get(key) || {}).isNew) {
    wmEditing.key = null;
    wmEditing.dirty = false;
    wmSketch.getSource().clear();
    wm$("identify").hidden = true;
    wmSay("Nothing was added.");
    const layer = wmIdentified.get(key).layer;
    document.querySelector(`[data-act="addFeature"][data-layer="${CSS.escape(layer.id)}"]`)?.focus();
    return;
  }
  const scroll = wmEditing.scroll;
  wmEditing.key = null;
  wmEditing.dirty = false;
  if (wmLastClick) await wmIdentify(wmLastClick);
  wm$("identify").scrollTop = scroll;
  if (key) document.querySelector(`#identify [data-edit="${CSS.escape(key)}"]`)?.focus();
}

/** Whether a form typed in may be left: asked, rather than dropped without a word (design review 2026-10-01). */
function wmMayLeaveEdit() {
  return !wmEditing.key || !wmEditing.dirty || confirm("Discard your changes to this feature?");
}

/** Deletes one feature through its layer's applyEdits, once the reader has said yes. */
async function wmDeleteFeature(key) {
  const held = wmIdentified.get(key);
  if (!held) return;
  const name = wmFeatureName(held);
  if (!confirm(`Delete ${name} from ${held.layer.title}? This cannot be undone.`)) return;
  try {
    const answer = await wmFetch(`${held.layer.url}/applyEdits`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ deletes: String(held.oid), rollbackOnFailure: "true", f: "json" }),
    });
    const result = ((answer || {}).deleteResults || [])[0];
    if (result && result.success === false) {
      wmSay(`${name} was not deleted: ${((result.error || {}).description) || "the server refused."}`, true);
      return;
    }
    wmRefreshLayer(held.layer);
    if (wmLastClick) await wmIdentify(wmLastClick);
    wmSay(`${name} deleted.`);
    (wm$("identify").hidden ? wm$("map") : wm$("identify").querySelector("button"))?.focus();
  } catch (e) {
    wmSay(`${name} was not deleted: ${e.message || e}`, true);
  }
}

// ---- adding a feature: draw its shape, then its attributes in the card's form

/** The layer a feature is being added to, and the drawing in progress. */
const wmAdding = { layer: null, draw: null };

/** What is drawn for a new feature until it is saved or given up. */
const wmSketch = new ol.layer.Vector({
  source: new ol.source.Vector(),
  zIndex: 1000,
  style: new ol.style.Style({
    fill: new ol.style.Fill({ color: "rgba(11, 97, 87, .2)" }),
    stroke: new ol.style.Stroke({ color: "#0b6157", width: 2, lineDash: [6, 4] }),
    image: new ol.style.Circle({ radius: 7, fill: new ol.style.Fill({ color: "#0b6157" }), stroke: new ol.style.Stroke({ color: "#fff", width: 2 }) }),
  }),
});
wmMap.addLayer(wmSketch);

/** The shape OpenLayers draws for a layer's geometry type. */
function wmDrawType(layer) {
  const type = String(((wmRuntime.get(layer) || {}).info || {}).geometryType || "");
  return /Polygon/.test(type) ? "Polygon" : /Polyline/.test(type) ? "LineString" : /Multipoint/.test(type) ? "MultiPoint" : "Point";
}

function wmStopAdding() {
  if (wmAdding.draw) wmMap.removeInteraction(wmAdding.draw);
  wmAdding.draw = null;
  wmAdding.layer = null;
  wm$("sketchBar").hidden = true;
  wm$("mapWrap").classList.remove("sketching");
}

/** What a drawn shape is, in words, for the new feature's form. */
function wmShapeSaid(esri) {
  if (!esri) return "";
  if (Number.isFinite(esri.x)) {
    const [lon, lat] = ol.proj.toLonLat([esri.x, esri.y]);
    return `A point at ${lat.toFixed(5)}, ${lon.toFixed(5)}.`;
  }
  if (esri.paths) return `A line of ${esri.paths[0].length} points.`;
  if (esri.rings) return `An area of ${Math.max(0, esri.rings[0].length - 1)} corners.`;
  if (esri.points) return `${esri.points.length} points.`;
  return "";
}

/** Places a point, or a vertex of a line or area, at the map's centre — how a keyboard or touch reader draws. */
function wmPlaceAtCentre() {
  const draw = wmAdding.draw;
  if (!draw) return;
  const centre = wmMap.getView().getCenter();
  if (wmDrawType(wmAdding.layer) === "Point") {
    const feature = new ol.Feature(new ol.geom.Point(centre));
    wmSketch.getSource().addFeature(feature);
    draw.dispatchEvent({ type: "drawend", feature });
    return;
  }
  draw.appendCoordinates([centre]);
}

/** Counts the sketch's points as it is drawn, says each, and lets Finish go only once the shape can be one. */
function wmSketchCount(type, geometry) {
  const coordinates = type === "Polygon" ? (geometry.getCoordinates()[0] || []) : geometry.getCoordinates();
  // OpenLayers keeps the moving pointer as the sketch's last point; placed points are the ones before it.
  const placed = Math.max(0, coordinates.length - (type === "Polygon" ? 2 : 1));
  const least = type === "Polygon" ? 3 : 2;
  wm$("sketchFinish").disabled = placed < least;
  if (placed !== wmAdding.placed) {
    wmSay(placed > (wmAdding.placed || 0) ? `Point ${placed} placed.` : `Point removed; ${placed} left.`);
    wmAdding.placed = placed;
  }
}

/** Starts drawing a new feature for a layer; the form opens when the shape is finished. */
function wmStartAdding(layer) {
  if (!wmMayLeaveEdit()) return;
  wmStopAdding();
  wmStopMeasuring();
  wmEditing.key = null;
  wm$("identify").hidden = true;
  wmSketch.getSource().clear();
  const type = wmDrawType(layer);
  const draw = new ol.interaction.Draw({ source: wmSketch.getSource(), type, style: wmSketch.getStyle() });
  wmAdding.layer = layer;
  wmAdding.draw = draw;
  wmMap.addInteraction(draw);
  // The sketch bar: placing at the centre, undoing and finishing without a mouse (design review 2026-10-01).
  const bar = wm$("sketchBar");
  bar.hidden = false;
  wm$("sketchSays").textContent = type === "Point"
    ? `New feature of ${layer.title}: click the map, or place it at the centre.`
    : `New feature of ${layer.title}: click the map for each point, or place them at the centre; Finish when done.`;
  wm$("sketchUndo").hidden = type === "Point";
  wm$("sketchFinish").hidden = type === "Point";
  wm$("sketchFinish").disabled = true;
  wmAdding.placed = 0;
  wm$("mapWrap").classList.add("sketching");
  draw.on("drawstart", event => {
    const geometry = event.feature.getGeometry();
    geometry.on("change", () => wmSketchCount(type, geometry));
  });
  wm$("sketchPlace").focus();
  wmSay(wm$("sketchSays").textContent);
  draw.on("drawend", event => {
    const geometry = event.feature.getGeometry();
    const esri = WM_ESRI.writeGeometryObject(geometry, { featureProjection: WM_MERCATOR, dataProjection: WM_MERCATOR });
    esri.spatialReference = { wkid: 102100, latestWkid: 3857 };
    const at = geometry.getType() === "Polygon" ? geometry.getInteriorPoint().getCoordinates().slice(0, 2)
      : geometry.getType() === "LineString" ? geometry.getCoordinateAt(0.5)
      : ol.extent.getCenter(geometry.getExtent());
    // The interaction is done with; removed after this event so it does not take the next click.
    setTimeout(() => { wmStopAdding(); wmDrawLayerList(); }, 0);
    wmLastClick = at;
    const key = `${layer.id}:new`;
    // On a layer with time, the new feature starts at the end of the time window, so the slider does not hide it the
    // moment it is created (design review 2026-10-01); the reader may change it.
    const timeField = (((wmRuntime.get(layer) || {}).info || {}).timeInfo || {}).startTimeField;
    const attributes = timeField ? { [timeField]: wmTime.span ? Math.round(wmTimeAt(wmTime.end)) : Date.now() } : {};
    wmIdentified.set(key, { layer, feature: { attributes }, oid: null, isNew: true, geometry: esri, preset: { ...attributes } });
    wmOpenEdit(key);
    wmSay(`Shape drawn. Fill in the new feature of ${layer.title}, then choose Create in the card.`);
  });
}

wm$("sketchBar").addEventListener("click", event => {
  const t = event.target instanceof Element ? event.target.closest("button") : null;
  if (!t || !wmAdding.draw) return;
  if (t.id === "sketchPlace") wmPlaceAtCentre();
  else if (t.id === "sketchUndo") wmAdding.draw.removeLastPoint();
  else if (t.id === "sketchFinish") wmAdding.draw.finishDrawing();
  else if (t.id === "sketchCancel") {
    const layer = wmAdding.layer;
    wmStopAdding();
    wmSketch.getSource().clear();
    wmDrawLayerList();
    wmSay("Adding stopped; nothing was added.");
    if (layer) document.querySelector(`[data-act="addFeature"][data-layer="${CSS.escape(layer.id)}"]`)?.focus();
  }
});

/** Draws a layer again from the server, and the open table with it. */
function wmRefreshLayer(layer, mapOnly = false) {
  const run = wmRuntime.get(layer) || {};
  const source = run.ol && run.ol.getSource && run.ol.getSource();
  if (source && source.refresh) source.refresh();
  if (mapOnly || wmTable.layer !== layer) return;
  // Unsaved cells are not read over; the table says the card saved (design review 2026-10-01).
  if (wmTableChanges().length) {
    if (!wmTable.keepSaid) wmSayIn("tableStatus", "Saved from the card; the table still has unsaved changes.");
    return;
  }
  wmLoadTable();
}

wm$("identify").addEventListener("click", event => {
  const t = event.target instanceof Element ? event.target.closest("button") : null;
  if (!t) return;
  if (t.dataset.edit !== undefined) { wmOpenEdit(t.dataset.edit); return; }
  if (t.dataset.delete !== undefined) { wmDeleteFeature(t.dataset.delete); return; }
  if (t.dataset.editCancel !== undefined && wmMayLeaveEdit()) wmCancelEdit();
});
wm$("identify").addEventListener("input", event => {
  if (event.target instanceof Element && event.target.closest("form.editform")) wmEditing.dirty = true;
});
wm$("identify").addEventListener("submit", event => {
  const form = event.target instanceof HTMLFormElement ? event.target : null;
  if (!form || !form.dataset.editing) return;
  event.preventDefault();
  wmSaveEdit(form);
});


// ---------------------------------------------------------------- raster functions on an imagery layer (ADR-136)

/** Whether an image service may be shown through a raster function: one it declares, on one band of measurements. */
function wmFunctionsOffered(info) {
  return wmFunctionOptions(info).length > 0;
}

/**
 * The functions a layer may be shown through: the surface three on one band of measurements (ADR-136), the band
 * functions on an image of several (ADR-151, ADR-158) — band combination from three bands, NDVI from four.
 */
function wmFunctionOptions(info) {
  if (!info || !info.allowRasterFunction) return [];
  const names = new Set((info.rasterFunctionInfos || []).map(f => f.name));
  const bands = info.bandCount || 1;
  const options = [];
  if (bands < 3 && info.pixelType !== "U8" && names.has("Slope")) {
    options.push(["Hillshade", "Hillshade"], ["Slope", "Slope (degrees)"], ["Aspect", "Aspect (direction)"]);
  }
  if (bands >= 3 && names.has("ExtractBand")) options.push(["ExtractBand", "Band combination"]);
  if (bands >= 4 && names.has("NDVI")) options.push(["NDVI", "NDVI"]);
  if (bands >= 2 && names.has("BandArithmetic")) options.push(["BandArithmetic", "Band arithmetic"]);
  return options;
}

/** A band function's arguments as first chosen: false colour, NDVI from red and near infrared, its expression. */
function wmRuleDefaults(fn, bands) {
  if (fn === "ExtractBand") return { BandIDs: bands >= 4 ? [3, 2, 1] : [0, 1, 2] };
  if (fn === "NDVI") return { VisibleBandID: bands >= 4 ? 2 : 0, InfraredBandID: bands >= 4 ? 3 : 1, Scientific: true };
  if (fn === "BandArithmetic") return { Method: 0, BandIndexes: bands >= 4 ? "(B4 - B3) / (B4 + B3)" : "(B2 - B1) / (B2 + B1)" };
  return null;
}

/**
 * A band function's arguments applied — after the service has read them, so an expression it cannot read is said
 * beside the input, with the draft kept, rather than drawn as a broken picture (ux review, ADR-158).
 */
async function wmApplyRuleArgs(layer, args) {
  const run = wmRuntime.get(layer);
  const rule = { ...layer.renderingRule, rasterFunctionArguments: args };
  try {
    await wmFetch(`${layer.url}/exportImage?` + wmParams({ f: "json", size: "1,1", renderingRule: JSON.stringify(rule) }));
  } catch (e) {
    if (run) {
      run.ruleError = `Not applied: ${e.message}`;
      run.ruleDraft = args.BandIndexes;
    }
    wmDrawLayerList();
    wmSayIn("layersStatus", `The expression on ${layer.title} was not applied.`, true);
    return;
  }
  const hadError = run && run.ruleError;
  if (run) { run.ruleError = null; run.ruleDraft = undefined; }
  layer.renderingRule = rule;
  const source = run && run.ol && run.ol.getSource && run.ol.getSource();
  if (source && source.updateParams) source.updateParams({ RENDERINGRULE: JSON.stringify(rule) });
  wmMarkDirty();
  if (hadError) wmDrawLayerList();
  wmSayIn("layersStatus", rule.rasterFunction === "BandArithmetic"
    ? `${layer.title} is drawn as ${args.BandIndexes}.`
    : `${layer.title} is drawn again with the bands chosen.`);
}

/** A band function's controls under the layer's Shown as, each sending its change as `ruleArg`. */
function wmRuleArgsMarkup(layer, key, info) {
  const rule = layer.renderingRule || {};
  const args = rule.rasterFunctionArguments || {};
  const bands = info.bandCount || 1;
  const run = wmRuntime.get(layer) || {};
  const title = wmEscape(layer.title || "");
  const pick = (name, label, selected) => `<label class="lkind">${label}<select data-act="ruleArg" data-arg="${name}" data-layer="${wmEscape(key)}"
      data-focus="ruleArg:${name}:${wmEscape(key)}" aria-label="${label} of ${title}">${Array.from({ length: bands }, (_, i) =>
        `<option value="${i}"${i === selected ? " selected" : ""}>Band ${i + 1}</option>`).join("")}</select></label>`;
  if (rule.rasterFunction === "ExtractBand") {
    const ids = args.BandIDs || [0, 1, 2];
    return `<div class="ruleargs">${pick("R", "Red", ids[0])}${pick("G", "Green", ids[1])}${pick("B", "Blue", ids[2])}</div>`;
  }
  if (rule.rasterFunction === "NDVI") {
    return `<div class="ruleargs">${pick("VisibleBandID", "Red band", args.VisibleBandID ?? 0)}${pick("InfraredBandID", "Infrared band", args.InfraredBandID ?? 1)}</div>`;
  }
  if (rule.rasterFunction === "BandArithmetic") {
    const error = run.ruleError;
    return `<div class="ruleargs"><label class="lkind">Expression <input type="text" data-act="ruleArg" data-arg="BandIndexes"
      data-layer="${wmEscape(key)}" data-focus="ruleArg:BandIndexes:${wmEscape(key)}" spellcheck="false" aria-label="Expression of ${title}"
      value="${wmEscape(run.ruleDraft ?? args.BandIndexes ?? "")}"${error ? ` aria-invalid="true"` : ""}
      aria-describedby="ruleArgHint-${wmEscape(key)}${error ? ` ruleArgError-${wmEscape(key)}` : ""}"></label>
      <span class="lkind" id="ruleArgHint-${wmEscape(key)}">Use B1–B${bands} with + − * / and ( ). Example: (B${Math.min(4, bands)} − B${Math.min(3, bands - 1) || 1}) / (B${Math.min(4, bands)} + B${Math.min(3, bands - 1) || 1})</span>
      ${error ? `<span class="lkind bad" id="ruleArgError-${wmEscape(key)}">${wmEscape(error)}</span>` : ""}</div>`;
  }
  return "";
}

/** A function's name as the viewer says it. */
function wmFunctionLabel(name) {
  return ({ Hillshade: "Hillshade", Slope: "Slope", Aspect: "Aspect", None: "Value", ExtractBand: "Band combination",
    BandArithmetic: "Band arithmetic", NDVI: "NDVI" })[name] || name;
}

/** What a layer is drawn through now: its own rule, else the service's default, else none. */
function wmShownFunction(layer, info) {
  const asked = (layer.renderingRule || {}).rasterFunction;
  return asked ? (asked === "None" ? null : asked) : (info && info.defaultRasterFunction) || null;
}

/** A function's key under the layer's row: its colours and their ends. */
function wmFunctionKey(fn) {
  if (!fn) return "";
  const key = {
    Hillshade: ["linear-gradient(to right, #000, #fff)", ["Shadow", "Lit"]],
    Slope: ["linear-gradient(to right, #38a800, #a8d400, #ffff00, #ff8000, #ff0000)", ["0°", "15°", "30°", "45°+"]],
    Aspect: ["linear-gradient(to right, #ff0000, #ffa600, #ffff00, #00ff00, #00ffff, #00a6ff, #0000ff, #ff00ff, #ff0000)", ["N", "E", "S", "W", "N"]],
    // ADR-151: a service shown through NDVI by its owner, as Studio stores it, −1 to 1.
    NDVI: ["linear-gradient(to right, #8c510a 0%, #d8b365 45%, #f6e8c3 55%, #a6d96a 70%, #1a9641 85%, #00441b 100%)", ["−1", "0", "1"]],
  }[fn];
  return key ? `<div class="rulekey" aria-hidden="true"><div class="rulebar" style="background:${key[0]}"></div>
    <div class="ruleticks">${key[1].map(t => `<span>${t}</span>`).join("")}</div></div>` : "";
}

/** A function's value as a reader reads it: degrees for slope, a compass word beside an aspect. */
function wmFunctionValue(fn, text) {
  const n = Number(text);
  if (!Number.isFinite(n)) return text === "NoData" ? "none here" : text;
  if (fn === "Slope") return `${n.toLocaleString(undefined, { maximumFractionDigits: 1 })}°`;
  if (fn === "Aspect") {
    const words = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
    return `${Math.round(n)}° (${words[Math.round(n / 45) % 8]})`;
  }
  if (fn === "Hillshade") return `${Math.round(n)} of 255`;
  if (fn === "NDVI") return n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  return n.toLocaleString(undefined, { maximumSignificantDigits: 7 });
}


// ---------------------------------------------------------------- the attribute table (ADR-130)

/** The table under the map: which layer, which page, and whether it follows the map's extent. */
const wmTable = { layer: null, page: 0, total: 0, byExtent: true, turn: 0, rows: [], chosen: -1, selfMove: false, editing: false };
const WM_TABLE_PAGE = 50;

/**
 * Opens a layer's attribute table under the map — ADR-130, as ArcGIS Map Viewer's *Show table*: its features in the
 * map's filter, by default only those in view and following the map as it moves, fifty to a page; a row takes the
 * map to its feature.
 */
function wmOpenTable(layer) {
  wmTable.layer = layer;
  wmTable.page = 0;
  wmTable.chosen = -1;
  wmTable.editing = false;
  wm$("tablePanel").hidden = false;
  wm$("mapWrap").classList.add("withtable");
  wmMap.updateSize();
  wmLoadTable();
  // The heading first, so a screen reader hears whose table this is and how many rows it has.
  wm$("tableTitle").focus();
}

function wmCloseTable() {
  const layer = wmTable.layer;
  wmTable.layer = null;
  wm$("tablePanel").hidden = true;
  wm$("mapWrap").classList.remove("withtable");
  wmMap.updateSize();
  wmHighlight.getSource().clear();
  if (layer) document.querySelector(`[data-act="table"][data-layer="${CSS.escape(layer.id)}"]`)?.focus();
}

async function wmLoadTable() {
  const layer = wmTable.layer;
  if (!layer) return;
  const turn = ++wmTable.turn;
  const info = (wmRuntime.get(layer) || {}).info || {};
  const oid = info.objectIdField || "objectid";
  const box = wmQueryBox(wmMap.getView().calculateExtent(wmMap.getSize()));
  const where = { where: wmWhere(layer), ...wmTimeParam(layer) };
  const spatial = wmTable.byExtent
    ? { geometry: box.join(","), geometryType: "esriGeometryEnvelope", inSR: 3857, spatialRel: "esriSpatialRelIntersects" }
    : {};

  wm$("tableTitle").textContent = layer.title || "Layer";
  wmSayIn("tableStatus", "Reading…");

  try {
    const [counted, page] = await Promise.all([
      wmFetch(`${layer.url}/query?` + wmParams({ ...where, ...spatial, returnCountOnly: true, f: "json" })),
      wmFetch(`${layer.url}/query?` + wmParams({
        ...where, ...spatial, outFields: "*", returnGeometry: true, outSR: 3857, orderByFields: oid,
        resultOffset: wmTable.page * WM_TABLE_PAGE, resultRecordCount: WM_TABLE_PAGE, f: "json",
      })),
    ]);
    if (turn !== wmTable.turn || wmTable.layer !== layer) return;

    wmTable.total = Number(counted && counted.count) || 0;
    wmTable.rows = (page && page.features) || [];
    // The pop-up's choice of fields, when the map has made one — ArcGIS's table hides what the pop-up hides.
    const hidden = new Set(((layer.popupInfo && layer.popupInfo.fieldInfos) || [])
      .filter(f => f.visible === false).map(f => String(f.fieldName).toLowerCase()));
    const fields = (info.fields || []).filter(f => f.type !== "esriFieldTypeGeometry" && !wmSystemField(f.name, info)
      && !hidden.has(String(f.name).toLowerCase()));
    const shownFields = fields.length ? fields
      : Object.keys((wmTable.rows[0] || {}).attributes || {}).map(name => ({ name, alias: name }));

    // ADR-134 condition 3: in edit mode a cell of an editable field is its control, as the card's form makes it.
    const editable = new Set(wmTable.editing ? wmEditFields(info).map(f => f.name) : []);
    const named = feature => {
      const shown = info.displayField && (feature.attributes || {})[info.displayField];
      return shown !== null && shown !== undefined && shown !== "" ? String(shown) : `feature ${(feature.attributes || {})[oid]}`;
    };
    wmTable.keepSaid = false;
    wm$("tableGrid").innerHTML = `<thead><tr>${shownFields.map(f =>
      `<th scope="col"${WM_NUMBER_TYPES.has(f.type) && !(f.domain && f.domain.type === "codedValue") ? ' class="num"' : ""}>${
        wmEscape(f.alias || f.name)}</th>`).join("")}</tr></thead>
      <tbody>${wmTable.rows.map((feature, i) => `<tr tabindex="${!wmTable.editing && i === Math.max(0, wmTable.chosen) ? 0 : -1}" data-row="${i}"
        aria-selected="${i === wmTable.chosen ? "true" : "false"}">${shownFields.map(f => {
        const value = (feature.attributes || {})[f.name];
        const number = WM_NUMBER_TYPES.has(f.type) && !(f.domain && f.domain.type === "codedValue");
        if (editable.has(f.name)) {
          return `<td class="celledit${number ? " num" : ""}">${wmFieldControl(f, value, { row: i, label: `${f.alias || f.name} of ${named(feature)}` })}</td>`;
        }
        const text = value === null || value === undefined ? "" : wmFormatValue(value, null, f.type);
        return `<td title="${wmEscape(text)}"${number ? ' class="num"' : ""}>${wmEscape(text)}</td>`;
      }).join("")}</tr>`).join("")}</tbody>`;
    wm$("tableEdit").hidden = !wmEditable(layer);
    wm$("tableEdit").textContent = wmTable.editing ? "Stop editing" : "Edit in table";
    wm$("tableEdit").setAttribute("aria-pressed", String(wmTable.editing));
    wmTable.named = named;
    wmTableCount();

    const first = wmTable.page * WM_TABLE_PAGE;
    const last = first + wmTable.rows.length;
    const pages = Math.max(1, Math.ceil(wmTable.total / WM_TABLE_PAGE));
    wmTable.countSaid = wmTable.total === 0 ? "" : `${first + 1}–${last} of ${wmTable.total.toLocaleString()}${wmTable.byExtent ? " in view" : ""}${
        wmWhere(layer) !== "1=1" ? ", filtered" : ""}.${pages > 1 ? ` Page ${wmTable.page + 1} of ${pages}.` : ""}`;
    wmSayIn("tableStatus", wmTable.total === 0
      ? (wmTimeNarrowed(layer)
        ? `No feature ${wmTable.byExtent ? "in view " : ""}${wmTimeBetween()}. Widen the time window or choose Show all time.`
        : wmTable.byExtent ? "No feature in view." : "No feature matches this layer's filter.")
      : `${first + 1}–${last} of ${wmTable.total.toLocaleString()}${wmTable.byExtent ? " in view" : ""}${
        wmWhere(layer) !== "1=1" ? ", filtered" : ""}.${pages > 1 ? ` Page ${wmTable.page + 1} of ${pages}.` : ""}`);
    wm$("tablePrev").disabled = wmTable.page === 0;
    wm$("tableNext").disabled = last >= wmTable.total;
  } catch (e) {
    if (turn === wmTable.turn) wmSayIn("tableStatus", `The table could not be read: ${e.message || e}`, true);
  }
}

/** The table's changed cells, by row: what the feature held and what is typed now. */
function wmTableChanges() {
  if (!wmTable.editing || !wmTable.layer) return [];
  const info = (wmRuntime.get(wmTable.layer) || {}).info || {};
  const rows = new Map();
  for (const control of document.querySelectorAll("#tableGrid [data-cell]")) {
    const field = (info.fields || []).find(f => f.name === control.dataset.field);
    const feature = wmTable.rows[Number(control.dataset.cell)];
    if (!field || !feature) continue;
    const was = (feature.attributes || {})[field.name];
    const read = wmReadControl(control, field, was === undefined ? null : was);
    control.closest("td").classList.toggle("changed", !read.same);
    // A cell the server refused stays marked until it is typed in again (design review 2026-10-01).
    control.setAttribute("aria-invalid", read.problem || control.dataset.refused ? "true" : "false");
    control.title = read.problem || control.dataset.refused || "";
    if (read.same) continue;
    const row = rows.get(control.dataset.cell) || { index: Number(control.dataset.cell), changed: {}, problems: [], first: null };
    const of = wmTable.named ? ` of ${wmTable.named(feature)}` : "";
    if (read.problem) { row.problems.push(read.problem.replace(field.alias || field.name, `${field.alias || field.name}${of}`)); row.first = row.first || control; }
    else row.changed[field.name] = read.value;
    rows.set(control.dataset.cell, row);
  }
  return [...rows.values()];
}

/** Says how many rows have changed, and offers Save and Discard while any has. */
function wmTableCount() {
  const changes = wmTableChanges();
  const n = changes.length;
  wm$("tableSave").hidden = !wmTable.editing;
  wm$("tableDiscard").hidden = !wmTable.editing;
  wm$("tableSave").disabled = n === 0;
  wm$("tableDiscard").disabled = n === 0;
  wm$("tableSave").textContent = n ? `Save ${n} ${n === 1 ? "row" : "rows"}` : "Save changes";
}

/**
 * Takes a feature saved from the card into its table row: a cell not typed in shows the saved value, a typed one is
 * compared against it — so Save in the table cannot overwrite the card's save unnoticed (design review 2026-10-01).
 * Whether the row still has typed cells that differ.
 */
function wmTableTakeSaved(layer, oid, changed, name) {
  if (wmTable.layer !== layer || !wmTable.editing) return false;
  const info = (wmRuntime.get(layer) || {}).info || {};
  const index = wmTable.rows.findIndex(f => String((f.attributes || {})[info.objectIdField || "objectid"]) === String(oid));
  if (index < 0) return false;
  const before = { ...wmTable.rows[index].attributes };
  Object.assign(wmTable.rows[index].attributes, changed);
  let clash = false;
  for (const control of document.querySelectorAll(`#tableGrid [data-cell="${index}"]`)) {
    if (!(control.dataset.field in changed)) continue;
    const field = (info.fields || []).find(f => f.name === control.dataset.field);
    const typed = !wmReadControl(control, field, before[field.name] === undefined ? null : before[field.name]).same;
    const shown = changed[field.name] === null ? "" : field.type === "esriFieldTypeDate" ? wmLocalInput(changed[field.name]) : String(changed[field.name]);
    if (typed) clash = true;
    else control.value = shown;
    control.defaultValue = shown;
  }
  wmTableCount();
  if (clash) {
    wmTable.keepSaid = true;
    wmSayIn("tableStatus", `${name} was saved from the card; its table cells still differ. Save the table to replace the card's values, or Discard.`, true);
  }
  return clash;
}

/** Whether the table may be left or moved: asked when cells have changed, rather than dropped. */
function wmMayLeaveTable() {
  return !wmTableChanges().length || confirm("Discard your changes in the table?");
}

function wmToggleTableEdit() {
  if (wmTable.editing && !wmMayLeaveTable()) return;
  wmTable.editing = !wmTable.editing;
  wmLoadTable().then(() => {
    (wmTable.editing ? document.querySelector("#tableGrid [data-cell]") : wm$("tableEdit"))?.focus();
  });
}

/** Ends an edit of the table — after a save, or discarding what was typed — and reads it again. */
async function wmTableEditDone(saved) {
  if (!saved && !confirm("Discard your changes in the table?")) return;
  await wmLoadTable();
  if (!saved) wmSayIn("tableStatus", "Changes discarded.");
  document.querySelector("#tableGrid [data-cell]")?.focus();
}

/** Sends every changed row in one applyEdits, and says which rows the server refused. */
async function wmSaveTable() {
  wmTable.keepSaid = false;
  const layer = wmTable.layer;
  const info = (wmRuntime.get(layer) || {}).info || {};
  const oid = info.objectIdField || "objectid";
  const changes = wmTableChanges();
  const wrong = changes.find(c => c.problems.length);
  if (wrong) {
    wmSayIn("tableStatus", `Not saved: ${wrong.problems[0]}`, true);
    wrong.first.focus();
    return;
  }
  if (!changes.length) return;
  wm$("tableSave").disabled = true;
  wmSayIn("tableStatus", "Saving…");
  try {
    const updates = changes.map(c => ({ attributes: { [oid]: (wmTable.rows[c.index].attributes || {})[oid], ...c.changed } }));
    const answer = await wmFetch(`${layer.url}/applyEdits`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ updates: JSON.stringify(updates), rollbackOnFailure: "false", f: "json" }),
    });
    const refused = ((answer || {}).updateResults || []).filter(r => r.success === false);
    if (refused.length) {
      // The rows that saved take their new values as what they hold; the refused ones stay typed and marked, and the
      // table is not read again over them (design review 2026-10-01: the report was erased within 100 ms).
      const no = new Set(refused.map(r => String(r.objectId)));
      for (const c of changes) {
        const feature = wmTable.rows[c.index];
        const id = String((feature.attributes || {})[oid]);
        const controls = document.querySelectorAll(`#tableGrid [data-cell="${c.index}"]`);
        if (no.has(id)) {
          const why = ((refused.find(r => String(r.objectId) === id) || {}).error || {}).description || "refused";
          controls.forEach(control => { if (control.closest("td").classList.contains("changed")) control.dataset.refused = why; });
          continue;
        }
        Object.assign(feature.attributes, c.changed);
        controls.forEach(control => { control.defaultValue = control.value; });
      }
      const source = (wmRuntime.get(layer) || {}).ol;
      if (source && source.getSource && source.getSource().refresh) source.getSource().refresh();
      wmTableCount();
      wmTable.keepSaid = true;
      wmSayIn("tableStatus", `${updates.length - refused.length} of ${updates.length} saved. Not saved: ${refused.map(r => {
        const c = changes.find(x => String((wmTable.rows[x.index].attributes || {})[oid]) === String(r.objectId));
        return `${c ? wmTable.named(wmTable.rows[c.index]) : `feature ${r.objectId}`} (${((r.error || {}).description) || "refused"})`;
      }).join(", ")}.`, true);
      document.querySelector('#tableGrid [aria-invalid="true"]')?.focus();
      return;
    }
    wmRefreshLayer(layer, true);
    await wmLoadTable();
    wmSayIn("tableStatus", `${updates.length} ${updates.length === 1 ? "row" : "rows"} saved.`);
    document.querySelector("#tableGrid [data-cell]")?.focus();
  } catch (e) {
    wmSayIn("tableStatus", `Not saved: ${e.message || e}`, true);
    wm$("tableSave").disabled = false;
  }
}

wm$("tablePanel").addEventListener("input", event => {
  if (!(event.target instanceof Element && event.target.matches("[data-cell]"))) return;
  delete event.target.dataset.refused;
  wmTableCount();
  // A problem said for a cell goes once the cells have none, and the line says the rows again.
  if (/^Not saved: /.test(wm$("tableStatus").textContent) && !wmTableChanges().some(c => c.problems.length)) {
    wmSayIn("tableStatus", wmTable.countSaid || "");
  }
});
wm$("tablePanel").addEventListener("change", event => {
  if (event.target instanceof Element && event.target.matches("[data-cell]")) wmTableCount();
});

/**
 * Chooses a row: outlines its feature and takes the map to it, and the table stays where it was — the move is the
 * table's own, so it does not start the table over from page one (design review 2026-10-01).
 */
function wmTableGo(index) {
  const feature = wmTable.rows[index];
  if (!feature || !feature.geometry) return;
  wmTable.chosen = index;
  for (const row of document.querySelectorAll("#tableGrid tr[data-row]")) {
    const on = Number(row.dataset.row) === index;
    row.setAttribute("aria-selected", String(on));
    row.tabIndex = on ? 0 : -1;
  }
  document.querySelector(`#tableGrid tr[data-row="${index}"]`)?.focus();
  const read = WM_ESRI.readFeature(feature, { featureProjection: WM_MERCATOR });
  wmHighlight.getSource().clear();
  wmHighlight.getSource().addFeature(read);
  wmTable.selfMove = true;
  wmFit(read.getGeometry().getExtent());
}

wm$("tablePanel").addEventListener("click", event => {
  const t = event.target instanceof Element ? event.target : null;
  if (!t) return;
  if (t.closest("#tableClose") && wmMayLeaveTable()) { wmTable.editing = false; wmCloseTable(); wmDrawLayerList(); return; }
  if (t.closest("#tablePrev") && wmMayLeaveTable()) { wmTable.page = Math.max(0, wmTable.page - 1); wmTable.chosen = -1; wmLoadTable(); return; }
  if (t.closest("#tableNext") && wmMayLeaveTable()) { wmTable.page++; wmTable.chosen = -1; wmLoadTable(); return; }
  if (t.closest("#tableEdit")) { wmToggleTableEdit(); return; }
  if (t.closest("#tableSave")) { wmSaveTable(); return; }
  if (t.closest("#tableDiscard")) { wmTableEditDone(false); return; }
  if (t.closest("input, select, textarea")) return;
  const row = t.closest("tr[data-row]");
  if (row) wmTableGo(Number(row.dataset.row));
});
// One Tab stop for the rows: the arrows move between them, Home and End to the ends, Enter chooses (roving tabindex).
wm$("tablePanel").addEventListener("keydown", event => {
  const cell = event.target instanceof Element && event.target.matches("[data-cell]") ? event.target : null;
  if (event.key === "Escape") {
    event.preventDefault();
    event.stopPropagation();
    // In a cell, Escape puts back what the cell held, as ArcGIS's table does; the table stays.
    if (cell) {
      if (cell.tagName === "SELECT") [...cell.options].forEach(o => { o.selected = o.defaultSelected; });
      else cell.value = cell.defaultValue;
      wmTableCount();
      return;
    }
    if (!wmMayLeaveTable()) return;
    wmTable.editing = false;
    wmCloseTable();
    wmDrawLayerList();
    return;
  }
  // Up and Down in a text cell move to the same field in the row above or below.
  if (cell && (event.key === "ArrowUp" || event.key === "ArrowDown") && cell.tagName === "INPUT" && cell.type === "text") {
    const next = document.querySelector(`#tableGrid [data-field="${CSS.escape(cell.dataset.field)}"][data-cell="${
      Number(cell.dataset.cell) + (event.key === "ArrowDown" ? 1 : -1)}"]`);
    if (next) { event.preventDefault(); next.focus(); next.select?.(); }
    return;
  }
  const row = event.target instanceof Element && event.target.matches("tr[data-row]") ? event.target : null;
  if (!row) return;
  const rows = [...document.querySelectorAll("#tableGrid tr[data-row]")];
  const at = rows.indexOf(row);
  const to = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: rows.length - 1 }[event.key];
  if (to !== undefined) {
    event.preventDefault();
    const next = rows[Math.max(0, Math.min(rows.length - 1, to))];
    rows.forEach(r => { r.tabIndex = r === next ? 0 : -1; });
    next.focus();
    return;
  }
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    wmTableGo(Number(row.dataset.row));
  }
});
wm$("tableByExtent").addEventListener("change", () => {
  if (!wmMayLeaveTable()) {
    wm$("tableByExtent").checked = wmTable.byExtent;
    wmSayIn("tableStatus", wmTable.countSaid || "");
    return;
  }
  wmTable.byExtent = wm$("tableByExtent").checked;
  wmTable.page = 0;
  wmLoadTable();
});
// In view means what is in view now: the table follows the map, from its first page.
// The Labels panel's current scale follows the map.
wmMap.on("moveend", () => {
  if (!wmState.doc) return;
  for (const layer of wmLayers()) {
    const note = wm$(`labScale-${layer.id}`);
    if (note) note.textContent = wmScaleNote(layer);
  }
});

wmMap.on("moveend", () => {
  if (wmTable.selfMove) {
    wmTable.selfMove = false;
    return;
  }
  if (wmTable.layer && wmTable.byExtent) {
    // Unsaved cells are not thrown away because the map moved; the table follows once they are saved or discarded.
    if (wmTableChanges().length) {
      wmSayIn("tableStatus", "The map moved; save or discard your changes to see the features now in view.");
      return;
    }
    wmTable.page = 0;
    wmTable.chosen = -1;
    wmLoadTable();
  }
});


// ---------------------------------------------------------------- the time slider (ADR-132)

/**
 * The map's time window — ADR-132, as ArcGIS Map Viewer's time slider: shown when a layer on the map has time, over
 * the span its layers cover, cut into time stops of a unit that suits the span, and sent to every such layer as
 * `time=start,end` so what is drawn, clicked and tabled is what falls in the window. Kept with the map in the Web Map's
 * own `widgets.timeSlider`.
 */
const wmTime = { span: null, unit: 0, stops: 0, start: 0, end: 0, playing: null };

/** Time-stop units, smallest first, as ArcGIS names them. */
const WM_TIME_UNITS = [
  [60000, "esriTimeUnitsMinutes"], [3600000, "esriTimeUnitsHours"], [86400000, "esriTimeUnitsDays"],
  [7 * 86400000, "esriTimeUnitsWeeks"], [30 * 86400000, "esriTimeUnitsMonths"], [365 * 86400000, "esriTimeUnitsYears"],
];

/** The `time` a layer is asked with: the window, for a layer that has time; nothing otherwise. */
function wmTimeParam(layer) {
  const info = (wmRuntime.get(layer) || {}).info;
  if (!wmTime.span || !info || !info.timeInfo) return {};
  return { time: `${Math.round(wmTimeAt(wmTime.start))},${Math.round(wmTimeAt(wmTime.end))}` };
}

/** Whether the window is narrower than the whole span — what an empty answer should be blamed on. */
function wmTimeNarrowed(layer) {
  return !!(wmTime.span && ((wmRuntime.get(layer) || {}).info || {}).timeInfo && !wmTimeWhole());
}

function wmTimeWhole() {
  return wmTime.start <= 0 && wmTime.end >= wmTime.stops;
}

/** The window in words, for an empty answer: "between … and …". */
function wmTimeBetween() {
  return `between ${wmTimeSaid(wmTimeAt(wmTime.start))} and ${wmTimeSaid(wmTimeAt(wmTime.end))}`;
}

/** The layers with time, loaded. */
function wmTimeLayers() {
  return wmLayers().filter(layer => wmKind(layer) === "feature" && ((wmRuntime.get(layer) || {}).info || {}).timeInfo);
}

/** A stop's moment: the span's start plus so many units, the last stop being the span's end. */
function wmTimeAt(stop) {
  return stop >= wmTime.stops ? wmTime.span[1] : wmTime.span[0] + stop * wmTime.unit;
}

/** A moment as a person reads it: with the time of day when the stops are shorter than a day. */
function wmTimeSaid(ms) {
  const d = new Date(ms);
  return wmTime.unit < 86400000 ? d.toLocaleString() : d.toLocaleDateString();
}

/** Works out the span the map's layers cover and shows the slider, or hides it when none has time. */
function wmDrawTime() {
  const bar = wm$("timeBar");
  if (!bar || !wmState.doc) return;
  let low = Infinity;
  let high = -Infinity;
  for (const layer of wmTimeLayers()) {
    const [from, until] = (wmRuntime.get(layer).info.timeInfo.timeExtent || []);
    if (Number.isFinite(from)) low = Math.min(low, from);
    if (Number.isFinite(until)) high = Math.max(high, until);
  }

  if (!(high > low)) {
    wmStopPlaying();
    wmTime.span = null;
    bar.hidden = true;
    wm$("mapWrap").classList.remove("withtime");
    return;
  }

  const first = !wmTime.span;
  wmTime.span = [low, high];
  // The unit that cuts the span into no more than about two hundred stops.
  const [unit] = WM_TIME_UNITS.find(([ms]) => (high - low) / ms <= 200) || WM_TIME_UNITS[WM_TIME_UNITS.length - 1];
  wmTime.unit = unit;
  wmTime.stops = Math.max(1, Math.ceil((high - low) / unit));
  for (const id of ["timeStart", "timeEnd"]) wm$(id).max = String(wmTime.stops);

  if (first) {
    // The window the map was saved with, or the whole span.
    const saved = (((wmState.doc.widgets || {}).timeSlider || {}).properties || {}).currentTimeExtent;
    const toStop = ms => Math.max(0, Math.min(wmTime.stops, Math.round((ms - low) / unit)));
    [wmTime.start, wmTime.end] = Array.isArray(saved) && saved.every(Number.isFinite)
      ? [toStop(saved[0]), Math.max(toStop(saved[0]), toStop(saved[1]))]
      : [0, wmTime.stops];
  }
  wm$("timeFirst").textContent = wmTimeSaid(low);
  wm$("timeLast").textContent = wmTimeSaid(high);
  bar.hidden = false;
  wm$("mapWrap").classList.add("withtime");
  wmDrawTimeControls();
}

function wmDrawTimeControls() {
  if (!wmTime.span) return;
  const start = wm$("timeStart");
  const end = wm$("timeEnd");
  start.value = String(wmTime.start);
  end.value = String(wmTime.end);
  start.setAttribute("aria-valuetext", wmTimeSaid(wmTimeAt(wmTime.start)));
  end.setAttribute("aria-valuetext", wmTimeSaid(wmTimeAt(wmTime.end)));
  // One track: the window is the part between the thumbs.
  const at = v => `${(v / wmTime.stops) * 100}%`;
  wm$("timeTrack").style.setProperty("--from", at(wmTime.start));
  wm$("timeTrack").style.setProperty("--to", at(wmTime.end));
  wm$("timeSaid").textContent = wmTimeWhole() ? "All time"
    : `${wmTimeSaid(wmTimeAt(wmTime.start))} – ${wmTimeSaid(wmTimeAt(wmTime.end))}`;
  wm$("timePlay").textContent = wmTime.playing ? "Pause" : "Play";
}

/** Says the window to a screen reader once a move is over, not at every step of it. */
function wmAnnounceTime() {
  wm$("timeAnnounce").textContent = wmTimeWhole() ? "Time window: all time."
    : `Time window: ${wmTimeSaid(wmTimeAt(wmTime.start))} to ${wmTimeSaid(wmTimeAt(wmTime.end))}.`;
}

/** Asks every layer with time again for the window, and the open table with it; the open card is closed. */
let wmTimeRefresh = null;
function wmApplyTime() {
  wmDrawTimeControls();
  clearTimeout(wmTimeRefresh);
  wmTimeRefresh = setTimeout(() => {
    for (const layer of wmTimeLayers()) {
      const run = wmRuntime.get(layer);
      const source = run.ol && run.ol.getSource && run.ol.getSource();
      if (source && source.refresh) source.refresh();
    }
    if (wmTable.layer && wmTimeLayers().includes(wmTable.layer)) {
      wmTable.page = 0;
      wmLoadTable();
    }
    // A card about a feature the window has just hidden would answer a question nobody is asking now.
    if (!wm$("identify").hidden) {
      wm$("identify").hidden = true;
      wmHighlight.getSource().clear();
    }
    wmDrawLayerList();
  }, 150);
}

/** Keeps the window in the map's document — once a move is over, not at every step. */
function wmKeepTime() {
  const widgets = (wmState.doc.widgets = wmState.doc.widgets || {});
  widgets.timeSlider = {
    properties: {
      startTime: wmTime.span[0], endTime: wmTime.span[1], thumbCount: 2, thumbMovingRate: 2000,
      timeStopInterval: { interval: 1, units: (WM_TIME_UNITS.find(([ms]) => ms === wmTime.unit) || [0, "esriTimeUnitsDays"])[1] },
      currentTimeExtent: [Math.round(wmTimeAt(wmTime.start)), Math.round(wmTimeAt(wmTime.end))],
    },
  };
  wmMarkDirty();
}

function wmStopPlaying() {
  if (wmTime.playing) clearInterval(wmTime.playing);
  wmTime.playing = null;
}

/** Moves the window by whole stops, keeping its width; the step buttons and Play. */
function wmTimeShift(by) {
  const width = wmTime.end - wmTime.start;
  const start = Math.max(0, Math.min(wmTime.stops - width, wmTime.start + by));
  wmTime.start = start;
  wmTime.end = start + width;
}

for (const id of ["timeStart", "timeEnd"]) {
  wm$(id).addEventListener("input", () => {
    // The reader has taken the window: Play lets go of it.
    if (wmTime.playing) wmStopPlaying();
    let a = Number(wm$("timeStart").value);
    let b = Number(wm$("timeEnd").value);
    // The thumbs do not cross: the one moved stops at the other.
    if (a > b) {
      if (id === "timeStart") a = b; else b = a;
    }
    wmTime.start = a;
    wmTime.end = b;
    wmApplyTime();
  });
  wm$(id).addEventListener("change", () => {
    wmKeepTime();
    wmAnnounceTime();
  });
}

wm$("timeBar").addEventListener("click", event => {
  const t = event.target instanceof Element ? event.target.closest("button") : null;
  if (!t || !wmTime.span) return;

  if (t.id === "timePlay") {
    if (wmTime.playing) {
      wmStopPlaying();
      wmDrawTimeControls();
      wmKeepTime();
      wmAnnounceTime();
      return;
    }
    // From the whole span, Play walks a window of a twentieth of it from the start; a window already at the end
    // starts again from the beginning (design review 2026-10-01: Play from the whole span did nothing).
    if (wmTimeWhole()) {
      wmTime.start = 0;
      wmTime.end = Math.max(1, Math.round(wmTime.stops / 20));
    } else if (wmTime.end >= wmTime.stops) {
      wmTimeShift(-wmTime.stops);
    }
    wmTime.playing = setInterval(() => {
      if (!wmTime.span || wmTime.end >= wmTime.stops) {
        wmStopPlaying();
        wmDrawTimeControls();
        if (wmTime.span) { wmKeepTime(); wmAnnounceTime(); }
        return;
      }
      wmTimeShift(1);
      wmApplyTime();
    }, 2000);
    wmApplyTime();
    return;
  }

  if (t.id === "timeBack" || t.id === "timeForward") {
    wmStopPlaying();
    wmTimeShift(t.id === "timeBack" ? -1 : 1);
    wmApplyTime();
    wmKeepTime();
    wmAnnounceTime();
    return;
  }

  if (t.id === "timeAll") {
    wmStopPlaying();
    wmTime.start = 0;
    wmTime.end = wmTime.stops;
    wmApplyTime();
    wmKeepTime();
    wmAnnounceTime();
  }
});


// ---------------------------------------------------------------- print (ADR-133)

/**
 * The map as a page — ADR-133, as ArcGIS Map Viewer's Print: the area in view drawn again at the page's own resolution
 * (150 dpi on A4, as OpenLayers' export example does — a screenshot stretched to the page was blurred and its scale
 * false), with a title, a legend of what is drawn class by class, a scale bar and the true scale of the paper, a north
 * arrow, the date, the time window when there is one, and the credits. Downloaded as a PNG, or opened as a page to
 * print or save as PDF from the browser.
 */
const WM_PRINT_DPI = 150;

/** The legend rows of one layer: one per class, each with its label and colour, or one for a single symbol. */
function wmLegendRows(layer) {
  const run = wmRuntime.get(layer) || {};
  const renderer = ((layer.layerDefinition || {}).drawingInfo || {}).renderer
    || (((run.info || {}).drawingInfo || {}).renderer);
  // An image is listed by what it is drawn through (ADR-136), its function's ends as its classes.
  if (wmKind(layer) === "imagery") {
    const fn = wmShownFunction(layer, run.info);
    return fn === "Slope" ? [{ label: "Slope, 0° to 45° and steeper", colour: null, shape: "ramp:slope" }]
      : fn === "Aspect" ? [{ label: "Aspect: N, E, S, W, N left to right", colour: null, shape: "ramp:aspect" }]
      : fn === "Hillshade" ? [{ label: "Hillshade, shadow to lit", colour: null, shape: "ramp:hillshade" }]
      : [{ label: "", colour: "#9aa5a0", shape: "area" }];
  }
  const geometry = String((run.info || {}).geometryType || "");
  const shape = /Point/.test(geometry) ? "point" : /Polyline/.test(geometry) ? "line" : "area";
  if (!renderer || renderer.type === "simple") {
    return [{ label: "", colour: (renderer && wmSymbolColour(renderer.symbol)) || (run.swatches || [])[0], shape }];
  }
  if (renderer.type === "heatmap") return [{ label: "Density, low to high", colour: null, shape: "heat" }];
  const classes = renderer.type === "uniqueValue" ? renderer.uniqueValueInfos || []
    : renderer.type === "classBreaks" ? renderer.classBreakInfos || [] : [];
  return classes.map(entry => ({ label: String(entry.label ?? entry.value ?? ""), colour: wmSymbolColour(entry.symbol), shape }));
}

/** Draws a legend swatch: a circle for points, a stroke for lines, a filled and outlined square for areas. */
function wmDrawSwatch(c, shape, colour, x, y) {
  c.save();
  c.fillStyle = colour || "#cccccc";
  c.strokeStyle = "#576a66";
  c.lineWidth = 1.5;
  if (shape === "point") {
    c.beginPath(); c.arc(x + 11, y + 11, 8, 0, Math.PI * 2); c.fill(); c.stroke();
  } else if (shape === "line") {
    c.strokeStyle = colour || "#576a66"; c.lineWidth = 4;
    c.beginPath(); c.moveTo(x, y + 11); c.lineTo(x + 22, y + 11); c.stroke();
  } else if (String(shape).startsWith("ramp:")) {
    // A raster function's continuous colours, as its key on screen draws them (ADR-136).
    const stops = { slope: ["#38a800", "#a8d400", "#ffff00", "#ff8000", "#ff0000"],
      aspect: ["#ff0000", "#ffa600", "#ffff00", "#00ff00", "#00ffff", "#00a6ff", "#0000ff", "#ff00ff", "#ff0000"],
      hillshade: ["#000000", "#ffffff"] }[shape.slice(5)] || ["#000", "#fff"];
    const g = c.createLinearGradient(x, 0, x + 22, 0);
    stops.forEach((colour, i) => g.addColorStop(i / (stops.length - 1), colour));
    c.fillStyle = g; c.fillRect(x, y, 22, 22); c.strokeRect(x, y, 22, 22);
  } else if (shape === "heat") {
    const g = c.createLinearGradient(x, 0, x + 22, 0);
    g.addColorStop(0, "rgb(144,161,190)"); g.addColorStop(0.5, "rgb(250,197,113)"); g.addColorStop(1, "rgb(255,84,35)");
    c.fillStyle = g; c.fillRect(x, y, 22, 22);
  } else {
    c.fillRect(x, y, 22, 22); c.strokeRect(x, y, 22, 22);
  }
  c.restore();
}

/** Text cut to a width with an ellipsis, measured, rather than squeezed by the canvas. */
function wmEllipsize(c, text, width) {
  if (c.measureText(text).width <= width) return text;
  let t = text;
  while (t.length > 1 && c.measureText(`${t}…`).width > width) t = t.slice(0, -1);
  return `${t}…`;
}

/** Text over at most two lines of a width, the second ellipsized. */
function wmWrap(c, text, width) {
  if (c.measureText(text).width <= width) return [text];
  const words = text.split(/\s+/);
  let first = "";
  while (words.length && c.measureText(`${first} ${words[0]}`.trim()).width <= width) first = `${first} ${words.shift()}`.trim();
  return first ? [first, wmEllipsize(c, words.join(" "), width)] : [wmEllipsize(c, text, width)];
}

/** A round length for a scale bar: 1, 2 or 5 times a power of ten, no longer than `most` metres. */
function wmNiceLength(most) {
  const power = Math.pow(10, Math.floor(Math.log10(most)));
  return [5, 2, 1].map(n => n * power).find(n => n <= most) || power;
}

/**
 * Draws the area in view at a size, and gives back the picture and the ground metres a pixel of it covers at its
 * centre. The map is set to that size and resolution for one render, then put back as it was.
 */
async function wmRenderAt(width, height) {
  const view = wmMap.getView();
  const size = wmMap.getSize();
  const resolution = view.getResolution();
  const extent = view.calculateExtent(size);
  const printed = Math.max((extent[2] - extent[0]) / width, (extent[3] - extent[1]) / height);
  try {
    await new Promise(done => {
      const timer = setTimeout(done, 8000);
      wmMap.once("rendercomplete", () => { clearTimeout(timer); done(); });
      wmMap.setSize([width, height]);
      view.setResolution(printed);
      wmMap.renderSync();
    });
    const picture = wmComposeMap();
    const metres = ol.proj.getPointResolution(view.getProjection(), printed, view.getCenter(), "m");
    return { picture, metres };
  } finally {
    wmMap.setSize(size);
    view.setResolution(resolution);
  }
}

async function wmComposePage() {
  if (!wmMap.getSize()) return null;
  const portrait = wm$("printLayout").value === "portrait";
  const [W, H] = portrait ? [1240, 1754] : [1754, 1240];
  const pad = 48;
  const page = document.createElement("canvas");
  page.width = W;
  page.height = H;
  const c = page.getContext("2d");
  c.fillStyle = "#ffffff";
  c.fillRect(0, 0, W, H);

  const title = wm$("printTitle").value.trim() || wmState.meta.title || "Map";
  c.fillStyle = "#0f1e1b";
  c.font = "600 44px sans-serif";
  const titleLines = wmWrap(c, title, W - 2 * pad);
  titleLines.forEach((line, i) => c.fillText(line, pad, pad + 40 + i * 52));
  const top = pad + 40 + titleLines.length * 52;

  // What the legend lists: layers switched on that draw something in view, top of the map first, class by class.
  const extent = wmMap.getView().calculateExtent(wmMap.getSize());
  const shown = wmLayers().slice().reverse().filter(layer => {
    const run = wmRuntime.get(layer) || {};
    if (layer.visibility === false || run.status !== "ok") return false;
    const source = wmKind(layer) === "feature" && run.ol && run.ol.getSource && run.ol.getSource();
    return !source || !source.getFeaturesInExtent || source.getFeaturesInExtent(extent).length > 0;
  });
  const rows = shown.flatMap(layer => {
    const classes = wmLegendRows(layer);
    return classes.length === 1 && !classes[0].label
      ? [{ ...classes[0], label: layer.title || "Layer", heading: false }]
      : [{ label: layer.title || "Layer", heading: true }, ...classes.map(r => ({ ...r, indent: true }))];
  });

  const footer = 124;
  const legendW = portrait ? W - 2 * pad : 420;
  const rowH = 32;
  const legendRowsFit = portrait ? Math.min(rows.length, 12) : Math.floor((H - top - footer - 40) / rowH);
  const legendH = portrait ? 48 + Math.min(rows.length, legendRowsFit) * rowH : 0;
  const box = portrait
    ? { x: pad, y: top, w: W - 2 * pad, h: H - top - footer - legendH - 24 }
    : { x: pad, y: top, w: W - 3 * pad - legendW, h: H - top - footer };

  // The area in view, drawn again at the paper's pixels.
  const { picture, metres } = await wmRenderAt(Math.round(box.w), Math.round(box.h));
  if (!picture) return null;
  c.drawImage(picture, box.x, box.y, box.w, box.h);
  c.strokeStyle = "#d7e0dc";
  c.lineWidth = 2;
  c.strokeRect(box.x, box.y, box.w, box.h);

  // A north arrow in the map's corner: Web Mercator's north is up.
  const nx = box.x + box.w - 44;
  const ny = box.y + 20;
  c.fillStyle = "rgba(255,255,255,.85)";
  c.fillRect(nx - 18, ny - 8, 40, 70);
  c.fillStyle = "#0f1e1b";
  c.beginPath(); c.moveTo(nx + 2, ny); c.lineTo(nx + 14, ny + 36); c.lineTo(nx + 2, ny + 28); c.lineTo(nx - 10, ny + 36); c.closePath(); c.fill();
  c.font = "600 18px sans-serif";
  c.fillText("N", nx - 4, ny + 56);

  // The legend.
  const lx = portrait ? pad : box.x + box.w + pad;
  let ly = portrait ? box.y + box.h + 40 : top + 24;
  c.fillStyle = "#0f1e1b";
  c.font = "600 26px sans-serif";
  c.fillText("Legend", lx, ly);
  ly += 12;
  c.font = "21px sans-serif";
  const drawn = rows.slice(0, legendRowsFit);
  for (const row of drawn) {
    ly += rowH;
    const x = lx + (row.indent ? 24 : 0);
    if (row.heading) {
      c.font = "600 21px sans-serif";
      c.fillStyle = "#0f1e1b";
      c.fillText(wmEllipsize(c, row.label, legendW - 8), x, ly);
      c.font = "21px sans-serif";
      continue;
    }
    wmDrawSwatch(c, row.shape, row.colour, x, ly - 18);
    c.fillStyle = "#0f1e1b";
    c.fillText(wmEllipsize(c, row.label, legendW - (x - lx) - 40), x + 34, ly);
  }
  if (rows.length > drawn.length) {
    ly += rowH;
    c.fillStyle = "#576a66";
    c.fillText(`+${rows.length - drawn.length} more`, lx, ly);
  }

  // The scale bar and the paper's own scale: ground metres a page pixel covers, over the page pixel's length.
  const barMetres = wmNiceLength(metres * box.w / 4);
  const barPx = barMetres / metres;
  const by = H - pad - 52;
  c.fillStyle = "#0f1e1b";
  c.fillRect(pad, by, barPx, 6);
  c.fillRect(pad, by - 8, 2, 14);
  c.fillRect(pad + barPx - 2, by - 8, 2, 14);
  c.font = "18px sans-serif";
  c.fillText(barMetres >= 1000 ? `${barMetres / 1000} km` : `${barMetres} m`, pad + barPx + 10, by + 6);
  const scale = Math.round(metres / (0.0254 / WM_PRINT_DPI));

  const parts = [`Scale 1:${scale.toLocaleString()} on A4`, `Printed ${new Date().toLocaleDateString(undefined, { dateStyle: "long" })}`];
  if (wmTime.span && !wmTimeWhole()) parts.push(`Time ${wmTimeSaid(wmTimeAt(wmTime.start))} – ${wmTimeSaid(wmTimeAt(wmTime.end))}`);
  const credits = [...new Set(shown.map(l => ((wmRuntime.get(l) || {}).info || {}).copyrightText).filter(Boolean))];
  const ground = ((wmState.doc.baseMap || {}).title || "");
  if (/openstreetmap/i.test(ground) || wm$("mapBasemap").value === "osm") credits.push("Map data © OpenStreetMap contributors");
  c.fillStyle = "#576a66";
  c.font = "20px sans-serif";
  c.fillText(wmEllipsize(c, parts.join("   ·   "), W - 2 * pad), pad, H - pad);
  if (credits.length) c.fillText(wmEllipsize(c, credits.join(" · "), W - 2 * pad), pad, H - pad + 26 > H - 8 ? H - 8 : H - pad + 26);
  return { page, title, scale };
}

/** A file name from the title: letters, digits and dashes, cut before the dashes are trimmed. */
function wmPrintName(title) {
  return (title.replace(/[^\p{L}\p{N}]+/gu, "-").slice(0, 60).replace(/^-+|-+$/g, "") || "map");
}

wm$("printPng").addEventListener("click", async () => {
  const button = wm$("printPng");
  button.disabled = true;
  wmSayIn("printStatus", "Drawing the page…");
  try {
    const made = await wmComposePage();
    if (!made) { wmSayIn("printStatus", "The map has no size yet; try again once it has drawn.", true); return; }
    const blob = await new Promise(done => made.page.toBlob(done, "image/png"));
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = `${wmPrintName(made.title)}.png`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(link.href), 10000);
    wmSayIn("printStatus", `${link.download} downloaded, at 1:${made.scale.toLocaleString()} on A4.`);
  } catch (e) {
    wmSayIn("printStatus", `The map could not be made into a picture: ${e.message || e}`, true);
  } finally {
    button.disabled = false;
  }
});

wm$("printPdf").addEventListener("click", async () => {
  // The page is opened before the drawing, while the click still allows a new window.
  const sheet = window.open("", "_blank");
  if (!sheet) { wmSayIn("printStatus", "The browser blocked the print page. Allow pop-ups for this site, or download the PNG.", true); return; }
  sheet.document.write("<!doctype html><title>Preparing the print…</title><p style=\"font:16px sans-serif\">Preparing the print…</p>");
  wmSayIn("printStatus", "Drawing the page…");
  try {
    const made = await wmComposePage();
    if (!made) { sheet.close(); wmSayIn("printStatus", "The map has no size yet; try again once it has drawn.", true); return; }
    const portrait = wm$("printLayout").value === "portrait";
    sheet.document.open();
    sheet.document.write(`<!doctype html><html><head><meta charset="utf-8"><title>${wmEscape(made.title)}</title>
      <style>@page { size: A4 ${portrait ? "portrait" : "landscape"}; margin: 0; } html, body { margin: 0; }
      img { width: 100%; height: auto; display: block; }</style></head>
      <body><img alt="${wmEscape(made.title)}" src="${made.page.toDataURL("image/png")}"></body></html>`);
    sheet.document.close();
    sheet.onafterprint = () => sheet.close();
    sheet.onload = () => { sheet.focus(); sheet.print(); };
    wmSayIn("printStatus", "Print page opened. To get a PDF, choose Save as PDF as the printer.");
  } catch (e) {
    sheet.close();
    wmSayIn("printStatus", `The map could not be printed: ${e.message || e}`, true);
  }
});


// ---------------------------------------------------------------- bookmarks (ADR-130)

/** The map's bookmarks — the Web Map's own `bookmarks`, each a name and an extent. */
function wmBookmarks() {
  if (!Array.isArray(wmState.doc.bookmarks)) wmState.doc.bookmarks = [];
  return wmState.doc.bookmarks;
}

function wmDrawBookmarks() {
  const list = wm$("bookmarkList");
  if (!list || !wmState.doc) return;
  const marks = Array.isArray(wmState.doc.bookmarks) ? wmState.doc.bookmarks : [];
  list.innerHTML = marks.length
    ? marks.map((mark, i) => `<li><button type="button" class="linkbtn" data-bookmark="${i}">${wmEscape(mark.name || "Bookmark")}</button>
        <button type="button" class="tiny ghost" data-unbookmark="${i}" aria-label="Remove the bookmark ${wmEscape(mark.name || "")}">Remove</button></li>`).join("")
    : `<li class="hint">No bookmarks yet. Move the map to a place, name it below and add it. Saving the map keeps its bookmarks.</li>`;
}

wm$("bookmarkName").addEventListener("keydown", event => {
  if (event.key === "Enter") {
    event.preventDefault();
    wm$("bookmarkAdd").click();
  }
});

wm$("bookmarkAdd").addEventListener("click", () => {
  const name = wm$("bookmarkName").value.trim();
  if (!name) {
    wmSayIn("bookmarkStatus", "Give the bookmark a name.", true);
    wm$("bookmarkName").focus();
    return;
  }
  const box = wmQueryBox(wmMap.getView().calculateExtent(wmMap.getSize()));
  wmBookmarks().push({
    name,
    extent: { xmin: box[0], ymin: box[1], xmax: box[2], ymax: box[3], spatialReference: { wkid: 102100, latestWkid: 3857 } },
  });
  wm$("bookmarkName").value = "";
  wmDrawBookmarks();
  wmMarkDirty();
  wmSayIn("bookmarkStatus", `“${name}” added. Save the map to keep it.`);
});

wm$("bookmarkList").addEventListener("click", event => {
  const t = event.target instanceof Element ? event.target.closest("button") : null;
  if (!t) return;
  const marks = wmBookmarks();
  if (t.dataset.bookmark !== undefined) {
    const e = (marks[Number(t.dataset.bookmark)] || {}).extent;
    if (!e) return;
    const reference = e.spatialReference || {};
    const wkid = reference.latestWkid || reference.wkid || 102100;
    try {
      wmFit(ol.proj.transformExtent([e.xmin, e.ymin, e.xmax, e.ymax],
        wkid === 102100 || wkid === 3857 ? WM_MERCATOR : `EPSG:${wkid}`, WM_MERCATOR), 0, 0);
    } catch {
      wmSayIn("bookmarkStatus", "This bookmark is in a reference this page cannot draw.", true);
    }
    return;
  }
  if (t.dataset.unbookmark !== undefined) {
    const at = Number(t.dataset.unbookmark);
    const [gone] = marks.splice(at, 1);
    wmDrawBookmarks();
    wmMarkDirty();
    wmSayIn("bookmarkStatus", `“${gone ? gone.name : "The bookmark"}” removed. Save the map to keep the change.`);
    // To the bookmark that took its place, or the one before; the name box when none is left.
    (document.querySelector(`[data-bookmark="${Math.min(at, marks.length - 1)}"]`) || wm$("bookmarkName")).focus();
  }
});


// ---------------------------------------------------------------- heat maps (ADR-121)

/** The colour stops a heat map is written with: transparent where sparse, through blue and yellow to red. */
const WM_HEAT_STOPS = [
  [0, [133, 193, 200, 0]], [0.01, [144, 161, 190, 0]], [0.0925, [144, 161, 190, 180]], [0.17875, [162, 145, 192, 210]],
  [0.265, [176, 150, 175, 200]], [0.53, [250, 197, 113, 230]], [0.795, [252, 155, 85, 245]], [1, [255, 84, 35, 255]],
];

/**
 * A heat map over the layer's source, from ArcGIS's `heatmap` renderer — its blur radius and its colour stops; the
 * weight field, when there is one, scaled by the largest value drawn.
 */
function wmHeatLayer(source, renderer) {
  const radius = Math.max(2, Number(renderer.blurRadius) || 24);
  const stops = Array.isArray(renderer.colorStops) && renderer.colorStops.length
    ? renderer.colorStops.map(s => [Number(s.ratio), s.color])
    : WM_HEAT_STOPS;
  // OpenLayers takes an evenly spaced gradient, so the stops are sampled at ten even ratios.
  const gradient = Array.from({ length: 10 }, (_, i) => {
    const at = i / 9;
    const next = stops.findIndex(([r]) => r >= at);
    const [r1, c1] = stops[Math.max(0, next)] || stops[stops.length - 1];
    const [r0, c0] = stops[Math.max(0, next - 1)] || stops[0];
    const t = r1 > r0 ? (at - r0) / (r1 - r0) : 0;
    const mix = j => Math.round(c0[j] + (c1[j] - c0[j]) * t);
    return `rgba(${mix(0)}, ${mix(1)}, ${mix(2)}, ${(mix(3) / 255).toFixed(3)})`;
  });
  let largest = 1;
  if (renderer.field) {
    source.on("change", () => {
      largest = Math.max(1, ...source.getFeatures().map(f => Number(f.get(renderer.field)) || 0));
    });
  }
  return new ol.layer.Heatmap({
    source,
    radius: Math.round(radius * 0.6),
    blur: radius,
    gradient,
    weight: renderer.field ? f => Math.max(0, Math.min(1, (Number(f.get(renderer.field)) || 0) / largest)) : () => 1,
  });
}


/** The field a pop-up's image comes from — `mediaInfos[0]` of type image whose address is one `{field}` — or null. */
function wmPopupImageField(popup) {
  const media = popup && Array.isArray(popup.mediaInfos) ? popup.mediaInfos.find(m => m && m.type === "image") : null;
  const url = media && media.value && media.value.sourceURL;
  const m = /^\s*\{([^}]+)\}\s*$/.exec(url || "");
  return m ? m[1].trim() : null;
}


// ADR-122: the pop-up panel shows the part its content mode uses, and typing text chooses Text.
wm$("layerList").addEventListener("change", event => {
  const t = event.target;
  if (!(t instanceof HTMLInputElement) || !/^popMode-/.test(t.name)) return;
  const panel = t.closest(".lstyle");
  if (!panel) return;
  panel.classList.toggle("popmode-text", t.value === "text");
  panel.classList.toggle("popmode-fields", t.value !== "text");
});
wm$("layerList").addEventListener("input", event => {
  const t = event.target;
  if (!(t instanceof HTMLTextAreaElement) || t.dataset.popText === undefined || !t.value) return;
  const panel = t.closest(".lstyle");
  const text = panel && panel.querySelector(`input[name="popMode-${CSS.escape(t.dataset.popText)}"][value="text"]`);
  if (text && !text.checked) {
    text.checked = true;
    panel.classList.add("popmode-text");
    panel.classList.remove("popmode-fields");
  }
});
