// System › Backups: Validate and Restore — ADR-317.
//
// <b>Its own file, and every name in it starts with `bkr`</b>: the console's classic scripts share one global namespace,
// and a second function of the same name silently replaces the first.
//
// An operation is followed by reading `/admin/backups/operations/{id}` every second and a half. While a restore runs every
// node answers everything else 503 'restoring', and that one route is still answered — so the page keeps saying where the
// restore is while the rest of the console cannot be used.
//
// <b>A restore that succeeds signs this session out</b> (design review 2026-11-23, F1): the session lives in the database
// it replaced, so every read after the switch answers 401. That is not retried — the page says the restore ended, shows
// the sign-in, and once signed in reads the operation again and says how it ended. Who may read the operation without a
// session is deliberately not widened to avoid this.

/** The operation being followed, as last read; null when none is. */
let bkrOperation = null;

/** The timer that reads it again. */
let bkrTimer = 0;

/** The sentence the status line last said, so a read that changes nothing is not announced again. */
let bkrLastSaid = "";

/** When the reads of the operation began to fail, or 0 while they answer. */
let bkrQuietSince = 0;

/** How long the reads may fail before the page says the server has gone quiet. */
const bkrQuietLimit = 120000;

/** The operation whose result is to be read once the reader has signed in again; kept across the sign-in's reload. */
const bkrAfterKey = "gis-bkr-after";

/** What the page says when a second operation is asked for while one runs; cleared when that one ends (F5). */
const bkrBusySaid = "A validation or restore is already running; this page says when it ends.";

/** What each step is called on the page. */
const bkrStepNames = {
  checks: "Checking the manifest, every file's checksum, the key and the schema",
  maintenance: "Putting every machine of the site into maintenance",
  drain: "Waiting for requests already running to finish",
  restore: "Restoring the dump into a new database",
  migrate: "Bringing the restored database up to this server's schema",
  count: "Counting what the restored copy holds",
  drop: "Dropping the scratch database",
  files: "Putting the imagery, the package files and the certificate in place",
  switch: "Switching to the restored database",
  return: "Bringing the machines back",
  rollback: "Putting the old database back in service",
  done: "Finished",
};

/** A span of time in seconds, to a tenth: `2.2 s`, `1 min 4 s`. */
function bkrSeconds(ms) {
  const s = Math.max(0, Number(ms) || 0) / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  return `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`;
}

/** How long ago a moment was, as a reader says it: `just now`, `45 minutes ago`, `2 hours ago`, `3 days ago`. */
function bkrAgo(value, now = Date.now()) {
  const at = new Date(value).getTime();
  if (Number.isNaN(at)) return "";
  const seconds = Math.max(0, (now - at) / 1000);
  const say = (n, unit) => `${n} ${unit}${n === 1 ? "" : "s"} ago`;
  if (seconds < 60) return "just now";
  if (seconds < 5400) return say(Math.max(1, Math.round(seconds / 60)), "minute");
  if (seconds < 129600) return say(Math.round(seconds / 3600), "hour");
  return say(Math.round(seconds / 86400), "day");
}

/** A moment in UTC to the second, as the dialog names what is lost: `17:29:38 UTC`. */
function bkrUtcExact(value) {
  const at = new Date(value);
  if (Number.isNaN(at.getTime())) return "";
  const two = n => String(n).padStart(2, "0");
  return `${two(at.getUTCHours())}:${two(at.getUTCMinutes())}:${two(at.getUTCSeconds())} UTC`;
}

/** A server's reason as the middle of a sentence, ending with a full stop. */
function bkrReason(error) {
  const reason = lowerFirst(String(error || "no reason was given").trim());
  return /[.!?]$/.test(reason) ? reason : `${reason}.`;
}

/** The sentence the status line says for an operation as it stands. */
function bkrSentence(op) {
  const what = op.kind === "restore" ? "The restore" : "The validation";
  const of = `of ${op.backup}`;

  if (op.state === "running") {
    return `${what} ${of} is running: ${lowerFirst(bkrStepNames[op.step] || op.step)}…`;
  }

  if (op.state === "failed") {
    return op.kind === "restore"
      ? `${what} ${of} failed${op.result?.failedAt ? ` while ${lowerFirst(bkrStepNames[op.result.failedAt] || op.result.failedAt)}` : ""}, `
        + `and the database this server used before still serves: ${lowerFirst(op.error || "no reason was given")}`
      : `${what} ${of} failed: ${bkrReason(op.error)} Validate another backup, or take a new one.`;
  }

  if (op.kind === "restore") {
    return `${what} ${of} finished. The server serves the restored database; the one it replaced is kept as `
      + `${op.result?.keptDatabase || "a database named before_restore"}.`;
  }

  const r = op.result || {};
  return `${what} ${of} finished: the backup is whole and restores — ${num(r.services ?? 0)} services, `
    + `${num(r.layers ?? 0)} layers and ${num(r.items ?? 0)} items, at schema ${r.schema ?? "—"}. The scratch copy was dropped.`;
}

