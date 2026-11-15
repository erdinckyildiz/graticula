using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Graticula.Host;

/// <summary>What a service definition that references registered data says — ADR-299 condition 1.</summary>
/// <param name="ServiceName">The service's name, from <c>serviceconfiguration.json</c>.</param>
/// <param name="FolderName">The server folder Pro chose, or null for the root.</param>
/// <param name="Layers">The map's layers and tables, top to bottom, group layers flattened.</param>
/// <param name="Item">The summary, description and tags Pro's share pane held.</param>
internal sealed record ReferenceDefinition(
    string ServiceName, string? FolderName, IReadOnlyList<ReferenceLayer> Layers, ItemDetails Item);

/// <summary>One layer or standalone table of the map Pro shared, as far as publishing it by reference needs.</summary>
/// <param name="Title">The layer's name in Pro's map — the title it is served under.</param>
/// <param name="Connection">The database it was added from, or null when it names none this server reads.</param>
/// <param name="Schema">The table's schema.</param>
/// <param name="Table">The table.</param>
/// <param name="Columns">The columns Pro's query selected, or null for all of them (<c>select *</c>).</param>
/// <param name="ObjectIdField">The column Pro uses as the object id, or null.</param>
/// <param name="IsTable">True for a standalone table: rows without a shape.</param>
/// <param name="Refusal">Why this layer cannot be served from its database, in words for Pro's user; null when it can.</param>
/// <param name="Renderer">
/// The layer's CIM renderer as Pro drew it, as JSON text, or null for a table or a layer without one. CIM is what this
/// server stores (ADR-052), so it is kept as written and read by <c>SymbologyConversion.Read</c> after the publish.
/// </param>
internal sealed record ReferenceLayer(
    string Title, ArcGisConnection? Connection, string? Schema, string? Table, IReadOnlyList<string>? Columns,
    string? ObjectIdField, bool IsTable, string? Refusal, string? Renderer = null);

/// <summary>
/// Reads the service definition ArcGIS Pro uploads when it shares a map image layer with <i>Reference registered
/// data</i> — ADR-299.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three parts are read, and the map document is the CIM, not the binary.</b> <c>serviceconfiguration.json</c> names
/// the service; <c>esriinfo/iteminfo.xml</c> carries the pane's summary, description and tags; and the map is read from
/// the <c>.mapx</c> Pro writes beside its <c>.msd</c> — a JSON document in the published Cartographic Information Model
/// (github.com/Esri/cim-spec), where each layer's <c>featureTable.dataConnection</c> names its database connection and
/// the query it reads. The <c>.msd</c>, the manifest's encrypted passwords and the thumbnail are never opened: the data
/// is read with the registered source's own credentials.
/// </para>
/// <para>
/// <b>Only a query this server can serve as a table is accepted.</b> A PostgreSQL table in a database that is not a
/// geodatabase is always a query layer in Pro (measured), whose SQL for a table added as it stands is a plain
/// <c>select … from schema.table</c>. That is read as the table and the columns it names; anything else — a filter, a
/// join, an expression — would be served as something other than what Pro drew, so it is refused with the reason.
/// Bounded as <see cref="ServiceDefinitionArchive"/> is: entry count, paths that leave the archive, and document size.
/// </para>
/// </remarks>
internal static partial class ReferenceServiceDefinition
{
    private const long MaximumDocumentBytes = 32L * 1024 * 1024;

