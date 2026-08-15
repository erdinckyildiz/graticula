using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;
using GisServer.Platform.Catalog;
using Microsoft.AspNetCore.Http;

namespace GisServer.Host;

/// <summary>
/// The query page: a form for building a query, and its results as a table.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where an ArcGIS administrator actually tries a query.</b> The
/// documented <c>f=html</c> format on the query operation is the form every
/// tutorial tells people to open, and it is how a WHERE clause gets tested
/// before it goes into a client. A server without one can be queried by anybody
/// who can hand-write a URL, which is a smaller set of people than the ones who
/// need to.
/// </para>
/// <para>
/// <b>The form shows only what this server honours, and that is the design.</b>
/// Esri's own page offers <c>outStatistics</c>, <c>groupByFieldsForStatistics</c>,
/// <c>distance</c>, <c>returnIdsOnly</c>, <c>time</c> and twenty more. Rendering
/// controls for parameters the query endpoint refuses would be
/// [ADR-008](../../docs/adr/ADR-008-query-engine.md) §2's never-degrade-silently
/// rule broken through the UI instead of through a header: somebody fills in a
/// box, presses the button, and gets an error the page invited. The form is a
/// live capability report, and if it is missing a control the answer is to
/// implement the parameter.
/// </para>
/// <para>
/// <b>Results stream.</b> A-037 measured allocation as the binding constraint,
/// and buffering a page of features to build a table would reintroduce exactly
/// the peak the JSON path is careful to avoid. Rows are written as they arrive.
/// </para>
/// </remarks>
internal static class QueryPage
{
    /// <summary>
    /// Whether this request is asking for the form rather than for results.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True when nothing has been submitted yet.</returns>
    /// <remarks>
    /// <b>An empty query string means "show me the form".</b> A bare
    /// <c>…/query</c> in a browser is somebody who wants to build a query, not
    /// somebody who wants every feature in the layer — and answering it with an
    /// unfiltered read of a large layer, as a table, would be a denial of
    /// service you can trigger by clicking a link.
    /// </remarks>
    public static bool WantsForm(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (string name in (string[])["where", "objectIds", "geometry", "outFields"])
        {
            if (!string.IsNullOrWhiteSpace(request.Query[name]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes the form, and nothing else.</summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="description">Its fields, for the field list.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The write.</returns>
    public static Task WriteFormAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);

        StringBuilder body = new();
        AppendForm(body, context, layer, description);

        return WritePageAsync(context, body.ToString(), cancellation);
    }

    /// <summary>
    /// Writes the form, then the results, streaming the rows as they arrive.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="description">Its fields, for the form.</param>
    /// <param name="source">Where the features come from.</param>
    /// <param name="query">The parsed query.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The write.</returns>
    public static async Task WriteResultsAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description,
        IFeatureSource source,
        FeatureQuery query,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        FeatureSchema schema = source.SchemaFor(query);

        StringBuilder head = new();
        AppendForm(head, context, layer, description);

        head.Append("<h3>Results:</h3>");
        head.Append("<div class=\"scroll\"><table class=\"grid\"><thead><tr>");

        if (query.IncludeGeometry)
        {
            head.Append("<th>Geometry</th>");
        }

        foreach (string field in schema.Names)
        {
            head.Append(CultureInfo.InvariantCulture, $"<th>{H(field)}</th>");
        }

        head.Append("</tr></thead><tbody>");

        context.Response.ContentType = "text/html; charset=utf-8";

        // <b>The first row is pulled before a byte is written</b>, for the
        // reason the JSON writer documents at length: executing the query is
        // what raises the statement timeout and the dropped table, and once the
        // header is in the pipe the status cannot be taken back.
        IAsyncEnumerator<Feature> features =
            source.ReadAsync(query, cancellation).GetAsyncEnumerator(cancellation);

        try
        {
            bool more = await features.MoveNextAsync().ConfigureAwait(false);

            await context.Response.WriteAsync(Page(context, head.ToString()), cancellation)
                .ConfigureAwait(false);

            int written = 0;

            for (; more; more = await features.MoveNextAsync().ConfigureAwait(false))
            {
                await context.Response
                    .WriteAsync(Row(features.Current, schema, query.IncludeGeometry), cancellation)
                    .ConfigureAwait(false);

                written++;
            }

            StringBuilder tail = new("</tbody></table></div>");

            if (written == 0)
            {
                tail.Append("<p><i>No features matched.</i></p>");
            }

            AppendPaging(tail, context, query, written);

            tail.Append("</main>");

            await context.Response.WriteAsync(tail.ToString(), cancellation).ConfigureAwait(false);
        }
        finally
        {
            await features.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Writes a count-only answer as a page.</summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="description">Its fields, for the form.</param>
    /// <param name="count">How many features matched.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The write.</returns>
    /// <remarks>
    /// <b>The form stays on the page.</b> Somebody who asked for a count is
    /// usually about to ask for the rows, and sending them back to a blank form
    /// to do it loses the WHERE clause they just wrote.
    /// </remarks>
    public static Task WriteCountAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description,
        long count,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);

        StringBuilder body = new();
        AppendForm(body, context, layer, description);

        body.Append(CultureInfo.InvariantCulture,
            $"<h3>Results:</h3><p><b>Count:</b> {count.ToString("N0", CultureInfo.InvariantCulture)}</p>");

        return WritePageAsync(context, body.ToString(), cancellation);
    }

    /// <summary>One result row.</summary>
    /// <remarks>
    /// <b>The geometry is summarised, not printed.</b> A polygon's coordinate
    /// list is thousands of numbers, and pasting it into a table cell makes the
    /// attributes — which is what somebody is reading — impossible to find. The
    /// JSON link at the top of the page is where the coordinates are.
    /// </remarks>
    private static string Row(Feature feature, FeatureSchema schema, bool geometry)
    {
        StringBuilder row = new("<tr>");

        if (geometry)
        {
            row.Append(CultureInfo.InvariantCulture, $"<td>{H(Summarise(feature.Geometry))}</td>");
        }

        for (int i = 0; i < schema.Names.Count; i++)
        {
            object? value = feature[i];

            row.Append(CultureInfo.InvariantCulture,
                $"<td>{(value is null
                    ? "<i>null</i>"
                    : H(Convert.ToString(value, CultureInfo.InvariantCulture)))}</td>");
        }

        return row.Append("</tr>").ToString();
    }

