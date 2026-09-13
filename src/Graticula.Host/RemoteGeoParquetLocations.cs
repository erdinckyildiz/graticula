using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Graticula.Platform.Admin;
using Graticula.Providers.DuckDb;

namespace Graticula.Host;

/// <summary>What an administrator sends to describe a remote GeoParquet location — ADR-067 §5.2.</summary>
/// <param name="Url">An <c>s3://bucket/prefix/</c>, an <c>s3://bucket/name.parquet</c>, or an <c>https://…/name.parquet</c>.</param>
/// <param name="Region">The bucket's region.</param>
/// <param name="Endpoint">An S3-compatible endpoint, as <c>host</c> or <c>host:port</c>.</param>
/// <param name="AccessKeyId">The access key id, or null to read anonymously.</param>
/// <param name="SecretAccessKey">The secret access key.</param>
/// <param name="UrlStyle"><c>vhost</c> or <c>path</c>.</param>
/// <param name="UseSsl">False only for an endpoint without TLS.</param>
internal sealed record RemoteLocationRequest(
    string? Url,
    string? Region,
    string? Endpoint,
    string? AccessKeyId,
    string? SecretAccessKey,
    string? UrlStyle,
    bool? UseSsl)
{
    /// <summary>Never the secret: a record's generated text prints every property.</summary>
    /// <returns>The URL and whether credentials came with it.</returns>
    public override string ToString() =>
        $"{Url}{(string.IsNullOrEmpty(AccessKeyId) ? string.Empty : " (with credentials)")}";
}

