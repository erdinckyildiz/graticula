/*
  Analysis — ADR-222 condition 7: Studio runs the Spatial Analysis Service's tools from a hosted layer's page and from its
  map, through this one script.

  <b>One copy, two pages.</b> The item page (console.js) and the map (webmap.js) are separate documents, and the tools'
  questions, their checks and what each sends by the documented parameter names were written once for the item page. A
  second copy for the map would be the shape D-130 records: the next tool added to one page and not the other. Each page
  says what is its own — how it asks the server, how it tells the reader, where the questions are drawn and what a
  result becomes — and the rest is here.

  <b>Inside a function, with one name outside it.</b> Both pages are classic scripts sharing one global namespace, where
  a second function of the same name silently replaces the first (a memory the project keeps for that reason).
*/
(() => {
  /** Where the Spatial Analysis Service is — what portals/self names as helperServices.analysis. */
  const SERVICE = "/rest/services/System/SpatialAnalysisTools/GPServer";

  /**
   * The tools, in Map Viewer's order — summarize, find locations, proximity, manage data (design review) — what each says it
   * does, the kind of layer it starts from, and the kind its second layer must be.
   */
  const TOOLS = [
    { task: "AggregatePoints", label: "Aggregate points", short: "counts", result: "aggregatedLayer", from: "points",
      second: "polygons", secondLabel: "Polygons to count in", binnable: true,
      says: "How many of these points fall in each polygon of another layer, or in square or hexagon bins." },
    { task: "JoinFeatures", label: "Join features", short: "joined", result: "outputLayer", second: "any",
      secondLabel: "Join with", says: "This layer's features with another layer's attributes, where they meet or are near." },
    { task: "SummarizeWithin", label: "Summarize within", short: "summary", result: "resultLayer",
      second: "polygons", secondLabel: "Polygons to summarize in", binnable: true,
      says: "How many of these features, and how much of their length or area, fall in each polygon or bin." },
    { task: "FindCentroids", label: "Find centroids", short: "centroids", result: "outputLayer", notFrom: "points",
      says: "A point at the centre of each feature, or on it." },
    { task: "CreateBuffers", label: "Create buffers", short: "buffers", result: "bufferLayer",
      says: "Areas within a distance of each feature, measured on the ground." },
    { task: "DissolveBoundaries", label: "Dissolve boundaries", short: "dissolved", result: "dissolvedLayer", from: "polygons",
      says: "Polygons that share a value, or that touch, merged into one." },
    { task: "MergeLayers", label: "Merge layers", short: "merged", result: "mergedLayer", second: "same",
      secondLabel: "Merge with", says: "This layer's features and another's of the same kind, in one layer." },
    { task: "OverlayLayers", label: "Overlay layers", short: "overlay", result: "outputLayer", second: "any",
      secondLabel: "Overlay layer", says: "Where this layer meets another, both together, or this one less the other." },
  ];

  /** What the server's parameter names are called on this screen, for a refusal it words in them. */
  const LABELS = {
    inputLayer: "this layer", pointLayer: "this layer", polygonLayer: "Polygons to count in", overlayLayer: "Overlay layer",
    mergeLayer: "Merge with", distances: "Distances", outputName: "Result layer name", dissolveFields: "the fields chosen",
    targetLayer: "this layer", joinLayer: "Join with", summaryLayer: "this layer", sumWithinLayer: "Polygons to summarize in",
    binSize: "Bin size", spatialRelationshipDistance: "Distance", summaryFields: "the fields chosen",
  };

  /** The relationships Join features offers, in ArcGIS's words and in the screen's. */
  const RELATIONSHIPS = [
    ["Intersects", "Intersects"], ["WithinDistance", "Within a distance"], ["Contains", "Contains"],
    ["CompletelyContains", "Completely contains"], ["Within", "Within"], ["CompletelyWithin", "Completely within"],
    ["IdenticalTo", "Identical to"],
  ];

  const UNITS = ["Meters", "Kilometers", "Feet", "Miles", "NauticalMiles", "Yards"];
  const AREA_UNITS = ["SquareMeters", "SquareKilometers", "Hectares", "Acres", "SquareMiles", "SquareFeet", "SquareYards"];

  const $ = id => document.getElementById(id);
  const h = value => String(value ?? "").replace(/[&<>"']/g,
    c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const lowerFirst = text => text.charAt(0).toLowerCase() + text.slice(1);
  const unitLabel = u => u === "NauticalMiles" ? "Nautical miles"
    : u.replace(/^Square(\w)/, "Square $1").toLowerCase().replace(/^./, c => c.toUpperCase());
  const kindOf = geometryType => /Point/.test(geometryType || "") ? "points"
    : /Polyline/.test(geometryType || "") ? "lines" : "polygons";
  const checkedTool = () => TOOLS.find(t => t.task === $("azTool")?.value);

  /** The page that opened it: how it asks, tells and draws, and what a result becomes. */
  let host = null;

  /** The run in progress: its job and tool, its result's name, and whether the page still listens. */
  let running = null;

  /** The layer analysed: its address, name, geometry and fields, as its own document says. */
  let layer = null;

  /** The other layers this reader may use as a second input, each with the kind of geometry it holds and its service. */
  let others = [];

  /** Why the layers could not be read, when they could not; said in place of "you have none". */
  let othersUnread = null;

  /**
   * Opens the tools on a layer. `opened` is the page's: `fetch(path, options)`, `notify(text, tone)`, the `body` and
   * `foot` elements to draw in, `lead` (the sentence above the tools) and `succeeded(run, value)`, which resolves to the
   * markup that says the result is ready and the element to focus.
   */
  async function open(target, opened) {
    host = opened;
    running = null;
    host.body.innerHTML = `<p class="hint">Reading the layer…</p>`;
    host.foot.innerHTML = "";

    try {
      const [doc, found] = await Promise.all([
        host.fetch(`${new URL(target.url).pathname}?f=json`),
        host.fetch("/content/layers").catch(e => ({ unread: e.message || String(e) })),
      ]);
      layer = { url: target.url, name: target.name, kind: kindOf(doc.geometryType),
        fields: (doc.fields || []).filter(f => !/OID|GlobalID|Geometry/.test(f.type || "")) };

      // Each candidate's own geometry, so a list offers only what its tool can use (design review: the default
      // polygon layer for Aggregate points was a point layer, and every run of it failed). <b>From one listing, every
      // layer this reader may see with its geometry (D-322)</b>: this read the portal search's first 100 services and
      // then each one's layer 0, so a layer past the hundredth, or not first in its service, could not be chosen.
      const self = new URL(target.url).pathname;
      const listed = [...(found.mine || []), ...(found.shared || []), ...(found.notShared || [])]
        .filter(l => l.geometry && l.status === "started" && l.url !== self);
      othersUnread = found.unread || null;
      others = listed.map(l => ({
        url: new URL(l.url, location.origin).href,
        title: l.title || l.name,
        service: l.service,
        layerId: l.layerId,
        kind: /Point/.test(l.geometry) ? "points" : /Line/.test(l.geometry) ? "lines" : "polygons",
      })).sort((a, b) => a.title.localeCompare(b.title, undefined, { numeric: true, sensitivity: "base" }));
    } catch (e) {
      host.body.innerHTML = `<p class="bad">${h(e.message || e)}</p>`;
      return;
    }

    const tools = TOOLS.filter(t => (!t.from || t.from === layer.kind) && t.notFrom !== layer.kind);

    // <b>One select, its sentence under it</b> (design review 2026-10-29): eight tools as radios with a sentence each
    // filled the first screen, so choosing one changed nothing a reader could see. Map Viewer shows one tool at a time.
    host.body.innerHTML = `
      <p class="hint">${host.lead(h(layer.name))}</p>
      <div class="stacked"><label for="azTool">Tool</label>
        <select id="azTool" name="azTool" aria-describedby="azToolSays">${tools.map(t =>
          `<option value="${t.task}">${h(t.label)}</option>`).join("")}</select>
        <span class="hint" id="azToolSays">${h(tools[0].says)}</span></div>
      <div id="azOptions"></div>
      <div class="stacked"><label for="azName">Result layer name</label>
        <input type="text" id="azName" maxlength="40" autocomplete="off" spellcheck="false" aria-describedby="azNameHint">
        <span class="hint" id="azNameHint">Letters, digits, _ and -, up to 40. A name already in use is refused.</span></div>`;

    // <b>The status line is made once and only its words change</b> (design review): a live region inserted with its
    // text already in it is often not announced, and the footer was being rebuilt around it on every run.
    host.foot.innerHTML = `<p class="hint fill" id="analyzeSays" role="status" aria-live="polite"></p><span id="analyzeButtons"></span>`;
    drawFoot(false);
    drawOptions();
    $("azTool").focus();
  }

  /** The footer: Cancel and Run, or, while a job runs, Stop and Close — closing lets it finish and says so. */
  function drawFoot(busy) {
    $("analyzeButtons").innerHTML = busy
      ? `<button type="button" class="ghost" id="analyzeClosing">Close</button>
         <button type="button" id="analyzeStop">Stop</button>`
      : `<button type="button" class="ghost" id="analyzeCancel">Cancel</button>
         <button type="button" class="primary" id="analyzeGo">Run</button>`;
  }

  /** The second layers a tool may use: the kind it needs, never the layer itself. */
  function candidates(tool) {
    const overlay = document.querySelector('input[name="azOverlay"]:checked')?.value;
    return others.filter(o =>
      tool.second === "polygons" ? o.kind === "polygons"
      : tool.second === "same" ? o.kind === layer.kind
      : tool.task === "OverlayLayers" && (overlay === "Erase" || overlay === "Union") ? o.kind === "polygons"
      : true);
  }

  /** The second layer's list, said plainly when nothing fits — or nothing, when bins take its place. */
  function drawSecond(tool) {
    const box = $("azSecond");
    if (!box) return;

    const where = document.querySelector('input[name="azWhere"]:checked')?.value;
    const bins = $("azBins");
    if (bins) bins.hidden = !where || where === "layer";
    box.hidden = Boolean(where) && where !== "layer";
    if (box.hidden) { box.innerHTML = ""; return; }

    const fits = candidates(tool);
    const what = tool.second === "polygons" ? "polygon layer" : tool.second === "same" ? `layer of ${layer.kind}`
      : tool.task === "JoinFeatures" ? "layer" : "layer of polygons";
    // <b>Told apart within this list, ignoring case</b> (design review 2026-10-31): a name twice gets its service, and
    // twice in one service its layer too. Nothing is chosen until somebody chooses, and the line under the select says
    // where the chosen layer is — on a narrow screen the end of an option is cut off.
    const key = text => text.toLocaleLowerCase();
    const named = o => key(o.title), served = o => key(`${o.title} (${o.service})`);
    const count = (f, o) => fits.filter(x => f(x) === f(o)).length;
    const shown = o => count(named, o) < 2 ? o.title
      : count(served, o) < 2 ? `${o.title} (${o.service})` : `${o.title} (${o.service} / ${o.layerId})`;
    box.innerHTML = othersUnread
      ? `<span class="label">${h(tool.secondLabel)}</span><p class="hint bad" id="azNoSecond">Could not read your layers: ${h(othersUnread)}.</p>`
      : fits.length
      ? `<label for="azOther">${h(tool.secondLabel)}</label>
         <select id="azOther" aria-describedby="azOtherFrom"><option value="">Choose a layer</option>${fits.map(o =>
           `<option value="${h(o.url)}">${h(shown(o))}</option>`).join("")}</select>
         <span class="hint" id="azOtherFrom"></span>`
      : `<span class="label">${h(tool.secondLabel)}</span><p class="hint" id="azNoSecond">You have no other ${what} to use here.</p>`;
  }

  /** Where a binnable tool counts: another layer's polygons, or bins it draws, with their size. */
  function whereMarkup(legend) {
    return `
      <fieldset class="offered"><legend>${h(legend)}</legend>
        <label class="check"><input type="radio" name="azWhere" value="layer" checked> <span>Polygons of another layer</span></label>
        <label class="check"><input type="radio" name="azWhere" value="Square"> <span>Square bins</span></label>
        <label class="check"><input type="radio" name="azWhere" value="Hexagon"> <span>Hexagon bins</span></label>
      </fieldset>
      <div class="stacked" id="azSecond"></div>
      <div id="azBins" hidden>
        <div class="azrow">
          <div class="stacked"><label for="azBinSize">Bin size</label>
            <input type="text" id="azBinSize" value="1" inputmode="decimal" aria-describedby="azBinSizeHint"></div>
          <div class="stacked"><label for="azBinUnit">Units</label>
            <select id="azBinUnit">${UNITS.map(u => `<option value="${u}"${u === "Kilometers" ? " selected" : ""}>${h(unitLabel(u))}</option>`).join("")}</select></div>
        </div>
        <p class="hint" id="azBinSizeHint">A square's side, or a hexagon's width between flat sides.</p>
      </div>`;
  }

  /** The numeric fields a summary may total, with one statistic for all of them. */
  function summaryMarkup() {
    const numeric = layer.fields.filter(f => /Integer|Single|Double/.test(f.type || ""));
    if (numeric.length === 0) return "";
    return `
      <fieldset class="offered"><legend>Also summarize</legend>
        ${numeric.map(f => `<label class="check"><input type="checkbox" data-az-sum="${h(f.name)}"> <span>${h(f.alias || f.name)}</span></label>`).join("")}
        <div class="stacked"><label for="azStatistic">As</label>
          <select id="azStatistic">${[["Sum", "Total"], ["Mean", "Average"], ["Min", "Smallest"], ["Max", "Largest"]]
            .map(([value, label]) => `<option value="${value}">${label}</option>`).join("")}</select></div>
      </fieldset>`;
  }

  /** The chosen tool's own questions, and a name for its result that says what it is. */
  function drawOptions() {
    const tool = checkedTool();
    if (!tool || !layer) return;

    const fieldBoxes = layer.fields.map(f => `<label class="check"><input type="checkbox" data-az-field="${h(f.name)}">
      <span>${h(f.alias || f.name)}</span></label>`).join("");
    const polygons = layer.kind === "polygons";

    $("azOptions").innerHTML = {
      CreateBuffers: `
        <div class="stacked"><label for="azDistances">Distances</label>
          <input type="text" id="azDistances" value="100" inputmode="decimal" aria-describedby="azDistancesHint">
          <span class="hint" id="azDistancesHint">One or more, separated by commas: 100, 250, 500.</span></div>
        <div class="stacked"><label for="azUnits">Units</label>
          <select id="azUnits">${UNITS.map(u => `<option value="${u}">${h(unitLabel(u))}</option>`).join("")}</select></div>
        <label class="check"><input type="checkbox" id="azDissolve"> <span>One area per distance, for all features together</span></label>
        <label class="check"><input type="checkbox" id="azRings"> <span>Rings: each distance leaves out the smaller ones</span></label>`,
      DissolveBoundaries: `
        <fieldset class="offered"><legend>Merge polygons that share</legend>
          ${fieldBoxes || `<p class="hint">This layer has no fields to dissolve by.</p>`}
          <p class="hint">Choose none to merge every polygon that touches or overlaps another.</p></fieldset>
        <label class="check"><input type="checkbox" id="azMulti" checked> <span>Keep each group as one feature, even in separate parts</span></label>`,
      OverlayLayers: `
        <fieldset class="offered"><legend>Keep</legend>
          <label class="check"><input type="radio" name="azOverlay" value="Intersect" checked> <span>Where the two meet</span></label>
          ${polygons ? `<label class="check"><input type="radio" name="azOverlay" value="Union"> <span>Both, split where they meet</span></label>` : ""}
          <label class="check"><input type="radio" name="azOverlay" value="Erase"> <span>This layer, less the other's area</span></label>
        </fieldset>
        <div class="stacked" id="azSecond"></div>`,
      MergeLayers: `
        <div class="stacked" id="azSecond"></div>
        <p class="hint">Fields of the same name become one; the others are kept apart.</p>`,
      FindCentroids: `
        <label class="check"><input type="checkbox" id="azInside"> <span>On each feature, where its centre would fall outside it</span></label>`,
      AggregatePoints: `
        ${whereMarkup("Count in")}
        <label class="check" id="azKeepRow"><input type="checkbox" id="azKeep" checked> <span>Keep polygons with no points, counted 0</span></label>`,
      SummarizeWithin: `
        ${whereMarkup("Summarize in")}
        ${layer.kind === "points" ? "" : `
        <div class="stacked"><label for="azShapeUnits">${layer.kind === "lines" ? "Length in" : "Area in"}</label>
          <select id="azShapeUnits">${(layer.kind === "lines" ? UNITS : AREA_UNITS)
            .map(u => `<option value="${u}">${h(unitLabel(u))}</option>`).join("")}</select></div>`}
        ${summaryMarkup()}`,
      JoinFeatures: `
        <div class="stacked" id="azSecond"></div>
        <div class="stacked"><label for="azRelation">Spatial relationship</label>
          <select id="azRelation">${RELATIONSHIPS.map(([value, label]) => `<option value="${value}">${h(label)}</option>`).join("")}</select></div>
        <div class="azrow" id="azDistanceRow" hidden>
          <div class="stacked"><label for="azJoinDistance">Distance</label>
            <input type="text" id="azJoinDistance" value="100" inputmode="decimal"></div>
          <div class="stacked"><label for="azJoinUnits">Units</label>
            <select id="azJoinUnits">${UNITS.map(u => `<option value="${u}">${h(unitLabel(u))}</option>`).join("")}</select></div>
        </div>
        <fieldset class="offered"><legend>For each of this layer's features</legend>
          <label class="check"><input type="radio" name="azJoinOp" value="JoinOneToOne" checked> <span>One feature, with how many matched</span></label>
          <label class="check"><input type="radio" name="azJoinOp" value="JoinOneToMany"> <span>One feature for every match</span></label>
        </fieldset>
        <label class="check"><input type="checkbox" id="azKeepAll"> <span>Keep this layer's features that match nothing</span></label>`,
    }[tool.task];
    drawSecond(tool);

    const name = $("azName");
    if (name && (!name.value || name.dataset.made === name.value)) {
      name.value = nameFor(`${layer.name}_${tool.short}`);
      name.dataset.made = name.value;
    }
  }

  /** A layer name the server takes: letters, digits, _ and -, starting with a letter or digit, up to 40. */
  function nameFor(text) {
    return (String(text).replace(/[^A-Za-z0-9_-]+/g, "_").replace(/^[^A-Za-z0-9]+/, "") || "result").slice(0, 40);
  }

  /** The distances typed, or null when one is not a number above zero. */
  function distances() {
    const parts = ($("azDistances")?.value || "").split(/[,;\s]+/).filter(Boolean);
    const numbers = parts.map(Number);
    return parts.length && numbers.every(n => Number.isFinite(n) && n > 0) ? numbers : null;
  }

  /** A binnable tool's areas: the other layer, or bins of the size typed. */
  function whereForm(form, parameter, other) {
    const where = document.querySelector('input[name="azWhere"]:checked')?.value || "layer";
    if (where === "layer") { form.set(parameter, other); return; }
    form.set("binType", where);
    form.set("binSize", $("azBinSize").value.trim());
    form.set("binSizeUnit", $("azBinUnit").value);
  }

  /** What the chosen tool is sent, by the parameter names the service documents. */
  function formFor(tool, name) {
    const form = new FormData();
    form.set("f", "json");
    form.set("outputName", JSON.stringify({ serviceProperties: { name } }));
    const input = JSON.stringify({ url: layer.url });
    const other = $("azOther")?.value ? JSON.stringify({ url: $("azOther").value }) : null;

    switch (tool.task) {
      case "CreateBuffers":
        form.set("inputLayer", input);
        form.set("distances", JSON.stringify(distances()));
        form.set("units", $("azUnits").value);
        form.set("dissolveType", $("azDissolve").checked ? "Dissolve" : "None");
        form.set("ringType", $("azRings").checked ? "Rings" : "Disks");
        break;
      case "DissolveBoundaries":
        form.set("inputLayer", input);
        form.set("dissolveFields", JSON.stringify([...document.querySelectorAll("[data-az-field]:checked")].map(b => b.dataset.azField)));
        form.set("multiPartFeatures", String($("azMulti").checked));
        break;
      case "OverlayLayers":
        form.set("inputLayer", input);
        form.set("overlayLayer", other);
        form.set("overlayType", document.querySelector('input[name="azOverlay"]:checked').value);
        break;
      case "MergeLayers":
        form.set("inputLayer", input);
        form.set("mergeLayer", other);
        break;
      case "FindCentroids":
        form.set("inputLayer", input);
        form.set("pointLocation", String($("azInside").checked));
        break;
      case "JoinFeatures":
        form.set("targetLayer", input);
        form.set("joinLayer", other);
        form.set("spatialRelationship", $("azRelation").value);
        if ($("azRelation").value === "WithinDistance") {
          form.set("spatialRelationshipDistance", $("azJoinDistance").value.trim());
          form.set("spatialRelationshipDistanceUnits", $("azJoinUnits").value);
        }
        form.set("joinOperation", document.querySelector('input[name="azJoinOp"]:checked').value);
        form.set("joinType", $("azKeepAll").checked ? "Left" : "Inner");
        break;
      case "SummarizeWithin": {
        form.set("summaryLayer", input);
        whereForm(form, "sumWithinLayer", other);
        if ($("azShapeUnits")) form.set("shapeUnits", $("azShapeUnits").value);
        const sums = [...document.querySelectorAll("[data-az-sum]:checked")].map(b => `${b.dataset.azSum} ${$("azStatistic").value}`);
        if (sums.length) form.set("summaryFields", JSON.stringify(sums));
        break;
      }
      default:
        form.set("pointLayer", input);
        whereForm(form, "polygonLayer", other);
        form.set("keepBoundariesWithNoPoints", String($("azKeep").checked));
    }

    return form;
  }

  /** A refusal in the screen's words: the server words it in its parameter names. */
  function said(message) {
    return lowerFirst(Object.entries(LABELS).reduce(
      (text, [parameter, label]) => text.replace(new RegExp(`\\b${parameter}\\b`, "g"), label), String(message || "")));
  }

  /** Says a refusal in the footer and under the field it is about, keeping the reader there. */
  function refuse(text, field) {
    $("analyzeSays").textContent = text;
    if (!field) return;

    const error = document.createElement("span");
    error.className = "fielderror";
    error.id = `${field.id}Error`;
    error.textContent = text.replace(/^Not run: /, "");
    error.textContent = error.textContent.charAt(0).toUpperCase() + error.textContent.slice(1);
    field.closest(".stacked")?.append(error);
    field.setAttribute("aria-invalid", "true");
    field.setAttribute("aria-describedby", `${field.getAttribute("aria-describedby") || ""} ${error.id}`.trim());
    field.focus();
  }

  /** Takes back what the last refusal marked, before the next try. */
  function clearRefusals() {
    for (const error of host.body.querySelectorAll(".fielderror")) error.remove();
    for (const one of host.body.querySelectorAll("[aria-invalid]")) {
      one.removeAttribute("aria-invalid");
      one.setAttribute("aria-describedby", (one.getAttribute("aria-describedby") || "").split(" ").filter(d => !d.endsWith("Error")).join(" "));
    }
  }

  /** Submits, then follows the job until it ends, saying where it is; the page says what the result became. */
  async function run() {
    const go = $("analyzeGo");
    if (!go || go.getAttribute("aria-disabled") === "true") return;

    const tool = checkedTool();
    const nameBox = $("azName");
    const name = (nameBox.value || "").trim();
    clearRefusals();

    if (tool.task === "CreateBuffers" && !distances()) {
      refuse("Not run: enter distances as numbers above zero, such as 100, 250.", $("azDistances"));
      return;
    }

    const binned = (document.querySelector('input[name="azWhere"]:checked')?.value || "layer") !== "layer";
    if (binned && !(Number($("azBinSize").value) > 0)) {
      refuse("Not run: the bin size is a number above zero.", $("azBinSize"));
      return;
    }

    if (tool.task === "JoinFeatures" && $("azRelation").value === "WithinDistance"
        && !($("azJoinDistance").value.trim() !== "" && Number($("azJoinDistance").value) >= 0)) {
      refuse("Not run: the distance is a number, zero or more.", $("azJoinDistance"));
      return;
    }

    if (tool.second && !binned && $("azOther") && !$("azOther").value) {
      refuse(`Not run: choose a layer for ${tool.secondLabel.toLocaleLowerCase()}.`, $("azOther"));
      return;
    }

    if (tool.second && !binned && !$("azOther")?.value) {
      const none = $("azNoSecond")?.textContent || "there is no second layer to use.";
      refuse(`Not run: ${none.charAt(0).toLowerCase()}${none.slice(1)}`, null);
      return;
    }

    if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/.test(name)) {
      refuse("Not run: the result's name takes letters, digits, _ and -, starting with a letter or digit.", nameBox);
      return;
    }

    // Not `disabled`: a disabled button drops the focus it holds (design review); it says it is busy instead.
    go.setAttribute("aria-disabled", "true");
    $("analyzeSays").textContent = "Starting…";

    let job;
    try {
      job = await host.fetch(`${SERVICE}/${tool.task}/submitJob`, { method: "POST", body: formFor(tool, name) });
    } catch (e) {
      const message = String(e.message || e);
      go.removeAttribute("aria-disabled");
      refuse(`Not run: ${said(message)}`, /already exists|cannot name/.test(message) ? nameBox : null);
      return;
    }

    if (!job?.jobId) {
      go.removeAttribute("aria-disabled");
      refuse("Not run: the server did not start the job.", null);
      return;
    }

    const mine = { job: job.jobId, tool, name, host };
    running = mine;
    $("analyzeSays").textContent = `Running ${tool.label.toLowerCase()}…`;
    host.body.inert = true;
    drawFoot(true);
    $("analyzeStop").focus();

    for (;;) {
      await new Promise(done => setTimeout(done, 1500));

      let status;
      try {
        status = await mine.host.fetch(`${SERVICE}/${tool.task}/jobs/${encodeURIComponent(mine.job)}?f=json`);
      } catch {
        continue;
      }

      if (!["esriJobSucceeded", "esriJobFailed", "esriJobCancelled"].includes(status.jobStatus)) continue;

      let value = null;
      let line;
      if (status.jobStatus === "esriJobSucceeded") {
        const result = await mine.host.fetch(`${SERVICE}/${tool.task}/jobs/${encodeURIComponent(mine.job)}/results/${tool.result}?f=json`).catch(() => null);
        value = result?.value || null;
      } else {
        const last = [...(status.messages || [])].reverse().find(m => m.type === "esriJobMessageTypeError");
        line = status.jobStatus === "esriJobCancelled" ? `${mine.name} was stopped; nothing was made.`
          : `${mine.name} failed: ${said(last?.description || "the server did not say why.")}`;
      }

      // Closed while it ran: the page says how it ended, since the tools cannot.
      if (running !== mine) {
        if (value) await mine.host.succeeded(mine, value, false);
        else mine.host.notify(line, false);
        return;
      }

      running = null;
      host.body.inert = false;
      drawFoot(false);

      if (value) {
        const { html, focus } = await host.succeeded(mine, value, true);
        $("analyzeSays").innerHTML = html;
        // The next run needs a name of its own; this one is taken now.
        // The next run needs a name of its own. A number this page added is counted on; one the reader typed is theirs,
        // and _2 goes after it (design review: zz_1029 became zz_1030).
        const counted = nameBox.dataset.made === mine.name ? /_(\d+)$/.exec(mine.name) : null;
        nameBox.value = nameFor(counted
          ? `${mine.name.slice(0, -counted[0].length)}_${Number(counted[1]) + 1}`
          : `${mine.name}_2`);
        nameBox.dataset.made = nameBox.value;
        (focus ? $(focus) : $("analyzeGo"))?.focus();
      } else {
        $("analyzeSays").textContent = line;
        $("analyzeGo").focus();
      }
      return;
    }
  }

  /** The page closed the tools: a run goes on, and its end is said by the page. */
  function closed() {
    if (running) {
      running.host.notify(`${running.name} is still running; it will appear in Content when it is done.`, "info");
      running = { ...running, closed: true };
    }
    if (host) host.body.inert = false;
  }

  document.addEventListener("click", async event => {
    const t = event.target;
    if (!host || !(t instanceof Element) || !host.body.isConnected) return;
    if (t.id === "analyzeGo") { await run(); return; }

    if (t.id === "analyzeStop" && running) {
      const stopping = running;
      t.setAttribute("aria-disabled", "true");
      $("analyzeSays").textContent = "Stopping…";
      await stopping.host.fetch(`${SERVICE}/${stopping.tool.task}/jobs/${encodeURIComponent(stopping.job)}/cancel`, { method: "POST" }).catch(() => null);
      return;
    }

    if (t.id === "analyzeCancel" || t.id === "analyzeClosing") host.close();
  });

  document.addEventListener("change", event => {
    if (!host || !layer) return;
    const t = event.target;
    if (t.id === "azTool") {
      $("azToolSays").textContent = checkedTool()?.says || "";
      drawOptions();
      return;
    }
    // Erase and Union take a polygon layer to overlay with; the list follows the choice.
    if (t.name === "azOverlay") { drawSecond(TOOLS.find(one => one.task === "OverlayLayers")); return; }
    // Counted in bins, the polygon list gives way to the bin size; a bin with no point is never kept.
    if (t.name === "azWhere") {
      drawSecond(checkedTool());
      if ($("azKeepRow")) $("azKeepRow").hidden = t.value !== "layer";
      return;
    }
    if (t.id === "azRelation") $("azDistanceRow").hidden = t.value !== "WithinDistance";
    // Where the chosen second layer is, which the cut-off option may not show.
    if (t.id === "azOther" && $("azOtherFrom")) {
      const o = others.find(x => x.url === t.value);
      $("azOtherFrom").textContent = o ? `${o.service} · layer ${o.layerId} · ${o.kind}` : "";
    }
  });

  window.GraticulaAnalysis = { open, closed, kindOf, SERVICE };
})();
