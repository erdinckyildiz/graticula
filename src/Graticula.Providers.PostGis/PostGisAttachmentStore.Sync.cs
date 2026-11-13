using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Providers.PostGis;

/// <summary>
/// The half of the store a replica's synchronisation uses — ADR-290: attachments that keep the GlobalID a device gave
/// them, and a record of every change made to an attachment table, written by the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database records it, not this server</b>, for ADR-078's reason (ADR-005 §3.8): a deferred constraint trigger on
/// each attachment table writes one row per insert, update and delete into a table beside it, in the writer's own
/// transaction, so a rolled-back upload leaves no entry and a feature's deletion — which the foreign key cascades to
/// its attachments — is recorded as the deletes it is.
/// </para>
/// <para>
/// <b>Stamped at commit.</b> The feature history stamps a version with the moment its transaction began, which is right
/// for a row edit that takes milliseconds. An attachment is streamed in chunks for as long as the bytes take to arrive;
/// stamped at the start, one uploaded while a download took its generation would sit before that generation, be absent
/// from that download, and never be sent. A deferred trigger runs at commit, and <c>clock_timestamp()</c> there is the
/// moment the change became visible.
/// </para>
/// </remarks>
public sealed partial class PostGisAttachmentStore
{
    /// <summary>The name of the table, in the layer's schema, that records every attachment change — one per schema.</summary>
    public const string LogTable = "graticula_attachment_log";

    /// <summary>The name of the trigger on each attachment table, and of the function it runs.</summary>
    public const string LogTrigger = "graticula_attach_log";

    private string QualifiedLog => $"{LayerDefinition.Quote(_schema)}.{LayerDefinition.Quote(LogTable)}";

    private string QualifiedLogFunction => $"{LayerDefinition.Quote(_schema)}.{LayerDefinition.Quote(LogTrigger)}";

    /// <inheritdoc/>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand there = new("select to_regclass(@name) is not null", connection);
        there.Parameters.AddWithValue("name", Qualified);

