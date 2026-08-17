using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Api.ArcGis;
using GisServer.Geometries;
using GisServer.Platform.Admin;
using GisServer.Platform.Catalog;
using GisServer.Platform.Postgres;
using GisServer.Tiles;
using GisServer.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace GisServer.Host;

/// <summary>A candidate data source to test or register.</summary>
/// <param name="Name">An administrator-chosen name. Not needed to test.</param>
/// <param name="ConnectionString">The credential, in the clear over TLS.</param>
internal sealed record DataSourceRequest(string? Name, string? ConnectionString);

/// <summary>A layer to publish.</summary>
/// <remarks>
/// <b><c>ServiceName</c> is the service to publish into, or null for a service
/// of this layer's own.</b> Naming an existing service adds this layer to it at
/// the next free index, which is how three related layers become one service —
/// owner correction 2026-08-15, <em>"a service is a combination of layers"</em>.
/// Omitting it keeps the behaviour every layer published before that date got.
/// </remarks>
internal sealed record PublishRequest(
    string? Name,
    Guid DataSourceId,
    string? SchemaName,
    string? TableName,
    string? GeometryColumn,
    string? IdentityColumn,
    string? ObjectIdColumn,
    int Srid,
    string? GeometryType,
    string? Sharing,
    string? ServiceName = null,
    int? ParentLayerId = null,
    int? CacheSeconds = null);

/// <summary>A group layer to create inside a service.</summary>
/// <param name="Name">What to call it.</param>
/// <param name="Folder">Which folder the service is in, or null for the root.</param>
/// <param name="ParentLayerId">A group to nest it under, or null for the top level.</param>
internal sealed record GroupLayerRequest(string? Name, string? Folder, int? ParentLayerId);

/// <summary>An empty service to create.</summary>
/// <param name="Name">Its name within the folder.</param>
/// <param name="Folder">Its folder, or null for the root.</param>
/// <param name="Description">What it is for, or null.</param>
/// <param name="Sharing">Who may read it. Private unless said otherwise.</param>
internal sealed record ServiceRequest(
    string? Name, string? Folder, string? Description, string? Sharing);

/// <summary>A change of sharing scope.</summary>
internal sealed record SharingRequest(string? Sharing);

/// <summary>
/// What a service is configured to offer. Null means unset, everywhere.
/// </summary>
/// <param name="Folder">Its folder, or null for the root.</param>
/// <param name="ServesFeatures">Whether the feature face is offered, or null for unset.</param>
/// <param name="ServesTiles">Whether the tile face is offered, or null for unset.</param>
/// <param name="Capabilities">
/// The ceiling, or null for unset. An **empty array is not null**: it means this
/// service offers nothing, which ADR-031 §2a keeps as a legitimate state distinct
/// from stopped.
/// </param>
/// <param name="StatementTimeoutMilliseconds">
/// A timeout this service asks for, or null for the source's own. May only lower.
/// </param>
/// <param name="MaxRecordCount">Most rows one response may carry, or null.</param>
/// <param name="DefaultRecordCount">Rows when the caller does not ask, or null.</param>
/// <param name="MaxResponseBytes">Most bytes one response body may reach, or null.</param>
/// <param name="MaxRequestBytes">Most bytes one request body may carry, or null.</param>
/// <param name="MaxEditsPerTransaction">Most edits one applyEdits may carry, or null.</param>
internal sealed record ServiceCapabilitiesRequest(
    string? Folder,
    bool? ServesFeatures,
    bool? ServesTiles,
    IReadOnlyList<string>? Capabilities,
    int? StatementTimeoutMilliseconds,
    int? MaxRecordCount = null,
    int? DefaultRecordCount = null,
    long? MaxResponseBytes = null,
    long? MaxRequestBytes = null,
    int? MaxEditsPerTransaction = null);

/// <summary>How long a layer's tiles stay fresh.</summary>
/// <param name="Seconds">
/// Seconds, or null to fall back to the server default. <b>Zero is not
/// null</b>: zero means never serve a cached tile, which is a real answer for
/// a layer that changes continuously.
/// </param>
internal sealed record CacheLifetimeRequest(int? Seconds);

/// <summary>
/// The administrative surface (ADR-017).
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate prefix, versioned separately</b> (§5a), because the admin
/// surface will change far faster than the data APIs.
/// </para>
/// <para>
/// <b>Every mutating call is audited</b> (§5d), including the ones that fail.
/// A register of successful actions answers <em>who did this</em> and cannot
/// answer <em>who tried</em>, and during an incident the second is the question.
/// </para>
/// <para>
/// <b>What is deliberately not here yet</b>, so the gap is visible rather than
/// assumed: §5b's rule that long operations return <c>202</c> and a job id. The
/// job system (ADR-011) is not built, and every operation below completes in one
/// round trip, so returning a job identifier would mean inventing a job resource
/// that is already finished. When registration grows a schema crawl, this
/// becomes wrong and §5b applies.
/// </para>
/// </remarks>
internal static class AdminEndpoints
{
    /// <summary>Maps the admin surface.</summary>
    public static void MapAdmin(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/admin/health", HealthAsync);
        app.MapPost("/admin/datasources/test", TestDataSourceAsync);
        app.MapPost("/admin/datasources", RegisterDataSourceAsync);
        app.MapGet("/admin/datasources", ListDataSourcesAsync);
        app.MapGet("/admin/datasources/{id:guid}/capability", CapabilityAsync);
        app.MapPost("/admin/layers", PublishAsync);
        app.MapGet("/admin/layers", ListLayersAsync);
        app.MapPut("/admin/layers/{name}/sharing", SetSharingAsync);
        app.MapPut("/admin/layers/{name}/cache", SetCacheLifetimeAsync);
        app.MapPost("/admin/layers/{name}/start", (HttpContext c, string name, IAdminCatalog a, IAuditLog l, CancellationToken t) =>
            SetStatusAsync(c, name, ServiceStatus.Started, a, l, t));
        app.MapPost("/admin/layers/{name}/stop", (HttpContext c, string name, IAdminCatalog a, IAuditLog l, CancellationToken t) =>
            SetStatusAsync(c, name, ServiceStatus.Stopped, a, l, t));
        app.MapDelete("/admin/layers/{name}", UnpublishAsync);
        app.MapPost("/admin/layers/{name}/refresh", RefreshAsync);

        // A service that is not a layer, shared the same way. Owner correction
        // 2026-08-15: "we might make all services public, private or
        // organization" — including the geometry service, which has no layer
        // and was therefore governed by nothing.
        app.MapGet("/admin/services", ListSystemServicesAsync);
        app.MapPut("/admin/services/{name}/sharing", SetServiceSharingAsync);
        app.MapPut("/admin/services/{name}/capabilities", SetServiceCapabilitiesAsync);

        // Group layers. Owner request 2026-08-15: "enable group layers also."
        app.MapGet("/admin/routes", ListRoutesAsync);
        app.MapPost("/admin/featureservices", CreateServiceAsync);
        app.MapPost("/admin/services/{name}/groups", CreateGroupLayerAsync);

        // <b>The style, which is the one thing about a map this server cannot
        // guess.</b> ADR-028. GET is here rather than only on the public
        // resource so that an author can read back exactly what they stored,
        // including a null meaning "still the generated one".
        app.MapGet("/admin/services/{name}/style", GetStyleAsync);
        app.MapPut("/admin/services/{name}/style", SetStyleAsync);
        app.MapDelete("/admin/services/{name}/style", DeleteStyleAsync);
    }