/** The line under the panel's head: when it started, or when it finished and how long it took (F9, F10). */
function bkrWhen(op, now = Date.now()) {
  const started = new Date(op.started).getTime();
  const finished = new Date(op.finished).getTime();
  if (op.state === "running" || Number.isNaN(finished)) {
    return Number.isNaN(started) ? "" : `Started ${bkrAgo(op.started, now)}.`;
  }
  const verb = op.state === "failed" ? "Failed" : "Finished";
  return `${verb} ${bkrAgo(op.finished, now)}${Number.isNaN(started) ? "" : `, after ${bkrSeconds(finished - started)}`}.`;
}

/** Draws the operation's panel and says its sentence when it changed. */
function bkrDraw(op) {
  const panel = $("bkrOp");
  if (!panel || !op) return;

  panel.hidden = false;
  $("bkrOpHead").textContent = op.kind === "restore" ? `Restore of ${op.backup}` : `Validation of ${op.backup}`;
  $("bkrOpWhen").textContent = bkrWhen(op);

  // Each step with the time it began after the operation did, so steps a second apart do not all read the same minute.
  const started = new Date(op.started).getTime();
  $("bkrSteps").innerHTML = (op.steps || [])
    .filter((step, i, all) => i === all.length - 1 || step.name !== all[i + 1].name)
    .map(step => {
      const at = new Date(step.at).getTime();
      const after = Number.isNaN(started) || Number.isNaN(at) ? backupWhen(step.at) : `+${bkrSeconds(at - started)}`;
      return `<li><b>${h(bkrStepNames[step.name] || step.name)}</b>
        <span class="rowmeta">${h(after)}</span></li>`;
    }).join("");

  bkrSay(bkrSentence(op), op.state === "failed");
}

/** Puts a sentence in the panel's aria-live line, once; `bad` colours it as a failure. */
function bkrSay(said, bad) {
  const progress = $("bkrProgress");
  if (!progress) return;
  progress.classList.toggle("bad-inline", !!bad);
  if (said !== bkrLastSaid) {
    progress.textContent = said;
    bkrLastSaid = said;
  }
}

/** The busy sentence goes once the operation it was about has ended (F5). */
function bkrClearBusy() {
  const says = $("bkSays");
  if (says && says.textContent === bkrBusySaid) says.textContent = "";
}

/**
 * The session was refused while following an operation (F1): a restore that switched databases took the session with it.
 * Stops reading, says why on the page and on the sign-in, and shows the console's sign-in; `bkrNoticed` reads the result
 * once the reader is back.
 */
async function bkrSignedOut(id, kind) {
  clearTimeout(bkrTimer);
  bkrOperation = null;
  bkrQuietSince = 0;
  bkrClearBusy();
  try { if (id) sessionStorage.setItem(bkrAfterKey, id); } catch { /* no storage: the list still shows the last one */ }

  const said = kind === "restore"
    ? "The restore has ended and signed this session out, because the database was replaced. Sign in again to see the result."
    : "This session has ended. Sign in again to see how the validation ended.";
  bkrSay(said, false);

  // The token the page holds belongs to the database that was replaced; `start` finds no session and draws the sign-in.
  token = null;
  try { sessionStorage.removeItem("gis-token"); } catch { /* nothing to remove */ }
  await start();

  const line = $("signinFor");
  if (line) {
    line.textContent = said;
    line.hidden = false;
  }
  if (!$("signin")?.contains(document.activeElement)) {
    $("u")?.focus();
  }
}

/** Says, once, that the server has not answered for a while; the reads go on (F1). */
function bkrSayQuiet(kind) {
  const since = new Date(bkrQuietSince).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  bkrSay(`The server has not answered since ${since}; it may still be ${kind === "restore" ? "restoring" : "validating"}. `
    + "This page keeps asking.", false);
}

