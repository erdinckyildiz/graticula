using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GisServer.Host;

/// <summary>
/// The response headers that hold when everything else has already gone wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added by the §66 security gate, 2026-08-15, and the finding was the shape
/// of the gap rather than its size.</b> Attachment downloads carried
/// <c>nosniff</c>, a sandboxing <c>Content-Security-Policy</c> and
/// <c>Content-Disposition: attachment</c> — carefully, by hand, because somebody
/// thought hard about the one surface that serves arbitrary user bytes. **Every
/// other response carried none of them.** That is the characteristic result of
/// protecting surfaces one at a time: the one that was considered is safe and
/// the default is bare.
/// </para>
/// <para>
/// <b>The REST directory is the reason this is not cosmetic.</b> It renders
/// user-supplied layer names, field aliases and folder names into HTML, and the
/// person reading that page is an administrator holding every privilege the
/// server has. The encoding is tested and correct; a policy is what stands
/// between an encoding mistake and a stolen session.
/// </para>
/// <para>
/// <b><c>Referrer-Policy</c> is the one with a named reason in
/// <see href="../../docs/security.md">security.md</see>.</b> ArcGIS
/// compatibility puts a token in the query string, and that document lists where
/// it leaks: logs, proxies, browser history, and <c>Referer</c> headers sent to
/// third parties. It then gives four mitigations — log redaction, preferring the
/// header form, short lifetimes, revocability — and **not one of them closes the
/// <c>Referer</c> channel**, which a single header does.
/// </para>
/// <para>
/// <b>Nothing already set is overwritten.</b> The attachment path deliberately
/// sends a stricter policy than the general one, and a middleware that stamped
/// over it would quietly relax the only surface that had been thought about.
/// </para>
/// </remarks>
internal static class SecurityHeaders
{
    /// <summary>
    /// The policy for the browsable HTML surfaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from what the pages actually contain, not copied from a
    /// checklist.</b> They load no external resource by design — that decision
    /// is in ADR-023, so that the directory is legible when it is the only thing
    /// working — and they contain no script at all, which is asserted by a test
    /// rather than believed.
    /// </para>
    /// <para>
    /// So: nothing may load, scripts are forbidden outright rather than
    /// restricted, inline styles are permitted because the page carries its own
    /// <c>&lt;style&gt;</c> block, and forms may only post back here. A page
    /// that later needs a script must change this line, which is the point.
    /// </para>
    /// </remarks>
    private const string HtmlPolicy =
        "default-src 'none'; "
        + "style-src 'unsafe-inline'; "
        + "img-src 'self' data:; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'; "
        + "base-uri 'none'";

    /// <summary>
    /// The policy for the console, which is the one HTML surface that is script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-08-16 after <see cref="HtmlPolicy"/> silently disabled the
    /// console for a day — D-44.</b> That policy's own comment said the pages
    /// "contain no script at all, which is asserted by a test rather than
    /// believed", and it was true of the three server-rendered pages it was
    /// written for. The console is a fourth HTML surface and it is nothing but
    /// script, so `default-src 'none'` with no `script-src` blocked its inline
    /// block and its map SDK together. It rendered — inline styles were
    /// permitted — and then every button did nothing, and its sign-in form fell
    /// back to a native GET that wrote the password into the URL.
    /// </para>
    /// <para>
    /// <b>Its script is a file, not an inline block, so this needs no
    /// `'unsafe-inline'`.</b> That was the reason for splitting `console.js` out:
    /// `script-src 'self'` is a real restriction where `'unsafe-inline'` is
    /// close to none.
    /// </para>
    /// <para>
    /// <b>The rest of the width is the map SDK, and it is a cost ADR-020 §4 did
    /// not record.</b> Choosing Esri's CDN over a vendored library was argued on
    /// redistribution grounds and on being the demanding real client; nobody
    /// noted that it also puts a third-party origin into this server's security
    /// policy. It is written out here rather than hidden behind a wildcard so
    /// that the cost is visible to whoever revisits that decision — and so that
    /// an air-gapped deployment (Q-15), which cannot reach the CDN at all, is
    /// visibly a different policy and not a broken one.
    /// </para>
    /// </remarks>
    private const string ConsolePolicy =
        "default-src 'none'; "
        + "script-src 'self' https://js.arcgis.com; "
        + "style-src 'unsafe-inline' https://js.arcgis.com; "
        + "img-src 'self' data: blob: https://js.arcgis.com https://tile.openstreetmap.org; "
        + "connect-src 'self' https://js.arcgis.com https://tile.openstreetmap.org; "
        + "font-src https://js.arcgis.com; "
        + "worker-src blob:; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'; "
        + "base-uri 'none'";

