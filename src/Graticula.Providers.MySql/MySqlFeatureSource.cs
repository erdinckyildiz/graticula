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
using MySqlConnector;
using SortKey = Graticula.Features.SortKey;

namespace Graticula.Providers.MySql;

/// <summary>A layer served from one spatial table of a registered MySQL database — ADR-208.</summary>
/// <remarks>
/// <para>
/// <b>OracleFeatureSource's shape, with MySQL's index in Oracle's place.</b> A spatial filter is two passes:
/// <c>MBRIntersects</c> asks the spatial index which rows' boxes meet the filter's, and
/// <see cref="GeometryPredicates.Intersects"/> — or the geometry engine, for every other relation and a distance
/// (<see cref="EngineRelations"/>) — decides here which shapes do. Under a geographic reference MySQL computes its own
/// relations on the ellipsoid where PostGIS's <c>geometry</c> is planar, so deciding here is what keeps a MySQL layer's
/// answer a PostGIS layer's (ADR-208 Alternative B).
/// </para>
/// <para>
/// <b>Geometry crosses as WKB in longitude–latitude order</b> (<c>'axis-order=long-lat'</c>, which MySQL ignores for a
/// projected reference), so nothing above this class sees MySQL's latitude-first storage under EPSG:4326. MySQL keeps x
/// and y only: a layer here has no Z or M.
/// </para>
/// </remarks>
public sealed class MySqlFeatureSource : IFeatureSource, IFeatureSummaries, IFeatureVersions, IGeometryStatistics
{
    private const int ProjectionBatch = 512;

    /// <summary>How long one query may run when the service says nothing shorter — the PostGIS path's thirty seconds.</summary>
    public static readonly TimeSpan DefaultStatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The most features a filter may match before it is refused — GeoParquet's bound, for its reason.</summary>
    public const int DefaultMostMatched = 1_000_000;

    /// <summary>The most vertices a spatial filter's geometry may have — GeometryServer's cap.</summary>
    public const int MostFilterVertices = 130_000;

