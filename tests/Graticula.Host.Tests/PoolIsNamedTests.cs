using Graticula.Host;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The pool that reads a layer says who it is, and does not overwrite a name it was given.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every connection that reads a layer was anonymous in <c>pg_stat_activity</c> until
/// 2026-09-09</b>, while <see cref="PoolNames"/>'s own summary said <i>this server's
/// connection pools</i>, plural. <c>PoolNames.Of</c> was applied to the platform store and to
/// the job pollers and nowhere else, so the one pool an operator most needs to attribute —
/// the one holding their database while a query runs — was the one with no name.
/// </para>
/// <para>
/// <b>Found while measuring [Q-139](../../docs/open-questions.md), where it cost a whole
/// round of measurement.</b> A sampler filtering <c>application_name like 'graticula%'</c>
/// was watching the platform store while the layer pool it meant to watch had no name at all,
/// and the run had to be discarded.
/// </para>
/// <para>
/// <b>The second test is the one that matters, and its rule differs from the statement
/// timeout's on purpose.</b> A timeout is imposed where unset, because it is a bound this
/// server owes its own reliability. A name is a label on <em>somebody else's</em> server — a
/// layer pool connects to a registered database as often as to ours — and an operator who
/// wrote one into a registered connection string wrote it for their own monitoring.
/// Overwriting it would take away their answer in order to give them ours.
/// </para>
/// </remarks>
public sealed class PoolIsNamedTests
{
    /// <summary>An unnamed connection string is given this server's name for layers.</summary>
    [Fact]
    public void A_connection_string_with_no_name_gets_this_pools_name()
    {
        string named = LayerConnections.WithApplicationName(
            "Host=localhost;Database=gis;Username=gis");

        NpgsqlConnectionStringBuilder read = new(named);

        Assert.StartsWith(PoolNames.Layers, read.ApplicationName, System.StringComparison.Ordinal);

        // <b>And it stays inside PostgreSQL's ceiling</b>, which is what `Of` exists to
        // guarantee: `application_name` is a `name`, and a silently truncated instance is two
        // servers sharing an identity again.
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(read.ApplicationName!) <= 63,
            $"'{read.ApplicationName}' is longer than PostgreSQL will keep.");
    }

    /// <summary>An operator who named their own connection keeps their name.</summary>
    /// <param name="theirs">What they called it.</param>
    [Theory]
    [InlineData("their-monitoring")]
    [InlineData("gis-readonly")]
    public void An_operator_who_named_their_connection_keeps_it(string theirs)
    {
        string named = LayerConnections.WithApplicationName(
            $"Host=localhost;Database=gis;Username=gis;Application Name={theirs}");

        Assert.Equal(theirs, new NpgsqlConnectionStringBuilder(named).ApplicationName);
    }
}
