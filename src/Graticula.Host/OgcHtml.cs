using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Graticula.Api.OgcFeatures;
using Graticula.Features;

namespace Graticula.Host;

/// <summary>
/// The HTML representation OGC API Features' <c>html</c> conformance class requires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rendered through <see cref="RestDirectory"/> rather than with a second set of
/// pages.</b> This server already has a directory renderer with a breadcrumb, a
/// format line and a signed-in badge, and a second one would be a second place for
/// the theme, the escaping and the dark-mode palette to be right.
/// </para>
/// <para>
/// <b>The class is claimed, so these pages have to carry the links.</b> The HTML
/// representation of a resource must be navigable to the same places as the JSON
/// one — a page that renders the data and drops the <c>links</c> array is a dead end
/// for a person and a failed assertion for a validator.
/// </para>
/// </remarks>
internal static class OgcHtml
{
    /// <summary>The landing page.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="document">The JSON this page shows.</param>
    /// <returns>The HTML.</returns>
    public static string Landing(string path, string document) =>
        Document(path, "OGC API Features", document);

    /// <summary>Any document, as a property table with its links followed.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="title">What to call it.</param>
    /// <param name="document">The JSON.</param>
    /// <returns>The HTML.</returns>
    public static string Document(string path, string title, string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);

        using JsonDocument parsed = JsonDocument.Parse(document);

