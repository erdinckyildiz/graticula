// The map's ground: what is under your data when no tile service is configured.
//
// <b>Shared by the console's map panel and the standalone viewer, in one file
// rather than copied into both.</b> Today's lesson twice over: a second copy of a
// thing nobody re-audits is how `havingClause` survived beside a working `where`
// parser, and how one page kept an inline script after the other was fixed. Two
// pages drawing the same ground differently would be the same shape of mistake in
// a smaller place.
//
// <b>Vendored data, not a tile service.</b> Natural Earth 1:110m — countries,
// lakes — plus a graticule generated from arithmetic. All public domain, ~196 KB,
// served from this origin. So no usage policy governs it, nothing can start
// answering 403, an air-gapped deployment (Q-15) works, and the
// Content-Security-Policy needs no third-party origin. ADR-020 §4a.
//
// <b>Coarse on purpose.</b> Its whole job is *where in the world is this*. At
// street zoom nothing but a real tile service will help, and the console's map
// panel takes a template for exactly that.

/** The ocean. Everything else is drawn on top of it. */
const GROUND_WATER = "#dce7ec";

/** Where the chosen imported grounds are remembered. Shared with the OpenLayers viewer. */
const GROUND_TILES_KEY = "gis-ground-tiles";

/**
 * The imported tile services chosen as the ground, if any.
 *
 * <b>One rule, read from one place, because writing it in one page of three was the
 * bug.</b> The OpenLayers viewer was taught to prefer imported OSM tiles over the
 * vendored Natural Earth files, and the console's map panel and the SDK page were
 * not — so "Natural Earth is still showing" was true in two of the three places
 * somebody might be looking. Putting the decision here is the same fix as putting
 * the cartography here.
 */
function chosenGroundTiles() {
  try {
    const stored = JSON.parse(localStorage.getItem(GROUND_TILES_KEY) || "[]");
    return Array.isArray(stored) ? stored.filter(Boolean) : [stored].filter(Boolean);
  } catch {
    const single = localStorage.getItem(GROUND_TILES_KEY);
    return single ? [single] : [];
  }
}

/**
 * The ground layers, bottom first.
 *
 * <b>Imported tiles replace the vendored files entirely rather than drawing over
 * them.</b> Two grounds competing is unreadable, and the vendored world exists for
 * the server that has imported nothing yet — a first run that draws a blank field is
 * worse than one that draws a coarse world.
 *
 * @param {object} modules The SDK modules — GeoJSONLayer always, VectorTileLayer to
 *   be able to use an imported ground.
 * @returns {object[]} Layers to add beneath the data.
 */
function groundLayers({ GeoJSONLayer, VectorTileLayer }) {
  const chosen = chosenGroundTiles();

  if (chosen.length && VectorTileLayer) {
    return chosen.map(service => new VectorTileLayer({
      url: `${location.origin}/rest/services/${service}/VectorTileServer`,
      title: service,
      opacity: 0.9,
    }));
  }

  const at = name => `${location.origin}/console/${name}`;

  // Countries rather than land alone. Land alone drew one shape with nothing in
  // it — no borders, no water, no names — which reads as a blank fill rather than
  // as a map, and the point of a ground is to be recognisable at a glance.
  const countries = new GeoJSONLayer({
    url: at("ground-countries.geojson"),
    title: "Countries",
    copyright: "Natural Earth (public domain)",
    renderer: {
      type: "simple",
      symbol: {
        type: "simple-fill",
        color: [243, 241, 234, 255],
        outline: { color: [186, 196, 190, 255], width: 0.6 },
      },
    },

    // Names, but only while they can be read. A country label at city zoom is a
    // word floating over a street with nothing to do with it.
    labelsVisible: true,
    labelingInfo: [{
      labelExpressionInfo: { expression: "$feature.name" },
      maxScale: 9000000,
      symbol: {
        type: "text",
        color: [120, 134, 128, 255],
        haloColor: [243, 241, 234, 200],
        haloSize: 1,
        font: { size: 9.5, family: "sans-serif" },
      },
    }],
  });

  const lakes = new GeoJSONLayer({
    url: at("ground-lakes.geojson"),
    title: "Lakes",
    copyright: "Natural Earth (public domain)",
    renderer: {
      type: "simple",
      symbol: {
        type: "simple-fill",
        color: [220, 231, 236, 255],
        outline: { color: [186, 196, 190, 255], width: 0.4 },
      },
    },
  });

  // Above the countries, because a graticule is a reading aid and being covered by
  // land makes it stop being one.
  const graticule = new GeoJSONLayer({
    url: at("ground-graticule.geojson"),
    title: "Graticule",
    renderer: {
      type: "simple",
      symbol: { type: "simple-line", color: [176, 190, 184, 110], width: 0.5 },
    },
    maxScale: 4000000,
  });

  return [countries, lakes, graticule];
}
