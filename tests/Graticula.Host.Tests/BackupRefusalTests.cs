using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A restore refuses, before anything is written, the two backups that would restore and be broken: one taken with
/// another secret key, and one whose schema is newer than this server reads — ADR-193 condition 2.
/// </summary>
/// <remarks>
/// <b>No database is reached</b>: the connection string names a server that does not exist, so a refusal that came
/// after any connection would fail differently, and the test would see it.
/// </remarks>
public sealed class BackupRefusalTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string Key = Convert.ToBase64String(new byte[32]);
    private static readonly string OtherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"backups-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Backups Service() => new(
        "Host=nowhere.invalid;Port=1;Database=gis;Username=gis;Password=x;SearchPath=gisserver",
        Key, Path.Combine(_root, "state"), Path.Combine(_root, "backups"));

    private string Backup(string keyFingerprint, int schema, bool corrupt = false)
    {
        string folder = Path.Combine(_root, "backups", "graticula-backup-20261017T000000Z");
        Directory.CreateDirectory(folder);
        byte[] dump = [1, 2, 3, 4];
        File.WriteAllBytes(Path.Combine(folder, "database.dump"), dump);

        Backups.BackupManifest manifest = new(
            "Graticula", "1.0.0", DateTimeOffset.UtcNow, "gis", schema, "16", "3.4", keyFingerprint,
            [new Backups.BackupPart("database.dump", dump.Length,
                corrupt ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(dump)).ToLowerInvariant())]);

        File.WriteAllText(Path.Combine(folder, "manifest.json"),
            JsonSerializer.Serialize(manifest, Web));
        return folder;
    }

    private static async Task<(int Code, string Said)> RestoreAsync(Backups service, string folder)
    {
        using StringWriter log = new();
        int code = await service.RestoreAsync(folder, force: false, log, CancellationToken.None);
        return (code, log.ToString());
    }

    [Fact]
    public async Task A_backup_taken_with_another_key_is_refused_naming_both_fingerprints()
    {
        string folder = Backup(Backups.KeyFingerprint(OtherKey), 90);

        (int code, string said) = await RestoreAsync(Service(), folder);

        Assert.Equal(1, code);
        Assert.Contains("another secret key", said, StringComparison.Ordinal);
        Assert.Contains(Backups.KeyFingerprint(OtherKey), said, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_of_a_newer_schema_is_refused()
    {
        string folder = Backup(Backups.KeyFingerprint(Key), 100_000);

        (int code, string said) = await RestoreAsync(Service(), folder);

        Assert.Equal(1, code);
        Assert.Contains("this server reads up to", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_that_does_not_match_its_checksum_is_refused()
    {
        string folder = Backup(Backups.KeyFingerprint(Key), 90, corrupt: true);

        (int code, string said) = await RestoreAsync(Service(), folder);

        Assert.Equal(1, code);
        Assert.Contains("does not match its checksum", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_without_a_manifest_is_an_unfinished_backup()
    {
        string folder = Path.Combine(_root, "backups", "graticula-backup-unfinished.partial");
        Directory.CreateDirectory(folder);

        (int code, string said) = await RestoreAsync(Service(), folder);

        Assert.Equal(1, code);
        Assert.Contains("not a finished backup", said, StringComparison.Ordinal);
        Assert.Empty(Service().List());
    }
}
