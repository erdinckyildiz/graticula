using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Microsoft.Data.SqlClient;

namespace Graticula.Providers.SqlServer;

/// <summary>Applies edits to a registered SQL Server table — ADR-210, as amended by Q-188.</summary>
/// <remarks>
/// <para>
/// <b><c>MySqlFeatureWriter</c>'s shape in T-SQL: <see cref="EditColumns"/> decides what an edit may send</b>, as for
/// every registered engine; what is SQL Server's is the SQL. One transaction, each edit in its own savepoint.
/// </para>
/// <para>
/// <b>Where SQL Server differs.</b> A new row's key comes back through <c>OUTPUT inserted</c>, into a table variable so a
/// trigger on the table does not refuse it — the key is an <c>IDENTITY</c> column or has a default (a sequence), or the
/// add is refused with the reason, since this server does not invent numbers. Shapes are two-dimensional, so a shape
/// with Z or M is refused rather than flattened (ADR-077). A row's version is a hash of what it holds
/// (<see cref="VersionSqlOf"/>). <b>A <c>geography</c> polygon is turned the way SQL Server reads it</b>: ArcGIS sends an
/// outer ring clockwise and SQL Server takes the left-hand side of a ring as its inside, so a ring read as covering more
/// than a hemisphere is reoriented — the polygon the client drew, not the rest of the Earth.
/// </para>
/// </remarks>
public sealed class SqlServerFeatureWriter : IFeatureWriter
{
    private readonly string _connectionString;
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<FieldDescription> _fields;
    private readonly EditColumns _columns;
    private readonly EditorTracking _tracking;
    private readonly IProjector _projector;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the writer.</summary>
    /// <param name="locator">The data source's <c>sqlserver:</c> locator.</param>
    /// <param name="layer">The layer; its schema is the SQL Server schema.</param>
    /// <param name="fields">The layer's real columns, from its describe — the whitelist.</param>
    /// <param name="projector">Moves a geometry sent in another reference into the layer's.</param>
    /// <param name="tracking">Which columns record edits — ADR-064 — or null.</param>
    /// <param name="subtypes">The layer's subtypes — ADR-065 — or null.</param>
    /// <param name="statementTimeout">How long one statement may run; thirty seconds when null.</param>
    public SqlServerFeatureWriter(
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

        _connectionString = SqlServerConnections.For(locator, pooling: true);
        _layer = layer;
        _fields = fields;
        _columns = new EditColumns(layer, fields, subtypes);
        _tracking = tracking ?? EditorTracking.None;
        _projector = projector;
        _timeout = statementTimeout ?? TimeSpan.FromSeconds(30);
    }

    private string Id => LayerDefinition.Quote(_layer.IntegerIdentityColumn!);

    private string Shape => LayerDefinition.Quote(_layer.GeometryColumn);

    private string Table => _layer.QuotedTable;

    /// <summary>The database's clock, in UTC.</summary>
    private const string Now = "sysutcdatetime()";

    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await using SqlConnection connection = await SqlServerConnections.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = (SqlTransaction)
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

    /// <summary>Several layers' edits in one SQL Server database, in one transaction — as several MySQL layers' are.</summary>
    /// <param name="locator">The database's <c>sqlserver:</c> locator, which every layer shares.</param>
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

