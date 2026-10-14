using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Postgres;
using Graticula.Platform.Schema;
using Graticula.Platform.Secrets;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-183's two conditions, against two real stores in one database.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first store carries one of everything the manifest covers</b>, so a byte-identical round trip says
/// something about each of them: a folder, a shared domain referenced from a layer by id, a service in a content
/// folder with a group layer, a layer inside the group, a layer on a second data source, a relationship between the
/// two, a named style, a sprite and a sharing group. The second store starts empty but for what a manifest names and
/// never creates: the principal, the sharing group and its own datastore.
/// </para>
/// <para>
/// <b>Measured by hand first, 2026-10-15</b>: the local fixture's catalogue applied to an empty store and served by
/// a second server answered the same bytes as the first for queries, documents and a drawn map, once the datastore
/// was matched by role rather than carried, which is in <see cref="CatalogManifest"/>'s remarks.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CatalogManifestTests : PostgresFixture
{
    private static readonly SecretProtector Secrets = new(1, SecretProtector.GenerateKey());

    private static string Locate(string connection) => new NpgsqlConnectionStringBuilder(connection) is { } b
        ? $"{b.Host}:{b.Port}/{b.Database}"
        : "?";

    private static string Base => Environment.GetEnvironmentVariable("GRATICULA_TEST_PG")!;

    private static async Task Run(NpgsqlDataSource store, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = store.CreateCommand(sql);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>A second store in a schema of its own, migrated, with the principal and group a manifest names.</summary>
    private async Task<(NpgsqlDataSource Store, string Schema)> SecondStoreAsync()
    {
        string schema = SchemaName + "_b";
        await Run(DataSource, $"create schema \"{schema}\"");

        NpgsqlDataSourceBuilder builder = new(Base);
        builder.ConnectionStringBuilder.SearchPath = $"{schema},public";
        NpgsqlDataSource store = builder.Build();

        await new SchemaMigrator(new PostgresPlatformSchemaStore(store), PlatformMigrations.All).ApplyAsync(CancellationToken.None);
        await SeedIdentityAsync(store);
        await new PostgresAdminCatalog(store, Secrets).EnsureDatastoreAsync(Base, CancellationToken.None);

        return (store, schema);
    }

    private static async Task SeedIdentityAsync(NpgsqlDataSource store)
    {
        Guid owner = Guid.NewGuid();
        await Run(store, "insert into principal (id, kind, name) values (@id, 'user', 'mf_owner')", ("id", owner));
        await Run(store, "insert into sharing_group (id, name, owner_principal_id) values (@id, 'mf_group', @owner)",
            ("id", Guid.NewGuid()), ("owner", owner));
    }

    /// <summary>The first store: one of everything.</summary>
    private async Task SeedAsync()
    {
        await MigrateAsync();
        await SeedIdentityAsync(DataSource);
        await new PostgresAdminCatalog(DataSource, Secrets).EnsureDatastoreAsync(Base, CancellationToken.None);

        // The layers' tables, schema-qualified, so both stores' sources find them.
        await Run(DataSource, $"create table \"{SchemaName}\".mf_points (id serial primary key, kind text, geom geometry(Point, 4326))");
        await Run(DataSource, $"create table \"{SchemaName}\".mf_notes (id serial primary key, point_id int, note text, geom geometry(Point, 4326))");

        await Run(DataSource,
            "insert into data_source (id, name, kind, connection_secret, key_version) values (gen_random_uuid(), 'mf_second', 'postgis', @secret, 1)",
            ("secret", Secrets.Protect(Base)));

        await Run(DataSource, """
            insert into folder (name, owner_principal_id) select 'mf_folder', id from principal where name = 'mf_owner';
            insert into field_domain (id, name, definition, owner_principal_id)
              select gen_random_uuid(), 'MfKinds', '{"type":"codedValue","name":"MfKinds","codedValues":[{"code":"a","name":"Ağaç"}]}', id
                from principal where name = 'mf_owner';
            insert into content_folder (id, owner_principal_id, title) select gen_random_uuid(), id, 'Projeler' from principal where name = 'mf_owner';
            insert into service (id, name, folder, kind, description, owner_principal_id, sharing, content_folder_id, tags)
              select gen_random_uuid(), 'mf_service', 'mf_folder', 'FeatureServer', 'Bir açıklama', p.id, 'private', c.id, array['bir','iki']
                from principal p join content_folder c on c.owner_principal_id = p.id where p.name = 'mf_owner';
            insert into group_layer (id, service_id, layer_index, name)
              select gen_random_uuid(), id, 0, 'Grup' from service where name = 'mf_service';
            """);

        await Run(DataSource, $$"""
            insert into layer (id, name, data_source_id, schema_name, table_name, geometry_column, srid, identity_column,
                               object_id_column, geometry_type, service_id, layer_index, parent_layer_index, field_overrides)
              select gen_random_uuid(), 'mf_points', d.id, '{{SchemaName}}', 'mf_points', 'geom', 4326, 'id', 'id', 'Point',
                     s.id, 1, 0,
                     jsonb_build_array(jsonb_build_object('column', 'kind', 'domain', jsonb_build_object('id', f.id::text)))
                from data_source d, service s, field_domain f
               where d.is_datastore and s.name = 'mf_service' and f.name = 'MfKinds';
            insert into layer (id, name, data_source_id, schema_name, table_name, geometry_column, srid, identity_column,
                               object_id_column, geometry_type, service_id, layer_index)
              select gen_random_uuid(), 'mf_notes', d.id, '{{SchemaName}}', 'mf_notes', 'geom', 4326, 'id', 'id', 'Point', s.id, 2
                from data_source d, service s where d.name = 'mf_second' and s.name = 'mf_service';
            update service set next_layer_index = 3 where name = 'mf_service';
            insert into relationship (id, name, origin_layer_id, origin_key, related_layer_id, related_key, cardinality)
              select gen_random_uuid(), 'points_notes', a.id, 'id', b.id, 'point_id', 'OneToMany'
                from layer a, layer b where a.name = 'mf_points' and b.name = 'mf_notes';
            insert into service_style (service_id, name, style) select id, 'gece', '{"version":8}' from service where name = 'mf_service';
            insert into service_sprite (service_id, pixel_ratio, index_json, image)
              select id, 1, '{}', '\x89504e47'::bytea from service where name = 'mf_service';
            insert into sharing_group_item (group_id, service_id)
              select g.id, s.id from sharing_group g, service s where g.name = 'mf_group' and s.name = 'mf_service';
            """);
    }

    [Fact]
    public async Task A_stores_manifest_applied_to_an_empty_store_is_written_back_byte_for_byte()
    {
        await SeedAsync();

        string first = CatalogManifest.Write(await CatalogManifest.ExportAsync(DataSource, Secrets, Locate, CancellationToken.None));

        // What the file must not hold, before anything is compared: no key and no credential.
        Assert.DoesNotMatch("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", first);
        Assert.DoesNotContain("Password", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"shared_domain\": \"MfKinds\"", first, StringComparison.Ordinal);

        (NpgsqlDataSource second, string schema) = await SecondStoreAsync();

        try
        {
            ManifestPlan applied = await CatalogManifest.ApplyAsync(
                second, Secrets, Locate, (JsonObject)JsonNode.Parse(first)!,
                new Dictionary<string, string> { ["mf_second"] = Base }, apply: true, CancellationToken.None);

            Assert.True(applied.Refusals.Count == 0, string.Join("\n", applied.Refusals));
            Assert.True(applied.Applied);

            string back = CatalogManifest.Write(await CatalogManifest.ExportAsync(second, Secrets, Locate, CancellationToken.None));

            Assert.Equal(first, back);

            // And applying it again changes nothing.
            ManifestPlan again = await CatalogManifest.ApplyAsync(
                second, Secrets, Locate, (JsonObject)JsonNode.Parse(first)!,
                new Dictionary<string, string>(), apply: false, CancellationToken.None);

            Assert.Empty(again.Refusals);
            Assert.All(again.Lines, l => Assert.StartsWith("unchanged", l, StringComparison.Ordinal));
        }
        finally
        {
            await second.DisposeAsync();
            await Run(DataSource, $"drop schema \"{schema}\" cascade");
        }
    }

    /// <summary>ADR-002 condition 3's own test: export, edit one value, import, and the catalogue differs in exactly that.</summary>
    [Fact]
    public async Task A_value_edited_in_the_file_is_the_only_thing_an_apply_changes()
    {
        await SeedAsync();

        JsonObject manifest = await CatalogManifest.ExportAsync(DataSource, Secrets, Locate, CancellationToken.None);
        string before = CatalogManifest.Write(manifest);

        manifest["services"]!.AsArray().OfType<JsonObject>().Single(s => (string?)s["name"] == "mf_service")["description"] =
            "Düzenlendi";

        ManifestPlan plan = await CatalogManifest.ApplyAsync(
            DataSource, Secrets, Locate, manifest, new Dictionary<string, string>(), apply: true, CancellationToken.None);

        Assert.Empty(plan.Refusals);
        Assert.Equal(["update     service mf_folder/mf_service"], plan.Lines.Where(l => !l.StartsWith("unchanged", StringComparison.Ordinal)));

        string after = CatalogManifest.Write(await CatalogManifest.ExportAsync(DataSource, Secrets, Locate, CancellationToken.None));
        string[] was = before.Split('\n');
        string[] now = after.Split('\n');

        Assert.Equal(was.Length, now.Length);

        (string Was, string Now)[] changed = [.. was.Zip(now).Where(p => p.First != p.Second)];

        Assert.Single(changed);
        Assert.Contains("\"description\": \"Düzenlendi\"", changed[0].Now, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_apply_refuses_with_every_reason_and_writes_nothing()
    {
        await SeedAsync();

        JsonObject manifest = await CatalogManifest.ExportAsync(DataSource, Secrets, Locate, CancellationToken.None);
        JsonObject service = manifest["services"]!.AsArray().OfType<JsonObject>().Single(s => (string?)s["name"] == "mf_service");

        service["owner"] = "nobody_here";
        service["layers"]!.AsArray().OfType<JsonObject>().Single(l => (string?)l["name"] == "mf_points")["table_name"] = "no_such_table";

        (NpgsqlDataSource second, string schema) = await SecondStoreAsync();

        try
        {
            // No credential for mf_second, an owner nobody has, and a table that is not there: three reasons, one run.
            ManifestPlan refused = await CatalogManifest.ApplyAsync(
                second, Secrets, Locate, manifest, new Dictionary<string, string>(), apply: true, CancellationToken.None);

            Assert.False(refused.Applied);
            Assert.Contains(refused.Refusals, r => r.Contains("'mf_second'", StringComparison.Ordinal) && r.Contains("--credentials", StringComparison.Ordinal));
            Assert.Contains(refused.Refusals, r => r.Contains("nobody_here", StringComparison.Ordinal));
            Assert.Contains(refused.Refusals, r => r.Contains("no_such_table", StringComparison.Ordinal));

            await using NpgsqlCommand count = second.CreateCommand("select count(*) from service");
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await second.DisposeAsync();
            await Run(DataSource, $"drop schema \"{schema}\" cascade");
        }
    }

    [Fact]
    public async Task The_datastore_takes_no_credential_because_every_server_registers_its_own()
    {
        await SeedAsync();

        JsonObject manifest = await CatalogManifest.ExportAsync(DataSource, Secrets, Locate, CancellationToken.None);

        // Where the datastore points is the deployment's, not the catalogue's, so it is not written.
        Assert.Null(manifest["data_sources"]!.AsArray().OfType<JsonObject>().Single(d => (bool?)d["is_datastore"] == true)["location"]);

        (NpgsqlDataSource second, string schema) = await SecondStoreAsync();

        try
        {
            ManifestPlan refused = await CatalogManifest.ApplyAsync(
                second, Secrets, Locate, manifest,
                new Dictionary<string, string> { ["mf_second"] = Base, ["datastore"] = Base }, apply: false, CancellationToken.None);

            Assert.Contains(refused.Refusals, r => r.Contains("is the datastore", StringComparison.Ordinal));
        }
        finally
        {
            await second.DisposeAsync();
            await Run(DataSource, $"drop schema \"{schema}\" cascade");
        }
    }
}
