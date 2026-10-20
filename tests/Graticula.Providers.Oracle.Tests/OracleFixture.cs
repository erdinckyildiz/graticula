using System;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>
/// The Oracle database ADR-205's tests read: an <c>oracle:</c> locator in <c>GRATICULA_TEST_ORACLE</c>, naming a schema
/// that holds <c>TR_KARA</c> and <c>TR_YER</c> as the VPS's <c>TRDATA</c> does.
/// </summary>
/// <remarks>
/// <b>Fails rather than skips when it is not set</b>, as every database-backed suite here does: a test that goes green
/// with its subject absent is worse than no test. Filter it out deliberately with <c>--filter Needs!=Oracle</c>.
/// </remarks>
internal static class OracleFixture
{
    public const string Variable = "GRATICULA_TEST_ORACLE";

    public static string Locator
    {
        get
        {
            string? stored = Environment.GetEnvironmentVariable(Variable);
            Assert.True(
                stored is not null && OracleLocator.Is(stored),
                $"{Variable} is not set to an oracle: locator, so these tests FAIL rather than skip. Set it to "
                + "oracle:{\"host\":…,\"port\":…,\"service\":…,\"user\":…,\"password\":…} for a schema holding TR_KARA "
                + "and TR_YER, or filter them out deliberately with --filter Needs!=Oracle.");
            return stored!;
        }
    }

    public static OracleSource Source => OracleLocator.Of(Locator);
}
