using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Graticula.Platform.Tenancy;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-204 condition 14: the tenant is carried ahead of each statement over the one platform pool, not by a pool per
/// tenant. What has to hold for that to be safe and to be the fix the measurement asked for: a statement's answer is
/// the answer of what the store wrote, never another tenant's setting and never a leftover one, and one tenant's work
/// may use the whole pool rather than a share of it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ATenantIsSentAheadOfEachStatementTests : PostgresFixture
{
    private static readonly Guid First = new("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Second = new("b0000000-0000-0000-0000-00000000000b");

    private const string Setting = "select nullif(current_setting('graticula.tenant', true), '')";

    [Fact]
    public async Task A_reader_a_scalar_and_a_row_count_are_the_statements_own()
    {
        await using (NpgsqlCommand make = DataSource.CreateCommand(
            "create table counted (i int); insert into counted select generate_series(1, 3)"))
        {
            await make.ExecuteNonQueryAsync();
        }

        TenantSource source = new(DataSource, First);

        await using (NpgsqlCommand scalar = source.CreateCommand(Setting))
        {
            Assert.Equal(First.ToString("D"), await scalar.ExecuteScalarAsync());
        }

        await using (NpgsqlCommand read = source.CreateCommand("select i from counted order by i"))
        await using (NpgsqlDataReader reader = await read.ExecuteReaderAsync())
        {
            List<int> rows = [];

            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetInt32(0));
            }

            Assert.Equal([1, 2, 3], rows);
            Assert.False(await reader.NextResultAsync());
        }

        await using (NpgsqlCommand update = source.CreateCommand("update counted set i = i + 10 where i >= @from"))
        {
            update.Parameters.AddWithValue("from", 2);
            Assert.Equal(2, await update.ExecuteNonQueryAsync());
        }

        await using (NpgsqlCommand select = source.CreateCommand("select count(*) from counted"))
        {
            // A SELECT counts no rows affected through Npgsql either way; the SET ahead of it must not make it count.
            Assert.Equal(-1, await select.ExecuteNonQueryAsync());
        }

        await using NpgsqlConnection connection = await source.OpenConnectionAsync();
        await using NpgsqlCommand onConnection = new(Setting, connection);
        Assert.Equal(First.ToString("D"), await onConnection.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Interleaved_tenants_on_a_pool_of_one_never_read_each_others_setting_or_a_leftover()
    {
        NpgsqlConnectionStringBuilder built = new(Environment.GetEnvironmentVariable("GRATICULA_TEST_PG"))
        {
            SearchPath = $"{SchemaName},public",
            MaxPoolSize = 1,
            MinPoolSize = 0,
        };
        await using NpgsqlDataSource one = NpgsqlDataSource.Create(built.ConnectionString);

        TenantSource first = new(one, First);
        TenantSource second = new(one, Second);
        TenantSource none = one;
        ConcurrentBag<string> wrong = [];

        async Task Ask(int i)
        {
            (TenantSource source, string? expected) = (i % 3) switch
            {
                0 => (first, First.ToString("D")),
                1 => (second, Second.ToString("D")),
                _ => (none, (string?)null),
            };

            object? read;

            if (i % 2 == 0)
            {
                await using NpgsqlCommand command = source.CreateCommand(Setting);
                read = await command.ExecuteScalarAsync();
            }
            else
            {
                await using NpgsqlConnection connection = await source.OpenConnectionAsync();
                await using NpgsqlCommand command = new(Setting, connection);
                read = await command.ExecuteScalarAsync();
            }

            string? got = read is DBNull or null ? null : (string)read;

            if (got != expected)
            {
                wrong.Add($"#{i}: expected {expected ?? "none"}, read {got ?? "none"}");
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 1200).Select(i => Task.Run(() => Ask(i))));

        Assert.True(wrong.IsEmpty, string.Join("; ", wrong.Take(10)));
    }

    [Fact]
    public async Task One_tenants_statements_use_the_whole_pool_and_not_a_share_of_it()
    {
        NpgsqlConnectionStringBuilder built = new(Environment.GetEnvironmentVariable("GRATICULA_TEST_PG"))
        {
            SearchPath = $"{SchemaName},public",
            MaxPoolSize = 8,
            MinPoolSize = 0,
        };
        await using NpgsqlDataSource root = NpgsqlDataSource.Create(built.ConnectionString).RoutedByTenant();

        // Ten organisations: a pool per tenant held a share max(2, 8/10) = 2 of these eight, and eight sleeps of
        // 0.4 s took four turns, 1.6 s. On the one pool they take one turn.
        TenantScope.SetCount(10);

        try
        {
            ConcurrentDictionary<int, bool> backends = new();
            Stopwatch clock = Stopwatch.StartNew();

            using (TenantScope.Enter(First))
            {
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                {
                    await using NpgsqlCommand command = TenantDataSources.Route(root).CreateCommand(
                        "select pg_backend_pid(), pg_sleep(0.4), current_setting('graticula.tenant', true)");
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync());
                    backends[reader.GetInt32(0)] = true;
                    Assert.Equal(First.ToString("D"), reader.GetString(2));
                })));
            }

            clock.Stop();

            Assert.Equal(8, backends.Count);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.2), $"eight statements took {clock.Elapsed.TotalSeconds:0.00} s");
            Assert.Same(root, TenantDataSources.Route(root).Pool);
        }
        finally
        {
            TenantScope.SetCount(1);
        }
    }
}
