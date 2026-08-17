using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// Reads and writes declared relationships.
/// </summary>
/// <remarks>
/// Small enough to be one class. The interesting part of relationships is not
/// their storage — it is the validation that happens before a declaration is
/// accepted, and the join that happens when one is queried, and both live where
/// the layers' own databases can be reached.
/// </remarks>
public sealed class PostgresRelationshipCatalog
{
    private const string Columns =
        "id, name, origin_layer_id, origin_key, related_layer_id, related_key, "
        + "cardinality, composite";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the catalogue.</summary>
    /// <param name="dataSource">The platform store.</param>
    public PostgresRelationshipCatalog(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>Declares a relationship.</summary>
    /// <param name="relationship">What to declare. Its id is ignored.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The new id.</returns>
    public async Task<Guid> DeclareAsync(
        LayerRelationship relationship, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        Guid id = Guid.NewGuid();

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            """
            insert into relationship
              (id, name, origin_layer_id, origin_key, related_layer_id, related_key,
               cardinality, composite)
            values (@id, @name, @origin, @originKey, @related, @relatedKey, @cardinality, @composite)
            """);

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", relationship.Name);
        command.Parameters.AddWithValue("origin", relationship.OriginLayerId);
        command.Parameters.AddWithValue("originKey", relationship.OriginKey);
        command.Parameters.AddWithValue("related", relationship.RelatedLayerId);
        command.Parameters.AddWithValue("relatedKey", relationship.RelatedKey);
        command.Parameters.AddWithValue("cardinality", relationship.Cardinality.ToString());
        command.Parameters.AddWithValue("composite", relationship.Composite);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Every relationship a layer takes part in, from either side.
    /// </summary>
    /// <param name="layerId">The layer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The relationships.</returns>
    /// <remarks>
    /// <b>Both sides, because a client asks from both.</b> ArcGIS reports a
    /// relationship on each participating layer with a role — a parcel's layer
    /// document lists its owners and the owners' document lists its parcels —
    /// and returning only the ones where this layer is the origin makes half of
    /// them invisible.
    /// </remarks>
    public async Task<IReadOnlyList<LayerRelationship>> ForLayerAsync(
        Guid layerId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"""
             select {Columns} from relationship
             where origin_layer_id = @layer or related_layer_id = @layer
             order by name
             """);

        command.Parameters.AddWithValue("layer", layerId);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every relationship, for the admin listing.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The relationships.</returns>
    public async Task<IReadOnlyList<LayerRelationship>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            _dataSource.CreateCommand($"select {Columns} from relationship order by name");

        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One relationship by id, or null.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The relationship, or null.</returns>
    public async Task<LayerRelationship?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            _dataSource.CreateCommand($"select {Columns} from relationship where id = @id");

        command.Parameters.AddWithValue("id", id);

        IReadOnlyList<LayerRelationship> found =
            await ReadAsync(command, cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <summary>Removes a relationship.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it existed.</returns>
    /// <remarks>
    /// <b>Only the declaration goes.</b> No data is touched — a relationship is
    /// metadata about two tables, and removing it says *stop reporting this*,
    /// never *delete these rows*. That distinction is worth stating because
    /// composite relationships do cascade on delete, and somebody could
    /// reasonably fear this did the same.
    /// </remarks>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command =
            _dataSource.CreateCommand("delete from relationship where id = @id");

        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static async Task<IReadOnlyList<LayerRelationship>> ReadAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<LayerRelationship> relationships = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            relationships.Add(new LayerRelationship(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetGuid(4),
                reader.GetString(5),
                Enum.Parse<RelationshipCardinality>(reader.GetString(6)),
                reader.GetBoolean(7)));
        }

        return relationships;
    }
}
