using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Secrets;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The catalogue as a file that can be reviewed and applied — ADR-183, ADR-002 condition 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every covered table is projected whole, and its keys are translated to names.</b> A row is read as
/// <c>to_jsonb</c>, its identity, its timestamps and its parent's key are dropped, and every other reference is
/// replaced by the name of what it points at. On apply the row is rebuilt and written through
/// <c>jsonb_populate_record</c>, so PostgreSQL converts each column as it would any value. A column a migration adds
/// is carried by the next manifest without anybody remembering it; a <c>uuid</c> or <c>bytea</c> column with no rule
/// stops the export by name, so a new reference cannot put a key into the file unnoticed.
/// </para>
/// <para>
/// <b>Nothing is deleted, and nothing is written without being asked.</b> <see cref="ApplyAsync"/> plans first and
/// writes only with <c>apply</c>, in one transaction, after every refusal has been collected.
/// </para>
/// </remarks>
public static class CatalogManifest
{
    /// <summary>The format's version, written as <c>manifest</c>.</summary>
    public const int Version = 1;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,

        // Turkish letters and the like are written as themselves: the file is for reading.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// How one table becomes manifest objects: what is dropped and which <c>uuid</c> or <c>bytea</c> columns are
    /// handled. A <c>uuid</c> or <c>bytea</c> column not named here stops the export.
    /// </summary>
    private sealed record Table(string Name, string[] Drop, string[] Handled);

    private static readonly Table DataSources = new("data_source", ["id", "connection_secret"], ["id", "connection_secret"]);
    private static readonly Table Folders = new("folder", [], ["owner_principal_id"]);
    private static readonly Table Domains = new("field_domain", ["id"], ["id", "owner_principal_id"]);
    private static readonly Table Services = new("service", ["id"], ["id", "owner_principal_id", "view_of", "content_folder_id"]);
    private static readonly Table Layers = new("layer", ["id", "service_id"], ["id", "service_id", "data_source_id"]);
    private static readonly Table Groups = new("group_layer", ["id", "service_id"], ["id", "service_id"]);
    private static readonly Table Coverages = new("coverage", ["id", "service_id"], ["id", "service_id"]);
    private static readonly Table Styles = new("service_style", ["service_id"], ["service_id"]);
    private static readonly Table Sprites = new("service_sprite", ["service_id"], ["service_id", "image"]);
    private static readonly Table SharedWith = new("sharing_group_item", ["service_id", "shared_by"], ["group_id", "service_id", "shared_by"]);
    private static readonly Table Relationships = new("relationship", ["id"], ["id", "origin_layer_id", "related_layer_id"]);

    private static readonly Table[] Covered =
        [DataSources, Folders, Domains, Services, Layers, Groups, Coverages, Styles, Sprites, SharedWith, Relationships];

    /// <summary>
    /// The columns of a service's item that a manifest carries on the service — ADR-056, migration 87.
    /// </summary>
    /// <remarks>
    /// <b>The manifest's shape did not move when the store's did.</b> A service object has always carried its owner,
    /// sharing, description, tags, content folder and protection, and since migration 87 those live on its item. So the
    /// snapshot reads a service as its row with these beside it, and the writer splits them off again into the item,
    /// and a manifest written before the item existed applies to a store that has one. The service's own name is the
    /// item's title when the item is made.
    /// </remarks>
    private static readonly string[] ItemColumns =
        ["description", "tags", "owner_principal_id", "sharing", "content_folder_id", "delete_protected"];

    /// <summary>Where a covered table's rows are read from: the table itself, or for the two above, what stands for it.</summary>
    private static string SourceOf(string table) => table switch
    {
        "service" => "(select s.*, " + string.Join(", ", ItemColumns.Select(c => "i." + c))
            + " from service s join item i on i.service_id = s.id)",

        // A service's group shares, as the table that held them until migration 87 named them.
        "sharing_group_item" => "(select gi.group_id, i.service_id, gi.shared_by from item_group gi "
            + "join item i on i.id = gi.item_id where i.service_id is not null)",

        _ => table,
    };

    /// <summary>A column's name and its PostgreSQL type, as <c>information_schema</c> reports it.</summary>
    /// <param name="Name">The column's name.</param>
    /// <param name="Type">Its type, as <c>information_schema</c> spells it.</param>
    /// <param name="Identity">An identity column PostgreSQL generates <c>always</c>: written only with
    /// <c>overriding system value</c>, never updated.</param>
    /// <param name="Generated">A column computed from the others: never written.</param>
    private sealed record Column(string Name, string Type, bool Identity = false, bool Generated = false);

    /// <summary>
    /// Writes the store's catalogue as a manifest.
    /// </summary>
    /// <param name="store">The platform store.</param>
    /// <param name="secrets">Opens a data source's connection string, to say where it points.</param>
    /// <param name="locate">Where a connection string points, without its credentials — the console's summary.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ManifestException">A column the manifest has no rule for, or a source that cannot be opened.</exception>
    public static async Task<JsonObject> ExportAsync(
        NpgsqlDataSource store, SecretProtector secrets, Func<string, string> locate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(locate);

        await using NpgsqlConnection connection = await store.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        Snapshot now = await Snapshot.ReadAsync(connection, null, cancellationToken).ConfigureAwait(false);

        return now.Manifest(secrets, locate);
    }

    /// <summary>The manifest as the text a file holds: keys in ordinal order, two-space indentation, a newline at the end.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The text.</returns>
    public static string Write(JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return Canonical(manifest)!.ToJsonString(Indented) + "\n";
    }

    /// <summary>
    /// Compares a manifest with the store, and with <paramref name="apply"/> writes the difference in one transaction.
    /// </summary>
    /// <param name="store">The platform store.</param>
    /// <param name="secrets">Opens and seals data source credentials.</param>
    /// <param name="locate">Where a connection string points, without its credentials.</param>
    /// <param name="manifest">The manifest to apply.</param>
    /// <param name="credentials">A connection string for each data source that needs one, by name.</param>
    /// <param name="apply">Write the plan, rather than only report it.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>The plan, and every reason it was refused.</returns>
    public static async Task<ManifestPlan> ApplyAsync(
        NpgsqlDataSource store,
        SecretProtector secrets,
        Func<string, string> locate,
        JsonObject manifest,
        IReadOnlyDictionary<string, string> credentials,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(locate);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(credentials);

        await using NpgsqlConnection connection = await store.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Snapshot now = await Snapshot.ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        Applier applier = new(connection, transaction, now, secrets, locate, manifest, credentials);

        await applier.PlanAsync(cancellationToken).ConfigureAwait(false);

        if (applier.Refusals.Count > 0 || !apply)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new ManifestPlan(applier.Lines, applier.Refusals, Applied: false);
        }

