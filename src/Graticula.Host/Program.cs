using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Features;
using System.Diagnostics;
using Graticula.Diagnostics;
using Graticula.Geometries;
using Graticula.Raster.Tiff;
using Graticula.Coverages;
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

        /*
          <b>Default pool settings, and a twenty-second `ConnectionIdleLifetime` was tried here
          and taken out again.</b> [D-110](../../docs/architecture-debt.md): the background
          workers now back off to thirty seconds when idle, and the thought was that a lifetime
          under the poll would let the pool reach zero. Measured, it did not — two backends,
          idle 13.4 s, in the same place as before. The poll returns the connection and takes it
          again inside the window the pruner needs, so the floor moves from *held for ever* to
          *churned*, which is not what the number was added for.

          <b>So it is not here, because a setting that does not do what it was added for is
          worse than the default.</b> What the measurement says is that the remaining floor is
          the pollers sharing this pool at all, and the repayable form is their own — recorded
          in the row rather than guessed at here.
        */
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
        // <b>ADR-007 §4.8's N3, the last of that section's four requirements to land.</b>
        // One per worker, because the state it holds is *which source failed a moment ago*
        // and that is a fact about this process's view of the world. Registered here
        // rather than beside the connection pools because its first two consumers are the
        // two that read the platform store — D-131's eight seconds were four of each.
        builder.Services.AddSingleton(services => new SourceBreaker(
            services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        builder.Services.AddSingleton(services => new CatalogFallback(
            services.GetRequiredService<PostgresLayerCatalog>(),
            services.GetRequiredService<TimeProvider>(),
            settings.CatalogFallbackWindow,
            services.GetRequiredService<SourceBreaker>()));

        // <b>Read once at startup.</b> The directory listing is the set of font
        // stacks and it cannot change while the process runs, so scanning it per
        // request would be work in exchange for nothing.
        builder.Services.AddSingleton(_ => new GlyphStore(GlyphStore.BesideThisOne()));

        // <b>The rasteriser, behind its port, and this is the only line that names
        // the adapter.</b> ADR-041 §5.1: everything that draws asks for
        // IMapCanvasFactory and receives whatever is registered here, so replacing
        // the implementation is this line and nothing else. A singleton because the
        // factory holds nothing; the canvases it makes are per request and disposed.
        builder.Services.AddSingleton<IMapCanvasFactory>(
            _ => new Graticula.Render.Skia.SkiaMapCanvasFactory());

        // <b>The geodatabase reader, and the loop that uses it.</b> Registered even when the executable
        // is absent: `Available` is the question every caller asks, and a missing registration would
        // turn a deployment that did not ship it into a startup failure rather than a server that
        // refuses one kind of upload with a sentence. ADR-037 §5a, ADR-034 §5j.
        builder.Services.AddSingleton(services => new GeodatabaseReader(
            GeodatabaseReader.ExecutableBesideThisOne(),
            services.GetRequiredService<ILogger<GeodatabaseReader>>()));

        builder.Services.AddSingleton<ImportScratch>();
        // <b>One signal, shared by whoever enqueues and whoever waits.</b> D-110: the
        // workers back off to half a minute when idle so the platform-store pool can
        // finally prune, and this is what keeps that from costing latency for work this
        // node was asked to do.
        builder.Services.AddSingleton<JobSignal>();

        builder.Services.AddHostedService<GeodatabaseInspector>();
        builder.Services.AddHostedService<GeodatabaseImporter>();

        builder.Services.AddSingleton<TileSingleFlight>();

        // <b>ADR-007 §4.8's connection cap, which the ADR has required since 2026-08-12.</b>
        // Registered before `LayerConnections` because that is what consumes it: every read path
        // gets its source from there, so one wrapper bounds them all. Q-04 has the numbers.
        builder.Services.AddSingleton(new ConnectionBudget(
            settings.ConnectionBudget,
            settings.PerSourceConcurrency,
            wait: null,
            waitersPerPermit: settings.QueueWaitersPerPermit));

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

        // <b>What each role grants, read from the store — ADR-035.</b> A singleton because it holds
        // the answer between requests; registered as both the interface and the concrete type
        // because the authentication path calls `EnsureFreshAsync`, which is not on the interface:
        // freshness is this implementation's problem and the compiled one has nothing to refresh.
        builder.Services.AddSingleton(services => new PostgresRoleGrants(
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger<PostgresRoleGrants>(),
            services.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton<IRoleGrants>(services =>
            services.GetRequiredService<PostgresRoleGrants>());

        builder.Services.AddSingleton<IRoleDirectory>(services =>
            new PostgresRoleDirectory(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton<IGroupDirectory>(services =>
            new PostgresGroupDirectory(services.GetRequiredService<NpgsqlDataSource>()));

        // ADR-037's job record. A singleton over the data source, like every other store here.
        builder.Services.AddSingleton<Graticula.Platform.Jobs.IJobStore>(services =>
            new PostgresJobStore(services.GetRequiredService<NpgsqlDataSource>()));

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

        // <b>The raster reader and the coverage catalogue, ADR-043.</b> The factory is
        // the only place the host names a raster format, which is what makes the
        // adapter's project boundary a boundary rather than a suggestion.
        builder.Services.AddSingleton<ICoverageReaderFactory, TiffCoverageReaderFactory>();

        builder.Services.AddSingleton<ICoverageCatalog>(services =>
            new PostgresCoverageCatalog(services.GetRequiredService<NpgsqlDataSource>()));

        // <b>Behind the breaker, D-127.</b> A capabilities document needs one projection call
        // per distinct spatial reference and cannot be written without them; during an outage
        // each of those waited out a connect nothing answered, which is what a WFS document
        // costing 6.0 s and a WMS one costing 8.0 s were made of. See BreakingProjector.
        builder.Services.AddSingleton<IProjector>(services =>
            new BreakingProjector(
                new PostGisProjector(
                    services.GetRequiredKeyedService<NpgsqlDataSource>(DatastorePool)),
                services.GetRequiredService<SourceBreaker>()));

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

        /*
          <b>The request log is a singleton because it owns a queue and a background
          flusher, not because it is cheap to share.</b> A scoped one would start a thread
          per request, which is the opposite of the point.
          [ADR-045](../../docs/adr/ADR-045-the-server-keeps-a-log-you-can-ask-questions-of.md).

          <b>Registered as its concrete type as well as its port.</b> The Logs screen needs
          `Dropped` and `Waiting` to show how much the log is losing, and those are not on
          `IRequestLog` beyond the count — condition 6 asks for the drop to be visible, and
          the health of the writer is a different question from reading the log.
        */
        builder.Services.AddSingleton(services =>
            new PostgresRequestLog(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton<IRequestLog>(services =>
            services.GetRequiredService<PostgresRequestLog>());

        builder.Services.AddSingleton<IClientEventLog>(services =>
            new PostgresClientEventLog(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton<ILogReader>(services =>
            new PostgresLogReader(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton(services =>
            new PostgresRequestLogHealth(services.GetRequiredService<PostgresRequestLog>()));

        // ADR-045 condition 3: the cap is enforced, not promised. LogRetention states the
        // window and says how much it swept.
        builder.Services.AddHostedService<LogRetention>();

        builder.Services.AddSingleton<ISetupStore>(services =>
            new PostgresSetupStore(services.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddSingleton(services => new Authentication(
            services.GetRequiredService<IIdentityStore>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IRoleGrants>(),
            services.GetRequiredService<SourceBreaker>()));

        builder.Services.AddSingleton(services => new LoginService(
            services.GetRequiredService<IIdentityStore>(),
            services.GetRequiredService<IPasswordHasher>(),
            LoginThrottle.Default,
            settings.SessionLifetime,
            services.GetRequiredService<TimeProvider>()));

        // <b>The framework's own request logging is silenced, and this server writes
        // its own line instead.</b> `Microsoft.AspNetCore.Hosting.Diagnostics` logs
        // the full URL — query string included — before any middleware runs, so a
        // token sent as `?token=` was written down in full on every request. ADR-015
        // §4's first mitigation says redaction is *the code path*, not a
        // configuration, which is why the line is replaced rather than the level
        // lowered: a filter leaves the raw query one setting away from returning.
        builder.Logging.AddFilter(
            "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

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
        app.MapGet(LivenessPath, () => Results.Ok(new { status = "live" }));

        // <b>First, so that everything answers with them — including a 404 from
        // routing and a 500 from the exception handler, which are exactly the
        // responses a hardening pass forgets.</b> Added by the §66 security
        // gate; see SecurityHeaders.
        // <b>The request line this server writes, with credentials removed.</b>
        // ADR-015 §4.1 and its condition 2. Every request is logged once, after it
        // completes, with its status and the query string redacted by
        // `QueryRedaction` — which is a pure function with its own tests, so the
        // redaction can be asserted rather than trusted.
        ILogger requests = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("requests");

        IRequestLog requestLog = app.Services.GetRequiredService<IRequestLog>();

        app.Use(async (context, next) =>
        {
            long started = Stopwatch.GetTimestamp();

            /*
              <b>In a finally, because an aborted request left no line at all.</b> This was
              a plain `await next()` followed by the logging, so a request whose pipeline
              threw — which is what a client resetting the connection mid-write does —
              unwound straight past it. Measured: two connections reset during a 4000x4000
              GetMap produced *Executing endpoint* and *Executed endpoint* from the
              framework and **nothing from this middleware**.

              <b>That is the real shape of [D-132](../../docs/architecture-debt.md).</b> The
              row says the access log records `- 200` for a response nobody received, and
              that is true for the case where the write fits a socket buffer the client
              never drains. For a genuine mid-write abort there was no line to be wrong:
              the request that failed is the one the log does not mention, which is worse,
              because a reader counting requests never sees a gap.

              <b>Nothing is swallowed.</b> The exception continues to whatever handles it;
              this only guarantees the line.
            */
            try
            {
                await next().ConfigureAwait(false);
            }
            finally
            {
                Access(context, started, requests, requestLog, settings.RequestLog);
            }
        });

        // <b>Static, so it captures nothing.</b> A closure over `settings` here would
        // read fine and would be one more thing living for the life of the pipeline; the
        // one value it needs is passed instead, and the compiler enforces that.
        static void Access(
            HttpContext context,
            long started,
            ILogger requests,
            IRequestLog requestLog,
            bool storeRow)
        {
            /*
              <b>Redacted once and used twice.</b> The text line and the stored row are the
              same sentence written to two places, so they read the same query string —
              redacting separately would be two chances to get
              [D-120](../../docs/architecture-debt.md) wrong instead of one.

              <b>The redaction is not optional here, and the guard below is why that needs
              saying.</b> The text line is skipped when the logger is off; the stored row is
              not, because the Logs screen is the log now and a deployment that turns the
              console's logger down did not ask to stop recording. So the redaction happens
              before the guard rather than inside it.
            */
            string redacted = QueryRedaction.Redact(context.Request.QueryString.Value);

            // <b>Guarded and computed into a local</b>, which is ImportScratch's
            // pattern for CA1873 and the analyser is right in general: a deployment
            // that turns request logging off should not pay to build a string nothing
            // writes. Here the work is one pass over the query string.
            /*
              <b>What happened, not what the header promised, which is
              [D-132](../../docs/architecture-debt.md).</b> `Response.StatusCode` is fixed
              the moment the headers go out, so a client that hung up on the thousandth row
              and a projection that threw on it were both logged as the 200 the header had
              already announced. `grep -c ' - 499'` over a 6.6 MB log returned one, and that
              one was ArcGIS's own *Token Required* using the number for something else.
              `ResponseOutcome` answers the question somebody actually asks — *did they
              leave or did we break* — because those have opposite next steps.
            */
            int outcome = ResponseOutcome.StatusFor(context);

            if (requests.IsEnabled(LogLevel.Information))
            {
                Log.Request(
                    requests,
                    context.Request.Method,
                    context.Request.Path.Value,
                    redacted,
                    outcome);
            }

            /*
              <b>Queued, never awaited — `Record` returns void for exactly this reason.</b>
              ADR-045 condition 1: a request must not wait on the log. If the queue is full
              this entry is dropped and counted, and the Logs screen shows the count.

              <b>Read from the features after `next`, because that is when it is known.</b>
              The principal is resolved by a middleware further in, so asking before would
              record every request as anonymous — including every administrative one, which
              is the opposite of useful.
            */
            if (!storeRow)
            {
                return;
            }

            RequestPrincipal? who = context.Features.Get<RequestPrincipal>();

            requestLog.Record(new RequestEntry(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                redacted is { Length: > 0 } ? redacted : null,

                // <b>The same number the text line carries, and for the same reason.</b>
                // The Logs screen is what an operator reads now, so a row saying 200 for a
                // response nobody received would put D-132 back where it started with a
                // nicer interface on it.
                outcome,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                who is { } resolved && !resolved.Principal.IsAnonymous
                    ? resolved.Principal.Name
                    : null,
                context.Connection.RemoteIpAddress?.ToString(),
                RequestFacts.Face(context.Request.Path),
                RequestFacts.Service(context.Request.Path),
                context.Response.ContentLength));
        }

        app.UseSecurityHeaders(settings.RequireHttps);

        // <b>Before authentication, so that a request cannot outlive its deadline by
        // hanging in a place the deadline has not started yet.</b> Signing in reads the
        // platform store, and a store that has stopped answering would otherwise hold the
        // request open with nothing bounding it. Owner requirement 2026-08-18: every
        // service needs a timeout — see RequestDeadline for why it is two stages.
        app.UseRequestDeadline(settings.RequestDeadline);

        app.Use(async (context, next) =>
        {
            // <b>The liveness probe does not ask who is calling, because asking costs a
            // database round trip and the probe exists for the case where the database is
            // gone.</b> `ResolveAsync` looks up grants even for an anonymous caller —
            // deliberately, ADR-015 §2a, so that a public portal is a row rather than a
            // branch — and this middleware runs before every route. Measured with the
            // store stopped: 22 ms healthy, **4.03 s** from about eight seconds into the
            // outage, once the pool has drained its already-broken connectors.
            //
            // <b>That defeats the fix one screen up.</b> `/healthz/live` was made to
            // answer a constant precisely so an outage does not become a restart loop —
            // and Kubernetes' default probe `timeoutSeconds` is 1, so a 4-second probe
            // fails on every attempt and the kubelet restarts the container about thirty
            // seconds in. The restart does not reach the database and it empties
            // `CatalogFallback`, which is the degraded serving ADR-026 exists to provide.
            // **The outage would remove its own mitigation.** Found by the second failure
            // gate; the endpoint's own remark had predicted the shape and nobody looked at
            // what ran in front of it.
            //
            // The principal set here is the one an unreachable store produces anyway, so
            // the liveness path answers identically in both directions — it just does not
            // wait to find out.
            if (context.Request.Path == LivenessPath)
            {
                context.Features.Set(
                    new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing));

                await next(context).ConfigureAwait(false);
                return;
            }

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
                || context.Request.Path == LivenessPath)
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

        // <b>A dirty password reaches nothing but its own replacement.</b> Owner rule 2026-08-17:
        // the system issues a password, an administrator may pass it along, and its owner has to
        // change it on signing in. *Has to* is this middleware — not a screen that asks nicely.
        //
        // <b>Middleware rather than a check in each endpoint</b>, for the reason the geometry
        // service's sharing filter is one: eighty-odd routes each remembering a guard is eighty
        // chances to ship one that forgets, and the one that forgets is the interesting route.
        //
        // <b>What is allowed through, and each is required.</b> The password change, obviously.
        // `whoami`, because the console has to be able to say who you are and why it is showing
        // you one screen. Logout, because refusing to let somebody leave is not a security
        // control. The static console files, because a page that cannot load cannot offer the
        // form. And the anonymous surfaces — the services directory and health — because they are
        // not this caller's to be restricted from: a dirty password is a fact about an account,
        // not about the server.
        app.Use(async (context, next) =>
        {
            RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

            if (current is not { MustChangePassword: true } || Reachable(context.Request))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // <b>403 with the way out named.</b> Not 401: the credential was accepted and the
            // session is real, which is exactly why the caller can be told what to do about it.
            // A 401 would send a client back to sign in again and land it here again.
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 403,
                        message =
                            "This password was issued by the server and has to be replaced before "
                            + "the account can be used. Set your own with "
                            + "POST /rest/auth/password. Nothing else answers until then — the "
                            + "password you were given is known to whoever passed it to you.",
                    },
                },
                statusCode: StatusCodes.Status403Forbidden)
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
    /// <summary>
    /// Whether a caller holding a password they must replace may reach this request.
    /// </summary>
    /// <remarks>
    /// <b>An allow-list, and the direction matters.</b> A deny-list of *the interesting routes*
    /// would let every route added afterwards through by default, which is the wrong way round for
    /// a control whose whole job is that nothing else answers. Written as a method so the list is
    /// one thing a reviewer can read rather than a condition inside a lambda.
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <returns>Whether it is one of the few that answer.</returns>
    private static bool Reachable(HttpRequest request)
    {
        PathString path = request.Path;

        // The way out, and the two things a client needs around it.
        if (path == "/rest/auth/password" || path == "/rest/auth/logout" || path == "/rest/whoami")
        {
            return true;
        }

        // The console's own files: a page that cannot load cannot offer the form.
        if (path.StartsWithSegments("/server") || path.StartsWithSegments("/studio")
            || path.StartsWithSegments("/console"))
        {
            return true;
        }

        // <b>Anonymous surfaces, because they are not this caller's to be restricted from.</b> The
        // services directory answers strangers by design (ADR-023) and health answers an outage
        // (D-18). Refusing them here would make a dirty password *less* than no credential at all,
        // which is a strange shape and would break a browser that is signed in and reading a map.
        return path.StartsWithSegments("/rest/services")
            || path == LivenessPath
            || path == "/admin/health";
    }

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

        // ADR-015 §4 has clients discovering how to authenticate here.
        //
        // <b>It pointed at /rest/auth/login until 2026-08-19, and that was a
        // promise this server could not keep.</b> An ArcGIS client reads this
        // field and posts a form with `username`; /rest/auth/login speaks JSON and
        // wants `name`, so the client's sign-in failed with a credential error
        // about a credential that was correct. ArcGIS Pro found it in one attempt.
        // The endpoint it names now exists and speaks the client's vocabulary.
        app.MapGet("/rest/info", (HttpContext context) => Results.Ok(
            FeatureServerMetadataWriter.ServerInfo(
                $"{context.Request.Scheme}://{context.Request.Host}/rest/generateToken")));

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
            CatalogFallback catalog,
            PostgresSystemServices system,
            ICoverageCatalog coverages,
            CancellationToken cancellation) =>
            CatalogueAsync(context, catalog, system, coverages, null, cancellation))
            .Governed(SharingGovernedExtensions.ByFiltering);

        // Everything the datastore owns. The literal segment is more specific
        // than {layerName}, so routing prefers it — and it is matched
        // case-insensitively, which is why a client sending ArcGIS's own
        // capitalised "Hosted" reaches the same place.
        app.MapGet($"/rest/services/{FeatureServerMetadataWriter.HostedFolder}", (
            HttpContext context,
            CatalogFallback catalog,
            PostgresSystemServices system,
            ICoverageCatalog coverages,
            CancellationToken cancellation) =>
            CatalogueAsync(
                context, catalog, system, coverages,
                FeatureServerMetadataWriter.HostedFolder, cancellation))
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>Any folder's own directory.</b> Added 2026-08-17 with named folders: the two
        // literal routes above covered the two folders that could exist, so browsing
        // /rest/services/turkiye answered nothing at all while the root advertised it — a
        // folder a client could see and not open. The literals stay because a literal segment
        // beats a parameter in routing and both carry their own comment.
        app.MapGet("/rest/services/{folder}", (
            HttpContext context,
            CatalogFallback catalog,
            PostgresSystemServices system,
            string folder,
            ICoverageCatalog coverages,
            CancellationToken cancellation) =>
            CatalogueAsync(context, catalog, system, coverages, folder, cancellation))
            .Governed(SharingGovernedExtensions.ByFiltering);

        // Where the system services live. ArcGIS puts the geometry service in a
        // Utilities folder and every client that looks for one looks there.
        app.MapGet("/rest/services/Utilities", (
            HttpContext context,
            CatalogFallback catalog,
            PostgresSystemServices system,
            ICoverageCatalog coverages,
            CancellationToken cancellation) =>
            CatalogueAsync(context, catalog, system, coverages, "Utilities", cancellation))
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

        // <b>Outside /rest/services, deliberately.</b> Every surface above is
        // ArcGIS-shaped and lives under that prefix; WFS is a different protocol
        // with a different discovery document, and a client pastes one address for
        // the whole server rather than one per service (ADR-039 §5).
        WfsEndpoints.Map(app);
        WmsEndpoints.Map(app);
        MapServerEndpoints.Map(app);
        ImageServerEndpoints.Map(app);
        LogEndpoints.Map(app);
        CoverageAdminEndpoints.Map(app);
        OgcFeaturesEndpoints.Map(app);

        // <b>The portal surface, and it is here for one reason.</b> ArcGIS Pro's
        // server connection wants a SOAP catalogue this product has never scoped;
        // its portal connection wants the ArcGIS REST API, which is what everything
        // else here already is. ADR-040.
        app.MapPortal();

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

                // <b>Reported here because this is one of the three calls a caller holding an
                // issued password may make.</b> Without it a console would sign somebody in, paint
                // its screens, and watch every one of them answer 403 — which is what a client sees
                // when a server enforces a rule it does not advertise.
                mustChangePassword = current.MustChangePassword,

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

    /// <summary>Every folder the directory should advertise.</summary>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="system">The system services.</param>
    /// <param name="services">What the listing already gave.</param>
    /// <param name="blind">
    /// True when the listing came from memory. <b>Then nothing else is asked, and that is the
    /// repair rather than an optimisation</b>: each further read costs one blackholed connect —
    /// about four seconds — and answers what this method already catches and ignores.
    /// [D-127](../../docs/architecture-debt.md).
    /// </param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The folder names.</returns>
    private static async Task<string[]> FoldersAsync(
        PostgresLayerCatalog catalog,
        PostgresSystemServices system,
        IReadOnlyList<PublishedService> services,
        bool blind,
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

        if (blind)
        {
            // The two reads below would each wait out a connect that is not going to be
            // answered, and the catch under the second one already says what the answer is:
            // the folders named by the services in hand are still the truth about where those
            // services are. Waiting eight seconds to be told that is the cost this removes.
            return [.. names];
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
        CatalogFallback catalog,
        PostgresSystemServices system,
        ICoverageCatalog coverages,
        string? folder,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        /*
          <b>Through the fallback since 2026-08-23, and this directory is where
          [D-127](../../docs/architecture-debt.md)'s first axis was measured.</b> ADR-026's
          degraded serving could resolve one named service and could not list, so every face
          that enumerates went down with the store — and this one went down slowly: the reads
          below are five separate connects, each waiting out about four seconds of a socket
          nothing answers. Concurrently, 45 seconds into an outage, it served 0 of 20 requests
          instantly while the authentication path served 19 of 20 in 13 ms.

          <b>So when the answer is remembered, nothing else is asked.</b> Each read skipped
          below has an answer this method can already give from what it holds, and every one of
          them costs a connect that will not be answered.
        */
        CatalogListing listing = await catalog.ListServicesAsync(cancellation)
            .ConfigureAwait(false);

        if (listing.Services is not { } services)
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = 503,
                        message =
                            "The catalogue is not reachable and this server has no remembered "
                            + "listing to answer from, so it cannot say what it publishes. "
                            + "Retry shortly; see /healthz/ready.",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        bool blind = listing.Blind;

        if (blind)
        {
            ServiceLookup.SayAge(context, listing.Age);
        }

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
                        service.Sharing, service.Owner, current.Principal, current.Authorization,
                        service.SharedWith)
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
            ? await FoldersAsync(catalog.Catalog!, system, services, blind, cancellation)
                .ConfigureAwait(false)
            : [];

        // <b>System services are services.</b> Owner correction 2026-08-15: the
        // geometry service belongs in the directory beside the layers, governed
        // by the same sharing, or an administrator browsing the server cannot
        // see half of what it offers.
        // <b>Empty while blind, and it is the same argument the folder register makes.</b> The
        // system services live in their own table with no memory in front of it, so asking for
        // them during an outage buys a four-second wait and an exception. A directory missing
        // Utilities is smaller than the truth; one that takes four seconds to say so is worse
        // for every client that browses it. [D-127](../../docs/architecture-debt.md).
        List<(string Name, string Type)> systemServices = blind
        ?
        []
        :
        [
            .. (await system.ListAsync(cancellation).ConfigureAwait(false))
                .Where(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase)
                    && LayerAccess
                        // <b>No group shares for a system service, and that is a scope statement
                        // rather than an omission.</b> `sharing_group_item` references `service`;
                        // the Utilities services live in `system_service` and are part of the
                        // server rather than somebody's content, so *share this with the planning
                        // team* is not a thing to say about the geometry service.
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
        // <b>Not asked while blind, and the answer would have been the same.</b>
        // `FolderExistsAsync` already answers *yes* when it cannot read — a 404 about a folder
        // this server cannot look up would be a claim it is in no position to make — so the
        // read buys four seconds and no information.
        if (folder is not null && visible.Count == 0 && systemServices.Count == 0 && !blind
            && !await FolderExistsAsync(catalog.Catalog!, folder, cancellation)
                .ConfigureAwait(false))
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

        // <b>Every service with a drawable layer also has a MapServer</b>, added
        // 2026-08-20 with ADR-041. A face a client cannot find in the catalogue is a
        // face only somebody who already knew the URL can use, which is the same
        // argument the WFS link on a service page made three commits earlier.
        List<PublishedService> drawable =
        [
            .. visible.Where(s =>
                s.Layers.Any(l => l.Definition.GeometryColumn is { Length: > 0 })),
        ];

        /*
          <b>Image services, ADR-043, and they are the first entry here that is not a
          second face on a layer.</b> The three above are the same published data under
          three types; a coverage is its own thing with no layer behind it, so it is
          read from its own catalogue and filtered by the same sharing rule rather than
          inheriting one.

          <b>Stopped ones follow the rule the others follow</b> — visible only to
          somebody who may manage the server — so a service an operator switched off
          does not vanish from their own directory.
        */
        // Empty while blind, for the reason the system services are: its catalogue has no
        // memory in front of it, and the wait is the whole of what asking would buy.
        List<PublishedCoverage> imagery = blind
        ?
        []
        :
        [
            .. (await coverages.ListAsync(cancellation).ConfigureAwait(false))
                .Where(c => string.Equals(
                    c.Folder ?? string.Empty, folder ?? string.Empty, StringComparison.Ordinal))
                .Where(c => seesStopped || c.Status == ServiceStatus.Started)
                .Where(c => LayerAccess.Evaluate(
                    c.Sharing, c.Owner, current.Principal, current.Authorization).IsAllowed()),
        ];

        List<(string Name, string Type)> everything =
        [
            .. visible.Select(s => (s.QualifiedName, Type: "FeatureServer")),
            .. drawable.Select(s => (s.QualifiedName, Type: "MapServer")),
            .. tileable.Select(s => (s.QualifiedName, Type: "VectorTileServer")),
            .. imagery.Select(c => (c.QualifiedName, Type: "ImageServer")),
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
            systemServices,
            drawable.Select(s => s.Name),
            imagery.Select(c => c.Name)));
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
        // <b>Unwrapped, and the slot is held for the whole of it.</b> `LayerConnections` hands out a
        // `BudgetedFeatureSource` (ADR-007 §4.8's connection cap) and the shape queries below are the
        // provider's own methods rather than `IFeatureSource`'s, so the concrete type is needed here.
        // Taking the lease first is what keeps a count inside the bound — a filtered `count(*)` is one
        // of the more expensive statements this server issues, and it is the first thing an ArcGIS
        // client asks for.
        BudgetedFeatureSource? budgeted = source as BudgetedFeatureSource;

        using ConnectionBudget.Lease lease = budgeted is not null
            ? await budgeted.LeaseAsync(cancellation).ConfigureAwait(false)
            : default;

        if (budgeted is not null)
        {
            source = budgeted.Inner;
        }

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

        /*
          <b>Reported to the breaker, because this path is invisible to the decorator it
          just unwrapped.</b> D-131: the shape queries are the provider's own methods, so a
          failure here told the breaker nothing and a `returnCountOnly` during an outage
          kept paying the full four seconds — which is the request an ArcGIS client makes
          first, so it is the one that matters most.

          <b>Nothing is swallowed.</b> `Observe` returns false for a database that
          answered, and a false filter leaves the exception to the middleware unchanged.
        */
        try
        {
            await ShapedAsync(context, layer, described, postgis, query, shape, html, cancellation)
                .ConfigureAwait(false);

            budgeted?.Observe(null);
            return;
        }
        catch (Exception failure) when (budgeted?.Observe(failure) ?? false)
        {
            throw;
        }
    }

    private static async Task ShapedAsync(
        HttpContext context,
        PublishedLayer layer,
        LayerDescription described,
        PostGisFeatureSource postgis,
        FeatureQuery query,
        QueryShape shape,
        bool html,
        CancellationToken cancellation)
    {
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
        HostSettings settings,
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
            service.Limits.Cost.MaximumRecordCount,

            // <b>The deployment's own ceiling, since 2026-08-19.</b> It was a compile-time constant, so
            // an operator who wanted *nothing on this server returns more than two thousand* had to set
            // it service by service and every new service started at 50,000 again. Advertised here as
            // well as enforced in the parser, because a client sizes its paging from this number.
            settings.MaximumRecordCount);

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
                    tree: LayerTree(service, context.Request.Path),

                    // <b>The format line, which is where an ArcGIS Server
                    // directory prints JSON | SOAP | WMS | WFS.</b> This server
                    // has spoken WFS since 2026-08-19 and no service page said
                    // so, which made the surface discoverable only to somebody
                    // who already knew it existed.
                    formats:
                    [
                        WfsEndpoints.DirectoryLink(null),
                        WmsEndpoints.DirectoryLink(null, null, 0),
                        OgcFeaturesEndpoints.DirectoryLink(null),
                    ]),
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
        HostSettings settings,
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
            layer.Cost.MaximumRecordCount,
            settings.MaximumRecordCount,

            // ADR-033 §5a: the stored canonical document, or null for the generated
            // appearance. The writer decides which; this only carries it.
            layer.Symbology);

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
                    ],

                    // The layer's own WFS schema, on the format line beside JSON.
                    // The WFS type name is the layer's name, which is why this is
                    // built here and not from the path: the ArcGIS layer id and
                    // the WFS type name are different identifiers for one layer.
                    // <b>The WMS link draws the layer rather than fetching its
                    // capabilities.</b> A capabilities document on a layer page says
                    // nothing about that layer, and the one thing somebody clicking
                    // here wants to know is whether it draws — which is also the
                    // fastest way to see that its stored symbology is wrong.
                    formats:
                    [
                        WfsEndpoints.DirectoryLink(layer.Definition.Name),
                        WmsEndpoints.DirectoryLink(
                            layer.Definition.Name, description.Extent, layer.Definition.Srid),
                        OgcFeaturesEndpoints.DirectoryLink(layer.Definition.Name),
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

    /// <summary>
    /// The one route that must answer while every dependency is gone.
    /// </summary>
    /// <remarks>
    /// <b>A name rather than four string literals, because three of the four were a
    /// guard and the fourth was the route itself.</b> Setup, the dirty-password gate and
    /// the anonymous-surface list each exempt this path, and every exemption is only as
    /// good as somebody spelling it the same way. The middleware exemption added
    /// 2026-08-20 is the fourth, and it is the one whose absence made the other three
    /// pointless during an outage.
    /// </remarks>
    private const string LivenessPath = "/healthz/live";

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

        // <b>A layer's scope comes from its service and so do its groups.</b> `layer.Sharing` is
        // what the catalogue read off the owning service row (migration 11), and `SharedWith` is the
        // same service's group shares — the layer table carries neither.
        LayerAccess.Reason reason = LayerAccess.Evaluate(
            layer.Sharing, layer.Owner, current.Principal, current.Authorization, layer.SharedWith);

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

        /*
          <b>The merged view, because this route has always accepted POST and always ignored
          what was posted.</b> [D-139](../../docs/architecture-debt.md) was written as *POST is
          refused*, which is true of `exportImage` and `identify` and is not true here: `query`
          is mapped for both methods, and read `context.Request.Query` either way.

          <b>So a posted query answered a different question in silence.</b> Measured
          2026-08-23: `POST .../query` with `where=1=1&returnCountOnly=true&f=json` in the body
          returned the full attribute set rather than the count — every parameter absent, so
          every default applied. A 405 would have been better: it says it did not work.

          `query` is also the operation that needs a body most. A `where` clause, an input
          geometry and an `outFields` list are exactly what does not fit in a URL, which is
          why the POST route was mapped in the first place.
        */
        if (!FeatureServerQueryParameters.TryParse(
                await ArcGisParameters.ReadAsync(context, cancellation).ConfigureAwait(false),
                layer.Definition.ObjectIdColumn!,
                layer.Definition.Srid,
                described.Fields,
                out FeatureQuery? query,
                out QueryShape shape,
                out string? error,
                cost,
                settings.DefaultRecordCount,
                settings.MaximumRecordCount))
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
            ErrorResponse.LogTruncated(context, loggerFactory.CreateLogger("query"), e);
            context.Abort();
        }
    }
}
