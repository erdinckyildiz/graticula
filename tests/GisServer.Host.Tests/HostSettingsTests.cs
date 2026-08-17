using System;
using System.Collections.Generic;
using GisServer.Host;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GisServer.Host.Tests;

/// <summary>
/// Reading configuration under the product's name, and under the one it used to have.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-032 condition 2.</b> The product was renamed from <c>gis-server</c> to
/// Graticula on 2026-08-17, which renames its configuration section. One of those keys
/// holds the AES-256 key that seals every registered data-source credential — so a
/// rename that quietly stopped reading the old name would take a working server and
/// leave it unable to open its own catalogue, reporting a *missing* setting rather than
/// a *renamed* one.
/// </para>
/// <para>
/// Both halves are asserted, and the second is the one that decays: that the old keys
/// still work, and that a start using them can be told it did. A compatibility path
/// nobody is told about is one nobody knows to stop relying on, and there is no way to
/// decide when to remove this without knowing whether anybody still needs it.
/// </para>
/// </remarks>
public sealed class HostSettingsTests
{
    // 32 zero bytes, base64: valid AES-256 and obviously not a real key.
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void The_current_names_are_read()
    {
        HostSettings settings = HostSettings.Read(Configuration(new()
        {
            ["Graticula:PlatformStore"] = "Host=localhost;Database=gis",
            ["Graticula:SecretKey"] = Key,
            ["Graticula:Port"] = "9443",
        }));

        Assert.Equal("Host=localhost;Database=gis", settings.PlatformStore);
        Assert.Equal(9443, settings.Port);

        // Nothing to report: this deployment is configured under the product's name.
        Assert.Empty(settings.LegacyKeys ?? []);
    }

    [Fact]
    public void The_former_names_still_start_the_server_and_are_reported()
    {
        HostSettings settings = HostSettings.Read(Configuration(new()
        {
            ["GisServer:PlatformStore"] = "Host=localhost;Database=gis",
            ["GisServer:SecretKey"] = Key,
            ["GisServer:Port"] = "9443",
        }));

        Assert.Equal("Host=localhost;Database=gis", settings.PlatformStore);
        Assert.Equal(Key, settings.SecretKeyBase64);
        Assert.Equal(9443, settings.Port);

        // Named individually rather than counted, because the operator has to move
        // exactly these and a number tells them nothing about which.
        Assert.Equal(
            ["GisServer:PlatformStore", "GisServer:SecretKey", "GisServer:Port"],
            settings.LegacyKeys!);
    }

    /// <summary>
    /// With both set, the product's own name wins.
    /// </summary>
    /// <remarks>
    /// <b>The order matters during a migration and only then.</b> An operator moving
    /// keys one at a time will have both present for a while, and the new one is what
    /// they just wrote — a fallback that shadowed it would make the edit look like it
    /// did nothing.
    /// </remarks>
    [Fact]
    public void The_current_name_wins_over_the_former_one()
    {
        HostSettings settings = HostSettings.Read(Configuration(new()
        {
            ["Graticula:PlatformStore"] = "Host=new;Database=gis",
            ["GisServer:PlatformStore"] = "Host=old;Database=gis",
            ["Graticula:SecretKey"] = Key,
            ["Graticula:Port"] = "9443",
            ["GisServer:Port"] = "1111",
        }));

        Assert.Equal("Host=new;Database=gis", settings.PlatformStore);
        Assert.Equal(9443, settings.Port);
        Assert.Empty(settings.LegacyKeys ?? []);
    }

    /// <summary>
    /// A missing required setting names the new variable and says the old one works.
    /// </summary>
    /// <remarks>
    /// The message is the whole value of this path. Somebody upgrading reads it at the
    /// moment the server refuses to start, and it has to answer both questions they
    /// have: what to set, and whether their existing configuration is now invalid.
    /// </remarks>
    [Fact]
    public void A_missing_setting_is_refused_with_both_names()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => HostSettings.Read(Configuration(new() { ["Graticula:SecretKey"] = Key })));

        Assert.Contains("Graticula:PlatformStore", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Graticula__PlatformStore", refused.Message, StringComparison.Ordinal);
        Assert.Contains("GisServer__PlatformStore", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key that decodes to the wrong length is refused under the product's name.
    /// </summary>
    /// <remarks>
    /// Not a new check — it guards that the rename did not move the validation off the
    /// path the fallback takes, which would let a 16-byte key through when configured
    /// under the old name and refuse it under the new one.
    /// </remarks>
    [Fact]
    public void A_short_key_is_refused_whichever_name_carried_it()
    {
        foreach (string section in new[] { "Graticula", "GisServer" })
        {
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => HostSettings.Read(Configuration(new()
                {
                    [$"{section}:PlatformStore"] = "Host=localhost",
                    [$"{section}:SecretKey"] = "AAAAAAAAAAAAAAAAAAAAAA==",   // 16 bytes
                })));

            Assert.Contains("AES-256 needs 32", refused.Message, StringComparison.Ordinal);
        }
    }
}
