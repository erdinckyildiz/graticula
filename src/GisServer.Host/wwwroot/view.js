// The layer viewer's behaviour. OpenLayers, vendored, no runtime third party.
//
// Addressed as ?service=hosted/name&layer=0. Without a layer id the service's
// layers become a picker rather than a stack — a service's layers are alternatives
// to look at, not a composition (ADR-020 §4b).

const QUERY = new URLSearchParams(location.search);
const SERVICE = QUERY.get("service") || "";
const LAYER = QUERY.get("layer");
const WEB_MERCATOR = "EPSG:3857";

const $ = id => document.getElementById(id);

function problem(title, detail) {
  $("note").innerHTML = `<b>${escape(title)}</b>${detail}`;
  $("note").className = "on";
}

function escape(value) {
  return String(value ?? "").replace(/[&<>"']/g,
    c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

const serviceUrl = () =>
  `${location.origin}/rest/services/${SERVICE.split("/").map(encodeURIComponent).join("/")}`;

// ------------------------------------------------------------------- the ground
//
// The same public-domain files the console's map panel uses: Natural Earth 1:110m
// countries and lakes, plus a graticule. Read as EPSG:4326 and projected here,
// which is what `dataProjection` is for.

function groundLayer(file, style, options = {}) {
  return new ol.layer.Vector({
    source: new ol.source.Vector({
      url: `${location.origin}/console/${file}`,
      format: new ol.format.GeoJSON({
        dataProjection: "EPSG:4326",
        featureProjection: WEB_MERCATOR,
      }),
      attributions: "Natural Earth (public domain)",
    }),
    style,
    ...options,
  });
}

/*
  <b>Country names, and enough contrast to read as a map.</b> The first version had
  neither: pale beige land on pale blue water with no text anywhere, which is a
  diagram of the world rather than a map of it. Muting the ground so the data stands
  out was right in intent and went too far — you cannot tell where you are looking
  without names, and everything being one value makes borders disappear.

  Labels are declutter-ed and resolution-gated. A country name is useful while a
  country is on screen and meaningless at street zoom, and OpenLayers' declutter
  keeps them from overprinting each other.
*/
// <b>Warmer land against a bluer sea, and country edges dark enough to be edges.</b>
// The first pass had land and water within a few percent of each other in
// lightness, which is why it read as one flat wash: borders vanished and the eye
// had nothing to hold. These are still quiet — the data carries the only saturated
// colour on the page — but they are separable.
const LAND = "#f0e8d8";
const LAND_EDGE = "#8d9c95";
const WATER = "#bfd6e2";
const LABEL = "#4e5c57";

// Roughly: names appear once the view is continental, and go when it is regional.
// Resolution is metres per pixel in 3857.
const NAME_FROM = 200;
const NAME_TO = 40000;

const countries = groundLayer("ground-countries.geojson", (feature, resolution) => {
  /*
    <b>Natural Earth steps back the moment there is better data.</b> Asked for
    "OpenLayers instead of Natural Earth", and those are not alternatives —
    OpenLayers draws, Natural Earth is drawn. What the request means is *stop showing
    me the coarse outline*, and the answer is: when imported tiles are the ground,
    this drops to land against sea and nothing else. No country lines to compete with
    district lines, no country names over a city.

    It cannot be dropped entirely. Nothing else here supplies the land/water
    silhouette — OSM boundary lines on an empty field are lines on an empty field —
    and importing a world coastline is another fifty megabytes for something already
    solved in 172 KB.
  */
  const style = [new ol.style.Style({
    fill: new ol.style.Fill({ color: LAND }),
    stroke: new ol.style.Stroke({ color: LAND_EDGE, width: 0.8 }),
  })];

  if (resolution > NAME_FROM && resolution < NAME_TO) {
    style.push(new ol.style.Style({
      text: new ol.style.Text({
        text: feature.get("name") || "",
        font: "600 11px system-ui, sans-serif",
        fill: new ol.style.Fill({ color: LABEL }),
        stroke: new ol.style.Stroke({ color: "rgba(255,255,255,0.85)", width: 2.5 }),
        overflow: false,
      }),
    }));
  }

  return style;
}, { declutter: true });

/*
  Cities and rivers, and what was left out.

  <b>Measured before choosing.</b> Natural Earth's 10m provinces are 21 MB trimmed
  and its 10m roads 16 MB — not console assets. The 50m provinces are 1.2 MB but
  carry 294 subdivisions worldwide, so Turkey's provinces are not in them at all.
  Cities are 964 KB for 7,342 named points and rivers 416 KB, and between them they
  are what turns a country outline into something you can navigate.

  <b>Province and district boundaries, roads and anything at street scale are a
  different job, and this server is the right tool for it:</b> import that data into
  the datastore, publish it, and point the console's basemap template at your own
  vector tiles. No size limit, no third-party licence, and it exercises the tile
  pipeline this product is built on.

  Names are ranked, so the map shows a dozen at world zoom and hundreds at regional
  zoom instead of 7,342 at once. Declutter does the rest.
*/
const cities = new ol.layer.Vector({
  source: new ol.source.Vector({
    url: `${location.origin}/console/ground-cities.geojson`,
    format: new ol.format.GeoJSON({
      dataProjection: "EPSG:4326",
      featureProjection: WEB_MERCATOR,
    }),
    attributions: "Natural Earth (public domain)",
  }),
  declutter: true,
  style: (feature, resolution) => {
    // A rank threshold that loosens as you zoom in: at world resolution only the
    // most important names, at city resolution most of them.
    const allowed = resolution > 20000 ? 2
      : resolution > 8000 ? 4
        : resolution > 2000 ? 6
          : resolution > 400 ? 8
            : 10;

    if (feature.get("rank") > allowed) {
      return null;
    }

    const capital = feature.get("capital") === 1;

    return new ol.style.Style({
      image: new ol.style.Circle({
        radius: capital ? 3.4 : 2.4,
        fill: new ol.style.Fill({ color: capital ? "#6b7d76" : "#8b9a94" }),
        stroke: new ol.style.Stroke({ color: "rgba(255,255,255,0.9)", width: 1 }),
      }),
      text: new ol.style.Text({
        text: feature.get("name") || "",
        offsetY: -11,
        font: `${capital ? "600 " : ""}11px system-ui, sans-serif`,
        fill: new ol.style.Fill({ color: "#43514c" }),
        stroke: new ol.style.Stroke({ color: "rgba(255,255,255,0.9)", width: 2.5 }),
      }),
    });
  },
});

/*
  <b>Our own vector tiles as extra ground, and this is the answer to "can we use
  OpenStreetMap data".</b> Yes — the data, not their tile service. Those are two
  different things and only the service has a policy that forbids this use.

  The route is: download an extract, import it here, publish it, and the server cuts
  tiles from it. Nothing is redistributed by us, so the ODbL obligations stay with
  whoever imported the data — attribution included, which is why the name is shown.
  There is no size ceiling and no third-party request at runtime.

  Turkey's 81 provincial boundaries went through it in one step: 6.2 MB of GeoJSON
  from Overpass became 5,433 rows, and the tile service answers 118 KB at z6 and
  serves the second request from cache.

  Set it with `?ground=hosted/tr_il`, or persistently in localStorage under
  `gis-ground-tiles`. Names come from whichever attribute the layer has.
*/
const GROUND_KEY = "gis-ground-tiles";

/*
  <b>Several grounds, not one.</b> A basemap of Turkish administrative boundaries is
  districts *and* roads; offering a single choice meant picking which half of the map
  to have. Stored as a list, drawn bottom-up in the order chosen.
*/
function chosenGrounds() {
  /*
    <b>`?ground=` is remembered, and forgetting it was the bug.</b> The parameter
    selected a ground for one page load and wrote nothing down, so opening the same
    viewer without it fell back to the vendored world — which read as "Natural Earth
    is still coming" and was exactly right. A link that chooses a ground has to be a
    way of choosing it, not a way of previewing it once.
  */
  const fromQuery = QUERY.get("ground");

  if (fromQuery !== null) {
    const chosen = fromQuery ? fromQuery.split(",").filter(Boolean) : [];
    localStorage.setItem(GROUND_KEY, JSON.stringify(chosen));
    return chosen;
  }

  try {
    const stored = JSON.parse(localStorage.getItem(GROUND_KEY) || "[]");
    return Array.isArray(stored) ? stored.filter(Boolean) : [stored].filter(Boolean);
  } catch {
    // An older single-value setting, kept working rather than discarded.
    const single = localStorage.getItem(GROUND_KEY);
    return single ? [single] : [];
  }
}

// Neutral and distinguishable, by position rather than by guessing from the name —
// a service called "roads" may hold anything, and styling on a name would be a
// convention nobody agreed to.
const GROUND_PENS = [
  { color: "#8b9aa4", width: 1.0 },
  { color: "#a8968a", width: 1.3 },
  { color: "#94a396", width: 0.8 },
  { color: "#9c93a8", width: 1.1 },
];

/*
  <b>Drawn by geometry, not by name.</b> A ground made only of grey lines is
  unreadable once there is more than one of them: an area has to read as an area, a
  line as a line, a point as a labelled point. Deciding that from the feature's own
  geometry is the one honest signal available — the alternative is matching service
  names, which is a convention nobody agreed to and which breaks the first time
  somebody names a layer differently.

  Labels come from whichever attribute looks like a name. Imported data does not have
  to be told what to call its name column, and the candidates are short and explicit
  rather than a guess at every possible spelling.
*/
const NAME_KEYS = ["ad", "name", "isim", "ilce", "il", "ref"];

function labelOf(feature) {
  for (const key of NAME_KEYS) {
    const value = feature.get(key);
    if (value) return String(value);
  }
  return "";
}

function tileGround(service, index) {
  const pen = GROUND_PENS[index % GROUND_PENS.length];

  return new ol.layer.VectorTile({
    declutter: true,
    source: new ol.source.VectorTile({
      format: new ol.format.MVT(),
      url: `${location.origin}/rest/services/`
        + `${service.split("/").map(encodeURIComponent).join("/")}`
        + "/VectorTileServer/tile/{z}/{y}/{x}.pbf",
      attributions: `${service} — imported data, attribution is the operator's`,
      maxZoom: 22,
    }),
    style: (feature, resolution) => {
      const kind = feature.getGeometry().getType();

      if (kind === "Polygon" || kind === "MultiPolygon") {
        // Land, or anything else with an inside. Filled so the sea becomes sea.
        return new ol.style.Style({
          fill: new ol.style.Fill({ color: "#efe9dc" }),
          stroke: new ol.style.Stroke({ color: "#b3a595", width: 0.8 }),
        });
      }

      if (kind === "Point" || kind === "MultiPoint") {
        // Places, with their names, thinned by zoom so a country of towns is not
        // 1,400 labels at once.
        if (resolution > 2000) return null;

        return new ol.style.Style({
          image: new ol.style.Circle({
            radius: 2.6,
            fill: new ol.style.Fill({ color: "#6b7d76" }),
            stroke: new ol.style.Stroke({ color: "rgba(255,255,255,0.9)", width: 1 }),
          }),
          text: new ol.style.Text({
            text: labelOf(feature),
            offsetY: -11,
            font: "11px system-ui, sans-serif",
            fill: new ol.style.Fill({ color: "#43514c" }),
            stroke: new ol.style.Stroke({ color: "rgba(255,255,255,0.9)", width: 2.5 }),
          }),
        });
      }

      return new ol.style.Style({
        stroke: new ol.style.Stroke({ color: pen.color, width: pen.width }),
      });
    },
  });
}

/*
  A rendered raster basemap, if the operator has one they may use.

  <b>This is the honest answer to "I want it to look like openstreetmap.org".</b> That
  screen is a rendered tile service — OSM Carto over a full planet import, with
  landuse, water, road casings and label placement. Boundary lines imported as vector
  data will never resemble it, and saying otherwise wasted several rounds.

  There are three ways to have that look and only one of them is available to this
  product today:

  · **openstreetmap.org's own tiles** — forbidden by their Tile Usage Policy for
    exactly this use, and they answered 403 when it was tried (ADR-020 §4a).
  · **A provider you have an account with** — MapTiler, Stadia, Thunderforest, Carto,
    Esri, Mapbox. Paste the URL with your key here. Nothing is shipped by us, and the
    terms are between you and them, which is why this is a box rather than a default.
  · **Rendering it ourselves** — a full extract, a cartographic style and a renderer.
    v1 has no renderer at all: ADR-004 is `DEFERRED` and v1-scope §3b cut rendering.
    Not a small task and not pending.

  Both template shapes are accepted, because ArcGIS writes one and everybody else
  writes the other, and a paste that silently draws nothing is the worst outcome.
*/
const BASEMAP_KEY = "gis-basemap";

/*
  <b>The rendered OpenStreetMap map, by default, with nothing to choose.</b>

  Their tile servers appeared to refuse us and the diagnosis was wrong in an
  important way: there is no 403. The same tile URL answers 200 twice over — a 6.9 KB
  PNG reading "Access blocked" when the request carries no identification, and the
  real 40 KB map when it carries a Referer. Our own `Referrer-Policy: no-referrer`
  was stripping it. The console now sends `strict-origin-when-cross-origin`, which is
  the origin and nothing else, so the credential that header protects still cannot
  leave.

  <b>Their Tile Usage Policy still says these servers are not for an application's
  default basemap</b>, and that is stated here rather than left out: it is the owner's
  decision, taken after the constraint was put in front of them, and the template
  below is how a deployment points somewhere it is entitled to. Attribution is
  displayed, which the licence does require.
*/
const OSM_TILES = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
const NO_BASEMAP = "none";

function basemapTemplate() {
  const fromQuery = QUERY.get("basemap");

  if (fromQuery !== null) {
    localStorage.setItem(BASEMAP_KEY, fromQuery || NO_BASEMAP);
    return fromQuery;
  }

  const stored = localStorage.getItem(BASEMAP_KEY);

  // Nothing stored means the default, not "off". Turning it off is a deliberate
  // act and is stored as such, so a default can change later without overriding
  // somebody who said no.
  if (stored === null) return OSM_TILES;
  return stored === NO_BASEMAP ? "" : stored;
}

function asXyz(template) {
  return template
    .replace(/\{level\}/gi, "{z}")
    .replace(/\{col\}/gi, "{x}")
    .replace(/\{row\}/gi, "{y}");
}

const basemapUrl = basemapTemplate();

const basemap = !basemapUrl ? null : new ol.layer.Tile({
  source: new ol.source.XYZ({
    url: asXyz(basemapUrl),
    crossOrigin: "anonymous",
    maxZoom: 19,
    attributions: localStorage.getItem(BASEMAP_KEY + "-credit")
      || (basemapUrl === OSM_TILES
        ? '© <a href="https://www.openstreetmap.org/copyright" target="_blank" '
          + 'rel="noreferrer">OpenStreetMap</a> contributors'
        : "Basemap: whoever you are entitled to use"),
  }),
});

const groundTiles = chosenGrounds();
const ourTiles = groundTiles.map((service, index) => tileGround(service, index));

/*
  <b>Natural Earth is the fallback, not the ground.</b> Asked plainly for OSM instead
  of Natural Earth, and with imported tiles present that is exactly what happens:
  none of the vendored files are drawn at all — no countries, no lakes, no rivers, no
  city list, no graticule.

  It stays in the build for the case it was added for: a server with nothing imported
  yet. A first-run console that draws a blank field is worse than one that draws a
  coarse world, and there is no third option that costs nothing.

  The land silhouette then has to come from the imported data too, which is why the
  Turkey polygon was fetched — a boundary drawn as lines gives no land to fill, and
  lines on an empty field are not a map either.
*/
const HAS_IMPORTED_GROUND = ourTiles.length > 0;

/**
 * Offers the tile services this server has as grounds to draw under the data.
 *
 * <b>This was a query parameter, which is the same as not existing.</b> The whole
 * answer to "can we use OpenStreetMap data" is *import it and serve your own
 * tiles*, and hiding the way to select it behind `?ground=` meant nobody would ever
 * see the answer working. Discovered from the services directory, so anything
 * published here shows up without a list to maintain.
 */
async function drawGroundPicker() {
  const box = $("grounds");
  if (!box || box.dataset.done) return;
  box.dataset.done = "1";

  let services = [];
  for (const folder of ["hosted", ""]) {
    try {
      const directory = await (await fetch(
        `${location.origin}/rest/services${folder ? "/" + folder : ""}?f=json`,
        { headers: { Accept: "application/json" } })).json();

      for (const service of directory.services || []) {
        if (service.type === "VectorTileServer" && service.name !== SERVICE) {
          services.push(service.name);
        }
      }
    } catch { /* a folder we cannot read is not a ground */ }
  }

  services = [...new Set(services)].sort();

  if (!services.length) {
    box.innerHTML = `<span>No tile service to use as a ground. Import one — an OSM extract,
      for instance — publish it, and it appears here.</span>`;
    return;
  }

  /*
    <b>The ground says what it is.</b> Which of two grounds was drawn was decided
    silently from a stored setting, so "Natural Earth is still coming" could not be
    told from "Natural Earth is what you chose". A default nobody can see is a
    default nobody can correct.
  */
  /*
    <b>What is drawn, not what was configured.</b> This said "Natural Earth — the
    vendored fallback" while the rendered OpenStreetMap basemap was on screen and
    Natural Earth was not drawn at all: the line described the *ground* setting and
    ignored the basemap above it. A state line that names the wrong layer is worse
    than none, because it is the thing somebody checks when the map looks wrong.

    Seen in a screenshot on 2026-08-16 — the first one taken of this page, which is
    its own lesson.
  */
  const parts = [];
  if (basemapUrl) {
    parts.push(basemapUrl === OSM_TILES
      ? "<b>OpenStreetMap</b> rendered tiles"
      : "<b>your basemap</b>");
  }
  if (groundTiles.length) {
    parts.push(`${groundTiles.length} imported layer${groundTiles.length === 1 ? "" : "s"}`);
  }
  if (!parts.length) {
    parts.push("<b>Natural Earth</b> — the vendored fallback");
  }

  const state = parts.join(" + ");

  box.innerHTML = `<span>Ground</span><span style="font-weight:400">${state}</span>`
    + services.map((name, i) => {
      const on = groundTiles.includes(name);
      const pen = GROUND_PENS[groundTiles.indexOf(name) % GROUND_PENS.length];
      return `<button data-ground="${escape(name)}" class="${on ? "on" : ""}">`
        + (on ? `<i style="display:inline-block;width:8px;height:2px;vertical-align:2px;`
          + `margin-right:5px;background:${pen.color}"></i>` : "")
        + `${escape(name)}</button>`;
    }).join("")
    + (groundTiles.length
      ? `<button data-ground="" title="Draw only the vendored Natural Earth ground">Clear</button>`
      : "");

  box.onclick = event => {
    const button = event.target.closest("button[data-ground]");
    if (!button) return;

    const name = button.dataset.ground;
    const next = !name
      ? []
      : groundTiles.includes(name)
        ? groundTiles.filter(g => g !== name)
        : [...groundTiles, name];

    localStorage.setItem(GROUND_KEY, JSON.stringify(next));

    // Reloaded rather than swapped live: the grounds sit under the data in a fixed
    // order, and rebuilding that order in place is more code than a reload is worth
    // on a page somebody opened to look at one layer.
    const url = new URL(location.href);
    url.searchParams.delete("ground");
    location.href = url.toString();
  };
}

/*
  <b>A rendered basemap is the ground; everything else is only there when it is not.</b>
  With OpenStreetMap drawn underneath there is nothing for Natural Earth's coastlines
  or its city list to add — they would print a second set of names over the first. Any
  imported layers still draw on top, because boundaries over a rendered map is exactly
  what an operator wants from them.
*/
const ground = basemap
  ? [basemap, ...ourTiles]
  : HAS_IMPORTED_GROUND
    ? ourTiles
    : [
    countries,
    groundLayer("ground-lakes.geojson", new ol.style.Style({
      fill: new ol.style.Fill({ color: WATER }),
      stroke: new ol.style.Stroke({ color: "#8fa7b2", width: 0.5 }),
    })),
    groundLayer("ground-rivers.geojson", new ol.style.Style({
      stroke: new ol.style.Stroke({ color: "#a8c2ce", width: 1 }),
    })),
    groundLayer("ground-graticule.geojson", new ol.style.Style({
      stroke: new ol.style.Stroke({ color: "rgba(120,140,133,0.22)", width: 0.6 }),
    })),
    cities,
  ];

// --------------------------------------------------------------------- the data
//
// <b>Read straight off the FeatureServer with OpenLayers' own EsriJSON format.</b>
// This server produces Esri JSON and ignores `f=geojson`, and it did not have to
// change: the reader exists.

const ESRI = new ol.format.EsriJSON();
const ACCENT = "#b8422e";

/*
  <b>The data is the one thing on this page that should be loud.</b> It was drawn in
  the console's own accent green, at the same value as the land, which made eight
  buildings on a beige continent invisible even when framed. A ground exists to be
  ignored; the layer exists to be seen, so it gets the one saturated colour here and
  the ground keeps none.
*/
const dataStyle = new ol.style.Style({
  fill: new ol.style.Fill({ color: "rgba(184,66,46,0.35)" }),
  stroke: new ol.style.Stroke({ color: ACCENT, width: 2.2 }),
  image: new ol.style.Circle({
    radius: 5.5,
    fill: new ol.style.Fill({ color: ACCENT }),
    stroke: new ol.style.Stroke({ color: "#fff", width: 1.4 }),
  }),
});

const dataSource = new ol.source.Vector();
const dataLayer = new ol.layer.Vector({ source: dataSource, style: dataStyle });

const map = new ol.Map({
  target: "map",
  layers: [...ground, dataLayer],
  view: new ol.View({
    projection: WEB_MERCATOR,
    center: [0, 0],
    zoom: 2,
  }),
  controls: ol.control.defaults.defaults({ attribution: true }).extend([
    // A scale bar and a readout of where the pointer is. Both are how a map says
    // what it is showing; without them a screen of shapes carries no units.
    new ol.control.ScaleLine({ units: "metric" }),
    new ol.control.MousePosition({
      projection: "EPSG:4326",
      className: "ol-mouse-position",
      coordinateFormat: coordinate => coordinate
        ? `${coordinate[0].toFixed(4)}, ${coordinate[1].toFixed(4)}` : "",
    }),
  ]),
});

/**
 * Fits the view to an extent, once the map has a size to fit it into.
 *
 * <b>This is why the viewer opened on the whole world with its data off screen.</b>
 * `fit` needs the map's pixel size; called before the first render it silently does
 * nothing — no error, no warning, and a header correctly reporting an extent the
 * view is not looking at. Waiting for a size makes the call happen at all.
 */
// Which load the pending fit belongs to. A later load cancels an earlier one's
// pending fit instead of both landing.
let fitFor = 0;

/**
 * Frames an extent once, for one load, and never again on its own.
 *
 * <b>The listener is gone deliberately.</b> This waited for a size by registering a
 * `postrender` handler, and the map re-framed itself whenever it was panned. Reading
 * the code, that listener is `once` and should not have persisted — so rather than
 * argue about which path was firing, the mechanism that could fire is removed:
 * frames are counted, not listened for, and a token makes a stale attempt a no-op.
 * An interaction must never move the view; only a load or the Frame button may.
 */
function fitWhenSized(extent, token = ++fitFor, frames = 0) {
  const go = () => {
    if (token !== fitFor) {
      return;   // a newer load owns the view now
    }

    const size = map.getSize();
    if (!size || !size[0] || !size[1]) {
      // Up to about a second of frames, then give up rather than wait forever.
      if (frames < 60) {
        requestAnimationFrame(() => fitWhenSized(extent, token, frames + 1));
      }
      return;
    }

    /*
      <b>The centre and the resolution, computed here rather than left to `fit`.</b>
      `fit` was called with a correct extent and the view stayed at continental
      zoom — it moved and did not scale, with no error to read. Rather than keep
      guessing at its preconditions, this is the arithmetic it would do: metres per
      pixel is the box divided by the pixels it has to fill, and the larger of the
      two axes wins so nothing is cropped.

      A degenerate box — one point, or a single vertical line — would divide to
      zero, so it is given a floor of one metre per pixel.
    */
    const padding = 48;
    const width = Math.max(extent[2] - extent[0], 0);
    const height = Math.max(extent[3] - extent[1], 0);

    const resolution = Math.max(
      width / Math.max(size[0] - padding * 2, 1),
      height / Math.max(size[1] - padding * 2, 1),
      1);

    const view = map.getView();
    view.setCenter([(extent[0] + extent[2]) / 2, (extent[1] + extent[3]) / 2]);
    view.setResolution(view.getConstrainedResolution(resolution) ?? resolution);
  };

  go();
}

/**
 * Loads one layer, replacing whatever was drawn.
 *
 * <b>Everything the server said is reported, including the limit.</b> A viewer
 * that quietly draws the first page of a large layer shows a picture that is
 * wrong in a way nobody can see; `exceededTransferLimit` is the server saying so
 * and it is repeated here rather than dropped.
 */
async function load(layerId, name) {
  dataSource.clear();
  $("attrs").className = "";

  const at = `${serviceUrl()}/FeatureServer/${encodeURIComponent(layerId)}`;

  /*
    <b>Paged, because one page of a boundary layer is not a boundary layer.</b> A
    single request returned the server's default page — 1,000 of 25,280 district
    lines — and the header said so honestly while the map showed scattered fragments
    that looked like bad data rather than a partial read. Being told and being shown
    are different things.

    Pages until the server stops saying there is more, up to a ceiling: a viewer that
    will happily fetch a million features is a viewer that hangs a browser. The
    ceiling is reported when it is hit, which keeps the honest half of the old
    behaviour.
  */
  const PAGE = 5000;
  const CEILING = 50000;

  const features = [];
  let truncated = false;
  let offset = 0;

  try {
    for (;;) {
      const query = `${at}/query?where=1%3D1&outFields=*&returnGeometry=true`
        + `&outSR=3857&f=json&resultRecordCount=${PAGE}&resultOffset=${offset}`;

      // Same origin, so the browsing cookie goes with it — the cookie this server
      // sets for GET and HEAD only, which is exactly a viewer's need.
      const response = await fetch(query, { headers: { Accept: "application/json" } });
      const payload = await response.json();

      if (!response.ok || payload.error) {
        throw new Error((payload.error && payload.error.message) || `${response.status}`);
      }

      const page = ESRI.readFeatures(payload, { featureProjection: WEB_MERCATOR });
      features.push(...page);
      offset += page.length;

      if (!payload.exceededTransferLimit || page.length === 0) break;

      if (offset >= CEILING) {
        truncated = true;
        break;
      }
    }
  } catch (e) {
    problem("The layer could not be read.",
      `${escape(e.message || e)}<br><br>Reading <code>${escape(at)}</code> at offset ${offset}.`);
    return;
  }

  dataSource.addFeatures(features);

  const extent = dataSource.getExtent();
  const empty = !features.length || !isFinite(extent[0]);

  $("facts").innerHTML =
    `<span><b>${features.length.toLocaleString()}</b> feature${features.length === 1 ? "" : "s"}`
    + (truncated
      ? ` — <b>more exist</b>, stopped at this viewer's ${CEILING.toLocaleString()} ceiling`
      : "")
    + `</span>`
    + (empty ? "<span>nothing to frame</span>" : `<span><code>${
        extent.map(v => Math.round(v)).join(", ")}</code></span>`)
    + `<span><code>EPSG:3857</code></span>`
    + (empty ? "" : `<button id="frame">Frame layer</button>`)
    + `<a href="${escape(at)}?f=html" style="color:var(--accent)">layer document</a>`;

  $("title").textContent = name ? `${SERVICE} — ${name}` : `${SERVICE} / ${layerId}`;

  if (empty) {
    problem("Nothing was returned.",
      "The layer answered with no features. It may be empty, or every feature may be "
      + "outside what was asked for.");
    return;
  }

  const frame = $("frame");
  if (frame) frame.onclick = () => fitWhenSized(extent);
  fitWhenSized(extent);
  drawGroundPicker();
}

// ------------------------------------------------------------------ attributes

map.on("singleclick", event => {
  const hit = map.getFeaturesAtPixel(event.pixel, {
    layerFilter: layer => layer === dataLayer,
    hitTolerance: 4,
  })[0];

  if (!hit) {
    $("attrs").className = "";
    return;
  }

  const properties = hit.getProperties();
  const rows = Object.keys(properties)
    .filter(key => key !== hit.getGeometryName())
    .map(key => `<tr><th>${escape(key)}</th><td>${escape(properties[key])}</td></tr>`)
    .join("");

  $("attrs").innerHTML = `<table>${rows || "<tr><td>no attributes</td></tr>"}</table>`;
  $("attrs").className = "on";
});

// ------------------------------------------------------------------------ boot

(async function start() {
  if (!SERVICE) {
    problem("No service named.",
      "This viewer is addressed as <code>?service=hosted/name&amp;layer=0</code>. The services "
      + "directory links here with both.");
    return;
  }

  $("directory").href = `/rest/services/${SERVICE.split("/")[0] || ""}`;

  // Named layer: draw it. Unnamed: the service's layers become a picker, because
  // "view the service" means choosing, not stacking.
  if (LAYER !== null) {
    await load(LAYER, null);
    return;
  }

  let document_;
  try {
    const response = await fetch(`${serviceUrl()}/FeatureServer?f=json`,
      { headers: { Accept: "application/json" } });
    document_ = await response.json();
  } catch (e) {
    problem("The service document could not be read.", escape(e.message || e));
    return;
  }

  const drawable = (document_.layers || []).filter(l => l.type !== "Group Layer");

  if (!drawable.length) {
    problem("Nothing to draw.", "This service lists no feature layers.");
    return;
  }

  $("picker").innerHTML = drawable.map((l, i) =>
    `<button data-id="${l.id}"${i === 0 ? ' class="on"' : ""}>${escape(l.name)}</button>`).join("");

  $("picker").onclick = event => {
    const id = event.target.dataset && event.target.dataset.id;
    if (!id) return;
    for (const button of $("picker").querySelectorAll("button")) {
      button.classList.toggle("on", button === event.target);
    }
    load(id, event.target.textContent);
  };

  await load(drawable[0].id, drawable[0].name);
})();
