using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// System › Backups — ADR-193 condition 4: take, list, download, delete, and a schedule that keeps what it is told to.
/// </summary>
/// <remarks>
/// The list is answered in the page: the fixture has no backup and the harness traps every write, so a backup taken
/// here would never appear. The server's half is <c>BackupEndpointsTests</c>.
/// </remarks>
public sealed class BackupsScreenTests : ConsoleTest
{
    /// <summary>Answers <c>GET /admin/backups</c> in the page: two backups, and a running one once Take is pressed.</summary>
    private const string FakeBackups = """
        (() => {
          const real = window.fetch;
          window.__bkTaken = 0;
          const two = [
            { name: "backup-20261027T020000Z", taken: "2026-10-27T02:00:00Z", version: "1.0.469", schema: "gisserver", bytes: 3080000000 },
            { name: "backup-20261026T020000Z", taken: "2026-10-26T02:00:00Z", version: "1.0.468", schema: "gisserver", bytes: 3010000000 },
          ];
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            const method = ((init && init.method) || "GET").toUpperCase();
            const path = new URL(url, location.href).pathname;
            if (method === "POST" && path === "/admin/backups") window.__bkTaken++;
            if (method !== "GET" || path !== "/admin/backups") return real(input, init);
            // The first read after Take finds it running; the next finds it finished, listed first.
            const running = window.__bkTaken === 1;
            if (running) window.__bkTaken = 2;
            const done = window.__bkTaken === 2 && !running;
            return new Response(JSON.stringify({
              location: "/var/lib/graticula/backups", running,
              last: window.__bkTaken ? { started: "2026-10-28T09:00:00Z", finished: done ? "2026-10-28T09:06:00Z" : null,
                name: done ? "backup-20261028T090000Z" : null, error: null } : null,
              schedule: { enabled: false, hourUtc: 2, keep: 7 },
              backups: done ? [{ name: "backup-20261028T090000Z", taken: "2026-10-28T09:00:00Z", version: "1.0.470",
                schema: "gisserver", bytes: 3100000000 }, ...two] : two,
            }), { status: 200, headers: { "Content-Type": "application/json" } });
          };
          return true;
        })();
        """;

    [Fact]
    public async Task Backups_are_listed_taken_and_deleted_and_the_schedule_is_saved()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/operations", token);
        await Browser.EvaluateAsync<bool>(FakeBackups);
        await Browser.EvaluateAsync<bool>("(navigateTo('#/backups'), true)");

