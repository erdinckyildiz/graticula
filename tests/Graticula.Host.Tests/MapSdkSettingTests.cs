using System;
using System.Collections.Generic;
using Graticula.Host;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The SDK's address and the policy that has to allow it come from one setting.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-034](../../docs/adr/ADR-034-server-and-studio.md) condition 3.</b> *"The SDK setting
/// and the Content-Security-Policy move together, asserted by a test that sets a different SDK
/// origin and checks the policy names it. Otherwise §5e is a trap rather than a setting."*
/// </para>
/// <para>
/// <b>The trap is [D-44](../../docs/architecture-debt.md) and it has already happened once.</b>
/// A script the policy does not allow is fetched and then refused by the browser, after the
/// response has left this server — so the page renders with a dead map and the server's log
/// shows a successful request. There is nothing to notice. That is why this is asserted rather
/// than reasoned about.
/// </para>
/// <para>
/// <b>Five sources, and each is asserted separately.</b> Dropping one of them produces a map
/// that half works — an unstyled view, missing icons, modules that never arrive — which is
/// harder to diagnose than a map that does not load at all.
/// </para>
/// </remarks>
public sealed class MapSdkSettingTests
{
    /// <summary>An address that is obviously not Esri's, so a leftover literal shows up.</summary>
    private const string Elsewhere = "https://sdk.inside.example:8443/arcgis/4.29/";

    /// <summary>Its origin, which is what a policy source may name.</summary>
    private const string ElsewhereOrigin = "https://sdk.inside.example:8443";

    /// <summary>Every policy source the SDK has to be named in.</summary>
    private static readonly string[] Sources =
        ["script-src", "style-src", "img-src", "connect-src", "font-src"];

    [Fact]
    public void A_different_sdk_origin_is_named_by_every_source_that_needs_it()
    {
        string policy = SecurityHeaders.ConsolePolicyFor(ElsewhereOrigin);

        foreach (string source in Sources)
        {
            string directive = Directive(policy, source);

            Assert.True(
                directive.Contains(ElsewhereOrigin, StringComparison.Ordinal),
                $"{source} does not name the configured SDK origin: '{directive}'. A source the "
                + "policy omits is a request the browser refuses after this server has already "
                + "answered, which is D-44 and leaves nothing in the log.");
        }
    }

    /// <summary>
    /// Nothing in the policy still names the default when the setting says otherwise.
    /// </summary>
    /// <remarks>
    /// <b>The half that catches a literal left behind.</b> A policy that named both origins
    /// would pass the assertion above and still be wrong: the operator's SDK would work and
    /// Esri's would remain permitted, which is a third-party origin nobody chose.
    /// </remarks>
    [Fact]
    public void The_default_origin_disappears_when_the_setting_moves()
    {
        string policy = SecurityHeaders.ConsolePolicyFor(ElsewhereOrigin);

        Assert.DoesNotContain("js.arcgis.com", policy, StringComparison.Ordinal);
    }

    /// <summary>The unset case is the behaviour every existing deployment has.</summary>
    [Fact]
    public void The_default_setting_produces_the_policy_this_console_shipped_with()
    {
        HostSettings settings = Read([]);

        Assert.Equal(HostSettings.DefaultMapSdkUrl, settings.MapSdkUrl);
        Assert.Equal("https://js.arcgis.com", settings.MapSdkOrigin);

        string policy = SecurityHeaders.ConsolePolicyFor(settings.MapSdkOrigin);

        Assert.Contains("script-src 'self' https://js.arcgis.com;", policy, StringComparison.Ordinal);
    }

    /// <summary>The origin is the origin, and a path in a policy source is not one.</summary>
    [Fact]
    public void The_origin_drops_the_path_the_pages_need()
    {
        HostSettings settings = Read([new("Graticula:MapSdkUrl", Elsewhere)]);

        Assert.Equal(Elsewhere, settings.MapSdkUrl);
        Assert.Equal(ElsewhereOrigin, settings.MapSdkOrigin);
    }

    /// <summary>
    /// A value that is not a URL stops the server rather than producing a policy.
    /// </summary>
    /// <remarks>
    /// <b>[D-171](../../docs/architecture-debt.md)'s rule applied to this key.</b> A policy
    /// naming something that is not an origin is a policy the browser discards — which fails
    /// open, in the one direction that matters. The server says which key and stops.
    /// </remarks>
    [Theory]
    [InlineData("not a url")]
    [InlineData("js.arcgis.com/4.29/")]
    [InlineData("ftp://example.test/sdk/")]
    public void A_value_that_is_not_an_http_url_is_refused_by_name(string value)
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => Read([new("Graticula:MapSdkUrl", value)]));

        Assert.Contains("Graticula:MapSdkUrl", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A URL without its trailing slash is refused, because the pages append to it.
    /// </summary>
    /// <remarks>
    /// <b>The failure it prevents is a working server with a broken map.</b>
    /// `esri/themes/light/main.css` appended to `.../4.29` resolves one directory too high, so
    /// the policy would be right, the request would be allowed, and the theme would 404 — a
    /// diagnosis that starts by suspecting everything except a missing slash.
    /// </remarks>
    [Fact]
    public void A_url_without_its_trailing_slash_is_refused_with_the_reason()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => Read([new("Graticula:MapSdkUrl", "https://sdk.inside.example/arcgis/4.29")]));

        Assert.Contains("must end with '/'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>One directive out of a policy, without its name.</summary>
    private static string Directive(string policy, string name)
    {
        foreach (string part in policy.Split(';', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(name + " ", StringComparison.Ordinal))
            {
                return part;
            }
        }

        return $"<no {name} directive>";
    }

    /// <summary>Settings read from the minimum a server needs, plus what a test says.</summary>
    private static HostSettings Read(IEnumerable<KeyValuePair<string, string?>> extra)
    {
        List<KeyValuePair<string, string?>> values =
        [
            new("Graticula:PlatformStore", "Host=localhost;Database=gis;Username=gis;Password=gis"),
            new("Graticula:SecretKey", Convert.ToBase64String(new byte[32])),
            new("Graticula:RequireHttps", "false"),
            .. extra,
        ];

        return HostSettings.Read(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
}
