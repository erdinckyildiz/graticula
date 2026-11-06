// The scene viewer — ADR-269, and Scene Viewer's tools since ADR-270. A web scene kept as a portal item, drawn by the
// ArcGIS Maps SDK for JavaScript (ADR-253).
//
// The portal is this server under /studio: the SDK reads the item and its layers from there, and the server asked
// through /studio names /studio as its owner (ADR-191's 2026-11-05 note), so a private layer opens with the reader's
// session rather than a sign-in dialog. Requests carry the browser's session cookie; the SDK's own sign-in dialog is
// turned off, as map.js turns it off, and a refusal is said on this page.
//
// <b>What is Studio's and what is the SDK's — ADR-270.</b> The panel (layers, adding, basemap, ground, slides) is this
// page's own HTML in Studio's style, because it edits the web scene and its state is what the tests read; the legend and
// the analysis tools are the SDK's widgets, which draw into the scene itself, and are loaded the first time they are
// asked for.
"use strict";

const scQuery = new URLSearchParams(location.search);
const SC_PORTAL = `${location.origin}/studio`;
const SC_REST = `${SC_PORTAL}/sharing/rest`;

const sc$ = id => document.getElementById(id);
const scState = { id: scQuery.get("id"), scene: null, view: null, user: null, manages: true, dirty: false, saving: false,
  open: new Set(), tool: null, tools: {}, playing: null, gallery: [] };

/** Says a sentence in the page's live status line; `bad` marks a failure. */
function scSay(text, bad = false) {
  const status = sc$("status");
  status.textContent = text;
  status.style.color = bad ? "var(--alert)" : "";
}

function scEscape(value) {
  return String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" })[c]);
}

/** Whether this reader may change the scene: signed in, and its owner (or a new scene of theirs). */
/** The embed view (ADR-275): another site frames the scene, and nothing in it changes the scene. */
const SC_EMBED = document.body.classList.contains("embed");
if (SC_EMBED) {
  // The scene's own page, for whoever wants more than the frame shows.
  const asked = new URLSearchParams(location.search);
  document.getElementById("embedOpen").href = `/studio/scene.html${asked.get("id") ? `?id=${encodeURIComponent(asked.get("id"))}` : ""}${location.hash}`;
}

function scCanEdit() {
  return !SC_EMBED && !!scState.user && scState.manages;
}

// A GET carries the session cookie on its own; a write needs the bearer token the console keeps in sessionStorage,
// because the server honours the cookie for GET and HEAD only (ADR-023 §4c) — webmap.js's rule, and its exchange.
let scToken = (() => {
  try { return sessionStorage.getItem("gis-token"); } catch { return null; }
})();

/** Trades the browsing cookie for a token of this tab's own, as webmap.js does. */
async function scExchangeSession() {
  try {
    const response = await fetch("/rest/auth/session", { method: "POST", credentials: "same-origin" });
    if (!response.ok) return false;
    const body = await response.json();
    if (!body || typeof body.token !== "string" || !body.token) return false;
    scToken = body.token;
    try { sessionStorage.setItem("gis-token", scToken); } catch { /* private mode: this tab keeps it */ }
    return true;
  } catch {
    return false;
  }
}

/** A portal request, answered as JSON; a portal error is thrown as one. */
async function scPortal(path, form) {
  const headers = scToken ? { Authorization: "Bearer " + scToken } : {};
  const options = form
    ? { method: "POST", body: new URLSearchParams({ ...form, f: "json" }), credentials: "same-origin", headers }
    : { credentials: "same-origin", headers };
  const response = await fetch(`${SC_REST}${path}${form ? "" : (path.includes("?") ? "&" : "?") + "f=json"}`, options);
  const body = await response.json().catch(() => ({}));
  if (!response.ok || body.error) {
    const error = new Error((body.error && body.error.message) || `The portal answered ${response.status}.`);
    error.status = (body.error && body.error.code) || response.status;
    throw error;
  }
  return body;
}

function scLoadSdk() {
  return new Promise((resolve, reject) => {
    if (typeof require === "function") { resolve(); return; }
    const url = window.GRATICULA_MAP_SDK;
    if (!url) { reject(new Error("this page did not receive the map SDK's address from the server (surface.js)")); return; }
    const css = document.createElement("link");
    css.rel = "stylesheet";
    css.href = url + `esri/themes/${document.documentElement.dataset.theme === "dark" ? "dark" : "light"}/main.css`;
    document.head.append(css);
    const script = document.createElement("script");
    script.src = url;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error("it could not be fetched from " + url));
    document.head.append(script);
  });
}

/**
 * Loads SDK modules by name. <b>A plain callback, not an async one</b>: the SDK's module loader does not call an async
 * callback at all — it reads `[object AsyncFunction]` as not a function (2026-11-05).
 */
function scRequire(names) {
  return new Promise((resolve, reject) => require(names, (...loaded) => resolve(loaded), reject));
}

/** What a layer is, said as Studio says it. */
function scKind(layer) {
  // A scene layer is 3D objects or points (D-386); the SDK gives both the type "scene".
  if (layer.type === "scene" && layer.geometryType === "point") return "Points";
  return ({ "feature": "Features", "scene": "3D objects", "point-cloud": "Point cloud", "building-scene": "Buildings",
    "integrated-mesh": "Mesh", "imagery": "Imagery", "imagery-tile": "Imagery", "map-image": "Map image",
    "vector-tile": "Vector tiles", "elevation": "Elevation", "voxel": "Voxels", "csv": "CSV points", "geojson": "GeoJSON" })[layer.type] || layer.type;
}

function scMarkDirty() {
  if (!scCanEdit()) return;
  scState.dirty = true;
  scDrawSaved();
}

function scDrawSaved() {
  const saved = sc$("saved");
  saved.textContent = scState.dirty ? "Not saved" : scState.id ? "Saved" : "";
  saved.className = scState.dirty ? "dirty" : "";
  const button = sc$("headSave");
  button.hidden = !scCanEdit();
  // Bright only when there is something to save: a saved, unchanged scene's Save read as a change waiting (review).
  button.classList.toggle("primary", scState.dirty || !scState.id);
  button.disabled = scState.saving
    || (!scState.id && !sc$("sceneTitle").value.trim() && (!scState.scene || scState.scene.layers.length === 0));
  button.title = button.disabled && !scState.saving ? "Add a layer or name the scene to save it" : "";
}

// ---------------------------------------------------------------------------------------------------------------------
// The panel's tabs.

function scShowTab(name) {
  for (const tab of document.querySelectorAll("#tabs [role=tab]")) {
    const on = tab.id === `tab-${name}`;
    tab.setAttribute("aria-selected", on ? "true" : "false");
    tab.tabIndex = on ? 0 : -1;
    sc$(tab.getAttribute("aria-controls")).hidden = !on;
    // Seven tabs do not fit 340 px: the chosen one is scrolled into the row (design review 2026-11-07).
    if (on) tab.scrollIntoView({ block: "nearest", inline: "nearest" });
  }
  scTabsFade();
  if (name === "legend") scEnsureLegend();
  if (name === "add" && sc$("addInView").checked) scSearchAdd();
  // Leaving the tools lets a run go on; its end is said in the header and its result still joins the scene.
  if (name !== "analysis" && scState.analysisOpen) { scState.analysisOpen = false; window.GraticulaAnalysis?.closed(); }
  if (name === "analysis") scDrawAnalysis();
}

/** The tab row fades at the edge it scrolls past, so a tab out of sight is seen to be there (design review). */
function scTabsFade() {
  const row = sc$("tabs");
  row.classList.toggle("more-right", row.scrollLeft + row.clientWidth < row.scrollWidth - 1);
  row.classList.toggle("more-left", row.scrollLeft > 1);
}
sc$("tabs").addEventListener("scroll", scTabsFade, { passive: true });
window.addEventListener("resize", scTabsFade);

sc$("tabs").addEventListener("click", event => {
  const tab = event.target.closest("[role=tab]");
  if (tab) scShowTab(tab.id.slice(4));
});

