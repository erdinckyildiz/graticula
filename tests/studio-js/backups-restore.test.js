// System › Backups' validation and restore sentences (ADR-317): what the aria-live status says at each state, so a
// failed restore says the old database still serves and where it failed, and a finished one names the kept database.
// Run: node --test tests/studio-js
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

// The console's own helpers the file calls, as console.js defines them.
globalThis.lowerFirst = text => (text ? text[0].toLowerCase() + text.slice(1) : text);
globalThis.num = value => Number(value).toLocaleString("en-US");
globalThis.h = value => String(value ?? "").replace(/[&<>"']/g,
  c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const R = require(path.join(__dirname, "..", "..", "src", "Graticula.Host", "wwwroot", "backups-restore.js"));

test("a running operation says its step", () => {
  const said = R.bkrSentence({ kind: "restore", backup: "graticula-backup-1", state: "running", step: "switch" });
  assert.equal(said, "The restore of graticula-backup-1 is running: switching to the restored database…");
});

test("an unknown step is said by its name rather than dropped", () => {
  assert.match(R.bkrSentence({ kind: "validate", backup: "b", state: "running", step: "later" }), /later…$/);
});

test("a failed restore says the old database still serves, and where it failed", () => {
  const said = R.bkrSentence({
    kind: "restore", backup: "b", state: "failed", error: "A failure was injected at 'switch'.",
    result: { failedAt: "switch" },
  });
  assert.match(said, /failed while switching to the restored database/);
  assert.match(said, /the database this server used before still serves/);
  assert.match(said, /a failure was injected/);
});

test("a finished restore names the database it kept", () => {
  const said = R.bkrSentence({ kind: "restore", backup: "b", state: "succeeded", result: { keptDatabase: "gis_before_restore_20261123" } });
  assert.match(said, /kept as gis_before_restore_20261123/);
});

test("a finished validation says what the copy held and that it was dropped", () => {
  const said = R.bkrSentence({
    kind: "validate", backup: "b", state: "succeeded", result: { services: 1200, layers: 3, items: 0, schema: 146 },
  });
  assert.equal(said, "The validation of b finished: the backup is whole and restores — 1,200 services, 3 layers and 0 items, at "
    + "schema 146. The scratch copy was dropped.");
});

test("a failed validation says why", () => {
  assert.equal(
    R.bkrSentence({ kind: "validate", backup: "b", state: "failed", error: "'database.dump' is missing or does not match its checksum." }),
    "The validation of b failed: 'database.dump' is missing or does not match its checksum. Validate another backup, or "
    + "take a new one.");
});

test("every step the server names has a name on the page", () => {
  for (const step of ["checks", "maintenance", "drain", "restore", "migrate", "count", "drop", "files", "switch", "return", "rollback", "done"]) {
    assert.ok(R.bkrStepNames[step], step);
  }
});

test("a reason without a full stop gets one, so the next sentence does not run into it", () => {
  assert.equal(R.bkrReason("Disk full"), "disk full.");
  assert.equal(R.bkrReason(""), "no reason was given.");
});

test("a finished restore no longer asks to sign in again: the page does that itself", () => {
  const said = R.bkrSentence({ kind: "restore", backup: "b", state: "succeeded", result: { keptDatabase: "k" } });
  assert.doesNotMatch(said, /sign in/i);
});

test("each step is timed from the operation's start, and the panel says how long ago it finished and how long it took", () => {
  assert.equal(R.bkrSeconds(2200), "2.2 s");
  assert.equal(R.bkrSeconds(64000), "1 min 4 s");
  const now = Date.parse("2026-11-23T18:15:00Z");
  const op = { state: "succeeded", started: "2026-11-23T17:29:36.0Z", finished: "2026-11-23T17:29:38.2Z" };
  assert.equal(R.bkrWhen(op, now), "Finished 45 minutes ago, after 2.2 s.");
  assert.equal(R.bkrWhen({ ...op, state: "failed" }, now), "Failed 45 minutes ago, after 2.2 s.");
  assert.equal(R.bkrWhen({ state: "running", started: "2026-11-23T18:14:30Z" }, now), "Started just now.");
});

test("ages are said as a reader says them", () => {
  const now = Date.parse("2026-11-23T18:00:00Z");
  assert.equal(R.bkrAgo("2026-11-23T17:59:30Z", now), "just now");
  assert.equal(R.bkrAgo("2026-11-23T17:59:00Z", now), "1 minute ago");
  assert.equal(R.bkrAgo("2026-11-23T16:00:00Z", now), "2 hours ago");
  assert.equal(R.bkrAgo("2026-11-20T18:00:00Z", now), "3 days ago");
});

test("the restore dialog leads with what is lost, to the second, and says everyone is signed out", () => {
  const now = Date.parse("2026-11-23T19:29:38Z");
  const backup = { name: "b1", taken: "2026-11-23T17:29:38Z", version: "1.0.540" };
  const body = R.bkrDialogHtml(backup, "gis", null, now);
  assert.match(body, /^\s*<p><b>Everything changed after 17:29:38 UTC \(2 hours ago\) is lost:<\/b>/);
  assert.match(body, /Everyone signed in is signed out, you included/);
  assert.match(body, /gis<\/code>_before_restore_|gis_before_restore_/);
  assert.doesNotMatch(body, /last validation/);
});

test("the restore dialog says when this backup's last validation failed, and not another backup's", () => {
  const backup = { name: "b1", taken: "2026-11-23T17:29:38Z" };
  const failed = { kind: "validate", backup: "b1", state: "failed", error: "The checksum does not match" };
  assert.match(R.bkrDialogHtml(backup, "gis", failed), /The last validation of this backup failed: the checksum does not match\./);
  assert.doesNotMatch(R.bkrDialogHtml(backup, "gis", { ...failed, backup: "b2" }), /last validation/);
});
