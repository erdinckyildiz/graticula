using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;

namespace Graticula.Providers.PostGis;

/// <summary>Bins generated over the summarised features, instead of a polygon layer — ADR-222.</summary>
/// <param name="Hexagon">Hexagons rather than squares.</param>
/// <param name="SizeMetres">A square's side, or a hexagon's distance between parallel sides, on the ground.</param>
public sealed record BinPlan(bool Hexagon, double SizeMetres);

/// <summary>What a summary of features inside areas measures besides its fields — ADR-222.</summary>
public enum SummaryMeasure
{
    /// <summary>Nothing: the count only, or not even that.</summary>
    None = 0,

    /// <summary>The length of the lines inside each area.</summary>
    Length = 1,

    /// <summary>The area of the polygons inside each area.</summary>
    Area = 2,
}

/// <summary>What <c>SummarizeWithin</c> and <c>AggregatePoints</c> were asked — ADR-222.</summary>
/// <param name="CountName">The count's column, or null for no count.</param>
/// <param name="Measure">Whether the inside length or area is summed.</param>
/// <param name="MeasureName">That sum's column.</param>
/// <param name="MetresPerMeasureUnit">Metres per unit of length, or square metres per unit of area.</param>
/// <param name="Summaries">Each statistic of a summarised field and its column.</param>
/// <param name="KeepEmpty">Whether an area with nothing in it is kept; bins never are.</param>
/// <param name="Bins">Bins in place of a polygon layer, or null.</param>
/// <param name="Weighted">
/// Whether statistics are proportional to the part of each line or polygon inside the area, as ArcGIS documents for
/// lines and polygons; points are not weighted.
/// </param>
/// <param name="Group">Statistics for each value of a field as well, in a table of their own, or null.</param>
public sealed record SummaryPlan(
    string? CountName,
    SummaryMeasure Measure,
    string? MeasureName,
    double MetresPerMeasureUnit,
    IReadOnlyList<(string Column, string Statistic, string Output)> Summaries,
    bool KeepEmpty,
    BinPlan? Bins,
    bool Weighted,
    GroupPlan? Group = null);

/// <summary>
/// A summary by the values of a field — ADR-222: the statistics again for each value in each area, in a table that names
/// its area by <c>join_id</c>, and, on the areas, the value that holds the most and the least.
/// </summary>
/// <param name="Column">The summarised layer's field whose values are grouped.</param>
/// <param name="MajorityName">The areas' column for the value with the most, or null for none.</param>
/// <param name="MinorityName">The areas' column for the value with the least, or null for none.</param>
/// <param name="PercentName">The group table's column for each value's share of its area, or null for none.</param>
public sealed record GroupPlan(string Column, string? MajorityName, string? MinorityName, string? PercentName);

/// <summary>What <c>JoinFeatures</c> was asked — ADR-222.</summary>
/// <param name="Relationship">
/// The spatial relationship, as ArcGIS names it (<c>Intersects</c>, <c>WithinDistance</c>, <c>Contains</c>, <c>Within</c>,
/// <c>CompletelyContains</c>, <c>CompletelyWithin</c>, <c>IdenticalTo</c>), or null for an attribute join only.
/// </param>
/// <param name="DistanceMetres">The distance for <c>WithinDistance</c>.</param>
/// <param name="Attributes">Target and join columns whose values must be equal.</param>
/// <param name="OneToMany">One output feature per matching pair, rather than one per target.</param>
/// <param name="KeepAll">Whether a target with no match is kept — <c>joinType: Left</c>.</param>
/// <param name="JoinColumns">The join layer's columns the output carries, renamed where they clash.</param>
/// <param name="CountName">The count's column, in a one-to-one join.</param>
/// <param name="Summaries">Statistics of the join layer's columns, in a one-to-one join.</param>
/// <param name="OrderBy">Which match a one-to-one join without summaries keeps: columns and directions.</param>
/// <param name="Output">The target's kind of geometry.</param>
/// <param name="SinglePoints">Whether the target holds single points, kept as points.</param>
public sealed record JoinPlan(
    string? Relationship,
    double DistanceMetres,
    IReadOnlyList<(string Target, string Join)> Attributes,
    bool OneToMany,
    bool KeepAll,
    IReadOnlyList<(string Source, string Output)> JoinColumns,
    string CountName,
    IReadOnlyList<(string Column, string Statistic, string Output)> Summaries,
    IReadOnlyList<(string Column, bool Descending)> OrderBy,
    AnalysisShape Output,
    bool SinglePoints);