        return RestDirectory.Document(path, title, parsed.RootElement.Clone());
    }

    /// <summary>The collection list, as a table.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="collections">The collections.</param>
    /// <returns>The HTML.</returns>
    public static string Collections(string path, IReadOnlyList<CollectionMetadata> collections)
    {
        ArgumentNullException.ThrowIfNull(collections);

        HtmlBuilder body = new();

        body.Append(Html.Markup("<h1>Collections</h1>"));

        if (collections.Count == 0)
        {
            // <b>A sentence rather than an empty table.</b> Nothing published, or
            // nothing shared with this caller, are the same page to a stranger and
            // both are answers.
            body.Append(Html.Markup(
                "<p>No collection is published to you. Sign in, or ask whoever published one "
                + "to share it.</p>"));

            return RestDirectory.Wrap(path, body.ToHtml());
        }

        body.Append(Html.Markup("<table class=\"grid\"><thead><tr><th>Collection</th><th>Title</th>"
            + "<th>Geometry</th><th>Stored in</th><th>Features</th></tr></thead><tbody>"));

        foreach (CollectionMetadata collection in collections)
        {
            string self = $"{OgcNames.Base}/collections/{Uri.EscapeDataString(collection.Id)}";

            body.Append($"<tr><td><a href=\"{self}?f=html\">"
                + $"{collection.Id}</a></td>"
                + $"<td>{collection.Title}</td>"
                + $"<td>{collection.GeometryType?.ToString() ?? "none (a table)"}</td>"
                + $"<td>EPSG:{collection.Srid.ToString(CultureInfo.InvariantCulture)}</td>"
                + $"<td><a href=\"{self}/items?f=html\">items</a></td></tr>");
        }

        body.Append(Html.Markup("</tbody></table>"));

        return RestDirectory.Wrap(path, body.ToHtml());
    }

    /// <summary>One collection's page.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>The HTML.</returns>
    public static string Collection(string path, CollectionMetadata collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        string self = $"{OgcNames.Base}/collections/{Uri.EscapeDataString(collection.Id)}";

        HtmlBuilder body = new();

        body.Append($"<h2>{collection.Title}</h2>");

        body.Append($"<p><b>Features:</b> <a href=\"{self}/items?f=html\">as HTML</a>"
            + $" &middot; <a href=\"{self}/items\">as GeoJSON</a></p>");

        body.Append(Html.Markup("<table class=\"props\">"));

        Row(body, "id", collection.Id);
        Row(body, "geometry", collection.GeometryType?.ToString() ?? "none (a table)");
        Row(body, "storageCrs", collection.StorageCrs);

        if (collection.Extent is { IsEmpty: false } extent)
        {
            Row(body, "extent (CRS84)",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{extent.MinX:0.######}, {extent.MinY:0.######}, "
                    + $"{extent.MaxX:0.######}, {extent.MaxY:0.######}"));
        }

        if (collection.IsTemporal)
        {
            Row(body, "temporal property", collection.TemporalField!);
        }

        body.Append(Html.Markup("</table>"));

        body.Append(Html.Markup("<h3>Properties</h3><table class=\"grid\"><thead><tr><th>Name</th>"
            + "<th>Type</th><th>Nullable</th></tr></thead><tbody>"));

        foreach (FieldDescription field in collection.Fields)
        {
            body.Append($"<tr><td>{field.Name}</td>"
                + $"<td>{field.Type.ToString()}</td>"
                + $"<td>{(field.Nullable ? "yes" : "no")}</td></tr>");
        }

        body.Append(Html.Markup("</tbody></table>"));

        return RestDirectory.Wrap(path, body.ToHtml());
    }

    /// <summary>A page of features, as a table.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="collection">The collection.</param>
    /// <param name="features">The page.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="matched">How many matched in total.</param>
    /// <returns>The HTML.</returns>
    public static string Items(
        string path,
        CollectionMetadata collection,
        IReadOnlyList<Feature> features,
        OgcRequest request,
        long matched)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(request);

        HtmlBuilder body = new();

        body.Append($"<h2>{collection.Title}</h2>");

        body.Append($"<p>{features.Count.ToString("N0", CultureInfo.InvariantCulture)} of "
            + $"{matched.ToString("N0", CultureInfo.InvariantCulture)}, from "
            + $"{request.Offset.ToString("N0", CultureInfo.InvariantCulture)}. "
            + $"<a href=\"{path}\">This page as GeoJSON</a></p>");

        if (features.Count == 0)
        {
            body.Append(Html.Markup("<p>Nothing matched.</p>"));
            return RestDirectory.Wrap(path, body.ToHtml());
        }

        body.Append(Html.Markup("<table class=\"grid\"><thead><tr><th>id</th>"));

        foreach (string name in features[0].Schema.Names)
        {
            body.Append($"<th>{name}</th>");
        }

        body.Append(Html.Markup("</tr></thead><tbody>"));

        foreach (Feature feature in features)
        {
            string self = $"{path}/{Uri.EscapeDataString(feature.Id)}?f=html";

            body.Append($"<tr><td><a href=\"{self}\">"
                + $"{feature.Id}</a></td>");

            for (int i = 0; i < feature.Schema.Count; i++)
            {
                body.Append($"<td>{Text(feature[i])}</td>");
            }

            body.Append(Html.Markup("</tr>"));
        }

        body.Append(Html.Markup("</tbody></table>"));

        // <b>Paging links, because a table with no way forward is a table of the
        // first ten rows of everything.</b> Built from the same offsets the GeoJSON
        // links use, so the two representations page identically.
        body.Append(Html.Markup("<p class=\"paging\">"));

        if (request.Offset > 0)
        {
            body.Append($"<a href=\"{path}?f=html&amp;offset="
                + $"{Math.Max(0, request.Offset - request.Limit).ToString(CultureInfo.InvariantCulture)}"
                + $"&amp;limit={request.Limit.ToString(CultureInfo.InvariantCulture)}\">Previous</a> ");
        }

        if (request.Offset + features.Count < matched)
        {
            body.Append($"<a href=\"{path}?f=html&amp;offset="
                + $"{(request.Offset + request.Limit).ToString(CultureInfo.InvariantCulture)}"
                + $"&amp;limit={request.Limit.ToString(CultureInfo.InvariantCulture)}\">Next</a>");
        }

        body.Append(Html.Markup("</p>"));

        return RestDirectory.Wrap(path, body.ToHtml());
    }

    private static void Row(HtmlBuilder body, string name, string value) =>
        body.Append($"<tr><th>{name}</th>"
            + $"<td>{value}</td></tr>");

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
