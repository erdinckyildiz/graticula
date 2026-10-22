using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using MySqlConnector;

namespace Graticula.Providers.MySql;

/// <summary>Applies edits to a registered MySQL table — ADR-208 condition 3.</summary>
/// <remarks>
/// <para>
/// <b><c>OracleFeatureWriter</c>'s shape: <see cref="EditColumns"/> decides what an edit may send</b>, as for PostGIS and
/// Oracle; what is MySQL's is the SQL. One transaction, each edit in its own savepoint.
/// </para>
/// <para>
/// <b>Where MySQL differs.</b> It has no <c>RETURNING</c>, so a new row's key is <c>LAST_INSERT_ID()</c> — the key is an
/// <c>AUTO_INCREMENT</c> column or the add is refused with the reason, since this server does not invent numbers. It keeps
/// x and y only, so a shape with Z or M is refused rather than flattened (ADR-077). A row's version is a hash of what it
/// holds (<c>MySqlFeatureSource.VersionSqlOf</c>), and an edit conditional on one carries it in its <c>where</c>. A
/// polygon is stored as sent, as on Oracle (ADR-205 §5b item 2).
/// </para>
/// </remarks>
public sealed class MySqlFeatureWriter : IFeatureWriter
{
    private readonly string _connectionString;
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<FieldDescription> _fields;
    private readonly EditColumns _columns;
    private readonly EditorTracking _tracking;
    private readonly IProjector _projector;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the writer.</summary>
    /// <param name="locator">The data source's <c>mysql:</c> locator.</param>
    /// <param name="layer">The layer; its schema is the MySQL database.</param>
    /// <param name="fields">The layer's real columns, from its describe — the whitelist.</param>
    /// <param name="projector">Moves a geometry sent in another reference into the layer's.</param>
    /// <param name="tracking">Which columns record edits — ADR-064 — or null.</param>
    /// <param name="subtypes">The layer's subtypes — ADR-065 — or null.</param>
    /// <param name="statementTimeout">How long one statement may run; thirty seconds when null.</param>
    public MySqlFeatureWriter(
        string locator,
        LayerDefinition layer,
        IReadOnlyList<FieldDescription> fields,
        IProjector projector,
        EditorTracking? tracking = null,
        LayerSubtypes? subtypes = null,
        TimeSpan? statementTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(projector);

        if (layer.IntegerIdentityColumn is null)
        {
            throw new ArgumentException(
                $"Layer '{layer.Name}' has no integer object-id column, so its features cannot be addressed for update or "
                + "delete. It is readable and not editable.",
                nameof(layer));
        }

        _connectionString = MySqlConnections.For(locator, pooling: true, userVariables: true);
        _layer = layer;
        _fields = fields;
        _columns = new EditColumns(layer, fields, subtypes);
        _tracking = tracking ?? EditorTracking.None;
        _projector = projector;
        _timeout = statementTimeout ?? TimeSpan.FromSeconds(30);
    }

    private string Id => LayerDefinition.Quote(_layer.IntegerIdentityColumn!);

    private string Shape => LayerDefinition.Quote(_layer.RequireGeometryColumn());

    /// <summary>Why a shape cannot be written to a table — it has no column to put one in (ADR-213), as on PostGIS.</summary>
    private string TableRefusal =>
        $"'{_layer.Name}' is a table and keeps no geometry; send the feature's attributes without one.";

    private string Table => _layer.QuotedTable;

    /// <summary>The database's clock, in UTC — the session's time zone is UTC (MySqlConnections).</summary>
    private const string Now = "utc_timestamp(6)";

    /// <summary>The session variable a history trigger reads the editor from — ADR-207.</summary>
    internal const string EditorVariable = "@graticula_editor";

    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await using MySqlConnection connection = await MySqlConnections.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        EditOutcome outcome = await ApplyWithinAsync(connection, transaction, batch, cancellationToken).ConfigureAwait(false);

