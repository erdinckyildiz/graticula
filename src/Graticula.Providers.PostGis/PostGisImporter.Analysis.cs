using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Npgsql;

namespace Graticula.Providers.PostGis;

/// <summary>A hosted layer an analysis reads: its definition, the columns it carries through, and its filter.</summary>
/// <param name="Layer">The layer, which must be in the hosted schema.</param>
/// <param name="Columns">The source columns the output keeps, by their column names.</param>
/// <param name="Where">The caller's filter, parsed — empty for every row.</param>
/// <param name="Extent">Only features overlapping this box are read — the request's <c>context.extent</c> — or null.</param>
/// <param name="OutputSrid">The reference the result is stored in — <c>context.outSR</c> — or null for the layer's own.</param>
public sealed record AnalysisInput(
    LayerDefinition Layer,
    IReadOnlyList<string> Columns,
    ParsedWhere Where,
    AnalysisExtent? Extent = null,
    int? OutputSrid = null);

/// <summary>A box in a reference, as a request's <c>context.extent</c> gives one.</summary>
public sealed record AnalysisExtent(double XMin, double YMin, double XMax, double YMax, int Srid);

/// <summary>What <c>CreateBuffers</c> was asked — ADR-222 §5.3, in metres and square metres.</summary>
/// <param name="Distances">The distances, in the caller's unit; null when <paramref name="DistanceColumn"/> gives them.</param>
/// <param name="DistanceColumn">The column each feature's distance is read from, or null.</param>
/// <param name="MetresPerUnit">How many metres one of the caller's units is.</param>
/// <param name="Dissolve">Whether the buffers of one distance are made one feature.</param>
/// <param name="Rings">Whether each distance's buffer leaves out the smaller ones', as rings.</param>
/// <param name="SquareMetresPerAreaUnit">What <c>AnalysisArea</c> is counted in: a square mile's or a square kilometre's metres.</param>
public sealed record BufferPlan(
    IReadOnlyList<double>? Distances,
    string? DistanceColumn,
    double MetresPerUnit,
    bool Dissolve,
    bool Rings,
    double SquareMetresPerAreaUnit);

/// <summary>What <c>DissolveBoundaries</c> was asked — ADR-222 §5.3.</summary>
/// <param name="GroupBy">The columns whose equal values are merged; empty merges every polygon that touches or overlaps.</param>
/// <param name="Summaries">Each summary as a column and a statistic: <c>sum</c>, <c>avg</c>, <c>min</c>, <c>max</c> or <c>stddev_samp</c>.</param>
/// <param name="MultiPart">Whether a group's polygons stay one feature, or one feature per separate part.</param>
public sealed record DissolvePlan(
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<(string Column, string Statistic, string Output)> Summaries,
    bool MultiPart);

/// <summary>
/// The Spatial Analysis Service's tools, as SQL over the datastore — ADR-222. Each writes a new table in the hosted schema
/// under a name the job gives it, which the host then publishes as a hosted layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here, beside the importer, because this is the one project that writes SQL against the datastore</b>, and the
/// table it makes is a hosted table like an import's: an identity <c>objectid</c>, a typed <c>geom</c> with a GiST index.
/// </para>
/// <para>
/// <b>The table is made empty first, with the source's own column types, and filled by an insert.</b> PostgreSQL takes no
/// bound parameters in <c>create table … as</c>, and a filter's literals are parameters; an <c>insert … select</c> takes
/// them. No text from the request reaches a statement: column names are the layer's own, checked by the caller and
/// quoted here; the filter is <see cref="WhereClause"/>'s; numbers are parameters.
/// </para>
/// <para>
/// <b>Geodesic, in WGS 84, whatever the layer is stored in</b> (ADR-222 §5.8c, INFERRED): a buffer is drawn on the
/// ellipsoid by <c>geography</c> and the result moved back to the layer's reference, so a distance in metres is metres on
/// the ground in Istanbul and in Oslo alike.
/// </para>
/// </remarks>
public sealed partial class PostGisImporter
{
    private const string AnalysisGeometry = "__g";
    private const string AnalysisDistance = "__dist";
    private const string AnalysisOrigin = "__oid";

    /// <summary>The table an analysis job writes, from the job's id, so a second run finds and replaces its own.</summary>
    /// <param name="job">The job.</param>
    /// <returns>The table's name in the hosted schema.</returns>
    public static string AnalysisTableFor(Guid job) => "analysis_" + job.ToString("N")[..20];