/// <summary><c>SummarizeWithin</c>, bins and <c>JoinFeatures</c> — ADR-222 condition 3.</summary>
/// <remarks>
/// <para>
/// <b>Bins are drawn in Web Mercator, scaled to the ground at the middle of the features.</b> A metre of Web Mercator is
/// <c>1 / cos(latitude)</c> metres on the ground, so the size is divided by that at the centre of the summarised
/// features' extent: a bin is its size on the ground there, and grows or shrinks with the projection away from it, as
/// a grid in any one projected reference must. PostGIS's <c>ST_HexagonGrid</c> takes an edge, and a hexagon's distance
/// between parallel sides — what ArcGIS documents as its size — is the edge times √3.
/// </para>
/// <para>
/// <b>Lines and polygons count what is inside, in proportion</b> — ArcGIS documents its statistics for them as using the
/// part of each feature inside the area, with a weighted mean and standard deviation. A sum is the value times the
/// inside fraction; a mean and a standard deviation are weighted by the inside length or area; a minimum and a maximum
/// are of the features that reach inside. A line that only touches an area's edge has no length inside and is not
/// counted.
/// </para>
/// </remarks>
public sealed partial class PostGisImporter
{
    private const string AreaId = "__aid";
    private const string AreaGeometry = "__ag";
    private const string Part = "__part";
    private const string Whole = "__whole";

    /// <summary>
    /// <c>SummarizeWithin</c>, and <c>AggregatePoints</c> as its case of points: features of one layer summarised in each
    /// polygon of another, or in bins.
    /// </summary>
    /// <param name="areas">The polygons, or null when <see cref="SummaryPlan.Bins"/> gives them.</param>
    /// <param name="summarized">The features summarised.</param>
    /// <param name="shape">What kind of features they are.</param>
    /// <param name="plan">What is counted, measured and summarised.</param>
    /// <param name="table">The table to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table, its row count and its reference.</returns>
    public async Task<ImportResult> SummarizeAsync(
        AnalysisInput? areas, AnalysisInput summarized, AnalysisShape shape, SummaryPlan plan, string table,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summarized);
        ArgumentNullException.ThrowIfNull(plan);

        if (areas is null == plan.Bins is null)
        {
            throw new ArgumentException("Either areas or bins, not both and not neither.", nameof(plan));
        }

        if (areas is not null)
        {
            RefuseOutsideHosted(areas.Layer.SchemaName, areas.Layer.TableName, "analyse");
        }

        RefuseOutsideHosted(summarized.Layer.SchemaName, summarized.Layer.TableName, "analyse");
        RefuseOutsideHosted(HostedSchema, table, "make");

        string sg = LayerDefinition.Quote(summarized.Layer.RequireGeometryColumn());
        int areaSrid = areas?.Layer.Srid ?? summarized.Layer.Srid;
        int output = (areas ?? summarized).OutputSrid ?? areaSrid;
        string[] carried = areas is null ? [] : [.. areas.Columns.Select(LayerDefinition.Quote)];
        string from = string.Concat(carried.Select(c => "s." + c + ", "));

        string areaCte;

