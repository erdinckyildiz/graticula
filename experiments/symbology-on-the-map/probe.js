/*
  <b>The experiment's whole mechanism, and it is small on purpose.</b> An OpenLayers map for the
  ground and the panning, and one ordinary `<img>` over it whose source is a POST to
  `/admin/layers/{name}/symbology/preview?bbox=&size=`. Every view change asks for a new one and
  the round trip is timed on the panel.

  <b>What is being tested.</b> ADR-051 rejected a browser-drawn preview because it would be a
  picture of the browser's reading of the style rather than of the renderer that serves the
  layer. This draws with the server's renderer and still pans, so the open question is only
  whether the round trip is affordable at the rate panning asks for it.

  <b>An `img` rather than an OpenLayers image source, deliberately.</b> A source with a custom
  loader is a different API in almost every OpenLayers major version, and the thing being
  measured is the server, not the binding. An element over the map is version-proof and exactly
  as good for a measurement.

  <b>Not production code.</b> No token refresh, no error surface worth the name, no retry.
  CLAUDE.md §1: /experiments is never promoted.
*/

const $ = id => document.getElementById(id);

/** The session, taken from where the console keeps it — sign in on /studio/ first. */
const token = sessionStorage.getItem("gis-token") || "";

const headers = () => {
  const h = { "Content-Type": "application/json" };

  if (token) h.Authorization = "Bearer " + token;

  return h;
};

/** The layer being drawn and the document being edited. Never stored. */
let layer = null;
let cim = null;

/** What the last draws cost, which is the experiment's answer. */
const trips = [];

/** One draw at a time: panning fires faster than the server answers. */
let drawing = 0;

/** Every class of the renderer, whatever family it is — the real objects, so edits land. */
function classesOf(doc) {
  if (!doc || typeof doc !== "object") return [];

  if (doc.type === "CIMUniqueValueRenderer") {
    return (doc.groups || []).flatMap(g => g.classes || []);
  }

  if (doc.type === "CIMClassBreaksRenderer") return doc.breaks || [];

  return doc.symbol ? [doc] : [];
}

/** The first painted layer of a class's symbol — the one whose colour reads as its colour. */
function paintOf(cls) {
  const layers = ((cls.symbol || {}).symbol || {}).symbolLayers || [];

  return layers.find(l => l.type === "CIMSolidFill" || l.type === "CIMSolidStroke")
    || layers.find(l => l.color) || null;
}

const hex = colour => {
  const v = (colour && colour.values) || [0, 0, 0, 100];

  return "#" + [v[0], v[1], v[2]]
    .map(n => Math.round(Number(n) || 0).toString(16).padStart(2, "0")).join("");
};

const rgb = text => [
  parseInt(text.slice(1, 3), 16),
  parseInt(text.slice(3, 5), 16),
  parseInt(text.slice(5, 7), 16),
];

// ------------------------------------------------------------------------- the map

const map = new ol.Map({
  target: "map",
  layers: [new ol.layer.Tile({ source: new ol.source.OSM() })],
  view: new ol.View({ center: [3660000, 4869000], zoom: 11 }),
});

const over = $("over");

/**
 * Draws the current document at the current view.
 *
 * <b>The size is the map's own, so the picture is one server pixel per screen pixel.</b> Asking
 * for a thumbnail and stretching it is what the editor does today and is exactly what this
 * experiment exists to stop doing.
 */
