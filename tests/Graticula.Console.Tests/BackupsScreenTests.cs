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

    [Fact]
    public async Task System_lists_Backups_in_its_column()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/operations", token);
        await WaitForAsync("[...document.querySelectorAll('a')].some(a => a.textContent.trim() === 'Backups')",
            "System's column has no Backups.");
    }
}
