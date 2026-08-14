using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using GisServer.Features;
using GisServer.Geometries;
using Microsoft.AspNetCore.Http;

namespace GisServer.Host;

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
        "groupByFieldsForStatistics", "outStatistics",
        "returnIdsOnly", "returnDistinctValues", "returnExtentOnly",
        "objectIds", "time", "distance", "relationParam", "having",
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
        ["f"] = "JSON is the only format",
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
    /// <param name="countOnly">Whether the caller asked for a count rather than features.</param>
    /// <param name="error">Why it could not be parsed.</param>
    public static bool TryParse(
        IQueryCollection parameters,
        string objectIdColumn,
        int layerSrid,
        IReadOnlyList<FieldDescription> allFields,
        [NotNullWhen(true)] out FeatureQuery? query,
        out bool countOnly,
        [NotNullWhen(false)] out string? error)
    {
        query = null;
        countOnly = false;
        error = null;

        foreach (string refused in RefusedParameters)
        {
            if (parameters.ContainsKey(refused))
            {
                error =
                    $"'{refused}' is not supported yet. It is refused rather than ignored: "
                    + "answering a different question than the one asked would be worse than "
                    + "saying so. Supported: where (only 1=1), geometry (envelope), outFields, "
                    + "resultRecordCount, resultOffset, returnGeometry, returnCountOnly, outSR.";
                return false;
            }
        }

        // Each step sets `error` on failure. Written as separate statements
        // rather than a chain of ||, because the nullable analysis cannot see
        // through the chain that `error` is non-null when any of them returns
        // false — and silencing it with a ! would be asserting exactly the thing
        // worth having checked.
        if (!TryUnknown(parameters, out error)) { return Fail(out error, error); }
        if (!TryGeometryType(parameters, out error)) { return Fail(out error, error); }
        if (!TryWhere(parameters, out error)) { return Fail(out error, error); }
        if (!TrySpatialRelationship(parameters, out error)) { return Fail(out error, error); }
        if (!TrySpatialReference(parameters, layerSrid, out error)) { return Fail(out error, error); }
        if (!TryLimit(parameters, out int limit, out error)) { return Fail(out error, error); }
        if (!TryOffset(parameters, out int offset, out error)) { return Fail(out error, error); }
        if (!TryEnvelope(parameters, layerSrid, out Envelope? boundingBox, out error)) { return Fail(out error, error); }

        if (!TryFields(parameters, objectIdColumn, allFields, out List<string> fields, out error))
        {
            return Fail(out error, error);
        }

        if (!TryOrderBy(parameters, allFields, out List<GisServer.Features.SortKey> orderBy, out error))
        {
            return Fail(out error, error);
        }

        countOnly = Flag(parameters, "returnCountOnly", defaultValue: false);

        query = new FeatureQuery(
            limit,
            boundingBox,
            fields,
            offset,
            includeGeometry: Flag(parameters, "returnGeometry", defaultValue: true),
            orderBy);

        return true;
    }


    /// <summary>Every parameter this class understands, in any capacity.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "where", "geometry", "geometryType", "spatialRel", "outFields", "orderByFields",
        "resultRecordCount", "resultOffset", "returnGeometry", "returnCountOnly",
        "returnCentroid", "outSR", "inSR",
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

    /// <summary>
    /// Accepts the always-true predicate and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is not SQL support and must not grow into it.</b> Every ArcGIS
    /// client sends <c>where=1=1</c> to mean <em>no filter</em>, so refusing it
    /// refuses every client for no safety gained. Anything else is a predicate
    /// we would have to parse, and parsing SQL fragments from a request is how
    /// injection happens — the real answer is ADR-008's query AST.
    /// </remarks>
    private static bool TryWhere(IQueryCollection parameters, out string? error)
    {
        error = null;

        if (!parameters.TryGetValue("where", out Microsoft.Extensions.Primitives.StringValues values)
            || values.Count == 0)
        {
            return true;
        }

        string where = (values[0] ?? string.Empty).Trim();

        if (where.Length == 0
            || string.Equals(where, "1=1", StringComparison.Ordinal)
            || string.Equals(where.Replace(" ", string.Empty, StringComparison.Ordinal), "1=1",
                StringComparison.Ordinal))
        {
            return true;
        }

        error =
            $"'where' accepts only the always-true predicate (1=1), and this one is \"{where}\". "
            + "Attribute filtering needs the query AST in ADR-008, which does not exist yet. It is "
            + "refused rather than ignored, because returning every feature to a client that asked "
            + "for some of them is a wrong answer rather than a missing feature.";
        return false;
    }

    private static bool TrySpatialRelationship(IQueryCollection parameters, out string? error)
    {
        error = null;

        if (!parameters.TryGetValue("spatialRel", out Microsoft.Extensions.Primitives.StringValues values)
            || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return true;
        }

        if (string.Equals(values[0], "esriSpatialRelIntersects", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error =
            $"'spatialRel' of '{values[0]}' is not supported. The only relationship implemented is "
            + "esriSpatialRelIntersects, which is what the bounding-box pushdown computes. The "
            + "others need the geometry engine on the request path (ADR-003).";
        return false;
    }

    /// <summary>
    /// Refuses a spatial reference that would require reprojection.
    /// </summary>
    /// <remarks>
    /// Matching the layer is accepted; anything else is refused rather than
    /// silently returned in the wrong system, which would put a client's
    /// features in the sea.
    /// </remarks>
    private static bool TrySpatialReference(
        IQueryCollection parameters, int layerSrid, out string? error)
    {
        error = null;

        foreach (string name in (string[])["outSR", "inSR"])
        {
            if (!parameters.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues values)
                || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
            {
                continue;
            }

            string raw = values[0]!.Trim();

            // A client may send either a bare wkid or a spatial reference object.
            // Only the bare form is understood; the object form is refused rather
            // than half-parsed.
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wkid))
            {
                error =
                    $"'{name}' must be a numeric wkid; '{raw}' is not one. A spatial reference "
                    + "object is not accepted here.";
                return false;
            }

            if (!SameSpatialReference(wkid, layerSrid))
            {
                error =
                    $"'{name}' asks for {wkid} and this layer is {layerSrid}. This server does not "
                    + "reproject: returning geometry in a system it was not asked for, or claiming "
                    + "a system it is not in, are both worse than refusing. Request the layer's own "
                    + "spatial reference.";
                return false;
            }
        }

        return true;
    }

    private static bool TryLimit(IQueryCollection parameters, out int limit, out string? error)
    {
        limit = 1000;
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
        limit = Math.Min(limit, FeatureQuery.MaximumLimit);
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

    private static bool TryEnvelope(
        IQueryCollection parameters, int layerSrid, out Envelope? boundingBox, out string? error)
    {
        boundingBox = null;
        error = null;

        if (!parameters.TryGetValue("geometry", out Microsoft.Extensions.Primitives.StringValues geometry)
            || geometry.Count == 0 || string.IsNullOrWhiteSpace(geometry[0]))
        {
            return true;
        }

        if (TryParseEnvelope(geometry[0]!, layerSrid, out boundingBox))
        {
            return true;
        }

        error =
            "geometry must be an envelope, either 'xmin,ymin,xmax,ymax' or "
            + "{\"xmin\":…,\"ymin\":…,\"xmax\":…,\"ymax\":…}. Other geometry types as a spatial "
            + "filter need the query AST (ADR-008) and are not implemented.";
        return false;
    }

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
    private static bool TryParseEnvelope(string value, int layerSrid, out Envelope? envelope)
    {
        envelope = null;
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

                // The SDK puts the spatial reference inside the geometry rather
                // than in inSR. Ignoring it would accept a box stated in one
                // system and filter with it in another, which returns the wrong
                // features rather than none.
                if (root.TryGetProperty("spatialReference", out System.Text.Json.JsonElement reference)
                    && reference.TryGetProperty("wkid", out System.Text.Json.JsonElement wkid)
                    && wkid.TryGetInt32(out int declared)
                    && !SameSpatialReference(declared, layerSrid))
                {
                    return false;
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