        // No table means nothing attached and nothing to evolve: the first upload makes it, with its record.
        if ((bool)(await there.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            await EvolveAsync(connection, tableExisted: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives the table the GlobalID column and the change record when it lacks either — expand only, and a no-op once done.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="tableExisted">
    /// Whether the table was there before this server evolved it. A table already there held attachments whose changes
    /// nobody recorded, so its record says where it begins; one made together with its record has no such mark to make.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>Never rewrites an id or a GlobalID.</b> The new column is null for every attachment already there, which means
    /// <i>the one derived from its id</i>, so a replica already on a device goes on calling it what it was called.
    /// </remarks>
    private async Task EvolveAsync(NpgsqlConnection connection, bool tableExisted, CancellationToken cancellationToken)
    {
        const string Probe = """
            select exists (select 1 from pg_attribute
                           where attrelid = to_regclass(@table) and attname = 'globalid' and not attisdropped),
                   exists (select 1 from pg_trigger
                           where tgrelid = to_regclass(@table) and tgname = @trigger and not tgisinternal),
                   to_regclass(@log) is not null
            """;

        await using (NpgsqlCommand probe = new(Probe, connection))
        {
            probe.Parameters.AddWithValue("table", Qualified);
            probe.Parameters.AddWithValue("trigger", LogTrigger);
            probe.Parameters.AddWithValue("log", QualifiedLog);

            await using NpgsqlDataReader reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (reader.GetBoolean(0) && reader.GetBoolean(1) && reader.GetBoolean(2))
            {
                return;
            }
        }

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Two first uploads at once — of one table or of two in this schema, which share the log and its function — would both
        // run the DDL below; one waits for the other. The lock is per schema, and the statements are idempotent.
        await using (NpgsqlCommand gate = new("select pg_advisory_xact_lock(hashtextextended(@name, 289))", connection, transaction))
        {
            gate.Parameters.AddWithValue("name", QualifiedLog);
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // One function for every attachment table of the schema; each trigger names its table through TG_TABLE_NAME.
        string function = string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              create or replace function {{QualifiedLogFunction}}() returns trigger language plpgsql as $fn$
              declare
                  v_at timestamptz := nullif(current_setting('graticula.attachment_at', true), '')::timestamptz;
                  v_row record;
              begin
                  if v_at is null then
                      v_at := clock_timestamp();
                      perform set_config('graticula.attachment_at', v_at::text, true);
                  end if;
                  if tg_op = 'TRUNCATE' then
                      insert into {{QualifiedLog}} (at, attach_table, kind, attachmentid, rel_objectid)
                      values (v_at, tg_table_name, 'truncate', 0, 0);
                      return null;
                  end if;
                  if tg_op = 'DELETE' then v_row := old; else v_row := new; end if;
                  insert into {{QualifiedLog}} (at, attach_table, kind, attachmentid, rel_objectid, globalid)
                  values (v_at, tg_table_name, lower(tg_op), v_row.attachmentid, v_row.rel_objectid, v_row.globalid);
                  return null;
              end
              $fn$
              """);

        string[] statements =
        [
            $"alter table {Qualified} add column if not exists globalid uuid",
            $"create unique index if not exists {LayerDefinition.Quote(TableName + "_globalid")} on {Qualified} (globalid) where globalid is not null",
            $"""
             create table if not exists {QualifiedLog} (
               log_id       bigint generated always as identity primary key,
               at           timestamptz not null,
               attach_table text        not null,
               kind         text        not null check (kind in ('insert', 'update', 'delete', 'begin', 'truncate')),
               attachmentid integer     not null,
               rel_objectid bigint      not null,
               globalid     uuid
             )
             """,
            $"create index if not exists {LayerDefinition.Quote(LogTable + "_at")} on {QualifiedLog} (attach_table, at)",
            function,
        ];

        foreach (string statement in statements)
        {
            await using NpgsqlCommand command = new(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        bool hadTrigger;

        await using (NpgsqlCommand check = new(
            "select exists (select 1 from pg_trigger where tgrelid = to_regclass(@table) and tgname = @trigger and not tgisinternal)",
            connection,
            transaction))
        {
            check.Parameters.AddWithValue("table", Qualified);
            check.Parameters.AddWithValue("trigger", LogTrigger);
            hadTrigger = (bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!hadTrigger)
        {
            // <b>Deferred to commit</b>, so a change is stamped when it became visible and not when its transaction began.
            await using (NpgsqlCommand trigger = new(
                $"create constraint trigger {LogTrigger} after insert or update or delete on {Qualified} "
                + $"deferrable initially deferred for each row execute function {QualifiedLogFunction}()",
                connection,
                transaction))
            {
                await trigger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // A truncate fires no row trigger and names no attachment, so the record can only say that it happened.
            await using (NpgsqlCommand truncate = new(
                $"create trigger {LogTrigger}_truncate after truncate on {Qualified} "
                + $"for each statement execute function {QualifiedLogFunction}()",
                connection,
                transaction))
            {
                await truncate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // A table that held attachments before there was a record says where the record begins, so a replica older
            // than that is told nothing rather than something false.
            if (tableExisted)
            {
                await using NpgsqlCommand begin = new(
                    $"insert into {QualifiedLog} (at, attach_table, kind, attachmentid, rel_objectid) "
                    + "values (clock_timestamp(), @table, 'begin', 0, 0)",
                    connection,
                    transaction);
                begin.Parameters.AddWithValue("table", TableName);
                await begin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, int>> IdsOfAsync(
        IReadOnlyCollection<Guid> globalIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(globalIds);

        Dictionary<Guid, int> found = [];

        if (globalIds.Count == 0)
        {
            return found;
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand($"select globalid, attachmentid from {Qualified} where globalid = any(@ids)");
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = globalIds.ToArray() });

        try
        {
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found[reader.GetGuid(0)] = reader.GetInt32(1);
            }
        }
        catch (PostgresException e) when (e.SqlState is "42P01" or "42703")
        {
            // No table, or one with no GlobalID column yet: nothing is stored under a GlobalID.
        }

        return found;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, Guid?>> GlobalIdsAsync(
        IReadOnlyCollection<int> attachmentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);

        Dictionary<int, Guid?> found = [];

        if (attachmentIds.Count == 0)
        {
            return found;
        }

        foreach (bool withGlobalId in (bool[])[true, false])
        {
            await using NpgsqlCommand command = _dataSource.CreateCommand(
                $"select attachmentid, {GlobalIdColumn(withGlobalId)} from {Qualified} where attachmentid = any(@ids)");
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = attachmentIds.ToArray() });

            try
            {
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    found[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                }
            }
            catch (PostgresException e) when (e.SqlState == "42P01")
            {
                return found;
            }
            catch (PostgresException e) when (e.SqlState == "42703" && withGlobalId)
            {
                continue;
            }

            return found;
        }

        return found;
    }

    /// <inheritdoc/>
    public async Task<AttachmentChanges> ChangesAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        bool table;
        bool recorded;

        await using (NpgsqlCommand state = new(
            """
            select to_regclass(@table) is not null,
                   to_regclass(@log) is not null
                     and exists (select 1 from pg_trigger
                                 where tgrelid = to_regclass(@table) and tgname = @trigger and not tgisinternal)
            """,
            connection))
        {
            state.Parameters.AddWithValue("table", Qualified);
            state.Parameters.AddWithValue("log", QualifiedLog);
            state.Parameters.AddWithValue("trigger", LogTrigger);

            await using NpgsqlDataReader reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            table = reader.GetBoolean(0);
            recorded = reader.GetBoolean(1);
        }

        // No table: nothing was ever attached, so nothing changed. A table with no record: it changed without anyone
        // writing it down, and the record cannot say.
        if (!table)
        {
            return AttachmentChanges.None;
        }

        if (!recorded)
        {
            return AttachmentChanges.Unvouched;
        }

        DateTime start = after.UtcDateTime;
        DateTime end = until.UtcDateTime;

        await using (NpgsqlCommand began = new(
            $"select exists (select 1 from {QualifiedLog} where attach_table = @name and kind = 'begin' and at > @from)",
            connection))
        {
            began.Parameters.AddWithValue("name", TableName);
            began.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = start });

            if ((bool)(await began.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                return AttachmentChanges.Unvouched;
            }
        }

        List<AttachmentLogEntry> entries = [];

        await using NpgsqlCommand read = new(
            $"""
             select kind, attachmentid, rel_objectid, globalid
             from {QualifiedLog}
             where attach_table = @name and kind <> 'begin' and at > @from and at <= @to
             order by at, log_id
             """,
            connection);
        read.Parameters.AddWithValue("name", TableName);
        read.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = start });
        read.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = end });

        await using NpgsqlDataReader entriesReader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await entriesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Emptied in the window: which attachments went is not recorded, so the window is not vouched for.
            if (entriesReader.GetString(0) == "truncate")
            {
                return AttachmentChanges.Unvouched;
            }

            entries.Add(new AttachmentLogEntry(
                entriesReader.GetString(0),
                entriesReader.GetInt32(1),
                entriesReader.GetInt64(2),
                entriesReader.IsDBNull(3) ? null : entriesReader.GetGuid(3)));
        }

        return new AttachmentChanges(true, AttachmentChangeLog.Net(entries));
    }

    /// <summary>Removes a dropped attachment table's entries from its schema's record, which is otherwise left holding them.</summary>
    /// <param name="dataSource">The pool.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The attachment table's name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task ForgetChangesAsync(NpgsqlDataSource dataSource, string schema, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ForgetChangesAsync(connection, null, schema, table, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a dropped attachment table's entries from its schema's record, on a connection the caller holds.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The caller's transaction, or null.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The attachment table's name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task ForgetChangesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string log = $"{LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(LogTable)}";

        await using NpgsqlCommand there = new("select to_regclass(@log) is not null", connection, transaction);
        there.Parameters.AddWithValue("log", log);

        if (!(bool)(await there.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            return;
        }

        await using NpgsqlCommand forget = new($"delete from {log} where attach_table = @table", connection, transaction);
        forget.Parameters.AddWithValue("table", table);
        await forget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
