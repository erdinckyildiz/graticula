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

const SCOPES = ["private", "organization", "public"];

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
        <td class="name">${h(g.title || g.name)}${g.title && g.title !== g.name
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
  const chosen = shown.find(g => g.name === groupOpen)
    || groups.find(g => g.name === groupOpen)
    || null;

  if (!chosen) { $("groupEditor").hidden = true; return; }

  $("groupEditor").hidden = false;

  const one = await api(`/admin/groups/${encodeURIComponent(chosen.name)}`) || {};

  $("groupEditorName").innerHTML = h(one.title || one.name || chosen.name);

  // <b>Facts as a fact list, which is the reference's Details block reduced to what we have.</b>
  // Standing and capability were two paragraphs of grey `hint` — the same weight as a footnote, and
  // fifty-five words to deliver one. The irreversibility keeps a sentence, because it is the only
  // part a reader has to be persuaded of rather than told.
  $("groupStanding").innerHTML = `
    <dl class="facts2">
      <dt>You are</dt><dd>${chosen.standing === "owner"
        ? `its owner <span class="val">— you may delete it; you cannot leave it</span>`
        : chosen.standing === "manager"
          ? `a manager <span class="val">— you may add members and share services</span>`
          : `a member <span class="val">— you read what is shared with it</span>`}</dd>
      <dt>Owner</dt><dd>${h(one.owner || "—")}</dd>
      <dt>Confers</dt><dd>${one.itemUpdate === "allItems"
        ? `editing every service shared with it`
        : one.itemUpdate === "ownItems"
          ? `editing the services a member shared themselves`
          : `reading only`}<span class="val"> — fixed when the group was created</span></dd>
    </dl>
    <p class="hint">The capability cannot be changed: widening it would make every service already
      shared with the group editable by every member, retroactively (ADR-036 §4c). To change it, make
      another group and move the shares.</p>`;

  $("groupCapability").innerHTML = "";

  const members = one.members || [];
  const items = one.items || [];

  $("groupMemberCount").textContent = members.length === 0 ? "" : `${members.length}`;

  $("groupMembers").innerHTML = members.length === 0
    ? `<tr><td colspan="3" class="empty">Nobody yet. <b>Add member</b> offers everybody who is not
         in it — they will read whatever is shared with the group.</td></tr>`
    : members.map(m => `
      <tr>
        <td class="name">${h(m.name)}</td>
        <td>${m.standing === "member"
          ? `<span class="val">member</span>`
          : `<b>${h(m.standing)}</b>`}</td>
        <td style="text-align:right">${chosen.mayManage && m.standing !== "owner" ? `
          <button class="tiny" data-group-grade="${h(m.name)}"
            data-to="${m.standing === "manager" ? "member" : "manager"}"
            title="${m.standing === "manager"
              ? "A manager may add members and share services; make them a plain member"
              : "A manager may add members and share services, and may not delete the group"}"
            >Make ${m.standing === "manager" ? "member" : "manager"}</button>
          <button class="tiny danger" data-group-drop="${h(m.name)}">Remove</button>` : ""}</td>
      </tr>`).join("");

  // <b>Which shares actually reach anybody, shown rather than warned about.</b> A service reaches a
  // group's members only when its own scope is `group` as well, and that was prose in two places —
  // a per-service fact delivered as a per-screen caveat, which the operator then has to carry to
  // another page and check one at a time. The server already knows: `items` carries each service's
  // own scope.
  const reaching = items.filter(i => i.sharing === "group").length;

  $("groupItemCount").textContent = items.length === 0
    ? ""
    : reaching === items.length
      ? `${items.length}`
      : `${reaching} of ${items.length} reaching members`;

  $("groupItems").innerHTML = items.length === 0
    ? `<tr><td colspan="3" class="empty">Nothing shared with it yet. <b>Share a service</b> offers
         what you have published — and a service reaches these members only once its own sharing
         scope is <b>group</b>, which is set on the service's Sharing page.</td></tr>`
    : items.map(i => `
      <tr>
        <td class="name">${h(i.name)}</td>
        <td>${i.sharing === "group"
          ? `<span class="val">reaching members</span>`
          : `${pill(i.sharing)} <span class="val">inert here</span>`}</td>
        <td style="text-align:right">${chosen.mayManage
          ? `<button class="tiny danger" data-group-unshare="${h(i.name)}">Stop sharing</button>`
          : ""}</td>
      </tr>`).join("");

  // <b>Only what a manager may act on gets controls.</b> ADR-034 condition 1: a screen must not
  // offer what its reader cannot do, and a plain member of a group is a reader here.
  $("groupActions").hidden = !chosen.mayManage;
  $("groupDelete").hidden = !chosen.mayDelete;
  $("groupPicker").hidden = true;

  // <b>Absent for the owner.</b> The store refuses to remove them — they would keep owning a group
  // that a membership-filtered list omits — so the button would be a refusal waiting to happen.
  $("groupLeave").hidden = chosen.standing === "owner";
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
        <td class="name">${h(r.name)}${r.builtIn
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

  $("roleCount").textContent =
    `${roles.length} role${roles.length === 1 ? "" : "s"}, `
    + `${catalogue.length} privilege${catalogue.length === 1 ? "" : "s"}`;

  if (!chosen) { $("roleEditor").hidden = true; return; }

  $("roleEditor").hidden = false;
  $("roleEditorName").innerHTML = `<b>${h(chosen.name)}</b>`;

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
    action: { id: "newLayer", label: "New layer" },
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
  service: "server",

  // <b>Deliberately absent: `layer`.</b> It is the one screen that lives in both surfaces, and
  // naming a single owner here is what sent every sharing link to Server. Which surface a layer's
  // page belongs to is `LAYER_PAGES`, and the router asks that instead.
  operations: "server",
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

  const rest = location.hash.replace(/^#\/?/, "").split("/").filter(Boolean)
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

  // A service, and what is in it. The address carries the folder because a service is
  // addressed by folder and name — `#/service/turkiye/tr_ref`.
  if (rest[0] === "service" && rest[1]) {
    showService(rest.slice(1).join("/"));
    return;
  }

  const screens = SURFACES[surface].tabs.map(([name]) => name);
  const screen = screens.includes(rest[0]) ? rest[0] : SURFACES[surface].home;

  // The folder a Server services screen is looking at, which is part of its address so that
  // "the services in turkiye" is a place you can link somebody to.
  openScreen(surface, screen, screen === "services" ? rest[1] ?? null : null);
}

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

  // <b>The action goes to whichever page head is on screen.</b> Each surface's home screen has a
  // slot; naming them apart and asking for both is what keeps the router from having to know which
  // view is visible.
  const markup = config.action
    ? `<button class="primary" id="${config.action.id}"><span class="plus"
         aria-hidden="true">+</span>${h(config.action.label)}</button>`
    : "";

  for (const id of ["pageAction", "pageActionContent"]) {
    const slot = $(id);
    if (slot) slot.innerHTML = markup;
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
function toMap() {
  // Studio's content screen is where the map is, and it is another *path* now — so this is a
  // navigation rather than a hash change when the reader is in Server.
  if (surfaceOfPath() === "studio") location.hash = "#/content";
  else location.assign(surfaceHref("studio", "content"));
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

  if (screen === "content") section("your content", loadMyContent, "mine");
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
async function loadMyContent() {
  const { mine, shared, note } = await api("/content/layers");

  content = new Map([...(mine || []), ...(shared || [])].map(e => [e.name, e]));

  const row = (e, last) => {
    const isShown = shown.has(e.name);
    const stopped = e.status === "stopped";
    const swatch = isShown
      ? `<i class="swatch" style="background:${h(shown.get(e.name).colour)}"></i>` : "";

    return `<tr class="pick" data-pick="${h(e.name)}">
      <td class="acts">${swatch}<button class="tiny ${isShown ? "on" : ""}"
        data-show="${h(e.name)}" ${stopped
          ? "disabled title='A stopped service answers 503, so there is nothing to draw.'"
          : ""}>${isShown ? "Hide" : "Map"}</button>${e.hosted
        ? `<button class="tiny ${shown.has(tileKey(e.name)) ? "on" : ""}"
             data-tiles="${h(e.name)}" ${stopped ? "disabled" : ""}>Tiles</button>` : ""}</td>
      <td class="name">${h(e.name)}</td>
      <td>${pill(e.hosted ? "hosted" : "registered")}${
        // The folder, but not when it merely repeats the word beside it: every hosted layer
        // is in `hosted`, so saying both is noise in a column that has one job.
        e.folder && e.folder !== "hosted"
          ? ` <span class="val">${h(e.folder)}</span>` : ""}</td>
      <td>${pill(e.status)}</td>
      <td>${pill(e.sharing)}</td>
      <td class="val">${last(e)}</td>
    </tr>`;
  };

  const address = e => `<a href="${h(e.url)}?f=json" target="_blank" rel="noreferrer">${
    h(e.service)}/FeatureServer/${e.layerId}</a>`;

  $("mine").innerHTML = (mine || []).length === 0
    ? `<tr><td colspan="6" class="empty">${h(note || "Nothing yet.")} <b>New layer</b> publishes
         one.</td></tr>`
    : mine.map(e => row(e, address)).join("");

  $("sharedWithMe").innerHTML = (shared || []).length === 0
    ? `<tr><td colspan="6" class="empty">Nothing is shared with you by anybody else.</td></tr>`
    : shared.map(e => row(e, x => h(x.because))).join("");
}

/**
 * One service, and the layers it holds.
 *
 * <b>Read from the service document, not from a new admin route.</b> The document is what every
 * ArcGIS client reads to find a service's layers, so this screen and every client agree by
 * construction — and a stopped service refusing it is shown in place, because that refusal is
 * expected rather than a fault.
 */
async function showService(qualified) {
  editing = null;
  showView("view-service", "services");

  const { folder, name } = splitService(qualified);

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

    return;
  }


  // <b>The service's own settings, on the service.</b> Rendered before the layer list, because they
  // are what this page is now for: the list below says what is inside, and these say what the
  // container offers. Same `.setting` rows and `h4` groups as everywhere else.
  drawServiceSettings(name, folder);

  try {
    const doc = await api(
      `/rest/services/${qualified.split("/").map(encodeURIComponent).join("/")}/FeatureServer?f=json`);

    const layers = doc.layers || [];

    $("serviceFacts").textContent =
      `${layers.length} layer${layers.length === 1 ? "" : "s"} · max ${num(doc.maxRecordCount)} rows`
      + ` · ${doc.capabilities || "no capabilities"}`;

    // The layer list was here until 2026-08-18, when the owner asked for it to go: this page is
    // the service's settings, and the counts are in the facts line above. What went with it is a
    // route — a layer's own page is reachable from Studio's content list and by its address, and no
    // longer from Server.
  } catch (e) {
    $("serviceFacts").textContent = "";
    toast(`${qualified}: ${e.message || e}`);
  }
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

  $("servicePagesBody").innerHTML = serviceSettingsMarkup(name, folder)
    + `<div class="row" style="margin-top:22px">
         <button class="primary" data-service-save="${h(name)}"
           data-folder="${h(folder || "")}">Save</button>
       </div>`;

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
  return `
    <section class="page" id="page-capabilities">
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
    </section>

    <section class="page" id="page-sharing">
      <h4>Who may read this service</h4>
      <div class="setting"><span class="q">Sharing scope:</span>
        <select id="capSharing" data-service-sharing="${h(name || "")}"
          data-folder="${h(folder || "")}">${SCOPES.map(v =>
          `<option value="${v}">${v}</option>`).join("")}</select></div>
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

  $("members").innerHTML = pageOf("members", rows).map(m => `
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
async function loadServices() {
  const [{ services }, system] = await Promise.all([
    api("/admin/featureservices"),
    api("/admin/services").catch(() => ({ services: [] })),
  ]);

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
          ? `<canvas class="thumb" width="104" height="74"
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
          : `<a href="#/service/${r.qualified.split("/").map(encodeURIComponent).join("/")}"
               data-open-service-page="sharing"
               title="Set on this service's Sharing page — a scope is its owner's decision">${
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
  const trail = [`<a href="#/services">Services</a>`];

  if (at) {
    trail.push(`<a href="#/services${at.folder ? "/" + encodeURIComponent(at.folder) : ""}">${
      h(at.folder || "Site (root)")}</a>`);

    // Only when the service holds something else. For a service of one layer this page *is*
    // the service, and a link to a single-row table is the step the owner asked us to drop.
    const siblings = known.filter(k => k.service === at.bare
      && (k.folder || null) === at.folder).length;

    if (siblings > 1) {
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

async function loadSources() {
  const { dataSources } = await api("/admin/datasources");
  $("cSources").textContent = dataSources.length;

  $("sourcesPager").innerHTML = pagerFor("sources", dataSources.length);

  $("sources").innerHTML = dataSources.length === 0
    ? `<tr><td colspan="5" class="empty">None registered.</td></tr>`
    : pageOf("sources", dataSources).map(d => `<tr>
        <td class="name">${h(d.name)}</td>
        <td class="val">${h(d.kind)}</td>
        <td class="num">${num(d.layerCount)}</td>
        <td class="val">${h(d.id || "")}</td>
        <td style="text-align:right"><button data-probe="${h(d.id)}"
          data-probe-name="${h(d.name)}">Probe</button></td>
      </tr>`).join("");
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

function openNewLayer() {
  // Retitled 2026-08-16: it held only hosted layers, and now it also publishes a
  // table this server does not hold and creates services and groups. A heading
  // that names one of four things is worse than a general one.
  $("drawerTitle").textContent = "Create";
  $("drawerSub").textContent = "a layer, a service, or a group inside one";
  $("drawerBody").innerHTML = `
    <div class="group">
      <h3>Design a schema</h3>
      <p class="hint">For data you are going to collect. Creates an empty feature class you
        fill through the feature service. <code>objectid</code> and <code>geom</code> are made
        for you, stored in Web Mercator so the layer can serve tiles.</p>
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
          <button class="primary" type="submit">Create empty layer</button>
        </div>
      </form>
    </div>

    <div class="group">
      <h3>Import a file</h3>
      <p class="hint">For data you already have. Reads the schema from the file. GeoJSON only —
        a shapefile is a ZIP and this server does not open archives (Q-98). Coordinates must be
        WGS 84 longitude, latitude; they are reprojected to Web Mercator once on the way in.</p>
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
          <label class="field">GeoJSON<input id="iFile" type="file"
            accept=".json,.geojson,application/geo+json" required></label>
        </div>
        <button class="primary" type="submit">Import and publish</button>
      </form>
    </div>

      // Empty, required, and not prefilled — which is a decision from the
      // register rather than a UI preference. Q-57: identity for a registered
      // table is "declared, not inferred", and the administrator nominates the
      // column. Filling this from the probe would make the server's inference
      // look like the operator's choice, and this is the column that decides
      // which row an edit lands on.
    <div class="group">
      <h3>Publish a registered table</h3>
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
        <button class="primary" type="submit">Publish</button>
      </form>
    </div>

    <div class="group">
      <h3>A service, and groups inside one</h3>
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

      // The groups a service already has, listed here and removable here. The place
      // something is made is the place to unmake it: there is no other screen a group
      // belongs to, since a group holds no data and has no settings of its own.
      <form id="grpForm" autocomplete="off" style="margin-top:16px">
        <div class="row">
          <label class="field" style="flex:2 1 220px">Group inside
            <input type="text" id="gService" list="serviceNames" placeholder="hosted/EarlyAlert" required></label>
          <label class="field">Group name<input type="text" id="gName" placeholder="Reports" required></label>
          <label class="field">Nest under <span class="val">(layer id)</span>
            <input type="number" id="gParent" min="0" placeholder="top level"></label>
        </div>
        <button type="submit">Create group layer</button>
        <div id="gExisting" class="val" style="margin-top:10px"></div>
      </form>
    </div>

    <div id="newResult" class="group" style="display:none"></div>`;

  $("designForm").addEventListener("submit", createDesigned);
  $("importForm").addEventListener("submit", createImported);
  $("regForm").addEventListener("submit", publishRegistered);
  $("svcForm").addEventListener("submit", createService);
  $("grpForm").addEventListener("submit", createGroupLayer);
  $("gService").addEventListener("change", event => showServiceGroups(event.target.value));
  $("rSource").addEventListener("change", loadRegisteredTables);
  $("rTable").addEventListener("change", showChosenTable);
  $("dAdd").addEventListener("click", () => addFieldRow());
  addFieldRow();

  // Both lists are filled after the drawer is on screen. A form that cannot be
  // used until a request finishes should say so in its own control rather than
  // hold the drawer shut.
  section("connections", fillConnectionChoices);
  section("service names", fillServiceChoices);

  $("drawer").classList.add("on");
  $("drawer").setAttribute("aria-hidden", "false");
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

  // No Content-Type header: the browser sets it with the multipart boundary,
  // and setting it by hand produces a body the server cannot parse.
  try {
    reportNew(await api("/admin/hosted/import", { method: "POST", body }));
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
  if (t.id === "newLayer" || t.id === "newService") { openNewLayer(); return; }

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
  const control = t.closest("button, select, input, textarea, a, label");

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
    const drawn = !shown.has(tileKey(d.tiles));
    if (drawn) await showTiles(d.tiles);
    else hide(tileKey(d.tiles));
    await loadLayers();
    if (drawn) toMap();
    return;
  }

  if (d.show) {
    if (shown.has(d.show)) { hide(d.show); await loadLayers(); return; }
    t.disabled = true;
    try {
      // The whole document, because the SDK will read it anyway and this console now
      // takes its colour from the server's `drawingInfo` rather than choosing one.
      const doc = await api(
        `${serviceRoot(layerNamed(d.show)).replace(location.origin, "")}/FeatureServer/0?f=json`);
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
  const groupRow = t.closest?.("tr[data-group]");

  if (groupRow) {
    groupOpen = groupRow.dataset.group;
    await section("groups", loadGroups, "groupRows");

    // <b>Because the panel it opens is below the fold.</b> Ten rows at ~51px each put the editor's
    // heading near 780px, so on a 1366×768 window the operator clicks a row and nothing visible
    // changes. `nearest` rather than `start`: if it is already on screen, do not move the page.
    $("groupEditor")?.scrollIntoView({ block: "nearest", behavior: "smooth" });
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
    $("groupPicker").innerHTML = `
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

    $("groupPicker").hidden = false;
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
      $("groupPicker").hidden = true;
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

  if (t.id === "groupPickCancel") {
    $("groupPicker").hidden = true;
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
    await section("groups", loadGroups, "groupRows");
    return;
  }

  // <b>The services you may share are your own content, which is a list the server already
  // offers.</b> `/content/layers` is what any signed-in member may read about their own things —
  // no new endpoint, and the set is right rather than merely available: you share what you
  // published.
  if (t.id === "groupShare") {
    if (!groupOpen) return;

    const content = await api("/content/layers") || {};

    // <b>Services, not layers.</b> The listing is per layer and a group is shared a *service*, so
    // three layers of one service must offer one choice rather than three.
    const services = [...new Set((content.mine || []).map(l =>
      l.folder ? `${l.folder}/${l.service}` : l.service))].sort();

    if (services.length === 0) {
      toast("You have published nothing to share.");
      return;
    }

    $("groupPicker").innerHTML = `
      <div class="picker">
        <label for="groupPickFilter">Share a service</label>
        <input id="groupPickFilter" type="search" placeholder="Filter&hellip;" autocomplete="off">
        <select id="groupPickWhat" size="8" aria-label="Services you could share">${services.map(n =>
          `<option value="${h(n)}">${h(n)}</option>`).join("")}</select>
        <div class="row">
          <button class="primary" id="groupPickShare">Share</button>
          <button class="ghost" id="groupPickCancel">Cancel</button>
        </div>
      </div>`;

    $("groupPicker").hidden = false;
    $("groupPickFilter")?.focus();
    return;
  }

  if (t.id === "groupPickShare") {
    const what = $("groupPickWhat")?.value;
    if (!what || !groupOpen) return;

    const cut = what.split("/");
    const bare = cut.pop();
    const folder = cut.join("/");

    try {
      const done = await api(
        `/admin/groups/${encodeURIComponent(groupOpen)}/items/${encodeURIComponent(bare)}`
        + `?folder=${encodeURIComponent(folder)}`,
        { method: "PUT" });

      toast(done.note ? `${bare} shared. ${done.note}` : `${bare} shared`, true);
    } catch (e) { toast(e.message); }

    $("groupPicker").hidden = true;
    await section("groups", loadGroups, "groupRows");
    return;
  }

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

    await section("groups", loadGroups, "groupRows");
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

    await section("groups", loadGroups, "groupRows");
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

    await section("groups", loadGroups, "groupRows");
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

    await section("groups", loadGroups, "groupRows");
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

  if (t.dataset?.servicePage) {
    event.preventDefault();
    SERVICE_PAGE_OPEN = t.dataset.servicePage;
    const { folder, name } = splitService(
      ($("serviceCrumb").querySelector("b")?.textContent || "").trim());
    drawServiceSettings(name || ($("serviceCrumb").querySelector("b")?.textContent || "").trim(),
      folder);
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
  if (d.memberRole) {
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
});

document.addEventListener("input", event => {
  // <b>On `input`, and it was on `change` — so typing in it did nothing.</b> A `<input
  // type=search>` reports `change` on blur or Enter only, and `#groupFilter` twenty lines below was
  // already on `input`: two search boxes on one screen with two behaviours, which is worse than
  // either.
  if (event.target.id === "groupPickFilter") {
    const needle = event.target.value.trim().toLowerCase();

    for (const list of [$("groupPickWho"), $("groupPickWhat")]) {
      if (!list) continue;

      for (const option of list.options) {
        option.hidden = needle.length > 0
          && !option.value.toLowerCase().includes(needle);
      }
    }

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
