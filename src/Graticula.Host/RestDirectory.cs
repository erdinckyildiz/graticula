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
/// content. Every page is <see cref="Html"/>, whose interpolation escapes any hole that is not already
/// markup, and the page writer takes nothing else — ADR-023 condition 3. It was one helper and a rule
/// until 2026-10-12.
/// </para>
/// </remarks>
internal static class RestDirectory
{
    /// <summary>The product's mark — a three-by-three grid in white — as the console draws it in its bar.</summary>
    private const string MarkSvg =
        "<svg viewBox=\"0 0 16 16\" focusable=\"false\"><g fill=\"#fff\">"
        + "<rect x=\"1\" y=\"1\" width=\"4\" height=\"4\"/><rect x=\"6\" y=\"1\" width=\"4\" height=\"4\"/>"
        + "<rect x=\"11\" y=\"1\" width=\"4\" height=\"4\"/><rect x=\"1\" y=\"6\" width=\"4\" height=\"4\"/>"
        + "<rect x=\"6\" y=\"6\" width=\"4\" height=\"4\"/><rect x=\"11\" y=\"6\" width=\"4\" height=\"4\"/>"
        + "<rect x=\"1\" y=\"11\" width=\"4\" height=\"4\"/><rect x=\"6\" y=\"11\" width=\"4\" height=\"4\"/>"
        + "<rect x=\"11\" y=\"11\" width=\"4\" height=\"4\"/></g></svg>";

    /// <summary>The site's icon: the mark on the accent disc, as a standalone SVG document.</summary>
    /// <remarks>
    /// <b>Served at <c>/favicon.svg</c> and <c>/favicon.ico</c></b> — UX sweep 2026-10-19: every
    /// page cost a 404 in the browser's console for the icon nobody had served, and the tab showed
    /// a blank page glyph. The disc is the console's light accent, which reads on a light and a
    /// dark tab strip alike; an icon cannot follow the page's theme.
    /// </remarks>
    internal const string Favicon =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\">"
        + "<circle cx=\"16\" cy=\"16\" r=\"16\" fill=\"#1565d8\"/>"
        + "<g fill=\"#fff\"><rect x=\"8\" y=\"8\" width=\"4.5\" height=\"4.5\"/>"
        + "<rect x=\"13.75\" y=\"8\" width=\"4.5\" height=\"4.5\"/><rect x=\"19.5\" y=\"8\" width=\"4.5\" height=\"4.5\"/>"
        + "<rect x=\"8\" y=\"13.75\" width=\"4.5\" height=\"4.5\"/><rect x=\"13.75\" y=\"13.75\" width=\"4.5\" height=\"4.5\"/>"
        + "<rect x=\"19.5\" y=\"13.75\" width=\"4.5\" height=\"4.5\"/><rect x=\"8\" y=\"19.5\" width=\"4.5\" height=\"4.5\"/>"
        + "<rect x=\"13.75\" y=\"19.5\" width=\"4.5\" height=\"4.5\"/><rect x=\"19.5\" y=\"19.5\" width=\"4.5\" height=\"4.5\"/>"
        + "</g></svg>";

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
    public static string SignIn(string returnTo, string? failed) => SignIn(returnTo, failed, [], [], null);

