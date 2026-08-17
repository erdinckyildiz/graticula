using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

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

        foreach (string name in (string[])
        [
            "where", "objectIds", "geometry", "outFields", "outStatistics",
            "returnCountOnly", "returnIdsOnly", "returnExtentOnly", "resultOffset",
        ])
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

    /// <summary>Writes an object-id list as a page.</summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="description">Its fields, for the form.</param>
    /// <param name="ids">The matching ids.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The write.</returns>
    public static Task WriteIdsAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description,
        IReadOnlyList<long> ids,
        CancellationToken cancellation)
    {
        StringBuilder body = new();
        AppendForm(body, context, layer, description);

        string many = ids.Count.ToString("N0", CultureInfo.InvariantCulture);

        body.Append(CultureInfo.InvariantCulture,
            $"<h3>Results:</h3><p><b>{many}</b> object id(s):</p>");

        // <b>Comma-separated in one block, not a table.</b> Somebody looking at
        // this is about to paste it into an objectIds parameter, and a table of
        // ten thousand single-cell rows is unusable for that and slow to render.
        body.Append("<div class=\"scroll\"><code>")
            .Append(H(string.Join(", ", ids)))
            .Append("</code></div>");

        return WritePageAsync(context, body.ToString(), cancellation);
    }

    /// <summary>Writes an extent-only answer as a page.</summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="description">Its fields, for the form.</param>
    /// <param name="extent">The box, or null when nothing matched.</param>
    /// <param name="count">How many matched.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The write.</returns>
    public static Task WriteExtentAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description,
        Envelope? extent,
        long count,
        CancellationToken cancellation)
    {
        StringBuilder body = new();
        AppendForm(body, context, layer, description);

        body.Append(CultureInfo.InvariantCulture,
            $"<h3>Results:</h3><p><b>Count:</b> {count.ToString("N0", CultureInfo.InvariantCulture)}</p>");

        if (extent is { } box)
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<table class=\"props\"><tr><th>XMin</th><td>{box.MinX:0.######}</td></tr>"
                + $"<tr><th>YMin</th><td>{box.MinY:0.######}</td></tr>"
                + $"<tr><th>XMax</th><td>{box.MaxX:0.######}</td></tr>"
                + $"<tr><th>YMax</th><td>{box.MaxY:0.######}</td></tr></table>");
        }
        else
        {
            body.Append("<p><i>No extent: nothing matched, or every match has no geometry.</i></p>");
        }

        return WritePageAsync(context, body.ToString(), cancellation);
    }

    /// <summary>Writes computed statistics as a page.</summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="description">Its fields, for the form.</param>
    /// <param name="rows">One row per group.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The write.</returns>
    public static Task WriteStatisticsAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellation)
    {
        StringBuilder body = new();
        AppendForm(body, context, layer, description);

        body.Append("<h3>Results:</h3>");

        if (rows.Count == 0)
        {
            body.Append("<p><i>Nothing matched, so there was nothing to compute.</i></p>");
            return WritePageAsync(context, body.ToString(), cancellation);
        }

        body.Append("<div class=\"scroll\"><table class=\"grid\"><thead><tr>");

        foreach (string column in rows[0].Keys)
        {
            body.Append(CultureInfo.InvariantCulture, $"<th>{H(column)}</th>");
        }

        body.Append("</tr></thead><tbody>");

        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            body.Append("<tr>");

            foreach (string column in rows[0].Keys)
            {
                object? value = row.TryGetValue(column, out object? found) ? found : null;

                body.Append(CultureInfo.InvariantCulture,
                    $"<td>{(value is null
                        ? "<i>null</i>"
                        : H(Convert.ToString(value, CultureInfo.InvariantCulture)))}</td>");
            }

            body.Append("</tr>");
        }

        body.Append("</tbody></table></div>");

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
    /// <remarks>
    /// <para>
    /// <b>Every parameter ArcGIS's page has, in ArcGIS's order.</b> An
    /// administrator who knows that page should not have to re-learn this one,
    /// and a screenshot of the two should differ only in what is greyed out.
    /// </para>
    /// <para>
    /// <b>What this server cannot honour is present and disabled, not
    /// missing.</b> Omitting a control makes the page silently different from
    /// every ArcGIS page in the world, and leaves somebody hunting for a field
    /// that is simply not drawn. Disabling it with a reason answers the question
    /// on the spot — and a disabled input is not submitted, so the request is
    /// exactly the request the enabled controls describe.
    /// </para>
    /// </remarks>
    private static void AppendForm(
        StringBuilder body,
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description)
    {
        IQueryCollection q = context.Request.Query;

        body.Append(CultureInfo.InvariantCulture,
            $"<h2>Query: {H(layer.ServiceName)} - {H(layer.Definition.Name)} "
            + $"(ID: {layer.LayerIndex})</h2>");

        body.Append(CultureInfo.InvariantCulture,
            $"<form action=\"{H(context.Request.Path)}\" method=\"get\"><table class=\"form\">");

        Text(body, "Where:", "where", q["where"], "objectid > 0 and address like 'High%'", 80);

        Area(body, "Full Text Search:", "fullText", q["fullText"],
            "Needs a tsvector column and an index on your table, which this server will not "
            + "create on data it does not own.");

        Text(body, "Object IDs:", "objectIds", q["objectIds"], "1, 2, 3", 80);

        Text(body, "Unique Ids:", "uniqueIds", q["uniqueIds"], string.Empty, 80,
            disabled: "An ArcGIS 12.1 concept with no counterpart in this server.");

        Text(body, "Time:", "time", q["time"], string.Empty, 40,
            disabled: "No layer here declares timeInfo, so there is no time field to filter on.");

        Area(body, "Input Geometry:", "geometry", q["geometry"], null);

        Select(body, "Geometry Type:", "geometryType", q["geometryType"],
        [
            ("esriGeometryEnvelope", "Envelope"),
            ("esriGeometryPoint", "Point"),
            ("esriGeometryMultipoint", "Multipoint"),
            ("esriGeometryPolyline", "Polyline"),
            ("esriGeometryPolygon", "Polygon"),
        ]);

        Text(body, "Input Spatial Reference:", "inSR", q["inSR"],
            layer.Definition.Srid.ToString(CultureInfo.InvariantCulture), 20);

        Text(body, "Default Spatial Reference:", "defaultSR", q["defaultSR"], string.Empty, 20);

        Select(body, "Spatial Relationship:", "spatialRel", q["spatialRel"],
        [
            ("esriSpatialRelIntersects", "Intersects"),
            ("esriSpatialRelContains", "Contains"),
            ("esriSpatialRelCrosses", "Crosses"),
            ("esriSpatialRelEnvelopeIntersects", "Envelope Intersects"),
            ("esriSpatialRelIndexIntersects", "Index Intersects"),
            ("esriSpatialRelOverlaps", "Overlaps"),
            ("esriSpatialRelTouches", "Touches"),
            ("esriSpatialRelWithin", "Within"),
            ("esriSpatialRelRelation", "Relation"),
        ]);

        Text(body, "Distance:", "distance", q["distance"], string.Empty, 20);

        Select(body, "Units:", "units", q["units"],
        [
            ("", "(default: metres)"),
            ("esriSRUnit_Meter", "Meter"),
            ("esriSRUnit_Foot", "Foot"),
            ("esriSRUnit_Kilometer", "Kilometer"),
            ("esriSRUnit_StatuteMile", "Statute Mile"),
            ("esriSRUnit_NauticalMile", "Nautical Mile"),
            ("esriSRUnit_USNauticalMile", "US Nautical Mile"),
        ]);

        Text(body, "Relation:", "relationParam", q["relationParam"], "FFFTTT***", 20);

        Text(body, "Out Fields:", "outFields", q["outFields"], "*", 80);

        body.Append(CultureInfo.InvariantCulture,
            $"<tr><th></th><td class=\"hint\">{H(string.Join(", ", description.Fields.Select(f => f.Name)))}</td></tr>");

        Radio(body, "Return Geometry:", "returnGeometry", q["returnGeometry"], defaultTrue: true);

        Text(body, "Max Allowable Offset:", "maxAllowableOffset", q["maxAllowableOffset"],
            string.Empty, 20);

        Text(body, "Geometry Precision:", "geometryPrecision", q["geometryPrecision"],
            string.Empty, 20);

        Text(body, "Output Spatial Reference:", "outSR", q["outSR"], string.Empty, 20);

        // <b>No Having Clause box, since 2026-08-16.</b> ArcGIS's own page offers
        // one and ours did too, with `COUNT(objectid) > 5` as the placeholder — a
        // field whose contents went into the SQL statement unparsed (D-41). The
        // parameter is now refused, and a box that always answers 400 is worse
        // than no box: it reads as a capability and behaves as a bug report. It
        // comes back with the parser, Q-109.

        Text(body, "Order By Fields:", "orderByFields", q["orderByFields"],
            "field ASC, field DESC", 80);

        Text(body, "Group By Fields For Statistics:", "groupByFieldsForStatistics",
            q["groupByFieldsForStatistics"], "field1, field2", 80);

        Area(body, "Output Statistics:", "outStatistics", q["outStatistics"], null,
            "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\","
            + "\"outStatisticFieldName\":\"n\"}]");

        Radio(body, "Return Z:", "returnZ", q["returnZ"], defaultTrue: false,
            disabled: "Geometry is stored without z and m values.");

        Radio(body, "Return M:", "returnM", q["returnM"], defaultTrue: false,
            disabled: "Geometry is stored without z and m values.");

        Text(body, "gdbVersion:", "gdbVersion", q["gdbVersion"], string.Empty, 20,
            disabled: "There is no version tree.");

        Text(body, "Historic Moment:", "historicMoment", q["historicMoment"], string.Empty, 20,
            disabled: "There is no history to query.");

        Radio(body, "Return Distinct Values:", "returnDistinctValues", q["returnDistinctValues"],
            defaultTrue: false);

        Text(body, "Result Offset:", "resultOffset", q["resultOffset"], "0", 20);

        Text(body, "Result Record Count:", "resultRecordCount", q["resultRecordCount"], "1000", 20);

        Radio(body, "Return Extent Only:", "returnExtentOnly", q["returnExtentOnly"],
            defaultTrue: false);

        Radio(body, "Return Count Only:", "returnCountOnly", q["returnCountOnly"],
            defaultTrue: false);

        Radio(body, "Return IDs Only:", "returnIdsOnly", q["returnIdsOnly"], defaultTrue: false);

        Select(body, "SQL Format:", "sqlFormat", q["sqlFormat"],
            [("none", "none"), ("standard", "standard")]);

        // <b>Two, and the labels say what each one is.</b> "HTML" and "JSON"
        // are format names; a person choosing between them is choosing between
        // reading a table and copying a document into a client, and the labels
        // may as well say that.
        Select(body, "Format:", "f", string.IsNullOrEmpty(q["f"]) ? "html" : q["f"],
            [("html", "HTML (table)"), ("json", "JSON")]);

        body.Append(
            "<tr><th></th><td><button type=\"submit\">Query (GET)</button></td></tr>");

        body.Append("</table></form>");
    }

    /// <summary>A multi-line input, for the parameters that take JSON.</summary>
    private static void Area(
        StringBuilder body,
        string label,
        string name,
        string? value,
        string? disabled,
        string placeholder = "")
    {
        body.Append(CultureInfo.InvariantCulture,
            $"<tr><th>{H(label)}</th><td><textarea name=\"{H(name)}\" rows=\"4\" cols=\"78\" "
            + $"placeholder=\"{H(placeholder)}\"{Off(disabled)}>{H(value)}</textarea>"
            + $"{Why(disabled)}</td></tr>");
    }

    /// <summary>A true/false pair, which is how ArcGIS renders these.</summary>
    private static void Radio(
        StringBuilder body,
        string label,
        string name,
        string? value,
        bool defaultTrue,
        string? disabled = null)
    {
        bool on = string.IsNullOrEmpty(value) ? defaultTrue : value == "true";

        body.Append(CultureInfo.InvariantCulture,
            $"<tr><th>{H(label)}</th><td>"
            + $"<label><input type=\"radio\" name=\"{H(name)}\" value=\"true\""
            + $"{(on ? " checked" : string.Empty)}{Off(disabled)}> True</label> "
            + $"<label><input type=\"radio\" name=\"{H(name)}\" value=\"false\""
            + $"{(on ? string.Empty : " checked")}{Off(disabled)}> False</label>"
            + $"{Why(disabled)}</td></tr>");
    }

    /// <summary>The disabled attribute, or nothing.</summary>
    private static string Off(string? disabled) =>
        disabled is null ? string.Empty : " disabled";

    /// <summary>Why a control is disabled, said beside it.</summary>
    /// <remarks>
    /// <b>The reason is the point.</b> A greyed-out box with no explanation is
    /// the same dead end as a missing one, one step later.
    /// </remarks>
    private static string Why(string? disabled) =>
        disabled is null
            ? string.Empty
            : $"<div class=\"hint\">Not supported: {H(disabled)}</div>";

    private static void Text(
        StringBuilder body,
        string label,
        string name,
        string? value,
        string placeholder,
        int size,
        string? disabled = null)
    {
        body.Append(CultureInfo.InvariantCulture,
            $"<tr><th>{H(label)}</th><td><input type=\"text\" name=\"{H(name)}\" "
            + $"value=\"{H(value)}\" placeholder=\"{H(placeholder)}\" "
            + $"size=\"{size.ToString(CultureInfo.InvariantCulture)}\"{Off(disabled)}>"
            + $"{Why(disabled)}</td></tr>");
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
