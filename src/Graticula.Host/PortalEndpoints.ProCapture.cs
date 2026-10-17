using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
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
/// <b>Off unless <c>Graticula:ProPublishCapture</c> is true</b>, which only dev.graticula.com sets. With it on, the
/// portal names this server as its hosting server, the publishing and sharing privileges are advertised to whoever
/// may publish here, and Pro's publishing calls — <c>addItem</c>, <c>addPart</c>, <c>commit</c>, <c>status</c>,
/// <c>publish</c>, <c>isServiceNameAvailable</c> — are answered just far enough for Pro to send the next one. Every
/// call's fields (never its token) and every uploaded file are written under
/// <c>Graticula:ProPublishCaptureDirectory</c>; <c>publish</c> then refuses with a sentence saying so.
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

        // Pro's last act after a publish sets the service item's typeKeywords; this server derives those from the
        // service (PortalEndpoints.Keywords), so the answer is success with nothing stored.
        app.MapPost($"{Path}/content/users/{{username}}/items/{{id}}/update", (string id) => Results.Ok(new { success = true, id }))
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/users/{{username}}/{{folderId:length(32)}}/items/{{id}}/update", (string id) => Results.Ok(new { success = true, id }))
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();

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
    private static async Task<IResult> HostingAdminAsync(HttpContext context, IAdminCatalog catalog, string? rest, CancellationToken cancellation)
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

        if (what.Length == 0)
        {
            return Results.Ok(new
            {
                resources = AdminResources,
                currentVersion = 11.2,
                fullVersion = "11.2.0",
            });
        }

        if (what == "info")
        {
            return Results.Ok(new
            {
                currentversion = 11.2,
                fullVersion = "11.2.0",
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

        if (what == "services")
        {
            IReadOnlyList<string> folders = (await catalog.ListFoldersAsync(cancellation).ConfigureAwait(false))
                .Select(folder => folder.Name).Order(StringComparer.Ordinal).ToList();

            return Results.Ok(new
            {
                folderName = "/",
                description = "Root folder",
                folders,
                foldersDetail = folders.Select(name => new { folderName = name, description = (string?)null, webEncrypted = false, isDefault = false }),
                services = Array.Empty<object>(),
            });
        }

        return Results.Json(new { status = "error", messages = new[] { $"'{what}' is not answered by this server." }, code = 404 });
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
