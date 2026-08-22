using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// The <c>f</c> parameter, read the way every ArcGIS face on this server has to read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One copy, because there were two faces and only one of them read <c>f</c> at all.</b>
/// `MapServer/export` honoured <c>f=json</c> — a descriptor naming where the picture is,
/// which is what the JavaScript API asks for before it places an image element — and
/// `ImageServer/exportImage` ignored the parameter entirely and returned PNG bytes for
/// <c>f=json</c>, <c>f=html</c> and anything else. Two faces on one server disagreeing
/// about a parameter both document is a defect a client meets and cannot work around.
/// </para>
/// <para>
/// <b>Promoted here rather than copied across</b>, so that the next face to render an image
/// inherits the reading instead of a third interpretation of it. The <c>f</c>-twice rule
/// below is the kind of detail that survives one careful implementation and not two.
/// </para>
/// </remarks>
internal static class ArcGisResponseFormat
{
    /// <summary>The format asked for, or empty when none was.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The value, lower-cased by the caller if it cares.</returns>
    /// <remarks>
    /// <b>The first value, and the key matched case-insensitively.</b> A query string
    /// carrying <c>f</c> twice — which happens whenever a client appends its own format to a
    /// URL that already has one — makes <c>Query["f"]</c> the single string
    /// <c>"json,image"</c>, and an equality check then matches neither. The image came back
    /// for a request that had asked for JSON, with a 200 on it.
    /// </remarks>
    public static string Asked(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        StringValues values = default;

        foreach (KeyValuePair<string, StringValues> pair in context.Request.Query)
        {
            if (string.Equals(pair.Key, "f", StringComparison.OrdinalIgnoreCase))
            {
                values = pair.Value;
                break;
            }
        }

        return values.Count > 0 ? values[0] ?? string.Empty : string.Empty;
    }

    /// <summary>Whether the request asked for JSON.</summary>
    /// <param name="context">The request.</param>
    /// <returns>Whether <c>f</c> is <c>json</c> or <c>pjson</c>.</returns>
    /// <remarks>
    /// <b><c>pjson</c> counts, and it is not a synonym invented here.</b> Esri's own faces
    /// take it to mean *the same document, indented for a person reading it in a browser*.
    /// This server does not indent — the bytes are identical either way — so treating it as
    /// <c>json</c> answers the question that was asked rather than refusing a spelling.
    /// </remarks>
    public static bool WantsJson(HttpContext context)
    {
        string asked = Asked(context);

        return string.Equals(asked, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(asked, "pjson", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same query string with <c>f</c> replaced.</summary>
    /// <param name="query">The query string, with or without its leading question mark.</param>
    /// <param name="format">What <c>f</c> should become.</param>
    /// <returns>A query string starting with a question mark.</returns>
    /// <remarks>
    /// <b>So that a <c>href</c> in a JSON answer fetches the picture the client asked
    /// about.</b> Every other parameter has to survive: an href that dropped the extent
    /// would name a different picture, and the client would place it in the right frame.
    /// </remarks>
    public static string WithFormat(string? query, string format)
    {
        if (string.IsNullOrEmpty(query))
        {
            return "?f=" + format;
        }

        List<string> parts = [];

        foreach (string part in query.TrimStart('?').Split('&'))
        {
            if (!part.StartsWith("f=", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(part);
            }
        }

        parts.Add("f=" + format);

        return "?" + string.Join('&', parts);
    }
}
