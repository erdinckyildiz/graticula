using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Postgres;
using Graticula.Platform.Schema;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// <b>Backup and restore, the server's own</b> — ADR-193: the whole database through <c>pg_dump</c> and
/// <c>pg_restore</c>, the state files beside it, and a manifest that says what it is and how to check it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A backup is a folder</b>, <c>graticula-backup-&lt;UTC time&gt;</c>, holding <c>database.dump</c> (the whole
/// database — deployment.md §2.2, never one schema), <c>roles.sql</c> when the login may read roles, <c>state/</c> (the
/// imagery and the serving certificate) and <c>manifest.json</c>, written last: a folder without one is unfinished.
/// </para>
/// <para>
/// <b>The files are copied twice, around the dump</b> (ADR-193 §5.3): once before, so a file deleted while the
/// database is dumped is still there for the row the dump holds, and once after, for a file added before the dump
/// took its snapshot. An extra file is harmless; a row without its file is not, and this leaves none.
/// </para>
/// <para>
/// <b>Restore goes into a new database first</b>, checks it, and only then renames: the current database is kept as
/// <c>&lt;name&gt;_before_restore_&lt;time&gt;</c>, which is the way back. It refuses before anything is written when the
/// key is not the one the backup was taken with — every registered source would fail to open — or when the backup's
/// schema is newer than this server reads.
/// </para>
/// </remarks>
internal sealed class Backups
{
    /// <summary>The name a backup folder starts with.</summary>
    public const string Prefix = "graticula-backup-";

    private const string Manifest = "manifest.json";
    private const string Database = "database.dump";
    private const string Roles = "roles.sql";
    private const string Certificate = "serving-certificate.pfx";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly NpgsqlConnectionStringBuilder _store;
    private readonly string _keyFingerprint;
    private readonly string _stateDirectory;
    private readonly string _imageryDirectory;

    /// <summary>Scene layer packages kept as files (ADR-274): beside the imagery, in the server directories.</summary>
    private string PackageFilesDirectory => Path.Combine(Path.GetDirectoryName(_imageryDirectory)!, ScenePackageFiles.Folder);

    /// <summary>Map area package files (ADR-288 §11a, D-407): geodatabases and VTPKs, in the same directories.</summary>
    private string MapAreaFilesDirectory => Path.Combine(Path.GetDirectoryName(_imageryDirectory)!, MapAreaPackageFiles.Folder);

    private readonly string? _toolDirectory;
    private readonly NpgsqlConnectionStringBuilder? _maintenance;

    /// <summary>Creates the service over one deployment.</summary>
    /// <param name="platformStore">The platform store's connection string.</param>
    /// <param name="secretKeyBase64">The configured secret key, for its fingerprint only.</param>
    /// <param name="stateDirectory">The server's state directory.</param>
    /// <param name="directory">Where backups are written.</param>
    /// <param name="toolDirectory">Where <c>pg_dump</c> and <c>pg_restore</c> are, or null for the path.</param>
    /// <param name="maintenanceStore">
    /// A connection to the same server as a role that may create databases and extensions, or null when the
    /// platform store's own role may — <c>Graticula:MaintenanceStore</c>, ADR-204 condition 9.
    /// </param>
    /// <param name="imageryDirectory">
    /// Where uploaded imagery is — the site's server directories (ADR-012 phase 1); <c>imagery</c> under the state
    /// directory when null.
    /// </param>
    /// <remarks>
    /// <b>Why there can be two credentials.</b> A server that serves as a role row-level security binds — neither a
    /// superuser nor <c>BYPASSRLS</c> — cannot restore by itself: it may not create the new database, and the dump's
    /// <c>create extension postgis</c> needs a superuser, so every hosted table with a geometry column fails to
    /// restore. Measured 2026-10-19: restored as that role alone, <i>8 hosted layer(s) name a table that is not in
    /// it</i>, and the check refused it. With a maintenance connection the database-level steps go through it — the
    /// new database is created owned by the serving role, with PostGIS in it — and <c>pg_restore</c> still runs as
    /// the serving role, so everything it creates is that role's, as before.
    /// </remarks>
    public Backups(
        string platformStore,
        string secretKeyBase64,
        string stateDirectory,
        string directory,
        string? toolDirectory = null,
        string? maintenanceStore = null,
        string? imageryDirectory = null)
    {
        // ADR-012 phase 1: the imagery may be in the site's server directories rather than the state directory.
        _imageryDirectory = imageryDirectory ?? Path.Combine(stateDirectory, "imagery");
        _store = new NpgsqlConnectionStringBuilder(platformStore);
        _keyFingerprint = KeyFingerprint(secretKeyBase64);
        _stateDirectory = stateDirectory;
        // Whole and in one style of separator: the console shows it, and `stateackups` after `D:/temp/` read as two paths.
        Location = Path.GetFullPath(directory);
        _toolDirectory = toolDirectory;
        _maintenance = maintenanceStore is { Length: > 0 }
            ? new NpgsqlConnectionStringBuilder(maintenanceStore) { Database = "postgres", SearchPath = null }
            : null;
    }

