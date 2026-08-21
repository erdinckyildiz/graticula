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

// The colour a preview is drawn in before its layer document has been read. The document
// carries the server's own `drawingInfo`, and `drawPreview` reads it on the way — this is only
// what the canvas starts with.
const GENERATED_FALLBACK = PALETTE[0];
const TILE_COLOUR = "#8fb8cc";
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
    <dt>Members</dt><dd>${num((one.members || []).length)}</dd>
    <dt>Services</dt><dd>${num(items.length)}</dd>
    <dt>Visible to</dt><dd>${one.visibility === "public"
      ? `anybody`
      : one.visibility === "organization" ? `any signed-in member` : `its members only`}</dd>
    <dt>Joining</dt><dd>${one.joinPolicy === "self" ? `anyone who can see it` : `by invitation`}</dd>
    <dt>Contributing</dt><dd>${one.contribute === "members"
      ? `any member`
      : `the owner and its managers`}</dd>
    <dt>Confers</dt><dd>${one.itemUpdate === "allItems"
      ? `editing every service shared with it`
      : one.itemUpdate === "ownItems"
        ? `editing the services a member shared themselves`
        : `reading only`}<span class="val"> — fixed at creation</span></dd>
    ${one.deleteLocked ? `<dt>Deletion</dt><dd>locked <span class="val">— including for an
      administrator</span></dd>` : ""}`;
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
            ? `<canvas class="thumb" width="104" height="70"
                 data-preview="${h(i.cover.url)}" data-colour=""></canvas>`
            : `<div class="thumb empty"></div>`}</td>
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
        ["public", "Everybody — not built yet", true],
      ])}<span class="u"></span></div>
    <p class="hint">Seeing that a group exists is not being able to read what is in it. What is shared
      with this group stays readable by its members and nobody else, whatever this says.
      <b>Everybody</b> would mean an anonymous caller, and there is nowhere for that to happen yet.</p>

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
                 "gsVisibility", "gsJoin", "gsContribute"];

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
            ? `<canvas class="thumb" width="104" height="70"
                 data-preview="${h(i.cover.url)}" data-colour=""></canvas>`
            : `<div class="thumb empty"></div>`}</td>
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

  // `needs a, b` is written into the label by loadRoles.
  const needsOf = element => {
    const said = element.parentElement?.querySelector(".val")?.textContent ?? "";
    const match = said.match(/needs (.+)/);
    return match ? match[1].split(",").map(x => x.trim()).filter(Boolean) : [];
  };

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
          ${items.map(c => `
            <label class="roleprivilege">
              <input type="checkbox" data-privilege="${h(c.name)}"
                ${held.has(c.name) ? "checked" : ""}
                ${chosen.editable ? "" : "disabled"}>
              <span class="mono">${h(c.name)}</span>
              ${c.requires.length
                ? `<span class="val">needs ${c.requires.map(h).join(", ")}</span>` : ""}
              ${c.includes.length
                ? `<span class="val">includes ${c.includes.map(h).join(", ")}</span>` : ""}
            </label>`).join("")}
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

    // <b>Server's action is a service, Studio's is a layer.</b> `POST /admin/featureservices`
    // creates an empty container, which is the thing an operator makes here — publishing data into
    // it is Studio's act. The drawer holds the form either way; only the label and the id differ.
    action: { id: "newService", label: "New service" },

    tabs: [
      ["services", "Services"],

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
const SDK = "https://js.arcgis.com/4.29/";

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
            <td class="thumbcell">${i.cover
              ? `<canvas class="thumb" width="104" height="70"
                   data-preview="${h(i.cover.url)}" data-colour=""></canvas>`
              : `<div class="thumb empty"></div>`}</td>
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

  if (serviceTabWanted) serviceTab = serviceTabWanted;

  // <b>Consumed here and never read again, which the first version got wrong.</b> `drawServiceVis` was
  // re-reading `?mode=` on every redraw, so pressing *Tiles* set the mode and the next draw put it back
  // to what the address said. Measured 2026-08-19. An address is an instruction on arrival, not a
  // standing one.
  const askedLayer = hashQuery.get("layer");
  const askedMode = hashQuery.get("mode");

  if (askedLayer !== null) visLayerIndex = askedLayer;
  if (askedMode === "tiles" || askedMode === "features") visMode = askedMode;

  $("serviceCrumb").innerHTML =
    `<a href="#/services${folder ? "/" + encodeURIComponent(folder) : ""}">Services</a>
     › ${folder ? h(folder) : "root"} › <b>${h(name)}</b>`;
  $("serviceFacts").textContent = "";

  // <b>A service with no layers is a different screen.</b> There is nothing to list and there are
  // bounds to set, and asking the server which kind this is beats guessing from the shape of a
  // document that 404s for the other kind. `/limits` exists only for a system service, so its
  // answer is the question.
  const limits = await loadServiceLimits(name);

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

  // <b>The service's own settings, on the service.</b> Rendered before the layer list, because they
  // are what this page is now for: the list below says what is inside, and these say what the
  // container offers. Same `.setting` rows and `h4` groups as everywhere else.
  drawServiceSettings(name, folder);
  drawServiceDelete();
  drawServiceTabs();

  // <b>An image service has no FeatureServer face, so asking for one is a 404 in the
  // console's own network log.</b> `kind` comes from the row that was just drawn, so
  // this costs nothing and skips a request that could only ever fail. The panel below
  // describes layers; a coverage's own description is on the settings page instead.
  const kind = (SERVICE_ROWS.find(r =>
    r.name === name && (r.folder || null) === (folder || null)) || {}).kind;

  if (kind === "ImageServer") {
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
    $("serviceFacts").textContent =
      `${layers.length} entr${layers.length === 1 ? "y" : "ies"} in the service document`
      + ` · max ${num(doc.maxRecordCount)} rows`
      + ` · ${doc.capabilities ? `operations: ${doc.capabilities}` : "no editing operations"}`;

    drawServiceLayers(layers, qualified);
  } catch (e) {
    $("serviceFacts").textContent = "";
    toast(`${qualified}: ${e.message || e}`);
  }
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

    for (const id of ["serviceOverview", "serviceData", "serviceDanger"]) {
      const panel = $(id);
      if (panel) panel.hidden = true;
    }

    // The wrapper is a container on this surface, not a tab.
    $("serviceSettings").hidden = false;

    return;
  }

  strip.hidden = false;

  const mine = SERVICE_TABS.filter(([key]) => {
    // A system service is settings and nothing else.
    if (serviceIsSystem) return key === "settings";

    // Data needs a layer to read and Visualization needs one to draw. Overview stays either way:
    // *this service holds no layers* is a fact about the service and belongs on the page that
    // describes it.
    if (key === "data" || key === "visualization") {
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

  strip.innerHTML = mine.map(([key, label]) =>
    `<a href="#" data-service-tab="${key}"${key === serviceTab ? ' aria-current="page"' : ""}
      >${label}${key === "overview" && serviceLayers.length
        ? ` <span class="count">${num(serviceLayers.length)}</span>` : ""}</a>`).join("");

  showServiceTab(serviceTab);
}

/** Reveals one tab's panels and hides the others. */
function showServiceTab(which) {
  if (surfaceOfPath() !== "studio") return;

  serviceTab = which;

  for (const [key, id] of [["overview", "serviceOverview"], ["data", "serviceData"],
                           ["visualization", "serviceVis"], ["settings", "serviceSettings"]]) {
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

      return `<tr>
        <td class="name${nested ? " nested" : ""}">${group ? `<span class="rowicon">${
          icon("folder")}</span>` : ""}${group
            ? h(layer.name || "")
            : `<a href="#/layer/${encodeURIComponent(layer.name || "")}">${h(layer.name || "")}</a>`}
          <div class="rowmeta">${h(said)}</div></td>
        <td class="acts">${group ? "" : `<a class="tiny"
          href="${h(`/rest/services/${qualified.split("/").map(encodeURIComponent).join("/")}`)}/FeatureServer/${
            num(layer.id ?? 0)}?f=json" target="_blank" rel="noreferrer">document</a>`}</td>
      </tr>`;
    }).join("");

  // <b>Redrawn here, because the strip is built before the document arrives.</b> `showService` draws the
  // tabs so the page is usable while the FeatureServer document is in flight; the count on Overview and
  // the sentence under Delete both depend on the answer, so both are drawn again when it lands.
  drawServiceDelete();
  drawServiceTabs();
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
async function drawServiceDetails(qualified) {
  const box = $("serviceDetails");
  if (!box || surfaceOfPath() !== "studio") return;

  const root = `${location.origin}/rest/services/${
    qualified.split("/").map(encodeURIComponent).join("/")}/FeatureServer`;

  // The address first and without waiting, because it is derivable here and is the one thing on this
  // column somebody came for.
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
    const answer = await api("/content/items");
    const item = (answer.items || []).find(i => i.name === qualified);

    if (!item) return;

    // <b>The sharing scope is the control that changes it, here as everywhere else.</b> This was the one
    // place on the product where that pill was inert — the content list has wrapped it in `.pillbtn`
    // since §5l, and the click delegation matches `[data-share]` anywhere in the document, so this needs
    // no new markup and no new behaviour. A fact that is a button in one place and a label in another is
    // the page lying about which.
    //
    // <b>And no `Layers` row.</b> The count is already on the tab badge and in the subtitle above; a
    // third copy is a row that varies with nothing.
    const rows = [
      ["Kind", h(item.kind || "feature service")],
      ["Owner", h(item.owner || "—")],
      ["Folder", item.folder ? h(item.folder) : `<span class="val">the site root</span>`],
      ["Sharing", `<button class="pillbtn" data-share="${h(item.name)}"
         title="Set who can reach this">${pill(item.sharing)}</button>`],
      ["Published", item.created ? h(String(item.created).slice(0, 10)) : `<span class="val">—</span>`],
      ["Updated", item.updated ? h(String(item.updated).slice(0, 10)) : `<span class="val">—</span>`],
    ];

    $("svcFacts").innerHTML = rows.map(([label, value]) =>
      `<dt>${label}</dt><dd>${value}</dd>`).join("");

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

  picker.innerHTML = publishable.length === 0
    ? `<option value="">this service holds no layers</option>`
    : publishable.map(l =>
        `<option value="${num(l.id ?? 0)}">${h(l.name || `layer ${l.id}`)}</option>`).join("");

  picker.disabled = publishable.length === 0;

  views.innerHTML = [["table", "Table"], ["fields", "Fields"]].map(([key, label]) =>
    `<a href="#" data-data-view="${key}"${key === dataView ? ' aria-current="page"' : ""}>${label}</a>`)
    .join("");

  picker.onchange = loadServiceData;

  if (publishable.length > 0) loadServiceData();
  else $("dataRows").innerHTML = "";
}

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
 * <b>The preview is the canvas the content list already draws.</b> `paintPreviews` walks every
 * `canvas[data-preview]` in the document, so placing one here needs no new mechanism — and it is
 * generated from the layer's own geometry rather than stored, which is why a product with no thumbnail
 * storage can still show a picture.
 */
function drawServiceHead(item) {
  const box = $("serviceHead");
  if (!box) return;

  box.innerHTML = `
    <div class="itemhead">
      ${item.cover
        ? `<canvas class="thumb" width="168" height="112"
             data-preview="${h(item.cover.url)}" data-colour=""></canvas>`
        : `<div class="thumb empty"></div>`}
      <div>
        <b>${h(item.bare || item.name || "")}</b>
        <div class="rowmeta">${h(item.kind || "feature service")} · ${num(item.layers || 0)}
          layer${(item.layers || 0) === 1 ? "" : "s"} · ${h(item.sharing || "")}</div>
        ${item.description ? `<p class="hint">${h(item.description)}</p>` : ""}
        <div class="footnote">${item.updated ? `Updated ${h(String(item.updated).slice(0, 10))}` : ""}${
          item.created ? ` · published ${h(String(item.created).slice(0, 10))}` : ""}</div>
      </div>
    </div>`;

  paintPreviews();
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
    `<option value="${num(l.id ?? 0)}"${String(l.id) === wanted ? " selected" : ""}
      >${h(l.name || `layer ${l.id}`)}</option>`).join("");

  picker.onchange = () => {
    visLayerIndex = picker.value;
    drawVisNow();
  };

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

  const query = `?tab=visualization&layer=${encodeURIComponent(String(at.index ?? 0))}`
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

        <p><a class="button" id="coverageView" target="_blank" rel="noreferrer" href="#"
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
      <div class="setting"><span class="q">Sharing scope:</span>

        <select id="capSharing" data-service-sharing="${h(name || "")}"
          data-folder="${h(folder || "")}">${
          ["private", "organization", "public"].map(v =>
            `<option value="${v}">${v}</option>`).join("")}<option value="group" disabled
              >group — shared into a group; set on the item, not here</option></select></div>
      <p class="hint">Applied the moment it is chosen, not on Save — an owner narrowing who may see
        a service has to be able to trust that it happened rather than press Save afterwards
        (ADR-031 §2b, the same rule the role select follows).</p>
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
async function loadSymbology(name) {
  const state = $("symState");

  try {
    const r = await api(`/admin/layers/${encodeURIComponent(name)}/symbology`);

    $("symDoc").value = r.symbology ? JSON.stringify(r.symbology, null, 1) : "";

    $("symDerived").textContent = r.drawingInfo
      ? JSON.stringify(r.drawingInfo, null, 1)
      : "none — this layer's stored document could not be projected";

    // <b>Generated is stated, not implied by an empty box.</b> §5b makes a generated
    // appearance a real answer with a version of 0, and a reader who sees nothing cannot
    // tell that from a layer whose style failed to load.
    state.innerHTML = r.stored
      ? `A stored document, ${num(JSON.stringify(r.symbology).length)} bytes. `
        + `Both faces are derived from it.`
      : `<b>Generated.</b> No document is stored, so this layer is drawn in a colour `
        + `derived from its name — the same colour tomorrow and on another deployment. `
        + `Storing one replaces it.`;

    drawLosses(r.losses);
  } catch (e) {
    state.textContent = e.message;
    $("symDerived").textContent = "—";
    drawLosses([]);
  }
}

/** The conversion's losses, or nothing when there are none. */
function drawLosses(losses) {
  const box = $("symLoss");
  const list = $("symLossList");

  if (!losses || losses.length === 0) {
    box.hidden = true;
    list.innerHTML = "";
    return;
  }

  list.innerHTML = losses.map(l => `<li>${h(l)}</li>`).join("");
  box.hidden = false;
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
async function loadServiceLimits(name) {
  const panel = $("serviceLimits");
  if (!panel) return;

  panel.hidden = true;
  $("limSave").hidden = true;
  $("limClear").hidden = true;

  let limits;
  try {
    limits = await api(`/admin/services/${encodeURIComponent(name)}/limits`);
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
async function loadMembers() {
  const answer = await api("/admin/members");
  const rows = answer.members || [];

  memberGrants = answer.grants || {};

  // Kept for the transfer picker: a removal has to offer somebody, and the listing is where the
  // names already are. Only the ones who can sign in, because the server refuses a disabled
  // target and a control that exists to be told no is worse than no control.
  memberNames = rows.filter(m => !m.disabled).map(m => m.name);

  const fill = (id, values, chosen) => {
    $(id).innerHTML = values.map(v =>
      `<option value="${h(v)}"${v === chosen ? " selected" : ""}>${h(v)}</option>`).join("");
  };

  fill("mRole", answer.roles || [], "publisher");
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
      <td class="name">${h(m.name)}${m.displayName
        ? `<div class="val" style="font-weight:400">${h(m.displayName)}</div>` : ""}</td>
      <td><select data-member-role="${h(m.name)}">
        ${(answer.roles || []).map(r =>
          `<option value="${h(r)}"${m.roles.includes(r) ? " selected" : ""}>${h(r)}</option>`)
          .join("")}
        <option value=""${m.roles.length === 0 ? " selected" : ""}>— none —</option>
      </select></td>
      <td class="val">${h(m.userType)}</td>
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
          ? `<canvas class="thumb" width="104" height="70"
               data-preview="${h(r.cover.url)}" data-colour="${GENERATED_FALLBACK}"></canvas>`
          : `<div class="thumb empty"></div>`}</td>

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
 * Fills every preview canvas on screen, in reading order.
 *
 * <b>One at a time on purpose.</b> Forty rows is forty queries, and firing them together is a
 * load test of our own server dressed up as a screen — the argument the anonymous view's
 * batching already makes. Sequential also means the pictures appear in the order somebody reads
 * them.
 */
async function paintPreviews() {
  for (const canvas of document.querySelectorAll("canvas[data-preview]")) {
    if (canvas.dataset.drawn) continue;
    canvas.dataset.drawn = "1";
    await drawPreview(canvas, canvas.dataset.preview, canvas.dataset.colour);
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

/** Says, next to Save, that there is something to save. */
function markUnsaved(dirty) {
  const marker = $("editDirty");
  if (marker) marker.hidden = !dirty;
}

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

      <h4>Style</h4>
      <div class="row">
        <button data-style="${h(name)}">Fetch current</button>
        <button data-style-del="${h(name)}" class="ghost">Back to generated</button>
        <button class="primary" data-style-put="${h(name)}">Store</button>
      </div>
      <textarea id="styleDoc" rows="8" spellcheck="false"
        placeholder="A MapLibre style document. Fetch it first — an empty box means none is stored."></textarea>
    </section>

    <section class="page" id="page-symbology">
      <h4>How this layer is drawn</h4>
      <p class="hint" id="symState">Reading…</p>

      <div class="row">
        <button data-symbology="${h(name)}">Fetch current</button>
        <button data-symbology-del="${h(name)}" class="ghost">Back to generated</button>
        <button class="primary" data-symbology-put="${h(name)}">Store</button>
      </div>

      <textarea id="symDoc" rows="12" spellcheck="false"
        placeholder="A MapLibre style, or an Esri drawingInfo pasted straight from ArcGIS. Both are accepted; a drawingInfo is converted on the way in and you are told what the conversion cost."></textarea>

      <!--
        <b>The losses are the point of this page, not a footnote.</b> ADR-033 accepted a
        lossy conversion and the mitigation is that it says so — so the report is a block
        of its own under the editor rather than a line in a toast that scrolls away.
      -->
      <div id="symLoss" hidden>
        <h4>What the ArcGIS face cannot carry</h4>
        <ul class="losses" id="symLossList"></ul>
      </div>

      <h4>What an ArcGIS client receives</h4>
      <p class="hint">Derived from the document above, in the three renderer families a
        client understands — <span class="mono">simple</span>,
        <span class="mono">uniqueValue</span>, <span class="mono">classBreaks</span>.
        Read-only: it is a projection, not a second place to edit.</p>
      <pre class="doc" id="symDerived">—</pre>
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

  showEditPage(page);
  describeContents(name, l);

  // A background refresh passes its own snapshot; otherwise anything left unsaved
  // from earlier in this session is the snapshot.
  const restore = pending ?? unsaved.get(name) ?? null;
  markUnsaved(unsaved.has(name));

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
        ["Pixel size", doc.pixelSizeX
          ? `${Number(doc.pixelSizeX.toPrecision(6))} × ${Number(doc.pixelSizeY.toPrecision(6))}`
          : null],
        ["No data", doc.noDataValue ?? "none declared"],
        ["Formats", doc.supportedImageFormatTypes],
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

    if (row?.sharing) $("capSharing").value = row.sharing;
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
async function loadSources() {
  const { dataSources } = await api("/admin/datasources");
  $("cSources").textContent = dataSources.length;

  $("sourcesPager").innerHTML = pagerFor("sources", dataSources.length);

  $("sources").innerHTML = dataSources.length === 0
    ? `<tr><td colspan="4" class="empty">None registered.</td></tr>`
    : pageOf("sources", dataSources).map(d => `<tr>
        <td class="name">${h(d.name)}
          <div class="rowmeta">${h(d.kind)}${d.name === "datastore"
            ? " · this server's own hosted store, and it cannot be removed" : ""}</div></td>
        <td class="val">${d.sealedWithAnotherKey
          ? `<span class="bad-inline">sealed with a key this build does not hold</span>`
          : h(d.summary || "—")}</td>
        <td class="num">${num(d.layerCount)}</td>
        <td class="acts"><button data-probe="${h(d.id)}"
            data-probe-name="${h(d.name)}">Probe</button>
          <button data-source-edit="${h(d.id)}" data-source-name="${h(d.name)}"
            data-source-summary="${h(d.summary || "")}"
            data-source-layers="${num(d.layerCount)}">Edit</button>${d.name === "datastore"
              ? ""
              : ` <button class="danger" data-source-remove="${h(d.id)}"
                    data-source-name="${h(d.name)}"
                    data-source-layers="${num(d.layerCount)}">Remove</button>`}</td>
      </tr>`).join("");
}

/** Which source the edit form is about, or null. */
let editingSource = null;

/**
 * The form for correcting a source's connection string.
 *
 * <b>The whole string, not the part that changed, and the form says so.</b> The stored one is sealed
 * and the server does not read it back to merge into — so an operator who types only a new password
 * would lose the host. The current host and database are shown above the field for exactly that
 * reason: they are what has to be retyped.
 *
 * <b>What it does not offer is `force`.</b> The server refuses when layers on this source would stop
 * working, and it names them; that refusal arrives here as a sentence with a *Publish anyway* button
 * built from it, so the decision is made against the list rather than in advance of it.
 */
function drawSourceEdit(id, name, summary, layers) {
  editingSource = { id, name, summary, layers, force: false };

  const box = $("probe");

  box.innerHTML = `
    <h2>${h(name)} — connection</h2>
    <div class="panel pad">
      <p class="hint">Currently <code>${h(summary || "unknown")}</code>${layers > 0
        ? ` · ${num(layers)} layer${layers === 1 ? "" : "s"} read from it`
        : " · nothing is published on it"}. Send the <b>whole</b> connection string: the stored one is
        sealed, so this server cannot merge a change into it.</p>

      <form id="sourceEditForm" autocomplete="off">
        <div class="row">
          <label class="field" style="flex:1">Connection string
            <input id="seConnection" required spellcheck="false"
                   placeholder="Host=…;Port=5432;Database=…;Username=…;Password=…"></label>
        </div>
        <div class="row">
          <label class="field">Name
            <input id="seName" value="${h(name)}" spellcheck="false"></label>
        </div>
        <p class="hint bad-inline" id="seRefused" hidden role="alert"></p>
        <div class="row">
          <button type="submit" class="primary">Test and save</button>
          <button type="button" class="ghost" id="seCancel">Cancel</button>
        </div>
      </form>
    </div>`;

  $("sourceEditForm").addEventListener("submit", saveSourceEdit);
  $("seCancel").addEventListener("click", () => {
    editingSource = null;
    box.innerHTML = "";
    focusSources();
  });
  $("seConnection").focus();
}

/**
 * Sends the correction, and turns a refusal into the decision it is.
 *
 * <b>Two refusals arrive here and they are different.</b> *Cannot connect* is a typo — the field keeps
 * what was typed and the message says what to check. *These layers would stop working* is a judgement:
 * the server has connected, looked, and found the tables missing, so the form offers to proceed with
 * the list in front of the operator rather than asking them to guess in advance.
 */
async function saveSourceEdit(event) {
  event.preventDefault();

  const refused = $("seRefused");
  const connection = $("seConnection").value.trim();

  if (!connection) {
    refused.hidden = false;
    refused.textContent = "A connection string is required.";
    return;
  }

  refused.hidden = true;

  try {
    const answer = await api(
      `/admin/datasources/${encodeURIComponent(editingSource.id)}`
      + (editingSource.force ? "?force=true" : ""),
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: $("seName").value.trim() || null,
          connectionString: connection,
        }),
      });

    editingSource = null;
    $("probe").innerHTML = `<h2>Saved</h2>
      <div class="panel pad"><p class="hint">${h(answer.name)} now reads
        <code>${h(answer.summary)}</code>, and found ${num(answer.publishable ?? 0)} publishable
        table${(answer.publishable ?? 0) === 1 ? "" : "s"} there. ${(answer.missing || []).length
          ? `<b>${num(answer.missing.length)} layer${answer.missing.length === 1 ? "" : "s"} will not
             work:</b> ${h(answer.missing.join(", "))}.`
          : "Every layer on it still has its table."}</p></div>`;

    await loadSources();
  } catch (e) {
    const message = e.message || String(e);

    refused.hidden = false;

    // <b>The API's last sentence is not the operator's.</b> The server ends the missing-tables refusal
    // with *"Send force=true if it is deliberate"* — which is right for the caller holding a terminal
    // and wrong on a screen that is about to grow a button doing exactly that. Two audiences, one
    // message: the endpoint keeps the instruction for `curl`, and the console trims it and lets the
    // button speak. Design review 2026-08-19.
    refused.textContent = message.replace(/\s*Send force=true[^.]*\.\s*$/, "");

    // <b>The forced retry is offered only for the refusal that is a judgement.</b> A connection that
    // cannot be reached is not a decision anybody should be able to override.
    if (message.includes("force=true") && !editingSource.force) {
      // <b>One button, however many times this path is taken.</b> `after` inserts, so a second
      // force-eligible refusal used to stack a second *Save anyway* ahead of the first — two identical
      // overrides, one of them stale. An id makes the insert idempotent.
      $("seForceAgain")?.remove();

      const again = document.createElement("button");

      again.type = "button";
      again.id = "seForceAgain";
      again.className = "danger";
      again.textContent = "Save anyway";
      again.addEventListener("click", () => {
        editingSource.force = true;
        $("sourceEditForm").requestSubmit();
      });

      refused.after(again);
    }

    // <b>Scrolled to, because at 1024×768 this sits below the fold.</b> The row's three action buttons
    // wrap to two lines at that width, which pushes the form down — measured at `top: 724` in a
    // 768-pixel viewport before any browser chrome, so in a real window the message an operator needs
    // is off screen. Focus alone does not fix it: the field is already focused when the form draws.
    refused.scrollIntoView({ block: "center", behavior: "instant" });
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

function renderProbe(name, r) {
  const tables = r.tables || [];
  $("probe").innerHTML = `
    <h2>${h(name)} — probed just now</h2>
    <div class="panel pad">
      <div class="row" style="margin-bottom:10px">
        ${pill(r.outcome)}
        <span style="font-size:13.5px">${h(r.message)}</span>
      </div>
      <dl class="facts" style="grid-template-columns:auto 1fr auto 1fr">
        <dt>PostgreSQL</dt><dd>${h(r.serverVersion || "—")}</dd>
        <dt>PostGIS</dt><dd>${h(r.postgisVersion || "—")}</dd>
        <dt>Can publish</dt><dd>${r.canPublish ? "yes" : "no"}</dd>
        <dt>Tables visible</dt><dd>${num(tables.length)}</dd>
      </dl>
    </div>
    ${tables.length ? `<div class="panel" style="margin-top:14px">
      <table>
        <thead><tr><th>Schema</th><th>Table</th><th>Geometry</th><th class="num">SRID</th>
          <th>Object id</th><th>Writable</th></tr></thead>
        <tbody>${tables.map(t => `<tr>
          <td class="val">${h(t.schemaName)}</td>
          <td class="name">${h(t.tableName)}</td>
          <td class="val">${h(t.geometryType)} · ${h(t.geometryColumn)}</td>
          <td class="num">${h(t.srid)}</td>
          <td class="val">${h(t.objectIdColumn || "—")}</td>
          <td>${t.writable ? "yes" : "read only"}</td>
        </tr>`).join("")}</tbody>
      </table>
    </div>` : ""}`;
}

// ----------------------------------------------------------------- operations

async function loadOperations() {
  let health;
  try { health = await api("/admin/health"); }
  catch (e) { $("storeMetrics").innerHTML = metric("Platform store", "unreachable", e.message); return; }

  const store = health.platformStore || {};
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

  $("drawerTitle").textContent = "New service";
  $("drawerSub").textContent = "a container for layers, and the groups inside it";
  $("drawerBody").innerHTML = `
    <div class="group">
      <h3>An empty service</h3>
      <p class="hint">A service is a container of layers, so it can exist before its layers do —
        and that is the order you need when the structure matters: create the service, add the
        groups, then publish layers into it naming the group to nest under.</p>
      <form id="svcForm" autocomplete="off">
        <div class="row">
          <label class="field">Service name<input type="text" id="cName" placeholder="EarlyAlert" required></label>
          <label class="field">Folder <span class="val">(optional)</span>
            <input type="text" id="cFolder" placeholder="hosted"></label>
          <label class="field">Sharing<select id="cShare">
            <option value="private" selected>private</option>
            <option value="organization">organization</option>
            <option value="public">public</option>
          </select></label>
        </div>
        <div class="row">
          <label class="field" style="flex:1 1 100%">Description <span class="val">(optional)</span>
            <input type="text" id="cDesc" placeholder="what it is for"></label>
        </div>
        <button type="submit">Create empty service</button>
      </form>
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

  $("svcForm").addEventListener("submit", createService);
  $("grpForm").addEventListener("submit", createGroupLayer);
  $("gService").addEventListener("change", event => showServiceGroups(event.target.value));

  section("service names", fillServiceChoices);

  $("drawer").classList.add("on");
  $("drawer").setAttribute("aria-hidden", "false");
  $("drawer").inert = false;

  // <b>Focus goes in, which every other reveal in this console already does.</b> Opening it left
  // `document.activeElement` on the trigger, so the first Tab went to the folder rail's *+* button
  // elsewhere on the page rather than into the drawer. It is a panel and not a modal — deliberately,
  // see the note above — and a panel still places initial focus in itself. Design review 2026-08-19;
  // D-93 is the half of that finding this does not fix, which is that focus is not trapped and does
  // not need to be.
  $("cName")?.focus();
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