    /// <summary>The sign-in page, with a way in through each configured provider — ADR-088.</summary>
    /// <param name="returnTo">Where to go afterwards.</param>
    /// <param name="failed">Why the last attempt was refused, or null.</param>
    /// <param name="providers">The providers to offer, by where their sign-in starts and their name.</param>
    /// <param name="directories">The directories whose names and passwords the form takes — ADR-089.</param>
    /// <param name="host">The address this server was reached at, for the card's head, or null.</param>
    /// <returns>The page.</returns>
    public static string SignIn(
        string returnTo,
        string? failed,
        IReadOnlyList<(string Start, string Name)> providers,
        IReadOnlyList<string> directories,
        string? host = null)
    {
        ArgumentNullException.ThrowIfNull(directories);

        ArgumentNullException.ThrowIfNull(providers);

        HtmlBuilder body = new();

        body.Append(Html.Markup("<div class=\"card\"><div class=\"card-head\"><div><h1>Sign in to Graticula</h1>"));

        if (!string.IsNullOrEmpty(host))
        {
            body.Append($"<p class=\"where\">{host}</p>");
        }

        body.Append(Html.Markup("</div><span class=\"mark\" aria-hidden=\"true\">" + MarkSvg + "</span></div><div class=\"card-body\">"));

        // <b>A provider first, when there is one</b>: an organisation that configured one expects its people to
        // use it, and the password form below is for the accounts that live here.
        if (providers.Count > 0)
        {
            body.Append(Html.Markup("<ul class=\"providers\">"));

            foreach ((string start, string name) in providers)
            {
                body.Append($"<li><a href=\"{start}?return={Uri.EscapeDataString(returnTo)}\">Sign in with {name}</a></li>");
            }

            body.Append(Html.Markup("</ul>"));
        }

        // <b>Whose password the form takes, said</b> — design review 2026-09-24: "an account on this server" told a
        // directory's people their password did not belong here.
        Html whose = directories.Count == 0
            ? Html.Markup("an account on this server")
            : Html.Of($"your {Html.Join(Html.Markup(" or "), directories.Select(Html.Text))} name and password, or an account on this server");

        if (providers.Count > 0 || directories.Count > 0)
        {
            body.Append($"<p class=\"hint\">{(providers.Count > 0 ? "Or with " : "Sign in with ")}{whose}:</p>");
        }

        if (failed is not null)
        {
            // <b>A failed sign-in is a warning, not a hint.</b> `.hint` is the faint
            // grey this page uses for asides; the reason a sign-in did not work is the
            // most important sentence on the page at the moment it appears.
            body.Append($"<p class=\"warn\" id=\"signin-refused\">{failed}</p>");
        }

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
        /*
          <b>Labels above the fields, not a two-column table</b>, now that the form is a narrow card:
          the table put *Name* and *Password* beside boxes that had no room. Each field still has an
          `id` and a real `<label for>`, which is why the table's `<th>`s were made labels to begin with.

          <b>No Show button here, and that is the price of this page having no script.</b> The
          directory's policy is `default-src 'none'`, asserted by a test, and showing a password needs
          a script. The console's own sign-in has one.
        */
        body.Append(
            $"<form action=\"/rest/auth/login\" method=\"post\">"
            + $"<input type=\"hidden\" name=\"return\" value=\"{returnTo}\">"
            + $"<input type=\"hidden\" name=\"f\" value=\"html\">"
            + $"<label class=\"field\" for=\"signin-name\">Name</label>"
            + $"<input id=\"signin-name\" type=\"text\" name=\"name\" autocomplete=\"username\" autofocus"
            + $"{(failed is null ? Html.Empty : Html.Markup(" aria-describedby=\"signin-refused\""))}>"
            + $"<label class=\"field\" for=\"signin-password\">Password</label>"
            + $"<input id=\"signin-password\" type=\"password\" name=\"password\" autocomplete=\"current-password\">"
            // ADR-201: the second step, for an account that has one; left empty by everybody else.
            + $"<label class=\"field\" for=\"signin-code\">Code <span class=\"hint\">(only if your account asks for one)</span></label>"
            + $"<input id=\"signin-code\" type=\"text\" name=\"code\" inputmode=\"numeric\" autocomplete=\"one-time-code\">"
            + $"<label class=\"keep\"><input type=\"checkbox\" name=\"remember\" value=\"true\"> Keep me signed in</label>"
            + $"<button type=\"submit\">Sign in</button>"
            + $"</form>");

        // Not a reset link: this server sends no mail, so the answer is who can fix it.
        body.Append(Html.Markup(
            "<details><summary>Forgot password?</summary><p>Ask an administrator of this server to reset "
            + "it. They pass you a new password, and you choose your own the first time you use it. If you "
            + "sign in with your organization's account, your organization resets it.</p></details>"));

        body.Append(Html.Markup("</div></div>"));

        return Page("/rest/login", body.ToHtml(), bare: true);
    }

    /// <summary>A page that says one thing about a sign-in, with the way back to the form — ADR-088.</summary>
    /// <param name="heading">What happened.</param>
    /// <param name="sentence">What to do about it.</param>
    /// <returns>The page.</returns>
    /// <param name="back">Where the sign-in started, so the way back is to the same place.</param>
    public static string SignInMessage(string heading, string sentence, string back = "/rest/login") =>
        Page("/rest/login",
            Html.Of($"<h1>{heading}</h1><p class=\"warn\">{sentence}</p><p><a href=\"{back}\">Back to sign in</a></p>"));

    /// <summary>A refusal — a missing service, a stopped one, a bad query — as a directory page.</summary>
    /// <param name="path">The request path, for the breadcrumb.</param>
    /// <param name="heading">What happened, in a few words.</param>
    /// <param name="message">The same sentence the JSON answer carries.</param>
    /// <returns>The page.</returns>
    /// <remarks>
    /// <b>A browser that asked for a page gets one, saying what the JSON says</b> — UX sweep
    /// 2026-10-19. A mistyped service name in the directory replaced the page with a raw
    /// <c>{"error":…}</c> blob: no breadcrumb back, no styling, in the one surface built for
    /// browsing. The sentence is not reworded here, so the two answers cannot drift apart.
    /// </remarks>
    public static string Refusal(string path, string heading, string message) =>
        Page(path, Html.Of(
            $"<h1>{heading}</h1><p class=\"refused\" role=\"alert\">{message}</p>"
            + $"<p><a href=\"/rest/services\">Back to the services directory</a></p>"));

    /// <summary>Writes a refusal as a directory page for a browser in <c>/rest/</c>, and as ArcGIS JSON otherwise.</summary>
    /// <param name="context">The request.</param>
    /// <param name="status">The status, which both forms carry.</param>
    /// <param name="heading">The page's heading.</param>
    /// <param name="message">The sentence, the same in both forms.</param>
    /// <returns>The write.</returns>
    internal static System.Threading.Tasks.Task RefuseAsync(
        Microsoft.AspNetCore.Http.HttpContext context, int status, string heading, string message)
    {
        ArgumentNullException.ThrowIfNull(context);

        return RefusesAsPage(context.Request)
            ? Microsoft.AspNetCore.Http.Results.Content(
                Refusal(context.Request.Path, heading, message), "text/html; charset=utf-8", statusCode: status)
                .ExecuteAsync(context)
            : Microsoft.AspNetCore.Http.Results.Json(
                new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status)
                .ExecuteAsync(context);
    }

    /// <summary>Whether a refusal for this request should be a page rather than JSON.</summary>
    /// <param name="request">The request.</param>
    /// <returns>True for a browser reading the REST directory.</returns>
    /// <remarks>
    /// <b>Under <c>/rest/</c> only.</b> The OGC faces negotiate their own HTML and the WMS and WFS
    /// faces refuse in XML envelopes their clients parse; a browser's Accept header must not turn
    /// one of those into this page.
    /// </remarks>
    internal static bool RefusesAsPage(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Path.StartsWithSegments("/rest", StringComparison.OrdinalIgnoreCase)
            && WantsHtml(request.Query["f"], request.Headers.Accept);
    }

    /// <summary>A folder listing: its folders and its services.</summary>
    /// <param name="path">The request path, for the breadcrumb.</param>
    /// <param name="folder">The folder being listed, or null for the root.</param>
    /// <param name="version">The REST version to report.</param>
    /// <param name="folders">Folder names.</param>
    /// <param name="services">Service name and type pairs.</param>
    /// <param name="formats">
    /// The other protocol faces this server offers, or null.
    /// <b>[Q-102](../../docs/open-questions.md): the front door was the one page that did
    /// not name them.</b> A service page and a layer page have carried WFS, WMS and OGC API
    /// Features on their format line since 2026-08-20; this method took no such argument, and
    /// `/` redirects here — so the first page an anonymous reader sees advertised one protocol
    /// out of four. A stranger cannot ask a question whose vocabulary they have not been given.
    /// </param>
    /// <param name="folderCounts">
    /// How many services in each folder this reader can see, by folder name, or null when the
    /// caller could not count them. A folder missing from it shows no count.
    /// </param>
    /// <returns>An HTML document.</returns>
    public static string Folder(
        string path,
        string? folder,
        double version,
        IEnumerable<string> folders,
        IEnumerable<(string Name, string Type)> services,
        IEnumerable<(string Label, string Href)>? formats = null,
        IReadOnlyDictionary<string, int>? folderCounts = null)
    {
        HtmlBuilder body = new();

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
        body.Append(folder is { Length: > 0 }
            ? Html.Of($"<h1><code>{folder}</code></h1>")
            : Html.Markup("<h1>Services</h1>"));

        body.Append(
            $"<p class=\"sub\">REST services directory · version "
            + $"{version.ToString("0.0#", CultureInfo.InvariantCulture)}</p>"); // 12.1 as Esri writes it, not 12.10

        List<string> folderList = [.. folders];

        if (folderList.Count > 0)
        {
            body.Append($"<h3>Folders<span class=\"count\">{folderList.Count}</span></h3>");

            body.Append(Html.Markup("<ul class=\"cards\">"));

            foreach (string name in folderList)
            {
                // <b>How many, so an empty folder does not look like a full one</b> — UX sweep
                // 2026-10-19. The count is of what this reader can see, which the folder page
                // itself would show them; it says nothing about what is hidden from them.
                int many = folderCounts is not null && folderCounts.TryGetValue(name, out int held) ? held : 0;

                Html count = folderCounts is not null
                    ? Html.Of($"<span class=\"n\">{(many == 1 ? "1 service" : many.ToString(CultureInfo.InvariantCulture) + " services")}</span>")
                    : Html.Empty;

                body.Append(
                    $"<li><a class=\"row folder\" href=\"/rest/services/{U(name)}\">"
                    + $"<span class=\"id\">{name}</span>{count}</a></li>");
            }

            body.Append(Html.Markup("</ul>"));
        }

        List<(string Name, string Type)> serviceList = [.. services];

        // <b>A root whose services are all in folders says nothing about services — 2026-10-11.</b> The showcase's
        // first page read *No services … You are not signed in … sign in and look again* above six folders of public
        // services, and a reviewer walking it as a visitor from a public post read it as a locked demo. ArcGIS lists
        // the folders and says nothing. The sentence below stays for a directory with nothing in it at all.
        if (serviceList.Count == 0 && folderList.Count == 0)
        {
            // Said rather than left blank. An empty list and a list that failed
            // to load look identical, and the first is a fact worth stating —
            // especially here, where sharing may be the reason.
            body.Append(Html.Markup(
                "<h3>Services<span class=\"count\">0</span></h3><div class=\"empty\">"
                + "<p>No services, or none visible to you. A service you cannot read is not "
                + "listed, so this may look empty to one person and full to another.</p>"));

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
                body.Append(
                    $"<p class=\"hint\">You are not signed in. Services shared with the "
                    + $"organization are not listed to a stranger — "
                    + $"<a href=\"/rest/login?return={U(path)}\">sign in</a> and look again.</p>");
            }

            body.Append(Html.Markup("</div>"));
        }
        else if (serviceList.Count > 0)
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

            body.Append($"<h3>Services<span class=\"count\">{order.Count}</span></h3>");

            body.Append(Html.Markup("<ul class=\"cards\">"));

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

                body.Append(
                    $"<li><a class=\"row\" href=\"/rest/services/{U(name)}/{U(kinds[0])}\">"
                    + $"<span class=\"id\">{shown}</span>");

                // <b>One face is named on the row; several become links.</b> A row that
                // said "(FeatureServer)" and nothing else for a service with three faces
                // was the omission that made the same name appear three times.
                // <b>One face is a chip too, and the reason is that it sits next to three
                // of them.</b> A single-face card drew its type as small grey text inside the
                // link, so a row of cards mixed two shapes: some ending in a line of chips
                // and some in a caption, with visible dead space under the shorter one. Same
                // structure for one as for three, and the grid stops looking sorted by
                // accident.
                body.Append(Html.Markup("</a><span class=\"also\">"));

                foreach (string kind in kinds)
                {
                    body.Append($"<a href=\"/rest/services/{U(name)}/{U(kind)}\">{kind}</a>");
                }

                body.Append(Html.Markup("</span></li>"));
            }

            body.Append(Html.Markup("</ul>"));
        }

        return Page(path, body.ToHtml(), close: true, formats);
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
    /// <param name="serviceItemId">The service's portal item, linked to Studio's item page; null for none.</param>
    /// <param name="omit">
    /// Properties left out of the table because the page already shows them above it — the
    /// operations a GeometryServer links, for instance. A <paramref name="tree"/> leaves out
    /// <c>layers</c> by itself.
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
        IEnumerable<(string Label, string Href)>? formats = null,
        string? serviceItemId = null,
        IEnumerable<string>? omit = null)
    {
        HtmlBuilder body = new();

        body.Append($"<h1>{title}</h1>");

        if (links is not null)
        {
            List<(string Label, string Href)> list = [.. links];

            if (list.Count > 0)
            {
                body.Append($"<p><b>{linksLabel}:</b> ");
                body.Append(Html.Join(Html.Markup(" &middot; "), list.Select(l =>
                    Html.Of($"<a href=\"{l.Href}\">{l.Label}</a>"))));
                body.Append(Html.Markup("</p>"));
            }
        }

        // The service's portal item, on Studio's item page (ServiceItem, 2026-10-18).
        if (serviceItemId is { Length: > 0 })
        {
            body.Append($"<p><b>Portal item:</b> <a href=\"/studio/item/{serviceItemId}\">{serviceItemId}</a></p>");
        }

        if (tree is not null)
        {
            AppendTree(body, [.. tree]);
        }

        using JsonDocument parsed = JsonDocument.Parse(JsonSerializer.Serialize(document));

        /*
          <b>Listed once.</b> A service's layers were a linked tree and then, two screens down, the
          same layers again as a grid, and a GeometryServer's operations were links and then a
          bulleted list of the same words (UX sweep 2026-10-19). The JSON keeps both — it is a
          contract — and the page shows each thing in the form a reader can use.
        */
        HashSet<string> skipped = new(omit ?? [], StringComparer.Ordinal);

        if (tree is not null)
        {
            skipped.Add("layers");
        }

        body.Append(Html.Markup("<table class=\"props\">"));
        Rows(body, parsed.RootElement, path, skipped);
        body.Append(Html.Markup("</table>"));

        return Page(path, body.ToHtml(), close: true, formats);
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
        HtmlBuilder body, List<(string Label, string Href, int Depth)> entries)
    {
        if (entries.Count == 0)
        {
            body.Append(Html.Markup("<h3>Layers:</h3><p><i>None yet.</i></p>"));
            return;
        }

        body.Append(Html.Markup("<h3>Layers:</h3><ul>"));

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
                body.Append(Html.Markup("<ul>"));
                depth = at;
            }
            else
            {
                if (open)
                {
                    body.Append(Html.Markup("</li>"));
                    open = false;
                }

                while (depth > at)
                {
                    body.Append(Html.Markup("</ul></li>"));
                    depth--;
                }
            }

            body.Append($"<li><a href=\"{href}\">{label}</a>");

            open = true;
        }

        if (open)
        {
            body.Append(Html.Markup("</li>"));
        }

        while (depth > 0)
        {
            body.Append(Html.Markup("</ul></li>"));
            depth--;
        }

        body.Append(Html.Markup("</ul>"));
    }

    /// <summary>Walks a JSON object into table rows.</summary>
    private static void Rows(HtmlBuilder body, JsonElement element, string path, HashSet<string> skipped)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (skipped.Contains(property.Name))
            {
                continue;
            }

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
            Html rendered = property.Value.ValueKind == JsonValueKind.Array
                    && property.Value.GetArrayLength() > 0
                    && property.Value[0].ValueKind == JsonValueKind.Object
                ? Grid(property.Value, path, property.Name)
                : Value(property.Value, path, property.Name);

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
            if (rendered.ToString().Contains("class=\"scroll\"", StringComparison.Ordinal))
            {
                body.Append(
                    $"<tr class=\"wide\"><th colspan=\"2\">"
                    + $"{Humanise(property.Name)}</th></tr>"
                    + $"<tr class=\"wide\"><td colspan=\"2\">{rendered}</td></tr>");

                continue;
            }

            body.Append($"<tr><th>{Humanise(property.Name)}</th><td>{rendered}</td></tr>");
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
    private static Html Grid(JsonElement array, string path, string? key = null)
    {
        List<string> columns = [];

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                // Not a uniform array of objects, so a table would misrepresent
                // it. Fall back rather than force it.
                return Value(array, path, null);
            }

            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (!columns.Contains(property.Name, StringComparer.Ordinal))
                {
                    columns.Add(property.Name);
                }
            }
        }

        HtmlBuilder grid = new();
        // Focusable and named, so a keyboard can scroll it — axe `scrollable-region-focusable`.
        grid.Append($"<div class=\"scroll\" tabindex=\"0\" role=\"region\" aria-label=\"{(key is null ? "Table" : Humanise(key))}\">"
            + $"<table class=\"grid\"><thead><tr>");

        foreach (string column in columns)
        {
            grid.Append($"<th scope=\"col\">{Humanise(column)}</th>");
        }

        grid.Append(Html.Markup("</tr></thead><tbody>"));

        foreach (JsonElement item in array.EnumerateArray())
        {
            grid.Append(Html.Markup("<tr>"));

            foreach (string column in columns)
            {
                grid.Append(
                    $"<td>{(item.TryGetProperty(column, out JsonElement cell)
                        ? Value(cell, path, column)
                        : Html.Markup("<i>—</i>"))}</td>");
            }

            grid.Append(Html.Markup("</tr>"));
        }

        return grid.Append(Html.Markup("</tbody></table></div>")).ToHtml();
    }

    /// <summary>
    /// The integers that are amounts a person reads, and the only ones given group separators.
    /// </summary>
    /// <remarks>
    /// <b>An allow-list, because the safe default is the raw digits.</b> Every integer on these
    /// pages was printed with separators until 2026-10-19, so a developer copying a wkid out of
    /// the page got <c>102,100</c> — not a reference any client accepts — and a field length read
    /// <c>1,073,741,822</c>. An ArcGIS directory prints <c>4326</c>. A key missing from this list
    /// prints as digits, which is never wrong; a key wrongly on it would print an identifier that
    /// does not paste, which is.
    /// </remarks>
    private static readonly HashSet<string> Amounts = new(StringComparer.Ordinal)
    {
        "maxRecordCount", "standardMaxRecordCount", "tileMaxRecordCount", "maxSelectionCount",
        "maxRecordCountFactor", "maximumVertices", "maximumRequestBytes", "maximumCandidatePairs",
        "maxImageHeight", "maxImageWidth", "maxDownloadImageCount", "maxMosaicImageCount",
        "count", "featureCount", "recordCount",
    };

    /// <summary>A number, printed the way a person reads it.</summary>
    /// <param name="value">The number as it arrived.</param>
    /// <param name="key">The property it is the value of, or null when it has none.</param>
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
    /// <b>Integers are left exactly alone; only the fractional ones are rounded.</b> An id, a
    /// pixel count, a wkid and a byte count are exact and must stay exact — rounding 102100
    /// would be a different reference. Twelve significant figures is far more than any
    /// georeference carries and still short of where double's noise starts. Separators go only
    /// on the amounts in <see cref="Amounts"/>.
    /// </para>
    /// <para>
    /// <b>The console rounds the same numbers to six figures and this rounds to twelve, and
    /// that difference is deliberate.</b> The console is showing an operator a summary; this
    /// is the document a developer copies a value out of, so it errs towards keeping what is
    /// really there.
    /// </para>
    /// </remarks>
    private static string Number(JsonElement value, string? key)
    {
        if (value.TryGetInt64(out long whole))
        {
            return whole.ToString(
                key is not null && Amounts.Contains(key) ? "N0" : "0", CultureInfo.InvariantCulture);
        }

        if (!value.TryGetDouble(out double real) || !double.IsFinite(real))
        {
            return value.ToString();
        }

        return Math.Round(real, 12).ToString("0.############", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A colour array — <c>[r, g, b, a]</c> on 0–255, as a renderer's symbols carry it — drawn as a swatch.
    /// </summary>
    /// <param name="value">The array.</param>
    /// <param name="key">The property it is the value of.</param>
    /// <returns>The swatch and its text, or <see cref="Html.Empty"/> when this is not a colour.</returns>
    /// <remarks>
    /// <b>Decided by the key and the shape together.</b> A bounding box is four numbers too, and
    /// a swatch for one would be a lie with a colour; every colour in these documents sits under
    /// a key that ends in <c>color</c>. The text stays beside the swatch because it is what a
    /// developer copies, and because a swatch alone is colour alone.
    /// </remarks>
    private static Html Colour(JsonElement value, string? key)
    {
        if (key is null
            || !key.EndsWith("olor", StringComparison.Ordinal)
            || value.GetArrayLength() is not (3 or 4))
        {
            return Html.Empty;
        }

        int[] parts = new int[4];
        parts[3] = 255;
        int at = 0;

        foreach (JsonElement part in value.EnumerateArray())
        {
            if (!part.TryGetInt32(out int channel) || channel is < 0 or > 255)
            {
                return Html.Empty;
            }

            parts[at++] = channel;
        }

        string text = string.Create(
            CultureInfo.InvariantCulture, $"rgba({parts[0]},{parts[1]},{parts[2]},{parts[3]})");

        // CSS wants the alpha on 0–1; the text keeps Esri's 0–255, which is what the JSON says.
        string css = string.Create(
            CultureInfo.InvariantCulture,
            $"background:rgba({parts[0]},{parts[1]},{parts[2]},{Math.Round(parts[3] / 255.0, 3)})");

        return Html.Of($"<span class=\"swatch\" style=\"{css}\" aria-hidden=\"true\"></span><code>{text}</code>");
    }

    private static Html Value(JsonElement value, string path, string? key)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return Html.Markup("<i>none</i>");

            case JsonValueKind.True or JsonValueKind.False:
                // <b>A column of a dozen capability flags is read by pattern, not word by
                // word.</b> Plain lowercase `true`/`false` gives the eye nothing to catch,
                // and these documents carry ten or more of them.
                return value.GetBoolean()
                    ? Html.Markup("<span class=\"yes\">yes</span>")
                    : Html.Markup("<span class=\"no\">no</span>");

            case JsonValueKind.Number:
                return Html.Of($"<span class=\"v\">{Number(value, key)}</span>");

            case JsonValueKind.String:
                return Html.Text(value.ToString());

            case JsonValueKind.Array:
                if (value.GetArrayLength() == 0)
                {
                    return Html.Markup("<i>none</i>");
                }

                // <b>An array of like-shaped objects is a table wherever it sits, not only
                // at the top of the document.</b> `Rows` handles the top level; `lods` lives
                // one level down inside `tileInfo`, which is why 24 zoom levels were still
                // 24 bullets after the top-level rule was written. Deciding here catches
                // every depth.
                if (value[0].ValueKind == JsonValueKind.Object)
                {
                    return Grid(value, path, key);
                }

                // A renderer's colour was a bulleted column of four numbers — 2026-10-19.
                if (Colour(value, key) is { IsEmpty: false } swatch)
                {
                    return swatch;
                }

                HtmlBuilder items = new();
                items.Append(Html.Markup("<ul>"));

                foreach (JsonElement item in value.EnumerateArray())
                {
                    items.Append($"<li>{Value(item, path, key)}</li>");
                }

                return items.Append(Html.Markup("</ul>")).ToHtml();

            default:
                // A nested object — a field, an extent, a spatial reference.
                // Rendered inline as name: value pairs, because a nested table
                // per spatial reference makes a layer document unreadable.
                return Html.Join(Html.Markup(", "), value.EnumerateObject()
                    .Select(p => Html.Of($"<b>{Humanise(p.Name)}</b>: {Value(p.Value, path, p.Name)}")));
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
    public static string OpenPage(string path, Html body) => Page(path, body, close: false);

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
    /// <param name="bare">
    /// Leave out the masthead and the breadcrumb row, for the sign-in card: nothing in them is
    /// reachable until the reader has signed in.
    /// </param>
    /// <returns>The page.</returns>
    private static string Page(
        string path,
        Html body,
        bool close = true,
        IEnumerable<(string Label, string Href)>? formats = null,
        bool bare = false)
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

        HtmlBuilder crumbs = new();

        for (int i = 0; i < trail.Count; i++)
        {
            (string label, string? href) = trail[i];
            bool last = i == trail.Count - 1;

            crumbs.Append(last
                ? Html.Of($"<li aria-current=\"page\">{label}</li>")
                : Html.Of($"<li><a href=\"{href}\">{label}</a></li>"));
        }

        string json = path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "f=json";

        HtmlBuilder faces = new();
        faces.Append($"<a href=\"{json}\">JSON</a>");

        List<(string Label, string Href)> others = [.. formats ?? []];

        /*
          <b>JSON on the line, and the rest behind one disclosure when there are many.</b> The
          root names ten formats, and a row of ten chips that did not wrap made the page 651
          pixels wide on a 390-pixel phone (UX sweep 2026-10-19). A service page names four and
          keeps them inline — a disclosure there would hide the WFS link a reader looks for.
          `<details>` because this page has no script, and it opens in place, wrapping.
        */
        Html Chip((string Label, string Href) face) => Html.Of($"<a href=\"{face.Href}\">{face.Label}</a>");

        if (others.Count > 4)
        {
            faces.Append($"<details class=\"more\"><summary>Other formats ({others.Count})</summary><span class=\"list\">");
            faces.Append(Html.Concat(others.Select(Chip)));
            faces.Append(Html.Markup("</span></details>"));
        }
        else
        {
            // <b>No separator.</b> These were joined by " | " when they were bare links
            // on a line of their own; they are bordered chips in a flex row with a gap
            // now, so the pipe is a second separator drawn on top of the first.
            faces.Append(Html.Concat(others.Select(Chip)));
        }

        /*
          <b>The console's tokens, since 2026-10-19 — blue and navy, not teal and grey-black.</b>
          This page kept its own palette after the console moved to the Hybrid one: a teal
          accent, a `#10151b` dark ground, and format chips at 4.36:1 at 12px — under the 4.5
          small text needs (UX sweep 2026-10-19). The values below are `console.css`'s, copied
          because this page loads nothing: `--accent-ink` on `--accent-soft` is the pair the
          console's own chips use, 5.78:1 light and 6.08:1 dark. What is *not* borrowed is the
          console's rail: this page has one surface, so it needs one family of ink.

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
            <link rel="icon" href="/favicon.svg" type="image/svg+xml">
            <style>
              :root {
                --paper: #f4f6f9; --panel: #fff; --panel-2: #f1f4f8;
                --ink: #101826; --muted: #566275; --faint: #616b7a;
                --rule: #e3e8ef; --rule-strong: #cfd7e3; --control-edge: #7e889a;
                --accent: #1565d8; --accent-ink: #1257c2; --accent-soft: #e8f0fe;
                --accent-line: #bcd2fa; --on-accent: #fff;
                --ok: #1c7d4d; --ok-soft: #e6f4ec; --ok-line: #b6ddc6;
                --warn: #8a5a08; --warn-soft: #fdf2e0; --warn-line: #e8d3a8;
                --alert: #a5342a; --alert-soft: #fceceb; --alert-line: #edc4c0;
                --mono: ui-monospace, "Cascadia Mono", Consolas, monospace;
                --sans: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
                --gutter: 24px;
                color-scheme: light dark;
                /* Checkboxes and radios were browser-blue on a teal page — the one place
                   the palette did not reach, because nothing had told the browser. */
                accent-color: var(--accent);
              }
              @media (prefers-color-scheme: dark) {
                :root {
                  --paper: #0a1120; --panel: #0f1828; --panel-2: #142034;
                  --ink: #e6ebf4; --muted: #9aa7bd; --faint: #8592aa;
                  --rule: #1f2b40; --rule-strong: #2b3a55; --control-edge: #62739a;
                  --accent: #2563eb; --accent-ink: #6aa6ff; --accent-soft: #11264a;
                  --accent-line: #24497f;
                  --ok: #5fd39a; --ok-soft: #10301f; --ok-line: #1f5236;
                  --warn: #f0bd5c; --warn-soft: #33260c; --warn-line: #5a4416;
                  --alert: #f58a7f; --alert-soft: #3a1714; --alert-line: #6a2a24;
                }
              }
              /* The page's sides at a phone's width: 24px each side of 280 left 232 for content. */
              @media (max-width: 480px) { :root { --gutter: 16px; } }

              *, *::before, *::after { box-sizing: border-box; }

              body { margin: 0; background: var(--paper); color: var(--ink);
                     font: 15px/1.6 var(--sans);
                     -webkit-font-smoothing: antialiased; }

              a { color: var(--accent-ink); text-decoration-thickness: 1px;
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
                           padding: 14px var(--gutter); background: var(--panel);
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
                                     margin: 0; font: inherit; color: var(--accent-ink);
                                     text-decoration: underline;
                                     text-underline-offset: 2px; cursor: pointer;
                                     vertical-align: baseline; }
              .who form.out button:hover { text-decoration-thickness: 2px;
                                           filter: none; }
              .who b { color: var(--ink); font-weight: 600; }

              .bar { display: flex; flex-wrap: wrap; gap: 4px 16px;
                     align-items: baseline; justify-content: space-between;
                     padding: 9px var(--gutter); background: var(--paper);
                     border-bottom: 1px solid var(--rule); font-size: 13px; }
              .bar ol { display: flex; flex-wrap: wrap; align-items: baseline;
                        gap: 0 6px; margin: 0; padding: 0; list-style: none; }
              .bar li + li::before { content: "/"; color: var(--rule-strong);
                                     margin-right: 6px; }
              .bar li[aria-current] { color: var(--muted); }
              .fmt { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 8px;
                     font-size: 12px; min-width: 0; }
              .fmt a { font-family: var(--mono); text-transform: uppercase;
                       letter-spacing: 0.4px; text-decoration: none;
                       color: var(--accent-ink);
                       border: 1px solid var(--accent-line); border-radius: 5px;
                       padding: 2px 8px; background: var(--accent-soft); }
              .fmt a:hover { border-color: var(--accent); }
              .fmt details.more[open] { flex-basis: 100%; }
              .fmt details.more summary { display: inline-block; cursor: pointer; color: var(--muted);
                                          border: 1px solid var(--rule-strong);
                                          border-radius: 5px; padding: 2px 8px;
                                          background: var(--panel); }
              .fmt details.more[open] summary { margin-bottom: 8px; }
              .fmt .list { display: flex; flex-wrap: wrap; gap: 8px; }

              main { padding: 28px var(--gutter) 72px; max-width: 76rem; margin: 0 auto; }

              h1 { font-size: 26px; line-height: 1.25; font-weight: 640;
                   letter-spacing: -0.4px; margin: 0 0 4px; overflow-wrap: anywhere; }
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
              ul.cards a.row:hover { color: var(--accent-ink); }
              ul.cards a.folder { display: flex; justify-content: space-between;
                                  align-items: baseline; gap: 12px; }
              ul.cards .n { font-size: 12.5px; color: var(--muted); white-space: nowrap; }
              ul.cards .id { font-family: var(--mono); font-size: 13.5px;
                             font-weight: 600; word-break: break-word; }
              ul.cards .also { display: flex; flex-wrap: wrap; gap: 6px;
                               padding: 0 12px 9px; font-size: 11.5px; }
              /* 24px high: WCAG 2.2's target size, which these failed at 19 (UX sweep round 2). */
              ul.cards .also a { font-family: var(--mono); text-decoration: none;
                                 color: var(--muted); border: 1px solid var(--rule);
                                 border-radius: 4px; padding: 2px 7px; min-height: 24px;
                                 display: inline-flex; align-items: center; }
              ul.cards .also a:hover { color: var(--accent-ink);
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

              /*
                <b>The operation forms: labels above their fields, fields as wide as their
                group, and groups in fieldsets — 2026-10-19.</b> This was a two-column table
                with the label beside the input and `size="80"` on the inputs, so its floor was
                *widest label plus widest input*: 634px inside a 390px phone, and below 640px a
                media query unstacked the table cells. A label above a full-width field has no
                floor at all, which is the whole fix; the two columns at 760px are groups of
                short fields sitting side by side, not labels beside boxes.
              */
              form.ops { display: grid; gap: 14px; margin: 18px 0 8px; }
              form.ops fieldset { margin: 0; min-width: 0; padding: 10px 14px 14px;
                                  background: var(--panel); border: 1px solid var(--rule);
                                  border-radius: 8px; display: grid; gap: 12px;
                                  grid-template-columns: minmax(0, 1fr); }
              form.ops legend { font-size: 12.5px; font-weight: 650; text-transform: uppercase;
                                letter-spacing: 0.6px; color: var(--muted); padding: 0 4px; }
              .f { display: grid; gap: 4px; min-width: 0; align-content: start; }
              .f > label, .f > .lab { font-size: 13.5px; font-weight: 600; }
              .f input[type=text], .f textarea, .f select { width: 100%; min-width: 0; }
              .f .hint { margin: 0; font-size: 12.5px; }
              /* A pair of radios is one control with one name, so it is a fieldset of its own
                 whose legend is the label. */
              .f.pair { display: block; border: 0; padding: 0; margin: 0; min-width: 0; }
              .f.pair legend { font-size: 13.5px; font-weight: 600; padding: 0; margin-bottom: 4px;
                               text-transform: none; letter-spacing: 0; color: var(--ink); }
              .f.pair label { display: inline-flex; align-items: center; gap: 6px;
                              min-height: 28px; margin-right: 16px; }
              /*
                <b>A control this server cannot honour looks like one.</b> They were white boxes
                with the same edge as the live ones, so the reason under each was the only sign,
                and a reader scanning the form read it as forty working fields (UX sweep
                2026-10-19). Present, greyed and explained; still in the order a reader of
                ArcGIS's page expects to find them.
              */
              .f.off > label, .f.off > legend { color: var(--faint); font-weight: 500; }
              input:disabled, textarea:disabled, select:disabled {
                background: var(--panel-2); color: var(--faint); border-style: dashed;
                cursor: not-allowed; }
              @media (min-width: 760px) {
                form.ops fieldset { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                form.ops .f.wide { grid-column: 1 / -1; }
              }
              form.ops .run { display: flex; flex-wrap: wrap; align-items: end; gap: 12px; }
              form.ops .run .f { min-width: 12rem; }
              form.ops .run button { margin-top: 0; }
              /* The outcome of a run, above the form, so pressing the button visibly does
                 something at 1280x800 — the results start below a form two thousand pixels
                 tall. */
              .outcome { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 16px;
                         margin: 8px 0 18px; padding: 10px 14px; font-size: 14px;
                         border-radius: 8px; border: 1px solid var(--ok-line);
                         background: var(--ok-soft); }
              .outcome p { margin: 0; }
              .refused { margin: 4px 0 0; padding: 8px 12px; font-size: 13.5px;
                         color: var(--alert); background: var(--alert-soft);
                         border: 1px solid var(--alert-line); border-radius: 7px; }
              input[aria-invalid=true] { border-color: var(--alert); }
              input, select, textarea, button {
                font: inherit; color: var(--ink); background: var(--panel);
                border: 1px solid var(--control-edge); border-radius: 5px;
                padding: 6px 8px;
              }
              textarea { font: 13px/1.5 var(--mono); resize: vertical; }
              /* White on the accent in both themes: 5.41:1 on #1565d8 and 5.17:1 on the dark
                 theme's #2563eb. The old dark accent was a light teal, which is why the label
                 used to switch to the page's ground there. */
              button { background: var(--accent); border-color: var(--accent);
                       color: var(--on-accent); font-weight: 550; padding: 6px 16px;
                       cursor: pointer; margin-top: 8px; }
              button:hover { filter: brightness(1.08); }
              /* A colour from a renderer, beside the text that says it. */
              .swatch { display: inline-block; width: 14px; height: 14px; margin-right: 6px;
                        vertical-align: -2px; border-radius: 3px;
                        border: 1px solid var(--control-edge); }

              /* The word still carries the meaning; the colour only makes a column of
                 them scannable. Never colour alone. */
              .yes { color: var(--ok); font-weight: 600; }
              .no { color: var(--faint); }
              .hint { font-size: 13px; color: var(--faint); max-width: 62ch; }
              .lede { font-size: 15px; color: var(--muted); max-width: 68ch;
                      margin: 0 0 20px; }
              .warn { max-width: 68ch; margin: 0 0 18px; padding: 10px 12px;
                      font-size: 13.5px; background: var(--warn-soft);
                      color: var(--warn); border: 1px solid var(--warn-line);
                      border-radius: 7px; }
              /* ADR-088: a provider's way in, drawn as the console draws it. */
              ul.providers { list-style: none; padding: 0; margin: 0 0 12px; max-width: 320px; }
              ul.providers a { display: block; text-align: center; padding: 9px 14px; margin-bottom: 8px;
                               border: 1px solid var(--rule-strong); border-radius: 7px; font-weight: 600;
                               text-decoration: none; }
              .empty { background: var(--panel); border: 1px dashed var(--rule-strong);
                       border-radius: 7px; padding: 16px 18px; max-width: 68ch; }
              .empty p { margin: 0 0 8px; }
              .empty p:last-child { margin: 0; }
              .paging { margin-top: 16px; display: flex; gap: 12px; font-size: 13px; }

              /* <b>The sign-in card, alone on the page</b> — owner, 2026-09-25, holding this beside the
                 portal's. A head that says what you are signing in to, then the fields, then the two
                 small things the portal also has: keep me signed in, and what to do about a
                 forgotten password. No directory chrome: nothing in it is reachable yet. */
              main.bare { max-width: 420px; margin: clamp(32px, 12vh, 120px) auto 48px; padding: 0 16px; }
              .card { background: var(--panel); border: 1px solid var(--rule); border-radius: 10px;
                      box-shadow: 0 1px 2px rgba(16,24,38,.05), 0 12px 32px -14px rgba(16,24,38,.2); }
              .card-head { display: flex; align-items: center; justify-content: space-between; gap: 12px;
                           padding: 18px 22px; border-bottom: 1px solid var(--rule); }
              .card-head h1 { margin: 0; font-size: 20px; font-weight: 600; }
              .card-head .where { margin: 2px 0 0; font-size: 13.5px; color: var(--muted); overflow-wrap: anywhere; }
              .card-head .mark { width: 34px; height: 34px; flex: none; border-radius: 50%; display: grid;
                                 place-items: center; background: var(--accent); }
              .card-head .mark svg { width: 18px; height: 18px; display: block; }
              .card-body { padding: 16px 22px 22px; }
              .card-body .hint { margin: 0 0 12px; }
              .card-body ul.providers { max-width: none; }
              .card-body label.field { display: block; font-size: 13.5px; color: var(--muted); margin: 12px 0 4px; }
              .card-body input[type=text], .card-body input[type=password] { width: 100%; padding: 8px 10px; }
              .card-body label.keep { display: inline-flex; align-items: center; gap: 8px; margin: 12px 0 0; cursor: pointer; }
              .card-body label.keep input { width: 18px; height: 18px; margin: 0; }
              .card-body button { display: block; width: 100%; margin-top: 16px; padding: 10px 16px; }
              .card-body details { margin-top: 14px; text-align: center; font-size: 13.5px; }
              .card-body summary { color: var(--accent-ink); cursor: pointer; text-decoration: underline;
                                   text-underline-offset: 3px; display: inline-block; padding: 4px; }
              .card-body details p { margin: 6px 0 0; color: var(--muted); }

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
            {{(bare ? string.Empty : Chrome(path, crumbs.ToHtml(), faces.ToHtml()))}}
            <main{{(bare ? " class=\"bare\"" : string.Empty)}}>{{body}}{{(close ? "</main></body></html>" : string.Empty)}}
            """;
    }

    /// <summary>The directory's masthead and breadcrumb row, which a bare page leaves out.</summary>
    private static string Chrome(string path, Html crumbs, Html faces) =>
        "<header class=\"top\"><span class=\"name\">Graticula <span>REST Services\n"
        + "Directory</span></span><span class=\"who\">" + Who(path) + "</span></header>\n"
        + "<nav class=\"bar\" aria-label=\"Breadcrumb\"><ol>" + crumbs + "</ol>\n"
        + "<span class=\"fmt\">" + faces + "</span></nav>";

    /// <summary>The signed-in badge, or a link to sign in.</summary>
    /// <remarks>
    /// <b>The console is linked from here because the origin now redirects here.</b>
    /// Added 2026-08-17: nothing was mapped to "/", so the server's own address
    /// answered an empty 404 and the administration surface was reachable only by
    /// somebody who already knew its path. The directory is the front door — it is
    /// what an ArcGIS client asks for — and the console is the other thing a person
    /// wants, so it belongs in the one line that appears on every page.
    /// </remarks>
    private static Html Who(string path)
    {
        string here = U(path);

        // Server rather than Console: the application was renamed with ADR-034 and a link
        // that says otherwise teaches the wrong word.
        // <b>Studio first, since 2026-10-19</b> (UX sweep): `/` redirects here, so this line is the
        // first one a stranger reads, and Studio is where they can see a map. Server is the
        // administrator's door.
        Html console = Html.Markup("<a href=\"/studio/\">Studio</a> &middot; <a href=\"/server/\">Server</a> &middot; ");

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
              + Html.Of($"Signed in: <b>{name}</b> &middot; ")
              + Html.Markup("<form class=\"out\" action=\"/rest/auth/logout\" method=\"post\">"
                  + "<input type=\"hidden\" name=\"f\" value=\"html\">"
                  + "<button type=\"submit\">Sign out</button></form>")
            : console + Html.Of($"<a href=\"/rest/login?return={here}\">Sign in</a>");
    }

    /// <summary>Wraps a body in the directory chrome, for pages built elsewhere.</summary>
    /// <param name="path">The request path, for the breadcrumb.</param>
    /// <param name="body">The page body.</param>
    /// <returns>The complete page.</returns>
    public static string Wrap(string path, Html body) => Page(path, body);

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
