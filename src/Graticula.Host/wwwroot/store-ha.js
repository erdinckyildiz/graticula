/* ---- System › Database — ADR-319 §5.10 -------------------------------------------------------------------------
 * The platform store's primary and standby as this machine sees them: each host's role, how far the standby is behind,
 * and the last failover. Promoting by hand is a command on a machine of the site, `graticula store promote` (§5.7), not
 * a button: it is needed when the primary is gone, and then this page cannot prove who is asking — so the page says the
 * command instead of offering a button that would answer 401.
 * Read again every five seconds while it is on screen, because a failover is what someone opens it to watch — and a
 * read that fails is exactly what a failover looks like, so a failure keeps the reading going and says so (design
 * review of 2026-11-23, findings 1 and 2).
 *
 * A classic script: it shares console.js's global namespace, so every name here starts with `sh` (store HA), and it
 * uses console.js's `$`, `h`, `num` and `api`, which exist by the time a screen is shown.
 */

/** The timer that reads the page again while it is open. */
let shTimer = null;

/** Each row as last drawn, by host, so only what changed is touched and a reader keeps their place (finding 7b). */
const shDrawn = new Map();

/** The roles last seen, by host, so a change of primary is said once (finding 7a). */
let shRolesSeen = null;

/** When the hosts were last read. */
let shReadAt = null;

/** Whether the line under the heading is saying a failed read, so the next reading that works clears it. */
let shFailing = false;

/** The callout's source as last written. The browser's serialised innerHTML never equals it, so it is compared here. */
let shHeldSaid = null;

/** Beyond either of these the standby is called lagging. INFERRED (ADR-319 §5.12): no source names a threshold. */
const SH_LAG_SECONDS = 60;
const SH_LAG_BYTES = 64 * 1024 * 1024;

/** The deployment guide's section on a standby, which every call to action on this page points at (finding 8). */
const SH_GUIDE = "https://github.com/erdinckyildiz/graticula/blob/main/docs/deployment.md#16-a-standby-database-promoted-when-the-primary-is-lost-adr-319";

/** A host's role, as a person reads it, and the pill it is drawn in. The standby's sentence depends on the mode. */
const SH_ROLES = {
  primary: { label: "Primary", pill: "p-started", says: () => "Takes every write." },
  standby: {
    label: "Standby", pill: "p-info",
    says: mode => mode === "manual"
      ? "A copy that follows every change. Promoted by an administrator if the primary is lost."
      : "A copy that follows every change. Promoted automatically if the primary is lost.",
  },
  fenced: {
    label: "Fenced", pill: "p-failing",
    says: () => "An old primary that came back. Read-only, and nothing writes to it.",
  },
  unreachable: { label: "Unreachable", pill: "p-failing", says: () => "This machine cannot reach it." },
};

/** A line under the heading, read out when it changes; `bad` marks a failure. */
function shSay(message, bad = false) {
  const line = $("shSays");
  if (!line) return;
  if (line.textContent !== (message || "")) line.textContent = message || "";
  line.classList.toggle("bad-inline", !!bad);
}

/** Bytes as a person reads a replication lag. */
function shBytes(n) {
  if (n == null) return "";
  if (n < 1024) return `${num(n)} bytes`;
  if (n < 1024 * 1024) return `${num(Math.round(n / 1024))} KB`;
  if (n < 1024 * 1024 * 1024) return `${num(Math.round(n / 1024 / 1024))} MB`;
  return `${num(Math.round(n / 1024 / 1024 / 102.4) / 10)} GB`;
}

/** Seconds as a person reads a delay: "0.6 s", "4 min 10 s", "3 h 26 min", "2 days 4 h". */
function shDuration(s) {
  if (s == null) return "";
  if (s < 10) return `${num(Math.round(s * 10) / 10)} s`;
  if (s < 60) return `${num(Math.round(s))} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${num(m)} min${s % 60 >= 1 ? ` ${num(Math.floor(s % 60))} s` : ""}`;
  const hrs = Math.floor(m / 60);
  if (hrs < 48) return `${num(hrs)} h${m % 60 ? ` ${num(m % 60)} min` : ""}`;
  return `${num(Math.floor(hrs / 24))} days${hrs % 24 ? ` ${num(hrs % 24)} h` : ""}`;
}

