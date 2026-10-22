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
using Oracle.ManagedDataAccess.Client;
using SortKey = Graticula.Features.SortKey;

namespace Graticula.Providers.Oracle;

/// <summary>
/// A layer served from one SDO_GEOMETRY table of a registered Oracle database — ADR-205; its writer is OracleFeatureWriter.
/// </summary>
/// <remarks>
/// <para>
/// <b>GeoParquetFeatureSource's shape, with Oracle's index in place of DuckDB's covering column.</b> A spatial filter
/// is two passes: <c>SDO_FILTER</c> asks the spatial index which rows' boxes meet the filter's, and
/// <see cref="GeometryPredicates.Intersects"/> decides here which of their shapes do. Every other part of the query —
/// order, paging, distinct, statistics, counts — then runs in Oracle again over exactly those identities.
/// </para>
/// <para>
/// <b>Why not <c>SDO_ANYINTERACT</c> — ADR-205 condition 8.</b> Oracle's exact operators check validity by Oracle's
/// rules, which are stricter than PostGIS's: the customer-shaped test table has a land polygon PostGIS calls valid and
/// Oracle refuses with ORA-13356, and <c>RECTIFY_GEOMETRY</c> moves its area by 1.2 %. <c>SDO_FILTER</c> reads only
/// the index's boxes, and the exact test here is the one every other layer of this server is checked against, so an
/// Oracle layer and a PostGIS layer holding the same rows answer the same.
/// </para>
/// <para>
/// <b>Geometry crosses as WKB</b> (<c>SDO_UTIL.TO_WKBGEOMETRY</c>), and coordinates move through the datastore's PROJ
/// (<see cref="IProjector"/>) — no Oracle type leaves this project.
/// </para>
/// </remarks>
public sealed class OracleFeatureSource : IFeatureSource, IFeatureSummaries, IFeatureVersions, IGeometryStatistics
{
    private const int ProjectionBatch = 512;

    /// <summary>How long one query may run when the service says nothing shorter — the PostGIS path's thirty seconds.</summary>
    public static readonly TimeSpan DefaultStatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The most features a filter may match before it is refused — GeoParquet's bound, for its reason.</summary>
    public const int DefaultMostMatched = 1_000_000;

    /// <summary>The most vertices a spatial filter's geometry may have — GeometryServer's cap.</summary>
    public const int MostFilterVertices = 130_000;

