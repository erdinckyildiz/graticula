using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Postgres;
using Graticula.Platform.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// One validation or restore of a backup, as its record says it — ADR-317 §5.2: a file under the backup location, written
/// at every step, because a restore replaces the database a job row would be in.
/// </summary>
internal sealed class BackupOperation
{
    /// <summary>Its id: 32 hex digits, which is also what lets its status be read while every node refuses requests.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary><c>validate</c> or <c>restore</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The backup's name.</summary>
    public string Backup { get; init; } = string.Empty;

    /// <summary><c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
    public string State { get; set; } = "running";

    /// <summary>The step it is on, or ended on.</summary>
    public string Step { get; set; } = "checks";

    /// <summary>Each step it reached, with when.</summary>
    public List<BackupOperationStep> Steps { get; init; } = [];

    /// <summary>When it began.</summary>
    public DateTimeOffset Started { get; init; }

    /// <summary>When it ended, or null while it runs.</summary>
    public DateTimeOffset? Finished { get; set; }

    /// <summary>Why it failed, or null.</summary>
    public string? Error { get; set; }

    /// <summary>What it found: validate's counts and scratch database; restore's kept database.</summary>
    public Dictionary<string, object?> Result { get; init; } = [];
}

/// <summary>One step of an operation.</summary>
/// <param name="Name">The step's name.</param>
/// <param name="At">When it began.</param>
/// <param name="Said">What it said about itself.</param>
internal sealed record BackupOperationStep(string Name, DateTimeOffset At, string Said);

/// <summary>
/// The gate a restore closes on every node — ADR-317 §5.4.2: while it is closed this node answers every request 503
/// <c>restoring</c>, except <c>/healthz/live</c> and the operation's own status, and counts the requests already inside so
/// the restore can wait for them.
/// </summary>
internal sealed partial class RestoreGate(ILogger<RestoreGate> log)
{
    /// <summary>The setting the executing node writes into the platform store for the length of a restore.</summary>
    public const string Marker = "site.restore";

    /// <summary>The announcement's kind (ADR-012 phase 0 item 2).</summary>
    public const string Announcement = "restore";

    /// <summary>The readiness state, beside ADR-318's refusals.</summary>
    public const string State = "restoring";

    private string? _operation;
    private int _inside;

    /// <summary>The operation that closed it, or null when open.</summary>
    public string? Operation => Volatile.Read(ref _operation);

    /// <summary>Where operations' records are read from, for the status a closed gate still answers.</summary>
    public Func<string, BackupOperation?>? Read { get; set; }

    /// <summary>Closes it for an operation.</summary>
    public void Close(string operation)
    {
        if (Interlocked.Exchange(ref _operation, operation) is null)
        {
            Closed(log, operation);
        }
    }

    /// <summary>Opens it.</summary>
    public void Open()
    {
        if (Interlocked.Exchange(ref _operation, null) is { } was)
        {
            Opened(log, was);
        }
    }

    /// <summary>Waits until no request is inside, or <paramref name="within"/> has passed; answers how many still are.</summary>
    public async Task<int> DrainAsync(TimeSpan within, CancellationToken cancellation)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + within;

        while (Volatile.Read(ref _inside) > 0 && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(100, cancellation).ConfigureAwait(false);
        }

        return Volatile.Read(ref _inside);
    }

    /// <summary>The middleware: first in the pipeline, before anything reads the store.</summary>
    public async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (Operation is not { } operation)
        {
            Interlocked.Increment(ref _inside);

            try
            {
                await next().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }

            return;
        }

        PathString path = context.Request.Path;

        if (path.Equals("/healthz/live", StringComparison.OrdinalIgnoreCase))
        {
            // A restore is not a fault in this process, and restarting the node that runs it would kill it half way.
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { status = "live", state = State }, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // The operation's own status, answered here — without a session, which lives in the database being replaced.
        // Its id is 128 random bits; the record says which step it is on and nothing about the data.
        if (HttpMethods.IsGet(context.Request.Method)
            && path.StartsWithSegments("/admin/backups/operations", StringComparison.OrdinalIgnoreCase, out PathString rest)
            && rest.Value?.Trim('/') is { Length: 32 } id
            && Read?.Invoke(id) is { } record)
        {
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(BackupOperations.Describe(record), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await ServingRefusal.WriteAsync(
            context,
            new ServingRefusal(State, "This server is being restored from a backup and answers nothing else until it is done (ADR-317). Retry shortly."),
            status: State).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 2784, Level = LogLevel.Warning,
        Message = "A restore ({Operation}) closed this node: it answers every request 503 'restoring' until it ends (ADR-317).")]
    private static partial void Closed(ILogger logger, string operation);

    [LoggerMessage(EventId = 2785, Level = LogLevel.Warning,
        Message = "The restore ({Operation}) has ended here: this node serves again (ADR-317).")]
    private static partial void Opened(ILogger logger, string operation);
}