    /// <summary>Reads the archive at <paramref name="sdPath"/>.</summary>
    /// <exception cref="InvalidDataException">It is not a service definition, or names no service.</exception>
    public static async Task<ReferenceDefinition> ReadAsync(string sdPath, CancellationToken cancellation)
    {
        using IArchive archive = ArchiveFactory.OpenArchive(sdPath, new ReaderOptions());

        if (archive.Type != ArchiveType.SevenZip)
        {
            throw new InvalidDataException("A service definition is a 7z archive, and this file is not one.");
        }

        List<IArchiveEntry> entries = [.. archive.Entries];
        if (entries.Count > ServiceDefinitionArchive.MaximumMembers)
        {
            throw new InvalidDataException(
                $"The service definition holds {entries.Count:N0} entries; at most {ServiceDefinitionArchive.MaximumMembers:N0} are opened.");
        }

        JsonElement? configuration = null;
        JsonElement? map = null;
        ItemDetails item = ItemDetails.None;

        foreach (IArchiveEntry entry in entries.Where(e => !e.IsDirectory))
        {
            cancellation.ThrowIfCancellationRequested();
            string key = Normalised(entry.Key);
            bool isConfiguration = string.Equals(key, "serviceconfiguration.json", StringComparison.OrdinalIgnoreCase);
            bool isMap = map is null && key.EndsWith(".mapx", StringComparison.OrdinalIgnoreCase);
            bool isItem = string.Equals(key, "esriinfo/iteminfo.xml", StringComparison.OrdinalIgnoreCase);

            if (!isConfiguration && !isMap && !isItem)
            {
                continue;
            }

            if (entry.Size > MaximumDocumentBytes)
            {
                throw new InvalidDataException($"The service definition's '{key}' is larger than 32 MB.");
            }

            using Stream read = entry.OpenEntryStream();
            if (isItem)
            {
                item = ServiceDefinitionArchive.ItemOf(read);
                continue;
            }

            using JsonDocument document = await JsonDocument.ParseAsync(
                read, new JsonDocumentOptions { MaxDepth = 256 }, cancellation).ConfigureAwait(false);

            if (isConfiguration)
            {
                configuration = document.RootElement.Clone();
            }
            else
            {
                map = document.RootElement.Clone();
            }
        }

        if (configuration is not { } config || !config.TryGetProperty("service", out JsonElement service)
            || service.ValueKind != JsonValueKind.Object
            || !service.TryGetProperty("serviceName", out JsonElement named)
            || named.GetString() is not { Length: > 0 } name)
        {
            throw new InvalidDataException(
                "The service definition has no serviceconfiguration.json naming its service, so there is nothing to call it.");
        }

        if (map is not { } mapx)
        {
            throw new InvalidDataException("The service definition carries no map (.mapx), so it names no layers.");
        }

        string? folder = config.TryGetProperty("folderName", out JsonElement f) && f.ValueKind == JsonValueKind.String
            && f.GetString() is { Length: > 0 } inFolder ? inFolder : null;

        return new ReferenceDefinition(name, folder, LayersOf(mapx), item);
    }

    /// <summary>The map's layers and standalone tables, in the order Pro lists them, from its CIM document.</summary>
    internal static List<ReferenceLayer> LayersOf(JsonElement mapx)
    {
        Dictionary<string, JsonElement> byUri = new(StringComparer.OrdinalIgnoreCase);
        foreach (string list in (string[])["layerDefinitions", "tableDefinitions"])
        {
            if (mapx.TryGetProperty(list, out JsonElement definitions) && definitions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement definition in definitions.EnumerateArray())
                {
                    if (Text(definition, "uRI") is { } uri)
                    {
                        byUri[uri] = definition;
                    }
                }
            }
        }

        List<ReferenceLayer> layers = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        JsonElement mapDefinition = mapx.TryGetProperty("mapDefinition", out JsonElement m) ? m : default;

        void Walk(JsonElement uris)
        {
            if (uris.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement uriElement in uris.EnumerateArray())
            {
                if (uriElement.GetString() is not { } uri || !seen.Add(uri) || !byUri.TryGetValue(uri, out JsonElement layer))
                {
                    continue;
                }

                // A group layer's children are served beside each other: this server's group layers are made in Studio,
                // and the map image layer's drawing order is kept.
                if (Text(layer, "type") == "CIMGroupLayer")
                {
                    Walk(layer.TryGetProperty("layers", out JsonElement children) ? children : default);
                    continue;
                }

                layers.Add(LayerOf(layer));
            }
        }

        if (mapDefinition.ValueKind == JsonValueKind.Object)
        {
            Walk(mapDefinition.TryGetProperty("layers", out JsonElement top) ? top : default);
            Walk(mapDefinition.TryGetProperty("standaloneTables", out JsonElement tables) ? tables : default);
        }

