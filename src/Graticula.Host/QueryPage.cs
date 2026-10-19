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

        HtmlBuilder body = new();
        AppendForm(body, context, layer, description);

        return WritePageAsync(context, body.ToHtml(), cancellation);
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

        HtmlBuilder head = new();

        // <b>No count here, and that is the price of streaming.</b> The rows are written as they
        // arrive, so how many there were is known only at the end — where the paging line says it.
        // What this line owes the reader is that the button did something.
        // <b>Neutral, because it may be zero</b>: "its rows are below" over an empty table was a
        // promise the page then broke (UX sweep 2026-10-19, round 2). The end of the table says
        // how many there were, or that none matched.
        AppendForm(head, context, layer, description, Outcome(
            Html.Markup("The query ran. The results are below the form.")));

        head.Append(Html.Markup(ResultsOpen + Scroll + "<table class=\"grid\"><thead><tr>"));

        if (query.IncludeGeometry)
        {
            head.Append(Html.Markup("<th scope=\"col\">Geometry</th>"));
        }

        foreach (string field in schema.Names)
        {
            head.Append($"<th scope=\"col\">{field}</th>");
        }

        head.Append(Html.Markup("</tr></thead><tbody>"));

        context.Response.ContentType = "text/html; charset=utf-8";

        // <b>The first row is pulled before a byte is written</b>, for the
        // reason the JSON writer documents at length: executing the query is
        // what raises the statement timeout and the dropped table, and once the
        // header is in the pipe the status cannot be taken back.
        IAsyncEnumerator<Feature> features =
            source.ReadAsync(query, cancellation).GetAsyncEnumerator(cancellation);

        // <b>Wrapped 2026-09-09.</b> This page writes a row at a time and had a
        // `finally` for disposal and no `catch`, so a failure after the first byte
        // was attributed to the caller leaving. It is the same route as `f=json` and
        // was the one of the two that actually streams. Q-91.
        await ErrorResponse.StreamAsync(context, "arcgis", async () =>
        {
        try
        {
            bool more = await features.MoveNextAsync().ConfigureAwait(false);

            await context.Response.WriteAsync(Page(context, head.ToHtml()), cancellation)
                .ConfigureAwait(false);

            int written = 0;

            for (; more; more = await features.MoveNextAsync().ConfigureAwait(false))
            {
                await context.Response
                    .WriteAsync(Row(features.Current, schema, query.IncludeGeometry).ToString(), cancellation)
                    .ConfigureAwait(false);

                written++;
            }

            HtmlBuilder tail = new HtmlBuilder().Append(Html.Markup("</tbody></table></div>"));

            if (written == 0)
            {
                tail.Append(Html.Markup("<p><i>No rows matched.</i></p>"));
            }

            AppendPaging(tail, context, query, written);

            tail.Append(Html.Markup("</section></main></body></html>"));

            await context.Response.WriteAsync(tail.ToHtml().ToString(), cancellation).ConfigureAwait(false);
        }
        finally
        {
            await features.DisposeAsync().ConfigureAwait(false);
        }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the form again with the reason the query was refused, keeping what the reader typed.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="description">Its fields, for the form.</param>
    /// <param name="message">The sentence the JSON answer would carry.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The write.</returns>
    /// <remarks>
    /// <b>A 400 still, and a page.</b> A where clause with a typo replaced this page with
    /// <c>{"error":{"code":400,…}}</c> — the form and everything typed into it gone, and the way
    /// back the browser's Back button (UX sweep 2026-10-19). The status stays what the JSON says,
    /// so a script that asked for HTML still learns it failed; the sentence is the JSON's own.
    /// </remarks>
    public static Task WriteRefusalAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description,
        string message,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status400BadRequest;

        HtmlBuilder body = new();
        AppendForm(body, context, layer, description, refusal: message);

        return WritePageAsync(context, body.ToHtml(), cancellation);
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

        string many = count.ToString("N0", CultureInfo.InvariantCulture);

        HtmlBuilder body = new();
        AppendForm(body, context, layer, description, Outcome(Html.Of($"<b>{many}</b> {(count == 1 ? "feature matches" : "features match")}.")));

        body.Append($"{Html.Markup(ResultsOpen)}<p><b>Count:</b> {many}</p></section>");

        return WritePageAsync(context, body.ToHtml(), cancellation);
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
        string many = ids.Count.ToString("N0", CultureInfo.InvariantCulture);

        HtmlBuilder body = new();
        AppendForm(body, context, layer, description, Outcome(Html.Of($"<b>{many}</b> object id(s) matched.")));

        body.Append($"{Html.Markup(ResultsOpen)}<p><b>{many}</b> object id(s):</p>");

        // <b>Comma-separated in one block, not a table.</b> Somebody looking at
        // this is about to paste it into an objectIds parameter, and a table of
        // ten thousand single-cell rows is unusable for that and slow to render.
        body.Append(Html.Markup(Scroll + "<code>"))
            .Append(Html.Text(string.Join(", ", ids)))
            .Append(Html.Markup("</code></div></section>"));

        return WritePageAsync(context, body.ToHtml(), cancellation);
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
        string many = count.ToString("N0", CultureInfo.InvariantCulture);

        HtmlBuilder body = new();
        AppendForm(body, context, layer, description, Outcome(
            Html.Of($"<b>{many}</b> {(count == 1 ? "feature matches" : "features match")}; the extent is below.")));

        body.Append($"{Html.Markup(ResultsOpen)}<p><b>Count:</b> {many}</p>");

        if (extent is { } box)
        {
            body.Append($"<table class=\"props\"><tr><th>XMin</th><td>{box.MinX:0.######}</td></tr>"
                + $"<tr><th>YMin</th><td>{box.MinY:0.######}</td></tr>"
                + $"<tr><th>XMax</th><td>{box.MaxX:0.######}</td></tr>"
                + $"<tr><th>YMax</th><td>{box.MaxY:0.######}</td></tr></table>");
        }
        else
        {
            body.Append(Html.Markup("<p><i>No extent: nothing matched, or every match has no geometry.</i></p>"));
        }

        body.Append(Html.Markup("</section>"));

        return WritePageAsync(context, body.ToHtml(), cancellation);
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
        HtmlBuilder body = new();
        AppendForm(body, context, layer, description, Outcome(Html.Of(
            $"<b>{rows.Count.ToString("N0", CultureInfo.InvariantCulture)}</b> {(rows.Count == 1 ? "row" : "rows")} of statistics.")));

        body.Append(Html.Markup(ResultsOpen));

        if (rows.Count == 0)
        {
            body.Append(Html.Markup("<p><i>Nothing matched, so there was nothing to compute.</i></p></section>"));
            return WritePageAsync(context, body.ToHtml(), cancellation);
        }

        body.Append(Html.Markup(Scroll + "<table class=\"grid\"><thead><tr>"));

        foreach (string column in rows[0].Keys)
        {
            body.Append($"<th scope=\"col\">{column}</th>");
        }

        body.Append(Html.Markup("</tr></thead><tbody>"));

        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            body.Append(Html.Markup("<tr>"));

            foreach (string column in rows[0].Keys)
            {
                object? value = row.TryGetValue(column, out object? found) ? found : null;

                body.Append($"<td>{(value is null
                        ? Html.Markup("<i>null</i>")
                        : Html.Text(Convert.ToString(value, CultureInfo.InvariantCulture)))}</td>");
            }

            body.Append(Html.Markup("</tr>"));
        }

        body.Append(Html.Markup("</tbody></table></div></section>"));

        return WritePageAsync(context, body.ToHtml(), cancellation);
    }

    /// <summary>Where the results begin, which the outcome line above the form links to.</summary>
    /// <remarks>
    /// <b>The heading is the one the conformance suite looks for</b>, so it stays as it was; the
    /// section around it is what the jump link lands on.
    /// </remarks>
    /// <summary>The box the results scroll in, which a keyboard can reach.</summary>
    /// <remarks>
    /// <b>Focusable and named, because a box that scrolls and cannot take focus is one a keyboard
    /// user cannot read past its first screen</b> (UX sweep 2026-10-19, round 2: axe
    /// <c>scrollable-region-focusable</c>).
    /// </remarks>
    private const string Scroll = "<div class=\"scroll\" tabindex=\"0\" role=\"region\" aria-label=\"Results\">";

    private const string ResultsOpen = "<section id=\"results\" aria-label=\"Results\"><h3>Results:</h3>";

    /// <summary>The line above the form that says what a run produced, with the way down to it.</summary>
    /// <remarks>
    /// <b>Above the form because the form is two thousand pixels tall.</b> At 1280 by 800 the
    /// results began below the fold, so pressing the button changed nothing a reader could see
    /// (UX sweep 2026-10-19). <c>role="status"</c> so a screen reader hears it on arrival.
    /// </remarks>
    private static Html Outcome(Html said) =>
        Html.Of($"<div class=\"outcome\" role=\"status\"><p>{said}</p><a href=\"#results\">Jump to the results</a></div>");

    /// <summary>One result row.</summary>
    /// <remarks>
    /// <b>The geometry is summarised, not printed.</b> A polygon's coordinate
    /// list is thousands of numbers, and pasting it into a table cell makes the
    /// attributes — which is what somebody is reading — impossible to find. The
    /// JSON link at the top of the page is where the coordinates are.
    /// </remarks>
    private static Html Row(Feature feature, FeatureSchema schema, bool geometry)
    {
        HtmlBuilder row = new HtmlBuilder().Append(Html.Markup("<tr>"));

        if (geometry)
        {
            row.Append($"<td>{Summarise(feature.Geometry)}</td>");
        }

        for (int i = 0; i < schema.Names.Count; i++)
        {
            object? value = feature[i];

            row.Append($"<td>{(value is null
                    ? Html.Markup("<i>null</i>")
                    : Html.Text(Convert.ToString(value, CultureInfo.InvariantCulture)))}</td>");
        }

        return row.Append(Html.Markup("</tr>")).ToHtml();
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
        HtmlBuilder body, HttpContext context, FeatureQuery query, int written)
    {
        int offset = query.Offset;
        int limit = query.Limit;

        // <b>No "Rows 1–0".</b> An empty page has said so above this line; what is left to offer is
        // the way back, when there is one.
        if (written == 0)
        {
            if (offset > 0)
            {
                body.Append($"<p class=\"paging\"><a href=\"{WithOffset(context, Math.Max(0, offset - limit))}\">« Previous</a></p>");
            }

            return;
        }

        body.Append($"<p class=\"paging\">Rows {offset + 1}–{offset + written} "
            + $"(page size {limit}). ");

        if (offset > 0)
        {
            body.Append($"<a href=\"{WithOffset(context, Math.Max(0, offset - limit))}\">« Previous</a> ");
        }

        if (written >= limit)
        {
            body.Append($"<a href=\"{WithOffset(context, offset + limit)}\">Next »</a>");
        }
        else
        {
            body.Append(Html.Markup("<i>Last page.</i>"));
        }

        body.Append(Html.Markup("</p>"));
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
    /// <param name="body">Where to write it.</param>
    /// <param name="context">The request, whose values the form keeps.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="description">Its fields.</param>
    /// <param name="outcome">What a run produced, said above the form; empty before a run.</param>
    /// <param name="refusal">Why the last run was refused, or null.</param>
    /// <remarks>
    /// <para>
    /// <b>Every parameter ArcGIS's page has, grouped the way a reader looks for them.</b> An
    /// administrator who knows that page should not have to re-learn this one. Until 2026-10-19
    /// the controls were one table in ArcGIS's order with the label beside each box; forty of
    /// them in a column, at 634 pixels wide on a phone and with not one <c>&lt;label for&gt;</c>,
    /// so a screen reader could not tell Distance from Geometry Precision (UX sweep 2026-10-19).
    /// They are now in fieldsets — filter, spatial filter, output, statistics, paging — each
    /// control with its own label, and ArcGIS's order is kept inside each group.
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
        HtmlBuilder body,
        HttpContext context,
        PublishedLayer layer,
        LayerDescription description,
        Html outcome = default,
        string? refusal = null)
    {
        IQueryCollection q = context.Request.Query;

        body.Append($"<h1>Query: {layer.ServiceName} - {layer.Definition.Name} "
            + $"(ID: {layer.LayerIndex})</h1>");

        body.Append(outcome);

        /*
          <b>A refusal sits beside the field it is about when this page can tell which one.</b>
          Nearly every refusal a reader meets here is a where clause, and the sentence names
          `where` when it is; the rest — an unknown reference, a moment before the kept history —
          go above the form, where they are read before any field.
        */
        bool aboutWhere = refusal is not null
            && refusal.Contains("where", StringComparison.OrdinalIgnoreCase);

        if (refusal is not null && !aboutWhere)
        {
            body.Append($"<p class=\"refused\" role=\"alert\">The query was not run. {refusal}</p>");
        }

        // Names from this layer, so every example on the form is one the reader can run (UX sweep
        // 2026-10-19: the Where example named an `address` field no layer here has).
        string id = layer.Definition.IntegerIdentityColumn ?? "objectid";
        string? text = description.Fields
            .Where(f => f.Type == FieldType.Text && !string.Equals(f.Name, id, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .FirstOrDefault();
        string other = text ?? description.Fields
            .Select(f => f.Name)
            .FirstOrDefault(n => !string.Equals(n, id, StringComparison.OrdinalIgnoreCase)) ?? id;

        body.Append($"<form class=\"ops\" action=\"{context.Request.Path}\" method=\"get\">");

        // ---------- filter ----------
        body.Append(Html.Markup("<fieldset><legend>Filter</legend>"));

        Text(body, "Where", "where", q["where"],
            text is null ? $"{id} > 0" : $"{id} > 0 AND {text} LIKE 'A%'",
            wide: true,
            hint: "SQL on this layer's fields, which are listed under Out fields.",
            invalid: aboutWhere ? refusal : null);

        Area(body, "Full text search", "fullText", q["fullText"],
            "Needs a tsvector column and an index on your table, which this server will not "
            + "create on data it does not own.");

        Text(body, "Object IDs", "objectIds", q["objectIds"], "1, 2, 3");

        Text(body, "Unique IDs", "uniqueIds", q["uniqueIds"], string.Empty,
            disabled: "An ArcGIS 12.1 concept with no counterpart in this server.");

        // <b>Enabled where the layer has a time field — 2026-09-15</b>, the same field the layer
        // document reports as `timeInfo` and the query filters on.
        Text(body, "Time", "time", q["time"], string.Empty,
            disabled: Graticula.Api.Wms.TimeDimension.FieldOf(description.Fields, layer.TimeField) is null
                ? "This layer has no time field, so there is nothing to filter on."
                : null);

        body.Append(Html.Markup("</fieldset>"));

        // ---------- spatial filter ----------
        body.Append(Html.Markup("<fieldset><legend>Spatial filter</legend>"));

        Area(body, "Input geometry", "geometry", q["geometry"], null,
            hint: "An envelope as xmin,ymin,xmax,ymax, or a geometry as Esri JSON.");

        Select(body, "Geometry type", "geometryType", q["geometryType"],
        [
            ("esriGeometryEnvelope", "Envelope"),
            ("esriGeometryPoint", "Point"),
            ("esriGeometryMultipoint", "Multipoint"),
            ("esriGeometryPolyline", "Polyline"),
            ("esriGeometryPolygon", "Polygon"),
        ]);

        Text(body, "Input spatial reference", "inSR", q["inSR"],
            layer.Definition.Srid.ToString(CultureInfo.InvariantCulture));

        Text(body, "Default spatial reference", "defaultSR", q["defaultSR"], string.Empty);

        Select(body, "Spatial relationship", "spatialRel", q["spatialRel"],
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

        Text(body, "Distance", "distance", q["distance"], string.Empty);

        Select(body, "Units", "units", q["units"],
        [
            ("", "(default: metres)"),
            ("esriSRUnit_Meter", "Meter"),
            ("esriSRUnit_Foot", "Foot"),
            ("esriSRUnit_Kilometer", "Kilometer"),
            ("esriSRUnit_StatuteMile", "Statute Mile"),
            ("esriSRUnit_NauticalMile", "Nautical Mile"),
            ("esriSRUnit_USNauticalMile", "US Nautical Mile"),
        ]);

        Text(body, "Relation", "relationParam", q["relationParam"], "FFFTTT***");

        body.Append(Html.Markup("</fieldset>"));

        // ---------- output ----------
        body.Append(Html.Markup("<fieldset><legend>Output</legend>"));

        Text(body, "Out fields", "outFields", q["outFields"], "*", wide: true,
            hint: "* for all, or some of: " + string.Join(", ", description.Fields.Select(f => f.Name)));

        Radio(body, "Return geometry", "returnGeometry", q["returnGeometry"], defaultTrue: true);

        Text(body, "Output spatial reference", "outSR", q["outSR"], string.Empty);

        Text(body, "Max allowable offset", "maxAllowableOffset", q["maxAllowableOffset"], string.Empty);

        Text(body, "Geometry precision", "geometryPrecision", q["geometryPrecision"], string.Empty);

        Text(body, "Order by fields", "orderByFields", q["orderByFields"], $"{other} ASC, {id} DESC");

        // <b>The layer's answer, not the server's — ADR-077, step 3</b>, as `time` became above: `query`
        // returns an elevation and a measure where the column declares them, so the control is offered
        // there and greyed out, with the reason, where it does not.
        Radio(body, "Return Z", "returnZ", q["returnZ"], defaultTrue: false,
            disabled: (description.StoredOrdinates & Graticula.Geometries.GeometryOrdinates.Z) != 0
                ? null
                : "This layer's geometry is stored without z values.");

        Radio(body, "Return M", "returnM", q["returnM"], defaultTrue: false,
            disabled: (description.StoredOrdinates & Graticula.Geometries.GeometryOrdinates.M) != 0
                ? null
                : "This layer's geometry is stored without m values.");

        Radio(body, "Return distinct values", "returnDistinctValues", q["returnDistinctValues"],
            defaultTrue: false);

        Text(body, "gdbVersion", "gdbVersion", q["gdbVersion"], string.Empty,
            disabled: "There is no version tree.");

        // The layer's answer too since 2026-09-19 — ADR-078: a hosted layer whose owner turned its
        // history on answers a moment in the past, and every other layer refuses one.
        Text(body, "Historic moment", "historicMoment", q["historicMoment"], string.Empty,
            disabled: description.Archived
                ? null
                : "This layer does not keep its history.");

        Select(body, "SQL format", "sqlFormat", q["sqlFormat"],
            [("none", "none"), ("standard", "standard")]);

        body.Append(Html.Markup("</fieldset>"));

        // ---------- statistics ----------
        body.Append(Html.Markup("<fieldset><legend>Statistics</legend>"));

        Text(body, "Group by fields for statistics", "groupByFieldsForStatistics",
            q["groupByFieldsForStatistics"], other);

        Area(body, "Output statistics", "outStatistics", q["outStatistics"], null,
            placeholder: $"[{{\"statisticType\":\"count\",\"onStatisticField\":\"{id}\","
            + "\"outStatisticFieldName\":\"n\"}]");

        // <b>No Having Clause box from 2026-08-16 to 2026-10-17</b>, while the parameter was refused: its
        // contents had gone into the SQL statement unparsed (D-41). It is back with its parser — Q-109 —
        // on the layers that answer it.
        if (description.AnswersHaving)
        {
            Text(body, "Having clause", "havingClause", q["havingClause"], $"COUNT({id}) > 5");
        }

        body.Append(Html.Markup("</fieldset>"));

        // ---------- paging and the shape of the answer ----------
        body.Append(Html.Markup("<fieldset><legend>Paging and answer</legend>"));

        Text(body, "Result offset", "resultOffset", q["resultOffset"], "0");

        Text(body, "Result record count", "resultRecordCount", q["resultRecordCount"], "1000");

        Radio(body, "Return extent only", "returnExtentOnly", q["returnExtentOnly"],
            defaultTrue: false);

        Radio(body, "Return count only", "returnCountOnly", q["returnCountOnly"],
            defaultTrue: false);

        Radio(body, "Return IDs only", "returnIdsOnly", q["returnIdsOnly"], defaultTrue: false);

        body.Append(Html.Markup("</fieldset>"));

        // <b>Two, and the labels say what each one is.</b> "HTML" and "JSON"
        // are format names; a person choosing between them is choosing between
        // reading a table and copying a document into a client, and the labels
        // may as well say that.
        body.Append(Html.Markup("<div class=\"run\">"));

        Select(body, "Format", "f", string.IsNullOrEmpty(q["f"]) ? "html" : q["f"],
            [("html", "HTML (table)"), ("json", "JSON")]);

        body.Append(Html.Markup("<button type=\"submit\">Query (GET)</button></div></form>"));
    }

    /// <summary>The id a control is given, so its label can name it.</summary>
    private static string Id(string name) => "q-" + name;

    /// <summary>The <c>aria-describedby</c> attribute for the notes under a control, or nothing.</summary>
    private static Html Described(string name, bool hint, bool why, bool refused) =>
        hint || why || refused
            ? Html.Of($" aria-describedby=\"{string.Join(' ', new[]
                {
                    refused ? Id(name) + "-refused" : null,
                    hint ? Id(name) + "-hint" : null,
                    why ? Id(name) + "-why" : null,
                }.Where(x => x is not null))}\"")
            : Html.Empty;

    /// <summary>A note under a control.</summary>
    private static Html Hint(string name, string? hint) =>
        hint is null ? Html.Empty : Html.Of($"<p class=\"hint\" id=\"{Id(name)}-hint\">{hint}</p>");

    /// <summary>The wrapper's classes: wide, and greyed out when the server cannot honour it.</summary>
    private static string Classes(bool wide, string? disabled) =>
        "f" + (wide ? " wide" : string.Empty) + (disabled is null ? string.Empty : " off");

    /// <summary>A multi-line input, for the parameters that take JSON.</summary>
    private static void Area(
        HtmlBuilder body,
        string label,
        string name,
        string? value,
        string? disabled,
        string placeholder = "",
        string? hint = null)
    {
        body.Append($"<div class=\"{Classes(true, disabled)}\"><label for=\"{Id(name)}\">{label}</label>"
            + $"<textarea name=\"{name}\" id=\"{Id(name)}\" rows=\"4\" "
            + $"placeholder=\"{placeholder}\"{Described(name, hint is not null, disabled is not null, false)}"
            + $"{Off(disabled)}>{value}</textarea>"
            + $"{Hint(name, hint)}{Why(name, disabled)}</div>");
    }

    /// <summary>A true/false pair, which is how ArcGIS renders these.</summary>
    /// <remarks>
    /// <b>A fieldset whose legend is the label</b>, because the two radios are one control with
    /// one name, and a label can name only one of them. The attribute order of each input is
    /// relied on by the conformance test that submits this form as a browser would.
    /// </remarks>
    private static void Radio(
        HtmlBuilder body,
        string label,
        string name,
        string? value,
        bool defaultTrue,
        string? disabled = null)
    {
        bool on = string.IsNullOrEmpty(value) ? defaultTrue : value == "true";

        body.Append($"<fieldset class=\"{Classes(false, disabled) + " pair"}\"{Described(name, false, disabled is not null, false)}>"
            + $"<legend>{label}</legend>"
            + $"<label><input type=\"radio\" name=\"{name}\" value=\"true\""
            + $"{(on ? " checked" : string.Empty)}{Off(disabled)}> True</label>"
            + $"<label><input type=\"radio\" name=\"{name}\" value=\"false\""
            + $"{(on ? string.Empty : " checked")}{Off(disabled)}> False</label>"
            + $"{Why(name, disabled)}</fieldset>");
    }

    /// <summary>The disabled attribute, or nothing.</summary>
    private static string Off(string? disabled) =>
        disabled is null ? string.Empty : " disabled";

    /// <summary>Why a control is disabled, said beside it.</summary>
    /// <remarks>
    /// <b>The reason is the point.</b> A greyed-out box with no explanation is
    /// the same dead end as a missing one, one step later.
    /// </remarks>
    private static Html Why(string name, string? disabled) =>
        disabled is null
            ? Html.Empty
            : Html.Of($"<p class=\"hint\" id=\"{Id(name)}-why\">Not supported: {disabled}</p>");

    /// <summary>A one-line input.</summary>
    /// <remarks>
    /// <b>No <c>size</c>, and the box is as wide as its group</b> — a <c>size="80"</c> box is
    /// eighty characters wide whatever the screen is. <c>name</c> and <c>value</c> come first
    /// and in that order: the conformance test that submits this form reads them that way.
    /// </remarks>
    private static void Text(
        HtmlBuilder body,
        string label,
        string name,
        string? value,
        string placeholder,
        string? disabled = null,
        bool wide = false,
        string? hint = null,
        string? invalid = null)
    {
        Html refused = invalid is null
            ? Html.Empty
            : Html.Of($"<p class=\"refused\" id=\"{Id(name)}-refused\" role=\"alert\">The query was not run. {invalid}</p>");

        body.Append($"<div class=\"{Classes(wide, disabled)}\"><label for=\"{Id(name)}\">{label}</label>"
            + $"<input type=\"text\" name=\"{name}\" value=\"{value}\" id=\"{Id(name)}\" "
            + $"placeholder=\"{placeholder}\""
            + $"{(invalid is null ? Html.Empty : Html.Markup(" aria-invalid=\"true\""))}"
            + $"{Described(name, hint is not null, disabled is not null, invalid is not null)}{Off(disabled)}>"
            + $"{refused}{Hint(name, hint)}{Why(name, disabled)}</div>");
    }

    private static void Select(
        HtmlBuilder body,
        string label,
        string name,
        string? value,
        IReadOnlyList<(string Value, string Label)> options)
    {
        body.Append($"<div class=\"f\"><label for=\"{Id(name)}\">{label}</label>"
            + $"<select name=\"{name}\" id=\"{Id(name)}\">");

        foreach ((string option, string text) in options)
        {
            bool chosen = string.Equals(option, value, StringComparison.Ordinal);

            body.Append($"<option value=\"{option}\"{(chosen ? " selected" : string.Empty)}>"
                + $"{text}</option>");
        }

        body.Append(Html.Markup("</select></div>"));
    }

    private static Task WritePageAsync(HttpContext context, Html body, CancellationToken ct)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(Page(context, body) + "</main></body></html>", ct);
    }

    /// <summary>The shell, shared with the rest of the directory, left open.</summary>
    /// <remarks>
    /// <b>Returned without its closing <c>&lt;/main&gt;</c> on purpose.</b> The
    /// results stream in after it, so the page cannot be finished before the
    /// rows are known. Every caller closes it.
    /// </remarks>
    private static string Page(HttpContext context, Html body) =>
        RestDirectory.OpenPage(context.Request.Path, body);

}
