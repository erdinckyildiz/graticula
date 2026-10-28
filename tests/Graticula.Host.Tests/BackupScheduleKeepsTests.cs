using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-193 condition 4's second half: the schedule keeps the number it is told to — and what the Backups screen says
/// before a backup is tried, that the PostgreSQL client tools are missing.
/// </summary>
public sealed class BackupScheduleKeepsTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graticula-keep-" + Guid.NewGuid().ToString("n")[..10]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Backups At(string tools) => new(
        "Host=nowhere.invalid;Port=1;Database=gis;Username=gis;Password=x;SearchPath=gisserver",
        Convert.ToBase64String(new byte[32]), Path.Combine(_root, "state"), Path.Combine(_root, "backups"), tools);

    /// <summary>A finished backup's folder, taken <paramref name="daysAgo"/> days ago.</summary>
    private string Finished(int daysAgo)
    {
        DateTimeOffset taken = new DateTimeOffset(2026, 10, 28, 2, 0, 0, TimeSpan.Zero).AddDays(-daysAgo);
        string folder = Path.Combine(_root, "backups", Backups.Prefix + taken.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(
            new Backups.BackupManifest("Graticula", "1.0.0", taken, "gis", 1, null, null, "0", []),
            Web));
        return Path.GetFileName(folder);
    }

    [Fact]
    public void Pruning_keeps_the_newest_it_is_told_to_and_leaves_an_unfinished_one_alone()
    {
        string[] taken = [.. Enumerable.Range(0, 5).Select(Finished)];
        string unfinished = Path.Combine(_root, "backups", Backups.Prefix + "20261028T030000Z");
        Directory.CreateDirectory(unfinished);

        Backups backups = At(_root);
        var deleted = backups.Prune(2);

        Assert.Equal(taken[2..], deleted.OrderByDescending(n => n, StringComparer.Ordinal));
        Assert.Equal(taken[..2], backups.List().Select(b => b.Name));

        // A folder with no manifest is a backup still being written, not an old one.
        Assert.True(Directory.Exists(unfinished));
        Assert.Throws<ArgumentOutOfRangeException>(() => backups.Prune(0));
    }

    [Fact]
    public void Missing_tools_are_said_before_a_backup_is_tried_and_name_the_folder_configured()
    {
        string tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(tools);

        string? missing = At(tools).ToolsMissing();
        Assert.NotNull(missing);
        Assert.Contains(tools, missing, StringComparison.Ordinal);
        Assert.Contains("Graticula:PostgresTools", missing, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(tools, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump"), string.Empty);
        Assert.Null(At(tools).ToolsMissing());
    }
}
