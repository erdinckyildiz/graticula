using System;
using System.Collections.Generic;
using System.Net;
using Graticula.Api.Wms;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The switch that lets a deployment refuse a token in the query string.
/// </summary>
/// <remarks>
/// <para>
/// <b><see href="../../docs/architecture-debt.md">D-120</see>'s stated closing
/// condition.</b> Esri clients send a session token as <c>?token=</c> on ordinary requests,
/// so a URL carrying a live credential reaches this server's log and every proxy's and
/// every browser history in between. <c>QueryRedaction</c> keeps it out of ours; nothing we
/// ship reaches theirs. The row closes when the channel can be switched off in a deployment
/// whose clients do not need it.
/// </para>
/// <para>
/// <b>The default is the half that matters most.</b> Off by default would refuse every
/// unmodified ArcGIS client the first time somebody upgraded, which is the whole of Q-17's
/// promise — so the test that would catch that mistake is the one asserting the default is
/// true, and it is asserted from a configuration with the key absent rather than from the
/// record's own default, because absent is what a real deployment has.
/// </para>
/// </remarks>
public sealed class TokenQueryChannelTests
{
    private static HostSettings Read(params (string Key, string Value)[] overrides)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["Graticula:PlatformStore"] = "Host=localhost;Database=x;Username=x;Password=x",

            // A real 32-byte key, because Read validates the length and a placeholder would
            // fail for a reason that has nothing to do with what is under test.
            ["Graticula:SecretKey"] = Convert.ToBase64String(new byte[32]),
        };

        foreach ((string key, string value) in overrides)
        {
            values[key] = value;
        }

        IConfiguration configuration =
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        return HostSettings.Read(configuration);
    }

    /// <summary>Absent means on, because absent is what an upgrading deployment has.</summary>
    [Fact]
    public void The_query_channel_is_accepted_when_nothing_says_otherwise()
    {
        Assert.True(
            Read().AcceptTokenInQueryString,
            "A deployment that says nothing must keep accepting ?token=. Every unmodified "
            + "ArcGIS client sends it, and defaulting this off would refuse all of them on "
            + "upgrade. D-120, Q-17.");
    }

    /// <summary>And a deployment can turn it off.</summary>
    [Fact]
    public void A_deployment_can_refuse_the_query_channel()
    {
        Assert.False(
            Read(("Graticula:AcceptTokenInQueryString", "false")).AcceptTokenInQueryString,
            "D-120 closes when the query channel can be switched off in a deployment whose "
            + "clients do not need it. If this setting is not read, it cannot be.");
    }

    /// <summary>The legacy key spelling reaches it too.</summary>
    /// <remarks>
    /// <b>ADR-032 §5: <c>GisServer:*</c> is still read</b> so that no existing deployment has
    /// to be reconfigured to start. A setting added after the rename is the one most likely
    /// to be wired to the new name only, which is why this is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void The_old_configuration_prefix_reaches_the_switch_too()
    {
        Assert.False(
            Read(("GisServer:AcceptTokenInQueryString", "false")).AcceptTokenInQueryString,
            "A deployment configured under the GisServer prefix cannot turn the query channel "
            + "off, so ADR-032 §5's promise that existing configuration keeps working does not "
            + "hold for this setting.");
    }
}
