using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host.Tools;

/// <summary>
/// Walks an ArcGIS Server's REST services directory and says, service by service and layer by layer,
/// what this server can take over, what only in part, and what not at all — the first half of
/// Q-16's migration tooling.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-16](../../../docs/open-questions.md), owner decision: inventory plus definition import, free.</b>
/// This is the inventory. Its reader is somebody whose licence ends on a date and who needs to know
/// whether landing here is possible at all before committing to anything, so every verdict carries
/// its reason, and a reason names the decision or the debt that makes it so.
/// </para>
/// <para>
/// <b>Read-only, from the published REST documents, and nothing else.</b> It asks what any ArcGIS
/// client may ask — the directory, each service document, each layer document — with the token the
/// operator gives it, and it writes nothing anywhere but the report. A command rather than an admin
/// endpoint, so the server is never made to fetch an address somebody typed into a form.
/// </para>
/// <para>
/// <b>What the REST documents cannot say is said instead of guessed.</b> Where a layer's table lives
/// is not published, and it decides everything for a registered layer: data stays where it is
/// (Q-16), which here means PostGIS. Every layer's report says so rather than implying it.
/// </para>
/// </remarks>
internal static class InventoryScan
{
    /// <summary>How far a layer comes across.</summary>
    internal enum Verdict
    {
        /// <summary>Everything the document describes is served here.</summary>
        Comes,

        /// <summary>It is served, with something named left behind.</summary>
        Partly,

        /// <summary>It is not served by this build.</summary>
        Stays,
    }

    /// <summary>One layer's verdict.</summary>
    /// <param name="Id">Its index in the service.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="Verdict">How far it comes across.</param>
    /// <param name="Notes">Why, one sentence each.</param>
    internal sealed record LayerReport(int Id, string Name, Verdict Verdict, IReadOnlyList<string> Notes);

    /// <summary>One service's verdict.</summary>
    /// <param name="Name">Its qualified name.</param>
    /// <param name="Type">Its type, as the directory lists it.</param>
    /// <param name="Verdict">How far it comes across — the worst of its layers, for a feature service.</param>
    /// <param name="Notes">Why, about the service as a whole.</param>
    /// <param name="Layers">Its layers and tables.</param>
    internal sealed record ServiceReport(
        string Name, string Type, Verdict Verdict, IReadOnlyList<string> Notes, IReadOnlyList<LayerReport> Layers);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private const string DataStaysNote =
        "No data is moved (Q-16), and where a layer's table lives is not in its REST document: a layer comes "
        + "across by registering its table, which needs the table in PostGIS; anywhere else, export the feature "
        + "class to a file geodatabase and import that.";

