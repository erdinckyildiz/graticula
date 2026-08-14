using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        //
        // Omitted entirely when the client did not ask for a shape. For a layer
        // of large polygons that is the difference between an attribute table
        // and a download, and it is the single cheapest thing a client can do
        // to make a grid view fast.
        if (query.IncludeGeometry)
        {
            sql.Append(", st_asbinary(")
               .Append(LayerDefinition.Quote(_layer.GeometryColumn))
               .Append(')');
        }
        else
        {
            sql.Append(", null::bytea");
        }

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

        // <b>The client's order if it gave one, identity if it is paging, and
        // nothing otherwise.</b> Ordering costs a sort or an index scan that an
        // unordered read avoids, so it is not imposed on a plain first page —
        // but an offset without an order is meaningless, because the provider
        // may return rows in any order and page two can then repeat page one.
        if (query.OrderBy.Count > 0)
        {
            sql.Append(" order by ");

            for (int i = 0; i < query.OrderBy.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                // The field was checked against the layer's real columns before
                // it reached here, and is quoted as well — the same two-step
                // that makes the select list safe (ADR-008 §4.6).
                sql.Append(LayerDefinition.Quote(query.OrderBy[i].Field))
                   .Append(query.OrderBy[i].Descending ? " desc" : " asc");
            }
        }
        else if (query.Offset > 0)
        {
            sql.Append(" order by ").Append(LayerDefinition.Quote(_layer.IdentityColumn));
        }

        sql.Append(" limit @limit");

        if (query.Offset > 0)
        {
            sql.Append(" offset @offset");
        }

        return sql.ToString();
    }

    private static void Bind(NpgsqlCommand command, FeatureQuery query)
    {
        command.Parameters.AddWithValue("limit", query.Limit);

        if (query.Offset > 0)
        {
            command.Parameters.AddWithValue("offset", query.Offset);
        }

        if (query.BoundingBox is Envelope box)
        {
            command.Parameters.AddWithValue("minx", box.MinX);
            command.Parameters.AddWithValue("miny", box.MinY);
            command.Parameters.AddWithValue("maxx", box.MaxX);
            command.Parameters.AddWithValue("maxy", box.MaxY);
        }
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The same filter as a read, and deliberately not the same limit: a
        // client asking how many features match wants the total, not the size of
        // the page it would get.
        StringBuilder sql = new("select count(*) from ");
        sql.Append(_layer.QuotedTable);

        if (query.BoundingBox is not null)
        {
            sql.Append(" where ")
               .Append(LayerDefinition.Quote(_layer.GeometryColumn))
               .Append(" && st_makeenvelope(@minx, @miny, @maxx, @maxy, ")
               .Append(_layer.Srid.ToString(CultureInfo.InvariantCulture))
               .Append(')');
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql.ToString());

        if (query.BoundingBox is Envelope box)
        {
            command.Parameters.AddWithValue("minx", box.MinX);
            command.Parameters.AddWithValue("miny", box.MinY);
            command.Parameters.AddWithValue("maxx", box.MaxX);
            command.Parameters.AddWithValue("maxy", box.MaxY);
        }

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc/>
    public async Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken)
    {
        return new LayerDescription(
            await ReadFieldsAsync(cancellationToken).ConfigureAwait(false),
            await ReadExtentAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The attribute columns, geometry excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from <c>information_schema.columns</c>, which is privilege-filtered
    /// — so it shows what this credential may actually see, not what exists.
    /// That is the honest answer for a capability report.
    /// </para>
    /// <para>
    /// <b>The geometry column is excluded because it is the shape, not a
    /// field.</b> An ArcGIS client that finds <c>way</c> in the field list will
    /// offer to label features with WKB.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<FieldDescription>> ReadFieldsAsync(
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select column_name, udt_name, is_nullable, character_maximum_length
            from information_schema.columns
            where table_schema = @schema and table_name = @table
              and column_name <> @geometry
            order by ordinal_position
            """;

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("schema", _layer.SchemaName);
        command.Parameters.AddWithValue("table", _layer.TableName);
        command.Parameters.AddWithValue("geometry", _layer.GeometryColumn);

        List<FieldDescription> fields = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string type = reader.GetString(1);

            // A second geometry or geography column is not an attribute either.
            // Rare, and the failure is the same one the doc comment describes.
            if (type is "geometry" or "geography")
            {
                continue;
            }

            fields.Add(new FieldDescription(
                reader.GetString(0),
                MapType(type),
                string.Equals(reader.GetString(2), "YES", StringComparison.Ordinal),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return fields;
    }

    /// <summary>
    /// Where the features are, from statistics where possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>ST_EstimatedExtent</c> first, and it is the right default.</b> It
    /// reads the planner's statistics and returns instantly; <c>ST_Extent</c>
    /// reads every geometry, which on this project's 6.5-million-row corpus is
    /// not something to do while somebody waits for a layer to appear.
    /// </para>
    /// <para>
    /// It returns null when the table has never been analysed, and for a view it
    /// does not apply at all — so there is a fallback, and the fallback is
    /// bounded rather than exact: it reads the extent of a sample. An extent
    /// that is slightly small costs a pan. An extent that costs a table scan
    /// costs the layer.
    /// </para>
    /// </remarks>
    private async Task<Envelope?> ReadExtentAsync(CancellationToken cancellationToken)
    {
        Envelope? estimated = await TryExtentAsync(
            $"select st_estimatedextent({Literal(_layer.SchemaName)}, {Literal(_layer.TableName)}, "
            + $"{Literal(_layer.GeometryColumn)})::text",
            cancellationToken).ConfigureAwait(false);

        if (estimated is not null)
        {
            return estimated;
        }

        return await TryExtentAsync(
            $"select st_extent(g)::text from (select {LayerDefinition.Quote(_layer.GeometryColumn)} "
            + $"as g from {_layer.QuotedTable} limit 10000) s",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Envelope?> TryExtentAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlCommand command = _dataSource.CreateCommand(sql);

            object? value =
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            // <b>Cast to text in SQL, not read as a type.</b> Npgsql has no
            // mapping for PostGIS's box2d, so reading it directly yields
            // something this code silently failed to recognise — the extent came
            // back null while the same query in psql returned a box. Adding a
            // type mapping to read four numbers would be more machinery than the
            // numbers are worth; ::text makes the contract explicit.
            return value is string text ? ParseBox(text) : null;
        }
        catch (PostgresException)
        {
            // A view, a missing statistic, or no privilege on the statistics.
            // Unknown is a legitimate answer here — LayerDescription says so —
            // and it must not take down a metadata request.
            return null;
        }
    }

    private static Envelope? ParseBox(string box)
    {
        int open = box.IndexOf('(', StringComparison.Ordinal);
        int close = box.IndexOf(')', StringComparison.Ordinal);

        if (open < 0 || close < open)
        {
            return null;
        }

        string[] corners = box[(open + 1)..close].Split(',');

        if (corners.Length != 2)
        {
            return null;
        }

        string[] low = corners[0].Split(' ');
        string[] high = corners[1].Split(' ');

        if (low.Length != 2 || high.Length != 2
            || !double.TryParse(low[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double minX)
            || !double.TryParse(low[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double minY)
            || !double.TryParse(high[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double maxX)
            || !double.TryParse(high[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double maxY))
        {
            return null;
        }

        return new Envelope(minX, minY, maxX, maxY);
    }

    /// <summary>A single-quoted SQL literal.</summary>
    /// <remarks>
    /// <c>st_estimatedextent</c> takes its schema, table and column as text
    /// arguments rather than identifiers, and the values come from
    /// <see cref="LayerDefinition"/>, which has already validated them against
    /// an identifier pattern. The doubling is belt and braces.
    /// </remarks>
    private static string Literal(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// PostgreSQL's type names, mapped into ours.
    /// </summary>
    /// <remarks>
    /// The one place a provider type name is allowed to appear. Anything
    /// unrecognised becomes <see cref="FieldType.Unknown"/>, which surfaces are
    /// expected to render as text — an unfamiliar type is a reason to be
    /// cautious, never a reason to fail a metadata request.
    /// </remarks>
    private static FieldType MapType(string udtName) => udtName switch
    {
        "int2" => FieldType.SmallInteger,
        "int4" => FieldType.Integer,
        "int8" => FieldType.BigInteger,
        "float4" => FieldType.Single,
        "float8" or "numeric" => FieldType.Double,
        "bool" => FieldType.Boolean,
        "uuid" => FieldType.Guid,
        "bytea" => FieldType.Binary,
        "date" or "timestamp" or "timestamptz" or "time" or "timetz" => FieldType.Date,
        "text" or "varchar" or "bpchar" or "name" or "citext" => FieldType.Text,
        _ => FieldType.Unknown,
    };
}
