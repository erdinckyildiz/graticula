// System › Backups: Validate and Restore — ADR-317.
//
// <b>Its own file, and every name in it starts with `bkr`</b>: the console's classic scripts share one global namespace,
// and a second function of the same name silently replaces the first.
//
// An operation is followed by reading `/admin/backups/operations/{id}` every second and a half. While a restore runs every
// node answers everything else 503 'restoring', and that one route is still answered — so the page keeps saying where the
// restore is while the rest of the console cannot be used.

/** The operation being followed, as last read; null when none is. */
let bkrOperation = null;

/** The timer that reads it again. */
let bkrTimer = 0;

/** The sentence the status line last said, so a read that changes nothing is not announced again. */
let bkrLastSaid = "";

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
      : `${what} ${of} failed: ${lowerFirst(op.error || "no reason was given")}`;
  }

  if (op.kind === "restore") {
    return `${what} ${of} finished. The server serves the restored database; the one it replaced is kept as `
      + `${op.result?.keptDatabase || "a database named before_restore"}. Sign in again if the console asks.`;
  }

  const r = op.result || {};
  return `${what} ${of} finished: the backup is whole and restores — ${num(r.services ?? 0)} services, `
    + `${num(r.layers ?? 0)} layers and ${num(r.items ?? 0)} items, at schema ${r.schema ?? "—"}. The scratch copy was dropped.`;
}

/** Draws the operation's panel and says its sentence when it changed. */
function bkrDraw(op) {
  const panel = $("bkrOp");
  if (!panel || !op) return;

  panel.hidden = false;
  $("bkrOpHead").textContent = op.kind === "restore" ? `Restore of ${op.backup}` : `Validation of ${op.backup}`;
  $("bkrSteps").innerHTML = (op.steps || [])
    .filter((step, i, all) => i === all.length - 1 || step.name !== all[i + 1].name)
    .map(step => `<li><b>${h(bkrStepNames[step.name] || step.name)}</b>
      <span class="rowmeta">${h(backupWhen(step.at))}</span></li>`).join("");

  const said = bkrSentence(op);
  const progress = $("bkrProgress");
  progress.classList.toggle("bad-inline", op.state === "failed");
  if (said !== bkrLastSaid) {
    progress.textContent = said;
    bkrLastSaid = said;
  }
}

/** Reads the operation until it ends, then reads the list again. */
async function bkrFollow(id) {
  clearTimeout(bkrTimer);
  let op = null;

  try {
    op = await api(`/admin/backups/operations/${encodeURIComponent(id)}`);
  } catch {
    // A restore ends every connection to the database it replaces; the next read finds the server again.
  }

  if (op) {
    bkrOperation = op;
    bkrDraw(op);
  }

  if (!op || op.state === "running") {
    bkrTimer = setTimeout(() => bkrFollow(id), 1500);
    return;
  }

  bkrOperation = null;
  if ($("view-backups")?.classList.contains("on")) {
    await section("backups", loadBackups, "bkRows");
  }
}

/**
 * Called by the list (console.js `drawBackups`): an operation under way when the page is opened — started by somebody
 * else, or before a reload — is followed too; one that ended is shown once, not announced.
 */
function bkrNoticed(op) {
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
    bkrOperation = started;
    bkrDraw(started);
    bkrFollow(started.id);
  } catch (problem) {
    says.textContent = `Not started: ${lowerFirst(problem.message)}`;
  }
}

/** Asks before a restore, naming what it replaces; resolves true when the backup's name was typed and Restore pressed. */
function bkrAsk(backup) {
  const dialog = $("bkrDialog");
  if (!dialog) return Promise.resolve(false);

  const back = document.activeElement;
  const when = backupWhen(backup.taken);
  const database = backupsNow?.database || "the server's database";
  const go = $("bkrGo");
  const typed = $("bkrConfirm");

  $("bkrDialogTitle").textContent = `Restore the backup of ${when}?`;
  $("bkrDialogBody").innerHTML = `
    <p>This replaces, on every machine of the site:</p>
    <ul>
      <li>the database <code>${h(database)}</code> — every organization, its content, its members and its settings —
        with the one in the backup, taken ${h(when)} by version ${h(backup.version || "—")};</li>
      <li>the uploaded imagery and the serving certificate, with the backup's.</li>
    </ul>
    <p>Every machine goes into maintenance and answers nothing else until the restore ends; anything changed since the
      backup was taken is not in it. The database it replaces is kept as
      <code>${h(database)}_before_restore_<i>time</i></code>, and the replaced files beside them, so there is a way back.</p>`;
  $("bkrConfirmName").textContent = backup.name;
  typed.value = "";
  $("bkrConfirmSays").textContent = "";
  go.setAttribute("aria-disabled", "true");

  return new Promise(resolve => {
    let answered = false;
    const end = yes => {
      if (answered) return;
      answered = true;
      go.onclick = null;
      typed.oninput = null;
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
      $("bkrConfirmSays").textContent = "";
    };
    // aria-disabled rather than disabled, so the button can be reached and says why it does nothing yet.
    go.onclick = () => {
      if (!matches()) {
        $("bkrConfirmSays").textContent = "Type the backup's name exactly as shown to restore it.";
        typed.focus();
        return;
      }
      end(true);
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
    $("bkSays").textContent = "A validation or restore is already running; this page says when it ends.";
    return;
  }

  if (t.dataset.backupValidate) {
    await bkrStart("validate", name);
    return;
  }

  if (!await bkrAsk(backup)) return;
  await bkrStart("restore", name, { confirm: name });
});

if (typeof module !== "undefined") module.exports = { bkrSentence, bkrStepNames };
