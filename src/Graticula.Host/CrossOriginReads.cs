using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// Which web pages on other origins may read this server's service responses — ADR-072.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-09-15, after a review from an ArcGIS user's side.</b> No response carried
/// <c>Access-Control-Allow-Origin</c> and an <c>OPTIONS</c> preflight answered 405, so a Maps SDK
/// for JavaScript application served from any other address could not read a single layer. ArcGIS
/// Server has allowed every origin by default since 10.1, through its <c>allowedOrigins</c>
/// setting, and a web developer meets this on the first request.
/// </para>
/// <para>
/// <b>Never with credentials.</b> The header is <c>*</c> or a named origin and
/// <c>Access-Control-Allow-Credentials</c> is never sent, so a browser does not attach the console's
/// session cookie to a cross-origin request and will not hand a page the response to one that did.
/// The cookie is also <c>SameSite=Strict</c> and honoured on GET and HEAD only
/// (<c>Authentication.CookieToken</c>). What a page on another origin can read is therefore what an
/// anonymous caller can read, or what a token the page itself holds can read — which it could have
/// asked for from its own server anyway.
/// </para>
/// <para>
/// <b>Not on the administrative surfaces.</b> <c>/admin</c> and the console answer nothing across
/// origins; a script on somebody else's page has no business driving them, even with a token.
/// </para>
/// </remarks>
internal sealed class CrossOriginReads
{
    /// <summary>Headers a cross-origin page may read beyond the safelisted ones.</summary>
    internal const string Exposed = "ETag, Age, Retry-After, X-Tile-Cache";

    private static readonly string[] Closed = ["/admin", "/server", "/studio", "/console"];

    private readonly bool _any;
    private readonly HashSet<string> _origins;

