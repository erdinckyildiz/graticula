using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;

namespace Graticula.Platform.Catalog;

/// <summary>
/// A document — ADR-188: a file somebody brought, kept as an item with an owner and a sharing scope.
/// </summary>
/// <param name="Id">32 lower-case hexadecimal characters, an ArcGIS item id's shape, as a web map's is.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Snippet">A line saying what it is for, or null.</param>
/// <param name="Owner">Who owns it.</param>
/// <param name="OwnerName">The owner's account name.</param>
/// <param name="Sharing">Its scope.</param>
/// <param name="FileName">The name it was uploaded under, and is downloaded under.</param>
/// <param name="Kind">What this server decided the file is.</param>
/// <param name="Size">Its length in bytes.</param>
/// <param name="Created">When it was first uploaded.</param>
/// <param name="Modified">When its file or its description last changed.</param>
/// <param name="SharedWith">The groups it is shared with.</param>
/// <param name="SharedWithNames">The same groups, by name.</param>
/// <param name="DeleteProtected">Whether a delete is refused until its owner turns this off.</param>
/// <param name="Tags">The words it is found by.</param>
/// <param name="ContentFolder">The owner's content folder it is in, or null for the root.</param>
/// <param name="Description">What it is about at length, or null.</param>
/// <param name="HasThumbnail">Whether its owner gave it a picture — D-321, migration 121.</param>
/// <param name="Url">An app's or a link's address (ADR-264), or null.</param>
/// <param name="ContentFile">
/// Where a scene layer package above the document limit is, relative to the server directories; null for every
/// document whose bytes are in the store (ADR-274).
/// </param>
/// <param name="TypeKeywords">An app's or a link's type keywords (ADR-264), or null for its kind's own.</param>
public sealed record Document(
    string Id,
    string Title,
    string? Snippet,
    Guid Owner,
    string OwnerName,
    SharingScope Sharing,
    string FileName,
    DocumentKind Kind,
    long Size,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    IReadOnlyList<Guid>? SharedWith = null,
    IReadOnlyList<string>? SharedWithNames = null,
    bool DeleteProtected = false,
    IReadOnlyList<string>? Tags = null,
    Guid? ContentFolder = null,
    string? Description = null,
    bool HasThumbnail = false,
    string? Url = null,
    IReadOnlyList<string>? TypeKeywords = null,
    string? ContentFile = null);

/// <summary>What a document is, decided by this server from its first bytes and its name — ADR-188 §5.3.</summary>
/// <param name="Name">The short name stored with it: <c>pdf</c>, <c>xlsx</c> and so on.</param>
/// <param name="MediaType">The content type it is served with.</param>
/// <param name="ArcGisType">The ArcGIS item type a portal client is told.</param>
/// <param name="Extension">The extension a download is named with.</param>
public sealed record DocumentKind(string Name, string MediaType, string ArcGisType, string Extension);

/// <summary>The rules a document is held to, in one place for the store and the endpoints.</summary>
public static class Documents
{
    /// <summary>The largest file accepted unless a deployment says otherwise: 64 MB (ADR-188 §5.6).</summary>
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;

    /// <summary>The longest file name kept.</summary>
    public const int MaximumFileNameLength = 200;

    private static readonly DocumentKind Pdf = new("pdf", "application/pdf", "PDF", ".pdf");
    private static readonly DocumentKind Word = new(
        "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Microsoft Word", ".docx");
    private static readonly DocumentKind Excel = new(
        "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Microsoft Excel", ".xlsx");
    private static readonly DocumentKind PowerPoint = new(
        "pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", "Microsoft Powerpoint", ".pptx");
    private static readonly DocumentKind Csv = new("csv", "text/csv", "CSV", ".csv");
    private static readonly DocumentKind Png = new("png", "image/png", "Image", ".png");
    private static readonly DocumentKind Jpeg = new("jpeg", "image/jpeg", "Image", ".jpg");
    private static readonly DocumentKind Gif = new("gif", "image/gif", "Image", ".gif");
    private static readonly DocumentKind WebP = new("webp", "image/webp", "Image", ".webp");