    /// <summary>The service types this build serves, with what taking one over means.</summary>
    private static readonly Dictionary<string, (Verdict Verdict, string Note)> ServiceTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FeatureServer"] = (Verdict.Comes, "Served as a FeatureServer."),
            ["MapServer"] = (Verdict.Partly,
                "Its feature layers are served as a FeatureServer; a MapServer face exists here but is outside "
                + "v1's scope (docs/v1-scope.md §3b), and cached map tiles are not taken over."),
            ["VectorTileServer"] = (Verdict.Partly,
                "Vector tiles are drawn here from the source layer's data rather than copied from the tile "
                + "package; publish the layer and its tiles follow."),
            ["GeometryServer"] = (Verdict.Partly,
                "This server has its own GeometryServer; its overlay operations are not offered (Q-97)."),
            ["ImageServer"] = (Verdict.Stays, "Imagery is not served by v1 (docs/v1-scope.md §3b)."),
            ["GPServer"] = (Verdict.Stays, "Geoprocessing services are not served by this build."),
            ["GeocodeServer"] = (Verdict.Stays, "Geocoding services are not served by this build."),
            ["NAServer"] = (Verdict.Stays, "Network analysis services are not served by this build."),
            ["SceneServer"] = (Verdict.Stays, "Scene services are not served by this build."),
            ["StreamServer"] = (Verdict.Stays, "Stream services are not served by this build."),
            ["GlobeServer"] = (Verdict.Stays, "Globe services are not served by this build."),
            ["SymbolServer"] = (Verdict.Stays, "Symbol services are not served by this build."),
        };

    /// <summary>Field types that come across as they are.</summary>
    private static readonly HashSet<string> ServedFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "esriFieldTypeOID", "esriFieldTypeGlobalID", "esriFieldTypeGUID", "esriFieldTypeString",
        "esriFieldTypeSmallInteger", "esriFieldTypeInteger", "esriFieldTypeBigInteger", "esriFieldTypeSingle",
        "esriFieldTypeDouble", "esriFieldTypeDate", "esriFieldTypeBlob", "esriFieldTypeGeometry",
    };

    /// <summary>Renderers this server's symbology document carries as drawn (ADR-052).</summary>
    private static readonly HashSet<string> ServedRenderers = new(StringComparer.OrdinalIgnoreCase)
    {
        "simple", "uniqueValue", "classBreaks",
    };

    /// <summary>
    /// Runs the scan from the command line.
    /// </summary>
    /// <param name="args">The command line: <c>tools inventory &lt;url&gt; [--json &lt;file&gt;] [--insecure]</c>.</param>
    /// <param name="output">Where the report goes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Zero when the directory could be read.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string? url = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && !IsValueOf(args, a));

        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? root)
            || (root.Scheme != Uri.UriSchemeHttps && root.Scheme != Uri.UriSchemeHttp))
        {
            await output.WriteLineAsync(
                "Usage: graticula tools inventory <https://host/arcgis> [--json <file>] [--insecure]\n"
                + "A token, when the services need one, is read from GRATICULA_INVENTORY_TOKEN rather than "
                + "the command line, which lands in shell history.").ConfigureAwait(false);
            return 2;
        }

        string? token = Environment.GetEnvironmentVariable("GRATICULA_INVENTORY_TOKEN");
        string? jsonPath = ValueAfter(args, "--json");

        using HttpClientHandler handler = new();

        if (args.Contains("--insecure"))
        {
            // Asked for by name, for a server with a self-signed certificate; never the default.
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(60) };

        IReadOnlyList<ServiceReport> services;

        try
        {
            services = await ScanAsync(http, root, token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            await output.WriteLineAsync($"The services directory at {root} could not be read: {e.Message}").ConfigureAwait(false);
            return 1;
        }

        await output.WriteAsync(Report(root, services)).ConfigureAwait(false);

        if (jsonPath is not null)
        {
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(
                    new { source = root.ToString(), services },
                    JsonOptions),
                cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync($"\nThe same report as JSON: {jsonPath}").ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Walks the directory and every service in it.</summary>
    /// <param name="http">The client.</param>
    /// <param name="root">The server's address — <c>…/arcgis</c> or <c>…/arcgis/rest/services</c>.</param>
    /// <param name="token">A token, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Every service's report.</returns>
    internal static async Task<IReadOnlyList<ServiceReport>> ScanAsync(
        HttpClient http, Uri root, string? token, CancellationToken cancellationToken)
    {
        string services = ServicesRoot(root);
        List<ServiceReport> reports = [];
        Queue<string?> folders = new([null]);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        while (folders.Count > 0)
        {
            string? folder = folders.Dequeue();
            JsonElement listing = await GetAsync(http, folder is null ? services : $"{services}/{folder}", token, cancellationToken)
                .ConfigureAwait(false);

            if (folder is null && listing.TryGetProperty("folders", out JsonElement nested))
            {
                foreach (JsonElement name in nested.EnumerateArray())
                {
                    folders.Enqueue(name.GetString());
                }
            }

            if (!listing.TryGetProperty("services", out JsonElement list))
            {
                continue;
            }

            foreach (JsonElement entry in list.EnumerateArray())
            {
                string name = entry.GetProperty("name").GetString()!;
                string type = entry.GetProperty("type").GetString()!;

                if (seen.Add($"{name}/{type}"))
                {
                    reports.Add(await ServiceAsync(http, services, name, type, token, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return reports;
    }

    private static async Task<ServiceReport> ServiceAsync(
        HttpClient http, string services, string name, string type, string? token, CancellationToken cancellationToken)
    {
        (Verdict verdict, string note) = ServiceTypes.TryGetValue(type, out var known)
            ? known
            : (Verdict.Stays, $"'{type}' is not a service type this build serves.");

        if (!type.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
            && !type.Equals("MapServer", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceReport(name, type, verdict, [note], []);
        }

        string address = $"{services}/{name}/{type}";
        JsonElement document;

        try
        {
            document = await GetAsync(http, address, token, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException refused)
        {
            return new ServiceReport(name, type, Verdict.Stays, [$"Its service document could not be read: {refused.Message}"], []);
        }

        List<string> notes = [note];
        List<LayerReport> layers = [];

        if (document.TryGetProperty("capabilities", out JsonElement capabilities)
            && capabilities.GetString() is { } offered
            && offered.Contains("Sync", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("It offers Sync, and this server does not: offline replicas made against it cannot be synchronised here.");
        }

        foreach (string kind in (string[])["layers", "tables"])
        {
            if (!document.TryGetProperty(kind, out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement entry in entries.EnumerateArray())
            {
                int id = entry.GetProperty("id").GetInt32();

                // A group layer is a folder of other layers and has nothing of its own to report.
                if (entry.TryGetProperty("subLayerIds", out JsonElement children) && children.ValueKind == JsonValueKind.Array
                    && children.GetArrayLength() > 0)
                {
                    continue;
                }

                try
                {
                    JsonElement layer = await GetAsync(http, $"{address}/{id}", token, cancellationToken).ConfigureAwait(false);
                    layers.Add(Classify(id, layer, isTable: kind == "tables"));
                }
                catch (InvalidOperationException refused)
                {
                    layers.Add(new LayerReport(
                        id, entry.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? $"{id}" : $"{id}",
                        Verdict.Stays, [$"Its layer document could not be read: {refused.Message}"]));
                }
            }
        }

        Verdict worst = layers.Count == 0 ? verdict : (Verdict)Math.Max((int)verdict, layers.Max(l => (int)l.Verdict));
        return new ServiceReport(name, type, worst, notes, layers);
    }

    /// <summary>Judges one layer document.</summary>
    /// <param name="id">The layer's index.</param>
    /// <param name="layer">Its document.</param>
    /// <param name="isTable">Whether the service listed it under <c>tables</c>.</param>
    /// <returns>The verdict and every reason for it.</returns>
    internal static LayerReport Classify(int id, JsonElement layer, bool isTable)
    {
        string name = layer.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? $"{id}" : $"{id}";
        Verdict verdict = Verdict.Comes;
        List<string> notes = [];

        void Partly(string why)
        {
            verdict = verdict == Verdict.Stays ? Verdict.Stays : Verdict.Partly;
            notes.Add(why);
        }

        void Stays(string why)
        {
            verdict = Verdict.Stays;
            notes.Add(why);
        }

        string geometry = layer.TryGetProperty("geometryType", out JsonElement g) && g.ValueKind == JsonValueKind.String
            ? g.GetString()!
            : string.Empty;

        if (isTable || geometry.Length == 0)
        {
            Stays("A table without geometry: this build publishes feature layers and lists no tables in a FeatureServer.");
        }
        else if (geometry is not ("esriGeometryPoint" or "esriGeometryMultipoint" or "esriGeometryPolyline" or "esriGeometryPolygon"))
        {
            Stays($"Its geometry is {geometry}, and this server stores points, multipoints, lines and polygons only.");
        }

        if (True(layer, "hasZ") || True(layer, "hasM"))
        {
            Partly("It has Z or M values, and they are dropped when the data is read (D-107).");
        }

        if (layer.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Array)
        {
            string[] unserved = [.. fields.EnumerateArray()
                .Where(f => f.TryGetProperty("type", out JsonElement t) && !ServedFieldTypes.Contains(t.GetString() ?? string.Empty))
                .Select(f => $"{f.GetProperty("name").GetString()} ({f.GetProperty("type").GetString()})")];

            if (unserved.Length > 0)
            {
                Partly($"Field{(unserved.Length == 1 ? string.Empty : "s")} of a type this server does not serve as such: {string.Join(", ", unserved)}.");
            }
        }

        if (layer.TryGetProperty("drawingInfo", out JsonElement drawing)
            && drawing.TryGetProperty("renderer", out JsonElement renderer)
            && renderer.TryGetProperty("type", out JsonElement kind)
            && kind.GetString() is { } rendererType
            && !ServedRenderers.Contains(rendererType))
        {
            Partly($"Its renderer is '{rendererType}'; this server draws simple, unique value and class breaks renderers, and gives this layer a generated one.");
        }

        if (True(layer, "isDataVersioned") || True(layer, "isDataBranchVersioned"))
        {
            Partly("Its data is versioned; this server has no versions and serves the table as it is (the default version).");
        }

        if (True(layer, "isDataArchived"))
        {
            Partly("It is archived; its history does not come with it. Once imported as a hosted layer, its owner can turn history on and historic moments are served from then (ADR-078).");
        }

        if (layer.TryGetProperty("relationships", out JsonElement relationships) && relationships.ValueKind == JsonValueKind.Array
            && relationships.GetArrayLength() > 0)
        {
            notes.Add($"{relationships.GetArrayLength()} relationship(s): related records are served (ADR-013), declared again after publishing.");
        }

        if (True(layer, "hasAttachments"))
        {
            notes.Add("It has attachments, which this server serves; they are not copied by the inventory.");
        }

        if (layer.TryGetProperty("types", out JsonElement types) && types.ValueKind == JsonValueKind.Array && types.GetArrayLength() > 0)
        {
            notes.Add($"{types.GetArrayLength()} subtype(s), served with their domains (ADR-065).");
        }

        if (layer.TryGetProperty("editFieldsInfo", out JsonElement tracking) && tracking.ValueKind == JsonValueKind.Object)
        {
            notes.Add("Editor tracking is on, and served here (ADR-064).");
        }

        return new LayerReport(id, name, verdict, notes);
    }

    /// <summary>The human-readable report.</summary>
    /// <param name="root">What was scanned.</param>
    /// <param name="services">What was found.</param>
    /// <returns>The text.</returns>
    internal static string Report(Uri root, IReadOnlyList<ServiceReport> services)
    {
        StringBuilder text = new();
        int layers = services.Sum(s => s.Layers.Count);

        text.AppendLine(CultureInfo.InvariantCulture, $"Inventory of {root}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{services.Count} service(s), {layers} layer(s). Services that come across: {Count(Verdict.Comes)}; in part: {Count(Verdict.Partly)}; not served: {Count(Verdict.Stays)}.");
        text.AppendLine(DataStaysNote);
        text.AppendLine();

        foreach (ServiceReport service in services.OrderBy(s => s.Verdict).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"[{Word(service.Verdict)}] {service.Name} ({service.Type})");

            foreach (string note in service.Notes)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    {note}");
            }

            foreach (LayerReport layer in service.Layers)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    [{Word(layer.Verdict)}] {layer.Id} {layer.Name}");

                foreach (string note in layer.Notes)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"        {note}");
                }
            }

            text.AppendLine();
        }

        return text.ToString();

        int Count(Verdict verdict) => services.Count(s => s.Verdict == verdict);
    }

    private static string Word(Verdict verdict) => verdict switch
    {
        Verdict.Comes => "comes across",
        Verdict.Partly => "in part",
        Verdict.Stays => "not served",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    private static string ServicesRoot(Uri root)
    {
        string path = root.GetLeftPart(UriPartial.Path).TrimEnd('/');

        return path.EndsWith("/rest/services", StringComparison.OrdinalIgnoreCase) ? path : $"{path}/rest/services";
    }

    private static async Task<JsonElement> GetAsync(HttpClient http, string address, string? token, CancellationToken cancellationToken)
    {
        string url = $"{address}?f=json" + (token is { Length: > 0 } ? $"&token={Uri.EscapeDataString(token)}" : string.Empty);

        using HttpResponseMessage response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonElement document = JsonDocument.Parse(body).RootElement.Clone();

        // ArcGIS answers most refusals with 200 and an error object.
        if (!response.IsSuccessStatusCode
            || (document.ValueKind == JsonValueKind.Object && document.TryGetProperty("error", out _)))
        {
            string message = document.ValueKind == JsonValueKind.Object
                && document.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement said)
                    ? said.GetString() ?? "an error"
                    : $"HTTP {(int)response.StatusCode}";

            throw new InvalidOperationException(message);
        }

        return document;
    }

    private static bool True(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string? ValueAfter(string[] args, string flag)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static bool IsValueOf(string[] args, string value)
    {
        int at = Array.IndexOf(args, value);
        return at > 0 && args[at - 1] == "--json";
    }
}
