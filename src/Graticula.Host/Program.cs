using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Features;
using System.Diagnostics;
using Graticula.Diagnostics;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Platform.Schema;
using Graticula.Tiles;
using Graticula.Providers.PostGis;
using Graticula.Platform.Secrets;
using Graticula.Security.Argon2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

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
                "That is a 32-byte AES-256 key, base64. Set it as Graticula__SecretKey. It seals "
                + "every registered data source credential (ADR-002 §4.7), so a lost key means "
                + "every registration must be re-entered, and a leaked one means every credential "
                + "is readable from a database backup. Keep it where you keep secrets.");
            return 0;
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        HostSettings settings = HostSettings.Read(builder.Configuration);
        ConfigureKestrel(builder, settings);

        // <b>Registered so a handler can be given it rather than reaching for
        // configuration again.</b> Added with Q-113's response ceiling: the query
        // handler needs one number from here, and re-reading `IConfiguration` inside
        // a request would mean two places that could disagree about what the setting
        // is — which is the shape of every stale-value fault in this repository's
        // register.
        builder.Services.AddSingleton(settings);

        builder.Services.AddSingleton(_ => new SecretProtector(
            settings.SecretKeyVersion, Convert.FromBase64String(settings.SecretKeyBase64)));

        builder.Services.AddSingleton(_ =>
            new NpgsqlDataSourceBuilder(settings.PlatformStore).Build());

        builder.Services.AddSingleton(services => new PostgresLayerCatalog(
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<SecretProtector>()));

        // <b>Q-95: the catalogue, plus the last answer it gave.</b> The serving
        // path resolves services through this rather than through the catalogue
        // directly, so that a platform-store outage degrades to public-only
        // serving instead of stopping the server. While the store answers,
        // every request still reads it and nothing about the healthy path
        // changes — see CatalogFallback.
        builder.Services.AddSingleton(services => new CatalogFallback(
            services.GetRequiredService<PostgresLayerCatalog>(),
            services.GetRequiredService<TimeProvider>(),
            settings.CatalogFallbackWindow));

        // <b>Read once at startup.</b> The directory listing is the set of font
        // stacks and it cannot change while the process runs, so scanning it per
        // request would be work in exchange for nothing.
        builder.Services.AddSingleton(_ => new GlyphStore(GlyphStore.BesideThisOne()));

        builder.Services.AddSingleton<TileSingleFlight>();

        builder.Services.AddSingleton<LayerConnections>();

        builder.Services.AddSingleton(services =>
            new PostgresRelationshipCatalog(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton(services =>
            new PostgresSystemServices(services.GetRequiredService<NpgsqlDataSource>()));

        // <b>Raised so our own cap is the one that fires.</b> The default form
        // value limit is 4 MB, which is well under the 500,000 vertices
        // GeometryServer documents as its bound — so a request inside the
        // documented limit was refused by the framework, as a 500 that told the
        // caller the server had failed. A limit nobody documented, producing an
        // error that blames the wrong party, ahead of the limit that was
        // designed and explained.
        //
        // 48 MB is roughly four times the JSON a 500,000-vertex request weighs.
        // It is the outer bound; the vertex cap is the semantic one, and this
        // exists so that cap is reached.
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        {
            options.ValueLengthLimit = 48 * 1024 * 1024;
            options.MultipartBodyLengthLimit = 48 * 1024 * 1024;
            options.ValueCountLimit = 4096;

            // The hosted-data upload is the one multipart surface, and its own
            // cap is stated in HostedDataEndpoints. This is the outer bound so a
            // body larger than that is refused by the framework before it is
            // buffered rather than after.
            options.MultipartBodyLengthLimit = HostedDataEndpoints.MaximumBytes;
        });

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ServerState>();
        builder.Services.AddSingleton<IPasswordHasher>(_ => new Argon2idPasswordHasher());

        builder.Services.AddSingleton<IIdentityStore>(services =>
            new PostgresIdentityStore(services.GetRequiredService<NpgsqlDataSource>()));

        // <b>A second port over the same store, and the split is deliberate</b> — see
        // IMemberDirectory. Every request touches IIdentityStore to authenticate; only
        // admin:manageMembers touches this, so the login path has no route to member creation.
        builder.Services.AddSingleton<IMemberDirectory>(services =>
            new PostgresMemberDirectory(services.GetRequiredService<NpgsqlDataSource>()));

        // <b>Its own pool, not the platform store's, and the difference is the
        // search path.</b> The platform store's connection names the schema
        // holding `layer` and `principal`; PostGIS lives in `public`. Sharing
        // that pool made every spatial function undefined — the same defect
        // already fixed once for the datastore registration, arriving by a
        // second route within the hour. Registered as a keyed service so there
        // is one place that knows how to reach PostGIS and both callers use it.
        builder.Services.AddKeyedSingleton(
            DatastorePool,
            (_, _) => new NpgsqlDataSourceBuilder(DatastoreConnection(settings.PlatformStore)).Build());

        builder.Services.AddSingleton(services =>
            new PostGisImporter(services.GetRequiredKeyedService<NpgsqlDataSource>(DatastorePool)));

        builder.Services.AddSingleton<IProjector>(services =>
            new PostGisProjector(services.GetRequiredKeyedService<NpgsqlDataSource>(DatastorePool)));

        // <b>Overlay runs in its own process, and the pool is what kills it.</b>
        // Q-97, answered by the owner: no property of the input predicts overlay
        // cost, so the bound is a deadline on a process rather than a cap on a
        // number. NetTopologySuite is referenced by that worker and by nothing
        // in this assembly, so an overlay cannot allocate a byte in this heap.
        builder.Services.AddSingleton(services => new GeometryWorkerPool(
            GeometryWorkerPool.ExecutableBesideThisOne(),
            settings.OverlayWorkers,
            services.GetRequiredService<ILoggerFactory>(),

            // <b>Both bounds come from configuration now.</b> The pool has always taken them
            // and this call has always omitted them, so the constants were the only values a
            // deployment could have — which is what the owner's question about the timeout
            // uncovered. The defaults are unchanged, so no existing deployment moves.
            settings.OverlayDeadline,
            settings.OverlayPreflightPairs,
            settings.OverlayWait,
            settings.OverlayIdle));

        builder.Services.AddSingleton<IGeometryEngine>(services =>
            services.GetRequiredService<GeometryWorkerPool>());

        builder.Services.AddSingleton<ITileCache>(services => new FileSystemTileCache(
            settings.TileCachePath,
            settings.TileCacheBudgetBytes,
            settings.TileCacheLayerBudgetBytes,
            settings.TileCacheLifetime,
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILoggerFactory>()));

        builder.Services.AddSingleton(services => new ServiceContexts(
            services.GetRequiredService<LayerConnections>(),
            services.GetRequiredService<TimeProvider>()));

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

        // Every startup, for the same reason: the rename kept reading the old keys so
        // that no existing deployment had to be reconfigured to start (ADR-032 §5), and
        // the only way that stays temporary is if it says so each time.
        if (settings.LegacyKeys is { Count: > 0 } legacy)
        {
            Log.ConfiguredUnderTheFormerName(logger, string.Join(", ", legacy));
        }

        if (!await HandshakeAsync(app.Services, logger).ConfigureAwait(false))
        {
            return 1;
        }

        await BootstrapAsync(app.Services, logger).ConfigureAwait(false);

        // After the handshake, so the schema is known to be at least version 7,
        // and after bootstrap, so a first start has already refused everything
        // if it needs setup. Registering the datastore is not an administrative
        // act — ADR-019 fused it into the product and Q-69 made it mandatory, so
        // it is already present and asking an operator to register it would be
        // asking them to re-enter a credential the server is holding.
        await EnsureDatastoreAsync(app.Services, settings, logger).ConfigureAwait(false);

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

        // <b>First, so that everything answers with them — including a 404 from
        // routing and a 500 from the exception handler, which are exactly the
        // responses a hardening pass forgets.</b> Added by the §66 security
        // gate; see SecurityHeaders.
        app.UseSecurityHeaders(settings.RequireHttps);

        app.Use(async (context, next) =>
        {
            RequestPrincipal current = await context.RequestServices
                .GetRequiredService<Authentication>()
                .ResolveAsync(context, context.RequestAborted)
                .ConfigureAwait(false);

            context.Features.Set(current);

            // <b>Set per request and cleared after it.</b> The directory's
            // banner needs to know who is browsing, and threading that through
            // eight renderers would be ceremony — but a thread-static left set
            // is the next request seeing the last request's name in the corner,
            // which is a disclosure rather than a cosmetic bug.
            RestDirectory.SignedInAs(
                current.Principal.IsAnonymous ? null : current.Principal.Name);

            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                RestDirectory.SignedInAs(null);
            }
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

        // ADR-020 §2: the console is static files and nothing else. Mapped
        // after the setup gate so an unconfigured server does not serve a UI
        // whose every call would 503, and scoped to /console so that the file
        // provider can never see anything outside its own directory —
        // condition 2, and the first code here that could suffer path traversal.
        // <b>`.geojson` has to be mapped or the file is not served at all.</b> The
        // static file middleware refuses an extension it has no content type for,
        // and answers 404 — indistinguishable from a missing file, which cost a
        // round of confusion when the console's ground layer would not load. A GIS
        // server having no mapping for the most common interchange format on the
        // web was worth fixing for its own sake.
        Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider contentTypes = new();
        contentTypes.Mappings[".geojson"] = "application/geo+json";

        // <b>Two paths, one directory, since ADR-034.</b> The application used to be *the
        // console* and lived at `/console`; it is now two surfaces — Server for the operator
        // and Studio for the publisher — and the owner's objection was exactly this: *"console
        // yerine server kullanacaktık ya."* A name nobody uses any more should not be in the
        // address bar.
        //
        // <b>The surface is the path and the screen is the hash.</b> `/server/#/services` and
        // `/studio/#/content`. The path carries the environment because that is what it is —
        // ArcGIS separates them as far as two applications — while the hash keeps doing what
        // ADR-020 §5c needs: a screen you can link to and reach with Back.
        //
        // <b>One physical directory served twice, not two copies.</b> ADR-034 condition 2 asks
        // for one stylesheet and one map module across both surfaces, and two mounts over one
        // folder is the cheapest way to mean it: there is nothing to keep in step.
        PhysicalFileProvider surfaces = new(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

        foreach (string surface in (string[])["/server", "/studio"])
        {
            app.UseFileServer(new FileServerOptions
            {
                FileProvider = surfaces,
                RequestPath = surface,
                EnableDefaultFiles = true,
                EnableDirectoryBrowsing = false,
                StaticFileOptions = { ContentTypeProvider = contentTypes },
            });
        }

        // <b>The old address keeps working.</b> ADR-020 §5c took *frozen URLs* from the
        // reference as a rule, and a rename is exactly the case that rule is about: anybody
        // who bookmarked `/console/` is sent to Server, which is where the screens they knew
        // now are. A reader without `admin:manageServer` is bounced on to Studio by the
        // application itself, which is the only place that knows their privileges.
        app.MapGet("/console/{**rest}", (string? rest) =>
            Results.Redirect($"/server/{rest}", permanent: true));

        app.MapGet("/console", () => Results.Redirect("/server/", permanent: true));

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

        try
        {
            MigrationReport applied =
                await migrator.ApplyAsync(CancellationToken.None).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Applied {applied.Pending.Count} migration(s). Now at {applied.To}.");
            return 0;
        }
        catch (PostgresException failure) when (failure.SqlState == "3F000")
        {
            // <b>The first thing a new operator sees, and it used to be a raw
            // Npgsql stack trace saying "no schema has been selected to create
            // in".</b> That is Postgres telling us the search path names a
            // schema that does not exist — which on a brand-new database is the
            // normal state, not a fault. Found 2026-08-15 while making CI build
            // a server from nothing, which is the first time anything had
            // installed this product against an empty database. D-36.
            //
            // <b>It creates tables, not the namespace it was pointed at</b>, and
            // that stays true: creating a schema is a privileged act and doing
            // it silently would mean a typo in a connection string quietly
            // producing a second, empty installation.
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "The platform store's search path names a schema that does not exist.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "  Create it once, as a user that may:  CREATE SCHEMA gisserver;");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "This tool creates tables, not the schema it was told to put them in — a "
                + "typo in SearchPath would otherwise produce a second, empty installation "
                + "rather than an error.");

            return 1;
        }
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
            IAdminCatalog catalog,
            [FromKeyedServices(DatastorePool)] NpgsqlDataSource datastore,
            CancellationToken cancellation) =>
        {
            // Readiness DOES depend on the store, which is the whole difference
            // from liveness. A load balancer should stop sending traffic here;
            // an orchestrator should not kill the process.
            //
            // <b>Both pools, and the second one was added by the failure gate.</b>
            // Checking only the platform store made this endpoint answer 200
            // while every query answered 503: after the database came back, the
            // catalogue pool reconnected several seconds before the datastore
            // pool did, and for those seconds an orchestrator would have routed
            // traffic to a server that could not serve it. A readiness probe
            // that is green while the serving path is red is worse than none —
            // it is the signal something acts on.
            //
            // <b>Only the datastore, not every registered source.</b> The
            // datastore is mandatory (ADR-019) and every hosted layer needs it;
            // a registered source being down should make its own layers fail,
            // not take the server out of rotation.
            try
            {
                await catalog.ListLayersAsync(cancellation);

                await using NpgsqlCommand probe = datastore.CreateCommand("select 1");
                await probe.ExecuteScalarAsync(cancellation);

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

        // <b>The origin itself answers, and until 2026-08-17 it did not.</b> Nothing
        // was mapped to "/", so typing the server's address produced an empty 404 —
        // no status line, no hint, nothing to click. The owner hit exactly that:
        // every link in this product carries a path, and the one thing a person types
        // is the host.
        //
        // <b>It redirects to the services directory rather than to the console</b>,
        // and that ordering is the product's own claim. ADR-023's browsable directory
        // at the ArcGIS URL is the compatibility surface an administrator arriving
        // from ArcGIS Server expects to find, and Q-07's promise is that their client
        // works — so the front door is the thing a client would ask for. The console
        // is one link away from there.
        //
        // 302 rather than 301: a permanent redirect is cached by browsers past the
        // point where anybody can change their mind about what the root is for.
        app.MapGet("/", () => Results.Redirect("/rest/services?f=html", permanent: false));

        // The root: registered services, the folders, and the system services.
        app.MapGet("/rest/services", (
            HttpContext context,
            PostgresLayerCatalog catalog,
            PostgresSystemServices system,
            CancellationToken cancellation) =>
            CatalogueAsync(context, catalog, system, folder: null, cancellation))
            .Governed(SharingGovernedExtensions.ByFiltering);

        // Everything the datastore owns. The literal segment is more specific
        // than {layerName}, so routing prefers it — and it is matched
        // case-insensitively, which is why a client sending ArcGIS's own
        // capitalised "Hosted" reaches the same place.
        app.MapGet($"/rest/services/{FeatureServerMetadataWriter.HostedFolder}", (
            HttpContext context,
            PostgresLayerCatalog catalog,
            PostgresSystemServices system,
            CancellationToken cancellation) =>
            CatalogueAsync(
                context, catalog, system, FeatureServerMetadataWriter.HostedFolder, cancellation))
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>Any folder's own directory.</b> Added 2026-08-17 with named folders: the two
        // literal routes above covered the two folders that could exist, so browsing
        // /rest/services/turkiye answered nothing at all while the root advertised it — a
        // folder a client could see and not open. The literals stay because a literal segment
        // beats a parameter in routing and both carry their own comment.
        app.MapGet("/rest/services/{folder}", (
            HttpContext context,
            PostgresLayerCatalog catalog,
            PostgresSystemServices system,
            string folder,
            CancellationToken cancellation) =>
            CatalogueAsync(context, catalog, system, folder, cancellation))
            .Governed(SharingGovernedExtensions.ByFiltering);

        // Where the system services live. ArcGIS puts the geometry service in a
        // Utilities folder and every client that looks for one looks there.
        app.MapGet("/rest/services/Utilities", (
            HttpContext context,
            PostgresLayerCatalog catalog,
            PostgresSystemServices system,
            CancellationToken cancellation) =>
            CatalogueAsync(context, catalog, system, "Utilities", cancellation))
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>Two URL spaces, and a layer answers on exactly one.</b> Hosted
        // services live under the folder; registered ones live at the root. A
        // hosted layer asked for at the root is redirected rather than served,
        // so the separation is a fact about the server and not a convention
        // clients may ignore.
        // <b>Any folder, since 2026-08-17.</b> This was two literal prefixes — the root and
        // `hosted` — so a service in any other folder had no route at all: the owner's
        // `turkiye` folder would have answered 404 while the catalogue happily listed it.
        // `{folder}` is a parameter, and a literal segment beats a parameter in routing, so
        // the hosted and Utilities routes registered above still win where they apply.
        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            // <b>{layerId}, not /0.</b> Every route in this server ended in a
            // literal zero until 2026-08-15, because one published layer was one
            // service and there could never be a layer 1. A service is a
            // container of layers, so the number is now real.
            app.MapGet($"{prefix}/{{serviceName}}/FeatureServer", ServiceMetadataAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}", LayerMetadataAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/query", QueryAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapPost($"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/query", QueryAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapPost(
                $"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/applyEdits",
                ApplyEditsAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // <b>The three single-operation endpoints ArcGIS also offers.</b>
            // applyEdits is the one a modern client uses and the only one that
            // can be transactional across operations, but plenty of tooling —
            // older ArcGIS clients, scripts, anything written against the
            // 10.x documentation — posts to these instead and gets a 404 from a
            // server that can do exactly what was asked. They are thin: each
            // rewrites its own parameter into the batch applyEdits already
            // takes, so there is one writer, one audit path and one place where
            // rollback is decided.
            foreach ((string route, EditOperation operation) in
                ((string, EditOperation)[])
                [
                    ("addFeatures", EditOperation.Add),
                    ("updateFeatures", EditOperation.Update),
                    ("deleteFeatures", EditOperation.Delete),
                ])
            {
                EditOperation which = operation;

                app.MapPost(
                    $"{prefix}/{{serviceName}}/FeatureServer/{{layerId:int}}/{route}",
                    (HttpContext context,
                     string serviceName,
                     int layerId,
                     CatalogFallback catalog,
                     LayerConnections connections,
                     ServiceContexts contexts,
                     IAuditLog audit,
                     CancellationToken cancellation) =>
                        ApplyEditsAsync(
                            context, serviceName, layerId, catalog, connections, contexts,
                            audit, cancellation, which))
                    .Governed(SharingGovernedExtensions.ByService);
            }
        }

        VectorTileEndpoints.Map(app);
        AttachmentEndpoints.Map(app);
        RelationshipEndpoints.Map(app);
        GeometryServerEndpoints.Map(app);
        HostedDataEndpoints.Map(app);

        app.MapAuth();
        app.MapAdmin();
        app.MapPost("/rest/setup", AuthEndpoints.SetupAsync);

        // The only way a browser gets a session. See Authentication.CookieToken
        // for why that session can only read.
        app.MapGet("/rest/login", (HttpContext context) => Results.Content(
            RestDirectory.SignIn(
                context.Request.Query["return"].ToString(),
                context.Request.Query["failed"].Count > 0
                    ? "That name and password were not accepted."
                    : null),
            "text/html; charset=utf-8"));

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

    /// <summary>
    /// Lists the services a caller may see, in one folder or at the root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A filter, not a gate.</b> ADR-018 §3b governs reading by sharing, so
    /// the catalogue lists what this caller may see rather than refusing the
    /// whole endpoint — which is what makes two layers with two audiences
    /// possible.
    /// </para>
    /// <para>
    /// <b>A stopped service is listed for somebody who can administer it and
    /// hidden from everybody else.</b> To a data consumer a stopped service is
    /// indistinguishable from an absent one, and listing it would offer a
    /// service that answers 503 to every click.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Every folder the root advertises: the register, and whatever services name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A union, because the two can disagree and neither is a lie.</b> The register holds
    /// folders that exist while empty — which is why it exists (migration 18) — and a
    /// service's own <c>folder</c> is where that service actually answers. Migration 18 adds
    /// no foreign key between them, so advertising only the register would hide a folder that
    /// serves a URL, and advertising only what services say would lose the empty folder
    /// somebody just made.
    /// </para>
    /// <para>
    /// <b>Reserved names are added last and unconditionally.</b> <c>hosted</c> is where the
    /// next datastore publish goes and <c>Utilities</c> is the geometry service's address; a
    /// directory that stopped listing them because a register row was deleted would be a
    /// directory that disagrees with its own URLs.
    /// </para>
    /// </remarks>
    /// <summary>Whether the register holds this folder.</summary>
    /// <remarks>
    /// <b>Blind means yes.</b> If the platform store cannot be read, a folder whose services
    /// are also unreadable would 404 — turning a store outage into *your folder does not
    /// exist*, which is a worse answer than an empty directory (ADR-026).
    /// </remarks>
    private static async Task<bool> FolderExistsAsync(
        PostgresLayerCatalog catalog, string folder, CancellationToken cancellation)
    {
        try
        {
            foreach (string named in await catalog.ListFolderNamesAsync(cancellation)
                .ConfigureAwait(false))
            {
                if (named.Equals(folder, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Npgsql.NpgsqlException)
        {
            return true;
        }
    }

    private static async Task<string[]> FoldersAsync(
        PostgresLayerCatalog catalog,
        PostgresSystemServices system,
        IReadOnlyList<PublishedService> services,
        CancellationToken cancellation)
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase)
        {
            FeatureServerMetadataWriter.HostedFolder,
            "Utilities",
        };

        foreach (PublishedService service in services)
        {
            if (service.Folder is { Length: > 0 } named)
            {
                names.Add(named);
            }
        }

        foreach (SystemService service in await system.ListAsync(cancellation).ConfigureAwait(false))
        {
            if (service.Folder is { Length: > 0 } named)
            {
                names.Add(named);
            }
        }

        try
        {
            foreach (string named in await catalog.ListFolderNamesAsync(cancellation)
                .ConfigureAwait(false))
            {
                names.Add(named);
            }
        }
        catch (Npgsql.NpgsqlException)
        {
            // Blind, and the directory still answers — ADR-026. The folders above came from
            // services this caller can already see, so the list is smaller rather than wrong.
        }

        return [.. names];
    }

    private static async Task<IResult> CatalogueAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        PostgresSystemServices system,
        string? folder,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        IReadOnlyList<PublishedService> services = await catalog.ListServicesAsync(cancellation)
            .ConfigureAwait(false);

        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        // <b>Any folder holds services now, and this is the second time this line has been
        // wrong about that.</b> It began as `folder is not null`, which made every non-root
        // folder the hosted one — so /rest/services/Utilities listed all five hosted layers.
        // The fix named the two folders that could hold services, which was true until the
        // owner asked for a third on 2026-08-17. So the comparison is now just the folder
        // against the folder, with no list of which ones are allowed to have contents.
        List<PublishedService> visible =
        [
            .. services.Where(service =>
                string.Equals(
                    service.Folder ?? string.Empty,
                    folder ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                && (service.IsRunning || seesStopped)
                && LayerAccess
                    .Evaluate(
                        service.Sharing, service.Owner, current.Principal, current.Authorization)
                    .IsAllowed()),
        ];

        // The folder is advertised at the root whether or not anything in it is
        // visible to this caller. Hiding an empty folder would make its
        // emptiness depend on who is asking, and a client that caches the root
        // would then never look inside it again.
        //
        // <b>Read rather than typed, since 2026-08-17.</b> This was the literal
        // list `["hosted", "Utilities"]`, so a service in any other folder was
        // reachable at its URL and invisible to every client that browses the
        // catalogue — the owner asked for a `turkiye` folder and it would not
        // have appeared here. Migration 18 made folders a register; this reads
        // it, unioned with what the services in hand actually say, so a folder
        // cannot be advertised-but-absent or present-but-hidden.
        //
        // <b>The register is read through the same fallback the services are.</b>
        // A folder list is not authorization-sensitive and must survive a
        // platform-store outage (ADR-026): if the register cannot be read, the
        // folders the visible services name are still the truth about where
        // those services are.
        string[] folders = folder is null
            ? await FoldersAsync(catalog, system, services, cancellation).ConfigureAwait(false)
            : [];

        // <b>System services are services.</b> Owner correction 2026-08-15: the
        // geometry service belongs in the directory beside the layers, governed
        // by the same sharing, or an administrator browsing the server cannot
        // see half of what it offers.
        List<(string Name, string Type)> systemServices =
        [
            .. (await system.ListAsync(cancellation).ConfigureAwait(false))
                .Where(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase)
                    && LayerAccess
                        .Evaluate(s.Sharing, null, current.Principal, current.Authorization)
                        .IsAllowed())
                .Select(s => (
                    Name: folder is null ? s.Name : $"{folder}/{s.Name}",
                    Type: s.Kind)),
        ];

        // <b>Only hosted services have tile services (Q-67).</b> The spatial
        // reference no longer decides: a layer keeps the projection it arrived
        // in and the tile path transforms per request (owner correction
        // 2026-08-15, Q-96). Filtering on 3857 here would hide a tile service
        // that works.
        // <b>A folder that is not a folder answers 404, rather than an empty directory.</b>
        // Before the register existed every folder name "existed" and listed nothing, so a
        // typo looked like an empty folder and no client could tell the two apart. The
        // register makes the difference knowable: one it holds lists — empty is a legitimate
        // state for a folder somebody just made — and a name nothing points at is not a
        // folder at all.
        //
        // <b>Not privilege-dependent, deliberately.</b> A registered folder lists for
        // everybody and its contents are already filtered by sharing above. Making a
        // folder's existence depend on who asks is what the comment below warns against.
        if (folder is not null && visible.Count == 0 && systemServices.Count == 0
            && !await FolderExistsAsync(catalog, folder, cancellation).ConfigureAwait(false))
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = 404,
                        message = $"No folder '{folder}'. A folder is created by publishing "
                                + "into it, or through POST /admin/folders.",
                    },
                },
                statusCode: 404);
        }

        List<PublishedService> tileable =
        [
            .. visible.Where(s =>
                s.Layers.Count > 0 && s.Layers.All(l => l.Definition.IsHosted)),
        ];

        List<(string Name, string Type)> everything =
        [
            .. visible.Select(s => (s.QualifiedName, Type: "FeatureServer")),
            .. tileable.Select(s => (s.QualifiedName, Type: "VectorTileServer")),
            .. systemServices,
        ];

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            return Results.Content(
                RestDirectory.Folder(
                    context.Request.Path,
                    folder,
                    FeatureServerMetadataWriter.CurrentVersion,
                    folders,
                    everything),
                "text/html; charset=utf-8");
        }

        return Results.Ok(FeatureServerMetadataWriter.Catalogue(
            visible.Select(s => s.Name),
            folders,
            folder,
            tileable.Select(s => s.Name),
            systemServices));
    }

    /// <summary>
    /// The qualified service path out of a directory request path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/rest/services/hosted/parcels/FeatureServer/1</c> yields
    /// <c>hosted/parcels</c>, and <c>/rest/services/parcels/FeatureServer</c>
    /// yields <c>parcels</c>. Everything between the directory root and the
    /// service type, whatever the folder depth.
    /// </para>
    /// <para>
    /// <b>Read from the request rather than rebuilt from the model.</b> A layer
    /// carries its bare service name, so composing a URL from it silently drops
    /// the folder — which is how a link to a hosted layer pointed at a
    /// root-folder service that does not exist. The path being answered is the
    /// one thing guaranteed to name the service the caller actually reached.
    /// </para>
    /// </remarks>
    private static string ServicePathOf(PathString path)
    {
        string text = path.Value ?? string.Empty;
        const string root = "/rest/services/";

        int start = text.IndexOf(root, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += root.Length;

        int end = text.IndexOf("/FeatureServer", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = text.IndexOf("/VectorTileServer", start, StringComparison.OrdinalIgnoreCase);
        }

        return end < 0 ? text[start..].Trim('/') : text[start..end].Trim('/');
    }

    /// <summary>
    /// A service's layers and groups, depth-first, with their nesting depth.
    /// </summary>
    /// <remarks>
    /// <b>Depth-first from the roots, not a sort by index.</b> Sorting by index
    /// puts a group's children next to it only while nothing was added
    /// afterwards; a layer published into group 0 after group 5 exists would sit
    /// at the bottom of the list under the wrong heading. Walking the tree is
    /// the only ordering that stays true.
    /// </remarks>
    private static List<(string Label, string Href, int Depth)> LayerTree(
        PublishedService service, string path)
    {
        List<(string Label, string Href, int Depth)> entries = [];

        void Walk(int? parent, int depth)
        {
            foreach (int index in service.ChildrenOf(parent))
            {
                bool isGroup = service.Group(index) is not null;

                entries.Add((
                    Label: NameOf(service, index),
                    Href: $"{path}/{index}",
                    Depth: depth));

                if (isGroup)
                {
                    Walk(index, depth + 1);
                }
            }
        }

        Walk(null, 0);
        return entries;
    }

    /// <summary>
    /// Answers a query that asked for something other than features.
    /// </summary>
    /// <remarks>
    /// <b>Every one of these runs the same parsed query through the same
    /// source.</b> A count computed by a different code path from the features
    /// it counts is a count that can disagree with them, and the disagreement
    /// appears only when somebody compares the two.
    /// </remarks>
    private static async Task AlternateShapeAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription described,
        IFeatureSource source,
        FeatureQuery query,
        QueryShape shape,
        bool html,
        CancellationToken cancellation)
    {
        if (source is not PostGisFeatureSource postgis)
        {
            // Every source in this build is PostGIS. Said out loud rather than
            // cast blindly, so a second provider fails here with a sentence
            // instead of an InvalidCastException in a stack trace.
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 501,
                        message =
                            "Counts, ids, extents and statistics are implemented for the PostGIS "
                            + "provider only, and this layer is served by another.",
                    },
                },
                statusCode: StatusCodes.Status501NotImplemented)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        switch (shape)
        {
            case QueryShape.Count:
            {
                long count = await postgis.CountAsync(query, cancellation).ConfigureAwait(false);

                if (html)
                {
                    await QueryPage
                        .WriteCountAsync(context, layer, described, count, cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                // What an ArcGIS client asks before it starts paging.
                await Results.Json(new { count }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            case QueryShape.Ids:
            {
                IReadOnlyList<long> ids = await postgis
                    .ObjectIdsAsync(query, cancellation).ConfigureAwait(false);

                if (html)
                {
                    await QueryPage
                        .WriteIdsAsync(context, layer, described, ids, cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                await Results.Json(new
                {
                    objectIdFieldName = layer.Definition.ObjectIdColumn,
                    objectIds = ids,
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            case QueryShape.Extent:
            {
                (Envelope? extent, long count) = await postgis
                    .ExtentAsync(query, cancellation).ConfigureAwait(false);

                int srid = query.OutSrid ?? layer.Definition.Srid;

                if (html)
                {
                    await QueryPage
                        .WriteExtentAsync(context, layer, described, extent, count, cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                await Results.Json(new
                {
                    count,
                    extent = extent is { } box
                        ? new
                        {
                            xmin = box.MinX,
                            ymin = box.MinY,
                            xmax = box.MaxX,
                            ymax = box.MaxY,
                            spatialReference = new { wkid = srid, latestWkid = srid },
                        }
                        : null,
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            default:
            {
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await postgis
                    .StatisticsAsync(query, cancellation).ConfigureAwait(false);

                if (html)
                {
                    await QueryPage
                        .WriteStatisticsAsync(context, layer, described, rows, cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                // <b>Shaped as features with no geometry, which is what ArcGIS
                // returns.</b> A client reads statistics through the same code
                // path it reads features with; inventing a different envelope
                // would mean every client needed a special case for us.
                await Results.Json(new
                {
                    displayFieldName = string.Empty,
                    fields = FieldsOf(rows),
                    features = rows.Select(r => new { attributes = r }),
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>The field list for a statistics response, from its first row.</summary>
    /// <remarks>
    /// <b>From the row rather than from the request</b>, because the grouping
    /// fields and the computed ones both appear and only the database knows what
    /// type an average of an integer column came back as.
    /// </remarks>
    private static object[] FieldsOf(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        List<object> fields = [];

        foreach (KeyValuePair<string, object?> cell in rows[0])
        {
            fields.Add(new
            {
                name = cell.Key,
                alias = cell.Key,
                type = cell.Value switch
                {
                    null => "esriFieldTypeString",
                    long or int or short => "esriFieldTypeInteger",
                    double or float or decimal => "esriFieldTypeDouble",
                    DateTime => "esriFieldTypeDate",
                    _ => "esriFieldTypeString",
                },
            });
        }

        return [.. fields];
    }

    /// <summary>What sits at an index — a layer's name, a group's, or the number.</summary>
    private static string NameOf(PublishedService service, int index)
    {
        if (service.Layer(index) is { } layer)
        {
            return $"{layer.Definition.Name} ({index})";
        }

        if (service.Group(index) is { } group)
        {
            return $"{group.Name} ({index}) — group";
        }

        return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A layer's relationships, in the shape an ArcGIS client reads.
    /// </summary>
    /// <remarks>
    /// <b>Both sides, with the role named.</b> A relationship appears on each
    /// participating layer — a parcel's document lists its owners and the
    /// owners' lists its parcels — and reporting only the ones where this layer
    /// is the origin makes half of them undiscoverable.
    /// </remarks>
    /// <remarks>
    /// <b>Empty when the platform store is unreachable, and the document says
    /// so.</b> Relationships live only in the platform store, so while blind
    /// there is no way to report them and no remembered copy to report from
    /// (Q-95). Reporting none is a lie by omission unless the caller is told,
    /// which is what <c>catalogStale</c> on the layer document is for.
    /// </remarks>
    private static async Task<IEnumerable<object>> RelationshipsForAsync(
        PublishedLayer layer,
        PostgresRelationshipCatalog relationships,
        CatalogFallback catalog,
        CancellationToken cancellation)
    {
        if (catalog.Catalog is not { } layers)
        {
            return [];
        }

        IReadOnlyList<LayerRelationship> declared;

        try
        {
            declared = await relationships.ForLayerAsync(layer.Id, cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (CatalogFallback.IsUnreachable(e))
        {
            return [];
        }

        if (declared.Count == 0)
        {
            return [];
        }

        List<object> reported = [];

        foreach (LayerRelationship relationship in declared)
        {
            bool fromOrigin = relationship.OriginLayerId == layer.Id;

            Guid otherId = fromOrigin ? relationship.RelatedLayerId : relationship.OriginLayerId;

            PublishedLayer? other =
                await layers.FindByIdAsync(otherId, cancellation).ConfigureAwait(false);

            reported.Add(new
            {
                id = relationship.Id,
                name = relationship.Name,
                relatedTableName = other?.Definition.Name,

                // ArcGIS names the direction from the reader's point of view.
                role = fromOrigin ? "esriRelRoleOrigin" : "esriRelRoleDestination",
                keyField = fromOrigin ? relationship.OriginKey : relationship.RelatedKey,
                cardinality = relationship.Cardinality == RelationshipCardinality.OneToOne
                    ? "esriRelCardinalityOneToOne"
                    : "esriRelCardinalityOneToMany",
                composite = relationship.Composite,
            });
        }

        return reported;
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



    private static async Task ServiceMetadataAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        // The feature face, if configured off, answers as absent — ADR-031
        // condition 2. Asserted here as well as in ServiceLookup.LayerAsync,
        // because a service document is reachable without resolving a layer and a
        // document listing layers nobody may then read is worse than no document.
        if (!service.Limits.AllowsFeatures(dataSupportsIt: true))
        {
            await Authorize.RefuseReadAsync(context, service.Name).ConfigureAwait(false);
            return;
        }

        // <b>Every layer's extent, which means every layer's shape query.</b>
        // ServiceContexts caches those for thirty seconds, so a three-layer
        // service costs three cached reads rather than three round trips — but
        // a service with fifty layers would pay fifty on a cold cache, and that
        // is the cost of a document that states a real full extent.
        List<FeatureServerMetadataWriter.ServiceLayer> layers = [];

        foreach (PublishedLayer layer in service.Layers)
        {
            (_, LayerDescription described) = await contexts.GetAsync(layer, cancellation)
                .ConfigureAwait(false);

            layers.Add(new FeatureServerMetadataWriter.ServiceLayer(
                layer.LayerIndex,
                layer.Definition.Name,
                layer.GeometryType,
                layer.Definition.Srid,
                described.Extent)
            {
                ParentId = layer.ParentIndex,
            });
        }

        List<FeatureServerMetadataWriter.ServiceGroup> groups =
        [
            .. service.Groups.Select(g => new FeatureServerMetadataWriter.ServiceGroup(
                g.Index, g.Name, g.ParentIndex, service.ChildrenOf(g.Index))),
        ];

        // <b>The service's own row ceiling, advertised rather than only enforced.</b>
        // ADR-031: what is served is the intersection, and a document that reports the
        // server's figure while the query path applies a lower one sends every paging
        // client to a page size that does not exist.
        object document = FeatureServerMetadataWriter.Service(
            layers,
            CapabilitiesFor(context, service),
            service.Description,
            groups,
            service.Limits.Cost.MaximumRecordCount);

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            await Results.Content(
                RestDirectory.Document(
                    context.Request.Path,
                    $"{service.QualifiedName} (FeatureServer)",
                    document,

                    // <b>View In, as an ArcGIS Server directory offers it —
                    // added 2026-08-16.</b> The layers themselves are the tree
                    // below; this is the thing somebody does before reading any
                    // of it, which is look at the service on a map. Without a
                    // layer id the viewer draws every feature layer the service
                    // has, because "View In" on a service means the service.
                    //
                    // Not ArcGIS Online's viewer: that hands this URL to
                    // arcgis.com and needs an ArcGIS account, which is the
                    // account this product exists for people not to need.
                    links:
                    [
                        ("Map", "/studio/view.html"
                            + $"?service={Uri.EscapeDataString(service.QualifiedName)}"),
                        ("ArcGIS SDK", "/studio/map.html"
                            + $"?service={Uri.EscapeDataString(service.QualifiedName)}"),
                    ],
                    linksLabel: "View in",
                    tree: LayerTree(service, context.Request.Path)),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Ok(document).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task LayerMetadataAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        PostgresRelationshipCatalog relationships,
        CancellationToken cancellation)
    {
        // <b>A group answers at its own index.</b> The service document lists
        // it, so a client following subLayerIds — or a person clicking it in the
        // directory — arrives here, and a 404 for an id the service advertised
        // is the kind of self-contradiction that makes a client abandon the
        // whole service.
        PublishedService? owning = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (owning is null)
        {
            return;
        }

        if (owning.Group(layerId) is { } group)
        {
            FeatureServerMetadataWriter.ServiceGroup entry = new(
                group.Index, group.Name, group.ParentIndex, owning.ChildrenOf(group.Index));

            object groupDocument = FeatureServerMetadataWriter.GroupLayerDocument(
                entry, CapabilitiesFor(context, owning));

            if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
            {
                await Results.Content(
                    RestDirectory.Document(
                        context.Request.Path,
                        $"{owning.Name} - {group.Name} ({group.Index}, group layer)",
                        groupDocument,
                        [.. entry.ChildIds.Select(id => (
                            Label: NameOf(owning, id),
                            Href: $"/rest/services/{owning.QualifiedName}/FeatureServer/{id}"))],
                        linksLabel: "Contains"),
                    "text/html; charset=utf-8")
                    .ExecuteAsync(context).ConfigureAwait(false);

                return;
            }

            await Results.Ok(groupDocument).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, catalog, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        (_, LayerDescription description) = await contexts.GetAsync(layer, cancellation)
            .ConfigureAwait(false);

        object document = FeatureServerMetadataWriter.Layer(
            layer.Definition,
            layer.GeometryType,
            description,
            CapabilitiesFor(context, layer),
            await RelationshipsForAsync(layer, relationships, catalog, cancellation)
                .ConfigureAwait(false),
            layer.LayerIndex,
            layer.Cost.MaximumRecordCount);

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            await Results.Content(
                RestDirectory.Document(
                    context.Request.Path,
                    $"{layer.ServiceName} - {layer.Definition.Name} ({layer.LayerIndex})",
                    document,
                    // <b>The query page, and nothing that runs a query.</b>
                    // This link carried where=1%3D1&outFields=*&f=json until
                    // 2026-08-15, because there was no page to send anybody to —
                    // so clicking Query in a browser executed an unfiltered read
                    // and printed a wall of JSON. Opening a form is what the
                    // link should do; running the query is what the button in it
                    // is for.
                    //
                    // <b>And a map, added 2026-08-16.</b> An ArcGIS Server
                    // directory offers "View In" here and it is the first thing
                    // somebody does with a layer they did not publish: see whether
                    // it draws, and where. The viewer was already in this
                    // repository and nothing linked to it.
                    //
                    // <b>Deliberately not ArcGIS Online's map viewer</b>, which is
                    // the other half of what Esri offers. That link hands this
                    // service's URL to arcgis.com and needs an ArcGIS account —
                    // the account this product exists for people not to need — and
                    // on an internal server it tells a third party the URL exists.
                    // <b>The service path comes from the request, not from the
                    // model.</b> `layer.ServiceName` is the bare name, so building
                    // the link from it dropped the folder and produced
                    // `?service=look_EarlyAlert` for a layer that lives at
                    // `hosted/look_EarlyAlert` — the same class of guess as D-45,
                    // one edit later. The path being answered already carries the
                    // qualified name exactly.
                    //
                    // <b>Two viewers, because they do two jobs.</b> `Map` is
                    // OpenLayers, vendored, no third-party request — what somebody
                    // opens to look at their data. `ArcGIS SDK` is Esri's own
                    // client from Esri's CDN, which is ADR-020 §4's argument kept
                    // where it belongs: a compatibility probe. Offering both makes
                    // the two jobs visible as two jobs instead of one page trying
                    // to be both.
                    [
                        ("Map", "/studio/view.html"
                            + $"?service={Uri.EscapeDataString(ServicePathOf(context.Request.Path))}"
                            + $"&layer={layer.LayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
                        ("ArcGIS SDK", "/studio/map.html"
                            + $"?service={Uri.EscapeDataString(ServicePathOf(context.Request.Path))}"
                            + $"&layer={layer.LayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
                        ("Query", context.Request.Path + "/query"),
                    ]),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Ok(document).ExecuteAsync(context).ConfigureAwait(false);
    }


    /// <summary>
    /// ArcGIS <c>applyEdits</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The privilege mapping is stricter than ArcGIS Portal's, deliberately.</b>
    /// Portal separates <em>edit</em> — add, and change your own features — from
    /// <em>full edit</em>, which reaches everybody's. That distinction rests on
    /// editor tracking, which records who created each row, and editor tracking
    /// is deferred (Q-58). Without it we cannot tell whose feature is whose, so
    /// treating an update as "probably yours" would be a guess with somebody
    /// else's data. Adds need <c>features:edit</c>; updates and deletes need
    /// <c>features:fullEdit</c>, and that is narrower than Portal until Q-58
    /// lands.
    /// </para>
    /// <para>
    /// <b>Editing requires being able to read.</b> The sharing scope is checked
    /// first, and a layer that is not visible to the caller answers exactly as it
    /// does for a query — 404, indistinguishable from absent, so the endpoint
    /// cannot be used to discover layer names.
    /// </para>
    /// </remarks>
    /// <summary>Which endpoint asked, and therefore where the features go.</summary>
    private enum EditOperation
    {
        /// <summary>applyEdits: adds, updates and deletes together.</summary>
        Apply,

        /// <summary>addFeatures: one list, in "features".</summary>
        Add,

        /// <summary>updateFeatures: one list, in "features".</summary>
        Update,

        /// <summary>deleteFeatures: object ids, in "objectIds".</summary>
        Delete,
    }

    private static async Task ApplyEditsAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        LayerConnections connections,
        ServiceContexts contexts,
        IAuditLog audit,
        CancellationToken cancellation,
        EditOperation operation = EditOperation.Apply)
    {
        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, catalog, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
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
                            $"Layer '{layer.Definition.Name}' has no integer object-id column, so its features "
                            + "cannot be addressed for update or delete (ADR-013 §2a).",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // <b>The request-body ceiling, checked before the body is read</b> — Q-113.
        // Content-Length is advisory and a chunked request carries none, so this
        // refuses a request that *declares* too much while Kestrel's own limit stays
        // the backstop for one that lies. Checking it here rather than after parsing
        // is the whole point: an edit batch is parsed into memory, so a ceiling
        // applied afterwards would already have paid the cost it exists to avoid.
        if (layer.Cost.MaximumRequestBytes is { } maximumRequest
            && context.Request.ContentLength is { } declared
            && declared > maximumRequest)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 413,
                        message =
                            $"This request declares {declared} bytes and this service accepts at "
                            + $"most {maximumRequest}. Send the edits in smaller batches: "
                            + "applyEdits is transactional per call, so several calls are several "
                            + "transactions rather than one partial one.",
                    },
                },
                statusCode: StatusCodes.Status413PayloadTooLarge)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;

        // <b>One shape underneath.</b> The single-operation endpoints spell
        // their input differently — "features" for add and update, "objectIds"
        // for delete — and mean exactly one third of what applyEdits means.
        // Translating here rather than duplicating the handler keeps one writer,
        // one audit record and one rollback rule.
        string? features = Field(form, context, "features");

        string? adds = operation switch
        {
            EditOperation.Apply => Field(form, context, "adds"),
            EditOperation.Add => features,
            _ => null,
        };

        string? updates = operation switch
        {
            EditOperation.Apply => Field(form, context, "updates"),
            EditOperation.Update => features,
            _ => null,
        };

        string? deletes = operation switch
        {
            EditOperation.Apply => Field(form, context, "deletes"),
            EditOperation.Delete => Field(form, context, "objectIds"),
            _ => null,
        };

        // <b>deleteFeatures also takes a where clause in ArcGIS, and this
        // refuses it.</b> Deleting by predicate is a different risk from
        // deleting by identity: one mistyped clause removes a layer, and there
        // is nothing to undo it with — no versioning, no soft delete, no
        // preview of what would go. Refusing is recoverable and a wiped layer is
        // not, so the caller is told to resolve the clause to ids first with a
        // query they can look at.
        if (operation == EditOperation.Delete && Field(form, context, "where") is { } clause)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message =
                            "deleteFeatures accepts 'objectIds' and not 'where' on this server. "
                            + "Deleting by predicate removes an unknown number of features and "
                            + "nothing here can undo it \u2014 there is no versioning and no soft "
                            + "delete. Run the same clause through /query with returnIdsOnly=true, "
                            + $"look at what it selects, and pass those ids. (where: {clause})",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        // A single-operation endpoint called with nothing to do is a client
        // sending the wrong parameter name, not a request to do nothing.
        if (operation != EditOperation.Apply && adds is null && updates is null && deletes is null)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message = operation == EditOperation.Delete
                            ? "deleteFeatures needs 'objectIds': a comma-separated list."
                            : $"{(operation == EditOperation.Add ? "addFeatures" : "updateFeatures")}"
                              + " needs 'features': an array of ArcGIS features.",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        // Adds need less than updates and deletes do; asking for the wider
        // privilege on a batch that only adds would refuse a legitimate edit.
        if (adds is not null
            && !await Authorize.RequireAsync(context, Privilege.FeaturesEdit).ConfigureAwait(false))
        {
            return;
        }

        if ((updates is not null || deletes is not null)
            && !await Authorize.RequireAsync(context, Privilege.FeaturesFullEdit).ConfigureAwait(false))
        {
            return;
        }

        (_, LayerDescription description) = await contexts.GetAsync(layer, cancellation)
            .ConfigureAwait(false);

        ApplyEditsRequest.Parsed? parsed = ApplyEditsRequest.TryParse(
            adds, updates, deletes,
            RollbackOnFailure(form, context),
            layer.Definition, description.Fields,
            out string? malformed);

        if (parsed is null)
        {
            await Results.Json(
                new { error = new { code = 400, message = malformed } },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // <b>The edit ceiling, after parsing and before writing</b> — Q-113. It has
        // to be here: the count is only known once the batch has parsed, and the
        // thing being bounded is the transaction, not the parse. A batch refused at
        // this point has cost memory and no database work, which is the cheaper half
        // of the two.
        //
        // <b>Refused rather than truncated, and this is the one ceiling in Q-113 that
        // must not truncate.</b> Shortening a response is a smaller answer to the
        // same question; shortening a transaction is a *different edit* than the one
        // the caller asked for, applied silently. `rollbackOnFailure` exists so a
        // caller can insist on all-or-nothing, and a server that quietly applied
        // half would break that guarantee while reporting success.
        if (layer.Cost.MaximumEditsPerTransaction is { } maximumEdits
            && parsed.Batch.Count > maximumEdits)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 400,
                        message =
                            $"This batch carries {parsed.Batch.Count} edits and this service "
                            + $"accepts at most {maximumEdits} in one transaction. It is refused "
                            + "rather than trimmed: applying part of a batch would be a different "
                            + "edit than the one requested, and rollbackOnFailure exists so a "
                            + "caller can require all or nothing.",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        EditOutcome outcome = await connections
            .WriterFor(layer, description.Fields)
            .ApplyAsync(parsed.Batch, cancellation)
            .ConfigureAwait(false);

        await AuditEditsAsync(
            context, audit, $"{serviceName}/{layerId}", parsed, outcome, cancellation)
            .ConfigureAwait(false);

        // <b>ArcGIS answers each single-operation endpoint with only its own
        // results array.</b> A client posting addFeatures reads addResults and
        // nothing else; handing it three arrays, two of them empty, is a
        // different document from the one it was written against.
        await Results.Json(operation switch
        {
            EditOperation.Add =>
                ApplyEditsResponse.One(outcome, parsed, ApplyEditsResponse.EditKind.Add),
            EditOperation.Update =>
                ApplyEditsResponse.One(outcome, parsed, ApplyEditsResponse.EditKind.Update),
            EditOperation.Delete =>
                ApplyEditsResponse.One(outcome, parsed, ApplyEditsResponse.EditKind.Delete),
            _ => ApplyEditsResponse.Build(outcome, parsed),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Reads a field from the form, falling back to the query string.</summary>
    /// <remarks>
    /// ArcGIS clients POST <c>applyEdits</c> form-encoded. The query string is
    /// accepted too because it makes the endpoint testable with a plain URL, and
    /// because some clients do it for small batches.
    /// </remarks>
    private static string? Field(IFormCollection form, HttpContext context, string name)
    {
        if (form.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value)
            && !string.IsNullOrWhiteSpace(value))
        {
            return value.ToString();
        }

        return context.Request.Query.TryGetValue(name, out value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : null;
    }

    /// <summary>
    /// Whether one failure abandons the batch.
    /// </summary>
    /// <remarks>
    /// <b>Defaults to true, where ArcGIS defaults to false.</b> Partial
    /// application leaves the client responsible for reconciling a half-applied
    /// batch, and a client that does not read the per-feature results has
    /// silently lost data it believes it saved. The dangerous mode is available
    /// and has to be asked for.
    /// </remarks>
    private static bool RollbackOnFailure(IFormCollection form, HttpContext context)
    {
        string? value = Field(form, context, "rollbackOnFailure");

        return value is null
            || !bool.TryParse(value, out bool requested)
            || requested;
    }

    private static Task AuditEditsAsync(
        HttpContext context,
        IAuditLog audit,
        string layerName,
        ApplyEditsRequest.Parsed parsed,
        EditOutcome outcome,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        int failed = 0;

        foreach (IReadOnlyList<EditResult> results in
            (IReadOnlyList<EditResult>[])[outcome.Adds, outcome.Updates, outcome.Deletes])
        {
            foreach (EditResult result in results)
            {
                if (!result.Succeeded)
                {
                    failed++;
                }
            }
        }

        // Counts, not the features themselves. An audit row per edited feature
        // would make a ten-thousand-feature sync write ten thousand audit rows,
        // and the question an audit answers here is "who changed this layer and
        // how much", not "what were the coordinates".
        return audit.RecordAsync(
            new AuditEvent(
                current.Principal.Id,
                current.Principal.Name,
                context.Connection.RemoteIpAddress?.ToString(),
                "layer.applyEdits",
                layerName,
                JsonSerializer.Serialize(new
                {
                    adds = parsed.Batch.Adds.Count,
                    updates = parsed.Batch.Updates.Count,
                    deletes = parsed.Batch.Deletes.Count,
                    rejected = parsed.RejectedAdds.Count + parsed.RejectedUpdates.Count
                        + parsed.RejectedDeletes.Count,
                    failed,
                    outcome.RolledBack,
                }),

                // Rejections count. Counting only writer failures reported a
                // batch as successful when a feature never reached the writer,
                // which is the same mistake that made all-or-nothing a lie.
                !outcome.RolledBack && failed == 0
                    && parsed.RejectedAdds.Count == 0
                    && parsed.RejectedUpdates.Count == 0
                    && parsed.RejectedDeletes.Count == 0),
            cancellation);
    }

    /// <summary>
    /// What this caller may actually do with this layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per caller, not per service.</b> A client reads this string to decide
    /// whether to show an edit button, so reporting the service's theoretical
    /// capability would put that button in front of somebody who will be refused
    /// when they press it — never-degrade-silently, inverted.
    /// </para>
    /// <para>
    /// A layer with no integer object id can never be edited whatever the
    /// caller holds, because there is no way to name a row (ADR-013 §2a).
    /// </para>
    /// </remarks>
    /// <summary>
    /// What the caller may do across a whole service.
    /// </summary>
    /// <remarks>
    /// <b>The intersection, not the union.</b> One layer that cannot be served
    /// through ArcGIS — no integer identity, ADR-013 §2a — makes the service
    /// read-only, because a client reads one capabilities string for the service
    /// and offers one edit button. Claiming Update because two layers of three
    /// support it puts that button in front of a refusal.
    /// </remarks>
    /// <summary>
    /// What this caller may do in this service — the intersection of three things.
    /// </summary>
    /// <remarks>
    /// <b>ADR-031, and the shape is the decision.</b> Effective capability is what
    /// the data supports, intersected with what the service is configured to offer,
    /// intersected with what the caller's privileges allow. The first two lines
    /// below are the data; <c>PrivilegedCapabilities</c> is the caller;
    /// <c>Limits.Restrict</c> is the configuration, and it can only remove. There is
    /// no branch here that adds a capability, which is what stops a configured
    /// service from handing out what a role does not carry.
    /// </remarks>
    private static string CapabilitiesFor(HttpContext context, PublishedService service)
    {
        if (service.Layers.Count == 0 || service.Layers.Any(l => !l.Definition.IsArcGisServable))
        {
            return Join(service.Limits.Restrict(["Query"]));
        }

        return CapabilitiesFor(context, service.Layers[0], service.Limits);
    }

    private static string CapabilitiesFor(HttpContext context, PublishedLayer layer) =>
        CapabilitiesFor(context, layer, ServiceCapabilityLimits.Unset);

    private static string CapabilitiesFor(
        HttpContext context, PublishedLayer layer, ServiceCapabilityLimits limits)
    {
        if (!layer.Definition.IsArcGisServable)
        {
            return Join(limits.Restrict(["Query"]));
        }

        return Join(limits.Restrict(PrivilegedCapabilities(context)));
    }

    /// <summary>What the caller's privileges alone would allow.</summary>
    private static List<string> PrivilegedCapabilities(HttpContext context)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        List<string> capabilities = ["Query"];

        if (current.Authorization.Allows(Privilege.FeaturesEdit))
        {
            capabilities.Add("Create");
        }

        if (current.Authorization.Allows(Privilege.FeaturesFullEdit))
        {
            capabilities.Add("Update");
            capabilities.Add("Delete");
        }

        return capabilities;
    }

    /// <summary>
    /// The capability string, and an empty set is spelled as the empty string.
    /// </summary>
    /// <remarks>
    /// <b>A service configured to offer nothing says so.</b> ADR-031 §2a keeps
    /// <c>Query</c> revocable on purpose — a service that is running and refusing is
    /// a state distinct from stopped — so this has to be able to produce an empty
    /// value rather than quietly falling back to <c>Query</c>, which would make the
    /// setting look applied while doing nothing.
    /// </remarks>
    private static string Join(IReadOnlyList<string> capabilities) =>
        string.Join(",", capabilities);

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
    /// <summary>
    /// Registers the datastore as a data source, so hosted data can exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This repairs a feature that was never reachable.</b> <c>is_hosted</c>
    /// has been a column since schema version 1 and every insert wrote
    /// <c>false</c>, so no layer was ever hosted, so Q-67's rule — vector tiles
    /// come only from hosted data — refused every layer that has ever existed.
    /// The gap was invisible because the tile surface did not exist to be
    /// refused by it.
    /// </para>
    /// <para>
    /// <b>A failure here is loud and not fatal.</b> Feature services do not need
    /// the datastore registered; only tiles do. Refusing to start would take a
    /// working server down over a capability the deployment may never use.
    /// </para>
    /// </remarks>
    private static async Task EnsureDatastoreAsync(
        IServiceProvider services, HostSettings settings, ILogger logger)
    {
        try
        {
            await services.GetRequiredService<IAdminCatalog>()
                .EnsureDatastoreAsync(DatastoreConnection(settings.PlatformStore), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (NpgsqlException e)
        {
            Log.DatastoreNotRegistered(logger, e.Message);
        }
    }

    /// <summary>The key for the datastore's own connection pool.</summary>
    private const string DatastorePool = "datastore";

    /// <summary>
    /// The datastore connection, derived from the platform store's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The search path is deliberately dropped, and this was a real bug.</b>
    /// The platform store's connection sets <c>SearchPath</c> to the schema
    /// holding <c>layer</c>, <c>principal</c> and the rest. Reusing that string
    /// verbatim for the datastore inherits it — and PostGIS is installed
    /// somewhere else, usually <c>public</c>. Every spatial function then
    /// resolves to nothing: the first vector tile request came back
    /// <c>42883 function st_tileenvelope does not exist</c>, against a database
    /// that was up, connected and had PostGIS installed the whole time.
    /// </para>
    /// <para>
    /// Cleared rather than set to something: the empty value restores
    /// PostgreSQL's default of <c>"$user", public</c>, which finds a normally
    /// installed PostGIS without this code having to guess where it went.
    /// </para>
    /// </remarks>
    private static string DatastoreConnection(string platformStore) =>
        new NpgsqlConnectionStringBuilder(platformStore) { SearchPath = null }.ConnectionString;

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

        Log.SetupTokenIssued(
            logger,
            token,
            (int)SetupTokenLifetime.TotalMinutes,
            AuthEndpoints.MinimumPasswordLength);
    }

    private static long Microseconds(long ticks) =>
        ticks * 1_000_000 / Stopwatch.Frequency;

    private static async Task QueryAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IAuditLog audit,
        ILoggerFactory loggerFactory,
        HostSettings settings,
        CancellationToken cancellation)
    {
        // <b>D-30: the clock starts before the catalogue read, not after.</b>
        // The first version began timing just before the body and reported a
        // 1-row query as 17 ms with 5 ms accounted for. The missing twelve were
        // everything this handler does first — which is exactly the part a
        // measurement is supposed to expose rather than leave as a remainder.
        ILogger queryLog = loggerFactory.CreateLogger("query");

        bool tracing = queryLog.IsEnabled(LogLevel.Debug);
        long handlerStarted = tracing ? Stopwatch.GetTimestamp() : 0;

        // <b>Through ServiceLookup like everything else.</b> This endpoint used
        // to resolve the layer itself, and that is exactly how it became the one
        // path where the hosted/registered URL split was not enforced — metadata
        // and applyEdits redirected; query, the most-used of the three, served
        // happily from the wrong folder. A conformance test caught it. The
        // divergence was justified at the time by its refusals differing, and
        // they do not: the audit below needs the *reason*, not a different set
        // of answers, and the reason is a pure function of what we already have.
        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, catalog, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        // <b>The catalogue read is its own number, because it is its own round
        // trip.</b> Lumped into "setup" it looked like parsing overhead; it is
        // a second query to Postgres on every request, deliberately (D-17 —
        // it carries the sharing scope and the started/stopped status, and
        // those are not safe to remember). On a one-row query it is most of the
        // request, which is a fact worth being able to state.
        long lookedUp = tracing ? Stopwatch.GetTimestamp() : 0;

        if (layer is null)
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        LayerAccess.Reason reason = LayerAccess.Evaluate(
            layer.Sharing, layer.Owner, current.Principal, current.Authorization);

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
                    $"{serviceName}/{layerId}",
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
                            $"Layer '{layer.Definition.Name}' has no integer object-id column, so it cannot be "
                            + "served through the ArcGIS surface. It remains servable natively.",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // The described shape buys two things — outFields=* and validating a
        // field name against the database before it reaches SQL — and used to
        // cost two round trips on every request to buy them again. It is now
        // remembered for ServiceContexts.Lifetime (D-17). The catalogue read
        // above is still per request, deliberately: it carries the sharing scope
        // and the started/stopped status, and those are not safe to remember.
        (IFeatureSource source, LayerDescription described) = await contexts
            .GetAsync(layer, cancellation).ConfigureAwait(false);

        bool html = RestDirectory.WantsHtml(
            context.Request.Query["f"], context.Request.Headers.Accept);

        // <b>An empty query string in a browser is a request for the form.</b>
        // Running it as an unfiltered read and rendering the answer as a table
        // would make a link somebody clicks into a full scan of the layer.
        if (html && QueryPage.WantsForm(context.Request))
        {
            await QueryPage.WriteFormAsync(context, layer, described, cancellation)
                .ConfigureAwait(false);
            return;
        }

        // Q-113: the service's own cost ceilings, carried on the layer.
        ServiceCostCeilings cost = layer.Cost;

        if (!FeatureServerQueryParameters.TryParse(
                context.Request.Query,
                layer.Definition.ObjectIdColumn!,
                layer.Definition.Srid,
                described.Fields,
                out FeatureQuery? query,
                out QueryShape shape,
                out string? error,
                cost,
                FeatureServerQueryParameters.DefaultRecordCount))
        {
            await Results.Json(
                new { error = new { code = 400, message = error } },
                statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // Parameters accepted and ignored are logged rather than left invisible.
        // Each is a claim that ignoring it cannot lose data, and a claim nobody
        // can see is one nobody checks.
        foreach (string ignored in context.Request.Query.Keys)
        {
            if (FeatureServerQueryParameters.IsIgnored(ignored, out string why))
            {
                Log.QueryParameterIgnored(queryLog, ignored, layer.Definition.Name, why);
            }
        }

        // <b>Four shapes that are not a feature collection.</b> Each replaces
        // the response entirely, which is why the parser refuses a request that
        // asks for two of them: there would be no honest way to answer both.
        if (shape is not QueryShape.Features)
        {
            await AlternateShapeAsync(
                context, layer, described, source, query!, shape, html, cancellation)
                .ConfigureAwait(false);
            return;
        }

        if (html)
        {
            // <b>The same source and the same parsed query as the JSON path.</b>
            // A second way of running the query is a second set of answers, and
            // the whole point of the page is that it shows what a client will
            // get.
            await QueryPage
                .WriteResultsAsync(context, layer, described, source, query!, cancellation)
                .ConfigureAwait(false);
            return;
        }

        // <b>The body's size ceiling (Q-113).</b> Passed in rather than read here,
        // because the writer is the only place that knows how many bytes it has
        // committed — and a ceiling enforced anywhere else would have to buffer the
        // response to measure it, which is the allocation A-037 measured as the
        // binding constraint.
        FeatureServerQueryWriter writer = new(
            layer.Definition, cost.ResponseBytes(settings.MaximumResponseBytes));

        context.Response.ContentType = "application/json; charset=utf-8";

        // <b>D-30. Nothing is timed unless the logger that would read it is
        // on</b> — no trace object, no timestamps, and no branch inside the row
        // loop that does any work. That is the only way an instrument earns the
        // right to stay in a hot path. See QueryTrace.
        using QueryTrace.Scope trace = tracing ? QueryTrace.Begin() : default;

        long bodyStarted = tracing ? Stopwatch.GetTimestamp() : 0;

        // Written straight to the response body. Nothing is buffered, because
        // A-037 measured allocation as the binding constraint and a serialised
        // copy of a 50,000-feature result is exactly the kind of peak it warns
        // about.
        await using Utf8JsonWriter json = new(context.Response.BodyWriter);

        try
        {
            await writer.WriteAsync(json, source, query!, layer.GeometryType, cancellation)
                .ConfigureAwait(false);

            if (tracing && trace.Trace is { } recorded)
            {
                long finished = Stopwatch.GetTimestamp();

                long total = Microseconds(finished - handlerStarted);
                long lookup = Microseconds(lookedUp - handlerStarted);
                long prepare = Microseconds(bodyStarted - lookedUp);
                long body = Microseconds(finished - bodyStarted);

                // <b>Serialise is a remainder, and it is labelled as one.</b>
                // JSON writing and the flush to the socket are interleaved with
                // the row loop, so timing them directly would mean a stopwatch
                // pair per feature — which at a thousand features is the
                // instrument outweighing the thing. Subtracting what is measured
                // from the body is honest as long as nobody reads it as pure
                // encoding, hence the wording of the message.
                long serialise = Math.Max(
                    0, body - recorded.SqlMicroseconds - recorded.DecodeMicroseconds);

                Log.QueryTimings(
                    queryLog,
                    layer.Definition.Name,
                    total,
                    lookup,
                    prepare,
                    recorded.SqlMicroseconds,
                    recorded.DecodeMicroseconds,
                    serialise,
                    recorded.Rows,
                    recorded.Vertices,
                    json.BytesCommitted + json.BytesPending);
            }
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