        if (areas is not null)
        {
            string pg = LayerDefinition.Quote(areas.Layer.RequireGeometryColumn());
            string id = LayerDefinition.Quote(areas.Layer.IntegerIdentityColumn ?? areas.Layer.IdentityColumn);
            areaCte =
                $"p as (select {from}s.{id}::integer as {AreaId}, s.{pg} as {AreaGeometry} "
                + $"from (select * from {Qualified(areas.Layer.TableName)}{Filter(areas, pg)}) s where s.{pg} is not null)";
        }
        else
        {
            string grid = plan.Bins!.Hexagon ? "ST_HexagonGrid" : "ST_SquareGrid";
            string size = plan.Bins.Hexagon ? "(@binsize / sqrt(3))" : "@binsize";
            areaCte =
                $"x as (select ST_SetSRID(ST_Extent(ST_Transform(s.{sg}, 3857))::geometry, 3857) as e "
                + $"from (select * from {Qualified(summarized.Layer.TableName)}{Filter(summarized, sg, "v")}) s where s.{sg} is not null), "
                + $"p as (select row_number() over ()::integer as {AreaId}, ST_Transform(c.geom, {Srid(areaSrid)}) as {AreaGeometry} "
                + $"from x cross join lateral {grid}({size} / cos(radians(ST_Y(ST_Transform(ST_Centroid(x.e), 4326)))), x.e) c "
                + "where x.e is not null)";
        }

        // A line's or a polygon's measure on the ground, inside the area and whole.
        string Measured(string geometry) => shape switch
        {
            AnalysisShape.Lines => $"ST_Length(ST_Transform({geometry}, 4326)::geography)",
            AnalysisShape.Polygons => $"ST_Area(ST_Transform({geometry}, 4326)::geography)",
            _ => "1",
        };

        bool weighted = plan.Weighted && shape != AnalysisShape.Points;
        string q =
            $"q as (select s.*, ST_Transform(s.{sg}, {Srid(areaSrid)}) as {SecondGeometry}"
            + (weighted ? $", {Measured("ST_Transform(s." + sg + ", " + Srid(areaSrid) + ")")} as {Whole}" : string.Empty)
            + $" from (select * from {Qualified(summarized.Layer.TableName)}{Filter(summarized, sg, "v")}) s where s.{sg} is not null)";

        string pairs =
            $"j as (select p.{AreaId}, q.*"
            + (shape == AnalysisShape.Points ? string.Empty : $", {Measured($"ST_Intersection(p.{AreaGeometry}, q.{SecondGeometry})")} as {Part}")
            + $" from p join q on ST_Intersects(p.{AreaGeometry}, q.{SecondGeometry}))";

        List<string> statistics = [];

        if (plan.CountName is not null)
        {
            statistics.Add("count(*)::integer as __n");
        }

        if (plan.Measure != SummaryMeasure.None)
        {
            statistics.Add($"sum({Part}) / @measureunit as __m");
        }

        foreach ((string column, string statistic, string name) in plan.Summaries)
        {
            statistics.Add($"{Statistic(LayerDefinition.Quote(column), statistic, weighted)} as {LayerDefinition.Quote(name)}");
        }

        string inside = shape == AnalysisShape.Points ? string.Empty : $" where {Part} > 0";
        string grouped =
            $"s as (select {AreaId}{string.Concat(statistics.Select(s => ", " + s))} from j{inside} group by {AreaId})";

        // By the group's values too: the count and measure always, for the majority, the minority and the shares.
        string? groupColumn = plan.Group is null ? null : LayerDefinition.Quote(plan.Group.Column);
        List<string> groupStatistics = ["count(*)::integer as __n"];

        if (shape != AnalysisShape.Points)
        {
            groupStatistics.Add($"sum({Part}) / @measureunit as __m");
        }

        groupStatistics.AddRange(statistics.Where(s => !s.EndsWith(" as __n", StringComparison.Ordinal) && !s.EndsWith(" as __m", StringComparison.Ordinal)));
        string groups = groupColumn is null
            ? string.Empty
            : $", g as (select {AreaId}, {groupColumn} as __grp, {string.Join(", ", groupStatistics)} from j{inside} group by {AreaId}, {groupColumn})";
        string weight = shape == AnalysisShape.Points ? "g.__n" : "g.__m";

