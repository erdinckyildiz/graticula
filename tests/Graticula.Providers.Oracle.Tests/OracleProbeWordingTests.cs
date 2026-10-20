using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>
/// A locator missing its fields is said as what is missing, not as the argument exception's own text ending
/// "(Parameter 'stored')" — found in the console by D-310's design review. Needs no database.
/// </summary>
public sealed class OracleProbeWordingTests
{
    [Fact]
    public async Task A_locator_missing_its_service_is_said_as_what_is_missing()
    {
        ProbeResult said = await new OracleDataSourceProbe().ProbeAsync(
            "oracle:{\"host\":\"127.0.0.1\",\"port\":1521,\"service\":\"\",\"user\":\"u\"}", CancellationToken.None);

        Assert.Equal(ProbeOutcome.CannotConnect, said.Outcome);
        Assert.DoesNotContain("Parameter", said.Message, StringComparison.Ordinal);
        Assert.Contains("FREEPDB1", said.Message, StringComparison.Ordinal);
    }
}
