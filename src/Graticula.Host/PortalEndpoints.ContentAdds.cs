using System;
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
            .Any(kind => string.Equals(kind.ArcGisType, itemType?.Trim(), StringComparison.OrdinalIgnoreCase))
        // ADR-269: Pro adds a scene layer package as "Scene Package"; it is kept as the scene service it serves.
        || string.Equals(itemType?.Trim(), Documents.ScenePackageType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>publish</c> file type for an ArcGIS item type a data file is added as, or null.</summary>
    internal static string? FileTypeOf(string? itemType) =>
        DataFileTypes.FirstOrDefault(p => string.Equals(p.Value, itemType?.Trim(), StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>
    /// A job id for a publish whose import finished before the answer (ADR-257): the portal's clients poll the job they
    /// are given. It is not a GUID, so the item's <c>status</c> on any node of a site finds no job to look up and
    /// answers <c>completed</c>, which it is; nothing has to remember it.
    /// </summary>
    internal static string FinishedJob() => "done" + Guid.NewGuid().ToString("N")[4..];

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

        // ADR-264: an app or a link is added with its text, its address, or both, and no file.
        if (Documents.AppOfType(type) is { } app && form.Files.GetFile("file") is null)
        {
            return await AddAppItemAsync(context, current, form, app, folder, cancellation).ConfigureAwait(false);
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
        CancellationToken cancellation, Guid? given = null)
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
            InnerAnswerAsync(context, () => AdminEndpoints.CreateDocumentUnderAsync(context, documents, audit, given, cancellation)))
            .ConfigureAwait(false);

        if (status is < 200 or >= 300 || made.ValueKind != JsonValueKind.Object
            || !made.TryGetProperty("id", out JsonElement madeId) || madeId.GetString() is not { } documentId)
        {
            return PortalError(status is >= 200 and < 300 ? 500 : status, message ?? $"'{file.FileName}' was not added.");
        }

        string id = documentId;

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
            // ADR-269 condition 2: the item keeps the id addItem gave it, as a portal's does — Pro asks for it by that id
            // after commit, and a document under another id was one it could not find, update, share or publish.
            answer = await AddDocumentAsync(
                context, current, new FormCollection(fields), file, staged.ItemType!, staged.Folder, cancellation,
                Guid.TryParseExact(staged.Id, "N", out Guid stagedId) ? stagedId : null).ConfigureAwait(false);
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

        // ADR-257 §12: an overwrite is Studio's Overwrite of the layer this item was published to (PortalEndpoints.Overwrite).
        if (string.Equals(First(form, context, "overwrite"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return await OverwriteFromItemAsync(context, form, document, cancellation).ConfigureAwait(false);
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
            job = FinishedJob();
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

            // ADR-260: the layer's item is called what the data's item is called; the service keeps the name it was given.
            if (document.Title.Trim() is { Length: > 0 and <= WebMaps.MaximumTitleLength } titled && titled != done.Name)
            {
                await catalog.SetServiceTitleAsync(done.Name, FeatureServerMetadataWriter.HostedFolder, titled, cancellation)
                    .ConfigureAwait(false);
            }

            // ADR-257 §12: the service remembers the item it was published from, as ArcGIS's Service2Data.
            await services.GetRequiredService<IItemAnnotationStore>()
                .RelateAsync(done.Id, Guid.Parse(document.Id), Service2Data, cancellation).ConfigureAwait(false);
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

    /// <summary>The keys of an ArcGIS layer definition a designed layer is made from.</summary>
    private static readonly HashSet<string> DesignedLayerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "type", "geometryType", "fields", "id", "hasZ", "hasM", "extent", "spatialReference",
    };

    /// <summary>
    /// Keys of an ArcGIS layer definition that describe what this server works out for itself, or that change nothing
    /// a client can observe here, and are passed over. ADR-257 §11: the ArcGIS API for Python's own examples send them,
    /// and refusing a definition for its <c>objectIdField</c> would refuse every one of them.
    /// </summary>
    private static readonly HashSet<string> DescriptiveLayerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "objectIdField", "uniqueIdField", "displayField", "description", "copyrightText", "defaultVisibility",
        "minScale", "maxScale", "htmlPopupType", "capabilities", "supportsRollbackOnFailureParameter",
        "allowGeometryUpdates", "currentVersion", "maxRecordCount", "supportedQueryFormats", "hasStaticData",
        "isDataVersioned", "supportsAdvancedQueries", "supportsStatistics", "useStandardizedQueries", "popupInfo",
    };

    /// <summary>
    /// The hosted layer an ArcGIS layer definition describes — <c>createService</c>'s <c>createParameters.layers</c> and
    /// a service's <c>addToDefinition</c> (ADR-257 §5.5, §11). Every key is either read, passed over as descriptive, or
    /// refused by name: a definition told <c>success</c> while its renderer or its attachments were dropped would be
    /// built on by a client that believes they are there (ADR-008 §2).
    /// </summary>
    /// <param name="layer">The layer's definition.</param>
    /// <param name="fallbackName">The name a lone unnamed layer takes, or empty.</param>
    /// <param name="serviceName">The service it is designed into.</param>
    /// <param name="serviceSrid">The spatial reference a layer that names none is stored in, or null for Web Mercator.</param>
    /// <param name="design">The design, when this returns true.</param>
    /// <param name="error">Why not, when it returns false.</param>
    /// <returns>Whether the layer can be designed.</returns>
    internal static bool TryReadLayerDesign(
        JsonElement layer, string fallbackName, string serviceName, int? serviceSrid,
        out HostedDataEndpoints.LayerDesign? design, out string? error)
    {
        design = null;
        error = null;

        if (layer.ValueKind != JsonValueKind.Object)
        {
            error = "Every layer is a JSON object.";
            return false;
        }

        string layerName = layer.TryGetProperty("name", out JsonElement ln) && ln.ValueKind == JsonValueKind.String
            && ln.GetString() is { Length: > 0 } given
                ? given.Trim()
                : fallbackName;

        if (layerName.Length == 0)
        {
            error = "Every layer has a name of its own.";
            return false;
        }

        foreach (JsonProperty property in layer.EnumerateObject())
        {
            if (DesignedLayerKeys.Contains(property.Name) || DescriptiveLayerKeys.Contains(property.Name)
                || property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (property.Name.Equals("hasAttachments", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                error = $"Layer '{layerName}' asks for attachments, which are turned on after it exists — on its Settings page in "
                    + "Studio. Nothing was created: send it with hasAttachments false.";
                return false;
            }

            error = property.Name.Equals("drawingInfo", StringComparison.OrdinalIgnoreCase)
                ? $"Layer '{layerName}' carries a drawingInfo, which this server does not take from a definition: a layer's "
                  + "drawing is its style, set on its Style page in Studio. Nothing was created: send it without one."
                : $"Layer '{layerName}' carries '{property.Name}', which this server does not make from a definition. "
                  + "Nothing was created: a layer is designed here from its name, geometryType, fields and spatial reference.";
            return false;
        }

        string? geometry = layer.TryGetProperty("geometryType", out JsonElement g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
        bool table = geometry is null
            || (layer.TryGetProperty("type", out JsonElement t) && string.Equals(t.GetString(), "Table", StringComparison.OrdinalIgnoreCase));
        string designed;

        if (table)
        {
            designed = "Table";
        }
        else if (!DesignedGeometry.TryGetValue(geometry!, out designed!))
        {
            error = $"Layer '{layerName}' has geometryType '{geometry}'; a hosted layer is one of "
                + string.Join(", ", DesignedGeometry.Keys) + ", or none for a table.";
            return false;
        }
        else
        {
            bool z = layer.TryGetProperty("hasZ", out JsonElement hz) && hz.ValueKind == JsonValueKind.True;
            bool m = layer.TryGetProperty("hasM", out JsonElement hm) && hm.ValueKind == JsonValueKind.True;
            designed += (z ? "Z" : string.Empty) + (m ? "M" : string.Empty);
        }

        JsonElement[] described = layer.TryGetProperty("fields", out JsonElement f) && f.ValueKind == JsonValueKind.Array
            ? [.. f.EnumerateArray().Where(field => !(field.TryGetProperty("type", out JsonElement ft)
                && MadeByTheTable.Contains(ft.GetString() ?? string.Empty)))]
            : [];

        List<HostedDataEndpoints.FieldDesign> fields = [];

        if (described.Length > 0
            && !ArcGisAdminEndpoints.TryDefinition(
                JsonSerializer.Serialize(new { fields = described }), adding: true, out fields, out string? fieldError))
        {
            error = $"Layer '{layerName}': {fieldError}";
            return false;
        }

        int? srid = SridOf(layer, "spatialReference")
            ?? (layer.TryGetProperty("extent", out JsonElement extent) && extent.ValueKind == JsonValueKind.Object
                ? SridOf(extent, "spatialReference")
                : null)
            ?? serviceSrid;

        design = new HostedDataEndpoints.LayerDesign(layerName, designed, fields, null, serviceName, Srid: table ? null : srid);
        return true;
    }

    /// <summary>The EPSG code a <c>spatialReference</c> property names, 102100 read as 3857; or null.</summary>
    internal static int? SridOf(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(property, out JsonElement sr) && sr.ValueKind == JsonValueKind.Object
        && (sr.TryGetProperty("latestWkid", out JsonElement w) || sr.TryGetProperty("wkid", out w))
        && w.TryGetInt32(out int wkid) && wkid > 0
            ? ArcGisGeometryReader.Canonical(wkid)
            : null;

    /// <summary>
    /// A service name the portal may create empty: the rule Studio's views keep, which the ArcGIS API for Python's own
    /// check (letters, digits and underscores) falls inside.
    /// </summary>
    private static readonly Regex EmptyServiceName = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Portal's <c>createService</c> — ADR-257: a service with its layers named in <c>createParameters.layers</c>
    /// (and <c>tables</c>), each one designed by Studio's <c>POST /admin/hosted/define</c>; or, with
    /// <c>isView=true</c>, a view of the source the layers' <c>adminLayerInfo.viewLayerDefinition</c> names, made by
    /// Studio's view. <b>Without layers, since 2026-11-02 (ADR-257 §11, owner decision)</b>, an empty hosted service —
    /// the shape the ArcGIS API for Python's <c>create_service</c> and <c>create_view</c> send, filled next by the
    /// service's <c>addToDefinition</c>.
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

        IResult result = layers.Count == 0
            ? await CreateEmptyServiceAsync(context, name, create, view, current, cancellation).ConfigureAwait(false)
            : view
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
    /// An empty hosted service and its item — ADR-257 §11. What <c>createParameters</c> says about the service and this
    /// server keeps is kept through the writers Studio uses: <c>serviceDescription</c>, <c>copyrightText</c>, and the
    /// editing <c>capabilities</c> offer. A view asked for this way is an empty service until its
    /// <c>addToDefinition</c> names the source, and is made a view of it then; its capabilities become its ceiling now,
    /// as Studio's view sets them.
    /// </summary>
    /// <returns>The service's name as a value result, or the refusal.</returns>
    private static async Task<IResult> CreateEmptyServiceAsync(
        HttpContext context, string name, JsonElement create, bool view, RequestPrincipal current, CancellationToken cancellation)
    {
        if (!EmptyServiceName.IsMatch(name))
        {
            return PortalError(400,
                $"'{name}' is not a service name here: letters, digits, '_' and '-', starting with a letter or digit, at most 40.");
        }

        IServiceProvider services = context.RequestServices;
        IAdminCatalog catalog = services.GetRequiredService<IAdminCatalog>();

        // A layer of that name would make the admin pages that find a layer by its name ambiguous, as Studio's view refuses.
        if (await HostedDataEndpoints.NameTakenAsync(catalog, name, cancellation).ConfigureAwait(false)
            || await catalog.FindServiceAtAsync(FeatureServerMetadataWriter.HostedFolder, name, cancellation).ConfigureAwait(false) is not null)
        {
            return PortalError(409, $"A service or layer named '{name}' already exists in hosted.");
        }

        string? capabilities = create.TryGetProperty("capabilities", out JsonElement c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        HashSet<string> asked = new(
            (capabilities ?? "Query").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        if (asked.Remove("Editing") && !asked.Overlaps(["Create", "Update", "Delete"]))
        {
            asked.UnionWith(["Create", "Update", "Delete"]);
        }

        // Sync, ChangeTracking and the rest are not served (ADR-082); the stock client asks for them by default only on
        // ArcGIS Online, so they are left out of the offer rather than refused, and the service document says what is.
        string[] offered = [.. AdminEndpoints.OwnerOperations.Where(op => asked.Contains(op))];

        if (await catalog.CreateEmptyServiceAsync(name, FeatureServerMetadataWriter.HostedFolder, current.Principal.Id, cancellation)
            .ConfigureAwait(false) is null)
        {
            return PortalError(409, $"A service named '{name}' already exists in hosted.");
        }

        string folder = FeatureServerMetadataWriter.HostedFolder;

        if (view)
        {
            // The ceiling is what was asked, as Studio's view sets it: an add-only view offers no Query (ADR-113 §5.3).
            string[] ceiling = [.. ((string[])["Query", "Create", "Update", "Delete"]).Where(asked.Contains)];
            ceiling = ceiling.Length == 0 ? ["Query"] : ceiling;
            await catalog.SetServiceCapabilitiesAsync(name, folder, new ServiceCapabilityLimits(null, null, ceiling, null), cancellation)
                .ConfigureAwait(false);
        }

        await catalog.SetEditingOfferedAsync(name, folder, offered, cancellation).ConfigureAwait(false);

        string? Text(string key) => create.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } said
                ? said
                : null;

        if (Text("serviceDescription") is { } description)
        {
            await catalog.SetServiceDescriptionAsync(name, folder, description, cancellation).ConfigureAwait(false);
        }

        if (Text("copyrightText") is { } credits)
        {
            await catalog.SetServiceCreditsAsync(name, folder, credits, cancellation).ConfigureAwait(false);
        }

        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "service.create.empty", name,
            AdminEndpoints.Detail(new { folder, view, offered }), succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(name);
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
        IAdminCatalog catalog = context.RequestServices.GetRequiredService<IAdminCatalog>();

        if (await catalog.FindServiceAtAsync(FeatureServerMetadataWriter.HostedFolder, name, cancellation).ConfigureAwait(false) is not null)
        {
            return PortalError(409, $"A service named '{name}' already exists in hosted.");
        }

        (int status, string? message, _) = await DefineLayersAsync(
            context, name, SridOf(create, "spatialReference"), layers, lone: layers.Count == 1 ? name : string.Empty, cancellation)
            .ConfigureAwait(false);

        return status is >= 200 and < 300 ? Results.Ok(name) : PortalError(status, message!);
    }

    /// <summary>
    /// ArcGIS layer definitions designed into one hosted service through Studio's <c>POST /admin/hosted/define</c> — the
    /// one-request <c>createService</c> and a service's <c>addToDefinition</c> (ADR-257 §5.5, §11). Every layer is read
    /// and its name checked before the first is made, so a refusal leaves nothing behind; a failure past that leaves the
    /// layers before it, and the message names them.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="serviceName">The service, in hosted.</param>
    /// <param name="serviceSrid">The spatial reference a layer naming none is stored in, or null.</param>
    /// <param name="layers">The definitions.</param>
    /// <param name="lone">The name a lone unnamed layer takes, or empty.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A 2xx status and the names made, or the refusal's status and sentence.</returns>
    internal static async Task<(int Status, string? Message, IReadOnlyList<string> Made)> DefineLayersAsync(
        HttpContext context, string serviceName, int? serviceSrid, IReadOnlyList<JsonElement> layers, string lone,
        CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        IAdminCatalog catalog = services.GetRequiredService<IAdminCatalog>();
        List<HostedDataEndpoints.LayerDesign> designs = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (JsonElement layer in layers)
        {
            if (!TryReadLayerDesign(layer, lone, serviceName, serviceSrid, out HostedDataEndpoints.LayerDesign? design, out string? error))
            {
                return (400, error, []);
            }

            if (!names.Add(design!.Name!))
            {
                return (400, $"Two layers are named '{design.Name}'; every layer has a name of its own.", []);
            }

            if (await HostedDataEndpoints.NameTakenAsync(catalog, design.Name!, cancellation).ConfigureAwait(false))
            {
                return (409, $"A layer named '{design.Name}' already exists. Nothing was created.", []);
            }

            designs.Add(design);
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
                return (status, (message ?? $"Layer '{design.Name}' was not created.")
                    + (made.Count > 0 ? $" Already made in '{serviceName}': {string.Join(", ", made)}." : string.Empty), made);
            }

            made.Add(design.Name!);
        }

        return (200, null, made);
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

    /// <summary>A Studio handler run inside a request that is not its own: its status and refusal, for a door (ADR-257 §11).</summary>
    internal static Task<(int Status, string? Message)> InnerStatusAsync(HttpContext context, Func<Task> handler) =>
        InnerAsync(context, handler);
}