        await WaitForAsync(Shown("#view-backups"), "System has no Backups screen (ADR-193 condition 4).");
        await WaitForAsync("(document.getElementById('bkRows')?.textContent || '').includes('backup-20261027T020000Z')",
            "The backups are not listed.");
        Assert.Contains("2 backups", await Browser.EvaluateAsync<string>("document.getElementById('bkCount').textContent") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains("GB", await Browser.EvaluateAsync<string>("document.getElementById('bkRows').textContent") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains("/var/lib/graticula/backups", await Browser.EvaluateAsync<string>("document.getElementById('bkWhere').textContent") ?? string.Empty,
            StringComparison.Ordinal);

        // Each row's buttons say which backup they act on.
        Assert.StartsWith("Download the backup of", await Browser.EvaluateAsync<string>(
            "document.querySelector('[data-backup-download]').getAttribute('aria-label')") ?? string.Empty, StringComparison.Ordinal);

        // The schedule: filled from the server, Save off until something changes, a keep out of range refused by name.
        await WaitForAsync("!document.getElementById('bkKeep').disabled && document.getElementById('bkKeep').value === '7'",
            "The schedule's controls were not filled from the server.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('bkScheduleSave').disabled"), "Save is offered with nothing changed.");
        Assert.Equal(24, await Browser.EvaluateAsync<int>("document.getElementById('bkHour').options.length"));

        await Browser.EvaluateAsync<bool>("""
            (() => { const k = document.getElementById('bkKeep'); k.value = '0'; k.dispatchEvent(new Event('input', { bubbles: true })); return true; })()
            """);
        await ClickAsync("#bkScheduleSave");
        await WaitForAsync("document.getElementById('bkKeep').getAttribute('aria-invalid') === 'true'", "A keep of 0 was not refused.");
        Assert.DoesNotContain(await WritesAsync(), w => w.StartsWith("PUT /admin/backups/schedule", StringComparison.Ordinal));

        await Browser.EvaluateAsync<bool>("""
            (() => {
              const k = document.getElementById('bkKeep'); k.value = '14'; k.dispatchEvent(new Event('input', { bubbles: true }));
              const e = document.getElementById('bkEnabled'); e.checked = true; e.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);
        await ClickAsync("#bkScheduleSave");
        await WaitForAsync("(document.getElementById('bkScheduleSays')?.textContent || '').includes('newest 14')", "Save said nothing.");
        Assert.Contains(await WritesAsync(), w => w.StartsWith("PUT /admin/backups/schedule", StringComparison.Ordinal));
        Assert.Equal("bkEnabled", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

        // Take: started, running while it runs, and said once when it finishes.
        await ClickAsync("#bkTake");
        await WaitForAsync("document.getElementById('bkTake').getAttribute('aria-disabled') === 'true'", "Take did not show the backup running.");
        Assert.Contains(await WritesAsync(), w => w.StartsWith("POST /admin/backups", StringComparison.Ordinal));
        await WaitForAsync("(document.getElementById('bkSays')?.textContent || '').includes('finished')",
            "The page did not say when the backup finished.");
        Assert.Contains("3 backups", await Browser.EvaluateAsync<string>("document.getElementById('bkCount').textContent") ?? string.Empty,
            StringComparison.Ordinal);

        // Delete asks first, and focus goes to the page's heading once the row is drawn away.
        await Browser.EvaluateAsync<bool>("(document.querySelector('[data-backup-delete]').focus(), true)");
        await ClickAsync("[data-backup-delete]");
        await WaitForAsync("document.getElementById('serverConfirm')?.open === true", "Delete did not ask first.");
        await ClickAsync("#serverConfirmGo");
        await WaitForAsync("(document.getElementById('bkSays')?.textContent || '').includes('is deleted')", "Delete said nothing.");
        Assert.Contains(await WritesAsync(), w => w.StartsWith("DELETE /admin/backups/backup-20261028T090000Z", StringComparison.Ordinal));
        Assert.Equal("bkTitle", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The design review's first finding, on the real server: <c>pg_dump</c> was not on its path, the backup failed
    /// before the page read the list again, and the status line went on saying <i>The backup started</i> beside the
    /// failure — which a screen reader never heard. And the missing tools are said before Take, not after.
    /// </summary>
    [Fact]
    public async Task A_backup_that_fails_at_once_is_announced_and_missing_tools_are_said_first()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/operations", token);
        await Browser.EvaluateAsync<bool>("""
            (() => {
              const real = window.fetch;
              window.__bkTaken = 0;
              const missing = "pg_dump was not found on this server. Install the PostgreSQL client tools.";
              window.fetch = async (input, init) => {
                const url = typeof input === "string" ? input : input.url;
                const method = ((init && init.method) || "GET").toUpperCase();
                const path = new URL(url, location.href).pathname;
                if (method === "POST" && path === "/admin/backups") window.__bkTaken++;
                if (method !== "GET" || path !== "/admin/backups") return real(input, init);
                return new Response(JSON.stringify({
                  location: "/var/lib/graticula/backups", running: false, toolsMissing: missing,
                  last: window.__bkTaken ? { started: "2026-10-28T09:00:00Z", finished: "2026-10-28T09:00:01Z", name: null,
                    error: missing } : null,
                  schedule: { enabled: false, hourUtc: 2, keep: 7 }, backups: [],
                }), { status: 200, headers: { "Content-Type": "application/json" } });
              };
              return true;
            })();
            """);
        await Browser.EvaluateAsync<bool>("(navigateTo('#/backups'), true)");

        await WaitForAsync("(document.getElementById('bkRows')?.textContent || '').includes('none can be yet')",
            "An empty list does not say that no backup can be taken here.");

        await ClickAsync("#bkTake");
        await WaitForAsync("(document.getElementById('bkSays')?.textContent || '').includes('The backup failed')",
            "A backup that failed before the next read was never announced.");
        Assert.DoesNotContain("started", await Browser.EvaluateAsync<string>("document.getElementById('bkSays').textContent") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Null(await Browser.EvaluateAsync<string?>("document.getElementById('bkTake').getAttribute('aria-disabled')"));

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// ADR-317 condition 4: Validate and Restore on each backup. Validate is followed in an <c>aria-live</c> status to its
    /// end; Restore asks first, naming the database it replaces and where the old one is kept, sends nothing until the
    /// backup's name is typed, and the reader's place survives the list being drawn again.
    /// </summary>
    [Fact]
    public async Task A_backup_is_validated_and_restored_from_its_row()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/operations", token);
        await Browser.EvaluateAsync<bool>("""
            (() => {
              const real = window.fetch;
              window.__bkr = [];
              const ops = {};
              const backups = [{ name: "graticula-backup-20261027T020000Z", taken: "2026-10-27T02:00:00Z", version: "1.0.469",
                schema: 146, bytes: 3080000000 }];
              const answer = body => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
              window.fetch = async (input, init) => {
                const url = typeof input === "string" ? input : input.url;
                const method = ((init && init.method) || "GET").toUpperCase();
                const path = new URL(url, location.href).pathname;
                const started = path.match(/^\/admin\/backups\/([^/]+)\/(validate|restore)$/);
                if (method === "POST" && started) {
                  window.__bkr.push(started[2] + " " + (init.body || ""));
                  const id = started[2] === "restore" ? "b".repeat(32) : "a".repeat(32);
                  ops[id] = { id, kind: started[2], backup: decodeURIComponent(started[1]), reads: 0 };
                  return new Response(JSON.stringify({ id, kind: started[2], backup: decodeURIComponent(started[1]),
                    state: "running", step: "checks", steps: [{ name: "checks", at: "2026-11-23T10:00:00Z" }], result: {} }),
                    { status: 202, headers: { "Content-Type": "application/json" } });
                }
                const read = path.match(/^\/admin\/backups\/operations\/([0-9a-f]{32})$/);
                if (method === "GET" && read && ops[read[1]]) {
                  const op = ops[read[1]];
                  op.reads++;
                  const done = op.reads > 2;
                  const steps = [{ name: "checks", at: "2026-11-23T10:00:00Z" }, { name: "restore", at: "2026-11-23T10:00:02Z" }];
                  return answer({ id: op.id, kind: op.kind, backup: op.backup, state: done ? "succeeded" : "running",
                    step: done ? "done" : "restore", steps,
                    result: op.kind === "restore" ? { keptDatabase: "gis_before_restore_20261123100000" }
                      : { services: 98, layers: 120, items: 67, schema: 146 } });
                }
                if (method !== "GET" || path !== "/admin/backups") return real(input, init);
                return answer({ location: "/var/lib/graticula/backups", database: "gis", running: false, last: null,
                  schedule: { enabled: false, hourUtc: 2, keep: 7 }, backups, operation: null });
              };
              return true;
            })();
            """);
        await Browser.EvaluateAsync<bool>("(navigateTo('#/backups'), true)");
        await WaitForAsync("!!document.querySelector('[data-backup-validate]')", "A backup's row has no Validate.");
        Assert.StartsWith("Restore the backup of", await Browser.EvaluateAsync<string>(
            "document.querySelector('[data-backup-restore]').getAttribute('aria-label')") ?? string.Empty, StringComparison.Ordinal);

        // Validate: followed to its end in a polite live region, with what the copy held.
        Assert.Equal("polite", await Browser.EvaluateAsync<string>("document.getElementById('bkrProgress').getAttribute('aria-live')"));
        await Browser.EvaluateAsync<bool>("(document.querySelector('[data-backup-validate]').focus(), true)");
        await ClickAsync("[data-backup-validate]");
        await WaitForAsync("(document.getElementById('bkrProgress')?.textContent || '').includes('is running')",
            "Validate's progress was not said.");
        await WaitForAsync("(document.getElementById('bkrProgress')?.textContent || '').includes('98 services')",
            "Validate's end was not said with what the copy held.");

        // The list was drawn again when it ended, and the reader is still on the button they pressed.
        await WaitForAsync("document.activeElement?.hasAttribute('data-backup-validate') === true",
            "Focus was lost when the list was drawn again.");

        // Restore: asks first and names what it replaces; Restore does nothing until the name is typed.
        await Browser.EvaluateAsync<bool>("(document.querySelector('[data-backup-restore]').focus(), true)");
        await ClickAsync("[data-backup-restore]");
        await WaitForAsync("document.getElementById('bkrDialog')?.open === true", "Restore did not ask first.");
        string body = await Browser.EvaluateAsync<string>("document.getElementById('bkrDialogBody').textContent") ?? string.Empty;
        // What is lost comes first, to the second, and the dialog says everyone is signed out (design review 2026-11-23, F4).
        Assert.StartsWith("Everything changed after 02:00:00 UTC", body.Trim(), StringComparison.Ordinal);
        Assert.Contains("Everyone signed in is signed out, you included", body, StringComparison.Ordinal);
        Assert.Contains("gis", body, StringComparison.Ordinal);
        Assert.Contains("_before_restore_", body, StringComparison.Ordinal);
        Assert.Contains("imagery and the serving certificate", body, StringComparison.Ordinal);
        Assert.Equal("bkrConfirm", await Browser.EvaluateAsync<string>("document.activeElement?.id ?? ''"));

        await ClickAsync("#bkrGo");
        await WaitForAsync("(document.getElementById('bkrConfirmSays')?.textContent || '').includes('Type the backup')",
            "Restore without the name typed said nothing.");
        Assert.Equal("true", await Browser.EvaluateAsync<string?>("document.getElementById('bkrConfirm').getAttribute('aria-invalid')"));
        Assert.Empty(await Browser.EvaluateAsync<string[]>("window.__bkr.filter(w => w.startsWith('restore'))") ?? []);

        await Browser.EvaluateAsync<bool>("""
            (() => { const i = document.getElementById('bkrConfirm'); i.value = 'graticula-backup-20261027T020000Z';
              i.dispatchEvent(new Event('input', { bubbles: true })); return true; })()
            """);
        await ClickAsync("#bkrGo");
        await WaitForAsync("(document.getElementById('bkrProgress')?.textContent || '').includes('gis_before_restore_20261123100000')",
            "The restore's end did not name the database it kept.");
        Assert.Contains(await Browser.EvaluateAsync<string[]>("window.__bkr") ?? [],
            w => w.StartsWith("restore", StringComparison.Ordinal)
                && w.Contains("graticula-backup-20261027T020000Z", StringComparison.Ordinal)
                && w.Contains("confirm", StringComparison.Ordinal));
        await WaitForAsync("document.activeElement?.hasAttribute('data-backup-restore') === true",
            "Focus did not come back to Restore after the dialog and the list being drawn again.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Design review 2026-11-23, F1: a restore that succeeds replaces the database the session lived in, so every read of
    /// the operation after the switch answers 401. The page stops reading, shows the sign-in saying why, and once signed
    /// in reads the operation again and says how it ended.
    /// </summary>
    [Fact]
    public async Task A_restore_that_ends_the_session_shows_the_sign_in_and_then_its_result()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/operations", token);
        await Browser.EvaluateAsync<bool>("""
            (() => {
              const real = window.fetch;
              window.__bkrReads = 0;
              window.__bkrSignedIn = false;
              const id = "c".repeat(32);
              const backup = "graticula-backup-20261027T020000Z";
              const answer = (body, status = 200) => new Response(JSON.stringify(body),
                { status, headers: { "Content-Type": "application/json" } });
              window.fetch = async (input, init) => {
                const url = typeof input === "string" ? input : input.url;
                const method = ((init && init.method) || "GET").toUpperCase();
                const path = new URL(url, location.href).pathname;
                if (method === "POST" && path === `/admin/backups/${backup}/restore`) {
                  return answer({ id, kind: "restore", backup, state: "running", step: "checks", started: "2026-11-23T10:00:00Z",
                    steps: [{ name: "checks", at: "2026-11-23T10:00:00Z" }], result: {} }, 202);
                }
                if (method === "GET" && path === `/admin/backups/operations/${id}`) {
                  window.__bkrReads++;
                  // The first read finds it running; after the switch the session is gone, until the reader signs in.
                  if (window.__bkrReads > 1 && !window.__bkrSignedIn) return answer({ error: { code: 401, message: "Not signed in." } }, 401);
                  const done = window.__bkrSignedIn;
                  return answer({ id, kind: "restore", backup, state: done ? "succeeded" : "running", step: done ? "done" : "restore",
                    started: "2026-11-23T10:00:00Z", finished: done ? "2026-11-23T10:00:02.2Z" : null,
                    steps: [{ name: "checks", at: "2026-11-23T10:00:00Z" }, { name: "restore", at: "2026-11-23T10:00:01Z" }],
                    result: done ? { keptDatabase: "gis_before_restore_20261123100000" } : {} });
                }
                if (method !== "GET" || path !== "/admin/backups") return real(input, init);
                return answer({ location: "/var/lib/graticula/backups", database: "gis", running: false, last: null,
                  schedule: { enabled: false, hourUtc: 2, keep: 7 },
                  backups: [{ name: backup, taken: "2026-10-27T02:00:00Z", version: "1.0.469", schema: 146, bytes: 3080000000 }],
                  operation: null });
              };
              return true;
            })();
            """);
        await Browser.EvaluateAsync<bool>("(navigateTo('#/backups'), true)");
        await WaitForAsync("!!document.querySelector('[data-backup-restore]')", "A backup's row has no Restore.");

        await ClickAsync("[data-backup-restore]");
        await WaitForAsync("document.getElementById('bkrDialog')?.open === true", "Restore did not ask first.");
        // Enter in the name field is Restore (F7).
        await Browser.EvaluateAsync<bool>("""
            (() => { const i = document.getElementById('bkrConfirm'); i.value = 'graticula-backup-20261027T020000Z';
              i.dispatchEvent(new Event('input', { bubbles: true }));
              i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); return true; })()
            """);

        await WaitForAsync("!!document.getElementById('signin')?.offsetParent",
            "A restore whose reads answered 401 never showed the sign-in.");
        Assert.Contains("signed this session out", await Browser.EvaluateAsync<string>(
            "document.getElementById('signinFor').textContent") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("polite", await Browser.EvaluateAsync<string>("document.getElementById('signinFor').getAttribute('aria-live')"));

        // The reads stop: a refused session is not asked again every second and a half.
        int reads = await Browser.EvaluateAsync<int>("window.__bkrReads");
        await Task.Delay(4000);
        Assert.Equal(reads, await Browser.EvaluateAsync<int>("window.__bkrReads"));

        // Signed in again — the token planted and the console started, as the sign-in form does — the result is said.
        await Browser.EvaluateAsync<bool>($$"""
            (async () => { window.__bkrSignedIn = true; token = "{{token}}"; sessionStorage.setItem("gis-token", token);
              await start(); return true; })()
            """);
        await WaitForAsync("(document.getElementById('bkrProgress')?.textContent || '').includes('gis_before_restore_20261123100000')",
            "After signing in again, the restore's result was not shown.");
        Assert.Contains("after 2.2 s", await Browser.EvaluateAsync<string>("document.getElementById('bkrOpWhen').textContent") ?? string.Empty,
            StringComparison.Ordinal);

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task System_lists_Backups_in_its_column()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/operations", token);
        await WaitForAsync("[...document.querySelectorAll('a')].some(a => a.textContent.trim() === 'Backups')",
            "System's column has no Backups.");
    }
}
