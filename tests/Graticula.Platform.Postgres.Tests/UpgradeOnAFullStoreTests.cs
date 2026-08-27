using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Postgres;
using Graticula.Platform.Schema;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// The upgrade is walked on a store that already has layers, and the operator is told what it
/// does to them.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-018](../../docs/adr/ADR-018-authorization-and-roles.md) condition 4</b>: *the
/// upgrade is walked on a store that already has layers, and the operator is told that
/// existing layers became private. Silently privatising somebody's published data is a worse
/// regression than the closed default was.*
/// </para>
/// <para>
/// <b>A real walk, not a fabricated state.</b> The store is built by running the real
/// migrations up to version 4 — the last one before sharing exists — a layer row is inserted
/// into it the way a deployment of that version would have, and then the real migrator takes
/// it the rest of the way. Stamping a store at 4 by hand and dropping a column would have
/// tested the fabrication.
/// </para>
/// <para>
/// <b>Two halves, and the second is the one that was missing.</b> The upgrade works — that
/// was never in doubt. What the plan said about it was *Add ownership and sharing scope to
/// layers*, which is true, complete as a description of the schema, and leaves an operator
/// with no idea that everything they have published is about to stop being visible.
/// </para>
/// </remarks>
public sealed class UpgradeOnAFullStoreTests : PostgresFixture
{
    /// <summary>The last version before <c>layer.sharing</c> exists.</summary>
    private const int BeforeSharing = 4;

    private static MigrationSet Through(int version) =>
        new(PlatformMigrations.All.All.Where(m => m.Version.Value <= version).ToList());

    [Fact]
    public async Task An_upgrade_that_privatises_published_layers_says_so_before_it_runs()
    {
        PostgresPlatformSchemaStore store = new(DataSource);

        // ---------------------------------------------------------------- a store of that age
        SchemaMigrator old = new(store, Through(BeforeSharing));
        MigrationReport built = await old.ApplyAsync(CancellationToken.None);

        Assert.Equal(BeforeSharing, built.To.Applied.Value);

        await using (NpgsqlConnection connection =
            await DataSource.OpenConnectionAsync(CancellationToken.None))
        {
            // <b>No `sharing` column to set, which is the whole point.</b> A deployment of
            // version 4 had layers and no notion of who could see them: everything a caller
            // could name, they could read.
            // <b>The version-4 shape of `layer`, which is not today's.</b> No `service_id`
            // and no `sharing`: both arrive later, and writing today's insert against a
            // four-version-old table is how a walk stops being a walk. A data source has to
            // exist first, because `layer.data_source_id` has always been a foreign key.
            Guid source = Guid.NewGuid();

            await using (NpgsqlCommand addSource = connection.CreateCommand())
            {
                addSource.CommandText =
                    $"""
                     insert into "{SchemaName}".data_source
                            (id, name, kind, connection_secret, key_version)
                     values (@id, 'before_sharing', 'postgis', @secret, 1)
                     """;

                addSource.Parameters.AddWithValue("id", source);
                addSource.Parameters.AddWithValue("secret", new byte[] { 1, 2, 3 });

                await addSource.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using NpgsqlCommand insert = connection.CreateCommand();
            insert.CommandText =
                $"""
                 insert into "{SchemaName}".layer
                        (id, name, data_source_id, schema_name, table_name, geometry_column,
                         srid, identity_column, object_id_column, geometry_type, is_hosted)
                 values (@id, 'published_before_sharing', @source, 'public', 'things', 'geom',
                         3857, 'id', 'objectid', 'Polygon', false)
                 """;

            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("source", source);

            Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
        }

        // ---------------------------------------------------------------- the upgrade
        SchemaMigrator today = new(store, PlatformMigrations.All);
        MigrationReport plan = await today.PlanAsync(CancellationToken.None);

        Assert.False(plan.IsUpToDate, "A store at version 4 has migrations to apply.");

        Assert.Contains(
            plan.Pending,
            migration => migration.Version.Value == 5);

        string described = plan.Describe();

        // <b>The sentence the condition asks for.</b> Not the schema description — that was
        // always there and is what made this look answered.
        Assert.Contains("data that is already there", described, StringComparison.Ordinal);
        Assert.Contains("PRIVATE", described, StringComparison.Ordinal);

        // And what to do about it, because a warning an operator cannot act on is a warning
        // they learn to skip.
        Assert.Contains("sharing", described, StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- and it works
        MigrationReport applied = await today.ApplyAsync(CancellationToken.None);

        Assert.Equal(
            PlatformMigrations.ComponentSchemaVersion.Value,
            applied.To.Applied.Value);

        await using (NpgsqlConnection connection =
            await DataSource.OpenConnectionAsync(CancellationToken.None))
        {
            await using NpgsqlCommand read = connection.CreateCommand();
            read.CommandText =
                $"""select sharing from "{SchemaName}".layer where name = 'published_before_sharing'""";

            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(CancellationToken.None);

            List<string> scopes = [];

            while (await reader.ReadAsync(CancellationToken.None))
            {
                scopes.Add(reader.GetString(0));
            }

            // The upgrade did what the caution says it does. If this ever stops being
            // `private`, the caution is a lie and the condition is unmet from the other side.
            Assert.All(scopes, scope => Assert.Equal("private", scope));
        }
    }

    [Fact]
    public void A_migration_with_nothing_to_say_about_existing_rows_says_nothing()
    {
        // <b>The half that keeps the warning worth reading.</b> A caution beside every step
        // is noise, and noise is how the one that matters gets skipped. Most migrations add a
        // nullable column and change no row's meaning.
        int withCaution = PlatformMigrations.All.All.Count(m => !string.IsNullOrWhiteSpace(m.Caution));

        Assert.True(
            withCaution < PlatformMigrations.All.All.Count / 2,
            $"{withCaution} of {PlatformMigrations.All.All.Count} migrations carry a caution. A "
            + "caution on most steps is a list nobody reads, which is the same as no warning "
            + "at all.");
    }
}