        await applier.WriteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ManifestPlan(applier.Lines, applier.Refusals, Applied: true);
    }

    /// <summary>A copy with every object's keys in ordinal order, at every depth.</summary>
    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Canonical)]),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    private static string Text(JsonNode? node) => Canonical(node)?.ToJsonString() ?? "null";

    private static string? Str(JsonObject row, string key) =>
        row.TryGetPropertyValue(key, out JsonNode? value) && value is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static Guid? Id(JsonObject row, string key) =>
        Str(row, key) is { } s && Guid.TryParse(s, out Guid g) ? g : null;

    private static int Int(JsonObject row, string key) =>
        row.TryGetPropertyValue(key, out JsonNode? value) && value is JsonValue v && v.TryGetValue(out int i) ? i : 0;

    private static string Key(string? folder, string name) =>
        $"{(folder ?? string.Empty).ToLowerInvariant()}/{name.ToLowerInvariant()}";

    private static string Named(string? folder, string name) => folder is { Length: > 0 } ? $"{folder}/{name}" : name;

    /// <summary>
    /// Replaces a shared domain's reference, <c>{"id": …}</c>, with its name, at any depth — ADR-087's form in the
    /// store, ADR-183's in the file.
    /// </summary>
    private static JsonNode? DomainReferences(JsonNode? node, Func<Guid, string?> name, Func<string, Guid?> id, bool toNames)
    {
        switch (node)
        {
            case JsonObject o when toNames && o.Count == 1 && Id(o, "id") is { } reference:
                return new JsonObject { ["shared_domain"] = name(reference) ?? reference.ToString() };
            case JsonObject o when !toNames && o.Count == 1 && Str(o, "shared_domain") is { } shared:
                return new JsonObject { ["id"] = id(shared)?.ToString() };
            case JsonObject o:
                return new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, DomainReferences(p.Value, name, id, toNames))));
            case JsonArray a:
                return new JsonArray([.. a.Select(e => DomainReferences(e, name, id, toNames))]);
            case null:
                return null;
            default:
                return JsonNode.Parse(node.ToJsonString());
        }
    }

    /// <summary>The store as it is: every covered table's rows, its columns and the names its keys point at.</summary>
    private sealed class Snapshot
    {
        public Dictionary<string, List<Column>> Columns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<JsonObject>> Rows { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, string> Principals { get; } = [];
        public Dictionary<Guid, string> SharingGroups { get; } = [];
        public Dictionary<Guid, (Guid Owner, string Title)> ContentFolders { get; } = [];
        public int SchemaVersion { get; private set; }

        public static async Task<Snapshot> ReadAsync(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        {
            Snapshot read = new();

            foreach (Table table in Covered)
            {
                List<Column> columns = [];

                // The service's own columns and then its item's (ADR-056); the shares as item_group holds them.
                await using (NpgsqlCommand command = new(
                    "select column_name, data_type, identity_generation = 'ALWAYS', is_generated = 'ALWAYS' "
                    + "from information_schema.columns "
                    + "where table_schema = current_schema() and ("
                    + "(table_name = @t and @t not in ('sharing_group_item')) "
                    + "or (@t = 'service' and table_name = 'item' and column_name = any(@item)) "
                    + "or (@t = 'sharing_group_item' and table_name = 'item_group' and column_name in ('group_id', 'shared_by'))) "
                    + "order by table_name = 'item', ordinal_position",
                    connection, transaction))
                {
                    command.Parameters.AddWithValue("t", table.Name);
                    command.Parameters.AddWithValue("item", ItemColumns);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        columns.Add(new Column(
                            reader.GetString(0), reader.GetString(1),
                            !reader.IsDBNull(2) && reader.GetBoolean(2), !reader.IsDBNull(3) && reader.GetBoolean(3)));
                    }
                }

                foreach (Column column in columns)
                {
                    if (column.Type is "uuid" or "bytea" && !table.Handled.Contains(column.Name, StringComparer.Ordinal))
                    {
                        throw new ManifestException(
                            $"{table.Name}.{column.Name} is a {column.Type} column the manifest has no rule for, so it "
                            + "would carry a key or bytes nobody chose to carry. Add a rule for it in CatalogManifest "
                            + "and to docs/catalogue-manifest.md (ADR-183).");
                    }
                }

                if (table == SharedWith)
                {
                    columns.Insert(1, new Column("service_id", "uuid"));
                }

                read.Columns[table.Name] = columns;
                read.Rows[table.Name] = await RowsAsync(connection, transaction, table.Name, cancellationToken).ConfigureAwait(false);
            }

            await using (NpgsqlCommand command = new("select id, name from principal", connection, transaction))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    read.Principals[reader.GetGuid(0)] = reader.GetString(1);
                }
            }

            await using (NpgsqlCommand command = new("select id, name from sharing_group", connection, transaction))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    read.SharingGroups[reader.GetGuid(0)] = reader.GetString(1);
                }
            }

            await using (NpgsqlCommand command = new("select id, owner_principal_id, title from content_folder", connection, transaction))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    read.ContentFolders[reader.GetGuid(0)] = (reader.GetGuid(1), reader.GetString(2));
                }
            }

            await using (NpgsqlCommand command = new("select applied_version from platform_schema", connection, transaction))
            {
                read.SchemaVersion = Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            return read;
        }

        private static async Task<List<JsonObject>> RowsAsync(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, string table, CancellationToken cancellationToken)
        {
            List<JsonObject> rows = [];

            // The table name is one of the constants above, never a caller's text.
            await using NpgsqlCommand command = new($"select to_jsonb(t)::text from {SourceOf(table)} t", connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((JsonObject)JsonNode.Parse(reader.GetString(0))!);
            }

            return rows;
        }

        public string? Principal(Guid? id) => id is { } known && Principals.TryGetValue(known, out string? name) ? name : null;

        public JsonObject? Service(Guid id) =>
            Rows["service"].FirstOrDefault(s => Id(s, "id") == id);

        public string? DomainName(Guid id) =>
            Rows["field_domain"].FirstOrDefault(d => Id(d, "id") == id) is { } found ? Str(found, "name") : null;

        /// <summary>A row as the file holds it: its timestamps, its identity and its parent's key gone.</summary>
        public JsonObject Strip(Table table, JsonObject row)
        {
            JsonObject kept = [];

            foreach (Column column in Columns[table.Name])
            {
                if (column.Type.StartsWith("timestamp", StringComparison.Ordinal)
                    || column.Generated
                    || table.Drop.Contains(column.Name, StringComparer.Ordinal)
                    || !row.TryGetPropertyValue(column.Name, out JsonNode? value))
                {
                    continue;
                }

                kept[column.Name] = value is null ? null : JsonNode.Parse(value.ToJsonString());
            }

            return kept;
        }

        public JsonObject Manifest(SecretProtector secrets, Func<string, string> locate)
        {
            JsonArray sources = [];

            foreach (JsonObject row in Rows["data_source"].OrderBy(r => Str(r, "name"), StringComparer.Ordinal))
            {
                JsonObject source = Strip(DataSources, row);
                string connection;

                try
                {
                    connection = secrets.Unprotect(
                        Convert.FromHexString(Str(row, "connection_secret")![2..]), Int(row, "key_version"));
                }
                catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
                {
                    throw new ManifestException(
                        $"Data source '{Str(row, "name")}' cannot be opened with this server's key, so where it points "
                        + $"cannot be written. ({e.Message})", e);
                }

                // The datastore is every server's own platform database, a fact about the deployment and not the
                // catalogue, so where it points is not written: two servers of one catalogue write one file.
                if (row["is_datastore"]?.GetValue<bool>() != true)
                {
                    source["location"] = locate(connection);
                }

                sources.Add(source);
            }

            JsonArray folders = [];

            foreach (JsonObject row in Rows["folder"].OrderBy(r => Str(r, "name"), StringComparer.Ordinal))
            {
                JsonObject folder = Strip(Folders, row);
                folder.Remove("owner_principal_id");
                folder["owner"] = Principal(Id(row, "owner_principal_id"));
                folders.Add(folder);
            }

            JsonArray domains = [];

            foreach (JsonObject row in Rows["field_domain"].OrderBy(r => Str(r, "name"), StringComparer.Ordinal))
            {
                JsonObject domain = Strip(Domains, row);
                domain.Remove("owner_principal_id");
                domain["owner"] = Principal(Id(row, "owner_principal_id"));

                // The definition repeats the id on some paths; the name is the identity here.
                if (domain["definition"] is JsonObject definition)
                {
                    definition.Remove("id");
                }

                domains.Add(domain);
            }

            JsonArray services = [];

            foreach (JsonObject row in Rows["service"]
                .OrderBy(r => Key(Str(r, "folder"), Str(r, "name")!), StringComparer.Ordinal))
            {
                services.Add(ServiceObject(row));
            }

            JsonArray relationships = [];

            foreach (JsonObject row in Rows["relationship"].OrderBy(r => Str(r, "name"), StringComparer.Ordinal))
            {
                JsonObject relationship = Strip(Relationships, row);
                relationship.Remove("origin_layer_id");
                relationship.Remove("related_layer_id");
                relationship["origin"] = LayerReference(Id(row, "origin_layer_id"));
                relationship["related"] = LayerReference(Id(row, "related_layer_id"));
                relationships.Add(relationship);
            }

            return new JsonObject
            {
                ["manifest"] = Version,
                ["schema"] = SchemaVersion,
                ["data_sources"] = sources,
                ["folders"] = folders,
                ["shared_domains"] = domains,
                ["services"] = services,
                ["relationships"] = relationships,
            };
        }

        public JsonObject ServiceObject(JsonObject row)
        {
            Guid id = Id(row, "id")!.Value;
            JsonObject service = Strip(Services, row);

            service.Remove("owner_principal_id");
            service["owner"] = Principal(Id(row, "owner_principal_id"));

            service.Remove("view_of");
            service["view_of"] = Id(row, "view_of") is { } viewed && Service(viewed) is { } of
                ? new JsonObject { ["folder"] = Str(of, "folder"), ["name"] = Str(of, "name") }
                : null;

            service.Remove("content_folder_id");
            service["content_folder"] = Id(row, "content_folder_id") is { } folder
                && ContentFolders.TryGetValue(folder, out (Guid Owner, string Title) held)
                    ? held.Title
                    : null;

            JsonArray layers = [];

            foreach (JsonObject layerRow in Rows["layer"].Where(l => Id(l, "service_id") == id).OrderBy(l => Int(l, "layer_index")))
            {
                JsonObject layer = Strip(Layers, layerRow);
                layer.Remove("data_source_id");
                layer["data_source"] = Rows["data_source"].FirstOrDefault(s => Id(s, "id") == Id(layerRow, "data_source_id"))
                    is { } source ? Str(source, "name") : null;

                if (layer["field_overrides"] is JsonNode overrides)
                {
                    layer["field_overrides"] = DomainReferences(overrides, DomainName, _ => null, toNames: true);
                }

                layers.Add(layer);
            }

            service["layers"] = layers;
            service["groups"] = new JsonArray([.. Rows["group_layer"]
                .Where(g => Id(g, "service_id") == id).OrderBy(g => Int(g, "layer_index"))
                .Select(g => (JsonNode)Strip(Groups, g))]);
            service["coverage"] = Rows["coverage"].FirstOrDefault(c => Id(c, "service_id") == id) is { } coverage
                ? Strip(Coverages, coverage)
                : null;
            service["styles"] = new JsonArray([.. Rows["service_style"]
                .Where(s => Id(s, "service_id") == id).OrderBy(s => Str(s, "name")!.ToLowerInvariant(), StringComparer.Ordinal)
                .Select(s => (JsonNode)Strip(Styles, s))]);
            service["sprites"] = new JsonArray([.. Rows["service_sprite"]
                .Where(s => Id(s, "service_id") == id).OrderBy(s => Int(s, "pixel_ratio"))
                .Select(s =>
                {
                    JsonObject sprite = Strip(Sprites, s);
                    sprite["image"] = Convert.ToBase64String(Convert.FromHexString(Str(s, "image")![2..]));
                    return (JsonNode)sprite;
                })]);
            service["shared_with"] = new JsonArray([.. Rows["sharing_group_item"]
                .Where(s => Id(s, "service_id") == id)
                .Select(s => SharingGroups.TryGetValue(Id(s, "group_id")!.Value, out string? g) ? g : null)
                .Where(g => g is not null).OrderBy(g => g, StringComparer.Ordinal)
                .Select(g => (JsonNode)JsonValue.Create(g)!)]);

            return service;
        }

        public JsonObject? LayerReference(Guid? id)
        {
            if (id is not { } known || Rows["layer"].FirstOrDefault(l => Id(l, "id") == known) is not { } layer
                || Id(layer, "service_id") is not { } owner || Service(owner) is not { } service)
            {
                return null;
            }

            return new JsonObject
            {
                ["folder"] = Str(service, "folder"),
                ["service"] = Str(service, "name"),
                ["layer"] = Int(layer, "layer_index"),
            };
        }
    }

    /// <summary>Compares a manifest with a snapshot, collects every refusal, and writes the difference.</summary>
    private sealed class Applier(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Snapshot now,
        SecretProtector secrets,
        Func<string, string> locate,
        JsonObject manifest,
        IReadOnlyDictionary<string, string> credentials)
    {
        public List<string> Lines { get; } = [];
        public List<string> Refusals { get; } = [];

        /// <summary>Each data source the manifest names, and the store's row it is, if any.</summary>
        private readonly Dictionary<string, (JsonObject Wanted, Guid? Existing, string? Connection, bool Reseal)> _sources =
            new(StringComparer.Ordinal);

        private readonly List<Action> _work = [];

        private JsonArray Array(string key) => manifest[key] as JsonArray ?? [];

        private Guid? PrincipalId(string? name) =>
            name is null ? null : now.Principals.FirstOrDefault(p => string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase)) is { Value: not null } p ? p.Key : null;

        private bool Owner(string? name, string what)
        {
            if (name is null || PrincipalId(name) is not null)
            {
                return true;
            }

            Refusals.Add($"{what} is owned by '{name}', and this server has no principal by that name.");
            return false;
        }

        public async Task PlanAsync(CancellationToken cancellationToken)
        {
            if (Int(manifest, "manifest") != Version)
            {
                Refusals.Add($"This is manifest format {Int(manifest, "manifest")}; this server reads format {Version}.");
                return;
            }

            if (Int(manifest, "schema") > now.SchemaVersion)
            {
                Refusals.Add(
                    $"The manifest was written from schema {Int(manifest, "schema")} and this store is at "
                    + $"{now.SchemaVersion}. Migrate this store first; a newer manifest may name columns it does not have.");
            }

            JsonObject current = now.Manifest(secrets, locate);

            PlanSources();
            PlanFolders(current);
            PlanDomains(current);
            PlanServices(current);
            PlanRelationships(current);

            await CheckTablesAsync(cancellationToken).ConfigureAwait(false);
        }

        private void Say(string verb, string what) => Lines.Add($"{verb,-10} {what}");

        private void PlanSources()
        {
            Guid? datastore = now.Rows["data_source"].FirstOrDefault(r => r["is_datastore"]?.GetValue<bool>() == true) is { } d
                ? Id(d, "id")
                : null;

            foreach (JsonObject wanted in Array("data_sources").OfType<JsonObject>())
            {
                string name = Str(wanted, "name") ?? string.Empty;
                bool isDatastore = wanted["is_datastore"]?.GetValue<bool>() == true;

                // <b>The datastore is this server's own, whatever the other one called it.</b> There is exactly one,
                // and reproducing a configuration on another server means its hosted tables are in that server's
                // datastore, which is a different database by design. So it is matched by role, and where it points is
                // not compared.
                JsonObject? existing = isDatastore && datastore is { } own
                    ? now.Rows["data_source"].First(r => Id(r, "id") == own)
                    : now.Rows["data_source"].FirstOrDefault(r => string.Equals(Str(r, "name"), name, StringComparison.Ordinal));

                credentials.TryGetValue(name, out string? supplied);

                // <b>The datastore is not the catalogue's to move.</b> Every server registers its own platform
                // database as the datastore when it starts (`EnsureDatastoreAsync`), so a credential written here would
                // be overwritten at the next start, and a table looked for through it would be looked for in the wrong
                // database. Measured 2026-10-15: a store applied with the other server's datastore credential passed
                // every check and served nothing, because its server read its own database.
                if (isDatastore)
                {
                    if (supplied is not null)
                    {
                        Refusals.Add(
                            $"Data source '{name}' is the datastore, which is always this server's own platform database "
                            + "(Graticula__PlatformStore). It takes no credential; remove it from the --credentials file.");
                        continue;
                    }

                    if (existing is null)
                    {
                        Refusals.Add(
                            "This store has no datastore yet. A server registers its own database as the datastore when it "
                            + "starts: start one on this store once, then apply.");
                        continue;
                    }

                    _sources[name] = (wanted, Id(existing, "id"), OpenSource(existing), Reseal: false);
                    Say(string.Equals(Str(existing, "name"), name, StringComparison.Ordinal) ? "unchanged" : "map",
                        $"data source {name} is this server's datastore, at {(OpenSource(existing) is { } at ? locate(at) : "?")}");
                    continue;
                }

                if (supplied is not null && !string.Equals(locate(supplied), Str(wanted, "location"), StringComparison.Ordinal)
                    && !isDatastore)
                {
                    Refusals.Add(
                        $"The credential given for data source '{name}' points at {locate(supplied)}, and the manifest "
                        + $"says {Str(wanted, "location")}.");
                    continue;
                }

                if (existing is null)
                {
                    if (supplied is null)
                    {
                        Refusals.Add(
                            $"Data source '{name}' ({Str(wanted, "location")}) is not on this server. Give its connection "
                            + "string in the --credentials file; a manifest names a credential and never carries one.");
                        continue;
                    }

                    _sources[name] = (wanted, null, supplied, true);
                    Say("create", $"data source {name} → {Str(wanted, "location")}");
                    continue;
                }

                string? held = OpenSource(existing);
                string heldAt = held is null ? "?" : locate(held);

                if (!isDatastore && !string.Equals(heldAt, Str(wanted, "location"), StringComparison.Ordinal) && supplied is null)
                {
                    Refusals.Add(
                        $"Data source '{name}' points at {heldAt} here and at {Str(wanted, "location")} in the manifest. "
                        + "Give its new connection string in the --credentials file to move it.");
                    continue;
                }

                bool same = string.Equals(Str(existing, "kind"), Str(wanted, "kind"), StringComparison.Ordinal)
                    && supplied is null;

                _sources[name] = (wanted, Id(existing, "id"), supplied ?? held, supplied is not null);

                Say(same ? "unchanged" : "update", $"data source {name}");
            }

            HashSet<Guid> matched = [.. _sources.Values.Where(v => v.Existing is not null).Select(v => v.Existing!.Value)];

            foreach (JsonObject kept in now.Rows["data_source"].Where(r => !matched.Contains(Id(r, "id")!.Value)))
            {
                Say("kept", $"data source {Str(kept, "name")} (not in the manifest)");
            }

            foreach ((string name, var source) in _sources.ToList())
            {
                (JsonObject wanted, Guid? existing, string? connectionString, bool reseal) = (source.Wanted, source.Existing, source.Connection, source.Reseal);

                if (existing is not null && !reseal && string.Equals(
                    Str(now.Rows["data_source"].First(r => Id(r, "id") == existing), "kind"), Str(wanted, "kind"), StringComparison.Ordinal))
                {
                    continue;
                }

                Guid id = existing ?? Guid.NewGuid();
                _sources[name] = (wanted, id, connectionString, reseal);

                _work.Add(() =>
                {
                    JsonObject row = new()
                    {
                        ["id"] = id.ToString(),
                        ["name"] = existing is null ? name : Str(now.Rows["data_source"].First(r => Id(r, "id") == existing), "name"),
                        ["kind"] = Str(wanted, "kind"),
                        ["is_datastore"] = wanted["is_datastore"]?.GetValue<bool>() == true,
                    };

                    if (reseal)
                    {
                        row["connection_secret"] = "\\x" + Convert.ToHexString(secrets.Protect(connectionString!));
                        row["key_version"] = secrets.KeyVersion;
                    }

                    Pending.Add(("data_source", row, existing is null ? null : "id"));
                });
            }
        }

        private string? OpenSource(JsonObject row)
        {
            try
            {
                return secrets.Unprotect(Convert.FromHexString(Str(row, "connection_secret")![2..]), Int(row, "key_version"));
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
            {
                return null;
            }
        }

        private Guid? SourceId(string? name) =>
            name is not null && _sources.TryGetValue(name, out var s) ? s.Existing : null;

        private void PlanFolders(JsonObject current)
        {
            foreach (JsonObject wanted in Array("folders").OfType<JsonObject>())
            {
                string name = Str(wanted, "name") ?? string.Empty;

                if (!Owner(Str(wanted, "owner"), $"Folder '{name}'"))
                {
                    continue;
                }

                JsonObject? existing = current["folders"]!.AsArray().OfType<JsonObject>()
                    .FirstOrDefault(f => string.Equals(Str(f, "name"), name, StringComparison.OrdinalIgnoreCase));

                if (existing is not null && Text(existing) == Text(wanted))
                {
                    Say("unchanged", $"folder {name}");
                    continue;
                }

                Say(existing is null ? "create" : "update", $"folder {name}");
                JsonObject row = Rebuild(Folders, wanted, ["owner"]);
                row["owner_principal_id"] = PrincipalId(Str(wanted, "owner"))?.ToString();
                _work.Add(() => Pending.Add(("folder", row, existing is null ? null : "folder")));
            }
        }

        private readonly Dictionary<string, Guid> _domains = new(StringComparer.OrdinalIgnoreCase);

        private void PlanDomains(JsonObject current)
        {
            foreach (JsonObject row in now.Rows["field_domain"])
            {
                _domains[Str(row, "name")!] = Id(row, "id")!.Value;
            }

            foreach (JsonObject wanted in Array("shared_domains").OfType<JsonObject>())
            {
                string name = Str(wanted, "name") ?? string.Empty;

                if (!Owner(Str(wanted, "owner"), $"Shared domain '{name}'"))
                {
                    continue;
                }

                JsonObject? existing = current["shared_domains"]!.AsArray().OfType<JsonObject>()
                    .FirstOrDefault(d => string.Equals(Str(d, "name"), name, StringComparison.OrdinalIgnoreCase));

                if (existing is not null && Text(existing) == Text(wanted))
                {
                    Say("unchanged", $"shared domain {name}");
                    continue;
                }

                Guid id = existing is null ? Guid.NewGuid() : _domains[name];
                _domains[name] = id;
                Say(existing is null ? "create" : "update", $"shared domain {name}");

                JsonObject row = Rebuild(Domains, wanted, ["owner"]);
                row["id"] = id.ToString();
                row["owner_principal_id"] = PrincipalId(Str(wanted, "owner"))?.ToString();
                _work.Add(() => Pending.Add(("field_domain", row, existing is null ? null : "id")));
            }
        }

        /// <summary>The services the manifest names, by folder and name, with the id each will have.</summary>
        private readonly Dictionary<string, Guid> _services = new(StringComparer.Ordinal);

        /// <summary>The layers, by service key and index, with the id each will have.</summary>
        private readonly Dictionary<(string Service, int Index), Guid> _layers = [];

        /// <summary>Each layer's table to look for, by the data source it is in.</summary>
        private readonly List<(string Source, string Schema, string Table, string Layer)> _tables = [];

        private void PlanServices(JsonObject current)
        {
            foreach (JsonObject row in now.Rows["service"])
            {
                _services[Key(Str(row, "folder"), Str(row, "name")!)] = Id(row, "id")!.Value;
            }

            foreach (JsonObject row in now.Rows["layer"])
            {
                if (Id(row, "service_id") is { } owner && now.Service(owner) is { } service)
                {
                    _layers[(Key(Str(service, "folder"), Str(service, "name")!), Int(row, "layer_index"))] = Id(row, "id")!.Value;
                }
            }

            List<JsonObject> wantedServices = [.. Array("services").OfType<JsonObject>()];

            foreach (JsonObject wanted in wantedServices)
            {
                string key = Key(Str(wanted, "folder"), Str(wanted, "name") ?? string.Empty);

                if (!_services.ContainsKey(key))
                {
                    _services[key] = Guid.NewGuid();
                }
            }

            foreach (JsonObject wanted in wantedServices)
            {
                PlanService(current, wanted);
            }

            foreach (JsonObject kept in current["services"]!.AsArray().OfType<JsonObject>()
                .Where(s => !wantedServices.Any(w => Key(Str(w, "folder"), Str(w, "name")!) == Key(Str(s, "folder"), Str(s, "name")!))))
            {
                Say("kept", $"service {Named(Str(kept, "folder"), Str(kept, "name")!)} (not in the manifest)");
            }
        }

        private static readonly string[] Children = ["layers", "groups", "coverage", "styles", "sprites", "shared_with"];

        private void PlanService(JsonObject current, JsonObject wanted)
        {
            string folder = Str(wanted, "folder") ?? string.Empty;
            string name = Str(wanted, "name") ?? string.Empty;
            string key = Key(folder, name);
            string label = Named(folder, name);
            Guid id = _services[key];

            JsonObject? existing = current["services"]!.AsArray().OfType<JsonObject>()
                .FirstOrDefault(s => Key(Str(s, "folder"), Str(s, "name")!) == key);
            bool ownerKnown = Owner(Str(wanted, "owner"), $"Service '{label}'");

            if (wanted["view_of"] is JsonObject view && !_services.ContainsKey(Key(Str(view, "folder"), Str(view, "name") ?? string.Empty)))
            {
                Refusals.Add($"Service '{label}' is a view of '{Named(Str(view, "folder"), Str(view, "name")!)}', which is neither here nor in the manifest.");
            }

            JsonObject own = (JsonObject)JsonNode.Parse(wanted.ToJsonString())!;
            JsonObject? had = existing is null ? null : (JsonObject)JsonNode.Parse(existing.ToJsonString())!;

            foreach (string child in Children)
            {
                own.Remove(child);
                had?.Remove(child);
            }

            if (had is not null && Text(had) == Text(own))
            {
                Say("unchanged", $"service {label}");
            }
            else if (ownerKnown)
            {
                Say(had is null ? "create" : "update", $"service {label}");

                JsonObject row = Rebuild(Services, own, ["owner", "view_of", "content_folder"]);
                row["id"] = id.ToString();
                string? ownerName = Str(wanted, "owner");
                Guid? owner = PrincipalId(ownerName);
                row["owner_principal_id"] = owner?.ToString();

                // A layer index once used is never handed out again (clients remember them), so the counter only
                // moves forward, whichever of the two is ahead.
                if (existing is not null)
                {
                    int held = Int(now.Rows["service"].First(s => Id(s, "id") == id), "next_layer_index");
                    row["next_layer_index"] = Math.Max(held, Int(own, "next_layer_index"));
                }

                string? title = Str(wanted, "content_folder");
                JsonObject? viewOf = wanted["view_of"] as JsonObject;

                _work.Add(() =>
                {
                    if (title is not null && owner is { } who)
                    {
                        row["content_folder_id"] = ContentFolder(who, title).ToString();
                    }

                    // The view's target is written once every service exists — see WriteAsync's second pass.
                    row.Remove("view_of");
                    Pending.Add(("service", row, existing is null ? null : "id"));

                    if (viewOf is not null)
                    {
                        Later.Add(("service", new JsonObject
                        {
                            ["id"] = id.ToString(),
                            ["view_of"] = _services[Key(Str(viewOf, "folder"), Str(viewOf, "name")!)].ToString(),
                        }, "id"));
                    }
                });
            }

            // Groups before layers: a layer's parent_layer_index is a foreign key to its group.
            PlanList(existing, wanted, "groups", Groups, "layer_index", label, id, "group");
            PlanLayers(existing, wanted, key, label, id);
            PlanCoverage(existing, wanted, label, id);
            PlanList(existing, wanted, "styles", Styles, "name", label, id, "style");
            PlanList(existing, wanted, "sprites", Sprites, "pixel_ratio", label, id, "sprite");
            PlanSharing(existing, wanted, label, id);
        }

        private void PlanLayers(JsonObject? existing, JsonObject wanted, string key, string label, Guid service)
        {
            foreach (JsonObject layer in (wanted["layers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                int index = Int(layer, "layer_index");
                string name = Str(layer, "name") ?? string.Empty;
                string source = Str(layer, "data_source") ?? string.Empty;

                // A source the manifest lists and that was refused has said so already; one it does not list is this.
                bool listed = Array("data_sources").OfType<JsonObject>().Any(d => Str(d, "name") == source);

                if (!listed && now.Rows["data_source"].All(r => Str(r, "name") != source))
                {
                    Refusals.Add($"Layer '{name}' in '{label}' reads data source '{source}', which the manifest does not list.");
                    continue;
                }

                _tables.Add((source, Str(layer, "schema_name") ?? string.Empty, Str(layer, "table_name") ?? string.Empty, $"{label}/{index} {name}"));

                JsonObject? had = (existing?["layers"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(l => Int(l, "layer_index") == index);

                if (had is not null && Text(had) == Text(layer))
                {
                    Say("unchanged", $"layer {label}/{index} {name}");
                    continue;
                }

                if (layer["field_overrides"] is JsonNode overrides)
                {
                    foreach (string missing in SharedDomainNames(overrides).Where(n => !_domains.ContainsKey(n)).Distinct(StringComparer.Ordinal))
                    {
                        Refusals.Add($"Layer '{name}' in '{label}' uses shared domain '{missing}', which is neither here nor in the manifest.");
                    }
                }

                Say(had is null ? "create" : "update", $"layer {label}/{index} {name}");

                Guid id = _layers.TryGetValue((key, index), out Guid known) ? known : Guid.NewGuid();
                _layers[(key, index)] = id;

                _work.Add(() =>
                {
                    JsonObject row = Rebuild(Layers, layer, ["data_source"]);
                    row["id"] = id.ToString();
                    row["service_id"] = service.ToString();
                    row["data_source_id"] = (SourceId(source)
                        ?? Id(now.Rows["data_source"].First(r => Str(r, "name") == source), "id"))!.Value.ToString();

                    if (row["field_overrides"] is JsonNode written)
                    {
                        row["field_overrides"] = DomainReferences(written, _ => null,
                            n => _domains.TryGetValue(n, out Guid d) ? d : null, toNames: false);
                    }

                    Pending.Add(("layer", row, had is null ? null : "id"));
                });
            }
        }

        private static IEnumerable<string> SharedDomainNames(JsonNode? node) => node switch
        {
            JsonObject o when o.Count == 1 && Str(o, "shared_domain") is { } name => [name],
            JsonObject o => o.SelectMany(p => SharedDomainNames(p.Value)),
            JsonArray a => a.SelectMany(SharedDomainNames),
            _ => [],
        };

        private void PlanList(
            JsonObject? existing, JsonObject wanted, string key, Table table, string by, string label, Guid service, string what)
        {
            foreach (JsonObject item in (wanted[key] as JsonArray ?? []).OfType<JsonObject>())
            {
                string identity = item[by]?.ToJsonString() ?? "null";
                JsonObject? had = (existing?[key] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(h => string.Equals(h[by]?.ToJsonString(), identity, StringComparison.OrdinalIgnoreCase));

                if (had is not null && Text(had) == Text(item))
                {
                    Say("unchanged", $"{what} {label} {identity}");
                    continue;
                }

                Say(had is null ? "create" : "update", $"{what} {label} {identity}");

                _work.Add(() =>
                {
                    JsonObject row = Rebuild(table, item, []);
                    row["service_id"] = service.ToString();

                    if (table == Sprites)
                    {
                        row["image"] = "\\x" + Convert.ToHexString(Convert.FromBase64String(Str(item, "image") ?? string.Empty));
                    }

                    if (table == Groups)
                    {
                        Guid? held = now.Rows["group_layer"]
                            .FirstOrDefault(g => Id(g, "service_id") == service && Int(g, "layer_index") == Int(item, "layer_index")) is { } g
                            ? Id(g, "id")
                            : null;
                        row["id"] = (held ?? Guid.NewGuid()).ToString();
                        Pending.Add(("group_layer", row, held is null ? null : "id"));
                        return;
                    }

                    Pending.Add((table.Name, row, had is null ? null : table == Styles ? "style" : "sprite"));
                });
            }
        }

        private void PlanCoverage(JsonObject? existing, JsonObject wanted, string label, Guid service)
        {
            if (wanted["coverage"] is not JsonObject coverage)
            {
                return;
            }

            JsonObject? had = existing?["coverage"] as JsonObject;

            if (had is not null && Text(had) == Text(coverage))
            {
                Say("unchanged", $"coverage {label}");
                return;
            }

            Say(had is null ? "create" : "update", $"coverage {label}");

            _work.Add(() =>
            {
                JsonObject row = Rebuild(Coverages, coverage, []);
                Guid? held = now.Rows["coverage"].FirstOrDefault(c => Id(c, "service_id") == service) is { } c ? Id(c, "id") : null;
                row["id"] = (held ?? Guid.NewGuid()).ToString();
                row["service_id"] = service.ToString();
                Pending.Add(("coverage", row, held is null ? null : "id"));
            });
        }

        private void PlanSharing(JsonObject? existing, JsonObject wanted, string label, Guid service)
        {
            HashSet<string> had = [.. (existing?["shared_with"] as JsonArray ?? []).Select(g => g!.GetValue<string>())];

            foreach (string group in (wanted["shared_with"] as JsonArray ?? []).Select(g => g!.GetValue<string>()))
            {
                if (had.Contains(group))
                {
                    continue;
                }

                if (now.SharingGroups.FirstOrDefault(g => string.Equals(g.Value, group, StringComparison.Ordinal)) is not { Value: not null } found)
                {
                    Refusals.Add($"Service '{label}' is shared with group '{group}', and this server has no group by that name.");
                    continue;
                }

                Say("share", $"service {label} with group {group}");
                _work.Add(() => Pending.Add(("sharing_group_item", new JsonObject
                {
                    ["group_id"] = found.Key.ToString(),
                    ["service_id"] = service.ToString(),
                }, null)));
            }
        }

        private void PlanRelationships(JsonObject current)
        {
            foreach (JsonObject wanted in Array("relationships").OfType<JsonObject>())
            {
                string name = Str(wanted, "name") ?? string.Empty;
                Guid? origin = LayerOf(wanted["origin"] as JsonObject);
                Guid? related = LayerOf(wanted["related"] as JsonObject);

                if (origin is null || related is null)
                {
                    Refusals.Add($"Relationship '{name}' names a layer that is neither here nor in the manifest.");
                    continue;
                }

                JsonObject? existing = current["relationships"]!.AsArray().OfType<JsonObject>()
                    .FirstOrDefault(r => string.Equals(Str(r, "name"), name, StringComparison.Ordinal));

                if (existing is not null && Text(existing) == Text(wanted))
                {
                    Say("unchanged", $"relationship {name}");
                    continue;
                }

                Say(existing is null ? "create" : "update", $"relationship {name}");
                Guid id = now.Rows["relationship"].FirstOrDefault(r => Str(r, "name") == name) is { } held ? Id(held, "id")!.Value : Guid.NewGuid();

                _work.Add(() =>
                {
                    JsonObject row = Rebuild(Relationships, wanted, ["origin", "related"]);
                    row["id"] = id.ToString();
                    row["origin_layer_id"] = origin.Value.ToString();
                    row["related_layer_id"] = related.Value.ToString();
                    Later.Add(("relationship", row, existing is null ? null : "id"));
                });
            }
        }

        private Guid? LayerOf(JsonObject? reference) =>
            reference is not null && _layers.TryGetValue(
                (Key(Str(reference, "folder"), Str(reference, "service") ?? string.Empty), Int(reference, "layer")), out Guid id)
                ? id
                : null;

        /// <summary>Every layer's table or view, looked for in its own source — a manifest is configuration, not data.</summary>
        private async Task CheckTablesAsync(CancellationToken cancellationToken)
        {
            foreach (IGrouping<string, (string Source, string Schema, string Table, string Layer)> bySource in _tables.GroupBy(t => t.Source))
            {
                string? connectionString = _sources.TryGetValue(bySource.Key, out var planned) ? planned.Connection : null;
                string? kind = _sources.TryGetValue(bySource.Key, out var p) ? Str(p.Wanted, "kind") : null;

                if (connectionString is null || !string.Equals(kind, "postgis", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    await using NpgsqlConnection source = new(connectionString);
                    await source.OpenAsync(cancellationToken).ConfigureAwait(false);

                    foreach ((_, string schema, string table, string layer) in bySource)
                    {
                        await using NpgsqlCommand command = new(
                            "select to_regclass(quote_ident(@s) || '.' || quote_ident(@t)) is not null", source);
                        command.Parameters.AddWithValue("s", schema);
                        command.Parameters.AddWithValue("t", table);

                        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                        {
                            Refusals.Add(
                                $"Layer {layer} reads {schema}.{table} in data source '{bySource.Key}', and there is no such "
                                + "table or view there. A manifest carries configuration, not data: the table has to be "
                                + "in the source first.");
                        }
                    }
                }
                catch (NpgsqlException e)
                {
                    Refusals.Add($"Data source '{bySource.Key}' could not be reached to look for its layers' tables: {e.Message}");
                }
            }
        }

        /// <summary>The manifest's object as a row: references translated back, keys checked against the table.</summary>
        private JsonObject Rebuild(Table table, JsonObject item, string[] translated)
        {
            JsonObject row = [];
            HashSet<string> columns = [.. now.Columns[table.Name].Select(c => c.Name)];

            foreach ((string key, JsonNode? value) in item)
            {
                if (translated.Contains(key, StringComparer.Ordinal) || Children.Contains(key, StringComparer.Ordinal)
                    || (table == DataSources && key == "location"))
                {
                    continue;
                }

                if (!columns.Contains(key))
                {
                    Refusals.Add($"The manifest gives {table.Name}.{key}, and this store has no such column.");
                    continue;
                }

                row[key] = value is null ? null : JsonNode.Parse(value.ToJsonString());
            }

            return row;
        }

        private readonly Dictionary<(Guid, string), Guid> _contentFolders = [];

        private Guid ContentFolder(Guid owner, string title)
        {
            if (now.ContentFolders.FirstOrDefault(f => f.Value.Owner == owner && f.Value.Title == title) is { Value.Title: not null } found)
            {
                return found.Key;
            }

            if (!_contentFolders.TryGetValue((owner, title), out Guid made))
            {
                made = Guid.NewGuid();
                _contentFolders[(owner, title)] = made;
                Pending.Insert(0, ("content_folder", new JsonObject
                {
                    ["id"] = made.ToString(),
                    ["owner_principal_id"] = owner.ToString(),
                    ["title"] = title,
                }, null));
            }

            return made;
        }

        /// <summary>
        /// A service row's item columns written to its item, and a service's group share written as an item's — ADR-056,
        /// migration 87; see <see cref="ItemColumns"/>.
        /// </summary>
        private static void Split(List<(string Table, JsonObject Row, string? Match)> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                (string table, JsonObject row, string? match) = rows[i];

                if (table == "sharing_group_item")
                {
                    rows[i] = ("item_group", new JsonObject
                    {
                        ["group_id"] = row["group_id"]?.DeepClone(),
                        ["item_id"] = row["service_id"]?.DeepClone(),
                    }, match);
                    continue;
                }

                if (table != "service")
                {
                    continue;
                }

                JsonObject item = new() { ["id"] = row["id"]?.DeepClone() };

                foreach (string column in ItemColumns)
                {
                    if (row.TryGetPropertyValue(column, out JsonNode? value))
                    {
                        item[column] = value?.DeepClone();
                        row.Remove(column);
                    }
                }

                if (match is null)
                {
                    item["kind"] = "service";
                    item["title"] = row["name"]?.DeepClone();
                    item["service_id"] = row["id"]?.DeepClone();
                }
                else if (item.Count == 1)
                {
                    continue;
                }

                rows.Insert(++i, ("item", item, match));
            }
        }

        /// <summary>The rows to write in order: table, row, and how an existing one is matched (null to insert).</summary>
        private List<(string Table, JsonObject Row, string? Match)> Pending { get; } = [];

        /// <summary>Rows that point at other rows of the same pass, written after it.</summary>
        private List<(string Table, JsonObject Row, string? Match)> Later { get; } = [];

        public async Task WriteAsync(CancellationToken cancellationToken)
        {
            foreach (Action step in _work)
            {
                step();
            }

            Split(Pending);
            Split(Later);

            HashSet<string> identities = new(StringComparer.Ordinal);

            foreach ((string table, JsonObject row, string? match) in Pending.Concat(Later))
            {
                // <b>An identity column is a number a client already holds</b> — a relationship's is the id an
                // ArcGIS client follows — so an insert keeps it, with `overriding system value`, and an update leaves
                // it alone, which is the only thing PostgreSQL allows for one generated `always`.
                List<Column> known = now.Columns.TryGetValue(table, out List<Column>? c) ? c : [];
                bool overriding = match is null && known.Any(k => k.Identity && row.ContainsKey(k.Name));
                string[] columns = [.. row.Select(p => p.Key)
                    .Where(k => match is null || !known.Any(x => x.Identity && x.Name == k))];
                string list = string.Join(", ", columns.Select(c => $"\"{c}\""));
                string populated = $"select {list} from jsonb_populate_record(null::{table}, @row::jsonb)";

                foreach (Column identity in known.Where(k => k.Identity && overriding))
                {
                    identities.Add($"{table}.{identity.Name}");
                }

                string sql = match switch
                {
                    null when overriding => $"insert into {table} ({list}) overriding system value {populated}",
                    null => $"insert into {table} ({list}) {populated}",
                    "id" => $"update {table} set ({list}) = ({populated}) where id = (@row::jsonb ->> 'id')::uuid",
                    "folder" => $"update {table} set ({list}) = ({populated}) where lower(name) = lower(@row::jsonb ->> 'name')",
                    "style" => $"update {table} set ({list}) = ({populated}) where service_id = (@row::jsonb ->> 'service_id')::uuid and lower(name) = lower(@row::jsonb ->> 'name')",
                    "sprite" => $"update {table} set ({list}) = ({populated}) where service_id = (@row::jsonb ->> 'service_id')::uuid and pixel_ratio = (@row::jsonb ->> 'pixel_ratio')::smallint",
                    _ => throw new InvalidOperationException(match),
                };

                await using NpgsqlCommand command = new(sql, connection, transaction);
                command.Parameters.AddWithValue("row", row.ToJsonString());
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // A number written past the sequence would be handed out again by the next insert, so the sequence is moved
            // to the largest number the table holds.
            foreach (string identity in identities)
            {
                string[] parts = identity.Split('.');

                await using NpgsqlCommand advance = new(
                    $"select setval(pg_get_serial_sequence('{parts[0]}', '{parts[1]}'), "
                    + $"(select coalesce(max(\"{parts[1]}\"), 0) + 1 from {parts[0]}), false)",
                    connection, transaction);
                await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>What an apply would do or did, and why it was refused.</summary>
/// <param name="Lines">One line per object: create, update, unchanged, kept, map or share.</param>
/// <param name="Refusals">Every reason the apply cannot go ahead; empty when it can.</param>
/// <param name="Applied">Whether anything was written.</param>
public sealed record ManifestPlan(IReadOnlyList<string> Lines, IReadOnlyList<string> Refusals, bool Applied);

/// <summary>A manifest that cannot be written or read.</summary>
public sealed class ManifestException : Exception
{
    /// <inheritdoc/>
    public ManifestException()
    {
    }

    /// <inheritdoc/>
    public ManifestException(string message)
        : base(message)
    {
    }

    /// <inheritdoc/>
    public ManifestException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
