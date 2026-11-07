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
  selected: null, side: null, layerPart: "props", group: null, tool: null, tools: {}, playing: null, gallery: [] };

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
    // The SDK's widgets are Calcite components, which take their mode from an ancestor's class, not from the theme's
    // stylesheet: without it they drew white cards on the dark page (ADR-279's third look).
    const dark = document.documentElement.dataset.theme === "dark";
    document.body.classList.toggle("calcite-mode-dark", dark);
    document.body.classList.toggle("calcite-mode-light", !dark);
    // ADR-281: 5.x is loaded as a module (core.js), which still answers `require`; 4.x by its classic init.
    const script = document.createElement("script");
    const modular = !/\/4\.\d+\/$/.test(url);
    if (modular) script.type = "module";
    script.src = modular ? url + "core.js" : url;
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
    "vector-tile": "Vector tiles", "elevation": "Elevation", "voxel": "Voxels", "csv": "CSV points", "geojson": "GeoJSON",
    "integrated-mesh-3dtiles": "3D tiles", "gaussian-splat": "Gaussian splats" })[layer.type] || layer.type;
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

/** What the left panel is called while a tab has it — its tool's own name, as Scene Viewer heads its panels. */
const SC_TAB_TITLES = { add: "Add layers", layers: "Layers", basemap: "Basemap", ground: "Ground", slides: "Slides",
  legend: "Legend", scene: "Scene properties", more: "More" };

/** Whether the left panel is open: a column on a wide screen, a sheet over the scene on a phone (ADR-279). */
const SC_PHONE = window.matchMedia("(max-width: 760px)");
function scOpenPanel(open) {
  document.body.classList.toggle("lclosed", !open);
  document.body.classList.toggle("sheet", open && SC_PHONE.matches);
}

function scShowTab(name) {
  for (const tab of document.querySelectorAll("#tabs [role=tab]")) {
    const on = tab.id === `tab-${name}`;
    tab.setAttribute("aria-selected", on ? "true" : "false");
    tab.tabIndex = on ? 0 : -1;
    sc$(tab.getAttribute("aria-controls")).hidden = !on;
  }
  sc$("panelTitle").textContent = SC_TAB_TITLES[name] || name;
  scOpenPanel(true);
  if (name === "legend") scEnsureLegend();
  if (name === "add" && sc$("addInView").checked) scSearchAdd();

}

// The Layers panel's own buttons, as Studio's map has them.
sc$("layersAdd").addEventListener("click", () => { scShowTab("add"); sc$("addFilter").focus(); });
// Scene Viewer's group layers (ADR-270 §5.24): a group to put layers in, from the Layers panel.
sc$("layersGroup").addEventListener("click", async () => {
  const [GroupLayer] = await scRequire(["esri/layers/GroupLayer"]);
  const n = scState.scene.layers.filter(l => l.type === "group").length + 1;
  const group = new GroupLayer({ title: `Group ${n}`, visibilityMode: "independent" });
  scState.scene.layers.add(group);
  scMarkDirty();
  scDrawLayers();
  scSay(`Group ${n} was made. Put a layer in it from the layer's ⋯ menu.`);
});

sc$("layersZoomAll").addEventListener("click", () => {
  const extents = scLeaves().map(l => l.fullExtent).filter(Boolean);
  if (extents.length === 0) { scSay("No layer says where it is.", true); return; }
  scState.view.goTo({ target: extents, tilt: 55 }).catch(() => {});
});

// The open tab's button closes its panel, giving the scene the room, as Studio's map does (ADR-259).
sc$("tabs").addEventListener("click", event => {
  const tab = event.target.closest("[role=tab]");
  if (!tab) return;
  const open = !document.body.classList.contains("lclosed") && (!SC_PHONE.matches || document.body.classList.contains("sheet"));
  if (tab.getAttribute("aria-selected") === "true" && open) { scOpenPanel(false); return; }
  scShowTab(tab.id.slice(4));
});
sc$("panelClose").addEventListener("click", () => {
  scOpenPanel(false);
  document.querySelector("#tabs [aria-selected=true]")?.focus();
});
// A phone opens with the scene whole; a wide screen with the Layers panel. The toolbar runs along the bottom on a phone.
function scOrient() { sc$("tabs").setAttribute("aria-orientation", SC_PHONE.matches ? "horizontal" : "vertical"); }
scOrient();
SC_PHONE.addEventListener("change", scOrient);
if (SC_PHONE.matches) document.body.classList.remove("sheet");
// On a phone, Ground, Legend and Scene are behind More: a bottom bar holds five (design review).
sc$("paneMore").addEventListener("click", event => {
  const go = event.target.closest("[data-goto]");
  if (go) scShowTab(go.dataset.goto);
});

