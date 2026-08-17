using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Linq;
using GisServer.Api.ArcGis;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;
using GisServer.Platform.Catalog;
using Microsoft.AspNetCore.Http;

namespace GisServer.Host;

/// <summary>
/// What the caller asked the query response to be.
/// </summary>
/// <remarks>
/// <b>One enum rather than four booleans, because they are exclusive.</b>
/// <c>returnCountOnly</c>, <c>returnIdsOnly</c>, <c>returnExtentOnly</c> and
/// <c>outStatistics</c> each replace the response with something else entirely,
/// and a request carrying two of them is refused rather than ranked — guessing a
/// precedence means answering one question and silently dropping the other.
/// </remarks>
internal enum QueryShape
{
    /// <summary>The features themselves.</summary>
    Features,

    /// <summary>How many matched.</summary>
    Count,

    /// <summary>Their object ids.</summary>
    Ids,

    /// <summary>The box around them, and how many.</summary>
    Extent,

    /// <summary>Computed aggregates instead of features.</summary>
    Statistics,
}

/// <summary>
/// Parses the ArcGIS FeatureServer <c>query</c> parameters we support.
/// </summary>
/// <remarks>
/// <para>
/// ADR-008 §2: a parameter we do not implement is refused with a reason rather
/// than accepted and ignored, because a client that asks for
/// <c>outStatistics</c> and receives features has been lied to.
/// </para>
/// <para>
/// <b>The supported set grew when a real client was pointed at it.</b> The first
/// version refused <c>where</c>, <c>outFields=*</c>, <c>resultOffset</c> and
/// <c>returnCountOnly</c> — all four of which the ArcGIS Maps SDK sends as a
/// matter of course, so it failed on its first request. What follows is the
/// difference between a surface that is defensible on paper and one a client can
/// actually use.
/// </para>
/// <para>
/// <b>Three kinds of parameter live here.</b> Supported ones change the answer.
/// <em>Accepted-and-ignored</em> ones cannot change the answer in a way that
/// loses anything — asking for less precision and getting full precision is not
/// a degradation — and each is listed with why. Refused ones would change the
/// answer and we cannot produce it.
/// </para>
/// </remarks>
internal static class FeatureServerQueryParameters
{
    /// <summary>Parameters that change the answer and that we do not implement.</summary>
    private static readonly string[] RefusedParameters =
    [
        // <b>What is left after 2026-08-15, and each is absent for a reason
        // that is not effort.</b> The rest of this list became implemented that
        // day; these five cannot be implemented without something the product
        // does not have.
        //
        //   time                  — no layer declares timeInfo, so there is no
        //                           field to filter on and no extent to report.
        //   gdbVersion            — there is no version tree.
        //   historicMoment        — there is no history.
        //   fullText              — needs a tsvector column and an index nobody
        //                           has asked us to create on their table.
        //   uniqueIds /
        //   returnUniqueIdsOnly   — a 12.1 concept with no counterpart here.
        "time", "fullText", "uniqueIds", "returnUniqueIdsOnly",
    ];

    /// <summary>
    /// Parameters accepted and ignored, with the reason each is harmless.
    /// </summary>
    /// <remarks>
    /// <b>Every entry here is a claim that ignoring it cannot lose data.</b>
    /// Quantization and generalisation both ask for <em>less</em> — coarser
    /// coordinates, fewer vertices — so ignoring them returns more than was
    /// asked for, which costs bandwidth and never accuracy. If an entry is ever
    /// added whose omission changes an answer, this list has become the silent
    /// degradation ADR-008 §2 forbids.
    /// </remarks>
    private static readonly Dictionary<string, string> IgnoredParameters = new(StringComparer.Ordinal)
    {
        ["quantizationParameters"] = "coordinates are returned at full precision",

        // <b>Refused for half an hour, on a misreading.</b> returnCentroid does
        // not replace the geometry — it asks for a centroid property *alongside*
        // it, which the ArcGIS SDK uses to place labels on polygons. Refusing it
        // broke every polygon layer in the SDK to prevent a harm that does not
        // exist: not returning extra data is not returning wrong data. The
        // visible consequence of ignoring it is that labels fall back to the
        // client's own placement, which is a missing capability rather than a
        // wrong answer.
        ["returnCentroid"] = "no centroid is computed, so labels use the client's own placement",
        ["maxAllowableOffset"] = "geometry is returned ungeneralised",
        ["returnExceededLimitFeatures"] = "the transfer limit is reported either way",
        ["cacheHint"] = "nothing is cached yet",
        ["datumTransformation"] = "no reprojection happens, so none is applied",
        ["gdbVersion"] = "there is no version tree",
        ["historicMoment"] = "there is no history",
        // f=html renders the query page (ADR-023 §4b). Anything else — pbf,
        // geojson, kmz — is still ignored, and this is where that is said.
        ["f"] = "only json and html are produced; any other format returns json",
        ["token"] = "authentication is by header; see ADR-015 §4",
        ["resultType"] = "no result-type specialisation exists",
        ["sqlFormat"] = "no SQL is exposed",
    };