    private static readonly TimeSpan ColumnsKept = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, MySqlTable Table)> Tables = new(StringComparer.Ordinal);

    private readonly string _connectionString;
    private readonly string _tableKey;
    private readonly LayerDefinition _layer;
    private readonly IProjector _projector;
    private readonly TimeSpan _timeout;
    private readonly int _mostMatched;
    private readonly IGeometryEngine? _engine;

    /// <summary>Serves one table of a registered MySQL database as a layer.</summary>
    /// <param name="locator">The data source's <c>mysql:</c> locator.</param>
    /// <param name="layer">The layer; its schema is the MySQL database.</param>
    /// <param name="projector">The datastore's projector.</param>
    /// <param name="statementTimeout">How long one query may run; <see cref="DefaultStatementTimeout"/> when null.</param>
    /// <param name="engine">The geometry engine, which decides a distance and every relation beyond intersection.</param>
    public MySqlFeatureSource(
        string locator, LayerDefinition layer, IProjector projector, TimeSpan? statementTimeout = null,
        IGeometryEngine? engine = null)
        : this(locator, layer, projector, statementTimeout, DefaultMostMatched, engine)
    {
    }

    internal MySqlFeatureSource(
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

        _connectionString = MySqlConnections.For(locator, pooling: true);
        _tableKey = MySqlLocator.DatabaseKey(locator) + "|" + layer.SchemaName + "." + layer.TableName + "." + layer.GeometryColumn;
        _layer = layer;
        _projector = projector;
        _timeout = statementTimeout ?? DefaultStatementTimeout;
        _mostMatched = mostMatched;
        _engine = engine;
    }

    /// <summary>The relations a MySQL layer answers itself; the rest are the geometry engine's (EngineRelations).</summary>
    public static IReadOnlyList<SpatialRelation> Relations { get; } =
        [SpatialRelation.Intersects, SpatialRelation.EnvelopeIntersects, SpatialRelation.IndexIntersects];

    private string Id => LayerDefinition.Quote(_layer.IdentityColumn);

    private string Shape => LayerDefinition.Quote(_layer.GeometryColumn);

    private string From => _layer.QuotedTable;

    /// <summary>
    /// What a query reads: the table, or — with a <c>historicMoment</c> — the layer's history as it was then (ADR-207,
    /// <see cref="MySqlFeatureHistory.AtMoment"/>). The moment is written as a literal this code formats from a
    /// <see cref="DateTimeOffset"/>, so every statement a query builds carries it without a bind to thread through.
    /// </summary>
    private string Source(FeatureQuery query, MySqlTable table) =>
        query.HistoricMoment is not { } moment
            ? From
            : MySqlFeatureHistory.AtMoment(
                _layer,
                [.. table.Columns.Select(c => (c.Name, c.Type, c.Guid))],
                "cast('" + moment.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "' as datetime(6))",
                table.Dialect);

    /// <summary>A shape as WKB in longitude–latitude order.</summary>

    /// <summary>
    /// An ORDER BY key where nulls fall as PostgreSQL's — last ascending, first descending — rather than MySQL's and
    /// MariaDB's, which put them first ascending. Found by ADR-209's parity run: a grouped answer cut at its page limit
    /// kept a different thousand groups once a name was null, and the MySQL run had missed it because its data had none.
    /// </summary>
    /// <remarks>
    /// <b>Only where the column can hold a null.</b> The extra key is an expression, so MySQL cannot read the order off an
    /// index with it: added to a key column it turned a page at offset 100,000 into a sort of every row (measured on the
    /// VPS, 2.7 times PostGIS's time). A NOT NULL column has no nulls to place and is ordered as it is.
    /// </remarks>
    private static string Ordered(string expression, bool descending, bool nullable = true) =>
        !nullable ? $"{expression} {(descending ? "desc" : "asc")}"
        : descending ? $"{expression} is null desc, {expression} desc" : $"{expression} is null, {expression} asc";

    /// <summary>Whether a column of the table may hold a null; the identity never does, and an unknown name may.</summary>
    private bool Nullable(MySqlTable table, string field) =>
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

        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MySqlTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);

        Statement statement = new(filters);
        StringBuilder sql = statement.Sql;
        bool distinct = query.Distinct && schema.Count > 0;

        // Every selected column has its own alias, as on Oracle, so the identity asked for among the fields is not
        // named twice.
        string selected = string.Concat(schema.Names.Select(
            (field, i) => $", {LayerDefinition.Quote(field)} as c{i.ToString(CultureInfo.InvariantCulture)}"));

        if (distinct)
        {
            sql.Append("select min(").Append(Id).Append(") as i").Append(selected).Append(", null as g from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
            sql.Append(" group by ").Append(string.Join(", ", schema.Names.Distinct(StringComparer.Ordinal).Select(LayerDefinition.Quote)));
        }
        else
        {
            sql.Append("select ").Append(Id).Append(" as i").Append(selected);
            sql.Append(query.IncludeGeometry || query.ReturnCentroid ? $", {table.Dialect.Wkb(Shape)} as g" : ", null as g");
            sql.Append(" from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
        }

        AppendOrderAndPaging(statement, query, schema, distinct, table);

        FieldType[] types = [.. schema.Names.Select(table.TypeOf)];
        List<Feature> batch = new(ProjectionBatch);
        bool reshape = (query.IncludeGeometry || query.ReturnCentroid) && !distinct
            && (Projects(query) || query.Precision is >= 0 || query.MaxAllowableOffset is > 0);

        await using MySqlCommand command = statement.Command(connection, _timeout);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

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
    /// <remarks>
    /// <b>MySQL keeps no row version</b> — no <c>xmin</c>, no <c>ORA_ROWSCN</c> — so a row's version is a hash of what it
    /// holds: every column and the shape. It changes exactly when the row does (ADR-208 §5.5).
    /// </remarks>
    public Task<string?> VersionOfAsync(long identity, CancellationToken cancellationToken) =>
        Bounded(async token =>
        {
            await using MySqlConnection connection = await OpenAsync(token).ConfigureAwait(false);
            MySqlTable table = await TableAsync(connection, token).ConfigureAwait(false);

            await using MySqlCommand command = new($"select {VersionSql(table, "t")} from {From} t where t.{Id} = @id", connection);
            command.Parameters.AddWithValue("@id", identity);

            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        }, cancellationToken);

    internal string VersionSql(MySqlTable table, string alias) =>
        VersionSqlOf(table.Columns.Select(c => c.Name), _layer.GeometryColumn, alias);

    /// <summary>The SQL of a row's version: a SHA-1 of its columns and its shape, each named so a null is not lost.</summary>
    /// <param name="columns">The table's columns other than the shape.</param>
    /// <param name="shape">The geometry column.</param>
    /// <param name="alias">The row's alias, or the table's name.</param>
    /// <returns>The expression.</returns>
    internal static string VersionSqlOf(IEnumerable<string> columns, string shape, string alias) =>
        "sha1(concat_ws('|', "
        + string.Join(", ", columns.Select(c =>
            $"concat('{c.Replace("'", "''", StringComparison.Ordinal)}=', coalesce(hex({alias}.{LayerDefinition.Quote(c)}), '~'))"))
        + $", coalesce(hex(st_asbinary({alias}.{LayerDefinition.Quote(shape)})), '~')))";

    /// <inheritdoc />
    public Task<GeometryWidth?> GeometryWidthAsync(CancellationToken cancellationToken) =>
        Bounded<GeometryWidth?>(async token =>
        {
            await using MySqlConnection connection = await OpenAsync(token).ConfigureAwait(false);
            await using MySqlCommand command = new(
                $"""
                select (select table_rows from information_schema.tables where table_schema = @db and table_name = @tab),
                       (select avg(length(st_asbinary(s.{Shape}))) from (select {Shape} from {From} where {Shape} is not null limit 200) s)
                """, connection);

            command.Parameters.AddWithValue("@db", _layer.SchemaName);
            command.Parameters.AddWithValue("@tab", _layer.TableName);

            await using MySqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            float rows = reader.IsDBNull(0) ? -1 : Convert.ToSingle(reader.GetValue(0), CultureInfo.InvariantCulture);
            int? each = reader.IsDBNull(1) ? null : (int)Math.Ceiling(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture));

            return new GeometryWidth(rows, each, each is { } bytes && rows > 0 ? (long)(bytes * (double)rows) : 0);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) => Bounded(DescribeBoundedAsync, cancellationToken);

    private async Task<LayerDescription> DescribeBoundedAsync(CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MySqlTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);

        List<FieldDescription> fields = [.. table.Columns.Select(c => new FieldDescription(
            c.Name, c.Type, c.Nullable && !string.Equals(c.Name, _layer.IdentityColumn, StringComparison.Ordinal), c.MaxLength))];

        bool writable = table.IsTable
            && await MySqlConnections.CanWriteAsync(connection, _layer.SchemaName, _layer.TableName, cancellationToken).ConfigureAwait(false);

        HistoryKept? kept = await MySqlFeatureHistory.KeptAsync(connection, _layer, cancellationToken).ConfigureAwait(false);

        return new LayerDescription(fields, await ExtentOfTableAsync(connection, table.Dialect, cancellationToken).ConfigureAwait(false), writable)
        {
            AnswersDistance = _engine is not null,
            AnswersHaving = true,
            AnswersTopFeatures = true,
            AnswersCentroid = true,
            AnswersStatisticExpressions = true,
            AnswersRelations = _engine is not null,
            SpatiallyIndexed = true,

            // ADR-207: a history kept beside the table, which historicMoment reads.
            Archived = kept is not null,
            ArchivedSince = kept?.Since,
            HistoryKeepDays = kept?.KeepDays,
        };
    }

    /// <summary>
    /// The table's extent. <b>MySQL computes no envelope under a geographic reference</b> (error 3618), so the shapes are
    /// collected with their SRID set to 0 — the stored numbers, longitude first — and the envelope is of those.
    /// </summary>
    private async Task<Envelope?> ExtentOfTableAsync(MySqlConnection connection, MySqlDialect dialect, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = new(
            dialect.Extent(Shape, From), connection)
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
        catch (MySqlException)
        {
            return null;
        }
    }

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
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MySqlTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        bool distinct = query.Distinct && query.Fields.Count > 0;

        if (filters.Refined is { } counted && !distinct)
        {
            return ceiling is { } most ? Math.Min(counted.Count, most) : counted.Count;
        }

        Statement statement = new(filters);

        // Every derived table has an alias: MySQL refuses one without (error 1248).
        if (distinct)
        {
            statement.Sql.Append("select count(*) from (select distinct ")
                .Append(string.Join(", ", query.Fields.Select(LayerDefinition.Quote)))
                .Append(" from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
            statement.Sql.Append(") d");
        }
        else if (ceiling is { } most)
        {
            statement.Sql.Append("select count(*) from (select 1 from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
            statement.Sql.Append(" limit ").Append(statement.Bind("ceiling", most)).Append(") d");
        }
        else
        {
            statement.Sql.Append("select count(*) from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
        }

        await using MySqlCommand command = statement.Command(connection, _timeout);
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
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MySqlTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        Statement statement = new(filters);

        statement.Sql.Append("select ").Append(table.Dialect.Wkb(Shape)).Append(" from ").Append(Source(query, table)).Append(" t");
        statement.AppendWhere();

        Envelope extent = Envelope.Empty;
        long count = 0;
        List<Geometry> pending = new(ProjectionBatch);

        await using (MySqlCommand command = statement.Command(connection, _timeout))
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MySqlTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);

        if (filters.Refined is { } refined)
        {
            List<long> sorted = [.. refined];
            sorted.Sort();
            return sorted;
        }

        Statement statement = new(filters);

        statement.Sql.Append("select ").Append(Id).Append(" from ").Append(Source(query, table)).Append(" t");
        statement.AppendWhere();
        statement.Sql.Append(" order by ").Append(Id);

        List<long> ids = [];

        await using MySqlCommand command = statement.Command(connection, _timeout);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

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
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MySqlTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        Statement statement = new(filters);
        StringBuilder sql = statement.Sql;
        List<string> columns = [.. table.Columns.Select(c => c.Name)];

        // Expressions are computed in an inner select and grouped and aggregated outside it, by name — Oracle's shape,
        // which MySQL's ONLY_FULL_GROUP_BY asks for too.
        List<string> inner = ["t.*"];
        List<string> groups = [.. query.GroupBy.Select(LayerDefinition.Quote)];

        int expressions = 0;

        string Bound(BoundExpression expression)
        {
            string prefix = "x" + expressions.ToString(CultureInfo.InvariantCulture);
            expressions++;

            if (!PredicateSql.TryEmitExpression(
                    expression.Operand!, columns, LayerDefinition.Quote, i => $"@{prefix}_{i.ToString(CultureInfo.InvariantCulture)}",
                    out string written, out IReadOnlyList<object?> values, out string? error, SqlDialect.MySql))
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

        // <b>MySQL has no percentile function</b>, so a percentile is computed as PostgreSQL defines it from each row's
        // rank among its group's values and how many there are — window functions over a second inner select, since a
        // window cannot name an expression its own select made.
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

                windows.Add($"row_number() over ({partition}order by {value} is null, {value} {(statistic.Descending ? "desc" : "asc")}) as {rank}");
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

        sql.Append("select ").Append(string.Join(", ", inner)).Append(" from ").Append(Source(query, table)).Append(" t");
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
                    out ParsedWhere written, out string? error, i => "@h" + i.ToString(CultureInfo.InvariantCulture), SqlDialect.MySql))
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
                    .Concat(query.Statistics.Select(s => s.OutName))
                    .FirstOrDefault(n => n.Equals(key.Field, StringComparison.OrdinalIgnoreCase));

                if (named is not null)
                {
                    ordering.Add(Ordered(LayerDefinition.Quote(named), key.Descending, Nullable(table, named)));
                }
            }

            if (ordering.Count == 0)
            {
                ordering.AddRange(groups.Select(g => Ordered(g, descending: false)));
            }

            sql.Append(" order by ").Append(string.Join(", ", ordering)).Append(" limit ").Append(statement.Bind("limit", query.Limit));
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

        await using MySqlCommand command = statement.Command(connection, _timeout);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

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
        const string Here = "This layer is in a registered MySQL database, which this build reads";

        if (query.Having is { } having && (having.Predicate is null || having.Columns is null))
        {
            throw new QueryNotSupportedException(
                $"{Here}, and this having clause reached it without its parsed form, so it cannot be written in MySQL's dialect.");
        }

        if (query.Statistics.Any(s => s.Expression is { Operand: null }) || query.GroupExpressions.Any(g => g.Expression.Operand is null))
        {
            throw new QueryNotSupportedException(
                $"{Here}, and a statistic expression reached it without its parsed form, so it cannot be written in MySQL's dialect.");
        }

        if (query.Spatial is not { } spatial)
        {
            return;
        }

        if (spatial.Geometry.CoordinateCount > MostFilterVertices)
        {
            throw new QueryNotSupportedException(
                $"The filter geometry has {spatial.Geometry.CoordinateCount:N0} vertices, and a MySQL layer compares it with "
                + $"each feature in this server, so it takes at most {MostFilterVertices:N0}. Generalize it first "
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
        deadline.IsCancellationRequested && !caller.IsCancellationRequested && stopped is OperationCanceledException or MySqlException;

    private TimeoutException Timeout() => new(
        $"A query on layer '{_layer.Name}' ran past the {_timeout.TotalSeconds:0.#} seconds this service allows one "
        + "statement, and MySQL was told to stop. Narrow the extent or the where clause.");

    private Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken) => MySqlConnections.OpenAsync(_connectionString, cancellationToken);

    /// <summary>The table's columns and its SRID, read at most once a minute.</summary>
    internal async Task<MySqlTable> TableAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        if (Tables.TryGetValue(_tableKey, out var kept) && DateTimeOffset.UtcNow - kept.At < ColumnsKept)
        {
            return kept.Table;
        }

        List<MySqlColumn> columns = [];

        await using (MySqlCommand command = new(
            """
            select column_name, data_type, numeric_precision, numeric_scale, character_maximum_length, is_nullable, column_type
              from information_schema.columns
             where table_schema = @db and table_name = @tab
             order by ordinal_position
            """, connection))
        {
            command.Parameters.AddWithValue("@db", _layer.SchemaName);
            command.Parameters.AddWithValue("@tab", _layer.TableName);

            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string name = reader.GetString(0);
                MySqlGuids.Storage? guid = MySqlGuids.Of(
                    name, reader.GetString(1), reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture));
                FieldType type = guid is not null
                    ? FieldType.Guid
                    : TypeOf(
                        reader.GetString(1),
                        reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                        reader.GetString(6));

                if (type == FieldType.Unknown || string.Equals(name, _layer.GeometryColumn, StringComparison.Ordinal))
                {
                    continue;
                }

                int? length = type == FieldType.Text && !reader.IsDBNull(4)
                    && Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture) is var max and > 0 and <= int.MaxValue
                        ? (int)max
                        : null;

                columns.Add(new MySqlColumn(name, type, reader.GetString(5) == "YES", guid is null ? length : null, guid));
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' is served from {_layer.SchemaName}.{_layer.TableName}, and this database user "
                + "sees no such table. It was dropped, renamed, or the user lost the right to read it.");
        }

        int? srid = null;
        bool isTable = true;
        bool points = false;

        await using (MySqlCommand command = new(
            $"""
            select g.srs_id, (select t.table_type from information_schema.tables t
                               where t.table_schema = g.table_schema and t.table_name = g.table_name),
                   g.geometry_type_name
              from {MySqlDialect.Of(connection).GeometryColumns} g
             where g.table_schema = @db and g.table_name = @tab and g.column_name = @col
            """, connection))
        {
            command.Parameters.AddWithValue("@db", _layer.SchemaName);
            command.Parameters.AddWithValue("@tab", _layer.TableName);
            command.Parameters.AddWithValue("@col", _layer.GeometryColumn);

            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                srid = reader.IsDBNull(0) ? null : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                isTable = reader.IsDBNull(1) || reader.GetString(1) == "BASE TABLE";
                points = !reader.IsDBNull(2) && string.Equals(reader.GetString(2), "point", StringComparison.OrdinalIgnoreCase);
            }
        }

        MySqlTable table = new(columns, srid ?? throw new InvalidOperationException(
            $"Layer '{_layer.Name}' is served from {_layer.SchemaName}.{_layer.TableName}.{_layer.GeometryColumn}, which "
            + "is no longer a spatial column with an SRID."), isTable, MySqlDialect.Of(connection), points);

        Tables[_tableKey] = (DateTimeOffset.UtcNow, table);
        return table;
    }

    private bool Projects(FeatureQuery query) => (query.OutSrid is { } srid && srid != _layer.Srid) || query.OutWkt is { Length: > 0 };

    /// <summary>The where clause and its values, shared by every method.</summary>
    private sealed record Filters(
        IReadOnlyList<string> Clauses,
        IReadOnlyList<(string Name, object Value)> Parameters,
        IReadOnlyList<long>? Refined = null);

    private async Task<Filters> FiltersAsync(MySqlConnection connection, FeatureQuery query, MySqlTable table, CancellationToken cancellationToken)
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
            : string.Join(", ", top.OrderBy.Select(k => Ordered(LayerDefinition.Quote(k.Field), k.Descending, Nullable(table, k.Field))));

        StringBuilder ranked = new StringBuilder("select ").Append(Id).Append(" as i, row_number() over (");

        if (top.GroupBy.Count > 0)
        {
            ranked.Append("partition by ").Append(string.Join(", ", top.GroupBy.Select(LayerDefinition.Quote))).Append(' ');
        }

        ranked.Append("order by ").Append(order).Append(", ").Append(Id).Append(") as r from ").Append(Source(query, table)).Append(" t");

        if (filters.Clauses.Count > 0)
        {
            ranked.Append(" where ").Append(string.Join(" and ", filters.Clauses));
        }

        return new Filters(
            [$"{Id} in (select k.i from ({ranked}) k where k.r <= @top_count)"],
            [.. filters.Parameters, ("top_count", (object)top.Count)]);
    }

    private async Task<Filters> RowFiltersAsync(MySqlConnection connection, FeatureQuery query, MySqlTable table, CancellationToken cancellationToken)
    {
        List<string> attributes = [];
        List<(string Name, object Value)> parameters = [];
        List<Envelope> boxes = [];
        Geometry? shape = null;
        SpatialFilter? byEngine = null;

        if (query.BoundingBox is { } box)
        {
            boxes.Add(await ToLayerAsync(Rectangle(box), query, cancellationToken).ConfigureAwait(false) is { IsEmpty: false } moved
                ? moved.Envelope
                : box);
        }

        if (query.Where is { Sql.Length: > 0 } where)
        {
            attributes.Add("(" + Where(where, table, parameters) + ")");
        }

        if (query.Identities.Count > 0)
        {
            parameters.Add(("ids", JsonSerializer.Serialize(query.Identities)));
            attributes.Add($"{Id} in (select j.v from json_table(@ids, '$[*]' columns (v bigint path '$')) j)");
        }

        if (query.Spatial is { } spatial)
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

        List<string> inMySql = [.. attributes];

        for (int i = 0; i < boxes.Count; i++)
        {
            inMySql.Add(BoxClause(boxes[i], "box" + i.ToString(CultureInfo.InvariantCulture), table, parameters));
        }

        if (shape is null && query.BoundingBox is null && query.Spatial is { Relation: SpatialRelation.IndexIntersects })
        {
            return new Filters(inMySql, parameters);
        }

        // <b>A box against points needs nothing taken out of the database.</b> A point is inside an axis-aligned box when
        // each coordinate is between the box's — the answer the exact pass here would give, boundary included, and the
        // one PostGIS gives — so it is written as that comparison beside the index's box test and the whole query stays
        // in MySQL. Measured on the VPS before: a box over the whole region carried 200,000 shapes here and 200,000 ids
        // back, 3.6 times PostGIS's time. A polygon that is not a box, a distance, and any other relation keep the
        // exact pass.
        if (table.Points && byEngine is null && (shape is null || IsBox(shape)))
        {
            List<string> exact = [.. inMySql];

            for (int i = 0; i < boxes.Count; i++)
            {
                exact.Add(InBox(boxes[i], "inbox" + i.ToString(CultureInfo.InvariantCulture), table, parameters));
            }

            return new Filters(exact, parameters);
        }

        List<Geometry>? kept = byEngine is null ? null : [];

        List<long>? matched = await RefineAsync(connection, Source(query, table), table.Dialect, new Filters(inMySql, parameters), boxes, shape, cancellationToken, kept)
            .ConfigureAwait(false);

        if (matched is null)
        {
            return new Filters(inMySql, parameters);
        }

        if (byEngine is not null)
        {
            matched = await EngineRelations.SatisfyingAsync(
                    matched, kept!, byEngine, _layer.Srid, _projector, _engine!, "a registered MySQL database", cancellationToken)
                .ConfigureAwait(false);
        }

        if (matched.Count == 0)
        {
            return new Filters(["1 = 0"], [], matched);
        }

        return new Filters(
            [$"{Id} in (select r.v from json_table(@refined, '$[*]' columns (v bigint path '$')) r)"],
            [("refined", JsonSerializer.Serialize(matched))],
            matched);
    }

    /// <summary>The identities of the candidates whose boxes and shapes meet the filters — the exact pass.</summary>
    private async Task<List<long>?> RefineAsync(
        MySqlConnection connection,
        string from,
        MySqlDialect dialect,
        Filters candidates,
        IReadOnlyList<Envelope> boxes,
        Geometry? shape,
        CancellationToken cancellationToken,
        List<Geometry>? kept = null)
    {
        Statement statement = new(candidates);

        statement.Sql.Append("select ").Append(Id).Append(", ").Append(dialect.Wkb(Shape)).Append(" from ").Append(from).Append(" t");
        statement.AppendWhere();

        List<long> matched = [];

        await using MySqlCommand command = statement.Command(connection, _timeout);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

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
                    $"The filter meets more than {_mostMatched:N0} features of this layer. A MySQL layer holds what a "
                    + "spatial filter matched while it answers, so it bounds how many; narrow the filter.");
            }

            matched.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            kept?.Add(geometry);
        }

        return matched;
    }

    /// <summary><c>MBRIntersects</c> with a rectangle in the column's SRID — the spatial index's box test.</summary>
    /// <remarks>The rectangle is bound as WKT read in longitude–latitude order; in degrees it is kept inside the world.</remarks>
    private string BoxClause(Envelope box, string name, MySqlTable table, List<(string Name, object Value)> parameters)
    {
        if (AxisOrder.IsGeographic(_layer.Srid))
        {
            box = new Envelope(Math.Max(-180, box.MinX), Math.Max(-90, box.MinY), Math.Min(180, box.MaxX), Math.Min(90, box.MaxY));
        }

        string n(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        parameters.Add((name,
            $"POLYGON(({n(box.MinX)} {n(box.MinY)}, {n(box.MaxX)} {n(box.MinY)}, {n(box.MaxX)} {n(box.MaxY)}, "
            + $"{n(box.MinX)} {n(box.MaxY)}, {n(box.MinX)} {n(box.MinY)}))"));

        return $"mbrintersects({Shape}, {table.Dialect.FromText("@" + name, table.Srid)})";
    }

    /// <summary>Whether a filter shape is an axis-aligned rectangle, so its box is the shape.</summary>
    private static bool IsBox(Geometry shape) =>
        shape is Polygon { Holes.Count: 0 } polygon
        && polygon.Shell.Coordinates is { Count: 5 } ring
        && Enumerable.Range(0, 5).All(i => (ring.X(i) == polygon.Envelope.MinX || ring.X(i) == polygon.Envelope.MaxX)
                                           && (ring.Y(i) == polygon.Envelope.MinY || ring.Y(i) == polygon.Envelope.MaxY));

    /// <summary>A point's coordinates between a box's, in the order a client gives them: longitude, latitude.</summary>
    private string InBox(Envelope box, string name, MySqlTable table, List<(string Name, object Value)> parameters)
    {
        (string x, string y) = table.Dialect.Coordinates(Shape, AxisOrder.IsGeographic(table.Srid));
        parameters.Add((name + "x0", box.MinX));
        parameters.Add((name + "x1", box.MaxX));
        parameters.Add((name + "y0", box.MinY));
        parameters.Add((name + "y1", box.MaxY));
        return $"{x} between @{name}x0 and @{name}x1 and {y} between @{name}y0 and @{name}y1";
    }

    private static Polygon Rectangle(Envelope box) =>
        new(new LinearRing(XySequence.Wrap([box.MinX, box.MinY, box.MaxX, box.MinY, box.MaxX, box.MaxY, box.MinX, box.MaxY, box.MinX, box.MinY])));

    /// <summary>The where clause, emitted again from its tree in MySQL's dialect (D-162, ADR-208).</summary>
    private static string Where(ParsedWhere where, MySqlTable table, List<(string Name, object Value)> parameters)
    {
        if (where.Predicate is null)
        {
            throw new InvalidOperationException(
                "A where clause reached a MySQL layer without its parsed predicate, so it cannot be emitted in MySQL's dialect.");
        }

        // A GlobalID is compared as ArcGIS spells one, whatever its column keeps (MySqlGuids).
        Dictionary<string, MySqlGuids.Storage> guids = table.Columns
            .Where(c => c.Guid is not null)
            .ToDictionary(c => c.Name, c => c.Guid!.Value, StringComparer.Ordinal);

        string Quoted(string name) =>
            guids.TryGetValue(name, out MySqlGuids.Storage storage) ? MySqlGuids.Text(LayerDefinition.Quote(name), storage) : LayerDefinition.Quote(name);

        if (!PredicateSql.TryEmit(
                where.Predicate, [.. table.Columns.Select(c => c.Name)], Quoted,
                out ParsedWhere emitted, out string? error, dialect: SqlDialect.MySql.WithGuids(guids.Keys, arcGisText: true)))
        {
            throw new QueryNotSupportedException(error!);
        }

        for (int i = 0; i < emitted.Parameters.Count; i++)
        {
            object? value = emitted.Parameters[i] is Guid id ? MySqlGuids.Braced(id) : emitted.Parameters[i];
            parameters.Add(("w" + i.ToString(CultureInfo.InvariantCulture), value ?? DBNull.Value));
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

    private void AppendOrderAndPaging(Statement statement, FeatureQuery query, FeatureSchema schema, bool distinct, MySqlTable table)
    {
        StringBuilder sql = statement.Sql;

        if (distinct)
        {
            sql.Append(" order by ").Append(string.Join(", ", schema.Names.Select(n => Ordered(LayerDefinition.Quote(n), descending: false, Nullable(table, n)))));
        }
        else if (query.OrderBy.Count > 0)
        {
            sql.Append(" order by ").Append(string.Join(", ", query.OrderBy.Select(k => Ordered(LayerDefinition.Quote(k.Field), k.Descending, Nullable(table, k.Field)))));

            if (!query.OrderBy.Any(k => string.Equals(k.Field, _layer.IdentityColumn, StringComparison.Ordinal)))
            {
                sql.Append(", ").Append(Id);
            }
        }
        else
        {
            sql.Append(" order by ").Append(Id);
        }

        sql.Append(" limit ").Append(statement.Bind("limit", query.Limit));

        if (query.Offset > 0)
        {
            sql.Append(" offset ").Append(statement.Bind("offset", query.Offset));
        }
    }

    private static string Aggregate(StatisticRequest statistic, string? computed = null)
    {
        string column = LayerDefinition.Quote(computed ?? statistic.Field);

        return statistic.Kind switch
        {
            StatisticKind.Count => $"count({column})",
            StatisticKind.Sum => $"sum({column})",
            StatisticKind.Min => $"min({column})",
            StatisticKind.Max => $"max({column})",

            // MySQL averages whole numbers into a DECIMAL kept to four places; PostgreSQL's is exact, and a double here.
            StatisticKind.Avg => $"avg(1e0 * {column})",
            StatisticKind.StdDev => $"stddev_samp(1e0 * {column})",
            StatisticKind.Var => $"var_samp(1e0 * {column})",
            _ => throw new ArgumentOutOfRangeException(nameof(statistic), statistic.Kind, null),
        };
    }

    /// <summary>
    /// A percentile from each row's rank among its group's values and their count, as PostgreSQL defines it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>percentile_disc(p)</c></b> is the first value, in the order asked, whose rank reaches <c>p</c> of the count:
    /// the least such value ascending, the greatest descending.
    /// </para>
    /// <para>
    /// <b><c>percentile_cont(p)</c></b> interpolates between the values at ranks <c>floor</c> and <c>ceil</c> of
    /// <c>1 + p·(n − 1)</c>, by the fraction between them.
    /// </para>
    /// </remarks>
    private static string Percentile(StatisticRequest statistic, string value, string rank, string count)
    {
        string p = Math.Clamp(statistic.Fraction, 0, 1).ToString("0.################", CultureInfo.InvariantCulture);

        if (statistic.Kind == StatisticKind.PercentileDiscrete)
        {
            return $"{(statistic.Descending ? "max" : "min")}(case when {rank} >= ceil({p} * {count}) then {value} end)";
        }

        string position = $"(1 + {p} * ({count} - 1))";
        string lower = $"max(case when {rank} = floor{position} then {value} end)";
        string upper = $"max(case when {rank} = ceil{position} then {value} end)";
        string fraction = $"((1 + {p} * (max({count}) - 1)) - floor(1 + {p} * (max({count}) - 1)))";

        return $"({lower} + {fraction} * ({upper} - {lower}))";
    }

    /// <summary>A MySQL column type as the field list reports it; the rest are not served.</summary>
    /// <remarks><c>tinyint(1)</c> is MySQL's boolean by convention and is served as a small integer, as PostGIS serves a smallint.</remarks>
    internal static FieldType TypeOf(string type, int? scale, string columnType) => type.ToLowerInvariant() switch
    {
        "tinyint" or "smallint" => FieldType.SmallInteger,
        "mediumint" or "int" or "integer" => FieldType.Integer,
        "bigint" => FieldType.BigInteger,
        "decimal" or "numeric" when scale == 0 => FieldType.BigInteger,
        "decimal" or "numeric" or "double" or "real" => FieldType.Double,
        "float" => FieldType.Single,
        "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" or "json" => FieldType.Text,
        "date" or "datetime" or "timestamp" => FieldType.Date,
        "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => FieldType.Binary,
        "bit" when columnType == "bit(1)" => FieldType.Boolean,
        _ => FieldType.Unknown,
    };

    /// <summary>A MySQL value as the faces expect one — the CLR type its column's field type promises.</summary>
    internal static object? Normalise(object? value, FieldType type) => value switch
    {
        null or DBNull => null,

        // A bit(1) column arrives as an unsigned long.
        ulong bit when type == FieldType.Boolean => bit != 0,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal =>
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

        // A GlobalID, as its column keeps it (MySqlGuids).
        string or byte[] when type == FieldType.Guid && MySqlGuids.Read(value) is { } guid => guid,
        _ => value,
    };

    internal sealed record MySqlColumn(string Name, FieldType Type, bool Nullable, int? MaxLength, MySqlGuids.Storage? Guid = null);

    /// <param name="Columns">The table's served columns.</param>
    /// <param name="Srid">The geometry column's reference.</param>
    /// <param name="IsTable">Whether it is a table rather than a view.</param>
    /// <param name="Dialect">Which server: MySQL or MariaDB.</param>
    /// <param name="Points">Whether the column is declared POINT, so a shape's box is the shape (the box filters' exact case).</param>
    internal sealed record MySqlTable(IReadOnlyList<MySqlColumn> Columns, int Srid, bool IsTable, MySqlDialect Dialect, bool Points = false)
    {
        public FieldType TypeOf(string name) =>
            Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.Type ?? FieldType.Unknown;
    }

    /// <summary>A statement being built, with MySqlConnector's named parameters.</summary>
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

        public MySqlCommand Command(MySqlConnection connection, TimeSpan timeout)
        {
            MySqlCommand command = new(Sql.ToString(), connection) { CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds) };

            foreach ((string name, object value) in _parameters)
            {
                command.Parameters.AddWithValue("@" + name, value);
            }

            return command;
        }
    }
}
