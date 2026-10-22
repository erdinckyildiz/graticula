using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Microsoft.Data.SqlClient;
using SortKey = Graticula.Features.SortKey;

namespace Graticula.Providers.SqlServer;

/// <summary>A layer served from one spatial table of a registered SQL Server database — ADR-210.</summary>
/// <remarks>
/// <para>
/// <b>MySqlFeatureSource's shape, in T-SQL.</b> A spatial filter is two passes: SQL Server is asked which rows' shapes
/// meet the filter's box, and <see cref="GeometryPredicates.Intersects"/> — or the geometry engine, for every other
/// relation and a distance (<see cref="EngineRelations"/>) — decides here which shapes meet the filter. Deciding here is
/// what keeps a SQL Server layer's answer a PostGIS layer's.
/// </para>
/// <para>
/// <b>A <c>geography</c> column is read as planar coordinates.</b> SQL Server draws a <c>geography</c> polygon's edges as
/// great-circle arcs, so a box asked of it is not the box a client drew — MongoDB's <c>2dsphere</c> lost 8,481 of 200,000
/// points that way (other-databases-trial.md). Its shapes are taken as WKB, longitude first, and boxed as a
/// <c>geometry</c> of SRID 0: exact, and without the spatial index, which a <c>geometry</c> column keeps.
/// </para>
/// <para>
/// <b>Geometry crosses as WKB in longitude–latitude order</b> (<c>STAsBinary</c>), two-dimensional: a layer here has no
/// Z or M.
/// </para>
/// </remarks>
public sealed class SqlServerFeatureSource : IFeatureSource, IFeatureSummaries, IFeatureVersions
{
    private const int ProjectionBatch = 512;

    /// <summary>How long one query may run when the service says nothing shorter — the PostGIS path's thirty seconds.</summary>
    public static readonly TimeSpan DefaultStatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The most features a filter may match before it is refused — GeoParquet's bound, for its reason.</summary>
    public const int DefaultMostMatched = 1_000_000;

    /// <summary>The most vertices a spatial filter's geometry may have — GeometryServer's cap.</summary>
    public const int MostFilterVertices = 130_000;