    /// <summary>Parses, or explains why not.</summary>
    /// <param name="parameters">The query string.</param>
    /// <param name="objectIdColumn">
    /// Always requested, whatever <c>outFields</c> says. An ArcGIS response
    /// whose <c>objectIdFieldName</c> names a field the features do not carry is
    /// one a client cannot page or select against.
    /// </param>
    /// <param name="layerSrid">The layer's SRID, for checking <c>outSR</c>.</param>
    /// <param name="allFields">
    /// Every column, for expanding <c>outFields=*</c>. Taken from the database.
    /// </param>
    /// <param name="query">The parsed query.</param>
    /// <param name="shape">What the caller asked the response to be.</param>
    /// <param name="error">Why it could not be parsed.</param>
    /// <param name="cost">
    /// What one request may cost this layer's service, or null for the server's own
    /// figures (Q-113). Narrows and never widens.
    /// </param>
    /// <param name="serverDefaultRecordCount">
    /// The page size to use when neither the caller nor the service says.
    /// </param>
    public static bool TryParse(
        IQueryCollection parameters,
        string objectIdColumn,
        int layerSrid,
        IReadOnlyList<FieldDescription> allFields,
        [NotNullWhen(true)] out FeatureQuery? query,
        out QueryShape shape,
        [NotNullWhen(false)] out string? error,
        ServiceCostCeilings? cost = null,
        int serverDefaultRecordCount = DefaultRecordCount)
    {
        query = null;
        shape = QueryShape.Features;
        error = null;

        foreach (string refused in RefusedParameters)
        {
            if (parameters.ContainsKey(refused))
            {
                error =
                    $"'{refused}' is not supported. It is refused rather than ignored: answering "
                    + "a different question than the one asked would be worse than saying so. "
                    + $"{WhyRefused(refused)}";
                return false;
            }
        }

        // Each step sets `error` on failure. Written as separate statements
        // rather than a chain of ||, because the nullable analysis cannot see
        // through the chain that `error` is non-null when any of them returns
        // false — and silencing it with a ! would be asserting exactly the thing
        // worth having checked.
        if (!TryUnknown(parameters, out error)) { return Fail(out error, error); }
        if (!TryLimit(parameters, cost ?? ServiceCostCeilings.Unset, serverDefaultRecordCount,
                out int limit, out error))
        {
            return Fail(out error, error);
        }
        if (!TryOffset(parameters, out int offset, out error)) { return Fail(out error, error); }
        if (!TrySpatial(
                parameters, layerSrid, out SpatialFilter? spatial, out Envelope? box,
                out int? filterSrid, out error))
        {
            return Fail(out error, error);
        }

        if (!TryOutSpatialReference(parameters, layerSrid, out int? outSrid, out error))
        {
            return Fail(out error, error);
        }

        if (!TryFields(parameters, objectIdColumn, allFields, out List<string> fields, out error))
        {
            return Fail(out error, error);
        }

        if (!TryOrderBy(parameters, allFields, out List<GisServer.Features.SortKey> orderBy, out error))
        {
            return Fail(out error, error);
        }

        if (!TryWhere(parameters, allFields, out ParsedWhere? where, out error))
        {
            return Fail(out error, error);
        }

        if (!TryObjectIds(parameters, out List<long> objectIds, out error))
        {
            return Fail(out error, error);
        }

        if (!TryPositiveInt(parameters, "geometryPrecision", out int? precision, out error))
        {
            return Fail(out error, error);
        }

        if (!TryPositiveDouble(parameters, "maxAllowableOffset", out double? tolerance, out error))
        {
            return Fail(out error, error);
        }

        if (!TryStatistics(parameters, allFields, out List<StatisticRequest> statistics,
                out List<string> groupBy, out string? having, out error))
        {
            return Fail(out error, error);
        }

        if (!TryShape(parameters, statistics.Count > 0, out shape, out error))
        {
            return Fail(out error, error);
        }

        bool distinct = Flag(parameters, "returnDistinctValues", defaultValue: false);

        if (distinct && Flag(parameters, "returnGeometry", defaultValue: true)
            && parameters.ContainsKey("returnGeometry"))
        {
            error =
                "'returnDistinctValues' needs 'returnGeometry=false'. Two features with identical "
                + "attributes still have different shapes, so distinct rows and returned geometry "
                + "cannot both be honoured — ArcGIS requires the same combination.";
            return false;
        }

        query = new FeatureQuery(
            limit,
            box,
            fields,
            offset,

            // Distinct implies no geometry, and asking for the ids or the count
            // means nobody is going to read a shape.
            includeGeometry: !distinct
                && shape is QueryShape.Features
                && Flag(parameters, "returnGeometry", defaultValue: true),
            orderBy,
            spatial,
            objectIds,
            distinct,
            statistics,
            groupBy,
            having,
            precision,
            tolerance,
            outSrid,
            where,
            filterSrid);

        return true;
    }

    /// <summary>Why a refused parameter is refused, said specifically.</summary>
    private static string WhyRefused(string name) => name switch
    {
        "time" => "No layer here declares timeInfo, so there is no time field to filter on.",
        "fullText" => "Full-text search needs a tsvector column and an index on your table.",
        _ => "Unique ids are an ArcGIS 12.1 concept with no counterpart in this server.",
    };

    /// <summary>What the caller asked the response to be.</summary>
    private static bool TryShape(
        IQueryCollection parameters, bool statistics, out QueryShape shape, out string? error)
    {
        error = null;
        shape = QueryShape.Features;

        List<string> asked = [];

        if (Flag(parameters, "returnCountOnly", false)) { asked.Add("returnCountOnly"); }
        if (Flag(parameters, "returnIdsOnly", false)) { asked.Add("returnIdsOnly"); }
        if (Flag(parameters, "returnExtentOnly", false)) { asked.Add("returnExtentOnly"); }
        if (statistics) { asked.Add("outStatistics"); }

        if (asked.Count > 1)
        {
            // <b>Refused rather than ranked.</b> ArcGIS documents a precedence
            // among these; guessing at it would mean answering one question and
            // silently dropping the other, which is the failure this whole class
            // is written to avoid.
            error =
                $"{string.Join(" and ", asked)} were all asked for, and each replaces the "
                + "response with something different. Ask for one.";
            return false;
        }

        shape = asked.Count == 0 ? QueryShape.Features : asked[0] switch
        {
            "returnCountOnly" => QueryShape.Count,
            "returnIdsOnly" => QueryShape.Ids,
            "returnExtentOnly" => QueryShape.Extent,
            _ => QueryShape.Statistics,
        };

        return true;
    }


