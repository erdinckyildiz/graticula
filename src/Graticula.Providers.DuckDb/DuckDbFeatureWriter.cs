using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Providers.DuckDb;

/// <summary>Applies edits to a table of an attached DuckDB file or MotherDuck database — ADR-211.</summary>
/// <remarks>
/// <para>
/// <b><c>MySqlFeatureWriter</c>'s rules, through the source's own sandboxed DuckDB.</b> <see cref="EditColumns"/> decides
/// what an edit may send, as for every engine. The writes run on a duplicate of the instance the layer is read through
/// — a DuckDB file can be open read-write in one process only (measured, ADR-211 §1a), so there is no second
/// connection to write through — one batch at a time (<see cref="GeoParquetFolder.Writes"/>).
/// </para>
/// <para>
/// <b>DuckDB has no savepoints</b> (measured: a syntax error). So a batch that is all or nothing runs in one transaction
/// and, at its first failure, applies no more: the rest are answered as not applied, saying why. A batch that may keep
/// some runs each edit in its own transaction, which is the same answer the savepoints give on the other engines.
/// </para>
/// <para>
/// <b>A new row's key comes back through <c>RETURNING</c></b>, from a column with a default (a sequence), or the add
/// is refused with the reason. Shapes are two-dimensional here; a shape with Z or M is refused rather than flattened. A
/// row's version is an MD5 of its columns and shape as JSON, compared in the <c>where</c>.
/// </para>
/// </remarks>
public sealed class DuckDbFeatureWriter : IFeatureWriter
{
    private readonly GeoParquetFolder _folder;
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<FieldDescription> _fields;
    private readonly EditColumns _columns;
    private readonly EditorTracking _tracking;
    private readonly IProjector _projector;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the writer.</summary>
    /// <param name="folder">The attached database's sandboxed DuckDB, opened for writing.</param>
    /// <param name="layer">The layer; its table is in the database's <c>main</c> schema.</param>
    /// <param name="fields">The layer's real columns, from its describe — the whitelist.</param>
    /// <param name="projector">Moves a geometry sent in another reference into the layer's.</param>
    /// <param name="tracking">Which columns record edits — ADR-064 — or null.</param>
    /// <param name="subtypes">The layer's subtypes — ADR-065 — or null.</param>
    /// <param name="statementTimeout">How long one batch may run; thirty seconds when null.</param>
    public DuckDbFeatureWriter(
        GeoParquetFolder folder,
        LayerDefinition layer,
        IReadOnlyList<FieldDescription> fields,
        IProjector projector,
        EditorTracking? tracking = null,
        LayerSubtypes? subtypes = null,
        TimeSpan? statementTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(projector);

        if (!folder.IsAttached || !folder.IsWritable)
        {
            throw new QueryNotSupportedException(
                $"Layer '{layer.Name}' is read from {(folder.IsAttached ? "a DuckDB database this server could open only to read — a read-only mount, or a file it may not write" : "files, which are read in place and never written (ADR-066)")}, so it cannot be edited.");
        }

        if (layer.IntegerIdentityColumn is null
            || string.Equals(layer.IdentityColumn, GeoParquetFolder.RowNumberColumn, StringComparison.Ordinal))
        {
            throw new QueryNotSupportedException(
                $"Layer '{layer.Name}' identifies its features by DuckDB's row number, which moves when a row is deleted, "
                + "so its features cannot be addressed for an edit. Publish it with an integer column of its own as the "
                + "object id (ADR-211).");
        }

        _folder = folder;
        _layer = layer;
        _fields = fields;
        _columns = new EditColumns(layer, fields, subtypes);
        _tracking = tracking ?? EditorTracking.None;
        _projector = projector;
        _timeout = statementTimeout ?? TimeSpan.FromSeconds(30);
    }

    private string Id => GeoParquetFolder.Quote(_layer.IntegerIdentityColumn!);