// The tab pattern as webmap.js has it: arrows move between the tabs that are shown, Home and End go to the ends.
sc$("tabs").addEventListener("keydown", event => {
  if (!["ArrowRight", "ArrowLeft", "Home", "End"].includes(event.key)) return;
  const tabs = [...document.querySelectorAll("#tabs [role=tab]")].filter(t => !t.hidden);
  const at = tabs.findIndex(t => t.getAttribute("aria-selected") === "true");
  const next = event.key === "Home" ? tabs[0] : event.key === "End" ? tabs[tabs.length - 1]
    : tabs[(at + (event.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
  event.preventDefault();
  scShowTab(next.id.slice(4));
  next.focus();
});

// ---------------------------------------------------------------------------------------------------------------------
// Layers: visibility, order, opacity, removal, and each layer's settings — its look, its height and its pop-ups.

/** The geometry a layer draws, as the style choices need it: "polygon", "point", "polyline", "mesh" or null. */
/** A layer of features the SDK holds or queries: a feature service's, or a CSV or GeoJSON file's. */
function scFeatureLike(layer) {
  return layer.type === "feature" || layer.type === "csv" || layer.type === "geojson";
}

function scGeometry(layer) {
  if (scFeatureLike(layer)) return layer.geometryType || null;
  if (layer.type === "scene") return layer.geometryType === "point" ? "point" : "mesh";
  return null;
}

/** Whether this page can restyle a layer: the kinds whose renderer a web scene keeps and whose fields it can read. */
function scStylable(layer) {
  return !!scGeometry(layer) && layer.loadStatus === "loaded";
}

function scNumberFields(layer) {
  return (layer.fields || []).filter(f => ["small-integer", "integer", "single", "double", "long", "big-integer"].includes(f.type));
}

function scDrawLayers() {
  // The tools' list of layers follows the scene's, when they are open.
  if (!sc$("pane-analysis").hidden) scDrawAnalysis();
  const list = sc$("layerList");
  const layers = scState.scene.layers.toArray().slice().reverse();
  if (layers.length === 0) {
    list.innerHTML = `<li class="hint" style="border:0;padding:0">This scene has no layers yet.${
      scCanEdit() ? " Add one from the Add tab." : ""}</li>`;
    return;
  }
  const edit = scCanEdit();
  list.innerHTML = layers.map((layer, at) => `
    <li data-layer="${scEscape(layer.id)}">
      <div class="lrow">
        <label style="flex:1;min-width:0;display:flex;gap:8px;align-items:center"><input type="checkbox" data-act="visible" ${layer.visible ? "checked" : ""}>
          <span class="lname">${scEscape(layer.title || "Untitled layer")}<span class="lkind">${scEscape(scKind(layer))}${
            layer.loadStatus === "failed" ? ` · <span class="lnote">could not be read</span>` : ""}</span></span></label>
        <span class="lbtns">
          ${edit ? `<button type="button" class="tiny" data-act="up" ${at === 0 ? "disabled" : ""}
            aria-label="Move ${scEscape(layer.title || "this layer")} up">▲</button>
          <button type="button" class="tiny" data-act="down" ${at === layers.length - 1 ? "disabled" : ""}
            aria-label="Move ${scEscape(layer.title || "this layer")} down">▼</button>` : ""}
          <button type="button" class="tiny" data-act="settings" aria-expanded="${scState.open.has(layer.id)}"
            aria-label="${edit ? "Settings for" : "About"} ${scEscape(layer.title || "this layer")}">${edit ? "Settings" : "About"}</button>
        </span>
      </div>
      ${scState.open.has(layer.id) ? scLayerDetail(layer) : ""}
    </li>`).join("");
  scFillLayerDetails();
}

/** The settings of one layer, drawn under its row. */
function scLayerDetail(layer) {
  const edit = scCanEdit();
  const geometry = scGeometry(layer);
  const id = scEscape(layer.id);
  const opacity = Math.round((layer.opacity ?? 1) * 100);
  let html = `<div class="ldetail" data-detail="${id}">
    <div class="row" style="margin-top:6px">
      <button type="button" class="tiny" data-act="zoom">Zoom to</button>
      ${edit ? `<button type="button" class="tiny danger" data-act="remove" aria-label="Remove ${scEscape(layer.title || "this layer")}">Remove</button>` : ""}
    </div>`;
  if (!edit) return html + `<p class="hint">${scEscape(scKind(layer))}${layer.url ? ` from ${scEscape(layer.url)}` : ""}.</p></div>`;

  html += `<label class="field" for="op-${id}">Opacity <span data-says="opacity">${opacity}%</span></label>
    <input type="range" id="op-${id}" data-act="opacity" min="0" max="100" step="5" value="${opacity}">
    <label class="field" for="ttl-${id}">Title in this scene</label>
    <input type="text" id="ttl-${id}" data-act="title" value="${scEscape(layer.title || "")}" maxlength="250">`;

  if (scStylable(layer)) {
    html += `<h3>Look</h3><div data-part="style"></div>`;
    if (scFeatureLike(layer) || geometry === "point") {
      html += `<h3>Position</h3>
        <label class="field" for="em-${id}">Sits</label>
        <select id="em-${id}" data-act="elevation">
          ${scFeatureLike(layer) ? `<option value="on-the-ground">Draped on the ground</option>` : ""}
          <option value="relative-to-ground">Above the ground</option>
          <option value="absolute-height">At its own height</option>
          ${scFeatureLike(layer) ? `<option value="relative-to-scene">On top of buildings and meshes</option>` : ""}
        </select>
        <div data-part="offset"><label class="field" for="eo-${id}">Raised by (metres)</label>
        <input type="number" id="eo-${id}" data-act="offset" step="any" value="${(layer.elevationInfo && layer.elevationInfo.offset) || 0}"></div>`;
    }
    html += `<h3>Pop-ups</h3><div data-part="popup"></div>`;
  }
  if (layer.type === "point-cloud" && layer.loadStatus === "loaded") html += scPointCloudForm(layer);
  html += scVisibilityForm(layer);
  return html + `</div>`;
}

// --- a point cloud's look: Scene Viewer's RGB, stretch, classes (ADR-270 §5.12) ------------------------------------------

/** The looks a point cloud can take, those its attributes allow. */
function scPointCloudLooks(layer) {
  const has = name => (layer.fields || []).some(f => f.name.toUpperCase() === name);
  return [
    has("RGB") && ["rgb", "True colour"],
    ["elevation", "By elevation"],
    has("CLASS_CODE") && ["class", "By class"],
    has("INTENSITY") && ["intensity", "By intensity"],
  ].filter(Boolean);
}

function scPointCloudForm(layer) {
  const id = scEscape(layer.id);
  const current = ({ "point-cloud-rgb": "rgb", "point-cloud-unique-value": "class" })[layer.renderer && layer.renderer.type]
    || (layer.renderer && layer.renderer.type === "point-cloud-stretch" && /INTENSITY/i.test(layer.renderer.field || "") ? "intensity"
      : layer.renderer && layer.renderer.type === "point-cloud-stretch" ? "elevation" : "");
  return `<h3>Look</h3>
    <label class="field" for="pc-${id}">Coloured</label>
    <select id="pc-${id}" data-pc="look">${current ? "" : `<option value="">As published</option>`}${scPointCloudLooks(layer).map(([v, t]) =>
      `<option value="${v}" ${v === current ? "selected" : ""}>${scEscape(t)}</option>`).join("")}</select>`;
}

/** LAS classes as a reader names them, the ones a survey usually has. */
const SC_LAS_CLASSES = [[["2"], "Ground", [166, 124, 82]], [["3", "4", "5"], "Vegetation", [56, 168, 0]], [["6"], "Building", [222, 98, 65]],
  [["9"], "Water", [0, 112, 255]], [["1"], "Unassigned", [180, 180, 180]], [["7", "18"], "Noise", [255, 0, 197]]];

async function scPointCloudRenderer(layer, look) {
  const field = name => ((layer.fields || []).find(f => f.name.toUpperCase() === name) || {}).name || name;
  const size = { pointSizeAlgorithm: { type: "splat", scaleFactor: 1 }, pointsPerInch: 15 };
  if (look === "rgb") return { type: "point-cloud-rgb", field: field("RGB"), ...size };
  if (look === "class") {
    return { type: "point-cloud-unique-value", field: field("CLASS_CODE"), ...size,
      colorUniqueValueInfos: SC_LAS_CLASSES.map(([values, label, color]) => ({ values, label, color })) };
  }
  if (look === "intensity") {
    return { type: "point-cloud-stretch", field: field("INTENSITY"), ...size,
      stops: [{ value: 0, color: [20, 20, 20], label: "Low" }, { value: 255, color: [255, 255, 255], label: "High" }] };
  }
  // The heights from the layer's extent, or, when it has none — Esri's Morro Bay sample has none — from the ELEVATION
  // attribute's statistics, which a point cloud package carries (measured 2026-11-07: 0–100 left most points one colour).
  const extent = layer.fullExtent || {};
  let low = extent.zmin, high = extent.zmax;
  if (!Number.isFinite(low) || !Number.isFinite(high) || low === high) {
    const stats = await layer.queryCachedStatistics(field("ELEVATION")).catch(() => null);
    const summary = stats && stats.stats;
    low = summary && Number.isFinite(summary.min) ? summary.min : 0;
    high = summary && Number.isFinite(summary.max) && summary.max > low ? summary.max : low + 100;
  }
  return { type: "point-cloud-stretch", field: field("ELEVATION"), ...size,
    stops: [{ value: low, color: [43, 131, 186], label: String(Math.round(low)) },
      { value: (low + high) / 2, color: [255, 255, 191] }, { value: high, color: [215, 25, 28], label: String(Math.round(high)) }] };
}

sc$("layerList").addEventListener("change", async event => {
  if (event.target.dataset.pc !== "look") return;
  const layer = scLayerOf(event.target);
  if (!layer || !event.target.value) return;
  layer.renderer = await scPointCloudRenderer(layer, event.target.value);
  scMarkDirty();
  scSay(`${layer.title || "The point cloud"} is coloured ${event.target.selectedOptions[0].textContent.toLowerCase()}.`);
});

// --- visibility: a scale range, and a filter (ADR-270 §5.9) -------------------------------------------------------------

/** Scene Viewer's named scales, as the visible range is chosen from them. 0 is no limit. */
const SC_SCALES = [[0, "No limit"], [147914382, "World"], [36978595, "Continent"], [9244649, "Country"],
  [2311162, "State"], [577791, "County"], [144448, "City"], [36112, "Town"], [9028, "Neighbourhood"],
  [2257, "Street"], [564, "Building"]];

function scVisibilityForm(layer) {
  const id = scEscape(layer.id);
  const scales = (value, label, key) => `<label class="field" for="${key}-${id}">${label}</label>
    <select id="${key}-${id}" data-vis="${key}">${SC_SCALES.map(([v, t]) =>
      `<option value="${v}" ${Number(value || 0) === v ? "selected" : ""}>${scEscape(t)}${v ? ` (1:${v.toLocaleString("en-GB")})` : ""}</option>`).join("")}</select>`;
  const custom = v => v && !SC_SCALES.some(([s]) => s === Number(v));
  return `<h3>Visibility</h3>
    ${custom(layer.minScale) || custom(layer.maxScale) ? `<p class="hint">This layer's range was set elsewhere (1:${Math.round(layer.minScale || 0)} to 1:${Math.round(layer.maxScale || 0)}); choosing here replaces it.</p>` : ""}
    ${scales(layer.minScale, "Shown when zoomed in beyond", "minScale")}
    ${scales(layer.maxScale, "Hidden when zoomed in beyond", "maxScale")}
    ${scFeatureLike(layer) ? `<label class="field" for="flt-${id}">Filter — which features are shown</label>
      <input type="text" id="flt-${id}" data-vis="filter" value="${scEscape(layer.definitionExpression || "")}" placeholder="e.g. floors >= 5" autocomplete="off">
      <p class="hint">A condition on the layer's fields, as a query's <code>where</code> is written. Press Enter to apply; empty shows every feature.</p>` : ""}`;
}

sc$("layerList").addEventListener("change", async event => {
  const key = event.target.dataset.vis;
  if (!key) return;
  const layer = scLayerOf(event.target);
  if (!layer) return;
  if (key === "minScale" || key === "maxScale") {
    layer[key] = Number(event.target.value);
    scMarkDirty();
    scSay(`The visible range of ${layer.title || "the layer"} was set.`);
    return;
  }
  // A filter is tried before it is kept: one the layer refuses is said, and the layer keeps the one it had.
  const where = event.target.value.trim();
  try {
    if (where) await layer.queryFeatureCount({ where });
    layer.definitionExpression = where || null;
    scMarkDirty();
    scSay(where ? `${layer.title || "The layer"} shows only features where ${where}.` : `${layer.title || "The layer"} shows every feature.`);
  } catch (e) {
    event.target.value = layer.definitionExpression || "";
    scSay(`That filter was not accepted: ${(e.details && e.details.messages && e.details.messages[0]) || e.message}`, true);
  }
});

/** Fills each open layer's style and pop-up parts, which need the layer's fields and current renderer. */
function scFillLayerDetails() {
  for (const detail of document.querySelectorAll("[data-detail]")) {
    const layer = scState.scene.layers.find(l => l.id === detail.dataset.detail);
    if (!layer) continue;
    const elevation = detail.querySelector("[data-act=elevation]");
    if (elevation) {
      const mode = (layer.elevationInfo && layer.elevationInfo.mode) || (scFeatureLike(layer) ? "on-the-ground" : "relative-to-ground");
      elevation.value = mode;
      if (!elevation.value) elevation.selectedIndex = 0;
      // Draped features ignore an offset, so it is not offered (design review).
      detail.querySelector("[data-part=offset]").hidden = elevation.value === "on-the-ground";
    }
    const style = detail.querySelector("[data-part=style]");
    if (style) style.innerHTML = scStyleForm(layer);
    const popup = detail.querySelector("[data-part=popup]");
    if (popup) popup.innerHTML = scPopupForm(layer);
  }
}

// --- the look --------------------------------------------------------------------------------------------------------

/**
 * A layer's look as this page edits it, read back from its renderer: a simple renderer with one 3D symbol and up to two
 * visual variables (a size and a colour), which is what every choice here writes. Anything else is `own`, left alone
 * until the reader picks a look, so a scene styled in Pro is not rewritten by being opened.
 */
function scReadStyle(layer) {
  const geometry = scGeometry(layer);
  const style = { shape: scDefaultShape(geometry), color: "#3a7bd5", size: geometry === "point" ? 10 : 2,
    sizeField: "", sizeScale: 1, colorBy: "single", colorField: "", own: false };
  const renderer = layer.renderer;
  if (!renderer) return style;
  // A colour for each value, or classes of a number field (ADR-270 §5.15): the symbol of the first value or class carries
  // the shape and size, and the last class's colour is the ramp's end.
  const grouped = renderer.type === "unique-value" ? renderer.uniqueValueInfos
    : renderer.type === "class-breaks" ? renderer.classBreakInfos : null;
  const symbol = grouped ? grouped.length && grouped[grouped.length - 1].symbol : renderer.symbol;
  if (grouped && renderer.field && symbol && symbol.symbolLayers) {
    style.colorBy = renderer.type === "unique-value" ? "unique" : "classes";
    style.colorField = renderer.field;
  } else if (renderer.type !== "simple" || !symbol || !symbol.symbolLayers) { style.own = renderer.type !== "simple"; return style; }
  const symbolLayer = symbol.symbolLayers.getItemAt(0);
  if (!symbolLayer) return style;
  const color = symbolLayer.material && symbolLayer.material.color;
  if (color) style.color = color.toHex();
  style.shape = ({ extrude: "extrude", fill: geometry === "mesh" ? "mesh" : "flat", icon: "icon", line: "line", path: "tube" })[symbolLayer.type]
    || (symbolLayer.type === "object" ? (symbolLayer.resource && symbolLayer.resource.primitive) || "sphere" : style.shape);
  if (symbolLayer.size) style.size = symbolLayer.size;
  if (symbolLayer.width) style.size = symbolLayer.width;
  if (symbolLayer.height && symbolLayer.type === "object") style.size = symbolLayer.height;
  for (const variable of renderer.visualVariables || []) {
    if (variable.type === "size") {
      const match = /^\$feature\.(\w+)\s*\*\s*([\d.]+)$/.exec(variable.valueExpression || "");
      style.sizeField = match ? match[1] : variable.field || "";
      style.sizeScale = match ? Number(match[2]) : 1;
    }
    if (variable.type === "color" && variable.field) { style.colorBy = "ramp"; style.colorField = variable.field; }
  }
  return style;
}

function scDefaultShape(geometry) {
  return ({ polygon: "flat", point: "icon", polyline: "line", mesh: "mesh" })[geometry] || "flat";
}

/** The shapes a geometry may be drawn as. */
function scShapes(geometry) {
  if (geometry === "polygon") return [["flat", "Flat, draped"], ["extrude", "Extruded"]];
  if (geometry === "point") return [["icon", "Icon"], ["sphere", "Sphere"], ["cylinder", "Cylinder"], ["cone", "Cone"],
    ["cube", "Cube"], ["diamond", "Diamond"]];
  if (geometry === "polyline") return [["line", "Line"], ["tube", "Tube"]];
  return [["mesh", "Solid colour"]];
}

function scStyleForm(layer) {
  const style = scReadStyle(layer);
  const id = scEscape(layer.id);
  const geometry = scGeometry(layer);
  const fields = scNumberFields(layer);
  const options = list => list.map(([v, t]) => `<option value="${scEscape(v)}">${scEscape(t)}</option>`).join("");
  const fieldOptions = fields.map(f => [f.name, f.alias || f.name]);
  const sized = geometry === "polygon" || (geometry === "point") || geometry === "polyline";
  const sizeLabel = geometry === "polygon" ? "Height from a field" : geometry === "point" ? "Size from a field" : "Width from a field";
  return `
    ${style.own ? `<p class="hint">This layer has a look of its own (${scEscape(layer.renderer.type)}). Choosing one here replaces it.</p>` : ""}
    <label class="field" for="sh-${id}">Drawn as</label>
    <select data-style="shape" id="sh-${id}">${options(scShapes(geometry))}</select>
    <label class="field" for="cb-${id}">Colour by</label>
    <select data-style="colorBy" id="cb-${id}">${SC_COLOR_BY.map(([v, t]) => {
      const none = v !== "single" && !scColorFieldOptions(layer, v);
      return `<option value="${v}" ${none ? "disabled" : ""}>${scEscape(t)}${none ? (v === "unique" ? " (no text fields)" : " (no number fields)") : ""}</option>`;
    }).join("")}</select>
    <div data-part="colorField" ${style.colorBy === "single" ? "hidden" : ""}>
      <label class="field" for="cf-${id}">By field</label>
      <select data-style="colorField" id="cf-${id}">${scColorFieldOptions(layer, style.colorBy)}</select></div>
    <div data-part="colorSwatch" ${style.colorBy === "unique" ? "hidden" : ""}>
      <label class="field" for="co-${id}" data-part="colorSwatchLabel">${SC_SWATCH_LABELS[style.colorBy] || "Colour"}</label>
      <input type="color" data-style="color" id="co-${id}" value="${scEscape(style.color)}"></div>
    ${sized ? `<label class="field" for="sz-${id}">${geometry === "polygon" ? "Height (metres)" : geometry === "point" ? "Size (metres for shapes, pixels for icons)" : "Width (metres for tubes, pixels for lines)"}</label>
    <input type="number" data-style="size" id="sz-${id}" min="0" step="any" value="${scEscape(style.size)}">
    <label class="field" for="sf-${id}">${sizeLabel}</label>
    <div class="row"><select data-style="sizeField" id="sf-${id}" style="flex:1;width:auto">
      <option value="">None</option>${options(fieldOptions)}</select>
      <span class="hint" style="margin:0">×</span>
      <input type="number" data-style="sizeScale" min="0" step="any" value="${scEscape(style.sizeScale)}" style="width:70px" aria-label="Metres per unit">
    </div>` : ""}
    `;
}

/** How a layer is coloured: Scene Viewer's styles that colour by a field, beside one colour (ADR-270 §5.15). */
const SC_COLOR_BY = [["single", "One colour"], ["ramp", "A ramp by a number field"], ["classes", "Classes of a number field"],
  ["unique", "A colour for each value"]];

/** The fields a colouring can use: numbers for a ramp or classes, and text or whole numbers for a colour each. */
function scColorFieldOptions(layer, colorBy) {
  const fields = colorBy === "unique"
    ? (layer.fields || []).filter(f => ["string", "small-integer", "integer", "long"].includes(f.type) && !scQuietField(f))
    : scNumberFields(layer).filter(f => !scQuietField(f));
  return fields.map(f => `<option value="${scEscape(f.name)}">${scEscape(f.alias || f.name)}</option>`).join("");
}

/** What the colour well means under each colouring; a colour each has none. */
const SC_SWATCH_LABELS = { single: "Colour", ramp: "Ramp ends at", classes: "Darkest class" };

/**
 * Ten colours told apart from each other, for a colour each: Okabe and Ito's eight, read by most colour-blind readers,
 * and two more (design review); what is not among the ten most common is a light grey, as Other.
 */
const SC_CATEGORIES = ["#e69f00", "#56b4e9", "#009e73", "#f0e442", "#0072b2", "#d55e00", "#cc79a7", "#000000", "#8c564b", "#7f7f7f"];

/** A colour between two, a share of the way along. */
function scBlend(from, to, share) {
  const a = from.match(/\w\w/g).map(h => parseInt(h, 16)), b = to.match(/\w\w/g).map(h => parseInt(h, 16));
  return "#" + a.map((v, i) => Math.round(v + (b[i] - v) * share).toString(16).padStart(2, "0")).join("");
}

// The field list follows the colouring chosen, before the look is applied with it.
sc$("layerList").addEventListener("change", event => {
  if (!event.target.matches("[data-style=colorBy]")) return;
  const detail = event.target.closest("[data-detail]");
  const layer = scLayerOf(event.target);
  if (!detail || !layer) return;
  const row = detail.querySelector("[data-part=colorField]");
  row.hidden = event.target.value === "single";
  row.querySelector("select").innerHTML = scColorFieldOptions(layer, event.target.value);
  detail.querySelector("[data-part=colorSwatch]").hidden = event.target.value === "unique";
  detail.querySelector("[data-part=colorSwatchLabel]").textContent = SC_SWATCH_LABELS[event.target.value] || "Colour";
}, true);

/** Puts the form's current values into the select boxes after drawing, where `value=` in the markup cannot. */
function scSyncStyleForm(detail, layer) {
  const style = scReadStyle(layer);
  for (const [key, value] of Object.entries(style)) {
    const control = detail.querySelector(`[data-style=${key}]`);
    if (control && control.tagName === "SELECT") control.value = String(value);
  }
}

/** The style read from a layer's form. */
function scFormStyle(detail) {
  const read = key => { const c = detail.querySelector(`[data-style=${key}]`); return c ? c.value : ""; };
  const colorBy = read("colorBy") || "single";
  return { shape: read("shape"), color: read("color") || "#3a7bd5", size: Number(read("size")) || 0,
    sizeField: read("sizeField"), sizeScale: Number(read("sizeScale")) || 1, colorBy,
    colorField: colorBy === "single" ? "" : read("colorField") };
}

/** The renderer a style asks for, as the JSON the SDK autocasts — the same JSON a web scene keeps. */
async function scRendererFor(layer, style) {
  const geometry = scGeometry(layer);
  const symbolOf = color => scSymbolFor(geometry, style, { color });
  const symbol = symbolOf(style.color);
  const visualVariables = await scSizeVariables(layer, style, geometry);

  if (style.colorBy === "unique" && style.colorField) {
    const [uniqueValues] = await scRequire(["esri/smartMapping/statistics/uniqueValues"]);
    const found = await uniqueValues({ layer, field: style.colorField, view: scState.view });
    const common = (found.uniqueValueInfos || []).filter(info => info.value !== null && info.value !== undefined)
      .sort((a, b) => b.count - a.count).slice(0, SC_CATEGORIES.length);
    if (common.length === 0) throw new Error(`${style.colorField} has no values to colour by`);
    return { type: "unique-value", field: style.colorField, visualVariables,
      defaultSymbol: symbolOf("#c9ced4"), defaultLabel: "Other",
      uniqueValueInfos: common.map((info, i) => ({ value: info.value, label: String(info.value), symbol: symbolOf(SC_CATEGORIES[i]) })) };
  }

  if (style.colorBy === "classes" && style.colorField) {
    const [classBreaks] = await scRequire(["esri/smartMapping/statistics/classBreaks"]);
    const found = await classBreaks({ layer, field: style.colorField, classificationMethod: "natural-breaks", numClasses: 5, view: scState.view });
    const classes = found.classBreakInfos || [];
    if (classes.length === 0) throw new Error(`${style.colorField} has no values to class`);
    return { type: "class-breaks", field: style.colorField, visualVariables,
      classBreakInfos: classes.map((info, i) => ({ minValue: info.minValue, maxValue: info.maxValue, label: info.label,
        symbol: symbolOf(scBlend(scBlend("#ffffff", style.color, 0.25), scBlend(style.color, "#000000", 0.25),
          classes.length === 1 ? 1 : i / (classes.length - 1))) })) };
  }

  if (style.colorBy === "ramp" && style.colorField) {
    const stats = await scFieldRange(layer, style.colorField);
    visualVariables.push({ type: "color", field: style.colorField,
      stops: [{ value: stats.min, color: "#fde4c3", label: String(stats.min) }, { value: stats.max, color: style.color, label: String(stats.max) }] });
  }
  return { type: "simple", symbol, visualVariables };
}

/** The 3D symbol a style draws with, in one colour. */
function scSymbolFor(geometry, style, material) {
  let symbol;
  if (geometry === "polygon") {
    symbol = { type: "polygon-3d", symbolLayers: [style.shape === "extrude"
      ? { type: "extrude", size: style.size || 10, material, edges: { type: "solid", color: [0, 0, 0, 0.35], size: 0.5 } }
      : { type: "fill", material, outline: { color: [0, 0, 0, 0.4], size: 0.75 } }] };
  } else if (geometry === "point") {
    symbol = { type: "point-3d", symbolLayers: [style.shape === "icon"
      ? { type: "icon", resource: { primitive: "circle" }, material, size: style.size || 10, outline: { color: "white", size: 1 } }
      : { type: "object", resource: { primitive: style.shape }, material, width: style.size || 10, depth: style.size || 10,
        height: style.size || 10, anchor: "bottom" }] };
  } else if (geometry === "polyline") {
    symbol = { type: "line-3d", symbolLayers: [style.shape === "tube"
      ? { type: "path", profile: "circle", width: style.size || 2, height: style.size || 2, material }
      : { type: "line", size: style.size || 2, material }] };
  } else {
    symbol = { type: "mesh-3d", symbolLayers: [{ type: "fill", material }] };
  }
  return symbol;
}

/** The size variable a style asks for: a height, size or width from a field. */
async function scSizeVariables(layer, style, geometry) {
  const visualVariables = [];
  if (style.sizeField && geometry !== "mesh" && !(geometry === "polygon" && style.shape !== "extrude")) {
    // A field times metres per unit: floors × 3 is a height in metres (ADR-269's dialog asks the same).
    const size = { type: "size", valueExpression: `$feature.${style.sizeField} * ${style.sizeScale || 1}`,
      valueUnit: geometry === "point" && style.shape === "icon" ? undefined : "meters" };
    if (geometry === "point" && style.shape !== "icon") size.axis = "height";
    if (size.valueUnit === undefined) {
      const stats = await scFieldRange(layer, style.sizeField);
      Object.assign(size, { field: style.sizeField, valueExpression: undefined, minDataValue: stats.min, maxDataValue: stats.max,
        minSize: 6, maxSize: 36 });
    }
    visualVariables.push(size);
  }
  return visualVariables;
}

/** A number field's smallest and largest value, by the SDK's own statistics (a query, or a scene layer's statistics). */
async function scFieldRange(layer, field) {
  try {
    const [summaryStatistics] = await scRequire(["esri/smartMapping/statistics/summaryStatistics"]);
    const stats = await summaryStatistics({ layer, field, view: scState.view });
    if (Number.isFinite(stats.min) && Number.isFinite(stats.max)) return { min: stats.min, max: stats.max === stats.min ? stats.min + 1 : stats.max };
  } catch { /* a layer without statistics: a range that still draws */ }
  return { min: 0, max: 100 };
}

// --- pop-ups ---------------------------------------------------------------------------------------------------------

function scPopupForm(layer) {
  const id = scEscape(layer.id);
  const fields = (layer.fields || []).filter(f => f.type !== "geometry" && f.type !== "blob");
  const template = layer.popupTemplate;
  const shown = new Set();
  let titleField = "";
  if (template) {
    const m = /^\{(\w+)\}$/.exec(typeof template.title === "string" ? template.title : "");
    titleField = m ? m[1] : "";
    for (const info of template.fieldInfos || []) if (info.visible !== false) shown.add(info.fieldName);
  }
  const all = !template || shown.size === 0;
  return `
    <label class="check"><input type="checkbox" data-popup="enabled" ${layer.popupEnabled !== false ? "checked" : ""}> Show a pop-up when a feature is clicked</label>
    <label class="field" for="pt-${id}">Pop-up title</label>
    <select data-popup="title" id="pt-${id}"><option value="">The layer's title</option>${
      fields.map(f => `<option value="${scEscape(f.name)}" ${f.name === titleField ? "selected" : ""}>${scEscape(f.alias || f.name)}</option>`).join("")}</select>
    <fieldset class="fields"><legend>Fields shown</legend>${fields.length === 0 ? `<span class="hint">This layer has no fields to show.</span>` : fields.map(f => `
      <label><input type="checkbox" data-popup-field="${scEscape(f.name)}" ${(all ? !scQuietField(f) : shown.has(f.name)) ? "checked" : ""}> ${scEscape(f.alias || f.name)}</label>`).join("")}</fieldset>
    `;
}

/** An id field, which a reader rarely wants in a pop-up: shown only when asked for (design review). */
function scQuietField(field) {
  return field.type === "oid" || field.type === "global-id" || /^(objectid|fid|globalid|shape_(length|area))$/i.test(field.name);
}

async function scApplyPopup(layer, detail) {
  const [PopupTemplate] = await scRequire(["esri/PopupTemplate"]);
  layer.popupEnabled = detail.querySelector("[data-popup=enabled]").checked;
  const titleField = detail.querySelector("[data-popup=title]").value;
  const fieldInfos = [...detail.querySelectorAll("[data-popup-field]")].map(box => {
    const field = (layer.fields || []).find(f => f.name === box.dataset.popupField);
    return { fieldName: box.dataset.popupField, label: (field && field.alias) || box.dataset.popupField, visible: box.checked };
  });
  layer.popupTemplate = new PopupTemplate({
    title: titleField ? `{${titleField}}` : (layer.title || "Feature"),
    outFields: ["*"],
    fieldInfos,
    content: [{ type: "fields", fieldInfos: fieldInfos.filter(f => f.visible) }],
  });
  scMarkDirty();
  scSay(`The pop-up of ${layer.title || "the layer"} was set.`);
}

// --- the list's events -----------------------------------------------------------------------------------------------

function scLayerOf(element) {
  const row = element.closest("[data-layer]");
  return row ? scState.scene.layers.find(l => l.id === row.dataset.layer) : null;
}

sc$("layerList").addEventListener("change", event => {
  const layer = scLayerOf(event.target);
  if (!layer) return;
  const act = event.target.dataset.act;
  if (act === "visible") {
    layer.visible = event.target.checked;
    scMarkDirty();
  } else if (act === "title") {
    layer.title = event.target.value.trim() || layer.title;
    scMarkDirty();
  } else if (act === "elevation" || act === "offset") {
    const detail = event.target.closest("[data-detail]");
    const mode = detail.querySelector("[data-act=elevation]").value;
    const offset = Number(detail.querySelector("[data-act=offset]").value) || 0;
    layer.elevationInfo = { mode, offset: mode === "on-the-ground" ? undefined : offset };
    detail.querySelector("[data-part=offset]").hidden = mode === "on-the-ground";
    scMarkDirty();
  }
});

sc$("layerList").addEventListener("input", event => {
  if (event.target.dataset.act !== "opacity") return;
  const layer = scLayerOf(event.target);
  if (!layer) return;
  layer.opacity = Number(event.target.value) / 100;
  event.target.closest("[data-detail]").querySelector("[data-says=opacity]").textContent = `${event.target.value}%`;
  scMarkDirty();
});

sc$("layerList").addEventListener("click", async event => {
  const button = event.target.closest("button[data-act]");
  if (!button) return;
  const layer = scLayerOf(button);
  if (!layer) return;
  const act = button.dataset.act;
  const layers = scState.scene.layers;
  if (act === "up" || act === "down") {
    // The list is drawn top first, so "up" is a higher index in the scene's collection.
    const at = layers.indexOf(layer);
    const to = act === "up" ? Math.min(layers.length - 1, at + 1) : Math.max(0, at - 1);
    layers.reorder(layer, to);
    scMarkDirty();
    scDrawLayers();
    scSay(`${layer.title || "The layer"} was moved ${act}.`);
    const again = sc$("layerList").querySelector(`[data-layer="${CSS.escape(layer.id)}"] [data-act=${act}]`);
    (again && !again.disabled ? again : sc$("layerList").querySelector(`[data-layer="${CSS.escape(layer.id)}"] [data-act=settings]`)).focus();
  } else if (act === "settings") {
    if (scState.open.has(layer.id)) scState.open.delete(layer.id); else scState.open.add(layer.id);
    scDrawLayers();
    const detail = sc$("layerList").querySelector(`[data-detail="${CSS.escape(layer.id)}"]`);
    if (detail) scSyncStyleForm(detail, layer);
    sc$("layerList").querySelector(`[data-layer="${CSS.escape(layer.id)}"] [data-act=settings]`).focus();
  } else if (act === "zoom") {
    const extent = layer.fullExtent;
    if (extent) scState.view.goTo({ target: extent, tilt: 55 }).catch(() => {});
    else scSay(`${layer.title || "The layer"} does not say where it is.`, true);
  } else if (act === "remove") {
    const title = layer.title || "The layer";
    scState.scene.remove(layer);
    scState.open.delete(layer.id);
    scMarkDirty();
    scDrawLayers();
    scSaySummary();
    scSay(`${title} was removed.`);
    (sc$("layerList").querySelector("input, button") || sc$("tab-layers")).focus();
  }
});

// <b>A layer's look and pop-up apply as they are changed</b>, as its opacity and height do (design review, blocker): with
// Apply buttons, anything that redrew the list — a slide, a reorder — threw away choices not yet applied, unsaid.
const scApplyTimers = new Map();
function scApplySoon(layer, detail, what) {
  clearTimeout(scApplyTimers.get(layer.id + what));
  scApplyTimers.set(layer.id + what, setTimeout(async () => {
    try {
      if (what === "style") {
        layer.renderer = await scRendererFor(layer, scFormStyle(detail));
        scMarkDirty();
        scSay(`The look of ${layer.title || "the layer"} was set.`);
      } else {
        await scApplyPopup(layer, detail);
      }
    } catch (e) {
      scSay(`The ${what === "style" ? "look" : "pop-up"} could not be set: ${e.message}`, true);
    }
  }, Number(window.__applyDelay ?? 300)));
}

sc$("layerList").addEventListener("change", event => {
  const detail = event.target.closest("[data-detail]");
  if (!detail) return;
  const layer = scLayerOf(event.target);
  if (!layer) return;
  if (event.target.matches("[data-style]")) scApplySoon(layer, detail, "style");
  if (event.target.matches("[data-popup], [data-popup-field]")) scApplySoon(layer, detail, "popup");
});

// ---------------------------------------------------------------------------------------------------------------------
// Adding layers: the portal's own search, by title and kind, the reader's content or everything shared with them.

/**
 * Adds a layer from a service address, or from its portal item when it was picked from the list — as Scene Viewer adds
 * one, so the web scene names the item, and a scene layer's associated feature layer is found through the item's
 * `Service2Service` relationship (ADR-270 §5.4). The SDK works out what kind it is.
 */
async function scAddUrl(url, title, itemId, kind) {
  const said = sc$("addSaid");
  said.className = "said";
  said.textContent = `Adding ${title || "the layer"}…`;
  try {
    const layer = SC_FILES[kind]
      ? new (await scRequire([SC_FILES[kind]]))[0]({ url, title })
      : itemId
      ? await scState.sdk.Layer.fromPortalItem({ portalItem: { id: itemId, portal: { url: SC_PORTAL } } })
      : await scState.sdk.Layer.fromArcGISServerUrl({ url });
    await layer.load();
    // The item's title, when it was added from the list: a service's own name may be its item id (ADR-269); one
    // added by its address here is called by its item's title too, read from the portal.
    const hosted = /\/rest\/services\/hosted\/([0-9a-f]{32})\/SceneServer/i.exec(url);
    if (title) layer.title = title;
    else if (hosted) layer.title = (await scPortal(`/content/items/${hosted[1]}`).catch(() => ({}))).title || layer.title;
    // I3S: a scene layer in a geographic system is for a global scene only, and the SDK would leave it undrawn in a local
    // one without a word (ADR-270 §5.7) — so the reason is said instead.
    if (scState.mode === "local" && ["scene", "building-scene", "point-cloud", "integrated-mesh", "voxel"].includes(layer.type)
        && layer.spatialReference && layer.spatialReference.isGeographic) {
      throw new Error(`${layer.title || "this scene layer"} is in a geographic system (WGS 84), which I3S shows in a global scene only. Make a global scene for it`);
    }
    // A layer that carries its own heights is placed at them; a flat one is draped on the ground.
    if (layer.type === "feature" && layer.hasZ) layer.elevationInfo = { mode: "absolute-height" };
    scState.scene.add(layer);
    // A layer the scene loads but cannot draw — a voxel layer the pinned SDK cannot place in this scene (ADR-270 §5.17)
    // — would otherwise sit in the list drawing nothing; its reason is said instead.
    layer.__drawable = scState.view.whenLayerView(layer).then(() => true, e => {
      // The SDK's own words are for a developer: kept in the console, not shown (design review).
      console.warn("Layer view not created", layer.title, e);
      if (!scState.scene.layers.includes(layer)) return false;
      const why = layer.type === "voxel"
        ? `${layer.title || "The layer"} was added but can't be drawn: this server's 3D viewer can't draw voxel layers in WGS 84 yet. Remove it from Layers.`
        : `${layer.title || "The layer"} was added but can't be drawn in this scene. Remove it from Layers, or try it in a ${scState.mode === "local" ? "global" : "local"} scene.`;
      said.className = "said bad";
      said.textContent = why;
      scSay(why, true);
      return false;
    });
    scMarkDirty();
    scDrawLayers();
    scDrawMode();
    said.textContent = `${layer.title || "The layer"} was added.`;
    scSaySummary();
    scSay(`${layer.title || "The layer"} was added. ${sc$("status").textContent}`);
    scDrawAddItems();
    // Framed at an angle: a scene seen from straight above looks like a map (design: the point of a scene is height).
    if (layer.fullExtent) scState.view.goTo({ target: layer.fullExtent, tilt: 55 }).catch(() => {});
  } catch (e) {
    said.className = "said bad";
    said.textContent = `Not added: ${e.message || "the service could not be read"}.`;
    scSay(said.textContent, true);
    scDrawAddItems();
  }
}

