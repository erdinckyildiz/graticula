using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The outage with a date known in advance is announced before it arrives, and named after.
/// </summary>
/// <remarks>
/// <b>[ADR-017](../../docs/adr/ADR-017-admin-api.md) §3.4, walked 2026-08-27.</b> Step 1 asked
/// that `/admin/health` say **certificate expired at 03:14** rather than leave it to be
/// inferred from a TLS handshake error. The walk found the route answering and carrying no
/// certificate at all — while the process had held one since startup. These tests are what
/// stops that from silently becoming true again.
/// </remarks>
public sealed class ServingCertificateTests
{
    /// <summary>A certificate whose validity window is chosen rather than waited for.</summary>
    private static X509Certificate2 Expiring(DateTimeOffset at)
    {
        using RSA key = RSA.Create(2048);

        CertificateRequest request = new(
            "CN=test.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(at.AddDays(-400), at);
    }

    private static T Field<T>(object description, string name)
    {
        PropertyInfo property = description.GetType().GetProperty(name)
            ?? throw new InvalidOperationException(
                $"The health document's certificate block has no '{name}'. ADR-017 §3.4 step 1 "
                + $"asks for expiry and days remaining; it carries: "
                + string.Join(", ", Array.ConvertAll(
                    description.GetType().GetProperties(), p => p.Name)));

        return (T)property.GetValue(description)!;
    }

    [Fact]
    public void A_certificate_that_expired_last_night_is_named_rather_than_inferred()
    {
        DateTimeOffset expired = new(2026, 8, 27, 3, 14, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = Expiring(expired);

        object description = ServingCertificate.Describe(certificate, expired.AddHours(6))
            ?? throw new InvalidOperationException(
                "A server holding a certificate said nothing about it. This is exactly what "
                + "the ADR-017 walk found on 2026-08-27.");

        Assert.Equal("expired", Field<string>(description, "state"));

        // <b>Negative, and that is the point.</b> Zero would read as *expires today*, which
        // is the difference between a warning and a post-mortem.
        Assert.True(Field<double>(description, "daysRemaining") < 0);

        // The hour and minute reach the operator. "Everything stopped at 03:14" is the
        // sentence §3.4 is named after, and a date alone does not answer it.
        string note = Field<string>(description, "note");
        Assert.Contains("2026-08-27 03:14", note, StringComparison.Ordinal);
        Assert.Contains("expired", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// [ADR-014](../../docs/adr/ADR-014-tls-and-certificates.md) §2c's ladder, rung by rung.
    /// </summary>
    /// <remarks>
    /// <b>*A warning at 30 days, escalating at 7, critical at 1.*</b> §2c gives that duty to a
    /// runtime supervisor that does not exist, and the ladder does not need one to be true —
    /// the operator reading the health page is the person §2c is written for. Written as one
    /// theory rather than four facts because the interesting part is the **boundaries**: a
    /// certificate with exactly seven days left is a warning and not yet escalating, and an
    /// off-by-one here would move a 2 AM page by six days.
    /// </remarks>
    [Theory]
    [InlineData(400, "valid")]
    [InlineData(31, "valid")]
    [InlineData(30, "warning")]
    [InlineData(8, "warning")]
    [InlineData(7, "escalating")]
    [InlineData(2, "escalating")]
    [InlineData(1, "critical")]
    public void The_expiry_ladder_is_the_one_ADR_014_2c_names(int daysLeft, string expected)
    {
        DateTimeOffset now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = Expiring(now.AddDays(daysLeft));

        object description = ServingCertificate.Describe(certificate, now)!;

        Assert.Equal(expected, Field<string>(description, "state"));
        Assert.Equal(daysLeft, Field<double>(description, "daysRemaining"));
    }

    [Fact]
    public void A_certificate_with_a_week_left_says_how_to_replace_it()
    {
        DateTimeOffset now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = Expiring(now.AddDays(5));

        object description = ServingCertificate.Describe(certificate, now)!;

        // An operator planning the replacement is told what it costs in the same breath, and
        // as of ADR-014 condition 1 what it costs is nothing: replacing the file is enough
        // and `CertificateReload` picks it up on the next handshake.
        string note = Field<string>(description, "note");

        Assert.Contains("no restart is needed", note, StringComparison.Ordinal);
        Assert.Contains("2b", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_certificate_with_a_year_left_is_reported_without_alarm()
    {
        DateTimeOffset now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = Expiring(now.AddDays(300));

        object description = ServingCertificate.Describe(certificate, now)!;

        Assert.Equal("valid", Field<string>(description, "state"));
        Assert.Null(Field<string?>(description, "note"));

        // Still reported. A quiet certificate is still a certificate an operator can plan
        // around, and §3.4 step 2 asks for expiry and days remaining on every one we hold.
        Assert.Equal(300, Field<double>(description, "daysRemaining"));
        Assert.Equal(
            certificate.NotAfter.ToUniversalTime(),
            Field<DateTimeOffset>(description, "notAfter"));
    }

    [Fact]
    public void A_plain_http_server_holds_no_certificate_and_claims_none()
    {
        // The key is absent rather than present and null. A block saying `null` reads as a
        // certificate we failed to describe; no block reads as a server not serving TLS,
        // which is what a plain-HTTP deployment is.
        Assert.Null(ServingCertificate.Describe(null, DateTimeOffset.UtcNow));
    }
}