    /// <summary>
    /// The policy for everything else, which is a document rather than a page.
    /// </summary>
    /// <remarks>
    /// A JSON document, a tile or a glyph range has no business loading
    /// anything, and a browser told to render one directly should be unable to
    /// do anything with it.
    /// </remarks>
    private const string DocumentPolicy = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>Stamps the headers on every response.</summary>
    /// <param name="app">The application.</param>
    /// <param name="https">Whether this server requires HTTPS, for HSTS.</param>
    public static void UseSecurityHeaders(this WebApplication app, bool https)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, next) =>
        {
            // <b>On starting, not here.</b> Headers set before the pipeline runs
            // would be written even on a response the endpoint replaced, and
            // headers set after it are too late — the response may already be on
            // the wire. This is the one hook that fires exactly once, just
            // before the first byte.
            context.Response.OnStarting(() =>
            {
                IHeaderDictionary headers = context.Response.Headers;

                Set(headers, "X-Content-Type-Options", "nosniff");

                // no-referrer rather than same-origin: the thing being protected
                // is a credential in the URL, and same-origin still sends the
                // full URL to ourselves — into our own access log, by a second
                // route that the log redaction does not cover.
                //
                // <b>Except on the console, and the reason is measured rather than
                // assumed.</b> A tile service asked for identification and got
                // none, so it served a 256×256 PNG reading "Access blocked" in
                // place of every tile — the same URL answers with the real tile
                // when a Referer is present. That was tested directly: no Referer
                // gives a 6.9 KB warning image, our origin as Referer gives a
                // 40 KB map.
                //
                // `strict-origin-when-cross-origin` sends the **origin only** —
                // no path, no query — so the credential this header exists to
                // protect still cannot leave. What a third party learns is that
                // this server exists, which it learns from the request anyway.
                Set(headers, "Referrer-Policy",
                    context.Request.Path.StartsWithSegments(
                        "/console", StringComparison.OrdinalIgnoreCase)
                        ? "strict-origin-when-cross-origin"
                        : "no-referrer");

                // X-Frame-Options as well as frame-ancestors, because they are
                // not the same age and not every proxy and browser in a
                // ten-year-old estate honours the newer one.
                Set(headers, "X-Frame-Options", "DENY");

                bool html = headers.ContentType.ToString()
                    .StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

                // <b>By path, not by content type, and the console is the reason
                // the distinction exists.</b> Three of the four HTML surfaces
                // here genuinely carry no script and their policy should keep
                // saying so; the fourth is an application. Deciding on content
                // type alone is what produced one policy for four pages with
                // different needs — and the page that needed something else broke
                // silently, because a blocked script logs nothing the server can
                // see.
                bool console = context.Request.Path
                    .StartsWithSegments("/console", StringComparison.OrdinalIgnoreCase);

                Set(headers, "Content-Security-Policy",
                    console ? ConsolePolicy : html ? HtmlPolicy : DocumentPolicy);

                if (https)
                {
                    // Two years, and no preload directive. Preloading is a
                    // decision about somebody's domain that this server has no
                    // business taking on their behalf, and it is close to
                    // irreversible.
                    Set(headers, "Strict-Transport-Security", "max-age=63072000; includeSubDomains");
                }

                return Task.CompletedTask;
            });

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Sets a header only if the response has not already spoken.</summary>
    private static void Set(IHeaderDictionary headers, string name, string value)
    {
        if (!headers.ContainsKey(name))
        {
            headers[name] = value;
        }
    }
}
