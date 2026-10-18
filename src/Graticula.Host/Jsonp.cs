using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>Wraps a JSON answer in the function <c>callback=</c> names, as ArcGIS's REST API does (JSONP).</summary>
/// <remarks>
/// <para>
/// <b>Found by the veteran review of 2026-10-17 (V-85):</b> <c>callback</c> was ignored and plain JSON came back, which a
/// <c>&lt;script&gt;</c> tag cannot read. Old embeds and the JS 3.x era's clients read the REST API that way.
/// </para>
/// <para>
/// <b>Never with the session cookie.</b> A script request carries the browser's cookies and no <c>Origin</c>, so it is
/// the one cross-site read the CORS rule (Q-169) cannot see; <c>SameSite=Strict</c> keeps the cookie off another site's
/// requests but not off a sibling host's. <see cref="Authentication"/> reads a request with <c>callback</c> as if it had
/// no cookie, so a JSONP answer is what an anonymous caller, or the token the page itself put in the address, reads.
/// </para>
/// <para>
/// <b>The answer is 200 whatever the status was</b>: a browser does not run a script it was answered 4xx or 5xx for,
/// so the error object — which carries its own code, as ArcGIS's does — would never reach the callback.
/// </para>
/// </remarks>
internal static partial class Jsonp
{
    /// <summary>Whether the request asks for JSONP.</summary>
    public static bool Asked(HttpRequest request) => request.Query.ContainsKey("callback");

    /// <summary>A callback this server will write: a JavaScript name or a dotted path of them, as Dojo and the SDKs send.</summary>
    public static bool IsCallback(string? name) => name is { Length: > 0 and <= 128 } && CallbackName().IsMatch(name);

    /// <summary>Buffers the answer and, when it is JSON, writes it as <c>/**/callback(answer);</c>.</summary>
    public static async Task WrapAsync(HttpContext context, RequestDelegate next)
    {
        string? callback = context.Request.Query["callback"];

        if (!IsCallback(callback))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                "{\"error\":{\"code\":400,\"message\":\"'callback' is not a JavaScript function name, so nothing was "
                + "written around the answer. Send letters, digits, '_', '$' and '.' only.\",\"details\":[]}}")
                .ConfigureAwait(false);
            return;
        }

        Stream original = context.Response.Body;
        using MemoryStream buffered = new();
        context.Response.Body = buffered;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffered.Position = 0;

        if (context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true || buffered.Length == 0)
        {
            await buffered.CopyToAsync(original).ConfigureAwait(false);
            return;
        }

        byte[] before = Encoding.UTF8.GetBytes($"/**/{callback}(");
        byte[] after = ");"u8.ToArray();

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/javascript; charset=utf-8";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.ContentLength = before.Length + buffered.Length + after.Length;

        await original.WriteAsync(before).ConfigureAwait(false);
        await buffered.CopyToAsync(original).ConfigureAwait(false);
        await original.WriteAsync(after).ConfigureAwait(false);
    }

    // \z, not $: $ also matches before a final newline, and "cb\n" passed as a name.
    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*(\.[A-Za-z_$][A-Za-z0-9_$]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex CallbackName();
}