/** Whether a service is already a layer of the scene, so the add list says so rather than adding it twice. */
function scHasLayer(url) {
  const plain = String(url || "").replace(/\/+$/, "");
  return !!scState.scene && scState.scene.layers.some(l => (l.url || "").replace(/\/+$/, "") === plain
    || (l.parsedUrl && `${l.url}/${l.layerId}` === plain));
}

/** The header's line about the scene as it now is: whether it is new, and how many layers it has. */
function scSaySummary() {
  const count = scState.scene.layers.length;
  // A new, empty scene says what to do first (design review: Save was off with no reason given).
  scSay(!scState.id && count === 0 ? (scCanEdit() ? "Add a layer or name the scene to start." : "") : `${scState.id ? "" : "A new scene. "}${count === 0 ? "No layers" : count === 1 ? "1 layer" : `${count} layers`}.`);
}

sc$("addUrlButton").addEventListener("click", () => {
  const url = sc$("addUrl").value.trim();
  if (!url) { sc$("addSaid").textContent = "Type a service address first."; sc$("addUrl").focus(); return; }
  scAddUrl(url);
});
sc$("addUrl").addEventListener("keydown", event => { if (event.key === "Enter") sc$("addUrlButton").click(); });

const SC_ADDABLE = ["Feature Service", "Scene Service", "Image Service", "Map Service", "Vector Tile Service", "CSV", "GeoJson"];