        if (MustRollBack(batch, outcome))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return outcome with { RolledBack = true };
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Whether a batch's outcome means its transaction is discarded: all or nothing, and not all.</summary>
    public static bool MustRollBack(EditBatch batch, EditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(outcome);

        return batch.RollbackOnFailure && (!outcome.AllSucceeded || batch.AnythingAlreadyFailed);
    }

    /// <summary>One layer's part of an edit across several, for <see cref="ApplyTogetherAsync"/>.</summary>
    /// <param name="Layer">The layer.</param>
    /// <param name="Fields">Its real columns.</param>
    /// <param name="Tracking">Its edit-tracking columns, or null.</param>
    /// <param name="Subtypes">Its subtypes, or null.</param>
    /// <param name="Batch">Its edits.</param>
    public sealed record LayerBatch(
        LayerDefinition Layer, IReadOnlyList<FieldDescription> Fields, EditorTracking? Tracking, LayerSubtypes? Subtypes, EditBatch Batch);

    /// <summary>Several layers' edits in one MySQL database, in one transaction — as several Oracle layers' are.</summary>
    /// <param name="locator">The database's <c>mysql:</c> locator, which every layer shares.</param>
    /// <param name="layers">The layers' edits.</param>
    /// <param name="projector">Moves a geometry sent in another reference into its layer's.</param>
    /// <param name="statementTimeout">How long one statement may run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each layer's outcome, in order, every one rolled back if any was.</returns>
    public static async Task<IReadOnlyList<EditOutcome>> ApplyTogetherAsync(
        string locator, IReadOnlyList<LayerBatch> layers, IProjector projector, TimeSpan? statementTimeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(projector);

        await using MySqlConnection connection =
            await MySqlConnections.OpenAsync(MySqlConnections.For(locator, pooling: true, userVariables: true), cancellationToken).ConfigureAwait(false);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        List<EditOutcome> outcomes = [];
        bool rollBack = false;

        foreach (LayerBatch part in layers)
        {
            MySqlFeatureWriter writer = new(locator, part.Layer, part.Fields, projector, part.Tracking, part.Subtypes, statementTimeout);
            EditOutcome outcome = await writer.ApplyWithinAsync(connection, transaction, part.Batch, cancellationToken).ConfigureAwait(false);

            rollBack |= MustRollBack(part.Batch, outcome);
            outcomes.Add(outcome);
        }

        if (rollBack)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return [.. outcomes.Select(o => o with { RolledBack = true })];
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcomes;
    }