    /// <summary>How long a table's columns and SRID are remembered before they are read again.</summary>
    private static readonly TimeSpan ColumnsKept = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, OracleTable Table)> Tables = new(StringComparer.Ordinal);

    private readonly string _connectionString;
    private readonly string _tableKey;
    private readonly LayerDefinition _layer;
    private readonly IProjector _projector;
    private readonly TimeSpan _timeout;
    private readonly int _mostMatched;
    private readonly IGeometryEngine? _engine;

    /// <summary>Serves one table of a registered Oracle database as a layer.</summary>
    /// <param name="locator">The data source's <c>oracle:</c> locator.</param>
    /// <param name="layer">The layer; its schema is the table's owner.</param>
    /// <param name="projector">The datastore's projector.</param>
    /// <param name="statementTimeout">How long one query may run; <see cref="DefaultStatementTimeout"/> when null.</param>
    /// <param name="engine">
    /// The geometry engine, which decides a distance and every relation beyond intersection (Core's
    /// <see cref="EngineRelations"/>, as for a GeoParquet layer); null answers intersection only.
    /// </param>
    public OracleFeatureSource(
        string locator, LayerDefinition layer, IProjector projector, TimeSpan? statementTimeout = null,
        IGeometryEngine? engine = null)
        : this(locator, layer, projector, statementTimeout, DefaultMostMatched, engine)
    {
    }

    internal OracleFeatureSource(
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

        _connectionString = OracleConnections.For(locator, pooling: true);
        _tableKey = OracleLocator.DatabaseKey(locator) + "|" + layer.SchemaName + "." + layer.TableName + "." + layer.GeometryColumn;
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
        Tables.TryRemove(OracleLocator.DatabaseKey(locator) + "|" + layer.SchemaName + "." + layer.TableName + "." + layer.GeometryColumn, out _);
    }

    /// <summary>The relations an Oracle layer answers itself; the rest are the geometry engine's (EngineRelations).</summary>
    public static IReadOnlyList<SpatialRelation> Relations { get; } =
        [SpatialRelation.Intersects, SpatialRelation.EnvelopeIntersects, SpatialRelation.IndexIntersects];

    private string Id => LayerDefinition.Quote(_layer.IdentityColumn);

    private string Shape => LayerDefinition.Quote(_layer.RequireGeometryColumn());

    private string From => _layer.QuotedTable;

    /// <summary>
    /// What a query reads: the table, or — with a <c>historicMoment</c> — the layer's history as it was then (ADR-207,
    /// <see cref="OracleFeatureHistory.AtMoment"/>). The moment is written as a literal this code formats from a
    /// <see cref="DateTimeOffset"/>, so every statement a query builds carries it without a bind to thread through.
    /// </summary>
    private string Source(FeatureQuery query, OracleTable table) =>
        query.HistoricMoment is not { } moment
            ? From
            : OracleFeatureHistory.AtMoment(
                _layer,
                [.. table.Columns.Select(c => (c.Name, c.Type, c.Guid))],
                "to_timestamp_tz('" + moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                + " +00:00', 'YYYY-MM-DD\"T\"HH24:MI:SS.FF6 TZH:TZM')");

    /// <summary>
    /// The shape as Oracle's WKB can carry it: itself on a layer of x and y, and flattened (<c>SDO_CS.MAKE_2D</c>) on
    /// one with Z or M, whose own WKB is not WKB (<see cref="SdoShape"/>).
    /// </summary>
    private string Flat(OracleTable table) => table.Ordinates == GeometryOrdinates.None ? Shape : $"sdo_cs.make_2d({Shape})";

    /// <inheritdoc />
    public FeatureSchema SchemaFor(FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Refused before a response has started, as GeoParquetFeatureSource does.
        Refuse(query);

        return query.Fields.Count == 0 ? FeatureSchema.Empty : new FeatureSchema(query.Fields);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Feature> ReadAsync(
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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

        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        OracleTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);

        Statement statement = new(filters);
        StringBuilder sql = statement.Sql;
        bool distinct = query.Distinct && schema.Count > 0;

        // <b>Oracle has no `distinct on`.</b> A distinct query is grouped by its fields and answers each group with its
        // lowest identity and no shape — ArcGIS returns no geometry with returnDistinctValues either.
        // <b>Every selected column has its own alias</b>, because the faces ask for the identity among the fields as
        // well, and a select list naming a column twice makes Oracle's `order by` on it ambiguous (ORA-00960 — found
        // through the FeatureServer, which every query of it did).
        string selected = string.Concat(schema.Names.Select(
            (field, i) => $", {LayerDefinition.Quote(field)} as c{i.ToString(CultureInfo.InvariantCulture)}"));

        bool parted = !distinct && query.IncludeGeometry && (table.Ordinates & query.KeepOrdinates) != GeometryOrdinates.None;

        // <b>A page is chosen first, and only its shapes are converted.</b> Oracle evaluates SDO_UTIL.TO_WKBGEOMETRY for
        // every row it sorts, before the offset and the fetch take the page: on the VPS, 200,000 points ordered by their
        // key took 28 s for the first thousand and 23 s for the thousand at offset 100,000, where PostGIS took 78 ms.
        // So a read asks for identities and attributes first, in its order, and for the shapes of each thousand of those
        // rows as they arrive — a map drawn from 200,000 points timed out at 30 s before this, in one statement.
        // <b>Points need neither pass</b>: where the index promises points and no Z or M is asked for, a row's shape is
        // its two numbers, read with its attributes — the second pass cost a page of a thousand 52 ms on the VPS, and a
        // single feature by its id 78 ms against 28 ms in one statement.
        // A table answers every row without a shape (ADR-213), as the PostGIS source does.
        bool shaped = !_layer.IsTable && (query.IncludeGeometry || query.ReturnCentroid);
        bool inlinePoints = !distinct && table.Points && !parted && shaped;
        bool twoPass = !distinct && !inlinePoints && shaped;

        if (distinct)
        {
            sql.Append("select min(").Append(Id).Append(") as i").Append(selected).Append(", null as g from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
            sql.Append(" group by ").Append(string.Join(", ", schema.Names.Distinct(StringComparer.Ordinal).Select(LayerDefinition.Quote)));
        }
        else
        {
            sql.Append("select ").Append(Id).Append(" as i").Append(selected);
            // A centroid is the output shape's label point, so the shape is read for it even when it is not returned.
            sql.Append(inlinePoints
                ? $", {PointOrdinate(1)} as g, {PointOrdinate(2)} as gy"
                : shaped && !twoPass ? $", sdo_util.to_wkbgeometry({Flat(table)}) as g" : ", null as g");

            // Z and M come from the shape's parts, because Oracle's WKB of a shape with them is not WKB (SdoShape).
            if (parted && !twoPass)
            {
                sql.Append(", ").Append(SdoShape.PartsSql("t", Shape)).Append(" as z");
            }

            sql.Append(" from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
        }

        AppendOrderAndPaging(statement, query, schema, distinct);

        FieldType[] types = [.. schema.Names.Select(table.TypeOf)];
        List<Feature> batch = new(ProjectionBatch);
        bool reshape = shaped && !distinct
            && (Projects(query) || query.Precision is >= 0 || query.MaxAllowableOffset is > 0);

        await using OracleCommand command = statement.Command(connection, _timeout);
        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        reader.SuppressGetDecimalInvalidCastException = true;
        List<Feature>? paged = twoPass ? [] : null;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                throw new InvalidOperationException(
                    $"Layer '{_layer.Name}' has a row whose identity column '{_layer.IdentityColumn}' is null, and it "
                    + "is the table's primary key.");
            }

            string id = Convert.ToInt64(reader.GetDecimal(0), CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);

            object?[] values = new object?[schema.Count];

            for (int i = 0; i < schema.Count; i++)
            {
                values[i] = reader.IsDBNull(i + 1) ? null : Normalise(reader.GetValue(i + 1), types[i]);
            }

            int geometryOrdinal = schema.Count + 1;
            Geometry? geometry = reader.IsDBNull(geometryOrdinal)
                ? null
                : inlinePoints
                    ? new Point(Convert.ToDouble(reader.GetValue(geometryOrdinal), CultureInfo.InvariantCulture),
                        Convert.ToDouble(reader.GetValue(geometryOrdinal + 1), CultureInfo.InvariantCulture))
                : parted && !reader.IsDBNull(geometryOrdinal + 1)
                    && SdoShape.ToWkb(SdoShape.ParseParts(reader.GetString(geometryOrdinal + 1))) is { } full
                        ? WkbReader.Read(full, query.KeepOrdinates, out _)
                        : WkbReader.Read((byte[])reader.GetValue(geometryOrdinal));

            Feature feature = new(id, geometry, schema, values);
            IReadOnlyList<Feature> ready = [feature];

            // In two passes the rows wait for their shapes, a chunk at a time, while this reader stays open: Oracle keeps
            // two cursors on one connection, so memory is a chunk whatever the read's size.
            if (paged is not null)
            {
                paged.Add(feature);

                if (paged.Count < ShapeChunk)
                {
                    continue;
                }

                ready = await WithShapesAsync(connection, query, table, schema, paged, parted, cancellationToken).ConfigureAwait(false);
                paged.Clear();
            }

            foreach (Feature one in ready)
            {
                if (!reshape)
                {
                    yield return Finished(one, query);
                    continue;
                }

                batch.Add(one);

                if (batch.Count == ProjectionBatch)
                {
                    foreach (Feature done in await ReshapeAsync(batch, query, cancellationToken).ConfigureAwait(false))
                    {
                        yield return Finished(done, query);
                    }

                    batch.Clear();
                }
            }
        }

        if (paged is { Count: > 0 })
        {
            foreach (Feature one in await WithShapesAsync(connection, query, table, schema, paged, parted, cancellationToken).ConfigureAwait(false))
            {
                if (!reshape)
                {
                    yield return Finished(one, query);
                    continue;
                }

                batch.Add(one);

                if (batch.Count == ProjectionBatch)
                {
                    foreach (Feature done in await ReshapeAsync(batch, query, cancellationToken).ConfigureAwait(false))
                    {
                        yield return Finished(done, query);
                    }

                    batch.Clear();
                }
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

    /// <summary>A point's x (1) or y (2), from SDO_POINT where it is there and from its ordinates where it is not.</summary>
    private string PointOrdinate(int axis)
    {
        string name = axis == 1 ? "x" : "y";
        return $"coalesce(t.{Shape}.sdo_point.{name}, sdo_geom.sdo_min_mbr_ordinate(t.{Shape}, {axis.ToString(CultureInfo.InvariantCulture)}))";
    }

    /// <summary>How many rows wait for their shapes at once in a two-pass read.</summary>
    private const int ShapeChunk = 1_000;

    /// <summary>A chunk of rows with their shapes, in the chunk's order.</summary>
    private async Task<IReadOnlyList<Feature>> WithShapesAsync(
        OracleConnection connection, FeatureQuery query, OracleTable table, FeatureSchema schema, List<Feature> rows, bool parted,
        CancellationToken cancellationToken)
    {
        Dictionary<string, Geometry?> shapes = await ShapesOfAsync(
            connection, query, table, [.. rows.Select(f => long.Parse(f.Id, CultureInfo.InvariantCulture))], parted, cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(bare => new Feature(
            bare.Id, shapes.GetValueOrDefault(bare.Id), schema, [.. Enumerable.Range(0, schema.Count).Select(i => bare[i])]))];
    }

    /// <summary>The shapes of a page's rows, by identity — the second pass of a bounded read.</summary>
    private async Task<Dictionary<string, Geometry?>> ShapesOfAsync(
        OracleConnection connection, FeatureQuery query, OracleTable table, IReadOnlyList<long> ids, bool parted,
        CancellationToken cancellationToken)
    {
        // <b>Points are their two numbers</b>, where the index promises points and no Z or M is asked for; and <b>the ids
        // drive the join</b>, so each is one probe of the key's index. Written as `in (select … from json_table(…))` this
        // cost Oracle 330 ms a page on the VPS — the shapes converted first and matched after — against 20 ms for the
        // same rows by their keys.
        bool points = table.Points && !parted;
        StringBuilder sql = new($"select t.{Id}, ");

        sql.Append(points
            ? $"{PointOrdinate(1)}, {PointOrdinate(2)}"
            : $"sdo_util.to_wkbgeometry({Flat(table).Replace(Shape, "t." + Shape, StringComparison.Ordinal)})");

        if (parted)
        {
            sql.Append(", ").Append(SdoShape.PartsSql("t", Shape));
        }

        sql.Append(" from json_table(:gids, '$[*]' columns (v number path '$')) j join ").Append(Source(query, table))
           .Append(" t on t.").Append(Id).Append(" = j.v");

        // The WKB comes inline with its row, as Statement.Command has it: without this ODP.NET fetches each BLOB in a round
        // trip of its own, and a page of 1,421 shapes took 28 s over the test tunnel (found with dotnet-stack).
        await using OracleCommand command = new(sql.ToString(), connection)
        {
            BindByName = true,
            CommandTimeout = (int)Math.Ceiling(_timeout.TotalSeconds),
            InitialLOBFetchSize = -1,
        };

        command.Parameters.Add("gids", OracleDbType.Clob, JsonSerializer.Serialize(ids), System.Data.ParameterDirection.Input);

        Dictionary<string, Geometry?> shapes = new(ids.Count, StringComparer.Ordinal);

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        reader.SuppressGetDecimalInvalidCastException = true;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string id = Convert.ToInt64(reader.GetDecimal(0), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

            shapes[id] = reader.IsDBNull(1)
                ? null
                : points
                    ? new Point(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture), Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture))
                : parted && !reader.IsDBNull(2) && SdoShape.ToWkb(SdoShape.ParseParts(reader.GetString(2))) is { } full
                    ? WkbReader.Read(full, query.KeepOrdinates, out _)
                    : WkbReader.Read((byte[])reader.GetValue(1));
        }

        return shapes;
    }

    /// <summary>
    /// A feature as the face receives it: with its centroid when one was asked for, and without its shape when only the
    /// centroid needed it.
    /// </summary>
    /// <remarks>
    /// <b>The centroid is the output shape's label point</b> — <see cref="GeometryMeasures.LabelPoint"/>, a point inside
    /// the polygon, which is what PostGIS's <c>ST_PointOnSurface</c> over the output geometry answers on a PostGIS layer
    /// and what ArcGIS calls a label point; a point's centroid is itself, and a line has none.
    /// </remarks>
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
    /// <b>A row's version is its <c>ORA_ROWSCN</c></b> — the system change number of the commit that last wrote it, as
    /// PostgreSQL's <c>xmin</c> is the transaction's. Unless the table was made with <c>ROWDEPENDENCIES</c> Oracle keeps
    /// it per block, so a neighbour's commit moves it too: an edit conditional on it can be refused when nothing of its
    /// own changed, and is never let through when something did — the safe side of D-186.
    /// </remarks>
    public Task<string?> VersionOfAsync(long identity, CancellationToken cancellationToken) =>
        Bounded(async token =>
        {
            await using OracleConnection connection = await OpenAsync(token).ConfigureAwait(false);
            await using OracleCommand command = new(
                $"select to_char(ora_rowscn) from {From} where {Id} = :id", connection) { BindByName = true };

            command.Parameters.Add("id", OracleDbType.Int64, identity, System.Data.ParameterDirection.Input);

            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// <b>Measured from the first rows, because Oracle's statistics do not know</b> — measured 2026-10-20: the dictionary
    /// leaves an <c>SDO_GEOMETRY</c> column's <c>AVG_COL_LEN</c> empty, and <c>AVG_ROW_LEN</c> counts 20 bytes for a row
    /// whose ordinates, stored out of line, are 580 KB. So the width is the WKB length of up to 200 shapes, and the
    /// row count is the dictionary's (the last <c>DBMS_STATS</c>), or counted when the table has never been analysed.
    /// </remarks>
    public Task<GeometryWidth?> GeometryWidthAsync(CancellationToken cancellationToken) =>
        // A table has no geometry to be wide (ADR-213).
        _layer.IsTable ? Task.FromResult<GeometryWidth?>(null) : Bounded<GeometryWidth?>(async token =>
        {
            await using OracleConnection connection = await OpenAsync(token).ConfigureAwait(false);
            OracleTable table = await TableAsync(connection, token).ConfigureAwait(false);

            await using OracleCommand command = new(
                $"""
                -- COALESCE stops at the first value, so a table that was analysed is not counted.
                select coalesce((select num_rows from all_tables where owner = :o and table_name = :t),
                                (select count(*) from {From})),
                       (select avg(dbms_lob.getlength(sdo_util.to_wkbgeometry({Flat(table)})))
                          from (select {Shape} from {From} where {Shape} is not null and rownum <= 200))
                  from dual
                """, connection) { BindByName = true };

            command.Parameters.Add("o", OracleDbType.Varchar2, _layer.SchemaName, System.Data.ParameterDirection.Input);
            command.Parameters.Add("t", OracleDbType.Varchar2, _layer.TableName, System.Data.ParameterDirection.Input);

            await using OracleDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            float rows = Convert.ToSingle(reader.GetValue(0), CultureInfo.InvariantCulture);
            int? each = reader.IsDBNull(1) ? null : (int)Math.Ceiling(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture));

            return new GeometryWidth(rows, each, each is { } bytes ? (long)(bytes * (double)rows) : 0);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
        Bounded(DescribeBoundedAsync, cancellationToken);

    private async Task<LayerDescription> DescribeBoundedAsync(CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        OracleTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);

        List<FieldDescription> fields = [.. table.Columns.Select(c => new FieldDescription(
            c.Name, c.Type, c.Nullable && !string.Equals(c.Name, _layer.IdentityColumn, StringComparison.Ordinal), c.MaxLength))];

        // ADR-205 phase 2: editable where the connected user may write the table, and read-only otherwise.
        bool writable = await OracleConnections.CanWriteAsync(connection, _layer.SchemaName, _layer.TableName, cancellationToken)
            .ConfigureAwait(false);

        HistoryKept? kept = await OracleFeatureHistory.KeptAsync(connection, _layer, cancellationToken).ConfigureAwait(false);

        Envelope? extent = _layer.IsTable ? null : await ExtentOfTableAsync(connection, cancellationToken).ConfigureAwait(false);

        return new LayerDescription(fields, extent, writable)
        {
            AnswersDistance = _engine is not null,
            AnswersHaving = true,
            AnswersTopFeatures = true,
            AnswersCentroid = true,
            AnswersStatisticExpressions = true,
            AnswersRelations = _engine is not null,
            SpatiallyIndexed = _layer.IsTable ? null : true,
            StoredOrdinates = table.Ordinates,

            // ADR-207: a history kept beside the table, which historicMoment reads.
            Archived = kept is not null,
            ArchivedSince = kept?.Since,
            HistoryKeepDays = kept?.KeepDays,
        };
    }

    /// <summary>The table's extent, as its spatial index holds it (<c>SDO_TUNE.EXTENT_OF</c>), so no row is read.</summary>
    private async Task<Envelope?> ExtentOfTableAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        await using OracleCommand command = new(
            "select t.x, t.y from table(sdo_util.getvertices(sdo_tune.extent_of(:t, :c))) t", connection)
        {
            BindByName = true,
            CommandTimeout = (int)Math.Ceiling(_timeout.TotalSeconds),
        };

        command.Parameters.Add("t", OracleDbType.Varchar2, _layer.SchemaName + "." + _layer.TableName, System.Data.ParameterDirection.Input);
        command.Parameters.Add("c", OracleDbType.Varchar2, _layer.RequireGeometryColumn(), System.Data.ParameterDirection.Input);

        List<(double X, double Y)> corners = [];

        // <b>An index it cannot ask is an unknown extent, not a failed describe</b> — a table with no spatial index (a
        // layer's is checked at registration) or an empty one. The describe is what every face starts from.
        try
        {
            await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    corners.Add((Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture),
                        Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture)));
                }
            }
        }
        catch (OracleException)
        {
            return null;
        }

        return corners.Count == 0
            ? null
            : new Envelope(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
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
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        OracleTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        bool distinct = query.Distinct && query.Fields.Count > 0;

        if (filters.Refined is { } counted && !distinct)
        {
            return ceiling is { } most ? Math.Min(counted.Count, most) : counted.Count;
        }

        Statement statement = new(filters);

        if (distinct)
        {
            statement.Sql.Append("select count(*) from (select distinct ")
                .Append(string.Join(", ", query.Fields.Select(LayerDefinition.Quote)))
                .Append(" from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
            statement.Sql.Append(')');
        }
        else if (ceiling is { } most)
        {
            statement.Sql.Append("select count(*) from (select 1 from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
            statement.Sql.Append(" fetch first ").Append(statement.Bind("ceiling", most)).Append(" rows only)");
        }
        else
        {
            statement.Sql.Append("select count(*) from ").Append(Source(query, table)).Append(" t");
            statement.AppendWhere();
        }

        await using OracleCommand command = statement.Command(connection, _timeout);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Computed from the shapes, in this process</b>, in the output reference when one is asked for — which is what
    /// PostGIS's <c>st_extent</c> over the output geometry returns, and GeoParquetFeatureSource's reason.
    /// </remarks>
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

        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        OracleTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        Statement statement = new(filters);

        statement.Sql.Append("select sdo_util.to_wkbgeometry(").Append(Flat(table)).Append(") from ").Append(Source(query, table)).Append(" t");
        statement.AppendWhere();

        Envelope extent = Envelope.Empty;
        long count = 0;
        List<Geometry> pending = new(ProjectionBatch);

        await using (OracleCommand command = statement.Command(connection, _timeout))
        await using (OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                count++;

                if (reader.IsDBNull(0))
                {
                    continue;
                }

                Geometry shape = WkbReader.Read((byte[])reader.GetValue(0));

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
    public Task<IReadOnlyList<long>> ObjectIdsAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Refuse(query);
        return Bounded(token => ObjectIdsBoundedAsync(query, token), cancellationToken);
    }

    private async Task<IReadOnlyList<long>> ObjectIdsBoundedAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        OracleTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
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

        await using OracleCommand command = statement.Command(connection, _timeout);
        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(Convert.ToInt64(reader.GetDecimal(0), CultureInfo.InvariantCulture));
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
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        OracleTable table = await TableAsync(connection, cancellationToken).ConfigureAwait(false);
        Filters filters = await FiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);
        Statement statement = new(filters);
        StringBuilder sql = statement.Sql;
        List<string> columns = [.. table.Columns.Select(c => c.Name)];

        // <b>Expressions are computed in an inner select and grouped and aggregated outside it</b>, by name. Oracle
        // compares a GROUP BY expression with the select list's as text, and one carrying a bind variable does not match
        // its own copy (ORA-00979); a named column of the inner select does.
        List<string> inner = ["t.*"];
        List<string> groups = [.. query.GroupBy.Select(LayerDefinition.Quote)];

        int expressions = 0;

        string Bound(BoundExpression expression)
        {
            string prefix = "x" + expressions.ToString(CultureInfo.InvariantCulture);
            expressions++;

            if (!PredicateSql.TryEmitExpression(
                    expression.Operand!, columns, LayerDefinition.Quote, i => $":{prefix}_{i.ToString(CultureInfo.InvariantCulture)}",
                    out string written, out IReadOnlyList<object?> values, out string? error, SqlDialect.Oracle))
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

        for (int k = 0; k < query.Statistics.Count; k++)
        {
            StatisticRequest statistic = query.Statistics[k];
            string? computed = null;

            if (statistic.Expression is { } expression)
            {
                computed = "graticula_s" + k.ToString(CultureInfo.InvariantCulture);
                inner.Add($"({Bound(expression)}) as {LayerDefinition.Quote(computed)}");
            }

            select.Add($"{Aggregate(statistic, computed)} as {LayerDefinition.Quote(statistic.OutName)}");
        }

        sql.Append("select ").Append(string.Join(", ", select))
           .Append(" from (select ").Append(string.Join(", ", inner)).Append(" from ").Append(Source(query, table)).Append(" t");
        statement.AppendWhere();
        sql.Append(") q");

        bool grouping = groups.Count > 0;

        if (grouping)
        {
            sql.Append(" group by ").Append(string.Join(", ", groups));
        }

        // Q-109's having clause, written again from its tree in Oracle's dialect; the aggregates it names are ones both
        // databases spell alike, and its values bind as :h0, :h1, ….
        if (query.Having is { Predicate: { } having, Columns: { } aggregates })
        {
            List<string> names = [.. aggregates.Keys, .. query.GroupBy, .. query.GroupExpressions.Select(g => g.Name)];

            if (!PredicateSql.TryEmit(
                    having, names, name => aggregates.TryGetValue(name, out string? aggregate) ? aggregate : LayerDefinition.Quote(name),
                    out ParsedWhere written, out string? error, i => ":h" + i.ToString(CultureInfo.InvariantCulture), SqlDialect.Oracle))
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
            // The vocabulary rule of the other paths: a sort key naming neither a group nor a statistic is dropped.
            List<string> ordering = [];

            foreach (SortKey key in query.OrderBy)
            {
                string? named = query.GroupBy
                    .Concat(query.GroupExpressions.Select(g => g.Name))
                    .Concat(query.Statistics.Select(s => s.OutName))
                    .FirstOrDefault(n => n.Equals(key.Field, StringComparison.OrdinalIgnoreCase));

                if (named is not null)
                {
                    ordering.Add(key.Descending ? $"{LayerDefinition.Quote(named)} desc" : LayerDefinition.Quote(named));
                }
            }

            if (ordering.Count == 0)
            {
                ordering.AddRange(groups);
            }

            sql.Append(" order by ").Append(string.Join(", ", ordering))
               .Append(" fetch first ").Append(statement.Bind("limit", query.Limit)).Append(" rows only");
        }

        Dictionary<string, FieldType> kinds = new(StringComparer.Ordinal);

        foreach (string group in query.GroupBy)
        {
            kinds[group] = table.TypeOf(group);
        }

        // What the PostGIS path answers each with: a count is a bigint, a minimum or maximum the column's own type, and
        // everything else a double.
        foreach (StatisticRequest statistic in query.Statistics)
        {
            kinds[statistic.OutName] = statistic.Kind switch
            {
                StatisticKind.Count => FieldType.BigInteger,
                StatisticKind.Min or StatisticKind.Max or StatisticKind.PercentileDiscrete => table.TypeOf(statistic.Field),
                _ => FieldType.Double,
            };
        }

        List<IReadOnlyDictionary<string, object?>> rows = [];

        await using OracleCommand command = statement.Command(connection, _timeout);
        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        reader.SuppressGetDecimalInvalidCastException = true;

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
        const string Here = "This layer is in a registered Oracle database, which this build reads";

        // <b>The face parses a having clause and a statistic expression once, in PostgreSQL's spelling, and keeps the
        // tree and the aggregates beside it; this layer writes them again in Oracle's.</b> One built without them — by
        // hand, not by the face — is refused rather than pasted.
        if (query.Having is { } having && (having.Predicate is null || having.Columns is null))
        {
            throw new QueryNotSupportedException(
                $"{Here}, and this having clause reached it without its parsed form, so it cannot be written in Oracle's "
                + "dialect.");
        }

        if (query.Statistics.Any(s => s.Expression is { Operand: null })
            || query.GroupExpressions.Any(g => g.Expression.Operand is null))
        {
            throw new QueryNotSupportedException(
                $"{Here}, and a statistic expression reached it without its parsed form, so it cannot be written in "
                + "Oracle's dialect.");
        }

        // A table ignores a spatial filter, as ArcGIS's does (ADR-213), so nothing about one is refused.
        if (_layer.IsTable || query.Spatial is not { } spatial)
        {
            return;
        }

        if (spatial.Geometry.CoordinateCount > MostFilterVertices)
        {
            throw new QueryNotSupportedException(
                $"The filter geometry has {spatial.Geometry.CoordinateCount:N0} vertices, and an Oracle layer compares it "
                + $"with each feature in this server, so it takes at most {MostFilterVertices:N0}. Generalize it first "
                + "(GeometryServer/generalize).");
        }

        // <b>A distance and the relations beyond intersection are the geometry engine's</b> — the worker the
        // GeometryServer's `relation` runs in, as for a GeoParquet layer (Core's EngineRelations). Without one, which a
        // test can build, only intersection is answered.
        if (spatial.Distance > 0 && _engine is null)
        {
            throw new QueryNotSupportedException(
                $"{Here} without a geometry engine here, so not within a distance. Buffer the geometry first "
                + "(GeometryServer/buffer) and filter by the result.");
        }

        // A distance is answered as within that distance whatever relation names it, as the PostGIS path's ST_DWithin
        // answers it (2026-10-20, ADR-205: refused here until then, so the same query had two answers).

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

    /// <summary>Whether a failure is this provider's own deadline rather than the caller leaving.</summary>
    /// <remarks>
    /// <b>An Oracle error counts too</b>: a cancelled statement surfaces as ORA-01013, and the command timeout as the
    /// same — reporting either as a server fault would send an operator looking for a defect.
    /// </remarks>
    private static bool TimedOut(Exception stopped, CancellationTokenSource deadline, CancellationToken caller) =>
        deadline.IsCancellationRequested
        && !caller.IsCancellationRequested
        && stopped is OperationCanceledException or OracleException;

    private TimeoutException Timeout() => new(
        $"A query on layer '{_layer.Name}' ran past the {_timeout.TotalSeconds:0.#} seconds this service allows one "
        + "statement, and Oracle was told to stop. Narrow the extent or the where clause.");

    private Task<OracleConnection> OpenAsync(CancellationToken cancellationToken) =>
        OracleConnections.OpenAsync(_connectionString, cancellationToken);

    /// <summary>The table's columns and its geometry's Oracle SRID, read at most once a minute.</summary>
    private async Task<OracleTable> TableAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        if (Tables.TryGetValue(_tableKey, out var kept) && DateTimeOffset.UtcNow - kept.At < ColumnsKept)
        {
            return kept.Table;
        }

        List<OracleColumn> columns = [];

        await using (OracleCommand command = new(
            """
            select column_name, data_type, data_precision, data_scale, char_length, nullable, data_length
              from all_tab_columns
             where owner = :o and table_name = :t and data_type_owner is null
             order by column_id
            """, connection) { BindByName = true })
        {
            command.Parameters.Add("o", OracleDbType.Varchar2, _layer.SchemaName, System.Data.ParameterDirection.Input);
            command.Parameters.Add("t", OracleDbType.Varchar2, _layer.TableName, System.Data.ParameterDirection.Input);

            await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                OracleGuids.Storage? guid = OracleGuids.Of(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetDecimal(6)),
                    reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetDecimal(4)));

                FieldType type = guid is not null
                    ? FieldType.Guid
                    : TypeOf(
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetDecimal(2)),
                        reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetDecimal(3)));

                if (type == FieldType.Unknown)
                {
                    continue;
                }

                int? length = type == FieldType.Text && !reader.IsDBNull(4) && reader.GetDecimal(4) > 0
                    ? Convert.ToInt32(reader.GetDecimal(4))
                    : null;

                columns.Add(new OracleColumn(reader.GetString(0), type, reader.GetString(5) == "Y", guid is null ? length : null, guid));
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' is served from {_layer.SchemaName}.{_layer.TableName}, and this database user "
                + "sees no such table. It was dropped, renamed, or the user lost the right to read it.");
        }

        // <b>A table has no geometry column to find in the spatial metadata — ADR-213</b>: its reference is the one a table
        // carries, which nothing reads, and it has no ordinates, no points and no extent.
        if (_layer.IsTable)
        {
            OracleTable plain = new(columns, _layer.Srid, GeometryOrdinates.None, false, null);
            Tables[_tableKey] = (DateTimeOffset.UtcNow, plain);
            return plain;
        }

        int? srid = null;
        GeometryOrdinates ordinates = GeometryOrdinates.None;
        bool points = false;

        // <b>What the column stores: its metadata's dimensions, and a measure where one is named or a row says so</b> —
        // a third dimension is an elevation unless it is called M, as Oracle's linear referencing names it, or the first
        // row's SDO_GTYPE puts a measure there (its L digit).
        await using (OracleCommand command = new(
            $"""
            select m.srid,
                   (select count(*) from table(m.diminfo)),
                   (select count(*) from table(m.diminfo) d where upper(d.sdo_dimname) = 'M'),
                   (select r.{Shape}.sdo_gtype from {From} r where r.{Shape} is not null and rownum = 1),
                   (select count(*) from all_sdo_index_info i, all_sdo_index_metadata x
                     where i.table_owner = m.owner and i.table_name = m.table_name and i.column_name = m.column_name
                       and x.sdo_index_owner = i.sdo_index_owner and x.sdo_index_name = i.index_name
                       and x.sdo_layer_gtype = 'POINT')
              from all_sdo_geom_metadata m
             where m.owner = :o and m.table_name = :t and m.column_name = :c
            """, connection) { BindByName = true })
        {
            command.Parameters.Add("o", OracleDbType.Varchar2, _layer.SchemaName, System.Data.ParameterDirection.Input);
            command.Parameters.Add("t", OracleDbType.Varchar2, _layer.TableName, System.Data.ParameterDirection.Input);
            command.Parameters.Add("c", OracleDbType.Varchar2, _layer.RequireGeometryColumn(), System.Data.ParameterDirection.Input);

            await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0))
            {
                srid = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                int dimensions = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                bool named = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) > 0;
                int? sampled = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);

                if (sampled is { } gtype && gtype / 1000 > dimensions)
                {
                    dimensions = gtype / 1000;
                }

                bool measured = named || sampled / 100 % 10 > 0;

                // A spatial index made with LAYER_GTYPE=POINT is Oracle's promise that the column holds points only: it
                // refuses any other shape.
                points = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) > 0;

                ordinates = dimensions switch
                {
                    >= 4 => GeometryOrdinates.Z | GeometryOrdinates.M,
                    3 => measured ? GeometryOrdinates.M : GeometryOrdinates.Z,
                    _ => GeometryOrdinates.None,
                };
            }
        }

        OracleTable table = new(columns, srid ?? throw new InvalidOperationException(
            $"Layer '{_layer.Name}' is served from {_layer.SchemaName}.{_layer.TableName}.{_layer.RequireGeometryColumn()}, "
            + "which is no longer in the database's spatial metadata (ALL_SDO_GEOM_METADATA)."), ordinates, points,
            points ? await ExtentOfTableAsync(connection, cancellationToken).ConfigureAwait(false) : null);

        Tables[_tableKey] = (DateTimeOffset.UtcNow, table);
        return table;
    }

    private bool Projects(FeatureQuery query) =>
        (query.OutSrid is { } srid && srid != _layer.Srid) || query.OutWkt is { Length: > 0 };

    /// <summary>The where clause and its values, shared by every method.</summary>
    /// <param name="Clauses">The conditions, joined with <c>and</c>.</param>
    /// <param name="Parameters">The values they bind, by name.</param>
    /// <param name="Refined">When the exact test ran, the identities it kept.</param>
    private sealed record Filters(
        IReadOnlyList<string> Clauses,
        IReadOnlyList<(string Name, object Value)> Parameters,
        IReadOnlyList<long>? Refined = null);

    private async Task<Filters> FiltersAsync(
        OracleConnection connection, FeatureQuery query, OracleTable table, CancellationToken cancellationToken)
    {
        Filters filters = await RowFiltersAsync(connection, query, table, cancellationToken).ConfigureAwait(false);

        if (query.Top is not { } top)
        {
            return filters;
        }

        // <b>queryTopFeatures: the identities ranked within their group by the order asked, kept to the count</b> — the
        // PostGIS path's `row_number() over (partition by … order by …)`, over exactly the rows the other filters keep,
        // so a count, an id list and an extent of the same query count the features the read returns. Every name was
        // checked against the layer's fields when the filter was read, and is quoted here by us.
        string order = top.OrderBy.Count == 0
            ? Id
            : string.Join(", ", top.OrderBy.Select(k => LayerDefinition.Quote(k.Field) + (k.Descending ? " desc" : " asc")));

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
            [$"{Id} in (select t.i from ({ranked}) t where t.r <= :top_count)"],
            [.. filters.Parameters, ("top_count", (object)top.Count)]);
    }

    private async Task<Filters> RowFiltersAsync(
        OracleConnection connection, FeatureQuery query, OracleTable table, CancellationToken cancellationToken)
    {
        List<string> attributes = [];
        List<(string Name, object Value)> parameters = [];
        List<Envelope> boxes = [];
        Geometry? shape = null;
        SpatialFilter? byEngine = null;

        // <b>A table has no geometry, so a spatial filter is not applied — ADR-213</b>, as ArcGIS's own table and the PostGIS
        // source answer.
        if (!_layer.IsTable && query.BoundingBox is { } box)
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
            parameters.Add(("ids", JsonSerializer.Serialize(query.Identities)));
            attributes.Add($"{Id} in (select j.v from json_table(:ids, '$[*]' columns (v number path '$')) j)");
        }

        if (!_layer.IsTable && query.Spatial is { } spatial)
        {
            Geometry filter = await ToLayerAsync(spatial.Geometry, query, cancellationToken).ConfigureAwait(false);

            if (filter.IsEmpty)
            {
                return new Filters(["1 = 0"], []);
            }

            // A distance widens the box it is looked for in; the exact test is then the engine's.
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

        bool indexOnly = shape is null && query.BoundingBox is null
            && query.Spatial is { Relation: SpatialRelation.IndexIntersects };
        bool pointBoxes = !indexOnly && table.Points && byEngine is null && (shape is null || IsBox(shape));

        // The index's answer, kept for a box-only query too large to refine here.
        List<string> inOracle = [.. attributes];
        bool[] compared = new bool[boxes.Count];

        for (int i = 0; i < boxes.Count; i++)
        {
            // <b>A box over a good share of a point layer is not asked of the index</b> but compared, which is exact on
            // its own, because a scan is cheaper than the geodetic index's answer once that answer is more than a few
            // rows in a hundred: on the VPS, 200,000 points took SDO_FILTER 230–420 ms for a box holding all of them
            // and 210 ms for one holding 24,596, against 60–80 ms compared; a box holding 1,768 cost both about 60 ms.
            // A projected index was not measured. A stale extent costs speed only, never an answer.
            if (table.Points && !indexOnly && Wide(boxes[i], table.Extent))
            {
                inOracle.Add(InBox(boxes[i], "inbox" + i.ToString(CultureInfo.InvariantCulture), parameters));
                compared[i] = true;
                continue;
            }

            inOracle.Add(BoxClause(boxes[i], "box" + i.ToString(CultureInfo.InvariantCulture), table.Srid, parameters));
        }

        // <b>Index-intersects is the index's answer, as `&&` is PostGIS's</b> — esriSpatialRelIndexIntersects asks for
        // exactly that. Nothing is read twice for it; a bounding box or another relation beside it is exact.
        if (indexOnly)
        {
            return new Filters(inOracle, parameters);
        }

        // <b>A box against points is decided in Oracle, exactly.</b> A point is inside an axis-aligned box when each
        // coordinate is between the box's — the exact pass's answer and PostGIS's, boundary included — so it is written
        // as that comparison beside SDO_FILTER, whose geodetic index answers wide (ADR-205 §4), and nothing is carried
        // here. Only where the spatial index promises points (LAYER_GTYPE=POINT). A point's numbers are read from
        // SDO_POINT — SDO_GEOM.SDO_MIN_MBR_ORDINATE, which reads them wherever they are kept, cost 177 s against 0.43 s
        // for 200,000 points, so it is only the fallback for a point kept in the ordinate array (COALESCE stops early).
        if (pointBoxes)
        {
            List<string> exact = [.. inOracle];

            for (int i = 0; i < boxes.Count; i++)
            {
                if (!compared[i])
                {
                    exact.Add(InBox(boxes[i], "inbox" + i.ToString(CultureInfo.InvariantCulture), parameters));
                }
            }

            return new Filters(exact, parameters);
        }

        List<Geometry>? kept = byEngine is null ? null : [];

        // A distance's box is the filter's grown by the distance, so the exact box pass keeps every row it could reach.
        List<long>? matched = await RefineAsync(
                connection, table, Source(query, table), new Filters(inOracle, parameters), boxes, shape, cancellationToken, kept)
            .ConfigureAwait(false);

        if (matched is null)
        {
            return new Filters(inOracle, parameters);
        }

        if (byEngine is not null)
        {
            matched = await EngineRelations.SatisfyingAsync(
                    matched, kept!, byEngine, _layer.Srid, _projector, _engine!, "a registered Oracle database",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (matched.Count == 0)
        {
            return new Filters(["1 = 0"], [], matched);
        }

        return new Filters(
            [$"{Id} in (select r.v from json_table(:refined, '$[*]' columns (v number path '$')) r)"],
            [("refined", JsonSerializer.Serialize(matched))],
            matched);
    }

    /// <summary>The identities of the candidates whose boxes and shapes meet the filters — the exact pass.</summary>
    /// <returns>
    /// The identities, or null when a query of boxes alone matched more than this server holds — which the caller
    /// answers with the index's box instead. A shape past the bound is refused, because only this process answers it.
    /// </returns>
    private async Task<List<long>?> RefineAsync(
        OracleConnection connection,
        OracleTable table,
        string relation,
        Filters candidates,
        IReadOnlyList<Envelope> boxes,
        Geometry? shape,
        CancellationToken cancellationToken,
        List<Geometry>? kept = null)
    {
        Statement statement = new(candidates);

        // <b>Points are read as their two numbers</b>, where the spatial index promises points: converting each to WKB
        // cost Oracle about 25 microseconds a row, 5 s for 200,000 candidates on the VPS.
        statement.Sql.Append("select ").Append(Id).Append(table.Points
            ? $", coalesce(t.{Shape}.sdo_point.x, sdo_geom.sdo_min_mbr_ordinate(t.{Shape}, 1)), "
              + $"coalesce(t.{Shape}.sdo_point.y, sdo_geom.sdo_min_mbr_ordinate(t.{Shape}, 2))"
            : $", sdo_util.to_wkbgeometry({Flat(table)})").Append(" from ").Append(relation).Append(" t");
        statement.AppendWhere();

        List<long> matched = [];

        await using OracleCommand command = statement.Command(connection, _timeout);
        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        reader.SuppressGetDecimalInvalidCastException = true;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            Geometry geometry = table.Points
                ? new Point(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture), Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture))
                : WkbReader.Read((byte[])reader.GetValue(1));
            Envelope own = geometry.Envelope;

            if (boxes.Any(b => !own.Intersects(b))
                || (shape is not null && !GeometryPredicates.Intersects(geometry, shape)))
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
                    $"The filter meets more than {_mostMatched:N0} features of this layer. An Oracle layer holds what a "
                    + "spatial filter matched while it answers, so it bounds how many; narrow the filter.");
            }

            matched.Add(Convert.ToInt64(reader.GetDecimal(0), CultureInfo.InvariantCulture));
            kept?.Add(geometry);
        }

        return matched;
    }

    /// <summary>
    /// <c>SDO_FILTER</c> with an optimized rectangle — the spatial index's box test, which reads no geometry.
    /// </summary>
    /// <remarks>
    /// The rectangle is built from bound numbers rather than a bound SDO_GEOMETRY, so no Oracle object type is bound,
    /// and carries the column's own Oracle SRID, which Oracle requires to match (ORA-13295). In degrees it is kept inside
    /// the world, where a geodetic rectangle is allowed.
    /// </remarks>
    private string BoxClause(Envelope box, string name, int srid, List<(string Name, object Value)> parameters)
    {
        if (AxisOrder.IsGeographic(_layer.Srid))
        {
            box = new Envelope(
                Math.Max(-180, box.MinX), Math.Max(-90, box.MinY), Math.Min(180, box.MaxX), Math.Min(90, box.MaxY));
        }

        parameters.Add((name + "a", box.MinX));
        parameters.Add((name + "b", box.MinY));
        parameters.Add((name + "c", box.MaxX));
        parameters.Add((name + "d", box.MaxY));

        return $"sdo_filter({Shape}, sdo_geometry(2003, {srid.ToString(CultureInfo.InvariantCulture)}, null, "
            + $"sdo_elem_info_array(1, 1003, 3), sdo_ordinate_array(:{name}a, :{name}b, :{name}c, :{name}d))) = 'TRUE'";
    }

    /// <summary>A point inside a box, decided by its coordinates.</summary>
    private string InBox(Envelope box, string name, List<(string Name, object Value)> parameters)
    {
        parameters.Add((name + "a", box.MinX));
        parameters.Add((name + "b", box.MaxX));
        parameters.Add((name + "c", box.MinY));
        parameters.Add((name + "d", box.MaxY));
        return $"{PointOrdinate(1)} between :{name}a and :{name}b and {PointOrdinate(2)} between :{name}c and :{name}d";
    }

    /// <summary>
    /// The share of a point layer's extent past which a box is compared rather than asked of the index — an area's share
    /// standing for a share of rows, which is where the two cost the same on a geodetic index (see RowFiltersAsync).
    /// </summary>
    private const double WideShare = 0.05;

    /// <summary>Whether a box covers enough of the layer's extent that the spatial index would return most rows.</summary>
    private static bool Wide(Envelope box, Envelope? extent)
    {
        if (extent is not { } e || !box.Intersects(e))
        {
            return false;
        }

        double overlap = (Math.Min(box.MaxX, e.MaxX) - Math.Max(box.MinX, e.MinX)) * (Math.Min(box.MaxY, e.MaxY) - Math.Max(box.MinY, e.MinY));
        double whole = (e.MaxX - e.MinX) * (e.MaxY - e.MinY);

        return whole <= 0 ? box.Contains(e) : overlap >= WideShare * whole;
    }

    /// <summary>Whether a filter shape is an axis-aligned rectangle, so its box is the shape.</summary>
    private static bool IsBox(Geometry shape) =>
        shape is Polygon { Holes.Count: 0 } polygon
        && polygon.Shell.Coordinates is { Count: 5 } ring
        && Enumerable.Range(0, 5).All(i => (ring.X(i) == polygon.Envelope.MinX || ring.X(i) == polygon.Envelope.MaxX)
                                           && (ring.Y(i) == polygon.Envelope.MinY || ring.Y(i) == polygon.Envelope.MaxY));

    private static Polygon Rectangle(Envelope box) =>
        new(new LinearRing(XySequence.Wrap(
        [
            box.MinX, box.MinY,
            box.MaxX, box.MinY,
            box.MaxX, box.MaxY,
            box.MinX, box.MaxY,
            box.MinX, box.MinY,
        ])));

    /// <summary>The where clause, emitted again from its tree in Oracle's dialect (D-162, ADR-205).</summary>
    private static string Where(ParsedWhere where, OracleTable table, List<(string Name, object Value)> parameters)
    {
        if (where.Predicate is null)
        {
            throw new InvalidOperationException(
                "A where clause reached an Oracle layer without its parsed predicate, so it cannot be emitted in "
                + "Oracle's dialect.");
        }

        // A GlobalID is compared as ArcGIS spells one, whatever its column keeps (OracleGuids); the value sent is read
        // as a GUID first, so braces and case do not matter.
        Dictionary<string, OracleGuids.Storage> guids = table.Columns
            .Where(c => c.Guid is not null)
            .ToDictionary(c => c.Name, c => c.Guid!.Value, StringComparer.Ordinal);

        string Quoted(string name) =>
            guids.TryGetValue(name, out OracleGuids.Storage storage)
                ? OracleGuids.Text(LayerDefinition.Quote(name), storage)
                : LayerDefinition.Quote(name);

        if (!PredicateSql.TryEmit(
                where.Predicate, [.. table.Columns.Select(c => c.Name)], Quoted,
                out ParsedWhere emitted, out string? error, dialect: SqlDialect.Oracle.WithGuids(guids.Keys, arcGisText: true)))
        {
            throw new QueryNotSupportedException(error!);
        }

        for (int i = 0; i < emitted.Parameters.Count; i++)
        {
            object? value = emitted.Parameters[i] is Guid id ? OracleGuids.Braced(id) : emitted.Parameters[i];
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
                    "The projector could not read the written coordinate reference this request asked for.");
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
                moved = await _projector
                    .GeneralizeAsync(shapes, _layer.Srid, srid, tolerance, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
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
                ? GeometryRounding.Round(moved[k], places)
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

    private void AppendOrderAndPaging(Statement statement, FeatureQuery query, FeatureSchema schema, bool distinct)
    {
        StringBuilder sql = statement.Sql;

        if (distinct)
        {
            sql.Append(" order by ").Append(string.Join(", ", schema.Names.Select(LayerDefinition.Quote)));
        }
        else if (query.OrderBy.Count > 0)
        {
            sql.Append(" order by ").Append(string.Join(", ", query.OrderBy.Select(
                k => LayerDefinition.Quote(k.Field) + (k.Descending ? " desc" : " asc"))));

            // D-21's tiebreak: an order on a column that is not unique is not a total order.
            if (!query.OrderBy.Any(k => string.Equals(k.Field, _layer.IdentityColumn, StringComparison.Ordinal)))
            {
                sql.Append(", ").Append(Id);
            }
        }
        else
        {
            sql.Append(" order by ").Append(Id);
        }

        if (query.Offset > 0)
        {
            sql.Append(" offset ").Append(statement.Bind("offset", query.Offset)).Append(" rows");
        }

        sql.Append(" fetch next ").Append(statement.Bind("limit", query.Limit)).Append(" rows only");
    }

    private static string Aggregate(StatisticRequest statistic, string? computed = null)
    {
        string column = LayerDefinition.Quote(computed ?? statistic.Field);

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

    /// <summary>An Oracle column type, as the field list reports it — measured types only; the rest are not served.</summary>
    /// <remarks>
    /// <b>A whole number by its declared precision, as ArcGIS maps them</b>: <c>NUMBER(5)</c> short, <c>NUMBER(10)</c>
    /// long, wider a big integer — <c>NUMBER(38)</c>, what a geodatabase declares for an object id, included. A value
    /// wider than its CLR type is widened rather than refused (<see cref="Normalise"/>), since <c>NUMBER(10)</c> holds
    /// numbers an <c>int</c> does not. <c>NUMBER</c> with neither precision nor scale holds any number and is a double.
    /// </remarks>
    internal static FieldType TypeOf(string type, int? precision, int? scale) => type.ToUpperInvariant() switch
    {
        "NUMBER" when scale == 0 && precision is <= 5 => FieldType.SmallInteger,
        "NUMBER" when scale == 0 && precision is <= 10 => FieldType.Integer,
        "NUMBER" when scale == 0 => FieldType.BigInteger,
        "NUMBER" or "FLOAT" or "BINARY_DOUBLE" => FieldType.Double,
        "BINARY_FLOAT" => FieldType.Single,
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "CLOB" or "NCLOB" => FieldType.Text,
        "DATE" => FieldType.Date,
        "BLOB" or "RAW" => FieldType.Binary,
        "BOOLEAN" => FieldType.Boolean,
        _ when type.StartsWith("TIMESTAMP", StringComparison.OrdinalIgnoreCase) => FieldType.Date,
        _ => FieldType.Unknown,
    };

    /// <summary>An Oracle value as the faces expect one — the shapes Npgsql hands the PostGIS path.</summary>
    /// <remarks>
    /// The managed driver answers every <c>NUMBER</c> as a <c>decimal</c> (truncated past 28 digits rather than
    /// refused, <c>SuppressGetDecimalInvalidCastException</c>), so a number becomes the CLR type its column's field type
    /// promises; an aggregate is given the type the PostGIS path answers it with (StatisticsBoundedAsync).
    /// </remarks>
    internal static object? Normalise(object? value, FieldType type) => value switch
    {
        null or DBNull => null,

        // The driver answers a NUMBER(10) as a long rather than a decimal (measured), so every whole number takes the
        // decimal's road to the type its column promises.
        short or int or long => Normalise(Convert.ToDecimal(value, CultureInfo.InvariantCulture), type),
        decimal number => type switch
        {
            FieldType.SmallInteger when number is >= short.MinValue and <= short.MaxValue => (object)(short)number,
            FieldType.SmallInteger or FieldType.Integer when number is >= int.MinValue and <= int.MaxValue => (object)(int)number,
            FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger
                when number is >= long.MinValue and <= long.MaxValue => (object)(long)number,
            FieldType.Double => (object)(double)number,
            FieldType.Single => (object)(float)number,
            _ => (object)(double)number,
        },

        // A BINARY_DOUBLE column, and an aggregate over one. Every arm is boxed, because a switch over short, int, long
        // and double has double as its type and would turn a count into 1312.0 — which it did, first time round.
        double real => type switch
        {
            FieldType.SmallInteger => (object)(short)real,
            FieldType.Integer => (object)(int)real,
            FieldType.BigInteger => (object)(long)real,
            FieldType.Single => (object)(float)real,
            _ => (object)real,
        },
        // A GlobalID, as its column keeps it (OracleGuids).
        string or byte[] when type == FieldType.Guid && OracleGuids.Read(value) is { } guid => guid,
        _ => value,
    };

    private sealed record OracleColumn(string Name, FieldType Type, bool Nullable, int? MaxLength, OracleGuids.Storage? Guid = null);

    private sealed record OracleTable(
        IReadOnlyList<OracleColumn> Columns, int Srid, GeometryOrdinates Ordinates = GeometryOrdinates.None, bool Points = false,
        Envelope? Extent = null)
    {
        public FieldType TypeOf(string name) =>
            Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.Type ?? FieldType.Unknown;
    }

    /// <summary>A statement being built, with Oracle's named binds.</summary>
    private sealed class Statement(Filters filters)
    {
        private readonly List<(string Name, object Value)> _parameters = [.. filters.Parameters];

        public StringBuilder Sql { get; } = new();

        public string Bind(string name, object value)
        {
            _parameters.Add((name, value));
            return ":" + name;
        }

        public void AppendWhere()
        {
            if (filters.Clauses.Count > 0)
            {
                Sql.Append(" where ").Append(string.Join(" and ", filters.Clauses));
            }
        }

        public OracleCommand Command(OracleConnection connection, TimeSpan timeout)
        {
            OracleCommand command = new(Sql.ToString(), connection)
            {
                BindByName = true,
                CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds),

                // The whole WKB with the row, rather than a round trip per geometry to read its LOB.
                InitialLOBFetchSize = -1,
            };

            foreach ((string name, object value) in _parameters)
            {
                OracleParameter parameter = value is string text && text.Length > 4000
                    ? new OracleParameter(name, OracleDbType.Clob) { Value = text }
                    : new OracleParameter(name, value);

                command.Parameters.Add(parameter);
            }

            return command;
        }
    }
}
