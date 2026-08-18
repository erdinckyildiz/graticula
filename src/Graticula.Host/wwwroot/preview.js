// A small picture of what is in a layer, drawn from the layer's own data.
//
// <b>Why this exists, and why it is not a thumbnail.</b> The owner, looking at ArcGIS Server
// Manager's service list: *"bir de thumbnailler var. girmeden görebiliyoruz."* — there are
// thumbnails too, you can see it without going in. They are right about the value: a list of
// forty service names tells you nothing about which one holds the roads.
//
// <b>Theirs is a rendered map image and we cannot make one.</b> ADR-004 is `DEFERRED` and
// v1-scope puts WMS, MapServer, ImageServer and OGC Maps out of scope, so this server has no
// path that turns geometry into pixels. Faking one — a stock icon per geometry type, the way
// their geoprocessing services get a toolbox picture — would be decoration rather than
// information, and this project's rule is that a picture of the data has to come from the data.
//
// <b>So the browser draws it, from one query.</b> A few hundred features at a precision matched
// to eighty pixels, through `maxAllowableOffset`, into a canvas with plain 2D calls. No SDK, no
// tiles, no renderer: one request per layer, and it works for a registered table as well as a
// hosted one — which a tile-based preview could not, since tiles are hosted-only (Q-67).
//
// <b>It is an indication, not the map.</b> Two hundred simplified features out of a hundred
// thousand is a hint about shape and place, and the title says so on hover. A preview that
// looked authoritative would be the worse mistake.

// <b>Eight hundred, measured rather than guessed.</b> Two hundred was the first choice and it
// produced a picture nobody could read: `tr_il` is 5,433 short polylines, and two hundred
// two-vertex segments on an eighty-pixel canvas is a scatter of specks that reads as *this
// layer is nearly empty*. The sample was never the problem — its bounding box already covered
// 75% of the layer's extent — the density was. Eight hundred covers 96% and costs 115 KB and
// 34 ms against a local server; two thousand costs 290 KB and 114 ms for a picture no better.
const PREVIEW_FEATURES = 800;

// About a metre at the equator, which is two orders of magnitude finer than a hundred-pixel
// box can show. It is here for the payload rather than the picture: full float coordinates on
// short segments are most of the bytes, and dropping them took `tr_yol` from 116 KB to 90.
const PREVIEW_PRECISION = 4;

// Session-scoped, because a preview is worth exactly one request per layer per visit: the data
// changes rarely, the picture is 80 pixels wide, and a list of forty services must not fire
// forty requests every time somebody switches folder.
const previews = new Map();

/**
 * Draws a layer into a canvas, or says why it could not.
 *
 * @param {HTMLCanvasElement} canvas where to draw
 * @param {string} url the layer's address, as the content or service document gives it
 * @param {string} colour the layer's own colour, from the server's drawingInfo
 */
async function drawPreview(canvas, url, colour) {
  const key = `${url}|${colour}`;

  try {
    // <b>The cache holds the promise, not the resolved shape, and holding the shape made it miss
    // exactly when it mattered.</b> `previews.set` ran *after* the await, so two overlapping passes
    // over the same list both found nothing and both fetched: measured on the Services screen at
    // folder `hosted`, 34 requests were issued for 20 distinct layers — 14 of them exact duplicates,
    // 7 services fetched twice each, in 612 KB and 510 ms. Caching the in-flight promise makes the
    // second caller await the first one's answer.
    //
    // <b>This is the change that makes a second surface affordable.</b> Ten thumbnails on a picker
    // over the same services the listing behind it already drew cost nothing the second time; without
    // it, adding a surface doubles the bytes. Found in the design review of 2026-08-18, which measured
    // it rather than inferring it.
    let pending = previews.get(key);

    if (!pending) {
      pending = readShape(url, canvas.width);
      previews.set(key, pending);
    }

    const shape = await pending;

    // <b>The server's colour, not the caller's.</b> `readShape` reads the layer document, which
    // carries `drawingInfo` since ADR-033 — so the picture is drawn in the colour every other
    // client draws that layer in, and the caller's argument is only what to fall back on.
    colour = shape.colour || colour;

    if (shape.features.length === 0) {
      label(canvas, "empty");
      return;
    }

    paint(canvas, shape, colour);
    canvas.title = `${shape.features.length}${shape.partial ? " of more" : ""} of this layer's `
      + `features, simplified to fit — an indication rather than the map`;
  } catch (e) {
    // <b>A rejection is not kept.</b> Caching the promise means caching a failed one too, and a layer
    // that answered 503 once while its service was starting would then never draw again for the rest
    // of the session — a transient fault made permanent by the fix for a different one.
    previews.delete(key);

    // A stopped service answers 503 here and a layer without Query answers 400. Both are
    // expected states rather than faults, so the box says the state instead of going blank.
    // Read off `e.status`, which `api` attaches for exactly this.
    label(canvas, e.status === 503 ? "stopped" : "no preview");
    canvas.title = e.message || String(e);
  }
}