/** A moment as a date: "20 Nov 2026, 10:14". Plain text, and steady, so a live region holding it is not rewritten. */
function shStamp(at) {
  const d = new Date(at);
  if (Number.isNaN(d.getTime())) return String(at);
  return d.toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

/** How long ago, in plain text, in days beyond 48 hours: "50 seconds ago", "3 hours ago", "3 days ago". */
function shAgo(at) {
  const s = (Date.now() - new Date(at).getTime()) / 1000;
  if (!(s >= 0)) return "";
  if (s < 90) return `${num(Math.round(s))} seconds ago`;
  if (s < 90 * 60) return `${num(Math.round(s / 60))} minutes ago`;
  if (s < 48 * 3600) return `${num(Math.round(s / 3600))} hours ago`;
  return `${num(Math.round(s / 86400))} days ago`;
}

/** A moment as an ago and a date: "3 days ago (20 Nov 2026, 10:14)" (finding 11). */
function shWhen(at) {
  return h(`${shAgo(at)} (${shStamp(at)})`);
}

/** A clock time for the status line. */
function shClock(d) {
  return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

/**
 * How far behind the standby is, on the standby's own row — where someone who ran ArcGIS Data Store looks (finding 5).
 * The primary reports it; it is drawn against the standby it is about. Returns the sentence and whether it is lagging.
 */
function shBehind(primary) {
  if (!primary || !primary.standbys) return null;
  const secs = primary.replayLagSeconds;
  const bytes = primary.lagBytes;
  if (!bytes && !secs) return { text: "Up to date", lagging: false };
  const lagging = (secs != null && secs > SH_LAG_SECONDS) || (bytes != null && bytes > SH_LAG_BYTES);
  const parts = [];
  if (secs != null) parts.push(`${shDuration(secs)} behind`);
  if (bytes) parts.push(secs != null ? `(${shBytes(bytes)})` : `${shBytes(bytes)} behind`);
  return { text: parts.join(" "), lagging, secs };
}

/** The replication cell for one host. */
function shReplication(host, primary) {
  if (!host.reachable) return h(host.error || "Not heard from");
  if (host.role === "primary") {
    return host.standbys ? `Sending changes to ${num(host.standbys)} ${host.standbys === 1 ? "standby" : "standbys"}` : "No standby connected";
  }
  if (host.role === "standby") {
    const receiving = host.receiver === "streaming" ? "Receiving changes" : "Not receiving changes (waiting for the primary)";
    const behind = shBehind(primary);
    if (!behind) return h(receiving);
    const pill = behind.lagging ? ` <span class="pill p-stopped">Lagging</span>` : "";
    // One element in the cell: a phone's `td[data-l]` is a flex row, and loose children would each become a column
    // (second render, DB-N3).
    const risk = behind.lagging && behind.secs != null
      ? `<div class="rowmeta">If the primary is lost now, about ${h(shDuration(behind.secs))} of changes are lost.</div>` : "";
    return `<div class="shrep">${h(receiving)}<span class="sep" aria-hidden="true"> · </span><span data-sh-lag>${h(behind.text)}</span>${pill}${risk}</div>`;
  }
  if (host.role === "fenced") return "Read-only until it is rewound";
  return "";
}

/** One host's row. */
function shRow(x, mode, primary) {
  const role = SH_ROLES[x.role] || SH_ROLES.unreachable;
  return `<tr data-sh="${h(x.address)}">
      <td class="name"><b>${h(x.address)}</b><div class="rowmeta">${h(role.says(mode))}${
        x.timeline != null ? ` <span class="sep" aria-hidden="true">·</span> timeline ${num(x.timeline)}` : ""}</div></td>
      <td data-l="Role"><span class="pill ${role.pill}">${h(role.label)}</span></td>
      <td data-l="Replication">${shReplication(x, primary)}</td>
    </tr>`;
}

/**
 * Puts the rows in, touching only those whose text changed, so a screen reader's place and any focus survive a
 * reading where only the lag moved (finding 7b).
 */
function shDraw(hosts, mode, primary) {
  const tbody = $("shRows");
  const wanted = hosts.map(x => [x.address, shRow(x, mode, primary)]);
  const sameSet = wanted.length === shDrawn.size && wanted.every(([a]) => shDrawn.has(a));
  if (!sameSet) {
    tbody.innerHTML = wanted.map(([, r]) => r).join("");
    shDrawn.clear();
    for (const [a, r] of wanted) shDrawn.set(a, r);
    return;
  }
  for (const [a, r] of wanted) {
    if (shDrawn.get(a) === r) continue;
    const old = [...tbody.querySelectorAll("tr[data-sh]")].find(tr => tr.getAttribute("data-sh") === a);
    if (old) old.outerHTML = r;
    shDrawn.set(a, r);
  }
}

/** The callout: held, no primary, or no standby since a failover — the page's one job, drawn as a callout (finding 3). */
function shCallout(report, hosts, primary) {
  const held = $("shHeld");
  const cmd = `<code class="shcmd">graticula store promote</code>`;
  let html = "";
  if (report.held) {
    html = `<p><b>Failover is held:</b> ${h(report.held)}.</p>
      <p>If the primary is gone for good, run this on a machine of the site:</p>${cmd}`;
  } else if (!primary) {
    html = `<p><b>No primary is reachable.</b> If it is gone for good and the standby was not promoted, run this on a
      machine of the site:</p>${cmd}`;
  } else if (hosts.length > 1 && !primary.standbys) {
    const fenced = hosts.find(x => x.role === "fenced");
    // The date, not an age: an age would change the alert's text every minute and have it read out again (DB-N1).
    const since = report.lastFailover ? ` since the failover of ${h(shStamp(report.lastFailover.at))}` : "";
    html = `<p><b>The site has no standby${since}.</b> ${fenced
      ? `${h(fenced.address)} is fenced; rewind it with <code>pg_rewind</code> and start it as the standby.`
      : "Start the standby again so the database has a copy to take over."}
      <a href="${SH_GUIDE}" target="_blank" rel="noopener">How (deployment guide, 1.6)</a></p>`;
  }
  // An alert is read out when its content changes, so it is written only when its source does (DB-N1): rewriting it
  // each poll had it read out every five seconds and took the focus off the link inside it.
  if (shHeldSaid !== html) { held.innerHTML = html; shHeldSaid = html; }
  held.hidden = !html;
}

/** Says a change of primary once, in the live line (finding 7a). */
function shAnnounce(hosts) {
  const roles = new Map(hosts.map(x => [x.address, x.role]));
  if (shRolesSeen) {
    const now = hosts.find(x => x.role === "primary");
    if (now && shRolesSeen.get(now.address) !== "primary") shSay(`${now.address} is now the primary.`);
  }
  shRolesSeen = roles;
}

/** Reads the hosts and draws them. Throws on a failed read; `shPoll` keeps the page reading either way. */
async function shRead() {
  const report = await api("/admin/site/store") || {};
  // The failure said before goes as soon as a read works, before anything new is said, so that a new primary said below
  // is not wiped with it (DB-N4) — on a failover the read before the new primary is exactly the one that failed.
  if (shFailing) { shFailing = false; shSay(""); }
  const none = $("shNone");
  const body = $("shBody");
  if (!report.configured) {
    none.hidden = false;
    body.hidden = true;
    $("shCount").textContent = "1 host, no standby";
    return;
  }
  none.hidden = true;
  body.hidden = false;

  const hosts = report.hosts || [];
  const primary = hosts.find(x => x.role === "primary");
  const standbys = hosts.filter(x => x.role === "standby").length;
  $("shCount").textContent = `${num(hosts.length)} hosts, ${primary ? "1 primary" : "no primary"}, ${standbys ? `${num(standbys)} standby` : "no standby"}`;

  const last = report.lastFailover;
  $("shLast").innerHTML = last
    ? `Last failover ${shWhen(last.at)}: ${h(last.from)} to ${h(last.to)},
       ${last.how === "automatic" ? `decided by ${h(last.decidedBy)}` : `promoted by an administrator on ${h(last.decidedBy)}`}${
       last.unreachableMs != null ? `, ${num(Math.round(last.unreachableMs / 100) / 10)} s after the primary was lost` : ""}.`
    : "No failover has happened.";
  $("shMode").textContent = report.mode === "manual"
    ? "Failover is manual: when the primary is lost, an administrator runs graticula store promote on a machine of this site."
    : "Failover is automatic: the standby is promoted when it and every machine of the site agree the primary is lost.";

  shCallout(report, hosts, primary);
  shDraw(hosts, report.mode, primary);
  shAnnounce(hosts);
}

/** Why a read failed, in words: a failover in progress, or the refusal itself (finding 2). */
async function shWhy(e) {
  try {
    const r = await fetch("/healthz/ready", { cache: "no-store" });
    if (r.status === 503) {
      const said = await r.json().catch(() => ({}));
      if (said.state === "store-failover") return { failingOver: true, text: "The database is failing over." };
      if (said.state) return { failingOver: false, text: `The server is not ready (${said.state}${said.reason ? `: ${said.reason}` : ""}).` };
    }
  } catch { /* the server itself is not answering */ }
  return { failingOver: false, text: `Could not read the database hosts (${e.message || e}).` };
}

/** One reading, and the next one scheduled whether it worked or not (finding 1). */
async function shPoll() {
  clearTimeout(shTimer);
  try {
    await shRead();
    shReadAt = new Date();
    $("shAge").textContent = `Read at ${shClock(shReadAt)}. Read again every 5 seconds.`;
  } catch (e) {
    const why = await shWhy(e);
    shFailing = true;
    const kept = shReadAt ? ` Showing what was read at ${shClock(shReadAt)}.` : "";
    shSay(why.failingOver
      ? `${why.text} This page reads again every 5 seconds; nothing needs doing yet.${kept}`
      : `${why.text} Trying again every 5 seconds.${kept}`, !why.failingOver);
  } finally {
    shTimer = setTimeout(() => {
      if ($("view-database")?.classList.contains("on") && document.visibilityState === "visible") shPoll();
    }, 5000);
  }
}

/** System › Database, from the console's screen switch. Never throws: its failures are said on the page itself. */
async function shLoad() {
  await shPoll();
}

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible" && $("view-database")?.classList.contains("on")) shPoll();
});