/// <summary>
/// Turns a remote location request into a sealed locator, and a locator back into a location —
/// with every rule ADR-067 §5.2 puts between an administrator's text and DuckDB.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field that reaches DuckDB has a rule of its own here</b>, because each is bound into a
/// setting as a literal (§5.1.2): quoting makes it one value, and this is what makes it the right
/// kind of value.
/// </para>
/// <para>
/// <b>The address check is at registration and again at every open</b>, because a name that
/// resolved to a public address last week may resolve to a private one today. It does not cover a
/// redirect, which DuckDB follows without asking — ADR-067 §3 and its condition 2.
/// </para>
/// </remarks>
internal static partial class RemoteGeoParquetLocations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Characters DuckDB reads as a pattern, and the ones no location needs.</summary>
    private static readonly System.Buffers.SearchValues<char> Refused =
        System.Buffers.SearchValues.Create("*?[]{}#'\"\\ ");

    /// <summary>The refusal when <c>httpfs</c> is not where the deployment said.</summary>
    public const string Off =
        "Remote GeoParquet is not enabled on this server: it needs DuckDB's httpfs extension, loaded "
        + "from the directory named by Graticula:DuckDbExtensions (the image sets it). Set it to a "
        + "directory holding <platform>/httpfs.duckdb_extension and restart.";

    /// <summary>Validates a request and produces the locator to seal.</summary>
    /// <param name="request">What the administrator sent.</param>
    /// <param name="allowPrivate">Whether a private address is allowed (<c>Graticula:RemoteDataAllowPrivate</c>).</param>
    /// <param name="locator">The locator.</param>
    /// <param name="why">Why it was refused.</param>
    /// <returns>Whether the location may be registered.</returns>
    public static bool TryLocate(RemoteLocationRequest request, bool allowPrivate, out string? locator, out string? why)
    {
        locator = null;

        if (!TryDescribe(request, out RemoteGeoParquet? remote, out why))
        {
            return false;
        }

        if (Unreachable(remote!, allowPrivate) is { } unreachable)
        {
            why = unreachable;
            return false;
        }

        locator = GeoParquetLocator.ForRemote(JsonSerializer.Serialize(new Stored(
            remote!.Location, remote.Region, remote.Endpoint, remote.AccessKeyId, remote.SecretAccessKey,
            remote.UrlStyle, remote.UseSsl), Json));

        return true;
    }

    /// <summary>The location a stored locator describes, credentials included.</summary>
    /// <param name="locator">A remote locator.</param>
    /// <returns>The location.</returns>
    public static RemoteGeoParquet Parse(string locator)
    {
        Stored stored = JsonSerializer.Deserialize<Stored>(GeoParquetLocator.RemoteOf(locator), Json)
            ?? throw new InvalidOperationException("The stored remote location is empty.");

        return new RemoteGeoParquet(
            stored.Location, stored.Region, stored.Endpoint, stored.AccessKeyId, stored.SecretAccessKey,
            stored.UrlStyle, stored.UseSsl);
    }

    /// <summary>Why a location may not be read from here, or null: the address check.</summary>
    /// <param name="remote">The location.</param>
    /// <param name="allowPrivate">Whether private addresses are allowed.</param>
    /// <returns>A sentence, or null.</returns>
    public static string? Unreachable(RemoteGeoParquet remote, bool allowPrivate)
    {
        ArgumentNullException.ThrowIfNull(remote);

        if (allowPrivate)
        {
            return null;
        }

        // <b>Every host the bytes may come from</b>: the https host; for an S3-compatible endpoint the
        // endpoint itself and — unless the URL style is `path` — the bucket prepended to it, which is the
        // host DuckDB actually connects to; and nothing for a bucket on AWS itself, whose hosts are
        // Amazon's. <b>The second was missing</b>, and a security review found what that allowed with no
        // DNS server of one's own: bucket `169.254.169.254`, endpoint `nip.io` — the check resolved
        // `nip.io`, and DuckDB connected to `169.254.169.254.nip.io`.
        List<string> hosts = [];

        if (remote.Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            hosts.Add(new Uri(remote.Location).IdnHost);
        }
        else if (remote.Endpoint is { Length: > 0 } endpoint)
        {
            string host = HostOf(endpoint);
            hosts.Add(host);

            if (!string.Equals(remote.UrlStyle, "path", StringComparison.Ordinal))
            {
                hosts.Add(BucketOf(remote.Location) + "." + host);
            }
        }

        foreach (string host in hosts)
        {
            IPAddress[] addresses;

            try
            {
                addresses = IPAddress.TryParse(host, out IPAddress? literal)
                    ? [literal]
                    : Dns.GetHostAddresses(host);
            }
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                return $"'{host}' does not resolve: {e.Message}";
            }

            if (addresses.Length == 0)
            {
                return $"'{host}' does not resolve to any address.";
            }

            if (addresses.FirstOrDefault(IsPrivate) is { } inward)
            {
                return $"'{host}' resolves to {inward}, which is a private, loopback or link-local address. This "
                    + "server reads a remote location from inside its own network, so an address there is refused "
                    + "unless the deployment sets Graticula:RemoteDataAllowPrivate — for an object store beside it "
                    + "on a private network.";
            }
        }

        return null;
    }

    private static string BucketOf(string location)
    {
        string rest = location["s3://".Length..];
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? rest : rest[..slash];
    }

    /// <summary>Whether an address is one a public location must not resolve to.</summary>
    /// <param name="address">The address.</param>
    /// <returns>True for loopback, private, link-local, unique-local, unspecified and multicast.</returns>
    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal
                || address.IsIPv6Multicast)
            {
                return true;
            }

            // <b>An IPv4 address carried inside an IPv6 one is judged as the IPv4 address</b> — the
            // review's L1: NAT64 (64:ff9b::/96), 6to4 (2002::/16) and the deprecated IPv4-compatible
            // form (::/96) all reach the embedded address on a network that routes them.
            byte[] v6 = address.GetAddressBytes();

            if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b && v6.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            {
                return IsPrivate(new IPAddress(v6.AsSpan(12, 4)));
            }

            if (v6[0] == 0x20 && v6[1] == 0x02)
            {
                return IsPrivate(new IPAddress(v6.AsSpan(2, 4)));
            }

            if (v6.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            {
                return IsPrivate(new IPAddress(v6.AsSpan(12, 4)));
            }

            return false;
        }

        byte[] b = address.GetAddressBytes();

        return b[0] == 10
            || b[0] == 0
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
            || b[0] >= 224;
    }

    private static bool TryDescribe(RemoteLocationRequest request, out RemoteGeoParquet? remote, out string? why)
    {
        remote = null;
        why = null;

        string url = request.Url?.Trim() ?? string.Empty;

        if (url.Length == 0)
        {
            why = "`url` is required: an s3://bucket/prefix/, an s3://bucket/name.parquet, or an https://…/name.parquet.";
            return false;
        }

        if (url.AsSpan().IndexOfAny(Refused) >= 0 || url.Any(char.IsControl))
        {
            why = "The URL holds a character DuckDB reads as a pattern or that no location needs (* ? [ ] { } # quotes, "
                + "backslash, space). A query string is not accepted either: a signed URL expires, and a layer on it "
                + "would stop answering when it did.";
            return false;
        }

        bool s3 = url.StartsWith("s3://", StringComparison.Ordinal);
        bool https = url.StartsWith("https://", StringComparison.Ordinal);

        if (!s3 && !https)
        {
            why = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "Plain http is refused: the file would cross the network unauthenticated and could be altered on the way. Use https."
                : "A remote location is s3:// or https://.";
            return false;
        }

        if (https)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.UserInfo.Length > 0
                || parsed.Host.Length == 0)
            {
                why = "That is not an https URL this server can read: it needs a host, and no user name or password in it.";
                return false;
            }

            if (url.EndsWith('/'))
            {
                why = "An https location is one .parquet file: https cannot list a directory. For many files, use an s3:// prefix.";
                return false;
            }

            if (request.Region is { Length: > 0 } || request.Endpoint is { Length: > 0 } || request.AccessKeyId is { Length: > 0 }
                || request.SecretAccessKey is { Length: > 0 } || request.UrlStyle is { Length: > 0 } || request.UseSsl is false)
            {
                why = "Region, endpoint, credentials and URL style belong to an s3:// location; an https file is read as it is.";
                return false;
            }
        }
        else
        {
            string rest = url["s3://".Length..];
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            string bucket = slash < 0 ? rest : rest[..slash];

            if (!Bucket().IsMatch(bucket))
            {
                why = $"'{bucket}' is not a bucket name: 3 to 63 lower-case letters, digits, dots and hyphens.";
                return false;
            }

            // AWS refuses these too, and one was the review's way past the address check (H1).
            if (IPAddress.TryParse(bucket, out _) || IpShaped().IsMatch(bucket))
            {
                why = $"'{bucket}' is shaped like an IP address, which S3 does not allow as a bucket name.";
                return false;
            }

            if (slash < 0)
            {
                url += "/";
            }
        }

        // A file name that is not an identifier is published under a sanitised one (GeoParquetFolder.TableNameOf).
        if (!url.EndsWith('/') && GeoParquetFolder.NameOfUrl(url) is null)
        {
            why = "The URL names neither a prefix ending in / nor a file ending in .parquet.";
            return false;
        }

        if (request.Region is { Length: > 0 } region && !Region().IsMatch(region))
        {
            why = $"'{region}' is not a region name: lower-case letters, digits and hyphens.";
            return false;
        }

        if (request.Endpoint is { Length: > 0 } endpoint && !Endpoint().IsMatch(endpoint))
        {
            why = "`endpoint` is a host or host:port, without a scheme or a path — e.g. minio.example.org:9000.";
            return false;
        }

        if (request.UrlStyle is { Length: > 0 } style && style is not ("vhost" or "path"))
        {
            why = "`urlStyle` is vhost or path.";
            return false;
        }

        bool hasKey = request.AccessKeyId is { Length: > 0 };
        bool hasSecret = request.SecretAccessKey is { Length: > 0 };

        if (hasKey != hasSecret)
        {
            why = "An access key id and a secret access key come together, or neither is sent and the bucket is read anonymously.";
            return false;
        }

        if (hasKey
            && request.AccessKeyId is { } keyId && request.SecretAccessKey is { } secret
            && (keyId.Any(c => char.IsControl(c) || c == '\'') || keyId.Length > 256
                || secret.Any(char.IsControl) || secret.Length > 1024))
        {
            why = "The credentials hold a character or a length no access key has.";
            return false;
        }

        remote = new RemoteGeoParquet(
            url,
            request.Region is { Length: > 0 } ? request.Region : null,
            request.Endpoint is { Length: > 0 } ? request.Endpoint : null,
            hasKey ? request.AccessKeyId : null,
            hasSecret ? request.SecretAccessKey : null,
            request.UrlStyle is { Length: > 0 } ? request.UrlStyle : null,
            request.UseSsl ?? true);

        return true;
    }

    private static string HostOf(string endpoint)
    {
        if (endpoint.StartsWith('['))
        {
            int close = endpoint.IndexOf(']', StringComparison.Ordinal);
            return close < 0 ? endpoint : endpoint[1..close];
        }

        int colon = endpoint.LastIndexOf(':');
        return colon < 0 ? endpoint : endpoint[..colon];
    }

    /// <summary>What is sealed. Property names are the wire's, and never shown.</summary>
    private sealed record Stored(
        string Location, string? Region, string? Endpoint, string? AccessKeyId, string? SecretAccessKey,
        string? UrlStyle, bool UseSsl);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex Bucket();

    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Region();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IpShaped();

    [GeneratedRegex(@"^(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]{1,253})(:[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Endpoint();
}
