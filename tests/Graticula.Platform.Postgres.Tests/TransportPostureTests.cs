using System;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A source that is encrypted and unauthenticated says so, and the baseline deployment is
/// left alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-190](../../docs/architecture-debt.md)</b>, and
/// [ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) §3 already required it: *remote
/// connections default to the driver's verify mode, not merely require — `require` without
/// verification accepts any certificate and is encryption-without-authentication, which is a
/// weaker guarantee than it appears.* Nothing enforced it and nothing said it, so the probe
/// reported a `Require` source and a `VerifyFull` one identically.
/// </para>
/// <para>
/// <b>The measurement behind it</b>, `ExpiredSourceCertificateTests`: against a server
/// offering a certificate that expired yesterday, `VerifyCA` and `VerifyFull` refuse the
/// handshake and **`Require` completes it**. Npgsql 9 follows libpq, where `Require` means
/// *fail if the server will not encrypt* and nothing more.
/// </para>
/// </remarks>
public sealed class TransportPostureTests
{
    private static string? CautionFor(string connectionString) =>
        TransportPosture.Caution(new NpgsqlConnectionStringBuilder(connectionString));

    [Theory]
    [InlineData("Ssl Mode=Require", "does **not** check")]
    [InlineData("Ssl Mode=Prefer", "may not be encrypted at all")]
    [InlineData("Ssl Mode=Disable", "in the clear")]
    [InlineData("Ssl Mode=Allow", "in the clear")]
    public void A_remote_source_that_is_not_verified_is_named(string mode, string expected)
    {
        string? caution = CautionFor($"Host=db.example.com;Username=a;Password=b;{mode}");

        Assert.NotNull(caution);
        Assert.Contains(expected, caution, StringComparison.Ordinal);

        // <b>A warning an operator cannot act on is one they learn to skip.</b> Every branch
        // names the setting that fixes it.
        Assert.Contains("VerifyFull", caution, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ssl Mode=VerifyFull")]
    [InlineData("Ssl Mode=VerifyCA")]
    public void A_remote_source_that_verifies_is_left_alone(string mode)
    {
        Assert.Null(CautionFor($"Host=db.example.com;Username=a;Password=b;{mode}"));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("/var/run/postgresql")]
    public void The_baseline_deployment_is_not_warned_about(string host)
    {
        // <b>§3's own sentence.</b> *TLS is required for remote data sources and optional for
        // local ones, since a unix socket or loopback connection to a co-located datastore
        // gains little from encryption and pays a handshake for it* — 2.8 ms of it, measured
        // in `benchmarks/tls-handshake`. This server beside its own PostGIS is the baseline
        // deployment, and warning about it is how a warning becomes noise.
        Assert.Null(CautionFor($"Host={host};Username=a;Password=b;Ssl Mode=Disable"));
    }

    [Fact]
    public void A_name_that_merely_resolves_to_loopback_is_still_remote()
    {
        // The operator wrote a name that can point elsewhere tomorrow. A caution that
        // disappears because of today's DNS is worse than one that is occasionally
        // unnecessary.
        Assert.NotNull(CautionFor("Host=db.internal;Username=a;Password=b;Ssl Mode=Require"));
    }
}