/**
 * A CSV or GeoJSON file in the portal is a document, with no service address: it is drawn from the document's own data,
 * which is this server's, so the console's policy admits it (ADR-270 §5.11, Scene Viewer's "add a file" from content).
 */
const SC_FILES = { "CSV": "esri/layers/CSVLayer", "GeoJson": "esri/layers/GeoJSONLayer" };

function scAddressOf(item) {
  return SC_FILES[item.type] ? `${location.origin}/content/documents/${encodeURIComponent(item.id)}/data` : item.url;
}

/** Asks the portal for what can be added, by the search box's words, the kind and whose content. */
async function scSearchAdd() {
  const list = sc$("addList");
  const words = sc$("addFilter").value.trim();
  const type = sc$("addType").value;
  const types = type ? [type] : SC_ADDABLE;
  const parts = [`type:(${types.map(t => `"${t}"`).join(" OR ")})`];
  if (words) parts.push(`title:${JSON.stringify(words)}`);
  if (sc$("addMine").checked && scState.user) parts.push(`owner:${scState.user}`);
  const asked = (scState.addAsked = (scState.addAsked || 0) + 1);
  try {
    const box = sc$("addInView").checked ? await scViewBox() : null;
    // A view that is not one box (it crosses the antimeridian) cannot narrow the list; said, not silently ignored.
    const unboxed = "This view cannot be searched as one area, so every layer is listed.";
    if (sc$("addInView").checked && !box) { sc$("addSaid").className = "said"; sc$("addSaid").textContent = unboxed; }
    else if (sc$("addSaid").textContent === unboxed) sc$("addSaid").textContent = "";
    const found = await scPortal(`/search?num=100&sortField=modified&sortOrder=desc&q=${encodeURIComponent(parts.join(" AND "))}`
      + (box ? `&bbox=${box.join(",")}` : ""));
    if (asked !== scState.addAsked) return;
    // The portal's title search is by word; the filter here is by any part of the title, as the old list's was.
    const lowered = words.toLowerCase();
    scState.addItems = (found.results || []).map(item => ({ ...item, url: scAddressOf(item) }))
      .filter(item => item.url && types.includes(item.type)
      && (!lowered || (item.title || "").toLowerCase().includes(lowered)));
    scDrawAddItems();
  } catch (e) {
    list.innerHTML = `<li class="hint" style="border:0;padding:0">The list could not be read: ${scEscape(e.message)}</li>`;
  }
}

/**
 * The view's extent in degrees, as the portal's search reads a box (ADR-271), or null when the view has none or it
 * crosses the antimeridian, which one box cannot say. A tilted camera's extent reaches to the horizon, which is what
 * ArcGIS's own "within the map area" sees too.
 */
async function scViewBox() {
  const extent = scState.view && scState.view.extent;
  if (!extent) return null;
  let geographic = extent;
  if (extent.spatialReference.isWebMercator) {
    const [webMercatorUtils] = await scRequire(["esri/geometry/support/webMercatorUtils"]);
    geographic = webMercatorUtils.webMercatorToGeographic(extent);
  } else if (!extent.spatialReference.isWGS84) {
    return null;
  }
  const clamp = (v, limit) => Math.max(-limit, Math.min(limit, v));
  const box = [clamp(geographic.xmin, 180), clamp(geographic.ymin, 90), clamp(geographic.xmax, 180), clamp(geographic.ymax, 90)];
  return box.every(Number.isFinite) && box[0] <= box[2] && box[1] <= box[3] ? box.map(v => +v.toFixed(6)) : null;
}

