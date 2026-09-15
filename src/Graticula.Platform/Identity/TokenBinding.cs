using System;
using System.Net;

namespace Graticula.Platform.Identity;

/// <summary>
/// What an ArcGIS token is bound to — a caller address or a referer — and whether a request is one
/// the token admits. ADR-015 §4 mitigation 3, D-268.
/// </summary>
/// <remarks>
/// <para>
/// <b>ArcGIS's <c>client</c> parameter, as its token documentation describes it.</b>
/// <c>client=requestip</c> binds the token to the address that asked for it, <c>client=ip</c> to an
/// address the caller names in <c>ip</c>, and <c>client=referer</c> to the <c>referer</c> it names.
/// A token presented from anywhere else is not a token: the request is answered as though it
/// carried an unrecognised one, which on the ArcGIS surface is 498.
/// </para>
/// <para>
/// <b>No <c>client</c> is no binding</b>, which is what every token this server issued before
/// 2026-09-15 was. Binding by default would break every caller whose address moves between the
/// token request and the next one — a phone, a pool of egress addresses — for a promise the caller
/// did not ask for.
/// </para>
/// <para>
/// <b>A referer is compared by origin when it is a URL.</b> Browsers send only the origin across
/// origins under the default <c>Referrer-Policy</c>, so a token bound to
/// <c>https://maps.example.com/app/</c> is used from requests whose <c>Referer</c> is
/// <c>https://maps.example.com/</c>; comparing whole URLs would refuse the application the token was
/// made for. A referer that is not a URL — the ArcGIS API for Python sends <c>http</c> — must match
/// the header exactly. With no <c>Referer</c>, the <c>Origin</c> header stands in; with neither, a
/// referer-bound token is refused.
/// </para>
/// </remarks>
public static class TokenBinding
{
    private const string IpPrefix = "ip:";
    private const string RefererPrefix = "referer:";

    /// <summary>
    /// Reads a token request's <c>client</c>, <c>referer</c> and <c>ip</c> into the binding to store.
    /// </summary>
    /// <param name="client">The <c>client</c> parameter, or null.</param>
    /// <param name="referer">The <c>referer</c> parameter, or null.</param>
    /// <param name="ip">The <c>ip</c> parameter, or null.</param>
    /// <param name="caller">The address the request came from, or null when it is unknown.</param>
    /// <param name="bound">What to store, or null for an unbound token.</param>
    /// <param name="error">Why the request cannot be honoured.</param>
    /// <returns>Whether a token may be issued.</returns>
    public static bool TryRead(
        string? client, string? referer, string? ip, IPAddress? caller, out string? bound, out string? error)
    {
        bound = null;
        error = null;

        switch (client?.Trim().ToLowerInvariant())
        {
            case null or "":
                return true;

            case "requestip":
                if (caller is null)
                {
                    error = "'client=requestip' binds the token to the address this request came from, and that address is not known here.";
                    return false;
                }

                bound = IpPrefix + Normal(caller);
                return true;

            case "ip":
                if (!IPAddress.TryParse(ip?.Trim(), out IPAddress? named))
                {
                    error = "'client=ip' needs the address to bind the token to in 'ip'.";
                    return false;
                }

                bound = IpPrefix + Normal(named);
                return true;

            case "referer":
                if (string.IsNullOrWhiteSpace(referer))
                {
                    error = "'client=referer' needs the referer to bind the token to in 'referer'.";
                    return false;
                }

                if (referer.Trim().Length > 2048)
                {
                    error = "'referer' is longer than 2,048 characters.";
                    return false;
                }

                bound = RefererPrefix + referer.Trim();
                return true;

            default:
                error = $"'client={client}' is not one of referer, ip or requestip.";
                return false;
        }
    }

    /// <summary>Whether a request may use a token with this binding.</summary>
    /// <param name="bound">The stored binding, or null for none.</param>
    /// <param name="caller">The address the request came from, or null.</param>
    /// <param name="refererHeader">The request's <c>Referer</c> header, or null.</param>
    /// <param name="originHeader">The request's <c>Origin</c> header, or null.</param>
    /// <returns>Whether the token is honoured for this request.</returns>
    public static bool Admits(string? bound, IPAddress? caller, string? refererHeader, string? originHeader)
    {
        if (string.IsNullOrEmpty(bound))
        {
            return true;
        }

        if (bound.StartsWith(IpPrefix, StringComparison.Ordinal))
        {
            return caller is not null
                && IPAddress.TryParse(bound.AsSpan(IpPrefix.Length), out IPAddress? address)
                && Normal(address) == Normal(caller);
        }

        if (bound.StartsWith(RefererPrefix, StringComparison.Ordinal))
        {
            string expected = bound[RefererPrefix.Length..];
            string? presented = string.IsNullOrWhiteSpace(refererHeader) ? originHeader : refererHeader;

            if (string.IsNullOrWhiteSpace(presented))
            {
                return false;
            }

            if (OriginOf(expected) is { } origin)
            {
                return OriginOf(presented) is { } seen && string.Equals(origin, seen, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(expected, presented.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // A binding this build does not know is refused rather than ignored: a newer build wrote it,
        // and honouring the token would drop the restriction it was issued under.
        return false;
    }

    private static string? OriginOf(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    private static string Normal(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
