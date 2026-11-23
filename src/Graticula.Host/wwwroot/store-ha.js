/* ---- System › Database — ADR-319 §5.10 -------------------------------------------------------------------------
 * The platform store's primary and standby as this machine sees them: each host's role, its timeline and the lag the
 * primary reports for its standby, and the last failover. Promoting by hand is a command on a machine of the site,
 * `graticula store promote` (§5.7), not a button: it is needed when the primary is gone, and then this page cannot
 * prove who is asking — so the page says the command instead of offering a button that would answer 401.
 * Read again every five seconds while it is on screen, because a failover is what someone opens it to watch.
 *
 * A classic script: it shares console.js's global namespace, so every name here starts with `sh` (store HA), and it
 * uses console.js's `$`, `h`, `num`, `api` and `servicePinAgo`, which exist by the time a screen is shown.
 */

/** The timer that reads the page again while it is open. */
let shTimer = null;

/** The rows last drawn, so an unchanged table is left alone and keeps the reader's focus. */
let shDrawn = "";

/** A host's role, as a person reads it, and the pill it is drawn in. */
const SH_ROLES = {
  primary: { label: "Primary", pill: "p-started", says: "Takes every write." },
  standby: { label: "Standby", pill: "p-stopped", says: "Replays the primary's changes; promoted if the primary is lost." },
  fenced: { label: "Fenced", pill: "p-failing", says: "An old primary that came back. Read-only, and nothing writes to it. Rewind it to make it the standby." },
  unreachable: { label: "Unreachable", pill: "p-failing", says: "This machine cannot reach it." },
};

/** A line under the heading, read out when it changes; `bad` marks a refusal. */
function shSay(message, bad = false) {
  const line = $("shSays");
  if (!line) return;
  line.textContent = message || "";
  line.classList.toggle("bad-inline", !!bad);
}

/** Bytes as a person reads a replication lag. */
function shBytes(n) {
  if (n == null) return "";
  if (n < 1024) return `${num(n)} bytes`;
  if (n < 1024 * 1024) return `${num(Math.round(n / 1024))} KB`;
  return `${num(Math.round(n / 1024 / 1024))} MB`;
}

/** The replication cell: what the primary reports for its standby, or what a standby's receiver is doing. */
function shReplication(host) {
  if (!host.reachable) return host.error ? h(host.error) : "Not heard from";
  const parts = [`timeline ${num(host.timeline)}`];
  if (host.role === "primary") {
    if (!host.standbys) parts.push("no standby connected");
    else {
      parts.push(host.lagBytes ? `${shBytes(host.lagBytes)} behind` : "standby up to date");
      if (host.replayLagSeconds != null) parts.push(`${host.replayLagSeconds.toFixed(1)} s replay lag`);
    }
  }
  if (host.role === "standby") parts.push(host.receiver === "streaming" ? "streaming" : `receiver ${host.receiver || "stopped"}`);
  return parts.map((p, i, a) => `<span class="mcmeta">${h(p)}${i < a.length - 1 ? `<span class="sep" aria-hidden="true">·</span>` : ""}</span>`).join(" ");
}

/** System › Database — the hosts, the last failover and whether failover is held. */
async function shLoad() {
  let report;
  try {
    report = await api("/admin/site/store") || {};
  } catch (e) {
    shDrawn = null;
    throw e;
  }

  const none = $("shNone");
  const body = $("shBody");
  if (!report.configured) {
    none.hidden = false;
    body.hidden = true;
    $("shCount").textContent = "One database host";
    return;
  }
  none.hidden = true;
  body.hidden = false;

  const hosts = report.hosts || [];
  const primary = hosts.find(x => x.role === "primary");
  $("shCount").textContent = primary ? `Primary ${primary.address}` : "No primary";

  const last = report.lastFailover;
  $("shLast").innerHTML = last
    ? `Last failover ${servicePinAgo(last.at)}: <code>${h(last.from)}</code> to <code>${h(last.to)}</code>,
       ${last.how === "automatic" ? `decided by ${h(last.decidedBy)}` : `promoted by an administrator on ${h(last.decidedBy)}`}${
       last.unreachableMs != null ? `, ${num(Math.round(last.unreachableMs / 100) / 10)} s after the primary was lost` : ""}.`
    : "No failover has happened.";
  $("shMode").textContent = report.mode === "manual"
    ? "Failover is manual: when the primary is lost, an administrator promotes the standby here."
    : "Failover is automatic: the standby is promoted when it and every machine of the site agree the primary is lost.";

  // An alert is read out when its text changes, so it is written only when it does.
  const held = $("shHeld");
  // With no primary, how a person ends the wait: the command, on a machine of the site (§5.7).
  const heldText = report.held
    ? `Failover is held: ${report.held}. If the primary is gone for good, run graticula store promote on a machine of this site.`
    : primary ? "" : "No primary is reachable. If it is gone for good and the standby was not promoted, run graticula store promote on a machine of this site.";
  if (held.textContent !== heldText) held.textContent = heldText;
  held.hidden = !heldText;

  const rows = hosts.map(x => {
    const role = SH_ROLES[x.role] || SH_ROLES.unreachable;
    return `<tr data-sh="${h(x.address)}">
      <td class="name"><b><code>${h(x.address)}</code></b><div class="rowmeta">${h(role.says)}</div></td>
      <td data-l="Role"><span class="pill ${role.pill}">${h(role.label)}</span></td>
      <td data-l="Replication"><span class="rowmeta">${shReplication(x)}</span></td>
    </tr>`;
  }).join("");

  if (rows !== shDrawn) {
    $("shRows").innerHTML = rows;
    shDrawn = rows;
  }

  clearTimeout(shTimer);
  shTimer = setTimeout(() => {
    if ($("view-database")?.classList.contains("on") && document.visibilityState === "visible") shLoad().catch(() => {});
  }, 5000);
}

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible" && $("view-database")?.classList.contains("on")) shLoad().catch(() => {});
});