    /// <summary>Drops an analysis's table if a run left one — a failed or repeated run's.</summary>
    /// <param name="table">The table, from <see cref="AnalysisTableFor"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>When it is gone.</returns>
    public async Task DropAnalysisAsync(string table, CancellationToken cancellationToken)
    {
        RefuseOutsideHosted(HostedSchema, table, "drop");
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"drop table if exists {Qualified(table)}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>CreateBuffers</c>: a buffer of every feature at every distance, or rings, or one feature per distance.</summary>
    /// <param name="input">The layer and what it keeps.</param>
    /// <param name="plan">The distances and the shape.</param>
    /// <param name="table">The table to write, from <see cref="AnalysisTableFor"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table, its row count and its reference.</returns>
    public async Task<ImportResult> BufferAsync(
        AnalysisInput input, BufferPlan plan, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(plan);
        RefuseOutsideHosted(input.Layer.SchemaName, input.Layer.TableName, "analyse");
        RefuseOutsideHosted(HostedSchema, table, "make");

        LayerDefinition layer = input.Layer;
        string geometry = LayerDefinition.Quote(layer.RequireGeometryColumn());
        string origin = LayerDefinition.Quote(layer.IntegerIdentityColumn ?? layer.IdentityColumn);
        int srid = input.OutputSrid ?? layer.Srid;

        // A dissolved buffer keeps no source column: one feature stands for many (the task's documented fields).
        IReadOnlyList<string> carried = plan.Dissolve ? [] : input.Columns;
        bool byField = plan.DistanceColumn is not null;
        string carriedList = string.Concat(carried.Select(c => LayerDefinition.Quote(c) + ", "));
        string carriedFrom = string.Concat(carried.Select(c => "b." + LayerDefinition.Quote(c) + ", "));

        // Each feature's buffer at each distance, in WGS 84 geometry, with the distance and the feature's id beside it.
        string distance = byField
            ? $"s.{LayerDefinition.Quote(plan.DistanceColumn!)}::double precision"
            : $"d.{AnalysisDistance}";
        string disks =
            $"select {string.Concat(carried.Select(c => "s." + LayerDefinition.Quote(c) + ", "))}"
            + $"{distance} as {AnalysisDistance}, s.{origin}::integer as {AnalysisOrigin}, "
            + $"ST_Buffer(ST_Transform(s.{geometry}, 4326)::geography, {distance} * @metres)::geometry as {AnalysisGeometry} "
            + $"from (select * from {Qualified(layer.TableName)}{Filter(input, geometry)}) s "
            + (byField ? string.Empty : $"cross join unnest(@distances) as d({AnalysisDistance}) ")
            + $"where s.{geometry} is not null and {distance} > 0";

        string shaped = plan.Dissolve
            ? $"select {AnalysisDistance}, ST_Union({AnalysisGeometry}) as {AnalysisGeometry} from ({disks}) u group by {AnalysisDistance}"
            : disks;

        // Rings: each distance less the next smaller one, per feature — or per distance once dissolved.
        string partition = plan.Dissolve ? string.Empty : $"partition by {AnalysisOrigin} ";
        string ringed = plan.Rings
            ? $"select {carriedList}{(plan.Dissolve ? string.Empty : AnalysisOrigin + ", ")}{AnalysisDistance}, "
              + $"coalesce(ST_Difference({AnalysisGeometry}, lag({AnalysisGeometry}) over w), {AnalysisGeometry}) as {AnalysisGeometry} "
              + $"from ({shaped}) r window w as ({partition}order by {AnalysisDistance})"
            : shaped;

        List<string> outputs = [.. carried.Select(LayerDefinition.Quote)];
        List<string> values = [.. carried.Select(c => "b." + LayerDefinition.Quote(c))];

        if (!byField)
        {
            outputs.Add("buff_dist");
            values.Add($"b.{AnalysisDistance}");
        }

        if (!plan.Dissolve)
        {
            outputs.Add("orig_fid");
            values.Add($"b.{AnalysisOrigin}");
        }

        outputs.Add("analysisarea");
        values.Add($"ST_Area(b.{AnalysisGeometry}::geography) / @areaunit");
        outputs.Add("geom");
        values.Add($"ST_Multi(ST_CollectionExtract(ST_Transform(b.{AnalysisGeometry}, {srid.ToString(CultureInfo.InvariantCulture)}), 3))");

        string insert =
            $"insert into {Qualified(table)} ({string.Join(", ", outputs)}) "
            + $"select {string.Join(", ", values)} from ({ringed}) b where not ST_IsEmpty(b.{AnalysisGeometry})";

        // The empty table, typed from the source's own columns.
        string shape =
            $"create table {Qualified(table)} as select {carriedList}"
            + (byField ? string.Empty : "null::double precision as buff_dist, ")
            + (plan.Dissolve ? string.Empty : "null::integer as orig_fid, ")
            + "null::double precision as analysisarea "
            + $"from {Qualified(layer.TableName)} where false";

        return await WriteAnalysisAsync(table, shape, insert, srid, command =>
        {
            command.Parameters.AddWithValue("metres", plan.MetresPerUnit);
            command.Parameters.AddWithValue("areaunit", plan.SquareMetresPerAreaUnit);

            if (!byField)
            {
                command.Parameters.AddWithValue("distances", plan.Distances!.ToArray());
            }

            Bind(command, input);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>DissolveBoundaries</c>: polygons merged by equal values, or every touching polygon when none are named.</summary>
    /// <param name="input">The layer, a polygon layer.</param>
    /// <param name="plan">What is grouped and summarised.</param>
    /// <param name="table">The table to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table, its row count and its reference.</returns>
    /// <remarks>
    /// <b>A part's statistics are its own polygons'</b>: split into separate parts, each part counts and sums the source
    /// polygons whose interior point lies in it, so two islands of one group do not each claim the group's total.
    /// </remarks>
    public async Task<ImportResult> DissolveAsync(
        AnalysisInput input, DissolvePlan plan, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(plan);
        RefuseOutsideHosted(input.Layer.SchemaName, input.Layer.TableName, "analyse");
        RefuseOutsideHosted(HostedSchema, table, "make");

        LayerDefinition layer = input.Layer;
        string geometry = LayerDefinition.Quote(layer.RequireGeometryColumn());
        int srid = input.OutputSrid ?? layer.Srid;
        string stored = srid == layer.Srid
            ? $"b.{AnalysisGeometry}"
            : $"ST_Transform(b.{AnalysisGeometry}, {srid.ToString(CultureInfo.InvariantCulture)})";

        string[] groups = [.. plan.GroupBy.Select(LayerDefinition.Quote)];
        string groupList = string.Concat(groups.Select(g => g + ", "));
        string groupBy = groups.Length == 0 ? "group by ()" : "group by " + string.Join(", ", groups);
        string matched = groups.Length == 0
            ? "true"
            : string.Join(" and ", groups.Select(g => $"s.{g} is not distinct from p.{g}"));

        string[] summaries = [.. plan.Summaries.Select(s =>
            $"{s.Statistic}(s.{LayerDefinition.Quote(s.Column)}) as {LayerDefinition.Quote(s.Output)}")];
        string summaryList = string.Concat(summaries.Select(s => ", " + s));

        string filtered = $"select * from {Qualified(layer.TableName)}{Filter(input, geometry)}";
        string parts = plan.MultiPart
            ? $"select {groupList}ST_Union({geometry}) as {AnalysisGeometry} from f where {geometry} is not null {groupBy}"
            : $"select {groupList}(ST_Dump(ST_Union({geometry}))).geom as {AnalysisGeometry} from f where {geometry} is not null {groupBy}";

        string grouped =
            $"select {string.Concat(groups.Select(g => "p." + g + ", "))}p.{AnalysisGeometry}, count(*)::integer as n{summaryList} "
            + $"from p join f s on {matched} and s.{geometry} is not null "
            + $"and ST_Intersects(ST_PointOnSurface(s.{geometry}), p.{AnalysisGeometry}) "
            + $"group by {string.Concat(groups.Select(g => "p." + g + ", "))}p.{AnalysisGeometry}";

        List<string> outputs = [.. groups, "count", .. plan.Summaries.Select(s => LayerDefinition.Quote(s.Output)), "geom"];
        List<string> values =
        [
            .. groups.Select(g => "b." + g),
            "b.n",
            .. plan.Summaries.Select(s => "b." + LayerDefinition.Quote(s.Output)),
            $"ST_Multi(ST_CollectionExtract({stored}, 3))",
        ];

        string insert =
            $"insert into {Qualified(table)} ({string.Join(", ", outputs)}) "
            + $"with f as ({filtered}), p as ({parts}) select {string.Join(", ", values)} from ({grouped}) b";

        // Typed by the same functions the insert uses, so a sum of integers is a bigint in both. `limit 0`, not `where
        // false` alone: an aggregate without a group answers one row even over no rows.
        string shape =
            $"create table {Qualified(table)} as select {string.Concat(groups.Select(g => "s." + g + ", "))}0::integer as count"
            + string.Concat(plan.Summaries.Select(s =>
                $", {s.Statistic}(s.{LayerDefinition.Quote(s.Column)}) as {LayerDefinition.Quote(s.Output)}"))
            + $" from {Qualified(layer.TableName)} s where false"
            + (groups.Length == 0 ? string.Empty : " group by " + string.Join(", ", groups.Select(g => "s." + g)))
            + " limit 0";

        return await WriteAnalysisAsync(table, shape, insert, srid, command => Bind(command, input), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The filter and the extent, as one <c>where</c>; the extent's numbers are parameters too.</summary>
    /// <summary>
    /// The filter and the extent, as one <c>where</c>; the extent's numbers are parameters too. A second input's
    /// parameters take another letter, so two filters can sit in one statement.
    /// </summary>
    private static string Filter(AnalysisInput input, string geometry, string letter = "w")
    {
        List<string> clauses = [];

        if (input.Where.Sql is { Length: > 0 } sql)
        {
            // Highest first, so @w1 is not taken for the start of @w10.
            for (int i = input.Where.Parameters.Count - 1; letter != "w" && i >= 0; i--)
            {
                sql = sql.Replace($"@w{i.ToString(CultureInfo.InvariantCulture)}", $"@{letter}{i.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal);
            }

            clauses.Add($"({sql})");
        }

        if (input.Extent is not null)
        {
            clauses.Add($"ST_Intersects({geometry}, ST_Transform(ST_MakeEnvelope(@{letter}xmin, @{letter}ymin, @{letter}xmax, @{letter}ymax, @{letter}boxsrid), "
                + $"{input.Layer.Srid.ToString(CultureInfo.InvariantCulture)}))");
        }

        return clauses.Count == 0 ? string.Empty : " where " + string.Join(" and ", clauses);
    }

    private static void Bind(NpgsqlCommand command, AnalysisInput input, string letter = "w")
    {
        for (int i = 0; i < input.Where.Parameters.Count; i++)
        {
            command.Parameters.AddWithValue($"{letter}{i.ToString(CultureInfo.InvariantCulture)}", input.Where.Parameters[i] ?? DBNull.Value);
        }

        if (input.Extent is { } box)
        {
            command.Parameters.AddWithValue(letter + "xmin", box.XMin);
            command.Parameters.AddWithValue(letter + "ymin", box.YMin);
            command.Parameters.AddWithValue(letter + "xmax", box.XMax);
            command.Parameters.AddWithValue(letter + "ymax", box.YMax);
            command.Parameters.AddWithValue(letter + "boxsrid", box.Srid);
        }
    }

    private static string GeometryTypeOf(AnalysisShape shape) => shape switch
    {
        AnalysisShape.Points => "MultiPoint",
        AnalysisShape.Lines => "MultiLineString",
        _ => "MultiPolygon",
    };

    /// <summary>The empty table, its identity and typed geometry, the rows, the index — in one transaction.</summary>
    private async Task<ImportResult> WriteAnalysisAsync(
        string table, string shape, string insert, int srid, Action<NpgsqlCommand> bind, CancellationToken cancellationToken,
        AnalysisShape geometry = AnalysisShape.Polygons, bool single = false)
    {
        await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHostedSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, $"drop table if exists {Qualified(table)}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, shape, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction,
            $"alter table {Qualified(table)} add column objectid integer generated always as identity primary key, "
            + $"add column geom geometry({(single ? "Point" : GeometryTypeOf(geometry))}, {srid.ToString(CultureInfo.InvariantCulture)})",
            cancellationToken).ConfigureAwait(false);

        int rows;

        await using (NpgsqlCommand command = new(insert, connection, transaction) { CommandTimeout = 0 })
        {
            bind(command);
            rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, $"create index on {Qualified(table)} using gist (geom)", cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"analyze {Qualified(table)}", cancellationToken).ConfigureAwait(false);

        return new ImportResult(HostedSchema, table, rows, srid, srid, null);
    }
}