        List<string> outputs = [.. carried];
        List<string> values = [.. carried.Select(c => "p." + c)];

        if (plan.CountName is not null)
        {
            outputs.Add(LayerDefinition.Quote(plan.CountName));
            values.Add("coalesce(s.__n, 0)");
        }

        if (plan.Measure != SummaryMeasure.None)
        {
            outputs.Add(LayerDefinition.Quote(plan.MeasureName!));
            values.Add("coalesce(s.__m, 0)");
        }

        outputs.AddRange(plan.Summaries.Select(s => LayerDefinition.Quote(s.Output)));
        values.AddRange(plan.Summaries.Select(s => "s." + LayerDefinition.Quote(s.Output)));

        if (plan.Group is { } group)
        {
            outputs.Add("join_id");
            values.Add($"p.{AreaId}");

            if (group.MajorityName is not null)
            {
                outputs.Add(LayerDefinition.Quote(group.MajorityName));
                values.Add($"(select g.__grp from g where g.{AreaId} = p.{AreaId} order by {weight} desc, g.__grp limit 1)");
            }

            if (group.MinorityName is not null)
            {
                outputs.Add(LayerDefinition.Quote(group.MinorityName));
                values.Add($"(select g.__grp from g where g.{AreaId} = p.{AreaId} order by {weight}, g.__grp limit 1)");
            }
        }

        bool keep = plan.KeepEmpty && plan.Bins is null;
        string insert =
            $"insert into {Qualified(table)} ({string.Concat(outputs.Select(c => c + ", "))}geom) "
            + $"with {areaCte}, {q}, {pairs}, {grouped}{groups} "
            + $"select {string.Concat(values.Select(v => v + ", "))}ST_Multi(ST_CollectionExtract({ToSrid("p." + AreaGeometry, areaSrid, output)}, 3)) "
            + $"from p {(keep ? "left " : string.Empty)}join s on s.{AreaId} = p.{AreaId}";

        // The empty table: the areas' own columns, an integer count, a double measure, and each statistic typed by the
        // aggregate itself — or a double where it is weighted.
        List<string> typed = [.. carried.Select(c => "a." + c)];

        if (plan.CountName is not null)
        {
            typed.Add($"0::integer as {LayerDefinition.Quote(plan.CountName)}");
        }

        if (plan.Measure != SummaryMeasure.None)
        {
            typed.Add($"0::double precision as {LayerDefinition.Quote(plan.MeasureName!)}");
        }

        foreach ((string column, string statistic, string name) in plan.Summaries)
        {
            typed.Add(weighted && statistic is not ("min" or "max")
                ? $"null::double precision as {LayerDefinition.Quote(name)}"
                : $"{statistic}(q.{LayerDefinition.Quote(column)}) as {LayerDefinition.Quote(name)}");
        }

        string source = areas is null
            ? $"{Qualified(summarized.Layer.TableName)} q"
            : $"{Qualified(areas.Layer.TableName)} a cross join {Qualified(summarized.Layer.TableName)} q";
        string? then = null;
        string groupShape = string.Empty;