    // ADR-257: the data files a portal client adds as items before it publishes them, kept as documents as a CSV is.
    private static readonly DocumentKind Shapefile = new("shapefile", "application/zip", "Shapefile", ".zip");
    private static readonly DocumentKind FileGeodatabase = new("filegdb", "application/zip", "File Geodatabase", ".zip");
    private static readonly DocumentKind GeoJson = new("geojson", "application/geo+json", "GeoJson", ".geojson");

    // ADR-263: what a layer's export becomes when the portal's export makes it an item — ADR-106's packaging, by its name.
    private static readonly DocumentKind GeoPackage = new("geopackage", "application/geopackage+sqlite3", "GeoPackage", ".gpkg");
    private static readonly DocumentKind Kml = new("kml", "application/vnd.google-earth.kml+xml", "KML", ".kml");
    private static readonly DocumentKind CsvCollection = new("csvcollection", "application/zip", "CSV Collection", ".zip");
    private static readonly DocumentKind FeatureCollection = new("featurecollection", "application/json", "Feature Collection", ".json");

    // ADR-264: an app or a link is a document whose content is its JSON and which may carry a URL.
    private static readonly DocumentKind Dashboard = new("dashboard", "application/json", "Dashboard", ".json");
    private static readonly DocumentKind WebExperience = new("webexperience", "application/json", "Web Experience", ".json");
    private static readonly DocumentKind WebMappingApplication = new("webmappingapplication", "application/json", "Web Mapping Application", ".json");
    private static readonly DocumentKind Form = new("form", "application/json", "Form", ".json");
    private static readonly DocumentKind StoryMap = new("storymap", "application/json", "StoryMap", ".json");

    // ADR-269: a scene layer package — a zip of I3S resources, Pro's or this server's own — kept as a file and served as
    // a SceneServer from it. Listed as the scene service it serves; Pro adds it as a "Scene Package" (ScenePackageType).
    private static readonly DocumentKind ScenePackage = new("slpk", "application/octet-stream", "Scene Service", ".slpk");

    /// <summary>The item type Pro and the portal API add a scene layer package as (ADR-269).</summary>
    public const string ScenePackageType = "Scene Package";

    /// <summary>Whether a kind is a scene layer package, served as a SceneServer (ADR-269).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for a package.</returns>
    public static bool IsScenePackage(DocumentKind kind) => kind == ScenePackage;

    // ADR-269: a web scene is the 3D twin of a web map, a JSON document the Maps SDK and Pro read and write as an item.
    private static readonly DocumentKind WebScene = new("webscene", "application/json", "Web Scene", ".json");
    private static readonly DocumentKind FeatureServiceLink = new("featureservicelink", "application/json", "Feature Service", ".json");
    private static readonly DocumentKind MapServiceLink = new("mapservicelink", "application/json", "Map Service", ".json");
    private static readonly DocumentKind VectorTileServiceLink = new("vectortileservicelink", "application/json", "Vector Tile Service", ".json");
    private static readonly DocumentKind ImageServiceLink = new("imageservicelink", "application/json", "Image Service", ".json");
    private static readonly DocumentKind DocumentLink = new("documentlink", "application/json", "Document Link", ".json");

    /// <summary>The largest picture a document keeps as its thumbnail: 1 MB, ArcGIS's limit on an item's.</summary>
    public const int MaximumThumbnailBytes = 1 << 20;

