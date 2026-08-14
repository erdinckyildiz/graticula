using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using GisServer.Platform.Admin;
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
    string? Sharing);

/// <summary>A change of sharing scope.</summary>
internal sealed record SharingRequest(string? Sharing);

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
        app.MapDelete("/admin/layers/{name}", UnpublishAsync);
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
        HttpContext context, IAdminCatalog catalog, CancellationToken cancellation)
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
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool maySeeDetail = current.Authorization.Allows(Privilege.AdminManageServer);

        await Results.Json(new
        {
            status = storeError is null ? "ok" : "degraded",
            version = typeof(AdminEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",

            platformStore = new
            {
                reachable = storeError is null,
                layers,
                error = maySeeDetail ? storeError : storeError is null ? null : "redacted",
            },

            // Said explicitly in the degraded case, because an administrator
            // reading this during an outage should not have to infer which half
            // of the answer they are allowed to trust.
            note = storeError is null
                ? "The platform store is reachable, so everything below is current."
                : "The platform store is unreachable. Serving may continue for layers already "
                  + "resolved, but the catalogue, identity and audit are unavailable, so most of "
                  + "this API will refuse. This is the failure ADR-019 accepted when it fused the "
                  + "catalogue and the runtime into one deployable.",
        }).ExecuteAsync(context).ConfigureAwait(false);
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
            Guid id = await catalog
                .PublishLayerAsync(publication, current.Principal.Id, cancellation)
                .ConfigureAwait(false);

            await AuditAsync(
                context, audit, "layer.publish", publication.Name,
                Detail(new
                {
                    id,
                    table = $"{publication.SchemaName}.{publication.TableName}",
                    sharing = PostgresSharing(publication.Sharing),
                    arcGisServable = publication.ObjectIdColumn is not null,
                }),
                succeeded: true, cancellation).ConfigureAwait(false);

            await Results.Json(
                new
                {
                    id,
                    name = publication.Name,
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
                owner = l.OwnerName,
                l.ArcGisServable,
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

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

    private static async Task UnpublishAsync(
        HttpContext context,
        string name,
        IAdminCatalog catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent)
            .ConfigureAwait(false))
        {
            return;
        }

        bool removed = await catalog.UnpublishLayerAsync(name, cancellation).ConfigureAwait(false);

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
            scope);

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
