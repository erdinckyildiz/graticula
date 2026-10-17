using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Graticula.Host;

/// <summary>What a service definition says about the service it describes — only what is read.</summary>
/// <param name="ServiceName">The service's name, from <c>serviceconfiguration.json</c>.</param>
/// <param name="FolderName">The server folder Pro chose; this server publishes into <c>hosted</c> regardless.</param>
/// <param name="Capabilities">The operations Pro asked for, comma-separated, or null.</param>
/// <param name="MaxRecordCount">The page size Pro asked for, or null.</param>
/// <param name="Layers">How many geodatabase files were taken out of the archive.</param>
/// <param name="DrawingInfos">
/// Each layer's ArcGIS <c>drawingInfo</c>, by the layer's name, from
/// <c>servicedescriptions/featureserver/featureserver.json</c> — the symbology Pro gave it, in the form a FeatureServer
/// layer publishes. Empty when the file is absent.
/// </param>
internal sealed record ServiceDefinition(
    string ServiceName, string? FolderName, string? Capabilities, int? MaxRecordCount, int Layers,
    IReadOnlyDictionary<string, string> DrawingInfos);

/// <summary>
/// Opens a service definition Pro uploaded and takes out its file geodatabase — ADR-191.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the file Pro sent, and only three parts of it</b> (ADR-191 condition 4). A service definition is a 7z
/// container; this reads <c>serviceconfiguration.json</c> for the service's name and settings,
/// <c>servicedescriptions/featureserver/featureserver.json</c> for each layer's <c>drawingInfo</c>, and copies the entries
/// under <c>cd/*.gdb/</c> — the data, a file geodatabase — into a zip the hosted-import pipeline already reads
/// (ADR-038). The map documents, metadata and thumbnail are not opened.
/// </para>
/// <para>
/// <b>Bounded before anything is trusted</b> (condition 3): an entry whose path is absolute or climbs out with
/// <c>..</c> is refused, as are more than <see cref="MaximumMembers"/> entries, a configuration larger than a
/// megabyte, and more than <see cref="MaximumBytes"/> of geodatabase once uncompressed — counted while copying, not
/// believed from the header. SharpCompress is the 7z reader (Tier 2, MIT); its types stay inside this class.
/// </para>
/// </remarks>
internal static class ServiceDefinitionArchive
{
    /// <summary>The most entries a service definition may hold.</summary>
    public const int MaximumMembers = 10_000;

    /// <summary>The most geodatabase bytes, uncompressed, one service definition may unpack to.</summary>
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;

    private const long MaximumConfigurationBytes = 1024 * 1024;

    /// <summary>The largest layer description read; a classification with pictures is far larger than a config.</summary>
    private const long MaximumDescriptionBytes = 32L * 1024 * 1024;

