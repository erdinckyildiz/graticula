using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;
using Npgsql;
using NpgsqlTypes;

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

        sql.Append("select ");

        if (query.Distinct)
        {
            // <b>distinct on the requested fields, with the identity excluded
            // from the comparison.</b> Selecting the identity too would make
            // every row distinct by construction and the parameter a no-op that
            // looked like it worked. ArcGIS requires returnGeometry=false with
            // this for the same reason, and the parser enforces it.
            sql.Append("distinct on (")
               .Append(string.Join(", ", schema.Names.Select(LayerDefinition.Quote)))
               .Append(") ");
        }

        sql.Append(LayerDefinition.Quote(_layer.IdentityColumn));

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
            sql.Append(", st_asbinary(").Append(OutputGeometry(query)).Append(')');
        }
        else
        {
            sql.Append(", null::bytea");
        }

        sql.Append(" from ").Append(_layer.QuotedTable);

        AppendWhere(sql, query);

        AppendOrderAndPaging(sql, query, schema);

        return sql.ToString();
    }

    /// <summary>
    /// The geometry as it should leave the database: reprojected, generalised
    /// and rounded, in that order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is not arbitrary.</b> <c>maxAllowableOffset</c> is specified
    /// in the <em>output</em> reference — ArcGIS says so — so transforming after
    /// simplifying would apply a tolerance measured in degrees to metres, or the
    /// reverse, and be wrong by a factor of a hundred thousand. Rounding last,
    /// because rounding first would give the simplifier a shape the caller never
    /// had.
    /// </para>
    /// <para>
    /// <b>Every step is skipped when it was not asked for</b>, so an ordinary
    /// query still compiles to a bare <c>st_asbinary(geom)</c> and nothing about
    /// the fast path changed.
    /// </para>
    /// </remarks>
    private string OutputGeometry(FeatureQuery query)
    {
        string expression = LayerDefinition.Quote(_layer.GeometryColumn);

        if (query.OutSrid is { } srid && srid != _layer.Srid)
        {
            expression = $"st_transform({expression}, {srid.ToString(CultureInfo.InvariantCulture)})";
        }

        if (query.MaxAllowableOffset is > 0)
        {
            // Preserve-topology, not the plain simplifier: ST_Simplify can turn
            // a polygon into something self-intersecting, and a client asked for
            // a smaller shape rather than an invalid one.
            expression = $"st_simplifypreservetopology({expression}, @tolerance)";
        }

        if (query.Precision is >= 0)
        {
            expression = $"st_reduceprecision({expression}, @grid)";
        }

        return expression;
    }

    /// <summary>Every filter this query carries, joined with <c>and</c>.</summary>
    private void AppendWhere(StringBuilder sql, FeatureQuery query)
    {
        List<string> clauses = [];

        if (query.BoundingBox is not null)
        {
            // && is the index-backed bounding-box overlap operator. This is the
            // pushdown: the index answers it, and rows that fail never leave the
            // database.
            clauses.Add(
                $"{LayerDefinition.Quote(_layer.GeometryColumn)} && st_makeenvelope("
                + $"@minx, @miny, @maxx, @maxy, {_layer.Srid.ToString(CultureInfo.InvariantCulture)})");
        }

        if (query.Spatial is { } spatial)
        {
            clauses.Add(SpatialClause(spatial));
        }

        if (query.ObjectIds.Count > 0)
        {
            // = any(@ids) rather than an interpolated IN list: the ids are
            // values, so they bind, and one parameter carries any number of them
            // without rebuilding the statement for each distinct count.
            clauses.Add($"{LayerDefinition.Quote(_layer.ObjectIdColumn ?? _layer.IdentityColumn)} = any(@ids)");
        }

        if (query.Where is { Sql.Length: > 0 } where)
        {
            // <b>Interpolated, and that is safe here for one reason only.</b>
            // WhereClause rebuilt this string from a parsed expression tree:
            // the identifiers are our column names re-quoted by us, the
            // operators come from a fixed table, and every literal the caller
            // wrote is a bound parameter. None of the caller's text is in it.
            clauses.Add($"({where.Sql})");
        }

        if (clauses.Count > 0)
        {
            sql.Append(" where ").Append(string.Join(" and ", clauses));
        }
    }

    /// <summary>
    /// One of ArcGIS's nine relations, as PostGIS says it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every predicate gets the index operator in front of it.</b> PostGIS's
    /// <c>ST_Contains</c> and friends are index-accelerated only because they
    /// include a <c>&amp;&amp;</c> test internally — except <c>ST_Relate</c>,
    /// which does not, so an explicit one is prepended. Without it a DE-9IM
    /// query is a sequential scan over the whole table computing the most
    /// expensive predicate in the library.
    /// </para>
    /// <para>
    /// <b>A buffered filter buffers the <em>filter</em>, not the features.</b>
    /// <c>ST_DWithin</c> is the index-friendly way to say it and is what a
    /// distance query means; buffering every feature would be correct and
    /// unusable.
    /// </para>
    /// </remarks>
    private string SpatialClause(SpatialFilter spatial)
    {
        string column = LayerDefinition.Quote(_layer.GeometryColumn);

        // <b>The SRID is attached here, in SQL, and it has to be.</b> Plain WKB
        // carries no spatial reference, so a bound filter arrives as SRID 0 and
        // PostGIS refuses the comparison: "Operation on mixed SRID geometries".
        // The && operator does not check, which is the dangerous part — the
        // index-only relations answered happily while every real predicate
        // errored, so a partial implementation looked like a working one.
        string filter =
            $"st_setsrid(st_geomfromwkb(@filter), {_layer.Srid.ToString(CultureInfo.InvariantCulture)})";

        if (spatial.Distance > 0)
        {
            return $"st_dwithin({column}, {filter}, @distance)";
        }

        return spatial.Relation switch
        {
            // The bare index operator: bounding boxes only, which is what both
            // of these mean in a provider whose index is a box index.
            SpatialRelation.EnvelopeIntersects or SpatialRelation.IndexIntersects =>
                $"{column} && {filter}",

            SpatialRelation.Contains => $"{column} && {filter} and st_contains({column}, {filter})",
            SpatialRelation.Within => $"{column} && {filter} and st_within({column}, {filter})",
            SpatialRelation.Crosses => $"{column} && {filter} and st_crosses({column}, {filter})",
            SpatialRelation.Overlaps => $"{column} && {filter} and st_overlaps({column}, {filter})",
            SpatialRelation.Touches => $"{column} && {filter} and st_touches({column}, {filter})",

            // ST_Relate has no built-in index test, so it gets one here or it
            // scans the table.
            SpatialRelation.Relate =>
                $"{column} && {filter} and st_relate({column}, {filter}, @pattern)",

            _ => $"st_intersects({column}, {filter})",
        };
    }

    /// <summary>The order, then the window.</summary>
    private void AppendOrderAndPaging(StringBuilder sql, FeatureQuery query, FeatureSchema schema)
    {
        // <b>distinct on demands that the order start with its own expressions</b>,
        // or PostgreSQL refuses the statement outright. The client's order, if
        // any, follows.
        if (query.Distinct)
        {
            sql.Append(" order by ")
               .Append(string.Join(", ", schema.Names.Select(LayerDefinition.Quote)));

            foreach (GisServer.Features.SortKey key in query.OrderBy)
            {
                sql.Append(", ").Append(LayerDefinition.Quote(key.Field))
                   .Append(key.Descending ? " desc" : " asc");
            }

            sql.Append(" limit @limit");

            if (query.Offset > 0)
            {
                sql.Append(" offset @offset");
            }

            return;
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
    }

    /// <summary>Binds every parameter the built SQL might mention.</summary>
    /// <remarks>
    /// <b>Bound from the query rather than from the SQL text.</b> Npgsql refuses
    /// a parameter the statement does not use, so each of these is added under
    /// exactly the condition that put its placeholder in the statement — the two
    /// lists are the same list, read twice, and keeping them in step is what
    /// this method is for.
    /// </remarks>
    private static void BindFilters(NpgsqlCommand command, FeatureQuery query)
    {
        if (query.BoundingBox is { } box)
        {
            command.Parameters.AddWithValue("minx", box.MinX);
            command.Parameters.AddWithValue("miny", box.MinY);
            command.Parameters.AddWithValue("maxx", box.MaxX);
            command.Parameters.AddWithValue("maxy", box.MaxY);
        }

        if (query.Spatial is { } spatial)
        {
            // Plain WKB, which carries no spatial reference — SpatialClause
            // wraps it in st_setsrid for that reason.
            command.Parameters.AddWithValue(
                "filter", NpgsqlDbType.Bytea, WkbWriter.ToArray(spatial.Geometry));

            if (spatial.Distance > 0)
            {
                command.Parameters.AddWithValue("distance", spatial.Distance);
            }

            if (spatial.Relation == SpatialRelation.Relate)
            {
                command.Parameters.AddWithValue("pattern", spatial.RelatePattern!);
            }
        }

        if (query.ObjectIds.Count > 0)
        {
            command.Parameters.AddWithValue("ids", query.ObjectIds.ToArray<long>());
        }

        if (query.MaxAllowableOffset is { } tolerance and > 0)
        {
            command.Parameters.AddWithValue("tolerance", tolerance);
        }

        if (query.Precision is { } places and >= 0)
        {
            // ST_ReducePrecision takes a grid size, not a digit count.
            command.Parameters.AddWithValue("grid", Math.Pow(10, -places));
        }

        if (query.Where is { Sql.Length: > 0 } where)
        {
            for (int i = 0; i < where.Parameters.Count; i++)
            {
                command.Parameters.AddWithValue($"w{i}", where.Parameters[i] ?? DBNull.Value);
            }
        }
    }

    private static void Bind(NpgsqlCommand command, FeatureQuery query)
    {
        command.Parameters.AddWithValue("limit", query.Limit);

        if (query.Offset > 0)
        {
            command.Parameters.AddWithValue("offset", query.Offset);
        }

        BindFilters(command, query);
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The same filter as a read, and deliberately not the same limit: a
        // client asking how many features match wants the total, not the size of
        // the page it would get.
        // <b>Through the same AppendWhere as a read.</b> It used to build its
        // own bounding-box clause, which was identical and therefore fine — and
        // stopped being fine the moment a query could also carry object ids, a
        // relate pattern or a distance. A count that ignores half the filter is
        // worse than no count: it is a number the client believes.
        StringBuilder sql = new("select count(*) from ");
        sql.Append(_layer.QuotedTable);

        AppendWhere(sql, query);

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql.ToString());
        BindFilters(command, query);

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>The extent of everything this query matches, and how many.</summary>
    /// <param name="query">The query, whose filters apply.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The extent, or null when nothing matched, and the count.</returns>
    /// <remarks>
    /// <b>One statement, because the two numbers must describe the same set.</b>
    /// Asking separately leaves a window in which an edit lands between them,
    /// and the client is told the extent of one answer and the size of another.
    /// </remarks>
    public async Task<(Envelope? Extent, long Count)> ExtentAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        StringBuilder sql = new("select st_xmin(e), st_ymin(e), st_xmax(e), st_ymax(e), n from ("
            + "select st_extent(");

        sql.Append(OutputGeometry(query)).Append(") as e, count(*) as n from ")
           .Append(_layer.QuotedTable);

        AppendWhere(sql, query);

        sql.Append(") s");

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql.ToString());
        BindFilters(command, query);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, 0);
        }

        long count = reader.GetInt64(4);

        // st_extent over an empty set is null, and so is the extent of a set
        // whose every geometry is null. Both are "no extent", which is a
        // different answer from a zero-sized box at the origin.
        if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return (null, count);
        }

        return (
            new Envelope(
                reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)),
            count);
    }

    /// <summary>The object ids of everything this query matches.</summary>
    /// <param name="query">The query, whose filters and order apply.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The ids, in the query's order.</returns>
    /// <remarks>
    /// <b>Not capped by the query's limit, and that is ArcGIS's behaviour.</b>
    /// <c>returnIdsOnly</c> exists so a client can learn the whole answer set
    /// cheaply and then fetch it in pages; truncating it to a page would defeat
    /// the only reason to ask. An id is eight bytes, so a million of them is
    /// eight megabytes — bounded by the table, not by a user-supplied number.
    /// </remarks>
    public async Task<IReadOnlyList<long>> ObjectIdsAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        string column = LayerDefinition.Quote(_layer.ObjectIdColumn ?? _layer.IdentityColumn);

        StringBuilder sql = new("select ");
        sql.Append(column).Append(" from ").Append(_layer.QuotedTable);

        AppendWhere(sql, query);

        sql.Append(" order by ").Append(column);

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql.ToString());
        BindFilters(command, query);

        List<long> ids = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>
    /// Computes the requested aggregates, grouped as asked.
    /// </summary>
    /// <param name="query">The query, carrying statistics, grouping and having.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One row per group, each a name-to-value map.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every identifier here came from the caller, and none of it is
    /// interpolated unchecked.</b> Field names were matched against the layer's
    /// real columns before the query was built, and output names against the
    /// same identifier rule — ADR-008 §4.6's two-step. The statistic itself is
    /// an enum, so there is no path by which a caller's text becomes a SQL
    /// function name.
    /// </para>
    /// <para>
    /// <b>The having clause is the exception, and it is passed through.</b>
    /// ArcGIS defines it as SQL over aggregate functions — <c>COUNT(id) &gt; 5</c>
    /// — which cannot be expressed as anything but text. It is subject to the
    /// same treatment as <c>where</c>, which this server also passes through, so
    /// it is no wider a door; it is the same door.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StatisticsAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<string> select = [];

        foreach (string group in query.GroupBy)
        {
            select.Add(LayerDefinition.Quote(group));
        }

        foreach (StatisticRequest statistic in query.Statistics)
        {
            select.Add(
                $"{Function(statistic.Kind)}({LayerDefinition.Quote(statistic.Field)}) as "
                + LayerDefinition.Quote(statistic.OutName));
        }

        StringBuilder sql = new("select ");
        sql.Append(string.Join(", ", select)).Append(" from ").Append(_layer.QuotedTable);

        AppendWhere(sql, query);

        if (query.GroupBy.Count > 0)
        {
            sql.Append(" group by ")
               .Append(string.Join(", ", query.GroupBy.Select(LayerDefinition.Quote)));
        }

        if (!string.IsNullOrWhiteSpace(query.Having))
        {
            sql.Append(" having ").Append(query.Having);
        }

        if (query.GroupBy.Count > 0)
        {
            sql.Append(" order by ")
               .Append(string.Join(", ", query.GroupBy.Select(LayerDefinition.Quote)));

            // A grouped statistic can produce as many rows as there are distinct
            // values, which is unbounded. The query's own limit applies.
            sql.Append(" limit @limit");
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(sql.ToString());
        command.Parameters.AddWithValue("limit", query.Limit);
        BindFilters(command, query);

        List<IReadOnlyDictionary<string, object?>> rows = [];

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, object?> row = [];

            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] =
                    await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                        ? null
                        : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The SQL aggregate for a statistic kind.</summary>
    /// <remarks>
    /// <b>A switch over an enum, so no caller text reaches here.</b> Mapping
    /// from the wire string straight to a function name would be one typo away
    /// from letting a client name any function PostgreSQL has.
    /// </remarks>
    private static string Function(StatisticKind kind) => kind switch
    {
        StatisticKind.Count => "count",
        StatisticKind.Sum => "sum",
        StatisticKind.Min => "min",
        StatisticKind.Max => "max",
        StatisticKind.Avg => "avg",
        StatisticKind.StdDev => "stddev_samp",
        StatisticKind.Var => "var_samp",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

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
