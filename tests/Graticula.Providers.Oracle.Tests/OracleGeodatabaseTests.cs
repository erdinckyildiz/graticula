using System;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>
/// A table an enterprise geodatabase branch-versions or archives is refused at publish — ADR-217, the ArcGIS veteran's
/// N6. The columns are imitated in the test schema; the registry of a traditionally versioned class lives in the SDE
/// schema, which this schema cannot create, and was measured by hand against one (ADR-217 §4).
/// </summary>
[Trait("Needs", "Oracle")]
public sealed class OracleGeodatabaseTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static string Owner => OracleFixture.Source.User.ToUpperInvariant();

    private static async Task ExecuteAsync(string sql)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(OracleFixture.Locator, pooling: true), None);
        await using OracleCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(None);
    }

    [Fact]
    public async Task Branch_versioned_and_archived_classes_are_refused_and_a_plain_table_is_not()
    {
        await ExecuteAsync("drop table if exists ZZG_BRANCH purge");
        await ExecuteAsync("drop table if exists ZZG_ARCHIVE purge");
        await ExecuteAsync("drop table if exists ZZG_PLAIN purge");
        await ExecuteAsync("create table ZZG_BRANCH (objectid number(10) primary key, gdb_branch_id number(10))");
        await ExecuteAsync("create table ZZG_ARCHIVE (objectid number(10) primary key, gdb_from_date date, gdb_to_date date)");
        await ExecuteAsync("create table ZZG_PLAIN (objectid number(10) primary key)");

        try
        {
            Assert.Contains("branch versioned",
                await OracleTableChecks.GeodatabaseRefusalAsync(OracleFixture.Locator, Owner, "ZZG_BRANCH", None),
                StringComparison.Ordinal);
            Assert.Contains("archived",
                await OracleTableChecks.GeodatabaseRefusalAsync(OracleFixture.Locator, Owner, "ZZG_ARCHIVE", None),
                StringComparison.Ordinal);
            Assert.Null(await OracleTableChecks.GeodatabaseRefusalAsync(OracleFixture.Locator, Owner, "ZZG_PLAIN", None));
        }
        finally
        {
            await ExecuteAsync("drop table if exists ZZG_BRANCH purge");
            await ExecuteAsync("drop table if exists ZZG_ARCHIVE purge");
            await ExecuteAsync("drop table if exists ZZG_PLAIN purge");
        }
    }
}
