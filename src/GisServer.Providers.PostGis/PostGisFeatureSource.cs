using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;
using Npgsql;

namespace GisServer.Providers.PostGis;

/// <summary>
/// Reads features from a PostGIS table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bounding box is pushed down, and that is the whole point.</b>
/// ADR-003 §6a puts provider pushdown above our own primitives and above the
/// adopted engine, because <c>benchmarks/mvt-generation</c> finding 11 measured a
/// z16 tile reading <b>201,580 vertices to emit 2,080</b> — four administrative
/// polygons overlap every tile in the city, so a tile's cost floor is set by the
/// largest geometry near it rather than by its content. The governing rule is
/// that <em>the cheapest geometry operation is the one that never crosses the
/// wire</em>.
/// </para>
/// <para>
/// <b>Rows stream.</b> Nothing is materialised, because A-037 measured
/// allocation as the binding constraint and a list of features is a second copy
/// of everything for no benefit.
/// </para>
/// <para>
/// <b>Clipping is not done here.</b> A feature query returns whole geometries —
/// the caller asked for features intersecting a box, and a national outline that
/// intersects it genuinely does. Q-56's three tiers govern the oversized case,
/// and clipping belongs to the tile path where the output is a picture.
/// </para>
/// </remarks>
public sealed class PostGisFeatureSource : IFeatureSource
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly LayerDefinition _layer;

    /// <summary>Creates a feature source over one layer.</summary>
    public PostGisFeatureSource(NpgsqlDataSource dataSource, LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(layer);

        _dataSource = dataSource;
        _layer = layer;
    }

    /// <inheritdoc/>
    public FeatureSchema SchemaFor(FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Fields.Count == 0 ? FeatureSchema.Empty : new FeatureSchema(query.Fields);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Feature> ReadAsync(
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        FeatureSchema schema = SchemaFor(query);

        await using NpgsqlCommand command = _dataSource.CreateCommand(BuildSql(query, schema));
        Bind(command, query);

        // SequentialAccess: read each column once, in order, without buffering
        // the whole row. Geometry is the last column and the large one, so this
        // is the difference between holding one row and holding one geometry.
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Checked here rather than left to the driver. Rows already in the
            // receive buffer complete synchronously, so ReadAsync never reaches
            // an await and never observes the token — a client that disconnects
            // mid-stream would keep us working through whatever had arrived.
            // ADR-007 §4.9 says a disconnect cancels the query, and that has to
            // be true of the enumerator rather than only of the network wait.
            cancellationToken.ThrowIfCancellationRequested();

            // IsDBNull, not a null check on the value: the driver returns
            // DBNull.Value here, whose ToString() is the empty string — so a
            // null identity would have reached Feature's constructor and failed
            // there with "value cannot be an empty string", which tells an
            // operator nothing about which column is at fault. Caught by test.
            if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Layer '{_layer.Name}' has a row whose declared identity column "
                    + $"'{_layer.IdentityColumn}' is null. Identity is declared rather than "
                    + "inferred (Q-57), so a null there means the registration named the wrong "
                    + "column — guessing an identity would hide that.");
            }

            string id = reader.GetValue(0).ToString()!;

            object?[] values = new object?[schema.Count];
            for (int i = 0; i < schema.Count; i++)
            {
                int ordinal = i + 1;
                values[i] = await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(ordinal);
            }

            int geometryOrdinal = schema.Count + 1;
            Geometry? geometry = null;

            if (!await reader.IsDBNullAsync(geometryOrdinal, cancellationToken).ConfigureAwait(false))
            {
                // A row with no shape is allowed; dropping it would quietly
                // change the answer to a count.
                byte[] wkb = (byte[])reader.GetValue(geometryOrdinal);
                geometry = WkbReader.Read(wkb);
            }

            yield return new Feature(id, geometry, schema, values);
        }
    }

    private string BuildSql(FeatureQuery query, FeatureSchema schema)
    {
        // Every identifier below was validated by LayerDefinition's constructor
        // and is quoted here as well. Identifiers cannot be bound as parameters,
        // so this is the one place interpolation is unavoidable — the two checks
        // together are what make it provably safe rather than carefully written.
        StringBuilder sql = new();

        sql.Append("select ").Append(LayerDefinition.Quote(_layer.IdentityColumn));

        foreach (string field in schema.Names)
        {
            sql.Append(", ").Append(LayerDefinition.Quote(field));
        }

        // ST_AsBinary explicitly rather than relying on the driver's type
        // handling: WkbReader is verified against exactly this output, and a
        // silent switch to EWKB or to a driver-materialised geometry would move
        // the read path out from under its tests.
        sql.Append(", st_asbinary(").Append(LayerDefinition.Quote(_layer.GeometryColumn)).Append(')');

        sql.Append(" from ").Append(_layer.QuotedTable);

        if (query.BoundingBox is not null)
        {
            // && is the index-backed bounding-box overlap operator. This is the
            // pushdown: the index answers it, and rows that fail never leave the
            // database.
            sql.Append(" where ")
               .Append(LayerDefinition.Quote(_layer.GeometryColumn))
               .Append(" && st_makeenvelope(@minx, @miny, @maxx, @maxy, ")
               .Append(_layer.Srid.ToString(CultureInfo.InvariantCulture))
               .Append(')');
        }

        sql.Append(" limit @limit");

        return sql.ToString();
    }

    private static void Bind(NpgsqlCommand command, FeatureQuery query)
    {
        command.Parameters.AddWithValue("limit", query.Limit);

        if (query.BoundingBox is Envelope box)
        {
            command.Parameters.AddWithValue("minx", box.MinX);
            command.Parameters.AddWithValue("miny", box.MinY);
            command.Parameters.AddWithValue("maxx", box.MaxX);
            command.Parameters.AddWithValue("maxy", box.MaxY);
        }
    }
}
