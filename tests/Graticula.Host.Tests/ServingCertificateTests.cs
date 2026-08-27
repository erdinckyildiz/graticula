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

    [Fact]
    public void A_certificate_with_a_week_left_says_so_before_the_night_it_goes()
    {
        DateTimeOffset now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = Expiring(now.AddDays(5));

        object description = ServingCertificate.Describe(certificate, now)!;

        Assert.Equal("expiring", Field<string>(description, "state"));
        Assert.Equal(5, Field<double>(description, "daysRemaining"));

        // An operator planning the replacement is told the cost of it in the same breath,
        // because rotation without a restart is not built (ADR-014 condition 1).
        Assert.Contains("restart", Field<string>(description, "note"), StringComparison.Ordinal);
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
