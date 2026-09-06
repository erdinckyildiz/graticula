// The console's behaviour, in its own file rather than inline in the page.
//
// <b>Not a style preference — a Content-Security-Policy requirement.</b> The
// console was served with default-src 'none' and no script-src, which blocks
// inline script as firmly as it blocks a CDN. So from 2026-08-15 until this was
// found on 2026-08-16 the console ran no JavaScript at all: it rendered, because
// style-src 'unsafe-inline' was allowed, and then every button did nothing and
// the sign-in form fell back to a native GET that put the password in the URL.
// D-44.
//
// An external file can be allowed with script-src 'self', which needs no
// 'unsafe-inline' — so the fix is also the stricter policy. Still no build
// step and still no npm (A-055): one more file, served as it is written.

const $ = id => document.getElementById(id);
let token = sessionStorage.getItem("gis-token") || null;

// <b>Four, and the fourth was missing for a day while the server took it.</b> `group` is
// ADR-036's scope: the store holds it, `TryReadScope` accepts it, and it is the *only* way a service
// shared with a group actually reaches that group's members. With three here, the one instruction the
// groups page gives — *set the service's own scope to `group`* — could not be followed anywhere in
// this console, so every share made from the screen was inert and the screen said so without offering
// a remedy. Found in the design review of 2026-08-18, which had to set the scope over the API to see
// a reaching share at all. D-74's family: a value added to an enumeration leaves its readers wrong.
const SCOPES = ["private", "organization", "public", "group"];

// <b>A fallback, not a palette.</b> This list used to be where a layer's colour came
// from; since ADR-033 the server publishes one in the layer document and this console
// reads it. The first entry is used only when a document arrives without `drawingInfo` —
// an older server, or a layer whose document could not be read.
const PALETTE = ["#0b6157", "#a63a2b", "#1f5fa8", "#92620d", "#6b3fa0", "#2f7a55"];

const TILE_COLOUR = "#8fb8cc";
// <b>One picture per layer per visit.</b> A list of forty services must not re-fetch forty
// pictures every time a filter redraws the table, and the value is the *promise* rather than the
// resolved object — two overlapping renders of the same list both find the pending one and only
// one request goes out. The object URLs live as long as the tab, which for a couple of kilobytes
// each is cheaper than tracking when the last <img> using one went away.
const pictures = new Map();
const shown = new Map();     // layer name -> { colour, layer }
let esri = null;             // the SDK modules, loaded once
let view = null;

let known = [];              // last /admin/layers listing — Server only
let content = new Map();     // name -> the /content/layers entry, which carries its address
let selected = null;         // the row the table marks: the layer last opened
const layerNamed = name => known.find(l => l.name === name) || { name, hosted: false };

// ------------------------------------------------------------------ plumbing

function toast(message, ok = false) {
  const t = $("toast");
  t.textContent = message;
  t.className = "on " + (ok ? "good" : "bad");
  clearTimeout(t._timer);
  t._timer = setTimeout(() => (t.className = ""), 7000);
}

async function api(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (token) headers.Authorization = "Bearer " + token;

  const response = await fetch(path, { ...options, headers });
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* not json */ }

  if (!response.ok) {
    // The server's own message. Every refusal in this product is written to say
    // what to do about it, and replacing that with "request failed" throws away
    // the only useful part.
    const failure = new Error(
      (body && body.error && body.error.message) || `${response.status} ${path}`);

    // <b>And the status beside it, because a caller sometimes needs the code rather than the
    // prose.</b> The preview drawer wants to say *stopped* for a 503 and *no preview* for
    // anything else, and its first version got there by running a regular expression over
    // the message — which found nothing, because the message is the server's sentence and
    // carries no digits. A caller reading English to recover a number it was already sent is
    // a mistake with one fix.
    failure.status = response.status;
    throw failure;
  }
  return body;
}

// Escaped by default. Everything below interpolates catalogue text — layer
// names, table names, a connection error from PostgreSQL — into markup, and any
// of it can contain a bracket.
function h(value) {
  return String(value ?? "").replace(/[&<>"']/g,
    c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
/**
 * A small monochrome glyph, by name.
 *
 * <b>Inline SVG, not an icon font and not emoji.</b> A font is a request and a licence; emoji are
 * colour glyphs that ignore `currentColor` and render differently on every platform, which is fatal
 * for a set whose whole job is to inherit the hue that already distinguishes three sharing scopes.
 * These are four paths at 16 units, stroked in `currentColor`.
 *
 * <b>Named after what they mean, not what they look like.</b> `public` rather than `globe`, so
 * changing the picture later is one line here instead of a search for every caller that asked for a
 * globe.
 */
const ICONS = {
  // A globe: who may read it is *anyone*, and that is the one fact people scan this column for.
  public: '<circle cx="8" cy="8" r="6.2"/><path d="M1.8 8h12.4M8 1.8c3 3.4 3 9 0 12.4'
        + 'c-3-3.4-3-9 0-12.4"/>',

  // An organisation: two figures, because the scope is *the people here* rather than a building.
  organization: '<circle cx="5.6" cy="6" r="2.1"/><circle cx="11" cy="6.6" r="1.7"/>'
              + '<path d="M1.9 13c.3-2.2 1.8-3.4 3.7-3.4S9 10.8 9.3 13"/>'
              + '<path d="M10.3 9.9c1.6.1 2.7 1.1 3 3.1"/>',

  // A padlock, closed: the only scope where the answer is *nobody but its owner and an administrator*.
  private: '<rect x="3.4" y="7.2" width="9.2" height="6.6" rx="1.4"/>'
         + '<path d="M5.6 7.2V5.4a2.4 2.4 0 0 1 4.8 0v1.8"/>',

  // A sheet of paper with an arrow leaving it: the drop zone and the *Upload a file* option, which
  // are the same act reached two ways.
  upload: '<path d="M4.2 1.9h4.6l3.2 3.2v8.9H4.2z"/><path d="M8.6 1.9v3.4h3.4"/>'
        + '<path d="M8 11.6V7.4"/><path d="M6.5 8.9 8 7.4l1.5 1.5"/>',

  // A laptop, for *Your device* — the file is on the machine in front of you, not on this server.
  device: '<rect x="2.6" y="3.4" width="10.8" height="7" rx="1.1"/><path d="M1.4 12.6h13.2"/>',

  // A map sheet with a point on it, for a feature layer. Not a stack of sheets: a stack is what this
  // console draws for a *service*, which holds layers, and the two must not read as each other.
  featurelayer: '<path d="M2.3 12.9V4.1l4.4-1.6 4.6 1.6 2.4-1v8.8l-2.4 1-4.6-1.6z"/>'
              + '<circle cx="8" cy="7.1" r="1.6"/>',

  // A table: rows and a header, because what is published is a table and nothing is copied.
  table: '<rect x="2.2" y="3.2" width="11.6" height="9.6" rx="1.1"/>'
       + '<path d="M2.2 6.3h11.6M6.6 6.3v6.5"/>',

  // A house for the site root, a folder for the rest — see the rail's own comment for why that is a
  // distinction rather than decoration.
  root: '<path d="M2.2 7.4 8 2.6l5.8 4.8"/><path d="M3.8 8.6v4.8h8.4V8.6"/>',
  folder: '<path d="M1.9 4.6h4l1.4 1.7h6.8v7.1H1.9z"/>',
};

/**
 * One glyph, as markup.
 *
 * `stroke` and no `fill`, because every path above is drawn as an outline — a filled globe at twelve
 * pixels is a dot.
 */
const icon = name => ICONS[name]
  ? `<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.35"
       stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name]}</svg>`
  : "";

/**
 * A state badge, with a glyph where the state has one.
 *
 * <b>The glyph replaces the dot rather than joining it.</b> Owner 2026-08-17: *"sharing'deki icon
 * mantığı güzel."* A badge carrying both would be two signals for one fact, which is the thing the
 * badge rule exists to prevent — so `.withicon` suppresses the dot.
 */
const pill = value => {
  const key = String(value).toLowerCase();
  const glyph = icon(key);

  return `<span class="pill p-${h(key)}${glyph ? " withicon" : ""}">${glyph}${h(value)}</span>`;
};

/**
 * Which of the three tones a probe outcome wears.
 *
 * <b>Three for four, and the grouping is what an administrator acts on.</b> `CannotConnect` is
 * red because nothing about the data is known yet and the next move is a host, a port or a
 * password; `InsufficientPrivilege` and `UnusableGeometry` are amber because the connection
 * worked and what was found is the problem, which is a different afternoon. Colouring all three
 * failures alike would send somebody to check a network that is fine.
 *
 * @param {string} outcome What the probe answered.
 * @returns {string} The class.
 */
const sourceTone = outcome => ({
  usable: "ok",
  cannotconnect: "alert",
  insufficientprivilege: "warn",
  unusablegeometry: "warn",
}[String(outcome || "").toLowerCase()] ?? "ok");

/**
 * `CannotConnect` as `Cannot connect`.
 *
 * <b>The enum's spelling is the wire's, not a reader's.</b> It is on the screen because the
 * outcome is the heading of the box, and a heading in PascalCase reads as a symbol somebody
 * forgot to translate.
 *
 * @param {string} word The name.
 * @returns {string} It, with spaces.
 */
const spaced = word => String(word)
  .replace(/([a-z])([A-Z])/g, (_, before, after) => `${before} ${after.toLowerCase()}`)
  .replace(/^./, first => first.toUpperCase());

const nf = new Intl.NumberFormat();
const num = value => nf.format(value ?? 0);

function bytes(value) {
  if (!value) return "0<small>B</small>";
  const units = ["B", "KB", "MB", "GB"];
  let n = value, i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n < 10 && i > 0 ? n.toFixed(1) : Math.round(n)}<small>${units[i]}</small>`;
}

function duration(ms) {
  const s = Math.round((ms ?? 0) / 1000);
  if (s < 60) return `${s}<small>s</small>`;
  if (s < 3600) return `${Math.floor(s / 60)}<small>m</small> ${s % 60}<small>s</small>`;
  const h_ = Math.floor(s / 3600);
  return `${h_}<small>h</small> ${Math.floor((s % 3600) / 60)}<small>m</small>`;
}

function metric(label, value, note) {
  return `<div class="metric"><dt>${h(label)}</dt><dd>${value}</dd>${
    note ? `<dd class="val" style="font-size:12px;margin-top:2px">${h(note)}</dd>` : ""}</div>`;
}

// -------------------------------------------------------------------- surfaces

/**
 * Two surfaces over one API — ADR-034.
 *
 * <b>Server is for the operator and Studio is for the publisher</b>, and the gate is a
 * privilege the API already enforces rather than a rule invented here: Server needs
 * `admin:manageServer`, Studio needs a session. A reader without it gets no Server tab, no
 * Server link, and `/server/` itself puts them in Studio with a sentence instead of four
 * refusals — which is what the single console did, one screen at a time.
 *
 * Each surface owns its tab strip, and the strip is built rather than written down, because a
 * tab a reader cannot use must not be in the document at all.
 */
/**
 * Groups: the ones you are in, and what is shared with them.
 *
 * <b>ADR-036.</b> A group is a set of members with services shared to it; its members read those
 * services and nobody else does. The list is what you own or belong to — an administrator sees all
 * of them, because a group whose owner has left still has to be administrable.
 *
 * <b>Two axes on one screen, and the row says which one it is showing.</b> What you may do to a
 * group depends on where you stand in it (owner, manager, member) as well as on what your role
 * grants, so the server sends `mayManage` and `mayDelete` per row rather than leaving the console to
 * recompute them from a standing and a privilege list and get it wrong differently.
 */
let groupOpen = null;

/** What is typed in the Groups search box. */
let groupFilter = "";

/** Who is signed in, set by `whoami` and read only by *Leave group*. */
let signedInAs = "";

async function loadGroups() {
  const answer = await api("/admin/groups") || {};
  const groups = answer.groups || [];

  if (!groups.some(g => g.name === groupOpen)) groupOpen = groups[0]?.name ?? null;

  const needle = groupFilter.trim().toLowerCase();

  // <b>A search box, because the reference has one over seventy groups.</b> Matching the title, the
  // name, the description and the owner: an operator looking for a group remembers one of those and
  // not which one it was.
  const shown = needle
    ? groups.filter(g => [g.name, g.title, g.description, g.owner]
        .some(v => (v || "").toLowerCase().includes(needle)))
    : groups;

  $("groupCount").textContent = groups.length === 0
    ? "no groups"
    : needle
      ? `${shown.length} of ${groups.length}`
      : `${groups.length} group${groups.length === 1 ? "" : "s"} — ${h(answer.listing || "")}`;

  // <b>Three empty states, not one — and the filtered case used to render nothing at all.</b> The
  // branch was on `groups.length` while the rows came from `shown`, so a search matching nothing
  // left a blank body under a live header with `0 of 71` beside it and no sentence. `loadServices`
  // on this same screen already had it right; this is that shape copied.
  // <b>Confers, in the column Description had.</b> A group's capability is its one irreversible
  // property and it was buried in a paragraph below the table; the description had no way to be set
  // at all until the create form gained a field for it, so that column was structurally empty on
  // everything this console made.
  //
  // <b>Standing as words, not a badge.</b> `pill()` is for a state — whether a service answers, who
  // may read it — and a standing is a relationship. All three values fell through to one grey pill
  // with no colour family and no icon, so the border and the dot carried nothing the word did not,
  // and a fourth meaningless pill weakens the ones that mean something.
  //
  // <b>Both of these were written as HTML comments inside the template literal, and one of them
  // carried a backtick.</b> That closed the literal, the whole file stopped parsing, and the console
  // showed its sign-in screen — a syntax error presenting as *not signed in*. `node --check` finds
  // it in two seconds and now runs in the build.
  $("groupRows").innerHTML = groups.length === 0
    ? `<tr><td colspan="6" class="empty">${answer.listing === "every group on the server"
        ? `No groups on this server yet.`
        : `You are in no groups.`} <b>New group</b> makes one, and whoever makes it owns it.</td></tr>`
    : shown.length === 0
      ? `<tr><td colspan="6" class="empty">Nothing matches <b>${h(groupFilter)}</b>. The search reads
           a group's name, title, description and owner.</td></tr>`
      : pageOf("groupRows", shown).map(g => `
      <tr class="pick${g.name === groupOpen ? " on" : ""}" data-group="${h(g.name)}">
        <td class="name"><a href="#/group/${encodeURIComponent(g.name)}"
            >${h(g.title || g.name)}</a>${g.title && g.title !== g.name
          ? `<div class="val" style="font-weight:400">${h(g.name)}</div>` : ""}</td>
        <td class="val">${g.itemUpdate === "allItems"
          ? "edit all"
          : g.itemUpdate === "ownItems" ? "edit own" : "read"}</td>
        <td>${g.standing === "member"
          ? `<span class="val">member</span>`
          : `<b>${h(g.standing)}</b>`}</td>
        <td class="val">${h(g.owner || "—")}</td>
        <td class="num">${num(g.members)}</td>
        <td class="num">${num(g.items)}</td>
      </tr>`).join("");

  $("groupsPager").innerHTML = pagerFor("groupRows", shown.length);

  // <b>Chosen from what is on screen.</b> It was taken from the unfiltered list, so filtering to
  // exclude the open group left the panel describing a group with no row above it.
}

/**
 * The tabs of a group's page, in order.
 *
 * <b>Four, by owner decision — ADR-036 §4g.</b> §4f declined them on subject rather than scale: the
 * screen existed to compare *who is in a group* with *what they can therefore read*, and tabs hide
 * half of that. The owner's counter defeated it — maps and icons are coming, and Content stops being
 * two rows of a table the moment it holds items with pictures. The refusal was right about today and
 * wrong about the product.
 *
 * <b>What the tabs cost is paid on Overview.</b> The comparison §4f protected is now the reachability
 * tally, first thing on the landing tab. Without it the tabs would have taken the relation away and
 * given nothing back.
 */
const GROUP_TABS = [
  ["overview", "Overview"],
  ["content", "Content"],
  ["members", "Members"],
  ["settings", "Settings"],

  // <b>Addressable and not on the strip.</b> *Add items* is Content's child page, so it needs a place
  // in this table for the router to accept `#/group/x/add`, and it must not become a fifth link — a
  // strip entry for a transient act reads as a fifth subject. `false` is *not a destination in its own
  // right*; the strip filters on it and keeps **Content** marked current while this is open, because a
  // strip with nothing marked reads as nothing selected.
  ["add", "Add items", false],
];

/**
 * The group as the server last described it.
 *
 * <b>This is what makes the settings write safe, and without it the screen destroys data.</b>
 * `PUT /admin/groups/{name}/settings` replaces every field including the three that are text — so a
 * Settings tab that posted only its four policies would erase the title, the summary and the
 * description, and an Overview summary editor that posted only a summary would silently unlock a
 * delete-locked group. Every write goes through `saveGroupSettings`, which overlays a patch on this.
 */
let groupNow = null;

/** Which tab of the open group's page is showing. */
let groupTab = "overview";

/** A date as an operator reads it: the day, and the time only where it disambiguates. */
function day(value) {
  if (!value) return "—";
  const at = new Date(value);
  if (Number.isNaN(at.getTime())) return "—";
  return at.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

/**
 * A group's page.
 *
 * <b>A page and not a panel, which the four tabs forced.</b> The editor was a `.panel` below a
 * ten-row table, far enough down that opening it needed `scrollIntoView` to be visible at all. A tab
 * strip there would scroll out from under the pointer and Content would be boxed at half the
 * viewport. Addressed `#/group/{name}/{tab}`, so a tab is somewhere you can send somebody.
 */
async function showGroup(name, tab) {
  // <b>A fresh arrival at the add page starts with nothing ticked.</b> The selection outlives paging
  // and filtering deliberately; outliving *navigation away and back* would be a different promise, and
  // a footer offering to add eight things somebody chose ten minutes ago on another group is worse than
  // one that starts empty.
  if (name !== groupOpen || tab !== "add") {
    addPicked = new Set();
    addFolder = null;
    resetPage("addRows");
  }

  groupOpen = name;
  groupTab = GROUP_TABS.some(([id]) => id === tab) ? tab : "overview";
  showView("view-group", "groups");

  $("groupCrumb").innerHTML = `<a href="#/groups">Groups</a> › <b>${h(name)}</b>`;
  $("groupPicker").hidden = true;

  let one;
  try {
    one = await api(`/admin/groups/${encodeURIComponent(name)}`);
  } catch (e) {
    $("groupTitle").textContent = name;
    $("groupSummary").innerHTML =
      `<span class="bad">${h(e.message || e)}</span> — <a href="#/groups">back to the list</a>`;
    $("groupTabs").innerHTML = "";
    for (const [id] of GROUP_TABS) $(`tab-${id}`).hidden = true;
    return;
  }

  groupNow = one;

  $("groupTitle").textContent = one.title || one.name || name;

  // The summary as the page's subtitle. Absent for everybody, invitingly absent for a manager only:
  // ADR-034 condition 1 — do not show a reader an absence they cannot fill.
  // <b>Not in `.val`, which is the 13.5px monospace value font.</b> A whole sentence set in it
  // dominated the page head and read as data. Prose gets prose type.
  $("groupSummary").innerHTML = one.summary
    ? h(one.summary)
    : one.mayManage
      ? `No summary yet — <a href="#/group/${encodeURIComponent(name)}/settings">Settings</a> takes
         one. It is the line somebody reads while deciding whether to share into this group.`
      : "";

  const items = one.items || [];
  const members = one.members || [];
  const counts = { overview: null, content: items.length, members: members.length, settings: null };

  $("groupTabs").innerHTML = GROUP_TABS
    .filter(([, , onStrip]) => onStrip !== false)
    .map(([id, label]) => `
      <a href="#/group/${encodeURIComponent(name)}/${id}"${
        id === groupTab || (groupTab === "add" && id === "content")
          ? ' aria-current="page"' : ""}>${label}${counts[id] === null
            ? "" : ` <span class="count">${num(counts[id])}</span>`}</a>`).join("");

  // <b>Only what this reader may act on.</b> ADR-034 condition 1, and a plain member of a group is a
  // reader here. *Leave group* is absent for the owner because the store refuses it — they would keep
  // owning a group a membership-filtered list omits.
  // <b>The head holds one verb now, so it is shown exactly when that verb is available.</b> It used
  // to hold three and its own visibility was a compound of two flags, which is how an owner with no
  // `mayManage` ended up with no head at all.
  $("groupLeave").hidden = one.standing === "owner";
  $("groupActions").hidden = one.standing === "owner";

  // <b>The two managing verbs live on the tabs they act on</b> — the owner's correction, 2026-08-18 —
  // so each is drawn by its own tab and only when that tab is the one showing.
  if ($("groupAdd")) $("groupAdd").hidden = !one.mayManage;

  if ($("groupShare")) {
    $("groupShare").hidden = !one.mayManage;
    $("groupShare").setAttribute(
      "href", `#/group/${encodeURIComponent(name)}/add`);
  }

  for (const [id] of GROUP_TABS) $(`tab-${id}`).hidden = id !== groupTab;

  if (groupTab === "overview") drawGroupOverview(one);
  if (groupTab === "content") drawGroupContent(one);
  if (groupTab === "members") drawGroupMembers(one);
  if (groupTab === "settings") drawGroupSettings(one);
  if (groupTab === "add") await drawGroupAdd(one);

  paintPreviews();
}

/** Re-reads the open group and redraws whichever tab is showing. */
async function refreshGroup() {
  if (groupOpen) await section("group", () => showGroup(groupOpen, groupTab), null);
}

/**
 * Overview: what the group is, and whether its shares reach anybody.
 *
 * <b>The tally is the tab's reason to exist.</b> A share reaches a group's members only when the
 * group share and the service's own scope agree, and either alone is a state that reads as done and
 * is not. Nowhere else counts how many currently do.
 */
function drawGroupOverview(one) {
  const items = one.items || [];
  const inert = items.filter(i => i.sharing !== "group");
  const reaching = items.length - inert.length;
  const people = (one.members || []).length;

  // <b>Leads with the number nowhere else says.</b> The total is already in the tab strip and in the
  // facts below; *how many reach nobody* is this tab's own fact, and a design review found the first
  // version restating the tab count in three of its four states. `inert` is also gone as a word — it
  // was a coinage the sentence had to define in the same breath.
  $("groupReach").innerHTML = items.length === 0
    ? `Nothing is shared with this group yet, so its ${num(people)}
       member${people === 1 ? "" : "s"} can read nothing through it.`
    : inert.length === 0
      ? `All <b>${num(items.length)}</b> service${items.length === 1 ? "" : "s"} shared here
         reach${items.length === 1 ? "es" : ""} the ${num(people)}
         ${people === 1 ? "person" : "people"} in this group.`
      : `<b>${num(inert.length)} of the ${num(items.length)} services shared here reach nobody.</b>
         Sharing into a group and setting a service's own scope to <b>group</b> are two acts, and
         these have only the first.`;

  $("groupAbout").innerHTML = one.description
    ? `<p class="lede">${h(one.description)}</p>`
    : one.mayManage
      ? `<p class="hint">No description. Say what this group is for — it is what somebody deciding
           whether to share into it reads. <b>Settings</b> takes one.</p>`
      : "";

  // <b>The faults, and the verb that clears them — not a second copy of Content.</b> This block was
  // *Recently shared*: the same rows, the same order and the same columns as Content's first page
  // minus its verb, which is a worse Content tab and becomes a worse gallery when maps arrive. A
  // design review put the choice as *make Overview the repair desk or delete it*, and the repair desk
  // is the version that earns being landed on: it holds the one thing no other tab does, which is
  // what is wrong with this group and how to fix it.
  //
  // <b>The verb writes the service's own scope</b>, which is the act the sentence above names. It is
  // the service's setting and not the group's, so it is stated as such rather than hidden behind
  // *"fix"* — an operator who does not know these are two acts has to learn it here, once.
  $("groupRecent").innerHTML = items.length === 0
    ? `<p class="hint">Nothing yet. <b>Add item</b>, on Content, offers what you have published.</p>`
    : inert.length === 0
      ? `<p class="hint">Nothing to repair: every service shared with this group is
           <b>group</b>-scoped, so all ${num(reaching)} of them reach its members.</p>`
      : `<table><tbody>${inert.map(i => `
          <tr>
            <td class="name"><a href="#/service/${i.name.split("/").map(encodeURIComponent).join("/")}"
              >${h(i.name)}</a></td>
            <td class="val">its scope is ${pill(i.sharing)}</td>
            <td style="text-align:right">${one.mayManage
              ? `<button class="tiny" data-group-reach="${h(i.name)}">Set scope to group</button>`
              : ""}</td>
          </tr>`).join("")}</tbody></table>
         <p class="hint">Each of these is shared with the group and readable by nobody through it.
           ${one.mayManage
             ? `Setting the scope makes it readable by this group's members and by nobody else.`
             : `Their owner or an administrator sets the scope.`}</p>`;

  $("groupFacts").innerHTML = `
    <dt>You are</dt><dd>${one.standing === "owner"
      ? `its owner <span class="val">— you may delete it; you cannot leave it</span>`
      : one.standing === "manager"
        ? `a manager <span class="val">— you may add members and share services</span>`
        : `a member <span class="val">— you read what is shared with it</span>`}</dd>
    <dt>Owner</dt><dd>${h(one.owner || "—")}</dd>
    <dt>Created</dt><dd>${day(one.createdAt)}</dd>
    <dt>Members</dt><dd>${one.members
      ? num(one.members.length)
      : `<span class="val">not shown — this group's list is for its owner and managers</span>`}</dd>
    <dt>Services</dt><dd>${num(items.length)}</dd>
    <dt>Visible to</dt><dd>${one.visibility === "organization"
      ? `any signed-in member`
      : `its members only`}</dd>
    <dt>Joining</dt><dd>${one.joinPolicy === "self" ? `anyone who can see it` : `by invitation`}</dd>
    <dt>Contributing</dt><dd>${one.contribute === "members"
      ? `any member`
      : `the owner and its managers`}</dd>
    <dt>Confers</dt><dd>${one.itemUpdate === "allItems"
      ? `editing every service shared with it`
      : one.itemUpdate === "ownItems"
        ? `reading only <span class="val">— a member already owns what they shared</span>`
        : `reading only`}<span class="val"> — fixed at creation</span></dd>
    <dt>Member list</dt><dd>${one.memberList === "managers"
      ? `its owner and managers <span class="val">— and an administrator</span>`
      : `any member`}</dd>
    <dt>Leaving</dt><dd>${one.membersMayLeave === false
      ? `not by yourself <span class="val">— an administrative group; its owner or a manager
         removes you</span>`
      : `a member may leave on their own`}</dd>
    ${one.deleteLocked ? `<dt>Deletion</dt><dd>locked <span class="val">— including for an
      administrator</span></dd>` : ""}`;

  // <b>The way out, shown only to somebody who has one.</b> `mayLeave` is worked out by the
  // server rather than derived here from standing and a setting — the same rule `mayManage`
  // follows, and for the same reason: two implementations of one authorisation question
  // disagree eventually, and the screen is the copy that is wrong.
  const out = $("groupLeaveRow");

  if (out) {
    out.innerHTML = one.mayLeave
      ? `<button class="tiny danger" id="groupLeave">Leave this group</button>
         <span class="hint" style="margin:0">You stop reading what is shared with it. Nothing
           you own is deleted, and an owner or manager can add you back.</span>`
      : ``;
  }
}

/**
 * Content: what is shared with the group.
 *
 * <b>Built as the Services row is built, which is what makes a map an addition rather than a
 * redesign.</b> Name, kind, state, date, one verb. When a map arrives it is a new value in the kind
 * column and a picture in a slot that already exists; nothing about the row changes. What is
 * deliberately absent is the reference's category rail (a heading over an empty state linking to a
 * feature that does not exist), its item-type tree (three leaves is not a tree), its checkbox column
 * (there is no bulk operation) and its List/Grid toggle (there is no grid).
 */
function drawGroupContent(one) {
  const all = one.items || [];
  const needle = ($("groupItemFilter")?.value || "").trim().toLowerCase();

  // Search only above one page — the same rule the pager follows, so neither appears for a list you
  // can read at a glance.
  $("groupItemFilter").hidden = all.length <= PAGE_SIZE && !needle;

  const shown = needle
    ? all.filter(i => [i.name, i.kind, i.sharedBy].some(v => (v || "").toLowerCase().includes(needle)))
    : [...all];

  if (($("groupItemSort")?.value || "shared") === "name") {
    shown.sort((a, b) => a.name.localeCompare(b.name));
  } else {
    shown.sort((a, b) => String(b.shared || "").localeCompare(String(a.shared || "")));
  }

  const reaching = all.filter(i => i.sharing === "group").length;

  // <b>Both sides named, on the tab where the shares are.</b> This is what §4f's argument was
  // really about: services against *people*, not services against a row count. `showing` when
  // filtering, because `2 of 11` meant two different things in two places three inches apart.
  const people = (one.members || []).length;

  $("groupItemCount").textContent = all.length === 0
    ? ""
    : needle
      ? `showing ${shown.length} of ${all.length}`
      : `${reaching} of ${all.length} reach the ${people} ${people === 1 ? "person" : "people"} `
        + `in this group`;

  $("groupItems").innerHTML = all.length === 0
    ? `<tr><td colspan="6" class="empty">Nothing in it yet. <b>Add item</b> offers what you have
         published — and an item reaches these members only once its own sharing scope is
         <b>group</b> as well. Overview lists the ones that do not and sets it.</td></tr>`
    : shown.length === 0
      ? `<tr><td colspan="6" class="empty">Nothing matches <b>${h(needle)}</b>. The search reads a
           service's name, its kind and who shared it.</td></tr>`
      : pageOf("groupItems", shown).map(i => `
        <tr>
          <td class="thumbcell">${i.cover
            ? `<img class="thumb" alt="" loading="lazy"
                 data-thumb="${h(thumbnailFor(i.cover.url))}">`
            : `<div class="thumb empty" title="This service has no layer to draw, so there is no map to show."></div>`}</td>
          <td class="name"><a href="#/service/${i.name.split("/").map(encodeURIComponent).join("/")}"
            >${h(i.name)}</a></td>
          <td class="val">${h(i.kind || "service")}</td>
          <td>${i.sharing === "group"
            ? `yes`
            : `<b>no</b> <span class="val">— its own scope is ${h(i.sharing)}</span>`}</td>
          <td class="val">${day(i.shared)}${i.sharedBy
            ? ` <span class="faint">by ${h(i.sharedBy)}</span>` : ""}</td>
          <td style="text-align:right">${one.mayManage
            ? `<button class="tiny danger" data-group-unshare="${h(i.name)}"
                 title="Removes it from the group. Everybody in the group loses it; the service itself
                        keeps existing.">Stop sharing</button>`
            : ""}</td>
        </tr>`).join("");

  $("groupItemsPager").innerHTML = pagerFor("groupItems", shown.length);
}

/**
 * Members: who is in it, and since when.
 *
 * <b>`Joined` is an access-control fact.</b> A group's member list is an access-control list, and
 * *when did this person gain access to everything shared here* is an audit question the column could
 * answer and nothing asked. No avatar and no e-mail: neither exists on a principal, and the member
 * form already says on screen that this server cannot send a message at all — a column for an address
 * would contradict a sentence the console shows.
 */
function drawGroupMembers(one) {
  const all = one.members || [];
  const needle = ($("groupMemberFilter")?.value || "").trim().toLowerCase();

  $("groupMemberFilter").hidden = all.length <= PAGE_SIZE && !needle;

  const shown = needle
    ? all.filter(m => [m.name, m.displayName, m.standing]
        .some(v => (v || "").toLowerCase().includes(needle)))
    : all;

  $("groupMemberCount").textContent = all.length === 0
    ? ""
    : needle
      ? `${shown.length} of ${all.length}`
      : `${all.length} member${all.length === 1 ? "" : "s"}, ${
          all.filter(m => m.standing !== "member").length} of them able to manage it`;

  const page = pageOf("groupMembers", shown);
  let drawnPlain = false;

  $("groupMembers").innerHTML = all.length === 0
    ? `<tr><td colspan="4" class="empty">Nobody yet. <b>Add member</b> offers everybody who is not
         in it — they will read whatever is shared with the group.</td></tr>`
    : shown.length === 0
      ? `<tr><td colspan="4" class="empty">Nothing matches <b>${h(needle)}</b>. The search reads a
           member's name, the name they sign in with, and their standing — so <b>manager</b> finds
           the people who can manage this group.</td></tr>`
      : page.map(m => {
        // <b>A labelled row rather than a hairline, and the hairline was invisible anyway.</b> Every
        // `td` already carries a bottom rule, so `tr.after` stacked a second near-identical 1px line
        // against it. And the sort it depended on was wrong — `order by 2 desc` over text put the
        // managers *last*, so the line landed on the first row of page two and nowhere near the
        // boundary. The sort is ranked explicitly now, and the boundary says what it separates, which
        // survives a page break where a rule does not.
        const first = m.standing === "member" && !drawnPlain && page.some(o => o.standing !== "member");
        if (m.standing === "member") drawnPlain = true;

        return `${first ? `<tr class="groupband"><td colspan="4">Members, who read what is shared
          with the group</td></tr>` : ""}
        <tr>
          <td class="name">${h(m.displayName || m.name)}${m.displayName && m.displayName !== m.name
            ? `<div class="val" style="font-weight:400">${h(m.name)}</div>` : ""}</td>
          <td>${m.standing === "member"
            ? `<span class="val">member</span>`
            : `<b>${h(m.standing)}</b>`}</td>
          <td class="val">${day(m.joined)}${m.addedBy
            ? ` <span class="faint">by ${h(m.addedBy)}</span>` : ""}</td>
          <td style="text-align:right">${one.mayManage && m.standing !== "owner" ? `
            <button class="tiny" data-group-grade="${h(m.name)}"
              data-to="${m.standing === "manager" ? "member" : "manager"}"
              title="${m.standing === "manager"
                ? "A manager may add members and share services; make them a plain member"
                : "A manager may add members and share services, and may not delete the group"}"
              >Make ${m.standing === "manager" ? "member" : "manager"}</button>
            <button class="tiny danger" data-group-drop="${h(m.name)}">Remove</button>` : ""}</td>
        </tr>`;
      }).join("");

  $("groupMembersPager").innerHTML = pagerFor("groupMembers", shown.length);
}

/**
 * Settings: the four editable policies, and deletion.
 *
 * <b>Selects and not radios, and that is a house rule rather than a preference.</b> There is not one
 * radio input in this console; every either/or is a `.setting` row with a select, and the nearest
 * precedent is on this very subject — the create form's capability select carries the consequence in
 * each option's text. Three radio groups here would introduce a fifth visual dialect on the screen
 * most at risk of one.
 *
 * <b>Membership first, deletion last</b> — the opposite of the reference's order, and this console's
 * own rule about destructive controls.
 */
function drawGroupSettings(one) {
  // <b>*By request* is rendered disabled rather than omitted, and the harder reason is not
  // honesty.</b> The column stores three values and only the write path refuses the third, so a group
  // can already *hold* `request`. Render two options and such a group reads as *by invitation* on
  // screen while the store says otherwise — the console lying about a policy. Showing it means the
  // current value is always displayable: you can save away from it and not back to it.
  //
  // <b>The capability is stated here although Overview states it too</b>, because Settings is where
  // somebody arrives wanting to change it, and finding it fixed *there* answers the question they came
  // with. The two rows are worded so they cannot be confused: who may share something *into* the group
  // is editable, what may be done with what is *already* in it is not.
  //
  // <b>Both of these were HTML comments inside the template literal below, and one carried a
  // backtick.</b> That is D-77 exactly — it closed the literal and the file stopped parsing. The fix
  // is not to escape it: an explanation does not belong inside a string whose delimiter appears in the
  // language being explained.
  if (!one.mayManage) {
    $("groupSettings").innerHTML = `
      <p class="hint">These are the owner's and its managers' to set. You are a member of this group,
        so what it does is <a href="#/group/${encodeURIComponent(one.name)}/overview">on Overview</a>
        as facts.</p>`;
    return;
  }

  // <b>`<label for>`, because `<span class="q">` gives a combobox no accessible name.</b> A design
  // review's snapshot read all four of this tab's selects as `combobox: ""` — the one tab that is
  // nothing but form controls, and a screen reader got four unlabelled dropdowns.
  const pick = (id, value, options) => `<select id="${id}">${options.map(([v, label, off]) =>
    `<option value="${v}"${v === value ? " selected" : ""}${off ? " disabled" : ""}>${label}</option>`)
    .join("")}</select>`;

  // <b>Three text rows that existed nowhere in this console.</b> `title` and `description` were
  // create-only and `summary` was unreachable altogether, while the endpoint accepted all three and
  // the overlay already sent them — so the page's most persistent string, *"No summary. Settings takes
  // one"*, pointed at a field that did not exist. A design review read it cold and called it a promise
  // the page cannot keep. It can now.
  //
  // <b>And this comment is out here rather than in the template, which is the third time today.</b>
  // A backtick inside an HTML comment inside a template literal closes the literal (D-77). The rule is
  // not *escape it* — it is that HTML comments do not go inside template literals at all.
  $("groupSettings").innerHTML = `
    <h4>What this group is</h4>

    <div class="setting wide"><label class="q" for="gsTitle">Its display title:</label>
      <input id="gsTitle" type="text" autocomplete="off" value="${h(one.title || "")}">
      <span class="u"></span></div>

    <div class="setting wide"><label class="q" for="gsSummary">One line, shown under its name:</label>
      <input id="gsSummary" type="text" autocomplete="off" value="${h(one.summary || "")}">
      <span class="u"></span></div>

    <div class="setting wide"><label class="q" for="gsDescription">What it is for:</label>
      <input id="gsDescription" type="text" autocomplete="off"
        value="${h(one.description || "")}"><span class="u"></span></div>

    <h4>Group membership</h4>

    <div class="setting wide"><label class="q" for="gsVisibility">Who can see that this group exists:</label>
      ${pick("gsVisibility", one.visibility || "members", [
        ["members", "Only its members"],
        ["organization", "All signed-in members"],
      ])}<span class="u"></span></div>
    <p class="hint">Seeing that a group exists is not being able to read what is in it. What is shared
      with this group stays readable by its members and nobody else, whatever this says.</p>

    <div class="setting wide"><label class="q" for="gsJoin">How people come to be in it:</label>
      ${pick("gsJoin", one.joinPolicy || "invitation", [
        ["invitation", "An owner or manager adds them"],
        ["request", "They ask — not built yet", true],
        ["self", "They add themselves"],
      ])}<span class="u"></span></div>
    <p class="hint">Asking to join needs a queue somebody reviews, which is not built.</p>

    <div class="setting wide"><label class="q" for="gsContribute">Who may share a service into it:</label>
      ${pick("gsContribute", one.contribute || "managers", [
        ["managers", "The owner and its managers"],
        ["members", "Any member"],
      ])}<span class="u"></span></div>
    <p class="hint" id="gsVisNote"></p>

    <div class="setting wide"><label class="q" for="gsMemberList">Who can see who is in it:</label>
      ${pick("gsMemberList", one.memberList || "members", [
        ["members", "Any member"],
        ["managers", "The owner and its managers"],
      ])}<span class="u"></span></div>
    <p class="hint">Neither answer reaches outside the group, so this can only narrow what the first
      setting on this tab allows. An administrator sees the list either way.</p>

    <div class="setting wide"><label class="q" for="gsLeave">Whether a member may leave:</label>
      ${pick("gsLeave", one.membersMayLeave === false ? "no" : "yes", [
        ["yes", "They may leave on their own"],
        ["no", "Only the owner or a manager removes them"],
      ])}<span class="u"></span></div>
    <p class="hint">The second is what ArcGIS calls an administrative group. Nobody is trapped by it:
      the owner and its managers can still remove anyone.</p>

    <div class="row" style="margin:var(--gap-4) 0">
      <button class="primary" id="gsSave" disabled>Save</button>
      <span class="val" id="gsDirty"></span>
    </div>

    <h4>Fixed when the group was made</h4>

    <div class="setting wide"><span class="q">What members may do with what is already in it:</span>
      <select disabled aria-label="What members may do with what is already in it"><option>${
        one.itemUpdate === "allItems"
          ? "Edit every service shared with it"
          : one.itemUpdate === "ownItems"
            ? "Edit the ones they shared themselves"
            : "Read them"}</option></select><span class="u"></span></div>
    <p class="hint">Fixed when the group was made. Widening it would make every service already shared
      with the group editable by every member, retroactively — so there is no write path for it at all
      (ADR-036 §4c). To change it, make another group and move the shares.</p>

    <h4>Deletion</h4>

    <div class="setting wide"><span class="q">Protect this group from being deleted:</span>
      <label><input type="checkbox" id="gsLock"${one.deleteLocked ? " checked" : ""}
        aria-label="Protect this group from being deleted">
        ${one.deleteLocked ? "Locked" : "Not locked"}</label>
      <span class="u"></span></div>
    <p class="hint">Nobody can delete this group while this is on, <b>including an administrator</b>.
      A confirmation is dismissed by habit; a lock has to be turned off deliberately, here, where what
      the group holds is on the next tab. <b>Applied the moment it is ticked, not on Save</b> — the
      same rule a service's sharing follows, and it is stated because a safety switch left unsaved is
      believed-on and off.</p>

    ${one.mayDelete ? `
      <div class="row" style="margin-top:var(--gap-3)">
        <button class="tiny danger" id="groupDelete"${one.deleteLocked ? " disabled" : ""}
          >Delete group</button>
        <span class="hint" style="margin:0">${one.deleteLocked
          ? `Turn the protection off first — the switch is one row above.`
          : `Its members and its shares go with it. The services themselves are untouched.`}</span>
      </div>` : ""}`;

  // Dirty tracking, so Save is disabled until there is something to save. The lock is deliberately
  // not part of it: it writes on its own, because a safety switch left unsaved is believed-on and off.
  const watch = ["gsTitle", "gsSummary", "gsDescription",
                 "gsVisibility", "gsJoin", "gsContribute", "gsMemberList", "gsLeave"];

  const reading = () => watch.map(id => $(id).value).join("\u0000");
  const was = reading();

  const check = () => {
    const dirty = reading() !== was;
    $("gsSave").disabled = !dirty;
    $("gsDirty").textContent = dirty ? "unsaved" : "";
  };

  // The three text rows are `input`, the three selects are `change`; a text field that only reported
  // on blur would leave Save disabled while somebody typed into it.
  for (const id of watch) {
    $(id).onchange = check;
    $(id).oninput = check;
  }
}

/**
 * What is ticked on the add page, and which folder it is looking at.
 *
 * <b>The selection outlives paging and filtering, deliberately.</b> Anything else lets the footer
 * count exceed what somebody believes they picked, or shrink silently when they type — and this
 * console has twice shipped a control that could not be operated, so the rule is stated rather than
 * left to fall out of the rendering.
 */
let addPicked = new Set();
let addFolder = null;
let addOffered = [];

/**
 * The page that chooses what to put in a group.
 *
 * <b>It replaces a `<select size="8">` of qualified names, which the owner rejected on sight:</b>
 * *"going with name only is not feasible. I need to see thumbnail etc for items."* They are right at
 * any scale and unarguable at theirs — 512 items in a list box is not something a person chooses from.
 *
 * <b>Ten a page, which is the decision that makes the pictures affordable.</b> A preview is a real
 * query against the service, measured at roughly 115 KB for a dense line layer: sixty rows a page —
 * the reference's number — would be about 120 requests and over two megabytes *per page*, which is a
 * load test dressed up as a screen. At ten, `paintPreviews` walks the page in DOM order and awaits
 * each, which is the mechanism that already exists.
 */
async function drawGroupAdd(one) {
  const answer = await api("/content/items") || {};

  // <b>Your own content, and not the empty services.</b> A service with no layers has nothing to
  // share into a group and nothing to draw, so offering it would be a row whose picture can only be
  // hatching and whose add achieves nothing. They stay visible on My content, where their owner needs
  // to see the residue publishing leaves — the count line below says how many were held back.
  const own = (answer.items || []).filter(i => i.scope === "mine");
  const empties = own.filter(i => i.empty).length;

  addOffered = own.filter(i => !i.empty);

  const already = new Set((one.items || []).map(i => i.name));
  const needle = ($("addFilter")?.value || "").trim().toLowerCase();

  const shown = addOffered.filter(i =>
    (addFolder === null || (i.folder || "") === addFolder)
    && (!needle || [i.name, i.kind, i.description].some(v => (v || "").toLowerCase().includes(needle))));

  // ------------------------------------------------------------------ the rail
  const counted = folder => addOffered.filter(i => (i.folder || "") === folder).length;

  $("addRail").innerHTML = `
    <a class="rail-item${addFolder === null ? " on" : ""}" data-add-folder=""
      ${addFolder === null ? ' aria-current="page"' : ""}>All your content
      <span class="rail-count">${num(addOffered.length)}</span></a>
    ${(answer.folders || [])
      .filter(f => counted(f) > 0)
      .map(f => `
        <a class="rail-item${addFolder === f ? " on" : ""}" data-add-folder="${h(f)}"
          ${addFolder === f ? ' aria-current="page"' : ""}>${f === "" ? "Site (root)" : h(f)}
          <span class="rail-count">${num(counted(f))}</span></a>`).join("")}`;

  // ------------------------------------------------------------------ select-all, with its number
  // <b>The label carries the count, so its scope is not something to guess at.</b> A bare tri-state
  // checkbox is exactly the control this console has shipped unusable twice; *Select all 4 matching*
  // says what it will do. Rows already in the group are excluded from it — ticking them again is not
  // an act.
  const selectable = shown.filter(i => !already.has(i.name));
  const ticked = selectable.filter(i => addPicked.has(i.name)).length;

  $("addAllLabel").textContent = needle || addFolder !== null
    ? `Select all ${num(selectable.length)} matching`
    : `Select all ${num(selectable.length)}`;

  $("addAll").checked = selectable.length > 0 && ticked === selectable.length;
  $("addAll").indeterminate = ticked > 0 && ticked < selectable.length;
  $("addAll").disabled = selectable.length === 0;

  $("addCount").textContent = needle || addFolder !== null
    ? `showing ${num(shown.length)} of ${num(addOffered.length)}`
    : `${num(addOffered.length)} service${addOffered.length === 1 ? "" : "s"} you own`;

  // ------------------------------------------------------------------ the rows
  $("addRows").innerHTML = addOffered.length === 0
    ? `<tr><td colspan="5" class="empty">You have published nothing to share. <b>New layer</b>, on
         My content, publishes one.</td></tr>`
    : shown.length === 0
      ? `<tr><td colspan="5" class="empty">Nothing matches. The search reads a service's name, its
           kind and its description.</td></tr>`
      : pageOf("addRows", shown).map(i => {
        const here = already.has(i.name);

        // <b>Already-shared rows are shown, ticked and disabled — not filtered out.</b> Somebody
        // hunting for a service they shared last week needs to be told it is already there, not to
        // find it missing and share it twice. This also answers a live defect: the old picker offered
        // a service that was already in the group.
        return `
        <tr class="${here ? "already" : ""}">
          <td class="tick"><input type="checkbox" data-add="${h(i.name)}"
            ${here || addPicked.has(i.name) ? "checked" : ""}${here ? " disabled" : ""}
            aria-label="${here ? `${h(i.name)}, already in this group` : `Add ${h(i.name)}`}"></td>
          <td class="thumbcell">${i.cover
            ? `<img class="thumb" alt="" loading="lazy"
                 data-thumb="${h(thumbnailFor(i.cover.url))}">`
            : `<div class="thumb empty" title="This service has no layer to draw, so there is no map to show."></div>`}</td>
          <td class="name">${h(i.name)}
            <div class="rowmeta">${h(i.kind)}${i.description
              ? ` · ${h(i.description)}` : ""} · ${num(i.layers)} layer${i.layers === 1 ? "" : "s"}
              ${here ? `· <b>already in ${h(one.name)}</b>` : ""}</div></td>
          <td>${i.sharing === "group"
            ? `yes`
            : `<b>no</b> <span class="val">— its own scope is ${h(i.sharing)}</span>`}</td>
          <td class="val">${day(i.updated)}</td>
        </tr>`;
      }).join("");

  $("addRowsPager").innerHTML = pagerFor("addRows", shown.length);

  // ------------------------------------------------------------------ the footer counts the selection
  // <b>The selection, not the page</b>, because the selection is what the button will do — and it says
  // when part of it is somewhere you cannot see.
  const onPage = new Set(pageOf("addRows", shown).map(i => i.name));
  const offPage = [...addPicked].filter(n => !onPage.has(n)).length;

  $("addCommit").textContent = addPicked.size === 0
    ? "Add items"
    : offPage > 0
      ? `Add ${num(addPicked.size)} item${addPicked.size === 1 ? "" : "s"}`
        + ` (${num(offPage)} not on this page)`
      : `Add ${num(addPicked.size)} item${addPicked.size === 1 ? "" : "s"}`;

  $("addCommit").disabled = addPicked.size === 0;
  $("addCancel").setAttribute(
    "href", `#/group/${encodeURIComponent(one.name)}/content`);

  // <b>Two absences named rather than left as a shorter list.</b> An empty service held back, and the
  // two-step share the reference's own dialog says nothing about — ours has to, because sharing into a
  // group and setting the service's scope are separate acts and either alone reads as done.
  $("addNote").innerHTML = [
    empties > 0
      ? `${num(empties)} empty service${empties === 1 ? " is" : "s are"} not listed — they hold
         nothing to share.`
      : "",
    `A service reaches this group's members only once its own scope is <b>group</b> as well. Overview
     lists the ones that do not, and sets it.`,
  ].filter(Boolean).join(" ");
}

/**
 * Writes a group's settings, whole, from a patch.
 *
 * <b>The only path allowed to build that body, and the reason is a bug this caught before it
 * shipped.</b> The endpoint replaces every field, including title, summary and description — so a
 * caller that assembles the body from the controls in front of it erases whatever is not in front of
 * it. The port's own documentation said *"or null to leave it"* for an hour while the statement wrote
 * `set title = @title`; a design review found it before either screen existed. Overlay on the last
 * read, never on the DOM alone.
 */
async function saveGroupSettings(patch, what) {
  if (!groupNow) return;

  const body = {
    title: groupNow.title ?? null,
    summary: groupNow.summary ?? null,
    description: groupNow.description ?? null,
    visibility: groupNow.visibility || "members",
    joinPolicy: groupNow.joinPolicy || "invitation",
    contribute: groupNow.contribute || "managers",
    deleteLocked: !!groupNow.deleteLocked,

    // <b>Sent every time, not only when they changed.</b> The endpoint takes the whole settings
    // object, so a field left out is a field set to its default — which is how ticking the delete
    // lock would quietly reopen a member list somebody had narrowed.
    memberList: groupNow.memberList || "members",
    membersMayLeave: groupNow.membersMayLeave !== false,
    ...patch,
  };

  try {
    const answer = await api(
      `/admin/groups/${encodeURIComponent(groupNow.name)}/settings`,
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });

    // <b>The note is about visibility, so it is shown only when visibility moved.</b> The endpoint
    // returns it on every call because the write is whole-object — so ticking the delete lock produced
    // *"Locked. Nobody can delete this group… — Only its members can find it. What is shared with it
    // was already readable by them…"*, a lecture about discovery attached to a lock. Read cold in a
    // design review, which is the only way that kind of thing is ever caught.
    const moved = "visibility" in patch && patch.visibility !== groupNow.visibility;

    toast(moved && answer?.note ? `${what} ${answer.note}` : what, true);
    await refreshGroup();
  } catch (e) { toast(e.message); }
}

/**
 * Roles and what they grant.
 *
 * <b>ADR-035, and the shape is the reference's role editor — §4f.</b> Two sections, per-category
 * counts, and *set from existing role*, which is the control the owner marked in the screenshot:
 * with eighteen privileges here and sixty-five there, the realistic edit is *"publisher, but without
 * share-to-public"*, and starting from an empty set makes the operator rebuild something somebody
 * already designed. The errors that produces are omissions, and omissions are invisible.
 *
 * <b>The catalogue comes from the server, not from here.</b> A console that knew the privilege list
 * would hold a second copy of it and the two would disagree the first time either moved — the
 * failure ADR-035 §2 is about. So the response says which privileges exist, which section each is
 * in, what each requires and what each contains, and this file renders whatever it is given.
 */
let roleOpen = null;

/**
 * What a role grants, what carries administrative weight, and how many hold each — for the two
 * confirmations that ask before capability changes hands.
 *
 * <b>Three maps rather than one object</b>, because they answer three questions and are filled from two
 * different arrays in the same response: the privilege catalogue says which names are administrative,
 * and the role list says who holds what.
 */
const roleAdministrative = new Map();

/** The user-type ladder, in the order the platform states it. */
let memberUserTypes = [];
const roleHolders = new Map();
const roleIsAdministrative = new Set();

/**
 * Re-reads the ticks and updates every count and the compatibility line.
 *
 * <b>Recomputed from the checkboxes rather than tracked.</b> A tick changes a group count, a section
 * count and possibly which user types may hold the role; keeping three counters in step by hand is
 * how one of them comes to disagree, and there are eighteen boxes rather than eighteen thousand.
 */
/**
 * Ticks what a privilege requires, and unticks what requires it.
 *
 * <b>The server refuses a role that grants a privilege without its prerequisite, and refusing is
 * right — auto-adding at the API would widen a grant the operator did not make.</b> On the screen
 * the operator is present, so the tick happens in front of them and is named in a toast: they can
 * see both boxes and untick either. That is the difference between a server deciding and a form
 * helping.
 *
 * <b>Both directions, because only one of them is obvious.</b> Ticking `publishFeatures` ticks
 * `create`. Unticking `create` must also untick `publishFeatures`, or the operator saves a set the
 * server will refuse and has to work out which of eleven boxes caused it.
 *
 * The dependency table comes from the server — `needs` on each catalogue entry — and is rendered
 * into the label, so this reads the page rather than holding a second copy.
 */
function followRoleDependencies(box) {
  const boxes = [...document.querySelectorAll("#rolePrivileges input[data-privilege]")];
  const by = new Map(boxes.map(b => [b.dataset.privilege, b]));

  // <b>Read as data, and it used to be read as text.</b> This took the first `.val` chip in the
  // label and matched `needs (.+)` against it — so the dependency table was carried in a
  // sentence, and it survived only as long as nothing else was ever put in front of that chip.
  // 2026-09-05 something was: the ceiling note the 1c handoff asks for went in above it, the
  // regex stopped matching, and ticking `publishFeatures` silently stopped ticking `create` —
  // which the server then refuses, on a screen showing a set the operator believes is legal.
  // The comment above still says *this reads the page rather than holding a second copy*, and
  // that is still true and still the reason: it reads the page's own `data-needs`.
  const needsOf = element =>
    (element.dataset.needs || "").split(",").map(x => x.trim()).filter(Boolean);

  const added = [];
  const removed = [];

  if (box.checked) {
    // Walk the chain: deleteOwn needs create, and a longer chain later needs no new code.
    const pending = [box];

    while (pending.length) {
      for (const name of needsOf(pending.pop())) {
        const need = by.get(name);
        if (need && !need.checked && !need.disabled) {
          need.checked = true;
          added.push(name);
          pending.push(need);
        }
      }
    }
  } else {
    // Anything that named this one is now unsatisfiable.
    let again = true;

    while (again) {
      again = false;

      for (const other of boxes) {
        if (!other.checked || other.disabled) continue;

        if (needsOf(other).some(name => !by.get(name)?.checked)) {
          other.checked = false;
          removed.push(other.dataset.privilege);
          again = true;
        }
      }
    }
  }

  if (added.length) toast(`Also ticked ${added.join(", ")} — required by what you chose.`, true);
  if (removed.length) toast(`Unticked ${removed.join(", ")} — they need what you just removed.`);
}

function recountRoleSections() {
  const sections = [...document.querySelectorAll("#rolePrivileges .rolesection")];
  let anyAdmin = false;

  sections.forEach((section, index) => {
    const boxes = [...section.querySelectorAll("input[data-privilege]")];
    const on = boxes.filter(b => b.checked).length;

    const head = section.querySelector("h4 .val");
    if (head) head.textContent = `${on}/${boxes.length}`;

    const button = section.querySelector("h4 button");
    if (button) button.textContent = on === boxes.length ? "Disable all" : "Enable all";

    for (const group of section.querySelectorAll(".rolegroup")) {
      const mine = [...group.querySelectorAll("input[data-privilege]")];
      const count = group.querySelector(".rolegrouphead .val");
      if (count) count.textContent = `${mine.filter(b => b.checked).length}/${mine.length}`;
    }

    // The second section is the administrative one — `loadRoles` renders general first.
    if (index === 1 && on > 0) anyAdmin = true;
  });

  const line = $("roleCompatibility")?.querySelector("b");

  if (line) {
    line.textContent = anyAdmin
      ? "Only the unrestricted user type can hold this role"
      : "Any user type can hold this role";
  }
}

async function loadRoles() {
  const answer = await api("/admin/roles") || {};
  const roles = answer.roles || [];
  const catalogue = answer.catalogue || [];

  if (!roles.some(r => r.name === roleOpen)) roleOpen = roles[0]?.name ?? null;

  const chosen = roles.find(r => r.name === roleOpen) || null;

  $("roleRows").innerHTML = roles.length === 0
    ? `<tr><td colspan="5" class="empty">No roles, which cannot happen: the schema seeds five.</td></tr>`
    : pageOf("roleRows", roles).map(r => `
      <tr class="pick${r.name === roleOpen ? " on" : ""}" data-role="${h(r.name)}">
        <td class="name"><button type="button" class="rowname"
            data-role="${h(r.name)}">${h(r.name)}</button>${r.builtIn
          ? ` <span class="val">built in</span>` : ""}</td>
        <td class="val">${h(r.description || "")}</td>
        <td class="num">${num(r.privileges.length)}</td>
        <td class="num">${num(r.members)}</td>
        <td style="text-align:right">${r.removable
          ? `<button class="tiny danger" data-role-delete="${h(r.name)}"
               ${r.members ? "disabled title='Members hold this role'" : ""}>Delete</button>`
          : ``}</td>
      </tr>`).join("");

  $("rolesPager").innerHTML = pagerFor("roleRows", roles.length);

  // <b>What the save confirmation reads, recorded where the answer already is.</b> Held rather than
  // re-fetched: the confirmation must describe the change against what is *stored*, and the ticks on
  // screen are what is *wanted* — asking the server again between the two would be a third answer.
  roleAdministrative.clear();
  roleHolders.clear();
  roleIsAdministrative.clear();

  for (const entry of catalogue) {
    if (entry.administrative) roleIsAdministrative.add(entry.name);
  }

  for (const r of roles) {
    roleHolders.set(r.name, r.members || 0);
    roleAdministrative.set(
      r.name, (r.privileges || []).filter(privilege => roleIsAdministrative.has(privilege)));
  }

  $("roleCount").textContent =
    `${roles.length} role${roles.length === 1 ? "" : "s"}, `
    + `${catalogue.length} privilege${catalogue.length === 1 ? "" : "s"}`;

  if (!chosen) { $("roleEditor").hidden = true; return; }

  $("roleEditor").hidden = false;

  // <b>And how many accounts it changes, because *Save privileges* changes them retroactively.</b> The
  // editor named only the role, and the member count was in the table row you scrolled past to get
  // here — eighteen privilege rows earlier on a full page. A grant here is not a setting on a role, it
  // is a capability handed to N live accounts, and the number belongs beside the button that hands it
  // over. Design review 2026-08-19.
  $("roleEditorName").innerHTML = `<b>${h(chosen.name)}</b>`
    + (chosen.members
      ? ` <span class="val">held by ${num(chosen.members)} `
        + `member${chosen.members === 1 ? "" : "s"} — saving changes what they can do</span>`
      : ` <span class="val">held by nobody, so saving changes nothing yet</span>`);

  // <b>Set from existing role.</b> Every other role is an option; choosing one copies its ticks
  // into this editor without saving, so the operator can then narrow it. Absent for the
  // administrator, which cannot be edited at all.
  $("roleFrom").innerHTML = chosen.editable
    ? `<label>Set from existing role
         <select id="roleFromPick"><option value="">choose a role…</option>${
           roles.filter(r => r.name !== chosen.name)
             .map(r => `<option value="${h(r.name)}">${h(r.name)} (${r.privileges.length})</option>`)
             .join("")}</select></label>`
    : "";

  const held = new Set(chosen.privileges);

  // <b>Grouped by the prefix the privilege already carries.</b> `content:`, `sharing:`, `groups:`
  // and so on are categories the names state; inventing a second grouping here would be a mapping
  // to maintain, and the reference groups the same way.
  //
  // <b>And each one says what it does, D-100.</b> The structure of this screen was credited by the
  // design review and the gap it found was meaning: eighteen bare identifiers under a role that
  // gets a sentence. The sentence comes from the server, beside the enum, for the same reason the
  // catalogue does — a console that carried its own copy would be a second list to keep in
  // step with the one being enforced.
  const section = administrative => {
    const mine = catalogue.filter(c => !!c.administrative === administrative);
    const groups = new Map();

    for (const c of mine) {
      const key = c.name.split(":")[0];
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(c);
    }

    const on = mine.filter(c => held.has(c.name)).length;

    return `<div class="rolesection">
      <h4>${administrative ? "Administrative privileges" : "General privileges"}
        <span class="val">${num(on)}/${num(mine.length)}</span>
        ${chosen.editable
          ? `<button class="tiny ghost" data-role-all="${administrative ? "admin" : "general"}"
               >${on === mine.length ? "Disable all" : "Enable all"}</button>` : ""}</h4>
      ${[...groups.entries()].map(([key, items]) => `
        <div class="rolegroup">
          <div class="rolegrouphead">${h(key)}
            <span class="val">${num(items.filter(c => held.has(c.name)).length)}/${num(items.length)}</span></div>
          ${items.map(c => {
            // <b>The 1c handoff: *privileges capped by user type are greyed with a note*.</b>
            // ADR-018 §1's intersection, made visible on the row it applies to — a tick here does
            // nothing for a member whose type withholds it, and the screen said that only in
            // aggregate, in one sentence under the whole list.
            //
            // <b>The note is the ceiling, not the list of who is under it.</b> Measured against
            // the running server: every one of the eighteen privileges is capped for somebody,
            // because a viewer carries almost nothing — so *greyed when capped* would grey all
            // eighteen and say nothing. `leastUserType` is the same fact in two words, and the
            // server computes it because the ladder is that table's property rather than this
            // screen's guess.
            //
            // <b>Muted only where the ceiling is the highest one, and never disabled.</b> The
            // privilege is grantable: the role really does carry it and it really does work for
            // an unrestricted member. Disabling the box would say *you cannot grant this*, which
            // is false — the prototype greys the text and leaves the control, and it is right.
            // The grey is spent on the six a creator cannot reach, which is where a reader
            // ticking boxes is most likely to be surprised.
            // <b>Absent and null are different answers.</b> A server older than this field says
            // nothing about ceilings and the row should say nothing either; an explicit null is
            // the server stating that no user type carries this, which cannot happen today and
            // must be loud if it ever does. Reading both as "nothing carries it" would put that
            // sentence on all eighteen rows the first time this console met an older server.
            const least = c.leastUserType;
            const narrow = least === "unrestricted";

            return `
            <label class="roleprivilege${narrow ? " capped" : ""}">
              <input type="checkbox" data-privilege="${h(c.name)}"
                data-needs="${h(c.requires.join(","))}"
                ${held.has(c.name) ? "checked" : ""}
                ${chosen.editable ? "" : "disabled"}>
              <span class="privilegetext">
                <span class="mono">${h(c.name)}</span>
                ${least === undefined
                  ? ""
                  : least === null
                    ? `<span class="val cappednote">no user type carries this</span>`
                    : `<span class="val${narrow ? " cappednote" : ""}">${narrow
                        ? "unrestricted only"
                        : `${h(least)} or above`}</span>`}
                ${c.requires.length
                  ? `<span class="val">needs ${c.requires.map(h).join(", ")}</span>` : ""}
                ${c.includes.length
                  ? `<span class="val">includes ${c.includes.map(h).join(", ")}</span>` : ""}
                ${c.description
                  ? `<span class="privilegewhat">${h(c.description)}</span>` : ""}
              </span>
            </label>`;
          }).join("")}
        </div>`).join("")}
    </div>`;
  };

  $("rolePrivileges").innerHTML = section(false) + section(true);

  // <b>Which user types may hold this, derived rather than asked.</b> Esri states the rule plainly
  // — a role carrying administrative privileges can only be held by the higher user types — so it
  // is a consequence of what is ticked and the screen says it instead of offering a control.
  const anyAdmin = catalogue.some(c => c.administrative && held.has(c.name));

  $("roleCompatibility").innerHTML = chosen.editable
    ? `<p class="hint"><b>${anyAdmin
        ? "Only the unrestricted user type can hold this role"
        : "Any user type can hold this role"}</b> — ${anyAdmin
        ? "it carries administrative privileges, and ADR-018 §3a's ceiling withholds those from "
          + "viewer, editor and creator. A member with a lower type keeps the role and the "
          + "privileges do nothing, which the refusal says in words."
        : "nothing here is administrative, so each member's type narrows only what their own "
          + "type withholds."}</p>`
    : `<p class="hint"><b>This role cannot be edited or removed.</b> An administrator passes every
         privilege check without consulting these rows (ADR-035 §4b), so the ticks are what it holds
         and not what limits it. To take administrative power from somebody, change their role.</p>`;

  $("roleSave").hidden = !chosen.editable;
}

/**
 * Ten rows a page, on every Server listing, from one place.
 *
 * <b>Owner 2026-08-18:</b> *"server tarafında görüntülenen max item sayısı 10 olmalı. 10 üstü
 * paging olacak."* Server lists services, data sources and members, and before this every one of
 * them rendered its whole result — a deployment of the scale CLAUDE.md §7 targets, 100 to 1,000
 * services, would have drawn a thousand rows into one table.
 *
 * <b>One mechanism, not three, and that is the part worth defending.</b> D-61 is a debt about the
 * console solving the same problem separately on each screen until the screens disagreed; three
 * hand-rolled pagers would be the same mistake with a new subject. So: a page index per list id,
 * a slice, and a control strip — and a list opts in by calling {@link pageOf} and
 * {@link pagerFor} with its own id.
 *
 * <b>Paging is applied after filtering, and changing a filter resets to page one.</b> The
 * alternative is the bug every list of this shape has: filter to three results while standing on
 * page four, and the table is empty while the count says three.
 */
const PAGE_SIZE = 10;

/** Which page each list is on, by the id of its `tbody`. */
const pages = new Map();

/**
 * The slice of `rows` this list is currently showing.
 *
 * <b>Clamped rather than trusted.</b> A page index survives a reload of the list, and the list
 * may have got shorter — a service deleted, a filter typed — so the index is brought back into
 * range here instead of at every place that could shorten it.
 */
function pageOf(id, rows) {
  const last = Math.max(0, Math.ceil(rows.length / PAGE_SIZE) - 1);
  const at = Math.min(pages.get(id) ?? 0, last);

  pages.set(id, at);
  return rows.slice(at * PAGE_SIZE, (at + 1) * PAGE_SIZE);
}

/**
 * The control strip under a list, or nothing at all when there is one page.
 *
 * <b>Absent rather than disabled when it is not needed.</b> A deployment with four services has
 * no use for *Page 1 of 1* and two dead arrows; a control that never does anything is the kind
 * of furniture that makes a dense screen unreadable.
 *
 * <b>It says which rows, not only which page.</b> *11–20 of 47* answers the question somebody
 * actually has when a list is paged, which *Page 2* does not.
 */
function pagerFor(id, total) {
  if (total <= PAGE_SIZE) {
    pages.set(id, 0);
    return "";
  }

  const at = pages.get(id) ?? 0;
  const last = Math.ceil(total / PAGE_SIZE) - 1;
  const first = at * PAGE_SIZE + 1;
  const upto = Math.min(total, (at + 1) * PAGE_SIZE);

  return `<div class="pager">
    <button class="tiny ghost" data-page="${h(id)}" data-page-to="${at - 1}"
      ${at === 0 ? "disabled" : ""} title="Previous ${PAGE_SIZE}">&larr;</button>
    <span class="val">${num(first)}&ndash;${num(upto)} of ${num(total)}</span>
    <button class="tiny ghost" data-page="${h(id)}" data-page-to="${at + 1}"
      ${at === last ? "disabled" : ""} title="Next ${PAGE_SIZE}">&rarr;</button>
  </div>`;
}

/** Sends a list back to its first page, which every filter change must do. */
function resetPage(id) {
  pages.set(id, 0);
}

const SURFACES = {
  server: {
    title: "Server",
    needs: "admin:manageServer",
    home: "services",

    // <b>Server's action is a service, Studio's is a layer.</b> It used to open a drawer that
    // made an empty container; *katmansiz servis yaratilamaz* by owner decision, so it goes to
    // Publish instead (ADR-057 5h) - tables into a tree, the tree is the service, one request
    // writes it. The label did not change, because what an operator wanted from it did not.
    action: { id: "publishService", label: "New service" },

    tabs: [
      ["services", "Services"],

      // <b>Publish, and it is Server's because a registered database is.</b> ADR-057: a
      // service is composed from tables in databases this server was pointed at, and pointing
      // it at one is an administrator's act on the tab next door. Studio publishes a layer
      // somebody imported; this publishes a service somebody assembled.
      ["publish", "Publish"],

      // <b>Data sources is Server's, by owner decision 2026-08-17</b> — *"data sources studio'nun
      // değil server'in bir seçeneği. onu da sadece admin ayarlayabilir."* This corrects ADR-034
      // §5c, which had put it in Studio beside publishing. Registering a source is not publishing:
      // it hands this server a credential for somebody else's database and adds a machine the
      // deployment then depends on, and its failures are operational.
      ["sources", "Data sources"],

      // <b>Members, needing `admin:manageMembers`.</b> It sits in Server because making somebody
      // an administrator is an administrative act, not a content one — the same split §5c draws
      // everywhere else. The tab is drawn for everybody who can enter Server, which today is the
      // same set: the administrator role carries both privileges.
      ["members", "Members"],

      // <b>Roles, needing `admin:manageRoles` — ADR-035.</b> Server's, because granting a
      // capability is administrative even though most of the capabilities it hands out are
      // Studio's: the same split ADR-034 §5c draws everywhere else. The tab sits beside Members
      // because *who is there* and *what they may do* are read together.
      ["roles", "Roles"],

      ["operations", "Operations"],

      // <b>Beside Operations, because they answer the same shift.</b> Operations says what
      // this process is doing now; Logs says what it has done. The owner asked for it here:
      // *"hem server hem studio ile ilgili logların sorgulandığı bir ekran lazım. bu da
      // server ekranında olmalı."* ADR-045.
      ["logs", "Logs"],
    ],
  },
  studio: {
    title: "Studio",
    needs: null,
    home: "content",
    tabs: [
      ["content", "My content"],

      // <b>*"no anonymous view for server"* — the owner, 2026-08-17.</b> ADR-034 §6 had
      // already half-said this: the screen answers *what does a stranger see of this layer*,
      // which is a question about content and its sharing, not about a running server. It
      // stayed in Server because that is where it was written and where the listing it read
      // lived. Now it reads the content listing, which is Studio's, so it is where it belongs
      // rather than where it started.
      ["anonymous", "Anonymous view"],

      // <b>Groups — ADR-036, and Studio's because ADR-034 §6 said so before they existed:</b>
      // *"Q-112 (groups), when answered, lands in Studio."* A group is content collaboration — who
      // may read what somebody published — which is the publisher's business rather than the
      // administrator's.
      ["groups", "Groups"],
    ],
    action: { id: "newLayer", label: "New item" },
  },
};

/**
 * Which surface each screen lives in.
 *
 * <b>So a screen asked for in the wrong surface is a navigation, not a 404.</b> A link to
 * `#/sources` from Server means Studio's data sources — the reader is not wrong, the address is
 * just short — and the same table is what lets a hash left over from earlier today, when the
 * surface was in the hash, be translated once instead of guessed at.
 */
const SCREEN_SURFACE = {
  services: "server",

  // <b>Publish, and the suite caught this being missing.</b> A tab that is not in this table is
  // an address the other surface accepts and silently redirects — D-115's *silent navigation*,
  // which has happened twice. Adding a tab is two edits and this is the second one.
  publish: "server",

  // <b>Deliberately absent: `service`, for the reason `layer` is absent two lines down.</b> Naming
  // Server as the owner made **Studio's only service page unreachable** — `sharing` belongs to Studio
  // (`SERVICE_PAGES`, owner decision 2026-08-17), the router forced every `#/service/...` onto Server
  // before `servicePagesOf(surfaceOfPath())` was consulted, so `sharing` could never be in the page
  // set and `drawServiceSettings` fell back to Capabilities. Both of the two links this console
  // provides *to* the Sharing page landed on Capabilities instead, silently, with nothing to say the
  // page had not opened. Found by the design review 2026-08-19; the same mistake, the same table, and
  // the note below was already there describing it for `layer`.
  //
  // Which pages a service page shows is `SERVICE_PAGES`, and the router asks that instead.

  // <b>Deliberately absent: `layer`.</b> It is the one screen that lives in both surfaces, and
  // naming a single owner here is what sent every sharing link to Server. Which surface a layer's
  // page belongs to is `LAYER_PAGES`, and the router asks that instead.
  operations: "server",

  // <b>Server's, because reading a log is an operational act.</b> It carries principals,
  // source addresses and paths — most of what somebody probing a deployment wants — so it
  // sits behind `admin:manageServer` with the rest of Server. ADR-045.
  logs: "server",

  // <b>`group` beside `groups`, and forgetting it is the silent failure the comment above records.</b>
  // A screen absent from this table falls through to its surface's home, so `/server/#/group/planning`
  // would land an administrator on Services with no explanation.
  group: "studio",
  content: "studio",
  anonymous: "studio",
  sources: "server",
  members: "server",

  // <b>Both were missing, and the failure was silent.</b> A screen absent from this table falls
  // through to the surface's home — so `/server/#/groups` landed an administrator on Services with
  // no explanation, which is exactly the *"a screen asked for in the wrong surface is a navigation,
  // not a 404"* promise above, unkept for the two newest screens.
  roles: "server",
  groups: "studio",
};

let privileges = new Set();

const may = privilege => !privilege || privileges.has(privilege);

/** Which surfaces this reader may enter, in order. */
const allowed = () => Object.keys(SURFACES).filter(name => may(SURFACES[name].needs));

/**
 * Which surface this page *is*, from its own address.
 *
 * <b>The path carries the environment and the hash carries the screen</b> — `/server/#/services`
 * and `/studio/#/content`. The owner's objection was that the application still lived at
 * `/console` after being renamed: *"console yerine server kullanacaktık ya."* A surface in the
 * path is also what ArcGIS does, where the two environments are two applications; ours are one
 * application served at two paths, which is ADR-034 §5a's *one deployable* without pretending
 * the two audiences share an address.
 */
const surfaceOfPath = () =>
  location.pathname.startsWith("/studio") ? "studio" : "server";

/** A link into the other surface, keeping the reader's place in mind. */
const surfaceHref = (surface, hash) => `/${surface}/#/${hash}`;

/**
 * The address, read.
 *
 * <b>The surface is the path and the screen is the hash</b> — `/server/#/services`,
 * `/studio/#/content`. Addresses from before the surfaces existed are translated rather than
 * 404'd, in both shapes they have had today: ADR-020 §5c took *frozen URLs* from the reference
 * as a rule, and a rename is the case that rule is for.
 */
function route() {
  const surface = surfaceOfPath();

  // <b>Refused by leaving, with a sentence.</b> Not a 403 toast over an empty Server screen:
  // the reader cannot be in this environment, so they are put in one they can be, told why.
  // This is the only place that knows their privileges, which is why the redirect from
  // /console cannot make this decision.
  if (!may(SURFACES[surface].needs)) {
    toast(`${SURFACES[surface].title} administers this server, which needs `
      + `${SURFACES[surface].needs}. You are in Studio, where your own content is.`);
    location.replace(surfaceHref("studio", SURFACES.studio.home));
    return;
  }

  // <b>A query string, because a tab cannot go in the path segments.</b> This function splits on `/`
  // before a service's `folder/name` is reassembled, so `#/service/hosted/x/visualization` cannot be
  // told from a service named `x/visualization` — the note beside the service page's tab strip records
  // that. A query sits outside the split entirely, so an entry point can land on a named tab with a
  // named layer without waiting for the larger addressing fix. Design review 2026-08-19 proposed it.
  const cut = location.hash.indexOf("?");
  const path = cut < 0 ? location.hash : location.hash.slice(0, cut);

  hashQuery = new URLSearchParams(cut < 0 ? "" : location.hash.slice(cut + 1));

  const rest = path.replace(/^#\/?/, "").split("/").filter(Boolean)
    .map(decodeURIComponent);

  // <b>A hash that names a surface is from earlier today</b>, when the surface lived in the
  // hash rather than the path: `#/server/services` becomes `/server/#/services`. Translated in
  // place so Back does not bounce between the two shapes.
  if (rest[0] in SURFACES) {
    location.replace(surfaceHref(rest[0], rest.slice(1).join("/")));
    return;
  }

  // <b>A screen that lives in the other surface is a navigation.</b> `#/sources` asked for in
  // Server is Studio's data sources: the reader named a screen, not an environment, and sending
  // them to the environment that has it beats a 404 or a silent fallback to the home screen.
  // <b>A layer's page decides its own surface.</b> The editor is in both surfaces with different
  // pages in each (§5c), so `#/layer/x/sharing` asked for in Server is Studio's — the same
  // translation the screen table does one level up, one level down.
  if (rest[0] === "layer" && rest[1] && rest[2] && LAYER_PAGES[rest[2]]
      && LAYER_PAGES[rest[2]] !== surface) {
    const owner = LAYER_PAGES[rest[2]];

    if (!may(SURFACES[owner].needs)) {
      toast(`That page is in ${SURFACES[owner].title}, which needs ${SURFACES[owner].needs}.`);
      location.replace(surfaceHref(surface, SURFACES[surface].home));
      return;
    }

    location.assign(`/${owner}/${location.hash}`);
    return;
  }

  const lives = SCREEN_SURFACE[rest[0]];
  if (lives && lives !== surface) {
    if (!may(SURFACES[lives].needs)) {
      toast(`That screen is in ${SURFACES[lives].title}, which needs `
        + `${SURFACES[lives].needs}.`);
      location.replace(surfaceHref(surface, SURFACES[surface].home));
      return;
    }
    location.assign(`/${lives}/${location.hash}`);
    return;
  }

  drawSurfaces(surface);

  if (rest[0] === "layer" && rest[1]) {
    const mine = pagesOf(surface);
    showLayer(rest[1], mine.includes(rest[2]) ? rest[2] : mine[0]);
    return;
  }

  // <b>A group and one of its tabs — `#/group/planning/members`.</b> A group's page is addressable
  // where a service's is not, and the difference is real rather than an oversight: `route()` splits on
  // `/` before decoding, so a service's `folder/name` cannot survive a third segment, while a group's
  // name is one `encodeURIComponent`ed segment and can. A group's name may itself contain a slash —
  // nothing validates it — which is exactly why it has to be encoded here.
  if (rest[0] === "group" && rest[1]) {
    showGroup(decodeURIComponent(rest[1]), rest[2]);
    return;
  }

  // A service, and what is in it. The address carries the folder because a service is
  // addressed by folder and name — `#/service/turkiye/tr_ref`.
  if (rest[0] === "service" && rest[1]) {
    showService(rest.slice(1).join("/"));
    return;
  }

  // <b>`#/content/{scope}` — the scope is part of the address.</b> Five sections whose only handle is
  // a click are five things you cannot send anybody to; *here is what the organisation can see* has to
  // be a link. Validated against the table rather than trusted, so a stale bookmark lands on Everything
  // instead of an empty screen.
  if (rest[0] === "content" && rest[1]) {
    contentScope = CONTENT_SCOPES.some(([key]) => key === rest[1]) ? rest[1] : "all";
  }

  const screens = SURFACES[surface].tabs.map(([name]) => name);
  const screen = screens.includes(rest[0]) ? rest[0] : SURFACES[surface].home;

  // The folder a Server services screen is looking at, which is part of its address so that
  // "the services in turkiye" is a place you can link somebody to.
  openScreen(surface, screen, screen === "services" ? rest[1] ?? null : null);
}

/**
 * Places an open row menu against the viewport instead of against its cell.
 *
 * <b>Because the two widest tables have to scroll, and a scroll box clips a dropdown.</b> Server's
 * services list and Studio's content list are wider than a 1024-pixel window — measured 2026-08-19 by
 * hiding subtrees until `document.body.scrollWidth` fell: the tables' own minimum width, header row
 * included, is what exceeds it. So they scroll inside `widetable`, and `overflow-x: auto` computes
 * `overflow-y: auto` too, which clips absolutely positioned descendants. The row menu is one.
 *
 * <b>So an open sheet becomes `position: fixed`, measured from its summary.</b> Fixed positioning is
 * relative to the viewport, so no ancestor can clip it; on close it goes back to `absolute` and the
 * stylesheet's own `right: 0; top: calc(100% + 4px)` applies again. Two lines of arithmetic instead of
 * choosing between a page that scrolls sideways and a menu that is cut in half.
 *
 * <b>Capture phase, because `toggle` does not bubble.</b> The listings are rebuilt on every load, so a
 * listener per menu would have to be reattached each time and the one that was missed would be the one
 * that broke. A capture-phase listener on the document sees a non-bubbling event on any descendant,
 * once, for every menu this console will ever draw.
 */
document.addEventListener("toggle", event => {
  const menu = event.target;
  if (!(menu instanceof HTMLDetailsElement) || !menu.classList.contains("menu")) return;

  const sheet = menu.querySelector(".sheet");
  if (!sheet) return;

  if (!menu.open) {
    sheet.style.position = "";
    sheet.style.top = "";
    sheet.style.right = "";
    sheet.style.left = "";
    return;
  }

  // <b>One open at a time.</b> A fixed sheet does not move with its row, so two of them left open
  // while the table scrolled would sit over each other in the wrong places.
  for (const other of document.querySelectorAll("details.menu[open]")) {
    if (other !== menu) other.open = false;
  }

  const at = (menu.querySelector("summary") || menu).getBoundingClientRect();

  // <b>Clamped into the window, because the summary can be scrolled out of it.</b> The table scrolls
  // horizontally inside `widetable`, so a menu in the last column may sit past the panel's right edge
  // — and positioning against it would put the sheet off screen, which is what the first version did:
  // measured at right=1083 in a 1024 window. A menu you cannot see is not worth positioning precisely.
  sheet.style.position = "fixed";
  sheet.style.top = `${Math.round(at.bottom + 4)}px`;
  sheet.style.right = `${Math.max(8, Math.round(window.innerWidth - at.right))}px`;
  sheet.style.left = "auto";

  // <b>Flipped above the row when there is no room below.</b> A fixed sheet is not clipped, so without
  // this it would simply hang off the bottom of the window on the last row of a long table.
  const box = sheet.getBoundingClientRect();

  if (box.bottom > window.innerHeight - 8) {
    sheet.style.top = `${Math.max(8, Math.round(at.top - box.height - 4))}px`;
  }

  // And if the row itself is below the fold — a long table, opened by keyboard — bring it up rather
  // than leaving the sheet somewhere nobody is looking.
  const after = sheet.getBoundingClientRect();

  if (after.top > window.innerHeight - 8 || after.bottom < 8) {
    sheet.style.top = `${Math.round(Math.max(8, window.innerHeight - after.height - 8))}px`;
  }
}, true);

window.addEventListener("hashchange", route);

/** Draws the header's surface switch and the surface's own tab strip. */
function drawSurfaces(surface) {
  // <b>The surface on the root element, so the stylesheet can colour the environment.</b> Owner
  // 2026-08-18: Studio's sidebar should not be Server's. One attribute rather than a class toggled
  // per element — the router already knows which surface this is, and every rule that depends on it
  // can then say so in the stylesheet instead of in JavaScript.
  document.documentElement.dataset.surface = surface;

  const both = allowed();

  $("surfaces").hidden = both.length < 2;
  for (const link of document.querySelectorAll("#surfaces a")) {
    const name = link.dataset.surface;
    link.href = surfaceHref(name, SURFACES[name].home);
    if (name === surface) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }

  const config = SURFACES[surface];

  // <b>Sections in the sidebar, the surface's action on the page.</b> They used to share the tab
  // strip, so the strip carried both navigation and a verb — and when navigation moved into a
  // column, the button would have been a nav item that is not one. The router fills two slots
  // instead, which is the only change the redesign made to this function.
  $("tabs").innerHTML = config.tabs.map(([name, label]) =>
    `<a href="#/${name}" data-tab="${name}">
       <span class="ico" aria-hidden="true">${SECTION_GLYPH[name] ?? "·"}</span>
       <span class="label">${h(label)}</span>${
       name === "services" ? '<span class="count" id="cServices"></span>' : ""}${
       name === "sources" ? '<span class="count" id="cSources"></span>' : ""}</a>`).join("");

  // <b>One slot, because the other copy was a second element with the same id.</b> This wrote the
  // action into both page heads and said so — *"naming them apart and asking for both is what keeps
  // the router from having to know which view is visible"* — which bought the router one line and
  // put two `id="newLayer"` nodes in the document, one of them inside a hidden view. Clicks were
  // never wrong, because the handler reads `event.target.id` and nobody can press what they cannot
  // see; `getElementById` was, and it returns the hidden one. Found by the design review
  // 2026-08-19, whose own first script pressed the invisible copy and timed out. D-91.
  //
  // The surface's home screen owns the slot, so the router looks up one name instead of knowing
  // which view is on.
  const slots = { services: "pageAction", content: "pageActionContent" };

  const markup = config.action
    ? `<button class="primary" id="${config.action.id}"><span class="plus"
         aria-hidden="true">+</span>${h(config.action.label)}</button>`
    : "";

  for (const [home, id] of Object.entries(slots)) {
    const slot = $(id);
    if (slot) slot.innerHTML = home === config.home ? markup : "";
  }
}

/**
 * One glyph per section, so a sidebar row is scannable when the column is narrow.
 *
 * <b>Characters, not icons.</b> An icon font is a request and a licence; an inline SVG set is four
 * hundred bytes of path data per glyph in a file that is read by people. These are geometric
 * Unicode shapes at one opacity — they carry no meaning the label does not, which is why they can be
 * this plain, and they are the only thing that survives collapsing the sidebar.
 */
const SECTION_GLYPH = {
  services: "◈",
  sources: "▤",
  members: "◍",
  operations: "◎",
  logs: "≡",
  content: "◈",
  anonymous: "◌",
};

/**
 * Goes where the map is, if we are not already there.
 *
 * <b>Drawing something you cannot see is the same as a button doing nothing.</b> The map
 * belongs to Studio's content screen (ADR-034 §5d), so Show and Tiles pressed from a layer's
 * settings page in Server used to add a layer to a map on another screen and leave the
 * operator looking at an unchanged page. Asking to see something is a good enough reason to be
 * taken to it.
 */
/**
 * Brings the map into view after something was drawn on it.
 *
 * <b>It used to send the reader to Studio's content screen, because that is where the map was.</b> The
 * map is the item page's Visualization tab now (§5k), and whoever drew a layer is already on it — so
 * this only has to make sure the panel is in view rather than navigate anywhere.
 */
function toMap() {
  const panel = $("mapPanel");
  if (panel && !panel.hidden) panel.scrollIntoView({ block: "nearest" });
}

/** Shows one view, and marks its tab. */
function showView(id, tab) {
  for (const link of document.querySelectorAll("#tabs a[data-tab]")) {
    if (link.dataset.tab === tab) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  for (const view of document.querySelectorAll(".view")) {
    view.classList.toggle("on", view.id === id);
  }

  // <b>The one screen that takes the window, undone here rather than by each screen that
  // does not.</b> The symbology editor drops the shell's page padding and its own panel frame;
  // leaving that class on while another view is shown would give every other screen a
  // full-bleed layout it was not designed for. `showEditPage` puts it back on.
  if (id !== "view-layer") $("app").classList.remove("symfull");
}

/**
 * Opens a screen, and re-reads what it shows.
 *
 * Re-read on entry, because a screen showing numbers from when the page was opened is worse
 * than one that says how old they are.
 */
function openScreen(surface, screen, folder) {
  // The editing session ends with the page it was on. Leaving it open would let a background
  // refresh redraw a screen nobody is looking at.
  editing = null;

  showView("view-" + screen, screen);

  if (screen === "services") {
    selectedFolder = folder;
    section("folders", loadFolders);
    section("services", loadServices, "services");
  }

  if (screen === "content") {
    section("your content", loadMyContent, "contentRows").then(paintPreviews);
  }
  if (screen === "members") section("members", loadMembers, "members");
  if (screen === "roles") section("roles", loadRoles, "roleRows");
  if (screen === "groups") section("groups", loadGroups, "groupRows");
  if (screen === "operations") section("operations", loadOperations);
  if (screen === "sources") section("data sources", loadSources, "sources");
  if (screen === "publish") section("publish", loadPublish);
  if (screen === "logs") section("logs", loadLogs, "logRows");
}

// ---------------------------------------------------------------------- map

/**
 * Loads the SDK once and teaches it to authenticate.
 *
 * The map's requests go straight from the browser to /rest/services/… and would
 * otherwise carry no credential — so a private or organization layer would 404
 * for the map while the console can see it perfectly. ADR-015 §4's answer to
 * this in the ArcGIS world is a `token=` query parameter, which we deliberately
 * do not implement because a credential in a URL leaks into every proxy log.
 *
 * An interceptor puts it in the Authorization header instead, which is the form
 * ADR-015 §4 says clients should prefer. That it works with the real SDK is the
 * argument for not rushing `token=`.
 */
const SDK = window.GRATICULA_MAP_SDK;

/**
 * Fetches the map SDK the first time a map is wanted, and not before.
 *
 * <b>It used to be a blocking script tag in the head, which made the whole
 * console hostage to a third-party CDN</b> — D-44. Loaded here, a blocked or
 * unreachable CDN costs the map and nothing else, and says so in words instead of
 * leaving buttons that do nothing.
 */
function loadSdk() {
  if (window.require) return Promise.resolve();
  if (loadSdk._started) return loadSdk._started;

  loadSdk._started = new Promise((resolve, reject) => {
    // <b>No literal fallback, deliberately — ADR-034 condition 3.</b> A hard-coded address
    // here would be a copy the Content-Security-Policy does not know about, which is the
    // trap §5e names: the script would be fetched and the browser would refuse it, leaving a
    // dead map and nothing in the server's log. Saying so is the honest failure.
    if (!SDK) {
      reject(new Error(
        "This page did not receive the map SDK's address from the server, so it cannot load "
        + "the map library. surface.js is generated from Graticula:MapSdkUrl and should be "
        + "loaded before this script."));
      return;
    }

    const css = document.createElement("link");
    css.rel = "stylesheet";
    css.href = SDK + "esri/themes/light/main.css";
    document.head.append(css);

    const script = document.createElement("script");
    script.src = SDK;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(
      "The map library could not be loaded from " + SDK + ". Everything else in this "
      + "console works without it; only the map needs it. A script blocker, an offline "
      + "machine or an air-gapped deployment will all look like this."));
    document.head.append(script);
  });

  return loadSdk._started;
}

async function loadEsri() {
  if (esri) return esri;
  await loadSdk();

  return new Promise((resolve, reject) => {
    require([
      "esri/Map", "esri/views/MapView", "esri/layers/FeatureLayer",
      "esri/layers/VectorTileLayer", "esri/layers/GeoJSONLayer",
      "esri/layers/WebTileLayer", "esri/Basemap", "esri/config",
    ], (Map, MapView, FeatureLayer, VectorTileLayer, GeoJSONLayer, WebTileLayer, Basemap,
        config) => {
      config.request.interceptors.push({
        urls: location.origin,
        before: params => {
          params.requestOptions.headers = {
            ...(params.requestOptions.headers || {}),
            Authorization: "Bearer " + token,
          };
        },
      });

      esri = {
        Map, MapView, FeatureLayer, VectorTileLayer, GeoJSONLayer, WebTileLayer, Basemap,
      };
      resolve(esri);
    }, reject);
  });
}

/**
 * The basemap URL template, or null for none. The reader's own setting.
 *
 * <b>There is no default, and removing the one there was is the point.</b> This
 * console shipped `https://tile.openstreetmap.org/...` hardcoded, chosen because
 * Esri's basemaps need an API key tied to an ArcGIS account and the people this
 * product is for do not have one. That reasoning was right about Esri and wrong
 * about OpenStreetMap: their tile servers are volunteer-run and their usage policy
 * does not permit being an application's basemap. On 2026-08-16 every tile came
 * back **403 Access blocked**, which is the policy being enforced rather than a
 * fault to route around.
 *
 * The likely trigger was ours too: `Referrer-Policy: no-referrer` — set for a good
 * reason, because an ArcGIS token can be in a URL — leaves those requests with no
 * Referer, and a refererless browser request is exactly what that policy blocks.
 * Weakening the header to satisfy a third party would be the wrong trade, and it
 * would not make the use legitimate anyway.
 *
 * So: no basemap unless somebody names one. Layers draw on a plain ground, which
 * is all this console needs them to do, and an operator with a tile server of
 * their own can point at it. Kept in `localStorage` because it is a preference of
 * the person reading, not state of the server — ADR-020 §2 is untouched: this adds
 * no capability the admin API does not have, because it adds no server capability
 * at all.
 */
const BASEMAP_KEY = "gis-basemap";
const basemapUrl = () => localStorage.getItem(BASEMAP_KEY) || "";

/**
 * The view, once it is actually usable.
 *
 * <b>A promise rather than the view, and that is the bug this fixes.</b>
 * `ensureMap` used to construct the `MapView` and return it on the same tick,
 * and `showTiles` then called `goTo` on it. A view that has not finished
 * initialising has no animation manager, so `goTo` failed with *"Cannot read
 * properties of undefined (reading 'animation')"* — and only **sometimes**,
 * because a view that happened to be ready worked. Reported 2026-08-16 as
 * happening on the first click.
 *
 * Holding the view in a variable made it worse in a second way: the
 * `if (view) return view` guard returned the half-built view to a second click,
 * so two clicks in the initialising window both raced. Memoising the *promise*
 * means the second caller waits for the first. `loadSdk` in this file already
 * does exactly this, which is the argument for it.
 */
let viewReady = null;

async function ensureMap() {
  // <b>Before the view is built, not after — and getting this backwards cost a
  // silent hang.</b> `#view` lives inside `#mapPanel`, which is `display: none`
  // until it is shown, and both callers used to reveal the panel *after* asking
  // for the view. That was survivable while nothing waited on the view; once
  // `view.when()` was awaited, the first click built a MapView in a hidden,
  // zero-sized container and the promise never settled. Memoising it then made it
  // permanent: every later click joined the same pending promise, so the button
  // did nothing at all, with no error and nothing in any log. Reported by the
  // owner as *"I press it and nothing happens"*, which is the second time this
  // console has failed that way.
  $("mapPanel").classList.add("on");

  if (viewReady) return viewReady;

  viewReady = buildMap();

  try {
    return await viewReady;
  } catch (e) {
    // A failed build must not be remembered as the answer, or every later click
    // returns the same rejection with nothing having been retried.
    viewReady = null;
    view = null;
    throw e;
  }
}

async function buildMap() {
  const { Map, MapView, GeoJSONLayer, VectorTileLayer, WebTileLayer, Basemap } =
    await loadEsri();

  const url = basemapUrl();

  // <b>Our own ground when no tiles are configured</b> — countries, lakes and a
  // graticule from ground.js, so this page and the standalone viewer draw the same
  // map. Removing the OpenStreetMap default left geometry floating in white, where
  // being in the wrong place and having no data look identical.
  // VectorTileLayer passed as well, so an imported ground can replace the vendored
  // world here exactly as it does in the viewer — one rule, ground.js decides.
  // WebTileLayer passed, so ground.js can draw OpenStreetMap here exactly as the
  // viewer does. Without it this page fell through to the vendored Natural Earth
  // files and the owner asked a fourth time why they were still showing.
  const ground = url
    ? []
    : groundLayers({ GeoJSONLayer, VectorTileLayer, WebTileLayer });

  const map = new Map(url
    ? {
      basemap: new Basemap({
        baseLayers: [new WebTileLayer({
          urlTemplate: url,
          copyright: localStorage.getItem(BASEMAP_KEY + "-credit") || "",
        })],
      }),
    }
    : { layers: ground });

  // <b>Web Mercator, named rather than inherited.</b> With no basemap the view
  // takes its reference from whatever loads first, so the same layer could be
  // drawn in 4326 one day and 3857 the next — and hosted data is *stored* in 3857
  // (ADR-021), so asking for anything else makes the server reproject on every
  // request to answer a question nobody asked. Naming it means what is on screen
  // is the geometry as stored.
  // The starting view is an extent carrying its own reference, not a centre — a
  // centre written in degrees is read as metres here, which is how the viewer
  // ended up three and a half million metres from its data and looking blank.
  view = new MapView({
    container: "view",
    map,
    spatialReference: { wkid: 3857 },
    extent: {
      xmin: -20037508, ymin: -20037508, xmax: 20037508, ymax: 20037508,
      spatialReference: { wkid: 3857 },
    },

    // The ocean. Without it the sea is white and the graticule appears to float,
    // which reads as a drawing rather than a map.
    background: url ? undefined : { color: GROUND_WATER },
  });

  // The line the failure was about. Everything a caller does with the view —
  // goTo, add, hitTest — needs it initialised, so it is waited for here once
  // rather than remembered as a rule each caller has to follow.
  //
  // <b>And it is raced against a clock, because a hang is not a rejection.</b>
  // A view that never becomes ready leaves this awaiting forever: no error, no
  // status, nothing logged, and a button that does nothing — which is the exact
  // failure this console has now produced twice for two different reasons. A
  // timeout cannot fix whatever is wrong, but it turns silence into a sentence,
  // and a sentence is what makes the next one diagnosable.
  await Promise.race([
    view.when(),
    new Promise((_, reject) => setTimeout(
      () => reject(new Error(
        "The map did not become ready within 15 seconds. The view is built inside "
        + "the map panel, so this is usually the panel being hidden or having no "
        + "height, or WebGL being unavailable in this browser.")),
      15000)),
  ]);

  return view;
}

/**
 * Rebuilds the map after the basemap setting changes.
 *
 * Everything shown is re-added rather than kept, because the layers belong to the
 * old view and moving them between views is more fragile than asking for them
 * again — the service is the source of truth and the request is cheap.
 */
async function resetBasemap() {
  const showing = [...shown.keys()];
  for (const key of showing) hide(key);

  if (view) {
    view.destroy();
    view = null;
  }

  // Cleared with the view. Leaving it set hands the next caller a promise for a
  // destroyed view, which is the same class of fault as returning an unready one.
  viewReady = null;

  for (const key of showing) {
    const tiles = key.endsWith(" · tiles");
    const name = tiles ? key.slice(0, -" · tiles".length) : key;
    try {
      if (tiles) await showTiles(name);
      else {
        const doc = await api(
          `${layerUrl(name).replace(location.origin, "")}?f=json`);
        await show(name, doc);
      }
    } catch (e) { toast(`${name}: ${e.message || e}`); }
  }

  drawBasemapControl();
}

/**
 * Says which ground is actually drawn, rather than which setting is empty.
 *
 * <b>It used to read "none — layers draw on a plain ground" while OpenStreetMap or
 * Natural Earth was on screen.</b> That is the same fault as the viewer's state
 * line naming Natural Earth while OSM tiles were drawn: a status line that reports
 * a *setting* instead of the *outcome* is worth less than no status line, because
 * it is read as the outcome. So it names what ground.js decided.
 */
function drawBasemapControl() {
  const url = basemapUrl();
  const imported = chosenGroundTiles();

  $("basemapState").textContent = url
    ? url
    : imported.length
      ? `${imported.join(", ")} — imported tiles as the ground`
      : "OpenStreetMap — the default ground; name your own tiles to replace it";

  $("basemapInput").value = url;
}

/**
 * The colour the server says this layer is, for the legend swatch.
 *
 * <b>Read, not chosen — ADR-033 §5b.</b> This console used to pick from a palette of its
 * own, so a layer was one colour here, another in the tile style and a third in whatever
 * client somebody else was using. The layer document now carries `drawingInfo`, the SDK
 * draws the layer from it without being told to, and the only thing left for this file to
 * do is read the same colour into the legend so the swatch matches the map.
 *
 * Esri colours are `[r, g, b, a]` with the channels in 0–255.
 */
function serverColour(doc) {
  const rgba = doc?.drawingInfo?.renderer?.symbol?.color;
  if (!Array.isArray(rgba) || rgba.length < 3) return PALETTE[0];

  return "#" + rgba.slice(0, 3).map(c => Number(c).toString(16).padStart(2, "0")).join("");
}

function drawLegend() {
  if (shown.size === 0) {
    $("legend").innerHTML = "Nothing shown.";
    return;
  }

  // One layer at a time, so the legend says what is on screen and in which
  // reference — the second part because a map with no basemap gives no other clue
  // about where in the world it is looking.
  $("legend").innerHTML = [...shown.entries()].map(([name, s]) =>
    `<span title="The colour the server publishes for this layer"><i class="swatch"
       style="background:${h(s.colour)}"></i><b>${h(name)}</b></span>`).join("")
    + `<span style="margin-left:auto">EPSG:3857</span>`;
}

// Hosted services live under /rest/services/hosted and registered ones do
// not. The server redirects the wrong path, so building the wrong URL still
// works — but it costs a round trip on every request, and a console that
// relies on a compatibility redirect is a console teaching the wrong shape.
function serviceRoot(layer) {
  const folder = layer.hosted ? "hosted/" : "";
  return `${location.origin}/rest/services/${folder}${encodeURIComponent(layer.name)}`;
}

/**
 * Where a layer lives: its service, its folder and its number.
 *
 * <b>Read from a listing, which is the fix to a mechanism that could not see a stopped
 * service.</b> Until 2026-08-17 this walked the services directory — `/rest/services`, then
 * every service document — to map a layer name to a path, because neither `/admin/layers` nor
 * `/content/layers` said where a layer was. **A stopped service answers 503 to that walk**, so
 * it fell out of the map, and everything built on the map lost it: its settings page drew from
 * nothing, `Save` refused with *"not in the services directory"*, and the service list could
 * not offer it a Start button.
 *
 * The proper fix was already recorded as a gap in the API rather than a bug here — ADR-020 §2,
 * *the listing should carry the service and the index* — and it is now what both listings do.
 * So this is a lookup over data already on screen: no requests, no cache to invalidate, and a
 * stopped layer is as findable as a running one.
 */
function placeOf(name) {
  // Studio's reader has no administrative listing, so `/content/layers` answers there. Server's
  // has both; `known` is preferred because it covers every layer rather than only this
  // reader's own.
  const listed = known.find(l => l.name === name);
  if (listed) {
    return {
      service: listed.folder ? `${listed.folder}/${listed.service}` : listed.service,
      bare: listed.service,
      folder: listed.folder || null,
      id: listed.layerIndex,
      url: listed.url,
    };
  }

  const own = content.get(name);
  if (!own) return null;

  // `/content/layers` gives the address rather than the parts, because that is what a
  // publisher's row needs. Splitting it back is safe: it was built from the same three fields.
  const cut = own.url.replace("/rest/services/", "").split("/FeatureServer/");
  const path = splitService(cut[0]);
  return {
    service: cut[0],
    bare: path.name,
    folder: path.folder || null,
    id: Number(cut[1] ?? 0),
    url: own.url,
  };
}

/**
 * The FeatureServer URL for a layer, resolved rather than guessed.
 *
 * Falls back to the old shape when the directory does not know the name, so a
 * layer that is stopped — and therefore absent from the directory — still gets a
 * URL to try instead of no answer at all.
 */
function layerUrl(name) {
  const place = placeOf(name);
  return place
    ? `${location.origin}${place.url}`
    : `${serviceRoot(layerNamed(name))}/FeatureServer/0`;
}

const tileKey = name => `${name} · tiles`;

// Tiles, through Esri's own VectorTileLayer rather than anything of ours.
//
// This is the only check that A-057 actually names: that the ST_AsMVT output
// is acceptable to a real client. Rendering a tile ourselves proved the
// geometry is right, which is a different and smaller claim — our decoder and
// our encoder could agree about a mistake, and a client that refuses the
// service is a service nobody can use no matter how correct the bytes are.
async function showTiles(name) {
  const { VectorTileLayer } = await loadEsri();
  const mapView = await ensureMap();

  // The tile service belongs to the service, not to the layer, so a member of a
  // multi-layer service draws all of its siblings' tiles too. That is what the
  // server offers and the legend says which layer asked for it.
  const place = placeOf(name);
  const root = place
    ? `${location.origin}/rest/services/${place.service}`
    : serviceRoot(layerNamed(name));

  const layer = new VectorTileLayer({
    url: `${root}/VectorTileServer`,
    title: `${name} (tiles)`,
  });

  clearMap();
  mapView.map.add(layer);
  shown.set(tileKey(name), { colour: TILE_COLOUR, layer, tiles: true });
  drawLegend();
  $("mapPanel").classList.add("on");

  try {
    // <b>The same clock as the view's, for the same reason.</b> A tile layer that
    // reads the service document and then gives up leaves this awaiting forever:
    // that is exactly what happened when the document's fullExtent and tileInfo
    // declared different references — the SDK fetched the metadata, the style and
    // the sprites, requested no tile, and settled nothing. Only the server log
    // showed it. D-49.
    await Promise.race([
      layer.when(),
      new Promise((_, reject) => setTimeout(
        () => reject(new Error(
          "The tile layer did not become ready within 15 seconds. It fetched the service "
          + "document and stopped, which usually means the document is inconsistent — check "
          + "that fullExtent and tileInfo agree about the spatial reference.")),
        15000)),
    ]);

    if (layer.fullExtent) await mapView.goTo(layer.fullExtent.expand(1.2));
  } catch (e) {
    hide(tileKey(name));
    toast(`${name} tiles: ${e.message || e}`);
  }
}

/**
 * Takes everything off the map.
 *
 * <b>Showing replaces rather than accumulates, on the owner's correction of
 * 2026-08-16.</b> Every Map click used to add another layer, so a few clicks
 * produced a stack of unrelated geometry in six colours and a legend nobody could
 * read — and looking at one layer meant reloading the page. The question a viewer
 * answers is "what does this layer look like", which has one subject.
 *
 * Comparing two layers is a real thing to want and it is not this. If it is added
 * it should be asked for deliberately, with a control that says so, rather than
 * arrived at by clicking twice.
 */
function clearMap() {
  for (const key of [...shown.keys()]) hide(key);
}

/**
 * Draws a layer, with **no renderer of our own**.
 *
 * <b>Passing no `renderer` is the change, and it is the point.</b> The SDK then reads
 * `drawingInfo` from the layer document — so what this console shows is what the server
 * told every client, and if the two ever disagree it is visible here first. Before
 * 2026-08-17 this file built its own symbol from a local palette, which meant the console
 * was the one viewer guaranteed *not* to show what anybody else saw.
 *
 * The document is passed in because the caller already fetched it to decide the geometry
 * type; asking for it again to read a colour would be a second request for a fact in hand.
 */
async function show(name, doc) {
  const { FeatureLayer } = await loadEsri();
  const mapView = await ensureMap();
  const colour = serverColour(doc);

  const layer = new FeatureLayer({
    url: layerUrl(name),
    title: name,
    outFields: ["*"],
    popupTemplate: { title: name, content: "{*}" },
  });

  clearMap();
  mapView.map.add(layer);
  shown.set(name, { colour, layer });
  drawLegend();
  $("mapPanel").classList.add("on");

  try {
    await layer.when();

    // Zoom to the layer just shown. fullExtent comes from the layer document,
    // which this server fills from PostGIS statistics — so it may be absent,
    // and an absent extent is said out loud rather than left as a map that
    // quietly did not move.
    if (layer.fullExtent) {
      await mapView.goTo(layer.fullExtent.expand(1.4));
    } else {
      toast(`${name} is shown, but its extent is unknown so the map did not move. `
        + "Run ANALYZE on the source table and reload.");
    }
  } catch (e) {
    // The SDK reports a refused request here, and the server's message is the
    // useful part — a stopped service, a layer not shared, a parameter refused.
    hide(name);
    toast(`${name}: ${e.message || e}`);
  }
}

function hide(name) {
  const entry = shown.get(name);
  if (!entry) return;
  if (view) view.map.remove(entry.layer);
  shown.delete(name);
  drawLegend();
  if (shown.size === 0) $("mapPanel").classList.remove("on");
}

// ------------------------------------------------------------------- services

// <b>The sort and filter state went with the table.</b> Server lists the services in a folder
// now (ADR-034 §5h), and the flat all-layers table this replaces owned the ordering, its ORDER
// map and a search across name, table, source and owner. That search is the one thing the new
// shape cannot do, and §5h records it rather than dropping it quietly.

/**
 * The administrative catalogue, which is what the layer editor is edited against.
 *
 * <b>It draws nothing since ADR-034.</b> The flat all-layers table it used to fill is gone:
 * Server lists services in a folder and layers appear when a service is opened (§5h). What is
 * still needed is `known` — the editor asks it whether a layer is hosted, who owns it, what
 * table is under it — so this keeps reading and stops rendering.
 *
 * It needs `admin:viewAllContent`, so it is only called on the Server surface. Studio reads
 * `/content/layers` instead, which is the listing §5f had to add.
 */
async function loadLayers() {
  const { layers } = await api("/admin/layers");
  known = layers;

  // The service list moves whenever the layer list does: publishing creates a service
  // and unpublishing the last layer empties one, and an emptied service that still shows
  // "1 layer" would offer a Delete that refuses.
  section("services", loadServices, "services");

  if (!editing) return;

  // The layer being edited stopped existing — deleted here, or by somebody else.
  // Sitting on a page for it would offer to save settings onto nothing.
  if (!layers.some(l => l.name === editing.name)) {
    selected = null;
    editing = null;
    location.hash = "#/services";
    return;
  }
  redrawLayerPage();
}

/**
 * Redraws the open editor after the listing moved, keeping what has been typed.
 *
 * <b>A refresh must not eat an unsaved figure.</b> Start, Stop, Map and Tiles all
 * re-read `/admin/layers`, and the editor's General page shows the state they
 * change — so it has to be redrawn. Redrawing it from the server alone would
 * silently discard a limit half-entered on another page, which is the same class
 * of quiet loss as a control that displays a value it never read.
 */
function redrawLayerPage() {
  const pending = editedValues();
  const { name, page } = editing;
  editing = null;                // so showLayer rebuilds rather than flipping pages
  showLayer(name, page, pending);
}

/**
 * What this reader owns and what is shared with them — Studio's screen, from
 * `GET /content/layers` (ADR-034 §5f).
 *
 * <b>It carries the address, so nothing here has to guess one.</b> The administrative listing
 * does not say which service holds a layer or at which index — D-45 — and the console resolves
 * that through the services directory. This listing answers it directly, so Studio needs no
 * `admin:` privilege to draw a map: the URL it draws from came with the row.
 */
/**
 * The scopes content arrives by, in the order somebody reads them.
 *
 * <b>Five plus one, and mutually exclusive.</b> The owner named four — *my own, from my groups, shared
 * in organization, public* — and the fifth is *Everything*, which is what an operator actually asks for
 * most of the time. The sixth appears only for an administrator who has something to see through it.
 *
 * <b>Keyed on ownership and sharing, never on `because`.</b> That value is precedence-ordered for the
 * audit trail — `Evaluate` returns the narrowest justification the reader needed, so a public service
 * you own comes back *Public* — and faceting on it inverts its meaning. The server sends `scope` for
 * exactly this and `because` beside it as the fact about who *else* can see the thing.
 */
const CONTENT_SCOPES = [
  ["all", "Everything"],
  ["mine", "Mine"],
  ["group", "From my groups"],
  ["organization", "Organization"],
  ["public", "Public"],

  // <b>Named for what it means to the reader, not for the enum.</b> `AdministrativeOverride` is an
  // identifier; *not shared with you* is what an operator needs to understand about the rows under it.
  // Shown only when it holds something — a tab reading `0` invites the click that proves it reads `0`.
  ["administrative", "Not shared with you"],
];

/** Which scope the content screen is showing. */
let contentScope = "all";

/** What is typed in its search box. */
let contentFilter = "";

/**
 * Everything this operator can see, with a picture of each.
 *
 * <b>Rebuilt 2026-08-18 on the owner's two corrections.</b> *"I also need to see the thumbnails in
 * studio content"*, and the four scopes. Three things were wrong beyond the missing picture and each
 * had to be fixed before a thumbnail could be added rather than because of it:
 *
 * - **It never paginated.** No `pageOf`, no `pagerFor`, at a stated scale of 100–1,000 services. At
 *   their 512 that is 512 rows and something like thirty thousand pixels of table — the same defect
 *   class as the 512-option select box, on the screen where the owner asked for the picture.
 * - **Two tables with duplicate headers**, which the scope strip replaces: the strip *is* the
 *   mine-versus-shared split, done properly and with the two scopes that split could not express.
 * - **The row put two buttons ahead of the name.** Put a 104px picture in front of that and the name
 *   is the fourth thing in the row. The Services screen already obeys the owner's brief — name
 *   strongest, one verb, the rest behind `⋯` — and this screen did not.
 */
async function loadMyContent() {
  const answer = await api("/content/items") || {};
  const items = answer.items || [];

  // `content` still keys by layer name for the map, which reads it by name when a row is drawn. The
  // per-layer listing stays the map's source; this screen is about items.
  const layers = await api("/content/layers") || {};

  content = new Map(
    [...(layers.mine || []), ...(layers.shared || []), ...(layers.notShared || [])]
      .map(e => [e.name, e]));

  const counts = answer.counts || {};
  const total = items.length;

  // <b>The administrative scope appears only when it holds something.</b> And its own tab rather than
  // folded into any other: a listing that quietly mixed other people's private content into
  // *organization* would misreport the sharing model to the one person who can change it.
  // <b>And when it is the scope you are looking at, whatever it holds.</b> `#/content/administrative`
  // renders its own note and its own rows, and the strip beside it showed no current tab at all —
  // which reads as a rendering fault rather than as *you are somewhere the strip does not list*.
  // Design review 2026-08-19.
  const scopes = CONTENT_SCOPES.filter(([key]) =>
    key === "all"
    || key !== "administrative"
    || (counts.administrative || 0) > 0
    || contentScope === "administrative");

  $("contentScopes").innerHTML = scopes.map(([key, label]) => {
    const n = key === "all" ? total : (counts[key] || 0);

    return `<a href="#/content/${key}"${key === contentScope ? ' aria-current="page"' : ""}
      >${label} <span class="count">${num(n)}</span></a>`;
  }).join("");

  const inScope = contentScope === "all"
    ? items
    : items.filter(i => i.scope === contentScope);

  const needle = contentFilter.trim().toLowerCase();

  const visible = needle
    ? inScope.filter(i => [i.name, i.kind, i.description, i.owner, i.folder]
        .some(v => (v || "").toLowerCase().includes(needle)))
    : inScope;

  $("contentFilter").hidden = inScope.length <= PAGE_SIZE && !needle;

  $("contentCount").textContent = inScope.length === 0
    ? ""
    : needle
      ? `showing ${num(visible.length)} of ${num(inScope.length)}`
      : `${num(inScope.length)} item${inScope.length === 1 ? "" : "s"}`;

  // <b>One line, and only where it says something the table does not.</b> The administrative scope
  // carries the sentence ADR-018 condition 3 is about, and it is only writable because the listing now
  // records the read — the promise was checked before it was made.
  // <b>A line for every scope, because two of the six had none and their empty state was ambiguous.</b>
  // Design review 2026-08-19: landing on *Public* with no rows gave no way to tell whether nothing is
  // public, or sharing is unfinished, or something is broken — while *From my groups* and *Not shared
  // with you* each explained themselves. A scope is a claim about how something reached you, and a
  // claim with no sentence is a heading.
  const SCOPE_NOTES = {
    administrative:
      `These are private to their owners. You can see them because you are an administrator, and every
       listing that includes them is recorded against your name.`,
    group:
      `Shared with you through a group you are in. What a group confers is fixed when it is made —
       reading, or editing what its members shared.`,
    organization:
      `Shared with everyone signed in to this server. Not public — a stranger with the address still
       gets nothing — and not through any group.`,
    public:
      `Readable by anyone with the address, signed in or not. <b>Anonymous view</b> shows what a
       stranger actually receives, which is the check worth making before believing this list.`,
    mine:
      `Published by you. Sharing one does not move it out of here — this is ownership, and the other
       sections are how it reached somebody else.`,
  };

  $("contentNote").innerHTML = SCOPE_NOTES[contentScope]
    ?? (contentScope === "all" && total === 0 ? h(answer.note || "Nothing to see yet.") : "");

  // <b>*New item*, which is what the button says.</b> It said *New layer* until 2026-08-19, when
  // ADR-034 §5j renamed the page action — so the one instruction this screen gave named a control that
  // is not on it. D-83's exact shape: the page's own instruction unfollowable.
  $("contentRows").innerHTML = total === 0
    ? `<tr><td colspan="7" class="empty">${h(answer.note || "Nothing here yet.")}
         <b>New item</b> publishes one.</td></tr>`
    : inScope.length === 0
      ? `<tr><td colspan="7" class="empty">Nothing arrived this way.
           ${contentScope === "mine"
             ? `<b>New item</b> publishes something of your own.`
             : `<b>Everything</b> shows all ${num(total)} you can see.`}</td></tr>`
      : visible.length === 0
        ? `<tr><td colspan="7" class="empty">Nothing matches <b>${h(contentFilter)}</b>. The search
             reads a service's name, kind, description, owner and folder.</td></tr>`
        : pageOf("contentRows", visible).map(i => {
          // The map is driven per layer, and a service's cover layer is the one this row draws and
          // shows. A multi-layer service is opened from its own page for the rest.
          const key = i.cover ? i.cover.layer : null;
          const isShown = key !== null && shown.has(key);
          const stopped = i.status === "stopped";

          // <b>The row opens the item, and the item lists its layers — owner correction, ADR-034
          // §5k.</b> This carried a shortcut for a few hours: a single-layer service's row went
          // straight to that layer's editor, because D-98 had found the editor unreachable by any
          // click and that was the repair I chose. The owner's screenshot of the reference says the
          // row opens the **item page** and each layer is entered from its list — which Overview now
          // is, so the shortcut was solving a problem the layer list solves, and disagreeing with the
          // reference while doing it.
          //
          // The route D-98 asked for is intact: the name goes to the service page, and Overview's own
          // rows go to each layer.
          return `
          <tr>
            <!--
              <b>The picture is a target, and it looked like one already.</b> Handoff 2026-09-04.
              A reader scanning this list is scanning the thumbnails; clicking one did nothing,
              which is the worst state for something that reads as pressable. It opens the item
              page's Visualization tab with that layer drawn — because what somebody wants from a
              picture is a bigger picture, not a settings page.

              <b>The name still goes to Overview.</b> Two targets in one row are only worth having
              if they lead somewhere different.
            -->
            <td class="thumbcell">${i.cover
              ? `<a class="thumblink" href="${h(visHref(i.cover.layer || "", "features")
                   || `#/service/${i.name.split("/").map(encodeURIComponent).join("/")}`)}"
                   title="Draw ${h(i.cover.layer || i.name)} on the map"
                   ><img class="thumb" alt="" loading="lazy"
                   data-thumb="${h(thumbnailFor(i.cover.url))}"></a>`
              : `<div class="thumb empty" title="This service has no layer to draw, so there is no map to show."></div>`}</td>
            <td class="name"><a href="#/service/${
              i.name.split("/").map(encodeURIComponent).join("/")}">${h(i.name)}</a>
              <div class="rowmeta">${h(i.kind)} · ${num(i.layers)}
                layer${i.layers === 1 ? "" : "s"}${i.description
                  ? ` · ${h(i.description)}` : ""}${i.owner && i.scope !== "mine"
                  ? ` · ${h(i.owner)}` : ""}${(i.throughGroups || []).length > 0
                  ? ` · via ${i.throughGroups.map(h).join(", ")}` : ""}</div></td>
            <td class="val">${i.folder ? h(i.folder) : "root"}</td>
            <td>${pill(i.status)}</td>

            <!-- ADR-034 5l: the pill is the control, because it is already the thing that says who
                 can reach this and the reader is going to press it either way. -->
            <td><button class="pillbtn" data-share="${h(i.name)}"
                  title="Set who can reach this">${pill(i.sharing)}</button>${
              // `because` only where the scope pill does not already say it. On this server the two
              // used to read `public` and `Public` three inches apart, which is one fact twice.
              i.because === "administrativeoverride"
                ? ` <span class="val">by override</span>` : ""}</td>
            <td class="val">${day(i.updated)}</td>
            <td style="text-align:right">${key === null
              ? ""
              : `${stopped
                  ? ""
                  : `<button class="tiny ${isShown ? "on" : ""}" data-show="${h(key)}"
                       >${isShown ? "Hide" : "Map"}</button>`}
                <details class="menu">
                  <summary title="More" aria-label="More actions">⋯</summary>
                  <div class="sheet">
                    ${(content.get(key) || {}).hosted && !stopped
                      ? `<button data-tiles="${h(key)}">${shown.has(tileKey(key))
                          ? "Hide its tiles" : "Draw its tiles"}</button>` : ""}
                    <a href="${h(i.cover.url)}?f=json" target="_blank" rel="noreferrer"
                      >The layer document</a>
                    <div class="note">${stopped
                      ? `Stopped, so there is nothing to draw — a stopped service answers 503.`
                      : `The address is what any ArcGIS client would use.`}</div>
                  </div>
                </details>`}</td>
          </tr>`;
        }).join("");

  $("contentRowsPager").innerHTML = pagerFor("contentRows", visible.length);
}



/**
 * One service, and the layers it holds.
 *
 * <b>Read from the service document, not from a new admin route.</b> The document is what every
 * ArcGIS client reads to find a service's layers, so this screen and every client agree by
 * construction — and a stopped service refusing it is shown in place, because that refusal is
 * expected rather than a fault.
 */
/**
 * The service whose page is open, held rather than read back out of the breadcrumb.
 *
 * <b>This is the fix for the worst defect the 2026-08-19 design review found.</b> The settings tabs
 * re-derived the folder by running `splitService` over the **text of the breadcrumb's `<b>`** — and that
 * element holds only the bare name, because the breadcrumb is built as
 * `Services › folder › <b>name</b>`. So `folder` came back null for every service not at the site root,
 * `drawServiceSettings` refetched `/admin/services/{name}/capabilities?folder=`, the server answered
 * 404, `loadServiceCapabilities` threw before setting a single field, and the freshly drawn checkboxes
 * stayed at their unchecked default under a toast saying *No service at the root*.
 *
 * <b>And the unchecked state is what Save reads.</b> Open a foldered service, look at Limits, come back
 * to Capabilities, press Save — and Feature access and Vector tiles are switched off on a live service.
 * Measured on `hosted/look_EarlyAlert`, which is public and has four layers. The review did not press
 * Save, and did not need to: `saveServiceSettings` reads the same boxes the reader is looking at.
 *
 * <b>The bug class is reading a value back out of rendered text.</b> The name was in hand in
 * `showService` and was thrown away, then reconstructed from a string written for a person to read.
 * `editing` already holds the layer being edited for exactly this reason; this is its counterpart.
 */
let serviceOpen = null;

/**
 * A service's four tabs, and which of them this surface can show.
 *
 * <b>[ADR-034](../../docs/adr/ADR-034-server-and-studio.md) §5k, from the owner's screenshots.</b>
 * Overview is the list of layers in the service — removed on 2026-08-18 and asked for again on
 * 2026-08-19, which §5k records as a reversal rather than as a new idea. Settings holds what was already
 * on this page.
 *
 * <b>Visualization is not in this list because it is not built.</b> §5k says it *absorbs* the map and
 * tiles screens rather than adding a third, so it appears when that move happens — ADR-034's own rule
 * is that a control is not drawn for a feature that does not exist, and a tab that opens onto nothing is
 * the clearest possible breach of it.
 */
const SERVICE_TABS = [
  ["overview", "Overview"],
  ["data", "Data"],
  ["visualization", "Visualization"],

  // <b>Its own tab, by owner decision 2026-09-03.</b> How a service is drawn was reachable only
  // by opening one of its layers and noticing a tab there — *arayüzde yok düğmesi* — and the
  // question is asked of the service, not of a layer somebody has to pick first.
  ["symbology", "Symbology"],

  ["settings", "Settings"],
];

/**
 * Whatever followed a `?` in the address, parsed.
 *
 * <b>Read by the service page, written by whoever navigated there.</b> Empty on every other screen, and
 * cleared on each route so a stale `layer=` cannot survive into an unrelated page.
 */
let hashQuery = new URLSearchParams();

/** Which tab the service page is showing. */
let serviceTab = "overview";

/**
 * The tab the address asked for, held until it becomes available.
 *
 * <b>Two draws, and the first one cannot honour it.</b> `showService` draws the strip before the
 * FeatureServer document arrives so the page is usable while it is in flight — and Visualization and Data
 * only exist once the layers are known, so a `?tab=visualization` asked for at the first draw is
 * filtered out and lost. Measured 2026-08-19: the address was right and the page opened on Overview.
 * Kept here so the second draw can grant it.
 */
let serviceTabWanted = null;

/** Which layer Visualization is drawing, by its index in the service, and how. */
let visLayerIndex = null;
let visMode = "features";

/**
 * Whether the open service is a system service — one with no layers at all.
 *
 * <b>It decides which tabs exist, and the alternative was worse.</b> A GeometryServer has nothing to
 * list and no rows to read, so Overview and Data would be two tabs that open onto a sentence explaining
 * why they are empty. ADR-034's rule is that a control is not drawn for a feature that does not exist,
 * and *this service has no data* is not a feature.
 */
let serviceIsSystem = false;

async function showService(qualified) {
  editing = null;
  showView("view-service", "services");

  const { folder, name } = splitService(qualified);

  serviceOpen = { qualified, folder, name };

  // <b>What the address asked for, if it asked.</b> The three redirected map controls land here with
  // `?tab=visualization&layer=&mode=`; pressing a tab by hand sets `serviceTab` and leaves this null.
  const askedTab = hashQuery.get("tab");

  serviceTabWanted = SERVICE_TABS.some(([key]) => key === askedTab) ? askedTab : null;

  // <b>Every service opens on Overview unless the address asks otherwise.</b> The tab used to
  // be remembered across services, so leaving one on Settings and opening the next put somebody
  // on a delete button they had not asked for. A tab is a place inside one service, not a
  // preference that follows the reader from service to service.
  serviceTab = serviceTabWanted ?? "overview";

  // <b>Consumed here and never read again, which the first version got wrong.</b> `drawServiceVis` was
  // re-reading `?mode=` on every redraw, so pressing *Tiles* set the mode and the next draw put it back
  // to what the address said. Measured 2026-08-19. An address is an instruction on arrival, not a
  // standing one.
  const askedLayer = hashQuery.get("layer");
  const askedMode = hashQuery.get("mode");

  // <b>One `?layer=` and two tabs read it.</b> It was Visualization's alone, so a Data link
  // carrying a layer arrived at a picker that had already chosen the first one. Both are
  // consumed on arrival and never again — an address is an instruction on arrival, not a
  // standing one.
  if (askedLayer !== null) visLayerIndex = askedLayer;
  if (askedLayer !== null) dataLayerIndex = askedLayer;
  if (askedMode === "tiles" || askedMode === "features") visMode = askedMode;

  // <b>The first crumb is the screen this surface came from.</b> It said *Services* on both,
  // and `#/services` is a Server screen — so on Studio, where the reader arrived from *My
  // content*, the way back led nowhere. A breadcrumb that does not go back is decoration.
  const backTo = surfaceOfPath() === "studio"
    ? { hash: "#/content", label: "My content" }
    : { hash: `#/services${folder ? "/" + encodeURIComponent(folder) : ""}`, label: "Services" };

  $("serviceCrumb").innerHTML =
    `<a href="${h(backTo.hash)}">${h(backTo.label)}</a>
     › ${folder ? h(folder) : "root"} › <b>${h(name)}</b>`;
  $("serviceFacts").textContent = "";

  // <b>Named before anything is fetched.</b> The three requests below take a moment on a cold
  // service, and a page that says nothing while they are in flight is a page a reader cannot
  // tell from one that failed. The name is the one fact the address already carries.
  $("serviceTitle").textContent = name;
  $("serviceSub").textContent = folder ? `in ${folder}` : "in the site root";

  // <b>A service with no layers is a different screen.</b> There is nothing to list and there are
  // bounds to set, and asking the server which kind this is beats guessing from the shape of a
  // document that 404s for the other kind. `/limits` exists only for a system service, so its
  // answer is the question.
  const limits = await loadServiceLimits(name, folder);

  if (limits) {
    $("serviceFacts").textContent = `${limits.kind} · no layers`;

    // <b>Only Settings, and the wrapper has to be revealed for it.</b> A system service has nothing to
    // list and no rows to read; its Limits page is the whole page. Without this the four-tab wrapper
    // stayed hidden and the Geometry service's page rendered blank — which is the shape of defect this
    // repository has shipped three times, a control that exists and cannot be seen.
    //
    // <b>And the layer list is cleared, not merely hidden.</b> Measured 2026-08-19: going from
    // `hosted/look_EarlyAlert` to `Utilities/Geometry` left four rows of the previous service's layers
    // sitting in Overview, so pressing that tab showed somebody else's layers under this service's name.
    // Setting the array empty was not enough — the table is drawn from it and had to be redrawn.
    serviceIsSystem = true;
    serviceTab = "settings";

    // <b>And the previous service's settings panel is hidden, which is the worst finding of the
    // 2026-08-19 design review.</b> `drawServiceSettings` is what redraws `#serviceEdit` and stamps the
    // Save button with the service it belongs to, and this branch returns before reaching it — so the
    // panel kept the last feature service's Capabilities boxes **and its Save button, addressed to that
    // service**. Measured: opening `hosted/look_EarlyAlert`, then `Utilities/Geometry`, left Geometry's
    // Settings tab showing *Feature access* and *Vector tiles* above a Save that would `PUT` to
    // `hosted/look_EarlyAlert`. An operator clearing what they believed were the geometry service's
    // capabilities would have switched off a live public service's instead.
    //
    // <b>Latent before this page had tabs and reachable after.</b> The branch always left the panel
    // stale; what changed is that the tab strip made it visible. Which is the more dangerous half of a
    // bug like this: it was there and nobody could see it.
    $("serviceEdit").hidden = true;
    $("serviceNav").innerHTML = "";
    $("servicePagesBody").innerHTML = "";

    drawServiceLayers([], qualified);

    return;
  }


  serviceIsSystem = false;
  serviceDoc = null;

  // <b>Cleared, not left.</b> The 2026-08-19 review's worst finding on this screen was a panel
  // that kept the previous service's figures; these two are read from a document that a system
  // or image service does not have, so they are emptied before the request rather than after it
  // fails.
  for (const id of ["serviceOps", "serviceSpend"]) {
    if ($(id)) $(id).innerHTML = "";
  }

  // <b>The service's own settings, on the service.</b> Rendered before the layer list, because they
  // are what this page is now for: the list below says what is inside, and these say what the
  // container offers. Same `.setting` rows and `h4` groups as everywhere else.
  drawServiceSettings(name, folder);
  drawServiceDelete();
  drawServiceTabs();

  /*
    <b>An image service has no FeatureServer face, so asking for one is a 404 — and worse
    than a 404 in the network log, a red toast on the page saying no such layer is visible
    to you.</b>

    <b>This read the kind out of `SERVICE_ROWS`, and that was a race rather than a
    guard.</b> That array is filled as a side effect of having rendered a services list,
    so it is populated when a reader clicks through from the list and empty when they open
    `#/service/hosted/tile_gray` directly — from a bookmark, a shared link, or a page
    reload. Empty means `kind` is undefined, undefined is not `"ImageServer"`, and the
    FeatureServer branch runs for a coverage. Reproduced three times out of three on a
    fresh session, and a deliberate delay made it pass, which is what makes it a race:
    waiting 1000 ms still failed and 1500 ms succeeded.

    <b>The server already answers this.</b> `/admin/services/{name}/capabilities` carries
    `kind`, added the day the coverage panel was — it was simply not the thing being read
    here. Asking it costs one request that the settings panel below makes anyway, and it
    cannot be empty because of what a reader clicked on earlier.

    <b>This is the third time a control on this screen has depended on state that exists
    only if you arrived a particular way.</b> The rule the repeat argues for: a page
    decides what to draw from what the server says, never from what a previous page left
    behind.
  */
  /*
    <b>Inside its own try, because a service can be gone by the time this asks.</b> The
    line below it — the FeatureServer probe — has always been inside a try for exactly
    that reason, and putting a second request in front of it without one produced an
    unhandled rejection the moment a test's temporary import service was removed while
    its page was open. Caught by the console suite on the first full run after the
    change, which is the run that exists for this.

    <b>A failure here means *carry on*, not *stop*.</b> If the kind cannot be read, the
    path below runs and fails in the way it already knew how to fail — with a message on
    the page — rather than with a rejection nobody handles.
  */
  let kind = null;

  try {
    const settings = await api(
      `/admin/services/${encodeURIComponent(name)}/capabilities`
        + (folder ? `?folder=${encodeURIComponent(folder)}` : ""));

    kind = settings ? settings.kind : null;
  } catch {
    kind = null;
  }

  if (kind === "ImageServer") {
    /*
      <b>Not a bare return any more — [D-200](../../docs/architecture-debt.md).</b> Everything
      an image service needs *below* this line is drawn: `loadServiceCapabilities` fills the
      coverage panel with its size, bands, pixel type, reference and formats, and it was doing
      that all along. What returning here skipped was the two things every other service on this
      screen has — the subtitle under its name, which stayed empty, and Studio's address column,
      which was not drawn at all. Measured on `hosted/ci_imagery`: the coverage facts read
      *256 × 192 pixels, 1 band, U8, EPSG:4326* while `#serviceFacts` was an empty string and
      `#svcUrl` was absent from the document.

      <b>It reads as a page that failed rather than a page that is smaller.</b> An empty subtitle
      beside a filled panel, and no address where every other service has one, is the shape a
      reviewer reported as *never populates at all*.
    */
    $("serviceFacts").textContent = "image service · a coverage rather than layers";

    await drawServiceDetails(qualified, kind);

    return;
  }

  try {
    const doc = await api(
      `/rest/services/${qualified.split("/").map(encodeURIComponent).join("/")}/FeatureServer?f=json`);

    const layers = doc.layers || [];

    // <b>*Operations*, not *capabilities*, and the word was doing two jobs.</b> This line said *no
    // capabilities* directly above a panel headed **Capabilities** with two boxes ticked — both true,
    // because the ArcGIS `capabilities` string is Query/Create/Update/Delete/Extract while the panel
    // above is which faces the service offers. An operator scanning does not know that, and should not
    // have to. Design review 2026-08-19.
    //
    // <b>And the layer count is the document's, which is not the list screen's.</b> The services list
    // says *3 layers, 1 group* where this says 4: the FeatureServer `layers` array counts the group and
    // the layer nested under it. Both are right in different units, so this one names its unit.
    // <b>Three facts in one mono line became two cards and three words.</b> The line said how
    // many entries the document has, how many rows a request may read and which operations a
    // client may call — one shape for three different questions, in 13-pixel monospace beside a
    // breadcrumb. The two a publisher opens this page for are panels with headings now; the
    // entry count is a note on the Layers fact, which is the row it qualifies; and this strip
    // carries what the handoff asks of it — sharing, state, owner — written by
    // `drawServiceDetails`, which is where the item that holds all three arrives.
    serviceEntries = layers.length;
    serviceDoc = doc;
    drawServiceBounds(doc);
    drawServiceLayers(layers, qualified);
  } catch (e) {
    $("serviceFacts").textContent = "";
    toast(`${qualified}: ${e.message || e}`);
  }
}

/** The FeatureServer document the open service last answered with, or null. */
let serviceDoc = null;

/** How many entries that document listed, which is not the same as how many layers there are. */
let serviceEntries = 0;

/**
 * The two Overview cards: what a client may do, and what one request may spend.
 *
 * <b>Both are read from the service document.</b> It is what a client gets, so it is what the
 * answer to *may a client do this* has to come from — the settings form beside it says what
 * somebody asked for, and a service where the two disagree is exactly the case a reader is
 * looking at this page to find.
 *
 * @param {object} doc the FeatureServer document
 */
function drawServiceBounds(doc) {
  const ops = $("serviceOps");
  const spend = $("serviceSpend");

  if (!ops || !spend || !doc) return;

  // <b>Every operation the face defines, not only the ones granted.</b> A list of what is on
  // says nothing about what is off, and *why can this client not edit* is the question. The five
  // are ArcGIS's own set for a FeatureServer; a name the server sends that is not among them is
  // added rather than dropped.
  const granted = String(doc.capabilities || "")
    .split(",").map(one => one.trim()).filter(Boolean);

  const known = ["Query", "Create", "Update", "Delete", "Extract"];
  const all = [...known, ...granted.filter(one => !known.includes(one))];

  ops.innerHTML = `
    <h4>What a client may do</h4>
    <div class="pills">${all.map(one => {
      const on = granted.includes(one);

      return `<span class="pill ${on ? "p-on" : "p-off"}"
        title="${on ? "Offered" : "Not offered — a client asking for it is refused"}"
        >${h(one)}</span>`;
    }).join("")}</div>
    <p class="hint">Set on the service, not per layer — <a href="#" data-service-tab="settings"
      >Settings</a>. A greyed operation is one the service does not offer; a client asking for it
      is refused with the reason.</p>`;

  const rows = [
    ["Rows per request", doc.maxRecordCount != null ? num(doc.maxRecordCount) : null],
    ["Query formats", doc.supportedQueryFormats ? h(String(doc.supportedQueryFormats)) : null],
    // <b>No thousands separator on an identifier.</b> `num()` made Web Mercator read as
    // *EPSG:3,857*, which is not a code anybody can paste anywhere.
    ["Spatial reference", doc.spatialReference
      ? `EPSG:${h(String(doc.spatialReference.latestWkid || doc.spatialReference.wkid))}`
      : null],
    ["Units", h(String(doc.units || "").replace(/^esri/, "").toLowerCase()) || null],
    ["Data", doc.hasStaticData ? "static" : "editable"],
  ].filter(([, value]) => value !== null && value !== "");

  spend.innerHTML = `
    <h4>What one request may spend</h4>
    <dl class="facts2">${rows.map(([label, value]) =>
      `<dt>${h(label)}</dt><dd>${value}</dd>`).join("")}</dl>
    <p class="hint">Read from the service document — this is what a client is told, whatever a
      form elsewhere says.</p>`;
}

/**
 * The tab strip, and which panel it reveals.
 *
 * <b>Studio's, and only Studio's — owner correction, 2026-08-19.</b> Every screen the owner sent is an
 * ArcGIS Online **item page**: *"sana verdiğim ekranlar studio'dan. sen gidip server'ı
 * değiştiriyorsun."* I built the four tabs on the service page without noticing that page renders on
 * both surfaces, so the structure landed on Server — which is the administrative surface and has never
 * been what those screenshots showed.
 *
 * <b>So Server goes back to what it was:</b> its settings pages, drawn directly, with no strip, no
 * Overview, no Data and no delete panel. `#serviceSettings` is revealed as a plain container there
 * rather than as a tab's contents, because `#serviceLimits` and `#serviceEdit` were moved inside it and
 * hiding the wrapper would hide them too.
 *
 * <b>Which surface owns which settings page is unchanged</b> — §5c, `SERVICE_PAGES`. Studio's Settings
 * tab holds Sharing and the delete panel; Server's page holds Capabilities and Limits.
 */
function drawServiceTabs() {
  const strip = $("serviceTabs");
  if (!strip) return;

  if (surfaceOfPath() !== "studio") {
    strip.innerHTML = "";
    strip.hidden = true;

    // <b>The head goes with them.</b> Server's service page is its settings pages and nothing
    // else — no Overview, no tabs — so a title row with an empty tab strip beside it would be a
    // heading for a screen that is not there.
    if ($("servicePagehead")) $("servicePagehead").hidden = true;

    for (const id of ["serviceOverview", "serviceData", "serviceDanger"]) {
      const panel = $(id);
      if (panel) panel.hidden = true;
    }

    // The wrapper is a container on this surface, not a tab.
    $("serviceSettings").hidden = false;

    return;
  }

  strip.hidden = false;

  if ($("servicePagehead")) $("servicePagehead").hidden = false;

  const mine = SERVICE_TABS.filter(([key]) => {
    // A system service is settings and nothing else.
    if (serviceIsSystem) return key === "settings";

    // Data needs a layer to read and Visualization needs one to draw. Overview stays either way:
    // *this service holds no layers* is a fact about the service and belongs on the page that
    // describes it.
    // <b>Symbology follows the same rule as the other two, which it did not.</b> ADR-034's
    // *a control is not drawn for a feature that does not exist* governed Data and
    // Visualization and the new tab was left out, so a service with nothing drawable kept a tab
    // whose only content was a sentence saying there was nothing to draw. Two tabs under one
    // named rule applying it differently is the plainest way to look like two products.
    if (key === "data" || key === "visualization" || key === "symbology") {
      return serviceLayers.some(l => !(l.type || "").toLowerCase().includes("group"));
    }

    return key !== "settings"
      || servicePagesOf(surfaceOfPath()).length > 0
      || $("serviceLimits").hidden === false;
  });

  // The address's request wins the moment its tab becomes available, which is the second draw.
  if (serviceTabWanted && mine.some(([key]) => key === serviceTabWanted)) {
    serviceTab = serviceTabWanted;
  }

  if (!mine.some(([key]) => key === serviceTab)) serviceTab = mine[0]?.[0] ?? "overview";

  // <b>Symbology is a link out, not a tab of this page.</b> Handoff revision 2026-09-04: the
  // tab used to open a list whose every row was one *Edit* link — an indirection with nothing in
  // it, and at four layers still a page you pass through rather than work in. It opens the
  // editor now, and the layer is chosen there, in the column where everything else about
  // appearance is chosen.
  const firstDrawable = serviceLayers.find(
    l => !(l.type || "").toLowerCase().includes("group"));

  strip.innerHTML = mine.map(([key, label]) => key === "symbology"
    ? `<a href="#/layer/${encodeURIComponent(firstDrawable ? firstDrawable.name || "" : "")
        }/symbology" title="Edit how this service is drawn, layer by layer">${label}</a>`
    : `<a href="#" data-service-tab="${key}"${key === serviceTab ? ' aria-current="page"' : ""}
      >${label}${key === "overview" && serviceLayers.length
        ? ` <span class="count">${num(serviceLayers.length)}</span>` : ""}</a>`).join("");

  // <b>And an address that asks for it goes the same way.</b> `?tab=symbology` is a link people
  // already have; landing them on a tab that no longer draws anything would be the shape of
  // defect this console records four times over.
  if (serviceTab === "symbology") {
    if (firstDrawable) {
      location.hash = `#/layer/${encodeURIComponent(firstDrawable.name || "")}/symbology`;

      return;
    }

    serviceTab = mine[0]?.[0] ?? "overview";
  }

  showServiceTab(serviceTab);
}

/** Reveals one tab's panels and hides the others. */
function showServiceTab(which) {
  if (surfaceOfPath() !== "studio") return;

  serviceTab = which;

  for (const [key, id] of [["overview", "serviceOverview"], ["data", "serviceData"],
                           ["visualization", "serviceVis"],
                           ["settings", "serviceSettings"]]) {
    const panel = $(id);
    if (panel) panel.hidden = key !== which;
  }

  // <b>The head bar's Save belongs to the Limits page and to nothing else.</b> Leaving it on Overview
  // would offer to save a list.
  for (const id of ["limSave", "limClear"]) {
    const button = $(id);
    if (button && which !== "settings") button.hidden = true;
  }

  for (const link of $("serviceTabs").querySelectorAll("a")) {
    if (link.dataset.serviceTab === which) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }

  if (which === "data") drawServiceData();
  if (which === "visualization") drawServiceVis();
}

/**
 * Says whether this service carries a style override, on the page.
 *
 * <b>A toast cannot carry this.</b> Which document the tile face serves is the whole subject of
 * the panel it sits in; seven seconds in a corner is where a fact goes when nobody has decided
 * where it belongs.
 *
 * @param {boolean} stored whether a document is stored
 * @param {string|null} note what the server said, when it said anything
 */
function styleState(stored, note) {
  const line = $("styleState");

  if (!line) return;

  line.innerHTML = stored
    ? `<b>Stored.</b> The tile face serves this document instead of the composition.`
    : `<b>Not stored.</b> The tile face composes a style from each layer's own symbology.`;

  if (note) line.title = note;
}

/*
  <b>`drawServiceSymbology` is gone, and what it did is in two places now.</b> Handoff revision
  2026-09-04 removed the service page's Symbology tab: it drew one row per layer whose only
  control was an *Edit* link, which is an indirection with nothing in it. The list it drew is
  the symbology editor's own left column (`drawSymStrip` and `fillSymLayerStates`), and the
  service style override it carried is at the foot of that column with the same ids and the
  same endpoint. The stamping moved with the control — see `drawSymStrip`.
*/

/**
 * How many classes a stored renderer holds, whatever family it is.
 *
 * <b>Its own function because `symClasses` reads the editor's model.</b> That one answers *the
 * document the form is holding*; this one answers *this document*, and a list screen has no form
 * open. Two callers reading one global is how the editor's state ends up on a page that is not
 * the editor.
 *
 * @param {object|null} cim the stored document
 * @returns {number} the number of classes, or 0 for a renderer that has none
 */
function classCountOf(cim) {
  if (!cim || typeof cim !== "object") return 0;

  if (cim.type === "CIMUniqueValueRenderer") {
    return (cim.groups || []).reduce((n, g) => n + (g.classes || []).length, 0);
  }

  if (cim.type === "CIMClassBreaksRenderer") return (cim.breaks || []).length;

  return 0;
}

/**
 * The symbol layers of the first thing a renderer draws, read without touching the editor.
 *
 * <b>A read-only twin of `symSymbolOf`, and the twin is on purpose.</b> That one fills in the
 * missing parts of the object it is given, because the editor is about to write into them; a
 * list screen must not edit a document it is only showing.
 *
 * @param {object|null} cim the stored renderer
 * @returns {Array} the symbol layers, possibly empty
 */
function firstSymbolLayers(cim) {
  if (!cim || typeof cim !== "object") return [];

  const holder = cim.type === "CIMUniqueValueRenderer"
    ? ((cim.groups || [])[0] || {}).classes?.[0]
    : cim.type === "CIMClassBreaksRenderer" ? (cim.breaks || [])[0] : cim;

  return ((holder || {}).symbol || {}).symbol?.symbolLayers || [];
}

/**
 * Fills each Overview row's symbology state and its geometry swatch.
 *
 * <b>Two facts from one request.</b> *Authored, and into how many classes* is what a publisher
 * scanning a service wants; the swatch beside the name is the same answer for somebody who is
 * not reading. Neither was on this page, and both are in a document the row would otherwise
 * fetch twice to get separately.
 *
 * @param {Array} layers the drawable layers, in the order they are listed
 */
async function fillLayerSymbologyStates(layers) {
  for (const one of layers) {
    const name = one.name || "";
    const says = document.querySelector(`[data-symstate="${CSS.escape(name)}"]`);
    const swatch = document.querySelector(`[data-geoswatch="${CSS.escape(name)}"]`);

    if (!says) continue;

    try {
      const r = await api(`/admin/layers/${encodeURIComponent(name)}/symbology`);
      const classes = classCountOf(r.symbology);

      says.textContent = r.stored
        ? `Authored${classes > 0 ? ` · ${num(classes)} classes` : ""}`
        : "Generated · version 0";

      says.classList.toggle("authored", !!r.stored);

      if (swatch) {
        const paint = symLayerColour(symThematicLayer(firstSymbolLayers(r.symbology)));

        swatch.className = `geoswatch ${symSwatchShape(r.geometry || "")}`;
        swatch.style.setProperty("--sw", symCimHex(paint && paint.color));
      }
    } catch {
      // <b>Silent, and the row keeps its name.</b> A layer whose symbology cannot be read is
      // still a layer in this service; putting the request's error where a two-word state
      // belongs would make one failed request look like a broken list.
      says.textContent = "";
    }
  }
}

/** The layers in the open service, from its own FeatureServer document. */
let serviceLayers = [];

/**
 * Overview's list of what is in the service.
 *
 * <b>And it is the route to a layer's own page from Server, which nothing had.</b> D-98: the editor was
 * reachable only by typing an address with a bare layer name that no screen displayed. A row here links
 * to it by name, so the five settings pages behind it have a way in again.
 *
 * <b>A group layer is listed and is not a link.</b> It holds no data and has no settings of its own, so
 * a row that offered a page would offer an empty one — the same reasoning that keeps a service's
 * attachment tables visible but unpublishable on the geodatabase screen.
 */
function drawServiceLayers(layers, qualified) {
  serviceLayers = layers || [];

  drawServiceDetails(qualified);

  const box = $("serviceLayerRows");
  if (!box) return;

  // <b>Ordered here rather than trusted to arrive ordered.</b> A group's children are its own rows in the
  // service document and today they happen to follow it; the review pointed out that the nesting reads
  // correctly by accident. `parentLayerId` is in the document and was being ignored.
  const top = serviceLayers.filter(l => (l.parentLayerId ?? -1) < 0);
  const under = id => serviceLayers.filter(l => (l.parentLayerId ?? -1) === id);

  const ordered = [];

  for (const one of top) {
    ordered.push([one, false]);

    for (const child of under(one.id)) ordered.push([child, true]);
  }

  // Anything the walk did not reach — a child whose parent is not in the document — is listed rather
  // than dropped. A layer this page cannot place is still a layer somebody has.
  for (const one of serviceLayers) {
    if (!ordered.some(([had]) => had === one)) ordered.push([one, false]);
  }

  box.innerHTML = ordered.length === 0
    ? `<tr><td colspan="2" class="empty">This service holds no layers. A service can exist before its
         layers do, which is the order you need when the structure matters.</td></tr>`
    : ordered.map(([layer, nested]) => {
      const group = (layer.type || "").toLowerCase().includes("group");
      const geometry = GEOMETRY_NAMES[layer.geometryType]
        || (layer.geometryType || "").replace(/^esriGeometry/, "")
        || null;

      const children = group ? under(layer.id).length : 0;

      const said = [
        group
          ? `group layer${children ? ` · ${num(children)} layer${children === 1 ? "" : "s"}` : ""}`
          : `${geometry ? `${geometry} layer` : "feature layer"}`,
        `id ${num(layer.id ?? 0)}`,
      ].join(" · ");

      // <b>Five columns, and it was two.</b> Handoff 2026-09-04. The id is what every address
      // and every error message uses and the row did not carry it; the swatch says what the
      // layer is before the words do; the state answers *has anybody styled this* without
      // opening it; and the three ways on were two links run together with no space between
      // them, which measured as one 96-pixel target holding two.
      const at = `#/layer/${encodeURIComponent(layer.name || "")}`;

      return `<tr>
        <td class="lid">${group ? "" : num(layer.id ?? 0)}</td>
        <td class="lsw">${group
          ? `<span class="rowicon">${icon("folder")}</span>`
          : `<span class="geoswatch" data-geoswatch="${h(layer.name || "")}"></span>`}</td>
        <td class="name${nested ? " nested" : ""}">${group
            ? h(layer.name || "")
            : `<a href="${at}">${h(layer.name || "")}</a>`}
          <div class="rowmeta">${h(said)}</div></td>
        <td class="lstate"><span class="rowmeta" data-symstate="${h(layer.name || "")}">${
          group ? "" : "reading…"}</span></td>
        <!--
          <b>Each of the three opens with *this* layer, and it took a revision to say so.</b>
          Handoff 2026-09-04: Data went to the Data tab and let it choose a layer for itself, so
          on a four-layer service every row's Data button opened the same one. It carries the id
          now, the way Symbology and Map already did.

          <b>A group holds no geometry</b>, so it has no rows to read, nothing to draw and no
          symbology of its own. Three disabled buttons would be three controls that fail on
          press; the sentence says where the answer is instead.
        -->
        <td class="acts">${group
          ? `<span class="rowmeta">its children carry the symbology</span>`
          : `<a class="tiny" href="#/service/${
              qualified.split("/").map(encodeURIComponent).join("/")}?tab=data&layer=${
              num(layer.id ?? 0)}" title="This layer's rows and its fields">Data</a>
        <a class="tiny" href="${at}/symbology"
          title="How this layer is drawn">Symbology</a>
        <a class="tiny" href="${h(visHref(layer.name || "") || `${at}`)}"
          title="Draw this layer on the map">Map</a>`}</td>
      </tr>`;
    }).join("");

  // <b>One request a layer, in order, and only for the layers that draw.</b> The same rule the
  // Symbology tab follows: a service of thirty layers must not open thirty requests as the first
  // thing this screen does.
  fillLayerSymbologyStates(ordered.filter(([one]) =>
    !(one.type || "").toLowerCase().includes("group")).map(([one]) => one));

  // <b>Redrawn here, because the strip is built before the document arrives.</b> `showService` draws the
  // tabs so the page is usable while the FeatureServer document is in flight; the count on Overview and
  // the sentence under Delete both depend on the answer, so both are drawn again when it lands.
  drawServiceDelete();
  drawServiceTabs();
}

/**
 * The caller's own content item for a service, or null when it is not one of theirs.
 *
 * <b>Its own function because two things need it and one of them is a fallback — D-200.</b> The
 * facts list needs the item; the address needs only the kind, which the item carries when there is
 * one and the services directory carries always.
 *
 * @param {string} qualified the service's folder-and-name
 * @returns {Promise<object|null>} the item, or null
 */
async function contentItem(qualified) {
  try {
    const answer = await api("/content/items");
    return (answer.items || []).find(i => i.name === qualified) || null;
  } catch {
    return null;
  }
}

/**
 * A service's kind, from the services directory.
 *
 * <b>The directory answers for every kind and needs no privilege.</b> It is the same document a
 * client reads to find what this server publishes, it is sharing-governed like everything else
 * under `/rest/services`, and its `type` is a face's name — which is what an address is built
 * from.
 *
 * <b>A service appears once per face, not once.</b> `ci_buildings` is listed as a `FeatureServer`
 * *and* as a `VectorTileServer` because it serves tiles too, so taking the first match gives
 * whichever the server happened to write first — a tile address on a page about the service. The
 * order here is the page's own: the feature face if there is one, the image face otherwise, and
 * anything else only when those two are absent.
 *
 * @param {string} qualified the service's folder-and-name
 * @returns {Promise<string|null>} `FeatureServer`, `ImageServer`, or null when it cannot be read
 */
async function serviceKind(qualified) {
  const cut = qualified.lastIndexOf("/");
  const folder = cut < 0 ? "" : qualified.slice(0, cut);

  try {
    const answer = await api(
      `/rest/services${folder ? "/" + encodeURIComponent(folder) : ""}?f=json`);

    const faces = (answer.services || [])
      .filter(s => s.name === qualified)
      .map(s => s.type);

    return faces.find(f => f === "FeatureServer")
      || faces.find(f => f === "ImageServer")
      || faces[0]
      || null;
  } catch {
    return null;
  }
}

/**
 * Overview's right-hand column: what this service is, and the address it answers on.
 *
 * <b>The URL is why this column exists.</b> The owner, pointing at it: *"burada da servisin url'i
 * var."* An operator wiring a service into ArcGIS Pro, a web map or a script needs that string, and
 * this console showed it nowhere — it was derivable from the address bar by somebody who already knew
 * the shape of a REST path. Copy and a link that opens it.
 *
 * <b>What the reference's column has and this one does not.</b> An *Item Information* completeness
 * meter, a star rating, Categories, Tags, Credits, Metadata. None of those has anywhere to be stored
 * here — §5j settled that there is no item that exists apart from its service — and a meter scoring a
 * description nothing keeps would score nothing. ADR-034 §5k lists the omission rather than leaving it
 * to be noticed.
 *
 * <b>Read from the content listing rather than assembled from three places.</b> `/content/items`
 * already answers with the kind, the owner, the sharing scope and the layer count for exactly the
 * services this caller may see, which is the same set they could have navigated here from.
 */
async function drawServiceDetails(qualified, knownKind) {
  const box = $("serviceDetails");
  if (!box || surfaceOfPath() !== "studio") return;

  // <b>The face is the service's kind, and hard-coding it was [D-200](../../docs/architecture-debt.md).</b>
  // Every service in this catalogue is a `FeatureServer` or an `ImageServer` and the kind *is* the
  // face's name, so an image service was being shown a FeatureServer address that answers 404 —
  // beside a facts list that stayed empty, because `/content/items` lists feature services only and
  // the lookup below simply returned. A page that renders empty is read as a product that lost the
  // service.
  //
  // <b>Asked of the services directory, which knows every kind and needs no privilege.</b>
  // `/content/items` is the caller's own content and `/admin/featureservices` needs
  // `admin:manageServer`; the directory is sharing-governed and answers for both kinds, which is
  // what this needs and all it needs.
  const item = await contentItem(qualified);
  const kind = knownKind || (item ? item.kind : null) || await serviceKind(qualified);

  const root = `${location.origin}/rest/services/${
    qualified.split("/").map(encodeURIComponent).join("/")}/${kind || "FeatureServer"}`;

  //
  // <b>`title`, because the field truncates.</b> Measured at 1440: a 260-pixel box against a 70-character
  // address, cut off mid-word, so Copy worked and nobody could read what they were about to copy. And
  // the ground is `--surface-2` so it does not read as one of the editable boxes on the Settings page.
  //
  // <b>And the facts get a heading of their own.</b> One `h4` was governing both the address and six
  // unrelated facts, so everything below it read as part of the address.
  $("serviceAddress").innerHTML = `
    <h4>The service's address</h4>
    <div class="urlrow">
      <input type="text" id="svcUrl" readonly value="${h(root)}" title="${h(root)}">
      <button class="tiny" id="svcUrlCopy" title="Copy this address">Copy</button>
    </div>
    <p class="hint"><a href="${h(root)}?f=json" target="_blank" rel="noreferrer">Open it</a> — the
      service document, which is what a client reads first.</p>`;

  // <b>`facts2`, not `dl.facts` — and this is why the column read as a debug dump.</b> `dl.facts` is
  // monospace by design and its one other user is Server's *fixed, and not editable here* block, which is
  // genuinely technical numbers. Setting `root` and `4` in mono dilutes the one place monospace still
  // means something on this page: the address. `.facts2` is the same content's own idiom one screen over
  // — the group page's Overview lists a standing, an owner, a date and two counts in it.
  box.innerHTML = `
    <h4>About this service</h4>
    <dl class="facts2" id="svcFacts"></dl>`;

  try {
    if (!item) {
      // <b>What is known, rather than nothing — D-200.</b> `/content/items` lists feature
      // services, so an image service is not in it and this used to return, leaving the empty
      // definition list it had just written. The facts that do not need that listing are the
      // ones a reader came for, and the rest of the page — the coverage panel — carries the
      // service's own numbers.
      //
      // <b>Whether Studio should list every kind in `/content/items`</b> is a decision about
      // what Studio is for, and it belongs to
      // [ADR-034](../../docs/adr/ADR-034-server-and-studio.md) rather than to this template.
      $("svcFacts").innerHTML = `
        <dt>Kind</dt><dd>${h(kind || "unknown")}</dd>
        <dt>Folder</dt><dd>${qualified.includes("/")
          ? h(qualified.slice(0, qualified.lastIndexOf("/")))
          : `<span class="val">the site root</span>`}</dd>
        <dt>Sharing</dt><dd><span class="val">set on the Settings tab — this kind is not in
          your content listing, so the owner and the dates are not read here</span></dd>`;

      return;
    }

    // <b>The sharing scope is the control that changes it, here as everywhere else.</b> This was the one
    // place on the product where that pill was inert — the content list has wrapped it in `.pillbtn`
    // since §5l, and the click delegation matches `[data-share]` anywhere in the document, so this needs
    // no new markup and no new behaviour. A fact that is a button in one place and a label in another is
    // the page lying about which.
    //
    // <b>And no `Layers` row.</b> The count is already on the tab badge and in the subtitle above; a
    // third copy is a row that varies with nothing.
    // <b>Three more, from the handoff.</b> Where the data lives, what it is projected in and how
    // many layers there are: the first two were on no screen at all, and the third was on the
    // tab badge only — which is not where somebody reads a service's facts.
    //
    // <b>The count comment that used to be here said the opposite and was right at the time.</b>
    // It argued a third copy of the layer count varies with nothing; what changed is that the
    // subtitle now carries the kind and the sharing as well, so this list is the one place the
    // seven facts are together rather than a duplicate of a badge.
    const mine = known.filter(k => k.service === item.bare
      && (k.folder || null) === (item.folder || null));

    const source = mine.length > 0
      ? (mine.every(k => k.hosted)
        ? `<span title="The tables are in this server's datastore">hosted</span>`
        : mine.some(k => k.hosted)
          ? `<span title="Some of this service's tables are this server's and some are not">mixed</span>`
          : `<span title="The tables stay where they are; this server reads them">registered</span>`)
      : null;

    const spatial = serviceDoc && serviceDoc.spatialReference
      ? `EPSG:${h(String(serviceDoc.spatialReference.latestWkid
        || serviceDoc.spatialReference.wkid))}`
      : null;

    const rows = [
      ["Kind", h(item.kind || "feature service")],
      ["Owner", h(item.owner || "—")],
      ["Folder", item.folder ? h(item.folder) : `<span class="val">the site root</span>`],
      ["Sharing", `<button class="pillbtn" data-share="${h(item.name)}"
         title="Set who can reach this">${pill(item.sharing)}</button>`],
      // <b>Only when it is known, rather than inferred from the folder's name.</b> A service in
      // a folder called `hosted` is usually hosted and a convention is not a fact; the
      // administrative listing carries the answer and Studio's reader may not have it, so the
      // row is absent rather than guessed.
      ...(source ? [["Source", source]] : []),
      ...(spatial ? [["Spatial ref", spatial]] : []),
      ["Layers", `<span title="${num(serviceEntries)} entr${serviceEntries === 1 ? "y" : "ies"} in the service document, which counts a group layer and what is nested under it">${
        num(item.layers || serviceLayers.length || 0)}</span>`],
      ["Published", item.created ? h(String(item.created).slice(0, 10)) : `<span class="val">—</span>`],
      ["Updated", item.updated ? h(String(item.updated).slice(0, 10)) : `<span class="val">—</span>`],
    ];

    $("svcFacts").innerHTML = rows.map(([label, value]) =>
      `<dt>${label}</dt><dd>${value}</dd>`).join("");

    // <b>Sharing, state, owner — the handoff's three, and the strip's whole job.</b> It held a
    // mono dump of the service document's numbers, which are two panels of their own now.
    // <b>The scope is the pill beside this, so the word is not repeated.</b>
    // `loadServiceCapabilities` writes `#serviceScope` from the server's own answer; printing
    // *public* again in the line after it is the page saying one fact twice in two shapes. And
    // it stays that function's element — two writers for one pill is how a className set by one
    // gets cleared by the other.
    if ($("serviceFacts")) {
      $("serviceFacts").textContent = [item.status, item.owner].filter(Boolean).join(" · ");
    }

    drawServiceHead(item);
  } catch {
    // The column's own reason — the address — is already on screen and needed no request. A failure
    // here loses the facts beside it and nothing somebody came for.
  }
}

/**
 * Data: one layer's rows, or its fields.
 *
 * <b>The geometry column is shown, and theirs hides it — owner decision.</b> *"coğrafi kolonlar
 * özellikle gizlenmiş ama bizde açık olabilir çok sorun değil."* So the query asks for the geometry and
 * the table states it as its type and vertex count rather than as coordinates, because a WKB blob in a
 * cell is not information.
 */
let dataView = "table";

function drawServiceData() {
  const picker = $("dataLayer");
  const views = $("dataViews");
  if (!picker || !views) return;

  const publishable = serviceLayers.filter(l => !(l.type || "").toLowerCase().includes("group"));

  // <b>What the address asked for, if it asked, and the first drawable layer otherwise.</b> An
  // Overview row's *Data* button carries its own layer; without this the picker chose the first
  // one and every row on a four-layer service opened the same table.
  const wanted = dataLayerIndex !== null
    && publishable.some(l => String(l.id) === String(dataLayerIndex))
    ? String(dataLayerIndex)
    : String((publishable[0] || {}).id ?? 0);

  picker.innerHTML = publishable.length === 0
    ? `<option value="">this service holds no layers</option>`
    : publishable.map(l =>
        `<option value="${num(l.id ?? 0)}"${String(l.id) === wanted ? " selected" : ""}>${
          h(l.name || `layer ${l.id}`)}</option>`).join("");

  picker.disabled = publishable.length === 0;

  views.innerHTML = [["table", "Table"], ["fields", "Fields"]].map(([key, label]) =>
    `<a href="#" data-data-view="${key}"${key === dataView ? ' aria-current="page"' : ""}>${label}</a>`)
    .join("");

  picker.onchange = () => {
    // The reader has chosen, so the address stops choosing.
    dataLayerIndex = null;
    loadServiceData();
  };

  if (publishable.length > 0) loadServiceData();
  else $("dataRows").innerHTML = "";
}

/**
 * Which layer the Data tab shows, when an address asked for one.
 *
 * <b>Consumed on arrival, like `visLayerIndex`.</b> Kept null once the reader has used the
 * picker, so a redraw does not put the address's choice back over theirs — which is the fault
 * `?mode=` had on the Visualization tab and the reason that one is documented.
 */
let dataLayerIndex = null;

/** Reads whichever of the two views is chosen. */
async function loadServiceData() {
  const box = $("dataRows");
  const index = $("dataLayer").value;

  if (!serviceOpen || index === "") return;

  const root = `/rest/services/${
    serviceOpen.qualified.split("/").map(encodeURIComponent).join("/")}/FeatureServer/${
    encodeURIComponent(index)}`;

  box.innerHTML = `<p class="hint">Reading…</p>`;

  try {
    const document = await api(`${root}?f=json`);
    const fields = document.fields || [];

    if (dataView === "fields") {
      // <b>Named as the reference names them: what it is called and what it is called on the wire.</b>
      // Their screen has Display Name, Field Name and Type, and the distinction is real here too — an
      // alias is what an operator reads and the column is what a query names.
      box.innerHTML = `
        <table>
          <thead><tr><th>Display name</th><th>Field</th><th>Type</th><th>Length</th></tr></thead>
          <tbody>${fields.map(field => `
            <tr>
              <td class="name">${h(field.alias || field.name || "")}</td>
              <td class="mono">${h(field.name || "")}</td>
              <td class="val">${h((field.type || "").replace(/^esriFieldType/, ""))}</td>
              <td class="num">${field.length ? num(field.length) : ""}</td>
            </tr>`).join("")}</tbody>
        </table>
        <p class="hint"><b>Dropping a column is not built yet.</b> It is acceptable on hosted data and
          has to be planned for a registered table, which points at somebody else's database — ADR-034
          §5k. The list is what the service document declares.</p>`;
      return;
    }

    // <b>Twenty rows and no geometry in the cells.</b> The count is what makes a table readable at a
    // glance and the geometry is what makes it unreadable: `returnGeometry=false` keeps the request
    // small, and the geometry's presence is a fact about the layer rather than about a row.
    const shown = fields.filter(f => (f.type || "") !== "esriFieldTypeGeometry").slice(0, 12);

    const answer = await api(`${root}/query?where=1%3D1&outFields=*&returnGeometry=false`
      + `&resultRecordCount=20&resultOffset=0&f=json`);

    const rows = answer.features || [];

    box.innerHTML = `
      <table>
        <thead><tr>${shown.map(f => `<th>${h(f.alias || f.name)}</th>`).join("")}</tr></thead>
        <tbody>${rows.length === 0
          ? `<tr><td colspan="${Math.max(1, shown.length)}" class="empty">No rows in this layer
               yet.</td></tr>`
          : rows.map(feature => `<tr>${shown.map(f => {
              const value = (feature.attributes || {})[f.name];
              return `<td${typeof value === "number" ? ' class="num"' : ""}>${
                value === null || value === undefined ? '<span class="val">—</span>' : h(String(value))
              }</td>`;
            }).join("")}</tr>`).join("")}</tbody>
      </table>
      <p class="hint">The first ${num(rows.length)} row${rows.length === 1 ? "" : "s"}${
        shown.length < fields.length - 1
          ? ` and the first ${num(shown.length)} of ${num(fields.length)} columns`
          : ""}. The geometry is not asked for — a layer's geometry is a fact about the layer, and
        <b>Fields</b> states it.</p>`;
  } catch (e) {
    box.innerHTML = `<p class="hint">${h(e.message || String(e))}</p>`;
  }
}

/**
 * The three scopes, in the words the reference uses and the values this server takes.
 *
 * <b>Their labels, our values.</b> The owner's screen says Owner / Organization / Everyone (public);
 * `PUT /admin/services/{name}/sharing` takes `private` / `organization` / `public`, which is what
 * §5z's content sections are computed from. The glyphs are the ones the sharing pills already use, so a
 * scope looks the same in the dialog that sets it as in the row that reports it.
 */
const SHARE_SCOPES = [
  ["private", "Owner", "Only you and an administrator can reach it."],
  ["organization", "Organization",
   "Everyone signed in to this server can read it. A stranger with the address still gets nothing."],
  ["public", "Everyone (public)",
   "Anyone with the address can read it, signed in or not. Anonymous view shows what they receive."],
];

/** What the share dialog is editing, and what it has been told to do. */
let sharing = null;

/**
 * Opens the share dialog for one service.
 *
 * <b>Read again on opening rather than taken from the row.</b> A row was drawn when the screen loaded
 * and the scope may have moved since — and the dialog is about to write, so it starts from what the
 * server says now.
 */
async function openShare(qualified) {
  sharing = {
    qualified,
    scope: null,
    groups: [],          // the groups it is in now: { name, title }
    wanted: null,        // the set the reader has chosen, or null while unchanged
    step: "scope",
    filter: "",
  };

  $("shareTitle").textContent = "Share";
  $("shareBody").innerHTML = `<p class="hint">Reading how this is shared…</p>`;
  $("shareFoot").innerHTML = "";
  $("share").showModal();

  try {
    const answer = await api("/content/items");
    const item = (answer.items || []).find(i => i.name === qualified);

    if (!item) {
      $("shareBody").innerHTML = `<p class="hint">This service is no longer in your content.</p>`;
      return;
    }

    sharing.scope = item.sharing || "private";

    // <b>Absent means *not yours to know*, and it is not the same as empty.</b> The endpoint returns
    // `sharedWith` only to an owner or an administrator (§5l), so a null here is a reader who may see
    // the item and may not set its sharing — and the dialog says so rather than showing an empty list
    // that reads as *shared with nobody*.
    sharing.groups = Array.isArray(item.sharedWith) ? item.sharedWith : null;
    sharing.wanted = sharing.groups === null
      ? null
      : new Set(sharing.groups.map(g => g.name).filter(Boolean));

    drawShare();
  } catch (e) {
    $("shareBody").innerHTML = `<p class="hint">${h(e.message || String(e))}</p>`;
  }
}

/** Whichever of the two screens the dialog is on. */
function drawShare() {
  if (!sharing) return;

  if (sharing.step === "groups") return drawShareGroups();

  $("shareTitle").textContent = "Share";

  const readOnly = sharing.groups === null;

  $("shareBody").innerHTML = `
    <p class="picklede">Set sharing level.</p>
    ${SHARE_SCOPES.map(([key, label, said]) => `
      <label class="pickrow${key === sharing.scope ? " on" : ""}" data-scope="${key}">
        <input type="radio" name="shareScope" value="${key}"
          ${key === sharing.scope ? "checked" : ""}${readOnly ? " disabled" : ""}>
        <span><b>${icon(key)} ${h(label)}</b><span class="lede">${h(said)}</span></span>
      </label>`).join("")}

    <h4 style="margin-top:var(--gap-5)">Set group sharing</h4>
    ${readOnly
      ? `<p class="hint">Which groups this is shared with is the owner's answer, and you are neither
           its owner nor an administrator. You can see it because of how it reached you.</p>`
      : `<div id="shareGroupList"></div>
         <button type="button" class="ghost" id="shareEditGroups">Edit group sharing</button>`}`;

  if (!readOnly) drawShareGroupList();

  $("shareFoot").innerHTML = `
    <span class="fill"></span>
    <button type="button" class="ghost" id="shareCancel">Cancel</button>
    ${readOnly ? "" : `<button type="button" class="primary" id="shareSave">Save</button>`}`;
}

/** The groups it is in, as the dialog currently intends them. */
function drawShareGroupList() {
  const box = $("shareGroupList");
  if (!box || !sharing?.wanted) return;

  // <b>Titles from both places it knows them.</b> The item's own `sharedWith` names the groups it is
  // already in; a group ticked on the second screen is not in that list yet, so its title comes from the
  // list of the caller's groups. Without the second source a newly ticked group showed as its key — the
  // chip read `planning` where the table two clicks earlier said *Planning Group*.
  const named = new Map([
    ...(sharing.available || []).map(g => [g.name, g.title || g.name]),
    ...(sharing.groups || []).map(g => [g.name, g.title]),
  ]);

  box.innerHTML = sharing.wanted.size === 0
    ? `<p class="hint">Not shared with any group. <b>Edit group sharing</b> offers the groups you are a
         member of.</p>`
    : `<div class="chips">${[...sharing.wanted].map(name =>
        `<span class="chip">${h(named.get(name) || name)}
           <button type="button" class="tiny ghost" data-unshare="${h(name)}"
             aria-label="Stop sharing with ${h(named.get(name) || name)}"
             title="Stop sharing with this group">&#10005;</button></span>`).join("")}</div>`;
}

/**
 * The second screen: the groups you are a member of.
 *
 * <b>*"üyesi olduğum gruplarla"* — the caller's own groups, and only the ones they may add to.</b>
 * `/admin/groups` answers with `standing` and `contribute` per row, so a group whose contribution is
 * managers-only and where you stand as a member is a group you cannot share into. Offering it would be
 * a control that fails on press, which is what ADR-034's own rule is about.
 */
async function drawShareGroups() {
  $("shareTitle").textContent = "Group sharing";

  $("shareFoot").innerHTML = `
    <button type="button" class="ghost" id="shareBack">Back</button>
    <span class="fill"></span>
    <button type="button" class="ghost" id="shareCancel">Cancel</button>`;

  if (!sharing.available) {
    $("shareBody").innerHTML = `<p class="hint">Reading your groups…</p>`;

    try {
      const answer = await api("/admin/groups");

      sharing.available = (answer.groups || []).filter(g =>
        g.standing === "owner" || g.standing === "manager"
        || (g.contribute !== "managers" && g.standing));
    } catch (e) {
      $("shareBody").innerHTML = `<p class="hint">${h(e.message || String(e))}</p>`;
      return;
    }
  }

  $("shareBody").innerHTML = `
    <div class="toolbar">
      <input type="search" id="shareFilter" placeholder="Search your groups"
        value="${h(sharing.filter)}" autocomplete="off">
      <span class="val" id="shareCount"></span>
      <span style="flex:1"></span>
      <span class="val" id="shareShown"></span>
    </div>
    <p class="hint">Choices here are kept if you go <b>Back</b> — nothing is sent until <b>Save</b>.</p>
    <div id="shareRows"></div>`;

  drawShareRows();
}

/**
 * The group table, redrawn on its own.
 *
 * <b>Separate from the screen around it so a keystroke does not replace the box being typed in.</b> The
 * first version redrew everything and then re-focused the input, which works and loses the caret
 * position — this leaves the toolbar alone.
 *
 * <b>No pager, at 54 groups or well beyond.</b> The design review simulated 54 and measured five screens
 * of scroll inside the dialog's own `max-height`; its conclusion was that a search box resolves finding
 * one in two keystrokes and a pager would be complexity for a count nowhere near this product's stated
 * scale. The reference's filter rail — Owner, Special groups, Date modified, Date created — is not
 * copied either: `/admin/groups` answers with `standing` and `contribute` and no dates at all, so those
 * would be filters over facts this model does not have.
 */
function drawShareRows() {
  const needle = sharing.filter.trim().toLowerCase();

  const shown = sharing.available.filter(g => needle === ""
    || (g.title || g.name).toLowerCase().includes(needle)
    || g.name.toLowerCase().includes(needle));

  const count = $("shareCount");
  const said = $("shareShown");

  if (count) count.textContent = `Selected: ${num(sharing.wanted.size)}`;

  if (said) {
    said.textContent = shown.length === sharing.available.length
      ? `${num(sharing.available.length)} group${sharing.available.length === 1 ? "" : "s"}`
      : `${num(shown.length)} of ${num(sharing.available.length)}`;
  }

  $("shareRows").innerHTML = `
    <div class="widetable">
      <table>
        <thead><tr><th class="tick"></th><th>Group</th><th>You are</th><th>Already shared</th></tr></thead>
        <tbody>${sharing.available.length === 0
          ? `<tr><td colspan="4" class="empty">You are not in a group you can share into. A group's
               owner or a manager can add you, and a group that only lets managers contribute cannot
               take this from you.</td></tr>`
          : shown.length === 0
            ? `<tr><td colspan="4" class="empty">No group matches <b>${h(sharing.filter)}</b>.</td></tr>`
            : shown.map(g => {
              const has = sharing.wanted.has(g.name);
              const had = (sharing.groups || []).some(x => x.name === g.name);

              return `<tr>
                <td class="tick"><input type="checkbox" data-share-group="${h(g.name)}"
                  ${has ? "checked" : ""}></td>
                <td class="name">${h(g.title || g.name)}</td>
                <td class="val">${h(g.standing || "")}</td>
                <td class="val">${had ? "already shared" : ""}</td>
              </tr>`;
            }).join("")}</tbody>
      </table>
    </div>`;
}

/**
 * Writes what the dialog intends: the scope, then the group changes.
 *
 * <b>The scope first, because it is the one that can fail on authorization.</b> A caller who may not set
 * it gets a refusal before any group has moved, so a partial write is a scope that did not change and
 * groups that did not either.
 *
 * <b>And each group change is reported by name if it fails.</b> Sharing with four groups and failing on
 * the third is three that worked; a single *could not save* would leave the reader to guess which.
 */
async function saveShare() {
  if (!sharing) return;

  const chosen = $("shareBody").querySelector(`input[name="shareScope"]:checked`);
  const scope = chosen ? chosen.value : sharing.scope;

  const { folder, name } = splitService(sharing.qualified);
  const failed = [];

  try {
    if (scope !== sharing.scope) {
      await api(`/admin/services/${encodeURIComponent(name)}/sharing`
        + `?folder=${encodeURIComponent(folder || "")}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sharing: scope }),
      });
    }
  } catch (e) {
    toast(e.message);
    return;
  }

  const had = new Set((sharing.groups || []).map(g => g.name).filter(Boolean));

  // <b>The bare name in the path and the folder in a query, which the group's own picker already
  // does.</b> The first version passed the qualified name — `encodeURIComponent("hosted/tr_ilce")` is
  // `hosted%2Ftr_ilce`, and the route's `{service}` segment does not put the slash back, so the server
  // answered *'hosted%2Ftr_ilce' is not something this server has*. Found by sharing a service that was
  // **not** already shared: the first test used one that was, so the loop skipped the write and reported
  // success without having made a request. A test that exercises nothing passes.
  const where = group => {
    const cut = sharing.qualified.lastIndexOf("/");
    const folder = cut < 0 ? "" : sharing.qualified.slice(0, cut);
    const bare = cut < 0 ? sharing.qualified : sharing.qualified.slice(cut + 1);

    return `/admin/groups/${encodeURIComponent(group)}/items/${encodeURIComponent(bare)}`
      + `?folder=${encodeURIComponent(folder)}`;
  };

  for (const group of sharing.wanted) {
    if (had.has(group)) continue;

    try {
      await api(where(group), { method: "PUT" });
    } catch (e) { failed.push(`${group}: ${e.message}`); }
  }

  for (const group of had) {
    if (sharing.wanted.has(group)) continue;

    try {
      await api(where(group), { method: "DELETE" });
    } catch (e) { failed.push(`${group}: ${e.message}`); }
  }

  $("share").close();

  toast(failed.length === 0
    ? `${sharing.qualified}: shared ${scope}${sharing.wanted.size
        ? ` and with ${sharing.wanted.size} group${sharing.wanted.size === 1 ? "" : "s"}` : ""}.`
    : `Some group changes did not apply — ${failed.join("; ")}`, failed.length === 0);

  sharing = null;

  await section("your content", loadMyContent, "contentRows").then(paintPreviews);
}

/**
 * What the item is, above the list of what is in it.
 *
 * <b>Nothing here is invented to fill space.</b> The design review's answer to *most of this page is
 * white at one layer* was that padding it with a completeness meter or a description nobody wrote is
 * exactly the filler ADR-034 argues against — but three real things were already being fetched and
 * discarded: the cover layer's URL, which is what the content list already paints a live preview from;
 * the description, when the create form was given one; and the update date.
 *
 * <b>The picture is the one the content list shows.</b> It is the server's own render of the
 * cover layer, addressed the same way from `thumbnailFor`, so placing one here needs no new
 * mechanism — and it is drawn from the layer's geometry and symbology rather than stored, which
 * is why a product with no thumbnail storage can still show a picture.
 */
function drawServiceHead(item) {
  const box = $("serviceHead");
  if (!box) return;

  box.innerHTML = `
    <div class="itemhead">
      ${item.cover
        ? `<img class="thumb" alt="" loading="lazy"
             data-thumb="${h(thumbnailFor(item.cover.url))}">`
        : `<div class="thumb empty" title="This service has no layer to draw, so there is no map to show."></div>`}
      <div>
        <!--
          <b>The name and the kind moved to the page head.</b> They were here because this panel
          was the only thing on the page that said what the service is; with a 33-pixel title
          above it, printing them again in 15-pixel bold is the same fact at two sizes.
        -->
        ${item.description
          ? `<p class="lede">${h(item.description)}</p>`
          : `<p class="hint">No description. A service with one is easier to find in a listing
             than one identified only by its name.</p>`}
        <div class="footnote">${item.updated ? `Updated ${h(String(item.updated).slice(0, 10))}` : ""}${
          item.created ? ` · published ${h(String(item.created).slice(0, 10))}` : ""}</div>
      </div>
    </div>`;

  // <b>The subtitle is the line this panel used to carry.</b> Kind, how many layers and who may
  // read it: three facts that qualify the name, which is what a subtitle is for.
  const sub = $("serviceSub");

  if (sub) {
    sub.textContent = [
      item.kind || "feature service",
      `${num(item.layers || 0)} layer${(item.layers || 0) === 1 ? "" : "s"}`,
      item.sharing || "",
    ].filter(Boolean).join(" · ");
  }

  // <b>And then the cover is read, not only shown.</b> A blank white rectangle where a map
  // should be is the shape of a fault, and this one is not one: the picture arrived, it is
  // simply empty. Saying which of the two it is takes one canvas read of an image the page has
  // already fetched.
  paintPreviews().then(() => explainBlankCover());
}

/**
 * Whether a picture this server drew has nothing in it.
 *
 * <b>Every thumbnail is cleared to transparent</b> (`ThumbnailEndpoints.RenderAsync`), so *drew
 * nothing* is a question about alpha and has an exact answer. Eight rather than zero, because a
 * hairline antialiased to almost nothing is still something that drew.
 *
 * <b>Same-origin, so the canvas is not tainted.</b> The picture is a blob of this server's own
 * response; a cross-origin one would throw on `getImageData` and be reported as *not blank*,
 * which is the safe direction to be wrong in.
 *
 * @param {string} href the image's address
 * @returns {Promise<boolean>} true when every pixel is transparent
 */
async function drewNothing(href) {
  try {
    const image = await new Promise((ok, no) => {
      const img = new Image();

      img.onload = () => ok(img);
      img.onerror = no;
      img.src = href;
    });

    // Sampled rather than read whole: a feature that covers no 160th of the picture is a
    // feature nobody can see either.
    const wide = Math.max(1, Math.min(image.naturalWidth || 1, 160));
    const tall = Math.max(1, Math.min(image.naturalHeight || 1, 160));

    const canvas = document.createElement("canvas");

    canvas.width = wide;
    canvas.height = tall;

    const paper = canvas.getContext("2d", { willReadFrequently: true });

    paper.drawImage(image, 0, 0, wide, tall);

    const { data } = paper.getImageData(0, 0, wide, tall);

    for (let i = 3; i < data.length; i += 4) {
      if (data[i] > 8) return false;
    }

    return true;
  } catch {
    return false;
  }
}

/**
 * Says so when the service's cover drew nothing, instead of showing white.
 *
 * <b>The blank thumbnail was reading as a fault.</b> Handoff 2026-09-04. It is not one — the
 * request succeeded and the renderer ran — so the panel says what happened and offers the one
 * thing that would show more: the map, where the extent can be changed.
 */
async function explainBlankCover() {
  const image = document.querySelector("#serviceHead img.thumb[src]");

  if (!image || image.dataset.checked) return;

  image.dataset.checked = "1";

  if (!await drewNothing(image.src)) return;

  const said = document.createElement("div");

  said.className = "thumb empty drewnothing";
  said.innerHTML = `<b>Nothing drew</b><span>Every pixel is transparent. The features may be
    outside the drawn extent, or painted at an opacity too low to see.
    <a href="#" data-service-tab="visualization">Open the map</a></span>`;

  image.replaceWith(said);
}

/**
 * Visualization: one layer of this service, drawn as features or as tiles.
 *
 * <b>The picker's population rule is Data's, and the group layers are out for the same reason.</b> A
 * group holds no geometry, so there is nothing to draw and offering it would be a control that fails on
 * press.
 *
 * <b>The address may have chosen for you.</b> `?layer=` and `?mode=` are what the three redirected entry
 * points carry — Studio's content row, its tiles control, and the layer editor's own buttons — so
 * pressing *Map* on a row lands here already drawing that layer. Without them the first drawable layer
 * is chosen, which is what somebody who pressed the tab meant.
 */
function drawServiceVis() {
  const picker = $("visLayer");
  const modes = $("visModes");
  if (!picker || !modes) return;

  const drawable = serviceLayers.filter(l => !(l.type || "").toLowerCase().includes("group"));

  if (drawable.length === 0) {
    $("mapPanel").hidden = true;
    return;
  }

  // <b>What was chosen, or the first drawable layer.</b> The address's choice was taken once on arrival
  // — see `showService` — so this reads only what is current, which is what lets the picker and the mode
  // strip actually change anything.
  const wanted = visLayerIndex !== null && drawable.some(l => String(l.id) === String(visLayerIndex))
    ? String(visLayerIndex)
    : String(drawable[0].id ?? 0);

  visLayerIndex = wanted;

  picker.innerHTML = drawable.map(l =>
    `<a href="#" data-vis-layer="${num(l.id ?? 0)}"${
      String(l.id) === wanted ? ' aria-current="page"' : ""} title="${h(l.name || "")}"
      >${num(l.id ?? 0)} · ${h(l.name || `layer ${l.id}`)}</a>`).join("");

  // <b>The link follows the picker.</b> A Symbology link that always opened the first layer
  // would be worse than none on a service with several: it would look like it worked.
  const named = at => {
    const one = drawable.find(l => String(l.id) === String(at)) || drawable[0];
    const link = $("visSymbology");

    if (link && one) {
      link.href = `#/layer/${encodeURIComponent(one.name || "")}/symbology`;
      link.title = `How ${one.name || "this layer"} is drawn`;
    }

    // <b>What the picked layer is, beside the picker.</b> The chips carry an id and a name; the
    // geometry and how many features it holds are the two facts somebody checks against the
    // picture, and neither was anywhere on this tab.
    const meta = $("visMeta");

    if (meta && one) {
      const geometry = GEOMETRY_NAMES[one.geometryType]
        || (one.geometryType || "").replace(/^esriGeometry/, "");

      meta.textContent = [geometry, one.type && !geometry ? one.type : ""]
        .filter(Boolean).join(" · ");
    }
  };

  named(wanted);

  // <b>Tiles only where the service has them.</b> A registered layer is served as features and has no
  // vector tile service, so the mode would be a button that answers 404 — the row controls already make
  // this distinction and this one inherits it.
  const hosted = (serviceOpen?.folder || "") === "hosted";

  if (!hosted && visMode === "tiles") visMode = "features";

  modes.innerHTML = [["features", "Features"], ...(hosted ? [["tiles", "Tiles"]] : [])]
    .map(([key, label]) =>
      `<a href="#" data-vis-mode="${key}"${key === visMode ? ' aria-current="page"' : ""}>${label}</a>`)
    .join("");

  drawVisNow();
}

/**
 * Draws whichever layer and mode are chosen.
 *
 * <b>It says it is working, which the map panel never did.</b> Once a layer was picked the old screen
 * showed its empty-state placeholder until the SDK either painted or gave up fifteen seconds later —
 * silence for the whole window. *Dümdüz* still means saying something is happening.
 */
async function drawVisNow() {
  if (!serviceOpen || visLayerIndex === null) return;

  const layer = serviceLayers.find(l => String(l.id) === String(visLayerIndex));
  if (!layer) return;

  const named = layer.name || `layer ${layer.id}`;

  $("mapPanel").hidden = false;
  $("legend").textContent = `Drawing ${named}…`;

  try {
    if (visMode === "tiles") {
      await showTiles(named);
      return;
    }

    // The whole document, because the SDK reads it anyway and the colour comes from the server's
    // `drawingInfo` rather than from a choice of ours.
    const document_ = await api(
      `/rest/services/${serviceOpen.qualified.split("/").map(encodeURIComponent).join("/")}`
      + `/FeatureServer/${encodeURIComponent(String(layer.id ?? 0))}?f=json`);

    await show(named, document_);
  } catch (e) {
    // <b>Into the caption rather than a toast.</b> A toast fades while the reader is still looking at
    // an empty map wondering whether it is slow or broken.
    $("legend").innerHTML = `<span class="val">${h(e.message || String(e))}</span>`;
  }
}

/**
 * Where a layer is drawn now: its service's Visualization tab.
 *
 * <b>The three controls that used to open a map now navigate.</b> §5k's instruction was that
 * Visualization absorbs them rather than becoming a third place, and a control that still opened its own
 * map would have made it the third. The layer editor's buttons exist on both surfaces, so this crosses
 * when it has to — which is why the tab and the layer travel in a query string rather than in a module
 * variable that a path change would lose.
 */
function visHref(name, mode) {
  const at = placeOf(name);
  if (!at) return null;

  // <b>`id`, which is what `placeOf` returns.</b> It read `at.index`, which nothing sets — so
  // `?? 0` caught it every time and every *Map* shortcut in this console opened layer 0. On a
  // one-layer service that is right by accident; on `ci_EarlyAlert` it means pressing Map beside
  // `_reports` draws `_sites`.
  const query = `?tab=visualization&layer=${encodeURIComponent(String(at.id ?? 0))}`
    + `&mode=${encodeURIComponent(mode)}`;

  const hash = `service/${at.service.split("/").map(encodeURIComponent).join("/")}${query}`;

  return surfaceOfPath() === "studio" ? `#/${hash}` : surfaceHref("studio", hash);
}

/** Sends the reader to a layer's Visualization tab, from either surface. */
function toVisualization(name, mode) {
  const where = visHref(name, mode);

  if (!where) {
    toast(`'${name}' is not in the services directory, so there is nothing to draw.`);
    return;
  }

  if (where.startsWith("#")) location.hash = where;
  else location.assign(where);
}

/**
 * Settings' delete, behind a lock that starts closed.
 *
 * <b>Two guards, both the owner's.</b> *"yanlışlıkla kullanıcının silme durumu engellensin. silerken de
 * emin misin diye sorarız."* The lock has to be cleared before the button does anything, and clearing it
 * is not the same gesture as pressing it — so a misplaced click cannot destroy a service. Then the
 * confirmation names what goes, because *are you sure* is a question nobody reads and *delete
 * hosted/Environmental_gdb and its 23 layers* is one they do.
 *
 * <b>Locked by default, where the reference starts unlocked.</b> A default that protects is the right
 * way round for the only irreversible action on this page.
 */
function drawServiceDelete() {
  const lock = $("svcLock");
  const button = $("svcDelete");
  const note = $("svcDeleteNote");

  if (!lock || !button || !note) return;

  // <b>Studio's, with the rest of the item page.</b> Server's service page is the administrative one —
  // starting, stopping, capabilities, ceilings — and deleting a service from it was a structure that
  // arrived by accident when the tabs did.
  const panel = $("serviceDanger");

  if (panel) panel.hidden = surfaceOfPath() !== "studio";

  if (surfaceOfPath() !== "studio") return;

  const count = serviceLayers.length;

  // <b>What actually goes, per the owner's correction.</b> The old sentence said the tables are not
  // dropped, which was true of the server and wrong as a policy: *"servis hosted sa ve silindiyse,
  // datastore dan silinmesi lazım."* ADR-034 §5k. A hosted layer's table is ours and goes with it; a
  // registered layer points at somebody else's database and its table is never touched.
  const hostedHere = (serviceOpen?.folder || "") === "hosted";

  note.innerHTML = count === 0
    ? `This service holds no layers, so deleting it removes the service and no data.`
    : hostedHere
      ? `Deleting this service unpublishes <b>${num(count)} layer${count === 1 ? "" : "s"}</b> and
         <b>drops their tables from the datastore</b>. Hosted data belongs to the service that holds it,
         so this cannot be undone — there is no unpublished copy left behind.`
      : `Deleting this service unpublishes <b>${num(count)} layer${count === 1 ? "" : "s"}</b>. These
         are registered layers: they point at a database that is not ours, so <b>no table is
         dropped</b> — the registration goes and the data stays exactly as it is.`;

  button.disabled = lock.checked;
  $("svcLockState").textContent = lock.checked ? "Locked" : "Not locked";
}

/**
 * A feature service's settings, on the service's own page.
 *
 * <b>This is D-61's repair.</b> The pages themselves are unchanged — they are the same markup that
 * was on each layer — and what changed is that there is now **one** of them per service instead of
 * one per layer, addressed by the service the endpoint was always addressing.
 *
 * <b>Nothing about the API moved.</b> `GET`/`PUT /admin/services/{name}/capabilities` was already
 * service-scoped; the layer pages were resolving a layer to its service in order to call it, which
 * is the clearest possible sign of where they belonged.
 */
function drawServiceSettings(name, folder) {
  const box = $("serviceEdit");
  if (!box) return;

  const mine = servicePagesOf(surfaceOfPath());

  if (mine.length === 0) {
    box.hidden = true;
    return;
  }

  const page = SERVICE_PAGE_OPEN && mine.includes(SERVICE_PAGE_OPEN) ? SERVICE_PAGE_OPEN : mine[0];

  $("serviceNav").innerHTML = mine.map(p =>
    `<a href="#" data-service-page="${p}"${p === page ? ' aria-current="page"' : ""}>${
      p[0].toUpperCase() + p.slice(1)}</a>`).join("");

  // <b>No Save on the Sharing page, because that page has nothing to save.</b> The scope applies the
  // moment it is chosen — ADR-031 §2b, so that an owner narrowing who may read a service can trust it
  // happened rather than press a button afterwards — and the page said so in its own copy while a Save
  // sat underneath it. The owner: *"combo değiştiğinde kaydoluyor gibi. save neden dikkate alınmıyor."*
  // Exactly: it was not, and a button that does nothing contradicts the sentence above it.
  $("servicePagesBody").innerHTML = serviceSettingsMarkup(name, folder)
    + (page === "sharing"
      ? ""
      : `<div class="row" style="margin-top:22px">
           <button class="primary" data-service-save="${h(name)}"
             data-folder="${h(folder || "")}">Save</button>
         </div>`);

  for (const section of document.querySelectorAll("#servicePagesBody .page")) {
    section.classList.toggle("on", section.id === `page-${page}`);
  }

  box.hidden = false;
  section("capabilities", () => loadServiceCapabilities(name, folder));
}

/** Which of a service's pages is open. Held here because it is a screen state, not an address. */
let SERVICE_PAGE_OPEN = null;

/**
 * The markup for a service's settings pages.
 *
 * <b>A constant rather than a function of the service</b>, because none of it depends on which
 * service is open — the values are filled by `loadServiceCapabilities` after the panel is drawn,
 * which is the same order the layer editor's pages use and the reason a control here never shows a
 * figure it did not read.
 */
/**
 * The markup for a service's settings pages.
 *
 * <b>Written out rather than lifted.</b> The first attempt moved the layer editor's page markup here
 * as a string constant, and its template expressions came through as literal text on screen — a
 * fragment of a template literal is not a string. Rewriting the two pages is also the honest move:
 * their wording was a layer's (*"faces this layer offers"*) and this is a service.
 */
const OPERATIONS = ["Query", "Create", "Update", "Delete", "Extract"];

function serviceSettingsMarkup(name, folder) {
  // <b>Three scopes, not four — owner instruction 2026-08-19: *"group shall not be part of this."*</b>
  // `group` is a value this server stores (`SharingScope.Group`) and it is not a thing you pick: a
  // service reaches a group because it was shared into one, which is the Share dialog's second screen
  // (ADR-034 §5l). Offering it here invited an operator to set a scope that grants nothing on its own —
  // and the group page's own repair desk exists because that half-done state is reachable.
  //
  // It is still rendered when it is the *current* value, disabled, so the select reports the state
  // rather than silently changing it. Which is also the honest shape of the model question this raises,
  // recorded rather than decided: whether `group` should remain a scope at all.
  //
  // <b>And this comment is out here because the last one was not.</b> D-77, for the fourth time: a
  // backtick inside an HTML comment inside a template literal closes the literal. `node --check` caught
  // it. An explanation does not go inside a string.


  // <b>Both halves are rendered and one is revealed, and the explanation is out here
  // because D-77 is about exactly this.</b> A backtick inside an HTML comment inside a
  // template literal closes the literal — five times now in this file, and its own
  // note two hundred lines up says an explanation does not go inside a string.
  //
  // An image service holds a coverage rather than layers, so none of the feature
  // settings apply to one: nothing to query, no vector tiles, no Create or Delete.
  // Which half applies is decided by loadServiceCapabilities, after the panel is
  // drawn, which is this panel's existing order — markup first, values after, so a
  // control never shows a figure it did not read.
  //
  // Until 2026-08-21 only the feature half existed, so clicking an ImageServer opened
  // the feature editor and offered five operations it does not answer. That is
  // correctness gate 2's fifth finding told by a different surface: the service
  // document claimed Map,Query,Data with no query route, and here the console claimed
  // it on the service's behalf.

  return `
    <section class="page" id="page-capabilities">
      <div id="coverageSettings" hidden>
        <h4>This service holds a coverage</h4>
        <dl class="facts" id="coverageFacts"><dt>Reading it…</dt><dd></dd></dl>

        <p class="hint"><b>An image service has no feature settings.</b> There is
          nothing to query, create or delete: it answers <code>exportImage</code> and
          <code>identify</code>, and what it draws is decided by the coverage's own
          rendering rule. Sharing and status are where every service's are.</p>

        <p class="hint">Imagery is registered where it lives and is never copied, so
          this server holds a reference rather than the file. Removing the registration
          leaves the file untouched.</p>

        <p><a class="btn" id="coverageView" target="_blank" rel="noreferrer" href="#"
          >Open in the ArcGIS SDK viewer</a></p>
      </div>

      <div id="featureSettings">
      <h4>Faces this service offers</h4>
      <div class="grid2">
        <label><input type="checkbox" id="capFeatures"> Feature access</label>
        <label><input type="checkbox" id="capTiles"> Vector tiles <span class="val">hosted only</span></label>
      </div>

      <h4>Operations allowed</h4>
      <div class="grid2" id="ops">
        ${OPERATIONS.map(o =>
          `<label><input type="checkbox" data-op="${o}"> ${o}</label>`).join("")}
      </div>
      <p class="hint">A tick is a ceiling, not a grant: what a caller may do is this narrowed by
        their privileges and by what the data supports — ADR-031. Unticking is the only direction
        that has an effect.</p>
      <p class="hint"><b>One setting per service.</b> Every layer this service holds offers what is
        ticked here; there is no per-layer version of this, and the console used to imply there
        was — D-61.</p>
          </div>
    </section>

    <section class="page" id="page-sharing">
      <h4>Who may read this service</h4>
      <!--
        <b>Three cards, and it was a select of three.</b> Handoff 2026-09-04. The difference
        between private, organization and public is a sentence each, and a dropdown can carry a
        word — so the sentences were three paragraphs under the control, which is where an
        explanation goes when the control has no room for it. Radio inputs keep the arrow keys,
        the grouping and the applied-on-choice behaviour exactly as they were: the change handler
        reads the value off whatever fired, and a radio has one.

        <b>capSharing is still the id that is read.</b> loadServiceCapabilities sets it from
        the catalogue listing; a radio group has no single element to set, so the id stays on the
        group and the setter picks the member.
      -->
      <div class="scopecards" id="capSharing" role="radiogroup"
        aria-label="Who may read this service">
        ${[
          ["private", "Private", "The owner, and anybody with <i>view all content</i>."],
          ["organization", "Organization", "Anybody who can sign in."],
          ["public", "Public",
            "Anybody, without a token — what an ArcGIS client with no credential sees."],
        ].map(([value, label, said]) => `<label class="scopecard">
          <input type="radio" name="capSharing" value="${value}"
            data-service-sharing="${h(name || "")}" data-folder="${h(folder || "")}">
          <span><b>${label}</b><span class="said">${said}</span></span>
        </label>`).join("")}
      </div>
      <p class="hint">Applied the moment it is chosen, not on Save — an owner narrowing who may see
        a service has to be able to trust that it happened rather than press Save afterwards
        (ADR-031 §2b, the same rule the role select follows).</p>
      <p class="hint"><b>Shared into a group</b> is a fourth state and it is not set here: it is
        set on the item, and it adds readers on top of whichever of these three is chosen.</p>
      <p class="hint"><b>One scope per service, and every layer inside it is read under that
        scope.</b> There is no per-layer version: <code>service.sharing</code> is what the serving
        path reads, and the console used to offer this page once per layer — D-61.</p>
      <p class="hint"><b>A ceiling, not a grant.</b> <b>Private</b> is the owner plus anybody with
        <i>view all content</i>; <b>organization</b> is any signed-in member; <b>public</b> is
        anyone at all, including an anonymous caller. Sharing to public needs
        <code>sharing:shareToPublic</code>, which not every role carries.</p>
    </section>

    <section class="page" id="page-limits">
      <h4>Response</h4>
      <div class="setting"><span class="q">The most rows one response may carry:</span>
        <input type="number" id="capMaxRows" min="1" placeholder="50000"><span class="u">rows</span></div>
      <div class="setting"><span class="q">Rows returned when the caller does not ask:</span>
        <input type="number" id="capDefRows" min="1" placeholder="1000"><span class="u">rows</span></div>
      <div class="setting"><span class="q">The most one response body may reach:</span>
        <input type="number" id="capOutBytes" min="1" placeholder="67108864"><span class="u">bytes</span></div>

      <h4>Request</h4>
      <div class="setting"><span class="q">The most one request body may carry:</span>
        <input type="number" id="capInBytes" min="1" placeholder="unset"><span class="u">bytes</span></div>
      <div class="setting"><span class="q">The most edits one call may apply:</span>
        <input type="number" id="capEdits" min="1" placeholder="unset"><span class="u">edits</span></div>

      <h4>Time</h4>
      <div class="setting"><span class="q">The longest a client may use this service:</span>
        <input type="number" id="capDeadline" min="1" placeholder="600"><span class="u">seconds</span></div>
      <div class="setting"><span class="q">The longest one database statement may run:</span>
        <input type="number" id="capTimeout" min="1000" step="1000" placeholder="30000"><span class="u">ms</span></div>
      <p class="hint"><b>The two time limits are not the same limit.</b> The first bounds the whole
        request — reading it, querying, projecting, encoding and writing the answer. The second
        bounds one database statement, and stops counting the moment the query returns. A request
        that spends four minutes writing a million features exceeds the first and never touches the
        second.</p>
      <p class="hint">Empty means the server's own value. These are the service's, so they bound
        every layer inside it. Neither can raise the server's limit: a value above it is held down
        to it rather than refused.</p>
    </section>`;
}

/**
 * The choice a removal needs, with what is at stake named.
 *
 * <b>Both buttons, neither preselected.</b> ADR-015 §6c: the server refuses a removal that did
 * not say what to do, and the console's job is to put the question rather than to answer it —
 * a dialog with a default has answered it. The list of what is owned is above the buttons
 * because it is what the choice is about.
 */
function showRemoveMember(name, held) {
  const box = $("removeMember");

  $("removeWho").dataset.member = name;
  $("removeWho").innerHTML = `Removing <b>${h(name)}</b>.`;

  const list = [];
  if (held.services.length) list.push(`<li><b>${num(held.services.length)}</b> service(s): `
    + `${held.services.map(h).join(", ")}</li>`);
  if (held.folders.length) list.push(`<li><b>${num(held.folders.length)}</b> folder(s): `
    + `${held.folders.map(h).join(", ")}</li>`);
  if (held.groups) list.push(`<li><b>${num(held.groups)}</b> group(s)</li>`);

  $("removeHolds").innerHTML = list.join("");

  // <b>Only members who can sign in.</b> Transferring to a disabled account produces content
  // nobody can administer, and the server refuses it — offering it here would be a control
  // that exists to be told no.
  $("removeTo").innerHTML = `<option value="">choose a member…</option>`
    + (memberNames || []).filter(n => n !== name)
        .map(n => `<option value="${h(n)}">${h(n)}</option>`).join("");

  box.style.display = "";
}

/** The members a transfer may go to, kept from the last listing. */
let memberNames = [];

/**
 * Who can still sign in and administer, from the last listing.
 *
 * <b>D-101: the console knew this and asked anyway.</b> Removing the only administrator is
 * refused by the server, and the panel that opens first asks whether to transfer what they own
 * or delete it — so an operator could answer *delete*, watch every layer they own be
 * unpublished and every service removed, and only then be told the removal was never possible.
 * The listing carries the roles; there is no reason to find out from the failure.
 */
let administrators = [];

/**
 * The services that hold nothing, listed before anything is removed.
 *
 * <b>The Remove button is disabled until this has run and found something</b>, which is
 * the whole safety of the pair: an operator cannot sweep an estate they have not looked
 * at, and pressing Remove on an empty list would be a destructive verb that does nothing
 * — the shape that teaches people the button is harmless.
 */
async function loadEmptyServices() {
  const r = await api("/admin/featureservices/empty");
  const rows = r.empty || [];

  $("emptyRows").innerHTML = rows.length === 0
    ? `<tr><td colspan="3" class="empty">${h(r.note)}</td></tr>`
    : rows.map(e => `<tr>
        <td><span class="name">${h(e.name)}</span></td>
        <td class="val">${h(e.folder || "Site (root)")}</td>
        <td>${pill(e.sharing)}</td>
      </tr>`).join("");

  $("emptySweep").disabled = rows.length === 0;
  $("emptyWhen").textContent = rows.length === 0
    ? "nothing to remove"
    : `${num(rows.length)} to remove`;
}

/**
 * Reads a layer's symbology and draws all three of its faces.
 *
 * <b>The canonical document, the derived `drawingInfo`, and what the derivation cost.</b>
 * ADR-033 §7's second condition is that the conversion reports its losses; a page that
 * showed only the document a person wrote would leave them to discover the losses from a
 * client's rendering, which is the failure the condition exists to prevent.
 */
/**
 * Draws the colours an ArcGIS client will use, as colours.
 *
 * <b>D-99: the least visual page in a product about maps.</b> The derived renderer was shown
 * as raw JSON and a colour inside it as an RGBA quad — `[214, 92, 43, 255]` — which is a
 * number a person converts in their head, badly, or does not convert at all. The document
 * stays, because it is the exact thing a client receives and a reader checking a value needs
 * to see it; what changes is that the answer is above the evidence.
 *
 * <b>The three families ArcGIS understands, and nothing invented.</b> `simple` draws every
 * feature one way; `uniqueValue` and `classBreaks` each carry a list whose entries already
 * have the label a legend would print. So the labels are read rather than composed — a
 * swatch captioned by this console rather than by the renderer would be a second opinion
 * about what the layer means.
 *
 * <b>An outline is a colour too, and it is drawn as a border rather than as a second square.</b>
 * A fill and its outline are one symbol, and two squares side by side would read as two
 * classes.
 *
 * @param {object|null|undefined} drawingInfo What an ArcGIS client receives.
 */
function drawSwatches(drawingInfo) {
  const box = $("symSwatches");
  const renderer = drawingInfo && drawingInfo.renderer;
  const symbols = [];

  if (renderer && renderer.type === "simple") {
    symbols.push({ label: "Every feature", symbol: renderer.symbol });
  } else if (renderer && Array.isArray(renderer.uniqueValueInfos)) {
    for (const info of renderer.uniqueValueInfos) {
      symbols.push({ label: info.label || info.value, symbol: info.symbol });
    }
  } else if (renderer && Array.isArray(renderer.classBreakInfos)) {
    for (const info of renderer.classBreakInfos) {
      symbols.push({ label: info.label, symbol: info.symbol });
    }
  }

  // <b>Hidden rather than empty.</b> An empty strip under a heading reads as a layer with no
  // colours, which is a different thing from a renderer this console does not draw swatches
  // for — and the document below says which.
  const drawn = symbols.filter(s => rgba(s.symbol && s.symbol.color));
  box.hidden = drawn.length === 0;
  box.textContent = "";

  for (const { label, symbol } of drawn) {
    const chip = document.createElement("span");
    chip.className = "swatch";

    // <b>Two elements, because one cannot carry both.</b> The checkerboard is a background
    // image and the colour is a background colour, and a colour paints *behind* an image —
    // so a single element would show the checkerboard and hide the fill. The outer square
    // holds the texture, the inner one holds the paint.
    const patch = document.createElement("span");
    patch.className = "patch";

    // <b>The swatch takes the symbol's shape — D-99.</b> A square is right for a fill and
    // wrong for everything else: a road layer drawn as a filled square says the wrong thing
    // about what a client will see, and a point layer says it twice. ArcGIS already names the
    // shape — `esriSFS` fills, `esriSLS` strokes, `esriSMS` marks — so the shape is read
    // rather than guessed, which is the same rule the labels follow.
    const kind = typeof symbol.type === "string" ? symbol.type : "";
    if (kind === "esriSLS") patch.classList.add("stroke");
    else if (kind === "esriSMS") patch.classList.add("mark");

    const fill = document.createElement("span");
    fill.className = "fill";
    fill.style.background = rgba(symbol.color);
    patch.append(fill);

    // <b>A line's colour is its own, not an outline's.</b> `esriSLS` has no fill: its `color`
    // *is* the stroke, so the band drawn across the chip takes it and the border stays clear.
    const outline = symbol.outline && rgba(symbol.outline.color);
    if (outline && kind !== "esriSLS") {
      patch.style.borderColor = outline;
    }

    const text = document.createElement("span");
    text.textContent = label === undefined || label === null || label === ""
      ? "(no label)"
      : String(label);

    chip.append(patch, text);

    // <b>The quad stays reachable.</b> Somebody checking a value against a specification
    // wants the numbers, and hiding them behind a picture would be the opposite of this
    // page's job.
    chip.title = `[${symbol.color.join(", ")}]`;

    box.append(chip);
  }
}

/**
 * An ArcGIS colour as CSS, or null when it is not one.
 *
 * ArcGIS writes `[r, g, b, a]` with **a in 0–255**, which is the trap: passing it to CSS
 * unchanged makes every opaque colour 255 times too opaque, which browsers clamp to 1 and
 * every transparent one wrong. Divided here, once.
 *
 * @param {unknown} color The candidate.
 * @returns {string|null} A CSS colour, or null.
 */
function rgba(color) {
  if (!Array.isArray(color) || color.length < 3) return null;
  if (!color.slice(0, 3).every(n => typeof n === "number")) return null;

  const [r, g, b] = color;
  const a = typeof color[3] === "number" ? color[3] / 255 : 1;

  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

async function loadSymbology(name) {
  const state = $("symState");

  // <b>This load's ticket.</b> Anything that makes it the wrong answer — a newer load, or a
  // Store — bumps the counter, and every write below the next `await` is abandoned.
  const mine = ++symLoadingFor;
  const overtaken = () => mine !== symLoadingFor;

  // <b>A load starts the screen over.</b> A refusal from the last Store, a tab somebody left on
  // the derived document, a ground chosen for a different layer: none of them are facts about
  // what has just been read, and every one of them survived a navigation before this.
  symRefuse("");
  if ($("symOverride")) $("symOverride").hidden = true;
  symShowFold("", false);
  symShowInspector("classes");

  // <b>The map first, because the preview asks it for the frame.</b> Built once for the page and
  // framed on each layer; if OpenLayers does not load, both are no-ops and the picture is drawn
  // the way it was before there was a map.
  await symBuildMap();
  await symFrameMap(name);

  if (overtaken()) return;

  symShowGround("light");

  try {
    const r = await api(`/admin/layers/${encodeURIComponent(name)}/symbology`);

    if (overtaken()) return;

    $("symDoc").value = r.symbology ? JSON.stringify(r.symbology, null, 1) : "";

    $("symDerived").textContent = r.drawingInfo
      ? JSON.stringify(r.drawingInfo, null, 1)
      : "none — this layer's stored document could not be projected";

    drawSwatches(r.drawingInfo);

    // <b>The state line is written before the slower half.</b> The fields and the preview are
    // two more round trips; a reader who has already been told *a stored document, 138 bytes*
    // is not waiting for them to know what they are looking at, and a line still reading
    // *Reading…* while the derived document is on screen is the page contradicting itself.
    state.innerHTML = r.stored
      ? `A stored document, ${num(JSON.stringify(r.symbology).length)} bytes. `
        + `Both faces are derived from it.`
      : `<b>Generated.</b> No document is stored, so this layer is drawn in a colour `
        + `derived from its name — the same colour tomorrow and on another deployment. `
        + `Storing one replaces it.`;

    // <b>Said here, because this is where somebody would be confused.</b> A service-wide style
    // override wins for the tile face (ADR-033 §5d), so a layer whose symbology is stored and
    // correct can still be drawn by something else on a map — with nothing on this screen to
    // explain it. The check is one GET and it only speaks when there is something to say.
    if (r.service) {
      try {
        const service = await api(
          `/admin/services/${encodeURIComponent(r.service)}/style`);

        // <b>A banner rather than a sentence appended to the state line.</b> The state line is
        // in the Document tab now, and this fact is not about the document: it says the picture
        // beside it is not what a map draws. Something that contradicts what is on the screen
        // has to be on the screen.
        if (service && service.stored && !overtaken()) {
          const note = $("symOverride");

          if (note) {
            note.innerHTML = `<span><b>This layer's service carries a style override</b>, so `
              + `the tile face draws it rather than this document. The ArcGIS feature face `
              + `still reads this one. The override is on the service's Visualization page.`
              + `</span>`;

            note.hidden = false;
          }
        }
      } catch {
        // A service that cannot be read is the service page's problem to report, not this
        // one's: a layer's own symbology is what this screen is about.
      }
    }

    // <b>The layer's fields, for the two families that classify by one.</b> The layer document
    // is where the console already reads them, so this is the same call the Data page makes
    // rather than a second answer to the same question.
    symFields = [];

    try {
      const described = await api(`${layerUrl(name).replace(location.origin, "")}?f=json`);
      symFields = (described.fields || []).map(f => ({ name: f.name, type: f.type }));
    } catch {
      // A layer whose document cannot be read still gets the single-symbol controls, and the
      // field picker says so rather than offering an empty list that looks like no fields.
    }

    if (overtaken()) return;

    symGeometry = r.geometry || "";

    // <b>The stored CIM, not the derived `drawingInfo`.</b> The derived one is flattened to a
    // single symbol for the Esri face; filling the form from it would throw away every symbol
    // layer past the first the moment anybody pressed Store.
    fillSymbologyForm(r.symbology, symGeometry);
    await drawSymbologyPreview(name, JSON.stringify(r.symbology || r.drawingInfo));

    if (overtaken()) return;

    symStored = !!r.stored;
    symEditedSince = false;
    symSayState();

    // <b>An unstyled layer opens on the fact rather than on a form.</b> The controls behind it
    // are already holding the generated document, so this stands in front of them rather than
    // instead of them: the two buttons dismiss it and nothing is fetched.
    symShowEmpty(!r.stored);

    // <b>Generated is stated, not implied by an empty box.</b> §5b makes a generated
    // appearance a real answer with a version of 0, and a reader who sees nothing cannot
    // tell that from a layer whose style failed to load.
    drawLosses(r.losses);
  } catch (e) {
    if (overtaken()) return;

    state.textContent = e.message;
    $("symDerived").textContent = "—";
    drawLosses([]);

    // <b>The refusal banner, because the state line is behind a tab.</b> A layer whose
    // symbology could not be read shows three empty columns otherwise, which reads as a layer
    // with nothing in it rather than as a request that failed.
    symRefuse(e.message || "The symbology could not be read.");
    symShowEmpty(false);
  }
}

/**
 * Wires the symbology form once, on the document.
 *
 * <b>Delegated, because every row is rebuilt whenever anything changes.</b> Binding to the
 * inputs themselves would mean rebinding after every edit, which is the shape that leaves one
 * control dead and looks like a control that does nothing — this console has met that three
 * times.
 *
 * <b>Every handler edits the model and then calls `symSettled`.</b> One path from a control to
 * the document box and the picture means the three cannot drift: there is no branch where a
 * change is drawn and not stored, or stored and not drawn.
 */
function wireSymbologyForm() {
  document.addEventListener("change", async e => {
    if (!(e.target instanceof Element) || !e.target.closest("#page-symbology")) return;
    if (e.target.id === "symDoc" || symFilling || !symModel) return;

    if (e.target.name === "symKind") {
      symFamily(e.target.value);
      await symSettled({ classes: true, stack: true });

      return;
    }

    if (e.target.id === "symVaryWhat") {
      if (e.target.value !== "") varyStarted(e.target.value);

      varyFromForm();
      await symSettled({ vary: true });

      return;
    }

    if (e.target.closest("#symVaryRows")) {
      varyFromForm();
      await symSettled({});

      return;
    }

    if (e.target.id === "symField") {
      if (symModel.type === "CIMUniqueValueRenderer") symModel.fields = symChosenFields();
      if (symModel.type === "CIMClassBreaksRenderer") symModel.field = e.target.value;

      await symSettled({});

      return;
    }

    await symEdited(e.target, true);
  });

  document.addEventListener("input", e => {
    if (!(e.target instanceof Element) || !e.target.closest("#page-symbology")) return;
    if (e.target.id === "symDoc" || symFilling || !symModel) return;

    clearTimeout(symDebounce);

    if (e.target.closest("#symVaryRows")) {
      symDebounce = setTimeout(async () => {
        varyFromForm();
        await symSettled({});
      }, 250);

      return;
    }

    symDebounce = setTimeout(() => symEdited(e.target, false), 250);
  });

  // <b>The document box is the one control that edits the model wholesale.</b> ADR-051 §3.3
  // used to leave the form alone when somebody typed here, because a MapLibre expression had no
  // checkbox. The canonical document is CIM now and the model keeps whatever it is handed —
  // including the parts the form has no box for — so adopting the text is safe and the two
  // stop being able to disagree. ADR-052 §3.7.
  // <b>`input`, not `click`.</b> The filter's first version was a case in the click handler,
  // which fires when somebody puts the caret in the box and never again while they type -- so
  // the list stayed whole and the box looked broken. There is no debounce: the rows are already
  // in the page and hiding them is a re-render of at most 256 elements, which is under a frame.
  document.addEventListener("input", e => {
    if (!(e.target instanceof Element) || e.target.id !== "symFilter" || !symModel) return;

    drawSymbologyClasses(symKindValue());
  });

  document.addEventListener("input", e => {
    if (!(e.target instanceof Element) || e.target.id !== "symDoc" || !editing) return;

    clearTimeout(symDebounce);

    symDebounce = setTimeout(async () => {
      if (!editing || !$("symDoc")) return;

      let typed = null;

      try {
        typed = JSON.parse($("symDoc").value);
      } catch {
        // Half-typed JSON is the normal state of a box somebody is editing; the picture waits.
        return;
      }

      fillSymbologyForm(typed, symGeometry);

      symEditedSince = true;

      symSayState();

      await drawSymbologyPreview(editing.name, $("symDoc").value);
    }, 400);
  });

  // <b>Enter and Space open a class row.</b> The row carries `tabindex` and a role, and a
  // control a keyboard can reach and cannot use is worse than one it cannot reach: it takes the
  // focus and then does nothing.
  // <b>Selecting a class by focusing anything in it.</b> This replaced a `tabindex` on the row
  // itself plus an Enter/Space handler, which cost a tab stop per row and, at 256 classes, put
  // 1,280 of them between the filter and the Store button.
  //
  // <b>The list is not redrawn, and that is the whole difficulty.</b> Selecting through the
  // click handler calls `symSettled`, which rebuilds the rows — and rebuilding them while one of
  // their inputs has the focus takes the focus away, so a keyboard user would be thrown out of
  // the box they had just reached. The highlight is moved in place and only the panel below is
  // redrawn.
  document.addEventListener("focusin", e => {
    if (!(e.target instanceof Element) || !symModel) return;

    const row = e.target.closest(".symclass");

    if (!row || !row.closest("#page-symbology")) return;

    const at = Number(row.dataset.class);

    if (!Number.isInteger(at) || at === symClassIndex) return;

    symClassIndex = at;

    for (const one of document.querySelectorAll("#symClasses .symclass")) {
      const mine = Number(one.dataset.class) === at;

      one.classList.toggle("symchosen", mine);
      one.setAttribute("aria-current", mine ? "true" : "false");
    }

    // <b>The stack follows the selection again.</b> It is under the list rather than instead of
    // it, so a row that is marked and a symbol panel showing a different class would be two
    // statements about the same thing on one screen. Redrawing here is what stops that; the
    // heading names the class as well, so the mark is a second signal rather than the only one.
    symShowDetail(true);
    drawSymbolLayers();
    drawSymbolGallery();
  });

  document.addEventListener("click", async e => {
    const t = e.target;

    if (!(t instanceof Element) || !t.closest("#page-symbology") || !symModel) return;

    const row = t.closest(".symclass");

    if (row && !t.matches("input, button")) {
      symClassIndex = Number(row.dataset.class) || 0;
      await symSettled({ classes: true, stack: true, quiet: true });

      return;
    }

    if (t.id === "symField2" || t.id === "symField3") {
      // The third picker appears once the second is answered.
      drawExtraFields(symChosenFields());

      return;
    }

    if (t.id === "symClassify") {
      e.preventDefault();
      await symClassify();

      return;
    }

    if (t.id === "symAllAlphaApply") {
      await symAlphaForAll();

      return;
    }

    // <b>The rail's two folded sections, and the override under them.</b> Handoff revision
    // 2026-09-04: *Vary with a number* and *Symbol sets* were the two blocks that made a
    // 264-pixel column read as a wall of controls, and each of them summarises its own state on
    // its own row — so a reader who has not asked for either can see what they hold without
    // opening them.
    //
    // <b>One at a time, which is the prototype's own rule.</b> Two open at once is the wall
    // again, in a column that also has to hold the renderer and the layer list.
    const fold = t.closest(".symfoldhead, #symOverrideHead");

    if (fold) {
      e.preventDefault();
      symShowFold(fold.id, fold.getAttribute("aria-expanded") !== "true");

      return;
    }

    // <b>The inspector's tabs.</b> Three panes over one column: the classes, the document Store
    // sends, and the projection an ArcGIS client reads. They were a list, a disclosure triangle
    // and two sections at the bottom of the page.
    const tab = t.closest("#symInspTabs a");

    if (tab) {
      e.preventDefault();
      symShowInspector(tab.dataset.insp);

      return;
    }

    // <b>What is drawn under the layer, and it costs no request.</b> The preview is a
    // transparent PNG, so the ground is this page's own background — which is the only way to
    // find out that a pale fill is invisible on white without storing it first.
    const ground = t.closest("#symGround a");

    if (ground) {
      e.preventDefault();
      symShowGround(ground.dataset.ground);

      return;
    }

    if (t.id === "symStartGenerated" || t.id === "symPasteDoc") {
      e.preventDefault();

      // <b>The form is already holding the generated document.</b> The server answers a layer
      // with no stored symbology with the generated CIM rather than with nothing (ADR-052), so
      // *start from it* is this screen getting out of the way, not a document being fetched.
      symShowEmpty(false);

      if (t.id === "symPasteDoc") {
        symShowInspector("document");
        $("symDoc").focus();
      }

      return;
    }

    if (t.id === "symRefusalClose") {
      e.preventDefault();
      $("symRefusal").hidden = true;

      return;
    }

    if (t.id === "symAddClass") {
      e.preventDefault();
      symAddClass();
      await symSettled({ classes: true, stack: true });

      return;
    }

    if (t.classList.contains("symdrop")) {
      e.preventDefault();
      symRemoveClass(Number(t.dataset.class));
      await symSettled({ classes: true, stack: true });

      return;
    }

    const card = t.closest("[data-symbol]");

    if (card) {
      e.preventDefault();

      if (symApplyLibrary(card.dataset.symbol)) {
        await symSettled({ classes: true, stack: true });
      }

      return;
    }

    if (t.dataset.addLayer) {
      e.preventDefault();
      symStack().push(symNewLayer(t.dataset.addLayer));
      await symSettled({ classes: true, stack: true });

      return;
    }

    if (t.classList.contains("symlayerdrop")) {
      e.preventDefault();
      symStack().splice(Number(t.dataset.layer), 1);
      await symSettled({ classes: true, stack: true });

      return;
    }

    if (t.classList.contains("symup") || t.classList.contains("symdown")) {
      e.preventDefault();

      const layers = symStack();
      const at = Number(t.dataset.layer);
      const to = t.classList.contains("symup") ? at - 1 : at + 1;

      if (to >= 0 && to < layers.length) {
        [layers[at], layers[to]] = [layers[to], layers[at]];
        await symSettled({ classes: true, stack: true });
      }
    }
  });
}

/** How many classes the last classification made, and from which field, for its own sentence. */
let symClassCount = 0;
let symClassField = "";

/** The selected class's symbol layers, ready to be mutated. */
function symStack() {
  const cls = symClasses()[symClassIndex];

  return cls ? symSymbolOf(cls).symbolLayers : [];
}

/**
 * Applies one control's value to the model.
 *
 * @param {Element} control what changed
 * @param {boolean} settled true for `change`, false for a debounced `input`
 */
async function symEdited(control, settled) {
  if (!symModel || !control.isConnected) return;

  const classes = symClasses();
  const layers = symStack();

  if (control.classList.contains("symfill")) {
    // The class swatch sets the topmost painted layer, which is the one a reader sees.
    const paint = symLayerColour(
      symSymbolOf(classes[Number(control.dataset.class)] || classes[0])?.symbolLayers[0]);

    if (paint) paint.color = symCimColour(control.value, paint.color);
  } else if (control.classList.contains("symlayercolour")) {
    const paint = symLayerColour(layers[Number(control.dataset.layer)]);

    if (paint) paint.color = symCimColour(control.value, paint.color);
  } else if (control.classList.contains("symalpha")) {
    // <b>The alpha is on the colour, not beside it.</b> A CIMRGBColor is four numbers and the
    // fourth is opacity; there is no separate opacity property on a symbol layer to set, so this
    // box edits `values[3]` of whichever colour the layer is painted with.
    const paint = symLayerColour(layers[Number(control.dataset.layer)]);

    if (paint && paint.color) {
      // <b>An emptied box falls back to the opacity that is there, not to opaque.</b> Clearing
      // the field to type a new number passes through `""`, and answering that with 100 makes a
      // faded symbol flash solid mid-keystroke — then stay solid if the reader gives up and
      // clicks away, having changed a value they were only about to change.
      paint.color = symCimColour(
        symCimHex(paint.color), null, symPercent(control.value, symCimAlpha(paint.color)));
    }
  } else if (control.classList.contains("symwidth")) {
    const layer = layers[Number(control.dataset.layer)];

    if (layer) layer.width = Number(control.value) || 0;
  } else if (control.classList.contains("symsize")) {
    const layer = layers[Number(control.dataset.layer)];

    if (layer) layer.size = Number(control.value) || 1;
  } else if (control.classList.contains("symlabel")) {
    const cls = classes[Number(control.dataset.class)];

    if (cls) cls.label = control.value;
  } else if (control.classList.contains("symvalue")) {
    const cls = classes[Number(control.dataset.class)];

    if (!cls) return;

    if (symModel.type === "CIMClassBreaksRenderer") {
      cls.upperBound = Number(control.value) || 0;
    } else {
      cls.values = [{ type: "CIMUniqueValue", fieldValues: [control.value] }];
    }
  } else {
    return;
  }

  // <b>Rows are redrawn on `change`, not on every keystroke.</b> Rebuilding the list while
  // somebody is typing in it takes the caret with it.
  await symSettled({ classes: settled, stack: settled, keep: settled ? null : control.id });
}

/**
 * The one path from an edit to the document, the rows and the picture.
 *
 * @param {{classes?: boolean, stack?: boolean, quiet?: boolean}} what to redraw
 */
async function symSettled(what) {
  if (!symModel || !editing) return;

  symFilling = true;

  try {
    if (what.classes) drawSymbologyClasses(symKindValue());
    if (what.stack) drawSymbolLayers();
    if (what.vary) drawVarying();

    // <b>Written on every settle, because the fold is usually closed.</b> A summary corrected
    // only by the function that happens to produce it is the shape D-219 recorded five times on
    // this very screen.
    symSaySummaries();
  } finally {
    symFilling = false;
  }

  if ($("symDoc")) $("symDoc").value = JSON.stringify(symModel, null, 1);

  // <b>One funnel, so one place records that the document has moved.</b> Every control on this
  // page reaches the model through here, which is why the flag is set here rather than in each
  // handler: a caption that has to be corrected by twenty callers is a caption that will be
  // wrong after the twenty-first is written. A quiet settle is a selection, which changes
  // nothing about the document.
  if (!what.quiet) {
    symEditedSince = true;

    symSayState();

    await drawSymbologyPreview(editing.name, $("symDoc").value);
  }
}

/**
 * Turns the model into another renderer family, keeping the symbol somebody built.
 *
 * <b>The first class's symbol survives the change.</b> Losing it would mean choosing *by value*
 * threw away the colours already chosen, which is the moment somebody stops trusting the form.
 */
function symFamily(kind) {
  const first = symClasses()[0];
  const symbol = first ? JSON.parse(JSON.stringify(symSymbolOf(first))) : null;
  const wrap = () => ({ type: "CIMSymbolReference", symbol: JSON.parse(JSON.stringify(symbol)) });

  if (kind === "simple") {
    symModel = { type: "CIMSimpleRenderer", label: "", description: "", symbol: wrap() };
  } else if (kind === "uniqueValue") {
    symModel = {
      type: "CIMUniqueValueRenderer",
      fields: [$("symField").value || (symFields[0] || {}).name || ""],
      groups: [{ classes: [{
        label: "", visible: true,
        values: [{ type: "CIMUniqueValue", fieldValues: [""] }],
        symbol: wrap(),
      }] }],
    };
  } else {
    symModel = {
      type: "CIMClassBreaksRenderer",
      field: $("symField").value || (symFields[0] || {}).name || "",
      breaks: [{ upperBound: 0, label: "", symbol: wrap() }],
    };
  }

  symClassIndex = 0;

  $("symFieldRow").hidden = kind === "simple";
  symShowClassify(kind);

  // <b>Changing the family goes back to the list.</b> The class being edited belonged to the
  // renderer that has just been replaced, so staying in its symbol would leave the editor open
  // on whatever now sits at that index — a different class wearing the same number. This also
  // owns `symClassActions`, which two other places used to set for themselves.
  symShowDetail(false);

  // <b>Redrawn, because the two families can use different fields.</b> Switching to ranges with
  // a text field selected would otherwise leave the name of a column the new family cannot
  // classify sitting in the picker. The extra pickers go with it: only the unique-value family
  // has anything to do with them.
  drawSymbologyFields($("symField").value);
  drawExtraFields(symChosenFields());
}

/**
 * Shows the classifier for the two families that classify, and only the parts each uses.
 *
 * <b>A unique-value renderer has nothing to choose.</b> Its classes are the field's distinct
 * values -- there is no method and no count, so offering either would be offering a control that
 * changes nothing. ADR-034's rule, applied inside one row rather than to the row.
 *
 * @param {string} kind the renderer family
 */
function symShowClassify(kind) {
  const row = $("symClassifyRow");

  if (!row) return;

  row.hidden = kind === "simple";
  $("symMethod").hidden = kind !== "classBreaks";
  $("symClassCount").hidden = kind !== "classBreaks";

  // <b>And the word in front of them, which stayed when they went.</b> *Into* names the count
  // and the method; with both hidden it sat alone in front of a button, labelling nothing. A
  // unique-value renderer is not classified into anything — it reads the values there are.
  const label = $("symClassifyLabel");

  if (label) label.hidden = kind !== "classBreaks";

  $("symClassify").textContent = kind === "uniqueValue"
    ? "Read the values"
    : "Read the data";
}

/**
 * Every field the form is classifying by, in order, with the empty ones dropped.
 *
 * <b>Order is the whole point.</b> A class of a two-field renderer is a pair, and the pair is
 * read in the order the fields are listed — swap them and every class key changes.
 *
 * @returns {Array<string>} one to three field names
 */
function symChosenFields() {
  return [$("symField"), $("symField2"), $("symField3")]
    .map(box => (box && box.value) || "")
    .filter(name => name.length > 0);
}

/**
 * Asks the server what this field's classes are, and fills the form with the answer.
 *
 * <b>A GET, and it stores nothing.</b> The answer goes into the form and the document box; the
 * operator still presses Store, which is the same rule every other control on this page follows.
 * It also means the console's test harness -- which traps every write -- can exercise this the
 * way a person does.
 *
 * <b>The server does the arithmetic, not this file.</b> `/admin/layers/{name}/classify` is one
 * door onto the classifier that `generateRenderer` is the other door onto (ADR-052 §3.13). Seven
 * classification methods in JavaScript would be a second implementation of Fisher's algorithm
 * that nothing compares against the first.
 */
async function symClassify() {
  const says = $("symClassifySays");
  const button = $("symClassify");
  const kind = symKindValue();
  const field = $("symField").value;

  if (!editing || kind === "simple") return;

  if (!field) {
    says.hidden = false;
    says.textContent = "Pick a field first — the classes are of something.";

    return;
  }

  const asked = new URLSearchParams({
    type: kind,
    field: kind === "uniqueValue" ? symChosenFields().join(",") : field,
    delimiter: ", ",
    method: $("symMethod").value,
    classes: $("symClassCount").value || "5",
  });

  button.disabled = true;
  says.hidden = false;
  says.textContent = "Reading the field…";

  try {
    const r = await api(
      `/admin/layers/${encodeURIComponent(editing.name)}/classify?${asked}`);

    // <b>The harness answers every trapped write with `{}`, and a GET is not trapped —</b>
    // but a proxy in front of this console might answer with something else entirely, and
    // filling the form from a document that is not one would empty it silently.
    if (!r || !r.symbology || !r.symbology.type) {
      says.textContent = "The server answered without a renderer, so nothing was changed.";

      return;
    }

    fillSymbologyForm(r.symbology, symGeometry);
    await symSettled({ classes: true, stack: true, vary: true });

    const made = symClasses().length;

    // <b>It says what it made, and no longer says whether it is saved.</b> That clause was
    // correct the instant it was written and wrong forever afterwards, because nothing cleared
    // it when a Store succeeded — a second, independently maintained copy of a fact the state
    // line above already keeps, which is how a page comes to contradict itself in writing.
    symClassCount = made;
    symClassField = field;

    says.textContent = `${num(made)} class${made === 1 ? "" : "es"} from ${h(field)}.`
      + ((r.losses || []).length > 0 ? ` ${r.losses.length} thing(s) could not be carried.` : "");
  } catch (err) {
    // The server's own sentence. It says which field, which method and what the data could not
    // carry, and every one of those is more useful than "could not classify".
    says.textContent = err.message || String(err);
  } finally {
    button.disabled = false;
  }
}

/**
 * Holds the class list to ten rows, measured rather than assumed.
 *
 * <b>Ten by owner decision 2026-09-04</b>, looking at 256 rows and then at twenty: *"20 bile
 * uzun"*. It cannot be a length in the stylesheet because a row's height depends on the font,
 * the theme and the browser's zoom — so this measures a row that is already on screen and sets
 * the box to ten of it, after the rows are in place so there is always one to measure.
 *
 * <b>Nothing at all when it fits.</b> A scroller around eight rows is a frame around nothing.
 *
 * @param {Element} box the class list
 * @param {number} rows how many rows are about to be in it
 */
function symBoxToTenRows(box, rows) {
  const TEN = 10;

  if (rows <= TEN) {
    box.classList.remove("symscrolls");
    box.style.maxHeight = "";

    return;
  }

  const one = box.querySelector(".symclass");
  const tall = one ? one.getBoundingClientRect().height : 0;

  box.classList.add("symscrolls");

  // Eight pixels for the box's own padding, so the tenth row is whole rather than a sliver.
  box.style.maxHeight = tall > 0 ? `${Math.round((tall * TEN) + 8)}px` : "";
}

/**
 * Everything about a class a person would type to find it, lowercased.
 *
 * <b>Value and label both.</b> They are usually the same when a classification is generated and
 * usually different once somebody has named the classes; searching only one would find nothing
 * for whichever half they remember.
 *
 * @param {object} cls the class
 * @returns {string} its searchable text
 */
function symClassText(cls) {
  const values = (cls.values || [])
    .map(v => ((v.fieldValues || [])).join(" "))
    .join(" ");

  return `${values} ${cls.label || ""} ${cls.upperBound ?? ""}`.toLowerCase();
}

/**
 * Shows the filter once there are enough classes for it to be worth anything, and says how many.
 *
 * <b>Hidden below thirteen.</b> A search box over eight rows is a control that costs a reader
 * attention and saves them nothing, which is ADR-034's rule about drawing a control for
 * something that is not there.
 *
 * @param {number} total how many classes the renderer has
 * @param {number} showing how many the filter leaves
 */
function symShowFilter(total, showing) {
  const row = $("symFilterRow");
  const count = $("symShowing");

  if (!row || !count) return;

  // <b>Exactly when the list stops fitting.</b> Ten rows are shown, so eleven is the first
  // count at which something is out of sight — and the moment somebody needs a way to reach it
  // that is not scrolling. Twelve was an invented threshold and did not line up with anything.
  row.hidden = total <= 10;

  count.textContent = total === showing
    ? `${num(total)} classes`
    : `${num(showing)} of ${num(total)}`;
}

/** Another class, built from the one that is selected so its symbol carries over. */
function symAddClass() {
  const classes = symClasses();
  const like = classes[symClassIndex] || classes[0];
  const symbol = like
    ? JSON.parse(JSON.stringify(like.symbol))
    : { type: "CIMSymbolReference", symbol: null };

  if (symModel.type === "CIMUniqueValueRenderer") {
    symModel.groups = symModel.groups || [{ classes: [] }];
    symModel.groups[0].classes = symModel.groups[0].classes || [];
    symModel.groups[0].classes.push({
      label: "", visible: true,
      values: [{ type: "CIMUniqueValue", fieldValues: [""] }],
      symbol,
    });
  } else if (symModel.type === "CIMClassBreaksRenderer") {
    symModel.breaks = symModel.breaks || [];
    symModel.breaks.push({
      upperBound: (classes[classes.length - 1]?.upperBound || 0) + 1,
      label: "",
      symbol,
    });
  } else {
    return;
  }

  symClassIndex = symClasses().length - 1;
  symScrollToChoice = true;
}

/** Whether the next draw should bring the selected class into view. */
let symScrollToChoice = false;

/**
 * The class list and the selected class's symbol, one above the other in the inspector.
 *
 * <b>They were two views and they are one column now.</b> D-217 made them alternate, because a
 * permanently rendered symbol panel could be titled after a row that had scrolled out of sight
 * — *Symbol layers — Ankara* over a list showing ten classes of two hundred and fifty-six, none
 * of them Ankara. That fault was real and this does not bring it back: the two are adjacent
 * inside one 336-pixel column, the chosen row keeps its accent edge, and every move of the
 * selection scrolls it into view. What alternating cost was a click to see what a class is made
 * of, on the screen whose complaint was that it took too many.
 *
 * <b>A simple renderer has no list.</b> Its one row is the colour of the layer, so the heading
 * over the stack says *Symbol* rather than naming a class that is not one of several.
 *
 * @param {boolean} detail kept for the callers that ask for the stack to be redrawn; the two
 *   are no longer alternatives, so it only decides whether the selection is scrolled to
 */
function symShowDetail(detail) {
  const simple = !symModel || symModel.type === "CIMSimpleRenderer";

  symInDetail = !simple && !!detail;

  for (const [id, on] of [
    ["symClasses", true],
    ["symClassActions", !simple],
    ["symAllRow", !simple && symClasses().length > 1],
    ["symDetail", true],
    ["symStackHead", true],
  ]) {
    if ($(id)) $(id).hidden = !on;
  }

  const which = $("symDetailWhich");

  if (which) {
    which.textContent = simple
      ? "Symbol"
      : `Symbol · ${symClassLabel(symClasses()[symClassIndex], symClassIndex)}`;
  }

  // <b>*Classes* over one row is a tab lying about what is behind it.</b> A simple renderer
  // has one symbol and no classes, which is the whole difference between it and the other two.
  const tab = $("symTabClasses");

  if (tab) tab.firstChild.textContent = simple ? "Symbol" : "Classes";

  // <b>And the pane does not stretch for one row.</b> The stack is pinned to the bottom of the
  // pane so a long class list gives up the room rather than pushing it under the fold; with one
  // row above it that pin is 500 pixels of nothing between the colour and the symbol it
  // belongs to, which reads as a panel that failed to draw.
  const pane = $("insp-classes");

  if (pane) pane.classList.toggle("onesymbol", simple);
}

/**
 * Writes the strip's state line, and its own tooltip with it.
 *
 * <b>The line is 44 pixels tall and beside two buttons, so it can be cut.</b> Measured at 1440:
 * the strip wants 1,370 pixels of content and has 1,208, and this is the only child of it that
 * is prose. Cutting it silently would lose *this is what Store would keep* — which is the
 * sentence the button next to it is about — so the whole of it is in the title.
 */
function symSayState() {
  const line = $("symPreviewState");

  if (!line) return;

  const said = symPreviewSays();

  line.textContent = said;
  line.title = said;
}

/**
 * Opens one of the rail's folded sections and closes the others.
 *
 * <b>One at a time, and closed on arrival.</b> The two folds and the service override are the
 * three things in this column a reader does not need in order to change a colour, so none of
 * them is open when the editor opens. Opening a second would put the column back where it was.
 *
 * @param {string} which the head button's id
 * @param {boolean} open whether to open it, or close everything
 */
function symShowFold(which, open) {
  const folds = [
    ["symVaryHead", "symVaryBody"],
    ["symSetsHead", "symSetsBody"],
    ["symOverrideHead", "symOverrideBody"],
  ];

  for (const [head, body] of folds) {
    const on = open && head === which;
    const button = $(head);
    const box = $(body);

    if (button) button.setAttribute("aria-expanded", on ? "true" : "false");
    if (box) box.hidden = !on;

    const caret = button && button.querySelector(".caret");

    if (caret) caret.innerHTML = on ? "&#9662;" : "&#9656;";
  }
}

/**
 * What each folded section says about itself while it is closed.
 *
 * <b>A disclosure owes a summary.</b> A row that says only *Vary with a number* asks the reader
 * to open it to find out whether anything is varying, which is the cost the fold was meant to
 * remove. *nothing* in grey and *its colour, by population* in ink answer it from the row.
 */
function symSaySummaries() {
  const vary = $("symVarySays");

  if (vary) {
    const what = $("symVaryWhat");
    const chosen = what ? what.value : "";
    const field = ($("symVaryField") || {}).value || "";

    const said = {
      colour: "its colour",
      size: "its width or size",
      opacity: "how solid it is",
    }[chosen];

    vary.textContent = said
      ? (field ? `${said}, by ${field}` : said)
      : "nothing";

    vary.classList.toggle("quiet", !said);
  }

  const sets = $("symSetsSays");

  if (sets) {
    const shape = symKindOfGeometry(symGeometry);
    const set = SYMBOL_LIBRARY.find(one => one.shape === shape);

    // The word is the geometry's, not the library's internal name: a reader picking a symbol
    // for a road is choosing between lines, whatever the array is called.
    const noun = shape === "marker" ? "points" : shape === "line" ? "lines" : "polygons";

    sets.textContent = set ? `${num(set.symbols.length)} for ${noun}` : "";
  }
}

/** Which of the inspector's three panes is showing. */
let symInspector = "classes";

/**
 * Moves the inspector between its three panes.
 *
 * <b>Not a router, and that is deliberate.</b> A layer's page is addressable and its symbology
 * page is addressable; which of three read-outs of the same document is on the right of it is
 * not a place, and putting it in the address would make Back walk through tab presses.
 *
 * @param {string} which `classes`, `document` or `arcgis`
 */
function symShowInspector(which) {
  const panes = ["classes", "document", "arcgis"];

  if (!panes.includes(which)) return;

  symInspector = which;

  for (const pane of panes) {
    const box = $(`insp-${pane}`);
    if (box) box.hidden = pane !== which;
  }

  for (const link of document.querySelectorAll("#symInspTabs a")) {
    if (link.dataset.insp === which) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
}

/**
 * What is painted behind the preview.
 *
 * <b>Three grounds, none of them a request.</b> The picture the server draws is cleared to
 * transparent, so the frame's own background is what shows through the gaps — and a fill at 45%
 * alpha over white is a different thing to look at than the same fill over navy. This console
 * could not answer *will anybody see this on a dark basemap* at all before.
 *
 * @param {string} which `light`, `dark` or `none`
 */
function symShowGround(which) {
  const box = $("symPreviewBox");

  if (!box || !["light", "dark", "none"].includes(which)) return;

  box.classList.remove("ground-light", "ground-dark", "ground-none");
  box.classList.add(`ground-${which}`);

  for (const link of document.querySelectorAll("#symGround a")) {
    if (link.dataset.ground === which) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
}

/**
 * The generated-appearance screen, instead of an editor nobody has written a document into.
 *
 * <b>§5b says a generated appearance is an answer, so it is one screen rather than a sentence
 * over a full form.</b> The form behind it is already filled with the generated document — the
 * server answers an unstyled layer with it — so dismissing this is the only thing either button
 * has to do.
 *
 * @param {boolean} on whether to stand in front of the editor
 */
function symShowEmpty(on) {
  const empty = $("symEmpty");
  const cols = $("symCols");

  if (!empty || !cols) return;

  empty.hidden = !on;

  // <b>A class on the grid, not a hidden grid.</b> The rail stays: it says which layer is being
  // edited and what the service's own style is, and neither is a claim about the document this
  // screen is reporting the absence of.
  cols.classList.toggle("generated", on);

  // <b>The swatch is the colour the sentence beside it is about.</b> It was a hatched
  // rectangle, on a screen whose one claim is that the colour is derived from the layer's
  // identity — so the one thing that could have shown that claim was showing nothing.
  const sw = $("symEmptySwatch");

  if (on && sw) {
    const first = symClasses()[0];
    const paint = first
      ? symLayerColour(symThematicLayer(symSymbolOf(first).symbolLayers))
      : null;

    sw.innerHTML = `<i class="${symSwatchShape(symGeometry)}"
      style="--sw:${h(symCimHex(paint && paint.color))}"></i>`;
  }
}

/**
 * The server's refusal, said where the document was typed.
 *
 * <b>Shown rather than toasted.</b> A toast is right for something that happened to a layer
 * while the reader was looking elsewhere; a refusal to store what is on the screen is about the
 * screen, and it has to still be readable while they fix it.
 *
 * @param {string} why the server's own sentence, or an empty string to clear it
 */
function symRefuse(why) {
  const box = $("symRefusal");

  if (!box) return;

  if (!why) {
    box.hidden = true;
    box.innerHTML = "";

    return;
  }

  box.innerHTML = `<b>Not stored.</b> <span>${h(why)}</span>`
    + `<button class="tiny ghost" id="symRefusalClose">Dismiss</button>`;

  box.hidden = false;
}

/**
 * Sets every class's every painted layer to one opacity.
 *
 * <b>It sets rather than scales, and that is the decision.</b> A control that multiplied each
 * class's own alpha by a fraction would read as *fade the whole layer* and keep the relative
 * differences somebody had chosen — but it is not idempotent: applying 50 % twice leaves 25 %,
 * so the number in the box has no stable meaning and the only way back is to remember how many
 * times it was pressed. Setting is repeatable, reversible by setting it again, and says exactly
 * what it did.
 *
 * <b>Every painted layer of every class</b>, because a symbol is a stack: a fill under a stroke
 * where only the fill faded would change the drawing rather than fade it.
 *
 * @returns {Promise<void>} when the document and the picture have caught up
 */
async function symAlphaForAll() {
  const box = $("symAllAlpha");
  const says = $("symAllSays");

  if (!box || !symModel) return;

  const typed = String(box.value).trim();

  if (typed === "" || !Number.isFinite(Number(typed))) {
    if (says) says.textContent = "Type an opacity first.";

    return;
  }

  const alpha = symPercent(typed, 100);
  let touched = 0;

  for (const cls of symClasses()) {
    for (const layer of symSymbolOf(cls).symbolLayers || []) {
      const paint = symLayerColour(layer);

      if (paint && paint.color) {
        paint.color = symCimColour(symCimHex(paint.color), null, alpha);
        touched += 1;
      }
    }
  }

  if (says) {
    says.textContent = touched === 0
      ? "Nothing here is painted with a colour."
      : `${num(touched)} symbol layer${touched === 1 ? "" : "s"} across `
        + `${num(symClasses().length)} class${symClasses().length === 1 ? "" : "es"}.`;
  }

  await symSettled({ classes: true, stack: true });
}

/** Whether the symbol editor is standing in for the class list. */
let symInDetail = false;

/**
 * What to call a class on screen.
 *
 * <b>Its label, then its value, then its number.</b> A generated classification labels every
 * class with its own value, so the first is usually enough; a hand-built one may have neither,
 * and *class 7* is still better than an empty heading.
 *
 * @param {object} cls the class
 * @param {number} at its index
 * @returns {string} a name for it
 */
function symClassLabel(cls, at) {
  if (!cls) return `class ${at + 1}`;

  const value = (((cls.values || [])[0] || {}).fieldValues || [])[0];

  return cls.label || value || (cls.upperBound !== undefined
    ? `up to ${cls.upperBound}`
    : `class ${at + 1}`);
}

/** Removes a class, refusing to leave a renderer with none. */
function symRemoveClass(at) {
  const holder = symModel.type === "CIMUniqueValueRenderer"
    ? (symModel.groups || [{}])[0]?.classes
    : symModel.breaks;

  if (!Array.isArray(holder) || holder.length <= 1) {
    toast("A classified renderer needs at least one class.");
    return;
  }

  holder.splice(at, 1);
  symClassIndex = Math.min(symClassIndex, holder.length - 1);
}

/** The geometry of the layer being styled, so the form knows which symbol kind to build. */
let symGeometry = "";

/** One timer for the form and the document box, because only the last edit matters. */
let symDebounce = 0;

// ------------------------------------------------------------- the symbology form
//
// <b>A graphical editor over the same document — owner request 2026-09-03.</b> The page had a
// JSON box, the derived `drawingInfo` and colour swatches, which is an editor for somebody who
// already knows MapLibre. What it did not have is the two things anybody choosing an appearance
// needs: controls that name what they do, and a picture of the result.
//
// <b>One source of truth on screen.</b> The controls write the document box on every change and
// **Store sends the box** — so there is no rule about which of the two wins, and what is about to
// be stored is always visible. Editing the box by hand still works and the controls stop claiming
// to describe it, which is honest: a MapLibre expression has no checkbox.
//
// <b>The three families are the ones this form authors</b> — `simple`, `uniqueValue`,
// `classBreaks`, which is what ADR-033's conversion reads and what the ArcGIS face publishes.
// Nothing here invents a fourth.
//
// <b>The server reads four, and that is not the same number on purpose.</b> ADR-052 §3.10 added
// `CIMProportionalRenderer` to the read subset because it reduces to a simple renderer with a
// size variable and cost no new drawing; authoring it needs a minimum symbol, a data range and
// Flannery's exponent, and none of those has a control here. `SYM_AUTHORED` is where the two
// numbers meet: a renderer outside it hides this form rather than being flattened into it.

/** The layer this editor is currently describing, and what its fields are. */
let symFields = [];

/** True while the form is being filled from the server, so its own events do not fire back. */
let symFilling = false;

/** `#rrggbb` from an Esri colour array, which is what a colour input wants. */
function symHex(colour) {
  if (!Array.isArray(colour) || colour.length < 3) return "#888888";

  return "#" + colour.slice(0, 3)
    .map(c => Math.max(0, Math.min(255, Number(c) || 0)).toString(16).padStart(2, "0"))
    .join("");
}

/** An Esri colour array from `#rrggbb`, keeping the alpha somebody already had. */
function symColour(hex, alpha) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex || "");
  const n = m ? parseInt(m[1], 16) : 0x888888;

  return [(n >> 16) & 255, (n >> 8) & 255, n & 255, alpha === undefined ? 255 : alpha];
}

/**
 * Reads the model's visual variables into the two-stop form.
 *
 * <b>One variable, two stops, and that is a deliberate floor rather than the model's.</b> CIM
 * carries as many stops as somebody wants and the reader keeps them all; this form edits the
 * common case and says so when it is looking at something richer, instead of silently
 * rewriting a five-stop ramp as two.
 */
function drawVarying() {
  const rows = $("symVaryRows");
  const what = $("symVaryWhat");

  if (!rows || !what || !symModel) return;

  const variables = Array.isArray(symModel.visualVariables) ? symModel.visualVariables : [];
  const first = variables[0];

  const kind = !first
    ? ""
    : first.type === "CIMColorVisualVariable"
      ? "colour"
      : first.type === "CIMSizeVisualVariable"
        ? "size"
        : first.type === "CIMTransparencyVisualVariable" ? "opacity" : "";

  what.value = kind;
  rows.hidden = kind === "";

  const colour = kind === "colour";

  for (const [id, on] of [
    ["symVaryFromColour", colour], ["symVaryToColour", colour],
    // <b>The box and its per-cent sign are hidden as one.</b> They were two entries here and the
    // sign was a sibling of the box; now it is inside `.pair` with it, so hiding the pair hides
    // both and there is no arrangement in which one of them can be left behind.
    ["symVaryFromPer", colour], ["symVaryToPer", colour],
    ["symVaryFromMeasure", !colour], ["symVaryToMeasure", !colour],
  ]) {
    if ($(id)) $(id).hidden = !on;
  }

  if (kind === "") return;

  drawVaryFields(symVaryFieldOf(first));

  const stops = symVaryStops(first);

  $("symVaryFrom").value = stops.from.at;
  $("symVaryTo").value = stops.to.at;

  if (colour) {
    $("symVaryFromColour").value = stops.from.colour;
    $("symVaryToColour").value = stops.to.colour;
    $("symVaryFromAlpha").value = String(stops.from.alpha);
    $("symVaryToAlpha").value = String(stops.to.alpha);
  } else {
    $("symVaryFromNumber").value = stops.from.number;
    $("symVaryToNumber").value = stops.to.number;

    const unit = kind === "opacity" ? "%" : "pt";

    $("symVaryFromUnit").textContent = unit;
    $("symVaryToUnit").textContent = unit;
  }

  const many = variables.length > 1 || symVaryStopCount(first) > 2;

  // <b>Said before it is drawn, not discovered from the picture.</b> A ramp on a field that
  // holds words draws every feature the same colour, which looks like a broken ramp rather than
  // a wrong field — so the screen says which it is.
  const field = symVaryFieldOf(first);
  const wordy = field !== "" && symFields.length > 0
    && !symFields.some(f => f.name === field && symIsNumeric(f.type));

  $("symVaryNote").textContent = wordy
    ? `${field} does not hold numbers, so every feature counts as nought and the whole layer `
      + "draws in the first colour. Choose a number, or take this off with — none —."
    : many
      ? "This layer's document carries more than these two stops. They are kept; only the two "
        + "ends are edited here."
      : "";
}

/** The field a stored variable reads, in the spelling this form shows. */
function symVaryFieldOf(variable) {
  if (!variable) return "";

  if (variable.field) return variable.field;

  const e = String(variable.expression || "");

  return e.startsWith("$feature.") ? e.slice("$feature.".length) : e;
}

/** How many stops a stored variable carries. */
function symVaryStopCount(variable) {
  if (!variable) return 0;

  if (Array.isArray(variable.dataValues)) return variable.dataValues.length;

  return 2;
}

/** The two ends of a stored variable, in the units the form edits. */
function symVaryStops(variable) {
  const data = Array.isArray(variable.dataValues) && variable.dataValues.length > 1
    ? variable.dataValues
    : [variable.minValue ?? 0, variable.maxValue ?? 1];

  const from = { at: data[0], colour: "#ffffff", alpha: 100, number: 1 };
  const to = { at: data[data.length - 1], colour: "#000000", alpha: 100, number: 10 };

  if (variable.type === "CIMColorVisualVariable") {
    const ramp = variable.colorRamp || {};
    const colours = Array.isArray(ramp.colors) ? ramp.colors : null;

    const low = colours ? colours[0] : ramp.fromColor;
    const high = colours ? colours[colours.length - 1] : ramp.toColor;

    from.colour = symCimHex(low);
    to.colour = symCimHex(high);
    from.alpha = symCimAlpha(low);
    to.alpha = symCimAlpha(high);
  } else if (variable.type === "CIMSizeVisualVariable") {
    const sizes = Array.isArray(variable.sizeValues) && variable.sizeValues.length > 1
      ? variable.sizeValues
      : [variable.minSize ?? 1, variable.maxSize ?? 10];

    from.number = sizes[0];
    to.number = sizes[sizes.length - 1];
  } else {
    const alphas = Array.isArray(variable.transparencyValues)
      && variable.transparencyValues.length > 1
      ? variable.transparencyValues
      : [100, 0];

    // The form asks how solid, which is the other way up from what CIM stores.
    from.number = Math.round(100 - alphas[0]);
    to.number = Math.round(100 - alphas[alphas.length - 1]);
  }

  return { from, to };
}

/** The field picker for the variable, from the layer's own fields. */
function drawVaryFields(chosen) {
  const box = $("symVaryField");
  if (!box) return;

  // <b>A visual variable reads a number, and this list offered every field.</b> Measured
  // 2026-09-04 on `ci_buildings`: a colour ramp told to read a text field paints
  // <b>1,752 pixels of one colour</b> — the ramp's low end, because every feature's value
  // becomes 0 — against 45 distinct colours from a numeric field. Nothing refuses it, nothing
  // reports a loss, and the map comes out flat with no sentence anywhere saying why. The owner
  // had exactly this on their screen: *Change: its colour, With: il*, where `il` is a province
  // name.
  const usable = symFields.filter(f => symIsNumeric(f.type));

  // <b>A field already in the document stays in the list even when it does not belong there.</b>
  // Dropping it would silently move a stored variable onto another field the moment somebody
  // opened the page — the form editing the document behind the reader's back, which is worse
  // than the fault being fixed. It is shown, marked, and explained underneath.
  const stored = chosen && !usable.some(f => f.name === chosen)
    ? [{ name: chosen, type: "", kept: true }]
    : [];

  const offer = [...stored, ...usable];

  box.innerHTML = offer.length === 0
    ? `<option value="">— this layer has no number to vary with —</option>`
    : offer.map(f =>
        `<option value="${h(f.name)}"${f.name === chosen ? " selected" : ""}>${
          h(f.name)}${f.kept ? " — not a number" : ""}</option>`)
      .join("");
}

/**
 * Writes the form's two stops back into the model.
 *
 * <b>Replaces the first variable and leaves any others alone.</b> A document that carries two
 * of them was authored somewhere richer than this form, and dropping the second because this
 * screen only shows one would be the form deciding what the document may contain.
 */
function varyFromForm() {
  if (!symModel) return;

  const kind = $("symVaryWhat").value;
  const rest = (Array.isArray(symModel.visualVariables) ? symModel.visualVariables : []).slice(1);

  if (kind === "") {
    if (rest.length > 0) symModel.visualVariables = rest;
    else delete symModel.visualVariables;

    return;
  }

  const field = $("symVaryField").value;
  const from = Number($("symVaryFrom").value) || 0;
  const to = Number($("symVaryTo").value) || 0;

  let built;

  if (kind === "colour") {
    built = {
      type: "CIMColorVisualVariable",
      expression: "$feature." + field,
      minValue: from,
      maxValue: to,
      colorRamp: {
        type: "CIMLinearContinuousColorRamp",
        // <b>Three arguments, not one.</b> Built from the hex alone, this rebuilt every ramp
        // at full opacity — so a stored ramp that faded from transparent lost the fade the
        // first time anybody touched the form, silently, on a page that shows no alpha.
        fromColor: symCimColour(
          $("symVaryFromColour").value, null, symPercent($("symVaryFromAlpha").value, 100)),
        toColor: symCimColour(
          $("symVaryToColour").value, null, symPercent($("symVaryToAlpha").value, 100)),
      },
    };
  } else if (kind === "size") {
    const small = Number($("symVaryFromNumber").value) || 0;
    const large = Number($("symVaryToNumber").value) || 0;

    built = {
      type: "CIMSizeVisualVariable",
      expression: "$feature." + field,
      dataValues: [from, to],
      sizeValues: [small, large],
      minValue: from,
      maxValue: to,
      minSize: small,
      maxSize: large,
    };
  } else {
    built = {
      type: "CIMTransparencyVisualVariable",
      field,
      dataValues: [from, to],
      transparencyValues: [
        Math.max(0, 100 - (Number($("symVaryFromNumber").value) || 0)),
        Math.max(0, 100 - (Number($("symVaryToNumber").value) || 0)),
      ],
    };
  }

  symModel.visualVariables = [built, ...rest];
}

/** The stops a variable starts with, so choosing one draws something immediately. */
function varyStarted(kind) {
  const field = (symFields.find(f => /int|double|float|number|small|single/i.test(f.type || ""))
    || symFields[0] || {}).name || "";

  $("symVaryField").innerHTML = "";
  drawVaryFields(field);

  $("symVaryFrom").value = 0;
  $("symVaryTo").value = 100;

  if (kind === "colour") {
    $("symVaryFromColour").value = "#fff5eb";
    $("symVaryToColour").value = "#8c2d04";
    $("symVaryFromAlpha").value = "100";
    $("symVaryToAlpha").value = "100";
  } else if (kind === "size") {
    $("symVaryFromNumber").value = 1;
    $("symVaryToNumber").value = 12;
  } else {
    $("symVaryFromNumber").value = 20;
    $("symVaryToNumber").value = 100;
  }
}

// ------------------------------------------------------------------ the symbol library
//
// <b>ADR-052 §3.8.</b> Esri's Symbol Styler opens on *Current symbol* and a set to pick from,
// and the sets are what make a complex symbol reachable by somebody who is not going to type
// `CIMSolidStroke` twice. These are that, for the symbols this server actually draws.
//
// <b>Every entry is a stack, because a single fill needs no gallery.</b> The library earns its
// place on the road with a casing under it, the boundary that is a dash over a solid, the
// polygon whose edge is heavier than its fill — the cases ADR-052 was decided for and the ones
// nobody builds by hand twice.
//
// <b>No sprites, and that is a boundary rather than an omission.</b> A picture marker needs a
// sprite sheet, which [ADR-027](../../../docs/adr/ADR-027-glyphs-and-sprites.md) condition 5
// still refuses; every symbol here is drawn from solid fills, solid strokes and vector markers,
// which is exactly what `MapRenderer` paints.

/** A CIMRGBColor, written the short way these presets are read in. */
const symRgb = (r, g, b, a = 100) => ({ type: "CIMRGBColor", values: [r, g, b, a] });

/** A solid stroke, optionally dashed. */
const symStroke = (colour, width, dashes) => {
  const layer = {
    type: "CIMSolidStroke",
    enable: true,
    capStyle: dashes ? "Butt" : "Round",
    joinStyle: "Round",
    width,
    color: colour,
  };

  if (dashes) {
    layer.effects = [{
      type: "CIMGeometricEffectDashes",
      dashTemplate: dashes,
      lineDashEnding: "NoConstraint",
    }];
  }

  return layer;
};

/** A solid fill. */
const symFill = colour => ({ type: "CIMSolidFill", enable: true, color: colour });

/** A round vector marker of one colour. */
const symMarker = (colour, size) => ({
  type: "CIMVectorMarker",
  enable: true,
  size,
  rotation: 0,
  markerGraphics: [{
    type: "CIMMarkerGraphic",
    geometry: { x: 0, y: 0 },
    symbol: { type: "CIMPolygonSymbol", symbolLayers: [symFill(colour)] },
  }],
});

/**
 * The shipped symbol sets.
 *
 * <b>Layers are listed the way CIM lists them: the first one is drawn on top.</b> A casing is
 * therefore the *last* entry of a road, which is the one thing to get right when reading these.
 */
const SYMBOL_LIBRARY = [
  {
    name: "Lines",
    shape: "line",
    symbols: [
      {
        id: "line-plain",
        name: "Plain line",
        layers: [symStroke(symRgb(60, 60, 60), 1)],
      },
      {
        id: "line-casing",
        name: "Road with casing",
        layers: [
          symStroke(symRgb(255, 255, 255), 2),
          symStroke(symRgb(40, 40, 40), 6),
        ],
      },
      {
        id: "line-major",
        name: "Major road",
        layers: [
          symStroke(symRgb(250, 200, 90), 3.5),
          symStroke(symRgb(160, 110, 20), 6.5),
        ],
      },
      {
        id: "line-dashed-boundary",
        name: "Dashed boundary",
        layers: [
          symStroke(symRgb(120, 60, 140), 1.2, [6, 3]),
          symStroke(symRgb(235, 225, 240), 3.5),
        ],
      },
      {
        id: "line-railway",
        name: "Railway",
        layers: [
          symStroke(symRgb(255, 255, 255), 2, [4, 4]),
          symStroke(symRgb(40, 40, 40), 2.6),
        ],
      },
      {
        id: "line-stream",
        name: "Stream",
        layers: [symStroke(symRgb(80, 150, 210), 1.4)],
      },
    ],
  },
  {
    name: "Areas",
    shape: "fill",
    symbols: [
      {
        id: "area-plain",
        name: "Plain fill",
        layers: [symFill(symRgb(204, 187, 68))],
      },
      {
        id: "area-hairline",
        name: "Hairline edge",
        layers: [
          symStroke(symRgb(90, 90, 90), 0.4),
          symFill(symRgb(220, 220, 214)),
        ],
      },
      {
        id: "area-heavy-edge",
        name: "Heavy edge",
        layers: [
          symStroke(symRgb(60, 70, 90), 2.4),
          symFill(symRgb(198, 212, 232)),
        ],
      },
      {
        id: "area-outline-only",
        name: "Outline only",
        layers: [symStroke(symRgb(150, 50, 40), 1.6)],
      },
      {
        id: "area-dashed-edge",
        name: "Dashed edge",
        layers: [
          symStroke(symRgb(120, 90, 40), 1.2, [5, 3]),
          symFill(symRgb(244, 236, 218)),
        ],
      },
      {
        id: "area-water",
        name: "Water",
        layers: [
          symStroke(symRgb(90, 150, 200), 0.6),
          symFill(symRgb(178, 214, 238)),
        ],
      },
    ],
  },
  {
    name: "Points",
    shape: "marker",
    symbols: [
      {
        id: "point-plain",
        name: "Plain marker",
        layers: [symMarker(symRgb(200, 60, 50), 8)],
      },
      {
        id: "point-haloed",
        name: "Haloed marker",
        layers: [
          symMarker(symRgb(200, 60, 50), 7),
          symMarker(symRgb(255, 255, 255), 12),
        ],
      },
      {
        id: "point-ringed",
        name: "Ringed marker",
        layers: [
          symMarker(symRgb(255, 255, 255), 5),
          symMarker(symRgb(40, 90, 150), 12),
        ],
      },
      {
        id: "point-small",
        name: "Small dot",
        layers: [symMarker(symRgb(70, 70, 70), 4)],
      },
    ],
  },
];

/**
 * Draws the gallery for the geometry being edited.
 *
 * <b>Only the sets this geometry can be drawn with.</b> A line layer offered an area fill is a
 * gallery that mostly does not work, and finding that out costs a click each time.
 */
function drawSymbolGallery() {
  const box = $("symGallery");
  if (!box) return;

  const shape = symKindOfGeometry(symGeometry);
  const set = SYMBOL_LIBRARY.find(s => s.shape === shape);

  if (!set) {
    box.innerHTML = "";
    return;
  }

  box.innerHTML = set.symbols.map(symbol =>
    `<button class="symcard" data-symbol="${h(symbol.id)}" title="${h(symbol.name)}">
      ${symSwatch(symbol, shape)}
      <span>${h(symbol.name)}</span>
    </button>`).join("");
}

/**
 * A small drawing of one preset.
 *
 * <b>Drawn here rather than fetched, and that is the one place this console draws a symbol
 * itself.</b> A gallery of sixteen server previews is sixteen requests before anybody has
 * chosen anything. This is an icon; the picture beside the form is still the renderer's, and
 * that is the one that decides.
 */
function symSwatch(symbol, shape) {
  const parts = [...symbol.layers].reverse().map(layer => {
    const paint = symLayerColour(layer);
    const colour = symCimHex(paint && paint.color);

    if (layer.type === "CIMSolidStroke") {
      const dash = layer.effects && layer.effects[0] && layer.effects[0].dashTemplate;

      return shape === "fill"
        ? `<rect x="7" y="9" width="44" height="24" fill="none" stroke="${colour}"
             stroke-width="${(layer.width || 1) * 1.2}"${
               dash ? ` stroke-dasharray="${dash.join(" ")}"` : ""}/>`
        : `<path d="M5,32 C 18,32 20,12 30,12 S 46,26 55,26" fill="none" stroke="${colour}"
             stroke-width="${(layer.width || 1) * 1.6}" stroke-linecap="round"${
               dash ? ` stroke-dasharray="${dash.join(" ")}"` : ""}/>`;
    }

    if (layer.type === "CIMVectorMarker") {
      return `<circle cx="30" cy="21" r="${(layer.size || 8) * 0.9}" fill="${colour}"/>`;
    }

    return `<rect x="7" y="9" width="44" height="24" fill="${colour}"/>`;
  });

  return `<svg viewBox="0 0 60 42" aria-hidden="true">${parts.join("")}</svg>`;
}

/** Puts one preset onto the selected class. */
function symApplyLibrary(id) {
  const set = SYMBOL_LIBRARY.find(s => s.shape === symKindOfGeometry(symGeometry));
  const chosen = set && set.symbols.find(s => s.id === id);
  const cls = symClasses()[symClassIndex];

  if (!chosen || !cls) return false;

  // <b>The symbol is replaced, not merged.</b> Keeping the class's old colour would make the
  // gallery unpredictable — a preset chosen for its colours would arrive in somebody else's —
  // and recolouring afterwards is one click in the row below.
  symSymbolOf(cls).symbolLayers = JSON.parse(JSON.stringify(chosen.layers));

  return true;
}

/** The symbol kind this layer's geometry takes: fill, line or marker. */
function symKindOfGeometry(geometry) {
  const g = String(geometry || "").toLowerCase();

  if (g.includes("point")) return "marker";
  if (g.includes("line")) return "line";

  return "fill";
}

/**
 * The class of swatch a layer of this geometry gets: a rule, a box or a dot.
 *
 * <b>Shape, not only colour.</b> The handoff's class row draws a line layer's swatch as a
 * stroked rule and a polygon's as a filled box, because a column of identical squares says
 * nothing about what the map does with them. The stylesheet does the drawing; this only names
 * which of the three it is.
 *
 * @param {string} geometry the layer's geometry, as the server reports it
 * @returns {string} the class name
 */
function symSwatchShape(geometry) {
  const shape = symKindOfGeometry(geometry);

  if (shape === "marker") return "sw-point";
  if (shape === "line") return "sw-line";

  return "sw-poly";
}

/** The CIM symbol type a layer of this geometry is drawn with. */
function symTypeOfGeometry(geometry) {
  const shape = symKindOfGeometry(geometry);

  if (shape === "marker") return "CIMPointSymbol";
  if (shape === "line") return "CIMLineSymbol";

  return "CIMPolygonSymbol";
}

/**
 * The renderer being edited, whole.
 *
 * <b>The model is the parsed document, not a set of fields read back off the screen.</b> A
 * symbol can hold layers this console does not understand — a hatch fill, an effect it does not
 * draw — and ADR-052's whole argument is that those survive. Keeping the parsed object and
 * touching only the parts the controls own is what makes that true through an edit; rebuilding
 * a renderer from the inputs would quietly delete everything the form has no box for.
 */
let symModel = null;

/**
 * Which renderer family the form is authoring.
 *
 * <b>Three radio inputs rather than a select, so there is no one element to read.</b> The
 * handoff draws the family as three cards, which a select cannot be; a hidden select kept
 * beside them as the value holder would be two controls for one fact, and this file has already
 * paid for that shape twice. One reader and one writer instead, both here.
 *
 * @returns {string} `simple`, `uniqueValue` or `classBreaks`
 */
function symKindValue() {
  const on = document.querySelector('#page-symbology input[name="symKind"]:checked');

  return (on && on.value) || "simple";
}

/** Moves the family cards to a value without firing the change handler. */
function symKindSet(kind) {
  for (const box of document.querySelectorAll('#page-symbology input[name="symKind"]')) {
    box.checked = box.value === kind;
  }
}

/** Which class's symbol the stack panel is showing. */
let symClassIndex = 0;

/** The classes of the model, whatever family it is, as one array to walk. */
function symClasses() {
  if (!symModel) return [];

  if (symModel.type === "CIMUniqueValueRenderer") {
    return (symModel.groups || []).flatMap(g => g.classes || []);
  }

  if (symModel.type === "CIMClassBreaksRenderer") {
    return symModel.breaks || [];
  }

  return [symModel];
}

/** One class's symbol object, creating the reference chain if it is missing. */
function symSymbolOf(cls) {
  if (!cls) return null;

  if (!cls.symbol) {
    cls.symbol = { type: "CIMSymbolReference", symbol: null };
  }

  if (!cls.symbol.symbol) {
    cls.symbol.symbol = {
      type: symTypeOfGeometry(symGeometry),
      symbolLayers: [],
    };
  }

  if (!Array.isArray(cls.symbol.symbol.symbolLayers)) {
    cls.symbol.symbol.symbolLayers = [];
  }

  return cls.symbol.symbol;
}

/** `#rrggbb` from a CIMRGBColor. */
function symCimHex(colour) {
  const v = (colour && colour.values) || [];

  return symHex(v.length >= 3 ? [v[0], v[1], v[2]] : null);
}

/**
 * The opacity of a CIMRGBColor, as a percentage, fraction and all.
 *
 * <b>Nought to a hundred, which is the CIM scale and not Esri's.</b> `CIMRGBColor.values` is
 * `[r, g, b, alpha]` with the three channels on 0–255 and the alpha on <b>0–100</b>; the ArcGIS
 * REST face puts all four on 0–255, so the two differ by a factor this console never applies
 * because it edits the stored document rather than the published one. Missing means opaque:
 * a colour written without a fourth number is a solid colour everywhere this server reads one.
 *
 * @param {object} colour a CIMRGBColor, or nothing
 * @returns {number} its opacity in per cent
 */
function symCimAlpha(colour) {
  const v = (colour && colour.values) || [];

  return v.length > 3 && Number.isFinite(Number(v[3])) ? Number(v[3]) : 100;
}

/**
 * A CIMRGBColor from `#rrggbb`, with an opacity taken from an argument or from what was there.
 *
 * @param {string} hex the colour, `#rrggbb`
 * @param {object} [was] the colour being replaced, whose alpha is kept
 * @param {number} [alpha] an opacity in per cent, which wins over `was`
 * @returns {object} the colour
 */
function symCimColour(hex, was, alpha) {
  const c = symColour(hex);

  return {
    type: "CIMRGBColor",
    values: [c[0], c[1], c[2], alpha ?? symCimAlpha(was)],
  };
}

/**
 * A number in 0–100 from a box somebody is still typing in.
 *
 * <b>An empty box is not nought.</b> Somebody clearing the field to type `40` passes through
 * `""`, and reading that as fully transparent makes the symbol vanish mid-keystroke — so the
 * caller says what an unreadable box means, and the value is clamped rather than trusted. The
 * layer rows answer with the opacity already on the colour; the ramp form, which rebuilds its
 * variable from nothing each time, answers with opaque.
 *
 * <b>And it is not rounded to a whole number, which it was for an hour.</b> A stored opacity is
 * very often fractional and nobody chose it: an ArcGIS document carries alpha as one byte of
 * 255, so a symbol meaning 45 per cent arrives as 115 and converts to <b>45.1</b>. Every
 * generated fixture in this repository has one. Rounding for display in a `step="1"` box put
 * 45.1 in front of a spinner that snapped it to 46 on the first press: a value nobody typed,
 * overwriting one nobody could see was different.
 *
 * @param {string} text what the box holds
 * @param {number} fallback what to use when it holds nothing readable
 * @returns {number} a per cent, clamped
 */
function symPercent(text, fallback) {
  const n = Number(String(text).trim());

  return String(text).trim().length > 0 && Number.isFinite(n)
    ? Math.max(0, Math.min(100, n))
    : fallback;
}

/** A fresh symbol layer of the asked kind. */
function symNewLayer(kind) {
  if (kind === "CIMSolidStroke") {
    return {
      type: "CIMSolidStroke",
      enable: true,
      capStyle: "Butt",
      joinStyle: "Miter",
      width: 1,
      color: { type: "CIMRGBColor", values: [68, 51, 17, 100] },
    };
  }

  if (kind === "CIMVectorMarker") {
    return {
      type: "CIMVectorMarker",
      enable: true,
      size: 8,
      rotation: 0,
      markerGraphics: [{
        type: "CIMMarkerGraphic",
        geometry: { x: 0, y: 0 },
        symbol: {
          type: "CIMPolygonSymbol",
          symbolLayers: [{
            type: "CIMSolidFill",
            enable: true,
            color: { type: "CIMRGBColor", values: [136, 136, 136, 100] },
          }],
        },
      }],
    };
  }

  return {
    type: "CIMSolidFill",
    enable: true,
    color: { type: "CIMRGBColor", values: [204, 187, 68, 100] },
  };
}

/** Where a symbol layer keeps the colour this console edits. */
/**
 * The layer of a symbol whose colour a reader would call "this class's colour".
 *
 * <b>Not layer zero, which is what this used to take.</b> CIM lists symbol layers top first, and
 * the top of a generated polygon symbol is its outline — one flat grey shared by every class. So
 * a five-class choropleth with a perfectly good light-to-dark ramp listed five identical grey
 * swatches, and the row list, which is where somebody reads and edits *what colour is class
 * three*, said nothing at all. Measured by a design review on 2026-09-04: the picture was a
 * correct ramp and the list beside it was five of `#6e6e6e`.
 *
 * <b>The fill, then the marker, then the widest stroke.</b> That is the order of how much of a
 * feature each one paints, which is the order of what a reader sees.
 *
 * @param {Array} layers the symbol's layers, top first
 * @returns {object|null} the layer to read and write the class's colour on
 */
function symThematicLayer(layers) {
  const list = layers || [];

  const fill = list.find(l => l.type === "CIMSolidFill");

  if (fill) return fill;

  const marker = list.find(l => l.type === "CIMVectorMarker");

  if (marker) return marker;

  let widest = null;

  for (const one of list) {
    if (one.type === "CIMSolidStroke"
      && (!widest || (one.width || 0) > (widest.width || 0))) {
      widest = one;
    }
  }

  return widest || list[0] || null;
}

function symLayerColour(layer) {
  if (!layer) return null;

  if (layer.type === "CIMVectorMarker") {
    const inner = (layer.markerGraphics || [])[0];
    const parts = (inner && inner.symbol && inner.symbol.symbolLayers) || [];

    return parts.find(p => p.type === "CIMSolidFill") || null;
  }

  return layer;
}

/**
 * Fills the whole form from a stored CIM renderer.
 *
 * <b>From the canonical document, not from the derived `drawingInfo`.</b> The derived one is
 * flattened to one symbol for the Esri face; reading it here would mean every edit silently
 * threw away the symbol layers past the first.
 */
const SYM_AUTHORED = [
  "CIMSimpleRenderer", "CIMUniqueValueRenderer", "CIMClassBreaksRenderer",
];

function fillSymbologyForm(cim, geometry) {
  symFilling = true;

  try {
    symModel = cim && typeof cim === "object"
      ? JSON.parse(JSON.stringify(cim))
      : { type: "CIMSimpleRenderer", label: "", description: "", symbol: null };

    // <b>The form authors three renderers and the server reads four.</b> ADR-052 §3.10 added
    // `CIMProportionalRenderer` to the read subset because it costs no new drawing; authoring it
    // is a different job, with a minimum symbol and a data range and Flannery's exponent, and
    // none of that has a control here. Falling through to `simple` -- which is what this did
    // before the renderer was read at all -- put the form in a state where the picture, the
    // dropdown and the document disagreed, and the first edit would have written a
    // `CIMSimpleRenderer` over a document nobody asked to change.
    if (!SYM_AUTHORED.includes(symModel.type || "CIMSimpleRenderer")) {
      $("symForm").hidden = true;
      $("symUnauthored").hidden = false;

      // <b>The picture and the document stay; the two columns that author go.</b> Hiding only
      // the rail would leave a class list for classes this form cannot read, and a Classes tab
      // over an empty pane is a control for something that is not there (ADR-034). The
      // inspector opens on the document instead, which is the one place this renderer can be
      // changed at all.
      $("symTabClasses").hidden = true;
      symShowInspector("document");

      // <b>Two columns, not three with a hole in the first.</b> A hidden grid item is removed
      // from the flow rather than left empty, so the picture slid into the rail's 264 pixels
      // and the inspector's 336 were blank. Measured on `ci_observations`, whose renderer is a
      // CIMProportionalRenderer.
      $("symCols").classList.add("noRail");
      // <b>It says nothing is wrong, because a form that vanishes does not.</b> A design
      // review on 2026-09-04 read this as a person who does not know what CIM is and reported
      // that it answers *what is going on* and *what can I do*, and never answers *is this
      // broken* — which is the question somebody actually has when the controls disappear.
      // The second sentence used to repeat the state line above it almost word for word; it
      // now carries the reassurance instead, which is the sentence that was missing.
      $("symUnauthored").innerHTML =
        `<b>This layer is drawn by a <span class="mono">${h(symModel.type || "")}</span>,`
        + ` which this form does not author.</b> Nothing is wrong — the server reads it, and`
        + ` the picture above is what the map looks like. To change it, edit the document`
        + ` below, or store a new one.`;

      return;
    }

    $("symForm").hidden = false;
    $("symUnauthored").hidden = true;
    $("symTabClasses").hidden = false;
    $("symCols").classList.remove("noRail");

    const kind = symModel.type === "CIMUniqueValueRenderer"
      ? "uniqueValue"
      : symModel.type === "CIMClassBreaksRenderer" ? "classBreaks" : "simple";

    symKindSet(kind);
    $("symFieldRow").hidden = kind === "simple";
    symShowClassify(kind);

    symClassIndex = Math.min(symClassIndex, Math.max(symClasses().length - 1, 0));

    drawSymbologyFields(
      (symModel.fields && symModel.fields[0]) || symModel.field || "");

    drawExtraFields(symModel.fields || []);

    // <b>A document that has just been loaded or typed is shown as its list of classes.</b>
    // Staying in one class's symbol across a change of document would leave the editor open on
    // whatever now happens to be at that index, which is a different class wearing the same
    // number.
    symShowDetail(false);

    drawSymbologyClasses(kind);
    drawSymbolLayers();
    drawSymbolGallery();
    drawVarying();
    symSaySummaries();
  } finally {
    symFilling = false;
  }
}

/**
 * The field picker, from the layer's own fields, narrowed to the ones the family can use.
 *
 * <b>Classifying by ranges over a text column is arithmetic on a name.</b> The picker offered
 * every field whatever the family, so choosing *by ranges of a number* and then a text field was
 * one click away — and the answer, before this and its two server-side repairs, was a 500 telling
 * the operator to go and check whether PostGIS was installed. Narrowing the list removes the
 * path rather than improving the apology. Found by a design review on 2026-09-04.
 *
 * <b>Every field is still offered for the unique-value family</b>, because any column has
 * distinct values — that is the whole of what it needs.
 *
 * @param {string} chosen which field is selected
 */
function drawSymbologyFields(chosen) {
  const box = $("symField");
  if (!box) return;

  const ranges = symKindValue() === "classBreaks";
  const usable = ranges ? symFields.filter(f => symIsNumeric(f.type)) : symFields;

  if (symFields.length === 0) {
    box.innerHTML = `<option value="">— this layer's fields could not be read —</option>`;

    return;
  }

  if (usable.length === 0) {
    box.innerHTML = `<option value="">— no numeric field to classify by ranges —</option>`;

    return;
  }

  box.innerHTML = usable.map(f =>
      `<option value="${h(f.name)}"${f.name === chosen ? " selected" : ""}>${h(f.name)}</option>`)
    .join("");
}

/**
 * The second and third field pickers, shown only when the family can use them.
 *
 * <b>One at a time.</b> The third stays hidden until the second is chosen — a control that
 * cannot yet do anything is a control somebody tries and then wonders about. Each offers a blank
 * first option, because dropping back to one field has to be as easy as adding a second.
 *
 * @param {Array} chosen the fields the model currently classifies by
 */
function drawExtraFields(chosen) {
  const many = symKindValue() === "uniqueValue";

  for (const [at, id] of [[1, "symField2"], [2, "symField3"]]) {
    const box = $(id);
    const row = $(id + "Row");

    if (!box || !row) continue;

    // The third appears once the second is answered, and never before.
    const earlier = at === 1 || (chosen[1] || "").length > 0;

    row.hidden = !many || !earlier;

    box.innerHTML = `<option value="">— none —</option>` + symFields
      .map(f => `<option value="${h(f.name)}"${
        f.name === chosen[at] ? " selected" : ""}>${h(f.name)}</option>`)
      .join("");
  }
}

/**
 * Whether a field's values can be put in ranges.
 *
 * <b>The same list the server checks against</b>, in the vocabulary the layer document uses. A
 * date is included because a classification by decade is a real map; a boolean is not, because a
 * column of two values has nothing a range means.
 *
 * @param {string} type the ArcGIS field type
 * @returns {boolean} whether a range classification can be computed over it
 */
function symIsNumeric(type) {
  return /^esriFieldType(SmallInteger|Integer|BigInteger|Single|Double|OID|Date)$/
    .test(type || "");
}

/**
 * One row per class: what it matches, what it is called, and the colour of its top layer.
 *
 * <b>The swatch is a shortcut into the stack, not a second place the colour lives.</b> It shows
 * and sets the topmost painted layer, which is the one a reader sees; anything deeper is edited
 * in the panel below. Two independent colours for one symbol is how a form starts disagreeing
 * with the document it writes.
 */
function drawSymbologyClasses(kind) {
  const box = $("symClasses");
  if (!box) return;

  const classes = symClasses();

  if (classes.length === 0) {
    box.innerHTML = `<p class="hint">No classes yet. <b>Add a class</b> makes one.</p>`;
    symShowFilter(0, 0);
    drawSymLegend();
    return;
  }

  // <b>The filter drops rows from the page and never renumbers the ones left.</b> It rebuilds
  // the list rather than hiding rows with CSS — a design review checked and found 199 of 200
  // rows genuinely gone from the DOM, which is what keeps the tab order short — and every
  // surviving row keeps its index in the model. Rebuilding with fresh numbering instead would
  // make the third visible row edit the third class rather than the one somebody is looking at,
  // which is the kind of fault that only shows up on the values nobody tested with. The review
  // confirmed a selection made before filtering is still selected after clearing it.
  const wanted = ((($("symFilter") || {}).value) || "").trim().toLowerCase();

  const shown = classes
    .map((cls, i) => ({ cls, i }))
    .filter(({ cls }) => wanted.length === 0 || symClassText(cls).includes(wanted));

  symShowFilter(classes.length, shown.length);

  // <b>The view is re-applied here rather than having a second writer.</b> Two of these controls
  // appear only when there is more than one class, and adding or removing one changes that
  // without changing the view — so the counts are re-read where the rows are drawn, by the one
  // function that owns which of them are on the screen.
  symShowDetail(symInDetail);

  if (shown.length === 0) {
    box.innerHTML = `<p class="hint">No class matches <b>${h(wanted)}</b>.</p>`;
    // <b>The legend is not filtered.</b> The filter narrows what a reader is editing; the map
    // beside it still draws every class, and a legend that agreed with the search box would be
    // saying something false about the picture.
    drawSymLegend();
    return;
  }

  box.innerHTML = shown.map(({ cls, i }) => {
    const layers = symSymbolOf(cls).symbolLayers;
    const top = symLayerColour(symThematicLayer(layers));
    // <b>Nothing to choose between when there is one.</b> A `simple` renderer has exactly
    // one row and it was always drawn as selected, which reads as a state somebody set.
    const chosen = classes.length > 1 && i === symClassIndex ? " symchosen" : "";

    // <b>The swatch is the shape of the thing it stands for.</b> A square of colour beside a
    // road layer says *this is a fill*, which is what the map does not do. Shaping it costs
    // nothing — the inner swatch of a colour input is stylable, so the target stays 26 by 22
    // whatever shape is drawn inside it — and it is the one signal on the row that survives at
    // a glance down 256 of them.
    const shape = symSwatchShape(symGeometry);

    if (kind === "simple") {
      return `<div class="setting symclass${chosen}" data-class="${i}">
        <input type="color" class="symfill ${shape}" data-class="${i}"
          value="${h(symCimHex(top && top.color))}"
          title="The colour every feature is drawn in"
          aria-label="The colour every feature is drawn in">
        <span class="symtexts"><span class="symname">Every feature</span></span></div>`;
    }

    const value = kind === "uniqueValue"
      ? (((cls.values || [])[0] || {}).fieldValues || [])[0] ?? ""
      : cls.upperBound ?? "";

    // <b>The row is not its own tab stop, and it was.</b> It carried `tabindex` and a role so a
    // keyboard could open it, which was right when a classification had four classes and wrong
    // at 256: a design review counted <b>1,000 focusable elements</b> in a 200-class list — five
    // a row — and at the ceiling that is 1,280 stops between the filter and the Store button,
    // with nothing to skip them.
    //
    // <b>Selection follows focus instead.</b> Every row already contains the controls a keyboard
    // reaches; focusing any of them selects that row, which is both fewer stops and a better
    // answer, because tabbing into a class's value box and having the symbol panel below follow
    // is what somebody expects anyway. `aria-current` says which one, since the row is no longer
    // a button that could be pressed.
    //
    // <b>The label is above the value, and the value is mono.</b> The label is the sentence
    // somebody writes and the value is the datum it matches; stacking them is what lets both be
    // editable in a 336-pixel column without either becoming a box six characters wide.
    return `<div class="setting symclass${chosen}" data-class="${i}"
      aria-current="${i === symClassIndex ? "true" : "false"}"
      aria-label="Class ${i + 1}">
      <input type="color" class="symfill ${shape}" data-class="${i}"
        value="${h(symCimHex(top && top.color))}"
        title="The colour this class is drawn in"
        aria-label="Class ${i + 1} colour">
      <span class="symtexts">
        <input type="text" class="symlabel" data-class="${i}"
          placeholder="label" value="${h(cls.label || "")}"
          aria-label="Class ${i + 1} label">
        <input type="${kind === "uniqueValue" ? "text" : "number"}" class="symvalue"
          data-class="${i}" value="${h(String(value))}"
          placeholder="${kind === "uniqueValue" ? "value" : "up to"}"
          aria-label="Class ${i + 1} ${kind === "uniqueValue" ? "value" : "upper bound"}">
      </span>
      <button class="tiny ghost symdrop" data-class="${i}" title="Remove this class">×</button>
    </div>`;
  }).join("");

  // <b>After the rows exist, because it measures one of them.</b> The first version measured
  // before rebuilding, from whatever was already there — and the case that matters is the one
  // where nothing was: pressing *Read the values* puts 256 rows into an empty box, so there was
  // no row to measure and the list grew to all 256, which is the exact complaint this fixes.
  symBoxToTenRows(box, shown.length);

  // <b>A new class is at the end of a list showing ten of two hundred, so nothing happens where
  // anybody is looking.</b> Measured by a design review: clicking *Add a class* on a 200-class
  // renderer took the count to 201 and left `scrollTop` at 0 — a button that appears to do
  // nothing, in the common case rather than the rare one.
  if (symScrollToChoice) {
    symScrollToChoice = false;

    box.querySelector(`.symclass[data-class="${symClassIndex}"]`)
      ?.scrollIntoView({ block: "nearest" });
  }

  drawSymLegend();
}

/**
 * The legend over the picture: what the reader of a map would be given.
 *
 * <b>The same classes, read rather than edited.</b> The inspector's list is boxes and a remove
 * button because it is where a class is changed; this is swatch and label because it is where
 * somebody checks that the picture beside it means anything. Neither is a copy of the other's
 * job, and the editor had only the first.
 *
 * <b>Bounded, because a classification can hold 256.</b> Twelve is what fits a card that is not
 * allowed to cover the map it labels; the rest are counted rather than listed, which is what a
 * printed legend does with a long class list too.
 */
function drawSymLegend() {
  const box = $("symLegend");

  if (!box) return;

  const classes = symClasses();

  if (classes.length === 0) {
    box.hidden = true;
    box.innerHTML = "";

    return;
  }

  const shape = symSwatchShape(symGeometry);
  const most = 12;

  box.innerHTML = classes.slice(0, most).map((cls, i) => {
    const paint = symLayerColour(symThematicLayer(symSymbolOf(cls).symbolLayers));

    return `<span><i class="legendsw ${shape}"
      style="--sw:${h(symCimHex(paint && paint.color))}"></i>${
      h(symClassLabel(cls, i))}</span>`;
  }).join("")
    + (classes.length > most
      ? `<span><em>and ${num(classes.length - most)} more</em></span>`
      : "");

  box.hidden = false;
}

/**
 * Every layer of the service this one is in, in the order the service publishes them.
 *
 * <b>Two listings, because the two surfaces have two.</b> Server reads `/admin/layers` into
 * `known` and Studio reads `/content/layers` into `content`; a publisher has no administrative
 * listing at all, so a switcher built only from `known` would be empty on the surface this page
 * belongs to. Both are already fetched — this reads whichever answered.
 *
 * @param {object} at the layer's place, from `placeOf`
 * @returns {Array} one entry per sibling, id and name, lowest id first
 */
function siblingLayers(at) {
  if (!at) return [];

  const mine = known.filter(k =>
    k.service === at.bare && (k.folder || null) === at.folder);

  const found = mine.length > 0
    ? mine.map(k => ({ name: k.name, id: k.layerIndex ?? 0 }))
    : [...content.values()]
      .filter(e => String(e.url || "").includes(`/rest/services/${at.service}/FeatureServer/`))
      .map(e => ({
        name: e.name,
        id: Number(String(e.url).split("/FeatureServer/")[1] ?? 0),
      }));

  return found.sort((a, b) => a.id - b.id);
}

/**
 * The symbology editor's title strip: where this layer is, which one it is, and its siblings.
 *
 * <b>The tabs are the service's own, not the layer editor's.</b> Caching, Maintenance and
 * Endpoints are about the layer as a published thing; Symbology is about how it draws. Putting
 * them in one nav said that choosing a colour and deleting the layer are two of a kind, and it
 * meant a reader who arrived from the service page lost the tabs they had been using.
 *
 * <b>The layer switcher exists because this page is per layer and the tab above it is not.</b>
 * A service of twenty layers had twenty symbology pages reachable only by going back to a list;
 * moving between them is the commonest thing anybody does here.
 *
 * @param {string} name the layer being edited
 * @param {object|null} at its place, from `placeOf`
 * @param {Array<string>} trail the breadcrumb steps the editor head already built
 */
function drawSymStrip(name, at, trail) {
  const crumb = $("symCrumb");
  const pick = $("symLayerPick");
  const tabs = $("symItemTabs");

  if (!crumb || !pick || !tabs) return;

  const l = layerNamed(name);
  const section = $("symLayerSection");

  const siblings = at ? siblingLayers(at) : [];

  // <b>The crumb says which layer, always.</b> It stopped saying so while a segmented switcher
  // in this strip said it instead; the switcher has moved into the rail, so the trail is the
  // only thing left that names the subject. The one exception is a service already called after
  // its single layer, which is what a one-layer import is called.
  crumb.innerHTML = at && at.bare === name
    ? trail.join(" › ")
    : `${trail.join(" › ")} › <b>${h(name)}</b>`;

  // <b>The list is drawn even for one layer, and that is not the same call as the switcher.</b>
  // A segmented control of one was a control with nothing to choose; this is a heading that says
  // *this is the layer you are editing* with its geometry and its state beside it, and a service
  // of one still has an answer to that. It goes away only when this page cannot tell which
  // service the layer is in — the case where there is nothing to list from.
  if (section) section.hidden = siblings.length === 0;

  // <b>The service's own name is not information inside its own list.</b> A geodatabase import
  // names every layer after the service it made — `ci_EarlyAlert_sites`, `_routes`, `_reports` —
  // so a 264-pixel column would carry twenty characters of prefix three times. The prefix goes
  // and the full name stays in the title, because a shortened name that cannot be recovered is
  // a different fault from a long one.
  const prefix = at && at.bare ? `${at.bare}_` : "";

  pick.innerHTML = siblings.map(one => {
    const shown = prefix && one.name.startsWith(prefix) && one.name.length > prefix.length
      ? one.name.slice(prefix.length)
      : one.name;

    return `<a class="symlayerpick${one.name === name ? " on" : ""}"
      href="#/layer/${encodeURIComponent(one.name)}/symbology"${
      one.name === name ? ' aria-current="page"' : ""} title="${h(one.name)}" role="listitem">
      <span class="geoswatch" data-picksw="${h(one.name)}"></span>
      <span class="symlayerpicktext"><span class="symlayerpickname">${num(one.id)} · ${
        h(shown)}</span><span class="rowmeta" data-pickstate="${h(one.name)}">reading…</span></span>
    </a>`;
  }).join("");

  // <b>One request a layer, in order</b> — the same rule the item page follows, and the same
  // reason: a service of thirty layers must not open thirty requests as the first thing this
  // screen does.
  fillSymLayerStates(siblings);

  // <b>The service style override is stamped here, because this is where the service is known.</b>
  // It used to be stamped by the service page's Symbology tab, and that tab is gone: the override
  // now lives at the foot of this rail. A button addressed by whichever screen was drawn last is
  // the defect this control has already had twice.
  if (at && at.bare) {
    for (const attribute of ["data-style", "data-style-put", "data-style-del"]) {
      const node = document.querySelector(`#serviceStyle [${attribute}]`);

      if (node) node.setAttribute(attribute, at.bare);
    }
  }

  if ($("styleDoc")) $("styleDoc").value = "";
  if ($("styleState")) $("styleState").innerHTML = "<b>Not fetched yet.</b>";

  // <b>Studio's, because the service page's tabs are Studio's.</b> `drawServiceTabs` draws
  // nothing on Server, so linking to them from a Server address would be a row of links to a
  // strip that is not there. The editor's own nav is still the way between a layer's pages, and
  // it is still on every page but this one.
  const service = at && at.service
    ? at.service.split("/").map(encodeURIComponent).join("/")
    : null;

  if (!service || surfaceOfPath() !== "studio") {
    tabs.hidden = true;
    tabs.innerHTML = "";
  } else {
    tabs.hidden = false;

    tabs.innerHTML = SERVICE_TABS.map(([key, label]) => key === "symbology"
      ? `<a href="#/layer/${encodeURIComponent(name)}/symbology" aria-current="page">${label}</a>`
      : `<a href="#/service/${service}?tab=${key}">${label}</a>`).join("");
  }

  // The sharing scope, as the pill every other list draws it. A reader who arrived from a link
  // rather than from a list has no other way to know whether what they are styling is public.
  const scope = $("symScope");

  if (scope) scope.innerHTML = l.sharing ? pill(l.sharing) : "";
}

/**
 * Fills the rail's layer list with each layer's geometry swatch and symbology state.
 *
 * <b>A twin of `fillLayerSymbologyStates`, and the duplication is deliberate.</b> That one fills
 * the item page's table; this one fills a 264-pixel column of cards, and the two write into
 * different elements with different shapes. What they share — *has anybody styled this, and into
 * how many classes* — is `classCountOf` and `firstSymbolLayers`, which are the parts worth
 * having once.
 *
 * @param {Array} layers the service's layers, in the order they are listed
 */
async function fillSymLayerStates(layers) {
  for (const one of layers) {
    const says = document.querySelector(`[data-pickstate="${CSS.escape(one.name)}"]`);
    const swatch = document.querySelector(`[data-picksw="${CSS.escape(one.name)}"]`);

    if (!says) continue;

    try {
      const r = await api(`/admin/layers/${encodeURIComponent(one.name)}/symbology`);
      const classes = classCountOf(r.symbology);

      says.textContent = r.stored
        ? `Authored${classes > 0 ? ` · ${num(classes)} classes` : ""}`
        : "Generated · version 0";

      says.classList.toggle("authored", !!r.stored);

      if (swatch) {
        const paint = symLayerColour(symThematicLayer(firstSymbolLayers(r.symbology)));

        swatch.className = `geoswatch ${symSwatchShape(r.geometry || "")}`;
        swatch.style.setProperty("--sw", symCimHex(paint && paint.color));
      }
    } catch {
      // A layer whose symbology cannot be read is still a layer in this service. Putting the
      // request's error where a two-word state belongs would make one failed request look like
      // a broken list.
      says.textContent = "";
    }
  }
}

/**
 * The selected class's symbol, layer by layer.
 *
 * <b>Top first, which is CIM's own order and Esri's.</b> The renderer reads the stack bottom
 * first; showing it that way would put the casing above the road in a list whose whole job is
 * to say which is on top.
 */
function drawSymbolLayers() {
  const box = $("symStack");
  if (!box) return;

  const cls = symClasses()[symClassIndex];

  if (!cls) {
    box.innerHTML = `<p class="hint">Pick a class to edit its symbol.</p>`;
    return;
  }

  // <b>The heading no longer names the class.</b> It had to, when a permanently rendered panel
  // could be looking at a row scrolled out of sight; now the panel replaces the list, so what it
  // edits is said once, at the top of the view, beside the way back.
  const layers = symSymbolOf(cls).symbolLayers;

  if (layers.length === 0) {
    box.innerHTML = `<p class="hint">This symbol has no layers, so it draws nothing.
      Add a fill, a stroke or a marker.</p>`;
    return;
  }

  box.innerHTML = layers.map((layer, i) => {
    const paint = symLayerColour(layer);
    const known = layer.type === "CIMSolidFill"
      || layer.type === "CIMSolidStroke"
      || layer.type === "CIMVectorMarker";

    const name = { CIMSolidFill: "Fill", CIMSolidStroke: "Stroke", CIMVectorMarker: "Marker" }
      [layer.type] || layer.type;

    // <b>A layer this console cannot edit is shown, not hidden.</b> It is in the document and
    // it is drawn or reported by the server; a form that skipped it would make Store look like
    // it had deleted something.
    // <b>A number and its unit are one thing on the line.</b> As two flex children they are two,
    // and a row that runs out of width breaks between them — measured by a design review at
    // 1280 wide, where the *%* of an opacity landed on a line of its own under its own box.
    const measure = layer.type === "CIMSolidStroke"
      ? `<span class="pair"><input type="number" class="symwidth" data-layer="${i}"
           min="0" max="40" step="0.25" title="How wide this stroke is, in points"
           value="${h(String(layer.width ?? 1))}"><span class="u">pt</span></span>`
      : layer.type === "CIMVectorMarker"
        ? `<span class="pair"><input type="number" class="symsize" data-layer="${i}"
             min="1" max="96" step="1" title="How big this marker is, in points"
             value="${h(String(layer.size ?? 8))}"><span class="u">pt</span></span>`
        : "";

    // <b>Four cells, because the panel is 306 pixels wide.</b> The kind, the colour, what can
    // be measured about it, and what can be done to the row — grouped rather than laid out one
    // flex child per control, which is what let a row of six break in the middle.
    return `<div class="setting symlayer" data-layer="${i}">
      <span class="q symlayerkind">${h(name)}</span>
      ${known
        ? `<input type="color" class="symlayercolour" data-layer="${i}"
             value="${h(symCimHex(paint && paint.color))}"
             title="The colour of this ${h(name.toLowerCase())}"
             aria-label="${h(name)} colour">
           <span class="symmeasures">${measure}<span class="pair"><input type="number"
             class="symalpha" data-layer="${i}" min="0" max="100" step="0.1"
             value="${h(String(symCimAlpha(paint && paint.color)))}"
             title="How opaque this colour is: 100 is solid, 0 is invisible"
             aria-label="${h(name)} opacity, per cent"><span class="u">%</span></span></span>`
        : `<span></span><span class="symmeasures hint">kept, not editable here</span>`}
      <span class="symbuttons">
        <button class="tiny ghost symup" data-layer="${i}" title="Move up"${i === 0 ? " disabled" : ""}>↑</button>
        <button class="tiny ghost symdown" data-layer="${i}" title="Move down"${
          i === layers.length - 1 ? " disabled" : ""}>↓</button>
        <button class="tiny ghost symlayerdrop" data-layer="${i}" title="Remove this layer">×</button>
      </span>
    </div>`;
  }).join("");
}

/** Whether this layer has a stored document, as the last load or Store reported it. */
let symStored = false;

/** Whether anything has been changed since that load or Store. */
let symEditedSince = false;

/**
 * What the picture beside the form is of.
 *
 * <b>Three states, and the middle one is the one that was missing.</b> A layer either has no
 * stored document and is drawn from the generated appearance, or has one and the picture matches
 * it, or has one and the picture is ahead of it because somebody has been editing. The caption
 * used to have two sentences for three states and pick between them in two different places.
 *
 * @returns {string} the caption
 */
function symPreviewSays() {
  if (symEditedSince) {
    return symStored
      ? "Edited — this is what Store would keep, not what is stored now."
      : "Edited — this is what Store would keep.";
  }

  return symStored ? "The stored appearance." : "Generated — no document is stored.";
}

/**
 * The symbology screen's map, once OpenLayers has been loaded for it.
 *
 * <b>One map for the page, kept across layers.</b> Rebuilding it per layer would throw away the
 * view somebody had panned to, which is the whole reason the map is here.
 */
let symMap = null;

/**
 * The reference the symbology map works in.
 *
 * <b>Web Mercator, because that is what the basemap tiles are.</b> OpenLayers can reproject a
 * view, but only between references it knows, and a national grid is not one of them without a
 * library this console does not carry. So the browser stays in one reference and the server —
 * which has PROJ through its datastore — does every conversion either way.
 */
const SYM_MAP_SR = 3857;

/** Which draw is current, so a stale answer does not paint over a newer view. */
let symDrawing = 0;

/**
 * Which load is current, so a load that has been overtaken stops writing.
 *
 * <b>A read of the old document must not land on top of a write of the new one.</b> Reading a
 * layer's symbology is five round trips and the last of them finishes long after the screen looks
 * ready — so an operator who pastes a style and presses Store while it is still running had the
 * answer overwritten by the read that preceded it: the losses cleared, the state line back to
 * *Generated. No document is stored*, under a document that had just been stored. This is the
 * same sequence number `symDrawing` keeps for the picture, kept for the page.
 */
let symLoadingFor = 0;

/** The promise that loads OpenLayers, so it is fetched once. */
let symOlLoading = null;

/** What the map is showing, or null before it has a size. */
function symExtent() {
  if (!symMap) return null;

  const size = symMap.getSize();

  if (!size || !(size[0] > 0) || !(size[1] > 0)) return null;

  return {
    box: symMap.getView().calculateExtent(size),
    wide: Math.round(size[0]),
    tall: Math.round(size[1]),
  };
}

/**
 * Loads OpenLayers once, from this server.
 *
 * <b>Ours, not a CDN.</b> The file is already in `wwwroot` because the layer viewer uses it, and
 * the policy on these pages is `script-src 'self'` with no `'unsafe-inline'` (D-44) — a third
 * party would be refused by the browser before anybody had to refuse it on judgement.
 *
 * @returns {Promise<boolean>} whether the library is available
 */
function loadOpenLayers() {
  if (window.ol) return Promise.resolve(true);

  if (!symOlLoading) {
    symOlLoading = new Promise(done => {
      const css = document.createElement("link");

      css.rel = "stylesheet";
      css.href = "ol.css";
      document.head.appendChild(css);

      const tag = document.createElement("script");

      tag.src = "ol.js";
      tag.onload = () => done(true);
      tag.onerror = () => done(false);
      document.head.appendChild(tag);
    });
  }

  return symOlLoading;
}

/**
 * Builds the map under the editor's picture, once.
 *
 * <b>A redraw on `moveend`, not during the drag.</b> The reader sees the picture they had while
 * they pan and the new one about a twentieth of a second after they let go, which is what every
 * map-image layer does and what the measurement was taken against.
 *
 * <b>It fails quietly.</b> If the library does not load, the picture is still drawn — at the
 * layer's own extent, exactly as it was before there was a map. A screen that loses its editor
 * because a script did not arrive is worse than one that loses its panning.
 */
async function symBuildMap() {
  const box = $("symMap");

  if (!box || symMap) return;

  if (!await loadOpenLayers()) return;

  // <b>Zoom buttons, which the still picture was right not to have.</b> ADR-034's rule is that
  // a control is not drawn for a feature that does not exist — and until this was a map, a plus
  // and a minus could not have changed anything. Now they can, so they are here; the rest of
  // OpenLayers' default furniture is not, because a rotation control and an attribution the
  // ground already carries would be three more things over a picture.
  symMap = new ol.Map({
    target: box,
    layers: [new ol.layer.Tile({ source: new ol.source.OSM() })],
    view: new ol.View({ center: [0, 0], zoom: 2 }),
    controls: [new ol.control.Zoom()],
  });

  symMap.on("moveend", () => {
    if (editing && symModel && $("symDoc")) {
      drawSymbologyPreview(editing.name, $("symDoc").value);
    }
  });
}

/**
 * Frames the map on a layer's own extent.
 *
 * <b>Only when the layer changes.</b> Refitting after every edit would take the view away from
 * wherever somebody had panned to, which is the fault this map exists to fix.
 *
 * @param {string} name the layer
 * @returns {Promise<void>} when the view has moved, or immediately when it cannot
 */
async function symFrameMap(name) {
  if (!symMap) return;

  try {
    const described = await api(`${layerUrl(name).replace(location.origin, "")}?f=json`);
    const e = described.extent;

    if (!e || !Number.isFinite(e.xmin) || !(e.xmax > e.xmin) || !(e.ymax > e.ymin)) return;

    // <b>The layer's extent is in the layer's reference, and the map is not.</b> Handing those
    // four numbers to `fit` unconverted is what put the map in the Gulf of Guinea at maximum
    // zoom, which is why the owner's screen was open ocean: a Turkish 4326 extent read as
    // metres is a box nineteen metres wide near where the equator meets the prime meridian.
    const box = await symInMapSr(e);

    if (!box) return;

    symMap.updateSize();
    symMap.getView().fit(box, { padding: [24, 24, 24, 24] });
  } catch {
    // A layer whose document cannot be read still gets a map; it opens where the last one was.
  }
}

/**
 * An ArcGIS extent in the map's reference, or null when it cannot be put there.
 *
 * <b>Asked of this server rather than computed here.</b> `GeometryServer/project` is the
 * operation this product already publishes for exactly this, and it reaches the datastore's
 * PROJ — so a layer on a national grid is framed as correctly as one on Web Mercator, and the
 * console carries no projection library and no table of references.
 *
 * <b>A grid of points, not two corners.</b> A reprojected rectangle is not a rectangle, and on
 * a wide extent the corners alone put the edges kilometres out. Nine points is the cheapest
 * shape that catches the bulge.
 *
 * @param {object} e The extent, with its own `spatialReference`.
 * @returns {Promise<number[]|null>} `[minx, miny, maxx, maxy]` in the map's reference.
 */
async function symInMapSr(e) {
  const from = e.spatialReference
    ? (e.spatialReference.latestWkid || e.spatialReference.wkid)
    : null;

  // <b>102100 is 3857.</b> ArcGIS's own code for Web Mercator, and a layer published through
  // that face reports it rather than the EPSG number.
  if (!from || from === SYM_MAP_SR || from === 102100) {
    return [e.xmin, e.ymin, e.xmax, e.ymax];
  }

  const xs = [e.xmin, (e.xmin + e.xmax) / 2, e.xmax];
  const ys = [e.ymin, (e.ymin + e.ymax) / 2, e.ymax];

  const points = [];

  for (const x of xs) for (const y of ys) points.push({ x, y });

  const form = new URLSearchParams({
    f: "json",
    inSR: String(from),
    outSR: String(SYM_MAP_SR),
    geometries: JSON.stringify({ geometryType: "esriGeometryPoint", geometries: points }),
  });

  const headers = { "Content-Type": "application/x-www-form-urlencoded" };

  if (token) headers.Authorization = "Bearer " + token;

  try {
    const response = await fetch(
      "/rest/services/Utilities/Geometry/GeometryServer/project",
      { method: "POST", headers, body: form });

    if (!response.ok) return null;

    const said = await response.json();
    const got = (said.geometries || []).filter(g => Number.isFinite(g.x) && Number.isFinite(g.y));

    if (got.length < 2) return null;

    const box = [
      Math.min(...got.map(g => g.x)), Math.min(...got.map(g => g.y)),
      Math.max(...got.map(g => g.x)), Math.max(...got.map(g => g.y)),
    ];

    // <b>A projection that answered with infinities has not answered.</b> Out-of-range
    // coordinates come back as numbers that pass every comparison and frame nothing.
    return box.every(Number.isFinite) && box[2] > box[0] && box[3] > box[1] ? box : null;
  } catch {
    // <b>No frame rather than a wrong one.</b> The map keeps the view it had, which is the
    // world, and the picture is still drawn — the reader can find the layer by panning.
    return null;
  }
}

/**
 * Asks for the picture and shows it, or says why there is none.
 *
 * <b>Two lines, and they answer two questions.</b> The strip beside Store says which of the two
 * appearances the picture is of; the caption over the picture says whether there is a picture at
 * all, and why not when there is not. They were one element, so a preview that failed to draw
 * replaced the sentence that says whether anything is stored.
 */
async function drawSymbologyPreview(name, body) {
  const image = $("symPreview");
  const none = $("symPreviewNone");
  const state = $("symPreviewState");
  const cap = $("symPreviewCap");

  const says = why => { if (cap) cap.textContent = why; };

  if (!image) return;

  // <b>One draw at a time, and the last one wins.</b> Panning fires faster than the server
  // answers, so without this the picture somebody ends on is whichever request happened to
  // return last — the extent they panned away from, painted over the one they are looking at.
  const mine = ++symDrawing;

  try {
    const headers = { "Content-Type": "application/json" };
    if (token) headers.Authorization = "Bearer " + token;

    // <b>The map's own extent and size, when there is a map.</b> Without them the endpoint
    // frames itself on the features and answers 336x224 — which is still what the thumbnails
    // and the content list want, and what this screen falls back to when OpenLayers is not
    // there.
    const at = symExtent();

    // <b>And which reference those four numbers are in, which is the whole of the repair.</b>
    // The map is in Web Mercator because that is what the basemap tiles are; the endpoint used
    // to read `bbox` as the layer's own, and every seeded fixture is 3857 so the two agreed in
    // every test. On a 4326 layer they did not, and the picture was of somewhere else.
    const where = at
      ? `?bbox=${at.box.join(",")}&size=${at.wide}x${at.tall}&bboxSR=${SYM_MAP_SR}`
      : "";

    const response = await fetch(
      `/admin/layers/${encodeURIComponent(name)}/symbology/preview${where}`,
      { method: "POST", headers, body });

    if (!response.ok) {
      let why = `${response.status}`;

      try {
        const said = await response.json();
        why = (said && said.error && said.error.message) || why;
      } catch { /* not json */ }

      image.hidden = true;
      none.hidden = false;
      says(why);

      return;
    }

    // <b>A `data:` URL rather than a blob, and it is not a preference.</b> One element gets a
    // new picture on every edit, and an object URL has a lifetime somebody has to manage:
    // revoke it early and the load in flight fails, revoke it late and they accumulate. Either
    // way the element fires `error` for an abandoned load — measured here as the console
    // harness recording *never arrived* for a picture that was on screen the whole time, which
    // is the hardest kind of error to notice. A data URL has no lifetime. These are two
    // kilobytes; a large one would be a different trade.
    // <b>Checked before it is shown, because a 200 is not a picture.</b> Anything in front of
    // this console — a proxy, a portal's sign-in page, a test harness that traps writes — can
    // answer a POST with a cheerful `200 application/json`, and assigning that to an `<img>`
    // produces a broken-image glyph and a load error, which reads as *this style draws nothing*.
    // Measured on 2026-09-03: the suite's write trap answered `{}` and the preview showed an
    // empty frame with no explanation.
    const kind = response.headers.get("Content-Type") || "";

    if (!kind.startsWith("image/")) {
      image.hidden = true;
      none.hidden = false;
      says("The preview is not available here.");

      return;
    }

    const bytes = new Uint8Array(await response.arrayBuffer());

    let binary = "";

    for (let i = 0; i < bytes.length; i++) {
      binary += String.fromCharCode(bytes[i]);
    }

    if (mine !== symDrawing) return;

    image.src = "data:image/png;base64," + btoa(binary);
    image.hidden = false;
    none.hidden = true;
    // <b>The caption is about the picture, and it used to be about saving.</b> It read *Not
    // stored yet — this is what Store would keep*, which is true of a picture drawn from an
    // edited form and false forever after a Store, because nothing redraws the preview when a
    // Store succeeds and nothing else ever corrected the sentence. A design review read it
    // immediately after storing 256 classes and it still said *not stored yet*.
    //
    // <b>One question, one place.</b> Whether a document is stored is answered by the state line
    // at the top of the section; this sentence answers a different question — which of the two
    // appearances the picture beside it is of — and now says only that.
    symSayState();
    says("rendered by this server");
  } catch (e) {
    image.hidden = true;
    none.hidden = false;
    says(e.message || "The preview could not be drawn.");
  }
}

/**
 * The conversion's losses, or nothing when there are none.
 *
 * <b>And a count on the tab that holds them.</b> ADR-033 accepted a lossy conversion on the
 * condition that it says so, and the block saying so was at the bottom of a page nobody reached
 * — so the mitigation was true of the document and false of the screen. A badge is the same
 * sentence in the one place a reader cannot scroll past.
 */
function drawLosses(losses) {
  const box = $("symLoss");
  const list = $("symLossList");
  const badge = $("symLossBadge");

  if (!box || !list) return;

  if (!losses || losses.length === 0) {
    box.hidden = true;
    list.innerHTML = "";

    if (badge) badge.hidden = true;

    return;
  }

  list.innerHTML = losses.map(l => `<li>${h(l)}</li>`).join("");
  box.hidden = false;

  if (badge) {
    badge.textContent = String(losses.length);
    badge.hidden = false;
  }
}

/**
 * The bounds of a service that has no layers, and the panel for changing them.
 *
 * <b>Shown only for a system service, and it reads before it draws.</b> A control that displays a
 * figure it did not read is this repository's most repeated fault — the geometry service's own row
 * carried a hard-coded `started` pill until the day this was written — so the panel stays hidden
 * until the server has answered, and every field it fills comes from that answer.
 *
 * The three numbers are three different facts and the placeholder is how they stay distinguishable:
 * an empty box with a placeholder of `10 (default)` says *nobody has set this and the server would
 * use ten*, which is not what `10` in the box would say.
 *
 * @param {string} name the service's name within its folder
 * @returns the limits document for a system service, or null for anything else
 */
async function loadServiceLimits(name, folder) {
  const panel = $("serviceLimits");
  if (!panel) return;

  panel.hidden = true;
  $("limSave").hidden = true;
  $("limClear").hidden = true;

  let limits;
  try {
    // <b>The folder, because `/admin/services/{name}` used to mean two things.</b> D-39: a
    // published service named Geometry would have had its settings land on the system geometry
    // server. The server tells them apart by folder now, so a call that omits it is a call about
    // the root — which is where the system services are not.
    limits = await api(`/admin/services/${encodeURIComponent(name)}/limits`
      + `?folder=${encodeURIComponent(folder || "")}`);
  } catch {
    // A feature service has no such route, which is the ordinary case rather than a fault. Null
    // is the answer to "is this a system service", which is what the caller asked.
    return null;
  }

  const box = (id, stored, fallback) => {
    $(id).value = stored ?? "";
    $(id).placeholder = String(fallback);
  };

  box("limDeadline", limits.deadlineSeconds, limits.defaultDeadlineSeconds);
  box("limWait", limits.waitSeconds, limits.defaultWaitSeconds);
  box("limPreflight", limits.preflightPairs, num(limits.defaultPreflightPairs));
  box("limIdle", limits.idleSeconds, limits.defaultIdleSeconds);

  // The three an operator cannot set here, said with the reason each one is fixed. A number
  // without its reason invites the question this list exists to answer.
  // <b>The number in mono, the reason in prose.</b> `dl.facts` sets its values in the monospace
  // face because they are usually table names and identities; a sentence rendered that way reads
  // as output rather than as an explanation, which is what it looked like on the first attempt.
  const fixed = (value, why) =>
    `<dd><b class="mono">${value}</b> <span class="why">${why}</span></dd>`;

  $("limFixed").innerHTML =
    `<dt>Worker processes</dt>`
    + fixed(num(limits.workers),
        "per server, from <code>Graticula:OverlayWorkers</code>. Total memory exposure is this "
        + "times the heap below.")
    + `<dt>Heap per worker</dt>`
    + fixed("1 GB",
        "the other half of the timeout: a request can exhaust memory well inside any deadline "
        + "worth having, so the process has a ceiling and dies instead of the server.")
    + `<dt>Vertices per request</dt>`
    + fixed(num(limits.maximumVertices),
        "every operation here is one pass over the coordinates, so input size bounds the work "
        + "exactly — a cap is the right mechanism rather than a preference.");

  $("limNote").innerHTML =
    `<b>In force now:</b> work is cut off after ${h(limits.effectiveDeadlineSeconds)} s, `
    + `a request queues for at most ${h(limits.effectiveWaitSeconds)} s, `
    + `${limits.effectivePreflightPairs
        ? `work above ${num(limits.effectivePreflightPairs)} segment pairs is refused before it starts`
        : "there is no pre-flight"}, and `
    + `${limits.effectiveIdleSeconds
        ? `an unused worker is reclaimed after ${h(limits.effectiveIdleSeconds)} s`
        : "workers are kept for ever"}. `
    + `A change applies from the next operation; nothing is restarted.`;

  $("limSave").hidden = false;
  $("limClear").hidden = false;
  panel.hidden = false;
  return limits;
}

/**
 * Everybody with an account, and the form that makes one.
 *
 * <b>This is what <a href="../../../docs/architecture-debt.md">D-56</a> was.</b> A deployment had
 * exactly one account for ever, so every claim about what a publisher sees was reasoning rather
 * than measurement — including ADR-034's, whose condition 1 asks for a test that signs in
 * *without* `admin:manageServer`.
 *
 * <b>The role picker says what each role carries, from the server.</b> `/admin/members` reports
 * the grants beside the roles for exactly this: a picker whose options are words an administrator
 * has to look up elsewhere is a picker that gets used wrongly, and a copy of ADR-018 §2a written
 * into this file would be the copy nobody updates.
 */
/**
 * Two letters for a name, for the square that stands in for a face.
 *
 * <b>A separator is a word boundary and so is a capital.</b> `ci_second_admin` gives CS and
 * `RootOperator` gives RO; a name with neither gives its first two letters. One letter would be
 * ambiguous across a directory of any size and three do not fit a 26-pixel square.
 *
 * @param {string} name the member's name
 * @returns {string} one or two upper-case letters
 */
function initialsOf(name) {
  const words = String(name || "")
    .replace(/([a-z\d])([A-Z])/g, "$1 $2")
    .split(/[\s._@-]+/)
    .filter(Boolean);

  if (words.length === 0) return "?";
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();

  return (words[0][0] + words[1][0]).toUpperCase();
}

/**
 * A stable hue for a name, so the square is the same one every time.
 *
 * <b>Derived, not stored and not random.</b> The same argument the generated symbology makes:
 * a colour a reader learns is only worth learning if it is the same tomorrow and on another
 * deployment. Lightness and saturation are fixed so every square is legible with the same ink
 * on it; only the hue moves.
 *
 * @param {string} name the member's name
 * @returns {string} an hsl() colour
 */
function initialsHue(name) {
  let hash = 0;

  for (const ch of String(name || "")) {
    hash = (hash * 31 + ch.codePointAt(0)) % 360;
  }

  return `hsl(${hash} 42% 88%)`;
}

async function loadMembers() {
  const answer = await api("/admin/members");
  const rows = answer.members || [];

  memberGrants = answer.grants || {};

  // Kept for the transfer picker: a removal has to offer somebody, and the listing is where the
  // names already are. Only the ones who can sign in, because the server refuses a disabled
  // target and a control that exists to be told no is worse than no control.
  memberNames = rows.filter(m => !m.disabled).map(m => m.name);

  // Only the ones who can sign in, which is the server's own test: a disabled administrator
  // cannot recover a server, so they do not count towards there being one.
  administrators = rows
    .filter(m => !m.disabled && (m.roles || []).includes("administrator"))
    .map(m => m.name);

  const fill = (id, values, chosen) => {
    $(id).innerHTML = values.map(v =>
      `<option value="${h(v)}"${v === chosen ? " selected" : ""}>${h(v)}</option>`).join("");
  };

  fill("mRole", answer.roles || [], "publisher");
  // <b>The ladder, in the server's order.</b> `UserTypes.All` is viewer, editor, creator,
  // unrestricted — each ceiling contains the one below — and the console needs the order to know
  // whether a change takes something away. Ranking the names here would be a second copy of a
  // fact the platform already states, and the two would part company the day a type is added.
  memberUserTypes = answer.userTypes || [];

  fill("mType", answer.userTypes || [], "creator");
  describeRole();

  $("memberCount").textContent =
    `${rows.length} member${rows.length === 1 ? "" : "s"}`;

  $("membersPager").innerHTML = pagerFor("members", rows.length);

  // <b>An empty listing says so, like its two siblings in this file.</b> `loadSources` answers *None
  // registered.* and `loadRoles` answers *No roles, which cannot happen: the schema seeds five.*; this
  // one rendered a header over blank white. Probably unreachable — root always exists — and a screen
  // that would look broken if it were is worth one line. Design review 2026-08-19.
  $("members").innerHTML = rows.length === 0
    ? `<tr><td colspan="7" class="empty">No members, which cannot happen while you are reading this:
         you are signed in as one. If you are seeing this, the directory answered and the answer was
         empty — look at the platform store rather than at this screen.</td></tr>`
    : pageOf("members", rows).map(m => `
    <tr>
      <!--
        <b>Initials, because a list of names is read by shape before it is read by word.</b>
        Handoff 2026-09-04. No image and no request: two letters on a tinted square, the tint
        derived from the name so it is the same square every time and on every deployment — the
        same determinism the generated symbology uses, and for the same reason.
      -->
      <td class="name"><span class="whorow"><span class="avatar"
        style="--avatar:${h(initialsHue(m.name))}" aria-hidden="true">${h(initialsOf(m.name))}</span>
        <span>${h(m.name)}${m.displayName
          ? `<span class="val" style="display:block;font-weight:400">${h(m.displayName)}</span>`
          : ""}</span></span></td>
      <!--
        <b>The role keeps its select and takes the pill's hue.</b> The handoff draws a pill —
        admin teal, publisher violet, other grey — and a pill is not editable; this is the one
        control on the row that changes what somebody may do, so it stays a control and wears the
        colour rather than being replaced by a label that carries it.
      -->
      <td><select class="rolepick" data-role-is="${h((m.roles || [])[0] || "none")}"
        data-member-role="${h(m.name)}">
        ${(answer.roles || []).map(r =>
          `<option value="${h(r)}"${m.roles.includes(r) ? " selected" : ""}>${h(r)}</option>`)
          .join("")}
        <option value=""${m.roles.length === 0 ? " selected" : ""}>— none —</option>
      </select></td>
      <!--
        <b>A control, because it was a word and the word was the answer to a question nobody
        could act on.</b> The owner read this column and asked how to change a user type; the
        answer was that they could not — not here, not through the API, not at all after the
        member was created — while ADR-018's own privilege table has said that admin:manageRoles
        grants *roles and user types* since it was written.

        <b>Beside the role and shaped like it, because they are two halves of one answer.</b>
        §1: what somebody may do is this ceiling intersected with what that role grants. Putting
        them in different idioms — one a control, one a label — said the ceiling was a property
        of the account rather than a decision somebody makes.
      -->
      <td><select class="rolepick" data-member-type="${h(m.name)}"
        data-was-type="${h(m.userType)}">
        ${(answer.userTypes || []).map(t =>
          `<option value="${h(t)}"${t === m.userType ? " selected" : ""}>${h(t)}</option>`)
          .join("")}
      </select></td>
      <td>${pill(m.disabled ? "disabled" : "active")}</td>
      <td class="num">${num(m.ownsServices)}</td>
      <td class="val">${h(String(m.createdAt).slice(0, 10))}</td>
      <td class="acts" style="text-align:right">
        <button class="tiny" data-member-password="${h(m.name)}">Set password</button>
        <button class="tiny ${m.disabled ? "" : "danger"}"
          data-member-state="${h(m.name)}" data-to="${m.disabled ? "enable" : "disable"}"
          >${m.disabled ? "Enable" : "Disable"}</button>

        <!--
          <b>Remove reads before it asks, and asks before it acts.</b> ADR-015 §6c puts the
          judgement with the operator: what a member owns keeps serving under somebody's name,
          and only a person can say whose. So this fetches the holdings, and the dialog is
          where the choice is made rather than a confirm that guesses one.
        -->
        <button class="tiny danger" data-member-remove="${h(m.name)}"
          title="Remove this member, and decide what happens to what they own">Remove</button>
      </td>
    </tr>`).join("");
}

/**
 * Shows a password the server has just issued, and says what it is for.
 *
 * <b>On the page rather than in a toast, because this is the only moment it exists.</b> Only an
 * Argon2id hash is stored, so a reader who looks away has to issue another — a message that fades
 * after four seconds is the wrong container for a value that cannot be recovered.
 *
 * <b>Selectable, and not a copy button.</b> The clipboard API needs a permission prompt in some
 * browsers and silently does nothing in others; a `readonly` field with its text already selected
 * works everywhere and shows the reader exactly what they are taking.
 */
function showIssuedPassword(issued) {
  const dialog = $("issued");
  if (!dialog) return;

  $("issuedTitle").textContent = `Give this to ${issued.name}`;
  $("issuedValue").value = issued.password;
  $("issuedNote").textContent = issued.note;

  // <b>Modal, so the page behind it is inert.</b> `show()` would leave the row's buttons clickable
  // behind the dialog — including *Set password* again, which would issue a second one and make the
  // first useless while it is still on screen being read.
  dialog.showModal();
  $("issuedValue").select();
}

/** What each role carries, kept from the last listing so the picker can explain itself. */
let memberGrants = {};

/** Names the privileges of the role the form has selected. */
function describeRole() {
  const role = $("mRole")?.value;
  const carries = memberGrants[role] || [];

  $("mGrants").innerHTML = carries.length
    ? `<b>${h(role)}</b> carries ${carries.map(p => `<code>${h(p)}</code>`).join(" ")}`
    : `<b>${h(role)}</b> carries nothing. Reading is governed by each layer's sharing rather than `
      + `by a privilege, so a viewer reads plenty and can change nothing (ADR-018 §2).`;
}

/**
 * The two operational widgets beside the folder rail.
 *
 * <b>Both report what this server actually measures, which is not what the reference shows.</b> The
 * brief asked for *Service health* and *Server resources* and said plainly: do not fabricate backend
 * data. So:
 *
 * - **Health is counted from the rows already on screen.** No request, no new endpoint, and it
 *   cannot disagree with the table beside it. It has two states and not three: `service.status` is
 *   `started` or `stopped` by a check constraint, so an *Error* row would be a state this server
 *   cannot be in — the reference has one because its services can fail to start, and ours cannot.
 * - **Resources come from `/admin/health`**, which reports process CPU time, managed heap bytes,
 *   tile-cache size and uptime. Two of the reference's three are percentages of a quota; this server
 *   has no memory limit and no disk quota, so those are shown as the figures they are. CPU *is* a
 *   ratio and is shown as one: process CPU time over wall-clock times cores, since start.
 *
 * <b>And no sparklines.</b> The reference draws three, and a sparkline claims a history — nothing
 * here keeps one, so drawing a wiggle from a single sample would be the most literal possible
 * version of inventing data.
 */
function drawHealthWidget(rows) {
  const widget = $("healthWidget");
  if (!widget) return;

  const real = rows.filter(r => !r.system);

  if (real.length === 0) {
    widget.hidden = true;
    return;
  }

  const started = real.filter(r => r.status !== "stopped").length;
  const stopped = real.length - started;

  $("hwTotal").textContent = num(real.length);

  // The arc is the real proportion: one custom property, and the conic-gradient does the geometry.
  $("hwDonut").style.setProperty("--started", `${started / real.length}turn`);
  $("hwDonut").classList.toggle("none", started === 0 && stopped === 0);

  const share = n => `${Math.round((n / real.length) * 100)}%`;

  const line = (kind, name, count, note) =>
    `<div class="legendrow ${kind}"${note ? ` title="${h(note)}"` : ""}>`
    + `<span class="key">${name}</span><b>${num(count)}</b><em>${share(count)}</em></div>`;

  // <b>Three rows, and the third is a state this server cannot enter.</b> The owner asked for
  // Started/Stopped/Error twice — in the reference and in the refinement brief — so it is here, at
  // zero, dimmed, and with the reason on hover. `service.status` is `started` or `stopped` by a
  // check constraint: a service that cannot be started is a service that was never created, and a
  // request that fails is a request, not a service state. Showing it undimmed would imply a
  // detection this server does not do; dropping it would ignore a direct instruction twice given.
  $("hwRows").innerHTML =
    line("ok", "Started", started)
    + line("warn", "Stopped", stopped)
    + line("alert impossible", "Error", 0,
        "This server has no error state: a service is started or stopped by a check constraint on "
        + "the column. Shown at zero so the absence is visible rather than assumed.");

  widget.hidden = false;
}

/**
 * What the process is spending, from `/admin/health`.
 *
 * <b>Read here rather than shared with the health line in the top bar</b>, because that line reports
 * the *platform store* and this reports the *process*, and the two answer different questions —
 * merging them is how a screen ends up saying the server is fine because the database is.
 */
async function drawResourceWidget() {
  const widget = $("resourceWidget");
  if (!widget || !may("admin:manageServer")) return;

  let health;
  try {
    health = await api("/admin/health");
  } catch {
    widget.hidden = true;
    return;
  }

  const runtime = health.runtime;
  if (!runtime) { widget.hidden = true; return; }

  // Process CPU time over wall-clock times cores: a real ratio, and the only one available.
  const busy = runtime.uptimeMilliseconds > 0
    ? runtime.cpuMilliseconds / (runtime.uptimeMilliseconds * (runtime.cores || 1))
    : 0;

  remember("cpu", busy * 100);
  remember("heap", runtime.heapBytes / 1048576);
  remember("tiles", health.tileCache?.megabytes ?? 0);

  $("rwRows").innerHTML =
    meterRow("CPU", "cpu", `${(busy * 100).toFixed(1)}%`, true)
    + meterRow("Heap", "heap", bytesPlain(runtime.heapBytes))
    + meterRow("Tiles", "tiles", `${(health.tileCache?.megabytes ?? 0).toFixed(1)} MB`)
    + `<div class="meter"><span class="k">Uptime</span><span></span>`
      + `<span class="t">${h(duration(runtime.uptimeMilliseconds).replace(/<[^>]+>/g, ""))}</span></div>`;

  // <b>The caveats live on the glyph, not under the numbers.</b> Plain text rather than markup,
  // because a `title` is plain text — and it has to stay complete: CPU being a ratio since start and
  // the lines being the page's own samples are the two things that stop these numbers being
  // misread, so shortening them to fit a tooltip would be the wrong economy.
  $("rwWhy").title =
    `CPU is process time since start, over ${runtime.cores} cores — not an instantaneous load. `
    + `Heap and tiles are figures rather than percentages: this server has no memory limit and no `
    + `disk quota for them to be a share of. The lines are samples this page has taken since you `
    + `opened it, five seconds apart — not a history the server keeps, because it keeps none.`;

  widget.hidden = false;
  keepSampling();
}

/**
 * Samples of a metric, in the order they were observed.
 *
 * <b>This is how the sparklines are honest.</b> The brief asked for the reference's sparklines and,
 * in the same breath, said not to fabricate data — and both are possible, because a line does not
 * have to come from the server's history. It can come from *ours*: each visit to this screen reads
 * `/admin/health`, and every reading is kept here in order. The first sample draws nothing, the
 * second draws a segment, and after a minute there is a minute of real measurement.
 *
 * <b>Bounded and thrown away on reload</b>, which is the honest bargain: the alternative is a
 * server-side time series, and that is a decision about storage and retention rather than a chart.
 * Sixty samples at five seconds is five minutes, which is as far back as a line 74 pixels wide can
 * say anything about anyway.
 */
const samples = new Map();
const SAMPLE_LIMIT = 60;

function remember(key, value) {
  const series = samples.get(key) ?? [];
  series.push(Number.isFinite(value) ? value : 0);
  if (series.length > SAMPLE_LIMIT) series.shift();
  samples.set(key, series);
}

/**
 * One resource row: name, sparkline, value.
 *
 * <b>Inline SVG rather than a canvas.</b> A canvas needs a device-pixel-ratio dance to avoid looking
 * soft, and this is four polylines — the markup is shorter than the drawing code would be, and it
 * scales with the row.
 */
function meterRow(label, key, text, warm = false) {
  const series = samples.get(key) ?? [];

  const chart = series.length < 2
    ? `<span class="waiting">sampling…</span>`
    : sparkline(series);

  return `<div class="meter${warm ? " warm" : ""}"><span class="k">${h(label)}</span>`
    + `<span>${chart}</span><span class="t">${h(text)}</span></div>`;
}

/**
 * A line and a fill under it, scaled to what has been seen.
 *
 * <b>The scale is the observed range, not zero to a guess.</b> Heap sits between 30 and 90 MB and a
 * chart anchored at zero would draw a flat line across the top — which is a picture of the axis
 * rather than of the metric. A flat series gets a flat line in the middle, deliberately: it means
 * *nothing changed*, and inventing a wiggle for it would be the exact thing the brief forbids.
 */
function sparkline(series) {
  const w = 100;
  const h_ = 22;
  const low = Math.min(...series);
  const high = Math.max(...series);
  const span = high - low || 1;

  const at = (value, i) => {
    const x = (i / (series.length - 1)) * w;
    const y = h_ - 2 - ((value - low) / span) * (h_ - 5);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  };

  const line = series.map(at).join(" L ");

  return `<svg viewBox="0 0 ${w} ${h_}" preserveAspectRatio="none" aria-hidden="true">`
    + `<path class="area" d="M ${line} L ${w},${h_} L 0,${h_} Z"/>`
    + `<path class="line" d="M ${line}"/></svg>`;
}

/**
 * Keeps sampling while this screen is the one on show.
 *
 * <b>It stops itself rather than being stopped.</b> There is no teardown hook per screen, so the
 * interval checks whether the widget is still on screen and clears itself when it is not — which
 * also covers navigating away, signing out and the surface changing, without three call sites
 * remembering to cancel it.
 */
let sampler = null;

function keepSampling() {
  if (sampler) return;

  sampler = setInterval(() => {
    const widget = $("resourceWidget");
    const showing = widget && !widget.hidden && widget.offsetParent !== null;

    if (!showing) {
      clearInterval(sampler);
      sampler = null;
      return;
    }

    drawResourceWidget();
  }, 5000);
}

/** Bytes as a plain string, for a widget that has no room for markup. */
const bytesPlain = value => bytes(value).replace(/<[^>]+>/g, " ").trim();

/** Which folder the Server services screen is looking at: null is the root. */
let selectedFolder = null;

/** The filter over the services in that folder. */
let serviceFilter = "";

/**
 * The folder rail — ADR-034 §5h.
 *
 * <b>The root is an entry, not a special case.</b> Their reference shows *Site (root)* at the
 * top of the same list, and a rail that omits the place half the services are is a rail you
 * cannot navigate from. `hosted` and `Utilities` are entries too: `hosted` stopped being a
 * rule when folders became real, and the geometry service is a service in `Utilities`.
 */
async function loadFolders() {
  const { root, folders } = await api("/admin/folders");

  const entry = (name, label, counts, extra = "") => {
    const here = (selectedFolder ?? "") === (name ?? "");
    return `<a href="#/services${name ? "/" + encodeURIComponent(name) : ""}"
      class="rail-item${here ? " on" : ""}"${here ? ' aria-current="page"' : ""}>
      ${icon(name ? "folder" : "root")}
      <span class="rail-name">${h(label)}${extra}</span>
      <span class="rail-count">${counts}</span></a>`;
  };

  $("folders").innerHTML =
    entry(null, "Site (root)", num(root.services))
    + (folders || []).map(f => entry(
        f.name,
        f.name,
        num(f.services + f.systemServices),
        f.reserved ? ' <span class="val" title="Reserved: this folder is where something the'
          + ' server does lives">·</span>' : "")).join("");
}

/**
 * The services in the selected folder, and what each holds.
 *
 * <b>One list, replacing three tables</b> — the owner's objection to what this used to be
 * (ADR-034 §5h). The system services are in it rather than beside it: the geometry service is
 * a service in `Utilities`, and now that a folder is a thing it can be listed as one.
 */
/**
 * The services the list last read, kept so that other panels can ask what kind one is.
 *
 * <b>A cache of a fact, not of a decision.</b> `kind` is written once at registration
 * and never changes, so a stale entry cannot be wrong about it — which is why this is
 * safe to hold and why nothing else about a service is held here. The alternative was
 * a second request per panel to learn one word.
 */
let SERVICE_ROWS = [];

async function loadServices() {
  const [{ services }, system] = await Promise.all([
    api("/admin/featureservices"),
    api("/admin/services").catch(() => ({ services: [] })),
  ]);

  SERVICE_ROWS = services || [];

  const inFolder = (folder) => (folder ?? "") === (selectedFolder ?? "");

  const rows = [
    ...(services || []).filter(s => inFolder(s.folder)).map(s => ({
      qualified: s.qualified,
      name: s.name,
      folder: s.folder,
      kind: s.kind,
      status: s.status,
      sharing: s.sharing,
      layers: s.layers,
      groups: s.groups,
      owner: s.owner,
      empty: s.empty,
      description: s.description,
      system: false,

      // <b>From the listing, rather than worked out here.</b> Both the preview and the status
      // button need one of the service's layers, and the console used to find one by walking
      // the services directory — which cannot see a stopped service at all, since a stopped
      // service answers 503 to the walk. So the row for the one service you most want to
      // start was the row that could not offer a Start button. `cover` is the fix, added to
      // /admin/featureservices for this.
      cover: s.cover
        ? {
            name: s.cover.name,
            url: `/rest/services/${s.qualified}/FeatureServer/${s.cover.layerIndex}`,
          }
        : null,
    })),

    // A service with no layers behind it, carrying its own sharing scope — ADR-018 §3b-i. It
    // has no layers to open and no ceilings to set, so its row offers only what it has.
    ...(system.services || []).filter(y => inFolder(y.folder)).map(y => ({
      qualified: y.folder ? `${y.folder}/${y.name}` : y.name,
      name: y.name,
      folder: y.folder,
      kind: y.kind,

      // <b>From the server, and until 2026-08-17 this was the literal `"started"`.</b> There was
      // no status to read — `system_service` carried sharing and nothing else — so the row
      // printed a pill nobody had asserted. The owner asked the question that found it: *"geometry
      // server'in, startı stop'u, timeout'u vs si yok mu?"*
      status: y.status,
      sharing: y.sharing,
      layers: 0,
      groups: 0,
      owner: null,
      empty: false,
      description: null,
      cover: null,   // it has no layers by definition — ADR-018 §3b-i
      system: true,
    })),
  ];

  const needle = serviceFilter.trim().toLowerCase();
  const shown_ = needle
    ? rows.filter(r => [r.qualified, r.kind, r.owner].some(v => (v || "").toLowerCase().includes(needle)))
    : rows;

  $("serviceCount").textContent = needle
    ? `${shown_.length} of ${rows.length}`
    : `${rows.length} service${rows.length === 1 ? "" : "s"}`;

  // Ten a page. The slice is taken after the filter, so a narrowed list starts at its own
  // first page rather than wherever the unfiltered one was standing.
  const onPage = pageOf("services", shown_);
  $("servicesPager").innerHTML = pagerFor("services", shown_.length);

  const where = selectedFolder ? `the ${selectedFolder} folder` : "the root";

  $("services").innerHTML = shown_.length === 0
    ? `<tr><td colspan="6" class="empty">${rows.length === 0
        ? `Nothing in ${h(where)}. Publishing a layer creates a service; a folder can hold none.`
        : `Nothing in ${h(where)} matches <b>${h(serviceFilter)}</b>.`}</td></tr>`
    : onPage.map(r => {
      const held = [
        r.layers ? `${r.layers} layer${r.layers === 1 ? "" : "s"}` : "",
        r.groups ? `${r.groups} group${r.groups === 1 ? "" : "s"}` : "",
      ].filter(Boolean).join(", ");

      const stopped = r.status === "stopped";

      // <b>The shortcut is gone, and its removal is the fix to a defect it caused.</b> A
      // single-layer service used to open its layer directly, because the drill-in was then a
      // one-row table whose only control was a *Settings* link (owner: *"this is a really
      // meaningless page tbh"*). That page now holds the **service's** settings — so the shortcut
      // meant eight of nine services had no reachable Capabilities or Limits at all, which is what
      // *"tüm servislerden limits ler uçmuş"* was. A row opens its service; the service's list
      // opens a layer.

      // <b>A system row opens as well.</b> It used to be the one row on this screen that was
      // not clickable, on the argument that there are no layers inside to list — true, and it
      // also meant the geometry service's own bounds had nowhere to be edited from.
      // <b>The name is the strongest thing in the row and its metadata is not.</b> Owner brief:
      // *"Service names should be one of the strongest elements in each row. Secondary metadata
      // should have lower visual emphasis. Do not turn every piece of metadata into a badge."* So
      // the kind and the counts are one line of small muted text with a thin separator, and the
      // only badges in the row are the two states.
      // <b>One verb in the row, everything else behind the overflow.</b> Owner brief: *"Keep the
      // Stop action available but visually secondary. Destructive actions such as Delete should NOT
      // compete visually with normal actions. Put less frequently used actions inside an overflow
      // menu where appropriate."* Start/Stop is the thing an operator does here, so it stays; Delete
      // moves into the menu, where its refusal — a service that still holds layers cannot be
      // removed — has room to be a sentence instead of a tooltip.
      return `<tr class="pick" data-service="${h(r.qualified)}">
        <td>${r.cover
          ? `<img class="thumb" alt="" loading="lazy"
               data-thumb="${h(thumbnailFor(r.cover.url))}">`
          : `<div class="thumb empty" title="This service has no layer to draw, so there is no map to show."></div>`}</td>

        <td>
          <span class="name">${h(r.name)}</span>
          <span class="rowmeta">${h(r.kind)}${r.description
            ? `<span class="sep">·</span>${h(r.description)}` : ""}${r.system
            ? ""
            : `<span class="sep">·</span><span class="count">${held || "empty"}</span>`}</span>
        </td>

        <td>${pill(r.status)}</td>
        <td>${r.system
          ? `<select data-service-share="${h(r.name)}">${SCOPES.map(v =>
              `<option value="${v}"${v === r.sharing ? " selected" : ""}>${v}</option>`).join("")}</select>`
          // <b>A link to where it is set, not a dead label.</b> The owner: *"server tarafında
          // sharing mekanizması çok işlemiyor"* — and on this screen it did nothing at all, while
          // the row above it (a system service) carried a working select in the same column. A
          // reader cannot tell a scope that is fixed from one that is set elsewhere unless the
          // screen says which. Sharing stays Studio's (owner, 2026-08-17); what was missing is the
          // route to it.
          : `<a href="${surfaceHref("studio",
                 "service/" + r.qualified.split("/").map(encodeURIComponent).join("/"))}"
               title="Set on this service's Sharing page in Studio — a scope is its owner's decision">${
                 pill(r.sharing)}</a>`}</td>
        <td class="val">${h(r.owner || "—")}</td>

        <td class="acts">${r.system ? `
          <button class="tiny" data-system-status="${h(r.name)}"
            data-system-folder="${h(r.folder || "")}"
            data-to="${stopped ? "start" : "stop"}"
            title="${stopped ? "Answer this service's operations again"
              : "Answer 503 for every operation on this service, without changing who may call it"}"
            ><span class="ico" aria-hidden="true">${stopped ? "▶" : "■"}</span>${stopped ? "Start" : "Stop"}</button>` : `
          <button class="tiny" data-service-status="${h(r.cover ? r.cover.name : "")}"
            data-to="${stopped ? "start" : "stop"}"
            ${r.cover ? "" : "disabled"}
            title="${stopped ? "Serve this again"
              : "Answer 503 for this service, without changing who may see it"}"
            ><span class="ico" aria-hidden="true">${stopped ? "▶" : "■"}</span>${stopped ? "Start" : "Stop"}</button>

          <!--
            <b>A way to the map, from the screen that lists the services.</b> Handoff 2026-09-04.
            Studio content rows have had one since the map moved to the item page; Server had
            none, so an operator checking whether a service draws anything had to open it, find
            the Visualization tab and pick a layer. It crosses to Studio, which is where the map
            is — the same crossing the Sharing column in this row already makes.

            <b>Not offered for a stopped service.</b> It would answer 503, and a control that
            fails on press is worse than one that is not there.
          -->
          ${r.cover && !stopped
            ? `<a class="tiny" href="${h(visHref(r.cover.name, "features") || "#/")}"
                 title="Draw ${h(r.cover.name)} on the map, in Studio">Map</a>`
            : ""}

          <details class="menu">
            <summary title="More" aria-label="More actions">⋯</summary>
            <div class="sheet">
              <button data-service-delete="${h(r.name)}" data-folder="${h(r.folder || "")}"
                class="danger" ${r.empty ? "" : "disabled"}>Delete this service</button>
              ${r.empty
                ? `<div class="note">It holds nothing, so nothing is unpublished by removing it.</div>`
                : `<div class="note">It holds ${h(held)}. Unpublish them first — a service delete
                     never removes what is in it.</div>`}
            </div>
          </details>`}</td>
      </tr>`;
    }).join("");

  // What opening a service does, said once rather than per row.
  $("serviceNote").innerHTML = shown_.some(r => !r.system)
    ? "Select a service to see its layers and set what it offers. A preview samples the "
      + "service's first layer; hover it for how much."
    : "";

  drawHealthWidget(rows);
  section("resources", drawResourceWidget);

  paintPreviews();
}

/**
 * The address of a layer's rendered thumbnail, from the cover URL a listing gives.
 *
 * <b>D-58, and the reason it is one function is that there are five call sites.</b> The `cover`
 * object has a different shape at three of them and the same one thing at all five: a URL of the
 * form `/rest/services/{folder}/{name}/FeatureServer/{index}`. Deriving the picture's address
 * from that is one place to be wrong instead of five, and it means no caller has to know how the
 * server names a thumbnail.
 *
 * <b>It is the server that draws now.</b> This used to be a canvas the browser painted from up to
 * 800 sampled features, which for a layer of 46,041 roads drew 1.7% of it and read as *nearly
 * empty*. The server renders every feature from the layer's own symbology, once, and holds the
 * picture — measured on `ci_many`: 121.3 kB of GeoJSON per viewer per visit against a 17.8 kB
 * PNG that revalidates to a 304.
 *
 * @param {string} url the cover layer's address
 * @returns {string|null} the thumbnail address, or null if the URL is not one we can read
 */
function thumbnailFor(url) {
  const parts = /^\/rest\/services\/(.+)\/FeatureServer\/(\d+)$/.exec(url || "");

  if (!parts) return null;

  return `/admin/thumbnail?service=${encodeURIComponent(parts[1])}&layer=${parts[2]}`;
}

/**
 * Fills every thumbnail on screen, in reading order.
 *
 * <b>The server draws these now — D-58.</b> Each is a map rendered from every feature of the
 * layer and its own symbology, held for five minutes and revalidated with an `ETag`; this walks
 * the slots and fills them.
 *
 * <b>What a failure looks like matters more than that it is handled.</b> A service whose picture
 * is refused gets the same hatched slot a service with no drawable layer gets, with the reason on
 * it — so it looks like a service with no map, which is what it is for this caller. The
 * alternative is the browser's broken-image glyph, which reads as *this console is broken*.
 *
 * <b>One at a time, for the reason the sample it replaces gave.</b> Forty rows is forty
 * pictures, and on a cold cache each of those is a render — firing them together is a load test
 * of our own server dressed up as a screen. Warm they cost about fifteen milliseconds each from
 * memory, so the serial cost is invisible; cold it is the difference between forty concurrent
 * renders and forty sequential ones. Sequential also means the pictures appear in the order
 * somebody reads them.
 */
async function paintPreviews() {
  for (const image of document.querySelectorAll("img.thumb[data-thumb]")) {
    if (image.dataset.drawn) continue;
    image.dataset.drawn = "1";

    const address = image.dataset.thumb;

    if (!address) {
      image.replaceWith(emptyThumb("This service has no map to show.", null));
      continue;
    }

    let holding = pictures.get(address);

    if (!holding) {
      holding = drawnPicture(address);
      pictures.set(address, holding);
    }

    const drawn = await holding;

    if (drawn.href) {
      image.src = drawn.href;
      image.title = "";

      // <b>The attribute is removed, not just read.</b> `img.thumb[data-thumb]:not([src])` is the
      // waiting shimmer, and leaving the attribute on would keep a filled slot matching a rule
      // written for an empty one the moment anything else set `src` later.
      delete image.dataset.thumb;
    } else {
      image.replaceWith(emptyThumb(drawn.why, drawn.note));
    }
  }
}

/**
 * The hatched slot a service with no picture shows.
 *
 * <b>A word in the box when there is one, not only on hover — D-58.</b> The canvas this replaced
 * painted *stopped* or *no preview* where the picture would be, and that row's own text calls
 * hover *"a weak place to carry a correction"*. So a reason short enough to read at 104 pixels is
 * shown; the sentence is on hover for the rest. With neither, the box keeps its em dash, which
 * means *nothing to draw* and always has.
 *
 * @param {string} why the sentence, for hover
 * @param {string|null} note two or three words, shown in the box
 */
function emptyThumb(why, note) {
  const empty = document.createElement("div");
  empty.className = "thumb empty";
  empty.title = why;
  if (note) empty.dataset.note = note;
  return empty;
}

/**
 * Fetches one rendered thumbnail, or says why there is none.
 *
 * <b>`fetch` rather than `<img src>`, and it is not a style preference — D-58.</b> Three things
 * follow from it. The console authenticates with a bearer token, which an `<img>` cannot send.
 * A refusal here is *expected* — a service listed to an administrator whose data is private to
 * somebody else has no picture for this caller — and an `<img>` that 404s is a failed
 * subresource: it paints the browser's broken-image glyph, fills the network tab with red on
 * every page load, and is recorded by the console test harness as a page error, which is
 * correct of the harness and wrong about this. A handled outcome should not look like a
 * failure. And the object URL lets the reason reach the hover text instead of being lost.
 *
 * <b>The HTTP cache still applies.</b> `fetch` honours `Cache-Control` and revalidates with
 * `If-None-Match`, so the server's five-minute window and its 304 work exactly as they would
 * for an `<img>`; the memo beside it is what stops one visit re-asking per render.
 *
 * @param {string} address the thumbnail's URL
 * @returns {Promise<{href: string|null, note: string|null, why: string}>} the picture, or why there is none
 */
async function drawnPicture(address) {
  try {
    const headers = token ? { Authorization: "Bearer " + token } : {};
    const response = await fetch(address, { headers });

    if (!response.ok) {
      if (response.status === 403) {
        return {
          href: null,
          note: "no query",
          why: "This service is configured not to answer queries, so it draws nothing.",
        };
      }

      if (response.status === 503) {
        return { href: null, note: "stopped", why: "This service is stopped." };
      }

      return { href: null, note: null, why: "This service has no map to show." };
    }

    return { href: URL.createObjectURL(await response.blob()), note: null, why: "" };
  } catch (e) {
    return {
      href: null,
      note: "no map",
      why: e.message || "This service has no map to show.",
    };
  }
}

// --------------------------------------------------------------------- drawer

/**
 * Closes the drawer, which now holds one thing: Create.
 *
 * <b>It used to hold the layer editor too</b>, and that was the owner's
 * correction on 2026-08-16 — settings pages inside a slide-over inside a console
 * is nested twice over. Creating is a short form you fill and dismiss, so it
 * stays here; editing a service is a place you navigate to, so it left.
 */
/**
 * Where focus was when the drawer opened, so closing it can put it back.
 *
 * <b>[D-93](../../docs/architecture-debt.md)'s remaining half.</b> Opening the drawer moves focus
 * into it, which the row was written before and does not record. Closing it moved focus nowhere:
 * the element that had focus was inside a subtree that had just been made `inert`, so the browser
 * dropped focus to `<body>` and a keyboard operator restarted from the top of the page. The same
 * defect, on the data-source edit form, is already recorded in this repository as measured — and
 * this is the same fix.
 */
let drawerOpenedFrom = null;

function closeDrawer() {
  $("drawer").classList.remove("on");
  $("drawer").setAttribute("aria-hidden", "true");

  // <b>`inert` as well as `aria-hidden`, because the two are not the same claim.</b> `aria-hidden`
  // tells a screen reader to ignore the subtree; it does nothing about the tab order, so
  // `#drawerClose` stayed focusable while translated off-canvas — measured at x=1986 in a 1440-pixel
  // window by the design review of 2026-08-19, and `offsetParent` is non-null there, so the check this
  // repository normally relies on does not catch it. A focusable descendant of an `aria-hidden`
  // container is also a contradiction in its own right: the reader can reach something it has been
  // told is not there. `inert` removes it from the tab order and from hit testing together.
  $("drawer").inert = true;

  /*
    <b>Focus goes back where it came from</b>, which is what closing a panel owes whoever opened
    it. Without this the browser has nowhere to put focus — the element holding it is inside
    the subtree `inert` has just removed — so it lands on `<body>` and the next Tab starts at
    the top of the page.

    <b>Only if the trigger is still there and still reachable.</b> A submit that succeeded may have
    redrawn the list the button was in; `isConnected` answers that, and `offsetParent` answers the
    case this console has met before, where an element survives inside a hidden view. When neither
    holds, focus goes to the main region rather than being left wherever the browser put it.
  */
  const back = drawerOpenedFrom;
  drawerOpenedFrom = null;

  if (back?.isConnected && back.offsetParent !== null) {
    back.focus();
    return;
  }

  $("app")?.focus();
}

/**
 * The layer editor: its own page, a left column of short settings pages, one Save.
 *
 * <b>The structure is taken from ArcGIS Server Manager, on the owner's reference,
 * and not from taste.</b> Three visual concepts were built and thrown away first;
 * what was wrong with them was the information architecture, not the palette. What
 * their screen does that ours did not:
 *
 * · <b>Settings are paginated, not stacked.</b> General, Capabilities, Pooling,
 *   Caching — each page short enough to read without scrolling. Ours was eight
 *   sections in one column.
 * · <b>A breadcrumb names the object being edited</b> — theirs reads
 *   `Editing: Site (root) > EGDB > _06Z_Wind_Gust_Day_3`, so you always know what
 *   Save will change.
 * · <b>One Save and one Cancel for the session</b>, always visible, rather than a
 *   button beside every control.
 * · <b>Capabilities are names in a grid</b>, seventeen visible at once, with no
 *   prose. A greyed, checked box states a fact — *Mapping (always enabled)* — which
 *   is the same device we need for tiles on a registered layer.
 * · <b>Numbers read as sentences with the unit outside the box</b>: *The maximum
 *   time a client can use a service: [600] seconds*.
 * · <b>Help is a link.</b> Not a paragraph under each control.
 *
 * Their pages do not map one-to-one onto ours and are not forced to: we have no
 * instance pool, so *Pooling* becomes *Limits*, which is where ADR-031's ceilings
 * and Q-113's live.
 *
 * The page names are also the route: each is an address under
 * `#/layer/<name>/<page>`, so the left column is six ordinary links.
 */
/**
 * A layer's settings pages, and which surface each belongs to.
 *
 * <b>ADR-034 §5c already said this and the code did not do it.</b> Its table puts *Sharing*,
 * *Symbology* and *Cache lifetime* in Studio and the capability ceilings and limits in Server,
 * with the sentence: *"a layer therefore appears in both, and that is correct: its limits are the
 * server's business and its appearance is the publisher's."* The editor was built as one screen in
 * Server holding all six pages, so three of §5c's Studio rows were implemented in Server.
 *
 * The owner, looking at the Server services screen: *"aslında bir servisin private mi organization
 * mu public mi olduğu studio tarafında ayarlanacak."* — whether a service is private,
 * organisation or public will be set on the Studio side. They are restating their own decision,
 * which is the clearest possible sign it had not been carried out.
 *
 * <b>The split is by whose act it is, not by who is allowed.</b> An administrator may do all of
 * it and still wants them apart: stopping a service and choosing who sees it are different
 * decisions with different consequences, and a screen that mixes them invites the wrong one.
 */
const LAYER_PAGES = {
  // <b>Capabilities and Limits are not here, and that is the correction.</b> Owner 2026-08-17: *"bir
  // serviste n tane katman olabilir. ama bu her katmanın kendi ayarı olacağı anlamına gelmez. her
  // servisin kendi ayarları olur… bu tamamen saçmalık."* They are right, the storage always agreed
  // with them — `capability_ceiling`, `max_record_count` and the rest are columns on `service` — and
  // the screens did not: three layers of one service each had a Capabilities page reading and writing
  // the same row. See D-61, and `SERVICE_PAGES` below for where they went.
  general: "server",
  endpoints: "server",

  // <b>Sharing left this list on 2026-08-18 — see `SERVICE_PAGES`.</b> It is a service's scope,
  // not a layer's: `service.sharing` is the column the serving path reads, `layer.sharing` is
  // vestigial, and a layer page for it gave one setting as many screens as the service had layers.
  // Kept as a comment rather than deleted, because *why is sharing not here* is the question
  // somebody will have.
  //
  // <b>`maintenance` is what the page became once the scope left it.</b> It held two unrelated
  // things — who may read the service, and unpublishing this layer — and only the first belonged to
  // the service. The name is not new: ADR-034 §5c records *Maintenance* as the section the split of
  // 2026-08-17 broke up, and this is the half that stayed with the layer. It is Studio's for the
  // reason that section gives — *"Delete layer is a decision about content, and the person who
  // published it unpublishes it."*
  maintenance: "studio",

  // <b>Symbology is a layer's own, and it is the one appearance fact that is.</b> ADR-033
  // §5a stores a canonical document per layer, and the endpoint behind this page asks for
  // `content:publishFeatures` — choosing what a layer looks like is the job of whoever
  // published it. It is not the D-61 mistake returning: the *service* style orders and
  // filters across layers and stays on the service (§5d); this is one layer's symbol.
  symbology: "studio",

  caching: "studio",
};

const EDIT_PAGES = Object.keys(LAYER_PAGES);

/**
 * A service's own settings pages, and which surface each belongs to.
 *
 * <b>One settings object per service, which is what the reference does and what the schema always
 * said.</b> The owner pointed at ArcGIS Server Manager: a service has *General, Parameters,
 * Capabilities, Pooling, Processes, Caching, Item Description* — all on the service, once, however
 * many layers are inside it. Ours were on each layer, so a service with four layers looked like four
 * configurations of one thing.
 *
 * <b>The surface split is the same rule as a layer's</b> (ADR-034 §5c): what the service *will
 * spend* is the server's business and *who may read it* is the publisher's.
 */
const SERVICE_PAGES = {
  capabilities: "server",
  limits: "server",

  // <b>Sharing is a service's setting and was the one D-61's repair missed.</b> D-61 moved
  // Capabilities and Limits off the layer pages because their columns are on `service`;
  // `service.sharing` is also on `service` — the endpoint behind the old layer page writes it, and
  // has since migration 11 — and sharing stayed a layer page anyway. So a service with three layers
  // had three Sharing pages editing one row, which is the same defect with a different subject.
  //
  // <b>Studio's, by owner decision 2026-08-17:</b> *"aslında bir servisin private mi organization
  // mu public mi olduğu studio tarafında ayarlanacak."* That decision is unchanged; what changes is
  // which *object* the page hangs off.
  sharing: "studio",
};

/** The service pages this surface owns. */
const servicePagesOf = surface =>
  Object.keys(SERVICE_PAGES).filter(page => SERVICE_PAGES[page] === surface);

/** The pages this surface owns, in the order they are listed above. */
const pagesOf = surface => EDIT_PAGES.filter(page => LAYER_PAGES[page] === surface);

/** Which layer's page is open, and which of its settings pages. */
let editing = null;

/**
 * What has been typed and not saved, per layer, for as long as this tab is open.
 *
 * <b>Instead of a confirmation dialog, and deliberately.</b> The exits from a
 * settings page are the breadcrumb, the tab strip and the browser's own Back — and a
 * hashchange cannot be cancelled once it has fired, so a guard there would have to
 * navigate the reader back afterwards and would leave a history entry pointing at a
 * page they were sent away from. Keeping the values is the better answer to the same
 * risk: nothing is lost, so nothing needs asking about. Returning to the layer puts
 * them back and says so.
 *
 * <b>Cancel is the one exit that clears it</b>, because Cancel is the reader saying
 * *discard*, which is the only unambiguous signal in the set.
 */
const unsaved = new Map();      // layer name -> Map(control id -> value)

/** Reads every control on the open editor, so a redraw can put them back. */
function editedValues() {
  const values = new Map();
  for (const el of document.querySelectorAll(
    "#editPages input, #editPages select, #editPages textarea")) {
    if (el.id) values.set(el.id, el.type === "checkbox" ? el.checked : el.value);
  }
  return values;
}

/*
 * <b>There is no `markUnsaved` any more, and no Save, and no *unsaved*.</b> Every setting on a
 * layer's pages applies through its own control — the cache lifetime has Set, the symbology has
 * Store, sharing applies when it is chosen — so nothing here was ever unsaved in the sense a
 * marker means. The `unsaved` map above stays: what it does now is remember a half-typed cache
 * lifetime while the reader looks at Symbology and comes back, which is a convenience rather
 * than a promise.
 */

/**
 * Opens a layer's settings page — or flips between its pages if it is already open.
 *
 * <b>Flipping rather than rebuilding is why this is one function and not two.</b>
 * All six pages are in the document at once, so moving between them is a class on a
 * section. Rebuilding on every click of the left column would re-read the server and
 * throw away a figure typed on the page you just left, which is a worse fault than
 * the one it would be fixing.
 *
 * `pending` carries the values a background refresh must not lose — redrawLayerPage
 * has the reason.
 */
function showLayer(name, page, pending = null) {
  if (editing && editing.name === name && $("view-layer").classList.contains("on")) {
    editing.page = page;
    showEditPage(page);
    return;
  }

  const l = layerNamed(name);
  selected = name;
  editing = { name, page };

  const stopped = l.status === "stopped";
  const isShown = shown.has(name);
  const tilesShown = shown.has(tileKey(name));

  // The breadcrumb is a link, not a label: it is also how you get back, and their
  // reference reads the same way — Site (root) › folder › service.
  // <b>The trail back, and it matters more since a one-layer service opens this page
  // directly.</b> It used to read *Services › hosted › name*, where the middle word was
  // `hosted` or `registered` — a fact about the data, printed where the reader expects a place.
  // Now every step is where the layer is and every step is a link: the folder goes to the
  // folder, and the service goes to its layer list where there is one worth seeing.
  const at = placeOf(name);

  // <b>Back to the surface you are on, not always to Server's.</b> Both the breadcrumb's first step
  // and the Cancel link were hardcoded to `#/services`, so a Studio publisher editing a layer's
  // symbology and pressing *Cancel* was sent to Server — and one without `admin:manageServer` got the
  // refusal toast and a bounce to Studio's home. A plain *nevermind* should not cross the product.
  // Design review 2026-08-19.
  const home = surfaceOfPath() === "studio" ? "content" : "services";
  const homeLabel = home === "content" ? "My content" : "Services";

  $("editCancel").setAttribute("href", `#/${home}`);

  const trail = [`<a href="#/${home}">${homeLabel}</a>`];

  if (at) {
    // <b>The folder step is Server's, because only Server has a folder list.</b> In Studio it linked
    // to a screen that surface does not have, which for a publisher without `admin:manageServer` is a
    // refusal toast and a bounce. Studio's trail is *My content › the service › this layer*, which is
    // the path they actually came along.
    if (home === "services") {
      trail.push(`<a href="#/services${at.folder ? "/" + encodeURIComponent(at.folder) : ""}">${
        h(at.folder || "Site (root)")}</a>`);
    }

    // Only when the service holds something else. For a service of one layer this page *is*
    // the service, and a link to a single-row table is the step the owner asked us to drop.
    const siblings = known.filter(k => k.service === at.bare
      && (k.folder || null) === at.folder).length;

    if (siblings > 1 || home === "content") {
      trail.push(`<a href="#/service/${encodeURIComponent(at.service)}">${h(at.bare)}</a>`);
    }
  } else {
    trail.push(h(l.hosted ? "hosted" : "registered"));
  }

  $("editCrumb").innerHTML = `${trail.join(" › ")} › <b>${h(name)}</b>`;

  // <b>This surface's pages, and a way to the other's.</b> §5c promises that *each surface links
  // to the other's page for the same layer, so the split never means a dead end* — without the
  // second half a publisher looking for Sharing in Server finds five pages and no clue, which is
  // worse than the single screen this replaced.
  const here = surfaceOfPath();
  const elsewhere = here === "server" ? "studio" : "server";

  $("editNav").innerHTML = pagesOf(here).map(p =>
    `<a href="#/layer/${encodeURIComponent(name)}/${p}">${
      p[0].toUpperCase() + p.slice(1)}</a>`).join("")
    + (may(SURFACES[elsewhere].needs)
      ? `<a class="crossing" href="${surfaceHref(elsewhere,
          `layer/${encodeURIComponent(name)}/${pagesOf(elsewhere)[0]}`)}">${
          pagesOf(elsewhere).map(p => p[0].toUpperCase() + p.slice(1)).join(", ")}
          <span class="in">in ${h(SURFACES[elsewhere].title)}</span></a>`
      : "");

      // The server's own cache of this table's shape (D-17), not the publisher's business. It sat
      // under Maintenance beside Delete layer until the editor was split by surface, which is when
      // the two turned out to be different kinds of act.
  $("editPages").innerHTML = `
    <section class="page" id="page-general">
      <h4>State</h4>
      <div class="row">
        ${pill(l.status)}${pill(l.sharing)}${pill(l.hosted ? "hosted" : "registered")}
        <span style="flex:1"></span>
        <button data-toggle="${h(name)}" data-to="${stopped ? "start" : "stop"}"
          title="${stopped ? "Serve this service again"
            : "Stops the service this layer is in — status belongs to the service, so a "
              + "sibling layer stops with it"}">
          ${stopped ? "Start" : "Stop"}</button>
        <button data-show="${h(name)}" class="${isShown ? "on" : ""}" ${stopped ? "disabled" : ""}>
          ${isShown ? "Hide on map" : "Show on map"}</button>
        ${l.hosted
          ? `<button data-tiles="${h(name)}" class="${tilesShown ? "on" : ""}" ${stopped ? "disabled" : ""}>
               ${tilesShown ? "Hide tiles" : "Show tiles"}</button>`
          : ""}
        <button data-refresh="${h(name)}">Forget remembered shape</button>
      </div>

      <h4>Contents</h4>
      <div id="contents" class="val">reading the layer document…</div>

      <h4>Time</h4>
      <div class="setting"><span class="q">Which column is this layer's time:</span>
        <input type="text" id="timeField" placeholder="derive it from the schema"
          value="${h(l.timeField || "")}"></div>
      <p class="hint">Leave it empty and the server uses the layer's one date column, or
        publishes no time dimension when it has none or several. Name a column when the
        table has more than one date and only one of them is when the thing happened —
        <code>observed_at</code> rather than <code>created_at</code>.</p>
      <div class="row" style="margin-top:10px">
        <button data-time="${h(name)}">Set</button>
        <button data-time="${h(name)}" data-clear="1" class="ghost">Derive it</button>
      </div>

      <h4>Identity</h4>
      <dl class="facts">
        <dt>Source table</dt><dd>${h(l.table)}</dd>
        <dt>Data source</dt><dd>${h(l.dataSource || "")}</dd>
        <dt>Owner</dt><dd>${h(l.owner || "—")}</dd>
        <dt>Layer id</dt><dd>${h(l.id || "—")}</dd>
      </dl>
    </section>

    <section class="page" id="page-caching">
      <h4>Tile cache</h4>
      ${l.hosted ? `
      <div class="setting"><span class="q">How long a tile stays fresh:</span>
        <input type="number" id="ttl" min="0" step="1" placeholder="server default"><span class="u">seconds</span></div>
      <div class="row" style="margin-top:10px">
        <button data-cache="${h(name)}">Set</button>
        <button data-cache="${h(name)}" data-clear="1" class="ghost">Use the server's</button>
      </div>`
      : `<p class="hint">Tiles come only from hosted data, so this layer has no tile cache.</p>`}

    </section>

    <!--
      <b>The symbology editor, rebuilt to the owner's handoff (direction 1c) on 2026-09-04.</b>
      The complaint it answers is one sentence: *ya ben bu ekranı cidden anlayamıyorum. çok
      karmaşık.* What made it complex was not the number of controls — every one of them is still
      here — but that the page was a single column of unrelated sections, so a reader looking for
      one thing read past four others, and Store was below all of them.

      <b>Three columns, and each one answers a different question.</b> What kind of drawing is
      this (the rail), what does it look like (the picture), what is in it (the inspector). The
      document, the ArcGIS projection and the conversion's losses are the inspector's three tabs
      rather than three more sections under the fold, and the picture — a 336-pixel thumbnail
      beside a form until today — is now most of the screen.

      <b>Nothing was removed and no id was renamed.</b> Every control the form had is in one of
      the three columns, which is what makes this a rearrangement rather than a redesign of what
      the editor can do.
    -->
    <section class="page" id="page-symbology">
      <!--
        <b>One strip: where you are, what you are looking at, and the two things you can do to
        it.</b> These were three rows — a breadcrumb above the panel, a nav down its left side,
        and a row of buttons at the bottom of the form. Store being the last of the three is
        what produced the owner's *save ne, store ne?*: the button that keeps the work was the
        one furthest from it.

        <b>The tabs here are the service's, not the layer editor's.</b> Caching, Maintenance and
        Endpoints are a different subject — they are about the layer as a published thing, not
        about how it draws — and putting them beside Symbology said that choosing a colour and
        deleting the layer are two of a kind. The service's own tabs are what a reader arriving
        from a list was on a moment ago, so this is the strip they already know.
      -->
      <div class="symstrip">
        <div class="crumbs" id="symCrumb"></div>
        <!--
          <b>Outside the crumb, because the crumb is the thing that abbreviates.</b> The pill was
          appended to it, so on a long layer name the ellipsis ate the one fact a reader arriving
          from a link cannot get anywhere else: whether what they are about to restyle is public.
        -->
        <span id="symScope"></span>
        <!--
          <b>The solid variant, and the comment on that class already said so.</b> It was written for
          the item page's strip with the sentence *two shapes for one act is D-46's whole subject* and
          then applied to one of the two. Measured 2026-09-04 on the running console: this strip
          drew the current tab on --surface inside a container that is also --surface, so the
          only thing separating *the tab you are on* from the four you are not was a shadow at
          five per cent and a font weight. The same five labels, in two places, must look the
          same in both.
        -->
        <nav class="segmented solid tabs" id="symItemTabs"
          aria-label="This service's pages"></nav>
        <div class="symdo">
          <span class="symstate" id="symPreviewState">The stored appearance.</span>
          <button data-symbology-del="${h(name)}">Back to generated</button>
          <button class="primary" data-symbology-put="${h(name)}">Store</button>
        </div>
      </div>

      <!--
        <b>The server's refusal, under the strip and above everything else.</b> ADR-033: a stored
        absolute URL is a fact with an expiry date, so it is refused rather than stripped. The
        page below is unchanged — the document that was not stored is still the document on the
        screen, which is what makes the message actionable.
      -->
      <div class="symbanner" id="symRefusal" hidden></div>

      <!--
        <b>A style override on the service wins for the tile face (ADR-033 §5d)</b>, so a layer
        whose own document is stored and correct can still be drawn by something else on a map.
        It was a sentence appended to the state line, which is behind a tab now; a fact that
        contradicts the picture belongs where the picture is.
      -->
      <div class="symbanner note" id="symOverride" hidden></div>

      <p class="hint" id="symUnauthored" hidden></p>

      <div class="symcols" id="symCols">

        <!-- ---------------------------------------------------------- the renderer rail -->
        <div class="symrail" id="symForm">
          <!--
            <b>Which layer, in the column where everything else about appearance is chosen.</b>
            Handoff revision 2026-09-04. It was a segmented control in the title strip, which is
            where a *place* goes — and this is not a place, it is the first choice the editor
            asks. Each entry carries the geometry as a swatch and whether anybody has styled it,
            so choosing between three layers does not mean opening three of them.

            <b>And the service page's Symbology tab is gone with it.</b> A list whose every row
            was one *Edit* link was an indirection with nothing in it; the tab opens this editor
            directly now and this section is the list.
          -->
          <section id="symLayerSection" hidden>
            <h5>Layer</h5>
            <div id="symLayerPick" role="list"></div>
          </section>

          <section>
            <h5>Renderer</h5>

            <!--
              <b>Three cards, and it was a *select*.</b> This is the biggest decision on the page
              and the three answers look different from each other — one colour, a colour per
              value, a ramp — so they are shown rather than named behind a click. Radio inputs,
              so the arrow keys move between them and a screen reader is told it is one choice of
              three; the sentence each one used to carry is its *title* attribute, which is where the
              handoff puts a hint that no longer has room to be body text.
            -->
            <div class="symkinds" role="radiogroup" aria-label="How this layer is drawn">
              <label class="symkind" title="Every feature the same">
                <input type="radio" name="symKind" value="simple" checked>
                <span class="ramp" aria-hidden="true"><i style="background:#8d99a8"></i><i
                  style="background:#8d99a8"></i><i style="background:#8d99a8"></i></span>
                Single symbol</label>
              <label class="symkind" title="By the value of a field">
                <input type="radio" name="symKind" value="uniqueValue">
                <span class="ramp" aria-hidden="true"><i style="background:#c8452b"></i><i
                  style="background:#e08a2e"></i><i style="background:#d9b445"></i></span>
                Unique values</label>
              <label class="symkind" title="By ranges of a number">
                <input type="radio" name="symKind" value="classBreaks">
                <span class="ramp" aria-hidden="true"><i style="background:#d1e5e2"></i><i
                  style="background:#6fb1a8"></i><i style="background:#0d7d70"></i></span>
                Class breaks</label>
            </div>

            <div class="setting" id="symFieldRow" hidden>
              <label class="q" for="symField">Field</label>
              <select id="symField"></select></div>

            <!--
              <b>Two more, for the family that can use them - ADR-052 §3.17.</b> ArcGIS classifies
              by up to three fields at once and joins their values, so a class can be "land use
              within district". This form offered one, which was offering half the renderer. They
              appear only for the unique-value family, and only one at a time: the third is hidden
              until the second is chosen, so a reader is never looking at a control that cannot
              yet do anything.
            -->
            <div class="setting" id="symField2Row" hidden>
              <label class="q" for="symField2">and</label>
              <select id="symField2"></select></div>

            <div class="setting" id="symField3Row" hidden>
              <label class="q" for="symField3">and</label>
              <select id="symField3"></select></div>

            <!--
              <b>The step the editor was missing - ADR-052 §3.12.</b> A unique-value renderer is
              the list of a field's distinct values and a class-breaks renderer is a set of bounds
              computed from its distribution. This form knew how to draw a class and not how to
              find one: it made one class whose value was the empty string, or one bound of zero
              and an "Add a class" button that added one to it. The values were always a query
              away and nothing asked.
            -->
            <div class="setting" id="symClassifyRow" hidden>
              <label class="q" for="symMethod" id="symClassifyLabel">Into</label>
              <span class="symclassify">
                <input id="symClassCount" type="number" min="1" max="32" value="5"
                  title="How many classes" aria-label="How many classes">
                <select id="symMethod">
                  <option value="NaturalBreaks">natural breaks</option>
                  <option value="EqualInterval">equal intervals</option>
                  <option value="Quantile">equal counts</option>
                  <option value="GeometricalInterval">geometric intervals</option>
                  <option value="StandardDeviation">standard deviations</option>
                  <option value="DefinedInterval">a fixed interval</option>
                </select>
              </span>
              <button class="tiny primary" id="symClassify">Read the data</button>
            </div>

            <p class="hint" id="symClassifySays" hidden></p>
          </section>

          <!--
            <b>The second axis, ADR-052 §3.6.</b> A renderer says which feature gets which
            symbol; this says how one property of that symbol slides with a number. Half of
            what ArcGIS calls a style is a renderer plus one of these, and the renderer here
            has drawn them since ADR-041 without any way to ask for one.
          -->
          <!--
            <b>Closed, and it summarises itself on the right.</b> Handoff revision 2026-09-04:
            this and the symbol sets are the two blocks that made the column read as a wall of
            controls, and a reader who has not asked for either should not be paying for them.
            The summary is what a disclosure owes: *nothing*, or *its width, by length_m* — so
            the row answers the question without being opened.
          -->
          <section class="symfold">
            <button type="button" class="symfoldhead" id="symVaryHead"
              aria-expanded="false" aria-controls="symVaryBody">
              <span class="caret" aria-hidden="true">&#9656;</span>
              <span>Vary with a number</span>
              <span class="symfoldsays" id="symVarySays">nothing</span>
            </button>
            <div class="symfoldbody" id="symVaryBody" hidden>
            <div class="setting"><label class="q" for="symVaryWhat">Change</label>
              <select id="symVaryWhat">
                <option value="">nothing — the symbol is the same everywhere</option>
                <option value="colour">its colour</option>
                <option value="size">its width or size</option>
                <option value="opacity">how solid it is</option>
              </select></div>

            <div id="symVaryRows" hidden>
              <div class="setting">
                <label class="q" for="symVaryField">With</label>
                <select id="symVaryField"></select></div>

              <!--
                <b>A per-cent box beside each colour, because an *input type=color* has no alpha.</b>
                The element gives back *#rrggbb* and nothing else — it cannot express the fourth
                number a CIMRGBColor carries — so a form built only from colour boxes rebuilds
                every ramp fully opaque, which is what this one did until 2026-09-04.
              -->
              <div class="setting symvarystop"><span class="q">From</span>
                <input type="number" id="symVaryFrom" step="any">
                <input type="color" id="symVaryFromColour" title="The colour at the low end"
                  aria-label="The colour at the low end">
                <span class="pair" id="symVaryFromPer"><input type="number" id="symVaryFromAlpha"
                  min="0" max="100" step="0.1"
                  title="How opaque the low end is: 100 is solid, 0 is invisible"
                  aria-label="The opacity at the low end, per cent"><span class="u">%</span></span>
                <span class="pair" id="symVaryFromMeasure"><input type="number"
                  id="symVaryFromNumber" step="0.5" min="0"
                  aria-label="The value at the low end"><span class="u"
                  id="symVaryFromUnit">pt</span></span></div>

              <div class="setting symvarystop"><span class="q">To</span>
                <input type="number" id="symVaryTo" step="any">
                <input type="color" id="symVaryToColour" title="The colour at the high end"
                  aria-label="The colour at the high end">
                <span class="pair" id="symVaryToPer"><input type="number" id="symVaryToAlpha"
                  min="0" max="100" step="0.1"
                  title="How opaque the high end is: 100 is solid, 0 is invisible"
                  aria-label="The opacity at the high end, per cent"><span class="u">%</span></span>
                <span class="pair" id="symVaryToMeasure"><input type="number"
                  id="symVaryToNumber" step="0.5" min="0"
                  aria-label="The value at the high end"><span class="u"
                  id="symVaryToUnit">pt</span></span></div>

              <p class="hint" id="symVaryNote"></p>
            </div>
            </div>
          </section>

          <!--
            <b>ADR-052 §3.8, and it moved out of the class detail.</b> A shipped symbol is a
            starting point for the class you have selected, and it was behind the same click that
            opened that class's stack — so the gallery only existed while you were already
            editing a symbol, which is after the moment you would have wanted it.
          -->
          <section class="symfold" id="symSets">
            <button type="button" class="symfoldhead" id="symSetsHead"
              aria-expanded="false" aria-controls="symSetsBody">
              <span class="caret" aria-hidden="true">&#9656;</span>
              <span>Symbol sets</span>
              <span class="symfoldsays" id="symSetsSays"></span>
            </button>
            <div class="symfoldbody" id="symSetsBody" hidden>
              <p class="hint" id="symGalleryNote">For this geometry. Choosing one replaces the
                selected class's symbol; its colours are edited in the inspector.</p>
              <div id="symGallery"></div>
            </div>
          </section>

          <!--
            <b>The service's own style, three lines of prose at the bottom of the column.</b>
            Handoff revision 2026-09-04. It was a panel with a raw textarea standing open under a
            list of layers, which made an expert control — a MapLibre document for the *whole
            service* — outweigh the layer whose appearance the page is about. It is a footnote to
            everything above it, so it is written as one, and the document opens only when
            somebody asks for it.

            <b>Same ids, same endpoint.</b> The serviceStyle and styleDoc elements moved rather than
            being rebuilt; what stamps them with the service's name moved too — see
            drawSymStrip, which is the one place that knows which service this layer is in.
          -->
          <section class="symfold symoverride" id="serviceStyle">
            <b>Service style override</b>
            <p class="hint" id="styleState"><b>Not fetched yet.</b></p>
            <p class="hint">The tile face composes a style from every layer's symbology, in layer
              order. Storing one here replaces that composition for the whole service, which is
              how layers are reordered or filtered against each other. The ArcGIS feature face is
              not affected.</p>
            <button type="button" class="tiny ghost" id="symOverrideHead"
              aria-expanded="false" aria-controls="symOverrideBody">Write one&hellip;</button>
            <div class="symfoldbody" id="symOverrideBody" hidden>
              <div class="row">
                <button data-style="">Fetch current</button>
                <button data-style-del="" class="ghost">Back to the composition</button>
                <button class="primary" data-style-put="">Store the override</button>
              </div>
              <textarea id="styleDoc" rows="8" spellcheck="false"
                placeholder="A MapLibre style document. Fetch it first — an empty box means none is stored, and the composition is being served."></textarea>
            </div>
          </section>
        </div>


          <!--
            <b>A generated appearance is an answer, so the columns say so instead of opening on
            a form.</b> §5b makes it a real state with a version of 0. Somebody who has never
            styled this layer was previously shown a full editor already filled in with a
            document they did not write, and no way to tell that from one they had.

            <b>It replaces the picture and the inspector, and not the rail — a departure from
            the prototype, made because the handoff's revision moved the layer list into the
            rail.</b> Covering all three columns would hide the way to the service's other
            layers behind a sentence about this one: on a three-layer service whose first layer
            is unstyled, the other two would be unreachable without dismissing a screen that is
            not about them. What the empty screen exists to withhold is the *claim about this
            layer's appearance*, which is the picture and the inspector. Which layer you are
            editing, and what the service's own style is, are not that claim.
          -->
          <div class="symempty" id="symEmpty" hidden>
            <div>
              <span class="sw" id="symEmptySwatch"></span>
              <b>This layer draws generated</b>
              <p>Nobody has styled it. The colour is deterministic from the layer's identity, so
                it is the same tomorrow and on another deployment, and both faces report it as
                <span class="mono">version 0</span>.</p>
              <div class="row">
                <button class="primary" id="symStartGenerated">Start from the generated look</button>
                <button id="symPasteDoc">Paste a document</button>
              </div>
            </div>
          </div>


        <!-- --------------------------------------------------------------- the picture -->
        <!--
          <b>The picture is the column now, not a thumbnail in it.</b> It is what somebody
          choosing a colour is actually choosing, and at 336 pixels wide beside a form it was
          smaller than the swatch grid under it.
        -->
        <!--
          <b>A map, not a picture of one.</b> Owner 2026-09-04, pointing at two ArcGIS Online Map
          Viewer videos: the map should open the way theirs does, and what the symbology controls
          change should show on it. A still frame could never answer *what does this look like at
          z14 over Ankara*, which is most of what somebody choosing an appearance wants to know.

          <b>Drawn by this server, which is what keeps ADR-051.</b> That decision refused a
          browser-drawn preview because it would be a picture of the browser's reading of the
          style rather than of the renderer that serves the layer. This is the same renderer, the
          same record ceiling and the same candidate document, asked for the viewport's extent
          instead of a fixed one — measured before it was built at 78 ms for 256 classes and
          34-58 ms for everything else. See experiments/symbology-on-the-map.

          <b>Two elements, two jobs.</b> OpenLayers pans and zooms the ground; the image carries
          what the server drew. Neither pretends to do the other's job, which is the line ADR-051
          drew.
        -->
        <div class="sympreview ground-light" id="symPreviewBox">
          <div id="symMap"></div>
          <img id="symPreview" alt="" hidden>
          <div class="thumb empty" id="symPreviewNone"
            title="Draw something and this shows what it looks like."></div>

          <div class="symcap" id="symPreviewCap">rendered by this server</div>

          <!--
            <b>Real, because the picture is transparent.</b> ThumbnailEndpoints.RenderAsync
            clears to Rgba.Transparent, so what is behind the image is a decision this page can
            make on its own: a pale fill is invisible on white and legible on dark, and until now
            there was no way to find that out except by storing it and opening a map. No request
            is made — the chips change a class on the frame.

            <b>There is no zoom control here and the handoff drew one.</b> The preview is one PNG
            at the layer's own drawn extent; a plus and a minus that could not change it would be
            two controls for a feature that does not exist, which is the fault ADR-034 names.
          -->
          <nav class="segmented symground" id="symGround" aria-label="What is drawn under the layer">
            <a href="#" data-ground="light" aria-current="page">Light</a>
            <a href="#" data-ground="dark">Dark</a>
            <a href="#" data-ground="none">None</a>
          </nav>

          <!--
            <b>The legend is the class list, drawn as the map's reader would meet it.</b> The
            inspector's list is for editing and this one is for reading: no boxes, no remove
            button, and it says what the picture is showing rather than what can be changed
            about it.
          -->
          <div class="symlegend" id="symLegend" hidden></div>
        </div>

        <!-- ------------------------------------------------------------- the inspector -->
        <div class="syminsp">
          <!--
            <b>The losses get a badge, because they were a block below the fold.</b> ADR-033
            accepted a lossy conversion and the mitigation is that it says so — a count on the
            tab is that sentence in the one place a reader cannot scroll past.
          -->
          <nav class="insptabs" id="symInspTabs">
            <a href="#" data-insp="classes" aria-current="page" id="symTabClasses">Classes</a>
            <a href="#" data-insp="document">Document</a>
            <a href="#" data-insp="arcgis">ArcGIS <span class="badge" id="symLossBadge" hidden>0</span></a>
          </nav>

          <div class="insppane" id="insp-classes">
            <div class="pad">
              <!--
                <b>A filter and a fixed height, because a classification can have 256 classes.</b>
                Two hundred and fifty-six rows down one page is not a list anybody reads; it is a
                page anybody scrolls past. Map Viewer's own categories panel is a bounded,
                scrolling list with a search over it, and for the same reason: past a couple of
                dozen classes the way to reach one is to name it, not to hunt for it.
              -->
              <div class="setting" id="symFilterRow" hidden>
                <input id="symFilter" type="search" placeholder="Find a value or a label"
                  autocomplete="off" spellcheck="false" aria-label="Find a value or a label">
                <!--
                  <b>Named symShowing, not symClassCount.</b> It was the latter for an afternoon,
                  which is also the id of the number box in the Classify row -- getElementById
                  returns the first, so every "12 of 256" this code wrote went into an input's
                  textContent, where nothing renders it. <b>The count was never once visible.</b>
                -->
                <span class="rowmeta" id="symShowing"></span>
              </div>

              <!--
                <b>The controls that act on every class, because most of the work is every
                class.</b> Nobody hand-edits eighty-one provinces one at a time; they take the
                machine's split and adjust the whole of it, then tune a handful. Every control on
                this page before D-217 acted on exactly one class, which is why the owner set an
                opacity and reported that opacity does nothing. ADR-052 §3.20.
              -->
              <div class="setting" id="symAllRow" hidden>
                <label class="q" for="symAllAlpha">All classes</label>
                <span class="pair"><input type="number" id="symAllAlpha" min="0" max="100" step="0.1"
                  placeholder="opacity"
                  title="Set every class's opacity to this, replacing whatever each one has"
                  aria-label="Opacity for every class, per cent"><span class="u">%</span></span>
                <button class="tiny" id="symAllAlphaApply">Set</button>
                <span class="rowmeta" id="symAllSays"></span>
              </div>

              <div id="symClasses"></div>

              <div class="row" id="symClassActions" hidden>
                <button class="tiny" id="symAddClass">Add a class</button>
              </div>
            </div>

            <!--
              <b>The stack sits under the list rather than replacing it.</b> D-217 made them two
              views because a permanently rendered editor could be titled after a row scrolled out
              of sight — a panel whose subject nobody can see is a panel people misread. The
              handoff's answer to the same fault is adjacency: one 336-pixel column, the selected
              row marked and scrolled into view whenever it moves, and the symbol directly under
              it. That keeps what D-217 was protecting and costs no click to see what a class is
              made of, which is the half D-217 paid for it.
            -->
            <div class="symstack" id="symDetail">
              <div class="symstackhead" id="symStackHead">
                <b id="symDetailWhich">Symbol</b>
                <span id="symStackNote">top first</span>
              </div>
              <div id="symStack"></div>
              <div class="row" id="symStackActions">
                <button class="tiny" data-add-layer="CIMSolidFill">+ Fill</button>
                <button class="tiny" data-add-layer="CIMSolidStroke">+ Stroke</button>
                <button class="tiny" data-add-layer="CIMVectorMarker">+ Marker</button>
              </div>
            </div>
          </div>

          <!--
            <b>The document is a tab, and it was behind a disclosure triangle.</b> It is the one
            thing Store sends and everything above writes into it, so a disclosure element said the
            opposite of what is true about it. Somebody who needs something the controls cannot
            express — a MapLibre expression, a filter, a second layer in the style — edits it
            here and the controls stop claiming to describe it.
          -->
          <div class="insppane" id="insp-document" hidden>
            <div class="docpane">
              <div class="dochead"><span class="tag">CIM</span>
                <span id="symState">Reading…</span></div>
              <textarea id="symDoc" spellcheck="false"
                placeholder="A MapLibre style, or an Esri drawingInfo pasted straight from ArcGIS. Both are accepted; a drawingInfo is converted on the way in and you are told what the conversion cost."></textarea>
              <div class="docfoot"><span>Paste a MapLibre style or an Esri
                <span class="mono">drawingInfo</span> here as well — both are converted on the way
                in, and the conversion's cost is reported under ArcGIS.</span>
                <button class="tiny" data-symbology="${h(name)}">Fetch current</button></div>
            </div>
          </div>

          <div class="insppane" id="insp-arcgis" hidden>
            <div class="pad">
              <b>What an ArcGIS client receives</b>
              <p class="hint">Derived from the document, in the three renderer families a client
                understands — <span class="mono">simple</span>,
                <span class="mono">uniqueValue</span>, <span class="mono">classBreaks</span>.
                Read-only: a projection, not a second place to edit.</p>

              <div id="symLoss" hidden>
                <b>What the ArcGIS face cannot carry</b>
                <ul class="losses" id="symLossList"></ul>
              </div>

              <div class="swatches" id="symSwatches" hidden></div>
            </div>
            <pre class="doc" id="symDerived">—</pre>
          </div>
        </div>
      </div>
    </section>

    <section class="page" id="page-maintenance">
      <p class="hint"><b>Who may read this is set on the service</b>, not here — one scope covers
        every layer the service holds, because <code>service.sharing</code> is the column the serving
        path reads. <a href="#/service/${
          [l.folder, l.service].filter(Boolean).map(encodeURIComponent).join("/")}"
        data-open-service-page="sharing">Open its Sharing page</a>. This page offered the same scope
        once per layer until 2026-08-18, which made one setting look like several — D-61.</p>

      <h4>Unpublish</h4>
      <div class="row">
        <button class="danger" data-delete="${h(name)}">Delete layer</button>
      </div>
      <p class="hint">The source table is not touched. For a hosted layer the data is in this
        server's datastore and goes with it; for a registered one it stays where it was.</p>
    </section>

    <section class="page" id="page-endpoints">
      <h4>Addresses</h4>
      <dl class="facts" id="endpoints"><dt>—</dt><dd>resolving…</dd></dl>
    </section>`;

  // <b>After the pages are written, because the strip is one of them.</b> The first version
  // filled it beside the breadcrumb above — thirty lines before `#editPages` exists — so every
  // element it looks for was null and it returned without a word. The screen was on the owner's
  // machine with an empty title bar for exactly as long as it took to look at it.
  drawSymStrip(name, at, trail);

  showEditPage(page);
  describeContents(name, l);

  // A background refresh passes its own snapshot; otherwise anything left unsaved
  // from earlier in this session is the snapshot.
  const restore = pending ?? unsaved.get(name) ?? null;


  if (restore) {
    // Put back what was on screen, and do not re-read: the server's answer would
    // overwrite exactly the edits this exists to keep.
    for (const [id, value] of restore) {
      const el = $(id);
      if (!el) continue;
      if (el.type === "checkbox") el.checked = value;
      else el.value = value;
    }

    // Said out loud, because a form showing figures the server does not have is
    // only safe if the reader knows that is what they are looking at.
    if (!pending) {
      toast(`${name}: showing what you had typed and not saved. Save applies it; `
        + `Cancel throws it away.`, true);
    }
  } else {
    const ttl = $("ttl");
    if (ttl && l.cacheSeconds != null) ttl.value = l.cacheSeconds;
  }

  for (const row of document.querySelectorAll("tr.sel")) row.classList.remove("sel");
  const row = document.querySelector(`tr[data-pick="${CSS.escape(name)}"]`);
  if (row) row.classList.add("sel");

  showView("view-layer", "services");
  window.scrollTo(0, 0);
}

/**
 * Lists a service's group layers, from the document any client would read.
 *
 * <b>No new endpoint, and that is ADR-020 §2 rather than laziness.</b> The service
 * document already carries the tree — a group is a layer of type `Group Layer` with its
 * children in `subLayerIds` — so asking for it here is the same request the map makes.
 * A second admin route returning the same facts would be a second place for them to
 * disagree.
 */
async function showServiceGroups(qualified) {
  const box = $("gExisting");
  if (!box) return;

  const service = (qualified || "").trim();
  if (!service) { box.innerHTML = ""; return; }

  box.textContent = "reading the service…";

  try {
    const doc = await api(`/rest/services/${service.split("/").map(encodeURIComponent).join("/")}`
      + `/FeatureServer?f=json`);

    const groups = (doc.layers || []).filter(l => l.type === "Group Layer");

    box.innerHTML = groups.length === 0
      ? `<span>No group layers in ${h(service)} yet.</span>`
      : `<div>Groups in ${h(service)}:</div>` + groups.map(g => {
        const children = (g.subLayerIds || []).length;
        return `<div class="row" style="margin:6px 0 0;align-items:center">
          <span class="pill p-registered">${g.id}</span>
          <span style="font-family:var(--sans);font-size:13.5px">${h(g.name)}</span>
          <span>${children
            ? `${children} child${children === 1 ? "" : "ren"}`
            : "empty"}</span>
          <button class="tiny danger" data-group-delete="${h(service)}#${g.id}"
            data-group-name="${h(g.name)}"
            ${children ? `disabled title="Move its ${children} child${
              children === 1 ? "" : "ren"} out first — they are not reparented for you,
              because that would move them in every saved map."` : ""}>Delete</button>
        </div>`;
      }).join("");
  } catch (e) {
    // A stopped service answers 503 here, which is expected rather than a fault, so the
    // reason is shown in place rather than as an error.
    box.innerHTML = `<span style="color:var(--stop)">${h(e.message || String(e))}</span>`;
  }
}

/** Shows one settings page, and marks it in the left column. */
function showEditPage(page) {
  // <b>Symbology is the one page in this editor that is a screen rather than a form.</b> Three
  // columns that fill the window cannot live inside a 196-pixel nav and a page of padding, so
  // the shell steps aside for it and steps back for every other page.
  $("app").classList.toggle("symfull", page === "symbology");

  for (const link of document.querySelectorAll("#editNav a")) {
    if (link.getAttribute("href").endsWith(`/${page}`)) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }

  for (const s of document.querySelectorAll("#editPages .page")) {
    s.classList.toggle("on", s.id === `page-${page}`);
  }

  // <b>Symbology reads itself, unlike the service style beside it.</b> The style page has
  // a *Fetch current* button because a style can be a megabyte and is usually absent; a
  // layer's symbology is one symbol and its whole value is knowing what is there now —
  // `#symState` promises *Reading…*, and a line that says it is working on something it
  // has stopped working on is the small lie D-46 keeps catching.
  if (page === "symbology" && editing) {
    section("the symbology", () => loadSymbology(editing.name), "symState");
  }
}

/**
 * Reads what the service is configured to offer, into the pages.
 *
 * <b>Read, never assumed.</b> The old cache box started empty and explained in a
 * paragraph that it could not show the current value. A control that displays a
 * figure it did not read is a control that lies the moment somebody changes it in
 * another window.
 */
async function loadServiceCapabilities(name, folderGiven) {
  // <b>It takes a service now, and used to take a layer.</b> The old signature resolved a layer to
  // its service in order to call a service-scoped endpoint, which is exactly the confusion D-61 is
  // about. `folderGiven` is undefined only on the legacy path, which no longer has a caller.
  const service = name;
  const folder = folderGiven ?? null;

  if (!service) return;
  const c = await api(`/admin/services/${encodeURIComponent(service)}/capabilities`
    + `?folder=${encodeURIComponent(folder || "")}`) || {};

  const set = (id, v) => { const el = $(id); if (el && v != null) el.value = v; };

  /*
    <b>Which half of this page applies is decided here, because here is where the
    service's kind is known.</b> The markup renders both and hides neither on its own;
    doing it the other way round would mean the page's shape depended on a list fetched
    somewhere else, and the two would drift.

    An image service reports its own facts out of the ArcGIS document rather than the
    admin API, because that document is where they live — and reading it here is the
    same request an ArcGIS client makes, so a difference between what the console shows
    and what a client sees is not possible.
  */
  /*
    <b>Who may read this service, in the head bar, on every one of its pages.</b>
    [D-141](../../docs/architecture-debt.md): sharing was a pill on the services list and
    nothing at all here, so an operator who arrived from a bookmark or a shared link read a
    whole settings page with no sign of whether the thing was private. *You can see it on the
    other screen* is not an answer when the other screen is not the one they are on.

    <b>In the head bar rather than on the Capabilities page.</b> A service has four pages and
    the fact is true on all of them; putting it on one would have fixed a quarter of the
    problem and made the other three look deliberate.

    <b>From the server's answer, not from the list this reader may never have seen.</b> The
    same rule the `kind` lookup two hundred lines down had to learn the hard way.
  */
  const scope = $("serviceScope");

  if (scope) {
    scope.hidden = !c.sharing;

    if (c.sharing) {
      scope.className = "pill p-" + c.sharing;
      scope.textContent = c.sharing;
    }
  }

  const raster = $("coverageSettings");
  const features = $("featureSettings");

  if (raster && c.kind === "ImageServer") {
    raster.hidden = false;
    if (features) features.hidden = true;

    // <b>Limits is a feature service's page and it 404s for a coverage.</b> A tab that
    // leads to a refusal is worse than one that is not offered: the operator cannot
    // tell a missing screen from a broken one. Record counts, edit ceilings and
    // response-byte bounds are all about rows, and a coverage has none.
    for (const tab of document.querySelectorAll("#serviceNav a[data-service-page=limits]")) {
      tab.hidden = true;
    }

    // <b>And no Save, because there is nothing on this page to save.</b> This panel's
    // own note two hundred lines up says a button that does nothing contradicts the
    // sentence above it — written for the Sharing page, true here for the same reason:
    // everything shown is read from the coverage and none of it is editable.
    for (const save of document.querySelectorAll("#servicePagesBody [data-service-save]")) {
      save.closest("div")?.setAttribute("hidden", "hidden");
    }

    const qualified = folder ? `${folder}/${service}` : service;
    const view = $("coverageView");

    if (view) {
      view.href = `/studio/map.html?face=imageserver&service=${encodeURIComponent(qualified)}`;
    }

    const doc = await api(`/rest/services/${qualified}/ImageServer?f=json`) || {};
    /*
      <b>What a pixel is measured in, which the service document does not say.</b> It
      gives a reference code and two numbers, and the numbers are in whatever that
      reference counts in. Naming it from the code is the only way to label the row.

      <b>Geographic references are the ones worth naming</b>, and this only knows the two
      that matter here: 4326 and its equivalents are degrees, everything else this server
      serves is projected and counts in metres. A reference that is neither — feet, or a
      geographic code not in the list — gets `units` rather than a wrong name, because a
      wrong unit is what this row exists to stop.
    */
    const GEOGRAPHIC = [4326, 4269, 4267, 4258];

    function groundUnit(document) {
      const wkid = document?.spatialReference?.wkid;

      if (GEOGRAPHIC.includes(wkid)) {
        return "degrees";
      }

      // 2000-32760 is the bulk of the projected EPSG range, and every projected system
      // this server can serve from PostGIS within it is metric. Outside it, say nothing
      // rather than guess.
      return wkid >= 2000 && wkid <= 32760 ? "m" : "units";
    }

    const facts = $("coverageFacts");

    if (facts) {
      const rows = [
        ["Size", doc.extent ? `${Math.round((doc.extent.xmax - doc.extent.xmin) / (doc.pixelSizeX || 1))} × ${Math.round((doc.extent.ymax - doc.extent.ymin) / (doc.pixelSizeY || 1))} pixels` : null],
        ["Bands", doc.bandCount],
        ["Pixel type", doc.pixelType],
        ["Reference", doc.spatialReference ? `EPSG:${doc.spatialReference.wkid}` : null],
        // <b>Rounded, because the number is a division and the division shows.</b>
        // The pixel size is the extent over the pixel count, so it arrives as
        // 0.010000000000000009 and printing that says *this measurement is precise to
        // seventeen digits*, which it is not. Six significant figures is more than any
        // raster's georeference carries.
        //
        // <b>And with its unit, because without one the number is unreadable.</b> This
        // showed `0.01 × 0.01` for a coverage in EPSG:4326 and `100 × 100` for one in
        // EPSG:3857 — degrees beside metres, four orders of magnitude apart on the
        // ground, rendered as though they were comparable. An operator reading the two
        // rows would conclude the second raster was ten thousand times coarser, when it
        // is roughly a hundred times finer. The note two hundred lines above this one
        // records a real incident in this codebase from exactly that conflation.
        ["Pixel size", doc.pixelSizeX
          ? `${Number(doc.pixelSizeX.toPrecision(6))} × `
            + `${Number(doc.pixelSizeY.toPrecision(6))} ${groundUnit(doc)}`
          : null],
        ["No data", doc.noDataValue ?? "none declared"],
        ["Formats", doc.supportedImageFormatTypes],
        // <b>The scheme, and the sentence saying it is not a cache.</b> An operator who
        // reads *tiled* on this page will ask where the tiles are kept, and the answer is
        // nowhere: each one is drawn when it is asked for, out of the same file
        // exportImage reads. Saying so here costs a line and saves that question.
        ["Tiles", doc.tileInfo
          ? `${doc.tileInfo.rows} px, ${doc.tileInfo.lods.length} levels, `
            + `EPSG:${doc.tileInfo.spatialReference.wkid}, drawn on request`
          : null],
      ].filter(([, v]) => v !== null && v !== undefined && v !== "");

      facts.innerHTML = rows.map(([k, v]) =>
        `<dt>${h(k)}</dt><dd>${h(String(v))}</dd>`).join("");
    }

    return;
  }

  if (raster) raster.hidden = true;
  if (features) features.hidden = false;

  if ($("capFeatures")) $("capFeatures").checked = c.servesFeatures !== false;
  if ($("capTiles") && !$("capTiles").disabled) $("capTiles").checked = c.servesTiles !== false;

  // An unset ceiling is every operation the caller's privileges allow, so the boxes
  // start ticked and unticking one is the narrowing.
  const allowed = c.capabilities ?? null;
  for (const box of document.querySelectorAll("#ops input[data-op]")) {
    box.checked = allowed === null || allowed.includes(box.dataset.op);
  }

  set("capMaxRows", c.maxRecordCount);
  set("capDefRows", c.defaultRecordCount);
  set("capOutBytes", c.maxResponseBytes);
  set("capInBytes", c.maxRequestBytes);
  set("capEdits", c.maxEditsPerTransaction);
  set("capTimeout", c.statementTimeoutMs);

  // <b>Read from the catalogue listing rather than from `/capabilities`.</b> Sharing is not a
  // capability — ADR-031 §2b keeps them apart deliberately, because a scope answers *who may read*
  // and a capability answers *what may be done* — so it is not in that document and must not be
  // added to it.
  if ($("capSharing")) {
    const listing = await api("/admin/featureservices") || {};
    const row = (listing.services || []).find(x =>
      (x.name || "").toLowerCase() === service.toLowerCase()
      && ((x.folder || "") || "").toLowerCase() === ((folder || "") || "").toLowerCase());

    // <b>The group has no `value`, so the member is the thing that is set.</b> `#capSharing`
    // was a select and is a radiogroup; assigning to its `value` would silently do nothing,
    // which is the shape of dead control this console keeps meeting.
    if (row?.sharing) {
      const one = $("capSharing").querySelector(
        `input[name="capSharing"][value="${CSS.escape(row.sharing)}"]`);

      if (one) one.checked = true;
    }
  }
  set("capDeadline", c.requestDeadlineSeconds);

  // <b>The placeholder is read, not assumed.</b> 600 is this build's default and a deployment may
  // have chosen otherwise, so the empty box shows what would actually apply to this request.
  const box = $("capDeadline");
  if (box) {
    box.placeholder = c.serverRequestDeadlineSeconds != null
      ? String(c.serverRequestDeadlineSeconds)
      : "no bound";
  }
}

/**
 * Saves a service's capabilities and limits, which is what one Save means here.
 *
 * <b>One service, one settings object.</b> Owner 2026-08-17, with ArcGIS Server Manager beside it:
 * *"bir service te n tane katman olabilir. ama servis ayarları tek."*
 */
async function saveServiceSettings(service, folder) {
  // <b>Named after what it saves, which it was not.</b> It was `saveEditing`, hung off the layer
  // editor's Save button, and it wrote the *service's* capabilities and limits using the layer's page
  // to find them — so pressing Save on a layer whose boxes had never been filled wrote an empty
  // ceiling onto its service, which is how `look_EarlyAlert` came to answer 500 (D-61).
  //
  // <b>Now it takes the service it saves.</b> One argument instead of a lookup, and no caller can
  // reach it from a layer.
  if (!service) return;

  const num = id => {
    const raw = ($(id)?.value ?? "").trim();
    return raw === "" ? null : Number(raw);
  };

  const ops = [...document.querySelectorAll("#ops input[data-op]")];
  const ticked = ops.filter(b => b.checked).map(b => b.dataset.op);

  const saved = await api(`/admin/services/${encodeURIComponent(service)}/capabilities`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      folder,
      servesFeatures: $("capFeatures").checked ? null : false,
      servesTiles: $("capTiles").disabled ? null : ($("capTiles").checked ? null : false),

      // All ticked means unset — the caller's privileges decide — rather than a
      // ceiling that happens to list everything. The two behave alike today and
      // diverge the moment a capability is added.
      capabilities: ticked.length === ops.length ? null : ticked,

      maxRecordCount: num("capMaxRows"),
      defaultRecordCount: num("capDefRows"),
      maxResponseBytes: num("capOutBytes"),
      maxRequestBytes: num("capInBytes"),
      maxEditsPerTransaction: num("capEdits"),
      statementTimeoutMilliseconds: num("capTimeout"),
      requestDeadlineSeconds: num("capDeadline"),
    }),
  });

  toast(saved.note ? `${service}: saved. ${saved.note}` : `${service}: saved`, true);
}

/**
 * Fills the Contents group from the layer's own service document.
 *
 * <b>Not a new admin capability</b> — ADR-020 §2. This is the same document the
 * map already fetches to choose a symbol, and the same one any ArcGIS client
 * reads. What the console was missing is that "what is in this layer" — its
 * fields, its geometry, its extent — had no answer anywhere in the UI, and it is
 * the first thing anybody asks about a layer they did not publish themselves.
 *
 * Loaded after the page paints rather than before, so opening a layer is never
 * held up by a request; and a refusal is shown in place rather than as a toast,
 * because a stopped service refusing this is expected and not an error.
 */
async function describeContents(name, layer) {
  const box = $("contents");
  if (!box) return;

  const place = placeOf(name);
  if (editing?.name === name) fillEndpoints(name, layer, place);

  try {
    const doc = await api(`${layerUrl(name).replace(location.origin, "")}?f=json`);
    if (editing?.name !== name) return;   // the reader moved on while this was in flight

    const fields = (doc.fields || []);
    const geometry = (doc.geometryType || "").replace("esriGeometry", "") || "none";
    const box2 = doc.extent;

    box.innerHTML = `
      <dl class="facts">
        <dt>Geometry</dt><dd>${h(geometry)}</dd>
        <dt>Reference</dt><dd>${h(doc.extent?.spatialReference?.wkid ?? "—")}</dd>
        <dt>Fields</dt><dd>${num(fields.length)}</dd>
        <dt>Extent</dt><dd>${box2
          ? `${Number(box2.xmin).toFixed(1)}, ${Number(box2.ymin).toFixed(1)} →
             ${Number(box2.xmax).toFixed(1)}, ${Number(box2.ymax).toFixed(1)}`
          : "unknown — run ANALYZE on the source table"}</dd>
      </dl>
      ${fields.length ? `<div style="margin-top:8px">${fields.map(f =>
        `<span class="pill p-private" style="margin:0 4px 4px 0">${h(f.name)}
           <span style="color:var(--faint);font-weight:400">${
             h((f.type || "").replace("esriFieldType", ""))}</span></span>`).join("")}</div>` : ""}`;
  } catch (e) {
    if (editing?.name !== name) return;
    box.innerHTML = `<span style="color:var(--stop)">${h(e.message || String(e))}</span>`;
  }
}

/**
 * The layer's endpoint links, once its real address is known.
 *
 * <b>Tiles are per service, not per layer, and saying so matters.</b> A member of
 * a multi-layer service shares one VectorTileServer with its siblings, so the tile
 * link for `look_EarlyAlert_routes` serves the whole of `look_EarlyAlert`.
 * Labelling it as this layer's tiles would be a quiet lie.
 */
function fillEndpoints(name, layer, place) {
  const box = $("endpoints");
  if (!box) return;

  if (!place) {
    box.innerHTML = `<dt>None</dt><dd>Not in the services directory. A stopped layer is
      absent from it, which is expected; otherwise the catalogue and the directory
      disagree and that is worth looking into.</dd>`;
    return;
  }

  const service = `${location.origin}/rest/services/${place.service}`;
  const shared = !name.endsWith(place.service.split("/").pop());

  box.innerHTML = `
    <dt>Feature</dt><dd><a href="${h(service)}/FeatureServer/${place.id}?f=json"
      target="_blank" rel="noreferrer">FeatureServer/${place.id}</a></dd>
    <dt>Service</dt><dd><a href="${h(service)}/FeatureServer?f=json"
      target="_blank" rel="noreferrer">${h(place.service)}</a></dd>
    ${layer.hosted ? `<dt>Tiles</dt><dd><a href="${h(service)}/VectorTileServer?f=json"
      target="_blank" rel="noreferrer">VectorTileServer</a>${shared
        ? ` <span class="val">— the whole service, not this layer alone</span>` : ""}</dd>` : ""}
    <dt>Directory</dt><dd><a href="${h(service)}" target="_blank" rel="noreferrer">browse</a></dd>`;
}

// -------------------------------------------------------------------- sources

/**
 * The registered sources, what each points at, and what can be done to one.
 *
 * <b>The connection replaced the id in the second column, and that is the point of the row.</b>
 * The id is a number an operator never types — every action here is a button — while *which database
 * is this* was on no screen at all. The owner found that by trying to correct one: *"registered db
 * path'ini güncelleyemiyorum sanırım… path derken connection string."* Two `kurum-postgis` entries,
 * one on the old host, are indistinguishable without it.
 *
 * <b>Host, port and database; never the credential.</b> The server sends `summary`, which is
 * `Summarise` over the decrypted string — the same shape the audit log records for the same reason.
 */
/*
  ---------------------------------------------------------------- publish a service

  <b>ADR-057.</b> The composition is the service: the tree's order is the layer order, index 0
  is drawn on top, and pressing Publish sends the whole thing to `POST /admin/publish`, which
  writes it in one transaction or not at all. There is no empty container to make first —
  §5h, by owner decision — so there is no sequence to remember and no layer index to type.

  <b>What is not here, and it is not an oversight.</b> The study drew a map between the two
  trees; nothing on this server turns an unpublished composition into a picture, so drawing a
  preview would be a control for a feature that does not exist. Symbology is the same: a
  layer's appearance is edited on the Symbology screen once it exists, and offering a swatch
  here that writes nowhere would be worse than the link that is here instead.
*/

/** The composition being assembled: groups and layers, in draw order. */
let pubTree = [];

/** Which nodes are selected, by id, for grouping. */
let pubPicked = new Set();

/** The registered databases and what they hold, once probed. */
let pubDatabases = [];

let pubSeq = 0;

/** Every layer in the composition, groups flattened, in draw order. */
function pubLayers() {
  const out = [];

  for (const node of pubTree) {
    if (node.kind === "group") { out.push(...node.children); } else { out.push(node); }
  }

  return out;
}

/** A node, the list holding it, and where in that list it sits. */
function pubFind(id) {
  for (let i = 0; i < pubTree.length; i++) {
    if (pubTree[i].id === id) return { node: pubTree[i], list: pubTree, at: i };

    if (pubTree[i].kind === "group") {
      const kids = pubTree[i].children;

      for (let k = 0; k < kids.length; k++) {
        if (kids[k].id === id) return { node: kids[k], list: kids, at: k, group: pubTree[i] };
      }
    }
  }

  return null;
}

function pubDetach(id) {
  const found = pubFind(id);

  if (!found) return null;

  found.list.splice(found.at, 1);
  return found.node;
}

/** A group with nothing in it is not a group. */
function pubTidy() {
  pubTree = pubTree.filter(n => n.kind !== "group" || n.children.length > 0);
}

/**
 * Reads the registered databases and what each holds.
 *
 * <b>Probed one at a time and drawn as they arrive.</b> A capability read opens a connection to
 * somebody else's database; doing four at once to fill a tree nobody has expanded yet is a cost
 * this screen has no reason to pay, and a source that is unreachable says so in its own row
 * rather than failing the screen.
 */
async function loadPublish() {
  const { dataSources = [] } = await api("/admin/datasources") || {};

  pubDatabases = dataSources.map(d => ({
    id: d.id,
    name: d.name,
    summary: d.summary,
    open: false,
    schemas: null,
    why: null,
  }));

  $("pubDbSays").textContent = `${num(pubDatabases.length)} registered`;

  pubDraw();
}

/** Opens one database and reads what can be published from it. */
async function pubProbe(db) {
  if (db.schemas || db.reading) return;

  db.reading = true;
  pubDraw();

  try {
    const answer = await api(`/admin/datasources/${encodeURIComponent(db.id)}/capability`) || {};

    // <b>Already served is greyed rather than hidden.</b> `layer_table_unique` is global — one
    // table is one layer on this server — so a table in use cannot be dragged, and a reader
    // looking for it needs to find it and see why rather than wonder where it went. ADR-057 §5i.
    const taken = new Set(
      (await api("/admin/layers") || {}).layers?.map(l => (l.table || "").toLowerCase()) || []);

    const schemas = new Map();

    for (const t of answer.tables || []) {
      if (!schemas.has(t.schemaName)) schemas.set(t.schemaName, { name: t.schemaName, open: true, tables: [] });

      schemas.get(t.schemaName).tables.push({
        ...t,
        used: taken.has(`${t.schemaName}.${t.tableName}`.toLowerCase()),
      });
    }

    db.schemas = [...schemas.values()];
  } catch (e) {
    db.why = e.message;
  } finally {
    db.reading = false;
    pubDraw();
  }
}

/** Adds a table to the composition, if it can be. */
function pubAdd(dbId, schema, table) {
  const db = pubDatabases.find(d => d.id === dbId);
  const found = db?.schemas
    ?.find(s => s.name === schema)?.tables
    ?.find(t => t.tableName === table);

  if (!found || found.used || !found.objectIdColumn || !found.geometryColumn) return;

  pubTree.unshift({
    kind: "layer",
    id: `L${++pubSeq}`,

    // <b>A layer's name is unique inside its service, and two schemas may hold one table
    // name.</b> `public.parcels` and `arsiv.parcels` are an ordinary pair, and composing both
    // under one name is refused by `layer_name_unique_in_service` — at the end, after the whole
    // composition is built. The screen can see it coming, so it does, and the operator renames
    // it afterwards if the suffix is not what they wanted.
    name: pubFreeName(found.tableName),
    source: dbId,
    sourceName: db.name,
    schema: found.schemaName,
    table: found.tableName,
    geometry: found.geometryColumn,
    identity: found.objectIdColumn,
    srid: found.srid,
    type: found.geometryType,
  });

  found.used = true;
  pubPicked = new Set();
  pubDraw();
}

/**
 * A layer name nothing else in this composition is using.
 *
 * <b>Suffixed rather than refused.</b> Somebody dragging the second `parcels` in meant to add
 * it; telling them they cannot until they rename something is a refusal for a problem the
 * screen can solve, and the name is theirs to change afterwards.
 *
 * @param {string} wanted the name to start from
 * @returns {string} that name, or the first free variation of it
 */
function pubFreeName(wanted) {
  const used = new Set(pubLayers().map(l => l.name.toLowerCase()));

  if (!used.has(wanted.toLowerCase())) return wanted;

  for (let n = 2; ; n++) {
    const tried = `${wanted}_${n}`;

    if (!used.has(tried.toLowerCase())) return tried;
  }
}

/**
 * Renames a layer or a group in the composition.
 *
 * <b>The name is what a client asks for, so it is the operator's to choose.</b> A layer arrives
 * named after its table because that is the only name the screen knows; a service somebody else
 * will read deserves better than `ci_buildings_1a3afeeb`.
 *
 * @param {string} id which node
 */
function pubRename(id) {
  const found = pubFind(id);

  if (!found) return;

  const given = prompt(
    found.node.kind === "group" ? "Group name" : "Layer name", found.node.name);

  if (given === null) return;

  const wanted = given.trim();

  if (!wanted) return;

  if (found.node.kind === "layer") {
    const clash = pubLayers().some(l =>
      l.id !== id && l.name.toLowerCase() === wanted.toLowerCase());

    if (clash) {
      toast(`Another layer in this service is already called ${wanted}.`);
      return;
    }
  }

  found.node.name = wanted;
  pubDraw();
}

/** Puts a layer back: it stops being in the composition and becomes draggable again. */
function pubRemove(id) {
  const gone = pubDetach(id);

  if (!gone) return;

  const returned = gone.kind === "group" ? gone.children : [gone];

  for (const layer of returned) {
    const db = pubDatabases.find(d => d.id === layer.source);
    const table = db?.schemas
      ?.find(s => s.name === layer.schema)?.tables
      ?.find(t => t.tableName === layer.table);

    if (table) table.used = false;
  }

  pubTidy();
  pubPicked.delete(id);
  pubDraw();
}

/** Wraps the selected nodes in a group, keeping their order. */
function pubGroup() {
  const rows = pubRows().filter(n => pubPicked.has(n.id));

  if (rows.length < 2) return;

  const where = pubTree.findIndex(n =>
    pubPicked.has(n.id) || (n.kind === "group" && n.children.some(c => pubPicked.has(c.id))));

  const kids = [];

  for (const node of rows) {
    const taken = pubDetach(node.id);

    if (!taken) continue;

    if (taken.kind === "group") { kids.push(...taken.children); } else { kids.push(taken); }
  }

  pubTidy();

  const made = {
    kind: "group",
    id: `G${++pubSeq}`,
    name: `Group ${pubTree.filter(n => n.kind === "group").length + 1}`,
    children: kids,
  };

  pubTree.splice(Math.max(0, Math.min(where, pubTree.length)), 0, made);
  pubPicked = new Set([made.id]);
  pubDraw();
}

function pubUngroup(id) {
  const found = pubFind(id);

  if (!found || found.node.kind !== "group") return;

  pubTree.splice(found.at, 1, ...found.node.children);
  pubPicked = new Set();
  pubDraw();
}

/** Every row the tree draws, in the order it draws them. */
function pubRows() {
  const out = [];

  for (const node of pubTree) {
    out.push(node);
    if (node.kind === "group") out.push(...node.children);
  }

  return out;
}

/* ------------------------------------------------------------------ drawing */

function pubNode(node, inside) {
  if (node.kind === "group") {
    return `<div class="pubnode ${pubPicked.has(node.id) ? "sel" : ""}" draggable="true"
        data-pubnode="${h(node.id)}">
        <div class="pubrow" data-pubgroup="${h(node.id)}">
          <span aria-hidden="true" style="color:var(--faint)">&#9707;</span>
          <span class="pubname pubgroupname">${h(node.name)}</span>
          <span class="pubcount">${num(node.children.length)}</span>
          <button class="pubkill" data-pubkill="${h(node.id)}"
            aria-label="Remove ${h(node.name)}">&#10005;</button>
        </div>
      </div>`
      + node.children.map(c => pubNode(c, true)).join("");
  }

  return `<div class="pubnode ${inside ? "grouped" : ""} ${pubPicked.has(node.id) ? "sel" : ""}"
      draggable="true" data-pubnode="${h(node.id)}">
      <div class="pubrow">
        <span class="pubname" title="${h(node.sourceName)} · ${h(node.schema)}.${h(node.table)}">${h(node.name)}</span>
        <span class="pubsr">EPSG:${num(node.srid)}</span>
        <button class="pubkill" data-pubkill="${h(node.id)}"
          aria-label="Remove ${h(node.name)}">&#10005;</button>
      </div>
    </div>`;
}

/* ------------------------------------------------------------------ publishing */

/** The references this console offers, and why somebody would pick each. */
const PUB_REFERENCES = [
  { code: 0, name: "Each layer's own", note: "no reprojection" },
  { code: 3857, name: "WGS 84 / Pseudo-Mercator", note: "what web basemaps use" },
  { code: 4326, name: "WGS 84", note: "degrees" },
  { code: 5254, name: "TUREF / TM30", note: "Turkish national grid" },
];

/**
 * Asks where the service goes and what it can do, then publishes it.
 *
 * <b>One request, because it is one act.</b> ADR-057 §5h: a service is not created without
 * layers, so this does not make a container and fill it — `POST /admin/publish` writes the
 * service, its groups and its layers in one transaction or none of them.
 */
async function openPublishDialog() {
  const dialog = $("publish");
  const layers = pubLayers();

  const folders = [...new Set([
    ...(await api("/admin/folders").catch(() => null))?.folders?.map(f => f.name || f) ?? [],
  ].filter(Boolean))];

  $("publishBody").innerHTML = `
    <form id="publishForm" autocomplete="off">
      <div class="row">
        <label class="field" style="flex:2 1 60%">Service name
          <input id="pbName" spellcheck="false" required placeholder="cadastre"></label>
        <label class="field" style="flex:1 1 30%">Folder <span class="val">(optional)</span>
          <input id="pbFolder" list="pbFolders" spellcheck="false" placeholder="the root">
          <datalist id="pbFolders">${folders
            .map(f => `<option value="${h(f)}"></option>`).join("")}</datalist></label>
      </div>
      <p class="hint" id="pbNewFolder" hidden></p>

      <div class="row">
        <label class="field" style="flex:1 1 100%">Description <span class="val">(optional)</span>
          <input id="pbAbout" spellcheck="false"
                 placeholder="what somebody finding this in the directory needs to know"></label>
      </div>

      <div class="row">
        <label class="field">Who can see it<select id="pbShare">
          <option value="private" selected>Only me</option>
          <option value="organization">Everybody signed in</option>
          <option value="public">Anybody, without signing in</option>
        </select></label>

        <label class="field">Served in<select id="pbSrid">${PUB_REFERENCES
          .map(r => `<option value="${r.code}">${h(r.name)}${r.code ? ` — EPSG:${r.code}` : ""}</option>`)
          .join("")}</select></label>
      </div>

      <p class="hint" id="pbWarp"></p>

      <!--
        <b>What it can do — ADR-057 §5g.</b> Two faces and a ceiling, and every one of them is
        something this server actually stores: serves_features, serves_tiles and
        capability_ceiling are columns on the service row.

        <b>MapServer and the OGC faces are named and not offered.</b> They follow the feature
        face — derived, with no column to set — so a switch for them would be a control for a
        capability that does not exist, which is ADR-034's prohibition. Saying so beats leaving
        an operator to wonder whether the screen forgot them.
      -->
      <fieldset class="pbcaps">
        <legend>What it can do</legend>

        <div class="row">
          <label class="pbtick"><input type="checkbox" id="pbFeatures" checked>
            <span><b>FeatureServer</b> — query and, where privileges allow, edit</span></label>
          <label class="pbtick"><input type="checkbox" id="pbTiles" checked>
            <span><b>VectorTileServer</b> — vector tiles of these layers</span></label>
        </div>

        <p class="hint">MapServer and the OGC faces follow the feature face and are not chosen
          separately — this server derives them rather than storing them.</p>

        <div class="row" id="pbCeiling">
          <label class="pbtick"><input type="checkbox" id="pbQuery" checked disabled>
            <span>Query <span class="val">— always; a service that answers nothing is a
              stopped one</span></span></label>
          <label class="pbtick"><input type="checkbox" id="pbCreate" checked>
            <span>Create</span></label>
          <label class="pbtick"><input type="checkbox" id="pbUpdate" checked>
            <span>Update</span></label>
          <label class="pbtick"><input type="checkbox" id="pbDelete" checked>
            <span>Delete</span></label>
        </div>

        <p class="hint" id="pbCapsSays"></p>
      </fieldset>

      <p class="hint bad-inline" id="pbRefused" hidden role="alert"></p>
    </form>`;

  $("publishFoot").innerHTML = `
    <button type="button" class="ghost" id="pbCancel">Cancel</button>
    <button type="button" class="primary" id="pbGo">Publish ${num(layers.length)}
      layer${layers.length === 1 ? "" : "s"}</button>`;

  const warp = () => {
    const wanted = Number($("pbSrid").value) || 0;
    const moved = layers.filter(l => wanted && l.srid !== wanted);

    $("pbWarp").innerHTML = wanted === 0
      ? "Every layer answers in whatever its own table is stored in."
      : moved.length === 0
        ? `Every layer is already stored in EPSG:${num(wanted)}, so nothing is reprojected.`
        : `<b>${num(moved.length)} of ${num(layers.length)}</b> will be reprojected on the way
           out: ${moved.map(l => `${h(l.name)} (EPSG:${num(l.srid)})`).join(", ")}.`;
  };

  const folderNote = () => {
    const given = $("pbFolder").value.trim();
    const note = $("pbNewFolder");

    note.hidden = !(given && !folders.includes(given));
    note.textContent = note.hidden
      ? ""
      : `There is no folder called ${given} — publishing will create it.`;
  };

  // <b>The ceiling is a ceiling, so the sentence says what a caller will actually get.</b>
  // Ticking Update does not grant it: the answer is the intersection of this, the reader's
  // privileges and whether the table has an integer identity. A screen that showed the ticks
  // as the outcome would promise something the server can refuse.
  const capsSay = () => {
    const chosen = pubCeiling();
    const faces = [
      $("pbFeatures").checked ? "FeatureServer" : null,
      $("pbTiles").checked ? "VectorTileServer" : null,
    ].filter(Boolean);

    $("pbCapsSays").innerHTML = faces.length === 0
      ? `<span class="bad-inline">With neither face on, the service answers at no address.</span>`
      : `Advertised as <code>${h(chosen.join(","))}</code> on ${faces.join(" and ")}. A reader
         gets the part of that their privileges carry — the ceiling narrows, it never grants.`;
  };

  for (const id of ["pbFeatures", "pbTiles", "pbCreate", "pbUpdate", "pbDelete"]) {
    $(id).addEventListener("change", capsSay);
  }

  $("pbSrid").addEventListener("change", warp);
  $("pbFolder").addEventListener("input", folderNote);
  $("pbCancel").addEventListener("click", () => dialog.close());
  $("publishClose").onclick = () => dialog.close();
  $("pbGo").addEventListener("click", sendPublish);

  warp();
  capsSay();
  dialog.showModal();
  $("pbName").focus();
}

/**
 * The capability ceiling the dialog is asking for, in ArcGIS's spelling.
 *
 * <b>`Query` is always in it.</b> The box is drawn ticked and disabled because the server
 * refuses a ceiling without it — publishing a service that refuses everything is reached by
 * stopping it, which says so in the directory, and this screen should not be a second way to
 * arrive somewhere that looks running.
 *
 * @returns {string[]} the capability names, in document order
 */
function pubCeiling() {
  return ["Query",
    ...($("pbCreate")?.checked ? ["Create"] : []),
    ...($("pbUpdate")?.checked ? ["Update"] : []),
    ...($("pbDelete")?.checked ? ["Delete"] : [])];
}

/** Sends the composition, and turns a refusal into a sentence on the dialog. */
async function sendPublish() {
  const refused = $("pbRefused");
  const button = $("pbGo");
  const name = $("pbName").value.trim();

  if (!name) {
    refused.hidden = false;
    refused.textContent = "A name first — it is what the service is called and how it is found.";
    $("pbName").focus();
    return;
  }

  const srid = Number($("pbSrid").value) || null;

  const nodes = pubTree.map(node => node.kind === "group"
    ? { group: node.name, layers: node.children.map(pubWire) }
    : { layer: pubWire(node) });

  refused.hidden = true;
  button.disabled = true;

  try {
    const made = await api("/admin/publish", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name,
        folder: $("pbFolder").value.trim() || null,
        description: $("pbAbout").value.trim() || null,
        sharing: $("pbShare").value,
        srid,

        // Sent whichever way the boxes sit, because "unset" and "off" are different states on
        // the service row and the operator has just answered the question either way.
        servesFeatures: $("pbFeatures").checked,
        servesTiles: $("pbTiles").checked,
        capabilities: pubCeiling(),
        nodes,
      }),
    });

    $("publish").close();

    // <b>The composition is spent, and the tables it held are free again.</b> Leaving it on
    // screen after a successful publish invites a second press, which is a second service.
    pubTree = [];
    pubPicked = new Set();

    for (const db of pubDatabases) { db.schemas = null; db.open = false; }

    toast(`${made.name} is served at ${made.url}.`, true);
    pubDraw();
  } catch (e) {
    // <b>The server's own sentence.</b> It names which entry, or which collision — a name
    // taken, a table already served, two layers of one name — and each of those is a different
    // repair.
    refused.hidden = false;
    refused.textContent = e.message;
  } finally {
    button.disabled = false;
  }
}

/** One composed layer, as `POST /admin/publish` takes it. */
function pubWire(layer) {
  return {
    name: layer.name,
    dataSourceId: layer.source,
    schemaName: layer.schema,
    tableName: layer.table,
    geometryColumn: layer.geometry,
    identityColumn: layer.identity,
    objectIdColumn: layer.identity,
    srid: layer.srid,
    geometryType: layer.type,
  };
}

/* ------------------------------------------------------------------ interaction */

let pubDragging = null;

document.addEventListener("dragover", event => {
  if (event.target.closest?.("#pubContents")) pubOver(event);
});

document.addEventListener("drop", event => {
  if (event.target.closest?.("#pubContents")) pubDrop(event);
});

document.addEventListener("contextmenu", event => {
  const node = event.target.closest?.("[data-pubnode]");

  if (!node || !event.target.closest("#pubTree")) return;

  event.preventDefault();

  if (!pubPicked.has(node.dataset.pubnode)) {
    pubPicked = new Set([node.dataset.pubnode]);
    pubDraw();
  }

  const found = pubFind(node.dataset.pubnode);

  // <b>Group when several are chosen, ungroup on a group, remove either way.</b> ADR-057 §5b:
  // one level, so a group swept into a group contributes its layers.
  // <b>Grouping wins when several are chosen, because that is what several means.</b> With one
  // node the question is about that node: ungroup it, or rename it.
  if (pubPicked.size > 1) {
    if (confirm(`Group ${pubPicked.size} layers?`)) pubGroup();
    return;
  }

  if (found?.node.kind === "group") {
    if (confirm(`Ungroup "${found.node.name}"? Cancel to rename it instead.`)) {
      pubUngroup(node.dataset.pubnode);
    } else {
      pubRename(node.dataset.pubnode);
    }

    return;
  }

  pubRename(node.dataset.pubnode);
});

document.addEventListener("dragstart", event => {
  const table = event.target.closest?.("[data-pubtable]");
  const node = event.target.closest?.("[data-pubnode]");

  if (table && table.getAttribute("draggable") === "true") {
    pubDragging = { kind: "table", id: table.dataset.pubtable };
    event.dataTransfer.effectAllowed = "copy";
    event.dataTransfer.setData("text/plain", table.dataset.pubtable);
    return;
  }

  if (node) {
    pubDragging = { kind: "node", id: node.dataset.pubnode };
    node.classList.add("dragging");
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", node.dataset.pubnode);
  }
});

document.addEventListener("dragend", () => {
  pubDragging = null;
  $("pubContents")?.classList.remove("dropping");
  document.querySelectorAll("[data-pubnode]").forEach(n =>
    n.classList.remove("dragging", "over", "into"));
});

/**
 * Where a drop would land, drawn while the pointer is over the tree.
 *
 * <b>A group's own row accepts a layer into it; anything else inserts beside.</b> Without the
 * distinction there is no way to put a layer <i>in</i> a group with a mouse, and the only
 * alternative is a menu — which is the shape this screen exists to replace.
 */
function pubOver(event) {
  if (!pubDragging) return;

  event.preventDefault();
  event.dataTransfer.dropEffect = pubDragging.kind === "table" ? "copy" : "move";

  document.querySelectorAll("[data-pubnode]").forEach(n => n.classList.remove("over", "into"));

  if (pubDragging.kind === "table") {
    $("pubContents").classList.add("dropping");
    return;
  }

  const group = event.target.closest?.("[data-pubgroup]");
  const over = event.target.closest?.("[data-pubnode]");

  if (group) { group.closest("[data-pubnode]").classList.add("into"); return; }
  if (over && over.dataset.pubnode !== pubDragging.id) over.classList.add("over");
}

function pubDrop(event) {
  event.preventDefault();

  if (!pubDragging) return;

  if (pubDragging.kind === "table") {
    const [db, schema, table] = pubDragging.id.split("|");
    pubAdd(db, schema, table);
  } else {
    const into = event.target.closest?.("[data-pubgroup]");
    const over = event.target.closest?.("[data-pubnode]");
    const moved = pubDetach(pubDragging.id);

    if (moved) {
      if (into && moved.kind === "layer") {
        const group = pubFind(into.dataset.pubgroup);

        if (group && group.node.kind === "group") { group.node.children.push(moved); }
        else { pubTree.push(moved); }
      } else if (over && over.dataset.pubnode !== pubDragging.id) {
        const target = pubFind(over.dataset.pubnode);

        if (target) {
          // A group cannot go inside a group — ADR-057 §5b — so it lands beside the one it
          // was dropped on.
          if (moved.kind === "group" && target.list !== pubTree) {
            pubTree.splice(pubTree.indexOf(target.group), 0, moved);
          } else {
            target.list.splice(target.at, 0, moved);
          }
        } else {
          pubTree.push(moved);
        }
      } else {
        pubTree.push(moved);
      }

      pubTidy();
      pubPicked = new Set([moved.id]);
      pubDraw();
    }
  }

  $("pubContents").classList.remove("dropping");
  pubDragging = null;
}

/** Selects a row, or extends the selection the way a list does. */
function pubPick(id, event) {
  const rows = pubRows().map(n => n.id);

  if (event.shiftKey && pubPicked.size > 0) {
    const anchored = rows.findIndex(r => pubPicked.has(r));
    const to = rows.indexOf(id);

    pubPicked = new Set(rows.slice(Math.min(anchored, to), Math.max(anchored, to) + 1));
    return;
  }

  if (event.ctrlKey || event.metaKey) {
    if (pubPicked.has(id)) { pubPicked.delete(id); } else { pubPicked.add(id); }
    return;
  }

  pubPicked = new Set([id]);
}

function pubDraw() {
  const tree = $("pubTree");

  if (!tree) return;

  tree.innerHTML = pubTree.length
    ? pubTree.map(n => pubNode(n, false)).join("")
    : `<div class="pubempty">Drag a table here from Databases.<br>
        <span style="font-size:11.5px">The order you build is the order it is served in —
        the top is drawn on top. Select two and right-click to group them.</span></div>`;

  // ------------------------------------------------------------ databases
  let html = "";

  for (const db of pubDatabases) {
    html += `<div class="pubdb" data-pubdb="${h(db.id)}">
      <span style="width:12px;color:var(--faint);font-size:10px">${db.open ? "&#9660;" : "&#9654;"}</span>
      <span>${h(db.name)}</span>
      ${db.reading ? `<span class="val" style="margin-left:auto">reading…</span>` : ""}
    </div>`;

    if (!db.open) continue;

    if (db.why) {
      html += `<div class="pubwhy">${h(db.why)}</div>`;
      continue;
    }

    for (const schema of db.schemas || []) {
      html += `<div class="pubschema" data-pubschema="${h(db.id)}:${h(schema.name)}">
        <span style="width:12px;font-size:10px">${schema.open ? "&#9660;" : "&#9654;"}</span>
        <span>${h(schema.name)}</span>
      </div>`;

      if (!schema.open) continue;

      for (const t of schema.tables) {
        const can = Boolean(t.objectIdColumn) && Boolean(t.geometryColumn);

        html += `<div class="pubtable ${can ? "" : "no"} ${t.used ? "used" : ""}"
            ${can && !t.used ? `draggable="true"` : ""}
            data-pubtable="${h(db.id)}|${h(schema.name)}|${h(t.tableName)}"
            title="${can ? (t.used ? "already served by a layer" : "drag into Contents") : "cannot be published"}">
          <span class="pubdot ${can && !t.used ? "" : "no"}"></span>
          <span>${h(t.tableName)}</span>
          ${t.used ? `<span class="val">· in use</span>` : ""}
          <span class="pubsrid">${t.srid ? "EPSG:" + num(t.srid) : "—"}</span>
        </div>`;

        if (!can) {
          html += `<div class="pubwhy">${t.geometryColumn
            ? "No integer column this server can use as an ArcGIS object id."
            : "No geometry column — a table without one is not a feature class."}</div>`;
        }
      }
    }
  }

  $("pubDbTree").innerHTML = html;

  // ------------------------------------------------------------ what will exist
  const layers = pubLayers();

  $("pubOpen").disabled = layers.length === 0;

  $("pubWhat").innerHTML = layers.length
    ? `<ul class="pubwill">${pubRows().map((n, i) => n.kind === "group"
        ? `<li><b>${h(n.name)}</b> <span class="pubsame">group layer, index ${num(i)}</span></li>`
        : `<li><code>${h(n.name)}</code> <span class="pubsame">index ${num(i)} ·
             ${h(n.schema)}.${h(n.table)}</span></li>`).join("")}</ul>
       <p class="hint" style="padding:0 12px">${num(layers.length)}
          layer${layers.length === 1 ? "" : "s"} in
          ${num(pubTree.filter(n => n.kind === "group").length)} group(s). The reference it is
          served in is chosen when you press Publish.</p>`
    : `<div class="pubempty">Nothing yet. What you build on the left is what will be
        served.</div>`;
}

async function loadSources() {
  const { dataSources } = await api("/admin/datasources");
  $("cSources").textContent = dataSources.length;

  $("sourcesPager").innerHTML = pagerFor("sources", dataSources.length);

  $("sources").innerHTML = dataSources.length === 0
    ? `<tr><td colspan="4" class="empty">None registered.</td></tr>`
    : pageOf("sources", dataSources).map(d => `<tr>
        <td class="name">${h(d.name)}
          <div class="rowmeta">${h(d.kind)}${d.name === "datastore"
            ? " · this server's own hosted store: its connection comes from the "
              + "Graticula:PlatformStore setting on every start, so it is neither edited "
              + "here nor removed"
            : ""}</div></td>
        <td class="val">${d.sealedWithAnotherKey
          ? `<span class="bad-inline">sealed with a key this build does not hold</span>`
          : h(d.summary || "—")}</td>
        <td class="num">${num(d.layerCount)}</td>
        <td class="acts"><button data-probe="${h(d.id)}"
            data-probe-name="${h(d.name)}">Probe</button>${d.name === "datastore"
              ? ""
              : ` <button data-source-edit="${h(d.id)}" data-source-name="${h(d.name)}"
                    data-source-summary="${h(d.summary || "")}"
                    data-source-layers="${num(d.layerCount)}">Edit</button>
                  <button class="danger" data-source-remove="${h(d.id)}"
                    data-source-name="${h(d.name)}"
                    data-source-layers="${num(d.layerCount)}">Remove</button>`}</td>
      </tr>`).join("");
}

/** What the connection dialog is doing: the source it corrects, or null to register one. */
let dbconn = null;

/**
 * Everything the connection dialog knows, as the server's request body.
 *
 * <b>Fields, never a string.</b> An Npgsql connection string quotes a value containing a
 * semicolon and doubles a quote inside one, and a password is exactly where those characters
 * turn up — so a browser that concatenated `Password=` + what was typed would produce a string
 * that parses into something else, on the passwords nobody tests with. The server assembles it
 * with the builder that takes it apart again. The advanced box is the one exception, and it is
 * the caller saying *I have a string already*.
 *
 * @returns {object} the body for `/admin/datasources` and its neighbours
 */
function dbconnBody() {
  const raw = $("dcRaw");

  if (raw && raw.value.trim()) {
    return { name: $("dcName").value.trim(), connectionString: raw.value.trim() };
  }

  return {
    name: $("dcName").value.trim(),
    host: $("dcHost").value.trim(),
    port: Number($("dcPort").value) || 5432,
    database: $("dcDatabase").value.trim(),
    username: $("dcUser").value,
    password: $("dcPassword").value,
  };
}

/**
 * Shows, or stops showing, that the advanced string is what will be sent.
 *
 * <b>The fields are disabled rather than hidden.</b> Somebody who typed a host into them wants
 * to see it still there when they empty the advanced box again, and a control that vanishes and
 * returns is harder to trust than one that is visibly out of use.
 */
function dbconnOverride() {
  const raw = $("dcRaw");
  const on = Boolean(raw && raw.value.trim());
  const said = $("dcOverride");

  for (const id of ["dcHost", "dcPort", "dcUser", "dcPassword", "dcDatabase"]) {
    const field = $(id);

    if (field) {
      field.disabled = on;
      field.closest("label")?.classList.toggle("overridden", on);
    }
  }

  if (said) {
    said.hidden = !on;
    said.textContent = on
      ? "The connection string below is what will be sent. Empty it to go back to the fields."
      : "";
  }

  // <b>Kept open while it has content.</b> Collapsing a disclosure that is deciding the
  // request is how the value became invisible in the first place.
  if (on) { $("dcAdvanced").open = true; }
}

/**
 * Says something in the dialog's own result line.
 *
 * @param {string} tone the class: `ok`, `warn`, `alert` or empty
 * @param {string} head the bold half
 * @param {string} rest the sentence
 */
function dbconnSays(tone, head, rest) {
  const said = $("dcResult");

  if (!said) return;

  said.className = `testresult ${tone}`;
  said.innerHTML = `<b>${h(head)}</b>${h(rest || "")}`;
}

/**
 * Fills the database combo from the server, which is also the test.
 *
 * <b>Nothing comes back unless the host resolved, the port answered, TLS agreed and the
 * credential was accepted</b> — so a filled list says all four at once, and an empty one says
 * which of them failed in the probe's own sentence. That is why this is on the combo rather than
 * behind a separate *Test* button: the operator's next action after typing a password is to
 * choose a database, and the choosing is the check.
 *
 * @returns {Promise<void>} when the list has been filled or the refusal shown
 */
async function dbconnFill() {
  const combo = $("dcDatabase");
  const chosen = combo.value;

  // <b>Three events reach this and a server should hear about one of them.</b> A mouse fires
  // `mousedown` then `focus` then `click`, a keyboard fires `focus` alone, and a synthetic
  // `element.click()` — which is what the console's own harness sends — fires only `click`. All
  // three are listened for, so the control answers however it was reached; this is what keeps
  // that from being three requests. The key is what the answer depends on, so changing the host
  // or the credential asks again and moving the caret does not.
  const asked = [
    $("dcHost").value.trim(),
    $("dcPort").value,
    $("dcUser").value,
    $("dcPassword").value,
  ].join("\u0000");

  if (combo.dataset.asked === asked) return;

  if (!$("dcHost").value.trim() || !$("dcUser").value) {
    dbconnSays("warn", "The host and the user first. ",
      "This list comes from the server, so it cannot be asked for until there is a server to "
      + "ask and somebody to ask as.");
    return;
  }

  if (combo.dataset.filling === "yes") return;

  combo.dataset.filling = "yes";
  dbconnSays("", "Asking the server…", "");

  try {
    const said = await api("/admin/datasources/databases", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...dbconnBody(), database: "" }),
    });

    const names = said.databases || [];

    if (!names.length) {
      dbconnSays(sourceTone(said.outcome), spaced(said.outcome || "No answer"), said.message || "");
      return;
    }

    // <b>A datalist, not a select, and that is not a detail.</b> A database this account may
    // connect to is not always a database it may *see* in `pg_database` — and on a server that
    // hides them, a `<select>` would be a control that refuses the correct answer. The list is a
    // suggestion over a text field, so what the server knows helps and what it does not know
    // does not block.
    $("dcDatabases").innerHTML = names
      .map(n => `<option value="${h(n)}"></option>`).join("");

    if (!chosen && names.length) { combo.value = names[0]; }

    combo.dataset.asked = asked;

    dbconnSays("ok", `${num(names.length)} database${names.length === 1 ? "" : "s"}. `,
      "The connection worked, so what is left is choosing one.");
  } catch (e) {
    dbconnSays("alert", "Refused. ", e.message);
  } finally {
    combo.dataset.filling = "";
  }
}

/**
 * Opens the connection dialog, to register a source or to correct one.
 *
 * <b>One dialog for both, because they are the same eight fields.</b> Registering and correcting
 * differed only in which endpoint took the answer, and keeping two forms meant a repair to one
 * of them left the other — which is what happened to the empty box in
 * [D-228](docs/architecture-debt.md).
 *
 * <b>The password is never filled in, on either path, and the dialog says why.</b> This server
 * seals it and does not read it back: returning it would hand a credential to anybody holding
 * `content:registerDataStore`, and keeping it here to merge would let them repoint a source at a
 * listener of their own and have this server deliver to it. So saving requires already knowing
 * it.
 *
 * @param {object|null} source the source being corrected, or null to register a new one
 * @returns {Promise<void>} when the dialog is on screen
 */
async function openDbConnection(source) {
  dbconn = source ? { ...source, force: false } : null;

  const dialog = $("dbconn");

  $("dbconnTitle").textContent = source
    ? `${source.name} — connection`
    : "Database connection";

  $("dbconnBody").innerHTML = `
    <form id="dcForm" autocomplete="off">
      <div class="row">
        <label class="field" style="flex:1 1 100%">Name
          <input id="dcName" spellcheck="false" placeholder="cadastre"
                 value="${h(source ? source.name : "")}"></label>
      </div>
      <div class="row">
        <label class="field" style="flex:3 1 60%">Instance
          <input id="dcHost" spellcheck="false" placeholder="localhost" required></label>
        <label class="field" style="flex:1 1 20%">Port
          <input id="dcPort" type="number" min="1" max="65535" value="5432"></label>
      </div>
      <div class="row">
        <label class="field" style="flex:1 1 45%">User name
          <input id="dcUser" spellcheck="false" autocomplete="off" required></label>
        <label class="field" style="flex:1 1 45%">Password
          <input id="dcPassword" type="password" autocomplete="new-password"></label>
      </div>
      <div class="row">
        <label class="field" style="flex:1 1 100%">Database
          <input id="dcDatabase" list="dcDatabases" spellcheck="false"
                 placeholder="press to list what is on that server">
          <datalist id="dcDatabases"></datalist></label>
      </div>
      <p class="hint">${source
        ? "The password is the one thing not filled in: it is sealed and this server does not "
          + "read it back, so it has to be typed again — which also means saving requires "
          + "already knowing it."
        : "Choosing a database asks the server for the list, which only answers if the host, the "
          + "port and the credential are all right."}</p>
      <details id="dcAdvanced">
        <summary>Write the connection string instead</summary>
        <p class="hint">For anything the fields above cannot say — an SSL mode, a timeout, an
          application name. Filling this in replaces every field above it.</p>
        <label class="field" style="flex:1 1 100%">Connection string
          <input id="dcRaw" spellcheck="false"
                 placeholder="Host=…;Port=5432;Database=…;Username=…;Password=…"></label>
      </details>
      <p class="hint" id="dcOverride" hidden></p>
      <div id="dcResult" role="status" aria-live="polite"></div>
    </form>`;

  $("dbconnFoot").innerHTML = `
    <button type="button" id="dcTest">Test connection</button>
    <button type="button" class="primary" id="dcSave">${source ? "Save" : "Register"}</button>
    <button type="button" class="ghost" id="dcCancel">Cancel</button>`;

  // <b>An abandoned advanced string still wins, so the form has to say so.</b> Found by a
  // design review on 2026-09-06: type a host into *Write the connection string instead*, change
  // your mind, collapse the disclosure — and the box keeps the value, `dbconnBody` keeps
  // preferring it, and Test reports a failure against a host that is nowhere on screen. The
  // fields above are then visibly right and completely ignored.
  //
  // <b>Said rather than silently discarded.</b> Clearing the box on collapse would throw away
  // something somebody typed; leaving it unsaid is what the review caught. So the fields grey
  // out, the disclosure stays open while it has content, and a line under it names what is
  // being sent.
  $("dcRaw").addEventListener("input", dbconnOverride);

  $("dcDatabase").addEventListener("mousedown", dbconnFill);
  $("dcDatabase").addEventListener("focus", dbconnFill);
  $("dcDatabase").addEventListener("click", dbconnFill);
  $("dcTest").addEventListener("click", dbconnTest);
  $("dcSave").addEventListener("click", dbconnSave);
  $("dcCancel").addEventListener("click", () => dialog.close());
  $("dbconnClose").onclick = () => dialog.close();

  $("dcForm").addEventListener("submit", event => {
    event.preventDefault();
    dbconnSave();
  });

  // <b>Focus goes back to the table, said rather than inherited.</b> Closing a `<dialog>`
  // restores focus to whatever had it when the dialog opened — which is the Edit button when a
  // person pressed it, and nothing at all when the button was activated any other way. A
  // keyboard reader who lands on `<body>` has to tab from the top of the page, and this screen
  // has been measured doing exactly that once before.
  //
  // <b>`onclose` rather than `addEventListener`, because this function runs again every time
  // the dialog opens</b> and a listener added on each of those would fire once per opening.
  // <b>Back to whatever opened it, and `focusSources` was the wrong answer.</b> Yesterday this
  // sent focus to the first row of the table, which is right only when the first row is the one
  // you pressed — a design review edited a later row and watched focus land on row one. The
  // opener is remembered because a synthetic click does not focus a button and a real one does,
  // so `<dialog>`'s own restoration cannot be relied on either way.
  const opener = document.activeElement instanceof HTMLElement
      && document.activeElement !== document.body
    ? document.activeElement
    : null;

  dialog.onclose = () => {
    dbconn = null;

    if (opener && opener.isConnected && opener.offsetParent !== null) {
      opener.focus();
    } else {
      focusSources();
    }
  };

  dbconnOverride();
  dialog.showModal();

  if (!source) {
    $("dcHost").focus();
    return;
  }

  // <b>Everything but the password, read from the server rather than retyped from memory.</b>
  // Owner, 2026-09-05: *edit dediğimde boş oluyor.* The host, the port, the database and the
  // user are what is being corrected, and asking somebody to reproduce them turns one
  // correction into two mistakes.
  try {
    const said = await api(`/admin/datasources/${encodeURIComponent(source.id)}/connection`);

    $("dcHost").value = said.host || "";
    $("dcPort").value = said.port || 5432;
    $("dcDatabase").value = said.database || "";
    $("dcUser").value = said.username || "";
    $("dcPassword").focus();
  } catch (e) {
    // A source sealed with a key this build no longer holds cannot be read back at all, and
    // that is worth saying here rather than leaving somebody to wonder why this one is empty.
    dbconnSays("alert", "Not filled in. ", `${e.message} Type the whole connection.`);
    $("dcHost").focus();
  }
}

/**
 * Connects and reports, storing nothing.
 *
 * @returns {Promise<void>} when the answer is on screen
 */
async function dbconnTest() {
  const button = $("dcTest");

  button.disabled = true;
  dbconnSays("", "Connecting…", "");

  try {
    const r = await api("/admin/datasources/test", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(dbconnBody()),
    });

    dbconnSays(sourceTone(r.outcome), spaced(r.outcome || "Done"), r.message || "");
  } catch (e) {
    dbconnSays("alert", "Refused. ", e.message);
  } finally {
    button.disabled = false;
  }
}

/**
 * Registers or corrects, and turns a refusal into the decision it is.
 *
 * <b>Two refusals arrive here and they are different.</b> *Cannot connect* is a typo — the
 * fields keep what was typed and the message says what to check. *These layers would stop
 * working* is a judgement: the server has connected, looked, and found the tables missing, so
 * the dialog offers to proceed with the list in front of the operator rather than asking them to
 * guess in advance.
 *
 * @returns {Promise<void>} when it has saved or said why not
 */
async function dbconnSave() {
  const button = $("dcSave");
  const body = dbconnBody();

  if (!body.name) {
    dbconnSays("warn", "A name first. ", "It is what this source is called in the list.");
    $("dcName").focus();
    return;
  }

  button.disabled = true;

  try {
    const answer = dbconn
      ? await api(
        `/admin/datasources/${encodeURIComponent(dbconn.id)}`
          + (dbconn.force ? "?force=true" : ""),
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
        })
      : await api("/admin/datasources", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });

    $("dbconn").close();
    dbconn = null;

    toast(answer.name
      ? `${answer.name} now reads ${answer.summary || "that connection"}.`
      : "Saved.", true);

    await loadSources();
  } catch (e) {
    // <b>The missing-layers refusal is a decision, so it is offered as one.</b> The server has
    // named which layers would stop working; repeating the save with `force` is the operator
    // agreeing to that list rather than to an abstraction.
    if (dbconn && /would stop working|no longer/i.test(e.message)) {
      dbconnSays("warn", "Refused. ", `${e.message} Press Save again to do it anyway.`);
      dbconn.force = true;
    } else {
      dbconnSays("alert", "Refused. ", e.message);
    }
  } finally {
    button.disabled = false;
  }
}

/**
 * Removes a source, once, with the count in the question.
 *
 * <b>Three things the design review of 2026-08-19 found here, all of them about what happens
 * afterwards.</b> The refusal had no `role="alert"`, so a screen-reader user heard nothing while a
 * sighted user saw red text — and the Edit form two functions up had it right, which is the worst way
 * to be inconsistent. Success said nothing at all: `#probe` was cleared and the only signal was a row
 * disappearing, which on a paged list is a row that may already have scrolled out of view. And
 * clearing `#probe` deleted the button that had just been pressed, dropping focus to `<body>` — a
 * keyboard user lost their place and had to tab from the top.
 */
async function removeSource(id, name, layers) {
  if (layers > 0) {
    sourcePanel(`<h2>${h(name)}</h2>
      <div class="panel pad"><p class="hint bad-inline" role="alert">${num(layers)}
        layer${layers === 1 ? "" : "s"} still read from this source. Unpublish
        ${layers === 1 ? "it" : "them"} first — removing the source would take
        ${layers === 1 ? "its" : "their"} services with it.</p></div>`);
    return;
  }

  if (!confirm(
      `Remove the connection "${name}"? Nothing is published on it, so nothing stops serving.`)) {
    return;
  }

  try {
    await api(`/admin/datasources/${encodeURIComponent(id)}`, { method: "DELETE" });

    // <b>Said, not implied.</b> A row vanishing is the same signal as a row that scrolled away.
    sourcePanel(`<h2>Removed</h2>
      <div class="panel pad"><p class="hint">The connection <b>${h(name)}</b> is gone. Nothing was
        published on it, so no service stopped serving.</p></div>`);

    await loadSources();
    focusSources();
  } catch (e) {
    sourcePanel(`<h2>${h(name)}</h2>
      <div class="panel pad"><p class="hint bad-inline" role="alert">${
        h(e.message || String(e))}</p></div>`);
  }
}

/**
 * Writes into the panel under the sources table.
 *
 * <b>Named for the panel rather than called `say`</b>, which is what it was for one draft: there is
 * already a local `say` inside `publishGeodatabase` that shows a refusal, and two functions with one
 * name in one file are a reader's problem even when the scopes do not collide.
 */
function sourcePanel(markup) {
  $("probe").innerHTML = markup;
}

/**
 * Puts focus somewhere on screen after a panel is torn down.
 *
 * <b>Because `innerHTML = ""` deletes whatever had focus.</b> The browser then sends focus to
 * `<body>`, which for a keyboard user means starting the page again. The first button of the sources
 * table is the nearest thing that is still there and still meaningful — it is the row the reader was
 * working in, or its neighbour after a removal.
 */
function focusSources() {
  const first = document.querySelector("#sources td.acts button");

  if (first && first.offsetParent !== null) {
    first.focus({ preventScroll: true });
  }
}

/**
 * The last probe, held so its table can be paged and filtered without probing again.
 *
 * <b>D-103: this table had no cap, where every other list in this console pages at ten.</b> A
 * source with 77 publishable tables rendered all 77 and took the page past five thousand pixels
 * — on a database that is ordinary at the 100–1,000 services this product targets.
 */
let probeShown = null;

/**
 * Shows what a probe found: the shell, once.
 *
 * <b>The filter and the header are written here and the rows are written by
 * {@link drawProbeRows}</b>, so typing in the box does not replace the box. Every other filtered
 * list in this file is built the same way, and the one that was not — the share dialog's search —
 * is recorded in this file as a defect twice over.
 *
 * <b>*Probed just now* is gone from the heading.</b> It was true when the only way to see this
 * table was to probe; with a result held across page turns it would go on saying so on page
 * eight.
 */
function renderProbe(name, r) {
  probeShown = { name, result: r || {} };
  resetPage("probeRows");

  const all = probeShown.result.tables || [];

  $("probe").innerHTML = `
    <h2>${h(name)} — probed</h2>
    <div class="panel pad">
      <div class="row" style="margin-bottom:10px">
        ${pill(r.outcome)}
        <span style="font-size:13.5px">${h(r.message)}</span>
      </div>
      <dl class="facts" style="grid-template-columns:auto 1fr auto 1fr">
        <dt>PostgreSQL</dt><dd>${h(r.serverVersion || "—")}</dd>
        <dt>PostGIS</dt><dd>${h(r.postgisVersion || "—")}</dd>
        <dt>Can publish</dt><dd>${r.canPublish ? "yes" : "no"}</dd>
        <dt>Tables visible</dt><dd>${num(all.length)}</dd>
      </dl>
    </div>
    ${all.length ? `<div class="panel" style="margin-top:14px">
      <div class="row" style="margin:0 0 10px">
        <input type="search" id="probeFilter" placeholder="Filter tables…"
          ${all.length <= PAGE_SIZE ? "hidden" : ""}>
        <span class="val" id="probeCount"></span>
      </div>
      <table>
        <thead><tr><th>Schema</th><th>Table</th><th>Geometry</th><th class="num">SRID</th>
          <th>Object id</th><th>Writable</th></tr></thead>
        <tbody id="probeRows"></tbody>
      </table>
      <div id="probePager"></div>
    </div>` : ""}`;

  drawProbeRows();
}

/**
 * Draws the held probe's rows, at whatever page and filter they are on.
 *
 * <b>Sliced rather than re-read, which is the opposite of what the pager usually does.</b> Every
 * other paged list re-runs its loader on a page turn, so the page you turn to is as fresh as the
 * page you refresh onto. A probe is not that kind of read: it opens a connection to somebody
 * else's database on the operator's explicit instruction, and turning a page is not that
 * instruction.
 */
function drawProbeRows() {
  if (!probeShown || !$("probeRows")) return;

  const all = probeShown.result.tables || [];
  const needle = ($("probeFilter")?.value || "").trim().toLowerCase();

  const shown = needle
    ? all.filter(t => [t.schemaName, t.tableName, t.geometryColumn, t.geometryType]
        .some(v => (v || "").toLowerCase().includes(needle)))
    : all;

  $("probeCount").textContent = needle
    ? `${shown.length} of ${all.length} match`
    : `${all.length} table${all.length === 1 ? "" : "s"}`;

  $("probeRows").innerHTML = shown.length === 0
    ? `<tr><td colspan="6" class="empty">No table matches what you typed.</td></tr>`
    : pageOf("probeRows", shown).map(t => `<tr>
        <td class="val">${h(t.schemaName)}</td>
        <td class="name">${h(t.tableName)}</td>
        <td class="val">${h(t.geometryType)} · ${h(t.geometryColumn)}</td>
        <td class="num">${h(t.srid)}</td>
        <td class="val">${h(t.objectIdColumn || "—")}</td>
        <td>${t.writable ? "yes" : "read only"}</td>
      </tr>`).join("");

  $("probePager").innerHTML = pagerFor("probeRows", shown.length);
}

// ----------------------------------------------------------------- operations

async function loadOperations() {
  let health;
  try { health = await api("/admin/health"); }
  catch (e) {
    $("storeMetrics").innerHTML = metric("Platform store", "unreachable", e.message);

    // <b>And the dot with it.</b> A green heading over a card that says *unreachable* is the
    // page contradicting itself, which is the fault this console keeps a rule about.
    if ($("storeDot")) {
      $("storeDot").className = "dot unusable";
      $("storeDot").title = e.message || "The platform store could not be read.";
    }

    return;
  }

  const store = health.platformStore || {};

  // <b>The dot is the two facts that can fail, and nothing else.</b> A card's heading dot that
  // is always green is decoration; this one is red when the store cannot be reached and amber
  // when it can but the server calls itself degraded.
  const storeDot = $("storeDot");

  if (storeDot) {
    storeDot.className = "dot " + (!store.reachable
      ? "unusable"
      : health.status === "ok" ? "ok" : "degraded");

    storeDot.title = !store.reachable
      ? (store.error || "The platform store is not reachable.")
      : health.status === "ok" ? "Reachable, and the server calls itself healthy."
        : "Reachable, and the server calls itself degraded.";
  }

  $("storeMetrics").innerHTML =
    metric("Status", h(health.status === "ok" ? "ok" : "degraded")) +
    metric("Reachable", store.reachable ? "yes" : "no", store.error || "") +
    metric("Layers", num(store.layers)) +
    metric("Version", h(health.version || "—"));

  const r = health.runtime || {};
  const pause = r.uptimeMilliseconds
    ? (100 * (r.gcPauseMilliseconds || 0) / r.uptimeMilliseconds) : 0;
  $("runtimeMetrics").innerHTML =
    metric("Uptime", duration(r.uptimeMilliseconds)) +
    metric("Managed heap", bytes(r.heapBytes)) +
    metric("Allocated since start", bytes(r.allocatedBytes)) +
    metric("GC pause", `${pause.toFixed(pause < 1 ? 2 : 1)}<small>% of wall-clock</small>`) +
    metric("Collections", `${num(r.gen0)}<small>/</small>${num(r.gen1)}<small>/</small>${num(r.gen2)}`,
           "gen0 / gen1 / gen2") +
    metric("Cores", `${num(r.cores)}${r.serverGc ? "" : ""}`, r.serverGc ? "server GC" : "workstation GC");

  const tiles = health.tileCache || {};
  const shapes = health.describedShapes || {};
  $("cacheMetrics").innerHTML =
    metric("Tiles cached", num(tiles.entries)) +
    metric("On disk", `${num(tiles.megabytes)}<small>MB</small>`) +
    metric("Building now", num(tiles.building)) +
    metric("Shapes remembered", num(shapes.count)) +
    metric("Shape lifetime", `${num(shapes.lifetimeSeconds)}<small>s</small>`);
  $("cacheNotes").textContent = [tiles.note, shapes.note].filter(Boolean).join(" ");

  const { routes, ungoverned } = await api("/admin/routes");

  // ADR-018 condition 5 is a number that is zero or is not, which is exactly what a dot can say.
  const routeDot = $("routeDot");

  if (routeDot) {
    routeDot.className = "dot " + (ungoverned === 0 ? "ok" : "unusable");
    routeDot.title = ungoverned === 0
      ? "Every route is governed — ADR-018 condition 5 holds."
      : `${ungoverned} route(s) are ungoverned — ADR-018 condition 5 is failing.`;
  }

  $("routeMetrics").innerHTML =
    metric("Routes", num(routes.length)) +
    metric("Ungoverned", num(ungoverned),
           ungoverned === 0 ? "ADR-018 condition 5 holds" : "ADR-018 condition 5 is failing");
  $("routes").innerHTML = routes.map(route => `<tr>
      <td class="val">${h(route.pattern)}</td>
      <td class="val">${h((route.methods || []).join(", "))}</td>
      <td>${route.governed ? h(route.by || "") : pill("ungoverned")}</td>
    </tr>`).join("");

  // Stamped, because a runtime figure with no time on it is read as "now" for as
  // long as the tab stays open — and uptime and GC pause are exactly the numbers
  // somebody compares across a few minutes.
  $("opsWhen").textContent = "read at " + new Date().toLocaleTimeString();
}

async function refreshHealth() {
  try {
    const health = await api("/admin/health");
    const ok = health.status === "ok";
    $("healthDot").className = "dot " + (ok ? "" : "bad");
    $("healthLine").textContent = ok
      ? `platform store reachable · ${health.platformStore.layers} layers`
      : "DEGRADED — platform store unreachable";
  } catch {
    $("healthDot").className = "dot unknown";
    $("healthLine").textContent = "health unreachable";
  }
}

// ------------------------------------------------------------------ new layer

const FIELD_TYPES =
  ["Text", "Integer", "BigInteger", "SmallInteger", "Double", "Single", "Boolean", "Date", "Guid"];

/**
 * The three ways a feature layer comes into being, as the second screen lists them.
 *
 * <b>An array rather than three blocks of markup</b>, because the radio list, the heading it gets on
 * the third screen and the route the *Next* takes all read the same row — and D-74's lesson is that a
 * set of values with no one place naming them all gains a member somewhere and loses it everywhere
 * else.
 */
const ITEM_ROUTES = [
  {
    id: "design",
    icon: "featurelayer",
    title: "Define your own layer",
    lede: "Specify the fields and the geometry. Creates an empty layer you fill through the feature "
        + "service.",
    form: "designForm",
    submit: "Create empty layer",
  },
  {
    id: "registered",
    icon: "table",
    title: "Publish a table this server can reach",
    lede: "Use a table in a registered PostGIS database. Nothing is copied — the layer reads the "
        + "table where it is.",
    form: "regForm",
    submit: "Publish",
  },
  {
    id: "import",
    icon: "upload",
    title: "Upload a file",
    lede: "Use the fields and the data in a zipped shapefile or a GeoJSON FeatureCollection.",
    form: "importForm",
    submit: "Import and publish",
  },
];

/** Which screen the dialog is showing: `item`, `kind`, or a route id. */
let itemStep = "item";

/** Which route the radio list has selected, remembered so Back returns to the answer given. */
let itemRoute = "design";

/**
 * A file chosen on the first screen, held until the import form exists to receive it.
 *
 * <b>Held rather than assigned, because the input is not in the document yet.</b> The drop zone and
 * the *Your device* button are on screen one and `#iFile` is on screen three, so the `File` waits
 * here and `#iFile.files` is set from a `DataTransfer` once the form is drawn — which is the only way
 * to fill a file input from script, and it keeps the form with one source of truth rather than two.
 */
let handedFile = null;

/** Opens the New item dialog on its first screen. */
function openAddItem() {
  itemStep = "item";
  handedFile = null;

  // <b>The drawer is emptied, and this is not tidiness.</b> Both surfaces build a `#newResult` box
  // for the server's answer, and two elements with one id means `getElementById` returns whichever
  // comes first in the document — the dialog's, always, since it is written above the drawer.
  $("drawerBody").innerHTML = "";
  closeDrawer();

  drawAddItem();

  // <b>And nothing focuses the heading here.</b> `showModal` sets focus itself and honours the
  // `autofocus` on *Your device*, which is the better landing for the first screen — the browser's own
  // default is the first tabbable element, the ✕, where a stray Enter dismissed the dialog. Calling
  // `nameTheScreen` after this would take that back; the heading is for the transitions, where there
  // is no `showModal` to do it and the redraw drops focus on the floor.
  $("addItem").showModal();
}

/** Draws whichever screen `itemStep` names, with the footer that screen needs. */
function drawAddItem() {
  // <b>Wide only where a table needs it.</b> The reading screen lists five columns against
  // feature-class names forty characters long; every other screen is a short form and 680px is
  // right for reading prose. Set here rather than inside the screen so that leaving it takes the
  // width back.
  $("addItem").classList.toggle("wide", itemStep === "inspect" || itemStep === "publish");

  if (itemStep === "item") drawItemKinds();
  else if (itemStep === "kind") drawLayerRoutes();
  else if (itemStep === "inspect") drawInspect();
  else if (itemStep === "publish") drawPublish();
  else drawRouteForm(itemStep);

  nameTheScreen();
}

/**
 * Moves focus to the heading of whichever screen was just drawn.
 *
 * <b>Because a redraw dropped it on the floor.</b> Measured by the design review 2026-08-19:
 * `document.activeElement` was `<body>` after the tile and after *Next*, since replacing
 * `#addItemBody` wholesale destroys whatever held focus. A screen-reader user got no announcement
 * that the screen had changed and a keyboard user's focus ring vanished until the next Tab.
 *
 * <b>The heading rather than the first control</b>, on two grounds. It is what announces *which*
 * screen you are now on, which is the thing that changed; and a heading does nothing when you press
 * Enter — where the browser's own default put focus on the close button, so a stray Enter on opening
 * dismissed the dialog. `autofocus` on *Your device* handles the first screen's opening; this handles
 * every transition after it.
 */
function nameTheScreen() {
  const dialog = $("addItem");
  if (!dialog?.open) return;

  const title = $("addItemTitle");
  if (title) title.focus({ preventScroll: true });
}

/** Screen one: drop a file, or choose what kind of item to make. */
function drawItemKinds() {
  $("addItemTitle").textContent = "New item";
  $("addItemFoot").innerHTML = "";

  // <b>The two halves are one question, and the first version did not say so.</b> The design review
  // read this screen as two disconnected regions: the drop zone is a complete module with its own
  // dashed edge and tinted ground, the tile grid below has no border at rest, and the sentence
  // *"or choose an option"* most naturally points at the button inside the same box rather than at a
  // grid past a gap. So the drop zone now names what it is for, and the divider says *or* out loud —
  // which is what screen two gets from its `.picklede` and this screen had no equivalent of.
  $("addItemBody").innerHTML = `
    <div class="dropzone" id="dropzone">
      ${icon("upload")}
      <p>Drag and drop a file here</p>
      <button type="button" class="ghost" id="fromDevice" autofocus>${icon("device")} Your device</button>
      <span class="val">A zipped shapefile, a zipped File Geodatabase, or a GeoJSON
        FeatureCollection</span>
      <input type="file" id="deviceFile" hidden
             accept=".zip,.json,.geojson,application/zip,application/geo+json">
    </div>

    <p class="orbar"><span>or start from a type</span></p>

    <div class="newtiles">
      <button type="button" class="newtile" id="kindFeatureLayer">
        <span class="glyph">${icon("featurelayer")}</span>
        <span><b>Feature layer</b>
          <span>Create an editable layer — define the fields yourself, publish a table this server
            can reach, or upload a file.</span></span>
      </button>
    </div>`;

  const zone = $("dropzone");

  // <b>Four listeners, and `dragleave` is the one that is easy to get wrong.</b> It fires when the
  // pointer crosses onto a child, so the highlight would flicker over the button and the text; the
  // guard is that the element being entered is still inside the zone.
  zone.addEventListener("dragover", event => {
    event.preventDefault();
    zone.classList.add("over");
  });
  zone.addEventListener("dragleave", event => {
    if (!zone.contains(event.relatedTarget)) zone.classList.remove("over");
  });
  zone.addEventListener("drop", event => {
    event.preventDefault();
    zone.classList.remove("over");
    takeFile(event.dataTransfer?.files);
  });

  $("fromDevice").addEventListener("click", () => $("deviceFile").click());
  $("deviceFile").addEventListener("change", event => takeFile(event.target.files));

  $("kindFeatureLayer").addEventListener("click", () => {
    itemStep = "kind";
    drawAddItem();
  });
}

/**
 * A file arriving from the drop zone or the device button.
 *
 * Both land on the import form with the file already in hand, because asking for it again after it
 * has been dropped is the interaction the drop zone exists to remove.
 */
function takeFile(files) {
  if (!files || files.length === 0) return;

  handedFile = files[0];
  itemRoute = "import";
  itemStep = "import";
  drawAddItem();
}

/** Screen two: which of the three routes makes the layer. */
function drawLayerRoutes() {
  $("addItemTitle").textContent = "Create a feature layer";

  $("addItemBody").innerHTML = `
    <p class="picklede">Select an option to create a feature layer.</p>
    ${ITEM_ROUTES.map(route => `
      <label class="pickrow${route.id === itemRoute ? " on" : ""}" data-route="${route.id}">
        <input type="radio" name="layerRoute" value="${route.id}"
               ${route.id === itemRoute ? "checked" : ""}>
        <span><b>${h(route.title)}</b><span class="lede">${h(route.lede)}</span></span>
      </label>`).join("")}`;

  $("addItemFoot").innerHTML = `
    <button type="button" class="ghost" id="itemBack">Back</button>
    <span class="fill"></span>
    <button type="button" class="ghost" id="itemCancel">Cancel</button>
    <button type="button" class="primary" id="itemNext">Next</button>`;

  // The ground moves with the choice, so the selected row is legible without hunting for the dot.
  $("addItemBody").addEventListener("change", event => {
    itemRoute = event.target.value;
    for (const row of $("addItemBody").querySelectorAll(".pickrow")) {
      row.classList.toggle("on", row.dataset.route === itemRoute);
    }
  });

  $("itemBack").addEventListener("click", () => {
    itemStep = "item";
    drawAddItem();
  });
  $("itemCancel").addEventListener("click", () => $("addItem").close());
  $("itemNext").addEventListener("click", () => {
    itemStep = itemRoute;
    drawAddItem();
  });
}

/**
 * Screen three: the chosen route's form.
 *
 * <b>The forms and their ids are unchanged from the drawer they came out of.</b> `createDesigned`,
 * `createImported` and `publishRegistered` read `#dName`, `#iFile`, `#rSource` and the rest, and four
 * tests in `ImportFormTests` assert the import form's contract. Moving markup is not the moment to
 * also rewrite what it submits.
 */
function drawRouteForm(id) {
  const route = ITEM_ROUTES.find(candidate => candidate.id === id);
  if (!route) {
    itemStep = "item";
    return drawAddItem();
  }

  $("addItemTitle").textContent = route.title;

  // <b>The primary is in the footer, and leaving it inline was measured wrong.</b> The design review
  // of 2026-08-19 found *Publish* off the bottom of the screen at 1024×720 in the form's **default**
  // state, and *Create empty layer* 44 pixels below the fold at 1440×900 once ten fields were added —
  // while Back and Cancel, which abandon the work, stayed pinned in view the whole time. It came
  // inline because the markup was lifted whole out of the drawer, where the whole panel scrolled
  // together; here the chrome is fixed and the body is not, so the button that finishes the
  // transaction has to live in the chrome. Screen two already does this with its *Next*.
  $("addItemFoot").innerHTML = `
    <button type="button" class="ghost" id="itemBack">Back</button>
    <span class="fill"></span>
    <button type="button" class="ghost" id="itemCancel">Cancel</button>
    <button type="button" class="primary" id="itemSubmit">${h(route.submit)}</button>`;

  $("itemBack").addEventListener("click", () => {
    itemStep = "kind";
    drawAddItem();
  });
  $("itemCancel").addEventListener("click", () => $("addItem").close());

  // `requestSubmit` rather than `submit`: it runs the form's own validation and fires the submit
  // event the route's handler is listening for, which is exactly what pressing an inline button did.
  $("itemSubmit").addEventListener("click", () => $(route.form)?.requestSubmit());

  if (id === "design") return drawDesignForm();
  if (id === "import") return drawImportForm();
  return drawRegisteredForm();
}

/** Define your own layer: fields, geometry, sharing. */
function drawDesignForm() {
  $("addItemBody").innerHTML = `
    <p class="hint">For data you are going to collect. Creates an empty feature class you fill
      through the feature service. <code>objectid</code> and <code>geom</code> are made for you,
      stored in Web Mercator so the layer can serve tiles.</p>
    <form id="designForm" autocomplete="off">
      <div class="row">
        <label class="field">Name<input type="text" id="dName" placeholder="inspections" required></label>
        <label class="field">Geometry<select id="dGeom">
          <option>Point</option><option>MultiPoint</option><option>LineString</option>
          <option>MultiLineString</option><option selected>Polygon</option><option>MultiPolygon</option>
        </select></label>
        <label class="field">Sharing<select id="dShare">
          <option value="private" selected>private</option>
          <option value="organization">organization</option>
          <option value="public">public</option>
        </select></label>
      </div>
      <table>
        <thead><tr><th>Field</th><th>Type</th><th>Required</th><th></th></tr></thead>
        <tbody id="dFields"></tbody>
      </table>
      <div class="row" style="margin:10px 0 0">
        <button type="button" id="dAdd" class="ghost">Add field</button>
      </div>
    </form>
    <div id="newResult" class="group" style="display:none"></div>`;

  $("designForm").addEventListener("submit", createDesigned);
  $("dAdd").addEventListener("click", () => addFieldRow());
  addFieldRow();
}

/**
 * Upload a file.
 *
 * <b>The coordinate system is asked for and not inferred</b>, and the note says so: a shapefile
 * carries a `.prj` and matching its WKT to a code by comparing strings is how a layer comes to
 * declare a system it is not in (ADR-024).
 */
function drawImportForm() {
  $("addItemBody").innerHTML = `
    <p class="hint">For data you already have. The schema is read from the file — a
      <b>zipped shapefile</b>, a <b>zipped File Geodatabase</b>, or a
      <b>GeoJSON FeatureCollection</b>. A geodatabase holds many feature classes, so it is read by a
      separate process and this screen reports what is in it rather than publishing straight away.</p>
    <form id="importForm" autocomplete="off">
      <div class="row">
        <label class="field">Name<input type="text" id="iName" placeholder="parks" required></label>
        <label class="field">Sharing<select id="iShare">
          <option value="private" selected>private</option>
          <option value="organization">organization</option>
          <option value="public">public</option>
        </select></label>
      </div>
      <div class="row">
        <label class="field">File<input id="iFile" type="file"
          accept=".zip,.json,.geojson,application/zip,application/geo+json" required></label>
        <label class="field">Coordinate system<input type="text" id="iSrid" inputmode="numeric"
          placeholder="4326"><span class="u"></span></label>
      </div>
      <p class="hint" id="iChosen" hidden></p>
      <p class="hint" id="iNote">Leave the coordinate system empty for GeoJSON, which is always
        WGS 84 longitude, latitude by its own specification. A shapefile carries a
        <code>.prj</code> and this server will not guess a code from it.</p>
    </form>
    <div id="newResult" class="group" style="display:none"></div>`;

  $("importForm").addEventListener("submit", createImported);

  if (!handedFile) return;

  // <b>A `DataTransfer` is the only way to fill a file input from script</b>, and it is a real one:
  // the form then validates, submits and clears exactly as it does when the picker was used.
  const carrier = new DataTransfer();
  carrier.items.add(handedFile);
  $("iFile").files = carrier.files;

  // <b>The name is offered, not imposed</b>, and it is cleaned rather than copied. The reference
  // publishes under the file's own name; the owner's note on that was *"aslında bir isim sorup o isimde
  // publish edebiliriz"* — which this field already does. What it did badly was the derivation:
  // `Project Information.gdb.zip` became `Project Information.gdb`, because only the archive extension
  // came off. A space and a dot in a service name are legal and awful.
  //
  // So: the archive extension, then the format's own — `.gdb`, `.gpkg` — then anything that is not a
  // letter, a digit or an underscore becomes one, and runs collapse. `Project Information.gdb.zip`
  // offers `Project_Information`. Predictable, and typed over in one gesture because it is selected.
  //
  // <b>`\p{L}` and not `A-Za-z`, which the first version had.</b> An ASCII class turns
  // `TR_ilçe sınırları (2024).zip` into `TR_il_e_s_n_rlar_2024` — it deletes Turkish, which is the
  // language of the data this server was built for. Postgres quotes an awkward identifier rather than
  // us sanitising it (`GeometryValidityTests` asserts exactly that), so there is nothing to protect
  // here and a name to preserve: `TR_ilçe_sınırları_2024`.
  $("iName").value = handedFile.name
    .replace(/\.(zip|json|geojson)$/i, "")
    .replace(/\.(gdb|gpkg)$/i, "")
    .replace(/[^\p{L}\p{N}_]+/gu, "_")
    .replace(/^_+|_+$/g, "");

  $("iName").select();

  // <b>The whole file name, because the native input elides the middle of it.</b> Their screen states
  // the file on its own line — *File: Environmental.gdb.zip* — and ours showed
  // `PointofInve…ation.gdb.zip` inside the control, which hides exactly the part that distinguishes
  // one export from another. Size beside it, so an empty or truncated upload is visible before it is
  // sent rather than after it is refused.
  const chosen = $("iChosen");

  if (chosen) {
    const kb = handedFile.size / 1024;

    chosen.hidden = false;
    chosen.innerHTML = `<b>${h(handedFile.name)}</b> <span class="val">${
      kb >= 1024 ? `${(kb / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(kb))} KB`
    } · the format is read from the bytes, not from the name</span>`;
  }

  handedFile = null;
}

/** Publish a table this server can reach. */
function drawRegisteredForm() {
  $("addItemBody").innerHTML = `
    <p class="hint">For data that already lives in a PostGIS database this server can reach.
      <b>Nothing is copied</b> — the layer reads the table where it is. The tables are read from
      the database when you pick a connection, not remembered from when it was registered, so a
      table that has been dropped or revoked does not appear. Only tables with a geometry column
      are offered, because the rest cannot be a feature layer.</p>
    <form id="regForm" autocomplete="off">
      <div class="row">
        <label class="field" style="flex:1 1 190px">Connection<select id="rSource">
          <option value="">choose a connection…</option>
        </select></label>
        <label class="field" style="flex:2 1 250px">Table<select id="rTable" disabled>
          <option value="">pick a connection first</option>
        </select></label>
      </div>
      <div id="rFacts" style="display:none"></div>
      <div class="row">
        <label class="field">Layer name<input type="text" id="rName" placeholder="parcels" required></label>
        <label class="field">Identity column
          <input type="text" id="rIdentity" placeholder="nominate one" required></label>
        <label class="field">Sharing<select id="rShare">
          <option value="private" selected>private</option>
          <option value="organization">organization</option>
          <option value="public">public</option>
        </select></label>
        <label class="field">Into service <span class="val">(optional)</span>
          <input type="text" id="rService" list="serviceNames" placeholder="a service of its own">
        </label>
      </div>
      <p class="hint">Which column identifies a feature for the life of the layer. It is your
        nomination, not something read from the table (Q-57): we will not synthesise one, because
        a row number is not stable and a side mapping table would drift on the owner's first
        edit.</p>
      <datalist id="serviceNames"></datalist>
      <p class="hint">Naming an existing service adds this layer to it at the next free index —
        that is how several related layers become one service. Leaving it empty gives the layer a
        single-layer service named after it.</p>
    </form>
    <div id="newResult" class="group" style="display:none"></div>`;

  $("regForm").addEventListener("submit", publishRegistered);
  $("rSource").addEventListener("change", loadRegisteredTables);
  $("rTable").addEventListener("change", showChosenTable);

  // Filled after the screen is on, so a request does not hold the form shut. The control says what
  // it is waiting for in its own first option.
  section("connections", fillConnectionChoices);
  section("service names", fillServiceChoices);
}

/**
 * Server's own action: an empty service, and the group layers inside one.
 *
 * <b>Not on the New item dialog, and that is the owner's rule applied.</b> They gave it for the group
 * screen — *"add member shall be inside members section"* — and then again here: *"grupun ve servisin
 * orada ilişkisi yok. servis katmanın bir özelliği."* A service is not an item you add; it is how a
 * layer is presented. So it keeps the surface whose subject it is, Server, and the item dialog is
 * about items.
 *
 * <b>And it stays a drawer.</b> Two short forms that are read against the service list behind them —
 * the group form's *Nest under* is a layer id you get from that list — which is the case the note
 * beside `#issued` gives for a panel rather than a modal.
 */
function openNewService() {
  itemStep = "item";
  $("addItemBody").innerHTML = "";
  if ($("addItem").open) $("addItem").close();

  // <b>The empty-service half came off on 2026-09-06 — ADR-057 §5h.</b> *Katmansız servis
  // yaratılamaz*, by owner decision, and the Publish screen next door is where a service is
  // made now: tables into a tree, the tree is the service, one request writes it. What this
  // drawer taught was the API's order — create a container, add groups, publish layers naming
  // a group by a numeric index a design review could find nowhere on this surface — and none
  // of that is a thing to know any more.
  //
  // <b>The group half stays, and it is a different job.</b> Adding a group to a service that
  // already exists is reorganising something published, not composing something new; the
  // Publish screen groups what it is building and has nothing to say about what is already
  // served.
  $("drawerTitle").textContent = "Group layers";
  $("drawerSub").textContent = "inside a service that is already published";
  $("drawerBody").innerHTML = `
    <div class="group">
      <p class="hint">To make a <b>new</b> service, use
        <a href="#/publish">Publish</a> — it composes the layers and the
        groups together and publishes them in one act. This is for a service that is already
        served and wants reorganising.</p>
    </div>

    <div class="group">
      <h3>A group layer inside one</h3>
      <p class="hint">A group layer nests layers within a service's own tree. It is not a sharing
        group and holds no data — the groups a service already has are listed below, and removable
        there, because the place a thing is made is the place to unmake it.</p>
      <form id="grpForm" autocomplete="off">
        <div class="row">
          <label class="field" style="flex:2 1 220px">Group inside
            <input type="text" id="gService" list="serviceNames" placeholder="hosted/EarlyAlert" required></label>
          <label class="field">Group name<input type="text" id="gName" placeholder="Reports" required></label>
          <label class="field">Nest under <span class="val">(layer id)</span>
            <input type="number" id="gParent" min="0" placeholder="top level"></label>
        </div>
        <datalist id="serviceNames"></datalist>
        <button type="submit">Create group layer</button>
        <div id="gExisting" class="val" style="margin-top:10px"></div>
      </form>
    </div>

    <div id="newResult" class="group" style="display:none"></div>`;

  $("grpForm").addEventListener("submit", createGroupLayer);
  $("gService").addEventListener("change", event => showServiceGroups(event.target.value));

  section("service names", fillServiceChoices);

  // Remembered before focus moves, because moving it is the next thing that happens. D-93.
  drawerOpenedFrom = document.activeElement instanceof HTMLElement
    ? document.activeElement
    : null;

  $("drawer").classList.add("on");
  $("drawer").setAttribute("aria-hidden", "false");
  $("drawer").inert = false;

  // <b>Focus goes in, which every other reveal in this console already does.</b> Opening it left
  // `document.activeElement` on the trigger, so the first Tab went to the folder rail's *+* button
  // elsewhere on the page rather than into the drawer. It is a panel and not a modal — deliberately,
  // see the note above — and a panel still places initial focus in itself. Design review 2026-08-19;
  // D-93 is the half of that finding this does not fix, which is that focus is not trapped and does
  // not need to be.
  $("gService")?.focus();
}

// ------------------------------------------- publishing what is already there

// The tables the last probe found, by their select value. Held because the
// publish request needs six fields the operator should not have to retype from a
// table they just chose — schema, table, geometry column, its type, the SRID and
// the object-id column.
let probed = new Map();

async function fillConnectionChoices() {
  const { dataSources = [] } = await api("/admin/datasources");
  const select = $("rSource");
  if (!select) return;

  select.innerHTML = `<option value="">choose a connection…</option>` +
    dataSources.map(s => `<option value="${h(s.id)}">${h(s.name)}</option>`).join("");

  if (!dataSources.length) {
    select.innerHTML = `<option value="">no connection is registered</option>`;
  }
}

/**
 * Service names for both the publish target and the group's parent.
 *
 * <b>From the administrative listing, not the services directory.</b> It used to read the
 * directory on the argument that the directory is the document saying what exists at which
 * path — true, and it also hides every stopped service, so the one thing you could not publish
 * into was a service somebody had stopped while working on it.
 */
async function fillServiceChoices() {
  const list = $("serviceNames");
  if (!list) return;

  const { services = [] } = await api("/admin/featureservices");
  const paths = [...new Set(services.map(v => v.qualified))].sort();
  list.innerHTML = paths.map(v => `<option value="${h(v)}"></option>`).join("");
}

/**
 * Splits a directory path into the folder and the bare service name.
 *
 * <b>The two endpoints want it two ways, which is why this exists.</b> Publishing
 * takes a bare `serviceName` and no folder — the folder is implicit in where
 * hosted layers go. Creating a group takes the bare name in the path and the
 * folder in the body. Offering one datalist of directory paths and splitting here
 * keeps the operator from having to know which is which.
 */
function splitService(text) {
  const value = (text || "").trim().replace(/^\/+|\/+$/g, "");
  const cut = value.lastIndexOf("/");
  return cut < 0
    ? { folder: null, name: value }
    : { folder: value.slice(0, cut), name: value.slice(cut + 1) };
}

async function loadRegisteredTables() {
  const id = $("rSource").value;
  const select = $("rTable");
  probed = new Map();
  $("rFacts").style.display = "none";
  $("rName").value = "";

  if (!id) {
    select.disabled = true;
    select.innerHTML = `<option value="">pick a connection first</option>`;
    return;
  }

  select.disabled = true;
  select.innerHTML = `<option value="">reading the database…</option>`;

  let capability;
  try {
    capability = await api(`/admin/datasources/${encodeURIComponent(id)}/capability`);
  } catch (e) {
    // The refusal belongs in the control, not only in a toast that will fade
    // while the operator is still looking at an empty list.
    select.innerHTML = `<option value="">could not read: ${h(e.message || e)}</option>`;
    toast(`connection: ${e.message || e}`);
    return;
  }

  const tables = capability.tables || [];

  if (!capability.canPublish) {
    select.innerHTML = `<option value="">${h(capability.message
      || "this connection cannot publish")}</option>`;
    return;
  }

  if (!tables.length) {
    select.innerHTML = `<option value="">no table with a geometry column is visible</option>`;
    return;
  }

  for (const t of tables) {
    probed.set(`${t.schemaName}.${t.tableName}`, t);
  }

  select.innerHTML = `<option value="">choose a table…</option>` +
    [...probed.keys()].sort().map(k => `<option value="${h(k)}">${h(k)}</option>`).join("");
  select.disabled = false;
}

function showChosenTable() {
  const t = probed.get($("rTable").value);
  const facts = $("rFacts");

  if (!t) {
    facts.style.display = "none";
    return;
  }

  // The layer name defaults to the table's, which is what an operator publishing
  // one table almost always wants, and is still editable.
  if (!$("rName").value.trim()) $("rName").value = t.tableName;

  facts.style.display = "";
  facts.innerHTML = `<dl class="facts">
      <dt>Geometry</dt><dd>${h(t.geometryType)} in <code>${h(t.geometryColumn)}</code></dd>
      <dt>SRID</dt><dd>${h(t.srid)}</dd>
      <dt>Object id</dt><dd>${t.objectIdColumn
        ? `<code>${h(t.objectIdColumn)}</code>`
        : `<span class="bad-inline">none — see below</span>`}</dd>
      <dt>Writable</dt><dd>${t.writable ? "yes" : "read only"}</dd>
    </dl>`
    + (t.objectIdColumn
      ? `<p class="hint">The probe found <code>${h(t.objectIdColumn)}</code> as an integer
         object-id column. <button type="button" class="ghost tiny" id="rUseOid">Nominate
         ${h(t.objectIdColumn)}</button> — or name a different column, if identity and the ArcGIS
         object id are not the same thing in this table.</p>`
      : `<p class="hint bad-inline">This table has no integer object-id column, so the layer will
         publish and will <b>not</b> be servable through the ArcGIS surface — ADR-013 §2a. It
         stays servable natively. The identity column is still required, and per Q-57 a table
         keyed by UUID or text is exactly the case that needs DDL before a client can read
         it.</p>`);

  // Wired here rather than once at open, because the paragraph holding it is
  // rebuilt for every table.
  const use = $("rUseOid");
  if (use) use.onclick = () => { $("rIdentity").value = t.objectIdColumn; };
}

async function publishRegistered(event) {
  event.preventDefault();

  const t = probed.get($("rTable").value);
  if (!t) { toast("Choose a table to publish."); return; }

  const into = splitService($("rService").value);

  const created = await api("/admin/layers", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: $("rName").value.trim(),
      dataSourceId: $("rSource").value,
      schemaName: t.schemaName,
      tableName: t.tableName,
      geometryColumn: t.geometryColumn,
      identityColumn: $("rIdentity").value.trim(),
      objectIdColumn: t.objectIdColumn,
      srid: t.srid,
      geometryType: t.geometryType,
      sharing: $("rShare").value,
      // Bare name: the publish endpoint takes no folder for the target service.
      serviceName: into.name || null,
    }),
  });

  reportCreated("Published", [
    `<b>${h(created.name)}</b> is layer ${created.layerId} of service
     <code>${h(created.service)}</code>, shared ${h(created.sharing)}.`,
    created.arcGisServable
      ? `<a href="/rest/services/${h(created.service)}/FeatureServer/${created.layerId}?f=json"
          target="_blank" rel="noreferrer">the layer document</a>`
      : `<span class="bad-inline">Not servable through the ArcGIS surface.</span>`,
    created.note ? `<span class="val">${h(created.note)}</span>` : "",
  ]);

  await section("layers", loadLayers, "layers");
  await section("service names", fillServiceChoices);
}


async function createGroupLayer(event) {
  event.preventDefault();

  const where = splitService($("gService").value);
  if (!where.name) { toast("Name the service the group goes in."); return; }

  const parent = $("gParent").value.trim();
  const button = event.submitter || $("grpForm")?.querySelector("[type=submit]");

  // <b>The refusal was invisible and the second click made a duplicate.</b> A nesting target
  // that is not a group answers 400 with a sentence naming why, and until 2026-09-06 that
  // sentence went nowhere: `api` throws on a refusal and nothing caught it. The same fault was
  // in the empty-service form beside this one, which has since come off the screen with
  // ADR-057 §5h.
  if (button) button.disabled = true;

  try {
    const created = await api(
      `/admin/services/${encodeURIComponent(where.name)}/groups`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: $("gName").value.trim(),
          folder: where.folder,
          parentLayerId: parent === "" ? null : Number(parent),
        }),
      });

    reportCreated("Group created", [
    `<b>${h(created.name)}</b> is layer ${created.layerId} of
     <code>${h($("gService").value.trim())}</code>${created.parentLayerId >= 0
       ? `, nested under layer ${created.parentLayerId}` : ", at the top level"}.`,
    `<span class="val">Publish a layer into this service and name ${created.layerId} as the
     parent to put it inside this group.</span>`,
  ]);

    // <b>Emptied, because two presses meant two groups with one name.</b> The service and the
    // parent stay: filling a service with several groups is one sitting, and retyping where
    // they go each time is the work this form is supposed to save.
    $("gName").value = "";

    // The list under the form is the record of what this service now holds, so it moves
    // with the thing it lists rather than on the next drawer opening.
    await section("groups", () => showServiceGroups($("gService").value));
    await section("services", loadServices, "services");
  } catch (e) {
    toast(e.message);
  } finally {
    if (button) button.disabled = false;
  }
}

function reportCreated(title, lines) {
  const box = $("newResult");
  box.style.display = "";
  box.innerHTML = `<h3>${h(title)}</h3>` + lines.filter(Boolean).join("<br>");
}

function addFieldRow(name = "", type = "Text") {
  const row = document.createElement("tr");
  row.innerHTML =
    `<td><input type="text" class="fName" value="${h(name)}" placeholder="inspector"></td>
     <td><select class="fType">${FIELD_TYPES.map(t =>
       `<option${t === type ? " selected" : ""}>${t}</option>`).join("")}</select></td>
     <td style="text-align:center"><input class="fReq" type="checkbox"></td>
     <td><button type="button" class="ghost tiny fDel">Remove</button></td>`;
  row.querySelector(".fDel").onclick = () => row.remove();
  $("dFields").append(row);
}

// Reports what the server said rather than "done". The import response carries
// the row count, the inferred field types and the reprojection note — all of
// which are things somebody uploading a file wants to check before trusting it.
function reportNew(created) {
  const fields = (created.fields || [])
    .map(f => `${h(f.name)} <span class="val">${h(f.type)}</span>`).join(", ");
  const box = $("newResult");
  box.style.display = "";
  box.innerHTML =
    `<h3>Created</h3>
     <b>${h(created.name)}</b> — ${num(created.rows)} row${created.rows === 1 ? "" : "s"},
     table <code>${h(created.table)}</code>.<br>
     ${fields ? `Fields: ${fields}.<br>` : ""}
     ${created.storedIn && created.storedIn.sourceSR !== created.storedIn.storedSR
        ? `<span class="val">${h(created.storedIn.note)}</span><br>` : ""}
     <a href="${h(created.services.feature)}?f=json" target="_blank" rel="noreferrer">feature service</a>
     · <a href="${h(created.services.tiles)}?f=json" target="_blank" rel="noreferrer">tile service</a>`;
}

/**
 * What the reading screen is showing: the job being watched, and its answer once it has one.
 *
 * <b>Held outside the drawing, because the screen is redrawn while the job is still going.</b> The
 * elapsed seconds tick and the status changes, and a screen that rebuilt itself from a closure would
 * lose the job the moment anything else redrew it.
 */
let inspecting = null;

/**
 * Follows a geodatabase inspection on a screen of its own.
 *
 * <b>A screen rather than a panel under the form, and the first version was the panel.</b> Measured on
 * the owner's archive: the result sat below the upload form inside a 680-pixel dialog, so two of the
 * five columns were unreachable and the footer still offered *Import and publish* — which, pressed,
 * would have uploaded the same archive again. A finished job is not an annotation on the form that
 * started it.
 *
 * <b>Polled at the interval the server claims work on.</b> Two seconds is the inspector's own idle
 * period, so asking faster only asks a database a question it cannot yet answer differently. ADR-011's
 * queue can have a notification channel; until then this says how long it has been waiting, because a
 * spinner with no elapsed time is indistinguishable from a stuck one.
 */
async function watchInspect(opened, asked) {
  inspecting = {
    opened,
    asked: asked || { name: "", sharing: "private" },
    since: Date.now(),
    status: "queued",
    job: null,
    error: null,

    // <b>Which feature classes the operator has ticked, held here for the same reason the job is.</b>
    // The screen is redrawn whenever anything changes — the select-all box, a service name that is
    // refused — and a selection kept in the DOM would be lost every time.
    picked: null,
  };

  itemStep = "inspect";
  drawAddItem();

  for (let attempt = 0; attempt < 300; attempt++) {
    await new Promise(done => setTimeout(done, 2000));

    // <b>Abandoned if the reader left this screen.</b> Closing the dialog or pressing Back does not
    // stop the job — it is the server's — but it must stop this loop writing into a screen that is now
    // showing something else.
    if (itemStep !== "inspect" || inspecting?.opened.job !== opened.job) return;

    try {
      inspecting.job = await api(`/admin/jobs/${encodeURIComponent(opened.job)}`);
      inspecting.status = inspecting.job.status;
    } catch (e) {
      inspecting.error = e.message || String(e);
      drawAddItem();
      return;
    }

    if (inspecting.status === "queued" || inspecting.status === "running") {
      // Only the elapsed line changes, so the table is not rebuilt sixty times while nothing happens.
      const age = $("inspectAge");
      const seconds = Math.round((Date.now() - inspecting.since) / 1000);
      if (age) age.textContent = `${inspecting.status} — ${seconds} second${seconds === 1 ? "" : "s"}`;
      continue;
    }

    drawAddItem();
    return;
  }

  inspecting.status = "lost";
  drawAddItem();
}

/** The reading screen, in whichever of its four states the job is in. */
function drawInspect() {
  const state = inspecting;

  $("addItemFoot").innerHTML = `
    <button type="button" class="ghost" id="itemBack">Back</button>
    <span class="fill"></span>
    <button type="button" class="ghost" id="itemCancel">Close</button>`;

  $("itemBack").addEventListener("click", () => {
    itemStep = "import";
    drawAddItem();
  });
  $("itemCancel").addEventListener("click", () => $("addItem").close());

  if (!state) {
    itemStep = "item";
    drawAddItem();
    return;
  }

  if (state.error) {
    $("addItemTitle").textContent = "Lost track of the job";
    $("addItemBody").innerHTML = `<p class="hint">${h(state.error)} The job itself is unaffected — it
      is the server's, and its record is at <code>${h(state.opened.watch)}</code>.</p>`;
    return;
  }

  if (state.status === "queued" || state.status === "running") {
    $("addItemTitle").textContent = "Reading the geodatabase";
    $("addItemBody").innerHTML = `
      <p class="hint">A File Geodatabase holds many feature classes, so it is read by a separate
        process rather than inside the upload. This takes as long as the archive is large, and closing
        this does not stop it — the job is the server's.</p>
      <p class="val" id="inspectAge">${h(state.status)} — just started</p>`;
    return;
  }

  if (state.status === "lost") {
    $("addItemTitle").textContent = "Still going after ten minutes";
    $("addItemBody").innerHTML = `<p class="hint">The reader's own deadline is two minutes, so a job
      still unfinished has not been picked up — or was claimed by something that stopped. Its record is
      at <code>${h(state.opened.watch)}</code> and it is safe to upload again.</p>`;
    return;
  }

  if (state.status !== "done") {
    $("addItemTitle").textContent = "Refused";
    $("addItemBody").innerHTML = `<p class="hint">${h(state.job.failure
      || "The job failed without a reason, which the job store is supposed to prevent.")}</p>`;
    return;
  }

  drawInspected(state.job);
}

/**
 * What a finished inspection found, and which of it to publish.
 *
 * <b>Every layer the driver reported, including the ones nobody would publish.</b> A geodatabase's
 * attachment tables have no geometry — one of the owner's archives holds six of them beside six
 * feature classes — and hiding them would leave a screen that quietly disagrees with what ArcGIS
 * shows. They are listed and dimmed instead, which is the rule the sharing scopes follow too: say why
 * something is not offered rather than shorten the list.
 *
 * <b>One service, N layers, which is the owner's rule and the whole point of this screen.</b>
 * *"servis ve katman ayrı şeyler. bir serviste n katman olabilir."* Every other route into hosted data
 * makes one service per layer; here the operator names one service and ticks what goes into it.
 * ADR-038.
 *
 * <b>Everything publishable is ticked on arrival.</b> Fifty-five checkboxes is not a decision anybody
 * wants to make one row at a time, and *all of it* is what somebody uploading a geodatabase almost
 * always means. Unticking three is quicker than ticking fifty-two, and the count on the button says
 * what will happen either way.
 */
function drawInspected(job) {
  let found;

  try {
    found = JSON.parse(job.detail || "{}");
  } catch {
    $("addItemTitle").textContent = "Read, and the answer is unreadable";
    $("addItemBody").innerHTML = `<p class="hint">The job finished and its detail is not JSON, which is
      a version mismatch between this server and its reader rather than anything about your data.</p>`;
    return;
  }

  const layers = found.layers || [];
  const publishable = layers.filter(canPublish);

  // First draw only: everything that can go, goes.
  if (inspecting.picked === null) {
    inspecting.picked = new Set(publishable.map(layer => layer.name));
  }

  const picked = inspecting.picked;

  $("addItemTitle").textContent = "Choose what to publish";

  $("addItemBody").innerHTML = `
    <p class="hint">The archive holds ${num(layers.length)} layer${layers.length === 1 ? "" : "s"},
      ${num(publishable.length)} of which can become a feature layer. The rest are attachment or
      relationship tables — they carry no geometry, so they are listed and cannot be ticked. A feature
      class with <b>no features</b> can be published: it becomes an empty layer with the fields the
      archive declares.</p>

    <label class="field">Service name
      <input id="gdbService" value="${h(inspecting.asked.name || "")}" maxlength="128"
             autocomplete="off" spellcheck="false">
      <span class="val">Every layer you tick becomes a layer inside this one service, at
        <code>/rest/services/hosted/<b id="gdbEcho">${h(inspecting.asked.name || "…")}</b
        >/FeatureServer</code>. It will be ${h(SHARE_WORDS[inspecting.asked.sharing]
          || "private to you")}, which is what you chose on the upload screen.</span></label>

    <p class="hint bad-inline" id="gdbRefused" hidden role="alert"></p>

    <div class="widetable">
      <table class="gdbpick">
        <thead><tr>
          <th class="tick"><input type="checkbox" id="gdbAll" aria-label="Every feature class"></th>
          <th>Feature class</th><th>Geometry</th><th>Features</th><th>Fields</th>
          <th>Coordinate system</th>
        </tr></thead>
        <tbody>${layers.map(layer => {
          const may = canPublish(layer);
          const name = layer.name || "";

          return `<tr${may ? "" : ' class="val"'}>
            <td class="tick">${may
              ? `<input type="checkbox" class="gdbPick" data-layer="${h(name)}"${
                  picked.has(name) ? " checked" : ""} aria-label="${h(name)}">`
              : (() => {
                  // <b>`aria-label` beside the `title`, because a title is a mouse.</b> A reader who
                  // never hovers had only the row's own cells — `none — a table` — to infer from,
                  // which is one inference more than the sentence costs. Design review 2026-08-19.
                  const why = "a table with no geometry cannot become a feature layer";

                  return `<span class="val" title="${h(why)}"
                    aria-label="${h(`Cannot be published: ${why}`)}">—</span>`;
                })()}</td>
            <td>${h(name)}</td>
            <td>${h(GEOMETRY_NAMES[layer.geometry] || layer.geometry || "none")}</td>
            <td>${layer.features === null || layer.features === undefined
                  ? '<span class="val">unknown</span>'
                  : layer.features === 0
                    ? '<span class="val" title="Publishing this creates the layer and its fields with no rows in it">none — schema only</span>'
                    : num(layer.features)}</td>
            <td>${num((layer.fields || []).length)}</td>
            <td>${layer.srid ? `EPSG:${h(String(layer.srid))}`
                  : '<span class="val">not identified</span>'}</td>
          </tr>`;
        }).join("")}</tbody>
      </table>
    </div>
    ${(found.messages || []).length
      ? `<p class="hint"><b>GDAL said:</b> ${h((found.messages || []).join(" "))}</p>` : ""}`;

  $("addItemFoot").innerHTML = `
    <button type="button" class="ghost" id="itemBack">Back</button>
    <span class="fill"></span>
    <button type="button" class="ghost" id="itemCancel">Cancel</button>
    <button type="button" class="primary" id="gdbPublish"></button>`;

  $("itemBack").addEventListener("click", () => {
    itemStep = "import";
    drawAddItem();
  });
  $("itemCancel").addEventListener("click", () => $("addItem").close());
  $("gdbPublish").addEventListener("click", () => publishGeodatabase(publishable));

  // <b>The address echoes what is typed, because a service name is a URL and that is not obvious.</b>
  // `Project Information` is a legal service name and an unpleasant address; seeing it appear inside
  // `/rest/services/hosted/…` is what makes the operator rename it before it exists rather than after.
  $("gdbService").addEventListener("input", () => {
    const echo = $("gdbEcho");
    if (echo) echo.textContent = $("gdbService").value.trim() || "…";
  });

  $("gdbAll").addEventListener("change", () => {
    const on = $("gdbAll").checked;

    picked.clear();
    if (on) for (const layer of publishable) picked.add(layer.name);

    for (const box of document.querySelectorAll(".gdbPick")) box.checked = on;

    countPicked(publishable);
  });

  for (const box of document.querySelectorAll(".gdbPick")) {
    box.addEventListener("change", () => {
      if (box.checked) picked.add(box.dataset.layer);
      else picked.delete(box.dataset.layer);

      countPicked(publishable);
    });
  }

  countPicked(publishable);
}

/**
 * Whether a layer the reader described can become a feature layer at all.
 *
 * <b>One reason it cannot: no geometry.</b> That is an attachment or relationship table — one of the
 * owner's archives holds six of them beside six feature classes — and there is nothing to create a
 * geometry column as.
 *
 * <b>An empty feature class *is* publishable again, and the round trip is why.</b> For half a day this
 * also refused `features === 0`, because every import path in this server built its columns by reading
 * rows and an empty layer left nothing to read. A geodatabase is not GeoJSON: the reader's header
 * carries the field list and the geometry type, which is exactly what an empty hosted layer needs, so
 * D-106 was closed by using it. The tick is offered because it now succeeds — a survey layer exported
 * before anybody filled it in is the ordinary case, and ArcGIS publishes those.
 */
function canPublish(layer) {
  return Boolean(layer.geometry) && layer.geometry !== "wkbNone";
}

/**
 * The words the scopes are called on the screen that chose them.
 *
 * <b>Not the enum's own names.</b> *Organization* is a value; *visible to everybody in your
 * organisation* is what it does — and this sentence is the last chance to notice that a public
 * service is about to be created.
 */
const SHARE_WORDS = {
  private: "private to you",
  organization: "visible to everyone in your organisation",
  public: "public — visible to anybody on the internet, signed in or not",
};

/**
 * Keeps the button and the select-all box honest about the ticks.
 *
 * <b>The count is on the button because that is where the decision is made.</b> A footer that says
 * *Publish* under a table of fifty-five rows does not tell you how many you are about to create, and
 * the number is the whole difference between the two most likely mistakes here — publishing one layer
 * by accident, and publishing all of them.
 */
function countPicked(publishable) {
  const picked = inspecting.picked;
  const button = $("gdbPublish");
  const all = $("gdbAll");

  if (all) {
    all.checked = publishable.length > 0 && picked.size === publishable.length;
    all.indeterminate = picked.size > 0 && picked.size < publishable.length;
  }

  if (!button) return;

  button.textContent = picked.size === 0
    ? "Publish"
    : `Publish ${num(picked.size)} layer${picked.size === 1 ? "" : "s"}`;

  // <b>Disabled rather than refusing on press.</b> Nothing ticked has one possible outcome and no
  // useful error, which is the case for a control that cannot be pressed — unlike an empty name, where
  // the refusal has something to say.
  button.disabled = picked.size === 0;
}

/**
 * Asks the server to publish the ticked feature classes into one service.
 *
 * <b>The name is checked here as well as on the server, and the two say the same thing.</b> The server
 * is what makes it true; this is what makes it quick, and it is the difference between a refusal
 * beside the field you typed and one at the top of a screen.
 */
async function publishGeodatabase(publishable) {
  const refused = $("gdbRefused");
  const service = $("gdbService").value.trim();

  const say = why => {
    refused.hidden = false;
    refused.textContent = why;
    $("gdbService").focus();
  };

  if (service.length === 0) {
    say("A service needs a name. Every layer you ticked goes inside it, and it becomes part of the "
      + "URL clients will use.");
    return;
  }

  if (/[/\\?#%]/.test(service)) {
    say(`'${service}' cannot be a service name: it becomes one segment of a URL, so it may not `
      + "contain / \\ ? # or %.");
    return;
  }

  refused.hidden = true;

  const button = $("gdbPublish");
  button.disabled = true;
  button.textContent = "Opening the job…";

  try {
    const answer = await api("/admin/hosted/geodatabase", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        archive: inspecting.opened.job,
        service,
        sharing: inspecting.asked.sharing,
        layers: [...inspecting.picked],
      }),
    });

    watchPublish(answer, service);
  } catch (e) {
    // <b>Back to the screen, with the reason.</b> Every refusal this endpoint gives is something the
    // operator can act on here — a name in use, a layer the inspection did not report, an archive that
    // has already been swept — so none of them is a reason to lose the selection.
    say(e.message || String(e));

    button.disabled = false;
    countPicked(publishable);
  }
}

/** The publish job being followed, held outside the drawing for the reason `inspecting` is. */
let publishing = null;

/**
 * Follows the publish, which is minutes rather than seconds.
 *
 * <b>Per layer, because the job reports per layer.</b> Twenty-three feature classes is twenty-three
 * chances to fail, and a screen that said only *failed* after nineteen had landed would be describing
 * something nobody can act on. The percentage moves as each one lands; the table at the end says which
 * ones did.
 */
async function watchPublish(opened, service) {
  publishing = {
    opened, service, since: Date.now(), status: "queued", job: null, error: null, done: false,
  };

  itemStep = "publish";
  drawAddItem();

  for (let attempt = 0; attempt < 600; attempt++) {
    await new Promise(done => setTimeout(done, 2000));

    if (itemStep !== "publish" || publishing?.opened.job !== opened.job) return;

    try {
      publishing.job = await api(`/admin/jobs/${encodeURIComponent(opened.job)}`);
      publishing.status = publishing.job.status;
    } catch (e) {
      publishing.error = e.message || String(e);
      drawAddItem();
      return;
    }

    if (publishing.status === "queued" || publishing.status === "running") {
      const age = $("publishAge");
      const seconds = Math.round((Date.now() - publishing.since) / 1000);

      if (age) {
        const percent = publishing.job.progress ?? 0;

        age.textContent = `${publishing.status}${percent > 0 ? ` — ${num(percent)}%` : ""} — ${
          seconds} second${seconds === 1 ? "" : "s"}`;
      }

      continue;
    }

    drawAddItem();

    // <b>The lists are reloaded whatever the outcome.</b> A partly failed publish still created a
    // service with the layers that landed, so a console still showing the old content list would be
    // wrong in the more confusing direction.
    await loadLayers();
    return;
  }

  publishing.status = "lost";
  drawAddItem();
}

/** The publishing screen, in whichever of its states the job is in. */
function drawPublish() {
  const state = publishing;

  if (!state) {
    itemStep = "item";
    drawAddItem();
    return;
  }

  $("addItemFoot").innerHTML = `
    <span class="fill"></span>
    <button type="button" class="ghost" id="itemCancel">Close</button>`;

  $("itemCancel").addEventListener("click", () => $("addItem").close());

  if (state.error) {
    $("addItemTitle").textContent = "Lost track of the job";
    $("addItemBody").innerHTML = `<p class="hint">${h(state.error)} The publish itself is unaffected —
      it is the server's, and its record is at <code>${h(state.opened.watch)}</code>.</p>`;
    return;
  }

  if (state.status === "queued" || state.status === "running") {
    $("addItemTitle").textContent = `Publishing into ${state.service}`;
    $("addItemBody").innerHTML = `
      <p class="hint">Each feature class is read out of the archive and written into the datastore as
        its own table, then published as a layer in <b>${h(state.service)}</b>. Closing this does not
        stop it — the job is the server's.</p>
      <p class="val" id="publishAge">${h(state.status)} — just started</p>`;
    return;
  }

  if (state.status === "lost") {
    $("addItemTitle").textContent = "Still going after twenty minutes";
    $("addItemBody").innerHTML = `<p class="hint">The reader's own deadline is ten minutes per layer, so
      a job still unfinished has either been claimed by something that stopped or is working through a
      very long list. Its record is at <code>${h(state.opened.watch)}</code>, and the service holds
      whatever landed.</p>`;
    return;
  }

  let report = null;

  try {
    report = JSON.parse(state.job.detail || "null");
  } catch {
    report = null;
  }

  const rows = report?.layers || [];
  const landed = rows.filter(row => row.published);

  // <b>Done and failed share this screen, because a partly failed publish is both.</b> Nineteen layers
  // in a service and four refused is not two outcomes to choose between; the table is the answer and
  // the heading only says which way it leaned.
  $("addItemTitle").textContent = state.status === "done"
    ? `${state.service} is published`
    : landed.length > 0
      ? `${state.service} is published, with ${num(rows.length - landed.length)} refused`
      : "Nothing was published";

  const address = `#/service/hosted/${encodeURIComponent(state.service)}`;

  $("addItemBody").innerHTML = `
    <p class="hint">${landed.length > 0
      ? `${num(landed.length)} of ${num(rows.length)} feature class${rows.length === 1 ? "" : "es"}
         became layers in <a href="${h(address)}" id="gdbOpen">${h(state.service)}</a>.`
      : h(state.job.failure || "The job failed without a reason, which the job store is supposed to "
          + "prevent.")}</p>

    ${rows.length > 0 ? `<div class="widetable">
      <table class="gdbreport">
        <thead><tr><th>Feature class</th><th>Features</th><th>Outcome</th></tr></thead>
        <tbody>${rows.map(row => `<tr>
          <td>${h(row.layer || "")}</td>
          <td>${row.published
            ? (row.rows === 0
                ? `<span class="val">schema only</span>`
                : num(row.rows ?? 0))
            : "—"}</td>
          <td${row.published ? ' class="val"' : ' class="bad-inline"'}>${row.published
            ? "published"
            : h(row.why || "refused")}</td>
        </tr>`).join("")}</tbody>
      </table>
    </div>` : ""}

    ${state.status !== "done" && landed.length > 0
      ? `<p class="hint"><b>What was refused is not retried by this screen.</b> The service exists with
          the layers that landed; upload the archive again and tick only the ones that were refused, or
          fix them at the source. Nothing is half-written — a layer either has its table and its
          catalogue row, or neither.</p>`
      : ""}`;

  const open = $("gdbOpen");

  if (open) {
    open.addEventListener("click", () => $("addItem").close());
  }
}

/**
 * The driver's geometry names, in the words the rest of this console uses.
 *
 * <b>Beside this, a formatting rule worth stating once:</b> the coordinate system's code is printed
 * without a thousands separator, because `num()` turned EPSG:2952 into *EPSG:2,952* — an identifier
 * formatted as a quantity. The feature count next to it does take one, and the difference is exactly
 * whether the number counts something.
 *
 * <b>A lookup rather than a string trim.</b> `wkbMultiPolygon` becomes *MultiPolygon* by cutting three
 * characters, which works until `wkbNone` becomes *None* and `wkbPoint25D` becomes something nobody
 * asked for. D-71 is the entry about a constant that became a lookup, and this is a lookup from the
 * start — a name that is missing falls through to the driver's own, which is worse to read and never
 * wrong.
 */
const GEOMETRY_NAMES = {
  wkbPoint: "Point",
  wkbMultiPoint: "MultiPoint",
  wkbLineString: "LineString",
  wkbMultiLineString: "MultiLineString",
  wkbPolygon: "Polygon",
  wkbMultiPolygon: "MultiPolygon",
  wkbNone: "none — a table",

  // <b>The Z variants, because a geodatabase is full of them.</b> The owner's archives report
  // `wkbMultiPolygon25D` — a polygon carrying an elevation — and the first version of this table did
  // not have it, so the screen printed the driver's own name. Which is the designed fallback and is
  // still worse to read than a name; the fix is a row here rather than trimming `wkb` and `25D` off
  // with string arithmetic, which is what D-71 records going wrong.
  wkbPoint25D: "Point Z",
  wkbMultiPoint25D: "MultiPoint Z",
  wkbLineString25D: "LineString Z",
  wkbMultiLineString25D: "MultiLineString Z",
  wkbPolygon25D: "Polygon Z",
  wkbMultiPolygon25D: "MultiPolygon Z",
};

async function createDesigned(event) {
  event.preventDefault();
  const fields = [...document.querySelectorAll("#dFields tr")].map(row => ({
    name: row.querySelector(".fName").value.trim(),
    type: row.querySelector(".fType").value,
    nullable: !row.querySelector(".fReq").checked,
  })).filter(f => f.name);

  try {
    reportNew(await api("/admin/hosted/define", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: $("dName").value.trim(),
        geometryType: $("dGeom").value,
        sharing: $("dShare").value,
        fields,
      }),
    }));
    $("dName").value = "";
    $("dFields").innerHTML = "";
    addFieldRow();
    await loadLayers();
  } catch (e) {
    // The server's message is the useful part — a name in use, a reserved
    // field, a type it does not know.
    const box = $("newResult");
    box.style.display = "";
    box.innerHTML = `<h3>Refused</h3>${h(e.message || String(e))}`;
  }
}

async function createImported(event) {
  event.preventDefault();
  const file = $("iFile").files[0];
  if (!file) return;

  const body = new FormData();
  body.append("name", $("iName").value.trim());
  body.append("sharing", $("iShare").value);
  body.append("file", file);

  // <b>Only when given.</b> The server requires it for a shapefile and refuses to infer it; sending
  // an empty string would be a value rather than an absence, and GeoJSON needs none.
  const srid = ($("iSrid")?.value || "").trim();
  if (srid) body.append("srid", srid);

  // No Content-Type header: the browser sets it with the multipart boundary,
  // and setting it by hand produces a body the server cannot parse.
  try {
    const answer = await api("/admin/hosted/import", { method: "POST", body });

    // <b>Two shapes come back from one address, and the difference is the format.</b> A shapefile or a
    // GeoJSON FeatureCollection is read inside the request and answers with the layer it made; a File
    // Geodatabase cannot be, because it holds many feature classes and is read by a separate process
    // minutes later — so it answers 202 with a job to watch. ADR-034 §5j, ADR-037.
    if (answer && answer.job) {
      // <b>The name and the scope are carried, because the screen that asked for them is about to be
      // replaced.</b> `drawInspect` rewrites the dialog's body, so `#iName` and `#iShare` stop existing
      // — and the selection screen after it needs both: the cleaned file name becomes the service name
      // it offers, and the scope the operator already chose is the scope the service is created with.
      watchInspect(answer, { name: $("iName").value.trim(), sharing: $("iShare").value });
      return;
    }

    reportNew(answer);
    $("iName").value = "";
    $("iFile").value = "";
    await loadLayers();
  } catch (e) {
    const box = $("newResult");
    box.style.display = "";
    box.innerHTML = `<h3>Refused</h3>${h(e.message || String(e))}`;
  }
}

// -------------------------------------------------------------------- actions

/**
 * Every click, and no branch of it may fail in silence.
 *
 * <b>The wrapper is the point.</b> This handler is `async`, so a rejection inside
 * any branch becomes an unhandled promise rejection: the browser logs it to a
 * console nobody has open and the page does nothing at all. Some branches had
 * their own `try`; the tiles branch did not, which is how a hanging map read as a
 * dead button. Guarding the whole handler rather than that one branch is
 * deliberate — D-46 is the register entry for fixing the instance and leaving the
 * class, and a new branch added later inherits this instead of remembering it.
 */
/*
  <b>A search box fires `input`, not `change`, and waiting for `change` means waiting for the
  reader to leave the field.</b> Debounced, because a log query is a database scan and one per
  keystroke would make a 60-row page cost sixty of them.
*/
let logTyping = null;

/*
  <b>A row that a keyboard can focus has to be a row a keyboard can open.</b> Space as well as
  Enter, because the row announces itself as a button and that is what a button does.
*/
document.addEventListener("keydown", event => {
  if (event.key !== "Enter" && event.key !== " ") {
    return;
  }

  const row = event.target.closest ? event.target.closest("tr.logrow") : null;

  if (!row || !row.nextElementSibling
      || !row.nextElementSibling.classList.contains("logdetail")) {
    return;
  }

  // Space scrolls a page by default, which is not what a focused control should do.
  event.preventDefault();

  const shown = row.nextElementSibling.hidden;

  row.nextElementSibling.hidden = !shown;
  row.setAttribute("aria-expanded", String(shown));
});

document.addEventListener("input", event => {
  if (event.target.id !== "logText" && event.target.id !== "logWho") {
    return;
  }

  clearTimeout(logTyping);
  logTyping = setTimeout(() => section("logs", loadLogs, "logRows"), 300);
});

document.addEventListener("click", async event => {
  try {
    await handleClick(event);
  } catch (e) {
    toast(e.message || String(e));
  }
});

async function handleClick(event) {
  const t = event.target;
  const d = t.dataset || {};

  /*
    <b>The Logs screen, delegated like everything else on this page.</b> Its rows are drawn
    and redrawn on every filter change and every page, so binding a listener per row would be
    binding hundreds and rebinding them each time.

    <b>Reading a log must not be able to change anything, and this is where that is
    enforced on the client side too.</b> Every branch here either re-reads or reveals; there
    is no action on this screen, which is why it has no row menu and no confirm dialogs.
  */
  if (d.logSource) {
    if (d.logSource !== logSource) {
      logSource = d.logSource;

      // <b>Cleared, because it means something else now.</b> An action carried on to the
      // request log is a filter no request can match, and the reader would be looking at an
      // empty table wondering which of their four filters did it.
      logOwn = "";
      await section("logs", loadLogs, "logRows");
    }

    return;
  }

  if (t.id === "logRefresh") {
    await section("logs", loadLogs, "logRows");
    return;
  }

  if (t.id === "logMore") {
    // <b>`true`, so the rows already read stay.</b> Paging that replaced the page would make
    // *Older* a navigation, and a reader following a thread down a log wants the thread.
    await section("logs", () => loadLogs(true), "logRows");
    return;
  }

  // <b>The whole row, not a chevron.</b> A row that reveals something should be clickable
  // where the eye already is; the detail row is the next sibling, which is what makes this
  // one line rather than a lookup.
  const row = t.closest ? t.closest("tr.logrow") : null;

  if (row && row.nextElementSibling
      && row.nextElementSibling.classList.contains("logdetail")) {
    const shown = row.nextElementSibling.hidden;

    row.nextElementSibling.hidden = !shown;
    row.setAttribute("aria-expanded", String(shown));
    return;
  }

  // Navigation is links now — the tab strip, the editor's left column, the
  // breadcrumb and Cancel — so it needs no branch here. The browser follows the
  // href, the hash changes, and route() paints. That is also what makes Back work.
  // <b>Studio's action opens the drawer; Server's is a link now.</b> The drawer covered a layer
  // and a service together while both were a form with a name in it. Composing a service is a
  // screen, so Server's action navigates, and only the group half of the drawer is left - reached
  // from the Services screen, where the services it reorganises are.
  if (t.id === "newLayer") { openAddItem(); return; }
  if (t.id === "newService") { openNewService(); return; }

  // <b>Server's action is a jump, not a form.</b> Composing a service is a screen with two trees
  // on it, and a drawer cannot hold that. It stays a button in the slot both surfaces share
  // rather than becoming a link there, because the slot is one line of markup for both and a
  // link that looks like Studio's button is worse than a button that navigates.
  if (t.id === "publishService") { location.hash = "#/publish"; return; }
  if (t.id === "addItemClose") { $("addItem").close(); return; }

  // <b>Making a folder is on the rail</b>, which is the only place a folder is the subject
  // rather than a field on something else (ADR-034 §5h).
  // <b>Refresh re-reads, and that is all it does.</b> It exists because a console showing a
  // catalogue somebody else can change needs a way to ask again without losing the screen you are
  // on — which a browser reload does.
  if (t.id === "serviceRefresh") {
    t.disabled = true;
    await section("folders", loadFolders);
    await section("services", loadServices, "services");
    t.disabled = false;
    return;
  }

  // <b>The sidebar narrows to its glyphs, and the choice is remembered.</b> `localStorage`, not
  // `sessionStorage`: this is a preference about the shape of the tool, not a fact about the
  // session, and somebody who wants the width back wants it back tomorrow too.
  if (t.id === "collapse" || t.closest?.("#collapse")) {
    const tight = $("shell").classList.toggle("tight");
    try { localStorage.setItem("gis-rail", tight ? "tight" : "wide"); } catch { /* private mode */ }

    // The label is hidden when narrow, so the tooltip is the only thing left that can say what
    // pressing this does — and it has to say the *next* state, like the arrow beside it.
    $("collapse").title = tight ? "Widen the sidebar" : "Narrow the sidebar";
    return;
  }

  if (t.id === "newFolder") {
    const name = prompt("A folder to publish into. It becomes part of the URL: "
      + "/rest/services/<folder>/<service>/FeatureServer");
    if (!name) return;
    try {
      const r = await api("/admin/folders", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name }),
      });
      toast(r.note, true);
      await section("folders", loadFolders);
    } catch (e) { toast(e.message); }
    return;
  }
  if (t.id === "drawerClose") { closeDrawer(); return; }

  // Cancel means discard, and it is the only exit that says so unambiguously — so it
  // is the only one that throws the typed values away. The link then navigates.
  if (t.id === "editCancel" && editing) {
    unsaved.delete(editing.name);
    return;
  }

  if (t.id === "basemapSet" || t.id === "basemapClear") {
    const url = t.id === "basemapClear" ? "" : $("basemapInput").value.trim();

    // A tile template has to carry all three, or every request asks for the same
    // tile and the map looks broken in a way that is hard to read.
    if (url && !["{level}", "{col}", "{row}"].every(token => url.includes(token))) {
      toast("A tile template needs {level}, {col} and {row} — those are what the map "
        + "substitutes per tile.");
      return;
    }

    if (url) localStorage.setItem(BASEMAP_KEY, url);
    else localStorage.removeItem(BASEMAP_KEY);

    if (view) await resetBasemap();
    else drawBasemapControl();
    return;
  }

  if (t.id === "opsRefresh") {
    t.disabled = true;
    await section("operations", loadOperations);
    await section("health", refreshHealth);
    t.disabled = false;
    return;
  }

  // Asked for rather than run on entry. It is three requests per layer against
  // our own server, and a report that quietly generates load every time somebody
  // clicks a tab is a report people learn to avoid.
  if (t.id === "anonRun") {
    t.disabled = true;
    await section("the anonymous view", loadAnonymous, "anonRows");
    t.disabled = false;
    return;
  }

  // <b>A click on a control inside a row is not a click on the row.</b> Both listings below
  // make the whole row clickable, and each of them carries buttons and selects that do
  // something else. This used to be an exception list per row — *unless it was Delete, unless
  // it was the sharing select* — and the owner found what that costs the day a third control
  // arrived: pressing **Stop** opened the service page instead of stopping it, because the
  // list had not been extended. Asking what was clicked cannot go stale the same way.
  // <b>`summary` and `details` are controls, and leaving them out made a menu unreachable.</b> A click
  // on the `⋯` summary inside a clickable row was treated as a click on the row, so the row's own
  // navigation fired before the browser could toggle the `details` — and the menu's contents were
  // unreachable by mouse on every such row. Found by the design review 2026-08-19 on Studio's content
  // rows; the same shape is on Server's services list, whose rows are `tr.pick` and do carry a menu.
  const control = t.closest("button, select, input, textarea, a, label, summary, details");

  // <b>A service row opens the service — unless there is nothing to choose inside it.</b>
  // ADR-034 §5h made the service the unit on this screen, and the drill-in is what shows what
  // it holds. For a service with one layer and no groups that page is a single-row table whose
  // only control is a *Settings* link, which the owner put plainly: *"this is a really
  // meaningless page tbh. we shall go to settings directly."* So a one-member service goes
  // straight to its layer, and the drill-in stays for the services where the list is a real
  // choice. `data-only` carries the member's name, from the same `cover` the preview uses.
  const service = t.closest("tr[data-service]");
  if (service && !control) {
    location.hash = `#/service/${encodeURIComponent(service.dataset.service)}`;
    return;
  }

  // A content row in Studio: the layer's own page, which is where its appearance and its
  // sharing are.
  const pick = t.closest("tr[data-pick]");
  if (pick && !control) {
    location.hash = `#/layer/${encodeURIComponent(pick.dataset.pick)}`;
    return;
  }

  if (d.tiles) {
    if (shown.has(tileKey(d.tiles))) { hide(tileKey(d.tiles)); await loadLayers(); return; }

    toVisualization(d.tiles, "tiles");
    return;
  }

  if (d.tilesLegacy) {
    const drawn = !shown.has(tileKey(d.tilesLegacy));
    if (drawn) await showTiles(d.tilesLegacy);
    else hide(tileKey(d.tilesLegacy));
    await loadLayers();
    if (drawn) toMap();
    return;
  }

  if (d.show) {
    // <b>Navigation, not a map.</b> §5k: Visualization absorbed this screen, so the control that used to
    // draw here goes there. Hiding still happens in place, because a reader looking at a map wants the
    // layer off it rather than a page change.
    if (shown.has(d.show)) { hide(d.show); await loadLayers(); return; }

    if (!$("serviceVis")) { toast("Nowhere to draw this."); return; }

    toVisualization(d.show, "features");
    return;
  }

  if (d.showLegacy) {
    t.disabled = true;
    try {
      // The whole document, because the SDK will read it anyway and this console now
      // takes its colour from the server's `drawingInfo` rather than choosing one.
      // <b>`layerUrl`, because the old line used the layer's name as its service's.</b>
      // `serviceRoot(layerNamed(name))` builds `/rest/services/{folder}/{layer}` and then appended
      // `/FeatureServer/0` — which is right only when a layer happens to be named after its service and
      // sits at index 0, and every single-layer import is exactly that, which is why this survived.
      // For `look_EarlyAlert_sites` it asked for a service called `look_EarlyAlert_sites` and got a 404
      // that the catch below reported as *no layer is visible to you. It may not exist, or it may not be
      // shared with you* — a sharing refusal for a layer that is shared and exists. Found by the design
      // review 2026-08-19 on the one multi-layer service in the demo data; it would have failed on every
      // one.
      //
      // `layerUrl` resolves the layer through `placeOf`, which carries the service **and the index**.
      const doc = await api(`${layerUrl(d.show).replace(location.origin, "")}?f=json`);
      await show(d.show, doc);
      await loadLayers();
      toMap();
      return;
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.toggle) {
    t.disabled = true;
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.toggle)}/${d.to}`, { method: "POST" });
      if (r.to === "stopped") hide(d.toggle);
      toast(`${d.toggle}: ${r.from} → ${r.to}. ${r.note}`, true);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.cache) {
    // <b>D-159: an empty box is "nobody has said", not zero.</b> `Number("")` is 0
    // and the endpoint takes 0 as the real answer *never serve a cached tile*, so
    // pressing Set on a layer whose box had not been filled in turned caching off and
    // said so in a toast that reads like confirmation. The box was empty because this
    // listing did not carry `cacheSeconds` — that is fixed too, and this line is the
    // half that has to hold even when a value fails to load.
    const typed = $("ttl").value.trim();
    const seconds = d.clear || typed === "" ? null : Number(typed);

    if (seconds !== null && !Number.isFinite(seconds)) {
      toast(`"${typed}" is not a number of seconds.`);
      return;
    }

    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.cache)}/cache`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seconds }),
      });
      toast(r.note, true);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.time) {
    const field = d.clear ? null : $("timeField").value.trim();
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.time)}/time-field`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ field: field || null }),
      });

      // The server's note says whether the declaration holds against the columns the
      // layer actually has, which is the half a publisher cannot see from here.
      toast(r.note, r.declarationHolds);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.refresh) {
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.refresh)}/refresh`,
        { method: "POST" });
      // The server's own note, which says what was forgotten and how many tiles
      // went with it. Writing our own sentence here would drop the tile count.
      toast(`${d.refresh}: ${r.note}`, true);
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.delete) {
    // A confirmation, because this is the one action here that destroys
    // something. The name has to be right: a mis-click on the wrong row of a
    // table of similar names is exactly how the wrong layer goes.
    if (!confirm(`Delete "${d.delete}"? This removes the publication`
        + `${layerNamed(d.delete).hosted ? " and its hosted table" : ""}.`)) return;
    try {
      await api(`/admin/layers/${encodeURIComponent(d.delete)}`, { method: "DELETE" });
      hide(d.delete);
      selected = null;
      editing = null;                    // there is no longer a layer to have open
      location.hash = "#/services";
      toast(`${d.delete} deleted.`, true);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  if (d.style) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.style)}/style`);
      // <b>Two different bodies from one endpoint, and putting the wrong one in
      // the box would be a trap.</b> With a style stored, the response *is* the
      // document, byte for byte. With none stored it is a wrapper saying so —
      // and pasting that wrapper back as a style is what would happen if this
      // filled the box either way.
      // <b>On the page, not in a toast.</b> Whether a service carries an override decides
      // what the tile face draws, and a fact that load-bearing cannot live for seven seconds
      // in a corner. The per-layer editor writes its equivalent into permanent text; this is
      // the same fact and it gets the same treatment.
      if (r && r.stored === false) {
        $("styleDoc").value = "";
        styleState(false, r.note);
      } else {
        $("styleDoc").value = JSON.stringify(r, null, 1);
        styleState(true, null);
      }
    } catch (e) { toast(e.message); }
    return;
  }

  if (t.id === "emptyRead") {
    t.disabled = true;
    await section("the empty services", loadEmptyServices, "emptyRows");
    t.disabled = false;
    return;
  }

  if (t.id === "emptySweep") {
    const count = document.querySelectorAll("#emptyRows tr .name").length;

    if (!confirm(`Remove ${count} empty service${count === 1 ? "" : "s"}? Nothing is `
      + `unpublished — a service holding a layer or a group is never swept — but a `
      + `container somebody made on purpose looks the same as one a publish left behind.`)) {
      return;
    }

    t.disabled = true;

    try {
      const r = await api("/admin/featureservices/sweep", { method: "POST" });

      // <b>Named in the toast, because the risk of this button is which.</b> A message
      // saying *4 removed* leaves an operator unable to answer what they just did.
      toast(r.count === 0 ? r.note : `Removed: ${r.removed.join(", ")}.`, true);

      await section("the empty services", loadEmptyServices, "emptyRows");
      await loadLayers();
    } catch (e) { toast(e.message); }

    t.disabled = false;
    return;
  }

  if (d.memberRemove) {
    /*
      <b>Answered before it is asked, D-101.</b> Both of these are refused by the server, and
      both were refused only after the operator had chosen a disposition from a panel that
      could not lead anywhere. The listing carries the roles and `whoami` carries the name, so
      the console can say which of the two it is instead of opening a dialog whose every answer
      is *no*.

      <b>The self case is the one an operator actually meets.</b> A server with one
      administrator is the ordinary state of a fresh install, and the only person who can reach
      the Remove button on that administrator is that administrator.
    */
    if (d.memberRemove === signedInAs) {
      toast(`You cannot remove yourself: the session doing the work would be revoked halfway `
        + `through it. Ask another administrator.`);
      return;
    }

    if (administrators.length === 1 && administrators[0] === d.memberRemove) {
      toast(`${d.memberRemove} is the only administrator who can still sign in, so they cannot `
        + `be removed. Make another administrator first.`);
      return;
    }

    t.disabled = true;

    try {
      const held = await api(`/admin/members/${encodeURIComponent(d.memberRemove)}/holdings`);

      // <b>Nothing owned: one question, and it is the ordinary one.</b> Asking about a
      // disposition for content that does not exist is a dialog nobody can answer.
      if (!held.owns) {
        if (confirm(`Remove ${d.memberRemove}? They own nothing, so nothing goes with them.`)) {
          const r = await api(`/admin/members/${encodeURIComponent(d.memberRemove)}`,
            { method: "DELETE" });

          toast(r.note, true);
          await section("members", loadMembers, "members");
        }
      } else {
        showRemoveMember(d.memberRemove, held);
      }
    } catch (e) { toast(e.message); }

    t.disabled = false;
    return;
  }

  if (t.id === "removeTransfer" || t.id === "removeDelete") {
    const name = $("removeWho").dataset.member;
    const transfer = t.id === "removeTransfer";
    const to = $("removeTo").value;

    if (transfer && !to) {
      toast("Choose who receives the content.");
      return;
    }

    if (!transfer && !confirm(`Delete ${name} and everything they own? The layers are `
      + `unpublished and the services and folders go with them. This cannot be undone.`)) {
      return;
    }

    t.disabled = true;

    try {
      const query = transfer
        ? `?transferTo=${encodeURIComponent(to)}`
        : "?deleteOwned=true";

      const r = await api(`/admin/members/${encodeURIComponent(name)}${query}`,
        { method: "DELETE" });

      $("removeMember").style.display = "none";
      toast(r.note, true);
      await section("members", loadMembers, "members");
      await loadLayers();
    } catch (e) { toast(e.message); }

    t.disabled = false;
    return;
  }

  if (t.id === "removeCancel") {
    $("removeMember").style.display = "none";
    return;
  }

  if (d.symbology) {
    await section("the symbology", () => loadSymbology(d.symbology), "symState");
    return;
  }

  if (d.symbologyPut) {
    const body = $("symDoc").value.trim();

    if (!body) {
      toast("Paste a MapLibre style or an Esri drawingInfo first.");
      return;
    }

    t.disabled = true;

    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.symbologyPut)}/symbology`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body,
      });

      // <b>A Store is newer than any read still in flight.</b> Opening this screen is five
      // round trips and the last lands well after it looks ready; without this, the tail of
      // that read clears the losses this Store is about to draw and puts the state line back
      // to *Generated*. Same counter, same rule as the picture's.
      symLoadingFor++;

      // <b>The losses go on the page, and the count goes in the toast.</b> A toast is
      // read once and dismissed; a conversion that lost four things needs to still be
      // saying so when the operator looks again.
      $("symDoc").value = JSON.stringify(r.symbology, null, 1);
      $("symDerived").textContent = JSON.stringify(r.drawingInfo, null, 1);
      drawLosses(r.losses);

      $("symState").innerHTML = `Stored from your ${h(r.from)}, ${num(r.bytes)} bytes.`;

      // A refusal that has been answered is not a refusal any more.
      symRefuse("");

      // <b>The editor stands in front of the generated screen once something is stored.</b>
      // Storing from the empty state is how that screen is left for good.
      symShowEmpty(false);

      // <b>Everything that claims to know whether this is stored is corrected here.</b> Two of
      // them were not, and went on saying *nothing is stored yet* under a document that had just
      // been stored — the classify line and the preview caption, each written once by whatever
      // function happened to produce it and never revisited by the one that made it wrong.
      symStored = true;
      symEditedSince = false;

      symSayState();

      if ($("symClassifySays")) {
        $("symClassifySays").textContent = symClassCount === 0
          ? ""
          : `${num(symClassCount)} class${symClassCount === 1 ? "" : "es"} from `
            + `${symClassField}.`;
      }

      toast(
        r.losses.length === 0
          ? `${r.name}: stored, and nothing was lost.`
          : `${r.name}: stored. ${r.losses.length} thing${r.losses.length === 1 ? "" : "s"} `
            + `the ArcGIS face cannot carry — listed below the editor.`,
        r.losses.length === 0);
    } catch (e) {
      // <b>On the page as well as in a toast, which is this file's own rule about facts that
      // matter.</b> The comment eight lines above says a toast is read once and dismissed and
      // that a conversion's losses need to still be saying so afterwards — and a Store that
      // FAILED got only the toast. The owner pressed Store, the toast went, and the page looked
      // exactly as it had: *save desem de işlem yapmıyor*. It says so now, and keeps saying it.
      $("symState").innerHTML =
        `<b>Not stored.</b> ${h(e.message || String(e))}`;

      // <b>And under the title strip, because the state line is in a tab now.</b> The rule this
      // is keeping is the one the comment above states: a Store that failed must still be
      // saying so after the toast has gone. A tab the reader is not on cannot do that.
      symRefuse(e.message || String(e));

      toast(e.message || String(e));
    }

    t.disabled = false;
    return;
  }

  if (d.symbologyDel) {
    if (!confirm(`Clear ${d.symbologyDel}'s symbology? It goes back to the generated `
      + `appearance, which is a colour derived from its name.`)) return;

    t.disabled = true;

    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.symbologyDel)}/symbology`,
        { method: "DELETE" });

      $("symDoc").value = "";
      await loadSymbology(d.symbologyDel);
      toast(r.note, true);
    } catch (e) { toast(e.message); }

    t.disabled = false;
    return;
  }

  if (d.stylePut) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.stylePut)}/style`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: $("styleDoc").value,
      });
      styleState(true, null);
      toast(`${r.name}: ${r.replaced ? "style replaced" : "style stored"}, ${num(r.bytes)} bytes.`, true);
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.styleDel) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.styleDel)}/style`,
        { method: "DELETE" });
      $("styleDoc").value = "";
      styleState(false, r.note);
      toast(r.note || "Back to the composition.", true);
    } catch (e) { toast(e.message); }
    return;
  }

  // <b>Empty only, and the button is already disabled otherwise</b> — this is the
  // second guard rather than the first, because a disabled button is a hint and the
  // server's refusal is the rule.
  // <b>Start and stop on the list itself</b>, which is where their reference puts it and where
  // ours needed it: until 2026-08-17 stopping a service did nothing at all (D-57), and the only
  // button for it was two screens away from the status it changes.
  if (d.serviceStatus) {
    t.disabled = true;
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.serviceStatus)}/${d.to}`,
        { method: "POST" });
      if (r.to === "stopped") hide(d.serviceStatus);
      toast(`${d.serviceStatus}: ${r.from} → ${r.to}. ${r.note}`, true);
    } catch (e) { toast(e.message); }
    await loadLayers();   // which redraws the service list, since the status it shows moved
    return;
  }

  // A service with no layers is started and stopped through its own route, because it is its
  // own row in its own table — see SetSystemStatusAsync for why that is not the layer route.
  if (t.id === "issuedDone") {
    $("issued").close();
    await section("members", loadMembers, "members");
    return;
  }

  // A service's settings page: a screen state, so it is not an address — the service already is one.
  if (d.serviceSave) {
    t.disabled = true;
    try {
      await saveServiceSettings(d.serviceSave, d.folder || null);
    } catch (e) { toast(e.message); }
    t.disabled = false;
    return;
  }

  // <b>Which page to open when arriving from somewhere else.</b> A service's pages are not
  // separately addressable — the hash carries the service and `folder/name` already uses the
  // separator a third segment would need — so a link that wants a particular page says so here and
  // the router honours it on the next render. Recorded rather than worked around: layer pages *are*
  // addressable (`#/layer/x/sharing`, ADR-034 §5c) and service pages are not, which is an
  // inconsistency and not a decision.
  // ---------------------------------------------------------------- groups (ADR-036)
  // <b>A row goes to the group's page, and the `scrollIntoView` this used to need is gone with the
  // panel it scrolled to.</b> `tr.pick.on` now marks the group you last visited rather than the one
  // that is open, which is the one useful thing left for it to mean: coming back from a page, the row
  // you came from is where you left it.
  const groupRow = t.closest?.("tr[data-group]");

  if (groupRow) {
    location.hash = `#/group/${encodeURIComponent(groupRow.dataset.group)}`;
    return;
  }

  // <b>A form, not two chained prompts — and the prompts cost more than looks.</b> A design review
  // on 2026-08-18 counted what they lost: the name was sent as the title as well and the description
  // was never sent at all, so **two of a group's four fields could not be filled from this console
  // and there is no endpoint to set them afterwards**. The capability was free text against a
  // case-sensitive enum, and a refusal threw away both prompts' input because there was no form to
  // return to. And a browser that has offered *"prevent this page from creating additional
  // dialogs"* makes New group silently do nothing for the rest of the session.
  if (t.id === "groupNew") {
    $("groupNewForm").innerHTML = `
      <div class="picker">
        <label for="newGroupName">Name</label>
        <input id="newGroupName" type="text" placeholder="planning" autocomplete="off">

        <label for="newGroupTitle">Title <span class="val">shown in the list</span></label>
        <input id="newGroupTitle" type="text" placeholder="Planning team" autocomplete="off">

        <label for="newGroupWhy">What it is for</label>
        <input id="newGroupWhy" type="text" autocomplete="off">

        <label for="newGroupUpdate">What members may do with the services shared into it</label>
        <select id="newGroupUpdate">
          <option value="none">none — read them</option>
          <option value="ownItems">ownItems — edit the ones they shared themselves</option>
          <option value="allItems">allItems — edit every service shared with the group</option>
        </select>

        <p class="hint"><b>This one cannot be changed afterwards.</b> Widening it would make every
          service already shared with the group editable by every member, retroactively — so the
          server has no way to change it and neither does this screen (ADR-036 §4c). To change it,
          make another group and move the shares.</p>

        <div class="row">
          <button class="primary" id="newGroupSave">Create group</button>
          <button class="ghost" id="groupPickCancel">Cancel</button>
        </div>
      </div>`;

    $("groupNewForm").hidden = false;
    $("newGroupName")?.focus();
    return;
  }

  if (t.id === "newGroupSave") {
    const name = ($("newGroupName")?.value || "").trim();

    if (!name) { toast("A group needs a name."); $("newGroupName")?.focus(); return; }

    try {
      await api("/admin/groups", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name,
          // <b>Sent only when it differs.</b> A title equal to the name made every row print its
          // own name twice — once bold, once in grey underneath — because the renderer shows both
          // when they are not the same.
          title: ($("newGroupTitle")?.value || "").trim() || null,
          description: ($("newGroupWhy")?.value || "").trim() || null,
          itemUpdate: $("newGroupUpdate")?.value || "none",
        }),
      });
      groupOpen = name;
      toast(`${name}: created. You own it.`, true);
      $("groupNewForm").hidden = true;

      // <b>Straight to its page.</b> The next act after making a group is adding members, and those
      // verbs live there — staying on the list left the new row highlighted under nothing, which was a
      // leftover of the panel this replaced.
      location.hash = `#/group/${encodeURIComponent(name)}/overview`;
      return;
    } catch (e) { toast(e.message); }

    await section("groups", loadGroups, "groupRows");
    return;
  }

  // <b>A picker, not a prompt.</b> Owner: *"add member asks for name. why not search a user and add
  // user from the list"*. Asking somebody to type a member's name from memory is asking them to
  // guess, and the failure is silent — a typo is a 404 about a member who does exist.
  //
  // <b>The candidates come from an endpoint written for this.</b> Reading the member directory needs
  // `admin:manageMembers`, so a publisher who owns a group could not fill a list; widening that
  // privilege to fill a dropdown would have been the wrong repair, so
  // `GET /admin/groups/{name}/candidates` returns names only, to somebody who already manages the
  // group.
  if (t.id === "groupAdd") {
    if (!groupOpen) return;

    const answer = await api(
      `/admin/groups/${encodeURIComponent(groupOpen)}/candidates`) || {};

    const names = answer.candidates || [];

    if (names.length === 0) {
      toast("Every member is already in this group.");
      return;
    }

    $("groupPicker").innerHTML = `
      <div class="picker">
        <label for="groupPickFilter">Add a member</label>
        <input id="groupPickFilter" type="search" placeholder="Filter&hellip;" autocomplete="off">
        <select id="groupPickWho" size="8" aria-label="Members who could join">${names.map(n =>
          `<option value="${h(n)}">${h(n)}</option>`).join("")}</select>
        <div class="row">
          <label><input type="checkbox" id="groupPickManager"> as a manager</label>
          <button class="primary" id="groupPickAdd">Add</button>
          <button class="ghost" id="groupPickCancel">Cancel</button>
        </div>
        <p class="hint">A <b>manager</b> may add members and share services, and may not delete the
          group — ADR-036 §3, which is the difference between delegating work and delegating
          control.</p>
      </div>`;

    $("groupPicker").hidden = false;
    $("groupPickFilter")?.focus();
    return;
  }

  // ---------------------------------------------------------------- the add page
  const railPick = t.closest?.("[data-add-folder]");

  if (railPick) {
    addFolder = railPick.dataset.addFolder === "" && railPick.textContent.includes("All your")
      ? null
      : railPick.dataset.addFolder;

    resetPage("addRows");
    if (groupNow) await drawGroupAdd(groupNow);
    paintPreviews();
    return;
  }

  if (t.id === "addCommit") {
    // <b>One call per service, sequentially, and every outcome reported.</b> `PUT .../items/{s}` is
    // per item, so eight selected is eight requests; firing them together would make a partial write
    // report *done* over a mixture of successes and refusals, which is worse than having no bulk
    // control at all. Sequential also makes the failure legible: the row that refused is named.
    const wanted = [...addPicked];

    t.disabled = true;

    const added = [];
    const refused = [];

    for (const qualified of wanted) {
      const cut = qualified.lastIndexOf("/");
      const folder = cut < 0 ? "" : qualified.slice(0, cut);
      const bare = cut < 0 ? qualified : qualified.slice(cut + 1);

      try {
        await api(
          `/admin/groups/${encodeURIComponent(groupOpen)}/items/${encodeURIComponent(bare)}`
          + `?folder=${encodeURIComponent(folder)}`,
          { method: "PUT" });

        added.push(qualified);
        addPicked.delete(qualified);
      } catch (e) {
        refused.push(`${bare} (${e.message || e})`);
      }
    }

    // <b>The refused stay ticked, so retry is one press.</b> And the confirmation counts what will
    // reach nobody, because a share that reports success and reaches no member is the two-step trap
    // this screen exists not to hide.
    const inert = added.filter(name =>
      (addOffered.find(i => i.name === name) || {}).sharing !== "group").length;

    toast(
      [
        added.length > 0
          ? `${num(added.length)} added${inert > 0
              ? `, ${num(inert)} of them reaching nobody yet — Overview sets the scope` : ""}.`
          : "",
        refused.length > 0 ? `Refused: ${refused.join(", ")}` : "",
      ].filter(Boolean).join(" "),
      refused.length === 0);

    t.disabled = false;

    if (refused.length === 0) {
      location.hash = `#/group/${encodeURIComponent(groupOpen)}/content`;
      return;
    }

    await refreshGroup();
    return;
  }

  if (t.id === "groupLeave") {
    if (!groupNow) return;

    // <b>Confirmed, because it is not undoable by the person doing it.</b> Rejoining needs an
    // owner or a manager, so a mis-click costs somebody else's attention rather than a click.
    if (!confirm(
      `Leave '${groupNow.name}'? You stop reading what is shared with it, and only its owner or `
      + `a manager can add you back.`)) {
      return;
    }

    t.disabled = true;

    try {
      const answer = await api(
        `/admin/groups/${encodeURIComponent(groupNow.name)}/membership`, { method: "DELETE" });

      toast(answer?.note || "You left the group.", true);
      location.hash = "#/groups";
    } catch (failure) {
      t.disabled = false;
      toast(failure.message);
    }

    return;
  }

  if (t.id === "gsSave") {
    await saveGroupSettings({
      title: $("gsTitle").value.trim() || null,
      summary: $("gsSummary").value.trim() || null,
      description: $("gsDescription").value.trim() || null,
      visibility: $("gsVisibility").value,
      joinPolicy: $("gsJoin").value,
      contribute: $("gsContribute").value,
      memberList: $("gsMemberList").value,
      membersMayLeave: $("gsLeave").value === "yes",
    }, "Saved.");
    return;
  }

  // <b>The remedy for an inert share, on the tab that names it.</b> A service reaches a group's
  // members only when its own scope is `group`; that is a setting on the service, and until
  // 2026-08-18 this console could not set it at all — `SCOPES` had three values and the server took
  // four, so the one instruction the group page gives was unfollowable. The verb is here rather than
  // only on the service's page because the operator is looking at the list of what is broken.
  const reach = t.closest?.("[data-group-reach]");

  if (reach) {
    const qualified = reach.dataset.groupReach;
    const cut = qualified.lastIndexOf("/");
    const folder = cut < 0 ? "" : qualified.slice(0, cut);
    const bare = cut < 0 ? qualified : qualified.slice(cut + 1);

    reach.disabled = true;

    try {
      await api(
        `/admin/services/${encodeURIComponent(bare)}/sharing`
        + `?folder=${encodeURIComponent(folder)}`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sharing: "group" }),
        });

      toast(`${bare} now reaches this group's members.`, true);
    } catch (e) { toast(e.message); reach.disabled = false; return; }

    await refreshGroup();
    return;
  }

  if (t.id === "groupPickCancel") {
    // Whichever is open: the create form has its own slot outside the editor, and the member and
    // service pickers are inside it, because those two *are* operations on the open group.
    $("groupPicker").hidden = true;
    $("groupNewForm").hidden = true;
    return;
  }

  if (t.id === "groupPickAdd") {
    const who = $("groupPickWho")?.value;
    if (!who || !groupOpen) return;

    try {
      await api(
        `/admin/groups/${encodeURIComponent(groupOpen)}/members/${encodeURIComponent(who)}`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ manager: !!$("groupPickManager")?.checked }),
        });
      toast(`${who} joined ${groupOpen}`, true);
    } catch (e) { toast(e.message); }

    $("groupPicker").hidden = true;
    await refreshGroup();
    return;
  }

  // <b>`groupShare` has no handler, and that absence is the point.</b> It is an `<a>` to
  // `#/group/{name}/add` now, so the router draws the page and the browser does the navigating.
  //
  // <b>The inline picker it used to open is deleted rather than left dormant.</b> Both were firing:
  // the anchor navigated to the new page *and* this handler rendered the old `<select>` of qualified
  // names above it — two pickers for one job, and the one on top was exactly what the owner rejected.
  // A dead branch that still runs is worse than a dead branch, and worse than either is a screen that
  // offers the same act twice by two different rules.
  //
  // What was here: a `<select size="8">` filled from `/content/layers`, and a `Share` button. What
  // replaced it is `drawGroupAdd` — thumbnails, a folder rail, multi-select, and a row that says
  // whether the share will actually reach anybody. ADR-034 §5z.

  // <b>Leave group, which we had no way to do at all.</b> Taken from the reference's group page,
  // where *"You are a member"* sits above it. A member could be removed by a manager and could not
  // walk out, which makes joining a group something done *to* somebody.
  //
  // <b>The owner cannot leave their own group</b> — the store refuses it, because they would keep
  // owning a group that a membership-filtered list omits. The button is absent for them rather than
  // present and refusing.
  if (t.id === "groupLeave" && groupOpen) {
    if (!confirm(
      `Leave '${groupOpen}'? You will stop seeing the services shared with it.`)) return;

    try {
      await api(
        `/admin/groups/${encodeURIComponent(groupOpen)}/members/${encodeURIComponent(signedInAs)}`,
        { method: "DELETE" });
      toast(`You left ${groupOpen}`, true);
      groupOpen = null;
    } catch (e) { toast(e.message); }

    await refreshGroup();
    return;
  }

  const grade = t.closest?.("[data-group-grade]");

  if (grade && groupOpen) {
    try {
      await api(
        `/admin/groups/${encodeURIComponent(groupOpen)}`
        + `/members/${encodeURIComponent(grade.dataset.groupGrade)}`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ manager: grade.dataset.to === "manager" }),
        });
      toast(`${grade.dataset.groupGrade} is now a ${grade.dataset.to}`, true);
    } catch (e) { toast(e.message); }

    await refreshGroup();
    return;
  }

  const drop = t.closest?.("[data-group-drop]");

  if (drop && groupOpen) {
    // <b>Asked, because Leave asks and this is the same act done to somebody else.</b> A manager
    // sees Remove on their own row too, so without this they can drop themselves out of a group they
    // manage with one unconfirmed click while the labelled exit beside it asks twice.
    if (!confirm(
      `Remove ${drop.dataset.groupDrop} from '${groupOpen}'? They will stop seeing the services `
      + "shared with it.")) return;

    try {
      await api(
        `/admin/groups/${encodeURIComponent(groupOpen)}`
        + `/members/${encodeURIComponent(drop.dataset.groupDrop)}`,
        { method: "DELETE" });
      // <b>*Removed*, not *left*.</b> The screen separates Leave — the member's own act — from
      // Remove, which a manager performs on somebody else, and the toast collapsed them into the
      // member's voice. A manager reading *"X left"* about their own click learns the wrong thing.
      toast(`${drop.dataset.groupDrop} removed from ${groupOpen}`, true);
    } catch (e) { toast(e.message); }

    await refreshGroup();
    return;
  }

  const unshare = t.closest?.("[data-group-unshare]");

  if (unshare && groupOpen) {
    const cut = unshare.dataset.groupUnshare.split("/");
    const bare = cut.pop();
    const folder = cut.join("/");

    try {
      await api(
        `/admin/groups/${encodeURIComponent(groupOpen)}/items/${encodeURIComponent(bare)}`
        + `?folder=${encodeURIComponent(folder)}`,
        { method: "DELETE" });
      toast(`${bare} is no longer shared with ${groupOpen}`, true);
    } catch (e) { toast(e.message); }

    await refreshGroup();
    return;
  }

  if (t.id === "groupDelete" && groupOpen) {
    if (!confirm(
      `Delete the group '${groupOpen}'? Its members and its shares go with it. The services `
      + "themselves keep existing and are read under their own sharing scope.")) return;

    try {
      await api(`/admin/groups/${encodeURIComponent(groupOpen)}`, { method: "DELETE" });
      toast(`${groupOpen}: removed`, true);
      groupOpen = null;
    } catch (e) { toast(e.message); }

    location.hash = "#/groups";
    await section("groups", loadGroups, "groupRows");
    return;
  }

  // ---------------------------------------------------------------- roles (ADR-035)
  // <b>`closest`, because a click lands on a cell and the row carries the name.</b> This read
  // `t.dataset.role` and `t` is the `<td>`, so choosing a role did nothing at all and the editor
  // stayed on whichever role happened to be first — reported by the owner, who could not select
  // anything and therefore could not save anything either. Every other row handler in this file
  // already used `closest`; this one was written without looking at them.
  const roleRow = t.closest?.("tr[data-role]");

  if (roleRow && !t.closest?.("[data-role-delete]")) {
    roleOpen = roleRow.dataset.role;
    await section("roles", loadRoles, "roleRows");
    $("roleEditor")?.scrollIntoView({ block: "nearest", behavior: "smooth" });
    return;
  }

  const roleDelete = t.closest?.("[data-role-delete]");

  if (roleDelete) {
    const name = roleDelete.dataset.roleDelete;

    // <b>Asked, because it cannot be undone and the count is the thing at stake.</b> The server
    // refuses while members hold it, so the only deletable role is one nobody has — which makes
    // this a cheap confirmation rather than a warning about consequences.
    if (!confirm(`Delete the role '${name}'? Nobody holds it, so nothing loses a privilege.`)) return;

    try {
      await api(`/admin/roles/${encodeURIComponent(name)}`, { method: "DELETE" });
      toast(`${name}: removed`, true);
      if (roleOpen === name) roleOpen = null;
    } catch (e) { toast(e.message); }

    await section("roles", loadRoles, "roleRows");
    return;
  }

  const roleAll = t.closest?.("[data-role-all]");

  if (roleAll) {
    // <b>Enable all, or disable all if everything in the section is already on.</b> One control
    // rather than two, because a section is either fully on or not and the label says which act it
    // is about to perform.
    const wantAdmin = roleAll.dataset.roleAll === "admin";
    const boxes = [...document.querySelectorAll("#rolePrivileges .rolesection")]
      [wantAdmin ? 1 : 0]?.querySelectorAll("input[data-privilege]:not([disabled])") ?? [];

    const allOn = [...boxes].every(b => b.checked);
    for (const box of boxes) box.checked = !allOn;

    // <b>Enable-all in one section can leave a prerequisite in the other unticked.</b>
    // `content:registerDataStore` is administrative and needs `content:create`, which is general —
    // so enabling all of Administrative alone produces a set the server refuses. Running the
    // dependency pass over every box afterwards fixes it in front of the operator.
    if (!allOn) {
      for (const box of boxes) followRoleDependencies(box);
    }

    recountRoleSections();
    return;
  }

  if (t.id === "roleSave") {
    const name = roleOpen;
    if (!name) return;

    const wanted = [...document.querySelectorAll("#rolePrivileges input[data-privilege]")]
      .filter(b => b.checked).map(b => b.dataset.privilege);

    // <b>The riskier act had less friction than the harmless one.</b> *Delete role* asks — and the
    // server only ever permits it on a role nobody holds — while *Save privileges* could hand every
    // `admin:*` capability to a role live accounts are using, and committed on one press with a toast
    // afterwards. Design review 2026-08-19.
    //
    // <b>Only when the administrative set changes, and only when somebody holds the role.</b> A
    // confirmation on every save is a confirmation nobody reads; this one fires on the case that
    // cannot be undone by noticing.
    const wasAdmin = new Set((roleAdministrative.get(name) || []));
    const nowAdmin = wanted.filter(privilege => roleIsAdministrative.has(privilege));

    const gained = nowAdmin.filter(privilege => !wasAdmin.has(privilege));
    const lost = [...wasAdmin].filter(privilege => !nowAdmin.includes(privilege));
    const holders = roleHolders.get(name) || 0;

    if (holders > 0 && (gained.length || lost.length)) {
      const said = [
        gained.length ? `grants ${gained.join(", ")}` : "",
        lost.length ? `takes away ${lost.join(", ")}` : "",
      ].filter(Boolean).join(" and ");

      if (!confirm(
        `Save '${name}'? This ${said}, for ${holders} member`
        + `${holders === 1 ? "" : "s"} who hold${holders === 1 ? "s" : ""} it — at once, and for `
        + "sessions already signed in.")) {
        return;
      }
    }

    try {
      const saved = await api(`/admin/roles/${encodeURIComponent(name)}/privileges`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ privileges: wanted }),
      });
      toast(saved.note ? `${name}: saved. ${saved.note}` : `${name}: saved`, true);
    } catch (e) { toast(e.message); }

    await section("roles", loadRoles, "roleRows");
    return;
  }

  if (t.id === "roleNew") {
    const name = prompt("Name for the new role (lower case, no spaces):");
    if (!name) return;

    try {
      // <b>Created empty, and the editor is where privileges are chosen.</b> A creation dialog with
      // eighteen ticks in it would be a second copy of the editor, and `set from existing role`
      // exists precisely so an empty start is a step rather than a burden.
      await api("/admin/roles", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: name.trim(), description: "", privileges: [] }),
      });
      roleOpen = name.trim();
      toast(`${name.trim()}: created, granting nothing yet`, true);
    } catch (e) { toast(e.message); }

    await section("roles", loadRoles, "roleRows");
    return;
  }

  if (t.dataset?.openServicePage) {
    SERVICE_PAGE_OPEN = t.dataset.openServicePage;
    // No preventDefault: the href is a real address and navigating to it is what draws the service.
    return;
  }

  if (t.dataset?.serviceTab !== undefined) {
    event.preventDefault();
    showServiceTab(t.dataset.serviceTab);
    return;
  }

  if (t.id === "shareClose" || t.id === "shareCancel") {
    $("share").close();
    sharing = null;
    return;
  }

  if (t.id === "shareEditGroups") {
    sharing.step = "groups";
    drawShare();
    return;
  }

  if (t.id === "shareBack") {
    sharing.step = "scope";
    drawShare();

    // <b>Focus follows, as it does when the dialog opens.</b> Pressing Back left `activeElement` on the
    // body: the trap held, so nothing escaped, and a screen-reader user was told nothing about having
    // moved and the next Tab skipped the whole scope list. Design review 2026-08-19.
    $("shareTitle")?.focus({ preventScroll: true });
    return;
  }

  if (t.id === "shareSave") { await saveShare(); return; }

  const unshared = t.closest?.("[data-unshare]");

  if (unshared) {
    sharing.wanted.delete(unshared.dataset.unshare);
    drawShareGroupList();
    return;
  }

  if (d.unshare) {
    sharing.wanted.delete(d.unshare);
    drawShareGroupList();
    return;
  }

  // <b>`closest`, because a real mouse click never lands on the button.</b> The pill inside it fills its
  // box exactly, so `elementFromPoint` at the button's own centre returns the `span` — and reading
  // `event.target.dataset.share` found nothing and swallowed the click. It worked from the keyboard,
  // where the browser dispatches with the button as the target, which is how it passed: my own test
  // called `.click()` on the button element directly and set the target itself. A test that presses a
  // control the way nobody presses it proves nothing. Design review 2026-08-19.
  const shared = t.closest?.("[data-share]");

  if (shared) { await openShare(shared.dataset.share); return; }

  if (t.dataset?.visMode !== undefined) {
    event.preventDefault();
    visMode = t.dataset.visMode;
    drawServiceVis();
    return;
  }

  // <b>The layer picker is a strip of links now, so it is pressed rather than changed.</b> It
  // was a `select` with its own `onchange`; a segmented control has no change event, and a
  // handler left on the element it no longer is would be the kind of dead control this console
  // has met three times.
  if (t.dataset?.visLayer !== undefined) {
    event.preventDefault();
    visLayerIndex = t.dataset.visLayer;
    drawServiceVis();
    return;
  }

  if (t.dataset?.dataView !== undefined) {
    event.preventDefault();
    dataView = t.dataset.dataView;
    drawServiceData();
    return;
  }

  if (t.id === "svcUrlCopy") {
    const field = $("svcUrl");
    if (!field) return;

    // <b>`select()` before the write, and the field stays selected after it.</b> Clipboard access can
    // be refused — an insecure origin, a browser policy — and a selected field is a working fallback
    // rather than a dead button: the reader presses the copy key themselves. `#issued` does the same
    // for the same reason.
    field.select();

    try {
      await navigator.clipboard.writeText(field.value);
      toast("The service's address is on the clipboard.", true);
    } catch {
      toast("This browser would not let the page write to the clipboard. The address is selected — "
        + "copy it with the keyboard.");
    }

    return;
  }

  if (t.id === "svcDelete") {
    if (!serviceOpen) return;

    const count = serviceLayers.length;

    // <b>The confirmation names the tables, because that is the irreversible part.</b> *Are you sure*
    // in front of a drop has not said anything; *drops 55 tables* has.
    const hosted = (serviceOpen.folder || "") === "hosted";

    if (!confirm(
      `Delete '${serviceOpen.qualified}'`
      + (count ? ` and its ${count} layer${count === 1 ? "" : "s"}` : "")
      + "? "
      + (count === 0
        ? "It holds no layers, so no data goes with it."
        : hosted
          ? `This drops ${count} table${count === 1 ? "" : "s"} from the datastore and cannot be undone.`
          : "These are registered layers, so their tables are not touched — only the registration "
            + "goes."))) {
      return;
    }

    try {
      const answer = await api(
        `/admin/featureservices/${encodeURIComponent(serviceOpen.name)}`
        + `?folder=${encodeURIComponent(serviceOpen.folder || "")}&drop=true`,
        { method: "DELETE" });

      toast(answer.note || `${serviceOpen.qualified}: deleted`, true);
      location.hash = "#/services";
    } catch (e) { toast(e.message); }

    return;
  }

  if (t.dataset?.servicePage) {
    event.preventDefault();
    SERVICE_PAGE_OPEN = t.dataset.servicePage;

    // <b>From `serviceOpen`, not from the breadcrumb.</b> See its own note: reading the folder back out
    // of rendered text lost it for every foldered service and turned a tab switch into a silent
    // capability wipe.
    if (serviceOpen) drawServiceSettings(serviceOpen.name, serviceOpen.folder);
    return;
  }

  if (t.id === "memberNew") {
    $("memberForm").hidden = false;
    $("mName").focus();
    return;
  }

  if (t.id === "mCancel") {
    $("memberForm").hidden = true;
    for (const id of ["mName", "mDisplay"]) $(id).value = "";
    return;
  }

  if (t.id === "mSave") {
    t.disabled = true;
    try {
      const made = await api("/admin/members", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: $("mName").value,
          displayName: $("mDisplay").value || null,
          role: $("mRole").value,
          userType: $("mType").value,
        }),
      });
      $("memberForm").hidden = true;
      for (const id of ["mName", "mDisplay"]) $(id).value = "";
      showIssuedPassword(made);
    } catch (e) { toast(e.message); }
    t.disabled = false;
    await section("members", loadMembers, "members");
    return;
  }

  if (d.memberState) {
    t.disabled = true;
    try {
      const r = await api(`/admin/members/${encodeURIComponent(d.memberState)}/${d.to}`,
        { method: "POST" });
      toast(`${r.name}: ${r.note}`, true);
    } catch (e) { toast(e.message); }
    await section("members", loadMembers, "members");
    return;
  }

  if (d.memberPassword) {
    // <b>A confirmation, not a prompt for a value</b> — there is no value to ask for, because the
    // server picks it. The first version prompted for one and its own message admitted the
    // consequence, *"you will know it afterwards"*, which is a hazard described rather than removed.
    if (!confirm(`Issue a new password for ${d.memberPassword}? The server picks it, you see it `
      + `once, and they must replace it on first use. Their current password stops working.`)) {
      return;
    }
    try {
      const r = await api(`/admin/members/${encodeURIComponent(d.memberPassword)}/password`,
        { method: "PUT" });
      showIssuedPassword(r);
    } catch (e) { toast(e.message); }
    return;
  }

  if (t.id === "limSave" || t.id === "limClear") {
    const clearing = t.id === "limClear";
    const name = ($("serviceCrumb").querySelector("b")?.textContent || "").trim();
    const number = id => {
      const raw = ($(id)?.value ?? "").trim();
      return raw === "" ? null : Number(raw);
    };

    t.disabled = true;
    try {
      const r = await api(`/admin/services/${encodeURIComponent(name)}/limits`
        + `?folder=${encodeURIComponent((serviceOpen && serviceOpen.folder) || "")}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(clearing
          ? { deadlineSeconds: null, preflightPairs: null, waitSeconds: null, idleSeconds: null }
          : {
              deadlineSeconds: number("limDeadline"),
              preflightPairs: number("limPreflight"),
              waitSeconds: number("limWait"),
              idleSeconds: number("limIdle"),
            }),
      });
      toast(r.note, true);
    } catch (e) { toast(e.message); }
    t.disabled = false;
    await section("limits", () => loadServiceLimits(name, serviceOpen && serviceOpen.folder));
    return;
  }

  if (d.systemStatus) {
    t.disabled = true;
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.systemStatus)}/${d.to}`
        + `?folder=${encodeURIComponent(d.systemFolder || "")}`,
        { method: "POST" });
      toast(`${d.systemStatus}: ${r.from} → ${r.to}. ${r.note}`, true);
    } catch (e) { toast(e.message); }
    await section("services", loadServices, "services");
    return;
  }

  if (d.serviceDelete) {
    if (!confirm(`Delete the service "${d.serviceDelete}"? It holds no layers, so nothing `
        + `published goes with it.`)) return;
    try {
      const r = await api(`/admin/featureservices/${encodeURIComponent(d.serviceDelete)}`
        + `?folder=${encodeURIComponent(d.folder || "")}`, { method: "DELETE" });
      toast(`${d.serviceDelete}: ${r.note}`, true);
    } catch (e) { toast(e.message); }
    await section("services", loadServices, "services");
    return;
  }

  if (d.groupDelete) {
    const [service, index] = d.groupDelete.split("#");
    if (!confirm(`Delete group layer ${index} ("${d.groupName}") from ${service}?`)) return;
    try {
      const { folder, name } = splitService(service);
      const r = await api(`/admin/services/${encodeURIComponent(name)}/groups/${index}`
        + `?folder=${encodeURIComponent(folder || "")}`, { method: "DELETE" });
      toast(r.note, true);
    } catch (e) { toast(e.message); }
    await section("groups", () => showServiceGroups($("gService").value));
    await section("services", loadServices, "services");
    return;
  }

  if (d.sourceEdit) {
    await openDbConnection({
      id: d.sourceEdit,
      name: d.sourceName,
      summary: d.sourceSummary,
      layers: Number(d.sourceLayers) || 0,
    });

    return;
  }

  if (d.sourceRemove) {
    await removeSource(d.sourceRemove, d.sourceName, Number(d.sourceLayers) || 0);
    return;
  }

  if (d.probe) {
    t.disabled = true;
    try { renderProbe(d.probeName, await api(`/admin/datasources/${d.probe}/capability`)); }
    catch (e) { toast(e.message); }
    t.disabled = false;
    return;
  }

  // ---------------------------------------------------------------- publish screen
  if (t.id === "pubClear") {
    for (const layer of pubLayers()) {
      const db = pubDatabases.find(d => d.id === layer.source);
      const table = db?.schemas
        ?.find(x => x.name === layer.schema)?.tables
        ?.find(x => x.tableName === layer.table);

      if (table) table.used = false;
    }

    pubTree = [];
    pubPicked = new Set();
    pubDraw();
    return;
  }

  if (t.id === "pubOpen") { openPublishDialog(); return; }

  const killed = t.closest?.("[data-pubkill]");

  if (killed) { pubRemove(killed.dataset.pubkill); return; }

  const openDb = t.closest?.("[data-pubdb]");

  if (openDb) {
    const db = pubDatabases.find(d => d.id === openDb.dataset.pubdb);

    if (db) {
      db.open = !db.open;
      pubDraw();
      if (db.open) await pubProbe(db);
    }

    return;
  }

  const openSchema = t.closest?.("[data-pubschema]");

  if (openSchema) {
    const [id, name] = openSchema.dataset.pubschema.split(":");
    const schema = pubDatabases.find(d => d.id === id)?.schemas?.find(x => x.name === name);

    if (schema) { schema.open = !schema.open; pubDraw(); }

    return;
  }

  const picked = t.closest?.("[data-pubtable]");

  if (picked) {
    const [db, schema, table] = picked.dataset.pubtable.split("|");
    pubAdd(db, schema, table);
    return;
  }

  const composed = t.closest?.("[data-pubnode]");

  if (composed && t.closest("#pubTree")) {
    pubPick(composed.dataset.pubnode, event);
    pubDraw();
    return;
  }

  if (t.id === "sAdd") {
    event.preventDefault();
    await openDbConnection(null);
  }
}

document.addEventListener("change", async event => {
  // <b>Every filter on the Logs screen re-reads, and none of them has a Search button.</b>
  // A search button on a form whose every control is a filter is a second thing to click
  // for something the change already decided.
  if (["logText", "logWho", "logSince", "logFailed", "logOwnValue"].includes(event.target.id)) {
    // Kept in module state so that redrawing the control does not discard it.
    if (event.target.id === "logOwnValue") {
      logOwn = event.target.value;
    }

    await section("logs", loadLogs, "logRows");
    return;
  }

  const d = event.target.dataset || {};

  // <b>The lock writes immediately, and it is the one control on that tab that does.</b> A safety
  // switch left unsaved is believed-on and off, which is the failure the switch exists to prevent. It
  // still goes through the overlay, so turning it on does not erase the description.
  // The add page's ticks, and its select-all. Both redraw from the group already in hand.
  const tick = event.target.closest?.("[data-add]");

  if (tick) {
    if (tick.checked) addPicked.add(tick.dataset.add);
    else addPicked.delete(tick.dataset.add);

    if (groupNow) await drawGroupAdd(groupNow);
    paintPreviews();
    return;
  }

  if (event.target.id === "addAll") {
    // <b>The filtered set, which is what its label says.</b> Not the page — a select-all that means
    // the page while the label says a number larger than the page is the ambiguity the number was
    // added to remove.
    const already = new Set((groupNow?.items || []).map(i => i.name));
    const needle = ($("addFilter")?.value || "").trim().toLowerCase();

    const matching = addOffered.filter(i =>
      !already.has(i.name)
      && (addFolder === null || (i.folder || "") === addFolder)
      && (!needle || [i.name, i.kind, i.description]
        .some(v => (v || "").toLowerCase().includes(needle))));

    for (const i of matching) {
      if (event.target.checked) addPicked.add(i.name);
      else addPicked.delete(i.name);
    }

    if (groupNow) await drawGroupAdd(groupNow);
    paintPreviews();
    return;
  }

  if (event.target.id === "gsLock") {
    await saveGroupSettings(
      { deleteLocked: event.target.checked },
      event.target.checked
        ? "Locked. Nobody can delete this group, including an administrator."
        : "Unlocked. It can be deleted again.");
    return;
  }

  if (event.target.id === "groupItemSort") {
    resetPage("groupItems");
    if (groupNow) drawGroupContent(groupNow);
    return;
  }

  // A tick and a chosen option are `change` rather than `input`, so the note lives
  // here too: a capability unticked and then left behind is exactly the edit worth
  // keeping.
  noteEdit(event.target);

  // <b>A service's scope, applied on choosing it.</b> The `data-service-share` name is already
  // taken by the Server list's system-service select, so this carries the folder too and goes to
  // the same endpoint — which since 2026-08-18 accepts an ordinary service as well as a system one.
  // <b>Set from existing role: copies the ticks and does not save.</b> Applying it immediately
  // would make *look at what publisher has* into *become publisher*, and the whole point is to then
  // narrow it.

  if (event.target.id === "roleFromPick") {
    const from = event.target.value;
    if (!from) return;

    const answer = await api("/admin/roles") || {};
    const source = (answer.roles || []).find(r => r.name === from);
    if (!source) return;

    const held = new Set(source.privileges);

    for (const box of document.querySelectorAll("#rolePrivileges input[data-privilege]")) {
      if (!box.disabled) box.checked = held.has(box.dataset.privilege);
    }

    recountRoleSections();
    event.target.value = "";
    toast(`Copied ${from}'s ${held.size} privilege(s). Narrow them, then Save.`, true);
    return;
  }

  // A tick moves two counters and the compatibility line; recomputing is cheaper than tracking.
  if (event.target.dataset?.privilege) {
    followRoleDependencies(event.target);
    recountRoleSections();
    return;
  }

  if (d.serviceSharing) {
    try {
      const at = event.target.dataset.folder || "";
      const r = await api(
        `/admin/services/${encodeURIComponent(d.serviceSharing)}/sharing`
        + `?folder=${encodeURIComponent(at)}`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sharing: event.target.value }),
        });
      toast(`${d.serviceSharing}: shared ${r.from} → ${r.to}`, true);
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.share) {
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.share)}/sharing`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sharing: event.target.value }),
      });
      toast(`${d.share}: shared ${r.from} → ${r.to}`, true);
    } catch (e) { toast(e.message); }
    await loadLayers();
    return;
  }

  // <b>A role change is a select, so it belongs here and not in the click handler.</b> Applied the
  // moment it is chosen, like sharing and for the same reason (ADR-031 §2b): an administrator
  // revoking somebody's ability to publish has to be able to trust that it happened, rather than
  // press Save afterwards.
  if (event.target?.name === "shareScope") {
    for (const row of $("shareBody").querySelectorAll(".pickrow")) {
      row.classList.toggle("on", row.dataset.scope === event.target.value);
    }
    return;
  }

  if (event.target?.dataset?.shareGroup) {
    if (event.target.checked) sharing.wanted.add(event.target.dataset.shareGroup);
    else sharing.wanted.delete(event.target.dataset.shareGroup);

    // Only the count, so a tick does not rebuild the table under the reader's finger.
    const said = $("shareCount");
    if (said) said.textContent = `Selected: ${num(sharing.wanted.size)}`;
    return;
  }


  if (event.target?.id === "svcLock") {
    drawServiceDelete();
    return;
  }

  if (d.memberType) {
    // <b>Confirmed when it takes something away, and instant when it does not.</b> Same rule as
    // the role beside it: raising a ceiling grants nothing by itself — the roles still decide —
    // and lowering one withdraws privileges from every role the member holds at once, which is
    // the half worth stopping to read. The ladder is the server's, so the console asks it rather
    // than ranking the names itself.
    const becoming = event.target.value;
    const order = memberUserTypes;
    const from = order.indexOf(event.target.dataset.wasType || "");
    const to = order.indexOf(becoming);

    if (from >= 0 && to >= 0 && to < from) {
      if (!confirm(
        `Lower '${d.memberType}' from '${event.target.dataset.wasType}' to '${becoming}'? `
        + "A user type is a ceiling: every privilege above it stops working at once, for every "
        + "role they hold, and the roles themselves do not change.")) {
        await section("members", loadMembers, "members");
        return;
      }
    }

    try {
      const r = await api(`/admin/members/${encodeURIComponent(d.memberType)}/usertype`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ userType: becoming }),
      });

      toast(`${r.name}: ${r.from} → ${r.to}. ${r.note}`, true);
    } catch (e) { toast(e.message); }

    await section("members", loadMembers, "members");
    return;
  }

  if (d.memberRole) {
    // <b>Instant and unconfirmed, which is right for a sharing toggle and not for handing out
    // administration.</b> Same reasoning as *Save privileges* above, and the same narrow trigger: only
    // when the role being given is one that carries administrative capability. Design review
    // 2026-08-19.
    const becoming = event.target.value || "";

    if (becoming && (roleAdministrative.get(becoming) || []).length > 0) {
      if (!confirm(
        `Make '${d.memberRole}' a '${becoming}'? That role carries `
        + `${(roleAdministrative.get(becoming) || []).length} administrative `
        + `privilege${(roleAdministrative.get(becoming) || []).length === 1 ? "" : "s"}, `
        + "and it takes effect at once.")) {
        await section("members", loadMembers, "members");
        return;
      }
    }

    try {
      const r = await api(`/admin/members/${encodeURIComponent(d.memberRole)}/role`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ role: event.target.value || null }),
      });
      toast(`${r.name}: ${(r.from || []).join(", ") || "none"} → ${r.to || "none"}. ${r.note}`, true);
    } catch (e) { toast(e.message); }
    await section("members", loadMembers, "members");
    return;
  }

  // The form's own role picker only explains itself; nothing is saved until Create.
  if (event.target?.id === "mRole") {
    describeRole();
    return;
  }

  if (d.serviceShare) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.serviceShare)}/sharing`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sharing: event.target.value }),
      });
      // This one answers {name, sharing} rather than the {from, to} a layer's
      // sharing returns, so it says the new scope and not the transition.
      toast(`${r.name}: now shared ${r.sharing}`, true);
    } catch (e) { toast(e.message); }
    await section("services", loadServices, "services");
  }
});

/**
 * Turning a page, for whichever list asked.
 *
 * <b>One listener for all three, keyed by the list's own id.</b> The button carries which list
 * and which page, so adding a fourth paged list needs nothing here — which is the whole reason
 * the mechanism is shared rather than copied.
 *
 * <b>Re-reads rather than re-slices.</b> Turning a page calls the same loader the screen already
 * uses, so a page you turn to is as fresh as a page you refresh onto. Slicing a list held in a
 * variable would be faster and would show somebody a service that was deleted a minute ago.
 */
document.addEventListener("click", event => {
  const turn = event.target.closest?.("[data-page]");
  if (!turn) return;

  const id = turn.dataset.page;
  pages.set(id, Math.max(0, Number(turn.dataset.pageTo)));

  if (id === "services") section("services", loadServices, "services");
  else if (id === "members") section("members", loadMembers, "members");
  else if (id === "sources") section("data sources", loadSources, "sources");
  else if (id === "roleRows") section("roles", loadRoles, "roleRows");
  else if (id === "groupRows") section("groups", loadGroups, "groupRows");

  // <b>A group's two paged tables were absent from this list, so both arrows were dead.</b> The
  // index was set and nothing redrew: a fourteen-member group showed ten members and no way to
  // reach the other four — which, with the store's sort, were the managers. Found in the design
  // review of 2026-08-18.
  else if (id === "groupItems" || id === "groupMembers") refreshGroup();
  else if (id === "contentRows") {
    section("your content", loadMyContent, "contentRows").then(paintPreviews);
  }
  else if (id === "addRows") {
    if (groupNow) drawGroupAdd(groupNow).then(paintPreviews);
  }

  // <b>The one list that is sliced rather than re-read.</b> Turning a page here would otherwise
  // open a connection to somebody else's database, which is a thing the operator asks for by
  // pressing Probe. See drawProbeRows. D-103.
  else if (id === "probeRows") drawProbeRows();
});

document.addEventListener("input", async event => {
  // <b>The share dialog's own search, for the reason the comment below gives.</b> It was wired on
  // `change` — the same defect this file already records fixing for `#groupPickFilter`, and it was not
  // migrated with it. Measured 2026-08-19: typing *Roads* against 54 groups left all 54 on screen until
  // Enter was pressed. Three search boxes on one product had two behaviours.
  if (event.target.id === "shareFilter") {
    sharing.filter = event.target.value;

    // Only the table, so the box the reader is typing in is not replaced under their cursor.
    drawShareRows();
    return;
  }

  // <b>On `input`, and it was on `change` — so typing in it did nothing.</b> A `<input
  // type=search>` reports `change` on blur or Enter only, and `#groupFilter` twenty lines below was
  // already on `input`: two search boxes on one screen with two behaviours, which is worse than
  // either.
  if (event.target.id === "groupPickFilter") {
    const needle = event.target.value.trim().toLowerCase();

    // <b>One list, since the service picker became a page.</b> `#groupPickWhat` was the other one and
    // is deleted with it; iterating over a second box that no longer exists is how a dead reference
    // survives a deletion.
    for (const list of [$("groupPickWho")]) {
      if (!list) continue;

      for (const option of list.options) {
        option.hidden = needle.length > 0
          && !option.value.toLowerCase().includes(needle);
      }
    }

    return;
  }

  // The Content and Members tabs re-render in place: the search reads the group already in hand rather
  // than asking the server again. The sort is a `<select>` and so is in the change handler.
  if (event.target.id === "contentFilter") {
    contentFilter = event.target.value;
    resetPage("contentRows");
    await section("your content", loadMyContent, "contentRows");
    paintPreviews();
    return;
  }

  if (event.target.id === "addFilter") {
    resetPage("addRows");
    if (groupNow) await drawGroupAdd(groupNow);
    paintPreviews();
    return;
  }

  if (event.target.id === "probeFilter") {
    resetPage("probeRows");
    drawProbeRows();
    return;
  }

  if (event.target.id === "groupItemFilter") {
    resetPage("groupItems");
    if (groupNow) drawGroupContent(groupNow);
    return;
  }

  if (event.target.id === "groupMemberFilter") {
    resetPage("groupMembers");
    if (groupNow) drawGroupMembers(groupNow);
    return;
  }

  if (event.target.id === "groupFilter") {
    groupFilter = event.target.value;
    resetPage("groupRows");
    section("groups", loadGroups, "groupRows");
    return;
  }

  if (event.target.id === "serviceFilter") {
    serviceFilter = event.target.value;

    // <b>Back to page one on every keystroke.</b> Without this, filtering to three results
    // while standing on page four shows an empty table beside a count of three — and the
    // reader blames the filter.
    resetPage("services");
    section("services", loadServices, "services");
    return;
  }

  noteEdit(event.target);
});

/** Remembers an edit to the open editor, so leaving the page cannot lose it. */
function noteEdit(target) {
  if (!editing || !target.closest || !target.closest("#editPages")) return;

  // Sharing is applied when chosen rather than on Save (ADR-031 §2b), so it is not
  // an unsaved edit and marking it as one would promise a Save that does nothing.
  if (target.dataset && target.dataset.share) return;

  // <b>The symbology page is not remembered here, and it would fight itself if it were.</b> Its
  // form is drawn from `symModel` every time it is filled, so replaying old input values into it
  // on a page flip would put a stale value beside a model that disagrees. Its document is kept by
  // its own Store button, and by nothing else.
  if (target.closest("#page-symbology")) return;

  unsaved.set(editing.name, editedValues());
}

document.addEventListener("keydown", event => {
  if (event.key === "Escape") {
    // Escape clears a filter before it closes the Create drawer, because the filter
    // is the thing you are most likely to be holding when you press it. It does not
    // leave the layer's page: Escape dismisses something floating, and a page you
    // navigated to is left with Back or Cancel.
    // <b>An open picker closes first.</b> It is the innermost thing on screen and Escape means *out
    // of this*, so a chain that skipped it would send the reader out of the screen instead.
    if ($("groupNewForm") && !$("groupNewForm").hidden) {
      $("groupNewForm").hidden = true;
      return;
    }

    if ($("groupPicker") && !$("groupPicker").hidden) {
      $("groupPicker").hidden = true;
      return;
    }

    if (document.activeElement && document.activeElement.id === "groupFilter"
        && $("groupFilter").value !== "") {
      $("groupFilter").value = "";
      groupFilter = "";
      resetPage("groupRows");
      section("groups", loadGroups, "groupRows");
      return;
    }

    if (document.activeElement && document.activeElement.id === "serviceFilter"
        && $("serviceFilter").value !== "") {
      $("serviceFilter").value = "";
      serviceFilter = "";
      section("services", loadServices, "services");
      return;
    }
    if ($("drawer").classList.contains("on")) closeDrawer();
  }
});

// ----------------------------------------------------------------------- boot

async function whoami() {
  const me = await api("/rest/whoami");

  // <b>The gate's input.</b> ADR-034 §5b: Server needs `admin:manageServer`, and the router
  // reads it from here rather than probing an endpoint to see whether it is refused.
  privileges = new Set(me.privileges || []);

  // <b>The signed-in name, for the one act that is about oneself.</b> *Leave group* removes the
  // caller from a group, and every other member operation names somebody else — so this is the only
  // place the console needs to know who it is beyond drawing the banner.
  signedInAs = me.authenticated ? (me.name || "") : "";
  // Two lines rather than one sentence with separators: `#who b` is a block, so a leading "·" on
  // the second line was left dangling under the name. The name is the identity and the rest is what
  // it can do, which is the hierarchy this pair should read as anyway.
  $("who").innerHTML = me.authenticated
    ? `<b>${h(me.name)}</b>${h(me.roles.join(", ") || "no roles")} · ${h(me.userType)}`
    : "anonymous";
  return me;
}

$("signinForm").addEventListener("submit", async event => {
  event.preventDefault();

  const button = $("go");
  const error = $("signinError");
  error.hidden = true;
  button.disabled = true;
  button.textContent = "Signing in…";

  try {
    const r = await api("/rest/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: $("u").value, password: $("p").value }),
    });
    token = r.token;
    sessionStorage.setItem("gis-token", token);
    await start();
  } catch (e) {
    // The server's own words. A refused sign-in says whether the name is
    // unknown, the password wrong, or the account throttled, and each sends the
    // reader somewhere different.
    error.textContent = e.message || String(e);
    error.hidden = false;
    $("p").select();
  } finally {
    button.disabled = false;
    button.textContent = "Sign in";
  }
});

/**
 * Signs out, and says so if it could not.
 *
 * <b>The refusal used to be swallowed</b> — `catch { /* already gone *\/ }` — which
 * is exactly wrong here, because a failed logout is the one case that produces the
 * symptom the owner reported on 2026-08-16: *"it does not sign out, it comes back
 * to the same page."* The session is carried in two forms, a bearer token this
 * page holds and a `gis-session` cookie the browser holds and script cannot read
 * (`httponly`). Only the server can end the second, so if the request fails and
 * the page reloads anyway, the cookie signs the operator straight back in and the
 * console looks like the button does nothing.
 *
 * So the order is: ask the server first, and only clear local state and leave once
 * it has answered. If it refuses, stay and say why — a console that cannot sign
 * out must not pretend it did.
 */
/**
 * Replaces an issued password with the member's own.
 *
 * <b>The server's own message on failure, because there are three different failures.</b> The
 * password they were given can be mistyped, the new one can be too short, and the account can have
 * been reset again by an administrator while they were typing — and each sends the reader somewhere
 * different.
 */
$("mustchangeForm").addEventListener("submit", async event => {
  event.preventDefault();

  const button = $("cGo");
  const error = $("mustchangeError");
  error.hidden = true;
  button.disabled = true;

  try {
    await api("/rest/auth/password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        currentPassword: $("cCurrent").value,
        newPassword: $("cNew").value,
      }),
    });

    // <b>Straight back through `start`, not a reload.</b> The flag is resolved per request, so the
    // session in hand is already clean — re-reading `whoami` is enough, and a reload would throw
    // away the session this page holds in `sessionStorage` for no reason.
    $("cCurrent").value = "";
    $("cNew").value = "";
    await start();
    toast("That is your password now. Nothing else on this server knows it.", true);
  } catch (e) {
    error.textContent = e.message || String(e);
    error.hidden = false;
  } finally {
    button.disabled = false;
  }
});

$("signout").addEventListener("click", async event => {
  const button = event.currentTarget;
  button.disabled = true;

  try {
    await api("/rest/auth/logout", { method: "POST" });
  } catch (e) {
    button.disabled = false;
    toast(`Could not sign out: ${e.message || e}. The session is still open — the `
      + `browser's session cookie can only be cleared by the server.`);
    return;
  }

  token = null;
  sessionStorage.removeItem("gis-token");

  // <b>And the attribute, so the page it navigates to paints the sign-in screen.</b> The
  // navigation below is a fresh load and the head script reads storage again, which is now
  // empty — so this line is belt and braces rather than load-bearing. It is here because the
  // three places that decide this should be findable by searching for one name.
  document.documentElement.dataset.session = "none";

  // <b>Replaced rather than reloaded.</b> A reload re-runs the same URL and may be
  // served from cache; `replace` also takes the signed-out state out of the back
  // button, so Back cannot return to a rendered console the caller can no longer
  // read anything into.
  location.replace(location.pathname);
});

/**
 * Boots the surface, or asks for a token.
 *
 * <b>The gate is "do I hold a token", not "am I authenticated", and the difference was a
 * shipped defect.</b> Found 2026-08-17 by the owner, who pressed **Stop** as a signed-in
 * administrator and was told *"this needs the 'admin:manageServer' privilege and you are not
 * signed in"*.
 *
 * A session reaches this page in two forms. A bearer token, which this console asks for and
 * keeps in `sessionStorage`; and a `gis-session` cookie, which the services directory's own
 * sign-in form sets and which — deliberately, [ADR-023](../../../docs/adr/ADR-023-rest-services-directory.md)
 * §4c — authenticates `GET` and `HEAD` **and nothing else**. That is a CSRF defence with no
 * antiforgery token to get wrong, and it is worth more than the convenience it costs.
 *
 * The cost is what landed here. With a cookie and no token, `/rest/whoami` answers
 * `authenticated: true` with `admin:manageServer` in the list, so the old check passed, the
 * whole console painted, the header named the administrator — and every write answered 401
 * with a message saying they were not signed in. **ADR-023 §4c predicted exactly this**: *"any
 * future write surface in the browser needs a deliberate design, not a `<form>` tag."* This
 * console is that write surface; the design was right and the boot check asked the wrong
 * question.
 *
 * So a tokenless reader gets the form, and is told why rather than left to wonder why an
 * administrator is being asked to sign in.
 */
/**
 * Puts the sidebar back the width it was left at.
 *
 * <b>Before the first paint rather than after `whoami`</b>, so the column does not visibly jump
 * from wide to narrow while the session is being read.
 */
try {
  if (localStorage.getItem("gis-rail") === "tight") {
    $("shell").classList.add("tight");
    $("collapse").title = "Widen the sidebar";
  }
} catch { /* private mode: the default width is a fine answer */ }

async function start() {
  const me = await whoami();

  if (!me.authenticated || !token) {
    /*
      <b>The attribute is cleared here, and forgetting to would strand an expired session on
      a console it cannot use.</b> `index.html`'s head sets `data-session="held"` from
      `sessionStorage` before the first paint, and the stylesheet hides the sign-in panel with
      `!important` — which is what stops the wrong screen appearing during the second it takes
      454 KB of JavaScript to load. So it has to be undone by whoever finds out the session is
      not good, and that is here.

      One source of truth: the attribute is the guess, `whoami` is the answer, and the answer
      writes over the guess in both directions.
    */
    document.documentElement.dataset.session = "none";

    $("signin").style.display = "";
    $("app").style.display = "none";
    $("tabs").style.display = "none";

    // Otherwise this reads "checking…" for ever, which is a small lie of the same family as
    // the one above: a line that says it is working on something it has stopped working on.
    $("healthLine").textContent = "sign in to read the server's state";

    const cookieOnly = $("signinCookie");
    if (me.authenticated && !token) {
      cookieOnly.innerHTML = `The browser is already signed in as <b>${h(me.name)}</b>, through `
        + "the services directory. <b>That session can read and not write</b> — it is carried "
        + "in a cookie, and a cookie authenticates <code>GET</code> only, so that a page on "
        + "somebody else's site cannot make your browser change anything here. Signing in "
        + "again gives this console a token of its own.";
      cookieOnly.hidden = false;
      $("who").innerHTML = `<b>${h(me.name)}</b> · read-only session`;
    } else {
      cookieOnly.hidden = true;
    }
    return;
  }

  // Confirmed, so the guess the head made is now a fact.
  document.documentElement.dataset.session = "held";

  $("signinCookie").hidden = true;

  // <b>Signed in, and holding a password that reaches nothing else.</b> Checked before any screen
  // is drawn, because every one of them would answer 403 — which is how a client behaves against a
  // server enforcing a rule it does not advertise, and `/rest/whoami` advertises this one.
  if (me.mustChangePassword) {
    $("signin").style.display = "none";
    $("app").style.display = "none";
    $("tabs").style.display = "none";
    $("signout").style.display = "";
    $("surfaces").hidden = true;
    $("healthDot").hidden = true;
    $("healthLine").hidden = true;
    $("mustchange").style.display = "";
    $("mustchangeWho").innerHTML =
      `Signed in as <b>${h(me.name)}</b>. This has to be done before anything else works.`;
    $("cCurrent").focus();
    return;
  }

  $("mustchange").style.display = "none";
  $("signin").style.display = "none";
  $("app").style.display = "";
  $("tabs").style.display = "";
  $("signout").style.display = "";
  drawLegend();
  drawBasemapControl();

  // <b>Each section fails on its own.</b> This was one Promise.all, so a single
  // refused endpoint rejected the whole boot and the console showed nothing at
  // all — the same shape of failure as the CSP one (D-44): a small fault
  // presenting as a dead page, with the reader given no way to tell which. Now a
  // section that cannot load says so in its own place and the rest still works,
  // which is also how somebody diagnoses a half-broken server.
  // <b>Only what this reader may read.</b> The catalogue listing needs
  // `admin:viewAllContent`, so asking for it as a publisher is a refusal in the corner of a
  // screen they should not have been shown — which is the defect ADR-034 exists to fix, and
  // it would be silly to reproduce it during boot.
  // <b>Nothing that says it is working on something it will not do.</b> The health line reads
  // *checking…* until `refreshHealth` answers, and `refreshHealth` is only called for a reader
  // with `admin:manageServer` — so for a publisher it said *checking…* for ever. The same small
  // lie as the tokenless case, found the same way: by looking at the screen as somebody else.
  if (!may("admin:manageServer")) {
    $("healthDot").hidden = true;
    $("healthLine").hidden = true;
  }

  await Promise.all([
    may("admin:manageServer") ? section("health", refreshHealth) : Promise.resolve(),
    may("admin:viewAllContent") ? section("layers", loadLayers) : Promise.resolve(),
  ]);

  // <b>The address is read after the listing, not before.</b> A link straight to
  // #/layer/tr_ilce/limits has to open that layer's page on load — that is what
  // makes it an address rather than a bookmark that lands somewhere else — and the
  // page cannot say whether the layer is hosted until the catalogue is in hand.
  route();
}

// ------------------------------------------------------- the anonymous view

/**
 * One request as a caller with no credential would make it.
 *
 * <b>This whole surface rests on omitting one header.</b> `api()` attaches the
 * session token to everything, which is right for a console and wrong for the
 * only question asked here. ADR-015 §3 makes a session an opaque bearer token
 * rather than a cookie, so there is nothing else to suppress — `credentials:
 * "omit"` is there anyway, so that moving to a cookie session later cannot
 * quietly turn this tab into a liar.
 *
 * The status is returned rather than thrown on. Every other caller in this file
 * wants a refusal to be an error; here a refusal is the measurement.
 */
async function anon(path) {
  try {
    const response = await fetch(path, { credentials: "omit" });
    return { status: response.status, ok: response.ok };
  } catch (e) {
    // A dropped connection is not a refusal, and reporting it as one would
    // invent an authorization result out of a network failure.
    return { status: 0, ok: false, error: e.message || String(e) };
  }
}

// What each code means to the person reading the table. The 404 line is the
// important one: ADR-018's refusal is deliberately identical for a layer that
// does not exist and one that exists and is not shared, so this column cannot
// be read as "missing" — which is exactly the mistake D-45 recorded.
const MEANING = {
  0: "no answer — the request did not complete",
  200: "readable with no credential",
  401: "a credential is asked for",
  403: "refused, and the reason is authorization",
  404: "not found, or found and not shared — deliberately the same answer",
  503: "the service is stopped",
};

/**
 * What the catalogue intended, against what an anonymous caller actually got.
 *
 * Only disagreement is marked. A table where every cell is coloured tells the
 * reader nothing about which row to look at, and the point of this screen is
 * that most rows are boring.
 */
function reality(layer, results) {
  if (layer.status === "stopped") {
    return { bad: false, text: "stopped — nothing is expected to answer" };
  }
  const anyReadable = results.some(r => r.ok);
  if (layer.sharing === "public") {
    return results.every(r => r.ok)
      ? { bad: false, text: "as intended" }
      : { bad: true, text: "shared public, and not readable" };
  }
  // private and organization are alike from out here: an anonymous caller is
  // not in the organization either.
  return anyReadable
    ? { bad: true, text: `shared ${layer.sharing}, and readable without a credential` }
    : { bad: false, text: "as intended" };
}

const codeCell = r =>
  `<td class="num" title="${h(r.error || MEANING[r.status] || "")}">${
    r.status || "—"}</td>`;

/**
 * Probes every catalogued layer as an anonymous client and reports the two
 * mismatches that matter.
 *
 * <b>Your content, not the catalogue</b>, since it moved to Studio. It used to read
 * `/admin/layers` — every layer on the server — which needed `admin:viewAllContent` and made
 * this an operator's report. The question it answers is a publisher's: *is this layer of mine
 * visible to somebody with no credential, and did I mean that?* So it now walks the same
 * `/content/layers` rows the content screen draws, which for an administrator is still
 * everything they own and everything shared with them.
 *
 * <b>The address comes from the listing and the probe is made without your session</b>, and
 * that split is the design: you cannot ask what a stranger sees at a URL you were unable to
 * find. Group layers never appear because they are not layers in this listing — they hold no
 * features, so a count query against one is not a question about sharing.
 *
 * <b>It used to walk the services directory for the address, and therefore skipped every
 * stopped layer</b> — reporting them as *not addressable* rather than probing them. A stopped
 * layer's anonymous answer is a real answer (503, and the same for everybody), and leaving it
 * out of a report about who can see what is the wrong silence.
 */
async function loadAnonymous() {
  const body = $("anonRows");

  // The content screen fills `content`; this tab can be opened first, so it is filled here
  // when it is empty rather than assumed.
  if (content.size === 0) await loadMyContent();

  const layers = [...content.values()];

  $("anonSummary").innerHTML = "";
  body.innerHTML = `<tr><td colspan="6" class="empty">Probing ${layers.length} layers…</td></tr>`;

  const rows = [];
  // Four at a time. Ninety-odd requests fired at once is a load test of our own
  // server dressed up as a report, and it would make the slowest row look like
  // a refusal.
  for (let i = 0; i < layers.length; i += 4) {
    const batch = layers.slice(i, i + 4);
    rows.push(...await Promise.all(batch.map(async layer => {
      // Straight from the row: `/content/layers` carries the address, which is the whole of
      // D-45's complaint answered for this listing.
      const place = { service: layer.service, id: layer.layerId };
      const base = `/rest/services/${place.service}/FeatureServer`;
      const results = await Promise.all([
        anon(`${base}?f=json`),
        anon(`${base}/${place.id}?f=json`),
        anon(`${base}/${place.id}/query?where=1%3D1&returnCountOnly=true&f=json`),
      ]);
      return { layer, place, results, said: reality(layer, results) };
    })));
    body.innerHTML = `<tr><td colspan="6" class="empty">Probing ${
      Math.min(i + 4, layers.length)} of ${layers.length}…</td></tr>`;
  }

  rows.sort((a, b) => (b.said.bad ? 1 : 0) - (a.said.bad ? 1 : 0)
    || a.layer.name.localeCompare(b.layer.name));

  // <b>A disagreeing row is tinted, and only a disagreeing one.</b> The comment on `reality`
  // above says why: a table where every cell is coloured says nothing about which row to look
  // at, and most rows here are meant to be boring. The tint is the row-level version of the
  // sentence that was already in the last cell.
  body.innerHTML = rows.map(({ layer, place, results, said }) => `
    <tr${said.bad ? ' class="rowbad"' : ""}>
      <td><code>${h(layer.name)}</code><br><span class="val">${h(place.service)} · ${place.id}</span></td>
      <td>${pill(layer.sharing)}</td>
      ${results.map(codeCell).join("")}
      <td${said.bad ? ' class="bad-inline"' : ' class="val"'}>${h(said.text)}</td>
    </tr>`).join("")
    || `<tr><td colspan="6" class="empty">You own nothing and nothing is shared with you, so
          there was nothing to probe.</td></tr>`;

  const exposed = rows.filter(r => r.said.bad && r.layer.sharing !== "public").length;
  const unreachable = rows.filter(r => r.said.bad && r.layer.sharing === "public").length;
  const agreed = rows.length - exposed - unreachable;

  // <b>Three tiles, and they were two sentences.</b> Handoff 2026-09-04. The two said what went
  // wrong and left the reader to work out how much did not; a report whose answer is usually
  // *nothing is wrong* has to be able to say that as a number, or every run reads as a run that
  // found nothing rather than as one that found nothing wrong.
  const tiles = [
    ["As intended", agreed, "ok",
      "The catalogue and the anonymous answer agree, or the service is stopped"],
    ["Answered anyway", exposed, "alert",
      "Shared private or organization, and readable without a credential"],
    ["Refused anyway", unreachable, "warn",
      "Shared public and did not answer — an ArcGIS client sees the layer as missing"],
  ];

  $("anonSummary").innerHTML = `<div class="tilerow">${tiles.map(([label, n, tone, why]) =>
    `<div class="tile t-${tone}${n === 0 && tone !== "ok" ? " quiet" : ""}" title="${h(why)}">
      <b>${num(n)}</b><span>${h(label)}</span>
    </div>`).join("")}</div>`;
}

/**
 * Runs a loader and, if it fails, reports it where it happened.
 *
 * The toast carries the server's message because it is the useful part; the
 * placeholder is there so the empty table is not read as "nothing published".
 */
/* ------------------------------------------------------------------ logs */

/**
 * Which log is showing, and where its paging has got to.
 *
 * <b>Module state rather than the hash, and that is a compromise worth naming.</b> A source
 * and a filter set are exactly the kind of thing that should be linkable — *here is the
 * failing request I mean* is a sentence an operator wants to send somebody. `route()` splits
 * the hash on `/` before decoding, so a filter containing a slash cannot survive it, which
 * is the same limitation the service page's tab strip records. The addressable version waits
 * for that fix; until then the screen opens on Administration and the reader filters.
 */
let logSource = "audit";
let logCursor = null;
let logActions = [];

/**
 * The source-specific filter's value, kept here rather than read off the element.
 *
 * <b>Because the element is rebuilt on every read, and rebuilding it was silently discarding
 * what the reader had just chosen.</b> `drawLogControls` replaces `#logOwn`'s markup so the
 * filter matches the source; it ran on every load, before the query was composed, so the
 * value went into a control that no longer existed and the query read a fresh empty one.
 * Every one of the three per-source filters did nothing at all, on every source, verified by
 * a review that clicked them.
 */
let logOwn = "";

/**
 * Which read is the newest, so an older one cannot overwrite it.
 *
 * <b>The other half of the same bug, and it looked like a different one.</b> Typing in
 * Contains starts a debounced read; clicking a source tab starts another. The first was still
 * in flight, resolved second, and painted the previous source's rows under the newly
 * highlighted tab — so switching source appeared to be exactly one click behind. A counter is
 * the smallest fix that is actually correct: the response checks whether it is still the one
 * being waited for, and drops itself if it is not.
 */
let logRead = 0;

/**
 * The three logs, and what each one calls its own dimension.
 *
 * <b>A table rather than three branches, because the screen differs in one field.</b> Every
 * log answers when, who, from where and what; each has exactly one filter of its own, and
 * writing that as a row here is what stops the shared surface growing a special case per
 * source.
 */
const LOG_SOURCES = [
  ["audit", "Administration", "action"],
  ["requests", "Requests", "status"],

  // <b>*Studio viewer*, not *Studio*.</b> This console's own surface switch says Server and
  // Studio, so a source tab called Studio asked the reader to hold two meanings of the word
  // on one screen. The log is not everything Studio does — it is what its map viewer reported
  // from a browser — so the longer name is also the more accurate one.
  ["studio", "Studio viewer", "kind"],
];

/**
 * Draws the source selector and the filter that belongs to the chosen source.
 *
 * <b>Only when the source has changed, and it restores the value it holds.</b> Rebuilding
 * this on every read is what made all three per-source filters inert — see `logOwn`.
 *
 * <b>`aria-selected` and `role="tab"`, because a row of buttons where one is a different
 * colour is a segmented control to a sighted reader and three unrelated buttons to a screen
 * reader.</b>
 */
function drawLogControls() {
  const sources = $("logSources");

  if (sources) {
    sources.setAttribute("role", "tablist");
    sources.setAttribute("aria-label", "Which log");

    sources.innerHTML = LOG_SOURCES.map(([key, label]) =>
      `<button role="tab" aria-selected="${key === logSource}"
        class="tiny${key === logSource ? "" : " ghost"}" data-log-source="${key}"
        >${h(label)}</button>`).join(" ");
  }

  const own = $("logOwn");
  if (!own) return;

  // <b>A real label, not a placeholder.</b> A placeholder disappears the moment somebody
  // types, so the only thing telling them what the box means is gone exactly when they have
  // committed to it — and a screen reader may never announce it at all.
  const [, , dimension] = LOG_SOURCES.find(([key]) => key === logSource);

  // <b>The audit trail's actions come from the server with counts, and that is why they are
  // a select rather than a text box.</b> `service.style.clear` is not a string anybody
  // guesses, and an empty result from a mistyped filter is indistinguishable from a quiet
  // server.
  if (logSource === "audit") {
    own.innerHTML = `<label for="logOwnValue">Action</label>
      <select id="logOwnValue"><option value="">Any</option>`
      + logActions.map(a =>
        `<option value="${h(a.action)}"${a.action === logOwn ? " selected" : ""}
          >${h(a.action)} (${num(a.count)})</option>`).join("")
      + `</select>`;
    return;
  }

  own.innerHTML = `<label for="logOwnValue">${h(dimension === "status" ? "Status" : "Kind")}</label>
    <input type="text" id="logOwnValue" value="${h(logOwn)}"
      ${dimension === "status" ? 'inputmode="numeric" ' : ""}autocomplete="off">`;
}

/** The query string the current filters describe. */
function logQuery() {
  const parts = new URLSearchParams();
  const text = ($("logText") || {}).value;
  const who = ($("logWho") || {}).value;
  const hours = ($("logSince") || {}).value;
  // Read from module state, not from the element: the element is rebuilt when the source
  // changes and would be empty at exactly the wrong moment.
  const own = logOwn;

  if (text) parts.set("q", text);
  if (who) parts.set("principal", who);

  // <b>Computed here rather than sent as a number of hours.</b> The server takes an instant,
  // so a page left open overnight and then paged does not silently shift its own window.
  if (hours) {
    parts.set("from", new Date(Date.now() - Number(hours) * 3600000).toISOString());
  }

  if (own) parts.set(LOG_SOURCES.find(([key]) => key === logSource)[2], own);
  if (($("logFailed") || {}).checked) parts.set("failed", "true");
  if (logCursor) parts.set("before", logCursor);

  parts.set("limit", "60");

  return parts.toString();
}

/** One row, and its detail behind a click. */
function logRow(row) {
  const when = new Date(row.at);

  // <b>`tabindex` and a role, because the row is the control.</b> Clicking anywhere on it
  // reveals the detail, which is right for a mouse and was unreachable without one: the
  // detail JSON — the only place a request's duration, query and face are shown — could not
  // be opened by a keyboard at all.
  return `<tr class="logrow" tabindex="0" role="button" aria-expanded="false"
      aria-label="Show the detail of this entry">
    <td class="nowrap"><span class="val" title="${h(when.toISOString())}"
      >${h(when.toLocaleString())}</span></td>
    <td>${row.ok ? "" : `<span class="pill p-unusable">failed</span> `}<code>${h(row.what)}</code></td>
    <td>${row.who ? h(row.who) : `<span class="faint">anonymous</span>`}</td>
    <td class="nowrap"><span class="faint">${row.from ? h(row.from) : "—"}</span></td>
    <td>${row.resource ? `<code>${h(row.resource)}</code>` : "—"}</td>
  </tr>
  <!--
    <b>The detail is a row, not a drawer.</b> Every column of every log is already on screen;
    what is left is a JSON object whose keys differ per source, and a panel that reformatted
    it per source would be three renderers for something a reader opens once in fifty rows.
  -->
  <tr class="logdetail" hidden><td colspan="5"><pre>${h(logPretty(row.detail))}</pre></td></tr>`;
}

/** The detail JSON, indented, or the raw string when it is not JSON. */
function logPretty(detail) {
  try {
    return JSON.stringify(JSON.parse(detail || "{}"), null, 2);
  } catch (ignored) {
    // <b>Shown as it arrived rather than hidden.</b> A studio event's detail comes from a
    // browser, so it is the one field here that a stranger wrote; if it is not JSON, that
    // fact is itself worth seeing.
    return String(detail || "");
  }
}

/** Reads the chosen log and draws it. */
async function loadLogs(more = false) {
  const body = $("logRows");
  if (!body) return;

  // <b>Claim this read.</b> Two can be in flight — a debounced keystroke and a source click —
  // and the older one resolving second is what made the tabs look one click behind. See
  // `logRead`.
  const mine = ++logRead;

  if (!more) {
    logCursor = null;

    // <b>Read once, and only for the source that needs it.</b> The action list is a group-by
    // over the whole audit table; fetching it on every page of every source would make the
    // cheapest control on the screen the most expensive request.
    if (logSource === "audit" && logActions.length === 0) {
      const index = await api("/admin/logs");
      if (mine !== logRead) return;

      logActions = index.actions || [];
      logWriterHealth = index.writer || null;
    }

    drawLogControls();
    drawLogWriter();
    body.innerHTML = `<tr><td colspan="5" class="empty">Reading&hellip;</td></tr>`;
  }

  const answer = await api(`/admin/logs/${logSource}?${logQuery()}`);

  // <b>A newer read started while this one was out.</b> Painting now would put the previous
  // source's rows under the newly chosen tab.
  if (mine !== logRead) return;

  const rows = answer.rows || [];

  if (!more) {
    body.innerHTML = "";
  }

  if (rows.length === 0 && !more) {
    body.innerHTML = `<tr><td colspan="5" class="empty">${logEmpty()}</td></tr>`;
  } else {
    body.insertAdjacentHTML("beforeend", rows.map(logRow).join(""));
  }

  logCursor = answer.next;

  const count = $("logCount");
  if (count) count.textContent = `${num(body.querySelectorAll("tr.logrow").length)} shown`;

  const older = $("logMore");
  // <b>Offered only on a full page.</b> A short page is the end of the log, and a button
  // that fetches nothing teaches a reader to distrust it.
  if (older) older.hidden = rows.length < 60;
}

/** What the request log's writer reported, or null before it has been read. */
let logWriterHealth = null;

/**
 * The dropped-entries notice, on the log it is about.
 *
 * <b>It was shown on all three tabs and it describes one of them.</b> Only the request log is
 * lossy — the audit trail fails the request rather than dropping the row, and studio events
 * are written straight through — so telling a reader of the audit trail that nothing has been
 * dropped invited them to wonder what could be.
 */
function drawLogWriter() {
  const writer = $("logWriter");
  if (!writer) return;

  if (logSource !== "requests" || !logWriterHealth) {
    writer.hidden = true;
    return;
  }

  writer.hidden = false;

  // <b>ADR-045 condition 6.</b> The request log drops rather than blocks, so a screen that
  // never mentioned the drop would be claiming a completeness it does not have.
  writer.innerHTML = logWriterHealth.dropped > 0
    ? `<b>${num(logWriterHealth.dropped)} entries were dropped</b> since this server started,
       because the writer's queue was full — requests are never made to wait for the log. The
       rows below are what it kept, so a gap here is not proof that nothing happened.`
    : `Nothing has been dropped since this server started, so this is every request.`
      + (logWriterHealth.waiting > 0
        ? ` ${num(logWriterHealth.waiting)} are waiting to be written.` : "");
}

/**
 * What an empty result says, which depends on why it is empty.
 *
 * <b>A near-empty studio log is the hardest case on this screen and the most likely.</b> A
 * server whose viewer has reported nothing is a server whose viewer is working; without a
 * sentence saying so, an empty table reads as a feature that is broken.
 */
function logEmpty() {
  if (logSource === "studio") {
    return `Nothing reported. The viewer sends a row only when something fails in a
      browser — a script error, or a layer that would not draw — so an empty list here is
      the good outcome.`;
  }

  return `Nothing in this window matches. Widen <b>Since</b>, or clear the filters.`;
}

async function section(what, load, placeholder) {
  try {
    await load();
  } catch (e) {
    toast(`${what}: ${e.message || e}`);
    if (placeholder) {
      const target = $(placeholder);
      if (target) {
        target.innerHTML =
          `<tr><td colspan="9" class="empty">Could not load ${h(what)}: ${h(e.message || e)}
           <br>This is not an empty list — the request was refused.</td></tr>`;
      }
    }
    return null;
  }
}

// <b>Wired once, before anything is drawn.</b> The symbology form's listeners are
// delegated on the document, so they survive every rebuild of the page under them.
wireSymbologyForm();

start().catch(e => toast(e.message));
