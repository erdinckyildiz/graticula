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
    "The validation of b failed: 'database.dump' is missing or does not match its checksum.");
});

test("every step the server names has a name on the page", () => {
  for (const step of ["checks", "maintenance", "drain", "restore", "migrate", "count", "drop", "files", "switch", "return", "rollback", "done"]) {
    assert.ok(R.bkrStepNames[step], step);
  }
});