        if (plan.Group is { } grouping)
        {
            string column = LayerDefinition.Quote(grouping.Column);
            typed.Add("0::integer as join_id");

            if (grouping.MajorityName is not null)
            {
                typed.Add($"min(q.{column}) as {LayerDefinition.Quote(grouping.MajorityName)}");
            }

            if (grouping.MinorityName is not null)
            {
                typed.Add($"min(q.{column}) as {LayerDefinition.Quote(grouping.MinorityName)}");
            }

            // The group table: the area's id, the value, and the statistics again — named as the areas' are.
            string groupTable = Qualified(GroupTableFor(table));
            List<string> groupTyped = ["0::integer as join_id", $"min(q.{column}) as {column}"];
            List<string> groupOutputs = ["join_id", column];
            List<string> groupValues = [$"g.{AreaId}", "g.__grp"];

            if (plan.CountName is not null)
            {
                groupTyped.Add($"0::integer as {LayerDefinition.Quote(plan.CountName)}");
                groupOutputs.Add(LayerDefinition.Quote(plan.CountName));
                groupValues.Add("g.__n");
            }

            if (plan.Measure != SummaryMeasure.None)
            {
                groupTyped.Add($"0::double precision as {LayerDefinition.Quote(plan.MeasureName!)}");
                groupOutputs.Add(LayerDefinition.Quote(plan.MeasureName!));
                groupValues.Add("g.__m");
            }

            foreach ((string summaryColumn, string statistic, string name) in plan.Summaries)
            {
                groupTyped.Add(weighted && statistic is not ("min" or "max")
                    ? $"null::double precision as {LayerDefinition.Quote(name)}"
                    : $"{statistic}(q.{LayerDefinition.Quote(summaryColumn)}) as {LayerDefinition.Quote(name)}");
                groupOutputs.Add(LayerDefinition.Quote(name));
                groupValues.Add("g." + LayerDefinition.Quote(name));
            }

            if (grouping.PercentName is not null)
            {
                groupTyped.Add($"0::double precision as {LayerDefinition.Quote(grouping.PercentName)}");
                groupOutputs.Add(LayerDefinition.Quote(grouping.PercentName));
                groupValues.Add($"100.0 * {weight} / nullif(sum({weight}) over (partition by g.{AreaId}), 0)");
            }

            groupShape =
                $"; drop table if exists {groupTable}; "
                + $"create table {groupTable} as select {string.Join(", ", groupTyped)} from {source} where false limit 0; "
                + $"alter table {groupTable} add column objectid integer generated always as identity primary key";
            then =
                $"insert into {groupTable} ({string.Join(", ", groupOutputs)}) "
                + $"with {areaCte}, {q}, {pairs}{groups} "
                + $"select {string.Join(", ", groupValues)} from g order by g.{AreaId}, g.__grp";
        }

        string shapeSql =
            $"create table {Qualified(table)} as select {string.Join(", ", typed)} from {source}"
            + " where false"
            + (carried.Length == 0 ? string.Empty : " group by " + string.Join(", ", carried.Select(c => "a." + c)))
            + " limit 0"
            + groupShape;