/** Reads the operation until it ends, then reads the list again. */
async function bkrFollow(id) {
  clearTimeout(bkrTimer);
  let op = null;

  try {
    op = await api(`/admin/backups/operations/${encodeURIComponent(id)}`);
    bkrQuietSince = 0;
  } catch (problem) {
    // 401 or 403: the session is gone, and reading again will not bring it back.
    if (problem?.status === 401 || problem?.status === 403) {
      await bkrSignedOut(id, bkrOperation?.kind);
      return;
    }
    // Anything else — 503 while the site restores, a connection the switch ended — is read again, and said after a while.
    if (!bkrQuietSince) bkrQuietSince = Date.now();
    else if (Date.now() - bkrQuietSince >= bkrQuietLimit) bkrSayQuiet(bkrOperation?.kind);
  }

  if (op) {
    bkrOperation = op;
    bkrDraw(op);
  }

  if (!op || op.state === "running") {
    bkrTimer = setTimeout(() => bkrFollow(id), bkrQuietSince && Date.now() - bkrQuietSince >= bkrQuietLimit ? 5000 : 1500);
    return;
  }

  bkrOperation = null;
  bkrClearBusy();
  if ($("view-backups")?.classList.contains("on")) {
    await bkrReadList(id, op.kind);
  }
}

/**
 * The list read again after an operation ended (F6). A 502, 503 or 504 — the site still coming back — is read again with
 * a growing wait before the list says it was refused; the reader's button is found again, or the page's heading is.
 */
async function bkrReadList(id, kind) {
  const focused = document.activeElement?.closest?.("#bkRows button");
  const mark = focused ? [...focused.attributes].find(a => a.name.startsWith("data-backup-")) : null;
  let last = null;

  for (let wait = 1000; wait <= 16000; wait *= 2) {
    try {
      await loadBackups();
      return;
    } catch (problem) {
      last = problem;
      if (problem?.status === 401 || problem?.status === 403) {
        await bkrSignedOut(id, kind);
        return;
      }
      if (problem?.status && ![502, 503, 504].includes(problem.status)) break;
      await new Promise(resolve => setTimeout(resolve, wait));
      if (!$("view-backups")?.classList.contains("on")) return;
    }
  }

  // Refused for good: the error row, as every list says it, and the reader's place kept.
  await section("backups", () => Promise.reject(last), "bkRows");
  const again = mark ? document.querySelector(`#bkRows [${mark.name}="${CSS.escape(mark.value)}"]`) : null;
  (again || $("bkTitle"))?.focus();
}

/** The operation a sign-in was asked for, read and said now that the reader is back (F1). */
async function bkrAfterSignIn(id) {
  try {
    const op = await api(`/admin/backups/operations/${encodeURIComponent(id)}`);
    if (!op) return;
    bkrLastSaid = "";
    if (op.state === "running") {
      bkrOperation = op;
      bkrDraw(op);
      bkrFollow(op.id);
    } else {
      bkrDraw(op);
    }
    // Signing in drew the console anew and left the reader on the page itself; their place is the result they came for.
    if (!document.activeElement || document.activeElement === document.body) $("bkrOpHead")?.focus();
  } catch (problem) {
    bkrSay(`How the operation ended could not be read: ${lowerFirst(problem?.message || String(problem))}`, true);
  }
}

/**
 * Called by the list (console.js `drawBackups`): an operation under way when the page is opened — started by somebody
 * else, or before a reload — is followed too; one that ended is shown once, not announced. One whose result the reader
 * signed in again to see is read and announced.
 */
function bkrNoticed(op) {
  let after = null;
  try {
    after = sessionStorage.getItem(bkrAfterKey);
    if (after) sessionStorage.removeItem(bkrAfterKey);
  } catch { /* no storage */ }
  if (after && !bkrOperation) {
    bkrAfterSignIn(after);
    return;
  }

  if (!op || bkrOperation) return;

  if (op.state === "running") {
    bkrOperation = op;
    bkrDraw(op);
    bkrFollow(op.id);
  } else if ($("bkrOp")?.hidden) {
    // Written while the panel is still hidden, so showing it is not an announcement of something long finished.
    bkrLastSaid = bkrSentence(op);
    $("bkrProgress").textContent = bkrLastSaid;
    bkrDraw(op);
  } else if (bkrOperation === null && $("bkrOpWhen")) {
    // Shown already: only its age moves.
    $("bkrOpWhen").textContent = bkrWhen(op);
  }
}