/** The add list, eight rows until the reader asks for all (design review). */
function scDrawAddItems() {
  const items = scState.addItems || [];
  const words = sc$("addFilter").value.trim();
  const shown = scState.showAll || words ? items : items.slice(0, 8);
  sc$("addList").innerHTML = items.length === 0
    ? `<li class="hint" style="border:0;padding:0">${sc$("addInView").checked
      ? `No ${sc$("addMine").checked ? "layers of yours" : sc$("addType").value ? scEscape(scTypeLabel(sc$("addType").value).toLowerCase()) + "s" : "layers"}${words ? ` with “${scEscape(words)}” in the title` : ""} lie within the current view. Move the view, or clear “Only within the current view”.`
      : words ? `No layer's title has “${scEscape(words)}” in it.`
      : "No layers of this kind are shared with you yet. Publish one in Content, or add one by its address."}</li>`
    : shown.map(item => `
      <li><span class="lname">${scEscape(item.title)}<span class="lkind">${scEscape(scTypeLabel(item.type))}${
        item.owner && item.owner !== scState.user ? ` · ${scEscape(item.owner)}` : ""}</span></span>
        ${scHasLayer(item.url) ? `<span class="hint" style="margin:0">Added</span>` : `<button type="button" class="tiny" data-add="${scEscape(item.url)}" data-title="${scEscape(item.title)}" data-item="${scEscape(item.id)}" data-kind="${scEscape(item.type)}"
          aria-label="Add ${scEscape(item.title)}">Add</button>`}</li>`).join("");
  const more = sc$("addShowAll");
  more.hidden = shown.length >= items.length;
  more.textContent = `Show all (${items.length})`;
}

/** The portal's item types, as Studio's rows name them. */
function scTypeLabel(type) {
  return ({ "Scene Service": "Scene layer", "Feature Service": "Feature layer", "Image Service": "Imagery layer",
    "Map Service": "Map image layer", "Vector Tile Service": "Vector tile layer", "CSV": "CSV file", "GeoJson": "GeoJSON file" })[type] || type;
}

let scAddTimer = 0;
sc$("addFilter").addEventListener("input", () => { clearTimeout(scAddTimer); scAddTimer = setTimeout(scSearchAdd, 250); });
sc$("addType").addEventListener("change", scSearchAdd);
sc$("addMine").addEventListener("change", scSearchAdd);
sc$("addInView").addEventListener("change", scSearchAdd);
sc$("addShowAll").addEventListener("click", () => {
  scState.showAll = true;
  scDrawAddItems();
  sc$("addList").querySelectorAll("button")[8]?.focus();
});

sc$("addList").addEventListener("click", event => {
  const button = event.target.closest("[data-add]");
  if (!button) return;
  button.disabled = true;
  button.textContent = "Adding…";
  scAddUrl(button.dataset.add, button.dataset.title, button.dataset.item, button.dataset.kind);
});

// ---------------------------------------------------------------------------------------------------------------------
// Analysis (ADR-270 §5.13): the Spatial Analysis Service's tools, as Studio's map runs them (analysis.js), on a feature
// layer of this scene or the feature layer a scene layer was made from; the result is added to the scene.

/** A request to this server answered as JSON, as analysis.js asks one (webmap.js's wmFetch, with this page's token). */
async function scFetch(url, options = {}) {
  const headers = { Accept: "application/json", ...(options.headers || {}) };
  if (scToken) headers.Authorization = "Bearer " + scToken;
  const response = await fetch(url, { credentials: "same-origin", ...options, headers });
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  const envelope = body && typeof body === "object" && body.error;
  if (!response.ok || envelope) {
    const failure = new Error((envelope && envelope.message) || `${response.status} ${response.statusText || ""}`.trim());
    failure.status = (envelope && Number(envelope.code)) || response.status;
    throw failure;
  }
  return body;
}

/** A feature layer's own address, with its layer id: the SDK keeps a portal item's as the service and the id apart. */
function scFeatureAddress(layer) {
  if (!layer || !layer.url) return null;
  const url = String(layer.url).replace(/\/+$/, "");
  return /\/FeatureServer$/i.test(url) ? `${url}/${layer.layerId ?? 0}` : url;
}

/**
 * What can be analysed: this server's hosted feature layers in the scene, and the feature layer a scene layer made here
 * is related to — Scene Viewer analyses a scene layer through its associated layer. A registered layer or another
 * server's is left out, because the service refuses it by name (ADR-222 condition 4).
 */
function scAnalysable() {
  const hosted = url => {
    try {
      const parsed = new URL(url, location.href);
      return parsed.origin === location.origin && /\/rest\/services\/hosted\/[^/]+\/FeatureServer\/\d+$/.test(parsed.pathname);
    } catch { return false; }
  };
  const found = [];
  for (const layer of scState.scene.layers.toArray()) {
    const source = layer.type === "feature" ? layer : layer.type === "scene" ? layer.associatedLayer : null;
    const url = scFeatureAddress(source);
    if (url && hosted(url) && !found.some(f => f.url === url)) {
      found.push({ url: new URL(url, location.href).href, title: layer.title || "Untitled layer",
        via: layer.type === "scene" ? " — the feature layer behind it" : "" });
    }
  }
  return found;
}

/** The panel: which layer to analyse and the tools on it, or why there is nothing to analyse. */
function scDrawAnalysis() {
  const pick = sc$("azLayer");
  const layers = scAnalysable();
  if (layers.length === 0) {
    sc$("azPick").hidden = true;
    sc$("analyzeBody").innerHTML = "";
    sc$("analyzeFoot").innerHTML = "";
    sc$("azNone").hidden = false;
    sc$("azNone").innerHTML = `Analysis runs on hosted feature layers from this server. Add one, or a scene layer published from one, on the <button type="button" class="linkish" id="azToAdd">Add tab</button>.`;
    scState.analysisOpen = false;
    return;
  }
  sc$("azNone").hidden = true;
  sc$("azPick").hidden = false;
  const was = pick.value;
  pick.innerHTML = layers.map(l => `<option value="${scEscape(l.url)}">${scEscape(l.title + l.via)}</option>`).join("");
  if (layers.some(l => l.url === was)) pick.value = was;
  if (pick.value !== was || !scState.analysisOpen) scOpenAnalysis();
}

/** Opens the shared tools on the layer chosen; a result is added to this scene. */
function scOpenAnalysis() {
  const chosen = scAnalysable().find(l => l.url === sc$("azLayer").value);
  if (!chosen || !window.GraticulaAnalysis) return;
  scState.analysisOpen = true;
  GraticulaAnalysis.open({ url: chosen.url, name: chosen.title }, {
    fetch: scFetch,
    notify: (text, tone) => scSay(text, tone === false),
    body: sc$("analyzeBody"),
    foot: sc$("analyzeFoot"),
    lead: name => `Runs on <b>${name}</b>. The result is a new hosted layer of yours, private until you share it, and is added to this scene.`,
    focus: false,
    // Cancel starts the questions again; the tab stays where the reader is.
    close: () => { scOpenAnalysis(); sc$("azLayer").focus(); },
    succeeded: async (run, value, open) => {
      const url = value.url ? `${String(value.url).replace(/\/+$/, "")}${/FeatureServer$/i.test(value.url) ? "/0" : ""}`
        : `${location.origin}/rest/services/hosted/${run.name}/FeatureServer/0`;
      const before = scState.scene.layers.length;
      await scAddUrl(url, run.name, value.itemId, "Feature Service");
      const layer = scState.scene.layers.length > before ? scState.scene.layers.getItemAt(scState.scene.layers.length - 1) : null;
      // Added and drawn: a layer that cannot be drawn is not "added to the scene" for the reader (design review).
      const added = !!layer && await (layer.__drawable || Promise.resolve(true));
      if (!open) return {};
      return added
        ? { html: `Done: ${scEscape(run.name)} was added to the scene. <a href="/studio/#/item/${scEscape(value.itemId || "")}" id="analyzeResult">Open it in Content</a>.`,
          focus: "analyzeResult" }
        : { html: `Done: ${scEscape(run.name)} is in Content; it could not be added to the scene. ${scEscape(sc$("addSaid").textContent)}` };
    },
  });
}

sc$("azLayer").addEventListener("change", scOpenAnalysis);
sc$("azNone").addEventListener("click", event => { if (event.target.id === "azToAdd") { scShowTab("add"); sc$("tab-add").focus(); } });

/**
 * The elevation surfaces the ground can be: the image services shared with the reader that are elevation layers — the
 * "Elevation 3D Layer" keyword ADR-182's surfaces carry, as Esri's Terrain3D does, and the list Scene Viewer offers. An
 * image service that is a picture, not heights, is not offered (ADR-270 §5.8).
 */
async function scFillGroundChoices() {
  try {
    const found = await scPortal(`/search?num=100&sortField=title&sortOrder=asc&q=${encodeURIComponent('type:"Image Service"')}`);
    const ground = sc$("groundSelect");
    const current = scState.scene.ground.layers.getItemAt(0);
    const surfaces = (found.results || []).filter(i => i.url && i.type === "Image Service"
      && (i.typeKeywords || []).includes("Elevation 3D Layer"));
    sc$("groundNone").hidden = surfaces.length > 0;
    for (const item of surfaces) {
      if ([...ground.options].some(o => o.value === item.url)) continue;
      const option = document.createElement("option");
      option.value = item.url;
      option.textContent = item.title;
      ground.append(option);
    }
    if (current && current.url) {
      if (![...ground.options].some(o => o.value === current.url)) {
        const option = document.createElement("option");
        option.value = current.url;
        option.textContent = current.title || current.url;
        ground.append(option);
      }
      ground.value = current.url;
    }
  } catch { /* the ground stays flat or as saved */ }
}

// ---------------------------------------------------------------------------------------------------------------------
// The basemap: the portal's gallery, the operator's map ground, OpenStreetMap, or none — webmap.js's list (ADR-184).

/** OpenStreetMap from its one address, which the console's policy names (see scStart). */
function scOsmLayer(layer) {
  return new scState.sdk.WebTileLayer({
    urlTemplate: "https://tile.openstreetmap.org/{level}/{col}/{row}.png",
    title: (layer && layer.title) || "OpenStreetMap", visible: layer ? layer.visible : true, opacity: layer ? layer.opacity : 1,
    copyright: "Map data © OpenStreetMap contributors",
  });
}

function scFixOsm(basemap) {
  const bases = basemap ? basemap.baseLayers : null;
  for (const layer of (bases ? bases.toArray() : [])) {
    if (layer.type === "open-street-map") bases.splice(bases.indexOf(layer), 1, scOsmLayer(layer));
  }
  return basemap;
}

async function scLoadGallery(self) {
  const entries = [];
  try {
    const query = typeof self?.basemapGalleryGroupQuery === "string" ? self.basemapGalleryGroupQuery : "";
    if (query) {
      const groups = await scPortal(`/community/groups?q=${encodeURIComponent(query)}&num=1`);
      const group = groups && Array.isArray(groups.results) ? groups.results[0] : null;
      if (group) {
        const found = await scPortal(`/search?num=100&q=${encodeURIComponent(`group:${group.id} AND type:"Web Map"`)}`);
        const read = await Promise.all((found.results || []).filter(i => i.type === "Web Map").map(item =>
          scPortal(`/content/items/${encodeURIComponent(item.id)}/data`)
            .then(data => data && data.baseMap && (data.baseMap.baseMapLayers || []).length
              ? { key: `gallery:${item.id}`, title: item.title || data.baseMap.title || "Basemap", json: data.baseMap } : null)
            .catch(() => null)));
        entries.push(...read.filter(Boolean).sort((a, b) => a.title.localeCompare(b.title)));
      }
    }
  } catch { /* no gallery rather than no basemap */ }
  if (self && self.defaultBasemap && (self.defaultBasemap.baseMapLayers || []).length) {
    const key = JSON.stringify(self.defaultBasemap.baseMapLayers.map(l => l.styleUrl || l.url || l.layerType));
    if (!entries.some(e => JSON.stringify((e.json.baseMapLayers || []).map(l => l.styleUrl || l.url || l.layerType)) === key)) {
      entries.unshift({ key: "ground", title: self.defaultBasemap.title || "Map ground", json: self.defaultBasemap });
    }
  }
  // OpenStreetMap once: the portal's default is often OpenStreetMap already (design review).
  const drawsOsm = e => (e.json.baseMapLayers || []).some(l => l.layerType === "OpenStreetMap" || /openstreetmap\.org/.test(l.urlTemplate || l.templateUrl || ""));
  if (!entries.some(drawsOsm)) entries.push({ key: "osm", title: "OpenStreetMap", json: { title: "OpenStreetMap",
    baseMapLayers: [{ id: "OpenStreetMap", layerType: "OpenStreetMap", title: "OpenStreetMap", visibility: true, opacity: 1 }] } });
  entries.push({ key: "none", title: "None", json: { title: "No basemap", baseMapLayers: [] } });
  scState.gallery = entries;
}

/** Which gallery entry the scene's basemap is, by what its layers draw. */
function scBasemapKey() {
  const basemap = scState.scene.basemap;
  if (!basemap || basemap.baseLayers.length === 0) return "none";
  const drawn = JSON.stringify(basemap.baseLayers.toArray().map(l => l.styleUrl || l.url || l.urlTemplate || l.type));
  for (const entry of scState.gallery) {
    const listed = JSON.stringify((entry.json.baseMapLayers || []).map(l => l.styleUrl || l.url || l.layerType));
    if (listed === drawn) return entry.key;
  }
  if (basemap.baseLayers.length === 1 && /tile\.openstreetmap\.org/.test(basemap.baseLayers.getItemAt(0).urlTemplate || "")) {
    const osm = scState.gallery.find(e => (e.json.baseMapLayers || []).some(l => l.layerType === "OpenStreetMap"));
    return osm ? osm.key : "osm";
  }
  return "own";
}

function scDrawBasemaps() {
  const current = scBasemapKey();
  const edit = scCanEdit();
  const named = (scState.gallery.find(e => e.key === current) || {}).title
    || (scState.scene.basemap && scState.scene.basemap.title) || "this scene's own";
  sc$("baseList").hidden = !edit;
  sc$("baseReadOnly").hidden = edit;
  sc$("baseReadOnly").textContent = current === "none" ? "This scene has no basemap. Only its owner can change it."
    : `The basemap is ${named}. Only the scene's owner can change it.`;
  sc$("baseList").innerHTML = [
    ...(current === "own" ? [{ key: "own", title: `${(scState.scene.basemap && scState.scene.basemap.title) || "This scene's own basemap"}` }] : []),
    ...scState.gallery,
  ].map(entry => `<li><button type="button" data-base="${scEscape(entry.key)}" aria-pressed="${entry.key === current}"
      ${edit ? "" : "disabled"}>${scEscape(entry.title)}</button></li>`).join("");
}

