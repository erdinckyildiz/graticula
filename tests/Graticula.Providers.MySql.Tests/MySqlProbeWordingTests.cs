using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>
/// What the registration probe says when it cannot get as far as a database — D-310's design review read both on the
/// console: an argument exception's own text, ending "(Parameter 'stored')", and "The database refused" over a port
/// nothing listens on. Neither needs a database to check.
/// </summary>
public sealed class MySqlProbeWordingTests
{
    [Fact]
    public async Task A_locator_missing_its_fields_is_said_as_what_is_missing()
    {
        ProbeResult said = await new MySqlDataSourceProbe().ProbeAsync("mysql:{\"host\":\"\",\"port\":0}", CancellationToken.None);

        Assert.Equal(ProbeOutcome.CannotConnect, said.Outcome);
        Assert.DoesNotContain("Parameter", said.Message, StringComparison.Ordinal);
        Assert.Contains("a host, a port, a database and a user", said.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_port_nothing_answers_on_is_said_as_where_it_looked_not_as_a_refusal()
    {
        ProbeResult said = await new MySqlDataSourceProbe().ProbeAsync(
            "mysql:{\"host\":\"127.0.0.1\",\"port\":1,\"database\":\"x\",\"user\":\"u\",\"password\":\"p\"}", CancellationToken.None);

        Assert.Equal(ProbeOutcome.CannotConnect, said.Outcome);
        Assert.Contains("Nothing answered at 127.0.0.1:1", said.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", said.Message, StringComparison.OrdinalIgnoreCase);
    }
}