async function createService(event) {
  event.preventDefault();

  const folder = $("cFolder").value.trim();
  const created = await api("/admin/featureservices", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: $("cName").value.trim(),
      folder: folder || null,
      description: $("cDesc").value.trim() || null,
      sharing: $("cShare").value,
    }),
  });

  reportCreated("Service created", [
    `<b>${h(created.name)}</b> at <code>${h(created.url)}</code>, shared ${h(created.sharing)}.`,
    // The server's own next-step note, which names the two calls that fill it.
    created.note ? `<span class="val">${h(created.note)}</span>` : "",
  ]);

  // Pre-fill the group form, because creating an empty service and then adding a
  // group to it is the sequence the note just described.
  $("gService").value = folder ? `${folder}/${created.name}` : created.name;

  await section("service names", fillServiceChoices);
}

async function createGroupLayer(event) {
  event.preventDefault();

  const where = splitService($("gService").value);
  if (!where.name) { toast("Name the service the group goes in."); return; }

  const parent = $("gParent").value.trim();

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

  // The list under the form is the record of what this service now holds, so it moves
  // with the thing it lists rather than on the next drawer opening.
  await section("groups", () => showServiceGroups($("gService").value));
  await section("services", loadServices, "services");
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

  // Navigation is links now — the tab strip, the editor's left column, the
  // breadcrumb and Cancel — so it needs no branch here. The browser follows the
  // href, the hash changes, and route() paints. That is also what makes Back work.
  // <b>One drawer for both surfaces' actions.</b> It already covers a layer, a service and a group
  // inside one — the form was general before the button was — so Server's *New service* and
  // Studio's *New layer* open the same thing and each names the part its reader came for. Two
  // drawers would be two copies of a publish form, which is D-46's whole subject.
  if (t.id === "newLayer") { openAddItem(); return; }
  if (t.id === "newService") { openNewService(); return; }
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
    markUnsaved(false);
    return;
  }

  if (t.id === "editSave") {
    t.disabled = true;
    // <b>The layer editor's Save has nothing left to save, and that is the point.</b> Capabilities
    // and limits belong to the service and are saved there; a layer's cache TTL and its style have
    // their own buttons, and its sharing applies when chosen. Kept as a no-op with a sentence rather
    // than removed, because a reader who remembers pressing Save here should be told where it went.
    toast("A layer's own settings apply as you set them. What a service offers — its capabilities "
      + "and its limits — is one setting per service, on the service's page.", true);
    t.disabled = false;
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
    const seconds = d.clear ? null : Number($("ttl").value);
    try {
      const r = await api(`/admin/layers/${encodeURIComponent(d.cache)}/cache`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ seconds: d.clear ? null : seconds }),
      });
      toast(r.note, true);
    } catch (e) { toast(e.message); }
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
      if (r && r.stored === false) {
        $("styleDoc").value = "";
        toast(r.note || "No style stored; a generated one is served.", true);
      } else {
        $("styleDoc").value = JSON.stringify(r, null, 1);
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

      // <b>The losses go on the page, and the count goes in the toast.</b> A toast is
      // read once and dismissed; a conversion that lost four things needs to still be
      // saying so when the operator looks again.
      $("symDoc").value = JSON.stringify(r.symbology, null, 1);
      $("symDerived").textContent = JSON.stringify(r.drawingInfo, null, 1);
      drawLosses(r.losses);

      $("symState").innerHTML = `Stored from your ${h(r.from)}, ${num(r.bytes)} bytes.`;

      toast(
        r.losses.length === 0
          ? `${r.name}: stored, and nothing was lost.`
          : `${r.name}: stored. ${r.losses.length} thing${r.losses.length === 1 ? "" : "s"} `
            + `the ArcGIS face cannot carry — listed below the editor.`,
        r.losses.length === 0);
    } catch (e) { toast(e.message); }

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
      toast(`${r.name}: ${r.replaced ? "style replaced" : "style stored"}, ${num(r.bytes)} bytes.`, true);
    } catch (e) { toast(e.message); }
    return;
  }

  if (d.styleDel) {
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.styleDel)}/style`,
        { method: "DELETE" });
      $("styleDoc").value = "";
      toast(r.note || "Back to the generated style.", true);
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

  if (t.id === "gsSave") {
    await saveGroupSettings({
      title: $("gsTitle").value.trim() || null,
      summary: $("gsSummary").value.trim() || null,
      description: $("gsDescription").value.trim() || null,
      visibility: $("gsVisibility").value,
      joinPolicy: $("gsJoin").value,
      contribute: $("gsContribute").value,
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
      const r = await api(`/admin/services/${encodeURIComponent(name)}/limits`, {
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
    await section("limits", () => loadServiceLimits(name));
    return;
  }

  if (d.systemStatus) {
    t.disabled = true;
    try {
      const r = await api(`/admin/services/${encodeURIComponent(d.systemStatus)}/${d.to}`,
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
    drawSourceEdit(d.sourceEdit, d.sourceName, d.sourceSummary, Number(d.sourceLayers) || 0);
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

  if (t.id === "sTest" || t.id === "sAdd") {
    event.preventDefault();
    const body = { name: $("sName").value.trim(), connectionString: $("sConn").value };
    const path = t.id === "sTest" ? "/admin/datasources/test" : "/admin/datasources";
    t.disabled = true;
    try {
      const r = await api(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      // The test answers 200 even when the source is unusable: the request
      // succeeded and the answer is "no". So the outcome is read from the body.
      $("sResult").innerHTML = `<div class="row" style="margin:0">${
        pill(r.outcome || (r.name ? "registered" : "done"))}<span style="font-size:13.5px">${
        h(r.message || `Registered as ${r.name}.`)}</span></div>`;
      if (t.id === "sAdd") { $("sName").value = ""; $("sConn").value = ""; await loadSources(); }
    } catch (e) { $("sResult").innerHTML = `<div class="row" style="margin:0">${
      pill("unusable")}<span style="font-size:13.5px">${h(e.message)}</span></div>`; }
    t.disabled = false;
  }
}

document.addEventListener("change", async event => {
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

  unsaved.set(editing.name, editedValues());
  markUnsaved(true);
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

  body.innerHTML = rows.map(({ layer, place, results, said }) => `
    <tr>
      <td><code>${h(layer.name)}</code><br><span class="val">${h(place.service)} · ${place.id}</span></td>
      <td>${pill(layer.sharing)}</td>
      ${results.map(codeCell).join("")}
      <td${said.bad ? ' class="bad-inline"' : ' class="val"'}>${h(said.text)}</td>
    </tr>`).join("")
    || `<tr><td colspan="6" class="empty">You own nothing and nothing is shared with you, so
          there was nothing to probe.</td></tr>`;

  const exposed = rows.filter(r => r.said.bad && r.layer.sharing !== "public").length;
  const unreachable = rows.filter(r => r.said.bad && r.layer.sharing === "public").length;

  $("anonSummary").innerHTML = [
    exposed
      ? `<p class="bad-inline"><b>${exposed}</b> layer${exposed === 1 ? " is" : "s are"} readable
         without a credential while shared private or organization.</p>`
      : `<p class="val">Nothing shared private or organization answered an anonymous caller.</p>`,
    unreachable
      ? `<p class="bad-inline"><b>${unreachable}</b> layer${unreachable === 1 ? " is" : "s are"}
         shared public but did not answer — an ArcGIS client would see this as the layer missing.</p>`
      : `<p class="val">Every public layer answered.</p>`,
  ].join("");
}

/**
 * Runs a loader and, if it fails, reports it where it happened.
 *
 * The toast carries the server's message because it is the useful part; the
 * placeholder is there so the empty table is not read as "nothing published".
 */
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

start().catch(e => toast(e.message));
