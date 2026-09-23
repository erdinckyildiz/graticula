using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// Puts a FeatureServer <c>query</c> GET's response under the layer's own cache lifetime, the
/// same number and the same header shape <see cref="VectorTileEndpoints"/> already puts on that
/// layer's tiles — ADR-069.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured against the live showcase, 2026-09-13.</b> <c>curl -D -</c> against a running
/// PostGIS layer's <c>query</c> and a GeoParquet layer's <c>query</c> both carried no
/// <c>Cache-Control</c>, no <c>ETag</c> and no <c>Last-Modified</c>. The ArcGIS JS SDK draws a
/// feature layer with a <c>query</c> GET per tile — <c>resultType=tile</c>, a <c>geometry</c>,
/// <c>maxAllowableOffset</c> — and a client that carries none of those headers never has grounds
/// to keep one, so Chrome re-fetched 0.6–4 MB tiles the owner had already scrolled past once.
/// </para>
/// <para>
/// <b>The layer's own lifetime, not a second one for queries — INFERRED.</b> A layer already
/// carries one owner-facing knob for staleness, <see cref="PublishedLayer.CacheLifetime"/>, used
/// by <see cref="VectorTileEndpoints"/>: null is the server default, zero is <c>no-store</c>, and
/// anything else is seconds, sent back as <c>max-age</c> so every cache downstream agrees with
/// this one. Reusing it here rather than adding a query-specific setting is inferred from the
/// owner's instruction to do all three changes together, not stated as policy for editable
/// layers by name — the alternative is a second lifetime nothing today lets an operator set, and
/// it is listed in the ADR for confirmation.
/// </para>
/// <para>
/// <b>Applied only where the query already decided the response is safe to answer.</b> This is
/// called once <c>QueryAsync</c> knows the shape is <see cref="QueryShape.Features"/>, the method
/// is GET and the format is not HTML — never before a refusal, an error or an alternate shape has
/// already been written, and never for a POST, which this route answers identically to GET
/// (D-139) but which RFC 9111 §3 never treats as cacheable regardless of what headers a response
/// carries.
/// </para>
/// </remarks>
internal static class QueryResponseCaching
{
    /// <summary>
    /// The <c>Cache-Control</c> value for a response built from <paramref name="layers"/>: <c>no-store</c>
    /// for a zero lifetime, <c>public</c> only when nobody is signed in and every layer is public, and
    /// <c>private</c> otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Private unless every layer is public and nobody is signed in — never the reverse.</b>
    /// ADR-018 §3b's read check already ran before any handler that calls this, so what is decided
    /// here is only whether a <i>shared</i> cache — a CDN, a corporate proxy — may hold the bytes for
    /// a second caller. <c>LayerAccess.Evaluate</c> can answer <c>Public</c> for an anonymous caller
    /// and something else for everyone else the same service admits (owner, organisation, group,
    /// administrative override), and a shared cache cannot tell those apart from the response alone
    /// — it would hand the owner's or the group's copy to the next anonymous caller who asks. Marking
    /// every authenticated read <c>private</c>, including a signed-in caller reading a service that
    /// happens to be public, costs nothing but a second lookup per client cache and is the only rule
    /// that cannot leak.
    /// </para>
    /// <para>
    /// <b>One rule for both faces that serve a layer's data under its cache lifetime.</b> Until
    /// 2026-09-15 this lived inline in <see cref="ApplyAsync"/> and the vector tile face wrote
    /// <c>public</c> for every tile, so a private layer's tiles, fetched with a token, told every
    /// proxy in front of the server it could keep them for an hour and hand them on. Found by an
    /// authenticated review against the showcase; a tile is several layers at once, which is why this
    /// takes a list and asks that <i>all</i> of them be public.
    /// </para>
    /// </remarks>
    /// <param name="context">The request, for its principal.</param>
    /// <param name="layers">Every layer whose data is in the response.</param>
    /// <param name="lifetime">The response's lifetime.</param>
    /// <returns>The header value.</returns>
    internal static string CacheControlFor(
        HttpContext context, IEnumerable<PublishedLayer> layers, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layers);

        if (lifetime <= TimeSpan.Zero)
        {
            return "no-store";
        }

        string seconds = ((long)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        return $"{Audience(context, layers)}, max-age={seconds}";
    }