async function redraw() {
  if (!layer || !cim) return;

  const size = map.getSize();

  if (!size) return;

  const extent = map.getView().calculateExtent(size);
  const mine = ++drawing;

  const wide = Math.max(1, Math.min(2048, Math.round(size[0])));
  const tall = Math.max(1, Math.min(2048, Math.round(size[1])));

  const at = performance.now();

  try {
    const response = await fetch(
      `/admin/layers/${encodeURIComponent(layer)}/symbology/preview`
      + `?bbox=${extent.join(",")}&size=${wide}x${tall}`,
      { method: "POST", headers: headers(), body: JSON.stringify(cim) });

    const took = Math.round(performance.now() - at);

    if (!response.ok) {
      say(`${response.status} — ${(await response.text()).slice(0, 120)}`, took, 0);

      return;
    }

    const blob = await response.blob();

    // <b>A later draw has already started, so this one is stale.</b> Painting it would show the
    // extent somebody has panned away from — the flicker every tiled map spends effort avoiding.
    if (mine !== drawing) return;

    const was = over.src;

    over.src = URL.createObjectURL(blob);

    if (was.startsWith("blob:")) URL.revokeObjectURL(was);

    say(null, took, blob.size);
  } catch (e) {
    say(e.message || String(e), Math.round(performance.now() - at), 0);
  }
}

/** What the last few draws cost. */
function say(error, took, bytes) {
  trips.push(took);

  const last = trips.slice(-12);
  const sorted = [...last].sort((a, b) => a - b);
  const median = sorted[Math.floor(sorted.length / 2)];
  const tone = took > 250 ? "bad" : took > 120 ? "slow" : "";

  $("timing").innerHTML = `
    <div>last <b class="${tone}">${took} ms</b> · ${Math.round(bytes / 1024)} kB</div>
    <div>median of ${last.length}: <b>${median} ms</b></div>
    <div>draws: <b>${trips.length}</b></div>
    ${error ? `<div class="bad">${error}</div>` : ""}`;
}

map.on("moveend", redraw);

// ---------------------------------------------------------------------- the panel

function drawClasses() {
  const box = $("classes");
  const all = classesOf(cim);

  box.innerHTML = all.slice(0, 14).map((cls, i) => {
    const paint = paintOf(cls);

    return `<div class="row">
      <input type="color" data-at="${i}" value="${hex(paint && paint.color)}">
      <span>${String(cls.label || "class " + (i + 1)).slice(0, 22)}</span>
      <span class="n">${i + 1}/${all.length}</span>
    </div>`;
  }).join("");

  box.oninput = e => {
    const at = Number(e.target.dataset.at);
    const paint = paintOf(all[at]);

    if (!paint) return;

    const [r, g, b] = rgb(e.target.value);
    const was = (paint.color && paint.color.values) || [0, 0, 0, 100];

    paint.color = { type: "CIMRGBColor", values: [r, g, b, was[3] ?? 100] };

    // The whole point: the map redraws from the edited document, which is never stored.
    redraw();
  };
}

async function open(name) {
  layer = name;

  // The picker says which layer is drawn. It did not, for one run: `open` was called with a
  // name the select had never been set to, so the panel named one layer and the map drew
  // another — which is the exact fault this whole screen is being redesigned over.
  if ($("layer").value !== name) $("layer").value = name;

  const held = await (await fetch(
    `/admin/layers/${encodeURIComponent(name)}/symbology`, { headers: headers() })).json();

  cim = held.symbology || null;

  drawClasses();

  try {
    const at = `/rest/services/${held.service}/FeatureServer/${held.layerId ?? 0}?f=json`;
    const doc = await (await fetch(at, { headers: headers() })).json();
    const e = doc.extent;

    if (e && Number.isFinite(e.xmin)) {
      map.getView().fit([e.xmin, e.ymin, e.xmax, e.ymax], { padding: [30, 30, 30, 30] });
    }
  } catch {
    // A layer whose document cannot be read still draws where the map happens to be.
  }

  redraw();
}

(async () => {
  const listing = await (await fetch("/content/layers", { headers: headers() })).json();

  const names = [...(listing.mine || []), ...(listing.shared || []), ...(listing.notShared || [])]
    .map(e => e.name).sort();

  $("layer").innerHTML = names.map(n => `<option value="${n}">${n}</option>`).join("");
  $("layer").onchange = () => open($("layer").value);

  if (names.length > 0) await open(names.includes("ci_many") ? "ci_many" : names[0]);
})();