    private string Shape => GeoParquetFolder.Quote(_layer.GeometryColumn);

    private string Table => "src.main." + GeoParquetFolder.Quote(_layer.TableName);

    private const string Now = "current_timestamp";

    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        await _folder.Writes.WaitAsync(deadline.Token).ConfigureAwait(false);

        try
        {
            using DuckDBConnection connection = _folder.Open();
            EditOutcome outcome = batch.RollbackOnFailure
                ? await AllOrNothingAsync(connection, batch, deadline.Token).ConfigureAwait(false)
                : await EachAloneAsync(connection, batch, deadline.Token).ConfigureAwait(false);

            if (!outcome.RolledBack && batch.Count > 0)
            {
                _folder.Edited(_layer.TableName);
            }

            return outcome;
        }
        finally
        {
            _folder.Writes.Release();
        }
    }

    /// <summary>Every edit in one transaction; at the first failure nothing more is applied and nothing is kept.</summary>
    private async Task<EditOutcome> AllOrNothingAsync(DuckDBConnection connection, EditBatch batch, CancellationToken cancellationToken)
    {
        Run(connection, "begin transaction");

        List<EditResult> adds = [];
        List<EditResult> updates = [];
        List<EditResult> deletes = [];
        bool failed = batch.AnythingAlreadyFailed;

        try
        {
            Dictionary<long, long?> subtypes = StoredSubtypes(connection, _columns.NeedingStoredSubtype(batch.Updates));
            Facts facts = FactsOf(connection);

            foreach (FeatureAdd add in batch.Adds)
            {
                adds.Add(failed ? NotApplied(-1) : await AddAsync(connection, facts, add, batch, cancellationToken).ConfigureAwait(false));
                failed |= !adds[^1].Succeeded;
            }

            foreach (FeatureUpdate update in batch.Updates)
            {
                updates.Add(failed
                    ? NotApplied(update.Identity)
                    : await UpdateAsync(connection, facts, update, subtypes.TryGetValue(update.Identity, out long? kind) ? kind : null, batch, cancellationToken)
                        .ConfigureAwait(false));
                failed |= !updates[^1].Succeeded;
            }

            foreach (long identity in batch.Deletes)
            {
                deletes.Add(failed ? NotApplied(identity) : Delete(connection, facts, identity, batch));
                failed |= !deletes[^1].Succeeded;
            }
        }
        catch
        {
            TryRun(connection, "rollback");
            throw;
        }

        if (failed)
        {
            TryRun(connection, "rollback");
            return new EditOutcome(adds, updates, deletes, RolledBack: true);
        }

        Run(connection, "commit");
        return new EditOutcome(adds, updates, deletes, RolledBack: false);
    }

    /// <summary>Each edit in its own transaction — what a savepoint per edit gives on the other engines.</summary>
    private async Task<EditOutcome> EachAloneAsync(DuckDBConnection connection, EditBatch batch, CancellationToken cancellationToken)
    {
        Dictionary<long, long?> subtypes = StoredSubtypes(connection, _columns.NeedingStoredSubtype(batch.Updates));
        Facts facts = FactsOf(connection);

        List<EditResult> adds = [];
        List<EditResult> updates = [];
        List<EditResult> deletes = [];

        foreach (FeatureAdd add in batch.Adds)
        {
            adds.Add(await AloneAsync(connection, () => AddAsync(connection, facts, add, batch, cancellationToken)).ConfigureAwait(false));
        }

        foreach (FeatureUpdate update in batch.Updates)
        {
            updates.Add(await AloneAsync(connection, () => UpdateAsync(
                connection, facts, update, subtypes.TryGetValue(update.Identity, out long? kind) ? kind : null, batch, cancellationToken)).ConfigureAwait(false));
        }

        foreach (long identity in batch.Deletes)
        {
            deletes.Add(await AloneAsync(connection, () => Task.FromResult(Delete(connection, facts, identity, batch))).ConfigureAwait(false));
        }

        return new EditOutcome(adds, updates, deletes, RolledBack: false);
    }

    private static async Task<EditResult> AloneAsync(DuckDBConnection connection, Func<Task<EditResult>> edit)
    {
        Run(connection, "begin transaction");

        EditResult result;

        try
        {
            result = await edit().ConfigureAwait(false);
        }
        catch
        {
            TryRun(connection, "rollback");
            throw;
        }

        if (result.Succeeded)
        {
            Run(connection, "commit");
        }
        else
        {
            TryRun(connection, "rollback");
        }

        return result;
    }

    private static EditResult NotApplied(long identity) => EditResult.Failed(
        identity,
        "Not applied: an earlier edit in this batch failed, and the batch asked for all or nothing. DuckDB keeps no "
        + "savepoints, so the edits after a failure are not tried (ADR-211).");

    /// <param name="KeyMade">Whether the key column has a default, a sequence most often, that makes a new row's key.</param>
    /// <param name="GlobalKept">Whether the GlobalID column is a UUID this writer can mint.</param>
    private sealed record Facts(bool KeyMade, bool GlobalKept);

    private Facts FactsOf(DuckDBConnection connection)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText =
            "select (select column_default from duckdb_columns() where database_name = 'src' and schema_name = 'main' "
            + "and table_name = $t and column_name = $k), "
            + "(select data_type from duckdb_columns() where database_name = 'src' and schema_name = 'main' "
            + "and table_name = $t and column_name = $u)";
        command.Parameters.Add(new DuckDBParameter("t", _layer.TableName));
        command.Parameters.Add(new DuckDBParameter("k", _layer.IntegerIdentityColumn!));
        command.Parameters.Add(new DuckDBParameter("u", (object?)_columns.GlobalId ?? DBNull.Value));

        using DuckDBDataReader reader = command.ExecuteReader();
        reader.Read();

        return new Facts(!reader.IsDBNull(0), !reader.IsDBNull(1) && string.Equals(reader.GetString(1), "UUID", StringComparison.Ordinal));
    }

    private async Task<EditResult> AddAsync(DuckDBConnection connection, Facts facts, FeatureAdd add, EditBatch batch, CancellationToken cancellationToken)
    {
        if (!facts.KeyMade)
        {
            return EditResult.Failed(
                -1,
                $"'{_layer.IntegerIdentityColumn}' is this table's key and the database does not make it — it has no "
                + "default, such as nextval of a sequence — so this server cannot add a row without inventing a number the "
                + "table's own users did not choose. Updates and deletes are answered (ADR-211).");
        }

        if (add.Geometry is { IsEmpty: false } shape
            && EditColumns.DimensionRefusal(shape, GeometryOrdinates.None, "a DuckDB layer's geometry column") is { } refused)
        {
            return EditResult.Failed(-1, refused);
        }

        if (!_columns.TryBind(add.Attributes, null, out List<(string Column, object? Value)> bound, out string? error, keepGlobalId: batch.KeepsGlobalIds))
        {
            return EditResult.Failed(-1, error!);
        }

        Guid? global = null;

        if (_columns.GlobalId is { } globalColumn && facts.GlobalKept)
        {
            int at = bound.FindIndex(b => string.Equals(b.Column, globalColumn, StringComparison.Ordinal));

            if (at >= 0 && bound[at].Value is Guid kept)
            {
                global = kept;
            }
            else
            {
                global = Guid.NewGuid();
                bound.RemoveAll(b => string.Equals(b.Column, globalColumn, StringComparison.Ordinal));
                bound.Add((globalColumn, global));
            }
        }

        List<string> columns = [.. bound.Select(b => GeoParquetFolder.Quote(b.Column))];
        List<string> values = [.. bound.Select((_, i) => "$v" + i.ToString(CultureInfo.InvariantCulture))];
        bool shaped = add.Geometry is { IsEmpty: false };

        if (shaped)
        {
            columns.Add(Shape);
            values.Add("st_geomfromwkb($geom)");
        }

        if (columns.Count == 0)
        {
            return EditResult.Failed(-1, "The feature has neither attributes nor geometry, so there is nothing to add.");
        }

        bool named = Sign(columns, values, batch, creating: true);

        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"insert into {Table} ({string.Join(", ", columns)}) values ({string.Join(", ", values)}) returning {Id}";
        Bind(command, bound);

        if (shaped)
        {
            command.Parameters.Add(new DuckDBParameter("geom", await WkbAsync(add.Geometry!, add.GeometrySrid, cancellationToken).ConfigureAwait(false)));
        }

        if (named)
        {
            command.Parameters.Add(new DuckDBParameter("editor", batch.Editor!));
        }

        try
        {
            return EditResult.Ok(Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture)) with { GlobalId = global };
        }
        catch (DuckDBException e)
        {
            return EditResult.Failed(-1, Explain(e));
        }
    }

    private async Task<EditResult> UpdateAsync(
        DuckDBConnection connection, Facts facts, FeatureUpdate update, long? storedSubtype, EditBatch batch, CancellationToken cancellationToken)
    {
        string[]? expected = Expected(batch, update.Identity);

        if (update.Geometry is not null
            && EditColumns.DimensionRefusal(update.Geometry, GeometryOrdinates.None, "a DuckDB layer's geometry column") is { } lossy)
        {
            return EditResult.Failed(update.Identity, lossy);
        }

        if (!_columns.TryBind(update.Attributes, storedSubtype, out List<(string Column, object? Value)> bound, out string? error))
        {
            return EditResult.Failed(update.Identity, error!);
        }

        string? owner = EditColumns.OwnerOf(batch);

        if (LeftOutsideItsNewSubtype(connection, update, owner) is { } outside)
        {
            return EditResult.Failed(update.Identity, outside);
        }

        List<string> assignments = [.. bound.Select((b, i) => $"{GeoParquetFolder.Quote(b.Column)} = $v{i.ToString(CultureInfo.InvariantCulture)}")];
        bool shaped = update.Geometry is { IsEmpty: false };

        if (update.Geometry is not null)
        {
            assignments.Add(shaped ? $"{Shape} = st_geomfromwkb($geom)" : $"{Shape} = null");
        }

        if (assignments.Count == 0)
        {
            return Exists(connection, update.Identity)
                ? owner is not null && Whose(connection, update.Identity, owner) is { } notYours
                    ? notYours
                    : EditResult.Ok(update.Identity) with { GlobalId = GlobalOf(connection, facts, update.Identity) }
                : EditResult.Missing(update.Identity);
        }

        bool named = false;

        if (_tracking.Editor is not null && batch.Editor is not null)
        {
            assignments.Add($"{GeoParquetFolder.Quote(_tracking.Editor)} = $editor");
            named = true;
        }

        if (_tracking.Edited is not null)
        {
            assignments.Add($"{GeoParquetFolder.Quote(_tracking.Edited)} = {Now}");
        }

        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"update {Table} set {string.Join(", ", assignments)} where {Id} = $id{VersionClause(expected, command)}{OwnerClause(owner)}";
        Bind(command, bound);

        if (shaped)
        {
            command.Parameters.Add(new DuckDBParameter("geom", await WkbAsync(update.Geometry!, update.GeometrySrid, cancellationToken).ConfigureAwait(false)));
        }

        if (named)
        {
            command.Parameters.Add(new DuckDBParameter("editor", batch.Editor!));
        }

        command.Parameters.Add(new DuckDBParameter("id", update.Identity));

        if (owner is not null)
        {
            command.Parameters.Add(new DuckDBParameter("owner", owner));
        }

        try
        {
            return command.ExecuteNonQuery() > 0
                ? EditResult.Ok(update.Identity) with { GlobalId = GlobalOf(connection, facts, update.Identity) }
                : WhyNot(connection, update.Identity, owner, expected);
        }
        catch (DuckDBException e)
        {
            return EditResult.Failed(update.Identity, Explain(e));
        }
    }

    private EditResult Delete(DuckDBConnection connection, Facts facts, long identity, EditBatch batch)
    {
        string[]? expected = Expected(batch, identity);
        string? owner = EditColumns.OwnerOf(batch);
        Guid? global = GlobalOf(connection, facts, identity);

        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"delete from {Table} where {Id} = $id{VersionClause(expected, command)}{OwnerClause(owner)}";
        command.Parameters.Add(new DuckDBParameter("id", identity));

        if (owner is not null)
        {
            command.Parameters.Add(new DuckDBParameter("owner", owner));
        }

        try
        {
            return command.ExecuteNonQuery() > 0
                ? EditResult.Ok(identity) with { GlobalId = global }
                : WhyNot(connection, identity, owner, expected);
        }
        catch (DuckDBException e)
        {
            return EditResult.Failed(identity, Explain(e));
        }
    }

    /// <summary>The object ids of the features carrying these GlobalIDs — <c>useGlobalIds=true</c>.</summary>
    /// <param name="folder">The attached database's DuckDB.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="globalIdColumn">Its GlobalID column, a UUID.</param>
    /// <param name="globalIds">The GlobalIDs.</param>
    /// <returns>Each GlobalID found, with its object id.</returns>
    public static IReadOnlyDictionary<Guid, long> ObjectIdsOf(
        GeoParquetFolder folder, LayerDefinition layer, string globalIdColumn, IReadOnlyCollection<Guid> globalIds)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(globalIds);

        Dictionary<Guid, long> found = [];

        if (globalIds.Count == 0 || layer.IntegerIdentityColumn is null)
        {
            return found;
        }

        using DuckDBConnection connection = folder.Open();
        using DuckDBCommand command = connection.CreateCommand();
        Guid[] wanted = [.. globalIds];

        command.CommandText =
            $"select {GeoParquetFolder.Quote(globalIdColumn)}, {GeoParquetFolder.Quote(layer.IntegerIdentityColumn)} "
            + $"from src.main.{GeoParquetFolder.Quote(layer.TableName)} where {GeoParquetFolder.Quote(globalIdColumn)} in ("
            + string.Join(", ", wanted.Select((_, i) => "$g" + i.ToString(CultureInfo.InvariantCulture))) + ")";

        for (int i = 0; i < wanted.Length; i++)
        {
            command.Parameters.Add(new DuckDBParameter("g" + i.ToString(CultureInfo.InvariantCulture), wanted[i]));
        }

        using DuckDBDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!reader.IsDBNull(0) && reader.GetValue(0) is Guid global)
            {
                found[global] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        return found;
    }

    /// <summary>The SQL of a row's version: an MD5 of its columns and its shape as JSON.</summary>
    /// <param name="columns">The table's columns other than the shape.</param>
    /// <param name="shape">The geometry column.</param>
    /// <returns>The expression, thirty-two hexadecimal characters.</returns>
    internal static string VersionSqlOf(IEnumerable<string> columns, string shape) =>
        "md5(to_json({"
        + string.Join(", ", columns.Select(c => $"'{c.Replace("'", "''", StringComparison.Ordinal)}': {GeoParquetFolder.Quote(c)}"))
        + $"{(columns.Any() ? ", " : string.Empty)}'graticula_shape': st_aswkb({GeoParquetFolder.Quote(shape)})}})::varchar)";

    private Guid? GlobalOf(DuckDBConnection connection, Facts facts, long identity)
    {
        if (!facts.GlobalKept || _columns.GlobalId is not { } column)
        {
            return null;
        }

        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"select {GeoParquetFolder.Quote(column)} from {Table} where {Id} = $id";
        command.Parameters.Add(new DuckDBParameter("id", identity));
        return command.ExecuteScalar() as Guid?;
    }

    private static string[]? Expected(EditBatch batch, long identity) =>
        batch.Expects is { } expects && expects.TryGetValue(identity, out IReadOnlyList<string>? versions) && versions.Count > 0
            ? [.. versions]
            : null;

    /// <summary>The precondition in the <c>where</c>: the row's version is one of those expected — D-186.</summary>
    private string VersionClause(string[]? expected, DuckDBCommand command)
    {
        if (expected is null)
        {
            return string.Empty;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            command.Parameters.Add(new DuckDBParameter("x" + i.ToString(CultureInfo.InvariantCulture), expected[i]));
        }

        return $" and {VersionSqlOf(VersionColumns, _layer.GeometryColumn)} in ("
            + string.Join(", ", expected.Select((_, i) => "$x" + i.ToString(CultureInfo.InvariantCulture))) + ")";
    }

    private IEnumerable<string> VersionColumns =>
        _fields.Select(f => f.Name).Where(n => !string.Equals(n, _layer.GeometryColumn, StringComparison.Ordinal)
                                              && !string.Equals(n, GeoParquetFolder.RowNumberColumn, StringComparison.Ordinal));

    private bool Sign(List<string> columns, List<string> values, EditBatch batch, bool creating)
    {
        bool named = false;

        foreach ((string? column, string value, bool isName) in (ReadOnlySpan<(string?, string, bool)>)
        [
            (creating ? _tracking.Creator : null, "$editor", true),
            (_tracking.Editor, "$editor", true),
            (creating ? _tracking.Created : null, Now, false),
            (_tracking.Edited, Now, false),
        ])
        {
            if (column is null || (isName && batch.Editor is null))
            {
                continue;
            }

            columns.Add(GeoParquetFolder.Quote(column));
            values.Add(value);
            named |= isName;
        }

        return named;
    }

    private async Task<byte[]> WkbAsync(Geometry geometry, int? sentIn, CancellationToken cancellationToken)
    {
        Geometry placed = geometry;

        if (sentIn is { } from && from != _layer.Srid)
        {
            (IReadOnlyList<Geometry> moved, _) = await _projector.ProjectAsync([geometry], from, _layer.Srid, cancellationToken).ConfigureAwait(false);
            placed = moved[0];
        }

        return WkbWriter.ToArray(placed);
    }

    private Dictionary<long, long?> StoredSubtypes(DuckDBConnection connection, long[] needed)
    {
        Dictionary<long, long?> codes = [];

        if (needed.Length == 0 || _columns.SubtypeField is not { } field)
        {
            return codes;
        }

        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"select {Id}, {GeoParquetFolder.Quote(field)} from {Table} where {Id} in ("
            + string.Join(", ", needed.Select((_, i) => "$i" + i.ToString(CultureInfo.InvariantCulture))) + ")";

        for (int i = 0; i < needed.Length; i++)
        {
            command.Parameters.Add(new DuckDBParameter("i" + i.ToString(CultureInfo.InvariantCulture), needed[i]));
        }

        using DuckDBDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            codes[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return codes;
    }

    private string? LeftOutsideItsNewSubtype(DuckDBConnection connection, FeatureUpdate update, string? owner)
    {
        if (_columns.SubtypeMove(update) is not { } move)
        {
            return null;
        }

        (Subtype target, long code, FieldDescription[] unsent) = move;

        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"select {GeoParquetFolder.Quote(_columns.SubtypeField!)}"
            + string.Concat(unsent.Select(f => ", " + GeoParquetFolder.Quote(f.Name)))
            + $" from {Table} where {Id} = $id{OwnerClause(owner)}";
        command.Parameters.Add(new DuckDBParameter("id", update.Identity));

        if (owner is not null)
        {
            command.Parameters.Add(new DuckDBParameter("owner", owner));
        }

        using DuckDBDataReader reader = command.ExecuteReader();

        if (!reader.Read() || (!reader.IsDBNull(0) && Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) == code))
        {
            return null;
        }

        for (int i = 0; i < unsent.Length; i++)
        {
            if (EditColumns.SubtypeMoveRefusal(target, unsent[i], reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1)) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    private string OwnerClause(string? owner) =>
        owner is null ? string.Empty
        : _tracking.Creator is null ? " and 1 = 0"
        : $" and {GeoParquetFolder.Quote(_tracking.Creator)} = $owner";

    private bool Exists(DuckDBConnection connection, long identity)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {Table} where {Id} = $id";
        command.Parameters.Add(new DuckDBParameter("id", identity));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private EditResult WhyNot(DuckDBConnection connection, long identity, string? owner, string[]? expected)
    {
        if (expected is not null)
        {
            using DuckDBCommand command = connection.CreateCommand();
            command.CommandText = $"select {VersionSqlOf(VersionColumns, _layer.GeometryColumn)} from {Table} where {Id} = $id";
            command.Parameters.Add(new DuckDBParameter("id", identity));

            switch (command.ExecuteScalar())
            {
                case null or DBNull:
                    return EditResult.Missing(identity);
                case string version when !expected.Contains(version, StringComparer.OrdinalIgnoreCase):
                    return EditResult.Stale(identity);
            }
        }

        return owner is not null && Whose(connection, identity, owner) is { } refused ? refused : EditResult.Missing(identity);
    }

    private EditResult? Whose(DuckDBConnection connection, long identity, string owner)
    {
        if (_tracking.Creator is null)
        {
            return EditColumns.NotOwned(identity, recordsCreator: false, creator: null);
        }

        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = $"select {GeoParquetFolder.Quote(_tracking.Creator)} from {Table} where {Id} = $id";
        command.Parameters.Add(new DuckDBParameter("id", identity));

        using DuckDBDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return EditResult.Missing(identity);
        }

        string? creator = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);

        return creator is not null && string.Equals(creator, owner, StringComparison.Ordinal)
            ? null
            : EditColumns.NotOwned(identity, recordsCreator: true, creator);
    }

    private static void Bind(DuckDBCommand command, List<(string Column, object? Value)> bound)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            command.Parameters.Add(new DuckDBParameter("v" + i.ToString(CultureInfo.InvariantCulture), bound[i].Value ?? DBNull.Value));
        }
    }

    private static void Run(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryRun(DuckDBConnection connection, string sql)
    {
        try
        {
            Run(connection, sql);
        }
        catch (DuckDBException)
        {
        }
    }

    /// <summary>A refusal in words the caller can act on — the column, never DuckDB's own sentence.</summary>
    internal static string Explain(DuckDBException e) => e.Message switch
    {
        { } m when m.Contains("NOT NULL constraint failed", StringComparison.Ordinal) => ColumnAfterDot(m) is { } column
            ? $"'{column}' is required, and this edit leaves it without a value."
            : "A required field is left without a value by this edit.",
        { } m when m.Contains("Duplicate key", StringComparison.OrdinalIgnoreCase) || m.Contains("PRIMARY KEY or UNIQUE", StringComparison.Ordinal) =>
            "This would duplicate a value that must be unique in this layer.",
        { } m when m.Contains("foreign key", StringComparison.OrdinalIgnoreCase) => "This refers to a row that does not exist, or other rows refer to this one.",
        { } m when m.Contains("CHECK constraint failed", StringComparison.Ordinal) => "A rule on this layer's table refused this value.",
        { } m when m.Contains("Conversion Error", StringComparison.Ordinal) || m.Contains("Could not convert", StringComparison.Ordinal) =>
            "A value is the wrong type or out of range for its column.",
        _ => "The database refused this edit (DuckDB).",
    };

    private static string? ColumnAfterDot(string message) =>
        Regex.Match(message, @"constraint failed: [^.\s]+\.([^\s]+)") is { Success: true } found ? found.Groups[1].Value : null;
}