    private static readonly TimeSpan ColumnsKept = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, SqlServerTable Table)> Tables = new(StringComparer.Ordinal);

    private readonly string _connectionString;
    private readonly string _tableKey;
    private readonly LayerDefinition _layer;
    private readonly IProjector _projector;
    private readonly TimeSpan _timeout;
    private readonly int _mostMatched;
    private readonly IGeometryEngine? _engine;

    /// <summary>Serves one table of a registered SQL Server database as a layer.</summary>
    /// <param name="locator">The data source's <c>sqlserver:</c> locator.</param>
    /// <param name="layer">The layer; its schema is the SQL Server schema (<c>dbo</c>, most often).</param>
    /// <param name="projector">The datastore's projector.</param>
    /// <param name="statementTimeout">How long one query may run; <see cref="DefaultStatementTimeout"/> when null.</param>
    /// <param name="engine">The geometry engine, which decides a distance and every relation beyond intersection.</param>
    public SqlServerFeatureSource(
        string locator, LayerDefinition layer, IProjector projector, TimeSpan? statementTimeout = null,
        IGeometryEngine? engine = null)
        : this(locator, layer, projector, statementTimeout, DefaultMostMatched, engine)
    {
    }

    internal SqlServerFeatureSource(
        string locator, LayerDefinition layer, IProjector projector, TimeSpan? statementTimeout, int mostMatched,
        IGeometryEngine? engine = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mostMatched);

        if (statementTimeout is { } given && given <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(statementTimeout), "A statement timeout must be positive.");
        }

        _connectionString = SqlServerConnections.For(locator, pooling: true);
        _tableKey = SqlServerLocator.DatabaseKey(locator) + "|" + layer.SchemaName + "." + layer.TableName + "." + layer.GeometryColumn;
        _layer = layer;
        _projector = projector;
        _timeout = statementTimeout ?? DefaultStatementTimeout;
        _mostMatched = mostMatched;
        _engine = engine;
    }

    /// <summary>
    /// Forgets the columns read for one table, so the next read sees columns added since — ADR-212, after the editor-tracking
    /// columns are added. The cache is static and keyed by database and table, so every source of the layer reads again.
    /// </summary>
    /// <param name="locator">The data source's locator.</param>
    /// <param name="layer">The layer.</param>
    public static void Forget(string locator, LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        Tables.TryRemove(SqlServerLocator.DatabaseKey(locator) + "|" + layer.SchemaName + "." + layer.TableName + "." + layer.GeometryColumn, out _);
    }

    /// <summary>The relations a SQL Server layer answers itself; the rest are the geometry engine's (EngineRelations).</summary>
    public static IReadOnlyList<SpatialRelation> Relations { get; } =
        [SpatialRelation.Intersects, SpatialRelation.EnvelopeIntersects, SpatialRelation.IndexIntersects];

    private string Id => "t." + LayerDefinition.Quote(_layer.IdentityColumn);

    private string Shape => "t." + LayerDefinition.Quote(_layer.RequireGeometryColumn());

    private string From => _layer.QuotedTable;

    /// <summary>A shape as WKB in longitude–latitude order, two-dimensional.</summary>
    // A table answers every row without a shape (ADR-213), as the PostGIS source does.
    private string Wkb => _layer.IsTable ? "cast(null as varbinary(max))" : Shape + ".STAsBinary()";

    /// <summary>
    /// An ORDER BY key where nulls fall as PostgreSQL's — last ascending, first descending — rather than SQL Server's,
    /// which puts them first ascending; only where the column can hold one (MySqlFeatureSource.Ordered, for its reason).
    /// </summary>
    private static string Ordered(string expression, bool descending, bool nullable = true) =>
        !nullable ? $"{expression} {(descending ? "desc" : "asc")}"
        : descending
            ? $"case when {expression} is null then 0 else 1 end, {expression} desc"
            : $"case when {expression} is null then 1 else 0 end, {expression} asc";

    /// <summary>Whether a column of the table may hold a null; the identity never does, and an unknown name may.</summary>
    private bool Nullable(SqlServerTable table, string field) =>
        !string.Equals(field, _layer.IdentityColumn, StringComparison.OrdinalIgnoreCase)
        && (table.Columns.FirstOrDefault(c => string.Equals(c.Name, field, StringComparison.OrdinalIgnoreCase)) is not { } column
            || column.Nullable);

    /// <inheritdoc />
    public FeatureSchema SchemaFor(FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return query.Fields.Count == 0 ? FeatureSchema.Empty : new FeatureSchema(query.Fields);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Feature> ReadAsync(FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SchemaFor(query);

        using CancellationTokenSource deadline = Deadline(cancellationToken);

        await using IAsyncEnumerator<Feature> rows = ReadBoundedAsync(query, deadline.Token).GetAsyncEnumerator(deadline.Token);

        while (true)
        {
            bool more;

            try
            {
                more = await rows.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception stopped) when (TimedOut(stopped, deadline, cancellationToken))
            {
                throw Timeout();
            }

            if (!more)
            {
                yield break;
            }

            yield return rows.Current;
        }
    }

    private async IAsyncEnumerable<Feature> ReadBoundedAsync(FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FeatureSchema schema = SchemaFor(query);

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SqlServerTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);

        Statement statement = new(filters);
        StringBuilder sql = statement.Sql;
        bool distinct = query.Distinct && schema.Count > 0;

        string selected = string.Concat(schema.Names.Select(
            (field, i) => $", t.{LayerDefinition.Quote(field)} as c{i.ToString(CultureInfo.InvariantCulture)}"));

        if (distinct)
        {
            sql.Append("select min(").Append(Id).Append(") as i").Append(selected).Append(", null as g from ").Append(From).Append(" t");
            statement.AppendWhere();
            sql.Append(" group by ").Append(string.Join(", ", schema.Names.Distinct(StringComparer.Ordinal).Select(n => "t." + LayerDefinition.Quote(n))));
        }
        else
        {
            sql.Append("select ").Append(Id).Append(" as i").Append(selected);
            sql.Append(query.IncludeGeometry || query.ReturnCentroid ? $", {Wkb} as g" : ", null as g");
            sql.Append(" from ").Append(From).Append(" t");
            statement.AppendWhere();
        }

        AppendOrderAndPaging(statement, query, schema, distinct, table);

        FieldType[] types = [.. schema.Names.Select(table.TypeOf)];
        List<Feature> batch = new(ProjectionBatch);
        bool reshape = (query.IncludeGeometry || query.ReturnCentroid) && !distinct
            && (Projects(query) || query.Precision is >= 0 || query.MaxAllowableOffset is > 0);

        await using SqlCommand command = statement.Command(connection, _timeout);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                throw new InvalidOperationException(
                    $"Layer '{_layer.Name}' has a row whose identity column '{_layer.IdentityColumn}' is null, and it "
                    + "is the table's primary key.");
            }

            string id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            object?[] values = new object?[schema.Count];

            for (int i = 0; i < schema.Count; i++)
            {
                values[i] = reader.IsDBNull(i + 1) ? null : Normalise(reader.GetValue(i + 1), types[i]);
            }

            int geometryOrdinal = schema.Count + 1;
            Geometry? geometry = reader.IsDBNull(geometryOrdinal) ? null : WkbReader.Read((byte[])reader.GetValue(geometryOrdinal));
            Feature feature = new(id, geometry, schema, values);

            if (!reshape)
            {
                yield return Finished(feature, query);
                continue;
            }

            batch.Add(feature);

            if (batch.Count == ProjectionBatch)
            {
                foreach (Feature done in await ReshapeAsync(batch, query, cancellationToken).ConfigureAwait(false))
                {
                    yield return Finished(done, query);
                }

                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            foreach (Feature done in await ReshapeAsync(batch, query, cancellationToken).ConfigureAwait(false))
            {
                yield return Finished(done, query);
            }
        }
    }

    /// <summary>A feature with its centroid when one was asked for — OracleFeatureSource's rule, for its reason.</summary>
    private static Feature Finished(Feature feature, FeatureQuery query)
    {
        if (!query.ReturnCentroid)
        {
            return feature;
        }

        Geometry? centroid = feature.Geometry switch
        {
            null or { IsEmpty: true } => null,
            Polygon or MultiPolygon => GeometryMeasures.LabelPoint(feature.Geometry),
            Point point => point,
            MultiPoint points => points.Parts.Count > 0 ? points.Parts[0] : null,
            _ => null,
        };

        object?[] values = new object?[feature.Schema.Count];

        for (int v = 0; v < values.Length; v++)
        {
            values[v] = feature[v];
        }

        return new Feature(feature.Id, query.IncludeGeometry ? feature.Geometry : null, feature.Schema, values)
        {
            Centroid = centroid,
        };
    }

    /// <inheritdoc />
    public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) => Bounded(DescribeBoundedAsync, cancellationToken);

    private async Task<LayerDescription> DescribeBoundedAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SqlServerTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);

        List<FieldDescription> fields = [.. table.Columns.Select(c => new FieldDescription(
            c.Name, c.Type, c.Nullable && !string.Equals(c.Name, _layer.IdentityColumn, StringComparison.Ordinal), c.MaxLength))];

        bool writable = await SqlServerConnections.CanWriteAsync(connection, _layer.SchemaName, _layer.TableName, cancellationToken).ConfigureAwait(false);

        Envelope? extent = _layer.IsTable ? null : await ExtentOfTableAsync(connection, table, cancellationToken).ConfigureAwait(false);

        return new LayerDescription(fields, extent, writable)
        {
            AnswersDistance = _engine is not null,
            AnswersHaving = true,
            AnswersTopFeatures = true,
            AnswersCentroid = true,
            AnswersStatisticExpressions = true,
            AnswersRelations = _engine is not null,
            SpatiallyIndexed = _layer.IsTable ? null : !table.Geography,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>SQL Server keeps no row version unless a table has a <c>rowversion</c> column</b>, so a row's version is a hash of
    /// what it holds — MySQL's rule (ADR-208 §5.5), the same expression the writer's precondition compares.
    /// </remarks>
    public Task<string?> VersionOfAsync(long identity, CancellationToken cancellationToken) =>
        Bounded(async token =>
        {
            await using SqlConnection connection = await OpenAsync(token).ConfigureAwait(false);
            SqlServerTable table = await TableAsync(connection, token).ConfigureAwait(false);

            await using SqlCommand command = new(
                $"select {SqlServerFeatureWriter.VersionSqlOf(table.Columns.Select(c => c.Name), _layer.GeometryColumn, "t")} from {From} t where {Id} = @id",
                connection);
            command.Parameters.AddWithValue("@id", identity);

            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        }, cancellationToken);

    /// <summary>The table's extent, from <c>EnvelopeAggregate</c> over its shapes as planar ones.</summary>
    private async Task<Envelope?> ExtentOfTableAsync(SqlConnection connection, SqlServerTable table, CancellationToken cancellationToken)
    {
        await using SqlCommand command = new(
            $"select geometry::EnvelopeAggregate({Planar(table)}).STAsBinary() from {From} t where {Shape} is not null", connection)
        {
            CommandTimeout = (int)Math.Ceiling(_timeout.TotalSeconds),
        };

        try
        {
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] wkb
                && WkbReader.Read(wkb) is { IsEmpty: false } envelope
                    ? envelope.Envelope
                    : null;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>A row's shape as a planar <c>geometry</c>: itself, or a <c>geography</c>'s coordinates under SRID 0.</summary>
    private string Planar(SqlServerTable table) => table.Geography ? $"geometry::STGeomFromWKB({Wkb}, 0)" : Shape;

    /// <inheritdoc />
    public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => CountBoundedAsync(query, null, token), cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountUpToAsync(FeatureQuery query, long ceiling, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);
        Refuse(query);
        return Bounded(token => CountBoundedAsync(query, ceiling, token), cancellationToken);
    }

    private async Task<long> CountBoundedAsync(FeatureQuery query, long? ceiling, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SqlServerTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        bool distinct = query.Distinct && query.Fields.Count > 0;

        if (filters.Refined is { } counted && !distinct)
        {
            return ceiling is { } most ? Math.Min(counted.Count, most) : counted.Count;
        }

        Statement statement = new(filters);

        if (distinct)
        {
            statement.Sql.Append("select count_big(*) from (select distinct ")
                .Append(string.Join(", ", query.Fields.Select(f => "t." + LayerDefinition.Quote(f))))
                .Append(" from ").Append(From).Append(" t");
            statement.AppendWhere();
            statement.Sql.Append(") d");
        }
        else if (ceiling is { } most)
        {
            statement.Sql.Append("select count_big(*) from (select top (").Append(statement.Bind("ceiling", most)).Append(") 1 as o from ")
                .Append(From).Append(" t");
            statement.AppendWhere();
            statement.Sql.Append(") d");
        }
        else
        {
            statement.Sql.Append("select count_big(*) from ").Append(From).Append(" t");
            statement.AppendWhere();
        }

        await using SqlCommand command = statement.Command(connection, _timeout);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public Task<(Envelope? Extent, long Count)> ExtentAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => ExtentBoundedAsync(query, token), cancellationToken);
    }

    private async Task<(Envelope? Extent, long Count)> ExtentBoundedAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        if (_layer.IsTable)
        {
            throw new QueryNotSupportedException($"'{_layer.Name}' is a table and has no geometry to take an extent of (ADR-213).");
        }

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SqlServerTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        Statement statement = new(filters);

        statement.Sql.Append("select ").Append(Wkb).Append(" from ").Append(From).Append(" t");
        statement.AppendWhere();

        Envelope extent = Envelope.Empty;
        long count = 0;
        List<Geometry> pending = new(ProjectionBatch);

        await using (SqlCommand command = statement.Command(connection, _timeout))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                count++;

                if (reader.IsDBNull(0) || WkbReader.Read((byte[])reader.GetValue(0)) is not { IsEmpty: false } shape)
                {
                    continue;
                }

                if (!Projects(query))
                {
                    extent = extent.Union(shape.Envelope);
                    continue;
                }

                pending.Add(shape);

                if (pending.Count == ProjectionBatch)
                {
                    extent = extent.Union(await ExtentOfAsync(pending, query, cancellationToken).ConfigureAwait(false));
                    pending.Clear();
                }
            }
        }

        if (pending.Count > 0)
        {
            extent = extent.Union(await ExtentOfAsync(pending, query, cancellationToken).ConfigureAwait(false));
        }

        return (extent.IsEmpty ? null : extent, count);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> ObjectIdsAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => ObjectIdsBoundedAsync(query, token), cancellationToken);
    }

    private async Task<IReadOnlyList<long>> ObjectIdsBoundedAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SqlServerTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);

        if (filters.Refined is { } refined)
        {
            List<long> sorted = [.. refined];
            sorted.Sort();
            return sorted;
        }

        Statement statement = new(filters);

        statement.Sql.Append("select ").Append(Id).Append(" from ").Append(From).Append(" t");
        statement.AppendWhere();
        statement.Sql.Append(" order by ").Append(Id);

        List<long> ids = [];

        await using SqlCommand command = statement.Command(connection, _timeout);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        return ids;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StatisticsAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => StatisticsBoundedAsync(query, token), cancellationToken);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StatisticsBoundedAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SqlServerTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        Statement statement = new(filters);
        StringBuilder sql = statement.Sql;
        List<string> columns = [.. table.Columns.Select(c => c.Name)];

        // Expressions are computed in an inner select and grouped and aggregated outside it, by name — MySQL's shape. The
        // inner select names the table's columns rather than `t.*`, so the shape is not carried through a GROUP BY.
        List<string> inner = [.. table.Columns.Select(c => "t." + LayerDefinition.Quote(c.Name))];
        List<string> groups = [.. query.GroupBy.Select(LayerDefinition.Quote)];

        int expressions = 0;

        string Bound(BoundExpression expression)
        {
            string prefix = "x" + expressions.ToString(CultureInfo.InvariantCulture);
            expressions++;

            if (!PredicateSql.TryEmitExpression(
                    expression.Operand!, columns, c => "t." + LayerDefinition.Quote(c), i => $"@{prefix}_{i.ToString(CultureInfo.InvariantCulture)}",
                    out string written, out IReadOnlyList<object?> values, out string? error, SqlDialect.SqlServer))
            {
                throw new QueryNotSupportedException(error!);
            }

            for (int i = 0; i < values.Count; i++)
            {
                statement.Bind($"{prefix}_{i.ToString(CultureInfo.InvariantCulture)}", values[i] ?? DBNull.Value);
            }

            return written;
        }

        foreach (GroupExpression grouped in query.GroupExpressions)
        {
            inner.Add($"({Bound(grouped.Expression)}) as {LayerDefinition.Quote(grouped.Name)}");
            groups.Add(LayerDefinition.Quote(grouped.Name));
        }

        List<string> select = [.. groups];

        // A percentile from each row's rank among its group's values and how many there are, as MySQL's provider computes
        // it — T-SQL's PERCENTILE_CONT is a window function only, which cannot sit beside a GROUP BY.
        List<string> windows = [];

        for (int k = 0; k < query.Statistics.Count; k++)
        {
            StatisticRequest statistic = query.Statistics[k];
            string? computed = null;

            if (statistic.Expression is { } expression)
            {
                computed = "graticula_s" + k.ToString(CultureInfo.InvariantCulture);
                inner.Add($"({Bound(expression)}) as {LayerDefinition.Quote(computed)}");
            }

            if (statistic.Kind is StatisticKind.PercentileContinuous or StatisticKind.PercentileDiscrete)
            {
                string value = LayerDefinition.Quote(computed ?? statistic.Field);
                string rank = LayerDefinition.Quote("graticula_rn" + k.ToString(CultureInfo.InvariantCulture));
                string count = LayerDefinition.Quote("graticula_n" + k.ToString(CultureInfo.InvariantCulture));
                string partition = groups.Count > 0 ? "partition by " + string.Join(", ", groups) + " " : string.Empty;

                windows.Add($"row_number() over ({partition}order by {Ordered(value, statistic.Descending)}) as {rank}");
                windows.Add($"count({value}) over ({(groups.Count > 0 ? "partition by " + string.Join(", ", groups) : string.Empty)}) as {count}");
                select.Add($"{Percentile(statistic, value, rank, count)} as {LayerDefinition.Quote(statistic.OutName)}");
                continue;
            }

            select.Add($"{Aggregate(statistic, computed)} as {LayerDefinition.Quote(statistic.OutName)}");
        }

        sql.Append("select ").Append(string.Join(", ", select)).Append(" from (");

        if (windows.Count > 0)
        {
            sql.Append("select w.*, ").Append(string.Join(", ", windows)).Append(" from (");
        }

        sql.Append("select ").Append(string.Join(", ", inner)).Append(" from ").Append(From).Append(" t");
        statement.AppendWhere();
        sql.Append(windows.Count > 0 ? ") w) q" : ") q");

        bool grouping = groups.Count > 0;

        if (grouping)
        {
            sql.Append(" group by ").Append(string.Join(", ", groups));
        }

        if (query.Having is { Predicate: { } having, Columns: { } aggregates })
        {
            List<string> names = [.. aggregates.Keys, .. query.GroupBy, .. query.GroupExpressions.Select(g => g.Name)];

            if (!PredicateSql.TryEmit(
                    having, names, name => aggregates.TryGetValue(name, out string? aggregate) ? aggregate : LayerDefinition.Quote(name),
                    out ParsedWhere written, out string? error, i => "@h" + i.ToString(CultureInfo.InvariantCulture), SqlDialect.SqlServer))
            {
                throw new QueryNotSupportedException(error!);
            }

            sql.Append(" having ").Append(written.Sql);

            for (int i = 0; i < written.Parameters.Count; i++)
            {
                statement.Bind("h" + i.ToString(CultureInfo.InvariantCulture), written.Parameters[i] ?? DBNull.Value);
            }
        }

        if (grouping)
        {
            List<string> ordering = [];

            foreach (SortKey key in query.OrderBy)
            {
                string? named = query.GroupBy
                    .Concat(query.GroupExpressions.Select(g => g.Name))
                    .FirstOrDefault(n => n.Equals(key.Field, StringComparison.OrdinalIgnoreCase));

                if (named is not null)
                {
                    ordering.Add(Ordered(LayerDefinition.Quote(named), key.Descending, Nullable(table, named)));
                    continue;
                }

                // A statistic is ordered by its aggregate: T-SQL does not let ORDER BY use an alias inside an expression.
                int at = query.Statistics.ToList().FindIndex(s => s.OutName.Equals(key.Field, StringComparison.OrdinalIgnoreCase));

                if (at >= 0 && query.Statistics[at].Kind is not (StatisticKind.PercentileContinuous or StatisticKind.PercentileDiscrete))
                {
                    string aggregate = Aggregate(query.Statistics[at], query.Statistics[at].Expression is null ? null : "graticula_s" + at.ToString(CultureInfo.InvariantCulture));
                    ordering.Add(Ordered(aggregate, key.Descending));
                }
            }

            if (ordering.Count == 0)
            {
                ordering.AddRange(groups.Select(g => Ordered(g, descending: false)));
            }

            sql.Append(" order by ").Append(string.Join(", ", ordering))
               .Append(" offset 0 rows fetch next ").Append(statement.Bind("limit", query.Limit)).Append(" rows only");
        }

        Dictionary<string, FieldType> kinds = new(StringComparer.Ordinal);

        foreach (string group in query.GroupBy)
        {
            kinds[group] = table.TypeOf(group);
        }

        foreach (StatisticRequest statistic in query.Statistics)
        {
            kinds[statistic.OutName] = statistic.Kind switch
            {
                StatisticKind.Count => FieldType.BigInteger,
                StatisticKind.Min or StatisticKind.Max or StatisticKind.PercentileDiscrete when statistic.Expression is null
                    => table.TypeOf(statistic.Field),
                _ => FieldType.Double,
            };
        }

        List<IReadOnlyDictionary<string, object?>> rows = [];

        await using SqlCommand command = statement.Command(connection, _timeout);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, object?> row = [];

            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                row[name] = reader.IsDBNull(i)
                    ? null
                    : Normalise(reader.GetValue(i), kinds.TryGetValue(name, out FieldType kind) ? kind : FieldType.Unknown);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Refuses what this provider does not answer, before any work is done.</summary>
    private void Refuse(FeatureQuery query)
    {
        const string Here = "This layer is in a registered SQL Server database, which this build reads";

        if (query.HistoricMoment is not null)
        {
            throw new QueryNotSupportedException($"{Here} and keeps no history of, so it has no historicMoment (ADR-210 §5).");
        }

        if (query.Having is { } having && (having.Predicate is null || having.Columns is null))
        {
            throw new QueryNotSupportedException(
                $"{Here}, and this having clause reached it without its parsed form, so it cannot be written in T-SQL.");
        }

        if (query.Statistics.Any(s => s.Expression is { Operand: null }) || query.GroupExpressions.Any(g => g.Expression.Operand is null))
        {
            throw new QueryNotSupportedException(
                $"{Here}, and a statistic expression reached it without its parsed form, so it cannot be written in T-SQL.");
        }

        // A table ignores a spatial filter, as ArcGIS's does (ADR-213), so nothing about one is refused.
        if (_layer.IsTable || query.Spatial is not { } spatial)
        {
            return;
        }

        if (spatial.Geometry.CoordinateCount > MostFilterVertices)
        {
            throw new QueryNotSupportedException(
                $"The filter geometry has {spatial.Geometry.CoordinateCount:N0} vertices, and a SQL Server layer compares it "
                + $"with each feature in this server, so it takes at most {MostFilterVertices:N0}. Generalize it first "
                + "(GeometryServer/generalize).");
        }

        if (spatial.Distance > 0 && _engine is null)
        {
            throw new QueryNotSupportedException(
                $"{Here} without a geometry engine here, so not within a distance. Buffer the geometry first "
                + "(GeometryServer/buffer) and filter by the result.");
        }

        if (_engine is not null && EngineRelations.Contains(spatial.Relation))
        {
            return;
        }

        if (!Relations.Contains(spatial.Relation))
        {
            throw new QueryNotSupportedException(
                $"{Here} with the spatial relations intersects, envelope-intersects and index-intersects, and "
                + $"'{spatial.Relation}' is not one of them.");
        }
    }

    private CancellationTokenSource Deadline(CancellationToken caller)
    {
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(caller);
        linked.CancelAfter(_timeout);
        return linked;
    }

    private async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = Deadline(cancellationToken);

        try
        {
            return await work(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception stopped) when (TimedOut(stopped, deadline, cancellationToken))
        {
            throw Timeout();
        }
    }

    private static bool TimedOut(Exception stopped, CancellationTokenSource deadline, CancellationToken caller) =>
        deadline.IsCancellationRequested && !caller.IsCancellationRequested && stopped is OperationCanceledException or SqlException;

    private TimeoutException Timeout() => new(
        $"A query on layer '{_layer.Name}' ran past the {_timeout.TotalSeconds:0.#} seconds this service allows one "
        + "statement, and SQL Server was told to stop. Narrow the extent or the where clause.");

    private Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) => SqlServerConnections.OpenAsync(_connectionString, cancellationToken);

    /// <summary>The table's columns, its reference and whether its shapes are <c>geography</c>, read at most once a minute.</summary>
    internal async Task<SqlServerTable> TableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (Tables.TryGetValue(_tableKey, out var kept) && DateTimeOffset.UtcNow - kept.At < ColumnsKept)
        {
            return kept.Table;
        }

        List<SqlServerColumn> columns = [];
        bool? geography = null;

        await using (SqlCommand command = new(
            """
            select COLUMN_NAME, DATA_TYPE, NUMERIC_SCALE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
              from INFORMATION_SCHEMA.COLUMNS
             where TABLE_SCHEMA = @s and TABLE_NAME = @t
             order by ORDINAL_POSITION
            """, connection))
        {
            command.Parameters.AddWithValue("@s", _layer.SchemaName);
            command.Parameters.AddWithValue("@t", _layer.TableName);

            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string name = reader.GetString(0);
                string type = reader.GetString(1);

                if (_layer.GeometryColumn is { } shape && string.Equals(name, shape, StringComparison.Ordinal))
                {
                    geography = string.Equals(type, "geography", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                FieldType field = TypeOf(type, reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture));

                if (field == FieldType.Unknown)
                {
                    continue;
                }

                int? length = field == FieldType.Text && !reader.IsDBNull(3)
                    && Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture) is var max and > 0 and <= int.MaxValue
                        ? (int)max
                        : null;

                columns.Add(new SqlServerColumn(name, field, reader.GetString(4) == "YES", length));
            }
        }

        // A table (ADR-213) has no geometry column to find; a layer that has lost its own is misregistered.
        if (columns.Count == 0 || (!_layer.IsTable && geography is null))
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' is served from {_layer.SchemaName}.{_layer.TableName}"
                + (_layer.GeometryColumn is { } column ? $".{column}" : string.Empty) + ", and "
                + "this database user sees no such table and column. It was dropped, renamed, or the user lost the right to read it.");
        }

        SqlServerTable table = new(columns, _layer.Srid, geography ?? false);
        Tables[_tableKey] = (DateTimeOffset.UtcNow, table);
        return table;
    }

    private bool Projects(FeatureQuery query) => (query.OutSrid is { } srid && srid != _layer.Srid) || query.OutWkt is { Length: > 0 };

    /// <summary>The where clause and its values, shared by every method.</summary>
    private sealed record Filters(
        IReadOnlyList<string> Clauses,
        IReadOnlyList<(string Name, object Value)> Parameters,
        IReadOnlyList<long>? Refined = null);

    private async Task<Filters> FiltersAsync(SqlConnection connection, FeatureQuery query, SqlServerTable table, CancellationToken cancellationToken)
    {
        Filters filters = await RowFiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);

        if (query.Top is not { } top)
        {
            return filters;
        }

        // queryTopFeatures: the PostGIS path's `row_number() over (partition by … order by …)`, over exactly the rows
        // the other filters keep.
        string order = top.OrderBy.Count == 0
            ? Id
            : string.Join(", ", top.OrderBy.Select(k => Ordered("t." + LayerDefinition.Quote(k.Field), k.Descending, Nullable(table, k.Field))));

        StringBuilder ranked = new StringBuilder("select ").Append(Id).Append(" as i, row_number() over (");

        if (top.GroupBy.Count > 0)
        {
            ranked.Append("partition by ").Append(string.Join(", ", top.GroupBy.Select(g => "t." + LayerDefinition.Quote(g)))).Append(' ');
        }

        ranked.Append("order by ").Append(order).Append(", ").Append(Id).Append(") as r from ").Append(From).Append(" t");

        if (filters.Clauses.Count > 0)
        {
            ranked.Append(" where ").Append(string.Join(" and ", filters.Clauses));
        }

        return new Filters(
            [$"{Id} in (select k.i from ({ranked}) k where k.r <= @top_count)"],
            [.. filters.Parameters, ("top_count", (object)top.Count)]);
    }

    private async Task<Filters> RowFiltersAsync(SqlConnection connection, FeatureQuery query, SqlServerTable table, CancellationToken cancellationToken)
    {
        List<string> attributes = [];
        List<(string Name, object Value)> parameters = [];
        List<Envelope> boxes = [];
        Geometry? shape = null;
        SpatialFilter? byEngine = null;

        if (query.Where is { } where)
        {
            attributes.Add("(" + Where(where, table, parameters) + ")");
        }

        if (query.Identities.Count > 0)
        {
            parameters.Add(("ids", JsonSerializer.Serialize(query.Identities)));
            attributes.Add($"{Id} in (select cast(j.[value] as bigint) from openjson(@ids) j)");
        }

        // <b>A table has no geometry, so a spatial filter tests nothing and is not applied — ADR-213</b>, as ArcGIS's own
        // table answers and as the PostGIS source does.
        if (!_layer.IsTable && query.BoundingBox is { } bbox)
        {
            boxes.Add(bbox);
        }

        if (!_layer.IsTable && query.Spatial is { } spatial)
        {
            Geometry filter = await ToLayerAsync(spatial.Geometry, query, cancellationToken).ConfigureAwait(false);

            if (filter.IsEmpty)
            {
                return new Filters(["1 = 0"], []);
            }

            boxes.Add(EngineRelations.SearchBox(filter, spatial.Distance, _layer.Srid));

            if (EngineRelations.Decides(spatial))
            {
                byEngine = spatial with { Geometry = filter };
            }
            else if (spatial.Relation == SpatialRelation.Intersects)
            {
                shape = filter;
            }
        }

        if (boxes.Count == 0)
        {
            return new Filters(attributes, parameters);
        }

        List<string> inSqlServer = [.. attributes];

        for (int i = 0; i < boxes.Count; i++)
        {
            inSqlServer.Add(BoxClause(boxes[i], "box" + i.ToString(CultureInfo.InvariantCulture), table, parameters));
        }

        // A box is decided in SQL Server, exactly: the shape meets the box polygon, planar, boundary included — what the
        // PostGIS path's `&&` and the exact pass here both answer for it.
        if (shape is null && byEngine is null)
        {
            return new Filters(inSqlServer, parameters);
        }

        List<Geometry>? kept = byEngine is null ? null : [];

        List<long>? matched = await RefineAsync(connection, new Filters(inSqlServer, parameters), boxes, shape, cancellationToken, kept)
            .ConfigureAwait(false);

        if (matched is null)
        {
            return new Filters(inSqlServer, parameters);
        }

        if (byEngine is not null)
        {
            matched = await EngineRelations.SatisfyingAsync(
                    matched, kept!, byEngine, _layer.Srid, _projector, _engine!, "a registered SQL Server database", cancellationToken)
                .ConfigureAwait(false);
        }

        if (matched.Count == 0)
        {
            return new Filters(["1 = 0"], [], matched);
        }

        return new Filters(
            [$"{Id} in (select cast(r.[value] as bigint) from openjson(@refined) r)"],
            [("refined", JsonSerializer.Serialize(matched))],
            matched);
    }

    /// <summary>The identities of the candidates whose shapes meet the filter — the exact pass.</summary>
    private async Task<List<long>?> RefineAsync(
        SqlConnection connection,
        Filters candidates,
        IReadOnlyList<Envelope> boxes,
        Geometry? shape,
        CancellationToken cancellationToken,
        List<Geometry>? kept = null)
    {
        Statement statement = new(candidates);

        statement.Sql.Append("select ").Append(Id).Append(", ").Append(Wkb).Append(" from ").Append(From).Append(" t");
        statement.AppendWhere();

        List<long> matched = [];

        await using SqlCommand command = statement.Command(connection, _timeout);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            Geometry geometry = WkbReader.Read((byte[])reader.GetValue(1));
            Envelope own = geometry.Envelope;

            if (boxes.Any(b => !own.Intersects(b)) || (shape is not null && !GeometryPredicates.Intersects(geometry, shape)))
            {
                continue;
            }

            if (matched.Count == _mostMatched)
            {
                if (shape is null && kept is null)
                {
                    return null;
                }

                throw new QueryNotSupportedException(
                    $"The filter meets more than {_mostMatched:N0} features of this layer. A SQL Server layer holds what a "
                    + "spatial filter matched while it answers, so it bounds how many; narrow the filter.");
            }

            matched.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            kept?.Add(geometry);
        }

        return matched;
    }

    /// <summary>
    /// A shape meeting a rectangle, planar: <c>STIntersects</c> against the box as a <c>geometry</c> in the column's SRID,
    /// which the spatial index answers — or, for a <c>geography</c> column, against its coordinates under SRID 0.
    /// </summary>
    private string BoxClause(Envelope box, string name, SqlServerTable table, List<(string Name, object Value)> parameters)
    {
        if (AxisOrder.IsGeographic(_layer.Srid))
        {
            box = new Envelope(Math.Max(-180, box.MinX), Math.Max(-90, box.MinY), Math.Min(180, box.MaxX), Math.Min(90, box.MaxY));
        }

        string n(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        parameters.Add((name,
            $"POLYGON(({n(box.MinX)} {n(box.MinY)}, {n(box.MaxX)} {n(box.MinY)}, {n(box.MaxX)} {n(box.MaxY)}, "
            + $"{n(box.MinX)} {n(box.MaxY)}, {n(box.MinX)} {n(box.MinY)}))"));

        string srid = table.Geography ? "0" : table.Srid.ToString(CultureInfo.InvariantCulture);
        return $"{Planar(table)}.STIntersects(geometry::STGeomFromText(@{name}, {srid})) = 1";
    }

    /// <summary>The where clause, emitted again from its tree in T-SQL (D-162, ADR-210).</summary>
    private static string Where(ParsedWhere where, SqlServerTable table, List<(string Name, object Value)> parameters)
    {
        if (where.Predicate is null)
        {
            throw new InvalidOperationException(
                "A where clause reached a SQL Server layer without its parsed predicate, so it cannot be emitted in T-SQL.");
        }

        string[] guids = [.. table.Columns.Where(c => c.Type == FieldType.Guid).Select(c => c.Name)];

        if (!PredicateSql.TryEmit(
                where.Predicate, [.. table.Columns.Select(c => c.Name)], c => "t." + LayerDefinition.Quote(c),
                out ParsedWhere emitted, out string? error, dialect: SqlDialect.SqlServer.WithGuids(guids, arcGisText: true)))
        {
            throw new QueryNotSupportedException(error!);
        }

        for (int i = 0; i < emitted.Parameters.Count; i++)
        {
            parameters.Add(("w" + i.ToString(CultureInfo.InvariantCulture), emitted.Parameters[i] ?? DBNull.Value));
        }

        return emitted.Sql;
    }

    private async Task<Geometry> ToLayerAsync(Geometry geometry, FeatureQuery query, CancellationToken cancellationToken)
    {
        if (query.FilterSrid is not { } from || from == _layer.Srid || geometry.IsEmpty)
        {
            return geometry;
        }

        (IReadOnlyList<Geometry> projected, _) = await _projector.ProjectAsync([geometry], from, _layer.Srid, cancellationToken).ConfigureAwait(false);
        return projected[0];
    }

    private async Task<IReadOnlyList<Geometry>> OutputAsync(IReadOnlyList<Geometry> geometries, FeatureQuery query, CancellationToken cancellationToken)
    {
        if (query.OutSrid is { } srid && srid != _layer.Srid)
        {
            (IReadOnlyList<Geometry> projected, _) = await _projector.ProjectAsync(geometries, _layer.Srid, srid, cancellationToken).ConfigureAwait(false);
            return projected;
        }

        if (query.OutWkt is { Length: > 0 } written)
        {
            return await _projector.ProjectToDefinitionAsync(geometries, _layer.Srid, written, cancellationToken).ConfigureAwait(false)
                ?? throw new QueryNotSupportedException("The projector could not read the written coordinate reference this request asked for.");
        }

        return geometries;
    }

    private async Task<Envelope> ExtentOfAsync(List<Geometry> shapes, FeatureQuery query, CancellationToken cancellationToken)
    {
        Envelope extent = Envelope.Empty;

        foreach (Geometry moved in await OutputAsync(shapes, query, cancellationToken).ConfigureAwait(false))
        {
            if (!moved.IsEmpty)
            {
                extent = extent.Union(moved.Envelope);
            }
        }

        return extent;
    }

    /// <summary>Projects, simplifies and rounds a batch's geometries — GeoParquetFeatureSource's order and reasons.</summary>
    private async Task<List<Feature>> ReshapeAsync(List<Feature> batch, FeatureQuery query, CancellationToken cancellationToken)
    {
        List<Geometry> shapes = [];
        List<int> at = [];

        for (int i = 0; i < batch.Count; i++)
        {
            if (batch[i].Geometry is { IsEmpty: false } shape)
            {
                shapes.Add(shape);
                at.Add(i);
            }
        }

        IReadOnlyList<Geometry> moved = shapes;

        if (shapes.Count > 0 && query.MaxAllowableOffset is { } tolerance and > 0)
        {
            if (query.OutSrid is { } srid && srid != _layer.Srid)
            {
                moved = await _projector.GeneralizeAsync(shapes, _layer.Srid, srid, tolerance, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                IReadOnlyList<Geometry> placed = Projects(query) ? await OutputAsync(shapes, query, cancellationToken).ConfigureAwait(false) : shapes;
                moved = await _projector.GeneralizeAsync(placed, _layer.Srid, _layer.Srid, tolerance, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (shapes.Count > 0 && Projects(query))
        {
            moved = await OutputAsync(shapes, query, cancellationToken).ConfigureAwait(false);
        }

        List<Feature> result = new(batch);

        for (int k = 0; k < at.Count; k++)
        {
            Feature old = batch[at[k]];
            Geometry shape = query.Precision is { } places and >= 0 ? GeometryRounding.Round(moved[k], places) : moved[k];
            object?[] values = new object?[old.Schema.Count];

            for (int v = 0; v < values.Length; v++)
            {
                values[v] = old[v];
            }

            result[at[k]] = new Feature(old.Id, shape, old.Schema, values);
        }

        return result;
    }

    private void AppendOrderAndPaging(Statement statement, FeatureQuery query, FeatureSchema schema, bool distinct, SqlServerTable table)
    {
        StringBuilder sql = statement.Sql;

        if (distinct)
        {
            sql.Append(" order by ").Append(string.Join(", ", schema.Names.Distinct(StringComparer.Ordinal)
                .Select(n => Ordered("t." + LayerDefinition.Quote(n), descending: false, Nullable(table, n)))));
        }
        else if (query.OrderBy.Count > 0)
        {
            sql.Append(" order by ").Append(string.Join(", ", query.OrderBy.Select(k => Ordered("t." + LayerDefinition.Quote(k.Field), k.Descending, Nullable(table, k.Field)))));

            if (!query.OrderBy.Any(k => string.Equals(k.Field, _layer.IdentityColumn, StringComparison.Ordinal)))
            {
                sql.Append(", ").Append(Id);
            }
        }
        else
        {
            sql.Append(" order by ").Append(Id);
        }

        sql.Append(" offset ").Append(statement.Bind("offset", Math.Max(0, query.Offset)))
           .Append(" rows fetch next ").Append(statement.Bind("limit", query.Limit)).Append(" rows only");
    }

    private static string Aggregate(StatisticRequest statistic, string? computed = null)
    {
        string column = LayerDefinition.Quote(computed ?? statistic.Field);

        return statistic.Kind switch
        {
            StatisticKind.Count => $"count_big({column})",
            StatisticKind.Sum => $"sum({column})",
            StatisticKind.Min => $"min({column})",
            StatisticKind.Max => $"max({column})",

            // T-SQL averages whole numbers into a whole number; PostgreSQL's is exact, and a double here.
            StatisticKind.Avg => $"avg(1e0 * {column})",
            StatisticKind.StdDev => $"stdev(1e0 * {column})",
            StatisticKind.Var => $"var(1e0 * {column})",
            _ => throw new ArgumentOutOfRangeException(nameof(statistic), statistic.Kind, null),
        };
    }

    /// <summary>A percentile from each row's rank and its group's count, as PostgreSQL defines it (MySqlFeatureSource).</summary>
    private static string Percentile(StatisticRequest statistic, string value, string rank, string count)
    {
        string p = Math.Clamp(statistic.Fraction, 0, 1).ToString("0.################", CultureInfo.InvariantCulture);

        if (statistic.Kind == StatisticKind.PercentileDiscrete)
        {
            return $"{(statistic.Descending ? "max" : "min")}(case when {rank} >= ceiling({p} * {count}) then {value} end)";
        }

        string position = $"(1 + {p} * ({count} - 1))";
        string lower = $"max(case when {rank} = floor{position} then {value} end)";
        string upper = $"max(case when {rank} = ceiling{position} then {value} end)";
        string fraction = $"((1 + {p} * (max({count}) - 1)) - floor(1 + {p} * (max({count}) - 1)))";

        return $"({lower} + {fraction} * ({upper} - {lower}))";
    }

    /// <summary>A SQL Server column type as the field list reports it; the rest are not served.</summary>
    internal static FieldType TypeOf(string type, int? scale) => type.ToLowerInvariant() switch
    {
        "tinyint" or "smallint" => FieldType.SmallInteger,
        "int" => FieldType.Integer,
        "bigint" => FieldType.BigInteger,
        "decimal" or "numeric" when scale == 0 => FieldType.BigInteger,
        "decimal" or "numeric" or "float" or "money" or "smallmoney" => FieldType.Double,
        "real" => FieldType.Single,
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" => FieldType.Text,
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => FieldType.Date,
        "bit" => FieldType.Boolean,
        "uniqueidentifier" => FieldType.Guid,
        "binary" or "varbinary" => FieldType.Binary,
        _ => FieldType.Unknown,
    };

    /// <summary>A SQL Server value as the faces expect one — the CLR type its column's field type promises.</summary>
    internal static object? Normalise(object? value, FieldType type) => value switch
    {
        null or DBNull => null,
        byte or short or int or long or decimal =>
            Convert.ToDecimal(value, CultureInfo.InvariantCulture) is var number
                ? type switch
                {
                    FieldType.SmallInteger when number is >= short.MinValue and <= short.MaxValue => (object)(short)number,
                    FieldType.SmallInteger or FieldType.Integer when number is >= int.MinValue and <= int.MaxValue => (object)(int)number,
                    FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger when number is >= long.MinValue and <= long.MaxValue
                        => (object)(long)number,
                    FieldType.Single => (object)(float)number,
                    _ => (object)(double)number,
                }
                : null,
        double real => type switch
        {
            FieldType.SmallInteger => (object)(short)real,
            FieldType.Integer => (object)(int)real,
            FieldType.BigInteger => (object)(long)real,
            FieldType.Single => (object)(float)real,
            _ => (object)real,
        },
        float single => type == FieldType.Double ? (object)(double)single : single,
        DateTime moment => DateTime.SpecifyKind(moment, DateTimeKind.Utc),
        DateTimeOffset moment => moment.UtcDateTime,
        _ => value,
    };

    internal sealed record SqlServerColumn(string Name, FieldType Type, bool Nullable, int? MaxLength);

    /// <param name="Columns">The table's served columns.</param>
    /// <param name="Srid">The layer's reference — SQL Server keeps one per shape, and the probe found one for every row.</param>
    /// <param name="Geography">Whether the column is <c>geography</c> rather than <c>geometry</c>.</param>
    internal sealed record SqlServerTable(IReadOnlyList<SqlServerColumn> Columns, int Srid, bool Geography)
    {
        public FieldType TypeOf(string name) =>
            Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.Type ?? FieldType.Unknown;
    }

    /// <summary>A statement being built, with the driver's named parameters.</summary>
    private sealed class Statement(Filters filters)
    {
        private readonly List<(string Name, object Value)> _parameters = [.. filters.Parameters];

        public StringBuilder Sql { get; } = new();

        public string Bind(string name, object value)
        {
            _parameters.Add((name, value));
            return "@" + name;
        }

        public void AppendWhere()
        {
            if (filters.Clauses.Count > 0)
            {
                Sql.Append(" where ").Append(string.Join(" and ", filters.Clauses));
            }
        }

        public SqlCommand Command(SqlConnection connection, TimeSpan timeout)
        {
            SqlCommand command = new(Sql.ToString(), connection) { CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds) };

            foreach ((string name, object value) in _parameters)
            {
                // A text longer than nvarchar(4000) — an id list — goes as nvarchar(max), as OPENJSON takes it.
                command.Parameters.Add(value is string { Length: > 4000 } text
                    ? new SqlParameter("@" + name, System.Data.SqlDbType.NVarChar, -1) { Value = text }
                    : new SqlParameter("@" + name, value));
            }

            return command;
        }
    }
}
