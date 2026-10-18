using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Npgsql;

namespace Graticula.Providers.PostGis;

/// <summary>
/// A deleted hosted layer's tables set aside rather than dropped, and brought back — ADR-203.
/// </summary>
/// <remarks>
/// <b>Moved, not copied</b>: <c>ALTER … SET SCHEMA</c> and a rename are catalogue changes, as cheap for a table of a
/// million rows as for ten, and a trigger, an index or a view on the table moves with it because PostgreSQL holds them
/// by the table's identity, not its name. A table waiting is renamed <c>r{entry}_{n}</c>, so two deleted layers with the
/// same table name, and a name past PostgreSQL's 63 characters, never meet.
/// </remarks>
public sealed partial class PostGisImporter
{
    /// <summary>Where a deleted item's hosted tables wait.</summary>
    public const string RecycleSchema = "graticula_recycle";

    /// <summary>
    /// Moves a hosted layer's table, its attachments, its history and the history's function into the recycle schema,
    /// or a view layer's views; what was moved, as (schema, original, recycled, kind).
    /// </summary>
    /// <param name="table">The layer's table in the hosted schema.</param>
    /// <param name="view">Whether the layer is a view (ADR-113), whose relations are views.</param>
    /// <param name="key">A short key of the entry they belong to, which their waiting names start with.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each relation moved.</returns>
    public async Task<IReadOnlyList<(string Schema, string Original, string Recycled, string Kind)>> SetAsideAsync(
        string table, bool view, string key, CancellationToken cancellationToken)
    {
        RefuseOutsideHosted(HostedSchema, table, "set aside");

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, $"create schema if not exists {LayerDefinition.Quote(RecycleSchema)}", cancellationToken)
            .ConfigureAwait(false);

        string attachments = table + PostGisAttachmentStore.Suffix;
        List<(string, string, string, string)> moved = [];
        string[] relations = view
            ? [table, attachments, attachments + PostGisAttachmentStore.ChunkSuffix]
            : [table, attachments, attachments + PostGisAttachmentStore.ChunkSuffix, table + PostGisFeatureHistory.Suffix];

        foreach (string name in relations)
        {
            if (!await ExistsAsync(connection, transaction, name, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            string waiting = $"r{key}_{moved.Count}";
            string kind = view ? "view" : "table";
            await ExecuteAsync(connection, transaction,
                $"alter {kind} {Qualified(name)} set schema {LayerDefinition.Quote(RecycleSchema)}", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                $"alter {kind} {LayerDefinition.Quote(RecycleSchema)}.{LayerDefinition.Quote(name)} rename to {LayerDefinition.Quote(waiting)}",
                cancellationToken).ConfigureAwait(false);
            moved.Add((HostedSchema, name, waiting, kind));
        }

        // The history's trigger function is the table's own (ADR-078); it moves so the trigger keeps working when restored.
        string function = table + PostGisFeatureHistory.FunctionSuffix;

        if (!view && await FunctionExistsAsync(connection, transaction, function, cancellationToken).ConfigureAwait(false))
        {
            string waiting = $"r{key}_{moved.Count}";
            await ExecuteAsync(connection, transaction,
                $"alter function {Qualified(function)}() set schema {LayerDefinition.Quote(RecycleSchema)}", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                $"alter function {LayerDefinition.Quote(RecycleSchema)}.{LayerDefinition.Quote(function)}() rename to {LayerDefinition.Quote(waiting)}",
                cancellationToken).ConfigureAwait(false);
            moved.Add((HostedSchema, function, waiting, "function"));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return moved;
    }

    /// <summary>
    /// Moves set-aside relations back under their names; refused, with nothing moved, when one of those names has been
    /// taken since.
    /// </summary>
    /// <returns>Null on success, or the name that is taken.</returns>
    public async Task<string?> BringBackAsync(
        IReadOnlyList<(string Schema, string Original, string Recycled, string Kind)> relations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relations);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach ((string schema, string original, _, string kind) in relations)
        {
            RefuseOutsideHosted(schema, original, "restore");

            if (kind != "function" && await ExistsAsync(connection, transaction, original, cancellationToken).ConfigureAwait(false))
            {
                return $"{schema}.{original}";
            }
        }

        foreach ((string schema, string original, string recycled, string kind) in relations)
        {
            string arguments = kind == "function" ? "()" : string.Empty;
            await ExecuteAsync(connection, transaction,
                $"alter {kind} {LayerDefinition.Quote(RecycleSchema)}.{LayerDefinition.Quote(recycled)}{arguments} rename to {LayerDefinition.Quote(original)}",
                cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                $"alter {kind} {LayerDefinition.Quote(RecycleSchema)}.{LayerDefinition.Quote(original)}{arguments} set schema {LayerDefinition.Quote(schema)}",
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Moves relations brought back into the recycle schema again, under the names they waited by.</summary>
    /// <remarks>For a restore whose catalogue rows would not go back after its tables had.</remarks>
    public async Task SetAsideAgainAsync(
        IReadOnlyList<(string Schema, string Original, string Recycled, string Kind)> relations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relations);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach ((string schema, string original, string recycled, string kind) in relations)
        {
            RefuseOutsideHosted(schema, original, "set aside");
            string arguments = kind == "function" ? "()" : string.Empty;
            await ExecuteAsync(connection, transaction,
                $"alter {kind} {Qualified(original)}{arguments} set schema {LayerDefinition.Quote(RecycleSchema)}", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                $"alter {kind} {LayerDefinition.Quote(RecycleSchema)}.{LayerDefinition.Quote(original)}{arguments} rename to {LayerDefinition.Quote(recycled)}",
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops set-aside relations for good — an entry that expired or was emptied.</summary>
    public async Task PurgeAsync(
        IReadOnlyList<(string Schema, string Original, string Recycled, string Kind)> relations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relations);

        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Views and tables before functions: a trigger holds its function.
        foreach ((_, _, string recycled, string kind) in relations)
        {
            if (kind != "function")
            {
                await ExecuteAsync(connection, transaction,
                    $"drop {kind} if exists {LayerDefinition.Quote(RecycleSchema)}.{LayerDefinition.Quote(recycled)} cascade",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        foreach ((_, _, string recycled, string kind) in relations)
        {
            if (kind == "function")
            {
                await ExecuteAsync(connection, transaction,
                    $"drop function if exists {LayerDefinition.Quote(RecycleSchema)}.{LayerDefinition.Quote(recycled)}()",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> FunctionExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string function, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("select to_regprocedure(@name) is not null", connection, transaction);
        command.Parameters.AddWithValue("name", Qualified(function) + "()");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
