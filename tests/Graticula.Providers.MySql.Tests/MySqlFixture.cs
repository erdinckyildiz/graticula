using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using MySqlConnector;
using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>
/// The MySQL database ADR-208's tests read: a <c>mysql:</c> locator in <c>GRATICULA_TEST_MYSQL</c>, naming a database
/// the user may create tables in. Each test makes the tables it reads, so no seed is needed.
/// </summary>
/// <remarks>
/// <b>Fails rather than skips when it is not set</b>, as every database-backed suite here does. Filter it out
/// deliberately with <c>--filter Needs!=MySql</c>.
/// </remarks>
internal static class MySqlFixture
{
    public const string Variable = "GRATICULA_TEST_MYSQL";

    public static string Locator
    {
        get
        {
            string? stored = Environment.GetEnvironmentVariable(Variable);
            Assert.True(
                stored is not null && MySqlLocator.Is(stored),
                $"{Variable} is not set to a mysql: locator, so these tests FAIL rather than skip. Set it to "
                + "mysql:{\"host\":…,\"port\":…,\"database\":…,\"user\":…,\"password\":…} for a database the user may "
                + "create tables in, or filter them out deliberately with --filter Needs!=MySql.");
            return stored!;
        }
    }

    public static MySqlSource Source => MySqlLocator.Of(Locator);

    private static bool? mariaDb;

    /// <summary>Whether the test database is MariaDB (ADR-209), asked once.</summary>
    public static bool IsMariaDb
    {
        get
        {
            if (mariaDb is null)
            {
                using MySqlConnection connection = new(MySqlConnections.For(Locator, pooling: true));
                connection.Open();
                mariaDb = MySqlDialect.Of(connection).IsMariaDb;
            }

            return mariaDb.Value;
        }
    }

    /// <summary>
    /// A test's SQL, written for MySQL, as MariaDB takes it: a column's reference is <c>REF_SYSTEM_ID=n</c> before
    /// <c>NOT NULL</c>, there is no axis-order option, and <c>ST_Longitude</c> is <c>ST_X</c>. The provider's own SQL
    /// is the dialect's business (<c>MySqlDialect</c>); this is only the tests' set-up and read-back.
    /// </summary>
    public static string Sql(string sql) => !IsMariaDb
        ? sql
        : Regex.Replace(
            Regex.Replace(
                Regex.Replace(sql, @"(\w+) not null srid (\d+)", "$1 ref_system_id=$2 not null", RegexOptions.IgnoreCase),
                @",\s*'axis-order=long-lat'", string.Empty),
            @"\bst_(longitude|latitude)\(", m => m.Groups[1].Value == "longitude" ? "st_x(" : "st_y(", RegexOptions.IgnoreCase);

    /// <summary>Runs statements on the test database, in ANSI mode as the provider's sessions are.</summary>
    public static async Task ExecuteAsync(params string[] statements)
    {
        await using MySqlConnection connection =
            await MySqlConnections.OpenAsync(MySqlConnections.For(Locator, pooling: true), CancellationToken.None);

        foreach (string statement in statements)
        {
            await using MySqlCommand command = new(Sql(statement), connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
