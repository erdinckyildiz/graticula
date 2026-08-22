using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Text.Json;

namespace Graticula.Host;

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


    /// <summary>
    /// The sign-in page, which is the only way a browser gets a session.
    /// </summary>
    /// <remarks>
    /// <b>The directory was anonymous-only until this existed.</b> A bearer
    /// header is not something a browser following a link can send, so every
    /// page showed a stranger and any service shared with the organisation was
    /// invisible — in the one surface built for browsing.
    /// </remarks>
    public static string SignIn(string returnTo, string? failed)
    {
        StringBuilder body = new();

        body.Append("<h1>Sign in</h1>");

        if (failed is not null)
        {
            // <b>A failed sign-in is a warning, not a hint.</b> `.hint` is the faint
            // grey this page uses for asides; the reason a sign-in did not work is the
            // most important sentence on the page at the moment it appears.
            body.Append(CultureInfo.InvariantCulture, $"<p class=\"warn\">{H(failed)}</p>");
        }

        string escapedReturn = H(returnTo);

        // No format provider: the whole expression is a concatenation and so
        // resolves to Append(string), which the culture overload cannot accept.
        /*
          <b>The two hidden inputs were inside the `<table>`, before its first row.</b> That
          is invalid, and it has worked for as long as it has existed because every browser
          hoists stray content out of a table and into the form around it. It is moved
          rather than left, because *it happens to survive parsing* is not a reason.

          <b>Each field has an `id` and a real `<label for>`.</b> A `<th>` holding the text
          *Name:* beside a `<td>` holding the input is a visual pairing; whether assistive
          technology reads it as a label depends on the inference each combination happens to
          make. Sign-in is the only way a browser gets a session, so this is the last page on
          which to leave two edit fields that might announce as unlabelled. It renders
          identically — a label around plain text looks like the plain text it replaced — and
          `QueryPage` already uses `<label>` correctly for its radio pairs, so this applies a
          pattern the codebase has rather than inventing one.
        */
        body.Append(
            "<form action=\"/rest/auth/login\" method=\"post\">"
            + "<input type=\"hidden\" name=\"return\" value=\"" + escapedReturn + "\">"
            + "<input type=\"hidden\" name=\"f\" value=\"html\">"
            + "<table class=\"form\">"
            + "<tr><th><label for=\"signin-name\">Name</label></th>"
            + "<td><input id=\"signin-name\" type=\"text\" name=\"name\" size=\"26\" "
            + "autocomplete=\"username\" autofocus></td></tr>"
            + "<tr><th><label for=\"signin-password\">Password</label></th>"
            + "<td><input id=\"signin-password\" type=\"password\" name=\"password\" "
            + "size=\"26\" autocomplete=\"current-password\"></td></tr>"
            + "<tr><th></th><td><button type=\"submit\">Sign in</button></td></tr>"
            + "</table></form>");

        body.Append(
            "<p class=\"hint\">Signing in stores a cookie that authenticates <b>reading</b> only. "
            + "Publishing, editing and administration still need an "
            + "<code>Authorization: Bearer</code> header, so nothing a browser is tricked into "
            + "sending can change anything here.</p>");

        return Page("/rest/login", body.ToString());
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

        /*
          <b>The folder's own name is the page's title, and the version is a subtitle.</b>
          This read `Folder: hosted` in a heading and then `Current Version: 10.81` as a
          bold-colon paragraph — the shape a page takes when its fields are printed in the
          order the object happens to hold them. The name is what the reader came for; the
          version is a fact about the server that they will read once.

          <b>`h1`, not `h2`.</b> There was no `h1` anywhere on this page: the masthead is a
          header, not a heading, and every renderer opened at `h2`. A page with no top-level
          heading and a skipped level is the first thing an automated audit reports and the
          thing that breaks heading navigation for a screen-reader user.
        */
        // No format provider: a conditional whose branches are an interpolated string and a
        // plain one resolves to `string`, which the culture overload cannot take.
        body.Append(folder is { Length: > 0 }
            ? $"<h1><code>{H(folder)}</code></h1>"
            : "<h1>Services</h1>");

        body.Append(CultureInfo.InvariantCulture,
            $"<p class=\"sub\">REST services directory · version "
            + $"{version.ToString("0.00", CultureInfo.InvariantCulture)}</p>");

        List<string> folderList = [.. folders];

        if (folderList.Count > 0)
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<h3>Folders<span class=\"count\">{folderList.Count}</span></h3>");

            body.Append("<ul class=\"cards\">");

            foreach (string name in folderList)
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"<li><a class=\"row\" href=\"/rest/services/{U(name)}\">"
                    + $"<span class=\"id\">{H(name)}</span></a></li>");
            }

            body.Append("</ul>");
        }

        List<(string Name, string Type)> serviceList = [.. services];

        if (serviceList.Count == 0)
        {
            // Said rather than left blank. An empty list and a list that failed
            // to load look identical, and the first is a fact worth stating —
            // especially here, where sharing may be the reason.
            body.Append(
                "<h3>Services<span class=\"count\">0</span></h3><div class=\"empty\">"
                + "<p>No services, or none visible to you. A service you cannot read is not "
                + "listed, so this may look empty to one person and full to another.</p>");

            // <b>And, if they are nobody, tell them what to do about it.</b> The
            // sentence above is honest and useless: the owner read it twice and
            // asked twice why the geometry service was missing, because knowing
            // that sharing *might* be the reason does not tell you that you are
            // the one who is not signed in.
            //
            // <b>This leaks nothing, and the condition is what makes that
            // true.</b> It depends on the caller being anonymous, not on
            // anything being hidden — an empty folder with nothing behind it
            // shows the same words. Saying "there are 2 services you cannot see"
            // would be the disclosure the §66 security gate refused on
            // /admin/health, arriving by a different door.
            if (Current.Value is not { Length: > 0 })
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"<p class=\"hint\">You are not signed in. Services shared with the "
                    + $"organisation are not listed to a stranger — "
                    + $"<a href=\"/rest/login?return={U(path)}\">sign in</a> and look again.</p>");
            }

            body.Append("</div>");
        }
        else
        {
            /*
              <b>One row per service, with its faces beside it, instead of one row per
              face.</b> Measured on this server's `hosted` folder before it was changed: 36
              rows for 14 distinct services, so 22 of them carried a name already on the
              page. `tr_il` appeared three times — FeatureServer, MapServer,
              VectorTileServer — with nothing saying they were one dataset seen three ways.
              A review timed the consequence: the vector tile service for `tr_il` was row 25
              of 32, and the browser's own find-in-page could not isolate it either, because
              searching *tr_il* matches `tr_ilce` as well.

              <b>The folder prefix is dropped from the label.</b> Every row began with
              `hosted/` — the folder named in the heading directly above — so all 36 shared
              their first eight characters before anything distinguishing appeared, and
              `tr_il` and `tr_ilce` differ at character fourteen.

              <b>`?f=json` is untouched, and that is the whole reason this is safe.</b> One
              entry per face is what an ArcGIS client reads and it is a contract; this is the
              page a person reads. The URLs are the same URLs — nothing here invents a link
              that did not exist.

              <b>Insertion order is kept.</b> The caller sorts, and re-sorting here would
              mean two orderings to keep in step.
            */
            List<string> order = [];
            Dictionary<string, List<string>> faces = new(StringComparer.Ordinal);

            foreach ((string name, string type) in serviceList)
            {
                if (!faces.TryGetValue(name, out List<string>? kinds))
                {
                    kinds = [];
                    faces[name] = kinds;
                    order.Add(name);
                }

                kinds.Add(type);
            }

            body.Append(CultureInfo.InvariantCulture,
                $"<h3>Services<span class=\"count\">{order.Count}</span></h3>");

            body.Append("<ul class=\"cards\">");

            string prefix = folder is { Length: > 0 } ? folder + "/" : string.Empty;

            foreach (string name in order)
            {
                List<string> kinds = faces[name];

                // The folder is already the heading of this page; what is left is the
                // part that distinguishes one row from the next.
                string shown = prefix.Length > 0
                    && name.StartsWith(prefix, StringComparison.Ordinal)
                        ? name[prefix.Length..]
                        : name;

                body.Append(CultureInfo.InvariantCulture,
                    $"<li><a class=\"row\" href=\"/rest/services/{U(name)}/{U(kinds[0])}\">"
                    + $"<span class=\"id\">{H(shown)}</span>");

                // <b>One face is named on the row; several become links.</b> A row that
                // said "(FeatureServer)" and nothing else for a service with three faces
                // was the omission that made the same name appear three times.
                // <b>One face is a chip too, and the reason is that it sits next to three
                // of them.</b> A single-face card drew its type as small grey text inside the
                // link, so a row of cards mixed two shapes: some ending in a line of chips
                // and some in a caption, with visible dead space under the shorter one. Same
                // structure for one as for three, and the grid stops looking sorted by
                // accident.
                body.Append("</a><span class=\"also\">");

                foreach (string kind in kinds)
                {
                    body.Append(CultureInfo.InvariantCulture,
                        $"<a href=\"/rest/services/{U(name)}/{U(kind)}\">{H(kind)}</a>");
                }

                body.Append("</span></li>");
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
    /// <param name="linksLabel">
    /// What those links are, without the colon. A service's links are its
    /// <em>Layers</em> and a layer's are things to <em>View in</em>, and calling
    /// both the same thing makes a service page read as though its layers were
    /// alternative renderings of it.
    /// </param>
    /// <param name="tree">
    /// A layer tree, depth-first, with one entry per layer and group and its
    /// depth. Rendered as nested lists above the property table.
    /// </param>
    /// <param name="formats">
    /// Other representations of this same resource, offered beside <c>JSON</c> on
    /// the format line — a WFS document for a feature service, for instance.
    /// </param>
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
        IEnumerable<(string Label, string Href)>? links = null,
        string linksLabel = "View in",
        IEnumerable<(string Label, string Href, int Depth)>? tree = null,
        IEnumerable<(string Label, string Href)>? formats = null)
    {
        StringBuilder body = new();

        body.Append(CultureInfo.InvariantCulture, $"<h1>{H(title)}</h1>");

        if (links is not null)
        {
            List<(string Label, string Href)> list = [.. links];

            if (list.Count > 0)
            {
                body.Append(CultureInfo.InvariantCulture, $"<p><b>{H(linksLabel)}:</b> ");
                body.Append(string.Join(" &middot; ", list.Select(l =>
                    $"<a href=\"{H(l.Href)}\">{H(l.Label)}</a>")));
                body.Append("</p>");
            }
        }

        if (tree is not null)
        {
            AppendTree(body, [.. tree]);
        }

        using JsonDocument parsed = JsonDocument.Parse(JsonSerializer.Serialize(document));

        body.Append("<table class=\"props\">");
        Rows(body, parsed.RootElement, path);
        body.Append("</table>");

        return Page(path, body.ToString(), close: true, formats);
    }

    /// <summary>
    /// A layer tree as nested lists, one level of indent per depth.
    /// </summary>
    /// <remarks>
    /// <b>Nested, because that is the whole point of a group layer.</b> The
    /// service document carries the tree as <c>parentLayerId</c> and
    /// <c>subLayerIds</c> on a flat array, which a client can rebuild and a
    /// person cannot. Rendering it flat here would show the structure exists and
    /// hide what it is.
    /// </remarks>
    private static void AppendTree(
        StringBuilder body, List<(string Label, string Href, int Depth)> entries)
    {
        if (entries.Count == 0)
        {
            body.Append("<h3>Layers:</h3><p><i>None yet.</i></p>");
            return;
        }

        body.Append("<h3>Layers:</h3><ul>");

        int depth = 0;
        bool open = false;

        foreach ((string label, string href, int at) in entries)
        {
            if (at > depth)
            {
                // <b>The child list goes inside its parent's still-open
                // &lt;li&gt;, not beside it.</b> A &lt;ul&gt; as a sibling of
                // the &lt;li&gt; it belongs to is invalid, and browsers render
                // it close enough to right that the mistake survives being
                // looked at — until a screen reader reads the tree flat.
                body.Append("<ul>");
                depth = at;
            }
            else
            {
                if (open)
                {
                    body.Append("</li>");
                    open = false;
                }

                while (depth > at)
                {
                    body.Append("</ul></li>");
                    depth--;
                }
            }

            body.Append(CultureInfo.InvariantCulture,
                $"<li><a href=\"{H(href)}\">{H(label)}</a>");

            open = true;
        }

        if (open)
        {
            body.Append("</li>");
        }

        while (depth > 0)
        {
            body.Append("</ul></li>");
            depth--;
        }

        body.Append("</ul>");
    }

    /// <summary>Walks a JSON object into table rows.</summary>
    private static void Rows(StringBuilder body, JsonElement element, string path)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            /*
              <b>Fields get a real table, and they are the reason anybody opens this
              page.</b> Owner request 2026-08-15: *"we need the fields also be shown"*.
              Rendered inline as name-colon-value pairs they were technically present and
              unreadable — a wall of prose where a person is trying to find one column's
              type before writing a query.

              <b>Any array of objects, not a list of two field names.</b> The rule was
              `fields` or `layers` by name, so every other array of like-shaped objects fell
              through to a bulleted list. The worst of them is an ImageServer's `lods`: 21 to
              24 zoom levels, each a bullet carrying a full-precision resolution and scale,
              550 to 650 pixels of page — more than every other field on the document put
              together, sitting in the middle of the table and pushing the load-bearing ones
              below the fold. Deciding by shape rather than by name fixes that one and every
              future one without a list to maintain.
            */
            string rendered = property.Value.ValueKind == JsonValueKind.Array
                    && property.Value.GetArrayLength() > 0
                    && property.Value[0].ValueKind == JsonValueKind.Object
                ? Grid(property.Value, path)
                : Value(property.Value, path);

            /*
              <b>A grid is its own row, spanning both columns, and that is a measured fix
              rather than a preference.</b> Nested in the value column of `table.props`, the
              grid's natural width became the width of that column for *every* row, because
              an HTML table sizes a column to its widest content anywhere in the table. A
              layer with three short field names already made `table.props` 761 pixels wide
              inside a 380-pixel `main`, so the whole page scrolled sideways rather than the
              grid scrolling inside its own box — which is exactly what `.scroll` exists to
              prevent, defeated by where it was put. Today's demo layers are too narrow to
              trip it at 900 pixels; a layer with eight realistically-named columns is not.
            */
            // <b>Decided by what was rendered, not by the shape of the property.</b> The
            // first version asked whether *this* property was an array of objects, which
            // missed an ImageServer's `tileInfo`: the grid is its nested `lods`, so the row
            // stayed a normal one and 24 zoom levels were squeezed into a 76-pixel sliver
            // of the value column at 600px. Asking whether the rendered value contains a
            // grid catches every depth, including the ones nobody has written yet.
            if (rendered.Contains("class=\"scroll\"", StringComparison.Ordinal))
            {
                body.Append(CultureInfo.InvariantCulture,
                    $"<tr class=\"wide\"><th colspan=\"2\">"
                    + $"{H(Humanise(property.Name))}</th></tr>"
                    + $"<tr class=\"wide\"><td colspan=\"2\">{rendered}</td></tr>");

                continue;
            }

            body.Append(CultureInfo.InvariantCulture,
                $"<tr><th>{H(Humanise(property.Name))}</th><td>{rendered}</td></tr>");
        }
    }

    /// <summary>
    /// An array of like-shaped objects as a table, one row each.
    /// </summary>
    /// <remarks>
    /// <b>The columns are the union of the keys, in first-seen order.</b> Taking
    /// the first element's keys would silently drop a column that only some
    /// fields carry — <c>domain</c> and <c>length</c> are exactly that — and
    /// dropping it is invisible, which is the worst way for a directory to be
    /// wrong.
    /// </remarks>
    private static string Grid(JsonElement array, string path)
    {
        List<string> columns = [];

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                // Not a uniform array of objects, so a table would misrepresent
                // it. Fall back rather than force it.
                return Value(array, path);
            }

            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (!columns.Contains(property.Name, StringComparer.Ordinal))
                {
                    columns.Add(property.Name);
                }
            }
        }

        StringBuilder grid = new("<div class=\"scroll\"><table class=\"grid\"><thead><tr>");

        foreach (string column in columns)
        {
            grid.Append(
                CultureInfo.InvariantCulture,
                $"<th scope=\"col\">{H(Humanise(column))}</th>");
        }

        grid.Append("</tr></thead><tbody>");

        foreach (JsonElement item in array.EnumerateArray())
        {
            grid.Append("<tr>");

            foreach (string column in columns)
            {
                grid.Append(CultureInfo.InvariantCulture,
                    $"<td>{(item.TryGetProperty(column, out JsonElement cell)
                        ? Value(cell, path)
                        : "<i>—</i>")}</td>");
            }

            grid.Append("</tr>");
        }

        return grid.Append("</tbody></table></div>").ToString();
    }

    /// <summary>Renders one value, following the shapes these documents use.</summary>
    /// <summary>A number, printed the way a person reads it.</summary>
    /// <param name="value">The number as it arrived.</param>
    /// <returns>Its text.</returns>
    /// <remarks>
    /// <para>
    /// <b>`0.010000000000000009` is what an extent divided by a pixel count looks like, and
    /// printing it says *this measurement is precise to eighteen digits*.</b> It is not: it
    /// is 0.01, arrived at by division. Every number on these pages went through
    /// `value.ToString()` untouched, so a service document showed that, and a scale of
    /// `36978595.474481836` twenty-two times over.
    /// </para>
    /// <para>
    /// <b>Integers are left exactly alone and given separators; only the fractional ones are
    /// rounded.</b> An id, a pixel count, a wkid and a byte count are exact and must stay
    /// exact — rounding 102100 would be a different reference. Twelve significant figures is
    /// far more than any georeference carries and still short of where double's noise starts.
    /// </para>
    /// <para>
    /// <b>The console rounds the same numbers to six figures and this rounds to twelve, and
    /// that difference is deliberate.</b> The console is showing an operator a summary; this
    /// is the document a developer copies a value out of, so it errs towards keeping what is
    /// really there.
    /// </para>
    /// </remarks>
    private static string Number(JsonElement value)
    {
        if (value.TryGetInt64(out long whole))
        {
            return whole.ToString("N0", CultureInfo.InvariantCulture);
        }

        if (!value.TryGetDouble(out double real) || !double.IsFinite(real))
        {
            return value.ToString();
        }

        return Math.Round(real, 12).ToString("0.############", CultureInfo.InvariantCulture);
    }

    private static string Value(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return "<i>none</i>";

            case JsonValueKind.True or JsonValueKind.False:
                // <b>A column of a dozen capability flags is read by pattern, not word by
                // word.</b> Plain lowercase `true`/`false` gives the eye nothing to catch,
                // and these documents carry ten or more of them.
                return value.GetBoolean()
                    ? "<span class=\"yes\">yes</span>"
                    : "<span class=\"no\">no</span>";

            case JsonValueKind.Number:
                return $"<span class=\"v\">{H(Number(value))}</span>";

            case JsonValueKind.String:
                return H(value.ToString());

            case JsonValueKind.Array:
                if (value.GetArrayLength() == 0)
                {
                    return "<i>none</i>";
                }

                // <b>An array of like-shaped objects is a table wherever it sits, not only
                // at the top of the document.</b> `Rows` handles the top level; `lods` lives
                // one level down inside `tileInfo`, which is why 24 zoom levels were still
                // 24 bullets after the top-level rule was written. Deciding here catches
                // every depth.
                if (value[0].ValueKind == JsonValueKind.Object)
                {
                    return Grid(value, path);
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

    /// <summary>
    /// The shell with <c>&lt;main&gt;</c> left open, for a page that streams.
    /// </summary>
    /// <param name="path">The request path, for the breadcrumb.</param>
    /// <param name="body">What goes at the top, before the streamed part.</param>
    /// <returns>An unfinished HTML document. The caller closes it.</returns>
    /// <remarks>
    /// <b>Shared so the query page cannot drift from the directory it sits
    /// in.</b> Two shells would be two stylesheets and two breadcrumbs, and the
    /// query page would slowly stop looking like the pages that link to it.
    /// </remarks>
    public static string OpenPage(string path, string body) => Page(path, body, close: false);

    /// <summary>The shell: breadcrumb, format links, and the styling.</summary>
    /// <remarks>
    /// <b>Deliberately plain, and deliberately not the console's design.</b> This
    /// is a directory somebody reads to find a URL, not a product surface. It
    /// also has to be legible when the only thing working is this page, which is
    /// an argument against anything it would need to load.
    /// </remarks>
    /// <summary>Who the current request is, for the banner.</summary>
    /// <remarks>
    /// <para>
    /// <b>Set per request rather than passed through every renderer.</b> Every
    /// page wants it and no page's content depends on it, so threading it
    /// through eight signatures would be ceremony.
    /// </para>
    /// <para>
    /// <b>This was <c>[ThreadStatic]</c> until 2026-08-15, and that was wrong in
    /// a way that only concurrency shows.</b> An ASP.NET Core request does not
    /// stay on one thread: it resumes on whichever thread the pool hands it
    /// after an await. So the name set by the middleware was frequently invisible
    /// by the time the page rendered — the banner said <em>Sign in</em> to
    /// somebody who was signed in — and, worse, a thread carrying a leftover name
    /// could render it into another request's page. That is one browsing user's
    /// name shown to another, which is a disclosure and not a cosmetic bug.
    /// </para>
    /// <para>
    /// <b><see cref="AsyncLocal{T}"/> is the fix, because it flows with the
    /// request rather than with the thread.</b> It was found by a conformance
    /// test that passed alone and failed in the full run; the flake was the
    /// symptom and the leak was the defect.
    /// </para>
    /// </remarks>
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>Records who is browsing, for the pages rendered after it.</summary>
    /// <param name="name">Their name, or null for anonymous.</param>
    public static void SignedInAs(string? name) => Current.Value = name;

    /// <summary>
    /// Renders one directory page.
    /// </summary>
    /// <param name="path">The path being answered, which becomes the breadcrumb.</param>
    /// <param name="body">The page's content.</param>
    /// <param name="close">Whether to close <c>main</c>, or leave it open for a caller that appends.</param>
    /// <param name="formats">
    /// <para>
    /// Other representations of this same resource, offered beside <c>JSON</c> on the
    /// format line. <b>Added 2026-08-20 because a second protocol had been built and
    /// nothing in the directory said so.</b> An ArcGIS Server directory prints
    /// <c>JSON | SOAP | WMS | WFS</c> there, and that line is how a person discovers
    /// that the service they are looking at is reachable another way. This server had
    /// spoken WFS for a day and the word did not appear on any service page.
    /// </para>
    /// <para>
    /// <b>The caller supplies them rather than this deriving them from the path</b>,
    /// because the WFS name of a layer is the layer's own name and only the caller has
    /// it. Deriving it from the URL would mean guessing that the ArcGIS layer id and
    /// the WFS type name are the same thing, which they are not.
    /// </para>
    /// </param>
    /// <returns>The page.</returns>
    private static string Page(
        string path,
        string body,
        bool close = true,
        IEnumerable<(string Label, string Href)>? formats = null)
    {
        /*
          <b>An ordered list, and the last crumb is not a link to itself.</b> This built a
          run of anchors joined by a literal `&gt;`, opening with a *Home* that pointed at
          `/rest/services` — and then the first real crumb was *services*, pointing at
          `/rest/services` as well. Two crumbs, one destination, on every folder page. The
          reader was given a choice between two identical doors.

          <b>*Home* is gone rather than repointed.</b> There is nothing above the services
          directory to go to: `/` redirects here. A crumb that only ever leads where the
          next crumb leads is not orientation, it is furniture.

          <b>The current page carries `aria-current="page"` and no href.</b> A breadcrumb
          whose last item links to the page you are on tells a screen-reader user there is
          somewhere else to go, and gives a keyboard user a stop on the way to the content.
        */
        List<(string Label, string? Href)> trail = [];
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

            trail.Add((label, "/rest" + sofar));
        }

        StringBuilder crumbs = new();

        for (int i = 0; i < trail.Count; i++)
        {
            (string label, string? href) = trail[i];
            bool last = i == trail.Count - 1;

            crumbs.Append(last
                ? $"<li aria-current=\"page\">{H(label)}</li>"
                : $"<li><a href=\"{H(href)}\">{H(label)}</a></li>");
        }

        string json = path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "f=json";

        StringBuilder faces = new(
            $"<a href=\"{H(json)}\">JSON</a>");

        foreach ((string Label, string Href) face in formats ?? [])
        {
            // <b>No separator.</b> These were joined by " | " when they were bare links
            // on a line of their own; they are bordered chips in a flex row with a gap
            // now, so the pipe is a second separator drawn on top of the first.
            faces.Append(CultureInfo.InvariantCulture,
                $"<a href=\"{H(face.Href)}\">{H(face.Label)}</a>");
        }

        // Two dollars, so a single brace is CSS and a doubled one interpolates.
        // The alternative is escaping every brace in the stylesheet.
        /*
          <b>The project's own tokens, not a third palette.</b> These are `console.css`'s
          values: `--ink`, `--muted`, `--faint`, `--accent` and the rules. They were chosen
          by the owner, they carry their contrast measurements in that file's own comments,
          and a directory page inventing a fourth blue is how a product comes to look like
          four products. What is *not* borrowed is the console's navy rail: this page has one
          surface, so it needs one family of ink.

          <b>Restyled 2026-08-22 because the owner said it plainly — *estetikten uzak*.</b>
          The page it replaced was three stacked strips at three different paddings, bulleted
          lists, bold-colon labels and full-bleed text at 13px. Every criticism was correct.
          The structure did not change: breadcrumb, folders, services, a link to the JSON. A
          developer who knows this kind of directory still knows where they are.

          <b>Dark is defined once, as tokens.</b> The old dark block redefined six rules and
          left the rest, which is why `.warn` — the banner an operation shows when it refuses
          — was `#e6e6e6` text on a `#fff4e5` background: 1.15:1, a cream card with no visible
          words in it, exactly when the reader most needed them. Tokens make that class of
          bug unavailable rather than fixed.
        */
        // Two dollars, so a single brace is CSS and a doubled one interpolates.
        // The alternative is escaping every brace in the stylesheet.
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Graticula REST Services Directory</title>
            <style>
              :root {
                --paper: #f6f8fa; --panel: #fff;
                --ink: #101826; --muted: #566275; --faint: #616b7a;
                --rule: #e3e8ef; --rule-strong: #cfd7e3;
                --accent: #0d7d70; --accent-soft: #e3f2f0; --accent-line: #b8ded8;
                --warn: #8a5a08; --warn-soft: #fdf2e0; --warn-line: #e8d3a8;
                --mono: ui-monospace, "Cascadia Mono", Consolas, monospace;
                --sans: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
                color-scheme: light dark;
                /* Checkboxes and radios were browser-blue on a teal page — the one place
                   the palette did not reach, because nothing had told the browser. */
                accent-color: var(--accent);
              }
              @media (prefers-color-scheme: dark) {
                :root {
                  --paper: #10151b; --panel: #161c24;
                  --ink: #e7ecf2; --muted: #9aa7b6; --faint: #8b98a8;
                  --rule: #263039; --rule-strong: #35424e;
                  --accent: #58cfc0; --accent-soft: #14312e; --accent-line: #2a5a54;
                  --warn: #f0c47a; --warn-soft: #2b2113; --warn-line: #5c4622;
                }
              }

              *, *::before, *::after { box-sizing: border-box; }

              body { margin: 0; background: var(--paper); color: var(--ink);
                     font: 15px/1.6 var(--sans);
                     -webkit-font-smoothing: antialiased; }

              a { color: var(--accent); text-decoration-thickness: 1px;
                  text-underline-offset: 2px; }
              a:hover { text-decoration-thickness: 2px; }

              /* One ring, one colour, everywhere something can be focused. There was
                 no focus style at all before this. */
              a:focus-visible, button:focus-visible, input:focus-visible,
              select:focus-visible, textarea:focus-visible, summary:focus-visible {
                outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 3px;
              }

              /* <b>Two bands, not three.</b> The masthead, then one row carrying the
                 breadcrumb on the left and the data formats on the right — they answer
                 *where am I* and *how do I get this*, and stacking them separately spent
                 a third of the viewport's first inch on chrome. */
              header.top { display: flex; flex-wrap: wrap; gap: 4px 16px;
                           align-items: baseline; justify-content: space-between;
                           padding: 14px 24px; background: var(--panel);
                           border-bottom: 1px solid var(--rule); }
              header.top .name { font-size: 15px; font-weight: 620; letter-spacing: -0.1px; }
              header.top .name span { color: var(--faint); font-weight: 450; }
              /*
                <b>Plain inline text, and it was `inline-flex` for a day.</b> A flex container
                turns each bare text run into an anonymous flex item, and an anonymous item's
                leading and trailing whitespace collapses to nothing — so every word in the
                banner ran into the one beside it, on every page, in both themes, with the
                spaces present in the HTML and zero pixels wide on screen. The flex was there
                to align the sign-out button's baseline, which `vertical-align` does without
                blockifying anything around it.

                <b>And the first version of this note quoted the broken banner verbatim, which
                broke a test.</b> This stylesheet is served inside the page, so its prose is
                page content: a conformance test asserting that an anonymous reader is never
                told somebody is signed in found the phrase in the comment explaining why they
                had been. Prose that ships has to be written as though it ships.
              */
              .who { font-size: 13px; color: var(--muted); }
              /* Sign out is a form because signing out is a POST; it should still read
                 as the line of text it sits in. */
              .who form.out { display: inline; margin: 0; }
              .who form.out button { background: none; border: 0; padding: 0;
                                     margin: 0; font: inherit; color: var(--accent);
                                     text-decoration: underline;
                                     text-underline-offset: 2px; cursor: pointer;
                                     vertical-align: baseline; }
              .who form.out button:hover { text-decoration-thickness: 2px;
                                           filter: none; }
              .who b { color: var(--ink); font-weight: 600; }

              .bar { display: flex; flex-wrap: wrap; gap: 4px 16px;
                     align-items: baseline; justify-content: space-between;
                     padding: 9px 24px; background: var(--paper);
                     border-bottom: 1px solid var(--rule); font-size: 13px; }
              .bar ol { display: flex; flex-wrap: wrap; align-items: baseline;
                        gap: 0 6px; margin: 0; padding: 0; list-style: none; }
              .bar li + li::before { content: "/"; color: var(--rule-strong);
                                     margin-right: 6px; }
              .bar li[aria-current] { color: var(--muted); }
              .fmt { display: flex; gap: 10px; font-size: 12px; }
              .fmt a { font-family: var(--mono); text-transform: uppercase;
                       letter-spacing: 0.4px; text-decoration: none;
                       border: 1px solid var(--accent-line); border-radius: 5px;
                       padding: 2px 8px; background: var(--accent-soft); }
              .fmt a:hover { border-color: var(--accent); }

              main { padding: 28px 24px 72px; max-width: 76rem; margin: 0 auto; }

              h1 { font-size: 26px; line-height: 1.25; font-weight: 640;
                   letter-spacing: -0.4px; margin: 0 0 4px; }
              h1 code { font-family: var(--mono); font-size: 0.82em; font-weight: 600; }
              h2 { font-size: 20px; font-weight: 620; letter-spacing: -0.2px;
                   margin: 32px 0 10px; }
              h3 { font-size: 13px; font-weight: 650; text-transform: uppercase;
                   letter-spacing: 0.6px; color: var(--muted);
                   margin: 26px 0 10px; padding-bottom: 6px;
                   border-bottom: 1px solid var(--rule); }
              h3 .count { float: right; font-weight: 500; letter-spacing: 0;
                          text-transform: none; color: var(--faint);
                          font-family: var(--mono); }

              .sub { color: var(--muted); font-size: 13px; margin: 0 0 24px; }
              .sub code { font-family: var(--mono); }

              /* Folders and services: a responsive grid of cards rather than bullets.
                 A list of 36 identifiers is a thing to scan, and a bulleted column of
                 them is the one shape that makes scanning hardest. */
              ul.cards { list-style: none; margin: 0; padding: 0;
                         display: grid; gap: 8px;
                         grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr)); }
              ul.cards li { background: var(--panel); border: 1px solid var(--rule);
                            border-radius: 7px; }
              ul.cards li:hover { border-color: var(--accent-line); }
              ul.cards a.row { display: block; padding: 9px 12px;
                               text-decoration: none; color: var(--ink); }
              ul.cards a.row:hover { color: var(--accent); }
              ul.cards .id { font-family: var(--mono); font-size: 13.5px;
                             font-weight: 600; word-break: break-word; }
              ul.cards .also { display: flex; flex-wrap: wrap; gap: 6px;
                               padding: 0 12px 9px; font-size: 11.5px; }
              ul.cards .also a { font-family: var(--mono); text-decoration: none;
                                 color: var(--muted); border: 1px solid var(--rule);
                                 border-radius: 4px; padding: 1px 6px; }
              ul.cards .also a:hover { color: var(--accent);
                                       border-color: var(--accent-line); }

              /* A property table, which is what a service document is. */
              table.props { border-collapse: collapse; width: 100%;
                            background: var(--panel); border: 1px solid var(--rule);
                            border-radius: 7px; overflow: hidden; }
              table.props th { text-align: left; vertical-align: top; font-weight: 550;
                               color: var(--muted); font-size: 13px;
                               padding: 7px 16px 7px 14px; white-space: nowrap;
                               width: 1%; border-bottom: 1px solid var(--rule); }
              table.props td { vertical-align: top; padding: 7px 14px 7px 0;
                               font-size: 13.5px; border-bottom: 1px solid var(--rule); }
              table.props tr:last-child th, table.props tr:last-child td {
                border-bottom: 0; }
              table.props code, table.props .v { font-family: var(--mono); }
              /* A spanning row: the label sits above its grid rather than beside it, and
                 the grid is measured against the page instead of against a shared column. */
              table.props tr.wide th { width: auto; padding-top: 14px;
                                       font-size: 12px; font-weight: 650;
                                       text-transform: uppercase; letter-spacing: 0.6px;
                                       border-bottom: 0; }
              table.props tr.wide td { padding: 0 14px 12px; }
              table.props tr.wide td .scroll { max-width: 100%; }
              /*
                <b>Fixed layout, because content must not be able to widen the page.</b> A
                table sizes a column to its widest content anywhere in it, so one long
                extent, one nested grid or one unbroken URL made the value column that wide
                for every row — and the whole page scrolled sideways rather than the wide
                thing scrolling in its own box. Measured before this: a three-field layer
                made the table 761px inside a 380px main.
                <b>`overflow-wrap: anywhere` is the other half.</b> Fixed layout without it
                clips instead of wrapping, which trades a scrollbar for silent truncation —
                the worse of the two, because nothing says anything is missing.
              */
              table.props { table-layout: fixed; }
              /*
                <b>A share of the table, not 15rem of it.</b> A fixed label column does not
                shrink, so at 380px it left the value column narrower than the words in it
                and *Advanced Query Capabilities* came out as a fifteen-line ladder of
                single words. A percentage under `table-layout: fixed` is honoured exactly,
                and the floor stops the label itself becoming the ladder.
              */
              table.props th { width: 30%; min-width: 7rem; }
              table.props td { overflow-wrap: anywhere; }

              /* A field list is a table, and a wide one scrolls in its own box
                 rather than pushing the page sideways. */
              /*
                <b>A height, because without one the sticky header below was decorative.</b>
                `overflow-x: auto` computes `overflow-y: auto` as well, which makes this box
                the sticky containing block instead of the viewport — and a box with no
                height never scrolls internally, so the header had nothing to stick to.
                Measured on a 1000-row result 36,094px tall: scrolled to the middle, no
                header. Capping the height makes the box scroll and the header stick, which
                is what was claimed.
              */
              .scroll { overflow: auto; max-width: 100%; max-height: 70vh;
                        border: 1px solid var(--rule); border-radius: 7px;
                        background: var(--panel); }
              table.grid { border-collapse: collapse; font-size: 13px; width: 100%; }
              table.grid th, table.grid td { padding: 6px 12px; text-align: left;
                                             vertical-align: top; white-space: nowrap;
                                             border-bottom: 1px solid var(--rule); }
              table.grid thead th { background: var(--paper); font-weight: 600;
                                    color: var(--muted); position: sticky; top: 0;
                                    border-bottom: 1px solid var(--rule-strong); }
              table.grid tbody tr:last-child td { border-bottom: 0; }
              table.grid tbody tr:hover td { background: var(--accent-soft); }

              /* The query form. Labels above their inputs at narrow widths and beside
                 them when there is room, so a column of boxes reads as one control. */
              table.form { border-collapse: collapse; max-width: 100%; }
              table.form th { text-align: right; font-weight: 550; color: var(--muted);
                              font-size: 13px; padding: 5px 12px 5px 0;
                              white-space: nowrap; vertical-align: top; }
              table.form td { padding: 5px 0; }
              table.form input, table.form select, table.form textarea { max-width: 100%; }
              /*
                <b>Labels above their inputs when there is no room beside them.</b> The query
                form measured 842px inside a 380px viewport and clipped its text mid-word —
                the same failure `table.props` had, in the one page the structural pass
                missed. A label-beside-input table has a floor of *widest label plus widest
                input*, and below that the only honest move is to stop putting them side by
                side.
              */
              @media (max-width: 640px) {
                table.form th, table.form td { display: block; text-align: left;
                                               white-space: normal; padding: 2px 0; }
                table.form th { padding-top: 8px; }
                table.form textarea { min-width: 0; width: 100%; }
              }
              input, select, textarea, button {
                font: inherit; color: var(--ink); background: var(--panel);
                border: 1px solid var(--rule-strong); border-radius: 5px;
                padding: 5px 8px;
              }
              textarea { font: 13px/1.5 var(--mono); min-width: 22rem; }
              button { background: var(--accent); border-color: var(--accent);
                       color: #fff; font-weight: 550; padding: 6px 16px;
                       cursor: pointer; margin-top: 8px; }
              button:hover { filter: brightness(1.08); }

              /* The word still carries the meaning; the colour only makes a column of
                 them scannable. Never colour alone. */
              .yes { color: var(--accent); font-weight: 600; }
              .no { color: var(--faint); }
              .hint { font-size: 13px; color: var(--faint); max-width: 62ch; }
              .lede { font-size: 15px; color: var(--muted); max-width: 68ch;
                      margin: 0 0 20px; }
              .warn { max-width: 68ch; margin: 0 0 18px; padding: 10px 12px;
                      font-size: 13.5px; background: var(--warn-soft);
                      color: var(--warn); border: 1px solid var(--warn-line);
                      border-radius: 7px; }
              .empty { background: var(--panel); border: 1px dashed var(--rule-strong);
                       border-radius: 7px; padding: 16px 18px; max-width: 68ch; }
              .empty p { margin: 0 0 8px; }
              .empty p:last-child { margin: 0; }
              .paging { margin-top: 16px; display: flex; gap: 12px; font-size: 13px; }

              /* A wide grid is clipped at the paper's edge with nothing to say so.
                 Let it wrap on paper instead, and drop the chrome nobody can click. */
              @media print {
                .scroll { overflow: visible; border: 0; }
                table.grid th, table.grid td { white-space: normal; }
                .fmt, .who { display: none; }
                body { background: #fff; }
              }
            </style>
            </head>
            <body>
            <header class="top"><span class="name">Graticula <span>REST Services
            Directory</span></span><span class="who">{{Who(path)}}</span></header>
            <nav class="bar" aria-label="Breadcrumb"><ol>{{crumbs}}</ol>
            <span class="fmt">{{faces}}</span></nav>
            <main>{{body}}{{(close ? "</main></body></html>" : string.Empty)}}
            """;
    }

    /// <summary>The signed-in badge, or a link to sign in.</summary>
    /// <remarks>
    /// <b>The console is linked from here because the origin now redirects here.</b>
    /// Added 2026-08-17: nothing was mapped to "/", so the server's own address
    /// answered an empty 404 and the administration surface was reachable only by
    /// somebody who already knew its path. The directory is the front door — it is
    /// what an ArcGIS client asks for — and the console is the other thing a person
    /// wants, so it belongs in the one line that appears on every page.
    /// </remarks>
    private static string Who(string path)
    {
        string here = U(path);

        // Server rather than Console: the application was renamed with ADR-034 and a link
        // that says otherwise teaches the wrong word.
        string console = "<a href=\"/server/\">Server</a> &middot; ";

        /*
          <b>Sign out was a link, and the route is `MapPost`, so it answered 405 with an
          empty body on every page it appeared on — which is every page.</b> The browser
          showed a blank white screen and the session cookie was never cleared, so the
          banner still said *Signed in* afterwards. Nobody could leave a session from the
          browsable surface at all, and the failure looked like the server falling over
          rather than a method mismatch.

          <b>A form, not a `MapGet` beside the post.</b> Adding GET would have been one line
          and would make signing out something a page can do to a reader by embedding an
          image — the cookie authenticates reading only, so the damage is small, but *small*
          is not a reason to write it the wrong way round. A one-button form is the shape
          this has, and it is styled as a link so the banner still reads as a line of text.
        */
        return Current.Value is { Length: > 0 } name
            ? console
              + $"Signed in: <b>{H(name)}</b> &middot; "
              + "<form class=\"out\" action=\"/rest/auth/logout\" method=\"post\">"
              + "<input type=\"hidden\" name=\"f\" value=\"html\">"
              + "<button type=\"submit\">Sign out</button></form>"
            : console + $"<a href=\"/rest/login?return={here}\">Sign in</a>";
    }

    /// <summary>HTML-encodes a value. Nothing user-supplied is written without it.</summary>
    /// <summary>HTML-encodes a value, for pages built outside this class.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The encoded value.</returns>
    public static string Encode(string? value) => H(value);

    /// <summary>Wraps a body in the directory chrome, for pages built elsewhere.</summary>
    /// <param name="path">The request path, for the breadcrumb.</param>
    /// <param name="body">The page body.</param>
    /// <returns>The complete page.</returns>
    public static string Wrap(string path, string body) => Page(path, body);

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