/** Starts an operation and follows it; the reader's place stays on the button they pressed. */
async function bkrStart(kind, name, body) {
  const says = $("bkSays");

  try {
    const started = await api(`/admin/backups/${encodeURIComponent(name)}/${kind}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body || {}),
    });
    says.textContent = "";
    bkrLastSaid = "";
    bkrQuietSince = 0;
    bkrOperation = started;
    bkrDraw(started);
    bkrFollow(started.id);
  } catch (problem) {
    if (problem?.status === 401) {
      await bkrSignedOut(null, kind);
      return;
    }
    says.textContent = `Not started: ${lowerFirst(problem.message)}`;
  }
}

/** The dialog's body: what is lost first, then who is signed out, then what is replaced and where the old one is kept. */
function bkrDialogHtml(backup, database, lastOp, now = Date.now()) {
  const ago = bkrAgo(backup.taken, now);
  const failed = lastOp && lastOp.kind === "validate" && lastOp.backup === backup.name && lastOp.state === "failed";
  return `
    <p><b>Everything changed after ${h(bkrUtcExact(backup.taken))}${ago ? ` (${h(ago)})` : ""} is lost:</b> new items, edits,
      members and settings, in every organization.</p>
    <p>Everyone signed in is signed out, you included, and every machine of the site answers nothing else until the
      restore ends.</p>`
    + (failed ? `<p class="bad-inline">The last validation of this backup failed: ${h(bkrReason(lastOp.error))}
      A restore of it is likely to fail too; if it does, the database this server uses now stays in service.</p>` : "")
    + `<p>It replaces the database <code>${h(database)}</code> with the one in the backup, taken by version
      ${h(backup.version || "—")}, and the uploaded imagery and the serving certificate with the backup's. The database
      it replaces is kept as <code>${h(database)}_before_restore_<i>time</i></code>, and the replaced files beside them,
      so there is a way back.</p>`;
}

/** Asks before a restore, naming what it replaces; resolves true when the backup's name was typed and Restore pressed. */
function bkrAsk(backup) {
  const dialog = $("bkrDialog");
  if (!dialog) return Promise.resolve(false);

  const back = document.activeElement;
  const database = backupsNow?.database || "the server's database";
  const go = $("bkrGo");
  const typed = $("bkrConfirm");
  const says = $("bkrConfirmSays");

  $("bkrDialogTitle").textContent = `Restore the backup of ${backupWhenExact(backup.taken)}?`;
  $("bkrDialogBody").innerHTML = bkrDialogHtml(backup, database, backupsNow?.operation);
  $("bkrConfirmName").textContent = backup.name;
  typed.value = "";
  typed.removeAttribute("aria-invalid");
  says.textContent = "";
  says.classList.remove("bad-inline");
  go.setAttribute("aria-disabled", "true");

  return new Promise(resolve => {
    let answered = false;
    const end = yes => {
      if (answered) return;
      answered = true;
      go.onclick = null;
      typed.oninput = null;
      typed.onkeydown = null;
      $("bkrCancel").onclick = null;
      $("bkrDialogClose").onclick = null;
      dialog.removeEventListener("close", onClose);
      if (dialog.open) dialog.close();
      if (back instanceof HTMLElement && back.isConnected) back.focus();
      resolve(yes);
    };
    const onClose = () => { if (!dialog.open) end(false); };
    const matches = () => typed.value.trim() === backup.name;

    typed.oninput = () => {
      if (matches()) go.removeAttribute("aria-disabled");
      else go.setAttribute("aria-disabled", "true");
      typed.removeAttribute("aria-invalid");
      says.textContent = "";
      says.classList.remove("bad-inline");
    };
    // aria-disabled rather than disabled, so the button can be reached and says why it does nothing yet.
    go.onclick = () => {
      if (!matches()) {
        typed.setAttribute("aria-invalid", "true");
        says.classList.add("bad-inline");
        says.textContent = "Type the backup's name exactly as shown to restore it.";
        typed.focus();
        return;
      }
      end(true);
    };
    // Enter in the name field is Restore, as in any form (F7).
    typed.onkeydown = event => {
      if (event.key !== "Enter" || event.isComposing) return;
      event.preventDefault();
      go.onclick();
    };
    $("bkrCancel").onclick = () => end(false);
    $("bkrDialogClose").onclick = () => end(false);
    dialog.addEventListener("close", onClose);

    dialog.showModal();
    typed.focus();
  });
}

// Guarded so the sentences above can be tested in Node, where there is no document (tests/studio-js).
if (typeof document !== "undefined") document.addEventListener("click", async event => {
  const t = event.target.closest?.("[data-backup-validate], [data-backup-restore]");
  if (!t) return;

  const name = t.dataset.backupValidate || t.dataset.backupRestore;
  const backup = (backupsNow?.backups || []).find(b => b.name === name);
  if (!backup) return;

  if (bkrOperation) {
    $("bkSays").textContent = bkrBusySaid;
    return;
  }

  if (t.dataset.backupValidate) {
    await bkrStart("validate", name);
    return;
  }

  if (!await bkrAsk(backup)) return;
  await bkrStart("restore", name, { confirm: name });
});

if (typeof module !== "undefined") {
  module.exports = { bkrSentence, bkrReason, bkrStepNames, bkrSeconds, bkrAgo, bkrUtcExact, bkrWhen, bkrDialogHtml };
}
