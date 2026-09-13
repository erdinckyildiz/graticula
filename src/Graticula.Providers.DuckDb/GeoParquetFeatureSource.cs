using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using SortKey = Graticula.Features.SortKey;

namespace Graticula.Providers.DuckDb;

/// <summary>
/// A layer served from one GeoParquet file, read in place by DuckDB — ADR-066.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and says so.</b> <see cref="LayerDescription.Writable"/> is false, which is what
/// takes Create, Update and Delete out of the layer's capabilities on every face; a GeoParquet
/// file is immutable, and editing one means rewriting it.
/// </para>
/// <para>
/// <b>The box test runs in DuckDB and the exact test runs here.</b> DuckDB's core has no exact
/// spatial predicate — <c>st_intersects_extent</c> compares bounding boxes — and the extension
/// that has one downloads at run time and links GEOS, which this server does not load (ADR-066
/// §2). So a spatial filter is two passes: DuckDB returns the identity and the shape of every row
/// whose box meets the filter's, <see cref="GeometryPredicates.Intersects"/> keeps the ones whose
/// shapes meet, and every other part of the query — order, paging, distinct, statistics, counts —
/// runs in DuckDB again over exactly those identities. PostGIS is the oracle for the second pass,
/// on the corpus the rest of the geometry code is checked against.
/// </para>
/// <para>
/// <b>The where clause is emitted again, not rewritten</b>: the parsed tree travels on
/// <see cref="ParsedWhere.Predicate"/> and <see cref="PredicateSql"/> emits it with DuckDB's
/// placeholders (D-162).
/// </para>
/// <para>
/// <b>Coordinates move through the datastore's PROJ</b> — <see cref="IProjector"/> — for an output
/// reference and for a filter stated in another one, so a GeoParquet layer and a PostGIS layer in
/// the same reference answer with the same numbers.
/// </para>
/// </remarks>
public sealed class GeoParquetFeatureSource
    : IFeatureSource, IFeatureSummaries, IFeatureVersions, ISourceCacheValidator
{
    private const int ProjectionBatch = 512;

    /// <summary>How long one query may run when the service says nothing shorter.</summary>
    /// <remarks>
    /// <b>The PostGIS path's thirty seconds, and for the PostGIS path's reason</b> (ADR-007 §4.8).
    /// A security review found this provider had none: a request deadline of ten minutes was all
    /// that stopped a public query holding a budget slot, a thread and DuckDB's cores, and three
    /// folders' worth of them was the whole server's budget. Measured before relying on it: a
    /// cancelled DuckDB command stops within a few milliseconds of the cancel and its connection
    /// answers the next statement.
    /// </remarks>
    public static readonly TimeSpan DefaultStatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The most features a spatial filter may match before it is refused.</summary>
    /// <remarks>
    /// <b>A bound on memory, because the exact test keeps what it matched.</b> The identities are
    /// the answer the rest of the query is computed over, so they are held — eight bytes each, and a
    /// copy handed to DuckDB. A filter over a whole province of buildings is a legitimate question
    /// and this layer refuses it; the datastore, where the geometry is indexed, does not.
    /// </remarks>
    public const int DefaultMostMatched = 1_000_000;

    /// <summary>The most vertices a spatial filter's geometry may have.</summary>
    /// <remarks>
    /// GeometryServer's cap, for the same reason: the exact test compares every segment of the filter
    /// with every segment of each candidate, in this process, and a caller chooses the filter.
    /// </remarks>
    public const int MostFilterVertices = 130_000;

    private readonly GeoParquetFolder _folder;
    private readonly LayerDefinition _layer;
    private readonly IProjector _projector;
    private readonly TimeSpan _timeout;
    private readonly int _mostMatched;

    /// <summary>Serves one file of a folder as a layer.</summary>
    /// <param name="folder">The folder's sandboxed DuckDB.</param>
    /// <param name="layer">The layer; its table name is the file name without <c>.parquet</c>.</param>
    /// <param name="projector">The datastore's projector.</param>
    /// <param name="statementTimeout">How long one query may run; <see cref="DefaultStatementTimeout"/> when null.</param>
    public GeoParquetFeatureSource(
        GeoParquetFolder folder, LayerDefinition layer, IProjector projector, TimeSpan? statementTimeout = null)
        : this(folder, layer, projector, statementTimeout, DefaultMostMatched)
    {
    }

    internal GeoParquetFeatureSource(
        GeoParquetFolder folder, LayerDefinition layer, IProjector projector, TimeSpan? statementTimeout, int mostMatched)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mostMatched);

        if (statementTimeout is { } given && given <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(statementTimeout), "A statement timeout must be positive.");
        }

        _folder = folder;
        _layer = layer;
        _projector = projector;
        _timeout = statementTimeout ?? DefaultStatementTimeout;
        _mostMatched = mostMatched;
    }

    /// <summary>The relations a GeoParquet layer answers.</summary>
    public static IReadOnlyList<SpatialRelation> Relations { get; } =
        [SpatialRelation.Intersects, SpatialRelation.EnvelopeIntersects, SpatialRelation.IndexIntersects];

    private bool RowNumbers =>
        string.Equals(_layer.IdentityColumn, GeoParquetFolder.RowNumberColumn, StringComparison.Ordinal);

    private string Id => GeoParquetFolder.Quote(_layer.IdentityColumn);

    private string Shape => GeoParquetFolder.Quote(_layer.GeometryColumn);

    /// <inheritdoc />
    public FeatureSchema SchemaFor(FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // <b>Refused here, before a response has started.</b> A face writes its headers after
        // asking for the schema and before the first row, so this is the last point at which a
        // refusal can still be a status code rather than a truncated body.
        Refuse(query);

        return query.Fields.Count == 0 ? FeatureSchema.Empty : new FeatureSchema(query.Fields);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Feature> ReadAsync(
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Refused before the deadline starts, so a refusal is a refusal and never a timeout.
        SchemaFor(query);

        using CancellationTokenSource deadline = Deadline(cancellationToken);

        await using IAsyncEnumerator<Feature> rows =
            ReadBoundedAsync(query, deadline.Token).GetAsyncEnumerator(deadline.Token);

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

    private async IAsyncEnumerable<Feature> ReadBoundedAsync(
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FeatureSchema schema = SchemaFor(query);
        GeoParquetTable table = Table();
        Filters filters = await FiltersAsync(query, table, cancellationToken).ConfigureAwait(false);

        Statement statement = new(filters);
        StringBuilder sql = statement.Sql;

        sql.Append("select ");

        if (query.Distinct && schema.Count > 0)
        {
            sql.Append("distinct on (")
               .Append(string.Join(", ", schema.Names.Select(GeoParquetFolder.Quote)))
               .Append(") ");
        }

        sql.Append(Id);

        foreach (string field in schema.Names)
        {
            sql.Append(", ").Append(GeoParquetFolder.Quote(field));
        }

        sql.Append(query.IncludeGeometry ? $", st_aswkb({Shape})" : ", null::blob");
        sql.Append(" from ").Append(GeoParquetFolder.TableExpression(table, RowNumbers));

        statement.AppendWhere();
        AppendOrderAndPaging(statement, query, schema);

        List<Feature> batch = new(ProjectionBatch);
        bool reshape = query.IncludeGeometry
            && (Projects(query) || query.Precision is >= 0 || query.MaxAllowableOffset is > 0);

        using DuckDBConnection connection = _folder.Open();
        using DuckDBCommand command = statement.Command(connection);
        using CancellationTokenRegistration cancel = cancellationToken.Register(command.Cancel);
        using DuckDBDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.IsDBNull(0))
            {
                throw new InvalidOperationException(
                    $"Layer '{_layer.Name}' has a row whose identity column '{_layer.IdentityColumn}' "
                    + "is null, and it was measured unique and never null when it was published — "
                    + "so the file has been replaced by one it no longer describes.");
            }

            string id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;

            object?[] values = new object?[schema.Count];

            for (int i = 0; i < schema.Count; i++)
            {
                values[i] = reader.IsDBNull(i + 1) ? null : Normalise(reader.GetValue(i + 1));
            }

            int geometryOrdinal = schema.Count + 1;
            Geometry? geometry = reader.IsDBNull(geometryOrdinal)
                ? null
                : WkbReader.Read(Bytes(reader, geometryOrdinal));

            Feature feature = new(id, geometry, schema, values);

            if (!reshape)
            {
                yield return feature;
                continue;
            }

            batch.Add(feature);

            if (batch.Count == ProjectionBatch)
            {
                foreach (Feature done in await ReshapeAsync(batch, query, cancellationToken).ConfigureAwait(false))
                {
                    yield return done;
                }

                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            foreach (Feature done in await ReshapeAsync(batch, query, cancellationToken).ConfigureAwait(false))
            {
                yield return done;
            }
        }
    }

    /// <inheritdoc />
    public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
        Bounded(DescribeBoundedAsync, cancellationToken);

    private async Task<LayerDescription> DescribeBoundedAsync(CancellationToken cancellationToken)
    {
        GeoParquetTable table = Table();

        List<FieldDescription> fields = [];

        if (RowNumbers)
        {
            fields.Add(new FieldDescription(GeoParquetFolder.RowNumberColumn, FieldType.BigInteger, false, null));
        }

        foreach (GeoParquetColumn column in table.Columns)
        {
            fields.Add(new FieldDescription(column.Name, MapType(column.Type), true, null));
        }

        Envelope? extent = table.Geometry.Bbox;

        if (extent is null && table.Geometry.Covering is { } covering)
        {
            extent = await Task.Run(() => CoveringExtent(table, covering), cancellationToken).ConfigureAwait(false);
        }

        // ADR-067 §5.3: a table in an attached database has neither a bbox nor a covering column.
        if (extent is null && table.Relation is not null)
        {
            extent = await Task.Run(() => _folder.AttachedExtent(table), cancellationToken).ConfigureAwait(false);
        }

        return new LayerDescription(fields, extent, Writable: false) { AnswersDistance = false };
    }

    /// <inheritdoc />
    public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => CountBoundedAsync(query, token), cancellationToken);
    }

    private async Task<long> CountBoundedAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);

        GeoParquetTable table = Table();
        Filters filters = await FiltersAsync(query, table, cancellationToken).ConfigureAwait(false);

        if (filters.Refined is { } counted && !(query.Distinct && query.Fields.Count > 0))
        {
            return counted.Count;
        }

        Statement statement = new(filters);

        if (query.Distinct && query.Fields.Count > 0)
        {
            statement.Sql.Append("select count(*) from (select distinct ")
                .Append(string.Join(", ", query.Fields.Select(GeoParquetFolder.Quote)))
                .Append(" from ").Append(GeoParquetFolder.TableExpression(table, RowNumbers));
            statement.AppendWhere();
            statement.Sql.Append(") distinct_rows");
        }
        else
        {
            statement.Sql.Append("select count(*) from ")
                .Append(GeoParquetFolder.TableExpression(table, RowNumbers));
            statement.AppendWhere();
        }

        return Convert.ToInt64(Scalar(statement, cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public Task<long> CountUpToAsync(
        FeatureQuery query, long ceiling, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);
        Refuse(query);
        return Bounded(token => CountUpToBoundedAsync(query, ceiling, token), cancellationToken);
    }

    private async Task<long> CountUpToBoundedAsync(
        FeatureQuery query, long ceiling, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);

        if (query.Distinct && query.Fields.Count > 0)
        {
            return await CountBoundedAsync(query, cancellationToken).ConfigureAwait(false);
        }

        Refuse(query);

        GeoParquetTable table = Table();
        Filters filters = await FiltersAsync(query, table, cancellationToken).ConfigureAwait(false);

        if (filters.Refined is { } counted)
        {
            return Math.Min(counted.Count, ceiling);
        }

        Statement statement = new(filters);

        statement.Sql.Append("select count(*) from (select 1 from ")
            .Append(GeoParquetFolder.TableExpression(table, RowNumbers));
        statement.AppendWhere();
        statement.Sql.Append(" limit ").Append(ceiling.ToString(CultureInfo.InvariantCulture)).Append(") s");

        return Convert.ToInt64(Scalar(statement, cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Computed from the shapes, in this process.</b> DuckDB's core has no <c>st_xmin</c>, and
    /// the covering column is single precision — a box rounded outwards is right for pruning and
    /// wrong for an answer a client zooms to. So the extent is the union of the envelopes of the
    /// matched rows, in the output reference when one is asked for, which is what PostGIS's
    /// <c>st_extent</c> over the output geometry returns.
    /// </remarks>
    public Task<(Envelope? Extent, long Count)> ExtentAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => ExtentBoundedAsync(query, token), cancellationToken);
    }

    private async Task<(Envelope? Extent, long Count)> ExtentBoundedAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);

        GeoParquetTable table = Table();
        Filters filters = await FiltersAsync(query, table, cancellationToken).ConfigureAwait(false);
        Statement statement = new(filters);

        statement.Sql.Append("select st_aswkb(").Append(Shape).Append(") from ")
            .Append(GeoParquetFolder.TableExpression(table, RowNumbers));
        statement.AppendWhere();

        Envelope extent = Envelope.Empty;
        long count = 0;
        List<Geometry> pending = new(ProjectionBatch);

        using (DuckDBConnection connection = _folder.Open())
        using (DuckDBCommand command = statement.Command(connection))
        using (cancellationToken.Register(command.Cancel))
        using (DuckDBDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;

                if (reader.IsDBNull(0))
                {
                    continue;
                }

                Geometry shape = WkbReader.Read(Bytes(reader, 0));

                if (shape.IsEmpty)
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
    public Task<IReadOnlyList<long>> ObjectIdsAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => ObjectIdsBoundedAsync(query, token), cancellationToken);
    }

    private async Task<IReadOnlyList<long>> ObjectIdsBoundedAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);

        GeoParquetTable table = Table();
        Filters filters = await FiltersAsync(query, table, cancellationToken).ConfigureAwait(false);

        if (filters.Refined is { } refined)
        {
            List<long> sorted = [.. refined];
            sorted.Sort();
            return sorted;
        }

        Statement statement = new(filters);

        statement.Sql.Append("select ").Append(Id).Append(" from ")
            .Append(GeoParquetFolder.TableExpression(table, RowNumbers));
        statement.AppendWhere();
        statement.Sql.Append(" order by ").Append(Id);

        List<long> ids = [];

        using DuckDBConnection connection = _folder.Open();
        using DuckDBCommand command = statement.Command(connection);
        using CancellationTokenRegistration cancel = cancellationToken.Register(command.Cancel);
        using DuckDBDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        return ids;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StatisticsAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => StatisticsBoundedAsync(query, token), cancellationToken);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StatisticsBoundedAsync(
        FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);

        GeoParquetTable table = Table();
        Filters filters = await FiltersAsync(query, table, cancellationToken).ConfigureAwait(false);
        Statement statement = new(filters);
        StringBuilder sql = statement.Sql;

        List<string> select = [.. query.GroupBy.Select(GeoParquetFolder.Quote)];

        foreach (StatisticRequest statistic in query.Statistics)
        {
            select.Add($"{Aggregate(statistic)} as {GeoParquetFolder.Quote(statistic.OutName)}");
        }

        sql.Append("select ").Append(string.Join(", ", select)).Append(" from ")
           .Append(GeoParquetFolder.TableExpression(table, RowNumbers));

        statement.AppendWhere();

        if (query.GroupBy.Count > 0)
        {
            sql.Append(" group by ").Append(string.Join(", ", query.GroupBy.Select(GeoParquetFolder.Quote)));

            // The same vocabulary rule as PostgreSQL's path: a sort key naming neither a group nor a
            // statistic of this query is dropped rather than quoted into the statement.
            List<string> ordering = [];

            foreach (SortKey key in query.OrderBy)
            {
                string? named = query.GroupBy
                    .Concat(query.Statistics.Select(s => s.OutName))
                    .FirstOrDefault(n => n.Equals(key.Field, StringComparison.OrdinalIgnoreCase));

                if (named is not null)
                {
                    ordering.Add(key.Descending
                        ? $"{GeoParquetFolder.Quote(named)} desc"
                        : GeoParquetFolder.Quote(named));
                }
            }

            if (ordering.Count == 0)
            {
                ordering.AddRange(query.GroupBy.Select(GeoParquetFolder.Quote));
            }

            sql.Append(" order by ").Append(string.Join(", ", ordering))
               .Append(" limit ").Append(statement.Bind("limit", query.Limit));
        }

        List<IReadOnlyDictionary<string, object?>> rows = [];

        using DuckDBConnection connection = _folder.Open();
        using DuckDBCommand command = statement.Command(connection);
        using CancellationTokenRegistration cancel = cancellationToken.Register(command.Cancel);
        using DuckDBDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, object?> row = [];

            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Normalise(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>The file's version, and the same for every row.</b> A row of an immutable file changes
    /// only when the file does, so an entity tag built from the file's length and modification
    /// time is exact rather than approximate: it changes on every replacement and on nothing else.
    /// </remarks>
    public Task<string?> VersionOfAsync(long identity, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(Table().Version);

    /// <inheritdoc />
    /// <remarks>
    /// <b>The same value <see cref="VersionOfAsync"/> answers, and the same reasoning — ADR-069.</b>
    /// <c>Table()</c> costs a dictionary lookup and, at most, a file stat that <c>GeoParquetFolder</c>
    /// already does to decide whether to reopen the file; nothing here queries DuckDB. That is what
    /// makes it cheap enough to compute before deciding whether to run the query it would validate.
    /// </remarks>
    public Task<string?> CacheValidatorAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(Table().Version);

    /// <summary>Refuses what this provider does not answer, before any work is done.</summary>
    private static void Refuse(FeatureQuery query)
    {
        if (query.Spatial is not { } spatial)
        {
            return;
        }

        if (spatial.Geometry.CoordinateCount > MostFilterVertices)
        {
            throw new QueryNotSupportedException(
                $"The filter geometry has {spatial.Geometry.CoordinateCount:N0} vertices, and a layer served "
                + $"from a GeoParquet file compares it with each feature in this server, so it takes at most "
                + $"{MostFilterVertices:N0}. Generalize it first (GeometryServer/generalize).");
        }

        if (spatial.Distance > 0)
        {
            throw new QueryNotSupportedException(
                "This layer is served from a GeoParquet file, which answers a spatial filter by "
                + "intersection and not within a distance. Buffer the geometry first "
                + "(GeometryServer/buffer) and filter by the result, or publish the data into the "
                + "datastore, where distance is answered.");
        }

        if (!Relations.Contains(spatial.Relation))
        {
            throw new QueryNotSupportedException(
                $"This layer is served from a GeoParquet file, which answers the spatial relations "
                + $"intersects, envelope-intersects and index-intersects, and '{spatial.Relation}' is "
                + "not one of them. It is computed exactly by a database this layer is not in; "
                + "publishing the data into the datastore answers it.");
        }
    }

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        return deadline;
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

    /// <summary>Whether a failure is this provider's own deadline rather than the caller leaving.</summary>
    /// <remarks>
    /// <b>A DuckDB error counts too</b>, because an interrupted statement can surface as the engine's
    /// own exception rather than as a cancellation, depending on where in its pipeline the interrupt
    /// lands — and reporting that as a server fault would send an operator looking for a defect.
    /// </remarks>
    private static bool TimedOut(Exception stopped, CancellationTokenSource deadline, CancellationToken caller) =>
        deadline.IsCancellationRequested
        && !caller.IsCancellationRequested
        && stopped is OperationCanceledException or DuckDBException;

    private TimeoutException Timeout() => new(
        $"A query on layer '{_layer.Name}' ran past the {_timeout.TotalSeconds:0.#} seconds this service "
        + "allows one statement, and DuckDB was stopped. Narrow the extent or the where clause, or import "
        + "the file into the datastore, where the geometry is indexed.");

    private GeoParquetTable Table()
    {
        GeoParquetTable? table = _folder.Find(_layer.TableName);

        if (table is null)
        {
            throw new FileNotFoundException(
                $"Layer '{_layer.Name}' is served from '{_folder.PathOf(_layer.TableName)}', and that "
                + "file is not there. The registration and the folder have diverged; retrying will "
                + "not help.");
        }

        if (table.Problem is { } problem)
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' is served from '{table.Path}', which can no longer be served: "
                + problem);
        }

        if (!string.Equals(table.Geometry.Column, _layer.GeometryColumn, StringComparison.Ordinal)
            || table.Geometry.Srid != _layer.Srid)
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' was published from a file whose geometry was "
                + $"'{_layer.GeometryColumn}' in EPSG:{_layer.Srid}, and '{table.Path}' now has "
                + $"'{table.Geometry.Column}' in EPSG:{table.Geometry.Srid?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}. "
                + "The file has been replaced by a different one; republishing reads it again.");
        }

        // <b>Measured again whenever the file changes, because a file is not a table with a key.</b>
        // The identity was unique when the layer was published; a replacement that duplicates one
        // would make a spatial filter return the duplicate's sibling — the identities carried back
        // from the exact test would match two rows — so a layer whose identity is no longer unique
        // is refused rather than answered.
        if (!table.IdentityCandidates.Contains(_layer.IdentityColumn, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' identifies its features by '{_layer.IdentityColumn}', and in "
                + $"'{table.Path}' as it is now that column is no longer unique and never null. The "
                + "file has been replaced by one that breaks the layer's identity; republish it "
                + $"with one of: {string.Join(", ", table.IdentityCandidates)}.");
        }

        return table;
    }

    private bool Projects(FeatureQuery query) =>
        (query.OutSrid is { } srid && srid != _layer.Srid) || query.OutWkt is { Length: > 0 };

    /// <summary>The parts of a statement every method shares: the where clause and its values.</summary>
    /// <param name="Clauses">The conditions, joined with <c>and</c>.</param>
    /// <param name="Parameters">The values they bind.</param>
    /// <param name="Refined">
    /// When the exact test ran, the identities it kept — already the whole answer to <em>which</em>
    /// and <em>how many</em>, so those two do not scan the file a second time to be told it again.
    /// </param>
    private sealed record Filters(
        IReadOnlyList<string> Clauses,
        IReadOnlyList<DuckDBParameter> Parameters,
        IReadOnlyList<long>? Refined = null);

    private async Task<Filters> FiltersAsync(
        FeatureQuery query, GeoParquetTable table, CancellationToken cancellationToken)
    {
        List<string> attributes = [];
        List<DuckDBParameter> parameters = [];
        List<Envelope> boxes = [];
        Geometry? shape = null;

        if (query.BoundingBox is { } box)
        {
            boxes.Add(await ToLayerAsync(Rectangle(box), query, cancellationToken)
                .ConfigureAwait(false) is { IsEmpty: false } moved
                    ? moved.Envelope
                    : box);
        }

        if (query.Where is { Sql.Length: > 0 } where)
        {
            attributes.Add("(" + Where(where, table, parameters) + ")");
        }

        if (query.Identities.Count > 0)
        {
            parameters.Add(new DuckDBParameter("ids", query.Identities.ToArray()));
            attributes.Add($"{Id} in (select unnest($ids))");
        }

        if (query.Spatial is { } spatial)
        {
            Geometry filter = await ToLayerAsync(spatial.Geometry, query, cancellationToken).ConfigureAwait(false);

            if (filter.IsEmpty)
            {
                return new Filters(["false"], []);
            }

            boxes.Add(filter.Envelope);

            // Envelope and index intersection are the box test; intersects is the shape.
            if (spatial.Relation == SpatialRelation.Intersects)
            {
                shape = filter;
            }
        }

        if (boxes.Count == 0)
        {
            return new Filters(attributes, parameters);
        }

        // The box tests as DuckDB would run them exactly — on the geometry column — kept for the
        // file with no covering column, and for a box-only query too large to answer here.
        List<string> inDuckDb = [.. attributes];

        for (int i = 0; i < boxes.Count; i++)
        {
            inDuckDb.Add(ExtentClause(boxes[i], "box" + i.ToString(CultureInfo.InvariantCulture), parameters));
        }

        List<long>? matched;

        if (table.Geometry.Covering is { } covering)
        {
            List<string> pruned = [.. attributes];

            for (int i = 0; i < boxes.Count; i++)
            {
                pruned.Add(CoveringClause(boxes[i], "cover" + i.ToString(CultureInfo.InvariantCulture), covering, parameters));
            }

            matched = Refine(table, new Filters(pruned, parameters), boxes, shape, cancellationToken);

            if (matched is null)
            {
                // A box-only query past the bound: DuckDB answers it exactly, and slowly, rather
                // than this server holding a list that long or refusing a question it can answer.
                return new Filters(inDuckDb, parameters);
            }
        }
        else if (shape is null)
        {
            return new Filters(inDuckDb, parameters);
        }
        else
        {
            matched = Refine(table, new Filters(inDuckDb, parameters), [], shape, cancellationToken);
        }

        if (matched!.Count == 0)
        {
            return new Filters(["false"], [], matched);
        }

        return new Filters(
            [$"{Id} in (select unnest($refined))"],
            [new DuckDBParameter("refined", matched.ToArray())],
            matched);
    }

    /// <summary>
    /// The identities of the candidates whose boxes and shapes meet the filters — the exact pass.
    /// </summary>
    /// <param name="table">The file.</param>
    /// <param name="candidates">What DuckDB narrows the rows to first.</param>
    /// <param name="boxes">Boxes each geometry's own box must meet, tested here exactly.</param>
    /// <param name="shape">A geometry each must intersect, or null for boxes only.</param>
    /// <param name="cancellationToken">The deadline.</param>
    /// <returns>
    /// The identities, or null when a query of boxes alone matched more than this server holds —
    /// which the caller answers in DuckDB instead. A shape past the bound is refused, because only
    /// this process can answer it.
    /// </returns>
    private List<long>? Refine(
        GeoParquetTable table,
        Filters candidates,
        IReadOnlyList<Envelope> boxes,
        Geometry? shape,
        CancellationToken cancellationToken)
    {
        Statement statement = new(candidates);

        statement.Sql.Append("select ").Append(Id).Append(", st_aswkb(").Append(Shape).Append(") from ")
            .Append(GeoParquetFolder.TableExpression(table, RowNumbers));
        statement.AppendWhere();

        List<long> matched = [];

        using DuckDBConnection connection = _folder.Open();
        using DuckDBCommand command = statement.Command(connection);
        using CancellationTokenRegistration cancel = cancellationToken.Register(command.Cancel);
        using DuckDBDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            Geometry geometry = WkbReader.Read(Bytes(reader, 1));
            Envelope own = geometry.Envelope;
            bool meets = true;

            foreach (Envelope box in boxes)
            {
                if (!own.Intersects(box))
                {
                    meets = false;
                    break;
                }
            }

            if (!meets || (shape is not null && !GeometryPredicates.Intersects(geometry, shape)))
            {
                continue;
            }

            if (matched.Count == _mostMatched)
            {
                if (shape is null)
                {
                    return null;
                }

                throw new QueryNotSupportedException(
                    $"The filter meets more than {_mostMatched:N0} features of this layer. A layer served "
                    + "from a GeoParquet file holds what a spatial filter matched while it answers, so "
                    + "it bounds how many; narrow the filter, or import the file into the datastore, "
                    + "where the geometry is indexed.");
            }

            matched.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        return matched;
    }

    /// <summary>
    /// A box test on the covering column, widened past single-precision rounding — the prefilter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>DuckDB prunes on this and this process decides.</b> The covering column's statistics let
    /// DuckDB skip whole row groups without reading a geometry; its values are single precision, so
    /// it is compared against a box widened outwards, which can only let extra rows through. The
    /// exact box test and the exact shape test then run in <see cref="Refine"/> on the WKB of the
    /// rows that survive.
    /// </para>
    /// <para>
    /// <b>Why the exact box test left DuckDB — measured on a million OSM polygons, 2026-09-13.</b>
    /// With the covering filter alone a small box took 35 ms; adding <c>st_intersects_extent</c> on the
    /// geometry made DuckDB turn the whole geometry column of every surviving row group into its
    /// <c>GEOMETRY</c> type, and the same query took 440 ms. Reading the survivors' WKB and testing
    /// their boxes here took 88 ms, and the second pass over the identities 9.
    /// </para>
    /// <para>
    /// <b>Where PostGIS differs, it is PostGIS that is wide.</b> Its <c>&amp;&amp;</c> compares
    /// boxes rounded outwards to single precision — 1.68 m at the far edge of web Mercator, measured
    /// for Q-20 — so a PostGIS layer can return a feature whose box misses the query's by a metre and
    /// this one will not.
    /// </para>
    /// </remarks>
    private static string CoveringClause(
        Envelope box, string name, string covering, List<DuckDBParameter> parameters)
    {
        string c = GeoParquetFolder.Quote(covering);

        parameters.Add(new DuckDBParameter(name + "_minx", Widen(box.MinX, -1)));
        parameters.Add(new DuckDBParameter(name + "_miny", Widen(box.MinY, -1)));
        parameters.Add(new DuckDBParameter(name + "_maxx", Widen(box.MaxX, 1)));
        parameters.Add(new DuckDBParameter(name + "_maxy", Widen(box.MaxY, 1)));

        return $"(struct_extract({c}, 'xmin') <= ${name}_maxx and "
            + $"struct_extract({c}, 'xmax') >= ${name}_minx and "
            + $"struct_extract({c}, 'ymin') <= ${name}_maxy and "
            + $"struct_extract({c}, 'ymax') >= ${name}_miny)";
    }

    /// <summary>The exact box test in DuckDB, on the geometry column itself.</summary>
    private string ExtentClause(Envelope box, string name, List<DuckDBParameter> parameters)
    {
        parameters.Add(new DuckDBParameter(name, WkbWriter.ToArray(Rectangle(box))));
        return $"st_intersects_extent({Shape}, st_geomfromwkb(${name}))";
    }

    /// <summary>A double moved outwards past any single-precision rounding of it.</summary>
    private static double Widen(double value, int direction) =>
        value + (direction * ((Math.Abs(value) * 1e-6) + 1e-6));

    private static Polygon Rectangle(Envelope box) =>
        new(new LinearRing(XySequence.Wrap(
        [
            box.MinX, box.MinY,
            box.MaxX, box.MinY,
            box.MaxX, box.MaxY,
            box.MinX, box.MaxY,
            box.MinX, box.MinY,
        ])));

    /// <summary>The where clause, emitted again from its tree with DuckDB's placeholders.</summary>
    private string Where(ParsedWhere where, GeoParquetTable table, List<DuckDBParameter> parameters)
    {
        if (where.Predicate is null)
        {
            // Every producer in this repository carries the tree (D-162). A clause without one was
            // built by hand as PostgreSQL text, and pasting it here would be the thing the parser
            // exists to prevent.
            throw new InvalidOperationException(
                "A where clause reached a GeoParquet layer without its parsed predicate, so it "
                + "cannot be emitted in DuckDB's dialect.");
        }

        List<string> columns = [.. table.Columns.Select(c => c.Name)];

        if (RowNumbers)
        {
            columns.Add(GeoParquetFolder.RowNumberColumn);
        }

        if (!PredicateSql.TryEmit(
                where.Predicate, columns, GeoParquetFolder.Quote, out ParsedWhere emitted, out string? error,
                i => "$w" + i.ToString(CultureInfo.InvariantCulture)))
        {
            throw new QueryNotSupportedException(error!);
        }

        for (int i = 0; i < emitted.Parameters.Count; i++)
        {
            parameters.Add(new DuckDBParameter(
                "w" + i.ToString(CultureInfo.InvariantCulture), emitted.Parameters[i] ?? DBNull.Value));
        }

        return emitted.Sql;
    }

    private async Task<Geometry> ToLayerAsync(Geometry geometry, FeatureQuery query, CancellationToken cancellationToken)
    {
        if (query.FilterSrid is not { } from || from == _layer.Srid || geometry.IsEmpty)
        {
            return geometry;
        }

        (IReadOnlyList<Geometry> projected, _) = await _projector
            .ProjectAsync([geometry], from, _layer.Srid, cancellationToken)
            .ConfigureAwait(false);

        return projected[0];
    }

    private async Task<IReadOnlyList<Geometry>> OutputAsync(
        IReadOnlyList<Geometry> geometries, FeatureQuery query, CancellationToken cancellationToken)
    {
        if (query.OutSrid is { } srid && srid != _layer.Srid)
        {
            (IReadOnlyList<Geometry> projected, _) = await _projector
                .ProjectAsync(geometries, _layer.Srid, srid, cancellationToken)
                .ConfigureAwait(false);

            return projected;
        }

        if (query.OutWkt is { Length: > 0 } written)
        {
            return await _projector
                .ProjectToDefinitionAsync(geometries, _layer.Srid, written, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new QueryNotSupportedException(
                    "The projector could not read the written coordinate reference this request "
                    + "asked for.");
        }

        return geometries;
    }

    private async Task<Envelope> ExtentOfAsync(
        List<Geometry> shapes, FeatureQuery query, CancellationToken cancellationToken)
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

    /// <summary>Projects, simplifies and rounds a batch's geometries, keeping everything else.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>maxAllowableOffset</c> is applied, through the same
    /// <c>ST_SimplifyPreserveTopology</c> a PostGIS layer runs.</b> Until 2026-09-13 it was not, on
    /// the argument that the stored shape is within any tolerance and that simplifying here would
    /// be a third simplifier beside two D-236 had measured disagreeing. The second half had
    /// stopped being true two days earlier — D-236 was repaired by making both faces run the same
    /// topology-preserving algorithm — and the first half is true of the parameter and false of
    /// the client: the ArcGIS SDK draws a polygon layer in tiles, sets the tolerance to the tile's
    /// resolution, and a GeoParquet layer handed it 200,159 vertices where 1,808 would do. The map
    /// never finished drawing.
    /// </para>
    /// <para>
    /// <b>Transform, simplify, round, in that order</b> — <c>PostGisFeatureSource</c>'s order,
    /// because the tolerance is in the output's units and rounding is the last thing a
    /// coordinate meets.
    /// </para>
    /// </remarks>
    private async Task<List<Feature>> ReshapeAsync(
        List<Feature> batch, FeatureQuery query, CancellationToken cancellationToken)
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
                moved = await _projector
                    .GeneralizeAsync(shapes, _layer.Srid, srid, tolerance, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // A written reference has no code, so it is moved first and simplified where it lands.
                IReadOnlyList<Geometry> placed = Projects(query)
                    ? await OutputAsync(shapes, query, cancellationToken).ConfigureAwait(false)
                    : shapes;

                moved = await _projector
                    .GeneralizeAsync(placed, _layer.Srid, _layer.Srid, tolerance, cancellationToken)
                    .ConfigureAwait(false);
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
            Geometry shape = query.Precision is { } places and >= 0
                ? Round(moved[k], places)
                : moved[k];

            object?[] values = new object?[old.Schema.Count];

            for (int v = 0; v < values.Length; v++)
            {
                values[v] = old[v];
            }

            result[at[k]] = new Feature(old.Id, shape, old.Schema, values);
        }

        return result;
    }

    /// <summary>Rounds every coordinate to a number of decimal places.</summary>
    /// <remarks>
    /// What <c>geometryPrecision</c> means in the ArcGIS REST documentation: decimal places.
    /// PostGIS's path snaps to a grid with <c>st_reduceprecision</c>, which can also repair or
    /// drop a part a rounding collapsed; this rounds and changes nothing else, so the two agree
    /// on every coordinate and may differ on a sliver that rounding closes.
    /// </remarks>
    internal static Geometry Round(Geometry geometry, int places)
    {
        places = Math.Min(places, 15);

        XySequence Sequence(XySequence coordinates)
        {
            double[] values = coordinates.ToInterleavedArray();

            for (int i = 0; i < values.Length; i++)
            {
                values[i] = Math.Round(values[i], places, MidpointRounding.AwayFromZero);
            }

            return XySequence.Wrap(values);
        }

        LinearRing Ring(LinearRing ring) => ring.IsEmpty ? ring : new LinearRing(Sequence(ring.Coordinates));

        Polygon Area(Polygon polygon) =>
            polygon.IsEmpty ? polygon : new Polygon(Ring(polygon.Shell), [.. polygon.Holes.Select(Ring)]);

        return geometry switch
        {
            { IsEmpty: true } => geometry,
            Point p => new Point(
                Math.Round(p.X, places, MidpointRounding.AwayFromZero),
                Math.Round(p.Y, places, MidpointRounding.AwayFromZero)),
            LinearRing ring => Ring(ring),
            LineString line => new LineString(Sequence(line.Coordinates)),
            Polygon polygon => Area(polygon),
            MultiPoint multi => new MultiPoint([.. multi.Parts.Select(p => (Point)Round(p, places))]),
            MultiLineString multi => new MultiLineString([.. multi.Parts.Select(l => (LineString)Round(l, places))]),
            MultiPolygon multi => new MultiPolygon([.. multi.Parts.Select(Area)]),
            _ => geometry,
        };
    }

    private void AppendOrderAndPaging(Statement statement, FeatureQuery query, FeatureSchema schema)
    {
        StringBuilder sql = statement.Sql;

        if (query.Distinct && schema.Count > 0)
        {
            sql.Append(" order by ").Append(string.Join(", ", schema.Names.Select(GeoParquetFolder.Quote)));

            foreach (SortKey key in query.OrderBy)
            {
                sql.Append(", ").Append(GeoParquetFolder.Quote(key.Field)).Append(key.Descending ? " desc" : " asc");
            }
        }
        else if (query.OrderBy.Count > 0)
        {
            sql.Append(" order by ").Append(string.Join(", ", query.OrderBy.Select(
                k => GeoParquetFolder.Quote(k.Field) + (k.Descending ? " desc" : " asc"))));

            // D-21's tiebreak, for D-21's reason: an order on a column that is not unique is not a
            // total order, and paging over it repeats and skips rows.
            if (!query.OrderBy.Any(k => string.Equals(k.Field, _layer.IdentityColumn, StringComparison.Ordinal)))
            {
                sql.Append(", ").Append(Id);
            }
        }
        else
        {
            // Every limited result is a page, including the first one — D-21 again.
            sql.Append(" order by ").Append(Id);
        }

        sql.Append(" limit ").Append(statement.Bind("limit", query.Limit));

        if (query.Offset > 0)
        {
            sql.Append(" offset ").Append(statement.Bind("offset", query.Offset));
        }
    }

    private static string Aggregate(StatisticRequest statistic)
    {
        string column = GeoParquetFolder.Quote(statistic.Field);

        string function = statistic.Kind switch
        {
            StatisticKind.Count => "count",
            StatisticKind.Sum => "sum",
            StatisticKind.Min => "min",
            StatisticKind.Max => "max",
            StatisticKind.Avg => "avg",
            StatisticKind.StdDev => "stddev_samp",
            StatisticKind.Var => "var_samp",
            StatisticKind.PercentileContinuous => "percentile_cont",
            StatisticKind.PercentileDiscrete => "percentile_disc",
            _ => throw new ArgumentOutOfRangeException(nameof(statistic), statistic.Kind, null),
        };

        if (statistic.Kind is not (StatisticKind.PercentileContinuous or StatisticKind.PercentileDiscrete))
        {
            return $"{function}({column})";
        }

        string fraction = Math.Clamp(statistic.Fraction, 0, 1)
            .ToString("0.################", CultureInfo.InvariantCulture);

        return $"{function}({fraction}) within group (order by {column} {(statistic.Descending ? "desc" : "asc")})";
    }

    private Envelope? CoveringExtent(GeoParquetTable table, string covering)
    {
        string c = GeoParquetFolder.Quote(covering);

        using DuckDBConnection connection = _folder.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText =
            $"select min(struct_extract({c}, 'xmin')), min(struct_extract({c}, 'ymin')), "
            + $"max(struct_extract({c}, 'xmax')), max(struct_extract({c}, 'ymax')) "
            + $"from {GeoParquetFolder.TableExpression(table, false)}";

        using DuckDBDataReader reader = command.ExecuteReader();

        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return new Envelope(
            Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture));
    }

    private object? Scalar(Statement statement, CancellationToken cancellationToken)
    {
        using DuckDBConnection connection = _folder.Open();
        using DuckDBCommand command = statement.Command(connection);
        using CancellationTokenRegistration cancel = cancellationToken.Register(command.Cancel);

        object? value = command.ExecuteScalar();
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    private static byte[] Bytes(DuckDBDataReader reader, int ordinal)
    {
        object value = reader.GetValue(ordinal);

        if (value is byte[] bytes)
        {
            return bytes;
        }

        using Stream stream = (Stream)value;
        byte[] copy = new byte[stream.Length];
        stream.ReadExactly(copy);
        return copy;
    }

    /// <summary>A DuckDB type, as the field list reports it.</summary>
    internal static FieldType MapType(string type) => GeoParquetFolder.BaseType(type) switch
    {
        "BOOLEAN" => FieldType.Boolean,
        "TINYINT" or "SMALLINT" or "UTINYINT" => FieldType.SmallInteger,
        "INTEGER" or "USMALLINT" => FieldType.Integer,
        "BIGINT" or "UINTEGER" => FieldType.BigInteger,
        "FLOAT" => FieldType.Single,
        "DOUBLE" or "DECIMAL" or "UBIGINT" or "HUGEINT" or "UHUGEINT" => FieldType.Double,
        "VARCHAR" => FieldType.Text,
        "UUID" => FieldType.Guid,
        "BLOB" => FieldType.Binary,
        "DATE" or "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMPTZ" or "TIMESTAMP_S"
            or "TIMESTAMP_MS" or "TIMESTAMP_NS" or "TIME" => FieldType.Date,
        _ => FieldType.Unknown,
    };

    /// <summary>
    /// A DuckDB value as the faces expect one — the shapes Npgsql hands the PostGIS path.
    /// </summary>
    /// <remarks>
    /// <b>Found by type, not assumed.</b> DuckDB.NET answers a <c>DATE</c> as <c>DateOnly</c>, a
    /// <c>TIME</c> as <c>TimeOnly</c>, a <c>HUGEINT</c> as <c>BigInteger</c>, a <c>BLOB</c> as a
    /// stream over native memory that is gone once the reader moves on, and a list or a struct as
    /// a .NET collection — measured 2026-09-13. None of those is a shape a response writer was
    /// written for, and the stream is not even safe to keep.
    /// </remarks>
    internal static object? Normalise(object? value) => value switch
    {
        null or DBNull => null,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        TimeOnly time => time.ToTimeSpan(),
        sbyte small => (short)small,
        byte small => (short)small,
        ushort medium => (int)medium,
        uint large => (long)large,
        ulong huge => (double)huge,
        BigInteger huge => (double)huge,
        Stream stream => ReadAll(stream),
        System.Collections.IDictionary or System.Collections.IList => JsonSerializer.Serialize(value),
        _ => value,
    };

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        {
            byte[] copy = new byte[stream.Length];
            stream.ReadExactly(copy);
            return copy;
        }
    }

    /// <summary>A statement being built, with DuckDB's named parameters.</summary>
    private sealed class Statement(Filters filters)
    {
        private readonly List<DuckDBParameter> _parameters = [.. filters.Parameters];

        public StringBuilder Sql { get; } = new();

        public string Bind(string name, object value)
        {
            _parameters.Add(new DuckDBParameter(name, value));
            return "$" + name;
        }

        public void AppendWhere()
        {
            if (filters.Clauses.Count > 0)
            {
                Sql.Append(" where ").Append(string.Join(" and ", filters.Clauses));
            }
        }

        public DuckDBCommand Command(DuckDBConnection connection)
        {
            DuckDBCommand command = connection.CreateCommand();
            command.CommandText = Sql.ToString();

            foreach (DuckDBParameter parameter in _parameters)
            {
                command.Parameters.Add(new DuckDBParameter(parameter.ParameterName, parameter.Value));
            }

            return command;
        }
    }
}