    /// <summary>Every parameter this class understands, in any capacity.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "where", "objectIds",
        "geometry", "geometryType", "spatialRel", "relationParam", "distance", "units",
        "inSR", "outSR", "defaultSR",
        "outFields", "orderByFields", "returnGeometry", "returnCentroid",
        "maxAllowableOffset", "geometryPrecision",
        "resultRecordCount", "resultOffset",
        "returnCountOnly", "returnIdsOnly", "returnExtentOnly", "returnDistinctValues",
        "outStatistics", "groupByFieldsForStatistics", "havingClause",
    };

    /// <summary>
    /// Refuses a parameter this class has never heard of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The hole this closes.</b> Until now anything absent from the refused
    /// list passed silently, so <c>returnCentroid</c> — which changes what
    /// geometry comes back — was accepted and ignored without anybody deciding
    /// it should be. That is the silent degradation ADR-008 §2 forbids, arrived
    /// at by omission rather than by choice, and it was invisible precisely
    /// because nothing complained.
    /// </para>
    /// <para>
    /// <b>The cost is real and accepted.</b> A client sending a harmless
    /// parameter nobody has catalogued gets refused. That is the trade §2 asks
    /// for: an unknown parameter might change the answer, we cannot know which,
    /// and saying so is better than guessing it did not matter.
    /// </para>
    /// </remarks>
    private static bool TryUnknown(IQueryCollection parameters, out string? error)
    {
        error = null;

        foreach (string name in parameters.Keys)
        {
            if (Known.Contains(name)
                || IgnoredParameters.ContainsKey(name)
                || Array.IndexOf(RefusedParameters, name) >= 0)
            {
                continue;
            }

            error =
                $"'{name}' is not a parameter this server understands, so it is refused rather "
                + "than ignored: it might change the answer, and assuming it does not is how a "
                + "client comes to be given a different result than the one it asked for. If it "
                + "is harmless, it belongs on the accepted-and-ignored list with a reason.";
            return false;
        }

        return true;
    }

    /// <summary>Only an envelope is understood as a spatial filter.</summary>
    private static bool TryGeometryType(IQueryCollection parameters, out string? error)
    {
        error = null;

        if (!parameters.TryGetValue("geometryType", out Microsoft.Extensions.Primitives.StringValues values)
            || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return true;
        }

        if (values[0]!.Equals("esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error =
            $"'geometryType' of '{values[0]}' is not supported as a spatial filter. Only "
            + "esriGeometryEnvelope is, because a bounding box is what the spatial index can "
            + "answer. Filtering by an arbitrary shape needs the geometry engine on the request "
            + "path (ADR-003).";
        return false;
    }

    /// <summary>
    /// Parses <c>orderByFields</c>, which every ArcGIS client sends.
    /// </summary>
    /// <remarks>
    /// <b>Field names are checked against the layer, never passed through.</b>
    /// This is the one place a client-supplied identifier reaches an ORDER BY,
    /// and an identifier cannot be bound as a parameter — so the whitelist is
    /// the safety, exactly as it is for the select list (ADR-008 §4.6).
    /// </remarks>
    private static bool TryOrderBy(
        IQueryCollection parameters,
        IReadOnlyList<FieldDescription> allFields,
        out List<GisServer.Features.SortKey> orderBy,
        out string? error)
    {
        orderBy = [];
        error = null;

        if (!parameters.TryGetValue("orderByFields", out Microsoft.Extensions.Primitives.StringValues values)
            || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return true;
        }

        HashSet<string> known = [.. allFields.Select(f => f.Name)];

        foreach (string clause in values[0]!.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            bool descending = parts.Length > 1
                && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

            if (parts.Length > 2
                || (parts.Length == 2 && !descending
                    && !parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase)))
            {
                error = $"'{clause}' is not a field name with an optional ASC or DESC.";
                return false;
            }

            if (!known.Contains(parts[0]))
            {
                error =
                    $"Cannot order by '{parts[0]}': it is not a field of this layer. Fields come "
                    + "from the database rather than from the request.";
                return false;
            }

            orderBy.Add(new GisServer.Features.SortKey(parts[0], descending));
        }

        return true;
    }

    /// <summary>
    /// Whether two spatial-reference identifiers name the same system.
    /// </summary>
    /// <remarks>
    /// <b>102100 is not a mistake for 3857 — it is Esri's own code for the same
    /// projection</b>, Web Mercator Auxiliary Sphere, and every ArcGIS client
    /// sends it. Comparing the numbers alone refuses the SDK on every request
    /// against a 3857 layer, which is exactly what happened. 102113 is the older
    /// spelling of the same thing.
    /// </remarks>
    private static bool SameSpatialReference(int a, int b) => Canonical(a) == Canonical(b);

    private static int Canonical(int wkid) => wkid switch
    {
        102100 or 102113 => 3857,
        _ => wkid,
    };

    /// <summary>Restates a failure so the compiler can see the message is set.</summary>
    private static bool Fail(out string error, string? message)
    {
        error = message ?? "The query could not be parsed.";
        return false;
    }

    /// <summary>The page size used when a caller does not ask for one.</summary>
    /// <remarks>
    /// <b>A named constant since Q-113, and it was a literal 1000 inside the parser
    /// before.</b> A number a service can override has to be readable from the place
    /// that overrides it, and a number nobody can find is a number nobody tunes.
    /// </remarks>
    public const int DefaultRecordCount = 1000;

    private static bool TryLimit(
        IQueryCollection parameters,
        ServiceCostCeilings cost,
        int serverDefault,
        out int limit,
        out string? error)
    {
        // The service's own default when it has one, clamped by whichever ceiling is
        // in force — so a page size set in one edit cannot exceed a maximum set in
        // another.
        limit = cost.PageSize(serverDefault, FeatureQuery.MaximumLimit);
        error = null;

        if (!parameters.TryGetValue("resultRecordCount", out Microsoft.Extensions.Primitives.StringValues count)
            || count.Count == 0 || string.IsNullOrWhiteSpace(count[0]))
        {
            return true;
        }

        if (!int.TryParse(count[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)
            || limit < 1)
        {
            error = "resultRecordCount must be a positive integer.";
            return false;
        }

        // Clamped, not refused: a client asking for everything is asking a
        // reasonable question badly, and exceededTransferLimit is how the
        // response tells them there is more.
        //
        // <b>Clamped by the service's ceiling as well as the server's, and by the
        // smaller of the two</b> — Q-113. A service may ask for less and never for
        // more, or a per-service setting would make the server-wide figure advisory
        // and an operator who lowered it globally would not have lowered it.
        limit = Math.Min(limit, cost.RecordCount(FeatureQuery.MaximumLimit));
        return true;
    }

    private static bool TryOffset(IQueryCollection parameters, out int offset, out string? error)
    {
        offset = 0;
        error = null;

        if (!parameters.TryGetValue("resultOffset", out Microsoft.Extensions.Primitives.StringValues values)
            || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return true;
        }

        if (!int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
            || offset < 0)
        {
            error = "resultOffset must be a non-negative integer.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The whole spatial filter: geometry, type, relation, buffer.
    /// </summary>
    /// <param name="parameters">The query string.</param>
    /// <param name="layerSrid">The layer's reference, which the filter must be in.</param>
    /// <param name="spatial">A rich filter, when the request needs one.</param>
    /// <param name="boundingBox">The fast path, when an envelope-intersects will do.</param>
    /// <param name="filterSrid">
    /// The reference the filter is in when it differs from the layer's, so the
    /// provider can transform it; null when they are the same.
    /// </param>
    /// <param name="error">Why it was refused.</param>
    /// <remarks>
    /// <para>
    /// <b>Two outputs, because one of the two cases is worth keeping fast.</b>
    /// An envelope with the default relation and no buffer is the overwhelming
    /// majority of real traffic and compiles to the index operator alone. Every
    /// other combination becomes a <see cref="SpatialFilter"/>. At most one is
    /// ever set.
    /// </para>
    /// <para>
    /// <b><c>inSR</c> must match the layer, still.</b> Reprojecting the filter
    /// would mean transforming the caller's geometry before comparing it, and
    /// the failure when that is skipped is silent — boxes in different units
    /// simply never meet and the answer is zero features with no error, which is
    /// exactly the defect that made every 4326 tile empty (Q-96). Output
    /// reprojection is a different question and is now supported; see
    /// <see cref="TryOutSpatialReference"/>.
    /// </para>
    /// </remarks>
    private static bool TrySpatial(
        IQueryCollection parameters,
        int layerSrid,
        out SpatialFilter? spatial,
        out Envelope? boundingBox,
        out int? filterSrid,
        out string? error)
    {
        spatial = null;
        boundingBox = null;
        filterSrid = null;
        error = null;

        string raw = First(parameters, "geometry");

        if (!TryWkid(parameters, "inSR", out int? inSrid, out error))
        {
            return false;
        }

        inSrid ??= Wkid(First(parameters, "defaultSR"));

        // <b>A differing inSR is accepted and transformed, and until 2026-08-16 it
        // was refused.</b> The refusal's reasoning was sound and its conclusion was
        // not: comparing two references silently yields zero features (Q-96), so
        // something must transform — but declining the request makes the caller do
        // it, and the caller is often an ArcGIS client that cannot. An ArcGIS JS
        // MapView in Web Mercator sends inSR=102100 on every query it issues; a
        // layer stored in EPSG:4326 therefore answered 400 to every such client,
        // whoever wrote it. Registered data makes that unanswerable rather than
        // merely awkward: the table belongs to somebody else and reprojecting it is
        // not ours to require. So the filter is carried with its own reference and
        // ST_Transform brings it to the layer's in the database (ADR-022 §4).
        filterSrid = inSrid is { } given && !SameSpatialReference(given, layerSrid)
            ? given
            : null;

        if (raw.Length == 0)
        {
            // <b>Refused only on unambiguous intent, and the distinction is one
            // the query page forced.</b> An HTML form submits every select it
            // has, so `spatialRel=esriSpatialRelIntersects` arrives on every
            // submission whether or not anybody touched the control — and the
            // first version of this check refused the form's own default query,
            // which is a server refusing a request its own page generated.
            //
            // A distance, a relate pattern, or a relationship that is not the
            // default cannot come from an untouched form. Those still refuse,
            // because ignoring them would answer an unfiltered query for a
            // caller who believes they sent a filter.
            string asked = First(parameters, "spatialRel");

            bool deliberate =
                First(parameters, "distance").Length > 0
                || First(parameters, "relationParam").Length > 0
                || (asked.Length > 0
                    && !asked.Equals("esriSpatialRelIntersects", StringComparison.OrdinalIgnoreCase));

            if (deliberate)
            {
                error =
                    "'spatialRel', 'distance' and 'relationParam' describe how to compare against "
                    + "a filter geometry, and no 'geometry' was given. Send one, or drop them.";
                return false;
            }

            return true;
        }

        if (!TryRelation(parameters, out SpatialRelation relation, out string? pattern, out error))
        {
            return false;
        }

        if (!TryDistance(parameters, layerSrid, out double distance, out error))
        {
            return false;
        }

        string kind = First(parameters, "geometryType");

        // The fast path, and the shape of it is the reason it is worth
        // detecting: a bare envelope-intersects is the index operator alone.
        bool plainEnvelope =
            distance == 0
            && relation is SpatialRelation.Intersects
            && (kind.Length == 0
                || kind.Equals("esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase));

        if (plainEnvelope
            && TryParseEnvelope(raw, layerSrid, out boundingBox, out int? declaredSrid))
        {
            // The geometry's own reference wins; inSR is the fallback for geometry
            // that does not declare one.
            filterSrid = declaredSrid ?? filterSrid;
            return true;
        }

        if (!TryFilterGeometry(raw, kind, layerSrid, out Geometry? geometry, out error))
        {
            return false;
        }

        spatial = new SpatialFilter(geometry!, relation, pattern, distance);
        return true;
    }

    /// <summary>
    /// The filter geometry, from either the simple syntax or ArcGIS JSON.
    /// </summary>
    /// <remarks>
    /// <b>The comma syntax is only defined for envelopes and points</b>, which is
    /// what the ArcGIS documentation says; everything else must be JSON. A
    /// four-number string with <c>geometryType=esriGeometryPolygon</c> is a
    /// client mistake worth naming rather than guessing at.
    /// </remarks>
    private static bool TryFilterGeometry(
        string raw, string kind, int layerSrid, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        bool json = raw.StartsWith('{');

        if (!json)
        {
            string[] parts = raw.Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length == 4
                && (kind.Length == 0
                    || kind.Equals("esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase)))
            {
                if (!TryParseEnvelope(raw, layerSrid, out Envelope? box, out _) || box is null)
                {
                    error = "The envelope must be four numbers: xmin,ymin,xmax,ymax.";
                    return false;
                }

                geometry = Rectangle(box.Value);
                return true;
            }

            if (parts.Length == 2
                && kind.Equals("esriGeometryPoint", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                {
                    error = "A point in the short syntax must be two numbers: x,y.";
                    return false;
                }

                geometry = new Point(x, y);
                return true;
            }

            error =
                $"'geometry' is '{raw}', which is not a shape this server can read. The "
                + "comma-separated form is defined only for an envelope (xmin,ymin,xmax,ymax) and "
                + "a point (x,y); anything else must be ArcGIS geometry JSON.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);

            if (!ArcGisGeometryReader.TryRead(document.RootElement, layerSrid, out geometry, out error))
            {
                return false;
            }

            return true;
        }
        catch (JsonException e)
        {
            error = $"'geometry' is not valid JSON: {e.Message}";
            return false;
        }
    }

    /// <summary>One of ArcGIS's nine relations.</summary>
    private static bool TryRelation(
        IQueryCollection parameters,
        out SpatialRelation relation,
        out string? pattern,
        out string? error)
    {
        relation = SpatialRelation.Intersects;
        pattern = null;
        error = null;

        string raw = First(parameters, "spatialRel");

        if (raw.Length > 0)
        {
            relation = raw.ToLowerInvariant() switch
            {
                "esrispatialrelintersects" => SpatialRelation.Intersects,
                "esrispatialrelcontains" => SpatialRelation.Contains,
                "esrispatialrelcrosses" => SpatialRelation.Crosses,
                "esrispatialrelenvelopeintersects" => SpatialRelation.EnvelopeIntersects,
                "esrispatialrelindexintersects" => SpatialRelation.IndexIntersects,
                "esrispatialreloverlaps" => SpatialRelation.Overlaps,
                "esrispatialreltouches" => SpatialRelation.Touches,
                "esrispatialrelwithin" => SpatialRelation.Within,
                "esrispatialrelrelation" => SpatialRelation.Relate,
                _ => (SpatialRelation)(-1),
            };

            if ((int)relation < 0)
            {
                error =
                    $"'spatialRel' of '{raw}' is not one of the nine ArcGIS relationships.";
                return false;
            }
        }

        pattern = First(parameters, "relationParam");

        if (relation == SpatialRelation.Relate)
        {
            if (pattern.Length == 0)
            {
                error =
                    "esriSpatialRelRelation needs 'relationParam', a nine-character DE-9IM "
                    + "pattern such as FFFTTT***. Without one there is no relation to test.";
                return false;
            }

            // <b>Checked here because it reaches ST_Relate as a value, and a
            // value is bound — so this is a usefulness check, not a safety
            // one.</b> A malformed pattern would otherwise surface as a database
            // error with our SQL in it.
            if (pattern.Length != 9 || pattern.Any(c => !"012TF*".Contains(c, StringComparison.Ordinal)))
            {
                error =
                    $"'relationParam' must be nine characters from 0 1 2 T F *, and '{pattern}' "
                    + "is not.";
                return false;
            }
        }
        else if (pattern.Length > 0)
        {
            error =
                "'relationParam' only means something with spatialRel=esriSpatialRelRelation. "
                + "Accepting it here would let a caller believe a pattern was applied.";
            return false;
        }

        if (pattern.Length == 0)
        {
            pattern = null;
        }

        return true;
    }

    /// <summary>
    /// The buffer distance, converted into the layer's own units.
    /// </summary>
    /// <remarks>
    /// <b>Refused on a geographic layer, and this is the interesting case.</b>
    /// A distance in metres against degrees is not a unit conversion — the
    /// number of metres in a degree of longitude depends on where you are — so
    /// there is no factor to apply. Doing it properly means a geography cast or
    /// a projected intermediate, and guessing would put the buffer in the wrong
    /// place by hundreds of kilometres at high latitudes.
    /// </remarks>
    private static bool TryDistance(
        IQueryCollection parameters, int layerSrid, out double distance, out string? error)
    {
        distance = 0;
        error = null;

        string raw = First(parameters, "distance");

        if (raw.Length == 0)
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || value < 0)
        {
            error = "'distance' must be a non-negative number.";
            return false;
        }

        if (value == 0)
        {
            return true;
        }

        if (layerSrid == 4326)
        {
            error =
                "'distance' is not supported on a layer stored in degrees (EPSG:4326). A distance "
                + "in metres has no fixed size in degrees — it depends on latitude — so there is "
                + "no conversion to apply and a guess would be wrong by hundreds of kilometres "
                + "near the poles. Publish the layer in a projected reference, or use a filter "
                + "geometry you have already buffered.";
            return false;
        }

        string units = First(parameters, "units");

        double metres = units.ToLowerInvariant() switch
        {
            "" or "esrisrunit_meter" => 1,
            "esrisrunit_foot" => 0.3048,
            "esrisrunit_kilometer" => 1000,
            "esrisrunit_statutemile" => 1609.344,
            "esrisrunit_nauticalmile" => 1852,
            "esrisrunit_usnauticalmile" => 1852,
            _ => double.NaN,
        };

        if (double.IsNaN(metres))
        {
            error =
                $"'units' of '{units}' is not one this server knows. Use esriSRUnit_Meter, "
                + "esriSRUnit_Foot, esriSRUnit_Kilometer, esriSRUnit_StatuteMile, "
                + "esriSRUnit_NauticalMile or esriSRUnit_USNauticalMile.";
            return false;
        }

        // The layer's units are metres for every projected reference this server
        // serves; Web Mercator's are metres that stretch with latitude, which is
        // its own well-known distortion and not something to correct here.
        distance = value * metres;
        return true;
    }

    /// <summary>The output reference, which the database will transform into.</summary>
    private static bool TryOutSpatialReference(
        IQueryCollection parameters, int layerSrid, out int? outSrid, out string? error)
    {
        if (!TryWkid(parameters, "outSR", out outSrid, out error))
        {
            return false;
        }

        outSrid ??= Wkid(First(parameters, "defaultSR"));

        if (outSrid is { } wanted && SameSpatialReference(wanted, layerSrid))
        {
            // Asking for what it already is costs a transform that changes
            // nothing, and reports a different wkid for identical coordinates.
            outSrid = null;
        }

        return true;
    }

    /// <summary>A wkid parameter, or null when absent.</summary>
    private static bool TryWkid(
        IQueryCollection parameters, string name, out int? wkid, out string? error)
    {
        wkid = null;
        error = null;

        string raw = First(parameters, name);

        if (raw.Length == 0)
        {
            return true;
        }

        // A client may send a bare wkid or a spatial reference object. Both are
        // read; the object form is what the ArcGIS SDK sends.
        if (raw.StartsWith('{'))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);

                foreach (string property in (string[])["latestWkid", "wkid"])
                {
                    if (document.RootElement.TryGetProperty(property, out JsonElement value)
                        && value.TryGetInt32(out int found))
                    {
                        wkid = found;
                        return true;
                    }
                }
            }
            catch (JsonException)
            {
                // Falls through to the message below.
            }

            error = $"'{name}' is an object with no wkid in it.";
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            error = $"'{name}' must be a numeric wkid or a spatial reference object; '{raw}' is not.";
            return false;
        }

        wkid = parsed;
        return true;
    }

    private static int? Wkid(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    /// <summary>The attribute predicate, parsed rather than forwarded.</summary>
    /// <remarks>
    /// <b><c>1=1</c> is stripped rather than parsed.</b> The grammar has no rule
    /// for comparing two literals — deliberately, because it is the shape every
    /// injection attempt starts with — and <c>1=1</c> is what every ArcGIS
    /// client sends to mean "no filter". Recognising it here keeps the grammar
    /// closed and the clients working.
    /// </remarks>
    private static bool TryWhere(
        IQueryCollection parameters,
        IReadOnlyList<FieldDescription> allFields,
        out ParsedWhere? where,
        out string? error)
    {
        where = null;
        error = null;

        string raw = First(parameters, "where").Trim();

        if (raw.Length == 0
            || string.Equals(
                raw.Replace(" ", string.Empty, StringComparison.Ordinal),
                "1=1",
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!WhereClause.TryParse(
                raw,
                [.. allFields.Select(f => f.Name)],
                LayerDefinition.Quote,
                out ParsedWhere parsed,
                out error))
        {
            error = $"'where' could not be parsed. {error}";
            return false;
        }

        where = parsed;
        return true;
    }

    /// <summary>A comma-separated list of object ids.</summary>
    private static bool TryObjectIds(
        IQueryCollection parameters, out List<long> ids, out string? error)
    {
        ids = [];
        error = null;

        string raw = First(parameters, "objectIds");

        if (raw.Length == 0)
        {
            return true;
        }

        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                error =
                    $"'objectIds' must be a comma-separated list of integers, and '{part}' is not "
                    + "one. An object id is an integer by definition (ADR-013 §2a).";
                return false;
            }

            ids.Add(id);
        }

        // Bound, because the list becomes an array parameter and a caller could
        // otherwise post a million of them to build one query.
        if (ids.Count > MaximumObjectIds)
        {
            error =
                $"'objectIds' carries {ids.Count} ids and the limit is {MaximumObjectIds}. Use a "
                + "'where' clause, which the database can index.";
            return false;
        }

        return true;
    }

    /// <summary>How many ids one request may name.</summary>
    /// <remarks>
    /// ArcGIS's own documentation warns that performance drops past a thousand.
    /// Ten times that is generous and still bounded.
    /// </remarks>
    public const int MaximumObjectIds = 10_000;

    /// <summary>Statistics, their grouping, and the filter over them.</summary>
    private static bool TryStatistics(
        IQueryCollection parameters,
        IReadOnlyList<FieldDescription> allFields,
        out List<StatisticRequest> statistics,
        out List<string> groupBy,
        out string? having,
        out string? error)
    {
        statistics = [];
        groupBy = [];
        having = null;
        error = null;

        string raw = First(parameters, "outStatistics");
        string groups = First(parameters, "groupByFieldsForStatistics");
        // <b>Refused rather than parsed, 2026-08-16 — D-41.</b> This used to take
        // the caller's text and PostGisFeatureSource appended it to the statement
        // unparsed, so `havingClause` was SQL injection on any layer shared to
        // public. Refusing is the same move §4a of ADR-008 records for `where`
        // before the parser existed: a refusal is a capability nobody has, and a
        // pass-through is a capability everybody has, including whoever did not
        // pay for it. The parser is Q-109.
        having = null;

        if (First(parameters, "havingClause").Length > 0)
        {
            error =
                "'havingClause' is not supported. It is SQL over aggregates, and this server "
                + "will not accept SQL it has not parsed — every predicate it runs is rebuilt "
                + "from a checked expression with bound literals. Filter the returned "
                + "statistics client-side, or use 'where' to narrow the rows before they are "
                + "aggregated.";
            return false;
        }

        HashSet<string> known = new(allFields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        foreach (string field in groups.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!known.TryGetValue(field, out string? actual))
            {
                error = $"'groupByFieldsForStatistics' names '{field}', which is not a field.";
                return false;
            }

            groupBy.Add(actual);
        }

        if (raw.Length == 0)
        {
            if (groupBy.Count > 0)
            {
                error =
                    "'groupByFieldsForStatistics' only means something with 'outStatistics'. "
                    + "Without it there is nothing to group.";
                return false;
            }

            return true;
        }

        JsonElement definitions;

        try
        {
            definitions = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException e)
        {
            error = $"'outStatistics' is not valid JSON: {e.Message}";
            return false;
        }

        if (definitions.ValueKind != JsonValueKind.Array)
        {
            error = "'outStatistics' must be an array of statistic definitions.";
            return false;
        }

        foreach (JsonElement definition in definitions.EnumerateArray())
        {
            if (!TryStatistic(definition, known, out StatisticRequest statistic, out error))
            {
                return false;
            }

            statistics.Add(statistic);
        }

        if (statistics.Count == 0)
        {
            error = "'outStatistics' is empty, so there is nothing to compute.";
            return false;
        }

        return true;
    }

    private static bool TryStatistic(
        JsonElement definition,
        HashSet<string> known,
        out StatisticRequest statistic,
        out string? error)
    {
        statistic = default;
        error = null;

        string type = Text(definition, "statisticType");
        string field = Text(definition, "onStatisticField");
        string outName = Text(definition, "outStatisticFieldName");

        StatisticKind kind = type.ToLowerInvariant() switch
        {
            "count" => StatisticKind.Count,
            "sum" => StatisticKind.Sum,
            "min" => StatisticKind.Min,
            "max" => StatisticKind.Max,
            "avg" => StatisticKind.Avg,
            "stddev" => StatisticKind.StdDev,
            "var" => StatisticKind.Var,
            _ => (StatisticKind)(-1),
        };

        if ((int)kind < 0)
        {
            error =
                $"'statisticType' of '{type}' is not supported. Use count, sum, min, max, avg, "
                + "stddev or var. Percentile needs an ordered-set aggregate and is not "
                + "implemented.";
            return false;
        }

        if (!known.TryGetValue(field, out string? actual))
        {
            error = $"'onStatisticField' names '{field}', which is not a field of this layer.";
            return false;
        }

        if (outName.Length == 0)
        {
            outName = $"{type.ToLowerInvariant()}_{actual}";
        }

        // <b>The output name reaches SQL as an identifier and cannot be bound.</b>
        // Restricted to what an identifier may contain rather than escaped,
        // because a name that needs escaping to be safe is a name worth
        // refusing — ADR-008 §4.6.
        if (outName.Length > 63 || !outName.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            error =
                $"'outStatisticFieldName' of '{outName}' is not a plain identifier. Use letters, "
                + "digits and underscores, up to 63 characters.";
            return false;
        }

        statistic = new StatisticRequest(kind, actual, outName);
        return true;
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryPositiveInt(
        IQueryCollection parameters, string name, out int? value, out string? error)
    {
        value = null;
        error = null;

        string raw = First(parameters, name);

        if (raw.Length == 0)
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            || parsed < 0)
        {
            error = $"'{name}' must be a non-negative integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryPositiveDouble(
        IQueryCollection parameters, string name, out double? value, out string? error)
    {
        value = null;
        error = null;

        string raw = First(parameters, name);

        if (raw.Length == 0)
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || parsed < 0)
        {
            error = $"'{name}' must be a non-negative number.";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// A box as a closed, clockwise polygon.
    /// </summary>
    /// <remarks>
    /// <b>ST_Relate and its siblings compare geometries, and an envelope is not
    /// one.</b> The ring is closed explicitly — the first point repeated as the
    /// last — because a ring that is not closed is not a ring, and the omission
    /// is the kind that produces a PostGIS error a long way from here.
    /// </remarks>
    private static Polygon Rectangle(Envelope box) =>
        new(new LinearRing(XySequence.Wrap(
        [
            box.MinX, box.MinY,
            box.MaxX, box.MinY,
            box.MaxX, box.MaxY,
            box.MinX, box.MaxY,
            box.MinX, box.MinY,
        ])));

    /// <summary>The first value of a parameter, or the empty string.</summary>
    private static string First(IQueryCollection parameters, string name) =>
        parameters.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues values)
        && values.Count > 0
            ? (values[0] ?? string.Empty).Trim()
            : string.Empty;

    /// <summary>
    /// Resolves <c>outFields</c>, including the star.
    /// </summary>
    /// <remarks>
    /// <b><c>*</c> was refused until the catalogue could describe columns.</b>
    /// The reason given then — that we could not honestly name types for fields
    /// nobody asked for — stopped being true when <c>DescribeAsync</c> arrived,
    /// and a refusal whose stated reason has expired is just an obstacle.
    /// </remarks>
    private static bool TryFields(
        IQueryCollection parameters,
        string objectIdColumn,
        IReadOnlyList<FieldDescription> allFields,
        out List<string> fields,
        out string? error)
    {
        error = null;
        fields = [objectIdColumn];

        parameters.TryGetValue("outFields", out Microsoft.Extensions.Primitives.StringValues outFields);

        string requested = outFields.Count > 0 ? (outFields[0] ?? string.Empty).Trim() : string.Empty;

        if (requested.Length == 0)
        {
            return true;
        }

        IEnumerable<string> names = requested == "*"
            ? allFields.Select(f => f.Name)
            : requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        HashSet<string> known = [.. allFields.Select(f => f.Name)];

        foreach (string name in names)
        {
            if (string.Equals(name, objectIdColumn, StringComparison.Ordinal))
            {
                continue;
            }

            // Named explicitly and not there. Silently dropping it would give a
            // client a response missing a column it asked for, with nothing to
            // say why.
            if (!known.Contains(name))
            {
                error =
                    $"'{name}' is not a field of this layer. Fields come from the database rather "
                    + "than the request, so a name that is not there cannot be returned.";
                return false;
            }

            fields.Add(name);
        }

        return true;
    }

    /// <summary>Reads a boolean parameter, tolerating the spellings clients use.</summary>
    private static bool Flag(IQueryCollection parameters, string name, bool defaultValue)
    {
        if (!parameters.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues values)
            || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return defaultValue;
        }

        string value = values[0]!.Trim();

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
            || (!value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0" && defaultValue);
    }

    /// <summary>
    /// Parses an envelope in either form clients send.
    /// </summary>
    /// <remarks>
    /// The comma-separated form is what a URL-built request uses; the JSON object
    /// is what the ArcGIS SDKs send. Supporting one is a compatibility surface
    /// that works for half of them.
    /// </remarks>
    private static bool TryParseEnvelope(
        string value, int layerSrid, out Envelope? envelope, out int? declaredSrid)
    {
        envelope = null;
        declaredSrid = null;
        string text = value.Trim();

        if (text.StartsWith('{'))
        {
            try
            {
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(text);
                System.Text.Json.JsonElement root = document.RootElement;

                if (!root.TryGetProperty("xmin", out System.Text.Json.JsonElement minX)
                    || !root.TryGetProperty("ymin", out System.Text.Json.JsonElement minY)
                    || !root.TryGetProperty("xmax", out System.Text.Json.JsonElement maxX)
                    || !root.TryGetProperty("ymax", out System.Text.Json.JsonElement maxY))
                {
                    return false;
                }

                // <b>The SDK puts the spatial reference inside the geometry rather
                // than only in inSR, and this is the hot path for it.</b> An
                // ArcGIS JS FeatureLayer drawing itself sends exactly this: an
                // envelope carrying `spatialReference: {wkid: 102100}`, with
                // `inSR` beside it. Refusing it — which this did until 2026-08-16
                // — meant no ArcGIS JS map in Web Mercator could draw a layer
                // stored in EPSG:4326, whoever wrote the client.
                //
                // It is reported rather than rejected, and the geometry's own
                // reference wins over `inSR` when both are present, which is the
                // ArcGIS rule: inSR exists for geometry that does not say.
                if (root.TryGetProperty("spatialReference", out System.Text.Json.JsonElement reference)
                    && reference.TryGetProperty("wkid", out System.Text.Json.JsonElement wkid)
                    && wkid.TryGetInt32(out int declared)
                    && !SameSpatialReference(declared, layerSrid))
                {
                    declaredSrid = declared;
                }

                envelope = new Envelope(
                    minX.GetDouble(), minY.GetDouble(), maxX.GetDouble(), maxY.GetDouble());
                return true;
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        string[] parts = text.Split(',');

        if (parts.Length != 4)
        {
            return false;
        }

        Span<double> ordinates = stackalloc double[4];

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out ordinates[i]))
            {
                return false;
            }
        }

        envelope = new Envelope(ordinates[0], ordinates[1], ordinates[2], ordinates[3]);
        return true;
    }

    /// <summary>Whether a parameter is knowingly ignored, and why.</summary>
    /// <remarks>Exposed so the endpoint can log it rather than leave it invisible.</remarks>
    public static bool IsIgnored(string name, out string reason) =>
        IgnoredParameters.TryGetValue(name, out reason!);
}
