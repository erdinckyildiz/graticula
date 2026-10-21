using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>ADR-212: Portal's editor-tracking columns added to a registered MySQL or MariaDB table on an administrator's word, once.</summary>
[Trait("Needs", "MySql")]
public sealed class MySqlTrackingColumnsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly (string Name, bool Moment)[] Portal =
        [("created_user", false), ("created_date", true), ("last_edited_user", false), ("last_edited_date", true)];

    [Fact]
    public async Task The_four_columns_are_added_once_with_a_name_and_a_moment_type()
    {
        string database = MySqlFixture.Source.Database;

        await MySqlFixture.ExecuteAsync(
            "drop table if exists zzt_tracked",
            "create table zzt_tracked (objectid int auto_increment primary key, label varchar(20), shape point not null srid 4326, spatial index (shape))");

        IReadOnlyList<string> added = await MySqlTableChecks.AddColumnsAsync(MySqlFixture.Locator, database, "zzt_tracked", Portal, None);
        Assert.Equal(Portal.Select(p => p.Name), added);
        Assert.Empty(await MySqlTableChecks.AddColumnsAsync(MySqlFixture.Locator, database, "zzt_tracked", Portal, None));

        await using MySqlConnection connection = await MySqlConnections.OpenAsync(MySqlConnections.For(MySqlFixture.Locator, pooling: true), None);
        await using MySqlCommand types = new(
            "select concat(column_name, ' ', data_type) from information_schema.columns where table_schema = @s and table_name = 'zzt_tracked' order by ordinal_position",
            connection);
        types.Parameters.AddWithValue("@s", database);
        List<string> found = [];

        await using (MySqlDataReader reader = await types.ExecuteReaderAsync(None))
        {
            while (await reader.ReadAsync(None))
            {
                found.Add(reader.GetString(0));
            }
        }

        Assert.Contains("created_user varchar", found);
        Assert.Contains("created_date datetime", found);
    }
}