    /// <summary>A geometry as its kind and size, which is what fits in a cell.</summary>
    /// <remarks>
    /// <b><see cref="Geometry.CoordinateCount"/> rather than a walk of my own.</b>
    /// Every part of this codebase that counts vertices should get the same
    /// number, and a second implementation is a second answer waiting to
    /// disagree with the first about whether a ring's closing point counts.
    /// </remarks>
    private static string Summarise(Geometry? geometry) => geometry switch
    {
        null => "none",
        Point p => string.Create(
            CultureInfo.InvariantCulture, $"point ({p.X:0.######}, {p.Y:0.######})"),
        _ => string.Create(
            CultureInfo.InvariantCulture,
            $"{geometry.GetType().Name.ToLowerInvariant()}, "
            + $"{geometry.CoordinateCount} vertices"),
    };

    /// <summary>
    /// Previous and next links, built from the offset this page was asked with.
    /// </summary>
    /// <remarks>
    /// <b>Next appears only on a full page.</b> A short page is the last one —
    /// the same signal <c>exceededTransferLimit</c> carries in the JSON — and
    /// offering Next there would hand somebody a link to an empty table and no
    /// explanation of why.
    /// </remarks>
    private static void AppendPaging(
        StringBuilder body, HttpContext context, FeatureQuery query, int written)
    {
        int offset = query.Offset;
        int limit = query.Limit;

        body.Append(CultureInfo.InvariantCulture,
            $"<p class=\"paging\">Rows {offset + 1}–{offset + written} "
            + $"(page size {limit}). ");

        if (offset > 0)
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<a href=\"{H(WithOffset(context, Math.Max(0, offset - limit)))}\">« Previous</a> ");
        }

        if (written >= limit)
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<a href=\"{H(WithOffset(context, offset + limit))}\">Next »</a>");
        }
        else
        {
            body.Append("<i>Last page.</i>");
        }

        body.Append("</p>");
    }

    /// <summary>This request's URL with a different <c>resultOffset</c>.</summary>
    private static string WithOffset(HttpContext context, int offset)
    {
        List<string> parts = [];

        foreach (var pair in context.Request.Query)
        {
            if (string.Equals(pair.Key, "resultOffset", StringComparison.Ordinal))
            {
                continue;
            }

            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value.ToString())}");
        }

        parts.Add($"resultOffset={offset.ToString(CultureInfo.InvariantCulture)}");

        return $"{context.Request.Path}?{string.Join("&", parts)}";
    }

    /// <summary>The form itself.</summary>
    private static void AppendForm(
        StringBuilder body,
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description)
    {
        IQueryCollection q = context.Request.Query;

        body.Append(CultureInfo.InvariantCulture,
            $"<h2>Query: {H(layer.ServiceName)} - {H(layer.Definition.Name)} "
            + $"({layer.LayerIndex})</h2>");

        body.Append(CultureInfo.InvariantCulture,
            $"<form action=\"{H(context.Request.Path)}\" method=\"get\"><table class=\"form\">");

        Text(body, "Where:", "where", q["where"], "1=1", wide: true);

        Text(body, "Out Fields:", "outFields", q["outFields"], "*", wide: true);

        // The field names, so nobody has to open the layer document in another
        // tab to remember whether it is parcel_id or parcelid.
        body.Append(CultureInfo.InvariantCulture,
            $"<tr><th></th><td class=\"hint\">{H(string.Join(", ", description.Fields.Select(f => f.Name)))}</td></tr>");

        Text(body, "Input Geometry:", "geometry", q["geometry"],
            "xmin,ymin,xmax,ymax", wide: true);

        Select(body, "Geometry Type:", "geometryType", q["geometryType"],
            [("", "(none)"), ("esriGeometryEnvelope", "esriGeometryEnvelope")]);

        Select(body, "Spatial Relationship:", "spatialRel", q["spatialRel"],
            [("", "(default)"), ("esriSpatialRelIntersects", "esriSpatialRelIntersects")]);

        Text(body, "Input Spatial Reference:", "inSR", q["inSR"],
            layer.Definition.Srid.ToString(CultureInfo.InvariantCulture), wide: false);

        Text(body, "Order By Fields:", "orderByFields", q["orderByFields"],
            "field ASC, field DESC", wide: true);

        Text(body, "Result Offset:", "resultOffset", q["resultOffset"], "0", wide: false);

        Text(body, "Result Record Count:", "resultRecordCount", q["resultRecordCount"],
            "1000", wide: false);

        Select(body, "Return Geometry:", "returnGeometry", q["returnGeometry"],
            [("", "true"), ("true", "true"), ("false", "false")]);

        Select(body, "Return Count Only:", "returnCountOnly", q["returnCountOnly"],
            [("", "false"), ("false", "false"), ("true", "true")]);

        Text(body, "Output Spatial Reference:", "outSR", q["outSR"], "", wide: false);

        Select(body, "Format:", "f", string.IsNullOrEmpty(q["f"]) ? "html" : q["f"],
            [("html", "HTML"), ("json", "JSON")]);

        body.Append(
            "<tr><th></th><td><button type=\"submit\">Query (GET)</button></td></tr>");

        body.Append("</table></form>");
    }

    private static void Text(
        StringBuilder body, string label, string name, string? value, string placeholder, bool wide)
    {
        body.Append(CultureInfo.InvariantCulture,
            $"<tr><th>{H(label)}</th><td><input type=\"text\" name=\"{H(name)}\" "
            + $"value=\"{H(value)}\" placeholder=\"{H(placeholder)}\" "
            + $"size=\"{(wide ? 70 : 12)}\"></td></tr>");
    }

    private static void Select(
        StringBuilder body,
        string label,
        string name,
        string? value,
        IReadOnlyList<(string Value, string Label)> options)
    {
        body.Append(CultureInfo.InvariantCulture,
            $"<tr><th>{H(label)}</th><td><select name=\"{H(name)}\">");

        foreach ((string option, string text) in options)
        {
            bool chosen = string.Equals(option, value, StringComparison.Ordinal);

            body.Append(CultureInfo.InvariantCulture,
                $"<option value=\"{H(option)}\"{(chosen ? " selected" : string.Empty)}>"
                + $"{H(text)}</option>");
        }

        body.Append("</select></td></tr>");
    }

    private static Task WritePageAsync(HttpContext context, string body, CancellationToken ct)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(Page(context, body) + "</main>", ct);
    }

    /// <summary>The shell, shared with the rest of the directory, left open.</summary>
    /// <remarks>
    /// <b>Returned without its closing <c>&lt;/main&gt;</c> on purpose.</b> The
    /// results stream in after it, so the page cannot be finished before the
    /// rows are known. Every caller closes it.
    /// </remarks>
    private static string Page(HttpContext context, string body) =>
        RestDirectory.OpenPage(context.Request.Path, body);

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
