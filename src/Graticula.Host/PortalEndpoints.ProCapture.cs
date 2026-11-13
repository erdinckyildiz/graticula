using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// <b>What ArcGIS Pro sends when it publishes, recorded before anything is built to answer it</b> (owner, 2026-10-17:
/// <i>"arcgis pro üzerinden hosted feature layer publish etmeyi planlıyorum … copy as feature"</i>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two switches now, and only the second is a measuring tool</b> — corrected 2026-10-22, the ArcGIS veteran's E8:
/// this said *"off unless ProPublishCapture is true"* after publishing had become the default.
/// <c>Graticula:PublishFromPro</c> (on by default, <c>PublishingFromPro</c>) names this server as the portal's hosting
/// server, advertises the publishing and sharing privileges, and answers Pro's publishing calls — <c>addItem</c>,
/// <c>addPart</c>, <c>commit</c>, <c>status</c>, <c>publish</c>, <c>isServiceNameAvailable</c> — for real.
/// <c>Graticula:ProPublishCapture</c> (off by default, <c>RecordingPro</c>, set only on dev.graticula.com) also writes
/// every call's fields (never its token) and every uploaded file under <c>Graticula:ProPublishCaptureDirectory</c>,
/// which is how the sequence below was first measured.
/// </para>
/// <para>
/// <b>Why measure first.</b> The sequence Pro uses to publish a hosted feature layer to an Enterprise portal, and
/// what is inside the service definition it uploads, are known here from memory and documentation, not from this
/// server's request log. Building the import on a guess is how the portal surface found three wrong turns before
/// (Q-126). This file is the experiment; the decision and the real implementation follow from what it records.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The resources an ArcGIS Server admin root lists.</summary>
    /// <summary>The logs' resources this server answers (ADR-261).</summary>
    private static readonly string[] LogResources = ["query"];

    private static readonly string[] AdminResources = ["machines", "clusters", "system", "services", "security", "data", "uploads", "logs", "mode"];

    /// <summary>A recorded call is read by a person, so it is indented.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Whether a field or query key carries a credential, which the capture writes as <c>*</c> or not at all. Only
    /// <c>token</c> was left out until 2026-10-17, and a sign-in's password reached the wire log in clear.
    /// </summary>
    internal static bool CapturedSecret(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("pass", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Equals("key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("api_key", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this server serves Pro's publishing (ADR-191) — <see cref="PublishingFromPro"/>, per request.</summary>
    private static bool Publishing(HttpContext context) =>
        PublishingFromPro(context.RequestServices.GetRequiredService<IConfiguration>());

    /// <summary>The publishing calls Pro makes, mapped only when capture is on.</summary>
    private static void MapProPublishCapture(WebApplication app)
    {
        if (!PublishingFromPro(app.Configuration))
        {
            return;
        }

        // The hosting server's admin API as Pro reads it (/arcgis/admin/... — Program.cs's rewrite).
        app.MapMethods("/pro-admin/{**rest}", ["GET", "POST"], HostingAdminAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();

        // ADR-299: the services root's SOAP door, which ArcGIS Pro's federated-server connection knocks on before it shares
        // a map image layer (/arcgis/services, rewritten to /services). PortalEndpoints.ProSoap.cs.
        app.MapPost("/services", ServicesSoapAsync).DisableAntiforgery();
        app.MapPost("/services/System/PublishingTools/GPServer", ServicesSoapAsync).DisableAntiforgery();

        // Pro's last act after a publish sets the service item's typeKeywords, which this server derives; the item update
        // that answers it, and every other client's, is PortalEndpoints.ItemWrites — mapped whether or not Pro publishes.

        if (!RecordingPro(app.Configuration))
        {
            return;
        }

        // Recorded only: Pro's delete is its rollback of a publish it read as failed, and must not reach a service.
        app.MapPost($"{Path}/content/users/{{username}}/items/{{id}}/delete", CaptureAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
    }

    /// <summary>
    /// The hosting server's admin documents Pro reads before it shares: the server, its directories and its folders.
    /// Recorded like the portal calls, and answered with this server's own facts in the admin API's shape.
    /// </summary>
    private static async Task<IResult> HostingAdminAsync(
        HttpContext context, CatalogFallback catalog, PostgresSystemServices system, ICoverageCatalog coverages, string? rest,
        CancellationToken cancellation)
    {
        RequestPrincipal caller = context.Features.Get<RequestPrincipal>()!;
        if (!caller.Authorization.Allows(Privilege.ContentPublishFeatures))
        {
            return PortalError(403, "The hosting server's administration is read by those who may publish.");
        }

        if (RecordingPro(context.RequestServices.GetRequiredService<IConfiguration>()))
        {
            await CaptureAsync(context, cancellation).ConfigureAwait(false);
        }

        string origin = $"{context.Request.Scheme}://{context.Request.Host}";
        string what = (rest ?? string.Empty).Trim('/');

        // <b>A measuring tool, dev only</b> (ADR-299): while Pro's calls are recorded, an answer written to
        // <c>{capture}/admin-answers/{path with / as _}.json</c> is returned as it stands, so a variation can be tried on Pro
        // without rebuilding the server. Nothing else reads that directory.
        if (RecordingPro(context.RequestServices.GetRequiredService<IConfiguration>())
            && System.IO.Path.Combine(
                    context.RequestServices.GetRequiredService<IConfiguration>()["Graticula:ProPublishCaptureDirectory"]
                    ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pro-capture"),
                    "admin-answers", (what.Length == 0 ? "root" : what.Replace('/', '_')) + ".json") is { } canned
            && File.Exists(canned))
        {
            return Results.Text(await File.ReadAllTextAsync(canned, cancellation).ConfigureAwait(false), "application/json");
        }

        if (what.Length == 0)
        {
            return Results.Ok(new
            {
                // ADR-299: what a federated 12.0 server's root says first, and what Pro's analyzer 00147 reads.
                isServerLicensed = "true",
                resources = AdminResources,

                // The release the REST documents and the portal's enterpriseVersion say (ADR-181 §5). It said 11.2 from
                // 2026-10-17, written before the owner made server and portal one 12.1 pair two days later.
                currentVersion = Graticula.Api.ArcGis.FeatureServerMetadataWriter.CurrentVersion,
                fullVersion = Graticula.Api.ArcGis.FeatureServerMetadataWriter.FullVersion,
            });
        }

        if (what == "info")
        {
            return Results.Ok(new
            {
                currentversion = Graticula.Api.ArcGis.FeatureServerMetadataWriter.CurrentVersion,
                fullVersion = Graticula.Api.ArcGis.FeatureServerMetadataWriter.FullVersion,
                timezone = new { windowsZoneID = "UTC", olsonZoneID = "Etc/UTC" },
                loggedInUser = context.Features.Get<RequestPrincipal>()?.Principal.Name,
                owningSystemUrl = origin,
            });
        }

        if (what == "system/directories")
        {
            object Directory(string name, string type) => new
            {
                name,
                physicalPath = $"/state/{name}",
                directoryType = type,
                cleanupMode = "NONE",
                maxFileAge = 0,
                description = $"This server's {name}",
            };

            return Results.Ok(new
            {
                directories = new[]
                {
                    Directory("arcgiscache", "CACHE"), Directory("arcgisjobs", "JOBS"),
                    Directory("arcgisoutput", "OUTPUT"), Directory("arcgissystem", "SYSTEM"),
                },
            });
        }

        // The root's services and folders, and one folder's services. A name with a dot is a service —
        // `services/{folder}/{name}.{type}` — and its document, status, start and stop are ADR-261's doors.
        if (what == "services" || IsAdminServicesFolderPath(what))
        {
            return await AdminServicesAsync(
                    context, catalog, system, coverages, what == "services" ? null : what["services/".Length..], cancellation)
                .ConfigureAwait(false);
        }

        if (what.StartsWith("services/", StringComparison.Ordinal) && what.Contains('.', StringComparison.Ordinal))
        {
            return await AdminServiceDoorAsync(context, catalog, system, coverages, what["services/".Length..], cancellation)
                .ConfigureAwait(false);
        }

        // ADR-261: the logs' one door an administrator's script uses.
        if (what == "logs")
        {
            return Results.Ok(new { resources = LogResources });
        }

        if (what == "logs/query")
        {
            return await AdminLogsQueryAsync(context, cancellation).ConfigureAwait(false);
        }

        // ADR-299: the documents Pro reads before it shares a map image layer, the registered databases among them.
        if (await ProReferenceAdminAsync(context, what, cancellation).ConfigureAwait(false) is { } answered)
        {
            return answered;
        }

        return Results.Json(new { status = "error", messages = new[] { $"'{what}' is not answered by this server." }, code = 404 });
    }

    /// <summary>What the admin listing says when the catalogue cannot be read and nothing is remembered.</summary>
    private static readonly string[] CatalogueUnreachable =
        ["The catalogue is not reachable and this server has no remembered listing to answer from. Retry shortly; see /healthz/ready."];

    /// <summary>One service as the hosting server's admin API lists it.</summary>
    /// <param name="Folder">Its folder, or null for the root.</param>
    /// <param name="Name">Its name within the folder.</param>
    /// <param name="Type">The face: <c>FeatureServer</c>, <c>MapServer</c>, <c>VectorTileServer</c>, <c>ImageServer</c> or a system service's type.</param>
    /// <param name="Description">Its description, or null.</param>
    internal sealed record AdminListedService(string? Folder, string Name, string Type, string? Description);

    /// <summary>
    /// The names under <c>admin/services/</c> that are the server's own resources, never a folder — ADR-299.
    /// </summary>
    /// <remarks>
    /// Until 2026-11-13 <c>services/canCreateService</c> and <c>services/properties</c> were read as folder names and
    /// answered <c>Folder '…' does not exist.</c> with status 200, so the answers written for them were never reached. Pro
    /// reads that error body from <c>canCreateService</c> as a refusal and reports it as analyzer 00147, <i>"The ArcGIS
    /// GIS server role is not licensed"</i>, which nothing in the message points to — measured by serving
    /// <c>{"status":"success"}</c> there alone, with every other answer at baseline: the draft stages.
    /// </remarks>
    internal static readonly System.Collections.Frozen.FrozenSet<string> AdminServicesResources =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(["canCreateService", "properties", "types"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when an admin path (trimmed of slashes) names one folder of services: <c>services/{folder}</c>, with no dot
    /// and not one of <see cref="AdminServicesResources"/>.
    /// </summary>
    internal static bool IsAdminServicesFolderPath(string what) =>
        what.StartsWith("services/", StringComparison.Ordinal)
        && what.Count(c => c == '/') == 1
        && !what.Contains('.', StringComparison.Ordinal)
        && !AdminServicesResources.Contains(what["services/".Length..]);

    /// <summary>
    /// The admin API's <c>services</c> resource, for the root (<paramref name="folder"/> null) or one folder: the services
    /// this caller may see there, in the order given, and — at the root — the folders.
    /// </summary>
    internal static object AdminServicesFolder(string? folder, IReadOnlyList<string>? folders, IEnumerable<AdminListedService> services)
    {
        object[] listed =
        [
            .. services.Select(s => new
            {
                folderName = s.Folder ?? "/",
                serviceName = s.Name,
                type = s.Type,
                description = s.Description ?? string.Empty,
                isDefault = false,
            }),
        ];

        return folder is null
            ? (object)new
            {
                folderName = "/",
                description = "Root folder",
                webEncrypted = false,
                isDefault = true,
                folders = folders ?? [],
                foldersDetail = (folders ?? []).Select(name => new { folderName = name, description = (string?)null, webEncrypted = false, isDefault = false }),
                services = listed,
            }
            : new
            {
                folderName = folder,
                description = (string?)null,
                webEncrypted = false,
                isDefault = false,
                folders = Array.Empty<string>(),
                foldersDetail = Array.Empty<object>(),
                services = listed,
            };
    }

    /// <summary>
    /// The hosting server's services, as its admin API lists them: what the services directory lists this caller, by
    /// the same catalogue and the same rule (<see cref="Program.DirectoryLists(PublishedService, string?, RequestPrincipal, bool)"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ArcGIS veteran's review of 2026-11-02, D: the admin listing said this server had no services.</b>
    /// <c>/arcgis/admin/services</c> answered its folders and <c>services: []</c>, and a folder answered an error, so the
    /// ArcGIS API for Python's <c>server.services.list()</c> and every admin script that walks a server found nothing to
    /// walk, and Pro's view of its hosting server was empty. The listing is now the directory's, in the admin API's shape
    /// — <c>folderName</c>, <c>serviceName</c>, <c>type</c>, <c>description</c> — one entry per face, as the directory
    /// lists one entry per face, and for whoever may read the hosting server's administration at all.
    /// </para>
    /// <para>
    /// <b>A listing, and nothing more.</b> A service's own admin document and its operations — <c>start</c>,
    /// <c>stop</c>, <c>edit</c>, <c>delete</c> — are not answered through this address and still refuse; a listing that
    /// implied otherwise would be a set of links that fail. A folder this server does not have refuses as one, by the
    /// rule the directory's 404 keeps, and the folders at the root are the directory's folders.
    /// </para>
    /// </remarks>
    private static async Task<IResult> AdminServicesAsync(
        HttpContext context, CatalogFallback catalog, PostgresSystemServices system, ICoverageCatalog coverages, string? folder,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        CatalogListing listing = await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        if (listing.Services is not { } services)
        {
            return Results.Json(new
            {
                status = "error",
                messages = CatalogueUnreachable,
                code = 503,
            });
        }

        bool blind = listing.Blind;
        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);
        List<AdminListedService> listed = [];

        foreach (PublishedService service in services
            .Where(s => Program.DirectoryLists(s, folder, current, seesStopped))
            .OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            if (ServiceFaces.AnswersFeatures(service))
            {
                listed.Add(new AdminListedService(service.Folder, service.Name, "FeatureServer", service.Description));
            }

            if (ServiceFaces.AnswersMap(service))
            {
                listed.Add(new AdminListedService(service.Folder, service.Name, "MapServer", service.Description));
            }

            if (ServiceFaces.AnswersTiles(service))
            {
                listed.Add(new AdminListedService(service.Folder, service.Name, "VectorTileServer", service.Description));
            }
        }

        // Not read while blind, as the directory does not read them: neither catalogue has a memory in front of it.
        if (!blind)
        {
            foreach (PublishedCoverage coverage in (await coverages.ListAsync(cancellation).ConfigureAwait(false))
                .Where(c => Program.DirectoryLists(c, folder, current, seesStopped))
                .OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                listed.Add(new AdminListedService(coverage.Folder, coverage.Name, "ImageServer", coverage.Description));
            }

            foreach (SystemService service in (await system.ListAsync(cancellation).ConfigureAwait(false))
                .Where(s => Program.DirectoryLists(s, folder, current))
                .OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                listed.Add(new AdminListedService(service.Folder, service.Name, service.Kind, null));
            }
        }

        // ADR-258 §5.8: the geocode service, in its folder, by the rule the services directory lists it by — from
        // configuration, so while blind too. The admin reference's services resource is the container of every service
        // in a folder, and the Utilities folder of "automatically deployed services" is where this one is.
        if (Geocoding.GeocodeServerEndpoints.DirectoryLists(
                context.RequestServices.GetService<Geocoding.GeocoderSettings>(), folder, current))
        {
            listed.Add(new AdminListedService(
                Geocoding.GeocoderSettings.Folder, Geocoding.GeocoderSettings.Name, "GeocodeServer", null));
        }

        if (folder is null)
        {
            string[] folders = await Program.FoldersAsync(catalog.Catalog!, system, services, blind, cancellation).ConfigureAwait(false);
            return Results.Ok(AdminServicesFolder(null, folders, listed));
        }

        if (listed.Count == 0 && !blind
            && !await Program.FolderExistsAsync(catalog.Catalog!, folder, cancellation).ConfigureAwait(false))
        {
            return Results.Json(new { status = "error", messages = new[] { $"Folder '{folder}' does not exist." }, code = 404 });
        }

        return Results.Ok(AdminServicesFolder(folder, null, listed));
    }

    /// <summary>Records one publishing call and answers it just far enough for Pro to make the next.</summary>
    private static async Task<IResult> CaptureAsync(HttpContext context, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal == Principal.Anonymous || !current.Authorization.Allows(Privilege.AdminManageServer))
        {
            return PortalError(403, "Recording Pro's publishing calls is an administrator's experiment on this server.");
        }

        string directory = context.RequestServices.GetRequiredService<IConfiguration>()["Graticula:ProPublishCaptureDirectory"]
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pro-capture");
        Directory.CreateDirectory(directory);

        string stamp = $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss.fff}-{Guid.NewGuid():N}"[..28];
        Dictionary<string, object?> record = new()
        {
            ["at"] = DateTimeOffset.UtcNow,
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path.Value,
            ["query"] = context.Request.Query
                .Where(q => !CapturedSecret(q.Key))
                .ToDictionary(q => q.Key, q => q.Value.ToString()),
            ["agent"] = context.Request.Headers.UserAgent.ToString(),
            ["contentType"] = context.Request.ContentType,
        };

        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);

            record["fields"] = form
                .Where(f => !CapturedSecret(f.Key))
                .ToDictionary(f => f.Key, f => f.Value.ToString());

            List<object> files = [];

            foreach (IFormFile file in form.Files)
            {
                string saved = System.IO.Path.Combine(directory, $"{stamp}-{System.IO.Path.GetFileName(file.FileName)}");

                await using (FileStream target = File.Create(saved))
                {
                    await file.CopyToAsync(target, cancellation).ConfigureAwait(false);
                }

                files.Add(new { field = file.Name, name = file.FileName, file.Length, file.ContentType, saved });
            }

            record["files"] = files;
        }

        await File.WriteAllTextAsync(
            System.IO.Path.Combine(directory, $"{stamp}.json"),
            JsonSerializer.Serialize(record, Indented),
            cancellation).ConfigureAwait(false);

        string path = context.Request.Path.Value ?? string.Empty;
        string item = context.Request.RouteValues.TryGetValue("id", out object? id) ? id?.ToString() ?? string.Empty : string.Empty;

        // Just far enough: an item id for an upload, success for its parts, "completed" for its status, a name that is
        // free — and a refusal that says what happened for the publish itself.
        return path.EndsWith("/addItem", StringComparison.Ordinal)
            ? Results.Ok(new { success = true, id = Guid.NewGuid().ToString("N"), folder = (string?)null })
            : path.EndsWith("/addPart", StringComparison.Ordinal) || path.EndsWith("/update", StringComparison.Ordinal)
                || path.EndsWith("/share", StringComparison.Ordinal) || path.EndsWith("/delete", StringComparison.Ordinal)
                ? Results.Ok(new { success = true, id = item })
            : path.EndsWith("/commit", StringComparison.Ordinal)
                ? Results.Ok(new { success = true, id = item })
            : path.EndsWith("/status", StringComparison.Ordinal)
                ? Results.Ok(new { status = "completed", itemId = item })
            : path.EndsWith("/isServiceNameAvailable", StringComparison.Ordinal)
                ? Results.Ok(new { available = true })
            : path.EndsWith("/shareItems", StringComparison.Ordinal)
                ? Results.Ok(new { results = Array.Empty<object>() })
            : PortalError(400, "Publishing from Pro is being measured on this server: your request and the service "
                + "definition were recorded, and nothing was published yet.");
    }
}
