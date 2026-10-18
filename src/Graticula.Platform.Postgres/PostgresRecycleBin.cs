using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The recycle bin in the platform store — ADR-203, migration 94: a deleted item's rows, kept with their ids.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a delete would take is read from the store's own foreign keys, at the moment of the delete.</b> Starting at
/// the root row, every table whose key cascades from it is walked, and every row it would lose is kept as
/// <c>to_jsonb</c>; a row it would set to null is kept too, so the link can be put back. Nothing here lists the
/// tables, so one a later migration adds — with a cascading key, as every child table here has — is carried without
/// anybody remembering it. That is the reason for the shape (Q-180): a list would be the fourth one in this repository
/// that has to grow when a kind of thing does.
/// </para>
/// <para>
/// <b>Restored parents first, by the depth each table was found at</b>, in one transaction, each row written through
/// <c>jsonb_populate_record</c> so PostgreSQL converts every column as it would any value — the shape
/// <c>CatalogManifest</c> uses, here keeping the ids instead of translating them.
/// </para>
/// </remarks>
public sealed class PostgresRecycleBin : IRecycleBin
{
    /// <summary>How deep the walk goes: far past any chain this catalogue has, and a stop for a cycle.</summary>
    private const int MaximumDepth = 8;

    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal) { "service", "web_map", "item" };

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the store.</summary>
    public PostgresRecycleBin(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>One foreign key into a table: the child, its columns, the parent's, and what a delete does.</summary>
    private sealed record Edge(string Child, string[] ChildColumns, string[] ParentColumns, char OnDelete);

    /// <summary>A table reached by the walk, and the SQL that selects its rows.</summary>
    private sealed record Reach(string Table, string Where, int Depth, bool Relink);

    /// <inheritdoc/>
    public async Task<Guid?> RecycleAsync(
        string rootTable, object rootId, Guid itemId, string kind, string title, Guid? owner, Guid? deletedBy,
        DateTimeOffset expiresAt, IReadOnlyList<RecycledRelation> relations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootId);
        ArgumentNullException.ThrowIfNull(relations);

        if (!Roots.Contains(rootTable))
        {
            throw new ArgumentOutOfRangeException(nameof(rootTable), rootTable, "Only a service, a web map or an item is recycled.");
        }

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        // The root, locked, so nothing is added under it between the walk and the delete.
        await using (NpgsqlCommand lockRoot = new($"select 1 from {rootTable} where id = @root for update", connection, transaction))
        {
            lockRoot.Parameters.AddWithValue("root", rootId);

            if (await lockRoot.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }
        }

        Guid entry = Guid.NewGuid();
        List<Reach> reached = [new Reach(rootTable, "id = @root", 0, Relink: false)];

        for (int at = 0; at < reached.Count; at++)
        {
            Reach parent = reached[at];

            if (parent.Relink || parent.Depth >= MaximumDepth)
            {
                continue;
            }

            foreach (Edge edge in await EdgesIntoAsync(connection, transaction, parent.Table, cancellationToken).ConfigureAwait(false))
            {
                // A table's key to itself (a category's parent) is not a way out of this item.
                if (edge.Child == parent.Table)
                {
                    continue;
                }

                // Cascade, restrict and no action: the row is the item's — a service's layers are held by a restricting
                // key, so a service is emptied before it is deleted — and goes with it and comes back with it. Set null:
                // the row stays, and its link is kept to be put back. Set default is not used by this catalogue.
                if (edge.OnDelete is not ('c' or 'r' or 'a' or 'n'))
                {
                    continue;
                }

                string columns = string.Join(", ", edge.ChildColumns.Select(Quote));
                string keys = string.Join(", ", edge.ParentColumns.Select(Quote));
                string where = $"({columns}) in (select {keys} from {parent.Table} where {parent.Where})";
                reached.Add(new Reach(edge.Child, where, parent.Depth + 1, Relink: edge.OnDelete == 'n'));
            }
        }

        await using (NpgsqlCommand made = new(
            """
            insert into recycle_entry (id, item_id, kind, title, owner_principal_id, deleted_by, expires_at, root_table, relations)
            values (@id, @item, @kind, @title, @owner, @by, @expires, @root_table, @relations::jsonb)
            """, connection, transaction))
        {
            made.Parameters.AddWithValue("id", entry);
            made.Parameters.AddWithValue("item", itemId);
            made.Parameters.AddWithValue("kind", kind);
            made.Parameters.AddWithValue("title", title);
            made.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Uuid) { Value = (object?)owner ?? DBNull.Value });
            made.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)deletedBy ?? DBNull.Value });
            made.Parameters.AddWithValue("expires", expiresAt);
            made.Parameters.AddWithValue("root_table", rootTable);
            made.Parameters.AddWithValue("relations", JsonSerializer.Serialize(relations));
            await made.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Each table's rows, once, at the deepest depth it was reached: restoring by depth puts every parent first.
        foreach (IGrouping<(string Table, bool Relink), Reach> group in reached.GroupBy(r => (r.Table, r.Relink)))
        {
            // `union all` and then distinct on the json: a plain union compares every column, and a `json` column has no
            // equality to compare with.
            string union = string.Join(" union all ", group.Select(r => $"select * from {r.Table} where {r.Where}"));

            await using NpgsqlCommand keep = new(
                $"""
                insert into recycle_row (entry_id, table_name, depth, relink, row)
                select @entry, @table, @depth, @relink, kept from (select distinct to_jsonb(t) as kept from ({union}) t) d
                """, connection, transaction);
            keep.Parameters.AddWithValue("entry", entry);
            keep.Parameters.AddWithValue("table", group.Key.Table);
            keep.Parameters.AddWithValue("depth", group.Max(r => r.Depth));
            keep.Parameters.AddWithValue("relink", group.Key.Relink);
            keep.Parameters.AddWithValue("root", rootId);
            await keep.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // What a restricting key holds is deleted first, deepest first, while the rows its selection names are still there;
        // what cascades goes with it either way.
        foreach (Reach owned in reached.Where(r => !r.Relink && r.Depth > 0).OrderByDescending(r => r.Depth))
        {
            await using NpgsqlCommand empty = new($"delete from {owned.Table} where {owned.Where}", connection, transaction);
            empty.Parameters.AddWithValue("root", rootId);
            await empty.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand delete = new($"delete from {rootTable} where id = @root", connection, transaction))
        {
            delete.Parameters.AddWithValue("root", rootId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RecycleEntry>> ListAsync(CancellationToken cancellationToken) =>
        await ReadAsync(null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<RecycleEntry?> FindAsync(Guid entry, CancellationToken cancellationToken)
    {
        IReadOnlyList<RecycleEntry> found = await ReadAsync(entry, cancellationToken).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    /// <inheritdoc/>
    public async Task<string?> RestoreRowsAsync(Guid entry, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        List<(string Table, bool Relink, string Row)> rows = [];

        await using (NpgsqlCommand read = new(
            "select table_name, relink, row::text from recycle_row where entry_id = @entry order by relink, depth, ordinal",
            connection, transaction))
        {
            read.Parameters.AddWithValue("entry", entry);
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetString(2)));
            }
        }

        if (rows.Count == 0)
        {
            return "Nothing is kept under that entry.";
        }

        Dictionary<string, (string Columns, string Keys)> shapes = [];

        try
        {
            foreach ((string table, bool relink, string row) in rows)
            {
                if (!shapes.TryGetValue(table, out (string Columns, string Keys) shape))
                {
                    shape = await ShapeOfAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false);
                    shapes[table] = shape;
                }

                // A row that was set to null gets its link back only if it is still there; one deleted since stays deleted.
                string sql = relink
                    ? shape.Keys.Length == 0
                        ? string.Empty
                        : $"update {table} t set ({shape.Columns}) = (select {shape.Columns} from jsonb_populate_record(null::{table}, @row::jsonb)) "
                            + $"where ({shape.Keys}) = (select {shape.Keys} from jsonb_populate_record(null::{table}, @row::jsonb))"
                    : $"insert into {table} ({shape.Columns}) overriding system value "
                        + $"select {shape.Columns} from jsonb_populate_record(null::{table}, @row::jsonb)";

                if (sql.Length == 0)
                {
                    continue;
                }

                await using NpgsqlCommand put = new(sql, connection, transaction);
                put.Parameters.AddWithValue("row", row);
                await put.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (NpgsqlCommand forget = new("delete from recycle_entry where id = @entry", connection, transaction))
            {
                forget.Parameters.AddWithValue("entry", entry);
                await forget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return e.SqlState == PostgresErrorCodes.UniqueViolation
                ? $"Something with the same name or id has been made since it was deleted ({e.ConstraintName}). Rename or "
                    + "delete that first; nothing was restored."
                : $"Something it belonged to has been deleted since — a folder, an owner's account or a data source "
                    + $"({e.ConstraintName}). Nothing was restored.";
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(Guid entry, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("delete from recycle_entry where id = @entry");
        command.Parameters.AddWithValue("entry", entry);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private async Task<IReadOnlyList<RecycleEntry>> ReadAsync(Guid? only, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            select e.id, e.item_id, e.kind, e.title, e.owner_principal_id, e.deleted_by, e.deleted_at, e.expires_at,
                   e.relations::text, (select count(*) from recycle_row r where r.entry_id = e.id)
              from recycle_entry e
             where @only is null or e.id = @only
             order by e.deleted_at desc
            """);
        command.Parameters.Add(new NpgsqlParameter("only", NpgsqlDbType.Uuid) { Value = (object?)only ?? DBNull.Value });

        List<RecycleEntry> entries = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new RecycleEntry(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                JsonSerializer.Deserialize<RecycledRelation[]>(reader.GetString(8)) ?? [],
                (int)reader.GetInt64(9)));
        }

        return entries;
    }

    /// <summary>Every foreign key that points into a table, read from <c>pg_constraint</c>.</summary>
    private static async Task<IReadOnlyList<Edge>> EdgesIntoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            """
            select c.conrelid::regclass::text,
                   array(select a.attname::text from unnest(c.conkey) with ordinality k(n, o)
                          join pg_attribute a on a.attrelid = c.conrelid and a.attnum = k.n order by k.o),
                   array(select a.attname::text from unnest(c.confkey) with ordinality k(n, o)
                          join pg_attribute a on a.attrelid = c.confrelid and a.attnum = k.n order by k.o),
                   c.confdeltype::text
              from pg_constraint c
             where c.contype = 'f' and c.confrelid = @table::regclass
               and c.conrelid::regclass::text not like 'recycle\_%'
             order by 1
            """, connection, transaction);
        command.Parameters.AddWithValue("table", table);

        List<Edge> edges = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(new Edge(reader.GetString(0), reader.GetFieldValue<string[]>(1), reader.GetFieldValue<string[]>(2), reader.GetString(3)[0]));
        }

        return edges;
    }

    /// <summary>A table's writable columns and its primary key's, quoted and joined.</summary>
    private static async Task<(string Columns, string Keys)> ShapeOfAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            """
            select array(select a.attname::text from pg_attribute a
                          where a.attrelid = @table::regclass and a.attnum > 0 and not a.attisdropped and a.attgenerated = ''
                          order by a.attnum),
                   coalesce(array(select a.attname::text from pg_index i
                                    join pg_attribute a on a.attrelid = i.indrelid and a.attnum = any(i.indkey)
                                   where i.indrelid = @table::regclass and i.indisprimary order by a.attnum), '{}')
            """, connection, transaction);
        command.Parameters.AddWithValue("table", table);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return (
            string.Join(", ", reader.GetFieldValue<string[]>(0).Select(Quote)),
            string.Join(", ", reader.GetFieldValue<string[]>(1).Select(Quote)));
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
