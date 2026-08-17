using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Two services cannot differ only in the case of their folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>They could until migration 15, and the lookup could not tell them
/// apart.</b> Migration 11's unique index was
/// <c>(coalesce(folder, ''), lower(name))</c> — case-insensitive on the name and
/// case-<em>sensitive</em> on the folder — while every read asks
/// <c>coalesce(lower(folder), '') = coalesce(lower(@folder), '')</c>. So
/// <c>Hosted/parcels</c> and <c>hosted/parcels</c> were two rows to the
/// constraint and one address to the caller, and <c>FindServiceAsync</c>
/// returned whichever came first.
/// </para>
/// <para>
/// <b>Which matters because the two can be shared differently.</b> The same URL
/// resolving to the public service or the private one depending on row order
/// means an anonymous caller sees it or gets a 404 for a reason nobody can
/// predict. Folders are taken from the administrator's request rather than
/// generated, so the pair needs no trick to create.
/// </para>
/// <para>
/// <b>Found by measurement, which is worth recording because it was not being
/// looked for.</b> The D-30 instrumentation put the per-request catalogue read
/// at 1.8 ms; reading the query to find out why turned up an index whose
/// expression did not match the predicate. The performance question had an
/// answer — the index was unusable, so the lookup scanned every service — and
/// underneath it was this.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FolderCaseSchemaTests : PostgresFixture
{
    private async Task InsertServiceAsync(string name, string? folder)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into service (id, name, folder, kind, sharing, status) "
            + "values (gen_random_uuid(), @name, @folder, 'FeatureServer', 'private', 'started')");

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("folder", (object?)folder ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// The folder is part of the identity, and its case is not.
    /// </summary>
    [Fact]
    public async Task Two_services_cannot_differ_only_in_the_case_of_the_folder()
    {
        await MigrateAsync();

        await InsertServiceAsync("parcels", "Hosted");

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertServiceAsync("parcels", "hosted"));

        // 23505: unique_violation. Asserted by code rather than by message,
        // which is localised and version-dependent.
        Assert.Equal("23505", error.SqlState);
    }

    /// <summary>
    /// Different folders are still different services.
    /// </summary>
    /// <remarks>
    /// <b>The other half of the constraint, and the half a too-eager fix
    /// breaks.</b> Making the index case-insensitive must not make it
    /// folder-blind: <c>/rest/services/roads</c> and
    /// <c>/rest/services/hosted/roads</c> are two addresses and may be two
    /// services, which is what migration 11 was for.
    /// </remarks>
    [Fact]
    public async Task A_service_in_the_root_and_one_in_a_folder_may_share_a_name()
    {
        await MigrateAsync();

        await InsertServiceAsync("roads", null);
        await InsertServiceAsync("roads", "hosted");
        await InsertServiceAsync("roads", "archive");

        await using NpgsqlCommand count = DataSource.CreateCommand(
            "select count(*) from service where lower(name) = 'roads'");

        Assert.Equal(3L, await count.ExecuteScalarAsync(CancellationToken.None));
    }

    /// <summary>
    /// The name is still matched without regard to case, as it always was.
    /// </summary>
    [Fact]
    public async Task Two_services_cannot_differ_only_in_the_case_of_the_name()
    {
        await MigrateAsync();

        await InsertServiceAsync("Parcels", "hosted");

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertServiceAsync("parcels", "hosted"));

        Assert.Equal("23505", error.SqlState);
    }

    /// <summary>
    /// The lookup's own predicate can use the index.
    /// </summary>
    /// <remarks>
    /// <b>An expression index is only an index for the expression it holds.</b>
    /// The predicate and the index agreed in meaning and differed in form —
    /// <c>coalesce(lower(folder), '')</c> against <c>coalesce(folder, '')</c> —
    /// so the planner could not use it and scanned the table on the most-used
    /// path in the product. This asserts the plan rather than a duration,
    /// because a duration on 33 rows says nothing and this is a claim about
    /// 1,000 services.
    /// </remarks>
    [Fact]
    public async Task The_service_lookup_uses_its_index_rather_than_scanning()
    {
        await MigrateAsync();

        for (int i = 0; i < 40; i++)
        {
            await InsertServiceAsync($"service{i}", i % 2 == 0 ? "hosted" : null);
        }

        // The planner will prefer a sequential scan on a tiny table whatever the
        // index says, so it is told not to — the question is whether the index
        // is *usable*, not which the planner picks at this size.
        await using (NpgsqlCommand off = DataSource.CreateCommand("set enable_seqscan = off"))
        {
            await off.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using NpgsqlCommand explain = DataSource.CreateCommand(
            "explain select id from service "
            + "where coalesce(lower(folder), '') = coalesce(lower(@folder), '') "
            + "  and lower(name) = lower(@name)");

        explain.Parameters.AddWithValue("folder", "hosted");
        explain.Parameters.AddWithValue("name", "service4");

        string plan = string.Empty;

        await using (NpgsqlDataReader reader =
                     await explain.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                plan += reader.GetString(0) + "\n";
            }
        }

        Assert.Contains("service_name_in_folder_ci", plan, StringComparison.Ordinal);
    }
}
