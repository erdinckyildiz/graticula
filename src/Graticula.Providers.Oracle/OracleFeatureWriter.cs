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
using Graticula.Platform.Admin;
using Oracle.ManagedDataAccess.Client;

namespace Graticula.Providers.Oracle;

/// <summary>
/// Applies edits to a registered Oracle table — ADR-205 phase 2.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>PostGisFeatureWriter</c>'s rules, run by the same code.</b> What an edit may send — the column whitelist,
/// domains and subtypes, the tracked columns and the GlobalID — is decided by <see cref="EditColumns"/>, which the
/// PostGIS writer calls too; what is Oracle's is only the SQL. One transaction always, each edit in its own savepoint,
/// so <c>rollbackOnFailure=false</c> keeps the good edits and every result is attempted on a clean state.
/// </para>
/// <para>
/// <b>Where it differs from the PostGIS writer, and why</b> (each INFERRED, ADR-205 §5b):
/// a new row's key must be made by the database — an identity column or a column default — because a customer's table
/// has its own way of numbering and this server does not invent one; a polygon is stored as sent and not repaired,
/// because Oracle's idea of valid is not PostGIS's (condition 8) and repairing by one to satisfy the other changes the
/// customer's data; and a version precondition is refused, because an Oracle layer publishes no row versions.
/// </para>
/// </remarks>
public sealed class OracleFeatureWriter : IFeatureWriter
{
    private readonly string _connectionString;
    private readonly LayerDefinition _layer;
    private readonly EditColumns _columns;
    private readonly EditorTracking _tracking;
    private readonly IProjector _projector;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the writer.</summary>
    /// <param name="locator">The data source's <c>oracle:</c> locator.</param>
    /// <param name="layer">The layer; its schema is the table's owner.</param>
    /// <param name="fields">The layer's real columns, from its describe — the whitelist.</param>
    /// <param name="projector">Moves a geometry sent in another reference into the layer's.</param>
    /// <param name="tracking">Which columns record edits — ADR-064 — or null.</param>
    /// <param name="subtypes">The layer's subtypes — ADR-065 — or null.</param>
    /// <param name="statementTimeout">How long one statement may run; thirty seconds when null.</param>
    public OracleFeatureWriter(
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
                $"Layer '{layer.Name}' has no integer object-id column, so its features cannot be "
                + "addressed for update or delete. It is readable and not editable.",
                nameof(layer));
        }

        _connectionString = OracleConnections.For(locator, pooling: true);
        _layer = layer;
        _columns = new EditColumns(layer, fields, subtypes);
        _tracking = tracking ?? EditorTracking.None;
        _projector = projector;
        _timeout = statementTimeout ?? TimeSpan.FromSeconds(30);
    }

    private string Id => LayerDefinition.Quote(_layer.IntegerIdentityColumn!);

    private string Shape => LayerDefinition.Quote(_layer.GeometryColumn);

    private string Table => _layer.QuotedTable;

    /// <summary>The database's clock, in UTC, which is how ArcGIS reads a date it is given without a zone.</summary>
    private const string Now = "sys_extract_utc(systimestamp)";

    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await using OracleConnection connection =
            await OracleConnections.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);

        TableFacts facts = await FactsAsync(connection, cancellationToken).ConfigureAwait(false);

        using OracleTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

        List<EditResult> adds = [];
        List<EditResult> updates = [];
        List<EditResult> deletes = [];

        IReadOnlyDictionary<long, GeometryOrdinates?> stored =
            await StoredOrdinatesAsync(connection, batch.Updates, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<long, long?> subtypes =
            await StoredSubtypesAsync(connection, _columns.NeedingStoredSubtype(batch.Updates), cancellationToken)
                .ConfigureAwait(false);

        int savepoint = 0;

        foreach (FeatureAdd add in batch.Adds)
        {
            adds.Add(await IsolatedAsync(
                transaction, savepoint++, () => AddAsync(connection, facts, add, batch, cancellationToken)).ConfigureAwait(false));
        }

        foreach (FeatureUpdate update in batch.Updates)
        {
            updates.Add(await IsolatedAsync(
                transaction, savepoint++,
                () => UpdateAsync(
                    connection, facts, update, stored,
                    subtypes.TryGetValue(update.Identity, out long? kind) ? kind : null, batch, cancellationToken))
                .ConfigureAwait(false));
        }

        foreach (long identity in batch.Deletes)
        {
            deletes.Add(await IsolatedAsync(
                transaction, savepoint++, () => DeleteAsync(connection, identity, batch, cancellationToken)).ConfigureAwait(false));
        }

        EditOutcome outcome = new(adds, updates, deletes, RolledBack: false);

        if (batch.RollbackOnFailure && (!outcome.AllSucceeded || batch.AnythingAlreadyFailed))
        {
            transaction.Rollback();
            return outcome with { RolledBack = true };
        }

        transaction.Commit();
        return outcome;
    }

    /// <summary>
    /// One edit in its own savepoint, so its failure does not take the others — <c>PostGisFeatureWriter</c>'s reason.
    /// </summary>
    private static async Task<EditResult> IsolatedAsync(OracleTransaction transaction, int ordinal, Func<Task<EditResult>> edit)
    {
        string name = "edit" + ordinal.ToString(CultureInfo.InvariantCulture);

        transaction.Save(name);

        EditResult result = await edit().ConfigureAwait(false);

        if (!result.Succeeded)
        {
            transaction.Rollback(name);
        }

        return result;
    }

    /// <summary>What the table says about itself, read once per batch.</summary>
    /// <param name="Srid">The geometry's Oracle SRID, which a written shape must carry (ORA-13295).</param>
    /// <param name="Declared">The ordinates the column declares, from its dimension count.</param>
    /// <param name="KeyMade">Whether the database makes a new row's key.</param>
    private sealed record TableFacts(int Srid, GeometryOrdinates Declared, bool KeyMade);

    private async Task<TableFacts> FactsAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        await using OracleCommand command = Command(
            """
            select m.srid,
                   (select count(*) from table(m.diminfo)),
                   (select count(*) from table(m.diminfo) d where upper(d.sdo_dimname) = 'M'),
                   (select case when c.identity_column = 'YES' or c.data_default_vc is not null then 1 else 0 end
                      from all_tab_columns c
                     where c.owner = m.owner and c.table_name = m.table_name and c.column_name = :k)
              from all_sdo_geom_metadata m
             where m.owner = :o and m.table_name = :t and m.column_name = :c
            """, connection);

        command.Parameters.Add("k", _layer.IntegerIdentityColumn);
        command.Parameters.Add("o", _layer.SchemaName);
        command.Parameters.Add("t", _layer.TableName);
        command.Parameters.Add("c", _layer.GeometryColumn);

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' is served from {_layer.SchemaName}.{_layer.TableName}.{_layer.GeometryColumn}, "
                + "which is no longer in the database's spatial metadata (ALL_SDO_GEOM_METADATA).");
        }

        int dimensions = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);

        // A third dimension is a measure where the metadata calls it M, as Oracle's linear referencing does.
        bool measured = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) > 0;

        return new TableFacts(
            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            dimensions switch
            {
                >= 4 => GeometryOrdinates.Z | GeometryOrdinates.M,
                3 => measured ? GeometryOrdinates.M : GeometryOrdinates.Z,
                _ => GeometryOrdinates.None,
            },
            !reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) == 1);
    }

    private async Task<EditResult> AddAsync(
        OracleConnection connection, TableFacts facts, FeatureAdd add, EditBatch batch, CancellationToken cancellationToken)
    {
        if (!facts.KeyMade)
        {
            return EditResult.Failed(
                -1,
                $"'{_layer.IntegerIdentityColumn}' is this table's key and the database does not make it — it is neither "
                + "an identity column nor given a default — so this server cannot add a row without inventing a number "
                + "the table's own users did not choose. Updates and deletes are answered (ADR-205).");
        }

        if (add.Geometry is { IsEmpty: false } shape
            && EditColumns.DimensionRefusal(shape, facts.Declared, "the layer's geometry column") is { } refused)
        {
            return EditResult.Failed(-1, refused);
        }

        if (!_columns.TryBind(add.Attributes, null, out List<(string Column, object? Value)> bound, out string? error,
                keepGlobalId: batch.KeepsGlobalIds))
        {
            return EditResult.Failed(-1, error!);
        }

        List<string> columns = [.. bound.Select(b => LayerDefinition.Quote(b.Column))];
        List<string> values = [.. bound.Select((_, i) => ":v" + i.ToString(CultureInfo.InvariantCulture))];
        bool shaped = add.Geometry is { IsEmpty: false };

        if (shaped)
        {
            columns.Add(Shape);
            values.Add("g");
        }

        if (columns.Count == 0)
        {
            return EditResult.Failed(-1, "The feature has neither attributes nor geometry, so there is nothing to add.");
        }

        bool named = Sign(columns, values, batch, creating: true);

        string block =
            "declare g sdo_geometry; begin "
            + (shaped ? Assignment(add.Geometry!) : string.Empty)
            + $"insert into {Table} ({string.Join(", ", columns)}) values ({string.Join(", ", values)}) "
            + $"returning {Id} into :rid; end;";

        await using OracleCommand command = Command(block, connection);
        Bind(command, bound);

        if (shaped)
        {
            await BindGeometryAsync(command, connection, facts, add.Geometry!, add.GeometrySrid, cancellationToken).ConfigureAwait(false);
        }

        if (named)
        {
            command.Parameters.Add("editor", batch.Editor!);
        }

        OracleParameter made = command.Parameters.Add("rid", OracleDbType.Int64, ParameterDirection.Output);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return EditResult.Ok(Convert.ToInt64(made.Value.ToString(), CultureInfo.InvariantCulture));
        }
        catch (OracleException e)
        {
            return EditResult.Failed(-1, Explain(e));
        }
    }

    private async Task<EditResult> UpdateAsync(
        OracleConnection connection,
        TableFacts facts,
        FeatureUpdate update,
        IReadOnlyDictionary<long, GeometryOrdinates?> stored,
        long? storedSubtype,
        EditBatch batch,
        CancellationToken cancellationToken)
    {
        if (!stored.TryGetValue(update.Identity, out GeometryOrdinates? ordinates))
        {
            return EditResult.Missing(update.Identity);
        }

        if (Precondition(batch, update.Identity) is { } refusedVersion)
        {
            return refusedVersion;
        }

        if (update.Geometry is not null
            && (ordinates ?? facts.Declared) is var holds
            && EditColumns.DimensionRefusal(
                update.Geometry, holds,
                ordinates is null ? "the layer's geometry column" : "this feature's stored geometry") is { } lossy)
        {
            return EditResult.Failed(update.Identity, lossy);
        }

        if (!_columns.TryBind(update.Attributes, storedSubtype, out List<(string Column, object? Value)> bound, out string? error))
        {
            return EditResult.Failed(update.Identity, error!);
        }

        string? owner = EditColumns.OwnerOf(batch);

        if (await LeftOutsideItsNewSubtypeAsync(connection, update, owner, cancellationToken).ConfigureAwait(false) is { } outside)
        {
            return EditResult.Failed(update.Identity, outside);
        }

        List<string> assignments =
            [.. bound.Select((b, i) => $"{LayerDefinition.Quote(b.Column)} = :v{i.ToString(CultureInfo.InvariantCulture)}")];
        bool shaped = update.Geometry is not null;

        if (shaped)
        {
            assignments.Add($"{Shape} = g");
        }

        if (assignments.Count == 0)
        {
            // Nothing to change is a success — but ownership is still asked, as the PostGIS writer asks it.
            return owner is not null
                && await WhoseAsync(connection, update.Identity, owner, cancellationToken).ConfigureAwait(false) is { } notYours
                ? notYours
                : EditResult.Ok(update.Identity);
        }

        bool named = false;

        if (_tracking.Editor is not null && batch.Editor is not null)
        {
            assignments.Add($"{LayerDefinition.Quote(_tracking.Editor)} = :editor");
            named = true;
        }

        if (_tracking.Edited is not null)
        {
            assignments.Add($"{LayerDefinition.Quote(_tracking.Edited)} = {Now}");
        }

        // Ownership rides in the `where`, so whose the row is and the write are one statement — ADR-064.
        string block =
            "declare g sdo_geometry; begin "
            + (shaped && update.Geometry is { IsEmpty: false } sentShape ? Assignment(sentShape) : string.Empty)
            + $"update {Table} set {string.Join(", ", assignments)} where {Id} = :id{OwnerClause(owner)}; "
            + ":n := sql%rowcount; end;";

        await using OracleCommand command = Command(block, connection);
        Bind(command, bound);

        if (shaped && update.Geometry is { IsEmpty: false } geometry)
        {
            await BindGeometryAsync(command, connection, facts, geometry, update.GeometrySrid, cancellationToken).ConfigureAwait(false);
        }

        if (named)
        {
            command.Parameters.Add("editor", batch.Editor!);
        }

        command.Parameters.Add("id", update.Identity);

        if (owner is not null)
        {
            command.Parameters.Add("owner", owner);
        }

        OracleParameter changed = command.Parameters.Add("n", OracleDbType.Int32, ParameterDirection.Output);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return Convert.ToInt32(changed.Value.ToString(), CultureInfo.InvariantCulture) > 0
                ? EditResult.Ok(update.Identity)
                : await WhyNotAsync(connection, update.Identity, owner, cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException e)
        {
            return EditResult.Failed(update.Identity, Explain(e));
        }
    }

    private async Task<EditResult> DeleteAsync(
        OracleConnection connection, long identity, EditBatch batch, CancellationToken cancellationToken)
    {
        if (Precondition(batch, identity) is { } refusedVersion)
        {
            return refusedVersion;
        }

        string? owner = EditColumns.OwnerOf(batch);

        await using OracleCommand command = Command(
            $"begin delete from {Table} where {Id} = :id{OwnerClause(owner)}; :n := sql%rowcount; end;", connection);

        command.Parameters.Add("id", identity);

        if (owner is not null)
        {
            command.Parameters.Add("owner", owner);
        }

        OracleParameter removed = command.Parameters.Add("n", OracleDbType.Int32, ParameterDirection.Output);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Deleting what is already gone is a failure, as ArcGIS reports it: the client believes it deleted a
            // feature it never saw.
            return Convert.ToInt32(removed.Value.ToString(), CultureInfo.InvariantCulture) > 0
                ? EditResult.Ok(identity)
                : await WhyNotAsync(connection, identity, owner, cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException e)
        {
            return EditResult.Failed(identity, Explain(e));
        }
    }

    /// <summary>
    /// A version precondition, refused: an Oracle layer publishes no row versions, so a version a caller holds is not
    /// one this server gave out, and writing over it would be the lost update D-186 exists to stop.
    /// </summary>
    private static EditResult? Precondition(EditBatch batch, long identity) =>
        batch.Expects is { } expects && expects.TryGetValue(identity, out IReadOnlyList<string>? versions) && versions.Count > 0
            ? EditResult.Failed(
                identity,
                "This layer is in a registered Oracle database, which publishes no row versions, so an edit "
                + "conditional on one cannot be checked. Send it without If-Match (ADR-205).")
            : null;

    /// <summary>Adds the tracked columns this server writes on a new row, and says whether the editor is bound.</summary>
    private bool Sign(List<string> columns, List<string> values, EditBatch batch, bool creating)
    {
        bool named = false;

        foreach ((string? column, string value, bool isName) in (ReadOnlySpan<(string?, string, bool)>)
        [
            (creating ? _tracking.Creator : null, ":editor", true),
            (_tracking.Editor, ":editor", true),
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

    /// <summary>
    /// Binds a geometry as WKB, in the layer's reference — moved there by the datastore's projector when it was sent
    /// in another, as the PostGIS writer's <c>st_transform</c> does (Q-153) — with the column's Oracle SRID.
    /// </summary>
    private async Task BindGeometryAsync(
        OracleCommand command, OracleConnection connection, TableFacts facts, Geometry geometry, int? sentIn,
        CancellationToken cancellationToken)
    {
        Geometry placed = geometry;

        if (sentIn is { } from && from != _layer.Srid)
        {
            (IReadOnlyList<Geometry> moved, _) = await _projector
                .ProjectAsync([geometry], from, _layer.Srid, cancellationToken)
                .ConfigureAwait(false);

            placed = moved[0];
        }

        byte[] wkb = WkbWriter.ToArray(placed);

        if (Ordinates.Of(geometry) != GeometryOrdinates.None)
        {
            SdoShape.Parts parts = SdoShape.FromWkb(wkb);

            command.Parameters.Add("gt", parts.GType);
            command.Parameters.Add("srid", facts.Srid);
            command.Parameters.Add("ei", OracleDbType.Clob, SdoShape.JsonArray(parts.ElemInfo!), ParameterDirection.Input);
            command.Parameters.Add("ord", OracleDbType.Clob, SdoShape.JsonArray(parts.Ordinates!), ParameterDirection.Input);
            return;
        }

        global::Oracle.ManagedDataAccess.Types.OracleBlob blob = new(connection);
        blob.Write(wkb, 0, wkb.Length);

        command.Parameters.Add("geom", OracleDbType.Blob, blob, ParameterDirection.Input);
        command.Parameters.Add("srid", facts.Srid);
    }

    /// <summary>
    /// The PL/SQL that makes <c>g</c> the shape <see cref="BindGeometryAsync"/> binds: from WKB for x and y, and from its
    /// parts for a shape with Z or M, whose ISO WKB <c>FROM_WKBGEOMETRY</c> refuses (<see cref="SdoShape"/>).
    /// </summary>
    private static string Assignment(Geometry geometry) =>
        Ordinates.Of(geometry) == GeometryOrdinates.None
            ? "g := sdo_util.from_wkbgeometry(:geom); g.sdo_srid := :srid; "
            : "select sdo_geometry(:gt, :srid, null, "
                + "cast(multiset(select v from json_table(:ei, '$[*]' columns (n for ordinality, v number path '$')) order by n) as sdo_elem_info_array), "
                + "cast(multiset(select v from json_table(:ord, '$[*]' columns (n for ordinality, v number path '$')) order by n) as sdo_ordinate_array)) "
                + "into g from dual; ";

    /// <summary>The ordinates each updated row stores, or null for a row with no shape; a missing row is absent.</summary>
    private async Task<IReadOnlyDictionary<long, GeometryOrdinates?>> StoredOrdinatesAsync(
        OracleConnection connection, IReadOnlyList<FeatureUpdate> updates, CancellationToken cancellationToken)
    {
        Dictionary<long, GeometryOrdinates?> found = [];

        if (updates.Count == 0)
        {
            return found;
        }

        await using OracleCommand command = Command(
            $"select r.{Id}, case when r.{Shape} is null then null else r.{Shape}.get_dims() end, "
            + $"case when r.{Shape} is null then null else r.{Shape}.get_lrs_dim() end "
            + $"from {Table} r where r.{Id} in (select j.v from json_table(:ids, '$[*]' columns (v number path '$')) j)",
            connection);

        command.Parameters.Add("ids", OracleDbType.Clob, JsonSerializer.Serialize(updates.Select(u => u.Identity).Distinct()), ParameterDirection.Input);

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);

            if (reader.IsDBNull(1))
            {
                found[id] = null;
                continue;
            }

            int dims = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            bool measured = !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) > 0;

            found[id] = dims switch
            {
                >= 4 => GeometryOrdinates.Z | GeometryOrdinates.M,
                3 => measured ? GeometryOrdinates.M : GeometryOrdinates.Z,
                _ => GeometryOrdinates.None,
            };
        }

        return found;
    }

    /// <summary>The subtype each of these rows already is — ADR-065, asked only for the updates whose values depend on it.</summary>
    private async Task<IReadOnlyDictionary<long, long?>> StoredSubtypesAsync(
        OracleConnection connection, long[] needed, CancellationToken cancellationToken)
    {
        Dictionary<long, long?> codes = [];

        if (needed.Length == 0 || _columns.SubtypeField is not { } field)
        {
            return codes;
        }

        await using OracleCommand command = Command(
            $"select {Id}, {LayerDefinition.Quote(field)} from {Table} "
            + $"where {Id} in (select j.v from json_table(:ids, '$[*]' columns (v number path '$')) j)",
            connection);

        command.Parameters.Add("ids", OracleDbType.Clob, JsonSerializer.Serialize(needed), ParameterDirection.Input);

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            codes[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return codes;
    }

    /// <summary>Q-152: a subtype move that keeps a value the new subtype disallows is refused, naming the field.</summary>
    private async Task<string?> LeftOutsideItsNewSubtypeAsync(
        OracleConnection connection, FeatureUpdate update, string? owner, CancellationToken cancellationToken)
    {
        if (_columns.SubtypeMove(update) is not { } move)
        {
            return null;
        }

        (Subtype target, long code, FieldDescription[] unsent) = move;

        await using OracleCommand command = Command(
            $"select {LayerDefinition.Quote(_columns.SubtypeField!)}, "
            + string.Join(", ", unsent.Select(f => LayerDefinition.Quote(f.Name)))
            + $" from {Table} where {Id} = :id{OwnerClause(owner)}",
            connection);

        command.Parameters.Add("id", update.Identity);

        if (owner is not null)
        {
            command.Parameters.Add("owner", owner);
        }

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        reader.SuppressGetDecimalInvalidCastException = true;

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || (!reader.IsDBNull(0) && Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) == code))
        {
            return null;
        }

        for (int i = 0; i < unsent.Length; i++)
        {
            object? stored = reader.IsDBNull(i + 1)
                ? null
                : OracleFeatureSource.Normalise(reader.GetValue(i + 1), unsent[i].Type);

            if (EditColumns.SubtypeMoveRefusal(target, unsent[i], stored) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>
    /// The ownership predicate for a write's <c>where</c>; a layer that records no creator has no row anybody owns.
    /// </summary>
    private string OwnerClause(string? owner) =>
        owner is null ? string.Empty
        : _tracking.Creator is null ? " and 1 = 0"
        : $" and {LayerDefinition.Quote(_tracking.Creator)} = :owner";

    /// <summary>Why a write that changed nothing changed nothing: a row that is not there, or not the caller's.</summary>
    private async Task<EditResult> WhyNotAsync(
        OracleConnection connection, long identity, string? owner, CancellationToken cancellationToken) =>
        owner is not null
        && await WhoseAsync(connection, identity, owner, cancellationToken).ConfigureAwait(false) is { } refused
            ? refused
            : EditResult.Missing(identity);

    /// <summary>The refusal for a row this caller does not own, or null when it is theirs — ADR-064.</summary>
    private async Task<EditResult?> WhoseAsync(
        OracleConnection connection, long identity, string owner, CancellationToken cancellationToken)
    {
        if (_tracking.Creator is null)
        {
            return EditColumns.NotOwned(identity, recordsCreator: false, creator: null);
        }

        await using OracleCommand command = Command(
            $"select {LayerDefinition.Quote(_tracking.Creator)} from {Table} where {Id} = :id", connection);

        command.Parameters.Add("id", identity);

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return EditResult.Missing(identity);
        }

        string? creator = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);

        return creator is not null && string.Equals(creator, owner, StringComparison.Ordinal)
            ? null
            : EditColumns.NotOwned(identity, recordsCreator: true, creator);
    }

    private OracleCommand Command(string sql, OracleConnection connection) => new(sql, connection)
    {
        BindByName = true,
        CommandTimeout = (int)Math.Ceiling(_timeout.TotalSeconds),
    };

    private static void Bind(OracleCommand command, List<(string Column, object? Value)> bound)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            command.Parameters.Add("v" + i.ToString(CultureInfo.InvariantCulture), Value(bound[i].Value));
        }
    }

    /// <summary>A value as the driver binds it: a boolean as Oracle's 1 or 0, a missing one as a null.</summary>
    private static object Value(object? value) => value switch
    {
        null => DBNull.Value,
        bool flag => flag ? 1 : 0,
        Guid id => id.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant(),
        _ => value,
    };

    /// <summary>A database refusal in words the caller can act on — the column, never the table's internal name.</summary>
    /// <remarks>
    /// The managed driver wraps the database's error, so the number is read from the innermost exception
    /// (<see cref="OracleDataSourceProbe.NumberOf"/>). ORA-01400 and ORA-01407 name the column as
    /// <c>("OWNER"."TABLE"."COLUMN")</c>; only the last part is said.
    /// </remarks>
    internal static string Explain(OracleException e) => OracleDataSourceProbe.NumberOf(e) switch
    {
        1400 or 1407 => ColumnIn(e) is { } column
            ? $"'{column}' is required, and this edit leaves it without a value."
            : "A required field is left without a value by this edit.",
        1 => "This would duplicate a value that must be unique in this layer.",
        2291 => "This refers to a row that does not exist.",
        2292 => "Other rows refer to this one, so it cannot be removed.",
        2290 => "A rule on this layer's table refused this value.",
        1722 or 1858 or 1840 or 1841 or 1843 or 1847 or 6502 or 1438 or 12899 =>
            "A value is the wrong type or out of range for its column.",
        1031 => "The server's credential for this database may not write to this table.",
        int number => $"The database refused this edit (ORA-{number.ToString("00000", CultureInfo.InvariantCulture)}).",
    };

    private static string? ColumnIn(Exception e)
    {
        for (Exception? at = e; at is not null; at = at.InnerException)
        {
            int open = at.Message.LastIndexOf(".\"", StringComparison.Ordinal);

            if (open >= 0)
            {
                int close = at.Message.IndexOf('"', open + 2);

                if (close > open)
                {
                    return at.Message[(open + 2)..close];
                }
            }
        }

        return null;
    }
}