    /// <summary>
    /// The <c>Cache-Control</c> value for a response a client may keep but must ask about before every
    /// use: <c>no-cache</c>, with the same public-or-private rule as <see cref="CacheControlFor"/>.
    /// </summary>
    /// <remarks>
    /// <b>The tile half of V-56, the owner's decision of 2026-09-23 (ADR-069 §11).</b> A tile of a layer
    /// somebody can edit was kept by the browser for the tile lifetime, so an edit the server's own cache
    /// had already dropped (<c>TilePurgingWriter</c>) stayed on screen for up to an hour. <c>no-store</c>
    /// would re-send every tile on every pan; <c>no-cache</c> keeps the tile and makes the browser ask,
    /// and the tile's ETag turns an unchanged answer into a 304 of a few hundred bytes.
    /// </remarks>
    /// <param name="context">The request, for its principal.</param>
    /// <param name="layers">Every layer whose data is in the response.</param>
    /// <returns>The header value.</returns>
    internal static string RevalidateFor(HttpContext context, IEnumerable<PublishedLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layers);

        return $"{Audience(context, layers)}, no-cache";
    }

    // enum-default-is-deliberate: private — the two words below are Cache-Control directives, not
    // sharing scopes. A shared cache may keep only what every caller could have had, which is a
    // Public layer read anonymously; every other scope, and every signed-in read, is `private`.
    private static string Audience(HttpContext context, IEnumerable<PublishedLayer> layers)
    {
        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();
        bool anonymous = current is null || current.Principal.IsAnonymous;
        bool sharable = anonymous && layers.All(layer => layer.Sharing == SharingScope.Public);

        return sharable ? "public" : "private";
    }

    /// <summary>
    /// Decides the response's privacy and freshness, sets <c>Cache-Control</c> and, where a cheap
    /// validator is available, <c>ETag</c> — answering <c>304</c> without the caller running the
    /// query at all when the caller already holds a matching tag.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer being queried.</param>
    /// <param name="source">
    /// The resolved source. Checked for <see cref="ISourceCacheValidator"/>: implemented by a
    /// GeoParquet layer through <c>BudgetedFeatureSource</c>'s pass-through, absent for a
    /// registered PostGIS layer.
    /// </param>
    /// <param name="defaultLifetime">
    /// The server's own default, used when the layer names none — the same figure
    /// <see cref="VectorTileEndpoints"/> falls back to (<c>HostSettings.TileCacheLifetime</c>).
    /// </param>
    /// <param name="cancellation">The caller's.</param>
    /// <param name="writable">
    /// Whether the store takes writes to the relation — <see cref="LayerDescription.Writable"/>; null when
    /// nothing asked.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a <c>304</c> was written and the caller must return without
    /// running the query; <see langword="false"/> to proceed as normal, with the headers already
    /// on the response.
    /// </returns>
    public static async Task<bool> ApplyAsync(
        HttpContext context,
        PublishedLayer layer,
        IFeatureSource source,
        TimeSpan defaultLifetime,
        CancellationToken cancellation,
        bool? writable = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(source);

        TimeSpan lifetime = LifetimeOf(layer, defaultLifetime, writable);

        context.Response.Headers.CacheControl = CacheControlFor(context, [layer], lifetime);

        // <b>Zero means never cache — the same rule the tile face applies.</b> There is nothing to
        // validate for a response nobody may keep, so no ETag work happens below.
        if (lifetime <= TimeSpan.Zero)
        {
            return false;
        }

        // <b>Only where a validator can be had before the query runs — ADR-069.</b> A GeoParquet
        // layer answers from a file stat; a registered PostGIS layer has no cheap answer (see
        // `ISourceCacheValidator`'s remarks) and this handler does not buffer a multi-megabyte
        // streamed body to hash it instead — `FeatureServerQueryWriter` exists precisely because
        // buffering that body once already broke this face (ADR-062). Without a validator the
        // response is still fresh for `max-age` seconds; it simply cannot be revalidated cheaply
        // after that, and re-fetches the whole answer the way it always has.
        if (source is not ISourceCacheValidator validator)
        {
            return false;
        }

        string? version = await validator.CacheValidatorAsync(cancellation).ConfigureAwait(false);

        if (version is null)
        {
            return false;
        }

        // <b>Built from the layer, the request and the data version — not from the bytes.</b> Two
        // requests for the same layer with the same parameters against the same file version
        // produce the same JSON, so hashing the *inputs* identifies the representation exactly
        // as well as hashing the *output* would, without reading a single row to answer a
        // caller who may already hold it. Query-string pairs are sorted first so that the SDK
        // asking the same tile twice with its parameters in the same order — which is what it
        // does — is the common case, and an unordered client is only a missed cache rather than
        // a wrong one.
        string etag = ComputeETag(layer, context.Request.QueryString.Value, version);
        context.Response.Headers.ETag = etag;

        if (!Matches(context.Request.Headers.IfNoneMatch, etag))
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status304NotModified;

        // A 304 carries no body; Kestrel refuses Content-Length alongside it and a stale length
        // left set here is a response some proxies treat as truncated (the same fix
        // `VectorTileEndpoints.WriteTileAsync` carries for a tile's 304).
        context.Response.Headers.ContentLength = null;
        return true;
    }

    /// <summary>
    /// How long a query answer from this layer may be kept: its own lifetime when an administrator set one,
    /// the server's default for a layer nobody can edit, and nothing for a layer somebody can — V-56.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's decision, 2026-09-23, which answers ADR-069 §11.1.</b> The third ArcGIS review measured
    /// an editable hosted layer's <c>query</c> answering <c>public, max-age=3600</c>: a record corrected
    /// through <c>applyEdits</c> stayed wrong in every other browser for up to an hour, and ArcGIS sends no
    /// cache lifetime on <c>query</c> unless an administrator turns one on. So a layer that can be edited
    /// is not cached by default; a layer nobody can edit keeps the server default it had; and a lifetime an
    /// administrator set on the layer is honoured either way, because that is the switch ArcGIS offers too.
    /// </para>
    /// <para>
    /// <b>Editable is a fact about the layer, not about the caller.</b> An anonymous reader who may not edit
    /// still sees another person's edit late, so the test is whether anybody can: the rows have an integer
    /// id to address them, the store takes writes, and the service's ceiling offers at least one edit.
    /// A GeoParquet layer answers <c>Writable: false</c> and stays cached.
    /// </para>
    /// </remarks>
    /// <param name="layer">The layer.</param>
    /// <param name="defaultLifetime">The server's default.</param>
    /// <param name="writable">The store's answer, or null when nothing asked.</param>
    /// <returns>The lifetime; zero means <c>no-store</c>.</returns>
    internal static TimeSpan LifetimeOf(PublishedLayer layer, TimeSpan defaultLifetime, bool? writable)
    {
        ArgumentNullException.ThrowIfNull(layer);

        if (layer.CacheLifetime is { } chosen)
        {
            return chosen;
        }

        return Editable(layer, writable) ? TimeSpan.Zero : defaultLifetime;
    }

    /// <summary>Whether anybody at all can edit this layer's rows.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="writable">The store's answer, or null when nothing asked.</param>
    /// <returns>True when an edit is possible for somebody.</returns>
    internal static bool Editable(PublishedLayer layer, bool? writable) =>
        layer.Definition.HasIntegerIdentity
        && writable is not false
        && (layer.CapabilityCeiling is not { } ceiling
            || ceiling.Any(offered => offered is "Create" or "Update" or "Delete" or "Editing"));

    /// <summary>Whether GET is the only method this may be applied to — used by the caller.</summary>
    /// <remarks>
    /// <b>A named check rather than an inline comparison</b>, so the one rule — GET only, never
    /// POST, whatever the query string says — has one place a reader confirms it rather than
    /// trusting a call site got the comparison right. <c>query</c> answers both methods with the
    /// same code (D-139) and reads the same <c>context.Request.Query</c> either way, so nothing
    /// about the *parameters* distinguishes a cacheable request from an uncacheable one — only
    /// the method does.
    /// </remarks>
    public static bool IsSafeMethod(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HttpMethods.IsGet(context.Request.Method);
    }

    /// <summary>What a server build is, for a validator: its informational version.</summary>
    /// <remarks>
    /// <b>In the tag because a release can change the body without changing the data.</b> v1.0.47
    /// began applying <c>maxAllowableOffset</c> to GeoParquet layers: the same file and the same
    /// query string produced a different, smaller answer, and a tag built from those two alone
    /// would have told every browser holding the old one that it was still current.
    /// </remarks>
    internal static string Build { get; } =
        typeof(QueryResponseCaching).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? typeof(QueryResponseCaching).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The parts of a layer's configuration that can change what a query answers.</summary>
    /// <remarks>
    /// <para>
    /// <b>In the tag because republishing changes the body without changing the file.</b> An alias,
    /// a hidden field, a served reference or a record ceiling edited on an unchanged file would
    /// otherwise revalidate an answer that no longer matches the layer.
    /// </para>
    /// <para>
    /// <b>Every public property of <see cref="PublishedLayer"/> is either here or in
    /// <see cref="NotInFingerprint"/></b>, and <c>QueryResponseCachingTests</c> fails when a new one
    /// is in neither — a field added next month that nobody remembered to put in a validator is
    /// the stale answer this exists to prevent.
    /// </para>
    /// </remarks>
    internal static string Fingerprint(PublishedLayer layer) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            layer.Definition.Name,
            layer.Definition.SchemaName,
            layer.Definition.TableName,
            layer.Definition.GeometryColumn,
            layer.Definition.Srid,
            layer.Definition.IdentityColumn,
            layer.Definition.IntegerIdentityColumn,
            layer.Definition.IsHosted,
            layer.DataSourceName,
            layer.GeometryType,
            layer.ServedSrid,
            layer.ServedWkt,
            layer.TimeField,
            layer.Symbology,
            layer.FieldOverrides,
            layer.Cost,
            layer.CapabilityCeiling,
            layer.StatementTimeout,
            layer.Sharing,
            layer.SharedWith,
            layer.Owner,
            layer.Status,
            layer.ServiceId,
            layer.ServiceName,
            layer.Folder,
            layer.LayerIndex,
            layer.ParentIndex,
            layer.AttachmentQuotaBytes,
        });

    /// <summary>The properties deliberately left out of <see cref="Fingerprint"/>, and why.</summary>
    internal static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> NotInFingerprint =
        new System.Collections.Generic.Dictionary<string, string>
        {
            [nameof(PublishedLayer.Id)] = "already in the tag on its own",
            [nameof(PublishedLayer.Definition)] = "its members are listed one by one",
            [nameof(PublishedLayer.CacheLifetime)] = "shapes Cache-Control, not the body",
            [nameof(PublishedLayer.VisibleRange)] = "a query answers the same rows at every scale (ADR-070)",
            [nameof(PublishedLayer.ConnectionString)] = "a secret; the data source name and definition identify the data",
            [nameof(PublishedLayer.PublishedSrid)] = "derived from ServedSrid and Definition.Srid",
            [nameof(PublishedLayer.IsRunning)] = "derived from Status",
            [nameof(LayerDefinition.HasIntegerIdentity)] = "derived from IntegerIdentityColumn",
            [nameof(LayerDefinition.QuotedTable)] = "derived from SchemaName and TableName",
        };

    private static string ComputeETag(PublishedLayer layer, string? queryString, string version)
    {
        string normalised = queryString is { Length: > 1 }
            ? string.Join(
                '&',
                queryString[1..] // drop the leading '?'
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .OrderBy(pair => pair, StringComparer.Ordinal))
            : string.Empty;

        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{layer.Id:D}|{normalised}|{version}|{Build}|{Fingerprint(layer)}")));

        // Sixteen bytes, as VectorTileEndpoints truncates its own tag to. <b>Weak</b>, because the
        // same representation is sent brotli, gzip or identity by the compression middleware
        // (ADR-068) under one tag, and RFC 9110 §8.8.1 reserves a strong tag for byte-identical
        // bodies; a weak one is exactly what If-None-Match compares.
        return "W/\"" + Convert.ToHexString(hash.AsSpan(0, 16)) + "\"";
    }

    /// <summary>Whether the caller already holds this tag — the same rule a tile's 304 uses.</summary>
    private static bool Matches(StringValues header, string etag)
    {
        foreach (string? value in header)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (string candidate in value.Split(
                         ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (candidate == "*" || string.Equals(Opaque(candidate), Opaque(etag), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A tag without its weakness prefix — RFC 9110 §8.8.3.2's weak comparison.</summary>
    private static string Opaque(string tag) =>
        tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
}
