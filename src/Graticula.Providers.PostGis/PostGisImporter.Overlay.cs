using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;

namespace Graticula.Providers.PostGis;

/// <summary>What an analysis's result holds: points, lines or polygons — the dimension <c>ST_CollectionExtract</c> keeps.</summary>
public enum AnalysisShape
{
    /// <summary>Points, stored as a multipoint.</summary>
    Points = 1,

    /// <summary>Lines, stored as a multilinestring.</summary>
    Lines = 2,

    /// <summary>Polygons, stored as a multipolygon.</summary>
    Polygons = 3,
}

/// <summary>What <c>OverlayLayers</c> was asked — ADR-222 condition 3.</summary>
/// <param name="Operation"><c>Intersect</c>, <c>Union</c> or <c>Erase</c>.</param>
/// <param name="Output">What the result holds — the lower of the two inputs' for an intersect, the input's for an erase.</param>
/// <param name="OverlayColumns">The overlay layer's columns the result keeps, each with the name it is kept under.</param>
public sealed record OverlayPlan(string Operation, AnalysisShape Output, IReadOnlyList<(string Source, string Output)> OverlayColumns);

/// <summary>What <c>MergeLayers</c> was asked — ADR-222 condition 3.</summary>
/// <param name="InputColumns">The input layer's columns, each with the column it is written to.</param>
/// <param name="MergeColumns">The merge layer's columns, each with the column it is written to — one of the input's, or its own.</param>
/// <param name="MergeOnly">The merge layer's columns that are columns of their own in the result.</param>
/// <param name="Output">What both layers hold.</param>
public sealed record MergePlan(
    IReadOnlyList<(string Source, string Output)> InputColumns,
    IReadOnlyList<(string Source, string Output)> MergeColumns,
    IReadOnlyList<(string Source, string Output)> MergeOnly,
    AnalysisShape Output);

/// <summary>What <c>AggregatePoints</c> was asked, with a polygon layer — ADR-222 condition 3.</summary>
/// <param name="Summaries">Each summary of the points as a column, a statistic and the name it is kept under.</param>
/// <param name="KeepEmpty">Whether a polygon with no point in it is kept, with a count of zero.</param>
public sealed record AggregatePlan(IReadOnlyList<(string Column, string Statistic, string Output)> Summaries, bool KeepEmpty);

/// <summary>The two-layer tools — ADR-222 condition 3: overlay, merge and aggregate, as SQL over the datastore.</summary>
/// <remarks>
/// <b>The second layer is moved into the first's reference before anything is compared</b>, and the result into the
/// output's; each filter is its own layer's, its parameters under a letter of its own.
/// </remarks>
public sealed partial class PostGisImporter
{
    private const string SecondGeometry = "__bg";