sc$("baseList").addEventListener("click", event => {
  const button = event.target.closest("[data-base]");
  if (!button || !scCanEdit()) return;
  const entry = scState.gallery.find(e => e.key === button.dataset.base);
  if (!entry) return;
  scState.scene.basemap = scFixOsm(scState.sdk.Basemap.fromJSON(entry.json));
  scMarkDirty();
  scDrawBasemaps();
  sc$("baseList").querySelector(`[data-base="${CSS.escape(entry.key)}"]`).focus();
  scSay(`The basemap is ${entry.title}.`);
});

// ---------------------------------------------------------------------------------------------------------------------
// The ground: elevation, opacity, and whether the camera may go below it.

sc$("groundSelect").addEventListener("change", async event => {
  const url = event.target.value;
  const ground = scState.scene.ground;
  ground.layers.removeAll();
  scMarkDirty();
  if (!url) { scSay("The ground is flat."); return; }
  try {
    const elevation = new scState.sdk.ElevationLayer({ url });
    await elevation.load();
    ground.layers.add(elevation);
    scSay(`The ground is ${elevation.title || "the chosen surface"}.`);
  } catch (e) {
    event.target.value = "";
    scSay(`That service is not an elevation surface: ${e.message || "it could not be read as one"}.`, true);
  }
});

sc$("groundOpacity").addEventListener("input", event => {
  scState.scene.ground.opacity = Number(event.target.value) / 100;
  sc$("groundOpacitySays").textContent = `${event.target.value}%`;
  scMarkDirty();
});

// Scene Viewer's quality setting: this reader's, kept in this browser, not in the scene (ADR-270 §5.10).
sc$("sceneQuality").addEventListener("change", event => {
  scState.view.qualityProfile = event.target.value;
  try { localStorage.setItem("graticula-scene-quality", event.target.value); } catch { /* private mode */ }
  scSay(`Drawing quality is ${event.target.value}.`);
});

sc$("groundUnder").addEventListener("change", event => {
  // Below the ground only when asked: Scene Viewer's own default keeps the camera above it.
  scState.scene.ground.navigationConstraint = { type: event.target.checked ? "none" : "stay-above" };
  scMarkDirty();
  scSay(event.target.checked ? "The camera may go below the ground." : "The camera stays above the ground.");
});

function scDrawGround() {
  const ground = scState.scene.ground;
  const opacity = Math.round((ground.opacity ?? 1) * 100);
  sc$("groundOpacity").value = String(opacity);
  sc$("groundOpacitySays").textContent = `${opacity}%`;
  sc$("groundUnder").checked = !!(ground.navigationConstraint && ground.navigationConstraint.type === "none");
  const edit = scCanEdit();
  sc$("groundControls").hidden = !edit;
  sc$("groundReadOnly").hidden = edit;
  const surface = ground.layers.getItemAt(0);
  sc$("groundReadOnly").textContent = `The ground is ${surface ? surface.title || "an elevation surface" : "flat"}${
    opacity < 100 ? `, ${opacity}% opaque` : ""}${sc$("groundUnder").checked ? ", and the camera may go below it" : ""}. Only the scene's owner can change it.`;
}

// ---------------------------------------------------------------------------------------------------------------------
// Global or local, and a local scene's clipping — ADR-270 §5.7.

/** The scene type, which a new scene may still change (the page starts again in the other), and a local scene's clip. */
function scDrawMode() {
  const select = sc$("sceneMode");
  select.value = scState.mode;
  // Chosen when a scene is new and empty, as Scene Viewer asks it: a saved scene keeps its type.
  const fresh = !scState.id && scState.scene.layers.length === 0;
  select.disabled = !fresh;
  sc$("sceneModeRow").hidden = !scCanEdit() || (!fresh && scState.mode === "global");
  const local = scState.mode === "local";
  sc$("clipPart").hidden = !local || !scCanEdit();
  const clipped = local && scState.scene.clippingEnabled && scState.scene.clippingArea;
  sc$("clipSays").textContent = clipped ? "The scene is clipped to an area." : "The scene is not clipped.";
  sc$("clipNone").disabled = !clipped;
}

sc$("sceneMode").addEventListener("change", event => {
  // A new view in the other mode: the page starts again, empty, as the type is chosen before anything is added.
  location.search = event.target.value === "local" ? "?mode=local" : "";
});

sc$("clipToView").addEventListener("click", () => {
  const area = scState.view.extent.clone();
  scState.scene.clippingArea = area;
  scState.scene.clippingEnabled = true;
  scState.view.clippingArea = area;
  scMarkDirty();
  scDrawMode();
  scSay("The scene is clipped to the current view.");
});

sc$("clipNone").addEventListener("click", () => {
  scState.scene.clippingEnabled = false;
  scState.view.clippingArea = null;
  scMarkDirty();
  scDrawMode();
  scSay("The scene shows everything again.");
  sc$("clipToView").focus();
});

// ---------------------------------------------------------------------------------------------------------------------
// Slides: a camera and what was on, captured, renamed, reordered, removed and played — `presentation.slides`.

function scSlides() {
  return scState.scene.presentation.slides;
}

function scDrawSlides() {
  const edit = scCanEdit();
  // A slide hidden from the presentation is its author's to see, not a reader's (Scene Viewer's Hide in presentation).
  const slides = scSlides().toArray().filter(slide => edit || !slide.hidden);
  sc$("slideAdd").hidden = !edit;
  sc$("slidePlay").disabled = slides.filter(s => !s.hidden).length < 2 && !scState.playing;
  sc$("slidePlay").textContent = scState.playing ? "Stop" : "Play";
  sc$("slidePlay").title = sc$("slidePlay").disabled ? "Two slides in the presentation are needed to play it" : "";
  sc$("slideList").innerHTML = slides.length === 0
    ? `<li class="hint" style="border:0;padding:0;display:block">${edit ? "No slides yet. Move the camera where you want it and capture one." : "This scene has no slides."}</li>`
    : slides.map((slide, at) => `
      <li data-slide="${scEscape(slide.id)}" class="${scState.currentSlide === slide.id ? "current" : ""}">
        <button type="button" class="noimg" data-act="go" aria-label="Go to ${scEscape(slide.title.text || `slide ${at + 1}`)}">${
          slide.thumbnail && slide.thumbnail.url ? `<img src="${scEscape(slide.thumbnail.url)}" alt="">` : ""}</button>
        ${edit ? `<input type="text" data-act="rename" value="${scEscape(slide.title.text || "")}" aria-label="Title of slide ${at + 1}" maxlength="120">
          <textarea data-act="describe" rows="2" maxlength="1000" placeholder="Description (optional)" aria-label="Description of slide ${at + 1}"
            style="grid-column:2">${scEscape((slide.description && slide.description.text) || "")}</textarea>`
          : `<button type="button" class="linkish" data-act="go">${scEscape(slide.title.text || `Slide ${at + 1}`)}</button>${
            slide.description && slide.description.text ? `<p class="hint" style="grid-column:2;margin:0">${scEscape(slide.description.text)}</p>` : ""}`}
        ${edit ? `<div class="row">
          <button type="button" class="tiny" data-act="up" ${at === 0 ? "disabled" : ""} aria-label="Move slide ${at + 1} earlier">▲</button>
          <button type="button" class="tiny" data-act="down" ${at === slides.length - 1 ? "disabled" : ""} aria-label="Move slide ${at + 1} later">▼</button>
          <button type="button" class="tiny" data-act="update" aria-label="Update slide ${at + 1} to the current view">Update</button>
          <button type="button" class="tiny" data-act="duplicate" aria-label="Duplicate slide ${at + 1}">Duplicate</button>
          <label class="check" style="margin:0;font-size:12px"><input type="checkbox" data-act="shown" ${slide.hidden ? "" : "checked"}> In presentation</label>
          <label style="font-size:12px">Shown as <select data-act="layout" aria-label="How slide ${at + 1} is shown when played">${
            [["caption", "Caption"], ["cover", "Cover"], ["none", "Scene only"]].map(([v, t]) =>
              `<option value="${v}" ${(slide.layout || "caption") === v ? "selected" : ""}>${t}</option>`).join("")}</select></label>
          <button type="button" class="tiny danger" data-act="delete" aria-label="Delete slide ${at + 1}">Delete</button>
        </div>` : ""}
      </li>`).join("");
}

sc$("slideAdd").addEventListener("click", async () => {
  if (!scCanEdit()) return;
  const button = sc$("slideAdd");
  button.disabled = true;
  try {
    const slide = await scState.sdk.Slide.createFrom(scState.view);
    await scCaptureElements(slide);
    slide.title.text = `Slide ${scSlides().length + 1}`;
    scSlides().add(slide);
    scState.currentSlide = slide.id;
    scMarkDirty();
    scDrawSlides();
    scSay(`${slide.title.text} was captured.`);
    sc$("slideList").querySelector(`[data-slide="${CSS.escape(slide.id)}"] [data-act=rename]`)?.focus();
  } catch (e) {
    scSay(`The slide could not be captured: ${e.message}`, true);
  } finally {
    button.disabled = false;
  }
});

/**
 * What a slide keeps beside its camera, as Scene Viewer's slide does (ADR-270 §5.19, SDK 4.34's `Slide.elements`): the
 * slice that is open, whether the legend is shown, and the pop-up that is open — its location and features by layer
 * and object id.
 */
async function scCaptureElements(slide) {
  const slice = scState.tool === "slice" && scState.tools.slice && scState.tools.slice.analysis;
  const popup = scState.view.popup;
  const features = popup && popup.visible ? (popup.features || []).filter(f => f.layer && typeof f.getObjectId === "function" && f.getObjectId() != null) : [];
  slide.elements = {
    analyses: slice && slice.shape ? [slice.clone()] : [],
    legendInfo: { visible: !sc$("pane-legend").hidden },
    popupInfo: features.length ? { location: popup.location, selectedFeatureIndex: Math.max(0, popup.selectedFeatureIndex || 0),
      features: features.map(f => ({ id: { type: "object-id", value: f.getObjectId() }, layerReference: { layerId: f.layer.id } })) } : null,
  };
}

/** Puts a slide's elements in the view: its slice, its legend and its pop-up, and takes the last slide's slice away. */
async function scApplyElements(slide) {
  for (const old of scState.slideAnalyses || []) scState.view.analyses.remove(old);
  scState.slideAnalyses = [];
  const elements = slide.elements;
  if (!elements) return;
  for (const analysis of elements.analyses || []) {
    const shown = analysis.clone();
    scState.view.analyses.add(shown);
    scState.slideAnalyses.push(shown);
  }
  if (elements.legendInfo && elements.legendInfo.visible) scShowTab("legend");
  const info = elements.popupInfo;
  scState.view.closePopup();
  if (info && info.features && info.features.length) {
    const found = [];
    for (const ref of info.features) {
      const layer = scState.scene.layers.find(l => l.id === (ref.layerReference && ref.layerReference.layerId));
      if (!layer || typeof layer.queryFeatures !== "function" || !ref.id) continue;
      const result = await layer.queryFeatures({ objectIds: [ref.id.value], outFields: ["*"], returnGeometry: true }).catch(() => null);
      if (result) found.push(...result.features);
    }
    if (found.length) scState.view.openPopup({ features: found, location: info.location || undefined });
  }
}

async function scGoToSlide(slide) {
  scState.currentSlide = slide.id;
  scDrawSlides();
  await slide.applyTo(scState.view, { animate: !matchMedia("(prefers-reduced-motion: reduce)").matches }).catch(() => {});
  await scApplyElements(slide).catch(() => {});
  // Only the checkboxes follow the slide: rebuilding the list would close an open layer's settings (design review).
  for (const box of document.querySelectorAll("#layerList [data-act=visible]")) {
    const layer = scLayerOf(box);
    if (layer) box.checked = layer.visible;
  }
}

sc$("slideList").addEventListener("click", async event => {
  const button = event.target.closest("button[data-act]");
  if (!button) return;
  const row = button.closest("[data-slide]");
  const slides = scSlides();
  const slide = slides.find(s => s.id === row.dataset.slide);
  if (!slide) return;
  const act = button.dataset.act;
  if (act === "go") {
    await scGoToSlide(slide);
  } else if (act === "up" || act === "down") {
    slides.reorder(slide, slides.indexOf(slide) + (act === "up" ? -1 : 1));
    scMarkDirty();
    scDrawSlides();
  } else if (act === "update") {
    await slide.updateFrom(scState.view);
    await scCaptureElements(slide);
    scMarkDirty();
    scDrawSlides();
    scSay(`${slide.title.text || "The slide"} now keeps the current view.`);
  } else if (act === "duplicate") {
    // A copy of its own, placed after it — Scene Viewer's Duplicate slide.
    const copy = slide.clone();
    copy.id = `slide_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 7)}`;
    copy.title.text = `${slide.title.text || "Slide"} (copy)`;
    slides.add(copy, slides.indexOf(slide) + 1);
    scMarkDirty();
    scDrawSlides();
    scSay(`${copy.title.text} was made.`);
    sc$("slideList").querySelector(`[data-slide="${CSS.escape(copy.id)}"] [data-act=rename]`)?.focus();
  } else if (act === "delete") {
    slides.remove(slide);
    scMarkDirty();
    scDrawSlides();
    scSay(`${slide.title.text || "The slide"} was deleted.`);
    (sc$("slideList").querySelector("button, input") || sc$("slideAdd")).focus();
  }
});