    /// <summary>
    /// Every route, and what governs who may reach it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-28, and it exists because an absence has nothing for a reviewer to
    /// look at.</b> The geometry service answered anonymously from the day it
    /// shipped, and no amount of reading the sharing code would have found it —
    /// the sharing code was correct. What was missing was a place for something
    /// that is not content. This turns "is anything ungoverned?" from a question
    /// somebody has to think of into a list they can read, and a test can assert
    /// is empty.
    /// </para>
    /// <para>
    /// <b>Administrative, because it enumerates every route including the ones
    /// a caller may not reach.</b> That is the opposite of what the directory
    /// does, and the reason it is not on the directory.
    /// </para>
    /// </remarks>
    private static async Task ListRoutesAsync(
        HttpContext context, EndpointDataSource endpoints, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        List<object> routes = [];

        foreach (Endpoint endpoint in endpoints.Endpoints)
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            string pattern = route.RoutePattern.RawText ?? string.Empty;

            // Only the data surface. /admin is governed by privileges, which is
            // a different mechanism with its own tests, and /healthz is
            // deliberately open.
            if (!pattern.StartsWith("rest/services", StringComparison.OrdinalIgnoreCase)
                && !pattern.StartsWith("/rest/services", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SharingGoverned? governed = endpoint.Metadata.GetMetadata<SharingGoverned>();

            routes.Add(new
            {
                pattern = pattern.StartsWith('/') ? pattern : "/" + pattern,
                methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    ?? (IReadOnlyList<string>)["*"],
                governed = governed is not null,
                by = governed?.Source,
            });
        }

        await Results.Json(new
        {
            routes = routes.OrderBy(r => r.GetType().GetProperty("pattern")!.GetValue(r) as string,
                StringComparer.Ordinal),

            // Stated rather than left for the reader to count. A non-zero number
            // here is ADR-018 condition 5 failing.
            ungoverned = routes.Count(r =>
                !(bool)r.GetType().GetProperty("governed")!.GetValue(r)!),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an empty service, ready for groups and layers.
    /// </summary>
    /// <remarks>
    /// <b>At <c>/admin/featureservices</c>, not <c>/admin/services</c>.</b> That
    /// path already lists the system services — the geometry service and
    /// whatever joins it — and those are a different kind of thing: they have no
    /// layers, are not published by anybody, and cannot be created. One path
    /// covering both would make <c>GET</c> and <c>POST</c> on the same URL
    /// return and accept unrelated shapes.
    /// </remarks>
    private static async Task CreateServiceAsync(
        HttpContext context,
        ServiceRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await Refuse(context, 400, "'name' is required.").ConfigureAwait(false);
            return;
        }

        SharingScope scope = SharingScope.Private;

        if (request.Sharing is not null
            && !TryReadScope(request.Sharing, out scope, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        // Opening a service to the public is the same act whether it is done at
        // creation or afterwards, so it takes the same privilege.
        if (scope != SharingScope.Private)
        {
            Privilege needed = scope == SharingScope.Public
                ? Privilege.SharingShareToPublic
                : Privilege.SharingShareToOrganization;

            if (!await Authorize.RequireAsync(context, needed).ConfigureAwait(false))
            {
                return;
            }
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        string? folder = string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim();
        string name = request.Name.Trim();

        Guid? id = await catalog.CreateServiceAsync(
            name, folder, request.Description, scope, current.Principal.Id, cancellation)
            .ConfigureAwait(false);

        if (id is not { } created)
        {
            await AuditAsync(
                context, audit, "service.create", name,
                Detail(new { folder }), succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 409,
                $"A service named '{name}' already exists"
                + (folder is null ? " at the root." : $" in folder '{folder}'."))
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.create", name,
            Detail(new { folder, sharing = PostgresSharing(scope) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(
            new
            {
                id = created,
                name,
                folder,
                sharing = PostgresSharing(scope),
                url = folder is null
                    ? $"/rest/services/{name}/FeatureServer"
                    : $"/rest/services/{folder}/{name}/FeatureServer",
                note = "The service has no layers yet. Add group layers with "
                     + $"POST /admin/services/{name}/groups, and layers by publishing with "
                     + "serviceName set to this service.",
            },
            statusCode: StatusCodes.Status201Created)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a group layer inside a service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Publishing, not administration.</b> Adding a group is arranging your
    /// own content, which is what <c>content:publishFeatures</c> covers — the
    /// same privilege that put the layers there. Requiring an administrative
    /// privilege to tidy them into folders would mean the person who published
    /// three layers cannot group them.
    /// </para>
    /// <para>
    /// <b>A bad parent is refused by the database, and the message says so
    /// plainly.</b> The foreign key requires the parent index to name a group in
    /// the same service, so naming a feature layer as a parent — which no client
    /// knows how to draw — cannot be written at all.
    /// </para>
    /// </remarks>
    private static async Task CreateGroupLayerAsync(
        HttpContext context,
        string name,
        GroupLayerRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await Refuse(context, 400, "'name' is required.").ConfigureAwait(false);
            return;
        }

        try
        {
            GroupLayerAddress? created = await catalog.CreateGroupLayerAsync(
                string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim(),
                name,
                request.Name.Trim(),
                request.ParentLayerId,
                cancellation).ConfigureAwait(false);

            if (created is not { } address)
            {
                await Refuse(context, 404,
                    $"No service '{name}'"
                    + (string.IsNullOrWhiteSpace(request.Folder)
                        ? " at the root. Pass 'folder' if it is in one, e.g. \"hosted\"."
                        : $" in folder '{request.Folder}'.")).ConfigureAwait(false);
                return;
            }

            await AuditAsync(
                context, audit, "service.group.create", $"{name}/{address.LayerIndex}",
                Detail(new { group = request.Name, parent = request.ParentLayerId }),
                succeeded: true, cancellation).ConfigureAwait(false);

            await Results.Json(
                new
                {
                    id = address.Id,
                    name = request.Name,
                    layerId = address.LayerIndex,
                    parentLayerId = request.ParentLayerId ?? -1,
                    type = "Group Layer",
                },
                statusCode: StatusCodes.Status201Created)
                .ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState is "23503" or "23514")
        {
            await AuditAsync(
                context, audit, "service.group.create", name,
                Detail(new { sqlState = e.SqlState }), succeeded: false, cancellation)
                .ConfigureAwait(false);

            await Refuse(context, 400,
                e.SqlState == "23503"
                    ? $"Layer {request.ParentLayerId} of '{name}' is not a group layer, or does "
                      + "not exist. A group can only be nested inside another group — a feature "
                      + "layer cannot contain anything, and no client would know how to draw it."
                    : "A group layer cannot be its own parent.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets a layer's tile cache lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-25, and the number is domain knowledge rather than tuning.</b>
    /// [ADR-010](../../docs/adr/ADR-010-caching.md) §5.3 says volatility is a
    /// per-layer property set by the administrator, and A-028 records why: a
    /// cadastral layer changes twice a year, an incident layer changes every
    /// minute, and nobody but the person who publishes them knows which is
    /// which. Until now it was one global hour, wrong in both directions.
    /// </para>
    /// <para>
    /// <b>Publishing, not administration.</b> Whoever published the layer knows
    /// how often it changes; requiring a server administrator to set it would
    /// put the decision with the person who has the least information.
    /// </para>
    /// </remarks>
    private static async Task SetCacheLifetimeAsync(
        HttpContext context,
        string name,
        CacheLifetimeRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTiles)
            .ConfigureAwait(false))
        {
            return;
        }

        if (request.Seconds is < 0)
        {
            await Refuse(context, 400,
                "'seconds' cannot be negative. Use 0 for 'never serve a cached tile', or omit it "
                + "to fall back to the server default.").ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetCacheLifetimeAsync(name, request.Seconds, cancellation)
            .ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "layer.cache", name,
            Detail(new { seconds = request.Seconds }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            cacheSeconds = request.Seconds,
            note = request.Seconds switch
            {
                null => "This layer now uses the server's default tile lifetime.",
                0 => "Tiles for this layer are never served from cache, and Cache-Control says "
                     + "no-store so nothing downstream keeps one either.",
                _ => "Tiles for this layer expire after this many seconds, here and in every "
                     + "cache downstream — the same number is sent as Cache-Control max-age. "
                     + "Nothing was purged: changing freshness does not make a cached tile wrong.",
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Every service that is not a layer, and how it is shared.</summary>
    private static async Task ListSystemServicesAsync(
        HttpContext context, PostgresSystemServices services, CancellationToken cancellation)
    {
        // Reading the list is an administrative act: it enumerates services
        // regardless of their sharing, which is exactly what the directory does
        // not do.
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        await Results.Json(new
        {
            services = (await services.ListAsync(cancellation).ConfigureAwait(false))
                .Select(s => new
                {
                    s.Name,
                    s.Kind,
                    s.Folder,
                    sharing = PostgresSharing(s.Sharing),
                }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Changes who may use a service that has no layer.</summary>
    /// <remarks>
    /// <b>The same privileges as a layer's sharing, for the same reason.</b>
    /// Opening the geometry service to the public is the same act as opening a
    /// layer to the public — it makes a server resource reachable without an
    /// account — so it takes <c>sharing:shareToPublic</c>, not a separate
    /// administrative privilege that would let one be granted without the other.
    /// </remarks>
    /// <summary>Reads back the stored style, or says there is none.</summary>
    private static async Task GetStyleAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindServiceForStyleAsync(name, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        if (service.Style is null)
        {
            await Results.Json(new
            {
                name = service.Name,
                stored = false,
                sourceLayers = service.SourceLayers,
                note = "This service has no stored style, so it serves a generated one: every "
                     + "layer in publication order, one colour per geometry type, no labels. "
                     + "PUT a style document here to replace it.",
            }).ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        // The document as it was stored, byte for byte. An author diffing this
        // against their file should see nothing.
        await Results.Content(service.Style, "application/json; charset=utf-8")
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a style, having checked it against the service it is for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read as text, not bound as a model.</b> A style is an open-ended
    /// document — the specification allows properties this server has never
    /// heard of, and a client that understands them should get them back. Binding
    /// it to a type would silently drop everything the type does not know, which
    /// is the worst possible failure for a document somebody hand-wrote.
    /// </para>
    /// <para>
    /// <b>content:publishFeatures, not an administrator privilege.</b> Styling
    /// a service is a publisher's job, and the person who published a layer is
    /// the person who knows what colour it should be.
    /// </para>
    /// </remarks>
    private static async Task SetStyleAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindServiceForStyleAsync(name, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        string body;

        // <b>Bounded before it is read, not after.</b> Reading an unbounded body
        // and then measuring it is an accounting exercise: the memory is already
        // spent. The cap is one more byte than the limit so that a document
        // exactly at the limit is accepted and one over is refused.
        using (System.IO.StreamReader reader = new(context.Request.Body))
        {
            char[] buffer = new char[StyleDocument.MaximumBytes + 1];
            int read = 0;

            while (read < buffer.Length)
            {
                int got = await reader
                    .ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellation)
                    .ConfigureAwait(false);

                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            body = new string(buffer, 0, read);
        }

        if (!StyleDocument.TryValidate(body, service.SourceLayers, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetStyleAsync(name, body, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        // The document is not in the audit record. It can be a megabyte, it is
        // readable through the API anyway, and an audit log that copies its
        // subject is a second place to keep the same thing correct.
        await AuditAsync(
            context, audit, "service.style", name,
            Detail(new { bytes = body.Length, replaced = service.Style is not null }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            stored = true,
            bytes = body.Length,
            replaced = service.Style is not null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Drops the stored style, returning the service to the generated one.</summary>
    private static async Task DeleteStyleAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await catalog.FindServiceForStyleAsync(name, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        await catalog.SetStyleAsync(name, null, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "service.style.clear", name,
            Detail(new { had = service.Style is not null }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            stored = false,
            had = service.Style is not null,
            note = "Back to the generated style.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task SetServiceSharingAsync(
        HttpContext context,
        string name,
        SharingRequest request,
        PostgresSystemServices services,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryReadScope(request.Sharing, out SharingScope scope, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        Privilege needed = scope == SharingScope.Public
            ? Privilege.SharingShareToPublic
            : Privilege.SharingShareToOrganization;

        if (!await Authorize.RequireAsync(context, needed).ConfigureAwait(false))
        {
            return;
        }

        SystemService? before = await services.FindAsync(name, cancellation).ConfigureAwait(false);

        if (!await services.SetSharingAsync(name, scope, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No service '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.share", name,
            Detail(new
            {
                from = before is { } b ? PostgresSharing(b.Sharing) : null,
                to = PostgresSharing(scope),
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { name, sharing = PostgresSharing(scope) })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores what a service offers — a ceiling, never a grant (ADR-031).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The privilege is <c>admin:manageServer</c>, not a sharing one.</b>
    /// Narrowing what a service does is server administration; it cannot widen who
    /// may read — that is sharing, and ADR-031 §2b keeps the two apart so there are
    /// not two controls over one fact.
    /// </para>
    /// <para>
    /// <b>The refusals are the domain's own words.</b> An unknown capability name
    /// and a non-positive timeout are both refused by
    /// <see cref="ServiceCapabilityLimits"/>, which explains why — an unrecognised
    /// name would be dropped by the intersection, and a zero timeout is how
    /// PostgreSQL spells *no limit*. Catching its exception and returning its
    /// message keeps one explanation rather than a second one written here that can
    /// drift from it.
    /// </para>
    /// </remarks>
    private static async Task SetServiceCapabilitiesAsync(
        HttpContext context,
        string name,
        ServiceCapabilitiesRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        ServiceCapabilityLimits limits;

        try
        {
            limits = new ServiceCapabilityLimits(
                request.ServesFeatures,
                request.ServesTiles,
                request.Capabilities,
                request.StatementTimeoutMilliseconds is { } ms
                    ? TimeSpan.FromMilliseconds(ms)
                    : null)
                .With(new ServiceCostCeilings(
                    request.MaxRecordCount,
                    request.DefaultRecordCount,
                    request.MaxResponseBytes,
                    request.MaxRequestBytes,
                    request.MaxEditsPerTransaction));
        }
        catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException)
        {
            await Refuse(context, 400, e.Message).ConfigureAwait(false);
            return;
        }

        string? folder = string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder.Trim();

        if (!await catalog
            .SetServiceCapabilitiesAsync(name, folder, limits, cancellation)
            .ConfigureAwait(false))
        {
            await AuditAsync(
                context, audit, "service.capabilities", name,
                Detail(new { folder }), succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 404,
                $"No service '{name}'" + (folder is null ? " at the root." : $" in folder '{folder}'."))
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.capabilities", name,
            Detail(new
            {
                folder,
                servesFeatures = limits.ServesFeatures,
                servesTiles = limits.ServesTiles,
                capabilities = limits.Ceiling,
                statementTimeoutMs = limits.StatementTimeout?.TotalMilliseconds,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            folder,
            servesFeatures = limits.ServesFeatures,
            servesTiles = limits.ServesTiles,
            capabilities = limits.Ceiling,
            statementTimeoutMs = limits.StatementTimeout is { } t ? (int?)t.TotalMilliseconds : null,
            maxRecordCount = limits.Cost.MaximumRecordCount,
            defaultRecordCount = limits.Cost.DefaultRecordCount,
            maxResponseBytes = limits.Cost.MaximumResponseBytes,
            maxRequestBytes = limits.Cost.MaximumRequestBytes,
            maxEditsPerTransaction = limits.Cost.MaximumEditsPerTransaction,

            // <b>Said back, because a ceiling is easy to misread as a grant.</b> An
            // operator who ticks Update on a service whose readers lack the
            // privilege will see no change in behaviour, and this is where that is
            // explained rather than in a document they are not reading.
            note = limits.IsUnset
                ? "Nothing is configured, so this service offers whatever its data supports and "
                  + "its callers' privileges allow."
                : "These are limits, not grants: a caller still needs the privilege. What is "
                  + "served is the intersection of the data, this configuration, and the caller.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Health, and it must answer when the platform store does not.
    /// </summary>
    /// <remarks>
    /// ADR-017 §6, and ADR-019 §4 made it the test of whether the seam between
    /// the catalogue and the runtime is real. <b>A 500 from the admin API during
    /// an outage is the worst possible response</b>, because it removes the only
    /// tool the administrator has left.
    /// </remarks>
    private static async Task HealthAsync(
        HttpContext context,
        IAdminCatalog catalog,
        ServiceContexts contexts,
        ITileCache tiles,
        TileSingleFlight flight,
        CancellationToken cancellation)
    {
        string? storeError = null;
        int layers = 0;

        try
        {
            layers = (await catalog.ListLayersAsync(cancellation).ConfigureAwait(false)).Count;
        }
        catch (NpgsqlException e)
        {
            storeError = e.Message;
        }

        // <b>Reachable without authentication, and therefore redacted.</b> It
        // has to be anonymous: sessions live in the platform store, so during
        // the outage this endpoint exists for, nobody can authenticate. That
        // makes the raw error a disclosure to anyone who can reach the port —
        // it named the store's host and port until this was noticed — so the
        // detail is shown only to a caller who has proved they operate the
        // server, and during an outage that is nobody. D-03's rule, and the
        // reason ADR-017 §6's break-glass path is still owed (A-051).
        //
        // <b>The redaction covered the error and nothing else, until the §66
        // security gate on 2026-08-15.</b> An anonymous caller was told there
        // are 26 layers while the catalogue showed them 2 services — so this
        // endpoint published the existence of private content, on a server that
        // answers 404 for a private layer precisely so that nobody can learn it
        // is there. Every other surface refuses to confirm; this one counted.
        //
        // So an anonymous caller now gets what the endpoint is for and nothing
        // else: is it alive, and is the store reachable. Inventory, cache state
        // and our own version are operational detail, and operational detail is
        // for operators.
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool maySeeDetail = current.Authorization.Allows(Privilege.AdminManageServer);

        Dictionary<string, object?> health = new()
        {
            ["status"] = storeError is null ? "ok" : "degraded",

            ["platformStore"] = new
            {
                reachable = storeError is null,
                error = maySeeDetail ? storeError : storeError is null ? null : "redacted",
            },
        };

        if (!maySeeDetail)
        {
            health["note"] =
                "Liveness only. Counts, cache state and the server version are shown to a caller "
                + "holding admin:manageServer, because an inventory is a disclosure about content "
                + "this server otherwise refuses to confirm exists.";

            await Results.Json(health).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        health["version"] =
            typeof(AdminEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown";

        // <b>The counters a load run needs to tell an allocation ceiling from a
        // connection-pool limit.</b> Four benchmark rounds concluded that this
        // system's cost is memory traffic and that it is invisible at
        // concurrency 1; the §66 performance gate then failed on F1 because the
        // feature path had never been measured, and a black-box probe could say
        // only that throughput plateaued. Throughput alone cannot distinguish a
        // server that is out of memory bandwidth from one that is out of
        // connections from one sharing a host with something else. Allocation
        // rate, GC pause share and CPU can.
        //
        // <b>Here rather than at /metrics, and that is the decision.</b> The
        // experiment harness had a public /metrics; this route already exists,
        // already resolves a principal, and already redacts everything below
        // this line from a caller without admin:manageServer. Adding a second
        // surface would mean a second authorization story for the same
        // disclosure — and D-03 records over-disclosure to unauthenticated
        // callers as open debt, so a new anonymous endpoint reporting heap size
        // and uptime is the wrong direction.
        //
        // <b>Sampled either side of a run and subtracted</b>, which is why they
        // are cumulative totals rather than rates. A rate computed here would be
        // a rate over the server's whole life, which is not a measurement of
        // anything a caller is doing.
        Process self = Process.GetCurrentProcess();

        health["runtime"] = new
        {
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
            heapBytes = GC.GetTotalMemory(forceFullCollection: false),

            // Not a GC "count" per se: these are cumulative collection counts by
            // generation, so a difference over a run is how many happened during
            // it.
            gen0 = GC.CollectionCount(0),
            gen1 = GC.CollectionCount(1),
            gen2 = GC.CollectionCount(2),

            // <b>The number four rounds kept coming back to.</b> One measured
            // 80.9% of wall time in GC pause at 18% CPU: a server doing almost
            // nothing and stopped most of the time.
            gcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds,

            cpuMilliseconds = self.TotalProcessorTime.TotalMilliseconds,
            uptimeMilliseconds = (DateTimeOffset.UtcNow - self.StartTime.ToUniversalTime())
                .TotalMilliseconds,
            cores = Environment.ProcessorCount,
            serverGc = System.Runtime.GCSettings.IsServerGC,
        };

        health["platformStore"] = new
        {
            reachable = storeError is null,
            layers,
            error = storeError,
        };

        // Not a vanity statistic. This is the one piece of state the request
        // path carries between requests, and a cache nobody can see is a
        // cache nobody suspects when the answers go stale. The lifetime is
        // reported alongside the count so an operator reading a wrong field
        // list knows exactly how long to wait, or that /refresh exists.
        // ADR-010 §6b: cache state must be readable. An operator asking
        // "is this seeded" or "why is the disk full" has no other way to
        // find out, and a cache nobody can see is one nobody suspects when
        // the datastore starts working harder than it should.
        health["tileCache"] = new
        {
            entries = tiles.Report(null).Entries,
            megabytes = Math.Round(tiles.Report(null).Bytes / 1048576.0, 1),

            // Builds in flight. A number that stays high is a datastore that
            // cannot keep up with cold tiles, which looks like a slow server and
            // is actually a slow query.
            building = flight.InFlight,
            note = "Tiles held on disk. A miss is datastore load (ADR-021), so this is the "
                 + "number that says how much of the tile traffic the datastore is actually "
                 + "seeing. X-Tile-Cache on a tile response says HIT or MISS.",
        };

        health["describedShapes"] = new
        {
            count = contexts.Count,
            lifetimeSeconds = (int)ServiceContexts.Lifetime.TotalSeconds,
            note = "Table shapes remembered from the data source (D-17). Sharing and "
                 + "started/stopped are deliberately NOT cached and are read per request. "
                 + "POST /admin/layers/{name}/refresh to forget one immediately.",
        };

        // Said explicitly in the degraded case, because an administrator
        // reading this during an outage should not have to infer which half
        // of the answer they are allowed to trust.
        health["note"] = storeError is null
            ? "The platform store is reachable, so everything below is current."
            : "The platform store is unreachable. Serving continues for public layers already "
              + "seen (ADR-026), but the catalogue, identity and audit are unavailable, so most "
              + "of this API will refuse. This is the failure ADR-019 accepted when it fused the "
              + "catalogue and the runtime into one deployable.";

        await Results.Json(health).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-017 §3.3's dry run: connect, check rights, list what is publishable,
    /// and create nothing.
    /// </summary>
    private static async Task TestDataSourceAsync(
        HttpContext context,
        DataSourceRequest request,
        IDataSourceProbe probe,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            await Refuse(context, 400, "connectionString is required.").ConfigureAwait(false);
            return;
        }

        ProbeResult result =
            await probe.ProbeAsync(request.ConnectionString, cancellation).ConfigureAwait(false);

        // 200 even when the source is unusable. The request succeeded — the
        // answer is "no", and it is the answer that was asked for. A 4xx here
        // would make a working diagnostic look like a broken call.
        await Results.Json(Describe(result)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RegisterDataSourceAsync(
        HttpContext context,
        DataSourceRequest request,
        IAdminCatalog catalog,
        IDataSourceProbe probe,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            await Refuse(context, 400, "name and connectionString are required.").ConfigureAwait(false);
            return;
        }

        ProbeResult result =
            await probe.ProbeAsync(request.ConnectionString, cancellation).ConfigureAwait(false);

        // Probed before writing. ADR-017 §3.3's point is that registration
        // should not leave a broken row behind for somebody to find later — and
        // a source we cannot even connect to is broken by any reading.
        if (result.Outcome == ProbeOutcome.CannotConnect)
        {
            await AuditAsync(
                context, audit, "datasource.register", request.Name,
                Detail(new { outcome = result.Outcome.ToString(), reason = result.Message }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 400, result.Message).ConfigureAwait(false);
            return;
        }

        Guid id;

        try
        {
            id = await catalog
                .RegisterDataSourceAsync(request.Name, "postgis", request.ConnectionString, cancellation)
                .ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            await AuditAsync(
                context, audit, "datasource.register", request.Name,
                Detail(new { outcome = "duplicate" }), succeeded: false, cancellation)
                .ConfigureAwait(false);

            await Refuse(context, 409, $"A data source named '{request.Name}' already exists.")
                .ConfigureAwait(false);
            return;
        }

        // The detail records host and database and never the credential. An
        // audit log that leaks what it audits is a new place to steal from.
        await AuditAsync(
            context, audit, "datasource.register", request.Name,
            Detail(new
            {
                id,
                summary = Summarise(request.ConnectionString),
                outcome = result.Outcome.ToString(),
                publishable = result.Tables.Count,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(
            new
            {
                id,
                name = request.Name,
                probe = Describe(result),
            },
            statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ListDataSourcesAsync(
        HttpContext context, IAdminCatalog catalog, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<RegisteredDataSource> sources =
            await catalog.ListDataSourcesAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            dataSources = sources.Select(s => new { s.Id, s.Name, s.Kind, s.LayerCount }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-017 §3.3 step 4: the honest list of what we could do with this
    /// source, given the rights actually granted.
    /// </summary>
    private static async Task CapabilityAsync(
        HttpContext context,
        Guid id,
        IAdminCatalog catalog,
        IDataSourceProbe probe,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentRegisterDataStore)
            .ConfigureAwait(false))
        {
            return;
        }

        string? connectionString =
            await catalog.ConnectionStringOfAsync(id, cancellation).ConfigureAwait(false);

        if (connectionString is null)
        {
            await Refuse(context, 404, $"No data source '{id}'.").ConfigureAwait(false);
            return;
        }

        ProbeResult result = await probe.ProbeAsync(connectionString, cancellation).ConfigureAwait(false);

        await Results.Json(Describe(result)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task PublishAsync(
        HttpContext context,
        PublishRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (!TryReadPublication(request, out LayerPublication? publication, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        // Publishing straight to public needs the privilege that puts data on
        // the internet, separately from the one that publishes at all.
        if (publication!.Sharing == SharingScope.Public
            && !await Authorize.RequireAsync(context, Privilege.SharingShareToPublic)
                .ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        try
        {
            PublishedLayerAddress published = await catalog
                .PublishLayerAsync(publication, current.Principal.Id, cancellation)
                .ConfigureAwait(false);

            await AuditAsync(
                context, audit, "layer.publish", publication.Name,
                Detail(new
                {
                    id = published.Id,
                    service = published.ServiceName,
                    layer = published.LayerIndex,
                    table = $"{publication.SchemaName}.{publication.TableName}",
                    sharing = PostgresSharing(publication.Sharing),
                    arcGisServable = publication.ObjectIdColumn is not null,
                }),
                succeeded: true, cancellation).ConfigureAwait(false);

            await Results.Json(
                new
                {
                    id = published.Id,
                    name = publication.Name,

                    // Where it actually is, which is no longer derivable from
                    // its name: a layer added to an existing service is an index
                    // inside that service.
                    service = published.ServiceName,
                    layerId = published.LayerIndex,
                    sharing = PostgresSharing(publication.Sharing),
                    arcGisServable = publication.ObjectIdColumn is not null,

                    // ADR-013 §2a, said at publish time rather than discovered
                    // at the first query by somebody who cannot fix it.
                    note = publication.ObjectIdColumn is null
                        ? "No integer object-id column was given, so this layer is not servable "
                          + "through the ArcGIS surface. It remains servable natively."
                        : null,
                },
                statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState is "23505" or "23503" or "23514")
        {
            await AuditAsync(
                context, audit, "layer.publish", publication.Name,
                Detail(new { sqlState = e.SqlState }), succeeded: false, cancellation)
                .ConfigureAwait(false);

            await Refuse(context, 409, Conflict(e, publication)).ConfigureAwait(false);
        }
    }

    private static async Task ListLayersAsync(
        HttpContext context, IAdminCatalog catalog, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminViewAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<AdminLayer> layers =
            await catalog.ListLayersAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            layers = layers.Select(l => new
            {
                l.Id,
                l.Name,
                dataSource = l.DataSourceName,
                table = l.Qualified,
                sharing = PostgresSharing(l.Sharing),
                status = Wire(l.Status),
                owner = l.OwnerName,
                l.ArcGisServable,

                // So the console can offer tiles only where they exist. Showing
                // the control everywhere and letting it 400 teaches people that
                // the button sometimes does not work, which is worse than not
                // having it.
                l.Hosted,
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <remarks>
    /// <para>
    /// <b>This does not purge the tile cache, and ADR-010 §5.1 says it should.</b>
    /// That row — *permissions changed → purge, and this one is a security
    /// matter* — was written before §4 settled how tile keys work, and for tiles
    /// the two do not agree. §4's rule is that authorization for a tile is
    /// <em>uniform</em>: a layer is readable or it is not, the check runs
    /// <em>before</em> the cache lookup, and every authorized caller shares one
    /// entry. So a sharing change cannot make a single cached byte wrong. It
    /// changes who reaches the cache, not what the cache holds.
    /// </para>
    /// <para>
    /// Purging anyway would throw away a whole seeded pyramid every time
    /// somebody moved a layer from organisation to public — the most ordinary
    /// administrative act there is — for no correctness gain at all.
    /// </para>
    /// <para>
    /// <b>What would change this:</b> row-level or field-level filtering, where
    /// the effective grant alters the output. §4 already answers that case with
    /// a grant fingerprint in the key, which makes the invalidation structural
    /// rather than a sweep. Neither exists yet. When one does, this comment is
    /// wrong and the fingerprint is the fix, not a purge here.
    /// </para>
    /// </remarks>
    private static async Task SetSharingAsync(
        HttpContext context,
        string name,
        SharingRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!TryReadScope(request.Sharing, out SharingScope scope, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        Privilege needed = scope == SharingScope.Public
            ? Privilege.SharingShareToPublic
            : Privilege.SharingShareToOrganization;

        if (!await Authorize.RequireAsync(context, needed).ConfigureAwait(false))
        {
            return;
        }

        // Read first, so the audit record can say what it was as well as what it
        // became. ADR-017 §5d asks for before and after, and after alone answers
        // "what is it now" — which anybody can see — rather than "what changed".
        AdminLayer? before = (await catalog.ListLayersAsync(cancellation).ConfigureAwait(false))
            .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal)) is
            { Name: not null } found ? found : null;

        AdminLayer? after = await catalog
            .SetSharingAsync(name, scope, cancellation).ConfigureAwait(false);

        if (after is null)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "layer.share", name,
            Detail(new
            {
                from = before is { } b ? PostgresSharing(b.Sharing) : null,
                to = PostgresSharing(scope),
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            from = before is { } was ? PostgresSharing(was.Sharing) : null,
            to = PostgresSharing(scope),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts or stops a service (ADR-020 §3).
    /// </summary>
    /// <remarks>
    /// <b><c>admin:manageServer</c>, not the publisher privilege.</b> Publishing
    /// is a content act; stopping a running service is an operational one that
    /// affects every consumer of it, including people the publisher has never
    /// met.
    /// </remarks>
    private static async Task SetStatusAsync(
        HttpContext context,
        string name,
        ServiceStatus status,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        ServiceStatus? previous =
            await catalog.SetStatusAsync(name, status, cancellation).ConfigureAwait(false);

        if (previous is null)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, status == ServiceStatus.Started ? "layer.start" : "layer.stop", name,
            Detail(new { from = Wire(previous.Value), to = Wire(status) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            from = Wire(previous.Value),
            to = Wire(status),

            // Said because it is the question an operator asks next, and the
            // answer is not obvious: stopping does not change who the service is
            // shared with, so starting it again restores exactly what it was.
            note = status == ServiceStatus.Stopped
                ? "Requests for this service now answer 503. Its sharing is unchanged, so starting "
                  + "it restores exactly the audience it had."
                : "The service is serving again.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task UnpublishAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        // Read before the delete, because afterwards there is nothing left to
        // derive the cache key from. Nothing here depends on it succeeding: a
        // shape left behind expires on its own, and would only ever be reused by
        // a layer republished onto the very same table.
        PublishedLayer? going = await layers.FindAsync(name, cancellation).ConfigureAwait(false);

        bool removed = await catalog.UnpublishLayerAsync(name, cancellation).ConfigureAwait(false);

        if (removed && going is not null)
        {
            contexts.Forget(going);

            // ADR-010 §5.1's *wrong* class, not the stale one. An unpublished
            // layer's tiles must go rather than expire: republishing the same
            // name over a different table would otherwise serve the old table's
            // pictures until the TTL ran out.
            tiles.Purge(going.Id);
        }

        await AuditAsync(
            context, audit, "layer.unpublish", name, Detail(new { removed }),
            succeeded: removed, cancellation).ConfigureAwait(false);

        if (!removed)
        {
            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            name,
            removed = true,

            // Said out loud, because "delete" on a layer that reads somebody
            // else's database could reasonably be feared to mean more.
            note = "The registration was removed. The table in the data source was not touched.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets a layer's remembered shape, so the next request re-reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This endpoint exists because the cache lifetime is a guess.</b>
    /// <see cref="ServiceContexts.Lifetime"/> bounds how long a table altered
    /// underneath us goes unnoticed, and the number was argued for rather than
    /// measured. An operator who has just added a column and does not want to
    /// wait it out should not have to restart the server — which is what they
    /// would do instead, and it would take every other layer down with it.
    /// </para>
    /// <para>
    /// <b>It needs <c>admin:manageServer</c>, not a content privilege.</b>
    /// Nothing about the layer changes; this is an operational act on the
    /// process, and the caller is being trusted with a lever over its behaviour.
    /// </para>
    /// <para>
    /// <b>It is local to this process.</b> Two servers over one platform store
    /// means two caches, and this clears the one that answered the call. That is
    /// stated in the response rather than left as a surprise — D-17 fixed the
    /// per-request cost, not the multi-process story, and pretending otherwise
    /// is how an operator comes to believe they have fixed something.
    /// </para>
    /// </remarks>
    private static async Task RefreshAsync(
        HttpContext context,
        string name,
        PostgresLayerCatalog layers,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        PublishedLayer? layer = await layers.FindAsync(name, cancellation).ConfigureAwait(false);

        if (layer is null)
        {
            await AuditAsync(context, audit, "layer.refresh", name, Detail(new { found = false }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 404, $"No layer '{name}'.").ConfigureAwait(false);
            return;
        }

        contexts.Forget(layer);

        // <b>Tiles go too, and the shape change alone would not have removed
        // them.</b> A fingerprint change makes entries built from the old
        // columns unreachable, which is structural invalidation and correct —
        // but an operator refreshing after an ALTER that changed *values*
        // rather than columns keeps the same fingerprint and would keep the old
        // pictures. Refresh means *forget what you know about this layer*, so
        // it means both.
        int purged = tiles.Purge(layer.Id);

        await AuditAsync(context, audit, "layer.refresh", name,
            Detail(new { found = true, tilesPurged = purged }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name,
            refreshed = true,
            tilesPurged = purged,
            note = "The next request re-reads this table's columns and extent from the data "
                 + "source, and any cached tiles are gone. This clears the caches of the server "
                 + "that answered the call; another server over the same platform store keeps its "
                 + "own until they expire.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ---------- helpers ----------

    private static object Describe(ProbeResult result) => new
    {
        outcome = result.Outcome.ToString(),
        result.Message,
        canPublish = result.CanPublish,
        serverVersion = result.ServerVersion,
        postgisVersion = result.PostgisVersion,
        tables = result.Tables.Select(t => new
        {
            t.SchemaName,
            t.TableName,
            t.GeometryColumn,
            t.Srid,
            geometryType = t.GeometryType,
            objectIdColumn = t.CandidateObjectIdColumn,
            arcGisServable = t.CandidateObjectIdColumn is not null,
            t.Writable,
        }),
    };

    private static bool TryReadPublication(
        PublishRequest request, out LayerPublication? publication, out string? error)
    {
        publication = null;
        error = null;

        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.SchemaName)
            || string.IsNullOrWhiteSpace(request.TableName)
            || string.IsNullOrWhiteSpace(request.GeometryColumn)
            || string.IsNullOrWhiteSpace(request.IdentityColumn))
        {
            error = "name, schemaName, tableName, geometryColumn and identityColumn are required.";
            return false;
        }

        if (request.Srid <= 0)
        {
            error = "srid must be a positive integer.";
            return false;
        }

        if (!Enum.TryParse(request.GeometryType, ignoreCase: true, out GeometryKind kind))
        {
            error =
                $"geometryType '{request.GeometryType}' is not one of: "
                + string.Join(", ", Enum.GetNames<GeometryKind>()) + ".";
            return false;
        }

        // Private unless asked otherwise. ADR-018 §3b's default, enforced at the
        // one place a layer comes into existence.
        if (!TryReadScope(request.Sharing ?? "private", out SharingScope scope, out error))
        {
            return false;
        }

        // <b>These last three used to be missing, and the record's defaults are why
        // nobody noticed.</b> Until 2026-08-16 this call stopped at `scope`;
        // ServiceName, ParentLayerIndex and CacheSeconds all default to null on
        // LayerPublication, so the shorter call compiled and the endpoint accepted
        // three documented fields and discarded them. The catalogue and the SQL
        // beneath it were correct the whole time — `ServiceName ?? Name`, parent
        // and cache are all handled — and PostgresAdminCatalogTests proved that
        // half. Nothing tested the mapping from the request to the record, so the
        // owner's 2026-08-15 correction that *"a service is a combination of
        // layers"* was unreachable through the admin API while the catalogue could
        // do it, and `POST /admin/featureservices` was answering 201 with a note
        // telling the operator to use a parameter that did nothing. D-47.
        publication = new LayerPublication(
            request.Name!,
            request.DataSourceId,
            request.SchemaName!,
            request.TableName!,
            request.GeometryColumn!,
            request.IdentityColumn!,
            string.IsNullOrWhiteSpace(request.ObjectIdColumn) ? null : request.ObjectIdColumn,
            request.Srid,
            kind,
            scope,
            string.IsNullOrWhiteSpace(request.ServiceName) ? null : request.ServiceName.Trim(),
            request.ParentLayerId,
            request.CacheSeconds);

        return true;
    }

    private static bool TryReadScope(string? value, out SharingScope scope, out string? error)
    {
        scope = SharingScope.Private;
        error = null;

        switch (value?.ToLowerInvariant())
        {
            case "private": scope = SharingScope.Private; return true;
            case "organization": scope = SharingScope.Organization; return true;
            case "public": scope = SharingScope.Public; return true;
            default:
                error = "sharing must be 'private', 'organization' or 'public'.";
                return false;
        }
    }

    private static string Conflict(PostgresException e, LayerPublication publication) => e.SqlState switch
    {
        "23505" =>
            $"Either a layer named '{publication.Name}' already exists, or this table is already "
            + "published from this data source. A table may be published once per source.",
        "23503" => "That data source id does not exist.",
        _ =>
            $"'{publication.GeometryType}' is not a geometry type the catalogue accepts.",
    };

    /// <summary>
    /// Host and database from a connection string, and nothing else.
    /// </summary>
    /// <remarks>
    /// Built by parsing rather than by trimming, so that a password containing a
    /// semicolon cannot survive into an audit record by accident.
    /// </remarks>
    private static string Summarise(string connectionString)
    {
        try
        {
            NpgsqlConnectionStringBuilder builder = new(connectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch (ArgumentException)
        {
            return "unparseable";
        }
    }

    private static string Wire(ServiceStatus status) =>
        Platform.Postgres.PostgresAdminCatalog.Wire(status);

    private static string PostgresSharing(SharingScope scope) =>
        Platform.Postgres.PostgresAdminCatalog.Wire(scope);

    private static string Detail(object value) => JsonSerializer.Serialize(value);

    private static Task AuditAsync(
        HttpContext context,
        IAuditLog audit,
        string action,
        string? resource,
        string detail,
        bool succeeded,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                context.Connection.RemoteIpAddress?.ToString(),
                action,
                resource,
                detail,
                succeeded),
            cancellation);
    }

    private static Task Refuse(HttpContext context, int status, string message) =>
        Results.Json(new { error = new { code = status, message } }, statusCode: status)
            .ExecuteAsync(context);
}