    /// <summary><c>OverlayLayers</c>: the input intersected with, united with, or less the overlay layer.</summary>
    /// <param name="input">The input layer.</param>
    /// <param name="overlay">The overlay layer.</param>
    /// <param name="plan">The operation and what the result keeps.</param>
    /// <param name="table">The table to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table, its row count and its reference.</returns>
    public async Task<ImportResult> OverlayAsync(
        AnalysisInput input, AnalysisInput overlay, OverlayPlan plan, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(plan);
        RefuseOutsideHosted(input.Layer.SchemaName, input.Layer.TableName, "analyse");
        RefuseOutsideHosted(overlay.Layer.SchemaName, overlay.Layer.TableName, "analyse");
        RefuseOutsideHosted(HostedSchema, table, "make");

        string a = LayerDefinition.Quote(input.Layer.RequireGeometryColumn());
        string b = LayerDefinition.Quote(overlay.Layer.RequireGeometryColumn());
        string srid = input.Layer.Srid.ToString(CultureInfo.InvariantCulture);
        int output = input.OutputSrid ?? input.Layer.Srid;
        string dimension = ((int)plan.Output).ToString(CultureInfo.InvariantCulture);

        string[] aColumns = [.. input.Columns.Select(LayerDefinition.Quote)];
        string[] bColumns = [.. plan.OverlayColumns.Select(c => LayerDefinition.Quote(c.Source))];
        string[] bOutputs = [.. plan.OverlayColumns.Select(c => LayerDefinition.Quote(c.Output))];

        string fromA = $"select * from {Qualified(input.Layer.TableName)}{Filter(input, a)}";
        string fromB = $"select *, ST_Transform({b}, {srid}) as {SecondGeometry} from {Qualified(overlay.Layer.TableName)}{Filter(overlay, b, "v")}";

        string aSelect(string alias) => string.Concat(aColumns.Select(c => $"{alias}.{c}, "));
        string bSelect(string alias) => string.Concat(bColumns.Select((c, i) => $"{alias}.{c} as {bOutputs[i]}, "));
        string aNulls = string.Concat(aColumns.Select(c => $"null as {c}, "));
        string bNulls = string.Concat(bOutputs.Select(c => $"null as {c}, "));

        string intersect =
            $"select {aSelect("a")}{bSelect("b")}ST_Intersection(a.{a}, b.{SecondGeometry}) as {AnalysisGeometry} "
            + $"from fa a join fb b on ST_Intersects(a.{a}, b.{SecondGeometry})";

        string inputLeft =
            $"select {aSelect("a")}{bNulls}coalesce(ST_Difference(a.{a}, u.g), a.{a}) as {AnalysisGeometry} from fa a "
            + $"left join lateral (select ST_Union(b.{SecondGeometry}) as g from fb b where ST_Intersects(a.{a}, b.{SecondGeometry})) u on true";

        string overlayLeft =
            $"select {aNulls}{bSelect("b")}coalesce(ST_Difference(b.{SecondGeometry}, u.g), b.{SecondGeometry}) as {AnalysisGeometry} from fb b "
            + $"left join lateral (select ST_Union(a.{a}) as g from fa a where ST_Intersects(a.{a}, b.{SecondGeometry})) u on true";

        string pieces = plan.Operation switch
        {
            "Intersect" => intersect,
            "Erase" => $"select {string.Concat(aColumns.Select(c => c + ", "))}{AnalysisGeometry} from ({inputLeft}) e",
            "Union" => $"{intersect} union all {inputLeft} union all {overlayLeft}",
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Operation, "Intersect, Union or Erase."),
        };

        string[] outputs = plan.Operation == "Erase" ? aColumns : [.. aColumns, .. bOutputs];
        string kept = ToSrid($"x.{AnalysisGeometry}", input.Layer.Srid, output);

        string insert =
            $"insert into {Qualified(table)} ({string.Concat(outputs.Select(c => c + ", "))}geom) "
            + $"with fa as ({fromA}), fb as ({fromB}) "
            + $"select {string.Concat(outputs.Select(c => "x." + c + ", "))}ST_Multi(ST_CollectionExtract({kept}, {dimension})) "
            + $"from ({pieces}) x where not ST_IsEmpty(ST_CollectionExtract(x.{AnalysisGeometry}, {dimension}))";