    /// <summary>Reads the service's settings and writes its geodatabase into <paramref name="zipPath"/>.</summary>
    /// <exception cref="InvalidDataException">The archive is not a service definition this server can publish.</exception>
    public static async Task<ServiceDefinition> ExtractAsync(string sdPath, string zipPath, CancellationToken cancellation)
    {
        using IArchive archive = ArchiveFactory.OpenArchive(sdPath, new ReaderOptions());

        if (archive.Type != ArchiveType.SevenZip)
        {
            throw new InvalidDataException("A service definition is a 7z archive, and this file is not one.");
        }

        List<IArchiveEntry> entries = [.. archive.Entries];

        if (entries.Count > MaximumMembers)
        {
            throw new InvalidDataException(
                $"The service definition holds {entries.Count:N0} entries; at most {MaximumMembers:N0} are opened.");
        }

        JsonElement? configuration = null;
        Dictionary<string, string> drawingInfos = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> geodatabases = new(StringComparer.OrdinalIgnoreCase);
        long written = 0;

        await using (FileStream target = File.Create(zipPath))
        using (ZipArchive zip = new(target, ZipArchiveMode.Create))
        {
            foreach (IArchiveEntry entry in entries.Where(e => !e.IsDirectory))
            {
                cancellation.ThrowIfCancellationRequested();
                string key = Normalised(entry.Key);

                if (string.Equals(key, "serviceconfiguration.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Size > MaximumConfigurationBytes)
                    {
                        throw new InvalidDataException("The service definition's configuration is larger than a megabyte.");
                    }

                    using Stream read = entry.OpenEntryStream();
                    using JsonDocument document = await JsonDocument.ParseAsync(read, cancellationToken: cancellation)
                        .ConfigureAwait(false);
                    configuration = document.RootElement.Clone();
                    continue;
                }

                if (string.Equals(key, "servicedescriptions/featureserver/featureserver.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Size > MaximumDescriptionBytes)
                    {
                        throw new InvalidDataException("The service definition's layer description is larger than 32 MB.");
                    }

                    using Stream read = entry.OpenEntryStream();
                    using JsonDocument document = await JsonDocument.ParseAsync(
                        read, new JsonDocumentOptions { MaxDepth = 256 }, cancellation).ConfigureAwait(false);
                    DrawingInfosOf(document.RootElement, drawingInfos);
                    continue;
                }

                // cd/<name>.gdb/<file>: the data. Kept with its .gdb folder, without the cd/ above it.
                string[] parts = key.Split('/');
                if (parts.Length < 3 || !string.Equals(parts[0], "cd", StringComparison.OrdinalIgnoreCase)
                    || !parts[1].EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                geodatabases.Add(parts[1]);
                ZipArchiveEntry copy = zip.CreateEntry(string.Join('/', parts.Skip(1)), CompressionLevel.Fastest);

                await using Stream to = copy.Open();
                using Stream from = entry.OpenEntryStream();
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await from.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    written += count;
                    if (written > MaximumBytes)
                    {
                        throw new InvalidDataException(
                            $"The service definition's geodatabase is larger than {MaximumBytes / (1024 * 1024 * 1024)} GB uncompressed.");
                    }

                    await to.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                }
            }
        }

        if (configuration is not { } config || !config.TryGetProperty("service", out JsonElement service)
            || service.ValueKind != JsonValueKind.Object
            || !service.TryGetProperty("serviceName", out JsonElement nameElement)
            || nameElement.GetString() is not { Length: > 0 } name)
        {
            throw new InvalidDataException(
                "The service definition has no serviceconfiguration.json naming its service, so there is nothing to call it.");
        }

        if (geodatabases.Count == 0)
        {
            throw new InvalidDataException(
                "The service definition carries no copy of its data (no file geodatabase under cd/). Share it with "
                + "\"Copy all data → Feature\"; registered data is not published from Pro here (ADR-191).");
        }

        string? folder = config.TryGetProperty("folderName", out JsonElement f) && f.ValueKind == JsonValueKind.String
            ? f.GetString() : null;
        string? capabilities = service.TryGetProperty("capabilities", out JsonElement c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;
        int? maxRecordCount = service.TryGetProperty("properties", out JsonElement p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("maxRecordCount", out JsonElement m) && m.ValueKind == JsonValueKind.Number
            && m.TryGetInt32(out int mrc) ? mrc : null;

        return new ServiceDefinition(name, folder, capabilities, maxRecordCount, geodatabases.Count, drawingInfos);
    }

    /// <summary>
    /// The <c>drawingInfo</c> of each layer under <c>resources[name=layers].resources[].contents</c> — the layer's own
    /// REST document as Pro wrote it (measured on an arcpy-staged definition, 2026-10-17). The CIM-symbol copy under
    /// <c>resourceInfo.advancedDrawingInfos</c> is not read: the REST form is the documented one.
    /// </summary>
    private static void DrawingInfosOf(JsonElement root, Dictionary<string, string> into)
    {
        if (!root.TryGetProperty("resources", out JsonElement resources) || resources.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement group in resources.EnumerateArray())
        {
            if (!group.TryGetProperty("name", out JsonElement named) || named.GetString() != "layers"
                || !group.TryGetProperty("resources", out JsonElement layers) || layers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (layer.TryGetProperty("contents", out JsonElement contents) && contents.ValueKind == JsonValueKind.Object
                    && contents.TryGetProperty("name", out JsonElement layerName) && layerName.GetString() is { Length: > 0 } text
                    && contents.TryGetProperty("drawingInfo", out JsonElement drawing) && drawing.ValueKind == JsonValueKind.Object)
                {
                    into[text] = drawing.GetRawText();
                }
            }
        }
    }

    /// <summary>An entry's path with forward slashes, refused when it is absolute or climbs out.</summary>
    private static string Normalised(string? key)
    {
        string path = (key ?? string.Empty).Replace('\\', '/');

        if (path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal)
            || path.Split('/').Any(segment => segment == ".."))
        {
            throw new InvalidDataException($"The service definition holds an entry outside itself: '{path}'.");
        }

        return path;
    }
}