/**
 * One query, at a precision matched to the box it will be drawn in.
 *
 * @param {string} url the layer's address
 * @param {number} across how many pixels wide the picture will be
 */
async function readShape(url, across) {
  const document_ = await api(`${url}?f=json`);
  const extent = document_.extent;

  if (!extent || extent.xmin === undefined) {
    throw new Error("This layer's extent is unknown, so there is nothing to frame a preview in.");
  }

  // <b>The offset is the point of this query.</b> Without it a preview of a coastline is
  // megabytes of vertices to draw a hundred pixels; with it PostGIS does the simplification
  // and sends what the picture can actually show. The width of the extent over the width of
  // the box, which is the definition ArcGIS gives the parameter — so it is taken from the
  // canvas rather than from a constant, and a bigger box asks for a finer answer.
  const offset = Math.max(extent.xmax - extent.xmin, extent.ymax - extent.ymin) / across;

  const answer = await api(`${url}/query?where=1%3D1&returnGeometry=true&outFields=`
    + `&resultRecordCount=${PREVIEW_FEATURES}&maxAllowableOffset=${offset}`
    + `&geometryPrecision=${PREVIEW_PRECISION}&f=json`);

  const rgba = document_.drawingInfo?.renderer?.symbol?.color;

  return {
    extent,
    kind: (document_.geometryType || "").replace("esriGeometry", ""),
    colour: Array.isArray(rgba) && rgba.length >= 3
      ? "#" + rgba.slice(0, 3).map(c => Number(c).toString(16).padStart(2, "0")).join("")
      : null,
    // <b>Filtered, because a row with no geometry is a real answer.</b> `tr_yol` has one, and
    // it arrived as a feature object with no `geometry` key at all — which threw here before
    // the filter, and would have turned one null row into a service with no preview.
    features: (answer.features || []).map(f => f.geometry).filter(Boolean),

    // Whether there are more than we asked for, so the title can say *of more* without a
    // second request. A count query would be honest too, and would double the requests this
    // screen makes for a number that only appears on hover.
    partial: answer.exceededTransferLimit === true,
  };
}

/** The 2D drawing, and nothing about where the data came from. */
function paint(canvas, shape, colour) {
  const context = canvas.getContext("2d");
  const width = canvas.width;
  const height = canvas.height;

  context.clearRect(0, 0, width, height);

  const { xmin, ymin, xmax, ymax } = shape.extent;

  // Fit the extent into the box without distorting it: a preview that stretched Turkey into a
  // square would be a picture of the box rather than of the data.
  const scale = Math.min(width / Math.max(xmax - xmin, 1e-9), height / Math.max(ymax - ymin, 1e-9));
  const offsetX = (width - (xmax - xmin) * scale) / 2;
  const offsetY = (height - (ymax - ymin) * scale) / 2;

  const px = x => offsetX + (x - xmin) * scale;
  const py = y => height - offsetY - (y - ymin) * scale;   // north is up

  // <b>Full-strength strokes and a light fill.</b> The first version stroked at a third
  // opacity and the line layers came out invisible: a road network is thousands of one-pixel
  // segments, and a third of a pixel of colour is nothing. Polygons keep the light fill —
  // eighty overlapping provinces at full opacity is a solid block.
  context.lineJoin = "round";
  context.strokeStyle = colour;
  context.lineWidth = 1.1;

  for (const geometry of shape.features) {
    if (geometry.x !== undefined) {
      context.beginPath();
      context.arc(px(geometry.x), py(geometry.y), 1.2, 0, Math.PI * 2);
      context.fillStyle = colour;
      context.fill();
      continue;
    }

    for (const part of geometry.rings || geometry.paths || []) {
      context.beginPath();
      part.forEach(([x, y], i) => (i ? context.lineTo(px(x), py(y)) : context.moveTo(px(x), py(y))));

      if (geometry.rings) {
        context.closePath();
        context.fillStyle = colour + "44";
        context.fill();
      }

      context.stroke();
    }
  }
}

/** A word in the box, for the states that have no picture. */
function label(canvas, text) {
  const context = canvas.getContext("2d");
  context.clearRect(0, 0, canvas.width, canvas.height);
  context.fillStyle = "#7d8f8b";
  context.font = "11px ui-monospace, monospace";
  context.textAlign = "center";
  context.fillText(text, canvas.width / 2, canvas.height / 2 + 4);
}