    /// <summary>
    /// What a thumbnail is, from its first bytes alone — a PNG, JPEG, GIF or WebP picture — or null. D-321.
    /// </summary>
    /// <remarks>
    /// <b>By signature only.</b> A thumbnail has no name anybody chose, and the bytes are served back with the type this
    /// decides, so a file that is not a picture is never sent to a browser as one.
    /// </remarks>
    /// <param name="head">Its first bytes.</param>
    /// <returns>The kind, or null when it is not one of the four.</returns>
    public static DocumentKind? RecognisePicture(ReadOnlySpan<byte> head) =>
        head.StartsWith(PngSignature) ? Png
        : head.StartsWith(JpegSignature) ? Jpeg
        : head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8) ? Gif
        : head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8) ? WebP
        : null;

    /// <summary>Every kind accepted, by its stored name.</summary>
    public static IReadOnlyList<DocumentKind> Accepted { get; } = [Pdf, Word, Excel, PowerPoint, Csv, Png, Jpeg, Gif, WebP, ScenePackage];

    /// <summary>
    /// The data kinds kept only when the upload names its item type (ADR-257): a zipped shapefile, a zipped file
    /// geodatabase and GeoJSON, which the portal's <c>addItem</c> declares and Studio's own picker does not offer.
    /// </summary>
    public static IReadOnlyList<DocumentKind> Data { get; } = [Shapefile, FileGeodatabase, GeoJson];

    /// <summary>The kinds an export's file is kept as when the portal's export makes it an item (ADR-263): not uploaded.</summary>
    public static IReadOnlyList<DocumentKind> Exports { get; } = [GeoPackage, Kml, CsvCollection, FeatureCollection];

    /// <summary>
    /// The kinds a portal client adds with a <c>text</c> or a <c>url</c> rather than a file (ADR-264): apps, and services
    /// and documents elsewhere, registered by their address.
    /// </summary>
    public static IReadOnlyList<DocumentKind> Apps { get; } =
        [Dashboard, WebExperience, WebMappingApplication, Form, StoryMap, WebScene, FeatureServiceLink, MapServiceLink,
         VectorTileServiceLink, ImageServiceLink, DocumentLink];

    /// <summary>The app or link kind an ArcGIS item type names, or null for one that is not (ADR-264).</summary>
    /// <param name="arcGisType">The item type as a portal client sends it.</param>
    /// <returns>The kind, or null.</returns>
    public static DocumentKind? AppOfType(string? arcGisType) =>
        Apps.FirstOrDefault(kind => string.Equals(kind.ArcGisType, arcGisType?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a kind is an app or a link — content that is its JSON, kept without a file of its own (ADR-264).</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for an app or a link.</returns>
    public static bool IsApp(DocumentKind kind) => Apps.Contains(kind);

    /// <summary>
    /// Whether a kind is a link — a service or a document elsewhere, registered by its address — rather than an app whose
    /// item may also carry the address it launches at (ADR-264's review).
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for a link.</returns>
    public static bool IsLink(DocumentKind kind) =>
        kind == FeatureServiceLink || kind == MapServiceLink || kind == VectorTileServiceLink || kind == ImageServiceLink
        || kind == DocumentLink;

    /// <summary>The kind stored under a name, or null for a name this build does not know.</summary>
    /// <param name="name">The stored name.</param>
    /// <returns>The kind.</returns>
    public static DocumentKind? Named(string? name)
    {
        foreach (DocumentKind kind in Accepted.Concat(Data).Concat(Exports).Concat(Apps))
        {
            if (string.Equals(kind.Name, name, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>
    /// Decides what a file is from its first bytes and its extension together, or why it is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both, never the client's content type.</b> The signature says what the bytes are; the extension says what
    /// the person meant. A PDF named <c>.xlsx</c> is refused rather than guessed at, because a file that is not what
    /// it says is the shape an attack takes and an honest mistake is cheap to correct.
    /// </para>
    /// <para>
    /// <b>CSV is the one kind without a signature</b>, so it is accepted only when the head is valid
    /// UTF-8 with no NUL and does not open like markup — a file beginning <c>&lt;</c> is HTML, SVG or XML whatever its
    /// name says, and every one of those is something a browser may run (ADR-188 §5.3).
    /// </para>
    /// <para>
    /// <b>A declared type is believed only where the bytes agree (ADR-257).</b> A <c>.zip</c> is a shapefile or a file
    /// geodatabase only when it was added as one, and what is inside it is the caller's to check, since this sees only
    /// the head. GeoJSON is plain text opening with <c>{</c>, named <c>.geojson</c>, or <c>.json</c> when added as
    /// GeoJson. Without a declared type a <c>.zip</c> is refused as it always was.
    /// </para>
    /// </remarks>
    /// <param name="fileName">The name it was uploaded under.</param>
    /// <param name="head">Its first bytes — at least the first 512, or all of a shorter file.</param>
    /// <param name="refusal">Why not, in a sentence for the person who uploaded it.</param>
    /// <param name="declaredType">The ArcGIS item type the upload names (<c>Shapefile</c>, <c>File Geodatabase</c>,
    /// <c>GeoJson</c> and so on), or null.</param>
    /// <returns>The kind, or null.</returns>
    public static DocumentKind? Recognise(string fileName, ReadOnlySpan<byte> head, out string? refusal, string? declaredType = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        refusal = null;
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        string declared = (declaredType ?? string.Empty).Trim();

        // ADR-269: a scene layer package is a zip named .slpk, whatever type it was added as.
        if (head.StartsWith(ZipSignature) && extension == ".slpk")
        {
            return ScenePackage;
        }

        if (head.StartsWith(ZipSignature) && extension == ".zip")
        {
            if (declared.Equals(Shapefile.ArcGisType, StringComparison.OrdinalIgnoreCase))
            {
                return Shapefile;
            }

            if (declared.Equals(FileGeodatabase.ArcGisType, StringComparison.OrdinalIgnoreCase))
            {
                return FileGeodatabase;
            }

            refusal = "A '.zip' is kept only as a Shapefile or a File Geodatabase item; add it with that type. "
                + "Nothing was kept.";
            return null;
        }

        if (extension == ".geojson" || (extension == ".json" && declared.Equals(GeoJson.ArcGisType, StringComparison.OrdinalIgnoreCase)))
        {
            if (!LooksLikePlainText(head) || FirstNonSpace(head) != (byte)'{')
            {
                refusal = $"'{fileName}' is named as GeoJSON but is not a JSON object in plain UTF-8 text.";
                return null;
            }

            return GeoJson;
        }

        DocumentKind? bySignature =
            head.StartsWith("%PDF-"u8) ? Pdf
            : head.StartsWith(PngSignature) ? Png
            : head.StartsWith(JpegSignature) ? Jpeg
            : head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8) ? Gif
            : head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8) ? WebP
            : head.StartsWith(ZipSignature) ? OfficeByExtension(extension)
            : null;

        if (bySignature is not null)
        {
            if (!Matches(bySignature, extension))
            {
                refusal = $"The file is a {bySignature.ArcGisType} but is named '{extension}'. Rename it to "
                    + $"'{bySignature.Extension}' and upload it again.";
                return null;
            }

            return bySignature;
        }

        if (extension == ".csv")
        {
            if (!LooksLikePlainText(head))
            {
                refusal = "A '.csv' file must be plain UTF-8 text, and this one is not — or it opens like HTML or "
                    + "XML, which is not accepted as a document.";
                return null;
            }

            return Csv;
        }

        // <b>A name this server accepts on bytes it does not</b> — a design review read "'.pdf' is none of these" beside a
        // list that names PDF, which says the opposite of what happened. The name is right; the inside is not.
        if (Accepted.FirstOrDefault(kind => Matches(kind, extension)) is { } named)
        {
            refusal = $"'{fileName}' is named '{extension}' but is not a {named.ArcGisType} inside. It may be damaged, or "
                + "another kind of file renamed.";
            return null;
        }

        refusal = "A document may be a PDF, a Word, Excel or PowerPoint file, a CSV file, or a PNG, JPEG, GIF "
            + $"or WebP picture. '{(extension.Length == 0 ? fileName : extension)}' is none of these.";
        return null;
    }

    /// <summary>A name a download can carry: the path removed, control characters and quotes taken out, bounded.</summary>
    /// <param name="fileName">The name as uploaded.</param>
    /// <param name="kind">What it is, for its extension when the name has lost it.</param>
    /// <returns>The name to keep.</returns>
    public static string CleanFileName(string? fileName, DocumentKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);

        string bare = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        StringBuilder kept = new(bare.Length);

        foreach (char c in bare)
        {
            if (!char.IsControl(c) && c is not ('"' or '/' or '\\'))
            {
                kept.Append(c);
            }
        }

        string name = kept.ToString().Trim();

        if (name.Length == 0)
        {
            name = "document" + kind.Extension;
        }

        return name.Length <= MaximumFileNameLength ? name : name[..(MaximumFileNameLength - kind.Extension.Length)] + kind.Extension;
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

    private static DocumentKind? OfficeByExtension(string extension) => extension switch
    {
        ".docx" => Word,
        ".xlsx" => Excel,
        ".pptx" => PowerPoint,
        _ => null,
    };

    private static bool Matches(DocumentKind kind, string extension) =>
        extension == kind.Extension || (kind == Jpeg && extension == ".jpeg");

    /// <summary>The first byte of <paramref name="head"/> past a byte-order mark and white space, or 0.</summary>
    private static byte FirstNonSpace(ReadOnlySpan<byte> head)
    {
        ReadOnlySpan<byte> body = head.StartsWith(ByteOrderMark) ? head[3..] : head;

        foreach (byte b in body)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return b;
            }
        }

        return 0;
    }

    private static bool LooksLikePlainText(ReadOnlySpan<byte> head)
    {
        if (head.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        // A byte-order mark is allowed, then the first character that is not white space must not open markup.
        ReadOnlySpan<byte> body = head.StartsWith(ByteOrderMark) ? head[3..] : head;
        int first = 0;

        while (first < body.Length && body[first] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            first++;
        }

        if (first < body.Length && body[first] == (byte)'<')
        {
            return false;
        }

        // The head may end inside a multi-byte character; up to three trailing bytes are left for the decoder.
        for (int trim = 0; trim <= Math.Min(3, body.Length); trim++)
        {
            try
            {
                _ = new UTF8Encoding(false, true).GetCharCount(body[..^trim]);
                return true;
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return false;
    }
}

/// <summary>
/// Where documents are kept — ADR-188, migration 89: the item, and the bytes beside it in the platform store.
/// </summary>
/// <remarks>
/// <b>No authorisation here</b>, as <see cref="IWebMapStore"/> has none: who may read or change a document is
/// <see cref="LayerAccess"/>'s decision, made by the caller that knows who is asking.
/// </remarks>
public interface IDocumentStore
{
    /// <summary>Every document, without its bytes, newest change first.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The documents.</returns>
    Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One document, without its bytes, or null.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The document.</returns>
    Task<Document?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>A document's bytes, or null when there is no such document.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The bytes.</returns>
    Task<byte[]?> ContentAsync(string id, CancellationToken cancellationToken);

    /// <summary>Keeps a new document under a fresh id.</summary>
    /// <param name="title">Its title.</param>
    /// <param name="snippet">Its snippet, or null.</param>
    /// <param name="owner">Its owner.</param>
    /// <param name="sharing">Its scope.</param>
    /// <param name="fileName">The name it is downloaded under, already cleaned.</param>
    /// <param name="kind">What it is, already decided.</param>
    /// <param name="content">Its bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The document as stored.</returns>
    Task<Document> CreateAsync(
        string title,
        string? snippet,
        Guid owner,
        SharingScope sharing,
        string fileName,
        DocumentKind kind,
        byte[] content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Keeps a new document whose bytes are a file in the server directories rather than in the store — a scene layer
    /// package above the document limit (ADR-274). The file is complete and named by its hash before this is called.
    /// </summary>
    Task<Document> CreateFileAsync(
        string title,
        string? snippet,
        Guid owner,
        SharingScope sharing,
        string fileName,
        DocumentKind kind,
        long size,
        string contentFile,
        string sha256,
        CancellationToken cancellationToken);

    /// <summary>Every file the store's rows name (ADR-274), relative to the server directories, for the sweep.</summary>
    Task<IReadOnlySet<string>> ContentFilesAsync(CancellationToken cancellationToken);

    /// <summary>Replaces a document's title, snippet and scope; null when there is no such document.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="title">The new title.</param>
    /// <param name="snippet">The new snippet, or null.</param>
    /// <param name="sharing">The new scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The document as stored.</returns>
    Task<Document?> UpdateAsync(string id, string title, string? snippet, SharingScope sharing, CancellationToken cancellationToken);

    /// <summary>Replaces a document's file, keeping everything else; false when there is no such document.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="fileName">The new name, already cleaned.</param>
    /// <param name="kind">What the new file is.</param>
    /// <param name="content">Its bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the document exists.</returns>
    Task<bool> ReplaceAsync(string id, string fileName, DocumentKind kind, byte[] content, CancellationToken cancellationToken);

    /// <summary>Replaces a document's tags.</summary>
    /// <param name="id">The document.</param>
    /// <param name="tags">The tags, already normalised.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the document exists.</returns>
    Task<bool> SetTagsAsync(string id, IReadOnlyList<string> tags, CancellationToken cancellationToken);

    /// <summary>Replaces an app's or a link's URL and type keywords (ADR-264); false when there is no such document.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="url">The address, or null for none.</param>
    /// <param name="typeKeywords">The type keywords, or null for its kind's own.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether there was such a document.</returns>
    Task<bool> SetLinkAsync(string id, string? url, IReadOnlyList<string>? typeKeywords, CancellationToken cancellationToken);

    /// <summary>The export job an item is waiting for (ADR-263, D-384), or null when it waits for none.</summary>
    /// <param name="id">The document.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The job, or null.</returns>
    Task<Guid?> ExportJobAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Names the export job an item waits for, or, with <paramref name="job"/> null, says it waits no longer — only when it
    /// is still waiting for <paramref name="expected"/>, so two nodes finishing the same export clear it once.
    /// </summary>
    /// <param name="id">The document.</param>
    /// <param name="job">The job, or null.</param>
    /// <param name="expected">The job it must be waiting for now, or null to set it whatever it waits for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the document was changed.</returns>
    Task<bool> SetExportJobAsync(string id, Guid? job, Guid? expected, CancellationToken cancellationToken);

    /// <summary>Replaces a document's description.</summary>
    /// <param name="id">The document.</param>
    /// <param name="description">The description, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the document exists.</returns>
    Task<bool> SetDescriptionAsync(string id, string? description, CancellationToken cancellationToken);

    /// <summary>Protects a document from deletion, or stops protecting it.</summary>
    /// <param name="id">The document.</param>
    /// <param name="protectedFromDeletion">On or off.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the document exists.</returns>
    Task<bool> SetDeleteProtectedAsync(string id, bool protectedFromDeletion, CancellationToken cancellationToken);

    /// <summary>Gives one document to another member; its scope is untouched and it leaves its folder.</summary>
    /// <param name="id">The document.</param>
    /// <param name="receiver">The member who receives it, by name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    Task<OwnerChange> ChangeOwnerAsync(string id, string receiver, CancellationToken cancellationToken);

    /// <summary>Keeps a picture as a document's thumbnail, or forgets it with null — D-321.</summary>
    /// <param name="id">The document.</param>
    /// <param name="picture">The picture's bytes, already recognised and bounded, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the document exists.</returns>
    Task<bool> SetThumbnailAsync(string id, byte[]? picture, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps a picture this server drew from the document's first page (ADR-268), or with <paramref name="picture"/> null
    /// removes one it drew — and changes nothing when the document holds a picture its owner gave.
    /// </summary>
    /// <param name="id">The document.</param>
    /// <param name="picture">The PNG, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the document's picture changed.</returns>
    Task<bool> SetDrawnThumbnailAsync(string id, byte[]? picture, CancellationToken cancellationToken);

    /// <summary>A document's thumbnail, or null when it has none or there is no such document.</summary>
    /// <param name="id">The document.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The picture's bytes.</returns>
    Task<byte[]?> ThumbnailAsync(string id, CancellationToken cancellationToken);

    /// <summary>Removes a document; false when there was none.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it went.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}
