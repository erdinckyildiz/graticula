using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Host.Tools;

/// <summary>
/// Q-16's second step: an ArcGIS Server's layer definitions, published here over the tables they already
/// read — ADR-081.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two commands, and the file between them is the point.</b> <c>plan</c> reads the source and the target
/// and writes what it would do — every source layer, the table it matched and why, or why it matched none.
/// <c>apply</c> does what the file says, after a person has read it and filled in what could not be matched.
/// A REST layer document does not say which table it reads, so a match is a guess by name, and a guess that
/// publishes somebody's service over the wrong table is not one to take without a reader in between.
/// </para>
/// <para>
/// <b>The data stays where it is</b> — Q-16, because Q-50a made registered sources fully capable. What comes
/// across is the definition: the layer over its table, its service and folder, its drawing (the target's
/// symbology endpoint reads an Esri <c>drawingInfo</c> as sent) and its field aliases.
/// </para>
/// </remarks>
internal static class MigrationPlan
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A table the target's data source offers, as its capability report lists it.</summary>
    internal sealed record Table(
        string Schema, string Name, string? GeometryColumn, int Srid, string? GeometryType, string? ObjectIdColumn);

    /// <summary>A source layer, and what the plan would publish it as.</summary>
    internal sealed record Entry(
        string Service,
        string? Folder,
        int LayerId,
        string LayerName,
        Table? Target,
        IReadOnlyDictionary<string, string> Aliases,
        JsonElement? DrawingInfo,
        string Note,
        bool IsTable = false);

    /// <summary>What <c>apply</c> asks of the symbology route: keep a picture it cannot use as a loss (ADR-099 §5.1).</summary>
    internal const string SymbologyQuery = "?pictures=keep";

    /// <summary>A name as a table and a layer are compared: its last dotted part, letters and digits, lower case.</summary>
    /// <remarks>
    /// <b>The last dotted part</b>, because an enterprise geodatabase names its feature classes
    /// <c>DATABASE.OWNER.PARCELS</c> and publishes them as <c>Parcels</c>; <b>letters and digits</b>, because a
    /// layer called <i>Road Centrelines</i> is the table <c>road_centrelines</c>.
    /// </remarks>
    internal static string Normalise(string name)
    {
        string last = name[(name.LastIndexOf('.') + 1)..];
        return new string([.. last.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
    }

    /// <summary>The one table a layer's name matches, or why there is none.</summary>
    /// <param name="layerName">The source layer's name.</param>
    /// <param name="tables">The target data source's tables.</param>
    /// <param name="isTable">
    /// Whether the source is a table — ADR-213 — which matches a table with no geometry column, as a layer matches one
    /// with.
    /// </param>
    /// <param name="geometryType">
    /// The source layer's <c>geometryType</c> (<c>esriGeometryPolygon</c> and so on), or null: several tables of one name
    /// are narrowed to those whose geometry is of the same kind — D-385.
    /// </param>
    /// <param name="dataset">
    /// The geodatabase dataset the source's manifest says the layer reads (<c>DB.OWNER.PARCELS</c>), or null. When given it
    /// is the name matched, in place of the layer's, and its owner part narrows several tables of one name to the schema
    /// of that name — D-385, ADR-081 Alternative C.
    /// </param>
    internal static (Table? Table, string Note) Match(
        string layerName, IReadOnlyList<Table> tables, bool isTable = false, string? geometryType = null, string? dataset = null)
    {
        string wanted = Normalise(dataset ?? layerName);
        string how = dataset is null ? "by name" : $"through the source's manifest, which names {dataset}";
        Table[] found = [.. tables.Where(t => (t.GeometryColumn is null) == isTable && Normalise(t.Name) == wanted)];

        // D-385: a tie is narrowed by what the source says of the layer — its geometry's kind, and its dataset's owner —
        // and is left a tie when that does not settle it.
        if (found.Length > 1 && !isTable && KindOf(geometryType) is { } kind)
        {
            Table[] sameKind = [.. found.Where(t => KindOf(t.GeometryType) == kind)];
            if (sameKind.Length > 0)
            {
                found = sameKind;
            }
        }

        if (found.Length > 1 && OwnerOf(dataset) is { } owner)
        {
            Table[] owned = [.. found.Where(t => string.Equals(t.Schema, owner, StringComparison.OrdinalIgnoreCase))];
            if (owned.Length == 1)
            {
                found = owned;
            }
        }

        return found.Length switch
        {
            1 when found[0].ObjectIdColumn is null => (null,
                $"The table {found[0].Schema}.{found[0].Name} matches {how} and has no integer object-id column, "
                + "so it cannot be served through the ArcGIS surface. Add one, or pick another table."),
            1 => (found[0], $"Matched {found[0].Schema}.{found[0].Name} {how}. Check it before applying."),
            0 => (null, isTable
                ? "No table without a geometry column has this name. Fill in schema and table by hand, or leave it out."
                : "No table with a geometry column has this name. Fill in schema and table by hand, or leave it out."),
            _ => (null,
                $"{found.Length} tables match by name ({string.Join(", ", found.Select(t => $"{t.Schema}.{t.Name}"))}). "
                + "Choose one by hand."),
        };
    }

    /// <summary>A geometry's kind — point, line or polygon — from an Esri <c>geometryType</c> or a PostGIS type name.</summary>
    internal static string? KindOf(string? geometryType)
    {
        string type = (geometryType ?? string.Empty).ToUpperInvariant().Replace("ESRIGEOMETRY", string.Empty, StringComparison.Ordinal);

        return type switch
        {
            _ when type.Contains("POLYGON", StringComparison.Ordinal) || type.Contains("ENVELOPE", StringComparison.Ordinal)
                || type.Contains("SURFACE", StringComparison.Ordinal) => "polygon",
            _ when type.Contains("LINE", StringComparison.Ordinal) || type.Contains("CURVE", StringComparison.Ordinal) => "line",
            _ when type.Contains("POINT", StringComparison.Ordinal) => "point",
            _ => null,
        };
    }

    /// <summary>The owner part of a geodatabase dataset's name — <c>GISOWNER</c> of <c>GISDB.GISOWNER.PARCELS</c> — or null.</summary>
    internal static string? OwnerOf(string? dataset)
    {
        string[] parts = (dataset ?? string.Empty).Split('.');
        return parts.Length >= 2 && parts[^2].Length > 0 ? parts[^2] : null;
    }

    /// <summary>
    /// Which dataset of the service's manifest each layer reads, or null where that is not certain — D-385, ADR-081
    /// Alternative C.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The manifest names a service's datasets, not which layer reads which</b>
    /// (<c>/admin/services/&lt;service&gt;.MapServer/iteminfo/manifest/manifest.json</c>, <c>databases[].datasets[].onServerName</c>,
    /// ArcGIS Server's Administrator API). So the pairing is made in two passes, and nothing is guessed: a layer whose
    /// name matches exactly one dataset takes it; then, of what is left, a service with one layer and one dataset pairs
    /// them, which is the common case of a layer renamed at publish time.
    /// </para>
    /// <para>
    /// <b>Why it helps although it is still a name rule:</b> the dataset's name is the table's own, so the second step reads
    /// the target's table by the name it really has rather than by a layer's label.
    /// </para>
    /// </remarks>
    internal static string?[] PairWithManifest(IReadOnlyList<string> layerNames, IReadOnlyList<string> datasets)
    {
        string?[] paired = new string?[layerNames.Count];
        HashSet<int> taken = [];

        for (int i = 0; i < layerNames.Count; i++)
        {
            string wanted = Normalise(layerNames[i]);
            int[] hits = [.. Enumerable.Range(0, datasets.Count).Where(d => Normalise(datasets[d]) == wanted)];

            if (hits.Length == 1)
            {
                paired[i] = datasets[hits[0]];
                taken.Add(hits[0]);
            }
        }

        int[] layersLeft = [.. Enumerable.Range(0, layerNames.Count).Where(i => paired[i] is null)];
        int[] datasetsLeft = [.. Enumerable.Range(0, datasets.Count).Where(d => !taken.Contains(d))];

        if (layersLeft.Length == 1 && datasetsLeft.Length == 1)
        {
            paired[layersLeft[0]] = datasets[datasetsLeft[0]];
        }

        return paired;
    }

    /// <summary>The dataset names a manifest lists, in order and once each.</summary>
    internal static IReadOnlyList<string> DatasetsOf(JsonElement manifest)
    {
        List<string> names = [];

        if (manifest.ValueKind == JsonValueKind.Object
            && manifest.TryGetProperty("databases", out JsonElement databases) && databases.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement database in databases.EnumerateArray())
            {
                if (database.TryGetProperty("datasets", out JsonElement datasets) && datasets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement dataset in datasets.EnumerateArray())
                    {
                        string? name = Text(dataset, "onServerName") ?? Text(dataset, "onPremiseName");
                        if (name is { Length: > 0 } && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        {
                            names.Add(name);
                        }
                    }
                }
            }
        }

        return names;
    }

    /// <summary>The Administrator API's services root beside a REST services root: <c>…/rest/services</c> to <c>…/admin/services</c>.</summary>
    internal static string AdminServicesRoot(string services) =>
        services.EndsWith("/rest/services", StringComparison.OrdinalIgnoreCase)
            ? services[..^"/rest/services".Length] + "/admin/services"
            : services + "/../admin/services";

    /// <summary>Every field whose alias says something its name does not.</summary>
    internal static IReadOnlyDictionary<string, string> AliasesOf(JsonElement layer)
    {
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);

        if (layer.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement field in fields.EnumerateArray())
            {
                if (field.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } column
                    && field.TryGetProperty("alias", out JsonElement alias) && alias.GetString() is { Length: > 0 } label
                    && !string.Equals(column, label, StringComparison.Ordinal))
                {
                    aliases[column] = label;
                }
            }
        }

        return aliases;
    }

    /// <summary>The plan's JSON: what a person reads and edits between the two commands.</summary>
    internal static string Write(Uri source, string dataSource, IReadOnlyList<Entry> entries)
    {
        JsonObject plan = new()
        {
            ["source"] = source.ToString(),
            ["dataSource"] = dataSource,
            ["note"] = "Every layer with a schema and a table is published by `graticula tools migrate apply`. Fill in "
                + "the ones left empty, or delete their entries; the tables must be in the data source named above.",
            ["layers"] = new JsonArray([.. entries.Select(e => (JsonNode?)new JsonObject
            {
                ["service"] = e.Service,
                ["folder"] = e.Folder,
                ["sourceLayer"] = e.LayerId,
                ["name"] = e.LayerName,

                // ADR-213: a table is published with no geometry column, and the plan says which entries are tables.
                ["kind"] = e.IsTable ? "table" : "layer",
                ["schema"] = e.Target?.Schema,
                ["table"] = e.Target?.Name,
                ["geometryColumn"] = e.Target?.GeometryColumn,
                ["objectIdColumn"] = e.Target?.ObjectIdColumn,
                ["srid"] = e.Target?.Srid,
                ["geometryType"] = e.Target?.GeometryType,
                ["note"] = e.Note,
                ["aliases"] = new JsonObject([.. e.Aliases.Select(a => new KeyValuePair<string, JsonNode?>(a.Key, a.Value))]),
                ["drawingInfo"] = e.DrawingInfo is { } drawing ? JsonNode.Parse(drawing.GetRawText()) : null,
            })]),
        };

        return plan.ToJsonString(Indented);
    }

    /// <summary>Runs <c>graticula tools migrate plan|apply</c>.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        const string Usage =
            "Usage:\n"
            + "  graticula tools migrate plan <https://host/arcgis> --to <https://graticula> --datasource <name> [--out plan.json] [--insecure]\n"
            + "  graticula tools migrate apply <plan.json> --to <https://graticula> [--sharing private|organization|public] [--insecure]\n"
            + "The target account is read from GRATICULA_USER and GRATICULA_PASSWORD, and a source token from "
            + "GRATICULA_INVENTORY_TOKEN, rather than the command line, which lands in shell history.";

        string? verb = args.Length > 2 ? args[2] : null;
        string? subject = args.Length > 3 ? args[3] : null;
        string? to = Value(args, "--to");

        if (verb is not ("plan" or "apply") || subject is null
            || !Uri.TryCreate(to, UriKind.Absolute, out Uri? target))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        using HttpClientHandler handler = new();

        if (args.Contains("--insecure"))
        {
            // Asked for by name, for a server with a self-signed certificate; never the default.
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(60) };

        try
        {
            string session = await SignInAsync(http, target, cancellationToken).ConfigureAwait(false);

            return verb == "plan"
                ? await PlanAsync(http, target, session, subject, args, output, cancellationToken).ConfigureAwait(false)
                : await ApplyAsync(http, target, session, subject, args, output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException
            or InvalidOperationException or IOException)
        {
            await output.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> PlanAsync(
        HttpClient http, Uri target, string session, string sourceUrl, string[] args, TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? source) || Value(args, "--datasource") is not { } named)
        {
            await output.WriteLineAsync("plan needs the source's address and --datasource <name>.").ConfigureAwait(false);
            return 2;
        }

        IReadOnlyList<Table> tables = await TablesAsync(http, target, session, named, cancellationToken).ConfigureAwait(false);
        string? token = Environment.GetEnvironmentVariable("GRATICULA_INVENTORY_TOKEN");
        string services = InventoryScan.ServicesRoot(source);

        List<Entry> entries = [];
        int unreadable = 0, manifests = 0, byManifest = 0;
        HashSet<string> featureServices = new(StringComparer.OrdinalIgnoreCase);
        List<(string Name, string Type)> listed = await ListAsync(http, services, token, cancellationToken).ConfigureAwait(false);

        foreach ((string name, string type) in listed)
        {
            if (type.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase))
            {
                featureServices.Add(name);
            }
        }

        foreach ((string name, string type) in listed)
        {
            // <b>One definition per layer.</b> A service published with feature access lists the same layers
            // under MapServer and FeatureServer; here one layer serves both, so the MapServer twin is skipped.
            bool map = type.Equals("MapServer", StringComparison.OrdinalIgnoreCase);

            if (!(map || type.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)) || (map && featureServices.Contains(name)))
            {
                continue;
            }

            // <b>A service listed and gone by the time it is read is skipped and said</b> — CI 2026-10-29: another test
            // deleted its own service between this walk's listing and its read, and the whole plan failed on a service
            // nobody meant to migrate. Anything other than a 404 still stops the plan.
            JsonElement document;

            try
            {
                document = await InventoryScan.GetAsync(http, $"{services}/{name}/{type}", token, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InventoryScan.GoneException gone)
            {
                await output.WriteLineAsync($"skipped {name}/{type}: listed, then gone when read ({gone.Message})")
                    .ConfigureAwait(false);
                continue;
            }
            catch (InvalidOperationException broken)
            {
                // The unreadable-layer skip below, one level up — CI 2026-11-02 again, after the layer-level skip had
                // shipped: a service whose layer is being deleted answers its own document 500 for the same moment, and
                // that read had no skip. Counted with the layers, so a fault that breaks every read still fails the plan.
                unreadable++;
                await output.WriteLineAsync($"skipped {name}/{type}: could not be read ({broken.Message})")
                    .ConfigureAwait(false);
                continue;
            }

            // The service's layers and, since ADR-213, its tables, each listed as the service lists it.
            List<(JsonElement Listed, bool IsTable)> members = [];

            foreach ((string array, bool isTable) in new[] { ("layers", false), ("tables", true) })
            {
                if (document.TryGetProperty(array, out JsonElement found) && found.ValueKind == JsonValueKind.Array)
                {
                    members.AddRange(found.EnumerateArray().Select(m => (m, isTable)));
                }
            }

            int slash = name.LastIndexOf('/');
            string? folder = slash < 0 ? null : name[..slash];
            string service = slash < 0 ? name : name[(slash + 1)..];

            List<(int Id, string Name, bool IsTable, JsonElement Layer)> read = [];

            foreach ((JsonElement listedLayer, bool isTable) in members)
            {
                if (listedLayer.TryGetProperty("subLayerIds", out JsonElement children) && children.ValueKind == JsonValueKind.Array
                    && children.GetArrayLength() > 0)
                {
                    continue;
                }

                int id = listedLayer.GetProperty("id").GetInt32();
                JsonElement layer;

                try
                {
                    layer = await InventoryScan.GetAsync(http, $"{services}/{name}/{type}/{id}", token, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InventoryScan.GoneException gone)
                {
                    // The same race one level down: the service was read and its layer is gone.
                    await output.WriteLineAsync($"skipped {name}/{type}/{id}: listed, then gone when read ({gone.Message})")
                        .ConfigureAwait(false);
                    continue;
                }
                catch (InvalidOperationException broken)
                {
                    // <b>One layer that cannot be read does not stop the plan — CI 2026-11-02.</b> A layer being deleted
                    // loses its table before its registration, and its document answers 500 ("the table behind this
                    // layer no longer exists") for that moment; a layer whose table was dropped by hand answers the same
                    // for good. Neither can be migrated, and neither is a reason not to plan the rest. Said by name; and
                    // when no layer at all could be read the plan still fails below, so a fault that breaks every read is
                    // not reported as an empty plan.
                    unreadable++;
                    await output.WriteLineAsync($"skipped {name}/{type}/{id}: could not be read ({broken.Message})")
                        .ConfigureAwait(false);
                    continue;
                }

                string layerName = layer.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? $"{id}" : $"{id}";
                read.Add((id, layerName, isTable, layer));
            }

            // D-385, ADR-081 Alternative C: the service's manifest, where the source token may read it, says which datasets
            // the service reads; each layer is matched by its dataset's name where that is certain, by its own otherwise.
            IReadOnlyList<string>? datasets = token is { Length: > 0 }
                ? await ManifestAsync(http, services, name, token, cancellationToken).ConfigureAwait(false)
                : null;
            string?[] fromManifest = datasets is { Count: > 0 }
                ? PairWithManifest([.. read.Select(r => r.Name)], datasets)
                : new string?[read.Count];
            manifests += datasets is { Count: > 0 } ? 1 : 0;

            for (int i = 0; i < read.Count; i++)
            {
                (int id, string layerName, bool isTable, JsonElement layer) = read[i];
                string? geometryType = layer.TryGetProperty("geometryType", out JsonElement g) ? g.GetString() : null;
                (Table? match, string note) = Match(layerName, tables, isTable, geometryType, fromManifest[i]);

                // A dataset the manifest named that matches nothing here is said, and the layer's own name is tried.
                if (match is null && fromManifest[i] is not null)
                {
                    (Table? byName, string nameNote) = Match(layerName, tables, isTable, geometryType);
                    (match, note) = byName is not null ? (byName, nameNote) : (null, note);
                }

                byManifest += match is not null && note.Contains("manifest", StringComparison.Ordinal) ? 1 : 0;

                entries.Add(new Entry(
                    service,
                    folder,
                    id,
                    layerName,
                    match,
                    AliasesOf(layer),
                    layer.TryGetProperty("drawingInfo", out JsonElement drawing) && drawing.ValueKind == JsonValueKind.Object
                        ? drawing.Clone()
                        : null,
                    note,
                    isTable));
            }
        }

        if (unreadable > 0 && entries.Count == 0)
        {
            await output.WriteLineAsync($"No layer of {source} could be read ({unreadable} tried); no plan was written.")
                .ConfigureAwait(false);
            return 1;
        }

        string plan = Write(source, named, entries);
        string path = Value(args, "--out") ?? "migration-plan.json";
        await File.WriteAllTextAsync(path, plan, cancellationToken).ConfigureAwait(false);

        int matched = entries.Count(e => e.Target is not null);
        await output.WriteLineAsync(
            $"{entries.Count} layer(s) read from {source}; {matched} matched a table in '{named}' "
            + (manifests > 0 ? $"({byManifest} through the manifests of {manifests} service(s), the rest by name), " : "by name, ")
            + $"{entries.Count - matched} did not.\nThe plan is {path}. Read it, fill in or delete the unmatched ones, then run "
            + "`graticula tools migrate apply`. Nothing has been published.").ConfigureAwait(false);

        foreach (Entry entry in entries.Where(e => e.Target is null))
        {
            await output.WriteLineAsync($"  {entry.Service}/{entry.LayerId} {entry.LayerName}: {entry.Note}").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> ApplyAsync(
        HttpClient http, Uri target, string session, string planPath, string[] args, TextWriter output,
        CancellationToken cancellationToken)
    {
        JsonElement plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath, cancellationToken).ConfigureAwait(false))
            .RootElement.Clone();

        string named = plan.GetProperty("dataSource").GetString()
            ?? throw new InvalidOperationException("The plan names no data source.");
        string id = await DataSourceIdAsync(http, target, session, named, cancellationToken).ConfigureAwait(false);
        string sharing = Value(args, "--sharing") ?? "private";

        int published = 0;
        int skipped = 0;
        int failed = 0;

        foreach (JsonElement layer in plan.GetProperty("layers").EnumerateArray())
        {
            string name = layer.GetProperty("name").GetString()!;
            string? schema = Text(layer, "schema");
            string? table = Text(layer, "table");

            if (schema is null || table is null)
            {
                skipped++;
                await output.WriteLineAsync($"  skipped {name}: no table in the plan.").ConfigureAwait(false);
                continue;
            }

            string service = Text(layer, "service") ?? name;
            string objectId = Text(layer, "objectIdColumn") ?? "objectid";

            // ADR-213: a table is published with no geometry column, no reference and no geometry type.
            bool isTable = string.Equals(Text(layer, "kind"), "table", StringComparison.Ordinal);

            JsonObject body = new()
            {
                ["name"] = name,
                ["dataSourceId"] = id,
                ["schemaName"] = schema,
                ["tableName"] = table,
                ["geometryColumn"] = isTable ? null : Text(layer, "geometryColumn") ?? "shape",
                ["identityColumn"] = objectId,
                ["objectIdColumn"] = objectId,
                ["srid"] = isTable ? 0 : layer.TryGetProperty("srid", out JsonElement srid) && srid.TryGetInt32(out int code) ? code : 0,
                ["geometryType"] = isTable ? null : Text(layer, "geometryType"),
                ["sharing"] = sharing,
                ["serviceName"] = service,
                ["folder"] = Text(layer, "folder"),
            };

            (int status, string said) = await SendAsync(http, HttpMethod.Post, target, "/admin/layers", session, body.ToJsonString(), cancellationToken)
                .ConfigureAwait(false);

            if (status == 409)
            {
                skipped++;
                await output.WriteLineAsync($"  skipped {name}: a layer by that name already exists.").ConfigureAwait(false);
                continue;
            }

            if (status is not (200 or 201))
            {
                failed++;
                await output.WriteLineAsync($"  refused {name}: {status} {Message(said)}").ConfigureAwait(false);
                continue;
            }

            published++;
            List<string> also = [];

            // <b>The drawing, as the source drew it.</b> Refused or partly converted is said, not fatal: the
            // layer is published and draws with a generated appearance until somebody restyles it.
            //
            // <b>`?pictures=keep` — ADR-099 §5.1.</b> A picture marker the source gives with `imageData` migrates
            // with its picture; one it gives only by `url` is kept as it was, reported as a loss and drawn as a grey
            // marker, rather than refusing the whole drawing and leaving every class generated. The target never
            // fetches the URL.
            if (layer.TryGetProperty("drawingInfo", out JsonElement drawing) && drawing.ValueKind == JsonValueKind.Object)
            {
                (int styled, string why) = await SendAsync(
                    http, HttpMethod.Put, target, $"/admin/layers/{Uri.EscapeDataString(name)}/symbology{SymbologyQuery}", session,
                    drawing.GetRawText(), cancellationToken).ConfigureAwait(false);

                also.Add(styled is 200 or 201 or 204 ? "its drawing" : $"not its drawing ({Message(why)})");
            }

            if (layer.TryGetProperty("aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Object
                && aliases.EnumerateObject().Any())
            {
                JsonObject overrides = new()
                {
                    ["overrides"] = new JsonArray([.. aliases.EnumerateObject().Select(a => (JsonNode?)new JsonObject
                    {
                        ["column"] = a.Name,
                        ["alias"] = a.Value.GetString(),
                        ["hidden"] = false,
                    })]),
                };

                (int labelled, string why) = await SendAsync(
                    http, HttpMethod.Put, target, $"/admin/layers/{Uri.EscapeDataString(name)}/fields", session,
                    overrides.ToJsonString(), cancellationToken).ConfigureAwait(false);

                also.Add(labelled is 200 or 201 or 204 ? $"{aliases.EnumerateObject().Count()} alias(es)" : $"not its aliases ({Message(why)})");
            }

            await output.WriteLineAsync(
                $"  published {name} over {schema}.{table}" + (also.Count > 0 ? $", with {string.Join(" and ", also)}" : "") + ".")
                .ConfigureAwait(false);
        }

        await output.WriteLineAsync($"{published} published, {skipped} skipped, {failed} refused.").ConfigureAwait(false);
        return failed > 0 ? 1 : 0;
    }

    private static async Task<List<(string Name, string Type)>> ListAsync(
        HttpClient http, string services, string? token, CancellationToken cancellationToken)
    {
        List<(string, string)> listed = [];
        Queue<string?> folders = new([null]);

        while (folders.Count > 0)
        {
            string? folder = folders.Dequeue();
            JsonElement listing = await InventoryScan.GetAsync(http, folder is null ? services : $"{services}/{folder}", token, cancellationToken)
                .ConfigureAwait(false);

            if (folder is null && listing.TryGetProperty("folders", out JsonElement nested))
            {
                foreach (JsonElement name in nested.EnumerateArray())
                {
                    folders.Enqueue(name.GetString());
                }
            }

            if (listing.TryGetProperty("services", out JsonElement list))
            {
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    listed.Add((entry.GetProperty("name").GetString()!, entry.GetProperty("type").GetString()!));
                }
            }
        }

        return listed;
    }

    /// <summary>
    /// The datasets a source service's manifest names, or null when it cannot be read — the token is not an
    /// administrator's, the service has none, or the server is not an ArcGIS Server. Never a reason to stop the plan.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> ManifestAsync(
        HttpClient http, string services, string name, string token, CancellationToken cancellationToken)
    {
        try
        {
            // The manifest is the MapServer's for a feature service too (measured 2026-11-05, 398 of 400 services).
            JsonElement manifest = await InventoryScan.GetAsync(
                http, $"{AdminServicesRoot(services)}/{name}.MapServer/iteminfo/manifest/manifest.json", token, cancellationToken)
                .ConfigureAwait(false);
            return DatasetsOf(manifest);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> SignInAsync(HttpClient http, Uri target, CancellationToken cancellationToken)
    {
        string? user = Environment.GetEnvironmentVariable("GRATICULA_USER");
        string? password = Environment.GetEnvironmentVariable("GRATICULA_PASSWORD");

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Set GRATICULA_USER and GRATICULA_PASSWORD to an account on the target that may publish layers.");
        }

        (int status, string body) = await SendAsync(
            http, HttpMethod.Post, target, "/rest/auth/login", null,
            new JsonObject { ["name"] = user, ["password"] = password }.ToJsonString(), cancellationToken).ConfigureAwait(false);

        return status == 200 && JsonDocument.Parse(body).RootElement.TryGetProperty("token", out JsonElement token)
            ? token.GetString()!
            : throw new InvalidOperationException($"Signing in to {target} answered {status}: {Message(body)}");
    }

    private static async Task<string> DataSourceIdAsync(
        HttpClient http, Uri target, string session, string named, CancellationToken cancellationToken)
    {
        (int status, string body) = await SendAsync(http, HttpMethod.Get, target, "/admin/datasources", session, null, cancellationToken)
            .ConfigureAwait(false);

        if (status != 200)
        {
            throw new InvalidOperationException($"The target's data sources could not be listed: {status} {Message(body)}");
        }

        return JsonDocument.Parse(body).RootElement.GetProperty("dataSources").EnumerateArray()
            .Where(s => string.Equals(s.GetProperty("name").GetString(), named, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.GetProperty("id").GetString())
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"The target has no data source named '{named}'. Register the database the source's tables are in first.");
    }

    private static async Task<IReadOnlyList<Table>> TablesAsync(
        HttpClient http, Uri target, string session, string named, CancellationToken cancellationToken)
    {
        string id = await DataSourceIdAsync(http, target, session, named, cancellationToken).ConfigureAwait(false);
        (int status, string body) = await SendAsync(
            http, HttpMethod.Get, target, $"/admin/datasources/{id}/capability", session, null, cancellationToken).ConfigureAwait(false);

        if (status != 200)
        {
            throw new InvalidOperationException($"The tables of '{named}' could not be read: {status} {Message(body)}");
        }

        return [.. JsonDocument.Parse(body).RootElement.GetProperty("tables").EnumerateArray().Select(t => new Table(
            t.GetProperty("schemaName").GetString()!,
            t.GetProperty("tableName").GetString()!,
            Text(t, "geometryColumn"),
            t.TryGetProperty("srid", out JsonElement srid) && srid.TryGetInt32(out int code) ? code : 0,
            Text(t, "geometryType"),
            Text(t, "objectIdColumn")))];
    }

    private static async Task<(int Status, string Body)> SendAsync(
        HttpClient http, HttpMethod method, Uri target, string path, string? session, string? json,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, new Uri(target, path));

        if (session is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The sentence in an error body, or the body itself.</summary>
    private static string Message(string body)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                return error.ValueKind == JsonValueKind.String ? error.GetString()!
                    : error.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? body : body;
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 300 ? body[..300] : body;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string? Value(string[] args, string flag)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
