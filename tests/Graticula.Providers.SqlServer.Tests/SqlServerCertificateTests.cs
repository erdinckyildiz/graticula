using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>
/// D-313: a registered SQL Server's certificate is trusted unless the registration asks for it to be verified, and asked,
/// a server whose certificate this machine does not trust is refused rather than read from.
/// </summary>
public sealed class SqlServerCertificateTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public void A_registration_that_does_not_ask_trusts_the_certificate_and_one_that_asks_verifies_it()
    {
        SqlServerSource source = new("db.example", 1433, "parcels", "reader", "secret");

        SqlConnectionStringBuilder trusted = new(SqlServerConnections.For(SqlServerLocator.For(source), pooling: false));
        SqlConnectionStringBuilder verified = new(SqlServerConnections.For(
            SqlServerLocator.For(source with { VerifyCertificate = true }), pooling: false));

        Assert.True(trusted.TrustServerCertificate);
        Assert.False(verified.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, verified.Encrypt);
    }

    [Fact]
    public void A_locator_written_before_the_field_existed_still_trusts()
    {
        const string Old = "sqlserver:{\"host\":\"db.example\",\"port\":1433,\"database\":\"parcels\",\"user\":\"reader\",\"password\":null}";

        Assert.False(SqlServerLocator.Of(Old).VerifyCertificate);
        Assert.True(new SqlConnectionStringBuilder(SqlServerConnections.For(Old, pooling: false)).TrustServerCertificate);
    }

    [Fact]
    public void A_tls_failure_is_recognised_inside_the_driver_s_own_exception_and_a_login_failure_is_not()
    {
        System.Exception linux = new System.InvalidOperationException(
            "A connection was successfully established with the server, but then an error occurred during the pre-login "
            + "handshake. (provider: TCP Provider, error: 35 - An internal exception was caught)",
            new System.Security.Authentication.AuthenticationException("The remote certificate was rejected."));

        Assert.True(SqlServerDataSourceProbe.CertificateRefused(linux));
        Assert.False(SqlServerDataSourceProbe.CertificateRefused(
            new System.InvalidOperationException("Login failed for user 'reader'.")));
    }

    /// <summary>
    /// The mechanism, against a real server. Over TCP — the official image in CI — the test database presents a
    /// self-signed certificate, so asked to verify it the probe refuses and says why, and not asked it connects.
    /// <b>LocalDB is reached over shared memory and presents no certificate at all</b> (measured 2026-10-29: verifying,
    /// it connected), so there the same registration connects either way — which is also what a LocalDB registration
    /// that ticks the box should expect.
    /// </summary>
    [Fact]
    [Trait("Needs", "SqlServer")]
    public async Task A_self_signed_server_is_refused_when_its_certificate_is_to_be_verified()
    {
        SqlServerSource source = SqlServerLocator.Of(SqlServerFixture.Locator);
        SqlServerDataSourceProbe probe = new();

        ProbeResult trusting = await probe.ProbeAsync(SqlServerLocator.For(source), None);
        ProbeResult verifying = await probe.ProbeAsync(SqlServerLocator.For(source with { VerifyCertificate = true }), None);

        Assert.Equal(ProbeOutcome.Usable, trusting.Outcome);

        if (source.Host.StartsWith("(localdb)", System.StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(ProbeOutcome.Usable, verifying.Outcome);
            return;
        }

        Assert.Equal(ProbeOutcome.CannotConnect, verifying.Outcome);
        Assert.StartsWith("The server's certificate was not accepted", verifying.Message, System.StringComparison.Ordinal);
    }
}