        await using SqlConnection connection =
            await SqlServerConnections.OpenAsync(SqlServerConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        List<EditOutcome> outcomes = [];
        bool rollBack = false;

        foreach (LayerBatch part in layers)
        {
            SqlServerFeatureWriter writer = new(locator, part.Layer, part.Fields, projector, part.Tracking, part.Subtypes, statementTimeout);
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
    /// <param name="locator">The data source's <c>sqlserver:</c> locator.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="globalIdColumn">Its GlobalID column, a <c>uniqueidentifier</c>.</param>
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

        await using SqlConnection connection =
            await SqlServerConnections.OpenAsync(SqlServerConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

        string column = LayerDefinition.Quote(globalIdColumn);

        await using SqlCommand command = new(
            $"select t.{column}, t.{LayerDefinition.Quote(layer.IntegerIdentityColumn)} from {layer.QuotedTable} t "
            + $"where t.{column} in (select try_cast(j.[value] as uniqueidentifier) from openjson(@ids) j)",
            connection);

        command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(globalIds) });

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0) && reader.GetValue(0) is Guid global)
            {
                found[global] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        return found;
    }

    /// <summary>The SQL of a row's version: a SHA-1 of its columns and its shape as JSON, nulls named.</summary>
    /// <param name="columns">The table's columns other than the shape.</param>
    /// <param name="shape">The geometry column.</param>
    /// <param name="alias">The row's alias.</param>
    /// <returns>The expression, forty hexadecimal characters.</returns>
    internal static string VersionSqlOf(IEnumerable<string> columns, string shape, string alias) =>
        "convert(varchar(40), hashbytes('SHA1', (select "
        + string.Join(", ", columns.Select(c => $"{alias}.{LayerDefinition.Quote(c)}"))
        + $"{(columns.Any() ? ", " : string.Empty)}{alias}.{LayerDefinition.Quote(shape)}.STAsBinary() as {LayerDefinition.Quote("graticula_shape")} "
        + "for json path, without_array_wrapper, include_null_values)), 2)";

    /// <summary>A batch in a transaction the caller owns.</summary>
    private async Task<EditOutcome> ApplyWithinAsync(
        SqlConnection connection, SqlTransaction transaction, EditBatch batch, CancellationToken cancellationToken)
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
        SqlTransaction transaction, int ordinal, Func<Task<EditResult>> edit, CancellationToken cancellationToken)
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
    /// <param name="Geography">Whether the shape column is <c>geography</c>.</param>
    /// <param name="KeyMade">Whether the key is an IDENTITY column or has a default.</param>
    /// <param name="GlobalKept">Whether the GlobalID column is a <c>uniqueidentifier</c> this writer can mint.</param>
    private sealed record TableFacts(bool Geography, bool KeyMade, bool GlobalKept);

    private async Task<TableFacts> FactsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            """
            select (select DATA_TYPE from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = @s and TABLE_NAME = @t and COLUMN_NAME = @g),
                   columnproperty(object_id(quotename(@s) + '.' + quotename(@t)), @k, 'IsIdentity'),
                   (select COLUMN_DEFAULT from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = @s and TABLE_NAME = @t and COLUMN_NAME = @k),
                   (select DATA_TYPE from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = @s and TABLE_NAME = @t and COLUMN_NAME = @u)
            """, connection, transaction);

        command.Parameters.AddWithValue("@s", _layer.SchemaName);
        command.Parameters.AddWithValue("@t", _layer.TableName);
        command.Parameters.AddWithValue("@g", _layer.GeometryColumn);
        command.Parameters.AddWithValue("@k", _layer.IntegerIdentityColumn!);
        command.Parameters.AddWithValue("@u", (object?)_columns.GlobalId ?? DBNull.Value);

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (reader.IsDBNull(0))
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' is served from {_layer.SchemaName}.{_layer.TableName}.{_layer.GeometryColumn}, which is "
                + "no longer a spatial column.");
        }

        bool geography = string.Equals(reader.GetString(0), "geography", StringComparison.OrdinalIgnoreCase);
        bool identity = !reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 1;
        bool defaulted = !reader.IsDBNull(2);
        bool global = !reader.IsDBNull(3) && string.Equals(reader.GetString(3), "uniqueidentifier", StringComparison.OrdinalIgnoreCase);

        return new TableFacts(geography, identity || defaulted, global);
    }

    /// <summary>
    /// The statement that puts the sent shape in <c>@shape</c>, in the column's type and the layer's SRID — reoriented when
    /// a <c>geography</c> ring came in clockwise.
    /// </summary>
    private string ShapeDeclaration(TableFacts facts)
    {
        string srid = _layer.Srid.ToString(CultureInfo.InvariantCulture);

        return facts.Geography
            ? $"declare @shape geography = geography::STGeomFromWKB(@geom, {srid}); "
              + "if @shape.EnvelopeAngle() > 90 set @shape = @shape.ReorientObject(); "
            : $"declare @shape geometry = geometry::STGeomFromWKB(@geom, {srid}); ";
    }

    private async Task<EditResult> AddAsync(
        SqlConnection connection, SqlTransaction transaction, TableFacts facts, FeatureAdd add, EditBatch batch, CancellationToken cancellationToken)
    {
        if (!facts.KeyMade)
        {
            return EditResult.Failed(
                -1,
                $"'{_layer.IntegerIdentityColumn}' is this table's key and the database does not make it — it is not an "
                + "IDENTITY column and has no default — so this server cannot add a row without inventing a number the "
                + "table's own users did not choose. Updates and deletes are answered (ADR-210).");
        }

        if (add.Geometry is { IsEmpty: false } shape
            && EditColumns.DimensionRefusal(shape, GeometryOrdinates.None, "a SQL Server layer's geometry column") is { } refused)
        {
            return EditResult.Failed(-1, refused);
        }

        if (!_columns.TryBind(add.Attributes, null, out List<(string Column, object? Value)> bound, out string? error, keepGlobalId: batch.KeepsGlobalIds))
        {
            return EditResult.Failed(-1, error!);
        }

        // Every new row gets a GlobalID, the client's under useGlobalIds or one minted here — Oracle's and MySQL's rule.
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

        List<string> columns = [.. bound.Select(b => LayerDefinition.Quote(b.Column))];
        List<string> values = [.. bound.Select((_, i) => "@v" + i.ToString(CultureInfo.InvariantCulture))];
        bool shaped = add.Geometry is { IsEmpty: false };

        if (shaped)
        {
            columns.Add(Shape);
            values.Add("@shape");
        }

        if (columns.Count == 0)
        {
            return EditResult.Failed(-1, "The feature has neither attributes nor geometry, so there is nothing to add.");
        }

        bool named = Sign(columns, values, batch, creating: true);

        await using SqlCommand command = Command(
            (shaped ? ShapeDeclaration(facts) : string.Empty)
            + "declare @made table (i bigint); "
            + $"insert into {Table} ({string.Join(", ", columns)}) output inserted.{Id} into @made values ({string.Join(", ", values)}); "
            + "select i from @made;",
            connection, transaction);
        Bind(command, bound);

        if (shaped)
        {
            command.Parameters.Add(new SqlParameter("@geom", SqlDbType.VarBinary, -1)
            {
                Value = await WkbAsync(add.Geometry!, add.GeometrySrid, cancellationToken).ConfigureAwait(false),
            });
        }

        if (named)
        {
            command.Parameters.AddWithValue("@editor", batch.Editor!);
        }

        try
        {
            object? made = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return EditResult.Ok(Convert.ToInt64(made, CultureInfo.InvariantCulture)) with { GlobalId = global };
        }
        catch (SqlException e)
        {
            return EditResult.Failed(-1, Explain(e));
        }
    }

    private async Task<EditResult> UpdateAsync(
        SqlConnection connection, SqlTransaction transaction, TableFacts facts, FeatureUpdate update, long? storedSubtype,
        EditBatch batch, CancellationToken cancellationToken)
    {
        string[]? expected = Expected(batch, update.Identity);

        if (update.Geometry is not null
            && EditColumns.DimensionRefusal(update.Geometry, GeometryOrdinates.None, "a SQL Server layer's geometry column") is { } lossy)
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
        bool shaped = update.Geometry is { IsEmpty: false };

        if (update.Geometry is not null)
        {
            assignments.Add(shaped ? $"t.{Shape} = @shape" : $"t.{Shape} = null");
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
        await using SqlCommand command = Command(
            (shaped ? ShapeDeclaration(facts) : string.Empty)
            + $"update t set {string.Join(", ", assignments)} from {Table} t where t.{Id} = @id{VersionClause(expected)}{OwnerClause(owner)}",
            connection, transaction);
        Bind(command, bound);

        if (shaped)
        {
            command.Parameters.Add(new SqlParameter("@geom", SqlDbType.VarBinary, -1)
            {
                Value = await WkbAsync(update.Geometry!, update.GeometrySrid, cancellationToken).ConfigureAwait(false),
            });
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
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0
                ? EditResult.Ok(update.Identity) with { GlobalId = await GlobalOfAsync(connection, transaction, facts, update.Identity, cancellationToken).ConfigureAwait(false) }
                : await WhyNotAsync(connection, transaction, update.Identity, owner, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException e)
        {
            return EditResult.Failed(update.Identity, Explain(e));
        }
    }

    private async Task<EditResult> DeleteAsync(
        SqlConnection connection, SqlTransaction transaction, TableFacts facts, long identity, EditBatch batch, CancellationToken cancellationToken)
    {
        string[]? expected = Expected(batch, identity);
        string? owner = EditColumns.OwnerOf(batch);
        Guid? global = await GlobalOfAsync(connection, transaction, facts, identity, cancellationToken).ConfigureAwait(false);

        await using SqlCommand command = Command(
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
        catch (SqlException e)
        {
            return EditResult.Failed(identity, Explain(e));
        }
    }

    private async Task<Guid?> GlobalOfAsync(
        SqlConnection connection, SqlTransaction transaction, TableFacts facts, long identity, CancellationToken cancellationToken)
    {
        if (!facts.GlobalKept || _columns.GlobalId is not { } column)
        {
            return null;
        }

        await using SqlCommand command = Command($"select {LayerDefinition.Quote(column)} from {Table} where {Id} = @id", connection, transaction);
        command.Parameters.AddWithValue("@id", identity);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    private static string[]? Expected(EditBatch batch, long identity) =>
        batch.Expects is { } expects && expects.TryGetValue(identity, out IReadOnlyList<string>? versions) && versions.Count > 0
            ? [.. versions]
            : null;

    /// <summary>The precondition in the <c>where</c>: the row's version is one of those expected — D-186.</summary>
    private string VersionClause(string[]? expected) =>
        expected is null
            ? string.Empty
            : $" and {VersionSqlOf(VersionColumns, _layer.GeometryColumn, "t")} in (select j.[value] from openjson(@expected) j)";

    /// <summary>The columns a version is computed over — the describe's, as the reader's is.</summary>
    private IEnumerable<string> VersionColumns =>
        _fields.Select(f => f.Name).Where(n => !string.Equals(n, _layer.GeometryColumn, StringComparison.Ordinal));

    private static void BindExpected(SqlCommand command, string[]? expected)
    {
        if (expected is not null)
        {
            command.Parameters.Add(new SqlParameter("@expected", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(expected) });
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
        SqlConnection connection, SqlTransaction transaction, long[] needed, CancellationToken cancellationToken)
    {
        Dictionary<long, long?> codes = [];

        if (needed.Length == 0 || _columns.SubtypeField is not { } field)
        {
            return codes;
        }

        await using SqlCommand command = Command(
            $"select {Id}, {LayerDefinition.Quote(field)} from {Table} where {Id} in (select cast(j.[value] as bigint) from openjson(@ids) j)",
            connection, transaction);

        command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(needed) });

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            codes[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return codes;
    }

    /// <summary>Q-152: a subtype move that keeps a value the new subtype disallows is refused, naming the field.</summary>
    private async Task<string?> LeftOutsideItsNewSubtypeAsync(
        SqlConnection connection, SqlTransaction transaction, FeatureUpdate update, string? owner, CancellationToken cancellationToken)
    {
        if (_columns.SubtypeMove(update) is not { } move)
        {
            return null;
        }

        (Subtype target, long code, FieldDescription[] unsent) = move;

        await using SqlCommand command = Command(
            $"select t.{LayerDefinition.Quote(_columns.SubtypeField!)}"
            + string.Concat(unsent.Select(f => ", t." + LayerDefinition.Quote(f.Name)))
            + $" from {Table} t where t.{Id} = @id{OwnerClause(owner)}", connection, transaction);

        command.Parameters.AddWithValue("@id", update.Identity);

        if (owner is not null)
        {
            command.Parameters.AddWithValue("@owner", owner);
        }

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || (!reader.IsDBNull(0) && Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) == code))
        {
            return null;
        }

        for (int i = 0; i < unsent.Length; i++)
        {
            object? stored = reader.IsDBNull(i + 1) ? null : SqlServerFeatureSource.Normalise(reader.GetValue(i + 1), unsent[i].Type);

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

    private async Task<bool> ExistsAsync(SqlConnection connection, SqlTransaction transaction, long identity, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command($"select count(*) from {Table} where {Id} = @id", connection, transaction);
        command.Parameters.AddWithValue("@id", identity);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Why a write that changed nothing changed nothing: not there, at another version, or not the caller's.</summary>
    private async Task<EditResult> WhyNotAsync(
        SqlConnection connection, SqlTransaction transaction, long identity, string? owner, string[]? expected, CancellationToken cancellationToken)
    {
        if (expected is not null)
        {
            await using SqlCommand command = Command(
                $"select {VersionSqlOf(VersionColumns, _layer.GeometryColumn, "t")} from {Table} t where t.{Id} = @id",
                connection, transaction);
            command.Parameters.AddWithValue("@id", identity);

            switch (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            {
                case null or DBNull:
                    return EditResult.Missing(identity);
                case string version when !expected.Contains(version, StringComparer.OrdinalIgnoreCase):
                    return EditResult.Stale(identity);
            }
        }

        return owner is not null && await WhoseAsync(connection, transaction, identity, owner, cancellationToken).ConfigureAwait(false) is { } refused
            ? refused
            : EditResult.Missing(identity);
    }

    private async Task<EditResult?> WhoseAsync(
        SqlConnection connection, SqlTransaction transaction, long identity, string owner, CancellationToken cancellationToken)
    {
        if (_tracking.Creator is null)
        {
            return EditColumns.NotOwned(identity, recordsCreator: false, creator: null);
        }

        await using SqlCommand command = Command($"select {LayerDefinition.Quote(_tracking.Creator)} from {Table} where {Id} = @id", connection, transaction);
        command.Parameters.AddWithValue("@id", identity);

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return EditResult.Missing(identity);
        }

        string? creator = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);

        return creator is not null && string.Equals(creator, owner, StringComparison.Ordinal)
            ? null
            : EditColumns.NotOwned(identity, recordsCreator: true, creator);
    }

    private SqlCommand Command(string sql, SqlConnection connection, SqlTransaction transaction) =>
        new(sql, connection, transaction) { CommandTimeout = (int)Math.Ceiling(_timeout.TotalSeconds) };

    private static void Bind(SqlCommand command, List<(string Column, object? Value)> bound)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            command.Parameters.AddWithValue("@v" + i.ToString(CultureInfo.InvariantCulture), bound[i].Value ?? DBNull.Value);
        }
    }

    /// <summary>A database refusal in words the caller can act on — the column, never the table's internal names.</summary>
    /// <remarks>
    /// <b>SQL Server's own text is never passed on</b>, as Oracle's and MySQL's are not: it names constraints, the
    /// statement's batch and, for a duplicate, the value refused. Where the message names a column, only that is said.
    /// </remarks>
    internal static string Explain(SqlException e) => e.Number switch
    {
        515 => ColumnIn(e) is { } column
            ? $"'{column}' is required, and this edit leaves it without a value."
            : "A required field is left without a value by this edit.",
        2627 or 2601 => "This would duplicate a value that must be unique in this layer.",
        547 when e.Message.Contains("REFERENCE", StringComparison.Ordinal) && e.Message.Contains("DELETE", StringComparison.Ordinal) =>
            "Other rows refer to this one, so it cannot be removed.",
        547 when e.Message.Contains("FOREIGN KEY", StringComparison.Ordinal) => "This refers to a row that does not exist.",
        547 => "A rule on this layer's table refused this value.",
        2628 or 8152 => ColumnIn(e) is { } column
            ? $"The value for '{column}' is longer than the column allows."
            : "A value is longer than its column allows.",
        8114 or 245 or 220 or 8115 or 242 or 241 => "A value is the wrong type or out of range for its column.",
        229 or 230 => "The server's credential for this database may not write to this table.",
        24200 or 24205 or 24144 or 24305 or 6522 => "SQL Server refused the shape as invalid for this column.",
        _ => $"The database refused this edit (SQL Server error {e.Number.ToString(CultureInfo.InvariantCulture)}).",
    };

    /// <summary>The column a SQL Server message names, or null.</summary>
    private static string? ColumnIn(SqlException e) =>
        Regex.Match(e.Message, @"column '([^']+)'", RegexOptions.IgnoreCase) is { Success: true } found ? found.Groups[1].Value : null;
}