    /// <summary>Where backups are written.</summary>
    public string Location { get; }

    /// <summary>The first sixteen hex digits of the key's SHA-256: enough to tell keys apart, nothing to open with.</summary>
    public static string KeyFingerprint(string secretKeyBase64) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(secretKeyBase64)))[..16].ToLowerInvariant();

    /// <summary>What a backup says about itself.</summary>
    public sealed record BackupManifest(
        string Product,
        string Version,
        DateTimeOffset Taken,
        string Database,
        int Schema,
        string? PostgreSql,
        string? PostGis,
        string KeyFingerprint,
        IReadOnlyList<BackupPart> Parts);

    /// <summary>One file of a backup and how to check it.</summary>
    public sealed record BackupPart(string Path, long Bytes, string Sha256);

    /// <summary>Whether a backup's database roles were saved — <c>roles.sql</c> is written only when the login may read them.</summary>
    public static bool HasRoles(BackupManifest manifest) =>
        manifest.Parts.Any(p => string.Equals(Path.GetFileName(p.Path), Roles, StringComparison.Ordinal));

    /// <summary>
    /// Why a backup cannot be taken here before one is tried — <c>pg_dump</c> is neither in the configured folder nor on
    /// the path — or null. Said in the list, so the console can say it before Take rather than after (design review,
    /// ADR-193 condition 4).
    /// </summary>
    public string? ToolsMissing()
    {
        string name = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
        IEnumerable<string> folders = _toolDirectory is { Length: > 0 } directory
            ? [directory]
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return folders.Any(folder => File.Exists(Path.Combine(folder.Trim('"'), name))) ? null : ToolsNotFound("pg_dump");
    }

    /// <summary>What to do when a PostgreSQL client tool is not where this server looks.</summary>
    private string ToolsNotFound(string tool) => _toolDirectory is { Length: > 0 } directory
        ? $"{tool} was not found in {directory}, the folder Graticula:PostgresTools names. Point it at the folder that holds "
            + "the PostgreSQL client tools, of the same major version as the database or newer."
        : $"{tool} was not found on this server. Install the PostgreSQL client tools, of the same major version as the "
            + "database or newer, or set Graticula:PostgresTools to the folder that holds them.";

    /// <summary>The finished backups, newest first, by their manifests.</summary>
    public IReadOnlyList<(string Name, BackupManifest Manifest)> List()
    {
        if (!Directory.Exists(Location))
        {
            return [];
        }

        List<(string, BackupManifest)> found = [];
        foreach (string folder in Directory.EnumerateDirectories(Location, Prefix + "*"))
        {
            if (ReadManifest(folder) is { } manifest)
            {
                found.Add((Path.GetFileName(folder), manifest));
            }
        }

        return [.. found.OrderByDescending(f => f.Item2.Taken)];
    }

    /// <summary>
    /// Deletes every finished backup but the newest <paramref name="keep"/>, by when each was taken — the schedule's half
    /// of ADR-193 §5.6. Backups taken by hand count among them, as the console says. Answers the names deleted.
    /// </summary>
    public IReadOnlyList<string> Prune(int keep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        List<string> deleted = [];

        foreach ((string name, _) in List().Skip(keep))
        {
            Directory.Delete(Path.Combine(Location, name), recursive: true);
            deleted.Add(name);
        }

        return deleted;
    }

    /// <summary>Takes a backup, and answers its folder.</summary>
    public async Task<string> TakeAsync(TextWriter log, CancellationToken cancellation)
    {
        DateTimeOffset taken = DateTimeOffset.UtcNow;
        string name = Prefix + taken.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string partial = Path.Combine(Location, name + ".partial");
        string finished = Path.Combine(Location, name);
        Directory.CreateDirectory(partial);

        try
        {
            string imagery = _imageryDirectory;
            string copiedImagery = Path.Combine(partial, "state", "imagery");
            // ADR-274: scene layer packages kept as files sit beside the imagery in the server directories.
            string packages = PackageFilesDirectory;
            string copiedPackages = Path.Combine(partial, "state", ScenePackageFiles.Folder);
            // ADR-288 §11a, D-407: a backup without them restores package rows whose files are not there.
            string areaFiles = MapAreaFilesDirectory;
            string copiedAreaFiles = Path.Combine(partial, "state", MapAreaPackageFiles.Folder);

            log.WriteLine("Copying the state files (first pass)…");
            CopyNew(imagery, copiedImagery);
            CopyNew(packages, copiedPackages);
            CopyNew(areaFiles, copiedAreaFiles);

            // <b>Through the maintenance connection when there is one — D-315.</b> The whole database is dumped, and a
            // serving role row-level security binds may not read every schema in it: on the showcase pg_dump stopped
            // at schemas a CI run and an earlier product had left, and the migration's backup refused. The maintenance
            // role reads them all; the dump is restored by the serving role with --no-owner, as before, so what this
            // changes is only who may read, not who owns what comes back.
            log.WriteLine($"Dumping database '{_store.Database}'{(_maintenance is null ? string.Empty : " through the maintenance connection")}…");

            try
            {
                await RunAsync("pg_dump", ["--format=custom", "--file", Path.Combine(partial, Database)], _store.Database, cancellation,
                        credentials: _maintenance)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException refused) when (_maintenance is null
                && refused.Message.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{refused.Message} The backup is of the whole database, and '{_store.Username}' may not read all of "
                    + "it. Give Graticula__MaintenanceStore a connection to the same server as a role that may — the "
                    + "superuser, for most — and the dump goes through it (deployment.md, D-315).", refused);
            }

            log.WriteLine("Copying the state files (second pass)…");
            CopyNew(imagery, copiedImagery);
            CopyNew(packages, copiedPackages);
            CopyNew(areaFiles, copiedAreaFiles);

            string certificate = Path.Combine(_stateDirectory, Certificate);
            if (File.Exists(certificate))
            {
                Directory.CreateDirectory(Path.Combine(partial, "state"));
                File.Copy(certificate, Path.Combine(partial, "state", Certificate), overwrite: true);
            }

            try
            {
                // The roles are the cluster's, read through the maintenance connection when there is one: the serving
                // role is not allowed to read pg_authid, and a backup taken by it alone has none.
                await RunAsync("pg_dumpall", ["--roles-only", "--file", Path.Combine(partial, Roles)], null, cancellation,
                        credentials: _maintenance)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException roles)
            {
                // A restricted login cannot read roles; the backup is still whole for a cluster that has them.
                log.WriteLine($"Roles were not saved: {roles.Message.Split('\n')[0]}");
                File.Delete(Path.Combine(partial, Roles));
            }

            (int schema, string? postgres, string? postgis) = await DescribeAsync(_store.ConnectionString, cancellation).ConfigureAwait(false);

            List<BackupPart> parts = [.. Directory.EnumerateFiles(partial, "*", SearchOption.AllDirectories)
                .Select(file => Part(partial, file))];

            BackupManifest manifest = new(
                "Graticula", ProductVersion(), taken, _store.Database ?? string.Empty, schema, postgres, postgis,
                _keyFingerprint, parts);

            await File.WriteAllTextAsync(Path.Combine(partial, Manifest), JsonSerializer.Serialize(manifest, Json), cancellation)
                .ConfigureAwait(false);

            Directory.Move(partial, finished);
            log.WriteLine($"Backup written: {finished} ({parts.Sum(p => p.Bytes) / (1024 * 1024)} MB, schema {schema}).");
            return finished;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>A backup's folder from its name or path.</summary>
    public string FolderOf(string archive) => Directory.Exists(archive) ? archive : Path.Combine(Location, archive);

    /// <summary>The platform store's database name.</summary>
    public string DatabaseName => _store.Database ?? throw new InvalidOperationException("The platform store names no database.");

    /// <summary>The connection the database-level steps go through: the maintenance store's, or the serving role's, to <c>postgres</c>.</summary>
    // Through StoreHosts, so that on a store with a standby (ADR-319) the maintenance connection reaches the primary.
    private NpgsqlConnectionStringBuilder MaintenanceConnection =>
        new(StoreHosts.ForConnection(
            (_maintenance ?? new(_store.ConnectionString) { Database = "postgres", SearchPath = null }).ConnectionString));

    /// <summary>The platform store's connection string, on another database of the same server.</summary>
    public string StoreOn(string database) => new NpgsqlConnectionStringBuilder(_store.ConnectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// Why a backup may not be restored here, before anything is written — no manifest, a part missing or changed, another
    /// key, a newer schema — or null (ADR-193 §5.5's refusals, which validate and the admin API's restore share, ADR-317).
    /// </summary>
    public string? Refusal(string folder, out BackupManifest? manifest)
    {
        manifest = ReadManifest(folder);
        if (manifest is null)
        {
            return $"'{Path.GetFileName(folder)}' is not a finished backup: it has no {Manifest}.";
        }

        foreach (BackupPart part in manifest.Parts)
        {
            string file = Path.Combine(folder, part.Path);
            if (!File.Exists(file) || Part(folder, file).Sha256 != part.Sha256)
            {
                return $"'{part.Path}' is missing or does not match its checksum.";
            }
        }

        if (!string.Equals(manifest.KeyFingerprint, _keyFingerprint, StringComparison.Ordinal))
        {
            return $"This backup was taken with another secret key (fingerprint {manifest.KeyFingerprint}; this server's is "
                + $"{_keyFingerprint}). Restored with this one, every registered data source would fail to open. Start "
                + "the server with the key the backup was taken with (Graticula__SecretKey).";
        }

        int readable = PlatformMigrations.ComponentSchemaVersion.Value;
        if (manifest.Schema > readable)
        {
            return $"This backup's platform schema is {manifest.Schema} and this server reads up to {readable}. Restore it with "
                + $"the version that took it ({manifest.Version}) or a later one.";
        }

        return null;
    }

    /// <summary>
    /// Restores a backup into this deployment — the server stopped. Answers the exit code, and writes what it did and
    /// why it refused to <paramref name="log"/>.
    /// </summary>
    public async Task<int> RestoreAsync(string archive, bool force, TextWriter log, CancellationToken cancellation)
    {
        string folder = FolderOf(archive);

        // ---- every refusal before anything is written ----
        if (Refusal(folder, out BackupManifest? read) is { } refused)
        {
            log.WriteLine(refused + " Nothing was changed.");
            return 1;
        }

        BackupManifest manifest = read!;
        string current = DatabaseName;

        await using (NpgsqlConnection admin = new(MaintenanceConnection.ConnectionString))
        {
            await admin.OpenAsync(cancellation).ConfigureAwait(false);
            await using NpgsqlCommand others = new(
                "select count(*) from pg_stat_activity where datname = @db and pid <> pg_backend_pid()", admin);
            others.Parameters.AddWithValue("db", current);
            long connected = (long)(await others.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0L);

            if (connected > 0 && !force)
            {
                log.WriteLine(
                    $"{connected} connection(s) are open to '{current}' — stop the server first; a restore replaces the database "
                    + "it serves. (--force closes them.) Nothing was changed.");
                return 1;
            }
        }

        // ---- into a new database, checked, then renamed into place ----
        string stamp = Stamp();
        string restored = $"{current}_restore_{stamp}";
        string kept = $"{current}_before_restore_{stamp}";

        if (await RestoreIntoAsync(folder, manifest, restored, log, cancellation).ConfigureAwait(false) is { } wrong)
        {
            log.WriteLine($"The restored database does not hold what the backup said: {wrong}. The current database is untouched.");
            return 1;
        }

        await SwitchAsync(current, restored, kept, null, cancellation).ConfigureAwait(false);
        log.WriteLine($"The database is restored. The one it replaced is kept as '{kept}'; drop it once you are satisfied.");

        // ---- the state files, the same way ----
        PutStateFiles(folder, stamp);

        log.WriteLine("The state files are restored; the ones they replaced are kept beside them, named before_restore.");
        NpgsqlConnection.ClearAllPools();
        return 0;
    }

    /// <summary>The time a restore or a validation names its databases and kept files with.</summary>
    public static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Restores a backup's dump into a new database of the same server and checks it against the manifest — the first
    /// half of every restore, and the whole of a validation (ADR-317). Answers null when it holds what the backup said;
    /// otherwise why not, with the database dropped. Any exception drops it too.
    /// </summary>
    public async Task<string?> RestoreIntoAsync(
        string folder, BackupManifest manifest, string database, TextWriter log, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(log);
        NpgsqlConnectionStringBuilder maintenance = MaintenanceConnection;

        if (_maintenance is null)
        {
            await ExecuteAsync(maintenance.ConnectionString, $"create database {Quote(database)} template template0", cancellation)
                .ConfigureAwait(false);
        }
        else
        {
            // Owned by the serving role, so the rename below and everything pg_restore creates are its; PostGIS put in
            // by the role that may, so the dump's own `create extension` is a warning rather than the loss of every
            // geometry table.
            string owner = _store.Username ?? throw new InvalidOperationException("The platform store names no user.");
            await ExecuteAsync(
                    maintenance.ConnectionString,
                    $"create database {Quote(database)} owner {Quote(owner)} template template0",
                    cancellation)
                .ConfigureAwait(false);

            if (manifest.PostGis is not null)
            {
                NpgsqlConnectionStringBuilder inside = new(maintenance.ConnectionString) { Database = database };
                await ExecuteAsync(inside.ConnectionString, "create extension if not exists postgis", cancellation)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            log.WriteLine($"Restoring into '{database}'…");
            string dump = Path.Combine(folder, Database);

            // <b>The tables and their rows in parallel, then the constraints, policies and triggers one at a time.</b>
            // With --jobs over the whole dump a foreign key could be validated after its table's row-level security
            // policy existed, and a serving role the policy binds is refused — "query would be affected by row-level
            // security policy" — which pg_restore reports and goes past. Measured 2026-10-27 on a fixture served as a
            // bound role: 54 of 70 foreign keys missing, the restore reported done, and the check below, which looked
            // at the hosted tables only, passed it. Restored in one pass the post-data section runs in the dump's
            // order, foreign keys before policies, and all 70 came back.
            await RunAsync("pg_restore", ["--no-owner", "--jobs=4", "--section=pre-data", "--section=data", "--dbname", database, dump],
                database, cancellation, tolerateWarnings: true).ConfigureAwait(false);
            await RunAsync("pg_restore", ["--no-owner", "--section=post-data", "--dbname", database, dump],
                database, cancellation, tolerateWarnings: true).ConfigureAwait(false);

            string check = StoreOn(database);
            string contents = await RunAsync("pg_restore", ["--list", dump], null, cancellation).ConfigureAwait(false);

            string? wrong = await CheckAsync(check, manifest.Schema, cancellation).ConfigureAwait(false)
                ?? await MissingAsync(check, contents, cancellation).ConfigureAwait(false);

            if (wrong is not null)
            {
                await DropAsync(database, CancellationToken.None).ConfigureAwait(false);
            }

            return wrong;
        }
        catch
        {
            await DropAsync(database, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Drops a database this service made — a scratch or restored one — if it is there.</summary>
    public async Task DropAsync(string database, CancellationToken cancellation)
    {
        // `with (force)`: a check's data source is disposed, but a connection it opened may not have left yet.
        await ExecuteAsync(MaintenanceConnection.ConnectionString, $"drop database if exists {Quote(database)} with (force)", cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>What a restored database holds, counted: services, layers and portal items — validate's report.</summary>
    public async Task<IReadOnlyDictionary<string, long>> CountAsync(string database, CancellationToken cancellation)
    {
        await using NpgsqlDataSource source = StoreHosts.Build(new NpgsqlDataSourceBuilder(StoreOn(database)));
        Dictionary<string, long> counted = new(StringComparer.Ordinal);

        foreach (string table in new[] { "service", "layer", "item" })
        {
            await using NpgsqlCommand count = source.CreateCommand(
                $"select case when to_regclass('{table}') is null then -1 else (select count(*) from {table}) end");
            counted[table] = Convert.ToInt64(await count.ExecuteScalarAsync(cancellation).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        return counted;
    }

    /// <summary>
    /// The switch, at the database level (ADR-317 §5.4.6): connections to <paramref name="current"/> refused, the remaining
    /// ones ended, it renamed <paramref name="kept"/>, <paramref name="restored"/> renamed into its place. The connection
    /// string never changes. When the second rename fails the first is undone, and the current database is open again
    /// either way; <paramref name="between"/>, when given, runs between the two renames — a test's failure.
    /// </summary>
    public async Task SwitchAsync(string current, string restored, string kept, Func<Task>? between, CancellationToken cancellation)
    {
        string maintenance = MaintenanceConnection.ConnectionString;
        string literal = current.Replace("'", "''", StringComparison.Ordinal);

        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(maintenance, $"alter database {Quote(current)} allow_connections false", cancellation).ConfigureAwait(false);

        try
        {
            // A terminated backend takes a moment to leave, and the rename refuses until it has.
            for (int tries = 0; ; tries++)
            {
                await ExecuteAsync(maintenance,
                    $"select pg_terminate_backend(pid) from pg_stat_activity where datname = '{literal}' and pid <> pg_backend_pid()",
                    cancellation).ConfigureAwait(false);

                try
                {
                    await ExecuteAsync(maintenance, $"alter database {Quote(current)} rename to {Quote(kept)}", cancellation)
                        .ConfigureAwait(false);
                    break;
                }
                catch (PostgresException busy) when (busy.SqlState == "55006" && tries < 50)
                {
                    await Task.Delay(100, cancellation).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await ExecuteAsync(maintenance, $"alter database {Quote(current)} allow_connections true", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        try
        {
            if (between is not null)
            {
                await between().ConfigureAwait(false);
            }

            await ExecuteAsync(maintenance, $"alter database {Quote(restored)} rename to {Quote(current)}", cancellation)
                .ConfigureAwait(false);
        }
        catch
        {
            await ExecuteAsync(maintenance, $"alter database {Quote(kept)} rename to {Quote(current)}", CancellationToken.None).ConfigureAwait(false);
            await ExecuteAsync(maintenance, $"alter database {Quote(current)} allow_connections true", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // The way back stays usable; it is renamed, so nothing connects to it by accident.
        await ExecuteAsync(maintenance, $"alter database {Quote(kept)} allow_connections true", cancellation).ConfigureAwait(false);
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>
    /// Puts a backup's state files in place — imagery moved aside and replaced, package files added, the certificate
    /// replaced with the old one kept — and answers what undoes it: the replaced ones moved back.
    /// </summary>
    public Action PutStateFiles(string folder, string stamp)
    {
        List<Action> undo = [];
        string imagery = _imageryDirectory;
        string keptImagery = Path.Combine(Path.GetDirectoryName(imagery)!, $"imagery_before_restore_{stamp}");
        bool hadImagery = Directory.Exists(imagery);

        if (hadImagery)
        {
            Directory.Move(imagery, keptImagery);
        }

        undo.Add(() =>
        {
            if (Directory.Exists(imagery))
            {
                Directory.Delete(imagery, recursive: true);
            }

            if (hadImagery)
            {
                Directory.Move(keptImagery, imagery);
            }
        });

        CopyNew(Path.Combine(folder, "state", "imagery"), imagery);

        // ADR-274: package files are named by their hash, so one already here is the same file; only the missing are
        // copied. Not undone: an extra file no row names is harmless (ADR-193 §5.3).
        CopyNew(Path.Combine(folder, "state", ScenePackageFiles.Folder), PackageFilesDirectory);

        // D-407: a package file is named by a fresh id, so one already here is the same file; only the missing are copied.
        CopyNew(Path.Combine(folder, "state", MapAreaPackageFiles.Folder), MapAreaFilesDirectory);

        string certificate = Path.Combine(_stateDirectory, Certificate);
        string savedCertificate = Path.Combine(folder, "state", Certificate);
        if (File.Exists(savedCertificate))
        {
            string keptCertificate = Path.Combine(_stateDirectory, $"{Certificate}.before_restore_{stamp}");
            bool had = File.Exists(certificate);

            if (had)
            {
                File.Move(certificate, keptCertificate);
            }

            File.Copy(savedCertificate, certificate);
            undo.Add(() =>
            {
                File.Delete(certificate);
                if (had)
                {
                    File.Move(keptCertificate, certificate);
                }
            });
        }

        return () =>
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                undo[i]();
            }
        };
    }

    /// <summary>Whether a backup's schema is older than this server's, so restore is followed by migrate.</summary>
    public static bool NeedsMigration(BackupManifest manifest) =>
        manifest.Schema < PlatformMigrations.ComponentSchemaVersion.Value;

    /// <summary>A backup's manifest, or null when it has none — an unfinished one.</summary>
    public static BackupManifest? ReadManifest(string folder)
    {
        string file = Path.Combine(folder, Manifest);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The restored database's own account of itself, checked against the manifest; null when it agrees.</summary>
    private static async Task<string?> CheckAsync(string connection, int schema, CancellationToken cancellation)
    {
        await using NpgsqlDataSource source = StoreHosts.Build(new NpgsqlDataSourceBuilder(connection));
        SchemaStamp? stamp = await new PostgresPlatformSchemaStore(source).ReadStampAsync(cancellation).ConfigureAwait(false);
        if (stamp?.Applied.Value != schema)
        {
            return $"its platform schema is {stamp?.Applied.Value.ToString(CultureInfo.InvariantCulture) ?? "missing"}, and the manifest says {schema}";
        }

        // deployment.md §2.3: a catalogue whose hosted tables are not there is the restore that looks successful.
        await using NpgsqlCommand missing = source.CreateCommand(
            "select count(*) from layer l join data_source d on d.id = l.data_source_id "
            + "where d.is_datastore and to_regclass(quote_ident(l.schema_name) || '.' || quote_ident(l.table_name)) is null");
        long absent = (long)(await missing.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0L);
        return absent == 0 ? null : $"{absent} hosted layer(s) name a table that is not in it";
    }

    /// <summary>
    /// What the dump holds and the restored database does not — foreign keys, row-level security policies, triggers — by
    /// count against the dump's own table of contents; null when nothing is short.
    /// </summary>
    /// <remarks>
    /// <b>Because pg_restore goes past what it cannot create</b>, and a restore must, since an extension's comment or
    /// <c>spatial_ref_sys</c>'s grant is refused to a serving role every time. What it went past was not looked at, so a
    /// database missing 54 of its 70 foreign keys was restored as whole (2026-10-27). These three kinds are the ones that
    /// hold the catalogue's rules — what may be deleted, who may read what, what history records — and a restore short of
    /// any is not the database the backup was of.
    /// </remarks>
    private static async Task<string?> MissingAsync(string connection, string contents, CancellationToken cancellation)
    {
        Dictionary<string, int> listed = new(StringComparer.Ordinal) { ["FK CONSTRAINT"] = 0, ["POLICY"] = 0, ["TRIGGER"] = 0 };

        foreach (string line in contents.Split('\n'))
        {
            System.Text.RegularExpressions.Match entry = System.Text.RegularExpressions.Regex.Match(
                line, @"^\d+; \d+ \d+ (FK CONSTRAINT|POLICY|TRIGGER) ");

            if (entry.Success)
            {
                listed[entry.Groups[1].Value]++;
            }
        }

        await using NpgsqlDataSource source = StoreHosts.Build(new NpgsqlDataSourceBuilder(connection));
        await using NpgsqlCommand count = source.CreateCommand(
            """
            select (select count(*) from pg_constraint c where c.contype = 'f'
                      and not exists (select 1 from pg_depend d where d.objid = c.oid and d.deptype = 'e')),
                   (select count(*) from pg_policy),
                   (select count(*) from pg_trigger t where not t.tgisinternal
                      and not exists (select 1 from pg_depend d where d.objid = t.oid and d.deptype = 'e'))
            """);
        await using NpgsqlDataReader reader = await count.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        await reader.ReadAsync(cancellation).ConfigureAwait(false);

        List<string> shortOf = [];

        foreach ((string kind, int column, string said) in new[]
                 {
                     ("FK CONSTRAINT", 0, "foreign key"), ("POLICY", 1, "row-level security policy"), ("TRIGGER", 2, "trigger"),
                 })
        {
            long there = reader.GetInt64(column);

            if (there < listed[kind])
            {
                shortOf.Add($"{listed[kind] - there} of its {listed[kind]} {said}s");
            }
        }

        return shortOf.Count == 0
            ? null
            : $"it is missing {string.Join(", ", shortOf)}, which pg_restore could not create";
    }

    private static async Task<(int Schema, string? PostgreSql, string? PostGis)> DescribeAsync(string connection, CancellationToken cancellation)
    {
        await using NpgsqlDataSource source = StoreHosts.Build(new NpgsqlDataSourceBuilder(connection));
        SchemaStamp? stamp = await new PostgresPlatformSchemaStore(source).ReadStampAsync(cancellation).ConfigureAwait(false);

        await using NpgsqlCommand versions = source.CreateCommand(
            "select current_setting('server_version'), (select extversion from pg_extension where extname = 'postgis')");
        await using NpgsqlDataReader reader = await versions.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        await reader.ReadAsync(cancellation).ConfigureAwait(false);

        return (stamp?.Applied.Value ?? 0, reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task ExecuteAsync(string connection, string sql, CancellationToken cancellation)
    {
        await using NpgsqlConnection open = new(StoreHosts.ForConnection(connection));
        await open.OpenAsync(cancellation).ConfigureAwait(false);
        await using NpgsqlCommand command = new(sql, open);
        await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Runs a PostgreSQL client tool against the platform store's server, the password in its environment.</summary>
    private async Task<string> RunAsync(
        string tool,
        string[] arguments,
        string? database,
        CancellationToken cancellation,
        bool tolerateWarnings = false,
        NpgsqlConnectionStringBuilder? credentials = null)
    {
        NpgsqlConnectionStringBuilder login = credentials ?? _store;
        string executable = _toolDirectory is { Length: > 0 } directory
            ? Path.Combine(directory, OperatingSystem.IsWindows() ? tool + ".exe" : tool)
            : tool;

        ProcessStartInfo start = new(executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // ADR-319 §5.2: a primary-and-standby store is dumped from, and restored into, its primary — libpq's lists.
        (string hosts, string ports, string? target) = StoreHosts.ForLibpq(login);
        start.Environment["PGHOST"] = hosts;
        start.Environment["PGPORT"] = ports;
        if (target is not null)
        {
            start.Environment["PGTARGETSESSIONATTRS"] = target;
        }

        start.Environment["PGUSER"] = login.Username ?? string.Empty;
        start.Environment["PGPASSWORD"] = login.Password ?? string.Empty;
        start.Environment["PGDATABASE"] = database ?? "postgres";
        start.Environment["PGSSLMODE"] = login.SslMode switch
        {
            SslMode.Disable => "disable",
            SslMode.Require => "require",
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => "prefer",
        };

        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The operating system's sentence names the working directory and not the tool's folder; ours says what to do.
            throw new InvalidOperationException(ToolsNotFound(tool));
        }

        using Process process = started ?? throw new InvalidOperationException($"{tool} could not be started.");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellation);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        string said = await errors.ConfigureAwait(false);
        string answered = await output.ConfigureAwait(false);

        // pg_restore exits 1 for warnings it ignored (an extension's comment it may not set, for one); the check after
        // it is what decides whether the database is whole.
        if (process.ExitCode != 0 && !(tolerateWarnings && process.ExitCode == 1 && said.Contains("errors ignored on restore", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"{tool} failed ({process.ExitCode}): {said.Trim()}");
        }

        return answered;
    }

    private static BackupPart Part(string root, string file)
    {
        using FileStream read = File.OpenRead(file);
        return new BackupPart(
            Path.GetRelativePath(root, file).Replace('\\', '/'),
            read.Length,
            Convert.ToHexString(SHA256.HashData(read)).ToLowerInvariant());
    }

    /// <summary>Copies every file not already copied, keeping the tree.</summary>
    private static void CopyNew(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    File.Copy(file, target);
                }
                catch (FileNotFoundException)
                {
                    // Deleted while copying: the first pass's copy, or none, is the right answer.
                }
            }
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string ProductVersion() =>
        typeof(Backups).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";
}