        return await WriteAnalysisAsync(table, shapeSql, insert, output, command =>
        {
            if (areas is not null)
            {
                Bind(command, areas);
            }

            Bind(command, summarized, "v");

            if (plan.Bins is not null)
            {
                command.Parameters.AddWithValue("binsize", plan.Bins.SizeMetres);
            }

            if (plan.Measure != SummaryMeasure.None || (plan.Group is not null && shape != AnalysisShape.Points))
            {
                command.Parameters.AddWithValue("measureunit", plan.MetresPerMeasureUnit);
            }
        }, cancellationToken, then: then).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>JoinFeatures</c>: the target's features with the join layer's attributes, by a spatial relationship, equal
    /// values, or both.
    /// </summary>
    /// <param name="target">The features that receive the attributes.</param>
    /// <param name="join">The features whose attributes are joined.</param>
    /// <param name="plan">How they match and what is kept.</param>
    /// <param name="table">The table to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The table, its row count and its reference.</returns>
    public async Task<ImportResult> JoinAsync(
        AnalysisInput target, AnalysisInput join, JoinPlan plan, string table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(join);
        ArgumentNullException.ThrowIfNull(plan);
        RefuseOutsideHosted(target.Layer.SchemaName, target.Layer.TableName, "analyse");
        RefuseOutsideHosted(join.Layer.SchemaName, join.Layer.TableName, "analyse");
        RefuseOutsideHosted(HostedSchema, table, "make");

        if (plan.Relationship is null && plan.Attributes.Count == 0)
        {
            throw new ArgumentException("A join needs a spatial relationship, equal attributes, or both.", nameof(plan));
        }

        string tg = LayerDefinition.Quote(target.Layer.RequireGeometryColumn());
        string jg = LayerDefinition.Quote(join.Layer.RequireGeometryColumn());
        string tid = LayerDefinition.Quote(target.Layer.IntegerIdentityColumn ?? target.Layer.IdentityColumn);
        int srid = target.Layer.Srid;
        int output = target.OutputSrid ?? srid;
        string dimension = ((int)plan.Output).ToString(CultureInfo.InvariantCulture);
        string[] carried = [.. target.Columns.Select(LayerDefinition.Quote)];

        List<string> matched = [];

        if (plan.Relationship is { } relationship)
        {
            string t = $"t.{tg}";
            string j = $"j.{SecondGeometry}";
            matched.Add(relationship switch
            {
                "Intersects" => $"ST_Intersects({t}, {j})",
                "Contains" => $"ST_Contains({t}, {j})",
                "Within" => $"ST_Within({t}, {j})",
                "CompletelyContains" => $"ST_ContainsProperly({t}, {j})",
                "CompletelyWithin" => $"ST_ContainsProperly({j}, {t})",
                "IdenticalTo" => $"ST_Equals({t}, {j})",

                // On the ground, as a buffer's distance is (§5.8c); a box test first so the index can narrow it.
                "WithinDistance" =>
                    $"ST_DWithin(ST_Transform({t}, 4326)::geography, ST_Transform({j}, 4326)::geography, @joindistance)",
                _ => throw new ArgumentOutOfRangeException(nameof(plan), relationship, "Not a relationship JoinFeatures takes."),
            });
        }

        matched.AddRange(plan.Attributes.Select(a =>
            $"t.{LayerDefinition.Quote(a.Target)} = j.{LayerDefinition.Quote(a.Join)}"));

        string on = string.Join(" and ", matched);
        string ctes =
            $"with t as (select * from {Qualified(target.Layer.TableName)}{Filter(target, tg)}), "
            + $"jn as (select *, ST_Transform({jg}, {Srid(srid)}) as {SecondGeometry} "
            + $"from {Qualified(join.Layer.TableName)}{Filter(join, jg, "v")}) ";

        string geometry = plan.SinglePoints
            ? ToSrid("t." + tg, srid, output)
            : $"ST_Multi(ST_CollectionExtract({ToSrid("t." + tg, srid, output)}, {dimension}))";

        List<string> outputs = [.. carried];
        List<string> values = [.. carried.Select(c => "t." + c)];
        List<string> typed = [.. carried.Select(c => "t." + c)];
        string body;

        if (plan.OneToMany)
        {
            outputs.AddRange(plan.JoinColumns.Select(c => LayerDefinition.Quote(c.Output)));
            values.AddRange(plan.JoinColumns.Select(c => "j." + LayerDefinition.Quote(c.Source)));
            typed.AddRange(plan.JoinColumns.Select(c => $"j.{LayerDefinition.Quote(c.Source)} as {LayerDefinition.Quote(c.Output)}"));

            body =
                $"select {string.Concat(values.Select(v => v + ", "))}{geometry} from t "
                + $"{(plan.KeepAll ? "left join" : "join")} jn j on {on} where t.{tg} is not null";
        }
        else
        {
            List<string> statistics = ["count(*)::integer as __n"];
            statistics.AddRange(plan.Summaries.Select(s =>
                $"{s.Statistic}(j.{LayerDefinition.Quote(s.Column)}) as {LayerDefinition.Quote(s.Output)}"));

            outputs.Add(LayerDefinition.Quote(plan.CountName));
            values.Add("m.__n");
            typed.Add($"0::integer as {LayerDefinition.Quote(plan.CountName)}");

            outputs.AddRange(plan.Summaries.Select(s => LayerDefinition.Quote(s.Output)));
            values.AddRange(plan.Summaries.Select(s => "m." + LayerDefinition.Quote(s.Output)));
            typed.AddRange(plan.Summaries.Select(s =>
                $"{s.Statistic}(j.{LayerDefinition.Quote(s.Column)}) as {LayerDefinition.Quote(s.Output)}"));

            // Without summaries, one matching record's attributes, chosen by recordsToMatch's order or the join
            // layer's own id order.
            string kept = string.Empty;

            if (plan.Summaries.Count == 0 && plan.JoinColumns.Count > 0)
            {
                string jid = LayerDefinition.Quote(join.Layer.IntegerIdentityColumn ?? join.Layer.IdentityColumn);
                string order = plan.OrderBy.Count == 0
                    ? $"j.{jid}"
                    : string.Join(", ", plan.OrderBy.Select(o => $"j.{LayerDefinition.Quote(o.Column)}{(o.Descending ? " desc" : string.Empty)}"));

                outputs.AddRange(plan.JoinColumns.Select(c => LayerDefinition.Quote(c.Output)));
                values.AddRange(plan.JoinColumns.Select(c => "one." + LayerDefinition.Quote(c.Output)));
                typed.AddRange(plan.JoinColumns.Select(c => $"j.{LayerDefinition.Quote(c.Source)} as {LayerDefinition.Quote(c.Output)}"));

                kept =
                    $" left join lateral (select {string.Join(", ", plan.JoinColumns.Select(c => $"j.{LayerDefinition.Quote(c.Source)} as {LayerDefinition.Quote(c.Output)}"))} "
                    + $"from jn j where {on} order by {order} limit 1) one on true";
            }

            body =
                $"select {string.Concat(values.Select(v => v + ", "))}{geometry} from t "
                + $"cross join lateral (select {string.Join(", ", statistics)} from jn j where {on}) m{kept} "
                + $"where t.{tg} is not null{(plan.KeepAll ? string.Empty : " and m.__n > 0")} order by t.{tid}";
        }

        string insert = $"insert into {Qualified(table)} ({string.Concat(outputs.Select(c => c + ", "))}geom) {ctes}{body}";

        string group = string.Join(", ", [.. carried.Select(c => "t." + c),
            .. (plan.OneToMany || plan.Summaries.Count == 0 ? plan.JoinColumns.Select(c => "j." + LayerDefinition.Quote(c.Source)) : [])]);
        string shapeSql =
            $"create table {Qualified(table)} as select {string.Join(", ", typed)} "
            + $"from {Qualified(target.Layer.TableName)} t cross join {Qualified(join.Layer.TableName)} j where false"
            + (group.Length == 0 ? string.Empty : " group by " + group)
            + " limit 0";

        return await WriteAnalysisAsync(table, shapeSql, insert, output, command =>
        {
            Bind(command, target);
            Bind(command, join, "v");

            if (plan.Relationship == "WithinDistance")
            {
                command.Parameters.AddWithValue("joindistance", plan.DistanceMetres);
            }
        }, cancellationToken, plan.Output, plan.SinglePoints).ConfigureAwait(false);
    }

    /// <summary>A statistic of a column, plain or weighted by the part of each feature inside the area.</summary>
    private static string Statistic(string column, string statistic, bool weighted)
    {
        if (!weighted)
        {
            return $"{statistic}({column})";
        }

        string w = Part;
        string fraction = $"({w} / nullif({Whole}, 0))";

        return statistic switch
        {
            "sum" => $"sum({column} * {fraction})",
            "avg" => $"sum({column} * {w}) / nullif(sum({w}), 0)",
            "stddev_samp" =>
                $"sqrt(greatest(sum({w} * {column} * {column}) / nullif(sum({w}), 0) - power(sum({w} * {column}) / nullif(sum({w}), 0), 2), 0))",
            _ => $"{statistic}({column})",
        };
    }

    private static string Srid(int srid) => srid.ToString(CultureInfo.InvariantCulture);
}