sc$("slideList").addEventListener("change", event => {
  const act = event.target.dataset.act;
  if (act !== "rename" && act !== "describe") return;
  const slide = scSlides().find(s => s.id === event.target.closest("[data-slide]").dataset.slide);
  if (!slide) return;
  if (act === "rename") slide.title.text = event.target.value.trim() || slide.title.text;
  else slide.description = { text: event.target.value.trim() };
  scMarkDirty();
});

// How a slide is shown when played: under a caption, as a cover over the scene, or the scene alone (SDK 4.34's
// Slide.layout, ADR-270 §5.19).
sc$("slideList").addEventListener("change", event => {
  if (event.target.dataset.act !== "layout") return;
  const slide = scSlides().find(s => s.id === event.target.closest("[data-slide]").dataset.slide);
  if (!slide) return;
  slide.layout = event.target.value;
  scMarkDirty();
  scSay(`${slide.title.text || "The slide"} is shown ${({ caption: "with a caption", cover: "as a cover", none: "as the scene only" })[slide.layout]} when played.`);
});

sc$("slideList").addEventListener("change", event => {
  if (event.target.dataset.act !== "shown") return;
  const slide = scSlides().find(s => s.id === event.target.closest("[data-slide]").dataset.slide);
  if (!slide) return;
  slide.hidden = !event.target.checked;
  scMarkDirty();
  scSay(`${slide.title.text || "The slide"} is ${slide.hidden ? "left out of" : "in"} the presentation.`);
});

/** Plays the slides in order, one every few seconds, until Stop or the last one. */
sc$("slidePlay").addEventListener("click", async () => {
  if (scState.playing) { scStopPlaying(); return; }
  const slides = scSlides().toArray().filter(slide => !slide.hidden);
  if (slides.length === 0) return;
  const run = scState.playing = { at: 0 };
  scDrawSlides();
  while (scState.playing === run && run.at < slides.length) {
    scCaption(slides[run.at], run.at, slides.length);
    await scGoToSlide(slides[run.at]);
    run.at++;
    if (run.at < slides.length) await new Promise(resolve => setTimeout(resolve, Number(window.__slideSeconds ?? 4) * 1000));
  }
  if (scState.playing === run) scStopPlaying("The slides were played.");
});

/**
 * The caption Play shows over the scene — the slide's title and description and where it is in the presentation — as
 * Scene Viewer's Present mode captions each slide (ADR-270 §5.16). Placed in the view's own corner, over the scene.
 */
function scCaption(slide, at, count) {
  let box = sc$("slideCaption");
  if (!box) {
    box = document.createElement("div");
    box.id = "slideCaption";
    // Seen, not spoken: the header's status announces each slide, the page's one live region (design review).
    scState.view.ui.add(box, "manual");
    box.addEventListener("click", event => { if (event.target.id === "captionStop") scStopPlaying(); });
  }
  const layout = slide ? slide.layout || "caption" : "none";
  box.hidden = !slide;
  box.classList.toggle("cover", layout === "cover");
  box.classList.toggle("bare", layout === "none");
  if (!slide) return;
  const title = slide.title.text || "Untitled slide";
  const description = slide.description && slide.description.text;
  box.innerHTML = `<div aria-hidden="true"><span class="where">Slide ${at + 1} of ${count}</span><strong>${scEscape(title)}</strong>${
    description ? `<p>${scEscape(description)}</p>` : ""}</div><button type="button" class="tiny" id="captionStop">Stop</button>`;
  scSay(`Slide ${at + 1} of ${count}: ${title}.`);
}

// Escape stops a presentation, as it closes a tool.
document.addEventListener("keydown", event => { if (event.key === "Escape" && scState.playing) scStopPlaying(); });

function scStopPlaying(said = "The slides stopped.") {
  scCaption(null);
  scState.playing = null;
  scDrawSlides();
  scSay(said);
}

// ---------------------------------------------------------------------------------------------------------------------
// The legend: the SDK's own, which follows every renderer in the scene.

async function scEnsureLegend() {
  if (scState.legend || !scState.view) return;
  const [Legend] = await scRequire(["esri/widgets/Legend"]);
  scState.legend = new Legend({ view: scState.view, container: sc$("legendBox") });
}

// ---------------------------------------------------------------------------------------------------------------------
// Analysis: the SDK's 3D tools, one open at a time, in a panel on the scene (ADR-270).

const SC_TOOLS = [
  { key: "distance", label: "Distance", module: "esri/widgets/DirectLineMeasurement3D" },
  { key: "area", label: "Area", module: "esri/widgets/AreaMeasurement3D" },
  { key: "sight", label: "Line of sight", module: "esri/widgets/LineOfSight" },
  { key: "slice", label: "Slice", module: "esri/widgets/Slice" },
  { key: "shadow", label: "Shadows", module: "esri/widgets/ShadowCast" },
  { key: "daylight", label: "Daylight", module: "esri/widgets/Daylight" },
  { key: "weather", label: "Weather", module: "esri/widgets/Weather" },
  { key: "profile", label: "Elevation profile", module: "esri/widgets/ElevationProfile" },
  // Scene Viewer's Location and feature search (ADR-270 §5.10): the SDK's coordinate widget, and its search widget over the
  // scene's own feature layers — no geocoder, which this server does not have.
  { key: "location", label: "Location", module: "esri/widgets/CoordinateConversion" },
  // ADR-270 §5.18: Scene Viewer's volume, viewshed and dimensions, which SDK 4.34 has as analyses with no widget
  // (ADR-273): each is a small panel of this page's over the SDK's own analysis and its interactive placement.
  { key: "volume", label: "Volume", analysis: "esri/analysis/VolumeMeasurementAnalysis",
    says: "Draw an area on the ground: the volume above and below a flat surface at its average height — cut, fill and net." },
  { key: "viewshed", label: "Viewshed", analysis: "esri/analysis/ViewshedAnalysis",
    says: "Click where an observer stands, then where they look: what they can see is green, what is hidden is red. Add as many as you need." },
  { key: "dimension", label: "Dimensions", analysis: "esri/analysis/DimensionAnalysis",
    says: "Click two points to put a length on the scene: between buildings, along a wall, up a height. Add as many as you need." },
  { key: "search", label: "Search", module: "esri/widgets/Search", when: l => l.type === "feature" },
  // Offered only when the scene has a layer they work on (scDrawConditionalTools).
  { key: "building", label: "Building explorer", module: "esri/widgets/BuildingExplorer", when: l => l.type === "building-scene" },
  { key: "time", label: "Time", module: "esri/widgets/TimeSlider", when: l => !!(l.timeInfo && l.timeInfo.fullTimeExtent) },
];

/** Shows a conditional tool's button only while a layer it works on is in the scene, closing it when the last goes. */
function scDrawConditionalTools() {
  const layers = scState.scene ? scState.scene.layers.toArray() : [];
  for (const tool of SC_TOOLS.filter(t => t.when)) {
    const button = document.querySelector(`#tools [data-tool=${tool.key}]`);
    if (!button) continue;
    const offered = layers.some(tool.when);
    button.hidden = !offered;
    if (!offered && scState.tool === tool.key) scToggleTool(tool.key);
  }
}

function scDrawToolbar() {
  const bar = document.createElement("div");
  bar.id = "tools";
  bar.setAttribute("role", "toolbar");
  bar.setAttribute("aria-label", "Analysis");
  // On a phone the tools fold behind one button (scene.html's media rule); on a wide screen the button is not drawn.
  bar.innerHTML = `<button type="button" id="toolsToggle" aria-expanded="false" aria-controls="tools">Tools</button>`
    + SC_TOOLS.map(t => `<button type="button" data-tool="${t.key}" aria-pressed="false">${scEscape(t.label)}</button>`).join("");
  const panel = document.createElement("div");
  panel.id = "toolPanel";
  scState.view.ui.add(bar, "top-right");
  scState.view.ui.add(panel, "top-right");
  bar.setAttribute("aria-orientation", "vertical");
  const roving = () => [...bar.querySelectorAll("button")].filter(b => b.offsetParent !== null);
  roving().forEach((b, i) => { b.tabIndex = i === 0 ? 0 : -1; });
  bar.addEventListener("keydown", event => {
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
    const buttons = roving();
    const at = buttons.indexOf(document.activeElement);
    const next = event.key === "Home" ? buttons[0] : event.key === "End" ? buttons[buttons.length - 1]
      : buttons[(at + (event.key === "ArrowDown" ? 1 : buttons.length - 1)) % buttons.length];
    event.preventDefault();
    buttons.forEach(b => { b.tabIndex = b === next ? 0 : -1; });
    next.focus();
  });
  bar.addEventListener("click", event => {
    if (event.target.closest("#toolsToggle")) {
      const open = bar.classList.toggle("open");
      sc$("toolsToggle").setAttribute("aria-expanded", String(open));
      return;
    }
    const button = event.target.closest("[data-tool]");
    if (!button) return;
    // Picking a tool folds the phone's list again, so the tool's panel has the scene to itself.
    bar.classList.remove("open");
    sc$("toolsToggle").setAttribute("aria-expanded", "false");
    scToggleTool(button.dataset.tool);
  });
}

async function scToggleTool(key) {
  const opening = scState.tool !== key;
  if (scState.tool) {
    const open = scState.tools[scState.tool];
    if (open) {
      // A measurement or analysis left in the scene when its tool closes is cleared, as Scene Viewer clears it.
      if (open.viewModel && typeof open.viewModel.clear === "function") open.viewModel.clear();
      open.destroy();
    }
    delete scState.tools[scState.tool];
    scState.tool = null;
  }
  for (const b of document.querySelectorAll("#tools [data-tool]")) b.setAttribute("aria-pressed", "false");
  const panel = sc$("toolPanel");
  panel.innerHTML = "";
  if (!opening) { scSay(`${SC_TOOLS.find(t => t.key === key).label} is closed.`); return; }
  const tool = SC_TOOLS.find(t => t.key === key);
  try {
    if (tool.analysis) {
      scState.tools[key] = await scAnalysisTool(tool, panel);
      scState.tool = key;
      document.querySelector(`#tools [data-tool=${key}]`).setAttribute("aria-pressed", "true");
      scSay(`${tool.label} is open.`);
      return;
    }
    const [Widget] = await scRequire([tool.module]);
    const container = document.createElement("div");
    panel.append(container);
    const options = { view: scState.view, container };
    if (key === "profile") {
      options.profiles = [{ type: "ground" }, { type: "input" }];
    }
    if (key === "search") {
      const searchable = scState.scene.layers.filter(l => l.type === "feature");
      options.includeDefaultSources = false;
      options.sources = searchable.map(layer => {
        const text = (layer.fields || []).filter(f => f.type === "string").map(f => f.name);
        return { layer, searchFields: text.length ? text : [layer.displayField || layer.objectIdField],
          displayField: layer.displayField || text[0] || layer.objectIdField, exactMatch: false, outFields: ["*"],
          name: layer.title, placeholder: `Search ${layer.title || "the layer"}` };
      }).toArray();
    }
    if (key === "building") {
      options.layers = scState.scene.layers.filter(l => l.type === "building-scene");
    }
    if (key === "time") {
      // The extent of every time-aware layer, stepped as the first one's own interval would be.
      const timed = scState.scene.layers.filter(l => l.timeInfo && l.timeInfo.fullTimeExtent);
      const start = Math.min(...timed.map(l => l.timeInfo.fullTimeExtent.start.getTime()));
      const end = Math.max(...timed.map(l => l.timeInfo.fullTimeExtent.end.getTime()));
      options.fullTimeExtent = { start: new Date(start), end: new Date(end) };
      options.timeVisible = true;
      options.mode = "time-window";
    }
    const close = document.createElement("button");
    close.type = "button";
    close.className = "tiny";
    close.id = "toolClose";
    close.textContent = "×";
    close.setAttribute("aria-label", `Close ${tool.label}`);
    close.addEventListener("click", () => { scToggleTool(key); document.querySelector(`#tools [data-tool=${key}]`)?.focus(); });
    panel.prepend(close);
    scState.tools[key] = new Widget(options);
    scState.tool = key;
    document.querySelector(`#tools [data-tool=${key}]`).setAttribute("aria-pressed", "true");
    scSay(`${tool.label} is open.`);
  } catch (e) {
    scSay(`${tool.label} could not be opened: ${e.message}`, true);
  }
}

/**
 * One of the analyses SDK 4.34 has without a widget (ADR-270 §5.18), as a tool: the analysis is added to the view, Start
 * begins its interactive placement, Done ends it, Clear empties it, and closing the tool takes it out of the scene — the
 * same life a widget's measurement has. Returns what scToggleTool destroys.
 */
