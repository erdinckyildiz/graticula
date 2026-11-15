using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// Two admin acts run inside another request, for ArcGIS Pro's reference publish — ADR-299 condition 1.
/// </summary>
/// <remarks>
/// <b>The same doors, not a copy of them.</b> A service published from Pro by reference goes through <c>POST
/// /admin/publish</c>'s own handler, and its hidden columns through <c>PUT /admin/layers/{name}/fields</c>'s, with the
/// caller's principal, scope and tenant: every refusal, privilege, address rule, replacement rule, audit line and
/// thumbnail those routes carry applies unchanged. D-46 is the reason — a second way in that does not carry what the
/// first way carries is the defect this repository has paid for most often.
/// </remarks>
internal static partial class AdminEndpoints
{
    /// <summary><c>POST /admin/publish</c> with <paramref name="request"/>, as <paramref name="caller"/>.</summary>
    internal static Task<(int Status, byte[] Body)> PublishCompositionInProcessAsync(
        HttpContext caller, CompositionRequest request, CancellationToken cancellation) =>
        InProcessAsync(caller, "/admin/publish", null, inner => PublishCompositionAsync(
            inner, request,
            Service<IAdminCatalog>(inner), Service<PostgresLayerCatalog>(inner), Service<PostgresSystemServices>(inner),
            Service<IProjector>(inner), Service<ServiceContexts>(inner), Service<ITileCache>(inner),
            Service<IAuditLog>(inner), Service<GeoParquetSources>(inner), Service<ThumbnailWarmer>(inner),
            cancellation));

    /// <summary><c>PUT /admin/layers/{layer}/fields?service={service}</c> with <paramref name="request"/>, as <paramref name="caller"/>.</summary>
    internal static Task<(int Status, byte[] Body)> SetFieldOverridesInProcessAsync(
        HttpContext caller, string layer, string service, FieldOverridesRequest request, CancellationToken cancellation) =>
        InProcessAsync(caller, $"/admin/layers/{Uri.EscapeDataString(layer)}/fields", $"?service={Uri.EscapeDataString(service)}",
            inner => SetFieldOverridesAsync(
                inner, layer, request,
                Service<PostgresLayerCatalog>(inner), Service<ServiceContexts>(inner), Service<IAdminCatalog>(inner),
                Service<IFieldDomainStore>(inner), Service<IAuditLog>(inner), cancellation));

    private static T Service<T>(HttpContext context)
        where T : notnull => context.RequestServices.GetRequiredService<T>();

    /// <summary>
    /// Runs <paramref name="handler"/> against a request that shares every feature of <paramref name="caller"/> — its
    /// principal, services, tenant and connection — except the request line and the response, which are its own.
    /// </summary>
    private static async Task<(int Status, byte[] Body)> InProcessAsync(
        HttpContext caller, string path, string? query, Func<HttpContext, Task> handler)
    {
        FeatureCollection features = new(caller.Features);
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = HttpMethods.Post,
            Scheme = caller.Request.Scheme,
            Protocol = caller.Request.Protocol,
            PathBase = string.Empty,
            Path = path,
            QueryString = query ?? string.Empty,
            Headers = new HeaderDictionary(),
            Body = Stream.Null,
        });

        using MemoryStream body = new();
        StreamResponseBodyFeature written = new(body);
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(written);

        DefaultHttpContext inner = new(features);
        features.Set<IQueryFeature>(new QueryFeature(inner.Features));

        await handler(inner).ConfigureAwait(false);
        await written.CompleteAsync().ConfigureAwait(false);

        return (inner.Response.StatusCode, body.ToArray());
    }
}
