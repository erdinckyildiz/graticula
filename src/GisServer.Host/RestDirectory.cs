using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GisServer.Host;

/// <summary>
/// The REST Services Directory — the browsable face of the API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every ArcGIS administrator navigates a server this way.</b> Typing
/// <c>/rest/services</c> into a browser and reading a list of folders and
/// services is how somebody finds out what a server has, and it is the first
/// thing they try. A server that answers only JSON is one they cannot browse —
/// technically complete and, in the moment they are exploring it, unusable.
/// </para>
/// <para>
/// <b>The same documents, rendered.</b> Nothing here computes anything: it takes
/// the objects the JSON endpoints already return and prints them. Two code paths
/// producing two answers to the same question is how the HTML view comes to
/// disagree with the API it describes, and the disagreement is always found by
/// somebody debugging something else.
/// </para>
/// <para>
/// <b>Every value is HTML-encoded, and that is the security of this file.</b>
/// Layer names, field names and file names are user input. A layer called
/// <c>&lt;script&gt;</c> rendered raw is stored XSS against the GIS
/// administrator — our most privileged user, and the exact threat
/// <see href="../../docs/security.md">security.md</see> names for uploaded
/// content. There is one <see cref="H"/> helper and nothing writes a value
/// without it.
/// </para>
/// </remarks>
internal static class RestDirectory
{
    /// <summary>Segments that name a service type rather than a resource.</summary>
    private static readonly HashSet<string> ServiceTypes = new(StringComparer.Ordinal)
        { "FeatureServer", "VectorTileServer", "GeometryServer", "MapServer" };


    /// <summary>Whether this request wants HTML rather than JSON.</summary>
    /// <param name="format">The <c>f</c> parameter, if any.</param>
    /// <param name="accept">The Accept header.</param>
    /// <returns>Whether to render HTML.</returns>
    /// <remarks>
    /// <b>An explicit <c>f</c> always wins, and a browser gets HTML by default.</b>
    /// That is ArcGIS's behaviour and the reason the directory is discoverable at
    /// all: nobody types <c>?f=html</c>. A client that sends no Accept header, or
    /// asks for JSON, gets JSON — so every existing caller is unaffected.
    /// </remarks>
    public static bool WantsHtml(string? format, string? accept)
    {
        if (!string.IsNullOrEmpty(format))
        {
            return format.Equals("html", StringComparison.OrdinalIgnoreCase);
        }

        return accept is not null
            && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A folder listing: its folders and its services.</summary>
    /// <param name="path">The request path, for the breadcrumb.</param>
    /// <param name="folder">The folder being listed, or null for the root.</param>
    /// <param name="version">The REST version to report.</param>
    /// <param name="folders">Folder names.</param>
    /// <param name="services">Service name and type pairs.</param>
    /// <returns>An HTML document.</returns>
    public static string Folder(
        string path,
        string? folder,
        double version,
        IEnumerable<string> folders,
        IEnumerable<(string Name, string Type)> services)
    {
        StringBuilder body = new();

        body.Append(CultureInfo.InvariantCulture, $"<h2>Folder: {H(folder ?? "/")}</h2>");
        body.Append(CultureInfo.InvariantCulture,
            $"<p><b>Current Version:</b> {version.ToString("0.00", CultureInfo.InvariantCulture)}</p>");

        List<string> folderList = [.. folders];

        if (folderList.Count > 0)
        {
            body.Append("<h3>Folders:</h3><ul>");

            foreach (string name in folderList)
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"<li><a href=\"/rest/services/{U(name)}\">{H(name)}</a></li>");
            }

            body.Append("</ul>");
        }

        body.Append("<h3>Services:</h3>");

        List<(string Name, string Type)> serviceList = [.. services];

