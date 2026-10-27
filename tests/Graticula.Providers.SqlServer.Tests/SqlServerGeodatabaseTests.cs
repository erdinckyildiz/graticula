using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>
/// A table an enterprise geodatabase versions or archives is refused at publish — ADR-217, the ArcGIS veteran's N6 —
/// and a table it only registered is not. The geodatabase is imitated by the names its public documentation gives: the
/// <c>sde.SDE_table_registry</c> row, the <c>a&lt;id&gt;</c> delta table, and the GDB_ columns.
/// </summary>
[Trait("Needs", "SqlServer")]
public sealed class SqlServerGeodatabaseTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task StageAsync()
    {
        await SqlServerFixture.ExecuteAsync(
            "if schema_id('sde') is null exec('create schema sde')",
            "drop table if exists sde.SDE_table_registry",
            "create table sde.SDE_table_registry (registration_id int, table_name sysname, owner sysname)",
            "insert into sde.SDE_table_registry values (7, 'zzg_versioned', 'dbo'), (8, 'zzg_registered', 'dbo')",
            "drop table if exists dbo.a7",
            "create table dbo.a7 (objectid int)",
            "drop table if exists dbo.zzg_branch",
            "create table dbo.zzg_branch (objectid int primary key, GDB_BRANCH_ID int, shape geometry)",
            "drop table if exists dbo.zzg_archive",
            "create table dbo.zzg_archive (objectid int primary key, GDB_FROM_DATE datetime2, GDB_TO_DATE datetime2, shape geometry)",
            "drop table if exists dbo.zzg_versioned",
            "create table dbo.zzg_versioned (objectid int primary key, shape geometry)",
            "drop table if exists dbo.zzg_registered",
            "create table dbo.zzg_registered (objectid int primary key, shape geometry)");
    }

    [Fact]
    public async Task Branch_versioned_archived_and_versioned_classes_are_refused_and_a_registered_one_is_not()
    {
        await StageAsync();

        try
        {
            Assert.Contains("branch versioned",
                await SqlServerTableChecks.GeodatabaseRefusalAsync(SqlServerFixture.Locator, "dbo", "zzg_branch", None),
                StringComparison.Ordinal);
            Assert.Contains("archived",
                await SqlServerTableChecks.GeodatabaseRefusalAsync(SqlServerFixture.Locator, "dbo", "zzg_archive", None),
                StringComparison.Ordinal);
            Assert.Contains("zzg_versioned_EVW",
                await SqlServerTableChecks.GeodatabaseRefusalAsync(SqlServerFixture.Locator, "dbo", "zzg_versioned", None),
                StringComparison.Ordinal);
            Assert.Null(await SqlServerTableChecks.GeodatabaseRefusalAsync(SqlServerFixture.Locator, "dbo", "zzg_registered", None));

            // ADR-217 condition 2: the probe skips the same three with the same sentences, and offers the registered one.
            await SqlServerFixture.ExecuteAsync(
                "insert into dbo.zzg_registered values (1, geometry::STGeomFromText('POINT (1 1)', 4326))");
            Graticula.Platform.Admin.ProbeResult probed = await new SqlServerDataSourceProbe().ProbeAsync(SqlServerFixture.Locator, None);

            foreach (string table in (string[])["zzg_branch", "zzg_archive", "zzg_versioned"])
            {
                Assert.DoesNotContain(probed.Tables, t => t.TableName == table);
                Assert.Equal(
                    await SqlServerTableChecks.GeodatabaseRefusalAsync(SqlServerFixture.Locator, "dbo", table, None),
                    Assert.Single(probed.Skipped ?? [], s => s.Name == $"dbo.{table}").Reason);
            }

            Assert.Contains(probed.Tables, t => t.TableName == "zzg_registered");
        }
        finally
        {
            await SqlServerFixture.ExecuteAsync(
                "drop table if exists dbo.zzg_branch", "drop table if exists dbo.zzg_archive",
                "drop table if exists dbo.zzg_versioned", "drop table if exists dbo.zzg_registered",
                "drop table if exists dbo.a7", "drop table if exists sde.SDE_table_registry");
        }
    }
}
