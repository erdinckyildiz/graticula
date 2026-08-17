using System;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// That a registered data source cannot lose its statement timeout by accident.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-42, found by the injection sweep of 2026-08-16 rather than by anything
/// failing.</b> ADR-007 §4.8 makes the timeout mandatory because without it one
/// expensive query holds a pooled connection until the client gives up, and enough
/// of them exhaust the pool for every layer sharing that data source.
/// </para>
/// <para>
/// The code applied it only when the connection string's <c>Options</c> was
/// empty, deferring to "an operator who has already set it". But <c>Options</c>
/// carries every server setting, not just this one — so registering a data source
/// with <c>Options=-c application_name=qgis</c>, which is a reasonable thing to
/// write and says nothing about timeouts, silently removed the control. Nothing
/// logged it and nothing looked different afterwards.
/// </para>
/// <para>
/// No database is needed: the whole behaviour is a string decision, which is why
/// it is tested here and not in the integration suite. That it was previously
/// unreachable without a database is a large part of why it stayed wrong.
/// </para>
/// </remarks>
public sealed class StatementTimeoutTests
{
    private const string Base = "Host=localhost;Database=gis;Username=gis;Password=gis";

    private static string Options(string connectionString) =>
        new NpgsqlConnectionStringBuilder(
            LayerConnections.WithStatementTimeout(connectionString)).Options ?? string.Empty;

    [Fact]
    public void A_connection_string_with_no_options_gets_the_timeout()
    {
        Assert.Contains("statement_timeout=30000", Options(Base), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-c application_name=qgis")]
    [InlineData("-c search_path=public")]
    [InlineData("-c application_name=qgis -c search_path=public")]
    public void An_unrelated_option_does_not_cost_the_timeout(string existing)
    {
        // The regression. Each of these used to mean "no timeout on this data
        // source, forever, silently".
        string result = Options($"{Base};Options={existing}");

        Assert.Contains("statement_timeout=30000", result, StringComparison.Ordinal);
        Assert.Contains(existing.Split(' ')[1], result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-c statement_timeout=5000")]
    [InlineData("-c application_name=qgis -c statement_timeout=5000")]
    [InlineData("-c STATEMENT_TIMEOUT=5000")]
    public void An_operator_who_set_this_option_keeps_their_value(string existing)
    {
        // <b>The deference the original comment intended, and it is still
        // right.</b> Somebody who names `statement_timeout` has said something
        // about this database; overriding them would be us assuming we know
        // their workload better than they do. Case-insensitively, because
        // PostgreSQL's parameter names are.
        string result = Options($"{Base};Options={existing}");

        Assert.Equal(existing, result);
        Assert.DoesNotContain("30000", result, StringComparison.Ordinal);
    }
}
