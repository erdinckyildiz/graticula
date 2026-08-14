using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Api.ArcGis;
using GisServer.Features;
using GisServer.Geometries;
using GisServer.Platform.Catalog;
using GisServer.Platform.Admin;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using GisServer.Platform.Schema;
using GisServer.Platform.Secrets;
using GisServer.Security.Argon2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GisServer.Host;

/// <summary>The server.</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        // Before configuration is read, because its whole purpose is to
        // produce the value that configuration will demand. A first run should
        // not require the operator to know how to drive openssl.
        if (args is ["keygen", ..])
        {
            Console.WriteLine(Convert.ToBase64String(SecretProtector.GenerateKey()));
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "That is a 32-byte AES-256 key, base64. Set it as GisServer__SecretKey. It seals "
                + "every registered data source credential (ADR-002 §4.7), so a lost key means "
                + "every registration must be re-entered, and a leaked one means every credential "
                + "is readable from a database backup. Keep it where you keep secrets.");
            return 0;
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        HostSettings settings = HostSettings.Read(builder.Configuration);
        ConfigureKestrel(builder, settings);

        builder.Services.AddSingleton(_ => new SecretProtector(
            settings.SecretKeyVersion, Convert.FromBase64String(settings.SecretKeyBase64)));

        builder.Services.AddSingleton(_ =>
            new NpgsqlDataSourceBuilder(settings.PlatformStore).Build());

        builder.Services.AddSingleton(services => new PostgresLayerCatalog(
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<SecretProtector>()));

        builder.Services.AddSingleton<LayerConnections>();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ServerState>();
        builder.Services.AddSingleton<IPasswordHasher>(_ => new Argon2idPasswordHasher());

        builder.Services.AddSingleton<IIdentityStore>(services =>
            new PostgresIdentityStore(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton<IAdminCatalog>(services => new PostgresAdminCatalog(
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<SecretProtector>()));

        builder.Services.AddSingleton<IDataSourceProbe>(_ => new PostgresDataSourceProbe());

        builder.Services.AddSingleton<IAuditLog>(services =>
            new PostgresAuditLog(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton<ISetupStore>(services =>
            new PostgresSetupStore(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton(services => new Authentication(
            services.GetRequiredService<IIdentityStore>(),
            services.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton(services => new LoginService(
            services.GetRequiredService<IIdentityStore>(),
            services.GetRequiredService<IPasswordHasher>(),
            LoginThrottle.Default,
            settings.SessionLifetime,
            services.GetRequiredService<TimeProvider>()));

        WebApplication app = builder.Build();
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup");

        // ADR-016 §4b: migration is an explicit operation, never something
        // startup does on its own. It reports what it intends to do — including
        // whether it closes the rollback window — before doing it.
        if (args is ["migrate", ..])
        {
            return await MigrateAsync(app.Services, args.Contains("--apply")).ConfigureAwait(false);
        }

        if (!settings.RequireHttps)
        {
            // Every startup, not once. ADR-014 §2a: a quiet option is one that
            // ends up in production.
            Log.ServingPlainHttp(logger);
        }

        if (!await HandshakeAsync(app.Services, logger).ConfigureAwait(false))
        {
            return 1;
        }

        await BootstrapAsync(app.Services, logger).ConfigureAwait(false);

        // Two facts an operator needs at every start: what authorization does
        // cover, and whether anything is currently visible at all.
        //
        // The message this replaced said authorization did not exist. It was
        // true for exactly as long as that was true, and then it was a lie the
        // server told at every startup — which is the failure mode of a standing
        // warning nobody re-reads.
        Log.AuthorizationIsPortalShaped(logger);

        if (!await AnythingIsSharedAsync(app.Services).ConfigureAwait(false))
        {
            Log.NothingIsShared(logger);
        }

        // Before the endpoints, so it wraps them. ADR-017 §6: an unhandled
        // exception must still produce an answer that says what to do.
        app.UseExceptionHandler(handler => handler.Run(context =>
            ErrorResponse.WriteAsync(
                context,
                context.Features.Get<IExceptionHandlerFeature>()?.Error
                    ?? new InvalidOperationException("An error was reported with no exception."),
                logger)));

        // BEFORE authentication, and it touches nothing. A liveness probe that
        // depends on the database tells an orchestrator to kill the container
        // during a database outage — turning an outage into a restart loop, and
        // destroying the running process that was the only thing still able to
        // answer questions. Found by stopping the datastore (ADR-017 condition
        // 1), which is also how it was found to be returning 503.
        //
        // Liveness answers "is this process alive". Readiness answers "can it
        // serve", needs the store, and is a separate endpoint precisely because
        // the two must be allowed to disagree.
        app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));

        app.Use(async (context, next) =>
        {
            context.Features.Set(await context.RequestServices
                .GetRequiredService<Authentication>()
                .ResolveAsync(context, context.RequestAborted)
                .ConfigureAwait(false));

            await next(context).ConfigureAwait(false);
        });

        // After authentication, so setup is the only surface an unconfigured
        // server exposes, and before the endpoints, so nothing is reachable
        // around it.
        app.Use(async (context, next) =>
        {
            ServerState state = context.RequestServices.GetRequiredService<ServerState>();

            if (!state.IsSetupPending
                || context.Request.Path == "/rest/setup"
                || context.Request.Path == "/healthz/live")
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 503,
                        message =
                            "This server has no administrator yet and is refusing everything "
                            + "except setup. A one-time setup token has been written to the "
                            + "server log; POST it to /rest/setup with a name and a password.",
                    },
                },
                statusCode: StatusCodes.Status503ServiceUnavailable)
                .ExecuteAsync(context).ConfigureAwait(false);
        });

        MapEndpoints(app);

        Log.Listening(
            logger,
            settings.RequireHttps ? "https" : "http",
            settings.ListenAddress,
            settings.Port);

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Runs, or merely reports, the pending migrations.
    /// </summary>
    /// <remarks>
    /// Reporting is the default and applying takes <c>--apply</c>. An operator
    /// who types the command without the flag gets the plan, which is the one
    /// chance they have to see that an upgrade closes the rollback window before
    /// it does.
    /// </remarks>
    private static async Task<int> MigrateAsync(IServiceProvider services, bool apply)
    {
        NpgsqlDataSource dataSource = services.GetRequiredService<NpgsqlDataSource>();
        SchemaMigrator migrator = new(new PostgresPlatformSchemaStore(dataSource), PlatformMigrations.All);

        MigrationReport plan = await migrator.PlanAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(plan.Describe());

        if (plan.IsUpToDate)
        {
            return 0;
        }

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing has been changed. Re-run with --apply to proceed.");
            return 0;
        }

        MigrationReport applied = await migrator.ApplyAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"Applied {applied.Pending.Count} migration(s). Now at {applied.To}.");
        return 0;
    }

    /// <summary>
    /// ADR-016 §4b: refuse rather than migrate.
    /// </summary>
    /// <remarks>
    /// Auto-migration on startup is how an old container started by accident —
    /// a stale tag, a rollback, a stray <c>docker run</c> — silently rewrites a
    /// newer schema. The result is unrecoverable and presents as corruption
    /// rather than as a mistake.
    /// </remarks>
    private static async Task<bool> HandshakeAsync(IServiceProvider services, ILogger logger)
    {
        NpgsqlDataSource dataSource = services.GetRequiredService<NpgsqlDataSource>();
        PostgresPlatformSchemaStore store = new(dataSource);

        SchemaStamp? stamp;
        try
        {
            stamp = await store.ReadStampAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException e)
        {
            Log.PlatformStoreUnreachable(logger, e);
            return false;
        }

        SchemaCompatibilityResult result = SchemaCompatibility.Check(
            "server", PlatformMigrations.ComponentSchemaVersion, stamp);

        if (!result.IsCompatible)
        {
            Log.SchemaIncompatible(logger, result.Explanation);
            return false;
        }

        Log.SchemaCompatible(logger, result.Explanation);
        return true;
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, HostSettings settings)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;

            kestrel.Listen(settings.ListenAddress, settings.Port, listen =>
            {
                if (!settings.RequireHttps)
                {
                    return;
                }

                X509Certificate2 certificate = settings.CertificatePath is { } path
                    ? X509CertificateLoader.LoadPkcs12FromFile(path, settings.CertificatePassword)
                    : ServerIdentity.LoadOrCreate(settings.HostName, settings.StatePath);

                listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;

                    // ADR-014 §2e: platform defaults rather than a hand-written
                    // cipher list. A hand-rolled list is correct on the day it
                    // is written and wrong two years later, and the platform is
                    // maintained by people who track this full time.
                    https.SslProtocols = System.Security.Authentication.SslProtocols.None;
                });

                // HTTP/2 is not optional: Q-78 put gRPC in scope, and it
                // requires it. Native termination gives us h2 directly.
                listen.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/healthz/ready", async (
            IAdminCatalog catalog, CancellationToken cancellation) =>
        {
            // Readiness DOES depend on the store, which is the whole difference
            // from liveness. A load balancer should stop sending traffic here;
            // an orchestrator should not kill the process.
            try
            {
                await catalog.ListLayersAsync(cancellation);
                return Results.Ok(new { status = "ready" });
            }
            catch (NpgsqlException e)
            {
                return Results.Json(
                    new { status = "not-ready", reason = e.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // ADR-015 §4 has clients discovering how to authenticate here. The
        // token URL is advertised even though /generateToken is unbuilt, because
        // a client that cannot find it assumes anonymous and fails later, less
        // clearly.
        app.MapGet("/rest/info", (HttpContext context) => Results.Ok(
            FeatureServerMetadataWriter.ServerInfo(
                $"{context.Request.Scheme}://{context.Request.Host}/rest/auth/login")));

        app.MapGet("/rest/services", async (
            HttpContext context, PostgresLayerCatalog catalog, CancellationToken cancellation) =>
        {
            // A filter, not a gate. ADR-018 §3b governs reading by sharing, so
            // the catalogue lists what this caller may see rather than refusing
            // the whole endpoint — which is what makes two layers with two
            // audiences possible.
            RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

            IReadOnlyList<PublishedLayer> layers = await catalog.ListAsync(cancellation);

            return Results.Ok(FeatureServerMetadataWriter.Catalogue(
                layers
                    .Where(layer => LayerAccess
                        .Evaluate(layer.Sharing, layer.Owner, current.Principal, current.Authorization)
                        .IsAllowed())
                    .Select(layer => layer.Definition.Name)));
        });

        app.MapGet("/rest/services/{layerName}/FeatureServer", ServiceMetadataAsync);
        app.MapGet("/rest/services/{layerName}/FeatureServer/0", LayerMetadataAsync);
        app.MapGet("/rest/services/{layerName}/FeatureServer/0/query", QueryAsync);

        app.MapAuth();
        app.MapAdmin();
        app.MapPost("/rest/setup", AuthEndpoints.SetupAsync);

        app.MapGet("/rest/whoami", (HttpContext context) =>
        {
            // Small, and it earns its place: it is the only way to confirm from
            // outside that a token resolved to the principal it should, and the
            // authentication tests assert against it.
            RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

            return Results.Ok(new
            {
                name = current.Principal.Name,
                kind = current.Principal.Kind.ToString(),
                authenticated = !current.Principal.IsAnonymous,
                userType = current.Authorization.UserType,
                roles = current.Authorization.Roles,
                privileges = current.Authorization.Privileges
                    .Select(Authorize.Name).OrderBy(p => p, StringComparer.Ordinal),
            });
        });
    }

    /// <summary>The audit detail for a read override.</summary>
    /// <remarks>
    /// Built with the JSON writer rather than by concatenation, so that a scope
    /// name containing a quote could never break the column's <c>jsonb</c> parse
    /// — which would turn an audit write into a failed request.
    /// </remarks>
    private static string SharingDetail(SharingScope sharing) =>
        JsonSerializer.Serialize(new { sharing = sharing.ToString().ToLowerInvariant() });

    /// <summary>
    /// Whether any layer is shared with anybody but its owner.
    /// </summary>
    /// <remarks>
    /// Asked once at startup so that a server whose layers are all private says
    /// so, rather than presenting as a working server that 404s everything.
    /// ADR-018 §3b's private default is correct and surprising — and after an
    /// upgrade it is surprising to somebody who had published those layers
    /// openly, which is condition 4.
    /// </remarks>
    private static async Task<bool> AnythingIsSharedAsync(IServiceProvider services)
    {
        PostgresLayerCatalog catalog = services.GetRequiredService<PostgresLayerCatalog>();

        IReadOnlyList<PublishedLayer> layers =
            await catalog.ListAsync(CancellationToken.None).ConfigureAwait(false);

        // Only worth saying if there is something to share in the first place.
        return layers.Count == 0 || layers.Any(l => l.Sharing != SharingScope.Private);
    }


    /// <summary>
    /// Resolves a layer for a metadata request, or writes the refusal.
    /// </summary>
    /// <remarks>
    /// Shared by the service and layer documents so that both apply ADR-018
    /// §3b's sharing rule identically. A metadata endpoint that leaks the
    /// existence of a private layer undoes the query endpoint's care.
    /// </remarks>
    private static async Task<PublishedLayer?> VisibleLayerAsync(
        HttpContext context,
        string layerName,
        PostgresLayerCatalog catalog,
        CancellationToken cancellation)
    {
        PublishedLayer? layer =
            await catalog.FindAsync(layerName, cancellation).ConfigureAwait(false);

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (layer is not null
            && LayerAccess
                .Evaluate(layer.Sharing, layer.Owner, current.Principal, current.Authorization)
                .IsAllowed())
        {
            return layer;
        }

        await Authorize.RefuseReadAsync(context, layerName).ConfigureAwait(false);
        return null;
    }

    private static async Task ServiceMetadataAsync(
        HttpContext context,
        string layerName,
        PostgresLayerCatalog catalog,
        LayerConnections connections,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await VisibleLayerAsync(context, layerName, catalog, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        LayerDescription description = await connections.SourceFor(layer)
            .DescribeAsync(cancellation).ConfigureAwait(false);

        await Results.Ok(FeatureServerMetadataWriter.Service(
            layer.Definition, layer.GeometryType, description.Extent))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task LayerMetadataAsync(
        HttpContext context,
        string layerName,
        PostgresLayerCatalog catalog,
        LayerConnections connections,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await VisibleLayerAsync(context, layerName, catalog, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        LayerDescription description = await connections.SourceFor(layer)
            .DescribeAsync(cancellation).ConfigureAwait(false);

        await Results.Ok(FeatureServerMetadataWriter.Layer(
            layer.Definition, layer.GeometryType, description))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>How long a first-start setup token lasts.</summary>
    /// <remarks>
    /// Long enough to read a log and make a request; short enough that a token
    /// sitting in the log of a server nobody finished setting up is not a
    /// standing invitation.
    /// </remarks>
    private static readonly TimeSpan SetupTokenLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// ADR-015 §6: issues a one-time setup token if there is no administrator.
    /// </summary>
    /// <remarks>
    /// Runs after the schema handshake, so a server that is about to refuse to
    /// start does not first print a credential into the log.
    /// </remarks>
    private static async Task BootstrapAsync(IServiceProvider services, ILogger logger)
    {
        IIdentityStore identity = services.GetRequiredService<IIdentityStore>();
        ServerState state = services.GetRequiredService<ServerState>();

        if (await identity.AnyUserExistsAsync(CancellationToken.None).ConfigureAwait(false))
        {
            // Users exist, so the setup flow is over — but that is not the same
            // as the server being administrable. A store upgraded from before
            // ADR-018 has accounts and no grants, which is ADR-018 §4's failure
            // reached by a different road: an administrator who cannot
            // administer, on a server with nobody able to fix it.
            //
            // <b>Said, not repaired.</b> Issuing a fresh setup token here would
            // be a credential printed to a log whenever the last administrator's
            // grant disappears, and "the last administrator's grant disappeared"
            // is a state an attacker would like to arrange. Refusing to start
            // would take a working read-only server down for a problem that does
            // not affect reads. So it is loud, and the fix is one statement.
            if (!await identity
                .AnyPrincipalHoldingAsync(Roles.Administrator, CancellationToken.None)
                .ConfigureAwait(false))
            {
                Log.NoAdministrator(logger);
            }

            return;
        }

        state.RequireSetup();

        ISetupStore setup = services.GetRequiredService<ISetupStore>();
        DateTimeOffset now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        if (await setup.HasUsableTokenAsync(now, CancellationToken.None).ConfigureAwait(false))
        {
            // A restart during setup must not print a second working token: two
            // live credentials for a one-time act is what condition 4 is about.
            Log.SetupStillPending(logger);
            return;
        }

        string token = await setup
            .IssueAsync(now + SetupTokenLifetime, CancellationToken.None)
            .ConfigureAwait(false);

        Log.SetupTokenIssued(logger, token, (int)SetupTokenLifetime.TotalMinutes);
    }

    private static async Task QueryAsync(
        HttpContext context,
        string layerName,
        PostgresLayerCatalog catalog,
        LayerConnections connections,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await catalog.FindAsync(layerName, cancellation).ConfigureAwait(false);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        // One response for "does not exist" and "not shared with you". A 403 on
        // a named layer confirms it exists, which turns this endpoint into a
        // directory of everything published on the server.
        LayerAccess.Reason reason = layer is null
            ? LayerAccess.Reason.Denied
            : LayerAccess.Evaluate(
                layer.Sharing, layer.Owner, current.Principal, current.Authorization);

        if (layer is null || !reason.IsAllowed())
        {
            await Authorize.RefuseReadAsync(context, layerName).ConfigureAwait(false);
            return;
        }

        if (reason == LayerAccess.Reason.AdministrativeOverride)
        {
            // ADR-018 condition 3. An administrator reading a private layer is
            // legitimate and must leave a record, or the sharing model is
            // decorative.
            await audit.RecordAsync(
                new AuditEvent(
                    current.Principal.Id,
                    current.Principal.Name,
                    context.Connection.RemoteIpAddress?.ToString(),
                    "layer.read.override",
                    layerName,
                    SharingDetail(layer.Sharing),
                    Succeeded: true),
                cancellation).ConfigureAwait(false);
        }

        if (!layer.Definition.IsArcGisServable)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message =
                            $"Layer '{layerName}' has no integer object-id column, so it cannot be "
                            + "served through the ArcGIS surface. It remains servable natively.",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!FeatureServerQueryParameters.TryParse(
                context.Request.Query, layer.Definition.ObjectIdColumn!, out FeatureQuery? query, out string? error))
        {
            await Results.Json(
                new { error = new { code = 400, message = error } },
                statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        IFeatureSource source = connections.SourceFor(layer);
        FeatureServerQueryWriter writer = new(layer.Definition);

        context.Response.ContentType = "application/json; charset=utf-8";

        // Written straight to the response body. Nothing is buffered, because
        // A-037 measured allocation as the binding constraint and a serialised
        // copy of a 50,000-feature result is exactly the kind of peak it warns
        // about.
        await using Utf8JsonWriter json = new(context.Response.BodyWriter);

        try
        {
            await writer.WriteAsync(json, source, query!, layer.GeometryType, cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (json.BytesCommitted > 0 || json.BytesPending > 0)
        {
            // Asked of the writer, not of HttpResponse.HasStarted. The response
            // has not "started" until the pipe flushes to the socket, so
            // HasStarted is still false while a complete JSON header sits in the
            // buffer — and the error handler then writes a second document into
            // the middle of the first. The writer is the only thing that knows.
            //
            // Nothing can rescue this response: the bytes are past the point of
            // recall and the document is truncated. Aborting turns it into an
            // incomplete transfer, which a client detects, instead of malformed
            // JSON, which it does not.
            ErrorResponse.LogTruncated(loggerFactory.CreateLogger("query"), e);
            context.Abort();
        }
    }
}