    private CrossOriginReads(bool any, IEnumerable<string> origins)
    {
        _any = any;
        _origins = new HashSet<string>(origins, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether any origin is allowed at all.</summary>
    public bool IsOn => _any || _origins.Count > 0;

    /// <summary>
    /// The policy this deployment is running, from its settings — the one place that reads them.
    /// </summary>
    /// <remarks>
    /// <b>Both the pipeline and the console read this.</b> The expression was written out where
    /// the middleware is installed, and a second copy of *unset means every origin* is a second
    /// place for the default to be wrong: a console reporting `none` for a server that allows
    /// everything is worse than a console that reports nothing.
    /// </remarks>
    /// <param name="settings">The host's.</param>
    /// <returns>The policy in force.</returns>
    public static CrossOriginReads Of(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.CorsOrigins ?? Parse(null);
    }

    /// <summary>
    /// The policy in words, for an administrator reading it rather than a browser obeying it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner decision on [Q-151](../../docs/open-questions.md), 2026-09-15: visible in the
    /// console, and read-only.</b> Every origin may read by default, which is ArcGIS Server's
    /// behaviour and the answer the owner confirmed; what was missing is that an administrator
    /// had no way to find out which policy their server was running short of sending a request
    /// from another origin and watching the headers. The setting stays a setting — it is a
    /// deployment's decision, made where the rest of the deployment is configured, and a
    /// console switch would put *who may read this server* one mis-click away.
    /// </para>
    /// </remarks>
    /// <returns>What is allowed, what is never allowed, and where it is set.</returns>
    public object Describe() => new
    {
        on = IsOn,
        allows = _any ? "every origin" : _origins.Count == 0 ? "no origin" : string.Join(", ", _origins.Order(StringComparer.OrdinalIgnoreCase)),
        anyOrigin = _any,
        credentials = false,
        exposes = Exposed,
        closed = Closed,
        setting = "Graticula:CorsOrigins",
        note = "A page on another origin reads what an anonymous caller reads, or what a token it "
             + "holds reads: Access-Control-Allow-Credentials is never sent, so a browser does not "
             + "attach this console's session cookie to a cross-origin request. The administrative "
             + "surfaces answer no origin at all. Set Graticula__CorsOrigins to 'none' or to a "
             + "comma-separated list of origins to narrow this — ADR-072.",
    };

    /// <summary>
    /// Reads <c>Graticula:CorsOrigins</c>: <c>*</c> for any origin, <c>none</c> for none, or a comma
    /// separated list of origins.
    /// </summary>
    /// <param name="configured">The setting, or null for the default, which is <c>*</c>.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="InvalidOperationException">An entry is not an origin.</exception>
    public static CrossOriginReads Parse(string? configured)
    {
        if (configured is null)
        {
            return new CrossOriginReads(any: true, []);
        }

        // <b>A word rather than an empty value</b>, because configuration treats an empty
        // environment variable as unset — so emptying it would silently mean the default.
        if (string.Equals(configured.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return new CrossOriginReads(any: false, []);
        }

        string[] entries = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (entries.Contains("*", StringComparer.Ordinal))
        {
            return new CrossOriginReads(any: true, []);
        }

        foreach (string entry in entries)
        {
            // <b>An origin, not a URL.</b> A browser sends `https://host[:port]` with no path, and a
            // configured `https://host/` would never match it — a setting that silently allows
            // nothing, discovered by the web developer it was meant for.
            if (!Uri.TryCreate(entry, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || uri.PathAndQuery != "/"
                || entry.EndsWith('/'))
            {
                throw new InvalidOperationException(
                    $"Graticula:CorsOrigins contains '{entry}', which is not an origin. Name each as "
                    + "scheme://host or scheme://host:port with no path and no trailing slash, "
                    + "separated by commas; '*' allows every origin and 'none' allows none.");
            }
        }

        return new CrossOriginReads(any: false, entries);
    }

    /// <summary>Whether a path answers across origins at all.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>Whether it does.</returns>
    public static bool Covers(PathString path) =>
        !Closed.Any(closed => path.StartsWithSegments(closed, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>Access-Control-Allow-Origin</c> value for a request's origin, or null.</summary>
    /// <param name="origin">The request's <c>Origin</c> header.</param>
    /// <returns><c>*</c>, the origin itself, or null when it is not allowed.</returns>
    public string? AllowFor(string origin) =>
        _any ? "*" : _origins.Contains(origin) ? origin : null;

    /// <summary>
    /// Adds the headers to responses and answers preflights, ahead of routing's 405.
    /// </summary>
    /// <param name="app">The pipeline.</param>
    /// <param name="policy">The policy.</param>
    public static void Use(IApplicationBuilder app, CrossOriginReads policy)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.IsOn)
        {
            return;
        }

        app.Use(async (context, next) =>
        {
            string? origin = context.Request.Headers.Origin;

            if (string.IsNullOrEmpty(origin) || !Covers(context.Request.Path)
                || policy.AllowFor(origin) is not { } allowed)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsOptions(context.Request.Method)
                && !StringValues.IsNullOrEmpty(context.Request.Headers.AccessControlRequestMethod))
            {
                // <b>Answered here, before any endpoint.</b> No route has an OPTIONS verb, so a
                // preflight reached the endpoint table and was refused 405 — and a browser that
                // cannot preflight never sends the request it wanted, which for the SDK is any
                // request carrying an `Authorization` header.
                IHeaderDictionary headers = context.Response.Headers;
                headers.AccessControlAllowOrigin = allowed;
                headers.AccessControlAllowMethods = "GET, POST, HEAD";
                headers.AccessControlAllowHeaders = context.Request.Headers.AccessControlRequestHeaders;
                headers.AccessControlMaxAge = "86400";
                AddVary(context, allowed);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // <b>On starting, not now</b>, because the exception handler clears the response's
            // headers before it writes an error — and a page that cannot read the error cannot tell
            // its user what went wrong.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.AccessControlAllowOrigin = allowed;
                context.Response.Headers.AccessControlExposeHeaders = Exposed;
                AddVary(context, allowed);
                return Task.CompletedTask;
            });

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// A named origin varies the response by <c>Origin</c>, so a shared cache does not hand one
    /// origin's allowance to another.
    /// </summary>
    private static void AddVary(HttpContext context, string allowed)
    {
        if (allowed != "*")
        {
            context.Response.Headers.Append("Vary", "Origin");
        }
    }
}
