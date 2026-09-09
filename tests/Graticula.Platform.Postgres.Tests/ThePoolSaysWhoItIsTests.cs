using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A connection built the way a layer's pool builds one names itself in
/// <c>pg_stat_activity</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every connection that reads a layer was anonymous until 2026-09-09.</b>
/// <c>PoolNames.Of</c> was applied to the platform store and to the job pollers and nowhere
/// else, while <c>PoolNames</c>' own summary said <i>this server's connection pools</i>,
/// plural — so the one pool an operator most needs to attribute, the one holding their
/// database while a query runs, was the one with no name.
/// </para>
/// <para>
/// <b>Found while measuring [Q-139](../../docs/open-questions.md), where it cost a whole
/// round.</b> A sampler filtering <c>application_name like 'graticula%'</c> was watching the
/// platform store while the layer pool it meant to watch had no name at all, and the run had
/// to be discarded.
/// </para>
/// <para>
/// <b>Here rather than beside the unit test, because the unit test proves the connection
/// string and this proves the database agrees.</b> <c>application_name</c> is a
/// <c>name</c> — NAMEDATALEN-1 usable bytes — and PostgreSQL truncates past it silently,
/// which is the failure a string assertion cannot see: two servers sharing an identity again,
/// which is the whole of what <c>PoolNames.Of</c> exists to prevent.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ThePoolSaysWhoItIsTests
{
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

    /// <summary>The name reaches PostgreSQL whole.</summary>
    [Fact]
    public async Task A_layer_pools_name_arrives_untruncated()
    {
        string? configured = Environment.GetEnvironmentVariable(ConnectionVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{ConnectionVariable} is not set, so this test FAILS rather than skips.");

        // The same two steps `LayerConnections.BuildPool` takes, in the same order. Named here
        // rather than referenced because this project does not see the host assembly; if that
        // changes, call the real one.
        NpgsqlConnectionStringBuilder builder = new(configured)
        {
            ApplicationName = "graticula-layers:"
                + Environment.MachineName + "/" + Environment.ProcessId,
        };

        await using NpgsqlDataSource source = NpgsqlDataSource.Create(builder.ConnectionString);
        await using NpgsqlConnection connection = await source.OpenConnectionAsync();

        await using NpgsqlCommand mine = new(
            "select application_name from pg_stat_activity where pid = pg_backend_pid()",
            connection);

        string? reported = (string?)await mine.ExecuteScalarAsync();

        Assert.Equal(builder.ApplicationName, reported);

        // <b>And it is inside the ceiling</b>, which is the assertion with the defect behind
        // it: past sixty-three bytes PostgreSQL keeps a prefix and says nothing, so a machine
        // with a long name would report a truncated identity and look like a different server.
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(reported ?? string.Empty) <= 63,
            $"'{reported}' is longer than PostgreSQL keeps.");
    }
}
