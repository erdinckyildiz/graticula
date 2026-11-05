// The scene viewer — ADR-269. A web scene kept as a portal item, drawn by the ArcGIS Maps SDK for JavaScript (ADR-253).
//
// The portal is this server under /studio: the SDK reads the item and its layers from there, and the server asked
// through /studio names /studio as its owner (ADR-191's 2026-11-05 note), so a private layer opens with the reader's
// session rather than a sign-in dialog. Requests carry the browser's session cookie; the SDK's own sign-in dialog is
// turned off, as map.js turns it off, and a refusal is said on this page.
"use strict";

const scQuery = new URLSearchParams(location.search);
const SC_PORTAL = `${location.origin}/studio`;
const SC_REST = `${SC_PORTAL}/sharing/rest`;

const sc$ = id => document.getElementById(id);
const scState = { id: scQuery.get("id"), scene: null, view: null, user: null, manages: true, dirty: false, saving: false };

/** Says a sentence in the page's live status line; `bad` marks a failure. */
function scSay(text, bad = false) {
  const status = sc$("status");
  status.textContent = text;
  status.style.color = bad ? "var(--alert)" : "";
}

function scEscape(value) {
  return String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" })[c]);
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

/** What a layer is, said as Studio says it. */
function scKind(layer) {
  // A scene layer is 3D objects or points (D-386); the SDK gives both the type "scene".
  if (layer.type === "scene" && layer.geometryType === "point") return "Points";
  return ({ "feature": "Features", "scene": "3D objects", "point-cloud": "Point cloud", "building-scene": "Buildings",
    "integrated-mesh": "Mesh", "imagery": "Imagery", "imagery-tile": "Imagery", "map-image": "Map image",
    "vector-tile": "Vector tiles", "elevation": "Elevation" })[layer.type] || layer.type;
}

function scMarkDirty() {
  scState.dirty = true;
  scDrawSaved();
}

function scDrawSaved() {
  const saved = sc$("saved");
  saved.textContent = scState.dirty ? "Not saved" : scState.id ? "Saved" : "";
  saved.className = scState.dirty ? "dirty" : "";
  const button = sc$("headSave");
  button.hidden = !scState.user || !scState.manages;
  button.disabled = scState.saving
    || (!scState.id && !sc$("sceneTitle").value.trim() && (!scState.scene || scState.scene.layers.length === 0));
}

function scDrawLayers() {
  const list = sc$("layerList");
  const layers = scState.scene.layers.toArray().slice().reverse();
  if (layers.length === 0) {
    list.innerHTML = `<li class="hint" style="border:0;padding:0">This scene has no layers yet.${
      scState.manages && scState.user ? " Add one below." : ""}</li>`;
    return;
  }
  list.innerHTML = layers.map(layer => `
    <li data-layer="${scEscape(layer.id)}">
      <label><input type="checkbox" data-act="visible" ${layer.visible ? "checked" : ""}>
        <span class="lname">${scEscape(layer.title || "Untitled layer")}<span class="lkind">${scEscape(scKind(layer))}${
          layer.loadStatus === "failed" ? ` · <span class="lnote">could not be read</span>` : ""}</span></span></label>
      ${scState.manages && scState.user ? `<button type="button" class="tiny" data-act="remove"
        aria-label="Remove ${scEscape(layer.title || "this layer")}">Remove</button>` : ""}
    </li>`).join("");
}

sc$("layerList").addEventListener("change", event => {
  const row = event.target.closest("[data-layer]");
  if (!row || event.target.dataset.act !== "visible") return;
  const layer = scState.scene.layers.find(l => l.id === row.dataset.layer);
  if (!layer) return;
  layer.visible = event.target.checked;
  scMarkDirty();
});

sc$("layerList").addEventListener("click", event => {
  const button = event.target.closest("[data-act=remove]");
  if (!button) return;
  const row = button.closest("[data-layer]");
  const layer = scState.scene.layers.find(l => l.id === row.dataset.layer);
  if (!layer) return;
  const title = layer.title || "The layer";
  scState.scene.remove(layer);
  scMarkDirty();
  scDrawLayers();
  scSay(`${title} was removed.`);
  (sc$("layerList").querySelector("input, button") || sc$("addUrl")).focus();
});

/** Adds a layer from a service address; the SDK works out what kind it is. */
async function scAddUrl(url, title) {
  const said = sc$("addSaid");
  said.className = "said";
  said.textContent = `Adding ${title || "the layer"}…`;
  try {
    const layer = await scState.sdk.Layer.fromArcGISServerUrl({ url });
    await layer.load();
    // The item's title, when it was added from the list: a service's own name may be its item id (ADR-269); one
    // added by its address here is called by its item's title too, read from the portal.
    const hosted = /\/rest\/services\/hosted\/([0-9a-f]{32})\/SceneServer/i.exec(url);
    if (title) layer.title = title;
    else if (hosted) layer.title = (await scPortal(`/content/items/${hosted[1]}`).catch(() => ({}))).title || layer.title;
    // A layer that carries its own heights is placed at them; a flat one is draped on the ground.
    if (layer.type === "feature" && layer.hasZ) layer.elevationInfo = { mode: "absolute-height" };
    scState.scene.add(layer);
    scMarkDirty();
    scDrawLayers();
    said.textContent = `${layer.title || "The layer"} was added.`;
    scSaySummary();
    // Framed at an angle: a scene seen from straight above looks like a map (design: the point of a scene is height).
    if (layer.fullExtent) scState.view.goTo({ target: layer.fullExtent, tilt: 55 }).catch(() => {});
  } catch (e) {
    said.className = "said bad";
    said.textContent = `Not added: ${e.message || "the service could not be read"}.`;
  }
}

/** The header's line about the scene as it now is: whether it is new, and how many layers it has. */
function scSaySummary() {
  const count = scState.scene.layers.length;
  // A new, empty scene: nothing to say that the panel does not say already.
  scSay(!scState.id && count === 0 ? "" : `${scState.id ? "" : "A new scene. "}${count === 0 ? "No layers" : count === 1 ? "1 layer" : `${count} layers`}.`);
}

sc$("addUrlButton").addEventListener("click", () => {
  const url = sc$("addUrl").value.trim();
  if (!url) { sc$("addSaid").textContent = "Type a service address first."; sc$("addUrl").focus(); return; }
  scAddUrl(url);
});
sc$("addUrl").addEventListener("keydown", event => { if (event.key === "Enter") sc$("addUrlButton").click(); });

/** What can be added: the reader's and the organization's layers, from the portal's own search. */
async function scDrawAddList() {
  const list = sc$("addList");
  try {
    const found = await scPortal(`/search?num=50&sortField=modified&sortOrder=desc&q=${encodeURIComponent(
      'type:("Feature Service" OR "Scene Service" OR "Image Service" OR "Map Service")')}`);
    const items = (found.results || []).filter(item => item.url);
    if (items.length === 0) {
      list.innerHTML = `<li class="hint" style="border:0;padding:0">No layers are shared with you yet. Publish one in Content, or add one by its address.</li>`;
      return;
    }
    scState.addItems = items;
    scDrawAddItems();
    // The ground offers the image services; the SDK says when one is not an elevation surface.
    const ground = sc$("groundSelect");
    for (const item of items.filter(i => i.type === "Image Service")) {
      const option = document.createElement("option");
      option.value = item.url;
      option.textContent = item.title;
      ground.append(option);
    }
  } catch (e) {
    list.innerHTML = `<li class="hint" style="border:0;padding:0">The list could not be read: ${scEscape(e.message)}</li>`;
  }
}

/** The add list, filtered by title, eight rows until the reader asks for all (design review). */
function scDrawAddItems() {
  const items = scState.addItems || [];
  const words = sc$("addFilter").value.trim().toLowerCase();
  const matched = words ? items.filter(i => (i.title || "").toLowerCase().includes(words)) : items;
  const shown = scState.showAll || words ? matched : matched.slice(0, 8);
  sc$("addList").innerHTML = matched.length === 0
    ? `<li class="hint" style="border:0;padding:0">No layer's title has “${scEscape(words)}” in it.</li>`
    : shown.map(item => `
      <li><span class="lname">${scEscape(item.title)}<span class="lkind">${scEscape(scTypeLabel(item.type))}</span></span>
        <button type="button" class="tiny" data-add="${scEscape(item.url)}" data-title="${scEscape(item.title)}"
          aria-label="Add ${scEscape(item.title)}">Add</button></li>`).join("");
  const more = sc$("addShowAll");
  more.hidden = shown.length >= matched.length;
  more.textContent = `Show all (${matched.length})`;
}

/** The portal's item types, as Studio's rows name them. */
function scTypeLabel(type) {
  return ({ "Scene Service": "Scene layer", "Feature Service": "Feature layer", "Image Service": "Imagery layer",
    "Map Service": "Map image layer" })[type] || type;
}

sc$("addFilter").addEventListener("input", scDrawAddItems);
sc$("addShowAll").addEventListener("click", () => {
  scState.showAll = true;
  scDrawAddItems();
  sc$("addList").querySelectorAll("button")[8]?.focus();
});

sc$("addList").addEventListener("click", event => {
  const button = event.target.closest("[data-add]");
  if (button) scAddUrl(button.dataset.add, button.dataset.title);
});

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
        { type: "Web Scene", title, text, tags: "scene", typeKeywords: "ViewingMode-global" });
      scState.id = added.id;
      scState.owner = scState.user;
      history.replaceState(null, "", `?id=${encodeURIComponent(added.id)}`);
    }
    scState.dirty = false;
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

  // <b>A plain function, not an async one</b>: the SDK's module loader does not call an async callback at all — it
  // reads `[object AsyncFunction]` as not a function — and the page stayed on *Loading* with no error (2026-11-05).
  const modules = await new Promise((resolve, reject) => require(["esri/WebScene", "esri/views/SceneView",
    "esri/layers/Layer", "esri/layers/ElevationLayer", "esri/Basemap", "esri/config", "esri/layers/WebTileLayer"],
    (...loaded) => resolve(loaded), reject));
  {
    const [WebScene, SceneView, Layer, ElevationLayer, Basemap, esriConfig, WebTileLayer] = modules;
    esriConfig.request.useIdentity = false;
    esriConfig.portalUrl = SC_PORTAL;
    scState.sdk = { Layer, ElevationLayer };

    if (scState.id) {
      scState.scene = new WebScene({ portalItem: { id: scState.id, portal: { url: SC_PORTAL } } });
    } else {
      const basemap = self.defaultBasemap ? Basemap.fromJSON(self.defaultBasemap) : null;
      scState.scene = new WebScene({ basemap, ground: {} });
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
    const bases = scState.scene.basemap ? scState.scene.basemap.baseLayers : null;
    for (const layer of (bases ? bases.toArray() : [])) {
      if (layer.type === "open-street-map") {
        const index = bases.indexOf(layer);
        bases.splice(index, 1, new WebTileLayer({
          urlTemplate: "https://tile.openstreetmap.org/{level}/{col}/{row}.png",
          title: layer.title || "OpenStreetMap", visible: layer.visible, opacity: layer.opacity,
          copyright: "Map data © OpenStreetMap contributors",
        }));
      }
    }

    const item = scState.scene.portalItem;
    if (item) {
      sc$("title").textContent = item.title || "Scene";
      sc$("sceneTitle").value = item.title || "";
      scState.owner = item.owner;
      scState.manages = !!scState.user && item.owner === scState.user;
    }
    sc$("sceneDetails").hidden = !(scState.user && scState.manages);
    sc$("addPart").hidden = !(scState.user && scState.manages);
    sc$("groundSelect").disabled = !(scState.user && scState.manages);

    scState.view = new SceneView({ container: "viewDiv", map: scState.scene, qualityProfile: "medium" });
    // A camera the reader moved is a change worth saving; one the view settled on its own — a resize, a phone's first
    // layout — is not (design review: a reopened scene read "Not saved" on a phone with nothing done).
    for (const kind of ["pointerdown", "wheel", "keydown"]) {
      sc$("viewDiv").addEventListener(kind, () => { scState.moved = true; }, { passive: true });
    }
    scState.view.watch("stationary", stationary => {
      if (stationary && scState.moved && scState.opened && scState.manages && scState.user) scMarkDirty();
    });
    try {
      await scState.view.when();
    } catch (e) {
      // A browser without WebGL cannot draw a scene at all; said, rather than left on "Loading" (ADR-269).
      scSay(`This browser cannot draw a 3D scene: ${e.message || "WebGL is not available"}. The scene itself is unharmed.`, true);
      return;
    }
    await Promise.all(scState.scene.layers.toArray().map(l => l.load().catch(() => null)));
    scState.opened = true;

    scDrawLayers();
    scDrawSaved();
    scSaySummary();
    if (scState.user && scState.manages) scDrawAddList();

    // ADR-269: opened from a scene layer's page (`?layer=`), a new scene starts with that layer, framed.
    const start = scQuery.get("layer");
    if (start && !scState.id) {
      if (!sc$("sceneTitle").value) {
        sc$("sceneTitle").value = scQuery.get("title") || "";
        sc$("title").textContent = sc$("sceneTitle").value || "New scene";
      }
      await scAddUrl(start, scQuery.get("title") || "");
    }
  }
}

scStart();
