using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>
/// Portal content adds as doors onto the import Studio already has — ADR-257, Q-225: <c>addItem</c> with a data file,
/// <c>publish</c> of that item, <c>content/features/analyze</c>, and <c>createService</c> for a designed layer or a view.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS veteran's review of 2026-11-02 ranked these first</b> among what stops a publisher in the first hour:
/// the ArcGIS API for Python's <c>gis.content.add</c> and <c>item.publish()</c> took only a service definition and a
/// web map here, while Studio imports a CSV, a zipped shapefile or file geodatabase, GeoJSON and a workbook (ADR-106,
/// ADR-112) and makes views (ADR-113).
/// </para>
/// <para>
/// <b>A door, not a second importer</b> — the owner's words, <i>"var olan içe aktarmanın kapısı olarak"</i>. Each one
/// shapes the portal's request into the one Studio's handler reads and runs that handler inside this request
/// (<see cref="InnerAnswerAsync"/>), so the privilege check, the store call, the audit line and every refusal are
/// Studio's; only the answer is reworded into the portal's envelope. An added file is a document (ADR-188), a
/// published one is Studio's <c>POST /admin/hosted/import</c>, a geodatabase is its inspection and publish jobs, a
/// designed layer is <c>POST /admin/hosted/define</c>, a view is <c>POST /admin/services/{name}/views</c>.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The <c>publish</c> file types a data item takes, each with the ArcGIS item type it publishes from.</summary>
    private static readonly FrozenDictionary<string, string> DataFileTypes = new Dictionary<string, string>
    {
        ["csv"] = "CSV",
        ["shapefile"] = "Shapefile",
        ["fileGeodatabase"] = "File Geodatabase",
        ["geojson"] = "GeoJson",
        ["excel"] = "Microsoft Excel",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <c>addItem</c> of this ArcGIS item type is a file Studio keeps as a document: a data file, or a PDF, an
    /// Office file or a picture (ADR-188).
    /// </summary>
    internal static bool IsFileItemType(string? itemType) =>
        Documents.Accepted.Concat(Documents.Data)
            .Any(kind => string.Equals(kind.ArcGisType, itemType?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>publish</c> file type for an ArcGIS item type a data file is added as, or null.</summary>
    internal static string? FileTypeOf(string? itemType) =>
        DataFileTypes.FirstOrDefault(p => string.Equals(p.Value, itemType?.Trim(), StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>
    /// The job ids a publish answered with when the import had finished before the answer — the portal's clients poll
    /// a job they are given, and this one is done (ADR-257). Held an hour, at most <see cref="MaximumStaged"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (Guid Owner, DateTimeOffset At)> Finished = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A job id for a publish that is already done, for <paramref name="owner"/>.</summary>
    private static string FinishedJob(Guid owner)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (KeyValuePair<string, (Guid Owner, DateTimeOffset At)> held in Finished)
        {
            if (held.Value.At < now.AddHours(-1) || Finished.Count > MaximumStaged)
            {
                Finished.TryRemove(held.Key, out _);
            }
        }

        string job = Guid.NewGuid().ToString("N");
        Finished[job] = (owner, now);
        return job;
    }

    /// <summary>Whether <paramref name="job"/> is a publish this door answered as done, for that owner.</summary>
    private static bool PublishedWhileAsked(string job, Guid owner) =>
        Finished.TryGetValue(job, out (Guid Owner, DateTimeOffset At) held) && held.Owner == owner;

    /// <summary>The routes beside addItem and publish: analyze and createService. Mapped where those are.</summary>
    private static void MapContentAdds(WebApplication app)
    {
        if (!PublishingFromPro(app.Configuration))
        {
            return;
        }

        app.MapMethods($"{Path}/content/features/analyze", ["GET", "POST"], AnalyzeAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();

        foreach (string user in new[] { $"{Path}/content/users/{{username}}", $"{Path}/content/users/{{username}}/{{folderId:length(32)}}" })
        {
            app.MapPost($"{user}/createService", CreateServiceAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        }
    }

    /// <summary>The caller's own folder the path names, or null for the root; false when it names one that is not theirs.</summary>
    private static async Task<(bool Ok, Guid? Folder)> PathFolderAsync(
        HttpContext context, Guid owner, CancellationToken cancellation)
    {
        if (!context.Request.RouteValues.TryGetValue("folderId", out object? given) || given is not string text)
        {
            return (true, null);
        }

        return Guid.TryParse(text, out Guid id)
            && context.RequestServices.GetService<IContentFolderStore>() is { } folders
            && await folders.FindAsync(id, cancellation).ConfigureAwait(false) is { } folder
            && folder.Owner == owner
                ? (true, id)
                : (false, null);
    }

    /// <summary>
    /// Portal's <c>addItem</c> for a data file — ADR-257: in one request, Studio's document upload; in parts
    /// (<c>multipart=true</c>, the ArcGIS API for Python's default), staged as a service definition is and made a
    /// document by its <c>commit</c>.
    /// </summary>
    private static async Task<IResult> AddDataItemAsync(
        HttpContext context, IFormCollection form, string type, string username, ImportScratch scratch,
        IContentFolderStore folders, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to add content from another site.");
        }

        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        (bool known, Guid? folder) = await PathFolderAsync(context, current.Principal.Id, cancellation).ConfigureAwait(false);
        if (!known)
        {
            return Unknown("Folder");
        }

        if (TagsOf(First(form, context, "tags"), out string? badTags) is null && badTags is not null)
        {
            return PortalError(400, badTags);
        }

        if (string.Equals(First(form, context, "multipart"), "true", StringComparison.OrdinalIgnoreCase))
        {
            // Said before any part is sent: Studio's handler says it again at the commit.
            if (!current.Authorization.Allows(Privilege.ContentCreate))
            {
                return PortalError(403, "Adding content needs the privilege to create content.");
            }

            string id = Guid.NewGuid().ToString("N");
            string directory = StagingDirectory(scratch, id);
            Directory.CreateDirectory(directory);

            string fileName = First(form, context, "fileName") is { Length: > 0 } named ? named : First(form, context, "filename");
            Stage(new StagedDefinition(id, current.Principal.Id, directory,
                First(form, context, "title") is { Length: > 0 } title ? title : fileName)
            {
                ItemType = type,
                FileName = fileName,
                Folder = folder,
            });

            return Results.Ok(new { success = true, id, folder = folder?.ToString("N") });
        }

        if (form.Files.GetFile("file") is not { Length: > 0 } file)
        {
            return PortalError(400, $"A {type} item is added with its 'file', or with multipart=true and its parts.");
        }

        return await AddDocumentAsync(context, current, form, file, type, folder, cancellation).ConfigureAwait(false);
    }

    /// <summary>The <c>tags</c> a portal client sends, as Studio keeps them; null with a reason when they cannot be kept.</summary>
    private static IReadOnlyList<string>? TagsOf(string? sent, out string? why)
    {
        why = null;

        if (string.IsNullOrWhiteSpace(sent))
        {
            return null;
        }

        return ItemTags.Normalise(sent.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), out why);
    }

    /// <summary>
    /// The file made a document through Studio's own <c>POST /content/documents</c>, then its tags, description and
    /// folder through Studio's own handlers and store — one item, as Studio's upload makes it.
    /// </summary>
    private static async Task<IResult> AddDocumentAsync(
        HttpContext context, RequestPrincipal current, IFormCollection form, IFormFile file, string type, Guid? folder,
        CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        IDocumentStore documents = services.GetRequiredService<IDocumentStore>();
        IAuditLog audit = services.GetRequiredService<IAuditLog>();

        // What Studio's upload reads, in its own names: the portal's `snippet` is its snippet, `type` the declared kind.
        Dictionary<string, StringValues> fields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = type,
        };

        if (First(form, context, "title") is { Length: > 0 } title)
        {
            fields["title"] = title;
        }

        if (First(form, context, "snippet") is { Length: > 0 } snippet)
        {
            fields["snippet"] = snippet;
        }

        (int status, string? message, JsonElement made) = await WithFormAsync(context, fields, file, () =>
            InnerAnswerAsync(context, () => AdminEndpoints.CreateDocumentAsync(context, documents, audit, cancellation)))
            .ConfigureAwait(false);

        if (status is < 200 or >= 300 || made.ValueKind != JsonValueKind.Object
            || !made.TryGetProperty("id", out JsonElement madeId) || madeId.GetString() is not { } id)
        {
            return PortalError(status is >= 200 and < 300 ? 500 : status, message ?? $"'{file.FileName}' was not added.");
        }

        if (TagsOf(First(form, context, "tags"), out _) is { Count: > 0 } tags)
        {
            await InnerAsync(context, () => AdminEndpoints.SetDocumentTagsAsync(
                context, id, new AdminEndpoints.TagsRequest([.. tags]), documents, audit, cancellation)).ConfigureAwait(false);
        }

        if (First(form, context, "description") is { Length: > 0 } description)
        {
            await InnerAsync(context, () => AdminEndpoints.SetDocumentDescriptionAsync(
                context, id, new AdminEndpoints.WebMapDescriptionRequest(description), documents, audit, cancellation))
                .ConfigureAwait(false);
        }

        if (folder is { } into)
        {
            await services.GetRequiredService<IContentFolderStore>().MoveDocumentAsync(id, into, cancellation).ConfigureAwait(false);
        }

        return Results.Ok(new { success = true, id, folder = folder?.ToString("N") });
    }

    /// <summary>
    /// A data file's parts, joined by <c>commit</c>, made a document by the door a single-request <c>addItem</c> takes.
    /// The commit's own fields — the ArcGIS API for Python sends the item's properties again — win over what
    /// <c>addItem</c> said.
    /// </summary>
    private static async Task<IResult> CommitDataItemAsync(
        HttpContext context, StagedDefinition staged, CancellationToken cancellation)
    {
        if (staged.DocumentId is { } done)
        {
            return Results.Ok(new { success = true, id = staged.Id, itemId = done });
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        Dictionary<string, StringValues> fields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = First(form, context, "title") is { Length: > 0 } title ? title : staged.Title,
        };

        foreach (string name in (string[])["snippet", "tags", "description"])
        {
            if (First(form, context, name) is { Length: > 0 } sent)
            {
                fields[name] = sent;
            }
        }

        string fileName = staged.FileName is { Length: > 0 } named ? named : staged.Title;
        IResult answer;

        await using (FileStream stream = File.OpenRead(staged.File))
        {
            FormFile file = new(stream, 0, stream.Length, "file", fileName) { Headers = new HeaderDictionary() };
            answer = await AddDocumentAsync(
                context, current, new FormCollection(fields), file, staged.ItemType!, staged.Folder, cancellation).ConfigureAwait(false);
        }

        if (answer is IValueHttpResult { Value: { } value }
            && JsonSerializer.SerializeToElement(value) is { ValueKind: JsonValueKind.Object } said
            && said.TryGetProperty("id", out JsonElement id) && id.GetString() is { } document)
        {
            staged.DocumentId = document;
            Remember(staged);
            File.Delete(staged.File);
            return Results.Ok(new { success = true, id = staged.Id, itemId = document });
        }

        return answer;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the request's form replaced by <paramref name="fields"/> and
    /// <paramref name="file"/> — what a Studio handler reads — and puts the portal's form back after.
    /// </summary>
    private static async Task<T> WithFormAsync<T>(
        HttpContext context, Dictionary<string, StringValues> fields, IFormFile? file, Func<Task<T>> work)
    {
        IFormFeature? before = context.Features.Get<IFormFeature>();
        FormFileCollection files = [];

        if (file is not null)
        {
            files.Add(file);
        }

        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(fields, files)));

        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            context.Features.Set(before);
        }
    }

    /// <summary>A service name from an item's title, as the ArcGIS API for Python makes one: every run of non-word characters an underscore.</summary>
    internal static string ServiceNameFrom(string title) =>
        Regex.Replace(title.Trim(), @"[\W_]+", "_", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim('_') is { Length: > 0 } name
            ? name
            : "layer";

    /// <summary>What a <c>publishParameters</c> document asks of a data item's publish.</summary>
    internal sealed record DataPublish(string? Name, string? LocationType, string? X, string? Y, int? Srid);

    /// <summary>
    /// Reads <c>publishParameters</c>: the service <c>name</c>, the CSV's <c>locationType</c> and its
    /// <c>longitudeFieldName</c> and <c>latitudeFieldName</c>, and <c>sourceSR</c> — the rest is ArcGIS's own
    /// presentation and is passed over (INFERRED, ADR-257 §5).
    /// </summary>
    internal static DataPublish? PublishParametersOf(string? json, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return new DataPublish(null, null, null, null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "'publishParameters' is a JSON object.";
                return null;
            }

            static string? Text(JsonElement on, string name) =>
                on.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                    ? s.Trim()
                    : null;

            int? srid = root.TryGetProperty("sourceSR", out JsonElement sr) && sr.ValueKind == JsonValueKind.Object
                && (sr.TryGetProperty("latestWkid", out JsonElement w) || sr.TryGetProperty("wkid", out w))
                && w.TryGetInt32(out int wkid) && wkid > 0
                    ? wkid
                    : null;

            return new DataPublish(
                Text(root, "name"), Text(root, "locationType"), Text(root, "longitudeFieldName"), Text(root, "latitudeFieldName"), srid);
        }
        catch (JsonException bad)
        {
            error = $"'publishParameters' is not JSON: {bad.Message}";
            return null;
        }
    }

    /// <summary>
    /// Portal's <c>publish</c> of a data item — ADR-257: the document's bytes handed to Studio's import as Studio's
    /// upload hands them, and a file geodatabase through Studio's inspection and publish jobs.
    /// </summary>
    private static async Task<IResult> PublishDataItemAsync(
        HttpContext context, IFormCollection form, RequestPrincipal current, string fileType, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        IDocumentStore documents = services.GetRequiredService<IDocumentStore>();

        string itemId = First(form, context, "itemid") is { Length: > 0 } given ? given : First(form, context, "itemId");

        if (!Guid.TryParse(itemId, out _)
            || await documents.FindAsync(itemId.ToLowerInvariant(), cancellation).ConfigureAwait(false) is not { } document
            || (document.Owner != current.Principal.Id && !current.Authorization.Allows(Privilege.AdminManageAllContent)))
        {
            return Unknown("Item");
        }

        string expected = DataFileTypes[fileType];
        if (!string.Equals(document.Kind.ArcGisType, expected, StringComparison.OrdinalIgnoreCase))
        {
            return PortalError(400, $"'{document.Title}' is a {document.Kind.ArcGisType} item, not a {expected} one.");
        }

        if (string.Equals(First(form, context, "overwrite"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return PortalError(400,
                "Overwriting a layer from a data item is not offered here (ADR-257); Studio's Update data, or the layer's "
                + "append with truncateExisting, replaces its rows. Nothing was written.");
        }

        if (PublishParametersOf(First(form, context, "publishParameters"), out string? unreadable) is not { } asked)
        {
            return PortalError(400, unreadable!);
        }

        // Only coordinates: Studio's import reads x and y, or WKT, and does not geocode (ADR-112).
        if (asked.LocationType is { } location && !location.Equals("coordinates", StringComparison.OrdinalIgnoreCase))
        {
            return PortalError(400,
                $"locationType '{location}' is not offered: a table is published from its coordinate columns (x and y, "
                + "longitude and latitude, or WKT). Nothing was written.");
        }

        (bool known, Guid? folder) = await PathFolderAsync(context, current.Principal.Id, cancellation).ConfigureAwait(false);
        if (!known)
        {
            return Unknown("Folder");
        }

        if (await documents.ContentAsync(document.Id, cancellation).ConfigureAwait(false) is not { } content)
        {
            return Unknown("Item");
        }

        string name = asked.Name is { Length: > 0 } wanted ? wanted : ServiceNameFrom(document.Title);
        Dictionary<string, StringValues> fields = new(StringComparer.OrdinalIgnoreCase) { ["name"] = name };

        if (asked.X is { } x)
        {
            fields["x"] = x;
        }

        if (asked.Y is { } y)
        {
            fields["y"] = y;
        }

        if (asked.Srid is { } srid)
        {
            fields["srid"] = srid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using MemoryStream bytes = new(content, writable: false);
        FormFile file = new(bytes, 0, bytes.Length, "file", document.FileName) { Headers = new HeaderDictionary() };

        PostGisImporter importer = services.GetRequiredService<PostGisImporter>();
        IAdminCatalog catalog = services.GetRequiredService<IAdminCatalog>();
        IAuditLog audit = services.GetRequiredService<IAuditLog>();
        IJobStore jobs = services.GetRequiredService<IJobStore>();
        JobSignal signal = services.GetRequiredService<JobSignal>();
        GeodatabaseReader reader = services.GetRequiredService<GeodatabaseReader>();
        ImportScratch scratch = services.GetRequiredService<ImportScratch>();

        (int status, string? message, JsonElement answer) = await WithFormAsync(context, fields, file, () =>
            InnerAnswerAsync(context, () => HostedDataEndpoints.ImportAsync(
                context, importer, catalog, audit, jobs, signal, reader, scratch, cancellation)))
            .ConfigureAwait(false);

        if (status is < 200 or >= 300)
        {
            return PortalError(status, message ?? $"'{document.Title}' was not published.");
        }

        string job;
        string? service;

        if (status == StatusCodes.Status202Accepted)
        {
            // A file geodatabase: Studio's upload opened an inspection, and its publish is a second request (ADR-038).
            if (!answer.TryGetProperty("job", out JsonElement opened) || !Guid.TryParse(opened.GetString(), out Guid inspection))
            {
                return PortalError(500, $"'{document.Title}' was not published: the import answered no job.");
            }

            JobRecord? inspected = await AwaitJobAsync(jobs, inspection, current.Principal.Id, TimeSpan.FromMinutes(2), cancellation)
                .ConfigureAwait(false);

            if (inspected?.Status != JobStatus.Done || LayerNames(inspected.Detail) is not { Count: > 0 } layers)
            {
                return PortalError(400, inspected?.Failure
                    ?? "The geodatabase could not be read in time, or holds no layer to publish. Nothing was published.");
            }

            (int published, string? refused, JsonElement queued) = await InnerAnswerAsync(context, () =>
                HostedDataEndpoints.PublishGeodatabaseAsync(
                    context, new HostedDataEndpoints.GeodatabasePublish(inspection, name, layers, null),
                    jobs, signal, catalog, reader, scratch, cancellation)).ConfigureAwait(false);

            if (published is < 200 or >= 300 || !queued.TryGetProperty("job", out JsonElement importing)
                || !Guid.TryParse(importing.GetString(), out Guid import))
            {
                return PortalError(published is >= 200 and < 300 ? 500 : published, refused ?? $"'{document.Title}' was not published.");
            }

            JobRecord? imported = await AwaitJobAsync(jobs, import, current.Principal.Id, TimeSpan.FromMinutes(3), cancellation)
                .ConfigureAwait(false);

            if (imported?.Status is JobStatus.Failed or JobStatus.Cancelled)
            {
                return PortalError(500, imported.Failure ?? "The import failed.");
            }

            job = import.ToString("N");
            service = imported?.Status == JobStatus.Done ? name : null;
        }
        else
        {
            job = FinishedJob(current.Principal.Id);
            service = ServiceOfImport(answer) ?? name;
        }

        ServiceAtAddress? made = service is null
            ? null
            : await catalog.FindServiceAtAsync(FeatureServerMetadataWriter.HostedFolder, service, cancellation).ConfigureAwait(false);

        if (made is { } done)
        {
            if (folder is { } into)
            {
                await services.GetRequiredService<IContentFolderStore>().MoveServiceAsync(done.Id, into, cancellation).ConfigureAwait(false);
            }

            // The item's own words go to the layer's item, as the portal's publish carries them.
            await ApplyDetailsAsync(
                new ItemDetails(null, document.Description ?? document.Snippet, document.Tags is { Count: > 0 } t ? string.Join(",", t) : null),
                done.Name, catalog, cancellation).ConfigureAwait(false);
        }

        string url = $"{context.Request.Scheme}://{context.Request.Host}/rest/services/{FeatureServerMetadataWriter.HostedFolder}/"
            + $"{Uri.EscapeDataString(made?.Name ?? name)}/FeatureServer";

        return Results.Ok(new
        {
            services = new[]
            {
                new
                {
                    type = "Feature Service",
                    serviceurl = url,
                    encodedServiceURL = url,
                    size = document.Size,
                    jobId = job,

                    // Still importing: the source item stands in until the service exists (INFERRED, ADR-257 §5).
                    serviceItemId = made is { } service1 ? service1.Id.ToString("N") : document.Id,
                    serviceItemType = "Feature Service",
                    success = true,
                },
            },
        });
    }

    /// <summary>The service an import made, from its own answer's <c>services.feature</c>, or null.</summary>
    internal static string? ServiceOfImport(JsonElement answer)
    {
        if (answer.ValueKind == JsonValueKind.Object
            && answer.TryGetProperty("services", out JsonElement services)
            && services.TryGetProperty("feature", out JsonElement feature)
            && feature.GetString() is { } path)
        {
            string prefix = $"/rest/services/{FeatureServerMetadataWriter.HostedFolder}/";
            int at = path.IndexOf("/FeatureServer", StringComparison.Ordinal);

            if (path.StartsWith(prefix, StringComparison.Ordinal) && at > prefix.Length)
            {
                return path[prefix.Length..at];
            }
        }

        return null;
    }

    /// <summary>
    /// Portal's <c>content/features/analyze</c> for an item — what the ArcGIS API for Python asks before it publishes a
    /// CSV. It analyses nothing: the import's own reader finds the coordinate columns when the publish runs, as Studio's
    /// upload does, so the answer names the service and the location type only (INFERRED, ADR-257 §5).
    /// </summary>
    private static async Task<IResult> AnalyzeAsync(HttpContext context, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return PortalError(403, "Analyzing a file needs a signed-in member.");
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;
        string itemId = First(form, context, "itemid") is { Length: > 0 } given ? given : First(form, context, "itemId");

        if (itemId.Length == 0)
        {
            return PortalError(400, "Analyze reads an item here: add the file with addItem first, then name it as 'itemid' (ADR-257).");
        }

        if (await ReadableDocumentAsync(context, itemId, cancellation).ConfigureAwait(false) is not { } document
            || FileTypeOf(document.Kind.ArcGisType) is not { } fileType)
        {
            return Unknown("Item");
        }

        Dictionary<string, object?> parameters = new()
        {
            ["type"] = fileType,
            ["name"] = ServiceNameFrom(document.Title),
        };

        if (fileType is "csv" or "excel")
        {
            parameters["locationType"] = "coordinates";
        }

        return Results.Ok(new { publishParameters = parameters, records = Array.Empty<object>() });
    }

    /// <summary>ArcGIS geometry types, as the hosted geometry types Studio designs a layer with (INFERRED: always multi-part, as ArcGIS's are).</summary>
    private static readonly FrozenDictionary<string, string> DesignedGeometry = new Dictionary<string, string>
    {
        ["esriGeometryPoint"] = "Point",
        ["esriGeometryMultipoint"] = "MultiPoint",
        ["esriGeometryPolyline"] = "MultiLineString",
        ["esriGeometryPolygon"] = "MultiPolygon",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fields a client describes that the table makes itself: the object id and the geometry.</summary>
    private static readonly HashSet<string> MadeByTheTable = new(StringComparer.OrdinalIgnoreCase)
    {
        "esriFieldTypeOID", "esriFieldTypeGeometry",
    };

    /// <summary>
    /// Portal's <c>createService</c> — ADR-257: a service with its layers named in <c>createParameters.layers</c>
    /// (and <c>tables</c>), each one designed by Studio's <c>POST /admin/hosted/define</c>; or, with
    /// <c>isView=true</c>, a view of the source the layers' <c>adminLayerInfo.viewLayerDefinition</c> names, made by
    /// Studio's view. A service with no layers is refused: none is created here (ADR-057 condition 4).
    /// </summary>
    private static async Task<IResult> CreateServiceAsync(HttpContext context, string username, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to create a service from another site.");
        }

        if (Publisher(context, username) is not { } current)
        {
            return PortalError(403, "Creating a service needs the privilege to publish features.");
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;

        if (First(form, context, "outputType") is { Length: > 0 } output
            && !output.Equals("featureService", StringComparison.OrdinalIgnoreCase))
        {
            return PortalError(400, $"outputType '{output}' is not created here; a featureService is.");
        }

        (bool known, Guid? folder) = await PathFolderAsync(context, current.Principal.Id, cancellation).ConfigureAwait(false);
        if (!known)
        {
            return Unknown("Folder");
        }

        JsonElement create;
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(First(form, context, "createParameters") is { Length: > 0 } text ? text : "{}");
            create = parsed.RootElement.Clone();
        }
        catch (JsonException bad)
        {
            return PortalError(400, $"'createParameters' is not JSON: {bad.Message}");
        }

        string name = create.ValueKind == JsonValueKind.Object && create.TryGetProperty("name", out JsonElement n)
            && n.GetString() is { Length: > 0 } named
                ? named.Trim()
                : string.Empty;

        if (name.Length == 0)
        {
            return PortalError(400, "'createParameters' names the service: its 'name' is required.");
        }

        bool view = string.Equals(First(form, context, "isView"), "true", StringComparison.OrdinalIgnoreCase)
            || (create.TryGetProperty("isView", out JsonElement isView) && isView.ValueKind == JsonValueKind.True);

        List<JsonElement> layers = [];
        foreach (string list in (string[])["layers", "tables"])
        {
            if (create.TryGetProperty(list, out JsonElement each) && each.ValueKind == JsonValueKind.Array)
            {
                layers.AddRange(each.EnumerateArray());
            }
        }

        if (layers.Count == 0)
        {
            return PortalError(400,
                (view ? "A view is made of its source in one request here" : "A service is created with its layers here")
                + ": name them in createParameters." + (view
                    ? "layers, each with adminLayerInfo.viewLayerDefinition's sourceServiceName and sourceLayerId."
                    : "layers, each with its name, geometryType and fields.")
                + " An empty service filled by addToDefinition afterwards is not made (ADR-057 condition 4, ADR-257).");
        }

        IResult result = view
            ? await CreateViewServiceAsync(context, name, create, layers, cancellation).ConfigureAwait(false)
            : await CreateDesignedServiceAsync(context, name, create, layers, cancellation).ConfigureAwait(false);

        if (result is not IValueHttpResult { Value: string madeName })
        {
            return result;
        }

        // A view is made beside its source, and the source of one made here is hosted, as every made service is.
        IAdminCatalog catalog = context.RequestServices.GetRequiredService<IAdminCatalog>();

        if (await catalog.FindServiceAtAsync(FeatureServerMetadataWriter.HostedFolder, madeName, cancellation).ConfigureAwait(false)
            is not { } made)
        {
            return PortalError(500, $"'{madeName}' was made but could not be found after.");
        }

        if (folder is { } into)
        {
            await context.RequestServices.GetRequiredService<IContentFolderStore>().MoveServiceAsync(made.Id, into, cancellation)
                .ConfigureAwait(false);
        }

        await ApplyDetailsAsync(
            new ItemDetails(First(form, context, "snippet"), First(form, context, "description"), First(form, context, "tags")),
            made.Name, catalog, cancellation).ConfigureAwait(false);

        string url = $"{context.Request.Scheme}://{context.Request.Host}/rest/services/{made.Folder ?? FeatureServerMetadataWriter.HostedFolder}/"
            + $"{Uri.EscapeDataString(made.Name)}/FeatureServer";

        return Results.Ok(new
        {
            encodedServiceURL = url,
            itemId = made.Id.ToString("N"),
            name = made.Name,
            serviceItemId = made.Id.ToString("N"),
            serviceurl = url,
            size = -1,
            success = true,
            type = "Feature Service",
            isView = view,
        });
    }

    /// <summary>
    /// The layers of a <c>createService</c>, each designed through Studio's <c>POST /admin/hosted/define</c> into one
    /// service. Every layer is read and checked before the first is made, so a refusal leaves nothing behind; a failure
    /// past that leaves the layers before it, and the answer names them.
    /// </summary>
    /// <returns>The service's name as a value result, or the refusal.</returns>
    private static async Task<IResult> CreateDesignedServiceAsync(
        HttpContext context, string name, JsonElement create, List<JsonElement> layers, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        IAdminCatalog catalog = services.GetRequiredService<IAdminCatalog>();

        if (await catalog.FindServiceAtAsync(FeatureServerMetadataWriter.HostedFolder, name, cancellation).ConfigureAwait(false) is not null)
        {
            return PortalError(409, $"A service named '{name}' already exists in hosted.");
        }

        int? srid = create.TryGetProperty("spatialReference", out JsonElement sr) && sr.ValueKind == JsonValueKind.Object
            && (sr.TryGetProperty("latestWkid", out JsonElement w) || sr.TryGetProperty("wkid", out w))
            && w.TryGetInt32(out int wkid) && wkid > 0
                ? wkid
                : null;

        List<HostedDataEndpoints.LayerDesign> designs = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (JsonElement layer in layers)
        {
            string layerName = layer.TryGetProperty("name", out JsonElement ln) && ln.GetString() is { Length: > 0 } given
                ? given.Trim()
                : layers.Count == 1 ? name : string.Empty;

            if (layerName.Length == 0 || !names.Add(layerName))
            {
                return PortalError(400, "Every layer in createParameters has a name of its own.");
            }

            string? geometry = layer.TryGetProperty("geometryType", out JsonElement g) ? g.GetString() : null;
            string designed;

            if (geometry is null)
            {
                designed = "Table";
            }
            else if (!DesignedGeometry.TryGetValue(geometry, out designed!))
            {
                return PortalError(400, $"Layer '{layerName}' has geometryType '{geometry}'; a hosted layer is one of "
                    + string.Join(", ", DesignedGeometry.Keys) + ", or none for a table.");
            }

            JsonElement[] described = layer.TryGetProperty("fields", out JsonElement f) && f.ValueKind == JsonValueKind.Array
                ? [.. f.EnumerateArray().Where(field => !(field.TryGetProperty("type", out JsonElement t)
                    && MadeByTheTable.Contains(t.GetString() ?? string.Empty)))]
                : [];

            List<HostedDataEndpoints.FieldDesign> fields = [];

            if (described.Length > 0
                && !ArcGisAdminEndpoints.TryDefinition(
                    JsonSerializer.Serialize(new { fields = described }), adding: true, out fields, out string? fieldError))
            {
                return PortalError(400, $"Layer '{layerName}': {fieldError}");
            }

            if (await HostedDataEndpoints.NameTakenAsync(catalog, layerName, cancellation).ConfigureAwait(false))
            {
                return PortalError(409, $"A layer named '{layerName}' already exists. Nothing was created.");
            }

            designs.Add(new HostedDataEndpoints.LayerDesign(layerName, designed, fields, null, name, Srid: designed == "Table" ? null : srid));
        }

        PostGisImporter importer = services.GetRequiredService<PostGisImporter>();
        IFieldDomainStore domains = services.GetRequiredService<IFieldDomainStore>();
        IAuditLog audit = services.GetRequiredService<IAuditLog>();
        List<string> made = [];

        foreach (HostedDataEndpoints.LayerDesign design in designs)
        {
            (int status, string? message) = await InnerAsync(context, () => HostedDataEndpoints.DefineAsync(
                context, design, importer, catalog, domains, audit, cancellation)).ConfigureAwait(false);

            if (status is < 200 or >= 300)
            {
                return PortalError(status, (message ?? $"Layer '{design.Name}' was not created.")
                    + (made.Count > 0 ? $" Already made in '{name}': {string.Join(", ", made)}." : string.Empty));
            }

            made.Add(design.Name!);
        }

        return Results.Ok(name);
    }

    /// <summary>
    /// A view through Studio's <c>POST /admin/services/{source}/views</c>: one source service, the layers by their
    /// <c>sourceLayerId</c>, the capabilities <c>createParameters</c> names.
    /// </summary>
    /// <returns>The view's name as a value result, or the refusal.</returns>
    private static async Task<IResult> CreateViewServiceAsync(
        HttpContext context, string name, JsonElement create, List<JsonElement> layers, CancellationToken cancellation)
    {
        HashSet<string> sources = new(StringComparer.OrdinalIgnoreCase);
        List<int> indexes = [];

        foreach (JsonElement layer in layers)
        {
            if (!layer.TryGetProperty("adminLayerInfo", out JsonElement info)
                || !info.TryGetProperty("viewLayerDefinition", out JsonElement definition)
                || !definition.TryGetProperty("sourceServiceName", out JsonElement source) || source.GetString() is not { Length: > 0 } sourceName
                || !definition.TryGetProperty("sourceLayerId", out JsonElement index) || !index.TryGetInt32(out int sourceLayer))
            {
                return PortalError(400,
                    "Each layer of a view names its source in adminLayerInfo.viewLayerDefinition: sourceServiceName and sourceLayerId.");
            }

            sources.Add(sourceName);
            indexes.Add(sourceLayer);
        }

        if (sources.Count != 1)
        {
            return PortalError(400, "A view here shows one source service; these layers name " + string.Join(", ", sources) + ".");
        }

        string[]? capabilities = create.TryGetProperty("capabilities", out JsonElement c) && c.GetString() is { Length: > 0 } listed
            ? listed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : null;

        IServiceProvider services = context.RequestServices;
        AdminEndpoints.ViewRequest request = new(name, [.. indexes], null, capabilities, null);

        (int status, string? message) = await InnerAsync(context, () => AdminEndpoints.CreateViewAsync(
            context, sources.First(), FeatureServerMetadataWriter.HostedFolder, request,
            services.GetRequiredService<IAdminCatalog>(), services.GetRequiredService<PostgresLayerCatalog>(),
            services.GetRequiredService<PostGisImporter>(), services.GetRequiredService<ServiceContexts>(),
            services.GetRequiredService<IAuditLog>(), cancellation)).ConfigureAwait(false);

        return status is >= 200 and < 300
            ? Results.Ok(name)
            : PortalError(status, message ?? $"The view '{name}' was not made.");
    }
}
