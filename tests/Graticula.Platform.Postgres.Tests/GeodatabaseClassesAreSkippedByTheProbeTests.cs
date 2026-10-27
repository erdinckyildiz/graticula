using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// The probe does not offer what a publish refuses — ADR-217 condition 2: a branch-versioned, an archived and a
/// traditionally versioned geodatabase class are listed as skipped with the publish's own sentence, and a class the
/// geodatabase only registered is offered.
/// </summary>
/// <remarks>
/// <b>The geodatabase is imitated by the names its public documentation gives</b>, as the SQL Server and Oracle tests
/// do: the GDB_ columns, an <c>sde.sde_table_registry</c> row and the <c>a&lt;id&gt;</c> delta table. The registry row
/// is this test's own id and is removed afterwards; the registry table is left, as a geodatabase would leave it.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GeodatabaseClassesAreSkippedByTheProbeTests : PostgresFixture
{
    private static readonly int Registration = 700_000 + Random.Shared.Next(99_999);

    [Fact]
    public async Task Versioned_and_archived_classes_are_skipped_with_the_publishs_sentence()
    {
        string schema;

        await using (NpgsqlConnection connection = await DataSource.OpenConnectionAsync(CancellationToken.None))
        {
            await using (NpgsqlCommand current = new("select current_schema()", connection))
            {
                schema = (string)(await current.ExecuteScalarAsync(CancellationToken.None))!;
            }

            foreach (string statement in new[]
            {
                "create table zzg_branch (objectid serial primary key, gdb_branch_id integer, geom geometry(Point, 4326))",
                "create table zzg_archive (objectid serial primary key, gdb_from_date timestamp, gdb_to_date timestamp, geom geometry(Point, 4326))",
                "create table zzg_versioned (objectid serial primary key, geom geometry(Point, 4326))",
                "create table zzg_registered (objectid serial primary key, geom geometry(Point, 4326))",
                $"create table a{Registration} (objectid integer)",
                "create schema if not exists sde",
                "create table if not exists sde.sde_table_registry (registration_id integer, owner text, table_name text)",
                $"insert into sde.sde_table_registry values ({Registration}, '{schema}', 'zzg_versioned'), ({Registration + 1}, '{schema}', 'zzg_registered')",
            })
            {
                await using NpgsqlCommand command = new(statement, connection);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        try
        {
            ProbeResult probed = await new PostgresDataSourceProbe().ProbeAsync(
                Environment.GetEnvironmentVariable("GRATICULA_TEST_PG")!, CancellationToken.None);

            Assert.True(probed.Outcome == ProbeOutcome.Usable, $"The probe answered {probed.Outcome}: {probed.Message}");

            (string Table, string Says)[] expected =
            [
                ("zzg_branch", "branch versioned"),
                ("zzg_archive", "archived"),
                ("zzg_versioned", "zzg_versioned_EVW"),
            ];

            foreach ((string table, string says) in expected)
            {
                Assert.DoesNotContain(probed.Tables, t => t.SchemaName == schema && t.TableName == table);
                SkippedTable skipped = Assert.Single(probed.Skipped ?? [], s => s.Name == $"{schema}.{table}");
                Assert.Contains(says, skipped.Reason, StringComparison.Ordinal);
                Assert.Contains("ADR-217", skipped.Reason, StringComparison.Ordinal);
            }

            // Registered and not versioned — no delta table — is a table ArcGIS itself lets SQL edit, and is offered.
            Assert.Contains(probed.Tables, t => t.SchemaName == schema && t.TableName == "zzg_registered");
        }
        finally
        {
            await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(CancellationToken.None);
            await using NpgsqlCommand forget = new(
                $"delete from sde.sde_table_registry where registration_id in ({Registration}, {Registration + 1})", connection);
            await forget.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
