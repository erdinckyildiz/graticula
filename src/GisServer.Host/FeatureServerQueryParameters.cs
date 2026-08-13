using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using GisServer.Features;
using GisServer.Geometries;
using Microsoft.AspNetCore.Http;

namespace GisServer.Host;

/// <summary>
/// Parses the ArcGIS FeatureServer <c>query</c> parameters we support.
/// </summary>
/// <remarks>
/// <para>
/// A small subset, deliberately. ADR-008 §2's principle is <em>never degrade
/// silently</em>: a parameter we do not implement is refused with a reason rather
/// than accepted and ignored, because a client that asks for
/// <c>returnCountOnly</c> and receives features has been lied to.
/// </para>
/// <para>
/// The full surface — <c>where</c>, ordering, statistics, pagination by
/// <c>resultOffset</c> — belongs with the query AST in ADR-008 and does not
/// exist yet.
/// </para>
/// </remarks>
internal static class FeatureServerQueryParameters
{
    /// <summary>Parameters that change the answer and that we do not implement.</summary>
    /// <remarks>
    /// Listed explicitly rather than by ignoring the unknown, because the harm
    /// is specific: each of these asks for a materially different response, and
    /// silently returning the default one is a wrong answer rather than a
    /// missing feature.
    /// </remarks>
    private static readonly string[] RefusedParameters =
    [
        "where", "orderByFields", "groupByFieldsForStatistics", "outStatistics",
        "returnCountOnly", "returnIdsOnly", "returnDistinctValues", "returnExtentOnly",
        "resultOffset", "objectIds", "time", "distance", "relationParam", "having",
    ];

    /// <summary>Parses, or explains why not.</summary>
    /// <param name="parameters">The query string.</param>
    /// <param name="objectIdColumn">
    /// Always requested, whatever <c>outFields</c> says. An ArcGIS response
    /// whose <c>objectIdFieldName</c> names a field the features do not carry is
    /// one a client cannot page or select against.
    /// </param>
    /// <param name="query">The parsed query.</param>
    /// <param name="error">Why it could not be parsed.</param>
    public static bool TryParse(
        IQueryCollection parameters,
        string objectIdColumn,
        [NotNullWhen(true)] out FeatureQuery? query,
        [NotNullWhen(false)] out string? error)
    {
        query = null;
        error = null;

        foreach (string refused in RefusedParameters)
        {
            if (parameters.ContainsKey(refused))
            {
                error =
                    $"'{refused}' is not supported yet. It is refused rather than ignored: "
                    + "answering a different question than the one asked would be worse than "
                    + "saying so. Supported: geometry (envelope), resultRecordCount, outFields.";
                return false;
            }
        }

        int limit = 1000;
        if (parameters.TryGetValue("resultRecordCount", out Microsoft.Extensions.Primitives.StringValues count)
            && count.Count > 0)
        {
            if (!int.TryParse(count[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)
                || limit < 1)
            {
                error = "resultRecordCount must be a positive integer.";
                return false;
            }

            limit = Math.Min(limit, FeatureQuery.MaximumLimit);
        }

        Envelope? boundingBox = null;
        if (parameters.TryGetValue("geometry", out Microsoft.Extensions.Primitives.StringValues geometry)
            && geometry.Count > 0
            && !string.IsNullOrWhiteSpace(geometry[0]))
        {
            if (!TryParseEnvelope(geometry[0]!, out boundingBox))
            {
                error =
                    "geometry must be an envelope as 'xmin,ymin,xmax,ymax'. Other geometry types "
                    + "as a spatial filter need the query AST (ADR-008) and are not implemented.";
                return false;
            }
        }

        string[]? fields = null;
        if (parameters.TryGetValue("outFields", out Microsoft.Extensions.Primitives.StringValues outFields)
            && outFields.Count > 0
            && !string.IsNullOrWhiteSpace(outFields[0])
            && outFields[0] != "*")
        {
            // '*' is refused rather than expanded: the catalogue does not record
            // column types yet, so we cannot honestly describe fields we were
            // not asked for by name.
            fields = outFields[0]!.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                | StringSplitOptions.TrimEntries);
        }
        else if (outFields.Count > 0 && outFields[0] == "*")
        {
            error =
                "outFields=* is not supported yet: the catalogue does not record column types, so "
                + "the field list in the response header would be a guess. Name the fields.";
            return false;
        }

        List<string> requested = [objectIdColumn];
        if (fields is not null)
        {
            foreach (string field in fields)
            {
                if (!string.Equals(field, objectIdColumn, StringComparison.Ordinal))
                {
                    requested.Add(field);
                }
            }
        }

        query = new FeatureQuery(limit, boundingBox, requested);
        return true;
    }

    private static bool TryParseEnvelope(string value, out Envelope? envelope)
    {
        envelope = null;
        string[] parts = value.Split(',');

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
}
