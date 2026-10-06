using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Api.OgcFeatures;
using Graticula.Api.Tiles;
using Graticula.Api.Wms;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// OGC API – Styles, read-only, over the styles this server already keeps — ADR-176.
/// </summary>
/// <remarks>
/// <para>
/// <b>At <c>/ogc/styles/v1</c>, beside the features and tiles faces</b>, and over their unit: a style here belongs to
/// a vector tile service, found by <see cref="TileFaces"/> exactly as OGC API Tiles and WMTS find it, so a style is
/// listed precisely when the tiles it draws are served to the same caller. A service the caller may not see has no
/// styles here — not listed, and each of its style addresses answers 404, one answer for absent and forbidden
/// (ADR-018).
/// </para>
/// <para>
/// <b>No second store.</b> The stored styles are the rows <c>/admin/services/{name}/styles</c> writes (ADR-094), read
/// by name through <see cref="PostgresLayerCatalog"/>; the Mapbox stylesheet of a stored style is what
/// <c>resources/styles/{name}.json</c> serves, checked the same way (<c>VectorTileEndpoints.StoredStyleFitsNowAsync</c>)
/// and given way to the generated style in the same case; the symbology style is the generated <c>root.json</c> and
/// WMS GetStyles' SLD (<see cref="WmsEndpoints.SldLayerOf"/>), the same derivations those routes make.
/// </para>
/// <para>
/// <b>Refusals are RFC 7807 problems</b>, as on the other OGC API faces: 404 for a style that is not there for the
/// caller, 400 for an <c>f</c> this face does not know, 406 for an encoding a style is not offered in, 503 while the
/// catalogue cannot say.
/// </para>
/// </remarks>
internal static class OgcStylesEndpoints
{
    private const string Root = OgcStylesDocuments.Base;

