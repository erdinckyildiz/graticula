using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>
/// The SQL Server database ADR-210's tests read: a <c>sqlserver:</c> locator in <c>GRATICULA_TEST_SQLSERVER</c>, naming
/// a database the user may create tables in — SQL Server 2025 Express LocalDB on a Windows machine, the official image in
/// CI. Each test class makes the tables it reads.
/// </summary>
/// <remarks>
/// <b>Fails rather than skips when it is not set</b>, as every database-backed suite here does. Filter it out
/// deliberately with <c>--filter Needs!=SqlServer</c>.
/// </remarks>
internal static class SqlServerFixture
{
    public const string Variable = "GRATICULA_TEST_SQLSERVER";

    public static string Locator
    {
        get
        {
            string? stored = Environment.GetEnvironmentVariable(Variable);
            Assert.True(
                stored is not null && SqlServerLocator.Is(stored),
                $"{Variable} is not set to a sqlserver: locator, so these tests FAIL rather than skip. Set it to "
                + "sqlserver:{\"host\":…,\"port\":…,\"database\":…,\"user\":…,\"password\":…} for a database the user may "
                + "create tables in, or filter them out deliberately with --filter Needs!=SqlServer.");
            return stored!;
        }
    }

    /// <summary>Runs statements on the test database, one batch each.</summary>
    public static async Task ExecuteAsync(params string[] statements)
    {
        await using SqlConnection connection =
            await SqlServerConnections.OpenAsync(SqlServerConnections.For(Locator, pooling: true), CancellationToken.None);

        foreach (string statement in statements)
        {
            await using SqlCommand command = new(statement, connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
