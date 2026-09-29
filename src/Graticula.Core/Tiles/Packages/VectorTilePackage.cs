using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Tiles.Packages;

/// <summary>
/// Writes an ArcGIS vector tile package — a <c>.vtpk</c> — as a zip of its documents and compact cache bundles —
/// ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only partly specified in public, and the ADR says which part is which.</b> The tile store is Esri's published
/// compact cache V2 (<see cref="CompactCacheBundle"/>); that a tile package is a zip whose tiles sit in
/// <c>tile/L&lt;nn&gt;/</c> folders of bundles beside a <c>root.json</c> service document and an item description is
/// Esri's <c>Esri/tile-package-spec</c> (written for the raster <c>.tpkx</c>). The vector package's own layout — the
/// <c>p12/</c> folder, <c>resources/styles/root.json</c>, <c>resources/fonts</c>, <c>resources/sprites</c>,
/// <c>resources/info/root.json</c>, and <c>esriinfo/iteminfo.xml</c> with <c>item.pkinfo</c> — is <b>INFERRED</b>
/// from the vector tile service's documented resources (a package's <c>p12</c> folder is that service's
/// <c>VectorTileServer/</c> tree, file for file) and from what published descriptions of the format say; no
/// Esri document read for this states it whole.
/// </para>
/// <para>
/// <b>Every entry is stored, not deflated.</b> The tiles in a bundle are already gzip-compressed, so deflating a
/// bundle again gains nothing, and a reader that maps a bundle and seeks into it by its index needs the bytes as
/// they are. <b>INFERRED</b> for the documents too: storing them costs a few kilobytes and removes a way for a
/// reader to disagree with us.
/// </para>
/// </remarks>
public sealed class VectorTilePackage : IDisposable
{
    /// <summary>The folder a package's service tree is in.</summary>
    public const string ServiceFolder = "p12/";

    /// <summary>The service document inside the package.</summary>
    public const string ServiceDocument = ServiceFolder + "root.json";

    /// <summary>The default style inside the package.</summary>
    public const string StyleDocument = ServiceFolder + "resources/styles/root.json";

    /// <summary>The list of the package's resources.</summary>
    public const string ResourceInfo = ServiceFolder + "resources/info/root.json";

    /// <summary>The item description.</summary>
    public const string ItemInfo = "esriinfo/iteminfo.xml";

    /// <summary>The package description.</summary>
    public const string PackageInfo = "esriinfo/item.pkinfo";

    private readonly ZipArchive _zip;

    /// <summary>Starts a package on a stream.</summary>
    /// <param name="output">Where the zip goes. It is left open.</param>
    public VectorTilePackage(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
    }

    /// <summary>Where a bundle goes in the package.</summary>
    /// <param name="level">The level.</param>
    /// <param name="originRow">The bundle's top-left row.</param>
    /// <param name="originColumn">The bundle's top-left column.</param>
    /// <returns>The entry's path.</returns>
    public static string BundlePath(int level, int originRow, int originColumn) =>
        ServiceFolder + "tile/" + CompactCacheBundle.LevelFolder(level) + "/"
        + CompactCacheBundle.FileName(originRow, originColumn);

    /// <summary>Where a resource of the service tree goes: <c>p12/resources/</c> and its path.</summary>
    /// <param name="relative">The path under <c>resources/</c>, e.g. <c>sprites/sprite.json</c>.</param>
    /// <returns>The entry's path.</returns>
    public static string ResourcePath(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);

        if (relative.Contains("..", StringComparison.Ordinal) || relative.StartsWith('/') || relative.Contains('\\'))
        {
            throw new ArgumentException($"'{relative}' is not a path inside the package's resources.", nameof(relative));
        }

        return ServiceFolder + "resources/" + relative;
    }

    /// <summary>Adds a document.</summary>
    /// <param name="path">Its path in the package.</param>
    /// <param name="bytes">Its bytes.</param>
    public void Add(string path, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using Stream entry = _zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
        entry.Write(bytes);
    }

    /// <summary>Adds one planned bundle, streaming its tiles in.</summary>
    /// <param name="level">The level.</param>
    /// <param name="layout">The bundle's plan.</param>
    /// <param name="read">Returns a tile's stored bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The bundle's length.</returns>
    public async Task<long> AddBundleAsync(
        int level,
        CompactCacheBundle.Layout layout,
        Func<BundleTile, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        await using Stream entry = _zip
            .CreateEntry(BundlePath(level, layout.OriginRow, layout.OriginColumn), CompressionLevel.NoCompression)
            .Open();

        return await CompactCacheBundle.WriteAsync(entry, layout, read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>esriinfo/item.pkinfo</c>: what the package is. <b>INFERRED</b> — the element names are those a package
    /// ArcGIS Pro writes is described as carrying; nothing public defines them.
    /// </summary>
    /// <param name="id">The package's identity.</param>
    /// <param name="name">Its name.</param>
    /// <param name="created">When it was made.</param>
    /// <returns>The document's bytes.</returns>
    public static byte[] PackageInfoDocument(Guid id, string name, DateTimeOffset created)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<pkinfo Culture=\"en-US\">\n"
            + $"  <ID>{id:D}</ID>\n"
            + $"  <name>{SecurityElement.Escape(name)}</name>\n"
            + "  <version>1.0</version>\n"
            + "  <size>-1</size>\n"
            + $"  <created>{created.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</created>\n"
            + "  <type>Vector Tile Package</type>\n"
            + "  <servable>false</servable>\n"
            + "  <packagelocation></packagelocation>\n"
            + "  <pkinfolocation></pkinfolocation>\n"
            + "</pkinfo>\n";

        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>
    /// <c>esriinfo/iteminfo.xml</c>: the item a portal would make of the package. <b>INFERRED</b> — the shape of
    /// an ArcGIS item description (<c>ESRI_ItemInformation</c>), with its extent in degrees.
    /// </summary>
    /// <param name="name">The item's name.</param>
    /// <param name="title">Its title.</param>
    /// <param name="description">A sentence about it.</param>
    /// <param name="west">The west edge, in degrees.</param>
    /// <param name="south">The south edge.</param>
    /// <param name="east">The east edge.</param>
    /// <param name="north">The north edge.</param>
    /// <returns>The document's bytes.</returns>
    public static byte[] ItemInfoDocument(
        string name, string title, string description, double west, double south, double east, double north)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<ESRI_ItemInformation Culture=\"en-US\">\n"
            + $"  <name>{SecurityElement.Escape(name)}</name>\n"
            + $"  <title>{SecurityElement.Escape(title)}</title>\n"
            + "  <type>Vector Tile Package</type>\n"
            + "  <typekeywords><typekeyword>Vector Tile Package</typekeyword><typekeyword>vtpk</typekeyword></typekeywords>\n"
            + $"  <description>{SecurityElement.Escape(description)}</description>\n"
            + "  <tags></tags>\n"
            + "  <snippet></snippet>\n"
            + $"  <extent><xmin>{N(west)}</xmin><ymin>{N(south)}</ymin><xmax>{N(east)}</xmax><ymax>{N(north)}</ymax></extent>\n"
            + "  <accessinformation></accessinformation>\n"
            + "  <licenseinfo></licenseinfo>\n"
            + "  <culture>en-US</culture>\n"
            + "</ESRI_ItemInformation>\n";

        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>Writes the zip's central directory.</summary>
    public void Dispose() => _zip.Dispose();
}