    /// <summary>Maps the face.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>Filtering, as every standard face is governed</b>: the list holds what the caller may see, and a style is
        // found through the same rule (TileFaces), so a style of a service the caller may not see is absent.
        app.MapGet(Root, Landing).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/conformance", Conformance).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/api", Api).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/styles", StylesAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/styles/{styleId}", StyleAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/styles/{styleId}/metadata", MetadataAsync).Governed(SharingGovernedExtensions.ByFiltering);
    }

    /// <summary>The link the REST directory's root page offers to this face.</summary>
    /// <returns>The label and the address.</returns>
    public static (string Label, string Href) DirectoryLink() => ("OGC API Styles", Root);

    private static string Base(HttpContext context) => TileFaces.Origin(context) + Root;

    private static string TilesBase(HttpContext context) => TileFaces.Origin(context) + TileNames.OgcBase;

    private static Task Landing(HttpContext context) =>
        AnswerAsync(context, "OGC API Styles", OgcStylesDocuments.Landing(Base(context)), OgcNames.Json);

    private static Task Conformance(HttpContext context) =>
        AnswerAsync(context, "Conformance", OgcStylesDocuments.Conformance(), OgcNames.Json);

    private static Task Api(HttpContext context) =>
        AnswerAsync(context, "API definition", OgcStylesDocuments.OpenApi(Base(context)), OgcNames.OpenApi);

    private static async Task StylesAsync(HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (await TileFaces.VisibleAsync(context, catalog, cancellation).ConfigureAwait(false) is not { } visible)
        {
            await RefuseAsync(context, Unavailable).ConfigureAwait(false);
            return;
        }

        if (await StylesOfAsync(catalog, visible.Services, cancellation).ConfigureAwait(false) is not { } styles)
        {
            await RefuseAsync(context, Unavailable).ConfigureAwait(false);
            return;
        }

        await AnswerAsync(context, "Styles", OgcStylesDocuments.Styles(Base(context), styles), OgcNames.Json)
            .ConfigureAwait(false);
    }

    private static async Task MetadataAsync(
        HttpContext context, string styleId, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (await FindAsync(context, styleId, catalog, cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        await AnswerAsync(
                context,
                found.Style.Title,
                OgcStylesDocuments.Metadata(Base(context), TilesBase(context), found.Style),
                OgcNames.Json)
            .ConfigureAwait(false);
    }

    /// <summary>A style's stylesheet, in the encoding <c>f</c> or <c>Accept</c> chose.</summary>
    /// <remarks>
    /// <para>
    /// <b>The Mapbox stylesheet is what the VectorTileServer serves for the same style</b>, with its relative
    /// addresses made absolute (<see cref="OgcStylesDocuments.AbsoluteAddresses"/>) because they were written for
    /// <c>resources/styles/</c> and are read from here. A stored style that no longer fits its service gives way to
    /// the generated one, with <c>Graticula-Style-Stale</c> saying so, as <c>resources/styles/{name}.json</c> does —
    /// one rule for one style, whichever address it is asked at.
    /// </para>
    /// <para>
    /// <b>The SLD is WMS GetStyles' for the service's layers</b>, a <c>NamedLayer</c> a layer named as the layer is
    /// named in a tile and in WMS, its <c>UserStyle</c> named with the style's id and its <c>FeatureTypeStyle</c> naming
    /// the layer — 20-009's <c>/rec/sld-se/style-names</c>.
    /// </para>
    /// </remarks>
    private static async Task StyleAsync(
        HttpContext context,
        string styleId,
        CatalogFallback catalog,
        GlyphStore glyphs,
        StyleOriginList origins,
        ILoggerFactory logs,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, styleId, catalog, cancellation).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        (StyleAnswer? answer, OgcProblem? refusal) = OgcStylesDocuments.Negotiate(
            context.Request.Query["f"], context.Request.Headers.Accept.ToString(), found.Style);

        // Whichever representation is chosen, it depends on what was asked for.
        context.Response.Headers.Vary = "Accept";

        if (refusal is not null)
        {
            await RefuseAsync(context, refusal).ConfigureAwait(false);
            return;
        }

        PublishedService service = found.Candidate.Service;

        switch (answer)
        {
            case StyleAnswer.Html:
                await HtmlAsync(
                        context, found.Style.Title,
                        OgcStylesDocuments.Metadata(Base(context), TilesBase(context), found.Style))
                    .ConfigureAwait(false);
                return;

            case StyleAnswer.Sld11:
            case StyleAnswer.Sld10:
                List<(string, System.Text.Json.Nodes.JsonNode?, IReadOnlyList<string>)> layers =
                    [.. service.Layers.Select(WmsEndpoints.SldLayerOf)];

                string sld = answer == StyleAnswer.Sld11
                    ? StyledLayerDescriptor.Write(layers, found.Style.Id, namesFeatureTypes: true)
                    : StyledLayerDescriptor.Write10(layers, found.Style.Id, namesFeatureTypes: true);

                context.Response.ContentType = (answer == StyleAnswer.Sld11
                    ? OgcStylesDocuments.Sld11MediaType
                    : OgcStylesDocuments.Sld10MediaType) + ";charset=utf-8";
                await context.Response.WriteAsync(sld, cancellation).ConfigureAwait(false);
                return;
        }

        string? stored = null;

        if (found.Style.Name is { } name)
        {
            (string Style, bool IsDefault)? named;

            try
            {
                named = catalog.Catalog is { } store
                    ? await store.FindNamedStyleAsync(service.Id, name, cancellation).ConfigureAwait(false)
                    : null;
            }
            catch (Exception e) when (CatalogFallback.IsUnreachable(e))
            {
                await RefuseAsync(context, Unavailable).ConfigureAwait(false);
                return;
            }

            // Listed a moment ago and removed since: the address no longer names a style.
            if (named is not { } still)
            {
                await RefuseAsync(context, NoSuchStyle(styleId)).ConfigureAwait(false);
                return;
            }

            (bool fits, string? stale) = await VectorTileEndpoints
                .StoredStyleFitsNowAsync(service, still.Style, catalog.Catalog, origins, cancellation)
                .ConfigureAwait(false);

            if (fits)
            {
                stored = still.Style;
            }
            else
            {
                Log.StyleStale(logs.CreateLogger("Graticula.Tiles"), service.Name, stale!);
                context.Response.Headers["Graticula-Style-Stale"] = "true";
            }
        }

        string document = stored
            ?? JsonSerializer.Serialize(VectorTileEndpoints.GeneratedStyle(service, glyphs), JsonSerializerOptions.Web);

        Uri writtenFor = new(
            $"{TileFaces.Origin(context)}/rest/services/{service.QualifiedName}/VectorTileServer/resources/styles/root.json");

        context.Response.ContentType = OgcStylesDocuments.MapboxMediaType + "; charset=utf-8";
        await context.Response
            .WriteAsync(OgcStylesDocuments.AbsoluteAddresses(document, writtenFor), cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>A style found by its id, with the service it belongs to.</summary>
    private sealed record Found(TileFaces.Candidate Candidate, OgcStyle Style);

    /// <summary>The style an id names for this caller, or null with the refusal written.</summary>
    /// <remarks>
    /// <b>The service by <see cref="TileFaces.FindAsync"/>, the style by name in that service</b> — the one or two
    /// reads the tile routes make, not the whole listing, since a client asks for a style once per map it opens.
    /// </remarks>
    private static async Task<Found?> FindAsync(
        HttpContext context, string styleId, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (!OgcStylesDocuments.TryParseStyleId(styleId, out string collectionId, out string? name))
        {
            await RefuseAsync(context, NoSuchStyle(styleId)).ConfigureAwait(false);
            return null;
        }

        if (await TileFaces.FindAsync(context, catalog, collectionId, cancellation).ConfigureAwait(false) is not { } lookup)
        {
            await RefuseAsync(context, Unavailable).ConfigureAwait(false);
            return null;
        }

        if (lookup is not (TileFaces.Lookup.Found, { } candidate))
        {
            await RefuseAsync(context, NoSuchStyle(styleId)).ConfigureAwait(false);
            return null;
        }

        if (await StylesOfAsync(catalog, [candidate], cancellation).ConfigureAwait(false) is not { } styles)
        {
            await RefuseAsync(context, Unavailable).ConfigureAwait(false);
            return null;
        }

        if (styles.FirstOrDefault(s => StyleNames.Same(s.Name, name)) is not { } style)
        {
            await RefuseAsync(context, NoSuchStyle(styleId)).ConfigureAwait(false);
            return null;
        }

        await TileFaces.UsingAsync(context, candidate).ConfigureAwait(false);
        return new Found(candidate, style);
    }

    /// <summary>Every style of some services, or null when the store cannot say which they have.</summary>
    /// <remarks>
    /// <b>Null rather than the symbology styles alone</b> while the store is unreachable: <c>/req/core/styles-success</c>
    /// C says the list holds every style on the server, so a list missing the stored ones would be a false answer, and
    /// the named styles have no remembered copy to fall back on (<c>FindNamedStyleAsync</c>).
    /// </remarks>
    private static async Task<IReadOnlyList<OgcStyle>?> StylesOfAsync(
        CatalogFallback catalog, IReadOnlyList<TileFaces.Candidate> services, CancellationToken cancellation)
    {
        IReadOnlyList<(Guid ServiceId, string Name, bool IsDefault, DateTimeOffset? UpdatedAt)> stored;

        try
        {
            stored = catalog.Catalog is { } store
                ? await store.ListStyleNamesAsync([.. services.Select(s => s.Service.Id)], cancellation).ConfigureAwait(false)
                : [];
        }
        catch (Exception e) when (CatalogFallback.IsUnreachable(e))
        {
            return null;
        }

        ILookup<Guid, (Guid ServiceId, string Name, bool IsDefault, DateTimeOffset? UpdatedAt)> byService =
            stored.ToLookup(s => s.ServiceId);

        List<OgcStyle> styles = [];

        foreach (TileFaces.Candidate candidate in services.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            PublishedService service = candidate.Service;
            List<(Guid ServiceId, string Name, bool IsDefault, DateTimeOffset? UpdatedAt)> named = [.. byService[service.Id]];

            // The first layer of a name is the one a tile carries under it (VectorTileEndpoints.GeneratedStyle).
            List<(string, Graticula.Geometries.GeometryKind)> layers = [.. service.Layers
                .DistinctBy(l => l.Definition.Name, StringComparer.Ordinal)
                .Select(l => (l.Definition.Name, l.GeometryType))];

            // The symbology style first: every service has it, and it is the default while no stored style is.
            styles.Add(new OgcStyle(
                OgcStylesDocuments.StyleId(candidate.Id, null), candidate.Id, service.QualifiedName, service.Description,
                null, !named.Any(n => n.IsDefault), null, layers));

            foreach ((Guid _, string name, bool isDefault, DateTimeOffset? updated) in named)
            {
                styles.Add(new OgcStyle(
                    OgcStylesDocuments.StyleId(candidate.Id, name), candidate.Id, service.QualifiedName,
                    service.Description, name, isDefault, updated, layers));
            }
        }

        return styles;
    }

    private static OgcProblem NoSuchStyle(string styleId) =>
        OgcProblem.NotFound($"`{styleId}` is not a style this server offers you. See {Root}/styles.");

    /// <summary>The answer while the catalogue can say nothing — not a 404, which would be a claim.</summary>
    private static OgcProblem Unavailable => OgcProblem.Unavailable(
        "The catalogue is not reachable, so this server cannot say which styles it has. Retry shortly; see "
        + "/healthz/ready.");

    /// <summary>A JSON document, or its HTML page when the caller asked for one.</summary>
    private static Task AnswerAsync(HttpContext context, string title, byte[] document, string mediaType)
    {
        if (WantsHtml(context))
        {
            return HtmlAsync(context, title, document);
        }

        context.Response.ContentType = mediaType + "; charset=utf-8";
        return context.Response.Body.WriteAsync(document, context.RequestAborted).AsTask();
    }

    private static Task HtmlAsync(HttpContext context, string title, byte[] document) =>
        Results.Content(
                OgcHtml.Document(context.Request.Path, title, Encoding.UTF8.GetString(document)),
                "text/html; charset=utf-8")
            .ExecuteAsync(context);

    /// <summary><c>f</c> wins over <c>Accept</c>, as on the features and tiles faces.</summary>
    private static bool WantsHtml(HttpContext context)
    {
        string? asked = context.Request.Query["f"];

        if (!string.IsNullOrWhiteSpace(asked))
        {
            return string.Equals(asked, "html", StringComparison.OrdinalIgnoreCase);
        }

        return RestDirectory.WantsHtml(default, context.Request.Headers.Accept);
    }

    private static Task RefuseAsync(HttpContext context, OgcProblem problem)
    {
        context.Response.StatusCode = problem.Status;
        context.Response.ContentType = OgcNames.Problem;
        return context.Response.WriteAsync(problem.ToJson());
    }
}
