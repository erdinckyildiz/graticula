using System;
using Graticula.Api.OgcFeatures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;

namespace Graticula.Host;

/// <summary>
/// Which responses this server compresses, and which it never touches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured on real tile responses from the showcase, not asserted.</b> A
/// 3,937,039-byte <c>FeatureServer</c> query answer is 1,608,820 bytes with
/// gzip-6 (2.4x, 197 ms) and 1,373,852 with brotli-4 (2.9x, 84 ms); a
/// 633,260-byte one is 186,284 gzip-6 / 170,668 brotli-4 (3.7x, 21 ms). Re-run
/// here against the same two captures at .NET's built-in levels:
/// <c>CompressionLevel.Fastest</c> brotli took the 633,260-byte capture to
/// 167,289 bytes (3.79x) in 11 ms and a 7,681,042-byte one to 3,198,832 bytes
/// (2.40x) in 41 ms — <c>SmallestSize</c> bought at most another half a ratio
/// point on the small one and cost <b>12.6 seconds</b> on the large one. Fastest
/// is the only level that does not turn a query into a latency problem.
/// </para>
/// <para>
/// <b>An allowlist of paths, not a denylist of secrets — ADR-068 §4.</b> The
/// alternative is compressing everything and excluding every response that
/// happens to carry a token, which is a list that has to be perfect and stay
/// perfect as routes are added. This is the other shape: nothing is compressed
/// unless its path is named here, so a new endpoint is excluded by default and
/// an author has to add it to be compressed — including the one that returns a
/// credential, which then has to be a deliberate choice rather than an
/// omission. <c>/rest/auth/*</c>, <c>/rest/generateToken</c>,
/// <c>/sharing/rest/generateToken</c>, <c>/rest/setup</c>, <c>/rest/whoami</c>
/// and everything under <c>/admin</c> are excluded by not appearing below —
/// nothing had to be written to keep BREACH away from a token.
/// </para>
/// <para>
/// <b>Protobuf is compressed since 2026-09-28, when the benchmark ADR-068 §9 waited
/// for was run</b> (<c>benchmarks/mvt-compression</c>). It had been left out because
/// nobody had measured what compression does to a packed binary structure. It does
/// a lot: brotli at <c>Fastest</c> took 274 showcase tiles to 1.72x smaller, the
/// SDF glyph ranges to 5.55x and FeatureServer <c>f=pbf</c> answers to 2.32x, at
/// about 11 µs per KiB on the arm64 showcase. Tiles under a kilobyte gain nothing
/// and some grow by a few bytes, which costs less than the decision to skip them
/// would. <c>application/vnd.mapbox-vector-tile</c> is what a tile is served as and
/// <c>application/x-protobuf</c> is a glyph range and a FeatureServer <c>pbf</c>
/// answer; none of them carries a secret, which is the reasoning above.
/// </para>
/// <para>
/// <b>Static console assets are included</b> — <c>/server</c> and
/// <c>/studio</c>, ADR-034's two surfaces — because <c>console.js</c> and
/// <c>console.css</c> are exactly the kind of text a browser waits on before
/// anything else can run, and they carry nothing secret: they are the same
/// bytes for every caller.
/// </para>
/// </remarks>
internal static class ResponseCompressionPolicy
{
    /// <summary>
    /// The MIME types this server ever compresses, on top of the path allowlist below.
    /// </summary>
    /// <remarks>
    /// <b>A second, independent gate — not redundant with <see cref="IsAllowed"/>.</b>
    /// The path allowlist admits <c>/rest/services</c> wholesale, and that prefix also
    /// serves PNG map exports, JPEG tiles, MVT bytes and attachment downloads. The images
    /// and attachments are already compressed or are somebody's arbitrary bytes, and carry
    /// no MIME type below, so the two gates together compress the JSON, GeoJSON, XML and
    /// protobuf that pass through that prefix and nothing else it also serves.
    /// </remarks>
    internal static readonly string[] MimeTypes =
    [
        .. ResponseCompressionDefaults.MimeTypes,
        OgcNames.GeoJson, // application/geo+json — FeatureServer has no equivalent constant; ArcGIS answers plain application/json, already covered by the default set.
        OgcNames.Problem, // application/problem+json
        "application/gml+xml", // WfsEndpoints.GmlMediaType, minus the ";version=3.2" parameter the header carries
        "application/vnd.mapbox-vector-tile", // a vector tile — ADR-068 §9, measured 1.72x
        "application/x-protobuf", // a glyph range (5.55x) and a FeatureServer f=pbf answer (2.32x)
    ];

    /// <summary>Whether a request path may be compressed at all.</summary>
    /// <remarks>
    /// <para>
    /// <b>The gate is which requests ever reach <c>ResponseCompressionMiddleware</c>, not a
    /// per-request override on the ones that do — <c>IHttpsCompressionFeature</c> was tried and
    /// measured wrong.</b> Setting the feature's <c>Mode</c> to <c>DoNotCompress</c> on an
    /// excluded path had no effect with <c>EnableForHttps = true</c> — the response was
    /// compressed anyway, because the middleware only consults that feature as an opt-**in**
    /// while compression over HTTPS defaults off, not as an opt-**out** once it is already on
    /// everywhere. Setting <c>EnableForHttps = false</c> and using the feature to opt allowed
    /// paths back in did not work either — measured against the same test. Both readings of the
    /// feature depend on exactly how <c>ResponseCompressionProvider</c> reads it, which is not
    /// itself part of the public contract.
    /// </para>
    /// <para>
    /// <b>So <c>Program.cs</c> uses <see cref="IsAllowed"/> to branch the pipeline
    /// with <c>UseWhen</c> instead</b>: <c>app.UseResponseCompression()</c> is only ever
    /// registered on the branch this predicate admits. A request outside the allowlist never
    /// passes through the compression middleware at all — there is no feature to misread and no
    /// framework internal this depends on.
    /// </para>
    /// </remarks>
    /// <param name="path">The request path.</param>
    /// <returns>True for a surface named in the type remarks above.</returns>
    internal static bool IsAllowed(PathString path) =>
        path.StartsWithSegments("/rest/services", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(OgcNames.Base, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(WfsEndpoints.Path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(WmsEndpoints.Path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/server", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/studio", StringComparison.OrdinalIgnoreCase);
}
