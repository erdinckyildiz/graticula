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
/// <b>ADR-008 §4.5a — lossy on read means not writable.</b> Our geometry model
/// is two-dimensional, so a client that read a feature carrying Z read it flat.
/// Letting it write back would silently drop the third ordinate. The target
/// rows' dimensionality is checked before any update runs, and the ones that
/// would lose an ordinate are refused individually.
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

    /// <summary>Creates the writer.</summary>
    /// <param name="dataSource">The pool for the layer's database.</param>
    /// <param name="layer">The layer definition.</param>
    /// <param name="fields">
    /// The layer's real columns, from <see cref="IFeatureSource.DescribeAsync"/>.
    /// This is the whitelist §4.6 requires, and it comes from the database
    /// rather than from the request.
    /// </param>
    public PostGisFeatureWriter(
        NpgsqlDataSource dataSource, LayerDefinition layer, IReadOnlyList<FieldDescription> fields)
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
    }

    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        List<EditResult> adds = [];
        List<EditResult> updates = [];
        List<EditResult> deletes = [];

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        // Read the dimensionality of every row an update targets, once, before
        // touching anything. Doing it per row would be a query per feature; doing
        // it not at all would flatten somebody's 3D data.
        IReadOnlyDictionary<long, int> zmFlags = await ReadZmFlagsAsync(
            connection, transaction, batch.Updates, cancellationToken).ConfigureAwait(false);

        int savepoint = 0;

        foreach (FeatureAdd add in batch.Adds)
        {
            adds.Add(await IsolatedAsync(
                transaction, savepoint++,
                () => AddAsync(connection, transaction, add, cancellationToken),
                cancellationToken).ConfigureAwait(false));
        }

        foreach (FeatureUpdate update in batch.Updates)
        {
            updates.Add(await IsolatedAsync(
                transaction, savepoint++,
                () => UpdateAsync(
                    connection, transaction, update, zmFlags,
                    Expected(batch, update.Identity), cancellationToken),
                cancellationToken).ConfigureAwait(false));
        }

        foreach (long objectId in batch.Deletes)
        {
            deletes.Add(await IsolatedAsync(
                transaction, savepoint++,
                () => DeleteAsync(
                    connection, transaction, objectId,
                    Expected(batch, objectId), cancellationToken),
                cancellationToken).ConfigureAwait(false));
        }

        EditOutcome outcome = new(adds, updates, deletes, RolledBack: false);

        if (batch.RollbackOnFailure && (!outcome.AllSucceeded || batch.AnythingAlreadyFailed))
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

    private async Task<EditResult> AddAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeatureAdd add,
        CancellationToken cancellationToken)
    {
        if (!TryBindColumns(add.Attributes, out List<(string Column, object? Value)> bound, out string? error))
        {
            return EditResult.Failed(-1, error!);
        }

        List<string> columns = [.. bound.Select(b => LayerDefinition.Quote(b.Column))];
        List<string> values = [.. bound.Select((_, i) => $"@v{i}")];

        if (add.Geometry is { IsEmpty: false })
        {
            columns.Add(LayerDefinition.Quote(_layer.GeometryColumn));
            values.Add($"st_geomfromwkb(@geom, {_layer.Srid})");
        }

        if (columns.Count == 0)
        {
            return EditResult.Failed(
                -1, "The feature has neither attributes nor geometry, so there is nothing to add.");
        }

        string sql =
            $"insert into {_layer.QuotedTable} ({string.Join(", ", columns)}) "
            + $"values ({string.Join(", ", values)}) "
            + $"returning {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)}";

        await using NpgsqlCommand command = new(sql, connection, transaction);
        Bind(command, bound, add.Geometry);

        try
        {
            object? id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return EditResult.Ok(Convert.ToInt64(id, CultureInfo.InvariantCulture));
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
        IReadOnlyDictionary<long, int> zmFlags,
        string[]? expected,
        CancellationToken cancellationToken)
    {
        if (!zmFlags.TryGetValue(update.Identity, out int zmFlag))
        {
            return EditResult.Missing(update.Identity);
        }

        // ADR-008 §4.5a, made concrete. zmFlag is 0 for 2D, 1 for M, 2 for Z,
        // 3 for ZM. Writing our flat geometry over anything else discards an
        // ordinate the client never saw.
        if (update.Geometry is not null && zmFlag != 0)
        {
            return EditResult.Failed(
                update.Identity,
                $"This feature's stored geometry carries {Ordinates(zmFlag)}, and this server "
                + "reads and writes two dimensions. Overwriting it would silently discard "
                + $"{(zmFlag == 3 ? "them" : "it")}, so the edit is refused. Attribute-only "
                + "updates to this feature are still accepted.");
        }

        if (!TryBindColumns(update.Attributes, out List<(string Column, object? Value)> bound, out string? error))
        {
            return EditResult.Failed(update.Identity, error!);
        }

        List<string> assignments =
            [.. bound.Select((b, i) => $"{LayerDefinition.Quote(b.Column)} = @v{i}")];

        if (update.Geometry is not null)
        {
            assignments.Add(
                $"{LayerDefinition.Quote(_layer.GeometryColumn)} = st_geomfromwkb(@geom, {_layer.Srid})");
        }

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

        // <b>The precondition rides in the `where`, which is what makes it atomic -- D-186.</b>
        // Reading the version first and comparing it here would be check-then-act: two
        // clients could both read the same version, both find it current, and both write.
        // The database compares and writes in one statement or does neither.
        string sql =
            $"update {_layer.QuotedTable} set {string.Join(", ", assignments)} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = @id"
            + (expected is null ? string.Empty : " and xmin::text = any(@expected)");

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("id", update.Identity);

        if (expected is not null)
        {
            command.Parameters.AddWithValue("expected", expected);
        }

        Bind(command, bound, update.Geometry);

        try
        {
            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (affected > 0)
            {
                return EditResult.Ok(update.Identity);
            }

            // Nothing changed. Without a precondition that can only be a missing row; with
            // one it is either that or a version that moved, and the caller needs to know
            // which, because one is 404 and the other is 412.
            return expected is not null
                && await MatchAsync(
                        connection, transaction, update.Identity, expected, cancellationToken)
                        .ConfigureAwait(false) is not Match.Gone
                ? EditResult.Stale(update.Identity)
                : EditResult.Missing(update.Identity);
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
        CancellationToken cancellationToken)
    {
        string sql =
            $"delete from {_layer.QuotedTable} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = @id"
            + (expected is null ? string.Empty : " and xmin::text = any(@expected)");

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("id", objectId);

        if (expected is not null)
        {
            command.Parameters.AddWithValue("expected", expected);
        }

        try
        {
            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Deleting something that is already gone is reported as a failure
            // rather than shrugged off, because the client believes it deleted a
            // feature it never saw. ArcGIS reports it the same way.
            if (affected > 0)
            {
                return EditResult.Ok(objectId);
            }

            // A versioned delete that removed nothing may have found the row and
            // refused it -- D-186. Which one it was decides 404 against 412.
            return expected is not null
                && await MatchAsync(
                        connection, transaction, objectId, expected, cancellationToken)
                        .ConfigureAwait(false) is not Match.Gone
                ? EditResult.Stale(objectId)
                : EditResult.Missing(objectId);
        }
        catch (PostgresException e)
        {
            return EditResult.Failed(objectId, Explain(e));
        }
    }

    /// <summary>Reads the dimensionality of every row the updates target.</summary>
    private async Task<IReadOnlyDictionary<long, int>> ReadZmFlagsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<FeatureUpdate> updates,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        // coalesce, because a row with a null geometry has no zmflag and is not
        // therefore three-dimensional — it is empty, and writing a shape into it
        // loses nothing.
        string sql =
            $"select {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)}, "
            + $"coalesce(st_zmflag({LayerDefinition.Quote(_layer.GeometryColumn)}), 0) "
            + $"from {_layer.QuotedTable} "
            + $"where {LayerDefinition.Quote(_layer.IntegerIdentityColumn!)} = any(@ids)";

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("ids", updates.Select(u => u.Identity).Distinct().ToArray());

        Dictionary<long, int> flags = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            flags[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return flags;
    }

    /// <summary>
    /// Checks every column against the layer's real columns.
    /// </summary>
    /// <remarks>
    /// ADR-008 §4.6. A column name cannot be a parameter, so it is compared
    /// against the list the database gave us and refused if absent. The object
    /// id and the geometry column are refused too: the first is assigned by the
    /// database and the second has its own handling, and letting either through
    /// here would write a raw value into a column that expects something else.
    /// </remarks>
    private bool TryBindColumns(
        IReadOnlyDictionary<string, object?> attributes,
        out List<(string Column, object? Value)> bound,
        out string? error)
    {
        bound = [];
        error = null;

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

    private static string Ordinates(int zmFlag) => zmFlag switch
    {
        1 => "an M ordinate",
        2 => "a Z ordinate",
        _ => "Z and M ordinates",
    };

    /// <summary>
    /// A database refusal in words the caller can act on.
    /// </summary>
    /// <remarks>
    /// The SQL state is the reliable part and the message text is appended
    /// rather than replaced — the provider's own error is often the only thing
    /// that names the constraint.
    /// </remarks>
    private static string Explain(PostgresException e) => e.SqlState switch
    {
        "23502" => $"A column that cannot be null was not given a value: {e.MessageText}",
        "23505" => $"This would duplicate a value that must be unique: {e.MessageText}",
        "23503" => $"This references a row that does not exist: {e.MessageText}",
        "23514" => $"A check constraint refused this value: {e.MessageText}",
        "22P02" or "22003" => $"A value is the wrong type or out of range: {e.MessageText}",
        "42501" => "The server's credential for this database may not write to this table.",
        _ => $"The database refused this edit ({e.SqlState}): {e.MessageText}",
    };
}
