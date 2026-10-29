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
public sealed record OverlayPlan(string Operation, AnalysisShape Output, IReadOnlyList<(string Source, string Output)> OverlayColumns)
{
    /// <summary>
    /// Whether an intersection is of the layers' linework — a polygon's boundary, a line itself — for <c>outputType</c>
    /// <c>Line</c> or <c>Point</c>, rather than of their areas.
    /// </summary>
    public bool Linework { get; init; }

    /// <summary>How near vertices snap to each other, in the input layer's units; 0 for no snapping.</summary>
    public double Tolerance { get; init; }

    /// <summary>Whether only the overlay's vertices move, to the input's — <c>snapToInput</c>.</summary>
    public bool SnapToInput { get; init; }
}

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

        // Snapping, before anything is cut: the overlay's vertices to the input's within the tolerance, and, unless
        // snapToInput, the input's to the overlay's as well — each against only the features within reach.
        string ag = a;

        if (plan.Tolerance > 0)
        {
            fromB =
                $"select *, coalesce(ST_Snap(ST_Transform({b}, {srid}), (select ST_Collect(x.{a}) from {Qualified(input.Layer.TableName)} x "
                + $"where x.{a} && ST_Expand(ST_Transform(o.{b}, {srid}), @tolerance)), @tolerance), ST_Transform({b}, {srid})) as {SecondGeometry} "
                + $"from {Qualified(overlay.Layer.TableName)} o{Filter(overlay, b, "v")}";

            if (!plan.SnapToInput)
            {
                ag = "__a2";
                fromA =
                    $"select *, coalesce(ST_Snap({a}, (select ST_Collect(ST_Transform(x.{b}, {srid})) from {Qualified(overlay.Layer.TableName)} x "
                    + $"where ST_Transform(x.{b}, {srid}) && ST_Expand(i.{a}, @tolerance)), @tolerance), {a}) as {ag} "
                    + $"from {Qualified(input.Layer.TableName)} i{Filter(input, a)}";
            }
        }

        // outputType Line or Point: what the layers' linework has in common — a polygon by its boundary.
        string Lines(string g) => plan.Linework ? $"(case when ST_Dimension({g}) = 2 then ST_Boundary({g}) else {g} end)" : g;

        string aSelect(string alias) => string.Concat(aColumns.Select(c => $"{alias}.{c}, "));
        string bSelect(string alias) => string.Concat(bColumns.Select((c, i) => $"{alias}.{c} as {bOutputs[i]}, "));
        string aNulls = string.Concat(aColumns.Select(c => $"null as {c}, "));
        string bNulls = string.Concat(bOutputs.Select(c => $"null as {c}, "));

        string intersect =
            $"select {aSelect("a")}{bSelect("b")}ST_Intersection({Lines("a." + ag)}, {Lines("b." + SecondGeometry)}) as {AnalysisGeometry} "
            + $"from fa a join fb b on ST_Intersects(a.{ag}, b.{SecondGeometry})";

        string inputLeft =
            $"select {aSelect("a")}{bNulls}coalesce(ST_Difference(a.{ag}, u.g), a.{ag}) as {AnalysisGeometry} from fa a "
            + $"left join lateral (select ST_Union(b.{SecondGeometry}) as g from fb b where ST_Intersects(a.{ag}, b.{SecondGeometry})) u on true";

        string overlayLeft =
            $"select {aNulls}{bSelect("b")}coalesce(ST_Difference(b.{SecondGeometry}, u.g), b.{SecondGeometry}) as {AnalysisGeometry} from fb b "
            + $"left join lateral (select ST_Union(a.{ag}) as g from fa a where ST_Intersects(a.{ag}, b.{SecondGeometry})) u on true";

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

            if (plan.Tolerance > 0)
            {
                command.Parameters.AddWithValue("tolerance", plan.Tolerance);
            }
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

    /// <summary>
    /// <c>FindCentroids</c>: a point per feature, its centre — or, with <paramref name="inside"/>, a point sure to lie on
    /// it, which a crescent's or a ring's centre is not — keeping the feature's columns.
    /// </summary>
    /// <param name="input">The layer, lines or polygons.</param>
    /// <param name="inside">Whether the point must lie on the feature (<c>pointLocation</c>).</param>
    /// <param name="table">The table to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table, its row count and its reference.</returns>
    public async Task<ImportResult> CentroidsAsync(AnalysisInput input, bool inside, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        RefuseOutsideHosted(input.Layer.SchemaName, input.Layer.TableName, "analyse");
        RefuseOutsideHosted(HostedSchema, table, "make");

        string g = LayerDefinition.Quote(input.Layer.RequireGeometryColumn());
        int output = input.OutputSrid ?? input.Layer.Srid;
        string[] carried = [.. input.Columns.Select(LayerDefinition.Quote)];
        string point = inside ? $"ST_PointOnSurface(s.{g})" : $"ST_Centroid(s.{g})";

        string insert =
            $"insert into {Qualified(table)} ({string.Concat(carried.Select(c => c + ", "))}geom) "
            + $"select {string.Concat(carried.Select(c => "s." + c + ", "))}{ToSrid(point, input.Layer.Srid, output)} "
            + $"from (select * from {Qualified(input.Layer.TableName)}{Filter(input, g)}) s where s.{g} is not null and not ST_IsEmpty(s.{g})";

        string shape = carried.Length == 0
            ? $"create table {Qualified(table)} (\"_\" boolean)"
            : $"create table {Qualified(table)} as select {string.Join(", ", carried.Select(c => "s." + c))} "
              + $"from {Qualified(input.Layer.TableName)} s where false";

        ImportResult made = await WriteAnalysisAsync(table, shape, insert, output, command => Bind(command, input), cancellationToken, AnalysisShape.Points, single: true)
            .ConfigureAwait(false);

        if (carried.Length == 0)
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, $"alter table {Qualified(table)} drop column \"_\"", cancellationToken).ConfigureAwait(false);
        }

        return made;
    }

    private static string ToSrid(string geometry, int from, int to) =>
        from == to ? geometry : $"ST_Transform({geometry}, {to.ToString(CultureInfo.InvariantCulture)})";
}
