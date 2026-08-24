using System;
using System.Collections.Generic;
using System.Net;
using Graticula.Host;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A forwarded header is read from a trusted proxy and from nobody else.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-12](../../docs/architecture-debt.md): the per-address rate limit used the socket address,
/// never <c>X-Forwarded-For</c>.</b> Behind a reverse proxy that made the limit one shared bucket
/// — fifty failures anywhere disabling sign-in for everybody.
/// </para>
/// <para>
/// <b>The row stood for eleven days because the obvious repair is worse than the fault.</b>
/// Trusting the header from anybody lets every caller choose their own rate-limit bucket, which
/// makes the limit zero. Too coarse is recoverable; forgeable is not. So the tests that matter
/// most here are the ones where the header is ignored.
/// </para>
/// </remarks>
public sealed class CallerAddressTests
{
    private static DefaultHttpContext Request(string socket, string? forwarded = null)
    {
        DefaultHttpContext context = new();

        context.Connection.RemoteIpAddress = IPAddress.Parse(socket);

        if (forwarded is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwarded;
        }

        return context;
    }

    private static IPAddress? Resolve(HttpContext context, params string[] trusted) =>
        CallerAddress.Resolve(context, CallerAddress.Trusted(string.Join(',', trusted)));

    /// <summary>
    /// With nothing configured, this is exactly what it was before the repair.
    /// </summary>
    /// <remarks>
    /// <b>The property that makes the change safe to ship.</b> Every existing deployment has no
    /// trusted proxies, so every existing deployment sees the socket address for every request —
    /// including one sending a forged header, which is the case below.
    /// </remarks>
    [Fact]
    public void With_no_trusted_proxies_the_socket_is_the_caller()
    {
        Assert.Equal(
            IPAddress.Parse("203.0.113.9"),
            Resolve(Request("203.0.113.9", "198.51.100.1")));
    }

    /// <summary>
    /// A caller who is not a proxy cannot speak for somebody else.
    /// </summary>
    /// <remarks>
    /// <b>This is the clause that makes the header unforgeable.</b> The rate limit is what it
    /// guards: an attacker who could set their own bucket would face no limit at all, which is
    /// worse than the shared bucket this row is about.
    /// </remarks>
    [Fact]
    public void A_caller_who_is_not_a_trusted_proxy_is_themselves()
    {
        Assert.Equal(
            IPAddress.Parse("203.0.113.9"),
            Resolve(Request("203.0.113.9", "198.51.100.1"), "10.0.0.0/8"));
    }

    /// <summary>
    /// A trusted proxy speaks for the client behind it.
    /// </summary>
    [Fact]
    public void A_trusted_proxy_speaks_for_the_caller_behind_it()
    {
        Assert.Equal(
            IPAddress.Parse("198.51.100.1"),
            Resolve(Request("10.0.0.4", "198.51.100.1"), "10.0.0.0/8"));
    }

    /// <summary>
    /// Two proxies in a row: the answer is the last hop this deployment cannot vouch for.
    /// </summary>
    /// <remarks>
    /// <b>Right to left, because the header is appended to.</b> The rightmost entry is what the
    /// nearest proxy wrote and the leftmost is whatever the original client claimed — which
    /// anybody can write. Stopping at the first untrusted address from the right gives the
    /// strongest thing the header can honestly say.
    /// </remarks>
    [Fact]
    public void A_chain_of_trusted_proxies_resolves_to_the_first_hop_outside_it()
    {
        Assert.Equal(
            IPAddress.Parse("198.51.100.1"),
            Resolve(
                Request("10.0.0.4", "203.0.113.200, 198.51.100.1, 10.0.0.7"),
                "10.0.0.0/8"));
    }

    /// <summary>
    /// A client that writes its own hops in front cannot reach past the proxy.
    /// </summary>
    /// <remarks>
    /// <b>The forgery that survives a trusted proxy, and it does not survive this.</b> A client
    /// can put anything at the left of the header; the proxy appends the address it actually saw.
    /// Reading from the right means the invented entries are behind the real one and never
    /// reached.
    /// </remarks>
    [Fact]
    public void Entries_a_client_wrote_itself_are_behind_the_one_the_proxy_appended()
    {
        Assert.Equal(
            IPAddress.Parse("198.51.100.1"),
            Resolve(Request("10.0.0.4", "1.2.3.4, 5.6.7.8, 198.51.100.1"), "10.0.0.0/8"));
    }

    /// <summary>
    /// A hop written with a port still parses.
    /// </summary>
    /// <remarks>
    /// <b>Because proxies write both.</b> Treating <c>203.0.113.7:41234</c> as unparsable would
    /// silently skip the hop that matters and fall through to the one behind it.
    /// </remarks>
    [Theory]
    [InlineData("198.51.100.1:41234", "198.51.100.1")]
    [InlineData("[2001:db8::1]:41234", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void A_hop_with_a_port_is_still_an_address(string written, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), Resolve(Request("10.0.0.4", written), "10.0.0.0/8"));
    }

    /// <summary>
    /// A proxy arriving as an IPv4-mapped IPv6 address still matches an IPv4 range.
    /// </summary>
    /// <remarks>
    /// <b>Kestrel reports <c>::ffff:10.0.0.4</c> on a dual-stack socket</b>, and a deployment
    /// writes <c>10.0.0.0/8</c>. Without this the setting would appear to do nothing, which is
    /// the failure mode this whole repair is trying not to have.
    /// </remarks>
    [Fact]
    public void A_mapped_address_matches_the_range_it_is_written_in()
    {
        Assert.Equal(
            IPAddress.Parse("198.51.100.1"),
            Resolve(Request("::ffff:10.0.0.4", "198.51.100.1"), "10.0.0.0/8"));
    }

    /// <summary>
    /// A single address is a range of one, so a deployment need not write a mask.
    /// </summary>
    [Fact]
    public void A_bare_address_is_accepted_as_a_range_of_one()
    {
        IReadOnlyList<IPNetwork> trusted = CallerAddress.Trusted("10.0.0.4");

        Assert.Single(trusted);

        Assert.Equal(
            IPAddress.Parse("198.51.100.1"),
            CallerAddress.Resolve(Request("10.0.0.4", "198.51.100.1"), trusted));
    }

    /// <summary>
    /// A mistyped entry stops the server rather than being ignored.
    /// </summary>
    /// <remarks>
    /// <b>Because ignoring it produces exactly the state this replaces</b> — the header untrusted,
    /// the limit one shared bucket — with nothing to say anything is wrong. It would be found when
    /// somebody's sign-in is rate-limited by a stranger.
    /// </remarks>
    [Fact]
    public void A_mistyped_proxy_is_refused_rather_than_ignored()
    {
        InvalidOperationException refused =
            Assert.Throws<InvalidOperationException>(() => CallerAddress.Trusted("10.0.0.0/8, nope"));

        Assert.Contains("nope", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trusted proxy that sends no header is itself.
    /// </summary>
    [Fact]
    public void A_trusted_proxy_with_no_header_is_the_caller()
    {
        Assert.Equal(IPAddress.Parse("10.0.0.4"), Resolve(Request("10.0.0.4"), "10.0.0.0/8"));
    }
}