/// <summary>
/// Validate and restore through the admin API — ADR-317. One operation at a time, each a background run whose record is a
/// file every node of the site reads.
/// </summary>
internal sealed partial class BackupOperations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(30);

    private readonly Backups _backups;
    private readonly NpgsqlDataSource _store;
    private readonly RestoreGate _gate;
    private readonly SiteNode? _node;
    private readonly SiteSingletons? _singletons;
    private readonly SiteAnnouncements? _site;
    private readonly ILogger<BackupOperations> _log;
    private int _running;
    private int _watching;

    /// <summary>Creates the service.</summary>
    public BackupOperations(
        Backups backups, NpgsqlDataSource store, RestoreGate gate, ILogger<BackupOperations> log,
        SiteNode? node = null, SiteSingletons? singletons = null, SiteAnnouncements? site = null)
    {
        _backups = backups;
        _store = store;
        _gate = gate;
        _log = log;
        _node = node;
        _singletons = singletons;
        _site = site;
        _gate.Read = Read;
    }

    /// <summary>
    /// Clears the pools the server holds beyond the platform store's — the datastore's and the layers' — which a restore
    /// left holding connections the database ended. Set where they are built.
    /// </summary>
    public Action? ClearOtherPools { get; set; }

    private void ClearPools()
    {
        _store.Clear();
        ClearOtherPools?.Invoke();
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Whether an operation is running in this process.</summary>
    public bool Running => Volatile.Read(ref _running) == 1;

    private string Folder => Path.Combine(_backups.Location, ".operations");

    /// <summary>An operation's record, or null.</summary>
    public BackupOperation? Read(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit))
        {
            return null;
        }

        string file = Path.Combine(Folder, id + ".json");

        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            // Shared for delete, so a read never stands in the way of the record being replaced.
            using FileStream read = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<BackupOperation>(read, Json);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The newest operation's record, or null.</summary>
    public BackupOperation? Latest()
    {
        if (!Directory.Exists(Folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(Folder, "*.json")
            .Select(f => Read(Path.GetFileNameWithoutExtension(f)))
            .Where(o => o is not null)
            .OrderByDescending(o => o!.Started)
            .FirstOrDefault();
    }

    /// <summary>What an operation's status answers.</summary>
    public static object Describe(BackupOperation operation)
    {
        // A snapshot under the operation's lock: the run adds steps and results while a status is being written, and
        // enumerating them unlocked threw 'Collection was modified' into a 500 (seen in a test run, 2026-11-23).
        lock (operation)
        {
            return Snapshot(operation);
        }
    }

    private static object Snapshot(BackupOperation operation) => new
    {
        id = operation.Id,
        kind = operation.Kind,
        backup = operation.Backup,
        state = operation.State,
        step = operation.Step,
        steps = operation.Steps.Select(s => new { name = s.Name, at = s.At, said = s.Said }).ToArray(),
        started = operation.Started,
        finished = operation.Finished,
        error = operation.Error,
        result = new Dictionary<string, object?>(operation.Result),
    };

    /// <summary>Records one finding of an operation, under its lock.</summary>
    private static void Note(BackupOperation operation, string key, object? value)
    {
        lock (operation)
        {
            operation.Result[key] = value;
        }
    }

    /// <summary>Starts a validation or a restore of a backup; null when one is already running here.</summary>
    /// <param name="kind"><c>validate</c> or <c>restore</c>.</param>
    /// <param name="name">The backup's name.</param>
    /// <returns>The operation, running.</returns>
    public BackupOperation? Start(string kind, string name)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return null;
        }

        BackupOperation operation = new()
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            Kind = kind,
            Backup = name,
            Started = DateTimeOffset.UtcNow,
        };

        Save(operation, "checks", "Checking the manifest, every part's checksum, the key and the schema.");

        _ = Task.Run(async () =>
        {
            try
            {
                if (kind == "restore")
                {
                    await RestoreAsync(operation).ConfigureAwait(false);
                }
                else
                {
                    await ValidateAsync(operation).ConfigureAwait(false);
                }

                operation.State = "succeeded";
                Save(operation, "done", kind == "restore" ? "Restored." : "The backup is whole and restores.");
            }
#pragma warning disable CA1031 // the record is the only place a background run can say why it stopped
            catch (Exception failed)
#pragma warning restore CA1031
            {
                operation.State = "failed";
                operation.Error = failed.Message;
                Save(operation, operation.Step, "Failed: " + failed.Message);
                Failed(_log, operation.Kind, operation.Backup, failed.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });

        return operation;
    }

    private void Save(BackupOperation operation, string step, string said)
    {
        lock (operation)
        {
            operation.Step = step;
            operation.Steps.Add(new BackupOperationStep(step, DateTimeOffset.UtcNow, said));

            if (operation.State != "running")
            {
                operation.Finished = DateTimeOffset.UtcNow;
            }

            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, operation.Id + ".json");
            string partial = file + ".partial";
            File.WriteAllText(partial, JsonSerializer.Serialize(operation, Json));

            // <b>Never a reason for the operation to fail.</b> A status being read on another thread holds the file open,
            // and on Windows the replace then refuses: a restore failed at 'files' over nothing but that (2026-11-23).
            for (int tries = 0; ; tries++)
            {
                try
                {
                    File.Move(partial, file, overwrite: true);
                    break;
                }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && tries < 20)
                {
                    Thread.Sleep(25);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    NotPutBack(_log, "the operation's record could not be written: " + e.Message);
                    break;
                }
            }
        }
    }

    private string FolderOf(BackupOperation operation) => Path.Combine(_backups.Location, operation.Backup);

    private Backups.BackupManifest Checked(BackupOperation operation)
    {
        if (_backups.Refusal(FolderOf(operation), out Backups.BackupManifest? manifest) is { } refused)
        {
            throw new InvalidOperationException(refused + " Nothing was changed.");
        }

        return manifest!;
    }

    /// <summary>ADR-317 §5.3: restored into a scratch database, checked, counted, dropped. The live database is not read.</summary>
    private async Task ValidateAsync(BackupOperation operation)
    {
        Backups.BackupManifest manifest = Checked(operation);
        string scratch = $"{_backups.DatabaseName}_validate_{Backups.Stamp()}";
        Note(operation, "scratchDatabase", scratch);
        Note(operation, "schema", manifest.Schema);

        Save(operation, "restore", $"Restoring the dump into the scratch database '{scratch}'.");

        try
        {
            using StringWriter said = new();
            if (await _backups.RestoreIntoAsync(FolderOf(operation), manifest, scratch, said, CancellationToken.None).ConfigureAwait(false) is { } wrong)
            {
                throw new InvalidOperationException($"The restored database does not hold what the backup said: {wrong}.");
            }

            Save(operation, "count", "The schema, the hosted tables, the foreign keys, policies and triggers agree; counting.");
            IReadOnlyDictionary<string, long> counted = await _backups.CountAsync(scratch, CancellationToken.None).ConfigureAwait(false);
            Note(operation, "services", counted["service"]);
            Note(operation, "layers", counted["layer"]);
            Note(operation, "items", counted["item"]);
        }
        finally
        {
            Save(operation, "drop", $"Dropping '{scratch}'.");
            await _backups.DropAsync(scratch, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>ADR-317 §5.4: every node in maintenance, a new database, the files, the switch, the nodes back.</summary>
    private async Task RestoreAsync(BackupOperation operation)
    {
        Backups.BackupManifest manifest = Checked(operation);
        string current = _backups.DatabaseName;
        string stamp = Backups.Stamp();
        string restored = $"{current}_restore_{stamp}";
        string kept = $"{current}_before_restore_{stamp}";
        string? failAt = Environment.GetEnvironmentVariable("GRATICULA_TEST_RESTORE_FAIL_AT");

        void Inject(string step)
        {
            if (string.Equals(failAt, step, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"A failure was injected at '{step}' (GRATICULA_TEST_RESTORE_FAIL_AT).");
            }
        }

        // ---- 2. maintenance ----
        Save(operation, "maintenance", "Every node of the site goes into maintenance and answers 503 'restoring'.");
        IReadOnlyList<(string Name, string State)> states = await StatesAsync(_store, CancellationToken.None).ConfigureAwait(false);
        string? own = _node is null ? null : states.FirstOrDefault(s => s.Name == _node.Name).State;
        bool switched = false;
        Action? undoFiles = null;

        await using (NpgsqlCommand marker = _store.CreateCommand(
            "insert into server_setting (name, value, changed_at) values (@name, @value, now()) "
            + "on conflict (name) do update set value = excluded.value, changed_at = now()"))
        {
            marker.Parameters.AddWithValue("name", RestoreGate.Marker);
            marker.Parameters.AddWithValue("value", operation.Id);
            await marker.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        try
        {
            foreach ((string name, _) in states)
            {
                await SiteNode.SetStateAsync(_store, name, "maintenance", CancellationToken.None).ConfigureAwait(false);
            }

            _gate.Close(operation.Id);

            if (_node is not null)
            {
                await _node.ApplyAsync("maintenance").ConfigureAwait(false);
            }

            _site?.Announce(RestoreGate.Announcement, operation.Id);

            foreach ((string name, _) in states)
            {
                _site?.Announce(SiteAnnouncements.Machine, name);
            }

            Note(operation, "nodes", states.Select(s => s.Name).ToArray());

            // ---- 3. drain ----
            Save(operation, "drain", "Waiting for the requests and queries already running to finish.");
            int inside = await _gate.DrainAsync(DrainLimit, CancellationToken.None).ConfigureAwait(false);
            long active = await ActiveElsewhereAsync(current, DrainLimit).ConfigureAwait(false);
            Note(operation, "drainedWithRequestsStillRunning", inside);
            Note(operation, "drainedWithQueriesStillRunning", active);

            // ---- 4. into a new database ----
            Save(operation, "restore", $"Restoring the dump into '{restored}'.");
            using (StringWriter said = new())
            {
                if (await _backups.RestoreIntoAsync(FolderOf(operation), manifest, restored, said, CancellationToken.None).ConfigureAwait(false) is { } wrong)
                {
                    throw new InvalidOperationException($"The restored database does not hold what the backup said: {wrong}.");
                }
            }

            try
            {
                await using NpgsqlDataSource fresh = NpgsqlDataSource.Create(_backups.StoreOn(restored));

                if (Backups.NeedsMigration(manifest))
                {
                    Save(operation, "migrate", $"The backup is at schema {manifest.Schema}; migrating '{restored}' to {PlatformMigrations.ComponentSchemaVersion} before the switch.");
                    await new SchemaMigrator(new PostgresPlatformSchemaStore(fresh), PlatformMigrations.All)
                        .ApplyAsync(CancellationToken.None).ConfigureAwait(false);
                }

                // The site's states as they were before this restore, not as the backup remembers them; no marker.
                foreach ((string name, string state) in states)
                {
                    try
                    {
                        await SiteNode.SetStateAsync(fresh, name, state, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (PostgresException missing) when (missing.SqlState == "42P01")
                    {
                        break;
                    }
                }

                await using NpgsqlCommand clear = fresh.CreateCommand("delete from server_setting where name = @name");
                clear.Parameters.AddWithValue("name", RestoreGate.Marker);
                await clear.ExecuteNonQueryAsync().ConfigureAwait(false);
                Inject("restore");
            }
            catch
            {
                await _backups.DropAsync(restored, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            // ---- 5. the state files ----
            // Inside the try, so nothing between the restored database's check and the switch can leave it behind — a
            // record that could not be written once did, before the record stopped being able to fail (2026-11-23).
            try
            {
                Save(operation, "files", "Putting the imagery, the package files and the serving certificate in place.");
                undoFiles = _backups.PutStateFiles(FolderOf(operation), stamp);
                Inject("files");

                // ---- 6. the switch ----
                Save(operation, "switch", $"Renaming '{current}' to '{kept}' and '{restored}' to '{current}'.");
                await _backups.SwitchAsync(current, restored, kept,
                    () => { Inject("switch"); return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
                switched = true;
            }
            catch
            {
                undoFiles?.Invoke();
                await _backups.DropAsync(restored, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            Note(operation, "keptDatabase", kept);
            Restored(_log, operation.Backup, kept);
        }
        finally
        {
            // ---- 7. the nodes come back — on success into the restored database, on failure into the old one ----
            await ComeBackAsync(operation, states, own, switched).ConfigureAwait(false);
        }
    }

    private async Task ComeBackAsync(BackupOperation operation, IReadOnlyList<(string Name, string State)> states, string? own, bool switched)
    {
        if (!switched)
        {
            Note(operation, "failedAt", operation.Step);
        }

        Save(operation, switched ? "return" : "rollback",
            switched ? "The nodes come back on the restored database." : "The old database serves; the nodes come back.");

        ClearPools();

        try
        {
            if (!switched)
            {
                // The old database is the one serving: what this restore changed in it is put back.
                foreach ((string name, string state) in states)
                {
                    await SiteNode.SetStateAsync(_store, name, state, CancellationToken.None).ConfigureAwait(false);
                }

                await using NpgsqlCommand clear = _store.CreateCommand("delete from server_setting where name = @name");
                clear.Parameters.AddWithValue("name", RestoreGate.Marker);
                await clear.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        catch (NpgsqlException e)
        {
            NotPutBack(_log, e.Message);
        }

        if (_singletons is not null)
        {
            // Its session held this node's name and the sweeps' locks on the old database; the next use takes them again.
            await _singletons.ReleaseAllAsync().ConfigureAwait(false);
        }

        _gate.Open();

        if (_node is not null)
        {
            await _node.ApplyAsync(own ?? "started").ConfigureAwait(false);
        }

        _site?.Announce(RestoreGate.Announcement, "end:" + operation.Id);

        foreach ((string name, _) in states)
        {
            _site?.Announce(SiteAnnouncements.Machine, name);
        }
    }

    /// <summary>
    /// Another node's restore, announced: this node closes its gate and opens it again when the platform store it can
    /// reach holds no restore marker — the restored database, or the old one put back (ADR-317 §5.4.7).
    /// </summary>
    public void Heard(string detail)
    {
        if (detail.StartsWith("end:", StringComparison.Ordinal))
        {
            return; // the watch below notices on its next look; an announcement may be lost, the marker cannot
        }

        if (Running)
        {
            return; // this node runs it
        }

        _gate.Close(detail);

        if (Interlocked.CompareExchange(ref _watching, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                    try
                    {
                        await using NpgsqlCommand read = _store.CreateCommand("select value from server_setting where name = @name");
                        read.Parameters.AddWithValue("name", RestoreGate.Marker);

                        if (await read.ExecuteScalarAsync().ConfigureAwait(false) is null)
                        {
                            break;
                        }
                    }
#pragma warning disable CA1031 // the database is being replaced; any failure is "not yet"
                    catch (Exception)
#pragma warning restore CA1031
                    {
                        ClearPools();
                    }
                }

                ClearPools();

                if (_singletons is not null)
                {
                    await _singletons.ReleaseAllAsync().ConfigureAwait(false);
                }

                _gate.Open();

                if (_node is not null)
                {
                    await _node.RefreshStateAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031
            catch (Exception e)
#pragma warning restore CA1031
            {
                _gate.Open();
                NotPutBack(_log, e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _watching, 0);
            }
        });
    }

    private static async Task<IReadOnlyList<(string, string)>> StatesAsync(NpgsqlDataSource store, CancellationToken cancellation)
    {
        List<(string, string)> states = [];

        try
        {
            await using NpgsqlCommand read = store.CreateCommand("select name, configured_state from site_node order by name");
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                states.Add((reader.GetString(0), reader.GetString(1)));
            }
        }
        catch (PostgresException missing) when (missing.SqlState == "42P01")
        {
            // A store before the site's tables: one node, and nothing to record.
        }

        return states;
    }

    /// <summary>Waits for the queries other nodes have running on the database to finish; answers how many still run.</summary>
    private async Task<long> ActiveElsewhereAsync(string database, TimeSpan within)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + within;
        long active;

        do
        {
            await using NpgsqlCommand count = _store.CreateCommand(
                "select count(*) from pg_stat_activity where datname = @db and state = 'active' and pid <> pg_backend_pid() "
                + "and backend_type = 'client backend' and query not ilike 'listen %'");
            count.Parameters.AddWithValue("db", database);
            active = (long)(await count.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);

            if (active == 0)
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < until);

        return active;
    }

    [LoggerMessage(EventId = 2786, Level = LogLevel.Error, Message = "A backup {Kind} of '{Backup}' failed: {Reason}")]
    private static partial void Failed(ILogger logger, string kind, string backup, string reason);

    [LoggerMessage(EventId = 2787, Level = LogLevel.Warning,
        Message = "The platform store was restored from '{Backup}'; the database it replaced is kept as '{Kept}' (ADR-317).")]
    private static partial void Restored(ILogger logger, string backup, string kept);

    [LoggerMessage(EventId = 2788, Level = LogLevel.Warning,
        Message = "After a restore, what it changed could not all be put back: {Reason}")]
    private static partial void NotPutBack(ILogger logger, string reason);
}