        if (serviceList.Count == 0)
        {
            // Said rather than left blank. An empty list and a list that failed
            // to load look identical, and the first is a fact worth stating —
            // especially here, where sharing may be the reason.
            body.Append(
                "<p><i>No services, or none visible to you. A service you cannot read is not "
                + "listed, so this may look empty to one person and full to another.</i></p>");
        }
        else
        {
            body.Append("<ul>");

            foreach ((string name, string type) in serviceList)
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"<li><a href=\"/rest/services/{U(name)}/{U(type)}\">{H(name)}</a> "
                    + $"({H(type)})</li>");
            }

            body.Append("</ul>");
        }

        return Page(path, body.ToString());
    }

    /// <summary>
    /// A service or layer document, rendered from the object the API returns.
    /// </summary>
    /// <param name="path">The request path, for the breadcrumb.</param>
    /// <param name="title">The heading.</param>
    /// <param name="document">The same object the JSON endpoint serialises.</param>
    /// <param name="links">Extra links to offer, as label and href.</param>
    /// <returns>An HTML document.</returns>
    /// <remarks>
    /// <b>Rendered by walking the serialised JSON</b>, rather than by a template
    /// per document type. A template per type is four templates that drift; this
    /// one cannot show a field the API does not return, or miss one it does.
    /// </remarks>
    public static string Document(
        string path,
        string title,
        object document,
        IEnumerable<(string Label, string Href)>? links = null)
    {
        StringBuilder body = new();

        body.Append(CultureInfo.InvariantCulture, $"<h2>{H(title)}</h2>");

        if (links is not null)
        {
            List<(string Label, string Href)> list = [.. links];

            if (list.Count > 0)
            {
                body.Append("<p><b>View in:</b> ");
                body.Append(string.Join(" &middot; ", list.Select(l =>
                    $"<a href=\"{H(l.Href)}\">{H(l.Label)}</a>")));
                body.Append("</p>");
            }
        }

        using JsonDocument parsed = JsonDocument.Parse(JsonSerializer.Serialize(document));

        body.Append("<table class=\"props\">");
        Rows(body, parsed.RootElement, path);
        body.Append("</table>");

        return Page(path, body.ToString());
    }

    /// <summary>Walks a JSON object into table rows.</summary>
    private static void Rows(StringBuilder body, JsonElement element, string path)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<tr><th>{H(Humanise(property.Name))}</th><td>{Value(property.Value, path)}</td></tr>");
        }
    }

    /// <summary>Renders one value, following the shapes these documents use.</summary>
    private static string Value(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return "<i>none</i>";

            case JsonValueKind.True or JsonValueKind.False:
                return value.GetBoolean() ? "true" : "false";

            case JsonValueKind.Number or JsonValueKind.String:
                return H(value.ToString());

            case JsonValueKind.Array:
                if (value.GetArrayLength() == 0)
                {
                    return "<i>none</i>";
                }

                StringBuilder items = new("<ul>");

                foreach (JsonElement item in value.EnumerateArray())
                {
                    items.Append(CultureInfo.InvariantCulture, $"<li>{Value(item, path)}</li>");
                }

                return items.Append("</ul>").ToString();

            default:
                // A nested object — a field, an extent, a spatial reference.
                // Rendered inline as name: value pairs, because a nested table
                // per spatial reference makes a layer document unreadable.
                return string.Join(", ", value.EnumerateObject()
                    .Select(p => $"<b>{H(Humanise(p.Name))}</b>: {Value(p.Value, path)}"));
        }
    }

    /// <summary><c>maxRecordCount</c> becomes <c>Max Record Count</c>.</summary>
    private static string Humanise(string name)
    {
        StringBuilder pretty = new();

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                pretty.Append(' ');
            }

            pretty.Append(i == 0 ? char.ToUpperInvariant(name[i]) : name[i]);
        }

        return pretty.ToString();
    }

    /// <summary>The shell: breadcrumb, format links, and the styling.</summary>
    /// <remarks>
    /// <b>Deliberately plain, and deliberately not the console's design.</b> This
    /// is a directory somebody reads to find a URL, not a product surface. It
    /// also has to be legible when the only thing working is this page, which is
    /// an argument against anything it would need to load.
    /// </remarks>
    private static string Page(string path, string body)
    {
        StringBuilder crumbs = new("<a href=\"/rest/services\">Home</a>");
        string sofar = "";

        string[] parts = [.. path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)];

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];

            if (part is "rest")
            {
                continue;
            }

            sofar += "/" + part;

            // <b>The service name and its type are one crumb.</b> There is no
            // resource at .../parcels — only at .../parcels/FeatureServer — so
            // splitting them produces a breadcrumb link that 404s. Merging them
            // is also what an ArcGIS directory shows: "parcels (FeatureServer)".
            string label = part;

            if (i + 1 < parts.Length && ServiceTypes.Contains(parts[i + 1]))
            {
                label = $"{part} ({parts[i + 1]})";
                sofar += "/" + parts[i + 1];
                i++;
            }

            crumbs.Append(CultureInfo.InvariantCulture,
                $" &gt; <a href=\"/rest{sofar}\">{H(label)}</a>");
        }

        string json = path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "f=json";

        // Two dollars, so a single brace is CSS and a doubled one interpolates.
        // The alternative is escaping every brace in the stylesheet.
        return $$"""
            <title>GIS Server REST Services Directory</title>
            <style>
              body { font: 13px/1.55 "Segoe UI", system-ui, sans-serif; margin: 0; color: #1a1a1a;
                     background: #fff; }
              .bar { background: #f0f4f8; border-bottom: 1px solid #c8d4e0; padding: 7px 14px;
                     font-size: 12px; }
              .top { padding: 6px 14px; border-bottom: 1px solid #c8d4e0; font-weight: 600; }
              main { padding: 14px 20px 60px; max-width: 1100px; }
              h2 { font-size: 19px; margin: 14px 0 10px; }
              h3 { font-size: 14px; margin: 18px 0 6px; }
              ul { margin: 4px 0 4px 4px; padding-left: 20px; }
              li { margin: 2px 0; }
              a { color: #0a4a8a; }
              table.props { border-collapse: collapse; margin-top: 8px; }
              table.props th { text-align: left; vertical-align: top; padding: 3px 16px 3px 0;
                               font-weight: 600; white-space: nowrap; }
              table.props td { vertical-align: top; padding: 3px 0; }
              .fmt { font-size: 11px; padding: 5px 14px; }
              @media (prefers-color-scheme: dark) {
                body { background: #14181c; color: #e6e6e6; }
                .bar, .top { background: #1b2228; border-color: #2c363f; }
                a { color: #7bb6f0; }
              }
            </style>
            <div class="top">GIS Server REST Services Directory</div>
            <div class="bar">{{crumbs}}</div>
            <div class="fmt"><a href="{{H(json)}}">JSON</a></div>
            <main>{{body}}</main>
            """;
    }

    /// <summary>HTML-encodes a value. Nothing user-supplied is written without it.</summary>
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// URL-encodes a name that may already carry its folder.
    /// </summary>
    /// <remarks>
    /// <b>Segment by segment, because the slash is structure.</b> Encoding the
    /// whole string turned <c>Utilities/Geometry</c> into
    /// <c>Utilities%2FGeometry</c> and the link 404'd — the escape was correct
    /// for a segment and this is a path. Everything else still gets escaped, so
    /// a layer named with a <c>?</c> or a <c>#</c> cannot break out of its URL.
    /// </remarks>
    private static string U(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
}