    /// <summary>The object ids of the features carrying these GlobalIDs — <c>useGlobalIds=true</c>.</summary>
    /// <param name="locator">The data source's <c>mysql:</c> locator.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="globalIdColumn">Its GlobalID column.</param>
    /// <param name="globalIds">The GlobalIDs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each GlobalID found, with its object id.</returns>
    public static async Task<IReadOnlyDictionary<Guid, long>> ObjectIdsOfAsync(
        string locator, LayerDefinition layer, string globalIdColumn, IReadOnlyCollection<Guid> globalIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(globalIds);

        Dictionary<Guid, long> found = [];

        if (globalIds.Count == 0 || layer.IntegerIdentityColumn is null)
        {
            return found;
        }

        await using MySqlConnection connection =
            await MySqlConnections.OpenAsync(MySqlConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

        if (await GlobalStorageAsync(connection, layer, globalIdColumn, null, cancellationToken).ConfigureAwait(false) is not { } storage)
        {
            return found;
        }

        string column = LayerDefinition.Quote(globalIdColumn);

        await using MySqlCommand command = new(
            $"select {column}, {LayerDefinition.Quote(layer.IntegerIdentityColumn)} from {layer.QuotedTable} "
            + $"where {MySqlGuids.Text(column, storage)} in (select j.v from json_table(@ids, '$[*]' columns (v varchar(38) path '$')) j)",
            connection);

        command.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(globalIds.Select(MySqlGuids.Braced)));

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0) && MySqlGuids.Read(reader.GetValue(0)) is { } global)
            {
                found[global] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        return found;
    }

    /// <summary>A batch in a transaction the caller owns, the editor named to the session for its length.</summary>
    private async Task<EditOutcome> ApplyWithinAsync(
        MySqlConnection connection, MySqlTransaction transaction, EditBatch batch, CancellationToken cancellationToken)
    {
        // Who is editing, for a history trigger (ADR-207) — a session variable, cleared before the connection goes back.
        await using (MySqlCommand who = Command($"set {EditorVariable} = @editor", connection, transaction))
        {
            who.Parameters.AddWithValue("@editor", (object?)batch.Editor ?? DBNull.Value);
            await who.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ApplyNamedAsync(connection, transaction, batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await using MySqlCommand forget = Command($"set {EditorVariable} = null", connection, transaction);
            await forget.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<EditOutcome> ApplyNamedAsync(
        MySqlConnection connection, MySqlTransaction transaction, EditBatch batch, CancellationToken cancellationToken)
    {
        TableFacts facts = await FactsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        List<EditResult> adds = [];
        List<EditResult> updates = [];
        List<EditResult> deletes = [];

        IReadOnlyDictionary<long, long?> subtypes =
            await StoredSubtypesAsync(connection, transaction, _columns.NeedingStoredSubtype(batch.Updates), cancellationToken).ConfigureAwait(false);

        int savepoint = 0;

        foreach (FeatureAdd add in batch.Adds)
        {
            adds.Add(await IsolatedAsync(transaction, savepoint++, () => AddAsync(connection, transaction, facts, add, batch, cancellationToken), cancellationToken)
                .ConfigureAwait(false));
        }

        foreach (FeatureUpdate update in batch.Updates)
        {
            updates.Add(await IsolatedAsync(
                transaction, savepoint++,
                () => UpdateAsync(connection, transaction, facts, update, subtypes.TryGetValue(update.Identity, out long? kind) ? kind : null, batch, cancellationToken),
                cancellationToken).ConfigureAwait(false));
        }

        foreach (long identity in batch.Deletes)
        {
            deletes.Add(await IsolatedAsync(transaction, savepoint++, () => DeleteAsync(connection, transaction, facts, identity, batch, cancellationToken), cancellationToken)
                .ConfigureAwait(false));
        }

        return new EditOutcome(adds, updates, deletes, RolledBack: false);
    }

    /// <summary>One edit in its own savepoint, so its failure does not take the others.</summary>
    private static async Task<EditResult> IsolatedAsync(
        MySqlTransaction transaction, int ordinal, Func<Task<EditResult>> edit, CancellationToken cancellationToken)
    {
        string name = "edit" + ordinal.ToString(CultureInfo.InvariantCulture);

        await transaction.SaveAsync(name, cancellationToken).ConfigureAwait(false);

        EditResult result = await edit().ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await transaction.RollbackAsync(name, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>What the table says about itself, read once per batch.</summary>
    /// <param name="Srid">The column's SRID, which a written shape is given.</param>
    /// <param name="KeyMade">Whether the key is AUTO_INCREMENT.</param>
    /// <param name="GlobalStorage">How the GlobalID column keeps its value, or null.</param>
    /// <param name="Dialect">Which server: MySQL or MariaDB (ADR-209).</param>
    private sealed record TableFacts(int Srid, bool KeyMade, MySqlGuids.Storage? GlobalStorage, MySqlDialect Dialect);

    private async Task<TableFacts> FactsAsync(MySqlConnection connection, MySqlTransaction transaction, CancellationToken cancellationToken)
    {
        MySqlDialect dialect = MySqlDialect.Of(connection);

        // A table (ADR-213) has no spatial column; its reference is the one it carries, which nothing reads.
        await using MySqlCommand command = Command(
            $"""
            select {(_layer.IsTable ? "@srid" : $"(select g.srs_id from {dialect.GeometryColumns} g where g.table_schema = @db and g.table_name = @tab and g.column_name = @col)")},
                   (select count(*) from information_schema.columns c
                     where c.table_schema = @db and c.table_name = @tab and c.column_name = @key and c.extra like '%auto_increment%')
            """, connection, transaction);

        command.Parameters.AddWithValue("@db", _layer.SchemaName);
        command.Parameters.AddWithValue("@tab", _layer.TableName);
        command.Parameters.AddWithValue("@col", (object?)_layer.GeometryColumn ?? DBNull.Value);
        command.Parameters.AddWithValue("@srid", _layer.Srid);
        command.Parameters.AddWithValue("@key", _layer.IntegerIdentityColumn);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (reader.IsDBNull(0))
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' is served from {_layer.SchemaName}.{_layer.TableName}.{_layer.GeometryColumn}, which is "
                + "no longer a spatial column with an SRID.");
        }

        int srid = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
        bool keyMade = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture) > 0;
        await reader.DisposeAsync().ConfigureAwait(false);

        MySqlGuids.Storage? global = _columns.GlobalId is { } named
            ? await GlobalStorageAsync(connection, _layer, named, transaction, cancellationToken).ConfigureAwait(false)
            : null;

        return new TableFacts(srid, keyMade, global, dialect);
    }

    private static async Task<MySqlGuids.Storage?> GlobalStorageAsync(
        MySqlConnection connection, LayerDefinition layer, string column, MySqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = new(
            "select data_type, character_maximum_length from information_schema.columns "
            + "where table_schema = @db and table_name = @tab and column_name = @col", connection, transaction);

        command.Parameters.AddWithValue("@db", layer.SchemaName);
        command.Parameters.AddWithValue("@tab", layer.TableName);
        command.Parameters.AddWithValue("@col", column);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MySqlGuids.Of(column, reader.GetString(0), reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture))
            : null;
    }

    private async Task<EditResult> AddAsync(
        MySqlConnection connection, MySqlTransaction transaction, TableFacts facts, FeatureAdd add, EditBatch batch, CancellationToken cancellationToken)
    {
        if (!facts.KeyMade)
        {
            return EditResult.Failed(
                -1,
                $"'{_layer.IntegerIdentityColumn}' is this table's key and the database does not make it — it is not "
                + "AUTO_INCREMENT — so this server cannot add a row without inventing a number the table's own users did not "
                + "choose. Updates and deletes are answered (ADR-208).");
        }

        if (add.Geometry is not null && _layer.IsTable)
        {
            return EditResult.Failed(-1, TableRefusal);
        }

        if (add.Geometry is { IsEmpty: false } shape
            && EditColumns.DimensionRefusal(shape, GeometryOrdinates.None, "a MySQL layer's geometry column") is { } refused)
        {
            return EditResult.Failed(-1, refused);
        }

        if (!_columns.TryBind(add.Attributes, null, out List<(string Column, object? Value)> bound, out string? error, keepGlobalId: batch.KeepsGlobalIds))
        {
            return EditResult.Failed(-1, error!);
        }

        // Every new row gets a GlobalID, the client's under useGlobalIds or one minted here — Oracle's rule.
        Guid? global = null;

        if (_columns.GlobalId is { } globalColumn && facts.GlobalStorage is not null)
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

        List<string> columns = [.. bound.Select(b => LayerDefinition.Quote(b.Column))];
        List<string> values = [.. bound.Select((_, i) => "@v" + i.ToString(CultureInfo.InvariantCulture))];
        bool shaped = add.Geometry is { IsEmpty: false };

        if (shaped)
        {
            columns.Add(Shape);
            values.Add(facts.Dialect.FromWkb("@geom", facts.Srid));
        }

        if (columns.Count == 0)
        {
            return EditResult.Failed(-1, "The feature has neither attributes nor geometry, so there is nothing to add.");
        }

        bool named = Sign(columns, values, batch, creating: true);

        await using MySqlCommand command = Command(
            $"insert into {Table} ({string.Join(", ", columns)}) values ({string.Join(", ", values)})", connection, transaction);
        Bind(command, bound, facts);

        if (shaped)
        {
            command.Parameters.AddWithValue("@geom", await WkbAsync(add.Geometry!, add.GeometrySrid, cancellationToken).ConfigureAwait(false));
        }

        if (named)
        {
            command.Parameters.AddWithValue("@editor", batch.Editor!);
        }

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return EditResult.Ok(command.LastInsertedId) with { GlobalId = global };
        }
        catch (MySqlException e)
        {
            return EditResult.Failed(-1, Explain(e));
        }
    }

    private async Task<EditResult> UpdateAsync(
        MySqlConnection connection, MySqlTransaction transaction, TableFacts facts, FeatureUpdate update, long? storedSubtype,
        EditBatch batch, CancellationToken cancellationToken)
    {
        string[]? expected = Expected(batch, update.Identity);

        if (update.Geometry is not null && _layer.IsTable)
        {
            return EditResult.Failed(update.Identity, TableRefusal);
        }

        if (update.Geometry is not null
            && EditColumns.DimensionRefusal(update.Geometry, GeometryOrdinates.None, "a MySQL layer's geometry column") is { } lossy)
        {
            return EditResult.Failed(update.Identity, lossy);
        }

        if (!_columns.TryBind(update.Attributes, storedSubtype, out List<(string Column, object? Value)> bound, out string? error))
        {
            return EditResult.Failed(update.Identity, error!);
        }

        string? owner = EditColumns.OwnerOf(batch);

        if (await LeftOutsideItsNewSubtypeAsync(connection, transaction, update, owner, cancellationToken).ConfigureAwait(false) is { } outside)
        {
            return EditResult.Failed(update.Identity, outside);
        }

        List<string> assignments = [.. bound.Select((b, i) => $"t.{LayerDefinition.Quote(b.Column)} = @v{i.ToString(CultureInfo.InvariantCulture)}")];
        bool shaped = update.Geometry is not null;

        if (shaped)
        {
            assignments.Add(update.Geometry is { IsEmpty: false }
                ? $"t.{Shape} = {facts.Dialect.FromWkb("@geom", facts.Srid)}"
                : $"t.{Shape} = null");
        }

        if (assignments.Count == 0)
        {
            return await ExistsAsync(connection, transaction, update.Identity, cancellationToken).ConfigureAwait(false)
                ? owner is not null && await WhoseAsync(connection, transaction, update.Identity, owner, cancellationToken).ConfigureAwait(false) is { } notYours
                    ? notYours
                    : EditResult.Ok(update.Identity) with { GlobalId = await GlobalOfAsync(connection, transaction, facts, update.Identity, cancellationToken).ConfigureAwait(false) }
                : EditResult.Missing(update.Identity);
        }

        bool named = false;

        if (_tracking.Editor is not null && batch.Editor is not null)
        {
            assignments.Add($"t.{LayerDefinition.Quote(_tracking.Editor)} = @editor");
            named = true;
        }

        if (_tracking.Edited is not null)
        {
            assignments.Add($"t.{LayerDefinition.Quote(_tracking.Edited)} = {Now}");
        }

        // Ownership and the version ride in the `where`, so whose the row is, which version it is and the write are one
        // statement — ADR-064, D-186.
        await using MySqlCommand command = Command(
            $"update {Table} t set {string.Join(", ", assignments)} where t.{Id} = @id{VersionClause(expected)}{OwnerClause(owner)}",
            connection, transaction);
        Bind(command, bound, facts);

        if (update.Geometry is { IsEmpty: false } geometry)
        {
            command.Parameters.AddWithValue("@geom", await WkbAsync(geometry, update.GeometrySrid, cancellationToken).ConfigureAwait(false));
        }

        if (named)
        {
            command.Parameters.AddWithValue("@editor", batch.Editor!);
        }

        command.Parameters.AddWithValue("@id", update.Identity);
        BindExpected(command, expected);

        if (owner is not null)
        {
            command.Parameters.AddWithValue("@owner", owner);
        }

        try
        {
            // MySqlConnector reports the rows a statement matched, not only those it changed, so an update that writes
            // the values a row already holds is still a success.
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0
                ? EditResult.Ok(update.Identity) with { GlobalId = await GlobalOfAsync(connection, transaction, facts, update.Identity, cancellationToken).ConfigureAwait(false) }
                : await WhyNotAsync(connection, transaction, update.Identity, owner, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException e)
        {
            return EditResult.Failed(update.Identity, Explain(e));
        }
    }

    private async Task<EditResult> DeleteAsync(
        MySqlConnection connection, MySqlTransaction transaction, TableFacts facts, long identity, EditBatch batch, CancellationToken cancellationToken)
    {
        string[]? expected = Expected(batch, identity);
        string? owner = EditColumns.OwnerOf(batch);

        // No RETURNING: the GlobalID is read before the row goes.
        Guid? global = await GlobalOfAsync(connection, transaction, facts, identity, cancellationToken).ConfigureAwait(false);

        await using MySqlCommand command = Command(
            $"delete t from {Table} t where t.{Id} = @id{VersionClause(expected)}{OwnerClause(owner)}", connection, transaction);

        command.Parameters.AddWithValue("@id", identity);
        BindExpected(command, expected);

        if (owner is not null)
        {
            command.Parameters.AddWithValue("@owner", owner);
        }

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0
                ? EditResult.Ok(identity) with { GlobalId = global }
                : await WhyNotAsync(connection, transaction, identity, owner, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException e)
        {
            return EditResult.Failed(identity, Explain(e));
        }
    }

    private async Task<Guid?> GlobalOfAsync(
        MySqlConnection connection, MySqlTransaction transaction, TableFacts facts, long identity, CancellationToken cancellationToken)
    {
        if (facts.GlobalStorage is null || _columns.GlobalId is not { } column)
        {
            return null;
        }

        await using MySqlCommand command = Command($"select {LayerDefinition.Quote(column)} from {Table} where {Id} = @id", connection, transaction);
        command.Parameters.AddWithValue("@id", identity);

        return MySqlGuids.Read(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string[]? Expected(EditBatch batch, long identity) =>
        batch.Expects is { } expects && expects.TryGetValue(identity, out IReadOnlyList<string>? versions) && versions.Count > 0
            ? [.. versions]
            : null;

    /// <summary>The precondition in the <c>where</c>: the row's version is one of those expected — D-186.</summary>
    private string VersionClause(string[]? expected) =>
        expected is null
            ? string.Empty
            : $" and {MySqlFeatureSource.VersionSqlOf(VersionColumns, _layer.GeometryColumn, "t")} "
              + "in (select j.v from json_table(@expected, '$[*]' columns (v varchar(64) path '$')) j)";

    /// <summary>The columns a version is computed over — the describe's, as the reader's is (MySqlFeatureSource).</summary>
    private IEnumerable<string> VersionColumns =>
        _fields.Select(f => f.Name).Where(n => !string.Equals(n, _layer.GeometryColumn, StringComparison.Ordinal));

    private static void BindExpected(MySqlCommand command, string[]? expected)
    {
        if (expected is not null)
        {
            command.Parameters.AddWithValue("@expected", JsonSerializer.Serialize(expected));
        }
    }

    /// <summary>Adds the tracked columns this server writes on a new row, and says whether the editor is bound.</summary>
    private bool Sign(List<string> columns, List<string> values, EditBatch batch, bool creating)
    {
        bool named = false;

        foreach ((string? column, string value, bool isName) in (ReadOnlySpan<(string?, string, bool)>)
        [
            (creating ? _tracking.Creator : null, "@editor", true),
            (_tracking.Editor, "@editor", true),
            (creating ? _tracking.Created : null, Now, false),
            (_tracking.Edited, Now, false),
        ])
        {
            if (column is null || (isName && batch.Editor is null))
            {
                continue;
            }

            columns.Add(LayerDefinition.Quote(column));
            values.Add(value);
            named |= isName;
        }

        return named;
    }

    /// <summary>A geometry as WKB in the layer's reference — moved there when it was sent in another (Q-153).</summary>
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

    private async Task<IReadOnlyDictionary<long, long?>> StoredSubtypesAsync(
        MySqlConnection connection, MySqlTransaction transaction, long[] needed, CancellationToken cancellationToken)
    {
        Dictionary<long, long?> codes = [];

        if (needed.Length == 0 || _columns.SubtypeField is not { } field)
        {
            return codes;
        }

        await using MySqlCommand command = Command(
            $"select {Id}, {LayerDefinition.Quote(field)} from {Table} "
            + $"where {Id} in (select j.v from json_table(@ids, '$[*]' columns (v bigint path '$')) j)", connection, transaction);

        command.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(needed));

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            codes[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return codes;
    }

    /// <summary>Q-152: a subtype move that keeps a value the new subtype disallows is refused, naming the field.</summary>
    private async Task<string?> LeftOutsideItsNewSubtypeAsync(
        MySqlConnection connection, MySqlTransaction transaction, FeatureUpdate update, string? owner, CancellationToken cancellationToken)
    {
        if (_columns.SubtypeMove(update) is not { } move)
        {
            return null;
        }

        (Subtype target, long code, FieldDescription[] unsent) = move;

        await using MySqlCommand command = Command(
            $"select t.{LayerDefinition.Quote(_columns.SubtypeField!)}, "
            + string.Join(", ", unsent.Select(f => "t." + LayerDefinition.Quote(f.Name)))
            + $" from {Table} t where t.{Id} = @id{OwnerClause(owner)}", connection, transaction);

        command.Parameters.AddWithValue("@id", update.Identity);

        if (owner is not null)
        {
            command.Parameters.AddWithValue("@owner", owner);
        }

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || (!reader.IsDBNull(0) && Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) == code))
        {
            return null;
        }

        for (int i = 0; i < unsent.Length; i++)
        {
            object? stored = reader.IsDBNull(i + 1) ? null : MySqlFeatureSource.Normalise(reader.GetValue(i + 1), unsent[i].Type);

            if (EditColumns.SubtypeMoveRefusal(target, unsent[i], stored) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    private string OwnerClause(string? owner) =>
        owner is null ? string.Empty
        : _tracking.Creator is null ? " and 1 = 0"
        : $" and t.{LayerDefinition.Quote(_tracking.Creator)} = @owner";

    private async Task<bool> ExistsAsync(MySqlConnection connection, MySqlTransaction transaction, long identity, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = Command($"select count(*) from {Table} where {Id} = @id", connection, transaction);
        command.Parameters.AddWithValue("@id", identity);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Why a write that changed nothing changed nothing: not there, at another version, or not the caller's.</summary>
    private async Task<EditResult> WhyNotAsync(
        MySqlConnection connection, MySqlTransaction transaction, long identity, string? owner, string[]? expected, CancellationToken cancellationToken)
    {
        if (expected is not null)
        {
            await using MySqlCommand command = Command(
                $"select {MySqlFeatureSource.VersionSqlOf(VersionColumns, _layer.GeometryColumn, "t")} from {Table} t where t.{Id} = @id",
                connection, transaction);
            command.Parameters.AddWithValue("@id", identity);

            switch (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            {
                case null or DBNull:
                    return EditResult.Missing(identity);
                case string version when !expected.Contains(version, StringComparer.Ordinal):
                    return EditResult.Stale(identity);
            }
        }

        return owner is not null && await WhoseAsync(connection, transaction, identity, owner, cancellationToken).ConfigureAwait(false) is { } refused
            ? refused
            : EditResult.Missing(identity);
    }

    private async Task<EditResult?> WhoseAsync(
        MySqlConnection connection, MySqlTransaction transaction, long identity, string owner, CancellationToken cancellationToken)
    {
        if (_tracking.Creator is null)
        {
            return EditColumns.NotOwned(identity, recordsCreator: false, creator: null);
        }

        await using MySqlCommand command = Command($"select {LayerDefinition.Quote(_tracking.Creator)} from {Table} where {Id} = @id", connection, transaction);
        command.Parameters.AddWithValue("@id", identity);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return EditResult.Missing(identity);
        }

        string? creator = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);

        return creator is not null && string.Equals(creator, owner, StringComparison.Ordinal)
            ? null
            : EditColumns.NotOwned(identity, recordsCreator: true, creator);
    }

    private MySqlCommand Command(string sql, MySqlConnection connection, MySqlTransaction transaction) =>
        new(sql, connection, transaction) { CommandTimeout = (int)Math.Ceiling(_timeout.TotalSeconds) };

    private void Bind(MySqlCommand command, List<(string Column, object? Value)> bound, TableFacts facts)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            object value = bound[i].Value is Guid id && facts.GlobalStorage is { } storage
                && string.Equals(bound[i].Column, _columns.GlobalId, StringComparison.Ordinal)
                    ? MySqlGuids.Bound(id, storage)
                    : bound[i].Value switch
                    {
                        null => DBNull.Value,
                        Guid other => MySqlGuids.Braced(other),
                        { } given => given,
                    };

            command.Parameters.AddWithValue("@v" + i.ToString(CultureInfo.InvariantCulture), value);
        }
    }

    /// <summary>A database refusal in words the caller can act on — the column, never the table's internal name.</summary>
    /// <remarks>
    /// <b>MySQL's own text is never passed on</b>, as Oracle's is not: a duplicate names the key as
    /// <c>'table.index'</c> and the value it refused, and the rest carry the row number of a statement the caller never
    /// saw. Where the message names a column — <c>Column 'x'</c>, <c>Field 'x'</c>, <c>for column 'x'</c> — only that is
    /// said. Found by porting Oracle's rule tests: the first version passed MySQL's message through whole.
    /// </remarks>
    internal static string Explain(MySqlException e) => e.ErrorCode switch
    {
        MySqlErrorCode.ColumnCannotBeNull or MySqlErrorCode.NoDefaultForField => ColumnIn(e) is { } column
            ? $"'{column}' is required, and this edit leaves it without a value."
            : "A required field is left without a value by this edit.",
        MySqlErrorCode.DuplicateKeyEntry => "This would duplicate a value that must be unique in this layer.",
        MySqlErrorCode.NoReferencedRow2 or MySqlErrorCode.NoReferencedRow => "This refers to a row that does not exist.",
        MySqlErrorCode.RowIsReferenced2 or MySqlErrorCode.RowIsReferenced => "Other rows refer to this one, so it cannot be removed.",
        MySqlErrorCode.DataTooLong => ColumnIn(e) is { } column
            ? $"The value for '{column}' is longer than the column allows."
            : "A value is longer than its column allows.",
        MySqlErrorCode.TruncatedWrongValueForField or MySqlErrorCode.WarningDataOutOfRange => ColumnIn(e) is { } column
            ? $"The value for '{column}' is the wrong type or out of range for the column."
            : "A value is the wrong type or out of range for its column.",
        MySqlErrorCode.TableAccessDenied or MySqlErrorCode.ColumnAccessDenied =>
            "The server's credential for this database may not write to this table.",
        _ when e.Number == 3819 => "A rule on this layer's table refused this value.",
        _ => $"The database refused this edit (MySQL error {e.Number.ToString(CultureInfo.InvariantCulture)}).",
    };

    /// <summary>The column a MySQL message names, or null.</summary>
    private static string? ColumnIn(MySqlException e) =>
        System.Text.RegularExpressions.Regex.Match(e.Message, @"(?:[Cc]olumn|Field) '([^']+)'") is { Success: true } found
            ? found.Groups[1].Value
            : null;
}