        string shape =
            $"create table {Qualified(table)} as select {string.Join(", ", [.. aColumns.Select(c => "a." + c),
                .. (plan.Operation == "Erase" ? [] : bColumns.Select((c, i) => $"b.{c} as {bOutputs[i]}"))])}"
            + $" from {Qualified(input.Layer.TableName)} a cross join {Qualified(overlay.Layer.TableName)} b where false";

        if (outputs.Length == 0)
        {
            shape = $"create table {Qualified(table)} (\"_\" boolean)";
        }

        ImportResult made = await WriteAnalysisAsync(table, shape, insert, output, command =>
        {
            Bind(command, input);
            Bind(command, overlay, "v");
        }, cancellationToken, plan.Output).ConfigureAwait(false);

        if (outputs.Length == 0)
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, $"alter table {Qualified(table)} drop column \"_\"", cancellationToken).ConfigureAwait(false);
        }

        return made;
    }

    /// <summary><c>MergeLayers</c>: the input's features and the merge layer's, in one layer.</summary>
    /// <param name="input">The input layer.</param>
    /// <param name="merge">The merge layer, of the same kind of geometry.</param>
    /// <param name="plan">Where each column goes.</param>
    /// <param name="table">The table to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table, its row count and its reference.</returns>
    /// <remarks>
    /// <b>A merge layer's value is written to the input's column by PostgreSQL's own assignment cast</b> — an integer into
    /// a double, a number into text — and the host refuses the one ArcGIS refuses, text into a number, before it is asked.
    /// </remarks>
    public async Task<ImportResult> MergeAsync(
        AnalysisInput input, AnalysisInput merge, MergePlan plan, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(merge);
        ArgumentNullException.ThrowIfNull(plan);
        RefuseOutsideHosted(input.Layer.SchemaName, input.Layer.TableName, "analyse");
        RefuseOutsideHosted(merge.Layer.SchemaName, merge.Layer.TableName, "analyse");
        RefuseOutsideHosted(HostedSchema, table, "make");

        string a = LayerDefinition.Quote(input.Layer.RequireGeometryColumn());
        string b = LayerDefinition.Quote(merge.Layer.RequireGeometryColumn());
        int output = input.OutputSrid ?? input.Layer.Srid;
        string dimension = ((int)plan.Output).ToString(CultureInfo.InvariantCulture);

        string[] shaped =
        [
            .. plan.InputColumns.Select(c => $"a.{LayerDefinition.Quote(c.Source)} as {LayerDefinition.Quote(c.Output)}"),
            .. plan.MergeOnly.Select(c => $"m.{LayerDefinition.Quote(c.Source)} as {LayerDefinition.Quote(c.Output)}"),
        ];

        string shape = shaped.Length == 0
            ? $"create table {Qualified(table)} (\"_\" boolean)"
            : $"create table {Qualified(table)} as select {string.Join(", ", shaped)} "
              + $"from {Qualified(input.Layer.TableName)} a cross join {Qualified(merge.Layer.TableName)} m where false";

        string fromInput =
            $"insert into {Qualified(table)} ({string.Concat(plan.InputColumns.Select(c => LayerDefinition.Quote(c.Output) + ", "))}geom) "
            + $"select {string.Concat(plan.InputColumns.Select(c => "s." + LayerDefinition.Quote(c.Source) + ", "))}"
            + $"ST_Multi(ST_CollectionExtract({ToSrid("s." + a, input.Layer.Srid, output)}, {dimension})) "
            + $"from (select * from {Qualified(input.Layer.TableName)}{Filter(input, a)}) s where s.{a} is not null";

        string fromMerge =
            $"insert into {Qualified(table)} ({string.Concat(plan.MergeColumns.Select(c => LayerDefinition.Quote(c.Output) + ", "))}geom) "
            + $"select {string.Concat(plan.MergeColumns.Select(c => "s." + LayerDefinition.Quote(c.Source) + ", "))}"
            + $"ST_Multi(ST_CollectionExtract({ToSrid("s." + b, merge.Layer.Srid, output)}, {dimension})) "
            + $"from (select * from {Qualified(merge.Layer.TableName)}{Filter(merge, b, "v")}) s where s.{b} is not null";

        ImportResult made = await WriteAnalysisAsync(table, shape, fromInput + "; " + fromMerge, output, command =>
        {
            Bind(command, input);
            Bind(command, merge, "v");
        }, cancellationToken, plan.Output).ConfigureAwait(false);

        if (shaped.Length == 0)
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, $"alter table {Qualified(table)} drop column \"_\"", cancellationToken).ConfigureAwait(false);
        }

        return made;
    }

    /// <summary><c>AggregatePoints</c> with a polygon layer: each polygon, how many points fall in it, and their summaries.</summary>
    /// <param name="points">The point layer.</param>
    /// <param name="polygons">The polygon layer, whose columns the result keeps.</param>
    /// <param name="plan">What is summarised.</param>
    /// <param name="table">The table to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table, its row count and its reference.</returns>
    public async Task<ImportResult> AggregateAsync(
        AnalysisInput points, AnalysisInput polygons, AggregatePlan plan, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(polygons);
        ArgumentNullException.ThrowIfNull(plan);
        RefuseOutsideHosted(points.Layer.SchemaName, points.Layer.TableName, "analyse");
        RefuseOutsideHosted(polygons.Layer.SchemaName, polygons.Layer.TableName, "analyse");
        RefuseOutsideHosted(HostedSchema, table, "make");

        string pg = LayerDefinition.Quote(polygons.Layer.RequireGeometryColumn());
        string qg = LayerDefinition.Quote(points.Layer.RequireGeometryColumn());
        string id = LayerDefinition.Quote(polygons.Layer.IntegerIdentityColumn ?? polygons.Layer.IdentityColumn);
        string srid = polygons.Layer.Srid.ToString(CultureInfo.InvariantCulture);
        int output = polygons.OutputSrid ?? polygons.Layer.Srid;

        string[] carried = [.. polygons.Columns.Select(LayerDefinition.Quote)];
        string[] summaries = [.. plan.Summaries.Select(s => $"{s.Statistic}(q.{LayerDefinition.Quote(s.Column)}) as {LayerDefinition.Quote(s.Output)}")];
        string[] outputs = [.. carried, "point_count", .. plan.Summaries.Select(s => LayerDefinition.Quote(s.Output))];

        string counted =
            $"select p.{id} as {AnalysisOrigin}, count(*)::integer as n{string.Concat(summaries.Select(s => ", " + s))} "
            + $"from p join q on ST_Intersects(p.{pg}, q.{SecondGeometry}) group by p.{id}";

        string insert =
            $"insert into {Qualified(table)} ({string.Concat(outputs.Select(c => c + ", "))}geom) "
            + $"with p as (select * from {Qualified(polygons.Layer.TableName)}{Filter(polygons, pg)}), "
            + $"q as (select *, ST_Transform({qg}, {srid}) as {SecondGeometry} from {Qualified(points.Layer.TableName)}{Filter(points, qg, "v")}) "
            + $"select {string.Concat(carried.Select(c => "p." + c + ", "))}coalesce(s.n, 0)"
            + string.Concat(plan.Summaries.Select(s => ", s." + LayerDefinition.Quote(s.Output)))
            + $", ST_Multi(ST_CollectionExtract({ToSrid("p." + pg, polygons.Layer.Srid, output)}, 3)) "
            + $"from p {(plan.KeepEmpty ? "left " : string.Empty)}join ({counted}) s on s.{AnalysisOrigin} = p.{id} "
            + $"where p.{pg} is not null";

        string shape =
            $"create table {Qualified(table)} as select {string.Concat(carried.Select(c => "p." + c + ", "))}0::integer as point_count"
            + string.Concat(plan.Summaries.Select(s => $", {s.Statistic}(q.{LayerDefinition.Quote(s.Column)}) as {LayerDefinition.Quote(s.Output)}"))
            + $" from {Qualified(polygons.Layer.TableName)} p cross join {Qualified(points.Layer.TableName)} q where false"
            + (carried.Length == 0 ? string.Empty : " group by " + string.Join(", ", carried.Select(c => "p." + c)))
            + " limit 0";

        return await WriteAnalysisAsync(table, shape, insert, output, command =>
        {
            Bind(command, polygons);
            Bind(command, points, "v");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string ToSrid(string geometry, int from, int to) =>
        from == to ? geometry : $"ST_Transform({geometry}, {to.ToString(CultureInfo.InvariantCulture)})";
}
