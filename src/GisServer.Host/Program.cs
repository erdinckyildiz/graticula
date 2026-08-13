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
using GisServer.Platform.Postgres;
using GisServer.Platform.Schema;
using GisServer.Platform.Secrets;
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

        // ADR-015 §1 warns that authentication cannot be threaded through
        // later without rewriting what came first. It is not implemented, so
        // this says so at every startup rather than letting the omission be
        // discovered by whoever exposes the server.
        Log.AuthenticationNotImplemented(logger);

        // Before the endpoints, so it wraps them. ADR-017 §6: an unhandled
        // exception must still produce an answer that says what to do.
        app.UseExceptionHandler(handler => handler.Run(context =>
            ErrorResponse.WriteAsync(
                context,
                context.Features.Get<IExceptionHandlerFeature>()?.Error
                    ?? new InvalidOperationException("An error was reported with no exception."),
                logger)));

        MapEndpoints(app);

        Log.Listening(
            logger,
            settings.RequireHttps ? "https" : "http",
            settings.ListenAddress.ToString(),
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
                    : ServerIdentity.GenerateSelfSigned(settings.HostName);

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
        app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));

        app.MapGet("/rest/services", async (
            PostgresLayerCatalog catalog, CancellationToken cancellation) =>
        {
            IReadOnlyList<PublishedLayer> layers = await catalog.ListAsync(cancellation);

            return Results.Ok(new
            {
                services = layers.Select(layer => new
                {
                    name = layer.Definition.Name,
                    type = "FeatureServer",

                    // ADR-013 §2a made physical. A layer without an integer
                    // identity is servable natively and not through ArcGIS, and
                    // saying so here is the never-degrade-silently principle
                    // applied to a data-shape limitation.
                    arcGisServable = layer.Definition.IsArcGisServable,
                }),
            });
        });

        app.MapGet("/rest/services/{layerName}/FeatureServer/0/query", QueryAsync);
    }

    private static async Task QueryAsync(
        HttpContext context,
        string layerName,
        PostgresLayerCatalog catalog,
        LayerConnections connections,
        ILoggerFactory loggerFactory,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await catalog.FindAsync(layerName, cancellation).ConfigureAwait(false);

        if (layer is null)
        {
            await Results.NotFound(new { error = new { code = 404, message = $"No layer '{layerName}'." } })
                .ExecuteAsync(context).ConfigureAwait(false);
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