async function scAnalysisTool(tool, panel) {
  const [Analysis, reactiveUtils] = await scRequire([tool.analysis, "esri/core/reactiveUtils"]);
  const analysis = new Analysis();
  scState.view.analyses.add(analysis);
  const analysisView = await scState.view.whenAnalysisView(analysis);
  let placing = null;
  const handles = [];
  const box = document.createElement("div");
  box.className = "esri-widget sc-analysis";
  box.innerHTML = `
    <button type="button" class="tiny" id="toolClose" aria-label="Close ${scEscape(tool.label)}">×</button>
    <h3>${scEscape(tool.label)}</h3>
    <p class="hint">${scEscape(tool.says)}</p>
    <div class="row"><button type="button" class="primary" id="anStart">Start</button>
      <button type="button" id="anDone" hidden>Done</button>
      <button type="button" id="anClear">Clear</button></div>
    <p class="said" id="anSays" role="status" aria-live="polite"></p>
    <dl id="anResult"></dl>`;
  panel.append(box);
  const says = text => { box.querySelector("#anSays").textContent = text; };
  const stop = () => {
    if (placing) placing.abort();
    placing = null;
    box.querySelector("#anStart").hidden = false;
    box.querySelector("#anDone").hidden = true;
  };
  const count = () => tool.key === "viewshed" ? analysis.viewsheds.length : tool.key === "dimension" ? analysis.dimensions.length : (analysis.geometry ? 1 : 0);

  box.querySelector("#toolClose").addEventListener("click", () => { scToggleTool(tool.key); document.querySelector(`#tools [data-tool=${tool.key}]`)?.focus(); });
  box.querySelector("#anStart").addEventListener("click", async () => {
    stop();
    placing = new AbortController();
    box.querySelector("#anStart").hidden = true;
    box.querySelector("#anDone").hidden = false;
    box.querySelector("#anDone").focus();
    says(tool.key === "volume" ? "Click the corners of the area; double-click to finish." : "Click in the scene to place it. Done when you have enough.");
    try {
      if (tool.key === "viewshed") await analysisView.createViewsheds({ signal: placing.signal });
      else if (tool.key === "dimension") await analysisView.createLengthDimensions({ signal: placing.signal });
      else await analysisView.place({ signal: placing.signal });
    } catch (e) {
      if (e && e.name !== "AbortError") says(`It could not be placed: ${e.message}`);
    }
    stop();
    if (!box.querySelector("#anSays").textContent.startsWith("It could not")) says(`${count()} placed.`);
  });
  box.querySelector("#anDone").addEventListener("click", () => { stop(); box.querySelector("#anStart").focus(); });
  box.querySelector("#anClear").addEventListener("click", () => {
    stop();
    if (tool.key === "viewshed") analysis.viewsheds.removeAll();
    else if (tool.key === "dimension") analysis.dimensions.removeAll();
    else analysis.geometry = null;
    box.querySelector("#anResult").innerHTML = "";
    says("Cleared.");
  });

  // A volume's result, in the units the SDK chose for the scene.
  if (tool.key === "volume") {
    const show = v => v ? `${Math.round(v.value).toLocaleString()} ${v.unit.replace(/^cubic-/, "cubic ").replace(/-/g, " ")}` : "—";
    handles.push(reactiveUtils.watch(() => analysisView.result, result => {
      box.querySelector("#anResult").innerHTML = result ? `
        <dt>Cut</dt><dd>${scEscape(show(result.cutVolume))}</dd>
        <dt>Fill</dt><dd>${scEscape(show(result.fillVolume))}</dd>
        <dt>Net</dt><dd>${scEscape(show(result.netVolume))}</dd>` : "";
    }));
    handles.push(reactiveUtils.watch(() => analysisView.error, error => { if (error) says(`Not measured: ${error.message}`); }));
  }

  return {
    analysis, analysisView,
    destroy() {
      stop();
      handles.forEach(h => h.remove());
      scState.view.analyses.remove(analysis);
      box.remove();
    },
  };
}

// Escape closes the open tool and gives focus back to its button, as webmap.js closes its side tool (design review).
document.addEventListener("keydown", event => {
  if (event.key !== "Escape" || !scState.tool) return;
  const key = scState.tool;
  scToggleTool(key);
  document.querySelector(`#tools [data-tool=${key}]`)?.focus();
});

// ---------------------------------------------------------------------------------------------------------------------
// Embedding (ADR-275): the code for a saved scene, when the operator has named a site that may frame it.

function scDrawEmbed() {
  const sites = window.GRATICULA_EMBED_ORIGINS || [];
  const part = sc$("embedPart");
  part.hidden = !scCanEdit() || !scState.id || sites.length === 0;
  if (part.hidden) return;
  const address = `${location.origin}/studio/scene-embed.html?id=${encodeURIComponent(scState.id)}`;
  sc$("embedCode").value = `<iframe src="${address}" width="800" height="500" style="border:0" allowfullscreen title="${scEscape(sc$("sceneTitle").value.trim() || "Scene")}"></iframe>`;
  sc$("embedSites").textContent = sites.join(", ");
}

sc$("embedCopy").addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(sc$("embedCode").value);
    sc$("embedSaid").textContent = "Copied.";
  } catch {
    sc$("embedCode").select();
    sc$("embedSaid").textContent = "Selected: copy it with your keyboard.";
  }
});

// ---------------------------------------------------------------------------------------------------------------------
// Saving.

sc$("sceneTitle").addEventListener("input", () => {
  sc$("title").textContent = sc$("sceneTitle").value.trim() || "New scene";
  scMarkDirty();
});

/** Saves the scene through the portal: addItem for a new one, update for one already kept. */
async function scSave() {
  if (scState.saving || !scState.user) return;
  const title = sc$("sceneTitle").value.trim();
  if (!title) {
    scSay("Give the scene a title before saving it.", true);
    sc$("sceneDetails").hidden = false;
    sc$("sceneTitle").focus();
    return;
  }
  scState.saving = true;
  scDrawSaved();
  scSay("Saving…");
  try {
    // Where the camera is now is where the scene opens next time.
    scState.scene.initialViewProperties.viewpoint = scState.view.viewpoint.clone();
    const text = JSON.stringify(scState.scene.toJSON());
    const owner = encodeURIComponent(scState.owner || scState.user);
    if (scState.id) {
      await scPortal(`/content/users/${owner}/items/${encodeURIComponent(scState.id)}/update`, { title, text });
    } else {
      const added = await scPortal(`/content/users/${encodeURIComponent(scState.user)}/addItem`,
        { type: "Web Scene", title, text, tags: "scene", typeKeywords: `ViewingMode-${scState.mode}` });
      scState.id = added.id;
      scState.owner = scState.user;
      history.replaceState(null, "", `?id=${encodeURIComponent(added.id)}`);
    }
    scState.dirty = false;
    scDrawEmbed();
    scSay(`Saved as “${title}”.`);
  } catch (e) {
    scSay(`Not saved: ${e.message}`, true);
  } finally {
    scState.saving = false;
    scDrawSaved();
  }
}

sc$("headSave").addEventListener("click", scSave);

window.addEventListener("beforeunload", event => {
  if (scState.dirty) { event.preventDefault(); event.returnValue = ""; }
});

// ---------------------------------------------------------------------------------------------------------------------

async function scStart() {
  try {
    await scLoadSdk();
  } catch (e) {
    scSay(`The scene cannot be drawn: the map SDK ${e.message}.`, true);
    return;
  }

  // Who reads this, and what the portal starts a scene on.
  const [self, me] = await Promise.all([
    scPortal("/portals/self").catch(() => ({})),
    scPortal("/community/self").catch(() => null),
  ]);
  scState.user = me && me.username ? me.username : null;
  // Signed in by the cookie alone (a new tab): a token of this tab's own, so Save can write.
  if (scState.user && !scToken) await scExchangeSession();
  if (!scState.user) sc$("studio").hidden = false;
  if (me && (me.role === "org_admin" || (me.privileges || []).includes("portal:admin:viewUsers"))) sc$("navOrganization").hidden = false;

  const [WebScene, SceneView, Layer, ElevationLayer, Basemap, esriConfig, WebTileLayer, Slide] = await scRequire([
    "esri/WebScene", "esri/views/SceneView", "esri/layers/Layer", "esri/layers/ElevationLayer", "esri/Basemap",
    "esri/config", "esri/layers/WebTileLayer", "esri/webscene/Slide"]);
  esriConfig.request.useIdentity = false;
  esriConfig.portalUrl = SC_PORTAL;
  scState.sdk = { Layer, ElevationLayer, Basemap, WebTileLayer, Slide };

  if (scState.id) {
    scState.scene = new WebScene({ portalItem: { id: scState.id, portal: { url: SC_PORTAL } } });
  } else {
    // The portal's default, or OpenStreetMap when it names none: a new globe without a basemap looked broken (review).
    const basemap = Basemap.fromJSON(self.defaultBasemap && (self.defaultBasemap.baseMapLayers || []).length ? self.defaultBasemap
      : { title: "OpenStreetMap", baseMapLayers: [{ id: "OpenStreetMap", layerType: "OpenStreetMap", title: "OpenStreetMap", visibility: true, opacity: 1 }] });
    // ADR-270 §5.7: a local scene is flat, in Web Mercator — the projection of every basemap this server offers, so the
    // ground draws; feature layers are projected to it by the SDK. A global scene is the whole earth, as it was.
    const local = scQuery.get("mode") === "local";
    scState.scene = new WebScene({ basemap, ground: {},
      initialViewProperties: local ? { viewingMode: "local", spatialReference: { wkid: 3857 } } : undefined });
  }

  try {
    await scState.scene.load();
  } catch (e) {
    sc$("title").textContent = "Scene not found";
    scSay(e.details && e.details.httpStatus === 404 || /not found|does not exist|inaccessible/i.test(e.message || "")
      ? "This scene does not exist, or it is not shared with you." : `The scene could not be read: ${e.message}`, true);
    return;
  }

  // <b>OpenStreetMap from its one address</b>: the SDK's own layer asks a., b. and c.tile.openstreetmap.org, which
  // the console's policy does not name and OpenStreetMap no longer recommends, and its address cannot be changed (it
  // is read-only). It is drawn as a tile layer of tile.openstreetmap.org, the address the Map Viewer asks.
  scFixOsm(scState.scene.basemap);

  const item = scState.scene.portalItem;
  if (item) {
    sc$("title").textContent = item.title || "Scene";
    sc$("sceneTitle").value = item.title || "";
    scState.owner = item.owner;
    scState.manages = !!scState.user && item.owner === scState.user;
  }
  sc$("sceneDetails").hidden = !scCanEdit();
  sc$("addPart").hidden = !scCanEdit();
  sc$("addNotYours").hidden = scCanEdit();
  // A reader who cannot change the scene has nothing to add: no Add tab (confirming review).
  sc$("tab-add").hidden = !scCanEdit();
  scDrawEmbed();
  // A result is a hosted layer of the reader's own added to this scene: offered to whoever may change the scene.
  sc$("tab-analysis").hidden = !scCanEdit() || !scToken || !window.GraticulaAnalysis;

  scState.mode = scState.scene.initialViewProperties.viewingMode === "local" ? "local" : "global";
  const quality = (() => { try { return localStorage.getItem("graticula-scene-quality"); } catch { return null; } })();
  sc$("sceneQuality").value = ["low", "medium", "high"].includes(quality) ? quality : "medium";
  scState.view = new SceneView({ container: "viewDiv", map: scState.scene, qualityProfile: sc$("sceneQuality").value,
    viewingMode: scState.mode,
    clippingArea: scState.mode === "local" && scState.scene.clippingEnabled ? scState.scene.clippingArea : null });
  // A camera the reader moved is a change worth saving; one the view settled on its own — a resize, a phone's first
  // layout — is not (design review: a reopened scene read "Not saved" on a phone with nothing done).
  for (const kind of ["pointerdown", "wheel", "keydown"]) {
    sc$("viewDiv").addEventListener(kind, () => { scState.moved = true; }, { passive: true });
  }
  scState.view.watch("stationary", stationary => {
    if (stationary && scState.moved && scState.opened && scCanEdit()) scMarkDirty();
    // The list that follows the view follows it when the camera settles.
    if (stationary && sc$("addInView").checked && scCanEdit() && !sc$("pane-add").hidden && !scState.playing) scSearchAdd();
  });
  try {
    await scState.view.when();
  } catch (e) {
    // A browser without WebGL cannot draw a scene at all; said, rather than left on "Loading" (ADR-269).
    scSay(`This browser cannot draw a 3D scene: ${e.message || "WebGL is not available"}. The scene itself is unharmed.`, true);
    return;
  }
  await Promise.all(scState.scene.layers.toArray().map(l => l.load().catch(() => null)));
  scDrawToolbar();
  scDrawConditionalTools();
  await scLoadGallery(self);
  scState.opened = true;

  scDrawLayers();
  scDrawSaved();
  scDrawBasemaps();
  scDrawGround();
  scDrawMode();
  scDrawSlides();
  scSaySummary();
  if (scCanEdit()) { scSearchAdd(); scFillGroundChoices(); }
  // A layer's look or a slide applied elsewhere changes what the panel says; a removed layer leaves the list.
  scState.scene.layers.on("change", () => { if (scState.opened) { scDrawLayers(); scDrawConditionalTools(); } });
  scState.scene.watch("basemap", () => scDrawBasemaps());

  // ADR-269: opened from a scene layer's page (`?layer=`), a new scene starts with that layer, framed.
  const start = scQuery.get("layer");
  if (start && !scState.id) {
    if (!sc$("sceneTitle").value) {
      sc$("sceneTitle").value = scQuery.get("title") || "";
      sc$("title").textContent = sc$("sceneTitle").value || "New scene";
    }
    const own = /\/rest\/services\/hosted\/([0-9a-f]{32})\/SceneServer/i.exec(start);
    await scAddUrl(start, scQuery.get("title") || "", own ? own[1] : undefined);
  }
  // Scene Viewer's address of a slide: `#3` opens the scene at its third slide (ADR-270 §5.9).
  const asked = /^#(\d+)$/.exec(location.hash);
  const opening = asked ? scSlides().getItemAt(Number(asked[1]) - 1) : null;
  if (opening) { scShowTab("slides"); await scGoToSlide(opening); }

  // A new scene with nothing in it starts on the Add tab: there is nothing else to do with it yet (first-run state).
  if (!scState.id && scState.scene.layers.length === 0 && scCanEdit()) scShowTab("add");
}

scStart();
