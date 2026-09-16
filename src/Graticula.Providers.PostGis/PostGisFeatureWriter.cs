using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Providers.PostGis;

/// <summary>
/// Applies edits to a PostGIS layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first code in this project that can destroy a customer's data.</b>
/// Everything before it was read-only, so the failure modes were wrong answers;
/// here they are lost rows. Three rules shape it, and each one is a refusal
/// rather than a best effort.
/// </para>
/// <para>
/// <b>ADR-008 §4.6 — identifiers are whitelisted, values are parameters.</b>
/// Every column named by a client is checked against the columns the layer
/// actually has before it reaches SQL. A column name cannot be parameterised, so
/// the only safe handling is to refuse anything not on the list.
/// </para>
/// <para>
/// <b>ADR-008 §4.5a — a geometry is written only where nothing it carries or the row stores is lost.</b>
/// Since ADR-077 §10 a geometry may carry Z and M. The target rows' dimensionality is read before any
/// update runs, and the column's declaration before any add, and a geometry whose ordinates differ from
/// either is refused individually — a flat shape over an elevation, and an elevation into a flat row.
/// </para>
/// <para>
/// <b>One transaction, always.</b> Even when the caller allows partial
/// application, the batch runs inside a transaction — the difference is only
/// whether a failure rolls it back or is recorded and skipped. Running without
/// one would leave a half-applied batch on a dropped connection with no record
/// of where it stopped.
/// </para>
/// </remarks>
public sealed class PostGisFeatureWriter : IFeatureWriter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly LayerDefinition _layer;
    private readonly IReadOnlyList<FieldDescription> _fields;
    private readonly EditorTracking _tracking;
    private readonly LayerSubtypes? _subtypes;

    /// <summary>Creates the writer.</summary>
    /// <param name="dataSource">The pool for the layer's database.</param>
    /// <param name="layer">The layer definition.</param>
    /// <param name="fields">
    /// The layer's real columns, from <see cref="IFeatureSource.DescribeAsync"/>.
    /// This is the whitelist §4.6 requires, and it comes from the database
    /// rather than from the request.
    /// </param>
    /// <param name="tracking">
    /// Which columns record edits — ADR-064 — or null for a layer that records none. Last and
    /// optional, so a writer built before editor tracking existed writes as it did.
    /// </param>
    /// <param name="subtypes">
    /// The layer's subtypes — ADR-065 — or null for a layer with none. Last and optional for the
    /// same reason. The columns' own domains ride on <paramref name="fields"/>.
    /// </param>
    /// <param name="geometryKind">
    /// The layer's geometry kind, so a repaired polygon is stored as the column's kind; null leaves polygons
    /// as they are made (Q-153).
    /// </param>
    public PostGisFeatureWriter(
        NpgsqlDataSource dataSource,
        LayerDefinition layer,
        IReadOnlyList<FieldDescription> fields,
        EditorTracking? tracking = null,
        LayerSubtypes? subtypes = null,
        GeometryKind? geometryKind = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(fields);

        if (layer.IntegerIdentityColumn is null)
        {
            // ADR-013 §2a. Without a unique integer key there is no way to name
            // a row for update or delete, so editing is not merely unsupported —
            // it is unexpressible.
            throw new ArgumentException(
                $"Layer '{layer.Name}' has no integer object-id column, so its features cannot be "
                + "addressed for update or delete. It is readable and not editable.",
                nameof(layer));
        }

        _dataSource = dataSource;
        _layer = layer;
        _fields = fields;
        _tracking = tracking ?? EditorTracking.None;
        _subtypes = subtypes;
        _globalId = GlobalIds.FieldOf(fields);
        _geometryKind = geometryKind;
    }

    /// <summary>The layer's GlobalID column, which this server fills, or null.</summary>
    private readonly string? _globalId;

    /// <summary>The layer's declared geometry kind, when the caller knows it; null when it does not.</summary>
    private readonly GeometryKind? _geometryKind;

    /// <summary>
    /// <c>returning</c> the object id, and the GlobalID beside it where the layer has one, so an edit
    /// result can carry both — 2026-09-15.
    /// </summary>
    private string Returning =>
        $" returning {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)}"
        + (_globalId is null ? string.Empty : $", {LayerDefinition.Quote(_globalId)}");

    /// <summary>
    /// The SQL a sent geometry is stored as, and the SQL that says whether it had to be repaired.
    /// </summary>
    /// <param name="sourceSrid">The reference it was sent in, when not the layer's.</param>
    /// <returns>The stored expression, and a boolean expression for <c>returning</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Q-153, owner decision 2026-09-15: project and normalise on write.</b> A geometry sent in another
    /// reference is transformed into the layer's by PostGIS, the engine every other projection here uses.
    /// </para>
    /// <para>
    /// <b>A polygon that is not valid is stored as the valid polygon made of it</b> —
    /// <c>ST_MakeValid</c>, keeping only its areas — and the edit says so, because a bow-tie kept as sent
    /// breaks every later spatial query on it. A valid polygon passes through untouched; <c>ST_IsValid</c> is
    /// asked first so the repair is paid only by a shape that needs it. A repair that leaves several parts
    /// on a single-polygon column fails that feature with the database's refusal rather than dropping a part.
    /// </para>
    /// </remarks>
    private (string Stored, string Repaired) GeometrySql(int? sourceSrid)
    {
        string sent = sourceSrid is { } from && from != _layer.Srid
            ? $"st_transform(st_geomfromwkb(@geom, {from}), {_layer.Srid})"
            : $"st_geomfromwkb(@geom, {_layer.Srid})";

        if (_geometryKind is not (GeometryKind.Polygon or GeometryKind.MultiPolygon))
        {
            return (sent, "false");
        }

        string valid = $"(case when st_isvalid({sent}) then {sent} else st_collectionextract(st_makevalid({sent}), 3) end)";

        string stored = _geometryKind == GeometryKind.MultiPolygon
            ? $"st_multi({valid})"
            : $"(case when st_numgeometries({valid}) = 1 then st_geometryn({valid}, 1) else {valid} end)";

        return (stored, $"not st_isvalid({sent})");
    }

    /// <summary>Runs a write that returns a row, and reads its object id, GlobalID and whether its shape was repaired.</summary>
    private async Task<(long Id, Guid? GlobalId, bool Repaired)?> ReturnedAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        long id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        Guid? global = _globalId is not null && !reader.IsDBNull(1) ? reader.GetGuid(1) : null;
        int repairedAt = _globalId is null ? 1 : 2;
        bool repaired = reader.FieldCount > repairedAt && !reader.IsDBNull(repairedAt) && reader.GetBoolean(repairedAt);

        return (id, global, repaired);
    }

    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        EditOutcome outcome = await ApplyWithinAsync(connection, transaction, batch, cancellationToken)
            .ConfigureAwait(false);

        if (MustRollBack(batch, outcome))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            // The per-feature results are kept, not discarded. A client whose
            // batch was rolled back still needs to know which feature caused it,
            // and "the batch failed" is not an answer anybody can act on.
            return outcome with { RolledBack = true };
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Whether a batch that asked for all or nothing has to be rolled back after this outcome.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="outcome">What applying it did.</param>
    /// <returns>Whether nothing of it may be kept.</returns>
    public static bool MustRollBack(EditBatch batch, EditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(outcome);

        return batch.RollbackOnFailure && (!outcome.AllSucceeded || batch.AnythingAlreadyFailed);
    }

    /// <summary>
    /// Applies a batch inside a transaction the caller owns, and neither commits nor rolls back.
    /// </summary>
    /// <param name="connection">The open connection, which must be one of this writer's source.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="batch">The edits.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened to each edit, with <c>RolledBack</c> false; the caller decides.</returns>
    /// <remarks>
    /// <b>Split from <see cref="ApplyAsync"/> on 2026-09-15 for the service-level
    /// <c>applyEdits</c></b>, which edits several layers and, when all or nothing is asked for, has
    /// to keep or discard them together. Each edit still runs in its own savepoint, so one failure
    /// does not abort the others' statements, and savepoint names are reused only after they are
    /// released — which PostgreSQL allows — so two writers can share one transaction in turn.
    /// </remarks>
    public async Task<EditOutcome> ApplyWithinAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EditBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(batch);

        List<EditResult> adds = [];
        List<EditResult> updates = [];
        List<EditResult> deletes = [];

        // Read the dimensionality of every row an update targets, once, before
        // touching anything. Doing it per row would be a query per feature; doing
        // it not at all would flatten somebody's 3D data.
        IReadOnlyDictionary<long, int?> zmFlags = await ReadZmFlagsAsync(
            connection, transaction, batch.Updates, cancellationToken).ConfigureAwait(false);

        // <b>What the column declares, once per batch that writes a shape</b> — for an add, and for an update
        // into a row with no geometry yet. Null for a column typed as bare `geometry`, which declares nothing
        // and holds whatever it is given.
        GeometryOrdinates? declared =
            batch.Adds.Any(add => add.Geometry is { IsEmpty: false }) || batch.Updates.Any(update => update.Geometry is not null)
                ? await ReadDeclaredOrdinatesAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
                : null;

        // <b>ADR-065: the subtype a feature already is, for the updates that need it</b> — the
        // ones that write a column some subtype governs and do not say which subtype the feature
        // is. Once, for the same reason as the flags above; not at all on a layer where no subtype
        // gives any column a domain of its own.
        IReadOnlyDictionary<long, long?> storedSubtypes = await ReadSubtypesAsync(
            connection, transaction, batch.Updates, cancellationToken).ConfigureAwait(false);

        int savepoint = 0;

        foreach (FeatureAdd add in batch.Adds)
        {
            adds.Add(await IsolatedAsync(
                transaction, savepoint++,
                () => AddAsync(connection, transaction, add, declared, batch, cancellationToken),
                cancellationToken).ConfigureAwait(false));
        }

        foreach (FeatureUpdate update in batch.Updates)
        {
            updates.Add(await IsolatedAsync(
                transaction, savepoint++,
                () => UpdateAsync(
                    connection, transaction, update, zmFlags, declared,
                    storedSubtypes.TryGetValue(update.Identity, out long? stored) ? stored : null,
                    Expected(batch, update.Identity), batch, cancellationToken),
                cancellationToken).ConfigureAwait(false));
        }

        foreach (long objectId in batch.Deletes)
        {
            deletes.Add(await IsolatedAsync(
                transaction, savepoint++,
                () => DeleteAsync(
                    connection, transaction, objectId,
                    Expected(batch, objectId), batch, cancellationToken),
                cancellationToken).ConfigureAwait(false));
        }

        return new EditOutcome(adds, updates, deletes, RolledBack: false);
    }

    /// <summary>
    /// Runs one edit inside a savepoint, so its failure does not take the others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, partial application is impossible</b>, and the reason is
    /// PostgreSQL rather than us: the first statement to error aborts the whole
    /// transaction, and every statement after it fails with <em>current
    /// transaction is aborted</em>. So a batch with <c>rollbackOnFailure=false</c>
    /// still lost every good edit, and the per-feature results after the first
    /// failure were all the same misleading message.
    /// </para>
    /// <para>
    /// Found by a test asserting the good row survived. It did not.
    /// </para>
    /// <para>
    /// Applied even when the caller asked for all-or-nothing, because the
    /// results are reported per feature either way: a client whose batch was
    /// rolled back still needs to know which feature caused it, and that answer
    /// is only truthful if each edit was attempted on a clean transaction state.
    /// </para>
    /// </remarks>
    private static async Task<EditResult> IsolatedAsync(
        NpgsqlTransaction transaction,
        int ordinal,
        Func<Task<EditResult>> edit,
        CancellationToken cancellationToken)
    {
        string name = $"edit{ordinal}";

        await transaction.SaveAsync(name, cancellationToken).ConfigureAwait(false);

        EditResult result = await edit().ConfigureAwait(false);

        if (result.Succeeded)
        {
            await transaction.ReleaseAsync(name, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await transaction.RollbackAsync(name, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// The object ids of the features carrying these GlobalIDs — what <c>useGlobalIds=true</c> edits
    /// are addressed by.
    /// </summary>
    /// <param name="dataSource">The layer's pool.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="globalIdColumn">Its GlobalID column.</param>
    /// <param name="globalIds">The ids to find.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each GlobalID found, with its object id; an id not in the table is absent.</returns>
    /// <remarks>
    /// <b>Read before the edit rather than inside it, and that is safe for this pair.</b> A GlobalID
    /// is never written after the row is made and an object id is an identity, so the mapping cannot
    /// change under the edit; a row deleted in between is answered by the edit itself as missing.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<Guid, long>> ObjectIdsOfAsync(
        NpgsqlDataSource dataSource,
        LayerDefinition layer,
        string globalIdColumn,
        IReadOnlyCollection<Guid> globalIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(globalIds);

        Dictionary<Guid, long> found = [];

        if (globalIds.Count == 0)
        {
            return found;
        }

        await using NpgsqlCommand command = dataSource.CreateCommand(
            $"select {LayerDefinition.Quote(globalIdColumn)}, {LayerDefinition.Quote(layer.IntegerIdentityColumn!)}::bigint "
            + $"from {LayerDefinition.Quote(layer.SchemaName)}.{LayerDefinition.Quote(layer.TableName)} "
            + $"where {LayerDefinition.Quote(globalIdColumn)} = any(@ids)");
        command.Parameters.AddWithValue("ids", globalIds.ToArray());

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return found;
    }

    private async Task<EditResult> AddAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeatureAdd add,
        GeometryOrdinates? declared,
        EditBatch batch,
        CancellationToken cancellationToken)
    {
        if (add.Geometry is { IsEmpty: false } shape && declared is { } stores
            && DimensionRefusal(shape, stores, "the layer's geometry column") is { } refused)
        {
            return EditResult.Failed(-1, refused);
        }

        // A new feature has no stored subtype: the one it is given is the one it is.
        if (!TryBindColumns(add.Attributes, null, out List<(string Column, object? Value)> bound, out string? error, keepGlobalId: batch.KeepsGlobalIds))
        {
            return EditResult.Failed(-1, error!);
        }

        List<string> columns = [.. bound.Select(b => LayerDefinition.Quote(b.Column))];
        List<string> values = [.. bound.Select((_, i) => $"@v{i}")];

        string repairedSql = "false";

        if (add.Geometry is { IsEmpty: false } measured
            && await UnrepairableMeasuresAsync(connection, transaction, measured, cancellationToken).ConfigureAwait(false) is { } unrepairable)
        {
            return EditResult.Failed(-1, unrepairable);
        }

        if (add.Geometry is { IsEmpty: false })
        {
            (string stored, repairedSql) = GeometrySql(add.GeometrySrid);
            columns.Add(LayerDefinition.Quote(_layer.GeometryColumn));
            values.Add(stored);
        }

        // Asked before signing, so a feature that carries nothing but what this server would
        // write for it is still refused as empty.
        if (columns.Count == 0)
        {
            return EditResult.Failed(
                -1, "The feature has neither attributes nor geometry, so there is nothing to add.");
        }

        // <b>ADR-064: the tracked columns are this server's to write</b> — the account's name and
        // the database's clock. Whatever the client sent for them was dropped by
        // `TryBindColumns`, so these are the only values the row can get.
        bool named = false;

        foreach ((string? column, string value, bool isName) in (ReadOnlySpan<(string?, string, bool)>)
        [
            (_tracking.Creator, "@editor", true),
            (_tracking.Editor, "@editor", true),
            (_tracking.Created, "now()", false),
            (_tracking.Edited, "now()", false),
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

        string sql =
            $"insert into {_layer.QuotedTable} ({string.Join(", ", columns)}) "
            + $"values ({string.Join(", ", values)})"
            + Returning + $", {repairedSql}";

        await using NpgsqlCommand command = new(sql, connection, transaction);
        Bind(command, bound, add.Geometry);

        if (named)
        {
            command.Parameters.AddWithValue("editor", batch.Editor!);
        }

        try
        {
            (long Id, Guid? GlobalId, bool Repaired) row = (await ReturnedAsync(command, cancellationToken).ConfigureAwait(false))!.Value;
            return EditResult.Ok(row.Id) with { GlobalId = row.GlobalId, GeometryRepaired = row.Repaired };
        }
        catch (PostgresException e)
        {
            return EditResult.Failed(-1, Explain(e));
        }
    }

    /// <summary>
    /// What version this batch expects one identity to be at, or null for no precondition.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="identity">The row.</param>
    /// <returns>The versions the edit will accept, or null for no precondition.</returns>
    private static string[]? Expected(EditBatch batch, long identity) =>
        batch.Expects is { } expects
        && expects.TryGetValue(identity, out IReadOnlyList<string>? versions)
        && versions.Count > 0
            ? [.. versions]
            : null;

    /// <summary>
    /// What a precondition says about one row: gone, at the version asked for, or moved on.
    /// </summary>
    private enum Match
    {
        /// <summary>There is no such row.</summary>
        Gone,

        /// <summary>The row is at one of the versions the caller offered.</summary>
        Matched,

        /// <summary>The row is there, at a version the caller did not offer.</summary>
        Moved,
    }

    /// <summary>
    /// Asks what a row's version is against the ones the caller offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inside the same transaction, so it is not a race - D-186.</b> A versioned write that
    /// affects no row has two possible causes and they need different answers: the row is gone
    /// (404) or somebody else wrote it first (412). Separating them needs a second look, and
    /// taking it inside the transaction that just failed to match means nothing can move
    /// between the two statements.
    /// </para>
    /// <para>
    /// <b>It never runs on the path that succeeds.</b> The write itself carries the comparison
    /// in its <c>where</c>; this is only asked when the write changed nothing, or when there is
    /// nothing to write. Optimistic concurrency costs an extra round trip exactly when it has
    /// refused something, which is the case nobody is timing.
    /// </para>
    /// </remarks>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction the write ran in.</param>
    /// <param name="identity">The row.</param>
    /// <param name="expected">The versions the caller offered; never empty.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>What the row says.</returns>
    private async Task<Match> MatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long identity,
        string[] expected,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            $"select xmin::text = any(@expected) from {_layer.QuotedTable} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = @id",
            connection,
            transaction);

        command.Parameters.AddWithValue("id", identity);
        command.Parameters.AddWithValue("expected", expected);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            null => Match.Gone,
            true => Match.Matched,
            _ => Match.Moved,
        };
    }

    private async Task<EditResult> UpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeatureUpdate update,
        IReadOnlyDictionary<long, int?> zmFlags,
        GeometryOrdinates? declared,
        long? storedSubtype,
        string[]? expected,
        EditBatch batch,
        CancellationToken cancellationToken)
    {
        if (!zmFlags.TryGetValue(update.Identity, out int? zmFlag))
        {
            return EditResult.Missing(update.Identity);
        }

        // ADR-008 §4.5a, made concrete. zmFlag is 0 for 2D, 1 for M, 2 for Z, 3 for ZM — ST_Zmflag's numbers,
        // which are GeometryOrdinates'. A row with a shape is held to what that shape carries; an empty row to
        // what its column declares, and to nothing when the column declares nothing.
        if (update.Geometry is not null
            && (zmFlag is { } flag ? (GeometryOrdinates)flag : declared) is { } stores
            && DimensionRefusal(
                update.Geometry,
                stores,
                zmFlag is null ? "the layer's geometry column" : "this feature's stored geometry") is { } lossy)
        {
            return EditResult.Failed(update.Identity, lossy);
        }

        if (!TryBindColumns(update.Attributes, storedSubtype, out List<(string Column, object? Value)> bound, out string? error))
        {
            return EditResult.Failed(update.Identity, error!);
        }

        if (await LeftOutsideItsNewSubtypeAsync(connection, transaction, update, batch, cancellationToken)
                .ConfigureAwait(false) is { } outside)
        {
            return EditResult.Failed(update.Identity, outside);
        }

        List<string> assignments =
            [.. bound.Select((b, i) => $"{LayerDefinition.Quote(b.Column)} = @v{i}")];

        string repairedSql = "false";

        if (update.Geometry is { } measured
            && await UnrepairableMeasuresAsync(connection, transaction, measured, cancellationToken).ConfigureAwait(false) is { } unrepairable)
        {
            return EditResult.Failed(update.Identity, unrepairable);
        }

        if (update.Geometry is not null)
        {
            (string stored, repairedSql) = GeometrySql(update.GeometrySrid);
            assignments.Add($"{LayerDefinition.Quote(_layer.GeometryColumn)} = {stored}");
        }

        string? owner = OwnerFilter(batch);

        if (assignments.Count == 0)
        {
            // Nothing to do is a success. A client that sends an update with no
            // changed fields has got what it asked for, and reporting a failure
            // would make an idempotent retry look broken.
            //
            // <b>But a precondition is still a precondition - D-186.</b> There is no
            // `where` to hide it in on this path, so it is asked directly. Skipping it
            // because the write is empty would let a stale client learn that its version
            // is current by sending an edit that changes nothing.
            //
            // <b>And so is ownership — ADR-064.</b> An empty edit to somebody else's feature
            // changes nothing, and answering it with success would still tell a caller that
            // the feature is one they may edit.
            if (owner is not null
                && await WhoseAsync(connection, transaction, update.Identity, owner, cancellationToken)
                    .ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            if (expected is null)
            {
                return EditResult.Ok(update.Identity);
            }

            return await MatchAsync(
                connection, transaction, update.Identity, expected, cancellationToken)
                .ConfigureAwait(false) switch
            {
                Match.Matched => EditResult.Ok(update.Identity),
                Match.Moved => EditResult.Stale(update.Identity),
                _ => EditResult.Missing(update.Identity),
            };
        }

        // <b>ADR-064: who changed it and when, written by this server</b> — only on a write that
        // changes something, which is why this comes after the empty case above.
        bool named = false;

        if (_tracking.Editor is not null && batch.Editor is not null)
        {
            assignments.Add($"{LayerDefinition.Quote(_tracking.Editor)} = @editor");
            named = true;
        }

        if (_tracking.Edited is not null)
        {
            assignments.Add($"{LayerDefinition.Quote(_tracking.Edited)} = now()");
        }

        // <b>The precondition rides in the `where`, which is what makes it atomic -- D-186.</b>
        // Reading the version first and comparing it here would be check-then-act: two
        // clients could both read the same version, both find it current, and both write.
        // The database compares and writes in one statement or does neither.
        //
        // <b>Ownership rides beside it, for the same reason — ADR-064.</b> Asking whose a row
        // is and then writing it would let the answer change between the two.
        string sql =
            $"update {_layer.QuotedTable} set {string.Join(", ", assignments)} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = @id"
            + (expected is null ? string.Empty : " and xmin::text = any(@expected)")
            + OwnerClause(owner)
            + Returning + $", {repairedSql}";

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("id", update.Identity);

        if (expected is not null)
        {
            command.Parameters.AddWithValue("expected", expected);
        }

        if (owner is not null)
        {
            command.Parameters.AddWithValue("owner", owner);
        }

        if (named)
        {
            command.Parameters.AddWithValue("editor", batch.Editor!);
        }

        Bind(command, bound, update.Geometry);

        try
        {
            if (await ReturnedAsync(command, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                return EditResult.Ok(update.Identity) with { GlobalId = changed.GlobalId, GeometryRepaired = changed.Repaired };
            }

            // Nothing changed. That is a missing row, somebody else's row, or a version that
            // moved — three different answers, and the caller needs to know which.
            return await WhyNotAsync(
                    connection, transaction, update.Identity, expected, owner, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException e)
        {
            return EditResult.Failed(update.Identity, Explain(e));
        }
    }

    private async Task<EditResult> DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long objectId,
        string[]? expected,
        EditBatch batch,
        CancellationToken cancellationToken)
    {
        string? owner = OwnerFilter(batch);

        string sql =
            $"delete from {_layer.QuotedTable} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = @id"
            + (expected is null ? string.Empty : " and xmin::text = any(@expected)")
            + OwnerClause(owner)
            + Returning;

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("id", objectId);

        if (expected is not null)
        {
            command.Parameters.AddWithValue("expected", expected);
        }

        if (owner is not null)
        {
            command.Parameters.AddWithValue("owner", owner);
        }

        try
        {
            // Deleting something that is already gone is reported as a failure
            // rather than shrugged off, because the client believes it deleted a
            // feature it never saw. ArcGIS reports it the same way.
            if (await ReturnedAsync(command, cancellationToken).ConfigureAwait(false) is { } removed)
            {
                return EditResult.Ok(objectId) with { GlobalId = removed.GlobalId };
            }

            // A delete that removed nothing may have found the row and refused it -- D-186 for
            // a version, ADR-064 for an owner. Which one it was decides what the caller is told.
            return await WhyNotAsync(
                    connection, transaction, objectId, expected, owner, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException e)
        {
            return EditResult.Failed(objectId, Explain(e));
        }
    }

    /// <summary>
    /// The name a row's creator must equal for this batch to change it, or null for any row — ADR-064.
    /// </summary>
    /// <summary>
    /// Why an update that moves a feature to another subtype leaves a value it did not send outside the new
    /// subtype's domain — or null when it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Q-152, owner decision 2026-09-15: refuse, and name the field.</b> ADR-065 checked what is written
    /// and not the row, so <c>kind</c> 2 → 1 with <c>pressure</c> left at 80 was accepted although subtype 1
    /// allows 0–50 — the edit made the row invalid and nothing said so. Now it is refused, and the refusal
    /// says which field to send with the change.
    /// </para>
    /// <para>
    /// <b>Only when the subtype changes, and only the columns the new subtype governs and the edit did not
    /// send</b>, so the ordinary update pays nothing: the stored values are read in one statement for the
    /// one feature, and a feature already of that subtype is not re-judged for values it already had.
    /// Ownership is applied to the read, so a caller who may not change the row learns nothing of it here.
    /// </para>
    /// </remarks>
    private async Task<string?> LeftOutsideItsNewSubtypeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeatureUpdate update,
        EditBatch batch,
        CancellationToken cancellationToken)
    {
        if (_subtypes is not { } subtypes
            || !update.Attributes.TryGetValue(subtypes.Field, out object? sent)
            || !DomainRules.TryCode(sent, out long code)
            || subtypes.Find(code) is not { } target)
        {
            return null;
        }

        FieldDescription[] unsent = [.. _fields.Where(f =>
            target.Domains.ContainsKey(f.Name) && !update.Attributes.ContainsKey(f.Name))];

        if (unsent.Length == 0)
        {
            return null;
        }

        string? owner = OwnerFilter(batch);

        await using NpgsqlCommand command = new(
            $"select {LayerDefinition.Quote(subtypes.Field)}::bigint, "
            + string.Join(", ", unsent.Select(f => LayerDefinition.Quote(f.Name)))
            + $" from {_layer.QuotedTable} where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = @id"
            + OwnerClause(owner),
            connection,
            transaction);

        command.Parameters.AddWithValue("id", update.Identity);

        if (owner is not null)
        {
            command.Parameters.AddWithValue("owner", owner);
        }

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // No row, or not the caller's: the update itself answers that, in its own words.
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || (!reader.IsDBNull(0) && reader.GetInt64(0) == code))
        {
            return null;
        }

        for (int i = 0; i < unsent.Length; i++)
        {
            object? stored = reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1);

            if (DomainRules.Refusal(unsent[i], stored, target.Domains[unsent[i].Name], target) is { } refusal)
            {
                return $"{refusal} This edit moves the feature to subtype {target.Code} ({target.Name}) and leaves "
                    + $"'{unsent[i].Name}' as it was, which the new subtype does not allow. Send '{unsent[i].Name}' "
                    + "with a value it allows, in the same edit.";
            }
        }

        return null;
    }

    private static string? OwnerFilter(EditBatch batch) =>
        batch.OwnOnly ? batch.Editor ?? string.Empty : null;

    /// <summary>The ownership predicate for a write's <c>where</c>, or nothing.</summary>
    /// <remarks>
    /// <b>A layer that records no creator has no row anybody owns</b>, so own-only matches none
    /// of it — <c>false</c> rather than a predicate on a column that is not there.
    /// </remarks>
    private string OwnerClause(string? owner) =>
        owner is null ? string.Empty
        : _tracking.Creator is null ? " and false"
        : $" and {LayerDefinition.Quote(_tracking.Creator)} = @owner";

    /// <summary>Why a write that changed nothing changed nothing.</summary>
    private async Task<EditResult> WhyNotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long identity,
        string[]? expected,
        string? owner,
        CancellationToken cancellationToken)
    {
        if (owner is not null
            && await WhoseAsync(connection, transaction, identity, owner, cancellationToken)
                .ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        return expected is not null
            && await MatchAsync(connection, transaction, identity, expected, cancellationToken)
                .ConfigureAwait(false) is not Match.Gone
            ? EditResult.Stale(identity)
            : EditResult.Missing(identity);
    }

    /// <summary>
    /// The refusal for a row this caller does not own, or null when it is theirs — ADR-064.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A row that is not there is still <see cref="EditResult.Missing"/></b>, so a caller who
    /// may change only their own features cannot use the difference to learn which ids exist
    /// beyond what reading already tells them.
    /// </para>
    /// <para>
    /// <b>Asked only on the path that refuses</b>, inside the transaction the write ran in —
    /// the shape <see cref="MatchAsync"/> has for a version, for the same reason.
    /// </para>
    /// </remarks>
    private async Task<EditResult?> WhoseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long identity,
        string owner,
        CancellationToken cancellationToken)
    {
        if (_tracking.Creator is null)
        {
            return EditResult.NotOwned(
                identity,
                "This layer does not record who created its features, so none of them is yours to "
                + "change. Changing them needs features:fullEdit.");
        }

        await using NpgsqlCommand command = new(
            $"select {LayerDefinition.Quote(_tracking.Creator)}::text from {_layer.QuotedTable} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = @id",
            connection,
            transaction);

        command.Parameters.AddWithValue("id", identity);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return EditResult.Missing(identity);
        }

        if (reader.IsDBNull(0))
        {
            return EditResult.NotOwned(
                identity,
                $"Feature {identity} has no creator recorded, so it is nobody's own. Changing it "
                + "needs features:fullEdit.");
        }

        return string.Equals(reader.GetString(0), owner, StringComparison.Ordinal)
            ? null
            : EditResult.NotOwned(
                identity,
                $"Feature {identity} was created by somebody else. Changing another account's "
                + "feature needs features:fullEdit.");
    }

    /// <summary>
    /// Why a geometry may not be written where <paramref name="stores"/> is kept, or null when it may.
    /// </summary>
    /// <param name="sent">The geometry the edit carries.</param>
    /// <param name="stores">What the row or column holds.</param>
    /// <param name="where">What holds it, for the sentence.</param>
    /// <remarks>
    /// <b>Both directions are losses, so both are refused.</b> A flat shape over an elevation discards the
    /// elevation; an elevation into a flat column is discarded by the column, or refused by PostGIS in words
    /// about typmods. Padding a missing Z with zero is not an option either: zero metres is a value, and
    /// nobody could tell it from a measured one (ADR-077 §10).
    /// </remarks>
    private static string? DimensionRefusal(Geometry sent, GeometryOrdinates stores, string where)
    {
        GeometryOrdinates carried = Ordinates.Of(sent);

        if (carried == stores)
        {
            return null;
        }

        GeometryOrdinates missing = stores & ~carried;

        if (missing != GeometryOrdinates.None)
        {
            return $"{char.ToUpperInvariant(where[0])}{where[1..]} carries {Ordinates.Name(missing)}, and the "
                + "geometry sent has none. Writing it would discard the stored value or invent one, so the edit "
                + $"is refused. Send the geometry with {(missing == (GeometryOrdinates.Z | GeometryOrdinates.M) ? "them" : "it")} "
                + "— hasZ / hasM and a value on every vertex — or update the attributes alone (ADR-077).";
        }

        GeometryOrdinates extra = carried & ~stores;

        return $"The geometry sent carries {Ordinates.Name(extra)}, and {where} stores "
            + $"{Ordinates.Name(stores) ?? "x and y only"}. Storing it would discard "
            + $"{(extra == (GeometryOrdinates.Z | GeometryOrdinates.M) ? "them" : "it")}, so the edit is refused (ADR-077).";
    }

    /// <summary>
    /// Why an invalid polygon carrying M cannot be stored, or null when it can — ADR-077 §10.
    /// </summary>
    /// <remarks>
    /// <b>Because the repair drops the measures, measured.</b> Q-153 stores an invalid polygon as the valid one
    /// <c>ST_MakeValid</c> makes of it. On PostGIS 3.4.3 that keeps Z — and gives the vertex it creates an
    /// elevation interpolated along its edge, which is ADR-077 §5.2 — and returns no M at all: a bow-tie with
    /// measures 10 to 40 came back as two triangles with none. Storing that is the silent loss ADR-077 exists
    /// to end, so the polygon is refused with the reason instead, and only a polygon that carries M and is
    /// invalid pays the extra question.
    /// </remarks>
    private async Task<string?> UnrepairableMeasuresAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Geometry geometry, CancellationToken cancellationToken)
    {
        if (_geometryKind is not (GeometryKind.Polygon or GeometryKind.MultiPolygon)
            || (Ordinates.Of(geometry) & GeometryOrdinates.M) == 0)
        {
            return null;
        }

        await using NpgsqlCommand command = new("select st_isvalid(st_geomfromwkb(@geom))", connection, transaction);
        command.Parameters.AddWithValue("geom", NpgsqlDbType.Bytea, WkbWriter.ToArray(geometry));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true
            ? null
            : "The polygon is not valid, and it carries M values. An invalid polygon is stored as the valid one "
              + "the database makes of it, and that repair discards every measure, so it is refused rather than "
              + "stored without them. Correct the rings — no self-intersection, holes inside their shell — and send "
              + "it again (ADR-077).";
    }

    /// <summary>What the geometry column declares, or null when it is bare <c>geometry</c> and declares nothing.</summary>
    private async Task<GeometryOrdinates?> ReadDeclaredOrdinatesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // The describe's query, cut to the one column: postgis_typmod_type answers `PointZ`, `MultiLineStringZM`,
        // and `Geometry` both for a bare `geometry` column and for `geometry(Geometry, 3857)`. **Those differ, and
        // only the typmod tells them apart** — the second declares two dimensions and PostGIS refuses a Z into
        // it (22023), measured on CI's 3.4.3 when this read the name alone. A bare column has no typmod: -1.
        const string Sql =
            """
            select case when g.atttypmod < 0 then null else postgis_typmod_type(g.atttypmod) end
            from pg_attribute g
            join pg_class c on c.oid = g.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = @schema and c.relname = @table and g.attname = @geometry
              and g.attnum > 0 and not g.attisdropped
            """;

        await using NpgsqlCommand command = new(Sql, connection, transaction);
        command.Parameters.AddWithValue("schema", _layer.SchemaName);
        command.Parameters.AddWithValue("table", _layer.TableName);
        command.Parameters.AddWithValue("geometry", _layer.GeometryColumn);

        object? type = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // Null is a bare column, which declares nothing; `Geometry` with a typmod declares x and y.
        return type is string name ? Ordinates.OfTypeName(name) : null;
    }

    /// <summary>Reads the dimensionality of every row the updates target; null for a row with no geometry.</summary>
    private async Task<IReadOnlyDictionary<long, int?>> ReadZmFlagsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<FeatureUpdate> updates,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return new Dictionary<long, int?>();
        }

        // Null for a row with no geometry, which has no zmflag: writing a shape into it loses nothing it
        // stores, and what it may hold is the column's declaration.
        string sql =
            $"select {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)}, "
            + $"st_zmflag({LayerDefinition.Quote(_layer.GeometryColumn)}) "
            + $"from {_layer.QuotedTable} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = any(@ids)";

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("ids", updates.Select(u => u.Identity).Distinct().ToArray());

        Dictionary<long, int?> flags = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            flags[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return flags;
    }

    /// <summary>
    /// The subtype each updated feature already is, for the updates whose values depend on it.
    /// </summary>
    /// <remarks>
    /// <b>Asked only when the answer changes what is allowed</b>: the layer has subtypes, some
    /// subtype gives a column a domain of its own, the update writes that column, and it does not
    /// say which subtype the feature is. Every other update is checked against what it sends, and
    /// asking would be a query per batch that decides nothing.
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, long?>> ReadSubtypesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<FeatureUpdate> updates,
        CancellationToken cancellationToken)
    {
        if (_subtypes is not { } subtypes || updates.Count == 0)
        {
            return new Dictionary<long, long?>();
        }

        long[] needed = [.. updates
            .Where(u => !u.Attributes.ContainsKey(subtypes.Field) && u.Attributes.Keys.Any(subtypes.Varies))
            .Select(u => u.Identity)
            .Distinct()];

        if (needed.Length == 0)
        {
            return new Dictionary<long, long?>();
        }

        await using NpgsqlCommand command = new(
            $"select {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)}, "
            + $"{LayerDefinition.Quote(subtypes.Field)}::bigint "
            + $"from {_layer.QuotedTable} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = any(@ids)",
            connection,
            transaction);

        command.Parameters.AddWithValue("ids", needed);

        Dictionary<long, long?> codes = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            codes[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        return codes;
    }

    /// <summary>
    /// Checks every column against the layer's real columns, and every value against the domain
    /// that governs it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR-008 §4.6. A column name cannot be a parameter, so it is compared
    /// against the list the database gave us and refused if absent. The object
    /// id and the geometry column are refused too: the first is assigned by the
    /// database and the second has its own handling, and letting either through
    /// here would write a raw value into a column that expects something else.
    /// </para>
    /// <para>
    /// <b>ADR-065 and ADR-013 condition 5: a domain the document reports is a domain this refuses
    /// a value outside.</b> Here, in the writer both faces share, so ArcGIS <c>applyEdits</c> and
    /// OGC API Features refuse the same values with the same sentence. A value is checked against
    /// its subtype's domain when the feature has a subtype that gives the column one — the subtype
    /// sent in the same edit, or the one it already is — and against the column's own otherwise.
    /// </para>
    /// <para>
    /// <b>What is checked is what is written</b>, not the row it lands in. An update that moves a
    /// feature to another subtype and leaves a column alone does not have that column's stored
    /// value checked against the new subtype's domain: the value was not sent, and refusing an edit
    /// for something already in the table is refusing a different edit than the one asked for.
    /// </para>
    /// </remarks>
    private bool TryBindColumns(
        IReadOnlyDictionary<string, object?> attributes,
        long? storedSubtype,
        out List<(string Column, object? Value)> bound,
        out string? error,
        bool keepGlobalId = false)
    {
        bound = [];
        error = null;

        // The subtype this feature will be: the one sent, when one is, and the one it is otherwise.
        long? subtype = storedSubtype;

        if (_subtypes is { } declared && attributes.TryGetValue(declared.Field, out object? sent))
        {
            subtype = DomainRules.TryCode(sent, out long code) ? code : null;
        }

        foreach (KeyValuePair<string, object?> attribute in attributes)
        {
            if (string.Equals(attribute.Key, _layer.IntegerIdentityColumn, StringComparison.Ordinal))
            {
                // Silently ignored rather than refused. Every ArcGIS client
                // round-trips the object id in the attributes of an update, and
                // refusing would fail every well-behaved edit.
                continue;
            }

            if (string.Equals(attribute.Key, _layer.GeometryColumn, StringComparison.Ordinal))
            {
                error =
                    $"'{attribute.Key}' is the geometry column and cannot be set as an attribute. "
                    + "Send the shape in the feature's 'geometry' member.";
                return false;
            }

            FieldDescription? field = null;

            foreach (FieldDescription candidate in _fields)
            {
                if (string.Equals(candidate.Name, attribute.Key, StringComparison.Ordinal))
                {
                    field = candidate;
                    break;
                }
            }

            if (field is null)
            {
                error =
                    $"'{attribute.Key}' is not a column of this layer. Columns are taken from the "
                    + "database rather than the request, so a name that is not there cannot be "
                    + "written.";
                return false;
            }

            // <b>A tracked column is dropped, not refused — ADR-064.</b> The document says it is
            // not editable, and an ArcGIS web client echoes every attribute back on an update
            // anyway; refusing would fail an ordinary edit over a value the client never meant
            // to change. This server writes the column itself, after this.
            if (field.Value.Maintained)
            {
                continue;
            }

            // <b>The GlobalID is this server's to write, like a tracked column</b> — dropped rather
            // than refused, because a client echoes it back on every update. The column's default
            // gives a new row its value.
            //
            // <b>Except on an add under `useGlobalIds=true`</b>, where the client minted it and will
            // refer to the feature by it. A null is still the column's default's to fill.
            if (string.Equals(attribute.Key, _globalId, StringComparison.Ordinal)
                && !(keepGlobalId && attribute.Value is Guid))
            {
                continue;
            }

            if (_subtypes is { } subtypes
                && string.Equals(attribute.Key, subtypes.Field, StringComparison.Ordinal))
            {
                if (DomainRules.SubtypeRefusal(subtypes, attribute.Value) is { } notASubtype)
                {
                    error = notASubtype;
                    return false;
                }
            }
            else
            {
                FieldDomain? governing = _subtypes?.DomainFor(attribute.Key, subtype, field.Value.Domain)
                    ?? field.Value.Domain;

                // The subtype is named in the refusal only when the domain is the subtype's, so a
                // value refused by the column's own domain is not blamed on the feature's kind.
                Subtype? whose = governing is not null
                    && !ReferenceEquals(governing, field.Value.Domain)
                    && subtype is { } kind
                        ? _subtypes!.Find(kind)
                        : null;

                if (DomainRules.Refusal(field.Value, attribute.Value, governing, whose) is { } outside)
                {
                    error = outside;
                    return false;
                }
            }

            bound.Add((attribute.Key, attribute.Value));
        }

        return true;
    }

    private static void Bind(
        NpgsqlCommand command, List<(string Column, object? Value)> bound, Geometry? geometry)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            command.Parameters.AddWithValue($"v{i}", bound[i].Value ?? DBNull.Value);
        }

        if (geometry is { IsEmpty: false })
        {
            command.Parameters.AddWithValue(
                "geom", NpgsqlDbType.Bytea, WkbWriter.ToArray(geometry));
        }
    }

    /// <summary>
    /// A database refusal in words the caller can act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The column, not the database's sentence.</b> This appended PostgreSQL's message text
    /// on the grounds that it is often the only thing naming the constraint, and it named more
    /// than that: a missing required value came back as <i>null value in column "req" of relation
    /// "veteran_probe_a5e96231"</i>, which hands every editor the hosted table's internal name —
    /// found on the showcase 2026-09-15. What a caller can act on is which field, and
    /// <see cref="PostgresException.ColumnName"/> carries it without the rest. The message stays
    /// for the two type errors, whose text is about the value the caller sent and nothing else.
    /// </para>
    /// </remarks>
    private static string Explain(PostgresException e) => e.SqlState switch
    {
        "23502" => e.ColumnName is { Length: > 0 } column
            ? $"'{column}' is required, and this edit leaves it without a value."
            : "A required field is left without a value by this edit.",
        "23505" => "This would duplicate a value that must be unique in this layer.",
        "23503" => "This refers to a row that does not exist.",
        "23514" => "A rule on this layer's table refused this value.",
        "22P02" or "22003" => $"A value is the wrong type or out of range: {e.MessageText}",
        "42501" => "The server's credential for this database may not write to this table.",
        _ => $"The database refused this edit ({e.SqlState}).",
    };
}