// The tab pattern as webmap.js has it: arrows move between the tabs that are shown, Home and End go to the ends.
sc$("tabs").addEventListener("keydown", event => {
  if (!["ArrowRight", "ArrowLeft", "ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
  const tabs = [...document.querySelectorAll("#tabs [role=tab]")].filter(t => !t.hidden);
  const at = tabs.findIndex(t => t.getAttribute("aria-selected") === "true");
  const forward = event.key === "ArrowRight" || event.key === "ArrowDown";
  const next = event.key === "Home" ? tabs[0] : event.key === "End" ? tabs[tabs.length - 1]
    : tabs[(at + (forward ? 1 : tabs.length - 1)) % tabs.length];
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
  if (!sc$("pane-analysis").hidden && scState.group === "server") scDrawAnalysis();
  const list = sc$("layerList");
  const layers = scState.scene.layers.toArray().filter(l => !l.scSourceOf && !scWidgetsOwn(l)).slice().reverse();
  sc$("layersActions").hidden = !scCanEdit();
  if (layers.length === 0) {
    sc$("layersCount").textContent = "0";
    list.innerHTML = `<li class="hint" style="border:0;padding:0">This scene has no layers yet.${
      scCanEdit() ? " <b>Add layer</b> lists the layers you can read." : ""}</li>`;
    scDrawLayerTools();
    return;
  }
  const edit = scCanEdit();
  sc$("layersCount").textContent = String(scLeaves().length);
  // ADR-279, as Studio's map draws a layer (ADR-259): show it, select it by its name, and ⋯ for what is done to it; what
  // it is given is in the right toolbar's panel. ADR-270 §5.24: a group is a row too, its layers inside it.
  const selected = scSelectedLayer();
  const groups = scState.scene.layers.toArray().filter(l => l.type === "group");
  const row = (layer, at, siblings, inGroup) => {
    const k = scEscape(layer.id);
    const title = scEscape(layer.title || (layer.type === "group" ? "Untitled group" : "Untitled layer"));
    const group = layer.type === "group";
    const mine = !group && selected && selected.id === layer.id;
    const children = group ? layer.layers.toArray().slice().reverse() : [];
    return `
    <li class="${layer.visible ? "" : "off"}${mine ? " sel" : ""}${group ? " group" : ""}" data-layer="${k}">
      <div class="lhead">
        <input type="checkbox" data-act="visible" aria-label="Show ${title}" ${layer.visible ? "checked" : ""}>
        ${group ? `<span class="lname gname"><span class="ltitle">${title}</span><span class="lkind">Group · ${children.length} ${children.length === 1 ? "layer" : "layers"}</span></span>`
          : `<button type="button" class="lname" data-act="select" aria-pressed="${mine ? "true" : "false"}" aria-controls="side">
          <span class="ltitle">${title}</span><span class="lkind">${scEscape(scKind(layer))}${
            layer.loadStatus === "failed" ? ` · <span class="lnote">could not be read</span>` : ""}</span></button>`}
        <button type="button" class="lmore" data-act="menu" aria-expanded="${scState.menu === layer.id}" aria-controls="menu-${k}"
          aria-label="Actions for ${title}">&hellip;</button>
      </div>
      <div class="lmenu" id="menu-${k}" role="group" aria-label="Actions for ${title}"${scState.menu === layer.id ? "" : " hidden"}>
        ${group ? (edit ? `<button type="button" data-act="rename">Rename</button>` : "") : `<button type="button" data-act="zoom">Zoom to</button>
        <button type="button" data-act="settings" aria-controls="side">Properties</button>`}
        ${edit ? `<button type="button" data-act="up" ${at === 0 ? "disabled" : ""}>Move up</button>
        <button type="button" data-act="down" ${at === siblings - 1 ? "disabled" : ""}>Move down</button>
        ${!group && inGroup ? `<button type="button" data-act="ungroup">Out of the group</button>` : ""}
        ${!group && !inGroup ? groups.map(g => `<button type="button" data-act="into" data-group-id="${scEscape(g.id)}">Into ${scEscape(g.title || "the group")}</button>`).join("") : ""}
        ${group ? `<button type="button" data-act="dissolve">Ungroup its layers</button>` : ""}
        <hr>
        <button type="button" class="danger" data-act="remove">${group ? "Remove the group and its layers" : "Remove"}</button>` : ""}
      </div>
      ${group ? `<ul class="glist">${children.map((c, i) => row(c, i, children.length, true)).join("")}</ul>` : ""}
    </li>`;
  };
  list.innerHTML = layers.map((layer, at) => row(layer, at, layers.length, false)).join("");
  // A group's own changes redraw the list, as the scene's do.
  for (const g of groups) {
    if (!g.__watched) { g.__watched = true; g.layers.on("change", () => { if (scState.opened) { scDrawLayers(); scDrawConditionalTools(); } }); }
  }

  // The right panel follows the scene's layers while it shows one of them.
  if (scState.side === "layer") scDrawLayerTool();
  scDrawLayerTools();
}

// ---------------------------------------------------------------------------------------------------------------------
// The selected layer's settings, in the right panel (ADR-279): one part at a time, the part its toolbar button names.

const SC_LAYER_PARTS = [["props", "Properties", "sliders"], ["look", "Styles", "style"], ["vis", "Filter", "filter"],
  ["popup", "Pop-ups", "popup"]];

/** The layer the right panel's settings are of: the one chosen, or the top of the list. */
function scSelectedLayer() {
  const leaves = scLeaves();
  if (leaves.length === 0) return null;
  const chosen = scFindLayer(scState.selected);
  return chosen && chosen.type !== "group" ? chosen : leaves[leaves.length - 1];
}

function scDrawLayerTool() {
  const box = sc$("layerTool");
  const layer = scSelectedLayer();
  const part = scState.layerPart || "props";
  sc$("sideTitle").textContent = scCanEdit() ? (SC_LAYER_PARTS.find(p => p[0] === part) || SC_LAYER_PARTS[0])[1] : "Layer details";
  if (!layer) {
    box.innerHTML = `<p class="hint">This scene has no layers yet.${scCanEdit() ? " Add one with Add, at the left." : ""}</p>`;
    return;
  }
  scState.selected = layer.id;
  const layers = scLeaves().slice().reverse();
  // Which layer, and which of its parts — the parts switch inside the panel too, since a phone's toolbar has one button
  // for all four (ADR-259 §3.5's pattern).
  box.innerHTML = `<div class="lhead"><label for="layerPick">Layer</label>
      <select id="layerPick">${layers.map(l => `<option value="${scEscape(l.id)}" ${l.id === layer.id ? "selected" : ""}>${scEscape(l.title || "Untitled layer")}</option>`).join("")}</select></div>
    ${scCanEdit() ? `<div id="layerParts" role="group" aria-label="Layer settings">${SC_LAYER_PARTS.map(([key, label]) =>
      `<button type="button" data-part="${key}" aria-pressed="${key === part}">${label}</button>`).join("")}</div>` : ""}
    ${scLayerDetail(layer)}`;
  box.dataset.show = part;
  for (const sec of box.querySelectorAll(".lsec")) sec.toggleAttribute("data-on", sec.dataset.section === part);
  if (!box.querySelector(".lsec[data-on]")) {
    box.querySelector("[data-detail]").insertAdjacentHTML("beforeend",
      `<p class="hint lsec" data-section="${part}" data-on>${scEscape(layer.title || "This layer")} has no ${part === "look" ? "look" : part === "popup" ? "pop-ups" : "settings"} to set here.</p>`);
  }
  scFillLayerDetails();
  const detail = box.querySelector("[data-detail]");
  if (detail) scSyncStyleForm(detail, layer);
}

/** Opens the right panel on a part of the selected layer's settings. */
function scOpenLayerPart(part, layerId) {
  if (scState.tool) scToggleTool(scState.tool, { keepSide: true });
  if (layerId) scState.selected = layerId;
  scState.layerPart = part;
  scState.side = "layer";
  sc$("toolPick").hidden = true;
  sc$("layerTool").hidden = false;
  scOpenSide(true);
  scDrawLayerTool();
  scDrawLayerTools();
  for (const row of sc$("layerList").querySelectorAll("[data-layer]")) {
    row.classList.toggle("selected", row.dataset.layer === scState.selected);
    row.querySelector("[data-act=settings]")?.setAttribute("aria-expanded", String(row.dataset.layer === scState.selected));
  }
}

sc$("layerTool").addEventListener("click", event => {
  const part = event.target.closest("#layerParts [data-part]");
  if (!part) return;
  scState.layerPart = part.dataset.part;
  scDrawLayerTool();
  scDrawLayerTools();
  sc$("layerParts").querySelector(`[data-part=${part.dataset.part}]`)?.focus();
});

sc$("layerTool").addEventListener("change", event => {
  if (event.target.id !== "layerPick") return;
  scState.selected = event.target.value;
  scDrawLayerTool();
  sc$("layerPick").focus();
});

/** Opens or closes the right panel; a column on a wide screen, a sheet on a phone. */
function scOpenSide(open) {
  scState.sideHidden = false;
  sc$("side").hidden = !open;
  document.body.classList.toggle("ropen", open);
  if (open && SC_PHONE.matches) scOpenPanel(false);
  if (open && innerWidth < 1600 && !SC_PHONE.matches) scOpenPanel(false);
  if (!open || scState.group !== "server") {
    // Leaving the analysis lets a run go on; its end is said in the header and its result still joins the scene.
    if (!sc$("pane-analysis").hidden) { sc$("pane-analysis").hidden = true; if (scState.analysisOpen) { scState.analysisOpen = false; window.GraticulaAnalysis?.closed(); } }
  }
  if (!open) { scState.side = null; scDrawLayerTools(); }
}

sc$("sideClose").addEventListener("click", () => {
  const was = scState.side === "layer" ? `[data-part-tool="${scState.layerPart}"]` : scState.group ? `[data-group="${scState.group}"]` : null;
  if (scState.tool) scToggleTool(scState.tool);
  scOpenSide(false);
  scState.group = null;
  scDrawLayerTools();
  (was && sc$("tools").querySelector(was))?.focus();
});

/** The settings of one layer, in parts (ADR-279): properties, look, pop-ups and visibility, shown one at a time. */
function scLayerDetail(layer) {
  const edit = scCanEdit();
  const geometry = scGeometry(layer);
  const id = scEscape(layer.id);
  const opacity = Math.round((layer.opacity ?? 1) * 100);
  // Title, opacity and Zoom to first; where it comes from folded; Remove last and apart (design review).
  const source = `<details class="lsource"><summary>Source</summary><p>${scEscape(scKind(layer))}${
    layer.url ? `<code>${scEscape(layer.url)}</code>` : ""}</p></details>`;
  let html = `<div class="ldetail" data-detail="${id}"><div class="lsec" data-section="props">`;
  if (!edit) return html + `<div class="row"><button type="button" class="tiny" data-act="zoom">Zoom to</button></div>${source}</div></div>`;

  html += `<label class="field" for="ttl-${id}">Title in this scene</label>
    <input type="text" id="ttl-${id}" data-act="title" value="${scEscape(layer.title || "")}" maxlength="250">
    <label class="field" for="op-${id}">Opacity <span data-says="opacity">${opacity}%</span></label>
    <input type="range" id="op-${id}" data-act="opacity" min="0" max="100" step="5" value="${opacity}">
    <div class="row" style="margin-top:10px"><button type="button" data-act="zoom">Zoom to</button></div>
    ${source}
    <div class="lremove"><button type="button" class="danger" data-act="remove" aria-label="Remove ${scEscape(layer.title || "this layer")} from the scene">Remove layer</button></div></div>`;

  if (scStylable(layer)) {
    html += `<div class="lsec" data-section="look"><h3>Look</h3><div data-part="style"></div>`;
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
    html += `</div><div class="lsec" data-section="popup"><h3>Pop-ups</h3><div data-part="popup"></div></div>`;
  }
  if (layer.type === "point-cloud" && layer.loadStatus === "loaded") html += `<div class="lsec" data-section="look">${scPointCloudForm(layer)}</div>`;
  html += `<div class="lsec" data-section="vis">${scVisibilityForm(layer)}</div>`;
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

scOnLayers("change", async event => {
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

scOnLayers("change", async event => {
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
    const layer = scFindLayer(detail.dataset.detail);
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
    sizeField: "", sizeScale: 1, colorBy: "single", colorField: "", groups: 0, own: false };
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
    style.groups = grouped.length;
  } else if (renderer.type !== "simple" || !symbol || !symbol.symbolLayers) { style.own = renderer.type !== "simple"; return style; }
  const symbolLayer = symbol.symbolLayers.getItemAt(0);
  if (!symbolLayer) return style;
  const color = symbolLayer.material && symbolLayer.material.color;
  if (color) style.color = color.toHex();
  style.shape = ({ extrude: "extrude", fill: geometry === "mesh" ? "mesh" : "flat", icon: "icon", line: "line", path: "tube" })[symbolLayer.type]
    || (symbolLayer.type === "object" && symbolLayer.resource && symbolLayer.resource.href && scGalleryModel(symbolLayer.resource.href)
      ? `model:${scGalleryModel(symbolLayer.resource.href).name}`
      : symbolLayer.type === "object" ? (symbolLayer.resource && symbolLayer.resource.primitive) || "sphere" : style.shape);
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

/**
 * The scene viewer's own 3D symbols (ADR-281 §5.3): glTF models this product makes (tools/scene-symbols.py), served
 * beside the page. A point drawn as one keeps the model's address in the web scene, as any glTF symbol is kept.
 */
let SC_GALLERY = [];
const SC_GALLERY_BASE = new URL("symbols/", location.href).href;
const scGalleryReady = fetch(SC_GALLERY_BASE + "gallery.json")
  .then(r => (r.ok ? r.json() : { items: [] }))
  .then(g => { SC_GALLERY = g.items || []; })
  .catch(() => { SC_GALLERY = []; });

/** The gallery model a symbol layer draws, by its address. */
function scGalleryModel(href) {
  return SC_GALLERY.find(m => href && href.replace(/[?#].*$/, "") === SC_GALLERY_BASE + m.href) || null;
}

/** The shapes a geometry may be drawn as. */
function scShapes(geometry) {
  if (geometry === "polygon") return [["flat", "Flat, draped"], ["extrude", "Extruded"]];
  if (geometry === "point") return [["icon", "Icon"], ["sphere", "Sphere"], ["cylinder", "Cylinder"], ["cone", "Cone"],
    ["cube", "Cube"], ["diamond", "Diamond"],
    ...SC_GALLERY.map(m => [`model:${m.name}`, `${m.title} (${m.category.toLowerCase()})`])];
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
      <select data-style="colorField" id="cf-${id}">${scColorFieldOptions(layer, style.colorBy)}</select>
      <label class="field" for="cg-${id}" data-part="groupsLabel">${style.colorBy === "unique" ? "Most common values shown" : "Classes"}</label>
      <input type="number" data-style="groups" id="cg-${id}" min="2" max="10" step="1" value="${style.groups || (style.colorBy === "unique" ? 10 : 5)}"></div>
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

// A model chosen is drawn at its own height until the reader says otherwise (ADR-281 §5.3).
scOnLayers("change", event => {
  if (!event.target.matches("[data-style=shape]")) return;
  const model = SC_GALLERY.find(m => `model:${m.name}` === event.target.value);
  const size = event.target.closest("[data-detail]")?.querySelector("[data-style=size]");
  if (model && size) size.value = String(model.height);
}, true);

// The field list follows the colouring chosen, before the look is applied with it.
scOnLayers("change", event => {
  if (!event.target.matches("[data-style=colorBy]")) return;
  const detail = event.target.closest("[data-detail]");
  const layer = scLayerOf(event.target);
  if (!detail || !layer) return;
  const row = detail.querySelector("[data-part=colorField]");
  row.hidden = event.target.value === "single";
  row.querySelector("select").innerHTML = scColorFieldOptions(layer, event.target.value);
  detail.querySelector("[data-part=colorSwatch]").hidden = event.target.value === "unique";
  // How many: the ten most common values, or five classes, unless the reader says otherwise (2 to 10).
  detail.querySelector("[data-part=groupsLabel]").textContent = event.target.value === "unique" ? "Most common values shown" : "Classes";
  detail.querySelector("[data-style=groups]").value = event.target.value === "unique" ? "10" : "5";
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
    colorField: colorBy === "single" ? "" : read("colorField"),
    groups: Math.min(10, Math.max(2, Math.round(Number(read("groups")) || (colorBy === "unique" ? 10 : 5)))) };
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
      .sort((a, b) => b.count - a.count).slice(0, Math.min(style.groups || SC_CATEGORIES.length, SC_CATEGORIES.length));
    if (common.length === 0) throw new Error(`${style.colorField} has no values to colour by`);
    return { type: "unique-value", field: style.colorField, visualVariables,
      defaultSymbol: symbolOf("#c9ced4"), defaultLabel: "Other",
      uniqueValueInfos: common.map((info, i) => ({ value: info.value, label: String(info.value), symbol: symbolOf(SC_CATEGORIES[i]) })) };
  }

  if (style.colorBy === "classes" && style.colorField) {
    const [classBreaks] = await scRequire(["esri/smartMapping/statistics/classBreaks"]);
    const found = await classBreaks({ layer, field: style.colorField, classificationMethod: "natural-breaks", numClasses: style.groups || 5, view: scState.view });
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
    const model = style.shape && style.shape.startsWith("model:") ? SC_GALLERY.find(m => `model:${m.name}` === style.shape) : null;
    symbol = { type: "point-3d", symbolLayers: [model
      // A model keeps its own colours under one colour, and is tinted when the layer is coloured by a field.
      ? { type: "object", resource: { href: SC_GALLERY_BASE + model.href }, height: style.size || model.height, anchor: "bottom",
        ...(style.colorBy && style.colorBy !== "single" ? { material } : {}) }
      : style.shape === "icon"
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

/**
 * Every layer of the scene that draws, inside groups too (ADR-270 §5.24): what the tools, the pickers and the lookups
 * read. A group is a folder of the list, not a layer a tool can use.
 */
/**
 * A layer a widget put in the map for itself, never the scene's (measured: the Editor's sketch layer, a hidden graphics
 * layer). A layer of the scene's own that is hidden from lists is still the scene's, and is kept.
 */
function scWidgetsOwn(layer) {
  return !layer.scSourceOf && (layer.internal === true || (layer.type === "graphics" && layer.listMode === "hide"));
}

function scLeaves(withSources = false) {
  if (!scState.scene) return [];
  const out = [];
  // A scene layer's feature layer, added while Edit is open (ADR-276 §5.4), is the editor's and not the scene's; so is
  // a layer a widget adds for itself, as the SDK's Editor adds its sketch layer.
  const walk = collection => collection.forEach(l => (l.type === "group" ? walk(l.layers)
    : (l.scSourceOf ? withSources : !scWidgetsOwn(l)) && out.push(l)));
  walk(scState.scene.layers);
  return out;
}

/** Any layer of the scene by its id, a group or one inside a group. */
function scFindLayer(id) {
  if (!scState.scene || !id) return null;
  let found = null;
  const walk = collection => collection.forEach(l => { if (!found && l.id === id) found = l; else if (!found && l.type === "group") walk(l.layers); });
  walk(scState.scene.layers);
  return found;
}

function scLayerOf(element) {
  const row = element.closest("[data-layer]") || element.closest("[data-detail]");
  const id = row && (row.dataset.layer || row.dataset.detail);
  return id ? scFindLayer(id) : null;
}

/** The layer list and the right panel's settings answer the same controls (ADR-279). */
function scOnLayers(type, handler, options) {
  sc$("layerList").addEventListener(type, handler, options);
  sc$("layerTool").addEventListener(type, handler, options);
}

scOnLayers("change", event => {
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

// A group's new name, as it is typed and when it is done.
sc$("layerList").addEventListener("change", event => {
  if (event.target.dataset.act !== "grouptitle") return;
  const group = scLayerOf(event.target);
  if (!group) return;
  group.title = event.target.value.trim() || group.title;
  scMarkDirty();
  scDrawLayers();
  scSay(`The group is called ${group.title}.`);
});

scOnLayers("input", event => {
  if (event.target.dataset.act !== "opacity") return;
  const layer = scLayerOf(event.target);
  if (!layer) return;
  layer.opacity = Number(event.target.value) / 100;
  event.target.closest("[data-detail]").querySelector("[data-says=opacity]").textContent = `${event.target.value}%`;
  scMarkDirty();
});

scOnLayers("click", async event => {
  const button = event.target.closest("button[data-act]");
  if (!button) return;
  const layer = scLayerOf(button);
  if (!layer) return;
  const act = button.dataset.act;
  // A layer moves among its own group's layers, or the scene's (ADR-270 §5.24).
  const owner = layer.parent && layer.parent.type === "group" ? layer.parent : scState.scene;
  const layers = owner.layers;
  if (act === "into" || act === "ungroup" || act === "dissolve" || act === "rename" || act === "newgroup") {
    scState.menu = null;
    if (act === "into") {
      const target = scFindLayer(button.dataset.groupId);
      scState.scene.layers.remove(layer);
      target.layers.add(layer);
      scSay(`${layer.title || "The layer"} is in ${target.title || "the group"}.`);
    } else if (act === "ungroup") {
      const group = layer.parent;
      group.layers.remove(layer);
      scState.scene.layers.add(layer, scState.scene.layers.indexOf(group) + 1);
      scSay(`${layer.title || "The layer"} is out of ${group.title || "the group"}.`);
    } else if (act === "dissolve") {
      const at = scState.scene.layers.indexOf(layer);
      const inside = layer.layers.toArray();
      layer.layers.removeAll();
      scState.scene.layers.remove(layer);
      scState.scene.layers.addMany(inside, at);
      scSay(`${layer.title || "The group"} was ungrouped.`);
    } else if (act === "rename") {
      const row = button.closest("[data-layer]");
      const name = row.querySelector(".gname .ltitle");
      name.innerHTML = `<input type="text" data-act="grouptitle" value="${scEscape(layer.title || "")}" maxlength="120" aria-label="Group name">`;
      name.querySelector("input").focus();
      return;
    }
    scMarkDirty();
    scDrawLayers();
    return;
  }
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
  } else if (act === "settings" || act === "select") {
    // Selected by its name, as in Studio's map: its settings in the right panel, the part already open or Properties.
    scState.menu = null;
    scOpenLayerPart(scState.side === "layer" ? scState.layerPart || "props" : "props", layer.id);
    sc$("layerList").querySelector(`[data-layer="${CSS.escape(layer.id)}"] [data-act=select]`)?.focus();
  } else if (act === "menu") {
    scState.menu = scState.menu === layer.id ? null : layer.id;
    scDrawLayers();
    const opened = sc$("layerList").querySelector(`[data-layer="${CSS.escape(layer.id)}"] .lmenu:not([hidden]) button`);
    (opened || sc$("layerList").querySelector(`[data-layer="${CSS.escape(layer.id)}"] [data-act=menu]`))?.focus();
  } else if (act === "zoom") {
    const extent = layer.fullExtent;
    if (extent) scState.view.goTo({ target: extent, tilt: 55 }).catch(() => {});
    else scSay(`${layer.title || "The layer"} does not say where it is.`, true);
  } else if (act === "remove") {
    const title = layer.title || "The layer";
    const at = layers.indexOf(layer);
    owner.layers.remove(layer);
    scState.menu = null;
    // The panel does not slide onto another layer, where the same Remove would act on it (design review).
    if (scState.selected === layer.id) { scState.selected = null; if (scState.side === "layer") scOpenSide(false); }
    scMarkDirty();
    scDrawLayers();
    scSaySummary();
    scOfferUndo(`${title} was removed.`, () => { owner.layers.add(layer, at); scMarkDirty(); scDrawLayers(); scSay(`${title} is back.`); });
    (sc$("layerList").querySelector("[data-act=select]") || sc$("tab-add")).focus();
  }
});

/** Says what was done with an Undo beside it in the header, for a few seconds (design review: Remove had no way back). */
function scOfferUndo(text, undo) {
  scSay(text);
  const status = sc$("status");
  const button = document.createElement("button");
  button.type = "button";
  button.className = "tiny";
  button.id = "undoLast";
  button.textContent = "Undo";
  button.addEventListener("click", () => { clearTimeout(scState.undoTimer); button.remove(); undo(); });
  sc$("undoLast")?.remove();
  status.after(button);
  clearTimeout(scState.undoTimer);
  scState.undoTimer = setTimeout(() => button.remove(), 10000);
}

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

scOnLayers("change", event => {
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
/** Whether a tileset's content is Gaussian splats: its glTF content declares KHR_gaussian_splatting. */
async function scIsSplatTileset(tileset) {
  try {
    const response = await fetch(tileset, { headers: scToken && new URL(tileset, location.href).origin === location.origin ? { Authorization: "Bearer " + scToken } : {} });
    const doc = await response.json();
    const content = (doc.extensions && doc.extensions["3DTILES_content_gltf"]) || {};
    return [...(content.extensionsUsed || []), ...(content.extensionsRequired || [])].some(e => /^KHR_gaussian_splatting/.test(e));
  } catch {
    return false;
  }
}

async function scAddUrl(url, title, itemId, kind) {
  const said = sc$("addSaid");
  said.className = "said";
  said.textContent = `Adding ${title || "the layer"}…`;
  try {
    // ADR-278: a 3D Tiles tileset is drawn by the SDK's 3D tiles layer, from its tileset.json — by its kind from the list,
    // or by an address that names a tileset or a 3DTilesServer.
    const tiles = kind === "3DTiles Service" || /\/3DTilesServer(\/tileset\.json)?\/?$|\/tileset\.json$/i.test(url);
    const tileset = tiles && !/tileset\.json$/i.test(url) ? `${url.replace(/\/+$/, "")}/tileset.json` : url;
    // ADR-281 §5.2: a tileset of Gaussian splats (glTF content with KHR_gaussian_splatting, as ArcGIS hosts them) is
    // drawn by the SDK's splat layer, from SDK 5; any other by its 3D tiles layer.
    const splats = tiles && await scIsSplatTileset(tileset);
    const layer = tiles
      ? new (await scRequire([splats ? "esri/layers/GaussianSplatLayer" : "esri/layers/IntegratedMesh3DTilesLayer"]))[0]({ url: tileset, title })
      : SC_FILES[kind]
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
    // ADR-270 §5.20: a 3D layer in a projected system other than the local scene's is drawn only in a local scene in its
    // own. A new, empty scene offers to start again in it, with the layer; a scene with layers says why.
    const ownWkid = layer.spatialReference && !layer.spatialReference.isGeographic && !layer.spatialReference.isWebMercator
      ? layer.spatialReference.wkid : null;
    if (scState.mode === "local" && ["scene", "building-scene", "point-cloud", "integrated-mesh", "voxel"].includes(layer.type)
        && ownWkid && ownWkid !== scState.view.spatialReference.wkid) {
      if (!scState.id && scState.scene.layers.length === 0) {
        said.className = "said";
        said.innerHTML = `${scEscape(layer.title || "This layer")} is in EPSG:${ownWkid}, and a 3D layer is drawn in a local scene in its own system.
          <a href="/studio/scene.html?mode=local&amp;wkid=${ownWkid}&amp;layer=${encodeURIComponent(url)}&amp;title=${encodeURIComponent(layer.title || "")}" id="restartIn">Start a local scene in EPSG:${ownWkid}</a> — basemaps in Web Mercator are not drawn in it.`;
        scSay(`${layer.title || "This layer"} needs a local scene in EPSG:${ownWkid}.`);
        scDrawAddItems();
        return;
      }
      throw new Error(`${layer.title || "this layer"} is in EPSG:${ownWkid}, and a 3D layer is drawn only in a local scene in its own system. Make a new local scene for it`);
    }
    // A layer that carries its own heights is placed at them; a flat one is draped on the ground.
    if (layer.type === "feature" && layer.hasZ) layer.elevationInfo = { mode: "absolute-height" };
    scState.scene.add(layer);
    // A layer the scene loads but cannot draw — a voxel layer the pinned SDK cannot place in this scene (ADR-270 §5.17)
    // — would otherwise sit in the list drawing nothing; its reason is said instead.
    layer.__drawable = scState.view.whenLayerView(layer).then(() => true, e => {
      // The SDK's own words are for a developer: kept in the console, not shown (design review).
      console.warn("Layer view not created", layer.title, e);
      if (!scFindLayer(layer.id)) return false;
      // Measured 2026-11-07 (ADR-270 §5.17, ADR-278): a voxel layer is drawn in a local scene in its own coordinate
      // system — this viewer's local scenes are Web Mercator. SDK 5.2 draws 3D Tiles 1.0 (b3dm) as well (ADR-281 §4).
      const why = layer.type === "voxel"
        ? `${layer.title || "The layer"} was added but can't be drawn here: a voxel layer is drawn in a local scene in its own coordinate system, and this viewer's local scenes are Web Mercator (EPSG:3857). Remove it from Layers.`
        : layer.type === "gaussian-splat"
        ? `${layer.title || "The layer"} was added but can't be drawn: the map SDK did not accept its tileset. Splats it draws are glTF with Gaussian splatting, as ArcGIS hosts them. Remove it from Layers.`
        : layer.type === "integrated-mesh-3dtiles"
        ? `${layer.title || "The layer"} was added but can't be drawn: the map SDK could not read its tiles. Remove it from Layers.`
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
    // One sentence, as the map says it (confirming review): what was added, then how many and whether saved.
    scSay(`${layer.title || "The layer"} was added — ${sc$("status").textContent.replace(/\.$/, "").replace(/^(\d)/, "$1")}.`);
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
  return !!scState.scene && scLeaves().some(l => (l.url || "").replace(/\/+$/, "") === plain
    || (l.parsedUrl && `${l.url}/${l.layerId}` === plain));
}

/** The header's line about the scene as it now is: whether it is new, and how many layers it has. */
function scSaySummary() {
  if (sc$("addStart")) sc$("addStart").hidden = !scCanEdit() || (scState.scene && scState.scene.layers.length > 0);
  const count = scState.scene.layers.length;
  // A new, empty scene says what to do first (design review: Save was off with no reason given).
  // The Add panel says where to start; the header does not say it again (confirming review).
  scSay(!scState.id && count === 0 ? "" : `${count === 0 ? "No layers" : count === 1 ? "1 layer" : `${count} layers`}${scState.id ? "" : ", not saved yet"}.`);
}

sc$("addUrlButton").addEventListener("click", () => {
  const url = sc$("addUrl").value.trim();
  if (!url) { sc$("addSaid").textContent = "Type a service address first."; sc$("addUrl").focus(); return; }
  scAddUrl(url);
});
sc$("addUrl").addEventListener("keydown", event => { if (event.key === "Enter") sc$("addUrlButton").click(); });

const SC_ADDABLE = ["Feature Service", "Scene Service", "3DTiles Service", "Image Service", "Map Service", "Vector Tile Service", "CSV", "GeoJson"];

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
  if (sc$("addFrom").value) parts.push(sc$("addFrom").value);
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
  return ({ "Scene Service": "Scene layer", "3DTiles Service": "3D tiles layer", "Feature Service": "Feature layer", "Image Service": "Imagery layer",
    "Map Service": "Map image layer", "Vector Tile Service": "Vector tile layer", "CSV": "CSV file", "GeoJson": "GeoJSON file" })[type] || type;
}

let scAddTimer = 0;
sc$("addFilter").addEventListener("input", () => { clearTimeout(scAddTimer); scAddTimer = setTimeout(scSearchAdd, 250); });
sc$("addType").addEventListener("change", scSearchAdd);
sc$("addMine").addEventListener("change", scSearchAdd);
sc$("addFrom").addEventListener("change", scSearchAdd);
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
  for (const layer of scLeaves()) {
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
 * Scene Viewer's Create layer (ADR-276 condition 2): an empty hosted layer with heights, made through the portal's own
 * createService as ArcGIS's clients make one, added to the scene and opened in Edit.
 */
sc$("createGo").addEventListener("click", async () => {
  const name = sc$("createName").value.trim();
  const said = sc$("addSaid");
  said.className = "said";
  if (!/^[A-Za-z][A-Za-z0-9_]{0,59}$/.test(name)) {
    said.className = "said bad";
    said.textContent = "Not created: a name starts with a letter and holds letters, digits and _.";
    sc$("createName").focus();
    return;
  }
  said.textContent = `Creating ${name}…`;
  try {
    // ADR-280: a 3D object layer and its scene layer, made together; the scene layer is added by its item, so the SDK
    // finds the 3D object layer it edits through.
    if (sc$("createKind").value === "mesh") {
      const response = await fetch("/content/meshes", { method: "POST", headers: { Authorization: "Bearer " + scToken, "Content-Type": "application/json" },
        body: JSON.stringify({ title: name }) });
      const mesh = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error((mesh.error && mesh.error.message) || `the server answered ${response.status}`);
      await scAddUrl(mesh.scene.url, name, mesh.scene.id, "Scene Service");
      const layer = scLeaves().find(l => (l.url || "").includes(mesh.scene.id) || l.portalItem?.id === mesh.scene.id);
      if (layer) {
        layer.scSourceAsked = true;
        layer.scSource = { id: mesh.scene.id, url: mesh.url + "/0", mesh: mesh.id };
        scDrawConditionalTools();
        scOpenGroup("edit");
      }
      sc$("createName").value = "";
      return;
    }
    const made = await scPortal(`/content/users/${encodeURIComponent(scState.user)}/createService`, {
      outputType: "featureService",
      createParameters: JSON.stringify({ name, spatialReference: { wkid: 4326 }, layers: [{
        name, type: "Feature Layer", geometryType: sc$("createKind").value, hasZ: true,
        fields: [{ name: "label", type: "esriFieldTypeString", alias: "Label", length: 255, nullable: true }] }] }),
    });
    const url = `${String(made.serviceurl || made.encodedServiceURL).replace(/\/+$/, "")}/0`;
    await scAddUrl(url, name, null, "Feature Service");
    const layer = scLeaves().find(l => scFeatureAddress(l) === url || l.url === url);
    if (layer) {
      layer.elevationInfo = { mode: "absolute-height" };
      await layer.load();
      // Edit only once the layer's capabilities are read, so the editor offers it.
      scDrawConditionalTools();
      scOpenGroup("edit");
    }
    sc$("createName").value = "";
  } catch (e) {
    said.className = "said bad";
    said.textContent = `Not created: ${e.message}`;
    scSay(said.textContent, true);
  }
});

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

// Scene Viewer's navigation settings, this reader's too: zooming with the mouse wheel, and one finger panning on a touch
// screen — off where the scene sits in a page that scrolls (ADR-270 §5.21, the SDK's view.navigation).
function scNavigation() {
  const read = (key, fallback) => { try { const v = localStorage.getItem(key); return v === null ? fallback : v === "true"; } catch { return fallback; } };
  const wheel = read("graticula-scene-wheel", true), touch = read("graticula-scene-touch", true);
  sc$("navWheel").checked = wheel;
  sc$("navTouch").checked = touch;
  if (scState.view) {
    scState.view.navigation.mouseWheelZoomEnabled = wheel;
    scState.view.navigation.browserTouchPanEnabled = touch;
  }
}
for (const [id, key, said] of [["navWheel", "graticula-scene-wheel", "Zooming with the mouse wheel"], ["navTouch", "graticula-scene-touch", "Panning with one finger"]]) {
  sc$(id).addEventListener("change", event => {
    try { localStorage.setItem(key, String(event.target.checked)); } catch { /* private mode */ }
    scNavigation();
    scSay(`${said} is ${event.target.checked ? "on" : "off"}.`);
  });
}

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
      const layer = scFindLayer(ref.layerReference && ref.layerReference.layerId);
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
  { key: "distance", group: "measure", label: "Distance", module: "esri/widgets/DirectLineMeasurement3D" },
  { key: "area", group: "measure", label: "Area", module: "esri/widgets/AreaMeasurement3D" },
  { key: "sight", group: "analyze", label: "Line of sight", module: "esri/widgets/LineOfSight" },
  { key: "slice", group: "analyze", label: "Slice", module: "esri/widgets/Slice" },
  { key: "shadow", group: "analyze", label: "Shadows", module: "esri/widgets/ShadowCast" },
  { key: "daylight", group: "environment", label: "Daylight", module: "esri/widgets/Daylight" },
  { key: "weather", group: "environment", label: "Weather", module: "esri/widgets/Weather" },
  { key: "profile", group: "analyze", label: "Elevation profile", module: "esri/widgets/ElevationProfile" },
  // Scene Viewer's Location and feature search (ADR-270 §5.10): the SDK's coordinate widget, and its search widget over the
  // scene's own feature layers — no geocoder, which this server does not have.
  { key: "location", group: "location", label: "Location", module: "esri/widgets/CoordinateConversion" },
  // ADR-270 §5.18: Scene Viewer's volume, viewshed and dimensions, which SDK 4.34 has as analyses with no widget
  // (ADR-273): each is a small panel of this page's over the SDK's own analysis and its interactive placement.
  { key: "volume", group: "measure", label: "Volume", analysis: "esri/analysis/VolumeMeasurementAnalysis",
    says: "Draw an area on the ground: the volume above and below a flat surface at its average height — cut, fill and net." },
  { key: "viewshed", group: "analyze", label: "Viewshed", analysis: "esri/analysis/ViewshedAnalysis",
    says: "Click where an observer stands, then where they look: what they can see is green, what is hidden is red. Add as many as you need." },
  // ADR-281 §5.4: slope, aspect and hillshade of one of this server's image services, worked out by the server's own
  // raster functions and draped on the scene as a layer.
  { key: "terrain", group: "analyze", label: "Terrain", custom: true },
  { key: "dimension", group: "measure", label: "Dimensions", analysis: "esri/analysis/DimensionAnalysis",
    says: "Click two points to put a length on the scene: between buildings, along a wall, up a height. Add as many as you need." },
  { key: "search", group: "search", label: "Search", module: "esri/widgets/Search", when: l => l.type === "feature" },
  // Offered only when the scene has a layer they work on (scDrawConditionalTools).
  { key: "building", group: "building", label: "Building explorer", module: "esri/widgets/BuildingExplorer", when: l => l.type === "building-scene" },
  { key: "time", group: "time", label: "Time", module: "esri/widgets/TimeSlider", when: l => !!(l.timeInfo && l.timeInfo.fullTimeExtent) },
  // ADR-276: Scene Viewer's Edit — the SDK's editor over the scene's layers the reader may edit, features placed in 3D
  // (with their height where the layer keeps one). The server decides who may edit; the layer's capabilities say it.
  { key: "edit", group: "edit", label: "Edit", module: "esri/widgets/Editor", when: l => scEditable(l) || !!l.scSource },
];

/**
 * The right toolbar's groups (ADR-279), as Scene Viewer groups its tools: each opens the right panel with its tools to
 * choose from, the first open. A group whose tools need a layer the scene does not have is not shown.
 */
const SC_GROUPS = [
  { key: "search", label: "Search", icon: "search" },
  { key: "measure", label: "Measure", icon: "ruler" },
  { key: "analyze", label: "3D analysis", icon: "sight" },
  // The server's analysis, where Studio's map has it (ADR-259): its own panel, analysis.js's (ADR-270 §5.14).
  { key: "server", label: "Analysis", icon: "analysis" },
  { key: "environment", label: "Light & weather", icon: "sun" },
  { key: "location", label: "Location", icon: "pin" },
  { key: "edit", label: "Edit", icon: "edit" },
  { key: "building", label: "Building", icon: "building" },
  { key: "time", label: "Time", icon: "clock" },
];

/** Whether the reader may edit a layer's features here: a feature layer whose service says it takes edits from them. */
function scEditable(layer) {
  return !SC_EMBED && !!scToken && layer.type === "feature" && layer.loaded && layer.editingEnabled !== false
    && !!(layer.capabilities && layer.capabilities.operations && layer.capabilities.operations.supportsEditing);
}

/**
 * ADR-276 §5.4: the feature layer a scene layer was made from here, for the scene layer's owner — the one who may rebuild
 * it. Asked once per layer; anyone else is answered no, and nothing is offered.
 */
function scLearnSources() {
  if (SC_EMBED || !scToken) return;
  for (const layer of scLeaves()) {
    const hosted = /\/rest\/services\/hosted\/([0-9a-f]{32})\/SceneServer/i.exec(layer.url || "");
    if (!hosted || layer.scSourceAsked) continue;
    layer.scSourceAsked = true;
    fetch(`/content/scenes/${hosted[1]}/source`, { headers: { Authorization: "Bearer " + scToken } })
      .then(r => (r.ok ? r.json() : null))
      .then(source => {
        if (!source || !source.exists) return;
        const service = source.service.split("/").map(encodeURIComponent).join("/");
        layer.scSource = { id: hosted[1], url: `${location.origin}/rest/services/${service}/FeatureServer/${source.layer}`, mesh: source.mesh || null };
        scDrawConditionalTools();
      })
      .catch(() => null);
  }
}

/** Adds, for the editor, each scene layer's own feature layer this reader may edit, and offers a rebuild after an edit. */
async function scAttachSources(panel) {
  const [FeatureLayer] = await scRequire(["esri/layers/FeatureLayer"]);
  // A 3D object layer's scene layer is edited itself, through its associated layer (ADR-280); it needs no stand-in.
  for (const scene of scLeaves().filter(l => l.scSource && l.scSource.mesh && !l.scEditsWatched)) {
    scene.scEditsWatched = true;
    try { scene.on("edits", () => scOfferRebuild(scene, panel)); } catch { /* an SDK without the event: Rebuild stays in Studio */ }
  }
  const made = scLeaves().filter(l => l.scSource && !l.scSource.mesh && !scLeaves(true).some(f => f.scSourceOf === l));
  for (const scene of made) {
    const layer = new FeatureLayer({ url: scene.scSource.url, title: `${scene.title || "Scene layer"} — features`,
      listMode: "hide", elevationInfo: { mode: "on-the-ground" }, opacity: 0.6 });
    layer.scSourceOf = scene;
    try { await layer.load(); } catch { continue; }
    if (!scEditable(layer)) continue;
    scState.scene.layers.add(layer);
    layer.on("edits", () => scOfferRebuild(layer.scSourceOf, panel));
  }
}

function scDetachSources() {
  for (const layer of scLeaves(true).filter(l => l.scSourceOf)) scState.scene.layers.remove(layer);
}

/** After an edit through a scene layer's features: the scene layer is a cache, made again when its owner says so. */
function scOfferRebuild(scene, panel) {
  let box = panel.querySelector(`[data-rebuild="${scene.scSource.id}"]`);
  if (!box) {
    box = document.createElement("div");
    box.className = "esri-widget sc-rebuild";
    box.dataset.rebuild = scene.scSource.id;
    box.innerHTML = `<p class="said" role="status"></p><button type="button" class="primary">Rebuild</button>`;
    panel.prepend(box);
    box.querySelector("button").addEventListener("click", () => scRebuildFromScene(box));
  }
  box.querySelector("button").disabled = false;
  box.querySelector("p").textContent = `${scene.title || "The scene layer"} does not show this edit until it is rebuilt from its features.`;
  box.scScene = scene;
}

async function scRebuildFromScene(box) {
  const scene = box.scScene;
  const button = box.querySelector("button");
  const says = box.querySelector("p");
  button.disabled = true;
  says.textContent = `Rebuilding ${scene.title || "the scene layer"}…`;
  try {
    const response = await fetch(`/content/scenes/${scene.scSource.id}/rebuild`, { method: "POST", headers: { Authorization: "Bearer " + scToken } });
    const made = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error((made.error && made.error.message) || `the server answered ${response.status}`);
    // The same layer, read again past the browser's cache of its nodes, where it stood.
    const fresh = scene.clone();
    fresh.customParameters = { ...(scene.customParameters || {}), built: String(Date.now()) };
    fresh.scSource = scene.scSource;
    fresh.scSourceAsked = true;
    const parent = scene.parent && scene.parent.layers ? scene.parent.layers : scState.scene.layers;
    const at = parent.indexOf(scene);
    for (const f of scLeaves(true).filter(l => l.scSourceOf === scene)) f.scSourceOf = fresh;
    box.scScene = fresh;
    // Added before the old one leaves, so Edit always has a layer to be offered for.
    parent.add(fresh, at);
    parent.remove(scene);
    says.textContent = `${fresh.title || "The scene layer"} was rebuilt${made.partial ? ` — ${made.partial}` : ""}.`;
  } catch (e) {
    button.disabled = false;
    says.textContent = `Not rebuilt: ${e.message}`;
  }
}

/** Shows a conditional tool's button only while a layer it works on is in the scene, closing it when the last goes. */
function scDrawConditionalTools() {
  const layers = scLeaves();
  for (const tool of SC_TOOLS.filter(t => t.when)) {
    const offered = layers.some(tool.when);
    if (!offered && scState.tool === tool.key) scToggleTool(tool.key);
  }
  // A group with nothing to offer is not shown (Search with no feature layer, Building with no building layer).
  for (const group of SC_GROUPS) {
    const button = document.querySelector(`#tools [data-group=${group.key}]`);
    if (button) button.hidden = scGroupTools(group.key).length === 0;
    if (button?.hidden && scState.group === group.key) { scOpenSide(false); scState.group = null; }
  }
  scDrawLayerTools();
}

function scDrawToolbar() {
  const bar = sc$("tools");
  const parts = SC_EMBED ? [] : SC_LAYER_PARTS;
  bar.innerHTML = parts.map(([key, label, icon]) =>
    `<button type="button" data-part-tool="${key}" aria-expanded="false" aria-controls="side"><svg class="i" aria-hidden="true"><use href="#i-${icon}"/></svg><span class="tl">${label}</span></button>`).join("")
    + (parts.length ? `<span class="rsep" aria-hidden="true"></span>` : "")
    + SC_GROUPS.map(g =>
      `<button type="button" data-group="${g.key}" aria-expanded="false" aria-controls="side"><svg class="i" aria-hidden="true"><use href="#i-${g.icon}"/></svg><span class="tl">${g.label}</span></button>`).join("");
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
    const hit = event.target.closest("button");
    if (hit) bar.querySelectorAll("button").forEach(b => { b.tabIndex = b === hit ? 0 : -1; });
    const part = event.target.closest("[data-part-tool]");
    if (part) {
      // The open part's button closes the panel, as the left toolbar's do.
      if (scState.side === "layer" && scState.layerPart === part.dataset.partTool) { scOpenSide(false); return; }
      scOpenLayerPart(part.dataset.partTool);
      return;
    }
    const group = event.target.closest("[data-group]");
    if (!group) return;
    if (scState.sideHidden && scState.side === "group" && scState.group === group.dataset.group) {
      scState.sideHidden = false;
      scOpenSide(true);
      scDrawLayerTools();
      return;
    }
    if (scState.side === "group" && scState.group === group.dataset.group) {
      if (scState.tool) scToggleTool(scState.tool, { keepSide: true });
      scOpenSide(false);
      scState.group = null;
      scDrawLayerTools();
      return;
    }
    scOpenGroup(group.dataset.group);
  });
  // A group's tools are a choice of one: the open tool's pill stays pressed rather than leaving the panel empty.
  sc$("toolPick").addEventListener("click", event => {
    const button = event.target.closest("[data-tool]");
    if (button && button.dataset.tool !== scState.tool) scToggleTool(button.dataset.tool, { keepSide: true });
  });
  scDrawLayerTools();
}

/** The tools of a group the scene offers now. */
function scGroupTools(group) {
  if (group === "server") return scCanEdit() && scToken && window.GraticulaAnalysis ? [{ key: "server", label: "Analysis" }] : [];
  const layers = scLeaves();
  // Terrain adds a layer, so it is the scene's editor's, as Add is; a reader and the embed view are not offered it.
  return SC_TOOLS.filter(t => t.group === group && (!t.when || layers.some(t.when)) && (!t.custom || (scCanEdit() && !SC_EMBED)));
}

/** Opens the right panel on a group of tools, its first (or last used) tool open. */
function scOpenGroup(group) {
  const tools = scGroupTools(group);
  if (tools.length === 0) return;
  if (scState.tool) scToggleTool(scState.tool, { keepSide: true });
  if (group === "server") {
    scState.side = "group";
    scState.group = "server";
    sc$("layerTool").hidden = true;
    sc$("toolPick").hidden = true;
    sc$("sideTitle").textContent = "Analysis";
    sc$("pane-analysis").hidden = false;
    scOpenSide(true);
    scDrawLayerTools();
    scDrawAnalysis();
    return;
  }
  sc$("pane-analysis").hidden = true;
  scState.side = "group";
  scState.group = group;
  sc$("layerTool").hidden = true;
  sc$("sideTitle").textContent = SC_GROUPS.find(g => g.key === group).label;
  const pick = sc$("toolPick");
  pick.hidden = tools.length < 2;
  pick.innerHTML = tools.map(t => `<button type="button" data-tool="${t.key}" aria-pressed="false">${scEscape(t.label)}</button>`).join("");
  scOpenSide(true);
  scDrawLayerTools();
  const wanted = tools.find(t => t.key === scState.lastTool?.[group]) || tools[0];
  if (scState.tool !== wanted.key) scToggleTool(wanted.key, { keepSide: true });
}

/** The right toolbar's buttons say which panel is open; a layer's parts are dimmed with no layer to act on. */
function scDrawLayerTools() {
  const bar = sc$("tools");
  if (!bar) return;
  const none = !scState.scene || scState.scene.layers.length === 0;
  for (const b of bar.querySelectorAll("[data-part-tool]")) {
    b.setAttribute("aria-expanded", String(scState.side === "layer" && scState.layerPart === b.dataset.partTool));
    // A reader has a layer's details only: Styles, Filter and Pop-ups would open empty (confirming review).
    b.hidden = none || (!scCanEdit() && b.dataset.partTool !== "props");
    b.querySelector(".tl").textContent = !scCanEdit() && b.dataset.partTool === "props" ? "Details"
      : (SC_LAYER_PARTS.find(p => p[0] === b.dataset.partTool) || [])[1];
  }
  const sep = bar.querySelector(".rsep");
  if (sep) sep.hidden = none;
  for (const b of bar.querySelectorAll("[data-group]")) {
    b.setAttribute("aria-expanded", String(scState.side === "group" && scState.group === b.dataset.group));
  }
}

async function scToggleTool(key, options = {}) {
  const opening = scState.tool !== key;
  // A tool still loading when another is chosen must not arrive afterwards and stay in the scene with nothing to
  // close it (measured: a fast second choice left the first tool's measurement behind).
  const ticket = scState.toolTicket = (scState.toolTicket || 0) + 1;
  if (scState.tool) {
    const open = scState.tools[scState.tool];
    if (open) {
      // A measurement or analysis left in the scene when its tool closes is cleared, as Scene Viewer clears it.
      if (open.viewModel && typeof open.viewModel.clear === "function") open.viewModel.clear();
      open.destroy();
    }
    if (scState.tool === "edit") scDetachSources();
    delete scState.tools[scState.tool];
    scState.tool = null;
  }
  for (const b of document.querySelectorAll("#toolPick [data-tool]")) b.setAttribute("aria-pressed", "false");
  const panel = sc$("toolPanel");
  panel.innerHTML = "";
  if (!opening) {
    scSay(`${SC_TOOLS.find(t => t.key === key).label} is closed.`);
    // A tool alone in its group takes the panel with it; one of several leaves the group's choice open.
    if (!options.keepSide && scState.side === "group" && scGroupTools(scState.group).length < 2) { scOpenSide(false); scState.group = null; scDrawLayerTools(); }
    return;
  }
  const tool = SC_TOOLS.find(t => t.key === key);
  // Opened from a group or by itself: either way the right panel shows its group.
  if (scState.side !== "group" || scState.group !== tool.group) {
    scState.side = "group";
    scState.group = tool.group;
    sc$("layerTool").hidden = true;
    sc$("sideTitle").textContent = SC_GROUPS.find(g => g.key === tool.group).label;
    const tools = scGroupTools(tool.group);
    const pick = sc$("toolPick");
    pick.hidden = tools.length < 2;
    pick.innerHTML = tools.map(t => `<button type="button" data-tool="${t.key}" aria-pressed="false">${scEscape(t.label)}</button>`).join("");
    scOpenSide(true);
    scDrawLayerTools();
  }
  scState.lastTool = { ...(scState.lastTool || {}), [tool.group]: key };
  const pressed = () => sc$("toolPick").querySelector(`[data-tool=${key}]`)?.setAttribute("aria-pressed", "true");
  pressed();
  // Said while the SDK's widget loads, so the panel is never blank (design review).
  panel.innerHTML = `<p class="hint" role="status">Opening ${scEscape(tool.label)}…</p>`;
  try {
    if (tool.analysis || tool.custom) {
      const made = tool.custom ? await scTerrainTool(panel) : await scAnalysisTool(tool, panel);
      if (ticket !== scState.toolTicket) { made.destroy(); return; }
      panel.querySelector("p[role=status]")?.remove();
      scState.tools[key] = made;
      scState.tool = key;
      pressed();
      scSay(tool.label);
      return;
    }
    const [Widget] = await scRequire([tool.module]);
    if (ticket !== scState.toolTicket) return;
    const container = document.createElement("div");
    panel.innerHTML = "";
    panel.append(container);
    const options = { view: scState.view, container };
    if (key === "profile") {
      options.profiles = [{ type: "ground" }, { type: "input" }];
    }
    if (key === "search") {
      const searchable = scLeaves().filter(l => l.type === "feature");
      options.includeDefaultSources = false;
      options.sources = searchable.map(layer => {
        const text = (layer.fields || []).filter(f => f.type === "string").map(f => f.name);
        return { layer, searchFields: text.length ? text : [layer.displayField || layer.objectIdField],
          displayField: layer.displayField || text[0] || layer.objectIdField, exactMatch: false, outFields: ["*"],
          name: layer.title, placeholder: `Search ${layer.title || "the layer"}` };
      });
    }
    if (key === "edit") {
      // ADR-276 §5.4: a scene layer made here is edited through the layer it was made from, added for the editor.
      await scAttachSources(panel);
      if (ticket !== scState.toolTicket) { scDetachSources(); return; }
      // Only the layers this reader may edit; the rest of the scene is drawn, not offered.
      options.layerInfos = [...scLeaves().filter(l => l.scSource && l.scSource.mesh).map(layer => ({ layer, enabled: true })),
        ...scLeaves(true).filter(l => l.type === "feature").map(layer => ({ layer, enabled: scEditable(layer) }))];
    }
    if (key === "building") {
      options.layers = scLeaves().filter(l => l.type === "building-scene");
    }
    if (key === "time") {
      // The extent of every time-aware layer, stepped as the first one's own interval would be.
      const timed = scLeaves().filter(l => l.timeInfo && l.timeInfo.fullTimeExtent);
      const start = Math.min(...timed.map(l => l.timeInfo.fullTimeExtent.start.getTime()));
      const end = Math.max(...timed.map(l => l.timeInfo.fullTimeExtent.end.getTime()));
      options.fullTimeExtent = { start: new Date(start), end: new Date(end) };
      options.timeVisible = true;
      options.mode = "time-window";
    }
    scState.tools[key] = new Widget(options);
    scState.tool = key;
    pressed();
    scSay(tool.label);
  } catch (e) {
    scSay(`${tool.label} could not be opened: ${e.message}`, true);
  }
}

/** The raster functions the Terrain tool offers, as the server's ImageServer answers them (RasterFunction.cs). */
const SC_TERRAIN = {
  hillshade: { label: "Hillshade", name: "Hillshade", args: a => ({ Azimuth: a.azimuth, Altitude: a.altitude, ZFactor: a.z }) },
  slope: { label: "Slope (degrees)", name: "Slope", args: a => ({ SlopeType: 1, ZFactor: a.z }) },
  slopePercent: { label: "Slope (percent rise)", name: "Slope", args: a => ({ SlopeType: 2, ZFactor: a.z }) },
  aspect: { label: "Aspect", name: "Aspect", args: () => ({}) },
};

/**
 * ADR-281 §5.4: the Terrain tool. A surface — one of this server's image services, the elevation surfaces first — and a
 * function; *Add to scene* drapes the server's answer as an imagery layer the scene keeps, with its raster function.
 * Returns what scToggleTool destroys: nothing stays in the view but the layers added on purpose.
 */
async function scTerrainTool(panel) {
  const found = await scPortal(`/search?num=100&sortField=title&sortOrder=asc&q=${encodeURIComponent('type:"Image Service"')}`).catch(() => ({ results: [] }));
  const own = (found.results || []).filter(i => i.url && /\/ImageServer\/?$/i.test(i.url));
  own.sort((a, b) => Number((b.typeKeywords || []).includes("Elevation 3D Layer")) - Number((a.typeKeywords || []).includes("Elevation 3D Layer")));
  const ground = scState.scene.ground.layers.getItemAt(0);
  const box = document.createElement("div");
  box.className = "esri-widget sc-analysis";
  box.innerHTML = own.length === 0
    ? `<h3>Terrain</h3><p class="hint">No image service of this server is readable here. Publish an elevation model as an image service, and its slope, aspect and hillshade can be added to the scene.</p>`
    : `<h3>Terrain</h3>
    <p class="hint">Slope, aspect or hillshade of a surface, worked out by the server and draped on the scene as a layer.</p>
    <label class="field" for="trSource">Surface</label>
    <select id="trSource">${own.map(i => `<option value="${scEscape(i.url)}">${scEscape(i.title)}${(i.typeKeywords || []).includes("Elevation 3D Layer") ? " (elevation)" : ""}</option>`).join("")}</select>
    <label class="field" for="trFunction">Function</label>
    <select id="trFunction">${Object.entries(SC_TERRAIN).map(([k, f]) => `<option value="${k}">${scEscape(f.label)}</option>`).join("")}</select>
    <div id="trLight"><div class="row">
      <label class="field" for="trAzimuth" style="flex:1">Light from (°)<input type="number" id="trAzimuth" min="0" max="360" step="1" value="315"></label>
      <label class="field" for="trAltitude" style="flex:1">Light height (°)<input type="number" id="trAltitude" min="0" max="90" step="1" value="45"></label></div></div>
    <label class="field" for="trZ">Z factor <span class="hint">— metres of height per unit of the surface's ground</span></label>
    <input type="number" id="trZ" min="0" step="any" value="1">
    <div class="row"><button type="button" class="primary" id="trAdd">Add to scene</button></div>
    <p class="said" id="trSays" role="status" aria-live="polite"></p>`;
  // Appended after the panel's "Opening…" line, which scToggleTool removes as the first status in the panel.
  panel.append(box);
  if (own.length) {
    if (ground && own.some(i => i.url.replace(/\/$/, "") === (ground.url || "").replace(/\/$/, ""))) box.querySelector("#trSource").value = own.find(i => i.url.replace(/\/$/, "") === ground.url.replace(/\/$/, "")).url;
    const light = () => { box.querySelector("#trLight").hidden = box.querySelector("#trFunction").value !== "hillshade"; };
    box.querySelector("#trFunction").addEventListener("change", light);
    light();
    box.querySelector("#trAdd").addEventListener("click", async () => {
      const says = box.querySelector("#trSays");
      const url = box.querySelector("#trSource").value;
      const key = box.querySelector("#trFunction").value;
      const fn = SC_TERRAIN[key];
      const args = fn.args({ azimuth: Number(box.querySelector("#trAzimuth").value) || 315, altitude: Number(box.querySelector("#trAltitude").value) || 45,
        z: Number(box.querySelector("#trZ").value) || 1 });
      const source = own.find(i => i.url === url);
      says.textContent = `Adding ${fn.label.toLowerCase()} of ${source.title}…`;
      try {
        const [ImageryLayer] = await scRequire(["esri/layers/ImageryLayer"]);
        const layer = new ImageryLayer({ url, title: `${source.title} — ${fn.label}`, opacity: key === "hillshade" ? 0.6 : 0.75,
          rasterFunction: { functionName: fn.name, functionArguments: args } });
        await layer.load();
        scState.scene.layers.add(layer);
        scMarkDirty();
        says.textContent = `${layer.title} was added. It is a layer of the scene: its look and its place are in Layers.`;
      } catch (e) {
        says.textContent = `Not added: ${e.message}`;
      }
    });
  }
  return { destroy() { box.remove(); } };
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

// Escape closes the right panel and gives focus back to the button that opened it — but not from inside a tool, where
// the SDK's tools use it to cancel a placement, and it never clears a measurement (design review: one key wiped it).
document.addEventListener("keydown", event => {
  if (event.key !== "Escape" || event.defaultPrevented) return;
  if (event.target instanceof Element && event.target.closest("#toolPanel, #layerTool input, #layerTool select, #layerTool textarea, #pane-analysis")) return;
  if (scState.playing) return;
  if (!sc$("side").hidden) {
    const back = scState.side === "layer" ? `[data-part-tool="${scState.layerPart}"]` : `[data-group="${scState.group}"]`;
    scHideSide();
    sc$("tools").querySelector(back)?.focus();
    return;
  }
  if (SC_PHONE.matches && document.body.classList.contains("sheet")) {
    scOpenPanel(false);
    document.querySelector("#tabs [aria-selected=true]")?.focus();
  }
});

/**
 * Hides the right panel and keeps what its tool drew: the tool stays open behind it, so its measurement is still in the
 * scene, and its button opens it again as it was. Closing the tool itself is its button's or the panel's ×.
 */
function scHideSide() {
  sc$("side").hidden = true;
  document.body.classList.remove("ropen");
  scState.sideHidden = true;
  for (const b of sc$("tools").querySelectorAll("[aria-expanded=true]")) b.setAttribute("aria-expanded", "false");
}

// ---------------------------------------------------------------------------------------------------------------------
// Embedding (ADR-275): the code for a saved scene, when the operator has named a site that may frame it.

sc$("appCreate").addEventListener("click", async () => {
  const title = `${sc$("sceneTitle").value.trim() || "Scene"} — app`;
  const said = sc$("appSaid");
  said.className = "said";
  said.textContent = "Creating the app…";
  try {
    const made = await scPortal(`/content/users/${encodeURIComponent(scState.user)}/addItem`, {
      type: "Web Mapping Application", title,
      url: `${location.origin}/studio/scene-embed.html?id=${encodeURIComponent(scState.id)}`,
      typeKeywords: "JavaScript,Map,Mapping Site,Online Map,Ready To Use,Scene",
      snippet: `The scene ${sc$("sceneTitle").value.trim() || ""} as an app.`.replace("  ", " "),
    });
    said.innerHTML = `Created <a href="/studio/#/item/${scEscape(made.id)}">${scEscape(title)}</a>. It opens <a href="/studio/scene-embed.html?id=${encodeURIComponent(scState.id)}" target="_blank" rel="noopener">the scene alone</a>.`;
    scSay(`${title} was created.`);
  } catch (e) {
    said.className = "said bad";
    said.textContent = `Not created: ${e.message}`;
  }
});

function scDrawEmbed() {
  // The app needs a saved scene, and is its owner's to make.
  sc$("appPart").hidden = !scCanEdit() || !scState.id;
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
    const json = scState.scene.toJSON();
    // The editor's feature layers (ADR-276 §5.4) are never part of the scene.
    const editorOnly = new Set(scState.scene.allLayers.toArray().filter(l => l.scSourceOf || scWidgetsOwn(l)).map(l => l.id));
    if (json.operationalLayers) json.operationalLayers = json.operationalLayers.filter(l => !editorOnly.has(l.id));
    const text = JSON.stringify(json);
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
    // The gallery's models are among the shapes the first style form offers (ADR-281 §5.3).
    await scGalleryReady;
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
  // ADR-270 §5.22: whose layers the Add list shows — the reader's favourites and groups, as Scene Viewer's Browse does.
  scState.me = me;
  const from = sc$("addFrom");
  if (me && me.favGroupId) from.insertAdjacentHTML("beforeend", `<option value="group:${scEscape(me.favGroupId)}">My favourites</option>`);
  for (const g of (me && me.groups) || []) from.insertAdjacentHTML("beforeend", `<option value="group:${scEscape(g.id)}">Group: ${scEscape(g.title)}</option>`);
  // Signed in by the cookie alone (a new tab): a token of this tab's own, so Save can write.
  if (scState.user && !scToken) await scExchangeSession();
  if (!scState.user) sc$("studio").hidden = false;
  if (me && (me.role === "org_admin" || (me.privileges || []).includes("portal:admin:viewUsers"))) sc$("navOrganization").hidden = false;

  const [WebScene, SceneView, Layer, ElevationLayer, Basemap, esriConfig, WebTileLayer, Slide] = await scRequire([
    "esri/WebScene", "esri/views/SceneView", "esri/layers/Layer", "esri/layers/ElevationLayer", "esri/Basemap",
    "esri/config", "esri/layers/WebTileLayer", "esri/webscene/Slide"]);
  esriConfig.request.useIdentity = false;
  esriConfig.portalUrl = SC_PORTAL;
  // ADR-276: the SDK's own writes to this server — an edit's applyEdits — carry this tab's token, as console.js's map
  // does; the server honours the cookie for reads only (ADR-023 §4c). Read when sent, so a token exchanged later counts.
  esriConfig.request.interceptors.push({
    urls: location.origin,
    before: params => {
      if (scToken) params.requestOptions.headers = { ...(params.requestOptions.headers || {}), Authorization: "Bearer " + scToken };
    },
  });
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
    // ADR-270 §5.20: a local scene in a layer's own projected system (`&wkid=`), for a layer drawn only in its own — a
    // voxel layer, a scene layer in a national grid. Web Mercator basemaps are not drawn in it, so it starts with none.
    const wkid = Number(scQuery.get("wkid")) || 3857;
    const own = local && wkid !== 3857 && wkid !== 102100;
    scState.scene = new WebScene({ basemap: own ? null : basemap, ground: {},
      initialViewProperties: local ? { viewingMode: "local", spatialReference: { wkid } } : undefined });
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
  sc$("sceneAbout").textContent = scCanEdit() ? "" : "You can look around this scene, play its slides and use its tools; only its owner can change it.";
  sc$("addPart").hidden = !scCanEdit();
  sc$("addNotYours").hidden = scCanEdit();
  // A reader who cannot change the scene has nothing to add: no Add tab (confirming review).
  sc$("tab-add").hidden = !scCanEdit();
  scDrawEmbed();
  // The server's analysis: a result is a hosted layer of the reader's own added to this scene, so it is offered to
  // whoever may change the scene (scGroupTools). Its pane lives in the right panel.
  sc$("sideBody").append(sc$("pane-analysis"));
  sc$("pane-analysis").hidden = true;

  scState.mode = scState.scene.initialViewProperties.viewingMode === "local" ? "local" : "global";
  const quality = (() => { try { return localStorage.getItem("graticula-scene-quality"); } catch { return null; } })();
  sc$("sceneQuality").value = ["low", "medium", "high"].includes(quality) ? quality : "medium";
  scState.view = new SceneView({ container: "viewDiv", map: scState.scene, qualityProfile: sc$("sceneQuality").value,
    viewingMode: scState.mode,
    clippingArea: scState.mode === "local" && scState.scene.clippingEnabled ? scState.scene.clippingArea : null });
  scNavigation();
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
  await Promise.all(scLeaves().map(l => l.load().catch(() => null)));
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
  scState.scene.layers.on("change", () => { if (scState.opened) { scDrawLayers(); scDrawConditionalTools(); scLearnSources(); } });
  scLearnSources();
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