        return layers;
    }

    /// <summary>One layer definition read as a reference, or refused with the reason.</summary>
    internal static ReferenceLayer LayerOf(JsonElement layer)
    {
        string title = Text(layer, "name") ?? "(unnamed layer)";
        string type = Text(layer, "type") ?? string.Empty;
        bool isTable = type == "CIMStandaloneTable";

        if (type != "CIMFeatureLayer" && !isTable)
        {
            return Refused(title, isTable, $"'{title}' is a {Kind(type)}. Only feature layers and tables are published by reference here.");
        }

        JsonElement table = isTable ? layer : layer.TryGetProperty("featureTable", out JsonElement t) ? t : default;
        JsonElement connection = table.ValueKind == JsonValueKind.Object && table.TryGetProperty("dataConnection", out JsonElement c)
            ? c : default;

        if (connection.ValueKind != JsonValueKind.Object)
        {
            return Refused(title, isTable, $"'{title}' names no data source.");
        }

        if (table.TryGetProperty("definitionExpression", out JsonElement filter) && filter.GetString() is { Length: > 0 })
        {
            return Refused(
                title, isTable,
                $"'{title}' has a definition query. A layer published by reference is served from its whole table here, so it "
                + "would show rows Pro does not; remove the query, or publish a view of those rows from the database.");
        }

        ArcGisConnection? database = ArcGisConnection.Parse(Text(connection, "workspaceConnectionString"));
        if (database is null)
        {
            string workspace = (Text(connection, "workspaceConnectionString") ?? string.Empty).Replace('\\', '/');

            // Measured 2026-11-13: when Pro finds a layer's database not registered — a different database user is
            // enough, as Pro compares the user too — it copies the rows into the definition under cd/ even with
            // copyDataToServer off, and the layer then names that copy.
            return Refused(title, isTable, workspace.Contains("/cd/", StringComparison.OrdinalIgnoreCase)
                ? $"'{title}' was copied into the service definition instead of referenced, because Pro found its database "
                    + "not registered on this server for the user it connects as. Connect as the registered database user, "
                    + "or share it as a web feature layer to copy the data."
                : $"'{title}' is not in a PostgreSQL database, so it cannot be referenced on this server.");
        }

        switch (Text(connection, "type"))
        {
            case "CIMSqlQueryDataConnection":
                if (!TryTable(Text(connection, "sqlQuery"), database.Database, out string? schema, out string? name,
                        out List<string>? columns, out string? why))
                {
                    return Refused(title, isTable, $"'{title}': {why}", database);
                }

                return new ReferenceLayer(
                    title, database, schema, name, columns, FirstOf(Text(connection, "oIDFields")), isTable, null, RendererOf(layer, isTable));

            case "CIMStandardDataConnection" or "CIMFeatureDatasetDataConnection":
                if (!TryName(Text(connection, "dataset"), database.Database, out schema, out name, out why))
                {
                    return Refused(title, isTable, $"'{title}': {why}", database);
                }

                return new ReferenceLayer(title, database, schema, name, null, null, isTable, null, RendererOf(layer, isTable));

            default:
                return Refused(title, isTable, $"'{title}' reads its data through a {Text(connection, "type")}, which is not published by reference here.", database);
        }
    }

    /// <summary>
    /// A feature layer's <c>renderer</c> — the CIM object Pro drew it with — as JSON text; null for a table, which
    /// draws nothing, and for a layer that names no renderer object.
    /// </summary>
    internal static string? RendererOf(JsonElement layer, bool isTable) =>
        !isTable && layer.TryGetProperty("renderer", out JsonElement renderer) && renderer.ValueKind == JsonValueKind.Object
            ? renderer.GetRawText()
            : null;

    /// <summary>
    /// Reads <c>select col, … from [database.]schema.table</c> — or <c>select *</c> — and nothing more elaborate.
    /// </summary>
    internal static bool TryTable(
        string? sql, string database, out string? schema, out string? table, out List<string>? columns, out string? why)
    {
        schema = table = null;
        columns = null;

        Match plain = sql is null ? Match.Empty : PlainSelect().Match(sql);
        if (!plain.Success)
        {
            why = $"its query is not a plain select of one table's columns (\"{Shortened(sql)}\"). A query with a filter, a "
                + "join or an expression would be served as something other than what Pro draws; publish a view of it "
                + "from the database, or share it with Copy all data.";
            return false;
        }

        if (!TryName(plain.Groups["name"].Value, database, out schema, out table, out why))
        {
            return false;
        }

        string list = plain.Groups["columns"].Value.Trim();
        if (list == "*")
        {
            return true;
        }

        columns = [];
        foreach (string part in list.Split(','))
        {
            if (!TryIdentifier(part.Trim(), out string column))
            {
                why = $"its query selects \"{Shortened(part.Trim())}\", which is not a column of the table. Only a table's "
                    + "own columns are published by reference.";
                return false;
            }

            columns.Add(column);
        }

        return true;
    }

    /// <summary>Reads <c>[database.]schema.table</c>; a bare table is in <c>public</c>.</summary>
    internal static bool TryName(string? dataset, string database, out string? schema, out string? table, out string? why)
    {
        schema = table = null;
        string[] raw = (dataset ?? string.Empty).Split('.');
        string[] parts = new string[raw.Length];

        // Pro writes a query layer's dataset as "db.schema.%name" — the % marks the layer's own name, not a table.
        if (raw[^1].Trim().StartsWith('%'))
        {
            why = $"\"{Shortened(dataset)}\" names a query layer, not a table.";
            return false;
        }

        bool readable = raw.Length <= 3;
        for (int i = 0; readable && i < raw.Length; i++)
        {
            readable = TryIdentifier(raw[i].Trim(), out parts[i]);
        }

        if (!readable)
        {
            why = $"\"{Shortened(dataset)}\" is not a table name this server can read.";
            return false;
        }

        if (parts.Length == 3 && !string.Equals(parts[0], database, StringComparison.OrdinalIgnoreCase))
        {
            why = $"its table is in database '{parts[0]}', and its connection is to '{database}'.";
            return false;
        }

        (schema, table) = parts.Length == 1 ? ("public", parts[0]) : (parts[^2], parts[^1]);
        why = null;
        return true;
    }

    private static ReferenceLayer Refused(string title, bool isTable, string why, ArcGisConnection? connection = null) =>
        new(title, connection, null, null, null, null, isTable, why);

    private static string Kind(string type) => type switch
    {
        "CIMRasterLayer" => "raster layer",
        "CIMAnnotationLayer" => "annotation layer",
        "CIMDimensionLayer" => "dimension layer",
        "CIMTiledServiceLayer" or "CIMVectorTileLayer" => "service layer",
        "" => "layer of an unknown kind",
        _ => type.StartsWith("CIM", StringComparison.Ordinal) ? type[3..] : type,
    };

    private static string? FirstOf(string? list) =>
        list?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A plain identifier, or a quoted one without quotes inside; an alias, an expression or a cast is neither.</summary>
    private static bool TryIdentifier(string raw, out string name)
    {
        if (raw.Length >= 3 && raw[0] == '"' && raw[^1] == '"' && !raw[1..^1].Contains('"', StringComparison.Ordinal))
        {
            name = raw[1..^1];
            return true;
        }

        name = raw;
        return Identifier().IsMatch(raw);
    }

    private static string Shortened(string? text) =>
        text is null ? string.Empty : text.Length <= 120 ? text : text[..117] + "…";

    private static string Normalised(string? key)
    {
        string path = (key ?? string.Empty).Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal) || path.Split('/').Any(s => s == ".."))
        {
            throw new InvalidDataException($"The service definition holds an entry outside itself: '{path}'.");
        }

        return path;
    }

    // select <columns> from <name> [;] — and nothing after it: no where, join, group, order or limit.
    [GeneratedRegex(
        @"^\s*select\s+(?<columns>\*|[^;]+?)\s+from\s+(?<name>(?:""[^""]+""|[A-Za-z_%][\w$%]*)(?:\s*\.\s*(?:""[^""]+""|[A-Za-z_%][\w$%]*)){0,2})\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PlainSelect();

    [GeneratedRegex(@"^[A-Za-z_][\w$]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
