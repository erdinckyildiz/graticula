using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>migrate --apply</c> takes a backup before it changes the schema, changes nothing when that backup fails, and
/// skips it only when told to — ADR-193 condition 3, ADR-016 §4b's promise.
/// </summary>
/// <remarks>
/// <b>The failing backup is a real one</b>: a <see cref="Backups"/> over a server that does not exist and a tool folder
/// with no <c>pg_dump</c> in it, so what is asserted is that a backup the deployment cannot take stops the migration —
/// not that a stub said no.
/// </remarks>
public sealed class MigrateTakesABackupFirstTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"migrate-backup-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Backups Unreachable()
    {
        string tools = Path.Combine(_root, "no-tools");
        Directory.CreateDirectory(tools);
        return new Backups(
            "Host=nowhere.invalid;Port=1;Database=gis;Username=gis;Password=x;SearchPath=gisserver",
            Convert.ToBase64String(new byte[32]), Path.Combine(_root, "state"), Path.Combine(_root, "backups"), tools);
    }

    [Fact]
    public async Task A_backup_that_fails_leaves_the_schema_alone_and_says_why()
    {
        Backups backups = Unreachable();
        bool applied = false;
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await Program.BackUpThenApplyAsync(
            backups.TakeAsync, () => { applied = true; return Task.FromResult(0); }, output, error);

        Assert.Equal(1, code);
        Assert.False(applied, "The migration ran although the backup before it failed.");
        Assert.Contains("No migration was run", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--no-backup", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(backups.List());
    }

    [Fact]
    public async Task The_backup_is_taken_before_the_migration_and_the_migration_answers()
    {
        string order = "";
        using StringWriter output = new();
        using StringWriter error = new();

        int code = await Program.BackUpThenApplyAsync(
            (_, _) => { order += "backup "; return Task.FromResult("graticula-backup-x"); },
            () => { order += "migrate"; return Task.FromResult(0); },
            output, error);

        Assert.Equal(0, code);
        Assert.Equal("backup migrate", order);
    }

    [Fact]
    public async Task No_backup_means_the_migration_runs_without_one()
    {
        bool asked = false;
        Backups? backup = Program.BackupBeforeMigration(["migrate", "--apply", "--no-backup"], () => { asked = true; return Unreachable(); });

        bool applied = false;
        using StringWriter output = new();
        using StringWriter error = new();
        int code = await Program.BackUpThenApplyAsync(
            backup is null ? null : backup.TakeAsync, () => { applied = true; return Task.FromResult(0); }, output, error);

        Assert.Null(backup);
        Assert.False(asked, "--no-backup still built the backup service.");
        Assert.True(applied);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Without_no_backup_migrate_asks_for_one()
    {
        Backups? backup = Program.BackupBeforeMigration(["migrate", "--apply"], Unreachable);

        Assert.NotNull(backup);
    }
}
