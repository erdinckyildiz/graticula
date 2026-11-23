using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Graticula.Host.Pki;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-315 §5.2–§5.4 without a server: the fields a certificate names an account by, the shapes a proxy forwards it in,
/// and the settings that refuse to start.
/// </summary>
public sealed class ACertificateIsReadByItsFieldTests
{
    [Fact]
    public void Each_field_is_read_from_where_the_certificate_keeps_it()
    {
        using TestPki pki = new("Field CA");
        using X509Certificate2 card = pki.Issue("Ada Lovelace", email: "ada@example.test", upn: "alovelace@corp.example.test");

        Assert.Equal("Ada Lovelace", ClientCertificates.Field(card, "cn"));
        Assert.Equal("ada@example.test", ClientCertificates.Field(card, "email"));
        Assert.Equal("alovelace@corp.example.test", ClientCertificates.Field(card, "upn"));

        using X509Certificate2 bare = pki.Issue(null);
        Assert.Null(ClientCertificates.Field(bare, "cn"));
        Assert.Null(ClientCertificates.Field(bare, "email"));
        Assert.Null(ClientCertificates.Field(bare, "upn"));
    }

    [Theory]
    [InlineData("Ada Lovelace", "Ada.Lovelace")]
    [InlineData("ada@example.test", "ada")]
    [InlineData("  Jane   Q  Public ", "Jane.Q.Public")]
    public void An_account_name_is_the_part_before_an_at_with_spaces_as_dots(string value, string expected) =>
        Assert.Equal(expected, ClientCertificates.AccountName(value));

    [Fact]
    public void A_forwarded_certificate_is_read_as_der_pem_or_url_encoded_pem()
    {
        using TestPki pki = new("Header CA");
        using X509Certificate2 card = pki.Issue("Grace Hopper");

        foreach (string forwarded in new[]
        {
            Convert.ToBase64String(card.RawData),
            card.ExportCertificatePem(),
            Uri.EscapeDataString(card.ExportCertificatePem()),
        })
        {
            using X509Certificate2? read = ClientCertificates.FromHeader(forwarded);
            Assert.NotNull(read);
            Assert.Equal(card.Thumbprint, read.Thumbprint);
        }

        Assert.Null(ClientCertificates.FromHeader("not a certificate"));
        Assert.Null(ClientCertificates.FromHeader(""));
    }

    /// <summary>
    /// The header is read from a trusted proxy and nobody else; and Caddy's placeholder, left as written when the client
    /// presented no certificate (Caddy 2.10.2, measured 2026-11-23), is no certificate rather than a broken one.
    /// </summary>
    [Fact]
    public async Task The_header_is_a_trusted_proxys_and_an_unreplaced_placeholder_is_none()
    {
        using TestPki pki = new("Presented CA");
        using X509Certificate2 card = pki.Issue("Grace Hopper");
        ClientCertificates reader = new(
            new ClientCertificateSettings(ClientCertificateMode.Off, [pki.Authority], "X-Client-Cert", RevocationPolicy.Off, null),
            CallerAddress.Trusted("127.0.0.1"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientCertificates>.Instance);

        async Task<(X509Certificate2? Certificate, string? Ignored)> FromAsync(string peer, string value)
        {
            Microsoft.AspNetCore.Http.DefaultHttpContext context = new();
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
            context.Request.Headers["X-Client-Cert"] = value;
            return await reader.PresentedAsync(context);
        }

        (X509Certificate2? trusted, _) = await FromAsync("127.0.0.1", Convert.ToBase64String(card.RawData));
        Assert.Equal(card.Thumbprint, trusted?.Thumbprint);
        Assert.Null(reader.Refusal(trusted!));

        (X509Certificate2? stranger, string? ignored) = await FromAsync("10.1.2.3", Convert.ToBase64String(card.RawData));
        Assert.Null(stranger);
        Assert.Contains("not a trusted proxy", ignored, StringComparison.Ordinal);

        (X509Certificate2? placeholder, string? said) = await FromAsync("127.0.0.1", "{http.request.tls.client.certificate_der_base64}");
        Assert.Null(placeholder);
        Assert.Null(said);
    }

    [Fact]
    public void Off_is_the_default_and_reads_no_authorities()
    {
        ClientCertificateSettings settings = Read(new Dictionary<string, string?>(), requireHttps: true, proxies: false);
        Assert.False(settings.Enabled);
        Assert.Equal(RevocationPolicy.SoftFail, settings.Revocation);
    }

    [Fact]
    public void A_setting_that_cannot_work_refuses_to_start()
    {
        using TestPki pki = new("Settings CA");
        string directory = Path.Combine(Path.GetTempPath(), "graticula-pki-settings-" + Guid.NewGuid().ToString("N")[..8]);
        string authorities = pki.WriteTo(directory);

        try
        {
            Refused("is not set", new() { ["Graticula:ClientCertificates:Mode"] = "Allow" }, requireHttps: true, proxies: false);
            Refused("RequireHttps is false", new()
            {
                ["Graticula:ClientCertificates:Mode"] = "Require", ["Graticula:ClientCertificates:Authorities"] = authorities,
            }, requireHttps: false, proxies: false);
            Refused("TrustedProxies is empty", new()
            {
                ["Graticula:ClientCertificates:Header"] = "X-Client-Cert", ["Graticula:ClientCertificates:Authorities"] = authorities,
            }, requireHttps: true, proxies: false);
            Refused("one of Off, Allow or Require", new() { ["Graticula:ClientCertificates:Mode"] = "Sometimes" }, requireHttps: true, proxies: false);
            Refused("could not be read", new()
            {
                ["Graticula:ClientCertificates:Mode"] = "Allow",
                ["Graticula:ClientCertificates:Authorities"] = Path.Combine(directory, "missing.pem"),
            }, requireHttps: true, proxies: false);

            ClientCertificateSettings working = Read(new()
            {
                ["Graticula:ClientCertificates:Header"] = "X-Client-Cert",
                ["Graticula:ClientCertificates:Authorities"] = authorities,
                ["Graticula:ClientCertificates:Revocation"] = "hardfail",
            }, requireHttps: false, proxies: true);
            Assert.True(working.Enabled);
            Assert.Equal(ClientCertificateMode.Off, working.Mode);
            Assert.Equal(RevocationPolicy.HardFail, working.Revocation);
            Assert.Single(working.Authorities);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Refused(string says, Dictionary<string, string?> values, bool requireHttps, bool proxies)
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => Read(values, requireHttps, proxies));
        Assert.Contains(says, refused.Message, StringComparison.Ordinal);
    }

    private static ClientCertificateSettings Read(Dictionary<string, string?> values, bool requireHttps, bool proxies) =>
        ClientCertificateSettings.Read(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            requireHttps,
            proxies ? CallerAddress.Trusted("127.0.0.1") : []);
}
