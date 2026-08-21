using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Coverages;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>What a caller sends to register imagery.</summary>
/// <param name="Name">What to call the service.</param>
/// <param name="Folder">Where to put it, or null for the root.</param>
/// <param name="Path">Where the file already lives, and stays.</param>
public sealed record RegisterCoverageRequest(string? Name, string? Folder, string? Path);

/// <summary>
/// Registering imagery, which is the only write this face has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration opens the file once and never again</b>
/// ([ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.3). What
/// it reads — size, bands, sample kind, no-data, extent, reference, the pyramid — is
/// stored, so the service document is answerable without touching a disk that may be
/// an object store on the other side of a network.
/// </para>
/// <para>
/// <b>Refusing at registration is the whole value of opening it then.</b> A file that
/// is not a GeoTIFF, or has no georeference, or is rotated, is refused to the person
/// publishing it — who can do something about it — rather than to a client six months
/// later, who cannot.
/// </para>
/// </remarks>
internal static class CoverageAdminEndpoints
{
    /// <summary>Registers the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/admin/coverages", RegisterAsync);
        app.MapGet("/admin/coverages", ListAsync);
    }

    private static async Task ListAsync(
        HttpContext context, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer)
            .ConfigureAwait(false))
        {
            return;
        }

        var listing = await coverages.ListAsync(cancellation).ConfigureAwait(false);

        // <b>The path is not in this listing either.</b> An administrator can read it
        // out of the store; a route that returns it makes it one misconfigured
        // privilege away from a client, and ADR-043 §3.3's proxy exists so the location
        // never has to leave here.
        await Results.Ok(new
        {
            coverages = Array.ConvertAll([.. listing], c => new
            {
                name = c.QualifiedName,
                srid = c.Info.Srid,
                width = c.Info.Width,
                height = c.Info.Height,
                bands = c.Info.Bands.Count,
                overviews = c.Info.Overviews.Count,
                sharing = c.Sharing.ToString().ToLowerInvariant(),
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RegisterAsync(
        HttpContext context,
        RegisterCoverageRequest request,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures)
            .ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Path))
        {
            await Refuse(context, 400,
                "A coverage registration needs a `name` for the service and a `path` to the "
                + "file. The file is not copied — ADR-043 §3.3 registers imagery where it "
                + "already lives — so the path has to be one this server can still read later.")
                .ConfigureAwait(false);

            return;
        }

        if (!File.Exists(request.Path))
        {
            await Refuse(context, 400,
                $"This server cannot see '{request.Path}'. Imagery is registered in place, so "
                + "the path is read at every request rather than once at upload: a path that is "
                + "not readable now will not be readable later either.")
                .ConfigureAwait(false);

            return;
        }

        CoverageInfo info;

        try
        {
            using ICoverageReader reader =
                await readers.OpenAsync(request.Path, cancellation).ConfigureAwait(false);

            info = reader.Info;
        }
        catch (InvalidDataException refused)
        {
            // <b>The reader's own sentence, not a summary of it.</b> It says which of
            // several specific things is wrong — no georeference, a rotation this
            // server will not place, not a TIFF at all — and rewording it here would
            // lose the part that tells the publisher what to fix.
            await Refuse(context, 400, refused.Message).ConfigureAwait(false);
            return;
        }

        if (info.Srid == 0)
        {
            await Refuse(context, 400,
                "This file carries no EPSG code in its GeoKey directory, so this server cannot "
                + "say what its coordinates mean. A coverage with an unknown reference cannot "
                + "be drawn beside anything else.")
                .ConfigureAwait(false);

            return;
        }

        RequestPrincipal principal = context.Features.Get<RequestPrincipal>()
            ?? new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing);

        PublishedCoverage published = await coverages.RegisterAsync(
            string.IsNullOrWhiteSpace(request.Folder) ? null : request.Folder,
            request.Name,
            request.Path,
            info,
            principal.Principal.IsAnonymous ? null : principal.Principal.Id,
            cancellation).ConfigureAwait(false);

        await audit.RecordAsync(
            new AuditEvent(
                principal.Principal.Id,
                principal.Principal.Name,
                context.Connection.RemoteIpAddress?.ToString(),
                "coverage.register",
                published.QualifiedName,
                // <b>JSON, because the column is.</b> A plain sentence here is a
                // `22P02` from PostgreSQL at the moment of the write, which the error
                // classifier then reports as an unreachable database — so a registration
                // that failed for a formatting reason reads as an outage. Caught on the
                // first end-to-end registration.
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    width = info.Width,
                    height = info.Height,
                    bands = info.Bands.Count,
                    srid = info.Srid,
                    overviews = info.Overviews.Count,
                    inPlace = true,
                }),
                true),
            cancellation).ConfigureAwait(false);

        await Results.Created(
            $"/rest/services/{published.QualifiedName}/ImageServer",
            new
            {
                name = published.QualifiedName,
                srid = info.Srid,
                width = info.Width,
                height = info.Height,
                bands = info.Bands.Count,
                overviews = info.Overviews.Count,

                // <b>Private, and said out loud.</b> Every service this server creates
                // starts private; saying so in the response is what stops a publisher
                // assuming otherwise and discovering it from a colleague.
                sharing = "private",
            }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static Task Refuse(HttpContext context, int code, string message) =>
        Results.Json(
            new { error = new { code, message } }, statusCode: code).ExecuteAsync(context);
}
